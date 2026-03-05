using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Services
{
    /// <summary>
    /// 任務追蹤器 (Singleton)
    /// 負責管理 Slave 節點上所有正在執行中的 CancellationTokenSource
    /// </summary>
    public sealed class JobTracker
    {
        private readonly ILogger<JobTracker> _log;
        
        // 使用 ConcurrentDictionary 確保多執行緒安全
        // Key: HistoryId, Value: 該任務的 CancellationTokenSource
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _activeJobs = new();

        public JobTracker(ILogger<JobTracker> log)
        {
            _log = log;
        }

        /// <summary>
        /// 註冊任務：當 Receive 收到新任務並準備開始搬移前呼叫
        /// </summary>
        public void Register(int historyId, CancellationTokenSource cts)
        {
            if (historyId <= 0) return;

            // 使用 AddOrUpdate：若 ID 重複（例如 Master 重送），先中斷舊的並 Dispose，再存入新的
            _activeJobs.AddOrUpdate(historyId, 
                addValue: cts, 
                updateValueFactory: (id, oldCts) => 
                {
                    _log.LogWarning("[Tracker] 重複註冊 hid={hid}，正在中斷舊任務並清理資源。", id);
                    try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
                    return cts;
                });
        }

        /// <summary>
        /// 註銷任務：由 Worker 的 finally 區塊呼叫，代表任務已完全結束
        /// </summary>
        public void Unregister(int historyId)
        {
            if (_activeJobs.TryRemove(historyId, out var cts))
            {
                // 任務結束時，確實釋放 CTS 資源
                try { cts.Dispose(); } catch { }
                _log.LogDebug("[Tracker] 任務 hid={hid} 已註銷。剩餘任務數: {count}", historyId, _activeJobs.Count);
            }
        }

        /// <summary>
        /// 核心中斷方法：觸發 Cancel 訊號，但不從清單移除（由 Worker 結束時自發 Unregister）
        /// </summary>
        public bool Cancel(int historyId)
        {
            if (_activeJobs.TryGetValue(historyId, out var cts))
            {
                try
                {
                    _log.LogWarning("[Tracker] 收到取消請求，發送 Cancel 訊號給 hid={hid}", historyId);
                    cts.Cancel(); // ✨ 讓 FileActionWorker 的異步 IO 拋出 OperationCanceledException
                    return true;
                }
                catch (ObjectDisposedException) { return true; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[Tracker] 中斷 hid={hid} 時發生錯誤", historyId);
                    return false;
                }
            }
            
            _log.LogInformation("[Tracker] 請求取消 hid={hid}，但該任務不在執行清單中。", historyId);
            return false;
        }

        /// <summary>
        /// 獲取目前 Slave 節點正在跑的所有 ID（除錯用）
        /// </summary>
        public List<int> GetActiveIds() => _activeJobs.Keys.ToList();
    }
}