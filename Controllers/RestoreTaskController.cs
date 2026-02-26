using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileMoverWeb.Services;

using Dapper;
using Microsoft.Data.SqlClient;

namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("jobs")]
    public sealed class RestoreTaskController : ControllerBase
    {
        private readonly RestoreTaskPoller _poller;
        private readonly IConfiguration _cfg;

        public RestoreTaskController(RestoreTaskPoller poller, IConfiguration cfg)
        {
            _poller = poller;
            _cfg = cfg;
        }

        // ✅ 這個就是你缺的
        public sealed class BatchReq
        {
            public int[]? historyIds { get; set; }
        }

        private bool IsMaster()
            => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);

        private IActionResult MasterOnly()
            => StatusCode(403, new { ok = false, message = "Master only" });

        private static string BuildTape(string? a, string? b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a.Length > 0 && b.Length > 0) return $"{a}、{b}";
            if (a.Length > 0) return a;
            if (b.Length > 0) return b;
            return "-";
        }

        // GET /jobs/phase2-pending
        [HttpGet("phase2-pending")]
        public async Task<IActionResult> Phase2Pending(CancellationToken ct = default)
        {
            if (!IsMaster()) return MasterOnly();

            var rows = await _poller.ListPhase2PendingAsync(ct);

            var data = rows.Select(x => new
            {
                historyId = x.HistoryId,
                fileStatus = x.FileStatus,
                filetype = x.filetype,

                tape = BuildTape(x.TapeNo, x.TapeBakNo),

                programName = x.FileName ?? x.UserBit ?? "",
                fileName = x.UserBit ?? x.FileName ?? "",

                sourceStorage = x.FromName ?? "",
                destStorage = x.ToName ?? "",

                fromGroup = x.FromGroup,
                toGroup = x.ToGroup
            });

            return Ok(data);
        }

        // POST /jobs/phase2/start  { "historyIds":[1,2,3] }
        [HttpPost("phase2/start")]
        public async Task<IActionResult> Phase2Start([FromBody] BatchReq req, CancellationToken ct = default)
        {
            if (!IsMaster()) return MasterOnly();

            var ids = (req?.historyIds ?? Array.Empty<int>())
                .Where(x => x > 0)
                .Distinct()
                .ToArray();

            if (ids.Length == 0)
                return BadRequest(new { ok = false, message = "historyIds is empty" });

            var updated = await Phase2StartAsync(ids, ct);

            return Ok(new
            {
                ok = true,
                updated,
                message = $"已送出回遷：成功 {updated} 筆"
            });
        }

        private async Task<int> Phase2StartAsync(int[] ids, CancellationToken ct)
        {
            var connStr = _cfg.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);

            const string sql = @"
            UPDATE dbo.FileData_History
            SET file_status = CASE
                WHEN file_status = 14 THEN 24
                WHEN file_status = 17 THEN 27
                ELSE file_status
            END,
                assigned_node = NULL,
                note = NULL,
                update_time = GETDATE()
            WHERE id IN @Ids
            AND file_status IN (14,17);";

            return await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Ids = ids }, cancellationToken: ct));
        }
    }
}