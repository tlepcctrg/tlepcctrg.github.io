using DMS.Api.Models;
using DMS.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Api.Controllers;

[ApiController]
[Route("api/permissions")]
public sealed class PermissionsController : ControllerBase
{
    private readonly PermissionsService _permissions;

    public PermissionsController(PermissionsService permissions) => _permissions = permissions;

    /// <summary>Grants/denies permission on a single node. O(1) write - no
    /// fan-out to descendants; propagation is resolved at read time.</summary>
    [HttpPost]
    public async Task<IActionResult> Grant([FromBody] GrantPermissionRequest request, CancellationToken ct)
    {
        await _permissions.GrantAsync(request.TenantId, request.NodeType, request.NodeId, request.PrincipalId, request.Permissions, request.Effect, ct);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Revoke([FromBody] RevokePermissionRequest request, CancellationToken ct)
    {
        await _permissions.RevokeAsync(request.TenantId, request.NodeType, request.NodeId, request.PrincipalId, ct);
        return NoContent();
    }

    /// <summary>Resolves the effective permission for a principal on a node,
    /// backed by the Redis L2 cache for sub-millisecond repeated checks.</summary>
    [HttpGet("effective/{nodeType}/{nodeId}")]
    public async Task<IActionResult> GetEffective(NodeType nodeType, string nodeId, [FromQuery] string principalId, CancellationToken ct)
    {
        var result = await _permissions.GetEffectiveAsync(principalId, nodeType, nodeId, ct);
        return Ok(result);
    }
}
