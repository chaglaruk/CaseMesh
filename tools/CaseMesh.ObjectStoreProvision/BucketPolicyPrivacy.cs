using System.Text.Json;

namespace CaseMesh.ObjectStoreProvision;

internal static class BucketPolicyPrivacy
{
    internal static bool HasPotentiallyPublicAllow(string policy)
    {
        try
        {
            using var document = JsonDocument.Parse(policy);
            if (!document.RootElement.TryGetProperty("Statement", out var statements))
                return false;

            return statements.ValueKind == JsonValueKind.Array
                ? statements.EnumerateArray().Any(IsPotentiallyPublicAllow)
                : IsPotentiallyPublicAllow(statements);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool IsPotentiallyPublicAllow(JsonElement statement)
    {
        if (!statement.TryGetProperty("Effect", out var effect) ||
            !string.Equals(effect.GetString(), "Allow", StringComparison.OrdinalIgnoreCase))
            return false;

        // S3 resource policies identify callers through Principal. MinIO can also
        // expose owner/identity-style statements without that field; Program verifies
        // their privacy with an authenticated canary and anonymous-read probe.
        return statement.TryGetProperty("NotPrincipal", out _) ||
               (statement.TryGetProperty("Principal", out var principal) && ContainsWildcard(principal));
    }

    private static bool ContainsWildcard(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains('*', StringComparison.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsWildcard),
        JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsWildcard(property.Value)),
        _ => true
    };
}
