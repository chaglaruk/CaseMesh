using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using Microsoft.Extensions.Options;

namespace HRCompanion.Infrastructure.OpenAI;

public sealed class OpenAiRealtimeTranscriber : IRealtimeTranscriber
{
    private const int MaximumReconnectAttempts = 3;
    internal const int AudioQueueCapacity = 12;
    private static readonly TimeSpan AudioSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionUpdateAckTimeout = TimeSpan.FromSeconds(5);
    private readonly IApiKeyStore _keys;
    private readonly OpenAiOptions _options;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly RealtimeTranscriptionEventParser _parser = new();
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private ClientWebSocket? _socket;
    private AudioFrameSendPump? _sendPump;
    private TranscriberDiagnostics _lastDiagnostics = new(0, 0, 0, 0, 0, false);
    private volatile bool _stopping;

    public OpenAiRealtimeTranscriber(SpeakerRole speaker, IApiKeyStore keys, IOptions<OpenAiOptions> options)
    {
        Speaker = speaker;
        _keys = keys;
        _options = options.Value;
    }

    public SpeakerRole Speaker { get; }
    public TranscriberDiagnostics Diagnostics => _sendPump?.Diagnostics ?? _lastDiagnostics;
    public event EventHandler<TranscriptionUpdate>? Updated;
    public event EventHandler<TranscriberConnectionState>? StateChanged;
    public event EventHandler<TranscriberDiagnostics>? DiagnosticsChanged;
    public event EventHandler<Exception>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_receiveTask is not null) return;
        var key = await _keys.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OpenAI API key is not configured.");

        _stopping = false;
        SetState(TranscriberConnectionState.Connecting);
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var socket = await ConnectAsync(key, cancellationToken).ConfigureAwait(false);
            _socket = socket;
            _sendPump = new AudioFrameSendPump(AudioQueueCapacity, SendFrameCoreAsync);
            _sendPump.DiagnosticsChanged += OnPumpDiagnosticsChanged;
            _sendPump.Start();
            SetState(TranscriberConnectionState.Listening);
            _receiveTask = ReceiveAndRecoverAsync(key, socket, _receiveCts.Token);
        }
        catch
        {
            SetState(TranscriberConnectionState.Failed);
            _receiveCts.Dispose();
            _receiveCts = null;
            throw;
        }
    }

    public bool TryEnqueue(AudioFrame frame) => _sendPump?.TryEnqueue(frame) ?? false;

    public async Task CommitInputAudioBufferAsync(CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            if (socket?.State != WebSocketState.Open)
                throw new InvalidOperationException("Realtime transcription socket is not open.");

            await SendJsonCoreAsync(
                socket,
                new { event_id = $"hrc-commit-{Guid.NewGuid():N}", type = "input_audio_buffer.commit" },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stopping = true;
        var sendPump = _sendPump;
        if (sendPump is not null)
        {
            await sendPump.StopAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        var socket = _socket;
        if (socket?.State == WebSocketState.Open)
        {
            using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            closeCts.CancelAfter(TimeSpan.FromSeconds(3));
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "meeting stopped", closeCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // A remote close or bounded close timeout is safe; persisted final turns are the recovery boundary.
            }
            finally
            {
                _sendGate.Release();
            }
        }

        _receiveCts?.Cancel();
        if (_receiveTask is not null)
        {
            try { await _receiveTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }

        await DisposeSocketAsync().ConfigureAwait(false);
        if (sendPump is not null)
        {
            sendPump.DiagnosticsChanged -= OnPumpDiagnosticsChanged;
            _lastDiagnostics = sendPump.Diagnostics;
            await sendPump.DisposeAsync().ConfigureAwait(false);
        }
        _sendPump = null;
        _receiveTask = null;
        _receiveCts?.Dispose();
        _receiveCts = null;
        _parser.ResetConnection();
        SetState(TranscriberConnectionState.Stopped);
    }

    private async Task ReceiveAndRecoverAsync(string key, ClientWebSocket initialSocket, CancellationToken cancellationToken)
    {
        var socket = initialSocket;
        var retryBudget = new ReconnectRetryBudget(MaximumReconnectAttempts);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or JsonException or ObjectDisposedException)
            {
                RaiseFault(ex);
            }

            if (_stopping || cancellationToken.IsCancellationRequested) break;
            _parser.ResetConnection();
            await DisposeSocketAsync(socket).ConfigureAwait(false);

            ClientWebSocket? reconnected = null;
            while (reconnected is null && retryBudget.TryUseAttempt() && !cancellationToken.IsCancellationRequested)
            {
                SetState(TranscriberConnectionState.Reconnecting);
                var reconnectAttempt = retryBudget.AttemptsUsed;
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, reconnectAttempt - 1)), cancellationToken).ConfigureAwait(false);
                    reconnected = await ConnectAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException or TimeoutException)
                {
                    RaiseFault(ex);
                }
                catch (RealtimeProtocolException ex)
                {
                    RaiseFault(ex);
                    SetState(TranscriberConnectionState.Failed);
                    return;
                }
            }

            if (reconnected is null)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    SetState(TranscriberConnectionState.Failed);
                    RaiseFault(new InvalidOperationException("Realtime transcription reconnect limit was reached."));
                }
                break;
            }

            socket = reconnected;
            _socket = socket;
            retryBudget.Reset();
            SetState(TranscriberConnectionState.Listening);
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(string key, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        socket.Options.SetRequestHeader("OpenAI-Safety-Identifier", "hrcompanion-local-user");
        try
        {
            await socket.ConnectAsync(CreateWebSocketUri(), cancellationToken).ConfigureAwait(false);
            await SendJsonCoreAsync(socket, CreateSessionUpdate(), cancellationToken).ConfigureAwait(false);
            await WaitForSessionUpdatedAsync(socket, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal Uri CreateWebSocketUri()
    {
        var wsBase = _options.BaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        return new Uri($"{wsBase}/realtime?model={Uri.EscapeDataString(_options.RealtimeConnectionModel)}");
    }

    internal object CreateSessionUpdate() => new
    {
        event_id = $"hrc-session-{Guid.NewGuid():N}",
        type = "session.update",
        session = new
        {
            type = "transcription",
            audio = new
            {
                input = new
                {
                    format = new { type = "audio/pcm", rate = 24000 },
                    transcription = new
                    {
                        model = _options.TranscriptionModel,
                        languages = new[] { _options.TranscriptionLanguage },
                        prompt = "British employment HR meeting. Preserve names, dates and role titles.",
                        keywords = new[]
                        {
                            "Occupational Health", "redeployment", "fit note", "phased return",
                            "reasonable adjustments", "capability", "grievance", "ACAS"
                        }
                    },
                    // gpt-live-transcribe's documented committed-turn mode requires this field
                    // to be explicitly null. The client then commits each completed audio turn.
                    turn_detection = (object?)null
                }
            }
        }
    };

    private async Task WaitForSessionUpdatedAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ackCts.CancelAfter(SessionUpdateAckTimeout);
        var buffer = new byte[64 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ackCts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("Realtime socket closed before session.updated.");
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                var json = Encoding.UTF8.GetString(stream.ToArray());
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String) continue;
                var type = typeProperty.GetString();
                if (string.Equals(type, "session.updated", StringComparison.Ordinal)) return;

                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    var parsed = _parser.Parse(json, DateTimeOffset.UtcNow);
                    if (parsed.Error is not null)
                        throw new RealtimeProtocolException(parsed.Error.Type, parsed.Error.Code);
                    throw new RealtimeProtocolException("realtime_error", null);
                }
            }

            throw new WebSocketException("Realtime socket closed before session.updated.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Realtime session.update was not acknowledged within the safety bound.");
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var parsed = _parser.Parse(Encoding.UTF8.GetString(stream.ToArray()), DateTimeOffset.UtcNow);
            if (parsed.Error is not null)
            {
                RaiseFault(new RealtimeProtocolException(parsed.Error.Type, parsed.Error.Code));
            }
            foreach (var update in parsed.Updates) Updated?.Invoke(this, update);
        }
    }

    private static Task SendJsonCoreAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<bool> SendFrameCoreAsync(AudioFrame frame, CancellationToken cancellationToken)
    {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendCts.CancelAfter(AudioSendTimeout);
        await _sendGate.WaitAsync(sendCts.Token).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            if (socket?.State != WebSocketState.Open) return false;
            var audio = Convert.ToBase64String(frame.Pcm16Bit24KhzMono.Span);
            try
            {
                await SendJsonCoreAsync(socket, new { type = "input_audio_buffer.append", audio }, sendCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                try { socket.Abort(); } catch { }
                if (ex is WebSocketException) RaiseFault(ex);
                return false;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void OnPumpDiagnosticsChanged(object? sender, TranscriberDiagnostics diagnostics)
    {
        _lastDiagnostics = diagnostics;
        DiagnosticsChanged?.Invoke(this, diagnostics);
    }

    private async Task DisposeSocketAsync(ClientWebSocket? expected = null)
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (expected is not null && !ReferenceEquals(_socket, expected))
            {
                expected.Dispose();
                return;
            }
            _socket?.Dispose();
            _socket = null;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void SetState(TranscriberConnectionState state) => StateChanged?.Invoke(this, state);
    private void RaiseFault(Exception error) => Faulted?.Invoke(this, error);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }
}

public sealed class RealtimeProtocolException : Exception
{
    public RealtimeProtocolException(string eventType, string? code)
        : base($"Realtime API event reported {eventType}{(code is null ? string.Empty : $" ({code})")}.")
    {
        EventType = eventType;
        Code = code;
    }

    public string EventType { get; }
    public string? Code { get; }
}
