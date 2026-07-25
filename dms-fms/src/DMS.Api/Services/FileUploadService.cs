using DMS.Api.Events;
using DMS.Api.Models;
using DMS.Api.Options;
using DMS.Api.Repositories;
using FMS.Contracts;
using Microsoft.Extensions.Options;

namespace DMS.Api.Services;

public sealed record InitUploadResult(string FileId, string UploadSessionId, string? PutUrl, IReadOnlyList<(int PartNumber, string Url)> Parts, string? MultipartUploadId);
public sealed record CommitResult(bool Success, string? Message, string? FileId, long Size);

/// <summary>
/// Orchestrates the direct-to-storage upload/download lifecycle: DMS never
/// streams bytes, it only validates the request, asks FMS (over gRPC) for
/// pre-signed URLs, and finalizes metadata once the client confirms the
/// transfer completed against object storage directly.
/// </summary>
public sealed class FileUploadService
{
    private readonly FileRepository _files;
    private readonly FolderRepository _folders;
    private readonly StorageService.StorageServiceClient _fms;
    private readonly IEventPublisher _events;
    private readonly TempZoneOptions _tempZoneOptions;
    private readonly ILogger<FileUploadService> _logger;

    public FileUploadService(
        FileRepository files,
        FolderRepository folders,
        StorageService.StorageServiceClient fms,
        IEventPublisher events,
        IOptions<TempZoneOptions> tempZoneOptions,
        ILogger<FileUploadService> logger)
    {
        _files = files;
        _folders = folders;
        _fms = fms;
        _events = events;
        _tempZoneOptions = tempZoneOptions.Value;
        _logger = logger;
    }

    public async Task<InitUploadResult> InitUploadAsync(
        string tenantId, string folderId, string fileName, string contentType,
        long expectedSize, bool multipart, int partCount, string ownerId, CancellationToken ct)
    {
        var folder = await _folders.GetByIdAsync(folderId, ct)
            ?? throw new InvalidOperationException($"Folder '{folderId}' not found.");

        var uploadSessionId = Guid.NewGuid().ToString("N");
        var file = new FileDocument
        {
            TenantId = tenantId,
            FolderId = folderId,
            Ancestors = [.. folder.Ancestors, folder.Id],
            Name = fileName,
            Size = expectedSize,
            StorageKey = $"{folderId}/{Guid.NewGuid():N}/{fileName}",
            OwnerId = ownerId,
            Status = FileStatus.Pending,
            UploadSessionId = uploadSessionId,
            TempExpiryAt = DateTime.UtcNow.AddHours(_tempZoneOptions.RetentionHours),
        };

        await _files.InsertAsync(file, ct);

        var response = await _fms.InitUploadAsync(new InitUploadRequest
        {
            TenantId = tenantId,
            UploadSessionId = uploadSessionId,
            ObjectKey = file.StorageKey,
            ContentType = contentType,
            ExpectedSize = expectedSize,
            Multipart = multipart,
            PartCount = partCount,
        }, cancellationToken: ct);

        await _events.PublishAsync(new DomainEvent(
            EventTypes.FileUploaded, tenantId, file.Id, DateTime.UtcNow,
            new { file.Id, uploadSessionId, file.StorageKey }), ct);

        return new InitUploadResult(
            file.Id,
            uploadSessionId,
            string.IsNullOrEmpty(response.PresignedPutUrl) ? null : response.PresignedPutUrl,
            response.Parts.Select(p => (p.PartNumber, p.Url)).ToList(),
            string.IsNullOrEmpty(response.MultipartUploadId) ? null : response.MultipartUploadId);
    }

    /// <summary>
    /// Finalizes an upload. If a committed file with the same content hash
    /// already exists (deduplication), the new record becomes a metadata
    /// pointer to the existing object instead of triggering a physical
    /// server-side copy - saving both storage and I/O.
    /// </summary>
    public async Task<CommitResult> CommitAsync(string tenantId, string fileId, string expectedSha256, CancellationToken ct)
    {
        var file = await _files.GetByIdAsync(fileId, ct);
        if (file is null || file.Status != FileStatus.Pending)
        {
            return new CommitResult(false, "Upload not found or already finalized.", null, 0);
        }

        var existing = await _files.FindCommittedByHashAsync(tenantId, expectedSha256, ct);
        if (existing is not null)
        {
            await _files.MarkCommittedAsync(fileId, existing.StorageKey, existing.Size, expectedSha256, ct);
            await _files.IncrementRefCountAsync(existing.Id, ct);

            _logger.LogInformation("Deduplicated file {FileId} against existing object {ExistingId}", fileId, existing.Id);

            await _events.PublishAsync(new DomainEvent(
                EventTypes.FileCommitted, tenantId, fileId, DateTime.UtcNow,
                new { fileId, deduplicated = true, sourceFileId = existing.Id }), ct);

            return new CommitResult(true, "Committed via deduplication.", fileId, existing.Size);
        }

        var response = await _fms.CommitUploadAsync(new CommitUploadRequest
        {
            TenantId = tenantId,
            ObjectKey = file.StorageKey,
            UploadSessionId = file.UploadSessionId ?? string.Empty,
            ExpectedSize = file.Size,
            ExpectedSha256 = expectedSha256,
            PermanentKey = file.StorageKey,
        }, cancellationToken: ct);

        if (!response.Exists)
        {
            return new CommitResult(false, "Object not found in temporary storage zone.", null, 0);
        }

        await _files.MarkCommittedAsync(fileId, response.FinalKey, response.Size, expectedSha256, ct);

        await _events.PublishAsync(new DomainEvent(
            EventTypes.FileCommitted, tenantId, fileId, DateTime.UtcNow,
            new { fileId, deduplicated = false, size = response.Size }), ct);

        return new CommitResult(true, "Committed.", fileId, response.Size);
    }

    public async Task<string?> GetDownloadUrlAsync(string tenantId, string fileId, CancellationToken ct)
    {
        var file = await _files.GetByIdAsync(fileId, ct);
        if (file is null || file.Status != FileStatus.Committed)
        {
            return null;
        }

        var response = await _fms.GetDownloadUrlAsync(new DownloadRequest
        {
            TenantId = tenantId,
            ObjectKey = file.StorageKey,
        }, cancellationToken: ct);

        return response.PresignedGetUrl;
    }

    public async Task<bool> DeleteAsync(string tenantId, string fileId, CancellationToken ct)
    {
        var file = await _files.GetByIdAsync(fileId, ct);
        if (file is null)
        {
            return false;
        }

        if (file.RefCount <= 1)
        {
            await _fms.DeleteObjectAsync(new DeleteObjectRequest { TenantId = tenantId, ObjectKey = file.StorageKey }, cancellationToken: ct);
        }

        await _files.MarkDeletedAsync(fileId, ct);

        await _events.PublishAsync(new DomainEvent(
            EventTypes.FileDeleted, tenantId, fileId, DateTime.UtcNow, new { fileId }), ct);

        return true;
    }
}
