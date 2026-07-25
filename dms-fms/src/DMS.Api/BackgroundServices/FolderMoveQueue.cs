using System.Threading.Channels;

namespace DMS.Api.BackgroundServices;

public sealed record FolderMoveJob(string TenantId, string FolderId, string NewParentId);

/// <summary>
/// In-process work queue (System.Threading.Channels) that decouples the
/// synchronous "move accepted" API response from the potentially large
/// background subtree rewrite performed by <see cref="FolderMoveBackgroundService"/>.
/// </summary>
public interface IFolderMoveQueue
{
    ValueTask EnqueueAsync(FolderMoveJob job, CancellationToken ct);
    ChannelReader<FolderMoveJob> Reader { get; }
}

public sealed class FolderMoveQueue : IFolderMoveQueue
{
    private readonly Channel<FolderMoveJob> _channel = Channel.CreateUnbounded<FolderMoveJob>();

    public ChannelReader<FolderMoveJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(FolderMoveJob job, CancellationToken ct) =>
        _channel.Writer.WriteAsync(job, ct);
}
