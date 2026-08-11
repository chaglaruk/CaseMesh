using HRCompanion.Audio.Windows;
using HRCompanion.Core.Models;

namespace HRCompanion.Infrastructure.Tests;

public sealed class RemoteSpeechMicrophoneGateTests
{
    [Fact]
    public void SilenceLevelRemotePeak_DoesNotSuppressUser()
    {
        RemoteSpeechMicrophoneGate.Reset();
        RemoteSpeechMicrophoneGate.ObserveRemotePeak(35, 1_000);

        Assert.False(RemoteSpeechMicrophoneGate.ShouldSuppress(1_000));
    }

    [Fact]
    public void SpeechLevelRemotePeak_SuppressesUserThroughHoldThenReleases()
    {
        RemoteSpeechMicrophoneGate.Reset();
        RemoteSpeechMicrophoneGate.ObserveRemotePeak(3_000, 1_000);

        Assert.True(RemoteSpeechMicrophoneGate.ShouldSuppress(1_000));
        Assert.True(RemoteSpeechMicrophoneGate.ShouldSuppress(1_500));
        Assert.False(RemoteSpeechMicrophoneGate.ShouldSuppress(1_501));
    }

    [Fact]
    public void LaterRemoteSpeech_ExtendsExistingSuppressionWindow()
    {
        RemoteSpeechMicrophoneGate.Reset();
        RemoteSpeechMicrophoneGate.ObserveRemotePeak(3_000, 1_000);
        RemoteSpeechMicrophoneGate.ObserveRemotePeak(4_000, 1_400);

        Assert.True(RemoteSpeechMicrophoneGate.ShouldSuppress(1_900));
        Assert.False(RemoteSpeechMicrophoneGate.ShouldSuppress(1_901));
    }

    [Fact]
    public void ObserveRemoteFrame_UsesPcm16Peak()
    {
        RemoteSpeechMicrophoneGate.Reset();
        var pcm = BitConverter.GetBytes((short)1_000);
        RemoteSpeechMicrophoneGate.ObserveRemoteFrame(new AudioFrame(pcm, DateTimeOffset.UtcNow));

        Assert.True(RemoteSpeechMicrophoneGate.ShouldSuppressUserFrame());
        RemoteSpeechMicrophoneGate.Reset();
    }
}
