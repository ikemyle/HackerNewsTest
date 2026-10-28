using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HackerNewsBestStories.Tests;

// In-memory stand-in for the Hacker News API, used as the primary handler of the HttpClient.
public sealed class FakeHackerNews : HttpMessageHandler
{
    public static readonly Uri BaseAddress = new("https://hacker-news.test/v0/");

    private readonly ConcurrentDictionary<int, string> _items = new();
    private readonly Lock _counterLock = new();
    private int _itemRequestsInFlight;
    private int _requestCount;

    public List<int> BestStoryIds { get; } = [];

    public HttpStatusCode BestStoriesStatus { get; set; } = HttpStatusCode.OK;

    public ConcurrentDictionary<int, HttpStatusCode> ItemFailures { get; } = new();

    // Awaited inside the request, so tests can hold requests open at a known point.
    public Func<CancellationToken, Task>? BestStoriesGate { get; set; }

    public Func<int, CancellationToken, Task>? ItemGate { get; set; }

    public int MaxItemRequestsInFlight { get; private set; }

    public int ItemRequestsInFlight
    {
        get { lock (_counterLock) return _itemRequestsInFlight; }
    }

    public int RequestCount => Volatile.Read(ref _requestCount);

    public void AddStory(
        int id,
        int score,
        string? title = null,
        string? url = null,
        string by = "author",
        long time = 1570887781,
        int descendants = 0)
    {
        BestStoryIds.Add(id);
        SetItem(id, new
        {
            id,
            type = "story",
            by,
            time,
            title = title ?? $"Story {id}",
            url = url ?? $"https://example.com/{id}",
            score,
            descendants,
        });
    }

    public BestStoriesCache CreateCache(TimeProvider time, IDistributedCache? sharedCache = null, HackerNewsOptions? options = null) =>
        new(
            new FakeHttpClientFactory(this),
            sharedCache ?? NewSharedCache(),
            Options.Create(options ?? TestSettings.HackerNews()),
            Options.Create(TestSettings.SharedCache()),
            time,
            NullLogger<BestStoriesCache>.Instance);

    public static IDistributedCache NewSharedCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    public void SetItem(int id, object? item) => _items[id] = JsonSerializer.Serialize(item);

    public void Clear()
    {
        BestStoryIds.Clear();
        _items.Clear();
        ItemFailures.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        var path = request.RequestUri!.AbsolutePath;

        if (path == "/v0/beststories.json")
        {
            if (BestStoriesGate is { } gate)
            {
                await gate(cancellationToken);
            }

            return BestStoriesStatus == HttpStatusCode.OK
                ? Json(JsonSerializer.Serialize(BestStoryIds))
                : new HttpResponseMessage(BestStoriesStatus);
        }

        if (path.StartsWith("/v0/item/") && path.EndsWith(".json"))
        {
            var id = int.Parse(path["/v0/item/".Length..^".json".Length]);
            lock (_counterLock)
            {
                _itemRequestsInFlight++;
                MaxItemRequestsInFlight = Math.Max(MaxItemRequestsInFlight, _itemRequestsInFlight);
            }

            try
            {
                if (ItemGate is { } gate)
                {
                    await gate(id, cancellationToken);
                }

                if (ItemFailures.TryGetValue(id, out var status))
                {
                    return new HttpResponseMessage(status);
                }

                // The real API answers 200 with a literal null for unknown ids.
                return Json(_items.GetValueOrDefault(id, "null"));
            }
            finally
            {
                lock (_counterLock) _itemRequestsInFlight--;
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = FakeHackerNews.BaseAddress };
}
