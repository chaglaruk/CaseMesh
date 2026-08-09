using System.Collections.Concurrent;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed class LiveMeetingCoordinator : IAsyncDisposable
{
    private readonly MeetingState _state;
    private readonly MeetingAssistantOrchestrator _orchestrator;
    private readonly IAudioCaptureSource _remoteAudio;
    private readonly IAudioCaptureSource _userAudio;
    private readonly IRealtimeTranscriber _remoteTranscriber;
    private readonly IRealtimeTranscriber _userTranscriber;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _turnStarts = new(StringComparer.Ordinal);
    private bool _started;

    public LiveMeetingCoordinator(
        MeetingState state,
        MeetingAssistantOrchestrator orchestrator,
        IAudioCaptureSource remoteAudio,
        IAudioCaptureSource userAudio,
        IRealtimeTranscriber remoteTranscriber,
        IRealtimeTranscriber userTranscriber)
    {
        if (remoteAudio.Speaker != SpeakerRole.Hr || remoteTranscriber.Speaker != SpeakerRole.Hr)
            throw new ArgumentException("Remote source/transcriber must own the HR speaker role.");
        if (userAudio.Speaker != SpeakerRole.User || userTranscriber.Speaker != SpeakerRole.User)
            throw new ArgumentException("Microphone source/transcriber must own the User speaker role.");

        _state = state;
        _orchestrator = orchestrator;
        _remoteAudio = remoteAudio;
        _userAudio = userAudio;
        _remoteTranscriber = remoteTranscriber;
        _userTranscriber = userTranscriber;
    }

    public event EventHandler<TranscriptTurn>? FinalTurn;
    public event EventHandler<AssistantResponse>? AssistantUpdated;
    public event EventHandler<Exception>? NonFatalError;
    public event EventHandler<TranscriberConnectionState>? ConnectionStateChanged;
    public event EventHandler<PipelineTiming>? LatencyMeasured;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        Attach();
        try
        {
            await _remoteTranscriber.StartAsync(cancellationToken).ConfigureAwait(false);
            await _userTranscriber.StartAsync(cancellationToken).ConfigureAwait(false);
            await _remoteAudio.StartAsync(cancellationToken).ConfigureAwait(false);
            await _userAudio.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        catch
        {
            Detach();
            await SafeStopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;
        _started = false;
        Detach();
        await StopOneAsync(() => _remoteAudio.StopAsync(cancellationToken)).ConfigureAwait(false);
        await StopOneAsync(() => _userAudio.StopAsync(cancellationToken)).ConfigureAwait(false);
        await StopOneAsync(() => _remoteTranscriber.StopAsync(cancellationToken)).ConfigureAwait(false);
        await StopOneAsync(() => _userTranscriber.StopAsync(cancellationToken)).ConfigureAwait(false);
    }

    private void Attach()
    {
        _remoteAudio.FrameReady += OnRemoteFrame;
        _userAudio.FrameReady += OnUserFrame;
        _remoteTranscriber.Updated += OnRemoteTranscription;
        _userTranscriber.Updated += OnUserTranscription;
        _remoteTranscriber.StateChanged += OnTranscriberStateChanged;
        _userTranscriber.StateChanged += OnTranscriberStateChanged;
        _remoteTranscriber.Faulted += OnFaulted;
        _userTranscriber.Faulted += OnFaulted;
        _remoteAudio.Faulted += OnFaulted;
        _userAudio.Faulted += OnFaulted;
    }

    private void Detach()
    {
        _remoteAudio.FrameReady -= OnRemoteFrame;
        _userAudio.FrameReady -= OnUserFrame;
        _remoteTranscriber.Updated -= OnRemoteTranscription;
        _userTranscriber.Updated -= OnUserTranscription;
        _remoteTranscriber.StateChanged -= OnTranscriberStateChanged;
        _userTranscriber.StateChanged -= OnTranscriberStateChanged;
        _remoteTranscriber.Faulted -= OnFaulted;
        _userTranscriber.Faulted -= OnFaulted;
        _remoteAudio.Faulted -= OnFaulted;
        _userAudio.Faulted -= OnFaulted;
    }

    private void OnRemoteFrame(object? sender, AudioFrame frame) => _ = ForwardFrameAsync(_remoteTranscriber, frame);
    private void OnUserFrame(object? sender, AudioFrame frame) => _ = ForwardFrameAsync(_userTranscriber, frame);
    private void OnRemoteTranscription(object? sender, TranscriptionUpdate update) => OnTranscription(SpeakerRole.Hr, "teams", update);
    private void OnUserTranscription(object? sender, TranscriptionUpdate update) => OnTranscription(SpeakerRole.User, "microphone", update);
    private void OnTranscriberStateChanged(object? sender, TranscriberConnectionState state) => ConnectionStateChanged?.Invoke(this, state);
    private void OnFaulted(object? sender, Exception error) => NonFatalError?.Invoke(this, error);

    private async Task ForwardFrameAsync(IRealtimeTranscriber transcriber, AudioFrame frame)
    {
        try { await transcriber.SendAsync(frame).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { NonFatalError?.Invoke(this, ex); }
    }

    private void OnTranscription(SpeakerRole speaker, string source, TranscriptionUpdate update)
    {
        var itemKey = !string.IsNullOrWhiteSpace(update.ItemId)
            ? $"{speaker}:{update.ItemId}"
            : $"{speaker}:fallback";
        _turnStarts.TryAdd(itemKey, update.OccurredAt);
        if (!update.IsFinal || string.IsNullOrWhiteSpace(update.Text)) return;
        var startedAt = update.StartedAt ?? (_turnStarts.TryRemove(itemKey, out var start) ? start : update.OccurredAt);
        _turnStarts.TryRemove(itemKey, out _);
        var turn = TranscriptTurn.Final(_state.MeetingId, speaker, update.Text, startedAt, update.OccurredAt, source, update.ItemId);
        _ = ProcessFinalTurnAsync(turn);
    }

    private async Task ProcessFinalTurnAsync(TranscriptTurn turn)
    {
        await _turnGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await _orchestrator.AcceptFinalTurnWithTimingAsync(
                _state,
                turn,
                () => FinalTurn?.Invoke(this, turn)).ConfigureAwait(false);
            var response = result.Response;
            if (response.Say is not null || response.Watch is not null || response.Ask is not null)
            {
                AssistantUpdated?.Invoke(this, response);
                if (result.Timing is not null)
                {
                    LatencyMeasured?.Invoke(this, result.Timing with { FirstUsefulRenderedAt = DateTimeOffset.UtcNow });
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NonFatalError?.Invoke(this, ex);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private async Task SafeStopAsync()
    {
        try { await _remoteAudio.StopAsync().ConfigureAwait(false); } catch { }
        try { await _userAudio.StopAsync().ConfigureAwait(false); } catch { }
        try { await _remoteTranscriber.StopAsync().ConfigureAwait(false); } catch { }
        try { await _userTranscriber.StopAsync().ConfigureAwait(false); } catch { }
    }

    private async Task StopOneAsync(Func<Task> stop)
    {
        try { await stop().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OperationCanceledException) { NonFatalError?.Invoke(this, ex); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _remoteAudio.DisposeAsync().ConfigureAwait(false);
        await _userAudio.DisposeAsync().ConfigureAwait(false);
        await _remoteTranscriber.DisposeAsync().ConfigureAwait(false);
        await _userTranscriber.DisposeAsync().ConfigureAwait(false);
        _turnGate.Dispose();
    }
}
