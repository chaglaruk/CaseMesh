using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Snapshots;
using CaseMesh.Core.Workplace;
using Npgsql;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMatterStoreTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Migrations_are_versioned_and_idempotent()
    {
        var migrator = new PostgresMigrator();

        var before = await migrator.GetAppliedMigrationsAsync(database.AdminConnectionString);
        var after = await migrator.MigrateAsync(database.AdminConnectionString);

        Assert.Equal(["0001", "0002", "0003", "0004", "0005", "0006", "0007", "0008"], after.Select(migration => migration.Version));
        Assert.Equal(before, after);
    }

    [PostgresFact]
    public async Task Migration_ledger_is_empty_before_zero_to_current_migration()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = $"casemesh_unmigrated_{suffix}";
        var roleName = $"casemesh_unmigrated_app_{suffix}";
        var rootBuilder = new NpgsqlConnectionStringBuilder(database.AdminRootConnectionString);
        await using (var root = new NpgsqlConnection(rootBuilder.ConnectionString))
        {
            await root.OpenAsync();
            await using var create = new NpgsqlCommand($"""
                CREATE DATABASE "{databaseName}";
                CREATE ROLE "{roleName}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                GRANT CONNECT ON DATABASE "{databaseName}" TO "{roleName}";
                """, root);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var emptyBuilder = new NpgsqlConnectionStringBuilder(rootBuilder.ConnectionString) { Database = databaseName };
            var migrator = new PostgresMigrator();
            Assert.Empty(await migrator.GetAppliedMigrationsAsync(emptyBuilder.ConnectionString));
            await migrator.MigrateThroughAsync(emptyBuilder.ConnectionString, "0001");
            await using (var admin = new NpgsqlConnection(emptyBuilder.ConnectionString))
            {
                await admin.OpenAsync();
                await new NpgsqlCommand($"""
                    GRANT USAGE ON SCHEMA casemesh TO "{roleName}";
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA casemesh TO "{roleName}";
                    """, admin).ExecuteNonQueryAsync();
            }
            var applied = await migrator.MigrateAsync(emptyBuilder.ConnectionString);
            Assert.Equal(["0001", "0002", "0003", "0004", "0005", "0006", "0007", "0008"], applied.Select(migration => migration.Version));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var root = new NpgsqlConnection(rootBuilder.ConnectionString);
            await root.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE); DROP ROLE IF EXISTS \"{roleName}\";",
                root);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Later_migrations_preserve_existing_restricted_runtime_role_privileges()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = $"casemesh_upgrade_{suffix}";
        var roleName = $"casemesh_upgrade_app_{suffix}";
        var password = $"synthetic-{Guid.NewGuid():N}";
        var rootBuilder = new NpgsqlConnectionStringBuilder(database.AdminRootConnectionString);
        await using (var root = new NpgsqlConnection(rootBuilder.ConnectionString))
        {
            await root.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", root).ExecuteNonQueryAsync();
            await new NpgsqlCommand($"""
                CREATE ROLE "{roleName}" LOGIN PASSWORD '{password}'
                    NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                GRANT CONNECT ON DATABASE "{databaseName}" TO "{roleName}";
                """, root).ExecuteNonQueryAsync();
        }

        try
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(rootBuilder.ConnectionString) { Database = databaseName };
            var migrator = new PostgresMigrator();
            await migrator.MigrateThroughAsync(adminBuilder.ConnectionString, "0001");
            await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await admin.OpenAsync();
                await new NpgsqlCommand($"""
                    GRANT USAGE ON SCHEMA casemesh TO "{roleName}";
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA casemesh TO "{roleName}";
                    """, admin).ExecuteNonQueryAsync();
            }

            await migrator.MigrateAsync(adminBuilder.ConnectionString);

            var appConnectionString = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Username = roleName,
                Password = password,
                Pooling = false
            }.ConnectionString;
            await using var app = new NpgsqlConnection(appConnectionString);
            await app.OpenAsync();
            await using var privileges = new NpgsqlCommand("""
                SELECT has_table_privilege(current_user, 'casemesh.original_object_storage', 'SELECT'),
                       has_table_privilege(current_user, 'casemesh.original_object_storage', 'INSERT'),
                       has_table_privilege(current_user, 'casemesh.original_object_storage', 'DELETE'),
                       has_table_privilege(current_user, 'casemesh.original_object_storage', 'UPDATE'),
                       has_table_privilege(current_user, 'casemesh.ingestion_span_sets', 'SELECT'),
                       has_table_privilege(current_user, 'casemesh.ingestion_span_sets', 'INSERT'),
                       has_table_privilege(current_user, 'casemesh.ingestion_span_sets', 'UPDATE'),
                       has_table_privilege(current_user, 'casemesh.ingestion_attempts', 'DELETE'),
                       has_table_privilege(current_user, 'casemesh.document_ingestion_state', 'UPDATE'),
                       has_table_privilege(current_user, 'casemesh.extraction_runs', 'SELECT'),
                       has_table_privilege(current_user, 'casemesh.extraction_runs', 'INSERT'),
                       has_table_privilege(current_user, 'casemesh.extraction_runs', 'UPDATE'),
                       has_table_privilege(current_user, 'casemesh.extraction_candidates', 'DELETE'),
                       has_table_privilege(current_user, 'casemesh.professional_export_runs', 'SELECT'),
                       has_table_privilege(current_user, 'casemesh.professional_export_runs', 'INSERT'),
                       has_table_privilege(current_user, 'casemesh.professional_export_runs', 'UPDATE'),
                       has_table_privilege(current_user, 'casemesh.professional_export_runs', 'DELETE'),
                       has_table_privilege(current_user, 'casemesh.web_document_metadata', 'DELETE'),
                       has_table_privilege(current_user, 'casemesh.web_processing_jobs', 'DELETE'),
                       has_table_privilege(current_user, 'casemesh.tenant_memberships', 'SELECT'),
                       has_table_privilege(current_user, 'casemesh.tenant_memberships', 'INSERT'),
                       has_function_privilege(current_user,
                           'casemesh.create_owned_workspace(uuid,uuid,text,timestamptz)', 'EXECUTE');
                """, app);
            await using var reader = await privileges.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
            Assert.True(reader.GetBoolean(5));
            Assert.False(reader.GetBoolean(6));
            Assert.False(reader.GetBoolean(7));
            Assert.True(reader.GetBoolean(8));
            Assert.True(reader.GetBoolean(9));
            Assert.True(reader.GetBoolean(10));
            Assert.False(reader.GetBoolean(11));
            Assert.False(reader.GetBoolean(12));
            Assert.True(reader.GetBoolean(13));
            Assert.True(reader.GetBoolean(14));
            Assert.False(reader.GetBoolean(15));
            Assert.False(reader.GetBoolean(16));
            Assert.True(reader.GetBoolean(17));
            Assert.True(reader.GetBoolean(18));
            Assert.True(reader.GetBoolean(19));
            Assert.False(reader.GetBoolean(20));
            Assert.True(reader.GetBoolean(21));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var root = new NpgsqlConnection(rootBuilder.ConnectionString);
            await root.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);", root)
                .ExecuteNonQueryAsync();
            await new NpgsqlCommand($"DROP ROLE IF EXISTS \"{roleName}\";", root).ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Runtime_store_rejects_superuser_or_bypassrls_connection()
    {
        await using var unsafeStore = new PostgresMatterStore(database.AdminConnectionString);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => unsafeStore.CreateTenantAsync(
            NewTenant(), "Synthetic unsafe-role tenant", SyntheticPersistedMatterFactory.RecordedAt));

        Assert.Contains("BYPASSRLS", exception.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Correct_tenant_can_round_trip_its_matter()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var expected = SyntheticPersistedMatterFactory.Create(tenant, matterId, 101);

        await store.SaveAsync(expected.Evidence, expected.Workplace);
        var actual = await store.LoadAsync(tenant, matterId);

        Assert.NotNull(actual);
        Assert.Equal(tenant, actual.Evidence.Matter.TenantId);
        Assert.Equal(matterId, actual.Evidence.Matter.Id);
    }

    [PostgresFact]
    public async Task Non_utc_timestamps_and_full_decimal_confidence_round_trip_without_loss()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var original = SyntheticPersistedMatterFactory.Create(tenant, matterId, 201);
        var snapshot = original.Evidence.CaptureSnapshot();
        var offset = TimeSpan.FromHours(1);
        var createdAt = SyntheticPersistedMatterFactory.RecordedAt.ToOffset(offset);
        const decimal confidence = 0.123456789m;
        var evidence = MatterEvidenceGraph.Rehydrate(snapshot with
        {
            Matter = new Matter(
                matterId,
                tenant,
                snapshot.Matter.MatterType,
                snapshot.Matter.Title,
                snapshot.Matter.Status,
                createdAt,
                createdAt,
                snapshot.Matter.Jurisdiction),
            SourceSpans = snapshot.SourceSpans
                .Select(span => span with { ExtractionConfidence = confidence })
                .ToArray(),
            Assertions = snapshot.Assertions
                .Select(assertion => assertion.SourceSpanId.HasValue
                    ? assertion with { ExtractionConfidence = confidence }
                    : assertion)
                .ToArray()
        });
        var workplace = WorkplaceMatter.Rehydrate(evidence, original.Workplace.CaptureSnapshot());

        await store.SaveAsync(evidence, workplace);
        var loaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));

        Assert.Equal(createdAt.ToUniversalTime(), loaded.Evidence.Matter.CreatedAt);
        Assert.All(loaded.Evidence.SourceSpans, span => Assert.Equal(confidence, span.ExtractionConfidence));
        Assert.All(
            loaded.Evidence.Assertions.Where(assertion => assertion.SourceSpanId.HasValue),
            assertion => Assert.Equal(confidence, assertion.ExtractionConfidence));
    }

    [PostgresFact]
    public async Task Existing_source_span_id_cannot_be_reused_for_different_provenance()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var original = SyntheticPersistedMatterFactory.Create(tenant, matterId, 202);
        await store.SaveAsync(original.Evidence, original.Workplace);

        var snapshot = original.Evidence.CaptureSnapshot();
        var conflictingEvidence = MatterEvidenceGraph.Rehydrate(snapshot with
        {
            SourceSpans = snapshot.SourceSpans
                .Select((span, index) => index == 0 ? span with { ParserVersion = "conflicting-parser/2" } : span)
                .ToArray()
        });
        var conflictingWorkplace = WorkplaceMatter.Rehydrate(
            conflictingEvidence,
            original.Workplace.CaptureSnapshot());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(conflictingEvidence, conflictingWorkplace));
        Assert.Contains("source span id", exception.Message, StringComparison.OrdinalIgnoreCase);

        var loaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));
        Assert.DoesNotContain(loaded.Evidence.SourceSpans, span => span.ParserVersion == "conflicting-parser/2");
    }

    [PostgresFact]
    public async Task Existing_document_version_id_cannot_be_reused_for_different_provenance()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var original = SyntheticPersistedMatterFactory.Create(tenant, matterId, 205);
        await store.SaveAsync(original.Evidence, original.Workplace);

        var snapshot = original.Evidence.CaptureSnapshot();
        var conflictingEvidence = MatterEvidenceGraph.Rehydrate(snapshot with
        {
            DocumentVersions = snapshot.DocumentVersions
                .Select((version, index) => index == 0 ? version with { DocumentId = Guid.NewGuid() } : version)
                .ToArray()
        });
        var conflictingWorkplace = WorkplaceMatter.Rehydrate(
            conflictingEvidence,
            original.Workplace.CaptureSnapshot());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(conflictingEvidence, conflictingWorkplace));
        Assert.Contains("document-version id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Existing_snapshot_entity_ids_cannot_overwrite_immutable_records_or_relationships()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var original = SyntheticPersistedMatterFactory.Create(tenant, matterId, 207);
        await store.SaveAsync(original.Evidence, original.Workplace);
        var evidenceSnapshot = original.Evidence.CaptureSnapshot();
        var workplaceSnapshot = original.Workplace.CaptureSnapshot();

        await AssertRejectedAsync(evidenceSnapshot with
        {
            AssertionEventLinks = evidenceSnapshot.AssertionEventLinks
                .Select((link, index) => index == 0
                    ? link with { Relation = AssertionEventRelation.Contextualizes }
                    : link)
                .ToArray()
        }, workplaceSnapshot);
        await AssertRejectedAsync(evidenceSnapshot with
        {
            AnalysisNodes = evidenceSnapshot.AnalysisNodes
                .Select((node, index) => index == 0 ? node with { Output = "Conflicting synthetic output." } : node)
                .ToArray()
        }, workplaceSnapshot);
        await AssertRejectedAsync(evidenceSnapshot, workplaceSnapshot with
        {
            EmploymentTerms = workplaceSnapshot.EmploymentTerms
                .Select((term, index) => index == 0 ? term with { Value = "Conflicting synthetic term." } : term)
                .ToArray()
        });
        await AssertRejectedAsync(evidenceSnapshot, workplaceSnapshot with
        {
            WorkplaceProcesses = workplaceSnapshot.WorkplaceProcesses
                .Select((process, index) => index == 0
                    ? process with { StageLabel = "Conflicting synthetic stage" }
                    : process)
                .ToArray()
        });

        async Task AssertRejectedAsync(MatterEvidenceSnapshot changedEvidence, WorkplaceSnapshot changedWorkplace)
        {
            var evidence = MatterEvidenceGraph.Rehydrate(changedEvidence);
            var workplace = WorkplaceMatter.Rehydrate(evidence, changedWorkplace);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(evidence, workplace));
        }
    }

    [PostgresFact]
    public async Task Stale_snapshot_cannot_reverse_event_supersession()
    {
        const int seed = 203;
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var current = SyntheticPersistedMatterFactory.Create(tenant, matterId, seed);
        await store.SaveAsync(current.Evidence, current.Workplace);

        var currentSnapshot = current.Evidence.CaptureSnapshot();
        var originalEventId = SyntheticPersistedMatterFactory.Id(seed, 130);
        var correctedEventId = SyntheticPersistedMatterFactory.Id(seed, 133);
        var staleEvidence = MatterEvidenceGraph.Rehydrate(currentSnapshot with
        {
            Events = currentSnapshot.Events
                .Where(matterEvent => matterEvent.Id != correctedEventId)
                .Select(matterEvent => matterEvent.Id == originalEventId
                    ? matterEvent with { Status = EventStatus.Candidate, SupersededByEventId = null }
                    : matterEvent)
                .ToArray(),
            AssertionEventLinks = currentSnapshot.AssertionEventLinks
                .Where(link => link.EventId != correctedEventId)
                .ToArray(),
            AuditEvents = []
        });
        var workplaceSnapshot = current.Workplace.CaptureSnapshot();
        var staleWorkplace = WorkplaceMatter.Rehydrate(staleEvidence, workplaceSnapshot with
        {
            WorkplaceProcesses = workplaceSnapshot.WorkplaceProcesses
                .Where(process => process.Id != SyntheticPersistedMatterFactory.Id(seed, 157))
                .ToArray()
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(staleEvidence, staleWorkplace));
        Assert.Contains("reverse supersession", exception.Message, StringComparison.OrdinalIgnoreCase);

        var loaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));
        var originalEvent = Assert.Single(loaded.Evidence.Events, item => item.Id == originalEventId);
        Assert.Equal(correctedEventId, originalEvent.SupersededByEventId);
    }

    [PostgresFact]
    public async Task Contradiction_resolution_is_persisted_and_cannot_be_reversed()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var original = SyntheticPersistedMatterFactory.Create(tenant, matterId, 204);
        await store.SaveAsync(original.Evidence, original.Workplace);

        var unresolvedSnapshot = original.Evidence.CaptureSnapshot();
        var resolvedAt = SyntheticPersistedMatterFactory.RecordedAt.AddDays(1);
        var resolvedEvidence = MatterEvidenceGraph.Rehydrate(unresolvedSnapshot with
        {
            Contradictions = unresolvedSnapshot.Contradictions
                .Select(contradiction => contradiction with
                {
                    ResolutionState = ContradictionResolutionState.Resolved,
                    ResolutionNote = "Synthetic records reconciled by reviewer.",
                    ResolvedAt = resolvedAt
                })
                .ToArray()
        });
        var resolvedWorkplace = WorkplaceMatter.Rehydrate(resolvedEvidence, original.Workplace.CaptureSnapshot());
        await store.SaveAsync(resolvedEvidence, resolvedWorkplace);

        var loaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));
        var contradiction = Assert.Single(loaded.Evidence.Contradictions);
        Assert.Equal(ContradictionResolutionState.Resolved, contradiction.ResolutionState);
        Assert.Equal(resolvedAt, contradiction.ResolvedAt);

        var reverseException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(original.Evidence, original.Workplace));
        Assert.Contains("reverse a resolution", reverseException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Wrong_tenant_cannot_read_another_tenants_matter()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenantA, tenantB);
        var tenantBMatter = SyntheticPersistedMatterFactory.Create(tenantB, matterId, 102);
        await store.SaveAsync(tenantBMatter.Evidence, tenantBMatter.Workplace);

        Assert.Null(await store.LoadAsync(tenantA, matterId));
        Assert.NotNull(await store.LoadAsync(tenantB, matterId));
    }

    [PostgresFact]
    public async Task Wrong_tenant_cannot_update_or_delete_another_tenants_matter()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenantA, tenantB);
        var tenantBMatter = SyntheticPersistedMatterFactory.Create(tenantB, matterId, 103);
        await store.SaveAsync(tenantBMatter.Evidence, tenantBMatter.Workplace);

        Assert.False(await store.UpdateMatterAsync(
            tenantA,
            matterId,
            "Cross-tenant overwrite",
            "closed",
            SyntheticPersistedMatterFactory.RecordedAt,
            SyntheticPersistedMatterFactory.RecordedAt.AddDays(1)));
        Assert.False(await store.DeleteMatterAsync(tenantA, matterId));

        var reloaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenantB, matterId));
        Assert.Equal(tenantBMatter.Evidence.Matter.Title, reloaded.Evidence.Matter.Title);
    }

    [PostgresFact]
    public async Task Owning_tenant_can_update_and_delete_but_cannot_move_updated_at_backwards()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var matter = SyntheticPersistedMatterFactory.Create(tenant, matterId, 206);
        await store.SaveAsync(matter.Evidence, matter.Workplace);

        Assert.False(await store.UpdateMatterAsync(
            tenant,
            matterId,
            "Stale synthetic title",
            "closed",
            SyntheticPersistedMatterFactory.RecordedAt.AddMinutes(-1),
            SyntheticPersistedMatterFactory.RecordedAt.AddMinutes(1)));
        Assert.True(await store.UpdateMatterAsync(
            tenant,
            matterId,
            "Updated synthetic title",
            "closed",
            SyntheticPersistedMatterFactory.RecordedAt,
            SyntheticPersistedMatterFactory.RecordedAt.AddMinutes(1)));
        Assert.False(await store.UpdateMatterAsync(
            tenant,
            matterId,
            "Lost synthetic update",
            "open",
            SyntheticPersistedMatterFactory.RecordedAt,
            SyntheticPersistedMatterFactory.RecordedAt.AddMinutes(2)));
        var updated = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));
        Assert.Equal("Updated synthetic title", updated.Evidence.Matter.Title);
        Assert.Equal("closed", updated.Evidence.Matter.Status);

        Assert.True(await store.DeleteMatterAsync(tenant, matterId));
        Assert.Null(await store.LoadAsync(tenant, matterId));
    }

    [PostgresFact]
    public async Task Rls_fails_closed_without_context_and_pool_does_not_leak_tenant_context()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenantA, tenantB);
        var matter = SyntheticPersistedMatterFactory.Create(tenantA, matterId, 104);
        await store.SaveAsync(matter.Evidence, matter.Workplace);

        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            Assert.Equal(0L, await CountVisibleMattersAsync(connection));
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction, tenantA);
            Assert.Equal(1L, await CountVisibleMattersAsync(connection, transaction));
            await transaction.CommitAsync();
            Assert.Equal(0L, await CountVisibleMattersAsync(connection));
        }

        await using (var pooledConnection = await dataSource.OpenConnectionAsync())
        {
            Assert.Equal(0L, await CountVisibleMattersAsync(pooledConnection));
            await using var transaction = await pooledConnection.BeginTransactionAsync();
            await SetTenantAsync(pooledConnection, transaction, tenantB);
            Assert.Equal(0L, await CountVisibleMattersAsync(pooledConnection, transaction));
        }
    }

    [PostgresFact]
    public async Task Cross_tenant_relational_link_is_rejected_by_composite_foreign_key()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var matterId = Guid.NewGuid();
        const int seedA = 105;
        const int seedB = 106;
        await using var store = await CreateStoreAsync(tenantA, tenantB);
        var matterA = SyntheticPersistedMatterFactory.Create(tenantA, matterId, seedA);
        var matterB = SyntheticPersistedMatterFactory.Create(tenantB, matterId, seedB);
        await store.SaveAsync(matterA.Evidence, matterA.Workplace);
        await store.SaveAsync(matterB.Evidence, matterB.Workplace);

        await AssertCrossTenantForeignKeyRejectedAsync(tenantA, """
            INSERT INTO casemesh.assertion_event_links
                (tenant_id, matter_id, link_id, assertion_id, event_id, relation)
            VALUES ($1, $2, $3, $4, $5, 0);
            """, matterId, Guid.NewGuid(), SyntheticPersistedMatterFactory.Id(seedB, 110),
            SyntheticPersistedMatterFactory.Id(seedA, 130));
        await AssertCrossTenantForeignKeyRejectedAsync(tenantA, """
            INSERT INTO casemesh.source_spans (
                tenant_id, matter_id, source_span_id, document_version_id, page_number,
                extracted_text, extracted_text_digest, parser_version, extraction_confidence)
            VALUES ($1, $2, $3, $4, 1, $5, $6, $7, 0.9);
            """, matterId, Guid.NewGuid(), SyntheticPersistedMatterFactory.Id(seedB, 11),
            "Synthetic cross-tenant span", new string('A', 64), "synthetic-parser/1");
        await AssertCrossTenantForeignKeyRejectedAsync(tenantA, """
            INSERT INTO casemesh.employment_term_assertions
                (tenant_id, matter_id, employment_term_id, assertion_id)
            VALUES ($1, $2, $3, $4);
            """, matterId, SyntheticPersistedMatterFactory.Id(seedA, 151),
            SyntheticPersistedMatterFactory.Id(seedB, 120));
    }

    [PostgresFact]
    public async Task Source_backed_assertions_and_contradiction_preserve_full_provenance()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        const int seed = 107;
        await using var store = await CreateStoreAsync(tenant);
        var matter = SyntheticPersistedMatterFactory.Create(tenant, matterId, seed);
        await store.SaveAsync(matter.Evidence, matter.Workplace);

        var loaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));
        var contradiction = Assert.Single(loaded.Evidence.Contradictions);
        var assertions = loaded.Evidence.Assertions.ToDictionary(item => item.Id);
        var spans = loaded.Evidence.SourceSpans.ToDictionary(item => item.Id);
        var twelve = assertions[contradiction.AssertionAId];
        var ten = assertions[contradiction.AssertionBId];

        Assert.Equal("12", twelve.Value);
        Assert.Equal("10", ten.Value);
        foreach (var assertion in new[] { twelve, ten })
        {
            var span = spans[Assert.IsType<Guid>(assertion.SourceSpanId)];
            Assert.Equal(64, span.DocumentVersion.ContentSha256.Length);
            Assert.NotEqual(Guid.Empty, span.DocumentVersion.OriginalObjectId);
        }
    }

    [PostgresFact]
    public async Task Corrected_and_rejected_events_keep_audit_and_historical_provenance_links()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        const int seed = 108;
        await using var store = await CreateStoreAsync(tenant);
        var matter = SyntheticPersistedMatterFactory.Create(tenant, matterId, seed);
        await store.SaveAsync(matter.Evidence, matter.Workplace);

        var loaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));
        var original = Assert.Single(loaded.Evidence.Events, item => item.Id == SyntheticPersistedMatterFactory.Id(seed, 130));
        var corrected = Assert.Single(loaded.Evidence.Events, item => item.Id == SyntheticPersistedMatterFactory.Id(seed, 133));
        Assert.Equal(corrected.Id, original.SupersededByEventId);
        Assert.Equal(original.Id, corrected.SupersedesEventId);
        Assert.Contains(loaded.Evidence.Events, item => item.Status == EventStatus.Rejected);
        var audit = Assert.Single(loaded.Evidence.AuditEvents);
        Assert.Equal(original.Id, audit.EntityId);
        Assert.Equal(corrected.Id, audit.ReplacementEntityId);
        Assert.Contains(loaded.Evidence.AssertionEventLinks, link => link.EventId == original.Id);
        Assert.Contains(loaded.Evidence.AssertionEventLinks, link => link.EventId == corrected.Id);
    }

    [PostgresFact]
    public async Task Workplace_records_remain_distinct_after_round_trip()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        const int seed = 109;
        await using var store = await CreateStoreAsync(tenant);
        var matter = SyntheticPersistedMatterFactory.Create(tenant, matterId, seed);
        await store.SaveAsync(matter.Evidence, matter.Workplace);

        var loaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));
        var terms = loaded.Workplace.EmploymentTerms.OrderBy(term => term.EffectiveFrom).ToArray();
        Assert.Equal(2, terms.Length);
        Assert.Equal("37.5 hours", terms[0].Value);
        Assert.Equal("40 hours", terms[1].Value);
        Assert.Equal(terms[0].Id, terms[1].SupersedesEmploymentTermId);

        var adjustment = Assert.Single(loaded.Workplace.AdjustmentRequests);
        Assert.Single(adjustment.RequestAssertionIds);
        Assert.Single(adjustment.ResponseAssertionIds);
        Assert.Single(adjustment.ImplementationAssertionIds);
        Assert.Empty(adjustment.RequestAssertionIds.Intersect(adjustment.ResponseAssertionIds));
        Assert.Empty(adjustment.ResponseAssertionIds.Intersect(adjustment.ImplementationAssertionIds));

        var oh = Assert.Single(loaded.Workplace.HealthAbsenceRecords,
            record => record.Kind == HealthAbsenceKind.OccupationalHealthRecommendation);
        Assert.DoesNotContain(oh.AssertionIds.Single(), adjustment.ImplementationAssertionIds);
    }

    [PostgresFact]
    public async Task Duplicate_hash_versions_reuse_the_logical_original_without_losing_versions()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var matter = SyntheticPersistedMatterFactory.Create(tenant, matterId, 110);
        await store.SaveAsync(matter.Evidence, matter.Workplace);

        var loaded = Assert.IsType<PersistedMatter>(await store.LoadAsync(tenant, matterId));
        var duplicates = loaded.Evidence.DocumentVersions
            .GroupBy(version => version.ContentSha256)
            .Single(group => group.Count() == 2)
            .ToArray();
        Assert.Equal(2, duplicates.Select(item => item.DocumentVersionId).Distinct().Count());
        Assert.Single(duplicates.Select(item => item.OriginalObjectId).Distinct());
    }

    [PostgresFact]
    public async Task Audit_history_cannot_be_overwritten_or_deleted()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var matter = SyntheticPersistedMatterFactory.Create(tenant, matterId, 111);
        await store.SaveAsync(matter.Evidence, matter.Workplace);

        var evidenceSnapshot = matter.Evidence.CaptureSnapshot();
        var audit = Assert.Single(evidenceSnapshot.AuditEvents);
        var changedEvidence = MatterEvidenceGraph.Rehydrate(evidenceSnapshot with
        {
            AuditEvents = [audit with { ChangeSummary = "Attempted overwrite" }]
        });
        var changedWorkplace = WorkplaceMatter.Rehydrate(changedEvidence, matter.Workplace.CaptureSnapshot());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(changedEvidence, changedWorkplace));

        await AssertAuditMutationRejectedAsync(tenant, matterId, "UPDATE casemesh.audit_events SET change_summary = 'overwrite' WHERE tenant_id = $1 AND matter_id = $2;");
        await AssertAuditMutationRejectedAsync(tenant, matterId, "DELETE FROM casemesh.audit_events WHERE tenant_id = $1 AND matter_id = $2;");

        await using var admin = new NpgsqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var truncate = new NpgsqlCommand("TRUNCATE casemesh.audit_events CASCADE;", admin);
        var truncateException = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, truncateException.SqlState);
    }

    [PostgresFact]
    public async Task Invalid_persisted_enum_is_rejected_by_schema_constraint()
    {
        var tenant = NewTenant();
        var matterId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenant);
        var matter = SyntheticPersistedMatterFactory.Create(tenant, matterId, 112);
        await store.SaveAsync(matter.Evidence, matter.Workplace);

        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenant);
        await using var command = new NpgsqlCommand("""
            UPDATE casemesh.assertions SET verification_state = 32767
            WHERE tenant_id = $1 AND matter_id = $2;
            """, connection, transaction);
        command.Parameters.AddWithValue(tenant.Value);
        command.Parameters.AddWithValue(matterId);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [PostgresFact]
    public async Task Tenant_cleanup_does_not_affect_another_tenant()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var matterAId = Guid.NewGuid();
        var matterBId = Guid.NewGuid();
        await using var store = await CreateStoreAsync(tenantA, tenantB);
        var matterA = SyntheticPersistedMatterFactory.Create(tenantA, matterAId, 113);
        var matterB = SyntheticPersistedMatterFactory.Create(tenantB, matterBId, 114);
        await store.SaveAsync(matterA.Evidence, matterA.Workplace);
        await store.SaveAsync(matterB.Evidence, matterB.Workplace);

        Assert.True(await store.DeleteTenantAsync(tenantA));
        Assert.Null(await store.LoadAsync(tenantA, matterAId));
        Assert.NotNull(await store.LoadAsync(tenantB, matterBId));
    }

    private async Task<PostgresMatterStore> CreateStoreAsync(params TenantId[] tenants)
    {
        var store = new PostgresMatterStore(database.AppConnectionString);
        foreach (var tenant in tenants)
        {
            await store.CreateTenantAsync(tenant, $"Synthetic tenant {tenant.Value:N}", SyntheticPersistedMatterFactory.RecordedAt);
        }

        return store;
    }

    private async Task AssertAuditMutationRejectedAsync(TenantId tenant, Guid matterId, string sql)
    {
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenant);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(tenant.Value);
        command.Parameters.AddWithValue(matterId);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
    }

    private async Task AssertCrossTenantForeignKeyRejectedAsync(
        TenantId tenant,
        string sql,
        params object[] values)
    {
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenant);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(tenant.Value);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        PostgresException exception;
        try
        {
            await command.ExecuteNonQueryAsync();
            exception = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        }
        catch (PostgresException executeException)
        {
            exception = executeException;
        }

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    private static async Task SetTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenant)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('casemesh.tenant_id', $1, true);", connection, transaction);
        command.Parameters.AddWithValue(tenant.Value.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountVisibleMattersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM casemesh.matters;", connection, transaction);
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("COUNT returned null."));
    }

    private static TenantId NewTenant() => new(Guid.NewGuid());
}
