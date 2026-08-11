using System.Globalization;
using System.Text.RegularExpressions;
using HRCompanion.Core.Abstractions;
using HRCompanion.Core.Models;
using Microsoft.Data.Sqlite;

namespace HRCompanion.Infrastructure.Data;

public sealed partial class SqliteCaseRepository : ICaseRepository
{
    private readonly string _connectionString;

    public SqliteCaseRepository(AppPaths paths)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        DatabaseInitializer.InitializeAsync(_connectionString, cancellationToken);

    public async Task<bool> HasDocumentHashAsync(string sha256, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM documents WHERE sha256 = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", sha256);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task SaveDocumentAsync(DocumentRecord document, IReadOnlyList<DocumentChunk> chunks, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO documents(
                    id, display_name, original_path, sha256, media_type, imported_at, source_date, chunk_count,
                    evidence_channel, evidence_authority)
                VALUES($id, $name, $path, $hash, $media, $imported, $sourceDate, $count, $channel, $authority);
                """;
            command.Parameters.AddWithValue("$id", document.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", document.DisplayName);
            command.Parameters.AddWithValue("$path", document.OriginalPath);
            command.Parameters.AddWithValue("$hash", document.Sha256);
            command.Parameters.AddWithValue("$media", document.MediaType);
            command.Parameters.AddWithValue("$imported", ToSqlDate(document.ImportedAt));
            command.Parameters.AddWithValue("$sourceDate", document.SourceDate is null ? DBNull.Value : ToSqlDate(document.SourceDate.Value));
            command.Parameters.AddWithValue("$count", document.ChunkCount);
            command.Parameters.AddWithValue("$channel", (int)document.Channel);
            command.Parameters.AddWithValue("$authority", (int)document.Authority);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in chunks)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO chunks(id, document_id, ordinal, text, locator)
                VALUES($id, $document, $ordinal, $text, $locator);
                INSERT INTO chunks_fts(chunk_id, document_id, source_name, locator, text)
                VALUES($id, $document, $sourceName, $locator, $text);
                """;
            command.Parameters.AddWithValue("$id", chunk.Id.ToString("D"));
            command.Parameters.AddWithValue("$document", chunk.DocumentId.ToString("D"));
            command.Parameters.AddWithValue("$ordinal", chunk.Ordinal);
            command.Parameters.AddWithValue("$text", chunk.Text);
            command.Parameters.AddWithValue("$locator", (object?)chunk.Locator ?? DBNull.Value);
            command.Parameters.AddWithValue("$sourceName", document.DisplayName);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DocumentRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, display_name, original_path, sha256, media_type, imported_at, source_date, chunk_count,
                   evidence_channel, evidence_authority
            FROM documents ORDER BY imported_at DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseSqlDate(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ParseSqlDate(reader.GetString(6)),
                reader.GetInt32(7),
                (EvidenceChannel)reader.GetInt32(8),
                (EvidenceAuthority)reader.GetInt32(9)));
        }
        return results;
    }

    public async Task UpdateDocumentClassificationAsync(
        Guid documentId,
        EvidenceChannel channel,
        EvidenceAuthority authority,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE documents
            SET evidence_channel = $channel, evidence_authority = $authority
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", documentId.ToString("D"));
        command.Parameters.AddWithValue("$channel", (int)channel);
        command.Parameters.AddWithValue("$authority", (int)authority);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var fts = connection.CreateCommand())
        {
            fts.Transaction = (SqliteTransaction)transaction;
            fts.CommandText = "DELETE FROM chunks_fts WHERE document_id = $id;";
            fts.Parameters.AddWithValue("$id", documentId.ToString("D"));
            await fts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var document = connection.CreateCommand())
        {
            document.Transaction = (SqliteTransaction)transaction;
            document.CommandText = "DELETE FROM documents WHERE id = $id;";
            document.Parameters.AddWithValue("$id", documentId.ToString("D"));
            await document.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EvidenceSnippet>> SearchAsync(string query, int limit = 8, CancellationToken cancellationToken = default)
    {
        var ftsQuery = ToFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery)) return [];

        var results = new List<EvidenceSnippet>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.chunk_id, f.document_id, f.source_name, f.locator, f.text,
                   bm25(chunks_fts) AS rank, d.source_date
            FROM chunks_fts f
            JOIN documents d ON d.id = f.document_id
            WHERE chunks_fts MATCH $query
              AND d.evidence_channel = $ordinary
              AND d.evidence_authority = $current
            ORDER BY rank ASC, d.source_date DESC
            LIMIT $candidateLimit;
            """;
        command.Parameters.AddWithValue("$query", ftsQuery);
        command.Parameters.AddWithValue("$ordinary", (int)EvidenceChannel.OrdinaryHr);
        command.Parameters.AddWithValue("$current", (int)EvidenceAuthority.CurrentFinal);
        var requestedLimit = Math.Clamp(limit, 1, 50);
        command.Parameters.AddWithValue("$candidateLimit", Math.Min(100, requestedLimit * 4));
        var seenText = new HashSet<string>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = reader.GetString(4);
            var normalized = CollapseForDedupRegex().Replace(text, " ").Trim().ToUpperInvariant();
            if (!seenText.Add(normalized)) continue;

            var rank = reader.IsDBNull(5) ? 0d : reader.GetDouble(5);
            results.Add(new(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                text,
                -rank,
                reader.IsDBNull(6) ? null : ParseSqlDate(reader.GetString(6))));
            if (results.Count >= requestedLimit) break;
        }
        return results;
    }

    public async Task<IReadOnlyList<CaseFact>> GetFactsAsync(CancellationToken cancellationToken = default)
    {
        var facts = new List<CaseFact>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.statement, f.status, f.source_document_id, f.source_locator, f.effective_date, f.created_at
            FROM facts f
            LEFT JOIN documents d ON d.id = f.source_document_id
            WHERE f.source_document_id IS NULL
               OR (d.evidence_channel = $ordinary AND d.evidence_authority = $current)
            ORDER BY CASE f.status WHEN 2 THEN 0 WHEN 1 THEN 1 ELSE 2 END,
                     f.effective_date DESC,
                     f.created_at DESC;
            """;
        command.Parameters.AddWithValue("$ordinary", (int)EvidenceChannel.OrdinaryHr);
        command.Parameters.AddWithValue("$current", (int)EvidenceAuthority.CurrentFinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            facts.Add(new(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                (FactStatus)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : ParseSqlDate(reader.GetString(5)),
                ParseSqlDate(reader.GetString(6))));
        }
        return facts;
    }

    public async Task SaveFactAsync(CaseFact fact, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO facts(id, statement, status, source_document_id, source_locator, effective_date, created_at)
            VALUES($id, $statement, $status, $document, $locator, $effective, $created)
            ON CONFLICT(id) DO UPDATE SET
                statement = excluded.statement,
                status = excluded.status,
                source_document_id = excluded.source_document_id,
                source_locator = excluded.source_locator,
                effective_date = excluded.effective_date;
            """;
        command.Parameters.AddWithValue("$id", fact.Id.ToString("D"));
        command.Parameters.AddWithValue("$statement", fact.Statement);
        command.Parameters.AddWithValue("$status", (int)fact.Status);
        command.Parameters.AddWithValue("$document", fact.SourceDocumentId is null ? DBNull.Value : fact.SourceDocumentId.Value.ToString("D"));
        command.Parameters.AddWithValue("$locator", (object?)fact.SourceLocator ?? DBNull.Value);
        command.Parameters.AddWithValue("$effective", fact.EffectiveDate is null ? DBNull.Value : ToSqlDate(fact.EffectiveDate.Value));
        command.Parameters.AddWithValue("$created", ToSqlDate(fact.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptPersistenceResult> SaveTranscriptTurnAsync(TranscriptTurn turn, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transcript_turns(id, meeting_id, speaker, text, started_at, ended_at, is_final, source, provider_item_id)
            VALUES($id, $meeting, $speaker, $text, $started, $ended, $final, $source, $providerItem)
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", turn.Id.ToString("D"));
        command.Parameters.AddWithValue("$meeting", turn.MeetingId.ToString("D"));
        command.Parameters.AddWithValue("$speaker", (int)turn.Speaker);
        command.Parameters.AddWithValue("$text", turn.Text);
        command.Parameters.AddWithValue("$started", ToSqlDate(turn.StartedAt));
        command.Parameters.AddWithValue("$ended", ToSqlDate(turn.EndedAt));
        command.Parameters.AddWithValue("$final", turn.IsFinal ? 1 : 0);
        command.Parameters.AddWithValue("$source", turn.Source);
        command.Parameters.AddWithValue("$providerItem", (object?)turn.ProviderItemId ?? DBNull.Value);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected switch
        {
            1 => TranscriptPersistenceResult.Inserted(DateTimeOffset.UtcNow),
            0 => TranscriptPersistenceResult.AlreadyDurable(),
            _ => throw new InvalidOperationException($"Unexpected transcript insert row count: {affected}.")
        };
    }

    public async Task<IReadOnlyList<TranscriptTurn>> GetMeetingTurnsAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        var turns = new List<TranscriptTurn>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, speaker, text, started_at, ended_at, is_final, source, provider_item_id
            FROM transcript_turns
            WHERE meeting_id = $meeting
            ORDER BY started_at ASC, ended_at ASC;
            """;
        command.Parameters.AddWithValue("$meeting", meetingId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            turns.Add(new(
                Guid.Parse(reader.GetString(0)),
                meetingId,
                (SpeakerRole)reader.GetInt32(1),
                reader.GetString(2),
                ParseSqlDate(reader.GetString(3)),
                ParseSqlDate(reader.GetString(4)),
                reader.GetInt32(5) != 0,
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return turns;
    }

    public async Task StartMeetingAsync(MeetingState meeting, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meetings(id, case_name, started_at, ended_at)
            VALUES($id, $caseName, $startedAt, NULL)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", meeting.MeetingId.ToString("D"));
        command.Parameters.AddWithValue("$caseName", meeting.CaseName);
        command.Parameters.AddWithValue("$startedAt", ToSqlDate(meeting.StartedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE meetings SET ended_at = $endedAt WHERE id = $id AND ended_at IS NULL;";
        command.Parameters.AddWithValue("$id", meetingId.ToString("D"));
        command.Parameters.AddWithValue("$endedAt", ToSqlDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MeetingState?> GetUnfinishedMeetingAsync(CancellationToken cancellationToken = default)
    {
        Guid meetingId;
        string caseName;
        DateTimeOffset startedAt;
        await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, case_name, started_at
                FROM meetings
                WHERE ended_at IS NULL
                ORDER BY started_at DESC
                LIMIT 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            meetingId = Guid.Parse(reader.GetString(0));
            caseName = reader.GetString(1);
            startedAt = ParseSqlDate(reader.GetString(2));
        }

        var meeting = new MeetingState(meetingId, caseName, startedAt);
        foreach (var turn in await GetMeetingTurnsAsync(meetingId, cancellationToken).ConfigureAwait(false)) meeting.AddTurn(turn);
        return meeting;
    }

    internal static string ToFtsQuery(string input)
    {
        var tokens = FtsTokenRegex().Matches(input)
            .Select(match => match.Value)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Select(token => "\"" + token.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .ToArray();
        return string.Join(" OR ", tokens);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string ToSqlDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseSqlDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex FtsTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex CollapseForDedupRegex();
}
