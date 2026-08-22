using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Security.Cryptography;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using CaseMesh.Persistence.Postgres;
using CaseMesh.ProfessionalExport;
using CaseMesh.Qa;
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
        app.MapGet("/health/ready", async (PostgresPilotOperationsRepository operations,
            IGeneratedArtifactStore generated, IMatterReasoningProvider reasoning,
            PilotRuntimeHealth workers, CancellationToken token) =>
        {
            var postgres = await ReadinessAsync(() => operations.CheckReadinessAsync(token));
            var objectStorage = await ReadinessAsync(() => generated.CheckReadinessAsync(token));
            var testHarness = options.EnableTestAuthentication && environment.IsEnvironment("Testing");
            var ingestionDependencies = testHarness ||
                (File.Exists(options.ClamAvExecutablePath) && File.Exists(options.TesseractExecutablePath) &&
                 File.Exists(options.PopplerExecutablePath));
            var qaProvider = !string.IsNullOrWhiteSpace(reasoning.Descriptor.Provider) &&
                             !string.IsNullOrWhiteSpace(reasoning.Descriptor.Model);
            var components = new
            {
                postgres,
                objectStorage,
                ingestionDependencies,
                evidenceWorker = workers.EvidenceWorkerReady(TimeSpan.FromMinutes(2)),
                deletionWorker = workers.DeletionWorkerReady(TimeSpan.FromMinutes(2)),
                qaProvider,
                build = testHarness || !string.IsNullOrWhiteSpace(options.BuildIdentity)
            };
            var ready = components.postgres && components.objectStorage && components.ingestionDependencies &&
                        components.evidenceWorker && components.deletionWorker && components.qaProvider && components.build;
            return Results.Json(new { status = ready ? "ready" : "not-ready" },
                statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });
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
        workspace.MapGet("/matters/{matterId:guid}/deletions/{deletionId:guid}", GetDeletionAsync);
        workspace.MapGet("/matters/{matterId:guid}/usage", GetUsageAsync);
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
        workspace.MapGet("/matters/{matterId:guid}/questions", GetQuestionsAsync);
        workspace.MapPost("/matters/{matterId:guid}/questions/ask", AskQuestionAsync)
            .RequireRateLimiting("matter-qa");
        workspace.MapPost("/matters/{matterId:guid}/assertions/{assertionId:guid}/corrections", CorrectAssertionAsync);
        workspace.MapPost("/matters/{matterId:guid}/exports", ExportAsync);
        workspace.MapGet("/matters/{matterId:guid}/exports/{exportId:guid}", DownloadExportAsync);
    }

    private static async Task<IResult> CreateMatterAsync(Guid tenantId, HttpContext context,
        CreateMatterRequest request, CurrentWebUser users, PostgresWebWorkspaceRepository repository,
        PostgresMatterBrainStore brains, PostgresPilotOperationsRepository operations,
        TimeProvider clock, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        var now = clock.GetUtcNow();
        var matterId = Guid.NewGuid();
        var reservation = await operations.ReserveActiveMatterAsync(tenant, matterId, token);
        var matter = new Matter(matterId, tenant, "workplace-dispute",
            RequireText(request.Title, 160), "active", now, now,
            string.IsNullOrWhiteSpace(request.Jurisdiction) ? null : RequireText(request.Jurisdiction, 80));
        var evidence = new MatterEvidenceGraph(matter);
        try
        {
            await brains.SaveAsync(new MatterBrainState(evidence), new WorkplaceMatter(evidence), token);
            await RecordUsageSafelyAsync(context, () => operations.RecordUsageEventAsync(
                tenant, matterId, PilotUsageEventKind.MatterCreated, "accepted", 1, cancellationToken: token));
            return Results.Created($"/api/workspaces/{tenantId:D}/matters/{matter.Id:D}", WorkspaceProjection.Matter(matter));
        }
        finally
        {
            await ReleaseReservationsSafelyAsync(context, operations, tenant, [reservation.ReservationId]);
        }
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
        PostgresMatterStore store, PostgresPilotOperationsRepository operations,
        TimeProvider clock, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        var current = await repository.GetMatterAsync(user.Id, tenant, matterId, token);
        if (current is null) return Results.NotFound();
        var status = RequireText(request.Status, 40).ToLowerInvariant();
        if (status is not ("active" or "closed" or "archived"))
            throw new BadHttpRequestException("The Matter status is not supported.");
        PilotQuotaReservation? reservation = null;
        if (current.Status != "active" && status == "active")
            reservation = await operations.ReserveActiveMatterAsync(tenant, matterId, token);
        var updatedAt = clock.GetUtcNow();
        if (updatedAt <= current.UpdatedAt) updatedAt = current.UpdatedAt.AddTicks(1);
        try
        {
            var updated = await store.UpdateMatterAsync(tenant, matterId, RequireText(request.Title, 160),
                status, current.UpdatedAt, updatedAt, token);
            return updated ? Results.NoContent() : Results.Conflict();
        }
        finally
        {
            if (reservation is not null)
                await ReleaseReservationsSafelyAsync(context, operations, tenant, [reservation.ReservationId]);
        }
    }

    private static async Task<IResult> DeleteMatterAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository,
        PostgresPilotOperationsRepository operations, PrivacyDeletionCoordinator coordinator,
        CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (await repository.GetMatterAsync(user.Id, tenant, matterId, token) is null) return Results.NotFound();
        var deletion = await operations.EnqueueDeletionAsync(user.Id, tenant, matterId, token);
        coordinator.Signal(user.Id, tenant);
        return Results.Accepted($"/api/workspaces/{tenantId:D}/matters/{matterId:D}/deletions/{deletion.DeletionId:D}",
            deletion);
    }

    private static async Task<IResult> GetDeletionAsync(Guid tenantId, Guid matterId, Guid deletionId,
        HttpContext context, CurrentWebUser users, PostgresWebWorkspaceRepository repository,
        PostgresPilotOperationsRepository operations, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        var deletion = await operations.GetDeletionAsync(tenant, matterId, deletionId, token);
        return deletion is null ? Results.NotFound() : Results.Ok(deletion);
    }

    private static async Task<IResult> GetUsageAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository,
        PostgresPilotOperationsRepository operations, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (await repository.GetMatterAsync(user.Id, tenant, matterId, token) is null) return Results.NotFound();
        return Results.Ok(await operations.GetUsageAsync(tenant, matterId, token));
    }

    private static async Task<IResult> UploadEvidenceAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
        IOriginalEvidenceStore originals, EvidenceJobCoordinator coordinator, CaseMeshApiOptions options,
        PostgresPilotOperationsRepository operations, TimeProvider clock,
        ILoggerFactory loggerFactory, CancellationToken token)
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
        var storeAttempted = false;
        var createdOriginal = false;
        PilotEvidenceReservation? quotaReservation = null;
        IAsyncDisposable? matterStateLock = null;
        try
        {
            string hash;
            long length;
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
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
            quotaReservation = await operations.ReserveEvidenceAsync(tenant, matterId, hash, length, token);
            matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
            var loaded = await brains.LoadAsync(tenant, matterId, token) ?? throw new UnauthorizedAccessException();
            var version = loaded.Evidence.RegisterDocumentVersion(documentId, documentVersionId, hash, proposedOriginalId);
            createdOriginal = version.OriginalObjectId == proposedOriginalId;
            await brains.SaveAsync(loaded.Brain, loaded.Workplace, token);
            brainPersisted = true;
            await using (var content = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                storeAttempted = true;
                await originals.StoreAsync(tenant, matterId, version.OriginalObjectId, content, token);
            }
            var jobId = Guid.NewGuid();
            await repository.AddDocumentJobAsync(user.Id, tenant, matterId, jobId, documentId,
                documentVersionId, version.OriginalObjectId, safeName, clock.GetUtcNow(), token);
            await RecordUsageSafelyAsync(context, () => operations.RecordUsageEventAsync(
                tenant, matterId, PilotUsageEventKind.UploadAccepted, "accepted", length,
                cancellationToken: token));
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
                    if (storeAttempted && createdOriginal)
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
            try
            {
                if (matterStateLock is not null) await matterStateLock.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (quotaReservation is not null)
                        await ReleaseReservationsSafelyAsync(context, operations, tenant,
                            quotaReservation.Reservations.Select(item => item.ReservationId));
                }
                finally
                {
                    try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
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
        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();
        var original = loaded.Evidence.Assertions.SingleOrDefault(item => item.Id == assertionId);
        if (original is null) return Results.NotFound();
        var result = loaded.Brain.CorrectAssertion(assertionId, Guid.NewGuid(), RequireText(request.CorrectedValue, 2_000),
            ResolveCorrectedEventTime(request.CorrectedEventTime, original.EventTime), Guid.NewGuid(),
            $"web-user:{user.Id:D}", clock.GetUtcNow());
        await brains.SaveAsync(loaded.Brain, loaded.Workplace, token);
        return Results.Ok(new
        {
            supersededAssertionId = result.SupersededAssertion.Id,
            correctedAssertionId = result.CorrectedAssertion.Id,
            auditEventId = result.AuditEvent.Id
        });
    }

    private static async Task<IResult> GetQuestionsAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresMatterBrainStore brains,
        CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        var processing = await repository.HasActiveJobsAsync(user.Id, tenant, matterId, token);
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();
        return Results.Ok(WorkspaceProjection.Questions(loaded, processing));
    }

    private static async Task<IResult> AskQuestionAsync(Guid tenantId, Guid matterId, HttpContext context,
        QuestionRequest request, CurrentWebUser users, PostgresWebWorkspaceRepository repository,
        PostgresMatterBrainStore brains, MatterQaService qa,
        PostgresPilotOperationsRepository operations, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        if (await repository.HasActiveJobsAsync(user.Id, tenant, matterId, token))
            return Results.Conflict(new { title = "Evidence processing is still in progress.", code = "evidence-processing" });
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();
        var question = RequireText(request.Question, MatterQaService.MaximumQuestionCharacters);
        var entitlements = await operations.GetEntitlementsAsync(tenant, token);
        await operations.ConsumeDailyAsync(tenant, PilotDailyUsageKind.QaRequest, cancellationToken: token);
        var qaStarted = Stopwatch.GetTimestamp();
        var answer = await qa.AskAsync(new MatterRetrievalRequest(
            tenant, matterId, question,
            MaximumContextBytes: entitlements.QaContextByteLimit), token);
        await RecordUsageSafelyAsync(context, () => operations.RecordUsageEventAsync(
            tenant, matterId, PilotUsageEventKind.Qa, answer.Status.ToString().ToLowerInvariant(), 1,
            cancellationToken: token));
        PilotOperationsTelemetry.QaDuration.Record(Stopwatch.GetElapsedTime(qaStarted).TotalMilliseconds);
        PilotOperationsTelemetry.QaOutcomes.Add(1,
            new TagList { { "outcome", answer.Status == MatterAnswerStatus.Answered ? "answered" : "gated" } });
        var refreshed = await brains.LoadAsync(tenant, matterId, token);
        if (refreshed is null) return Results.NotFound();
        if (await repository.HasActiveJobsAsync(user.Id, tenant, matterId, token))
            return Results.Conflict(new { title = "Evidence processing started while the answer was generated.", code = "evidence-processing" });
        return Results.Ok(WorkspaceProjection.QuestionAnswer(refreshed, answer));
    }

    private static async Task<IResult> ExportAsync(Guid tenantId, Guid matterId, HttpContext context,
        CurrentWebUser users, PostgresWebWorkspaceRepository repository, PostgresProfessionalExportService exports,
        PostgresPilotOperationsRepository operations, IGeneratedArtifactStore generated,
        TimeProvider clock, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        await using var matterLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        if (await repository.GetMatterAsync(user.Id, tenant, matterId, token) is null) return Results.NotFound();
        var entitlements = await operations.GetEntitlementsAsync(tenant, token);
        await operations.ConsumeDailyAsync(tenant, PilotDailyUsageKind.ExportGeneration, cancellationToken: token);
        var exportStarted = Stopwatch.GetTimestamp();
        var exportId = Guid.NewGuid();
        var package = await exports.GenerateAsync(new ProfessionalExportRequest(tenant, matterId, exportId), token);
        if (package is null) return Results.NotFound();
        var bundle = package.Artifacts.Single(item => item.Kind == ProfessionalExportArtifactKind.BundleZip);
        await using var content = new MemoryStream(bundle.Content, writable: false);
        var stored = await generated.StoreAsync(new GeneratedArtifactIdentity(tenant, matterId, exportId,
            (short)ProfessionalExportArtifactKind.BundleZip), content,
            clock.GetUtcNow().AddHours(entitlements.ExportArtifactRetentionHours), token);
        await RecordUsageSafelyAsync(context, () => operations.RecordUsageEventAsync(
            tenant, matterId, PilotUsageEventKind.ExportGenerated, "stored", stored.ByteLength,
            cancellationToken: token));
        PilotOperationsTelemetry.ExportDuration.Record(Stopwatch.GetElapsedTime(exportStarted).TotalMilliseconds);
        PilotOperationsTelemetry.ExportOutcomes.Add(1, new TagList { { "outcome", "stored" } });
        return Results.Accepted($"/api/workspaces/{tenantId:D}/matters/{matterId:D}/exports/{exportId:D}", new
        {
            exportId,
            fileName = bundle.FileName,
            expiresAt = stored.ExpiresAt,
            downloadUrl = $"/api/workspaces/{tenantId:D}/matters/{matterId:D}/exports/{exportId:D}"
        });
    }

    private static async Task<IResult> DownloadExportAsync(Guid tenantId, Guid matterId, Guid exportId,
        HttpContext context, CurrentWebUser users, PostgresWebWorkspaceRepository repository,
        PostgresProfessionalExportService exports, PostgresPilotOperationsRepository operations,
        IGeneratedArtifactStore generated, TimeProvider clock, CancellationToken token)
    {
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        var run = await exports.GetRunAsync(tenant, matterId, exportId, token);
        if (run is null) return Results.NotFound();
        var bundle = run.Run.Artifacts.Single(item => item.Kind == ProfessionalExportArtifactKind.BundleZip);
        if (bundle.ByteLength > int.MaxValue)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "The export bundle exceeds the supported delivery size.",
                extensions: new Dictionary<string, object?> { ["code"] = "export-delivery-size-limit" });
        var destination = new MemoryStream((int)bundle.ByteLength);
        try
        {
            await generated.ReadVerifiedAsync(new GeneratedArtifactIdentity(tenant, matterId, exportId,
                (short)ProfessionalExportArtifactKind.BundleZip), destination, clock.GetUtcNow(), token);
            await operations.ConsumeDailyAsync(tenant, PilotDailyUsageKind.ExportDownload, cancellationToken: token);
            await RecordUsageSafelyAsync(context, () => operations.RecordUsageEventAsync(
                tenant, matterId, PilotUsageEventKind.ExportDownloaded, "verified", bundle.ByteLength,
                cancellationToken: token));
            PilotOperationsTelemetry.ExportOutcomes.Add(1, new TagList { { "outcome", "downloaded" } });
            destination.Position = 0;
            return Results.File(destination, "application/zip", bundle.FileName, enableRangeProcessing: false);
        }
        catch
        {
            await destination.DisposeAsync();
            throw;
        }
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

    internal static DateTimeOffset? ResolveCorrectedEventTime(
        DateTimeOffset? requestedEventTime,
        DateTimeOffset? existingEventTime) => requestedEventTime ?? existingEventTime;

    private static async Task<bool> ReadinessAsync(Func<Task<bool>> check)
    {
        try { return await check(); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static async Task RecordUsageSafelyAsync(HttpContext context, Func<Task> record)
    {
        try { await record(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("CaseMesh.Api.PilotUsage")
                .LogWarning("Typed pilot usage metadata could not be recorded: {ExceptionType}.",
                    exception.GetType().Name);
            PilotOperationsTelemetry.ReconciliationOutcomes.Add(1,
                new TagList { { "outcome", "usage-record-failed" } });
        }
    }

    private static async Task ReleaseReservationsSafelyAsync(HttpContext context,
        PostgresPilotOperationsRepository operations, TenantId tenantId, IEnumerable<Guid> reservationIds)
    {
        try { await operations.ReleaseReservationsAsync(tenantId, reservationIds, CancellationToken.None); }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("CaseMesh.Api.PilotQuota")
                .LogWarning("Pilot reservation cleanup will be recovered by expiry: {ExceptionType}.",
                    exception.GetType().Name);
            PilotOperationsTelemetry.ReconciliationOutcomes.Add(1,
                new TagList { { "outcome", "reservation-cleanup-failed" } });
        }
    }
}

public sealed record TestSignInRequest(string Subject, string DisplayName);
public sealed record CreateWorkspaceRequest(string Name);
public sealed record CreateMatterRequest(string Title, string? Jurisdiction);
public sealed record UpdateMatterRequest(string Title, string Status);
public sealed record CorrectionRequest(string CorrectedValue, DateTimeOffset? CorrectedEventTime);
public sealed record QuestionRequest(string Question);
