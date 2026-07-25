using DMS.Api.Models;
using DMS.Api.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace DMS.Api.Repositories;

/// <summary>
/// Central MongoDB access point. Owns collection handles and index creation.
/// Collections are logically shardable by a hashed TenantId in production
/// (see docker-compose / README for the sharding note).
/// </summary>
public sealed class MongoContext
{
    public IMongoDatabase Database { get; }
    public IMongoCollection<FolderDocument> Folders { get; }
    public IMongoCollection<FileDocument> Files { get; }
    public IMongoCollection<AclOverrideDocument> AclOverrides { get; }

    public MongoContext(IOptions<MongoOptions> options)
    {
        var client = new MongoClient(options.Value.ConnectionString);
        Database = client.GetDatabase(options.Value.Database);
        Folders = Database.GetCollection<FolderDocument>("folders");
        Files = Database.GetCollection<FileDocument>("files");
        AclOverrides = Database.GetCollection<AclOverrideDocument>("acl_overrides");
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var folderIndexes = new List<CreateIndexModel<FolderDocument>>
        {
            new(Builders<FolderDocument>.IndexKeys.Ascending(f => f.TenantId).Ascending(f => f.Path)),
            new(Builders<FolderDocument>.IndexKeys.Ascending(f => f.Ancestors)),
            new(Builders<FolderDocument>.IndexKeys.Ascending(f => f.ParentId).Ascending(f => f.Name)),
        };
        await Folders.Indexes.CreateManyAsync(folderIndexes, ct);

        var fileIndexes = new List<CreateIndexModel<FileDocument>>
        {
            new(Builders<FileDocument>.IndexKeys.Ascending(f => f.FolderId)),
            new(Builders<FileDocument>.IndexKeys.Ascending(f => f.Ancestors)),
            new(Builders<FileDocument>.IndexKeys.Ascending(f => f.ContentHash)),
            new(
                Builders<FileDocument>.IndexKeys.Ascending(f => f.TempExpiryAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
        };
        await Files.Indexes.CreateManyAsync(fileIndexes, ct);

        var aclIndexes = new List<CreateIndexModel<AclOverrideDocument>>
        {
            new(Builders<AclOverrideDocument>.IndexKeys.Ascending(a => a.NodeId)),
            new(Builders<AclOverrideDocument>.IndexKeys.Ascending(a => a.PrincipalId)),
        };
        await AclOverrides.Indexes.CreateManyAsync(aclIndexes, ct);
    }
}
