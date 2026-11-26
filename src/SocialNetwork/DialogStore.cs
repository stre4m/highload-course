using System.Buffers.Binary;
using System.Security.Cryptography;
using Npgsql;

namespace SocialNetwork;

public sealed class DialogStore : IAsyncDisposable
{
    private readonly NpgsqlDataSource catalog;
    private readonly Dictionary<string, NpgsqlDataSource> shards;

    public DialogStore(NpgsqlDataSource catalog, IConfigurationSection configuration)
    {
        this.catalog = catalog;
        shards = configuration.GetChildren()
            .Where(shard => !string.IsNullOrWhiteSpace(shard.Value))
            .ToDictionary(shard => shard.Key, shard => NpgsqlDataSource.Create(shard.Value!), StringComparer.Ordinal);
    }

    public async Task<bool> SendAsync(Guid sender, Guid recipient, string message, CancellationToken ct)
    {
        var key = DialogKey.Create(sender, recipient);
        var routes = await GetRoutesAsync(key.Bucket, recipient, ct);
        if (routes is null)
            return false;

        await using var command = GetShard(routes[0]).CreateCommand("""
            INSERT INTO dialog_messages (bucket, user_low, user_high, message_id, sender_id, text)
            VALUES ($1, $2, $3, $4, $5, $6)
            """);
        BindKey(command, key);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sender });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = message });
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<DialogMessage>?> ListAsync(Guid user, Guid partner, CancellationToken ct)
    {
        var key = DialogKey.Create(user, partner);
        var routes = await GetRoutesAsync(key.Bucket, partner, ct);
        if (routes is null)
            return null;

        var results = await Task.WhenAll(routes.Distinct(StringComparer.Ordinal)
            .Select(route => ReadShardAsync(GetShard(route), key, ct)));
        return results.SelectMany(messages => messages)
            .DistinctBy(message => message.Id)
            .OrderBy(message => message.SentAt)
            .ThenBy(message => message.Id)
            .Select(message => message.Value)
            .ToArray();
    }

    private async Task<string[]?> GetRoutesAsync(int bucket, Guid partner, CancellationToken ct)
    {
        if (shards.Count == 0)
            throw new DialogStorageUnavailableException("Dialog shards are not configured.");

        await using var command = catalog.CreateCommand("""
            SELECT ARRAY(
                SELECT shard
                FROM dialog_routes
                WHERE bucket = $1
                ORDER BY generation DESC
            )
            WHERE EXISTS (SELECT 1 FROM users WHERE id = $2)
            """);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = bucket });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = partner });
        if (await command.ExecuteScalarAsync(ct) is not string[] routes)
            return null;
        if (routes.Length == 0)
            throw new DialogStorageUnavailableException($"No route for dialog bucket {bucket}.");

        return routes;
    }

    private NpgsqlDataSource GetShard(string name) =>
        shards.TryGetValue(name, out var shard)
            ? shard
            : throw new DialogStorageUnavailableException($"Dialog shard '{name}' is not configured.");

    private static async Task<List<StoredMessage>> ReadShardAsync(
        NpgsqlDataSource shard, DialogKey key, CancellationToken ct)
    {
        await using var command = shard.CreateCommand("""
            SELECT message_id, sent_at, sender_id, text
            FROM dialog_messages
            WHERE bucket = $1 AND user_low = $2 AND user_high = $3
            """);
        BindKey(command, key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var messages = new List<StoredMessage>();
        while (await reader.ReadAsync(ct))
        {
            var sender = reader.GetGuid(2);
            var recipient = sender == key.Low ? key.High : key.Low;
            messages.Add(new StoredMessage(reader.GetGuid(0), reader.GetDateTime(1),
                new DialogMessage(sender, recipient, reader.GetString(3))));
        }

        return messages;
    }

    private static void BindKey(NpgsqlCommand command, DialogKey key)
    {
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = key.Bucket });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = key.Low });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = key.High });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var shard in shards.Values)
            await shard.DisposeAsync();
    }

    private readonly record struct DialogKey(int Bucket, Guid Low, Guid High)
    {
        public static DialogKey Create(Guid first, Guid second)
        {
            var (low, high) = first.CompareTo(second) < 0 ? (first, second) : (second, first);
            Span<byte> pair = stackalloc byte[32];
            low.TryWriteBytes(pair[..16], bigEndian: true, out _);
            high.TryWriteBytes(pair[16..], bigEndian: true, out _);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(pair, hash);
            var bucket = (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % 1024);
            return new DialogKey(bucket, low, high);
        }
    }

    private sealed record StoredMessage(Guid Id, DateTime SentAt, DialogMessage Value);
}

public sealed record DialogMessage(Guid From, Guid To, string Text);

public sealed class DialogStorageUnavailableException(string message) : Exception(message);
