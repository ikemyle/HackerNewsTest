using Microsoft.Extensions.Configuration;

namespace HackerNewsBestStories.Tests;

// The app's own appsettings.json (copied to the test output by the project reference),
// so tests run with the same values as the app instead of a second copy of them.
public static class TestSettings
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    // Points the upstream at FakeHackerNews; everything else is as configured.
    public static HackerNewsOptions HackerNews()
    {
        var options = Configuration.GetSection(HackerNewsOptions.SectionName).Get<HackerNewsOptions>()!;
        options.BaseAddress = FakeHackerNews.BaseAddress;
        return options;
    }

    public static SharedCacheOptions SharedCache() =>
        Configuration.GetSection(SharedCacheOptions.SectionName).Get<SharedCacheOptions>()!;
}
