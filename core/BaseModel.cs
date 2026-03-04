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
        public Task<int> UpdateAsync(
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
        public async Task<int> CreateAsync(
            string table,
            Dictionary<string, object?> data,
            IReadOnlyCollection<string>? columnsWhitelist = null,
            CancellationToken ct = default)
        {
            if (data == null || data.Count == 0)
                throw new ArgumentException("data is empty");

            // 白名單過濾（可選，但我建議你習慣用）
            var clean = (columnsWhitelist == null)
                ? new Dictionary<string, object?>(data)
                : data
                    .Where(kv => columnsWhitelist.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (clean.Count == 0)
                throw new ArgumentException("no allowed columns to insert");

            var cols = string.Join(", ", clean.Keys);
            var vals = string.Join(", ", clean.Keys.Select(k => "@" + k));

            // SQL Server：插入後拿回 identity
            var sql = $@"
        INSERT INTO {table} ({cols})
        VALUES ({vals});
        SELECT CAST(SCOPE_IDENTITY() AS INT);
        ";

            var newId = await _conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, clean, cancellationToken: ct));

            return newId;
        }
// 用法
//     var id = await baseModel.CreateAsync(
//     "dbo.FileData_History",
//     new Dictionary<string, object?>
//     {
//         ["action"] = "copy",
//         ["file_status"] = 0,
//         ["assigned_node"] = null,
//         ["note"] = "created by api",
//         ["update_time"] = DateTime.Now
//     },
//     columnsWhitelist: new[] { "action", "file_status", "assigned_node", "note", "update_time" },
//     ct: ct
// );
public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken ct = default)
{
    return _conn.QueryAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: ct));
}
public Task<T?> FindAsync<T>(string table, string pkName, int id, CancellationToken ct = default)
{
    var sql = $"SELECT * FROM {table} WHERE {pkName} = @id";
    return _conn.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
}
public Task<T?> FindWhereAsync<T>(
    string table,
    string whereSql,
    object? parameters,
    string selectSql = "*",
    CancellationToken ct = default)
{
    var sql = $"SELECT {selectSql} FROM {table} WHERE {whereSql}";
    return _conn.QueryFirstOrDefaultAsync<T>(
        new CommandDefinition(sql, parameters, cancellationToken: ct));
}
    }
}