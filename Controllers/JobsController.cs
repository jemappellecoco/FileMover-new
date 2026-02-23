// Controllers/JobsController.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FileMoverWeb.Models;
using FileMoverWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Controllers;

[ApiController]
[Route("jobs")]
[ApiExplorerSettings(IgnoreApi = true)]
public class JobsController : ControllerBase
{
    private readonly ILogger<JobsController> _log;
    private readonly IJobProgress _progress;
    private readonly ITaskQueue _taskQueue;
    private readonly MoveWorker _worker;

    // ✅ Slave 不註冊 DB/Repo 也能啟動，所以 repo 必須 optional
    private readonly HistoryRepository? _repo;

    private readonly IMoveRetryStore _retryStore;
    private readonly IConfiguration _cfg;
    private readonly ICancelStore _cancelStore;

    public JobsController(
        ILogger<JobsController> log,
        IJobProgress progress,
        ITaskQueue taskQueue,
        MoveWorker worker,
        HistoryRepository? repo = null,            // ✅ 關鍵：Slave 沒註冊也不會炸
        IMoveRetryStore retryStore = null!,
        IConfiguration cfg = null!,
        ICancelStore cancelStore = null!)
    {
        _log = log;
        _progress = progress;
        _taskQueue = taskQueue;
        _worker = worker;
        _repo = repo;
        _retryStore = retryStore;
        _cfg = cfg;
        _cancelStore = cancelStore;
    }

