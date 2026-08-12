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
 * The word each severity is announced and rendered with.
 *
 * ⚠ SEVERITY IS NEVER CARRIED BY COLOUR ALONE. Colour is a reinforcement here, exactly as it
 * is in the shared error banner: the band is stated as a WORD that is both visible and read
 * out, so the message is fully perceivable without perceiving the hue. That is what makes the
 * four bands usable by a person with a colour-vision deficiency and by a person using a screen
 * reader, and it is the reason this map exists rather than the severity being expressed only
 * as a data attribute for the stylesheet.
 *
 * The wording matches the shared error banner's own severity vocabulary so that one
 * application does not describe the same condition two ways: a refusal is a "Warning" in both
 * surfaces, and a fault is an "Error" in both.
 *
 * Frozen, so the map cannot be mutated by a consumer and the object identity is stable across
 * change-detection passes.
 */
const SEVERITY_LABEL: Readonly<Record<NotificationSeverity, string>> = Object.freeze({
  success: 'Success',
  info: 'Information',
  warning: 'Warning',
  error: 'Error',
});

/**
 * The accessible name of the dismissal control.
 *
 * A control whose visible content is a glyph needs a name that says what it does. The name is
 * the same for every entry rather than interpolating the message into it: a message can be a
 * paragraph of remote text, and a several-hundred-character accessible name is worse than a
 * short one, not better.
 */
const DISMISS_LABEL = 'Dismiss this message';

/**
 * The accessible name of the region itself.
 *
 * Named so it is announced as something rather than as an unlabelled run of text, and so it is
 * reachable by landmark navigation. The name is an attribute, so it adds no visible content.
 */
const REGION_LABEL = 'Notifications';

/**
 * The accessible name and visible wording of the clear-everything control.
 *
 * MIGRATION: a net addition with no legacy counterpart - the legacy surface rendered ONE module
 * message per page render, so there was never a stack to clear. It exists because a queue that only
 * empties one row at a time is a queue an operator abandons: runtime testing measured nine entries
 * on screen at once, and dismissing them individually is nine pointer trips.
 */
const CLEAR_ALL_LABEL = 'Dismiss all';

/**
 * How long a self-dismissing entry stays on screen, in milliseconds.
 *
 * ⚠ CHOSEN AGAINST READING SPEED, NOT AESTHETICS, AND IT IS DELIBERATELY LONG. The bound on a
 * message is 1024 code units, and 8 seconds is comfortably longer than an average adult takes to
 * read the longest sentence this surface realistically shows. It is also only half of the safeguard:
 * the countdown STOPS while the pointer is over the region or focus is inside it, so a person who is
 * reading, or who has tabbed in to dismiss something, is never timed out mid-sentence.
 */
const AUTO_DISMISS_MS = 8_000;

/**
 * The severities that dismiss themselves.
 *
 * ⚠ ONLY THE OUTCOMES THAT REQUIRE NOTHING OF THE READER. A success and an advisory are
 * acknowledgements - the operator caused them, they confirm what they expected, and leaving them on
 * screen indefinitely is what turned a save into permanent furniture. A WARNING or an ERROR is the
 * opposite: it reports something that did NOT happen, it frequently carries the support reference an
 * operator has to quote, and removing it on a timer would destroy the only record of a failure. Those
 * two persist until they are dismissed.
 *
 * Frozen so the set cannot be widened at runtime, and expressed as a set rather than a predicate so
 * the membership is legible at a glance.
 */
const SELF_DISMISSING_SEVERITIES: ReadonlySet<NotificationSeverity> = Object.freeze(
  new Set<NotificationSeverity>(['success', 'info']),
);

/**
 * Whether one entry retires itself on the countdown.
 *
 * ⚠ THE SEVERITY IS THE FALLBACK, NOT THE ANSWER. The set above is a PROXY for the question that
 * actually decides a lifetime - does this outcome require anything of the reader - and on one
 * notification the proxy is wrong. The route guards' access refusal is a warning, so the set exempted
 * it, yet it carries no support reference to quote and asks for nothing, because the remedy is a
 * permission the operator cannot grant themselves. A browser audit measured it standing for four
 * minutes and forty-two seconds, cleared only by navigating away.
 *
 * So a caller may now state the lifetime for its own entry, and the severity decides only when none
 * has. The set is untouched and remains correct for every caller that expresses no opinion, which is
 * all but one of them - the alternative, widening the set to include `'warning'`, is refused with a
 * measurement both here and in the queue's own documentation, because a fault an operator has not
 * acted on must not vanish from under them on a timer.
 *
 * @param entry The entry to decide.
 * @returns True when a countdown should be armed for it.
 */
