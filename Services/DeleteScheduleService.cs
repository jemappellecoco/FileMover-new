// Services/DeleteScheduleService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Core; // BaseModel

namespace FileMoverWeb.Services
{
    /// <summary>
    /// ✅ 定時刪除排程（Master Only）
    /// - 只看 Storage.delete_date > 0 的 storage 才掃描 location
    /// - 掃描到過期檔（*.mxf / *.gxf）就建立 delete 任務：
    ///     action='delete', file_status=-1, to_storage_id=0
    /// - FileData 查不到也要刪：
    ///     file_id=0, note='123456.mxf'（用 note 讓 history 顯示 + 讓 worker 可刪）
    /// - 批次寫入：BaseModel.CreateManyAsync
    /// - 防重複：同 storage 下已存在 delete/-1 的 (file_id) 或 (note when file_id=0) 不再插入
    /// </summary>
    public sealed class DeleteScheduleService : BackgroundService
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<DeleteScheduleService> _log;

        public DeleteScheduleService(IConfiguration cfg, ILogger<DeleteScheduleService> log)
        {
            _cfg = cfg;
            _log = log;
        }

        private static IEnumerable<string> EnumerateConcat(IEnumerable<string> a, IEnumerable<string> b)
        {
            foreach (var x in a) yield return x;
            foreach (var x in b) yield return x;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ✅ Master only（照你 MasterDispatchService 的寫法）
            var role = (_cfg["Cluster:Role"] ?? "Slave").Trim();
            if (!string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("DeleteScheduleService not started because Role={role}", role);
                return;
            }

            // ===== 排程設定 =====
            var runHour = _cfg.GetValue<int?>("DeleteSchedule:RunHour") ?? 10;
            var runMin  = _cfg.GetValue<int?>("DeleteSchedule:RunMin")  ?? 0;

            var maxPerStorage = _cfg.GetValue<int?>("DeleteSchedule:MaxPerStorage") ?? 3000;
            var recursive = _cfg.GetValue<bool?>("DeleteSchedule:Recursive") ?? false; // 預設不掃子資料夾

            _log.LogInformation("DeleteScheduleService started (MASTER)");
            _log.LogInformation("DeleteSchedule run time set to {H:D2}:{M:D2}, MaxPerStorage={N}, Recursive={R}",
                runHour, runMin, maxPerStorage, recursive);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var next = new DateTime(now.Year, now.Month, now.Day, runHour, runMin, 0);
                    if (next <= now) next = next.AddDays(1);

                    await Task.Delay(next - now, stoppingToken);
                    await RunOnceAsync(maxPerStorage, recursive, stoppingToken);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    _log.LogError(ex, "DeleteScheduleService loop error");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }

