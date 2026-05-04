using Microsoft.Data.Sqlite;
using System.Data;

namespace Fedi;

public class Database : IDisposable
{
    private readonly string _path;
    private readonly SqliteConnection _db;
    private readonly Dictionary<string, Stack<SqliteCommand>> _stmts = new();
    private bool _disposed;

    public Database(string path)
    {
        _path = path;
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
    }

    public async Task InitAsync(AppConfiguration config)
    {
        await RunAsync("PRAGMA journal_mode=WAL");
        await RunAsync("PRAGMA synchronous=NORMAL");
        await RunAsync("PRAGMA wal_autocheckpoint=1000");
        await RunAsync("PRAGMA busy_timeout=5000");
        await RunAsync("PRAGMA temp_store=MEMORY");
        await RunAsync($"PRAGMA cache_size=-{config.SqliteCache}");
        await RunAsync("PRAGMA optimize");

        await RunAsync(
            "CREATE TABLE IF NOT EXISTS user (username VARCHAR(255) PRIMARY KEY, passwordHash VARCHAR(255), actorId VARCHAR(255), privateKey TEXT)");
        await RunAsync(
            "CREATE TABLE IF NOT EXISTS object (id VARCHAR(255) PRIMARY KEY, owner VARCHAR(255), data TEXT)");
        await RunAsync(
            "CREATE TABLE IF NOT EXISTS addressee (objectId VARCHAR(255), addresseeId VARCHAR(255))");
        await RunAsync(
            "CREATE TABLE IF NOT EXISTS upload (relative VARCHAR(255), mediaType VARCHAR(255), objectId VARCHAR(255))");
        await RunAsync(
            "CREATE TABLE IF NOT EXISTS server (origin VARCHAR(255) PRIMARY KEY, privateKey TEXT, publicKey TEXT)");
        await RunAsync(
            "CREATE TABLE IF NOT EXISTS remotecache (id VARCHAR(255), subject VARCHAR(255), expires INTEGER, data TEXT, complete INTEGER, PRIMARY KEY (id, subject))");
        await RunAsync(
            "CREATE INDEX IF NOT EXISTS idx_remotecache_expires ON remotecache(expires)");
        await RunAsync(
            "CREATE TABLE IF NOT EXISTS addressee_2 (objectId VARCHAR(255), addresseeId VARCHAR(255), PRIMARY KEY (objectId, addresseeId), FOREIGN KEY (objectId) REFERENCES object(id))");
        await RunAsync(
            "CREATE INDEX IF NOT EXISTS idx_addressee_2_objectId ON addressee_2(objectId)");
        await RunAsync(
            "INSERT OR IGNORE INTO addressee_2 SELECT * FROM addressee");
        await RunAsync("DELETE FROM addressee");
        await RunAsync(
            "CREATE TABLE IF NOT EXISTS upload_2 (relative VARCHAR(255) PRIMARY KEY, mediaType VARCHAR(255), objectId VARCHAR(255), FOREIGN KEY (objectId) REFERENCES object(id))");
        await RunAsync(
            "CREATE INDEX IF NOT EXISTS idx_upload_2_objectId ON upload_2(objectId)");
        await RunAsync(
            "INSERT OR IGNORE INTO upload_2 SELECT * FROM upload");
        await RunAsync("DELETE FROM upload");
        await RunAsync(
            "CREATE INDEX IF NOT EXISTS idx_user_actorId ON user(actorId)");
        await RunAsync(
            "CREATE TABLE IF NOT EXISTS remote_failure (url VARCHAR(255), subject VARCHAR(255), status INTEGER, expires INTEGER, PRIMARY KEY (url, subject))");
    }

    public async Task<int> RunAsync(string qry, params object[] args)
    {
        var stmt = GetStmt(qry);
        stmt.Parameters.Clear();
        for (int i = 0; i < args.Length; i++)
        {
            stmt.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        }
        var result = await stmt.ExecuteNonQueryAsync();
        ReleaseStmt(stmt, qry);
        return result;
    }

    public async Task<SqliteDataReader> GetAsync(string qry, params object[] args)
    {
        var stmt = GetStmt(qry);
        stmt.Parameters.Clear();
        for (int i = 0; i < args.Length; i++)
        {
            stmt.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        }
        var result = await stmt.ExecuteReaderAsync();
        // Note: caller must dispose reader; for simple scalar use GetScalarAsync
        return result;
    }

    public async Task<T?> GetScalarAsync<T>(string qry, params object[] args)
    {
        using var cmd = new SqliteCommand(qry, _db);
        for (int i = 0; i < args.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        }
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return default;
        return (T)Convert.ChangeType(result, typeof(T));
    }

    public async Task<Dictionary<string, object?>?> GetRowAsync(string qry, params object[] args)
    {
        using var cmd = new SqliteCommand(qry, _db);
        for (int i = 0; i < args.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        }
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var dict = new Dictionary<string, object?>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return dict;
    }

    public async Task<List<Dictionary<string, object?>>> AllAsync(string qry, params object[] args)
    {
        using var cmd = new SqliteCommand(qry, _db);
        for (int i = 0; i < args.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        }
        using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(dict);
        }
        return rows;
    }

    public async Task<bool> ReadyAsync()
    {
        try
        {
            var value = await GetScalarAsync<long>("SELECT 1");
            return value == 1;
        }
        catch
        {
            return false;
        }
    }

    private SqliteCommand GetStmt(string qry)
    {
        if (_stmts.TryGetValue(qry, out var stack) && stack.Count > 0)
        {
            return stack.Pop();
        }
        return new SqliteCommand(qry, _db);
    }

    private void ReleaseStmt(SqliteCommand stmt, string qry)
    {
        stmt.Parameters.Clear();
        if (!_stmts.TryGetValue(qry, out var stack))
        {
            stack = new Stack<SqliteCommand>();
            _stmts[qry] = stack;
        }
        stack.Push(stmt);
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var kvp in _stmts)
        {
            foreach (var stmt in kvp.Value)
            {
                stmt.Dispose();
            }
        }
        _stmts.Clear();
        _db.Close();
        _db.Dispose();
        _disposed = true;
    }
}
