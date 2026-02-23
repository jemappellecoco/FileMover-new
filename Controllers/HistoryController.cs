// Controllers/HistoryController.cs
using FileMoverWeb.Services;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration; 
namespace FileMoverWeb.Controllers
{
   // Controllers/HistoryController.cs
    [ApiController]
    [Route("history")]
    public class HistoryController : ControllerBase
    {
        private readonly HistoryRepository _repo;
        private readonly ICancelStore _cancelStore; 
        private readonly IConfiguration _cfg; 
        private readonly IMoveRetryStore _retryStore;

         public HistoryController(
            HistoryRepository repo,
            ICancelStore cancelStore,
            IConfiguration cfg,         // ⭐ DI 進來
             IMoveRetryStore retryStore 
        )
        {
            _repo = repo;
            _cancelStore = cancelStore;
            _cfg = cfg;
            _retryStore = retryStore;
        }

        
        // 只顯示成功/失敗（預設 200 筆，可用 take 覆寫）
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] string status = "all",
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
            
            // ⭐ group 解釋：
            //   - 沒給 / all  → 全部樓層（傳 null 給 repo）
            //   - current     → 用 FloorRouting:Group（這台代表的樓層）
            //   - 4F / 7F...  → 直接照字串傳給 repo
            if (string.IsNullOrWhiteSpace(group) ||
                group.Equals("all", System.StringComparison.OrdinalIgnoreCase))
            {
                group = null;   // 全部
            }
            else if (group.Equals("current", System.StringComparison.OrdinalIgnoreCase))
            {
                var g = _cfg.GetValue<string>("FloorRouting:Group");
                group = string.IsNullOrWhiteSpace(g) ? null : g;
            }
            // 這裡 status = all 時，repo 會只撈 10/90（見上方 whereStatus）
           var (total, rows) = await _repo.ListHistoryAsync(status, take, page, group, q,from, to,ct);
           
    
                var data = rows.Select(r =>
                     {
                var actionRaw = (r.Action ?? "").Trim();
                var actionOut = string.IsNullOrWhiteSpace(actionRaw) ? "-" : actionRaw;
                var ext = (r.Extension ?? "").Trim();
                if (!string.IsNullOrEmpty(ext) && !ext.StartsWith(".")) ext = "." + ext;
                var fileNameWithExt =
                !string.IsNullOrWhiteSpace(r.UserBit)
                    ? $"{r.UserBit}{ext}"          // ⭐你目前習慣用 UserBit 當檔名
                    : (r.FileName ?? string.Empty); // fallback（真的沒 UserBit 才用 FileName）

                return new
                {
                    r.HistoryId,
                    r.FileId,

                    ProgramName =
                        r.FileId == 0
                            ? (r.Note ?? string.Empty)
                            : (r.FileName ?? r.UserBit ?? string.Empty),
                    FileName =
                        r.FileId == 0
                            ? (r.Note ?? string.Empty)     // ✅ 只顯示 UB / note
                            : (r.UserBit ?? r.FileName ?? string.Empty),

                    SourceStorage = r.FromName,
                    DestStorage   = r.ToName,
                    DestType = r.ToType,

                     SourcePath = (!string.IsNullOrWhiteSpace(r.FromPath) && !string.IsNullOrWhiteSpace(fileNameWithExt))
                        ? Path.Combine(r.FromPath!, fileNameWithExt)
                        : null,
                    DestPath   = (!string.IsNullOrWhiteSpace(r.ToPath) && !string.IsNullOrWhiteSpace(fileNameWithExt))
                                    ? Path.Combine(r.ToPath!, fileNameWithExt)
                                    : null,

                    r.FromStorageId,
                    r.ToStorageId,
                    r.UpdateTime,
                    AssignedNode = r.AssignedNode,

                    Action = actionOut,

                    Status = r.Status,
                    StatusText = r.Status switch
                    {
                        11  => "搬移成功",
                        12  => "刪除成功",
                        91  => "搬移失敗（其他）",
                        92  => "刪除失敗（其他）",
                        14 or 17 => "等待回遷",
                        911 => "搬移失敗－找不到來源/檔案不存在",
                        912 => "搬移失敗－檔案使用中",
                        913 => "搬移失敗－權限不足",
                        914 => "搬移失敗－找不到目的地",
                        915 => "搬移失敗－檔案大小錯誤",
                        921 => "刪除失敗－找不到來源/檔案不存在",
                        922 => "刪除失敗－檔案使用中",
                        923 => "刪除失敗－權限不足",
                        999 => "使用者取消",
                        901 => "資料庫錯誤[From]",
                        902 => "資料庫錯誤[To]",
                        903 => "未設定restore錯誤",
                        904 => "排程失敗",
                        13 => "等待歸檔",
                        _   => r.Status.ToString()
                    }
                };
            }).ToList();

            var totalPages = (int)System.Math.Ceiling(total / (double)take);
            if (totalPages <= 0) totalPages = 1;

            return Ok(new
            {
                page,
                take,
                total,
                totalPages,
                rows = data
            });
        }
        [HttpPost("{id:int}/remove")]
        public async Task<IActionResult> Remove(int id, CancellationToken ct)
        {
            var ok = await _repo.MarkRemovedAsync(id, ct);   // status=111

            if (!ok)
                return NotFound(new { historyId = id, message = $"移除失敗：找不到 HistoryId={id}" });

            _cancelStore.Clear(id);
            return Ok(new { historyId = id, message = $"已移除 HistoryId={id} " });
        }

        [HttpPost("{id:int}/retry")]
            public async Task<IActionResult> Retry(int id, CancellationToken ct)
            {
                var ok = await _repo.RetryAsync(id, ct);
                if (!ok)
                {
                    return BadRequest(new
                    {
                        message = "此筆紀錄目前不能（可能不是錯誤狀態或已被處理）。"
                    });
                }
                 _cancelStore.Clear(id);
                  _retryStore.Clear(id);
                return Ok(new
                {
                    historyId = id,
                    message = "已將此筆任務重新排入佇列。"
                });
            }          
    }
    

}
