using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace CaseMesh.Storage.S3;

internal sealed class S3ImmutableObjectBackend : IImmutableObjectBackend
{
    internal const string BackendKind = "s3";
    private readonly S3ObjectStorageOptions _options;
    private readonly AmazonS3Client _client;

    internal S3ImmutableObjectBackend(S3ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        var configuration = new AmazonS3Config
        {
            ServiceURL = options.Endpoint.ToString().TrimEnd('/'),
            AuthenticationRegion = options.Region,
            ForcePathStyle = true,
            UseHttp = options.Endpoint.Scheme == Uri.UriSchemeHttp
        };
        _client = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            configuration);
    }

    public StorageAddress AddressFor(OriginalObjectIdentity identity) => new(
        BackendKind,
        _options.BucketName,
        $"v1/tenants/{identity.TenantId.Value:D}/matters/{identity.MatterId:D}/originals/{identity.OriginalObjectId:D}");

    public async Task<ObjectCreateResult> CreateIfAbsentAsync(
        StorageAddress address,
        Stream content,
        long byteLength,
        CancellationToken cancellationToken)
    {
        RequireConfiguredAddress(address);
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var request = new PutObjectRequest
        {
            BucketName = address.BucketName,
            Key = address.ObjectKey,
            InputStream = content,
            AutoCloseStream = false,
            IfNoneMatch = "*"
        };
        request.Headers.ContentLength = byteLength;

        try
        {
            await _client.PutObjectAsync(request, cancellationToken);
            return new ObjectCreateResult(true);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return new ObjectCreateResult(false);
        }
    }

    public async Task<Stream> OpenReadAsync(
        StorageAddress address,
        CancellationToken cancellationToken)
    {
        RequireConfiguredAddress(address);
        try
        {
            var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = address.BucketName, Key = address.ObjectKey },
                cancellationToken);
            return new ResponseOwnedStream(response);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new OriginalEvidenceNotFoundException("The physical evidence object does not exist.", exception);
        }
    }

    public async Task DeleteIfExistsAsync(
        StorageAddress address,
        CancellationToken cancellationToken)
    {
        RequireConfiguredAddress(address);
        await _client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = address.BucketName, Key = address.ObjectKey },
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RequireConfiguredAddress(StorageAddress address)
    {
        if (!string.Equals(address.BackendKind, BackendKind, StringComparison.Ordinal) ||
            !string.Equals(address.BucketName, _options.BucketName, StringComparison.Ordinal))
        {
            throw new OriginalEvidenceConflictException(
                "Persisted storage metadata does not belong to the configured private S3 backend.");
        }
    }

    private sealed class ResponseOwnedStream : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly Stream _stream;

        internal ResponseOwnedStream(GetObjectResponse response)
        {
            _response = response;
            _stream = response.ResponseStream;
        }

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }
        public override void Flush() => _stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _stream.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
