using CaseMesh.Core.Models;
using CaseMesh.Live;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api;

public static class MeetingReviewEndpoints
{
    public static void MapMeetingReviewApi(this WebApplication app)
    {
        app.MapGet("/api/workspaces/{tenantId:guid}/matters/{matterId:guid}/review/context", GetReviewContextAsync)
            .RequireAuthorization();
        app.MapGet("/api/workspaces/{tenantId:guid}/matters/{matterId:guid}/review/sources/{sourceSpanId:guid}", GetReviewSourceAsync)
            .RequireAuthorization();
        app.MapGet("/api/workspaces/{tenantId:guid}/matters/{matterId:guid}/review/sessions", ListReviewSessionsAsync)
            .RequireAuthorization();
        app.MapPost("/api/workspaces/{tenantId:guid}/matters/{matterId:guid}/review/sessions", CreateReviewSessionAsync)
            .RequireAuthorization();
        app.MapGet("/api/workspaces/{tenantId:guid}/matters/{matterId:guid}/review/sessions/{meetingId:guid}", GetReviewSessionAsync)
            .RequireAuthorization();
    }

    private static async Task<IResult> GetReviewContextAsync(
        Guid tenantId,
        Guid matterId,
        HttpContext context,
        CurrentWebUser users,
        PostgresWebWorkspaceRepository repository,
        PostgresMatterBrainStore brains,
        CancellationToken token)
    {
        ApplyPrivateNoStore(context);
        if (tenantId == Guid.Empty || matterId == Guid.Empty) return Results.NotFound();

        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();

        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        var processing = await repository.HasActiveJobsAsync(user.Id, tenant, matterId, token);
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();

        try
        {
            return Results.Ok(new CanonicalLiveContextAdapter().Build(tenant, matterId, loaded.Brain, processing));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> GetReviewSourceAsync(
        Guid tenantId,
        Guid matterId,
        Guid sourceSpanId,
        HttpContext context,
        CurrentWebUser users,
        PostgresWebWorkspaceRepository repository,
        PostgresMatterBrainStore brains,
        CancellationToken token)
    {
        ApplyPrivateNoStore(context);
        if (tenantId == Guid.Empty || matterId == Guid.Empty || sourceSpanId == Guid.Empty) return Results.NotFound();

        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();

        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();

        try
        {
            return Results.Ok(new CanonicalLiveContextAdapter().BuildSourceDetail(
                tenant,
                matterId,
                sourceSpanId,
                loaded.Brain));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> ListReviewSessionsAsync(
        Guid tenantId,
        Guid matterId,
        HttpContext context,
        CurrentWebUser users,
        PostgresWebWorkspaceRepository repository,
        PostgresMatterBrainStore brains,
        PostgresMatterStore matterStore,
        CancellationToken token)
    {
        ApplyPrivateNoStore(context);
        if (tenantId == Guid.Empty || matterId == Guid.Empty) return Results.NotFound();

        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();
        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        if (await brains.LoadAsync(tenant, matterId, token) is null) return Results.NotFound();

        var reviews = new PostgresUploadedMeetingReviewRepository(matterStore);
        return Results.Ok(await reviews.ListAsync(tenant, matterId, token));
    }

    private static async Task<IResult> CreateReviewSessionAsync(
        Guid tenantId,
        Guid matterId,
        CreateUploadedMeetingReviewRequest request,
        HttpContext context,
        CurrentWebUser users,
        PostgresWebWorkspaceRepository repository,
        PostgresMatterBrainStore brains,
        PostgresMatterStore matterStore,
        TimeProvider clock,
        CancellationToken token)
    {
        ApplyPrivateNoStore(context);
        if (tenantId == Guid.Empty || matterId == Guid.Empty) return Results.NotFound();
        if (request.Items is null || request.Items.Any(item => item is null))
            throw new BadHttpRequestException("Meeting review items are required and cannot contain null entries.");

        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();

        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        var processing = await repository.HasActiveJobsAsync(user.Id, tenant, matterId, token);
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();

        var canonicalContext = new CanonicalLiveContextAdapter().Build(tenant, matterId, loaded.Brain, processing);
        UploadedMeetingReview review;
        try
        {
            review = new UploadedMeetingReviewBuilder().Build(
                canonicalContext,
                Guid.NewGuid(),
                request.Items.Select(item => new LiveConversationItem(
                    item!.Id,
                    item.Origin,
                    item.Text ?? string.Empty,
                    item.StartedAt,
                    item.EndedAt,
                    item.ContextCitationSourceSpanIds ?? [])).ToArray());
        }
        catch (InvalidOperationException)
        {
            throw new BadHttpRequestException("The uploaded meeting review is invalid.");
        }

        var createdAt = clock.GetUtcNow();
        var reviews = new PostgresUploadedMeetingReviewRepository(matterStore);
        await reviews.SaveAsync(user.Id, review, createdAt, token);
        var analysis = new UploadedMeetingReviewAnalyzer().Analyze(review, canonicalContext);
        var response = new UploadedMeetingReviewView(review, createdAt, canonicalContext.Currentness, analysis);
        return Results.Created(
            $"/api/workspaces/{tenantId:D}/matters/{matterId:D}/review/sessions/{review.MeetingId:D}",
            response);
    }

    private static async Task<IResult> GetReviewSessionAsync(
        Guid tenantId,
        Guid matterId,
        Guid meetingId,
        HttpContext context,
        CurrentWebUser users,
        PostgresWebWorkspaceRepository repository,
        PostgresMatterBrainStore brains,
        PostgresMatterStore matterStore,
        CancellationToken token)
    {
        ApplyPrivateNoStore(context);
        if (tenantId == Guid.Empty || matterId == Guid.Empty || meetingId == Guid.Empty) return Results.NotFound();

        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();

        var reviews = new PostgresUploadedMeetingReviewRepository(matterStore);
        var stored = await reviews.LoadAsync(tenant, matterId, meetingId, token);
        if (stored is null) return Results.NotFound();

        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        var processing = await repository.HasActiveJobsAsync(user.Id, tenant, matterId, token);
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();

        var canonicalContext = new CanonicalLiveContextAdapter().Build(tenant, matterId, loaded.Brain, processing);
        var analysis = new UploadedMeetingReviewAnalyzer().Analyze(stored.Review, canonicalContext);
        return Results.Ok(new UploadedMeetingReviewView(
            stored.Review,
            stored.CreatedAt,
            canonicalContext.Currentness,
            analysis));
    }

    private static void ApplyPrivateNoStore(HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store, private";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
    }
}

public sealed record CreateUploadedMeetingReviewRequest(
    IReadOnlyList<CreateUploadedMeetingReviewItemRequest?> Items);

public sealed record CreateUploadedMeetingReviewItemRequest(
    Guid Id,
    LiveConversationOrigin Origin,
    string? Text,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<Guid>? ContextCitationSourceSpanIds);