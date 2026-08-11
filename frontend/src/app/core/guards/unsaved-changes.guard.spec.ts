/**
 * Specification for {@link unsavedChangesGuard}, the gate that refuses to leave a form holding
 * edits nobody has saved.
 *
 * NET-NEW COVERAGE, NOT A PORTED TEST. The legacy tree contains no automated tests at all, and
 * it could not have contained one for this behaviour: every affordance on those pages was a
 * postback, so leaving a page meant submitting it and an unsaved intermediate state was
 * unreachable. The defect this gate closes is created by the single-page model, and runtime
 * testing measured it as unbounded silent loss — 83 characters across 7 fields destroyed by one
 * Logout click, 4 edits across 2 tabs destroyed by a reload, 24 characters destroyed by a
 * refused request mid-submit.
 *
 * ---------------------------------------------------------------------------
 * WHAT THIS FILE IS ACTUALLY GUARDING AGAINST
 * ---------------------------------------------------------------------------
 * The gate carries no per-screen code, which is its principal virtue and also the source of
 * every way it can silently break. Each of these fails without any compiler complaint and
 * without any other specification noticing:
 *
 *   1. **A form shape going undiscovered.** Three shapes occur in the fourteen screens: a plain
 *      `FormGroup` field, TWO fields where only one is mounted, and a `Signal<FormGroup>` that is
 *      rebuilt during the screen's life. A gate that handled only the first would protect twelve
 *      screens and silently abandon two — and the two it abandoned would be the site-settings
 *      screen and the profile screen, one of which is where the loss was measured.
 *   2. **Invoking arbitrary component methods.** Unwrapping a signal means calling a function.
 *      If the test were `typeof value === 'function'` rather than `isSignal`, the gate would call
 *      every zero-argument method on the component during a navigation. That is not a style
 *      point: it would run business logic as a side effect of leaving a screen.
 *   3. **Challenging a departure the application itself initiated.** A save that succeeded and
 *      then navigated away would be met with a prompt offering to discard the work that had just
 *      been saved — turning the fix into a worse defect than the fault. Worse still on the
 *      session-lost path, where a prompt would offer to keep the operator on a screen whose
 *      session no longer exists.
 *   4. **Warning about a search box.** Every listing's filter control is a `FormGroup`, so a
 *      predicate applied indiscriminately would warn about "unsaved changes" for a search term
 *      the operator had typed and already seen applied.
 *   5. **Reversing the confirmation polarity.** `confirm` answers "leave", and the router wants
 *      "may proceed". They happen to agree, which is exactly why an accidental negation would
 *      look plausible and would discard work on every confirmation.
 *
 * The gate is deliberately NOT tested as a persistence mechanism. It warns; it does not buffer,
 * store or re-offer the work, and no assertion here implies otherwise.
 */

import { TestBed } from '@angular/core/testing';
import { FormControl, FormGroup } from '@angular/forms';
import { Router, provideRouter } from '@angular/router';
import { Component, computed, inject, signal } from '@angular/core';

import {
  DISCARD_CHANGES_PROMPT,
  UNSAVED_CHANGES_PROMPT,
  UnsavedChangesTracker,
  activeRouteGuardsUnsavedChanges,
  holdsUnsavedEdits,
  unsavedChangesGuard,
} from './unsaved-changes.guard';

import type { ActivatedRouteSnapshot, Navigation, RouterStateSnapshot } from '@angular/router';

