using System.Security.Claims;
using System.Security.Cryptography;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;
using CaseMesh.ProfessionalExport;
using CaseMesh.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CaseMesh.Api;

public static class CaseMeshApiEndpoints
{
    public static void MapCaseMeshApi(this WebApplication app, CaseMeshApiOptions options, IHostEnvironment environment)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/api/auth/sign-in", (HttpContext context) =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = "/matters" }));
        if (options.EnableTestAuthentication && environment.IsEnvironment("Testing"))
        {
            app.MapPost("/api/auth/test-sign-in", async (HttpContext context, TestSignInRequest request) =>
            {
                var claims = new[]
                {
                    new Claim("iss", "https://synthetic-idp.invalid"),
                    new Claim("sub", RequireText(request.Subject, 100)),
                    new Claim("name", RequireText(request.DisplayName, 100)),
                    new Claim(ClaimTypes.NameIdentifier, request.Subject),
                    new Claim(ClaimTypes.Name, request.DisplayName)
                };
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
                return Results.NoContent();
            });
        }

        var authenticated = app.MapGroup("/api").RequireAuthorization();
        authenticated.MapGet("/auth/session", async (HttpContext context, CurrentWebUser users,
            PostgresWebWorkspaceRepository workspaces, CancellationToken token) =>
        {
            var user = await users.RequireAsync(context.User, token);
            var memberships = await workspaces.ListMembershipsAsync(user.Id, token);
            return Results.Ok(new { user = new { user.Id, user.DisplayName }, memberships });
        });
        authenticated.MapGet("/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        });
        authenticated.MapPost("/auth/sign-out", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        authenticated.MapPost("/workspaces", async (HttpContext context, CreateWorkspaceRequest request,
            CurrentWebUser users, PostgresWebWorkspaceRepository workspaces, TimeProvider clock,
            CancellationToken token) =>
        {
            var user = await users.RequireAsync(context.User, token);
            var tenantId = new TenantId(Guid.NewGuid());
            await workspaces.CreateWorkspaceAsync(user, tenantId, RequireText(request.Name, 120), clock.GetUtcNow(), token);
            return Results.Created($"/api/workspaces/{tenantId.Value:D}", new { tenantId = tenantId.Value });
        });

        var workspace = authenticated.MapGroup("/workspaces/{tenantId:guid}");
        workspace.MapGet("/matters", async (Guid tenantId, HttpContext context, CurrentWebUser users,
            PostgresWebWorkspaceRepository repository, CancellationToken token) =>
        {
            var user = await users.RequireAsync(context.User, token);
            return Results.Ok(await repository.ListMattersAsync(user.Id, new TenantId(tenantId), token));
        });
        workspace.MapPost("/matters", CreateMatterAsync);
        workspace.MapGet("/matters/{matterId:guid}", GetMatterAsync);
        workspace.MapPut("/matters/{matterId:guid}", UpdateMatterAsync);
        workspace.MapDelete("/matters/{matterId:guid}", DeleteMatterAsync);
        workspace.MapPost("/matters/{matterId:guid}/evidence", UploadEvidenceAsync)
            .DisableAntiforgery(); // The explicit API middleware still validates the header before form parsing.
        workspace.MapGet("/matters/{matterId:guid}/jobs/{jobId:guid}", GetJobAsync);
        workspace.MapGet("/matters/{matterId:guid}/overview", (Guid tenantId, Guid matterId, HttpContext context,
            CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
            CancellationToken token) => ProjectAsync(tenantId, matterId, context, users, repository, brains,
                loaded => WorkspaceProjection.Overview(loaded), token));
        workspace.MapGet("/matters/{matterId:guid}/timeline", (Guid tenantId, Guid matterId, HttpContext context,
            CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
            CancellationToken token) => ProjectAsync(tenantId, matterId, context, users, repository, brains,
                loaded => WorkspaceProjection.Timeline(loaded), token));
        workspace.MapGet("/matters/{matterId:guid}/evidence", (Guid tenantId, Guid matterId, HttpContext context,
            CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
            CancellationToken token) => ProjectAsync(tenantId, matterId, context, users, repository, brains,
                loaded => WorkspaceProjection.Evidence(loaded), token));
        workspace.MapGet("/matters/{matterId:guid}/people", (Guid tenantId, Guid matterId, HttpContext context,
            CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
            CancellationToken token) => ProjectAsync(tenantId, matterId, context, users, repository, brains,
                loaded => WorkspaceProjection.People(loaded), token));
        workspace.MapGet("/matters/{matterId:guid}/disputed", (Guid tenantId, Guid matterId, HttpContext context,
            CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
            CancellationToken token) => ProjectAsync(tenantId, matterId, context, users, repository, brains,
                loaded => WorkspaceProjection.Disputed(loaded), token));
        workspace.MapGet("/matters/{matterId:guid}/workplace", (Guid tenantId, Guid matterId, HttpContext context,
            CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
            CancellationToken token) => ProjectAsync(tenantId, matterId, context, users, repository, brains,
                loaded => WorkspaceProjection.Workplace(loaded), token));
        workspace.MapGet("/matters/{matterId:guid}/questions", (Guid tenantId, Guid matterId, HttpContext context,
            CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
            CancellationToken token) => ProjectAsync(tenantId, matterId, context, users, repository, brains,
                loaded => WorkspaceProjection.OpenQuestions(loaded), token));
        workspace.MapPost("/matters/{matterId:guid}/assertions/{assertionId:guid}/corrections", CorrectAssertionAsync);
        workspace.MapPost("/matters/{matterId:guid}/exports", ExportAsync);
    }

    private static async Task<IResult> CreateMatterAsync(Guid tenantId, HttpContext context,
        CreateMatterRequest request, CurrentWebUser users, PostgresWebWorkspaceRepository repository,
        PostgresMatterBrainStore brains, TimeProvider clock, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        var now = clock.GetUtcNow();
        var matter = new Matter(Guid.NewGuid(), tenant, "workplace-dispute",
            RequireText(request.Title, 160), "active", now, now,
            string.IsNullOrWhiteSpace(request.Jurisdiction) ? null : RequireText(request.Jurisdiction, 80));
        var evidence = new MatterEvidenceGraph(matter);
        await brains.SaveAsync(new MatterBrainState(evidence), new WorkplaceMatter(evidence), token);
        return Results.Created($"/api/workspaces/{tenantId:D}/matters/{matter.Id:D}", WorkspaceProjection.Matter(matter));
    }

    private static async Task<IResult> GetMatterAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var matter = await repository.GetMatterAsync(user.Id, new TenantId(tenantId), matterId, token);
        return matter is null ? Results.NotFound() : Results.Ok(matter);
    }

