using FMS.Api.Options;
using FMS.Api.Storage;
using FMS.Contracts;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace FMS.Api.Services;

/// <summary>
/// Implements the internal StorageService gRPC contract consumed exclusively
/// by DMS. Never streams binary payloads - only issues pre-signed URLs and
/// performs control-plane object operations.
/// </summary>
public sealed class StorageServiceImpl : StorageService.StorageServiceBase
{
    private readonly IObjectStorageAdapter _storage;
    private readonly StorageOptions _options;
    private readonly ILogger<StorageServiceImpl> _logger;

    public StorageServiceImpl(IObjectStorageAdapter storage, IOptions<StorageOptions> options, ILogger<StorageServiceImpl> logger)
    {
        _storage = storage;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task<InitUploadResponse> InitUpload(InitUploadRequest request, ServerCallContext context)
    {
        var tempKey = BuildTempKey(request.TenantId, request.UploadSessionId, request.ObjectKey);

        var presigned = await _storage.CreatePresignedUploadAsync(
            _options.TempBucket, tempKey, request.ContentType, request.Multipart, request.PartCount, context.CancellationToken);

        var response = new InitUploadResponse
        {
            ExpiryUnixSeconds = presigned.ExpiresAt.ToUnixTimeSeconds(),
            MultipartUploadId = presigned.MultipartUploadId ?? string.Empty,
        };

        if (presigned.SinglePutUrl is not null)
        {
            response.PresignedPutUrl = presigned.SinglePutUrl;
        }

        foreach (var (partNumber, url) in presigned.Parts)
        {
            response.Parts.Add(new PresignedPart { PartNumber = partNumber, Url = url });
        }

        _logger.LogInformation("Init upload for session {SessionId}, key {Key}, multipart={Multipart}",
            request.UploadSessionId, tempKey, request.Multipart);

        return response;
    }

    public override async Task<CompleteMultipartUploadResponse> CompleteMultipartUpload(
        CompleteMultipartUploadRequest request, ServerCallContext context)
    {
        var tempKey = request.ObjectKey;
        var parts = request.Parts.Select(p => (p.PartNumber, p.Etag)).ToList();

        var etag = await _storage.CompleteMultipartUploadAsync(
            _options.TempBucket, tempKey, request.MultipartUploadId, parts, context.CancellationToken);

        return new CompleteMultipartUploadResponse { Succeeded = true, Etag = etag };
    }

    public override async Task<CommitUploadResponse> CommitUpload(CommitUploadRequest request, ServerCallContext context)
    {
        var tempKey = BuildTempKey(request.TenantId, request.UploadSessionId, request.ObjectKey);

        var metadata = await _storage.HeadObjectAsync(_options.TempBucket, tempKey, context.CancellationToken);
        if (!metadata.Found)
        {
            return new CommitUploadResponse { Exists = false };
        }

        if (request.ExpectedSize > 0 && metadata.Size != request.ExpectedSize)
        {
            _logger.LogWarning("Size mismatch for {Key}: expected {Expected}, got {Actual}",
                tempKey, request.ExpectedSize, metadata.Size);
            return new CommitUploadResponse { Exists = false };
        }

        var permanentKey = string.IsNullOrEmpty(request.PermanentKey) ? request.ObjectKey : request.PermanentKey;

        await _storage.CopyObjectAsync(_options.TempBucket, tempKey, _options.PermanentBucket, permanentKey, context.CancellationToken);

        return new CommitUploadResponse
        {
            Exists = true,
            Size = metadata.Size,
            Etag = metadata.ETag ?? string.Empty,
            FinalKey = permanentKey,
        };
    }

    public override async Task<DownloadResponse> GetDownloadUrl(DownloadRequest request, ServerCallContext context)
    {
        var ttl = TimeSpan.FromMinutes(_options.PresignTtlMinutes);
        var url = await _storage.CreatePresignedDownloadUrlAsync(
            _options.PermanentBucket, request.ObjectKey, ttl, context.CancellationToken);

        return new DownloadResponse
        {
            PresignedGetUrl = url,
            ExpiryUnixSeconds = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds(),
        };
    }

    public override async Task<DeleteObjectResponse> DeleteObject(DeleteObjectRequest request, ServerCallContext context)
    {
        var deleted = await _storage.DeleteObjectAsync(_options.PermanentBucket, request.ObjectKey, context.CancellationToken);
        return new DeleteObjectResponse { Deleted = deleted };
    }

    public override async Task<HeadObjectResponse> HeadObject(HeadObjectRequest request, ServerCallContext context)
    {
        var metadata = await _storage.HeadObjectAsync(_options.PermanentBucket, request.ObjectKey, context.CancellationToken);
        return new HeadObjectResponse
        {
            Found = metadata.Found,
            Size = metadata.Size,
            Etag = metadata.ETag ?? string.Empty,
        };
    }

    private static string BuildTempKey(string tenantId, string uploadSessionId, string objectKey) =>
        $"tmp/{tenantId}/{uploadSessionId}/{objectKey}";
}
