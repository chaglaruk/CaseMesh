using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using CaseMesh.Core.Models;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;

namespace CaseMesh.ProfessionalExport;

public sealed class ProfessionalExportGenerator(TimeProvider timeProvider)
{
    public const string CurrentSchemaVersion = "professional-export/v1";
    public const string CurrentTemplateVersion = "neutral-handover/v1";
    private const int MaximumRecordsPerKind = 50_000;
    internal const long MaximumInputUtf8Bytes = 32L * 1024 * 1024;
    private const int MaximumArtifactBytes = 64 * 1024 * 1024;
    private static readonly DateTimeOffset StableArchiveTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly string[] ProhibitedBriefTerms =
    [
        "legal liability", "liable", "merits score", "win probability", "compensation estimate",
        "should file", "should settle", "should resign", "solicitor-ready legal opinion"
    ];
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public ProfessionalExportPackage Generate(
        ProfessionalExportRequest request,
        ProfessionalExportInput input)
    {
        ValidateInput(request, input);
        var generatedAt = _timeProvider.GetUtcNow();
        var model = BuildModel(request, input, generatedAt);
        var prefix = $"casemesh-{request.MatterId:N}-{request.ExportId:N}";

        var payload = new List<GeneratedProfessionalExportArtifact>
        {
            Artifact(ProfessionalExportArtifactKind.BriefDocx, $"{prefix}-brief.docx", CreateDocx(model)),
            Artifact(ProfessionalExportArtifactKind.EvidenceIndexCsv, $"{prefix}-evidence-index.csv", EvidenceCsv(model)),
            Artifact(ProfessionalExportArtifactKind.ChronologyCsv, $"{prefix}-chronology.csv", ChronologyCsv(model)),
            Artifact(ProfessionalExportArtifactKind.AssertionsCsv, $"{prefix}-assertions.csv", AssertionsCsv(model)),
            Artifact(ProfessionalExportArtifactKind.ContradictionsCsv, $"{prefix}-contradictions.csv", ContradictionsCsv(model)),
            Artifact(ProfessionalExportArtifactKind.OriginalEvidenceManifestJson, $"{prefix}-original-evidence.json",
                JsonBytes(CreateOriginalEvidenceManifest(model)))
        };
        var payloadDigests = payload.Select(ToDigest).OrderBy(item => item.FileName, StringComparer.Ordinal).ToArray();
        var manifest = model with { PayloadArtifacts = payloadDigests };
        var manifestArtifact = Artifact(
            ProfessionalExportArtifactKind.MatterManifestJson,
            $"{prefix}-manifest.json",
            JsonBytes(manifest));
        payload.Add(manifestArtifact);
        var bundleArtifact = Artifact(
            ProfessionalExportArtifactKind.BundleZip,
            $"{prefix}-bundle.zip",
            CreateBundle(payload));
        payload.Add(bundleArtifact);

        var artifactDigests = payload.Select(ToDigest).OrderBy(item => item.Kind).ToArray();
        var run = new ProfessionalExportRun(
            request.ExportId,
            request.TenantId,
            request.MatterId,
            manifest.SnapshotDigest,
            CurrentSchemaVersion,
            CurrentTemplateVersion,
            generatedAt,
            Sha256(JsonSerializer.Serialize(artifactDigests, JsonOptions)),
            manifest.Documents.Select(item => item.DocumentVersionId).Order().ToArray(),
            manifest.Sources.Select(item => item.SourceSpanId).Order().ToArray(),
            manifest.Assertions.Select(item => item.AssertionId).Order().ToArray(),
            input.Evidence.Events.Select(item => item.Id).Order().ToArray(),
            manifest.Contradictions.Select(item => item.ContradictionId).Order().ToArray(),
            artifactDigests);
        return new ProfessionalExportPackage(run, manifest, payload.OrderBy(item => item.Kind).ToArray());
    }

