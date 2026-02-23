using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using FileMoverWeb.Services;


namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("api/worker")]
    public class WorkerReportController : ControllerBase
    {
        private readonly IConfiguration _cfg;
        
        private readonly HistoryRepository _repo;
        private readonly IMoveRetryStore _retryStore;
        private readonly ILogger<WorkerReportController> _log;

        public WorkerReportController(
            IConfiguration cfg,
            HistoryRepository repo,
            IMoveRetryStore retryStore,
            ILogger<WorkerReportController> log)
        {
            _cfg = cfg;
            _repo = repo;
            _retryStore = retryStore;
            _log = log;
        }

        public sealed class ReportDto
        {
            public int HistoryId { get; set; }
           
            public string Action { get; set; } = "";
            public bool Success { get; set; }
            public int StatusCode { get; set; }
            public string? Error { get; set; }
        }

[HttpPost("report")]
public async Task<IActionResult> Report([FromBody] ReportDto dto, CancellationToken ct)
{
    // 保險：只有 Master 真更新 DB
    var role = _cfg["Cluster:Role"] ?? "Slave";
    if (!string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase))
        return Ok();

    var act = (dto.Action ?? "").Trim().ToLowerInvariant();

    // ✅ 收到 hid（你要的第一個 log）
    _log.LogInformation("[REPORT_IN] hid={hid} act={act} ok={ok} code={code} err={err}",
        dto.HistoryId, act, dto.Success, dto.StatusCode, dto.Error);

    try
    {
        if (dto.Success)
        {
            switch (act)
            {
                case "phase1done":
                    await _repo.MarkPhase1DoneAsync(dto.HistoryId, dto.StatusCode, ct);
                    break;

                case "move":
                    await _repo.CompleteMoveAsync(dto.HistoryId, ct);
                    break;

                case "delete":
                    await _repo.CompleteDeleteAsync(dto.HistoryId, ct);
                    break;

                case "phase2":
                    await _repo.CompleteAsync(dto.HistoryId, ct);
                    break;

                default:
                    await _repo.CompleteAsync(dto.HistoryId, ct);
                    break;
            }

            _retryStore.Clear(dto.HistoryId);

            // ✅ DB 更新完成（你要的第二個 log）
            _log.LogInformation("[REPORT_DONE] hid={hid} act={act} ok={ok} code={code}",
                dto.HistoryId, act, true, dto.StatusCode);

            return Ok(new { ok = true });
        }

        // FAIL：重試策略由 Master 決定要不要寫 800
        var n = _retryStore.IncrementFail(dto.HistoryId, dto.StatusCode, dto.Error);

        const int MaxMoveAttempts = 1;

        if (dto.StatusCode == 999)
        {
            await _repo.FailAsync(dto.HistoryId, 999, dto.Error ?? "Canceled by user", ct);
            _retryStore.Clear(dto.HistoryId);

            _log.LogWarning("[REPORT_DONE] hid={hid} act={act} ok={ok} code=999 (canceled)",
                dto.HistoryId, act, false);

            return Ok(new { ok = true });
        }

        if (n >= MaxMoveAttempts)
        {
            await _repo.FailAsync(dto.HistoryId, dto.StatusCode, dto.Error, ct);
            _retryStore.Clear(dto.HistoryId);

            _log.LogWarning("[REPORT_DONE] hid={hid} act={act} ok={ok} n={n} code={code} (give up) err={err}",
                dto.HistoryId, act, false, n, dto.StatusCode, dto.Error);
        }
        else
        {
            await _repo.FailAsync(dto.HistoryId, 800, dto.Error, ct);

            _log.LogWarning("[REPORT_DONE] hid={hid} act={act} ok={ok} n={n} code={code} (will retry) err={err}",
                dto.HistoryId, act, false, n, dto.StatusCode, dto.Error);
        }

        return Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        // ✅ DB 更新失敗（第三個 log）+ 回 500
        _log.LogError(ex, "[REPORT_ERR] hid={hid} act={act} ok={ok} code={code} err={err}",
            dto.HistoryId, act, dto.Success, dto.StatusCode, dto.Error);

        return StatusCode(500, new { ok = false, message = ex.Message });
    }
}
    }}
    