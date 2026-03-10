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
            
            var connStr = _cfg.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            var baseModel = new FileMoverWeb.Core.BaseModel(conn);
           
            // 1) 用前端帶上來的欄位算 newStatus（不查 DB / 不 join）
            int newStatus;
            if (string.Equals(req.fromType, "RESTORE", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(req.fromGroup, "4F", StringComparison.OrdinalIgnoreCase)) newStatus = 14;
                else if (string.Equals(req.fromGroup, "7F", StringComparison.OrdinalIgnoreCase)) newStatus = 17;
                else newStatus = 0;
            }
            else if (string.Equals(req.toType, "TAPE", StringComparison.OrdinalIgnoreCase))
            {
                // ✅ 重試「寫去 TAPE」的任務，重新回到一般待派送
                newStatus = 0;

                // 先從 History 找 file_id
                var fileId = await baseModel.FindWhereAsync<int?>(
                    table: "dbo.FileData_History",
                    whereSql: "id = @hid",
                    parameters: new { hid = id },
                    selectSql: "file_id",
                    ct: ct);

                if (fileId.HasValue && fileId.Value > 0)
                {
                    await baseModel.UpdateAsync(
                        table: "dbo.FileData",
                        pkName: "id",
                        id: fileId.Value,
                        data: new Dictionary<string, object?>
                        {
                            ["tape_id"] = -2
                        },
                        columnsWhitelist: new[] { "tape_id" },
                        ct: ct);

                    _log.LogInformation(
                        "[TAPE_RETRY] historyId={HistoryId} fileId={FileId} tape_id set to -2",
                        id, fileId.Value);
                }
                else
                {
                    _log.LogWarning(
                        "[TAPE_RETRY_SKIP] historyId={HistoryId} invalid fileId={FileId}",
                        id, fileId);
                }
            }
            else if (string.Equals(req.fromType, "TAPE", StringComparison.OrdinalIgnoreCase))
            {
                // ✅ NEW：fromType=TAPE retry → 13
                newStatus = 13;
            }
               else if (string.Equals(req.action, "delete", StringComparison.OrdinalIgnoreCase))
            {
                newStatus = -1;

                var h = await baseModel.FindAsync<RetryHistoryRow>(
                    table: "dbo.FileData_History",
                    pkName: "id",
                    id: id,
                    ct: ct);

                if (h == null)
                    return NotFound(new { ok = false, message = $"找不到 history id={id}" });

                if (h.file_id <= 0)
                    return BadRequest(new { ok = false, message = $"history.file_id 無效，hid={id}" });

                if (h.from_storage_id <= 0)
                    return BadRequest(new { ok = false, message = $"history.from_storage_id 無效，hid={id}" });

                var s = await baseModel.FindAsync<RetryStorageRow>(
                    table: "dbo.Storage",
                    pkName: "id",
                    id: h.from_storage_id,
                    ct: ct);

                if (s == null)
                    return BadRequest(new { ok = false, message = $"找不到 storage id={h.from_storage_id}" });

                var grp = (s.set_group ?? "").Trim().ToUpperInvariant();
                var ft = (h.file_type ?? "").Trim().ToUpperInvariant();

                string? masterTable = ft == "CM" ? "dbo.CMData"
                                    : ft == "PO" ? "dbo.FileData"
                                    : null;

                string? flagCol = grp == "4F" ? "is_file_4F"
                                : grp == "7F" ? "is_file_7F"
                                : null;

                if (masterTable == null)
                    return BadRequest(new { ok = false, message = $"未知 file_type={ft}" });

                if (flagCol == null)
                    return BadRequest(new { ok = false, message = $"未知 storage group={grp}" });

                var flagPatch = new Dictionary<string, object?>
                {
                    [flagCol] = "N"
                };

                var updatedFlag = await baseModel.UpdateAsync(
                    table: masterTable,
                    pkName: "id",
                    id: h.file_id,
                    data: flagPatch,
                    columnsWhitelist: new[] { "is_file_4F", "is_file_7F" },
                    ct: ct);

                _log.LogWarning(
                    "[RETRY_DELETE_FIX] hid={hid} fid={fid} table={table} col={col} value='N' affected={affected}",
                    id, h.file_id, masterTable, flagCol, updatedFlag);
            }
            else
            {
                newStatus = 0;
            }
            _log.LogInformation(
    "[RETRY] id={id} action='{action}' fromType='{fromType}' toType='{toType}' fromGroup='{fromGroup}' => newStatus={newStatus}",
    id, req.action, req.fromType, req.toType, req.fromGroup, newStatus);
           


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
             public string? toType { get; set; }
        }
        private sealed class RetryHistoryRow
{
    public int file_id { get; set; }
    public int from_storage_id { get; set; }
    public string? file_type { get; set; }
}

private sealed class RetryStorageRow
{
    public string? set_group { get; set; }
}
    }}