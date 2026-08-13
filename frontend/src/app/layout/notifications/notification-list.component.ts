import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  NgZone,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import type { Signal } from '@angular/core';

import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map, distinctUntilChanged } from 'rxjs';

import { NotificationService } from '../../core/services/notification.service';

import type { AppNotification, NotificationSeverity } from '../../core/services/notification.service';

/**
 * The word each severity is announced and rendered with. ⚠ SEVERITY IS NEVER CARRIED BY COLOUR ALONE.
 * Colour is a reinforcement here, exactly as it is in the shared error banner: the band is stated as a
 * WORD that is both visible and read out, so the message is fully perceivable without perceiving the hue.
 */
const SEVERITY_LABEL: Readonly<Record<NotificationSeverity, string>> = Object.freeze({
  success: 'Success',
  info: 'Information',
  warning: 'Warning',
  error: 'Error',
});

/**
 * The accessible name of the dismissal control. A control whose visible content is a glyph needs a name
 * that says what it does.
 */
const DISMISS_LABEL = 'Dismiss this message';

/**
 * The accessible name of the region itself. Named so it is announced as something rather than as an
 * unlabelled run of text, and so it is reachable by landmark navigation.
 */
const REGION_LABEL = 'Notifications';

/**
 * The accessible name and visible wording of the clear-everything control. MIGRATION: a net addition with
 * no legacy counterpart - the legacy surface rendered ONE module message per page render, so there was
 * never a stack to clear.
 */
const CLEAR_ALL_LABEL = 'Dismiss all';

/**
 * How long a self-dismissing entry stays on screen, in milliseconds. ⚠ CHOSEN AGAINST READING SPEED, NOT
 * AESTHETICS, AND IT IS DELIBERATELY LONG. The bound on a message is 1024 code units, and 8 seconds is
 * comfortably longer than an average adult takes to read the longest sentence this surface realistically
 * shows.
 */
const AUTO_DISMISS_MS = 8_000;

/**
 * The severities that dismiss themselves. ⚠ ONLY THE OUTCOMES THAT REQUIRE NOTHING OF THE READER. A
 * success and an advisory are acknowledgements - the operator caused them, they confirm what they
 * expected, and leaving them on screen indefinitely is what turned a save into permanent furniture.
 */
const SELF_DISMISSING_SEVERITIES: ReadonlySet<NotificationSeverity> = Object.freeze(
  new Set<NotificationSeverity>(['success', 'info']),
);

/**
 * Whether one entry retires itself on the countdown. ⚠ THE SEVERITY IS THE FALLBACK, NOT THE ANSWER. The
 * set above is a PROXY for the question that actually decides a lifetime - does this outcome require
 * anything of the reader - and on one notification the proxy is wrong.
 *
 * @param entry The entry to decide.
 * @returns True when a countdown should be armed for it.
 */
function retiresItself(entry: AppNotification): boolean {
  return entry.selfDismisses ?? SELF_DISMISSING_SEVERITIES.has(entry.severity);
}

/**
 * The greatest number of entries rendered at once. ⚠ A DISPLAY CEILING, NOT A QUEUE CEILING - the service
 * keeps up to twenty-five, and every one of them stays dismissible and stays in the accessibility tree's
 * announcement history. This bounds only how much of the viewport the surface may occupy at any moment.
 */
const MAX_VISIBLE_ENTRIES = 4;

/**
 * Renders the queued notifications and lets a person dismiss them. ## The defect this closes
 * `core/services/notification.service.ts` is the one place an outcome is announced, and it is reached
 * from the error interceptor, from both navigation gates and from a dozen feature screens. Nothing
 * rendered its queue.
 */
