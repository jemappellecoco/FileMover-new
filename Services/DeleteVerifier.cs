using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using FileMoverWeb.Core;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Services
{
    public sealed class DeleteVerifier
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<DeleteVerifier> _log;
        public DeleteVerifier(IConfiguration cfg, ILogger<DeleteVerifier> log)
        {
            _cfg = cfg;
             _log = log;
        }

        // 用資料庫欄位同名，避免 Dapper underscore mapping 問題
        private sealed class HistoryRow
        {
            public int file_id { get; set; }
            public int from_storage_id { get; set; }
            public string? file_type { get; set; }
            public string? from_group { get; set; } 
            public string? action { get; set; }
        }

        private sealed class StorageRow
        {
            public int id { get; set; }
            public int file_id { get; set; }
            public int storage_id { get; set; }
            public string? file_type { get; set; }
        }

        /// <summary>
        /// 1) 查 history → 取得 fid / sid / ft
        /// 2) 刪 FileData_Storage 對應 row（用 id 刪）
        /// 3) 查 FileData_Storage 是否還有該 fid
        /// 4) 如果沒有 → 更新主檔(FileData/CMData) file_status = -1
        /// </summary>
        public async Task<(bool ok, string? message)> VerifyDeleteAsync(int historyId, CancellationToken ct)
        {
             _log.LogInformation("[DELETE_VERIFY] Start historyId={historyId}", historyId);
            var connStr = _cfg.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            var baseModel = new BaseModel(conn);

            // 1) 查 history
            var h = await baseModel.FindAsync<HistoryRow>(
                table: "dbo.FileData_History",
                pkName: "id",
                id: historyId,
                ct: ct);

            if (h == null)
            {
                _log.LogWarning("[DELETE_VERIFY] history not found historyId={historyId}", historyId);
                return (false, $"history not found: {historyId}");
            }
            _log.LogInformation("[DELETE_VERIFY] history found fid={fid} sid={sid} ft={ft}",
            h.file_id, h.from_storage_id, h.file_type);

            // 2) 找到 FileData_Storage 對應 row（fid + sid + ft）
            var row = await baseModel.FindWhereAsync<StorageRow>(
                table: "dbo.FileData_Storage",
                whereSql: @"
                file_id = @fid
                AND storage_id = @sid
                AND (
                    (@ft IS NULL AND file_type IS NULL)
                    OR (file_type = @ft)
                )",
                parameters: new
                {
                    fid = h.file_id,
                    sid = h.from_storage_id,
                    ft  = h.file_type
                },
                ct: ct);

                if (row == null)
                {
                    _log.LogWarning(
                        "[DELETE_VERIFY] storage row not found fid={fid} sid={sid} ft={ft}",
                        h.file_id, h.from_storage_id, h.file_type);

                    return (false,
                        $"FileData_Storage row not found (fid={h.file_id}, sid={h.from_storage_id}, ft={h.file_type ?? "NULL"})");
                }
            _log.LogInformation("[DELETE_VERIFY] deleting storage row id={id}", row.id);

            // 3) 用 id 刪除
            var deleted = await baseModel.DeleteAsync(
                table: "dbo.FileData_Storage",
                pkName: "id",
                id: row.id,
                ct: ct);

            if (deleted == 0)
            {
                _log.LogError("[DELETE_VERIFY] DeleteAsync affected 0 id={id}", row.id);
                return (false, $"DeleteAsync affected 0 (storage id={row.id})");
            }
             _log.LogInformation("[DELETE_VERIFY] storage row deleted id={id}", row.id);
            // 4) 檢查該 fid && file_type 是否還存在於 FileData_Storage（任何一筆都算存在）
            var stillExists = await baseModel.FindWhereAsync<StorageRow>(
                table: "dbo.FileData_Storage",
                whereSql: "file_id = @fid AND file_type = @ft",
                parameters: new { fid = h.file_id ,ft = h.file_type},
                ct: ct);
            // 都不存在 而且 "is_file_4F = 'N' AND is_file_7F = 'N'" 才更新
            if (stillExists == null)
            {
                  _log.LogInformation("[DELETE_VERIFY] no more storage rows, updating master file");
                var isCM = string.Equals(h.file_type, "CM", StringComparison.OrdinalIgnoreCase);

                var affected = await baseModel.UpdateAsync(
                    table: isCM ? "dbo.CMData" : "dbo.FileData",
                    pkName: "id",
                    id: h.file_id,
                    data: new Dictionary<string, object?>
                    {
                        ["file_status"] = -1,
                      
                    },
                    columnsWhitelist: new[] { "file_status" },
                    extraWhereSql: "is_file_4F = 'N' AND is_file_7F = 'N'",
                    ct: ct);

                // 主檔沒更新 
                if (affected == 0)
                {
                    _log.LogError(
                        "[DELETE_VERIFY] master table update failed table={table} id={id}",
                        isCM ? "CMData" : "FileData",
                        h.file_id);

                    return (false, (isCM ? "CMData" : "FileData") +
                                $" not found or not updated (id={h.file_id})");
                }
                else
                {
                    _log.LogInformation(
                    "[DELETE_VERIFY] master updated -1 table={table} id={id}",
                    isCM ? "CMData" : "FileData", h.file_id);
                }
                
            }
            else
            {
                _log.LogInformation(
                    "[DELETE_VERIFY] still storage rows exist fid={fid}, skip master update",
                    h.file_id);
            }

            _log.LogInformation("[DELETE_VERIFY] success historyId={historyId}", historyId);

            return (true, null);
        }


        public async Task FileOnFailAsync(int historyId, CancellationToken ct)
            {
                _log.LogWarning("[] start hid={hid}", historyId);

                var connStr = _cfg.GetConnectionString("DefaultConnection")!;
                await using var conn = new SqlConnection(connStr);
                var baseModel = new BaseModel(conn);

                var h = await baseModel.FindAsync<HistoryRow>(
                    table: "dbo.FileData_History",
                    pkName: "id",
                    id: historyId,
                    ct: ct);

                if (h == null)
                {
                    _log.LogWarning("[DELETE_FAIL_FIX] history not found hid={hid}", historyId);
                    return;
                }

                var act = (h.action ?? "").Trim().ToLowerInvariant();
                if (act != "delete") return;

                if (h.file_id <= 0 || h.from_storage_id <= 0)
                {
                    _log.LogWarning("[DELETE_FAIL_FIX] invalid ids hid={hid}", historyId);
                    return;
                }

                // 🔥 用 storage 表查 group
                var storage = await baseModel.FindAsync<StorageInfo>(
                    table: "dbo.Storage",
                    pkName: "id",
                    id: h.from_storage_id,
                    ct: ct);

                if (storage == null)
                {
                    _log.LogWarning("[DELETE_FAIL_FIX] storage not found sid={sid}", h.from_storage_id);
                    return;
                }

                var grp = (storage.set_group  ?? "").Trim().ToUpperInvariant();
                var ft = (h.file_type ?? "").Trim().ToUpperInvariant();

                var col = grp == "4F" ? "is_file_4F"
                        : grp == "7F" ? "is_file_7F"
                        : null;

                if (col == null)
                {
                    _log.LogWarning("[DELETE_FAIL_FIX] unknown group hid={hid} grp={grp}", historyId, grp);
                    return;
                }

                var table = ft == "CM" ? "dbo.CMData"
                        : ft == "PO" ? "dbo.FileData"
                        : null;

                if (table == null)
                {
                    _log.LogWarning("[DELETE_FAIL_FIX] unknown file_type hid={hid} ft={ft}", historyId, ft);
                    return;
                }

                var affected = await baseModel.UpdateAsync(
                    table: table,
                    pkName: "id",
                    id: h.file_id,
                    data: new Dictionary<string, object?> { [col] = "Y" },
                    columnsWhitelist: new[] { "is_file_4F", "is_file_7F" },
                    ct: ct);

                _log.LogWarning(
                    "[DELETE_FAIL_FIX] restored {table}.{col}='Y' fid={fid} hid={hid} affected={affected}",
                    table, col, h.file_id, historyId, affected);
            }

            private sealed class StorageInfo
            {
                public string? set_group  { get; set; }
            }
    }
}