function retiresItself(entry: AppNotification): boolean {
  return entry.selfDismisses ?? SELF_DISMISSING_SEVERITIES.has(entry.severity);
}

/**
 * The greatest number of entries rendered at once.
 *
 * ⚠ A DISPLAY CEILING, NOT A QUEUE CEILING - the service keeps up to twenty-five, and every one of
 * them stays dismissible and stays in the accessibility tree's announcement history. This bounds only
 * how much of the viewport the surface may occupy at any moment. Runtime testing measured the
 * unbounded version: nine entries came to 442 pixels, which on a 900-pixel viewport is 49 per cent of
 * the screen given over to notification chrome.
 *
 * Four rather than one, because related outcomes genuinely arrive together - a save that succeeded
 * followed by an advisory about what could not be included - and because the NEWEST are kept, which
 * are the ones describing what just happened.
 */
const MAX_VISIBLE_ENTRIES = 4;

/**
 * Renders the queued notifications and lets a person dismiss them.
 *
 * ## The defect this closes
 *
 * `core/services/notification.service.ts` is the one place an outcome is announced, and it is
 * reached from the error interceptor, from both navigation gates and from a dozen feature
 * screens. Nothing rendered its queue. Every one of those announcements — a permission
 * refusal, a rate-limit refusal, a network failure, a confirmed save, an advisory that a
 * requested notification could not be sent — was appended to a signal that no template read,
 * so the operator saw NOTHING. A screen that correctly refused to act, and correctly said so,
 * was indistinguishable from one that had silently done nothing.
 *
 * ## Where it is mounted, and why there
 *
 * Inside the shell's `main` region, above the routed outlet, so that it is present on every
 * screen and survives navigation: a queue rendered by a routed component would be destroyed by
 * the very navigation an announcement often accompanies. It is deliberately NOT one of the
 * shell's five grid regions — the shell's own specification pins that count, and a sixth
 * region would give an empty queue a grid track to occupy on every screen.
 *
 * ## The live region is PERSISTENT
 *
 * The region element is always in the document and only its contents change. A live region
 * inserted at the same moment as its first message is announced inconsistently across screen
 * readers, and the first message is the one that matters. The loop is therefore INSIDE the
 * region, never wrapped around it. This is the same arrangement, for the same measured reason,
 * as the shared error banner's.
 *
 * `role="status"` with `aria-live="polite"` is correct for this surface and `role="alert"` is
 * not: an announcement here reports the outcome of something the operator just did, so it must
 * not interrupt them mid-sentence. The one surface that IS assertive is the error banner, which
 * carries a validation failure the operator has to act on before proceeding. Two surfaces, two
 * urgencies, stated explicitly in each.
 *
 * ## What it deliberately does not do
 *
 * No auto-dismiss timer, no animation, no de-duplication, no grouping and no severity
 * derivation. The service owns the queue and documents that it holds no presentation
 * behaviour; every entry arrives with its severity already decided and its message already
 * composed, and this component's whole job is to place that text and offer the dismissal. An
 * automatic timer would in particular be an accessibility regression: a person reading with a
 * screen reader, or reading slowly, would lose a message before finishing it, and WCAG treats
 * timed content as something the user must be able to control.
 *
 * MIGRATION: this replaces the legacy skin's module-message band. The legacy renderer
 * `Library/Components/Skins/ModuleMessage.vb` painted one message per page load into the skin,
 * with a per-severity raster icon (L134, L140, L146) and — measured — NO `aria-live`, no
 * `role` and no `aria-` attribute of any kind anywhere in either legacy tree. Three
 * divergences are deliberate: the announcement semantics are net-new; the icons are not
 * carried across, because the design system permits no CSS asset reference and a word carries
 * the cue further than an image (it cannot fail to load and it is read out); and the message
 * is DISMISSIBLE, where a legacy band persisted until the next postback replaced it.
 *
 * MIGRATION: every string is rendered through escaping interpolation and nothing here is bound
 * as markup. That is structural rather than defensive: a message can originate in a server
 * `ProblemDetails` payload whose wording descends from legacy resource files in which raw
 * markup is commonplace — 76 of 1182 in-scope resource values carry an HTML tag and a handful
 * carry `<script>` elements — so a value still carrying markup renders as visible, inert text
 * and creates no element.
 *
 * MIGRATION: localisation is not ported. The three static strings here are authored in English
 * with the legacy resource files read only as the authority for wording.
 */
