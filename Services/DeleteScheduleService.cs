// Services/DeleteScheduleService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace FileMoverWeb.Services
{
    /// <summary>
    /// Delete 排程服務（Master Only）
    ///
    /// 功能：
    /// - 掃描 Storage 底下「過期檔案」
    /// - 嘗試對應 FileData（不限制 storage）
    /// - 檢查 FileData_Storage 是否掛在目前掃描的 storageId
    /// - 寫入 FileData_History
    ///
    /// 核心規則：
    /// - FileData 存在 + mapping 正確 + file_status == 11 → history = -1（可刪）
    /// - 其他所有狀況 → history = 904（不合法刪除）
    ///   - 查不到 FileData：file_id=0，note=UB+ext
    ///   - 查到 FileData 但 mapping 不對（實體在A、DB掛B）：file_id=真實 id，note=UB+ext（並 log dbStorageId）
    /// </summary>
    public sealed class DeleteScheduleService : BackgroundService
    {
        private readonly DbConnectionFactory _factory;
        private readonly IConfiguration _cfg;
        private readonly ILogger<DeleteScheduleService> _log;

        public DeleteScheduleService(
            DbConnectionFactory factory,
            IConfiguration cfg,
            ILogger<DeleteScheduleService> log)
        {
            _factory = factory;
            _cfg = cfg;
            _log = log;
        }

        /// <summary>
        /// 不用 LINQ 的 concat，避免你多 import System.Linq
        /// </summary>
        private static IEnumerable<string> EnumerateConcat(IEnumerable<string> a, IEnumerable<string> b)
        {
            foreach (var x in a) yield return x;
            foreach (var x in b) yield return x;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ✅ 只讓 Master 跑
            var role = _cfg["Cluster:Role"] ?? "Slave";
            if (!string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("DeleteScheduleService not started because Role={role}", role);
                return;
            }

            _log.LogInformation("DeleteScheduleService started (MASTER)");

            // ===== 排程設定 =====
            // const int RUN_HOUR = 17;
            // const int RUN_MIN = 12;
            var runHour = _cfg.GetValue<int?>("DeleteSchedule:RunHour") ?? 17;
            var runMin  = _cfg.GetValue<int?>("DeleteSchedule:RunMin")  ?? 12;
            // ✅ 就放在這裡
            _log.LogInformation(
                "DeleteSchedule run time set to {H:D2}:{M:D2}",
                runHour, runMin
            );
            // const int STARTUP_DELAY_SECONDS = 10;
            const int MAX_PER_STORAGE = 3000;
            const bool RECURSIVE = false; // ❌ 不掃子資料夾

            // 啟動後先跑一次
            // if (STARTUP_DELAY_SECONDS > 0)
            //     await Task.Delay(TimeSpan.FromSeconds(STARTUP_DELAY_SECONDS), stoppingToken);

            // await RunOnceAsync(MAX_PER_STORAGE, RECURSIVE, stoppingToken);

            // 每日排程
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var next = new DateTime(now.Year, now.Month, now.Day, runHour, runMin, 0);
                    if (next <= now) next = next.AddDays(1);

                    await Task.Delay(next - now, stoppingToken);
                    await RunOnceAsync(MAX_PER_STORAGE, RECURSIVE, stoppingToken);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    _log.LogError(ex, "DeleteScheduleService loop error");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }

        /// <summary>
        /// 單次掃描與寫入 history
        /// </summary>
        private async Task RunOnceAsync(int maxPerStorage, bool recursive, CancellationToken ct)
        {
            await using var conn = _factory.Create();
            await conn.OpenAsync(ct);

            // 只掃有設定 delete_date 的 storage
            var storages = await conn.QueryAsync<StorageRow>(new CommandDefinition(@"
SELECT id, storage_name, location, priority, delete_date
FROM dbo.Storage
WHERE delete_date IS NOT NULL
  AND delete_date > 0
  AND location IS NOT NULL AND LTRIM(RTRIM(location)) <> ''
", cancellationToken: ct, commandTimeout: 60));

            foreach (var s in storages)
            {
                ct.ThrowIfCancellationRequested();

                var basePath = (s.location ?? "").Trim();
                if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
                    continue;

                var cutoff = DateTime.Today.AddDays(-(s.delete_date ?? 0));
                var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                int scanned = 0, enqueued = 0;

                // ✅ 只掃 mxf / gxf（不分大小寫）
                var allowExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".mxf",
                    ".gxf"
                };

                // ✅ 用 pattern 先過濾一輪（效率最好）
                IEnumerable<string> filesMxf, filesGxf;
                try
                {
                    filesMxf = Directory.EnumerateFiles(basePath, "*.mxf", opt);
                    filesGxf = Directory.EnumerateFiles(basePath, "*.gxf", opt);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "DeleteScheduler enumerate failed: storage={sid} path={path}", s.id, basePath);
                    continue;
                }

                foreach (var filePath in EnumerateConcat(filesMxf, filesGxf))
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;

                    if (enqueued >= maxPerStorage)
                        break;

                    // ✅ 第二層保險：真的只接受 mxf/gxf
                    var extDot = Path.GetExtension(filePath); // ".mxf"
                    if (string.IsNullOrWhiteSpace(extDot) || !allowExts.Contains(extDot))
                        continue;

                    DateTime createTime;
                    try
                    {
                        createTime = File.GetCreationTime(filePath);
                    }
                    catch
                    {
                        continue;
                    }

                    if (createTime > cutoff)
                        continue;

                    var userBit = Path.GetFileNameWithoutExtension(filePath);
                    if (string.IsNullOrWhiteSpace(userBit))
                        continue;

                    // ✅ note 寫 UB + ext（例如 123456）
                    var fileNameForNote = $"{userBit}";

                    // ===== 對應 FileData + mapping 檢查 =====
                    var fileRef = await FindFileAsync(conn, s.id, userBit, extDot, ct);

                    int historyStatus;
                    int fileId;

                    if (fileRef == null)
                    {
                        // ❌ 查不到 FileData：file_id=0，904
                        historyStatus = 904;
                        fileId = 0;
                    }
                    else if (!fileRef.StorageMatch)
                    {
                        // ❌ 查到 FileData 但 mapping 不符（實體在A、DB掛B）：904
                        historyStatus = 904;
                        fileId = fileRef.FileId;

                        _log.LogWarning(
                            "DeleteScheduler mapping mismatch: storage={sid} file={name} fileId={fid} dbStorage={dbSid}",
                            s.id, fileNameForNote, fileRef.FileId, fileRef.DbStorageId
                        );
                    }
                    else if (fileRef.FileStatus == 11)
                    {
                        // ✅ 唯一合法刪除
                        historyStatus = -1;
                        fileId = fileRef.FileId;
                    }
                    else
                    {
                        // ❌ 狀態不符
                        historyStatus = 904;
                        fileId = fileRef.FileId;
                    }

                    // ✅ 只有錯誤才寫 note；可刪(-1) 不寫
                    string? note = (fileRef == null) ? fileNameForNote : null;

                    var inserted = await EnqueueDeleteHistoryAsync(
                        conn,
                        fromStorageId: s.id,
                        fileId: fileId,
                        priority: s.priority ?? 0,
                        historyStatus: historyStatus,
                        note: note,
                        ct: ct
                    );

                    if (inserted) enqueued++;
                }

                _log.LogInformation(
                    "DeleteScheduler storage={sid} scanned={scanned} enqueued={enq}",
                    s.id, scanned, enqueued
                );
            }
        }

        /// <summary>
        /// ✅ 依「實體檔名 (UserBit + ext)」先找 FileData（不限制 storage）
        /// ✅ 再檢查 FileData_Storage 是否包含「目前掃描到的 storageId」（也就是實體檔實際所在 Storage）
        ///
        /// 1) 找不到 FileData：回傳 null（上層會寫 904 + file_id=0 + note）
        /// 2) 找到 FileData 但 mapping 不符合：StorageMatch=false，並回傳 DbStorageId（上層會寫 904 + 真實 file_id）
        /// 3) 找到 FileData 且 mapping 正確：StorageMatch=true，回傳 FileStatus 供上層判斷是否可刪
        /// </summary>
        private async Task<FileRef?> FindFileAsync(
            DbConnection conn,
            int storageId,
            string userBit,
            string extension,
            CancellationToken ct)
        {
            // extension 可能是 ".mxf" / ".gxf"
            var ext = (extension ?? "").Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(userBit) || string.IsNullOrWhiteSpace(ext))
                return null;

            // 1) 先找 FileData（不限制 storage）
            const string sqlFileData = @"
SELECT TOP(1)
    fd.id          AS FileId,
    fd.file_status AS FileStatus
FROM dbo.FileData fd
WHERE fd.UserBit = @ub
  AND UPPER(fd.Extension) = UPPER(@ext)
ORDER BY fd.id DESC;
";
            var fd = await conn.QueryFirstOrDefaultAsync<FileRef>(new CommandDefinition(
                sqlFileData,
                new { ub = userBit, ext },
                cancellationToken: ct,
                commandTimeout: 5
            ));

            if (fd == null)
                return null;

            // 2) 檢查是否掛在目前掃描的 storageId（實體檔所在 A）
            const string sqlMatch = @"
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM dbo.FileData_Storage fds
    WHERE fds.file_id = @fid
      AND fds.storage_id = @sid
) THEN 1 ELSE 0 END;
";
            var match = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                sqlMatch,
                new { fid = fd.FileId, sid = storageId },
                cancellationToken: ct,
                commandTimeout: 5
            ));

            fd.StorageMatch = (match == 1);

            // 3) 若不 match，撈一個 DB 上目前掛的 storageId（B）方便追查
            if (!fd.StorageMatch)
            {
                const string sqlAnyStorage = @"
SELECT TOP(1) fds.storage_id
FROM dbo.FileData_Storage fds
WHERE fds.file_id = @fid
ORDER BY fds.id DESC;
";
                fd.DbStorageId = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                    sqlAnyStorage,
                    new { fid = fd.FileId },
                    cancellationToken: ct,
                    commandTimeout: 5
                ));
            }
            else
            {
                fd.DbStorageId = null;
            }

            return fd;
        }

        /// <summary>
        /// 寫入 FileData_History
        /// - 查不到也會寫（file_id = 0）
        /// - assigned_node 一律 NULL
        /// - note：錯誤時寫 UB+ext（例如 123456.mxf）
        /// </summary>
        private async Task<bool> EnqueueDeleteHistoryAsync(
            DbConnection conn,
            int fromStorageId,
            int fileId,
            int priority,
            int historyStatus,
            string? note,
            CancellationToken ct)
        {
            const int SYSTEM_USER_ID = 1;

            // ⚠️ 你目前用 delete_test 做驗證（不會被真正 delete worker 撿到）
            const string ACTION_NAME = "delete";

            const string sql = @"

    INSERT INTO dbo.FileData_History
    (
        file_id,
        user_id,
        action,
        from_storage_id,
        to_storage_id,
        priority,
        file_type,
        create_time,
        update_time,
        file_status,
        assigned_node,
        note
    )
    VALUES
    (
        @fileId,
        @userId,
        @action,
        @fromSid,
        0,
        @priority,
        'PO',
        GETDATE(),
        GETDATE(),
        @historyStatus,
        NULL,
        @note
    );

    SELECT 1;

";

            var inserted = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                sql,
                new
                {
                    action = ACTION_NAME,
                    fromSid = fromStorageId,
                    fileId,
                    userId = SYSTEM_USER_ID,
                    priority,
                    historyStatus,
                    note
                },
                cancellationToken: ct,
                commandTimeout: 5
            ));

            return inserted == 1;
        }

        // ===== DTOs =====

        private sealed class StorageRow
        {
            public int id { get; set; }
            public string? storage_name { get; set; }
            public string? location { get; set; }
            public int? priority { get; set; }
            public int? delete_date { get; set; }
        }

        private sealed class FileRef
        {
            public int FileId { get; set; }
            public int FileStatus { get; set; }

            // ✅ DB 是否有把這個 file_id 掛在「目前掃描的 storage」
            public bool StorageMatch { get; set; }

            // ✅ 如果不 match，DB 目前掛在哪個 storage（拿一個代表用來列錯）
            public int? DbStorageId { get; set; }
        }
    }
}
