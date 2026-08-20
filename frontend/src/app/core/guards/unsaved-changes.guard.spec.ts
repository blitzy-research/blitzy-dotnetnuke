/**
 * Specification for {@link unsavedChangesGuard}, the gate that refuses to leave a form holding edits
 * nobody has saved. NET-NEW COVERAGE, NOT A PORTED TEST. The legacy tree contains no automated tests at
 * all, and it could not have contained one for this behaviour: every affordance on those pages was a
 * postback, so leaving a page meant submitting it and an unsaved intermediate state was unreachable.
 */

import { TestBed } from '@angular/core/testing';
import { FormControl, FormGroup } from '@angular/forms';
import { Router, provideRouter } from '@angular/router';
import { Component, computed, inject, signal } from '@angular/core';

import {
  DISCARD_CHANGES_PROMPT,
  UNSAVED_CHANGES_PROMPT,
  UnsavedChangesTracker,
  confirmDiscardUnsavedChanges,
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
   * Runs the gate the way the router runs it. `runInInjectionContext` because the gate injects the router
   * to inspect the navigation in progress; the router supplies such a context in production.
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
      // The four listing screens. Each holds a filter control that IS a `FormGroup` and is dirty as soon as
      // a term is typed, and none of them registers a probe - so a typed search term cannot reach this
      // gate.
      expect(runGuard({ searchForm: form(true), rows: [1, 2, 3] })).toBeTrue();
      expect(confirmSpy).not.toHaveBeenCalled();
    });

    it('challenges once, with the authored sentence, when a mounted screen holds unsaved entry', () => {
      screenHoldsUnsavedEntry();

      expect(runGuard({})).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('does not read the deactivating component AT ALL, which is what keeps forms out of the eager bundle', () => {
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
      // Every post-success and post-failure departure in this application replaces. Challenging one would
      // offer to discard work that had just been saved successfully.
      navigationReplaces(true, 'imperative');

      expect(runGuard({})).toBeTrue();
      expect(confirmSpy)
        .withContext('the application moved the operator, so there is nothing to ask them')
        .not.toHaveBeenCalled();
    });

    it('CHALLENGES a replacing departure the BROWSER initiated, so Back cannot discard work silently', () => {
      navigationReplaces(true, 'popstate');

      expect(runGuard({})).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('CHALLENGES a replacing departure driven by the address fragment', () => {
      navigationReplaces(true, 'hashchange');

      expect(runGuard({})).toBeFalse();
      expect(confirmSpy).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
    });

    it('challenges a departure that does not replace', () => {
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
     * Every screen the real route tables protect with this gate. ⚠ THIS LIST IS A TRIPWIRE, NOT
     * DOCUMENTATION, and the obligation it enforces is easy to forget precisely because forgetting it
     * produces no error.
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
 * Covers the route guard FUNCTION and the tracker's own decision rules. ⚠ WHY THIS FILE EXISTS SEPARATELY
 * FROM THE NINE COMPONENT SPECS THAT ALREADY USE THE TRACKER. Those specs register a probe and then
 * assert on `isDirty()`, which exercises the tracker but never the guard: `unsavedChangesGuard` is
 * attached to ten routes across four feature route tables and was referenced by NO spec at all, so the
 * one line that actually raises the confirmation - and the short-circuit that must NOT raise it - had no
 * unit coverage.
 */
describe('unsavedChangesGuard, through the mounted-screen tracker', () => {
  /** A throwaway host, needed only so `watch` has a destroy scope to attach to. */
  @Component({ standalone: true, template: '' })
  class ProbeHostComponent {
    /** Flipped by a test to make this mounted screen report unsaved entry. */
    public dirty = false;

    // Registered from a field initialiser, which is where the production screens register theirs and where
    // the injection context is active. Nothing is kept: the probe is released by this component's own
    // destruction.
    private readonly unsavedEntry = inject(UnsavedChangesTracker).watch(() => this.dirty);
  }

  let tracker: UnsavedChangesTracker;
  let confirmSpy: jasmine.Spy;
  let nativeConfirm: typeof globalThis.confirm;

  /**
   * Runs the guard the way the router does, inside an injection context. The four arguments are the
   * `CanDeactivateFn` contract.
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

  // -----------------------------------------------------------------------------------------------------
  // ASKING BEFORE ACTING
  // -----------------------------------------------------------------------------------------------------

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED. Logout discarded a dirty form in SILENCE while navigating
  // from the very same form raised the confirmation. The cause was ordering rather than a missing gate: the
  // shell revoked the credential and tore the session down BEFORE the router could reach `canDeactivate`, so
  // the question either arrived too late to be answerable or could be answered "no" and leave the operator
  // holding unsaved work on a screen whose session had already ended.

  describe('a caller that must ask before it acts', () => {
    it('lets a clean application through without asking anything', () => {
      expect(tracker.confirmDiscard()).toBeTrue();
      expect(confirmSpy)
        .withContext('nothing is at stake, so nothing is asked')
        .not.toHaveBeenCalled();
    });

    it('asks the SAME question the router would ask, and reports the answer', () => {
      const host = TestBed.createComponent(ProbeHostComponent);

      host.componentInstance.dirty = true;

      expect(tracker.confirmDiscard()).toBeTrue();
      expect(confirmSpy).toHaveBeenCalledOnceWith(UNSAVED_CHANGES_PROMPT);
    });

    it('refuses the caller when the operator declines, so nothing irreversible happens', () => {
      const host = TestBed.createComponent(ProbeHostComponent);

      host.componentInstance.dirty = true;
      confirmSpy.and.returnValue(false);

      expect(tracker.confirmDiscard())
        .withContext('declining must abandon the whole gesture, not merely the navigation')
        .toBeFalse();
    });

    it('does not ask twice: the router admits the departure the operator already consented to', () => {
      const host = TestBed.createComponent(ProbeHostComponent);

      host.componentInstance.dirty = true;

      expect(tracker.confirmDiscard()).toBeTrue();
      expect(confirmSpy).toHaveBeenCalledTimes(1);

      expect(runGuard())
        .withContext('the navigation that follows carries the answer already given')
        .toBeTrue();
      expect(confirmSpy)
        .withContext('and asks nothing further')
        .toHaveBeenCalledTimes(1);
    });

    it('records the answer for ONE departure only, so consent cannot be reused', () => {
      const host = TestBed.createComponent(ProbeHostComponent);

      host.componentInstance.dirty = true;

      expect(tracker.confirmDiscard()).toBeTrue();
      expect(runGuard()).toBeTrue();

      // A second, unrelated departure is a new question.
      expect(runGuard()).toBeTrue();
      expect(confirmSpy)
        .withContext('the second departure is challenged on its own account')
        .toHaveBeenCalledTimes(2);
    });

    it('leaves no consent behind when the operator declined', () => {
      const host = TestBed.createComponent(ProbeHostComponent);

      host.componentInstance.dirty = true;
      confirmSpy.and.returnValue(false);

      expect(tracker.confirmDiscard()).toBeFalse();

      confirmSpy.calls.reset();
      confirmSpy.and.returnValue(false);

      expect(runGuard())
        .withContext('a refusal grants nothing, so the next departure is still challenged')
        .toBeFalse();
      expect(confirmSpy).toHaveBeenCalledTimes(1);
    });

    it('does not leave a stale consent behind when the departure never happens', () => {
      const host = TestBed.createComponent(ProbeHostComponent);

      host.componentInstance.dirty = true;

      expect(tracker.confirmDiscard()).toBeTrue();

      // The gesture is abandoned before any navigation, and the screen is edited again.
      host.componentInstance.dirty = false;

      expect(runGuard())
        .withContext('a clean application leaves freely; the record is consumed here')
        .toBeTrue();

      host.componentInstance.dirty = true;
      confirmSpy.calls.reset();

      expect(runGuard()).toBeTrue();
      expect(confirmSpy)
        .withContext('and the next dirty departure is challenged rather than waved through')
        .toHaveBeenCalledTimes(1);
    });
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

/**
 * Specification for {@link confirmDiscardUnsavedChanges}, the one place the discard question is asked.
 *
 * ⚠ EXPORTED BECAUSE A SECOND CALLER NEEDED IT, AND THAT CALLER IS WHY IT IS COVERED HERE. Measured on the
 * profile screen, the in-form Cancel button discarded unsaved entry in silence while the sidebar and the
 * browser's Back button beside it both refused until the operator confirmed. Both paths now put THIS question,
 * so a change to the sentence cannot reach one exit and miss the other.
 */
describe('confirmDiscardUnsavedChanges', () => {
  it('puts the shared sentence and reports acceptance', () => {
    const asked = spyOn(globalThis, 'confirm').and.returnValue(true);

    expect(confirmDiscardUnsavedChanges()).toBeTrue();
    expect(asked).toHaveBeenCalledOnceWith(DISCARD_CHANGES_PROMPT);
  });

  it('reports a refusal as a refusal, so the caller keeps the entry', () => {
    spyOn(globalThis, 'confirm').and.returnValue(false);

    expect(confirmDiscardUnsavedChanges()).toBeFalse();
  });
});
