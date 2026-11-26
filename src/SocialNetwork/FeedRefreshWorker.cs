namespace SocialNetwork;

internal sealed class FeedRefreshWorker(
    FeedRefreshQueue queue, FeedCache feeds, ILogger<FeedRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var userId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (await feeds.RefreshAsync(userId, stoppingToken) is { } count)
                    logger.LogInformation("Feed refreshed from queue for {UserId}: {PostCount} posts", userId, count);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Failed to refresh feed from queue for {UserId}", userId);
            }
        }
    }
}
