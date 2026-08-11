/**
 * Route gate that refuses to leave a form holding edits nobody has saved.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS EXISTS TO PREVENT
 * ---------------------------------------------------------------------------
 * Runtime testing measured unbounded, silent data loss across every form in the application:
 * 83 characters across 7 fields on the add-account screen destroyed by a single Logout click,
 * 4 edits across 2 tabs of the site-settings screen destroyed by a reload, 24 characters
 * destroyed by a refused request mid-submit. Nothing warned, nothing buffered, nothing
 * re-offered the work, and `window.onbeforeunload` was `null` so the browser could not even
 * offer its own prompt. The loss was silent in the worst sense: on the reload path the screen
 * re-read the record from the server and looked entirely normal afterwards, so an operator had
 * no way to notice that four edits had reverted.
 *
 * MIGRATION: the legacy application could not lose work this way, and that is why no legacy
 * counterpart exists to port. Every affordance on those pages was a POSTBACK — a full form
 * submission to the server — so leaving a page meant submitting it, and the browser's own
 * navigation model made an unsaved intermediate state impossible to reach. A single-page
 * application separates editing from submitting, which is what creates the failure this gate
 * closes. It is therefore an authored addition rather than a reproduction, and it is recorded
 * as such in `MIGRATION_NOTES.md`.
 *
 * ---------------------------------------------------------------------------
 * WHY THIS NEEDS NO CODE IN ANY SCREEN
 * ---------------------------------------------------------------------------
 * ⚠ NOT ONE OF THE FOURTEEN FORM SCREENS IS MODIFIED BY THIS GATE, and that is a deliberate
 * design constraint rather than a happy accident. The obvious shape — an interface every form
 * implements, or a directive every form template carries — costs an edit in fourteen screens
 * and, far worse, is silently incomplete the moment a fifteenth screen is written: the omission
 * produces no error and no test failure, only the original defect again on one screen.
 *
 * This gate instead discovers the forms. A component's reactive forms are its own fields, so
 * they are enumerable, and Angular publishes a real type test for each thing this needs to
 * know: {@link isSignal} for "is this a signal I may read?" and `instanceof FormGroup` for "is
 * this a form?". Neither is a naming convention, so nothing has to be remembered. The census of
 * shapes it has to cope with was taken from the code rather than assumed, and all three occur:
 *
 *   - twelve screens hold a plain `FormGroup` field, variously named;
 *   - the site-settings and add-portal screen holds TWO (`createForm` and `editForm`), only one
 *     of which is mounted at a time — so a gate that looked for a single well-known member
 *     would have to know which;
 *   - the profile screen's form is a `Signal<FormGroup>`, rebuilt when the property definitions
 *     arrive, so its identity changes during the screen's life and a reference captured once
 *     would go stale.
 *
 * Reading a signal is free of side effects, which is what makes unwrapping one safe here. An
 * arbitrary zero-argument function is NOT called: `isSignal` is what separates the two, and
 * without it this would be invoking unknown component methods during a navigation.
 *
 * ---------------------------------------------------------------------------
 * WHICH DEPARTURES ARE CHALLENGED, AND WHY THAT IS NOT "ALL OF THEM"
 * ---------------------------------------------------------------------------
 * ⚠ A DEPARTURE THE APPLICATION ITSELF INITIATED IS NEVER CHALLENGED. `role-form.component.ts`
 * states the requirement in its own words while explaining why it refuses to canonicalise a
 * pristine control — doing so "would mark a form dirty that nobody has touched and would then
 * have the unsaved-changes guard challenge an exit no one initiated". This gate is the other
 * half of that contract.
 *
 * The distinction is drawn from the navigation itself rather than from a flag a screen has to
 * remember to set, because the application already encodes it: every post-success and
 * post-failure departure REPLACES its history entry, and every operator-initiated departure —
 * a link, a Cancel button, the Back button — PUSHES or arrives by `popstate`. That convention
 * was established across ten screens for an unrelated reason (a create that pushed left the
 * submitted form in forward history, where Back would restore an empty duplicate), and it turns
 * out to separate exactly the two cases this gate has to tell apart:
 *
 *   - `extras.replaceUrl === true` — the application is moving the operator because a save
 *     succeeded, a record was deleted, or a gate refused them. Challenging any of these would
 *     be wrong, and on the gate-refusal path it would be actively harmful: a session that has
 *     already ended cannot be returned to, so a prompt offering to stay would be offering
 *     something that no longer exists.
 *   - anything else — the operator asked to leave. Challenge it.
 *
 * The alternative was to mark every form pristine in every success handler, which is ten edits
 * that must each be found and none of which any test would miss if omitted. Reading the
 * navigation asks the question once, in one place.
 *
 * ---------------------------------------------------------------------------
 * WHY A NATIVE PROMPT
 * ---------------------------------------------------------------------------
 * The confirmation is the browser's own, not the shared dialog component, and the reason is
 * consistency rather than convenience: the OTHER half of this defect — a reload, a tab close, a
 * navigation to another origin — can only be answered by `beforeunload`, whose prompt the
 * browser owns outright and whose wording it will not let an application choose. Answering the
 * in-application half with a styled dialog would give one hazard two visibly different
 * affordances. A native prompt is also keyboard-operable, announced by assistive technology and
 * impossible to mis-focus, none of which comes for free in a custom dialog.
 *
 * `beforeunload` itself is registered by `layout/shell/shell.component.ts`, which is mounted for
 * the whole life of the application; this file owns only the route half.
 */

