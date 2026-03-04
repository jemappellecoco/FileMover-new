using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Models.History;

namespace FileMoverWeb.Services
{
    public sealed class HistoryPoller
    {
        private readonly string _connStr;
        private readonly ILogger<HistoryPoller> _log;
        private readonly IConfiguration _cfg; 

        public HistoryPoller(IConfiguration cfg, ILogger<HistoryPoller> log)
        {
            _connStr = cfg.GetConnectionString("DefaultConnection")!;
            _log = log;
            _cfg = cfg;
            
        }

        /// <summary>
        /// 撈所有 History（預設排除 pending/dispatch 狀態）
        /// </summary>
       public async Task<List<HistoryRowDto>> GetHistoryList(CancellationToken ct)
{
    const string sql = @"
SELECT
    h.id            AS historyId,
    h.file_id       AS fileId,

    -- 節目名稱（你表格欄位叫 programName）
    COALESCE(f.filename, cm.filename, '') AS programName,

    -- 檔名(UserBit)（你舊前端用 fileName 欄顯示 UserBit）
    COALESCE(f.UserBit, cm.UserBit, '') AS fileName,

    -- Storage 顯示名稱
    sFrom.storage_name  AS sourceStorage,
    sTo.storage_name    AS destStorage,

    -- ✅ 直接把樓層(group)帶出去：前端不用再靠字串 startsWith
    sFrom.set_group AS fromGroup,
    sTo.set_group   AS toGroup,
    sFrom.[type] AS fromType,
    h.assigned_node AS assignedNode,
    h.action        AS action,

    -- 目的地類型（L1 / L2 / DOWNLOAD...）
    sTo.[type]      AS destType,
 
    h.file_status   AS status,
    h.update_time   AS updateTime
FROM dbo.FileData_History h
LEFT JOIN dbo.FileData f
       ON f.id = h.file_id AND h.file_type = 'PO'
LEFT JOIN dbo.CMData cm
       ON cm.id = h.file_id AND h.file_type = 'CM'
LEFT JOIN dbo.Storage sFrom
       ON sFrom.id = h.from_storage_id
LEFT JOIN dbo.Storage sTo
       ON sTo.id = h.to_storage_id
WHERE h.file_status NOT IN (0, -1, 1, 24, 27, 111, 213)
ORDER BY h.update_time DESC, h.id DESC;
";


  await using var conn = new SqlConnection(_connStr);

var rows = await conn.QueryAsync<HistoryRowDto>(
    new CommandDefinition(sql, cancellationToken: ct));

return rows.ToList();
}
 public async Task<List<HistoryRecentRowDto>> GetHistoryRecentList(CancellationToken ct)
{
    var group = (_cfg["FloorRouting:Group"] ?? _cfg["Cluster:Group"] ?? "")
                .Trim()
                .ToUpperInvariant();

            // ✅ 防呆：只接受 4F/7F（避免 config 打錯變空，SQL 變全撈）
            if (group != "4F" && group != "7F")
            {
                _log.LogWarning("[HistoryRecent] invalid FloorRouting:Group='{group}', fallback to ALL", group);
                group = "ALL";
            }
    const string sql = @"
SELECT TOP 200
    h.id            AS historyId,
    h.file_id       AS fileId,

    -- 節目名稱（你表格欄位叫 programName）
    COALESCE(f.filename, cm.filename, '') AS programName,

    -- 檔名(UserBit)（你舊前端用 fileName 欄顯示 UserBit）
    COALESCE(f.UserBit, cm.UserBit, '') AS fileName,

    -- Storage 顯示名稱
    sFrom.storage_name  AS sourceStorage,
    sTo.storage_name    AS destStorage,

    -- ✅ 直接把樓層(group)帶出去：前端不用再靠字串 startsWith
    sFrom.set_group AS fromGroup,
    sTo.set_group   AS toGroup,
    sFrom.[type] AS fromType,
    h.assigned_node AS assignedNode,
    h.action        AS action,

    -- 目的地類型（L1 / L2 / DOWNLOAD...）
    sTo.[type]      AS destType,
 
    h.file_status   AS status,
    h.update_time   AS updateTime
FROM dbo.FileData_History h
LEFT JOIN dbo.FileData f
       ON f.id = h.file_id AND h.file_type = 'PO'
LEFT JOIN dbo.CMData cm
       ON cm.id = h.file_id AND h.file_type = 'CM'
LEFT JOIN dbo.Storage sFrom
       ON sFrom.id = h.from_storage_id
LEFT JOIN dbo.Storage sTo
       ON sTo.id = h.to_storage_id
WHERE h.file_status NOT IN (0, -1, 1, 24, 27, 111, 213)
AND (@group = 'ALL' OR sFrom.set_group = @group) 
ORDER BY h.update_time DESC, h.id DESC;
";


  await using var conn = new SqlConnection(_connStr);

var rows = await conn.QueryAsync<HistoryRecentRowDto>(
                new CommandDefinition(sql, new { group }, cancellationToken: ct));

return rows.ToList();
}
    }
    
    }