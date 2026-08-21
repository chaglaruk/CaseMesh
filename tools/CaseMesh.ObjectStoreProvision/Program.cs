using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

var endpoint = new Uri(Required("CaseMesh__S3Endpoint"), UriKind.Absolute);
var bucketName = Required("CaseMesh__S3BucketName");
var accessKey = Required("CaseMesh__S3AccessKey");
var secretKey = Required("CaseMesh__S3SecretKey");
var region = Required("CaseMesh__S3Region");
var allowInsecureLocal = bool.TryParse(Environment.GetEnvironmentVariable(
    "CaseMesh__AllowInsecureLocalObjectStorage"), out var allowed) && allowed;

if (endpoint.Scheme != Uri.UriSchemeHttps && !(allowInsecureLocal && IsLoopback(endpoint)))
    throw new InvalidOperationException("Object-store provisioning requires HTTPS outside an explicit local harness.");

using var client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
{
    ServiceURL = endpoint.AbsoluteUri.TrimEnd('/'),
    AuthenticationRegion = region,
    ForcePathStyle = true,
    UseHttp = endpoint.Scheme == Uri.UriSchemeHttp
});
var buckets = await client.ListBucketsAsync();
if (!(buckets.Buckets ?? []).Any(bucket => string.Equals(bucket.BucketName, bucketName, StringComparison.Ordinal)))
    await client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName });

try
{
    var policy = await client.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucketName });
    if (!string.IsNullOrWhiteSpace(policy.Policy))
        throw new InvalidOperationException(
            "The provisioning helper cannot prove a bucket with an existing policy is private.");
}
catch (AmazonS3Exception exception) when (exception.ErrorCode is "NoSuchBucketPolicy" or "NoSuchPolicy")
{
}

using (var anonymous = new HttpClient())
using (var response = await anonymous.GetAsync(new Uri(endpoint,
           $"/{Uri.EscapeDataString(bucketName)}")))
{
    if (response.StatusCode is not HttpStatusCode.Forbidden and not HttpStatusCode.Unauthorized)
        throw new InvalidOperationException("Anonymous bucket access was not denied by the object store.");
}
Console.WriteLine("Private object-store bucket is ready.");

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");

static bool IsLoopback(Uri endpoint) =>
    string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
    (IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address));
