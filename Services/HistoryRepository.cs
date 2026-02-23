// Services/HistoryRepository.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Data.Common;
using System.IO;                 // for Path.Combine
using Dapper;
using Microsoft.Data.SqlClient;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace FileMoverWeb.Services
{
    #region DTOs
    /// <summary>
    /// 排程端使用的「待處理/領取」工作模型
    /// </summary>
    public sealed class HistoryTask
    {
        // ⭐ 來源 / 目的 Storage 所屬樓層 group
        public string? FromGroup { get; set; }
        public string? ToGroup   { get; set; }
         public string? Note { get; set; }
        public int HistoryId { get; set; }
        public int FileId    { get; set; }
        public string? FileDataType { get; set; }
        // 檔案資訊
        public long? FileSize4F { get; set; }
        public long? FileSize7F { get; set; }

        // 你原本的 FileSize 可以留著（顯示用）
        public string? TapeNo { get; set; }    // 儲存磁帶主編號
        public string? TapeBakNo { get; set; } // 儲存磁帶備份編號
        public string   FileName { get; set; } = "";
        public long     FileSize { get; set; }             // FileData.filesize
        public string?  UserBit  { get; set; }             // FileData.UserBit

        // 這筆 history 有沒有對到 FileData
        public bool HasFileData { get; set; }              // 0 = 沒有, 1 = 有

        // 來源/目的 Storage
        public int    FromStorageId { get; set; }
        public string? FromName      { get; set; }         // Storage.storage_name
        public string  FromPath      { get; set; } = "";
        public int?    ToStorageId   { get; set; }
        public string? ToName        { get; set; }         // Storage.storage_name
        public string  ToPath        { get; set; } = "";
        public int? RestoreStorageId { get; set; }
        public string? RestorePath { get; set; }
        // 申請者＆動作
        public string?  RequestedBy { get; set; }          // UserData.username
        public string?  Action      { get; set; }          // FileData_History.action
        public DateTime CreateTime  { get; set; }          // FileData_History.create_time
        
        // 目前指派給哪個 node
        public string? AssignedNode { get; set; }          // FileData_History.assigned_node    
        // 目前狀態（0 / 1 / -1 / 24 / 27 / 9xx...）
        public int   FileStatus { get; set; }
        public int?  Priority   { get; set; }
        public string? FromType { get; set; }   // Storage.type
        public string? ToType   { get; set; }   // Storage.type
        public string? Extension { get; set; }   // ← 來自 FileData.extension
        
        // 後端自動組完整路徑（含 .mxf）
        // public string? FullSourcePath =>
        //     string.IsNullOrWhiteSpace(FromPath) || string.IsNullOrWhiteSpace(UserBit)
        //         ? null
        //         : Path.Combine(FromPath, $"{UserBit}.MXF");

        // public string? FullDestPath =>
        //     string.IsNullOrWhiteSpace(ToPath) || string.IsNullOrWhiteSpace(UserBit)
        //         ? null
        //         : Path.Combine(ToPath, $"{UserBit}.MXF");
      private static string NormalizeExt(string? ext)
            {
                ext = (ext ?? "").Trim();
                if (string.IsNullOrEmpty(ext)) return "";      // 沒副檔名就不加
                return ext.StartsWith(".") ? ext : "." + ext;  // DB 存 MXF → 變 .MXF
            }

            private static string BuildFileName(string? userBit, string fileName, string? extension)
            {
                var ext = NormalizeExt(extension);

                // 你目前習慣用 UserBit 當檔名（filename 是顯示用）
                if (!string.IsNullOrWhiteSpace(userBit))
                    return $"{userBit}{ext}";

                // fallback：真的沒 UserBit 才用原 filename（注意：有些 filename 可能已含副檔名）
                return fileName;
            }

            private static string? BuildPath(string? type, string? basePath, string? userBit, string fileName, string? extension)
            {
                if (string.Equals(type, "IC", StringComparison.OrdinalIgnoreCase))
                    return null; // IC 只用 JSON 組 FTP Uri

                if (string.IsNullOrWhiteSpace(basePath))
                    return null;

                var finalName = BuildFileName(userBit, fileName, extension);
                return Path.Combine(basePath, finalName);
            }

            public string? FullSourcePath => BuildPath(FromType, FromPath, UserBit, FileName, Extension);
            public string? FullDestPath   => BuildPath(ToType,   ToPath,   UserBit, FileName, Extension);

    }

    #endregion

    public sealed class HistoryRepository
    {
        private readonly DbConnectionFactory _factory;
        private readonly IConfiguration      _cfg;
        private static readonly SemaphoreSlim _copyClaimLock = new(1, 1);
        private readonly string? _nodeName;
        public HistoryRepository(DbConnectionFactory factory, IConfiguration cfg)
        {
            _factory = factory;
            _cfg     = cfg;
             // ⭐ 這台程式實例對應的節點名稱，例如 4F-M1 / 4F-S1
            _nodeName = _cfg.GetValue<string>("Cluster:NodeName");
        }

        /// <summary>
        /// 取出「待處理＋進行中」清單，僅讀不改狀態
        /// - Phase1 / 刪除：來源樓層 = 本層 group
        /// - Phase2：依 status 決定要給哪一層
        /// </summary>
        public async Task<List<HistoryTask>> ListPendingAsync(int topN, CancellationToken ct)
        {
            if (topN <= 0) topN = 50;
            var group = _cfg.GetValue<string>("FloorRouting:Group");

            using var conn = _factory.Create();

            var sql = @"
            SELECT TOP (@n)
                h.id                 AS HistoryId,
                h.file_id            AS FileId,

                -- ✅ 依 history.file_type 決定資料來自 CMData 還是 FileData
                CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filename  ELSE f.filename  END AS FileName,
                CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.UserBit   ELSE f.UserBit   END AS UserBit,
                CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.extension ELSE f.extension END AS Extension,

                s_from.id            AS FromStorageId,
                s_from.storage_name  AS FromName,
                s_from.location      AS FromPath,
                s_from.set_group     AS FromGroup,

                s_to.id              AS ToStorageId,
                s_to.storage_name    AS ToName,
                s_to.location        AS ToPath,
                s_to.set_group       AS ToGroup,

                u.username           AS RequestedBy,
                h.action             AS Action,
                h.create_time        AS CreateTime,
                h.assigned_node      AS AssignedNode,

                s_from.[type]        AS FromType,
                s_to.[type]          AS ToType,

                h.file_type          AS FileType,
                h.priority           AS Priority,
                
                CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filesize_4F ELSE f.filesize_4F END AS FileSize4F,
                CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filesize_7F ELSE f.filesize_7F END AS FileSize7F,
/*
                CAST(
                    COALESCE(
                        CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filesize_7F ELSE f.filesize_7F END,
                        CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filesize_4F ELSE f.filesize_4F END,
                        0
                    ) AS BIGINT
                ) AS FileSize,
*/
                CAST(h.file_status AS int) AS FileStatus,
                CASE WHEN (cm.id IS NOT NULL OR f.id IS NOT NULL) THEN 1 ELSE 0 END AS HasFileData

            FROM dbo.FileData_History AS h

            -- ✅ 兩張主表都 LEFT JOIN，但用 history.file_type 控制命中
            LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
            LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')

            JOIN dbo.Storage       AS s_from ON s_from.id = h.from_storage_id
            LEFT JOIN dbo.Storage  AS s_to   ON s_to.id   = h.to_storage_id
            LEFT JOIN dbo.UserData AS u      ON u.id      = h.user_id
            WHERE 
            (
                -- ⭐ Phase 1：由來源樓層負責的任務
                -- 包含：
                --   0   → 新的 copy 任務
                --   1   → 正在搬移中的任務
                --  -1   → delete 任務（待刪除）
                --  800  → copy/delete 需要在本樓層重試的任務
                --  2  → phase2 pending 
                h.file_status IN (0, 1, 2 ,3,-1, 800,23)
                AND s_from.set_group = @group
            )
            OR
            (
                -- ⭐ Phase 2：跨樓層回遷（RESTORE → 目的地）
                -- 24 → 4F → 7F 回遷，由 7F 執行
                -- 27 → 7F → 4F 回遷，由 4F 執行
                (h.file_status = 24 AND @group = '7F')     -- 4F → 7F 回遷
                OR
                (h.file_status = 27 AND @group = '4F')     -- 7F → 4F 回遷
            )
            ORDER BY
                CASE WHEN h.file_status = 1 THEN 0 ELSE 1 END,    -- 先把進行中排前面
                h.priority DESC,                                  -- 再依 Priority
                h.create_time ASC;                                -- 同優先級比時間";

           var rows = await conn.QueryAsync<HistoryTask>(
                new CommandDefinition(
                    sql, new { n = topN, group },
                    cancellationToken: ct,
                    commandTimeout: 5));
            return rows.ToList();
        }

        /// <summary>取得本樓層 RESTORE storage 的名稱</summary>
        public async Task<string?> GetRestoreNameAsync(string group, CancellationToken ct)
        {
            using var conn = _factory.Create();

            const string sql = @"
            SELECT TOP 1 storage_name
            FROM dbo.Storage
            WHERE [type] = 'RESTORE'
            AND set_group = @group
            ORDER BY priority;";

            return await conn.ExecuteScalarAsync<string?>(
                new CommandDefinition(sql, new { group }, cancellationToken: ct,  commandTimeout: 5  ));
        }

        /// <summary>
        /// Phase2：列出所有等待回遷的歷史紀錄（file_status = 14 / 17）
        /// （前端「回遷清單」頁面用）
        /// </summary>
            public async Task<List<HistoryTask>> ListPhase2PendingAsync(int topN, CancellationToken ct)
            {
                if (topN <= 0) topN = 50;

                using var conn = _factory.Create();

                var sql = @"
            SELECT TOP (@n)
                h.id                 AS HistoryId,
                h.file_id            AS FileId,

                -- 🆕 新增：從磁帶資訊表取得編號
                t.tape_no            AS TapeNo,
                t.tape_bak_no        AS TapeBakNo,

                CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filename  ELSE f.filename  END AS FileName,
                CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.UserBit   ELSE f.UserBit   END AS UserBit,
                CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.extension ELSE f.extension END AS Extension,
/*
                CAST(
                    COALESCE(
                        CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filesize_7F ELSE f.filesize_7F END,
                        CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filesize_4F ELSE f.filesize_4F END,
                        0
                    ) AS BIGINT
                ) AS FileSize,
*/
                h.file_type          AS FileType,
                h.from_storage_id    AS FromStorageId,
                h.to_storage_id      AS ToStorageId,
                s_from.set_group     AS FromGroup,
                s_to.set_group       AS ToGroup,

                CASE 
                    WHEN s_from.set_group IS NOT NULL AND s_to.set_group IS NOT NULL AND s_from.set_group <> s_to.set_group
                    THEN sr.storage_name
                    ELSE s_from.storage_name
                END AS FromName,

                CASE 
                    WHEN s_from.set_group IS NOT NULL AND s_to.set_group IS NOT NULL AND s_from.set_group <> s_to.set_group
                    THEN sr.location
                    ELSE s_from.location
                END AS FromPath,

                s_to.storage_name    AS ToName,
                s_to.location        AS ToPath,

                u.username           AS RequestedBy,
                h.action             AS Action,
                h.create_time        AS CreateTime,
                s_from.[type]        AS FromType,
                s_to.[type]          AS ToType,
                CAST(h.file_status AS int) AS FileStatus

            FROM dbo.FileData_History AS h
            LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
            LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')

            -- 🆕 核心變更：透過 FileData 的 tape_id 關聯 TapeInfo 表
            LEFT JOIN dbo.TapeInfo t  ON f.tape_id = t.id 

            LEFT JOIN dbo.Storage  AS s_from ON s_from.id = h.from_storage_id
            LEFT JOIN dbo.Storage  AS s_to   ON s_to.id   = h.to_storage_id
            LEFT JOIN dbo.Storage  AS sr     ON sr.[type] = 'RESTORE' AND sr.set_group = s_to.set_group
            LEFT JOIN dbo.UserData AS u      ON u.id = h.user_id

            WHERE h.file_status IN (14, 17)
            ORDER BY h.priority, h.update_time DESC, h.id DESC;
            ";

                var rows = await conn.QueryAsync<HistoryTask>(
                    new CommandDefinition(sql, new { n = topN }, cancellationToken: ct,commandTimeout: 5));

                return rows.ToList();
            }

        /// <summary>
        /// Phase2：使用者在前端勾選「回遷」後，將 14/17 改成 24/27（等待回遷）
        /// </summary>
            public async Task MarkPhase2ToReadyAsync(int[] historyIds, CancellationToken ct)
        {
            if (historyIds == null || historyIds.Length == 0) return;

            using var conn = _factory.Create();

            const string sql = @"
        ;WITH T AS (
            SELECT
                h.id,
                CAST(h.file_status AS int) AS file_status,
                s_to.set_group AS to_group
            FROM dbo.FileData_History h
            LEFT JOIN dbo.Storage s_to ON s_to.id = h.to_storage_id
            WHERE h.id IN @ids
            AND h.file_status IN (14, 17)
        )
        UPDATE h
        SET
            file_status =
                CASE
                    WHEN T.to_group = '7F' THEN 24
                    WHEN T.to_group = '4F' THEN 27
                    ELSE
                        -- 保底：目的地 group 不明時，維持原本邏輯（不會破壞你舊流程）
                        CASE WHEN T.file_status = 14 THEN 24 ELSE 27 END
                END,
            assigned_node = NULL,
            update_time = GETDATE()
        FROM dbo.FileData_History h
        JOIN T ON T.id = h.id;";

            await conn.ExecuteAsync(
                new CommandDefinition(sql, new { ids = historyIds }, cancellationToken: ct,commandTimeout: 10));
        }

        /// <summary>
        /// Phase2：以批次方式領取一批「回遷任務」（舊有 batch 版本，現在 slot 模式可不再使用）
        /// </summary>
        public async Task<List<HistoryTask>> ClaimPhase2Async(
            int batchSize,
            string? group,
            CancellationToken ct)
        {
            using var conn = _factory.Create();
            await (conn as DbConnection)!.OpenAsync(ct);
            using var tran = (conn as DbConnection)!.BeginTransaction();
            var nodeName = _nodeName;
            var ids = await conn.QueryAsync<int>(
                new CommandDefinition(@"
            ;WITH P AS (
            SELECT TOP (@n) h.id
            FROM dbo.FileData_History h 
            JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
            WHERE
                    h.action IN ('copy','move')
                AND h.file_status IN (24, 27)          -- ⭐ Phase2 待回遷
                AND (@group IS NULL OR s_from.set_group = @group)
                -- ⭐ Node 篩選：如果有設定 NodeName，就只撿指派給自己或尚未指派的
                AND (
                    @nodeName IS NULL
                OR @nodeName = ''
                OR h.assigned_node IS NULL
                OR h.assigned_node = @nodeName
                )
            ORDER BY ISNULL(h.priority, 1) DESC,        -- ⭐ 優先級大的先回遷
                    h.update_time ASC,
                    h.id ASC
            )
            UPDATE h
            SET h.file_status = 2 ,
            --SET h.file_status = 1 ,
                h.update_time = GETDATE()
            OUTPUT inserted.id
            FROM dbo.FileData_History h
            JOIN P ON P.id = h.id;",
            new { n = batchSize, group, nodeName },   // ⭐ 記得把 nodeName 傳進去
            transaction: tran,
            cancellationToken: ct));

            if (!ids.Any())
            {
                tran.Commit();
                return new();
            }

            var tasks = (await conn.QueryAsync<HistoryTask>(
            new CommandDefinition(@"
            SELECT 
                h.id              AS HistoryId,
                h.file_id         AS FileId,
                h.from_storage_id AS FromStorageId,
                h.to_storage_id   AS ToStorageId,

                COALESCE(cm.filename,  f.filename)  AS FileName,
                COALESCE(cm.UserBit,   f.UserBit)   AS UserBit,
                COALESCE(cm.extension, f.extension) AS Extension,

                s_from.location   AS FromPath,
                s_to.location     AS ToPath,
                s_from.set_group  AS FromGroup,
                s_to.set_group    AS ToGroup,

                CAST(h.file_status AS int) AS FileStatus,
                h.priority        AS Priority,
                h.file_type       AS FileType,

                CASE WHEN (cm.id IS NOT NULL OR f.id IS NOT NULL) THEN 1 ELSE 0 END AS HasFileData
            FROM dbo.FileData_History h
            LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
            LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
            JOIN dbo.Storage         s_from ON s_from.id = h.from_storage_id
            LEFT JOIN dbo.Storage    s_to   ON s_to.id   = h.to_storage_id
            WHERE h.id IN @ids;",
                    new { ids }, transaction: tran, cancellationToken: ct,commandTimeout: 5))).ToList();

            tran.Commit();
            return tasks;
        }

        /// <summary>
        /// Phase2（slot 版）：以 TOP 1 領取一筆「回遷任務」，依 priority 排序
        /// </summary>
        public async Task<HistoryTask?> ClaimPhase2TopOneAsync(
            string? group,
            CancellationToken ct)
        {
            using var conn = _factory.Create();
            var dbConn = (DbConnection)conn;
            await dbConn.OpenAsync(ct);
            using var tran = dbConn.BeginTransaction();

            var nodeName = _nodeName;
            var useNodeFilter = !string.IsNullOrWhiteSpace(nodeName);

            try
            {
                // 🔹 第一步：【原子性標記】並【僅取回 HistoryId】
                // 這樣可以確保「號碼牌」是絕對正確的，完全不會被後續複雜的 JOIN 干擾映射順序
                // 🔹 第一步：【標記狀態】並【僅取回 ID】
                var historyId = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                ;WITH P AS (
                    SELECT TOP (1) h.id
                    FROM dbo.FileData_History h 
                    JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
                    WHERE
                        h.action = 'copy'
                        AND (@group IS NULL OR s_from.set_group = @group)
                        AND h.file_status IN (24, 27)
                        -- 💡 修改重點：強制限制只能領取指派給自己的任務
                        AND h.assigned_node = @nodeName 
                    ORDER BY 
                        ISNULL(h.priority, 1) DESC,
                        h.create_time ASC,
                        h.id ASC
                )
                UPDATE h
                SET h.file_status = 1,
                    h.update_time = GETDATE()
                    -- assigned_node 已經由 Master 填好了，這裡不需要再 Set
                OUTPUT inserted.id
                FROM dbo.FileData_History h
                JOIN P ON P.id = h.id;", 
                    new { group, nodeName }, // useNodeFilter 也不需要了，因為現在是強致過濾
                    transaction: tran, cancellationToken: ct,commandTimeout: 5));

                if (!historyId.HasValue)
                {
                    tran.Commit();
                    return null;
                }

                // 🔹 第二步：【精確組裝物件】
                // 已經拿到確定的 ID，這時候才做複雜的 JOIN。
                // 因為有 WHERE h.id = @id，Dapper 映射會變得極度穩定。
                var task = await conn.QuerySingleOrDefaultAsync<HistoryTask>(new CommandDefinition(@"
                    SELECT 
                        h.id              AS HistoryId,
                        h.file_id         AS FileId,
                        h.from_storage_id AS FromStorageId,
                        h.to_storage_id   AS ToStorageId,
                        h.action          AS Action,
                        h.file_type       AS FileType,
                        h.priority        AS Priority,
                        h.assigned_node   AS AssignedNode,
                        CAST(h.file_status AS int) AS FileStatus,

                        -- ✅ 依 file_type 取對應主表資料，並強制給予明確別名
                        COALESCE(cm.filename,  f.filename)   AS FileName,
                        COALESCE(cm.UserBit,   f.UserBit)    AS UserBit,
                        COALESCE(cm.extension, f.extension)  AS Extension,
                        COALESCE(cm.filesize_4F, f.filesize_4F) AS FileSize4F,
                        COALESCE(cm.filesize_7F, f.filesize_7F) AS FileSize7F,

                        s_from.storage_name AS FromName,
                        s_from.location     AS FromPath,
                        s_from.[type]       AS FromType,
                        s_from.set_group    AS FromGroup,

                        s_to.storage_name   AS ToName,
                        s_to.location       AS ToPath,
                        s_to.[type]         AS ToType,
                        s_to.set_group      AS ToGroup,

                        CASE WHEN (cm.id IS NOT NULL OR f.id IS NOT NULL) THEN 1 ELSE 0 END AS HasFileData

                    FROM dbo.FileData_History h
                    LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
                    LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
                    JOIN dbo.Storage         s_from ON s_from.id = h.from_storage_id
                    LEFT JOIN dbo.Storage    s_to   ON s_to.id   = h.to_storage_id
                    WHERE h.id = @id;",
                    new { id = historyId.Value },
                    transaction: tran,
                    cancellationToken: ct,commandTimeout: 5));

                tran.Commit();
                return task;
            }
            catch (Exception)
            {
                // 如果在組裝資料時出錯，Rollback 確保 ID 不會卡在狀態 2
                tran.Rollback();
                throw;
            }
        }
        /// Slot-based 搬移：一次領取「一筆」 copy 任務：
        /// - 僅處理 action='copy'
        /// - file_status = 0 為新任務

        /// ✅ 加上應用程式層級鎖，避免多個 slot 互搶造成死結 / 重複領取
        /// </summary>
        public async Task<HistoryTask?> ClaimCopyTopOneAsync(
            int retryMinutes,
            string? group,
            CancellationToken ct)
        {
            // 🔒 一次只允許一個 slot 進來 Claim，避免死結 & 重複領取同一筆
            await _copyClaimLock.WaitAsync(ct);
            try
            {
                using var conn = _factory.Create();
                var dbConn = (DbConnection)conn;
                await dbConn.OpenAsync(ct);
                using var tran = dbConn.BeginTransaction();
                var nodeName = _nodeName; // ⭐ 這台節點名稱
                var useNodeFilter = !string.IsNullOrWhiteSpace(nodeName);

                // 🔹 先標記「來源 StorageId 無效」→ 901
                await conn.ExecuteAsync(new CommandDefinition(@"
        UPDATE h
        SET h.file_status = 901,
            h.update_time = GETDATE()
        FROM dbo.FileData_History h
        LEFT JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
        WHERE h.action = 'copy'
        AND h.file_status IN (0, 1)
        AND s_from.id IS NULL;      -- 找不到來源 Storage
        ",
                    transaction: tran, cancellationToken: ct,commandTimeout: 5));

                // 🔹 再標記「目的地 StorageId 無效」→ 902
                await conn.ExecuteAsync(new CommandDefinition(@"
        UPDATE h
        SET h.file_status = 902,
            h.update_time = GETDATE()
        FROM dbo.FileData_History h
        JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
        LEFT JOIN dbo.Storage s_to ON s_to.id = h.to_storage_id
        WHERE h.action = 'copy'
        AND h.file_status IN (0, 1)
        AND h.to_storage_id IS NOT NULL
        AND s_to.id IS NULL;        -- 找不到目的地 Storage
        ",
                    transaction: tran, cancellationToken: ct,commandTimeout: 5
                    ));

   // 🔹 3. 第一步：【標記狀態】並【僅取回 ID】
        // 這裡不 JOIN 任何資料主表，保證 OUTPUT 只有一列，絕不拼錯 ID
        var historyId = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(@"
        ;WITH P AS (
            SELECT TOP (1) h.id
            FROM dbo.FileData_History h 
            JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
            WHERE
                h.action = 'copy'
                AND (@group IS NULL OR s_from.set_group = @group)
                AND h.file_status IN (0, 800)
                -- 💡 改成這樣：只領取 Master 已經指派給我的任務
                AND h.assigned_node = @nodeName 
            ORDER BY 
                ISNULL(h.priority, 1) DESC,
                h.create_time ASC,
                h.id ASC
        )
        UPDATE h
        SET h.file_status = 1,
            h.update_time = GETDATE()
        OUTPUT inserted.id
        FROM dbo.FileData_History h
        JOIN P ON P.id = h.id;", 
            new { group, nodeName }, // 不需要 useNodeFilter 了
            transaction: tran, cancellationToken: ct,commandTimeout: 5));
                if (!historyId.HasValue)
                {
                    tran.Commit();
                    return null;
                }

        // 🔹 4. 第二步：【組裝物件】
        // 已經拿到確定的 ID，這時候才做複雜的 JOIN，就算 Mapping 失敗也只會回 null，不會出現幽靈 ID
        var task = await conn.QueryFirstOrDefaultAsync<HistoryTask>(new CommandDefinition(@"
        SELECT 
            h.id              AS HistoryId,
            h.file_id         AS FileId,
            h.from_storage_id AS FromStorageId,
            h.to_storage_id   AS ToStorageId,
            h.action          AS Action,
            h.assigned_node   AS AssignedNode, 

            -- ✅ 依 file_type 取對主表資料
            COALESCE(cm.filename, f.filename)      AS FileName,
            COALESCE(cm.UserBit,  f.UserBit)       AS UserBit,
            COALESCE(cm.extension,f.extension)     AS Extension,
            COALESCE(cm.filesize_4F,f.filesize_4F) AS FileSize4F,
            COALESCE(cm.filesize_7F,f.filesize_7F) AS FileSize7F,
            
            CAST(h.file_status AS int) AS FileStatus,
            
            s_from.storage_name      AS FromName,
            s_from.location          AS FromPath,
            s_from.[type]            AS FromType,
            s_from.set_group         AS FromGroup,

            s_to.storage_name        AS ToName,
            s_to.location            AS ToPath,
            s_to.[type]              AS ToType,
            s_to.set_group           AS ToGroup,

            h.priority               AS Priority,
            h.file_type              AS FileType,
        
            CASE WHEN (cm.id IS NOT NULL OR f.id IS NOT NULL) THEN 1 ELSE 0 END AS HasFileData
        FROM dbo.FileData_History h
        JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
        LEFT JOIN dbo.Storage s_to ON s_to.id = h.to_storage_id
        LEFT JOIN dbo.CMData  cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
        LEFT JOIN dbo.FileData f ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
        WHERE h.id = @id;", 
        new { id = historyId.Value }, 
            transaction: tran, cancellationToken: ct,commandTimeout: 5));

                tran.Commit();
                return task;
            }
            catch (SqlException ex) when (ex.Number == 1205)
            {
                // 🧯 死結犧牲者
                return null;
            }
            finally
            {
                _copyClaimLock.Release();
            }
        }    
                

        /// <summary>標記搬移成功：status='11'</summary>
       public async Task<int> CompleteAsync(int historyId, CancellationToken ct)
        {
            for (int i = 0; i < 3; i++)
        {
        try
        {
            using var conn = _factory.Create();
            await conn.OpenAsync(ct);
            using var tx = conn.BeginTransaction();

            const string sql = @"
                DECLARE @now DATETIME = GETDATE();
                DECLARE @fid INT;
                DECLARE @ft  NVARCHAR(50);
                DECLARE @toSid INT;

                SELECT 
                    @fid   = h.file_id,
                    @ft    = h.file_type,
                    @toSid = h.to_storage_id
                FROM dbo.FileData_History h
                WHERE h.id = @historyId;

                -- 1) History：成功 11
                UPDATE dbo.FileData_History
                SET file_status = 11,
                    update_time  = @now
                WHERE id = @historyId;

                -- 2) 主表
                IF (UPPER(ISNULL(@ft,'')) = 'CM')
                BEGIN
                    UPDATE dbo.CMData SET file_status = 11 WHERE id = @fid;
                END
                ELSE
                BEGIN
                    UPDATE dbo.FileData SET file_status = 11 WHERE id = @fid;
                END
                /*
                -- 3) FileData_Storage upsert(to)
                UPDATE s
                SET s.create_time = @now,
                    s.file_status = 11
                FROM dbo.FileData_Storage s
                WHERE s.file_id    = @fid
                AND s.storage_id = @toSid
                AND (
                        (@ft IS NULL AND s.file_type IS NULL)
                        OR (s.file_type = @ft)
                    );

                IF (@@ROWCOUNT = 0)
                BEGIN
                    INSERT INTO dbo.FileData_Storage (file_id, storage_id, file_type, create_time, file_status)
                    VALUES (@fid, @toSid, @ft, @now, 11);
                END
                */

                -- 3) FileData_Storage: 沒資料才插入，有資料就 pass (不更新也不報錯)
                INSERT INTO dbo.FileData_Storage (file_id, storage_id, file_type, create_time, file_status)
                SELECT @fid, @toSid, @ft, @now, 11
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.FileData_Storage 
                    WHERE file_id = @fid 
                    AND storage_id = @toSid 
                    AND (
                            (@ft IS NULL AND file_type IS NULL) 
                            OR (file_type = @ft)
                        )
                );
                SELECT 11 AS [status];
                ";

            // ✅ 這裡要用 ExecuteScalar/QuerySingle 才拿得到 SELECT 的值
            var status = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, new { historyId }, transaction: tx, cancellationToken: ct,commandTimeout: 15));

            tx.Commit();
            return status;
            }
                catch (SqlException ex) when (ex.Number == 1205)
                {
                    if (i == 2) throw; // 第三次失敗才拋出
                    await Task.Delay(Random.Shared.Next(100, 500), ct); // 隨機延遲後重試
                }
            }
            return 0;
        }

        public async Task FailAsync(int hid, int code, string? note, CancellationToken ct)
        {
            using var conn = _factory.Create();

            // 1) 先標記 history 失敗/取消
            await conn.ExecuteAsync(new CommandDefinition(@"
        UPDATE dbo.FileData_History
        SET file_status = @code,
            assigned_node = NULL,
            update_time = GETDATE(),
            note = @note
        WHERE id = @hid;
        ", new { hid, code, note }, cancellationToken: CancellationToken.None,commandTimeout: 15)); // ✅ 取消要寫得進DB，別用 request ct

            // 2) ✅ 只有使用者取消（999）才回補 is_file
            if (code != 999) return;

            await conn.ExecuteAsync(new CommandDefinition(@"
        DECLARE @fid INT, @ft NVARCHAR(50), @grp NVARCHAR(10);

        SELECT
        @fid = h.file_id,
        @ft  = h.file_type,
        @grp = s.set_group
        FROM dbo.FileData_History h
        JOIN dbo.Storage s ON s.id = h.from_storage_id
        WHERE h.id = @hid;

        IF (@fid IS NULL) RETURN;

        IF (UPPER(ISNULL(@ft,'')) = 'CM')
        BEGIN
        UPDATE dbo.CMData
        SET is_file_4F = CASE WHEN @grp='4F' THEN 'Y' ELSE is_file_4F END,
            is_file_7F = CASE WHEN @grp='7F' THEN 'Y' ELSE is_file_7F END
        WHERE id = @fid;
        END
        ELSE
        BEGIN
        UPDATE dbo.FileData
        SET is_file_4F = CASE WHEN @grp='4F' THEN 'Y' ELSE is_file_4F END,
            is_file_7F = CASE WHEN @grp='7F' THEN 'Y' ELSE is_file_7F END
        WHERE id = @fid;
        END
        ", new { hid }, cancellationToken: CancellationToken.None,commandTimeout: 10));
        }


    public sealed class ArchiveRow
    {
        public int HistoryId { get; set; }
        public int FileId { get; set; }

        public string? FileName { get; set; }
        public string? UserBit { get; set; }
        public string? Extension { get; set; }

        public int FromStorageId { get; set; }
        public string? FromName { get; set; }
        public string? FromPath { get; set; }

        public int? ToStorageId { get; set; }
        public string? ToName { get; set; }
        public string? ToPath { get; set; }

        public string? Action { get; set; }
        public DateTime UpdateTime { get; set; }
        public string? AssignedNode { get; set; }

        public int Status { get; set; }   // 這裡會是 13
    }

    public async Task<(int total, List<ArchiveRow> rows)> ListArchiveAsync(
        int take,
        int page,
        string? group,
        string? q,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        if (take <= 0) take = 200;
        if (page <= 0) page = 1;

        using var conn = _factory.Create();

        // where 條件：只要 status=13 + group/q/from/to
        var where = @"
    WHERE h.file_status = 13
    AND (
            @group IS NULL
            OR s_from.set_group = @group
        )
    AND (
            @q IS NULL OR @q = ''
            OR (CASE WHEN UPPER(ISNULL(h.file_type,''))='CM' THEN cm.UserBit ELSE f.UserBit END) LIKE '%' + @q + '%'
            OR (CASE WHEN UPPER(ISNULL(h.file_type,''))='CM' THEN cm.filename ELSE f.filename END) LIKE '%' + @q + '%'
        )
    AND (
            @from IS NULL OR h.update_time >= @from
        )
    AND (
            @to IS NULL OR h.update_time < @to
        )
    ";

        // 1) count
        var sqlCount = @"
            SELECT COUNT(1)
            FROM dbo.FileData_History h
            JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
            LEFT JOIN dbo.Storage s_to ON s_to.id = h.to_storage_id
            LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,''))='CM'
            LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
            " + where + ";";

        var total = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sqlCount, new { group, q, from, to }, cancellationToken: ct,commandTimeout: 5));

    // 2) rows (分頁)
        var offset = (page - 1) * take;

        var sqlRows = @"
        SELECT
            h.id AS HistoryId,
            h.file_id AS FileId,

            COALESCE(
                CASE WHEN UPPER(ISNULL(h.file_type,''))='CM' THEN cm.filename ELSE f.filename END,
                CONCAT('(deleted file_id=', h.file_id, ')')
            ) AS FileName,

            CASE WHEN UPPER(ISNULL(h.file_type,''))='CM' THEN cm.UserBit ELSE f.UserBit END AS UserBit,
            CASE WHEN UPPER(ISNULL(h.file_type,''))='CM' THEN cm.extension ELSE f.extension END AS Extension,

            h.from_storage_id AS FromStorageId,
            s_from.storage_name AS FromName,
            s_from.location AS FromPath,

            h.to_storage_id AS ToStorageId,
            s_to.storage_name AS ToName,
            s_to.location AS ToPath,

            h.action AS Action,
            h.update_time AS UpdateTime,
            h.assigned_node AS AssignedNode,

            CAST(h.file_status AS int) AS Status

        FROM dbo.FileData_History h
        JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
        LEFT JOIN dbo.Storage s_to ON s_to.id = h.to_storage_id
        LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,''))='CM'
        LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
        " + where + @"
        ORDER BY h.update_time DESC, h.id DESC
        OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
        ";

    var rows = (await conn.QueryAsync<ArchiveRow>(
        new CommandDefinition(sqlRows, new { group, q, from, to, offset, take }, cancellationToken: ct,commandTimeout: 5)))
        .ToList();

    return (total, rows);
    }
    public async Task<int> CreateArchiveTaskAndMarkSourceAsync(
        int historyId,
        CancellationToken ct)
    {
        using var conn = _factory.Create();
        await conn.OpenAsync(ct);

        using var tx = conn.BeginTransaction();

        try
        {
      
            const string insertSql = @"
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
        assigned_node
    )
    SELECT
        h.file_id,
        h.user_id,
        h.action,
        h.to_storage_id,
        h.from_storage_id,
        h.priority,
        h.file_type,
        GETDATE(),
        GETDATE(),
        0,            -- ⭐ 歸檔任務
        NULL
    FROM dbo.FileData_History h
    WHERE h.id = @historyId;

    SELECT CAST(SCOPE_IDENTITY() AS int);
    ";

        var newHistoryId = await conn.ExecuteScalarAsync<int>(
            new Dapper.CommandDefinition(
                insertSql,
                new { historyId },
                transaction: tx,
                cancellationToken: ct,commandTimeout: 15
            )
        );

        // ② 將原任務標記為「已送歸檔」
        const string updateSql = @"
        UPDATE dbo.FileData_History
        SET file_status = 213,
            update_time = GETDATE()
        WHERE id = @historyId;
        ";

        await conn.ExecuteAsync(
            new Dapper.CommandDefinition(
                updateSql,
                new { historyId },
                transaction: tx,
                cancellationToken: ct,commandTimeout: 15
            )
        );

        tx.Commit();
        return newHistoryId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }



