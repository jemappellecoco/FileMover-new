using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Data;                    // ✅ DB
using Dapper;                         // ✅ Dapper
using Microsoft.Data.SqlClient;        // ✅ SqlClient
using FluentFTP;
using Microsoft.Extensions.Configuration;

namespace FileMoverWeb.Services
{
    public sealed class FtpSetting
    {
        private readonly IConfiguration _cfg;
        private readonly ICancelStore _cancelStore;
        private readonly ILogger<FtpSetting> _log; 

        // ✅ 你要寫死帳密（改成你要的值）
        private const string FIXED_USER = "pbs";
        private const string FIXED_PASS = "pbs";
        // private const string FIXED_USER = "coco";
        // private const string FIXED_PASS = "123456";
        public FtpSetting(IConfiguration cfg, ICancelStore cancelStore, ILogger<FtpSetting> log)
        {
            _cfg = cfg;
            _cancelStore = cancelStore;
            _log = log;
        }

        private IDbConnection OpenConn()
            // ⚠️ 如果你 connection string 名稱不是 Default，這裡改成你專案實際的名字
            => new SqlConnection(_cfg.GetConnectionString("Default"));

        // ===== Model =====
        public sealed class FtpEndpoint
        {
            public string Host { get; set; } = "";
            public int Port { get; set; } = 21;
            public string BasePath { get; set; } = "/";
            public string User { get; set; } = "";
            public string Pass { get; set; } = "";
        }

        // ✅ DB row
        private sealed class StorageRow
        {
            public string storage_name { get; set; } = "";
            public string location { get; set; } = "";
        }

        // ===== Read config (改讀 DB: Storage.type='IC') =====
        public FtpEndpoint GetIcEndpoint(string storageName)
        {
            if (string.IsNullOrWhiteSpace(storageName))
                throw new ArgumentException("storageName is empty", nameof(storageName));

            const string sql = @"
SELECT TOP 1 storage_name, location
FROM [MCRMamSystem].[dbo].[Storage]
WHERE type = 'IC' AND storage_name = @storageName;
";

            using var conn = OpenConn();
            var row = conn.QueryFirstOrDefault<StorageRow>(sql, new { storageName });

            if (row == null || string.IsNullOrWhiteSpace(row.location))
                throw new InvalidOperationException($"找不到 IC 設定：{storageName}（Storage.type=IC）");

            return new FtpEndpoint
            {
                Host = row.location.Trim(),  // ✅ 你 DB 的 location 放 IP
                Port = 21,
                BasePath = "/",              // ✅ 你說不用 base_path → 一律根目錄
                User = FIXED_USER,           // ✅ 寫死
                Pass = FIXED_PASS
            };
        }

        // ===== Path =====
        public Uri BuildFtpUri(
            FtpEndpoint ep,
            string userBit,
            string? extension
        )
        {
            if (string.IsNullOrWhiteSpace(userBit))
                throw new ArgumentException("userBit is empty", nameof(userBit));
            if (string.IsNullOrWhiteSpace(extension))
                throw new ArgumentException("extension is empty", nameof(extension));

            var ext = extension.StartsWith(".")
                ? extension
                : "." + extension;

            var file = $"{userBit}{ext}";

            var basePath = (ep.BasePath ?? "/")
                .Replace("\\", "/")
                .TrimEnd('/');

            var path = string.IsNullOrEmpty(basePath)
                ? $"/{file}"
                : $"{basePath}/{file}";

            return new Uri($"ftp://{ep.Host}:{ep.Port}{path}");
        }

