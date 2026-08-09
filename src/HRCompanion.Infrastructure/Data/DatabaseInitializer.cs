using Microsoft.Data.Sqlite;

namespace HRCompanion.Infrastructure.Data;

public static class DatabaseInitializer
{
    private const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS documents (
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            original_path TEXT NOT NULL,
            sha256 TEXT NOT NULL UNIQUE,
            media_type TEXT NOT NULL,
            imported_at TEXT NOT NULL,
            source_date TEXT NULL,
            chunk_count INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chunks (
            id TEXT PRIMARY KEY,
            document_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            text TEXT NOT NULL,
            locator TEXT NULL,
            FOREIGN KEY(document_id) REFERENCES documents(id) ON DELETE CASCADE,
            UNIQUE(document_id, ordinal)
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
            chunk_id UNINDEXED,
            document_id UNINDEXED,
            source_name UNINDEXED,
            locator UNINDEXED,
            text,
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TABLE IF NOT EXISTS facts (
            id TEXT PRIMARY KEY,
            statement TEXT NOT NULL,
            status INTEGER NOT NULL,
            source_document_id TEXT NULL,
            source_locator TEXT NULL,
            effective_date TEXT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(source_document_id) REFERENCES documents(id) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS meetings (
            id TEXT PRIMARY KEY,
            case_name TEXT NOT NULL,
            started_at TEXT NOT NULL,
            ended_at TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS transcript_turns (
            id TEXT PRIMARY KEY,
            meeting_id TEXT NOT NULL,
            speaker INTEGER NOT NULL,
            text TEXT NOT NULL,
            started_at TEXT NOT NULL,
            ended_at TEXT NOT NULL,
            is_final INTEGER NOT NULL,
            source TEXT NOT NULL,
            provider_item_id TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_transcript_meeting_time
            ON transcript_turns(meeting_id, started_at);
        """;

    public static async Task InitializeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (!await HasColumnAsync(connection, "transcript_turns", "provider_item_id", cancellationToken).ConfigureAwait(false))
        {
            await using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE transcript_turns ADD COLUMN provider_item_id TEXT NULL;";
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var index = connection.CreateCommand();
        index.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_transcript_provider_item
            ON transcript_turns(meeting_id, speaker, source, provider_item_id)
            WHERE provider_item_id IS NOT NULL;
            """;
        await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
