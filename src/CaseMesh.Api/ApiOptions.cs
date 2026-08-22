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
    public string ClamAvExecutablePath { get; init; } = string.Empty;
    public string TesseractExecutablePath { get; init; } = string.Empty;
    public string PopplerExecutablePath { get; init; } = string.Empty;
    public string BuildIdentity { get; init; } = string.Empty;

    public void Validate(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PostgresConnectionString);
        if (MaximumUploadBytes <= 0 || MaximumUploadBytes > 100 * 1024 * 1024)
            throw new InvalidOperationException("The upload limit must be between 1 byte and 100 MiB.");
        if (MaximumUploadFileNameLength is < 1 or > 255)
            throw new InvalidOperationException("The filename metadata limit must be between 1 and 255 characters.");
        if (EnableTestAuthentication &&
            !string.Equals(environment, "Testing", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test authentication can run only in the Testing environment.");
        var isTestHarness = EnableTestAuthentication &&
                            string.Equals(environment, "Testing", StringComparison.OrdinalIgnoreCase);
        if (!isTestHarness)
        {
            if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("A deployed environment requires an HTTPS public origin.");
            ArgumentException.ThrowIfNullOrWhiteSpace(OidcAuthority);
            ArgumentException.ThrowIfNullOrWhiteSpace(OidcClientId);
            ArgumentException.ThrowIfNullOrWhiteSpace(OidcClientSecret);
            ArgumentException.ThrowIfNullOrWhiteSpace(S3Endpoint);
            ArgumentException.ThrowIfNullOrWhiteSpace(S3BucketName);
            ArgumentException.ThrowIfNullOrWhiteSpace(S3AccessKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(S3SecretKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(ClamAvExecutablePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(TesseractExecutablePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(PopplerExecutablePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(BuildIdentity);
            RequireExecutable(ClamAvExecutablePath, nameof(ClamAvExecutablePath));
            RequireExecutable(TesseractExecutablePath, nameof(TesseractExecutablePath));
            RequireExecutable(PopplerExecutablePath, nameof(PopplerExecutablePath));
        }
    }

    private static void RequireExecutable(string value, string name)
    {
        if (string.Equals(value, "runtime-configured", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathRooted(value))
            throw new InvalidOperationException($"{name} must be an explicit absolute deployment path.");
    }
}
