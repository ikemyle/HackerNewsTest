using System.Globalization;
using System.Text.Json.Serialization;
using HackerNewsBestStories;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

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
builder.Services.AddOpenApi();
// The web defaults also accept numbers written as strings, which shows up in the OpenAPI schema as
// "integer | string". This API only ever writes numbers, so keep the schema exact.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Services.GetRequiredService<IOptions<ApiOptions>>().Value.EnableSwagger)
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Hacker News best stories"));
}

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
})
.WithName("GetBestStories")
.WithSummary("Highest-scoring stories from the current Hacker News best-story list")
.Produces<Story[]>(StatusCodes.Status200OK)
.ProducesValidationProblem()
.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
.AddOpenApiOperationTransformer((operation, context, _) =>
{
    // n is bound as a string so malformed values get our own 400 message; document it as the integer it is.
    var maxStories = context.ApplicationServices.GetRequiredService<IOptions<ApiOptions>>().Value.MaxStories;
    if (operation.Parameters?.FirstOrDefault(p => p.Name == "n") is OpenApiParameter n)
    {
        n.Required = true;
        n.Description = $"Number of stories to return, 1 to {maxStories}.";
        n.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer,
            Format = "int32",
            Minimum = "1",
            Maximum = maxStories.ToString(CultureInfo.InvariantCulture),
        };
    }

    AddHeader("200", "X-Snapshot-Age", "Age of the data in whole seconds.");
    AddHeader("503", "Retry-After", "Seconds to wait before retrying.");
    return Task.CompletedTask;

    void AddHeader(string status, string name, string description)
    {
        if (operation.Responses?.GetValueOrDefault(status) is OpenApiResponse response)
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers[name] = new OpenApiHeader
            {
                Description = description,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
            };
        }
    }
});

app.Run();
