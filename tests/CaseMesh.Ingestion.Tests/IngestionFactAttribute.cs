namespace CaseMesh.Ingestion.Tests;

internal sealed class IngestionFactAttribute : FactAttribute
{
    internal const string Postgres = "CASEMESH_POSTGRES_ADMIN_CONNECTION";
    internal const string Endpoint = "CASEMESH_OBJECT_STORAGE_ENDPOINT";
    internal const string AccessKey = "CASEMESH_OBJECT_STORAGE_ACCESS_KEY";
    internal const string SecretKey = "CASEMESH_OBJECT_STORAGE_SECRET_KEY";
    internal const string OcrImage = "CASEMESH_INGESTION_OCR_IMAGE";
    internal const string Required = "CASEMESH_INGESTION_REQUIRED";

    public IngestionFactAttribute()
    {
        var missing = new[] { Postgres, Endpoint, AccessKey, SecretKey, OcrImage }
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length == 0) return;
        if (string.Equals(Environment.GetEnvironmentVariable(Required), "1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{Required}=1 but required integration configuration is missing: {string.Join(", ", missing)}.");
        Skip = "Set PostgreSQL, S3-compatible storage and OCR integration variables to run ingestion integration tests.";
    }
}
