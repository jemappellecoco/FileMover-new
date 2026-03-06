// using FileMoverWeb.Services;
// using FileMoverWeb.Models.Node;
// using Serilog;
// using Serilog.Events;

// Log.Logger = new LoggerConfiguration()
//     .MinimumLevel.Information()

//     // ✅ 砍掉框架噪音（Request starting/finished、HttpClient sending/received）
//     .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
//     .MinimumLevel.Override("System", LogEventLevel.Warning)

//     // ✅ 但保留 Host 啟動/關機訊息
//     .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)

//     .Enrich.FromLogContext()
//     .Enrich.WithThreadId()

//     .WriteTo.File(
//         path: "logs/fm-.log",
//         rollingInterval: RollingInterval.Day,
//         retainedFileCountLimit: 14,
//         outputTemplate:
//             "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({ThreadId}) {Message:lj}{NewLine}{Exception}"
//     )
//     .WriteTo.Console(
//         outputTemplate:
//             "{Timestamp:HH:mm:ss.fff} [{Level:u3}] ({ThreadId}) {Message:lj}{NewLine}{Exception}"
//     )
//     .CreateLogger();

// var builder = WebApplication.CreateBuilder(args);

// // ✅ 避免預設 provider 也跟著印（常見造成「太多/重複」）
// builder.Logging.ClearProviders();

// // ✅ 讓 Serilog 真正接管
// builder.Host.UseSerilog(Log.Logger, dispose: true);

// // ✅ 讀 appsettings
// builder.Host.ConfigureAppConfiguration((ctx, cfg) =>
// {
//     cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
//        .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
//                     optional: true, reloadOnChange: true)
//        .AddEnvironmentVariables();
// });

// // 1) Controllers
// builder.Services.AddControllers();

// // 2) Services
// builder.Services.AddSingleton<TaskPoller>();
// builder.Services.AddSingleton<NodeRuntimeRegistry>();

// builder.Services.AddHttpClient();
// builder.Services.AddHostedService<NodeHeartbeatService>();
// builder.Services.AddHostedService<MasterDispatchService>();

// builder.Services.AddSingleton<FileActionWorker>();
// builder.Services.AddSingleton<RestoreLookup>();
// builder.Services.AddSingleton<TaskRoutingService>();
// builder.Services.AddSingleton<RestoreTaskPoller>();
// builder.Services.AddSingleton<HistoryPoller>();
// builder.Services.AddSingleton<ProgressHub>();
// builder.Services.AddSingleton<DeleteVerifier>();
// builder.Services.AddSingleton<CopyVerifier>();
// builder.Services.AddSingleton<ArchivePoller>();
// builder.Services.AddSingleton<FtpSetting>();
// builder.Services.AddSingleton<FtpTransfer>();
// builder.Services.AddSingleton<JobTracker>();
// builder.Services.AddHostedService<DeleteScheduleService>();
// var role = builder.Configuration["Cluster:Role"];

// if (string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase))
// {
//     builder.Services.AddSingleton<ProgressHub>();
//     builder.Services.AddSingleton<IProgressReporter, HubProgressReporter>();
// }
// else
// {
//     builder.Services.AddHttpClient<IProgressReporter, HttpProgressReporter>();
// }

// var app = builder.Build();

// app.UseDefaultFiles();
// app.UseStaticFiles();
// app.MapControllers();
// app.Run();
using FileMoverWeb.Services;
using FileMoverWeb.Models.Node;
using Serilog;
using Serilog.Events;
using System.Windows.Forms;
using System.Drawing;
using System.Text.Json.Serialization;

// ===================== 1. Serilog 初始化 =====================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithThreadId()
    // 過濾頻繁的輪詢日誌
    .Filter.ByExcluding(logEvent => 
    {
        if (logEvent.Properties.TryGetValue("RequestPath", out var pathValue))
        {
            var path = pathValue.ToString().ToLower();
            return path.Contains("/api/progress") || path.Contains("/heartbeat");
        }
        return false;
    })
    .WriteTo.File(
        path: "logs/fm-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({ThreadId}) {Message:lj}{NewLine}{Exception}"
    )
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("===== FileMover 背景服務啟動中 =====");

    var builder = WebApplication.CreateBuilder(args);

    // 接管 Logging
    builder.Logging.ClearProviders();
    builder.Host.UseSerilog(Log.Logger, dispose: true);

    // ===================== 2. 註冊服務 =====================
    builder.Services.AddControllers()
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    // 基礎服務注入
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
    builder.Services.AddSingleton<FtpTransfer>();
    builder.Services.AddSingleton<JobTracker>();
    builder.Services.AddHostedService<DeleteScheduleService>();

    var role = builder.Configuration["Cluster:Role"] ?? "Master";
    if (string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<IProgressReporter, HubProgressReporter>();
    }
    else
    {
        builder.Services.AddHttpClient<IProgressReporter, HttpProgressReporter>();
    }

    var app = builder.Build();

    // ===================== 3. 系統匣啟動 (STA Thread) =====================
    var trayThread = new Thread(() =>
    {
        ApplicationConfiguration.Initialize();
        using NotifyIcon trayIcon = new NotifyIcon();

        // 載入 Icon
        string iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        trayIcon.Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Shield;
        
        trayIcon.Text = $"FileMover ({role})";
        trayIcon.Visible = true;

        // 右鍵選單
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("開啟日誌資料夾", null, (s, e) =>
{
    try
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, "logs");

        // 保證資料夾存在
        Directory.CreateDirectory(logPath);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = logPath,
            UseShellExecute = true
        });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "開啟日誌資料夾失敗");
    }
});
        
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("結束並關閉服務", null, (s, e) => {
            Log.Information("使用者終止程式");
            trayIcon.Visible = false;
            Log.CloseAndFlush();
            Environment.Exit(0);
        });

        trayIcon.ContextMenuStrip = contextMenu;
        Application.Run(); // 啟動 WinForms 訊息循環
    });

    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.IsBackground = true;
    trayThread.Start();

    // ===================== 4. Web 配置 =====================
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapControllers();

    Log.Information("Web API 已啟動於背景執行。");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "程式發生致命錯誤");
}
finally
{
    Log.CloseAndFlush();
}