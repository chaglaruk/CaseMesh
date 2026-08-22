using System.Net;
using System.Net.Http.Json;
using CaseMesh.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresApiIsolationTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Invalid_or_missing_resources_do_not_consume_daily_QA_or_export_allowances()
    {
        using var factory = new PostgresApiFactory(database.AppConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = false });
        await SignInAsync(client, $"quota-{Guid.NewGuid():N}");
        var csrf = await AttachAntiforgeryAsync(client);
        var tenant = await CreateWorkspaceAsync(client, csrf, "Synthetic quota workspace");
        var matter = await CreateMatterAsync(client, csrf, tenant, "Synthetic quota Matter");

        using (var invalidQuestion = await SendJsonAsync(client, csrf, HttpMethod.Post,
                   $"/api/workspaces/{tenant:D}/matters/{matter:D}/questions/ask", new { question = " " }))
            Assert.Equal(HttpStatusCode.BadRequest, invalidQuestion.StatusCode);
        using (var missingExport = await SendJsonAsync(client, csrf, HttpMethod.Post,
                   $"/api/workspaces/{tenant:D}/matters/{Guid.NewGuid():D}/exports", new { }))
            Assert.Equal(HttpStatusCode.NotFound, missingExport.StatusCode);

        using var usageResponse = await client.GetAsync($"/api/workspaces/{tenant:D}/matters/{matter:D}/usage");
        Assert.Equal(HttpStatusCode.OK, usageResponse.StatusCode);
        var usage = await usageResponse.Content.ReadFromJsonAsync<UsageResponse>();
        Assert.NotNull(usage);
        Assert.Equal(0, usage.QaRequestsToday);
        Assert.Equal(0, usage.ExportsToday);
    }

    [PostgresFact]
    public async Task Tenant_B_receives_indistinguishable_not_found_responses_for_all_Tenant_A_routes()
    {
        using var factory = new PostgresApiFactory(database.AppConnectionString);
        using var alice = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = false });
        using var bob = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, HandleCookies = false });
        await SignInAsync(alice, $"alice-{Guid.NewGuid():N}");
        await SignInAsync(bob, $"bob-{Guid.NewGuid():N}");
        var aliceCsrf = await AttachAntiforgeryAsync(alice);
        var bobCsrf = await AttachAntiforgeryAsync(bob);
        var tenantA = await CreateWorkspaceAsync(alice, aliceCsrf, "Synthetic workspace A");
        var tenantB = await CreateWorkspaceAsync(bob, bobCsrf, "Synthetic workspace B");
        var matterA = await CreateMatterAsync(alice, aliceCsrf, tenantA, "Synthetic Matter A");

        using var crossMatter = await bob.GetAsync($"/api/workspaces/{tenantA}/matters/{matterA}");
        using var ownMissing = await bob.GetAsync($"/api/workspaces/{tenantB}/matters/{Guid.NewGuid():D}");
        await AssertSameEmptyNotFoundAsync(crossMatter, ownMissing);

        foreach (var projection in new[] { "overview", "timeline", "evidence", "people", "disputed", "workplace", "questions" })
        {
            using var ownResponse = await alice.GetAsync($"/api/workspaces/{tenantA}/matters/{matterA}/{projection}");
            Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
            using var response = await bob.GetAsync($"/api/workspaces/{tenantA}/matters/{matterA}/{projection}");
            await AssertEmptyNotFoundAsync(response);
        }

        using (var ownQuestion = await SendJsonAsync(alice, aliceCsrf, HttpMethod.Post,
                   $"/api/workspaces/{tenantA}/matters/{matterA}/questions/ask",
                   new { question = "What evidence is present?" }))
            Assert.Equal(HttpStatusCode.OK, ownQuestion.StatusCode);

        using (var crossQuestion = await SendJsonAsync(bob, bobCsrf, HttpMethod.Post,
                   $"/api/workspaces/{tenantA}/matters/{matterA}/questions/ask",
                   new { question = "What evidence is present?" }))
            await AssertEmptyNotFoundAsync(crossQuestion);

        using (var response = await bob.GetAsync(
                   $"/api/workspaces/{tenantA}/matters/{matterA}/jobs/{Guid.NewGuid():D}"))
            await AssertEmptyNotFoundAsync(response);

        using (var upload = new MultipartFormDataContent())
        {
            upload.Add(new ByteArrayContent("synthetic cross-tenant evidence"u8.ToArray()),
                "file", "synthetic.txt");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/workspaces/{tenantA}/matters/{matterA}/evidence")
            { Content = upload };
            request.Headers.Add("X-CSRF-TOKEN", bobCsrf);
            using var response = await bob.SendAsync(request);
            await AssertEmptyNotFoundAsync(response);
        }

        using (var response = await SendJsonAsync(bob, bobCsrf, HttpMethod.Post,
                   $"/api/workspaces/{tenantA}/matters/{matterA}/assertions/{Guid.NewGuid():D}/corrections",
                   new { correctedValue = "Synthetic correction" }))
            await AssertEmptyNotFoundAsync(response);

        using (var response = await SendJsonAsync(bob, bobCsrf, HttpMethod.Post,
                   $"/api/workspaces/{tenantA}/matters/{matterA}/exports", new { }))
            await AssertEmptyNotFoundAsync(response);

        var missingExport = Guid.NewGuid();
        using (var ownMissingExport = await alice.GetAsync(
                   $"/api/workspaces/{tenantA}/matters/{matterA}/exports/{missingExport:D}"))
        using (var crossExport = await bob.GetAsync(
                   $"/api/workspaces/{tenantA}/matters/{matterA}/exports/{missingExport:D}"))
            await AssertSameEmptyNotFoundAsync(ownMissingExport, crossExport);

        using (var response = await bob.GetAsync(
                   $"/api/workspaces/{tenantA}/matters/{matterA}/usage"))
            await AssertEmptyNotFoundAsync(response);

        using (var response = await bob.GetAsync(
                   $"/api/workspaces/{tenantA}/matters/{matterA}/deletions/{Guid.NewGuid():D}"))
            await AssertEmptyNotFoundAsync(response);
    }

    private static async Task SignInAsync(HttpClient client, string subject)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/test-sign-in",
            new TestSignInRequest(subject, "Synthetic API User"));
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

    private static Task<HttpResponseMessage> SendJsonAsync(HttpClient client, string csrf, HttpMethod method,
        string path, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return client.SendAsync(request);
    }

    private static async Task AssertSameEmptyNotFoundAsync(HttpResponseMessage first, HttpResponseMessage second)
    {
        await AssertEmptyNotFoundAsync(first);
        await AssertEmptyNotFoundAsync(second);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Content.Headers.ContentType, second.Content.Headers.ContentType);
    }

    private static async Task AssertEmptyNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private sealed record CsrfResponse(string Token);
    private sealed record WorkspaceResponse(Guid TenantId);
    private sealed record MatterResponse(Guid Id);
    private sealed record UsageResponse(long QaRequestsToday, long ExportsToday);
}

internal sealed class PostgresApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("CaseMesh:PostgresConnectionString", connectionString);
        builder.UseSetting("CaseMesh:EnableTestAuthentication", "true");
    }
}
