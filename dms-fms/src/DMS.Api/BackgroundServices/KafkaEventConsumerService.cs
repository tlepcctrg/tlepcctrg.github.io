using System.Text.Json;
using Confluent.Kafka;
using DMS.Api.Events;
using DMS.Api.Options;
using Microsoft.Extensions.Options;

namespace DMS.Api.BackgroundServices;

/// <summary>
/// Consumes domain events from the Kafka bus. In this reference
/// implementation it demonstrates the asynchronous, decoupled processing
/// pattern described in the architecture (audit logging + permission cache
/// fan-out); a production deployment would run dedicated consumer groups
/// per concern (search indexing, audit, notifications) instead of a single
/// in-process consumer.
/// </summary>
public sealed class KafkaEventConsumerService : BackgroundService
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaEventConsumerService> _logger;

    public KafkaEventConsumerService(IOptions<KafkaOptions> options, ILogger<KafkaEventConsumerService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => Consume(stoppingToken), stoppingToken);

    private void Consume(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_options.EventsTopic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning(ex, "Kafka consume error");
                    continue;
                }

                if (result is null)
                {
                    continue;
                }

                HandleEvent(result.Message.Value);
            }
        }
        finally
        {
            consumer.Close();
        }
    }

    private void HandleEvent(string payload)
    {
        try
        {
            var domainEvent = JsonSerializer.Deserialize<DomainEvent>(payload);
            if (domainEvent is null)
            {
                return;
            }

            _logger.LogInformation("Consumed event {EventType} for entity {EntityId} (tenant {TenantId})",
                domainEvent.EventType, domainEvent.EntityId, domainEvent.TenantId);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize event payload");
        }
    }
}
