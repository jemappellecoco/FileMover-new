using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using FileMoverWeb.Services;
using FileMoverWeb.Models.Progress;
using System.IO;
using System.Threading.Channels;
namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("api/progress")]
    public sealed class ProgressController : ControllerBase
    {
        private readonly ProgressHub _hub;
        private readonly IConfiguration _cfg;

        public ProgressController(ProgressHub hub, IConfiguration cfg)
        {
            _hub = hub;
            _cfg = cfg;
        }

        // POST /api/progress/report  (Slave -> Master)
        [HttpPost("report")]
        public IActionResult Report([FromBody] ProgressReportDto dto)
        {
            if (!IsMaster()) return Forbid();
            if (dto == null) return BadRequest(new { error = "body is required" });
            if (dto.HistoryId <= 0) return BadRequest(new { error = "historyId is required" });

            var ev = _hub.Publish(dto);
            return Ok(new { ok = true, ev });
        }

        // GET /api/progress/snapshot  (可選：UI 初次載入抓一次)
        [HttpGet("snapshot")]
        public IActionResult Snapshot()
        {
            // 你想限制只有 Master 才能看就打開：
            // if (!IsMaster()) return Forbid();

            return Ok(_hub.Snapshot());
        }

        // GET /api/progress/events  (UI SSE)
        [HttpGet("events")]
        public async Task Events(CancellationToken ct)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            // 如果有 nginx / 反代，避免 buffer（可留可不留）
            Response.Headers["X-Accel-Buffering"] = "no";

            var reader = _hub.Subscribe(out var subId);

            try
            {
                // 先送 hello（可選，方便你前端知道連上了）
                await WriteSseAsync("hello", new { ok = true, ts = DateTimeOffset.UtcNow }, ct);

                while (!ct.IsCancellationRequested)
                {
                    var ev = await reader.ReadAsync(ct);

                    // ✅ 這行很重要：event: progress
                    // 你前端 addEventListener('progress') 才收得到
                    await WriteSseAsync("progress", ev, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
            finally
            {
                _hub.Unsubscribe(subId);
            }
        }

        private async Task WriteSseAsync(string eventName, object data, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await Response.WriteAsync($"event: {eventName}\n", ct);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        private bool IsMaster()
            => string.Equals(_cfg["Cluster:Role"], "Master", StringComparison.OrdinalIgnoreCase);
    }
}