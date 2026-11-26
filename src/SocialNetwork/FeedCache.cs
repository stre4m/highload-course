using Microsoft.Extensions.Caching.Memory;

namespace SocialNetwork;

public sealed class FeedCache(PostStore store, FeedRefreshQueue refreshQueue) : IDisposable
{
    private const int FeedSize = 1000;
    private const int AuthorBatchSize = 8;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private static readonly IComparer<(DateTime, Guid)> NewestFirst =
        Comparer<(DateTime, Guid)>.Create((left, right) => right.CompareTo(left));

    public async Task<IReadOnlyList<Post>> GetAsync(Guid userId, int offset, int limit, CancellationToken ct)
    {
        var (feed, _) = await GetOrBuildAsync(userId, ct);
        return offset >= feed.Length
            ? Array.Empty<Post>()
            : new ArraySegment<Post>(feed, offset, Math.Min(limit, feed.Length - offset));
    }

    internal async Task<int?> RefreshAsync(Guid userId, CancellationToken ct)
    {
        var (feed, stored) = await GetOrBuildAsync(userId, ct);
        return stored ? feed.Length : null;
    }

    private async Task<(Post[] Feed, bool Stored)> GetOrBuildAsync(Guid userId, CancellationToken ct)
    {
        var key = new FeedKey(userId);
        if (cache.TryGetValue(key, out Post[]? feed))
            return (feed!, false);

        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out feed))
                return (feed!, false);

            feed = await BuildAsync(userId, ct);
            cache.Set(key, feed, EntryOptions(feed, static post => post));
            return (feed, cache.TryGetValue(key, out _));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Post[]> BuildAsync(Guid userId, CancellationToken ct)
    {
        var friends = await store.GetFriendsAsync(userId, ct);
        IReadOnlyList<StoredPost> newest = Array.Empty<StoredPost>();
        foreach (var batch in friends.Chunk(AuthorBatchSize))
        {
            var queue = new PriorityQueue<(IReadOnlyList<StoredPost> Posts, int Index), (DateTime, Guid)>(NewestFirst);
            Enqueue(newest, 0);
            var missing = new List<Guid>();
            foreach (var friendId in batch)
            {
                if (cache.TryGetValue(new AuthorKey(friendId), out IReadOnlyList<StoredPost>? authorPosts))
                    Enqueue(authorPosts!, 0);
                else
                    missing.Add(friendId);
            }

            if (missing.Count > 0)
            {
                var loaded = await store.GetRecentPostsAsync(missing.ToArray(), FeedSize, ct);
                foreach (var authorId in missing)
                {
                    IReadOnlyList<StoredPost> authorPosts = loaded.TryGetValue(authorId, out var posts)
                        ? posts
                        : Array.Empty<StoredPost>();
                    cache.Set(new AuthorKey(authorId), authorPosts, EntryOptions(authorPosts, static item => item.Post));
                    Enqueue(authorPosts, 0);
                }
            }

            var merged = new List<StoredPost>();
            while (merged.Count < FeedSize && queue.TryDequeue(out var item, out _))
            {
                merged.Add(item.Posts[item.Index]);
                Enqueue(item.Posts, item.Index + 1);
            }
            newest = merged;

            void Enqueue(IReadOnlyList<StoredPost> posts, int index)
            {
                if (index >= posts.Count)
                    return;

                var post = posts[index];
                queue.Enqueue((posts, index), (post.CreatedAt, post.Post.Id));
            }
        }
        return newest.Select(static item => item.Post).ToArray();
    }

    public Task<Guid> CreatePostAsync(Guid authorId, string text, CancellationToken ct) =>
        MutatePostsAsync(authorId, token => store.CreateAsync(authorId, text, token), ct);

    public Task<bool> UpdatePostAsync(Guid authorId, Guid postId, string text, CancellationToken ct) =>
        MutatePostsAsync(authorId, token => store.UpdateAsync(authorId, postId, text, token), ct);

    public Task<bool> DeletePostAsync(Guid authorId, Guid postId, CancellationToken ct) =>
        MutatePostsAsync(authorId, token => store.DeleteAsync(authorId, postId, token), ct);

    private async Task<T> MutatePostsAsync<T>(
        Guid authorId, Func<CancellationToken, Task<T>> mutation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var followers = await store.GetFollowersAsync(authorId, ct);
            ct.ThrowIfCancellationRequested();
            try
            {
                return await mutation(CancellationToken.None);
            }
            finally
            {
                // Сбрасываем кеш и при ошибке, поскольку запись могла успеть зафиксироваться.
                cache.Remove(new AuthorKey(authorId));
                foreach (var followerId in followers)
                    InvalidateFeed(followerId);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<bool> AddFriendAsync(Guid userId, Guid friendId, CancellationToken ct) =>
        MutateFriendsAsync(userId, token => store.AddFriendAsync(userId, friendId, token), ct);

    public Task<bool> DeleteFriendAsync(Guid userId, Guid friendId, CancellationToken ct) =>
        MutateFriendsAsync(userId, token => store.DeleteFriendAsync(userId, friendId, token), ct);

    private async Task<bool> MutateFriendsAsync(
        Guid userId, Func<CancellationToken, Task<bool>> mutation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await mutation(CancellationToken.None);
        }
        finally
        {
            InvalidateFeed(userId);
            gate.Release();
        }
    }

    private void InvalidateFeed(Guid userId)
    {
        var key = new FeedKey(userId);
        var wasCached = cache.TryGetValue(key, out _);
        cache.Remove(key);
        // При заполненной очереди лента будет собрана при следующем чтении.
        if (wasCached)
            refreshQueue.TryEnqueue(userId);
    }

    private static MemoryCacheEntryOptions EntryOptions<T>(IReadOnlyList<T> posts, Func<T, Post> getPost)
    {
        var size = 256L;
        foreach (var entry in posts)
            size += 128L + getPost(entry).Text.Length * sizeof(char);

        return new MemoryCacheEntryOptions { Size = size, AbsoluteExpirationRelativeToNow = Lifetime };
    }

    public void Dispose()
    {
        cache.Dispose();
        gate.Dispose();
    }

    private readonly record struct FeedKey(Guid UserId);
    private readonly record struct AuthorKey(Guid AuthorId);
}
