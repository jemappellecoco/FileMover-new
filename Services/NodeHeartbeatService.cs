using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Services;

public sealed class NodeHeartbeatService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _cfg;
    private readonly ILogger<NodeHeartbeatService> _log;

    public NodeHeartbeatService(
        IHttpClientFactory httpClientFactory,
        IConfiguration cfg,
        ILogger<NodeHeartbeatService> log)
    {
        _httpClientFactory = httpClientFactory;
        _cfg = cfg;
        _log = log;
    }

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    var role = _cfg["Cluster:Role"] ?? "Slave";
    var nodeName = _cfg["Cluster:NodeName"] ?? Environment.MachineName;

    var port = ResolveHttpPortFromKestrelUrl(_cfg["Kestrel:Endpoints:Http:Url"]) ?? 5089;
    var myIp = GetLocalIPv4();
    var endpoint = $"http://{myIp}:{port}";   // ✅ 存完整 endpoint

    var masterUrl = _cfg["Cluster:MasterBaseUrl"]?.TrimEnd('/');

    if (string.IsNullOrWhiteSpace(masterUrl))
    {
        if (role.Equals("Master", StringComparison.OrdinalIgnoreCase))
            masterUrl = endpoint;  // ✅ Master 自己打給自己
        else
        {
            _log.LogError("[HB] Slave 必須設定 Cluster:MasterBaseUrl！");
            return;
        }
    }

    var max = _cfg.GetValue<int>("Cluster:MaxConcurrency", 1);
    max = Math.Max(1, max);

    var client = _httpClientFactory.CreateClient();

    while (!stoppingToken.IsCancellationRequested)
    {
        var dto = new NodeFreeReportDto
        {
            Node = nodeName,
            IpAddress = endpoint,   // ✅ 回報完整網址
            Role = role,
            Group = _cfg["Cluster:Group"] ?? "Default",
            HostName = Environment.MachineName,
            MaxConcurrency = max,
            FreeSlots = max
        };

        await client.PostAsJsonAsync($"{masterUrl}/api/nodes/heartbeat-free", dto, stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
    }
}

    private static int? ResolveHttpPortFromKestrelUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var idx = url.LastIndexOf(':');
        if (idx < 0) return null;

        var tail = url[(idx + 1)..];
        var slash = tail.IndexOf('/');
        if (slash >= 0) tail = tail[..slash];

        return int.TryParse(tail, out var port) && port > 0 ? port : null;
    }

    private static string GetLocalIPv4()
    {
        // 優先找 Up + 非 Loopback + 有 Gateway（實體網卡通常穩）
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni =>
                ni.OperationalStatus == OperationalStatus.Up &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                ni.GetIPProperties().GatewayAddresses.Any());

        var ip = nic?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)?
            .Address.ToString();

        return string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip;
    }
}