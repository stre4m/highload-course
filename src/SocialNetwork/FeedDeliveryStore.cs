using System.Text.Json.Serialization;
using Npgsql;

namespace SocialNetwork;

internal sealed record PostedEvent(
    [property: JsonPropertyName("postId")] Guid PostId,
    [property: JsonPropertyName("postText")] string PostText,
    [property: JsonPropertyName("author_user_id")] Guid AuthorUserId);

public sealed class FeedDeliveryStore(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<Post>> GetFeedAsync(Guid userId, int offset, int limit, CancellationToken ct)
    {
        const int feedSize = 1000;
        if (offset >= feedSize)
            return Array.Empty<Post>();

        await using var command = dataSource.CreateCommand("""
            SELECT p.id, p.text, p.author_id
            FROM friendships f
            JOIN posts p ON p.author_id = f.friend_id
            WHERE f.user_id = @user_id
              AND (NOT p.feed_delayed OR p.created_at < f.added_at OR EXISTS (
                  SELECT 1 FROM feed_targets t WHERE t.user_id = f.user_id AND t.post_id = p.id
              ))
            ORDER BY p.created_at DESC, p.id DESC
            LIMIT @limit OFFSET @offset
            """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("limit", Math.Min(limit, feedSize - offset));
        command.Parameters.AddWithValue("offset", offset);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var posts = new List<Post>();
        while (await reader.ReadAsync(ct))
            posts.Add(new Post(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2)));
        return posts;
    }

    internal async Task<bool> RelayNextAsync(Func<Guid, Task> publish, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var pending = new NpgsqlCommand("""
            SELECT post_id FROM feed_outbox ORDER BY queued_at, post_id
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction);
        if (await pending.ExecuteScalarAsync(ct) is not Guid postId)
            return false;

        await publish(postId);
        await using var delete = new NpgsqlCommand("DELETE FROM feed_outbox WHERE post_id = @post_id",
            connection, transaction);
        delete.Parameters.AddWithValue("post_id", postId);
        await delete.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    internal async Task<(PostedEvent? Event, List<Guid> Recipients)> MaterializeAsync(Guid postId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var authorLock = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(hashtextextended(author_id::text, 0)) FROM posts WHERE id = @post_id
            """, connection, transaction);
        authorLock.Parameters.AddWithValue("post_id", postId);
        await authorLock.ExecuteNonQueryAsync(ct);

        // Новый снимок READ COMMITTED видит связи, зафиксированные перед получением блокировки.
        await using var command = new NpgsqlCommand("""
            WITH post AS MATERIALIZED (
                SELECT id, author_id, text, created_at FROM posts WHERE id = @post_id FOR KEY SHARE
            ), recipients AS MATERIALIZED (
                SELECT f.user_id FROM post p
                JOIN friendships f ON f.friend_id = p.author_id AND f.added_at <= p.created_at
                FOR SHARE OF f
            ), materialized AS (
                INSERT INTO feed_targets (user_id, post_id)
                SELECT user_id, @post_id FROM recipients ON CONFLICT DO NOTHING
            )
            SELECT p.id, p.text, p.author_id, r.user_id FROM post p CROSS JOIN recipients r
            """, connection, transaction);
        command.Parameters.AddWithValue("post_id", postId);
        var recipients = new List<Guid>();
        PostedEvent? posted = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                posted ??= new PostedEvent(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2));
                recipients.Add(reader.GetGuid(3));
            }
        }
        await transaction.CommitAsync(ct);
        return (posted, recipients);
    }
}
