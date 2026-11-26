using Npgsql;

namespace SocialNetwork;

public sealed record Post(Guid Id, string Text, Guid AuthorUserId);

internal sealed record StoredPost(Post Post, DateTime CreatedAt);

public sealed class PostStore(NpgsqlDataSource dataSource)
{
    internal async Task<Guid[]> GetFriendsAsync(Guid userId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT friend_id FROM friendships WHERE user_id = @user_id");
        command.Parameters.AddWithValue("user_id", userId);
        return await ReadUserIdsAsync(command, ct);
    }

    internal async Task<Guid[]> GetFollowersAsync(Guid authorId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT user_id FROM friendships WHERE friend_id = @author_id");
        command.Parameters.AddWithValue("author_id", authorId);
        return await ReadUserIdsAsync(command, ct);
    }

    private static async Task<Guid[]> ReadUserIdsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }

    internal async Task<Dictionary<Guid, List<StoredPost>>> GetRecentPostsAsync(
        Guid[] authorIds, int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT p.id, p.text, p.author_id, p.created_at
            FROM unnest(@author_ids) AS author(id)
            CROSS JOIN LATERAL (
                SELECT id, text, author_id, created_at
                FROM posts
                WHERE author_id = author.id
                ORDER BY created_at DESC, id DESC
                LIMIT @limit
            ) AS p
            ORDER BY p.author_id, p.created_at DESC, p.id DESC
            """);
        command.Parameters.AddWithValue("author_ids", authorIds);
        command.Parameters.AddWithValue("limit", limit);

        var posts = new Dictionary<Guid, List<StoredPost>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var post = ReadPost(reader);
            if (!posts.TryGetValue(post.AuthorUserId, out var authorPosts))
            {
                authorPosts = [];
                posts.Add(post.AuthorUserId, authorPosts);
            }
            authorPosts.Add(new StoredPost(post, reader.GetDateTime(3)));
        }
        return posts;
    }

    public async Task<Post?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT id, text, author_id FROM posts WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPost(reader) : null;
    }

    internal async Task<Guid> CreateAsync(Guid authorId, string text, CancellationToken ct, bool enqueue = false)
    {
        var id = Guid.NewGuid();
        var sql = enqueue ? """
            WITH inserted AS (
                INSERT INTO posts (id, author_id, text, feed_delayed)
                VALUES (@id, @author_id, @text, true) RETURNING id
            )
            INSERT INTO feed_outbox (post_id) SELECT id FROM inserted
            """ : """
            INSERT INTO posts (id, author_id, text) VALUES (@id, @author_id, @text)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("author_id", authorId);
        command.Parameters.AddWithValue("text", text);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    internal async Task<bool> UpdateAsync(Guid authorId, Guid id, string text, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE posts SET text = @text WHERE id = @id AND author_id = @author_id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("author_id", authorId);
        command.Parameters.AddWithValue("text", text);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    internal async Task<bool> DeleteAsync(Guid authorId, Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM posts WHERE id = @id AND author_id = @author_id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("author_id", authorId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    internal async Task<bool> AddFriendAsync(Guid userId, Guid friendId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Сериализуем изменение подписки и построение адресатов поста одного автора.
        await using var authorLock = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(hashtextextended(@friend_id::text, 0))
            """, connection, transaction);
        authorLock.Parameters.AddWithValue("friend_id", friendId);
        await authorLock.ExecuteNonQueryAsync(ct);

        await using var command = new NpgsqlCommand("""
            WITH friend AS (SELECT id FROM users WHERE id = @friend_id),
                 inserted AS (
                     INSERT INTO friendships (user_id, friend_id)
                     SELECT @user_id, id FROM friend ON CONFLICT DO NOTHING
                 )
            SELECT EXISTS (SELECT 1 FROM friend)
            """, connection, transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("friend_id", friendId);
        var exists = await command.ExecuteScalarAsync(ct) is true;
        await transaction.CommitAsync(ct);
        return exists;
    }

    internal async Task<bool> DeleteFriendAsync(Guid userId, Guid friendId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM friendships WHERE user_id = @user_id AND friend_id = @friend_id
            """);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("friend_id", friendId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static Post ReadPost(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2));
}
