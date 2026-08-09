using System.Collections.Concurrent;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;

namespace HRCompanion.Core.Services;

public sealed class LiveMeetingCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan DefaultAssistanceTimeout = TimeSpan.FromSeconds(8);

    private readonly MeetingState _state;
    private readonly MeetingAssistantOrchestrator _orchestrator;
    private readonly IAudioCaptureSource _remoteAudio;
    private readonly IAudioCaptureSource _userAudio;
    private readonly IRealtimeTranscriber _remoteTranscriber;
    private readonly IRealtimeTranscriber _userTranscriber;
    private readonly TimeSpan _assistanceTimeout;
    private readonly SemaphoreSlim _ingestionGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _turnStarts = new(StringComparer.Ordinal);
    private readonly object _lifecycleSync = new();
    private readonly object _pendingSync = new();
    private readonly HashSet<Task> _pendingIngestions = [];
    private CancellationTokenSource? _assistanceCts;
    private Task? _assistanceTask;
    private DateTimeOffset _latestSpeechStartedAt = DateTimeOffset.MinValue;
    private long _activityGeneration;
    private TranscriberConnectionState _hrState = TranscriberConnectionState.Stopped;
    private TranscriberConnectionState _userState = TranscriberConnectionState.Stopped;
    private bool _remoteAudioFailed;
    private bool _userAudioFailed;
    private bool _hasTranscriptionGap;
    private bool _assistantDegraded;
    private bool _started;

    public LiveMeetingCoordinator(
        MeetingState state,
        MeetingAssistantOrchestrator orchestrator,
        IAudioCaptureSource remoteAudio,
        IAudioCaptureSource userAudio,
        IRealtimeTranscriber remoteTranscriber,
        IRealtimeTranscriber userTranscriber,
        TimeSpan? assistanceTimeout = null)
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
        _assistanceTimeout = assistanceTimeout ?? DefaultAssistanceTimeout;
        if (_assistanceTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(assistanceTimeout));
    }

    public event EventHandler<TranscriptTurn>? FinalTurn;
    public event EventHandler<AssistantResponse>? AssistantUpdated;
    public event EventHandler? AssistanceInvalidated;
    public event EventHandler<Exception>? NonFatalError;
    public event EventHandler<LiveMeetingHealth>? HealthChanged;
    public event EventHandler<PipelineTiming>? LatencyMeasured;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            if (_started) return;
            _started = true;
        }
        Attach();
        PublishHealth();
        try
        {
            await _remoteTranscriber.StartAsync(cancellationToken).ConfigureAwait(false);
            await _userTranscriber.StartAsync(cancellationToken).ConfigureAwait(false);
            await _remoteAudio.StartAsync(cancellationToken).ConfigureAwait(false);
            await _userAudio.StartAsync(cancellationToken).ConfigureAwait(false);
            PublishHealth();
        }
        catch
        {
            lock (_lifecycleSync) _started = false;
            Detach();
            await SafeStopAsync().ConfigureAwait(false);
            PublishHealth();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            if (!_started) return;
            _started = false;
            CancelAssistanceLocked();
            _activityGeneration++;
        }

        Detach();
        AssistanceInvalidated?.Invoke(this, EventArgs.Empty);
        PublishHealth();

        await Task.WhenAll(
            StopOneAsync(() => _remoteAudio.StopAsync(cancellationToken)),
            StopOneAsync(() => _userAudio.StopAsync(cancellationToken)),
            StopOneAsync(() => _remoteTranscriber.StopAsync(cancellationToken)),
            StopOneAsync(() => _userTranscriber.StopAsync(cancellationToken))).ConfigureAwait(false);

        Task[] pending;
        lock (_pendingSync) pending = _pendingIngestions.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException ex) { NonFatalError?.Invoke(this, ex); }
        }

        Task? assistance;
        lock (_lifecycleSync) assistance = _assistanceTask;
        if (assistance is not null)
        {
            try { await assistance.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }
    }

    private void Attach()
    {
        _remoteAudio.FrameReady += OnRemoteFrame;
        _userAudio.FrameReady += OnUserFrame;
        _remoteTranscriber.Updated += OnRemoteTranscription;
        _userTranscriber.Updated += OnUserTranscription;
        _remoteTranscriber.StateChanged += OnTranscriberStateChanged;
        _userTranscriber.StateChanged += OnTranscriberStateChanged;
        _remoteTranscriber.DiagnosticsChanged += OnDiagnosticsChanged;
        _userTranscriber.DiagnosticsChanged += OnDiagnosticsChanged;
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
        _remoteTranscriber.DiagnosticsChanged -= OnDiagnosticsChanged;
        _userTranscriber.DiagnosticsChanged -= OnDiagnosticsChanged;
        _remoteTranscriber.Faulted -= OnFaulted;
        _userTranscriber.Faulted -= OnFaulted;
        _remoteAudio.Faulted -= OnFaulted;
        _userAudio.Faulted -= OnFaulted;
    }

    private void OnRemoteFrame(object? sender, AudioFrame frame) => _remoteTranscriber.TryEnqueue(frame);
    private void OnUserFrame(object? sender, AudioFrame frame) => _userTranscriber.TryEnqueue(frame);
    private void OnRemoteTranscription(object? sender, TranscriptionUpdate update) => OnTranscription(SpeakerRole.Hr, "teams", update);
    private void OnUserTranscription(object? sender, TranscriptionUpdate update) => OnTranscription(SpeakerRole.User, "microphone", update);

    private void OnTranscriberStateChanged(object? sender, TranscriberConnectionState state)
    {
        if (ReferenceEquals(sender, _remoteTranscriber)) _hrState = state;
        if (ReferenceEquals(sender, _userTranscriber)) _userState = state;
        PublishHealth();
    }

    private void OnDiagnosticsChanged(object? sender, TranscriberDiagnostics diagnostics)
    {
        if (diagnostics.HasTranscriptionGap) _hasTranscriptionGap = true;
        PublishHealth();
    }

    private void OnFaulted(object? sender, Exception error)
    {
        if (ReferenceEquals(sender, _remoteAudio)) _remoteAudioFailed = true;
        if (ReferenceEquals(sender, _userAudio)) _userAudioFailed = true;
        PublishHealth();
        NonFatalError?.Invoke(this, error);
    }

    private void OnTranscription(SpeakerRole speaker, string source, TranscriptionUpdate update)
    {
        var speechStartedAt = update.StartedAt ?? update.OccurredAt;
        var generation = _activityGeneration;
        if (update.IsSpeechStarted || !string.IsNullOrWhiteSpace(update.Text))
        {
            generation = RegisterConversationActivity(speechStartedAt);
        }

        var itemKey = !string.IsNullOrWhiteSpace(update.ItemId)
            ? $"{speaker}:{update.ItemId}"
            : $"{speaker}:fallback";
        _turnStarts.TryAdd(itemKey, speechStartedAt);
        if (!update.IsFinal || string.IsNullOrWhiteSpace(update.Text)) return;

        var startedAt = update.StartedAt ?? (_turnStarts.TryRemove(itemKey, out var start) ? start : update.OccurredAt);
        _turnStarts.TryRemove(itemKey, out _);
        var turn = TranscriptTurn.Final(_state.MeetingId, speaker, update.Text, startedAt, update.OccurredAt, source, update.ItemId);
        TrackIngestion(ProcessFinalTurnAsync(turn, generation));
    }

    private long RegisterConversationActivity(DateTimeOffset speechStartedAt)
    {
        long generation;
        lock (_lifecycleSync)
        {
            if (speechStartedAt < _latestSpeechStartedAt) return _activityGeneration;
            _latestSpeechStartedAt = speechStartedAt;
            generation = ++_activityGeneration;
            CancelAssistanceLocked();
        }
        AssistanceInvalidated?.Invoke(this, EventArgs.Empty);
        return generation;
    }

    private async Task ProcessFinalTurnAsync(TranscriptTurn turn, long generation)
    {
        DateTimeOffset persistedAt;
        bool isLatestConversationTurn;
        await _ingestionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            persistedAt = await _orchestrator.PersistFinalTurnAsync(_state, turn).ConfigureAwait(false);
            var turns = _state.Turns;
            isLatestConversationTurn = turns.Count > 0 && turns[^1].Id == turn.Id;
            FinalTurn?.Invoke(this, turn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NonFatalError?.Invoke(this, ex);
            return;
        }
        finally
        {
            _ingestionGate.Release();
        }

        if (turn.Speaker == SpeakerRole.Hr && isLatestConversationTurn && IsCurrent(generation))
        {
            StartAssistance(turn, persistedAt, generation);
        }
    }

    private void StartAssistance(TranscriptTurn turn, DateTimeOffset persistedAt, long generation)
    {
        CancellationTokenSource cts;
        lock (_lifecycleSync)
        {
            if (!_started || generation != _activityGeneration) return;
            CancelAssistanceLocked();
            cts = new CancellationTokenSource(_assistanceTimeout);
            _assistanceCts = cts;
            _assistanceTask = RunAssistanceAsync(turn, persistedAt, generation, cts);
        }
    }

    private async Task RunAssistanceAsync(
        TranscriptTurn turn,
        DateTimeOffset persistedAt,
        long generation,
        CancellationTokenSource cts)
    {
        await Task.Yield();
        try
        {
            var result = await _orchestrator.GenerateAssistanceWithTimingAsync(
                _state, turn, persistedAt, cts.Token).ConfigureAwait(false);
            if (!IsCurrent(generation, cts))
            {
                if (cts.IsCancellationRequested && IsLatestRequest(generation, cts)) MarkAssistantTimeout();
                return;
            }

            _assistantDegraded = false;
            PublishHealth();
            var response = result.Response;
            if (response.Say is null && response.Watch is null && response.Ask is null) return;

            AssistantUpdated?.Invoke(this, response);
            if (result.Timing is not null)
            {
                LatencyMeasured?.Invoke(this, result.Timing with { FirstUsefulRenderedAt = DateTimeOffset.UtcNow });
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (IsLatestRequest(generation, cts)) MarkAssistantTimeout();
        }
        catch (Exception ex)
        {
            if (!IsCurrent(generation, cts)) return;
            _assistantDegraded = true;
            PublishHealth();
            NonFatalError?.Invoke(this, ex);
        }
        finally
        {
            lock (_lifecycleSync)
            {
                if (ReferenceEquals(_assistanceCts, cts))
                {
                    _assistanceCts = null;
                    _assistanceTask = null;
                }
            }
            cts.Dispose();
        }
    }

    private bool IsCurrent(long generation, CancellationTokenSource? cts = null)
    {
        lock (_lifecycleSync)
        {
            return _started && generation == _activityGeneration &&
                   (cts is null || ReferenceEquals(_assistanceCts, cts) && !cts.IsCancellationRequested);
        }
    }

    private bool IsLatestRequest(long generation, CancellationTokenSource cts)
    {
        lock (_lifecycleSync)
        {
            return _started && generation == _activityGeneration && ReferenceEquals(_assistanceCts, cts);
        }
    }

    private void MarkAssistantTimeout()
    {
        _assistantDegraded = true;
        PublishHealth();
        NonFatalError?.Invoke(this, new TimeoutException(
            $"Automatic assistance exceeded the {_assistanceTimeout.TotalSeconds:F0}-second live timeout."));
    }

    private void CancelAssistanceLocked()
    {
        try { _assistanceCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void TrackIngestion(Task task)
    {
        lock (_pendingSync) _pendingIngestions.Add(task);
        _ = task.ContinueWith(
            completed => { lock (_pendingSync) _pendingIngestions.Remove(completed); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishHealth()
    {
        bool started;
        lock (_lifecycleSync) started = _started;
        var state = !started
            ? LiveMeetingHealthState.Manual
            : _hasTranscriptionGap
                ? LiveMeetingHealthState.TranscriptionGap
                : _hrState == TranscriberConnectionState.Reconnecting
                    ? LiveMeetingHealthState.HrReconnecting
                    : _userState == TranscriberConnectionState.Reconnecting
                        ? LiveMeetingHealthState.UserReconnecting
                        : _remoteAudioFailed || _userAudioFailed ||
                          _hrState == TranscriberConnectionState.Failed || _userState == TranscriberConnectionState.Failed ||
                          _hrState != TranscriberConnectionState.Listening || _userState != TranscriberConnectionState.Listening
                            ? LiveMeetingHealthState.TranscriptionDegraded
                            : _assistantDegraded
                                ? LiveMeetingHealthState.AssistantDegraded
                                : LiveMeetingHealthState.FullListening;
        HealthChanged?.Invoke(this, new(
            state,
            _hrState,
            _userState,
            _hasTranscriptionGap,
            _assistantDegraded,
            _remoteTranscriber.Diagnostics,
            _userTranscriber.Diagnostics));
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
        catch (Exception ex) { NonFatalError?.Invoke(this, ex); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _remoteAudio.DisposeAsync().ConfigureAwait(false);
        await _userAudio.DisposeAsync().ConfigureAwait(false);
        await _remoteTranscriber.DisposeAsync().ConfigureAwait(false);
        await _userTranscriber.DisposeAsync().ConfigureAwait(false);
        _ingestionGate.Dispose();
    }
}
