namespace HackerNewsBestStories;

public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    public Uri BaseAddress { get; set; } = new("https://hacker-news.firebaseio.com/v0/");

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    // First delay after a failed refresh; doubles on each consecutive failure up to RefreshInterval.
    public TimeSpan FailureRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public int MaxConcurrentRequests { get; set; } = 8;
}
