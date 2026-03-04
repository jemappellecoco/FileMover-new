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

        public ExecutorController(
            IConfiguration cfg,
            ILogger<ExecutorController> log,
            FileActionWorker worker,
            IHttpClientFactory http,
            IHostApplicationLifetime life)
        {
            _cfg = cfg;
            _log = log;
            _worker = worker;
            _http = http;
            _life = life;
        }

        // Master push 到 Slave：POST /api/executor/receive
        [HttpPost("receive")]
        public IActionResult Receive([FromBody] HistoryTask task)
        {
            if (task == null || task.HistoryId <= 0)
                return BadRequest(new { error = "invalid task" });

            // ✅ 不要用 RequestAborted：HTTP 回應結束就會 cancel
            // ✅ 改用 ApplicationStopping：只有服務要關機才取消
            var ct = _life.ApplicationStopping;

            // ✅ 先回 200，避免大檔案搬移卡住 Master 的 HTTP
            _ = Task.Run(() => RunAndReportAsync(task, ct), ct);

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
                    SetTape = false
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
                    SetTape = result.SetTape
                }, ct);

                _log.LogInformation("[EXEC] done hid={hid} ok={ok} status={st}",
                    hid, result.Success, result.FileStatus);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("[EXEC] cancelled (app stopping) hid={hid}", hid);
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
                        SetTape = false
                    }, ct);
                }
                catch { }
            }
        }
    }
}