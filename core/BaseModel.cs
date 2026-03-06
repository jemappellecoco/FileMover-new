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
        private readonly IDbTransaction? _trans;
        public BaseModel(IDbConnection conn, IDbTransaction? trans = null)
        {
            _conn = conn;
            _trans = trans;
        }

        public Task<int> DeleteAsync(string table, string pkName, int id, CancellationToken ct = default)
        {
            var sql = $"DELETE FROM {table} WHERE {pkName} = @id";
            return _conn.ExecuteAsync(new CommandDefinition(sql, new { id }, transaction: _trans, cancellationToken: ct));        }

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

            return _conn.ExecuteAsync(new CommandDefinition(sql, p, transaction: _trans, cancellationToken: ct));        }
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

            // ✅ 傳入 _trans
            var newId = await _conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, clean, transaction: _trans, cancellationToken: ct));

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
/// <summary>
        /// 批次更新：針對多個 ID 一次性更新相同欄位
        /// </summary>
        public async Task<int> UpdateBatchAsync(
            string table,
            string pkName,
            IEnumerable<int> ids,
            Dictionary<string, object?> data,
            IReadOnlyCollection<string> columnsWhitelist,
            string? extraWhereSql = null,
            object? extraWhereParams = null,
            CancellationToken ct = default)
        {
            var idList = ids?.ToList();
            if (idList == null || !idList.Any() || data == null || !data.Any()) 
                return 0;

            // 1. 白名單安全性過濾
            var clean = data
                .Where(kv => columnsWhitelist.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (!clean.Any()) return 0;

            // 2. 組成 SET 語句 (例如: file_status = @file_status, note = @note)
            var setSql = string.Join(", ", clean.Keys.Select(k => $"{k} = @{k}"));

            // 3. 組成完整 SQL (使用 WHERE pk IN @ids)
            var sql = $"UPDATE {table} SET {setSql} WHERE {pkName} IN @ids";
            
            if (!string.IsNullOrWhiteSpace(extraWhereSql))
                sql += $" AND ({extraWhereSql})";

            // 4. 準備參數
            var p = new DynamicParameters(clean);
            p.Add("ids", idList); // Dapper 會自動處理為 IN (...)
            if (extraWhereParams != null) p.AddDynamicParams(extraWhereParams);

            // 5. 執行
        return await _conn.ExecuteAsync(new CommandDefinition(sql, p, transaction: _trans, cancellationToken: ct));        }
    
        public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken ct = default)
        {
            return _conn.QueryAsync<T>(new CommandDefinition(sql, parameters, transaction: _trans,cancellationToken: ct));
        }
        public Task<T?> FindAsync<T>(string table, string pkName, int id, CancellationToken ct = default)
        {
            var sql = $"SELECT * FROM {table} WHERE {pkName} = @id";
            return _conn.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql, new { id },transaction: _trans, cancellationToken: ct));
        }

        public Task<int> CreateManyAsync(
            string table,
            IReadOnlyCollection<string> columns,
            IEnumerable<object> rows,
            CancellationToken ct = default)
        {
            if (columns == null || columns.Count == 0) throw new ArgumentException("columns empty");
            var cols = string.Join(", ", columns);
            var vals = string.Join(", ", columns.Select(c => "@" + c));

            var sql = $@"
        INSERT INTO {table} ({cols})
        VALUES ({vals});
        ";
            return _conn.ExecuteAsync(new CommandDefinition(sql, rows,transaction: _trans, cancellationToken: ct));
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
                new CommandDefinition(sql, parameters, transaction: _trans,cancellationToken: ct));
        }
    }
}