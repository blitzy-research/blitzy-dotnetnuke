import { TestBed } from '@angular/core/testing';

import { NotificationService } from './notification.service';
import type { AppNotification, NotificationSeverity } from './notification.service';

const ALL_SEVERITIES: readonly NotificationSeverity[] = ['success', 'info', 'warning', 'error'];

const LEGACY_ACCESS_DENIED_TEXT =
  'Either you are not currently logged in, or you do not have access to this content.';

const SCRIPT_ELEMENT_MESSAGE = '<script type="text/javascript">alert(1)</script>';

const UNISSUED_ID = 4242;

/**
 * The message-length bound the service documents on its own `MAX_MESSAGE_LENGTH`
 * constant.
 *
 * Mirrored here rather than imported: the service keeps its bounds module-private,
 * exactly as it keeps its frozen empty queue private, and widening its exported
 * surface merely to be observable from a spec would be the wrong trade. Restating
 * the figure means a deliberate change to the bound has to be made in both places,
 * which is the intent - an accidental change fails these specs loudly.
 *
 * The figure itself is measured, not chosen: across the 40 in-scope resource files
 * (1561 plain `<data>` values) legacy admin wording runs to a 99th percentile of
 * 387 characters, so 1024 clears every real message value by a wide margin.
 */
const MAX_MESSAGE_LENGTH = 1024;

/**
 * The queue-depth bound the service documents on its own
 * `MAX_QUEUED_NOTIFICATIONS` constant. Mirrored here for the same reason as
 * {@link MAX_MESSAGE_LENGTH}.
 *
 * The legacy `AddModuleMessage` surface rendered one module message per page
 * render, so 25 is an order of magnitude more than any legacy screen ever showed.
 */
const MAX_QUEUED_NOTIFICATIONS = 25;

/**
 * A single astral-plane character, which JavaScript stores as a surrogate *pair* -
 * two UTF-16 code units, so `'\u{1F600}'.length === 2`.
 *
 * Used to prove that applying a code-unit bound never leaves half a pair behind.
 * A lone surrogate is not a representable character, so a consumer rendering it as
 * text content would show U+FFFD in place of the final character.
 */
const ASTRAL_CHARACTER = '\u{1F600}';

