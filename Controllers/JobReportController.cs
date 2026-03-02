using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Services;

namespace FileMoverWeb.Controllers
{
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

        // ✅ 只允許更新這些欄位（白名單）
        private static readonly string[] HistoryWhitelist =
        {
            "file_status",
            "note",
            "assigned_node",
            "update_time"
        };

        // POST /api/jobs/report
        [HttpPost("report")]
        public async Task<IActionResult> Report([FromBody] JobReportDto dto, CancellationToken ct)
        {
            if (!IsMaster()) return Forbid();

            if (dto is null || dto.HistoryId <= 0)
                return BadRequest(new { ok = false, error = "historyId is required" });

            if (string.IsNullOrWhiteSpace(dto.Node))
                return BadRequest(new { ok = false, error = "node is required" });

            var updated = await UpdateHistoryStatusAsync(dto, ct);

            _log.LogInformation("[JOB_REPORT] hid={hid} node={node} fileStatus={st} err={err} updated={updated}",
                dto.HistoryId, dto.Node, dto.FileStatus, dto.Error, updated);

            // ✅ 沒更新到：通常是 assigned_node != node（或 id 不存在）
            // 仍回 200 但帶訊息，方便你前端/worker debug
            if (updated == 0)
                return Ok(new { ok = true, updated = 0, message = "no rows updated (node mismatch or no patch fields)" });

            // ---- SLOT LOGIC（只在有給 fileStatus 時才做，避免誤觸）----

            // Consume：當 worker 回報「開始跑」(file_status==1) 且 assumeFreedSlot==false
            if (dto.FileStatus == 1 && dto.AssumeFreedSlot == false)
            {
                var ok = _registry.TryConsume(dto.Node.Trim(), 1);
                _log.LogInformation("[SLOT] consume node={node} ok={ok}", dto.Node, ok);
            }

            // Release：只有明確傳 true 才釋放（null=不動）
            if (dto.AssumeFreedSlot == true)
            {
                _registry.AddFree(dto.Node.Trim(), 1);
                _log.LogInformation("[SLOT] release(+1) node={node}", dto.Node);
            }

            return Ok(new { ok = true, updated });
        }

        private async Task<int> UpdateHistoryStatusAsync(JobReportDto dto, CancellationToken ct)
        {
            var connStr = _cfg.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);

            var baseModel = new FileMoverWeb.Core.BaseModel(conn);

            // ✅ Patch：只更新你有給的欄位
            var patch = new Dictionary<string, object?>();

            if (dto.FileStatus.HasValue)
                patch["file_status"] = dto.FileStatus.Value;

            if (dto.Error != null)
            {
                // 避免超長（你的 DB note 可能 nvarchar(4000)）
                var note = dto.Error.Length > 4000 ? dto.Error.Substring(0, 4000) : dto.Error;
                patch["note"] = note;
            }

            // 有 patch 才更新時間（這裡用 GETDATE 的話要改 BaseModel 支援 raw sql；
            // 目前先用 app time，已足夠）
            if (patch.Count > 0)
                patch["update_time"] = DateTime.Now;

            // ✅ 沒有任何欄位要改，就不做事
            if (patch.Count == 0)
                return 0;

            // ✅ 保留原本語意：必須 assigned_node = node 才能改
            return await baseModel.PatchAsync(
                table: "dbo.FileData_History",
                pkName: "id",
                id: dto.HistoryId,
                data: patch,
                columnsWhitelist: HistoryWhitelist,
                extraWhereSql: "assigned_node = @node",
                extraWhereParams: new { node = dto.Node!.Trim() },
                ct: ct);
        }

        private bool IsMaster()
            => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);

        public sealed class JobReportDto
        {
            public int HistoryId { get; set; }
            public string? Node { get; set; }

            // ✅ nullable 才能「沒給不更新」
            public int? FileStatus { get; set; }

            // 有給才更新 note
            public string? Error { get; set; }

            // ✅ nullable；null = 不動；true = 釋放；false = consume（搭配 FileStatus==1）
            public bool? AssumeFreedSlot { get; set; }
        }
    }
}