import { DestroyRef, Injectable, isSignal, inject } from '@angular/core';
import { FormGroup } from '@angular/forms';
import { Router } from '@angular/router';

import type { CanDeactivateFn } from '@angular/router';

/**
 * The question put to the operator when they leave a form holding unsaved edits.
 *
 * AUTHORED, BECAUSE NO LEGACY WORDING EXISTS TO REPRODUCE — the postback model made the
 * situation unreachable, so no resource document names it. Worded as a question with the
 * consequence stated, because a prompt that says only "are you sure?" leaves the operator to
 * guess what pressing each button costs them.
 *
 * Most browsers ignore the string supplied to `beforeunload` and substitute their own wording;
 * it is used verbatim for the in-application half, where it is honoured.
 */
export const DISCARD_CHANGES_PROMPT =
  'You have changes on this screen that have not been saved. Leave without saving them?';

/**
 * Reports whether a component is holding edits that have not been saved.
 *
 * Enumerates the component's own fields, unwraps any that are signals, and asks every
 * `FormGroup` it finds whether it is dirty. A screen holding two forms is dirty when EITHER is,
 * which is correct for the add-portal screen: only one of its two is mounted at a time, and the
 * unmounted one is pristine because nothing has touched it.
 *
 * ⚠ `dirty` IS THE RIGHT QUESTION AND `valid` IS NOT. A half-typed value that fails its rule is
 * still work the operator would be upset to lose — arguably more so, since they cannot save it
 * yet. Angular sets `dirty` on the first edit a person makes and never on a programmatic
 * `setValue`, which is exactly the distinction needed: a form populated from a record it just
 * read is pristine, and the screens that canonicalise a value on blur already refuse to do so
 * to a pristine control for this reason.
 *
 * `disabled` groups are skipped by Angular's own `dirty` bookkeeping, so a read-only screen —
 * the portal-maintained Administrators role, for instance — cannot report itself dirty.
 *
 * Exported so that `layout/shell/shell.component.ts` can ask the identical question of the
 * currently routed component when the BROWSER is about to leave — a reload, a tab close, a
 * navigation to another origin. Those never reach the router and so never reach this gate, and
 * they are half of the measured loss. One predicate serves both channels deliberately: two
 * copies of "what counts as unsaved" would eventually disagree, and the disagreement would show
 * up as a screen that warns on one exit route and not the other.
 *
 * @param component The deactivating component instance, as the router supplies it.
 * @returns True when at least one of its forms has been edited and not saved.
 */
export function holdsUnsavedEdits(component: unknown): boolean {
  if (component === null || typeof component !== 'object') {
    return false;
  }

  for (const value of Object.values(component)) {
    // Reading a signal is side-effect-free; an arbitrary function is NOT invoked, which is the
    // whole reason this tests `isSignal` rather than `typeof value === 'function'`.
    const resolved: unknown = isSignal(value) ? value() : value;

    if (resolved instanceof FormGroup && resolved.dirty) {
      return true;
    }
  }

  return false;
}

/**
 * Reports whether the screen currently on display is one this gate protects.
 *
 * ⚠ THE ROUTE TABLE IS THE SINGLE DECLARATION, AND THIS IS WHAT LETS THE BROWSER-EXIT CHANNEL
 * SHARE IT. The gate is attached to nineteen routes and deliberately NOT to the four listing
 * routes, because a listing's search box is a `FormGroup` too: typing a filter term marks it
 * dirty, and a `beforeunload` handler that consulted {@link holdsUnsavedEdits} alone would warn
 * an operator about "unsaved changes" for a search term they had typed and already seen applied.
 *
 * Rather than maintain a second list of protected screens — which would be the same defect as a
 * per-screen interface, one list drifting from the other — this reads the decision back off the
 * route that is actually active. Adding the gate to a new form route therefore extends BOTH
 * channels, and forgetting to add it withholds both, which is at least consistent.
 *
 * The deepest activated route is the one consulted, because the gate is declared on leaves.
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
 * tracker-based specification refers to it by.
 *
 * An ALIAS rather than a second sentence, and deliberately so: two spellings of one question would
 * let the two paths into the guard ask differently for the same thing, which is the sort of drift a
 * reader can never see from either call site. One sentence, two names, and nothing to keep in step.
 */
