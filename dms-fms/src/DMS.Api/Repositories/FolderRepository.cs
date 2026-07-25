using DMS.Api.Models;
using MongoDB.Driver;

namespace DMS.Api.Repositories;

public sealed class FolderRepository
{
    private readonly MongoContext _context;

    public FolderRepository(MongoContext context) => _context = context;

    public async Task<FolderDocument> CreateAsync(string tenantId, string? parentId, string name, string ownerId, CancellationToken ct)
    {
        List<string> ancestors = [];
        var depth = 0;
        var pathPrefix = "/";

        if (parentId is not null)
        {
            var parent = await GetByIdAsync(parentId, ct)
                ?? throw new InvalidOperationException($"Parent folder '{parentId}' not found.");

            ancestors = [.. parent.Ancestors, parent.Id];
            depth = parent.Depth + 1;
            pathPrefix = parent.Path;
        }

        var folder = new FolderDocument
        {
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            OwnerId = ownerId,
            Ancestors = ancestors,
            Depth = depth,
            Path = string.Empty,
        };
        folder.Path = $"{pathPrefix}{folder.Id}/";

        await _context.Folders.InsertOneAsync(folder, cancellationToken: ct);

        if (parentId is not null)
        {
            await _context.Folders.UpdateOneAsync(
                Builders<FolderDocument>.Filter.Eq(f => f.Id, parentId),
                Builders<FolderDocument>.Update.Inc(f => f.ChildCount, 1),
                cancellationToken: ct);
        }

        return folder;
    }

    public Task<FolderDocument?> GetByIdAsync(string id, CancellationToken ct) =>
        _context.Folders.Find(f => f.Id == id).FirstOrDefaultAsync(ct)!;

    public Task<List<FolderDocument>> GetChildrenAsync(string parentId, int skip, int limit, CancellationToken ct) =>
        _context.Folders.Find(f => f.ParentId == parentId)
            .SortBy(f => f.Name)
            .Skip(skip)
            .Limit(limit)
            .ToListAsync(ct);

    /// <summary>Ancestors are already denormalized on the document, so this
    /// is a single point read - no recursive parent-chasing required.</summary>
    public async Task<List<FolderDocument>> GetAncestorChainAsync(string folderId, CancellationToken ct)
    {
        var folder = await GetByIdAsync(folderId, ct);
        if (folder is null || folder.Ancestors.Count == 0)
        {
            return [];
        }

        return await _context.Folders.Find(Builders<FolderDocument>.Filter.In(f => f.Id, folder.Ancestors))
            .ToListAsync(ct);
    }

    /// <summary>Returns one page of descendants under <paramref name="folderId"/>
    /// using the materialized-path prefix index, for background bulk-move jobs.</summary>
    public async Task<List<FolderDocument>> GetDescendantsPageAsync(string pathPrefix, string? afterId, int pageSize, CancellationToken ct)
    {
        var filter = Builders<FolderDocument>.Filter.Regex(f => f.Path, new MongoDB.Bson.BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(pathPrefix)}"));
        if (afterId is not null)
        {
            filter &= Builders<FolderDocument>.Filter.Gt(f => f.Id, afterId);
        }

        return await _context.Folders.Find(filter).SortBy(f => f.Id).Limit(pageSize).ToListAsync(ct);
    }

    public Task UpdatePathAndAncestorsAsync(string folderId, string newPath, List<string> newAncestors, int newDepth, CancellationToken ct) =>
        _context.Folders.UpdateOneAsync(
            Builders<FolderDocument>.Filter.Eq(f => f.Id, folderId),
            Builders<FolderDocument>.Update
                .Set(f => f.Path, newPath)
                .Set(f => f.Ancestors, newAncestors)
                .Set(f => f.Depth, newDepth)
                .Set(f => f.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);

    public Task SetMovePendingAsync(string folderId, bool pending, CancellationToken ct) =>
        _context.Folders.UpdateOneAsync(
            Builders<FolderDocument>.Filter.Eq(f => f.Id, folderId),
            Builders<FolderDocument>.Update.Set(f => f.MovePending, pending),
            cancellationToken: ct);

    /// <summary>Applies a batch of computed path/ancestor/depth updates as a
    /// single bulk write - used by the background subtree-move job so no
    /// individual document is ever left in a partially-updated state.</summary>
    public async Task BulkUpdateHierarchyAsync(
        IReadOnlyList<(string Id, string Path, List<string> Ancestors, int Depth)> updates, CancellationToken ct)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var models = updates.Select(u => new UpdateOneModel<FolderDocument>(
            Builders<FolderDocument>.Filter.Eq(f => f.Id, u.Id),
            Builders<FolderDocument>.Update
                .Set(f => f.Path, u.Path)
                .Set(f => f.Ancestors, u.Ancestors)
                .Set(f => f.Depth, u.Depth)
                .Set(f => f.UpdatedAt, DateTime.UtcNow)));

        await _context.Folders.BulkWriteAsync(models, cancellationToken: ct);
    }
}
