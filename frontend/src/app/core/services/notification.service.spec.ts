import { TestBed } from '@angular/core/testing';

import { NotificationService } from './notification.service';
import type { AppNotification, NotificationSeverity } from './notification.service';

const ALL_SEVERITIES: readonly NotificationSeverity[] = ['success', 'info', 'warning', 'error'];

const LEGACY_ACCESS_DENIED_TEXT =
  'Either you are not currently logged in, or you do not have access to this content.';

const SCRIPT_ELEMENT_MESSAGE = '<script type="text/javascript">alert(1)</script>';

const UNISSUED_ID = 4242;

/** The message-length bound the service documents on its own `MAX_MESSAGE_LENGTH` constant. */
const MAX_MESSAGE_LENGTH = 1024;

/** The queue-depth bound the service documents on its own `MAX_QUEUED_NOTIFICATIONS` constant. */
const MAX_QUEUED_NOTIFICATIONS = 25;

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
      service.notify('info', '');

      expect(service.notifications()).toEqual([]);
    });

    it('refuses a whitespace-only message, in every spelling of blank', () => {
      for (const blank of ['', ' ', '   ', '\t', '\n', '\r\n', ' \t \n ']) {
        service.notify('info', blank);
      }

      expect(service.notifications()).toEqual([]);
    });

    it('keeps a padded but nonblank message, padding intact', () => {
      service.notify('info', ' kept ');

      expect(service.notifications().length).toBe(1);
      expect(service.notifications()[0].message).toBe(' kept ');
    });

    it('does not consume an id for a refused message', () => {
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

    it('collapses an immediate repetition into one row, and re-issues it', () => {
      service.notify('warning', LEGACY_ACCESS_DENIED_TEXT);

      const first = service.notifications()[0].id;

      service.notify('warning', LEGACY_ACCESS_DENIED_TEXT);

      expect(service.notifications().length).withContext('one row, not two').toBe(1);

      // RE-ISSUED rather than left alone: a person who caused the outcome a second time must be told a
      // second time, and a live region announces an entry by its identity, so the entry has to be a new one
      // for the second occurrence to be announced at all.
      expect(service.notifications()[0].id).withContext('a fresh identifier').not.toBe(first);
      expect(service.notifications()[0].message).toBe(LEGACY_ACCESS_DENIED_TEXT);
    });

    it('collapses only the NEWEST entry, so a repeat across other outcomes is kept', () => {
      // The entries in between are evidence that something else happened, and collapsing across
      // them would reorder the record of what the operator did.
      service.notify('warning', LEGACY_ACCESS_DENIED_TEXT);
      service.notify('info', 'something else happened');
      service.notify('warning', LEGACY_ACCESS_DENIED_TEXT);

      expect(service.notifications().map((entry) => entry.message)).toEqual([
        LEGACY_ACCESS_DENIED_TEXT,
        'something else happened',
        LEGACY_ACCESS_DENIED_TEXT,
      ]);
    });

    it('keeps a repeat that carries a different support reference', () => {
      // Same sentence, different incident: the reference is the thing an operator quotes, so two
      // references are two reports and both must survive.
      service.error('The request could not be completed.', 'reference-one');
      service.error('The request could not be completed.', 'reference-two');

      expect(service.notifications().map((entry) => entry.reference)).toEqual([
        'reference-one',
        'reference-two',
      ]);
    });

    it('keeps a repeat at a different severity', () => {
      service.notify('warning', 'the same words');
      service.notify('error', 'the same words');

      expect(service.notifications().map((entry) => entry.severity)).toEqual([
        'warning',
        'error',
      ]);
    });

    it('keeps a repeat that differs in whether it outlives a navigation', () => {
      // ⚠ THIS MEMBER WAS MISSING FROM THE IDENTITY TEST, AND THE CONSEQUENCE WAS A LOST MESSAGE. It is
      // stored on the entry and it governs whether the sweep discards it, so two entries agreeing on every
      // other member are still different reports when one claims the exemption and the other does not - and
      // collapsing them imposed the FIRST one's opinion on the second.
      service.notify('warning', 'the same words', null, false);
      service.notify('warning', 'the same words', null, true);

      expect(service.notifications().map((entry) => entry.survivesNavigation))
        .withContext('two lifetimes are two reports, exactly as two references are')
        .toEqual([false, true]);
    });

    it('collapses a repeat that agrees on the navigation opinion too', () => {
      // The narrowing above must not become a licence to keep every repeat. Two entries agreeing on every
      // stored member are still one report and are still collapsed, which is what keeps a genuinely
      // duplicated raise from appearing twice.
      service.notify('warning', 'the same words', null, true);
      service.notify('warning', 'the same words', null, true);

      expect(service.notifications().length).toBe(1);
      expect(service.notifications()[0]?.survivesNavigation).toBeTrue();
    });
  });

  // =========================================================================
  // QA-26 — SUPERSEDING BY OPERATION
  // =========================================================================

  describe('the operation scope', () => {
    /**
     * ⚠ THE CASE THE IDENTICAL-NEIGHBOUR COLLAPSE COULD NEVER CATCH. Two DIFFERENT sentences reporting
     * the SAME operation are two answers to one question, and only the later one is still true. Measured on
     * the module-import screen: choosing an oversized file and then an unreadable one left both refusals on
     * screen at once, beside that screen's own banner, so the operator had to work out which of the two
     * described the file they had actually selected.
     */
    it('retires an earlier report of the same operation, however it was worded', () => {
      service.notify('error', 'The file is too large.', null, false, null, 'import:file');
      service.notify('error', 'The file could not be read.', null, false, null, 'import:file');

      const queue = service.notifications();

      expect(queue).withContext('one operation, one current answer').toHaveSize(1);
      expect(queue[0]?.message).toBe('The file could not be read.');
    });

    it('retires it wherever it sits, not only when it is the newest entry', () => {
      service.notify('error', 'The file is too large.', null, false, null, 'import:file');
      service.info('Something unrelated happened.');
      service.notify('error', 'The file could not be read.', null, false, null, 'import:file');

      const messages = service.notifications().map((entry) => entry.message);

      expect(messages)
        .withContext('the unrelated entry survives; the superseded one does not')
        .toEqual(['Something unrelated happened.', 'The file could not be read.']);
    });

    it('keeps two different operations apart, because both are still true', () => {
      service.notify('error', 'The save was refused.', null, false, null, 'settings:save');
      service.notify('error', 'The delete was refused.', null, false, null, 'settings:delete');

      expect(service.notifications()).toHaveSize(2);
    });

    /**
     * The default is `null`, so nothing that has not opted in can be retired by anything — which is what
     * makes this change invisible to every call site that was not measured to need it.
     */
    it('never lets an unscoped report retire another, in either direction', () => {
      service.error('First unscoped failure.');
      service.error('Second unscoped failure.');
      service.notify('error', 'A scoped failure.', null, false, null, 'a:b');
      service.error('Third unscoped failure.');

      expect(service.notifications()).toHaveSize(4);
      expect(service.notifications().every((entry) => entry.scope === null)).toBeFalse();
    });

    it('publishes the scope it was given, and null when it was given none', () => {
      service.error('Scoped.', null, 'x:y');
      service.error('Unscoped.');

      expect(service.notifications()[0]?.scope).toBe('x:y');
      expect(service.notifications()[1]?.scope).toBeNull();
    });

    it('carries the scope through every severity alias', () => {
      service.success('Saved.', false, 's:1');
      service.info('Noted.', false, 'i:1');
      service.warning('Careful.', false, 'w:1');
      service.error('Refused.', null, 'e:1');

      expect(service.notifications().map((entry) => entry.scope)).toEqual([
        's:1',
        'i:1',
        'w:1',
        'e:1',
      ]);
    });

    it('lets a later success retire the refusal of the same operation', () => {
      service.error('The save was refused.', null, 'settings:save');
      service.success('Saved.', false, 'settings:save');

      const queue = service.notifications();

      expect(queue).toHaveSize(1);
      expect(queue[0]?.severity)
        .withContext('a refusal that has since succeeded is no longer an answer to anything')
        .toBe('success');
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

      // Identity, not value: a redundant clear has to publish the very same reference for the signal's
      // default comparison to suppress the notification, so toBe is the only assertion with meaning here.
      expect(service.notifications()).toBe(before);
    });
  });

  // The screen-lifetime rule, which exists because two severities deliberately never expire on a timer.
  describe('dismissStale', () => {
    it('discards the entries that described the screen just left', () => {
      service.warning('Correct the highlighted fields.');
      service.error('The request could not be processed.');

      service.dismissStale();

      expect(service.notifications())
        .withContext('neither severity expires on its own, so this is the only thing that removes them')
        .toEqual([]);
    });

    it('spends a reprieve rather than leaving it set, so it is worth exactly one change of screen', () => {
      service.success('The user account was created.', true);

      service.dismissStale();

      const [survivor] = service.notifications();

      expect(survivor)
        .withContext('it survives the navigation it was raised for')
        .not.toBeUndefined();
      expect(survivor.message).toBe('The user account was created.');
      expect(survivor.survivesNavigation)
        .withContext('but the flag is now spent, so a further change of screen discards it')
        .toBeFalse();

      service.dismissStale();

      expect(service.notifications())
        .withContext('a permanent flag would simply be the same leak under a nicer name')
        .toEqual([]);
    });

    it('keeps identity and order for survivors', () => {
      service.success('first', true);
      service.warning('discarded');
      service.info('second', true);

      const ids = service.notifications().map((entry) => entry.id);

      service.dismissStale();

      const survivors = service.notifications();

      expect(survivors.map((entry) => entry.message))
        .withContext('order is preserved as the discarded entry is removed from between them')
        .toEqual(['first', 'second']);
      // Identity matters because a dismissal timer may already be armed against an id; a survivor
      // that were re-keyed would leave that timer unable to find it.
      expect(survivors.map((entry) => entry.id)).toEqual([ids[0], ids[2]]);
    });

    it('publishes the same empty reference when there was nothing to discard', () => {
      const before = service.notifications();

      service.dismissStale();

      // The same identity requirement `clear` carries, and for the same reason: navigating with an
      // empty queue is the common case and must notify nobody.
      expect(service.notifications()).toBe(before);
    });
  });

  describe('immutability', () => {
    it('replaces the queue instead of mutating it in place', () => {
      const before = service.notifications();

      service.notify('info', 'appended');

      // Load-bearing for components using the on-push change-detection strategy: a snapshot already read
      // stays as it was, and the next read yields a different array. An in-place push would leave the
      // reference identical and the view stale.
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
      service.notify('error', SCRIPT_ELEMENT_MESSAGE);

      expect(service.notifications()[0].message).toBe(SCRIPT_ELEMENT_MESSAGE);
    });

    it('neither escapes nor decodes inline markup and character entities', () => {
      const mixed = '<b>bold</b> &amp; &lt;kept&gt;';

      service.notify('warning', mixed);

      expect(service.notifications()[0].message).toBe(mixed);
    });
  });

  /**
   * ⚠ THESE CASES ASSERT THE DELEGATION ARGUMENTS EXACTLY, so widening `notify` widened them too. Each
   * alias now forwards the lifetime opinion and the operation scope as well — both `null` when the caller
   * supplied neither, which is what keeps the aliases' own defaults identical to what they were. The
   * explicit `null`s are the point rather than noise: an alias silently dropping a parameter it was given
   * is exactly the drift this shape of assertion exists to catch.
   */
  describe('severity aliases', () => {
    it("success() delegates to notify with 'success' and adds nothing else", () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.success('Portal saved.');

      expect(notify).toHaveBeenCalledOnceWith('success', 'Portal saved.', null, false, null, null);
    });

    it("info() delegates to notify with 'info' and adds nothing else", () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.info('Import complete.');

      expect(notify).toHaveBeenCalledOnceWith('info', 'Import complete.', null, false, null, null);
    });

    it("warning() delegates to notify with 'warning' and adds nothing else", () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.warning(LEGACY_ACCESS_DENIED_TEXT);

      expect(notify).toHaveBeenCalledOnceWith(
        'warning',
        LEGACY_ACCESS_DENIED_TEXT,
        null,
        false,
        null,
        null,
      );
    });

    it('forwards a requested reprieve, and only when it is requested', () => {
      service.success('Saved and leaving.', true);
      service.warning('Stays put.');

      const [reprieved, ordinary] = service.notifications();

      expect(reprieved.survivesNavigation)
        .withContext('the caller asked for it, so the entry carries it')
        .toBeTrue();
      expect(ordinary.survivesNavigation)
        .withContext('and an alias called the ordinary way must never grant one')
        .toBeFalse();
    });

    it("error() delegates to notify with 'error' and adds nothing else", () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.error('Something failed');

      expect(notify).toHaveBeenCalledOnceWith('error', 'Something failed', null, false, null, null);
    });

    it('error() forwards a support reference when one is supplied', () => {
      const notify = spyOn(service, 'notify').and.callThrough();

      service.error('Something failed', 'abc123');

      expect(notify).toHaveBeenCalledOnceWith('error', 'Something failed', 'abc123', false, null, null);
      expect(service.notifications()[0].reference)
        .withContext('the reference is retained as its own member, not only in the text')
        .toBe('abc123');
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
    // A notification message is not always composed by this application. The error interceptor builds one
    // from a server `ProblemDetails` payload, whose `detail` and `errors` members are remote input, so
    // message length is not under the application's control.

    it('stores a message of exactly the bound in full', () => {
      const atBound = 'a'.repeat(MAX_MESSAGE_LENGTH);

      service.notify('error', atBound);

      // The bound is inclusive: at the limit nothing is removed, so the fixtures above - all far shorter -
      // are untouched by it and every verbatim spec in this file continues to describe real behaviour.
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

  describe('support reference', () => {
    it('survives truncation of an over-long message', () => {
      const overLong = 'a'.repeat(MAX_MESSAGE_LENGTH * 4);

      service.notify('error', overLong, 'trace-0001');

      const stored: AppNotification = service.notifications()[0];

      expect(stored.reference)
        .withContext('the reference is a member of its own and cannot be truncated away')
        .toBe('trace-0001');
      expect(stored.message.endsWith('If you report this, quote reference trace-0001.'))
        .withContext('the reference is appended AFTER the message is bounded')
        .toBeTrue();
      expect(stored.message.startsWith('a'.repeat(MAX_MESSAGE_LENGTH)))
        .withContext('the whole bounded message is retained ahead of the reference')
        .toBeTrue();
    });

    it('composes the label exactly once, with a single separating space', () => {
      service.notify('error', 'Something failed.', 'abc123');

      expect(service.notifications()[0].message).toBe(
        'Something failed. If you report this, quote reference abc123.',
      );
    });

    it('reports no reference when none is supplied', () => {
      service.notify('error', 'Something failed.');

      const stored: AppNotification = service.notifications()[0];

      expect(stored.reference).toBeNull();
      expect(stored.message).toBe('Something failed.');
    });

    it('treats a blank reference as absent rather than quoting an empty one', () => {
      service.notify('error', 'Something failed.', '   ');

      const stored: AppNotification = service.notifications()[0];

      // A dangling `Reference:` label with nothing after it would instruct an operator
      // to report an identifier that was never issued.
      expect(stored.reference).toBeNull();
      expect(stored.message).toBe('Something failed.');
    });

    it('refuses a blank message even when a reference accompanies it', () => {
      service.notify('error', '   ', 'abc123');

      // A notification reading only `Reference: abc123` says nothing about what
      // happened, so the blank-message refusal takes precedence over the reference.
      expect(service.notifications()).toEqual([]);
    });

    it('bounds an over-long reference so the queue stays finite', () => {
      // The reference is remote input on the same footing as the message: it is read
      // from a response body, and a proxy is free to put anything there.
      service.notify('error', 'Something failed.', 'r'.repeat(4096));

      const stored: AppNotification = service.notifications()[0];

      expect(stored.reference?.length).toBe(128);
      expect(stored.message).toBe(
        `Something failed. If you report this, quote reference ${'r'.repeat(128)}.`,
      );
    });
  });

  describe('queue depth bound', () => {
    /**
     * Queues `count` entries whose messages are their 1-based call ordinals, so that the retained window
     * can be identified precisely afterwards.
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

      // The very first entry is gone and the newest is present: eviction is oldest-first, so the queue is a
      // window over the most recent notifications rather than a snapshot of the earliest ones.
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

      // Eviction must not become a hidden in-place mutation: an `OnPush` consumer that already read the
      // queue depends on the reference changing, and on the snapshot it holds staying exactly as it was.
      expect(service.notifications()).not.toBe(before);
      expect(before.length).toBe(MAX_QUEUED_NOTIFICATIONS);
      expect(before[0].message).toBe('1');
    });
  });
});