/// <summary>
/// 歷史紀錄清單（成功＋失敗）— 分頁版
/// - status: all / success / fail
/// - take: 每頁筆數
/// - page: 第幾頁（1-based）
/// - group: null=全部；"4F"/"7F"=只看來源樓層
/// - q: 搜尋 UserBit / filename（模糊）
/// </summary>
    public async Task<(int total, List<HistoryRow> rows)> ListHistoryAsync(
    string status,
    int take,
    int page,
    string? group,
    string? q,
    DateTime? from,
    DateTime? to,
    CancellationToken ct)
{
    // ---- paging guard
    if (take <= 0) take = 50;
    
    if (page <= 0) page = 1;

    status = (status ?? "all").Trim().ToLowerInvariant();
    q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

    // ✅ 沒給日期就預設最近 7 天（避免 history 全表掃爆）
    // if (!from.HasValue && !to.HasValue)
    // {
    //     to = DateTime.Now;
    //     from = to.Value.AddDays(-7);
    // }
    if (!from.HasValue && !to.HasValue)
{
    // 只有在「沒有關鍵字」且「沒有選日期」時，才強制抓最近 7 天
    if (string.IsNullOrWhiteSpace(q))
    {
        to = DateTime.Now;
        from = to.Value.AddDays(-7);
    }
}
    var offset = (page - 1) * take;

    // ✅ contains 模式：使用者輸入 *ABC 才做 %ABC%（平常一律 prefix 搜尋 ABC%）
    bool containsMode = false;
    if (!string.IsNullOrWhiteSpace(q) && q!.StartsWith("*"))
    {
        containsMode = true;
        q = q.TrimStart('*').Trim();
        if (q.Length == 0) q = null;
    }

    // ✅ status 條件（沿用你前端）
    string whereStatus = "";
    if (status == "success")
    {
        whereStatus = " AND h.file_status IN (11,12,13) ";
    }
    else if (status == "fail")
    {
        whereStatus = @"
    AND h.file_status IN (
        91,92,999,915,
        901,902,903,904,
        911,912,913,914,
        921,922,923
    )";
    }

    // ✅ q 條件（拿掉 UPPER/ISNULL，CI DB 可直接比）
    //    - prefix: @qPrefix = 'ABC%'
    //    - contains: @qLike = '%ABC%'
    string whereQIdFilter = "";
    if (!string.IsNullOrWhiteSpace(q))
    {
        string matchOp = containsMode ? "@qLike" : "@qPrefix";

        whereQIdFilter = $@"
AND (
    (h.file_type = 'CM' AND EXISTS (
        SELECT 1
        FROM dbo.CMData cm
        WHERE cm.id = h.file_id
          AND (cm.UserBit LIKE {matchOp} OR cm.filename LIKE {matchOp})
    ))
    OR
    ((h.file_type IS NULL OR h.file_type <> 'CM') AND EXISTS (
        SELECT 1
        FROM dbo.FileData fd
        WHERE fd.id = h.file_id
          AND (fd.UserBit LIKE {matchOp} OR fd.filename LIKE {matchOp})
    ))
)";
    }

    // ✅ baseWhere：只用 History + Storage（為了 group filter）
    //    這段要讓 (update_time DESC, id DESC) 索引最好吃
    var baseWhereForIds = @"
WHERE h.file_status IN (
    11,12,13,
    901,902,903,904,
    91,92,999,915,
    911,912,913,914,
    921,922,923
)
AND (
    @group IS NULL OR @group = 'all'
    OR s_from.set_group = @group
    OR s_from.id IS NULL
)
AND (@from IS NULL OR h.update_time >= @from)
AND (@to   IS NULL OR h.update_time <  @to)
";

    // 1) COUNT：不 JOIN CM/FileData（但 q 需要用 EXISTS 過濾）
    var sqlCount = $@"
SELECT COUNT(1)
FROM dbo.FileData_History h 
LEFT JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
{baseWhereForIds}
{whereStatus}
{whereQIdFilter};
";

    // 2) PageIds：只拿本頁 h.id（重點：ORDER BY 用索引避免 Sort/TempDB）
    var sqlPageIds = $@"
SELECT h.id
FROM dbo.FileData_History h
LEFT JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
{baseWhereForIds}
{whereStatus}
{whereQIdFilter}
ORDER BY h.update_time DESC, h.id DESC
OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
";

    // 3) Rows：用 id 再 JOIN 拿明細，最後 ORDER BY 保序
    var sqlRows = @"
SELECT
    h.id                 AS HistoryId,
    h.file_id            AS FileId,

    COALESCE(
        CASE WHEN h.file_type = 'CM' THEN cm.filename ELSE f.filename END,
        CONCAT('(deleted file_id=', h.file_id, ')')
    ) AS FileName,

    CASE WHEN h.file_type = 'CM' THEN cm.UserBit ELSE f.UserBit END AS UserBit,
    CASE WHEN h.file_type = 'CM' THEN cm.extension ELSE f.extension END AS Extension,
/*
    CAST(
        COALESCE(
            CASE WHEN h.file_type = 'CM' THEN cm.filesize_7F ELSE f.filesize_7F END,
            CASE WHEN h.file_type = 'CM' THEN cm.filesize_4F ELSE f.filesize_4F END,
            0
        ) AS BIGINT
    ) AS FileSize,
*/
    h.from_storage_id     AS FromStorageId,
    s_from.storage_name   AS FromName,
    s_from.location       AS FromPath,

    h.to_storage_id       AS ToStorageId,
    s_to.storage_name     AS ToName,
    s_to.location         AS ToPath,

    u.username            AS RequestedBy,
    h.action              AS Action,
    h.create_time         AS CreateTime,
    h.update_time         AS UpdateTime,
    h.assigned_node       AS AssignedNode,

    s_from.[type]         AS FromType,
    s_to.[type]           AS ToType,
    h.note                AS Note,
    h.file_type           AS FileType,
    CAST(h.file_status AS int) AS Status

FROM dbo.FileData_History h
LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND h.file_type = 'CM'
LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR h.file_type <> 'CM')
LEFT JOIN dbo.Storage  s_from ON s_from.id = h.from_storage_id
LEFT JOIN dbo.Storage  s_to   ON s_to.id   = h.to_storage_id
LEFT JOIN dbo.UserData u      ON u.id      = h.user_id
WHERE h.id IN @ids
ORDER BY h.update_time DESC, h.id DESC;
";

    var args = new
    {
        group,
        take,
        offset,
        from,
        to,
        qPrefix = q == null ? null : $"{q}%",
        qLike   = q == null ? null : $"%{q}%"
    };

    using var conn = _factory.Create();

    // ✅ History 查詢不要 5 秒（你現在會爆就是因為這裡）
    const int timeout = 10;

    // COUNT
    var total = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(sqlCount, args, cancellationToken: ct, commandTimeout: timeout));

    // Page IDs
    var ids = (await conn.QueryAsync<int>(
        new CommandDefinition(sqlPageIds, args, cancellationToken: ct, commandTimeout: timeout))).ToList();

    if (ids.Count == 0)
        return (total, new List<HistoryRow>());

    // Rows
    var rows = (await conn.QueryAsync<HistoryRow>(
        new CommandDefinition(sqlRows, new { ids }, cancellationToken: ct, commandTimeout: timeout))).ToList();

    return (total, rows);
}


