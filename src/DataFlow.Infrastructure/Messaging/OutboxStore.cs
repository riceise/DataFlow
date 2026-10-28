using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Infrastructure.Messaging;

public sealed record ClaimedOutbox(Guid Id, string Type, string Payload, DateTimeOffset OccurredAt, int Attempts);

public sealed class OutboxStore(DataFlowDbContext database)
{
    public async Task<List<ClaimedOutbox>> ClaimAsync(string owner, int batchSize, int leaseSeconds, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var messages = await database.Database.SqlQuery<ClaimedOutbox>($"""
            WITH candidates AS (
                SELECT id FROM outbox_messages
                WHERE published_at IS NULL AND (locked_until IS NULL OR locked_until <= now())
                ORDER BY occurred_at, id LIMIT {batchSize} FOR UPDATE SKIP LOCKED
            )
            UPDATE outbox_messages AS message
            SET owner = {owner}, locked_until = now() + make_interval(secs => {leaseSeconds})
            FROM candidates WHERE message.id = candidates.id
            RETURNING message.id AS "Id", message.type AS "Type", message.payload::text AS "Payload",
                      message.occurred_at AS "OccurredAt", message.attempts AS "Attempts"
            """).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    public Task<int> MarkPublishedAsync(Guid id, string owner, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET published_at = now(), owner = NULL, locked_until = NULL
            WHERE id = {id} AND published_at IS NULL AND owner = {owner} AND locked_until > now()
            """, cancellationToken);

    public Task<int> MarkFailedAsync(Guid id, string owner, string errorType, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages
            SET attempts = attempts + 1, last_error = {errorType}, owner = NULL, locked_until = NULL
            WHERE id = {id} AND published_at IS NULL AND owner = {owner}
            """, cancellationToken);

    public Task<int> ReleaseAsync(string owner, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET owner = NULL, locked_until = NULL
            WHERE published_at IS NULL AND owner = {owner}
            """, cancellationToken);
}
