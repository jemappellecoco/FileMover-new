using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileMoverWeb.Services
{
    public static class FileHelper
    {
      

        // 新版：支援取消 + 檔案不存在直接 true
        public static async Task<bool> WaitFileFreeAsync(string path, int ms, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (!File.Exists(path))
                return true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
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
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    // 有時候是暫時性的（例如掃毒/權限切換/網路磁碟）
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }
    }
}