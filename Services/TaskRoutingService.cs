using System;
using System.Threading;
using System.Threading.Tasks;
using FileMoverWeb.Models;

namespace FileMoverWeb.Services
{
    public sealed class TaskRoutingService
    {
        private readonly RestoreLookup _restore;

        public TaskRoutingService(RestoreLookup restore) => _restore = restore;

        public static bool IsCrossFloor(HistoryTask t)
            => !string.IsNullOrWhiteSpace(t.FromGroup)
            && !string.IsNullOrWhiteSpace(t.ToGroup)
            && !string.Equals(t.FromGroup, t.ToGroup, StringComparison.OrdinalIgnoreCase);

        // ✅ 只改 payload，不改 DB
        public async Task ApplyEffectiveRoutingAsync(HistoryTask t, CancellationToken ct)
        {
            // Phase2：24/27：從「目的樓層 restore」-> 目的地
            if (t.HistoryStatus is 24 or 27)
            {
                var r = await _restore.GetRestoreAsync(t.ToGroup!, ct);
                t.FromStorageId = r.id;
                t.FromStorageName = r.name;
                t.FromLocation = r.location;
                return;
            }

            // Phase1：跨樓層：先 copy 到「來源樓層 restore」
            if (IsCrossFloor(t))
            {
                var r = await _restore.GetRestoreAsync(t.FromGroup!, ct);
                t.ToStorageId = r.id;
                t.ToStorageName = r.name;
                t.ToLocation = r.location;
                return;
            }
        }
    }
}