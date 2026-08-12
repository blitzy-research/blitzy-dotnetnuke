# blitzy-dotnetnuke

This repository holds the DotNetNuke 4.9.0 content-management platform and its
migration onto a modern, containerised two-tier stack. The two live **side by
side**: nothing in the legacy application has been removed, so it remains
buildable and deployable exactly as it was.

| Tree | Contents | Status |
| --- | --- | --- |
| `Library/`, `Website/` | The original VB.NET / .NET Framework 2.0 ASP.NET Web Forms application, its 88-script SQL Server DDL chain and its configuration | **Read-only reference.** Never edited by the migration |
| `DotNetNuke.sln`, `DotNetNuke_VS2008.sln` | The two legacy solution files | **Read-only reference** |
| `backend/` | A C# 12 / .NET 8 ASP.NET Core Web API in six projects, persisting through Entity Framework Core 8 against the existing, unaltered SQL Server schema | Migration target |
| `frontend/` | An Angular 19 single-page administration console built from standalone components and signals | Migration target |
| `docker/` | Two Linux Alpine images and the Compose file that runs them | Migration target |
| `docs/`, `mkdocs.yml` | The published documentation site | Unmodified |
| `MIGRATION_NOTES.md` | Every deliberate behavioural difference the migration introduces, with its legacy citation | Required reading before deploying |

## Prerequisites

