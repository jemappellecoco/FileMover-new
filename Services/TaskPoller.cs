using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Models;

namespace FileMoverWeb.Services
{
    public sealed class TaskPoller
    {
        private readonly string _connStr;
        private readonly ILogger<TaskPoller> _log;

        public TaskPoller(IConfiguration cfg, ILogger<TaskPoller> log)
        {
            _connStr = cfg.GetConnectionString("DefaultConnection")!;
            _log = log;
        }

        /// <summary>
        /// 取得所有待處理任務
        /// </summary>
        /// <param name="ct">取消 Token</param>
        /// <returns>HistoryTask 列表</returns>
        /// <remarks>
        /// 這個方法會回傳所有待處理的任務，包括 FileData 和 CMData
        /// </remarks>
        public async Task<List<HistoryTask>> GetPendingTasksAsync( CancellationToken ct)
        {
            const string sql = @"
        SELECT 
            h.id                 AS HistoryId,
            h.file_id            AS FileId,
            h.action             AS Action,
            h.priority           AS Priority,
            h.file_status        AS HistoryStatus,
            h.create_time        AS CreateTime,
            h.file_type          AS filetype,
            h.from_storage_id    AS FromStorageId,
            h.to_storage_id      AS ToStorageId,
            h.assigned_node      AS AssignedNode,

            -- FileData 欄位 (PO)
            f.UserBit            AS f_UserBit,
            f.filename           AS f_FileName,
            f.extension          AS f_Extension,
            f.file_status        AS f_FileStatus,

            -- CMData 欄位 (CM)
            cm.UserBit           AS cm_UserBit,
            cm.filename          AS cm_FileName,
            cm.extension         AS cm_Extension,
            cm.file_status       AS cm_FileStatus,
            
            -- 檔案大小
            -- FileData size (PO)
            f.filesize_4F        AS f_FileSize4F,
            f.filesize_7F        AS f_FileSize7F,

            -- CMData size (CM)
            cm.filesize_4F       AS cm_FileSize4F,
            cm.filesize_7F       AS cm_FileSize7F,
            
            -- Storage 欄位
            sFrom.storage_name   AS FromStorageName,
            sTo.storage_name     AS ToStorageName,
            sFrom.location       AS FromLocation,
            sTo.location         AS ToLocation,
            sFrom.[type]         AS FromType,
            sTo.[type]           AS ToType,
           
            sFrom.set_group      AS FromGroup,
            sTo.set_group        AS ToGroup

        FROM dbo.FileData_History h
        LEFT JOIN dbo.FileData f ON f.id = h.file_id AND h.file_type = 'PO'
        LEFT JOIN dbo.CMData cm ON cm.id = h.file_id AND h.file_type = 'CM'
        LEFT JOIN dbo.Storage sFrom ON sFrom.id = h.from_storage_id
        LEFT JOIN dbo.Storage sTo ON sTo.id = h.to_storage_id
        WHERE h.file_status IN (0, -1, 24, 27)
        ORDER BY h.priority DESC, h.create_time ASC;";

            using var conn = new SqlConnection(_connStr);

            var rawData = await conn.QueryAsync<dynamic>(
                new CommandDefinition(sql, cancellationToken: ct));

            return rawData.Select(row =>
            {
                var task = new HistoryTask
                {
                    HistoryId = row.HistoryId,
                    FileId = row.FileId,
                    Action = row.Action,
                    Priority = row.Priority,
                    HistoryStatus = row.HistoryStatus,
                    CreateTime = row.CreateTime,
                    filetype = row.filetype,
                    FromStorageId = row.FromStorageId,
                    ToStorageId = row.ToStorageId,
                    FromStorageName = row.FromStorageName, 
                    ToStorageName = row.ToStorageName,
                    AssignedNode = row.AssignedNode,
                    FromLocation = row.FromLocation,
                    ToLocation = row.ToLocation,
                    FromType = row.FromType,
                    ToType = row.ToType,
                    FromGroup = row.FromGroup,
                    ToGroup = row.ToGroup,
                };

                // 在 C# 做多型欄位判斷
                if (task.filetype == "PO")
                {
                    task.UserBit = row.f_UserBit;
                    task.FileName = row.f_FileName;
                    task.Extension = row.f_Extension;
                    task.FileStatus = row.f_FileStatus;
                    task.FileSize4F = row.f_FileSize4F;
                    task.FileSize7F = row.f_FileSize7F;
                }
                else if (task.filetype == "CM")
                {
                    task.UserBit = row.cm_UserBit;
                    task.FileName = row.cm_FileName;
                    task.Extension = row.cm_Extension;
                    task.FileStatus = row.cm_FileStatus;
                    task.FileSize4F = row.cm_FileSize4F;
                    task.FileSize7F = row.cm_FileSize7F;
                }

                return task;
            }).ToList();
        }
         public async Task<List<HistoryTask>> GetPendingUIAsync( CancellationToken ct)
        {
            const string sql = @"
        SELECT 
            h.id                 AS HistoryId,
            h.file_id            AS FileId,
            h.action             AS Action,
            h.priority           AS Priority,
            h.file_status        AS HistoryStatus,
            h.create_time        AS CreateTime,
            h.file_type          AS filetype,
            h.from_storage_id    AS FromStorageId,
            h.to_storage_id      AS ToStorageId,
            h.assigned_node      AS AssignedNode,

            -- FileData 欄位 (PO)
            f.UserBit            AS f_UserBit,
            f.filename           AS f_FileName,
            f.extension          AS f_Extension,
            f.file_status        AS f_FileStatus,

            -- CMData 欄位 (CM)
            cm.UserBit           AS cm_UserBit,
            cm.filename          AS cm_FileName,
            cm.extension         AS cm_Extension,
            cm.file_status       AS cm_FileStatus,
            
            -- 檔案大小
            -- FileData size (PO)
            f.filesize_4F        AS f_FileSize4F,
            f.filesize_7F        AS f_FileSize7F,

            -- CMData size (CM)
            cm.filesize_4F       AS cm_FileSize4F,
            cm.filesize_7F       AS cm_FileSize7F,
            
            -- Storage 欄位
            sFrom.storage_name   AS FromStorageName,
            sTo.storage_name     AS ToStorageName,
            sFrom.location       AS FromLocation,
            sTo.location         AS ToLocation,
            sFrom.[type]         AS FromType,
            sTo.[type]           AS ToType,
           
            sFrom.set_group      AS FromGroup,
            sTo.set_group        AS ToGroup

        FROM dbo.FileData_History h
        LEFT JOIN dbo.FileData f ON f.id = h.file_id AND h.file_type = 'PO'
        LEFT JOIN dbo.CMData cm ON cm.id = h.file_id AND h.file_type = 'CM'
        LEFT JOIN dbo.Storage sFrom ON sFrom.id = h.from_storage_id
        LEFT JOIN dbo.Storage sTo ON sTo.id = h.to_storage_id
        WHERE h.file_status IN (0, -1, 1, 24, 27)
        ORDER BY  
        CASE WHEN h.file_status = 1 THEN 0 ELSE 1 END,
            h.priority DESC,
            h.create_time ASC";

            using var conn = new SqlConnection(_connStr);

            var rawData = await conn.QueryAsync<dynamic>(
                new CommandDefinition(sql, cancellationToken: ct));

            return rawData.Select(row =>
            {
                var task = new HistoryTask
                {
                    HistoryId = row.HistoryId,
                    FileId = row.FileId,
                    Action = row.Action,
                    Priority = row.Priority,
                    HistoryStatus = row.HistoryStatus,
                    CreateTime = row.CreateTime,
                    filetype = row.filetype,
                    FromStorageId = row.FromStorageId,
                    ToStorageId = row.ToStorageId,
                    FromStorageName = row.FromStorageName, 
                    ToStorageName = row.ToStorageName,
                    AssignedNode = row.AssignedNode,
                    FromLocation = row.FromLocation,
                    ToLocation = row.ToLocation,
                    FromType = row.FromType,
                    ToType = row.ToType,
                    FromGroup = row.FromGroup,
                    ToGroup = row.ToGroup,
                };

                // 在 C# 做多型欄位判斷
                if (task.filetype == "PO")
                {
                    task.UserBit = row.f_UserBit;
                    task.FileName = row.f_FileName;
                    task.Extension = row.f_Extension;
                    task.FileStatus = row.f_FileStatus;
                    task.FileSize4F = row.f_FileSize4F;
                    task.FileSize7F = row.f_FileSize7F;
                }
                else if (task.filetype == "CM")
                {
                    task.UserBit = row.cm_UserBit;
                    task.FileName = row.cm_FileName;
                    task.Extension = row.cm_Extension;
                    task.FileStatus = row.cm_FileStatus;
                    task.FileSize4F = row.cm_FileSize4F;
                    task.FileSize7F = row.cm_FileSize7F;
                }

                return task;
            }).ToList();
        }
      