describe('NotificationService', () => {
  let service: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(NotificationService);
  });

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
      const untouched = '   Mixed CASE,\ttabbed and\nbroken across lines.   ';

      service.notify('info', untouched);

      expect(service.notifications()[0].message).toBe(untouched);
    });

    it('refuses an empty message instead of queueing it', () => {
      // The legacy model had no distinct "absent string": the sentinel for a
      // missing string was the empty string itself -
      // `Library/Components/Shared/Null.vb` L71-L75 returns `""` from
      // `NullString`. That is precisely why `''` is refused rather than stored:
      // it is the legacy representation of ABSENT, so a caller passing it is
      // reporting that it has no message, and the faithful response to "no
      // message" is to raise no notification at all.
      service.notify('info', '');

      expect(service.notifications()).toEqual([]);
    });

    it('refuses a whitespace-only message, in every spelling of blank', () => {
      // Whitespace-only input is absent in exactly the same sense as `''`: it
      // has no readable content and would render as an empty alert. Each of
      // these is refused independently, so no single spelling of blank slips
      // through a check written against another.
      for (const blank of ['', ' ', '   ', '\t', '\n', '\r\n', ' \t \n ']) {
        service.notify('info', blank);
      }

      expect(service.notifications()).toEqual([]);
    });

    it('keeps a padded but nonblank message, padding intact', () => {
      // The emptiness test runs against a trimmed copy while the ORIGINAL is
      // stored, so surrounding whitespace on a message that does have content is
      // preserved rather than being collateral damage of the blank check.
      service.notify('info', ' kept ');

      expect(service.notifications().length).toBe(1);
      expect(service.notifications()[0].message).toBe(' kept ');
    });

    it('does not consume an id for a refused message', () => {
      // The counter's contract is that it never REISSUES an id, not that it
      // counts call attempts. Leaving it untouched on a refusal keeps the ids of
      // real entries gapless, so a rejected call cannot be mistaken for, or
      // inferred as, a dismissal.
      service.notify('info', '   ');
      service.notify('info', '');
      service.notify('info', 'first real message');

      expect(service.notifications().map((entry) => entry.id)).toEqual([1]);
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
      // Pins the vocabulary rather than the mapping: which severity a given
      // failure earns is the caller's decision, so all this proves is that a
      // denial can be expressed as a warning and not only as an error.
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

      // Clearing empties the queue without rewinding the counter, so the first
      // entry after a clear is id 2 rather than id 1.
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

      // Compared by value, never by reference: dismiss filters unconditionally
      // and so publishes a fresh array even when nothing matched, and that
      // allocation is a detail the service does not promise.
      expect(service.notifications()).toEqual(before);
    });

    it('does not throw for an id that was never issued', () => {
      service.notify('info', 'kept');

      // A consumer racing a dismissal against a clear must not be punished for
      // it, so an absent id has to be tolerated rather than raise.
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

      // Identity, not value: a redundant clear has to publish the very same
      // reference for the signal's default comparison to suppress the
      // notification, so toBe is the only assertion with meaning here.
      expect(service.notifications()).toBe(before);
    });
  });

  describe('immutability', () => {
    it('replaces the queue instead of mutating it in place', () => {
      const before = service.notifications();

      service.notify('info', 'appended');

      // Load-bearing for components using the on-push change-detection strategy:
      // a snapshot already read stays as it was, and the next read yields a
      // different array. An in-place push would leave the reference identical
      // and the view stale.
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

      expect(service.notifications()[1]).toBe(survivor);
    });
  });

  describe('plain-text handling', () => {
    it('stores a script element as opaque text, character for character', () => {
      // The queue neither escapes nor sanitises, because it never treats a
      // message as markup; the defence lives at the render boundary, where text
      // interpolation escapes the value. This spec therefore asserts storage
      // only - it imports no sanitisation API and inserts nothing into the
      // document.
      service.notify('error', SCRIPT_ELEMENT_MESSAGE);

      expect(service.notifications()[0].message).toBe(SCRIPT_ELEMENT_MESSAGE);
    });

    it('neither escapes nor decodes inline markup and character entities', () => {
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

  describe('message length bound', () => {
    // A notification message is not always composed by this application. The error
    // interceptor builds one from a server `ProblemDetails` payload, whose `detail`
    // and `errors` members are remote input, so message length is not under the
    // application's control. These specs pin the bound that closes that exposure.

    it('stores a message of exactly the bound in full', () => {
      const atBound = 'a'.repeat(MAX_MESSAGE_LENGTH);

      service.notify('error', atBound);

      // The bound is inclusive: at the limit nothing is removed, so the fixtures
      // above - all far shorter - are untouched by it and every verbatim spec in
      // this file continues to describe real behaviour.
      expect(service.notifications()[0].message).toBe(atBound);
    });

    it('retains only the leading bounded prefix of an over-long message', () => {
      const overLong = 'a'.repeat(MAX_MESSAGE_LENGTH * 4);

      service.notify('error', overLong);

      expect(service.notifications()[0].message.length).toBe(MAX_MESSAGE_LENGTH);
    });

    it('truncates by prefix alone, adding no ellipsis or other marker', () => {
      const overLong = `${'a'.repeat(MAX_MESSAGE_LENGTH)}TAIL-THAT-MUST-NOT-SURVIVE`;

      service.notify('error', overLong);

      // Asserted as an exact slice rather than merely "starts with", because the
      // `AppNotification.message` contract forbids substitution: a truncated
      // message must contain nothing the caller did not supply. An appended
      // marker would also be a display decision, which belongs to the consumer.
      expect(service.notifications()[0].message).toBe(overLong.slice(0, MAX_MESSAGE_LENGTH));
    });

    it('does not leave half a surrogate pair when the cut falls inside one', () => {
      // The pair straddles the boundary: its high half sits at the last retained
      // index and its low half at the first discarded one.
      const filler = 'a'.repeat(MAX_MESSAGE_LENGTH - 1);
      const straddling = `${filler}${ASTRAL_CHARACTER}${'b'.repeat(64)}`;

      service.notify('error', straddling);

      const stored = service.notifications()[0].message;

      // One code unit short of the bound, and the incomplete character is gone
      // rather than retained as an unrepresentable lone surrogate.
      expect(stored).toBe(filler);
      expect(stored.length).toBe(MAX_MESSAGE_LENGTH - 1);
    });

    it('keeps an astral character whole when the whole pair fits inside the bound', () => {
      const filler = 'a'.repeat(MAX_MESSAGE_LENGTH - 2);
      const fitting = `${filler}${ASTRAL_CHARACTER}${'b'.repeat(64)}`;

      service.notify('error', fitting);

      const stored = service.notifications()[0].message;

      // The complementary case to the spec above: stepping back is applied only
      // when it is needed, so a pair that fits is retained in full and the bound
      // is reached exactly.
      expect(stored.length).toBe(MAX_MESSAGE_LENGTH);
      expect(stored.endsWith(ASTRAL_CHARACTER)).toBeTrue();
    });

    it('leaves the severity untouched when it bounds a message', () => {
      service.notify('warning', 'a'.repeat(MAX_MESSAGE_LENGTH * 2));

      expect(service.notifications()[0].severity).toBe('warning');
    });

    it('applies the same bound through the severity aliases', () => {
      service.error('a'.repeat(MAX_MESSAGE_LENGTH * 2));

      // The aliases must remain thin: bounding lives in `notify`, so an alias
      // inherits it rather than carrying a second, possibly divergent, copy.
      expect(service.notifications()[0].message.length).toBe(MAX_MESSAGE_LENGTH);
    });
  });

  describe('queue depth bound', () => {
    /**
     * Queues `count` entries whose messages are their 1-based call ordinals, so
     * that the retained window can be identified precisely afterwards.
     *
     * @param count How many entries to queue.
     */
    const queueSequentially = (count: number): void => {
      for (let ordinal = 1; ordinal <= count; ordinal += 1) {
        service.notify('info', String(ordinal));
      }
    };

    it('holds a queue that has not reached the cap in full', () => {
      queueSequentially(MAX_QUEUED_NOTIFICATIONS - 1);

      // Below the cap the behaviour is indistinguishable from an unbounded append,
      // which is what keeps every other spec in this file valid.
      expect(service.notifications().length).toBe(MAX_QUEUED_NOTIFICATIONS - 1);
      expect(service.notifications()[0].message).toBe('1');
    });

    it('holds exactly the cap once the cap is reached', () => {
      queueSequentially(MAX_QUEUED_NOTIFICATIONS);

      expect(service.notifications().length).toBe(MAX_QUEUED_NOTIFICATIONS);
      expect(service.notifications()[0].message).toBe('1');
    });

    it('never exceeds the cap however many entries are queued', () => {
      queueSequentially(MAX_QUEUED_NOTIFICATIONS * 4);

      expect(service.notifications().length).toBe(MAX_QUEUED_NOTIFICATIONS);
    });

    it('drops the oldest entry as each new one arrives past the cap', () => {
      queueSequentially(MAX_QUEUED_NOTIFICATIONS + 1);

      const messages = service.notifications().map((entry) => entry.message);

      // The very first entry is gone and the newest is present: eviction is
      // oldest-first, so the queue is a window over the most recent notifications
      // rather than a snapshot of the earliest ones.
      expect(messages).not.toContain('1');
      expect(messages[0]).toBe('2');
      expect(messages[messages.length - 1]).toBe(String(MAX_QUEUED_NOTIFICATIONS + 1));
    });

    it('retains exactly the newest window, in call order', () => {
      const total = MAX_QUEUED_NOTIFICATIONS * 2;
      queueSequentially(total);

      const expected = Array.from({ length: MAX_QUEUED_NOTIFICATIONS }, (_unused, index) =>
        String(total - MAX_QUEUED_NOTIFICATIONS + index + 1),
      );

      expect(service.notifications().map((entry) => entry.message)).toEqual(expected);
    });

    it('keeps ids monotonic and never reissued across eviction', () => {
      const total = MAX_QUEUED_NOTIFICATIONS + 5;
      queueSequentially(total);

      const ids = service.notifications().map((entry) => entry.id);

      // Ids count calls, not surviving entries: the counter advances once per
      // `notify` regardless of whether an entry was evicted, so the retained ids
      // are the contiguous tail ending at the total number of calls. Nothing is
      // rewound and nothing is reused, which is what keeps a `@for` track key and
      // a captured `dismiss` argument sound.
      expect(ids).toEqual(
        Array.from({ length: MAX_QUEUED_NOTIFICATIONS }, (_unused, index) =>
          total - MAX_QUEUED_NOTIFICATIONS + index + 1,
        ),
      );
    });

    it('still dismisses by id after eviction has occurred', () => {
      queueSequentially(MAX_QUEUED_NOTIFICATIONS + 3);

      const survivingId = service.notifications()[0].id;
      service.dismiss(survivingId);

      expect(service.notifications().length).toBe(MAX_QUEUED_NOTIFICATIONS - 1);
      expect(service.notifications().map((entry) => entry.id)).not.toContain(survivingId);
    });

    it('ignores an id that eviction already removed', () => {
      queueSequentially(MAX_QUEUED_NOTIFICATIONS + 1);
      const depthAfterFilling = service.notifications().length;

      // Id 1 was evicted rather than dismissed, so it is absent for a different
      // reason than the unknown-id specs cover - the outcome must still be a
      // no-op rather than an error.
      expect(() => service.dismiss(1)).not.toThrow();
      expect(service.notifications().length).toBe(depthAfterFilling);
    });

    it('empties a capped queue on clear without rewinding the counter', () => {
      const total = MAX_QUEUED_NOTIFICATIONS * 2;
      queueSequentially(total);

      service.clear();
      service.notify('info', 'after clear');

      expect(service.notifications().length).toBe(1);
      expect(service.notifications()[0].id).toBe(total + 1);
    });

    it('replaces the queue rather than mutating it when it evicts', () => {
      queueSequentially(MAX_QUEUED_NOTIFICATIONS);
      const before = service.notifications();

      service.notify('info', 'overflowing');

      // Eviction must not become a hidden in-place mutation: an `OnPush` consumer
      // that already read the queue depends on the reference changing, and on the
      // snapshot it holds staying exactly as it was.
      expect(service.notifications()).not.toBe(before);
      expect(before.length).toBe(MAX_QUEUED_NOTIFICATIONS);
      expect(before[0].message).toBe('1');
    });
  });
});
