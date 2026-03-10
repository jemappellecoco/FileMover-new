using System;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Models;
using FileMoverWeb.Services;


namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("api/executor")]
    public sealed class ExecutorController : ControllerBase
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<ExecutorController> _log;
        private readonly FileActionWorker _worker;
        private readonly IHttpClientFactory _http;
        private readonly IHostApplicationLifetime _life;
        private readonly JobTracker _tracker;

        public ExecutorController(
            IConfiguration cfg,
            ILogger<ExecutorController> log,
            FileActionWorker worker,
            IHttpClientFactory http,
            IHostApplicationLifetime life,
            JobTracker tracker)
        {
            _cfg = cfg;
            _log = log;
            _worker = worker;
            _http = http;
            _life = life;
            _tracker = tracker;
        }
/// <summary>
        /// 批次取消任務 (由 Master 調用)
        /// </summary>
        [HttpPost("cancel-batch")]
        public IActionResult CancelBatch([FromBody] List<int> ids)
        {
            if (ids == null || ids.Count == 0) return BadRequest("No IDs provided");

            var results = new Dictionary<int, bool>();
            foreach (var id in ids)
            {
                // 發送中斷信號，不在此處 Unregister，由 RunAndReportAsync 的 finally 處理
                bool ok = _tracker.Cancel(id);
                results[id] = ok;
                
                if (ok) _log.LogWarning("[EXEC] Batch Cancel: hid={id} signal sent.", id);
            }

            return Ok(new 
            { 
                success = true, 
                processedCount = results.Count(x => x.Value),
                details = results 
            });
        }

        /// <summary>
        /// 單筆取消任務 (包裝 Batch API 邏輯)
        /// </summary>
        [HttpPost("cancel/{id}")]
        public IActionResult Cancel(int id)
        {
            return CancelBatch(new List<int> { id });
        }

        /// <summary>
        /// 接收任務並執行 (由 Master PUSH)
        /// </summary>
        [HttpPost("receive")]
        public IActionResult Receive([FromBody] HistoryTask task)
        {
            if (task == null || task.HistoryId <= 0)
                return BadRequest(new { error = "invalid task" });

            // ✅ 建立連動 Token：當服務停止 (ApplicationStopping) 或手動取消時觸發
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.ApplicationStopping);
            
            // ✅ 將此任務的中斷控制源註冊到 Tracker
            _tracker.Register(task.HistoryId, cts);

            // ✅ 在背景執行並回報
            _ = Task.Run(async () => 
            {
                try 
                {
                    await RunAndReportAsync(task, cts.Token);
                }
                finally 
                {
                    // ✅ 務必在最後註銷並 Dispose，避免資源洩漏
                    _tracker.Unregister(task.HistoryId);
                    cts.Dispose();
                }
            }, cts.Token);

            return Ok(new { ok = true });
        }

        private async Task RunAndReportAsync(HistoryTask task, CancellationToken ct)
        {
            var hid = task.HistoryId;
            var nodeName = (_cfg["Cluster:NodeName"] ?? Environment.MachineName).Trim();
            var master = (_cfg["Cluster:MasterBaseUrl"] ?? "").Trim().TrimEnd('/');

            if (string.IsNullOrWhiteSpace(master))
            {
                _log.LogError("[EXEC] MasterBaseUrl empty, cannot report back. hid={hid}", hid);
                return;
            }

            var client = _http.CreateClient();
            var reportUrl = $"{master}/api/jobs/report";

            try
            {
                _log.LogInformation("[EXEC] start hid={hid} action={act} from={from} to={to}",
                    hid, task.Action, task.FromFullPath, task.ToFullPath);

                // 1️⃣ 開始前先報 Running=1（不釋放 slot）
                await client.PostAsJsonAsync(reportUrl, new
                {
                    historyId = hid,
                    node = nodeName,
                    fileStatus = 1,
                    error = (string?)null,
                    assumeFreedSlot = false,
                    SetTape = false,
                    toType = task.ToType
                }, ct);

                // 2️⃣ 執行實際檔案動作（用 ct：只有服務關閉才會中止）
                var result = await _worker.RunOneAsync(task, ct);

                // 3️⃣ 回報最終結果（釋放 slot）
                await client.PostAsJsonAsync(reportUrl, new
                {
                    historyId = hid,
                    node = nodeName,
                    fileStatus = result.FileStatus,
                    error = result.Error,
                    assumeFreedSlot = true,
                    SetTape = result.SetTape,
                    toType = task.ToType,
                    fromType = task.FromType,
                    fromGroup = task.FromGroup
                }, CancellationToken.None);

                _log.LogInformation("[EXEC] done hid={hid} ok={ok} status={st}",
                    hid, result.Success, result.FileStatus);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("[EXEC] cancelled (app stopping) hid={hid}", hid);
            
            // 🚨 被取消時，仍須回報 Master 釋放 Slot
                try
                {
                    await client.PostAsJsonAsync(reportUrl, new
                    {
                        historyId = hid,
                        node = nodeName,
                        fileStatus = 999, // 使用者取消狀態碼
                        error = "Canceled by user or system",
                        assumeFreedSlot = true,
                        SetTape = false,
                        toType = task.ToType,
                        fromType = task.FromType,
                        fromGroup = task.FromGroup
                    }, CancellationToken.None);
                }
                catch { }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[EXEC] fatal hid={hid}", hid);

                // 盡量回報失敗（這裡用 ct 也可以；若你想「就算關機也要回報」再改成 CancellationToken.None）
                try
                {
                    await client.PostAsJsonAsync(reportUrl, new
                    {
                        historyId = hid,
                        node = nodeName,
                        fileStatus = 91,
                        error = ex.Message,
                        assumeFreedSlot = true,
                        SetTape = false,
                        toType = task.ToType,
                        fromType = task.FromType,
                        fromGroup = task.FromGroup
                    }, ct);
                }
                catch { }
            }
        }
    }
}