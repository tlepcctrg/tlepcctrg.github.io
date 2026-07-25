using Amazon.Runtime;
using Amazon.S3;
using FMS.Api.Options;
using FMS.Api.Services;
using FMS.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var options = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
        ?? throw new InvalidOperationException("Storage configuration section is missing.");

    var credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
    var config = new AmazonS3Config
    {
        ServiceURL = options.ServiceUrl,
        ForcePathStyle = options.ForcePathStyle,
        AuthenticationRegion = options.Region,
    };
    return new AmazonS3Client(credentials, config);
});

builder.Services.AddSingleton<IObjectStorageAdapter, S3ObjectStorageAdapter>();

builder.Services.AddGrpc();
builder.Services.AddGrpcHealthChecks();

var app = builder.Build();

app.MapGrpcService<StorageServiceImpl>();
app.MapGrpcHealthChecksService();
app.MapGet("/", () => "FMS internal gRPC endpoint. Not intended for direct client access; invoke exclusively via DMS.");

app.Run();
