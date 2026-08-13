using CaseMesh.Core.Models;

namespace CaseMesh.Audio.Windows.Tests;

public sealed class TeamsProcessLoopbackCaptureSourceTests
{
    [Fact]
    public async Task InvalidPid_FailsClearly_AndTeardownIsIdempotent()
    {
        var source = new TeamsProcessLoopbackCaptureSource(int.MaxValue);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => source.StartAsync());

        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SpeakerRole.Hr, source.Speaker);
        await source.StopAsync();
        await source.StopAsync();
        await source.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task NonTeamsPid_IsRejectedInsteadOfCapturingArbitraryProcess()
    {
        var source = new TeamsProcessLoopbackCaptureSource(Environment.ProcessId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => source.StartAsync());

        Assert.Contains("not a recognised Microsoft Teams process", exception.Message, StringComparison.OrdinalIgnoreCase);
        await source.DisposeAsync();
    }
}
