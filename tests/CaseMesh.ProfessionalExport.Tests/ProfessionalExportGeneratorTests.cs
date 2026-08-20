using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.ProfessionalExport;
using DocumentFormat.OpenXml.Packaging;
using static CaseMesh.ProfessionalExport.Tests.SyntheticProfessionalExportFixture;

namespace CaseMesh.ProfessionalExport.Tests;

public sealed class ProfessionalExportGeneratorTests
{
    [Fact]
    public async Task Bundle_contains_every_required_format_with_generated_safe_names()
    {
        var package = await GenerateAsync();

        Assert.Equal(8, package.Artifacts.Count);
        Assert.Contains(package.Artifacts, item => item.Kind == ProfessionalExportArtifactKind.BriefDocx);
        Assert.Contains(package.Artifacts, item => item.Kind == ProfessionalExportArtifactKind.MatterManifestJson);
        Assert.Contains(package.Artifacts, item => item.Kind == ProfessionalExportArtifactKind.BundleZip);
        Assert.Equal(4, package.Artifacts.Count(item => item.FileName.EndsWith(".csv", StringComparison.Ordinal)));
        Assert.All(package.Artifacts, item =>
        {
            Assert.Contains(MatterId.ToString("N"), item.FileName);
            Assert.Contains(ExportId.ToString("N"), item.FileName);
            Assert.DoesNotContain("Alex", item.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Employer", item.FileName, StringComparison.OrdinalIgnoreCase);
            ProfessionalExportGenerator.ValidateFileName(item.FileName);
        });
    }

    [Fact]
    public async Task Every_documentary_citation_resolves_through_span_version_original_and_hash()
    {
        var package = await GenerateAsync();
        var sources = package.Manifest.Sources.ToDictionary(item => item.Reference);
        var documents = package.Manifest.Documents.ToDictionary(item => item.Reference);

        foreach (var assertion in package.Manifest.Assertions.Where(item => item.SourceReference is not null))
        {
            var source = sources[assertion.SourceReference!];
            var document = documents[source.DocumentReference];
            Assert.Equal(source.DocumentVersionId, document.DocumentVersionId);
            Assert.NotEqual(Guid.Empty, document.OriginalObjectId);
            Assert.Matches("^[0-9A-F]{64}$", document.ContentSha256);
        }
    }

    [Fact]
    public async Task Missing_or_divergent_document_provenance_is_rejected_before_export()
    {
        var input = await CreateAsync();
        var incomplete = input with { Documents = input.Documents.Skip(1).ToArray() };
        var divergent = input with
        {
            Documents = input.Documents.Select((item, index) => index == 0
                ? item with { ContentSha256 = new string('F', 64) }
                : item).ToArray()
        };
        var generator = Generator();

        Assert.Throws<InvalidOperationException>(() => generator.Generate(Request(), incomplete));
        Assert.Throws<InvalidOperationException>(() => generator.Generate(Request(), divergent));
    }

    [Fact]
    public async Task Oversized_canonical_snapshot_is_rejected_before_artifact_allocation()
    {
        var input = await CreateAsync();
        var oversized = input with
        {
            Documents = input.Documents.Select((item, index) => index == 0
                ? item with { ParserVersion = new string('x', 32 * 1024 * 1024) }
                : item).ToArray()
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Generator().Generate(Request(), oversized));
        Assert.Contains("input is too large", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Employer_employee_third_party_and_AI_attribution_remain_distinct()
    {
        var assertions = (await GenerateAsync()).Manifest.Assertions;

        Assert.Contains(assertions, item => item.OriginLabel == "EmployerAuthoredDocument/EmployerAssertion");
        Assert.Contains(assertions, item => item.OriginLabel == "EmployeeAuthoredDocument/UserAssertion");
        Assert.Contains(assertions, item => item.OriginLabel == "IndependentThirdPartyRecord/ThirdPartyAssertion");
        Assert.Contains(assertions, item => item.OriginLabel == "AiGeneratedInference/AiInference" && item.SourceReference is null);
    }

    [Fact]
    public async Task Chronology_is_deterministic_and_undated_entries_sort_last()
    {
        var first = await GenerateAsync();
        var second = await GenerateAsync();

        Assert.Equal(
            JsonSerializer.Serialize(first.Manifest.Chronology),
            JsonSerializer.Serialize(second.Manifest.Chronology));
        Assert.Null(first.Manifest.Chronology.Last().StartTime);
        Assert.Contains(first.Manifest.Chronology, item =>
            item.Kind == "Event" && item.StartTime.HasValue && item.EndTime > item.StartTime);
        Assert.Equal(Enumerable.Range(1, first.Manifest.Chronology.Count).Select(index => $"CHR-{index:D5}"),
            first.Manifest.Chronology.Select(item => item.Reference));
    }

    [Fact]
    public async Task Conflicting_dates_and_employment_terms_remain_visible()
    {
        var manifest = (await GenerateAsync()).Manifest;

        Assert.Contains(manifest.Assertions, item => item.Predicate == "meeting-date" && item.Value == "2026-03-12");
        Assert.Contains(manifest.Assertions, item => item.Predicate == "meeting-date" && item.Value == "2026-03-13");
        Assert.Equal(2, manifest.Workplace.EmploymentTerms.Count(item => item.Kind == "WorkingHours"));
        Assert.Contains(manifest.Contradictions, item => item.Type == ContradictionType.DirectConflict);
    }

    [Fact]
    public async Task Superseded_event_history_names_the_replacement_and_keeps_sources()
    {
        var manifest = (await GenerateAsync()).Manifest;
        var history = Assert.Single(manifest.SupersededHistory, item => item.Kind == "Event");

        Assert.Equal("Superseded", history.HistoricalStatus);
        Assert.StartsWith("EVT-", history.ReplacementReference, StringComparison.Ordinal);
        Assert.NotEmpty(history.SourceReferences);
        Assert.Contains(manifest.Chronology, item => item.CanonicalId == history.HistoricalId && item.Status.Contains("Superseded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Twelve_vs_ten_contradiction_exports_both_sides_and_sources()
    {
        var manifest = (await GenerateAsync()).Manifest;
        var contradiction = manifest.Contradictions.Single(item => item.Type == ContradictionType.NumericMismatch);
        var sides = new[]
        {
            manifest.Assertions.Single(item => item.Reference == contradiction.AssertionAReference),
            manifest.Assertions.Single(item => item.Reference == contradiction.AssertionBReference)
        };

        Assert.Equal(["10", "12"], sides.Select(item => item.Value).Order(StringComparer.Ordinal));
        Assert.Equal(2, contradiction.SourceReferences.Count);
        Assert.Equal(ContradictionResolutionState.Unresolved, contradiction.ResolutionState);
    }

    [Fact]
    public async Task Adjustment_request_response_and_implementation_are_separate_reference_sets()
    {
        var item = (await GenerateAsync()).Manifest.Workplace.AdjustmentRequests
            .Single(adjustment => adjustment.NeutralLabel == "Adjusted hours");

        Assert.Single(item.RequestAssertionReferences);
        Assert.Single(item.ResponseAssertionReferences);
        Assert.Single(item.ImplementationAssertionReferences);
        Assert.Empty(item.RequestAssertionReferences.Intersect(item.ResponseAssertionReferences));
        Assert.Empty(item.RequestAssertionReferences.Intersect(item.ImplementationAssertionReferences));
        Assert.Empty(item.ResponseAssertionReferences.Intersect(item.ImplementationAssertionReferences));
    }

    [Fact]
    public async Task OH_recommendation_remains_distinct_from_employer_action()
    {
        var assertions = (await GenerateAsync()).Manifest.Assertions;
        var oh = assertions.Single(item => item.Predicate == "oh-recommendation");
        var action = assertions.Single(item => item.Predicate == "employer-action");

        Assert.Equal("IndependentThirdPartyRecord/ThirdPartyAssertion", oh.OriginLabel);
        Assert.Equal("EmployerAuthoredDocument/EmployerAssertion", action.OriginLabel);
        Assert.NotEqual(oh.Reference, action.Reference);
        Assert.NotEqual(oh.SourceReference, action.SourceReference);
    }

    [Fact]
    public async Task Evidence_indexes_and_manifests_never_expose_storage_locators_or_credentials()
    {
        var package = await GenerateAsync();
        var combined = string.Join('\n', package.Artifacts
            .Where(item => item.Kind is not (ProfessionalExportArtifactKind.BriefDocx or ProfessionalExportArtifactKind.BundleZip))
            .Select(item => Encoding.UTF8.GetString(item.Content)));

        Assert.DoesNotContain("bucket", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object_key", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_key", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("presigned", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_is_openable_and_contains_all_required_structured_sections()
    {
        var artifact = (await GenerateAsync()).Artifacts.Single(item => item.Kind == ProfessionalExportArtifactKind.BriefDocx);
        using var stream = new MemoryStream(artifact.Content);
        using var document = WordprocessingDocument.Open(stream, false);
        var mainDocumentPart = Assert.IsType<MainDocumentPart>(document.MainDocumentPart);
        var wordDocument = Assert.IsType<DocumentFormat.OpenXml.Wordprocessing.Document>(mainDocumentPart.Document);
        var text = Assert.IsType<DocumentFormat.OpenXml.Wordprocessing.Body>(wordDocument.Body).InnerText;

        Assert.Contains("Matter header", text);
        Assert.Contains("Source-linked chronology", text);
        Assert.Contains("Evidence and document index", text);
        Assert.Contains("Attributed assertions by topic", text);
        Assert.Contains("Contradictions and disputed records", text);
        Assert.Contains("Open factual questions and missing evidence", text);
        Assert.Contains("Workplace-specific neutral context", text);
        Assert.Contains("Provenance and generation metadata", text);
    }

    [Fact]
    public async Task Csv_and_JSON_artifacts_are_deterministic_and_machine_readable()
    {
        var first = await GenerateAsync();
        var second = await GenerateAsync();
        var json = first.Artifacts.Single(item => item.Kind == ProfessionalExportArtifactKind.MatterManifestJson);
        using var parsed = JsonDocument.Parse(json.Content);

        Assert.Equal(ProfessionalExportGenerator.CurrentSchemaVersion,
            parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.All(first.Artifacts.Where(item => item.FileName.EndsWith(".csv", StringComparison.Ordinal)), artifact =>
        {
            var lines = Encoding.UTF8.GetString(artifact.Content).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Length >= 1);
            Assert.Contains(',', lines[0]);
        });
        Assert.Equal(first.Artifacts.Select(item => item.Sha256), second.Artifacts.Select(item => item.Sha256));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://invalid.example\")")]
    [InlineData("+SUM(1,1)")]
    [InlineData("-1+2")]
    [InlineData("@SUM(1,1)")]
    [InlineData("\t=1+1")]
    public void Csv_cells_cannot_become_spreadsheet_formulas(string untrustedValue)
    {
        var cell = ProfessionalExportGenerator.EscapeCsv(untrustedValue);
        var unquoted = cell.Length >= 2 && cell[0] == '"' && cell[^1] == '"'
            ? cell[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : cell;

        Assert.StartsWith("'", unquoted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bundle_paths_are_single_segment_and_cannot_escape_root()
    {
        var bundle = (await GenerateAsync()).Artifacts.Single(item => item.Kind == ProfessionalExportArtifactKind.BundleZip);
        using var archive = new ZipArchive(new MemoryStream(bundle.Content), ZipArchiveMode.Read);

        Assert.Equal(7, archive.Entries.Count);
        Assert.All(archive.Entries, entry =>
        {
            Assert.Equal(entry.Name, entry.FullName);
            Assert.DoesNotContain("..", entry.FullName, StringComparison.Ordinal);
            Assert.DoesNotContain('/', entry.FullName);
            Assert.DoesNotContain('\\', entry.FullName);
        });
        Assert.Throws<InvalidOperationException>(() => ProfessionalExportGenerator.ValidateFileName("../escape.json"));
        Assert.Throws<InvalidOperationException>(() => ProfessionalExportGenerator.ValidateFileName("folder/file.json"));
    }

    [Fact]
    public async Task Every_artifact_hash_and_length_matches_generated_bytes()
    {
        var package = await GenerateAsync();

        foreach (var artifact in package.Artifacts)
        {
            Assert.Equal(ProfessionalExportGenerator.Sha256(artifact.Content), artifact.Sha256);
            var digest = package.Run.Artifacts.Single(item => item.Kind == artifact.Kind);
            Assert.Equal(artifact.Sha256, digest.Sha256);
            Assert.Equal(artifact.Content.LongLength, digest.ByteLength);
        }
        Assert.Equal(ProfessionalExportGenerator.Sha256(
            JsonSerializer.Serialize(package.Run.Artifacts.OrderBy(item => item.Kind), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            })), package.Run.ArtifactManifestDigest);
    }

    [Fact]
    public async Task Same_snapshot_clock_and_export_identity_produce_byte_identical_artifacts()
    {
        var input = await CreateAsync();
        var generator = Generator();

        var first = generator.Generate(Request(), input);
        var second = generator.Generate(Request(), input);

        Assert.Equal(JsonSerializer.Serialize(first.Run), JsonSerializer.Serialize(second.Run));
        Assert.All(first.Artifacts, artifact =>
            Assert.Equal(artifact.Content, second.Artifacts.Single(item => item.Kind == artifact.Kind).Content));
    }

    [Fact]
    public async Task Unrelated_Matter_creation_cannot_change_existing_Matter_export()
    {
        var input = await CreateAsync();
        var generator = Generator();
        var before = generator.Generate(Request(), input);
        _ = await CreateAsync(701);
        var after = generator.Generate(Request(), input);

        Assert.Equal(before.Run.SnapshotDigest, after.Run.SnapshotDigest);
        Assert.Equal(before.Artifacts.Select(item => item.Sha256), after.Artifacts.Select(item => item.Sha256));
    }

    [Theory]
    [InlineData("The employer has legal liability.")]
    [InlineData("The employee should settle now.")]
    [InlineData("The win probability is high.")]
    [InlineData("This is a compensation estimate.")]
    public void Neutral_brief_guard_rejects_outcome_or_recommendation_language(string text)
    {
        Assert.Throws<InvalidOperationException>(() => ProfessionalExportGenerator.GuardNeutralBrief(text));
        ProfessionalExportGenerator.GuardNeutralBrief("Assertions remain attributed records and open questions remain.");
    }

    [Fact]
    public async Task Open_question_rules_describe_factual_gaps_without_accusation_or_legal_duty()
    {
        var questions = (await GenerateAsync()).Manifest.OpenQuestions;

        Assert.Contains(questions, item => item.Category == "Unresolved conflict");
        Assert.Contains(questions, item => item.Category == "Adjustment response");
        Assert.Contains(questions, item => item.Category == "Adjustment implementation");
        Assert.Contains(questions, item => item.Category == "Event evidence");
        Assert.All(questions, item =>
        {
            Assert.DoesNotContain("conceal", item.NeutralQuestion, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("breach", item.NeutralQuestion, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("duty", item.NeutralQuestion, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("must", item.NeutralQuestion, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Source_less_AI_assertion_is_labelled_and_never_receives_a_fake_citation()
    {
        var assertion = (await GenerateAsync()).Manifest.Assertions
            .Single(item => item.OriginLabel == "AiGeneratedInference/AiInference");

        Assert.Null(assertion.SourceReference);
        Assert.Equal("CaseMesh AI", assertion.AssertedBy);
        Assert.Contains("source-less", (await GenerateAsync()).Manifest.NeutralBrief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logical_duplicate_versions_share_original_identity_without_collapsing_document_references()
    {
        var manifest = (await GenerateAsync()).Manifest;
        var duplicateGroups = manifest.Documents.GroupBy(item => item.OriginalObjectId)
            .Where(group => group.Count() > 1).ToArray();

        var group = Assert.Single(duplicateGroups);
        Assert.All(group, item => Assert.True(item.SharesLogicalOriginal));
        Assert.Equal(group.Count(), group.Select(item => item.Reference).Distinct().Count());
        Assert.Single(group.Select(item => item.ContentSha256).Distinct());
    }

    [Fact]
    public async Task Export_references_are_unique_within_each_reference_domain()
    {
        var manifest = (await GenerateAsync()).Manifest;

        Assert.Equal(manifest.Documents.Count, manifest.Documents.Select(item => item.Reference).Distinct().Count());
        Assert.Equal(manifest.Sources.Count, manifest.Sources.Select(item => item.Reference).Distinct().Count());
        Assert.Equal(manifest.Assertions.Count, manifest.Assertions.Select(item => item.Reference).Distinct().Count());
        Assert.Equal(manifest.Chronology.Count, manifest.Chronology.Select(item => item.Reference).Distinct().Count());
        Assert.Equal(manifest.Contradictions.Count, manifest.Contradictions.Select(item => item.Reference).Distinct().Count());
    }

    private static async Task<ProfessionalExportPackage> GenerateAsync()
    {
        var input = await CreateAsync();
        return Generator().Generate(Request(), input);
    }

    private static ProfessionalExportGenerator Generator() => new(new FixedTimeProvider(RecordedAt.AddDays(1)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
