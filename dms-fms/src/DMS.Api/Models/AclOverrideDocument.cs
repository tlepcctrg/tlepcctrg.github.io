using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DMS.Api.Models;

public enum NodeType
{
    Folder = 0,
    File = 1,
}

public enum PermissionEffect
{
    Allow = 0,
    Deny = 1,
}

[Flags]
public enum PermissionFlags
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    Manage = 8,
    All = Read | Write | Delete | Manage,
}

/// <summary>
/// Only *explicit* ACL grants/denials are stored - never the fully resolved
/// inherited set. Effective permissions are computed at read time from the
/// node's ancestor chain, keeping writes O(1) regardless of subtree size.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class AclOverrideDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string TenantId { get; set; }

    public required string NodeId { get; set; }

    public NodeType NodeType { get; set; }

    /// <summary>Depth of NodeId in the hierarchy - used to pick the closest
    /// override (highest depth) when resolving precedence.</summary>
    public int Depth { get; set; }

    public required string PrincipalId { get; set; }

    public PermissionFlags Permissions { get; set; }

    public PermissionEffect Effect { get; set; } = PermissionEffect.Allow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