        // ===== Upload =====
        /// <summary>
        /// 上傳檔案到 FTPS 伺服器。
        /// 注意：因 FluentFTP 53.0.2 不支援 CancellationToken，此處使用 Disconnect 繞過中斷上傳。
        /// </summary>
public async Task UploadAsync(
    string srcPath,
    FtpEndpoint ep,
    Uri uri,
    int historyId,
    CancellationToken ct,
    Action<long, long>? onProgress = null
)
{
    long totalBytes = 0;
    try { totalBytes = new FileInfo(srcPath).Length; } catch { totalBytes = 0; }

    // OpenWrite 用相對路徑（避免 550 / root 問題）
    var remotePath = uri.AbsolutePath.Replace("\\", "/").TrimStart('/');

    using var client = new FtpClient(ep.Host)
    {
        Port = ep.Port,
        Credentials = new NetworkCredential(ep.User, ep.Pass),
    };

    client.Config.EncryptionMode = FtpEncryptionMode.None;
    client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

    // 重要：timeout 不要太長，卡住時才能快點跳
    client.Config.ConnectTimeout = 10_000;
    client.Config.ReadTimeout = 30_000;
    client.Config.DataConnectionConnectTimeout = 10_000;
    client.Config.DataConnectionReadTimeout = 30_000;
    client.Config.SocketKeepAlive = true;

    Stream? remote = null;

    void ThrowIfCanceled()
    {
        ct.ThrowIfCancellationRequested();
        if (_cancelStore.ShouldCancel(historyId))
            throw new OperationCanceledException("Canceled by user");
    }

    using var reg = ct.Register(() =>
    {
        // ✅ 一定要把 data stream 也砍掉
        try { remote?.Dispose(); } catch { }
        try { client.Dispose(); } catch { }
    });

    try
    {
        ThrowIfCanceled();
        client.Connect();
        ThrowIfCanceled();

        // 建資料夾（如果有需要）
        var dir = Path.GetDirectoryName(remotePath)?.Replace("\\", "/");
        if (!string.IsNullOrWhiteSpace(dir) && dir != ".")
            client.CreateDirectory(dir, true);

        using var local = File.OpenRead(srcPath);
        remote = client.OpenWrite(remotePath); // 這是 data channel

        var buffer = new byte[256 * 1024];
        long sent = 0;

        while (true)
        {
            ThrowIfCanceled();

            int read = await local.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0) break;

            // WriteAsync 沒有 token 就用同步 write，但靠 Dispose(remote) 強制中止
            remote.Write(buffer, 0, read);

            sent += read;
            onProgress?.Invoke(sent, totalBytes);
        }

        remote.Flush();
        ThrowIfCanceled();
    }
    catch (OperationCanceledException)
    {
        // 取消就丟回去給 MoveWorker -> 999
        throw;
    }
    catch (Exception ex)
    {
        throw new IOException($"FTP upload failed. host={ep.Host}, remote={remotePath}, local={srcPath}. {ex.Message}", ex);
    }
    finally
    {
        try { remote?.Dispose(); } catch { }
        try { if (client.IsConnected) client.Disconnect(); } catch { }
    }
}


        private void ThrowIfCanceled(int historyId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_cancelStore.ShouldCancel(historyId))
                throw new OperationCanceledException("Canceled by user", ct);
        }

        private static FtpClient NewClient(FtpEndpoint ep)
        {
            var c = new FtpClient(ep.Host)
            {
                Port = ep.Port,
                Credentials = new NetworkCredential(ep.User, ep.Pass),
            };

            c.Config.EncryptionMode = FtpEncryptionMode.None;

            // ✅ 先用 Passive；如果你環境常 550/425/連不上資料通道，就改 AutoActive 測試
            c.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
            // c.Config.DataConnectionType = FtpDataConnectionType.AutoActive;

            c.Config.ConnectTimeout = 10_000;
            c.Config.ReadTimeout = 30_000;
            c.Config.DataConnectionConnectTimeout = 10_000;
            c.Config.DataConnectionReadTimeout = 30_000;
            c.Config.SocketKeepAlive = true;

            return c;
        }

        private static long GetRemoteSizeBestEffort(FtpEndpoint ep, string remotePath)
        {
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    using var c = NewClient(ep);
                    c.Connect();
                    long size = c.GetFileSize(remotePath);
                    c.Disconnect();
                    return size;
                }
                catch
                {
                    Thread.Sleep(400);
                }
            }
            throw new IOException($"FTP verify failed: cannot read remote size, path={remotePath}");
        }

        private static string GetFtpDir(string p)
        {
            p = p.Replace("\\", "/").TrimEnd('/');
            var idx = p.LastIndexOf('/');
            return (idx <= 0) ? "" : p.Substring(0, idx);
        }

        private static void TryDeleteRemoteBestEffort(FtpEndpoint ep, string remotePath)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using var c = NewClient(ep);
                    c.Connect();

                    if (c.FileExists(remotePath))
                        c.DeleteFile(remotePath);

                    c.Disconnect();
                    return;
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
        }
    }
}
