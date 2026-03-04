using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Models.Archive;
using FileMoverWeb.Models.Common;
using FileMoverWeb.Core;
namespace FileMoverWeb.Services
{
    public sealed class ArchivePoller
    {
        private readonly string _connStr;
        private readonly ILogger<ArchivePoller> _log;

        public ArchivePoller(IConfiguration cfg, ILogger<ArchivePoller> log)
        {
            _connStr = cfg.GetConnectionString("DefaultConnection")!;
            _log = log;
        }

        // ✅ GET /archive：列出 file_status=13
public async Task<PagedResult<ArchiveRowDto>> GetArchiveAsync(int take, int page, CancellationToken ct)
{
    take = (take <= 0 || take > 500) ? 200 : take;
    page = page <= 0 ? 1 : page;

    const int st = 13;
    var offset = (page - 1) * take;

    const string sqlCount = @"
SELECT COUNT(1)
FROM dbo.FileData_History h
WHERE h.file_status = @st;
";

    // ✅ 這裡直接把欄位 alias 成 programName / fileName
    // ✅ 加上 OFFSET/FETCH 做分頁
    const string sqlRows = @"
SELECT
    h.id            AS historyId,
    h.file_id       AS fileId,
    h.assigned_node AS assignedNode,
    h.update_time   AS updateTime,

    sFrom.storage_name AS sourceStorage,
    sTo.storage_name   AS destStorage,

    CASE
        WHEN h.file_type = 'PO' THEN f.UserBit
        WHEN h.file_type = 'CM' THEN cm.UserBit
        ELSE NULL
    END AS filename,

    CASE
        WHEN h.file_type = 'PO' THEN (f.filename + ISNULL(f.extension,''))
        WHEN h.file_type = 'CM' THEN (cm.filename + ISNULL(cm.extension,''))
        ELSE NULL
    END AS programName

FROM dbo.FileData_History h
LEFT JOIN dbo.Storage sFrom ON sFrom.id = h.from_storage_id
LEFT JOIN dbo.Storage sTo   ON sTo.id   = h.to_storage_id
LEFT JOIN dbo.FileData f    ON f.id     = h.file_id AND h.file_type = 'PO'
LEFT JOIN dbo.CMData  cm    ON cm.id    = h.file_id AND h.file_type = 'CM'
WHERE h.file_status = @st
ORDER BY h.update_time DESC, h.id DESC
OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
";

    using var conn = new SqlConnection(_connStr);

    var total = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(sqlCount, new { st }, cancellationToken: ct));

    var rows = (await conn.QueryAsync<ArchiveRowDto>(
        new CommandDefinition(sqlRows, new { st, take, offset }, cancellationToken: ct)))
        .ToList();

    var totalPages = (int)Math.Ceiling(total / (double)take);
    if (totalPages <= 0) totalPages = 1;

    return new PagedResult<ArchiveRowDto>
    {
        rows = rows,
        total = total,
        page = page,
        totalPages = totalPages,
        take = take
    };
}
        // ✅ POST /archive/mark：把選到的 13 改 213，並創建一筆 move 任務 (from/to 對調)
        public sealed class MarkResult
        {
            public int updated { get; set; } // 13->213
            public int created { get; set; } // new move tasks
            public List<int> newHistoryIds { get; set; } = new();
        }

//         public async Task<MarkResult> MarkArchiveAndCreateMoveAsync(int[] historyIds, CancellationToken ct)
//         {
//             var ids = (historyIds ?? Array.Empty<int>())
//                 .Where(x => x > 0)
//                 .Distinct()
//                 .ToArray();

//             if (ids.Length == 0) return new MarkResult();

//             const int fromStatus = 13;
//             const int markedStatus = 213;
//             const int newTaskStatus = 0; // ✅ 讓 TaskPoller 撈得到
//             const string newAction = "move";

//             const string sql = @"
// SET XACT_ABORT ON;
// BEGIN TRAN;

// -- 1) 13 -> 213
// UPDATE h
// SET h.file_status = @markedStatus,
//     h.update_time = GETDATE()
// FROM dbo.FileData_History h
// WHERE h.id IN @ids
//   AND h.file_status = @fromStatus;

// DECLARE @updated INT = @@ROWCOUNT;

// -- 2) create new swapped move task (only for rows now in 213)
// DECLARE @newIds TABLE (id INT);

// INSERT INTO dbo.FileData_History
// (
//     user_id,
//     file_id,
//     action,
//     priority,
//     file_status,
//     create_time,
//     file_type,
//     from_storage_id,
//     to_storage_id,
//     assigned_node,
//     note,
//     update_time
// )
// OUTPUT INSERTED.id INTO @newIds(id)
// SELECT
//     h.user_id, 
//     h.file_id,
//     @newAction,
//     h.priority,
//     @newTaskStatus,
//     GETDATE(),
//     h.file_type,
//     h.to_storage_id,     -- ✅ swap
//     h.from_storage_id,   -- ✅ swap
//     NULL,
//     CONCAT(ISNULL(h.note,''), CASE WHEN h.note IS NULL OR h.note = '' THEN '' ELSE ' | ' END,
//            'archive-move from hid=', CAST(h.id AS varchar(20))),
//     GETDATE()
// FROM dbo.FileData_History h
// WHERE h.id IN @ids
//   AND h.file_status = @markedStatus;

// DECLARE @created INT = @@ROWCOUNT;

// COMMIT;

// SELECT @updated AS updated, @created AS created;
// SELECT id FROM @newIds ORDER BY id DESC;
// ";

//             using var conn = new SqlConnection(_connStr);

//             using var grid = await conn.QueryMultipleAsync(
//                 new CommandDefinition(sql, new
//                 {
//                     ids,
//                     fromStatus,
//                     markedStatus,
//                     newTaskStatus,
//                     newAction
//                 }, cancellationToken: ct));

//             var head = await grid.ReadFirstAsync<MarkResult>();
//             head.newHistoryIds = (await grid.ReadAsync<int>()).ToList();
//             return head;
//         }
   
