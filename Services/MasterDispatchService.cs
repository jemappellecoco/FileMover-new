using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Json;
using FileMoverWeb.Models.Node;
using FileMoverWeb.Models;
namespace FileMoverWeb.Services
{
    public class MasterDispatchService : BackgroundService
    {
        private readonly TaskPoller _poller;
        private readonly NodeRuntimeRegistry _registry;
        private readonly IConfiguration _cfg;
        private readonly ILogger<MasterDispatchService> _log;
        private readonly IHttpClientFactory _http;
        private readonly TaskRoutingService _routing;

        public MasterDispatchService(
            TaskPoller poller,
            NodeRuntimeRegistry registry,
            IConfiguration cfg,
            ILogger<MasterDispatchService> log,
            IHttpClientFactory http, 
            TaskRoutingService routing)
        {
            _poller = poller;
            _registry = registry;
            _cfg = cfg;
            _log = log;
            _http = http;
             _routing = routing; 
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var role = (_cfg["Cluster:Role"] ?? "").Trim();
            if (!string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase))
                return;

            _log.LogInformation("Master dispatch loop started.");

            var interval = int.TryParse(_cfg["Cluster:ScheduleIntervalSeconds"], out var sec) ? sec : 10;
            var allowMasterWork = _cfg.GetValue<bool>("Cluster:AllowMasterWork");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var timeout = int.TryParse(_cfg["Cluster:HeartbeatTimeoutSeconds"], out var hb) ? hb : 30;

                    var nodes = _registry.ListSnapshot(timeout);

                    foreach (var node in nodes)
                    {
                        // ✅ 如果不允許 master work，就略過非 Slave
                        if (!string.Equals(node.Role, "Slave", StringComparison.OrdinalIgnoreCase) && !allowMasterWork)
                            continue;

                        if (!string.Equals(node.Status, "Online", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // 你目前用 CurrentRunning 推算 free；先沿用（之後可改成 FreeSlots）
                        var free = node.MaxConcurrency - node.CurrentRunning;
                        if (free <= 0)
                            continue;
                        var myGroup = _cfg.GetValue<string>("Cluster:Group") ?? "";

                        // 1) Reserve 一批（注意：這裡必須是 reserve，不要先把 status 改 1）
                        var reserved = await _poller.DispatchFullAsync(node.NodeName,myGroup, free, stoppingToken);
                        if (reserved == null || reserved.Count == 0)
                            continue;

                        // 2) 取得 node endpoint（你說 IpAddress 是完整 http://ip:port）
                        var baseUrl = (node.IpAddress ?? "").Trim().TrimEnd('/');
                        if (string.IsNullOrWhiteSpace(baseUrl))
                        {
                            _log.LogWarning("[PUSH] baseUrl empty. node={node}", node.NodeName);

                            // reserve 了但沒 endpoint：清回去
                            foreach (var t in reserved)
                                await _poller.ClearReserveAsync(t.HistoryId, node.NodeName, stoppingToken);

                            continue;
                        }

                        var client = _http.CreateClient();
                        var receiveUrl = $"{baseUrl}/api/executor/receive";

                        var pushed = 0;

                        foreach (var t in reserved)
                        {
                            try
                            {
                                
                                // ✅ dispatch 當下決定 effective 路徑（只改 payload）
                                await _routing.ApplyEffectiveRoutingAsync(t, stoppingToken);
                                // await ApplyEffectiveRestoreRoutingAsync(t, stoppingToken);
                                // 保險：payload 帶 assigned_node
                                t.AssignedNode = node.NodeName;

                                _log.LogInformation("[PUSH] POST {url} hid={hid} node={node}",
                                    receiveUrl, t.HistoryId, node.NodeName);

                                // 3) Push（只做一次）
                                var resp = await client.PostAsJsonAsync(receiveUrl, t, stoppingToken);
                                if (!resp.IsSuccessStatusCode)
                                {
                                    var body = await resp.Content.ReadAsStringAsync(stoppingToken);
                                    throw new Exception($"push failed {(int)resp.StatusCode} body={body}");
                                }

                                pushed++;
                               
                            }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "[PUSH] failed hid={hid} node={node}",
                                    t.HistoryId, node.NodeName);

                                // push/commit 任一步失敗：清 reserve（因為還沒 commit 成 1）
                                try
                                {
                                    await _poller.ClearReserveAsync(t.HistoryId, node.NodeName, stoppingToken);
                                }
                                catch (Exception ex2)
                                {
                                    _log.LogError(ex2, "[PUSH] rollback ClearReserve failed hid={hid} node={node}",
                                        t.HistoryId, node.NodeName);
                                }
                            }
                        }

                        if (pushed > 0)
                        {
                            _log.LogInformation("Pushed {count} tasks to {node}", pushed, node.NodeName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Dispatch loop error");
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
        }
    }
}