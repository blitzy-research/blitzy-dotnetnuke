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
      // No provider is substituted. The service holds a signal and nothing else — no
      // transport, no timer, no storage — so the real one is both simpler and a stronger
      // subject than a double would be.
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
      // The property the whole arrangement rests on. A live region inserted at the same
      // moment as its first message is announced inconsistently across screen readers, and
      // the first message is the one that matters — so the region must already exist while
      // the queue is empty.
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

    it('renders two identical messages as two rows, because the service does not de-duplicate', () => {
      notifications.error('The request could not be completed.');
      notifications.error('The request could not be completed.');
      fixture.detectChanges();

      // Tracked by the service's never-reissued identifier rather than by the text. Tracking
      // by text would collapse these into one row and silently drop a message the
      // application deliberately raised twice.
      expect(items().length).toBe(2);
    });

    it('renders markup in a message as inert text rather than as elements', () => {
      // Structural rather than defensive: a message can originate in a server problem
      // document whose wording descends from legacy resource files in which raw markup is
      // commonplace.
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
});
