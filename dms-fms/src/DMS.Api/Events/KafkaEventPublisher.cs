using System.Text.Json;
using Confluent.Kafka;
using DMS.Api.Options;
using Microsoft.Extensions.Options;

namespace DMS.Api.Events;

public interface IEventPublisher
{
    Task PublishAsync(DomainEvent domainEvent, CancellationToken ct = default);
}

/// <summary>
/// Publishes domain events onto the Kafka event bus. Consumers (search
/// indexing, audit trail, cache-invalidation, notifications) subscribe to
/// the same topic independently of DMS/FMS request/response flows.
/// </summary>
public sealed class KafkaEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaEventPublisher> logger)
    {
        _topic = options.Value.EventsTopic;
        _logger = logger;
        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync(DomainEvent domainEvent, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(domainEvent);
        try
        {
            await _producer.ProduceAsync(_topic, new Message<string, string>
            {
                Key = domainEvent.EntityId,
                Value = payload,
                Headers = new Headers { { "eventType", System.Text.Encoding.UTF8.GetBytes(domainEvent.EventType) } },
            }, ct);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish {EventType} event for {EntityId}", domainEvent.EventType, domainEvent.EntityId);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}
