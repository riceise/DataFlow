using Npgsql;

namespace DataFlow.Infrastructure.Storage;

// All writers and orphan sweepers serialize per random object key. A process crash releases the lock.
public sealed class StorageObjectLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _key;
    private StorageObjectLock(NpgsqlConnection connection, string key) { _connection = connection; _key = key; }

    public static async Task<StorageObjectLock?> AcquireAsync(string connectionString, string key, bool wait, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = wait ? "SELECT pg_advisory_lock(hashtextextended(@key, 11))" : "SELECT pg_try_advisory_lock(hashtextextended(@key, 11))";
            command.Parameters.AddWithValue("key", key);
            var result = await command.ExecuteScalarAsync(ct);
            if (!wait && result is not true) { await connection.DisposeAsync(); return null; }
            return new StorageObjectLock(connection, key);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@key, 11))";
            command.Parameters.AddWithValue("key", _key);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
        finally { await _connection.DisposeAsync(); }
    }
}
