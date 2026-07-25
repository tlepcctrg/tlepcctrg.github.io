using DMS.Api.BackgroundServices;
using DMS.Api.Events;
using DMS.Api.Models;
using DMS.Api.Repositories;

namespace DMS.Api.Services;

public sealed record MoveFolderResult(bool Accepted, string Message);

public sealed class FolderService
{
    private readonly FolderRepository _folders;
    private readonly IEventPublisher _events;
    private readonly IFolderMoveQueue _moveQueue;
    private readonly ILogger<FolderService> _logger;

    public FolderService(FolderRepository folders, IEventPublisher events, IFolderMoveQueue moveQueue, ILogger<FolderService> logger)
    {
        _folders = folders;
        _events = events;
        _moveQueue = moveQueue;
        _logger = logger;
    }

    public Task<FolderDocument> CreateAsync(string tenantId, string? parentId, string name, string ownerId, CancellationToken ct) =>
        _folders.CreateAsync(tenantId, parentId, name, ownerId, ct);

    public Task<FolderDocument?> GetAsync(string id, CancellationToken ct) => _folders.GetByIdAsync(id, ct);

    public Task<List<FolderDocument>> GetChildrenAsync(string parentId, int skip, int limit, CancellationToken ct) =>
        _folders.GetChildrenAsync(parentId, skip, limit, ct);

    public Task<List<FolderDocument>> GetAncestorsAsync(string folderId, CancellationToken ct) =>
        _folders.GetAncestorChainAsync(folderId, ct);

    /// <summary>
    /// Initiates a folder move. Rather than synchronously rewriting an
    /// arbitrarily large subtree, this marks the folder as "move pending"
    /// and returns immediately; a background job pages through descendants
    /// via the materialized-path prefix index and updates each document
    /// atomically, publishing a FolderMoved event once complete.
    /// </summary>
    public async Task<MoveFolderResult> RequestMoveAsync(string tenantId, string folderId, string newParentId, CancellationToken ct)
    {
        var folder = await _folders.GetByIdAsync(folderId, ct);
        if (folder is null)
        {
            return new MoveFolderResult(false, "Folder not found.");
        }

        var newParent = await _folders.GetByIdAsync(newParentId, ct);
        if (newParent is null)
        {
            return new MoveFolderResult(false, "Destination folder not found.");
        }

        if (newParent.Path.StartsWith(folder.Path, StringComparison.Ordinal))
        {
            return new MoveFolderResult(false, "Cannot move a folder into its own subtree.");
        }

        await _folders.SetMovePendingAsync(folderId, true, ct);

        await _moveQueue.EnqueueAsync(new FolderMoveJob(tenantId, folderId, newParentId), ct);

        _logger.LogInformation("Folder move requested: {FolderId} -> new parent {NewParentId}", folderId, newParentId);

        await _events.PublishAsync(new DomainEvent(
            EventTypes.FolderMoved,
            tenantId,
            folderId,
            DateTime.UtcNow,
            new { folderId, newParentId, status = "pending" }), ct);

        return new MoveFolderResult(true, "Move accepted; processing in background.");
    }
}
