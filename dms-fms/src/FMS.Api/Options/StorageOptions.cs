namespace FMS.Api.Options;

/// <summary>
/// Configuration for the S3-compatible object storage provider (AWS S3 or
/// MinIO). FMS is the only component allowed to hold these credentials.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public required string ServiceUrl { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public string Region { get; init; } = "us-east-1";
    public required string TempBucket { get; init; }
    public required string PermanentBucket { get; init; }
    public bool ForcePathStyle { get; init; } = true;
    public int PresignTtlMinutes { get; init; } = 15;
}
