using System.Diagnostics;
using System.Text.Json;
using CaseMesh.Core.Models;
using CaseMesh.Persistence.Postgres;
using Npgsql;

if (args.Length == 0) Usage();
var command = args[0].ToLowerInvariant();
if (command == "grant")
{
    if (args.Length != 3) Usage();
    var tenant = Tenant(args[1]);
    PilotAdminTenantScope.Require(tenant, Environment.GetEnvironmentVariable("CaseMesh__PilotAdminTenantId"));
    var tier = Token(args[2], 40);
    var connectionString = Required("CaseMesh__PostgresAdminConnectionString");
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var update = new NpgsqlCommand("""
        UPDATE casemesh.pilot_entitlements
        SET tier_code=$2,configured_at=CURRENT_TIMESTAMP,configured_by='operator:pilot-admin'
        WHERE tenant_id=$1;
        """, connection);
    update.Parameters.AddWithValue(tenant.Value);
    update.Parameters.AddWithValue(tier);
    if (await update.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Tenant entitlement not found.");
    Write(new { action = "grant", tenantId = tenant.Value, tierCode = tier, updated = true });
}
else if (command is "status" or "reconcile" or "benchmark")
{
    var connectionString = Required("CaseMesh__PostgresConnectionString");
    var tenant = args.Length > 1 ? Tenant(args[1]) : throw new ArgumentException("Tenant id is required.");
    PilotAdminTenantScope.Require(tenant, Environment.GetEnvironmentVariable("CaseMesh__PilotAdminTenantId"));
    await using var operations = new PostgresPilotOperationsRepository(connectionString, TimeProvider.System);
    if (command == "status")
    {
        if (args.Length != 3) Usage();
        var usage = await operations.GetUsageAsync(tenant, Id(args[2], "Matter"));
        Write(new
        {
            action = "status",
            usage.Entitlements.TierCode,
            usage.ActiveMatters,
            usage.MatterOriginalBytes,
            usage.TenantOriginalBytes,
            usage.MatterEvidenceItems,
            usage.TenantEvidenceItems,
            usage.QaRequestsToday,
            usage.ExportsToday
        });
    }
    else if (command == "reconcile")
    {
        if (args.Length != 2) Usage();
        var pruned = await operations.PruneOperationalMetadataAsync(tenant);
        Write(new { action = "reconcile", tenantId = tenant.Value, pruned });
    }
    else
    {
        if (args.Length != 4 || !int.TryParse(args[3], out var iterations) || iterations is < 3 or > 100)
            Usage();
        var matterId = Id(args[2], "Matter");
        await using var store = new PostgresMatterStore(connectionString);
        var timings = new List<double>(iterations);
        for (var index = 0; index < iterations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            _ = await store.LoadAsync(tenant, matterId)
                ?? throw new InvalidOperationException("Matter not found.");
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        timings.Sort();
        Write(new
        {
            action = "benchmark",
            iterations,
            medianMilliseconds = Median(timings),
            p95Milliseconds = Percentile(timings, .95)
        });
    }
}
else Usage();

return;

static double Percentile(IReadOnlyList<double> values, double percentile) =>
    values[(int)Math.Ceiling(percentile * values.Count) - 1];
static double Median(IReadOnlyList<double> values) => values.Count % 2 == 0
    ? (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2
    : values[values.Count / 2];
static TenantId Tenant(string value) => new(Id(value, "Tenant"));
static Guid Id(string value, string label) => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
    ? parsed : throw new ArgumentException($"{label} id is invalid.");
static string Token(string value, int maximumLength) => !string.IsNullOrWhiteSpace(value) &&
    value.Length <= maximumLength && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
    ? value.ToLowerInvariant() : throw new ArgumentException("The tier code is invalid.");
static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"{name} is required.");
static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
static void Usage() => throw new ArgumentException(
    "Usage: grant <tenant> <tier> | status <tenant> <matter> | reconcile <tenant> | benchmark <tenant> <matter> <3-100>");
