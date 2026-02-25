using System;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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

        public ExecutorController(
            IConfiguration cfg,
            ILogger<ExecutorController> log,
            FileActionWorker worker,
            IHttpClientFactory http)
        {
            _cfg = cfg;
            _log = log;
            _worker = worker;
            _http = http;
        }

        // Master push 到 Slave：POST /api/executor/receive
       [HttpPost("receive")]
public IActionResult Receive([FromBody] HistoryTask task)
{
    if (task == null || task.HistoryId <= 0)
        return BadRequest(new { error = "invalid task" });

    // 用 RequestAborted 當作取消 token（用戶中斷連線/服務關閉時會觸發）
    var ct = HttpContext.RequestAborted;

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
        _log.LogInformation("[EXEC] start hid={hid} action={act}", hid, task.Action);

        // 1️⃣ 開始前先報 Running=1（不釋放 slot）
        await client.PostAsJsonAsync(reportUrl, new
        {
            historyId = hid,
            node = nodeName,
            fileStatus = 1,
            error = (string?)null,
            assumeFreedSlot = false
        }, ct);

        // 2️⃣ 執行實際檔案動作（用 ct）
        var result = await _worker.RunOneAsync(task, ct);

        // 3️⃣ 回報最終結果（釋放 slot）
        await client.PostAsJsonAsync(reportUrl, new
        {
            historyId = hid,
            node = nodeName,
            fileStatus = result.FileStatus,
            error = result.Error,
            assumeFreedSlot = true
        }, ct);

        _log.LogInformation("[EXEC] done hid={hid} ok={ok} status={st}",
            hid, result.Success, result.FileStatus);
    }
    catch (OperationCanceledException)
    {
        _log.LogWarning("[EXEC] cancelled hid={hid}", hid);
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "[EXEC] fatal hid={hid}", hid);

        try
        {
            await client.PostAsJsonAsync(reportUrl, new
            {
                historyId = hid,
                node = nodeName,
                fileStatus = 91,
                error = ex.Message,
                assumeFreedSlot = true
            }, ct);
        }
        catch { }
    }
}

        private bool IsMaster()
            => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);
    }
}