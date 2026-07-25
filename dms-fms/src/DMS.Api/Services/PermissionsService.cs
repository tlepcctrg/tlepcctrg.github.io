using DMS.Api.Events;
using DMS.Api.Models;
using DMS.Api.Repositories;

namespace DMS.Api.Services;

public sealed class PermissionsService
{
    private readonly AclRepository _acl;
    private readonly FolderRepository _folders;
    private readonly FileRepository _files;
    private readonly PermissionEngine _engine;
    private readonly IEventPublisher _events;

    public PermissionsService(
        AclRepository acl, FolderRepository folders, FileRepository files, PermissionEngine engine, IEventPublisher events)
    {
        _acl = acl;
        _folders = folders;
        _files = files;
        _engine = engine;
        _events = events;
    }

    /// <summary>
    /// Grants/denies permissions on a single node. Because only explicit
    /// overrides are persisted (never a fully-materialized inherited set),
    /// this is an O(1) write regardless of how many descendants exist -
    /// there is no fan-out write to propagate permissions down the tree.
    /// </summary>
    public async Task GrantAsync(
        string tenantId, NodeType nodeType, string nodeId, string principalId,
        PermissionFlags permissions, PermissionEffect effect, CancellationToken ct)
    {
        var depth = await GetDepthAsync(nodeType, nodeId, ct);

        await _acl.UpsertAsync(new AclOverrideDocument
        {
            TenantId = tenantId,
            NodeId = nodeId,
            NodeType = nodeType,
            Depth = depth,
            PrincipalId = principalId,
            Permissions = permissions,
            Effect = effect,
        }, ct);

        await _engine.InvalidateAsync(nodeType, nodeId, ct);

        await _events.PublishAsync(new DomainEvent(
            EventTypes.PermissionChanged, tenantId, nodeId, DateTime.UtcNow,
            new { nodeType, nodeId, principalId, permissions, effect }), ct);
    }

    public async Task RevokeAsync(string tenantId, NodeType nodeType, string nodeId, string principalId, CancellationToken ct)
    {
        await _acl.RemoveAsync(nodeId, principalId, ct);
        await _engine.InvalidateAsync(nodeType, nodeId, ct);

        await _events.PublishAsync(new DomainEvent(
            EventTypes.PermissionChanged, tenantId, nodeId, DateTime.UtcNow,
            new { nodeType, nodeId, principalId, revoked = true }), ct);
    }

    public Task<EffectivePermissionResult> GetEffectiveAsync(string principalId, NodeType nodeType, string nodeId, CancellationToken ct) =>
        _engine.ResolveAsync(principalId, nodeType, nodeId, ct);

    private async Task<int> GetDepthAsync(NodeType nodeType, string nodeId, CancellationToken ct)
    {
        if (nodeType == NodeType.Folder)
        {
            var folder = await _folders.GetByIdAsync(nodeId, ct)
                ?? throw new InvalidOperationException($"Folder '{nodeId}' not found.");
            return folder.Depth;
        }

        var file = await _files.GetByIdAsync(nodeId, ct)
            ?? throw new InvalidOperationException($"File '{nodeId}' not found.");
        return file.Ancestors.Count;
    }
}