        private async Task RunOnceAsync(int maxPerStorage, bool recursive, CancellationToken ct)
        {
            _log.LogInformation("DeleteScheduler RUN START at {time}", DateTime.Now);
            var connStr = _cfg.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connStr))
            {
                _log.LogError("DeleteScheduleService missing connection string: DefaultConnection");
                return;
            }

            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            var baseModel = new BaseModel(conn);

            // ✅ 只掃 delete_date > 0 且 location 有值的 storage
            var storages = await baseModel.QueryAsync<StorageRow>(@"
                SELECT id, storage_name, location, priority, delete_date
                FROM dbo.Storage
                WHERE delete_date IS NOT NULL
                AND delete_date > 0
                AND location IS NOT NULL AND LTRIM(RTRIM(location)) <> ''
                ", new { }, ct);
            _log.LogInformation("DeleteScheduler found {count} storages", storages.Count());
            foreach (var s in storages)
            {
                ct.ThrowIfCancellationRequested();

                var basePath = (s.location ?? "").Trim();
                if (string.IsNullOrWhiteSpace(basePath))
                    continue;

                if (!Directory.Exists(basePath))
                {
                    _log.LogWarning("DeleteScheduler skip: storage={sid} path not exists: {path}", s.id, basePath);
                    continue;
                }

                var keepDays = s.delete_date ?? 0; // >0
                var cutoff = DateTime.Today.AddDays(-keepDays);
                var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

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

                int scanned = 0;
                var pending = new List<DeleteInsertRow>(capacity: Math.Min(maxPerStorage, 2048));

                foreach (var filePath in EnumerateConcat(filesMxf, filesGxf))
                {
                    _log.LogInformation("scan file: {file}", filePath);
                    ct.ThrowIfCancellationRequested();
                    scanned++;

                    if (pending.Count >= maxPerStorage)
                        break;

                    var extDot = Path.GetExtension(filePath); // ".mxf"
                    if (string.IsNullOrWhiteSpace(extDot))
                        continue;

                    if (!extDot.Equals(".mxf", StringComparison.OrdinalIgnoreCase) &&
                        !extDot.Equals(".gxf", StringComparison.OrdinalIgnoreCase))
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

                    // ✅ FileData 沒資料也要刪：noteKey = "123456.mxf"
                    var noteKey = $"{userBit}{extDot}";

                    // 找 FileId（找不到就 0）
                    var fileId = await FindFileIdAsync(conn, userBit, extDot, ct) ?? 0;

                    var now = DateTime.Now;
                    pending.Add(new DeleteInsertRow
                    {
                        file_id = fileId,
                        user_id = 1,
                        action = "delete",
                        from_storage_id = s.id,
                        to_storage_id = 0,
                        priority = s.priority ?? 0,
                        file_type = "PO",
                        create_time = now,
                        update_time = now,
                        file_status = -1,
                        assigned_node = null,
                        // ✅ file_id=0 必填 note（否則 worker/前端都沒得用）
                        note = (fileId == 0) ? noteKey : null
                    });
                }

                if (pending.Count == 0)
                {
                    _log.LogInformation("DeleteScheduler storage={sid} scanned={scanned} pending=0 inserted=0",
                        s.id, scanned);
                    continue;
                }

                // ✅ 防重複：先查既有 delete/-1（同 storage）
                var filtered = await FilterExistingAsync(baseModel, s.id, pending, ct);

                int inserted = 0;
                if (filtered.Count > 0)
                {
                    inserted = await baseModel.CreateManyAsync(
                        table: "dbo.FileData_History",
                        columns: HistoryColumns,
                        rows: filtered,
                        ct: ct
                    );
                }

                _log.LogInformation(
                    "DeleteScheduler storage={sid} scanned={scanned} pending={pend} inserted={ins}",
                    s.id, scanned, pending.Count, inserted
                );
            }
        }

        private static async Task<int?> FindFileIdAsync(SqlConnection conn, string userBit, string extensionWithDot, CancellationToken ct)
        {
            var ub = (userBit ?? "").Trim();
            var ext = (extensionWithDot ?? "").Trim().TrimStart('.'); // "mxf"

            if (string.IsNullOrWhiteSpace(ub) || string.IsNullOrWhiteSpace(ext))
                return null;

            const string sql = @"
SELECT TOP(1) fd.id
FROM dbo.FileData fd
WHERE fd.UserBit = @ub
  AND UPPER(fd.Extension) = UPPER(@ext)
ORDER BY fd.id DESC;
";
            return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                sql,
                new { ub, ext },
                cancellationToken: ct,
                commandTimeout: 5
            ));
        }

        private static async Task<List<DeleteInsertRow>> FilterExistingAsync(
            BaseModel baseModel,
            int storageId,
            List<DeleteInsertRow> pending,
            CancellationToken ct)
        {
            var fileIds = pending.Where(x => x.file_id > 0).Select(x => x.file_id).Distinct().ToList();
            var notes = pending.Where(x => x.file_id == 0 && !string.IsNullOrWhiteSpace(x.note))
                               .Select(x => x.note!)
                               .Distinct()
                               .ToList();

            // 避免 IN () SQL error：空集合塞一個不可能值
            if (fileIds.Count == 0) fileIds.Add(-999999999);
            if (notes.Count == 0) notes.Add("__NO__");

            const string sqlExisting = @"
SELECT file_id AS file_id, note AS note
FROM dbo.FileData_History
WHERE action = 'delete'
  AND file_status = -1
  AND from_storage_id = @sid
  AND (
        (file_id IN @fileIds)
     OR (file_id = 0 AND note IN @notes)
  );
";
            var existed = await baseModel.QueryAsync<ExistKey>(
                sqlExisting,
                new { sid = storageId, fileIds, notes },
                ct
            );

            var existedFileIds = new HashSet<int>();
            var existedNotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in existed)
            {
                if (e.file_id > 0) existedFileIds.Add(e.file_id);
                else if (!string.IsNullOrWhiteSpace(e.note)) existedNotes.Add(e.note!);
            }

            var filtered = new List<DeleteInsertRow>(pending.Count);
            foreach (var row in pending)
            {
                if (row.file_id > 0)
                {
                    if (!existedFileIds.Contains(row.file_id))
                        filtered.Add(row);
                }
                else
                {
                    // file_id=0：用 noteKey 防重複（note 必填）
                    if (!string.IsNullOrWhiteSpace(row.note) && !existedNotes.Contains(row.note))
                        filtered.Add(row);
                }
            }

            return filtered;
        }

        // CreateManyAsync 用的欄位順序
        private static readonly string[] HistoryColumns = new[]
        {
            "file_id",
            "user_id",
            "action",
            "from_storage_id",
            "to_storage_id",
            "priority",
            "file_type",
            "create_time",
            "update_time",
            "file_status",
            "assigned_node",
            "note"
        };

        // ===== DTOs =====
        private sealed class StorageRow
        {
            public int id { get; set; }
            public string? storage_name { get; set; }
            public string? location { get; set; }
            public int? priority { get; set; }
            public int? delete_date { get; set; }
        }

        private sealed class ExistKey
        {
            public int file_id { get; set; }
            public string? note { get; set; }
        }

        /// <summary>
        /// ⚠️ 屬性名稱要跟 columns 完全一致（CreateManyAsync 用 @欄位名）
        /// </summary>
        private sealed class DeleteInsertRow
        {
            public int file_id { get; set; }
            public int user_id { get; set; }
            public string action { get; set; } = "delete";
            public int from_storage_id { get; set; }
            public int to_storage_id { get; set; } = 0;
            public int priority { get; set; }
            public string file_type { get; set; } = "PO";
            public DateTime create_time { get; set; }
            public DateTime update_time { get; set; }
            public int file_status { get; set; } = -1;
            public string? assigned_node { get; set; } = null;
            public string? note { get; set; } = null; // file_id=0 時放 "123456.mxf"
        }
    }
}