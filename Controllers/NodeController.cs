using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using FileMoverWeb.Services;

namespace FileMoverWeb.Controllers;

[ApiController]
[Route("api/nodes")]
public sealed class NodesController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly NodeRuntimeRegistry _registry;

    public NodesController(IConfiguration cfg, NodeRuntimeRegistry registry)
    {
        _cfg = cfg;
        _registry = registry;
    }

    // GET /api/nodes
    [HttpGet]
    public IActionResult List()
    {
        if (!IsMaster()) return Forbid();

        var timeoutSec = int.TryParse(_cfg["Cluster:HeartbeatTimeoutSeconds"], out var v) ? v : 30;
        return Ok(_registry.ListSnapshot(timeoutSec));
    }

    // POST /api/nodes/heartbeat
    [HttpPost("heartbeat-free")]
    public IActionResult HeartbeatFree([FromBody] NodeFreeReportDto dto)
    {
        if (!IsMaster()) return Forbid();
        if (string.IsNullOrWhiteSpace(dto.Node))
            return BadRequest(new { error = "node is required" });

        _registry.UpsertFree(dto);
        return Ok(new { ok = true });
    }

    private bool IsMaster()
        => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);
}