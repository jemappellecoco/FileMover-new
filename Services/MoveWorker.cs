// Services/MoveWorker.cs
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using FileMoverWeb.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace FileMoverWeb.Services
{
    public sealed class MoveWorker
    {   private readonly IProgressSink _sink;    
      
        private readonly ILogger<MoveWorker> _logger;
        private readonly IConfiguration _cfg;   // ⭐ 真的存下來，RunAsync 要用

        // ===== 調整參數（視環境可微調） =====
        private const int REPORT_INTERVAL_MS = 300;               // 至少每 300ms 回報一次
        private const long REPORT_BYTES_STEP = 4L * 1024 * 1024;  // 或每累積 ≥ 4 MB 回報
        private readonly ICancelStore _cancelStore;
        private readonly FtpSetting _ftp;
        public MoveWorker(
                IProgressSink sink,
                IJobProgress progress,
                ILogger<MoveWorker> logger,
                IConfiguration cfg,
                ICancelStore cancelStore,
                FtpSetting ftp)
            {
                _sink = sink;
                // _progress = progress;
                _logger = logger;
                _cfg = cfg;
                _cancelStore = cancelStore;
                _ftp = ftp;
            }
        
        /// <summary>
        /// 執行一個搬運批次，回傳每筆結果。
        /// </summary>
        public Task<List<MoveResult>> RunAsync(
            MoveBatchRequest req, 
            CancellationToken ct = default)
            => RunAsync(req, onItemDone: null, ct);

        /// <summary>
        /// 執行一個搬運批次，回傳每筆結果，並可在每筆完成時回呼 onItemDone。
        /// </summary>
        public async Task<List<MoveResult>> RunAsync(
        MoveBatchRequest req,
        Func<MoveResult, Task>? onItemDone,
        CancellationToken ct = default)
    {
        if (req is null) throw new ArgumentNullException(nameof(req));
        if (req.Items is null || req.Items.Count == 0)
            return new List<MoveResult>(0);

    
    // 預估總量（跟以前一樣，給 progress 用）
    var totals = req.Items
        .GroupBy(i => i.DestId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            g => g.Key,
           
            g => g.Sum(i =>
            
{
            // ✅ 新增：如果是 move，直接硬寫 100 代表 100%，不檢查實體檔案
            var act = (i.Action ?? "").Trim().ToLowerInvariant();
            if (act == "move")
            {
                _logger.LogInformation("[{job}] totals: action is MOVE, force total=100 (HistoryId={hid})", req.JobId, i.HistoryId);
                return 100L; 
            }

            try
            {
                _logger.LogDebug("[{job}] totals: stat begin src={src}", req.JobId, i.SourcePath);
                var fi = new FileInfo(i.SourcePath);
                
                // 只有非 move (如 copy) 才需要判斷 exists
                if (!fi.Exists) 
                {
                    _logger.LogDebug("[{job}] totals: file not found, return 0 for copy. src={src}", req.JobId, i.SourcePath);
                    return 0L;
                }

                var len = fi.Length;
                _logger.LogDebug("[{job}] totals: after Length={len} src={src}", req.JobId, len, i.SourcePath);
                return len;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{job}] totals: stat failed src={src}", req.JobId, i.SourcePath);
                return 0L;
            }
        }),

            StringComparer.OrdinalIgnoreCase
        );
        _logger.LogInformation("[{Job}] totals={Totals}",
        req.JobId,
        string.Join(", ", totals.Select(kv => $"{kv.Key}:{kv.Value}")));
        // _progress.InitTotals(req.JobId, totals);
        await _sink.InitTotalsAsync(req.JobId, totals, ct);

        var bag = new ConcurrentBag<MoveResult>();

        try
        {
            // 一個 destId 一次搬一組（slot 已經控好併行數了）
            foreach (var g in req.Items.GroupBy(i => i.DestId, StringComparer.OrdinalIgnoreCase))
            {
                await MoveGroupAsync(
                    req.JobId,
                    g.Key,
                    g.ToList(),
                    bag,
                    onItemDone,
                    ct
                ).ConfigureAwait(false);
            }

            return bag.ToList();
        }
        finally
        {
            // ⭐ 不管成功 / 失敗 / 被使用者取消，這一批 job 都結束了 → 把進度清掉
        
            await _sink.CompleteJobAsync(req.JobId, ct);
        }
    }
        

    private async Task MoveGroupAsync(
        string jobId,
        string destId,
        List<MoveItem> items,
        ConcurrentBag<MoveResult> results,
        Func<MoveResult, Task>? onItemDone,
        CancellationToken ct)
    {
    foreach (var item in items)
        {
        ct.ThrowIfCancellationRequested();
        var histId = item.HistoryId ?? 0;
         // === 使用者取消 ===
                if (_cancelStore.ShouldCancel(histId))
                {
                    var cancelResult = new MoveResult
                    {
                        HistoryId  = histId,
                        Success    = false,
                        StatusCode = 999,
                        Error      = "Canceled by user"
                    };

                    results.Add(cancelResult);
                    // _cancelStore.Clear(histId);

                    if (onItemDone != null)
                        await onItemDone(cancelResult).ConfigureAwait(false);
                        return;
                    // continue; // 跳過此筆
                }
                // === 2. 在印 Log 之前，先判定是否為 Move 並推送 50% ===
    bool isMove = string.Equals((item.Action ?? "").Trim(), "move", StringComparison.OrdinalIgnoreCase);
    
    
        _logger.LogInformation("[MOVE] job={job} hid={hid} src={src} dst={dst} toType={toType} destId={destId}",
                jobId, histId, item.SourcePath, item.DestPath, item.ToType, destId);

        MoveResult result;

        try
        {
            // 路徑拼不出來（多半是沒 FileData / 沒 UserBit） → 911
            if (string.IsNullOrWhiteSpace(item.SourcePath))
            {
                _logger.LogDebug(
                    "[{Job}] Source path empty (HistoryId={HistoryId})，多半是缺 FileData/UserBit。",
                    jobId, item.HistoryId);

                result = new MoveResult
                {
                    HistoryId  = item.HistoryId ?? 0,
                    Success    = false,
                    StatusCode = 911,
                    Error      = "Source path empty (no FileData/UserBit)"
                };
            }
            // 911：來源不存在
            else if (!File.Exists(item.SourcePath))
            {
                _logger.LogDebug("[{Job}] Source not found: {Src}", jobId, item.SourcePath);

                result = new MoveResult
                {
                    HistoryId  = item.HistoryId ?? 0,
                    Success    = false,
                    StatusCode = 911,
                    Error      = $"Source not found: {item.SourcePath}"
                };
            }
            


           else
                {                       
                  // ★ 在真正搬檔之前，確認來源檔案大小是否穩定
                    
                  _logger.LogDebug("[MOVE-CHECK] job={job} hid={hid} check stable src={src}", jobId, histId, item.SourcePath);
                    var stable = await WaitFileSizeStableAsync(
                        item.SourcePath,
                        histId,
                        probes: 3,
                        intervalMs: 800,
                        ct: ct);

                    if (!stable)
                        {
                            _logger.LogWarning("[{Job}] [SKIP] hid={hid} size not stable src={src}", jobId, histId, item.SourcePath);
                            // _logger.LogDebug("[{Job}] Source file still changing, skip move: {Src}", jobId, item.SourcePath);

                            result = new MoveResult
                            {
                                HistoryId  = item.HistoryId ?? 0,
                                Success    = false,
                                StatusCode = 912,
                                Error      = "Source file still changing (size not stable)"
                            };
                        }
                        else
                        {
                            // ✅ 檔案穩定了，才開始真正搬
                            if (string.Equals(item.ToType, "IC", StringComparison.OrdinalIgnoreCase))
                            {
                                if (string.IsNullOrWhiteSpace(item.ToName))
                                    throw new InvalidOperationException("ToName(storage_name) is empty for IC storage");
                                if (string.IsNullOrWhiteSpace(item.UserBit))
                                    throw new InvalidOperationException("UserBit is empty for IC upload");

                                var ep  = _ftp.GetIcEndpoint(item.ToName);
                                var uri = _ftp.BuildFtpUri(ep, item.UserBit, item.Extension);
                                _logger.LogInformation("[IC-START] job={job} hid={hid} src={src} toName={toName} uri={uri}",
                                jobId, histId, item.SourcePath, item.ToName, uri);
                               
                                // ✅ FTP 上傳 + 進度回報（用 AddCopiedAsync 類似你 CopyFileAsync 的方式）
                               long last = 0;
                                object gate = new object();
                                // using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                // var ftpCt = cts.Token;
                                await _ftp.UploadAsync(item.SourcePath, ep, uri, historyId: histId, ct, (copied, _total) =>
                                   
                                    {
                                        long delta = 0;
                                        lock (gate)
                                        {
                                            delta = copied - last;
                                            if (delta > 0) last = copied;
                                            else delta = 0;
                                        }

                                        if (delta > 0)
                                        {
                                            _sink.AddCopiedAsync(jobId, destId, delta, CancellationToken.None)
                                                .GetAwaiter().GetResult();
                                        }
                                    }
                                ).ConfigureAwait(false);
                                _logger.LogInformation("[IC-DONE] job={job} hid={hid} src={src}",
                                    jobId, histId, item.SourcePath);
                            }
                            else
                        {
                                var finalPath = NormalizeDestPath(item.SourcePath, item.DestPath, item.Action);
                                var tempPath  = BuildTempPath(finalPath);

                        // bool isMove = string.Equals((item.Action ?? "").Trim(), "move", StringComparison.OrdinalIgnoreCase);

                        // ✅ 一律確保 tempDir 存在
                        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

                        long totalBytes = 0;
                        try { totalBytes = new FileInfo(item.SourcePath).Length; } catch { totalBytes = 0; }

                        // ✅ copy 才用 bytes total
                        if (!isMove && totalBytes > 0)
                            await _sink.EnsureTotalAsync(jobId, destId, totalBytes, ct).ConfigureAwait(false);

                        if (isMove)
                        {

                            // ✅ 1. 先確認來源檔案是否可以「獨佔開啟」
                            // 這裡調用你之前寫的 WaitFileFreeAsync (或是簡單的 FileStream 測試)
                            // 確保沒有人正在讀取或寫入來源檔，避免 move 到一半失敗
                            bool srcFree = await FileHelper.WaitFileFreeAsync(item.SourcePath, 3000); 
                            if (!srcFree)
                            {
                                throw new IOException($"來源檔案正被佔用，無法執行 Move: {item.SourcePath}");
                            }
                                                    // ✅ move 固定 percent 模式：total=100
                            await _sink.EnsureTotalAsync(jobId, destId, 100, ct).ConfigureAwait(false);

                            // ✅ 真正開始搬（在 File.Move 前）→ 50%
                            await _sink.AddCopiedAsync(jobId, destId, 50, ct).ConfigureAwait(false);

                            _logger.LogInformation("[MOVE-TEMP] job={job} hid={hid} src={src} temp={temp}",
                                jobId, histId, item.SourcePath, tempPath);
                            // move 不會覆蓋 有的話要先刪
                            if (File.Exists(tempPath)) File.Delete(tempPath);
                            File.Move(item.SourcePath, tempPath);

                            // NAS 快取延遲保險
                            for (int retry = 0; retry < 3 && !File.Exists(tempPath); retry++)
                                await Task.Delay(200, ct);

                            // ✅ move 完成補滿 → 100%
                            await _sink.AddCopiedAsync(jobId, destId, 50, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogInformation("[COPY-TEMP] job={job} hid={hid} src={src} temp={temp}",
                                jobId, histId, item.SourcePath, tempPath);

                            await CopyFileAsync(jobId, destId, item.SourcePath, tempPath, histId, ct);
                        }

                        }

                            // ⭐ 成功一定要在這裡 set result
                            result = new MoveResult
                            {
                                HistoryId  = item.HistoryId ?? 0,
                                Success    = true,
                                // StatusCode = 3,
                                Error      = null
                                
                            };
                            _logger.LogInformation("[MOVE-DONE] job={job} hid={hid}  (ready for finalize)",
                        jobId, histId);
                            
                        }
                            

                }
                }
        catch (OperationCanceledException ex)
        {
            // 👇 這邊用 Warning 就好，代表是使用者要求的中止
            _logger.LogWarning(ex, "[{Job}] 搬運已被使用者取消：{Src}", jobId, item.SourcePath);

            result = new MoveResult
            {
                HistoryId  = item.HistoryId ?? 0,
                Success    = false,
                StatusCode = 999,                 // ⭐ 關鍵：用 999 表示「使用者取消」
                Error      = "Canceled by user"
            };
        }
        catch (IOException ex) when (IsSharingOrLockViolation(ex))   // 912
        {
            _logger.LogWarning(ex, "[{Job}] 檔案使用中（搬移失敗）：{Src}", jobId, item.SourcePath);
            result = new MoveResult
            {
                HistoryId  = item.HistoryId ?? 0,
                Success    = false,
                StatusCode = 912,
                Error      = ex.Message
            };
        }
        catch (DirectoryNotFoundException ex)                       // 914
        {
            _logger.LogWarning(ex, "[{Job}] 目的地路徑不存在（搬移失敗）：{Src}", jobId, item.SourcePath);
            result = new MoveResult
            {
                HistoryId  = item.HistoryId ?? 0,
                Success    = false,
                StatusCode = 914,
                Error      = ex.Message
            };
        }
        catch (UnauthorizedAccessException ex)                       // 913
        {
            _logger.LogWarning(ex, "[{Job}] 權限不足（搬移失敗）：{Src}", jobId, item.SourcePath);
            result = new MoveResult
            {
                HistoryId  = item.HistoryId ?? 0,
                Success    = false,
                StatusCode = 913,
                Error      = ex.Message
            };
        }
        catch (Exception ex)                                        // 91
        {
            _logger.LogError(ex, "[{Job}] 搬運失敗：{Src}", jobId, item.SourcePath);
            result = new MoveResult
            {
                HistoryId  = item.HistoryId ?? 0,
                Success    = false,
                StatusCode = 91,
                Error      = ex.Message
            };
        }

        // ⭐ 不管成功/失敗，都統一在這裡加入 results + 呼叫 callback
        results.Add(result);
        _logger.LogInformation("[MOVE-RESULT] job={job} hid={hid} ok={ok} code={code} err={err}",
    jobId, result.HistoryId, result.Success, result.StatusCode, result.Error);
        if (onItemDone != null)
        {
            try
    {
        await onItemDone(result).ConfigureAwait(false);
    }
    catch (Exception cbEx)
    {
        _logger.LogError(cbEx,
            "[{Job}] onItemDone crashed (HistoryId={hid}) — callback ignored to avoid job stuck at status=1",
            jobId, result.HistoryId);
        // ⭐不要 throw
    }
        }
    }
}

        /// <summary>
        /// 檔案大小在固定時間內維持不變才視為「穩定」
        /// 例：probes=3, intervalMs=800 → 約 1.6 秒內都沒有變化
        /// </summary>
        // private static async Task<bool> WaitFileSizeStableAsync(
        private async Task<bool> WaitFileSizeStableAsync(
            string path,
            int historyId,
            int probes = 3,
            int intervalMs = 800,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!File.Exists(path))
                return false;

            long? lastSize = null;

            for (int i = 0; i < probes; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_cancelStore.ShouldCancel(historyId))   // ← 需要 historyId
                throw new OperationCanceledException("Canceled by user");
                
                long size;
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists)
                        return false;

                    size = fi.Length;
                    _logger.LogDebug("[STABLE] hid={hid} probe={i}/{probes} size={size} path={path}",
                    historyId, i + 1, probes, size, path);
                }
                catch
                {
                    // 讀不到大小就當作不穩定
                    return false;
                }

                if (lastSize.HasValue && size != lastSize.Value)
                {
                    // 任兩次量測不一致 → 視為正在變化
                    _logger.LogWarning("[STABLE-NG] hid={hid} size changed {last}->{now} path={path}",
                        historyId, lastSize, size, path);
                    return false;
                }

                lastSize = size;

                // 最後一次不用再等
                if (i < probes - 1)
                    await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }

            return true;
        }

    private async Task CopyFileAsync(
        string jobId,
        string destId,
        string srcPath,
        string dstPath,
        int historyId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(srcPath))
            throw new ArgumentException("SourcePath 不能為空白", nameof(srcPath));
        if (string.IsNullOrWhiteSpace(dstPath))
            throw new ArgumentException("DestPath 不能為空白", nameof(dstPath));

        if (!File.Exists(srcPath))
            throw new FileNotFoundException("Source not found", srcPath);

        var destDir = Path.GetDirectoryName(dstPath)
                    ?? throw new InvalidOperationException($"DestPath 無法取得目錄：{dstPath}");

        Directory.CreateDirectory(destDir);
        
        // ✅ 這裡加（開始 copy 前）
        _logger.LogInformation("[COPY-START] job={job} dest={dest} hid={hid} src={src} dst={dst}",
            jobId, destId, historyId, srcPath, dstPath);
        // 來源大小（如果之後想比對可以用）
        long srcSize = 0;
        try
        {
            srcSize = new FileInfo(srcPath).Length;
        }
        catch
        {
            srcSize = 0;
        }
        //   await _sink.EnsureTotalAsync(jobId, destId, srcSize, ct);

        bool success = false;   // ⭐用來判斷要不要刪 dst 檔
        bool totalReported = false;
        try
        {
            // 來源：只讀，允許別人讀
            using var inFs = new FileStream(
                srcPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                useAsync: true);
            // ✅ FileInfo 取不到就用 inFs.Length（更穩）
            if (srcSize <= 0)
            {
                try { srcSize = inFs.Length; } catch { srcSize = 0; }
            }

            // ✅ 只要 >0 才送 total（避免 sink 端 total<=0 直接丟掉）
            if (srcSize > 0 && !totalReported)
            {
                await _sink.EnsureTotalAsync(jobId, destId, srcSize, ct);
                totalReported = true;
            }
            // 目的：直接寫到最後檔名，從一開始就 truncate / create
            using var outFs = new FileStream(
                dstPath,
                FileMode.Create,     // 有檔就清空，沒有就建立
                FileAccess.Write,
                FileShare.ReadWrite,      // copy 過程可以讀取
                bufferSize: 1024 * 1024,
                useAsync: true);

            var buffer = new byte[1024 * 1024];
            int read;
            long sinceLastReport = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while ((read = await inFs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                                    .ConfigureAwait(false)) > 0)
            {
                // 使用者取消 → 丟 OCE，外層會變成 999
                if (_cancelStore.ShouldCancel(historyId)){ 
                    _logger.LogWarning("！！！ 偵測到 ID {Id} 需要取消，準備拋出異常 ！！！", historyId);
                    throw new OperationCanceledException("Canceled by user");}
            

                await outFs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sinceLastReport += read;

                bool timeOk  = sw.ElapsedMilliseconds >= REPORT_INTERVAL_MS;
                bool bytesOk = sinceLastReport >= REPORT_BYTES_STEP;

                if (timeOk || bytesOk)
                {
                    // _progress.AddCopied(jobId, destId, sinceLastReport);
                    await _sink.AddCopiedAsync(jobId, destId, sinceLastReport, ct);
                    sinceLastReport = 0;
                    sw.Restart();
                }
            }

            // if (sinceLastReport > 0)
            //     _progress.AddCopied(jobId, destId, sinceLastReport);
            if (sinceLastReport > 0)
                await _sink.AddCopiedAsync(jobId, destId, sinceLastReport, ct);
            await outFs.FlushAsync(ct).ConfigureAwait(false);

            // ⭐ 如果你想再嚴格一點，可以在這裡做 size 檢查：
            if (srcSize > 0 && outFs.Length != srcSize)
            {
                throw new IOException(
                    $"Destination size mismatch: src={srcSize}, dst={outFs.Length}");
            }

            success = true;   // ✅ 走到這裡才算成功
            _logger.LogInformation("[COPY-END] job={job} dest={dest} hid={hid} src={src} dst={dst} bytes={bytes}",
        jobId, destId, historyId, srcPath, dstPath, srcSize);
        }
        finally
        {
            // ❗只要沒成功（例外 / cancel），就刪掉 dstPath，避免留半截檔
            if (!success)
        
            {
                _logger.LogWarning("[COPY-ABORT] job={job} hid={hid} deleted partial dst={dst}",
                jobId, historyId, dstPath);
                try
                {
                    if (File.Exists(dstPath))
                        File.Delete(dstPath);
                }
                catch
                {
                    // 刪不掉就算了，至少我們有試
                }
            }
        }

        // ✅ 結果：
        // - 成功：目的端是完整新檔，舊檔被覆蓋
        // - 失敗 / 取消：目的端不會殘留修改到一半的檔案（我們會刪掉）
    }
        

        private static string BuildTempDir(string finalPath)
        {
            var finalDir = Path.GetDirectoryName(finalPath)
                ?? throw new InvalidOperationException($"finalPath 無法取得目錄：{finalPath}");
            return Path.Combine(finalDir, "temp");
        }

         private static string BuildTempPath(string finalPath)
            {
                var tempDir  = BuildTempDir(finalPath);
                var fileName = Path.GetFileName(finalPath); // 原檔名（含副檔名）
                return Path.Combine(tempDir, fileName);
            }

        private static string NormalizeDestPath(string srcPath, string destPath,string action)
        {
          if (string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase)) return "";
            if (string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("destPath 不能為空白", nameof(destPath));

            // ⭐ 強制規定：由資料庫傳進來的 destPath 必須就是最終路徑，
            // 不要再用 Directory.Exists 去檢查，避免 NAS 權限或緩存造成的誤判。
            return destPath; 
        }
        private static bool IsSharingOrLockViolation(IOException ex)
        {
            // 32: ERROR_SHARING_VIOLATION, 33: ERROR_LOCK_VIOLATION
            int code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33;
        }

    }
}
