using HRCompanion.Audio.Windows;

namespace HRCompanion.Infrastructure.Tests;

public sealed class AudioContaminationPolicyTests
{
    [Fact]
    public void TeamsAudio_IsAllowed()
    {
        var result = AudioContaminationPolicy.FindLoudestNonTeamsSession(
            [new("Speakers", 15700, "ms-teams", 0.64f)]);

        Assert.Null(result);
    }

    [Fact]
    public void MeaningfulBrowserAudio_BlocksHrFrames()
    {
        var result = AudioContaminationPolicy.FindLoudestNonTeamsSession(
            [
                new("Speakers", 15700, "ms-teams", 0.64f),
                new("Speakers", 30000, "chrome", 0.25f)
            ]);

        Assert.NotNull(result);
        Assert.Equal("chrome", result.ProcessName);
        Assert.Equal((uint)30000, result.ProcessId);
    }

    [Fact]
    public void CurrentGuardProcessAudioSession_IsIgnored()
    {
        const uint guardProcessId = 8564;
        var result = AudioContaminationPolicy.FindLoudestNonTeamsSession(
            [
                new("Speakers", 15700, "ms-teams", 0.64f),
                new("Speakers", guardProcessId, "HRCompanion.AudioProbe", 0.61f)
            ],
            ignoredProcessId: guardProcessId);

        Assert.Null(result);
    }

    [Fact]
    public void IgnoringGuardProcess_DoesNotHideOtherContamination()
    {
        const uint guardProcessId = 8564;
        var result = AudioContaminationPolicy.FindLoudestNonTeamsSession(
            [
                new("Speakers", guardProcessId, "HRCompanion.AudioProbe", 0.61f),
                new("Speakers", 17544, "brave", 0.25f)
            ],
            ignoredProcessId: guardProcessId);

        Assert.NotNull(result);
        Assert.Equal("brave", result.ProcessName);
        Assert.Equal((uint)17544, result.ProcessId);
    }

    [Fact]
    public void TinyNonTeamsMeterNoise_DoesNotBlock()
    {
        var result = AudioContaminationPolicy.FindLoudestNonTeamsSession(
            [new("Speakers", 30000, "chrome", AudioContaminationPolicy.MeaningfulPeak / 2)]);

        Assert.Null(result);
    }

    [Fact]
    public void SystemSoundAboveThreshold_Blocks()
    {
        var result = AudioContaminationPolicy.FindLoudestNonTeamsSession(
            [new("Speakers", 0, "System Sounds", 0.3f)]);

        Assert.NotNull(result);
        Assert.Equal("System Sounds", result.ProcessName);
    }
}
