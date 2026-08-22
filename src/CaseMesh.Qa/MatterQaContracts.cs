using CaseMesh.Core.Models;

namespace CaseMesh.Qa;

public enum RetrievalMaterialKind
{
    SourceSpan = 0,
    Assertion,
    Event,
    Person,
    Organisation,
    Communication,
    EmploymentTerm,
    HealthAbsence,
    AdjustmentRequest,
    WorkplaceProcess
}

public enum MatterAnswerStatus
{
    Answered = 0,
    InsufficientEvidence,
    Rejected
}

public enum MatterClaimKind
{
    Evidence = 0,
    Analysis
}

public sealed record MatterRetrievalRequest(
    TenantId TenantId,
    Guid MatterId,
    string Question,
    int MaximumResults = 12,
    int MaximumContextBytes = 32 * 1024);

public sealed record MatterRetrievalResult(
    Guid Id,
    RetrievalMaterialKind Kind,
    Guid CanonicalId,
    Guid SourceSpanId,
    Guid DocumentVersionId,
    Guid OriginalObjectId,
    string OriginalSha256,
    string Label,
    string ContextText,
    string Attribution,
    string? DisputeState,
    bool IsHistorical,
    decimal Score)
{
    public override string ToString() =>
        $"{nameof(MatterRetrievalResult)} {{ Id = {Id}, Kind = {Kind}, SourceSpanId = {SourceSpanId} }}";
}

public interface IMatterEvidenceRetriever
{
    Task<IReadOnlyList<MatterRetrievalResult>> RetrieveAsync(
        MatterRetrievalRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyCanonicalAsync(
        TenantId tenantId,
        Guid matterId,
        IReadOnlyList<MatterRetrievalResult> results,
        CancellationToken cancellationToken = default);
}

public sealed record MatterReasoningProviderDescriptor(string Provider, string Model, string PromptVersion);

public sealed record MatterReasoningContext(
    Guid RetrievalResultId,
    RetrievalMaterialKind Kind,
    string Label,
    string EvidenceText,
    string Attribution,
    string? DisputeState,
    bool IsHistorical)
{
    public override string ToString() =>
        $"{nameof(MatterReasoningContext)} {{ RetrievalResultId = {RetrievalResultId}, Kind = {Kind} }}";
}

public sealed record MatterReasoningRequest(
    string Question,
    string ApplicationInstruction,
    IReadOnlyList<MatterReasoningContext> Context);

public sealed record MatterReasoningClaim(
    string Text,
    MatterClaimKind Kind,
    IReadOnlyList<Guid> CitationResultIds);

public sealed record MatterReasoningOutput(
    string Summary,
    IReadOnlyList<MatterReasoningClaim> Claims,
    IReadOnlyList<string> Warnings);

public interface IMatterReasoningProvider
{
    MatterReasoningProviderDescriptor Descriptor { get; }

    Task<MatterReasoningOutput> AnswerAsync(
        MatterReasoningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record VerifiedMatterCitation(
    Guid RetrievalResultId,
    RetrievalMaterialKind Kind,
    Guid CanonicalId,
    Guid SourceSpanId,
    Guid DocumentVersionId,
    Guid OriginalObjectId,
    string OriginalSha256,
    string Label,
    string Attribution,
    string? DisputeState,
    bool IsHistorical);

public sealed record VerifiedMatterClaim(
    string Text,
    MatterClaimKind Kind,
    IReadOnlyList<Guid> CitationResultIds);

public sealed record MatterQaAnswer(
    MatterAnswerStatus Status,
    string Summary,
    IReadOnlyList<VerifiedMatterClaim> Claims,
    IReadOnlyList<VerifiedMatterCitation> Citations,
    IReadOnlyList<string> Warnings,
    string? FailureCode,
    MatterReasoningProviderDescriptor? Provider);

public sealed record FactualGap(
    string Code,
    string Summary,
    string Route,
    IReadOnlyList<Guid> RelatedRecordIds,
    IReadOnlyList<Guid> SourceSpanIds);
