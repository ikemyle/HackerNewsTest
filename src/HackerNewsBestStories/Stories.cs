using System.Collections.Immutable;

namespace HackerNewsBestStories;

public sealed record Story(string Title, string Uri, string? PostedBy, DateTimeOffset Time, int Score, int CommentCount);

public sealed record StorySnapshot(ImmutableArray<Story> Stories, DateTimeOffset RefreshedAt);

// Shape of https://hacker-news.firebaseio.com/v0/item/{id}.json. Missing fields deserialize to defaults.
public sealed record HackerNewsItem(
    int Id,
    string? Type,
    string? By,
    long Time,
    string? Title,
    string? Url,
    int Score,
    int Descendants,
    bool Deleted,
    bool Dead);
