# Hacker News best stories API

A small ASP.NET Core 10 API that returns the `n` highest-scoring stories from the current Hacker News
[best-story list](https://github.com/HackerNews/API).

```
GET /api/stories/best?n=10
```

```json
[
  {
    "title": "A uBlock Origin update was rejected from the Chrome Web Store",
    "uri": "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
    "postedBy": "ismaildonmez",
    "time": "2019-10-12T13:43:01+00:00",
    "score": 1716,
    "commentCount": 572
  }
]
```

## Prerequisites

- .NET 10 SDK (`global.json` accepts any 10.0.1xx or later feature band)
- Docker, only if you want to run the container or the Redis setup

## Running

```bash
dotnet run --project src/HackerNewsBestStories
```

The API listens on `http://localhost:5131`. The first refresh starts immediately and usually takes a few seconds;
until it finishes the endpoint returns 503.

```bash
curl -i "http://localhost:5131/api/stories/best?n=10"
```

```bash
curl -i "http://localhost:5131/api/stories/best?n=0"
```

With Docker:

```bash
docker build -t hacker-news-best-stories .
```

```bash
docker run --rm -p 8080:8080 hacker-news-best-stories
```

```bash
curl -i "http://localhost:8080/api/stories/best?n=5"
```

## Tests

```bash
dotnet test
```

The tests use an in-memory fake of the Hacker News API (`FakeHackerNews`) and `FakeTimeProvider`, so they never
touch the network and do not sleep. The background service test waits for the service to create its delay timer
before advancing the fake clock, which keeps it deterministic.

## Responses

| Status | When |
| --- | --- |
| 200 | JSON array of up to `n` stories, highest score first. Header `X-Snapshot-Age` gives the age of the data in whole seconds. |
| 400 | `n` is missing, not a plain integer, or outside 1 to `Api:MaxStories` (500). Problem Details body with an `errors.n` message. |
| 503 | No refresh has succeeded yet since the process started. Problem Details body and `Retry-After` from `Api:ColdStartRetryAfter` (10 s). |

Fewer than `n` stories are returned when fewer eligible stories exist.

## How it works

There are three pieces:

- `BestStoriesCache` fetches the best-story id list, then every item (at most 8 requests at a time), filters,
  sorts and publishes an immutable `StorySnapshot`. Publishing is one reference assignment, so readers see either
  the previous snapshot or the complete new one.
- `BestStoriesRefreshService` is a `BackgroundService` that runs at startup and then every 60 seconds. Each tick
  it first checks the shared cache (below) and only calls Hacker News if no snapshot there is newer than the
  refresh interval.
- `Program.cs` holds the configuration, the cache provider choice and the single minimal API endpoint, which only
  reads the current snapshot.

API requests never call Hacker News. That is what makes concurrent load cheap: each request is an array slice
and JSON serialization. It also means a caller cancelling or timing out cannot affect a refresh, because a
refresh never runs on a request's behalf.

Ranking rules:

- All ids in `beststories.json` are fetched before ranking. The list is not assumed to be ordered by score.
- Order is score descending, then story id ascending so ties are stable between refreshes.
- `null` items, `deleted` or `dead` items, items without a title, and anything whose `type` is not `story` are dropped.
- `by` → `postedBy`, `url` → `uri`, `descendants` → `commentCount` (0 when absent), `time` (Unix seconds) → UTC
  `DateTimeOffset`.
- Stories without a `url` (Ask HN and similar) link to `https://news.ycombinator.com/item?id={id}`.

## Configuration

All tunable values live in `appsettings.json`; the C# options classes in `Settings.cs` have no defaults of
their own. Any value can be overridden with an environment variable (`HackerNews__RefreshInterval=00:02:00`) or
a command-line argument (`--HackerNews:RefreshInterval=00:02:00`).

| Setting | Value in appsettings.json | Meaning |
| --- | --- | --- |
| `Api:MaxStories` | `500` | Largest accepted `n` |
| `Api:ColdStartRetryAfter` | `00:00:10` | `Retry-After` sent with the 503 before the first snapshot |
| `HackerNews:BaseAddress` | `https://hacker-news.firebaseio.com/v0/` | Upstream API root |
| `HackerNews:BestStoriesPath` | `beststories.json` | Best-story id list, relative to `BaseAddress` |
| `HackerNews:ItemPathTemplate` | `item/{id}.json` | Single item, relative to `BaseAddress` |
| `HackerNews:DiscussionUrlTemplate` | `https://news.ycombinator.com/item?id={id}` | `uri` for stories without an external url |
| `HackerNews:RefreshInterval` | `00:01:00` | Delay between successful refreshes |
| `HackerNews:FailureRetryDelay` | `00:00:10` | First delay after a failed refresh; doubles per consecutive failure, capped at `RefreshInterval` |
| `HackerNews:RequestTimeout` | `00:00:10` | Timeout for each upstream HTTP request |
| `HackerNews:RefreshTimeout` | `00:00:45` | Deadline for a whole refresh (id list plus all items) |
| `HackerNews:MaxConcurrentRequests` | `8` | Upstream requests in flight at once during a refresh |
| `Cache:Provider` | `Memory` | `Memory` or `Redis`, see below |
| `Cache:SnapshotKey` | `hacker-news:best-stories` | Key the snapshot is stored under in the shared cache |
| `ConnectionStrings:Redis` | `localhost:6379` | Used only when `Cache:Provider` is `Redis` |

The settings are validated with data annotations when the app starts. A missing or invalid value (an empty path,
a template without `{id}`, a zero interval) stops the app with a message naming the setting, rather than letting
it run with a fallback.

What stays in code is the API contract and internal names: the route, the `n` parameter, the `X-Snapshot-Age`
header, the JSON field names, the `story` item type, and the named HttpClient.

## Cache providers

Every successful refresh is also written as JSON to a shared cache under `Cache:SnapshotKey`.
There are two providers, picked with `Cache:Provider`:

| Provider | Setting | Use |
| --- | --- | --- |
| `Memory` (as shipped) | nothing else needed | A single instance. The snapshot stays in-process, as before. |
| `Redis` | `ConnectionStrings:Redis`, e.g. `localhost:6379` | Several instances that share one snapshot. |

```bash
dotnet run --project src/HackerNewsBestStories -- --Cache:Provider=Redis --ConnectionStrings:Redis=localhost:6379
```

An unknown provider name stops the app at startup.

Both are the standard `IDistributedCache` implementations (`AddDistributedMemoryCache` and
`AddStackExchangeRedisCache`), so I didn't add an interface of my own. Another backend that implements
`IDistributedCache` (SQL Server, Postgres and others) would be one more branch in `Program.cs`.

Requests still read the in-process snapshot, never the provider. The shared cache is read and written once per
refresh tick, so Redis latency doesn't affect request latency and a Redis outage doesn't take the API down. If
Redis can't be reached, the app logs a warning and keeps refreshing from Hacker News on its own.

## Freshness, upstream load and failures

A full refresh costs about 201 upstream requests (one list plus up to 200 items; the list can hold up to 500).
Refreshing every 60 seconds keeps that to a few requests per second on average regardless of how much traffic the
API receives. The cost is that data can be up to about a minute old in normal operation. Hacker News scores move
slowly enough that I think that is the right side of the trade; if fresher data mattered more, the interval is a
config change, and the upstream load grows linearly as it shrinks.

Stale-data policy:

- If a refresh fails for any reason (an item request fails, the list request fails, a timeout, the deadline), the
  previous snapshot stays in place. A partial ranking is never published, because a missing item could be the top
  story. Items that come back successfully as `null` or deleted are just excluded.
- There is no maximum age. During a long upstream outage the API keeps serving the last good data and
  `X-Snapshot-Age` keeps growing, so clients and monitoring can decide what is too old.
- Failed refreshes are retried after 10 s, 20 s, 40 s, then every 60 s. Each attempt is bounded by the per-request
  timeout, the overall refresh deadline and the concurrency limit. There are no per-request retries; the next
  attempt is the retry.
- Only one refresh runs at a time. The background loop is sequential, and the cache also holds a lock around
  refreshes so nothing else can start a second one concurrently.
- On shutdown the in-flight refresh is cancelled through the host's stopping token.

## Running more than one instance

With the `Memory` provider the snapshot, the refresh schedule and the 8-request limit are all per process. Two
replicas mean two independent caches, twice the upstream traffic, and replicas that may briefly disagree about
the ranking.

With the `Redis` provider instances share their refreshes. On each tick an instance first loads the snapshot from
Redis. If that snapshot is younger than `RefreshInterval` it is used as-is and Hacker News isn't called. A new
instance whose first tick finds a snapshot in Redis serves it immediately instead of returning 503.

```bash
docker compose up --build
```

```bash
curl -i "http://localhost:8081/api/stories/best?n=3"
```

```bash
curl -i "http://localhost:8082/api/stories/best?n=3"
```

This is cooperative, not coordinated:

- Instances tick on their own schedules, so in steady state there is roughly one upstream refresh per interval
  across all instances. Occasionally two instances refresh at the same moment and the last write wins. Both
  snapshots are complete, so that only costs extra upstream requests.
- Because an instance may adopt a snapshot that is already almost an interval old and then wait a full interval,
  data can be up to about two intervals old (about two minutes with the defaults). `X-Snapshot-Age` shows the
  real age.
- The age check compares timestamps from different machines, so it relies on their clocks being roughly in sync.

If duplicate refreshes became a problem, the next step would be a Redis lock (`SET NX` with an expiry) around
the upstream fetch, or a single worker that refreshes while the API instances only read.

## Assumptions

- `n` must be written as a plain integer (`10`, not `+10`, `1e1` or `10.0`).
- Stories with no title are treated as invalid. The API never returns them, so `title` is always present.
- `postedBy` is passed through as-is and could be `null` if Hacker News ever returned a story without `by`.
- An empty id list from Hacker News is published as an empty ranking. A `null` response is treated as a failure.

## Known limitations and future improvements

- The concurrency-limit test proves that 8 requests run together and that the gate does not open below 8. It
  catches a higher limit only when a ninth request arrives before the gate opens, which depends on thread
  scheduling. Making that part deterministic would need the test to wait, which I chose not to do.
- A health check (`/health`) that reports unhealthy when the snapshot is older than some threshold.
- Fetch only changed items using the `updates.json` endpoint instead of all items on every refresh.
- `Cache-Control`/`ETag` headers so clients and proxies can avoid repeat downloads within a refresh window.
- Metrics for refresh duration, failures and snapshot age.
- A Redis lock so only one instance refreshes at a time, and Redis tests against a real server (for example with Testcontainers). The current tests use the in-memory provider and a fake that fails like an unreachable Redis.
