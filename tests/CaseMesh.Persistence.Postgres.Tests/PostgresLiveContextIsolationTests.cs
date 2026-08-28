using System.Net;
using System.Net.Http.Json;
using CaseMesh.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresLiveContextIsolationTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Review_context_is_tenant_scoped_and_cross_tenant_requests_fail_closed()
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
        var matterA = await CreateMatterAsync(alice, aliceCsrf, tenantA, "Synthetic Live Matter A");

        using var own = await alice.GetAsync($"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/context");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        using var cross = await bob.GetAsync($"/api/workspaces/{tenantA:D}/matters/{matterA:D}/review/context");
        using var ownMissing = await bob.GetAsync($"/api/workspaces/{tenantB:D}/matters/{Guid.NewGuid():D}/review/context");
        await AssertEmptyNotFoundAsync(cross);
        await AssertEmptyNotFoundAsync(ownMissing);
        Assert.Equal(await ownMissing.Content.ReadAsByteArrayAsync(), await cross.Content.ReadAsByteArrayAsync());
        Assert.Equal(ownMissing.Content.Headers.ContentType, cross.Content.Headers.ContentType);
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

    private static async Task<Guid> CreateMatterAsync(HttpClient client, string csrf, Guid tenantId, string title)
    {
        using var response = await SendJsonAsync(client, csrf, HttpMethod.Post,
            $"/api/workspaces/{tenantId:D}/matters", new { title, jurisdiction = "Synthetic jurisdiction" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MatterResponse>())!.Id;
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

    private static async Task AssertEmptyNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private sealed record CsrfResponse(string Token);
    private sealed record WorkspaceResponse(Guid TenantId);
    private sealed record MatterResponse(Guid Id);
}
