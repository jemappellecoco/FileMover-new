using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using FileMoverWeb.Services;
using FileMoverWeb.Models.Node;
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

    public sealed class UpdateConcurrencyDto
    {
        public int? MaxConcurrency { get; set; }
    }

    // PUT /api/nodes/{nodeName}/concurrency
    [HttpPut("{nodeName}/concurrency")]
    public IActionResult UpdateConcurrency(string nodeName, [FromBody] UpdateConcurrencyDto dto)
    {
        if (!IsMaster()) return Forbid();
        if (string.IsNullOrWhiteSpace(nodeName)) return BadRequest(new { error = "nodeName is required" });

        var v = dto?.MaxConcurrency;
        if (v.HasValue && v.Value <= 0)
            return BadRequest(new { error = "maxConcurrency must be > 0 (or null to clear override)" });

        _registry.SetAdminMax(nodeName.Trim(), v);
        return Ok(new { ok = true, node = nodeName, maxConcurrency = v });
    }

    private bool IsMaster()
        => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);


}
