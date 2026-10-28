using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace HackerNewsBestStories;

public sealed class BestStoriesCache(
    IHttpClientFactory httpClientFactory,
    IDistributedCache sharedCache,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider,
    ILogger<BestStoriesCache> logger)
{
    public const string HttpClientName = "HackerNews";
    public const string SharedCacheKey = "hacker-news:best-stories";

    // Serializes refreshes and shared-cache loads so an older shared snapshot can never overwrite a newer local one.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile StorySnapshot? _current;

    // Null until the first refresh (or shared-cache load) succeeds. Requests only ever read this local copy.
    public StorySnapshot? Current => _current;

    // Fetches the whole best-story list and publishes a new snapshot, or throws and leaves the current one in place.
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // The background service is the only regular caller, but the lock keeps any other caller
        // from starting a second set of upstream requests while one is in flight.
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var settings = options.Value;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(settings.RefreshTimeout);

            StorySnapshot snapshot;
            try
            {
                snapshot = await FetchSnapshotAsync(settings, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Refreshing best stories did not finish within {settings.RefreshTimeout}.");
            }

            _current = snapshot;
            await WriteSharedSnapshotAsync(snapshot, cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // Adopts the snapshot in the shared cache when it is newer than ours. With Redis this is how an instance
    // picks up another instance's refresh; with the in-memory provider it only ever finds its own.
    public async Task LoadSharedSnapshotAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            StorySnapshot? shared;
            try
            {
                var bytes = await sharedCache.GetAsync(SharedCacheKey, cancellationToken);
                shared = bytes is null ? null : JsonSerializer.Deserialize<StorySnapshot>(bytes, JsonSerializerOptions.Web);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The shared cache only saves upstream requests; Hacker News remains the source of truth.
                logger.LogWarning(ex, "Could not read the shared best-stories snapshot; refreshing from Hacker News instead");
                return;
            }

            if (shared is { Stories.IsDefault: false } && (_current is null || shared.RefreshedAt > _current.RefreshedAt))
            {
                _current = shared;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task WriteSharedSnapshotAsync(StorySnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            // No expiry: like the local copy, the last good snapshot is better than nothing during an outage.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonSerializerOptions.Web);
            await sharedCache.SetAsync(SharedCacheKey, bytes, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The local snapshot is already published, so this refresh still counts as a success.
            logger.LogWarning(ex, "Could not write the best-stories snapshot to the shared cache");
        }
    }

    private async Task<StorySnapshot> FetchSnapshotAsync(HackerNewsOptions settings, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        var ids = await client.GetFromJsonAsync<int[]>("beststories.json", cancellationToken)
            ?? throw new InvalidDataException("Hacker News returned null for the best-story list.");

        // The list is not guaranteed to be ordered by score, so every item is fetched before ranking.
        // Any failed item request throws, cancels the remaining requests and leaves the old snapshot published;
        // a partial ranking could silently drop the top story.
        var stories = new ConcurrentBag<HackerNewsItem>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = settings.MaxConcurrentRequests,
            CancellationToken = cancellationToken,
        };
        await Parallel.ForEachAsync(ids.Distinct(), parallelOptions, async (id, token) =>
        {
            var item = await client.GetFromJsonAsync<HackerNewsItem>($"item/{id}.json", token);
            if (item is { Type: "story", Deleted: false, Dead: false, Title: not null })
            {
                stories.Add(item);
            }
        });

        var ranked = stories
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Id)
            .Select(item => new Story(
                item.Title!,
                string.IsNullOrEmpty(item.Url) ? $"https://news.ycombinator.com/item?id={item.Id}" : item.Url,
                item.By,
                DateTimeOffset.FromUnixTimeSeconds(item.Time),
                item.Score,
                item.Descendants))
            .ToImmutableArray();

        return new StorySnapshot(ranked, timeProvider.GetUtcNow());
    }
}
