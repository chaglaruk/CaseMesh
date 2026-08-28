using System.Net;
using System.Net.Http.Json;
using CaseMesh.Api;
using CaseMesh.Core.Models;
using CaseMesh.Live;
using CaseMesh.MatterBrain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresLiveContextIsolationTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Review_context_and_source_detail_are_tenant_scoped_and_fail_closed()
    {
        using var factory = new PostgresApiFactory(database.AppConnectionString);
        using var alice = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = false });
        using var bob = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = false });

        await SignInAsync(alice, $"live-alice-{Guid.NewGuid():N}");
        await SignInAsync(bob, $"live-bob-{Guid.NewGuid():N}");
        var aliceCsrf = await AttachAntiforgeryAsync(alice);
        var bobCsrf = await AttachAntiforgeryAsync(bob);
        var tenantA = await CreateWorkspaceAsync(alice, aliceCsrf, "Synthetic Live workspace A");
        var tenantB = await CreateWorkspaceAsync(bob, bobCsrf, "Synthetic Live workspace B");
        var matterA = Guid.NewGuid();
        var persisted = SyntheticPersistedMatterFactory.Create(new TenantId(tenantA), matterA, 930);
        await using (var brainStore = new PostgresMatterBrainStore(database.AppConnectionString))
        {
            await brainStore.SaveAsync(new MatterBrainState(persisted.Evidence), persisted.Workplace);
        }
        var existingSource = persisted.Evidence.SourceSpans.First();

        using var own = await alice.GetAsync($"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/context");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        AssertPrivateNoStore(own);

        using var cross = await bob.GetAsync($"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/context");
        using var ownMissing = await bob.GetAsync($"/api/workspaces/{tenantB:D}/matters/{Guid.NewGuid():D}/review/context");
        await AssertPrivateEmptyNotFoundAsync(cross);
        await AssertPrivateEmptyNotFoundAsync(ownMissing);
        Assert.Equal(await ownMissing.Content.ReadAsByteArrayAsync(), await cross.Content.ReadAsByteArrayAsync());
        Assert.Equal(ownMissing.Content.Headers.ContentType, cross.Content.Headers.ContentType);

        using var ownExistingSource = await alice.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sources/{existingSource.Id:D}");
        Assert.Equal(HttpStatusCode.OK, ownExistingSource.StatusCode);
        AssertPrivateNoStore(ownExistingSource);
        var sourceDetail = await ownExistingSource.Content.ReadFromJsonAsync<LiveSourceDetail>();
        Assert.NotNull(sourceDetail);
        Assert.Equal(existingSource.Id, sourceDetail.Citation.SourceSpanId);
        Assert.Equal(existingSource.ExtractedText, sourceDetail.ExactText);

        using var crossExistingSource = await bob.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sources/{existingSource.Id:D}");
        await AssertPrivateEmptyNotFoundAsync(crossExistingSource);

        var missingSourceId = Guid.NewGuid();
        using var ownMissingSource = await alice.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sources/{missingSourceId:D}");
        using var crossMissingSource = await bob.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sources/{missingSourceId:D}");
        await AssertPrivateEmptyNotFoundAsync(ownMissingSource);
        await AssertPrivateEmptyNotFoundAsync(crossMissingSource);
        Assert.Equal(
            await ownMissingSource.Content.ReadAsByteArrayAsync(),
            await crossMissingSource.Content.ReadAsByteArrayAsync());
        Assert.Equal(ownMissingSource.Content.Headers.ContentType, crossMissingSource.Content.Headers.ContentType);

        using var zeroTenantContext = await alice.GetAsync(
            $"/api/workspaces/{Guid.Empty:D}/matters/{matterA:D}/review/context");
        using var zeroMatterContext = await alice.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{Guid.Empty:D}/review/context");
        using var zeroSource = await alice.GetAsync(
            $"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/sources/{Guid.Empty:D}");
        await AssertPrivateEmptyNotFoundAsync(zeroTenantContext);
        await AssertPrivateEmptyNotFoundAsync(zeroMatterContext);
        await AssertPrivateEmptyNotFoundAsync(zeroSource);
    }

    private static async Task SignInAsync(HttpClient client, string subject)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/test-sign-in",
            new TestSignInRequest(subject, "Synthetic Live API User"));
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
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.True(response.Headers.CacheControl.Private);
        Assert.Contains(response.Headers.Pragma,
            value => string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CsrfResponse(string Token);
    private sealed record WorkspaceResponse(Guid TenantId);
}