namespace CaseMesh.Api;

public sealed class CaseMeshApiOptions
{
    public const string SectionName = "CaseMesh";
    public string PostgresConnectionString { get; init; } = string.Empty;
    public string PublicOrigin { get; init; } = string.Empty;
    public bool EnableTestAuthentication { get; init; }
    public long MaximumUploadBytes { get; init; } = 25 * 1024 * 1024;
    public int MaximumUploadFileNameLength { get; init; } = 255;
    public string? OidcAuthority { get; init; }
    public string? OidcClientId { get; init; }
    public string? OidcClientSecret { get; init; }
    public string S3Endpoint { get; init; } = string.Empty;
    public string S3Region { get; init; } = "us-east-1";
    public string S3BucketName { get; init; } = string.Empty;
    public string S3AccessKey { get; init; } = string.Empty;
    public string S3SecretKey { get; init; } = string.Empty;
    public bool AllowInsecureLocalObjectStorage { get; init; }

    public void Validate(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PostgresConnectionString);
        if (MaximumUploadBytes <= 0 || MaximumUploadBytes > 100 * 1024 * 1024)
            throw new InvalidOperationException("The upload limit must be between 1 byte and 100 MiB.");
        if (MaximumUploadFileNameLength is < 1 or > 255)
            throw new InvalidOperationException("The filename metadata limit must be between 1 and 255 characters.");
        if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
        {
            if (EnableTestAuthentication)
                throw new InvalidOperationException("Test authentication cannot run in Production.");
            if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Production requires an HTTPS public origin.");
            ArgumentException.ThrowIfNullOrWhiteSpace(OidcAuthority);
            ArgumentException.ThrowIfNullOrWhiteSpace(OidcClientId);
            ArgumentException.ThrowIfNullOrWhiteSpace(OidcClientSecret);
        }
        if (!EnableTestAuthentication || !string.Equals(environment, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(S3Endpoint);
            ArgumentException.ThrowIfNullOrWhiteSpace(S3BucketName);
            ArgumentException.ThrowIfNullOrWhiteSpace(S3AccessKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(S3SecretKey);
        }
    }
}