export const UNSAVED_CHANGES_PROMPT = DISCARD_CHANGES_PROMPT;

/**
 * A predicate reporting whether one mounted screen currently holds unsaved entry.
 */
export type UnsavedChangesProbe = () => boolean;

/**
 * Tracks which mounted screens hold unsaved entry, for both ways of leaving one.
 *
 * ⚠ TWO EXITS HAVE TO BE COVERED AND ONLY ONE OF THEM IS THE ROUTER'S. Measured on the portal
 * settings screen and again on the role creation screen: pressing Cancel, navigating anywhere in
 * the application, and pressing the browser's Back button ALL discarded a dirty form in silence -
 * instrumented `window.confirm`, `window.alert` and `beforeunload` recorded nothing on any of the
 * three - and closing or reloading the tab did the same. A route guard answers the first three
 * because they are router navigations; only the browser's own unload prompt answers the fourth,
 * and it needs the dirty state at an arbitrary moment rather than at a navigation. Hence a service
 * that holds the state and one listener installed once, rather than a listener per screen.
 *
 * ⚠ A SET OF PROBES RATHER THAN A BOOLEAN, because more than one screen can be mounted at once -
 * a routed form and a persistent shell region, or two forms on one screen - and a boolean written
 * by whichever mounted last would let one screen clear another screen's warning. A probe is
 * removed when the screen that registered it is destroyed, so a screen that has gone can never
 * hold the application hostage.
 */
@Injectable({ providedIn: 'root' })
export class UnsavedChangesTracker {
  /** The probes of every currently mounted screen that has registered one. */
  private readonly probes = new Set<UnsavedChangesProbe>();

  /**
   * Installs the browser's own unload prompt, once for the whole application.
   *
   * The listener cannot choose its own wording - every current browser shows a fixed sentence -
   * so this only decides WHETHER to prompt. `preventDefault` is the specified mechanism and the
   * legacy `returnValue` assignment is set alongside it, because several engines still require it.
   */
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
   * Registers a screen's dirty-state probe for as long as that screen is mounted.
   *
   * Call it from a field initialiser, where the injection context is active, so the probe is
   * registered before the first render and released when the component is destroyed. The caller
   * keeps nothing: there is no handle to forget to release.
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
   * Whether any mounted screen currently holds unsaved entry.
   *
   * A probe that throws is treated as CLEAN rather than allowed to propagate: a screen mid-teardown
   * could otherwise turn a navigation into an unhandled error, and refusing to navigate because a
   * probe misbehaved would be a worse outcome than losing a warning.
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
 * Returns `true` to allow the departure and `false` to keep the operator where they are.
 * Returning `false` cancels the navigation, which leaves the address bar and the screen exactly
 * as they were — so declining the prompt costs the operator nothing, not even their scroll
 * position.
 *
 * @param component The component being deactivated.
 * @returns Whether the navigation may proceed.
 *
 * ⚠ TWO SOURCES OF TRUTH ABOUT ONE QUESTION, AND EITHER ONE IS ENOUGH. A screen may declare its
 * unsaved state in two ways: by holding a dirty `FormGroup` the reflective probe above can find, or
 * by registering its own predicate with {@link UnsavedChangesTracker} - which is what a screen whose
 * dirty state is not simply "the form is dirty" does, and what covers the exits a route guard cannot
 * see at all. Consulting both is what keeps the fourteen editing screens covered without asking any
 * of them to state the same fact twice.
 */
export const unsavedChangesGuard: CanDeactivateFn<unknown> = (component) => {
  const tracker = inject(UnsavedChangesTracker);

  if (holdsUnsavedEdits(component) === false && tracker.isDirty() === false) {
    return true;
  }

  // ⚠ THE ROUTER IS OPTIONAL HERE, so the guard can be exercised in an injection context that
  // configures no router at all. A departure the application itself initiated - a save that
  // succeeded and then replaced the address - must never be challenged, and that is what the
  // navigation is read for.
  const navigation = inject(Router, { optional: true })?.getCurrentNavigation();

  if (navigation?.extras.replaceUrl === true) {
    return true;
  }

  return globalThis.confirm(DISCARD_CHANGES_PROMPT);
};
