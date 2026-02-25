using FileMoverWeb.Services;

var builder = WebApplication.CreateBuilder(args);

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

// ✅ 加這兩行（註冊 Heartbeat）
builder.Services.AddHttpClient();
builder.Services.AddHostedService<NodeHeartbeatService>();
builder.Services.AddHostedService<MasterDispatchService>();
builder.Services.AddSingleton<FileActionWorker>();

var app = builder.Build();

var reg = app.Services.GetRequiredService<NodeRuntimeRegistry>();
var cfg = app.Services.GetRequiredService<IConfiguration>();

var nodeName = cfg["Cluster:NodeName"] ?? Environment.MachineName;
var role     = cfg["Cluster:Role"] ?? "Unknown";
var group    = cfg["Cluster:Group"] ?? "";
var selfUrl  = cfg["Cluster:SelfBaseUrl"] ?? "";  // 建議改成 SelfBaseUrl

var maxConcurrency =
    cfg.GetValue<int?>("Cluster:MaxConcurrency")
    ?? 1;

// ✅ 只有 Master 才寫入 registry
if (role.Equals("Master", StringComparison.OrdinalIgnoreCase))
{
    reg.UpsertFree(new NodeFreeReportDto
    {
        Node = nodeName,
        Role = role,
        Group = group,
        HostName = Environment.MachineName,
        IpAddress = selfUrl,
        MaxConcurrency = Math.Max(1, maxConcurrency),
        FreeSlots = Math.Max(1, maxConcurrency)
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.Run();