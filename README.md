# Web Crawler

Type in a website address and the crawler visits the site in the background, following its links up to a
chosen depth. For every page it saves the links it found and works out the page's **Domain Link Ratio
(DLR)**: how many of its links stay on the same site.

- **Crawl API** (ASP.NET Core, .NET 8): takes new crawl jobs and answers questions about them.
- **Crawl Worker** (a separate program, 2 copies running): does the actual crawling.
- **RabbitMQ** passes jobs from the API to the workers. **SQL Server** stores everything.
- **React** (Vite + TypeScript) is the web page you use.

Solution `WebCrawler.sln`: `WebCrawler.Api`, `WebCrawler.Worker`, `WebCrawler.Core` (shared logic),
`WebCrawler.Tests`, plus `web/` (React).

---

## 1. How to run it locally

### Option A - everything, one command (needs only Docker)

```bash
docker compose up --build
```

Open **http://localhost:5000**. This starts the API with the web page, 2 workers, SQL Server and RabbitMQ.
The first start takes about a minute while SQL Server warms up. Stop it with `docker compose down`
(add `-v` to also wipe the database).

| Service    | Address                   | Notes                                     |
|------------|---------------------------|-------------------------------------------|
| api        | http://localhost:5000     | the web page + the API (`/swagger` lists every endpoint) |
| worker     | -                         | the crawler, 2 copies                     |
| sqlserver  | localhost:1433            | user `sa`, password in `.env`             |
| rabbitmq   | localhost:5672, admin page :15672 | user/password in `.env`           |

The passwords have safe local defaults; copy `.env.sample` to `.env` to change them. Port 5000 must be free.

### Option B - without Docker (.NET 8 SDK + Node.js 20+)

```bash
cd web && npm ci && npm run build && cd ..
dotnet run --project src/WebCrawler.Api
```

Open http://localhost:5000, or on Windows just double-click **`RunCrawler.bat`** (it builds the web page
the first time). This uses the same code with a SQLite file instead of SQL Server, and a built-in queue
instead of RabbitMQ, so there is nothing else to install or start.

### Tests

```bash
dotnet test
```

