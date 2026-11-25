using Npgsql;

namespace DataFlow.Infrastructure.Persistence;

public sealed class SessionAdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _key;

    private SessionAdvisoryLock(NpgsqlConnection connection, string key)
    {
        _connection = connection;
        _key = key;
    }

    public static async Task<SessionAdvisoryLock> AcquireAsync(string connectionString, string key, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(hashtextextended(@key, 47))";
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteScalarAsync(cancellationToken);
            return new SessionAdvisoryLock(connection, key);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@key, 47))";
            command.Parameters.AddWithValue("key", _key);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
