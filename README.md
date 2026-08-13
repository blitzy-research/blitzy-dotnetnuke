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

Every **resource** endpoint lives under a version segment. All eleven attribute-routed
controllers carry `[Route("api/v{version:apiVersion}/…")]`, giving `/api/v1/portals`,
`/api/v1/modules`, `/api/v1/users`, `/api/v1/roles`, `/api/v1/permissions`, `/api/v1/tabs`,
`/api/v1/auth`, and the lookup surfaces `/api/v1/module-definitions`,
`/api/v1/profile-definitions` and `/api/v1/role-groups`. The segment is **mandatory** —
`AssumeDefaultVersionWhenUnspecified` is false, so `/api/portals` matches no route.

Two surfaces sit deliberately **outside** the version space, because neither is a resource a
client versions against: the three health views `/health`, `/health/live` and
`/health/ready`, and `/swagger`, the generated OpenAPI console. Both are described in
[§4](#4-backend), including which endpoints answer without a bearer token.

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
  `rollForward: latestFeature`, and every package that versions in step with the .NET 8
  shared framework is pinned to the matching **8.0.29** runtime band — the EF Core family,
  `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.AspNetCore.OpenApi` and
  `Microsoft.AspNetCore.Mvc.Testing`. Two `Microsoft.*` packages version independently of
  that framework and carry their own pins: **`Microsoft.Data.SqlClient` 5.2.3**, pinned
  explicitly because without it the SQL Server provider resolves 5.1.7 transitively, and
  **`Microsoft.NET.Test.Sdk` 17.14.1**.
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

Every key is overridable with .NET's standard **double-underscore** form, which is how the
containers supply them — a single underscore binds to nothing.

```json
{
  "ConnectionStrings": {
    "Default": "Server=sqlserver.example.com,1433;Database=DotNetNuke;User Id=REPLACE_ME;Password=REPLACE_ME;Encrypt=True"
  },
  "Jwt": {
    "Issuer": "DnnMigration",
    "Audience": "DnnMigration",
    "ExpirationMinutes": 60
  }
}
```

#### Where a value comes from, in order

Sources are listed lowest precedence first; **the last one that supplies a key wins.**

1. `appsettings.json` — the shipped defaults, and the only complete inventory of the shape.
2. `appsettings.{Environment}.json` — `Development` or `Production`, selected by
   `ASPNETCORE_ENVIRONMENT`.
3. User secrets — `Development` only.
4. Environment variables — what `docker-compose.yml` supplies.
5. Command-line arguments.
6. **A key-per-file secrets directory**, added last in `Program.cs` and therefore highest.
   The **file name is the key** (`/run/secrets/Jwt__Secret` supplies `Jwt:Secret`), the source
   is optional, and an absent directory contributes nothing and raises nothing. A mounted
   secret deliberately **beats** an environment variable of the same name, so migrating off
   environment delivery is one step rather than a cutover.

`Secrets:Directory` is the one key that cannot come from a secret file: it is read to decide
*where* that source points, so it must arrive from a source already loaded — an environment
variable or an `appsettings` entry.

#### The complete matrix

Every key the API reads is listed. **Secret** marks a value that must never be committed or
appear in a build log. **Production** states what a production deployment must do about it.

| Key | Environment form | Type | Default | Validation | Secret | Compose | Production |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `ConnectionStrings:Default` | `ConnectionStrings__Default` | string | *(blank)* | **Refuses to start** unless present, parseable, naming both a server and a database, naming a way to authenticate, and free of template placeholders. No refusal echoes any part of the value | **Yes** | `${DB_CONNECTION_STRING}` | **Required.** Replaces the legacy `SiteSqlServer` entry in `Website/release.config` |
| `Jwt:Secret` | `Jwt__Secret` | string | *(blank)* | **Refuses to start** below 32 UTF-8 bytes, below 8 distinct characters, or on a well-known placeholder | **Yes** | `${JWT_SECRET}` | **Required.** No overlay carries one in any environment, so there is no committed key to leak |
| `Jwt:Issuer` | `Jwt__Issuer` | string | `DnnMigration` | Non-blank, at most 256 characters | No | `${JWT_ISSUER:-DnnMigration}` | Optional |
| `Jwt:Audience` | `Jwt__Audience` | string | `DnnMigration` | Non-blank, at most 256 characters | No | `${JWT_AUDIENCE:-DnnMigration}` | Optional |
| `Jwt:ExpirationMinutes` | `Jwt__ExpirationMinutes` | int | `60` | 1–60 inclusive | No | `${JWT_EXPIRATION_MINUTES:-60}` | Optional. 60 preserves the legacy ticket timeout exactly |
| `Jwt:RefreshTokenExpirationDays` | `Jwt__RefreshTokenExpirationDays` | int | `7` | 1–30, and never above the absolute ceiling below | No | — | Optional |
| `Jwt:RefreshTokenAbsoluteExpirationDays` | `Jwt__RefreshTokenAbsoluteExpirationDays` | int | `30` | 1–30 inclusive | No | — | Optional. The family ceiling no rotation can extend |
| `RefreshTokenStore:Provider` | `RefreshTokenStore__Provider` | string | `InProcess` | `InProcess` (alias `InMemory`), `SqlServer` or `External` only; **refuses to start** on any other value, and on a declaration the registered store contradicts | No | `${REFRESH_TOKEN_STORE_PROVIDER:-InProcess}` | `SqlServer` selects the shared durable store this repository also ships — see [Operating the topology](#operating-the-topology) |
| `RefreshTokenStore:AcknowledgeSingleInstance` | `RefreshTokenStore__AcknowledgeSingleInstance` | bool | `false` | In **Production** on a store that is not replica-safe, `true` is **required** or the host refuses to start | No | `${REFRESH_TOKEN_STORE_SINGLE_INSTANCE:-true}` | Changes no behaviour. Records that exactly one instance runs. Compose sets it because it starts one `api` service |
| `RefreshTokenStore:ConnectionString` | `RefreshTokenStore__ConnectionString` | string | — | Required by `SqlServer`. Must name its catalogue explicitly, must not be a system catalogue, and its catalogue **name** must differ from `ConnectionStrings:Default`'s | Only for `SqlServer` | `${REFRESH_TOKEN_STORE_CONNECTION:-}` | The table is provisioned out of band by [`docker/sql/refresh-token-store.sql`](./docker/sql/refresh-token-store.sql) |
| `RefreshTokenStore:Schema` | `RefreshTokenStore__Schema` | string | `dbo` | A plain SQL identifier | No | — | Must match what the provisioning script creates |
| `RefreshTokenStore:TableName` | `RefreshTokenStore__TableName` | string | `DnnMigrationRefreshTokens` | A plain SQL identifier | No | — | Must match what the provisioning script creates |
| `RefreshTokenStore:CommandTimeoutSeconds` | `RefreshTokenStore__CommandTimeoutSeconds` | int | `15` | 1–600 inclusive | No | — | Optional; applies to the `SqlServer` store only |
| `RefreshTokenStore:MaximumTrackedTokens` | `RefreshTokenStore__MaximumTrackedTokens` | int | `100000` | 1,000–1,000,000 inclusive | No | `${REFRESH_TOKEN_MAX_TRACKED:-100000}` | Optional. Raise it only if the `refresh-token-store` probe reports `Degraded` |
| `RefreshTokenStore:ConcurrentUseGraceSeconds` | `RefreshTokenStore__ConcurrentUseGraceSeconds` | int | `5` | 0–60 inclusive; `0` disables the grace | No | `${REFRESH_TOKEN_GRACE_SECONDS:-5}` | Optional |
| `PasswordPolicy:MinRequiredPasswordLength` | `PasswordPolicy__MinRequiredPasswordLength` | int | `7` | At least 7 — **the measured legacy minimum, and lowering it is refused** — and at most the 256-byte credential ceiling | No | — | Optional |
| `PasswordPolicy:MinRequiredNonAlphanumericCharacters` | `PasswordPolicy__…` | int | `0` | Not negative, and not above the required length | No | — | Optional. `0` is the measured legacy value |
| `PasswordPolicy:RequiresQuestionAndAnswer` | `PasswordPolicy__…` | bool | `false` | **`true` is refused**: no question-and-answer flow was migrated, so enabling it would advertise a step that cannot be completed | No | — | Leave `false` |
| `PasswordPolicy:RequiresUniqueEmail` | `PasswordPolicy__…` | bool | `false` | **`true` is refused**: the legacy installation permitted duplicate addresses, and existing rows may already hold them | No | — | Leave `false` |
| `PasswordPolicy:PasswordResetEnabled` | `PasswordPolicy__…` | bool | `true` | — | No | — | Optional |
| `PasswordPolicy:PasswordStrengthRegularExpression` | `PasswordPolicy__…` | string | *(blank)* | Must compile, and at most 512 characters. Blank disables the rule | No | — | Optional |
| `PasswordPolicy:MaxInvalidPasswordAttempts` | `PasswordPolicy__…` | int | `5` | Positive | No | — | Optional |
| `PasswordPolicy:PasswordAttemptWindowMinutes` | `PasswordPolicy__…` | int | `10` | Positive | No | — | Optional |
| `LegacyCredentials:Enabled` | `LegacyCredentials__Enabled` | bool | `false` | Must parse as a boolean. When `true`, a deadline and a key are required | No | `${LEGACY_CREDENTIALS_ENABLED:-false}` | **Leave `false`** unless a bounded legacy-credential migration window is genuinely open |
| `LegacyCredentials:EnabledUntilUtc` | `LegacyCredentials__EnabledUntilUtc` | ISO-8601 instant | `null` | Round-trip parseable with a **zero** offset; required while the switch is on | No | `${LEGACY_CREDENTIALS_ENABLED_UNTIL_UTC:-}` | Required only inside the window |
| `LegacyCredentials:DecryptionKey` | `LegacyCredentials__DecryptionKey` | hex string | *(blank)* | Validated for the named algorithm; **no error echoes any part of it** | **Yes** | `${LEGACY_CREDENTIALS_DECRYPTION_KEY:-}` | Required only inside the window; remove it afterwards |
| `LegacyCredentials:DecryptionAlgorithm` | `LegacyCredentials__…` | string | `3DES` | A supported algorithm name | No | — | Leave at the legacy value |
| `LegacyCredentials:ValidationAlgorithm` | `LegacyCredentials__…` | string | `SHA1` | A supported algorithm name | No | — | Leave at the legacy value |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0` | string array | `["https://localhost:4443"]`; `Development` overlay uses `http://localhost:4200` | Each entry must be an exact scheme-and-host origin — **a wildcard, an upper-case scheme, a scheme-less value, a trailing path and a documentation host are each refused at start-up** | No | `${FRONTEND_ORIGIN:-http://localhost:4200}` (base) / derived from `DNN_PUBLIC_HOST` (TLS overlay) | Set it to the SPA's own origin. The containerised SPA is same-origin through the proxy and needs none of this |
| `RateLimiting:Authentication:PermitLimit` | `RateLimiting__Authentication__PermitLimit` | int | `30` | Positive; a non-positive value **stops the host** rather than refusing every sign-in | No | — | Optional |
| `RateLimiting:Authentication:WindowSeconds` | `RateLimiting__Authentication__WindowSeconds` | int | `60` | Positive | No | — | Optional |
| `Caching:PerformanceMultiplier` | `Caching__PerformanceMultiplier` | int | `3` | 0–1440 inclusive; `0` disables caching outright | No | — | Optional. Multiplies every cache lifetime; the legacy setting accepted a wider range than its four named levels, so the range is deliberately not narrowed |
| `Portal:AdminTemplateFileName` | `Portal__AdminTemplateFileName` | string | `admin.template` | Non-blank, at most 260 characters, no path separator and no `..` segment | No | — | Optional |
| `Portal:HomeDirectoryFormat` | `Portal__HomeDirectoryFormat` | string | `Portals/{0}` | Must contain the `{0}` portal placeholder, at most 260 characters, no `..` segment | No | — | Optional |
| `Swagger:Enabled` | `Swagger__Enabled` | bool | `false` | — | No | — | **Leave `false`.** Swagger is mounted unconditionally in `Development`; outside it, this key is the only way it appears |
| `Https:RedirectEnabled` | `Https__RedirectEnabled` | bool | `false` in `appsettings.json` and the `Development` overlay, **`true`** in the `Production` overlay | When enabled, the port below must be present and 1–65535 | No | `Https__RedirectEnabled=false` in the base topology | **`true` only when this process terminates TLS itself.** The base Compose topology sets it to `false` because nginx fronts the API over plain HTTP inside the network; leaving it on there answers every proxied call with a redirect the browser cannot follow |
| `Https:RedirectPort` | `Https__RedirectPort` | int | `443` in `appsettings.json`, repeated by the `Production` overlay; `4443` in `Development` | 1–65535 when the redirect is enabled | No | — | Required when the redirect is enabled |
| `Proxy:KnownProxies` | `Proxy__KnownProxies__0` | string array of IP addresses | `[]` | Each entry must parse as an IP address | No | `${TRUSTED_PROXY_ADDRESS:-172.28.0.10}` — the frontend container's fixed address on the Compose network | Required if a reverse proxy forwards `X-Forwarded-For`/`X-Forwarded-Proto`; without it those headers are ignored, which is the safe default |
| `Proxy:KnownNetworks` | `Proxy__KnownNetworks__0` | string array of CIDR ranges | `[]` | Each entry must parse as `prefix/length` | No | — | Alternative to the above for a proxy whose address is not fixed |
| `AllowedHosts` | `AllowedHosts` | `;`-separated string | `*` | Each entry is checked at start-up: **a host reserved for documentation (`example.com`, the `.example` TLD) or an editing marker (`changeme`, `yourdomain`) refuses to start.** `*` is deliberately permitted so the image runs unconfigured | No | Derived from `DNN_PUBLIC_HOST` by the TLS overlay | Set it to the deployment's own public host plus the loopback names the container probe uses |
| `Secrets:Directory` | `Secrets__Directory` | string | `/run/secrets` | A blank value means "not configured" rather than the filesystem root. The source is optional, so a missing directory is silent | No | — | Optional. Point it at a projected-volume path if your platform mounts elsewhere |
| `Serilog:*` | `Serilog__MinimumLevel__Default`, `Serilog__MinimumLevel__Override__<Category>` | Serilog configuration | Console sink, compact JSON, `Information`, with `Microsoft.AspNetCore` and the EF Core command channels held at `Warning` | Read by Serilog itself | No | — | Do **not** relax `Microsoft.AspNetCore` below `Warning`: the framework's request entries embed the raw query string, and this API has query-backed reads whose natural search term is an email address |
| `ASPNETCORE_ENVIRONMENT` | *(host variable)* | string | `Production` | — | No | `ASPNETCORE_ENVIRONMENT=Production` | Selects the overlay, and `Development` alone mounts Swagger and user secrets |
| `ASPNETCORE_URLS` | *(host variable)* | string | `http://+:8080` in the image | — | No | Set in `api.Dockerfile` | Change only alongside the published port and the container health check |

**Never commit a real secret.** The three keys marked **Secret** come from environment
variables or a mounted secret file in every environment, development included. Every
credential shown in this file is an obvious placeholder.

`appsettings.json` is the authoritative inventory of the shape, and the matrix is reconciled
against it in both directions: every configuration key above appears there with its shipped
default, and every key it declares appears above. The two host variables at the end of the
table are the only exceptions — `ASPNETCORE_ENVIRONMENT` and `ASPNETCORE_URLS` are read by the
host itself, not from a settings file. So a deployment can diff its own configuration against
`appsettings.json` and account for every difference.

**The connection strings in this file validate the server's certificate, and that is
deliberate.** `Microsoft.Data.SqlClient` has defaulted to `Encrypt=True` since its 4.0
release, so the driver always negotiates TLS; what `TrustServerCertificate=True` adds is the
silent *disabling* of the validation that makes the encryption worth having. Exactly one
copyable line in this file carries it — the explicitly labelled local-development line below —
and it belongs nowhere else:

```bash
# LOCAL DEVELOPMENT ONLY — a SQL Server presenting a self-signed certificate. Never deploy
# this shape: install the server certificate's issuer into the client's trust store, or issue
# the server a certificate from an authority the client already trusts, and drop the override.
ConnectionStrings__Default='Server=localhost,1433;Database=DotNetNuke;User Id=REPLACE_ME;Password=REPLACE_ME;TrustServerCertificate=True;Encrypt=True'
```

No tracked configuration file in this repository carries the override:
`appsettings.json` ships the connection string empty and neither environment overlay declares
one at all.

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
DNN_TEST_SQLSERVER='Server=sqlserver.example.com,1433;Database=master;User Id=REPLACE_ME;Password=REPLACE_ME;Encrypt=True' \
  dotnet test --configuration Release --filter "Category=Integration"
```

Against a local server presenting a self-signed certificate, and only there, add
`TrustServerCertificate=True` — see the note in [Configure](#configure) for why that switch
belongs in a labelled local line and nowhere else.

The suite fails closed rather than degrading when it can reach neither, and the failure names
both routes. Each run creates a database whose name carries a fresh identifier, so parallel
checkouts sharing one server cannot collide.

### Run

```bash
cd backend
ConnectionStrings__Default='Server=sqlserver.example.com,1433;Database=DotNetNuke;User Id=REPLACE_ME;Password=REPLACE_ME;Encrypt=True' \
Jwt__Secret="$(openssl rand -base64 48)" \
  dotnet run --project src/DnnMigration.Api
```

`Properties/launchSettings.json` is what makes that command short: its single profile sets
`ASPNETCORE_ENVIRONMENT=Development` and `applicationUrl=http://localhost:8080`.

Which endpoints answer without a bearer token, precisely:

| Endpoint | Credential |
| --- | --- |
| `GET /health`, `GET /health/live`, `GET /health/ready` | **None.** Mapped with `.AllowAnonymous()`, because an orchestrator's probe carries none |
| `POST /api/v1/auth/login`, `/auth/refresh`, `/auth/logout` | **None.** Each carries `[AllowAnonymous]` — a caller has no token yet, or is discarding the one it has. All three are rate-limited |
| `GET /api/v1/auth/me` and **every other** `/api/v1/…` endpoint | Bearer token, plus the endpoint's authorisation policy |
| `/swagger` | See below |

`/swagger` is mounted **unconditionally in Development**. Outside Development it is **off**
unless a deployment sets `Swagger:Enabled` to true, and when it is on there it is served
**only to an authenticated caller** — the console is branched behind an
`IsAuthenticated` predicate rather than published anonymously.

If port 8080 is already taken — by the container topology, for instance — bypass the profile.
Bypassing it discards the profile's environment as well as its URL, so the command has to
supply everything itself; without `ASPNETCORE_ENVIRONMENT` the host starts in **Production**,
where `appsettings.Production.json` turns HTTPS redirection on. Measured: the three health
views still answer `200`, and every other path — including `/swagger` — is answered `308` to
`https://…`, which this plain-HTTP listener cannot satisfy.

```bash
cd backend
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Default='Server=sqlserver.example.com,1433;Database=DotNetNuke;User Id=REPLACE_ME;Password=REPLACE_ME;Encrypt=True' \
Jwt__Secret="$(openssl rand -base64 48)" \
  dotnet run --project src/DnnMigration.Api --configuration Release \
  --no-launch-profile --urls http://127.0.0.1:5080
```

To run that same command in `Production` deliberately, either serve HTTPS or set
`Https__RedirectEnabled=false` for the run — which is exactly what the plain-HTTP container
topology does, visibly, in [`docker/docker-compose.yml`](./docker/docker-compose.yml).

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

The split is deliberate, and **both [`docker/api.Dockerfile`](./docker/api.Dockerfile) and
[`docker/docker-compose.yml`](./docker/docker-compose.yml) probe `/health/ready`** — not
`/health`. They used to probe `/health`, which is the process-only view: the container was
therefore reported healthy while SQL Server was unreachable, and the front-end service started
in front of an API that could not answer a single membership-backed request. `service_healthy`
is a statement about whether traffic may be sent, so it is tested against the view that
exercises the dependency.

The consequence is stated rather than hidden: the compose topology declares no database
service, so a deployment whose store is genuinely absent will see the API container marked
unhealthy and the front end held back. That is the correct report — the API cannot serve — and
it is why `/health/live` exists alongside it: point a restart-or-not probe there, so an
orchestrator does not recycle a healthy process merely because its dependency is down.

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
--browsers=ChromeHeadless --code-coverage` go through `package.json`'s `build` and `test`
scripts and are equivalent to the last two commands above.

[`docker/frontend.Dockerfile`](./docker/frontend.Dockerfile) runs **the install and the
production build only** — `npm ci` followed by `npm run build -- --configuration production`.
It runs no tests: the image build is not a test gate, and the suite is a separate validation
step ([§7](#7-validation-gates), gate 4).

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
curl -f http://localhost:8080/health/ready  # 200, the readiness view the container probes
curl -f http://localhost:8080/health    # 200, the anonymous process-only view
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
| Base images | **All four `FROM` lines pin a digest as well as the readable tag**, so one commit builds one image rather than whatever the moving tag points at that week. What was verified inside each pinned digest, and how a digest is refreshed, is in [§10](#10-security) | |
| Runs as | a **non-root** user created with `adduser -D -u 1000 appuser` | **uid 101 (`nginx`), set by `user: "101:101"` in the Compose file** — there is no root master process at all, which is why the container needs no capabilities whatsoever. See [runtime hardening](#runtime-hardening) |
| Runtime hardening | `no-new-privileges`, `cap_drop: [ALL]`, `read_only` root filesystem, and **no `tmpfs`** — the API writes no files | `no-new-privileges`, `cap_drop: [ALL]`, `read_only` root filesystem, plus two ephemeral `tmpfs` mounts (`/run`, `/var/cache/nginx`) owned by uid 101 |
| Listens on | `ASPNETCORE_URLS=http://+:8080`, `EXPOSE 8080` | `listen 80`, `EXPOSE 80` |
| Published as | **`127.0.0.1:8080:8080`** — loopback only, so the cleartext listener that carries credentials and bearer tokens is reachable from this host and the Compose network and nowhere else | **`127.0.0.1:4200:80`** — loopback only for the same reason and more sharply, since this port proxies `/api/`; the TLS overlay is the only topology that publishes a routable port |
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
  becomes healthy, and the front-end container never starts. The three health views are
  endpoint mappings rather than controller actions, so their exemption is the
  `.AllowAnonymous()` call on each `MapHealthChecks` registration in
  `Api/Extensions/ApplicationBuilderExtensions.cs` — keep it there.
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
gate probes `http://localhost:8080/health/ready` - the view the image and Compose both probe,
which is the one that exercises the database dependency - and `http://localhost:4200` directly.
It is a validation and demonstration topology, **not a public-facing one** — and it is
*constrained* to that role rather than merely described as having it: **both** published ports
are bound to `127.0.0.1`. The front end's binding matters most, because it proxies `/api/`, so a
browser that loads the sign-in screen from it posts a credential and then sends a bearer token
through it; published on every interface, that whole exchange was cleartext on any network this
host can route to, and the API's own loopback binding did nothing to prevent it.

To face the internet, add the TLS overlay. It terminates TLS at the front end, publishes `443`
(with `80` redirecting to it), withdraws the API's published port (`ports: !reset []`, so the API
is reachable only over the Compose network) and switches HTTPS redirection back on. **Two
variables are required and no tracked file is edited:**

```bash
DNN_PUBLIC_HOST=admin.acme.test \
TLS_CERTIFICATE_DIRECTORY=/etc/letsencrypt/live/admin.acme.test \
docker compose -f docker/docker-compose.yml -f docker/docker-compose.tls.yml \
  --env-file docker/.env up -d
```

| Variable | What it drives |
| --- | --- |
| `DNN_PUBLIC_HOST` | **One input, four consumers**: both `server_name` directives in the rendered TLS server block, the API's `AllowedHosts` (as `<host>;localhost;127.0.0.1`), the API's permitted browser origin (as `https://<host>`), and the name the certificate must be valid for |
| `TLS_CERTIFICATE_DIRECTORY` | A host directory holding `fullchain.pem` and `privkey.pem` for that name, mounted read-only |

Both are **required with no fallback**, so `docker compose` refuses to create a container
while either is unset or blank. The public host name is also checked by the API: a name
reserved for documentation — `example.com`, `example.net`, `example.org`, the `.example`
top-level domain, or an unreplaced marker such as `changeme` — makes the API **refuse to
start**, and because the front end waits on `condition: service_healthy` the deployment then
never serves at all. Reserved *test* domains are deliberately accepted, so `admin.acme.test`
and an internal certificate authority work as they should.

The server block itself is [`docker/nginx.tls.conf.template`](./docker/nginx.tls.conf.template),
rendered by the nginx image's own entrypoint into the directory
[`docker/nginx.conf`](./docker/nginx.conf) already scans (`include /etc/nginx/tls/*.conf`).
It serves the application by including the same three snippets the plain-HTTP server includes,
so the public listener cannot behave differently from the one the gate probes. Earlier
revisions hard-coded `dnn.example.com` in that block and in the API's host filter while
parameterising only the browser origin — a deployment that set its origin got a redirect its
browsers never matched, a certificate that matched no server name, and an API that answered
`400` to all of its own traffic.

### Operating the topology

- **Redeploying the API alone is supported, on both listeners.** `docker compose … up -d
  --force-recreate api` may place the API container on a different address, and the front end
  resolves the API per request rather than once at start-up, so it follows the new address by
  itself. Calls made in the first few seconds may be answered `503` with `Retry-After: 5` and
  an RFC 7807 body while the short-lived DNS entry ages out; nothing needs restarting. The
  front end also starts and keeps serving the application while the API is absent — only
  `/api/` calls fail, and they fail with a problem document rather than an HTML error page.
  This holds for the **TLS listener as well as the plain-HTTP one**, because both include the
  same [`docker/api-proxy.conf`](./docker/api-proxy.conf); it did not before, and that was a
  defect rather than a limitation — the public listener carried a copy of the original
  four-line proxy block and therefore none of these protections. The three shared snippets are
  the whole mechanism:

  | Snippet | What it owns | Included by |
  | --- | --- | --- |
  | [`docker/api-proxy.conf`](./docker/api-proxy.conf) | the API location — a regex matching `/api/` **and one optional tenant segment before it**, so a child portal addressed at `host/child` reaches the API too — per-request upstream resolution, the raw request target, the 6,356,992-byte import allowance, the six forwarded headers, and the `@api_unavailable` RFC 7807 answer | both servers |
  | [`docker/spa-static.conf`](./docker/spa-static.conf) | the immutable hashed-asset policy and the SPA deep-link fallback | both servers |
  | [`docker/security-headers.conf`](./docker/security-headers.conf) | the nine response headers, including the content security policy and TLS-only HSTS | both servers, and every location that sets a `Cache-Control` of its own |
- **Addressing a child portal beneath a path segment works end to end, and needs the alias
  row to exist.** <a id="addressing-a-child-portal-beneath-a-path-segment"></a>The legacy
  product let a child portal be reached at `domain/segment` — the signup screen composed and
  stored exactly that (`Website/admin/Portal/Signup.ascx.vb` L232-L236) — and that address
  shape is preserved. Externally the URLs look like this:

  | External URL | Which tenant answers |
  | --- | --- |
  | `http://localhost:4200/` | the tenant whose alias is `localhost:4200` |
  | `http://localhost:4200/portals` | the same tenant; `portals` is one of the console's own screens |
  | `http://localhost:4200/acme/` | the tenant whose alias is `localhost:4200/acme` |
  | `http://localhost:4200/acme/users/1/profile` | the same child tenant, deep-linked |
  | `http://localhost:4200/acme/api/v1/roles` | the child tenant's roles — the address the SPA composes for itself |

  Three hops each have to carry the segment, and each fails silently on its own:

  1. **The browser** puts it back onto every request. One built bundle is served to every
     tenant and the configured API base is the relative `/api/v1`, so the prefix cannot be a
     build-time value; it is derived at run time from the address the document was served at,
     by [`frontend/src/app/core/config/tenant-path.ts`](./frontend/src/app/core/config/tenant-path.ts),
     and composed in the one place that builds URLs. The same value is supplied as the
     router's `APP_BASE_HREF`, so in-application links keep the prefix. The document's own
     `<base href="/">` stays at the root, because the hashed assets are served from the
     server root for every tenant.
  2. **The proxy** forwards it. [`docker/api-proxy.conf`](./docker/api-proxy.conf) matches
     `^(?:/[^/]+)?/api/` and passes the caller's own request target through unchanged — the
     segment is *not* stripped, because the API needs it to identify the tenant.
  3. **The API** resolves and rebases it. `TenantPathBaseMiddleware` resolves the tenant from
     host + path before routing, then moves the segment into the request's path base so the
     routes still match and a `Location` header still names the child.

  Two operational requirements follow. The alias must be stored exactly as `host[:port]/segment`,
  matched whole-segment and case-insensitively — a substring is not a match, so `/acmeish/…`
  resolves nothing. And a child portal must be created through `POST /api/v1/portals`, not by
  inserting a `Portals` row: resolution refuses a portal that designates no administrator
  account, no administrator role or no registered-user role.

  **The addressable alias contract, in one place.**
  [`backend/src/DnnMigration.Domain/Common/PortalAliasTopology.cs`](./backend/src/DnnMigration.Domain/Common/PortalAliasTopology.cs)
  is the single definition, and five components mirror it: the alias write paths, the request
  pipeline that resolves an arriving address, the browser's prefix detection, the alias
  administration screen and the proxy location above. It says three things.

  | Clause | Rule |
  |---|---|
  | Depth | **One** path segment beneath the authority — what the legacy signup screen composed and what the proxy can deliver |
  | Vocabulary | ASCII letters, digits, hyphen and underscore. **No dot**, so a stored segment can never look like a served file (`/favicon.ico`, `/robots.txt`, `/main-ABCD.js` are never tenants) and `.`/`..` cannot be spelled |
  | Reserved | `login`, `modules`, `portals`, `role-groups`, `roles`, `settings`, `users` (the console's own routes) and `api`, `health`, `openapi`, `swagger` (the roots the API answers) |

  Both alias write paths and the portal creation contract **refuse** anything outside that
  contract, and the resolver **fails closed**: when the first path segment could name a tenant
  and matches no stored alias exactly, the request resolves *no* tenant rather than falling back
  to the bare host. That fallback was a tenant-isolation defect — a request for
  `/child/api/v1/…` whose `child` alias did not exist was answered with the **parent's** data
  under a child-looking address — and reserving `api` closes a denial-of-service besides: one
  stored alias of `host/api` used to match on every API request to the bare host and take the
  whole API surface away from it.

  An alias stored **before** this contract was enforced — one carrying two segments, a dot, or a
  reserved word — is no longer reachable. That is reported rather than left silent:
  `PortalAliasConformanceMonitor` scans the stored aliases shortly after start-up and logs a
  warning naming each offending row by its surrogate key (never its value, which is tenant
  data). Retire such an alias, or replace it with one inside the contract. Every divergence from
  the legacy behaviour here is recorded in [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md).
- **On the default store, treat an API restart as a sign-out.** Refresh-token state is held in
  the API process, because the DotNetNuke schema this API maps onto is immutable and owns no
  table for it. A restart or a redeploy therefore invalidates every refresh token: access tokens
  already issued stay valid until they expire, and after that each signed-in user authenticates
  again. The front end handles this cleanly — a rejected refresh returns the user to the
  sign-in screen — but the effect is visible, so a redeploy is best scheduled accordingly.
  **Selecting `RefreshTokenStore:Provider=SqlServer` removes this entirely**: families then live
  in a catalogue of their own, survive any restart and are observed identically by every
  replica.
- **On the default store, run exactly one API instance — and in Production you must say so.** A
  second replica without sticky routing would reject refresh tokens issued by the first, and its
  in-memory cache would keep serving its own projection of a row the other replica had just
  changed until that entry expired. **In `Production`, a store that is not replica-safe now
  refuses to start unless `RefreshTokenStore:AcknowledgeSingleInstance` is `true`.** The
  acknowledgement changes no behaviour and grants no capability; its only function is to make the
  claim explicit, because scaling out on the process-local store previously needed no code change
  and no configuration change and produced no warning at all. Set it only where a single instance
  is genuinely enforced by the topology — `docker/docker-compose.yml` sets it because it starts
  exactly one `api` service. Non-production environments are exempt, deliberately: an
  acknowledgement demanded everywhere is one that gets set reflexively and records nothing.

  Two answers satisfy the requirement, and the second is better for any deployment that really
  has replicas: select `SqlServer`, or supply your own store. Both the token store and the cache
  are properties of the *shipped implementations* rather than of the contracts —
  `IRefreshTokenStore` and `ICacheService` are public contracts, the container resolves the
  **last** registration of a service, and both consumers of the token store depend on the
  contract rather than the class — so a deployment that needs cross-process continuity calls
  `AddInfrastructure(...)`, registers its own implementation after it, and sets
  `RefreshTokenStore:Provider` to `External`; no file in this repository changes. The invariant
  keys on the contract's own `IsAuthoritativeAcrossReplicas`, so a replacement that is genuinely
  shared satisfies it without an acknowledgement and one that is replica-local is held to the
  same standard as the shipped default. The seam is covered by
  [`RefreshTokenStoreTopologyTests`](./backend/tests/DnnMigration.IntegrationTests/Infrastructure/RefreshTokenStoreTopologyTests.cs)
  rather than merely asserted here.
- **The store you are running is declared, enforced and reported.** Three mechanisms exist so
  that the limitation above can never be one a deployment *believes* it has escaped:

  | Mechanism | What it does |
  | --- | --- |
  | `RefreshTokenStore:Provider` (`InProcess` \| `SqlServer` \| `External`) | Makes the choice of store an explicit, validated setting instead of a default nobody chose. An unrecognised name is refused rather than treated as `InProcess` |
  | Start-up topology check | Compares the declaration against the store the container actually resolves and **refuses to start** in either direction — declaring `External` or `SqlServer` with the process-local store resolved, or overriding the store while still declaring `InProcess`. It also enforces the **Production single-instance invariant** described above. Wired in `Program.cs` immediately after the host is built, and covered by [`RefreshTokenStoreTopologyContractTests`](./backend/tests/DnnMigration.IntegrationTests/Api/RefreshTokenStoreTopologyContractTests.cs) |
  | `RefreshTokenStore:AcknowledgeSingleInstance` | Turns "this deployment runs one instance" from an assumption into a recorded claim, required in `Production` whenever the active store is not replica-safe |
  | `refresh-token-store` health probe | Reports the active store, whether it is replica-safe, whether it survives a restart, and how full it is. For the `SqlServer` store it **probes the catalogue**, distinguishing "unreachable" from "reachable but not provisioned" and naming the provisioning script for the latter. It is on the **liveness** view with a `Degraded` failure status, so it never holds a starting container back and never withdraws a serving instance; its finding reaches an operator through the structured health-report log entry, never through the anonymous response body |

  Reaching the tracked-generation ceiling is the one condition to act on: the store then
  retires the **oldest** refresh families first — whole families, never part of one, because a
  half-tracked family can no longer detect the replay it exists to detect — so their holders sign
  in again while every request continues to be served. Both shipped stores behave this way, on
  issue **and** on rotation. The probe reports `Degraded` with the usage and the two remedies —
  raise `RefreshTokenStore:MaximumTrackedTokens`, or move to a shared store.
- **The shared store this repository ships, and why it is opt-in.** Selecting
  `RefreshTokenStore:Provider=SqlServer` holds refresh families in **one table in a catalogue of
  its own**, so they survive a restart and every replica observes the same revocations. It is
  defaulted off because the default configuration must constitute a working deployment on its own,
  and this one needs a catalogue an operator provisions. Three things follow, and each is enforced
  rather than advised:

  | Requirement | Why, and what happens otherwise |
  | --- | --- |
  | The catalogue is **not** the DotNetNuke database | AAP rule T4 makes that schema immutable. The host refuses to start unless the session connection string names its catalogue explicitly, names no system catalogue (`master`, `model`, `msdb`, `tempdb`), and names a catalogue whose **name** differs from `ConnectionStrings:Default`'s. The comparison is by catalogue name rather than by whole connection string on purpose: a host is spellable as `localhost`, `127.0.0.1`, `(local)`, a machine name or a named instance, so a rule that compared hosts could be bypassed by writing one differently |
  | The table is provisioned **out of band** | Run [`docker/sql/refresh-token-store.sql`](./docker/sql/refresh-token-store.sql) against the catalogue before starting the API. The running application never creates or alters it — it probes for the table and reports session operations as unavailable, and says so on its health probe, while it is absent. Schema authorship is a deployment step, and the API's principal needs only `SELECT`, `INSERT`, `UPDATE` and `DELETE` on that one table |
  | The API's principal is **not** the provisioning principal | The script documents the least-privilege grant. Do not reuse `sa`, and do not reuse the login that ran the script |

  A deployment that wants neither the shipped shared store nor the process-local one supplies its
  own behind `IRefreshTokenStore` and declares `External`. The plan's constraints that shaped this
  arrangement — no object in the DotNetNuke schema (AAP rule T4), no distributed-cache client in
  the frozen dependency inventory (AAP 0.6), and a two-service Compose topology reproduced
  verbatim (AAP 0.9.3) — are recorded with their citations in
  [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md).
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

### Runtime hardening

<a id="runtime-hardening"></a>Both services, in **both** Compose files, drop every Linux
capability, refuse privilege escalation, and run with a read-only root filesystem. Every value
was measured against the images rather than copied from a checklist, and two consequences are
operator-visible rather than internal — read these before deploying.

| Setting | API | Frontend | Why this value |
| --- | --- | --- | --- |
| `security_opt: [no-new-privileges:true]` | yes | yes | a `setuid` binary in either image cannot raise privilege |
| `cap_drop: [ALL]` | yes | yes | the API needs none, and nginx needs none **once it runs as uid 101**. `CHOWN` and `SETUID` were only ever required by a root master handing its temp directories to a worker, and `NET_BIND_SERVICE` is not required because Docker sets `net.ipv4.ip_unprivileged_port_start=0` |
| `read_only: true` | yes | yes | neither service writes to its image |
| `tmpfs` | **none** | `/run`, `/var/cache/nginx` | nginx needs writable temp and PID paths; the API writes no files at all. The mount is `/run`, not `/var/run` — `nginx.conf` declares no `pid` directive so the compiled default applies, and `/var/run` is a symlink. Log paths need no mount because the image symlinks them to stdout and stderr |

- **⚠ TLS deployments: the private key must be readable by uid 101.** This is a breaking change
  for an existing TLS deployment. A root-owned `0600` key now stops the container with
  `cannot load certificate key … Permission denied`. Fix it with `chown 101:101` and
  `chmod 640` on the key — **not** by making it world-readable. If the key is managed by
  Let's Encrypt, apply the ownership to the file in the `archive` directory that the `live`
  symlink points at, and reapply it from a renewal hook, because renewal writes a new file.
  The TLS overlay also mounts its own `tmpfs` at `/etc/nginx/tls`: without it the image's
  template step fails to render, and — this is the part worth knowing — **the container then
  starts anyway, serving plain HTTP with no TLS block while reporting healthy.**
- **The API has no writable `/tmp`, so the .NET diagnostics socket is absent.** `dotnet-counters`
  and `dotnet-dump` cannot attach to a running API container. This is the accepted cost of
  `read_only` with no `tmpfs`; add a `/tmp` `tmpfs` temporarily if you need to attach, and
  remove it afterwards.


---

## 7. Validation gates

Seven gates are the acceptance criteria for this migration, and this section is the validation
report for them. It is in two halves: the commands **as specified**, then the commands **as
executed**, with one status and one date per gate.

**The specification, reproduced verbatim.** This block is the frozen input, kept for
traceability. Two of its lines cannot run on a current toolchain — see the notes under the
matrix — so do not copy from here.

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

**The supported equivalents, which is what to run.** Every line below is copy-pasteable from
the repository root and is what produced the evidence in the matrix. The differences from the
block above are mechanical and are listed after it: a working directory, a locally installed
CLI, and the Compose v2 spelling.

```bash
# Gate 1
cd backend && dotnet restore && dotnet build --configuration Release --warnaserror

# Gate 2  (run after gate 1: --no-build uses the binaries gate 1 produced)
cd backend && dotnet test --configuration Release --no-build --verbosity normal

# Gate 3
cd frontend && npm ci && npx ng build --configuration production

# Gate 4
cd frontend && npx ng test --watch=false --browsers=ChromeHeadless --code-coverage

# Gate 5
cd backend && dotnet test --configuration Release --filter "Category=Integration"

# Gate 6
docker compose -f docker/docker-compose.yml --env-file docker/.env build

# Gate 7
docker compose -f docker/docker-compose.yml --env-file docker/.env up -d
sleep 10
curl -f http://localhost:8080/health
curl -f http://localhost:4200
docker compose -f docker/docker-compose.yml --env-file docker/.env down
```

Three mechanical differences, and nothing else:

- **`cd backend` / `cd frontend`** are added to gates 2, 4 and 5. The specification carries
  the directory change only on the gate that first needs it; each command above is
  self-contained instead.
- **`npx ng`** replaces the bare `ng` in gates 3 and 4, because the Angular CLI is a local
  devDependency by design — there is no global `ng` on the path, and the bare spelling exits
  127.
- **`docker compose`** — two words — replaces `docker-compose` in gates 6 and 7, and the
  explicit `-f docker/docker-compose.yml --env-file docker/.env` replaces the implicit
  lookup, because the Compose file lives in `docker/` rather than at the repository root.
  Compose v1 is end-of-life and absent from current Docker distributions; the literal v1
  spelling exits 127 without starting anything. No product change can address this.

**Result matrix.** All seven gates were executed from this repository on **12 August 2026** on
Linux (Ubuntu 25.10 container) with .NET SDK 8.0.423 (runtimes 8.0.29), Node 20.20.2, npm
10.8.2, Angular CLI 19.2.27, Chrome Headless 151.0.0.0, Docker Engine 29.7.0 with Compose
v5.3.1, and SQL Server 2022 for the integration suites. Each row names the command that
produced its evidence.

| Gate | Command run | Status (12 Aug 2026) | Measured evidence |
| --- | --- | --- | --- |
| 1 | Gate 1 above | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` across all six projects |
| 2 | Gate 2 above | **PASS** | `DnnMigration.UnitTests` 3078 passed / 0 failed / 0 skipped; `DnnMigration.IntegrationTests` 1362 passed / 0 failed / 0 skipped; 4440 tests total |
| 3 | Gate 3 above | **PASS** | `npm ci` restored 938 packages; production build emitted `dist/dnn-migration/browser`; initial payload 500.80 kB raw / 131.74 kB transfer |
| 4 | Gate 4 above | **PASS** | `TOTAL: 5724 SUCCESS` — 5 724 specs, zero failures; coverage written to `frontend/coverage/dnn-migration` — statements 95.08 %, branches 85.38 %, functions 97.60 %, lines 95.04 % |
| 5 | Gate 5 above | **PASS** | `Failed: 0, Passed: 1362, Skipped: 0` on `DnnMigration.IntegrationTests.dll`; the unit-test assembly matches nothing and the run still exits 0 |
| 6 | Gate 6 above | **PASS** | Exit 0; both images tagged — `dnnmigration-api:latest` (197 MB) and `dnnmigration-frontend:latest` (63.5 MB) |
| 7 | Gate 7 above | **PASS** | `curl -f http://localhost:8080/health` → 200 and `curl -f http://localhost:4200` → 200, both services reporting `healthy`; the full `up -d` → probe → `down` cycle completed with exit 0 throughout. A request through the SPA origin (`/api/v1/portals`, no token) was answered 401 `application/problem+json`, proving the proxy hop end to end |

One note on how gate 7 was measured, because the host it ran on matters. An instance of this
same topology was already running there, and the Compose file fixes `container_name`, so the
gate was taken in two parts: the `curl -f` probes above were made against the canonical
`127.0.0.1:8080` and `:4200` mappings of the running instance, and the full `up -d` → probe →
`down` cycle was run from this checkout under a second Compose project name with the container
names, published ports and network subnet shifted, so that it could not disturb the first. Both
halves used the gate command above unchanged apart from those shifts; a host with nothing
already running needs neither.

Four things worth knowing about individual gates, each discovered by running it rather than by
predicting it:

- **Gate 1 fails until `CS1591` is suppressed.** Documentation generation combined with
  warnings-as-errors turns every undocumented public member into a build error. See
  [§4](#4-backend) for the suppression and why it is required rather than preferred.
- **Gate 4 cannot start without [`frontend/karma.conf.js`](./frontend/karma.conf.js)** and its
  `ChromeHeadlessNoSandbox` launcher. The literal command names `ChromeHeadless`, which
  *overrides* the configured browser list, so that name is declared as a flagged launcher too.
- **Gates 2 and 5 need a reachable SQL Server.** The suite provisions its own throwaway
  database by either of the routes in
  [§4](#the-database-the-integration-suite-runs-against) — a container runtime, or
  `DNN_TEST_SQLSERVER` pointed at an existing server. It fails closed rather than degrading.
- **Gate 7 depends on three things no build step checks:** the **anonymous** `/health`, the
  **`wget`-based** container probe, and the **relative** production `apiBaseUrl`. All three
  are pinned in [§6](#6-containers).

**Authoring history, kept separate from the result above.** Gates 6 and 7 were *not*
executable in the environment in which the container artefacts were first written: it had no
container runtime at all. The four Docker artefacts were therefore authored
correct-by-construction from their verbatim specifications, with both name placeholders proven
indirectly — a solution that emitted `DnnMigration.Api.dll`, the exact filename the image's
`ENTRYPOINT` names, and a workspace that emitted `dist/dnn-migration/browser/`, the exact path
the frontend image copies. That constraint no longer applies and is recorded only so the dated
`PASS` rows above are not mistaken for a re-statement of it. The same is true of one further
limitation, which **does** still apply: **the legacy VB.NET solution was never compiled**,
because `msbuild`, `mono` and `vbnc` are unavailable, so every claim about *legacy* behaviour
in [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md) rests on reading the source and the DDL chain
and carries a file-and-line citation. Claims about the *target* in that document are a
different matter: many are runtime observations, and its register records the measurements
they came from.

### `npm audit` is deliberately not a build gate

This is a decision about the *build*, not a claim that the dependency graph is clean. It is
recorded with the evidence it rests on, and that evidence has a date on it because advisory
data changes underneath a fixed lockfile.

**Everything with a released fix has been fixed**, so the set below is the *unfixable* one
rather than an accepted backlog. Eight `overrides` in
[`frontend/package.json`](./frontend/package.json) — two of them nested under a specific parent —
resolve the development-toolchain advisories that had a compatible release: `nanoid`,
`webpack-dev-server`, `serialize-javascript`, `uuid`, `sigstore`, `@sigstore/core`, and `esbuild`
and `http-proxy-middleware` where the *affected* copy was an exact pin rather than a live range.
Two packages were also moved out of production dependencies, since nothing under `frontend/src`
imports them.

**Reproduce it:** `cd frontend && npm audit`. Measured on **13 August 2026** with npm 10.8.2
against the committed `package-lock.json` (SHA-256 `d8f3a622…`), which resolves 1,123
dependencies — 9 production, 1,115 development, 171 optional:

| Severity | Count | Was, before remediation |
| --- | --- | --- |
| Critical | 0 | 0 |
| High | 13 | 19 |
| Moderate | 0 | 7 |
| Low | 0 | 1 |
| **Total** | **13** across 13 root advisory records | **27** across 22 |

Production-only (`npm audit --omit=dev`) reports **5 high**, down from 7.

Two of the 13 are `image-size`, reached through `less`, and are **genuinely unfixable**: the
latest published release is itself inside the affected range. They are also unreachable — the
workspace contains **zero `.less` files**, `inlineStyleLanguage` is `scss`, and the runtime image
serves static files from nginx with no build toolchain present at all.

The remaining eleven records resolve to six advisories against Angular packages, each naming a
feature this application does not use. All six are ranged `<= 19.2.25`, i.e. the whole of Angular
19.2.x, so none has a remedy inside the mandated major version. Note that `@angular/compiler` is
now a **development** dependency, so the two advisories against it no longer touch anything the
browser downloads:

| Advisory | Package | Requires | Measured in this workspace |
| --- | --- | --- | --- |
| GHSA-rgjc-h3x7-9mwg | `@angular/core` | Client hydration | No `provideClientHydration`, no `provideServerRendering`, `@angular/platform-server` not installed |
| GHSA-39pv-4j6c-2g6v, GHSA-jhpw-976m-542j | `@angular/common` | `HttpTransferCache` | No `withHttpTransferCache`; the cache exists only alongside hydration |
| GHSA-48r7-hpm6-gfxm | `@angular/common` | A caller-influenced date format | `formatDate` is called once, in `shared/pipes/date-display.pipe.ts`, with a closed two-member pattern union that no request value can reach |
| GHSA-jj27-h5hq-8x99 | `@angular/compiler` | Angular i18n | No `i18n` template attributes and `@angular/localize` is not installed |
| GHSA-58w9-8g37-x9v5 | `@angular/compiler` | A sanitised property bound two-way | No security-sensitive DOM property is bound anywhere: zero `innerHTML`, zero `DomSanitizer`, zero `bypassSecurityTrust` in production source |

So an audit threshold on the build would fail on 13 high-severity findings that this
application cannot execute, on the day it was added, with no upgrade available inside Angular
19 — which is why the gate list stops at install-plus-build (gate 3) and the test run (gate
4). The same reasoning is applied to the NuGet graph, where auditing is left at the SDK default
rather than promoted to an error under `TreatWarningsAsErrors`.

**The NuGet graph reports no vulnerable package at all.**
`dotnet list package --vulnerable --include-transitive` is clean across all six projects.
Keeping it that way needs **two direct
security pins** in
[`backend/tests/DnnMigration.IntegrationTests/DnnMigration.IntegrationTests.csproj`](./backend/tests/DnnMigration.IntegrationTests/DnnMigration.IntegrationTests.csproj)
— packages the project does not otherwise reference, pinned only to displace a vulnerable
transitive resolution: **`SSH.NET` 2026.0.0**, which `Testcontainers` would otherwise resolve at
2023.0.0 (`GHSA-q939-rpr3-3284`), and `SQLitePCLRaw.bundle_e_sqlite3`. Both are guarded by
`Infrastructure/SecurityPinTests.cs`, which inspects the assemblies actually copied beside the
tests rather than the manifest, because a pin can be present in a project file and still lose to a
nearer constraint. **Do not remove either reference as unused.**

Separately, deprecation is tracked but is *not* a security finding: the two lists are disjoint, and
`MIGRATION_NOTES.md` §10 carries a dated inventory of the deprecated packages with the pin each one
is downstream of.

**Re-check the table above** whenever `package-lock.json` changes, before a release, and when
the Angular major version is raised — at which point the six runtime advisories should be
re-tested for a remedy rather than re-inherited. The decision is scoped to this lockfile and
this major version, not open-ended. `MIGRATION_NOTES.md` carries the same record with its
supersession history.

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

One consequence is worth knowing before you go looking for bugs: the identity seeds collide
with the legacy null sentinels. `Portals.PortalID` is declared `IDENTITY(-1,1)` and
`Roles.RoleID` is declared `IDENTITY(0,1)`, so `-1` is a perfectly legitimate portal
identifier at the same time as being the legacy `Null.NullInteger` "absent" marker, and `0`
is a legitimate role identifier. Treating either as "no value" silently changes a branch
outcome. The domain model therefore uses nullable CLR types, while sentinel semantics are
preserved at the DTO boundary wherever the legacy contract is externally visible. All of it
is itemised in [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md).

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
├── NuGet.Config                cleared package sources plus package-source mapping
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
├── docker-compose.yml          the two-service topology, loopback-published
├── docker-compose.tls.yml      TLS overlay: public 80/443, API port withdrawn
├── nginx.conf                  the main configuration — copied into the image
├── api-proxy.conf              the /api/ proxy and its RFC 7807 gateway answer  \
├── spa-static.conf             hashed-asset policy and SPA fallback             > shared by
├── security-headers.conf       the response header policy                       /  both servers
├── nginx.tls.conf.template     TLS server block; server_name from DNN_PUBLIC_HOST
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
- **Package restore is repository-controlled.** [`backend/NuGet.Config`](./backend/NuGet.Config)
  clears every inherited source, declares the single public source the dependency inventory was
  pinned against, and maps each package identity to it with no bare `*` pattern, so an unreviewed
  identity fails restore rather than resolving from an unexpected feed. It sits beside the
  solution rather than at the repository root because NuGet searches upwards from each project,
  so that placement governs all six projects while leaving the legacy trees beside it alone — and
  it travels into the API image with `COPY backend/ ./`, so the container restore is governed the
  same way without an instruction of its own.
- **Secrets can be delivered as mounted files, and the host reads them.** A key-per-file
  configuration source is registered over `/run/secrets`, where the **file name is the
  configuration key** with `__` for the section separator — so
  `/run/secrets/ConnectionStrings__Default` and `/run/secrets/Jwt__Secret` supply exactly what
  the matching environment variables would, and a trailing newline is trimmed. That is the
  shape `docker compose secrets:`, Docker Swarm and a Kubernetes secret volume all produce; set
  `Secrets__Directory` if a projected volume is mounted elsewhere. A mounted file **outranks**
  an environment variable of the same name, because mounting is how a value is kept out of the
  environment block `docker inspect` can read. Each file must be readable by the image's
  unprivileged uid 1000 account — mode `0444`, which is the orchestrator default. This closes a
  gap rather than adding a feature: the deployment template previously said file delivery
  needed no code change while no such source was registered, so a `/run/secrets` mount was not
  read at all.
- **Package restore is repository-controlled, in the container as well as on a workstation.**
  [`backend/NuGet.Config`](./backend/NuGet.Config) clears every inherited source, declares the single public
  source the dependency inventory was pinned against, and maps each package identity to it
  with no bare `*` pattern, so an unreviewed identity fails restore rather than resolving from
  an unexpected feed. [`docker/api.Dockerfile`](./docker/api.Dockerfile) copies that file into
  its build stage and restores with `--configfile`, which makes NuGet read it *and nothing
  else* — so the image cannot inherit a source from the base image's own settings or from a
  build agent. Locked mode is not passed on the command line: `backend/Directory.Build.props`
  sets `RestorePackagesWithLockFile` and `RestoreLockedMode` for all six projects, and the six
  committed `packages.lock.json` files travel into the build with the source tree.
- **Both images are pinned by base-image digest, with the readable tag retained.** All four
  `FROM` lines name a digest as well as a tag, so one commit builds one image rather than
  whatever the moving tag points at that week. What was verified inside each pinned digest is
  recorded at the instruction: SDK 8.0.424 (satisfying `backend/global.json`), ASP.NET Core
  runtime 8.0.30 on Alpine 3.24.1 with ICU 78.1-r0 available, Node 20.20.2 with npm 10.8.2
  (satisfying the `engines` range), and nginx 1.31.3 with the BusyBox `wget` the health probe
  depends on. Refreshing a digest is a reviewed step — resolve the tag's current digest,
  confirm the pinned prerequisite still holds, then re-run the container and end-to-end gates
  — and it is where a base-image advisory is acted on. The `apk`-installed ICU packages are
  controlled by that digest rather than by a version constraint, because an Alpine branch
  repository serves only the current version of each package, so an exact `apk` pin would turn
  a distribution security update into a broken build.

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
  needs its `backend/NuGet.Config` mapping in the same commit.

### Frontend conventions

- Standalone components, signals for state, typed `nonNullable` reactive forms, built-in
  control flow with `track` on every `@for`, and `ChangeDetectionStrategy.OnPush` everywhere.
- Component-scoped SCSS over global design tokens — not Tailwind, and no component library.
- **No hardcoded design values.** Every colour, type step, space, radius, elevation, duration
  and dimension resolves to a token in `frontend/src/styles/_tokens.scss` — if a value
  describes how the application *looks*, it is a token or it is a bug. The keyword literals
  `0`, `none`, `auto`, `inherit`, `currentColor` and `transparent` are permitted anywhere.
  Breakpoints come from the shared mixins, never from an ad-hoc media query.

  Separately, a **bounded set of structural literals** is permitted, because these describe
  layout mechanics rather than design and a token for them would name nothing: CSS-grid line
  indices and track counts (`grid-column: 1 / -1`, `repeat(2, …)`), the `fr` unit and
  `minmax(0, 1fr)`, flex factors (`flex: 1 1 …`), line-clamp counts, the `-1` multiplier in
  `calc(-1 * var(--token))`, viewport and percentage bounds inside `min()`/`calc()`
  (`100vw`, `100vh`, `100%`), `1em` where a box is deliberately sized to the current type
  step, and keyframe rotation angles (`0deg`, `360deg`). That list is exhaustive as of this
  writing — 24 declarations across the workspace, and no other kind of literal appears in a
  declaration. Adding a category to it is a review decision, not a local one.
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
| Health check returns 401 | `/health` is not anonymous | Restore the `.AllowAnonymous()` call on the `MapHealthChecks` registration in `Api/Extensions/ApplicationBuilderExtensions.cs`; Compose's `service_healthy` condition depends on it |
| The frontend container never starts | The API health check never passes | Fix `/health`, and confirm the probe uses `wget --spider` rather than `curl` — the Alpine runtime image has no `curl` |
| The frontend image build fails at a `COPY` step | `docker/nginx.conf` missing, or excluded from the build context | Ensure the file exists and that the root [`.dockerignore`](./.dockerignore) does not exclude `docker/`. Build from the repository root, not from `docker/` |
| SPA requests answered 502 while both containers are healthy | The `api` service was renamed | `docker/nginx.conf` resolves the hostname `api`; keep the service name |
| SPA requests answered 503 with `Retry-After` just after a redeploy | The proxy's short-lived DNS entry has not yet aged out | Wait a few seconds. Nothing needs restarting |
| `dotnet run` fails to bind port 8080 | The container topology already holds it | Run with `--no-launch-profile --urls http://127.0.0.1:5080` |
| Sign-in returns 400 with an error on `portalId` | The host and port used are not a row in `PortalAlias` | Seed the alias, or address a portal explicitly |
| A child portal's address serves the SPA but every call resolves the parent | The tenant segment is being dropped somewhere between the browser and the API | All three hops must carry it: the browser composes it (`frontend/src/app/core/config/tenant-path.ts`), the proxy matches it (`docker/api-proxy.conf`), and the API rebases it (`Api/Middleware/TenantPathBaseMiddleware.cs`). See [addressing a child portal](#addressing-a-child-portal-beneath-a-path-segment) |
| A child portal's address answers 404 for every API call, or 403 `portal.tenant_unresolved` | No alias row matches `host/segment`, or the child portal designates no administrator account, administrator role or registered-user role | Add the alias row exactly as `host[:port]/segment`, and create the child through `POST /api/v1/portals` rather than by hand — resolution refuses a portal with those designations unset |
| `the attribute 'version' is obsolete` on every `docker compose` | The preserved Compose file keeps the `version` key | Expected. Do not delete the key |

---

## 13. Documentation map

| Document | What it answers | Standing |
| --- | --- | --- |
| `README.md` (this file) | How to build, configure, test, run and operate both stacks, and how the seven gates came out | Current |
| [`MIGRATION_NOTES.md`](./MIGRATION_NOTES.md) | Why the target differs from the legacy application, decision by decision. Legacy claims carry a file-and-line citation; target claims are frequently runtime observations, and its register carries the measurements | Current. Sections 1–13 are canonical; the register below them is append-only evidence |
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
