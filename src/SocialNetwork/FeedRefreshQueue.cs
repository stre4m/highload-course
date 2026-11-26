using System.Threading.Channels;

namespace SocialNetwork;

public sealed class FeedRefreshQueue
{
    private readonly Channel<Guid> channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });

    public bool TryEnqueue(Guid userId) => channel.Writer.TryWrite(userId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}
