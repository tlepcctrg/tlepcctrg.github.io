using DMS.Api.Models;
using MongoDB.Driver;

namespace DMS.Api.Repositories;

public sealed class AclRepository
{
    private readonly MongoContext _context;

    public AclRepository(MongoContext context) => _context = context;

    /// <summary>
    /// Fetches every explicit override for the given set of node ids (the
    /// target node plus its full ancestor chain) in a single round trip -
    /// this is what keeps ACL resolution O(1) regardless of hierarchy depth.
    /// </summary>
    public Task<List<AclOverrideDocument>> GetOverridesForNodesAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct) =>
        _context.AclOverrides.Find(Builders<AclOverrideDocument>.Filter.In(a => a.NodeId, nodeIds))
            .ToListAsync(ct);

    public Task UpsertAsync(AclOverrideDocument acl, CancellationToken ct) =>
        _context.AclOverrides.ReplaceOneAsync(
            Builders<AclOverrideDocument>.Filter.Eq(a => a.NodeId, acl.NodeId) &
            Builders<AclOverrideDocument>.Filter.Eq(a => a.PrincipalId, acl.PrincipalId),
            acl,
            new ReplaceOptions { IsUpsert = true },
            ct);

    public Task RemoveAsync(string nodeId, string principalId, CancellationToken ct) =>
        _context.AclOverrides.DeleteOneAsync(
            a => a.NodeId == nodeId && a.PrincipalId == principalId, ct);
}
