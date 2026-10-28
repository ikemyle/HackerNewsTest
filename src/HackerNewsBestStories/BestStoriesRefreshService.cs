using Microsoft.Extensions.Options;

namespace HackerNewsBestStories;

public sealed class BestStoriesRefreshService(
    BestStoriesCache cache,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider,
    ILogger<BestStoriesRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await cache.LoadSharedSnapshotAsync(stoppingToken);
                var age = timeProvider.GetUtcNow() - cache.Current?.RefreshedAt;

                if (age < settings.RefreshInterval)
                {
                    // Another instance refreshed recently enough, so skip the upstream requests this round.
                    // Instances tick independently, so data can be up to about two intervals old.
                    consecutiveFailures = 0;
                    delay = settings.RefreshInterval;
                    logger.LogDebug("Using the shared best-stories snapshot from {Age} ago", age);
                }
                else
                {
                    var started = timeProvider.GetTimestamp();
                    await cache.RefreshAsync(stoppingToken);
                    consecutiveFailures = 0;
                    delay = settings.RefreshInterval;
                    logger.LogInformation(
                        "Refreshed best stories: {Count} stories in {ElapsedMs:F0} ms",
                        cache.Current!.Stories.Length,
                        timeProvider.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                consecutiveFailures++;
                // Back off exponentially, but never wait longer than a normal refresh would.
                var backoff = settings.FailureRetryDelay * Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
                delay = backoff < settings.RefreshInterval ? backoff : settings.RefreshInterval;
                logger.LogWarning(
                    ex,
                    "Refreshing best stories failed ({Failures} in a row); serving the previous snapshot and retrying in {Delay}",
                    consecutiveFailures,
                    delay);
            }

            await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }
}
