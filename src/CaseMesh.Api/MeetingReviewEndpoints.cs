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
        var user = await users.RequireAsync(context.User, token);
        var tenant = new TenantId(tenantId);
        if (!await repository.HasMembershipAsync(user.Id, tenant, token)) return Results.NotFound();

        await using var matterStateLock = await repository.AcquireMatterStateLockAsync(tenant, matterId, token);
        var processing = await repository.HasActiveJobsAsync(user.Id, tenant, matterId, token);
        var loaded = await brains.LoadAsync(tenant, matterId, token);
        if (loaded is null) return Results.NotFound();

        try
        {
            return Results.Ok(new CanonicalLiveContextAdapter().Build(tenant, matterId, loaded, processing));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
    }
}
