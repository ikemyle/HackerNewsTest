using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HackerNewsBestStories.Tests;

public class BestStoriesRefreshServiceTests
{
    [Fact]
    public async Task Retries_failed_refreshes_with_growing_delays_then_returns_to_the_normal_interval()
    {
        var upstream = new FakeHackerNews { BestStoriesStatus = HttpStatusCode.InternalServerError };
        var time = new TimerRecordingTimeProvider();
        var cache = upstream.CreateCache(time);
        using var service = CreateService(cache, time);

        await service.StartAsync(CancellationToken.None);

        // Each awaited delay means the previous refresh attempt has finished.
        Assert.Equal(TimeSpan.FromSeconds(10), await time.NextTimerAsync());
        Assert.Null(cache.Current);
        Assert.Equal(1, upstream.RequestCount);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(20), await time.NextTimerAsync());
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(40), await time.NextTimerAsync());
        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(TimeSpan.FromSeconds(60), await time.NextTimerAsync());
        Assert.Equal(4, upstream.RequestCount);

        upstream.BestStoriesStatus = HttpStatusCode.OK;
        upstream.AddStory(1, 100);
        time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(60), await time.NextTimerAsync());
        Assert.Equal("Story 1", Assert.Single(cache.Current!.Stories).Title);

        // A later failure falls back to the short retry delay and keeps the snapshot.
        var snapshot = cache.Current;
        upstream.ItemFailures[1] = HttpStatusCode.BadGateway;
        time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(10), await time.NextTimerAsync());
        Assert.Same(snapshot, cache.Current);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stops_promptly_when_the_host_shuts_down_during_a_refresh()
    {
        var upstream = new FakeHackerNews();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.BestStoriesGate = async token =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var time = new FakeTimeProvider();
        var cache = upstream.CreateCache(time);
        using var service = CreateService(cache, time);

        await service.StartAsync(CancellationToken.None);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(service.ExecuteTask!.IsCanceled);
        Assert.Null(cache.Current);
    }

    [Fact]
    public async Task Uses_a_recent_snapshot_from_another_instance_instead_of_calling_upstream()
    {
        var sharedCache = FakeHackerNews.NewSharedCache();
        var time = new TimerRecordingTimeProvider();

        var otherUpstream = new FakeHackerNews();
        otherUpstream.AddStory(1, 100, "From the other instance");
        await otherUpstream.CreateCache(time, sharedCache).RefreshAsync(CancellationToken.None);

        var upstream = new FakeHackerNews();
        upstream.AddStory(2, 200, "From this instance");
        var cache = upstream.CreateCache(time, sharedCache);
        using var service = CreateService(cache, time);

        time.Advance(TimeSpan.FromSeconds(30));
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(60), await time.NextTimerAsync());
        Assert.Equal("From the other instance", Assert.Single(cache.Current!.Stories).Title);
        Assert.Equal(0, upstream.RequestCount);

        // By the next tick the shared snapshot is 90 seconds old, so this instance refreshes it itself.
        time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(60), await time.NextTimerAsync());
        Assert.Equal("From this instance", Assert.Single(cache.Current!.Stories).Title);
        Assert.Equal(2, upstream.RequestCount);

        await service.StopAsync(CancellationToken.None);
    }

    private static BestStoriesRefreshService CreateService(BestStoriesCache cache, TimeProvider time) =>
        new(cache, Options.Create(TestSettings.HackerNews()), time, NullLogger<BestStoriesRefreshService>.Instance);

    // Reports each timer the service creates, so the test can advance the clock only once
    // the service is actually waiting on it.
    private sealed class TimerRecordingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timers.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task<TimeSpan> NextTimerAsync() =>
            await _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
