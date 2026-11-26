using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SocialNetwork;

internal sealed class FeedSocketSubscriptions(FeedBroker broker, ILogger<FeedSocketSubscriptions> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Session? current;

    internal async Task<Subscription?> SubscribeAsync(Guid userId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (current is not { IsActive: true } session)
                return null;

            var subscription = new Subscription(this, session, userId);
            if (!session.Add(subscription))
                return subscription;

            try
            {
                // Команда меняет привязку для всей реплики и завершается независимо от запроса клиента.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await session.Channel.QueueBindAsync(session.QueueName, FeedBroker.PostedExchange,
                    FeedBroker.RecipientKey(userId), cancellationToken: timeout.Token);
                return subscription;
            }
            catch (Exception error)
            {
                session.Fail(error);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask UnsubscribeAsync(Subscription subscription)
    {
        var session = subscription.Session;
        try
        {
            await gate.WaitAsync();
            try
            {
                if (session.Remove(subscription) && session.IsActive)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await session.Channel.QueueUnbindAsync(session.QueueName, FeedBroker.PostedExchange,
                        FeedBroker.RecipientKey(subscription.UserId), cancellationToken: timeout.Token);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception error)
        {
            session.Fail(error);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Feed subscriptions will reconnect to RabbitMQ.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        await using var channel = await broker.OpenChannelAsync(ct);
        await FeedBroker.DeclarePostedAsync(channel, ct);
        var queue = await channel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true,
            arguments: new Dictionary<string, object?>
            {
                ["x-max-length"] = 4096,
                ["x-message-ttl"] = 30_000,
                ["x-expires"] = 60_000
            }, cancellationToken: ct);
        var session = new Session(channel, queue.QueueName);
        channel.ChannelShutdownAsync += (_, reason) =>
        {
            session.Fail(new InvalidOperationException($"Feed subscription channel closed: {reason.ReplyText}"));
            return Task.CompletedTask;
        };
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.UnregisteredAsync += (_, _) =>
        {
            session.Fail(new InvalidOperationException("Feed subscription consumer was unregistered."));
            return Task.CompletedTask;
        };
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                if (Guid.TryParseExact(delivery.RoutingKey, "N", out var userId))
                    session.Publish(userId, delivery.Body);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            }
            catch (Exception error)
            {
                session.Fail(error);
            }
        };

        try
        {
            await channel.BasicQosAsync(0, 32, global: false, ct);
            await channel.BasicConsumeAsync(queue.QueueName, autoAck: false, consumer, ct);
            await gate.WaitAsync(ct);
            try
            {
                current = session;
            }
            finally
            {
                gate.Release();
            }

            var failure = await session.Failure.Task.WaitAsync(ct);
            throw failure;
        }
        finally
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                current = null;
                session.DisconnectAll();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    internal sealed class Session(IChannel channel, string queueName)
    {
        private readonly Dictionary<Guid, HashSet<Subscription>> subscriptions = [];
        internal IChannel Channel { get; } = channel;
        internal string QueueName { get; } = queueName;
        internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsActive => Channel.IsOpen && !Failure.Task.IsCompleted;

        internal void Fail(Exception error) => Failure.TrySetResult(error);

        internal bool Add(Subscription subscription)
        {
            lock (subscriptions)
            {
                if (subscriptions.TryGetValue(subscription.UserId, out var entries))
                {
                    entries.Add(subscription);
                    return false;
                }

                subscriptions.Add(subscription.UserId, [subscription]);
                return true;
            }
        }

        internal bool Remove(Subscription subscription)
        {
            lock (subscriptions)
            {
                if (!subscriptions.TryGetValue(subscription.UserId, out var entries))
                    return false;

                entries.Remove(subscription);
                return entries.Count == 0 && subscriptions.Remove(subscription.UserId);
            }
        }

        internal void Publish(Guid userId, ReadOnlyMemory<byte> body)
        {
            lock (subscriptions)
            {
                if (!subscriptions.TryGetValue(userId, out var entries))
                    return;

                var payload = body.ToArray();
                foreach (var subscription in entries)
                {
                    if (!subscription.Buffer.Writer.TryWrite(payload))
                        subscription.Disrupt();
                }
            }
        }

        internal void DisconnectAll()
        {
            lock (subscriptions)
            {
                foreach (var entries in subscriptions.Values)
                {
                    foreach (var subscription in entries)
                        subscription.Disrupt();
                }
                subscriptions.Clear();
            }
        }
    }

    internal sealed class Subscription(FeedSocketSubscriptions owner, Session session, Guid userId) : IAsyncDisposable
    {
        internal Session Session { get; } = session;
        internal Guid UserId { get; } = userId;
        internal Channel<byte[]> Buffer { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true
        });
        private readonly TaskCompletionSource disrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Disrupted => disrupted.Task;
        internal void Disrupt() => disrupted.TrySetResult();

        public ValueTask DisposeAsync() => owner.UnsubscribeAsync(this);
    }
}
