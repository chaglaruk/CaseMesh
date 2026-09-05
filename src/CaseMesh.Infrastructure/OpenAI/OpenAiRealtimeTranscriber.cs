using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CaseMesh.Core.Abstractions;
using CaseMesh.Core.Models;
using Microsoft.Extensions.Options;

namespace CaseMesh.Infrastructure.OpenAI;

public sealed class OpenAiRealtimeTranscriber : IRealtimeTranscriber
{
    private static readonly TimeSpan SessionHandshakeTimeout = TimeSpan.FromSeconds(10);
    private readonly IApiKeyStore _keys;
    private readonly OpenAiOptions _options;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public OpenAiRealtimeTranscriber(SpeakerRole speaker, IApiKeyStore keys, IOptions<OpenAiOptions> options)
    {
        Speaker = speaker;
        _keys = keys;
        _options = options.Value;
    }

    public SpeakerRole Speaker { get; }
    public event EventHandler<TranscriptionUpdate>? Updated;
    public event EventHandler<Exception>? Failed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var key = await _keys.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OpenAI API key is not configured.");

        _socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        _socket.Options.SetRequestHeader("OpenAI-Safety-Identifier", "casemesh-local-user");
        await _socket.ConnectAsync(BuildConnectionUri(_options), cancellationToken).ConfigureAwait(false);
        await SendJsonAsync(CreateSessionUpdate(_options), cancellationToken).ConfigureAwait(false);
        await AwaitSessionUpdatedAsync(cancellationToken).ConfigureAwait(false);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = ObserveReceiveLoopAsync(_receiveCts.Token);
    }

    public async ValueTask SendAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        if (_socket.State != WebSocketState.Open) return;
        var audio = Convert.ToBase64String(frame.Pcm16Bit24KhzMono.Span);
        await SendJsonAsync(new { type = "input_audio_buffer.append", audio }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _receiveCts?.Cancel();
        if (_socket.State == WebSocketState.Open)
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "meeting stopped", cancellationToken).ConfigureAwait(false);
                }
            }
            catch (WebSocketException)
            {
                // Connection may already be gone; local transcript persistence is the recovery boundary.
            }
            finally
            {
                _sendGate.Release();
            }
        }
        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message is null) return;
            ProcessServerMessage(message);
        }
    }

    private async Task ObserveReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during meeting stop.
        }
        catch (Exception exception)
        {
            Failed?.Invoke(this, exception);
        }
    }

    private async Task AwaitSessionUpdatedAsync(CancellationToken cancellationToken)
    {
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCts.CancelAfter(SessionHandshakeTimeout);
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(handshakeCts.Token).ConfigureAwait(false);
                if (message is null) throw new InvalidOperationException("OpenAI closed the realtime transcription session during startup.");
                var serverEvent = ParseServerMessage(message);
                if (serverEvent.Type == "session.updated") return;
                ThrowIfServerError(serverEvent);
            }

            throw new InvalidOperationException("OpenAI did not keep the realtime transcription session open during startup.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && handshakeCts.IsCancellationRequested)
        {
            throw new TimeoutException($"OpenAI did not confirm the realtime transcription session within {SessionHandshakeTimeout.TotalSeconds:0} seconds.");
        }
    }

    internal void ProcessServerMessage(string json)
    {
        var serverEvent = ParseServerMessage(json);
        if (serverEvent.Error is not null)
        {
            Failed?.Invoke(this, CreateServerException(serverEvent));
            return;
        }
        if (serverEvent.Type is not ("conversation.item.input_audio_transcription.delta" or "conversation.item.input_audio_transcription.completed")) return;

        Updated?.Invoke(this, new(
            serverEvent.Text ?? string.Empty,
            serverEvent.Type.EndsWith(".completed", StringComparison.Ordinal),
            DateTimeOffset.UtcNow,
            serverEvent.ItemId));
    }

    private async Task<string?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text
            ? Encoding.UTF8.GetString(stream.ToArray())
            : string.Empty;
    }

    internal static Uri BuildConnectionUri(OpenAiOptions options)
    {
        var wsBase = options.BaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        return new Uri($"{wsBase}/realtime?intent=transcription");
    }

    internal static object CreateSessionUpdate(OpenAiOptions options) => new
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
                    transcription = new
                    {
                        model = options.TranscriptionModel,
                        language = options.TranscriptionLanguage,
                        prompt = "Employment HR meeting. Preserve names, dates, role titles and HR terminology accurately. " +
                                 "Terms: Occupational Health, redeployment, fit note, phased return, reasonable adjustments, capability, grievance, ACAS."
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

    internal static RealtimeServerEvent ParseServerMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, null, null, null);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = GetString(root, "type");
        var textProperty = type?.EndsWith(".delta", StringComparison.Ordinal) == true ? "delta" : "transcript";
        var text = GetString(root, textProperty);
        var itemId = GetString(root, "item_id");

        string? error = null;
        if ((type == "error" || type == "conversation.item.input_audio_transcription.failed") &&
            root.TryGetProperty("error", out var errorElement))
        {
            var code = GetString(errorElement, "code");
            var parameter = GetString(errorElement, "param");
            var message = GetString(errorElement, "message") ?? "The realtime transcription service reported an error.";
            error = string.Join("; ", new[]
            {
                string.IsNullOrWhiteSpace(code) ? null : $"code={code}",
                string.IsNullOrWhiteSpace(parameter) ? null : $"parameter={parameter}",
                message
            }.Where(value => value is not null));
        }

        return new(type, text, itemId, error);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void ThrowIfServerError(RealtimeServerEvent serverEvent)
    {
        if (serverEvent.Error is not null)
            throw CreateServerException(serverEvent);
    }

    private static InvalidOperationException CreateServerException(RealtimeServerEvent serverEvent) =>
        new($"OpenAI realtime transcription failed: {serverEvent.Error}");

    private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open) return;
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _receiveCts?.Dispose();
        _socket.Dispose();
        _sendGate.Dispose();
    }
}

internal sealed record RealtimeServerEvent(string? Type, string? Text, string? ItemId, string? Error);
