using System.Text.Json;
using DMS.Api.Models;
using DMS.Api.Options;
using DMS.Api.Repositories;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace DMS.Api.Services;

public sealed record EffectivePermissionResult(bool Allowed, PermissionFlags Flags);

/// <summary>
/// Resolves effective permissions for a (principal, node) pair. Reads the
/// node's denormalized ancestor chain (already stored on the document - no
/// recursive fetch) and issues a single `$in` query against the ACL override
/// collection, then applies "closest ancestor wins, explicit deny beats
/// allow" precedence. Results are cached in Redis for sub-millisecond
/// repeated checks.
/// </summary>
public sealed class PermissionEngine
{
    private readonly FolderRepository _folders;
    private readonly FileRepository _files;
    private readonly AclRepository _acl;
    private readonly IConnectionMultiplexer _redis;
    private readonly int _cacheTtlSeconds;

    public PermissionEngine(
        FolderRepository folders,
        FileRepository files,
        AclRepository acl,
        IConnectionMultiplexer redis,
        IOptions<RedisOptions> redisOptions)
    {
        _folders = folders;
        _files = files;
        _acl = acl;
        _redis = redis;
        _cacheTtlSeconds = redisOptions.Value.PermissionCacheTtlSeconds;
    }

    public async Task<EffectivePermissionResult> ResolveAsync(
        string principalId, NodeType nodeType, string nodeId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var cacheKey = BuildCacheKey(principalId, nodeType, nodeId);

        var cached = await db.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            return JsonSerializer.Deserialize<EffectivePermissionResult>((string)cached!)!;
        }

        var (targetDepth, ancestorChain) = await GetChainAsync(nodeType, nodeId, ct);
        var candidateIds = new List<string>(ancestorChain) { nodeId };

        var overrides = await _acl.GetOverridesForNodesAsync(candidateIds, ct);
        var relevant = overrides.Where(o => o.PrincipalId == principalId).ToList();

        // Closest ancestor (highest depth) wins; an explicit Deny at the same
        // or deeper level always beats an Allow.
        var result = Resolve(relevant, targetDepth);

        await db.StringSetAsync(cacheKey, JsonSerializer.Serialize(result), TimeSpan.FromSeconds(_cacheTtlSeconds));

        return result;
    }

    /// <summary>Invalidates cached permission entries after an ACL change.
    /// At this scale a short TTL plus best-effort key deletion for the
    /// changed node is a pragmatic trade-off versus tracking a full reverse
    /// index of every cached descendant.</summary>
    public async Task InvalidateAsync(NodeType nodeType, string nodeId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var server = _redis.GetServer(_redis.GetEndPoints()[0]);
        var pattern = $"acl:{nodeType}:{nodeId}:*";
        await foreach (var key in ScanAsync(server, pattern, ct))
        {
            await db.KeyDeleteAsync(key);
        }
    }

    private static async IAsyncEnumerable<RedisKey> ScanAsync(
        IServer server, string pattern, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var key in server.Keys(pattern: pattern))
        {
            ct.ThrowIfCancellationRequested();
            yield return key;
            await Task.Yield();
        }
    }

    private async Task<(int Depth, List<string> Ancestors)> GetChainAsync(NodeType nodeType, string nodeId, CancellationToken ct)
    {
        if (nodeType == NodeType.Folder)
        {
            var folder = await _folders.GetByIdAsync(nodeId, ct)
                ?? throw new InvalidOperationException($"Folder '{nodeId}' not found.");
            return (folder.Depth, folder.Ancestors);
        }

        var file = await _files.GetByIdAsync(nodeId, ct)
            ?? throw new InvalidOperationException($"File '{nodeId}' not found.");
        return (file.Ancestors.Count, file.Ancestors);
    }

    private static EffectivePermissionResult Resolve(List<AclOverrideDocument> overrides, int targetDepth)
    {
        if (overrides.Count == 0)
        {
            return new EffectivePermissionResult(false, PermissionFlags.None);
        }

        var closest = overrides
            .OrderByDescending(o => o.Depth)
            .ThenByDescending(o => o.Effect == PermissionEffect.Deny)
            .First();

        var allowed = closest.Effect == PermissionEffect.Allow && closest.Permissions != PermissionFlags.None;
        return new EffectivePermissionResult(allowed, closest.Permissions);
    }

    private static string BuildCacheKey(string principalId, NodeType nodeType, string nodeId) =>
        $"acl:{nodeType}:{nodeId}:{principalId}";
}