//    public async Task<bool> MarkRemovedAsync(int historyId, CancellationToken ct)
//     {
//         using var conn = _factory.Create();

//         const string sql = @"
//     UPDATE dbo.FileData_History
//     SET file_status = 111,
//         update_time = GETDATE()
//     WHERE id = @historyId;
//     SELECT @@ROWCOUNT;
//     ";
//         var rows = await conn.ExecuteScalarAsync<int>(
//             new CommandDefinition(sql, new { historyId }, cancellationToken: ct,commandTimeout: 10));

//         return rows > 0;
//     }

public async Task<bool> MarkRemovedAsync(int historyId, CancellationToken ct)
{
    using var conn = _factory.Create();

    const string sql = @"
DECLARE @action NVARCHAR(50);
DECLARE @fid INT;
DECLARE @ft NVARCHAR(50);
DECLARE @sid INT;               -- delete 影響的 storage（通常是 from_storage_id）
DECLARE @grp NVARCHAR(50);      -- Storage.set_group (4F/7F)

-- 0) 讀出這筆 history 的關鍵資訊（delete 用 from_storage_id）
SELECT
  @action = LOWER(LTRIM(RTRIM(ISNULL([action],'')))),
  @fid    = file_id,
  @ft     = file_type,
  @sid    = from_storage_id
