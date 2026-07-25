using DMS.Api.BackgroundServices;
using DMS.Api.Events;
using DMS.Api.Options;
using DMS.Api.Repositories;
using DMS.Api.Services;
using FMS.Contracts;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection(MongoOptions.SectionName));
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.Configure<FmsClientOptions>(builder.Configuration.GetSection(FmsClientOptions.SectionName));
builder.Services.Configure<TempZoneOptions>(builder.Configuration.GetSection(TempZoneOptions.SectionName));

// MongoDB - metadata & hierarchy store.
builder.Services.AddSingleton<MongoContext>();
builder.Services.AddScoped<FolderRepository>();
builder.Services.AddScoped<FileRepository>();
builder.Services.AddScoped<AclRepository>();

// Redis - distributed permission cache (L2).
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var options = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()
        ?? throw new InvalidOperationException("Redis configuration section is missing.");
    return ConnectionMultiplexer.Connect(options.ConnectionString);
});

// Kafka - domain event bus.
builder.Services.AddSingleton<IEventPublisher, KafkaEventPublisher>();

// gRPC client to FMS (internal control-plane calls only).
builder.Services.AddGrpcClient<StorageService.StorageServiceClient>((sp, o) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FmsClientOptions>>().Value;
    o.Address = new Uri(options.Address);
});

// Domain services.
builder.Services.AddScoped<PermissionEngine>();
builder.Services.AddScoped<FolderService>();
builder.Services.AddScoped<FileUploadService>();
builder.Services.AddScoped<PermissionsService>();

// Background processing.
builder.Services.AddSingleton<IFolderMoveQueue, FolderMoveQueue>();
builder.Services.AddHostedService<FolderMoveBackgroundService>();
builder.Services.AddHostedService<TempCleanupBackgroundService>();
builder.Services.AddHostedService<KafkaEventConsumerService>();

builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var mongoContext = scope.ServiceProvider.GetRequiredService<MongoContext>();
    await mongoContext.EnsureIndexesAsync();
}


app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
