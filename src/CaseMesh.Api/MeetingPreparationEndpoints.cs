using CaseMesh.Core.Models;
using CaseMesh.Persistence.Postgres;

namespace CaseMesh.Api;

public static class MeetingPreparationEndpoints
{
    public static void MapMeetingPreparationApi(this WebApplication app)
    {
        app.MapGet("/api/workspaces/{tenantId:guid}/matters/{matterId:guid}/prepare", GetPreparationAsync)
            .RequireAuthorization();
    }

    private static async Task<IResult> GetPreparationAsync(
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
        return loaded is null
            ? Results.NotFound()
            : Results.Ok(MeetingPreparationProjection.Create(loaded, processing));
    }
}
