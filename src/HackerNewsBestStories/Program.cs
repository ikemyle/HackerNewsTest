using System.Globalization;
using HackerNewsBestStories;
using Microsoft.Extensions.Options;

const int MaxStories = 500;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<HackerNewsOptions>()
    .Bind(builder.Configuration.GetSection(HackerNewsOptions.SectionName))
    .Validate(
        options => options.RefreshInterval > TimeSpan.Zero
            && options.FailureRetryDelay > TimeSpan.Zero
            && options.RequestTimeout > TimeSpan.Zero
            && options.RefreshTimeout > TimeSpan.Zero
            && options.MaxConcurrentRequests > 0,
        "HackerNews timeouts and intervals must be positive and MaxConcurrentRequests at least 1.")
    .ValidateOnStart();

builder.Services.AddHttpClient(BestStoriesCache.HttpClientName, (services, client) =>
{
    var options = services.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
    client.Timeout = options.RequestTimeout;
});

// The shared cache holds the latest snapshot so instances can reuse each other's refreshes.
// "Memory" keeps it in-process (one instance); "Redis" shares it between instances.
var cacheProvider = builder.Configuration["Cache:Provider"] ?? "Memory";
if (cacheProvider.Equals("Memory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDistributedMemoryCache();
}
else if (cacheProvider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
{
    var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("Cache:Provider is Redis but ConnectionStrings:Redis is not set.");
    builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redisConnectionString);
}
else
{
    throw new InvalidOperationException($"Unknown Cache:Provider '{cacheProvider}'. Use Memory or Redis.");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BestStoriesCache>();
builder.Services.AddHostedService<BestStoriesRefreshService>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/api/stories/best", (string? n, BestStoriesCache cache, TimeProvider timeProvider, HttpResponse response) =>
{
    if (string.IsNullOrEmpty(n))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["n"] = [$"The n query parameter is required and must be an integer between 1 and {MaxStories}."],
        });
    }

    if (!int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > MaxStories)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["n"] = [$"'{n}' is not valid; n must be an integer between 1 and {MaxStories}."],
        });
    }

    var snapshot = cache.Current;
    if (snapshot is null)
    {
        response.Headers.RetryAfter = "10";
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Best stories are not available yet.",
            detail: "The first refresh from Hacker News has not completed. Retry shortly.");
    }

    var age = timeProvider.GetUtcNow() - snapshot.RefreshedAt;
    response.Headers["X-Snapshot-Age"] = Math.Max(0, (long)age.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    return Results.Ok(snapshot.Stories.Take(count));
});

app.Run();
