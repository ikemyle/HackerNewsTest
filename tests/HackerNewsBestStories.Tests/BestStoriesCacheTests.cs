using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Time.Testing;

namespace HackerNewsBestStories.Tests;

public class BestStoriesCacheTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeHackerNews _upstream = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IDistributedCache _sharedCache = FakeHackerNews.NewSharedCache();
    private readonly BestStoriesCache _cache;

    public BestStoriesCacheTests()
    {
        _cache = _upstream.CreateCache(_time, _sharedCache);
    }

    [Fact]
    public async Task Ranks_every_story_in_the_list_not_just_the_first_ones()
    {
        for (var id = 1; id <= 200; id++)
        {
            _upstream.AddStory(id, score: id % 50);
        }
        _upstream.AddStory(999, score: 5000);
        _upstream.AddStory(998, score: 49);

        await _cache.RefreshAsync(CancellationToken.None);

        var stories = _cache.Current!.Stories;
        Assert.Equal(202, stories.Length);
        Assert.Equal("Story 999", stories[0].Title);
        // Score ties are broken by ascending id: 49, 99, 149, 199 and 998 all score 49.
        Assert.Equal(["Story 49", "Story 99", "Story 149", "Story 199", "Story 998"], stories[1..6].Select(s => s.Title));
        Assert.Equal(stories.OrderByDescending(s => s.Score).Select(s => s.Score), stories.Select(s => s.Score));
    }

    [Fact]
    public async Task Maps_fields_converts_time_to_utc_and_links_to_hacker_news_when_there_is_no_url()
    {
        _upstream.AddStory(21233041, 1716, "A uBlock Origin update was rejected from the Chrome Web Store",
            "https://github.com/uBlockOrigin/uBlock-issues/issues/745", "ismaildonmez", 1570887781, 572);
        _upstream.BestStoryIds.Add(5);
        _upstream.SetItem(5, new { id = 5, type = "story", by = "pg", time = 1160418111, title = "Ask HN: no link", score = 10 });

        await _cache.RefreshAsync(CancellationToken.None);

        var stories = _cache.Current!.Stories;
        Assert.Equal(
            new Story(
                "A uBlock Origin update was rejected from the Chrome Web Store",
                "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
                "ismaildonmez",
                new DateTimeOffset(2019, 10, 12, 13, 43, 1, TimeSpan.Zero),
                1716,
                572),
            stories[0]);
        Assert.Equal(TimeSpan.Zero, stories[0].Time.Offset);
        Assert.Equal("https://news.ycombinator.com/item?id=5", stories[1].Uri);
        Assert.Equal(0, stories[1].CommentCount);
        Assert.Equal(_time.GetUtcNow(), _cache.Current.RefreshedAt);
    }

    [Fact]
    public async Task Excludes_null_deleted_dead_and_non_story_items()
    {
        _upstream.AddStory(1, 100);
        _upstream.BestStoryIds.AddRange([2, 3, 4, 5, 6]);
        _upstream.SetItem(2, null);
        _upstream.SetItem(3, new { id = 3, type = "story", deleted = true, score = 500, time = 1 });
        _upstream.SetItem(4, new { id = 4, type = "story", dead = true, title = "Dead", score = 500, time = 1 });
        _upstream.SetItem(5, new { id = 5, type = "job", title = "Hiring", score = 500, time = 1 });
        _upstream.SetItem(6, new { id = 6, type = "comment", text = "Hi", score = 500, time = 1 });

        await _cache.RefreshAsync(CancellationToken.None);

        var story = Assert.Single(_cache.Current!.Stories);
        Assert.Equal("Story 1", story.Title);
    }

    [Fact]
    public async Task Never_has_more_than_eight_item_requests_in_flight()
    {
        for (var id = 1; id <= 60; id++)
        {
            _upstream.AddStory(id, id);
        }

        // Hold every request until eight are open at once. A lower limit never opens the gate and times out.
        // A higher limit usually lets a ninth request in before the gate opens, but that part depends on scheduling.
        var limitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _upstream.ItemGate = (_, token) =>
        {
            if (_upstream.ItemRequestsInFlight >= 8)
            {
                limitReached.TrySetResult();
            }
            return limitReached.Task.WaitAsync(token);
        };

        await _cache.RefreshAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(8, _upstream.MaxItemRequestsInFlight);
        Assert.Equal(60, _cache.Current!.Stories.Length);
    }

    [Fact]
    public async Task A_failed_item_request_keeps_the_previous_complete_snapshot()
    {
        _upstream.AddStory(1, 10);
        _upstream.AddStory(2, 20);
        await _cache.RefreshAsync(CancellationToken.None);
        var previous = _cache.Current;

        _upstream.AddStory(3, 1000);
        _upstream.ItemFailures[2] = HttpStatusCode.InternalServerError;

        await Assert.ThrowsAsync<HttpRequestException>(() => _cache.RefreshAsync(CancellationToken.None));

        Assert.Same(previous, _cache.Current);
        Assert.Equal(["Story 2", "Story 1"], _cache.Current!.Stories.Select(s => s.Title));
    }

    [Fact]
    public async Task A_failed_story_list_request_keeps_the_previous_snapshot_and_a_later_refresh_recovers()
    {
        _upstream.AddStory(1, 10);
        await _cache.RefreshAsync(CancellationToken.None);
        var previous = _cache.Current;

        _upstream.BestStoriesStatus = HttpStatusCode.ServiceUnavailable;
        _upstream.AddStory(2, 20);
        await Assert.ThrowsAsync<HttpRequestException>(() => _cache.RefreshAsync(CancellationToken.None));
        Assert.Same(previous, _cache.Current);

        _upstream.BestStoriesStatus = HttpStatusCode.OK;
        _time.Advance(TimeSpan.FromSeconds(30));
        await _cache.RefreshAsync(CancellationToken.None);

        Assert.NotSame(previous, _cache.Current);
        Assert.Equal(["Story 2", "Story 1"], _cache.Current!.Stories.Select(s => s.Title));
        Assert.Equal(_time.GetUtcNow(), _cache.Current.RefreshedAt);
    }

    [Fact]
    public async Task Readers_only_ever_see_complete_sorted_snapshots_while_refreshes_publish()
    {
        PublishGeneration(1);
        await _cache.RefreshAsync(CancellationToken.None);

        var stop = false;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var reads = 0;
            while (!Volatile.Read(ref stop))
            {
                var stories = _cache.Current!.Stories;
                Assert.Equal(10, stories.Length);
                Assert.Single(stories.Select(s => s.PostedBy).Distinct());
                Assert.True(stories.Zip(stories.Skip(1)).All(pair => pair.First.Score > pair.Second.Score));
                reads++;
            }
            return reads;
        })).ToArray();

        for (var generation = 2; generation <= 25; generation++)
        {
            PublishGeneration(generation);
            await _cache.RefreshAsync(CancellationToken.None);
        }
        Volatile.Write(ref stop, true);

        var reads = await Task.WhenAll(readers).WaitAsync(TestTimeout);
        Assert.All(reads, count => Assert.True(count > 0));
        Assert.Equal("generation-25", _cache.Current!.Stories[0].PostedBy);
    }

    [Fact]
    public async Task Only_one_refresh_talks_to_upstream_at_a_time()
    {
        _upstream.AddStory(1, 10);
        var sync = new Lock();
        var inside = 0;
        var maxInside = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _upstream.BestStoriesGate = async token =>
        {
            lock (sync)
            {
                inside++;
                maxInside = Math.Max(maxInside, inside);
            }
            firstEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            lock (sync) inside--;
        };

        var first = _cache.RefreshAsync(CancellationToken.None);
        var second = _cache.RefreshAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var third = _cache.RefreshAsync(cancelled.Token);

        await firstEntered.Task.WaitAsync(TestTimeout);
        // A caller that gives up while waiting only abandons its own wait.
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        Assert.Equal(1, maxInside);
        Assert.NotNull(_cache.Current);
    }

    [Fact]
    public async Task Another_instance_can_load_the_published_snapshot_without_calling_upstream()
    {
        _upstream.AddStory(1, 10);
        _upstream.AddStory(2, 20, url: "");
        await _cache.RefreshAsync(CancellationToken.None);

        var otherUpstream = new FakeHackerNews();
        var other = otherUpstream.CreateCache(_time, _sharedCache);
        await other.LoadSharedSnapshotAsync(CancellationToken.None);

        Assert.Equal(_cache.Current!.Stories.ToArray(), other.Current!.Stories.ToArray());
        Assert.Equal(_cache.Current.RefreshedAt, other.Current.RefreshedAt);
        Assert.Equal(0, otherUpstream.RequestCount);
    }

    [Fact]
    public async Task An_older_shared_snapshot_does_not_replace_a_newer_local_one()
    {
        _upstream.AddStory(1, 10);
        await _cache.RefreshAsync(CancellationToken.None);
        var local = _cache.Current!;

        var older = new StorySnapshot([new Story("Old", "https://example.com", "x", local.RefreshedAt, 1, 0)], local.RefreshedAt.AddMinutes(-5));
        await _sharedCache.SetAsync(TestSettings.SharedCache().SnapshotKey, JsonSerializer.SerializeToUtf8Bytes(older, JsonSerializerOptions.Web));

        await _cache.LoadSharedSnapshotAsync(CancellationToken.None);

        Assert.Same(local, _cache.Current);
    }

    [Fact]
    public async Task A_broken_shared_cache_does_not_stop_refreshes()
    {
        _upstream.AddStory(1, 10);
        var cache = _upstream.CreateCache(_time, new UnavailableDistributedCache());

        await cache.LoadSharedSnapshotAsync(CancellationToken.None);
        Assert.Null(cache.Current);

        await cache.RefreshAsync(CancellationToken.None);
        Assert.Equal("Story 1", Assert.Single(cache.Current!.Stories).Title);
    }

    private void PublishGeneration(int generation)
    {
        _upstream.Clear();
        for (var id = 1; id <= 10; id++)
        {
            _upstream.AddStory(id, generation * 100 + id, by: $"generation-{generation}");
        }
    }

    // Behaves like a Redis cache whose server cannot be reached.
    private sealed class UnavailableDistributedCache : IDistributedCache
    {
        private static Exception Unavailable() => new InvalidOperationException("Shared cache unavailable.");

        public byte[]? Get(string key) => throw Unavailable();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromException<byte[]?>(Unavailable());
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Unavailable();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.FromException(Unavailable());
        public void Refresh(string key) => throw Unavailable();
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.FromException(Unavailable());
        public void Remove(string key) => throw Unavailable();
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.FromException(Unavailable());
    }
}
