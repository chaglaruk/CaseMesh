using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CaseMesh.Core.Models;

namespace CaseMesh.Core.Services;

public sealed partial class MatterEvidenceGraph
{
    private readonly Dictionary<Guid, DocumentVersionIdentity> _documentVersions = [];
    private readonly Dictionary<string, Guid> _originalObjectIdsByHash = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _hashesByOriginalObjectId = [];
    private readonly Dictionary<Guid, SourceSpan> _sourceSpans = [];
    private readonly Dictionary<Guid, Assertion> _assertions = [];
    private readonly Dictionary<Guid, MatterEvent> _events = [];
    private readonly Dictionary<Guid, AssertionEventLink> _links = [];
    private readonly Dictionary<Guid, Contradiction> _contradictions = [];
    private readonly Dictionary<Guid, AnalysisNode> _analysisNodes = [];
    private readonly List<AuditEvent> _auditEvents = [];

    public MatterEvidenceGraph(Matter matter)
    {
        Matter = matter ?? throw new ArgumentNullException(nameof(matter));
    }

    public Matter Matter { get; }
    public IReadOnlyCollection<DocumentVersionIdentity> DocumentVersions => _documentVersions.Values.ToArray();
    public IReadOnlyCollection<SourceSpan> SourceSpans => _sourceSpans.Values.ToArray();
    public IReadOnlyCollection<Assertion> Assertions => _assertions.Values.ToArray();
    public IReadOnlyCollection<MatterEvent> Events => _events.Values.ToArray();
    public IReadOnlyCollection<AssertionEventLink> AssertionEventLinks => _links.Values.ToArray();
    public IReadOnlyCollection<Contradiction> Contradictions => _contradictions.Values.ToArray();
    public IReadOnlyCollection<AnalysisNode> AnalysisNodes => _analysisNodes.Values.ToArray();
    public IReadOnlyList<AuditEvent> AuditEvents => _auditEvents.ToArray();
    public int LogicalOriginalCount => _originalObjectIdsByHash.Count;

