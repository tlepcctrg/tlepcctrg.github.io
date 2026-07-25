namespace FMS.Api.Storage;

/// <summary>
/// Result of a single or multipart pre-signed upload initiation.
/// </summary>
public sealed record PresignedUpload(
    string? SinglePutUrl,
    IReadOnlyList<(int PartNumber, string Url)> Parts,
    string? MultipartUploadId,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Result of a HEAD/existence check against the object storage provider.
/// </summary>
public sealed record ObjectMetadata(bool Found, long Size, string? ETag);

/// <summary>
/// Storage-provider abstraction (Strategy pattern) so FMS can target S3,
/// MinIO, Azure Blob, etc. without leaking provider details into the gRPC
/// service layer.
/// </summary>
public interface IObjectStorageAdapter
{
    Task<PresignedUpload> CreatePresignedUploadAsync(
        string bucket, string key, string contentType, bool multipart, int partCount, CancellationToken ct);

    Task<string> CompleteMultipartUploadAsync(
        string bucket, string key, string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts, CancellationToken ct);

    Task<ObjectMetadata> HeadObjectAsync(string bucket, string key, CancellationToken ct);

    /// <summary>Server-side copy from the temporary bucket/key to the permanent bucket/key.</summary>
    Task CopyObjectAsync(string sourceBucket, string sourceKey, string destBucket, string destKey, CancellationToken ct);

    Task<bool> DeleteObjectAsync(string bucket, string key, CancellationToken ct);

    Task<string> CreatePresignedDownloadUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken ct);
}
