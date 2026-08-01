// Karma configuration for the `dnn-migration` Angular 19 workspace.
// Reference: https://karma-runner.github.io/6.4/config/configuration-file.html
//
// MIGRATION: this file has no legacy counterpart. The DotNetNuke 4.9.0 source tree
// (`Library/`, `Website/`) contains no automated tests of any kind, so the entire
// front-end test harness is net-new rather than a translation of anything. Every
// piece of correctness evidence for the migrated SPA is produced by this harness,
// which is why its configuration is documented switch by switch below.
//
// MIGRATION: the validation gate this file exists to serve is fixed and may not be
// edited:
//
//     ng test --watch=false --browsers=ChromeHeadless --code-coverage
//
// Chrome's setuid sandbox cannot be established when the browser process runs as
// root - the default inside a container - so the stock `ChromeHeadless` launcher
// exits immediately with "Running as root without --no-sandbox is not supported"
// (crbug.com/638180) and Karma gives up after two attempts. Verified by execution
// in this environment against Google Chrome 151. Because the gate passes
// `ChromeHeadless` and not `ChromeHeadlessNoSandbox`, the command line overrides
// whatever `browsers` default this file declares, so a config that only defines
// `ChromeHeadlessNoSandbox` would still fail the gate. Two custom launchers sharing
// one flag set are therefore declared:
//
//   * `ChromeHeadlessNoSandbox` - the launcher the migration plan names, and the one
//     a developer or a future CI job selects explicitly;
//   * `ChromeHeadless` - a shadowing override, so the *fixed* gate command runs
//     sandbox-free without being edited. Karma turns every `customLaunchers` key
//     into the provider for that browser name and registers it after the launcher
//     plugins, so this entry replaces the same-named launcher contributed by
//     karma-chrome-launcher and wins even when the CLI asks for `ChromeHeadless`.
//
// MIGRATION: the shadowing entry derives from `Chrome`, not from `ChromeHeadless`.
// Karma 6.4 registers each `customLaunchers` key as a dependency-injection provider
// named `launcher:<key>` whose factory resolves the token `launcher:<base>` through
// a child injector (karma/lib/config.js). Because that generated module also
// replaces the provider karma-chrome-launcher published under the same name, an
// entry keyed `ChromeHeadless` with `base: 'ChromeHeadless'` resolves to itself and
// dies with `Cannot load browser "ChromeHeadless"! RangeError: Maximum call stack
// size exceeded` - reproduced deliberately against karma 6.4.4 before this file was
// written. Deriving the shadow from `Chrome` is behaviourally equivalent because
// `--headless=new` is already part of the shared flag set, and the only switch the
// `ChromeHeadless` base would add beyond it is `--remote-debugging-port=9222`, which
// this configuration does not need. `ChromeHeadlessNoSandbox` keeps
// `base: 'ChromeHeadless'` exactly as specified - that key does not collide with a
// base name, so it resolves cleanly (through the shadow, with the identical flags).

/**
 * Chrome command-line switches required to run headless Chrome inside a container.
 *
 * Declared once and shared by both custom launchers below so that the two can never
 * drift apart. Every switch is load-bearing:
 *
 *   --no-sandbox             Chrome refuses to start as root without it. This is the
 *                            root cause of the gate failure described above.
 *   --disable-gpu            no GPU device exists in a headless container; without
 *                            this Chrome may stall attempting GPU initialisation.
 *   --disable-dev-shm-usage  /dev/shm defaults to 64 MB under Docker; Chrome
 *                            exhausts it and crashes mid-run with opaque errors.
 *   --headless=new           the modern headless implementation, and what makes the
 *                            `Chrome`-based shadow launcher headless.
 *
 * Nothing speculative is added: switches such as --remote-debugging-port,
 * --user-data-dir or --window-size duplicate what karma-chrome-launcher already
 * supplies, and --single-process is known to destabilise Karma runs.
 */
