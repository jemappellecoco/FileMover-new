using FileMoverWeb.Services;
using FileMoverWeb.Models.Node;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()

    // ✅ 砍掉框架噪音（Request starting/finished、HttpClient sending/received）
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)

    // ✅ 但保留 Host 啟動/關機訊息
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)

    .Enrich.FromLogContext()
    .Enrich.WithThreadId()

    .WriteTo.File(
        path: "logs/fm-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({ThreadId}) {Message:lj}{NewLine}{Exception}"
    )
    .WriteTo.Console(
        outputTemplate:
            "{Timestamp:HH:mm:ss.fff} [{Level:u3}] ({ThreadId}) {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// ✅ 避免預設 provider 也跟著印（常見造成「太多/重複」）
builder.Logging.ClearProviders();

// ✅ 讓 Serilog 真正接管
builder.Host.UseSerilog(Log.Logger, dispose: true);

// ✅ 讀 appsettings
builder.Host.ConfigureAppConfiguration((ctx, cfg) =>
{
    cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
       .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
                    optional: true, reloadOnChange: true)
       .AddEnvironmentVariables();
});

// 1) Controllers
builder.Services.AddControllers();

// 2) Services
builder.Services.AddSingleton<TaskPoller>();
builder.Services.AddSingleton<NodeRuntimeRegistry>();

builder.Services.AddHttpClient();
builder.Services.AddHostedService<NodeHeartbeatService>();
builder.Services.AddHostedService<MasterDispatchService>();

builder.Services.AddSingleton<FileActionWorker>();
builder.Services.AddSingleton<RestoreLookup>();
builder.Services.AddSingleton<TaskRoutingService>();
builder.Services.AddSingleton<RestoreTaskPoller>();
builder.Services.AddSingleton<HistoryPoller>();
builder.Services.AddSingleton<ProgressHub>();
builder.Services.AddSingleton<DeleteVerifier>();
builder.Services.AddSingleton<CopyVerifier>();
builder.Services.AddSingleton<ArchivePoller>();
builder.Services.AddSingleton<FtpSetting>();
var role = builder.Configuration["Cluster:Role"];

if (string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ProgressHub>();
    builder.Services.AddSingleton<IProgressReporter, HubProgressReporter>();
}
else
{
    builder.Services.AddHttpClient<IProgressReporter, HttpProgressReporter>();
}

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.Run();