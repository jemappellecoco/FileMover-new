using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Json;
using FileMoverWeb.Services;
using FileMoverWeb.Models;

namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("jobs")]
    public sealed class JobsController : ControllerBase
    {
        private readonly TaskPoller _poller;
        private readonly IConfiguration _cfg;
        private readonly NodeRuntimeRegistry _registry;
        private readonly ILogger<JobsController> _log;
        private readonly IHttpClientFactory _http;

        public JobsController(
            TaskPoller poller,
            IConfiguration cfg,
            NodeRuntimeRegistry registry,
            ILogger<JobsController> log,
            IHttpClientFactory http)
        {
            _poller = poller;
            _cfg = cfg;
            _registry = registry;
            _log = log;
            _http = http;
        }

        // GET /jobs/pending  (純列出給 UI)
        [HttpGet("pending")]
        public async Task<IActionResult> Pending(CancellationToken ct = default)
        {
            var rows = await _poller.GetPendingTasksAsync(ct);
            _log.LogInformation("[PENDING] returned {count} rows", rows.Count);
            return Ok(rows);
        }

        // POST /jobs/dispatch  (手動觸發一次派工)
        // 策略：Reserve(只寫 assigned_node) -> Push -> (狀態由 Slave /api/jobs/report 寫回 DB)
        // [HttpPost("dispatch")]
        // public async Task<IActionResult> Dispatch(CancellationToken ct = default)
        // {
        //     if (!IsMaster())
        //         return Forbid();

        //     _log.LogInformation("========== DISPATCH START ==========");

        //     var timeoutSec = int.TryParse(_cfg["Cluster:HeartbeatTimeoutSeconds"], out var v) ? v : 30;

        //     // 1) Online nodes
        //     var nodes = _registry.ListSnapshot(timeoutSec)
        //         .Where(n => string.Equals(n.Status, "Online", StringComparison.OrdinalIgnoreCase))
        //         .ToList();

        //     if (nodes.Count == 0)
        //     {
        //         _log.LogWarning("[DISPATCH] no online nodes");
        //         return Ok(new { ok = true, total = 0, assigned = 0, skipped = 0, reason = "no_online_nodes", items = Array.Empty<object>() });
        //     }

        //     // 2) Pending tasks (controller 再過濾一次未 assigned)
        //     var tasks = await _poller.GetPendingTasksAsync(ct);
        //     var candidates = tasks.Where(t => string.IsNullOrWhiteSpace(t.AssignedNode)).ToList();

        //     var items = new List<object>();
        //     var assigned = 0;
        //     var skipped = 0;

        //     foreach (var t in candidates)
        //     {
        //         ct.ThrowIfCancellationRequested();

        //         // 3) 挑節點：Slave 優先、空位多優先
        //         var ordered = nodes
        //             .Select(n => new
        //             {
        //                 Node = n,
        //                 Free = Math.Max(0, n.MaxConcurrency - n.CurrentRunning)
        //             })
        //             .Where(x => x.Free > 0)
        //             .OrderByDescending(x => string.Equals(x.Node.Role, "Slave", StringComparison.OrdinalIgnoreCase))
        //             .ThenByDescending(x => x.Free)
        //             .Select(x => x.Node)
        //             .ToList();

        //         if (ordered.Count == 0)
        //         {
        //             _log.LogWarning("[DISPATCH] no slot available, stop at hid={hid}", t.HistoryId);
        //             break;
        //         }

        //         NodeStatusDto? chosen = null;

        //         // 4) 先在 registry 佔 slot（避免同一輪派工超發）
        //         foreach (var n in ordered)
        //         {
        //             if (_registry.TryConsume(n.NodeName, 1))
        //             {
        //                 chosen = n;
        //                 break;
        //             }
        //         }

        //         if (chosen is null)
        //             continue;

        //         try
        //         {
        //             // 5) Reserve：只寫 assigned_node，不改 file_status
        //             var reserved = await _poller.TryReserveAsync(t.HistoryId, chosen.NodeName, ct);
        //             if (!reserved)
        //             {
        //                 _registry.AddFree(chosen.NodeName, 1);
        //                 items.Add(new { historyId = t.HistoryId, result = "reserve_failed", node = chosen.NodeName });
        //                 skipped++;
        //                 continue;
        //             }

        //             // 6) Push
        //             t.AssignedNode = chosen.NodeName;

        //             var pushOk = await PushTaskToNodeAsync(chosen, t, ct);
        //             if (!pushOk)
        //             {
        //                 // push 失敗：清 reserve + 回補 slot
        //                 await _poller.ClearReserveAsync(t.HistoryId, chosen.NodeName, ct);
        //                 _registry.AddFree(chosen.NodeName, 1);

        //                 items.Add(new { historyId = t.HistoryId, result = "push_failed", node = chosen.NodeName });
        //                 skipped++;
        //                 continue;
        //             }

        //             // ✅ 成功派出：不改 DB status（由 Slave report 寫回）
        //             items.Add(new { historyId = t.HistoryId, result = "assigned", node = chosen.NodeName });
        //             assigned++;
        //         }
        //         catch (Exception ex)
        //         {
        //             _log.LogError(ex, "[DISPATCH] error hid={hid} node={node}", t.HistoryId, chosen.NodeName);

        //             // 發生例外：盡力回滾 reserve + slot
        //             try { await _poller.ClearReserveAsync(t.HistoryId, chosen.NodeName, ct); } catch { /* ignore */ }
        //             try { _registry.AddFree(chosen.NodeName, 1); } catch { /* ignore */ }

        //             items.Add(new { historyId = t.HistoryId, result = "error", node = chosen.NodeName, message = ex.Message });
        //             skipped++;
        //         }
        //     }

        //     _log.LogInformation("========== DISPATCH END: {assigned} assigned, {skipped} skipped ==========", assigned, skipped);

        //     return Ok(new
        //     {
        //         ok = true,
        //         total = candidates.Count,
        //         assigned,
        //         skipped,
        //         items
        //     });
        // }

        // private async Task<bool> PushTaskToNodeAsync(NodeStatusDto node, HistoryTask task, CancellationToken ct)
        // {
        //     var baseUrl = (node.IpAddress ?? "").Trim().TrimEnd('/');
        //     if (string.IsNullOrWhiteSpace(baseUrl))
        //     {
        //         _log.LogError("[PUSH] baseUrl empty node={node}", node.NodeName);
        //         return false;
        //     }

        //     var targetUrl = $"{baseUrl}/api/executor/receive";

        //     try
        //     {
        //         var client = _http.CreateClient();

        //         _log.LogInformation("[PUSH] POST {url} hid={hid} node={node}",
        //             targetUrl, task.HistoryId, node.NodeName);

        //         var resp = await client.PostAsJsonAsync(targetUrl, task, ct);
        //         if (!resp.IsSuccessStatusCode)
        //         {
        //             var body = await resp.Content.ReadAsStringAsync(ct);
        //             _log.LogError("[PUSH] failed hid={hid} node={node} status={status} body={body}",
        //                 task.HistoryId, node.NodeName, (int)resp.StatusCode, body);
        //             return false;
        //         }

        //         return true;
        //     }
        //     catch (Exception ex)
        //     {
        //         _log.LogError(ex, "[PUSH] exception hid={hid} node={node} url={url}",
        //             task.HistoryId, node.NodeName, targetUrl);
        //         return false;
        //     }
        // }

        private bool IsMaster()
            => string.Equals((_cfg["Cluster:Role"] ?? "").Trim(), "Master", StringComparison.OrdinalIgnoreCase);
    }
}