    private static async Task<IResult> UpdateMatterAsync(Guid tenantId, Guid matterId, HttpContext context,
        UpdateMatterRequest request, CurrentWebUser users, PostgresWebWorkspaceRepository repository,
        PostgresMatterStore store, TimeProvider clock, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        var current = await repository.GetMatterAsync(user.Id, tenant, matterId, token);
        if (current is null) return Results.NotFound();
        var updatedAt = clock.GetUtcNow();
        if (updatedAt <= current.UpdatedAt) updatedAt = current.UpdatedAt.AddTicks(1);
        var updated = await store.UpdateMatterAsync(tenant, matterId, RequireText(request.Title, 160),
            RequireText(request.Status, 40), current.UpdatedAt, updatedAt, token);
        return updated ? Results.NoContent() : Results.Conflict();
    }

    private static async Task<IResult> DeleteMatterAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, IOriginalEvidenceStore originals,
        CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (await repository.GetMatterAsync(user.Id, tenant, matterId, token) is null) return Results.NotFound();
        return await originals.DeleteMatterAsync(tenant, matterId, token) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> UploadEvidenceAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
        IOriginalEvidenceStore originals, EvidenceJobCoordinator coordinator, CaseMeshApiOptions options,
        TimeProvider clock, ILoggerFactory loggerFactory, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (await repository.GetMatterAsync(user.Id, tenant, matterId, token) is null) return Results.NotFound();
        var maximumRequestBytes = checked(options.MaximumUploadBytes + 64 * 1024);
        if (context.Request.ContentLength > maximumRequestBytes)
            throw new BadHttpRequestException("The evidence upload request exceeds the configured limit.");
        var form = await context.Request.ReadFormAsync(token);
        var file = form.Files.Count == 1 ? form.Files[0] : throw new BadHttpRequestException("Exactly one evidence file is required.");
        if (file.Length <= 0 || file.Length > options.MaximumUploadBytes) throw new BadHttpRequestException("The evidence file size is outside the configured limit.");
        var safeName = RequireSafeFileName(file.FileName, options.MaximumUploadFileNameLength);
        var tempPath = Path.Combine(Path.GetTempPath(), $"casemesh-web-{Guid.NewGuid():N}.upload");
        var documentId = Guid.NewGuid();
        var documentVersionId = Guid.NewGuid();
        var proposedOriginalId = Guid.NewGuid();
        var brainPersisted = false;
        var originalStored = false;
        var createdOriginal = false;
        try
        {
            string hash;
            long length;
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                BufferSize = 64 * 1024, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(tempPath, fileOptions))
            {
                using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                length = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    length = checked(length + read);
                    if (length > options.MaximumUploadBytes) throw new BadHttpRequestException("The evidence file exceeds the configured limit.");
                    incremental.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                hash = Convert.ToHexString(incremental.GetHashAndReset());
            }
            var loaded = await brains.LoadAsync(tenant, matterId, token) ?? throw new UnauthorizedAccessException();
            var version = loaded.Evidence.RegisterDocumentVersion(documentId, documentVersionId, hash, proposedOriginalId);
            createdOriginal = version.OriginalObjectId == proposedOriginalId;
            await brains.SaveAsync(loaded.Brain, loaded.Workplace, token);
            brainPersisted = true;
            await using (var content = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await originals.StoreAsync(tenant, matterId, version.OriginalObjectId, content, token);
            originalStored = true;
            var jobId = Guid.NewGuid();
            await repository.AddDocumentJobAsync(user.Id, tenant, matterId, jobId, documentId,
                documentVersionId, version.OriginalObjectId, safeName, clock.GetUtcNow(), token);
            coordinator.Signal(user.Id, tenant);
            return Results.Accepted($"/api/workspaces/{tenantId:D}/matters/{matterId:D}/jobs/{jobId:D}",
                new { jobId, documentId, documentVersionId, byteLength = length });
        }
        catch (Exception uploadFailure)
        {
            if (brainPersisted)
            {
                var logger = loggerFactory.CreateLogger("CaseMesh.Api.EvidenceUpload");
                try
                {
                    if (originalStored && createdOriginal)
                        await originals.DeleteOriginalAsync(tenant, matterId, proposedOriginalId, CancellationToken.None);
                    await repository.CompensateFailedUploadAsync(user.Id, tenant, matterId, documentId,
                        documentVersionId, proposedOriginalId, CancellationToken.None);
                }
                catch (Exception compensationFailure)
                {
                    logger.LogError(
                        "Evidence upload compensation failed for document {DocumentId} with type {ExceptionType}.",
                        documentId, compensationFailure.GetType().Name);
                    throw new InvalidOperationException(
                        "The upload failed and its durable compensation requires retry.",
                        new AggregateException(uploadFailure, compensationFailure));
                }
            }
            throw;
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<IResult> GetJobAsync(Guid tenantId, Guid matterId, Guid jobId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, EvidenceJobCoordinator coordinator,
        CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        var job = await repository.GetJobAsync(user.Id, tenant, matterId, jobId, token);
        if (job is null) return Results.NotFound();
        if (job.Status is WebProcessingStatus.Pending or WebProcessingStatus.Processing) coordinator.Signal(user.Id, tenant);
        return Results.Ok(job);
    }

    private static async Task<IResult> CorrectAssertionAsync(Guid tenantId, Guid matterId, Guid assertionId,
        HttpContext context, CorrectionRequest request, CurrentWebUser users,
        PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains, TimeProvider clock,
        CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();
        var result = loaded.Brain.CorrectAssertion(assertionId, Guid.NewGuid(), RequireText(request.CorrectedValue, 2_000),
            request.CorrectedEventTime, Guid.NewGuid(), $"web-user:{user.Id:D}", clock.GetUtcNow());
        await brains.SaveAsync(loaded.Brain, loaded.Workplace, token);
        return Results.Ok(new { supersededAssertionId = result.SupersededAssertion.Id,
            correctedAssertionId = result.CorrectedAssertion.Id, auditEventId = result.AuditEvent.Id });
    }

    private static async Task<IResult> ExportAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresProfessionalExportService exports,
        CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        var package = await exports.GenerateAsync(new ProfessionalExportRequest(tenant, matterId, Guid.NewGuid()), token);
        if (package is null) return Results.NotFound();
        var bundle = package.Artifacts.Single(item => item.Kind == ProfessionalExportArtifactKind.BundleZip);
        return Results.File(bundle.Content, "application/zip", bundle.FileName, enableRangeProcessing: false);
    }

    private static async Task<IResult> ProjectAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
        Func<PersistedMatterBrain, object> projection, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        return loaded is null ? Results.NotFound() : Results.Ok(projection(loaded));
    }

    internal static string RequireSafeFileName(string value, int maximumLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maximumLength || trimmed.Contains('/') || trimmed.Contains('\\') ||
            Path.GetFileName(trimmed) != trimmed ||
            trimmed.Contains("..", StringComparison.Ordinal) || trimmed.Any(char.IsControl))
            throw new BadHttpRequestException("The evidence filename metadata is invalid.");
        return trimmed;
    }

    internal static string RequireText(string? value, int maximumLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maximumLength)
            throw new BadHttpRequestException($"A non-empty value of at most {maximumLength} characters is required.");
        return trimmed;
    }
}

public sealed record TestSignInRequest(string Subject, string DisplayName);
public sealed record CreateWorkspaceRequest(string Name);
public sealed record CreateMatterRequest(string Title, string? Jurisdiction);
public sealed record UpdateMatterRequest(string Title, string Status);
public sealed record CorrectionRequest(string CorrectedValue, DateTimeOffset? CorrectedEventTime);
