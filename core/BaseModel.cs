using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace FileMoverWeb.Core
{
    // 通用：只更新你傳進來的欄位（像你 PHP update($id,$data)）
    public sealed class BaseModel
    {
        private readonly IDbConnection _conn;

        public BaseModel(IDbConnection conn)
        {
            _conn = conn;
        }

        public Task<int> DeleteAsync(string table, string pkName, int id, CancellationToken ct = default)
        {
            var sql = $"DELETE FROM {table} WHERE {pkName} = @id";
            return _conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        }

        // ✅ Patch：data 有哪些欄位就更新哪些欄位
        // ⚠️ columnsWhitelist 必須由後端指定，避免把欄位名交給前端
        public Task<int> PatchAsync(
            string table,
            string pkName,
            int id,
            Dictionary<string, object?> data,
            IReadOnlyCollection<string> columnsWhitelist,
            string? extraWhereSql = null,
            object? extraWhereParams = null,
            CancellationToken ct = default)
        {
            if (data == null || data.Count == 0) return Task.FromResult(0);

            var clean = data
                .Where(kv => columnsWhitelist.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (clean.Count == 0) return Task.FromResult(0);

            var set = string.Join(", ", clean.Keys.Select(k => $"{k} = @{k}"));

            var sql = $"UPDATE {table} SET {set} WHERE {pkName} = @id";
            if (!string.IsNullOrWhiteSpace(extraWhereSql))
                sql += " AND " + extraWhereSql;

            var p = new DynamicParameters(clean);
            p.Add("id", id);
            if (extraWhereParams != null) p.AddDynamicParams(extraWhereParams);

            return _conn.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        }
    }
}