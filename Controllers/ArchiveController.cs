using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using FileMoverWeb.Services;

namespace FileMoverWeb.Controllers
{
    [ApiController]
    public sealed class ArchiveController : ControllerBase
    {
        private readonly ArchivePoller _poller;

        public ArchiveController(ArchivePoller poller) => _poller = poller;

        // GET /archive?take=200&page=1
        [HttpGet("/archive")]
        public async Task<IActionResult> Get(
            [FromQuery] int take = 200,
            [FromQuery] int page = 1,
            CancellationToken ct = default)
        {
            var res = await _poller.GetArchiveAsync(take, page, ct);
            return Ok(res);
        }

        public sealed class MarkReq
        {
            public int[] historyIds { get; set; } = System.Array.Empty<int>();
        }

        // POST /archive/mark  { "historyIds":[1,2,3] }
        [HttpPost("/archive/mark")]
        public async Task<IActionResult> Mark([FromBody] MarkReq req, CancellationToken ct = default)
        {
            var res = await _poller.MarkArchiveAndCreateMoveAsync(
                req?.historyIds ?? System.Array.Empty<int>(), ct);

            return Ok(new
            {
                ok = true,
                updated = res.updated,           // 13->213
                created = res.created,           // 新增 move 任務
                newHistoryIds = res.newHistoryIds,
                message = $"已歸檔：更新 {res.updated} 筆，新增 move 任務 {res.created} 筆"
            });
        }
    }
}