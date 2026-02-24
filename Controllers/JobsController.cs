// JobsController.cs
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;
using FileMoverWeb.Services;

namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("jobs")]
    public sealed class JobsController : ControllerBase
    {
        private readonly TaskPoller _poller;

        public JobsController(TaskPoller poller)
        {
            _poller = poller;
        }

        // GET /jobs/pending?take=200
        [HttpGet("pending")]
        public async Task<IActionResult> Pending([FromQuery] int take = 200, CancellationToken ct = default)
        {
           
            
            var rows = await _poller.GetPendingTasksAsync(take, ct);
            return Ok(rows);
        }
    }
}