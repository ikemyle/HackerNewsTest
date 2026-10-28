using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;

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

    private static WebApplicationFactory<Program> CreateFactory(string? provider) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (provider is not null)
            {
                builder.UseSetting("Cache:Provider", provider);
            }
            builder.UseSetting("ConnectionStrings:Redis", "localhost:1");
            builder.ConfigureTestServices(services =>
                services.Remove(services.Single(d => d.ImplementationType == typeof(BestStoriesRefreshService))));
        });
}
