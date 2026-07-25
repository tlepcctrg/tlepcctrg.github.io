using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DMS.Api.Models;

/// <summary>
/// A folder node in the hierarchy. Uses a hybrid materialized-path +
/// ancestry-array model: <see cref="Path"/> enables fast prefix/subtree
/// queries, while <see cref="Ancestors"/> enables O(1) ancestor lookups for
/// permission-inheritance resolution without recursive queries.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class FolderDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string TenantId { get; set; }

    /// <summary>Null for root folders.</summary>
    public string? ParentId { get; set; }

    public required string Name { get; set; }

    /// <summary>Materialized path, e.g. "/root123/folder45/folder902/".</summary>
    public required string Path { get; set; }

    /// <summary>Ordered list of ancestor folder ids, root-first.</summary>
    public List<string> Ancestors { get; set; } = [];

    public int Depth { get; set; }

    public required string OwnerId { get; set; }

    public long ChildCount { get; set; }

    /// <summary>Set while an asynchronous subtree move is in progress.</summary>
    public bool MovePending { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
