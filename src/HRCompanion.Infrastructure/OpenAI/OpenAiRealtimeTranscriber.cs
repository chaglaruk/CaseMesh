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
    private readonly IApiKeyStore _keys;
    private readonly OpenAiOptions _options;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly RealtimeTranscriptionEventParser _parser = new();
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private ClientWebSocket? _socket;
    private volatile bool _stopping;

    public OpenAiRealtimeTranscriber(SpeakerRole speaker, IApiKeyStore keys, IOptions<OpenAiOptions> options)
    {
        Speaker = speaker;
        _keys = keys;
        _options = options.Value;
    }

    public SpeakerRole Speaker { get; }
    public event EventHandler<TranscriptionUpdate>? Updated;
    public event EventHandler<TranscriberConnectionState>? StateChanged;
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

    public async ValueTask SendAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        var audio = Convert.ToBase64String(frame.Pcm16Bit24KhzMono.Span);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            if (socket?.State != WebSocketState.Open) return;
            await SendJsonCoreAsync(socket, new { type = "input_audio_buffer.append", audio }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stopping = true;
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
        _receiveTask = null;
        _receiveCts?.Dispose();
        _receiveCts = null;
        _parser.ResetConnection();
        SetState(TranscriberConnectionState.Stopped);
    }

    private async Task ReceiveAndRecoverAsync(string key, ClientWebSocket initialSocket, CancellationToken cancellationToken)
    {
        var socket = initialSocket;
        var reconnectAttempt = 0;
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
            while (reconnected is null && reconnectAttempt < MaximumReconnectAttempts && !cancellationToken.IsCancellationRequested)
            {
                SetState(TranscriberConnectionState.Reconnecting);
                reconnectAttempt++;
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, reconnectAttempt - 1)), cancellationToken).ConfigureAwait(false);
                    reconnected = await ConnectAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
                {
                    RaiseFault(ex);
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
            SetState(TranscriberConnectionState.Listening);
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(string key, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        socket.Options.SetRequestHeader("OpenAI-Safety-Identifier", "hrcompanion-local-user");
        var wsBase = _options.BaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        try
        {
            await socket.ConnectAsync(
                new Uri($"{wsBase}/realtime?model={Uri.EscapeDataString(_options.TranscriptionModel)}"),
                cancellationToken).ConfigureAwait(false);
            await SendJsonCoreAsync(socket, CreateSessionUpdate(), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private object CreateSessionUpdate() => new
    {
        type = "session.update",
        session = new
        {
            type = "transcription",
            audio = new
            {
                input = new
                {
                    format = new { type = "audio/pcm", rate = 24000 },
                    noise_reduction = new { type = Speaker == SpeakerRole.User ? "near_field" : "far_field" },
                    transcription = new
                    {
                        model = _options.TranscriptionModel,
                        language = _options.TranscriptionLanguage,
                        prompt = "British employment HR meeting. Expect Occupational Health, redeployment, fit note, phased return, reasonable adjustments, capability, grievance and ACAS. Preserve names, dates and role titles."
                    },
                    turn_detection = new
                    {
                        type = "server_vad",
                        threshold = 0.45,
                        prefix_padding_ms = 250,
                        silence_duration_ms = 650
                    }
                }
            }
        }
    };

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
    }
}