    private static ProfessionalExportManifest BuildModel(
        ProfessionalExportRequest request,
        ProfessionalExportInput input,
        DateTimeOffset generatedAt)
    {
        var evidence = input.Evidence;
        var documentReferences = input.Documents
            .OrderBy(item => item.DocumentId)
            .ThenBy(item => item.DocumentVersionId)
            .Select((item, index) => (item.DocumentVersionId, Reference: $"DOC-{index + 1:D4}"))
            .ToDictionary(item => item.DocumentVersionId, item => item.Reference);
        var sources = evidence.SourceSpans
            .OrderBy(item => documentReferences[item.DocumentVersion.DocumentVersionId], StringComparer.Ordinal)
            .ThenBy(item => item.PageNumber ?? int.MaxValue)
            .ThenBy(item => item.TextStart ?? int.MaxValue)
            .ThenBy(item => item.Id)
            .ToArray();
        var sourceReferences = sources.Select((item, index) => (item.Id, Reference: $"SRC-{index + 1:D5}"))
            .ToDictionary(item => item.Id, item => item.Reference);
        var assertions = evidence.Assertions.OrderBy(item => item.Id).ToArray();
        var assertionReferences = assertions.Select((item, index) => (item.Id, Reference: $"AST-{index + 1:D5}"))
            .ToDictionary(item => item.Id, item => item.Reference);
        var events = evidence.Events.OrderBy(item => item.Id).ToArray();
        var eventReferences = events.Select((item, index) => (item.Id, Reference: $"EVT-{index + 1:D5}"))
            .ToDictionary(item => item.Id, item => item.Reference);
        var contradictions = evidence.Contradictions.OrderBy(item => item.Id).ToArray();
        var assertionsById = assertions.ToDictionary(item => item.Id);
        var contradictionReferences = contradictions
            .Select((item, index) => (item.Id, Reference: $"CTR-{index + 1:D5}"))
            .ToDictionary(item => item.Id, item => item.Reference);

        var sourceMetadata = input.SourceMetadata.ToDictionary(item => item.SourceSpanId);
        var sourceItems = sources.Select(item =>
        {
            var metadata = sourceMetadata[item.Id];
            return new ExportSourceItem(
                sourceReferences[item.Id], item.Id, documentReferences[item.DocumentVersion.DocumentVersionId],
                item.DocumentVersion.DocumentVersionId, item.PageNumber, item.TextStart, item.TextEnd,
                item.ExtractedTextDigest, item.ParserVersion, item.ExtractionConfidence,
                metadata.LocatorKind, metadata.StableLocator, metadata.ExtractionRoute,
                metadata.ExtractionProvider, metadata.ExtractionProviderVersion,
                metadata.BoundingBoxLeft, metadata.BoundingBoxTop,
                metadata.BoundingBoxWidth, metadata.BoundingBoxHeight);
        }).ToArray();
        var dependencyAssertionIds = input.Brain.Dependencies
            .Where(item => item.CanonicalKind == CanonicalRecordKind.Assertion)
            .Select(item => item.CanonicalId).ToHashSet();
        var activeAssertionIds = input.Brain.ActiveDependencies
            .Where(item => item.CanonicalKind == CanonicalRecordKind.Assertion)
            .Select(item => item.CanonicalId).ToHashSet();
        var assertionItems = assertions.Select(item => new ExportAssertionItem(
            Reference: assertionReferences[item.Id],
            AssertionId: item.Id,
            TopicLabel: $"Predicate: {item.Predicate}",
            SubjectReference: item.SubjectReference,
            Predicate: item.Predicate,
            Value: item.Value,
            AssertedBy: item.AssertedBy,
            AllegedEventTime: item.EventTime,
            AssertedAt: item.AssertedAt,
            OriginLabel: $"{item.OriginClass}/{item.AssertionClass}",
            DisputeState: item.DisputeState,
            IntegrityState: item.IntegrityState,
            VerificationState: item.VerificationState,
            ExtractionConfidence: item.ExtractionConfidence,
            SourceReference: item.SourceSpanId.HasValue ? sourceReferences[item.SourceSpanId.Value] : null,
            IsCurrent: !item.SupersededByAssertionId.HasValue && item.DisputeState != DisputeState.Superseded &&
                       (!dependencyAssertionIds.Contains(item.Id) || activeAssertionIds.Contains(item.Id)),
            SupersededByReference: item.SupersededByAssertionId.HasValue
                ? assertionReferences.GetValueOrDefault(item.SupersededByAssertionId.Value)
                : null)).ToArray();
        var entities = CreateEntityItems(input, sourceReferences);
        var chronology = CreateChronology(
            input, assertionReferences, eventReferences, sourceReferences);
        var contradictionItems = contradictions.Select(item =>
        {
            var first = assertionsById[item.AssertionAId];
            var second = assertionsById[item.AssertionBId];
            return new ExportContradictionItem(
                contradictionReferences[item.Id], item.Id, item.Type, item.ResolutionState,
                assertionReferences[item.AssertionAId], assertionReferences[item.AssertionBId],
                new[] { first.SourceSpanId, second.SourceSpanId }.OfType<Guid>()
                    .Select(id => sourceReferences[id]).Distinct().Order(StringComparer.Ordinal).ToArray(),
                item.ResolutionNote);
        }).ToArray();
        var history = CreateHistory(input, assertionReferences, eventReferences, sourceReferences);
        var auditTrail = evidence.AuditEvents.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id)
            .Select((item, index) => new ExportAuditItem(
                $"AUD-{index + 1:D5}", item.Id, item.Kind, item.EntityType, item.EntityId,
                item.ReplacementEntityId, item.Actor, item.ChangeSummary, item.OccurredAt))
            .ToArray();
        var workplace = CreateWorkplace(input, assertionReferences, eventReferences);
        var questions = CreateOpenQuestions(
            input, assertionItems, contradictionItems, eventReferences, sourceReferences, workplace);
        var neutralBrief = CreateNeutralBrief(assertionItems, chronology, contradictionItems, questions);
        GuardNeutralBrief(neutralBrief);
        var citedSourceReferences = assertionItems.Select(item => item.SourceReference).OfType<string>()
            .Concat(entities.SelectMany(item => item.SourceReferences))
            .Concat(chronology.SelectMany(item => item.SourceReferences))
            .Concat(contradictionItems.SelectMany(item => item.SourceReferences))
            .Concat(history.SelectMany(item => item.SourceReferences))
            .ToHashSet(StringComparer.Ordinal);
        var documentItems = input.Documents
            .OrderBy(item => documentReferences[item.DocumentVersionId], StringComparer.Ordinal)
            .Select(item => new ExportDocumentItem(
                documentReferences[item.DocumentVersionId], item.DocumentId, item.DocumentVersionId,
                item.OriginalObjectId, item.ContentSha256, item.DetectedMediaType ?? "not-recorded",
                item.ByteLength, item.ProcessingStatus, item.ExtractionRoutes,
                item.ParserVersions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                item.OcrProviders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                item.OcrVersions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                sourceItems.Count(source => source.DocumentVersionId == item.DocumentVersionId &&
                                            citedSourceReferences.Contains(source.Reference)),
                input.Documents.Count(other => other.OriginalObjectId == item.OriginalObjectId) > 1))
            .ToArray();
        var snapshotDigest = CreateSnapshotDigest(input);
        return new ProfessionalExportManifest(
            CurrentSchemaVersion, CurrentTemplateVersion, request.ExportId, request.TenantId,
            request.MatterId, $"MAT-{request.MatterId:N}", evidence.Matter.MatterType,
            evidence.Matter.Title, evidence.Matter.Status, evidence.Matter.Jurisdiction,
            generatedAt, snapshotDigest, neutralBrief, documentItems, sourceItems, entities,
            chronology, assertionItems, contradictionItems, history, auditTrail, questions, workplace, []);
    }

    private static IReadOnlyList<ExportEntityItem> CreateEntityItems(
        ProfessionalExportInput input,
        IReadOnlyDictionary<Guid, string> sourceReferences)
    {
        var result = new List<ExportEntityItem>();
        var activeDependencies = input.Brain.ActiveDependencies.ToArray();
        var people = input.Brain.People.OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .ThenBy(item => item.Id).ToArray();
        foreach (var (person, index) in people.Select((item, index) => (item, index)))
        {
            var aliases = input.Brain.Aliases.Where(item => item.EntityKind == CanonicalEntityKind.Person && item.EntityId == person.Id)
                .OrderBy(item => item.NormalizedValue, StringComparer.Ordinal).ThenBy(item => item.Id).ToArray();
            result.Add(new ExportEntityItem(
                $"PER-{index + 1:D4}", person.Id, "Person", person.DisplayName,
                person.RoleLabels.Order(StringComparer.Ordinal).ToArray(),
                aliases.Select(item => item.Value).ToArray(),
                aliases.Select(item => item.SourceSpanId).OfType<Guid>()
                    .Concat(activeDependencies.Where(item =>
                            item.CanonicalKind == CanonicalRecordKind.Person && item.CanonicalId == person.Id)
                        .Select(item => item.SourceSpanId))
                    .Select(id => sourceReferences[id])
                    .Distinct().Order(StringComparer.Ordinal).ToArray()));
        }

        var organisations = input.Brain.Organisations.OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Id).ToArray();
        foreach (var (organisation, index) in organisations.Select((item, index) => (item, index)))
        {
            var aliases = input.Brain.Aliases.Where(item => item.EntityKind == CanonicalEntityKind.Organisation && item.EntityId == organisation.Id)
                .OrderBy(item => item.NormalizedValue, StringComparer.Ordinal).ThenBy(item => item.Id).ToArray();
            result.Add(new ExportEntityItem(
                $"ORG-{index + 1:D4}", organisation.Id, "Organisation", organisation.Name,
                [organisation.TypeLabel], aliases.Select(item => item.Value).ToArray(),
                aliases.Select(item => item.SourceSpanId).OfType<Guid>()
                    .Concat(activeDependencies.Where(item =>
                            item.CanonicalKind == CanonicalRecordKind.Organisation &&
                            item.CanonicalId == organisation.Id)
                        .Select(item => item.SourceSpanId))
                    .Select(id => sourceReferences[id])
                    .Distinct().Order(StringComparer.Ordinal).ToArray()));
        }

        return result.ToArray();
    }

    private static IReadOnlyList<ExportChronologyItem> CreateChronology(
        ProfessionalExportInput input,
        IReadOnlyDictionary<Guid, string> assertionReferences,
        IReadOnlyDictionary<Guid, string> eventReferences,
        IReadOnlyDictionary<Guid, string> sourceReferences)
    {
        var evidence = input.Evidence;
        var assertions = evidence.Assertions.ToDictionary(item => item.Id);
        var entries = new List<(Guid Id, string Kind, DateTimeOffset? Start, DateTimeOffset? End,
            string Label, string Status, IReadOnlyList<string> Assertions, IReadOnlyList<string> Sources)>();
        foreach (var matterEvent in evidence.Events)
        {
            var linked = evidence.AssertionEventLinks.Where(item => item.EventId == matterEvent.Id)
                .Select(item => assertions[item.AssertionId]).OrderBy(item => item.Id).ToArray();
            entries.Add((matterEvent.Id, "Event", matterEvent.StartTime, matterEvent.EndTime,
                matterEvent.Label, $"{matterEvent.Status}/{matterEvent.VerificationState}",
                linked.Select(item => assertionReferences[item.Id]).ToArray(),
                linked.Select(item => item.SourceSpanId).OfType<Guid>().Select(id => sourceReferences[id])
                    .Distinct().Order(StringComparer.Ordinal).ToArray()));
        }

        foreach (var communication in input.Brain.Communications)
        {
            entries.Add((communication.Id, "Communication", communication.OccurredAt, communication.OccurredAt,
                communication.NeutralLabel, communication.VerificationState.ToString(), [],
                communication.SourceSpanIds.Select(id => sourceReferences[id]).Distinct().Order(StringComparer.Ordinal).ToArray()));
        }

        foreach (var assertion in evidence.Assertions.Where(item => item.EventTime.HasValue))
        {
            entries.Add((assertion.Id, "Alleged assertion time", assertion.EventTime, assertion.EventTime,
                $"{assertion.SubjectReference}: {assertion.Predicate} = {assertion.Value}",
                $"{assertion.DisputeState}/{assertion.VerificationState}", [assertionReferences[assertion.Id]],
                assertion.SourceSpanId.HasValue ? [sourceReferences[assertion.SourceSpanId.Value]] : []));
        }

        return entries
            .OrderBy(item => item.Start.HasValue ? 0 : 1)
            .ThenBy(item => item.Start)
            .ThenBy(item => item.End)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Select((item, index) => new ExportChronologyItem(
                $"CHR-{index + 1:D5}", item.Id, item.Kind, item.Start, item.End, item.Label,
                item.Status, item.Assertions, item.Sources))
            .ToArray();
    }

    private static IReadOnlyList<ExportHistoryItem> CreateHistory(
        ProfessionalExportInput input,
        IReadOnlyDictionary<Guid, string> assertionReferences,
        IReadOnlyDictionary<Guid, string> eventReferences,
        IReadOnlyDictionary<Guid, string> sourceReferences)
    {
        var entries = new List<(Guid Id, string Kind, string Status, string? ReplacementReference,
            Guid? ReplacementId, IReadOnlyList<string> Sources)>();
        foreach (var assertion in input.Evidence.Assertions.Where(item => item.SupersededByAssertionId.HasValue))
        {
            entries.Add((assertion.Id, "Assertion", assertion.DisputeState.ToString(),
                assertionReferences.GetValueOrDefault(assertion.SupersededByAssertionId!.Value),
                assertion.SupersededByAssertionId,
                assertion.SourceSpanId.HasValue ? [sourceReferences[assertion.SourceSpanId.Value]] : []));
        }
        foreach (var matterEvent in input.Evidence.Events.Where(item => item.SupersededByEventId.HasValue))
        {
            var linkedSources = input.Evidence.AssertionEventLinks.Where(item => item.EventId == matterEvent.Id)
                .Select(link => input.Evidence.Assertions.Single(assertion => assertion.Id == link.AssertionId).SourceSpanId)
                .OfType<Guid>().Select(id => sourceReferences[id]).Distinct().Order(StringComparer.Ordinal).ToArray();
            entries.Add((matterEvent.Id, "Event", matterEvent.Status.ToString(),
                eventReferences.GetValueOrDefault(matterEvent.SupersededByEventId!.Value),
                matterEvent.SupersededByEventId, linkedSources));
        }

        return entries.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id)
            .Select((item, index) => new ExportHistoryItem(
                $"HIS-{index + 1:D5}", item.Kind, item.Id, item.Status,
                item.ReplacementReference, item.ReplacementId, item.Sources)).ToArray();
    }

    private static ExportWorkplaceSection CreateWorkplace(
        ProfessionalExportInput input,
        IReadOnlyDictionary<Guid, string> assertionReferences,
        IReadOnlyDictionary<Guid, string> eventReferences)
    {
        static string Dates(DateOnly? start, DateOnly? end) =>
            $"{start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated"} to {end?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "open"}";
        IReadOnlyList<string> Assertions(IEnumerable<Guid> ids) => ids.Select(id => assertionReferences[id]).Order(StringComparer.Ordinal).ToArray();
        IReadOnlyList<string> Events(IEnumerable<Guid> ids) => ids.Select(id => eventReferences[id]).Order(StringComparer.Ordinal).ToArray();
        var workplace = input.Workplace;
        return new ExportWorkplaceSection(
            workplace.EmploymentProfiles.OrderBy(item => item.Id).Select((item, index) => new ExportWorkplaceItem(
                $"WEP-{index + 1:D4}", item.Id, "Employment profile",
                $"{item.EmployerReference}; {item.RoleTitle}; {Dates(item.EmploymentStartedOn, item.EmploymentEndedOn)}",
                item.EvidenceReviewState.ToString(), Assertions(item.SupportingAssertionIds), [])).ToArray(),
            workplace.EmploymentTerms.OrderBy(item => item.Id).Select((item, index) => new ExportWorkplaceItem(
                $"WET-{index + 1:D4}", item.Id, item.Kind.ToString(),
                $"{item.Value}; {Dates(item.EffectiveFrom, item.EffectiveTo)}",
                item.SupersedesEmploymentTermId.HasValue ? "Superseding record" : "Recorded term",
                Assertions(item.SupportingAssertionIds), [])).ToArray(),
            workplace.HealthAbsenceRecords.OrderBy(item => item.Id).Select((item, index) => new ExportWorkplaceItem(
                $"WHA-{index + 1:D4}", item.Id, item.Kind.ToString(), item.NeutralLabel,
                item.EvidenceReviewState.ToString(), Assertions(item.AssertionIds), Events(item.EventIds))).ToArray(),
            workplace.AdjustmentRequests.OrderBy(item => item.Id).Select((item, index) => new ExportAdjustmentItem(
                $"WAR-{index + 1:D4}", item.Id, item.NeutralLabel, item.ResponseStatus.ToString(),
                Assertions(item.RequestAssertionIds), Assertions(item.ResponseAssertionIds),
                Assertions(item.ImplementationAssertionIds))).ToArray(),
            workplace.WorkplaceProcesses.OrderBy(item => item.Id).Select((item, index) => new ExportWorkplaceItem(
                $"WPR-{index + 1:D4}", item.Id, item.Kind.ToString(), item.StageLabel,
                item.Status.ToString(), Assertions(item.AssertionIds), Events(item.EventIds))).ToArray(),
            workplace.AcasProcessStates.OrderBy(item => item.Id).Select((item, index) => new ExportWorkplaceItem(
                $"WAC-{index + 1:D4}", item.Id, "ACAS process", item.Stage.ToString(),
                "Descriptive state only", Assertions(item.AssertionIds), Events(item.EventIds))).ToArray());
    }

    private static IReadOnlyList<ExportOpenQuestion> CreateOpenQuestions(
        ProfessionalExportInput input,
        IReadOnlyList<ExportAssertionItem> assertions,
        IReadOnlyList<ExportContradictionItem> contradictions,
        IReadOnlyDictionary<Guid, string> eventReferences,
        IReadOnlyDictionary<Guid, string> sourceReferences,
        ExportWorkplaceSection workplace)
    {
        var questions = new List<(string Category, string Text, IReadOnlyList<string> Related)>();
        foreach (var contradiction in contradictions.Where(item => item.ResolutionState == ContradictionResolutionState.Unresolved))
        {
            questions.Add(("Unresolved conflict",
                $"What additional source could clarify the differing attributed records in {contradiction.Reference}?",
                [contradiction.Reference, contradiction.AssertionAReference, contradiction.AssertionBReference]));
        }
        foreach (var assertion in assertions.Where(item => item.IsCurrent && item.SourceReference is not null &&
                     item.VerificationState != VerificationState.Confirmed &&
                     item.OriginLabel.Contains("Assertion", StringComparison.Ordinal)))
        {
            questions.Add(("Corroboration",
                $"Is there an independent or contemporaneous record that can corroborate {assertion.Reference}?",
                [assertion.Reference, assertion.SourceReference!]));
        }
        var linkedEventIds = input.Evidence.AssertionEventLinks.Select(item => item.EventId).ToHashSet();
        foreach (var matterEvent in input.Evidence.Events.Where(item => !linkedEventIds.Contains(item.Id)))
        {
            questions.Add(("Event evidence",
                $"What documentary evidence, if any, supports the candidate event {eventReferences[matterEvent.Id]}?",
                [eventReferences[matterEvent.Id]]));
        }
        foreach (var request in input.Workplace.AdjustmentRequests)
        {
            var reference = workplace.AdjustmentRequests.Single(item => item.Id == request.Id).Reference;
            if (request.ResponseAssertionIds.Count == 0)
            {
                questions.Add(("Adjustment response",
                    $"Is there a recorded response to the adjustment request {reference}?", [reference]));
            }
            if (request.ImplementationAssertionIds.Count == 0)
            {
                questions.Add(("Adjustment implementation",
                    $"Is there evidence showing whether the requested adjustment in {reference} was implemented?", [reference]));
            }
        }
        foreach (var process in input.Workplace.WorkplaceProcesses)
        {
            var hasSource = process.AssertionIds.Select(id => input.Evidence.Assertions.Single(item => item.Id == id))
                .Any(item => item.SourceSpanId.HasValue && sourceReferences.ContainsKey(item.SourceSpanId.Value));
            if (!hasSource)
            {
                var reference = workplace.Processes.Single(item => item.Id == process.Id).Reference;
                questions.Add(("Process evidence",
                    $"What source records the described workplace process state in {reference}?", [reference]));
            }
        }

        return questions.OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Text, StringComparer.Ordinal)
            .ThenBy(item => string.Join('|', item.Related), StringComparer.Ordinal)
            .Select((item, index) => new ExportOpenQuestion(
                $"Q-{index + 1:D4}", item.Category, item.Text, item.Related)).ToArray();
    }

    private static string CreateNeutralBrief(
        IReadOnlyList<ExportAssertionItem> assertions,
        IReadOnlyList<ExportChronologyItem> chronology,
        IReadOnlyList<ExportContradictionItem> contradictions,
        IReadOnlyList<ExportOpenQuestion> questions)
    {
        var documentary = assertions.Count(item => item.SourceReference is not null);
        var sourceLess = assertions.Count - documentary;
        var unresolved = contradictions.Where(item => item.ResolutionState == ContradictionResolutionState.Unresolved)
            .Select(item => item.Reference).ToArray();
        return $"This neutral evidence view contains {assertions.Count} attributed assertions " +
               $"({documentary} documentary and {sourceLess} source-less), {chronology.Count} chronology entries, " +
               $"and {unresolved.Length} unresolved contradictions" +
               (unresolved.Length == 0 ? ". " : $" ({string.Join(", ", unresolved)}). ") +
               $"Assertions remain attributed records rather than established facts. {questions.Count} factual questions are listed for further evidence review.";
    }

    internal static void GuardNeutralBrief(string brief)
    {
        if (ProhibitedBriefTerms.Any(term => brief.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The neutral brief contains prohibited recommendation or outcome language.");
        }
    }

    private static string CreateSnapshotDigest(ProfessionalExportInput input)
    {
        var evidence = input.Evidence.CaptureSnapshot();
        var workplace = input.Workplace.CaptureSnapshot();
        var brain = input.Brain.CaptureSnapshot();
        var fingerprint = new
        {
            matter = evidence.Matter,
            documentVersions = evidence.DocumentVersions.OrderBy(item => item.DocumentVersionId),
            sourceSpans = evidence.SourceSpans.OrderBy(item => item.Id).Select(item => new
            {
                item.Id, item.DocumentVersionId, item.PageNumber, item.TextStart, item.TextEnd,
                item.ExtractedTextDigest, item.ParserVersion, item.ExtractionConfidence
            }),
            assertions = evidence.Assertions.OrderBy(item => item.Id),
            events = evidence.Events.OrderBy(item => item.Id),
            links = evidence.AssertionEventLinks.OrderBy(item => item.Id),
            contradictions = evidence.Contradictions.OrderBy(item => item.Id),
            analyses = evidence.AnalysisNodes.OrderBy(item => item.Id),
            audits = evidence.AuditEvents.OrderBy(item => item.Id),
            workplace = new
            {
                employmentProfiles = workplace.EmploymentProfiles.OrderBy(item => item.Id),
                employmentTerms = workplace.EmploymentTerms.OrderBy(item => item.Id),
                healthAbsenceRecords = workplace.HealthAbsenceRecords.OrderBy(item => item.Id),
                adjustmentRequests = workplace.AdjustmentRequests.OrderBy(item => item.Id),
                workplaceProcesses = workplace.WorkplaceProcesses.OrderBy(item => item.Id),
                acasProcessStates = workplace.AcasProcessStates.OrderBy(item => item.Id)
            },
            people = brain.People.OrderBy(item => item.Id),
            organisations = brain.Organisations.OrderBy(item => item.Id),
            aliases = brain.Aliases.OrderBy(item => item.Id),
            communications = brain.Communications.OrderBy(item => item.Id),
            runs = brain.Runs.OrderBy(item => item.Id),
            candidates = brain.Candidates.OrderBy(item => item.Id),
            dependencies = brain.Dependencies.OrderBy(item => item.Id),
            invalidations = brain.DependencyInvalidations.OrderBy(item => item.Id),
            resolutionActions = brain.EntityResolutionActions.OrderBy(item => item.Id),
            documents = input.Documents.OrderBy(item => item.DocumentVersionId),
            sourceMetadata = input.SourceMetadata.OrderBy(item => item.SourceSpanId)
        };
        return Sha256(JsonSerializer.Serialize(fingerprint, JsonOptions));
    }

    private static void ValidateInput(ProfessionalExportRequest request, ProfessionalExportInput input)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Evidence);
        ArgumentNullException.ThrowIfNull(input.Workplace);
        ArgumentNullException.ThrowIfNull(input.Brain);
        ArgumentNullException.ThrowIfNull(input.Documents);
        ArgumentNullException.ThrowIfNull(input.SourceMetadata);
        if (request.MatterId == Guid.Empty || request.ExportId == Guid.Empty)
        {
            throw new ArgumentException("Matter and export identifiers must be non-empty.");
        }
        if (request.TenantId != input.Evidence.Matter.TenantId || request.MatterId != input.Evidence.Matter.Id ||
            !ReferenceEquals(input.Evidence, input.Workplace.Evidence) ||
            !ReferenceEquals(input.Evidence, input.Brain.Evidence))
        {
            throw new InvalidOperationException("The export request and canonical state must share one tenant-owned Matter aggregate.");
        }

        var evidenceVersions = input.Evidence.DocumentVersions.OrderBy(item => item.DocumentVersionId).ToArray();
        if (input.Documents.Count != evidenceVersions.Length ||
            input.Documents.Select(item => item.DocumentVersionId).Distinct().Count() != input.Documents.Count)
        {
            throw new InvalidOperationException("Export document metadata must cover each Matter document version exactly once.");
        }
        foreach (var metadata in input.Documents)
        {
            ArgumentNullException.ThrowIfNull(metadata.ParserVersions);
            ArgumentNullException.ThrowIfNull(metadata.OcrProviders);
            ArgumentNullException.ThrowIfNull(metadata.OcrVersions);
            if (metadata.TenantId != request.TenantId || metadata.MatterId != request.MatterId ||
                metadata.DocumentId == Guid.Empty || metadata.DocumentVersionId == Guid.Empty ||
                metadata.OriginalObjectId == Guid.Empty || !IsSha256(metadata.ContentSha256) ||
                metadata.ByteLength is < 0 || !Enum.IsDefined(metadata.ProcessingStatus) ||
                metadata.ExtractionRoutes is < ExportExtractionRoute.None or > (ExportExtractionRoute.Native | ExportExtractionRoute.Ocr) ||
                metadata.ParserVersions.Any(string.IsNullOrWhiteSpace) ||
                metadata.OcrProviders.Any(string.IsNullOrWhiteSpace) ||
                metadata.OcrVersions.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("Export document metadata is invalid or not owned by the requested Matter.");
            }
            var version = evidenceVersions.SingleOrDefault(item => item.DocumentVersionId == metadata.DocumentVersionId)
                ?? throw new InvalidOperationException("Export document metadata references an unknown document version.");
            if (version.DocumentId != metadata.DocumentId || version.OriginalObjectId != metadata.OriginalObjectId ||
                version.ContentSha256 != metadata.ContentSha256)
            {
                throw new InvalidOperationException("Export document metadata diverges from immutable provenance.");
            }
        }
        foreach (var original in input.Documents.GroupBy(item => item.OriginalObjectId))
        {
            if (original.Select(item => item.ContentSha256).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                throw new InvalidOperationException(
                    $"Logical original {original.Key:N} has divergent immutable content digests.");
            }
        }

        RequireBound(input.Evidence.DocumentVersions.Count, nameof(input.Evidence.DocumentVersions));
        RequireBound(input.Evidence.SourceSpans.Count, nameof(input.Evidence.SourceSpans));
        RequireBound(input.Evidence.Assertions.Count, nameof(input.Evidence.Assertions));
        RequireBound(input.Evidence.Events.Count, nameof(input.Evidence.Events));
        RequireBound(input.Evidence.AssertionEventLinks.Count, nameof(input.Evidence.AssertionEventLinks));
        RequireBound(input.Evidence.Contradictions.Count, nameof(input.Evidence.Contradictions));
        RequireBound(input.Evidence.AnalysisNodes.Count, nameof(input.Evidence.AnalysisNodes));
        RequireBound(input.Evidence.AuditEvents.Count, nameof(input.Evidence.AuditEvents));
        RequireBound(input.Workplace.EmploymentProfiles.Count, nameof(input.Workplace.EmploymentProfiles));
        RequireBound(input.Workplace.EmploymentTerms.Count, nameof(input.Workplace.EmploymentTerms));
        RequireBound(input.Workplace.HealthAbsenceRecords.Count, nameof(input.Workplace.HealthAbsenceRecords));
        RequireBound(input.Workplace.AdjustmentRequests.Count, nameof(input.Workplace.AdjustmentRequests));
        RequireBound(input.Workplace.WorkplaceProcesses.Count, nameof(input.Workplace.WorkplaceProcesses));
        RequireBound(input.Workplace.AcasProcessStates.Count, nameof(input.Workplace.AcasProcessStates));
        RequireBound(input.Brain.People.Count, nameof(input.Brain.People));
        RequireBound(input.Brain.Organisations.Count, nameof(input.Brain.Organisations));
        RequireBound(input.Brain.Aliases.Count, nameof(input.Brain.Aliases));
        RequireBound(input.Brain.Communications.Count, nameof(input.Brain.Communications));
        RequireBound(input.Brain.Runs.Count, nameof(input.Brain.Runs));
        RequireBound(input.Brain.Candidates.Count, nameof(input.Brain.Candidates));
        RequireBound(input.Brain.Dependencies.Count, nameof(input.Brain.Dependencies));
        RequireBound(input.Brain.DependencyInvalidations.Count, nameof(input.Brain.DependencyInvalidations));
        RequireBound(input.Brain.EntityResolutionActions.Count, nameof(input.Brain.EntityResolutionActions));
        RequireBound(input.SourceMetadata.Count, nameof(input.SourceMetadata));
        ValidateBoundedSnapshot(input);

        var versionIds = input.Documents.Select(item => item.DocumentVersionId).ToHashSet();
        var spans = input.Evidence.SourceSpans.ToDictionary(item => item.Id);
        if (input.SourceMetadata.Count != spans.Count ||
            input.SourceMetadata.Select(item => item.SourceSpanId).Distinct().Count() != input.SourceMetadata.Count)
        {
            throw new InvalidOperationException("Export source metadata must cover each source span exactly once.");
        }
        foreach (var metadata in input.SourceMetadata)
        {
            var hasIngestionMetadata = metadata.LocatorKind.HasValue;
            var hasCompleteBoundingBox = metadata.BoundingBoxLeft.HasValue;
            if (metadata.TenantId != request.TenantId || metadata.MatterId != request.MatterId ||
                !spans.TryGetValue(metadata.SourceSpanId, out var source) ||
                source.DocumentVersion.DocumentVersionId != metadata.DocumentVersionId ||
                (metadata.LocatorKind.HasValue && !Enum.IsDefined(metadata.LocatorKind.Value)) ||
                (hasIngestionMetadata && metadata.ExtractionRoute is not (ExportExtractionRoute.Native or ExportExtractionRoute.Ocr)) ||
                (!hasIngestionMetadata && metadata.ExtractionRoute != ExportExtractionRoute.None) ||
                (metadata.LocatorKind is null) != (metadata.StableLocator is null) ||
                (metadata.StableLocator is not null && string.IsNullOrWhiteSpace(metadata.StableLocator)) ||
                hasIngestionMetadata != (metadata.ExtractionProvider is not null) ||
                hasIngestionMetadata != (metadata.ExtractionProviderVersion is not null) ||
                (metadata.ExtractionProvider is not null && string.IsNullOrWhiteSpace(metadata.ExtractionProvider)) ||
                (metadata.ExtractionProviderVersion is not null && string.IsNullOrWhiteSpace(metadata.ExtractionProviderVersion)) ||
                new[] { metadata.BoundingBoxLeft, metadata.BoundingBoxTop,
                    metadata.BoundingBoxWidth, metadata.BoundingBoxHeight }.Count(item => item.HasValue) is not (0 or 4) ||
                (hasCompleteBoundingBox && (metadata.ExtractionRoute != ExportExtractionRoute.Ocr ||
                    metadata.LocatorKind != ExportSourceLocatorKind.ImageBoundingBox ||
                    metadata.BoundingBoxLeft < 0 || metadata.BoundingBoxTop < 0 ||
                    metadata.BoundingBoxWidth <= 0 || metadata.BoundingBoxHeight <= 0)))
            {
                throw new InvalidOperationException(
                    "Export source metadata is incomplete, divergent, or not owned by the requested Matter.");
            }
        }
        foreach (var span in spans.Values)
        {
            if (!versionIds.Contains(span.DocumentVersion.DocumentVersionId) || !IsSha256(span.ExtractedTextDigest))
            {
                throw new InvalidOperationException("An exported source span lacks complete immutable document provenance.");
            }
        }
        foreach (var assertion in input.Evidence.Assertions.Where(item => item.SourceSpanId.HasValue))
        {
            if (!spans.TryGetValue(assertion.SourceSpanId!.Value, out var span) ||
                !versionIds.Contains(span.DocumentVersion.DocumentVersionId))
            {
                throw new InvalidOperationException("A source-backed assertion cannot enter an export without a complete provenance chain.");
            }
        }
    }

    private static void RequireBound(int count, string label)
    {
        if (count > MaximumRecordsPerKind)
        {
            throw new InvalidOperationException($"The {label} collection exceeds the export record limit.");
        }
    }

    private static void ValidateBoundedSnapshot(ProfessionalExportInput input)
    {
        using var counter = new BoundedWriteStream(MaximumInputUtf8Bytes);
        try
        {
            JsonSerializer.Serialize(counter, new
            {
                evidence = input.Evidence.CaptureSnapshot(),
                workplace = input.Workplace.CaptureSnapshot(),
                brain = input.Brain.CaptureSnapshot(),
                input.Documents,
                input.SourceMetadata
            }, JsonOptions);
        }
        catch (InputSizeLimitExceededException exception)
        {
            throw new InvalidOperationException("The bounded professional export input is too large.", exception);
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static GeneratedProfessionalExportArtifact Artifact(
        ProfessionalExportArtifactKind kind,
        string fileName,
        byte[] content)
    {
        ValidateFileName(fileName);
        if (content.Length > MaximumArtifactBytes)
        {
            throw new InvalidOperationException("A generated professional export artifact exceeds its bounded size.");
        }
        return new GeneratedProfessionalExportArtifact(kind, fileName, content, Sha256(content));
    }

    private static ProfessionalExportArtifactDigest ToDigest(GeneratedProfessionalExportArtifact artifact) =>
        new(artifact.Kind, artifact.FileName, artifact.Sha256, artifact.ByteLength);

    private static byte[] JsonBytes<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private static byte[] EvidenceCsv(ProfessionalExportManifest manifest) => Csv(
        ["reference", "document_id", "document_version_id", "original_object_id", "sha256", "media_type", "byte_length", "processing_status", "extraction_routes", "parser_versions", "ocr_providers", "ocr_versions", "cited_source_span_count", "shares_logical_original"],
        manifest.Documents.Select(item => new[]
        {
            item.Reference, item.DocumentId.ToString("N"), item.DocumentVersionId.ToString("N"),
            item.OriginalObjectId.ToString("N"), item.ContentSha256, item.DetectedMediaType,
            item.ByteLength?.ToString(CultureInfo.InvariantCulture) ?? "", item.ProcessingStatus.ToString(),
            item.ExtractionRoutes.ToString(), string.Join(';', item.ParserVersions),
            string.Join(';', item.OcrProviders), string.Join(';', item.OcrVersions),
            item.CitedSourceSpanCount.ToString(CultureInfo.InvariantCulture),
            item.SharesLogicalOriginal.ToString(CultureInfo.InvariantCulture)
        }));

    private static byte[] ChronologyCsv(ProfessionalExportManifest manifest) => Csv(
        ["reference", "canonical_id", "kind", "start_time", "end_time", "neutral_label", "status", "assertion_references", "source_references"],
        manifest.Chronology.Select(item => new[]
        {
            item.Reference, item.CanonicalId.ToString("N"), item.Kind, Format(item.StartTime), Format(item.EndTime),
            item.NeutralLabel, item.Status, string.Join(';', item.AssertionReferences), string.Join(';', item.SourceReferences)
        }));

    private static byte[] AssertionsCsv(ProfessionalExportManifest manifest) => Csv(
        ["reference", "assertion_id", "topic", "subject", "predicate", "value", "asserted_by", "alleged_event_time", "asserted_at", "origin", "dispute_state", "integrity_state", "verification_state", "extraction_confidence", "source_reference", "is_current", "superseded_by"],
        manifest.Assertions.Select(item => new[]
        {
            item.Reference, item.AssertionId.ToString("N"), item.TopicLabel, item.SubjectReference, item.Predicate,
            item.Value, item.AssertedBy, Format(item.AllegedEventTime), Format(item.AssertedAt), item.OriginLabel,
            item.DisputeState.ToString(), item.IntegrityState.ToString(), item.VerificationState.ToString(),
            item.ExtractionConfidence?.ToString(CultureInfo.InvariantCulture) ?? "", item.SourceReference ?? "",
            item.IsCurrent.ToString(CultureInfo.InvariantCulture), item.SupersededByReference ?? ""
        }));

    private static byte[] ContradictionsCsv(ProfessionalExportManifest manifest) => Csv(
        ["reference", "contradiction_id", "type", "resolution_state", "assertion_a", "assertion_b", "source_references", "resolution_note"],
        manifest.Contradictions.Select(item => new[]
        {
            item.Reference, item.ContradictionId.ToString("N"), item.Type.ToString(), item.ResolutionState.ToString(),
            item.AssertionAReference, item.AssertionBReference, string.Join(';', item.SourceReferences), item.ResolutionNote ?? ""
        }));

    private static byte[] Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', headers.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(EscapeCsv)));
        }
        return Encoding.UTF8.GetBytes(builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    internal static string EscapeCsv(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var safeValue = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'
            ? $"'{value}"
            : value;
        return safeValue.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : safeValue;
    }

    private static string Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) ?? "";

    private static object CreateOriginalEvidenceManifest(ProfessionalExportManifest manifest) => new
    {
        schemaVersion = "original-evidence-manifest/v1",
        manifest.ExportId,
        manifest.TenantId,
        manifest.MatterId,
        originals = manifest.Documents.GroupBy(item => item.OriginalObjectId).OrderBy(item => item.Key).Select(group => new
        {
            originalObjectId = group.Key,
            sha256 = group.Select(item => item.ContentSha256).Distinct(StringComparer.Ordinal).Single(),
            documentReferences = group.Select(item => item.Reference).Order(StringComparer.Ordinal).ToArray()
        }).ToArray(),
        note = "Logical immutable identities only; raw evidence bytes and object-store locators are not included."
    };

    private static byte[] CreateDocx(ProfessionalExportManifest manifest)
    {
        var paragraphs = new List<(string Text, int Heading)>
        {
            ("CaseMesh Professional Handover", 1),
            ("Matter header", 2),
            ($"Reference: {manifest.MatterReference}", 0),
            ($"Type: {manifest.MatterType}; status: {manifest.MatterStatus}; jurisdiction: {manifest.Jurisdiction ?? "not recorded"}", 0),
            ("Neutral Matter brief", 2),
            (manifest.NeutralBrief, 0),
            ("People and organisations", 2)
        };
        paragraphs.AddRange(manifest.PeopleAndOrganisations.Select(item =>
            ($"{item.Reference} | {item.Kind} | {item.DisplayName} | roles: {string.Join(", ", item.Roles)} | aliases: {string.Join(", ", item.Aliases)} | sources: {JoinOrNone(item.SourceReferences)}", 0)));
        paragraphs.Add(("Source-linked chronology", 2));
        paragraphs.AddRange(manifest.Chronology.Select(item =>
            ($"{item.Reference} | {DisplayTime(item.StartTime, item.EndTime)} | {item.Kind} | {item.NeutralLabel} | {item.Status} | assertions: {JoinOrNone(item.AssertionReferences)} | sources: {JoinOrNone(item.SourceReferences)}", 0)));
        paragraphs.Add(("Evidence and document index", 2));
        paragraphs.AddRange(manifest.Documents.Select(item =>
            ($"{item.Reference} | version {item.DocumentVersionId:N} | type {item.DetectedMediaType} | processing {item.ProcessingStatus}/{item.ExtractionRoutes} | cited spans {item.CitedSourceSpanCount}", 0)));
        paragraphs.Add(("Exact source index", 2));
        paragraphs.AddRange(manifest.Sources.Select(item =>
            ($"{item.Reference} | document {item.DocumentReference} | locator {item.StableLocator ?? FormatOffsets(item)} | route {item.ExtractionRoute} | provider {item.ExtractionProvider ?? item.ParserVersion}/{item.ExtractionProviderVersion ?? "not recorded"} | bounding box {FormatBoundingBox(item)}", 0)));
        paragraphs.Add(("Attributed assertions by topic", 2));
        foreach (var group in manifest.Assertions.GroupBy(item => item.TopicLabel).OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            paragraphs.Add((group.Key, 0));
            paragraphs.AddRange(group.Select(item =>
                ($"{item.Reference} | {item.AssertedBy} ({item.OriginLabel}) asserted: {item.SubjectReference} / {item.Predicate} / {item.Value} | state {item.DisputeState}/{item.IntegrityState}/{item.VerificationState} | extraction confidence {item.ExtractionConfidence?.ToString(CultureInfo.InvariantCulture) ?? "not recorded"} | source {item.SourceReference ?? "none; origin labelled above"}", 0)));
        }
        paragraphs.Add(("Contradictions and disputed records", 2));
        paragraphs.AddRange(manifest.Contradictions.Select(item =>
            ($"{item.Reference} | {item.Type}/{item.ResolutionState} | {item.AssertionAReference} versus {item.AssertionBReference} | sources: {JoinOrNone(item.SourceReferences)}", 0)));
        paragraphs.Add(("Corrections and superseded history", 2));
        paragraphs.AddRange(manifest.SupersededHistory.Select(item =>
            ($"{item.Reference} | {item.Kind} {item.HistoricalId:N} | {item.HistoricalStatus} | replacement {item.ReplacementReference ?? "none"} | sources: {JoinOrNone(item.SourceReferences)}", 0)));
        paragraphs.Add(("Correction and review audit trail", 2));
        paragraphs.AddRange(manifest.AuditTrail.Select(item =>
            ($"{item.Reference} | {item.Kind} | {item.EntityType} {item.EntityId:N} | replacement {item.ReplacementEntityId?.ToString("N") ?? "none"} | actor {item.Actor} | occurred {Format(item.OccurredAt)} | {item.ChangeSummary}", 0)));
        paragraphs.Add(("Open factual questions and missing evidence", 2));
        paragraphs.AddRange(manifest.OpenQuestions.Select(item =>
            ($"{item.Reference} | {item.Category} | {item.NeutralQuestion} | related: {JoinOrNone(item.RelatedReferences)}", 0)));
        paragraphs.Add(("Workplace-specific neutral context", 2));
        foreach (var item in AllWorkplace(manifest.Workplace))
        {
            paragraphs.Add(($"{item.Reference} | {item.Kind} | {item.NeutralLabel} | {item.Status} | assertions: {JoinOrNone(item.AssertionReferences)} | events: {JoinOrNone(item.EventReferences)}", 0));
        }
        foreach (var item in manifest.Workplace.AdjustmentRequests)
        {
            paragraphs.Add(($"{item.Reference} | Adjustment request | {item.NeutralLabel} | response status: {item.ResponseStatus} | requests: {JoinOrNone(item.RequestAssertionReferences)} | responses: {JoinOrNone(item.ResponseAssertionReferences)} | implementation: {JoinOrNone(item.ImplementationAssertionReferences)}", 0));
        }
        paragraphs.Add(("Provenance and generation metadata", 2));
        paragraphs.Add(($"Export {manifest.ExportId:N}; generated {Format(manifest.GeneratedAt)}; schema {manifest.SchemaVersion}; template {manifest.TemplateVersion}; snapshot SHA-256 {manifest.SnapshotDigest}.", 0));
        paragraphs.Add(("This handover is a neutral evidence view, not legal advice or an outcome assessment.", 0));

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
        {
            WriteEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
                </Types>
                """);
            WriteEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "word/_rels/document.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "word/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
                  <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style>
                  <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:rPr><w:b/><w:sz w:val="26"/></w:rPr></w:style>
                </w:styles>
                """);
            WriteEntry(archive, "word/document.xml", CreateDocumentXml(paragraphs));
        }
        return output.ToArray();
    }

    private static string CreateDocumentXml(IEnumerable<(string Text, int Heading)> paragraphs)
    {
        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(builder, new XmlWriterSettings
        {
            // StringBuilder-backed XmlWriter instances otherwise declare UTF-16 even
            // though the package entry is deliberately emitted as UTF-8.
            OmitXmlDeclaration = true,
            Indent = false
        });
        writer.WriteStartElement("w", "document", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        writer.WriteStartElement("w", "body", null);
        foreach (var (text, heading) in paragraphs)
        {
            writer.WriteStartElement("w", "p", null);
            if (heading > 0)
            {
                writer.WriteStartElement("w", "pPr", null);
                writer.WriteStartElement("w", "pStyle", null);
                writer.WriteAttributeString("w", "val", null, $"Heading{heading}");
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteStartElement("w", "r", null);
            writer.WriteStartElement("w", "t", null);
            writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
            writer.WriteString(text);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteStartElement("w", "sectPr", null);
        writer.WriteStartElement("w", "pgSz", null);
        writer.WriteAttributeString("w", "w", null, "11906");
        writer.WriteAttributeString("w", "h", null, "16838");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.Flush();
        return builder.ToString();
    }

    private static IEnumerable<ExportWorkplaceItem> AllWorkplace(ExportWorkplaceSection workplace) =>
        workplace.EmploymentProfiles.Concat(workplace.EmploymentTerms).Concat(workplace.HealthAndAbsence)
            .Concat(workplace.Processes).Concat(workplace.AcasStates);

    private static string DisplayTime(DateTimeOffset? start, DateTimeOffset? end) =>
        !start.HasValue ? "undated" : start == end ? Format(start) : $"{Format(start)} to {Format(end)}";

    private static string JoinOrNone(IEnumerable<string> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? "none" : string.Join(", ", array);
    }

    private static string FormatOffsets(ExportSourceItem item) =>
        item.PageNumber.HasValue || item.TextStart.HasValue || item.TextEnd.HasValue
            ? $"page={item.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "n/a"};text={item.TextStart?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}-{item.TextEnd?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}"
            : "not recorded";

    private static string FormatBoundingBox(ExportSourceItem item) =>
        item.BoundingBoxLeft.HasValue
            ? $"{item.BoundingBoxLeft},{item.BoundingBoxTop},{item.BoundingBoxWidth},{item.BoundingBoxHeight}"
            : "not recorded";

    private static byte[] CreateBundle(IReadOnlyCollection<GeneratedProfessionalExportArtifact> artifacts)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
        {
            foreach (var artifact in artifacts.OrderBy(item => item.FileName, StringComparer.Ordinal))
            {
                ValidateFileName(artifact.FileName);
                var entry = archive.CreateEntry(artifact.FileName, CompressionLevel.NoCompression);
                entry.LastWriteTime = StableArchiveTimestamp;
                using var stream = entry.Open();
                stream.Write(artifact.ContentSpan);
            }
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = StableArchiveTimestamp;
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart());
    }

    internal static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName != Path.GetFileName(fileName) || fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException("Generated export filenames must be safe single-segment identifiers.");
        }
    }

    internal static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value));

    internal static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private sealed class BoundedWriteStream(long maximumBytes) : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException("The write range is outside the supplied buffer.");
            }
            Add(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer.Length);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Add(int count)
        {
            if (_length > maximumBytes - count)
            {
                throw new InputSizeLimitExceededException();
            }
            _length += count;
        }
    }

    private sealed class InputSizeLimitExceededException : Exception;
}
