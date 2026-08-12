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
 * The gate reads ONE source of truth - the probes mounted screens have registered with
 * {@link UnsavedChangesTracker} - and each of the following fails without any compiler complaint
 * and without any other specification noticing:
 *
 *   1. **A protected route reaching no probe.** The gate is declared on eighteen routes naming
 *      fourteen components, and a component that declares the gate without registering a probe is
 *      not merely unprotected: it LOOKS protected in the route table, which is worse than an
 *      obvious omission. The correspondence is asserted below by walking the real route tables,
 *      and each of the fourteen components proves its own probe in its own specification.
 *   2. **Inspecting the deactivating component.** The gate used to reflect over the component's
 *      fields looking for a dirty `FormGroup`. That is gone, and it must stay gone: it made
 *      `@angular/forms` reachable from the eager import graph of an application whose every form
 *      screen is lazily loaded, and it could only ever see dirty state that happened to be a
 *      `FormGroup` FIELD - so state held in a signal, in a child component or in anything else
 *      was silently unprotected while appearing covered. The case below proves the component is
 *      not read at all, which is the property that keeps the eager graph forms-free.
 *   3. **Challenging a departure the application itself initiated.** A save that succeeded and
 *      then navigated away would be met with a prompt offering to discard the work that had just
 *      been saved — turning the fix into a worse defect than the fault. Worse still on the
 *      session-lost path, where a prompt would offer to keep the operator on a screen whose
 *      session no longer exists.
 *   4. **Warning about a search box.** Every listing's filter control is a `FormGroup`. Under
 *      reflection that was a live hazard held back by reading `canDeactivate` off the active
 *      route; under registration it cannot arise, because a listing registers no probe. The route
 *      reader survives for the correspondence walk, and the negative half of that walk is what
 *      keeps the four listings observably outside the protected set.
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
  unsavedChangesGuard,
} from './unsaved-changes.guard';

import { APP_ROUTES } from '../../app.routes';

