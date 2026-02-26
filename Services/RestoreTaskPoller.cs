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
    public sealed class RestoreTaskPoller
    {
        private readonly string _connStr;
        private readonly ILogger<RestoreTaskPoller> _log;

        public RestoreTaskPoller(IConfiguration cfg, ILogger<RestoreTaskPoller> log)
        {
            _connStr = cfg.GetConnectionString("DefaultConnection")!;
            _log = log;
        }

public async Task<List<Phase2Row>> ListPhase2PendingAsync(CancellationToken ct)
{
    const string sql = @"
SELECT
    h.id              AS HistoryId,
    h.file_id         AS FileId,
    h.file_status     AS FileStatus,
    h.file_type       AS filetype,

    sFrom.storage_name AS FromName,
    sTo.storage_name   AS ToName,
    sFrom.set_group    AS FromGroup,
    sTo.set_group      AS ToGroup,

    -- PO
    f.UserBit          AS f_UserBit,
    f.filename         AS f_FileName,
    t.tape_no          AS f_TapeNo,
    t.tape_bak_no      AS f_TapeBakNo

FROM dbo.FileData_History h
LEFT JOIN dbo.Storage sFrom ON sFrom.id = h.from_storage_id
LEFT JOIN dbo.Storage sTo   ON sTo.id   = h.to_storage_id

LEFT JOIN dbo.FileData f    ON f.id = h.file_id AND h.file_type = 'PO'
LEFT JOIN dbo.TapeInfo t    ON t.id = f.tape_id



WHERE h.file_status IN (14,17)
ORDER BY h.create_time ASC;
";

    using var conn = new SqlConnection(_connStr);

    var raw = await conn.QueryAsync<dynamic>(
        new CommandDefinition(sql, cancellationToken: ct));

    return raw.Select(r =>
    {
        var row = new Phase2Row
        {
            HistoryId = r.HistoryId,
            FileId = r.FileId,
            FileStatus = r.FileStatus,
            filetype = r.filetype,

            FromName = r.FromName,
            ToName = r.ToName,
            FromGroup = r.FromGroup,
            ToGroup = r.ToGroup,
        };

        if (row.filetype == "PO")
        {
            row.UserBit = r.f_UserBit;
            row.FileName = r.f_FileName;
            row.TapeNo = r.f_TapeNo;
            row.TapeBakNo = r.f_TapeBakNo;
        }
        else if (row.filetype == "CM")
        {
            row.UserBit = r.cm_UserBit;
            row.FileName = r.cm_FileName;
            row.TapeNo = r.cm_TapeNo;
            row.TapeBakNo = r.cm_TapeBakNo;
        }

        return row;
    }).ToList();
}
    }
}