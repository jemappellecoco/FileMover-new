using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileMoverWeb.Services
{
    /// <summary>
    /// 本機進度：直接呼叫 IJobProgress（你原本的 in-memory JobProgress）。
    /// </summary>
    public sealed class LocalProgressSink : IProgressSink
    {
        private readonly IJobProgress _progress;

        public LocalProgressSink(IJobProgress progress)
        {
            _progress = progress;
        }

        public Task InitTotalsAsync(string jobId, Dictionary<string, long> totalsByDest, CancellationToken ct)
        {
            _progress.InitTotals(jobId, totalsByDest);
            return Task.CompletedTask;
        }

        public Task AddCopiedAsync(string jobId, string destId, long deltaBytes, CancellationToken ct)
        {
            if (deltaBytes > 0)
                _progress.AddCopied(jobId, destId, deltaBytes);

            return Task.CompletedTask;
        }

        public Task CompleteJobAsync(string jobId, CancellationToken ct)
        {
            _progress.CompleteJob(jobId);
            return Task.CompletedTask;
        }
        public Task EnsureTotalAsync(string jobId, string destId, long totalBytes, CancellationToken ct)
        {
            if (totalBytes > 0)
                _progress.EnsureTotal(jobId, destId, totalBytes);
            return Task.CompletedTask;
        }
    }
}
