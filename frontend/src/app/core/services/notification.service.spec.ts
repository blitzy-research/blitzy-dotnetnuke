/**
 * @fileoverview Jasmine specification for {@link NotificationService}.
 *
 * The co-located spec for `core/services/notification.service.ts`, satisfying
 * the target architecture's rule of one spec per service and forming part of the
 * Karma suite that the frontend test gate executes as
 * `ng test --watch=false --browsers=ChromeHeadless --code-coverage`.
 *
 * ## This is net-new coverage, not a port
 *
 * The legacy DotNetNuke tree contains no automated tests of any kind - no test
 * project, no test runner configuration and no assertion library - so nothing
 * here was translated from an existing specification. What *was* carried over is
 * the observed legacy *behaviour* that the assertions below pin down, each cited
 * to the file and line range it was measured in.
 *
 * ## Runner and framework
 *
 * Karma with Jasmine, never Jest: the gate command passes `--browsers`, which is
 * a Karma option, so a Jest-based suite could not satisfy it. The workspace
 * pins `karma` 6.4.x, `karma-jasmine` 5.1.x, `jasmine-core` 5.6.x and
 * `@types/jasmine` 5.1.x, and `tsconfig.spec.json` exposes only the `jasmine`
 * type package. Every assertion below is therefore a plain Jasmine `expect`.
 *
 * ## Scope of these specs
 *
 * `NotificationService` is one of the two files under `core/services/` that is
 * not an HTTP wrapper, and it performs no network I/O at all. The testing module
 * is consequently configured empty - no transport provider and no test double
 * for one, because introducing either would imply a dependency the service does
 * not have.
 *
 * Deliberately **not** exercised here, each because it belongs to a different
 * unit:
 *
 * - mapping an HTTP status code onto a severity - that decision lives in the
 *   error interceptor, whose own spec owns it. Note that `core/interceptors/`,
 *   `core/guards/`, `core/state/` and `core/models/` do not exist on this branch
 *   yet, so no cross-unit assertion is possible or attempted.
 *   RFC 7807 payload shapes appear nowhere below for the same reason;
 * - rendering - there is no host component, no template and no DOM assertion.
 *   That the `message` must be bound as text content is the consuming banner
 *   component's contract, proven in that component's own spec;
 * - elapsed time - the service owns no timer, no auto-dismiss window and no
 *   de-duplication window, so no fake-clock control or asynchronous test zone
 *   appears here. Asserting on timing would encode presentation behaviour the
 *   service deliberately does not own.
 *
 * @see ./notification.service.ts - the unit under test.
 * @see Website/admin/Security/AccessDenied.ascx.vb - the legacy denial page that
 *      establishes both the severity rule and the plain-text rule asserted
 *      below.
 */

import { TestBed } from '@angular/core/testing';

import { NotificationService } from './notification.service';
import type { AppNotification, NotificationSeverity } from './notification.service';

/**
 * Every member of the closed severity vocabulary, in the order the service's own
 * union type lists them.
 *
 * Typed as `readonly NotificationSeverity[]` rather than inferred, so that adding
 * a member to the union without adding it here - or misspelling one - is a
 * compile error in this file rather than a silent coverage gap.
 */
const ALL_SEVERITIES: readonly NotificationSeverity[] = ['success', 'info', 'warning', 'error'];

/**
 * The exact wording the legacy denial page displayed when no inbound message was
 * supplied.
 *
 * Sourced, not invented:
 * `Website/admin/Security/AccessDenied.ascx.vb` L45 resolves the `AccessDenied`
 * resource key, and
 * `Website/admin/Security/App_LocalResources/AccessDenied.ascx.resx` L42-L44
 * declares that key's value as the sentence reproduced here.
 */
const LEGACY_ACCESS_DENIED_TEXT =
  'Either you are not currently logged in, or you do not have access to this content.';

/**
 * A hostile message fixture, taken from real repository content rather than
 * imagined.
 *
 * Across the 37 in-scope
 * `Website/admin/{Portal,Users,Security,Modules,Tabs}/App_LocalResources/*.resx`
 * files - 1211 `<data>` entries in total - 76 values carry at least one raw HTML
 * tag, and one of them embeds a live script element: `Advertising.Text` in
 * `Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx` L189-L190,
 * whose opening and closing script tags account for all four script-tag tokens
 * in the corpus.
 *
 * Legacy message wording is therefore untrusted input, which is precisely why the
 * service treats a message as opaque text and why the plain-text specs below
 * exist.
 */
