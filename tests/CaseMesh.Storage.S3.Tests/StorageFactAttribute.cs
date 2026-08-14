namespace CaseMesh.Storage.S3.Tests;

internal sealed class StorageFactAttribute : FactAttribute
{
    internal const string PostgresVariable = "CASEMESH_POSTGRES_ADMIN_CONNECTION";
    internal const string EndpointVariable = "CASEMESH_OBJECT_STORAGE_ENDPOINT";
    internal const string AccessKeyVariable = "CASEMESH_OBJECT_STORAGE_ACCESS_KEY";
    internal const string SecretKeyVariable = "CASEMESH_OBJECT_STORAGE_SECRET_KEY";
    internal const string RequiredVariable = "CASEMESH_OBJECT_STORAGE_REQUIRED";

    public StorageFactAttribute()
    {
        var missing = new[]
        {
            PostgresVariable,
            EndpointVariable,
            AccessKeyVariable,
            SecretKeyVariable
        }.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(RequiredVariable), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{RequiredVariable}=1 but required integration configuration is missing: {string.Join(", ", missing)}.");
        }

        Skip = "Set the PostgreSQL and S3-compatible integration environment variables to run storage tests.";
    }
}
