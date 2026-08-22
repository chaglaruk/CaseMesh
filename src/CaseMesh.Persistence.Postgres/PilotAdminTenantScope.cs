using CaseMesh.Core.Models;

namespace CaseMesh.Persistence.Postgres;

public static class PilotAdminTenantScope
{
    public static void Require(TenantId requestedTenant, string? authorizedTenantId)
    {
        if (!Guid.TryParse(authorizedTenantId, out var authorized) || authorized == Guid.Empty ||
            authorized != requestedTenant.Value)
            throw new UnauthorizedAccessException(
                "The requested tenant is outside the explicitly authorized pilot-admin scope.");
    }
}
