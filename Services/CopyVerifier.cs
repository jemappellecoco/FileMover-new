using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Core;

namespace FileMoverWeb.Services
{
    public sealed class CopyVerifier
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<CopyVerifier> _log;

        // 你要不要白名單都行，但我建議跟 UpdateAsync 一樣習慣白名單
        private static readonly string[] FileDataStorageWhitelist =
        {
            "file_id", "storage_id", "file_type", "create_time", "file_status"
        };

        public CopyVerifier(IConfiguration cfg, ILogger<CopyVerifier> log)
        {
            _cfg = cfg;
            _log = log;
        }

        // 只做你說的：FileData_Storage 沒有就 insert 一筆（storage_id 用 to 的那個）
        public async Task<(bool ok, string? msg)> VerifyCopyAsync(int historyId, CancellationToken ct)
        {
            _log.LogInformation("[COPY_VERIFY] start hid={hid}", historyId);

            try
            {
                var connStr = _cfg.GetConnectionString("DefaultConnection")!;
                await using var conn = new SqlConnection(connStr);
                var baseModel = new BaseModel(conn);

                // 1️⃣ 撈 history
                var h = await baseModel.FindAsync<HistoryMini>(
                    table: "dbo.FileData_History",
                    pkName: "id",
                    id: historyId,
                    ct: ct);

                if (h == null)
                {
                    _log.LogWarning("[COPY_VERIFY] history not found hid={hid}", historyId);
                    return (false, $"history not found: {historyId}");
                }

                _log.LogInformation(
                    "[COPY_VERIFY] hid={hid} file_id={fid} to_storage_id={sid} file_type={ft} action={act}",
                    historyId, h.file_id, h.to_storage_id, h.file_type, h.action);

                var action = (h.action ?? "").Trim().ToLowerInvariant();
                if (action != "copy")
                {
                    _log.LogInformation("[COPY_VERIFY] skip non-copy action hid={hid}", historyId);
                    return (true, null);
                }

                if (h.file_id <= 0 || h.to_storage_id <= 0)
                {
                    _log.LogWarning("[COPY_VERIFY] invalid ids hid={hid}", historyId);
                    return (false, $"invalid fileId/toStorageId");
                }

                // 2️⃣ 檢查是否已存在
                var exist = await baseModel.FindWhereAsync<int?>(
                    table: "dbo.FileData_Storage",
                    whereSql: "file_id = @fileId AND storage_id = @storageId",
                    parameters: new { fileId = h.file_id, storageId = h.to_storage_id },
                    selectSql: "TOP 1 id",
                    ct: ct);

                if (exist.HasValue)
                {
                    _log.LogInformation(
                        "[COPY_VERIFY] already exists hid={hid} file_id={fid} storage_id={sid}",
                        historyId, h.file_id, h.to_storage_id);

                    return (true, null);
                }

                // 3️⃣ 不存在 → insert
                await baseModel.CreateAsync(
                    table: "dbo.FileData_Storage",
                    data: new Dictionary<string, object?>
                    {
                        ["file_id"] = h.file_id,
                        ["storage_id"] = h.to_storage_id,
                        ["file_type"] = h.file_type,
                        ["create_time"] = DateTime.Now,
                        ["file_status"] = 11
                    },
                    columnsWhitelist: FileDataStorageWhitelist,
                    ct: ct);

                _log.LogInformation(
                    "[COPY_VERIFY] inserted storage record hid={hid} file_id={fid} storage_id={sid}",
                    historyId, h.file_id, h.to_storage_id);

                return (true, null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[COPY_VERIFY] exception hid={hid}", historyId);
                return (false, ex.Message);
            }
        }

        // ✅ 只撈你要用的欄位就好（避免 SELECT * 帶一堆）
        private sealed class HistoryMini
        {
            public int file_id { get; set; }
            public int to_storage_id { get; set; }
            public string? file_type { get; set; }
            public string? action { get; set; }
        }
    }
}