import type { ActivatedRouteSnapshot, Navigation, Route, RouterStateSnapshot } from '@angular/router';

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

  /**
   * Reports a navigation in progress with the given behaviour options and trigger.
   *
   * ⚠ THE TRIGGER IS PART OF THE FIXTURE NOW, AND ITS ABSENCE HID A DATA-LOSS DEFECT. The gate used to
   * read `replaceUrl` alone, on the belief that replacing identified a departure the application itself
   * had initiated. The router sets the very same flag on every navigation the BROWSER drives - a popstate
   * has already moved the history entry by the time the router hears about it, so replacing is the only way
   * to stay in step - which meant Back and Forward inherited an exemption written for a successful save. A
   * browser audit reproduced it: a dirty form challenged a sidebar click correctly and let `history.back()`
   * discard the edits with no prompt at all. Defaulted to `'imperative'` so every existing case keeps its
   * meaning, and the two browser-driven triggers are exercised explicitly below.
   *
   * @param replaceUrl What the navigation's behaviour options state, or undefined to state nothing.
   * @param trigger What initiated the navigation.
   */
  function navigationReplaces(
    replaceUrl: boolean | undefined,
    trigger: Navigation['trigger'] = 'imperative',
  ): void {
    spyOn(router, 'getCurrentNavigation').and.returnValue({
      extras: replaceUrl === undefined ? {} : { replaceUrl },
      trigger,
    } as Navigation);
  }

  /**
   * Registers a probe reporting unsaved entry, exactly as a protected screen does.
   *
   * ⚠ THE FIXTURE IS THE CONTRACT NOW. The gate used to be handed a component and reflect over its
   * fields for a dirty `FormGroup`; it is handed a registration instead, because the screen is the
   * only party that knows what "unsaved" means for it - which form, whether a save is in flight,
   * whether the editor is even open. Everything below therefore states dirtiness the way production
   * states it.
   *
   * `runInInjectionContext` because `watch` attaches the release to the caller's `DestroyRef`, the
   * way a component field initialiser does.
   */
  function screenHoldsUnsavedEntry(): void {
    TestBed.runInInjectionContext(() => {
      TestBed.inject(UnsavedChangesTracker).watch(() => true);
    });
  }

  describe('deciding whether to challenge a departure', () => {
    it('lets a clean application leave without asking anything', () => {
      expect(runGuard({ form: form(false) })).toBeTrue();
      expect(confirmSpy)
        .withContext('a form nobody has edited must not interrupt a departure')
        .not.toHaveBeenCalled();
    });

    it('lets a screen that registered no probe at all leave without asking anything', () => {
      // The four listing screens. Each holds a filter control that IS a `FormGroup` and is dirty as
      // soon as a term is typed, and none of them registers a probe - so a typed search term cannot
      // reach this gate. Under the reflective predicate this was a live misfire held back only by
      // reading the route table; under registration it is structurally impossible.
      expect(runGuard({ searchForm: form(true), rows: [1, 2, 3] })).toBeTrue();
      expect(confirmSpy).not.toHaveBeenCalled();
    });

    it('challenges once, with the authored sentence, when a mounted screen holds unsaved entry', () => {
      screenHoldsUnsavedEntry();

      expect(runGuard({})).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('does not read the deactivating component AT ALL, which is what keeps forms out of the eager bundle', () => {
      /*
       * ⚠ THE PROPERTY THIS WHOLE CHANGE EXISTS FOR, AND IT IS ASSERTED IN BOTH DIRECTIONS.
       *
       * Positively: a component carrying a dirty `FormGroup` under every field name the application
       * actually uses is NOT challenged, because it registered nothing. Reintroducing a reflective
       * sweep would fail here immediately.
       *
       * Negatively: nothing on the component is touched. The accessors below throw, and a getter
       * enumerated by a sweep would turn a navigation into an unhandled error rather than a
       * mis-decision. The counter proves the fields were never reached rather than merely survived.
       *
       * The cost of the sweep was not theoretical. `unsaved-changes.guard.ts` is imported by
       * `app.routes.ts`, which is eager, so a `FormGroup` reference here put `@angular/forms` in the
       * eager import graph of an application in which every screen holding a form is lazily loaded.
       */
      let reads = 0;
      const component = {
        get form(): never {
          reads += 1;
          throw new Error('the gate must not read the component');
        },
        get editForm(): never {
          reads += 1;
          throw new Error('the gate must not read the component');
        },
        get createForm(): never {
          reads += 1;
          throw new Error('the gate must not read the component');
        },
        loadEverything: (): never => {
          reads += 1;
          throw new Error('the gate must not call component methods');
        },
      };

      expect(() => runGuard(component)).not.toThrow();
      expect(runGuard(component))
        .withContext('a dirty form the screen did not register is not this gate’s business')
        .toBeTrue();
      expect(reads).withContext('no field and no method on the component is reached').toBe(0);
      expect(confirmSpy).not.toHaveBeenCalled();
    });

    it('tolerates a null or non-object component instead of failing', () => {
      // The router types the component as `T`, and a route with no component at all yields null.
      // Throwing here would break a navigation rather than merely mis-decide it.
      expect(runGuard(null)).toBeTrue();
      expect(runGuard(undefined)).toBeTrue();
      expect(confirmSpy).not.toHaveBeenCalled();
    });

    it('challenges whatever shape the screen holds its entry in, because the screen owns the question', () => {
      /*
       * The three shapes that used to need three separate reflective cases - a plain field, two
       * fields of which one is mounted, and a form rebuilt into a signal during the screen's life -
       * collapse into this one. A probe is a closure over whatever the screen actually holds, so a
       * screen keeping its entry in a signal, in a child component, or in something that is not a
       * `FormGroup` at all is protected on exactly the same terms. That is a widening of coverage
       * rather than a narrowing: the sweep could only ever see a `FormGroup` field.
       */
      const rebuilt = signal(form(false));

      TestBed.runInInjectionContext(() => {
        TestBed.inject(UnsavedChangesTracker).watch(() => rebuilt().dirty);
      });

      expect(runGuard({})).withContext('pristine at first').toBeTrue();

      // The screen replaces its form, as the profile screen does when its definitions arrive.
      rebuilt.set(form(true));

      expect(runGuard({})).withContext('and dirty once the form is replaced').toBeFalse();
    });
  });

  describe('honouring the operator’s answer', () => {
    // Every case in this block needs a screen holding unsaved entry; the answer is what is under
    // test, not the detection.
    beforeEach(() => {
      screenHoldsUnsavedEntry();
    });

    it('allows the departure when the operator chooses to leave', () => {
      confirmSpy.and.returnValue(true);

      expect(runGuard({})).toBeTrue();
    });

    it('keeps the operator where they are when they decline', () => {
      confirmSpy.and.returnValue(false);

      // ⚠ POLARITY. `confirm` answers "leave" and the router wants "may proceed"; they agree, so
      // an accidental negation would read plausibly and would discard work on every
      // confirmation. Both directions are pinned so neither can be flipped unnoticed.
      expect(runGuard({})).toBeFalse();
    });
  });

  describe('telling an application-initiated departure from an operator-initiated one', () => {
    // Likewise: the screen holds unsaved entry throughout, and what varies is what initiated the
    // departure.
    beforeEach(() => {
      screenHoldsUnsavedEntry();
    });

    it('does not challenge a departure the APPLICATION initiated and replaced', () => {
      /*
       * Every post-success and post-failure departure in this application replaces. Challenging
       * one would offer to discard work that had just been saved successfully. On the
       * session-lost path it would be worse still: a prompt offering to stay on a screen whose
       * session has already ended is offering something that no longer exists.
       *
       * Both facts are now required, which is the correction: replacing on its own no longer earns
       * the exemption, because the browser's own navigations replace too.
       */
      navigationReplaces(true, 'imperative');

      expect(runGuard({})).toBeTrue();
      expect(confirmSpy)
        .withContext('the application moved the operator, so there is nothing to ask them')
        .not.toHaveBeenCalled();
    });

    it('CHALLENGES a replacing departure the BROWSER initiated, so Back cannot discard work silently', () => {
      // ⚠ THE DEFECT THIS GATE EXISTS TO PREVENT, REACHED THROUGH THE ONE DOOR IT LEFT OPEN. Angular sets
      // `replaceUrl: true` on a popstate navigation because the history entry has already moved, so a
      // guard reading that flag alone treated every press of Back as a departure the application had asked
      // for. A browser audit measured the result on a dirty `/roles/new`: a sidebar click prompted, and
      // `history.back()` changed the route with no prompt and discarded the edits. `beforeunload` does not
      // cover it either - the tracker registers it once and a same-document navigation does not fire it
      // at all - so this gate was the only thing standing between the Back button and unsaved work.
      navigationReplaces(true, 'popstate');

      expect(runGuard({})).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('CHALLENGES a replacing departure driven by the address fragment', () => {
      // The second browser-driven trigger, held to the same rule. The admissible trigger is named
      // positively rather than the inadmissible ones being listed, so a trigger added by a future router
      // arrives challenged rather than exempt - which is the direction this gate must fail in.
      navigationReplaces(true, 'hashchange');

      expect(runGuard({})).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('challenges a departure that does not replace', () => {
      // A link or a Cancel button. Cancel sits one click from Update, and silently discarding a
      // filled-in form is the measured defect.
      //
      // The Back button used to be listed here and does NOT belong: it replaces. It is covered by the
      // popstate case above, which is where the defect actually lived.
      navigationReplaces(false);

      expect(runGuard({})).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('challenges when the navigation states no preference', () => {
      navigationReplaces(undefined);

      expect(runGuard({})).toBeFalse();
    });

    it('challenges when no navigation can be read at all', () => {
      // Typed as nullable, so it is treated as such. Challenging a departure that might not have
      // needed it is the safe direction; the alternative discards work silently.
      spyOn(router, 'getCurrentNavigation').and.returnValue(null);

      expect(runGuard({})).toBeFalse();
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

  describe('the correspondence between protected routes and probe-registering screens', () => {
    /**
     * Every screen the real route tables protect with this gate.
     *
     * ⚠ THIS LIST IS A TRIPWIRE, NOT DOCUMENTATION, and the obligation it enforces is easy to
     * forget precisely because forgetting it produces no error. A route declaring
     * `unsavedChangesGuard` whose component registers no probe with {@link UnsavedChangesTracker}
     * is not merely unprotected - it LOOKS protected in the route table, and the gate will read it
     * as clean and discard the operator's entry in silence. Under the reflective predicate the
     * registration was implicit and this could not happen; under registration it can, so it is
     * asserted.
     *
     * ⚠ WHAT TO DO WHEN THIS CASE FAILS. Do not simply add the name. Add, in the component:
     * `private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(() => <this screen holds
     * entry>);` - naming ITS form field, which is not always `form`, so reaching for `form` where the
     * group is called something else would leave the screen unprotected while looking protected. Then
     * add a case to that component's own specification asserting
     * `TestBed.inject(UnsavedChangesTracker).isDirty()`, which is how each of these fourteen proves
     * its own probe. Only then add the name here.
     *
     * ⚠ AND IT ALREADY EARNED ITS KEEP IN THE OTHER DIRECTION. The set was briefly fifteen screens
     * across nineteen routes, the extra pair being the account self-service panel. That screen and its
     * `:userId/services` address are withdrawn - the route table is frozen at twenty-five screens and
     * names no member-services address - and this walk is what reported the stale expectation instead
     * of letting a tripwire quietly guard a screen that no longer exists.
     *
     * Sorted, because the walk's order follows the route tables and a reordering there is not a
     * change in the protected set.
     */
    const PROTECTED_SCREENS: readonly string[] = [
      'MembershipSettingsComponent',
      'ModuleFormComponent',
      'ModuleImportComponent',
      'ModuleSettingsComponent',
      'PortalAliasListComponent',
      'PortalFormComponent',
      'PortalSettingsComponent',
      'ProfileDefinitionListComponent',
      'RoleAssignmentComponent',
      'RoleFormComponent',
      'RoleGroupFormComponent',
      'UserFormComponent',
      'UserPasswordComponent',
      'UserProfileComponent',
    ];

    /**
     * How many routes across every table declare the gate.
     *
     * Eighteen rather than nineteen: the account self-service panel and its `:userId/services` address
     * are withdrawn, so the route that declared the gate for it is gone with it.
     */
    const PROTECTED_ROUTE_COUNT = 18;

    /**
     * The child routes of one route, resolving a lazily loaded feature table.
     *
     * @param route The route to descend into.
     * @returns Its children, or an empty list when it has none.
     */
    async function childrenOf(route: Route): Promise<readonly Route[]> {
      if (route.children !== undefined) {
        return route.children;
      }

      if (route.loadChildren === undefined) {
        return [];
      }

      const loaded: unknown = await route.loadChildren();

      return Array.isArray(loaded) ? (loaded as readonly Route[]) : [];
    }

    /**
     * The class name of the component one route mounts.
     *
     * Resolves `loadComponent` for real rather than reading the import path as text, so a route
     * pointing at the wrong module is a failure here rather than a surprise at run time.
     *
     * @param route The route to resolve.
     * @returns The component's class name, or a marker when the route mounts none.
     */
    async function componentNameOf(route: Route): Promise<string> {
      if (route.component !== undefined) {
        return route.component.name;
      }

      if (route.loadComponent === undefined) {
        return '(route mounts no component)';
      }

      const loaded: unknown = await route.loadComponent();

      if (typeof loaded === 'function') {
        return loaded.name;
      }

      const asDefaultExport = loaded as { default?: { name?: string } };

      return asDefaultExport.default?.name ?? '(unresolvable component)';
    }

    /**
     * Walks the whole route tree and collects what the gate is declared on.
     *
     * @param routes The routes to walk.
     * @param found Accumulates the component names and the route count.
     */
    async function walk(
      routes: readonly Route[],
      found: { names: Set<string>; routes: number },
    ): Promise<void> {
      for (const route of routes) {
        if (route.canDeactivate?.includes(unsavedChangesGuard) === true) {
          found.routes += 1;
          found.names.add(await componentNameOf(route));
        }

        await walk(await childrenOf(route), found);
      }
    }

    it('declares the gate only on screens that register an unsaved-entry probe', async () => {
      const found = { names: new Set<string>(), routes: 0 };

      await walk(APP_ROUTES, found);

      // The count is asserted as well as the set, because two routes can name one component - the
      // create and edit addresses of a portal, a module, a role and an account all do - so a new
      // protected route on an ALREADY protected screen would change the count and not the set. It
      // still deserves a deliberate decision, because the new address may hold its entry
      // differently.
      expect(found.routes)
        .withContext('the number of routes declaring this gate; see PROTECTED_SCREENS on a failure')
        .toBe(PROTECTED_ROUTE_COUNT);

      expect([...found.names].sort())
        .withContext('every protected screen must register a probe; see PROTECTED_SCREENS')
        .toEqual([...PROTECTED_SCREENS].sort());
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
