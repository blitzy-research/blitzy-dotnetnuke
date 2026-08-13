# dnn-migration — Angular 19 frontend

Administration console for the DotNetNuke migration: an Angular 19 single-page
application built from standalone components and Signals, served in production by
nginx behind the same origin as the API.

> **Full project documentation, prerequisites and the backend and container
> instructions live in the repository-root [`README.md`](../README.md).** This file
> covers only the `frontend/` workspace.

## Prerequisites

| Tool | Version | Note |
| --- | --- | --- |
| Node | **20.20.2** | Pinned in `.nvmrc` and in `package.json`'s `engines`; matches the `node:20-alpine` build stage of the front-end image. Node 22 is deliberately not used |
| npm | **10.8.2** | Ships with Node 20.20.2 |
| Angular CLI | **19.2.27** | A local dev dependency — drive it with `npx ng`. A global install is optional |
| Chrome / Chromium | any recent stable | Drives the Karma run. Set `CHROME_BIN` if the browser is not on the default path |

Install Node 20.20.2 by whatever means this machine already uses — `.nvmrc` is a plain version
file, so a direct install, a distribution package or a container image is as valid as a version
manager. **If you use nvm**, `nvm use` reads it for you. The full cross-stack prerequisite matrix
is in [`../README.md`](../README.md).

## Commands

All four run from `frontend/`:

```bash
npm ci                                        # deterministic install; needs package-lock.json
npm start                                     # dev server on http://localhost:4200
npm run build -- --configuration production   # emits dist/dnn-migration/browser
npm test -- --watch=false --browsers=ChromeHeadless --code-coverage
```

Three details are load-bearing:

- **Install with `npm ci`, never with a lockfile-rewriting install command.** It
  restores exactly what `package-lock.json` records and fails outright when that file
  is absent, which is why the lockfile is deliberately tracked rather than ignored.
- **The production build must emit exactly `dist/dnn-migration/browser`.**
  `docker/frontend.Dockerfile` copies that literal path into the nginx image, so
  changing `outputPath` in `angular.json` breaks the container build.
- **The headless run depends on `karma.conf.js`** and its `ChromeHeadlessNoSandbox`
  launcher: Chrome refuses to start as root without a sandbox flag, so the launcher
  adds `--no-sandbox`, `--disable-gpu`, `--disable-dev-shm-usage` and `--headless=new`.
  Without that file no browser starts at all.

## Project structure

```text
src/
├── index.html, main.ts, styles.scss
├── styles/           _tokens, _mixins, _reset, _layout, _forms, _tables (SCSS partials)
├── environments/     environment.ts (production), environment.development.ts
└── app/
    ├── app.component.*, app.config.ts, app.routes.ts
    ├── core/         models, services, interceptors, guards, state, config, utils
    ├── shared/       presentational components, directives, pipes
    ├── layout/       shell, header, sidebar, footer, notifications
    └── features/     portal, module, user, role, auth, not-found  (lazy-loaded)
public/               static assets served by nginx
```

`public/` is a sibling of `src/` at the workspace root, not a folder inside it;
`angular.json` declares it as the asset input.

## Conventions

- **Standalone components only** — there is no `NgModule` anywhere in this workspace.
- **Built-in control flow** `@if` / `@else if` / `@else` / `@for` / `@switch` — never
  `*ngIf` or `*ngFor`; `@for` always carries `track`.
- **`ChangeDetectionStrategy.OnPush` on every component.**
- **State via Signals** — `signal()`, `computed()`, `asReadonly()`, and `effect()` only
  for genuine side effects. No third-party store library, no subject-backed stores.
- **Typed reactive forms only** — each form declares a model interface used as
  `FormGroup<TModel>`, every control `new FormControl<T>(init, { nonNullable: true })`.
- **Application-wide providers are wired once** in `app.config.ts` — the router, the HTTP
  client and its three interceptors, and anything else the whole application shares. Interceptor
  order is significant: correlation-id, then auth, then error. Narrower scopes use Angular's
  ordinary mechanisms rather than being hoisted there: singletons declare
  `providedIn: 'root'` on themselves (every service and store under `core/` does), and a
  directive that participates in forms provides its own token locally — as
  `shared/directives/native-date-validity.directive.ts` does with `NG_VALIDATORS`. What
  `app.config.ts` must not become is a registry of things only one feature uses.
