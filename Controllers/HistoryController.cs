using Microsoft.AspNetCore.Mvc;
using FileMoverWeb.Services;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("history")]
    public sealed class HistoryController : ControllerBase
    {
        private readonly HistoryPoller _poller;
        private readonly IConfiguration _cfg;

        public HistoryController(HistoryPoller poller,IConfiguration cfg)
        {
            _poller = poller;
            _cfg = cfg;
        }
        public sealed class RemoveReq
    {
        public int HistoryId { get; set; }
        public string? Note { get; set; }
    }

        // GET /history
        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var rows = await _poller.GetHistoryList(ct);

            return Ok(new
            {
                total = rows.Count,
                page = 1,
                take = rows.Count,
                totalPages = 1,
                rows = rows
            });
        }
    [HttpPost("{id:int}/remove")]
public async Task<IActionResult> RemoveById([FromRoute] int id, CancellationToken ct)
{
    if (id <= 0) return BadRequest(new { ok = false, message = "id invalid" });

    var connStr = _cfg.GetConnectionString("DefaultConnection")!;
    await using var conn = new SqlConnection(connStr);

    var baseModel = new FileMoverWeb.Core.BaseModel(conn);

    var patch = new Dictionary<string, object?>
    {
        ["file_status"] = 111,
        ["assigned_node"] = null,
        ["note"] = "removed by UI",
        ["update_time"] = DateTime.Now
    };

    var updated = await baseModel.UpdateAsync(
        table: "dbo.FileData_History",
        pkName: "id",
        id: id,
        data: patch,
        columnsWhitelist: new[] { "file_status", "assigned_node", "note", "update_time" },
        ct: ct);

    if (updated == 0)
        return NotFound(new { ok = false, message = "not found" });

    return Ok(new { ok = true, historyId = id, message = $"已移除 HistoryId={id}" });
}

    }
}