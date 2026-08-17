import { ComponentFixture, TestBed } from '@angular/core/testing';

import { NotificationListComponent } from './notification-list.component';
import { NotificationService } from '../../core/services/notification.service';

describe('NotificationListComponent', () => {
  let fixture: ComponentFixture<NotificationListComponent>;
  let notifications: NotificationService;

  /** The component's host element. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /** The persistent live region. */
  function region(): HTMLElement | null {
    return host().querySelector<HTMLElement>('div.notification-list');
  }

  /** The rendered rows, in document order. */
  function items(): HTMLElement[] {
    return Array.from(host().querySelectorAll<HTMLElement>('div.notification-list__item'));
  }

  /** The overflow count sentence, or an empty string when none is rendered. */
  function overflowText(): string {
    return host().querySelector('p.notification-list__overflow')?.textContent?.trim() ?? '';
  }

  /** The clear-everything control, or null when it is not offered. */
  function clearAllButton(): HTMLButtonElement | null {
    return host().querySelector<HTMLButtonElement>('button.notification-list__clear-all');
  }

  /** The severity word of one row. */
  function severityWordOf(item: HTMLElement): string {
    return item.querySelector('p.notification-list__severity')?.textContent?.trim() ?? '';
  }

  /** The message of one row. */
  function messageOf(item: HTMLElement): string {
    return item.querySelector('p.notification-list__message')?.textContent?.trim() ?? '';
  }

  /** The dismissal control of one row. */
  function dismissalOf(item: HTMLElement): HTMLButtonElement | null {
    return item.querySelector<HTMLButtonElement>('button.notification-list__dismiss');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NotificationListComponent],
    }).compileComponents();

    notifications = TestBed.inject(NotificationService);

    fixture = TestBed.createComponent(NotificationListComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    notifications.clear();
  });

  describe('construction', () => {
    it('creates', () => {
      expect(fixture.componentInstance).toBeTruthy();
    });

    it('declares the on-push change detection strategy the migration plan mandates', () => {
      const definition = (
        NotificationListComponent as unknown as { ɵcmp?: { onPush?: boolean } }
      ).ɵcmp;

      expect(definition?.onPush).toBeTrue();
    });

    it('is standalone, so the shell can mount it without a module', () => {
      const definition = (
        NotificationListComponent as unknown as { ɵcmp?: { standalone?: boolean } }
      ).ɵcmp;

      expect(definition?.standalone).toBeTrue();
    });
  });

  describe('the live region', () => {
    it('is present before any notification arrives', () => {
      // The property the whole arrangement rests on. A live region inserted at the same moment as its first
      // message is announced inconsistently across screen readers, and the first message is the one that
      // matters — so the region must already exist while the queue is empty.
      expect(region()).not.toBeNull();
      expect(items().length).toBe(0);
    });

    it('announces politely and non-atomically, and says so on the element', () => {
      const live = region();

      expect(live?.getAttribute('role')).toBe('status');
      // Polite rather than assertive: an announcement here reports the outcome of something
      // the operator just did and must not interrupt them mid-sentence.
      expect(live?.getAttribute('aria-live')).toBe('polite');
      // Non-atomic, the opposite of the error banner's choice: entries arrive one at a time,
      // so re-announcing the whole queue on every append would read old messages out again.
      expect(live?.getAttribute('aria-atomic')).toBe('false');
    });

    it('carries an accessible name so it is announced as something', () => {
      expect(region()?.getAttribute('aria-label')).toBe('Notifications');
    });

    it('paints no content of its own while the queue is empty', () => {
      expect(region()?.children.length).toBe(0);
      expect(region()?.textContent?.trim()).toBe('');
    });

    it('keeps the same region element across an append, rather than replacing it', () => {
      const before = region();

      notifications.success('Portal saved.');
      fixture.detectChanges();

      // Identity, not merely presence. A replaced element is a new live region, and a new
      // live region does not reliably announce its initial contents.
      expect(region()).toBe(before);
    });
  });

  describe('rendering the queue', () => {
    it('renders one row per entry, in the order the service holds them', () => {
      notifications.info('First outcome.');
      notifications.warning('Second outcome.');
      notifications.error('Third outcome.');
      fixture.detectChanges();

      const rendered = items();

      expect(rendered.length).toBe(3);
      expect(rendered.map(messageOf)).toEqual([
        'First outcome.',
        'Second outcome.',
        'Third outcome.',
      ]);
    });

    it('states each severity as a word, so the band is perceivable without colour', () => {
      notifications.success('Saved.');
      notifications.info('Noted.');
      notifications.warning('Refused.');
      notifications.error('Failed.');
      fixture.detectChanges();

      expect(items().map(severityWordOf)).toEqual([
        'Success',
        'Information',
        'Warning',
        'Error',
      ]);
    });

    it('states each severity as a data attribute, so one band cannot be applied twice', () => {
      notifications.warning('Refused.');
      fixture.detectChanges();

      expect(items()[0]?.getAttribute('data-severity')).toBe('warning');
    });

    it('renders an immediate repetition as ONE row, because the service collapses it', () => {
      notifications.error('The request could not be completed.');
      notifications.error('The request could not be completed.');
      fixture.detectChanges();

      expect(items().length).toBe(1);
    });

    it('renders a repetition separated by another outcome as two rows', () => {
      notifications.error('The request could not be completed.');
      notifications.info('Something else happened.');
      notifications.error('The request could not be completed.');
      fixture.detectChanges();

      expect(items().length).toBe(3);
    });

    it('renders markup in a message as inert text rather than as elements', () => {
      notifications.error('<script>alert(1)</script> was refused.');
      fixture.detectChanges();

      const row = items()[0];

      expect(row?.querySelector('script')).toBeNull();
      expect(messageOf(row as HTMLElement)).toBe('<script>alert(1)</script> was refused.');
    });
  });

  describe('dismissal', () => {
    it('offers a real button, typed so it cannot submit a surrounding form', () => {
      notifications.info('Noted.');
      fixture.detectChanges();

      const control = dismissalOf(items()[0] as HTMLElement);

      expect(control?.tagName.toLowerCase()).toBe('button');
      expect(control?.getAttribute('type')).toBe('button');
    });

    it('names the control by what it does, and hides the glyph from assistive technology', () => {
      notifications.info('Noted.');
      fixture.detectChanges();

      const control = dismissalOf(items()[0] as HTMLElement);

      expect(control?.getAttribute('aria-label')).toBe('Dismiss this message');
      expect(control?.querySelector('span')?.getAttribute('aria-hidden')).toBe('true');
    });

    it('removes only the activated entry', () => {
      notifications.info('Keep this one.');
      notifications.warning('Dismiss this one.');
      notifications.error('Keep this one too.');
      fixture.detectChanges();

      dismissalOf(items()[1] as HTMLElement)?.click();
      fixture.detectChanges();

      expect(items().map(messageOf)).toEqual(['Keep this one.', 'Keep this one too.']);
    });

    it('leaves the region in place once the last entry is dismissed', () => {
      const before = region();

      notifications.success('Saved.');
      fixture.detectChanges();

      dismissalOf(items()[0] as HTMLElement)?.click();
      fixture.detectChanges();

      expect(items().length).toBe(0);
      expect(region()).toBe(before);
    });

    it('reflects a dismissal performed through the service rather than the control', () => {
      notifications.info('Noted.');
      fixture.detectChanges();

      const [entry] = notifications.notifications();

      notifications.dismiss(entry.id);
      fixture.detectChanges();

      expect(items().length).toBe(0);
    });

    it('reflects a clear of the whole queue', () => {
      notifications.info('One.');
      notifications.info('Two.');
      fixture.detectChanges();

      notifications.clear();
      fixture.detectChanges();

      expect(items().length).toBe(0);
      expect(region()).not.toBeNull();
    });
  });

  // ---------------------------------------------------------------------------
  // THE BOUNDED SURFACE
  // ---------------------------------------------------------------------------

  describe('how much of the screen it may occupy', () => {
    /** Queues `count` warnings, each distinguishable so none is collapsed as a repetition. */
    function queueWarnings(count: number): void {
      for (let index = 1; index <= count; index += 1) {
        notifications.warning(`Refusal number ${index}.`);
      }

      fixture.detectChanges();
    }

    it('renders at most four entries, keeping the newest', () => {
      queueWarnings(9);

      const messages = items().map((item) => messageOf(item));

      expect(messages.length).toBe(4);
      expect(messages).toEqual([
        'Refusal number 6.',
        'Refusal number 7.',
        'Refusal number 8.',
        'Refusal number 9.',
      ]);
    });

    it('keeps the rendered entries in the order the outcomes happened', () => {
      queueWarnings(6);

      // Painting order is reversed by the stylesheet so the newest sits nearest the corner, but the
      // DOM stays oldest-first: that is the order they are announced in and the order Tab follows.
      expect(items().map((item) => messageOf(item))).toEqual([
        'Refusal number 3.',
        'Refusal number 4.',
        'Refusal number 5.',
        'Refusal number 6.',
      ]);
    });

    it('states how many are not shown rather than hiding them silently', () => {
      queueWarnings(7);

      expect(overflowText()).toBe('3 earlier not shown');
    });

    it('states nothing about overflow while every entry is on screen', () => {
      queueWarnings(4);

      expect(host().querySelector('p.notification-list__overflow')).toBeNull();
    });

    it('keeps every queued entry in the service, so nothing off screen is destroyed', () => {
      queueWarnings(7);

      expect(notifications.notifications().length).toBe(7);
    });

    it('offers a single control that clears all of them, from the second entry onwards', () => {
      // Two DISTINCT messages: an immediate repetition of the same sentence is collapsed by the
      // service onto one row, so repeating one here would never produce a second entry.
      notifications.warning('Refusal number 1.');
      fixture.detectChanges();
      expect(clearAllButton()).withContext('one message needs no bulk control').toBeNull();

      notifications.warning('Refusal number 2.');
      fixture.detectChanges();
      const control = clearAllButton();

      expect(control?.textContent?.trim()).toBe('Dismiss all');

      control?.click();
      fixture.detectChanges();

      expect(items().length).toBe(0);
      expect(notifications.notifications().length).toBe(0);
    });
  });

  // ---------------------------------------------------------------------------
  // SELF-DISMISSAL
  // ---------------------------------------------------------------------------

  describe('entries that dismiss themselves', () => {
    // ⚠ ONLY THE OUTCOMES THAT REQUIRE NOTHING OF THE READER. A success and an advisory are
    // acknowledgements of something the operator just caused; leaving them until dismissed is what stranded
    // a role-creation success on the Portals screen after the operator navigated away mid-save.

    const INTERVAL_MS = 8_000;

    beforeEach(() => {
      jasmine.clock().install();
    });

    afterEach(() => {
      jasmine.clock().uninstall();
    });

    it('removes a success once the interval elapses', () => {
      notifications.success('The role was created.');
      fixture.detectChanges();
      expect(items().length).toBe(1);

      jasmine.clock().tick(INTERVAL_MS);
      fixture.detectChanges();

      expect(items().length).toBe(0);
      expect(notifications.notifications().length).withContext('removed from the queue too').toBe(0);
    });

    it('removes an advisory once the interval elapses', () => {
      notifications.info('The notification could not be sent.');
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS);
      fixture.detectChanges();

      expect(items().length).toBe(0);
    });

    it('keeps a warning and an error indefinitely', () => {
      notifications.warning('You are not permitted to view that.');
      notifications.error('The request could not be completed.', 'reference-one');
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS * 10);
      fixture.detectChanges();

      expect(items().length).withContext('a failure is not removed on a timer').toBe(2);
    });

    it('retires a warning that ASKS to be retired, because severity is the fallback and not the answer', () => {
      notifications.notify('warning', 'You do not have permission to view this content.', null, false, true);
      fixture.detectChanges();

      expect(items().length).withContext('shown first, then retired') .toBe(1);

      jasmine.clock().tick(INTERVAL_MS);
      fixture.detectChanges();

      expect(items().length).toBe(0);
      expect(notifications.notifications().length)
        .withContext('removed from the queue too, not merely hidden')
        .toBe(0);
    });

    it('keeps a SUCCESS that asks to be kept, so the opinion overrides in both directions', () => {
      notifications.notify('success', 'The import finished with warnings.', null, false, false);
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS * 10);
      fixture.detectChanges();

      expect(items().length).withContext('the caller asked for it to stand').toBe(1);
    });

    it('does not remove anything before the interval elapses', () => {
      notifications.success('The role was created.');
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS - 1);
      fixture.detectChanges();

      expect(items().length).toBe(1);
    });

    it('stops the countdown while the pointer is over the region', () => {
      notifications.success('The role was created.');
      fixture.detectChanges();

      host().dispatchEvent(new MouseEvent('mouseenter'));
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS * 3);
      fixture.detectChanges();

      expect(items().length).withContext('a person is reading it').toBe(1);
    });

    it('gives the FULL interval again once the pointer leaves', () => {
      notifications.success('The role was created.');
      fixture.detectChanges();

      host().dispatchEvent(new MouseEvent('mouseenter'));
      fixture.detectChanges();
      jasmine.clock().tick(INTERVAL_MS * 3);

      host().dispatchEvent(new MouseEvent('mouseleave'));
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS - 1);
      fixture.detectChanges();
      expect(items().length).withContext('not whatever was left of the first interval').toBe(1);

      jasmine.clock().tick(1);
      fixture.detectChanges();
      expect(items().length).toBe(0);
    });

    it('stops the countdown while focus is inside the region', () => {
      notifications.success('The role was created.');
      fixture.detectChanges();

      // This is the state a keyboard or screen-reader user is in for the whole time they are
      // working through the messages, which is exactly when a timed removal would be worst.
      region()?.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS * 3);
      fixture.detectChanges();

      expect(items().length).toBe(1);
    });

    it('keeps the countdown stopped while focus moves between controls inside the region', () => {
      notifications.success('The role was created.');
      notifications.info('One record could not be included.');
      fixture.detectChanges();

      region()?.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
      fixture.detectChanges();

      const inside = items()[1];

      region()?.dispatchEvent(
        new FocusEvent('focusout', { bubbles: true, relatedTarget: inside }),
      );
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS * 2);
      fixture.detectChanges();

      expect(items().length).withContext('focus never left the region').toBe(2);
    });

    it('resumes the countdown when focus leaves the region entirely', () => {
      notifications.success('The role was created.');
      fixture.detectChanges();

      region()?.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
      fixture.detectChanges();

      region()?.dispatchEvent(
        new FocusEvent('focusout', { bubbles: true, relatedTarget: document.body }),
      );
      fixture.detectChanges();

      jasmine.clock().tick(INTERVAL_MS);
      fixture.detectChanges();

      expect(items().length).toBe(0);
    });

    it('cancels a pending removal when the surface itself is destroyed', () => {
      notifications.success('The role was created.');
      fixture.detectChanges();

      fixture.destroy();
      jasmine.clock().tick(INTERVAL_MS * 2);

      // The entry survives in the service: a timer outliving the surface would remove a message
      // after a route change, which is a removal nobody asked for.
      expect(notifications.notifications().length).toBe(1);
    });
  });
  // THE DISMISSAL IS A 44px TARGET - QA-18. It used to be sized from a SPACING step, `--space-6`, which is
  // 24px: the AA floor met and not exceeded, on a transient control that disappears on its own and therefore
  // gives an operator one attempt at it. A missed dismissal re-triggers the toast's own hover-hold, so the
  // cost of a mis-tap here is a notification that will not go away.
  describe('dismissal target size', () => {
    it('meets the full target minimum in both axes', () => {
      notifications.success('Portal saved.');
      fixture.detectChanges();

      const [item] = items();
      const dismissal = dismissalOf(item);

      expect(dismissal).not.toBeNull();

      const bounds = (dismissal as HTMLButtonElement).getBoundingClientRect();

      expect(bounds.width).toBeGreaterThanOrEqual(44);
      expect(bounds.height).toBeGreaterThanOrEqual(44);
    });

    it('keeps the glyph centred, so the box grows around it rather than the glyph growing', () => {
      notifications.success('Portal saved.');
      fixture.detectChanges();

      const dismissal = dismissalOf(items()[0]);
      const computed = getComputedStyle(dismissal as HTMLButtonElement);

      // BLOCKIFIED, AND MEASURED RATHER THAN ASSUMED: the row this sits in is a flex container, so the
      // dismissal is a flex item and the stylesheet's `inline-flex` computes to `flex`. The centring the
      // rule asks for is unaffected, which is the point of the assertion.
      expect(computed.display).toBe('flex');
      expect(computed.alignItems).toBe('center');
      expect(computed.justifyContent).toBe('center');
    });
  });
});