FROM dbo.FileData_History
WHERE id = @historyId;

-- 1) 標記移除（111）+ 清掉 assigned_node（建議）
UPDATE dbo.FileData_History
SET file_status = 111,
    assigned_node = NULL,
    update_time = GETDATE()
WHERE id = @historyId;

DECLARE @rows INT = @@ROWCOUNT;

-- 2) 只有 delete 才做回補
IF (@rows > 0 AND @action = 'delete')
BEGIN
    -- 2-1) 先確認：這個 file 在「這個 storage」仍有 mapping（表示檔案仍被視為存在）
    IF EXISTS (
        SELECT 1
        FROM dbo.FileData_Storage s
        WHERE s.file_id = @fid
          AND s.storage_id = @sid
          AND (
                (@ft IS NULL AND s.file_type IS NULL)
                OR (s.file_type = @ft)
              )
    )
    BEGIN
        -- 2-2) 查這個 storage 屬於哪一層（用 Storage.set_group）
        SELECT @grp = UPPER(LTRIM(RTRIM(ISNULL(set_group,''))))
        FROM dbo.Storage
        WHERE id = @sid;

        -- 2-3) 依樓層回補 is_file（只補一邊）
        IF (UPPER(ISNULL(@ft,'')) = 'CM')
        BEGIN
            IF (@grp = '4F')
                UPDATE dbo.CMData SET is_file_4F = 'Y' WHERE id = @fid;
            ELSE IF (@grp = '7F')
                UPDATE dbo.CMData SET is_file_7F = 'Y' WHERE id = @fid;
        END
        ELSE
        BEGIN
            IF (@grp = '4F')
                UPDATE dbo.FileData SET is_file_4F = 'Y' WHERE id = @fid;
            ELSE IF (@grp = '7F')
                UPDATE dbo.FileData SET is_file_7F = 'Y' WHERE id = @fid;
        END
    END
