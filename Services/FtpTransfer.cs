// Services/FtpTransfer.cs
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using FileMoverWeb.Models;
namespace FileMoverWeb.Services
{
    public sealed class FtpTransfer
    {
        private readonly FtpSetting _setting;

        public FtpTransfer(FtpSetting setting)
        {
            _setting = setting;
        }

        /// <summary>
        /// ✅ 上傳到 IC（ToPath=IP:PORT），檔名 = UserBit + Extension
        /// ✅ remote 固定根目錄：/UserBit.ext
        /// </summary>
        public async Task UploadToIcAsync(
            HistoryTask task,
            string localPath,
            CancellationToken ct,
            Action<long, long>? onProgress = null)
        {
            if (task is null) throw new ArgumentNullException(nameof(task));
            if (string.IsNullOrWhiteSpace(localPath)) throw new ArgumentException("localPath empty", nameof(localPath));
            if (!File.Exists(localPath)) throw new FileNotFoundException($"Local file not found: {localPath}", localPath);

            // ✅ 目的端資訊（IP:PORT + 寫死帳密）
            var ep = _setting.GetIcEndpointFromTask(task);

            // ✅ 檔名：UserBit + Extension（你 HistoryTask 都有）
            if (string.IsNullOrWhiteSpace(task.UserBit))
                throw new InvalidOperationException("UserBit is empty (IC upload requires UserBit)");

            var ext = (task.Extension ?? "").Trim();
            if (!string.IsNullOrEmpty(ext) && !ext.StartsWith(".")) ext = "." + ext;

            var remotePath = $"{task.UserBit}{ext}".Trim();   // root
            if (string.IsNullOrWhiteSpace(remotePath))
                throw new InvalidOperationException("Remote filename is empty");

            long totalBytes = 0;
            try { totalBytes = new FileInfo(localPath).Length; } catch { totalBytes = 0; }

            using var client = new FtpClient(ep.Host)
            {
                Port = ep.Port,
                Credentials = new NetworkCredential(ep.User, ep.Pass),
            };

            client.Config.EncryptionMode = FtpEncryptionMode.None;
            client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

            client.Config.ConnectTimeout = 10_000;
            client.Config.ReadTimeout = 30_000;
            client.Config.DataConnectionConnectTimeout = 10_000;
            client.Config.DataConnectionReadTimeout = 30_000;
            client.Config.SocketKeepAlive = true;

            Stream? remote = null;

            // ct 取消時強制切斷 data stream / client
            using var reg = ct.Register(() =>
            {
                try { remote?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
            });

            try
            {
                ct.ThrowIfCancellationRequested();

                client.Connect();
                ct.ThrowIfCancellationRequested();

                using var local = File.OpenRead(localPath);

                // ✅ remotePath 不要加 /，用相對路徑，避免 550/root 問題
                remote = client.OpenWrite(remotePath);

                var buffer = new byte[256 * 1024];
                long sent = 0;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    int read = await local.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    // FluentFTP 這條 stream 常見沒有 WriteAsync(token) → 用同步 write + 取消時 Dispose
                    remote.Write(buffer, 0, read);

                    sent += read;
                    onProgress?.Invoke(sent, totalBytes);
                }

                remote.Flush();
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"FTP upload failed. host={ep.Host}:{ep.Port}, remote={remotePath}, local={localPath}. {ex.Message}", ex);
            }
            finally
            {
                try { remote?.Dispose(); } catch { }
                try { if (client.IsConnected) client.Disconnect(); } catch { }
            }
        }
    }
}