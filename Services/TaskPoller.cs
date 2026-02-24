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

        public async Task<List<HistoryTask>> GetPendingTasksAsync(int maxCount, CancellationToken ct)
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

    -- Storage 欄位
    sFrom.storage_name   AS FromStorageName,
    sTo.storage_name     AS ToStorageName,
    sFrom.location       AS FromLocation,
    sTo.location         AS ToLocation,
    sFrom.[type]         AS FromType,
    sTo.[type]           AS ToType

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
                    ToType = row.ToType
                };

                // 在 C# 做多型欄位判斷
                if (task.filetype == "PO")
                {
                    task.UserBit = row.f_UserBit;
                    task.FileName = row.f_FileName;
                    task.Extension = row.f_Extension;
                    task.FileStatus = row.f_FileStatus;
                }
                else if (task.filetype == "CM")
                {
                    task.UserBit = row.cm_UserBit;
                    task.FileName = row.cm_FileName;
                    task.Extension = row.cm_Extension;
                    task.FileStatus = row.cm_FileStatus;
                }

                return task;
            }).ToList();
        }
    }
}