using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Services;

sealed class ApiSlotConfigProvider : ISlotConfigProvider
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger _log;

    private int _cached = 1;
    private DateTime _expires = DateTime.MinValue;
    private readonly object _lock = new();

    public ApiSlotConfigProvider(
        IHttpClientFactory http,
        IConfiguration cfg,
        ILogger<ApiSlotConfigProvider> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    private sealed class NodeDto
    {
        public string? NodeName { get; set; }
        public int MaxConcurrency { get; set; }
    }

    public async Task<int> GetEnabledSlotsAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow < _expires)
                return _cached;
        }

        var baseUrl = (_cfg["Cluster:MasterBaseUrl"] ?? "").TrimEnd('/');
        var self    = (_cfg["Cluster:NodeName"] ?? "").Trim();

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(self))
            return _cached; // 保底，不炸 worker

        try
        {
            var client = _http.CreateClient("MasterClient");

            // 直接用你現有的 NodesController
            var nodes = await client
                .GetFromJsonAsync<List<NodeDto>>($"{baseUrl}/api/nodes", ct)
                ?? new();

            var me = nodes.FirstOrDefault(x =>
                string.Equals(x.NodeName, self, StringComparison.OrdinalIgnoreCase));

            var enabled = Math.Max(1, me?.MaxConcurrency ?? 1);

            lock (_lock)
            {
                _cached = enabled;
                _expires = DateTime.UtcNow.AddSeconds(3); // TTL 3 秒
            }

            return enabled;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "GetEnabledSlots via /api/nodes failed, fallback cached={cached}",
                _cached);

            lock (_lock)
                return _cached;
        }
    }
}
