using System.ComponentModel.DataAnnotations;

namespace HackerNewsBestStories;

// All values come from appsettings.json (or environment overrides). Nothing here has a default,
// so a missing setting fails validation at startup instead of silently falling back to a value in code.

public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    [Required]
    public Uri? BaseAddress { get; set; }

    [Required]
    public string BestStoriesPath { get; set; } = "";

    [Required, RegularExpression(".*\\{id\\}.*", ErrorMessage = "ItemPathTemplate must contain {{id}}.")]
    public string ItemPathTemplate { get; set; } = "";

    // Used as the story's uri when it has no external url (Ask HN and similar).
    [Required, RegularExpression(".*\\{id\\}.*", ErrorMessage = "DiscussionUrlTemplate must contain {{id}}.")]
    public string DiscussionUrlTemplate { get; set; } = "";

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan RefreshInterval { get; set; }

    // First delay after a failed refresh; doubles on each consecutive failure up to RefreshInterval.
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan FailureRetryDelay { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan RequestTimeout { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan RefreshTimeout { get; set; }

    [Range(1, 100)]
    public int MaxConcurrentRequests { get; set; }
}

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    // Upper bound for the n query parameter.
    [Range(1, 10_000)]
    public int MaxStories { get; set; }

    // Sent as Retry-After on the 503 returned before the first snapshot exists.
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan ColdStartRetryAfter { get; set; }

    // Serves the OpenAPI document at /openapi/v1.json and Swagger UI at /swagger.
    public bool EnableSwagger { get; set; }
}

public enum CacheProvider
{
    Memory,
    Redis,
}

public sealed class SharedCacheOptions
{
    public const string SectionName = "Cache";

    [Required]
    public CacheProvider? Provider { get; set; }

    [Required]
    public string SnapshotKey { get; set; } = "";
}
