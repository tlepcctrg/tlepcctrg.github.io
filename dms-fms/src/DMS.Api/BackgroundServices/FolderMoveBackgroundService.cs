using DMS.Api.Events;
using DMS.Api.Repositories;

namespace DMS.Api.BackgroundServices;

/// <summary>
/// Consumes folder-move jobs and performs the subtree rewrite in bounded
/// pages using the materialized-path prefix index, so a move of a folder
/// with millions of descendants never blocks the API request nor rewrites
/// the whole subtree in a single unbounded transaction. Each page is applied
/// as an atomic bulk write, so readers always observe either the pre-move or
/// post-move state for any given document - never a partially-updated one.
/// </summary>
public sealed class FolderMoveBackgroundService : BackgroundService
{
    private const int PageSize = 500;

    private readonly IFolderMoveQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FolderMoveBackgroundService> _logger;

    public FolderMoveBackgroundService(IFolderMoveQueue queue, IServiceScopeFactory scopeFactory, ILogger<FolderMoveBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Folder move job failed for folder {FolderId}", job.FolderId);
            }
        }
    }

    private async Task ProcessAsync(FolderMoveJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var folders = scope.ServiceProvider.GetRequiredService<FolderRepository>();
        var files = scope.ServiceProvider.GetRequiredService<FileRepository>();
        var events = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var folder = await folders.GetByIdAsync(job.FolderId, ct);
        var newParent = await folders.GetByIdAsync(job.NewParentId, ct);
        if (folder is null || newParent is null)
        {
            _logger.LogWarning("Folder move aborted: source or destination not found ({FolderId} -> {NewParentId})",
                job.FolderId, job.NewParentId);
            return;
        }

        var oldPathPrefix = folder.Path;
        var newPathPrefix = $"{newParent.Path}{folder.Id}/";
        var newAncestorsBase = new List<string>(newParent.Ancestors) { newParent.Id };
        var folderDepth = folder.Depth;

        string? afterId = null;
        var totalMoved = 0;

        while (true)
        {
            var page = await folders.GetDescendantsPageAsync(oldPathPrefix, afterId, PageSize, ct);
            if (page.Count == 0)
            {
                break;
            }

            var updates = new List<(string Id, string Path, List<string> Ancestors, int Depth)>(page.Count);
            foreach (var node in page)
            {
                var suffix = node.Ancestors.Skip(folderDepth + 1).ToList();
                var newAncestors = new List<string>(newAncestorsBase) { folder.Id };
                newAncestors.AddRange(suffix);
                var newPath = newPathPrefix + node.Path[oldPathPrefix.Length..];

                updates.Add((node.Id, newPath, newAncestors, newAncestors.Count));

                // Cascade the same ancestor rewrite to files owned by this folder.
                var folderFiles = await files.GetByFolderIdAsync(node.Id, ct);
                if (folderFiles.Count > 0)
                {
                    var fileUpdates = folderFiles.Select(f => (f.Id, newAncestors)).ToList();
                    await files.BulkUpdateAncestorsAsync(fileUpdates, ct);
                }
            }

            await folders.BulkUpdateHierarchyAsync(updates, ct);

            totalMoved += page.Count;
            afterId = page[^1].Id;

            if (page.Count < PageSize)
            {
                break;
            }
        }

        await folders.SetMovePendingAsync(job.FolderId, false, ct);

        _logger.LogInformation("Folder move completed for {FolderId}: {Count} nodes updated", job.FolderId, totalMoved);

        await events.PublishAsync(new DomainEvent(
            EventTypes.FolderMoved, job.TenantId, job.FolderId, DateTime.UtcNow,
            new { job.FolderId, job.NewParentId, status = "completed", nodesUpdated = totalMoved }), ct);
    }
}
