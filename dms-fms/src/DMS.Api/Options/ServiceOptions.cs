namespace DMS.Api.Options;

public sealed class MongoOptions
{
    public const string SectionName = "Mongo";
    public required string ConnectionString { get; init; }
    public required string Database { get; init; }
}

public sealed class RedisOptions
{
    public const string SectionName = "Redis";
    public required string ConnectionString { get; init; }
    public int PermissionCacheTtlSeconds { get; init; } = 60;
}

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";
    public required string BootstrapServers { get; init; }
    public string EventsTopic { get; init; } = "dms.events";
    public string ConsumerGroupId { get; init; } = "dms-api-consumer";
}

public sealed class FmsClientOptions
{
    public const string SectionName = "FmsClient";
    public required string Address { get; init; }
}

public sealed class TempZoneOptions
{
    public const string SectionName = "TempZone";
    public int RetentionHours { get; init; } = 24;
    public int SweepIntervalSeconds { get; init; } = 300;
}