describe('unsavedChangesGuard', () => {
  let router: Router;
  let confirmSpy: jasmine.Spy<(message?: string) => boolean>;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    router = TestBed.inject(Router);

    // The prompt is the browser's own, so it is stubbed rather than rendered. Defaulting to
    // "stay" means a case that forgets to state an answer fails closed instead of silently
    // asserting the permissive branch.
    confirmSpy = spyOn(window, 'confirm').and.returnValue(false);
  });

  /** A form with one control, dirtied only when asked. */
  function form(dirty: boolean): FormGroup {
    const group = new FormGroup({ name: new FormControl('', { nonNullable: true }) });

    if (dirty) {
      group.markAsDirty();
    }

    return group;
  }

  /**
   * Runs the gate the way the router runs it.
   *
   * `runInInjectionContext` because the gate injects the router to inspect the navigation in
   * progress; the router supplies such a context in production.
   */
  function runGuard(component: unknown): boolean {
    const decision = TestBed.runInInjectionContext(() =>
      unsavedChangesGuard(
        component,
        {} as ActivatedRouteSnapshot,
        {} as RouterStateSnapshot,
        {} as RouterStateSnapshot,
      ),
    );

    // The gate is documented as fully synchronous. A narrowing that accepted an observable or a
    // promise would weaken the claim being tested, so the shape is asserted before the cast.
    expect(typeof decision)
      .withContext('the gate decides synchronously; it returns neither observable nor promise')
      .toBe('boolean');

    return decision as boolean;
  }

  /** Reports a navigation in progress with the given behaviour options. */
  function navigationReplaces(replaceUrl: boolean | undefined): void {
    spyOn(router, 'getCurrentNavigation').and.returnValue({
      extras: replaceUrl === undefined ? {} : { replaceUrl },
    } as Navigation);
  }

  describe('deciding whether a screen holds unsaved edits', () => {
    it('lets a pristine form leave without asking anything', () => {
      expect(runGuard({ form: form(false) })).toBeTrue();
      expect(confirmSpy)
        .withContext('a form nobody has edited must not interrupt a departure')
        .not.toHaveBeenCalled();
    });

    it('lets a screen with no form at all leave without asking anything', () => {
      expect(runGuard({ heading: 'Portals', rows: [1, 2, 3] })).toBeTrue();
      expect(confirmSpy).not.toHaveBeenCalled();
    });

    it('tolerates a null or non-object component instead of failing', () => {
      // The router types the component as `T`, and a route with no component at all yields null.
      // Throwing here would break a navigation rather than merely mis-decide it.
      expect(runGuard(null)).toBeTrue();
      expect(runGuard(undefined)).toBeTrue();
      expect(confirmSpy).not.toHaveBeenCalled();
    });

    it('challenges a dirty form held as a plain field', () => {
      expect(runGuard({ form: form(true) })).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('challenges a dirty form whatever the field is called', () => {
      // No naming convention is relied on, so a screen is protected without having to be
      // remembered. `editForm` is the real name on the portal screen.
      expect(runGuard({ editForm: form(true) })).toBeFalse();
    });

    it('challenges a dirty form held inside a SIGNAL', () => {
      /*
       * The profile screen's shape. Its form is rebuilt when the property definitions arrive, so
       * a reference captured once would go stale — the gate has to read the signal at the moment
       * of departure. A gate that ignored signals would leave this screen unprotected, and
       * nothing else in the suite would notice.
       */
      const held = signal(form(true));

      expect(runGuard({ form: held })).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('re-reads the signal rather than a value captured earlier', () => {
      const source = signal(form(false));
      const component = { form: computed(() => source()) };

      expect(runGuard(component)).withContext('pristine at first').toBeTrue();

      // The screen replaces its form, exactly as the profile screen does when its definitions
      // arrive. The gate must see the replacement, not the group it saw a moment ago.
      source.set(form(true));

      expect(runGuard(component)).withContext('and dirty once the form is replaced').toBeFalse();
    });

    it('challenges when EITHER of two forms is dirty', () => {
      /*
       * The add-portal and edit-portal screen holds `createForm` and `editForm`, only one of
       * which is mounted at a time. A gate that stopped at the first form it found would answer
       * from whichever happened to be enumerated first — half right, and unpredictably so.
       */
      expect(runGuard({ createForm: form(false), editForm: form(true) })).toBeFalse();
      confirmSpy.calls.reset();
      expect(runGuard({ createForm: form(true), editForm: form(false) })).toBeFalse();
    });

    it('does not invoke a component method while deciding', () => {
      /*
       * ⚠ THE SAFETY PROPERTY. `isSignal` is what separates "a signal I may read" from "any
       * zero-argument function", and without it the gate would call component methods as a side
       * effect of leaving a screen. The method here throws so that an invocation cannot be
       * mistaken for a pass, and the counter proves it was never reached rather than merely
       * survived.
       */
      let invocations = 0;
      const component = {
        form: form(false),
        loadEverything: (): never => {
          invocations += 1;
          throw new Error('the gate must not call component methods');
        },
      };

      expect(() => runGuard(component)).not.toThrow();
      expect(invocations)
        .withContext('a plain function is never called; only a real signal is read')
        .toBe(0);
    });
  });

  describe('honouring the operator’s answer', () => {
    it('allows the departure when the operator chooses to leave', () => {
      confirmSpy.and.returnValue(true);

      expect(runGuard({ form: form(true) })).toBeTrue();
    });

    it('keeps the operator where they are when they decline', () => {
      confirmSpy.and.returnValue(false);

      // ⚠ POLARITY. `confirm` answers "leave" and the router wants "may proceed"; they agree, so
      // an accidental negation would read plausibly and would discard work on every
      // confirmation. Both directions are pinned so neither can be flipped unnoticed.
      expect(runGuard({ form: form(true) })).toBeFalse();
    });
  });

  describe('telling an application-initiated departure from an operator-initiated one', () => {
    it('does not challenge a departure that REPLACES its history entry', () => {
      /*
       * Every post-success and post-failure departure in this application replaces. Challenging
       * one would offer to discard work that had just been saved successfully. On the
       * session-lost path it would be worse still: a prompt offering to stay on a screen whose
       * session has already ended is offering something that no longer exists.
       */
      navigationReplaces(true);

      expect(runGuard({ form: form(true) })).toBeTrue();
      expect(confirmSpy)
        .withContext('the application moved the operator, so there is nothing to ask them')
        .not.toHaveBeenCalled();
    });

    it('challenges a departure that does not replace', () => {
      // A link, a Cancel button, the Back button. Cancel sits one click from Update, and
      // silently discarding a filled-in form is the measured defect.
      navigationReplaces(false);

      expect(runGuard({ form: form(true) })).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('challenges when the navigation states no preference', () => {
      navigationReplaces(undefined);

      expect(runGuard({ form: form(true) })).toBeFalse();
    });

    it('challenges when no navigation can be read at all', () => {
      // Typed as nullable, so it is treated as such. Challenging a departure that might not have
      // needed it is the safe direction; the alternative discards work silently.
      spyOn(router, 'getCurrentNavigation').and.returnValue(null);

      expect(runGuard({ form: form(true) })).toBeFalse();
    });
  });

  describe('the predicate the browser-exit channel shares', () => {
    it('answers the same question the gate asks', () => {
      // The shell's `beforeunload` handler imports this rather than restating it. Two definitions
      // of "holding unsaved edits" would eventually disagree, and the disagreement would appear
      // as a screen that warns when you click away but not when you reload.
      expect(holdsUnsavedEdits({ form: form(true) })).toBeTrue();
      expect(holdsUnsavedEdits({ form: form(false) })).toBeFalse();
      expect(holdsUnsavedEdits(null)).toBeFalse();
    });
  });

  describe('reading the protected-screen list back off the route table', () => {
    it('reports true only for a route that declares this gate', async () => {
      /*
       * ⚠ THE NEGATIVE HALF IS THE POINT. Without it the browser-exit channel would warn on the
       * listing screens, whose search boxes are dirty as soon as a filter is typed. The listing
       * route deliberately carries no `canDeactivate`, and this asserts that the difference is
       * observable rather than assumed.
       */
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideRouter([
            { path: 'list', children: [] },
            { path: 'edit', canDeactivate: [unsavedChangesGuard], children: [] },
          ]),
        ],
      });

      const testRouter = TestBed.inject(Router);

      await testRouter.navigateByUrl('/list');
      expect(activeRouteGuardsUnsavedChanges(testRouter))
        .withContext('a listing must not be treated as a protected form')
        .toBeFalse();

      await testRouter.navigateByUrl('/edit');
      expect(activeRouteGuardsUnsavedChanges(testRouter))
        .withContext('and a form route must be')
        .toBeTrue();
    });
  });
});


/**
 * Covers the route guard FUNCTION and the tracker's own decision rules.
 *
 * ⚠ WHY THIS FILE EXISTS SEPARATELY FROM THE NINE COMPONENT SPECS THAT ALREADY USE THE TRACKER.
 * Those specs register a probe and then assert on `isDirty()`, which exercises the tracker but
 * never the guard: `unsavedChangesGuard` is attached to ten routes across four feature route tables
 * and was referenced by NO spec at all, so the one line that actually raises the confirmation - and
 * the short-circuit that must NOT raise it - had no unit coverage. That gap sits directly underneath
 * the newest cases in this suite, because five screens now settle their own form on the success path
 * precisely so this guard does not fire on the navigation a save triggers. If the guard stopped
 * asking, or started asking unconditionally, every one of those cases would still pass.
 *
 * The confirmation is the BROWSER'S, so it is stubbed on `globalThis` rather than through a seam.
 * That is deliberate and it mirrors the production code, which calls `globalThis.confirm`; in a
 * browser `globalThis === window`, which is the property that made the runtime instrumentation used
 * to verify this behaviour trustworthy in the first place.
 */
describe('unsavedChangesGuard, through the mounted-screen tracker', () => {

  /** A throwaway host, needed only so `watch` has a destroy scope to attach to. */
  @Component({ standalone: true, template: '' })
  class ProbeHostComponent {
    /** Flipped by a test to make this mounted screen report unsaved entry. */
    public dirty = false;

    // Registered from a field initialiser, which is where the production screens register theirs and
    // where the injection context is active. Nothing is kept: the probe is released by this
    // component's own destruction.
    private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(() => this.dirty);
  }

  let tracker: UnsavedChangesTracker;
  let confirmSpy: jasmine.Spy;
  let nativeConfirm: typeof globalThis.confirm;

  /**
   * Runs the guard the way the router does, inside an injection context.
   *
   * The four arguments are the `CanDeactivateFn` contract. This guard reads none of them - it asks
   * the tracker - so they are supplied as the empty shapes the signature requires rather than as
   * fixtures that would imply the guard inspects them.
   *
   * @returns Whatever the guard decided.
   */
  function runGuard(): boolean {
    return TestBed.runInInjectionContext(
      () =>
        unsavedChangesGuard(
          {},
          {} as ActivatedRouteSnapshot,
          {} as RouterStateSnapshot,
          {} as RouterStateSnapshot,
        ) as boolean,
    );
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [ProbeHostComponent] });

    tracker = TestBed.inject(UnsavedChangesTracker);

    nativeConfirm = globalThis.confirm;
    confirmSpy = jasmine.createSpy('confirm').and.returnValue(true);
    globalThis.confirm = confirmSpy as unknown as typeof globalThis.confirm;
  });

  afterEach(() => {
    // Restored rather than left patched: a stubbed `confirm` leaking into a later spec file would
    // silently answer a prompt nobody asked about.
    globalThis.confirm = nativeConfirm;
  });

  it('leaves without asking when nothing is dirty', () => {
    expect(tracker.isDirty()).toBeFalse();

    expect(runGuard()).withContext('a clean application must never be questioned').toBeTrue();
    expect(confirmSpy).not.toHaveBeenCalled();
  });

  it('asks exactly once, with the authored sentence, when a screen holds unsaved entry', () => {
    const host = TestBed.createComponent(ProbeHostComponent);
    host.componentInstance.dirty = true;

    expect(runGuard()).toBeTrue();

    expect(confirmSpy).toHaveBeenCalledTimes(1);
    expect(confirmSpy).toHaveBeenCalledWith(UNSAVED_CHANGES_PROMPT);
  });

  it('stays on the screen when the operator declines', () => {
    const host = TestBed.createComponent(ProbeHostComponent);
    host.componentInstance.dirty = true;
    confirmSpy.and.returnValue(false);

    expect(runGuard()).withContext('declining must refuse the navigation').toBeFalse();
  });

  it('stops asking once the screen that was dirty has been destroyed', () => {
    const host = TestBed.createComponent(ProbeHostComponent);
    host.componentInstance.dirty = true;

    expect(tracker.isDirty()).toBeTrue();

    // The probe is released by the host's own destruction, which is why `watch` hands back no handle
    // to release manually: a screen that has gone can never hold a navigation up.
    host.destroy();

    expect(tracker.isDirty()).toBeFalse();
    expect(runGuard()).toBeTrue();
    expect(confirmSpy).not.toHaveBeenCalled();
  });

  it('treats a probe that throws as clean rather than turning a navigation into an error', () => {
    TestBed.runInInjectionContext(() => {
      tracker.watch(() => {
        throw new Error('a screen mid-teardown');
      });
    });

    expect(tracker.isDirty())
      .withContext('a misbehaving probe must not refuse a navigation')
      .toBeFalse();
    expect(runGuard()).toBeTrue();
    expect(confirmSpy).not.toHaveBeenCalled();
  });

  it('reports dirty when ANY of several mounted screens is, not only the last registered', () => {
    const clean = TestBed.createComponent(ProbeHostComponent);
    const dirty = TestBed.createComponent(ProbeHostComponent);

    clean.componentInstance.dirty = false;
    dirty.componentInstance.dirty = true;

    // The reason the tracker holds a SET of probes rather than a boolean: a boolean written by
    // whichever screen mounted last would let a clean screen clear a dirty screen's warning.
    expect(tracker.isDirty()).toBeTrue();
    expect(runGuard()).toBeTrue();
    expect(confirmSpy).toHaveBeenCalledTimes(1);
  });
});