END

SELECT @rows;
";

    var rows = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(sql, new { historyId }, cancellationToken: ct, commandTimeout: 10));

    return rows > 0;
}

        /// <summary>
        /// slot 版：領取一筆刪除任務
        /// </summary>
// HistoryRepository.cs

        public async Task<HistoryTask?> ClaimDeleteTopOneAsync(int retryMinutes, string? group, CancellationToken ct)
        {
            using var conn = _factory.Create();
            await (conn as DbConnection)!.OpenAsync(ct);
            using var tran = (conn as DbConnection)!.BeginTransaction();
            var nodeName = _nodeName;

            try {
                // 第一步：領取「已經指派給我」的 ID
                var id = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                    ;WITH P AS (
                        SELECT TOP (1) h.id
                        FROM dbo.FileData_History h 
                        JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
                        WHERE h.action = 'delete'
                            AND (@group IS NULL OR s_from.set_group = @group)
                            AND h.file_status IN (-1, 800)
                            AND h.assigned_node = @nodeName 
                        ORDER BY ISNULL(h.priority, 1) DESC, h.create_time ASC, h.id ASC
                    )
                    UPDATE h SET h.file_status = 1, h.update_time = GETDATE()
                    OUTPUT inserted.id FROM dbo.FileData_History h JOIN P ON P.id = h.id;",
                    new { group, nodeName }, transaction: tran, cancellationToken: ct,commandTimeout: 5));

                // 💡 修正點：加入這個檢查，避免 id 為 null 時呼叫 .Value
                if (!id.HasValue) 
                {
                    tran.Commit();
                    return null;
                }

                // 第二步：確定有 ID 才查明細
                var task = await conn.QuerySingleOrDefaultAsync<HistoryTask>(new CommandDefinition(@"
                    SELECT h.id AS HistoryId, h.file_id AS FileId, h.action AS Action,
                        COALESCE(cm.filename, f.filename) AS FileName,
                        COALESCE(cm.UserBit, f.UserBit) AS UserBit,
                        COALESCE(cm.extension, f.extension) AS Extension,
                        s_from.location AS FromPath, s_from.storage_name AS FromName,
                        h.file_type AS FileType, CAST(h.file_status AS int) AS FileStatus
                    FROM dbo.FileData_History h
                    LEFT JOIN dbo.CMData cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
                    LEFT JOIN dbo.FileData f ON f.id = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
                    JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
                    WHERE h.id = @id;", 
                    new { id = id.Value }, // 現在這裡保證安全了
                    transaction: tran, cancellationToken: ct,commandTimeout: 5));

                tran.Commit();
                return task;
            } catch {
                tran.Rollback();
                throw;
            }
        }

        /// <summary>刪除成功：status='12'</summary>
        public async Task CompleteDeleteAsync(int historyId, CancellationToken ct)
        {
        for (int i = 0; i < 3; i++)
            {
            try
            {
            using var conn = _factory.Create();

            const string sql = @"
            DECLARE @now DATETIME = GETDATE();
            DECLARE @fid INT;
            DECLARE @sid INT;
            DECLARE @ft  NVARCHAR(50); 
            DECLARE @is4F CHAR(1);
            DECLARE @is7F CHAR(1);
            -- 找到 file_id 和來源 storage_id
            SELECT 
                @fid = file_id,
                @sid = from_storage_id,
                @ft  = file_type
            FROM dbo.FileData_History
            WHERE id = @historyId;


            -- 1) 更新 History：刪除成功 = 12
            UPDATE dbo.FileData_History
            SET file_status = 12,
                update_time = @now
            WHERE id = @historyId;


            -- 2) 直接移除來源 storage row（只限 delete）

                DELETE FROM dbo.FileData_Storage
                WHERE file_id = @fid
                AND storage_id = @sid
                AND (
                    (@ft IS NULL AND file_type IS NULL)
                    OR (file_type = @ft)
                );
            -- 3) 主檔標記為 -1 的條件
            --    CM：只要 git 沒有同 fid+CM 的 row，就 -1
            --    非 CM：維持原本（storage 全空 + is_file_4F/7F 都不是 Y）才 -1

            IF (
                -- ✅ CM: 只看 storage
                (UPPER(ISNULL(@ft,'')) = 'CM' AND NOT EXISTS (
                    SELECT 1
                    FROM dbo.FileData_Storage
                    WHERE file_id = @fid
                    AND UPPER(ISNULL(file_type,'')) = 'CM'
                ))
                OR
                -- ✅ 非 CM: 原本規則（同 type storage 全空 + is_file 都不是 Y）
                (UPPER(ISNULL(@ft,'')) <> 'CM'
                    AND NOT EXISTS (
                        SELECT 1
                        FROM dbo.FileData_Storage
                        WHERE file_id = @fid
                        AND (
                                (@ft IS NULL AND file_type IS NULL)
                                OR (file_type = @ft)
                            )
                    )
                    AND NOT EXISTS (
                        SELECT 1
                        FROM dbo.FileData
                        WHERE id = @fid
                        AND (ISNULL(is_file_4F,'N') = 'Y' OR ISNULL(is_file_7F,'N') = 'Y')
                    )
                )
            )
            BEGIN
                IF (UPPER(ISNULL(@ft,'')) = 'CM')
                BEGIN
                    UPDATE dbo.CMData
                    SET file_status = -1
                    WHERE id = @fid;
                END
                ELSE
                BEGIN
                    UPDATE dbo.FileData
                    SET file_status = -1
                    WHERE id = @fid;
                END
            END
            ";

            await conn.ExecuteAsync(
            new CommandDefinition(sql, new { historyId }, cancellationToken: ct,commandTimeout: 10));
            return;
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            if (i == 2) throw; // 試了三次都死結才放棄
            await Task.Delay(Random.Shared.Next(100, 500), ct); // 等一下再試
        }}
        }
    

        /// <summary>
        /// 刪除失敗：status = 92x（921/922/923）
        /// </summary>
       public async Task FailDeleteAsync(int historyId, int statusCode, string? errorMessage, CancellationToken ct)
        {
            using var conn = _factory.Create();

            const string sql = @"
            DECLARE @fid INT;
            DECLARE @ft  NVARCHAR(50);
            DECLARE @fromSid INT;
            DECLARE @fromGroup NVARCHAR(10);

            -- 1) 找出 file_id / file_type / from_storage_id / from group
            SELECT
                @fid     = h.file_id,
                @ft      = h.file_type,
                @fromSid = h.from_storage_id,
                @fromGroup = s.set_group
            FROM dbo.FileData_History h
            LEFT JOIN dbo.Storage s ON s.id = h.from_storage_id
            WHERE h.id = @historyId;

            -- 2) 更新 History：刪除失敗碼
            UPDATE dbo.FileData_History
            SET file_status = @statusCode,
                assigned_node = NULL,   
                update_time = GETDATE()
            WHERE id = @historyId;
    
            -- 3) 刪除失敗：把主表對應樓層的 is_file_* 改回 Y
            IF (UPPER(ISNULL(@ft,'')) = 'CM')
            BEGIN
                UPDATE dbo.CMData
                SET
                    is_file_4F = CASE WHEN @fromGroup = '4F' THEN 'Y' ELSE is_file_4F END,
                    is_file_7F = CASE WHEN @fromGroup = '7F' THEN 'Y' ELSE is_file_7F END
                WHERE id = @fid;
            END
            ELSE
            BEGIN
                UPDATE dbo.FileData
                SET
                    is_file_4F = CASE WHEN @fromGroup = '4F' THEN 'Y' ELSE is_file_4F END,
                    is_file_7F = CASE WHEN @fromGroup = '7F' THEN 'Y' ELSE is_file_7F END
                WHERE id = @fid;
            END
        
        ";

            await conn.ExecuteAsync(
                new CommandDefinition(sql, new { historyId, statusCode }, cancellationToken: ct,commandTimeout: 10));
        }


        /// <summary>
        /// 取得本樓層 RESTORE storage 的 id
        /// </summary>
        public async Task<int> GetRestoreStorageIdAsync(string group, CancellationToken ct)
        {
            using var conn = _factory.Create();

            var ids = (await conn.QueryAsync<int>(
                new CommandDefinition(@"
            SELECT id
            FROM dbo.Storage
            WHERE set_group = @g
            AND [type] = 'RESTORE';",
                new { g = group }, cancellationToken: ct,commandTimeout: 5))).ToList();

            if (ids.Count == 0)
                throw new InvalidOperationException($"找不到 {group} 的 RESTORE storage (type='RESTORE')");

            if (ids.Count > 1)
                throw new InvalidOperationException($"{group} 有超過一個 RESTORE，請檢查 Storage 設定");

            return ids[0];
        }

        /// <summary>
        /// 取得某個 Storage 的實際路徑 (location)
        /// </summary>
        public async Task<string> GetStorageLocationAsync(int storageId, CancellationToken ct)
        {
            using var conn = _factory.Create();

            var path = await conn.ExecuteScalarAsync<string>(
                new CommandDefinition(@"
                SELECT location 
                FROM dbo.Storage
                WHERE id = @id;",
                    new { id = storageId }, cancellationToken: ct,commandTimeout: 5));

            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException($"找不到 StorageId={storageId} 的路徑 (location)");

            return path;
        }

        public sealed class FileSizeResult
        {
            public long? size4F { get; set; }
            public long? size7F { get; set; }
        }

        public async Task<(long? size4F, long? size7F)> GetLatestFileSizesByHistoryIdAsync(int historyId, CancellationToken ct)
        {
            using var conn = _factory.Create();

            const string sql = @"
        SELECT 
        CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filesize_4F ELSE f.filesize_4F END AS size4F,
        CASE WHEN UPPER(ISNULL(h.file_type,'')) = 'CM' THEN cm.filesize_7F ELSE f.filesize_7F END AS size7F
        FROM dbo.FileData_History h
        LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
        LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
        WHERE h.id = @historyId;
        ";

    var row = await conn.QueryFirstOrDefaultAsync<FileSizeResult>(
        new CommandDefinition(sql, new { historyId }, cancellationToken: ct,commandTimeout: 5));
    Console.WriteLine($"[DEBUG] Repository 查詢結果: hid={historyId}, size4F={(row?.size4F?.ToString() ?? "null")}, size7F={(row?.size7F?.ToString() ?? "null")}");
        return (row?.size4F, row?.size7F);
    }

        /// <summary>
        /// 跨樓層搬運：階段一完成（已搬到本樓層 RESTORE），更新 file_status=14/17
        /// </summary>
        public async Task MarkPhase1DoneAsync(
            int historyId,
            int statusCode,
            CancellationToken ct)
        {
            using var conn = _factory.Create();

            const string sql = @"
        UPDATE dbo.FileData_History
        SET file_status = @statusCode,
            assigned_node = NULL,
            update_time = GETDATE()
        WHERE id = @historyId;";

            await conn.ExecuteAsync(
                new CommandDefinition(sql, new { historyId, statusCode }, cancellationToken: ct,commandTimeout: 15));
        }

        /// <summary>
        /// 重試機制：將失敗的紀錄狀態改回 0 / -1
        /// </summary>
        public async Task<bool> RetryAsync(int historyId, CancellationToken ct)
{
        using var conn = _factory.Create();

        const string sql = @"
        UPDATE h
        SET h.file_status =
            CASE
                -- ✅ Phase2 / 回遷：來源是 RESTORE，就退回 Phase1Done（14/17）
                WHEN UPPER(ISNULL(s_from.[type], '')) = 'RESTORE' THEN
                    CASE
                        WHEN UPPER(ISNULL(s_from.set_group, '')) = '4F' THEN 14
                        WHEN UPPER(ISNULL(s_from.set_group, '')) = '7F' THEN 17
                        ELSE 0
                    END

                -- ✅ 其他照原本 retry 規則
                WHEN h.action = 'delete' THEN -1
                ELSE 0
            END,
            h.assigned_node = NULL,
            h.update_time   = GETDATE()
        FROM dbo.FileData_History h
        JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
        WHERE h.id = @id
        AND h.file_status IN (
                91, 92, 999, 901, 902, 903,
                911, 912, 913, 914, 915,
                921, 922, 923
            );";

    var affected = await conn.ExecuteAsync(
        new CommandDefinition(sql, new { id = historyId }, cancellationToken: ct,commandTimeout: 10));

    return affected > 0;
}


        /// <summary>
        /// 調整單筆 History 的 priority（1～10），delta 可為 +1 / -1
        /// 回傳更新後的 priority 值
        /// </summary>
       public async Task<int?> AdjustPriorityAsync(int historyId, int delta, CancellationToken ct)
        {
            using var conn = _factory.Create();

            const string sql = @"
        ;WITH T AS (
        SELECT 
            h.id,
            CAST(ISNULL(h.priority, 1) AS int) AS oldPri
        FROM dbo.FileData_History h
        WHERE h.id = @id
        )
        UPDATE h
        SET
        priority =
            CASE
            WHEN T.oldPri + @delta < 1 THEN 1
            WHEN T.oldPri + @delta > 10 THEN 10
            ELSE T.oldPri + @delta
            END,
        update_time = GETDATE()
        OUTPUT inserted.priority
        FROM dbo.FileData_History h
        JOIN T ON T.id = h.id;
        ";

            return await conn.ExecuteScalarAsync<int?>(
                new CommandDefinition(sql, new { id = historyId, delta }, cancellationToken: ct,commandTimeout: 15));
        }


