using DMS.Api.Models;
using MongoDB.Driver;

namespace DMS.Api.Repositories;

public sealed class FileRepository
{
    private readonly MongoContext _context;

    public FileRepository(MongoContext context) => _context = context;

    public Task InsertAsync(FileDocument file, CancellationToken ct) =>
        _context.Files.InsertOneAsync(file, cancellationToken: ct);

    public Task<FileDocument?> GetByIdAsync(string id, CancellationToken ct) =>
        _context.Files.Find(f => f.Id == id).FirstOrDefaultAsync(ct)!;

    /// <summary>Deduplication lookup: finds an already-committed file with the
    /// same content hash so a new upload can become a reference instead of a
    /// physical copy.</summary>
    public Task<FileDocument?> FindCommittedByHashAsync(string tenantId, string contentHash, CancellationToken ct) =>
        _context.Files.Find(f => f.TenantId == tenantId && f.ContentHash == contentHash && f.Status == FileStatus.Committed)
            .FirstOrDefaultAsync(ct)!;

    public Task MarkCommittedAsync(string id, string storageKey, long size, string contentHash, CancellationToken ct) =>
        _context.Files.UpdateOneAsync(
            Builders<FileDocument>.Filter.Eq(f => f.Id, id),
            Builders<FileDocument>.Update
                .Set(f => f.Status, FileStatus.Committed)
                .Set(f => f.StorageKey, storageKey)
                .Set(f => f.Size, size)
                .Set(f => f.ContentHash, contentHash)
                .Set(f => f.TempExpiryAt, (DateTime?)null)
                .Set(f => f.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);

    public Task MarkDeletedAsync(string id, CancellationToken ct) =>
        _context.Files.UpdateOneAsync(
            Builders<FileDocument>.Filter.Eq(f => f.Id, id),
            Builders<FileDocument>.Update.Set(f => f.Status, FileStatus.Deleted).Set(f => f.UpdatedAt, DateTime.UtcNow),
            cancellationToken: ct);

    public Task IncrementRefCountAsync(string id, CancellationToken ct) =>
        _context.Files.UpdateOneAsync(
            Builders<FileDocument>.Filter.Eq(f => f.Id, id),
            Builders<FileDocument>.Update.Inc(f => f.RefCount, 1),
            cancellationToken: ct);

    /// <summary>Finds pending (never-committed) uploads past their TTL, for
    /// the temp-zone cleanup sweep.</summary>
    public Task<List<FileDocument>> GetExpiredPendingAsync(int limit, CancellationToken ct) =>
        _context.Files.Find(f => f.Status == FileStatus.Pending && f.TempExpiryAt < DateTime.UtcNow)
            .Limit(limit)
            .ToListAsync(ct);

    public Task<List<FileDocument>> GetByFolderIdAsync(string folderId, CancellationToken ct) =>
        _context.Files.Find(f => f.FolderId == folderId).ToListAsync(ct);

    /// <summary>Bulk-updates the denormalized ancestor chain on every file
    /// belonging to a folder that was just relocated, so permission
    /// resolution for files stays consistent with the new hierarchy.</summary>
    public async Task BulkUpdateAncestorsAsync(IReadOnlyList<(string Id, List<string> Ancestors)> updates, CancellationToken ct)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var models = updates.Select(u => new UpdateOneModel<FileDocument>(
            Builders<FileDocument>.Filter.Eq(f => f.Id, u.Id),
            Builders<FileDocument>.Update.Set(f => f.Ancestors, u.Ancestors).Set(f => f.UpdatedAt, DateTime.UtcNow)));

        await _context.Files.BulkWriteAsync(models, cancellationToken: ct);
    }
}
