    // HistoryWatchService.cs
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using FileMoverWeb.Models;
    using FileMoverWeb.Services;

    using System.Net.Http;
    using System.Net.Http.Json;
    using Dapper;
    using System.Collections.Generic; // 解決 List<> 的錯誤
        // 解決 MoveItemProgress 的錯誤
    namespace FileMoverWeb.Services
    {
        /// <summary>
        /// 2025-12 Slot-based 版本：
        /// - 所有任務（Phase1 搬移 / Phase2 回遷 / 刪除）都由 slot 去一筆一筆撿出來執行
        /// - slot 數量 = GlobalMaxConcurrentMoves
        /// - 優先順序：Phase2 ＞ Phase1 搬移 ＞ 刪除
        /// - 前端仍然可以看到所有 file_status = 0 / 1 / 24 / 27 / -1 的「排隊中 / 進行中」任務
        /// 
        /// ⚠ 依賴 HistoryRepository 另外實作：
        ///   Task<HistoryTask?> ClaimPhase2TopOneAsync(string? group, CancellationToken ct)
        ///   Task<HistoryTask?> ClaimCopyTopOneAsync(int retryMinutes, string? group, CancellationToken ct)
        ///   Task<HistoryTask?> ClaimDeleteTopOneAsync(int retryMinutes, string? group, CancellationToken ct)
        /// </summary>
        public sealed class HistoryWatchService : BackgroundService
        {
            private readonly ISlotConfigProvider _slotConfig;
            private int _currentRunningSlots;
            private readonly ILogger<HistoryWatchService> _log;
            private readonly IServiceProvider _sp;
            private readonly IConfiguration _cfg;
            private readonly ICancelStore _cancelStore;
            private const int MaxMoveAttempts = 1;
            private int _lastEnabledSlots = -1;
            private readonly IJobProgress _progress;
            private readonly IMoveRetryStore _retryStore;

            private const int MaxSlots = 10;   // 上限，用來 clamp

            // Heartbeat / 節點資訊
            // private readonly HttpClient _http = new();
            private readonly IHttpClientFactory _httpClientFactory;
            private readonly string _nodeName;
            private readonly string _nodeGroup;
            private readonly string _role;
            private readonly int _maxConcurrencyConfigured;
            private readonly int _heartbeatIntervalSec;
            private readonly string? _masterBaseUrl;
            private readonly ITaskQueue _taskQueue;
            // 目前正在執行中的 slot 數量（所有 slot 加總）
            // private int _currentRunningSlots;
            public int CurrentRunningCount => _currentRunningSlots;
            public HistoryWatchService(
                ILogger<HistoryWatchService> log,
                IServiceProvider sp,
                IConfiguration cfg,
                IJobProgress progress,
                IMoveRetryStore retryStore,
                ICancelStore cancelStore,
                ISlotConfigProvider slotConfig,
                IHttpClientFactory httpClientFactory,
                ITaskQueue taskQueue)
            {
                _log = log;
                _sp = sp;
                _cfg = cfg;
                _progress = progress;
                _retryStore = retryStore;
                _slotConfig = slotConfig;
                // ⭐ 從 appsettings 讀節點資訊
                _nodeName = _cfg["Cluster:NodeName"] ?? Environment.MachineName;
                _nodeGroup = _cfg["FloorRouting:Group"] ?? _cfg["Cluster:Group"] ?? "";
                _role = _cfg["Cluster:Role"] ?? "Worker";

                _maxConcurrencyConfigured = _cfg.GetValue<int>("GlobalMaxConcurrentMoves", 2);
                if (_maxConcurrencyConfigured < 1) _maxConcurrencyConfigured = 1;
                if (_maxConcurrencyConfigured > MaxSlots) _maxConcurrencyConfigured = MaxSlots;

                // ⭐ 心跳頻率（秒），預設 5 秒一次
                _heartbeatIntervalSec = _cfg.GetValue<int>("Cluster:HeartbeatIntervalSeconds", 5);

                // ⭐ 要打到哪一台 Master（例如 http://192.168.30.118:5000）
                _masterBaseUrl = _cfg["Cluster:MasterBaseUrl"];
                _cancelStore = cancelStore;
                _httpClientFactory = httpClientFactory;
                _taskQueue = taskQueue;
            }
    // public async Task<bool> RunOneIterationAsync(int slotIndex, CancellationToken ct)
    // {
    //     var enabledSlots = await _slotConfig.GetEnabledSlotsAsync(ct);
    //     if (slotIndex >= enabledSlots) return false;

    //     try
    //     {
    //         _log.LogInformation("[Slot {slot}] waiting task...", slotIndex);

    //         var task = await _taskQueue.DequeueAsync(ct);
    //         if (task == null)
    //         {
    //             _log.LogInformation("[Slot {slot}] dequeue got null task", slotIndex);
    //             return false;
    //         }

    //         var action = (task.Action ?? "").Trim();

    //         // ✅ Phase2：按回遷後你說會變 24/27
    //         var isPhase2 = (task.FileStatus == 24 || task.FileStatus == 27);

    //         // ✅ Delete：-1 或 action=delete

    //         var isDelete = task.FileStatus == -1
    //                     || string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase);

    //         _log.LogInformation("[Slot {slot}] dequeued task hid={hid} action={action} status={status}",
    //             slotIndex, task.HistoryId, action, task.FileStatus);

    //         // ✅ 讓計數器/Log 更好看（Phase2 會顯示 PHASE2）
    //         var type = isDelete ? "DELETE"
    //                 : isPhase2 ? "PHASE2"
    //                 : (string.IsNullOrWhiteSpace(action) ? "MOVE" : action.ToUpperInvariant());

    //         return await ExecuteWithCounter(slotIndex, type, task.HistoryId, async () =>
    //         {
    //             using var scope = _sp.CreateScope();

    //             HistoryRepository? repo = null;
    //             if (IsMasterWriter())
    //                 repo = scope.ServiceProvider.GetRequiredService<HistoryRepository>();

    //             var mover = scope.ServiceProvider.GetRequiredService<MoveWorker>();
    //             var group = _cfg.GetValue<string>("FloorRouting:Group") ?? "";
    //             // var group = _cfg.GetValue<string>("FloorRouting:Group") ?? "";

    //             if (isDelete)
    //             {
    //                 // ✅ delete 永遠不進 MoveWorker
    //                 await RunSingleDeleteAsync(repo, task, ct);
    //                 // RunSingleDelete(task);
    //                 return;
    //             }

    //             var restoreId   = task.RestoreStorageId;
    //             var restorePath = task.RestorePath;

    //             // 初始化進度條（只對搬運任務）
    //             // var totals = new Dictionary<string, long> { { $"TO-{task.HistoryId}", task.FileSize } };
    //             // _progress.InitTotals(task.HistoryId.ToString(), totals);
         
    //             // var actLower = (task.Action ?? "").Trim().ToLowerInvariant();
    //             // long totalForUi = (actLower == "move") ? 100 : task.FileSize; // Move 硬寫 100

    //             // var totals = new Dictionary<string, long> { { $"TO-{task.HistoryId}", totalForUi } };
    //             // _progress.InitTotals(task.HistoryId.ToString(), totals);


    //             var jobId  = task.HistoryId.ToString();
    //         var destId = $"TO-{task.HistoryId}";

    //         var actLower = (task.Action ?? "").Trim().ToLowerInvariant();

    //         // ✅ Phase2：total 用 2 倍，讓「先加 fileSize」= 50%
    //         long totalForUi =
    //             isPhase2 ? task.FileSize * 2
    //         : (actLower == "move" ? 100 : task.FileSize);

    //         _progress.InitTotals(jobId, new Dictionary<string, long> { { destId, totalForUi } });

    //         // ✅ Phase2：slot 撿到且準備做 → 立刻讓 UI 跳到 50%
    //         // （這個點就是你定義的「開始」）
    //         if (isPhase2 && task.FileSize > 0)
    //         {
    //             _progress.AddCopied(jobId, destId, task.FileSize); // 50%
    //         }

    //             // ✅ Phase2：不走 HandleMoveOrCopy（避免被同樓層判斷吃掉）
    //             if (isPhase2)
    //             {
    //                 _log.LogInformation("[Slot {slot}] Pick PHASE2 task #{hid} status={st}",
    //                     slotIndex, task.HistoryId, task.FileStatus);

    //                 if (!restoreId.HasValue || string.IsNullOrWhiteSpace(restorePath))
    //                 {
    //                     if (IsMasterWriter())
    //                     {
    //                         await repo!.FailAsync(task.HistoryId, 903, "未設定 RESTORE，無法回遷 (Phase2)", ct);
    //                         _retryStore.Clear(task.HistoryId);
    //                     }
    //                     else
    //                     {
    //                         await ReportToMasterAsync(task.HistoryId, "phase2", false, 903, "未設定 RESTORE，無法回遷 (Phase2)", ct);
    //                     }
    //                     return;
    //                 }

    //                 await RunSinglePhase2Async(repo, mover, task, restoreId.Value, restorePath!, ct);
    //                 return;
    //             }

    //             // ✅ Phase1 / 一般搬移：照你原本邏輯走（同樓層或跨樓層→先去 RESTORE）
    //             await HandleMoveOrCopy(repo, mover, task, group, restoreId, restorePath, ct);
    //         });
    //     }
    //     catch (OperationCanceledException)
    //     {
    //         return false;
    //     }
    //     catch (Exception ex)
    //     {
    //         _log.LogError(ex, "[Slot {slot}] unexpected error", slotIndex);
    //         return false;
    //     }
    // }

public async Task<bool> RunOneIterationAsync(int slotIndex, CancellationToken ct)
{
    // 1. 優先檢查 Slot Index。
    // 如果目前配置縮減了 Slot，讓超出的 Slot 直接休息，不要去排隊搶任務。
    var enabledSlots = await _slotConfig.GetEnabledSlotsAsync(ct);
    if (slotIndex >= enabledSlots)
    {
        await Task.Delay(1000, ct); // 避免空轉過快
        return false;
    }

    try
    {
        // 2. 進入等待狀態（此時不計入 _currentRunningSlots）
        _log.LogInformation("[Slot {slot}] waiting for task...", slotIndex);

        var task = await _taskQueue.DequeueAsync(ct);
        
        // 如果 Queue 關閉或拿到 null，代表目前沒事做
        if (task == null) return false;

        // 3. 拿到任務了，解析類型
        var action = (task.Action ?? "").Trim();
        var isPhase2 = (task.FileStatus == 24 || task.FileStatus == 27);
        var isDelete = task.FileStatus == -1 || string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase);
        
        var type = isDelete ? "DELETE" 
                 : isPhase2 ? "PHASE2" 
                 : (string.IsNullOrWhiteSpace(action) ? "MOVE" : action.ToUpperInvariant());

        // 4. 真正「開始執行」才進入 ExecuteWithCounter
        // 此處 Interlocked.Increment 就會發生，_currentRunningSlots 正確反映執行中數量
        return await ExecuteWithCounter(slotIndex, type, task.HistoryId, async () =>
        {
            using var scope = _sp.CreateScope();
            var repo = IsMasterWriter() ? scope.ServiceProvider.GetRequiredService<HistoryRepository>() : null;
            var mover = scope.ServiceProvider.GetRequiredService<MoveWorker>();
            var group = _cfg.GetValue<string>("FloorRouting:Group") ?? "";

            if (isDelete)
            {
                await RunSingleDeleteAsync(repo, task, ct);
            }
            else
            {
                // 初始化進度條與處理 Phase2 的 50% 邏輯
                InitTaskProgress(task, isPhase2);

                if (isPhase2)
                {
                    // Phase2 邏輯
                    await RunSinglePhase2Async(repo, mover, task, task.RestoreStorageId ?? 0, task.RestorePath ?? "", ct);
                }
                else
                {
                    // Phase1 或 同樓層搬移
                    await HandleMoveOrCopy(repo, mover, task, group, task.RestoreStorageId, task.RestorePath, ct);
                }
            }
        });
    }
    catch (OperationCanceledException)
    {
        return false; 
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "[Slot {slot}] unexpected error", slotIndex);
        return false;
    }
}

