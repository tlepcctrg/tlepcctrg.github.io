namespace DMS.Api.Events;

public static class EventTypes
{
    public const string FileUploaded = "FileUploaded";
    public const string FileCommitted = "FileCommitted";
    public const string FileDeleted = "FileDeleted";
    public const string FolderMoved = "FolderMoved";
    public const string PermissionChanged = "PermissionChanged";
}

public sealed record DomainEvent(string EventType, string TenantId, string EntityId, DateTime OccurredAt, object Payload);
