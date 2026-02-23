using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Services
{
    public sealed class MasterSchedulerService : BackgroundService
    {
        private readonly DbConnectionFactory _factory;
        private readonly IConfiguration _cfg;
        private readonly ILogger<MasterSchedulerService> _log;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ITaskQueue _taskQueue;
        private readonly IJobProgress _progress;
        private readonly string _selfNodeName;
        public MasterSchedulerService(
            DbConnectionFactory factory,
            IConfiguration cfg, 
            ILogger<MasterSchedulerService> log,
            IHttpClientFactory httpClientFactory,
            ITaskQueue taskQueue,
            IJobProgress progress
            )
        {
            _factory = factory;
            _cfg = cfg;
            _log   = log;
            _httpClientFactory = httpClientFactory;
            _taskQueue = taskQueue;
            _progress  = progress;

             // 建議用 config 的 Cluster:NodeName，保持跟 DB 的 NodeName 一致
            _selfNodeName = cfg["Cluster:NodeName"] ?? Environment.MachineName;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var role  = _cfg["Cluster:Role"] ?? "Slave";
            var group = _cfg["Cluster:Group"] ?? "";

            // 只讓 Master 跑，保險一下
            if (!string.Equals(role, "Master", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("MasterSchedulerService not started because Role={role}", role);
                return;
            }

            var intervalSec = _cfg.GetValue<int>("Cluster:ScheduleIntervalSeconds", 15);
            if (intervalSec < 1) intervalSec = 1;

            _log.LogInformation("MasterSchedulerService started, group={group}, interval={interval}s",
                group, intervalSec);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(group, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "MasterSchedulerService loop error");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken);
                }
                catch { }
            }

            _log.LogInformation("MasterSchedulerService stopped.");
        }

        private sealed class WorkerRow
        {
            public string NodeName { get; set; } = "";
            public string GroupCode { get; set; } = "";
            public int MaxConcurrency { get; set; }
            public DateTime LastHeartbeat { get; set; }
            public string Role { get; set; } = string.Empty;
        }
        private sealed class SlotState
        {
            public string NodeName { get; set; } = "";
            public int Running { get; set; }
            public int Capacity { get; set; }
        }
