using DMS.Api.Models;

namespace DMS.Api.Controllers;

public sealed record CreateFolderRequest(string TenantId, string? ParentId, string Name, string OwnerId);
public sealed record MoveFolderRequest(string TenantId, string NewParentId);

public sealed record InitUploadRequestDto(
    string TenantId, string FolderId, string FileName, string ContentType,
    long ExpectedSize, bool Multipart, int PartCount, string OwnerId);

public sealed record CommitUploadRequestDto(string TenantId, string ExpectedSha256);

public sealed record GrantPermissionRequest(
    string TenantId, NodeType NodeType, string NodeId, string PrincipalId,
    PermissionFlags Permissions, PermissionEffect Effect);

public sealed record RevokePermissionRequest(string TenantId, NodeType NodeType, string NodeId, string PrincipalId);
