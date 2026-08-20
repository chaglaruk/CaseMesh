using CaseMesh.Core.Models;
using CaseMesh.MatterBrain;
using Npgsql;

namespace CaseMesh.Persistence.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMatterBrainStoreTests(PostgresFixture database)
{
    [PostgresFact]
    public async Task Complete_candidate_and_canonical_state_round_trips_with_provenance()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(810, 1));
        var persisted = SyntheticPersistedMatterFactory.Create(
            tenant, SyntheticPersistedMatterFactory.Id(810, 2), 810);
        var state = await CreateBrainAsync(persisted);
        var laterSource = persisted.Evidence.SourceSpans.Skip(2).First();
        await new MatterBrainMergeService(new FixedTimeProvider(SyntheticPersistedMatterFactory.RecordedAt.AddHours(2)))
            .ExtractAndMergeAsync(state, [laterSource.Id], new GoldenProvider(
                new StructuredCandidateBatch(
                    [new EntityCandidate("surname-reference", CanonicalEntityKind.Person, "Morgan", "person",
                        ["the employee"], ["employee"], [laterSource.Id], 0.8m)],
                    [], [], [], [], [], []),
                "extract/v2"));
        var people = state.People.OrderBy(item => item.DisplayName).ToArray();
        var proposal = state.ProposeEntityMerge(
            SyntheticPersistedMatterFactory.Id(810, 300), CanonicalEntityKind.Person,
            people[0].Id, people[1].Id, [persisted.Evidence.SourceSpans.First().Id], 0.64m,
            "synthetic-reviewer", SyntheticPersistedMatterFactory.RecordedAt.AddHours(2));
        var accepted = state.AcceptEntityMerge(
            SyntheticPersistedMatterFactory.Id(810, 301), proposal.Id,
            "synthetic-reviewer", SyntheticPersistedMatterFactory.RecordedAt.AddHours(3));
        state.ReverseEntityMerge(
            SyntheticPersistedMatterFactory.Id(810, 302), accepted.Id,
            "synthetic-reviewer", SyntheticPersistedMatterFactory.RecordedAt.AddHours(4));

        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        await matterStore.CreateTenantAsync(tenant, "Synthetic tenant", SyntheticPersistedMatterFactory.RecordedAt);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(state, persisted.Workplace);
        var loaded = await store.LoadAsync(tenant, persisted.Evidence.Matter.Id);

        Assert.NotNull(loaded);
        Assert.Equal(state.People.Count, loaded.Brain.People.Count);
        Assert.Equal(state.Organisations.Count, loaded.Brain.Organisations.Count);
        Assert.Equal(state.Aliases.Count, loaded.Brain.Aliases.Count);
        Assert.Equal(state.Communications.Count, loaded.Brain.Communications.Count);
        Assert.Equal(state.Runs.Count, loaded.Brain.Runs.Count);
        Assert.Equal(state.Candidates.Count, loaded.Brain.Candidates.Count);
        Assert.Equal(state.Dependencies.Count, loaded.Brain.Dependencies.Count);
        Assert.Equal(3, loaded.Brain.EntityResolutionActions.Count);
        Assert.Equal(people[0].Id, loaded.Brain.ResolveEntityId(CanonicalEntityKind.Person, people[0].Id));
        Assert.All(loaded.Brain.Candidates, candidate => Assert.Equal(64, candidate.PayloadDigest.Length));
        Assert.Equal(100m, MatterBrainEvaluation.Evaluate(loaded.Brain).SourceLinkValidityPercent);

        var employerAssertion = loaded.Evidence.Assertions.Single(item =>
            item.Predicate == "extracted-sickness-count" && item.Value == "12");
        var span = loaded.Evidence.SourceSpans.Single(item => item.Id == employerAssertion.SourceSpanId);
        var version = loaded.Evidence.DocumentVersions.Single(item =>
            item.DocumentVersionId == span.DocumentVersion.DocumentVersionId);
        Assert.Equal(span.DocumentVersion.OriginalObjectId, version.OriginalObjectId);
        Assert.Equal(64, version.ContentSha256.Length);
        Assert.Single(loaded.Brain.Aliases, item =>
            item.EntityId == people[0].Id && item.NormalizedValue == "MORGAN");
    }

    [PostgresFact]
    public async Task Wrong_tenant_and_missing_context_cannot_resolve_Matter_Brain_state()
    {
        var tenantA = new TenantId(SyntheticPersistedMatterFactory.Id(811, 1));
        var tenantB = new TenantId(SyntheticPersistedMatterFactory.Id(811, 2));
        var persisted = SyntheticPersistedMatterFactory.Create(
            tenantA, SyntheticPersistedMatterFactory.Id(811, 3), 811);
        var state = await CreateBrainAsync(persisted);
        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        await matterStore.CreateTenantAsync(tenantA, "Tenant A", SyntheticPersistedMatterFactory.RecordedAt);
        await matterStore.CreateTenantAsync(tenantB, "Tenant B", SyntheticPersistedMatterFactory.RecordedAt);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(state, persisted.Workplace);

        Assert.Null(await store.LoadAsync(tenantB, persisted.Evidence.Matter.Id));
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM casemesh.extraction_candidates;", connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Cross_tenant_candidate_entity_reference_is_rejected_by_composite_foreign_key()
    {
        var tenantA = new TenantId(SyntheticPersistedMatterFactory.Id(812, 1));
        var tenantB = new TenantId(SyntheticPersistedMatterFactory.Id(812, 2));
        var matterA = SyntheticPersistedMatterFactory.Create(tenantA, SyntheticPersistedMatterFactory.Id(812, 3), 812);
        var matterB = SyntheticPersistedMatterFactory.Create(tenantB, SyntheticPersistedMatterFactory.Id(812, 4), 813);
        var stateA = await CreateBrainAsync(matterA);
        var stateB = await CreateBrainAsync(matterB);
        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        await matterStore.CreateTenantAsync(tenantA, "Tenant A", SyntheticPersistedMatterFactory.RecordedAt);
        await matterStore.CreateTenantAsync(tenantB, "Tenant B", SyntheticPersistedMatterFactory.RecordedAt);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(stateA, matterA.Workplace);
        await store.SaveAsync(stateB, matterB.Workplace);

        var run = stateA.Runs.Single();
        var personB = stateB.People.First().Id;
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenantA);
        await using var command = new NpgsqlCommand("""
            INSERT INTO casemesh.extraction_candidates
                (tenant_id,matter_id,candidate_id,extraction_run_id,external_key,candidate_kind,
                 disposition,canonical_kind,person_id,payload_json,payload_digest)
            VALUES ($1,$2,$3,$4,'cross-tenant-person',0,0,0,$5,'{}'::jsonb,$6);
            """, connection, transaction);
        command.Parameters.AddWithValue(tenantA.Value);
        command.Parameters.AddWithValue(matterA.Evidence.Matter.Id);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(run.Id);
        command.Parameters.AddWithValue(personB);
        command.Parameters.AddWithValue(new string('A', 64));
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        await transaction.RollbackAsync();
    }

    [PostgresFact]
    public async Task Matter_Brain_history_cannot_be_updated_deleted_or_truncated()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(814, 1));
        var persisted = SyntheticPersistedMatterFactory.Create(
            tenant, SyntheticPersistedMatterFactory.Id(814, 2), 814);
        var state = await CreateBrainAsync(persisted);
        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        await matterStore.CreateTenantAsync(tenant, "Synthetic tenant", SyntheticPersistedMatterFactory.RecordedAt);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(state, persisted.Workplace);

        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenant);
        var runId = state.Runs.Single().Id;
        await using (var update = new NpgsqlCommand(
                         "UPDATE casemesh.extraction_runs SET model='changed' WHERE tenant_id=$1 AND matter_id=$2 AND extraction_run_id=$3;",
                         connection, transaction))
        {
            update.Parameters.AddWithValue(tenant.Value);
            update.Parameters.AddWithValue(persisted.Evidence.Matter.Id);
            update.Parameters.AddWithValue(runId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Contains("append-only", exception.MessageText, StringComparison.OrdinalIgnoreCase);
        }
        await transaction.RollbackAsync();

        await using var admin = new NpgsqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var truncate = new NpgsqlCommand("TRUNCATE casemesh.extraction_runs CASCADE;", admin);
        var truncateException = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
        Assert.Contains("append-only", truncateException.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Undefined_candidate_state_is_rejected_by_schema()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(815, 1));
        var persisted = SyntheticPersistedMatterFactory.Create(
            tenant, SyntheticPersistedMatterFactory.Id(815, 2), 815);
        var state = await CreateBrainAsync(persisted);
        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        await matterStore.CreateTenantAsync(tenant, "Synthetic tenant", SyntheticPersistedMatterFactory.RecordedAt);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(state, persisted.Workplace);

        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenant);
        var run = state.Runs.Single();
        await using var command = new NpgsqlCommand("""
            INSERT INTO casemesh.extraction_candidates
                (tenant_id,matter_id,candidate_id,extraction_run_id,external_key,candidate_kind,disposition,
                 rejection_code,payload_json,payload_digest)
            VALUES ($1,$2,$3,$4,'invalid',99,1,'invalid-state','{}'::jsonb,$5);
            """, connection, transaction);
        command.Parameters.AddWithValue(tenant.Value);
        command.Parameters.AddWithValue(persisted.Evidence.Matter.Id);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(run.Id);
        command.Parameters.AddWithValue(new string('A', 64));
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        await transaction.RollbackAsync();
    }

    [PostgresFact]
    public async Task Candidate_with_cross_Matter_source_is_rejected_by_composite_foreign_key()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(816, 1));
        var first = SyntheticPersistedMatterFactory.Create(tenant, SyntheticPersistedMatterFactory.Id(816, 2), 816);
        var second = SyntheticPersistedMatterFactory.Create(tenant, SyntheticPersistedMatterFactory.Id(816, 3), 817);
        var firstState = await CreateBrainAsync(first);
        var secondState = await CreateBrainAsync(second);
        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        await matterStore.CreateTenantAsync(tenant, "Synthetic tenant", SyntheticPersistedMatterFactory.RecordedAt);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(firstState, first.Workplace);
        await store.SaveAsync(secondState, second.Workplace);

        var candidate = firstState.Candidates.First();
        var foreignSource = second.Evidence.SourceSpans.First().Id;
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenant);
        await using var command = new NpgsqlCommand("""
            INSERT INTO casemesh.extraction_candidate_sources
                (tenant_id,matter_id,candidate_id,source_span_id,ordinal)
            VALUES ($1,$2,$3,$4,999);
            """, connection, transaction);
        command.Parameters.AddWithValue(tenant.Value);
        command.Parameters.AddWithValue(first.Evidence.Matter.Id);
        command.Parameters.AddWithValue(candidate.Id);
        command.Parameters.AddWithValue(foreignSource);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        await transaction.RollbackAsync();
    }

    [PostgresFact]
    public async Task Dependency_candidate_must_belong_to_run_and_cite_source()
    {
        var tenant = new TenantId(SyntheticPersistedMatterFactory.Id(818, 1));
        var persisted = SyntheticPersistedMatterFactory.Create(
            tenant, SyntheticPersistedMatterFactory.Id(818, 2), 818);
        var state = await CreateBrainAsync(persisted);
        await using var matterStore = new PostgresMatterStore(database.AppConnectionString);
        await matterStore.CreateTenantAsync(tenant, "Synthetic tenant", SyntheticPersistedMatterFactory.RecordedAt);
        await using var store = new PostgresMatterBrainStore(database.AppConnectionString);
        await store.SaveAsync(state, persisted.Workplace);

        var candidate = state.Candidates.Single(item => item.ExternalKey == "twelve");
        var citedSource = candidate.SourceSpanIds.Single();
        var uncitedSource = persisted.Evidence.SourceSpans.First(item => item.Id != citedSource);
        var secondRunId = SyntheticPersistedMatterFactory.Id(818, 400);
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, tenant);
        await using (var run = new NpgsqlCommand("""
                         INSERT INTO casemesh.extraction_runs
                             (tenant_id,matter_id,extraction_run_id,fingerprint,provider,model,extraction_version,
                              prompt_version,schema_version,generated_at,raw_result_digest)
                         VALUES ($1,$2,$3,$4,'synthetic-provider','golden-model','extract/v2',
                                 'prompt/v1','schema/v1',$5,$6);
                         """, connection, transaction))
        {
            run.Parameters.AddWithValue(tenant.Value);
            run.Parameters.AddWithValue(persisted.Evidence.Matter.Id);
            run.Parameters.AddWithValue(secondRunId);
            run.Parameters.AddWithValue(new string('B', 64));
            run.Parameters.AddWithValue(SyntheticPersistedMatterFactory.RecordedAt.AddHours(2));
            run.Parameters.AddWithValue(new string('C', 64));
            await run.ExecuteNonQueryAsync();
        }

        await using (var wrongRun = DependencyCommand(
                         connection, transaction, tenant, persisted.Evidence.Matter.Id,
                         SyntheticPersistedMatterFactory.Id(818, 401), secondRunId,
                         citedSource, candidate.Id, candidate.CanonicalId!.Value))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => wrongRun.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        }
        await transaction.RollbackAsync();

        await using var sourceTransaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, sourceTransaction, tenant);
        await using var wrongSource = DependencyCommand(
            connection, sourceTransaction, tenant, persisted.Evidence.Matter.Id,
            SyntheticPersistedMatterFactory.Id(818, 402), candidate.RunId,
            uncitedSource.Id, candidate.Id, candidate.CanonicalId!.Value);
        var sourceException = await Assert.ThrowsAsync<PostgresException>(() => wrongSource.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, sourceException.SqlState);
        await sourceTransaction.RollbackAsync();
    }

    private static async Task<MatterBrainState> CreateBrainAsync(PersistedMatter persisted)
    {
        var sources = persisted.Evidence.SourceSpans.Take(2).ToArray();
        var batch = new StructuredCandidateBatch(
            [
                new EntityCandidate("employee", CanonicalEntityKind.Person, "Alex Morgan", "person",
                    ["Morgan"], ["employee"], [sources[0].Id], 0.91m),
                new EntityCandidate("similar", CanonicalEntityKind.Person, "Alexa Morgan", "person",
                    ["A. Morgan"], ["manager"], [sources[1].Id], 0.81m),
                new EntityCandidate("employer", CanonicalEntityKind.Organisation, "Example Employer", "employer",
                    ["the employer"], [], [sources[0].Id], 0.96m)
            ],
            [new CommunicationCandidate("letter", CommunicationKind.Letter, "Synthetic capability letter",
                SyntheticPersistedMatterFactory.RecordedAt, "employer", ["employee", "employer"],
                [sources[0].Id], 0.9m)],
            [
                Assertion("twelve", sources[0], "12", EvidenceOriginClass.EmployerAuthoredDocument, AssertionClass.EmployerAssertion, "Example Employer"),
                Assertion("ten", sources[1], "10", EvidenceOriginClass.OriginalContemporaneousRecord, AssertionClass.DerivedCalculation, "Synthetic attendance")
            ],
            [new EventCandidate("absence", "reported-absence", "Reported absence remains disputed",
                null, null, ["employee", "employer"], sources.Select(item => item.Id).ToArray(), 0.7m)],
            [
                new AssertionEventLinkCandidate("link-twelve", "twelve", "absence", AssertionEventRelation.Supports, [sources[0].Id], 0.9m),
                new AssertionEventLinkCandidate("link-ten", "ten", "absence", AssertionEventRelation.Supports, [sources[1].Id], 0.9m)
            ],
            [], []);
        var state = new MatterBrainState(persisted.Evidence);
        await new MatterBrainMergeService(new FixedTimeProvider(SyntheticPersistedMatterFactory.RecordedAt.AddHours(1)))
            .ExtractAndMergeAsync(state, sources.Select(item => item.Id).ToArray(), new GoldenProvider(batch));
        return state;
    }

    private static AssertionCandidate Assertion(
        string key,
        SourceSpan source,
        string value,
        EvidenceOriginClass origin,
        AssertionClass assertionClass,
        string assertedBy) => new(
        key, "synthetic-employee", "extracted-sickness-count", value, assertedBy,
        SyntheticPersistedMatterFactory.RecordedAt, null, source.Id, origin, assertionClass,
        IntegrityState.OriginalHashVerified, [source.Id], 0.9m);

    private static async Task SetTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId)
    {
        await using var context = new NpgsqlCommand(
            "SELECT set_config('casemesh.tenant_id',$1,true);", connection, transaction);
        context.Parameters.AddWithValue(tenantId.Value.ToString());
        await context.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand DependencyCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenant,
        Guid matterId,
        Guid dependencyId,
        Guid runId,
        Guid sourceSpanId,
        Guid candidateId,
        Guid assertionId)
    {
        var command = new NpgsqlCommand("""
            INSERT INTO casemesh.matter_brain_dependencies
                (tenant_id,matter_id,dependency_id,extraction_run_id,source_span_id,candidate_id,
                 canonical_kind,assertion_id)
            VALUES ($1,$2,$3,$4,$5,$6,3,$7);
            """, connection, transaction);
        command.Parameters.AddWithValue(tenant.Value);
        command.Parameters.AddWithValue(matterId);
        command.Parameters.AddWithValue(dependencyId);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(sourceSpanId);
        command.Parameters.AddWithValue(candidateId);
        command.Parameters.AddWithValue(assertionId);
        return command;
    }

    private sealed class GoldenProvider(
        StructuredCandidateBatch batch,
        string extractionVersion = "extract/v1") : IStructuredExtractionProvider
    {
        public StructuredExtractionProviderDescriptor Descriptor { get; } =
            new("synthetic-provider", "golden-model", extractionVersion, "prompt/v1", "schema/v1");

        public Task<StructuredExtractionOutput> ExtractAsync(
            StructuredExtractionInput input,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StructuredExtractionOutput("{\"synthetic\":true}", batch));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
