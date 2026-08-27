using CaseMesh.Core.Models;
using CaseMesh.Core.Services;
using CaseMesh.Core.Workplace;
using CaseMesh.MatterBrain;
using Npgsql;

namespace CaseMesh.Persistence.Postgres;

public sealed record PersistedMatterBrain(
    MatterEvidenceGraph Evidence,
    WorkplaceMatter Workplace,
    MatterBrainState Brain);

public sealed class PostgresMatterBrainStore : IAsyncDisposable
{
    private readonly PostgresMatterStore _matterStore;

    public PostgresMatterBrainStore(string connectionString)
    {
        _matterStore = new PostgresMatterStore(connectionString);
    }

    public Task SaveAsync(
        MatterBrainState brain,
        WorkplaceMatter workplace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(workplace);
        if (!ReferenceEquals(brain.Evidence, workplace.Evidence) || workplace.MatterId != brain.MatterId)
        {
            throw new InvalidOperationException("Matter Brain, evidence, and workplace state must share one Matter aggregate.");
        }

        var evidence = brain.Evidence.CaptureSnapshot();
        var work = workplace.CaptureSnapshot();
        var snapshot = brain.CaptureSnapshot();
        return _matterStore.InTenantTransactionAsync(brain.Evidence.Matter.TenantId, async (connection, transaction) =>
        {
            await PostgresMatterWriter.WriteAsync(connection, transaction, evidence, work, cancellationToken);
            await WriteAsync(connection, transaction, brain.Evidence.Matter.TenantId.Value, brain.MatterId, snapshot, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<PersistedMatterBrain?> LoadAsync(
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken = default)
    {
        if (matterId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty Matter id is required.", nameof(matterId));
        }

        return _matterStore.InTenantTransactionAsync(tenantId, async (connection, transaction) =>
            await ReadPersistedAsync(connection, transaction, tenantId, matterId, cancellationToken),
            cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _matterStore.DisposeAsync();

    private static async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid matterId,
        MatterBrainSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.MatterId != matterId)
        {
            throw new InvalidOperationException("Matter Brain snapshot ownership changed before persistence.");
        }

        var personIds = snapshot.People.Select(item => item.Id).ToHashSet();
        var organisationIds = snapshot.Organisations.Select(item => item.Id).ToHashSet();
        if (personIds.Overlaps(organisationIds))
        {
            throw new InvalidOperationException("A canonical entity id cannot identify both a person and an organisation.");
        }

        foreach (var person in snapshot.People)
        {
            await EnsureAsync(connection, transaction,
                "INSERT INTO casemesh.people (tenant_id,matter_id,person_id,display_name) VALUES ($1,$2,$3,$4) ON CONFLICT DO NOTHING;",
                "SELECT EXISTS (SELECT 1 FROM casemesh.people WHERE tenant_id=$1 AND matter_id=$2 AND person_id=$3 AND display_name=$4);",
                cancellationToken, tenantId, matterId, person.Id, person.DisplayName);
            for (var ordinal = 0; ordinal < person.RoleLabels.Count; ordinal++)
            {
                await EnsureAsync(connection, transaction,
                    "INSERT INTO casemesh.person_roles (tenant_id,matter_id,person_id,ordinal,role_label) VALUES ($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING;",
                    "SELECT EXISTS (SELECT 1 FROM casemesh.person_roles WHERE tenant_id=$1 AND matter_id=$2 AND person_id=$3 AND ordinal=$4 AND role_label=$5);",
                    cancellationToken, tenantId, matterId, person.Id, ordinal, person.RoleLabels[ordinal]);
            }
        }

        foreach (var organisation in snapshot.Organisations)
        {
            await EnsureAsync(connection, transaction,
                "INSERT INTO casemesh.organisations (tenant_id,matter_id,organisation_id,name,type_label) VALUES ($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING;",
                "SELECT EXISTS (SELECT 1 FROM casemesh.organisations WHERE tenant_id=$1 AND matter_id=$2 AND organisation_id=$3 AND name=$4 AND type_label=$5);",
                cancellationToken, tenantId, matterId, organisation.Id, organisation.Name, organisation.TypeLabel);
        }

        foreach (var alias in snapshot.Aliases)
        {
            var personId = alias.EntityKind == CanonicalEntityKind.Person ? alias.EntityId : (Guid?)null;
            var organisationId = alias.EntityKind == CanonicalEntityKind.Organisation ? alias.EntityId : (Guid?)null;
            await EnsureAsync(connection, transaction,
                """
                INSERT INTO casemesh.entity_aliases
                    (tenant_id,matter_id,alias_id,entity_kind,person_id,organisation_id,alias_value,normalized_value,source_span_id)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT DO NOTHING;
                """,
                """
                SELECT EXISTS (SELECT 1 FROM casemesh.entity_aliases
                    WHERE tenant_id=$1 AND matter_id=$2 AND alias_id=$3 AND entity_kind=$4
                      AND person_id IS NOT DISTINCT FROM $5 AND organisation_id IS NOT DISTINCT FROM $6
                      AND alias_value=$7 AND normalized_value=$8 AND source_span_id IS NOT DISTINCT FROM $9);
                """,
                cancellationToken, tenantId, matterId, alias.Id, (short)alias.EntityKind,
                personId, organisationId, alias.Value, alias.NormalizedValue, alias.SourceSpanId);
        }

        foreach (var communication in snapshot.Communications)
        {
            var senderPerson = communication.SenderEntityId.HasValue && personIds.Contains(communication.SenderEntityId.Value)
                ? communication.SenderEntityId : null;
            var senderOrganisation = communication.SenderEntityId.HasValue && organisationIds.Contains(communication.SenderEntityId.Value)
                ? communication.SenderEntityId : null;
            if (communication.SenderEntityId.HasValue && !senderPerson.HasValue && !senderOrganisation.HasValue)
            {
                throw new InvalidOperationException(
                    $"Communication {communication.Id:N} references an unregistered sender entity.");
            }
            await EnsureAsync(connection, transaction,
                """
                INSERT INTO casemesh.communications
                    (tenant_id,matter_id,communication_id,communication_kind,neutral_label,occurred_at,
                     sender_person_id,sender_organisation_id,verification_state)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT DO NOTHING;
                """,
                """
                SELECT EXISTS (SELECT 1 FROM casemesh.communications
                    WHERE tenant_id=$1 AND matter_id=$2 AND communication_id=$3 AND communication_kind=$4
                      AND neutral_label=$5 AND occurred_at IS NOT DISTINCT FROM $6
                      AND sender_person_id IS NOT DISTINCT FROM $7 AND sender_organisation_id IS NOT DISTINCT FROM $8
                      AND verification_state=$9);
                """,
                cancellationToken, tenantId, matterId, communication.Id, (short)communication.Kind,
                communication.NeutralLabel, communication.OccurredAt, senderPerson, senderOrganisation,
                (short)communication.VerificationState);
            for (var ordinal = 0; ordinal < communication.ParticipantEntityIds.Count; ordinal++)
            {
                var participantId = communication.ParticipantEntityIds[ordinal];
                var isPerson = personIds.Contains(participantId);
                var isOrganisation = organisationIds.Contains(participantId);
                if (!isPerson && !isOrganisation)
                {
                    throw new InvalidOperationException(
                        $"Communication {communication.Id:N} references an unregistered participant entity.");
                }

                await EnsureAsync(connection, transaction,
                    """
                    INSERT INTO casemesh.communication_participants
                        (tenant_id,matter_id,communication_id,participant_kind,person_id,organisation_id,ordinal)
                    VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING;
                    """,
                    """
                    SELECT EXISTS (SELECT 1 FROM casemesh.communication_participants
                        WHERE tenant_id=$1 AND matter_id=$2 AND communication_id=$3 AND participant_kind=$4
                          AND person_id IS NOT DISTINCT FROM $5 AND organisation_id IS NOT DISTINCT FROM $6
                          AND ordinal=$7);
                    """,
                    cancellationToken, tenantId, matterId, communication.Id, (short)(isPerson ? 0 : 1),
                    isPerson ? participantId : null, isPerson ? null : participantId, ordinal);
            }

            await WriteOrderedSourcesAsync(connection, transaction, "communication_sources", "communication_id",
                tenantId, matterId, communication.Id, communication.SourceSpanIds, cancellationToken);
        }

        foreach (var run in snapshot.Runs)
        {
            await EnsureAsync(connection, transaction,
                """
                INSERT INTO casemesh.extraction_runs
                    (tenant_id,matter_id,extraction_run_id,fingerprint,provider,model,extraction_version,
                     prompt_version,schema_version,generated_at,raw_result_digest,run_sequence)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) ON CONFLICT DO NOTHING;
                """,
                """
                SELECT EXISTS (SELECT 1 FROM casemesh.extraction_runs
                    WHERE tenant_id=$1 AND matter_id=$2 AND extraction_run_id=$3 AND fingerprint=$4
                      AND provider=$5 AND model=$6 AND extraction_version=$7 AND prompt_version=$8
                      AND schema_version=$9 AND generated_at=$10 AND raw_result_digest=$11
                      AND run_sequence IS NOT DISTINCT FROM $12);
                """,
                cancellationToken, tenantId, matterId, run.Id, run.Fingerprint, run.Provider.Provider,
                run.Provider.Model, run.Provider.ExtractionVersion, run.Provider.PromptVersion,
                run.Provider.SchemaVersion, run.GeneratedAt, run.RawResultDigest, run.Sequence);
            await WriteOrderedSourcesAsync(connection, transaction, "extraction_run_sources", "extraction_run_id",
                tenantId, matterId, run.Id, run.SourceSpanIds, cancellationToken);
        }

        foreach (var candidate in snapshot.Candidates)
        {
            var links = CanonicalLinks(candidate.CanonicalKind, candidate.CanonicalId);
            await EnsureAsync(connection, transaction,
                """
                INSERT INTO casemesh.extraction_candidates
                    (tenant_id,matter_id,candidate_id,extraction_run_id,external_key,candidate_kind,disposition,
                     rejection_code,extraction_confidence,canonical_kind,person_id,organisation_id,communication_id,
                     assertion_id,event_id,assertion_event_link_id,contradiction_id,payload_json,payload_digest)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18::jsonb,$19)
                ON CONFLICT DO NOTHING;
                """,
                """
                SELECT EXISTS (SELECT 1 FROM casemesh.extraction_candidates
                    WHERE tenant_id=$1 AND matter_id=$2 AND candidate_id=$3 AND extraction_run_id=$4
                      AND external_key=$5 AND candidate_kind=$6 AND disposition=$7
                      AND rejection_code IS NOT DISTINCT FROM $8 AND extraction_confidence IS NOT DISTINCT FROM $9
                      AND canonical_kind IS NOT DISTINCT FROM $10 AND person_id IS NOT DISTINCT FROM $11
                      AND organisation_id IS NOT DISTINCT FROM $12 AND communication_id IS NOT DISTINCT FROM $13
                      AND assertion_id IS NOT DISTINCT FROM $14 AND event_id IS NOT DISTINCT FROM $15
                      AND assertion_event_link_id IS NOT DISTINCT FROM $16 AND contradiction_id IS NOT DISTINCT FROM $17
                      AND payload_json=$18::jsonb AND payload_digest=$19);
                """,
                cancellationToken, tenantId, matterId, candidate.Id, candidate.RunId, candidate.ExternalKey,
                (short)candidate.Kind, (short)candidate.Disposition, candidate.RejectionCode,
                candidate.ExtractionConfidence, candidate.CanonicalKind.HasValue ? (short)candidate.CanonicalKind.Value : null,
                links.PersonId, links.OrganisationId, links.CommunicationId, links.AssertionId,
                links.EventId, links.LinkId, links.ContradictionId, candidate.PayloadJson, candidate.PayloadDigest);
            await WriteOrderedSourcesAsync(connection, transaction, "extraction_candidate_sources", "candidate_id",
                tenantId, matterId, candidate.Id, candidate.SourceSpanIds, cancellationToken);
        }

        foreach (var dependency in snapshot.Dependencies)
        {
            var links = CanonicalLinks(dependency.CanonicalKind, dependency.CanonicalId);
            await EnsureAsync(connection, transaction,
                """
                INSERT INTO casemesh.matter_brain_dependencies
                    (tenant_id,matter_id,dependency_id,extraction_run_id,source_span_id,candidate_id,canonical_kind,
                     person_id,organisation_id,communication_id,assertion_id,event_id,assertion_event_link_id,
                     contradiction_id,analysis_node_id)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15) ON CONFLICT DO NOTHING;
                """,
                """
                SELECT EXISTS (SELECT 1 FROM casemesh.matter_brain_dependencies
                    WHERE tenant_id=$1 AND matter_id=$2 AND dependency_id=$3 AND extraction_run_id=$4
                      AND source_span_id=$5 AND candidate_id=$6 AND canonical_kind=$7
                      AND person_id IS NOT DISTINCT FROM $8 AND organisation_id IS NOT DISTINCT FROM $9
                      AND communication_id IS NOT DISTINCT FROM $10 AND assertion_id IS NOT DISTINCT FROM $11
                      AND event_id IS NOT DISTINCT FROM $12 AND assertion_event_link_id IS NOT DISTINCT FROM $13
                      AND contradiction_id IS NOT DISTINCT FROM $14 AND analysis_node_id IS NOT DISTINCT FROM $15);
                """,
                cancellationToken, tenantId, matterId, dependency.Id, dependency.RunId,
                dependency.SourceSpanId, dependency.CandidateId, (short)dependency.CanonicalKind,
                links.PersonId, links.OrganisationId, links.CommunicationId, links.AssertionId,
                links.EventId, links.LinkId, links.ContradictionId, links.AnalysisNodeId);
        }

        foreach (var invalidation in snapshot.DependencyInvalidations)
        {
            await EnsureAsync(connection, transaction,
                """
                INSERT INTO casemesh.dependency_invalidations
                    (tenant_id,matter_id,invalidation_id,dependency_id,invalidated_by_run_id,
                     invalidated_by_audit_event_id,invalidated_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING;
                """,
                """
                SELECT EXISTS (SELECT 1 FROM casemesh.dependency_invalidations
                    WHERE tenant_id=$1 AND matter_id=$2 AND invalidation_id=$3 AND dependency_id=$4
                      AND invalidated_by_run_id IS NOT DISTINCT FROM $5
                      AND invalidated_by_audit_event_id IS NOT DISTINCT FROM $6 AND invalidated_at=$7);
                """,
                cancellationToken, tenantId, matterId, invalidation.Id, invalidation.DependencyId,
                invalidation.InvalidatedByRunId, invalidation.InvalidatedByAuditEventId, invalidation.InvalidatedAt);
        }

        foreach (var action in snapshot.EntityResolutionActions)
        {
            var sourcePerson = action.EntityKind == CanonicalEntityKind.Person ? action.SourceEntityId : (Guid?)null;
            var targetPerson = action.EntityKind == CanonicalEntityKind.Person ? action.TargetEntityId : (Guid?)null;
            var sourceOrganisation = action.EntityKind == CanonicalEntityKind.Organisation ? action.SourceEntityId : (Guid?)null;
            var targetOrganisation = action.EntityKind == CanonicalEntityKind.Organisation ? action.TargetEntityId : (Guid?)null;
            await EnsureAsync(connection, transaction,
                """
                INSERT INTO casemesh.entity_resolution_actions
                    (tenant_id,matter_id,action_id,proposal_id,action_kind,entity_kind,source_person_id,
                     target_person_id,source_organisation_id,target_organisation_id,match_score,actor,
                     occurred_at,reverses_action_id)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) ON CONFLICT DO NOTHING;
                """,
                """
                SELECT EXISTS (SELECT 1 FROM casemesh.entity_resolution_actions
                    WHERE tenant_id=$1 AND matter_id=$2 AND action_id=$3 AND proposal_id=$4
                      AND action_kind=$5 AND entity_kind=$6 AND source_person_id IS NOT DISTINCT FROM $7
                      AND target_person_id IS NOT DISTINCT FROM $8 AND source_organisation_id IS NOT DISTINCT FROM $9
                      AND target_organisation_id IS NOT DISTINCT FROM $10 AND match_score IS NOT DISTINCT FROM $11
                      AND actor=$12 AND occurred_at=$13 AND reverses_action_id IS NOT DISTINCT FROM $14);
                """,
                cancellationToken, tenantId, matterId, action.Id, action.ProposalId,
                (short)action.Kind, (short)action.EntityKind, sourcePerson, targetPerson,
                sourceOrganisation, targetOrganisation, action.MatchScore, action.Actor,
                action.OccurredAt, action.ReversesActionId);
            await WriteOrderedSourcesAsync(connection, transaction, "entity_resolution_sources", "action_id",
                tenantId, matterId, action.Id, action.EvidenceSourceSpanIds, cancellationToken);
        }
    }

    internal static async Task<PersistedMatterBrain?> ReadPersistedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TenantId tenantId,
        Guid matterId,
        CancellationToken cancellationToken)
    {
        var persisted = await PostgresMatterReader.ReadAsync(
            connection, transaction, tenantId, matterId, cancellationToken);
        if (persisted is null)
        {
            return null;
        }

        var snapshot = await ReadAsync(
            connection, transaction, tenantId.Value, matterId, cancellationToken);
        var brain = MatterBrainState.Rehydrate(persisted.Evidence, snapshot);
        return new PersistedMatterBrain(persisted.Evidence, persisted.Workplace, brain);
    }

    private static async Task<MatterBrainSnapshot> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid matterId,
        CancellationToken cancellationToken)
    {
        var people = new List<Person>();
        var roles = await ReadOrderedIdsAndTextAsync(connection, transaction,
            "SELECT person_id,ordinal,role_label FROM casemesh.person_roles WHERE tenant_id=$1 AND matter_id=$2 ORDER BY person_id,ordinal",
            tenantId, matterId, cancellationToken);
        await using (var command = Command(connection, transaction,
                         "SELECT person_id,display_name FROM casemesh.people WHERE tenant_id=$1 AND matter_id=$2 ORDER BY person_id",
                         tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                people.Add(new Person(id, matterId, reader.GetString(1),
                    (roles.GetValueOrDefault(id) ?? []).AsReadOnly()));
            }
        }

        var organisations = new List<Organisation>();
        await using (var command = Command(connection, transaction,
                         "SELECT organisation_id,name,type_label FROM casemesh.organisations WHERE tenant_id=$1 AND matter_id=$2 ORDER BY organisation_id",
                         tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                organisations.Add(new Organisation(reader.GetGuid(0), matterId, reader.GetString(1), reader.GetString(2)));
            }
        }

        var aliases = new List<EntityAlias>();
        await using (var command = Command(connection, transaction, """
                         SELECT alias_id,entity_kind,person_id,organisation_id,alias_value,normalized_value,source_span_id
                         FROM casemesh.entity_aliases WHERE tenant_id=$1 AND matter_id=$2 ORDER BY alias_id
                         """, tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = (CanonicalEntityKind)reader.GetInt16(1);
                aliases.Add(new EntityAlias(reader.GetGuid(0), matterId, kind,
                    kind == CanonicalEntityKind.Person ? reader.GetGuid(2) : reader.GetGuid(3),
                    reader.GetString(4), reader.GetString(5), GetNullable<Guid>(reader, 6)));
            }
        }

        var participantIds = await ReadCommunicationParticipantsAsync(
            connection, transaction, tenantId, matterId, cancellationToken);
        var communicationSources = await ReadOrderedIdsAsync(connection, transaction,
            "SELECT communication_id,ordinal,source_span_id FROM casemesh.communication_sources WHERE tenant_id=$1 AND matter_id=$2 ORDER BY communication_id,ordinal",
            tenantId, matterId, cancellationToken);
        var communications = new List<Communication>();
        await using (var command = Command(connection, transaction, """
                         SELECT communication_id,communication_kind,neutral_label,occurred_at,
                                sender_person_id,sender_organisation_id,verification_state
                         FROM casemesh.communications WHERE tenant_id=$1 AND matter_id=$2 ORDER BY communication_id
                         """, tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var sender = GetNullable<Guid>(reader, 4) ?? GetNullable<Guid>(reader, 5);
                communications.Add(new Communication(id, matterId, (CommunicationKind)reader.GetInt16(1),
                    reader.GetString(2), GetNullable<DateTimeOffset>(reader, 3), sender,
                    (participantIds.GetValueOrDefault(id) ?? []).AsReadOnly(),
                    (communicationSources.GetValueOrDefault(id) ?? []).AsReadOnly(),
                    (VerificationState)reader.GetInt16(6)));
            }
        }

        var runSources = await ReadOrderedIdsAsync(connection, transaction,
            "SELECT extraction_run_id,ordinal,source_span_id FROM casemesh.extraction_run_sources WHERE tenant_id=$1 AND matter_id=$2 ORDER BY extraction_run_id,ordinal",
            tenantId, matterId, cancellationToken);
        var runs = new List<ExtractionRun>();
        await using (var command = Command(connection, transaction, """
                         SELECT extraction_run_id,fingerprint,provider,model,extraction_version,prompt_version,
                                schema_version,generated_at,raw_result_digest,run_sequence
                         FROM casemesh.extraction_runs WHERE tenant_id=$1 AND matter_id=$2
                         ORDER BY run_sequence NULLS FIRST,generated_at,extraction_run_id
                         """, tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                runs.Add(new ExtractionRun(id, matterId, reader.GetString(1),
                    new StructuredExtractionProviderDescriptor(reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6)),
                    (runSources.GetValueOrDefault(id) ?? []).AsReadOnly(),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), GetNullable<long>(reader, 9)));
            }
        }

        var candidateSources = await ReadOrderedIdsAsync(connection, transaction,
            "SELECT candidate_id,ordinal,source_span_id FROM casemesh.extraction_candidate_sources WHERE tenant_id=$1 AND matter_id=$2 ORDER BY candidate_id,ordinal",
            tenantId, matterId, cancellationToken);
        var candidates = new List<ExtractionCandidateRecord>();
        await using (var command = Command(connection, transaction, """
                         SELECT candidate_id,extraction_run_id,external_key,candidate_kind,disposition,rejection_code,
                                extraction_confidence,canonical_kind,person_id,organisation_id,communication_id,
                                assertion_id,event_id,assertion_event_link_id,contradiction_id,payload_json::text,payload_digest
                         FROM casemesh.extraction_candidates WHERE tenant_id=$1 AND matter_id=$2 ORDER BY extraction_run_id,candidate_id
                         """, tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var canonicalKind = reader.IsDBNull(7) ? null : (CanonicalRecordKind?)reader.GetInt16(7);
                candidates.Add(new ExtractionCandidateRecord(
                    id, matterId, reader.GetGuid(1), reader.GetString(2),
                    (ExtractionCandidateKind)reader.GetInt16(3), (CandidateDisposition)reader.GetInt16(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    (candidateSources.GetValueOrDefault(id) ?? []).AsReadOnly(),
                    GetNullable<decimal>(reader, 6), canonicalKind, FirstGuid(reader, 8, 14),
                    reader.GetString(15), reader.GetString(16)));
            }
        }

        var dependencies = new List<MatterBrainDependency>();
        await using (var command = Command(connection, transaction, """
                         SELECT dependency_id,extraction_run_id,source_span_id,candidate_id,canonical_kind,
                                person_id,organisation_id,communication_id,assertion_id,event_id,
                                assertion_event_link_id,contradiction_id,analysis_node_id
                         FROM casemesh.matter_brain_dependencies WHERE tenant_id=$1 AND matter_id=$2 ORDER BY dependency_id
                         """, tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                dependencies.Add(new MatterBrainDependency(reader.GetGuid(0), matterId, reader.GetGuid(1),
                    reader.GetGuid(2), reader.GetGuid(3), (CanonicalRecordKind)reader.GetInt16(4),
                    FirstGuid(reader, 5, 12)!.Value));
            }
        }

        var invalidations = new List<DependencyInvalidation>();
        await using (var command = Command(connection, transaction, """
                         SELECT invalidation_id,dependency_id,invalidated_by_run_id,invalidated_by_audit_event_id,invalidated_at
                         FROM casemesh.dependency_invalidations WHERE tenant_id=$1 AND matter_id=$2 ORDER BY invalidated_at,invalidation_id
                         """, tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                invalidations.Add(new DependencyInvalidation(reader.GetGuid(0), matterId, reader.GetGuid(1),
                    GetNullable<Guid>(reader, 2), GetNullable<Guid>(reader, 3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        var actionSources = await ReadOrderedIdsAsync(connection, transaction,
            "SELECT action_id,ordinal,source_span_id FROM casemesh.entity_resolution_sources WHERE tenant_id=$1 AND matter_id=$2 ORDER BY action_id,ordinal",
            tenantId, matterId, cancellationToken);
        var actions = new List<EntityResolutionAction>();
        await using (var command = Command(connection, transaction, """
                         SELECT action_id,proposal_id,action_kind,entity_kind,source_person_id,target_person_id,
                                source_organisation_id,target_organisation_id,match_score,actor,occurred_at,reverses_action_id
                         FROM casemesh.entity_resolution_actions WHERE tenant_id=$1 AND matter_id=$2 ORDER BY occurred_at,action_id
                         """, tenantId, matterId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var kind = (CanonicalEntityKind)reader.GetInt16(3);
                actions.Add(new EntityResolutionAction(id, matterId, reader.GetGuid(1),
                    (EntityResolutionActionKind)reader.GetInt16(2), kind,
                    kind == CanonicalEntityKind.Person ? reader.GetGuid(4) : reader.GetGuid(6),
                    kind == CanonicalEntityKind.Person ? reader.GetGuid(5) : reader.GetGuid(7),
                    (actionSources.GetValueOrDefault(id) ?? []).AsReadOnly(), GetNullable<decimal>(reader, 8),
                    reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10), GetNullable<Guid>(reader, 11)));
            }
        }

        return new MatterBrainSnapshot(matterId, people, organisations, aliases, communications,
            runs, candidates, dependencies, invalidations, actions);
    }

    private static async Task EnsureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string insertSql,
        string verifySql,
        CancellationToken cancellationToken,
        params object?[] values)
    {
        var inserted = await PostgresMatterStore.ExecuteAsync(
            connection, transaction, insertSql, cancellationToken, values);
        if (inserted == 1)
        {
            return;
        }

        await using var verify = new NpgsqlCommand(verifySql, connection, transaction);
        PostgresMatterStore.AddParameters(verify, values);
        if (await verify.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException("A Matter Brain identity cannot overwrite immutable canonical or model history.");
        }
    }

    private static async Task WriteOrderedSourcesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string ownerColumn,
        Guid tenantId,
        Guid matterId,
        Guid ownerId,
        IReadOnlyList<Guid> sourceIds,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < sourceIds.Count; ordinal++)
        {
            await EnsureAsync(connection, transaction,
                $"INSERT INTO casemesh.{table} (tenant_id,matter_id,{ownerColumn},source_span_id,ordinal) VALUES ($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING;",
                $"SELECT EXISTS (SELECT 1 FROM casemesh.{table} WHERE tenant_id=$1 AND matter_id=$2 AND {ownerColumn}=$3 AND source_span_id=$4 AND ordinal=$5);",
                cancellationToken, tenantId, matterId, ownerId, sourceIds[ordinal], ordinal);
        }
    }

    private static CanonicalLinkValues CanonicalLinks(CanonicalRecordKind? kind, Guid? id)
    {
        if (!kind.HasValue || !id.HasValue)
        {
            return new CanonicalLinkValues();
        }

        return kind.Value switch
        {
            CanonicalRecordKind.Person => new CanonicalLinkValues(PersonId: id),
            CanonicalRecordKind.Organisation => new CanonicalLinkValues(OrganisationId: id),
            CanonicalRecordKind.Communication => new CanonicalLinkValues(CommunicationId: id),
            CanonicalRecordKind.Assertion => new CanonicalLinkValues(AssertionId: id),
            CanonicalRecordKind.Event => new CanonicalLinkValues(EventId: id),
            CanonicalRecordKind.AssertionEventLink => new CanonicalLinkValues(LinkId: id),
            CanonicalRecordKind.Contradiction => new CanonicalLinkValues(ContradictionId: id),
            CanonicalRecordKind.AnalysisNode => new CanonicalLinkValues(AnalysisNodeId: id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid tenantId,
        Guid matterId)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(matterId);
        return command;
    }

    private static async Task<Dictionary<Guid, List<Guid>>> ReadOrderedIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid tenantId,
        Guid matterId,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<Guid, List<Guid>>();
        await using var command = Command(connection, transaction, sql, tenantId, matterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!values.TryGetValue(reader.GetGuid(0), out var list))
            {
                list = [];
                values.Add(reader.GetGuid(0), list);
            }

            list.Add(reader.GetGuid(2));
        }

        return values;
    }

    private static async Task<Dictionary<Guid, List<string>>> ReadOrderedIdsAndTextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid tenantId,
        Guid matterId,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<Guid, List<string>>();
        await using var command = Command(connection, transaction, sql, tenantId, matterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!values.TryGetValue(reader.GetGuid(0), out var list))
            {
                list = [];
                values.Add(reader.GetGuid(0), list);
            }

            list.Add(reader.GetString(2));
        }

        return values;
    }

    private static async Task<Dictionary<Guid, List<Guid>>> ReadCommunicationParticipantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid matterId,
        CancellationToken cancellationToken) =>
        await ReadOrderedIdsAsync(connection, transaction, """
            SELECT communication_id,ordinal,COALESCE(person_id,organisation_id)
            FROM casemesh.communication_participants WHERE tenant_id=$1 AND matter_id=$2
            ORDER BY communication_id,ordinal
            """, tenantId, matterId, cancellationToken);

    private static Guid? FirstGuid(NpgsqlDataReader reader, int start, int end)
    {
        for (var ordinal = start; ordinal <= end; ordinal++)
        {
            if (!reader.IsDBNull(ordinal))
            {
                return reader.GetGuid(ordinal);
            }
        }

        return null;
    }

    private static T? GetNullable<T>(NpgsqlDataReader reader, int ordinal) where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private sealed record CanonicalLinkValues(
        Guid? PersonId = null,
        Guid? OrganisationId = null,
        Guid? CommunicationId = null,
        Guid? AssertionId = null,
        Guid? EventId = null,
        Guid? LinkId = null,
        Guid? ContradictionId = null,
        Guid? AnalysisNodeId = null);
}
