using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileMoverWeb.Services
{
    public interface ITaskQueue
    {
        ValueTask EnqueueAsync(HistoryTask task, CancellationToken ct = default);
        ValueTask<HistoryTask> DequeueAsync(CancellationToken ct = default);
    }

    public sealed class TaskQueue : ITaskQueue
    {
        private readonly Channel<HistoryTask> _queue;

        public TaskQueue()
        {
            _queue = Channel.CreateUnbounded<HistoryTask>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });
        }

        public ValueTask EnqueueAsync(HistoryTask task, CancellationToken ct = default)
            => _queue.Writer.WriteAsync(task, ct);

        public ValueTask<HistoryTask> DequeueAsync(CancellationToken ct = default)
            => _queue.Reader.ReadAsync(ct);
    }
}
