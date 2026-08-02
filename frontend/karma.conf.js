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

/**
 * Candidate filesystem locations of a Chrome or Chromium executable, in probe order.
 *
 * MIGRATION: this list exists because the gate command is fixed and cannot name a
 * binary. `ng test --watch=false --browsers=ChromeHeadless --code-coverage` says
 * WHICH launcher to use and nothing about where the browser lives, so the only
 * remaining channel is `process.env.CHROME_BIN`, which karma-chrome-launcher reads.
 * When it is unset the launcher falls back to its own search, and on a distribution
 * whose package installs under a name the launcher does not try, that search fails
 * with `No binary for ChromeHeadless browser on your platform` — a message that
 * names no path and suggests no remedy.
 *
 * The order is deliberate: the Google-branded builds first, because that is what the
 * verified environment installs and what the migration plan pins; then the two
 * Debian/Ubuntu Chromium spellings; then the Alpine spellings, which matter because
 * the container images in `docker/` are Alpine-based and a future container-side test
 * run would find its browser under exactly those names. `chromium-browser` and
 * `chromium` are both listed because the two distributions disagree about which is
 * the executable and which is the wrapper.
 */
const CHROME_BINARY_CANDIDATES = [
  '/usr/bin/google-chrome',
  '/usr/bin/google-chrome-stable',
  '/opt/google/chrome/chrome',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
  '/usr/lib/chromium/chromium',
  '/usr/lib/chromium-browser/chromium-browser',
];

/**
 * Resolves the browser executable Karma should launch and publishes it on
 * `process.env.CHROME_BIN`.
 *
 * PRECEDENCE, HIGHEST FIRST:
 *
 *   1. `CHROME_BIN` — already set, so an operator or a CI job has made an explicit
 *      choice. It is returned UNVERIFIED and unmodified. Probing it and silently
 *      replacing a bad value would be worse than failing: the run would then execute
 *      against a browser nobody selected, and the specifications would pass or fail
 *      on the strength of a substitution recorded nowhere.
 *   2. `CHROME_PATH` — the variable Puppeteer and several CI images set. It is
 *      honoured because a machine that has one of them configured has already
 *      answered this question, and ignoring it would mean probing past a correct
 *      answer.
 *   3. The candidate list above, first one that exists on disk.
 *
 * If nothing resolves, the function returns `undefined` and sets nothing. That is
 * deliberate: leaving the variable unset hands the decision back to
 * karma-chrome-launcher's own search, which may well succeed on a platform this list
 * does not know about. Setting it to a path that does not exist would REPLACE a
 * recoverable situation with an unrecoverable one, and would do it while looking like
 * a fix.
 *
 * Writing to `process.env` rather than to a launcher's `executablePath` is not a
 * shortcut — it is the only channel that reaches BOTH custom launchers below without
 * duplicating the value, and it is the channel karma-chrome-launcher actually reads.
 */
function resolveChromeBinary() {
  const fs = require('fs');

  const configured = process.env.CHROME_BIN;
  if (configured) {
    return configured;
  }

  const puppeteerStyle = process.env.CHROME_PATH;
  if (puppeteerStyle) {
    process.env.CHROME_BIN = puppeteerStyle;
    return puppeteerStyle;
  }

  for (const candidate of CHROME_BINARY_CANDIDATES) {
    // `existsSync` and not `accessSync(X_OK)`: the executable bit is not ours to
    // adjudicate, and a path that exists but is not executable produces a launcher
    // error naming the path, which is a far more useful failure than skipping it and
    // reporting that no browser was found anywhere.
    if (fs.existsSync(candidate)) {
      process.env.CHROME_BIN = candidate;
      return candidate;
    }
  }

  return undefined;
}

module.exports = function (config) {
  // Resolved before `config.set` so that the value is in place on `process.env`
  // before Karma instantiates any launcher.
  resolveChromeBinary();

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

    // =========================================================================
    //  BROWSER RESILIENCE
    //
    //  MIGRATION: every value below is a NET ADDITION with no legacy counterpart —
    //  the legacy tree contains no automated tests of any kind — and every one of
    //  them exists to convert a SILENT, INTERMITTENT failure into either a
    //  successful run or an unambiguous message.
    //
    //  Karma's defaults were written for a developer's workstation launching a
    //  browser that is already warm. This harness runs headless Chrome inside a
    //  container, where the first launch pays for process start-up, and where the
    //  Angular builder is compiling the whole spec graph concurrently with the
    //  browser coming up. Both make the defaults too tight, and both fail in the
    //  same unhelpful way: the run reports a disconnected browser rather than a
    //  failing specification, so the output names no spec, no file and no cause.
    //
    //  These four are the reason a flake is a flake rather than a bug: raising them
    //  cannot mask a genuine assertion failure, because a failing expectation is
    //  reported by Jasmine and never reaches the disconnect machinery at all.
    // =========================================================================

    // How long Karma waits for a launched browser to connect back. The default is 60
    // seconds, which is ample for a warm browser and not always ample for a cold
    // container start competing with an in-progress build for CPU.
    captureTimeout: 120000,

    // How long Karma waits for a browser that has dropped its connection to come
    // back before declaring it gone. The default is 2 seconds — short enough that a
    // single long garbage-collection pause or a moment of host contention ends the
    // run.
    browserDisconnectTimeout: 30000,

    // How many times a disconnected browser may reconnect before the run is failed.
    // The default is 0, meaning the first disconnect is fatal. Two retries covers
    // the transient case without hiding a real one: a browser that is genuinely
    // crashing crashes on every attempt and the run still fails, only with three
    // pieces of evidence instead of one.
    browserDisconnectTolerance: 2,

    // How long a captured browser may report nothing at all before Karma assumes it
    // has hung. The default is 30 seconds, which a slow first compile can exceed
    // before a single specification has run — producing a timeout that looks like a
    // hanging test and is actually a build still in progress.
    browserNoActivityTimeout: 120000,

    // Local development ergonomics only. Neither affects a gate run: `--watch=false`
    // makes the Angular builder set `singleRun` to true for that invocation.
    restartOnFileChange: true,
    singleRun: false,
  });
};
