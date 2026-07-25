using DMS.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Api.Controllers;

[ApiController]
[Route("api/folders")]
public sealed class FoldersController : ControllerBase
{
    private readonly FolderService _folderService;

    public FoldersController(FolderService folderService) => _folderService = folderService;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateFolderRequest request, CancellationToken ct)
    {
        var folder = await _folderService.CreateAsync(request.TenantId, request.ParentId, request.Name, request.OwnerId, ct);
        return CreatedAtAction(nameof(Get), new { id = folder.Id }, folder);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var folder = await _folderService.GetAsync(id, ct);
        return folder is null ? NotFound() : Ok(folder);
    }

    [HttpGet("{id}/children")]
    public async Task<IActionResult> GetChildren(string id, [FromQuery] int skip, [FromQuery] int limit, CancellationToken ct)
    {
        var children = await _folderService.GetChildrenAsync(id, skip, limit <= 0 ? 50 : Math.Min(limit, 500), ct);
        return Ok(children);
    }

    [HttpGet("{id}/ancestors")]
    public async Task<IActionResult> GetAncestors(string id, CancellationToken ct)
    {
        var ancestors = await _folderService.GetAncestorsAsync(id, ct);
        return Ok(ancestors);
    }

    /// <summary>
    /// Accepts a folder move request. Returns immediately with 202 Accepted;
    /// the subtree rewrite happens asynchronously in the background.
    /// </summary>
    [HttpPost("{id}/move")]
    public async Task<IActionResult> Move(string id, [FromBody] MoveFolderRequest request, CancellationToken ct)
    {
        var result = await _folderService.RequestMoveAsync(request.TenantId, id, request.NewParentId, ct);
        return result.Accepted ? Accepted(result) : BadRequest(result.Message);
    }
}
