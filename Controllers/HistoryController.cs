using Microsoft.AspNetCore.Mvc;
using FileMoverWeb.Services;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using Dapper;
namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("history")]
    public sealed class HistoryController : ControllerBase
    {
        private readonly HistoryPoller _poller;
        private readonly IConfiguration _cfg;
         private readonly ILogger<HistoryController> _log;
        public HistoryController(HistoryPoller poller,IConfiguration cfg,ILogger<HistoryController> log)
        {
            _poller = poller;
            _cfg = cfg;
            _log = log;
        }
        public sealed class RemoveReq
    {
        public int HistoryId { get; set; }
        public string? Note { get; set; }
    }

        // GET /history
        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var rows = await _poller.GetHistoryList(ct);

            return Ok(new
            {
                total = rows.Count,
                page = 1,
                take = rows.Count,
                totalPages = 1,
                rows = rows
            });
        }
        [HttpGet("recent")]
        public async Task<IActionResult> GetRecent(CancellationToken ct)
        {
            // 呼叫上方寫好的 Service Method
            // 假設您的 poller 實體名稱為 _poller
            var list = await _poller.GetHistoryRecentList(ct);
            
            // 直接回傳 Array，符合前端 normalizeTask(rawData.map(...)) 的預期
            return Ok(list);
        }
        [HttpPost("{id:int}/remove")]
        public async Task<IActionResult> RemoveById([FromRoute] int id, CancellationToken ct)
        {
            if (id <= 0) return BadRequest(new { ok = false, message = "id invalid" });

            var connStr = _cfg.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);

            var baseModel = new FileMoverWeb.Core.BaseModel(conn);

            var patch = new Dictionary<string, object?>
            {
                ["file_status"] = 111,
                ["assigned_node"] = null,
                ["note"] = "removed by UI",
                ["update_time"] = DateTime.Now
            };

            var updated = await baseModel.UpdateAsync(
                table: "dbo.FileData_History",
                pkName: "id",
                id: id,
                data: patch,
                columnsWhitelist: new[] { "file_status", "assigned_node", "note", "update_time" },
                ct: ct);

            if (updated == 0)
                {
                    // 1. 寫 Log 紀錄這次「無效的操作」
                    _log.LogWarning("[REMOVE_FAIL] 使用者嘗試移除 ID={id}，但資料庫中找不到或不符合條件", id);

                    // 2. 回傳 404，讓前端知道這筆資料已經「過期」
                    return NotFound(new { ok = false, message = "找不到該紀錄，可能已被移除或存檔。" });
                }
            return Ok(new { ok = true, historyId = id, message = $"已移除 HistoryId={id}" });
        }

        
         // ✅ NEW: POST /history/{id}/retry
      [HttpPost("{id:int}/retry")]
        public async Task<IActionResult> Retry([FromRoute] int id, [FromBody] RetryReq req, CancellationToken ct)
        {
            if (id <= 0) return BadRequest(new { ok = false, message = "id invalid" });
            if (req == null) return BadRequest(new { ok = false, message = "body required" });

            // 1) 用前端帶上來的欄位算 newStatus（不查 DB / 不 join）
            int newStatus;
            if (string.Equals(req.fromType, "RESTORE", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(req.fromGroup, "4F", StringComparison.OrdinalIgnoreCase)) newStatus = 14;
                else if (string.Equals(req.fromGroup, "7F", StringComparison.OrdinalIgnoreCase)) newStatus = 17;
                else newStatus = 0;
            }
            else if (string.Equals(req.fromType, "TAPE", StringComparison.OrdinalIgnoreCase))
            {
                // ✅ NEW：fromType=TAPE retry → 13
                newStatus = 13;
            }
            else if (string.Equals(req.action, "delete", StringComparison.OrdinalIgnoreCase))
            {
                newStatus = -1;
            }
            else
            {
                newStatus = 0;
            }
            _log.LogInformation("[RETRY] id={id} action='{action}' fromType='{fromType}' fromGroup='{fromGroup}' => newStatus={newStatus}",
    id, req.action, req.fromType, req.fromGroup, newStatus);
            // 2) 直接 patch 回 DB（但用 extraWhereSql 做 status gate）
            var connStr = _cfg.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            var baseModel = new FileMoverWeb.Core.BaseModel(conn);

            var patch = new Dictionary<string, object?>
            {
                ["file_status"] = newStatus,
                ["assigned_node"] = null,
                ["update_time"] = DateTime.Now,
                ["note"] = null,
            };

            var updated = await baseModel.UpdateAsync(
                table: "dbo.FileData_History",
                pkName: "id",
                id: id,
                data: patch,
                columnsWhitelist: new[] { "file_status", "assigned_node", "update_time","note" },
                // ✅ 關鍵：只允許錯誤狀態才能被 retry
                extraWhereSql: "file_status IN (904,91,92,999,901,902,903,911,912,913,914,915,921,922,923)",
                ct: ct);

            if (updated == 0)
                return BadRequest(new { ok = false, message = "此筆紀錄目前不能（可能狀態已變或已被處理）。" });

            return Ok(new { ok = true, historyId = id, newStatus, message = "已將此筆任務重新排入佇列。" });
        }   

        public sealed class RetryReq
        {
            public string? action { get; set; }
            public string? fromType { get; set; }
            public string? fromGroup { get; set; }
        }
    }}