const SCRIPT_ELEMENT_MESSAGE = '<script type="text/javascript">alert(1)</script>';

/**
 * An id that these specs can never have issued.
 *
 * The service's counter starts at 1 and advances by one per queued entry, and no
 * spec below queues anywhere near this many, so this value is guaranteed absent
 * from the queue - which is what makes it a valid probe for the unknown-id path.
 */
const UNISSUED_ID = 4242;

describe('NotificationService', () => {
  let service: NotificationService;

  beforeEach(() => {
    // An empty testing module is the honest setup, not an oversight. The service
    // is registered `providedIn: 'root'`, injects nothing and issues no request,
    // so there is no provider to override and no transport harness to install.
    // Angular resets the testing injector before each spec, so every `it` below
    // receives a pristine service whose queue is empty and whose id counter is
    // back at 1.
    TestBed.configureTestingModule({});
    service = TestBed.inject(NotificationService);
  });

  /**
   * Seeds the queue with three entries of distinct severities and returns the
   * resulting snapshot, oldest first.
   *
   * Shared by the dismissal, clearing and immutability specs so that each states
   * only the behaviour it is actually asserting.
   *
   * @returns The queue immediately after seeding.
   */
  const seedThreeEntries = (): readonly AppNotification[] => {
    service.notify('success', 'first');
    service.notify('warning', 'second');
    service.notify('error', 'third');

    return service.notifications();
  };

  describe('provisioning', () => {
    it('instantiates through the root injector', () => {
      expect(service).toBeInstanceOf(NotificationService);
    });

    it('resolves to one shared instance per injector', () => {
      // The application-singleton guarantee that lets unrelated producers - an
      // interceptor and a feature component - enqueue into the same queue.
      expect(TestBed.inject(NotificationService)).toBe(service);
    });

    it('starts with an empty queue', () => {
      expect(service.notifications()).toEqual([]);
    });
  });

  describe('notify', () => {
    it('appends exactly one entry per call', () => {
      service.notify('error', 'Something failed');

      expect(service.notifications().length).toBe(1);
    });

    it('carries the severity it was given', () => {
      service.notify('error', 'Something failed');

      expect(service.notifications()[0].severity).toBe('error');
    });

    it('carries the message it was given', () => {
      service.notify('error', 'Something failed');

      expect(service.notifications()[0].message).toBe('Something failed');
    });

    it('stores the message verbatim, without trimming or reformatting', () => {
      // Surrounding whitespace, an interior tab, a line break and mixed case are
      // all preserved. Normalising any of them would be a display decision, and
      // display decisions belong to the consuming component.
      const untouched = '   Mixed CASE,\ttabbed and\nbroken across lines.   ';

      service.notify('info', untouched);

      expect(service.notifications()[0].message).toBe(untouched);
    });

    it('queues an empty message as the empty string', () => {
      // The legacy model had no distinct "absent string": the sentinel for a
      // missing string was the empty string itself -
      // `Library/Components/Shared/Null.vb` L71-L75 returns `""` from
      // `NullString`. So an empty message is asserted as `''` exactly, never
      // coalesced into a stand-in value and never treated as absent.
      service.notify('info', '');

      expect(service.notifications()[0].message).toBe('');
    });

    it('appends in call order', () => {
      service.notify('info', 'first');
      service.notify('info', 'second');
      service.notify('info', 'third');

      expect(service.notifications().map((entry) => entry.message)).toEqual([
        'first',
        'second',
        'third',
      ]);
    });

    it('keeps two identical calls as two separate entries', () => {
      // No de-duplication window exists, by design: collapsing repeats would
      // hide a genuine second failure from the user.
      service.notify('warning', LEGACY_ACCESS_DENIED_TEXT);
      service.notify('warning', LEGACY_ACCESS_DENIED_TEXT);

      expect(service.notifications().length).toBe(2);
    });
  });

  describe('severity vocabulary', () => {
    it('expresses every member of the closed vocabulary', () => {
      ALL_SEVERITIES.forEach((severity, index) => {
        service.notify(severity, `${severity} raised`);

        expect(service.notifications()[index].severity).toBe(severity);
      });

      expect(service.notifications().length).toBe(ALL_SEVERITIES.length);
    });

    it("expresses an authorisation denial at 'warning' rather than 'error'", () => {
      // MIGRATION: a denial is a warning, and that is measured rather than
      // assumed. `Website/admin/Security/AccessDenied.ascx.vb` is 50 lines and
      // its `Page_Load` (L41-L47) contains no permission check at all - it only
      // presents a denial decided elsewhere. Both of its branches render at
      // `ModuleMessage.ModuleMessageType.YellowWarning`: L43 for an inbound
      // query-string message and L45 for the localised fallback reproduced in
      // LEGACY_ACCESS_DENIED_TEXT above.
      //
      // Choosing the severity for a given status code is the error interceptor's
      // job, not this service's. This spec proves only that the vocabulary can
      // express the legacy outcome faithfully - which is why 'warning' has to be
      // a first-class member and not a synonym for 'error'.
      service.notify('warning', LEGACY_ACCESS_DENIED_TEXT);

      const entry: AppNotification = service.notifications()[0];

      expect(entry.severity).toBe('warning');
      expect(entry.message).toBe(LEGACY_ACCESS_DENIED_TEXT);
    });
  });

  describe('identifiers', () => {
    it('issues ids that start at 1 and advance by one', () => {
      service.notify('info', 'first');
      service.notify('info', 'second');
      service.notify('info', 'third');

      // Concrete values are asserted, not merely relative ordering, because the
      // ids are deterministic by design: the service counts, rather than drawing
      // from a random source or a clock reading. That determinism is what keeps
      // this assertion exact and keeps template track keys stable.
      expect(service.notifications().map((entry) => entry.id)).toEqual([1, 2, 3]);
    });

    it('issues a distinct id to every entry, even for identical messages', () => {
      service.notify('info', 'repeated');
      service.notify('info', 'repeated');

      const ids = service.notifications().map((entry) => entry.id);

      expect(new Set(ids).size).toBe(ids.length);
    });

    it('never reissues an id that a cleared entry already used', () => {
      service.notify('info', 'discarded');

      service.clear();
      service.notify('info', 'fresh');

      // Clearing empties the queue but deliberately does not rewind the counter,
      // so an id captured from a dismissed entry can never collide with a later
      // one. The first entry after a clear is therefore id 2, not id 1.
      expect(service.notifications()[0].id).toBe(2);
    });
  });

  describe('dismiss', () => {
    it('removes only the entry whose id was passed', () => {
      const seeded = seedThreeEntries();
      const middle: AppNotification = seeded[1];

      service.dismiss(middle.id);

      expect(service.notifications().map((entry) => entry.id)).toEqual([
        seeded[0].id,
        seeded[2].id,
      ]);
    });

    it('preserves the order of the surviving entries', () => {
      const seeded = seedThreeEntries();

      service.dismiss(seeded[0].id);

      expect(service.notifications().map((entry) => entry.message)).toEqual(['second', 'third']);
    });

    it('leaves the queue contents unchanged for an id that was never issued', () => {
      const before = seedThreeEntries();

      service.dismiss(UNISSUED_ID);

      // Compared by value, never by reference. `dismiss` filters
      // unconditionally, so it publishes a fresh array even when nothing
      // matched; asserting reference identity here would pin down an allocation
      // detail the service does not promise, and the spec would fail for a
      // reason that has nothing to do with the behaviour under test.
      expect(service.notifications()).toEqual(before);
    });

    it('does not throw for an id that was never issued', () => {
      service.notify('info', 'kept');

      // A consumer racing a dismissal against a clear must not be punished for
      // it, so an absent id is a tolerated no-op rather than an error.
      expect(() => service.dismiss(UNISSUED_ID)).not.toThrow();
    });

    it('leaves an already-empty queue empty', () => {
      service.dismiss(UNISSUED_ID);

      expect(service.notifications()).toEqual([]);
    });
  });

  describe('clear', () => {
    it('empties a populated queue', () => {
      seedThreeEntries();

      service.clear();

      expect(service.notifications()).toEqual([]);
    });

    it('publishes the same empty reference when the queue is already empty', () => {
      const before = service.notifications();

      service.clear();

      // Unlike a dismissal, clearing restores one shared frozen empty queue, so
      // clearing an already-empty queue changes no reference. Under the signal's
      // default identity comparison that means no dependent is notified, making
      // a redundant clear a true no-op rather than a spurious re-render.
      expect(service.notifications()).toBe(before);
    });
  });

  describe('immutability', () => {
    it('replaces the queue instead of mutating it in place', () => {
      const before = service.notifications();

      service.notify('info', 'appended');

      // Load-bearing, not decorative. A snapshot a consumer already read stays
      // exactly as it was, and the next read yields a different array. That pair
      // of facts is what makes components using the on-push change-detection
      // strategy re-render reliably: an in-place push would leave the reference
      // identical and the view stale.
      expect(before.length).toBe(0);
      expect(service.notifications()).not.toBe(before);
    });

    it('publishes a fresh reference for a dismissal as well as an append', () => {
      const before = seedThreeEntries();

      service.dismiss(before[0].id);

      expect(service.notifications()).not.toBe(before);
    });

    it('leaves the entries it carries over untouched', () => {
      const before = seedThreeEntries();
      const survivor: AppNotification = before[2];

      service.dismiss(before[0].id);

      // Entries are immutable value objects that are carried across, never
      // rebuilt, so a consumer holding one keeps a valid reference.
      expect(service.notifications()[1]).toBe(survivor);
    });
  });

  describe('plain-text handling', () => {
    it('stores a script element as opaque text, character for character', () => {
      // MIGRATION: the service neither escapes, strips, sanitises nor wraps a
      // message, because it never treats one as markup in the first place. The
      // legacy page took the same position from the other direction: at
      // `Website/admin/Security/AccessDenied.ascx.vb` L43 the inbound value is
      // wrapped in `HttpUtility.HtmlEncode(HttpUtility.UrlDecode(...))` before
      // display - encoded, not rendered.
      //
      // The defence therefore lives entirely at the render boundary, where
      // Angular's default text interpolation escapes the value. This spec makes
      // no trusted-markup assertion, imports no sanitisation API and inserts
      // nothing into the document, precisely because doing any of those would
      // contradict the contract being pinned down here.
      service.notify('error', SCRIPT_ELEMENT_MESSAGE);

      expect(service.notifications()[0].message).toBe(SCRIPT_ELEMENT_MESSAGE);
    });

    it('neither escapes nor decodes inline markup and character entities', () => {
      // A tag that survives as a tag, an entity that survives as an entity, and
      // an angle-bracket pair that is neither expanded nor collapsed. Any
      // transformation in either direction would show up here.
      const mixed = '<b>bold</b> &amp; &lt;kept&gt;';

      service.notify('warning', mixed);

      expect(service.notifications()[0].message).toBe(mixed);
    });
  });

  describe('severity aliases', () => {
    it("success() delegates to notify with 'success' and adds nothing else", () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.success('Portal saved.');

      expect(notify).toHaveBeenCalledOnceWith('success', 'Portal saved.');
    });

    it("info() delegates to notify with 'info' and adds nothing else", () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.info('Import complete.');

      expect(notify).toHaveBeenCalledOnceWith('info', 'Import complete.');
    });

    it("warning() delegates to notify with 'warning' and adds nothing else", () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.warning(LEGACY_ACCESS_DENIED_TEXT);

      expect(notify).toHaveBeenCalledOnceWith('warning', LEGACY_ACCESS_DENIED_TEXT);
    });

    it("error() delegates to notify with 'error' and adds nothing else", () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.error('Something failed');

      expect(notify).toHaveBeenCalledOnceWith('error', 'Something failed');
    });

    it('queues one entry per alias, each at its own severity and in call order', () => {
      service.success('saved');
      service.info('noted');
      service.warning('denied');
      service.error('failed');

      expect(service.notifications().map((entry) => entry.severity)).toEqual([
        'success',
        'info',
        'warning',
        'error',
      ]);
    });

    it('passes an alias message through as verbatim as notify does', () => {
      service.warning(SCRIPT_ELEMENT_MESSAGE);

      expect(service.notifications()[0].message).toBe(SCRIPT_ELEMENT_MESSAGE);
    });
  });
});

