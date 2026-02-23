using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
namespace FileMoverWeb.Services
{
    /// <summary>
    /// 遠端進度：把進度 POST 回 Master（由 Master 統一 SSE 推給 UI）。
    /// 需要 Master 端有對應 API：
    ///   POST /api/progress/report/init
    ///   POST /api/progress/report/complete
    /// </summary>
    public sealed class RemoteProgressSink : IProgressSink
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl; // e.g. http://192.168.30.118:5089
        private readonly ILogger<RemoteProgressSink> _log;
        private readonly string _who = Environment.MachineName; // 先用主機名當辨識
     
        public async Task EnsureTotalAsync(string jobId, string destId, long totalBytes, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(jobId)) return;
                if (string.IsNullOrWhiteSpace(destId)) return;
                if (totalBytes <= 0) return;
                 _log.LogDebug(
                "[PROGRESS][TOTAL] who={who} job={job} dest={dest} total={bytes}",
                _who, jobId, destId, totalBytes);
                var payload = new
                {
                    JobId = jobId,
                    DestId = destId,
                    TotalBytes = totalBytes
                };

                try
                {
                    await _http.PostAsJsonAsync(Url("/api/progress/report/ensure-total"), payload, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                    "[PROGRESS][TOTAL-FAIL] who={who} job={job} dest={dest}",
                    _who, jobId, destId); // progress 失敗不影響搬檔
                }
                
            }

        
        public RemoteProgressSink(HttpClient http, string masterBaseUrl, ILogger<RemoteProgressSink> log)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _baseUrl = (masterBaseUrl ?? string.Empty).Trim();
            _baseUrl = _baseUrl.TrimEnd('/');
            _log = log ?? throw new ArgumentNullException(nameof(log));
            if (string.IsNullOrWhiteSpace(_baseUrl))
                throw new ArgumentException("masterBaseUrl is empty.", nameof(masterBaseUrl));
        }

        private string Url(string path) => _baseUrl + path;

        public async Task InitTotalsAsync(string jobId, Dictionary<string, long> totalsByDest, CancellationToken ct)
        {
            _log.LogDebug(
            "[PROGRESS][INIT] who={who} job={job} dests={count}",
            _who, jobId, totalsByDest?.Count ?? 0);
            if (string.IsNullOrWhiteSpace(jobId)) return;

            // totalsByDest 允許空（那就只是不顯示百分比）
            var payload = new
            {
                JobId = jobId,
                TotalsByDest = totalsByDest ?? new Dictionary<string, long>()
            };

            try
            {
                await _http.PostAsJsonAsync(Url("/api/progress/report/init"), payload, ct);
            }
            catch
            {
                // progress 失敗不應該炸掉搬檔：忽略，下一次再回報
            }
        }

        public async Task AddCopiedAsync(string jobId, string destId, long deltaBytes, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return;
            if (string.IsNullOrWhiteSpace(destId)) return;
            if (deltaBytes <= 0) return;

            var payload = new
            {
                JobId = jobId,
                DestId = destId,
                DeltaBytes = deltaBytes
            };

            try
            {
                await _http.PostAsJsonAsync(Url("/api/progress/report/delta"), payload, ct);
            }
            catch
            {
                // 同上：不影響搬檔主流程
            }
        }

        public async Task CompleteJobAsync(string jobId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(jobId)) return;
             _log.LogInformation(
                "[PROGRESS][DONE] who={who} job={job}",
                _who, jobId);
            var payload = new { JobId = jobId };

            try
            {
                await _http.PostAsJsonAsync(Url("/api/progress/report/complete"), payload, ct);
            }
            catch
            {
                // 同上
            }
        }
    }
}
