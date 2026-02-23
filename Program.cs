using FileMoverWeb.Services;
using FileMoverWeb.Extensions;
using System.Text.Json.Serialization;
using Polly;
using Serilog;
using Polly.Extensions.Http;
using System.Windows.Forms;
using System.Drawing;

// ===================== 1. 初始日誌設定 (Serilog) =====================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // 👈 1. 排除掉 Microsoft 框架層級的「請求開始/結束」日誌
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    // 👈 2. 針對特定路徑（API）進行「黑名單」過濾
    .Filter.ByExcluding(logEvent => 
    {
        if (logEvent.Properties.TryGetValue("RequestPath", out var pathValue))
        {
            var path = pathValue.ToString().ToLower();
            // 這裡放你不想看到的 API 路徑關鍵字
            return path.Contains("/api/progress/report") ||  // 過濾進度回報 (delta/complete)
                   path.Contains("/api/worker/report")   ||  // 過濾 Worker 狀態回報
                   path.Contains("/api/nodes/heartbeat") ||  // 過濾心跳
                   path.Contains("/jobs/pending")        ||  // 過濾 UI 輪詢
                   path.Contains("/history")             ||  // 過濾 UI 歷史紀錄查詢
                   path.Contains("/api/nodes");              // 過濾節點查詢
        }
        return false;
    })
    .WriteTo.File(
        path: "logs/fm-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("===== FileMover 啟動中 =====");

    var builder = WebApplication.CreateBuilder(args);

    // 套用 Serilog 並取代預設 Logging Provider
    builder.Host.UseSerilog();

    // ===================== 2. 原本的服務註冊 (DI) =====================
    builder.Configuration
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true);

    builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerDocumentation();

    var role = (builder.Configuration["Cluster:Role"] ?? "Master").Trim();
    var isMaster = string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase);
    var watcherEnabled = builder.Configuration.GetValue("Watcher:Enabled", true);
    var allowMasterWork = builder.Configuration.GetValue("Cluster:AllowMasterWork", false);

    var retryPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

    builder.Services.AddHttpClient("MasterClient", client =>
    {
        var baseUrl = builder.Configuration["Cluster:MasterBaseUrl"];
        if (!string.IsNullOrEmpty(baseUrl)) client.BaseAddress = new Uri(baseUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
    }).AddPolicyHandler(retryPolicy);

    builder.Services.AddSingleton<ICancelStore, CancelStore>();
    builder.Services.AddSingleton<IMoveRetryStore, MoveRetryStore>();
    builder.Services.AddSingleton<FtpSetting>();

    if (isMaster)
    {
        builder.Services.AddSingleton<DbConnectionFactory>();
        builder.Services.AddScoped<HistoryRepository>();
        builder.Services.AddSingleton<IJobProgress, JobProgress>();
        builder.Services.AddSingleton<ISlotConfigProvider, DbSlotConfigProvider>();
    }
    else
    {
        builder.Services.AddHttpClient<IJobProgress, RemoteJobProgress>().ConfigureHttpClient((sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IConfiguration>()["Cluster:MasterBaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl)) client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
        }).AddPolicyHandler(retryPolicy);
        builder.Services.AddSingleton<ISlotConfigProvider, ApiSlotConfigProvider>();
    }

    builder.Services.AddTransient<MoveWorker>();
    builder.Services.AddSingleton<ITaskQueue, TaskQueue>();
    builder.Services.AddHttpClient("WorkerClient", client => client.Timeout = TimeSpan.FromSeconds(10)).AddPolicyHandler(retryPolicy);

    builder.Services.AddSingleton<IProgressSink>(sp =>
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        if (isMaster) return new LocalProgressSink(sp.GetRequiredService<IJobProgress>());
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("MasterClient");
        return new RemoteProgressSink(http, cfg["Cluster:MasterBaseUrl"]!, sp.GetRequiredService<ILogger<RemoteProgressSink>>());
    });

    builder.Services.AddSingleton<HistoryWatchService>();
    builder.Services.AddSingleton<Func<int, CancellationToken, Task<bool>>>(sp =>
    {
        var watcher = sp.GetRequiredService<HistoryWatchService>();
        return (slot, ct) => watcher.RunOneIterationAsync(slot, ct);
    });

    builder.Services.AddSingleton<ISlotWorkerFactory, SlotWorkerFactory>();
    builder.Services.AddHostedService<SlotSupervisorService>();
    builder.Services.AddHostedService<WorkerHeartbeatService>();

    if (isMaster)
    {
        builder.Services.AddHostedService<MasterSchedulerService>();
        builder.Services.AddHostedService<DeleteScheduleService>();
    }

    builder.Services.AddCors(opt => opt.AddPolicy("frontend", p => p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

    var app = builder.Build();
// ===================== 3. 系統匣啟動邏輯 =====================
    Thread trayThread = new Thread(() =>
    {
        ApplicationConfiguration.Initialize();
        using NotifyIcon trayIcon = new NotifyIcon();

        // --- .ico 檢查邏輯開始 ---
        // 使用 AppContext.BaseDirectory 確保發佈後仍能精準定位檔案
        string iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        
        if (File.Exists(iconPath))
        {
            try 
            {
                trayIcon.Icon = new Icon(iconPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "載入 app.ico 失敗，改用系統預設圖示");
                trayIcon.Icon = SystemIcons.Shield;
            }
        }
        else
        {
            Log.Information("未偵測到 app.ico，使用系統預設盾牌圖示");
            trayIcon.Icon = SystemIcons.Shield; 
        }
        // --- .ico 檢查邏輯結束 ---

        trayIcon.Text = $"FileMover ({role})";
        trayIcon.Visible = true;

        var contextMenu = new ContextMenuStrip();
        
        contextMenu.Items.Add("開啟日誌資料夾", null, (s, e) => {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (Directory.Exists(logPath)) 
                System.Diagnostics.Process.Start("explorer.exe", logPath);
        });

        // contextMenu.Items.Add("開啟 Swagger 介面", null, (s, e) => {
        //     // 注意：這裡的 Port 號請確認與你的 appsettings.json 一致
        //     var url = "http://localhost:5000/swagger"; 
        //     System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        // });

        contextMenu.Items.Add("-");

        contextMenu.Items.Add("結束並關閉程式", null, (s, e) => {
            Log.Information("使用者手動關閉程式");
            trayIcon.Visible = false;
            // 確保 Serilog 紀錄完整寫入後退出
            Log.CloseAndFlush();
            Environment.Exit(0);
        });

        trayIcon.ContextMenuStrip = contextMenu;
        Application.Run();
    });
    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.Start();

    // ===================== 4. Master 啟動修復 =====================
    if (isMaster)
    {
        using var scope = app.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<HistoryRepository>();
        await repo.ResetRunningJobsAsync(CancellationToken.None);
    }

    // ===================== 5. 中間件 (Middleware) =====================
    app.UseSwaggerDocumentation();
    app.UseCors("frontend");
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapControllers();
    app.MapFallbackToFile("/index.html");

    Log.Information("Web 服務已就緒。");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "程式啟動時發生致命錯誤");
}
finally
{
    Log.CloseAndFlush();
}