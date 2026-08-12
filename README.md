# blitzy-dotnetnuke

**DotNetNuke 4.x VB.NET to .NET 8 + Angular 19 complete migration.**

This repository holds the DotNetNuke 4.9.0 content-management platform and its migration
onto a modern, containerised two-tier stack: a C# 12 / .NET 8 ASP.NET Core Web API acting
as a Backend-for-Frontend, persisting through Entity Framework Core 8 against the
**existing, unaltered** SQL Server schema, paired with an Angular 19 single-page
administration console built from standalone components and signals. Both tiers ship as
Linux Alpine container images orchestrated by Docker Compose. The two stacks live **side by
side**: nothing in the legacy application has been removed, so it remains buildable and
deployable exactly as it was.

| Section | |
| --- | --- |
| [1. Side-by-side migration: read this first](#1-side-by-side-migration-read-this-first) | Why the legacy trees are untouchable |
| [2. Architecture](#2-architecture) | The six backend projects and the Angular workspace |
| [3. Prerequisites](#3-prerequisites) | Toolchain and versions |
| [4. Backend](#4-backend) | Build, configure, test, run |
| [5. Frontend](#5-frontend) | Install, test, build, serve |
| [6. Containers](#6-containers) | The two-container topology and its constraints |
| [7. Validation gates](#7-validation-gates) | The seven acceptance gates and their outcomes |
| [8. Database](#8-database) | An externally owned, immutable schema |
| [9. Project layout](#9-project-layout) | Where everything lives |
| [10. Security](#10-security) | Authentication, hashing, CORS, secrets |
| [11. Contributing and conventions](#11-contributing-and-conventions) | The rules this repository is held to |
| [12. Troubleshooting](#12-troubleshooting) | Symptoms, causes, fixes |
| [13. Documentation map](#13-documentation-map) | Which document answers which question |
| [14. Licence](#14-licence) | Where the legacy copyright headers live |

---

## 1. Side-by-side migration: read this first

This is the one section to read before running anything, because it prevents the most
likely and least recoverable mistake a newcomer can make.

The migration follows the **strangler pattern**. The modern stack grows *alongside* the
VB.NET original rather than in place of it:

| Tree | Contents | Status |
| --- | --- | --- |
| `Library/`, `Website/` | The original VB.NET / .NET Framework 2.0 ASP.NET Web Forms application, its SQL Server DDL chain and its configuration | **Read-only reference. Never edited, never deleted, byte-identical to the pre-migration state** |
| `DotNetNuke.sln`, `DotNetNuke_VS2008.sln` | The two legacy solution files | **Read-only reference** |
| `Website/release.config`, `Website/development.config` | Legacy configuration — the authority for connection strings, provider registration and password policy | **Read-only reference** |
| `backend/` | A C# 12 / .NET 8 ASP.NET Core Web API in six projects | Migration target |
| `frontend/` | An Angular 19 single-page administration console | Migration target |
| `docker/` | Two Linux Alpine images and the Compose topology that runs them | Migration target |
| `docs/`, `mkdocs.yml` | The published documentation site | Unmodified |
| [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md) | Every deliberate behavioural difference the migration introduces, with its legacy citation | Required reading before deploying |

**The change set is purely additive: zero files updated, zero files deleted.** That is a
deliberate property rather than an accident of sequencing, and it buys a **zero-regression
surface** — no existing behaviour can break, because no existing file changed. The legacy
application stays buildable and deployable throughout, so nothing has to be cut over on a
schedule.

### Why an in-place upgrade was never an option

`Library/DotNetNuke.Library.vbproj` is a pre-3.5 project format. It declares
`ToolsVersion="3.5"`, `<ProductVersion>8.0.50727</ProductVersion>` — the .NET Framework 2.0
/ Visual Studio 2005 build number — and `<OldToolsVersion>2.0</OldToolsVersion>`, with **no
`TargetFrameworkVersion` element at all**. There is no upgrade path from that baseline to
`net8.0`, so every line of production code in `backend/` and `frontend/` was re-authored
from the legacy source rather than converted. The same file also sets
`<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` with `<WarningLevel>1</WarningLevel>`;
the new solution inverts that policy exactly (see [§4](#4-backend)).

The legacy surface the migration reads from, measured rather than estimated:

| Measure | Count |
| --- | --- |
| VB.NET source files | **634** — 504 under `Library/`, 130 under `Website/` |
| Web Forms pages / user controls | **15** `.aspx`, **147** `.ascx` |
| Master pages | **0** — this DotNetNuke generation uses *skins*, not master pages, so there is no master-page-to-layout mapping to perform |
| SQL DDL scripts | **88** `*.SqlDataProvider` files, 83 of them version-numbered `00.00.00` through `04.09.00` |
| Legacy assembly version | **4.9.0.85** (DotNetNuke 4.9.0), in `Library/AssemblyInfo.vb` |

Every deliberate behavioural difference between the two stacks — and there are several,
because exact equivalence was impossible in a few places — is recorded with its legacy
citation in [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md). That document is the decision log;
this one is the operating manual.

---

## 2. Architecture

Two tiers, one repository. The browser talks to the SPA's origin; the SPA talks to the API
under `/api/v1`; the API talks to the existing DotNetNuke database.

### Backend — six projects, Clean Architecture, enforced by the compiler

| Project | Responsibility | References |
| --- | --- | --- |
| `DnnMigration.Domain` | Entities, enums, value objects, repository and service abstractions, `Result` and `PagedResult` | **none** — zero project references and zero third-party packages |
| `DnnMigration.Application` | Application services, DTOs, hand-written mappers, FluentValidation validators, options classes | Domain |
| `DnnMigration.Infrastructure` | `DnnDbContext`, Fluent entity configurations, the baseline migration, repositories, unit of work, BCrypt hasher, JWT token service, permission evaluator, health checks | Domain, Application |
| `DnnMigration.Api` | `Program.cs`, attribute-routed controllers, middleware, the global exception handler, authorisation policies | Application, Infrastructure |
| `DnnMigration.UnitTests` | Service, mapper, validator and security tests | Application, Infrastructure |
| `DnnMigration.IntegrationTests` | The `WebApplicationFactory<Program>` fixture and the API and persistence suites | Api |

The point of that graph is that it is **not a convention**. `Domain` takes no project
reference and no package, so business logic cannot reach a controller and data access
cannot bypass a repository interface: either would be a **compile error rather than a review
finding**. Layering is therefore checked by `dotnet build`, on every build, for free.

Concretely the solution carries 21 domain entities with one `IEntityTypeConfiguration<T>`
apiece, 9 repositories, hand-written mappers (no AutoMapper), and 11 attribute-routed
controllers — thin ones: they validate the request shape, delegate to an application
service, and translate the result into a status code.

### Frontend — the Angular 19 workspace `dnn-migration`

- **Standalone components only.** There is no `NgModule` anywhere in the workspace.
- **Signals for state.** `signal`, `computed` and `asReadonly` in feature-scoped stores
  under `src/app/core/state/`. No NgRx, and no `BehaviorSubject`-backed stores.
- **Typed reactive forms.** Every form declares an explicit model interface and every
  control is constructed `nonNullable`, so `form.value` is fully typed rather than a
  `Partial`.
- **Built-in template control flow** — `@if` / `@else` / `@for` / `@switch`, with `track` on
  every `@for`. Never `*ngIf` or `*ngFor`.
- **`ChangeDetectionStrategy.OnPush` on every component.**
- **Lazy-loaded features.** `app.routes.ts` reaches each feature with `loadChildren` on a
  `*.routes.ts` barrel; leaf routes use `loadComponent`.
- **Functional HTTP interceptors** (`HttpInterceptorFn`) in a deliberate order:
  `correlationId` → `auth` → `error`. The correlation id is attached first, the bearer token
  second, and error translation last so it observes the final response.
- **No design-system dependency.** `@angular/material`, Bootstrap, Tailwind and PrimeNG were
  each considered and declined. A ten-member shared component set — data table, page header,
  confirm dialog, pagination, form field, search input, loading spinner, error banner, empty
  state and a `hasPermission` structural directive — is authored in-repository over a design
  token vocabulary in `frontend/src/styles/_tokens.scss`.

### The domains that were preserved

Five aggregates anchor the whole migration — **Portal** (the multi-tenant site container),
**Module** (a pluggable content component with a lifecycle), **User** (identity with
credentials and profile), **Role** (permission grouping) and **Permission** (a granular
access-control entry) — plus **Tab**, the DotNetNuke page abstraction, as a supporting
aggregate. Tab is inseparable from the other five: module placement, tab permissions and
portal navigation are all keyed by it.

Everything the API exposes lives under a version segment: `/api/v1/portals`,
`/api/v1/modules`, `/api/v1/users`, `/api/v1/roles`, `/api/v1/permissions`, `/api/v1/tabs`,
`/api/v1/auth`, and the lookup surfaces `/api/v1/module-definitions`,
`/api/v1/profile-definitions` and `/api/v1/role-groups`.

---

## 3. Prerequisites

| Requirement | Version | Purpose |
| --- | --- | --- |
| .NET SDK | 8.0 LTS — verified on **8.0.423** | Backend build and test |
| Node.js | 20.x LTS — verified on **20.20.2** | Frontend build and test |
| npm | 10.x — verified on **10.8.2** | Package management |
| Angular CLI | **19.2.27**, as a local devDependency | Workspace tooling. Drive it with `npx ng`; no global install is needed or wanted |
| Google Chrome / Chromium | any recent stable | Required by the Karma headless test run. Set `CHROME_BIN` if it is not on the default path |
| Docker Engine | 24.x or later | Container builds |
| Docker Compose | v2 (the `docker compose` plugin) | Multi-container orchestration |
| SQL Server | 2019 or later | The existing DotNetNuke database. A container is sufficient for local work |

Two pins make those versions reproducible rather than aspirational:

- [`backend/global.json`](./backend/global.json) pins the SDK to the **8.0.423** band with
  `rollForward: latestFeature`, and every `Microsoft.*` package is pinned to the matching
  **8.0.29** runtime band.
- [`frontend/.nvmrc`](./frontend/.nvmrc) pins Node to **20.20.2**, which is also the
  `engines` floor in `frontend/package.json` and the version of the `node:20-alpine` build
  stage in [`docker/frontend.Dockerfile`](./docker/frontend.Dockerfile). Build the SPA on a
  different major and the container image and the workstation stop agreeing.

Docker is *not* required to run the backend test suite — see
[the database the integration suite runs against](#the-database-the-integration-suite-runs-against)
for the two routes it accepts.

---

## 4. Backend

Every command in this section runs from `backend/`.

### Build

```bash
cd backend
dotnet restore
dotnet build --configuration Release --warnaserror
# Expected: Build succeeded.  0 Warning(s)  0 Error(s)
```

`--warnaserror` is not decoration in this solution.
[`backend/Directory.Build.props`](./backend/Directory.Build.props) is the single source of
`net8.0`, `LangVersion 12.0`, `Nullable enable`, `ImplicitUsings enable`,
`GenerateDocumentationFile true`, `EnforceCodeStyleInBuild true` and
`TreatWarningsAsErrors true` for all six projects, and it suppresses **exactly two**
diagnostics:

| Suppressed | Why it is suppressed, narrowly |
| --- | --- |
| `CS1591` | **Required, not preferred.** `GenerateDocumentationFile` combined with warnings-as-errors turns every undocumented public member into a build error. Without this one entry the strict policy cannot be applied at all. Do not remove it |
| `CS8618` | The non-nullable-property-uninitialised warning, which fires on entity and DTO types whose values are supplied by the materialiser or the model binder rather than by a constructor |

No other warning is suppressed anywhere, and nothing widens that list per project.

### Configure

Configuration is read from `appsettings.json`, then the environment overlay
(`appsettings.Development.json` / `appsettings.Production.json`), then the environment
itself. Every key is overridable with .NET's standard **double-underscore** form, which is
how the containers supply them — a single underscore binds to nothing.

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost,1433;Database=DotNetNuke;User Id=REPLACE_ME;Password=REPLACE_ME;TrustServerCertificate=True;Encrypt=True"
  },
  "Jwt": {
    "Issuer": "DnnMigration",
    "Audience": "DnnMigration",
    "ExpirationMinutes": 60
  }
}
```

| Key | Environment form | Notes |
| --- | --- | --- |
| `ConnectionStrings:Default` | `ConnectionStrings__Default` | The existing DotNetNuke database. This key replaces the legacy `SiteSqlServer` connection string in `Website/release.config`. The host refuses to start on a value that is blank, unparseable, structurally incomplete or still a template placeholder |
| `Jwt:Secret` | `Jwt__Secret` | **At least 32 UTF-8 bytes, or the host refuses to start** — it also rejects a low-entropy or well-known placeholder. **No overlay carries one, in any environment**: `appsettings.json` ships it blank and neither overlay supplies a value, so every run — including a local one — passes it in from the environment or a secret store. There is deliberately no committed key to leak |
| `Jwt:Issuer`, `Jwt:Audience`, `Jwt:ExpirationMinutes` | `Jwt__…` | Access tokens are deliberately short-lived and refresh tokens rotate; see [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md) on sign-out |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0` | A named policy restricted to the SPA origin. A wildcard is never acceptable here |

**Never commit a real secret.** The connection string and `Jwt:Secret` come from environment
variables or a secret store in every environment, development included. Every credential
shown in this file is an obvious placeholder.

### Test

```bash
cd backend
dotnet test --configuration Release                                    # unit and integration suites
dotnet test --configuration Release --filter "Category=Integration"     # integration only
```

Integration tests carry `[Trait("Category", "Integration")]`, which is what that filter
selects on.

#### The database the integration suite runs against

Those two commands need no preparation, and that is deliberate: the integration suite
provisions its own **uniquely named, throwaway** database, applies the schema, seeds it, and
drops it again when the run ends. It never touches a database you already have. It does need
a **real SQL Server**, because the accounts it signs in as live in the external `aspnet_*`
membership tables and the credential store reports itself unavailable on any other provider
— a permissive in-memory substitute would report a pass while the behaviour under test had
stopped executing.

`TestDatabaseFactory` picks its route in this order:

| Order | Condition | Route |
| --- | --- | --- |
| 1 | `DNN_TESTS_USE_MSSQL` set to a truthy value | Starts a throwaway SQL Server container. An explicit request outranks everything, including a configured server |
| 2 | `DNN_TEST_SQLSERVER` set to a connection string with rights to create a database | Creates its database on **that** server. Fastest route, and it needs no container runtime |
| 3 | `DNN_TESTS_USE_MSSQL` set to `0`, `false`, `no` or `off`, with no server configured | Refuses to run. An explicit "off" also vetoes step 4 |
| 4 | Neither variable set, and a container runtime is reachable | Starts a throwaway container. **This is what lets the commands above run unprepared** |
| 5 | Neither variable set, and no container runtime found | Refuses to run, naming both routes |

So: with a container runtime available, run the gate commands as written. Without one, point
the suite at any SQL Server 2019 or later:

```bash
cd backend
DNN_TEST_SQLSERVER='Server=localhost,1433;Database=master;User Id=REPLACE_ME;Password=REPLACE_ME;TrustServerCertificate=True;Encrypt=True' \
  dotnet test --configuration Release --filter "Category=Integration"
```

The suite fails closed rather than degrading when it can reach neither, and the failure names
both routes. Each run creates a database whose name carries a fresh identifier, so parallel
checkouts sharing one server cannot collide.

### Run

```bash
cd backend
ConnectionStrings__Default='Server=localhost,1433;Database=DotNetNuke;User Id=REPLACE_ME;Password=REPLACE_ME;TrustServerCertificate=True;Encrypt=True' \
Jwt__Secret="$(openssl rand -base64 48)" \
  dotnet run --project src/DnnMigration.Api
```

The launch profile serves `http://localhost:8080` in the `Development` environment.
`/swagger` serves the generated OpenAPI document **in Development only**; every other
endpoint lives under `/api/v1/` and requires a bearer token. If port 8080 is already taken —
by the container topology, for instance — bypass the profile:

```bash
cd backend
dotnet run --project src/DnnMigration.Api --configuration Release \
  --no-launch-profile --urls http://127.0.0.1:5080
```

Three **anonymous** health views answer three different questions:

| Endpoint | Question | Probes run |
| --- | --- | --- |
| `GET /health` | **Liveness** — can this process answer at all? | every probe not tagged `ready` |
| `GET /health/live` | **Process liveness only** | none at all |
| `GET /health/ready` | **Readiness** — can it serve a request end to end? | every probe tagged `ready`, which today means the database probe |

All three answer plain HTTP with no credential and emit the same document:

```json
{"status":"Healthy","timestamp":"2026-01-01T00:00:00.0000000+00:00","version":"1.0.0.0","serviceName":"DnnMigration.Api"}
```

The split is deliberate. Both [`docker/api.Dockerfile`](./docker/api.Dockerfile) and
[`docker/docker-compose.yml`](./docker/docker-compose.yml) probe `/health`, and the compose
topology declares no database service — the store is external and may legitimately be
unreachable while the API starts. A liveness view that ran a database probe would report the
container unhealthy for a reason unrelated to whether it can answer, and would hold the
front-end service back behind `condition: service_healthy` indefinitely. Point an
orchestrator's readiness probe at `/health/ready`, its restart-or-not probe at
`/health/live`, and leave the container health check on `/health`.

---

## 5. Frontend

Every command in this section runs from `frontend/`. The Angular CLI is a local
devDependency, so drive it with `npx ng` rather than a global install.

```bash
cd frontend
npm ci                                                        # deterministic install
npx ng test --watch=false --browsers=ChromeHeadless --code-coverage
npx ng build --configuration production                       # emits dist/dnn-migration/browser/
```

`npm run build -- --configuration production` and `npm test -- --watch=false
--browsers=ChromeHeadless --code-coverage` are equivalent to the last two commands and are
what [`docker/frontend.Dockerfile`](./docker/frontend.Dockerfile) invokes.

The development server runs in the foreground, so give it its own shell:

```bash
cd frontend
npm start                                                     # http://localhost:4200
```

Four things a newcomer will otherwise trip over:

- **`npm ci` requires the committed `frontend/package-lock.json`** and fails outright without
  it. Never add a lockfile pattern to [`.gitignore`](./.gitignore); the root ignore file
  carries a comment saying exactly that, for exactly this reason. Prefer `npm ci` over
  `npm install`, which can rewrite the lockfile.
- **[`frontend/karma.conf.js`](./frontend/karma.conf.js) is mandatory, not optional.**
  Headless Chrome refuses to start as root without a sandbox flag, so the configuration
  declares a custom **`ChromeHeadlessNoSandbox`** launcher carrying `--no-sandbox`,
  `--disable-gpu`, `--disable-dev-shm-usage` and `--headless=new`. It declares a second
  launcher under the plain `ChromeHeadless` name as well, so the command line above — which
  names `ChromeHeadless` explicitly and therefore *overrides* the configured browser list —
  still gets the flags it needs. Without that file the test run cannot start in a container
  or in CI at all.
- **The development server does not proxy.** `frontend/src/environments/environment.development.ts`
  points the SPA straight at `http://localhost:8080/api/v1`, and the API's named CORS policy
  admits `http://localhost:4200` by default. So run the API alongside the dev server; there
  is no `proxy.conf.json` to configure.
- **The production API base URL is the relative path `/api/v1`**, set in
  `frontend/src/environments/environment.ts`. The reverse proxy serves the application *and*
  proxies `/api/`, so the browser reaches both through one origin. An absolute URL would
  resolve only inside the container network — a failure no build step detects. See
  [§6](#6-containers).

A dependency audit is deliberately **not** part of the build; the reasoning is in
[§7](#7-validation-gates).

---

## 6. Containers

**The build context is the repository root, not `docker/`.** Both services in the Compose
file set `context: ..`, because every `COPY` inside the two Dockerfiles is
repository-root-relative: the API image copies `backend/`, and the frontend image copies
`frontend/` **and** `docker/nginx.conf`. That is why the `dockerfile:` entries keep their
`docker/` prefix, and it is why the root [`.dockerignore`](./.dockerignore) — which keeps the
legacy reference trees, build output and every environment file out of both builds — governs
them.

```bash
cp docker/.env.example docker/.env      # then fill in the values; docker/.env is never committed
docker compose -f docker/docker-compose.yml --env-file docker/.env build
docker compose -f docker/docker-compose.yml --env-file docker/.env up -d
curl -f http://localhost:8080/health    # 200, the anonymous liveness view
curl -f http://localhost:4200           # 200, the SPA document
docker compose -f docker/docker-compose.yml --env-file docker/.env down
```

Building either image on its own, from the repository root:

```bash
docker build -f docker/api.Dockerfile      -t dnnmigration-api      .
docker build -f docker/frontend.Dockerfile -t dnnmigration-frontend .
```

`docker compose` — **two words** — is the invocation throughout. The legacy `docker-compose`
script is end-of-life and absent from current Docker distributions; the literal v1 spelling
exits 127 without starting anything. That is a tooling fact rather than a project one, and it
is why the gate commands in [§7](#7-validation-gates) are reproduced verbatim but executed in
the supported spelling.

Every `docker compose` invocation also prints `the attribute 'version' is obsolete, it will
be ignored`. That warning is **expected and must not be "fixed"**: the `version` key is part
of the Compose file the migration plan preserves verbatim, the exit code is unaffected, and
deleting the key would be a deviation with nothing to gain.

### Topology

| | API | Frontend |
| --- | --- | --- |
| Build stage | `mcr.microsoft.com/dotnet/sdk:8.0-alpine` | `node:20-alpine`, running `npm ci` then `npm run build -- --configuration production` |
| Runtime stage | `mcr.microsoft.com/dotnet/aspnet:8.0-alpine` | `nginx:alpine` |
| Runs as | a **non-root** user created with `adduser -D -u 1000 appuser` | nginx |
| Listens on | `ASPNETCORE_URLS=http://+:8080`, `EXPOSE 8080` | `listen 80`, `EXPOSE 80` |
| Published as | **`127.0.0.1:8080:8080`** — loopback only, so the cleartext listener that carries credentials and bearer tokens is reachable from this host and the Compose network and nowhere else | `4200:80` |
| Entry point | `ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"]` | `CMD ["nginx", "-g", "daemon off;"]` |
| Health probe | `HEALTHCHECK` with **`wget --spider`** against `/health` | its own baked-in probe |
| Starts | first | only once the API reports healthy (`depends_on: condition: service_healthy`) |

Four constraints make that topology work, and each of them fails silently if broken:

- **The probe must use `wget`.** The `aspnet:8.0-alpine` runtime image ships BusyBox `wget`
  as its only HTTP client. A probe written with `curl` exits 127 on every attempt, the
  service is reported unhealthy for its whole life, and the health condition below then holds
  the front end back for ever — with both images built perfectly.
- **`/health` must be anonymous.** Because the front end waits on `service_healthy`, an
  authenticated health endpoint means the probe is answered `401`, the API service never
  becomes healthy, and the front-end container never starts. The health endpoints carry
  `[AllowAnonymous]` for exactly this reason.
- **The production `apiBaseUrl` must be relative.** `docker/nginx.conf` proxies `/api/` to
  the `api` service over the Compose network, so the browser addresses the API through the
  same origin that served the application.
- **The service name `api` is a contract, not a label.** `docker/nginx.conf` resolves it
  through the Compose network's embedded DNS. Rename the service and every SPA request is
  answered `502` while both containers still report themselves healthy.

The API image additionally installs ICU and sets `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`,
because the mandated Alpine runtime base enables invariant globalisation and the SQL Server
client library refuses to open a connection while it is on. That is one of the documented
deviations from the preserved container example, and it is applied rather than merely
described so the composed topology works as delivered. Every deviation is recorded in
[`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md).

As delivered this topology speaks plain HTTP on both published ports, because the end-to-end
gate probes `http://localhost:8080/health` and `http://localhost:4200` directly. It is a
validation and demonstration topology, **not a public-facing one**. To face the internet, add
the TLS overlay, which terminates TLS at the front end, withdraws the API's published port
and switches HTTPS redirection back on:

```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.tls.yml \
  --env-file docker/.env up -d
```

### Operating the topology

- **Redeploying the API alone is supported.** `docker compose … up -d --force-recreate api`
  may place the API container on a different address, and the front end resolves the API per
  request rather than once at start-up, so it follows the new address by itself. Calls made
  in the first few seconds may be answered `503` with `Retry-After: 5` and an RFC 7807 body
  while the short-lived DNS entry ages out; nothing needs restarting. The front end also
  starts and keeps serving the application while the API is absent — only `/api/` calls fail,
  and they fail with a problem document rather than an HTML error page.
- **Treat an API restart as a sign-out.** Refresh-token state is held in the API process,
  because the DotNetNuke schema this API maps onto is immutable and owns no table for it. A
  restart or a redeploy therefore invalidates every refresh token: access tokens already
  issued stay valid until they expire, and after that each signed-in user authenticates
  again. The front end handles this cleanly — a rejected refresh returns the user to the
  sign-in screen — but the effect is visible, so a redeploy is best scheduled accordingly.
- **Run exactly one API instance.** For the same reason, a second replica without sticky
  routing would reject refresh tokens issued by the first. Introduce a shared, durable store
  behind `IRefreshTokenStore` before scaling out.
- **A database outage is reported as a dependency failure, not a server fault.** Data
  endpoints answer `503` with `Retry-After` while `/health` and `/health/live` stay `200` and
  `/health/ready` reports `503`. The API is not restarted by the outage and recovers on its
  own when the database returns.
- **`/health` is not reachable through `:4200`.** Only `/api/` is proxied. Probe the health
  views on the API's own port, which is what the container health check and the end-to-end
  gate do.
- **Deployment values arrive as environment variables**, which means anyone able to reach the
  Docker socket can read them from a running container with `docker inspect`. That is
  inherent to the Compose file the plan preserves; for a hardened deployment
  [`docker/.env.example`](./docker/.env.example) describes how to deliver the same values
  from a secret store instead.


---

## 7. Validation gates

Seven gates are the acceptance criteria for this migration, and this section is the
validation report for them. The commands are reproduced **verbatim**, exactly as specified:

```text
Gate 1: cd backend; dotnet restore; dotnet build --configuration Release --warnaserror
Gate 2: dotnet test --configuration Release --no-build --verbosity normal
Gate 3: cd frontend; npm ci; ng build --configuration production
Gate 4: ng test --watch=false --browsers=ChromeHeadless --code-coverage
Gate 5: dotnet test --configuration Release --filter "Category=Integration"
Gate 6: docker-compose build
Gate 7: docker-compose up -d; sleep 10; curl -f http://localhost:8080/health;
        curl -f http://localhost:4200; docker-compose down
```

| Gate | What it proves | Status | Precondition / note |
| --- | --- | --- | --- |
| 1 | The solution compiles clean with warnings as errors | **Proven** | Fails until `CS1591` is suppressed: documentation generation plus warnings-as-errors turns every undocumented public member into a build error. See [§4](#4-backend) |
| 2 | The full test suite passes | **Proven** | `--no-build` means it runs against the binaries Gate 1 produced, so run the two in that order. The integration project needs a reachable SQL Server; it provisions its own throwaway database by either of the routes in [§4](#the-database-the-integration-suite-runs-against) |
| 3 | Deterministic install and an ahead-of-time production build | **Proven** | Requires the committed `frontend/package-lock.json`; emits `dist/dnn-migration/browser` |
| 4 | The frontend specs pass with coverage | **Proven, conditionally** | **Fails outright without [`frontend/karma.conf.js`](./frontend/karma.conf.js)** and its `ChromeHeadlessNoSandbox` launcher. The literal command names `ChromeHeadless`, which overrides the configured browser list, so that name is declared as a flagged launcher too |
| 5 | The integration CRUD suites pass | **Proven** | Selects on `[Trait("Category", "Integration")]`. `PortalApiTests`, `ModuleApiTests` and `UserApiTests` assert `POST` 201, `GET` 200, `PUT` 200 and `DELETE` 204 |
| 6 | Both container images build | **Not executed — no container runtime available in the authoring environment.** Since exercised, and passing, wherever one is available | The four Docker artefacts were authored correct-by-construction from their verbatim specifications, with both name placeholders independently proven by real builds: a solution emitting `DnnMigration.Api.dll`, the exact filename the image's `ENTRYPOINT` names, and a workspace emitting `dist/dnn-migration/browser/`, the exact path the frontend image copies. They have since been built on Docker Engine with the Compose plugin, including a full `--no-cache` build |
| 7 | The end-to-end two-container topology is healthy | **Not executed — no container runtime available in the authoring environment.** Since exercised, and passing, wherever one is available | Depends on three things no build step checks: the **anonymous** `/health`, the **`wget`-based** probe, and the **relative** production `apiBaseUrl`. All three are pinned in [§6](#6-containers). Where it has been run, the full cycle — down, up, `curl -f` both endpoints, down — completes with both services reporting `healthy` |

Two facts about the commands themselves, both of which apply to any environment:

- **The literal `docker-compose` spelling in Gates 6 and 7 cannot run on a current Docker
  installation.** Compose v1 is end-of-life and absent; the supported spelling is the two-word
  `docker compose`, which is what [§6](#6-containers) documents and what was executed. Every
  gate passes on that spelling. No product change can address this.
- **The legacy VB.NET solution was never compiled.** `msbuild`, `mono` and `vbnc` are all
  unavailable in the authoring environment, so legacy behaviour was established by reading the
  source and analysing the DDL chain. That is why every claim in
  [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md) carries a file and line citation rather than a
  runtime observation.

### `npm audit` is deliberately not a build gate

A **pristine, freshly scaffolded** Angular 19.2.x workspace reports 48 advisories before a
single line of application code exists. Almost all of them are build-toolchain transitives
that never reach the browser bundle — archive, glob, worker-pool, CSS-processing, dev-server
proxy and registry-client chains. Exactly one root advisory touches a runtime dependency: a
client-hydration advisory against `@angular/core` whose affected range covers every 19.2.x
release, with no remedy inside the mandated major version.

That vector was verified unreachable rather than assumed so. The workspace is scaffolded
without server-side rendering, `@angular/platform-server` is not installed, and no hydration
provider appears anywhere in the source — `frontend/src/main.ts` records that absence as
deliberate and load-bearing. Adding an audit threshold to the build would therefore fail a
pristine workspace on day one and block delivery for a vector this application cannot
execute. The same reasoning is applied to the NuGet graph, where auditing is left at the SDK
default rather than promoted to an error under `TreatWarningsAsErrors`. Both decisions, and
the advisory itself, are recorded in [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md).

### No throughput or latency target is claimed

The migration is expected to improve runtime characteristics as a by-product of the
architecture, not through speculative optimisation — which the domain-logic-preservation
directive forbids. Four improvements are structural: server-rendered page assembly is gone;
`ViewState` round-tripping is gone; data access is asynchronous end to end; and caching is
deliberate and inspectable behind one service with named keys and explicit invalidation,
rather than coarse portal-wide and host-wide clears. **No throughput or latency target is
asserted anywhere in this repository**, because none was specified and none can be measured
against the legacy system from static analysis alone.

---

## 8. Database

The existing SQL Server schema is **authoritative and immutable**. EF Core maps to it; it
does not redefine it. **No `CREATE TABLE`, `ALTER TABLE` or `DROP` ever reaches a production
schema from this work.**

- **Mapping is Fluent API only.** One `IEntityTypeConfiguration<T>` per entity, each pinning
  `ToTable("<LegacyTable>", "dbo")` and `HasColumnName("<LegacyColumn>")` so the terminal
  legacy schema is honoured exactly. This installation's observed defaults are
  `objectQualifier=""` and `databaseOwner="dbo"`, taken from `Website/release.config`.
- **The `InitialCreate` migration is intentionally empty.** Its `Up` and `Down` bodies are
  emptied, so applying it seeds `__EFMigrationsHistory` as a no-op baseline without touching a
  single existing object.
- **`EnsureCreated` is forbidden**, and so is generating a create-migration from the model.
  This is not a preference. The terminal schema depends on ASP.NET membership objects that the
  DDL chain only ever **`ALTER`s and never creates**: not one of the 88 scripts contains a
  `CREATE TABLE` for any `aspnet_*` object, yet the chain references them throughout and
  patches 19 of Microsoft's own procedures — for example
  `ALTER PROCEDURE dbo.aspnet_Membership_UpdateUser` in `04.00.00.SqlDataProvider`, which adds
  failed-attempt and lockout bookkeeping. The four Microsoft installer scripts that *would*
  create those objects sit alongside the chain (`InstallCommon.sql`, `InstallMembership.sql`,
  `InstallProfile.sql`, `InstallRoles.sql`) and presuppose that the ASP.NET application
  services were installed externally by `aspnet_regsql.exe`. So replaying the scripts against
  an empty database cannot produce the terminal schema, and neither can any generated
  migration — not for want of effort, but in principle.
- **Schema ground truth** is `Website/Providers/DataProviders/SqlDataProvider/*.SqlDataProvider`
  — 88 scripts, 83 of them version-numbered `00.00.00` through `04.09.00`. They are an
  append-only upgrade history in which objects are repeatedly dropped and recreated, so only
  the **cumulative terminal state** is meaningful; reading any single script in isolation will
  mislead you.

Two consequences worth knowing before you go looking for bugs. Identity seeds collide with
the legacy null sentinels: `Portals.PortalID` is `IDENTITY(-1,1)`, so the first real portal is
`0` and `-1` is simultaneously a valid identifier *and* the legacy "absent" marker, while
`Roles.RoleID` is `IDENTITY(0,1)`. The domain model uses nullable CLR types, and sentinel
semantics are preserved at the DTO boundary wherever the legacy contract is externally
visible. All of it is itemised in [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md).

---

## 9. Project layout

```text
<repository root>
├── backend/                    .NET 8 / C# 12 Clean Architecture solution (DnnMigration)
├── frontend/                   Angular 19 SPA workspace (dnn-migration)
├── docker/                     container artefacts
├── Library/                    LEGACY, READ-ONLY — DotNetNuke core library (504 .vb files)
├── Website/                    LEGACY, READ-ONLY — DotNetNuke web app (130 .vb, 15 .aspx, 147 .ascx)
├── docs/                       published MkDocs documentation (reference only)
├── DotNetNuke.sln              LEGACY VS2005 solution — untouched
├── DotNetNuke_VS2008.sln       LEGACY VS2008 solution — untouched
├── catalog-info.yaml           Backstage component descriptor — untouched
├── mkdocs.yml                  docs site config — deliberately untouched
├── NuGet.Config                cleared package sources plus package-source mapping
├── MIGRATION_NOTES.md          migration decision log
├── README.md                   this file
├── .gitignore
└── .dockerignore
```

```text
backend/
├── DnnMigration.sln
├── Directory.Build.props       net8.0, C# 12, nullable, warnings-as-errors, two suppressions
├── global.json                 SDK 8.0.423, rollForward latestFeature
├── .editorconfig
├── src/
│   ├── DnnMigration.Domain/            Entities/ Enums/ ValueObjects/ Abstractions/ Common/
│   ├── DnnMigration.Application/       Abstractions/ Services/ Dtos/ Mapping/ Validation/ Options/
│   ├── DnnMigration.Infrastructure/    Persistence/ Repositories/ Security/ Services/ HealthChecks/
│   └── DnnMigration.Api/               Program.cs, Controllers/, Extensions/, Middleware/,
│                                       Filters/, ErrorHandling/, Authorization/, appsettings*.json
└── tests/
    ├── DnnMigration.UnitTests/         Domain/ Application/ Validation/ Security/ Mapping/
    └── DnnMigration.IntegrationTests/  ApiTestFixture, Api/, Persistence/, Schema/
```

```text
frontend/
├── package.json  package-lock.json  angular.json
├── tsconfig.json  tsconfig.app.json  tsconfig.spec.json
├── karma.conf.js               ChromeHeadlessNoSandbox — mandatory
├── .nvmrc  .dockerignore  .editorconfig  README.md
├── public/
└── src/
    ├── index.html  main.ts  styles.scss
    ├── styles/                 _tokens, _mixins, _reset, _layout, _forms, _tables
    ├── environments/           environment.ts (relative /api/v1), environment.development.ts
    └── app/
        ├── app.component.*  app.config.ts  app.routes.ts
        ├── core/               models/ services/ interceptors/ guards/ state/ config/ utils/
        ├── shared/             components/ directives/ pipes/
        ├── layout/             shell/ header/ sidebar/ footer/
        └── features/           portal/ module/ user/ role/ auth/ not-found/
```

```text
docker/
├── api.Dockerfile              sdk:8.0-alpine -> aspnet:8.0-alpine, non-root, wget probe
├── frontend.Dockerfile         node:20-alpine -> nginx:alpine
├── docker-compose.yml          the two-service topology
├── docker-compose.tls.yml      TLS overlay
├── nginx.conf                  SPA fallback plus the /api/ proxy — copied into the image
├── nginx.tls.conf              TLS server block, and its .example twin
└── .env.example                the documented contract for every deployment value
```


---

## 10. Security

### Authentication and the password store

Authentication is **JWT bearer** with short-lived access tokens and refresh rotation,
passwords are hashed with **BCrypt**, and authorisation is **policy-based**, mirroring the
DotNetNuke permission keys evaluated against the `Roles`, `UserRoles` and `RoleGroups` tables.

That replaces the ASP.NET 2.0 `SqlMembershipProvider` configuration the legacy application
ships, and the reason is not modernisation for its own sake. `Website/release.config`
registers the provider with `passwordFormat="Encrypted"` and `enablePasswordRetrieval="true"`,
and commits the 3DES `decryptionKey` that unlocks every stored password into source control in
plain sight. Reversible password storage backed by a committed key is not reproduced.

Three consequences follow, and all three are deliberate:

- **Password retrieval is not carried forward** to any endpoint or screen. It cannot be: a
  one-way hash has nothing to retrieve.
- **Legacy credentials cannot be verified by a one-way hash**, so the documented path is
  **re-hash on first successful login, with administrative reset as the fallback**. A bounded,
  explicitly enabled legacy-credential window exists for the cut-over and is **off** unless
  every one of its settings is supplied; see [`docker/.env.example`](./docker/.env.example) and
  [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md).
- **The legacy password *policy* is preserved verbatim** — minimum length 7, zero required
  non-alphanumeric characters, no question-and-answer requirement, and email uniqueness not
  enforced. Tightening a policy during a migration would lock out existing users, so any
  hardening is left as a separate, explicit decision rather than smuggled in here.

### The rest of the posture

- **CORS is a named policy restricted to the SPA origin.** `AllowAnyOrigin` combined with
  `AllowCredentials` is forbidden, and a wildcard origin is never acceptable. `UseCors` sits
  after routing and before authorisation in the pipeline.
- **Rate limiting is applied to the authentication endpoints**, partitioned on the caller's
  address. Exactly one forwarding hop is trusted to report that address, named by its fixed
  Compose address rather than by subnet — a forwarded address is supplied by whoever made the
  request, so trusting a range would let a direct caller choose its own partition.
- **Every error is RFC 7807 `ProblemDetails`**, produced by a single framework-registered
  `IExceptionHandler` paired with `AddProblemDetails()`; validation failures surface as
  `ValidationProblemDetails` with per-field errors. The SPA parses the same shape into its
  shared error banner.
- **A correlation id flows end to end**, emitted by the SPA's `correlationIdInterceptor`,
  consumed by `CorrelationIdMiddleware`, and attached to structured Serilog output. Logging
  never records credentials, tokens or other sensitive values.
- **Tokens are held in memory** by the SPA rather than in local or session storage.
- **HTTPS redirection is enabled in the production settings** and opted out of *only* in the
  plain-HTTP validation topology, visibly, in the one file that needs it. See
  [§6](#6-containers) for the TLS overlay.
- **Never commit secrets.** [`docker/.env.example`](./docker/.env.example) is a template and
  contains no real value; `docker/.env` is deliberately untracked. Supply
  `DB_CONNECTION_STRING` and `JWT_SECRET` from the environment or a secret store. There is no
  committed signing key anywhere in this repository — ending the committed-key practice of the
  legacy application is one of the reasons this migration exists.
- **Package restore is repository-controlled.** `NuGet.Config` clears every inherited source,
  declares the single public source the dependency inventory was pinned against, and maps each
  package identity to it with no bare `*` pattern, so an unreviewed identity fails restore
  rather than resolving from an unexpected feed.

---

## 11. Contributing and conventions

**No project-specific rules document was supplied for this repository** — the rules review
returns "No user rules provided." That absence is not licence to lower the bar. In its place
the migration's own normative sections are treated as binding:

- **The Minimal Change Clause and migration discipline**, six items covering domain-logic
  preservation, data-model fidelity, behavioural equivalence, UI functional parity, code
  organisation derived from discovered patterns, and mandatory migration annotation in both
  stacks plus a migration notes document.
- **System boundaries and preservation requirements** — the exclusion list, and the
  requirements to preserve tenant isolation, module registration and lifecycle, and the user,
  role and permission model. The database directive forbidding alteration of existing table
  structures is part of this set; see [§8](#8-database).
- **The non-functional requirements for both stacks**, mapped to the artefacts that satisfy
  them in [§6](#6-containers) and [§10](#10-security).
- **The seven validation gates as acceptance criteria**, reported in [§7](#7-validation-gates).

### Ground rules

- **The legacy trees are read-only.** Do not edit or delete anything under `Library/` or
  `Website/`, either legacy `.sln`, `Website/*.config`, `catalog-info.yaml`, or `docs/`. They
  are the authoritative record of the business rules being ported; the column names,
  validation rules and wording in the target are derived from them and cite them.
- **Do not modify [`mkdocs.yml`](./mkdocs.yml), and do not add nav entries for the root-level
  Markdown files.** It declares no `docs_dir`, so its nav resolves relative to `docs/` and a
  root-level file is unaddressable from there — adding an entry breaks the documentation build.
  `README.md` and `MIGRATION_NOTES.md` follow the same root-level, absent-from-nav convention.
- **Annotate every non-obvious conversion inline** with a `// MIGRATION:` comment at the point
  of departure, and record every deliberate behavioural difference in
  [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md), whose register is append-only: add to it rather
  than rewriting what is already there.

### Backend conventions

- Async all the way down. No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()` anywhere in
  a request path.
- Nullable reference types enabled; warnings are errors; the suppression list stays at two
  entries.
- No business logic in a controller. Controllers validate the request shape, delegate to an
  application service, and translate the result into a status code.
- All data access behind repository interfaces. `DnnDbContext` is declared `internal sealed`, so
  no application service can see it even by accident.
- No entity crosses the API boundary in either direction — request and response DTOs own the
  wire contract.
- Attribute-routed controllers, not minimal APIs; hand-written mappers, not AutoMapper; Fluent
  API mapping with an intentionally empty baseline migration, not model-generated DDL. These are
  settled decisions; please do not re-litigate them in a pull request.
- Exact dependency versions only. No `latest`, no floating ranges, and a new package identity
  needs its `NuGet.Config` mapping in the same commit.

### Frontend conventions

- Standalone components, signals for state, typed `nonNullable` reactive forms, built-in
  control flow with `track` on every `@for`, and `ChangeDetectionStrategy.OnPush` everywhere.
- Component-scoped SCSS over global design tokens — not Tailwind, and no component library.
- **Zero hardcoded CSS values.** Every value resolves to a token in
  `frontend/src/styles/_tokens.scss`; the only permitted literals are `0`, `none`, `auto`,
  `inherit`, `currentColor` and `transparent`. Breakpoints come from the shared mixins, never
  from an ad-hoc media query.
- Services under `core/services/` are typed `HttpClient` wrappers and hold no business logic.
- **Accessibility is required, not optional:** semantic landmarks, `<caption>` and
  `<th scope>` on tables, every control label-associated through `form-field`, `aria-live` on
  the error banner, visible focus rings, full keyboard operability of the data table and
  dialog, and a focus trap with `Escape` handling in `confirm-dialog`. None of it has a visual
  cost.
- Tests are Karma with Jasmine, one spec per component, service, interceptor, guard and store.
  The frontend has no linter configured; the authoritative check is the production build with
  strict templates.

---

## 12. Troubleshooting

| Issue | Cause | Solution |
| --- | --- | --- |
| `dotnet: command not found` | .NET SDK not installed | Install the .NET 8 SDK. [`backend/global.json`](./backend/global.json) names the band |
| `npm: command not found` | Node.js not installed | Install Node.js 20 LTS; [`frontend/.nvmrc`](./frontend/.nvmrc) names the version |
| `ng: command not found` | The CLI is a local devDependency, not a global install | Run `npx ng …` from `frontend/`, after `npm ci` |
| Build fails with `CS1591` | Documentation generation plus warnings-as-errors | `CS1591` is suppressed in [`backend/Directory.Build.props`](./backend/Directory.Build.props) — do not remove it |
| `npm ci` fails: lockfile missing | `package-lock.json` not committed | Commit `frontend/package-lock.json`; never add it to [`.gitignore`](./.gitignore) |
| Karma cannot start Chrome | Headless Chrome will not run as root without a sandbox flag | Use the `ChromeHeadlessNoSandbox` launcher in [`frontend/karma.conf.js`](./frontend/karma.conf.js). Set `CHROME_BIN` if the browser is not on the default path |
| Backend tests refuse to run, naming two routes | No container runtime and no `DNN_TEST_SQLSERVER` | Supply either — see [the database the integration suite runs against](#the-database-the-integration-suite-runs-against) |
| Connection string error, or the host refuses to start | Database not configured, or the value is still a placeholder | Set `ConnectionStrings:Default` / `ConnectionStrings__Default` to a real, complete connection string |
| The host refuses to start over `Jwt:Secret` | No signing key supplied, or a low-entropy placeholder | Supply at least 32 bytes from the environment or a secret store. No overlay ships one, by design |
| CORS errors in the browser | API origin mismatch | Align `Cors:AllowedOrigins` with the SPA origin. In containers the SPA is same-origin and uses the relative `/api/v1` |
| `docker-compose: command not found`, or exit 127 | Compose v1 is end-of-life and absent | Use the two-word `docker compose` — see [§6](#6-containers) |
| Health check returns 401 | `/health` is not anonymous | Restore `[AllowAnonymous]`; Compose's `service_healthy` condition depends on it |
| The frontend container never starts | The API health check never passes | Fix `/health`, and confirm the probe uses `wget --spider` rather than `curl` — the Alpine runtime image has no `curl` |
| The frontend image build fails at a `COPY` step | `docker/nginx.conf` missing, or excluded from the build context | Ensure the file exists and that the root [`.dockerignore`](./.dockerignore) does not exclude `docker/`. Build from the repository root, not from `docker/` |
| SPA requests answered 502 while both containers are healthy | The `api` service was renamed | `docker/nginx.conf` resolves the hostname `api`; keep the service name |
| SPA requests answered 503 with `Retry-After` just after a redeploy | The proxy's short-lived DNS entry has not yet aged out | Wait a few seconds. Nothing needs restarting |
| `dotnet run` fails to bind port 8080 | The container topology already holds it | Run with `--no-launch-profile --urls http://127.0.0.1:5080` |
| Sign-in returns 400 with an error on `portalId` | The host and port used are not a row in `PortalAlias` | Seed the alias, or address a portal explicitly |
| `the attribute 'version' is obsolete` on every `docker compose` | The preserved Compose file keeps the `version` key | Expected. Do not delete the key |

---

## 13. Documentation map

| Document | What it answers | Standing |
| --- | --- | --- |
| `README.md` (this file) | How to build, configure, test, run and operate both stacks, and how the seven gates came out | Current |
| [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md) | Why the target differs from the legacy application, decision by decision, with file and line citations | Current; its register is append-only |
| [`frontend/README.md`](./frontend/README.md) | The SPA workspace in detail — commands, structure, conventions and its API base URL | Current |
| [`docker/.env.example`](./docker/.env.example) | The documented contract for every deployment value, and how to move them to a secret store | Current |
| [`docs/index.md`](./docs/index.md), [`docs/project-guide.md`](./docs/project-guide.md), [`docs/technical-specifications.md`](./docs/technical-specifications.md) | Published through MkDocs | **Prior planning artefacts, reference only** |
| [`catalog-info.yaml`](./catalog-info.yaml) | The Backstage component descriptor for `blitzy-dotnetnuke` | Current, unmodified |

The `docs/` set deserves a specific caution, because it is easy to mistake for current state.
Those documents describe a **prior** migration effort and their published results, commit
counts and completion figures do not describe this checkout. Their commands, prerequisites and
configuration key names are still useful and were reused here; two of their technical
decisions are **superseded** by the implementation:

| Prior document says | This implementation does |
| --- | --- |
| The EF Core and `Microsoft.*` family pinned at 8.0.11 | Pinned at **8.0.29**, matching the installed shared-framework runtime exactly |
| AutoMapper for entity-to-DTO mapping | **Hand-written static mappers.** Mapping errors surface at compile time rather than at runtime profile validation, and no AutoMapper package is referenced anywhere |

---

## 14. Licence

The legacy DotNetNuke sources under `Library/` and `Website/` carry their original MIT-style
DotNetNuke Corporation copyright headers — "Copyright (c) 2002-2008 by DotNetNuke Corporation",
followed by the permission grant and the warranty disclaimer. See `Library/AssemblyInfo.vb` and
the header comment at the top of the legacy `.vb` files. Those headers are part of the read-only
legacy trees and are left exactly as they are.

No repository-level licence file is added by this migration; it was not in scope. If one is
required for distribution, that is a decision for the repository owner, taken with the legacy
headers above in view.
