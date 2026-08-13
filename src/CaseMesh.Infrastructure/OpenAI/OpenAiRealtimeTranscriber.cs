using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CaseMesh.Core.Abstractions;
using CaseMesh.Core.Models;
using Microsoft.Extensions.Options;

namespace CaseMesh.Infrastructure.OpenAI;

public sealed class OpenAiRealtimeTranscriber : IRealtimeTranscriber
{
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

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var key = await _keys.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OpenAI API key is not configured.");

        _socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        _socket.Options.SetRequestHeader("OpenAI-Safety-Identifier", "casemesh-local-user");
        var wsBase = _options.BaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        await _socket.ConnectAsync(new Uri($"{wsBase}/realtime?model={Uri.EscapeDataString(_options.TranscriptionModel)}"), cancellationToken).ConfigureAwait(false);

        await SendJsonAsync(new
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
                            model = _options.TranscriptionModel,
                            languages = new[] { _options.TranscriptionLanguage },
                            prompt = "Employment HR meeting. Preserve names, dates, role titles and HR terminology accurately.",
                            keywords = new[]
                            {
                                "Occupational Health", "redeployment", "fit note", "phased return",
                                "reasonable adjustments", "capability", "grievance", "ACAS"
                            },
                            delay = _options.TranscriptionDelay
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
        }, cancellationToken).ConfigureAwait(false);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
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
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var json = Encoding.UTF8.GetString(stream.ToArray());
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement)) continue;
            var type = typeElement.GetString();
            if (type is "conversation.item.input_audio_transcription.delta" or "conversation.item.input_audio_transcription.completed")
            {
                var textProperty = type.EndsWith(".delta", StringComparison.Ordinal) ? "delta" : "transcript";
                if (doc.RootElement.TryGetProperty(textProperty, out var text) && text.ValueKind == JsonValueKind.String)
                {
                    var itemId = doc.RootElement.TryGetProperty("item_id", out var itemIdElement) && itemIdElement.ValueKind == JsonValueKind.String
                        ? itemIdElement.GetString()
                        : null;
                    Updated?.Invoke(this, new(
                        text.GetString() ?? string.Empty,
                        type.EndsWith(".completed", StringComparison.Ordinal),
                        DateTimeOffset.UtcNow,
                        itemId));
                }
            }
        }
    }

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
