namespace CaseMesh.Storage.S3;

public sealed class S3ObjectStorageOptions
{
    public required Uri Endpoint { get; init; }
    public required string Region { get; init; }
    public required string BucketName { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public bool AllowInsecureLocalEndpoint { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(Region);
        ArgumentException.ThrowIfNullOrWhiteSpace(BucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(SecretKey);

        if (Endpoint.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        if (Endpoint.Scheme != Uri.UriSchemeHttp || !AllowInsecureLocalEndpoint || !Endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                "S3-compatible storage requires HTTPS unless an explicit loopback-only local-test override is enabled.");
        }
    }
}
