using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DMS.Api.Models;

public enum FileStatus
{
    /// <summary>Uploaded to the temporary zone but not yet committed.</summary>
    Pending = 0,
    Committed = 1,
    Deleted = 2,
}

/// <summary>File metadata record. The binary payload never touches DMS/FMS
/// compute - only the storage key/provider pointer is tracked here.</summary>
[BsonIgnoreExtraElements]
public sealed class FileDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string TenantId { get; set; }

    public required string FolderId { get; set; }

    /// <summary>Denormalized folder ancestors, copied from the owning folder
    /// at write time, used for effective-permission resolution on files.</summary>
    public List<string> Ancestors { get; set; } = [];

    public required string Name { get; set; }

    public long Size { get; set; }

    /// <summary>SHA-256 content hash, used for deduplication.</summary>
    public string? ContentHash { get; set; }

    public required string StorageKey { get; set; }

    public string StorageProvider { get; set; } = "s3";

    public int Version { get; set; } = 1;

    public FileStatus Status { get; set; } = FileStatus.Pending;

    public required string OwnerId { get; set; }

    /// <summary>Reference count for deduplicated content; >1 means the
    /// underlying object is shared by multiple logical file records.</summary>
    public int RefCount { get; set; } = 1;

    /// <summary>Upload session id, present while the file is still Pending.</summary>
    public string? UploadSessionId { get; set; }

    /// <summary>TTL field: MongoDB TTL index expires Pending records that are
    /// never committed within the retention window.</summary>
    public DateTime? TempExpiryAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