private async Task RunOnceAsync(string group, CancellationToken ct)
{
    using var conn = _factory.Create();
    var db = (DbConnection)conn;
    await db.OpenAsync(ct);

    // 1) 抓目前 Online 的 worker（同一個 group）
    var timeoutSec = _cfg.GetValue<int>("Cluster:HeartbeatTimeoutSeconds", 30);
    
    // ⭐ 0) 回收離線節點任務
    var requeued = await RequeueOfflineNodeJobsAsync(conn, group, timeoutSec, ct);
    if (requeued > 0)
    {
        _log.LogWarning("[MasterScheduler] Requeued {count} stuck jobs from offline nodes (group={group})",
            requeued, group);
    }

    // 1. 從設定中讀取開關
    bool allowMasterWork = _cfg.GetValue<bool>("Cluster:AllowMasterWork", true);

    // 💡 修正：SQL 必須選取 Role 欄位，否則後續無法過濾 Master 節點
    var workersQuery = await conn.QueryAsync<WorkerRow>(@"
        SELECT NodeName, GroupCode, MaxConcurrency, LastHeartbeat, Role 
        FROM dbo.WorkerNode
        WHERE GroupCode = @GroupCode;
        ", new { GroupCode = group });

    var workers = workersQuery
        .Where(w => (DateTime.Now - w.LastHeartbeat).TotalSeconds <= timeoutSec)
        .Where(w => 
        {
            // 💡 邏輯：如果是 Master 且 allowMasterWork 為 false，則排除該節點
            if (string.Equals(w.Role, "Master", StringComparison.OrdinalIgnoreCase))
            {
                return allowMasterWork;
            }
            return true; // Slave 節點一律保留
        })
        .ToList();

    if (workers.Count == 0)
        return;

    // 2) 算各節點目前已分配多少 active 任務
    var runningDict = (await conn.QueryAsync<(string NodeName, int RunningCount)>(@"
        SELECT assigned_node AS NodeName, COUNT(*) AS RunningCount
        FROM dbo.FileData_History
        WHERE assigned_node IS NOT NULL
          AND file_status IN (1, 2, 3, 24, 27)
        GROUP BY assigned_node;
        ")).ToDictionary(x => x.NodeName, x => x.RunningCount);

    var slots = workers
        .Select(w =>
        {
            runningDict.TryGetValue(w.NodeName, out var running);
            var cap = w.MaxConcurrency - running;
            if (cap < 0) cap = 0;

            return new SlotState
            {
                NodeName = w.NodeName,
                Running  = running,
                Capacity = cap
            };
        })
        .Where(x => x.Capacity > 0)
        .ToList();

    if (slots.Count == 0)
        return;

    // 3) 依照「目前 Running 最少」優先分配
    while (slots.Any())
    {
        var target = slots.OrderBy(s => s.Running).First();

        var assigned = await AssignOneTaskAsync(conn, target.NodeName, group, ct);
        if (!assigned)
            break;

        target.Running++;
        target.Capacity--;

        slots = slots
            .Select(s => s.NodeName == target.NodeName ? target : s)
            .Where(s => s.Capacity > 0)
            .ToList();
    }
}

        

private async Task<bool> AssignOneTaskAsync(
    System.Data.IDbConnection conn,
    string nodeName,
    string group,
    CancellationToken ct)
{
    const int MAX_RETRY = 3;

    for (int attempt = 1; attempt <= MAX_RETRY; attempt++)
    {
        try
        {
            // ✅ Phase2 以 file_status=24/27 判斷，不看 action
            // ✅ UPDATE 時保留 24/27，不要覆寫成 1（避免跟 Phase1 混）
            
            var claimSql = @"
;WITH C AS (
    SELECT TOP (1) h.id
    FROM dbo.FileData_History h
    JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
    WHERE h.assigned_node IS NULL
      AND (
            -- Phase1：本樓層搬運/刪除（依來源樓層）
            (
              ((h.action IN ('copy','move') AND h.file_status IN (0,800)) OR
               (h.action = 'delete' AND h.file_status IN (-1,800)))
              AND s_from.set_group = @group
            )
            OR
            -- Phase2：已按回遷（24/27），依目的樓層群組決定誰撿
            (
                (h.file_status = 24 AND @group = '7F') OR
                (h.file_status = 27 AND @group = '4F')
            )
          )
    ORDER BY ISNULL(h.priority, 1) DESC, h.create_time ASC, h.id ASC
)
UPDATE h
SET h.assigned_node = @nodeName,
    h.update_time   = GETDATE(),
    h.file_status   = CASE WHEN h.file_status IN (24,27) THEN h.file_status 
    WHEN h.action = 'delete' THEN -1
    ELSE 1 
        END
OUTPUT INSERTED.id
FROM dbo.FileData_History h
JOIN C ON h.id = C.id;";

            var hid = await conn.QueryFirstOrDefaultAsync<int?>(
                new CommandDefinition(claimSql, new { nodeName, group }, cancellationToken: ct));

            if (hid == null) return false;

            var detailSql = @"
SELECT
    h.id AS HistoryId,
    h.file_id AS FileId,
    h.action AS Action,
    CAST(h.file_status AS int) AS FileStatus,
    h.file_type AS FileType,

    s_from.location AS FromPath,
    s_from.set_group AS FromGroup,
    s_to.location AS ToPath,
    s_to.set_group AS ToGroup,

    COALESCE(cm.UserBit, f.UserBit) AS UserBit,
    COALESCE(cm.filename, f.filename) AS FileName,
    COALESCE(cm.extension, f.extension) AS Extension,

    -- ⭐⭐⭐ 關鍵：size 一定要在 Master 查好 ⭐⭐⭐
    CAST(COALESCE(cm.filesize_4F, f.filesize_4F) AS BIGINT) AS FileSize4F,
    CAST(COALESCE(cm.filesize_7F, f.filesize_7F) AS BIGINT) AS FileSize7F,

    CAST(
        COALESCE(
            COALESCE(cm.filesize_7F, f.filesize_7F),
            COALESCE(cm.filesize_4F, f.filesize_4F),
            0
        ) AS BIGINT
    ) AS FileSize
FROM dbo.FileData_History h
JOIN dbo.Storage s_from ON s_from.id = h.from_storage_id
LEFT JOIN dbo.Storage s_to ON s_to.id = h.to_storage_id
LEFT JOIN dbo.CMData cm ON cm.id = h.file_id AND UPPER(ISNULL(h.file_type,'')) = 'CM'
LEFT JOIN dbo.FileData f ON f.id = h.file_id AND (h.file_type IS NULL OR UPPER(h.file_type) <> 'CM')
WHERE h.id = @id;
"
;
 

            var task = await conn.QueryFirstOrDefaultAsync<HistoryTask>(
                new CommandDefinition(detailSql, new { id = hid.Value }, cancellationToken: ct));

            if (task == null)
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE dbo.FileData_History SET assigned_node = NULL, file_status = 800 WHERE id = @id",
                        new { id = hid.Value },
                        cancellationToken: ct));
                return false;
            }

            var restoreSql = @"
            SELECT TOP (1)
                id       AS RestoreStorageId,
                location AS RestorePath
            FROM dbo.Storage
            WHERE [type] = 'RESTORE'
            AND set_group = @group
            ORDER BY ISNULL(priority, 0) DESC, id ASC;
            ";
            var restore = await conn.QueryFirstOrDefaultAsync<(int? RestoreStorageId, string? RestorePath)>(
            new CommandDefinition(restoreSql, new { group }, cancellationToken: ct));

            task.RestoreStorageId = restore.RestoreStorageId;
            task.RestorePath      = restore.RestorePath;

            return await PushToWorkerAsync(conn, task, nodeName, ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 1205)
        {
            var delayMs = 50 + Random.Shared.Next(0, 120) + attempt * 60;
            _log.LogWarning(ex,
                "Deadlock(1205) in AssignOneTaskAsync, retry {attempt}/{max} after {delay}ms",
                attempt, MAX_RETRY, delayMs);
            await Task.Delay(delayMs, ct);
            continue;
        }
    }

    return false;
}


// ✅ 把你原本 push 段抽成一個 helper（內容照抄即可）
private async Task<bool> PushToWorkerAsync(
    System.Data.IDbConnection conn,
    HistoryTask task,
    string nodeName,
    CancellationToken ct)
{
     // ✅ 如果派給自己：不要走 HTTP，直接 enqueue 本機 TaskQueue
if (string.Equals(nodeName, _selfNodeName, StringComparison.OrdinalIgnoreCase))
{
    _log.LogWarning(">>> PUSH LOCAL (no HTTP) hid={hid} node={node}", task.HistoryId, nodeName);

    var totals = new Dictionary<string, long>
    {
        { $"TO-{task.HistoryId}", task.FileSize }
    };
    _progress.InitTotals(task.HistoryId.ToString(), totals);

    await _taskQueue.EnqueueAsync(task, ct);

    // ✅ local 也要同步 DB（等同於「HTTP push 成功」的效果）
    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.FileData_History
SET assigned_node = @node,
    -- ✅ 只有非 Phase 2 的任務才設為 1 (搬運中)
    file_status   = CASE 
                        WHEN file_status IN (24, 27) THEN file_status 
                    
                        ELSE 1 
                    END,
    update_time   = GETDATE()
WHERE id = @id
", new { id = task.HistoryId, node = nodeName }, cancellationToken: ct));

    return true;
}


    try
    {
        var nodeInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT IpAddress FROM dbo.WorkerNode WHERE NodeName = @nodeName",
            new { nodeName });

        string targetHost = !string.IsNullOrWhiteSpace(nodeInfo?.IpAddress)
            ? (string)nodeInfo.IpAddress
            : nodeName;

        int targetPort = _cfg.GetValue<int>($"Cluster:NodePorts:{nodeName}", 5089);
        string workerUrl = $"http://{targetHost}:{targetPort}/jobs/receive-task";

        _log.LogWarning(">>> PUSH URL = {url}", workerUrl);

        // using var client = _httpClientFactory.CreateClient();
        using var client = _httpClientFactory.CreateClient("WorkerClient");
        client.Timeout = TimeSpan.FromSeconds(10);

        var response = await client.PostAsJsonAsync(workerUrl, task, ct);

        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("Push task {id} to {node} failed: {code} URL={url}",
                task.HistoryId, nodeName, response.StatusCode, workerUrl);

            await conn.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE dbo.FileData_History SET assigned_node = NULL, file_status = 800 WHERE id = @id",
                    new { id = task.HistoryId },
                    cancellationToken: ct));

            return false;
        }

        _log.LogInformation("Successfully pushed task {id} to node {node}", task.HistoryId, nodeName);
        return true;
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "Error pushing task {id} to {node}", task.HistoryId, nodeName);

        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE dbo.FileData_History SET assigned_node = NULL, file_status = 800 WHERE id = @id",
                new { id = task.HistoryId },
                cancellationToken: ct));

        return false;
    }
}

        private async Task<int> RequeueOfflineNodeJobsAsync(
            System.Data.IDbConnection conn,
            string group,
            int timeoutSec,
            CancellationToken ct)
        {
        // 只回收「同 group 的離線節點」所持有的 running 任務
        // 回收策略：assigned_node=NULL + file_status=800（讓其他人能撿回去跑）
                var sql = @"
            ;WITH DeadNodes AS (
                SELECT NodeName
                FROM dbo.WorkerNode
                WHERE GroupCode = @GroupCode
                AND DATEDIFF(SECOND, LastHeartbeat, GETDATE()) > @TimeoutSec
            ),
            StuckJobs AS (
                SELECT h.id, h.file_status
                FROM dbo.FileData_History h
                JOIN DeadNodes d ON d.NodeName = h.assigned_node
                WHERE h.file_status IN (1,2,3)
            )
            UPDATE h
            SET assigned_node = NULL,
                file_status   =
                    CASE
                        WHEN s.file_status = 2 THEN
                            CASE
                                WHEN @GroupCode = '7F' THEN 24
                                WHEN @GroupCode = '4F' THEN 27
                                ELSE 800
                            END
                        ELSE 800
                    END,
                update_time   = GETDATE()
            FROM dbo.FileData_History h
            JOIN StuckJobs s ON s.id = h.id;
            ";

                var affected = await conn.ExecuteAsync(
                    new CommandDefinition(sql, new { GroupCode = group, TimeoutSec = timeoutSec }, cancellationToken: ct));

                return affected;
            }
                }
                
            }
