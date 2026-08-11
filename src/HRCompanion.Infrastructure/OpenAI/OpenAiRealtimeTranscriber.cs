using System.Net.Http.Headers;
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
    private static readonly TimeSpan SessionCreatedTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpClient ClientSecretHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
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
        var sendPump = _sendPump ?? throw new InvalidOperationException("Realtime transcriber is not started.");
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(TimeSpan.FromSeconds(5));
        while (sendPump.Diagnostics.QueueDepth > 0 ||
               sendPump.Diagnostics.FramesSent < sendPump.Diagnostics.FramesAccepted - sendPump.Diagnostics.FramesDropped)
        {
            await Task.Delay(10, waitCts.Token).ConfigureAwait(false);
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            if (socket?.State != WebSocketState.Open) throw new InvalidOperationException("Realtime socket is not open.");
            await SendJsonCoreAsync(socket, new { type = "input_audio_buffer.commit" }, cancellationToken).ConfigureAwait(false);
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
        var clientSecret = await CreateTranscriptionClientSecretAsync(key, cancellationToken).ConfigureAwait(false);
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("realtime");
        socket.Options.AddSubProtocol($"openai-insecure-api-key.{clientSecret}");
        socket.Options.SetRequestHeader("OpenAI-Safety-Identifier", "hrcompanion-local-user");
        try
        {
            await socket.ConnectAsync(CreateWebSocketUri(), cancellationToken).ConfigureAwait(false);
            await WaitForTranscriptionSessionCreatedAsync(socket, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal Uri CreateClientSecretUri() =>
        new($"{_options.BaseUrl.TrimEnd('/')}/realtime/client_secrets");

    internal Uri CreateWebSocketUri()
    {
        var wsBase = _options.BaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        return new Uri($"{wsBase}/realtime");
    }

    internal object CreateClientSecretRequest() => new
    {
        expires_after = new
        {
            anchor = "created_at",
            seconds = 120
        },
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
                        model = _options.TranscriptionModel
                    },
                    turn_detection = new
                    {
                        type = "server_vad",
                        threshold = 0.5,
                        prefix_padding_ms = 300,
                        silence_duration_ms = 500,
                        create_response = false,
                        interrupt_response = false
                    }
                }
            }
        }
    };

    private async Task<string> CreateTranscriptionClientSecretAsync(string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateClientSecretUri());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("OpenAI-Safety-Identifier", "hrcompanion-local-user");
        request.Content = new StringContent(
            JsonSerializer.Serialize(CreateClientSecretRequest()),
            Encoding.UTF8,
            "application/json");

        using var response = await ClientSecretHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateSafeRestProtocolException(json, $"client_secret_http_{(int)response.StatusCode}");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetString(root, "value", out var value))
                throw new RealtimeProtocolException("client_secret_error", "missing_value");

            if (!root.TryGetProperty("session", out var session) ||
                !TryGetString(session, "type", out var sessionType) ||
                !string.Equals(sessionType, "transcription", StringComparison.Ordinal))
            {
                throw new RealtimeProtocolException("client_secret_error", "unexpected_session_type");
            }

            return value;
        }
        catch (JsonException)
        {
            throw new RealtimeProtocolException("client_secret_error", "invalid_json");
        }
    }

    private async Task WaitForTranscriptionSessionCreatedAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readyCts.CancelAfter(SessionCreatedTimeout);
        var buffer = new byte[64 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextMessageAsync(socket, buffer, readyCts.Token).ConfigureAwait(false);
                if (json is null) continue;

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!TryGetString(root, "type", out var type)) continue;

                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    var parsed = _parser.Parse(json, DateTimeOffset.UtcNow);
                    if (parsed.Error is not null)
                        throw new RealtimeProtocolException(parsed.Error.Type, parsed.Error.Code);
                    throw new RealtimeProtocolException("realtime_error", null);
                }

                if (!string.Equals(type, "session.created", StringComparison.Ordinal) &&
                    !string.Equals(type, "transcription_session.created", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(type, "transcription_session.created", StringComparison.Ordinal)) return;
                if (!root.TryGetProperty("session", out var session))
                    throw new RealtimeProtocolException("session_created_error", "missing_session");

                var isTranscriptionType = TryGetString(session, "type", out var sessionType) &&
                                          string.Equals(sessionType, "transcription", StringComparison.Ordinal);
                var isTranscriptionObject = TryGetString(session, "object", out var sessionObject) &&
                                            string.Equals(sessionObject, "realtime.transcription_session", StringComparison.Ordinal);
                if (isTranscriptionType || isTranscriptionObject) return;

                throw new RealtimeProtocolException("session_created_error", "unexpected_session_type");
            }

            throw new WebSocketException("Realtime socket closed before a transcription session was created.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Realtime transcription session was not created within the safety bound.");
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var json = await ReceiveTextMessageAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
            if (json is null) continue;

            var parsed = _parser.Parse(json, DateTimeOffset.UtcNow);
            if (parsed.Error is not null)
            {
                RaiseFault(new RealtimeProtocolException(parsed.Error.Type, parsed.Error.Code));
            }
            foreach (var update in parsed.Updates) Updated?.Invoke(this, update);
        }
    }

    private static async Task<string?> ReceiveTextMessageAsync(
        ClientWebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text
            ? Encoding.UTF8.GetString(stream.ToArray())
            : null;
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

    private static RealtimeProtocolException CreateSafeRestProtocolException(string json, string fallbackCode)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var nested) ? nested : root;
            var type = TryGetString(error, "type", out var errorType) ? errorType : "client_secret_error";
            var hasCode = TryGetString(error, "code", out var code);
            var hasParam = TryGetString(error, "param", out var param);
            var safeCode = hasCode ? code : fallbackCode;
            if (hasParam) safeCode = $"{safeCode}@param={param}";
            return new RealtimeProtocolException(type, safeCode);
        }
        catch (JsonException)
        {
            return new RealtimeProtocolException("client_secret_error", fallbackCode);
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
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