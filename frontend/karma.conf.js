// Karma configuration for the dnn-migration Angular 19 workspace.
//
// MANDATORY (AAP 0.9.4, Gate 4): the `ChromeHeadlessNoSandbox` custom launcher.
// Headless Chrome refuses to start as root inside a container without
// `--no-sandbox`, so `ng test --browsers=ChromeHeadless` fails outright. Gate 4
// must therefore be run against ChromeHeadlessNoSandbox (see the `test:ci` npm
// script), or with CHROME_BIN exported and this launcher selected explicitly.
//
// Docs: https://karma-runner.github.io/6.4/config/configuration-file.html

// Resolve the Chrome binary explicitly: karma-chrome-launcher's PATH probing is
// unreliable in slim container images. Override with CHROME_BIN when Chrome lives
// elsewhere (e.g. a Puppeteer download).
const fs = require('fs');

if (!process.env.CHROME_BIN) {
  const candidates = [
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
  ];
  const found = candidates.find((candidate) => fs.existsSync(candidate));
  if (found) {
    process.env.CHROME_BIN = found;
  }
}

module.exports = function (config) {
  config.set({
    basePath: '',
    frameworks: ['jasmine', '@angular-devkit/build-angular'],
    plugins: [
      require('karma-jasmine'),
      require('karma-chrome-launcher'),
      require('karma-jasmine-html-reporter'),
      require('karma-coverage'),
      require('@angular-devkit/build-angular/plugins/karma'),
    ],
    client: {
      jasmine: {
        random: false,
      },
      clearContext: false,
    },
    jasmineHtmlReporter: {
      suppressAll: true,
    },
    coverageReporter: {
      dir: require('path').join(__dirname, './coverage/dnn-migration'),
      subdir: '.',
      reporters: [{ type: 'html' }, { type: 'text-summary' }, { type: 'lcovonly' }],
    },
    reporters: ['progress', 'kjhtml'],
    browsers: ['ChromeHeadlessNoSandbox'],
    customLaunchers: {
      // Gate 4 is specified literally as `--browsers=ChromeHeadless`. Overriding
      // that name here makes the literal gate command work even when the test run
      // happens as root inside a container. NOTE the base is 'Chrome' with explicit
      // headless flags - using base 'ChromeHeadless' would self-reference.
      ChromeHeadless: {
        base: 'Chrome',
        flags: [
          '--headless=new',
          '--no-sandbox',
          '--disable-setuid-sandbox',
          '--disable-gpu',
          '--disable-dev-shm-usage',
          '--remote-debugging-port=9222',
        ],
      },
      ChromeHeadlessNoSandbox: {
        base: 'ChromeHeadless',
        flags: [
          '--no-sandbox',
          '--disable-setuid-sandbox',
          '--disable-gpu',
          '--disable-dev-shm-usage',
          '--headless=new',
          '--remote-debugging-port=9222',
        ],
      },
    },
    browserDisconnectTimeout: 30000,
    browserNoActivityTimeout: 120000,
    captureTimeout: 120000,
    restartOnFileChange: true,
  });
};
