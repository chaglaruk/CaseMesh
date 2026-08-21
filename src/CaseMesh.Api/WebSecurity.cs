using System.Security.Claims;
using CaseMesh.Persistence.Postgres;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Antiforgery;

namespace CaseMesh.Api;

internal static class OidcConfiguration
{
    internal const string CallbackPath = "/api/auth/signin-oidc";

    internal static void Apply(OpenIdConnectOptions oidc, CaseMeshApiOptions options)
    {
        oidc.Authority = options.OidcAuthority;
        oidc.ClientId = options.OidcClientId;
        oidc.ClientSecret = options.OidcClientSecret;
        oidc.CallbackPath = CallbackPath;
        oidc.ResponseType = "code";
        oidc.UsePkce = true;
        oidc.SaveTokens = false;
        oidc.MapInboundClaims = false;
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        var externalCallbackUri = BuildExternalCallbackUri(options.PublicOrigin);
        oidc.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = externalCallbackUri;
            return Task.CompletedTask;
        };
    }

    internal static string BuildExternalCallbackUri(string publicOrigin) =>
        new Uri(new Uri(publicOrigin, UriKind.Absolute), CallbackPath).AbsoluteUri;
}

public sealed class CurrentWebUser(PostgresWebWorkspaceRepository repository, TimeProvider timeProvider)
{
    public async Task<WebUser> RequireAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.Identity?.IsAuthenticated is not true)
            throw new UnauthorizedAccessException("Authentication is required.");
        var issuer = principal.FindFirstValue("iss") ?? "configured-oidc-provider";
        var subject = principal.FindFirstValue("sub") ??
                      principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
                      throw new UnauthorizedAccessException("The authenticated identity has no subject.");
        var name = principal.FindFirstValue("name") ?? principal.Identity.Name ?? "CaseMesh user";
        return await repository.UpsertUserAsync(issuer, subject, name, timeProvider.GetUtcNow(), cancellationToken);
    }
}

public sealed class ApiAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) &&
                           !HttpMethods.IsHead(context.Request.Method) &&
                           !HttpMethods.IsOptions(context.Request.Method);
        var exemptTestLogin = context.Request.Path.Equals("/api/auth/test-sign-in") ||
                              context.Request.Path.Equals("/api/auth/sign-in");
        if (unsafeMethod && context.Request.Path.StartsWithSegments("/api") && !exemptTestLogin)
            await antiforgery.ValidateRequestAsync(context);
        await next(context);
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["X-Frame-Options"] = "DENY";
            return Task.CompletedTask;
        });
        await next(context);
    }
}
