using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileMoverWeb.Services
{
    /// <summary>
    /// 統一進度輸出介面：
    /// - Master / 單機：寫到本機 IJobProgress（SSE 讀得到）
    /// - Slave：改成 HTTP 回報到 Master
    /// </summary>
    public interface IProgressSink
    {
        Task InitTotalsAsync(string jobId, Dictionary<string, long> totalsByDest, CancellationToken ct);
        Task AddCopiedAsync(string jobId, string destId, long deltaBytes, CancellationToken ct);
        Task CompleteJobAsync(string jobId, CancellationToken ct);

        Task EnsureTotalAsync(string jobId, string destId, long totalBytes, CancellationToken ct); // ⭐新增
    }
}