const CHROME_HEADLESS_FLAGS = [
  '--no-sandbox',
  '--disable-gpu',
  '--disable-dev-shm-usage',
  '--headless=new',
];

module.exports = function (config) {
  config.set({
    // Relative paths in this file resolve against the workspace root (`frontend/`).
    basePath: '',

    // `jasmine` supplies the assertion framework. The Angular builder framework
    // compiles `src/**/*.spec.ts` per tsconfig.spec.json, bootstraps the TestBed
    // environment and instruments sources for coverage when --code-coverage is
    // passed. Because the builder owns bootstrapping, no separate spec entry-point
    // module exists or is needed; both entries are mandatory and in this order.
    frameworks: ['jasmine', '@angular-devkit/build-angular'],

    // Required explicitly rather than relying on Karma's `karma-*` auto-discovery:
    // the dependency contract with package.json stays visible, and a missing or
    // mis-hoisted plugin fails loudly at load time instead of silently disabling a
    // framework or reporter. Every entry is a pinned devDependency.
    plugins: [
      require('karma-jasmine'),
      require('karma-chrome-launcher'),
      require('karma-jasmine-html-reporter'),
      require('karma-coverage'),
      require('@angular-devkit/build-angular/plugins/karma'),
    ],

    client: {
      jasmine: {
        // Deliberately left at Jasmine's defaults. Random spec order surfaces
        // hidden inter-spec coupling early instead of masking it behind a fixed
        // sequence, and pinning a seed here would hide exactly that class of defect.
      },
      // Leave the Jasmine HTML reporter output visible in the browser.
      clearContext: false,
    },

    jasmineHtmlReporter: {
      // Remove the duplicated stack traces the HTML reporter otherwise emits.
      suppressAll: true,
    },

    coverageReporter: {
      // `dnn-migration` is the kebab-case Angular project name and matches
      // `dist/dnn-migration`. The PascalCase `DnnMigration` identifies the .NET
      // solution and must never be substituted here. The repository .gitignore
      // excludes `coverage/` and `/frontend/coverage/`, so this output is correctly
      // untracked.
      dir: require('path').join(__dirname, './coverage/dnn-migration'),
      subdir: '.',
      reporters: [
        // Browsable HTML report for humans.
        { type: 'html' },
        // Printed to stdout so a gate run is self-evidencing without opening a file.
        { type: 'text-summary' },
        // Portable machine-readable format for any downstream consumer.
        { type: 'lcovonly' },
      ],
      // No `check` thresholds are declared: no coverage floor is specified for this
      // migration, and inventing one would fail the gate on grounds nobody set.
    },

    // `coverage` is deliberately absent from this list. The Angular builder registers
    // the coverage reporter itself when --code-coverage is passed; naming it here as
    // well instruments the sources twice and corrupts the report.
    reporters: ['progress', 'kjhtml'],

    // Default for a bare `npm test`. Gate 4's `--browsers` flag overrides it, which
    // is precisely why the shadowing launcher below exists.
    browsers: ['ChromeHeadlessNoSandbox'],

    customLaunchers: {
      // Named by the migration plan; the explicit, unambiguous choice for a
      // developer or a CI job that can pass its own --browsers value.
      ChromeHeadlessNoSandbox: {
        base: 'ChromeHeadless',
        flags: CHROME_HEADLESS_FLAGS,
      },
      // Shadows the plugin-provided launcher of the same name so the fixed gate
      // command `--browsers=ChromeHeadless` also runs with the sandbox disabled.
      // See the MIGRATION note above for why the base is `Chrome` here.
      ChromeHeadless: {
        base: 'Chrome',
        flags: CHROME_HEADLESS_FLAGS,
      },
    },

    // Local development ergonomics only. Neither affects a gate run: `--watch=false`
    // makes the Angular builder set `singleRun` to true for that invocation.
    restartOnFileChange: true,
    singleRun: false,
  });
};