// 重啟的時候 我要撿回1->0
        // 重啟的時候，把「進行中」的工作撿回來：
// - copy 任務：1 → 0
// - delete 任務：1 → -1
           public async Task ResetRunningJobsAsync(CancellationToken ct)
            {
                using var conn = _factory.Create();

                // ✅ Master 全域 reset 不需要 nodeName
                var group = _cfg.GetValue<string>("FloorRouting:Group") ?? ""; // 4F / 7F

                const string sql = @"
            UPDATE dbo.FileData_History
            SET
                file_status =
                    CASE
                                
                        -- Delete：進行中 -> 回到待刪除
                        WHEN action = 'delete' AND file_status = 1 THEN -1

                        -- Phase1 Copy：進行中 -> 回到待搬移
                        WHEN action = 'copy'   AND file_status = 1 THEN 0
                        
                        -- Phase1 move -> 回到待搬移
                        WHEN action = 'move'   AND file_status = 1 THEN 0
                        
                        -- Phase2 Copy：進行中(2) -> 回到等待回遷(24/27)，依本機 group 決定
                       /* WHEN action = 'copy'   AND file_status = 2 THEN
                            CASE
                                WHEN @group = '7F' THEN 24
                                WHEN @group = '4F' THEN 27
                                ELSE 0  -- 保底：group 不明就回 0，避免卡死
                            END
                        */
                        ELSE file_status
                    END,
                assigned_node = NULL,
                update_time   = GETDATE()
            WHERE
                (file_status = 1   AND action IN ('copy','move','delete'))
            OR (file_status = 2   AND action IN ('copy','move'))
            
            ";

        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { group }, cancellationToken: ct,commandTimeout: 15));
    }

