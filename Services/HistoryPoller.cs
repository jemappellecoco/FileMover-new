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

        public HistoryPoller(IConfiguration cfg, ILogger<HistoryPoller> log)
        {
            _connStr = cfg.GetConnectionString("DefaultConnection")!;
            _log = log;
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
    }}