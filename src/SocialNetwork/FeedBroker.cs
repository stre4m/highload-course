using RabbitMQ.Client;

namespace SocialNetwork;

internal sealed class FeedBroker(ConnectionFactory factory) : IAsyncDisposable
{
    internal const string WorkQueue = "feed.materialize";
    internal const string PostedExchange = "feed.posted";

    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;

    internal async Task<IChannel> OpenChannelAsync(CancellationToken ct, bool confirms = false)
    {
        IConnection activeConnection;
        await gate.WaitAsync(ct);
        try
        {
            if (connection is not { IsOpen: true })
            {
                if (connection is not null)
                    await connection.DisposeAsync();
                connection = await factory.CreateConnectionAsync(ct);
            }
            activeConnection = connection;
        }
        finally
        {
            gate.Release();
        }

        return await activeConnection.CreateChannelAsync(confirms ? new CreateChannelOptions(true, true) : null, ct);
    }

    internal static Task DeclarePostedAsync(IChannel channel, CancellationToken ct) =>
        channel.ExchangeDeclareAsync(PostedExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

    internal static Task<QueueDeclareOk> DeclareWorkAsync(IChannel channel, CancellationToken ct) =>
        channel.QueueDeclareAsync(WorkQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-delivery-limit"] = -1 },
            cancellationToken: ct);

    internal static string RecipientKey(Guid userId) => userId.ToString("N");

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync();
        gate.Dispose();
    }
}
