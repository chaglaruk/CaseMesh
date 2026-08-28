using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseMesh.Api;
using CaseMesh.Core.Models;
using CaseMesh.Live;
using CaseMesh.MatterBrain;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresUploadedMeetingReviewTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Uploaded_review_is_persisted_private_current_source_gated_and_cross_tenant_inaccessible()
    {
        using var factory = new PostgresApiFactory(database.AppConnectionString);
        using var alice = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = false });
        using var bob = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = false });
        await SignInAsync(alice, $"review-alice-{Guid.NewGuid():N}");
        await SignInAsync(bob, $"review-bob-{Guid.NewGuid():N}");
        var aliceCsrf = await AttachAntiforgeryAsync(alice);
        var bobCsrf = await AttachAntiforgeryAsync(bob);
        var tenantA = await CreateWorkspaceAsync(alice, aliceCsrf, "Synthetic Review workspace A");
        var tenantB = await CreateWorkspaceAsync(bob, bobCsrf, "Synthetic Review workspace B");
        var matterA = Guid.NewGuid();
        var siblingMatter = Guid.NewGuid();

        var persisted = SyntheticPersistedMatterFactory.Create(new TenantId(tenantA), matterA, 940);
        var siblingPersisted = SyntheticPersistedMatterFactory.Create(new TenantId(tenantA), siblingMatter, 941);
        await using (var brainStore = new PostgresMatterBrainStore(database.AppConnectionString))
        {
            await brainStore.SaveAsync(new MatterBrainState(persisted.Evidence), persisted.Workplace);
            await brainStore.SaveAsync(new MatterBrainState(siblingPersisted.Evidence), siblingPersisted.Workplace);
        }
        var canonicalContext = new CanonicalLiveContextAdapter().Build(
            new TenantId(tenantA), matterA, new MatterBrainState(persisted.Evidence));
        var currentSourceId = canonicalContext.Evidence
            .First(item => item.RecordStatus == LiveEvidenceRecordStatus.Current)
            .SourceSpanId;
        var historicalSourceId = canonicalContext.Evidence
            .FirstOrDefault(item => item.RecordStatus == LiveEvidenceRecordStatus.Historical)
            ?.SourceSpanId;
        var startedAt = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
        var itemId = Guid.NewGuid();

        using var create = await SendJsonAsync(alice, aliceCsrf, HttpMethod.Post,
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions",
            new
            {
                items = new[]
                {
                    new
                    {
                        id = itemId,
                        origin = (int)LiveConversationOrigin.HrSaid,
                        text = "Synthetic HR meeting statement.",
                        startedAt,
                        endedAt = startedAt.AddSeconds(4),
                        contextCitationSourceSpanIds = new[] { currentSourceId }
                    },
                    new
                    {
                        id = Guid.NewGuid(),
                        origin = (int)LiveConversationOrigin.UserActuallySaid,
                        text = "Synthetic user response.",
                        startedAt = startedAt.AddSeconds(5),
                        endedAt = startedAt.AddSeconds(8),
                        contextCitationSourceSpanIds = Array.Empty<Guid>()
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        AssertPrivateNoStore(create);
        using var createdJson = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var meetingId = createdJson.RootElement.GetProperty("review").GetProperty("meetingId").GetGuid();
        Assert.NotEqual(Guid.Empty, meetingId);
        Assert.Equal("Synthetic HR meeting statement.",
            createdJson.RootElement.GetProperty("review").GetProperty("items")[0].GetProperty("text").GetString());

        using var own = await alice.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions/{meetingId:D}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        AssertPrivateNoStore(own);
        using (var ownJson = JsonDocument.Parse(await own.Content.ReadAsStringAsync()))
        {
            var items = ownJson.RootElement.GetProperty("review").GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            Assert.Equal((int)LiveConversationOrigin.HrSaid, items[0].GetProperty("origin").GetInt32());
            Assert.Equal(currentSourceId,
                items[0].GetProperty("contextCitationSourceSpanIds")[0].GetGuid());
        }

        using var list = await alice.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        AssertPrivateNoStore(list);
        using (var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, listJson.RootElement.GetArrayLength());
            Assert.Equal(meetingId, listJson.RootElement[0].GetProperty("meetingId").GetGuid());
            Assert.Equal(2, listJson.RootElement[0].GetProperty("itemCount").GetInt32());
        }

        using var siblingRead = await alice.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{siblingMatter:D}/review/sessions/{meetingId:D}");
        await AssertPrivateEmptyNotFoundAsync(siblingRead);
        using var siblingList = await alice.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{siblingMatter:D}/review/sessions");
        Assert.Equal(HttpStatusCode.OK, siblingList.StatusCode);
        AssertPrivateNoStore(siblingList);
        using (var siblingListJson = JsonDocument.Parse(await siblingList.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, siblingListJson.RootElement.GetArrayLength());
        }

        using var nullItem = await SendJsonAsync(alice, aliceCsrf, HttpMethod.Post,
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions",
            new { items = new object?[] { null } });
        Assert.Equal(HttpStatusCode.BadRequest, nullItem.StatusCode);
        AssertNoStore(nullItem);

        using var cross = await bob.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions/{meetingId:D}");
        using var ownMissing = await bob.GetAsync(
            $"/api/workspaces/{tenantB:D}/matters/{Guid.NewGuid():D}/review/sessions/{meetingId:D}");
        await AssertPrivateEmptyNotFoundAsync(cross);
        await AssertPrivateEmptyNotFoundAsync(ownMissing);
        Assert.Equal(await ownMissing.Content.ReadAsByteArrayAsync(), await cross.Content.ReadAsByteArrayAsync());
        Assert.Equal(ownMissing.Content.Headers.ContentType, cross.Content.Headers.ContentType);

        using var crossCreate = await SendJsonAsync(bob, bobCsrf, HttpMethod.Post,
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions",
            new
            {
                items = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        origin = (int)LiveConversationOrigin.HrSaid,
                        text = "Cross tenant transcript must not persist.",
                        startedAt,
                        endedAt = startedAt.AddSeconds(1),
                        contextCitationSourceSpanIds = Array.Empty<Guid>()
                    }
                }
            });
        await AssertPrivateEmptyNotFoundAsync(crossCreate);

        if (historicalSourceId.HasValue)
        {
            using var historicalCitation = await SendJsonAsync(alice, aliceCsrf, HttpMethod.Post,
                $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions",
                new
                {
                    items = new[]
                    {
                        new
                        {
                            id = Guid.NewGuid(),
                            origin = (int)LiveConversationOrigin.HrSaid,
                            text = "Historical context must not be newly attached.",
                            startedAt,
                            endedAt = startedAt.AddSeconds(1),
                            contextCitationSourceSpanIds = new[] { historicalSourceId.Value }
                        }
                    }
                });
            Assert.Equal(HttpStatusCode.BadRequest, historicalCitation.StatusCode);
            AssertNoStore(historicalCitation);
        }

        await SetReviewLimitsAsync(tenantA, matterSessionLimit: 1, matterBytesLimit: 16_777_216);
        using (var sessionQuota = await SendJsonAsync(alice, aliceCsrf, HttpMethod.Post,
                   $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions",
                   SingleItemReview(startedAt, "Session quota must reject this Review.")))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, sessionQuota.StatusCode);
            AssertNoStore(sessionQuota);
            using var problem = JsonDocument.Parse(await sessionQuota.Content.ReadAsStringAsync());
            Assert.Equal("matter-review-session-limit", problem.RootElement.GetProperty("code").GetString());
        }

        await SetReviewLimitsAsync(tenantA, matterSessionLimit: 100, matterBytesLimit: 1);
        using (var byteQuota = await SendJsonAsync(alice, aliceCsrf, HttpMethod.Post,
                   $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sessions",
                   SingleItemReview(startedAt, "Byte quota must reject this Review.")))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, byteQuota.StatusCode);
            AssertNoStore(byteQuota);
            using var problem = JsonDocument.Parse(await byteQuota.Content.ReadAsStringAsync());
            Assert.Equal("matter-review-bytes-limit", problem.RootElement.GetProperty("code").GetString());
        }

        var removedSourceId = Guid.NewGuid();
        var sourceTemplate = canonicalContext.SourceSpans.First();
        await using (var admin = new NpgsqlConnection(database.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO casemesh.source_spans
                    (tenant_id,matter_id,source_span_id,document_version_id,page_number,
                     extracted_text,extracted_text_digest,parser_version)
                VALUES ($1,$2,$3,$4,99,$5,$6,$7);
                INSERT INTO casemesh.uploaded_meeting_review_context_citations
                    (tenant_id,matter_id,meeting_id,item_id,source_span_id,ordinal)
                VALUES ($1,$2,$8,$9,$3,1);
                DELETE FROM casemesh.source_spans
                WHERE tenant_id=$1 AND matter_id=$2 AND source_span_id=$3;
                """, admin);
            PostgresMatterStore.AddParameters(command,
                tenantA,
                matterA,
                removedSourceId,
                sourceTemplate.DocumentVersionId,
                "Synthetic source removed after Review attachment.",
                new string('E', 64),
                "synthetic-parser/1",
                meetingId,
                itemId);
            await command.ExecuteNonQueryAsync();
        }

        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        var reviews = new PostgresUploadedMeetingReviewRepository(matterStore);
        var reopened = await reviews.LoadAsync(new TenantId(tenantA), matterA, meetingId);
        Assert.NotNull(reopened);
        Assert.Contains(removedSourceId,
            reopened.Review.Items.Single(item => item.Id == itemId).ContextCitationSourceSpanIds);
        var reopenedAnalysis = new UploadedMeetingReviewAnalyzer().Analyze(reopened.Review, canonicalContext);
        Assert.Contains(reopenedAnalysis.ContextReferences,
            reference => reference.SourceSpanId == removedSourceId &&
                         reference.Status == UploadedMeetingContextReferenceStatus.Missing);

        Assert.True(await matterStore.DeleteMatterAsync(new TenantId(tenantA), matterA));
        Assert.Null(await reviews.LoadAsync(new TenantId(tenantA), matterA, meetingId));
    }

    private async Task SetReviewLimitsAsync(Guid tenantId, int matterSessionLimit, long matterBytesLimit)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE casemesh.pilot_entitlements
            SET matter_review_session_limit=$2,
                matter_review_bytes_limit=$3
            WHERE tenant_id=$1;
            """, connection);
        PostgresMatterStore.AddParameters(command, tenantId, matterSessionLimit, matterBytesLimit);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static object SingleItemReview(DateTimeOffset startedAt, string text) => new
    {
        items = new[]
        {
            new
            {
                id = Guid.NewGuid(),
                origin = (int)LiveConversationOrigin.HrSaid,
                text,
                startedAt,
                endedAt = startedAt.AddSeconds(1),
                contextCitationSourceSpanIds = Array.Empty<Guid>()
            }
        }
    };

    private static async Task SignInAsync(HttpClient client, string subject)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/test-sign-in",
            new TestSignInRequest(subject, "Synthetic Review API User"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0]);
    }

    private static async Task<string> AttachAntiforgeryAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csrfCookie = response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
        var sessionCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        return (await response.Content.ReadFromJsonAsync<CsrfResponse>())!.Token;
    }

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client, string csrf, string name)
    {
        using var response = await SendJsonAsync(client, csrf, HttpMethod.Post, "/api/workspaces", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkspaceResponse>())!.TenantId;
    }

    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        string csrf,
        HttpMethod method,
        string path,
        object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return client.SendAsync(request);
    }

    private static async Task AssertPrivateEmptyNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        AssertPrivateNoStore(response);
    }

    private static void AssertPrivateNoStore(HttpResponseMessage response)
    {
        AssertNoStore(response);
        Assert.True(response.Headers.CacheControl!.Private);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Contains(response.Headers.Pragma,
            value => string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CsrfResponse(string Token);
    private sealed record WorkspaceResponse(Guid TenantId);
}