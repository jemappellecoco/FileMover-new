using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Json;
using FileMoverWeb.Services;
using FileMoverWeb.Models;

namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("jobs")]
    public sealed class JobsController : ControllerBase
    {
        private readonly TaskPoller _poller;
        private readonly IConfiguration _cfg;
        private readonly NodeRuntimeRegistry _registry;
        private readonly ILogger<JobsController> _log;
        private readonly IHttpClientFactory _http;

        public JobsController(
            TaskPoller poller,
            IConfiguration cfg,
            NodeRuntimeRegistry registry,
            ILogger<JobsController> log,
            IHttpClientFactory http)
        {
            _poller = poller;
            _cfg = cfg;
            _registry = registry;
            _log = log;
            _http = http;
        }

        // GET /jobs/pending  (純列出給 UI)
        [HttpGet("pending")]
        public async Task<IActionResult> Pending(CancellationToken ct = default)
        {
            var group = (_cfg["Cluster:Group"] ?? "").Trim();
            var rows = await _poller.GetPendingUIAsync(group,ct);
            // _log.LogInformation("[PENDING] returned {count} rows", rows.Count);
            return Ok(rows);
        }

      

        private bool IsMaster()
            => string.Equals((_cfg["Cluster:Role"] ?? "").Trim(), "Master", StringComparison.OrdinalIgnoreCase);
    }
}