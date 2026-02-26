// JobReportController.cs
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Services;
namespace FileMoverWeb.Controllers;

[ApiController]
[Route("api/jobs")]
public sealed class JobReportController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<JobReportController> _log;
    private readonly NodeRuntimeRegistry _registry;

    public JobReportController(IConfiguration cfg, ILogger<JobReportController> log, NodeRuntimeRegistry registry)
    {
        _cfg = cfg;
        _log = log;
        _registry = registry;
    }

    // POST /api/jobs/report
    [HttpPost("report")]
    public async Task<IActionResult> Report([FromBody] JobReportDto dto, CancellationToken ct)
    {
        if (!IsMaster()) return Forbid();

        if (dto.HistoryId <= 0) return BadRequest(new { error = "historyId is required" });
        if (string.IsNullOrWhiteSpace(dto.Node)) return BadRequest(new { error = "node is required" });

        // ✅ 寫回 DB（你最在意的：狀態要準）
        await UpdateHistoryStatusAsync(dto, ct);

        _log.LogInformation("[JOB_REPORT] hid={hid} node={node} status={st} err={err}",
            dto.HistoryId, dto.Node, dto.FileStatus, dto.Error);
        // ✅ 開始跑：-1
        if (!dto.AssumeFreedSlot && dto.FileStatus == 1)
        {
            var ok = _registry.TryConsume(dto.Node.Trim(), 1);
            _log.LogInformation("[SLOT] consume node={node} ok={ok}", dto.Node, ok);
        }
        // ✅ 可選：回報後，若 node 宣告自己完成一個任務，你可以在 registry 先 +1 空位
        //（你之後 master push 派工時很好用）
        if (dto.AssumeFreedSlot)
        {
            _registry.AddFree(dto.Node.Trim(), 1);
        }

        return Ok(new { ok = true });
    }
    
    private async Task UpdateHistoryStatusAsync(JobReportDto dto, CancellationToken ct)
    {
        var connStr = _cfg.GetConnectionString("DefaultConnection")!;
        using var conn = new SqlConnection(connStr);

        const string sql = @"
        DECLARE @now DATETIME = GETDATE();

        UPDATE dbo.FileData_History
        SET file_status   = @fileStatus,
            assigned_node = @node,
            note          = LEFT(COALESCE(@error, ''), 4000),
            update_time   = @now
        WHERE id = @historyId
        AND assigned_node = @node;
        ";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            historyId = dto.HistoryId,
            node = dto.Node,
            fileStatus = dto.FileStatus,
            error = dto.Error
        }, cancellationToken: ct));
    }

    private bool IsMaster()
        => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class JobReportDto
    {
        public int HistoryId { get; set; }
        public string? Node { get; set; }

        // ✅ 就用你 DB 的 file_status（11/12/91x/92x/24/27/999...）
        public int FileStatus { get; set; }

        // 失敗原因/訊息（可空）
        public string? Error { get; set; }

        // ✅ 可選：如果 node 回報代表「我做完一個了」，master 這裡可以先把 freeSlots +1
        public bool AssumeFreedSlot { get; set; } = true;
    }