        public async Task<bool> TryReserveAsync(int historyId, string nodeName, CancellationToken ct)
        {
            const string sql = @"
        UPDATE dbo.FileData_History
        SET assigned_node = @nodeName,
            update_time = GETDATE()
        WHERE id = @historyId
        AND file_status IN (0, -1, 24, 27)
        AND (assigned_node IS NULL OR assigned_node = '');

        SELECT @@ROWCOUNT;";

            using var conn = new SqlConnection(_connStr);
            var rows = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, new { historyId, nodeName }, cancellationToken: ct));
            return rows == 1;
        }

       

        public async Task ClearReserveAsync(int historyId, string nodeName, CancellationToken ct)
        {
            const string sql = @"
        UPDATE dbo.FileData_History
        SET assigned_node = NULL,
            update_time = GETDATE()
        WHERE id = @historyId
        AND assigned_node = @nodeName
        AND file_status IN (0, -1, 24, 27);";

            using var conn = new SqlConnection(_connStr);
            await conn.ExecuteAsync(
                new CommandDefinition(sql, new { historyId, nodeName }, cancellationToken: ct));
        }

        public async Task<List<HistoryTask>> DispatchFullAsync(
        string nodeName,
        int take,
        CancellationToken ct)
    {
        // 1️⃣ 先撈 pending（完整資料）
        var pending = await GetPendingTasksAsync( ct);

        var result = new List<HistoryTask>();

        foreach (var task in pending)
        {
            if (result.Count >= take)
                break;

            // 2️⃣ 嘗試 claim
            var ok = await TryReserveAsync(task.HistoryId, nodeName, ct);

            if (!ok)
                continue; // 被別台搶走

            // // 3️⃣ 更新記憶體物件
            // task.HistoryStatus = 1;
            task.AssignedNode = nodeName;

            result.Add(task);
        }

        return result;
    }
    }
}