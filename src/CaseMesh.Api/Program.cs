using System.Threading.RateLimiting;
using CaseMesh.Api;
using CaseMesh.Ingestion;
using CaseMesh.Persistence.Postgres;
using CaseMesh.Storage;
using CaseMesh.Storage.S3;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection(CaseMeshApiOptions.SectionName).Get<CaseMeshApiOptions>()
              ?? new CaseMeshApiOptions();
options.Validate(builder.Environment.EnvironmentName);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new PostgresWebWorkspaceRepository(options.PostgresConnectionString));
builder.Services.AddSingleton(_ => new PostgresMatterBrainStore(options.PostgresConnectionString));
builder.Services.AddSingleton(_ => new PostgresMatterStore(options.PostgresConnectionString));
builder.Services.AddSingleton(_ => new PostgresProfessionalExportService(options.PostgresConnectionString, TimeProvider.System));
builder.Services.AddSingleton<IOriginalEvidenceStore>(_ => options.EnableTestAuthentication && string.IsNullOrWhiteSpace(options.S3Endpoint)
    ? new DisabledEvidenceStore()
    : new S3OriginalEvidenceStore(options.PostgresConnectionString, new S3ObjectStorageOptions
    {
        Endpoint = new Uri(options.S3Endpoint), Region = options.S3Region, BucketName = options.S3BucketName,
        AccessKey = options.S3AccessKey, SecretKey = options.S3SecretKey,
        AllowInsecureLocalEndpoint = options.AllowInsecureLocalObjectStorage
    }));
builder.Services.AddSingleton<IIngestionRepository>(provider =>
    new PostgresIngestionRepository(provider.GetRequiredService<PostgresMatterStore>()));
builder.Services.AddSingleton<IMalwareScanner>(_ => options.EnableTestAuthentication
    ? new SyntheticCleanScanner() : new ClamAvCliScanner("runtime-configured", TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<IOcrEngine>(_ => options.EnableTestAuthentication
    ? new SyntheticOcrEngine() : new TesseractCliOcrEngine("runtime-configured", TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<IPdfPageRasterizer>(_ =>
    new PopplerPdfPageRasterizer("runtime-configured", TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton(new IngestionPipeline("web-v1", options.EnableTestAuthentication ? "synthetic-clean" : "clamav-cli",
    "runtime-configured", "casemesh-parsers-v1", options.EnableTestAuthentication ? "synthetic-ocr" : "tesseract-cli",
    "runtime-configured"));
builder.Services.AddSingleton<EvidenceJobCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<EvidenceJobCoordinator>());
builder.Services.AddScoped<CurrentWebUser>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddAntiforgery(configuration =>
{
    configuration.HeaderName = "X-CSRF-TOKEN";
    configuration.Cookie.Name = "__Host-casemesh-xsrf";
    configuration.Cookie.HttpOnly = true;
    configuration.Cookie.SameSite = SameSiteMode.Lax;
    configuration.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = options.MaximumUploadBytes;
    form.ValueLengthLimit = 16 * 1024;
    form.MultipartHeadersLengthLimit = 8 * 1024;
});
builder.Services.AddAuthentication(authentication =>
{
    authentication.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    authentication.DefaultChallengeScheme = options.EnableTestAuthentication
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
if (!options.EnableTestAuthentication)
{
    builder.Services.AddAuthentication().AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
    {
        oidc.Authority = options.OidcAuthority;
        oidc.ClientId = options.OidcClientId;
        oidc.ClientSecret = options.OidcClientSecret;
        oidc.ResponseType = "code";
        oidc.UsePkce = true;
        oidc.SaveTokens = false;
        oidc.MapInboundClaims = false;
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
    });
}
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiAntiforgeryMiddleware>();
app.MapCaseMeshApi(options, app.Environment);
app.Run();

public partial class Program;
