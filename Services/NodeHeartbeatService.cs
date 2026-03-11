using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FileMoverWeb.Models.Node;
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
    // 1. 從配置讀取必要資訊
    var role = _cfg["Cluster:Role"] ?? "Slave";
    var nodeName = _cfg["Cluster:NodeName"] ?? Environment.MachineName;
    var group = _cfg["Cluster:Group"] ?? "Default";
    var maxConcurrency = Math.Max(1, _cfg.GetValue<int>("Cluster:MaxConcurrency", 1));

    // 2. 核心：直接讀取 SelfUrl (例如 http://172.16.4.42:5089)
    var selfUrl = _cfg["Cluster:SelfUrl"]?.TrimEnd('/');

    if (string.IsNullOrWhiteSpace(selfUrl))
    {
        _log.LogCritical("[HB] 啟動失敗：偵測到多網卡環境，必須在 appsettings.json 中明確設定 'Cluster:SelfUrl'！");
        // 這裡可以選擇 Return 停止 Service，確保不會帶著錯誤的資訊向 Master 報名
        return; 
    }

    // 3. 處理 Master 網址
    var masterUrl = _cfg["Cluster:MasterBaseUrl"]?.TrimEnd('/');
    if (string.IsNullOrWhiteSpace(masterUrl))
    {
        // 如果是 Master 角色，MasterUrl 就是自己
        if (role.Equals("Master", StringComparison.OrdinalIgnoreCase))
        {
            masterUrl = selfUrl;
        }
        else
        {
            _log.LogError("[HB] Slave 啟動失敗：缺少 'Cluster:MasterBaseUrl' 設定。");
            return;
        }
    }

    _log.LogInformation("[HB] 節點啟動成功- 節點名稱: {Node}, 角色: {Role}, 自己IP SelfUrl : {SelfUrl}", nodeName, role, selfUrl);

    var client = _httpClientFactory.CreateClient();

    // 4. 心跳迴圈
    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            var dto = new NodeFreeReportDto
            {
                Node = nodeName,
                IpAddress = selfUrl, // Master 會根據這個位址連回來下指令
                Role = role,
                Group = group,
                HostName = Environment.MachineName,
                MaxConcurrency = maxConcurrency
            };

            var response = await client.PostAsJsonAsync(
                $"{masterUrl}/api/nodes/heartbeat-free",
                dto,
                stoppingToken);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("[HB] 回報失敗，Master 回傳狀態碼: {Code}", response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
            break; // 正常關閉
        }
        catch (Exception ex)
        {
            _log.LogWarning("[HB] Master 暫時連不通 ({Url}): {Msg}", masterUrl, ex.Message);
        }

        // 固定的心跳間隔
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}
// protected override async Task ExecuteAsync(CancellationToken stoppingToken)
// {
//     var role = _cfg["Cluster:Role"] ?? "Slave";
//     var nodeName = _cfg["Cluster:NodeName"] ?? Environment.MachineName;

//     var port = ResolveHttpPortFromKestrelUrl(_cfg["Kestrel:Endpoints:Http:Url"]) ?? 5089;
//     var myIp = GetLocalIPv4();
//     var endpoint = $"http://{myIp}:{port}";   // ✅ 存完整 endpoint

//     var masterUrl = _cfg["Cluster:MasterBaseUrl"]?.TrimEnd('/');

//     if (string.IsNullOrWhiteSpace(masterUrl))
//     {
//         if (role.Equals("Master", StringComparison.OrdinalIgnoreCase))
//             masterUrl = endpoint;  // ✅ Master 自己打給自己
//         else
//         {
//             _log.LogError("[HB] Slave 必須設定 Cluster:MasterBaseUrl！");
//             return;
//         }
//     }

//     var max = _cfg.GetValue<int>("Cluster:MaxConcurrency", 1);
//     max = Math.Max(1, max);

//     var client = _httpClientFactory.CreateClient();
//     while (!stoppingToken.IsCancellationRequested)
//     {
//         try
//         {
//             var dto = new NodeFreeReportDto
//             {
//                 Node = nodeName,
//                 IpAddress = endpoint,
//                 Role = role,
//                 Group = _cfg["Cluster:Group"] ?? "Default",
//                 HostName = Environment.MachineName,
//                 MaxConcurrency = max,
//             };

//             await client.PostAsJsonAsync(
//                 $"{masterUrl}/api/nodes/heartbeat-free",
//                 dto,
//                 stoppingToken);
//         }
//         catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
//         {
//             // ✅ Ctrl+C / StopAsync 進來的正常取消：安靜退出
//             break;
//         }
//         catch (HttpRequestException ex) when (stoppingToken.IsCancellationRequested)
//         {
//             // ✅ 關機途中 master 先停很常見：不要噴錯
//             break;
//         }
//         catch (Exception ex)
//         {
//             // ✅ 平常連不到：不要把 Host 弄死，只警告
//             _log.LogInformation("[HB] master offline: {MasterUrl}", masterUrl);
//         }

//         try
//         {
//             await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
//         }
//         catch (OperationCanceledException)
//         {
//             break;
//         }
//     }
// }

    // private static int? ResolveHttpPortFromKestrelUrl(string? url)
    // {
    //     if (string.IsNullOrWhiteSpace(url)) return null;
    //     var idx = url.LastIndexOf(':');
    //     if (idx < 0) return null;

    //     var tail = url[(idx + 1)..];
    //     var slash = tail.IndexOf('/');
    //     if (slash >= 0) tail = tail[..slash];

    //     return int.TryParse(tail, out var port) && port > 0 ? port : null;
    // }

    // private static string GetLocalIPv4()
    // {
    //     // 優先找 Up + 非 Loopback + 有 Gateway（實體網卡通常穩）
    //     var nic = NetworkInterface.GetAllNetworkInterfaces()
    //         .FirstOrDefault(ni =>
    //             ni.OperationalStatus == OperationalStatus.Up &&
    //             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
    //             ni.GetIPProperties().GatewayAddresses.Any());

    //     var ip = nic?.GetIPProperties().UnicastAddresses
    //         .FirstOrDefault(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)?
    //         .Address.ToString();

    //     return string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip;
    // }
}