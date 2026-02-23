using System;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace FileMoverWeb.Services
{
    public sealed class WorkerHeartbeatService : BackgroundService
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<WorkerHeartbeatService> _logger;
        private readonly IServiceProvider _sp;
        private readonly IHttpClientFactory _http;

        public WorkerHeartbeatService(
            IConfiguration cfg,
            ILogger<WorkerHeartbeatService> logger,
            IServiceProvider sp,
            IHttpClientFactory http)
        {
            _cfg = cfg;
            _logger = logger;
            _sp = sp;
            _http = http;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var nodeName  = (_cfg["Cluster:NodeName"] ?? "Unknown").Trim();
            var role      = (_cfg["Cluster:Role"] ?? "Slave").Trim();
            var isMaster  = string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase);

            var groupCode = (_cfg["FloorRouting:Group"] ?? _cfg["Cluster:Group"] ?? "").Trim();
            var hostName  = Dns.GetHostName();
            var ip        = ResolveLocalIp() ?? "127.0.0.1";

            _logger.LogInformation(
                "WorkerHeartbeatService started for node {Node}, role={Role}, group={Group}",
                nodeName, role, groupCode);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // ✅ 真實 running 數
                    var watchService = _sp.GetRequiredService<HistoryWatchService>();
                    var currentRunning = watchService.CurrentRunningCount;

                    var slotCfg = _sp.GetRequiredService<ISlotConfigProvider>();
                    var insertMaxConc = await slotCfg.GetEnabledSlotsAsync(stoppingToken);
                    if (insertMaxConc < 1) insertMaxConc = 1;
                    if (insertMaxConc > 10) insertMaxConc = 10;

                    if (isMaster)
                    {
                        // ✅ Master 才會碰 DB：用 SP resolve，避免 Slave DI 爆
                        var factory = _sp.GetRequiredService<DbConnectionFactory>();
                        using var conn = factory.Create();

                        const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.WorkerNode WHERE NodeName = @NodeName)
BEGIN
    UPDATE dbo.WorkerNode
    SET Role           = @Role,
        GroupCode      = @GroupCode,
        CurrentRunning = @CurrentRunning,
        LastHeartbeat  = SYSDATETIME(),
        HostName       = @HostName,
        IpAddress      = @IpAddress
    WHERE NodeName = @NodeName;
END
ELSE
BEGIN
    INSERT INTO dbo.WorkerNode
        (NodeName, Role, GroupCode, MaxConcurrency, CurrentRunning, LastHeartbeat, HostName, IpAddress)
    VALUES
        (@NodeName, @Role, @GroupCode, @InsertMaxConcurrency, @CurrentRunning, SYSDATETIME(), @HostName, @IpAddress);
END
";
                        await conn.ExecuteAsync(sql, new
                        {
                            NodeName = nodeName,
                            Role = role,
                            GroupCode = groupCode,
                            CurrentRunning = currentRunning,
                            HostName = hostName,
                            IpAddress = ip,
                            InsertMaxConcurrency = insertMaxConc
                        });
                    }
                    else
                    {
                        // ✅ Slave：改走 API 回報 Master
                        var baseUrl = (_cfg["Cluster:MasterBaseUrl"] ?? "").TrimEnd('/');
                        if (string.IsNullOrWhiteSpace(baseUrl))
                        {
                            _logger.LogWarning("Slave heartbeat skipped: missing Cluster:MasterBaseUrl");
                        }
                        else
                        {
                            var client = _http.CreateClient("MasterClient");

                            // 這個 payload 欄位名稱要跟你 NodesController 的 HeartbeatDto 對得上
                            var payload = new
                                {
                                    NodeName = nodeName,
                                    Role = role,

                                    // ✅ 一定要叫 Group（不是 GroupCode）
                                    Group = groupCode,

                                    MaxConcurrency = insertMaxConc,
                                    CurrentRunning = currentRunning,
                                    HostName = hostName,
                                    IpAddress = ip
                                };

                            // var resp = await client.PostAsJsonAsync($"{baseUrl}/api/nodes/heartbeat", payload, stoppingToken);
                            // resp.EnsureSuccessStatusCode();
                            var url = $"{baseUrl}/api/nodes/heartbeat";

                            // _logger.LogInformation("[HB->MASTER] POST {Url} node={Node} role={Role} group={Group} max={Max} running={Run}",
                            //     url, nodeName, role, groupCode, insertMaxConc, currentRunning);

                            var resp = await client.PostAsJsonAsync(url, payload, stoppingToken);

                            var body = await resp.Content.ReadAsStringAsync(stoppingToken);
                            // _logger.LogInformation("[HB->MASTER] status={StatusCode} body={Body}",
                            //     (int)resp.StatusCode, string.IsNullOrWhiteSpace(body) ? "(empty)" : body);

                            resp.EnsureSuccessStatusCode();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Heartbeat failed for node {Node}", nodeName);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        private static string? ResolveLocalIp()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip.ToString();
                }
            }
            catch { }
            return null;
        }
    }
}
