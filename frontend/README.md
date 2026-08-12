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

`nvm use` reads `.nvmrc`; the full cross-stack prerequisite matrix is in [`../README.md`](../README.md).

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
    └── features/     portal, module, user, role, auth  (lazy-loaded)
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
- **Providers wired once** in `app.config.ts`. Interceptor order is significant:
  correlation-id, then auth, then error.
- **Strict TypeScript and strict Angular templates** — `tsconfig.json` sets
  `strict: true` and `strictTemplates: true`. Never relax a flag; fix the code instead.
- **No third-party UI component library or CSS framework** is installed; the shared
  component set under `src/app/shared/` is the design system.
- **Zero hardcoded CSS values** — every property value resolves to a token in
  `src/styles/_tokens.scss`. The only permitted literals are `0`, `none`, `auto`,
  `inherit`, `currentColor` and `transparent`. Breakpoints live once in `_mixins.scss`.
- **One `.spec.ts` per component, service, interceptor, guard and store**, using
  `TestBed`, `provideHttpClientTesting` and `HttpTestingController`. Karma with Jasmine.

## API base URL

`src/environments/environment.ts` is the **production** configuration and sets
`apiBaseUrl` to the **relative** path `/api/v1`.

> ⚠️ **It must stay relative.** `docker/nginx.conf` proxies `/api/` to
> `http://api:8080/api/`, so the browser reaches the API through the same origin that
> served the application. An absolute `http://api:8080` resolves only inside the
> container network and fails from the browser — and **no build step detects it.**

`src/environments/environment.development.ts` overrides it with
`http://localhost:8080/api/v1` for `npm start`; `angular.json` performs that swap
through the `development` configuration's `fileReplacements`.

## Notes

- Every deliberate behavioural divergence from the legacy application is recorded in
  the repository-root [`MIGRATION_NOTES.md`](../MIGRATION_NOTES.md).
- The legacy VB.NET application under `Library/` and `Website/` is retained
  **read-only** as the authoritative reference for business rules, and is never edited.
- Backend API, container images and the validation report: [`../README.md`](../README.md).
- A missing-browser error from the test run means Chrome or Chromium is absent, or that `CHROME_BIN` points at nothing.