- **Strict TypeScript and strict Angular templates** — `tsconfig.json` sets
  `strict: true` and `strictTemplates: true`. Never relax a flag; fix the code instead.
- **No third-party UI component library or CSS framework** is installed; the shared
  component set under `src/app/shared/` is the design system.
- **No hardcoded design values** — every colour, type step, space, radius, elevation, duration
  and dimension resolves to a token in `src/styles/_tokens.scss`. If a value describes how the
  application *looks*, it is a token or it is a bug; duplicating a token's value as a literal
  counts as hardcoding it. The keyword literals `0`, `none`, `auto`, `inherit`, `currentColor`
  and `transparent` are permitted anywhere, and breakpoints live once in `_mixins.scss`.
- **A bounded set of structural literals is permitted**, because these name layout mechanics
  rather than appearance and a token for them would name nothing. Nine categories are permitted
  and **17 declarations across the 44 stylesheets** use them, measured rather than estimated:
  grid track counts (`repeat(2, …)`) 5, CSS-grid line indices (`grid-column: 1 / -1`) 4, the
  `-1` multiplier in `calc(-1 * var(--token))` 3, the `fr` unit and its `minmax(0, 1fr)`
  zero-basis pairing 2, flex grow and shrink factors (`flex: 1 1 …`) 2, line-clamp counts 2, and
  viewport or percentage bounds inside `min()`/`calc()` 1. Those counts sum to 19 because two
  declarations use two categories at once — `repeat(2, minmax(0, 1fr))` is both. The two
  remaining permitted categories, `1em` sizing to the current type step and keyframe rotation
  angles, are **currently empty**: both were tokenised, so the sort indicator's `inline-size`
  and the spinner's `0deg`/`360deg` frames now read tokens. That list is exhaustive, and adding a
  category to it is a review decision, not a local one.
- **One `.spec.ts` per component, service, interceptor, guard and store**, using
  `TestBed`, `provideHttpClientTesting` and `HttpTestingController`. Karma with Jasmine.

## API base URL

`src/environments/environment.ts` is the **production** configuration and sets
`apiBaseUrl` to the **relative** path `/api/v1`.

> ⚠️ **It must stay relative.** `docker/api-proxy.conf` — the shared snippet included by
> both the plain-HTTP server in `docker/nginx.conf` and the TLS server in
> `docker/nginx.tls.conf.template` — proxies `/api/` to the `api` service on port 8080, so
> the browser reaches the API through the same origin that served the application. An
> absolute `http://api:8080` resolves only inside the container network and fails from the
> browser — and **no build step detects it.**

`src/environments/environment.development.ts` overrides it with
`http://localhost:8080/api/v1` for `npm start`; `angular.json` performs that swap
through the `development` configuration's `fileReplacements`.

### Child portals: the tenant prefix is added at run time

A child portal is addressed beneath a **path segment** of a shared host — `host/acme` — which
is the shape the legacy signup screen stored (`Website/admin/Portal/Signup.ascx.vb` L232-L236),
and the API identifies the tenant from that segment. One built bundle serves every tenant, so
the prefix cannot be configured; `src/app/core/config/tenant-path.ts` derives it from the
address the document was served at, and `api-endpoints.ts` composes it with the configured base
in the two places that resolve a base — the URL builder AND the predicate the auth and
correlation interceptors classify requests with. Both must see the same value: a builder that
prefixed while the predicate did not would drop the bearer token from every call under a child
portal.

The same value is supplied as the router's `APP_BASE_HREF` (`app.config.ts`), so in-application
links keep the prefix. `index.html` keeps `<base href="/">`: the hashed assets are served from
the server root for every tenant, and a per-tenant document base would send the browser looking
for them beneath the tenant's segment. One segment of prefix is honoured, matching the proxy's
own matcher; a segment that spells one of the console's own top-level route names is read as the
console's, which is recorded in [`../MIGRATION_NOTES.md`](../MIGRATION_NOTES.md).

## Notes

- Every deliberate behavioural divergence from the legacy application is recorded in
  the repository-root [`MIGRATION_NOTES.md`](../MIGRATION_NOTES.md).
- The legacy VB.NET application under `Library/` and `Website/` is retained
  **read-only** as the authoritative reference for business rules, and is never edited.
- Backend API, container images and the validation report: [`../README.md`](../README.md).
- A missing-browser error from the test run means Chrome or Chromium is absent, or that `CHROME_BIN` points at nothing.