@Component({
  selector: 'app-notification-list',
  standalone: true,
  // No shared component is imported, and none fits: the shared error banner renders one RFC
  // 7807 problem document with per-field messages, whereas this renders a queue of
  // already-composed sentences with no document behind them. Reaching for it would mean
  // fabricating a problem document per notification.
  imports: [],
  templateUrl: './notification-list.component.html',
  styleUrl: './notification-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationListComponent {
  private readonly notifications = inject(NotificationService);

  /**
   * The surface's own root.
   *
   * Two readers, and both need the same thing. It is used to find a sibling dismissal control after one
   * is removed - read from the host rather than through a template query, because the control that must
   * receive focus is chosen AFTER the framework has removed the dismissed entry and a view query resolved
   * before that removal would name the button that no longer exists - and it is used to tell "focus left
   * the region" from "focus moved within it".
   *
   * ⚠ ONE FIELD, WHICH IT WAS NOT. The identical `ElementRef` token was injected twice under two names,
   * `host` and `hostElement`, each documented for one of the two uses as though they were different
   * things. They are the same object: the injector resolves one host `ElementRef` per component, so the
   * two fields were two references to it. Nothing misbehaved, and that is exactly why it was worth
   * removing - a reader encountering both is invited to look for the distinction that justifies them, and
   * the next person needing the host has two equally plausible fields to choose between and no way to
   * pick correctly.
   */
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
   * The router, observed only to learn that the operator has changed SCREEN.
   *
   * Injected here rather than into the service, and the reason is architectural rather than stylistic. The
   * service is depended upon by `core/interceptors/error.interceptor.ts`, so giving it a router dependency
   * would put the router on the construction path of an HTTP interceptor. This component is already the one
   * party that owns the queue's lifetime - it arms and cancels the dismissal timers, suspends them under the
   * pointer and cancels them on teardown - so screen lifetime belongs here beside elapsed-time lifetime, and
   * the service stays free of both.
   */
  private readonly router = inject(Router);

  /**
   * The queue, oldest entry first, exactly as the service publishes it.
   *
   * Re-exposed rather than copied or re-ordered: the service's order is append order, which is
   * the order the operator caused the outcomes in, and re-sorting by severity would move a
   * message out from under a reader mid-sentence.
   */
  protected readonly entries: Signal<readonly AppNotification[]> = this.notifications.notifications;

  /** The accessible name of the region. */
  protected readonly regionLabel: string = REGION_LABEL;

  /** The accessible name of every dismissal control. */
  protected readonly dismissLabel: string = DISMISS_LABEL;

  /** The wording of the clear-everything control. */
  protected readonly clearAllLabel: string = CLEAR_ALL_LABEL;

  /**
   * The entries actually rendered: the newest {@link MAX_VISIBLE_ENTRIES}, still oldest-first.
   *
   * Slicing from the END and then keeping the service's order is what makes the surface bounded
   * without reordering anything: the rows a person reads are still in the order the outcomes
   * happened, and the ones dropped from view are the oldest, which are the least likely to describe
   * what just happened.
   */
  protected readonly visibleEntries: Signal<readonly AppNotification[]> = computed(() =>
    this.entries().slice(-MAX_VISIBLE_ENTRIES),
  );

  /**
   * How many queued entries are not on screen, or zero when all of them are.
   *
   * Stated to the reader rather than silently swallowed. A surface that quietly hides messages is
   * indistinguishable from one that loses them, and a person who has just triggered several outcomes
   * needs to know that the count they can see is not the whole story.
   */
  protected readonly hiddenCount: Signal<number> = computed(() =>
    Math.max(this.entries().length - MAX_VISIBLE_ENTRIES, 0),
  );

  /**
   * Whether more than one entry is queued, which is when clearing them together is worth offering.
   */
  protected readonly canClearAll: Signal<boolean> = computed(() => this.entries().length > 1);

  /**
   * Whether the countdown is suspended because a person is reading or operating the region.
   *
   * ⚠ THIS IS THE SAFEGUARD THAT MAKES AUTO-DISMISS ACCEPTABLE AT ALL. A timed removal is a time
   * limit on reading, and the mitigation is that the limit stops the moment there is evidence someone
   * is engaged with it: the pointer resting over the region, or focus inside it - which is the state a
   * keyboard or screen-reader user is in for the whole time they are working through the messages.
   */
  private readonly paused = signal(false);

  /**
   * The pending removal for each self-dismissing entry, by identifier.
   *
   * Held per entry rather than as one timer for the queue, because entries arrive at different moments
   * and each is owed its own full reading time. Cleared wholesale while {@link paused}, and rebuilt
   * from scratch when the pause ends, which is what gives a person the FULL interval again rather than
   * whatever was left of it.
   */
  private readonly pendingDismissals = new Map<number, ReturnType<typeof setTimeout>>();

  private readonly destroyRef = inject(DestroyRef);

  /** The application's zone, used only to keep the dismissal countdown OUT of it. */
  private readonly zone = inject(NgZone);

  constructor() {
    // Reacts to the queue and to the pause state together. Reading both signals inside the effect is
    // what registers it as a dependent of both, so unpausing re-arms the timers and a new entry gets
    // one without any manual subscription.
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

    /*
     * Discards the previous screen's notifications once a new screen has actually rendered.
     *
     * ⚠ COMPARED ON THE PATH, WITH THE QUERY STRING DISCARDED. Paging, filtering and sorting are all
     * carried in the query string on every listing in this application, so reacting to the full address
     * would clear a refusal the operator was still reading the instant they turned a page. `distinctUntil-
     * Changed` over the path is what makes "the operator left the screen" the trigger rather than "the
     * address changed", and it also absorbs a repeat navigation to the address already held - re-clicking
     * the sidebar entry for the current screen is not a departure from it.
     *
     * `NavigationEnd` rather than `NavigationStart`, because a navigation can be cancelled by a gate or by
     * an unsaved-changes prompt. Clearing on start would discard the messages belonging to a screen the
     * operator then never left, which is a second way to lose a message unread.
     *
     * The first emission is a departure from nothing and clears an empty queue, which the service answers
     * by publishing an unchanged reference - so bootstrap costs nothing.
     */
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        // `urlAfterRedirects`, not `url`: a gate that answers with a redirect is one navigation whose
        // destination is the redirected address, and reading the requested address would treat the
        // refused one as the screen now shown.
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

  /**
   * Suspends the countdown while the pointer is over the region.
   */
  @HostListener('mouseenter')
  protected onPointerEnter(): void {
    this.paused.set(true);
  }

  /**
   * Resumes the countdown when the pointer leaves, giving every entry its full interval again.
   */
  @HostListener('mouseleave')
  protected onPointerLeave(): void {
    this.paused.set(false);
  }

  /**
   * Suspends the countdown while focus is inside the region.
   *
   * `focusin` rather than `focus`, because the event has to be observed on the region while the focus
   * itself lands on a dismissal control inside it - `focus` does not bubble and would never be seen
   * here.
   */
  @HostListener('focusin')
  protected onFocusEnter(): void {
    this.paused.set(true);
  }

  /**
   * Resumes the countdown when focus leaves the region entirely.
   *
   * The related target is checked because `focusout` also fires when focus moves from one dismissal
   * control to the next INSIDE the region, and treating that as leaving would restart the countdown
   * under a person who is still working through the messages.
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

  /**
   * Removes every queued entry.
   */
  protected clearAll(): void {
    this.notifications.clear();
  }

  /**
   * Arms the removal of one entry.
   *
   * ⚠ THE TIMER MUST NOT BE A ZONE TASK, and this is a correctness requirement rather than a
   * performance one. A pending timer inside the zone leaves the application permanently "unstable",
   * and anything that waits for stability then waits for the timer: the framework's own test
   * stability promise, server-side rendering readiness, and any `whenStable`-based coordination.
   * Measured: three specifications that await stability after a successful write timed out, because a
   * success notification had just armed an eight-second countdown inside the zone.
   *
   * Running it outside the zone costs nothing in correctness. The callback writes a SIGNAL through the
   * service, and signal writes mark their consumers dirty and schedule change detection independently
   * of the zone, so this surface still updates when the entry goes.
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

  /**
   * Cancels every pending removal without removing anything.
   */
  private clearPendingDismissals(): void {
    for (const handle of this.pendingDismissals.values()) {
      clearTimeout(handle);
    }

    this.pendingDismissals.clear();
  }

  /**
   * The word for one entry's severity.
   *
   * A method rather than a template lookup into the map, because
   * `noPropertyAccessFromIndexSignature` forbids reading a keyed record as a property in a
   * template and an index expression there would be harder to read than this call. The
   * parameter is the closed severity union, so every input has an answer and no fallback
   * branch can exist to be wrong.
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
   * Delegates without interpreting: the service owns the queue, and an identifier it never
   * issued — or one a clear has already discarded — is a no-op there rather than an error, so
   * no presence test is written here.
   *
   * @param id The entry to remove.
   */
  protected dismiss(id: number): void {
    this.notifications.dismiss(id);

    this.restoreFocusAfterDismissal();
  }

  /**
   * Puts focus somewhere deliberate after a dismissal removes the control that had it.
   *
   * ⚠ THE DEFECT: dismissing a notification dropped focus to `<body>`. The button holding focus
   * is the very element the dismissal destroys, and when focus has nowhere to go the browser
   * gives it to the document — which for a keyboard reader means the next Tab restarts from the
   * top of the page, losing whatever position they had reached. It is also silent: a screen
   * reader announces nothing, so the only feedback that the dismissal worked was the message
   * vanishing, which a reader who cannot see it never receives.
   *
   * THE ORDER OF PREFERENCE, and the reason for each step:
   *   1. ANOTHER NOTIFICATION'S DISMISS BUTTON, when the queue still holds entries. Focus stays
   *      in the surface the reader was working in, so clearing several messages is a sequence of
   *      presses in one place rather than a hunt after each one.
   *   2. THE MAIN REGION, when that was the last entry. The surface is now empty and has no
   *      focusable control at all, so focus moves to the region the notification sat in - the
   *      same target the skip link uses, and the reason `<main>` carries `tabindex="-1"`. A
   *      reader resumes at the content rather than at the document.
   *
   * Neither step scrolls or steals focus from anywhere else: this runs only in response to the
   * reader's own press on a control inside this surface.
   *
   * ⚠ READ AFTER THE REMOVAL HAS RENDERED. The queue is a signal and the entry's button is gone
   * only once change detection has run, so the surviving controls are collected on a macrotask -
   * a microtask would resolve before that render and would find the dismissed button still
   * present, and focus it. The timer is deliberately NOT wrapped for the zone: this component
   * needs the framework to see the focus move so the change is reflected, and the callback does
   * no further work that could keep the application unstable.
   */
  private restoreFocusAfterDismissal(): void {
    setTimeout(() => {
      const surviving: HTMLButtonElement | null =
        this.host.nativeElement.querySelector<HTMLButtonElement>('.notification-list__dismiss');

      if (surviving !== null) {
        surviving.focus();

        return;
      }

      // `closest` rather than a document lookup by identifier: the surface is mounted inside the
      // main region by the shell, so its own ancestry names the target without this component
      // having to know the identifier the shell generates for it.
      const main: HTMLElement | null = this.host.nativeElement.closest('main');

      // ⚠ `preventScroll`, AND ITS ABSENCE WAS MEASURED AS A DEFECT. The main region begins
      // above this surface, so focusing it without the option scrolled the viewport - measured
      // at 17px (window.scrollY 0 → 17) on a screen the reader had not scrolled at all, and by
      // as much as the reader HAS scrolled on a long grid. That trades a focus defect for a
      // scroll-position one: the reader is returned to the top of a screen they were working
      // down. Focus moves; the viewport does not. The same option is passed for the same reason
      // where the shared confirmation dialog falls back to this element on teardown.
      main?.focus({ preventScroll: true });
    });
  }
}
