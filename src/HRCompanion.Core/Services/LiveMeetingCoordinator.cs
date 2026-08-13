using System.Collections.Concurrent;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed class LiveMeetingCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan LiveAssistanceBudget = TimeSpan.FromSeconds(6);

    private readonly MeetingState _state;
    private readonly MeetingAssistantOrchestrator _orchestrator;
    private readonly IAudioCaptureSource _remoteAudio;
    private readonly IAudioCaptureSource _userAudio;
    private readonly IRealtimeTranscriber _remoteTranscriber;
    private readonly IRealtimeTranscriber _userTranscriber;
    private readonly SemaphoreSlim _recordGate = new(1, 1);
    private readonly object _assistantSync = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _turnStarts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _processingTasks = new();
    private CancellationTokenSource? _lifecycleCts;
    private CancellationTokenSource? _assistantCts;
    private long _turnGeneration;
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

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        _lifecycleCts = new CancellationTokenSource();
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
            _lifecycleCts.Cancel();
            _lifecycleCts.Dispose();
            _lifecycleCts = null;
            await SafeStopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;
        _started = false;
        Detach();
        CancelCurrentAssistant();
        _lifecycleCts?.Cancel();

        await _remoteAudio.StopAsync(cancellationToken).ConfigureAwait(false);
        await _userAudio.StopAsync(cancellationToken).ConfigureAwait(false);
        await _remoteTranscriber.StopAsync(cancellationToken).ConfigureAwait(false);
        await _userTranscriber.StopAsync(cancellationToken).ConfigureAwait(false);

        var pending = _processingTasks.Values.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        _lifecycleCts?.Dispose();
        _lifecycleCts = null;
    }

    private void Attach()
    {
        _remoteAudio.FrameReady += OnRemoteFrame;
        _userAudio.FrameReady += OnUserFrame;
        _remoteTranscriber.Updated += OnRemoteTranscription;
        _userTranscriber.Updated += OnUserTranscription;
    }

    private void Detach()
    {
        _remoteAudio.FrameReady -= OnRemoteFrame;
        _userAudio.FrameReady -= OnUserFrame;
        _remoteTranscriber.Updated -= OnRemoteTranscription;
        _userTranscriber.Updated -= OnUserTranscription;
    }

    private void OnRemoteFrame(object? sender, AudioFrame frame) => _ = ForwardFrameAsync(_remoteTranscriber, frame);
    private void OnUserFrame(object? sender, AudioFrame frame) => _ = ForwardFrameAsync(_userTranscriber, frame);
    private void OnRemoteTranscription(object? sender, TranscriptionUpdate update) => OnTranscription(SpeakerRole.Hr, "teams", update);
    private void OnUserTranscription(object? sender, TranscriptionUpdate update) => OnTranscription(SpeakerRole.User, "microphone", update);

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
        var startedAt = _turnStarts.TryRemove(itemKey, out var start) ? start : update.OccurredAt;
        var turn = TranscriptTurn.Final(_state.MeetingId, speaker, update.Text, startedAt, update.OccurredAt, source);
        var generation = Interlocked.Increment(ref _turnGeneration);

        // Any newer final speech makes an older suggested answer stale, including the user's own reply.
        CancelCurrentAssistant();
        var task = ProcessFinalTurnAsync(turn, generation);
        _processingTasks[generation] = task;
        _ = task.ContinueWith(
            completedTask => _processingTasks.TryRemove(generation, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ProcessFinalTurnAsync(TranscriptTurn turn, long generation)
    {
        var lifecycleToken = _lifecycleCts?.Token ?? CancellationToken.None;
        try
        {
            await _recordGate.WaitAsync(lifecycleToken).ConfigureAwait(false);
            try
            {
                FinalTurn?.Invoke(this, turn);
                await _orchestrator.RecordFinalTurnAsync(_state, turn, lifecycleToken).ConfigureAwait(false);
            }
            finally
            {
                _recordGate.Release();
            }

            if (turn.Speaker != SpeakerRole.Hr || !IsLatestGeneration(generation)) return;

            using var assistantCts = CreateAssistantToken(lifecycleToken);
            AssistantResponse response;
            try
            {
                response = await _orchestrator.CreateAssistanceForRecordedTurnAsync(
                    _state,
                    turn,
                    assistantCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (assistantCts.IsCancellationRequested)
            {
                if (!lifecycleToken.IsCancellationRequested && IsLatestGeneration(generation))
                {
                    NonFatalError?.Invoke(this, new TimeoutException(
                        $"Live assistance exceeded the {LiveAssistanceBudget.TotalSeconds:0}-second latency budget."));
                }
                return;
            }
            finally
            {
                ClearAssistantToken(assistantCts);
            }

            if (!IsLatestGeneration(generation)) return;
            if (response.Say is not null || response.Watch is not null || response.Ask is not null)
            {
                AssistantUpdated?.Invoke(this, response);
            }
        }
        catch (OperationCanceledException) when (lifecycleToken.IsCancellationRequested)
        {
            // Expected during meeting stop.
        }
        catch (Exception ex)
        {
            NonFatalError?.Invoke(this, ex);
        }
    }

    private CancellationTokenSource CreateAssistantToken(CancellationToken lifecycleToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifecycleToken);
        cts.CancelAfter(LiveAssistanceBudget);
        lock (_assistantSync)
        {
            _assistantCts?.Cancel();
            _assistantCts = cts;
        }
        return cts;
    }

    private void CancelCurrentAssistant()
    {
        lock (_assistantSync)
        {
            _assistantCts?.Cancel();
        }
    }

    private void ClearAssistantToken(CancellationTokenSource cts)
    {
        lock (_assistantSync)
        {
            if (ReferenceEquals(_assistantCts, cts)) _assistantCts = null;
        }
    }

    private bool IsLatestGeneration(long generation) => generation == Interlocked.Read(ref _turnGeneration);

    private async Task SafeStopAsync()
    {
        try { await _remoteAudio.StopAsync().ConfigureAwait(false); } catch { }
        try { await _userAudio.StopAsync().ConfigureAwait(false); } catch { }
        try { await _remoteTranscriber.StopAsync().ConfigureAwait(false); } catch { }
        try { await _userTranscriber.StopAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        CancelCurrentAssistant();
        _assistantCts?.Dispose();
        _assistantCts = null;
        _recordGate.Dispose();
    }
}
