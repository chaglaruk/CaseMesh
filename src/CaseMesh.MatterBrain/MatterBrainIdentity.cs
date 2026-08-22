namespace CaseMesh.MatterBrain;

public static class MatterBrainIdentity
{
    public static Guid EntityMergeProposalId(Guid runId, string externalKey)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("A non-empty extraction run id is required.", nameof(runId));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalKey);
        return MatterBrainState.DeterministicId("entity-merge-proposal", runId, externalKey);
    }
}