| Tool | Version | Why this version |
| --- | --- | --- |
| .NET SDK | **8.0.4xx** | `backend/global.json` pins the SDK band with `rollForward: latestFeature`. Every `Microsoft.*` package is pinned to the 8.0.29 runtime band |
| Node.js | **20.x** (`>=20.20.2 <21`) | `frontend/.nvmrc` and the `engines` block in `frontend/package.json`. Matches the `node:20-alpine` build stage |
| npm | **>=10.8.2** | Ships with the pinned Node |
| Google Chrome | any recent stable | The front-end test run drives it headless. Set `CHROME_BIN` if it is not on the default path |
| SQL Server | 2019 or later | The API maps to the existing DotNetNuke schema. A container is sufficient for local work. The integration suite also needs one, and provisions its own throwaway database — see [the database the integration suite runs against](#the-database-the-integration-suite-runs-against) |
| Docker Engine + Compose v2 | recent | Needed to build and run the containerised topology. Also sufficient on its own for the integration suite, which starts a throwaway SQL Server when no server is configured |

## Backend

All commands run from `backend/`.

```bash
dotnet restore
dotnet build --configuration Release --warnaserror     # must produce 0 warnings and 0 errors
dotnet test  --configuration Release                   # unit and integration suites
dotnet test  --configuration Release --filter "Category=Integration"
```

`--warnaserror` is not optional in this solution. `backend/Directory.Build.props`
enables nullable reference types, treats warnings as errors for every project, and
suppresses exactly two diagnostics, both documented in that file.

### The database the integration suite runs against

The two test commands above need no preparation, and that is deliberate: the
integration suite provisions its own **uniquely named, throwaway** database, applies
the schema, seeds it, and drops it again when the run ends. It never touches a
database you already have. It does need a **real SQL Server**, because the accounts
it signs in as live in the external `aspnet_*` membership tables and the credential
store reports itself unavailable on any other provider — a permissive in-memory
substitute would report a pass while the behaviour under test had stopped executing.

`TestDatabaseFactory` picks the route in this order:

| Order | Condition | Route |
| --- | --- | --- |
| 1 | `DNN_TESTS_USE_MSSQL` set to a truthy value | Starts a throwaway SQL Server container. An explicit request outranks everything, including a configured server |
| 2 | `DNN_TEST_SQLSERVER` set to a connection string with rights to create a database | Creates its database on **that** server. Fastest route, and it needs no container runtime |
| 3 | `DNN_TESTS_USE_MSSQL` set to `0`, `false`, `no` or `off`, with no server configured | Refuses to run. An explicit "off" also vetoes step 4 |
| 4 | Neither variable set, and a container runtime is reachable | Starts a throwaway container. **This is what lets the commands above run unprepared** |
| 5 | Neither variable set, and no container runtime found | Refuses to run, naming both routes |

So: with Docker (or Podman) available, run the gate commands as written. Without a
container runtime, point the suite at any SQL Server 2019 or later, for example

```bash
DNN_TEST_SQLSERVER='Server=localhost,1433;Database=master;User Id=sa;Password=…;TrustServerCertificate=True;Encrypt=True' \
  dotnet test --configuration Release --filter "Category=Integration"
```

The suite fails closed rather than degrading when it can reach neither, and the
failure names both routes. Each run creates a database whose name carries a fresh
identifier, so parallel checkouts sharing one server cannot collide.

### Solution layout

```
backend/
  src/DnnMigration.Domain/          entities, enums, value objects, repository and service abstractions
  src/DnnMigration.Application/     services, DTOs, hand-written mappers, FluentValidation validators
  src/DnnMigration.Infrastructure/  DnnDbContext, Fluent configurations, repositories, security services
  src/DnnMigration.Api/             host, controllers, middleware, authorisation policies
  tests/DnnMigration.UnitTests/
  tests/DnnMigration.IntegrationTests/
```

The reference graph points inward only and is enforced by the compiler:
`Domain` references nothing and takes no third-party package, `Application`
references `Domain`, `Infrastructure` references `Application`, and `Api`
references `Application` and `Infrastructure`. A layering violation is a build
failure rather than a review comment.

### Configuration

Configuration is read from `appsettings.json`, the environment overlay, and then
the environment itself. Every key is overridable with the standard double-underscore
form, which is how the container supplies them.

| Key | Environment form | Notes |
| --- | --- | --- |
| `ConnectionStrings:Default` | `ConnectionStrings__Default` | The existing DotNetNuke database. Replaces the legacy `SiteSqlServer` connection string |
| `Jwt:Secret` | `Jwt__Secret` | **At least 32 bytes, or the host refuses to start** — it also rejects a low-entropy or well-known placeholder value. **No overlay carries one, in any environment**: `appsettings.json` ships it blank and neither the Development nor the Production overlay supplies a value, so every run — including a local one — must pass it in from the environment or a secret store. That is the point: there is no committed key to leak and no environment in which one is silently used |
| `Jwt:Issuer`, `Jwt:Audience`, `Jwt:ExpirationMinutes` | `Jwt__…` | Access tokens are deliberately short-lived; see `MIGRATION_NOTES.md` on sign-out |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0` | A named policy restricted to the front-end origin |

### Running locally

```bash
ConnectionStrings__Default='Server=localhost,1433;Database=DotNetNuke;User Id=…;Password=…;TrustServerCertificate=True;Encrypt=True' \
  dotnet run --project src/DnnMigration.Api
```

Three anonymous health views answer three different questions:

| Endpoint | Question | Probes run |
|---|---|---|
| `GET /health` | **Liveness** — can this process answer at all? | every probe not tagged `ready`, which today means the audit-delivery probe |
| `GET /health/live` | **Process liveness only** | none at all |
| `GET /health/ready` | **Readiness** — can it serve a request end to end? | every probe tagged `ready`, which today means the single database probe |

All three answer plain HTTP with no credential and emit the same document. The split
is deliberate: `docker/api.Dockerfile` and `docker/docker-compose.yml` both probe
`/health`, and the compose topology declares no database service — the store is
external and may legitimately be unreachable while the API starts. A liveness view
that ran a database probe would report the container unhealthy for a reason
unrelated to whether it can answer, and would hold the front-end service back
behind `condition: service_healthy` indefinitely. Point an orchestrator's readiness
probe at `/health/ready`, its restart-or-not probe at `/health/live`, and leave the
container health check on `/health`. Each probe carries a two-second timeout,
comfortably inside the container check's five.

`/swagger` serves the generated OpenAPI document in the Development environment.
Every other endpoint lives under `/api/v1/` and requires a bearer token.

### Database

The schema is **externally owned and is never generated from the model.** The
`InitialCreate` migration is intentionally empty: applying it seeds the migrations
history table without touching a single existing object. `EnsureCreated` must not be
used, because the terminal schema depends on `aspnet_*` membership objects that the
legacy DDL chain only ever alters and never creates. The reasoning is recorded in
`MIGRATION_NOTES.md`.

## Frontend

All commands run from `frontend/`.

```bash
npm ci                                                  # requires the committed package-lock.json
npx ng build --configuration production                 # emits dist/dnn-migration/browser/
npx ng test --watch=false --browsers=ChromeHeadless --code-coverage
```

`karma.conf.js` declares a no-sandbox headless launcher, without which the test run
cannot start inside a container. The production build emits to
`dist/dnn-migration/browser/`, which is the exact path `docker/frontend.Dockerfile`
copies.

The production API base URL is the **relative** path `/api/v1`. The reverse proxy
serves the application and proxies `/api/` to the API, so the browser reaches both
through one origin; an absolute URL would resolve only inside the container network.

A dependency audit is deliberately **not** part of the build. A freshly scaffolded
Angular 19 workspace reports advisories before a line of application code exists,
almost all of them in build-toolchain packages that never reach the browser bundle;
the one runtime advisory concerns a hydration path this application does not
install. The reasoning is recorded in `MIGRATION_NOTES.md`.

## Containers

```bash
cp docker/.env.example docker/.env      # then fill in the values
docker compose -f docker/docker-compose.yml --env-file docker/.env build
docker compose -f docker/docker-compose.yml --env-file docker/.env up -d
docker compose -f docker/docker-compose.yml --env-file docker/.env exec -T api \
  wget --no-verbose --tries=1 --spider http://127.0.0.1:8080/health
curl -f http://localhost:4200                  # 200, the SPA document
curl -f http://localhost:8080/health           # 200, the anonymous liveness view
docker compose -f docker/docker-compose.yml --env-file docker/.env down
```

The API image runs as a non-root user and exposes port 8080; the front-end image
serves the built bundle from nginx on port 80, published as 4200. The front-end
service will not start until the API reports healthy — which is why the probe above
is the liveness view. Add `curl -f http://localhost:8080/health/ready` when you want
to confirm the external database is reachable as well; a 503 there is a dependency
report rather than a container fault.

The API image installs ICU and turns globalisation-invariant mode off, because the
mandated Alpine runtime base sets that mode and the SQL Server client library
refuses to open a connection while it is on. That two-line addition to the runtime
stage of `docker/api.Dockerfile` is one of the documented deviations from the
preserved container example, and it is applied rather than merely described so the
composed topology works as delivered. Every deviation is recorded in
`MIGRATION_NOTES.md`.

`docker compose` — two words — is the invocation throughout. The legacy
`docker-compose` script is end-of-life and absent from current Docker
distributions, so the commands above are also how the migration plan's container
build and end-to-end gates are executed; a literal v1 spelling exits 127 without
starting anything, which is a tooling fact rather than a project one.

Every `docker compose` command prints `the attribute 'version' is obsolete, it will
be ignored`. That warning is expected and must not be "fixed": the `version` key is
part of the compose file the migration plan preserves verbatim, the exit code is
unaffected, and deleting the key would be a deviation with nothing to gain.

### Operating the topology

- **Redeploying the API alone is supported.** `docker compose up -d
  --force-recreate api` may place the API container on a different address, and the
  front end resolves the API per request rather than once at start-up, so it follows
  the new address by itself. Calls made in the first few seconds may be answered
  `503` with `Retry-After: 5` and an RFC 7807 body while the ten-second DNS entry
  ages out; nothing needs restarting. The front end also starts and keeps serving
  the application while the API is absent — only `/api/` calls fail, and they fail
  with a problem document rather than an HTML error page.
- **Treat an API restart as a sign-out.** Refresh-token state is held in the API
  process, because the DotNetNuke schema this API maps onto is immutable and owns no
  table for it. A restart or a redeploy therefore invalidates every refresh token:
  access tokens already issued stay valid until they expire, and after that each
  signed-in user authenticates again. The front end handles this cleanly — a
  rejected refresh returns the user to the sign-in screen — but the effect is
  visible, so a redeploy is best scheduled accordingly.
- **Run exactly one API instance.** For the same reason, a second replica without
  sticky routing would reject refresh tokens issued by the first. Introduce a
  shared, durable store behind `IRefreshTokenStore` before scaling out.
- **A database outage is reported as a dependency failure, not a server fault.**
  Data endpoints answer `503` with `Retry-After` while `/health` and `/health/live`
  stay `200` and `/health/ready` reports `503`. The API is not restarted by the
  outage and recovers on its own when the database returns.
- **`/health` is not reachable through `:4200`.** Only `/api/` is proxied. Probe the
  health views on the API's own port, which is what the container health check and
  the end-to-end gate do.
- **Deployment values arrive as environment variables**, which means anyone able to
  reach the Docker socket can read them from a running container with `docker
  inspect`. That is inherent to the compose file the plan preserves; for a hardened
  deployment `docker/.env.example` describes how to deliver the same values from a
  secret store instead.

## Contributing to the migration

- `Library/` and `Website/` are authoritative inputs and are never edited. The
  business rules, column names, validation rules and wording in the target are
  derived from them and cite them.
- Every deliberate behavioural difference belongs in `MIGRATION_NOTES.md` and is
  annotated at the point of departure with a `// MIGRATION:` comment.
  `MIGRATION_NOTES.md` is append-only.
- No business logic in a controller; no data access outside a repository; no entity
  crosses the API boundary.
- Every style value in the front end resolves to a design token in
  `frontend/src/styles/_tokens.scss`. The only literals permitted are `0`, `none`,
  `auto`, `inherit`, `currentColor` and `transparent`.