107 tests, about 3 seconds, no internet and no Docker needed ([section 9](#9-tests)).

---

## 2. Key architectural choices

Three goals drove every choice: **fast** (to run, to review and to change), **flexible** (swap a piece by
changing one setting) and **easy to update** (any file makes sense in under a minute).

```
Web page (React)                Start Crawl / Job Details / History
   │  asks the API; gets live updates while a crawl runs
   ▼
Crawl API ──1. saves the job (Pending)──────────►  SQL Server: Jobs, Pages, Links, Edges
   │ 2. puts { jobId } on the queue                      ▲
   ▼                                                     │  the crawl's "to do" list is the Pages table
RabbitMQ ──► Worker x2 ──► JobRunner ──► PageWork: DownloadPage -> SortPage -> SavePage -> AddChildren
   │ a job that keeps failing
   ▼
error queue ("dead letter") ──► the job is marked Failed with the reason
```

### Fast

- **One command to run it, nothing to set up to try it.** Docker for the full system, `RunCrawler.bat`
  for a quick local run.
- **The API never makes you wait for the crawl.** It saves the job, hands it to a worker and replies at once
  with the job id. The worker does the slow part.
- **Two downloads at a time, but still polite.** While one slow page loads, the next one can start, but
  requests to a site always start at least 250 ms apart.
- **Light on the database.** Messages carry only an id, history is read one page at a time, and a page's
  links load only when you click "show links".
- **The web page is served by the API.** No second web server and one address for everything.

### Flexible

- **Each big piece is one switch in `Config/settings.csv`:** database (`DbMode`: Sqlite or SqlServer), queue
  (`QueueMode`: InMemory or RabbitMq), worker (`WorkerMode`: inside the API or Separate). Same code either way;
  Docker just flips the switches.
- **Nothing important is hardcoded. It lives in a CSV.** Depth, page limit, timeouts, retries, delays, user
  agent: all in `Config/settings.csv`, each with a sensible default in code so a missing value never crashes.
  Docker can override any of them with an environment variable (`WEBCRAWLER_<Key>`).
- **The crawler is a plug-in.** `JobRunner` handles a job's life (start, finish, fail, cancel) and doesn't care
  what the work is. `PageWork` is the crawl. A different kind of job would be one new class plus one line.
- **More workers = more jobs at once.** Raise the worker count in `docker-compose.yml`; they share safely.

### Easy to update

- **The main methods read like a table of contents.** `Main`, `RunJob` and `RunWork` are short lists of named
  steps (`LoadConfig`, `MarkRunning`, `DownloadPages`, `CrawlPage`, `SavePage`, `MarkFinished`...), so you can
  follow a whole run top to bottom and change one step without touching the rest.
- **The same verbs everywhere:** `Get` (read), `Download` (internet), `Sort` (pick the data out of a page),
  `SetUp` (build rows), `Check` (validate), `Save` (write). The name tells you where the code lives.
- **The rules that matter are small, separate methods** (`UrlRules.Normalize`, `PageParser.SortPage`,
  `GetLinkRatio`), so they're easy to test and easy to change.
- **One result per input, even failures.** A page that can't be read is saved as Failed or Skipped with the
  reason. One bad page never stops a crawl, and nothing silently disappears.
- **It talks while it works.** The logs use the same markers every run (`:TRYING:`, `BEGIN- URL:`, `FAIL`,
  `Saved ->`), and every line carries the job id, so you can follow one crawl through the logs.
- **Small and plain on purpose.** Three projects plus tests, few packages, no extra layers. Code is grouped by
  job: API (`Api/Endpoints`), crawl rules (`Core/Models`, `Core/Work`), database and queue (`Core/Data`, `Core/Queue`).

### RabbitMQ or Kafka?

**RabbitMQ** is a *to-do queue*: each message goes to one worker and is removed once that worker says "done",
with retries and an error queue built in. **Kafka** is a *shared log*: messages stay for days, many readers can
replay the same stream, and it's built for huge volumes. **We chose RabbitMQ because a crawl job is a task that
exactly one worker should pick up, finish, retry or give up on - which is what RabbitMQ does out of the box -
and it's lighter to run. We don't need Kafka's replay or volume.**

### Other choices, and what they cost

| Choice | Why | Cost |
|--------|-----|------|
| **One message per job; the crawl's to-do list lives in the database** | If a message arrives twice, a worker crashes, or two workers grab the same job, no page is downloaded twice and no row is saved twice. A crashed job carries on where it stopped. | One job is crawled by one worker at a time. |
| **A page, its new links and its edges are saved together, all or nothing** | If a worker crashes halfway, nothing half-done is kept; the page is simply crawled again. | Slightly longer database writes. |
| **An `Edges` table** (parent page -> child page) | The brief asks for it. Each page also remembers the first page that found it, which builds the tree. | Edges only point at pages that were crawled. |
| **Each url is stored with a short fingerprint (SHA-256)** | Lets the database check "already have this url?" quickly, however long the url is. | One extra column. |
| **React built into the API**, no extra state library | The brief asks for React; three screens don't need more. | Running without Docker needs Node once. |
| **Live updates while a crawl runs** (server-sent events), refreshing as a backup | Simple one-way updates, no extra hub. | The API checks the database every 1.5 s per open page. |
| **The database creates itself** (`EnsureCreated`), no migrations | Nothing for a reviewer to run. | A real deployment should use migrations. |
| **Blocks private / local addresses** | Stops someone using the crawler to reach internal machines. | Crawling an intranet needs `BlockPrivateAddresses=false`. |
| **A depth outside 0-5 is an error**, not silently changed | Clear errors beat guessing. | - |

---

## 3. Known limitations

- **One worker per job at a time.** Many jobs spread over all workers, but one huge job doesn't.
- **Progress is "pages finished / pages found so far"**, so the total grows while new pages are discovered.
- If a worker dies mid-page, that page is picked up again after 90 seconds (`2 x PageTimeoutSeconds`).
- Two workers on the same job can go a few pages over `MaxPagesPerJob`.
- **Sites that redirect from https to http** (e.g. quotes.toscrape.com's author pages) show those pages as
  Failed ("refused https -> http redirect"). That's on purpose: we don't downgrade to an unsafe connection.
- **If the start address redirects to another host** (`example.com` -> `www.example.com`), the crawl still
  follows the site, but the DLR counts only `example.com` as "same site", exactly as the brief defines it, so
  those pages show a low DLR.
- No robots.txt, no logins, and the rate limit is per IP address.
- After a database model change on SQL Server, run `docker compose down -v` (no migrations).
- .NET 8 (asked for by the brief) reaches end of support on 2026-11-10; .NET 10 is one line in `Directory.Build.props`.

---

## 4. What I built first (and why)

I started from my own job-pipeline skeleton (API + worker + queue + database + tests, already working in
Docker), so the first 15 minutes went into turning the brief into a checklist. Then, in order of what a
reviewer checks first:

1. **The brief's exact contract**: status `Pending`, create returns `{ jobId }`, `completedAt`, the "starting domain" rule.
2. **The `Edges` table**: the database design is 15% of the grade and the brief names edges.
3. **The test over local HTML files**: the one test the brief asks for by name.
4. **The React web page**: the biggest new piece.
5. Docker, CI, a check of everything from a fresh copy, then this README.

## 5. What I cut (and why)

- **One message per page.** More moving parts than needed; the database to-do list gives the same "nothing
  twice" guarantee at this size.
- **robots.txt.** Not asked for; a fixed delay between requests keeps the crawler polite.
- **Migrations and logins.** Nothing for a reviewer to set up, and no user data to protect yet.
- **Front-end tests and tests against real SQL Server / RabbitMQ containers.** The web page is type-checked,
  the logic lives in the API (107 tests), and the real system was checked end to end by hand (section 12).

## 6. What I would do next

1. **"Download CSV" button.** After a crawl, export its pages (depth, url, status, link counts, DLR) as a CSV
   on demand: one `GET /api/jobs/{id}/export.csv` endpoint (CsvHelper is already in the project) and a button
   on the Job Details page. Nothing is written to disk unless the user asks for it.
2. **User accounts and login.** The database is already there: add logins (ASP.NET Core Identity) and tie each
   job to a user, so everyone sees their own history and limits apply per user.
3. **A page cache and pull history.** Keep every download in its own table (url, time, status, a fingerprint of
   the content). A new crawl can reuse a recent copy instead of downloading again (faster, kinder to the site),
   and the history shows how a site changed between crawls (new pages, broken links, DLR over time).
4. **One message per page**, so a single big job is shared across all workers.
5. **robots.txt and one shared speed limit per site** across all jobs and workers.
6. **Database migrations, container-based tests in CI, and tracing** that follows a job id from the API
   through the queue to the worker.

---

## 7. How the messaging stays reliable

The brief asks for this to be documented: the message, how duplicates are handled, retries, and the dead-letter queue.

**The message.** One message per crawl job, on the queue `webcrawler-jobs`. It holds only the job id;
everything else is read from the database, so a message can never be out of date:

```json
{ "jobId": "56b6e884-11cb-4c27-9e66-43aabe59f74d" }
```

**Duplicates can't do damage (idempotency).** RabbitMQ may deliver the same message more than once, so:
- A worker *claims* a job by switching it from Pending to Running in one database step. If a copy of the
  message arrives later, there's nothing left to claim, so it does nothing.
- Pages are claimed the same way, one at a time. The database refuses a second copy of the same url in a job,
  and a second copy of the same edge, so duplicates are impossible rather than just unlikely.
- Saving a page, queuing the new pages it links to, and saving its edges happen **together or not at all**
  (one transaction). A crash in the middle leaves nothing half-saved; the page is crawled again later.
- Tests prove it: the same message twice (`RunJob_SameMessageTwiceDoesNothingTheSecondTime`), a worker crashing
  between saving a page and its edges (`RunJob_CrashBetweenSavingAPageAndItsEdgesLosesNothing`), and a job
  resumed after a crash.
- **A worker dies mid-crawl:** RabbitMQ only forgets a message once the job is finished, so it hands the job to
  the other worker, which carries on from the saved to-do list (checked by killing a worker, section 12).

**Retries: what's worth trying again.**
- **Downloading a page:** network errors, timeouts, server errors (5xx), 408 and 429 are temporary, so they get
  3 more tries with growing waits (respecting the site's `Retry-After`). Other errors (like 404) are final. If
  it still fails, the page is saved as Failed with the reason and the crawl carries on.
- **A whole job:** a bad page never throws an error, so an error here means the system itself is in trouble
  (for example the database is down). The job is retried after 1 s, 5 s and 15 s, picking up where it left off.

**The dead-letter queue.**
- If a job still fails after those retries, the message is moved to **`webcrawler-jobs_error`** instead of
  being lost or retried forever, and the job is marked **Failed** with the reason.
- You can look at those messages in the RabbitMQ admin page (http://localhost:15672) and send them back once the
  problem is fixed; running them again is safe because of the duplicate protection above.
- If RabbitMQ is down when you start a job, the API says so (503) and the job is saved as Failed with the reason.

**Being able to see what's happening.** Every log line of a job carries its job id, and in Docker the logs are
JSON so they can be searched. Both the API and the worker have health checks: `/health/live` (running) and
`/health/ready` (database and queue reachable).

### The database tables

| Table | What it holds | Indexes (why) |
|-------|---------------|---------------|
| `Jobs` | id, start url, max depth, status, created / started / completed times, failure reason, summary | by created time (history), by status |
| `Pages` | job id, url + fingerprint, final url after redirects, depth, first parent, status, HTTP code, title, link counts, DLR, error | **one row per url per job (unique)**; "next page to crawl"; redirect duplicates |
| `Links` | page id, url, internal or external, how many times it appeared | by page |
| `Edges` | job id, parent page, child page | **one row per parent/child pair (unique)**; by child |

## 8. Crawling rules and assumptions

- Only full `http`/`https` addresses are accepted; local and private addresses are refused.
- Only HTML pages are read; anything else, or a page over 2 MB, is Skipped.
- Links come from `<a href>` and `<area href>` and are turned into full addresses (relative links and
  `<base href>` are resolved). The `#fragment` is dropped, the host is lower-cased, and the query string is kept.
  `mailto:`, `tel:`, `javascript:` and other non-web links are ignored.
- **Every web link counts as an outgoing link**, including a link back to the same page (like `#top`) and very
  long urls (over 2,048 characters are counted but not stored). A link that appears twice counts twice.
- **Starting domain = the host of the address you typed** (the brief's definition). **DLR = links to that host /
  all outgoing links**, and **0 for a page with no links**. Shown with 3 decimals.
- Pages are crawled nearest-first, and each url only once per job. Links to other sites are counted, never followed.
- If the start address redirects to another host (`example.com` -> `www.example.com`), the crawler follows that
  host too, otherwise the crawl would stop at the first page. The DLR still uses the original host.
- Page results: **Done**, **Skipped** (not HTML, too big, redirected off the site, a duplicate, or the job was
  canceled) or **Failed** (error, timeout). One failed page never fails the job; only a failed start page does.
- Job statuses: **Pending -> Running -> Completed, Failed or Canceled**. Cancel is checked before every page;
  pages already saved are kept.
- Limits (all in `settings.csv`): depth 0-5 (default 2), 200 pages per job, 10 s per try, 45 s per page,
  5 redirects, 2 MB, 3 retries.

## 9. Tests

- **A full crawl over real HTML files** (`FixtureCrawlTests`, files in `tests/WebCrawler.Tests/Fixtures/site`):
  only the internet is replaced; it checks relative links, fragments, `<base href>`, mailto/tel, a 404, a
  non-HTML file, a page with no links, the DLR of every page, stored links and edges.
- **Crash safety** (`CrashRecoveryTests`): a worker "crashes" between saving a page and its edges; the job is
  run again and ends with every page and every edge, nothing lost or doubled.
- **URL clean-up and DLR** (`UrlRulesTests`, `PageParserTests`).
- **Crawl behaviour** (`JobRunnerTests`): depth and page limits, each url once, every edge saved, failed pages,
  redirects, cancel, the same message twice, resuming after a crash, a second worker taking over.
- **API** (`JobApiTests`): create, bad input, not found, history paging, live updates, health, rate limit.
- The React code is type-checked by `npm run build`. CI (`.github/workflows/ci.yml`) runs the build, the tests,
  the web build and the Docker build on every push.

## 10. API

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/api/jobs` with `{ "url": "https://...", "maxDepth": 2 }` (`maxDepth` optional) | starts a crawl, returns **202** `{ jobId }`; 400 for bad input |
| GET  | `/api/jobs/{id}` | status, times, failure reason, summary, page counts, progress |
| GET  | `/api/jobs/{id}/tree` | the pages as a tree, with depth, status, link counts and DLR |
| GET  | `/api/jobs/{id}/pages/{pageId}/links` | one page's links, each internal or external |
| GET  | `/api/jobs/{id}/edges` | every parent -> child pair |
| GET  | `/api/jobs?page=1&pageSize=20` | history, newest first |
| POST | `/api/jobs/{id}/cancel` | cancels a running job (409 if it already finished) |
| GET  | `/api/jobs/{id}/events` | live updates until the job finishes |
| GET  | `/health/live`, `/health/ready` | health checks (the worker has the same two) |

Full details at http://localhost:5000/swagger. Errors come back in the standard ProblemDetails format.

## 11. Requirements coverage

| Brief requirement | Status | Where |
|-------------------|--------|-------|
| Crawl API: create jobs, serve status, results, history | Done | `src/WebCrawler.Api` |
| Crawl Worker as a separate project/container | Done | `src/WebCrawler.Worker`, 2 copies in Docker |
| RabbitMQ with Docker Compose | Done | `docker-compose.yml` |
| SQL: jobs, pages, edges (parent -> child) | Done | `Jobs`, `Pages`, `Links`, `Edges` |
| Job: id, url, Pending/Running/Completed/Failed/Canceled, times, failure reason | Done | `Models/Job.cs` |
| Create `{ url, maxDepth? }` -> `{ jobId }`; status; tree; paged history | Done | section 10 |
| Cancel (optional) | Done | `POST /api/jobs/{id}/cancel` |
| HTML only, depth 2 by default, url + links + DLR per page | Done | `PageFetcher`, `PageParser` |
| Relative links, fragments, no page crawled twice | Done | `UrlRules.Normalize`, unique url per job |
| Page limit per job, timeouts and retries | Done | `settings.csv` |
| DLR rules (starting domain, non-web links ignored, 0 links -> 0) | Done | `PageParser` |
| Duplicate-safe worker, retries, dead-letter queue, documented | Done | section 7 |
| React: Start Crawl, Job Details (progress, tree), History; loading and error states | Done | `web/src/pages/` |
| Stretch: live progress | Done | server-sent events |
| Unit tests (url clean-up, DLR) + a test over local HTML files | Done | section 9 |
| Logs with the job id; health checks for API and worker | Done | section 7 |
| docker-compose: API, Worker, DB, RabbitMQ | Done | `docker-compose.yml` |
| Optional CI | Done | `.github/workflows/ci.yml` |

## 12. Acceptance checks

Run on 2026-11-09 from a **fresh copy in an empty folder**, following this README exactly.
Machine: Windows 10, .NET SDK 8, Node 26, Docker Engine 28 in WSL Ubuntu.

| # | Check | How | Result |
|---|-------|-----|--------|
| 1 | Builds clean | `dotnet build` | PASS - 0 warnings, 0 errors |
| 2 | Tests | `dotnet test` | PASS - 107 passed (~3 s) |
| 3 | Web page builds | `cd web && npm ci && npm run build` | PASS |
| 4 | Full system in Docker | `docker compose up --build` | PASS - api, 2 workers, SQL Server, RabbitMQ all healthy |
| 5 | Bad input is refused | `POST /api/jobs {"url":"ftp://x","maxDepth":9}` | PASS - 400 with a message for each field |
| 6 | Create returns `{ jobId }` | `POST /api/jobs {"url":"https://books.toscrape.com/","maxDepth":1}` | PASS - `{"jobId":"...","status":"Pending"}` |
| 7 | A crawl completes in Docker | check `GET /api/jobs/{id}` until done | PASS - Completed, 74 pages, tree and edges returned |
| 8 | Logs carry the job id | `docker compose logs worker` | PASS - JSON lines with `"JobId"` |
| 9 | Web page | Start (errors shown), Job Details (live, tree, links), History, page reload | PASS |
| 10 | Unknown API address | `GET /api/nope` | PASS - 404 error, not the web page |
| 11 | Runs without Docker | `dotnet run --project src/WebCrawler.Api` | PASS - SQLite + built-in queue, page loads |
| 12 | Unreachable site | `POST /api/jobs {"url":"https://this-site-does-not-exist.invalid"}` | PASS - Failed, "DNS lookup failed" |
| 13 | A worker dies mid-crawl | depth-2 crawl of books.toscrape.com, kill the busy worker after 10 s | PASS - the other worker finished it: 200/200 pages, every url and all 3127 edges unique |
| 14 | Submission zip | `MakeZip.bat`, unzip into an empty folder, build + test | PASS - only the source: no `bin/`, `obj/`, `node_modules/`, `.git`, databases or secrets |
