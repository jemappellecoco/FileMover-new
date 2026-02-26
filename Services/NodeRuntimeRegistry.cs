using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FileMoverWeb.Models.Node;
namespace FileMoverWeb.Services
{
    public sealed class NodeRuntimeRegistry
    {
       private sealed class NodeState
        {
            public string NodeName = "";
            public string Role = "";
            public string Group = "";
            public string? HostName;
            public string? IpAddress;

            public int? AdminMaxOverride = null; // 前端 override

            public int ReportedMax = 1;      // ✅ node 回報 max
            public bool Initialized = false; // ✅ 是否已初始化
            public int FreeSlots = 0;

            public DateTime LastSeenUtc = DateTime.UtcNow;

            public int EffectiveMax => Math.Max(1, AdminMaxOverride ?? ReportedMax);
        }

        private readonly ConcurrentDictionary<string, NodeState> _map =
            new(StringComparer.OrdinalIgnoreCase);

        private NodeState GetOrCreate(string node)
        {
            var key = (node ?? "").Trim();
            return _map.GetOrAdd(key, _ => new NodeState { NodeName = key });
        }

        // ✅ node 上線或定期回報：校正 max/free（也順便當 heartbeat）
       public void UpsertFree(NodeFreeReportDto dto)
        {
            if (dto == null) return;
            if (string.IsNullOrWhiteSpace(dto.Node)) return;

            var s = GetOrCreate(dto.Node);
            lock (s)
            {
                // 1) 更新前（舊狀態 + 這次 dto）
        // Console.WriteLine(
        //     $"[UP:BEFORE] node={s.NodeName} dtoMax={dto.MaxConcurrency} dtoFree={dto.FreeSlots} " +
        //     $"reported={s.ReportedMax} admin={s.AdminMaxOverride} eff={s.EffectiveMax} free={s.FreeSlots} init={s.Initialized}");

                s.Role = dto.Role ?? s.Role;
                s.Group = dto.Group ?? s.Group;
                s.HostName = dto.HostName ?? s.HostName;
                s.IpAddress = dto.IpAddress ?? s.IpAddress;

                // 只更新 ReportedMax
                if (dto.MaxConcurrency > 0)
                    s.ReportedMax = Math.Max(1, dto.MaxConcurrency);

                // 第一次看到這個 node：初始化 FreeSlots = EffectiveMax
                if (!s.Initialized)
                {
                    s.FreeSlots = s.EffectiveMax;
                    s.Initialized = true;
                }
                else
                {
                    // max 變動時也順手 clamp
                    var max = s.EffectiveMax;
                    if (s.FreeSlots > max) s.FreeSlots = max;
                    if (s.FreeSlots < 0) s.FreeSlots = 0;
                }

                s.LastSeenUtc = DateTime.UtcNow;
                // 2) 更新後（新狀態）
        // Console.WriteLine(
        //     $"[UP:AFTER ] node={s.NodeName} reported={s.ReportedMax} admin={s.AdminMaxOverride} " +
        //     $"eff={s.EffectiveMax} free={s.FreeSlots} init={s.Initialized}");
            }
        }
        public void SetAdminMax(string node, int? maxOverride)
        {
            if (string.IsNullOrWhiteSpace(node)) return;
            var s = GetOrCreate(node);

            lock (s)
            {
                var oldMax = s.EffectiveMax;

                s.AdminMaxOverride = maxOverride;

                var newMax = s.EffectiveMax;

                // ✅ max 變大：free 跟著補差額（維持 running 不變）
                if (newMax > oldMax)
                {
                    var add = newMax - oldMax;
                    s.FreeSlots = Math.Min(newMax, s.FreeSlots + add);
                }

                // clamp
                if (s.FreeSlots > newMax) s.FreeSlots = newMax;
                if (s.FreeSlots < 0) s.FreeSlots = 0;

                s.LastSeenUtc = DateTime.UtcNow;

                Console.WriteLine($"[ADMIN] node={s.NodeName} oldMax={oldMax} newMax={newMax} free={s.FreeSlots}");
            }
        }
        // ✅ 任務完成：free +1
        public void AddFree(string node, int delta)
        {
            if (string.IsNullOrWhiteSpace(node)) return;
            var s = GetOrCreate(node);

            lock (s)
            {
                var max = s.EffectiveMax;
                var next = s.FreeSlots + delta;
                if (next < 0) next = 0;
                if (next > max) next = max;
                s.FreeSlots = next;
                s.LastSeenUtc = DateTime.UtcNow;
            }
        }

        public bool TryConsume(string node, int need = 1)
        {
            if (string.IsNullOrWhiteSpace(node)) return false;
            var s = GetOrCreate(node);

            need = Math.Max(1, need);
            lock (s)
            {
                if (s.FreeSlots < need) return false;
                s.FreeSlots -= need;
                return true;
            }
        }

        // ✅ /api/nodes 給前端
        public IReadOnlyList<NodeStatusDto> ListSnapshot(int heartbeatTimeoutSeconds)
        {
            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(Math.Max(1, heartbeatTimeoutSeconds));

            var list = new List<NodeStatusDto>();
            foreach (var kv in _map)
            {
                var s = kv.Value;

                bool online;
                int max;
                int free;
                int running;
                DateTime last;

                lock (s)
                {
                    last = s.LastSeenUtc;
                    online = (now - last) <= timeout;

                    max = s.EffectiveMax;

                    if (online)
                    {
                        free = Math.Clamp(s.FreeSlots, 0, max);
                        running = Math.Max(0, max - free);
                    }
                    else
                    {
                        free = 0;
                        running = 0;
                    }
                }

                list.Add(new NodeStatusDto
                {
                    NodeName = s.NodeName,
                    Role = s.Role,
                    Group = s.Group,
                    Status = online ? "Online" : "Offline",
                    MaxConcurrency = max,
                    CurrentRunning = running,
                    LastHeartbeat = last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    HostName = s.HostName,
                    IpAddress = s.IpAddress
                });
            }

            list.Sort((a, b) => string.Compare(a.NodeName, b.NodeName, StringComparison.OrdinalIgnoreCase));
            return list;
        }



    
}
}