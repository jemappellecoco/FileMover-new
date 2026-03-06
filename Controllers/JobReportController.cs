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
using System.Linq;
using System.Net.Http.Json;
namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("api/jobs")]
    public sealed class JobReportController : ControllerBase
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<JobReportController> _log;
        private readonly NodeRuntimeRegistry _registry;
        private readonly DeleteVerifier _deleteVerifier;
        private readonly CopyVerifier _copyVerifier;
    private readonly IHttpClientFactory _http;
        public JobReportController
        (IConfiguration cfg, 
        ILogger<JobReportController> log, 
        NodeRuntimeRegistry registry, 
        DeleteVerifier deleteVerifier,
        CopyVerifier copyVerifier,
         IHttpClientFactory http)
        {
            _cfg = cfg;
            _log = log;
            _registry = registry;
             _deleteVerifier = deleteVerifier;
              _copyVerifier = copyVerifier;
              _http = http;
        }

        // ✅ 只允許更新這些欄位（白名單）
        private static readonly string[] HistoryWhitelist =
        {
            "file_status",
            "note",
            "assigned_node",
            "update_time"
        };
        private static readonly string[] FileDataWhitelist =
            {
                "tape_id"
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

        if (!dto.FileStatus.HasValue)
            return BadRequest(new { ok = false, error = "fileStatus is required" });
        // ✅ 特例：CopyDone(11) → Master 先驗證（FileData_Storage ensure），再「只寫一次」最終狀態
        if (dto.FileStatus.Value == 11)
        {
            var (okVerify, msg) = await _copyVerifier.VerifyCopyAsync(dto.HistoryId, ct);

            if (!okVerify)
            {
                dto.FileStatus = 904;   // 你自訂：copy verify failed
                dto.Error = msg ?? "copy verify failed";
            }
        }    
            // --- Delete 驗證 / 失敗修復邏輯（修正版） ---
        if (dto.FileStatus.Value == 12)
        {
            // ✅ DeleteDone(12) → 驗證刪除是否真的成功
            var (okVerify, msg) = await _deleteVerifier.VerifyDeleteAsync(dto.HistoryId, ct);

            if (!okVerify)
            {
                // ✅ 只有 verify 失敗才 restore
                await _deleteVerifier.FileOnFailAsync(dto.HistoryId, ct);
                dto.FileStatus = 904;
                dto.Error = msg ?? "delete verify failed";
            }
        }
        else if ( dto.FileStatus.Value == 91 ||dto.FileStatus.Value >= 900)
        {
            // ✅ 只有「取消 / 錯誤類狀態」才 restore
            // 999: 使用者取消
            // 900+: 你自訂的錯誤碼（901/902/903/904...）
            await _deleteVerifier.FileOnFailAsync(dto.HistoryId, ct);
        }

        // ✅ 寫回 DB（只更新你有給的欄位；且必須 assigned_node == node）
        var updated = await UpdateHistoryStatusAsync(dto, ct);

        _log.LogInformation("[JOB_REPORT] hid={hid} node={node} status={st} err={err} updated={updated}",
            dto.HistoryId, dto.Node, dto.FileStatus, dto.Error, updated);

        if (updated == 0)
            return Ok(new { ok = true, updated = 0, message = "no rows updated " });

        // ---- SLOT LOGIC ----
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
        // 你另外加一個白名單（如果你 BaseModel 有做 whitelist）
   
        private async Task<int> UpdateHistoryStatusAsync(JobReportDto dto, CancellationToken ct)
        {
            var connStr = _cfg.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);
            

            // 1 開啟 (Transaction)
            await using var trans = await conn.BeginTransactionAsync(ct);
            var baseModel = new FileMoverWeb.Core.BaseModel(conn,(SqlTransaction)trans);
           try
    {
           
            // ✅ Patch：只更新你有給的欄位
            var patch = new Dictionary<string, object?>();

            if (dto.FileStatus.HasValue)
                patch["file_status"] = dto.FileStatus.Value;

            if (dto.Error != null)
            {

                var note =  dto.Error;
                patch["note"] = note;
            }

            // 有 patch 才更新時間（這裡用 GETDATE 的話要改 BaseModel 支援 raw sql；
            // 目前先用 app time，已足夠）
            if (patch.Count > 0)
                patch["update_time"] = DateTime.Now;

            // ✅ 沒有任何欄位要改，就不做事
            if (patch.Count == 0)
                return 0;
             // ✅ 先更新 History
            var node = dto.Node!.Trim();
                _log.LogWarning(
                    "[TAPE_CHECK] hid={hid} status={st} setTape={setTape} node='{node}'",
                    dto.HistoryId, dto.FileStatus, dto.SetTape, node);
        // 2️⃣ 若成功且需要清 tape_id
            if (dto.SetTape == true && dto.FileStatus == Status.CopyDone /* 11 */)
            {
                
                // 用 history 找 file_id
                var fileId = await baseModel.FindWhereAsync<int?>(
                    table: "dbo.FileData_History",
                    whereSql: "id = @hid",
                    parameters: new { hid = dto.HistoryId },
                    selectSql: "file_id",
                    ct: ct);
                _log.LogWarning("[TAPE_CHECK] hid={hid} fileId={fileId}", dto.HistoryId, fileId);
                if (fileId.HasValue && fileId.Value > 0)
                {
                    await baseModel.UpdateAsync(
                        table: "dbo.FileData",
                        pkName: "id",          // ✅ FileData 主鍵
                        id: fileId.Value,     // ✅ 就是 History.file_id
                        data: new Dictionary<string, object?>
                        {
                            ["tape_id"] = -1
                        },
                        columnsWhitelist: FileDataWhitelist,
                        ct: ct);

                    _log.LogInformation(
                        "[TAPE_UPDATE] hid={hid} fileId={fid} tape_id=-1",
                        dto.HistoryId,
                        fileId.Value);
                }
            }
            _log.LogWarning(
                "[REPORT_APPLY] hid={hid} node={node} status={st}",
                dto.HistoryId, dto.Node, dto.FileStatus);
            var updated = await baseModel.UpdateAsync(
                table: "dbo.FileData_History",
                pkName: "id",
                id: dto.HistoryId,
                data: patch,
                columnsWhitelist: HistoryWhitelist,
                ct: ct);
            await trans.CommitAsync(ct);
            _log.LogWarning(
    "[REPORT_RESULT] hid={hid} updated={updated} status={st}",
    dto.HistoryId, updated, dto.FileStatus);
            return updated;
        }catch (Exception ex)
    {
        // 發生任何意外，全部還原
        await trans.RollbackAsync(ct);
        _log.LogError(ex, "[REPORT_ERR] hid={hid} transaction fallback!", dto.HistoryId);
        throw;
    }
}
        
            // ✅ 保留原本語意：必須 assigned_node = node 才能改
            // return await baseModel.UpdateAsync(
            //     table: "dbo.FileData_History",
            //     pkName: "id",
            //     id: dto.HistoryId,
            //     data: patch,
            //     columnsWhitelist: HistoryWhitelist,
            //     extraWhereSql: "assigned_node = @node",
            //     extraWhereParams: new { node = dto.Node!.Trim() },
            //     ct: ct);
        

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
            public bool? SetTape { get; set; }
        }

        // POST /api/jobs/cancel-batch
    [HttpPost("cancel-batch")]
    public async Task<IActionResult> CancelBatch([FromBody] List<int> ids, CancellationToken ct)
    {       // === 第一階段：驗證與初始化 ===
        if (!IsMaster()) return Forbid();
        if (ids == null || ids.Count == 0) return BadRequest("No IDs provided");

        ids = ids.Distinct().Where(x => x > 0).ToList();
        if (ids.Count == 0) return BadRequest("No valid IDs");

        var connStr = _cfg.GetConnectionString("DefaultConnection")!;
        await using var conn = new SqlConnection(connStr);
        var baseModel = new FileMoverWeb.Core.BaseModel(conn);
        // === 第二階段：狀態查詢 ===
        var rows = (await baseModel.QueryAsync<CancelRow>(
            "SELECT id AS HistoryId, file_status AS FileStatus, assigned_node AS AssignedNode FROM dbo.FileData_History WHERE id IN @ids",
            new { ids }, 
            ct)).ToList();

        // 找不到的 id
        var foundSet = rows.Select(r => r.HistoryId).ToHashSet();
        var notFound = ids.Where(id => !foundSet.Contains(id)).ToList();

    // === 第三階段：處理排隊中 (Pending) 任務 - 批次 SQL ===
        var pendingIds = rows.Where(r => r.FileStatus is 0 or -1 or 24 or 27).Select(r => r.HistoryId).ToList();
        int canceledInQueue = 0;

        if (pendingIds.Any())
        {
            var historyWhitelist = new[] { "file_status", "update_time", "note" };
            var updateParams = new Dictionary<string, object?>
            {
                ["file_status"] = 999,
                ["update_time"] = DateTime.Now,
                ["note"] = "canceled_in_batch"
            };

            // ✨ 參數化形式呼叫，100 筆更新也只需幾毫秒
            canceledInQueue = await baseModel.UpdateBatchAsync(
                table: "dbo.FileData_History",
                pkName: "id",
                ids: pendingIds,
                data: updateParams,
                columnsWhitelist: historyWhitelist,
                extraWhereSql: "file_status IN (0, -1, 24, 27)", // 二次防護：確保更新當下狀態未變
                ct: ct
            );
        }
    // === 第四階段：處理執行中 (Running) 任務 - HTTP 轉發 ===
        var runningGroups = rows
            .Where(r => r.FileStatus == 1 && !string.IsNullOrWhiteSpace(r.AssignedNode))
            .GroupBy(r => r.AssignedNode!.Trim(), StringComparer.OrdinalIgnoreCase);

        var details = new Dictionary<int, string>();
        foreach (var id in pendingIds) details[id] = "canceled_in_queue";
        foreach (var id in notFound) details[id] = "not_found";

        // 取得 node endpoint（從 registry snapshot）
        var hbTimeout = int.TryParse(_cfg["Cluster:HeartbeatTimeoutSeconds"], out var t) ? t : 30;
        var nodes = _registry.ListSnapshot(hbTimeout); // 你已有 registry

        var client = _http.CreateClient();

        foreach (var g in runningGroups)
        {
            var nodeName = g.Key;
            var idsOnNode = g.Select(x => x.HistoryId).Distinct().ToList();

            var node = nodes.FirstOrDefault(n => string.Equals(n.NodeName, nodeName, StringComparison.OrdinalIgnoreCase));
            if (node == null || string.IsNullOrWhiteSpace(node.IpAddress))
            {
                _log.LogError("[CANCEL] node not found: {node}", nodeName);
                foreach (var id in idsOnNode) details[id] = "node_not_found";
                continue;
            }

            var baseUrl = node.IpAddress.Trim().TrimEnd('/');
            var url = $"{baseUrl}/api/executor/cancel-batch";

            try
            {
                _log.LogWarning("[CANCEL] -> {node} ids={ids}", nodeName, string.Join(",", idsOnNode));
                var resp = await client.PostAsJsonAsync(url, idsOnNode, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogError("[CANCEL] node {node} http={code}", nodeName, resp.StatusCode);
                    foreach (var id in idsOnNode) details[id] = $"node_http_{(int)resp.StatusCode}";
                }
                else
                {
                    foreach (var id in idsOnNode) details[id] = "cancel_signal_sent";
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[CANCEL] node unreachable {node}", nodeName);
                foreach (var id in idsOnNode) details[id] = "node_unreachable";
            }
        }

        return Ok(new
        {
            ok = true,
            totalRequested = ids.Count,
            canceledInQueue,
            details
        });
    }

    // POST /api/jobs/cancel/123
    [HttpPost("cancel/{id}")]
    public Task<IActionResult> CancelOne(int id, CancellationToken ct)
        => CancelBatch(new List<int> { id }, ct);

    // 用來接 DB 查詢
    private sealed class CancelRow
    {
        public int HistoryId { get; set; }
        public int FileStatus { get; set; }
        public string? AssignedNode { get; set; }
    }
        


    public sealed class UpdatePriorityReq
{
    public int HistoryId { get; set; }
    public int Priority { get; set; }
}

// POST /api/jobs/update-priority
    [HttpPost("update-priority")]
    public async Task<IActionResult> UpdatePriority([FromBody] UpdatePriorityReq req, CancellationToken ct)
    {
        if (!IsMaster()) return Forbid();

        if (req is null || req.HistoryId <= 0)
            return BadRequest(new { ok = false, message = "historyId is required" });

        // 你想限制 priority 範圍可以加在這
        // 例如 UI 用 0~10：
        if (req.Priority < 0 || req.Priority > 10)
            return BadRequest(new { ok = false, message = "priority must be between 0 and 10" });

        var connStr = _cfg.GetConnectionString("DefaultConnection")!;
        await using var conn = new SqlConnection(connStr);
        var baseModel = new FileMoverWeb.Core.BaseModel(conn);

        var patch = new Dictionary<string, object?>
        {
            ["priority"] = req.Priority,
            ["update_time"] = DateTime.Now
        };

        // ✅ gate：只允許「還沒被派發」的任務改 priority
        // 1) 狀態必須還在 queue/pending/phase2 pending
        // 2) assigned_node 必須是空（避免已被別台 claim）
        var updated = await baseModel.UpdateAsync(
            table: "dbo.FileData_History",
            pkName: "id",
            id: req.HistoryId,
            data: patch,
            columnsWhitelist: new[] { "priority", "update_time" },
            extraWhereSql: "file_status IN (0, -1, 24, 27) AND (assigned_node IS NULL OR assigned_node = '')",
            ct: ct);

        if (updated == 0)
            return BadRequest(new
            {
                ok = false,
                message = "update rejected (status not allowed or already assigned to a node)"
            });

        return Ok(new { ok = true, historyId = req.HistoryId, priority = req.Priority });
    }
    }
}