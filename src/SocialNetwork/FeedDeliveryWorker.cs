using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SocialNetwork;

internal sealed class FeedDeliveryWorker(
    FeedBroker broker, FeedDeliveryStore store, ILogger<FeedDeliveryWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunWithRetryAsync(RelayAsync, stoppingToken), RunWithRetryAsync(ConsumeAsync, stoppingToken));

    private async Task RunWithRetryAsync(Func<CancellationToken, Task> run, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await run(ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(error, "Feed delivery worker will retry {Operation}.", run.Method.Name);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private async Task RelayAsync(CancellationToken ct)
    {
        await using var channel = await broker.OpenChannelAsync(ct, confirms: true);
        await FeedBroker.DeclareWorkAsync(channel, ct);
        async Task PublishAsync(Guid postId)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await channel.BasicPublishAsync("", FeedBroker.WorkQueue, mandatory: true,
                new BasicProperties { Persistent = true }, postId.ToByteArray(), timeout.Token);
        }

        while (!ct.IsCancellationRequested)
        {
            if (!channel.IsOpen)
                throw new InvalidOperationException("Feed outbox channel closed.");
            if (!await store.RelayNextAsync(PublishAsync, ct))
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await using var channel = await broker.OpenChannelAsync(ct, confirms: true);
        await FeedBroker.DeclareWorkAsync(channel, ct);
        await FeedBroker.DeclarePostedAsync(channel, ct);
        await channel.BasicQosAsync(0, 1, global: false, ct);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, reason) =>
        {
            stopped.TrySetException(new InvalidOperationException($"Feed work channel closed: {reason.ReplyText}"));
            return Task.CompletedTask;
        };
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.UnregisteredAsync += (_, _) =>
        {
            stopped.TrySetException(new InvalidOperationException("Feed work consumer was unregistered."));
            return Task.CompletedTask;
        };
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                if (delivery.Body.Length != 16)
                {
                    logger.LogWarning("Rejected invalid feed work message.");
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, ct);
                    return;
                }

                var postId = new Guid(delivery.Body.Span);
                var (posted, recipients) = await store.MaterializeAsync(postId, ct);
                if (posted is not null)
                {
                    var payload = JsonSerializer.SerializeToUtf8Bytes(posted);
                    var properties = new BasicProperties { ContentType = "application/json" };
                    foreach (var userId in recipients)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await channel.BasicPublishAsync(FeedBroker.PostedExchange, FeedBroker.RecipientKey(userId),
                            mandatory: false, properties, payload, timeout.Token);
                    }
                }
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            }
            catch (Exception error)
            {
                stopped.TrySetException(error);
            }
        };
        await channel.BasicConsumeAsync(FeedBroker.WorkQueue, autoAck: false, consumer, ct);
        await stopped.Task.WaitAsync(ct);
    }
}