    // --------------------------
    // Helpers
    // --------------------------
    private bool IsMaster()
    {
        var role = (_cfg["Cluster:Role"] ?? "Slave").Trim();
        return string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase);
    }

    private IActionResult MasterOnly()
        => StatusCode(403, new { message = "Master only endpoint" });

    private HistoryRepository RepoOrThrow()
    {
        // 只有 Master 才應該碰 DB
        if (!IsMaster()) throw new InvalidOperationException("Master only endpoint");
        if (_repo == null) throw new InvalidOperationException("HistoryRepository not available");
        return _repo;
    }

    // 0) 列出 status=0/-1/1 的待處理任務：GET /jobs/pending
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending([FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (!IsMaster()) return MasterOnly();

        if (take <= 0) take = 200;
        // if (take > 500) take = 500;

        var repo = RepoOrThrow();

        var rows = await repo.ListPendingAsync(take, ct);

        var data = rows.Select(x =>
        {
            // ✅ Action：完全吃 DB（copy/move…），不推導
            var actionRaw = (x.Action ?? "").Trim();
            var actionOut = string.IsNullOrWhiteSpace(actionRaw) ? "-" : actionRaw;

            // ✅ TaskKind：Phase 1/2（用 status 判斷）
            var phase = (x.FileStatus == 24 || x.FileStatus == 27) ? "phase2" : "phase1";

            // ✅ retry store
            int retryCount = 0;
            int? retryCode = null;
            string? retryMessage = null;

            if (_retryStore.TryGet(x.HistoryId, out var info))
            {
                retryCount = info.FailCount;
                retryCode = info.LastStatusCode;
                retryMessage = info.LastError;
            }

            return new
            {
                x.HistoryId,
                x.FileId,
                x.FromStorageId,
                x.ToStorageId,

                ProgramName = x.FileName ?? x.UserBit ?? string.Empty,
                FileName = x.UserBit ?? x.FileName,

                SourceStorage = x.FromName,
                DestStorage = x.ToName,
                DestType = x.ToType,

                Action = actionOut,
                TaskKind = phase,

                RequestedBy = x.RequestedBy ?? "-",
                SourceGroup = x.FromGroup,

                Status = x.FileStatus,
                Priority = x.Priority,

                Tag = (x.FileStatus == 24 || x.FileStatus == 27) ? "回遷任務" : null,

                RetryCount = retryCount,
                RetryCode = retryCode,
                RetryMessage = retryMessage,

                AssignedNode = x.AssignedNode
            };
        });

        return Ok(data);
    }

    // ===== 批次取消：POST /jobs/cancel-batch =====
    public sealed class CancelBatchRequest
    {
        public List<int> HistoryIds { get; init; } = new();
    }

    [HttpPost("cancel-batch")]
    public async Task<IActionResult> CancelBatch(
        [FromBody] CancelBatchRequest req,
        CancellationToken ct = default)
    {
        if (!IsMaster()) return MasterOnly();

        var repo = RepoOrThrow();

        if (req?.HistoryIds == null || req.HistoryIds.Count == 0)
            return BadRequest(new { message = "請至少選擇一筆任務" });

        var ids = req.HistoryIds
            .Where(x => x > 0)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
            return BadRequest(new { message = "HistoryIds 不可為空" });

        var ok = 0;

        foreach (var hid in ids)
        {
            // 1) 讓 worker 真的停（如果正在跑）
            _cancelStore.Cancel(hid);

            // 2) DB 立刻標 999，讓 pending/restore 立刻消失
            await repo.FailAsync(hid, 999, "User canceled", ct);

            // 3) 立刻清 progress
            _progress.CompleteJob(hid.ToString());

            ok++;
        }

        return Ok(new
        {
            count = ok,
            status = 999,
            message = $"Canceled {ok} jobs"
        });
    }

    // 手動取消：POST /jobs/{historyId}/cancel
    [HttpPost("{historyId:int}/cancel")]
    public async Task<IActionResult> Cancel(int historyId, CancellationToken ct)
    {
        if (!IsMaster()) return MasterOnly();

        var repo = RepoOrThrow();

        _cancelStore.Cancel(historyId);
        await repo.FailAsync(historyId, 999, "User canceled", ct);
        _progress.CompleteJob(historyId.ToString());

        return Ok(new { historyId, status = 999, message = "Canceled by user" });
    }

    // Phase2：列出等待回遷
    [HttpGet("phase2-pending")]
    public async Task<IActionResult> GetPhase2Pending(
        [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        if (!IsMaster()) return MasterOnly();

        var repo = RepoOrThrow();

        if (take <= 0) take = 200;
        // if (take > 500) take = 500;

        var rows = await repo.ListPhase2PendingAsync(take, ct);

        var restore7F = await repo.GetRestoreNameAsync("7F", ct);
        var restore4F = await repo.GetRestoreNameAsync("4F", ct);

        var data = rows.Select(x =>
        {
            var sourceName = (x.ToGroup ?? "").ToUpperInvariant() switch
            {
                "7F" => restore7F ?? x.FromName ?? "",
                "4F" => restore4F ?? x.FromName ?? "",
                _ => x.FromName ?? ""
            };

            return new
            {
                x.HistoryId,

                Tape = !string.IsNullOrWhiteSpace(x.TapeNo)
                       ? $"{x.TapeNo}、{x.TapeBakNo}"
                       : "-",

                ProgramName = x.FileName ?? x.UserBit ?? "",
                FileName = x.UserBit ?? x.FileName ?? "",
                SourceStorage = sourceName,
                DestStorage = x.ToName ?? "",
                x.FromGroup,
                x.ToGroup,
                x.FileStatus
            };
        });

        return Ok(data);
    }

    // Phase2：啟動回遷（把選到的變成 status=0）
    public sealed class Phase2StartRequest
    {
        public List<int> HistoryIds { get; init; } = new();
    }

    [HttpPost("phase2/start")]
    public async Task<IActionResult> StartPhase2(
        [FromBody] Phase2StartRequest req,
        CancellationToken ct = default)
    {
        if (!IsMaster()) return MasterOnly();

        var repo = RepoOrThrow();

        if (req.HistoryIds == null || req.HistoryIds.Count == 0)
            return BadRequest(new { message = "請至少勾選一筆回遷任務" });

        await repo.MarkPhase2ToReadyAsync(req.HistoryIds.ToArray(), ct);

        return Ok(new { count = req.HistoryIds.Count, message = "已送出回遷，稍後由背景服務處理" });
    }

    // 支援兩種命名：srcPath/dstPath 與 sourcePath/destObjectPath
    public sealed class CreateJobRequest
    {
        [JsonPropertyName("srcPath")] public string? SrcPath { get; init; }
        [JsonPropertyName("dstPath")] public string? DstPath { get; init; }

        [JsonPropertyName("sourcePath")] public string? SourcePath { get; init; }
        [JsonPropertyName("destObjectPath")] public string? DestObjectPath { get; init; }

        [JsonIgnore]
        public string Src => SrcPath ?? SourcePath
            ?? throw new ArgumentNullException(nameof(SrcPath), "請提供 srcPath 或 sourcePath");

        [JsonIgnore]
        public string Dst => DstPath ?? DestObjectPath
            ?? throw new ArgumentNullException(nameof(DstPath), "請提供 dstPath 或 destObjectPath");
    }

    // 建立單檔搬運（相容舊 /api/jobs）
    [HttpPost]
    public IActionResult Create([FromBody] CreateJobRequest req, CancellationToken ct)
    {
        if (!System.IO.File.Exists(req.Src))
            return BadRequest(new { message = "Source not found." });

        var jobId = Guid.NewGuid().ToString("N");

        var batch = new MoveBatchRequest
        {
            JobId = jobId,
            Items = new List<MoveItem>
            {
                new MoveItem
                {
                    SourcePath = req.Src,
                    DestPath   = req.Dst,
                    DestId     = $"TO-{jobId}"
                }
            }
        };

        _ = Task.Run(() => _worker.RunAsync(batch, ct)); // 背景執行

        return Ok(new { jobId, status = "Pending" });
    }

    [HttpGet("{jobId}")]
    public IActionResult Get(string jobId)
    {
        var list = _progress.Snapshot(jobId);
        if (list == null || list.Count == 0)
            return NotFound();

        long total = list.Sum(x => x.TotalBytes);
        long copied = list.Sum(x => x.CopiedBytes);
        int percent = total > 0 ? (int)Math.Clamp(copied * 100L / total, 0, 100) : 0;

        string status = "Pending";
        if (total > 0 && copied > 0 && copied < total) status = "Running";
        if (total > 0 && copied >= total) status = "Completed";

        return Ok(new
        {
            jobId,
            bytesCopied = copied,
            totalBytes = total,
            percent,
            status
        });
    }

    [HttpGet("{jobId}/events")]
    public async Task GetEvents(string jobId, CancellationToken ct)
    {
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.ContentType = "text/event-stream";

        while (!ct.IsCancellationRequested)
        {
            var list = _progress.Snapshot(jobId);
            if (list == null || list.Count == 0)
            {
                await Task.Delay(300, ct);
                continue;
            }

            long total = list.Sum(x => x.TotalBytes);
            long copied = list.Sum(x => x.CopiedBytes);
            int percent = total > 0 ? (int)Math.Clamp(copied * 100L / total, 0, 100) : 0;

            string status = "progress";
            if (total > 0 && copied >= total) status = "completed";

            await Response.WriteAsync($"event: {status}\n", ct);
            await Response.WriteAsync(
                $"data: {{\"jobId\":\"{jobId}\",\"bytes\":{copied},\"total\":{total},\"percent\":{percent}}}\n\n",
                ct);
            await Response.Body.FlushAsync(ct);

            if (status == "completed") break;
            await Task.Delay(300, ct);
        }
    }

    public sealed class PriorityRequest
    {
        public int HistoryId { get; set; }
        public int Delta { get; set; }   // +1 or -1
    }

    [HttpPost("priority")]
    public async Task<IActionResult> ChangePriority(
        [FromBody] PriorityRequest req,
        CancellationToken ct = default)
    {
        if (!IsMaster()) return MasterOnly();

        var repo = RepoOrThrow();

        if (req == null || req.HistoryId <= 0)
            return BadRequest(new { message = "HistoryId 不可為空" });

        var newPri = await repo.AdjustPriorityAsync(req.HistoryId, req.Delta, ct);

        if (!newPri.HasValue)
            return NotFound(new { message = $"找不到 HistoryId={req.HistoryId}" });

        return Ok(new
        {
            historyId = req.HistoryId,
            priority = newPri.Value
        });
    }

    static long CalcProgressTotal(HistoryTask task)
    {
        var act = (task.Action ?? "").Trim().ToLowerInvariant();

        // ✅ 你原本邏輯：move / delete 走兩段（size * 2）
        if (act is "move" or "delete")
        {
            var size = task.FileSize > 0 ? task.FileSize : 1;
            return size * 2;
        }

        // copy/phase2 或其他維持原始 size
        return task.FileSize > 0 ? task.FileSize : 1;
    }

    // ✅ Slave 接收推送：POST /jobs/receive-task
    [HttpPost("receive-task")]
    public async Task<IActionResult> Receive([FromBody] HistoryTask task)
    {
        if (task == null) return BadRequest("Task data is null");

        _log.LogInformation("➡️ Slave 節點收到任務推送: {id}, Action={action}", task.HistoryId, task.Action);

        long totalForUi = CalcProgressTotal(task);

        var totals = new Dictionary<string, long>
        {
            { $"TO-{task.HistoryId}", totalForUi }
        };

        // 初始化 SSE 進度緩存
        _progress.InitTotals(task.HistoryId.ToString(), totals);

        // 放入隊列由 SlotWorker 執行
        await _taskQueue.EnqueueAsync(task);

        return Ok();
    }
}
