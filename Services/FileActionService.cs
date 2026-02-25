// Services/FileActionWorker.cs (SINGLE ITEM VERSION)
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Models;

namespace FileMoverWeb.Services
{
    public sealed class FileActionWorker
    {
        private readonly ILogger<FileActionWorker> _log;

        public FileActionWorker(ILogger<FileActionWorker> log)
        {
            _log = log;
        }

        public async Task<FileActionResult> RunOneAsync(HistoryTask task, CancellationToken ct = default)
        {
            var hid = task.HistoryId;
            var act = (task.Action ?? "").Trim().ToLowerInvariant();

            try
            {
                ct.ThrowIfCancellationRequested();

                // 1) validate (共通)
                if (hid <= 0)
                    return Fail(hid, Err.InvalidTask, "HistoryId invalid");

                if (string.IsNullOrWhiteSpace(act))
                    return Fail(hid, Err.InvalidTask, "Action empty");

                // 2) route by action
                if (act == "copy")
                {
                    await CopyAsync(task, ct).ConfigureAwait(false);
                    return Ok(hid, Status.CopyDone);
                }

                if (act == "move")
                {
                    await MoveAsync(task, ct).ConfigureAwait(false);
                    return Ok(hid, Status.MoveDone);
                }

                if (act == "delete")
                {
                    await DeleteAsync(task, ct).ConfigureAwait(false);
                    return Ok(hid, Status.DeleteDone);
                }

                return Fail(hid, Err.InvalidTask, $"Unknown action: {act}");
            }
            catch (FileSizeMismatchException ex)
            {
                return Fail(hid, Err.SizeMismatch, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return Fail(hid, Err.Canceled, "Canceled");
            }
            catch (IOException ex) when (Err.IsSharingOrLockViolation(ex))
            {
                return Fail(hid, Err.FileInUse, ex.Message);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Fail(hid, Err.DestDirNotFound, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Fail(hid, Err.Unauthorized, ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[FileAction] fatal hid={hid}", hid);
                return Fail(hid, Err.Fatal, ex.Message);
            }
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
        internal sealed class FileSizeMismatchException : Exception
        {
            public FileSizeMismatchException(string msg) : base(msg) { }
        }
        // -------------------------
        // Actions
        // -------------------------

        private async Task CopyAsync(HistoryTask t, CancellationToken ct)
            {
                var hid = t.HistoryId;

                var src = t.FromFullPath;
                var dstFinal = t.ToFullPath;

                if (string.IsNullOrWhiteSpace(src))
                    throw new ArgumentException("FromFullPath is empty");
                if (string.IsNullOrWhiteSpace(dstFinal))
                    throw new ArgumentException("ToFullPath is empty");

                if (!File.Exists(src))
                    throw new FileNotFoundException($"Source not found: {src}", src);

                var dst = FileActionHelpers.NormalizeDestPath(dstFinal);
                var temp = FileActionHelpers.BuildTempPath(dst);

                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

                _log.LogInformation("[COPY] hid={hid} src={src} temp={temp}", hid, src, temp);

                await FileActionHelpers.CopyFileAsync(src, temp, ct).ConfigureAwait(false);

                // ✅ 等待 temp 完全釋放
                if (!await FileActionHelpers.WaitFileFreeAsync(temp, 3000, ct).ConfigureAwait(false))
                    throw new IOException("Temp file busy after copy");

                // ✅ SIZE VERIFY
                if (!VerifyTempSize(temp, t.FileSize4F, t.FileSize7F, hid))
                {
                    _log.LogWarning("[COPY] hid={hid} size verify FAILED. temp kept.", hid);

                    // ❗ 不刪 temp
                    throw new FileSizeMismatchException("Size mismatch after copy");
                }

                // ✅ VERIFY OK 才 finalize
                _log.LogInformation("[COPY] hid={hid} finalize temp={temp} -> dst={dst}", hid, temp, dst);

                FileActionHelpers.MoveReplace(temp, dst);
            }

       private async Task MoveAsync(HistoryTask t, CancellationToken ct)
            {
                var hid = t.HistoryId;

                var src = t.FromFullPath;
                var dstFinal = t.ToFullPath;

                if (string.IsNullOrWhiteSpace(src))
                    throw new ArgumentException("FromFullPath is empty");
                if (string.IsNullOrWhiteSpace(dstFinal))
                    throw new ArgumentException("ToFullPath is empty");

                if (!File.Exists(src))
                    throw new FileNotFoundException($"Source not found: {src}", src);

                var dst = FileActionHelpers.NormalizeDestPath(dstFinal);
                var temp = FileActionHelpers.BuildTempPath(dst);
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

                _log.LogInformation("[MOVE] hid={hid} src={src} temp={temp}", hid, src, temp);

                if (!await FileActionHelpers.WaitFileFreeAsync(src, 3000, ct).ConfigureAwait(false))
                    throw new IOException($"Source busy: {src}");

                if (File.Exists(temp)) File.Delete(temp);
                File.Move(src, temp);

                // ✅ VERIFY
                if (!VerifyTempSize(temp, t.FileSize4F, t.FileSize7F, hid))
                {
                    _log.LogWarning("[MOVE] hid={hid} size verify FAILED. temp kept.", hid);
                    throw new FileSizeMismatchException("Size mismatch after move");
                }

                _log.LogInformation("[MOVE] hid={hid} finalize temp={temp} -> dst={dst}", hid, temp, dst);
                FileActionHelpers.MoveReplace(temp, dst);
            }

        private Task DeleteAsync(HistoryTask t, CancellationToken ct)
        {
            var hid = t.HistoryId;
            var src = t.FromFullPath;

            if (string.IsNullOrWhiteSpace(src))
                throw new ArgumentException("FromFullPath is empty");

            _log.LogInformation("[DELETE] hid={hid} src={src}", hid, src);

            if (!File.Exists(src))
                return Task.CompletedTask;

            // 你要保守就 wait free
            // 這裡不 await 也行，但風格一致我們用 await
            return FileActionHelpers.DeleteFileSafeAsync(src, ct);
        }

        // -------------------------
        // Results
        // -------------------------

        private static FileActionResult Ok(int hid, int fileStatus) => new()
        {
            HistoryId = hid,
            Success = true,
            FileStatus = fileStatus,
            Error = null
        };

        private static FileActionResult Fail(int hid, int code, string msg) => new()
        {
            HistoryId = hid,
            Success = false,
            FileStatus = code,
            Error = msg
        };
    }

    public sealed class FileActionResult
    {
        public int HistoryId { get; set; }
        public bool Success { get; set; }
        public int FileStatus { get; set; }     // ✅ 成功=你們的完成狀態；失敗=Err code
        public string? Error { get; set; }
    }

    // ✅ 成功狀態集中（你之後要改一個地方就好）
    internal static class Status
    {
        public const int CopyDone   = 11;
        public const int MoveDone   = 13;
        public const int DeleteDone = 12;
    }

    // ✅ 錯誤碼集中（跟你 MoveWorker 一樣）
    internal static class Err
    {
       public const int SizeMismatch = 915;
       public const int Fatal           = 91;
        public const int Canceled        = 999;

        public const int InvalidTask     = 910;
        public const int SourceNotFound  = 911;

        public const int FileInUse       = 912;
        public const int Unauthorized    = 913;
        public const int DestDirNotFound = 914;

        public static bool IsSharingOrLockViolation(IOException ex)
        {
            int code = ex.HResult & 0xFFFF; // 32 sharing, 33 lock
            return code == 32 || code == 33;
        }
    }

    // ✅ Helper 抽出來（你剛剛說的重點）
    internal static class FileActionHelpers
    {
        public static string NormalizeDestPath(string destPath)
        {
            if (string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("DestPath is empty");
            return destPath;
        }

        public static string BuildTempPath(string finalPath)
        {
            var dir = Path.GetDirectoryName(finalPath)
                ?? throw new InvalidOperationException($"Cannot get dir from {finalPath}");
            var file = Path.GetFileName(finalPath);
            return Path.Combine(dir, "temp", file);
        }

        // ✅ 覆蓋式搬移（final 已存在就刪）
        public static void MoveReplace(string src, string dst)
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(dst))
                File.Delete(dst);

            File.Move(src, dst);
        }

        public static async Task<bool> WaitFileFreeAsync(string path, int timeoutMs, CancellationToken ct)
        {
            var until = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));

            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var fs = new FileStream(
                        path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (IOException)
                {
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
            }

            return false;
        }

        public static async Task DeleteFileSafeAsync(string path, CancellationToken ct)
        {
            if (!await WaitFileFreeAsync(path, 3000, ct).ConfigureAwait(false))
                throw new IOException($"File is busy, cannot delete: {path}");

            File.Delete(path);
        }

        // ✅ 單純 copy（先不含 progress；要 progress 再加一層 wrapper）
        public static async Task CopyFileAsync(string srcPath, string dstPath, CancellationToken ct)
        {
            bool success = false;

            try
            {
                using var inFs = new FileStream(
                    srcPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1024 * 1024, useAsync: true);

                using var outFs = new FileStream(
                    dstPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite,
                    bufferSize: 1024 * 1024, useAsync: true);

                var buffer = new byte[1024 * 1024];
                int read;

                while ((read = await inFs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await outFs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                await outFs.FlushAsync(ct).ConfigureAwait(false);
                success = true;
            }
            finally
            {
                if (!success)
                {
                    try { if (File.Exists(dstPath)) File.Delete(dstPath); } catch { }
                }
            }
        }
    }
}