using CaseMesh.Persistence.Postgres;

var connectionString = Environment.GetEnvironmentVariable("CASEMESH_POSTGRES_MIGRATION_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("CASEMESH_POSTGRES_MIGRATION_CONNECTION is required.");
var maximumVersion = args switch
{
    [] => null,
    ["--through", var version] when version.Length == 4 && version.All(char.IsAsciiDigit) => version,
    _ => throw new ArgumentException("Usage: CaseMesh.DbMigrate [--through <four-digit-version>]")
};
var migrator = new PostgresMigrator();
var applied = maximumVersion is null
    ? await migrator.MigrateAsync(connectionString)
    : await migrator.MigrateThroughAsync(connectionString, maximumVersion);
Console.WriteLine($"Applied schema through migration {applied.LastOrDefault()?.Version ?? "none"}.");
