using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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

            public int MaxConcurrency = 1;
            public int FreeSlots = 0;

            public DateTime LastSeenUtc = DateTime.UtcNow;
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

            s.Role = dto.Role ?? s.Role;
            s.Group = dto.Group ?? s.Group;
            s.HostName = dto.HostName ?? s.HostName;
            s.IpAddress = dto.IpAddress ?? s.IpAddress;

            s.MaxConcurrency = Math.Max(1, dto.MaxConcurrency <= 0 ? s.MaxConcurrency : dto.MaxConcurrency);

            var free = dto.FreeSlots;
            if (free < 0) free = 0;
            if (free > s.MaxConcurrency) free = s.MaxConcurrency;
            s.FreeSlots = free;

            s.LastSeenUtc = DateTime.UtcNow;
        }

        // ✅ 任務完成：free +1
        public void AddFree(string node, int delta)
        {
            if (string.IsNullOrWhiteSpace(node)) return;
            var s = GetOrCreate(node);

            lock (s)
            {
                var max = Math.Max(1, s.MaxConcurrency);
                var next = s.FreeSlots + delta;
                if (next < 0) next = 0;
                if (next > max) next = max;
                s.FreeSlots = next;
                s.LastSeenUtc = DateTime.UtcNow;
            }
        }

        // ✅ 派工時會用（你之後寫 push 用）
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
                var online = (now - s.LastSeenUtc) <= timeout;

                var max = Math.Max(1, s.MaxConcurrency);
                var free = online
                    ? Math.Clamp(s.FreeSlots, 0, max)
                    : 0;
                var running = Math.Max(0, max - free);

                list.Add(new NodeStatusDto
                {
                    NodeName = s.NodeName,
                    Role = s.Role,
                    Group = s.Group,
                    Status = online ? "Online" : "Offline",
                    MaxConcurrency = max,
                    CurrentRunning = running,
                    LastHeartbeat = s.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    HostName = s.HostName,
                    IpAddress = s.IpAddress
                });
            }

            list.Sort((a, b) => string.Compare(a.NodeName, b.NodeName, StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }

    // ✅ node「定期/上線」回報 free 的 DTO
    public sealed class NodeFreeReportDto
    {
        public string? Node { get; set; }
        public string? Role { get; set; }
        public string? Group { get; set; }
        public string? HostName { get; set; }
        public string? IpAddress { get; set; }

        public int MaxConcurrency { get; set; } = 1;
        public int FreeSlots { get; set; } = 0;
    }

    // ✅ 前端 nodes.js 用的 DTO
    public sealed class NodeStatusDto
    {
        public string NodeName { get; set; } = "";
        public string Role { get; set; } = "";
        public string Group { get; set; } = "";
        public string Status { get; set; } = "Offline";

        public int MaxConcurrency { get; set; } = 1;
        public int CurrentRunning { get; set; } = 0;

        public string? LastHeartbeat { get; set; }
        public string? HostName { get; set; }
        public string? IpAddress { get; set; }
    }
}