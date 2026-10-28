using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HackerNewsBestStories.Tests;

public sealed class BestStoriesApiTests : IDisposable
{
    private readonly FakeHackerNews _upstream = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public BestStoriesApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"{HackerNewsOptions.SectionName}:BaseAddress", FakeHackerNews.BaseAddress.ToString());
            builder.ConfigureTestServices(services =>
            {
                // Tests drive refreshes explicitly instead of racing the background loop.
                services.Remove(services.Single(d => d.ImplementationType == typeof(BestStoriesRefreshService)));
                services.AddHttpClient(BestStoriesCache.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _upstream);
                services.AddSingleton<TimeProvider>(_time);
            });
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Returns_503_with_retry_after_before_the_first_refresh()
    {
        var response = await _client.GetAsync("/api/stories/best?n=10");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(10), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, _upstream.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?n=")]
    [InlineData("?n=0")]
    [InlineData("?n=501")]
    [InlineData("?n=-1")]
    [InlineData("?n=abc")]
    [InlineData("?n=1.5")]
    [InlineData("?n=99999999999")]
    [InlineData("?n=1&n=2")]
    public async Task Rejects_missing_malformed_or_out_of_range_n(string query)
    {
        await RefreshAsync();

        var response = await _client.GetAsync($"/api/stories/best{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        var message = problem.RootElement.GetProperty("errors").GetProperty("n")[0].GetString();
        Assert.Contains("between 1 and 500", message);
    }

    [Fact]
    public async Task Validation_runs_even_before_the_first_refresh()
    {
        var response = await _client.GetAsync("/api/stories/best?n=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_the_top_n_stories_in_the_documented_shape()
    {
        _upstream.AddStory(1, 10);
        _upstream.AddStory(21233041, 1716, "A uBlock Origin update was rejected from the Chrome Web Store",
            "https://github.com/uBlockOrigin/uBlock-issues/issues/745", "ismaildonmez", 1570887781, 572);
        _upstream.AddStory(3, 50);
        await RefreshAsync();
        _time.Advance(TimeSpan.FromSeconds(42));

        var response = await _client.GetAsync("/api/stories/best?n=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("42", Assert.Single(response.Headers.GetValues("X-Snapshot-Age")));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetArrayLength());
        var first = body.RootElement[0];
        Assert.Equal(
            ["title", "uri", "postedBy", "time", "score", "commentCount"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("A uBlock Origin update was rejected from the Chrome Web Store", first.GetProperty("title").GetString());
        Assert.Equal("https://github.com/uBlockOrigin/uBlock-issues/issues/745", first.GetProperty("uri").GetString());
        Assert.Equal("ismaildonmez", first.GetProperty("postedBy").GetString());
        Assert.Equal("2019-10-12T13:43:01+00:00", first.GetProperty("time").GetString());
        Assert.Equal(1716, first.GetProperty("score").GetInt32());
        Assert.Equal(572, first.GetProperty("commentCount").GetInt32());
        Assert.Equal(50, body.RootElement[1].GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Returns_fewer_than_n_when_fewer_eligible_stories_exist()
    {
        _upstream.AddStory(1, 10);
        _upstream.AddStory(2, 20);
        _upstream.BestStoryIds.Add(3);
        _upstream.SetItem(3, new { id = 3, type = "story", deleted = true, time = 1 });
        await RefreshAsync();

        var stories = await GetStoriesAsync(500);

        Assert.Equal(2, stories.GetArrayLength());
    }

    [Fact]
    public async Task Concurrent_requests_are_served_from_the_snapshot_without_calling_upstream()
    {
        for (var id = 1; id <= 100; id++)
        {
            _upstream.AddStory(id, id);
        }
        await RefreshAsync();
        var upstreamCallsAfterRefresh = _upstream.RequestCount;

        var bodies = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => _client.GetStringAsync("/api/stories/best?n=25")));

        Assert.Single(bodies.Distinct());
        Assert.Equal(upstreamCallsAfterRefresh, _upstream.RequestCount);
    }

    [Fact]
    public async Task Allows_the_configured_client_origin_and_exposes_the_custom_headers()
    {
        await RefreshAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stories/best?n=1");
        request.Headers.Add("Origin", "http://localhost:5200");

        var response = await _client.SendAsync(request);

        Assert.Equal("http://localhost:5200", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("X-Snapshot-Age", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task Does_not_allow_other_origins()
    {
        await RefreshAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stories/best?n=1");
        request.Headers.Add("Origin", "https://example.com");

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private Task RefreshAsync() =>
        _factory.Services.GetRequiredService<BestStoriesCache>().RefreshAsync(CancellationToken.None);

    private async Task<JsonElement> GetStoriesAsync(int n)
    {
        var json = await _client.GetStringAsync($"/api/stories/best?n={n}");
        return JsonDocument.Parse(json).RootElement;
    }
}
