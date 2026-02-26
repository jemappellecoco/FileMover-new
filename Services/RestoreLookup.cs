using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace FileMoverWeb.Services
{
    public sealed class RestoreLookup
    {
        private readonly string _connStr;

        public RestoreLookup(IConfiguration cfg)
            => _connStr = cfg.GetConnectionString("DefaultConnection")!;

        public async Task<(int id, string name, string location)> GetRestoreAsync(string group, CancellationToken ct)
        {
            const string sql = @"
SELECT TOP 1 id, storage_name, location
FROM dbo.Storage
WHERE set_group = @group
  AND [type] = 'RESTORE'
ORDER BY priority DESC, id ASC;";

            using var conn = new SqlConnection(_connStr);

            var row = await conn.QueryFirstOrDefaultAsync<(int id, string name, string location)>(
                new CommandDefinition(sql, new { group }, cancellationToken: ct));

            if (row.id <= 0 || string.IsNullOrWhiteSpace(row.location))
                throw new InvalidOperationException($"RESTORE storage not found for group={group}");

            return row;
        }
    }
}