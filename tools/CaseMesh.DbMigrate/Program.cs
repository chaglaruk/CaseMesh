using CaseMesh.Persistence.Postgres;

var connectionString = Environment.GetEnvironmentVariable("CASEMESH_POSTGRES_MIGRATION_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("CASEMESH_POSTGRES_MIGRATION_CONNECTION is required.");
var applied = await new PostgresMigrator().MigrateAsync(connectionString);
Console.WriteLine($"Applied schema through migration {applied.LastOrDefault()?.Version ?? "none"}.");
