using System.Globalization;
using HackerNewsBestStories;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<HackerNewsOptions>()
    .BindConfiguration(HackerNewsOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ApiOptions>()
    .BindConfiguration(ApiOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SharedCacheOptions>()
    .BindConfiguration(SharedCacheOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient(BestStoriesCache.HttpClientName, (services, client) =>
{
    var options = services.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
    client.BaseAddress = options.BaseAddress;
    client.Timeout = options.RequestTimeout;
});

// The shared cache holds the latest snapshot so instances can reuse each other's refreshes.
// "Memory" keeps it in-process (one instance); "Redis" shares it between instances.
// The provider decides which services to register, so it is read here rather than through IOptions.
var cacheProvider = builder.Configuration.GetSection(SharedCacheOptions.SectionName).Get<SharedCacheOptions>()?.Provider
    ?? throw new InvalidOperationException("Cache:Provider is not set. Use Memory or Redis.");
switch (cacheProvider)
{
    case CacheProvider.Memory:
        builder.Services.AddDistributedMemoryCache();
        break;
    case CacheProvider.Redis:
        var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Cache:Provider is Redis but ConnectionStrings:Redis is not set.");
        builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redisConnectionString);
        break;
    default:
        throw new InvalidOperationException($"Unknown Cache:Provider '{cacheProvider}'. Use Memory or Redis.");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BestStoriesCache>();
builder.Services.AddHostedService<BestStoriesRefreshService>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/api/stories/best", (
    string? n,
    BestStoriesCache cache,
    IOptions<ApiOptions> apiOptions,
    TimeProvider timeProvider,
    HttpResponse response) =>
{
    var settings = apiOptions.Value;
    if (string.IsNullOrEmpty(n))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["n"] = [$"The n query parameter is required and must be an integer between 1 and {settings.MaxStories}."],
        });
    }

    if (!int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || count > settings.MaxStories)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["n"] = [$"'{n}' is not valid; n must be an integer between 1 and {settings.MaxStories}."],
        });
    }

    var snapshot = cache.Current;
    if (snapshot is null)
    {
        response.Headers.RetryAfter = ((long)settings.ColdStartRetryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
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
