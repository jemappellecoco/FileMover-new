// using System.Collections.Concurrent;

// public interface ICancelStore
// {
//     bool ShouldCancel(int historyId);
//     void Cancel(int historyId);
//     void Clear(int historyId);
// }

// public class CancelStore : ICancelStore
// {
//     private readonly ConcurrentDictionary<int, bool> _canceled = new();

//     public bool ShouldCancel(int historyId)
//         => _canceled.ContainsKey(historyId);
    
//     public void Cancel(int historyId)
//         => _canceled[historyId] = true;

//     public void Clear(int historyId)
//         => _canceled.TryRemove(historyId, out _);
// }



using System.Collections.Concurrent;

namespace FileMoverWeb.Services
{
    public interface ICancelStore
    {
        bool ShouldCancel(int historyId);
        void Cancel(int historyId);
        void Clear(int historyId);
    }

    public class CancelStore : ICancelStore
    {
        private readonly ConcurrentDictionary<int, bool> _canceled = new();

        public bool ShouldCancel(int historyId)
        {
            // 如果字典裡有這個 ID，表示要取消
            return _canceled.ContainsKey(historyId);
        }

        public void Cancel(int historyId)
        {
            _canceled[historyId] = true;
            // ⭐ 這樣你在 Console 就能看到 API 有沒有呼叫成功
            System.Console.WriteLine($"[CancelStore] User requested CANCEL for HistoryId: {historyId}");
        }

        public void Clear(int historyId)
        {
            _canceled.TryRemove(historyId, out _);
        }
    }
}