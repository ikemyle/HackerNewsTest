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

The API listens on `http://localhost:5131`, and `dotnet run` (or F5 in Visual Studio) opens the browser at
`/swagger`. The client opens at `http://localhost:5200` the same way. The first refresh starts immediately and usually takes a few seconds;
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

## Client page

`src/HackerNewsBestStories.Client` is a separate Blazor WebAssembly app built with MudBlazor. It has a number
input for how many stories to show, loads the default count on start, and lists the stories in a table with rank,
linked title, author, posting time (UTC), score and comment count. The table shows 10 rows per page, with a
pager to move between pages or switch to 25, 50 or 100 rows; ranks stay continuous across pages. API errors are shown as they come back: the
validation message for a bad `n`, the "not ready yet" message with the retry delay on a 503, and a plain message
if the API can't be reached.

Run the API and the client in two terminals:

```bash
dotnet run --project src/HackerNewsBestStories
```

```bash
dotnet run --project src/HackerNewsBestStories.Client
```

Then open `http://localhost:5200`.

Above the table a bar counts down from 60 to 0: the time until the server's next cache refresh, worked out from
`X-Snapshot-Age`. At zero it wraps straight back to 60 and keeps cycling with the server, whether or not anything
is reloaded. The "Auto reload" checkbox next to the button decides what else happens at zero, and is off by
default:

- Off: nothing is fetched. The table keeps the stories you loaded until you press Show stories.
- On: the page reloads the stories as the bar wraps. The server's refresh takes a few seconds, so the first reload
  often still gets the old snapshot. In that case the page quietly checks again every 5 seconds until a newer
  snapshot arrives, and the bar keeps counting meanwhile. Ticking the box after the bar has wrapped reloads on the
  next second.

The countdown is recomputed from the snapshot time on every tick, so a slow or backgrounded tab doesn't make it
drift. If a reload fails, the previous stories stay on screen with the error above them.

The client's settings are in `src/HackerNewsBestStories.Client/wwwroot/appsettings.json`:

| Setting | Value | Meaning |
| --- | --- | --- |
| `Api:BaseAddress` | `http://localhost:5131/` | The API's URL |
| `Api:DefaultStoryCount` | `10` | Stories loaded when the page opens |
| `Api:MaxStories` | `500` | Upper bound of the input; the API still validates `n` against its own `Api:MaxStories` |
| `Api:RefreshIntervalSeconds` | `60` | Length of the countdown; should match the API's `HackerNews:RefreshInterval` |
| `Api:RefreshPollSeconds` | `5` | With auto reload, how often to check again after zero until the server has a newer snapshot |
| `Api:AutoReloadByDefault` | `false` | Initial state of the "Auto reload" checkbox |
| `Api:DefaultRowsPerPage` | `10` | Rows per page in the table when the page opens |
| `Api:RowsPerPageOptions` | `[10, 25, 50, 100]` | Page sizes offered by the pager |

`RefreshIntervalSeconds` is a copy of the server setting. If the two differ the page still shows correct data,
but the countdown reaches zero at the wrong time and spends the difference polling. Having the API send its
interval in a response header would remove the duplication.

Because the client runs on a different origin, the API allows it through CORS. Origins are listed in the API's
`Cors:AllowedOrigins` (`http://localhost:5200` as shipped), and the `X-Snapshot-Age` and `Retry-After` headers
are exposed so the page can read them. Deploying the client somewhere else means adding its origin there and
pointing the client's `Api:BaseAddress` at the deployed API.

The client has its own copy of the `Story` record rather than a shared contracts project. It is six fields, and
the API's JSON contract is what both sides actually depend on.

## Swagger

With `Api:EnableSwagger` on (the shipped setting), Swagger UI is at `http://localhost:5131/swagger` and the
OpenAPI document at `http://localhost:5131/openapi/v1.json`. The document comes from ASP.NET Core's built-in
OpenAPI generator. Swashbuckle is used only for the UI page.

`n` is bound as a string so that malformed values get the API's own 400 message. The OpenAPI document still
describes it as a required integer between 1 and `Api:MaxStories`, and lists the `X-Snapshot-Age` and
`Retry-After` headers.

Set `Api__EnableSwagger=false` to turn both off, for example in a production deployment that shouldn't advertise
its schema.

## Tests

There are two test projects. `HackerNewsBestStories.Tests` covers the API. `HackerNewsBestStories.Client.Tests`
uses bUnit to render the client page against a stubbed `HttpClient`.

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
| `Api:EnableSwagger` | `true` | Serve the OpenAPI document and Swagger UI |
| `Cors:AllowedOrigins` | `["http://localhost:5200"]` | Browser origins allowed to call the API (the Blazor client) |
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

## Limitations

- **Incoming traffic is not limited.** The limit of 8 concurrent requests applies only to the background refresh
  calling Hacker News. Requests to `/api/stories/best` have no cap: each one reads the in-memory snapshot without
  locks or upstream calls, so a 9th or a 9,000th concurrent caller is served the same way. Kestrel's connection
  limit is left at its default (unlimited), so the real ceiling is CPU and network, mostly the cost of writing
  JSON for large `n` (500 stories is roughly 100 KB).
