// Services/DbSlotConfigProvider.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileMoverWeb.Services
{
    public sealed class DbSlotConfigProvider : ISlotConfigProvider
    {
        private const int MaxSlots = 10;

        private readonly IServiceProvider _sp;
        private readonly IConfiguration _cfg;
        private readonly ILogger<DbSlotConfigProvider> _log;

        private readonly string _nodeName;
        private readonly int _fallbackConfigured;

        public DbSlotConfigProvider(
            IServiceProvider sp,
            IConfiguration cfg,
            ILogger<DbSlotConfigProvider> log)
        {
            _sp = sp;
            _cfg = cfg;
            _log = log;

            _nodeName = _cfg["Cluster:NodeName"] ?? Environment.MachineName;

            _fallbackConfigured = _cfg.GetValue<int>("GlobalMaxConcurrentMoves", 2);
            if (_fallbackConfigured < 1) _fallbackConfigured = 1;
            if (_fallbackConfigured > MaxSlots) _fallbackConfigured = MaxSlots;
        }

        public async Task<int> GetEnabledSlotsAsync(CancellationToken ct)
        {
            try
            {
                using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(_sp);
                var factory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
                using var conn = factory.Create();

                const string sql = @"
SELECT MaxConcurrency
FROM dbo.WorkerNode
WHERE NodeName = @NodeName;
";

                var dbValue = await conn.ExecuteScalarAsync<int?>(sql, new { NodeName = _nodeName });

                var enabled = dbValue ?? _fallbackConfigured;

                if (enabled < 1) enabled = 1;
                if (enabled > MaxSlots) enabled = MaxSlots;

                return enabled;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "GetEnabledSlotsAsync failed, fallback={fallback}",
                    _fallbackConfigured);

                return _fallbackConfigured;
            }
        }
    }
}
