using System.Diagnostics;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using NAudio.CoreAudioApi;

namespace HRCompanion.Audio.Windows;

public sealed class AudioContaminationChangedEventArgs(
    bool isBlocked,
    string? processName,
    uint processId,
    float peak) : EventArgs
{
    public bool IsBlocked { get; } = isBlocked;
    public string? ProcessName { get; } = processName;
    public uint ProcessId { get; } = processId;
    public float Peak { get; } = peak;
}

/// <summary>
/// Captures the default Windows render mix because current Teams builds on some machines do not
/// expose their remote audio through process-loopback even when the Teams audio session is visible.
/// To prevent unrelated desktop audio from becoming HR transcript, frames are fail-closed whenever
/// a non-Teams render session has meaningful activity.
/// </summary>
public sealed class TeamsAwareSystemLoopbackCaptureSource : IAudioCaptureSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ClearHold = TimeSpan.FromMilliseconds(750);
    private readonly SystemLoopbackCaptureSource _inner = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _blocked = 1;

    public SpeakerRole Speaker => SpeakerRole.Hr;
    public string DisplayName => "System loopback (Teams-aware contamination guard)";
    public bool IsBlocked => Volatile.Read(ref _blocked) == 1;

    public event EventHandler<AudioFrame>? FrameReady;
    public event EventHandler<Exception>? Faulted;
    public event EventHandler<AudioContaminationChangedEventArgs>? ContaminationChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_monitorTask is not null) return;
        }

        RemoteSpeechMicrophoneGate.Reset();
        Volatile.Write(ref _blocked, 1);
        _inner.FrameReady += OnInnerFrameReady;
        _inner.Faulted += OnInnerFaulted;
        try
        {
            await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
            var monitorCts = new CancellationTokenSource();
            var monitorTask = Task.Run(() => MonitorSessionsAsync(monitorCts.Token), CancellationToken.None);
            lock (_sync)
            {
                _monitorCts = monitorCts;
                _monitorTask = monitorTask;
            }
        }
        catch
        {
            _inner.FrameReady -= OnInnerFrameReady;
            _inner.Faulted -= OnInnerFaulted;
            RemoteSpeechMicrophoneGate.Reset();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? monitorTask;
        CancellationTokenSource? monitorCts;
        lock (_sync)
        {
            monitorTask = _monitorTask;
            monitorCts = _monitorCts;
            _monitorTask = null;
            _monitorCts = null;
        }

        monitorCts?.Cancel();
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (monitorCts?.IsCancellationRequested == true)
            {
            }
            catch (TimeoutException ex)
            {
                Faulted?.Invoke(this, ex);
            }
        }
        monitorCts?.Dispose();

        _inner.FrameReady -= OnInnerFrameReady;
        _inner.Faulted -= OnInnerFaulted;
        await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _blocked, 1);
        RemoteSpeechMicrophoneGate.Reset();
    }

    private async Task MonitorSessionsAsync(CancellationToken cancellationToken)
    {
        var blockedUntil = DateTimeOffset.MaxValue;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            while (!cancellationToken.IsCancellationRequested)
            {
                var contaminant = AudioContaminationPolicy.FindLoudestNonTeamsSession(
                    RenderAudioSessionReader.Read(enumerator),
                    ignoredProcessId: checked((uint)Environment.ProcessId));
                var now = DateTimeOffset.UtcNow;

                if (contaminant is not null)
                {
                    blockedUntil = now + ClearHold;
                    SetBlocked(true, contaminant);
                }
                else if (blockedUntil == DateTimeOffset.MaxValue || now >= blockedUntil)
                {
                    blockedUntil = DateTimeOffset.MinValue;
                    SetBlocked(false, null);
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetBlocked(true, null);
            Faulted?.Invoke(this, new InvalidOperationException(
                "Windows audio-session contamination guard failed; HR system-loopback audio is blocked to avoid misattribution.", ex));
        }
    }

    private void SetBlocked(bool blocked, RenderAudioSessionSample? contaminant)
    {
        var value = blocked ? 1 : 0;
        var previous = Interlocked.Exchange(ref _blocked, value);
        if (previous == value) return;

        ContaminationChanged?.Invoke(this, new AudioContaminationChangedEventArgs(
            blocked,
            contaminant?.ProcessName,
            contaminant?.ProcessId ?? 0,
            contaminant?.Peak ?? 0f));
    }

    private void OnInnerFrameReady(object? sender, AudioFrame frame)
    {
        if (IsBlocked) return;
        RemoteSpeechMicrophoneGate.ObserveRemoteFrame(frame);
        FrameReady?.Invoke(this, frame);
    }

    private void OnInnerFaulted(object? sender, Exception error) => Faulted?.Invoke(this, error);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

internal sealed record RenderAudioSessionSample(
    string EndpointName,
    uint ProcessId,
    string ProcessName,
    float Peak);

internal static class AudioContaminationPolicy
{
    internal const float MeaningfulPeak = 0.01f;

    public static RenderAudioSessionSample? FindLoudestNonTeamsSession(
        IEnumerable<RenderAudioSessionSample> sessions,
        uint? ignoredProcessId = null) =>
        sessions
            .Where(session => session.Peak >= MeaningfulPeak)
            .Where(session => ignoredProcessId is null || session.ProcessId != ignoredProcessId.Value)
            .Where(session => !TeamsProcessLocator.IsTeamsProcessName(session.ProcessName))
            .OrderByDescending(session => session.Peak)
            .FirstOrDefault();
}

internal static class RenderAudioSessionReader
{
    public static IReadOnlyList<RenderAudioSessionSample> Read(MMDeviceEnumerator enumerator)
    {
        var samples = new List<RenderAudioSessionSample>();
        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var endpointIndex = 0; endpointIndex < endpoints.Count; endpointIndex++)
        {
            using var endpoint = endpoints[endpointIndex];
            try
            {
                endpoint.AudioSessionManager.RefreshSessions();
                var sessions = endpoint.AudioSessionManager.Sessions;
                for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
                {
                    using var session = sessions[sessionIndex];
                    try
                    {
                        var processId = session.GetProcessID;
                        var peak = session.AudioMeterInformation?.MasterPeakValue ?? 0f;
                        samples.Add(new(
                            endpoint.FriendlyName,
                            processId,
                            ResolveProcessName(processId),
                            peak));
                    }
                    catch
                    {
                        // The session may disappear between enumeration and sampling.
                    }
                }
            }
            catch
            {
                // Endpoint may change while a headset is connected; retry on the next poll.
            }
        }
        return samples;
    }

    private static string ResolveProcessName(uint processId)
    {
        if (processId == 0) return "System Sounds";
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return "<exited-or-unavailable>";
        }
    }
}
