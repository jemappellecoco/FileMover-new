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
        /// ✅ 帳密寫死
        /// </summary>
        public FtpEndpoint GetIcEndpointFromTask(HistoryTask task)
        {
            if (task is null) throw new ArgumentNullException(nameof(task));

            if (!string.Equals(task.ToType, "IC", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Not IC storage. ToType={task.ToType}");

            var (host, port) = ParseIpPort(task.ToLocation);

            return new FtpEndpoint
            {
                Host = host,
                Port = port,
                BasePath = "/",
                User = FIXED_USER,
                Pass = FIXED_PASS
            };
        }

        /// <summary>
        /// ✅ 僅接受 "IP:PORT"（例如 192.168.30.25:21）
        /// </summary>
        private static (string host, int port) ParseIpPort(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new InvalidOperationException("IC ToPath is empty (expect IP or IP:PORT)");

            var s = input.Trim();
            int idx = s.IndexOf(':');

            // ✅ 新增：如果找不到冒號，直接回傳 IP 並給予預設 Port 21
            if (idx == -1)
            {
                return (s, 21); 
            }

            // 原本的嚴格檢查（僅針對「有冒號」的情況）
            // 避免出現 "192.168.1.1:" 或 ":21" 這種錯誤格式
            if (idx == 0 || idx != s.LastIndexOf(':') || idx == s.Length - 1)
                throw new InvalidOperationException($"Invalid IC address format: '{s}' (expect IP or IP:PORT)");

            var host = s.Substring(0, idx).Trim();
            var portStr = s.Substring(idx + 1).Trim();

            if (!int.TryParse(portStr, out int port) || port <= 0 || port > 65535)
                throw new InvalidOperationException($"Invalid IC port: '{portStr}'");

            return (host, port);
        }
    }
}