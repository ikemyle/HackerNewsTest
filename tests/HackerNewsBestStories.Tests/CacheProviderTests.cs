using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HackerNewsBestStories.Tests;

public class CacheProviderTests
{
    [Theory]
    [InlineData(null, typeof(MemoryDistributedCache))]
    [InlineData("Memory", typeof(MemoryDistributedCache))]
    [InlineData("redis", typeof(RedisCache))]
    public void Registers_the_configured_provider(string? provider, Type expected)
    {
        using var factory = CreateFactory(provider);

        // Resolving the Redis cache does not connect; that happens on first use.
        Assert.IsAssignableFrom(expected, factory.Services.GetRequiredService<IDistributedCache>());
    }

    [Fact]
    public void Rejects_an_unknown_provider_at_startup()
    {
        using var factory = CreateFactory("Memcached");

        var error = Assert.Throws<InvalidOperationException>(() => factory.Services);
        Assert.Contains("Memcached", error.Message);
    }

    [Theory]
    [InlineData("HackerNews:BestStoriesPath", "")]
    [InlineData("HackerNews:ItemPathTemplate", "item.json")]
    [InlineData("HackerNews:MaxConcurrentRequests", "0")]
    [InlineData("HackerNews:RefreshInterval", "00:00:00")]
    [InlineData("Api:MaxStories", "0")]
    [InlineData("Cache:SnapshotKey", "")]
    public void Rejects_missing_or_invalid_settings_at_startup(string key, string value)
    {
        using var factory = CreateFactory(null, (key, value));

        var error = Assert.Throws<OptionsValidationException>(() => factory.Services);
        Assert.Contains(key.Split(':')[1], error.Message);
    }

    private static WebApplicationFactory<Program> CreateFactory(string? provider, (string Key, string Value)? setting = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (provider is not null)
            {
                builder.UseSetting("Cache:Provider", provider);
            }
            if (setting is var (key, value))
            {
                builder.UseSetting(key, value);
            }
            builder.UseSetting("ConnectionStrings:Redis", "localhost:1");
            builder.ConfigureTestServices(services =>
                services.Remove(services.Single(d => d.ImplementationType == typeof(BestStoriesRefreshService))));
        });
}
