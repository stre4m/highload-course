using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace SocialNetwork;

internal static class FeedSocket
{
    internal static async Task SubscribeAsync(HttpContext context, FeedSocketSubscriptions subscriptions,
        ILoggerFactory logs)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var authentication = await context.AuthenticateAsync();
        var expiresIn = authentication.Properties?.ExpiresUtc - DateTimeOffset.UtcNow;
        if (expiresIn <= TimeSpan.Zero)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (expiresIn is { } lifetime)
            stopped.CancelAfter(lifetime);
        try
        {
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
            setup.CancelAfter(TimeSpan.FromSeconds(5));
            await using var subscription = await subscriptions.SubscribeAsync(userId, setup.Token);
            setup.Token.ThrowIfCancellationRequested();
            if (subscription is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await RelayAsync(socket, subscription, stopped);
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (context.Response.HasStarted)
        {
        }
        catch (Exception error)
        {
            logs.CreateLogger(typeof(FeedSocket).FullName!).LogWarning(error, "Feed subscription ended.");
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            else
                context.Abort();
        }
    }

    private static async Task RelayAsync(WebSocket socket, FeedSocketSubscriptions.Subscription subscription,
        CancellationTokenSource stopped)
    {
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
        var read = ReceiveUntilClosedAsync(socket, stopped.Token);
        var write = SendUpdatesAsync(socket, subscription, sending.Token);
        var completed = await Task.WhenAny(read, write, subscription.Disrupted);
        if (completed == read && read.IsCompletedSuccessfully)
        {
            // Отправка Close не пересекается с отправкой пользовательского кадра.
            sending.Cancel();
            try
            {
                await write;
            }
            catch (OperationCanceledException) when (sending.IsCancellationRequested)
            {
            }
            if (socket.State == WebSocketState.CloseReceived)
            {
                using var close = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                close.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, close.Token);
            }
            return;
        }

        socket.Abort();
        stopped.Cancel();
        await Task.WhenAll(read, write);
    }

    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        while ((await socket.ReceiveAsync(buffer.AsMemory(), ct)).MessageType != WebSocketMessageType.Close)
        {
        }
    }

    private static async Task SendUpdatesAsync(WebSocket socket, FeedSocketSubscriptions.Subscription subscription,
        CancellationToken ct)
    {
        await foreach (var payload in subscription.Buffer.Reader.ReadAllAsync(ct))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        }
    }
}
