using HRCompanion.Core.Abstractions;

namespace HRCompanion.Audio.Windows;

/// <summary>
/// Conservative laptop-speaker echo guard. A guarded HR/system-loopback frame that contains
/// meaningful remote speech temporarily suppresses microphone/USER frames so the same remote
/// voice cannot be attributed to the local user through acoustic speaker-to-mic leakage.
/// This intentionally prefers a short USER gap during overlap over speaker misattribution.
/// </summary>
internal static class RemoteSpeechMicrophoneGate
{
    internal const int RemoteSpeechPeakThreshold = 300;
    internal const long HoldMilliseconds = 500;
    private static long _suppressUntilTick;

    public static void ObserveRemoteFrame(AudioFrame frame)
    {
        var span = frame.Pcm16Bit24KhzMono.Span;
        var peak = 0;
        for (var index = 0; index + 1 < span.Length; index += 2)
        {
            var sample = Math.Abs((int)(short)(span[index] | span[index + 1] << 8));
            if (sample > peak) peak = sample;
        }
        ObserveRemotePeak(peak, Environment.TickCount64);
    }

    public static bool ShouldSuppressUserFrame() => ShouldSuppress(Environment.TickCount64);

    internal static void ObserveRemotePeak(int peak, long nowTick)
    {
        if (peak < RemoteSpeechPeakThreshold) return;
        var candidate = nowTick + HoldMilliseconds;
        long current;
        do
        {
            current = Volatile.Read(ref _suppressUntilTick);
            if (candidate <= current) return;
        } while (Interlocked.CompareExchange(ref _suppressUntilTick, candidate, current) != current);
    }

    internal static bool ShouldSuppress(long nowTick) =>
        nowTick <= Volatile.Read(ref _suppressUntilTick);

    internal static void Reset() => Interlocked.Exchange(ref _suppressUntilTick, 0);
}
