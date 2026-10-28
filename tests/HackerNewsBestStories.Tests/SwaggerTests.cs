using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HackerNewsBestStories.Tests;

public class SwaggerTests
{
    [Fact]
    public async Task OpenApi_document_describes_the_best_stories_endpoint()
    {
        using var factory = CreateFactory(enableSwagger: true);
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/stories/best").GetProperty("get");
        Assert.Equal("GetBestStories", operation.GetProperty("operationId").GetString());

        var n = Assert.Single(operation.GetProperty("parameters").EnumerateArray());
        Assert.Equal("n", n.GetProperty("name").GetString());
        Assert.True(n.GetProperty("required").GetBoolean());
        Assert.Equal("integer", n.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(1, n.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(500, n.GetProperty("schema").GetProperty("maximum").GetInt32());

        var responses = operation.GetProperty("responses");
        Assert.Equal(["200", "400", "503"], responses.EnumerateObject().Select(r => r.Name).Order());
        Assert.True(responses.GetProperty("200").GetProperty("headers").TryGetProperty("X-Snapshot-Age", out _));
        Assert.True(responses.GetProperty("503").GetProperty("headers").TryGetProperty("Retry-After", out _));

        var story = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("Story");
        Assert.Equal(
            ["commentCount", "postedBy", "score", "time", "title", "uri"],
            story.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("integer", story.GetProperty("properties").GetProperty("score").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Swagger_ui_is_served()
    {
        using var factory = CreateFactory(enableSwagger: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    public async Task Nothing_is_exposed_when_swagger_is_disabled(string path)
    {
        using var factory = CreateFactory(enableSwagger: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool enableSwagger) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Api:EnableSwagger", enableSwagger.ToString());
            builder.ConfigureTestServices(services =>
                services.Remove(services.Single(d => d.ImplementationType == typeof(BestStoriesRefreshService))));
        });
}
