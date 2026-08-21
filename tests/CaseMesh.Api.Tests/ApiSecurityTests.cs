using System.Net;
using System.Net.Http.Json;
using CaseMesh.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CaseMesh.Api.Tests;

public sealed class ApiSecurityTests : IClassFixture<SyntheticApiFactory>
{
    private readonly SyntheticApiFactory _factory;
    public ApiSecurityTests(SyntheticApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_is_platform_neutral_and_security_hardened()
    {
        using var response = await _factory.CreateClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Protected_route_is_unauthorized_without_cookie()
    {
        using var response = await _factory.CreateClient().GetAsync("/api/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Test_sign_in_issues_http_only_server_cookie()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.PostAsJsonAsync("/api/auth/test-sign-in",
            new TestSignInRequest("synthetic-user", "Synthetic User"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("__Host-casemesh-session", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_http_test_harness_can_issue_antiforgery_token_without_weakening_session_cookie()
    {
        using var client = _factory.CreateClient();
        using var signIn = await client.PostAsJsonAsync("/api/auth/test-sign-in",
            new TestSignInRequest("synthetic-csrf-user", "Synthetic CSRF User"));
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        var sessionCookie = signIn.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
        client.DefaultRequestHeaders.Add("Cookie", sessionCookie);

        using var response = await client.GetAsync("/api/auth/csrf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("casemesh-xsrf-test", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__Host-", cookie, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evidence.txt")]
    [InlineData("folder/evidence.txt")]
    [InlineData("folder\\evidence.txt")]
    [InlineData("evidence..txt")]
    [InlineData("evidence\u0001.txt")]
    public void Unsafe_file_names_are_rejected(string value) =>
        Assert.Throws<BadHttpRequestException>(() => CaseMeshApiEndpoints.RequireSafeFileName(value, 255));

    [Fact]
    public void File_name_over_limit_is_rejected() =>
        Assert.Throws<BadHttpRequestException>(() => CaseMeshApiEndpoints.RequireSafeFileName(new string('a', 256), 255));

    [Fact]
    public void Safe_file_name_is_metadata_only() =>
        Assert.Equal("synthetic-evidence.txt", CaseMeshApiEndpoints.RequireSafeFileName(" synthetic-evidence.txt ", 255));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcdef")]
    public void Invalid_bounded_text_is_rejected(string? value) =>
        Assert.Throws<BadHttpRequestException>(() => CaseMeshApiEndpoints.RequireText(value, 5));

    [Fact]
    public void Bounded_text_is_trimmed() => Assert.Equal("Matter", CaseMeshApiEndpoints.RequireText(" Matter ", 10));

    [Fact]
    public void Production_rejects_test_authentication()
    {
        var options = ValidOptions().WithTestAuth();
        Assert.Throws<InvalidOperationException>(() => options.Validate("Production"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void Non_testing_environments_reject_test_authentication(string environment)
    {
        var options = ValidOptions().WithTestAuth();
        Assert.Throws<InvalidOperationException>(() => options.Validate(environment));
    }

    [Fact]
    public void Explicit_testing_environment_accepts_test_authentication()
    {
        var options = ValidOptions().WithTestAuth();
        options.Validate("Testing");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(104857601)]
    public void Invalid_upload_limits_fail_startup(long limit)
    {
        var options = new CaseMeshApiOptions { PostgresConnectionString = "Host=invalid", EnableTestAuthentication = true, MaximumUploadBytes = limit };
        Assert.Throws<InvalidOperationException>(() => options.Validate("Testing"));
    }

    [Fact]
    public void Production_requires_https_origin()
    {
        var options = ValidOptions("http://casemesh.invalid");
        Assert.Throws<InvalidOperationException>(() => options.Validate("Production"));
    }

    [Fact]
    public void Production_requires_oidc_configuration()
    {
        var options = new CaseMeshApiOptions
        {
            PostgresConnectionString = "Host=invalid", PublicOrigin = "https://casemesh.invalid",
            S3Endpoint = "https://storage.invalid", S3BucketName = "private", S3AccessKey = "external",
            S3SecretKey = "external"
        };
        Assert.Throws<ArgumentNullException>(() => options.Validate("Production"));
    }

    private static CaseMeshApiOptions ValidOptions(string origin = "https://casemesh.invalid") => new()
    {
        PostgresConnectionString = "Host=invalid", PublicOrigin = origin,
        OidcAuthority = "https://idp.invalid", OidcClientId = "client", OidcClientSecret = "external",
        S3Endpoint = "https://storage.invalid", S3BucketName = "private", S3AccessKey = "external", S3SecretKey = "external"
    };
}

internal static class OptionsExtensions
{
    internal static CaseMeshApiOptions WithTestAuth(this CaseMeshApiOptions source) => new()
    {
        PostgresConnectionString = source.PostgresConnectionString, PublicOrigin = source.PublicOrigin,
        OidcAuthority = source.OidcAuthority, OidcClientId = source.OidcClientId,
        OidcClientSecret = source.OidcClientSecret, S3Endpoint = source.S3Endpoint,
        S3BucketName = source.S3BucketName, S3AccessKey = source.S3AccessKey,
        S3SecretKey = source.S3SecretKey, EnableTestAuthentication = true
    };
}

public sealed class SyntheticApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("CaseMesh:PostgresConnectionString",
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1");
        builder.UseSetting("CaseMesh:EnableTestAuthentication", "true");
    }
}
