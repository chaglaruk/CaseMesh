using System.Threading.RateLimiting;
using CaseMesh.Api;
using CaseMesh.Ingestion;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Qa;
using CaseMesh.Storage;
using CaseMesh.Storage.S3;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection(CaseMeshApiOptions.SectionName).Get<CaseMeshApiOptions>()
              ?? new CaseMeshApiOptions();
options.Validate(builder.Environment.EnvironmentName);
var isExplicitTestHarness = options.EnableTestAuthentication && builder.Environment.IsEnvironment("Testing");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PilotRuntimeHealth>();
builder.Services.AddSingleton(_ => new PostgresWebWorkspaceRepository(options.PostgresConnectionString));
builder.Services.AddSingleton(_ => new PostgresMatterBrainStore(options.PostgresConnectionString));
builder.Services.AddSingleton(_ => new PostgresMatterStore(options.PostgresConnectionString));
builder.Services.AddSingleton(provider => new PostgresMatterEvidenceRetriever(
    provider.GetRequiredService<PostgresMatterStore>()));
builder.Services.AddSingleton<IMatterEvidenceRetriever>(provider =>
    provider.GetRequiredService<PostgresMatterEvidenceRetriever>());
builder.Services.AddSingleton<IMatterReasoningProvider, DeterministicMatterReasoningProvider>();
builder.Services.AddSingleton<MatterQaService>();
builder.Services.AddSingleton(_ => new PostgresProfessionalExportService(options.PostgresConnectionString, TimeProvider.System));
builder.Services.AddSingleton(_ => new PostgresPilotOperationsRepository(options.PostgresConnectionString, TimeProvider.System));
builder.Services.AddSingleton<IOriginalEvidenceStore>(_ => isExplicitTestHarness && string.IsNullOrWhiteSpace(options.S3Endpoint)
    ? new DisabledEvidenceStore()
    : new S3OriginalEvidenceStore(options.PostgresConnectionString, new S3ObjectStorageOptions
    {
        Endpoint = new Uri(options.S3Endpoint),
        Region = options.S3Region,
        BucketName = options.S3BucketName,
        AccessKey = options.S3AccessKey,
        SecretKey = options.S3SecretKey,
        AllowInsecureLocalEndpoint = options.AllowInsecureLocalObjectStorage
    }));
builder.Services.AddSingleton<IGeneratedArtifactStore>(_ => isExplicitTestHarness && string.IsNullOrWhiteSpace(options.S3Endpoint)
    ? new DisabledGeneratedArtifactStore()
    : new S3GeneratedArtifactStore(options.PostgresConnectionString, new S3ObjectStorageOptions
    {
        Endpoint = new Uri(options.S3Endpoint),
        Region = options.S3Region,
        BucketName = options.S3BucketName,
        AccessKey = options.S3AccessKey,
        SecretKey = options.S3SecretKey,
        AllowInsecureLocalEndpoint = options.AllowInsecureLocalObjectStorage
    }));
builder.Services.AddSingleton<IIngestionRepository>(provider =>
    new PostgresIngestionRepository(provider.GetRequiredService<PostgresMatterStore>()));
builder.Services.AddSingleton<IMalwareScanner>(_ => isExplicitTestHarness
    ? new SyntheticCleanScanner() : new ClamAvCliScanner(options.ClamAvExecutablePath, TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<IOcrEngine>(_ => isExplicitTestHarness
    ? new SyntheticOcrEngine() : new TesseractCliOcrEngine(options.TesseractExecutablePath, TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<IPdfPageRasterizer>(_ =>
    new PopplerPdfPageRasterizer(isExplicitTestHarness ? "runtime-configured" : options.PopplerExecutablePath, TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton(new IngestionPipeline("web-v1", isExplicitTestHarness ? "synthetic-clean" : "clamav-cli",
    "runtime-configured", "casemesh-parsers-v1", isExplicitTestHarness ? "synthetic-ocr" : "tesseract-cli",
    "runtime-configured"));
builder.Services.AddSingleton<EvidenceJobCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<EvidenceJobCoordinator>());
builder.Services.AddSingleton<PrivacyDeletionCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PrivacyDeletionCoordinator>());
builder.Services.AddScoped<CurrentWebUser>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddAntiforgery(configuration =>
{
    configuration.HeaderName = "X-CSRF-TOKEN";
    configuration.Cookie.Name = isExplicitTestHarness ? "casemesh-xsrf-test" : "__Host-casemesh-xsrf";
    configuration.Cookie.HttpOnly = true;
    configuration.Cookie.SameSite = SameSiteMode.Lax;
    configuration.Cookie.SecurePolicy = isExplicitTestHarness
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = options.MaximumUploadBytes;
    form.ValueLengthLimit = 16 * 1024;
    form.MultipartHeadersLengthLimit = 8 * 1024;
});
builder.WebHost.ConfigureKestrel(server =>
    server.Limits.MaxRequestBodySize = checked(options.MaximumUploadBytes + 64 * 1024));
builder.Services.AddAuthentication(authentication =>
{
    authentication.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    authentication.DefaultChallengeScheme = isExplicitTestHarness
        ? CookieAuthenticationDefaults.AuthenticationScheme : OpenIdConnectDefaults.AuthenticationScheme;
}).AddCookie(cookie =>
{
    cookie.Cookie.Name = "__Host-casemesh-session";
    cookie.Cookie.HttpOnly = true;
    cookie.Cookie.SameSite = SameSiteMode.Lax;
    cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    cookie.SlidingExpiration = false;
    cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
    cookie.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    cookie.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
if (!isExplicitTestHarness)
{
    builder.Services.AddAuthentication().AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
        OidcConfiguration.Apply(oidc, options));
}
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    limiter.AddPolicy("matter-qa", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Request.RouteValues.TryGetValue("tenantId", out var tenantId) &&
        Guid.TryParse(tenantId?.ToString(), out var parsedTenantId)
            ? $"tenant:{parsedTenantId:D}" : "tenant:unresolved",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<PilotTelemetryMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<ApiAntiforgeryMiddleware>();
app.MapCaseMeshApi(options, app.Environment);
app.Run();

public partial class Program;
