using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Live;
using CaseMesh.MatterBrain;
using Xunit;

namespace CaseMesh.Live.Tests;

public sealed class UnsupportedMatterStatementTests
{
    [Fact]
    public void Source_less_user_statement_is_visible_but_never_gets_documentary_provenance()
    {
        var tenantId = new TenantId(Guid.Parse("11000000-0000-0000-0000-000000000001"));
        var matterId = Guid.Parse("21000000-0000-0000-0000-000000000001");
        var now = DateTimeOffset.Parse("2026-08-28T08:30:00Z");
        var graph = new MatterEvidenceGraph(new Matter(
            matterId,
            tenantId,
            "workplace-dispute",
            "Synthetic unsupported statement Matter",
            "open",
            now,
            now));

        var assertionId = Guid.Parse("51000000-0000-0000-0000-000000000001");
        graph.AddAssertion(
            assertionId,
            "employee",
            "position",
            "I did not agree to that change.",
            "Employee",
            now,
            EvidenceOriginClass.ParticipantOrWitnessStatement,
            AssertionClass.UserAssertion,
            DisputeState.Uncorroborated,
            IntegrityState.Incomplete,
            VerificationState.NotReviewed);

        var context = new CanonicalLiveContextAdapter().Build(
            tenantId,
            matterId,
            new MatterBrainState(graph));

        Assert.Empty(context.Evidence);
        Assert.Empty(context.AiAnalysis);
        var statement = Assert.Single(context.UnsupportedStatements);
        Assert.Equal(assertionId, statement.AssertionId);
        Assert.Contains("without documentary SourceSpan provenance", statement.EvidenceNotice, StringComparison.Ordinal);
    }
}
