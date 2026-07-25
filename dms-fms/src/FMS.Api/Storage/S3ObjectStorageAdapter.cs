using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using FMS.Api.Options;

namespace FMS.Api.Storage;

/// <summary>
/// S3-compatible adapter. Works against real AWS S3 as well as MinIO (or any
/// other S3-API-compatible provider) purely through configuration.
/// </summary>
public sealed class S3ObjectStorageAdapter : IObjectStorageAdapter
{
    private readonly IAmazonS3 _client;
    private readonly StorageOptions _options;
    private readonly ILogger<S3ObjectStorageAdapter> _logger;

    public S3ObjectStorageAdapter(IAmazonS3 client, IOptions<StorageOptions> options, ILogger<S3ObjectStorageAdapter> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PresignedUpload> CreatePresignedUploadAsync(
        string bucket, string key, string contentType, bool multipart, int partCount, CancellationToken ct)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.PresignTtlMinutes);

        if (!multipart || partCount <= 1)
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = expiresAt.UtcDateTime,
                ContentType = contentType,
            };
            var url = await _client.GetPreSignedURLAsync(request);
            return new PresignedUpload(url, [], null, expiresAt);
        }

        var initiateResponse = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = contentType,
        }, ct);

        var parts = new List<(int, string)>(partCount);
        for (var partNumber = 1; partNumber <= partCount; partNumber++)
        {
            var partUrl = await _client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = expiresAt.UtcDateTime,
                UploadId = initiateResponse.UploadId,
                PartNumber = partNumber,
            });
            parts.Add((partNumber, partUrl));
        }

        return new PresignedUpload(null, parts, initiateResponse.UploadId, expiresAt);
    }

    public async Task<string> CompleteMultipartUploadAsync(
        string bucket, string key, string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts, CancellationToken ct)
    {
        var response = await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = parts.Select(p => new PartETag(p.PartNumber, p.ETag)).ToList(),
        }, ct);

        return response.ETag;
    }

    public async Task<ObjectMetadata> HeadObjectAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(bucket, key, ct);
            return new ObjectMetadata(true, response.ContentLength, response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ObjectMetadata(false, 0, null);
        }
    }

    public async Task CopyObjectAsync(string sourceBucket, string sourceKey, string destBucket, string destKey, CancellationToken ct)
    {
        await _client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            DestinationBucket = destBucket,
            DestinationKey = destKey,
        }, ct);

        // Best-effort cleanup of the temp-zone object; failures here do not
        // block commitment since the TTL sweep will reclaim it later.
        try
        {
            await _client.DeleteObjectAsync(sourceBucket, sourceKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove temp object {Bucket}/{Key} after commit copy", sourceBucket, sourceKey);
        }
    }

    public async Task<bool> DeleteObjectAsync(string bucket, string key, CancellationToken ct)
    {
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, ct);
        return true;
    }

    public async Task<string> CreatePresignedDownloadUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken ct)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ttl),
        };
        return await _client.GetPreSignedURLAsync(request);
    }
}