@Component({
  selector: 'app-notification-list',
  standalone: true,
  imports: [],
  templateUrl: './notification-list.component.html',
  styleUrl: './notification-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationListComponent {
  private readonly notifications = inject(NotificationService);

  /** The surface's own root. Two readers, and both need the same thing. */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * The router, observed only to learn that the operator has changed SCREEN. Injected here rather than
   * into the service, and the reason is architectural rather than stylistic. The service is depended upon
   * by `core/interceptors/error.interceptor.ts`, so giving it a router dependency would put the router on
   * the construction path of an HTTP interceptor.
   */
  private readonly router = inject(Router);

  /**
   * The queue, oldest entry first, exactly as the service publishes it. Re-exposed rather than copied or
   * re-ordered: the service's order is append order, which is the order the operator caused the outcomes
   * in, and re-sorting by severity would move a message out from under a reader mid-sentence.
   */
  protected readonly entries: Signal<readonly AppNotification[]> = this.notifications.notifications;

  /** The accessible name of the region. */
  protected readonly regionLabel: string = REGION_LABEL;

  /** The accessible name of every dismissal control. */
  protected readonly dismissLabel: string = DISMISS_LABEL;

  /** The wording of the clear-everything control. */
  protected readonly clearAllLabel: string = CLEAR_ALL_LABEL;

  /**
   * The entries actually rendered: the newest {@link MAX_VISIBLE_ENTRIES}, still oldest-first. Slicing
   * from the END and then keeping the service's order is what makes the surface bounded without
   * reordering anything: the rows a person reads are still in the order the outcomes happened, and the
   * ones dropped from view are the oldest, which are the least likely to describe what just happened.
   */
  protected readonly visibleEntries: Signal<readonly AppNotification[]> = computed(() =>
    this.entries().slice(-MAX_VISIBLE_ENTRIES),
  );

  /** How many queued entries are not on screen, or zero when all of them are. */
  protected readonly hiddenCount: Signal<number> = computed(() =>
    Math.max(this.entries().length - MAX_VISIBLE_ENTRIES, 0),
  );

  /** Whether more than one entry is queued, which is when clearing them together is worth offering. */
  protected readonly canClearAll: Signal<boolean> = computed(() => this.entries().length > 1);

  /**
   * Whether the countdown is suspended because a person is reading or operating the region. ⚠ THIS IS THE
   * SAFEGUARD THAT MAKES AUTO-DISMISS ACCEPTABLE AT ALL. A timed removal is a time limit on reading, and
   * the mitigation is that the limit stops the moment there is evidence someone is engaged with it: the
   * pointer resting over the region, or focus inside it - which is the state a keyboard or screen-reader
   * user is in for the whole time they are working through the messages.
   */
  private readonly paused = signal(false);

  /** The pending removal for each self-dismissing entry, by identifier. */
  private readonly pendingDismissals = new Map<number, ReturnType<typeof setTimeout>>();

  private readonly destroyRef = inject(DestroyRef);

  /** The application's zone, used only to keep the dismissal countdown OUT of it. */
  private readonly zone = inject(NgZone);

  constructor() {
    effect(() => {
      const entries = this.entries();
      const paused = this.paused();

      this.clearPendingDismissals();

      if (paused) {
        return;
      }

      for (const entry of entries) {
        if (retiresItself(entry)) {
          this.scheduleDismissal(entry.id);
        }
      }
    });

    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        map((event) => event.urlAfterRedirects.split('?')[0]),
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.notifications.dismissStale();
      });

    // A pending timer outliving the component would call into the service after the surface is gone,
    // which on a route change is a removal nobody asked for.
    this.destroyRef.onDestroy(() => {
      this.clearPendingDismissals();
    });
  }

  /** Suspends the countdown while the pointer is over the region. */
  @HostListener('mouseenter')
  protected onPointerEnter(): void {
    this.paused.set(true);
  }

  /** Resumes the countdown when the pointer leaves, giving every entry its full interval again. */
  @HostListener('mouseleave')
  protected onPointerLeave(): void {
    this.paused.set(false);
  }

  /**
   * Suspends the countdown while focus is inside the region. `focusin` rather than `focus`, because the
   * event has to be observed on the region while the focus itself lands on a dismissal control inside it
   * - `focus` does not bubble and would never be seen here.
   */
  @HostListener('focusin')
  protected onFocusEnter(): void {
    this.paused.set(true);
  }

  /**
   * Resumes the countdown when focus leaves the region entirely. The related target is checked because
   * `focusout` also fires when focus moves from one dismissal control to the next INSIDE the region, and
   * treating that as leaving would restart the countdown under a person who is still working through the
   * messages.
   *
   * @param event The focus event, whose related target is where focus is going.
   */
  @HostListener('focusout', ['$event'])
  protected onFocusLeave(event: FocusEvent): void {
    const next = event.relatedTarget;
    const host = this.host.nativeElement;

    if (next instanceof Node && host.contains(next)) {
      return;
    }

    this.paused.set(false);
  }

  /** Removes every queued entry. */
  protected clearAll(): void {
    this.notifications.clear();
  }

  /**
   * Arms the removal of one entry. ⚠ THE TIMER MUST NOT BE A ZONE TASK, and this is a correctness
   * requirement rather than a performance one.
   *
   * @param id The entry to remove when the interval elapses.
   */
  private scheduleDismissal(id: number): void {
    this.pendingDismissals.set(
      id,
      this.zone.runOutsideAngular(() =>
        setTimeout(() => {
          this.pendingDismissals.delete(id);
          this.notifications.dismiss(id);
        }, AUTO_DISMISS_MS),
      ),
    );
  }

  /** Cancels every pending removal without removing anything. */
  private clearPendingDismissals(): void {
    for (const handle of this.pendingDismissals.values()) {
      clearTimeout(handle);
    }

    this.pendingDismissals.clear();
  }

  /**
   * The word for one entry's severity. A method rather than a template lookup into the map, because
   * `noPropertyAccessFromIndexSignature` forbids reading a keyed record as a property in a template and
   * an index expression there would be harder to read than this call.
   *
   * @param severity The entry's severity.
   * @returns The word to render and announce.
   */
  protected labelFor(severity: NotificationSeverity): string {
    return SEVERITY_LABEL[severity];
  }

  /**
   * Removes one entry from the queue.
   *
   * @param id The entry to remove.
   */
  protected dismiss(id: number): void {
    this.notifications.dismiss(id);

    this.restoreFocusAfterDismissal();
  }

  private restoreFocusAfterDismissal(): void {
    setTimeout(() => {
      const surviving: HTMLButtonElement | null =
        this.host.nativeElement.querySelector<HTMLButtonElement>('.notification-list__dismiss');

      if (surviving !== null) {
        surviving.focus();

        return;
      }

      const main: HTMLElement | null = this.host.nativeElement.closest('main');

      // ⚠ `preventScroll`, AND ITS ABSENCE WAS MEASURED AS A DEFECT. The main region begins above this
      // surface, so focusing it without the option scrolled the viewport - measured at 17px (window.scrollY
      // 0 → 17) on a screen the reader had not scrolled at all, and by as much as the reader HAS scrolled
      // on a long grid.
      main?.focus({ preventScroll: true });
    });
  }
}
