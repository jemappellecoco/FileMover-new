using System;
using FileMoverWeb.Models;

namespace FileMoverWeb.Services
{
    public sealed class FtpSetting
    {
        // ✅ 寫死帳密，不給使用者改
        private const string FIXED_USER = "pbs";
        private const string FIXED_PASS = "pbs";

        public sealed class FtpEndpoint
        {
            public string Host { get; set; } = "";
            public int Port { get; set; } = 21;
            public string BasePath { get; set; } = "/";
            public string User { get; set; } = "";
            public string Pass { get; set; } = "";
        }

        /// <summary>
        /// ✅ ToType=IC 時，從 task.ToPath 讀 "IP:PORT"
        /// </summary>
        public FtpEndpoint GetIcEndpointFromTask(HistoryTask task)
        {
            if (task is null) throw new ArgumentNullException(nameof(task));
            // 1. 驗證儲存類型是否為 "IC"
            if (!string.Equals(task.ToType, "IC", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Not IC storage. ToType={task.ToType}");
            // 2. 解析目標位置 (ToLocation) 中的 Host 與 Port
            var (host, port) = ParseIpPort(task.ToLocation);
            // 3. 組裝端點設定，並填入帳密
            return new FtpEndpoint
            {
                Host = host,
                Port = port,
                BasePath = "/",
                User = FIXED_USER,
                Pass = FIXED_PASS
            };
        }

     
        /// 解析位址字串，支援 "IP" 或 "IP:PORT" 格式。
        /// 若未提供 Port，預設為 21。
        /// <param name="input">輸入的位址字串 (例如 "192.168.1.100:2121")</param>
        /// <returns>回傳包含 Host(string) 與 Port(int) 的元組 (Tuple)</returns>
        /// <exception cref="InvalidOperationException">當格式不正確或 Port 不合法時拋出</exception>
        private static (string host, int port) ParseIpPort(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new InvalidOperationException("IC ToPath is empty (expect IP or IP:PORT)");

            var s = input.Trim();
            int idx = s.IndexOf(':');

            // 如果找不到冒號，直接回傳 IP 並給予預設 Port 21
            if (idx == -1)
            {
                return (s, 21); 
            }
            // 狀況 B：有冒號，進行嚴格格式檢查
            // 阻擋非法格式：
            // 1. ":21" (冒號在首位)
            // 2. "192.168.1.1:21:80" (出現多個冒號)
            // 3. "192.168.1.1:" (冒號在末位)

            // 原本的嚴格檢查（僅針對「有冒號」的情況）
            // 避免出現 "192.168.1.1:" 或 ":21" 這種錯誤格式
            if (idx == 0 || idx != s.LastIndexOf(':') || idx == s.Length - 1)
                throw new InvalidOperationException($"Invalid IC address format: '{s}' (expect IP or IP:PORT)");
            // 分離 Host 與 Port 字串
            var host = s.Substring(0, idx).Trim();
            var portStr = s.Substring(idx + 1).Trim();
            // 驗證 Port 是否為合法的數字 (1 ~ 65535)
            if (!int.TryParse(portStr, out int port) || port <= 0 || port > 65535)
                throw new InvalidOperationException($"Invalid IC port: '{portStr}'");

            return (host, port);
        }
    }
}