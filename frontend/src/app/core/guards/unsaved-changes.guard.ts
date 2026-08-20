import { DestroyRef, Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';

import type { CanDeactivateFn } from '@angular/router';

/** The question put to the operator when they leave a form holding unsaved edits. */
export const DISCARD_CHANGES_PROMPT =
  'You have changes on this screen that have not been saved. Leave without saving them?';

/**
 * Reports whether the screen currently on display is one this gate protects. ⚠ WHY A ROUTE-TABLE TEST
 * SURVIVES THE REMOVAL OF THE REFLECTIVE PROBE. It is no longer needed to keep a listing's search box
 * from raising a false warning — a listing registers no probe, so the tracker cannot see its filter
 * control at all — but it remains the only way to ask "is the screen in front of the operator one the
 * application promised to protect?" without maintaining a second list of protected screens beside the
 * route table.
 *
 * @param router The router whose current state names the active screen.
 * @returns True when the active route declares this gate.
 */
export function activeRouteGuardsUnsavedChanges(router: Router): boolean {
  let snapshot = router.routerState.snapshot.root;

  while (snapshot.firstChild !== null) {
    snapshot = snapshot.firstChild;
  }

  // `canDeactivate` is typed as `any[]` by the router, so identity comparison against the
  // exported gate is the only check that cannot be fooled by a same-named import.
  return snapshot.routeConfig?.canDeactivate?.includes(unsavedChangesGuard) === true;
}

/**
 * The sentence an operator is asked before their unsaved entry is discarded, under the name the
 * tracker-based specification refers to it by. An ALIAS rather than a second sentence, and deliberately
 * so: two spellings of one question would let the two paths into the guard ask differently for the same
 * thing, which is the sort of drift a reader can never see from either call site.
 */
export const UNSAVED_CHANGES_PROMPT = DISCARD_CHANGES_PROMPT;

/**
 * Puts the discard question to the operator and reports their answer.
 *
 * ⚠ ONE SENTENCE, ONE CALL SITE. The gate below asks this question when a navigation would abandon unsaved
 * entry, and a screen with its own in-form Cancel has to ask exactly the same thing — measured on the profile
 * screen, that button discarded in silence while the sidebar and the browser's Back button next to it were
 * both refused until the operator confirmed. Exporting the question rather than letting each caller write its
 * own `confirm` is what stops the two paths drifting into asking differently for the same thing, which is
 * drift no reader could see from either call site.
 *
 * @returns True when the operator accepts that their unsaved entry will be discarded.
 */
export function confirmDiscardUnsavedChanges(): boolean {
  return globalThis.confirm(DISCARD_CHANGES_PROMPT);
}

/** A predicate reporting whether one mounted screen currently holds unsaved entry. */
export type UnsavedChangesProbe = () => boolean;

/**
 * Tracks which mounted screens hold unsaved entry, for both ways of leaving one. ⚠ TWO EXITS HAVE TO BE
 * COVERED AND ONLY ONE OF THEM IS THE ROUTER'S. Measured on the portal settings screen and again on the
 * role creation screen: pressing Cancel, navigating anywhere in the application, and pressing the
 * browser's Back button ALL discarded a dirty form in silence - instrumented `window.confirm`,
 * `window.alert` and `beforeunload` recorded nothing on any of the three - and closing or reloading the
 * tab did the same.
 */
@Injectable({ providedIn: 'root' })
export class UnsavedChangesTracker {
  /** The probes of every currently mounted screen that has registered one. */
  private readonly probes = new Set<UnsavedChangesProbe>();

  /** Installs the browser's own unload prompt, once for the whole application. */
  public constructor() {
    globalThis.addEventListener('beforeunload', (event: BeforeUnloadEvent) => {
      if (this.isDirty() === false) {
        return;
      }

      event.preventDefault();
      event.returnValue = true;
    });
  }

  /**
   * Registers a screen's dirty-state probe for as long as that screen is mounted. Call it from a field
   * initialiser, where the injection context is active, so the probe is registered before the first
   * render and released when the component is destroyed.
   *
   * @param probe Reports whether this screen currently holds unsaved entry.
   */
  public watch(probe: UnsavedChangesProbe): void {
    this.probes.add(probe);

    inject(DestroyRef).onDestroy(() => {
      this.probes.delete(probe);
    });
  }

  /**
   * Set while a departure the operator has ALREADY consented to is under way.
   *
   * One-shot, and consumed by the next {@link unsavedChangesGuard} invocation. It exists so that a caller
   * who must ask the question BEFORE it navigates - see {@link UnsavedChangesTracker.confirmDiscard} - does
   * not cause the same question to be asked a second time by the router a moment later.
   */
  private discardAcknowledged = false;

  /**
   * Asks the operator to confirm a discard BEFORE the caller does anything irreversible.
   *
   * ⚠ SIGN-OUT IS THE CASE THIS EXISTS FOR, AND ORDER IS THE WHOLE PROBLEM. The shell revoked the session
   * and only then navigated to the sign-in screen, so by the time the router could ask `canDeactivate` the
   * credential was already gone: answering "no" would have left the operator on a dirty form whose every
   * save was doomed, which is worse than the silent discard it was meant to prevent. Asking here lets the
   * caller abandon the whole gesture while the session is still alive.
   *
   * A confirmed answer is recorded so the navigation that follows is admitted without a second prompt. The
   * record is consumed by the next gate invocation; a caller that confirms and then navigates elsewhere in
   * the same task has still had exactly the consent it asked for, since the operator agreed to discard.
   *
   * @returns True when the caller may proceed, either because nothing is unsaved or because the operator
   * agreed to lose it.
   */
  public confirmDiscard(): boolean {
    if (this.isDirty() === false) {
      return true;
    }

    if (globalThis.confirm(DISCARD_CHANGES_PROMPT) === false) {
      return false;
    }

    this.discardAcknowledged = true;

    return true;
  }

  /**
   * Reads and clears the record of an already-answered discard.
   *
   * @returns True when the operator has already consented to this departure.
   */
  public consumeAcknowledgedDiscard(): boolean {
    const acknowledged = this.discardAcknowledged;

    this.discardAcknowledged = false;

    return acknowledged;
  }

  /**
   * Whether any mounted screen currently holds unsaved entry.
   *
   * @returns True when at least one screen reports unsaved entry.
   */
  public isDirty(): boolean {
    for (const probe of this.probes) {
      try {
        if (probe()) {
          return true;
        }
      } catch {
        continue;
      }
    }

    return false;
  }
}

/**
 * Refuses an operator-initiated departure from a form holding unsaved edits, unless they confirm.
 *
 * @returns Whether the navigation may proceed. ⚠ ONE SOURCE OF TRUTH: unsaved state is read only from the
 * predicate a screen registers with {@link UnsavedChangesTracker}, never by reflecting over a component's
 * fields for a dirty `FormGroup`.
 */
export const unsavedChangesGuard: CanDeactivateFn<unknown> = () => {
  const tracker = inject(UnsavedChangesTracker);

  // ⚠ READ FIRST, AND UNCONDITIONALLY, BECAUSE IT IS A ONE-SHOT RECORD. A caller that had to ask the
  // question before it could act - sign-out is the one - has already had the answer; asking again would put
  // the same question twice to somebody who answered it once. Reading it even when nothing is dirty is what
  // stops a stale acknowledgement outliving the gesture that set it.
  const alreadyAnswered = tracker.consumeAcknowledgedDiscard();

  if (tracker.isDirty() === false || alreadyAnswered) {
    return true;
  }

  // ⚠ THE ROUTER IS OPTIONAL HERE, so the guard can be exercised in an injection context that configures no
  // router at all. A departure the application itself initiated - a save that succeeded and then replaced
  // the address - must never be challenged, and that is what the navigation is read for.
  const navigation = inject(Router, { optional: true })?.getCurrentNavigation();

  // The trigger is what actually answers the question, and it is answered POSITIVELY rather than by
  // excluding what is known to be wrong: only an `'imperative'` navigation is one this application asked
  // for.
  if (navigation?.extras.replaceUrl === true && navigation.trigger === 'imperative') {
    return true;
  }

  return confirmDiscardUnsavedChanges();
};
