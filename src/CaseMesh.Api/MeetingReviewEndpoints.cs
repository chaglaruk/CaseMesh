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

    private static void ApplyPrivateNoStore(HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store, private";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
    }
}