using System.Security.Cryptography;
using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using Npgsql;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresWebWorkspaceTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Membership_is_resolved_server_side_and_cross_tenant_access_fails_closed()
    {
        await using var repository = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        var now = DateTimeOffset.Parse("2026-08-21T10:00:00Z");
        var alice = await repository.UpsertUserAsync("https://idp.invalid", $"alice-{Guid.NewGuid():N}", "Alice", now);
        var bob = await repository.UpsertUserAsync("https://idp.invalid", $"bob-{Guid.NewGuid():N}", "Bob", now);
        var tenantA = new TenantId(Guid.NewGuid());
        var tenantB = new TenantId(Guid.NewGuid());
        await repository.CreateWorkspaceAsync(alice, tenantA, "Workspace A", now);
        await repository.CreateWorkspaceAsync(bob, tenantB, "Workspace B", now);

        Assert.True(await repository.HasMembershipAsync(alice.Id, tenantA));
        Assert.False(await repository.HasMembershipAsync(alice.Id, tenantB));
        Assert.Single(await repository.ListMembershipsAsync(alice.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.ListMattersAsync(alice.Id, tenantB));
    }

    [PostgresFact]
    public async Task Matter_list_is_tenant_scoped_and_does_not_leak_another_members_title()
    {
        await using var repository = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        var now = DateTimeOffset.Parse("2026-08-21T11:00:00Z");
        var userA = await repository.UpsertUserAsync("https://idp.invalid", $"a-{Guid.NewGuid():N}", "A", now);
        var userB = await repository.UpsertUserAsync("https://idp.invalid", $"b-{Guid.NewGuid():N}", "B", now);
        var tenantA = new TenantId(Guid.NewGuid()); var tenantB = new TenantId(Guid.NewGuid());
        await repository.CreateWorkspaceAsync(userA, tenantA, "A", now);
        await repository.CreateWorkspaceAsync(userB, tenantB, "B", now);
        await SaveEmptyMatterAsync(tenantA, "Synthetic A", now);
        await SaveEmptyMatterAsync(tenantB, "Synthetic B", now);

        var matters = await repository.ListMattersAsync(userA.Id, tenantA);
        Assert.Single(matters);
        Assert.Equal("Synthetic A", matters[0].Title);
        Assert.DoesNotContain(matters, matter => matter.Title == "Synthetic B");
    }

    [PostgresFact]
    public async Task Durable_job_leases_are_exclusive_and_stale_leases_are_recovered()
    {
        await using var repository = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        var now = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
        var user = await repository.UpsertUserAsync("https://idp.invalid", $"jobs-{Guid.NewGuid():N}", "Worker", now);
        var tenant = new TenantId(Guid.NewGuid()); await repository.CreateWorkspaceAsync(user, tenant, "Jobs", now);
        var ids = await SaveMatterWithDocumentAsync(tenant, now);
        var jobId = Guid.NewGuid();
        await repository.AddDocumentJobAsync(user.Id, tenant, ids.MatterId, jobId, ids.DocumentId,
            ids.VersionId, ids.OriginalId, "synthetic.txt", now);
        Assert.Contains(await repository.ListPendingJobScopesAsync(now),
            scope => scope.UserId == user.Id && scope.TenantId == tenant);
        var firstWorker = Guid.NewGuid();
        var first = await repository.ClaimAsync(user.Id, tenant, firstWorker, now, TimeSpan.FromMinutes(5));
        Assert.NotNull(first); Assert.Equal(1, first.Attempts);
        Assert.Null(await repository.ClaimAsync(user.Id, tenant, Guid.NewGuid(), now.AddMinutes(1), TimeSpan.FromMinutes(5)));
        var processingLock = await repository.AcquireProcessingLockAsync(tenant, jobId);
        Assert.Null(await repository.ClaimAsync(user.Id, tenant, Guid.NewGuid(), now.AddMinutes(6), TimeSpan.FromMinutes(5)));
        await processingLock.DisposeAsync();
        var recovered = await repository.ClaimAsync(user.Id, tenant, Guid.NewGuid(), now.AddMinutes(6), TimeSpan.FromMinutes(5));
        Assert.NotNull(recovered); Assert.Equal(2, recovered.Attempts);
    }

    [PostgresFact]
    public async Task Job_completion_requires_the_active_lease_owner_and_is_visible_to_member_only()
    {
        await using var repository = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var owner = await repository.UpsertUserAsync("https://idp.invalid", $"owner-{Guid.NewGuid():N}", "Owner", now);
        var stranger = await repository.UpsertUserAsync("https://idp.invalid", $"stranger-{Guid.NewGuid():N}", "Stranger", now);
        var tenant = new TenantId(Guid.NewGuid()); await repository.CreateWorkspaceAsync(owner, tenant, "Private", now);
        var ids = await SaveMatterWithDocumentAsync(tenant, now);
        var jobId = Guid.NewGuid(); await repository.AddDocumentJobAsync(owner.Id, tenant, ids.MatterId, jobId,
            ids.DocumentId, ids.VersionId, ids.OriginalId, "synthetic.txt", now);
        var worker = Guid.NewGuid(); var claimed = await repository.ClaimAsync(owner.Id, tenant, worker, now, TimeSpan.FromMinutes(5));
        Assert.NotNull(claimed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CompleteAsync(owner.Id, tenant,
            ids.MatterId, jobId, Guid.NewGuid(), claimed.Attempts, now.AddMinutes(1)));
        await repository.CompleteAsync(owner.Id, tenant, ids.MatterId, jobId, worker, claimed.Attempts, now.AddMinutes(1));
        Assert.Equal(WebProcessingStatus.Completed,
            (await repository.GetJobAsync(owner.Id, tenant, ids.MatterId, jobId))!.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.GetJobAsync(stranger.Id, tenant, ids.MatterId, jobId));
    }

    [PostgresFact]
    public async Task Runtime_user_cannot_self_enroll_into_another_workspace()
    {
        await using var repository = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        var now = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var alice = await repository.UpsertUserAsync("https://idp.invalid", $"alice-{Guid.NewGuid():N}", "Alice", now);
        var bob = await repository.UpsertUserAsync("https://idp.invalid", $"bob-{Guid.NewGuid():N}", "Bob", now);
        var tenant = new TenantId(Guid.NewGuid());
        await repository.CreateWorkspaceAsync(alice, tenant, "Alice workspace", now);

        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
                         "SELECT set_config('casemesh.user_id',$1,true), set_config('casemesh.tenant_id',$2,true);",
                         connection, transaction))
        {
            PostgresMatterStore.AddParameters(context, bob.Id.ToString(), tenant.Value.ToString());
            await context.ExecuteNonQueryAsync();
        }
        await using var enroll = new NpgsqlCommand("""
            INSERT INTO casemesh.tenant_memberships (tenant_id,user_id,membership_role,created_at)
            VALUES ($1,$2,2,$3);
            """, connection, transaction);
        PostgresMatterStore.AddParameters(enroll, tenant.Value, bob.Id, now);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => enroll.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await transaction.RollbackAsync();
        Assert.False(await repository.HasMembershipAsync(bob.Id, tenant));
    }

    [PostgresFact]
    public async Task Processing_job_rejects_a_version_owned_by_another_document()
    {
        await using var repository = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        var now = DateTimeOffset.Parse("2026-08-21T15:00:00Z");
        var user = await repository.UpsertUserAsync("https://idp.invalid", $"fk-{Guid.NewGuid():N}", "Owner", now);
        var tenant = new TenantId(Guid.NewGuid());
        await repository.CreateWorkspaceAsync(user, tenant, "FK workspace", now);

        var matter = new Matter(Guid.NewGuid(), tenant, "workplace-dispute", "Synthetic", "active", now, now);
        var evidence = new MatterEvidenceGraph(matter);
        var firstDocument = Guid.NewGuid();
        var secondDocument = Guid.NewGuid();
        var firstVersion = evidence.RegisterDocumentVersion(firstDocument, Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData("first"u8.ToArray())), Guid.NewGuid());
        var secondVersion = evidence.RegisterDocumentVersion(secondDocument, Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData("second"u8.ToArray())), Guid.NewGuid());
        await using (var brain = new PostgresMatterBrainStore(database.AppConnectionString))
            await brain.SaveAsync(new MatterBrain.MatterBrainState(evidence), new WorkplaceMatter(evidence));

        var failure = await Assert.ThrowsAsync<PostgresException>(() => repository.AddDocumentJobAsync(
            user.Id, tenant, matter.Id, Guid.NewGuid(), firstDocument, secondVersion.DocumentVersionId,
            secondVersion.OriginalObjectId, "synthetic.txt", now));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        Assert.NotEqual(firstVersion.DocumentVersionId, secondVersion.DocumentVersionId);
    }

    [PostgresFact]
    public async Task Processing_job_rejects_an_original_not_owned_by_the_document_version()
    {
        await using var repository = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        var now = DateTimeOffset.Parse("2026-08-21T15:30:00Z");
        var user = await repository.UpsertUserAsync("https://idp.invalid", $"object-fk-{Guid.NewGuid():N}", "Owner", now);
        var tenant = new TenantId(Guid.NewGuid());
        await repository.CreateWorkspaceAsync(user, tenant, "Object FK workspace", now);

        var matter = new Matter(Guid.NewGuid(), tenant, "workplace-dispute", "Synthetic", "active", now, now);
        var evidence = new MatterEvidenceGraph(matter);
        var documentId = Guid.NewGuid();
        var version = evidence.RegisterDocumentVersion(documentId, Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData("first"u8.ToArray())), Guid.NewGuid());
        var otherVersion = evidence.RegisterDocumentVersion(Guid.NewGuid(), Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData("second"u8.ToArray())), Guid.NewGuid());
        await using (var brain = new PostgresMatterBrainStore(database.AppConnectionString))
            await brain.SaveAsync(new MatterBrain.MatterBrainState(evidence), new WorkplaceMatter(evidence));

        var failure = await Assert.ThrowsAsync<PostgresException>(() => repository.AddDocumentJobAsync(
            user.Id, tenant, matter.Id, Guid.NewGuid(), documentId, version.DocumentVersionId,
            otherVersion.OriginalObjectId, "synthetic.txt", now));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
    }

    [PostgresFact]
    public async Task Failed_upload_compensation_removes_unexposed_document_and_job_state()
    {
        await using var repository = new PostgresWebWorkspaceRepository(database.AppConnectionString);
        var now = DateTimeOffset.Parse("2026-08-21T16:00:00Z");
        var user = await repository.UpsertUserAsync("https://idp.invalid", $"comp-{Guid.NewGuid():N}", "Owner", now);
        var tenant = new TenantId(Guid.NewGuid());
        await repository.CreateWorkspaceAsync(user, tenant, "Compensation workspace", now);
        var ids = await SaveMatterWithDocumentAsync(tenant, now);
        var jobId = Guid.NewGuid();
        await repository.AddDocumentJobAsync(user.Id, tenant, ids.MatterId, jobId, ids.DocumentId,
            ids.VersionId, ids.OriginalId, "synthetic.txt", now);

        await repository.CompensateFailedUploadAsync(user.Id, tenant, ids.MatterId, ids.DocumentId,
            ids.VersionId, ids.OriginalId);

        Assert.Null(await repository.GetJobAsync(user.Id, tenant, ids.MatterId, jobId));
        await using var brain = new PostgresMatterBrainStore(database.AppConnectionString);
        var reloaded = await brain.LoadAsync(tenant, ids.MatterId);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded.Evidence.DocumentVersions);
    }

    private async Task SaveEmptyMatterAsync(TenantId tenantId, string title, DateTimeOffset now)
    {
        var matter = new Matter(Guid.NewGuid(), tenantId, "workplace-dispute", title, "active", now, now);
        var evidence = new MatterEvidenceGraph(matter);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(new MatterBrain.MatterBrainState(evidence), new WorkplaceMatter(evidence));
    }

    private async Task<(Guid MatterId, Guid DocumentId, Guid VersionId, Guid OriginalId)> SaveMatterWithDocumentAsync(
        TenantId tenantId, DateTimeOffset now)
    {
        var matter = new Matter(Guid.NewGuid(), tenantId, "workplace-dispute", "Synthetic", "active", now, now);
        var evidence = new MatterEvidenceGraph(matter);
        var documentId = Guid.NewGuid(); var versionId = Guid.NewGuid(); var originalId = Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData("synthetic"u8.ToArray()));
        var version = evidence.RegisterDocumentVersion(documentId, versionId, hash, originalId);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(new MatterBrain.MatterBrainState(evidence), new WorkplaceMatter(evidence));
        return (matter.Id, documentId, versionId, version.OriginalObjectId);
    }
}
