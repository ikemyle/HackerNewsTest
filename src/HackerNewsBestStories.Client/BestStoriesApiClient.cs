using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace HackerNewsBestStories.Client;

public sealed class ApiSettings
{
    public Uri? BaseAddress { get; set; }

    public int DefaultStoryCount { get; set; }

    // Mirrors Api:MaxStories on the server; the server still validates n itself.
    public int MaxStories { get; set; }

    // Mirrors HackerNews:RefreshInterval on the server; the countdown runs from this to zero.
    public int RefreshIntervalSeconds { get; set; }

    // How often to check again when the countdown has ended but the server has not published a new snapshot yet.
    public int RefreshPollSeconds { get; set; }

    // Initial state of the "Auto reload" checkbox.
    public bool AutoReloadByDefault { get; set; }
}

public sealed record Story(string Title, string Uri, string? PostedBy, DateTimeOffset Time, int Score, int CommentCount);

public sealed record BestStoriesResult(IReadOnlyList<Story> Stories, int? SnapshotAgeSeconds, string? Error);

public sealed class BestStoriesApiClient(HttpClient httpClient)
{
    public async Task<BestStoriesResult> GetBestStoriesAsync(int count, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(
                $"api/stories/best?n={count.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        }
        catch (HttpRequestException)
        {
            // In the browser this covers the API being down and CORS rejections; neither exposes more detail.
            return Failure($"Could not reach the API at {httpClient.BaseAddress}. Is it running?");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var stories = await response.Content.ReadFromJsonAsync<Story[]>(cancellationToken) ?? [];
                var age = response.Headers.TryGetValues("X-Snapshot-Age", out var values)
                    && int.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var seconds)
                        ? seconds
                        : (int?)null;
                return new BestStoriesResult(stories, age, null);
            }

            var problem = await ReadProblemAsync(response, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest =>
                    Failure(problem?.Errors?.GetValueOrDefault("n")?.FirstOrDefault() ?? problem?.Title ?? "The request was rejected."),
                HttpStatusCode.ServiceUnavailable =>
                    Failure($"{problem?.Title ?? "The API is not ready yet."} Try again in {response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 10:0} seconds."),
                _ => Failure($"The API returned {(int)response.StatusCode} {response.ReasonPhrase}."),
            };
        }
    }

    private static async Task<ProblemDetails?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/problem+json")
        {
            return null;
        }
        return await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
    }

    private static BestStoriesResult Failure(string error) => new([], null, error);

    private sealed record ProblemDetails(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}
