using System.Collections.Concurrent;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed class LiveMeetingCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultLiveAssistanceBudget = TimeSpan.FromSeconds(6);

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
    private readonly ConcurrentDictionary<long, Task> _forwardingTasks = new();
    private readonly TimeSpan _liveAssistanceBudget;
    private CancellationTokenSource? _lifecycleCts;
    private CancellationTokenSource? _assistantCts;
    private long _turnGeneration;
    private long _forwardGeneration;
    private bool _started;

    public LiveMeetingCoordinator(
        MeetingState state,
        MeetingAssistantOrchestrator orchestrator,
        IAudioCaptureSource remoteAudio,
        IAudioCaptureSource userAudio,
        IRealtimeTranscriber remoteTranscriber,
        IRealtimeTranscriber userTranscriber)
        : this(
            state,
            orchestrator,
            remoteAudio,
            userAudio,
            remoteTranscriber,
            userTranscriber,
            DefaultLiveAssistanceBudget)
    {
    }

    internal LiveMeetingCoordinator(
        MeetingState state,
        MeetingAssistantOrchestrator orchestrator,
        IAudioCaptureSource remoteAudio,
        IAudioCaptureSource userAudio,
        IRealtimeTranscriber remoteTranscriber,
        IRealtimeTranscriber userTranscriber,
        TimeSpan liveAssistanceBudget)
    {
        if (remoteAudio.Speaker != SpeakerRole.Hr || remoteTranscriber.Speaker != SpeakerRole.Hr)
            throw new ArgumentException("Remote source/transcriber must own the HR speaker role.");
        if (userAudio.Speaker != SpeakerRole.User || userTranscriber.Speaker != SpeakerRole.User)
            throw new ArgumentException("Microphone source/transcriber must own the User speaker role.");
        if (liveAssistanceBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(liveAssistanceBudget));

        _state = state;
        _orchestrator = orchestrator;
        _remoteAudio = remoteAudio;
        _userAudio = userAudio;
        _remoteTranscriber = remoteTranscriber;
        _userTranscriber = userTranscriber;
        _liveAssistanceBudget = liveAssistanceBudget;
    }

    public event EventHandler<TranscriptTurn>? FinalTurn;
    public event EventHandler<AssistantResponse>? AssistantUpdated;
    public event EventHandler<Exception>? NonFatalError;
    public event EventHandler<Exception>? CaptureFailed;
    public event EventHandler<LiveMeetingDiagnosticEventArgs>? Diagnostic;

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
            ReportDiagnostic("LIVE_STARTED", $"Live capture started: {_remoteAudio.DisplayName}; microphone: {_userAudio.DisplayName}.");
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
        CancelCurrentAssistant("Meeting stopped; any in-flight suggestion was cancelled.");
        _lifecycleCts?.Cancel();

        await StopComponentAsync(() => _remoteAudio.StopAsync(cancellationToken)).ConfigureAwait(false);
        await StopComponentAsync(() => _userAudio.StopAsync(cancellationToken)).ConfigureAwait(false);
        await StopComponentAsync(() => _remoteTranscriber.StopAsync(cancellationToken)).ConfigureAwait(false);
        await StopComponentAsync(() => _userTranscriber.StopAsync(cancellationToken)).ConfigureAwait(false);
        Detach();

        var forwarding = _forwardingTasks.Values.ToArray();
        if (forwarding.Length > 0)
        {
            await Task.WhenAll(forwarding).ConfigureAwait(false);
        }

        var pending = _processingTasks.Values.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        _lifecycleCts?.Dispose();
        _lifecycleCts = null;
        ReportDiagnostic("LIVE_STOPPED", "Live capture stopped cleanly; final turns received by the coordinator were persisted.");
    }

    private void Attach()
    {
        _remoteAudio.FrameReady += OnRemoteFrame;
        _userAudio.FrameReady += OnUserFrame;
        _remoteAudio.CaptureFailed += OnCaptureFailed;
        _userAudio.CaptureFailed += OnCaptureFailed;
        _remoteTranscriber.Updated += OnRemoteTranscription;
        _userTranscriber.Updated += OnUserTranscription;
    }

    private void Detach()
    {
        _remoteAudio.FrameReady -= OnRemoteFrame;
        _userAudio.FrameReady -= OnUserFrame;
        _remoteAudio.CaptureFailed -= OnCaptureFailed;
        _userAudio.CaptureFailed -= OnCaptureFailed;
        _remoteTranscriber.Updated -= OnRemoteTranscription;
        _userTranscriber.Updated -= OnUserTranscription;
    }

    private void OnRemoteFrame(object? sender, AudioFrame frame) => TrackForward(_remoteTranscriber, frame);
    private void OnUserFrame(object? sender, AudioFrame frame) => TrackForward(_userTranscriber, frame);
    private void OnRemoteTranscription(object? sender, TranscriptionUpdate update) => OnTranscription(SpeakerRole.Hr, "teams", update);
    private void OnUserTranscription(object? sender, TranscriptionUpdate update) => OnTranscription(SpeakerRole.User, "microphone", update);

    private async Task ForwardFrameAsync(IRealtimeTranscriber transcriber, AudioFrame frame)
    {
        var cancellationToken = _lifecycleCts?.Token ?? CancellationToken.None;
        try { await transcriber.SendAsync(frame, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException) { NonFatalError?.Invoke(this, ex); }
    }

    private void TrackForward(IRealtimeTranscriber transcriber, AudioFrame frame)
    {
        var generation = Interlocked.Increment(ref _forwardGeneration);
        var task = ForwardFrameAsync(transcriber, frame);
        _forwardingTasks[generation] = task;
        _ = task.ContinueWith(
            completedTask => _forwardingTasks.TryRemove(generation, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnCaptureFailed(object? sender, Exception exception)
    {
        ReportDiagnostic("CAPTURE_FAILED", exception.Message);
        CaptureFailed?.Invoke(this, exception);
        NonFatalError?.Invoke(this, exception);
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
        CancelCurrentAssistant($"New final {speaker} speech made the previous suggestion stale.");
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
            await _recordGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _orchestrator.RecordFinalTurnAsync(_state, turn, CancellationToken.None).ConfigureAwait(false);
                FinalTurn?.Invoke(this, turn);
                ReportDiagnostic("TURN_PERSISTED", $"Saved final {turn.Speaker} transcript turn.");
            }
            finally
            {
                _recordGate.Release();
            }

            if (lifecycleToken.IsCancellationRequested || turn.Speaker != SpeakerRole.Hr || !IsLatestGeneration(generation)) return;

            using var assistantCts = CreateAssistantToken(lifecycleToken);
            var assistanceStartedAt = DateTimeOffset.UtcNow;
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
                        $"Live assistance exceeded the {_liveAssistanceBudget.TotalSeconds:0.###}-second latency budget."));
                    ReportDiagnostic("ASSISTANCE_TIMEOUT", "The current assistance request timed out.");
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
                var latency = DateTimeOffset.UtcNow - assistanceStartedAt;
                ReportDiagnostic("ASSISTANCE_PUBLISHED", $"Current suggestion published in {latency.TotalMilliseconds:0} ms.");
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
        cts.CancelAfter(_liveAssistanceBudget);
        lock (_assistantSync)
        {
            _assistantCts?.Cancel();
            _assistantCts = cts;
        }
        return cts;
    }

    private void CancelCurrentAssistant(string? reason = null)
    {
        var cancelled = false;
        lock (_assistantSync)
        {
            if (_assistantCts is null || _assistantCts.IsCancellationRequested) return;
            _assistantCts.Cancel();
            cancelled = true;
        }
        if (cancelled && reason is not null) ReportDiagnostic("STALE_ASSISTANCE_CANCELLED", reason);
    }

    private void ClearAssistantToken(CancellationTokenSource cts)
    {
        lock (_assistantSync)
        {
            if (ReferenceEquals(_assistantCts, cts)) _assistantCts = null;
        }
    }

    private bool IsLatestGeneration(long generation) => generation == Interlocked.Read(ref _turnGeneration);

    private async Task StopComponentAsync(Func<Task> stop)
    {
        try
        {
            await stop().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            NonFatalError?.Invoke(this, ex);
        }
    }

    private void ReportDiagnostic(string code, string message) =>
        Diagnostic?.Invoke(this, new LiveMeetingDiagnosticEventArgs(code, message, DateTimeOffset.UtcNow));

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

public sealed class LiveMeetingDiagnosticEventArgs(string code, string message, DateTimeOffset occurredAt) : EventArgs
{
    public string Code { get; } = code;
    public string Message { get; } = message;
    public DateTimeOffset OccurredAt { get; } = occurredAt;
}
