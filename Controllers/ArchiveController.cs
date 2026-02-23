// Controllers/ArchiveController.cs
using FileMoverWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileMoverWeb.Controllers
{
    [ApiController]
    [Route("archive")]
    public class ArchiveController : ControllerBase
    {
        private readonly HistoryRepository _repo;
        private readonly IConfiguration _cfg;

        public ArchiveController(HistoryRepository repo, IConfiguration cfg)
        {
            _repo = repo;
            _cfg = cfg;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] int take = 200,
            [FromQuery] int page = 1,
            [FromQuery] string? group = null,
            [FromQuery] string? q = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            CancellationToken ct = default)
        {
            if (take <= 0) take = 50;
            if (take > 1000) take = 1000;
            if (page <= 0) page = 1;

            // group: all/current/4F/7F...
            if (string.IsNullOrWhiteSpace(group) ||
                group.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                group = null;
            }
            else if (group.Equals("current", StringComparison.OrdinalIgnoreCase))
            {
                var g = _cfg.GetValue<string>("FloorRouting:Group");
                group = string.IsNullOrWhiteSpace(g) ? null : g;
            }

            var (total, rows) = await _repo.ListArchiveAsync(take, page, group, q, from, to, ct);

            var data = rows.Select(r =>
            {
                var actionOut = string.IsNullOrWhiteSpace(r.Action) ? "-" : r.Action.Trim();

                var ext = (r.Extension ?? "").Trim();
                if (!string.IsNullOrEmpty(ext) && !ext.StartsWith(".")) ext = "." + ext;

                var fileNameWithExt =
                    !string.IsNullOrWhiteSpace(r.UserBit)
                        ? $"{r.UserBit}{ext}"
                        : (r.FileName ?? string.Empty);

                return new
                {
                    r.HistoryId,
                    r.FileId,

                    ProgramName = r.FileName ?? r.UserBit ?? "",
                    FileName    = r.UserBit  ?? r.FileName ?? "",

                    SourceStorage = r.FromName,
                    DestStorage   = r.ToName,

                    SourcePath = (!string.IsNullOrWhiteSpace(r.FromPath) && !string.IsNullOrWhiteSpace(fileNameWithExt))
                        ? Path.Combine(r.FromPath!, fileNameWithExt)
                        : null,

                    DestPath = (!string.IsNullOrWhiteSpace(r.ToPath) && !string.IsNullOrWhiteSpace(fileNameWithExt))
                        ? Path.Combine(r.ToPath!, fileNameWithExt)
                        : null,

                    r.UpdateTime,
                    r.AssignedNode,
                    Action = actionOut,

                    Status = r.Status,            // 一定是 13
                    StatusText = "等待歸檔"
                };
            }).ToList();

            var totalPages = (int)Math.Ceiling(total / (double)take);
            if (totalPages <= 0) totalPages = 1;

            return Ok(new { page, take, total, totalPages, rows = data });
        }
        public sealed class ArchiveMarkRequest
        {
            public int[] HistoryIds { get; init; } = Array.Empty<int>();
        }

        [HttpPost("mark")]
            public async Task<IActionResult> Mark(
                [FromBody] ArchiveMarkRequest req,
                CancellationToken ct = default)
            {
                if (req?.HistoryIds == null || req.HistoryIds.Length == 0)
                    return BadRequest(new { message = "請至少選擇一筆" });

                var ids = req.HistoryIds
                    .Where(x => x > 0)
                    .Distinct()
                    .ToArray();

                if (ids.Length == 0)
                    return BadRequest(new { message = "HistoryIds 不可為空" });

                var newIds = new List<int>();

                foreach (var hid in ids)
                {
                    var newId = await _repo.CreateArchiveTaskAndMarkSourceAsync(hid, ct);
                    newIds.Add(newId);
                }

                return Ok(new
                {
                    count = newIds.Count,
                    newHistoryIds = newIds,
                    message = $"已建立 {newIds.Count} 筆歸檔任務"
                });
            }

    }
}
