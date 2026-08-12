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
 * WHY EVERY PROTECTED SCREEN REGISTERS ITSELF, AND WHY IT DID NOT USED TO
 * ---------------------------------------------------------------------------
 * ⚠ THIS GATE ONCE DISCOVERED THE FORMS BY REFLECTION, AND THAT COST THE WHOLE APPLICATION ITS
 * FORMS-FREE STARTUP. It enumerated `Object.values(component)`, unwrapped anything that was a
 * signal, and asked every `FormGroup` it found whether it was dirty. The appeal was that no
 * screen had to be edited; the price was an `import { FormGroup } from '@angular/forms'` in a
 * module that `app.routes.ts` — which is EAGER — imports. A static walk of the import graph from
 * `main.ts` reached 45 eager modules and found `@angular/forms` reachable through exactly one of
 * them: this file. Every form screen in the application is lazily loaded, so the forms package
 * had no business being in the initial bundle at all, and it was there to support a convenience
 * in a route guard. A performance review measured it against the frozen bundle budget.
 *
 * ⚠ REFLECTION WAS ALSO NEVER AS COMPLETE AS IT LOOKED. It could only see dirty state that
 * happens to BE a `FormGroup` field: a screen whose unsaved entry lives in a signal, in a child
 * component, or in a form built inside a closure was invisible to it, and the invisibility
 * produced no error and no test failure. Nine screens had already registered an explicit probe
 * with {@link UnsavedChangesTracker} for exactly that reason, so the application was carrying
 * two mechanisms answering one question, and the reflective one was the weaker.
 *
 * So the tracker is now the ONLY mechanism, and each of the fifteen protected screens registers
 * a probe in a field initialiser. That is one line per screen and it says what the screen means
 * — `() => this.form.dirty && this.saving() === false` — rather than leaving a gate to infer it.
 * The registration is released automatically when the screen is destroyed, so there is no handle
 * to forget.
 *
 * ⚠ THE ONE THING TO WATCH WHEN ADDING A SIXTEENTH SCREEN: a route that declares this gate while
 * its component registers no probe is protected in name only. That is the failure mode reflection
 * was meant to prevent, and it is now covered by a specification instead — the guard's own suite
 * walks the route table and asserts that every route declaring this gate names a component that
 * registers a probe.
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
 * `beforeunload` is registered by {@link UnsavedChangesTracker}, which is root-provided and
 * therefore lives for the whole of a session. It used to be registered TWICE — the application
 * shell installed a second listener of its own, over the reflective probe — and one hazard with
 * two owners is one owner too many: the two could disagree, and a performance review counted the
 * duplication as work done twice on every attempt to leave. The tracker owns it alone now, and the
 * shell consumes state rather than keeping its own.
 */

import { DestroyRef, Injectable, inject } from '@angular/core';
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
 * Reports whether the screen currently on display is one this gate protects.
 *
 * ⚠ WHY A ROUTE-TABLE TEST SURVIVES THE REMOVAL OF THE REFLECTIVE PROBE. It is no longer needed to
 * keep a listing's search box from raising a false warning — a listing registers no probe, so the
 * tracker cannot see its filter control at all — but it remains the only way to ask "is the screen
 * in front of the operator one the application promised to protect?" without maintaining a second
 * list of protected screens beside the route table. The guard's own specification uses it to walk
 * the route table and prove that every route declaring this gate reaches a component that registers
 * a probe, which is the check that replaced reflection's accidental completeness.
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
 * @returns Whether the navigation may proceed.
 *
 * ⚠ ONE SOURCE OF TRUTH, AND IT USED TO BE TWO. A screen's unsaved state was read either from a
 * dirty `FormGroup` found by reflecting over the component's fields, or from a predicate the screen
 * had registered with {@link UnsavedChangesTracker}. Consulting both looked like belt and braces and
 * was not: the reflective half pulled `@angular/forms` into the eagerly loaded bundle for an
 * application whose every form screen is lazy, and it could only ever see dirty state that happened
 * to be a `FormGroup` field — so a screen holding its entry anywhere else was silently unprotected.
 * Every protected screen now registers a probe, and the tracker is the whole answer.
 *
 * The deactivating component is deliberately NOT inspected, which is why this takes no parameter.
 */
export const unsavedChangesGuard: CanDeactivateFn<unknown> = () => {
  const tracker = inject(UnsavedChangesTracker);

  if (tracker.isDirty() === false) {
    return true;
  }

  // ⚠ THE ROUTER IS OPTIONAL HERE, so the guard can be exercised in an injection context that
  // configures no router at all. A departure the application itself initiated - a save that
  // succeeded and then replaced the address - must never be challenged, and that is what the
  // navigation is read for.
  const navigation = inject(Router, { optional: true })?.getCurrentNavigation();

  // ⚠ `replaceUrl` ALONE IS NOT A STATEMENT THAT THE APPLICATION INITIATED THE DEPARTURE, AND
  // TESTING IT ALONE OPENED A SILENT DATA-LOSS PATH. The exemption above it exists for ONE
  // departure - a save that succeeded and then replaced the address it was reached by - and
  // `replaceUrl` was read as the signature of that. It is not: the router sets the very same flag on
  // every navigation the BROWSER drives, because a popstate has already moved the history entry by
  // the time the router hears about it and replacing is the only way to stay in step with it. So the
  // one flag was carrying two unrelated meanings, and the browser's Back button inherited the
  // exemption written for a successful save. A browser audit reproduced it end to end: typing into
  // `/roles/new` and clicking a sidebar link prompted correctly, and `history.back()` from the same
  // dirty form changed the route with NO prompt and discarded the edits silently. `beforeunload` does
  // not cover it either - the tracker registers it once and a same-document navigation does not fire
  // it at all - so this guard was the only thing standing between the Back button and an operator's
  // unsaved work.
  //
  // The trigger is what actually answers the question, and it is answered POSITIVELY rather than by
  // excluding what is known to be wrong: only an `'imperative'` navigation is one this application
  // asked for. `'popstate'` is the Back and Forward buttons and `'hashchange'` is the address's
  // fragment being edited, and neither is a save this screen just completed, so both are challenged
  // exactly as a link click is. Naming the one admissible trigger rather than blacklisting the two
  // inadmissible ones means a future trigger arrives challenged rather than exempt, which is the
  // direction this guard must fail in.
  if (navigation?.extras.replaceUrl === true && navigation.trigger === 'imperative') {
    return true;
  }

  return globalThis.confirm(DISCARD_CHANGES_PROMPT);
};
