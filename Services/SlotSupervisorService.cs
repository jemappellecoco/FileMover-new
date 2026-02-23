// Services/SlotSupervisorService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Services
{
    public sealed class SlotWorker
    {
        private readonly int _slotIndex;
        private readonly Func<CancellationToken, Task<bool>> _doOneIterationAsync;
        private readonly ILogger<SlotWorker> _log;
        private volatile bool _stopAfterCurrent;

        public SlotWorker(
            int slotIndex,
            Func<CancellationToken, Task<bool>> doOneIterationAsync,
            ILogger<SlotWorker> log)
        {
            _slotIndex = slotIndex;
            _doOneIterationAsync = doOneIterationAsync;
            _log = log;
        }

        public void RequestStopAfterCurrent() => _stopAfterCurrent = true;

        public async Task RunAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("[SLOT {slot}] worker started", _slotIndex);

            var delayMs = 300;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_stopAfterCurrent)
                {
                    _log.LogInformation("[SLOT {slot}] stop requested -> exiting", _slotIndex);
                    break;
                }

                bool didWork;
                try
                {
                    didWork = await _doOneIterationAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[SLOT {slot}] iteration crashed", _slotIndex);
                    didWork = false;
                }

                if (didWork)
                {
                    delayMs = 300;
                    continue;
                }

                await Task.Delay(delayMs, stoppingToken);
                delayMs = Math.Min(delayMs * 2, 3000);
            }

            _log.LogInformation("[SLOT {slot}] worker stopped", _slotIndex);
        }
    }

    public sealed class SlotSupervisorService : BackgroundService
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, SlotWorker> _workers = new();

        private readonly ISlotConfigProvider _slotConfigProvider;
        private readonly ISlotWorkerFactory _slotWorkerFactory;
        private readonly ILogger<SlotSupervisorService> _log;

        private int _lastTarget = -1;
        private int _lastRunning = -1;

        public SlotSupervisorService(
            ISlotConfigProvider slotConfigProvider,
            ISlotWorkerFactory slotWorkerFactory,
            ILogger<SlotSupervisorService> log)
        {
            _slotConfigProvider = slotConfigProvider;
            _slotWorkerFactory = slotWorkerFactory;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("[SLOT] supervisor started");

            while (!stoppingToken.IsCancellationRequested)
            {
                int target;
                try
                {
                    target = await _slotConfigProvider.GetEnabledSlotsAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[SLOT] GetEnabledSlotsAsync failed -> keep current workers");
                    target = GetRunningCount(); // 讀不到就維持現狀
                }

                if (target < 0) target = 0;

                AdjustWorkers(target, stoppingToken);

                // ✅ 印出 target / running（只在變動時印）
                var running = GetRunningCount();
                if (target != _lastTarget || running != _lastRunning)
                {
                    _lastTarget = target;
                    _lastRunning = running;
                    _log.LogInformation("[SLOT] target={target}, running={running}", target, running);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            _log.LogInformation("[SLOT] supervisor stopped");
        }

        private int GetRunningCount()
        {
            lock (_lock) return _workers.Count;
        }

        private void AdjustWorkers(int target, CancellationToken stoppingToken)
        {
            if (stoppingToken.IsCancellationRequested) return;
            lock (_lock)
            {
                // 擴增
                for (int i = 0; i < target; i++)
                {
                    if (_workers.ContainsKey(i)) continue;

                    var worker = _slotWorkerFactory.Create(i);
                    _workers[i] = worker;

                    _log.LogInformation("[SLOT] start slot #{slot}", i);
                    _ = Task.Run(() => worker.RunAsync(stoppingToken), stoppingToken);
                }

                // 縮減（soft stop）
                var toRemove = _workers.Keys.Where(k => k >= target).OrderByDescending(k => k).ToList();
                foreach (var idx in toRemove)
                {
                    _log.LogInformation("[SLOT] stop slot #{slot}", idx);
                    _workers[idx].RequestStopAfterCurrent();
                    _workers.Remove(idx);
                }
            }
        }
    }
}
