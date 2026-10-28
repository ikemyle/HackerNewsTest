using System.Net;
using System.Text;
using Bunit;
using HackerNewsBestStories.Client.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;

namespace HackerNewsBestStories.Client.Tests;

// Composes BunitContext rather than inheriting it: MudBlazor registers services that can only be
// disposed asynchronously, and xUnit v2 would call the synchronous Dispose on the test class.
public class HomePageTests : IAsyncLifetime
{
    private readonly BunitContext _context = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<Uri> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => Stories("[]");

    public HomePageTests()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        _context.Services.AddMudServices();
        var settings = new ApiSettings
        {
            BaseAddress = new Uri("http://api.test/"),
            DefaultStoryCount = 10,
            MaxStories = 500,
            RefreshIntervalSeconds = 60,
            RefreshPollSeconds = 5,
            AutoReloadByDefault = false,
        };
        _context.Services.AddSingleton(settings);
        _context.Services.AddSingleton<TimeProvider>(_time);
        _context.Services.AddSingleton(new BestStoriesApiClient(new HttpClient(new StubHandler(this)) { BaseAddress = settings.BaseAddress }));
    }

    [Fact]
    public void Loads_the_default_number_of_stories_on_start_and_lists_them_by_rank()
    {
        _respond = _ => Stories(
            """
            [
              {"title":"First","uri":"https://example.com/1","postedBy":"alice","time":"2019-10-12T13:43:01+00:00","score":300,"commentCount":12},
              {"title":"Second","uri":"https://news.ycombinator.com/item?id=2","postedBy":"bob","time":"2019-10-12T14:00:00+00:00","score":200,"commentCount":0}
            ]
            """,
            snapshotAge: 42);

        var page = _context.Render<Home>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
        Assert.Equal("http://api.test/api/stories/best?n=10", Assert.Single(_requests).ToString());
        Assert.Contains("2 stories", page.Markup);
        Assert.Contains("Cache refresh in 18 s", page.Markup);

        var first = page.FindAll("tbody tr")[0];
        Assert.Equal(["1", "First", "alice", "2019-10-12 13:43", "300", "12"], first.QuerySelectorAll("td").Select(td => td.TextContent.Trim()));
        Assert.Equal("https://example.com/1", first.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void Requests_the_number_of_stories_entered()
    {
        var page = _context.Render<Home>();
        page.WaitForAssertion(() => Assert.Single(_requests));

        page.Find("input").Change("3");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal("http://api.test/api/stories/best?n=3", _requests[^1].ToString()));
    }

    [Fact]
    public void Shows_the_api_validation_message()
    {
        _respond = _ => Problem(HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"n":["'0' is not valid; n must be an integer between 1 and 500."]}}""");

        var page = _context.Render<Home>();

        page.WaitForAssertion(() =>
            Assert.Contains("n must be an integer between 1 and 500.", page.Find(".mud-alert").TextContent));
        Assert.Empty(page.FindAll("tbody tr"));
    }

    [Fact]
    public void Shows_when_the_api_is_still_warming_up()
    {
        _respond = _ =>
        {
            var response = Problem(HttpStatusCode.ServiceUnavailable,
                """{"title":"Best stories are not available yet.","status":503}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return response;
        };

        var page = _context.Render<Home>();

        page.WaitForAssertion(() =>
            Assert.Contains("Best stories are not available yet. Try again in 10 seconds.", page.Find(".mud-alert").TextContent));
    }

    [Fact]
    public void Shows_when_the_api_cannot_be_reached()
    {
        _respond = _ => throw new HttpRequestException("Connection refused");

        var page = _context.Render<Home>();

        page.WaitForAssertion(() =>
            Assert.Contains("Could not reach the API at http://api.test/", page.Find(".mud-alert").TextContent));
    }

    [Fact]
    public void Counts_down_to_the_next_server_refresh()
    {
        _respond = _ => Stories("[]", snapshotAge: 42);
        var page = _context.Render<Home>();
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 18 s", page.Markup));

        _time.Advance(TimeSpan.FromSeconds(5));

        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 13 s", page.Markup));
        Assert.Single(_requests);
    }

    [Fact]
    public void Reloads_at_zero_when_auto_reload_is_checked_and_starts_again_from_the_full_interval()
    {
        _respond = _ => Stories("[]", snapshotAge: 42);
        var page = _context.Render<Home>();
        CheckAutoReload(page);
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 18 s", page.Markup));

        _respond = _ => Stories(OneStory("Fresh"), snapshotAge: 0);
        _time.Advance(TimeSpan.FromSeconds(18));

        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 60 s", page.Markup));
        Assert.Equal(2, _requests.Count);
        Assert.Contains("Fresh", page.Markup);
    }

    [Fact]
    public void Keeps_checking_with_auto_reload_until_the_server_publishes_a_new_snapshot()
    {
        _respond = _ => Stories("[]", snapshotAge: 58);
        var page = _context.Render<Home>();
        CheckAutoReload(page);
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 2 s", page.Markup));

        // The server's refresh takes a few seconds, so the first reload still gets the old snapshot.
        _respond = _ => Stories("[]", snapshotAge: 60);
        _time.Advance(TimeSpan.FromSeconds(2));
        page.WaitForAssertion(() => Assert.Equal(2, _requests.Count));
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 60 s", page.Markup));

        _respond = _ => Stories("[]", snapshotAge: 1);
        _time.Advance(TimeSpan.FromSeconds(5));

        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 59 s", page.Markup));
        Assert.Equal(3, _requests.Count);
    }

    [Fact]
    public void Keeps_the_stories_on_screen_when_an_automatic_reload_fails()
    {
        _respond = _ => Stories(OneStory("Kept"), snapshotAge: 59);
        var page = _context.Render<Home>();
        CheckAutoReload(page);
        page.WaitForAssertion(() => Assert.Contains("Kept", page.Markup));

        _respond = _ => throw new HttpRequestException("Connection refused");
        _time.Advance(TimeSpan.FromSeconds(1));

        page.WaitForAssertion(() => Assert.Contains("Could not reach the API", page.Find(".mud-alert").TextContent));
        Assert.Single(page.FindAll("tbody tr"));
    }

    [Fact]
    public void Without_auto_reload_the_countdown_wraps_back_to_the_full_interval_without_calling_the_api()
    {
        _respond = _ => Stories("[]", snapshotAge: 58);
        var page = _context.Render<Home>();
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 2 s", page.Markup));
        Assert.False(page.Find("input[type=checkbox]").HasAttribute("checked"));

        _time.Advance(TimeSpan.FromSeconds(2));
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 60 s", page.Markup));

        _time.Advance(TimeSpan.FromSeconds(10));
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 50 s", page.Markup));

        _time.Advance(TimeSpan.FromSeconds(60));
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 50 s", page.Markup));
        Assert.Single(_requests);
    }

    [Fact]
    public void Checking_auto_reload_after_zero_reloads_on_the_next_tick()
    {
        _respond = _ => Stories("[]", snapshotAge: 65);
        var page = _context.Render<Home>();
        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 55 s", page.Markup));

        _respond = _ => Stories("[]", snapshotAge: 0);
        CheckAutoReload(page);
        _time.Advance(TimeSpan.FromSeconds(1));

        page.WaitForAssertion(() => Assert.Contains("Cache refresh in 60 s", page.Markup));
        Assert.Equal(2, _requests.Count);
    }

    private static void CheckAutoReload(IRenderedComponent<Home> page) =>
        page.Find("input[type=checkbox]").Change(true);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private static HttpResponseMessage Stories(string json, int? snapshotAge = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (snapshotAge is not null)
        {
            response.Headers.Add("X-Snapshot-Age", snapshotAge.Value.ToString());
        }
        return response;
    }

    private static string OneStory(string title) =>
        $$"""[{"title":"{{title}}","uri":"https://example.com/1","postedBy":"x","time":"2019-10-12T13:43:01+00:00","score":1,"commentCount":0}]""";

    private static HttpResponseMessage Problem(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/problem+json") };

    private sealed class StubHandler(HomePageTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            test._requests.Add(request.RequestUri!);
            return Task.FromResult(test._respond(request));
        }
    }
}
