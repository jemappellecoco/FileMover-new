// NodeController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Controllers;

[ApiController]
[Route("api/nodes")]
public sealed class NodeDispatchController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<NodeDispatchController> _log;
    private readonly NodeRuntimeRegistry _registry;

    public NodeDispatchController(IConfiguration cfg, ILogger<NodeDispatchController> log, NodeRuntimeRegistry registry)
    {
        _cfg = cfg;
        _log = log;
        _registry = registry;
    }

    // POST /api/nodes/free
    [HttpPost("free")]
    public IActionResult NodeFree([FromBody] NodeFreeDto dto)
    {
        if (!IsMaster()) return Forbid();

        if (string.IsNullOrWhiteSpace(dto.Node))
            return BadRequest(new { error = "node is required" });

        var free = dto.FreeSlots < 0 ? 0 : dto.FreeSlots;

        // 記錄 node 目前空位（之後你要派工，就看這裡）
        _registry.SetFree(dto.Node.Trim(), free);

        _log.LogInformation("[NODE_FREE] node={node} freeSlots={free}", dto.Node, free);

        return Ok(new
        {
            ok = true,
            node = dto.Node,
            freeSlots = free
        });
    }

    private bool IsMaster()
        => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);
}

public sealed class NodeFreeDto
{
    public string? Node { get; set; }

    // ✅ node 告訴 master：我現在還能接幾個任務（slot 空位）
    // 你之後要跟前端 maxConcurrency 串，也完全沒衝突：
    // node 自己算 freeSlots = maxConcurrency - currentRunning 然後丟上來
    public int FreeSlots { get; set; } = 1;
}