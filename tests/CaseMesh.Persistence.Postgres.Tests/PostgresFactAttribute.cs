namespace CaseMesh.Persistence.Postgres.Tests;

internal sealed class PostgresFactAttribute : FactAttribute
{
    internal const string ConnectionVariable = "CASEMESH_POSTGRES_ADMIN_CONNECTION";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = $"Set {ConnectionVariable} to run real PostgreSQL integration tests.";
        }
    }
}
