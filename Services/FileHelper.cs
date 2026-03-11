using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileMoverWeb.Services
{
    public static class FileHelper
    {
      

        // 等待檔案釋放
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
                    // 嘗試以「獨佔模式」開啟檔案
                    // FileMode.Open: 開啟現有檔案
                    // FileAccess.ReadWrite: 需要讀寫權限
                    // FileShare.None: 不允許其他程序同時存取 (若成功代表檔案已自由)
                    using var fs = new FileStream(
                        path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (IOException)
                {
                    // IOException 通常代表檔案正被另一個程序佔用 (Lock)
                    // 等待 200 毫秒後再次嘗試
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
            // 4. 超過指定時間 (ms) 仍無法取得檔案控制權
            return false;
        }
    }
}