   public async Task<MarkResult> MarkArchiveAndCreateMoveAsync(int[] historyIds, CancellationToken ct)
{
    var ids = (historyIds ?? Array.Empty<int>())
        .Where(x => x > 0)
        .Distinct()
        .ToArray();

    if (ids.Length == 0) return new MarkResult();

    const int fromStatus = 13;
    const int markedStatus = 213;
    const int newTaskStatus = 0;
    const string newAction = "move";

   await using var conn = new SqlConnection(_connStr);
    await conn.OpenAsync(ct);

    var baseModel = new BaseModel(conn);

    // 1) 先撈出目前是 13 的 rows（避免你更新完又撈不到）
    const string pickSql = @"
SELECT
    id,
    user_id,
    file_id,
    priority,
    file_type,
    from_storage_id,
    to_storage_id,
    note
FROM dbo.FileData_History
WHERE id IN @ids AND file_status = @fromStatus;";

    var rows = (await baseModel.QueryAsync<HistRow>(pickSql, new { ids, fromStatus }, ct))
        .ToList();

    if (rows.Count == 0) return new MarkResult();

    var now = DateTime.Now;

    var updated = 0;
    var created = 0;
    var newIds = new List<int>(rows.Count);

    foreach (var h in rows)
    {
        ct.ThrowIfCancellationRequested();

        // 2) 13 -> 213（加 extraWhere 防止被別人改過）
        var ok = await baseModel.UpdateAsync(
            table: "dbo.FileData_History",
            pkName: "id",
            id: h.id,
            data: new Dictionary<string, object?>
            {
                ["file_status"] = markedStatus,
                ["update_time"] = now
            },
            columnsWhitelist: new[] { "file_status", "update_time" },
            extraWhereSql: "file_status = @fromStatus",
            extraWhereParams: new { fromStatus },
            ct: ct);

        if (ok <= 0) continue;
        updated++;

        // 3) 建新 move 任務（swap from/to）
        // ✅ 若 user_id 可能為 null，這裡你要怎麼辦？
        // A) 直接 skip（最安全，不會炸）
        // B) 或 throw（讓 API 回 500，提醒你資料有問題）
        if (h.user_id == null)
            continue;

        var note = BuildArchiveMoveNote(h.note, h.id);

        var newId = await baseModel.CreateAsync(
            table: "dbo.FileData_History",
            data: new Dictionary<string, object?>
            {
                ["user_id"] = h.user_id,
                ["file_id"] = h.file_id,
                ["action"] = newAction,
                ["priority"] = h.priority,
                ["file_status"] = newTaskStatus,
                ["create_time"] = now,
                ["file_type"] = h.file_type,
                ["from_storage_id"] = h.to_storage_id,   // ✅ swap
                ["to_storage_id"] = h.from_storage_id,   // ✅ swap
                ["assigned_node"] = null,
                ["note"] = note,
                ["update_time"] = now
            },
            columnsWhitelist: HistoryInsertWhitelist,
            ct: ct);

        newIds.Add(newId);
        created++;
    }

    return new MarkResult
    {
        updated = updated,
        created = created,
         newHistoryIds = newIds
    };
}

private static string BuildArchiveMoveNote(string? oldNote, int oldHid)
{
    var prefix = string.IsNullOrWhiteSpace(oldNote) ? "" : oldNote.Trim();
    var extra = $"archive-move from hid={oldHid}";
    return string.IsNullOrWhiteSpace(prefix) ? extra : $"{prefix} | {extra}";
}

private static readonly string[] HistoryInsertWhitelist =
{
    "user_id",
    "file_id",
    "action",
    "priority",
    "file_status",
    "create_time",
    "file_type",
    "from_storage_id",
    "to_storage_id",
    "assigned_node",
    "note",
    "update_time"
};

private sealed class HistRow
{
    public int id { get; set; }
    public int? user_id { get; set; }
    public int file_id { get; set; }
    public int priority { get; set; }
    public string? file_type { get; set; }
    public int from_storage_id { get; set; }
    public int to_storage_id { get; set; }
    public string? note { get; set; }
}
    }
}