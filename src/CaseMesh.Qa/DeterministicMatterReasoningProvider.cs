namespace CaseMesh.Qa;

public sealed class DeterministicMatterReasoningProvider : IMatterReasoningProvider
{
    public MatterReasoningProviderDescriptor Descriptor { get; } =
        new("casemesh", "deterministic-evidence-synthesis", "matter-evidence/v1");

    public Task<MatterReasoningOutput> AnswerAsync(
        MatterReasoningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selected = request.Context.Take(6).ToArray();
        var claims = selected.Select(item => new MatterReasoningClaim(
            $"{item.Attribution} — {item.Label}",
            MatterClaimKind.Evidence,
            [item.RetrievalResultId])).ToArray();
        var warnings = new List<string>();
        if (selected.Any(item => !string.IsNullOrWhiteSpace(item.DisputeState)))
            warnings.Add("Retrieved records include disputed or unresolved attributed statements; CaseMesh does not choose one as truth.");
        if (selected.Any(item => item.IsHistorical))
            warnings.Add("Historical, rejected or superseded material is included and remains distinct from the current view.");
        return Task.FromResult(new MatterReasoningOutput(
            $"Your Matter contains {selected.Length} source-backed record{(selected.Length == 1 ? string.Empty : "s")} relevant to this question.",
            claims,
            warnings));
    }
}