// ✅ 搬到 temp 成功：寫入 file_status = 3
/// <summary>
/// 搬到 temp 成功：統一寫入 file_status = 3
/// 移除舊有的 23 -> 231 邏輯，因為目前 move 流程已統一為 0 -> 1 -> 3 -> 11/13
/// </summary>
        public async Task MarkTempDoneAsync(int historyId, CancellationToken ct)
        {
            const string sql = @"
        UPDATE dbo.FileData_History
        SET file_status = 3,  -- ✅ 統一改為待收尾狀態
            update_time = GETDATE()
        WHERE id = @historyId;";

            using var conn = _factory.Create();
            await conn.ExecuteAsync(new CommandDefinition(sql, new { historyId }, cancellationToken: ct,commandTimeout: 15));
        }



        public async Task<HistoryTask?> ClaimMoveTopOneAsync(int retryMinutes, string? group, CancellationToken ct)
        {
            using var conn = _factory.Create();
            await (conn as DbConnection)!.OpenAsync(ct);
            using var tran = (conn as DbConnection)!.BeginTransaction();

            var nodeName = _nodeName;
            var useNodeFilter = !string.IsNullOrWhiteSpace(nodeName);

            try
            {
                // 🔹 第一步：標記狀態並僅取回唯一的 HistoryId
                // 這樣可以確保「號碼牌」是絕對正確的，不會被 JOIN 干擾
                const string sqlClaim = @"
                DECLARE @now DATETIME = GETDATE();

                ;WITH picked AS (
                    SELECT TOP(1) h.id
                    FROM dbo.FileData_History h 
                    JOIN dbo.Storage fs ON fs.id = h.from_storage_id
                    WHERE h.action = 'move'
                    AND h.file_status IN (0, 800)
                    AND (@group IS NULL OR @group = 'all' OR fs.set_group = @group)
                    AND (h.file_status <> 800 OR (h.update_time IS NULL OR h.update_time < DATEADD(MINUTE, -@retryMin, @now)))
                    -- 💡 改成這樣：強制要求節點匹配
                    AND h.assigned_node = @nodeName 
                    ORDER BY ISNULL(h.priority, 1) DESC, h.create_time ASC, h.id ASC
                )
                UPDATE h
                SET 
                    h.file_status    = 1,
                    h.update_time    = @now
                OUTPUT
                    inserted.id
                FROM dbo.FileData_History h
                JOIN picked p ON p.id = h.id;";
                // 💡 執行時不再需要 useNodeFilter
                var historyId = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sqlClaim,
                    new { retryMin = retryMinutes, group, nodeName }, 
                    transaction: tran, cancellationToken: ct,commandTimeout: 5));

                if (!historyId.HasValue)
                {
                    tran.Commit();
                    return null;
                }

        // 🔹 第二步：拿到確定的 ID 後，再進行組裝物件
        // 這裡使用 WHERE h.id = @id，絕對不會映射錯位
        const string sqlDetail = @"
        SELECT 
            h.id              AS HistoryId,
            h.file_id         AS FileId,
            h.from_storage_id AS FromStorageId,
            h.to_storage_id   AS ToStorageId,
            h.priority        AS Priority,
            h.file_type       AS FileType,
            h.action          AS Action,
            CAST(h.file_status AS int) AS FileStatus,

            COALESCE(cm.filename,  f.filename)  AS FileName,
            COALESCE(cm.UserBit,   f.UserBit)   AS UserBit,
            COALESCE(cm.extension, f.extension) AS Extension,

            fs.set_group      AS FromGroup,
            ts.set_group      AS ToGroup,
            fs.storage_name   AS FromName,
            fs.[type]         AS FromType,
            fs.location       AS FromPath,
            ts.storage_name   AS ToName,
            ts.[type]         AS ToType,
            ts.location       AS ToPath,

            CASE WHEN (cm.id IS NOT NULL OR f.id IS NOT NULL) THEN 1 ELSE 0 END AS HasFileData
        FROM dbo.FileData_History h
        JOIN dbo.Storage fs  ON fs.id = h.from_storage_id
        LEFT JOIN dbo.Storage ts ON ts.id = h.to_storage_id
        LEFT JOIN dbo.CMData   cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
        LEFT JOIN dbo.FileData f  ON f.id  = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
        WHERE h.id = @id;";

        var task = await conn.QueryFirstOrDefaultAsync<HistoryTask>(new CommandDefinition(sqlDetail,
            new { id = historyId.Value },
            transaction: tran, cancellationToken: ct,commandTimeout: 5));

        tran.Commit();
        return task;
        }
        catch (Exception)
        {
            // 發生錯誤時 Rollback，確保狀態不卡在 1
            tran.Rollback();
            throw;
        }
    }
    public async Task<int> CompleteMoveAsync(int historyId, CancellationToken ct)
        {
            for (int i = 0; i < 3; i++)
        {
            try
            {
            using var conn = _factory.Create();
            await conn.OpenAsync(ct);
            using var tx = conn.BeginTransaction();

            const string sql = @"
            DECLARE @now DATETIME = GETDATE();
            DECLARE @fid INT;
            DECLARE @ft  NVARCHAR(50);
            DECLARE @fromSid INT;
            DECLARE @toSid INT;
            DECLARE @fromType NVARCHAR(50);
            DECLARE @toType   NVARCHAR(50);

            SELECT 
                @fid      = h.file_id,
                @ft       = h.file_type,
                @fromSid  = h.from_storage_id,
                @toSid    = h.to_storage_id,
                @fromType = fs.[type],
                @toType   = ts.[type]
            --FROM dbo.FileData_History h
            FROM dbo.FileData_History h 
            JOIN dbo.Storage fs ON fs.id = h.from_storage_id
            LEFT JOIN dbo.Storage ts ON ts.id = h.to_storage_id
            WHERE h.id = @historyId;

            DECLARE @successStatus INT =
                CASE WHEN UPPER(ISNULL(@toType,'')) = 'TAPE' THEN 13 ELSE 11 END;

            -- 1) History：move 成功
            UPDATE dbo.FileData_History
            SET file_status = @successStatus,
                update_time  = @now
            WHERE id = @historyId;

            -- 2) 永遠先刪 from storage
            DELETE FROM dbo.FileData_Storage
            WHERE file_id    = @fid
            AND storage_id = @fromSid
            AND (
                    (@ft IS NULL AND file_type IS NULL)
                    OR (file_type = @ft)
                );

            -- 3/4/5/6) ToType != TAPE 才做 upsert + 主表同步 + tape_id 規則
            IF (UPPER(ISNULL(@toType,'')) <> 'TAPE')
            BEGIN
                -- upsert to storage
                UPDATE s
                SET s.create_time = @now,
                    s.file_status = @successStatus
                FROM dbo.FileData_Storage s
                WHERE s.file_id    = @fid
                AND s.storage_id = @toSid
                AND (
                        (@ft IS NULL AND s.file_type IS NULL)
                        OR (s.file_type = @ft)
                    );

                IF (@@ROWCOUNT = 0)
                BEGIN
                    INSERT INTO dbo.FileData_Storage (file_id, storage_id, file_type, create_time, file_status)
                    VALUES (@fid, @toSid, @ft, @now, @successStatus);
                END;

                -- 主表同步
                IF (UPPER(ISNULL(@ft,'')) = 'CM')
                BEGIN
                    UPDATE dbo.CMData
                    SET file_status = @successStatus
                    WHERE id = @fid;
                END
                ELSE
                BEGIN
                    UPDATE dbo.FileData
                    SET file_status = @successStatus
                    WHERE id = @fid;
                END;

                -- FromType=TAPE && ToType!=TAPE → tape_id=-1（非 CM）
                IF (UPPER(ISNULL(@fromType,'')) = 'TAPE' AND UPPER(ISNULL(@ft,'')) <> 'CM')
                BEGIN
                    UPDATE dbo.FileData
                    SET tape_id = -1
                    WHERE id = @fid;
                END;
            END;

            -- ✅ 一定要回傳
            SELECT @successStatus AS [status];
            ";
        
            var status = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, new { historyId }, transaction: tx, cancellationToken: ct,commandTimeout: 15));

            tx.Commit();
            return status;
        }
            catch (SqlException ex) when (ex.Number == 1205)
        {
            if (i == 2) throw; // 試了三次都死結才放棄
            await Task.Delay(Random.Shared.Next(100, 500), ct); // 等一下再試
        }   
        }

            // 💡 編譯器要求：雖然邏輯上不會執行到這，但 Task<int> 必須有最終回傳值
            return 0; 
        }


   }
    
}
