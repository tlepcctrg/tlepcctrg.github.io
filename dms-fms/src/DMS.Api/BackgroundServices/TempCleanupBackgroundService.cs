using DMS.Api.Events;
using DMS.Api.Options;
using DMS.Api.Repositories;
using FMS.Contracts;
using Microsoft.Extensions.Options;

namespace DMS.Api.BackgroundServices;

/// <summary>
/// Periodically sweeps the Temporary Storage Zone: pending file records
/// whose TTL has expired (never committed by the client) are purged both
/// from FMS object storage and from MongoDB. This is idempotent and safe to
/// re-run - it is the application-level safety net that complements the
/// provider-native (S3/MinIO) bucket lifecycle expiration rule.
/// </summary>
public sealed class TempCleanupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TempZoneOptions _options;
    private readonly ILogger<TempCleanupBackgroundService> _logger;

    public TempCleanupBackgroundService(
        IServiceScopeFactory scopeFactory, IOptions<TempZoneOptions> options, ILogger<TempCleanupBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.SweepIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Temp zone sweep failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<FileRepository>();
        var fms = scope.ServiceProvider.GetRequiredService<StorageService.StorageServiceClient>();
        var events = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var expired = await files.GetExpiredPendingAsync(limit: 200, ct);
        if (expired.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Temp zone sweep: reclaiming {Count} expired pending uploads", expired.Count);

        foreach (var file in expired)
        {
            try
            {
                await fms.DeleteObjectAsync(new DeleteObjectRequest { TenantId = file.TenantId, ObjectKey = file.StorageKey }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete expired temp object {Key}; relying on bucket lifecycle rule", file.StorageKey);
            }

            await files.MarkDeletedAsync(file.Id, ct);

            await events.PublishAsync(new DomainEvent(
                EventTypes.FileDeleted, file.TenantId, file.Id, DateTime.UtcNow,
                new { file.Id, reason = "temp_expired" }), ct);
        }
    }
}