// 抽取進度條邏輯，確保與 RunOneIterationAsync 職責分離
private void InitTaskProgress(HistoryTask task, bool isPhase2)
{
    var jobId = task.HistoryId.ToString();
    var destId = $"TO-{task.HistoryId}";
    var actLower = (task.Action ?? "").Trim().ToLowerInvariant();

    // UI 總量邏輯
    long totalForUi = isPhase2 ? task.FileSize * 2 : (actLower == "move" ? 100 : task.FileSize);

    _progress.InitTotals(jobId, new Dictionary<string, long> { { destId, totalForUi } });

    // Phase2 撿到即 50%
    if (isPhase2 && task.FileSize > 0)
    {
        _progress.AddCopied(jobId, destId, task.FileSize);
    }
}

    private async Task<bool> ExecuteWithCounter(int slot, string type, int hid, Func<Task> action)
    {
        _log.LogInformation("[Slot {slot}] Pick {type} task #{hid}", slot, type, hid);
        Interlocked.Increment(ref _currentRunningSlots);
        try
        {
            await action();
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _currentRunningSlots);
        }
    }


private async Task HandleMoveOrCopy(
    HistoryRepository? repo, MoveWorker mover, HistoryTask t,
    string group, int? restoreId, string? restorePath, CancellationToken ct)
{
    // 1. 判斷是否為同樓層搬移（根據 DB 記錄的 FromGroup 與 ToGroup）
    bool isSameFloorInDb =
        !string.IsNullOrEmpty(t.FromGroup) &&
        !string.IsNullOrEmpty(t.ToGroup) &&
        t.FromGroup.Equals(t.ToGroup, StringComparison.OrdinalIgnoreCase);

    _log.LogInformation("[{hid}] 搬移類型檢查: From={fg}, To={tg}, isSameFloor={same}",
        t.HistoryId, t.FromGroup, t.ToGroup, isSameFloorInDb);

    if (isSameFloorInDb)
    {
        // 同樓層：直接執行搬移（不需經過 RESTORE）
        await RunSingleSameFloorAsync(repo, mover, t, ct);
        return;
    }

    // 2. 跨樓層搬移 (Phase 1)：必須要有 RESTORE 設定
    if (!restoreId.HasValue || string.IsNullOrWhiteSpace(restorePath))
    {
        var err = "未設定 RESTORE，無法跨層搬移 (Phase1)";
        if (IsMasterWriter())
        {
            await repo!.FailAsync(t.HistoryId, 903, err, ct);
            _retryStore.Clear(t.HistoryId);
        }
        else
        {
            await ReportToMasterAsync(t.HistoryId, "move", false, 903, err, ct);
        }
        return;
    }

    // 執行跨層 Phase 1：搬到本層 RESTORE
    await RunSingleCrossFloorPhase1Async(repo, mover, t, group, restoreId.Value, restorePath!, ct);
}

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var role = _cfg["Cluster:Role"] ?? "Slave";
        var allowMasterWork = _cfg.GetValue<bool>("Cluster:AllowMasterWork", false);

        // 如果是 Master 且不兼任搬運，則不啟動搬運插槽
        if (string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase) && !allowMasterWork)
        {
            _log.LogInformation("HistoryWatchService (Worker) disabled for Master node.");
            return;
        }

        _log.LogInformation("HistoryWatchService (SlotWorker) started. Group={group}", _nodeGroup);

        // 啟動心跳回報給 Master
        // _ = Task.Run(() => HeartbeatLoopAsync(stoppingToken), stoppingToken);

        // 此處不需額外動作，SlotSupervisorService 會根據並行數啟動 SlotWorker 並呼叫 RunOneIterationAsync
        await Task.CompletedTask;
    }

            /// <summary>
            /// 固定每 _heartbeatIntervalSec 秒送一次節點心跳給 /api/nodes/heartbeat
            /// </summary>
                private async Task HeartbeatLoopAsync(CancellationToken ct)
                {
                    if (_heartbeatIntervalSec <= 0 || string.IsNullOrWhiteSpace(_masterBaseUrl))
                    {
                        _log.LogInformation("Heartbeat disabled: interval={interval}, masterBaseUrl={url}",
                            _heartbeatIntervalSec, _masterBaseUrl ?? "(null)");
                        return;
                    }

                    _log.LogInformation("Heartbeat loop started: interval={interval}s, master={url}",
                        _heartbeatIntervalSec, _masterBaseUrl);

                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            await SendHeartbeatAsync(ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Heartbeat loop error");
                        }

                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSec), ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    _log.LogInformation("Heartbeat loop stopped.");
                }

                private async Task SendHeartbeatAsync(CancellationToken ct)
                {
                    var client = _httpClientFactory.CreateClient("MasterClient");
                    var url = _masterBaseUrl!.TrimEnd('/') + "/api/nodes/heartbeat";

                    var payload = new
                    {
                        nodeName       = _nodeName,
                        role           = _role,
                        group          = _nodeGroup,
                        // maxConcurrency = await GetEnabledSlotsAsync(ct),
                        currentRunning = Math.Max(0, _currentRunningSlots),
                        hostName       = Environment.MachineName,
                        ipAddress      = "" // 你要的話之後可以真的查 IP
                    };

                    try
                    {
                        var resp = await client.PostAsJsonAsync(url, payload, ct);
                        // 不強制 EnsureSuccess，fail 就算了，下次再試
                        if (!resp.IsSuccessStatusCode)
                        {
                            _log.LogDebug("Heartbeat HTTP failed: {code}", resp.StatusCode);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug(ex, "Heartbeat send error");
                    }
                }


        

            private static string BuildTempPathFromFinal(string finalPath)
                {
                    var dir = Path.GetDirectoryName(finalPath)
                            ?? throw new InvalidOperationException($"finalPath 無法取得目錄：{finalPath}");
                    var file = Path.GetFileName(finalPath);
                    return Path.Combine(dir, "temp", file);
                }

            private  bool VerifyTempSize(string tempPath, long? size4F, long? size7F,int hid)
                {
                long actual;
                try
                {
                    actual = new FileInfo(tempPath).Length;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "[{hid}] [VERIFY] read size failed path={path}",
                        hid, tempPath);
                    return false;
                }

                // null 不跳過：記錄 + fail 交給 core
                if (!size4F.HasValue || !size7F.HasValue)
                {
                    _log.LogWarning(
                        "[{hid}] [VERIFY][WARN] null size: size4F={size4F}, size7F={size7F}",
                        hid, size4F, size7F);
                }

                var ok = VerifySizeCore(actual, size4F, size7F, out var match4, out var match7);

                _log.LogInformation(
                    "[{hid}] [VERIFY] path={path}, actual={actual}, size4F={size4F}, size7F={size7F}, match4={match4}, match7={match7}, ok={ok}",
                    hid, tempPath, actual, size4F, size7F, match4, match7, ok);

                return ok;
            }

                private static bool VerifySizeCore(long actual, long? size4F, long? size7F,
                    out bool match4, out bool match7)
                {
                    match4 = match7 = false;

                    var valid4 = size4F.HasValue && size4F.Value > 0;
                    var valid7 = size7F.HasValue && size7F.Value > 0;

                    // 兩個都沒有（null 或 0）→ 視為未知，直接 False

                    if (!valid4 && !valid7)
                        return false;

                    if (valid4) match4 = actual == size4F!.Value;
                    if (valid7) match7 = actual == size7F!.Value;

                    return match4 || match7;
                }



          // 移去helper 了
            // static async Task<bool> WaitFileFreeAsync(string path, int ms = 2000)
            // {
            //     // ⭐ 關鍵修正：如果檔案根本不存在，代表沒有人會佔用它，直接回傳 true
            //     if (!File.Exists(path)) 
            //     {
            //         return true; 
            //     }
            //     var sw = System.Diagnostics.Stopwatch.StartNew();
            //     while (sw.ElapsedMilliseconds < ms)
            //     {
            //         try
            //         {
            //             // 使用 FileShare.None 嘗試強制獨佔
            //             using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            //             return true; // 拿到獨占 → 代表安全，回傳成功
            //         }
            //         catch (IOException)
            //         {
            //             // 檔案被佔用中，持續輪詢
            //             await Task.Delay(200);
            //         }
            //         catch (Exception)
            //         {
            //             // 處理如權限不足等其他異常
            //             return false;
            //         }
            //     }
            //     return false; // 超時，判定為檔案仍在使用中
            // }

            private static void ReplaceToFinal(string tempPath, string finalPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

                if (File.Exists(finalPath))
                    File.Replace(tempPath, finalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, finalPath);
            }

        

   private async Task RunSingleSameFloorAsync(
    HistoryRepository? repo,
    MoveWorker mover,
    HistoryTask t,
    CancellationToken ct)
{
    var jobId    = t.HistoryId.ToString();
    var fileName = BuildFileName(t);
    var src      = Path.Combine(t.FromPath, fileName);
    var dst      = Path.Combine(t.ToPath, fileName);
    var tempPath = Path.Combine(t.ToPath, "temp", fileName);
    var action   = t.Action ?? "move";

    // --- 最終狀態追蹤變數 ---
    bool isFinalSuccess = false;
    int? finalCode = null;
    string? finalError = null;

    var req = new MoveBatchRequest
    {
        JobId = jobId,
        Items = new List<MoveItem>
        {
            new MoveItem
            {
                HistoryId     = t.HistoryId,
                FileId         = t.FileId,
                FromStorageId = t.FromStorageId,
                ToStorageId   = t.ToStorageId,
                SourcePath    = src,
                DestPath      = dst,
                DestId        = $"TO-{t.HistoryId}",
                UserBit       = t.UserBit,
                ToName        = t.ToName,
                ToType        = t.ToType,
                FromName      = t.FromName,
                FromType      = t.FromType,
                Extension     = t.Extension,
                Action        = t.Action,
            }
        }
    };

    _log.LogInformation("[{hid}] 開始 SAME-FLOOR move: {from} -> {to}", t.HistoryId, src, dst);

    try
    {
        // 執行搬移主體
        await mover.RunAsync(
            req,
            onItemDone: async r =>
            {
                // 注意：此 Callback 的目的是判定結果與執行實體檔案操作 (Verify/Move)
                try
                {
                    if (r.HistoryId == 0) return;

                    var code = r.StatusCode ?? MapMoveErrorCode(r.Error);

                    // 1) 處理失敗狀態 (包含 999 使用者取消)
                    if (!r.Success || code == 999)
                    {
                        finalCode = code;
                        finalError = r.Error ?? (code == 999 ? "Canceled by user" : "Transfer failed");
                        return; // 結束 Callback，狀態會由外層 finally 處理
                    }

                    // 2) 處理成功後的後續驗證與搬移
                    // A) IC/FTP 類型：直接標記成功
                    if (string.Equals(t.ToType, "IC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.ToType, "FTP", StringComparison.OrdinalIgnoreCase))
                    {
                        isFinalSuccess = true;
                        return;
                    }
                    await Task.Delay(1000, ct);
                    // B) 一般磁碟：執行 VERIFY + temp -> final
                    _log.LogInformation("[{hid}] 搬移完成 -> VERIFY...", r.HistoryId);
                    if (VerifyTempSize(tempPath, t.FileSize4F, t.FileSize7F, t.HistoryId))
                    {
                       // ✅ 改成 if 判斷：確保拿到獨佔權才執行搬移
                        bool isFree = await FileHelper.WaitFileFreeAsync(dst, 10000);
                        
                        if (isFree)
                        {
                            File.Move(tempPath, dst, overwrite: true);
                            isFinalSuccess = true;
                            _log.LogInformation("[{hid}] 完成：檔案已就位。", r.HistoryId);
                        }
                        else
                        {
                            // ❌ 如果 3 秒後還是被佔用，設定錯誤碼並報錯
                            finalCode = 912; // 912: 檔案使用中 (Sharing Violation)
                            finalError = $"目的地檔案正被其他程序佔用，無法覆寫: {dst}";
                            _log.LogWarning("[{hid}] 搬移失敗：{err}", r.HistoryId, finalError);
                        }
                    }
                    else
                    {
                        finalCode = 915;
                        finalError = "Size mismatch after transfer";
                       
                        _log.LogWarning("[{hid}] 驗證失敗：檔案大小不符。", r.HistoryId);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[{hid}] onItemDone Callback 異常", t.HistoryId);
                    finalCode = 91;
                    finalError = $"Callback error: {ex.Message}";
                }
            },
            ct: ct);
    }
    catch (OperationCanceledException)
    {
        // 捕捉從 ct: ct 丟出的取消異常
        _log.LogWarning("[{hid}] 任務偵測到 CancellationToken 取消", t.HistoryId);
        finalCode = 999;
        finalError = "Canceled by user";
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "[{hid}] RunAsync 執行異常", t.HistoryId);
        finalCode = 91;
        finalError = ex.Message;
    }
    finally
    {
        // =========================================================
        // 最終回報階段：所有 IO 與搬移結束後，統一在此寫入資料庫
        // =========================================================

        try
        {
            if (isFinalSuccess)
            {
                // --- 成功處理 ---
                if (IsMasterWriter() && repo != null)
                {
                    if (string.Equals(action, "move", StringComparison.OrdinalIgnoreCase))
                        await repo.CompleteMoveAsync(t.HistoryId, ct);
                    else
                        await repo.CompleteAsync(t.HistoryId, ct);

                    _retryStore.Clear(t.HistoryId);
                }
                else
                {
                    // Slave 模式
                    var okCode = string.Equals(action, "move", StringComparison.OrdinalIgnoreCase) ? 13 : 11;
                    await ReportToMasterAsync(t.HistoryId, action, true, okCode, null, ct);
                }
                _log.LogInformation("[{hid}] 資料庫狀態更新：成功", t.HistoryId);
            }
            else
            {
                // --- 失敗或取消處理 ---
                int errorCode = finalCode ?? 91;
                string errorMsg = finalError ?? "Unknown error";

                if (IsMasterWriter() && repo != null)
                {
                    await repo.FailAsync(t.HistoryId, errorCode, errorMsg, ct);
                    _retryStore.Clear(t.HistoryId);
                }
                else
                {
                    // Slave 模式回報失敗 (包含 999)
                    await ReportToMasterAsync(t.HistoryId, action, false, errorCode, errorMsg, ct);
                }
                _log.LogWarning("[{hid}] 資料庫狀態更新：失敗({code}) {msg}", t.HistoryId, errorCode, errorMsg);
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "[{hid}] Finally 寫入資料庫時發生嚴重錯誤", t.HistoryId);
        }

        // 清理資源
        _progress.CompleteJob(jobId);
        _cancelStore.Clear(t.HistoryId);
    }
}

        /// <summary>
    /// 跨層 Phase1：先從來源搬到本層 RESTORE
    /// 修改後：Slave 節點完全不連 DB，驗證資訊由 HistoryTask 提供
    /// </summary>
   private async Task RunSingleCrossFloorPhase1Async(
    HistoryRepository? repo,
    MoveWorker mover,
    HistoryTask t,
    string group,
    int restoreId,
    string restorePath,
    CancellationToken ct)
{
    var jobId = t.HistoryId.ToString(); 
    var fileName = BuildFileName(t);
    var src = Path.Combine(t.FromPath, fileName);
    var dst = Path.Combine(restorePath, fileName); // Phase1 目標：本層 RESTORE
    var action = t.Action ?? "move";

    // --- 最終狀態追蹤變數 ---
    bool isFinalSuccess = false;
    int? finalCode = null;
    string? finalError = null;

    var req = new MoveBatchRequest
    {
        JobId = jobId,
        Items = new List<MoveItem>
        {
            new MoveItem
            {
                HistoryId     = t.HistoryId,
                FileId        = t.FileId,
                FromStorageId = t.FromStorageId,
                ToStorageId   = restoreId,
                SourcePath    = src,
                DestPath      = dst,
                DestId        = $"TO-{t.HistoryId}",
                Extension     = t.Extension,
                Action        = t.Action, 
            }
        }
    };

    _log.LogInformation("[{hid}] CROSS-FLOOR Phase1 開始: {fromGroup} -> RESTORE({restoreId})", t.HistoryId, t.FromGroup, restoreId);

    try
    {
        await mover.RunAsync(req, onItemDone: async r =>
        {
            try
            {
                if (r.HistoryId == 0) return;

                var code = r.StatusCode ?? MapMoveErrorCode(r.Error);

                // 1) 處理使用者取消 (999) 或 搬運失敗
                if (!r.Success || code == 999)
                {
                    finalCode = code;
                    finalError = r.Error ?? (code == 999 ? "Canceled by user" : "Phase1 transfer failed");
                    return;
                }

                // 2) 搬運成功 (Success)
                // A) IC/FTP 類型：直接標記成功
                if (string.Equals(t.ToType, "IC", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.ToType, "FTP", StringComparison.OrdinalIgnoreCase))
                {
                    isFinalSuccess = true;
                    return;
                }

                // B) 一般磁碟：Verify temp -> Finalize
                var finalPath = dst;
                var tempPath = BuildTempPathFromFinal(finalPath);

                if (!File.Exists(tempPath))
                {
                    if (File.Exists(finalPath))
                    {
                        isFinalSuccess = true;
                        _log.LogInformation("[{hid}] Phase1: temp missing but final exists -> success", t.HistoryId);
                    }
                    else
                    {
                        finalCode = 915;
                        finalError = $"Temp file not found: {tempPath}";
                    }
                    return;
                }
                await Task.Delay(1000, ct);
                // 驗證大小
                if (VerifyTempSize(tempPath, t.FileSize4F, t.FileSize7F, t.HistoryId))
                {
                    bool isFree = await FileHelper.WaitFileFreeAsync(finalPath, 10000);
                    if (isFree)
                    {
                        ReplaceToFinal(tempPath, finalPath);
                        isFinalSuccess = true;
                        _log.LogInformation("[{hid}] Phase1 finalize 實體檔案就位成功。", t.HistoryId);
                    } 
                }
                else
                {
                    finalCode = 915;
                    finalError = $"Size mismatch after Phase1 transfer";
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[{hid}] Phase1 callback 處理異常", t.HistoryId);
                finalCode = 91;
                finalError = ex.Message;
            }
        }, ct: ct);
    }
    catch (OperationCanceledException)
    {
        finalCode = 999;
        finalError = "Canceled by user";
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "[{hid}] Phase1 RunAsync 其它異常", t.HistoryId);
        finalCode = 91;
        finalError = ex.Message;
    }
    finally
    {
        // =========================================================
        // 最終回報階段：統一在最後寫入資料庫
        // =========================================================
        try
        {
            if (isFinalSuccess)
            {
                // 跨層 Phase1 成功後的狀態碼 (4F=14, 7F=17)
                var statusCode = string.Equals(group, "4F", StringComparison.OrdinalIgnoreCase) ? 14 : 17;

                if (IsMasterWriter() && repo != null)
                {
                    await repo.MarkPhase1DoneAsync(t.HistoryId, statusCode, ct);
                    _retryStore.Clear(t.HistoryId);
                }
                else
                {
                    // Slave 模式：回報特定狀態碼給 Master
                    await ReportToMasterAsync(t.HistoryId, "phase1done", true, statusCode, null, ct);
                }
                _log.LogInformation("[{hid}] Phase1 資料庫更新成功，狀態碼: {s}", t.HistoryId, statusCode);
            }
            else
            {
                int errorCode = finalCode ?? 91;
                string errorMsg = finalError ?? "Unknown phase1 error";

                if (IsMasterWriter() && repo != null)
                {
                    await repo.FailAsync(t.HistoryId, errorCode, errorMsg, ct);
                    _retryStore.Clear(t.HistoryId);
                }
                else
                {
                    await ReportToMasterAsync(t.HistoryId, action, false, errorCode, errorMsg, ct);
                }
                _log.LogWarning("[{hid}] Phase1 標記失敗: {c}, {m}", t.HistoryId, errorCode, errorMsg);
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "[{hid}] Phase1 Finally DB 寫入崩潰", t.HistoryId);
        }

        _progress.CompleteJob(jobId);
        _cancelStore.Clear(t.HistoryId);
    }
}

            /// <summary>
            /// Phase2 回遷：從本層 RESTORE 搬到真正目的地
     
            /// </summary>
    private async Task RunSinglePhase2Async(
    HistoryRepository? repo,
    MoveWorker mover,
    HistoryTask t,
    int restoreId,
    string restorePath,
    CancellationToken ct)
{
    var jobId = t.HistoryId.ToString();
    var fileName = BuildFileName(t);
    var src = Path.Combine(restorePath, fileName);      // Phase2 來源：RESTORE
    var dst = Path.Combine(t.ToPath, fileName);         // 目的地：真正 Storage
    var action = (t.Action ?? "phase2").Trim();

    // --- 最終狀態追蹤變數 ---
    bool isFinalSuccess = false;
    int? finalCode = null;
    string? finalError = null;

    var req = new MoveBatchRequest
    {
        JobId = jobId,
        Items = new List<MoveItem>
        {
            new MoveItem
            {
                HistoryId     = t.HistoryId,
                FileId         = t.FileId,
                FromStorageId = restoreId,
                ToStorageId   = t.ToStorageId ?? t.FromStorageId,
                SourcePath    = src,
                DestPath      = dst,
                DestId        = $"TO-{t.HistoryId}",
                Extension     = t.Extension,
                Action        = "move",
            }
        }
    };

    _log.LogInformation("[{hid}] PHASE2 restore: RESTORE({restoreId}) -> {toPath}", t.HistoryId, restoreId, t.ToPath);

    try
    {
        await mover.RunAsync(
            req,
            onItemDone: async r =>
            {
                try
                {
                    if (r.HistoryId == 0) return;

                    var code = r.StatusCode ?? MapMoveErrorCode(r.Error);

                    // 1) 處理失敗或取消 (999)
                    if (!r.Success || code == 999)
                    {
                        finalCode = code;
                        finalError = r.Error ?? (code == 999 ? "Canceled by user" : "Phase2 transfer failed");
                        return;
                    }

                    // 2) 處理成功後的後續邏輯
                    // A) FTP/IC 類型：直接成功
                    if (string.Equals(t.ToType, "IC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.ToType, "FTP", StringComparison.OrdinalIgnoreCase))
                    {
                        isFinalSuccess = true;
                        _log.LogInformation("[{hid}] FTP/IC Phase2 success.", r.HistoryId);
                        return;
                    }

                    // B) 一般磁碟：判斷 temp 與 final 狀態
                    var finalPath = dst;
                    var tempPath = BuildTempPathFromFinal(finalPath);

                    if (!File.Exists(tempPath))
                    {
                        if (File.Exists(finalPath))
                        {
                            isFinalSuccess = true;
                            _log.LogInformation("[{hid}] FINALIZE: temp missing but final exists -> success", t.HistoryId);
                        }
                        else
                        {
                            finalCode = code; // 保持原始 code
                            finalError = $"Temp not ready yet: {tempPath}";
                            _log.LogWarning("[{hid}] FINALIZE: temp not ready -> fail", t.HistoryId);
                        }
                        return;
                    }
                    await Task.Delay(1000, ct);
                    // 驗證大小
                    if (VerifyTempSize(tempPath, t.FileSize4F, t.FileSize7F, t.HistoryId))
                    {
                        bool isFree = await FileHelper.WaitFileFreeAsync(finalPath, 10000);
                        if (isFree)
                        {
                            ReplaceToFinal(tempPath, finalPath);
                            isFinalSuccess = true;
                            _log.LogInformation("[{hid}] PHASE2 finalize success.", r.HistoryId);
                        }
                    }
                    else
                    {
                        finalCode = 915;
                        finalError = $"Size mismatch. db4F={t.FileSize4F}, db7F={t.FileSize7F}";
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[{hid}] PHASE2 callback crashed", t.HistoryId);
                    finalCode = 91;
                    finalError = ex.Message;
                }
            },
            ct: ct);
    }
    catch (OperationCanceledException)
    {
        finalCode = 999;
        finalError = "Canceled by user";
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "[{hid}] PHASE2 RunAsync fatal error", t.HistoryId);
        finalCode = 91;
        finalError = ex.Message;
    }
    finally
    {
        // =========================================================
        // 最終回報階段：統一在最後寫入資料庫
        // =========================================================
        try
        {
            if (isFinalSuccess)
            {
                if (IsMasterWriter() && repo != null)
                {
                    await repo.CompleteAsync(t.HistoryId, ct); // 狀態 11
                    _retryStore.Clear(t.HistoryId);
                }
                else
                {
                    await ReportToMasterAsync(t.HistoryId, action, true, 11, null, ct);
                }
            }
            else
            {
                int errorCode = finalCode ?? 91;
                string errorMsg = finalError ?? "Unknown phase2 error";

                if (IsMasterWriter() && repo != null)
                {
                    await repo.FailAsync(t.HistoryId, errorCode, errorMsg, ct);
                    _retryStore.Clear(t.HistoryId);
                }
                else
                {
                    await ReportToMasterAsync(t.HistoryId, action, false, errorCode, errorMsg, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "[{hid}] PHASE2 Finally DB error", t.HistoryId);
        }

        _progress.CompleteJob(jobId);
        _cancelStore.Clear(t.HistoryId);
    }
}
// private  void RunSingleDelete
// (
           
//             HistoryTask t)
       

//  {
//         var fileName = BuildFileName(t);
//         var jobId = t.HistoryId.ToString();
//         // var action = "delete";
//         var src = Path.Combine(t.FromPath, fileName);
//          try
//         {
//             File.Delete(src);
//         }
//         catch (Exception ex)
//             {
//               MessageBox.Show(ex.Message);
//             }}



            /// <summary>
            /// 單筆刪除任務（保留你原本的 delete 進度條＆錯誤碼 921/922/923）
            /// </summary>
            private async Task RunSingleDeleteAsync(
                HistoryRepository? repo,
                HistoryTask t,
                CancellationToken ct)
            {
                var fileName = BuildFileName(t);
                var jobId = t.HistoryId.ToString();
                var action = "delete";
                var src = Path.Combine(t.FromPath ?? "", fileName ?? "");
                bool isSuccess = false; // 關鍵：用來記錄最後是否真的刪除成功

                _log.LogWarning("[DBG_DEL_ENTER] pid={pid} node={node}", Environment.ProcessId, _cfg["Cluster:NodeName"]);

                try
                {
                    // 1. 基本檢查
                    if (string.IsNullOrWhiteSpace(t.FromPath) || string.IsNullOrWhiteSpace(fileName))
                    {
                        await HandleDeleteFailAsync(repo, t.HistoryId, 903, "Missing delete path", ct);
                        return; // 進入 finally，但 isSuccess 為 false，所以不會報成功
                    }

                    // 2. 使用者取消檢查
                    if (_cancelStore.ShouldCancel(t.HistoryId))
                    {
                        await HandleDeleteFailAsync(repo, t.HistoryId, 999, "Canceled by user", ct);
                        return; 
                    }

                    // 3. 來源檔案存在檢查 (包含 NAS 延遲 probe)
                    bool exists = File.Exists(src);
                    if (!exists)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            await Task.Delay(200, ct);
                            if (File.Exists(src)) { exists = true; break; }
                        }
                    }

                    if (!exists)
                    {
                        await HandleDeleteFailAsync(repo, t.HistoryId, 921, $"Source not found: {src}", ct);
                        return;
                    }

                    // 4. 執行刪除
                    try
                    {
                        bool isFree = await FileHelper.WaitFileFreeAsync(src, 2000);
                        if (!isFree)
                        {
                            // 關鍵：如果 2 秒後檔案還是被瀏覽器佔用，直接報錯跳出，不執行 File.Delete
                            await HandleDeleteFailAsync(repo, t.HistoryId, 922, "檔案正被其他程序佔用(如瀏覽器播放中)，拒絕刪除", ct);
                            return; 
                        }
                        // Debugger.Break(); // 建議僅在開發時開啟
                        File.Delete(src);

                        // // 5. 刪除後驗證 (確認 SMB/NAS 已經同步狀態)
                        for (int i = 0; i < 5; i++) // 稍微增加次數確保穩定
                        {
                            await Task.Delay(200, ct);
                            if (!File.Exists(src)) 
                            {
                                isSuccess = true; // 只有走到這裡，才算真正成功
                                break;
                            }
                        }

                        // if (!isSuccess)
                        // {
                        //     await HandleDeleteFailAsync(repo, t.HistoryId, 922, "Delete not converged (still exists)", ct);
                        //     return;
                        // }
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "[DEL_EX] {src}", src);
                        await HandleDeleteFailAsync(repo, t.HistoryId, 922, $"IO Error: {ex.Message}", ct);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    _log.LogInformation("Task canceled: {hid}", t.HistoryId);
                    // 取消通常不視為成功，讓 isSuccess 保持 false
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[FATAL_DEL] {hid}", t.HistoryId);
                    await HandleDeleteFailAsync(repo, t.HistoryId, 923, ex.Message, ct);
                }
                finally
                {
                    // =========================================================
                    // 最終回報階段：確保前面所有 await (IO, Delay) 都跑完了才進來
                    // =========================================================
                    if (isSuccess)
                    {
                        if (IsMasterWriter() && repo != null)
                        {
                            _log.LogInformation("[DEL_OK][MASTER] hid={hid} -> CompleteDeleteAsync", t.HistoryId);
                            await repo.CompleteDeleteAsync(t.HistoryId, ct);
                            _retryStore.Clear(t.HistoryId);
                        }
                        else
                        {
                            _log.LogInformation("[DEL_OK][REPORT] hid={hid} -> status=12", t.HistoryId);
                            await ReportToMasterAsync(t.HistoryId, action, true, 12, null, ct);
                        }
                    }

                    // 無論成功或失敗都要清理的資源
                    _progress.CompleteJob(jobId);
                    _cancelStore.Clear(t.HistoryId);
                    _log.LogInformation("[DEL_FINALLY] hid={hid} isSuccess={s}", t.HistoryId, isSuccess);
                }
            }
    // ===== helper：delete fail（不重試 or 直接記錄）=====
    private async Task HandleDeleteFailAsync(HistoryRepository? repo, int hid, int code, string err, CancellationToken ct)
    {
        if (IsMasterWriter())
        {
            await repo.FailDeleteAsync(hid, code, err, ct);
            _retryStore.Clear(hid);
        }
        else
        {
            await ReportToMasterAsync(hid, "delete", false, code, err, ct);
        }
    }
    

    // ===== helper：delete retryable fail（Master 才算 retry、寫 800）=====
    // private async Task HandleDeleteRetryableFailAsync(HistoryRepository? repo, int hid, int code, string err, CancellationToken ct)
    // {
    //     if (IsMasterWriter())
    //     {
    //         var failCount = _retryStore.IncrementFail(hid, code, err);

    //         if (failCount >= MaxMoveAttempts)
    //         {
    //             await repo.FailDeleteAsync(hid, code, err, ct);
    //             _retryStore.Clear(hid);
    //         }
    //         else
    //         {
    //             await repo.FailDeleteAsync(hid, 800, err, ct);
    //         }
    //     }
    //     else
    //     {
    //         await ReportToMasterAsync(hid, "delete", false, code, err, ct);
    //     }
    // }

            // #endregion

            /// <summary>
            /// 依錯誤訊息判斷搬移失敗狀態碼：911/912/913/914
            /// </summary>
            private static int MapMoveErrorCode(string? error)
            {
                var msg = (error ?? string.Empty).ToLowerInvariant();

                // 911: 找不到來源 / 檔案不存在
                if (msg.Contains("could not find file") || msg.Contains("does not exist") || msg.Contains("找不到") || msg.Contains("source not found"))
                    return 911;

                // 912: 檔案使用中 (sharing violation / being used by another process)
                if (msg.Contains("being used by another process") || msg.Contains("sharing violation"))
                    return 912;

                // 914: 找不到目的地 (路徑不存在)
                if (msg.Contains("could not find a part of the path") || msg.Contains("path not found") || msg.Contains("找不到路徑"))
                    return 914;

                // 913: 權限不足 or 其他錯誤
                if (msg.Contains("access is denied") || msg.Contains("未經授權") || msg.Contains("unauthorized"))
                    return 913;

                // 預設也歸類成 913
                return 913;
            }

            private static string NormalizeExt(string? ext, string defaultExt = ".MXF")
                {
                    ext = (ext ?? "").Trim();
                    if (string.IsNullOrEmpty(ext)) return defaultExt;
                    return ext.StartsWith(".") ? ext : "." + ext;
                }

            private static string BuildFileName(HistoryTask t)
            {
                // ✅ 你要求：一定用 Extension
                var ext = NormalizeExt(t.Extension, ".MXF");

                if (!string.IsNullOrWhiteSpace(t.UserBit))
                    return $"{t.UserBit}{ext}";

                // 沒 UserBit 就用原 FileName（不硬改）
                return t.FileName ?? "";
            }

            private bool IsMasterWriter()
            {
                var role = _cfg["Cluster:Role"] ?? "Worker";
                return string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase);
            }

                // private async Task ReportToMasterAsync(int historyId, string action, bool success, int statusCode, string? error, CancellationToken ct)
                // {
                //     if (string.IsNullOrWhiteSpace(_masterBaseUrl)) return;

                //     var url = _masterBaseUrl.TrimEnd('/') + "/api/worker/report";
                //     var payload = new
                //     {
                //         HistoryId = historyId,
                //         Action = action ?? "",
                //         Success = success,
                //         StatusCode = statusCode,
                //         Error = error
                //     };

                //     try
                //     {
                //         var resp = await _http.PostAsJsonAsync(url, payload, ct);
                //         // 不要炸流程：送不到就 log，下輪重試機制你再決定要不要做
                //         if (!resp.IsSuccessStatusCode)
                //             _log.LogWarning("[REPORT] HTTP {code} hid={hid}", resp.StatusCode, historyId);
                //     }
                //     catch (Exception ex) when (ex is not OperationCanceledException)
                //     {
                //         _log.LogWarning(ex, "[REPORT] send failed hid={hid}", historyId);
                //     }
                // }
    private async Task ReportToMasterAsync(int historyId, string action, bool success, int statusCode, string? error, CancellationToken ct)
    {
        // 如果是 Master 角色，且 MasterBaseUrl 未設定，可能不需要透過 HTTP 彙報
        // 但在 Master-Slave 架構下，Slave 一定要拿到工廠產生的 Client
        var client = _httpClientFactory.CreateClient("MasterClient");

        var payload = new
        {
            HistoryId = historyId,
            Action = action ?? "",
            Success = success,
            StatusCode = statusCode,
            Error = error
        };

        try
        {
            // 注意：這裡 url 不需要再拼接 _masterBaseUrl，
            // 因為在 Program.cs 的 AddHttpClient 中已經設定過 BaseAddress 了
            var resp = await client.PostAsJsonAsync("api/worker/report", payload, ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 走到這裡代表 Polly 已經重試了 3 次（或你設定的次數）依然失敗
                _log.LogError("[REPORT_FINAL_FAIL] 經過自動重試後仍失敗: HTTP {code} hid={hid}", resp.StatusCode, historyId);
            }
            else
            {
                // 成功（可能是第一次成功，也可能是重試後成功）
                _log.LogInformation("[REPORT_SUCCESS] hid={hid} act={act}", historyId, action);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 捕獲徹底連不上的嚴重異常
            _log.LogError(ex, "[REPORT_EX] 彙報發生連線異常 hid={hid}", historyId);
        }
    }
        

        }
    
    }
