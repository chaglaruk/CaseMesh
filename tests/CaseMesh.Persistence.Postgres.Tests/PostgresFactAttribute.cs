namespace CaseMesh.Persistence.Postgres.Tests;

internal sealed class PostgresFactAttribute : FactAttribute
{
    internal const string ConnectionVariable = "CASEMESH_POSTGRES_ADMIN_CONNECTION";
    internal const string RequiredVariable = "CASEMESH_POSTGRES_REQUIRED";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable(RequiredVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{RequiredVariable}=1 but {ConnectionVariable} is missing.");
            }

            Skip = $"Set {ConnectionVariable} to run real PostgreSQL integration tests.";
        }
    }
}