    public DocumentVersionIdentity RegisterDocumentVersion(
        Guid documentId,
        Guid documentVersionId,
        string contentSha256,
        Guid newOriginalObjectId)
    {
        RequireId(documentId, nameof(documentId));
        RequireId(documentVersionId, nameof(documentVersionId));
        RequireId(newOriginalObjectId, nameof(newOriginalObjectId));
        var normalizedHash = NormalizeSha256(contentSha256);

        if (_documentVersions.TryGetValue(documentVersionId, out var existing))
        {
            if (existing.DocumentId != documentId || !string.Equals(existing.ContentSha256, normalizedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Document version identity is immutable and cannot be registered with different content.");
            }

            return existing;
        }

        if (!_originalObjectIdsByHash.TryGetValue(normalizedHash, out var originalObjectId))
        {
            if (_hashesByOriginalObjectId.TryGetValue(newOriginalObjectId, out var existingHash) &&
                !string.Equals(existingHash, normalizedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An immutable original object cannot be registered with different content hashes.");
            }

            originalObjectId = newOriginalObjectId;
            _originalObjectIdsByHash.Add(normalizedHash, originalObjectId);
            _hashesByOriginalObjectId.Add(originalObjectId, normalizedHash);
        }

        var version = new DocumentVersionIdentity(Matter.Id, documentId, documentVersionId, originalObjectId, normalizedHash);
        _documentVersions.Add(documentVersionId, version);
        return version;
    }

    public SourceSpan AddSourceSpan(
        Guid id,
        DocumentVersionIdentity documentVersion,
        string extractedText,
        string parserVersion,
        decimal? extractionConfidence = null,
        int? pageNumber = null,
        int? textStart = null,
        int? textEnd = null)
    {
        RequireId(id, nameof(id));
        ArgumentNullException.ThrowIfNull(documentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        ValidateConfidence(extractionConfidence);

        if (pageNumber is <= 0) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (textStart is < 0) throw new ArgumentOutOfRangeException(nameof(textStart));
        if (textEnd is < 0) throw new ArgumentOutOfRangeException(nameof(textEnd));
        if (textStart.HasValue != textEnd.HasValue || textEnd < textStart)
        {
            throw new ArgumentException("Text offsets must be supplied as a valid start/end pair.");
        }

        if (!pageNumber.HasValue && !textStart.HasValue)
        {
            throw new ArgumentException("A source span requires a page number or text-offset address.");
        }

        if (documentVersion.MatterId != Matter.Id ||
            !_documentVersions.TryGetValue(documentVersion.DocumentVersionId, out var registeredVersion) ||
            registeredVersion != documentVersion)
        {
            throw new InvalidOperationException("Source span document version belongs to a different Matter or is not registered.");
        }

        EnsureAvailable(_sourceSpans, id, "source span");
        var extractedTextDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(extractedText)));
        var span = new SourceSpan(
            id,
            documentVersion,
            pageNumber,
            textStart,
            textEnd,
            extractedText,
            extractedTextDigest,
            parserVersion,
            extractionConfidence);
        _sourceSpans.Add(id, span);
        return span;
    }

    public Assertion AddAssertion(
        Guid id,
        string subjectReference,
        string predicate,
        string value,
        string assertedBy,
        DateTimeOffset assertedAt,
        EvidenceOriginClass originClass,
        AssertionClass assertionClass,
        DisputeState disputeState,
        IntegrityState integrityState,
        VerificationState verificationState,
        Guid? sourceSpanId = null,
        DateTimeOffset? eventTime = null,
        decimal? extractionConfidence = null,
        string? createdByModel = null)
    {
        RequireId(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(assertedBy);
        ValidateConfidence(extractionConfidence);
        RequireDefinedEnum(originClass);
        RequireDefinedEnum(assertionClass);
        RequireDefinedEnum(disputeState);
        RequireDefinedEnum(integrityState);
        RequireDefinedEnum(verificationState);
        EnsureAvailable(_assertions, id, "assertion");

        SourceSpan? sourceSpan = null;
        if (sourceSpanId.HasValue && !_sourceSpans.TryGetValue(sourceSpanId.Value, out sourceSpan))
        {
            throw new InvalidOperationException("A source-backed assertion requires a source span registered to the same Matter.");
        }

        if (sourceSpan is not null && sourceSpan.MatterId != Matter.Id)
        {
            throw new InvalidOperationException("A source-backed assertion cannot reference another Matter.");
        }

        if (RequiresSourceSpan(originClass, assertionClass) && sourceSpan is null)
        {
            throw new InvalidOperationException("Documentary assertions require a source span registered to the same Matter.");
        }

        var aiOrigin = originClass == EvidenceOriginClass.AiGeneratedInference;
        var aiAssertion = assertionClass == AssertionClass.AiInference;
        if (aiOrigin != aiAssertion)
        {
            throw new InvalidOperationException("AI inference origin and assertion classifications must be used together.");
        }

        if (aiAssertion)
        {
            if (sourceSpanId.HasValue)
            {
                throw new InvalidOperationException("AI inference must be stored separately and cannot masquerade as source-backed evidence.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(createdByModel);
        }
        else
        {
            if (createdByModel is not null)
            {
                throw new InvalidOperationException("Only AI inference assertions can record a generating model.");
            }

            if (extractionConfidence.HasValue && sourceSpan is null)
            {
                throw new InvalidOperationException("Extraction confidence requires a source span.");
            }
        }

        var assertion = new Assertion(
            id,
            Matter.Id,
            subjectReference,
            predicate,
            value,
            assertedBy,
            eventTime,
            assertedAt,
            sourceSpanId,
            originClass,
            assertionClass,
            disputeState,
            integrityState,
            verificationState,
            extractionConfidence,
            createdByModel);
        _assertions.Add(id, assertion);
        return assertion;
    }

    public MatterEvent AddEvent(
        Guid id,
        string eventType,
        string label,
        EventStatus status,
        VerificationState verificationState,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        IReadOnlyList<Guid>? participantIds = null)
    {
        RequireId(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        RequireDefinedEnum(status);
        RequireDefinedEnum(verificationState);
        if (endTime < startTime) throw new ArgumentOutOfRangeException(nameof(endTime), "Event end cannot precede its start.");
        EnsureAvailable(_events, id, "event");

        IReadOnlyList<Guid> participants = Array.AsReadOnly(participantIds?.Distinct().ToArray() ?? []);
        var matterEvent = new MatterEvent(
            id,
            Matter.Id,
            eventType,
            startTime,
            endTime,
            participants,
            label,
            status,
            verificationState);
        _events.Add(id, matterEvent);
        return matterEvent;
    }

    public AssertionEventLink AddAssertionEventLink(
        Guid id,
        Guid assertionId,
        Guid eventId,
        AssertionEventRelation relation)
    {
        RequireId(id, nameof(id));
        RequireDefinedEnum(relation);
        var assertion = RequireOwned(_assertions, assertionId, "assertion");
        var matterEvent = RequireOwned(_events, eventId, "event");
        if (assertion.MatterId != matterEvent.MatterId || assertion.MatterId != Matter.Id)
        {
            throw new InvalidOperationException("Assertion/event links cannot cross Matter boundaries.");
        }

        EnsureAvailable(_links, id, "assertion/event link");
        if (_links.Values.Any(existing =>
                existing.AssertionId == assertionId &&
                existing.EventId == eventId &&
                existing.Relation == relation))
        {
            throw new InvalidOperationException("The assertion/event relationship already exists.");
        }

        var link = new AssertionEventLink(id, Matter.Id, assertionId, eventId, relation);
        _links.Add(id, link);
        return link;
    }

    public Contradiction AddContradiction(
        Guid id,
        Guid assertionAId,
        Guid assertionBId,
        ContradictionType type,
        string detectedBy,
        DateTimeOffset createdAt)
    {
        RequireId(id, nameof(id));
        RequireDefinedEnum(type);
        if (assertionAId == assertionBId) throw new ArgumentException("A contradiction requires two distinct assertions.");
        var assertionA = RequireOwned(_assertions, assertionAId, "first assertion");
        var assertionB = RequireOwned(_assertions, assertionBId, "second assertion");
        if (assertionA.MatterId != assertionB.MatterId || assertionA.MatterId != Matter.Id)
        {
            throw new InvalidOperationException("Contradictions cannot cross Matter boundaries.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detectedBy);
        EnsureAvailable(_contradictions, id, "contradiction");
        if (_contradictions.Values.Any(existing =>
                (existing.AssertionAId == assertionAId && existing.AssertionBId == assertionBId) ||
                (existing.AssertionAId == assertionBId && existing.AssertionBId == assertionAId)))
        {
            throw new InvalidOperationException("A contradiction between these assertions already exists.");
        }

        var contradiction = new Contradiction(
            id,
            Matter.Id,
            assertionAId,
            assertionBId,
            type,
            detectedBy,
            ContradictionResolutionState.Unresolved,
            null,
            createdAt,
            null);
        _assertions[assertionAId] = assertionA.WithDisputeState(DisputeState.Contradicted);
        _assertions[assertionBId] = assertionB.WithDisputeState(DisputeState.Contradicted);
        _contradictions.Add(id, contradiction);
        return contradiction;
    }

    public AnalysisNode AddAnalysisNode(
        Guid id,
        string analysisType,
        IReadOnlyList<Guid> sourceSpanIds,
        string provider,
        string model,
        string promptVersion,
        string output,
        DateTimeOffset generatedAt,
        VerificationState verificationState)
    {
        RequireId(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisType);
        ArgumentNullException.ThrowIfNull(sourceSpanIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        RequireDefinedEnum(verificationState);
        EnsureAvailable(_analysisNodes, id, "analysis node");

        var sources = sourceSpanIds.Distinct().ToArray();
        foreach (var sourceSpanId in sources)
        {
            _ = RequireOwned(_sourceSpans, sourceSpanId, "analysis source span");
        }

        var node = new AnalysisNode(
            id,
            Matter.Id,
            analysisType,
            Array.AsReadOnly(sources),
            provider,
            model,
            promptVersion,
            output,
            generatedAt,
            verificationState,
            null);
        _analysisNodes.Add(id, node);
        return node;
    }

    public EventCorrectionResult CorrectEventDate(
        Guid eventId,
        Guid correctedEventId,
        DateTimeOffset? correctedStartTime,
        DateTimeOffset? correctedEndTime,
        string correctedLabel,
        Guid auditEventId,
        string actor,
        DateTimeOffset correctedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(correctedLabel);
        RequireId(correctedEventId, nameof(correctedEventId));
        RequireId(auditEventId, nameof(auditEventId));
        if (correctedEndTime < correctedStartTime)
        {
            throw new ArgumentOutOfRangeException(nameof(correctedEndTime), "Event end cannot precede its start.");
        }

        var original = RequireOwned(_events, eventId, "event");
        if (original.SupersededByEventId.HasValue)
        {
            throw new InvalidOperationException("A superseded event cannot be corrected again.");
        }

        EnsureAvailable(_events, correctedEventId, "corrected event");
        if (_auditEvents.Any(existing => existing.Id == auditEventId))
        {
            throw new InvalidOperationException("Audit event id already exists.");
        }

        var corrected = new MatterEvent(
            correctedEventId,
            Matter.Id,
            original.EventType,
            correctedStartTime,
            correctedEndTime,
            original.ParticipantIds,
            correctedLabel,
            EventStatus.Candidate,
            VerificationState.NotReviewed,
            original.Id);
        var superseded = original.SupersededBy(correctedEventId);
        _events[original.Id] = superseded;
        _events.Add(correctedEventId, corrected);

        var auditEvent = new AuditEvent(
            auditEventId,
            Matter.Id,
            AuditEventKind.EventCorrected,
            nameof(MatterEvent),
            original.Id,
            correctedEventId,
            actor,
            $"Event date corrected from {FormatTimeRange(original.StartTime, original.EndTime)} to {FormatTimeRange(correctedStartTime, correctedEndTime)}.",
            correctedAt);
        _auditEvents.Add(auditEvent);
        return new EventCorrectionResult(superseded, corrected, auditEvent);
    }

    private static T RequireOwned<T>(
        IReadOnlyDictionary<Guid, T> records,
        Guid id,
        string label,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(id))] string? parameterName = null)
    {
        RequireId(id, parameterName ?? nameof(id));
        if (!records.TryGetValue(id, out var record))
        {
            throw new InvalidOperationException($"The {label} is not registered to this Matter.");
        }

        return record;
    }

    private static void EnsureAvailable<T>(IReadOnlyDictionary<Guid, T> records, Guid id, string label)
    {
        if (records.ContainsKey(id)) throw new InvalidOperationException($"The {label} id already exists.");
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty) throw new ArgumentException("A non-empty id is required.", parameterName);
    }

    private static string NormalizeSha256(string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        if (contentSha256.Length != 64 || contentSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Content hash must be a 64-character SHA-256 value.", nameof(contentSha256));
        }

        return contentSha256.ToUpperInvariant();
    }

    private static void ValidateConfidence(decimal? extractionConfidence)
    {
        if (extractionConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(extractionConfidence), "Extraction confidence must be between zero and one.");
        }
    }

    private static void RequireDefinedEnum<TEnum>(
        TEnum value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "A defined enum value is required.");
        }
    }

    private static bool RequiresSourceSpan(EvidenceOriginClass originClass, AssertionClass assertionClass)
    {
        var documentaryOrigin = originClass is
            EvidenceOriginClass.OriginalContemporaneousRecord or
            EvidenceOriginClass.IndependentThirdPartyRecord or
            EvidenceOriginClass.EmployerAuthoredDocument or
            EvidenceOriginClass.EmployeeAuthoredDocument or
            EvidenceOriginClass.TranscriptDerivedRecord or
            EvidenceOriginClass.OcrDerivedRecord;
        var documentaryAssertion = assertionClass is
            AssertionClass.DirectlyDocumentedEvent or
            AssertionClass.DirectQuotation;
        return documentaryOrigin || documentaryAssertion;
    }

    private static string FormatTimeRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        var startText = start?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown";
        var endText = end?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown";
        return $"{startText}–{endText}";
    }
}
