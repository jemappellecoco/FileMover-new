using System;
using System.IO;
using System.Threading.Tasks;

namespace FileMoverWeb.Services // 確保 Namespace 與其他檔案一致
{
    public static class FileHelper
    {
        /// <summary>
        /// 確保檔案目前沒有被任何程序佔用
        /// </summary>
        public static async Task<bool> WaitFileFreeAsync(string path, int ms = 2000)
        {
            // ⭐ 關鍵修正：如果檔案根本不存在，代表沒有人會佔用它，直接回傳 true
            if (!File.Exists(path)) 
            {
                return true; 
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try
                {
                    // 使用 FileShare.None 嘗試強制獨佔
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return true; 
                }
                catch (IOException ex)
                {
                    // 這裡可以檢查是否為真的鎖定錯誤 (Error 32, 33)
                    await Task.Delay(200);
                }
                catch (Exception)
                {
                    // 權限不足或其他致命錯誤
                    return false;
                }
            }
            return false;
        }

       
    }
}

// static async Task<bool> WaitFileFreeAsync(string path, int ms = 2000)
//             {
//                 // ⭐ 關鍵修正：如果檔案根本不存在，代表沒有人會佔用它，直接回傳 true
//                 if (!File.Exists(path)) 
//                 {
//                     return true; 
//                 }
//                 var sw = System.Diagnostics.Stopwatch.StartNew();
//                 while (sw.ElapsedMilliseconds < ms)
//                 {
//                     try
//                     {
//                         // 使用 FileShare.None 嘗試強制獨佔
//                         using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
//                         return true; // 拿到獨占 → 代表安全，回傳成功
//                     }
//                     catch (IOException)
//                     {
//                         // 檔案被佔用中，持續輪詢
//                         await Task.Delay(200);
//                     }
//                     catch (Exception)
//                     {
//                         // 處理如權限不足等其他異常
//                         return false;
//                     }
//                 }
//                 return false; // 超時，判定為檔案仍在使用中
//             }
