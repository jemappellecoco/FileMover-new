// Services/SlotWorkerFactory.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using FileMoverWeb.Models;   // ✅ MoveBatchRequest / MoveItem / MoveResult
namespace FileMoverWeb.Services;

public sealed class SlotWorkerFactory : ISlotWorkerFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<int, CancellationToken, Task<bool>> _runOne;

    public SlotWorkerFactory(
        ILoggerFactory loggerFactory,
        Func<int, CancellationToken, Task<bool>> runOne)
    {
        _loggerFactory = loggerFactory;
        _runOne = runOne;
    }

    public SlotWorker Create(int slotIndex)
    {
        return new SlotWorker(
            slotIndex,
            ct => _runOne(slotIndex, ct),   // ✅ Task<bool>
            _loggerFactory.CreateLogger<SlotWorker>()
        );
    }
}
