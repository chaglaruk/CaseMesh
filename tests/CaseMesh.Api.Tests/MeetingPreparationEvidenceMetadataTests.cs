using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api.Tests;

public sealed class MeetingPreparationEvidenceMetadataTests
{
    [Fact]
    public void Preparation_preserves_assertion_metadata_and_event_end_time()
    {
        var assertedAt = new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero);
        var eventStart = assertedAt.AddDays(2);
        var eventEnd = eventStart.AddHours(1);
        var graph = new MatterEvidenceGraph(new Matter(Guid.NewGuid(), new TenantId(Guid.NewGuid()),
            "workplace-dispute", "Synthetic metadata Matter", "active", assertedAt, assertedAt));
        var version = graph.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(), new string('F', 64), Guid.NewGuid());
        const string text = "Synthetic invitation records a meeting from 10:00 until 11:00.";
        var span = graph.AddSourceSpan(Guid.NewGuid(), version, text, "synthetic-parser/1", 0.97m,
            pageNumber: 1, textStart: 0, textEnd: text.Length);
        var assertion = graph.AddAssertion(
            Guid.NewGuid(), "synthetic-manager", "meeting-time", "10:00-11:00", "Synthetic invitation", assertedAt,
            EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.AttributedAssertion,
            DisputeState.Unverified, IntegrityState.OriginalHashVerified, VerificationState.NotReviewed,
            span.Id, eventStart, 0.93m);
        var matterEvent = graph.AddEvent(Guid.NewGuid(), "meeting", "Synthetic review meeting",
            EventStatus.Candidate, VerificationState.NotReviewed, eventStart, eventEnd);
        graph.AddAssertionEventLink(Guid.NewGuid(), assertion.Id, matterEvent.Id, AssertionEventRelation.Supports);
        var loaded = new PersistedMatterBrain(graph, new WorkplaceMatter(graph), new MatterBrainState(graph));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(MeetingPreparationProjection.Create(loaded, false)));
        var point = Assert.Single(json.RootElement.GetProperty("evidencePoints").EnumerateArray());
        var chronology = Assert.Single(json.RootElement.GetProperty("chronology").EnumerateArray());

        Assert.Equal(assertedAt, point.GetProperty("AssertedAt").GetDateTimeOffset());
        Assert.Equal("OriginalHashVerified", point.GetProperty("integrity").GetString());
        Assert.Equal(0.93m, point.GetProperty("ExtractionConfidence").GetDecimal());
        Assert.Equal(eventStart, chronology.GetProperty("StartTime").GetDateTimeOffset());
        Assert.Equal(eventEnd, chronology.GetProperty("EndTime").GetDateTimeOffset());
        Assert.Contains(span.Id, chronology.GetProperty("sourceSpanIds").EnumerateArray().Select(item => item.GetGuid()));
    }
}