- **No rate limiting.** A single client can send as many requests as it likes. Nothing returns 429.
- **No HTTP caching headers.** Responses carry no `Cache-Control` or `ETag`, so browsers and proxies ask again
  even when the snapshot hasn't changed.
- **Not load-tested.** A test sends 200 requests in parallel and checks that they all get the same response
  without extra calls to Hacker News. There are no throughput or latency measurements, so I can't give a
  requests-per-second figure.
- **One process per snapshot unless Redis is used.** With the `Memory` provider each instance refreshes on its own.
  More capacity means more instances behind a load balancer, with `Cache:Provider` set to `Redis` to share
  the snapshot.
- **No authentication.** The API and the client page are open to anyone who can reach them.
- **The concurrency-limit test is only partly deterministic.** It proves that 8 requests run together and that
  the gate doesn't open below 8. It catches a higher limit only when a ninth request arrives before the gate
  opens, which depends on thread scheduling. Making that part deterministic would need the test to wait, which
  I chose not to do.
- **Redis is tested with fakes.** The tests use the in-memory provider and a fake that fails like an unreachable
  Redis. A real Redis server was checked manually with `docker compose`, not in the test suite.

## Improvements with more time

- **Authentication and authorization.** Require a JWT or API key on the API (`AddAuthentication` plus an
  authorization policy on the endpoint), and sign users in on the client, for example with OpenID Connect. That
  also gives rate limiting and stats a real identity to work with instead of an IP address.
- **Peak rank history.** Record the highest rank each story has reached in the ranking, and the date and time
  (UTC) it first got there, and show both in the client next to the current rank. Each refresh already produces
  the full ranking, so this is a comparison against the stored best for each story id. The history needs to
  outlive a restart and be shared between instances, so it belongs in Redis or a small database rather than the
  in-memory snapshot. It could be returned as two extra fields (`peakRank`, `peakRankAt`), which adds to the
  JSON contract without breaking it, or from a separate endpoint if the original response shape must stay
  exactly as specified.
- **Comment tree.** Each Hacker News item has a `kids` array with the ids of its direct replies, and every reply
  has its own `kids`, so a story's discussion forms a tree of any depth. The API could expose it as a
  hierarchical structure: each node carries the item's id, author, time, text and its own link
  (`https://news.ycombinator.com/item?id={id}`, from `HackerNews:DiscussionUrlTemplate`), plus its children, and
  so on for the grandkids. The client would show it as an expandable tree under each story (MudBlazor's
  `MudTreeView`).
  This shouldn't be part of the 60-second refresh: popular stories have over a thousand comments
  (`descendants`), so fetching every tree each minute would multiply the upstream load. Instead it would be a
  separate endpoint such as `GET /api/stories/{id}/comments?depth=2` that fetches one level at a time when a node is
  expanded, under the same concurrency limit and request timeouts, with a short cache per item. Deleted and dead
  comments would be dropped or shown as placeholders, so the tree keeps its shape.
- **Rate limiting.** ASP.NET Core's built-in rate limiter (`AddRateLimiter`) with a fixed-window or token-bucket
  policy per user or IP, with limits in `appsettings.json`, returning 429 with `Retry-After`.
- **User stats.** Record who calls the API, how often and with which `n`, and show it on a small admin page.
  Even without accounts, per-client request counts would show whether rate limiting is needed.
- **Logging.** Structured request logging (`UseHttpLogging`, or Serilog) sent to a central store such as Seq,
  Elasticsearch or Application Insights, with correlation ids so a client error can be traced to the API
  request. Today the app only writes refresh results and warnings to the console.
- **Metrics and health checks.** Refresh duration, failure count and snapshot age as metrics (OpenTelemetry),
  and a `/health` endpoint that reports unhealthy when the snapshot is older than some threshold.
- **HTTP caching.** `Cache-Control` and `ETag` headers so clients and proxies reuse a response within a refresh
  window.
- **Load testing.** A k6 or NBomber run to get real throughput and latency numbers and find the actual ceiling.
- **Cheaper refreshes.** Use Hacker News's `updates.json` to fetch only changed items instead of every item each
  minute.
- **Coordinated refresh across instances.** A Redis lock so only one instance calls Hacker News per interval,
  and Redis tests against a real server (for example with Testcontainers).
- **Send the refresh interval to the client.** Have the API return its interval in a header, so the client's
  countdown can't drift from the server's setting.
- **Database persistence.** Today nothing survives a restart except what Redis holds: the snapshot lives in
  memory, and the original brief ruled out a database. A small relational store (PostgreSQL or SQL Server through
  EF Core) would let the app keep each refresh's ranking with its timestamp. Several of the items above need that
  history: peak rank history, user stats and request logs. It would also let a restarted instance serve the last
  stored snapshot immediately instead of returning 503. The request path would stay as it is: requests read
  the in-memory snapshot, and only the background refresh writes to the database, so database latency or an
  outage never slows the API. Old rankings would be pruned or aggregated on a schedule so the table doesn't grow
  without bound.
