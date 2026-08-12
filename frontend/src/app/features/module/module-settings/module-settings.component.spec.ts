import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';

import { ModuleSettingsComponent } from './module-settings.component';
import {
  MODULE_VISIBILITY,
  type ModuleDefinition,
  type ModuleDetail,
  type ModuleSettingsBag,
  type UpdateModuleRequest,
} from '../../../core/models/module.model';
import type { ValidationProblemDetails } from '../../../core/models/problem-details.model';
import type { TabListItem } from '../../../core/models/tab.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import type { AuthSession, CurrentUser } from '../../../core/models/auth.model';
import type { ModuleSettingsSeed } from './module-settings.component';

// =====================================================================================================
// THE SESSION THE CATALOGUE READ IS CONDITIONED ON
//
// The advisory key list is requested only when the caller administers the tenant, because the catalogue
// endpoint is declared under the administrator policy and would otherwise answer 403. Neither
// `AuthStore.holdsPortalAdministration` nor `AuthStore.permissions` is writable — both are projections
// over the stored session — so storing a real session is the only supported way to state that standing,
// and it is the same instrument the shared permission directive's own specification reaches for.
//
// Every case OUTSIDE the advisory describe leaves the session absent, which is why none of them sees a
// catalogue request: the read is withheld, and the suite's `httpMock.verify()` proves it stayed withheld
// rather than merely going unasserted.
// =====================================================================================================

/** Builds a caller snapshot carrying exactly the standing each case needs. */
function userWith(administersPortal: boolean): CurrentUser {
  return {
    userId: 3,
    portalId: -1,
    portalName: 'Runtime Portal',
    username: 'runtime_operator',
    displayName: 'Runtime Operator',
    email: 'operator@runtime.test',
    // Host standing is a separate fact and is deliberately not set: the condition reads the tenant
    // determination, so leaving this false keeps each case honest about what admitted the read.
    isSuperUser: false,
    isPortalAdministrator: administersPortal,
    roles: administersPortal ? ['Administrators'] : [],
    // The keys the CALLER holds are irrelevant here and are deliberately empty: this screen reads the
    // keys the DEFINITION declares, which is a different fact, and an administrator named in no grant
    // row legitimately holds none.
    permissions: [],
  };
}

/** Wraps a caller snapshot in a session the storage service accepts. */
function sessionWith(administersPortal: boolean): AuthSession {
  return {
    accessToken: 'access-token-placeholder',
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    refreshToken: 'refresh-token-placeholder',
    mustChangePassword: false,
    mustUpdateProfile: false,
    passwordExpiring: false,
    user: userWith(administersPortal),
  };
}

/**
 * The sixteen members the server's update contract declares, and the only members a submission may carry.
 *
 * Spelled out here rather than derived from the type, so that widening the wire contract to re-admit a
 * value the server discards fails this expectation instead of passing silently. This is the regression
 * guard for the eight members that were removed: six placement values the server does not project, and
 * two superseded spellings of the instruction flags.
 */
const UPDATE_CONTRACT_MEMBERS: readonly string[] = [
  'allTabs',
  'applyToAllModules',
  'cacheTime',
  'displayTitle',
  'endDate',
  'footer',
  'header',
  'iconFile',
  'inheritViewPermissions',
  'isDeleted',
  'moduleOrder',
  'moduleTitle',
  // The relocation destination, distinct from 'tabId'. That member SELECTS the placement being edited; this
  // one names the page it is moving to. Conflating them made the move-to-page control unusable.
  'moveToTabId',
  'setAsDefaultSettings',
  'startDate',
  'tabId',
  'visibility',
];

/**
 * Builds a fully populated module, overriding only what a test cares about.
 *
 * Every field carries a value distinguishable from its own default, so a test that asserts a seeded value
 * cannot pass by accident on a control that was never written.
 *
 * @param overrides The fields to replace.
 * @returns The module state.
 */
function moduleOf(overrides: Partial<ModuleSettingsSeed> = {}): ModuleSettingsSeed {
  return {
    moduleId: 0,
    tabModuleId: 31,
    tabId: 7,
    portalId: 0,
    moduleDefId: 14,
    friendlyName: 'Announcements',
    moduleTitle: 'Latest News',
    moduleOrder: 6,
    allTabs: true,
    isDeleted: false,
    inheritViewPermissions: false,
    header: 'Header markup',
    footer: 'Footer markup',
    startDate: '2024-03-01T00:00:00Z',
    endDate: '2024-12-31T00:00:00Z',
    cacheTime: 120,
    iconFile: 'module.gif',
    visibility: MODULE_VISIBILITY.minimized,
    displayTitle: false,
    ...overrides,
  };
}

/**
 * Builds the module read payload the server publishes, overriding only what a test cares about.
 *
 * Every one of the twenty-three members the read decoder requires is present, and the falsy ones are
 * present EXPLICITLY rather than omitted. That is not defensive padding: the API serialises with
 * `DefaultIgnoreCondition = JsonIgnoreCondition.Never`, so `-1`, `0`, `''` and `false` all appear in the
 * real JSON, and a fixture that dropped them would be testing a wire shape the server never sends.
 *
 * @param overrides The members to replace.
 * @returns A complete module read payload.
 */
function moduleDetailOf(overrides: Partial<ModuleDetail> = {}): ModuleDetail {
  return {
    // 0 is a REAL module: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`.
    moduleId: 0,
    tabModuleId: 31,
    // 0 is a REAL page: `dbo.Tabs.TabID` is `IDENTITY(0, 1)`.
    tabId: 0,
    // 0 is a REAL tenant: `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`, so -1 and 0 are both real.
    portalId: 0,
    moduleDefId: 14,
    desktopModuleId: 3,
    moduleTitle: 'Latest News',
    allTabs: false,
    header: '',
    footer: '',
    startDate: null,
    endDate: null,
    inheritViewPermissions: false,
    isDeleted: false,
    moduleOrder: 0,
    cacheTime: 0,
    iconFile: '',
    visibility: MODULE_VISIBILITY.maximized,
    displayTitle: false,
    friendlyName: 'Announcements',
    moduleName: 'DNN_Announcements',
    description: null,
    version: '01.00.00',
    ...overrides,
  };
}

/**
 * Builds the definition read payload, which is the ONLY carrier of the cache default.
 *
 * `defaultCacheTime` defaults to 0 here, matching `ModuleDefinitionInfo.vb`, whose constructor sets
 * `_DefaultCacheTime = 0`. A default of 0 means "caching supported, zero-second default" and is a
 * different fact from -1, which means "this definition records no default at all".
 *
 * @param overrides The members to replace.
 * @returns A complete definition read payload.
 */
function definitionOf(overrides: Partial<ModuleDefinition> = {}): ModuleDefinition {
  return {
    moduleDefId: 14,
    friendlyName: 'Announcements',
    desktopModuleId: 3,
    defaultCacheTime: 0,
    moduleName: 'DNN_Announcements',
    description: null,
    version: '01.00.00',
    isPremium: false,
    isAdmin: false,
    isPortable: true,
    ...overrides,
  };
}

/**
 * Builds one page of the tenant's page list for the "Move To Page" picker.
 *
 * `tabId` defaults to 0 and `parentId` to -1, because both are real values this schema produces: `TabID`
 * is `IDENTITY(0, 1)` so the first page is 0, and a root-level page records -1 as its parent. Neither may
 * be read as "absent".
 *
 * @param overrides The members to replace.
 * @returns A complete page list entry.
 */
function tabOf(overrides: Partial<TabListItem> = {}): TabListItem {
  return {
    tabId: 0,
    tabName: 'Home',
    title: null,
    tabOrder: 1,
    parentId: -1,
    level: 0,
    tabPath: '//Home',
    isVisible: true,
    disableLink: false,
    isDeleted: false,
    hasChildren: false,
    isSecure: false,
    url: null,
    iconFile: null,
    ...overrides,
  };
}

/**
 * Builds the settings bag the placement read returns.
 *
 * Two maps, held apart exactly as the server returns them: one is recorded against the module and applies
 * on every page it appears on, the other belongs to one occurrence on one page, and they land in two
 * different tables.
 *
 * @param overrides The members to replace.
 * @returns A complete settings bag.
 */
function settingsBagOf(overrides: Partial<ModuleSettingsBag> = {}): ModuleSettingsBag {
  return {
    moduleId: 0,
    tabModuleId: 31,
    moduleSettings: {},
    tabModuleSettings: {},
    ...overrides,
  };
}

describe('ModuleSettingsComponent', () => {
  let fixture: ComponentFixture<ModuleSettingsComponent>;
  let component: ModuleSettingsComponent;

  /**
   * The testing backend, asserted against in `afterEach` so no request escapes unaccounted for.
   *
   * Held at the outermost scope on purpose: the guard has to apply to EVERY spec in the file, including the
   * presentational ones that are expected to issue nothing at all, because "issues nothing" is itself a
   * claim worth proving.
   */
  let httpMock: HttpTestingController;

  /**
   * Assigns an input the way an OnPush component requires.
   *
   * Writing to the field directly does not mark the component dirty, so the view would not re-render and
   * every assertion after it would read stale markup.
   *
   * @param name The input's name.
   * @param value The value to set.
   */
  function setInput(
    name: 'settings' | 'loading' | 'saving' | 'canDelete' | 'canManageAllPages' | 'heading' | 'pages',
    value: unknown,
  ): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  /** The component's host element. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  /**
   * The first element matching a selector, or `null`.
   *
   * @param selector A CSS selector.
   * @returns The element, or `null`.
   */
  function q<T extends HTMLElement>(selector: string): T | null {
    return host().querySelector<T>(selector);
  }

  /**
   * Every element matching a selector.
   *
   * @param selector A CSS selector.
   * @returns The elements.
   */
  function qa<T extends HTMLElement>(selector: string): T[] {
    return Array.from(host().querySelectorAll<T>(selector));
  }

  /** Every disclosure head on the screen, in document order. */
  function toggles(): HTMLButtonElement[] {
    return qa<HTMLButtonElement>('.module-settings__toggle');
  }

  /**
   * The disclosure head whose text is the given heading and whose nesting matches.
   *
   * Two regions legitimately share the heading 'Basic Settings' and two share 'Advanced Settings', so a head
   * is located by its identifier rather than by its wording.
   *
   * @param section The region key.
   * @returns The head.
   */
  function toggleFor(section: string): HTMLButtonElement | null {
    return q<HTMLButtonElement>(`#module-settings-section-${section}`);
  }

  /**
   * Ensures a region is open, clicking its head only when it is currently closed.
   *
   * The head is a toggle, so this helper must be idempotent: a test that re-seeds the component and then
   * asks for the form again would otherwise CLOSE everything the previous call opened. The disclosure state
   * deliberately survives a re-seed — an operator's open regions should not snap shut when the data
   * refreshes — so "open" cannot be implemented as an unconditional click.
   *
   * @param section The region key.
   */
  function open(section: string): void {
    const head = toggleFor(section);
    expect(head).withContext(`the head for ${section} must be rendered`).not.toBeNull();
    // Optionally chained rather than non-null asserted: the expectation above is what fails the spec if
    // the element is missing, so the call itself needs no assertion operator.
    if (head?.getAttribute('aria-expanded') === 'false') {
      head?.click();
      fixture.detectChanges();
    }
  }

  /**
   * Opens every region, so the whole form is in the document.
   *
   * The order matters: a nested region's head does not exist until its parent is open, so `pageSettings`
   * must be opened before `other`.
   */
  function openEverything(): void {
    for (const section of ['security', 'pageSettings', 'other', 'specificSettings']) {
      open(section);
    }
  }

  /**
   * A form control element by field name.
   *
   * @param field The field name.
   * @returns The element, or `null`.
   */
  function field<T extends HTMLElement>(fieldName: string): T | null {
    return q<T>(`#module-settings-${fieldName}`);
  }

  /**
   * The shared field region a named control sits inside.
   *
   * ⚠ FOUND BY WALKING UP FROM THE CONTROL, NEVER BY POSITION OR BY A DERIVED IDENTIFIER. Every field on
   * this screen is now an `app-form-field`, which owns its own identifier scheme: the help paragraph is
   * `<for>-help` and the message region is `<for>-error`, both derived from the `for` the caller supplied.
   * Three fields on this screen supply NO `for` - the read-only definition name, the permissions region
   * and the visibility group all caption something other than one control - and for those the component
   * falls back to a per-instance counter, so their identifiers are not predictable from a field name at
   * all. Walking up from the control works for both kinds and does not depend on field order.
   *
   * @param controlId The control's declared identifier.
   * @returns The enclosing field region.
   */
  function regionOf(controlId: string): HTMLElement | null {
    return q(`#${controlId}`)?.closest<HTMLElement>('.form-field') ?? null;
  }

  /**
   * The shared field region whose caption begins with the given wording.
   *
   * For the three fields that name no single control and therefore have no `for` to walk up from. The
   * caption is the field's visible name, which is the one stable, meaningful handle those fields have.
   *
   * @param caption The caption's leading wording.
   * @returns The enclosing field region.
   */
  function regionCaptioned(caption: string): HTMLElement | null {
    return (
      qa<HTMLElement>('.form-field').find((region: HTMLElement): boolean =>
        (region.querySelector('.form-field__label')?.textContent ?? '').trim().startsWith(caption),
      ) ?? null
    );
  }

  /**
   * A field's rendered help text, revealing it first when it is still collapsed.
   *
   * ⚠ THE HELP IS A DISCLOSURE NOW, AND THAT IS A RESTORATION RATHER THAN A REGRESSION. This screen used
   * to render its hints as always-visible text, which the rest of the application does not: the shared
   * component keeps help behind a keyboard-reachable toggle and removes the panel from the DOM while it is
   * closed, which is what `labelcontrol.ascx` did - a help link revealing a bordered panel. So the text is
   * present only after the affordance is pressed, and this helper presses it.
   *
   * @param region The field region to read.
   * @returns The trimmed text, or `null` when the field declares no help at all.
   */
  function hintIn(region: HTMLElement | null): string | null {
    if (region === null) {
      return null;
    }

    const toggle = region.querySelector<HTMLButtonElement>('.form-field__help-toggle');

    if (toggle === null) {
      return null;
    }

    if (toggle.getAttribute('aria-expanded') !== 'true') {
      toggle.click();
      fixture.detectChanges();
    }

    const help = region.querySelector('.form-field__help');

    return help === null ? null : (help.textContent ?? '').trim();
  }

  /**
   * A field's rendered help text, addressed by the control it describes.
   *
   * @param fieldName The field name, which is also its control's identifier stem.
   * @returns The trimmed text, or `null` when the hint is not rendered.
   */
  function hintFor(fieldName: string): string | null {
    return hintIn(regionOf(`module-settings-${fieldName}`));
  }

  /**
   * Every validation message rendered inside one field region, in render order.
   *
   * @param region The field region to read.
   * @returns The messages, or an empty list when the region reports nothing.
   */
  function messagesIn(region: HTMLElement | null): readonly string[] {
    return region === null
      ? []
      : Array.from(region.querySelectorAll('.form-field__error')).map((node: Element): string =>
          (node.textContent ?? '').trim(),
        );
  }

  /**
   * A field's rendered validation messages, addressed by the control they belong to.
   *
   * @param fieldName The field name, which is also its control's identifier stem.
   * @returns The messages, in render order.
   */
  function messagesFor(fieldName: string): readonly string[] {
    return messagesIn(regionOf(`module-settings-${fieldName}`));
  }

  /**
   * A field's rendered validation message as one string, for the cases that expect exactly one.
   *
   * @param fieldName The field name.
   * @returns The trimmed text, or `null` when no message is rendered.
   */
  function messageFor(fieldName: string): string | null {
    const messages = messagesFor(fieldName);

    return messages.length === 0 ? null : messages.join(' ');
  }

  /**
   * The radios of a choice group.
   *
   * @param fieldName The group's field name.
   * @returns The radio inputs.
   */
  function radios(fieldName: string): HTMLInputElement[] {
    return qa<HTMLInputElement>(`input[type="radio"][id^="module-settings-${fieldName}-"]`);
  }

  /**
   * Types into a text or number control.
   *
   * @param fieldName The field name.
   * @param value The value to type.
   */
  function type(fieldName: string, value: string): void {
    const element = field<HTMLInputElement>(fieldName);
    expect(element).withContext(`${fieldName} must be rendered`).not.toBeNull();

    // Narrowed by a real control-flow check rather than by a non-null assertion. An assignment target
    // cannot be optionally chained, so this is the one place a guard is needed instead, and returning early
    // is honest: the expectation above has already failed the spec, so there is nothing left to type into.
    if (element === null) {
      return;
    }

    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * The action-bar button carrying a label.
   *
   * @param label The button's text.
   * @returns The button, or `null`.
   */
  function actionButton(label: string): HTMLButtonElement | null {
    return (
      qa<HTMLButtonElement>('.module-settings__actions button').find(
        (button) => (button.textContent ?? '').trim() === label,
      ) ?? null
    );
  }

  /** Submits the form. */
  function submit(): void {
    const form = q<HTMLFormElement>('form.module-settings');
    expect(form).withContext('the form must be rendered').not.toBeNull();
    // Optionally chained rather than non-null asserted: the expectation above is what fails the spec if
    // the element is missing, so the call itself needs no assertion operator.
    form?.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  /**
   * Confirms the open destructive dialog.
   *
   * The shared dialog prefixes a warning glyph to a dangerous confirm label, so the button is located by
   * its danger class rather than by its text.
   */
  function confirmDialog(): void {
    const confirmButton = q<HTMLButtonElement>('.confirm-dialog__button--danger');
    expect(confirmButton).withContext('the confirmation must be open').not.toBeNull();
    // Optionally chained rather than non-null asserted: the expectation above is what fails the spec if
    // the element is missing, so the call itself needs no assertion operator.
    confirmButton?.click();
    fixture.detectChanges();
  }

  /**
   * Abandons the open destructive dialog.
   */
  function cancelDialog(): void {
    const cancelButton = qa<HTMLButtonElement>('.confirm-dialog__button').find(
      (button) => (button.textContent ?? '').trim() === 'Cancel',
    );
    expect(cancelButton).withContext('the confirmation must offer a way out').toBeTruthy();
    // Optionally chained rather than non-null asserted: the expectation above is what fails the spec if
    // the element is missing, so the call itself needs no assertion operator.
    cancelButton?.click();
    fixture.detectChanges();
  }

  /**
   * Consumes the listing re-read the store issues after a settled write.
   *
   * MIGRATION: THIS RE-READ BELONGS TO THE STORE, NOT TO THIS SCREEN, AND IT IS CONSUMED RATHER THAN
   * ASSERTED AS A BEHAVIOUR. `core/state/module.store.ts` re-reads the module listing once a removal
   * settles, because a removal DETACHES a placement rather than destroying a row -
   * `DeleteTabModule(TabId, ModuleId)` at `ModuleController.vb:L837`, never `DeleteModule` at L819 - so
   * only the listing knows whether the row should still be shown. It is answered here so the closing
   * `verify()` has nothing outstanding to report, while the separate `expectNone` on the module's OWN url
   * is what proves that the removed module itself is never re-fetched.
   */
  function drainListingReread(): void {
    for (const request of httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
    )) {
      request.flush({ data: [], meta: { totalCount: 0, pageIndex: 0, pageSize: 10 } });
    }

    fixture.detectChanges();
  }


  beforeEach(async () => {
    // The screen orchestrates its own reads and writes through `core/state/module.store.ts`, which reaches
    // `ModuleService` and `TabService` and therefore `HttpClient`. NEITHER SERVICE IS MOCKED, deliberately:
    // a stub would only prove that it returned what it was told to, whereas the real transport put behind
    // the testing backend proves the exact url, the exact verb and the exact request body, and lets
    // `verify()` prove that no endpoint outside the published contract was invoked. `provideHttpClient()`
    // is registered BEFORE `provideHttpClientTesting()` because the testing backend replaces the real one.
    // `provideRouter([])` satisfies the return-to-listing navigation the cancel and delete paths perform,
    // and installs no guards - guard behaviour belongs to the guards' own specs, not here.
    await TestBed.configureTestingModule({
      imports: [ModuleSettingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(ModuleSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('pages', [
      { value: 7, label: 'Home' },
      { value: 8, label: 'About' },
    ]);
    fixture.detectChanges();
  });

  // THE CLOSED-CONTRACT GUARD, AND THE REASON EVERY "NO REQUEST WAS ISSUED" CLAIM IN THIS FILE IS A PROOF
  // RATHER THAN A HOPE. `verify()` fails the spec when ANY request is still outstanding, so a screen that
  // invented an endpoint - a dedicated relocation command above all, which does not exist in the module API
  // and must never be expected - cannot pass by having its request quietly ignored. It is equally what
  // makes the `expectNone` assertions load-bearing: without this, "no re-fetch happened" would only mean
  // "no re-fetch was looked for".
  afterEach(() => {
    httpMock.verify();
  });

  it('creates', () => {
    expect(component).toBeTruthy();
  });

  describe('router input binding', () => {
    /**
     * REGRESSION GUARD FOR A REAL, MEASURED PRODUCTION FAILURE. Do not weaken or delete this.
     *
     * `withComponentInputBinding()` does not bind only the inputs a route names. On every activation it
     * reflects the component's inputs and calls `setInput(templateName, data[templateName])` for EVERY one,
     * where `data` is the merged query parameters, path parameters and route data. An input whose name is not
     * a key of that object receives `undefined` - it is not skipped and it does not keep its initialiser.
     *
     * This screen's route is `modules/:moduleId/settings`, so `moduleId` is the only name the router can
     * supply and all seven presentation inputs arrive as `undefined`. When the setters did not accept it the
     * screen threw out of `activateRoutes`, then threw again on every change-detection pass, and rendered an
     * empty form skeleton with no visible error at all. `TestBed` never runs the router's input binder, so
     * nothing else in this file can catch it - this test reproduces the binder's behaviour directly.
     */
    it('survives the router assigning undefined to every input it cannot supply', () => {
      const routerAssigned: readonly string[] = [
        'heading',
        'settings',
        'loading',
        'saving',
        'canDelete',
        'pages',
        'canManageAllPages',
        'tabModuleId',
      ];

      expect(() => {
        for (const name of routerAssigned) {
          fixture.componentRef.setInput(name, undefined);
        }
        fixture.componentRef.setInput('moduleId', '0');
        fixture.detectChanges();
      }).not.toThrow();

      // The heading must stay non-blank: the shared page header refuses a blank title outright.
      expect(component.heading).toBe('Module Settings');

      // The absent case must be exactly one value, because the template dereferences it.
      expect(component.settings).toBeNull();

      // `undefined` must not be mistaken for a caller pinning these flags: each must fall through to the
      // store. `loading` is therefore TRUE here - the reads the route just triggered are still in flight,
      // which is exactly the value the store reports and the value the screen should show.
      expect(component.loading)
        .withContext('an unpinned loading flag must follow the store, which is fetching')
        .toBeTrue();
      expect(component.saving).toBeFalse();
      expect(component.canDelete).toBeFalse();
      expect(component.pages).toEqual([]);

      // `'0'` is a REAL module identifier, and the string form the router supplies must coerce to it.
      expect(component.moduleId).toBe(0);
      expect(component.tabModuleId).toBeUndefined();

      // And the page picker must NOT be locked shut, which is what treating `undefined` as an assignment did.
      expect(component['form'].controls.tabId.disabled)
        .withContext('an undefined privilege input must not lock the page picker on every routed visit')
        .toBeFalse();

      // The two reads the routed activation triggered are consumed here rather than left parked, so the
      // closing `verify()` has nothing outstanding to report. Answering them with a null payload is the
      // honest response for this test: it asserts the binder survives, not what the module contains.
      httpMock.expectOne('/api/v1/modules/0').flush({ data: null });
      httpMock.expectOne('/api/v1/modules/0/settings').flush({ data: null });
      fixture.detectChanges();
    });
  });

  describe('shell states', () => {
    it('renders the shared page header outside the loading branch, so the screen is never untitled', () => {
      setInput('loading', true);

      const header = q('app-page-header h1');
      expect(header).not.toBeNull();
      expect((header?.textContent ?? '').trim()).toBe('Module Settings');
    });

    it('takes a supplied heading in place of the legacy module definition name', () => {
      setInput('heading', 'Announcements Settings');

      expect((q('app-page-header h1')?.textContent ?? '').trim()).toBe('Announcements Settings');
    });

    it('shows a spinner and no form while the module is being fetched', () => {
      setInput('loading', true);

      expect(q('app-loading-spinner')).not.toBeNull();
      expect(q('form.module-settings')).toBeNull();
    });

    it('shows an empty state rather than an empty form when no module resolved', () => {
      setInput('settings', null);

      expect(q('app-empty-state')).not.toBeNull();
      expect(q('form.module-settings')).toBeNull();
      expect((q('app-empty-state')?.textContent ?? '').trim()).toContain('No module settings are available.');
    });

    it('renders the form once a module resolves', () => {
      setInput('settings', moduleOf());

      expect(q('form.module-settings')).not.toBeNull();
      expect(q('app-empty-state')).toBeNull();
    });
  });

  describe('disclosure structure', () => {
    beforeEach(() => {
      setInput('settings', moduleOf());
    });

    it('presents three top-level regions, each ruled, reproducing includerule="True"', () => {
      const ruled = qa('.module-settings__toggle--ruled');

      expect(ruled.length).toBe(3);
      expect(ruled.map((head) => head.id)).toEqual([
        'module-settings-section-moduleSettings',
        'module-settings-section-pageSettings',
        'module-settings-section-specificSettings',
      ]);
    });

    it('presents seven regions in total once every one is open', () => {
      openEverything();

      // Seven is the total the accompanying stylesheet documents: three top level plus four nested.
      expect(toggles().length).toBe(7);
    });

    it('starts with exactly four regions closed, matching the legacy isexpanded attributes', () => {
      // The authoritative claim is about the component's collapsed set, not about what happens to be on
      // screen: dshSecurity, dshPage, dshOther and dshSpecific all declare isexpanded="False", while
      // dshModule, dshDetails and dshAppearance open expanded.
      const collapsed = component['collapsed'] as Set<string>;

      expect(Array.from(collapsed).sort()).toEqual(['other', 'pageSettings', 'security', 'specificSettings']);
      expect(collapsed.size).toBe(4);
    });

    it('does not render a closed region\'s nested heads, so only five of the seven are reachable at first', () => {
      // pageSettings starts closed, so neither of its children is in the document yet. That is the whole
      // point of removing a closed body rather than hiding it, and it is why openEverything must open a
      // parent before its child.
      const visible = toggles().map((head) => head.id);

      expect(visible).toEqual([
        'module-settings-section-moduleSettings',
        'module-settings-section-details',
        'module-settings-section-security',
        'module-settings-section-pageSettings',
        'module-settings-section-specificSettings',
      ]);
      expect(toggleFor('appearance')).toBeNull();
      expect(toggleFor('other')).toBeNull();

      const closedOnScreen = toggles()
        .filter((head) => head.getAttribute('aria-expanded') === 'false')
        .map((head) => head.id);

      expect(closedOnScreen).toEqual([
        'module-settings-section-security',
        'module-settings-section-pageSettings',
        'module-settings-section-specificSettings',
      ]);
    });

    it('nests the four child regions and marks them as nested', () => {
      openEverything();

      const nested = qa('.module-settings__section--nested');
      expect(nested.length).toBe(4);

      const nestedHeadIds = nested
        .map((section) => section.querySelector<HTMLButtonElement>('.module-settings__toggle'))
        .map((head) => head?.id ?? '');

      expect(nestedHeadIds).toEqual([
        'module-settings-section-details',
        'module-settings-section-security',
        'module-settings-section-appearance',
        'module-settings-section-other',
      ]);
    });

    it('removes a closed region\'s CONTENTS rather than hiding them', () => {
      // .module-settings__body declares display:grid, and the user-agent [hidden] rule loses to any author
      // rule, so a hidden BODY would still be visible. Removing the contents is the only correct collapse.
      // What remains behind is the empty, class-less placeholder the neighbouring spec pins down, which
      // exists purely so the toggle's aria-controls resolves in both states.
      expect(q('#module-settings-header')).toBeNull();

      open('security');

      expect(q('#module-settings-header')).not.toBeNull();
      expect(qa('.module-settings__body[hidden]').length)
        .withContext('no real region body is ever merely hidden')
        .toBe(0);
    });

    it('reports its state through aria-expanded and names its region in BOTH states', () => {
      const head = toggleFor('pageSettings')!;
      expect(head.getAttribute('aria-expanded')).toBe('false');

      // ⚠ CLOSED: THE REFERENCE IS PUBLISHED AND IT RESOLVES, WHICH IS THE OPPOSITE OF WHAT THIS SPEC
      // USED TO ASSERT. Two earlier revisions each got half of this right. The first declared
      // aria-controls never at all; the second declared it only while the region was open, reasoning
      // that a closed region is REMOVED from the document so a constant attribute would leave a
      // dangling IDREF. The reasoning was sound and the conclusion still wrong: a toggle whose
      // aria-controls disappears is a control that names nothing in the state where a reader most needs
      // to know what it governs, and assistive technology has no way to describe the target it is about
      // to reveal. The template now keeps an EMPTY, `hidden` placeholder carrying the body's id while
      // the region is closed, so the identity survives the state change and the reference resolves in
      // both states - while the region's CONTENTS are still removed rather than hidden, which is what
      // the neighbouring specs protect.
      const closedGoverned: string | null = head.getAttribute('aria-controls');
      expect(closedGoverned)
        .withContext('a collapsed toggle still names the region it governs')
        .toBe('module-settings-body-pageSettings');
      expect(q(`#${closedGoverned}`))
        .withContext('and that name resolves: no dangling IDREF is ever published')
        .not.toBeNull();

      head.click();
      fixture.detectChanges();

      const opened = toggleFor('pageSettings')!;
      expect(opened.getAttribute('aria-expanded')).toBe('true');

      // OPEN: the same reference, resolving to the same id - now carrying the real region.
      const governed: string | null = opened.getAttribute('aria-controls');
      expect(governed).toBe('module-settings-body-pageSettings');
      expect(q(`#${governed}`)).not.toBeNull();
      expect(governed)
        .withContext('the id is STABLE across the state change, which is what makes the reference valid')
        .toBe(closedGoverned);

      // And the toggle does not point at itself - a control naming its own id tells AT nothing.
      expect(governed).not.toBe(opened.getAttribute('id'));

      opened.click();
      fixture.detectChanges();

      // CLOSED AGAIN: still named, still resolving, and the id has not drifted.
      const reclosed = toggleFor('pageSettings')!;
      expect(reclosed.getAttribute('aria-controls')).toBe('module-settings-body-pageSettings');
      expect(q('#module-settings-body-pageSettings')).not.toBeNull();
    });

    /**
     * The placeholder must stay INERT, because an always-present element is only safe if it cannot be
     * seen, cannot be reached and cannot be mistaken for the region itself. Four separate expectations,
     * each of which would pass while one of the others failed.
     */
    it('keeps the collapsed region\'s placeholder empty, hidden and unstyled', () => {
      const placeholder = q('#module-settings-body-pageSettings');

      expect(placeholder).not.toBeNull();
      expect(placeholder?.hasAttribute('hidden'))
        .withContext('hidden, so no user agent renders it')
        .toBeTrue();
      expect((placeholder?.innerHTML ?? 'x').length)
        .withContext('empty, so it holds no control and can trap no focus')
        .toBe(0);
      expect(placeholder?.classList.contains('module-settings__body'))
        .withContext(
          'it must NOT carry the body class: that class declares display:grid, which beats the ' +
            'user-agent [hidden] rule and would paint an empty region',
        )
        .toBeFalse();
      expect(placeholder?.querySelectorAll('input, select, textarea, button').length)
        .withContext('no focusable descendant, so the tab order is untouched')
        .toBe(0);
    });

    it('closes an open region again', () => {
      open('pageSettings');
      expect(toggleFor('appearance')).not.toBeNull();

      toggleFor('pageSettings')?.click();
      fixture.detectChanges();

      expect(toggleFor('appearance')).toBeNull();
    });

    it('renders one intro paragraph per top-level region and none for a nested one', () => {
      openEverything();

      const intros = qa('.module-settings__intro').map((p) => (p.textContent ?? '').trim());
      expect(intros.length).toBe(3);
      expect(intros[0]).toContain('relate to the Module content and permissions');
      expect(intros[1]).toContain('specific to this particular occurrence of the Module');
      expect(intros[2]).toContain('specific for this module');
    });

    it('names every region body by its own head', () => {
      openEverything();

      for (const body of qa('.module-settings__body')) {
        const labelledBy = body.getAttribute('aria-labelledby');
        expect(labelledBy).withContext('every body must be named').not.toBeNull();
        expect(q(`#${labelledBy}`)).withContext(`${labelledBy} must exist`).not.toBeNull();
      }
    });

    it('projects module-specific settings into the slot', () => {
      // The slot is the successor to the legacy per-module settings control panel.
      open('specificSettings');

      expect(q('.module-settings__slot')).not.toBeNull();
    });
  });

  describe('seeding', () => {
    beforeEach(() => {
      setInput('canManageAllPages', true);
      setInput('settings', moduleOf());
      openEverything();
    });

    it('shows the definition name read-only, because which definition a module instantiates is fixed', () => {
      expect(q('#module-settings-friendlyName')).withContext('the name is not a form control').toBeNull();
      expect(host().textContent).toContain('Announcements');
    });

    it('seeds every text and numeric field from the supplied module', () => {
      expect(field<HTMLInputElement>('moduleTitle')?.value).toBe('Latest News');
      expect(field<HTMLTextAreaElement>('header')?.value).toBe('Header markup');
      expect(field<HTMLTextAreaElement>('footer')?.value).toBe('Footer markup');
      expect(field<HTMLInputElement>('iconFile')?.value).toBe('module.gif');
      expect(field<HTMLInputElement>('cacheTime')?.value).toBe('120');
    });

    it('seeds visibility from the stored numeric code', () => {
      const chosen = radios('visibility').find((radio) => radio.checked);
      expect(chosen).toBeTruthy();
      expect(chosen?.id).toBe('module-settings-visibility-1', 'Minimized is the second choice');
    });

    it('seeds the two boolean placement switches', () => {
      expect(field<HTMLInputElement>('displayTitle')?.checked).toBeFalse();
      expect(field<HTMLInputElement>('inheritViewPermissions')?.checked).toBeFalse();
      expect(field<HTMLInputElement>('allTabs')?.checked).toBeTrue();
    });

    it('narrows an instant to a date by slicing, so the shown day cannot shift with the viewer zone', () => {
      // Parsing the instant and formatting it locally would move a midnight-UTC date by a day in any zone
      // behind UTC, so a module scheduled for the first would be shown as the last day of the month before.
      expect(field<HTMLInputElement>('startDate')?.value).toBe('2024-03-01');
      expect(field<HTMLInputElement>('endDate')?.value).toBe('2024-12-31');
    });

    it('leaves the two intent flags unset rather than echoing a previous instruction', () => {
      // Neither is a column on the module: each describes work the server performs after the update, so
      // echoing one back onto the form would silently reapply it on the next submission.
      expect(field<HTMLInputElement>('setAsDefaultSettings')?.checked).toBeFalse();
      expect(field<HTMLInputElement>('applyToAllModules')?.checked).toBeFalse();
    });

    it('shows absent stored values as empty controls and a missing cache period as zero', () => {
      setInput('settings', moduleOf({ moduleTitle: null, iconFile: null, cacheTime: null }));
      openEverything();

      expect(field<HTMLInputElement>('moduleTitle')?.value).toBe('');
      expect(field<HTMLInputElement>('iconFile')?.value).toBe('');
      expect(field<HTMLInputElement>('cacheTime')?.value).toBe('0');
    });

    it('re-seeds when a different module is supplied', () => {
      setInput('settings', moduleOf({ moduleTitle: 'Replaced', iconFile: 'replacement.gif' }));
      openEverything();

      expect(field<HTMLInputElement>('moduleTitle')?.value).toBe('Replaced');
      expect(field<HTMLInputElement>('iconFile')?.value).toBe('replacement.gif');
    });

    it('leaves the operator\'s open regions open across a re-seed', () => {
      // Re-seeding replaces the values, not the disclosure state. Snapping every region shut whenever the
      // data refreshed would throw the operator back to the top of a seven-region form mid-edit.
      expect(toggleFor('other')).not.toBeNull();

      setInput('settings', moduleOf({ moduleTitle: 'Refreshed' }));

      expect(toggleFor('other')).withContext('a nested open region must stay open').not.toBeNull();
      expect(toggleFor('pageSettings')?.getAttribute('aria-expanded')).toBe('true');
      expect(field<HTMLInputElement>('moduleTitle')?.value).toBe('Refreshed');
    });
  });

  describe('migrated wording', () => {
    beforeEach(() => {
      setInput('canManageAllPages', true);
      setInput('settings', moduleOf());
      openEverything();
    });

    it('takes the label from the resource file where it disagrees with the markup', () => {
      // modulesettings.ascx declares text="Display Title?" but ModuleSettings.ascx.resx declares
      // plDisplayTitle.Text = 'Display Container?'. The resource file is what the legacy screen rendered, so
      // taking the markup value would have relabelled a control operators already know.
      const label = q<HTMLLabelElement>('label[for="module-settings-displayTitle"]');
      expect(label).not.toBeNull();
      expect((label?.textContent ?? '')).toContain('Display Container?');
      expect((label?.textContent ?? '')).not.toContain('Display Title?');
    });

    it('preserves the legacy double space after a sentence, character for character', () => {
      // Angular compiles templates with preserveWhitespaces disabled, so a run of whitespace in a TEXT NODE
      // is collapsed to one space. Every migrated string is therefore bound from a constant, and this is the
      // test that catches a regression back to template text.
      expect(hintFor('startDate')).toBe(
        'Enter the start date for displaying this module.  You may use the Calendar to pick a date.',
      );
      expect(hintFor('endDate')).toBe(
        'Enter the end date for displaying this module.  You may use the Calendar to pick a date.',
      );
      expect(hintFor('moduleTitle')).toBe(
        'Enter a title for the Module.  This will appear in the Title Bar of the Container for this Module, '
          + 'if supported by the Container.',
      );
      // Addressed by its CAPTION, because the permissions field names a region rather than one control and
      // therefore declares no `for` for the identifier scheme to derive from.
      expect(hintIn(regionCaptioned('Permissions'))).toBe(
        'Select the View and Edit permissions by checking/unchecking the boxes in the grid.  The module can '
          + 'inherit its permissions from the Page.  To do this check the Inherit View Permissions checkbox.',
      );
    });

    it('preserves the space the legacy resource carries before its closing parenthesis', () => {
      expect(qa('.module-settings__intro')[0].textContent?.trim()).toBe(
        'In this section, you can define the settings that relate to the Module content and permissions '
          + '(ie. those settings that will be the same on all pages that the Module appears ).',
      );
    });

    it('renders the inherit label as plain text, losing the legacy embedded bold', () => {
      const label = q<HTMLLabelElement>('label[for="module-settings-inheritViewPermissions"]');
      expect((label?.textContent ?? '').trim()).toBe('Inherit View permissions from Page');
      expect(label?.querySelector('b')).withContext('an interpolated value is not parsed as markup').toBeNull();
    });

    it('renders every declared hint exactly as declared, across every region', () => {
      // The sweep. Every hint is compared against its DECLARATION rather than against a copy retyped into
      // this test, so a reworded resource cannot pass by matching a stale expectation here.
      //
      // ⚠ EVERY HINT IS REVEALED FIRST, because the shared field keeps help behind a disclosure and removes
      // the panel from the DOM while it is closed. A sweep over rendered help elements would therefore find
      // NONE before the toggles are pressed, which is why this walks the toggles rather than the panels.
      const declared = component['hints'] as Record<string, string>;
      const rendered: string[] = [];

      for (const toggle of qa<HTMLButtonElement>('.form-field__help-toggle')) {
        toggle.click();
      }

      fixture.detectChanges();

      for (const element of qa('.form-field__help')) {
        rendered.push((element.textContent ?? '').trim());
      }

      // Fifteen of the sixteen declared hints are rendered: the permissions hint serves both the region and
      // its inherit switch, so inheritViewPermissions has no field of its own to carry one.
      expect(rendered.length).toBe(15);
      expect(Object.keys(declared).length).toBe(16);

      // Each rendered hint must be one of the declared ones, and the fifteen must be DISTINCT - so a
      // template that bound the same hint to two fields could not pass by rendering the right count.
      const declaredValues = new Set<string>(Object.values(declared));

      for (const text of rendered) {
        expect(declaredValues.has(text)).withContext(`"${text}" must be a declared hint`).toBeTrue();
      }

      expect(new Set<string>(rendered).size).withContext('no hint is rendered twice').toBe(15);

      // ⚠ THE SIXTEENTH DECLARATION IS A DUPLICATE OF THE FIFTEENTH, WHICH IS WHY FIFTEEN ELEMENTS COVER
      // SIXTEEN KEYS. `permissions` and `inheritViewPermissions` declare the SAME sentence - the legacy
      // resource attached one help string to the region and to the switch inside it - and one field now
      // carries them both, so exactly one element renders that sentence. Asserting the two declarations are
      // identical is what makes the count of fifteen correct rather than merely convenient: if they ever
      // diverged, one of the two would be going unrendered and this would fail.
      expect(declared['inheritViewPermissions'])
        .withContext('the switch and its region declare one shared sentence')
        .toBe(declared['permissions']);
      expect(rendered.filter((text: string): boolean => text === declared['permissions']).length)
        .withContext('that shared sentence is rendered once, by the field that captions the region')
        .toBe(1);
    });
  });

  describe('validation', () => {
    beforeEach(() => {
      setInput('settings', moduleOf());
      open('pageSettings');
    });

    it('refuses a non-integer cache period with the legacy message', () => {
      // The legacy rule is `Operator="DataTypeCheck" Type="Integer"` (modulesettings.ascx L172), which checks
      // the TYPE and nothing else, so the value tested here is one that is not an integer at all.
      type('cacheTime', 'soon');
      component['form'].controls.cacheTime.markAsTouched();
      fixture.detectChanges();

      expect(component['form'].controls.cacheTime.valid).toBeFalse();
      expect(messageFor('cacheTime')).toBe('Invalid Cache Time');
    });

    it('accepts a negative cache period, because the legacy data-type check accepted one', () => {
      // A deliberate parity assertion rather than an oversight. `Type="Integer"` imposed no lower bound, so
      // the legacy screen accepted -1 and stored it. That is a latent legacy defect, and adding a bound here
      // would be exactly the opportunistic optimisation the migration discipline forbids - it would reject
      // input the legacy screen accepted. The defect is annotated on `integerDataTypeCheck` instead.
      type('cacheTime', '-1');
      component['form'].controls.cacheTime.markAsTouched();
      fixture.detectChanges();

      expect(component['form'].controls.cacheTime.valid).toBeTrue();
      expect(messageFor('cacheTime')).toBeNull();
    });

    it('refuses a start date that is not a date, with the legacy message', () => {
      // valtxtStartDate (modulesettings.ascx L78), Operator="DataTypeCheck" Type="Date". The id/resource-key
      // mismatch in the source - id `valtxtStartDate`, key `valStartDate.` - is reproduced as wording only.
      // The two date fields sit in the region that starts CLOSED (isexpanded="False"), so it is opened first;
      // the surrounding block opens only pageSettings.
      openEverything();
      component['form'].controls.startDate.setValue('2024-02-31');
      component['form'].controls.startDate.markAsTouched();
      fixture.detectChanges();

      expect(component['form'].controls.startDate.valid).toBeFalse();
      expect(messageFor('startDate')).toBe('Invalid Start Date');
    });

    it('refuses an end date that is not a date, with the legacy message', () => {
      // valtxtEndDate (modulesettings.ascx L88), Operator="DataTypeCheck" Type="Date".
      openEverything();
      component['form'].controls.endDate.setValue('not-a-date');
      component['form'].controls.endDate.markAsTouched();
      fixture.detectChanges();

      expect(component['form'].controls.endDate.valid).toBeFalse();
      expect(messageFor('endDate')).toBe('Invalid End Date');
    });

    it('advertises each column bound through maxlength', () => {
      expect(field<HTMLInputElement>('moduleTitle')?.getAttribute('maxlength')).toBe('256');
      expect(field<HTMLInputElement>('iconFile')?.getAttribute('maxlength')).toBe('100');
    });

    it('does not submit an invalid form, and reveals every message at once', () => {
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      // A value that fails the integer data-type check. `-1` would NOT: see the parity assertion above.
      type('cacheTime', 'soon');
      submit();

      expect(emitted.length).toBe(0);
      expect(component['form'].controls.cacheTime.touched)
        .withContext('marking every control touched shows all messages at once rather than one at a time')
        .toBeTrue();
    });
  });

  describe('privilege locks', () => {
    beforeEach(() => {
      setInput('settings', moduleOf());
    });

    it('locks the four far-reaching settings for a caller who is not a portal administrator', () => {
      // ModuleSettings.ascx.vb:L333-L338 disabled these for a tab administrator, because each alters pages
      // the caller does not administer.
      setInput('canManageAllPages', false);
      openEverything();

      expect(field<HTMLSelectElement>('tabId')?.disabled).toBeTrue();
      expect(field<HTMLInputElement>('allTabs')?.disabled).toBeTrue();
      expect(field<HTMLInputElement>('setAsDefaultSettings')?.disabled).toBeTrue();
      expect(field<HTMLInputElement>('applyToAllModules')?.disabled).toBeTrue();
    });

    it('releases them for a portal administrator', () => {
      setInput('canManageAllPages', true);
      openEverything();

      expect(field<HTMLSelectElement>('tabId')?.disabled).toBeFalse();
      expect(field<HTMLInputElement>('allTabs')?.disabled).toBeFalse();
      expect(field<HTMLInputElement>('setAsDefaultSettings')?.disabled).toBeFalse();
      expect(field<HTMLInputElement>('applyToAllModules')?.disabled).toBeFalse();
    });

    it('keeps a locked setting at its stored value in the submission rather than clearing it', () => {
      // The submission is a whole-row replacement, so a locked control that submitted nothing would clear
      // the stored value. getRawValue includes disabled controls, which is why it is used.
      setInput('canManageAllPages', false);
      setInput('settings', moduleOf({ allTabs: true }));

      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      submit();

      expect(emitted.length).toBe(1);
      expect(emitted[0].allTabs).withContext('the stored value must survive a submission it could not edit').toBeTrue();
    });

    it('re-applies the locks after a re-seed, because a reset re-enables every control', () => {
      setInput('canManageAllPages', false);
      setInput('settings', moduleOf({ moduleTitle: 'Another' }));
      openEverything();

      expect(field<HTMLInputElement>('allTabs')?.disabled).toBeTrue();
    });
  });

  describe('submission', () => {
    beforeEach(() => {
      setInput('canManageAllPages', true);
      setInput('settings', moduleOf());
      openEverything();
    });

    it('emits every member of the update contract and nothing else', () => {
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      submit();

      expect(emitted.length).toBe(1);
      expect(emitted[0]).toEqual({
        tabId: 7,
        // Untouched picker, so nothing is being relocated. The destination is explicitly null rather than a
        // copy of the page above: a request that named its own page as a destination would ask the server to
        // perform a move to where the module already is.
        moveToTabId: null,
        moduleTitle: 'Latest News',
        allTabs: true,
        header: 'Header markup',
        footer: 'Footer markup',
        startDate: '2024-03-01',
        endDate: '2024-12-31',
        inheritViewPermissions: false,
        isDeleted: false,
        moduleOrder: 6,
        cacheTime: 120,
        iconFile: 'module.gif',
        visibility: MODULE_VISIBILITY.minimized,
        displayTitle: false,
        setAsDefaultSettings: false,
        applyToAllModules: false,
      });
    });

    // THE MOVE-TO-PAGE CONTRACT. These two specs exist because the screen previously sent the picker's value
    // as `tabId`, the member the server uses to SELECT the placement being edited. Choosing any page other
    // than the module's own therefore asked the server to update a placement on a page the module does not
    // occupy, which it refused with `module.placement_not_found` - so the control was labelled with an action
    // that could not succeed, and the module never moved. The page being edited and the page being moved to
    // are two different answers and must travel as two different members.
    it('sends the chosen page as a relocation and keeps the edited page as the selector', () => {
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      // 8 is 'About'; the module under test sits on 7, 'Home'.
      component['form'].controls.tabId.setValue(8);
      fixture.detectChanges();

      submit();

      expect(emitted.length).toBe(1);
      expect(emitted[0].tabId)
        .withContext('the placement being edited is still the page the module was loaded from')
        .toBe(7);
      expect(emitted[0].moveToTabId)
        .withContext('the picker names where the module is going')
        .toBe(8);
    });

    it('sends no relocation when the chosen page is the page it is already on', () => {
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      // Set explicitly to the value it already holds, so this proves the comparison rather than the default.
      component['form'].controls.tabId.setValue(7);
      fixture.detectChanges();

      submit();

      expect(emitted.length).toBe(1);
      expect(emitted[0].tabId).toBe(7);
      expect(emitted[0].moveToTabId)
        .withContext('naming the current page is not a move, so no instruction is sent')
        .toBeNull();
    });

    it('carries no member the server does not accept', () => {
      // The six placement values the legacy screen edited - paneName, alignment, color, border, displayPrint and
      // displaySyndicate - are not projected onto Dtos/Module/UpdateModuleRequest.cs, so sending them
      // would have no effect and would misrepresent what was saved. The adapter drops them; this is the
      // guard that keeps them dropped.
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      submit();

      expect(Object.keys(emitted[0]).sort()).toEqual([...UPDATE_CONTRACT_MEMBERS]);
    });

    it('names the page and the recycle-bin flag from the seeded state', () => {
      // Both are required by a whole-row replacement and neither has a form control. tabId in particular
      // cannot be defaulted: dbo.Tabs.TabID is IDENTITY(0, 1), so 0 is a real page and an omitted member
      // would silently address it.
      const emitted: UpdateModuleRequest[] = [];
      setInput('settings', moduleOf({ tabId: 0, isDeleted: true }));
      openEverything();
      component.save.subscribe((request) => emitted.push(request));

      submit();

      expect(emitted[0].tabId).withContext('page zero is a legitimate page').toBe(0);
      expect(emitted[0].isDeleted).toBeTrue();
    });

    it('sends an emptied nullable field as absent, so the column is cleared', () => {
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      type('moduleTitle', '   ');
      type('header', '');
      type('iconFile', ' ');
      submit();

      expect(emitted[0].moduleTitle).withContext('a field holding only whitespace is an absent value').toBeNull();
      expect(emitted[0].header).toBeNull();
      expect(emitted[0].iconFile).toBeNull();
    });

    it('keeps presentation-only placement state out of the wire request', () => {
      // MIGRATION: THIS FACT WAS RE-ORACLED, NOT WEAKENED. It was written when the screen still rendered an
      //   alignment choice group, and it selected the group's fourth option to prove that operating a
      //   presentation-only control could not smuggle its value onto the wire. The screen no longer renders
      //   any of the six values - `paneName`, `alignment`, `color`, `border`, `displayPrint` and
      //   `displaySyndicate` are absent from ModuleSettingsSeed and from the template, because none of
      //   them is projected onto Dtos/Module/UpdateModuleRequest.cs - so the interaction it performed is
      //   unreachable and `radios('alignment')` is empty. The obligation it asserted is stronger under the
      //   surviving shape and is asserted here in both halves: no control exists to operate, so no reachable
      //   interaction can introduce the member, and a submission carries none of the six.
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      const placementOnly = ['paneName', 'alignment', 'color', 'border', 'displayPrint', 'displaySyndicate'];
      for (const name of placementOnly) {
        expect(field(name)).withContext(`${name} must not be operable`).toBeNull();
        expect(radios(name).length).withContext(`${name} must not be a choice group either`).toBe(0);
      }

      submit();

      expect('paneName' in emitted[0]).toBeFalse();
      expect('alignment' in emitted[0]).toBeFalse();
      expect('color' in emitted[0]).toBeFalse();
      expect('border' in emitted[0]).toBeFalse();
      expect('displayPrint' in emitted[0]).toBeFalse();
      expect('displaySyndicate' in emitted[0]).toBeFalse();
    });

    it('carries a changed visibility code through unchanged, not as its label', () => {
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      radios('visibility')[2].click();
      fixture.detectChanges();
      submit();

      expect(emitted[0].visibility).toBe(MODULE_VISIBILITY.none);
    });

    it('sends the default-settings instruction under the server contract spelling', () => {
      // MIGRATION: THE CONTROL WAS RENAMED WITH THE CONTRACT, SO THIS FACT ADDRESSES IT BY ITS SURVIVING
      //   NAME. The legacy screen spelled this check box `isDefaultModule`
      //   (`Website/admin/Modules/modulesettings.ascx`), and an earlier revision of this screen kept that
      //   spelling on the form while the server read `setAsDefaultSettings`. Both the control and the wire
      //   member are now `setAsDefaultSettings`, and the superseded spelling is gone from the form, from
      //   UpdateModuleRequest and from UPDATE_CONTRACT_MEMBERS. The obligation is unchanged: operating the
      //   check box must reach the server under the member the server actually reads.
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      expect(field('isDefaultModule')).withContext('the superseded spelling must not be rendered').toBeNull();

      field<HTMLInputElement>('setAsDefaultSettings')?.click();
      fixture.detectChanges();
      submit();

      expect(emitted[0].setAsDefaultSettings).toBeTrue();
    });

    it('carries an operator instruction under the member the server reads', () => {
      // The control is still named for the legacy check box; the wire member is applyToAllModules. The
      // superseded isDefaultModule and allModules spellings are gone from the contract entirely.
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      field<HTMLInputElement>('applyToAllModules')?.click();
      fixture.detectChanges();
      submit();

      expect(emitted[0].applyToAllModules).toBeTrue();
    });

    it('reports an in-flight submission as disabled, busy AND relabelled, not merely disabled', () => {
      // At rest the control carries the action verb, is enabled, and declares no busy state.
      expect(actionButton('Update')?.disabled).toBeFalse();
      expect(actionButton('Update')?.getAttribute('aria-busy')).toBeNull();

      setInput('saving', true);

      // ⚠ THE CONTROL IS NOW FOUND BY ITS BUSY CAPTION, AND THAT IS THE ASSERTION. This spec
      // previously looked the button up as 'Update' and checked only `disabled`, which passed
      // while the control was indistinguishable from one the form considered incomplete: same
      // caption, same surface, no announced state. Disabled alone is silent to a screen reader
      // and easy to miss with a pointer, and on a slow answer it reads as a press that never
      // landed - which is what invites a second press.
      expect(actionButton('Update')).withContext('the resting caption is withdrawn').toBeNull();

      const busy = actionButton('Saving…');

      expect(busy).withContext('the busy caption is offered instead').not.toBeNull();
      expect(busy?.disabled).toBeTrue();
      expect(busy?.getAttribute('aria-busy')).toBe('true');
    });
  });

  describe('submission conclusion', () => {
    /**
     * MIGRATION: the legacy redirect at `ModuleSettings.ascx.vb:L421` -
     * `Response.Redirect(NavigateURL(), True)`, commented "Navigate back to admin page" - sat INSIDE the
     * `If Page.IsValid Then` / `Try` block AFTER `UpdateModule` had returned, so a postback that threw fell
     * through to `Catch` and never redirected. These specs pin both halves of that - a settled write leaves
     * for the listing and announces, a rejected one stays put so the per-field messages remain reachable - and
     * the third pins that the unchanged settings bag is never written at all.
     *
     * Driven through the ROUTE path rather than through the seed input, because that is the path the
     * conclusion observer serves: a host that pins `settings` owns its own navigation and is handed the `save`
     * output instead.
     */
    const ECHO = {
      moduleId: 0,
      tabModuleId: 31,
      tabId: 0,
      portalId: 0,
      moduleDefId: 14,
      moduleTitle: 'Latest News',
      allTabs: true,
      header: 'Header markup',
      footer: 'Footer markup',
      startDate: null,
      endDate: null,
      inheritViewPermissions: false,
      isDeleted: false,
      moduleOrder: 6,
      cacheTime: 0,
      iconFile: 'module.gif',
      visibility: MODULE_VISIBILITY.none,
      displayTitle: false,
      friendlyName: 'Announcements',
      moduleName: 'DNN_Announcements',
      description: null,
      version: '01.00.00',
      desktopModuleId: 3,
    };

    let http: HttpTestingController;
    let navigate: jasmine.Spy;
    let notify: jasmine.Spy;

    /**
     * Answers every outstanding request for one method and url.
     *
     * @param method The HTTP method to match.
     * @param url The exact url to match.
     * @param data The payload to place in the response envelope.
     * @returns How many requests were answered.
     */
    function answer(method: string, url: string, data: unknown): number {
      const matched = http.match((candidate) => candidate.method === method && candidate.url === url);

      for (const request of matched) {
        request.flush({ data });
      }

      fixture.detectChanges();

      return matched.length;
    }

    /** Answers whatever is still outstanding, which is the store's own follow-up reads and not this screen's. */
    function drain(): void {
      for (const request of http.match(() => true)) {
        const url = request.request.url;
        const body = url.includes('/tabs') || url.includes('module-definitions') ? [] : null;
        request.flush({ data: body });
      }

      fixture.detectChanges();
    }

    beforeEach(() => {
      http = TestBed.inject(HttpTestingController);
      navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      notify = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

      // Unpinned, so the picker follows the tenant's pages exactly as the route path leaves it.
      setInput('pages', null);
      fixture.componentRef.setInput('moduleId', '0');

      // The placement is supplied the way the route supplies it - as a string from a query parameter - so
      // every write below addresses ONE occurrence of the module rather than the module itself. Omitting it
      // is a materially different request, and with removal being soft the difference is consequential:
      // naming a placement removes that placement, while naming none reaches the module.
      //
      // ⚠ ASSIGNED HERE, BEFORE THE SEEDED ANSWERS, AND NOT INSIDE A TEST. Both reads are keyed on the
      // placement, so assigning it re-issues them; assigning it later leaves two unanswered reads, the shell
      // renders its spinner instead of the form, and every affordance the tests reach for is simply absent.
      fixture.componentRef.setInput('tabModuleId', '31');
      fixture.detectChanges();

      answer('GET', '/api/v1/modules/0', ECHO);
      answer('GET', '/api/v1/modules/0/settings', {
        moduleId: 0,
        tabModuleId: 31,
        moduleSettings: {},
        tabModuleSettings: {},
      });
      answer('GET', '/api/v1/module-definitions/14', {
        moduleDefId: 14,
        friendlyName: 'Announcements',
        desktopModuleId: 3,
        defaultCacheTime: 0,
        moduleName: 'DNN_Announcements',
        description: null,
        version: '01.00.00',
        isPremium: false,
        isAdmin: false,
        isPortable: true,
      });
      answer('GET', '/api/v1/portals/0/tabs', []);
    });

    it('announces and returns to the listing once the write has settled', () => {
      submit();

      // Nothing is claimed while the write is still outstanding. The legacy redirect ran after the update
      // had returned, never before it, so an optimistic announcement would not be the same behaviour.
      expect(notify).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();

      expect(answer('PUT', '/api/v1/modules/0', ECHO)).toBe(1);

      expect(notify).toHaveBeenCalledWith('success', jasmine.any(String), null, true);
      expect(navigate).toHaveBeenCalledWith(['/modules'], { replaceUrl: true });

      drain();
    });

    it('leaves the form settled at the instant it navigates, so the unsaved-entry guard cannot question a saved form', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      component['form'].controls.moduleTitle.setValue('Renamed by the operator');
      component['form'].controls.moduleTitle.markAsDirty();
      fixture.detectChanges();

      // THE CONTROL. Without it a later `false` would be indistinguishable from a probe that was never
      // registered, or from a form that was never dirty in the first place. `isDirty()` is the guard's own
      // public surface, so this is asserted through the same call the guard makes rather than through the
      // component's internals.
      expect(tracker.isDirty())
        .withContext('a dirty form with no write in flight is exactly what the guard exists to catch')
        .toBeTrue();

      // ⚠ SAMPLED AT THE INSTANT OF NAVIGATION, NOT AFTERWARDS. A later read cannot tell this fix from a
      // re-seed that happened to clear the flag on its own, and it is the navigation the save itself
      // triggers that the guard would have refused. `window.confirm` blocks the JavaScript thread, so that
      // refusal also made the success notification's auto-dismiss timer become due while the dialog stood -
      // measured in a real browser, the confirmation was emitted and then removed without being painted.
      let dirtyAtNavigation: boolean | null = null;
      navigate.and.callFake(() => {
        dirtyAtNavigation = tracker.isDirty();

        return Promise.resolve(true);
      });

      submit();
      expect(answer('PUT', '/api/v1/modules/0', ECHO)).toBe(1);

      expect(navigate).toHaveBeenCalledWith(['/modules'], { replaceUrl: true });
      expect(dirtyAtNavigation)
        .withContext('the guard must see a settled form on the navigation the save itself triggered')
        .toBeFalse();

      drain();
    });

    it('leaves the form settled at the instant a removal navigates, because deleted entry can no longer be saved', () => {
      const tracker = TestBed.inject(UnsavedChangesTracker);

      component['form'].controls.moduleTitle.setValue('Typed, then deleted');
      component['form'].controls.moduleTitle.markAsDirty();
      fixture.detectChanges();

      expect(tracker.isDirty()).withContext('the control condition for this assertion').toBeTrue();

      let dirtyAtNavigation: boolean | null = null;
      navigate.and.callFake(() => {
        dirtyAtNavigation = tracker.isDirty();

        return Promise.resolve(true);
      });

      // The destructive command, taken through the shared dialog exactly as the screen offers it.
      const deleteButton = actionButton('Delete');
      expect(deleteButton).withContext('the removal affordance must be offered').not.toBeNull();
      deleteButton?.click();
      fixture.detectChanges();
      confirmDialog();

      expect(answer('DELETE', '/api/v1/modules/0', null)).toBe(1);

      expect(navigate).toHaveBeenCalledWith(['/modules'], { replaceUrl: true });
      expect(dirtyAtNavigation)
        .withContext('there is nothing left to save once the placement is gone')
        .toBeFalse();

      drain();
    });

    it('does not write the settings bag, because this screen never changes it', () => {
      submit();

      // The whole point of the guard: this screen renders no control over a property-bag entry, so the bag it
      // would send is the bag it read and the request cannot change anything. Re-sending it would replace both
      // maps from a possibly stale read and silently revert a key another writer had changed in between.
      expect(
        http.match(
          (candidate) => candidate.method === 'PUT' && candidate.url === '/api/v1/modules/0/settings',
        ).length,
      ).toBe(0);

      expect(answer('PUT', '/api/v1/modules/0', ECHO)).toBe(1);

      // Still none after the module write settled, and the submission concluded regardless: waiting on a
      // write that was never issued would strand the operator on the screen for ever.
      expect(
        http.match(
          (candidate) => candidate.method === 'PUT' && candidate.url === '/api/v1/modules/0/settings',
        ).length,
      ).toBe(0);
      expect(navigate).toHaveBeenCalledWith(['/modules'], { replaceUrl: true });

      drain();
    });

    it('stays on the screen when a write is rejected, so the messages remain reachable', () => {
      submit();

      for (const request of http.match((candidate) => candidate.method === 'PUT')) {
        request.flush(
          // `detail` omitted rather than null - see the note on `reject()` in 'per-field message
          // association'. With `detail: null` the document is discarded and the messages this case's own
          // title calls reachable would not exist at all.
          { type: 'about:blank', title: 'Bad Request', status: 400, errors: {} },
          { status: 400, statusText: 'Bad Request' },
        );
      }

      fixture.detectChanges();

      expect(navigate).not.toHaveBeenCalled();
      expect(notify).not.toHaveBeenCalledWith('success', jasmine.any(String), null, true);

      drain();
    });

    /**
     * Confirms the destructive dialog, which is the only way to reach the removal.
     *
     * The affordance is behind a privilege input, and the shared dialog prefixes a warning glyph to a
     * dangerous confirm label, so the confirm button is located by its danger class rather than by its text.
     */
    function confirmRemoval(): void {
      setInput('canDelete', true);

      const open = actionButton('Delete');
      expect(open).withContext('the deletion affordance must be offered').not.toBeNull();
      // Non-null asserted on the argument, guarded by the expectation immediately above.
      open!.click();
      fixture.detectChanges();

      const confirm = q<HTMLButtonElement>('.confirm-dialog__button--danger');
      expect(confirm).withContext('the confirmation must be showing').not.toBeNull();
      // Non-null asserted on the argument, guarded by the expectation immediately above.
      confirm!.click();
      fixture.detectChanges();
    }

    it('claims nothing and goes nowhere until the removal has actually been carried out', () => {
      let removed = 0;
      component.remove.subscribe(() => (removed += 1));

      confirmRemoval();

      // ⚠ THE ASSERTION THE DEFECT WOULD FAIL. All three of these used to be raised in the same statement
      // block as the command, so the operator was told the module had been removed, the host component was
      // told the same, and the screen was left - all before the server had been asked, let alone answered.
      expect(notify)
        .withContext('nothing is announced while the removal is still outstanding')
        .not.toHaveBeenCalled();
      expect(removed).withContext('and the host component is not told either').toBe(0);
      expect(navigate).withContext('and the screen is not left').not.toHaveBeenCalled();

      const removal = http.expectOne(
        (candidate) => candidate.method === 'DELETE' && candidate.url === '/api/v1/modules/0',
      );

      // The placement travels as a query parameter, so the address is matched without its query above and the
      // placement is asserted here. Thirty-one is the tabModuleId the seeded module carries.
      expect(removal.request.params.get('tabModuleId')).toBe('31');

      removal.flush(null);
      fixture.detectChanges();

      expect(notify).toHaveBeenCalledWith('success', jasmine.any(String), null, true);
      expect(removed).withContext('the host component is told once the server has answered').toBe(1);
      expect(navigate).toHaveBeenCalledWith(['/modules'], { replaceUrl: true });

      drain();
    });

    it('makes no claim, emits nothing and stays put when the removal is refused', () => {
      let removed = 0;
      component.remove.subscribe(() => (removed += 1));

      confirmRemoval();

      const removal = http.expectOne(
        (candidate) => candidate.method === 'DELETE' && candidate.url === '/api/v1/modules/0',
      );

      // A 403 is the refusal most likely to be met here in practice: the tenant resolves from the request's
      // own host, so an operator who does not administer the addressed module's portal is refused on the
      // server no matter what this screen offered.
      removal.flush(
        { type: 'about:blank', title: 'Forbidden', status: 403, detail: 'Not your tenant.', errors: {} },
        { status: 403, statusText: 'Forbidden' },
      );

      fixture.detectChanges();

      expect(notify)
        .withContext('a refusal is never reported as a removal')
        .not.toHaveBeenCalledWith('success', jasmine.any(String), null, true);
      expect(removed).withContext('and the host component is not told a removal happened').toBe(0);
      expect(navigate)
        .withContext('the operator stays on the screen, where the reason is readable')
        .not.toHaveBeenCalled();

      drain();
    });

    it('does not resolve a removal against the listing re-read that follows it', () => {
      // A successful removal makes the store re-read the listing, and that re-read can fail on its own
      // account. Matching the operation as well as the write flag is what stops the store's aggregate
      // failure from being reported as a failed removal - and, in the other direction, stops the re-read's
      // own success from being announced twice.
      let removed = 0;
      component.remove.subscribe(() => (removed += 1));

      confirmRemoval();

      http
        .expectOne((candidate) => candidate.method === 'DELETE' && candidate.url === '/api/v1/modules/0')
        .flush(null);

      fixture.detectChanges();

      expect(removed).withContext('the removal itself settled successfully').toBe(1);
      expect(notify).toHaveBeenCalledTimes(1);

      const reread = http.match(
        (candidate) => candidate.method === 'GET' && candidate.url === '/api/v1/modules',
      );

      expect(reread.length).withContext('the store re-reads the listing after a removal').toBe(1);

      for (const request of reread) {
        request.flush(
          { type: 'about:blank', title: 'Server Error', status: 500, detail: null, errors: {} },
          { status: 500, statusText: 'Server Error' },
        );
      }

      fixture.detectChanges();

      // Still exactly one announcement. The re-read's failure is the banner's business, not a second verdict
      // on a removal that had already succeeded.
      expect(notify).toHaveBeenCalledTimes(1);
      expect(removed).toBe(1);

      drain();
    });
  });

  describe('abandonment and deletion', () => {
    beforeEach(() => {
      setInput('settings', moduleOf());
    });

    it('emits an abandonment without validating, matching causesvalidation="False"', () => {
      let cancelled = 0;
      component.cancel.subscribe(() => (cancelled += 1));

      actionButton('Cancel')?.click();
      fixture.detectChanges();

      expect(cancelled).toBe(1);
    });

    it('offers no deletion affordance unless the caller may delete', () => {
      expect(actionButton('Delete')).toBeNull();

      setInput('canDelete', true);

      expect(actionButton('Delete')).not.toBeNull();
    });

    it('asks before deleting, and names the module so the target is unambiguous', () => {
      setInput('canDelete', true);
      expect(q('app-confirm-dialog')).withContext('presence in the document is what opens the dialog').toBeNull();

      actionButton('Delete')?.click();
      fixture.detectChanges();

      const dialog = q('app-confirm-dialog');
      expect(dialog).not.toBeNull();
      expect(dialog?.textContent).toContain('Latest News');
    });

    it('falls back to the definition name when the module carries no title', () => {
      setInput('canDelete', true);
      setInput('settings', moduleOf({ moduleTitle: null }));

      actionButton('Delete')?.click();
      fixture.detectChanges();

      expect(q('app-confirm-dialog')?.textContent).toContain('Announcements');
    });

    it('emits nothing when the confirmation is abandoned, and closes it', () => {
      setInput('canDelete', true);
      let removed = 0;
      component.remove.subscribe(() => (removed += 1));

      actionButton('Delete')?.click();
      fixture.detectChanges();

      const cancelButton = qa<HTMLButtonElement>('.confirm-dialog__button').find(
        (button) => (button.textContent ?? '').trim() === 'Cancel',
      );
      expect(cancelButton).toBeTruthy();
      cancelButton?.click();
      fixture.detectChanges();

      expect(removed).toBe(0);
      expect(q('app-confirm-dialog')).toBeNull();
    });

    it('emits the removal once confirmed and closes the dialog', () => {
      setInput('canDelete', true);
      let removed = 0;
      component.remove.subscribe(() => (removed += 1));

      actionButton('Delete')?.click();
      fixture.detectChanges();

      // The shared dialog prefixes a warning glyph to a dangerous confirm label, so the button's text is not
      // the bare label. It is located by its danger class and matched on its ending.
      const confirmButton = q<HTMLButtonElement>('.confirm-dialog__button--danger');
      expect(confirmButton).not.toBeNull();
      expect((confirmButton?.textContent ?? '').trim().endsWith('Delete')).toBeTrue();

      confirmButton?.click();
      fixture.detectChanges();

      expect(removed).toBe(1);
      expect(q('app-confirm-dialog')).toBeNull();
    });
  });

  describe('scope decisions, asserted so a regression is visible', () => {
    beforeEach(() => {
      setInput('canManageAllPages', true);
      setInput('settings', moduleOf());
      openEverything();
    });

    it('offers no container selector, because a container is a skin object and skinning is excluded', () => {
      expect(q('#module-settings-containerSrc')).toBeNull();
      expect(host().textContent).not.toContain('Module Container');
    });

    it('offers the legacy move-to-page selector over the supplied page lookup', () => {
      const selector = field<HTMLSelectElement>('tabId');
      expect(selector).not.toBeNull();
      // `?? []` rather than an assertion operator: a missing selector then yields an empty list, which the
      // expectation below rejects just as clearly, and the guard above has already failed the spec.
      expect(
        Array.from(selector?.options ?? []).map((option) => option.textContent?.trim()),
      ).toEqual(['Home', 'About']);
      expect(host().textContent).toContain('Move To Page');
    });

    it('offers a plain multi-line control rather than a rich text editor', () => {
      const header = field<HTMLTextAreaElement>('header');
      expect(header).not.toBeNull();
      expect(header?.tagName.toLowerCase()).toBe('textarea');
      expect(header?.getAttribute('rows')).toBe('6', 'the legacy control declared rows="6"');
    });

    it('names the visibility choice group with a caption that claims no control of its own', () => {
      // ⚠ A CAPTION AND NOT A `label`, and the shared field renders it that way BECAUSE this field declares
      // no `for`: a label names exactly one control, so pointing it at the first radio would name the group
      // by side effect and make clicking its title select an option. The group is then named from that
      // caption by reference, which is the only correct way to name a set.
      const region = regionCaptioned('Visibility');

      expect(region).withContext('visibility must be a rendered field').not.toBeNull();

      const caption = region?.querySelector<HTMLElement>('.form-field__label');

      expect(caption).withContext('visibility must have a visible name').not.toBeNull();
      expect(caption?.tagName.toLowerCase())
        .withContext('a caption naming a SET is not a label element')
        .toBe('span');
      expect(caption?.hasAttribute('for')).toBeFalse();
      expect(caption?.id).withContext('the caption must be addressable to be referenced').toBeTruthy();

      const group = region?.querySelector('[role="radiogroup"]');

      expect(group).withContext('visibility must be a radiogroup').not.toBeNull();
      expect(group?.getAttribute('aria-labelledby'))
        .withContext('the group is named FROM the caption, by reference')
        .toBe(caption?.id ?? null);
    });

    it('does not render legacy appearance controls that have no read or write contract', () => {
      for (const name of ['paneName', 'alignment', 'color', 'border', 'displayPrint', 'displaySyndicate']) {
        expect(field(name)).withContext(`${name} would submit an unmapped member and receive 400`).toBeNull();
      }
    });

    it('describes every control by its own hint', () => {
      // ⚠ THE DESCRIPTION IS APPLIED BY THE SHARED FIELD, ONTO THE PROJECTED CONTROL, and it names the help
      // panel that field owns - `<for>-help`. The screen no longer composes the reference itself, which is
      // the whole point of the conversion: one component owns the wiring for all 118 fields in the
      // application instead of this screen owning a second copy of it.
      //
      // Asserted against the field's OWN published identifier rather than against a string built here, so a
      // change to the shared scheme cannot leave this passing against a reference that no longer resolves.
      for (const name of ['moduleTitle', 'cacheTime', 'iconFile', 'header', 'footer']) {
        const control = field<HTMLElement>(name);

        expect(control).withContext(`${name} must be rendered`).not.toBeNull();

        // ⚠ NOTHING IS NAMED WHILE THE HELP IS CLOSED, AND THAT IS CORRECT RATHER THAN MISSING. The shared
        // field REMOVES the help panel from the document while collapsed, so naming it then would be a
        // reference to an element that does not exist - which is worse than no description at all. This
        // screen's own machinery named the hint unconditionally because its hint was always rendered.
        expect(control?.getAttribute('aria-describedby'))
          .withContext(`${name} must name nothing while its help is closed and it is valid`)
          .toBeNull();

        const region = regionOf(`module-settings-${name}`);

        expect(hintIn(region)).withContext(`${name}'s help must be reachable`).not.toBeNull();

        const described = field<HTMLElement>(name)?.getAttribute('aria-describedby') ?? '';

        expect(described)
          .withContext(`${name} must be described by its own help panel once revealed`)
          .toBe(`module-settings-${name}-help`);

        // And the reference RESOLVES. A dangling description is exactly what a scheme change leaves behind.
        expect(q(`#${described}`)).withContext(`#${described} must exist`).not.toBeNull();
      }
    });
  });

  describe('choice group naming', () => {
    /**
     * MIGRATION - ⚠ THE NATIVE `name` IS LOAD-BEARING, AND `formControlName` DOES NOT SUPPLY IT. The reactive
     * radio directive groups its members in the MODEL, by the control they share, and writes nothing on to the
     * element. Every one of the browser's own radio behaviours is keyed off the native attribute instead:
     * arrow keys moving the selection within the group, and the whole group occupying ONE tab stop rather
     * than one per option. A group missing it degrades, for anyone navigating by keyboard, into a row of
     * independent controls - which is a keyboard-operability regression against the legacy screen, whose
     * `asp:RadioButtonList` rendered a shared name for exactly this reason.
     *
     * The name is written as a STATIC attribute equal to the control's own name, so the two cannot drift.
     */
    beforeEach(() => {
      setInput('canManageAllPages', true);
      setInput('settings', moduleOf());
      openEverything();
    });

    it('gives every visibility radio the same native name as its form control', () => {
      const group = radios('visibility');
      expect(group.length).withContext('the legacy group declared three options').toBe(3);

      for (const radio of group) {
        expect(radio.getAttribute('name'))
          .withContext(`${radio.id} must carry a native name equal to its formControlName`)
          .toBe(radio.getAttribute('formcontrolname'));
        expect(radio.getAttribute('name')).toBe('visibility');
      }

      // One name across the group, not one per option: that single shared value IS what makes the browser
      // treat the three inputs as one control.
      expect(new Set(group.map((radio) => radio.name)).size).toBe(1);
    });

    it('renders no other choice group, so this is the whole obligation on this screen', () => {
      // Pinned so that a group added later without a name is caught here rather than by a keyboard user.
      const named = qa<HTMLInputElement>('input[type="radio"]');
      expect(named.length).toBe(3);

      for (const radio of named) {
        expect(radio.getAttribute('name'))
          .withContext(`${radio.id} must carry a native name`)
          .not.toBeNull();
      }
    });
  });

  describe('per-field message association', () => {
    /**
     * MIGRATION - ⚠ A `role="alert"` REGION IS ANNOUNCED ONCE AND NEVER AGAIN, SO IT CANNOT BE THE ONLY ROUTE
     * TO THE MESSAGE. A person who hears the refusal, moves to the control to correct the value and then
     * returns is told nothing, because the region has already spoken and nothing associates it with the
     * control. The control's `aria-describedby` therefore names the region as well as the hint, which is what
     * makes the message reachable FROM the field on every refocus.
     *
     * These cases also pin the identifier's uniqueness. The client message and the server messages were
     * briefly two sibling elements that both bound `messageId(field)` - a duplicated id on the four controls
     * that carry a client rule, and an `aria-describedby` resolving to whichever the browser found first.
     * Both kinds now share ONE region, so the count assertions below are the regression guard for that.
     *
     * Driven through the ROUTE path, because a rejected write is what puts a problem document on the screen
     * and the store's failure observer is only reached that way.
     */
    const ECHO = {
      moduleId: 0,
      tabModuleId: 31,
      tabId: 0,
      portalId: 0,
      moduleDefId: 14,
      moduleTitle: 'Latest News',
      allTabs: true,
      header: 'Header markup',
      footer: 'Footer markup',
      startDate: null,
      endDate: null,
      inheritViewPermissions: false,
      isDeleted: false,
      moduleOrder: 6,
      cacheTime: 0,
      iconFile: 'module.gif',
      visibility: MODULE_VISIBILITY.none,
      displayTitle: false,
      friendlyName: 'Announcements',
      moduleName: 'DNN_Announcements',
      description: null,
      version: '01.00.00',
      desktopModuleId: 3,
    };

    let http: HttpTestingController;

    /**
     * Answers every outstanding request for one method and url.
     *
     * @param method The HTTP method to match.
     * @param url The exact url to match.
     * @param data The payload to place in the response envelope.
     */
    function answer(method: string, url: string, data: unknown): void {
      for (const request of http.match(
        (candidate) => candidate.method === method && candidate.url === url,
      )) {
        request.flush({ data });
      }

      fixture.detectChanges();
    }

    /**
     * Rejects the outstanding module write with a problem document carrying per-field messages.
     *
     * @param errors The model-state dictionary, keyed as the server keys it.
     */
    function reject(errors: Readonly<Record<string, readonly string[]>>): void {
      const outstanding = http.match((candidate) => candidate.method === 'PUT');
      expect(outstanding.length).withContext('a write must be outstanding to reject').toBe(1);

      outstanding[0].flush(
        // `detail` is OMITTED, not null. `isProblemDetails` is a conjunction over every member that is
        // present, so `detail: null` fails the string test and the whole document is discarded in favour of a
        // status-only fallback carrying no `errors` at all - which is exactly what a per-field assertion must
        // not be written against. ASP.NET Core omits a null ProblemDetails member rather than emitting it, so
        // absence is also the shape the server really sends.
        { type: 'about:blank', title: 'Bad Request', status: 400, errors },
        { status: 400, statusText: 'Bad Request' },
      );

      // TWO passes, deliberately. The failure reaches the screen through an EFFECT, so the first pass runs
      // the effect and the problem document it writes dirties the view only for the pass after it. One pass
      // would assert against markup rendered from the state as it was BEFORE the refusal was recorded.
      fixture.detectChanges();
      fixture.detectChanges();
    }

    /** Answers whatever the store still has outstanding, so no request is left parked at teardown. */
    function drain(): void {
      for (const request of http.match(() => true)) {
        const url = request.request.url;
        const body = url.includes('/tabs') || url.includes('module-definitions') ? [] : null;
        request.flush({ data: body });
      }

      fixture.detectChanges();
    }

    beforeEach(() => {
      http = TestBed.inject(HttpTestingController);
      spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

      setInput('pages', null);
      fixture.componentRef.setInput('moduleId', '0');
      fixture.detectChanges();

      answer('GET', '/api/v1/modules/0', ECHO);
      answer('GET', '/api/v1/modules/0/settings', {
        moduleId: 0,
        tabModuleId: 31,
        moduleSettings: {},
        tabModuleSettings: {},
      });
      answer('GET', '/api/v1/module-definitions/14', {
        moduleDefId: 14,
        friendlyName: 'Announcements',
        desktopModuleId: 3,
        defaultCacheTime: 0,
        moduleName: 'DNN_Announcements',
        description: null,
        version: '01.00.00',
        isPremium: false,
        isAdmin: false,
        isPortable: true,
      });
      answer('GET', '/api/v1/portals/0/tabs', []);
      openEverything();
    });

    it('names only what is rendered while there is nothing to report', () => {
      // ⚠ THE INVARIANT IS "NO DANGLING REFERENCE", not "always name the hint". Both of this field's
      // describable regions are absent right now - the help panel is removed while collapsed and the
      // message region is not emitted while the field is valid - so the control names NEITHER.
      expect(field<HTMLElement>('moduleTitle')?.getAttribute('aria-describedby')).toBeNull();

      expect(messagesFor('moduleTitle')).toEqual([]);
      expect(regionOf('module-settings-moduleTitle')?.querySelector('.form-field__errors')).toBeNull();

      // Revealing the help brings its reference into existence, and only then is it named.
      expect(hintIn(regionOf('module-settings-moduleTitle'))).not.toBeNull();
      expect(field<HTMLElement>('moduleTitle')?.getAttribute('aria-describedby'))
        .toBe('module-settings-moduleTitle-help');

      // A valid field also says so, rather than saying nothing: the state is published in both directions.
      expect(field<HTMLElement>('moduleTitle')?.getAttribute('aria-invalid')).toBe('false');

      drain();
    });

    it('associates a server message with the control that was refused', () => {
      submit();
      reject({ ModuleTitle: ['A module title is required.'] });

      const region = regionOf('module-settings-moduleTitle')?.querySelector('.form-field__errors') ?? null;

      expect(region).withContext('the refused control must render its message').not.toBeNull();
      expect(region?.getAttribute('role'))
        .withContext('a refusal follows the person\u2019s own action, so it is assertive')
        .toBe('alert');
      expect(messagesFor('moduleTitle')).toEqual(['A module title is required.']);

      // The message region exists, so it IS named - and it is named on its own while the help panel is
      // still collapsed and therefore absent.
      expect(field<HTMLElement>('moduleTitle')?.getAttribute('aria-describedby'))
        .toBe('module-settings-moduleTitle-error');

      // ⚠ THE HELP IS NAMED FIRST AND THE MESSAGE SECOND once both exist, so the order of the description
      // does not change as a field moves in and out of refusal - a reader hears the same field described
      // the same way either side of one, with the reason added rather than the description rebuilt.
      expect(hintIn(regionOf('module-settings-moduleTitle'))).not.toBeNull();
      expect(field<HTMLElement>('moduleTitle')?.getAttribute('aria-describedby'))
        .toBe('module-settings-moduleTitle-help module-settings-moduleTitle-error');

      drain();
    });

    it('associates every refused control, not only the first', () => {
      submit();
      reject({
        ModuleTitle: ['A module title is required.'],
        CacheTime: ['Cache duration must not be negative.'],
      });

      for (const name of ['moduleTitle', 'cacheTime']) {
        const control = field<HTMLElement>(name);

        expect(control).withContext(`${name} must be rendered`).not.toBeNull();
        expect(control?.getAttribute('aria-describedby'))
          .withContext(`${name} must name its OWN message region and not a sibling's`)
          .toBe(`module-settings-${name}-error`);
        expect(control?.getAttribute('aria-invalid'))
          .withContext(`${name} was refused, so it must say it is invalid`)
          .toBe('true');
      }

      expect(messagesFor('moduleTitle')).toEqual(['A module title is required.']);
      expect(messagesFor('cacheTime')).toEqual(['Cache duration must not be negative.']);

      drain();
    });

    it('renders two messages for one control in one region', () => {
      submit();
      reject({ ModuleTitle: ['A module title is required.', 'Title must be 256 characters or fewer.'] });

      // ONE region, so one identifier and one description - not one region per message. Counted rather
      // than assumed, because two regions sharing one identifier is exactly the defect that made the
      // screen's own field machinery ambiguous before it was replaced.
      expect(qa('#module-settings-moduleTitle-error').length).toBe(1);

      expect(messagesFor('moduleTitle')).toEqual([
        'A module title is required.',
        'Title must be 256 characters or fewer.',
      ]);

      drain();
    });

    it('reports a client and a server message through the SAME region, local reason first', () => {
      submit();
      reject({ CacheTime: ['Cache duration must not be negative.'] });

      // The server's message is on screen; now make the control fail locally as well. The problem document is
      // held until the next save answers, so both reasons are live at once - which is precisely the state that
      // duplicated the identifier when the two were separate elements.
      type('cacheTime', 'abc');
      // `touched`, not `dirty`, is what the client rule reports on, and dispatching `input` sets only the
      // latter - the same explicit mark every other validation case on this screen makes.
      component['form'].controls.cacheTime.markAsTouched();
      fixture.detectChanges();

      expect(qa('#module-settings-cacheTime-error').length)
        .withContext('two elements sharing one id is invalid markup and an ambiguous description')
        .toBe(1);

      // ⚠ ORDER IS THE ASSERTION. The local reason is produced first and is announced first, which is what
      // `messagesFor` on the component composes and what the shared field renders without reordering.
      expect(messagesFor('cacheTime')).toEqual([
        'Invalid Cache Time',
        'Cache duration must not be negative.',
      ]);

      const control = field<HTMLElement>('cacheTime');

      expect(control).not.toBeNull();
      expect(control?.getAttribute('aria-describedby')).toBe('module-settings-cacheTime-error');

      drain();
    });

    it('reports the permission switch under its own key, in the region that captions it', () => {
      submit();
      reject({ InheritViewPermissions: ['The inherit flag could not be applied.'] });

      const control = field<HTMLElement>('inheritViewPermissions');

      expect(control).withContext('the inherit switch must be rendered').not.toBeNull();

      // ⚠ THE TWO-FIELD INDIRECTION IS GONE, AND ITS ABSENCE IS THE ASSERTION. The switch used to be
      // described by the PERMISSIONS hint while reporting failures under its OWN control name, so the
      // description had to be composed from two different field keys and no single identifier stem
      // described the field. The shared component owns one region per field, so both the help and the
      // message now belong to the same field and share one stem - and because that field declares no
      // `for`, the stem is the component's own per-instance one rather than a name derived here.
      const region = regionCaptioned('Permissions');

      expect(region).withContext('the permissions field must be rendered').not.toBeNull();
      expect(messagesIn(region)).toEqual(['The inherit flag could not be applied.']);

      // Revealed first, so BOTH of the field's regions exist and both must be named.
      expect(hintIn(region)).withContext('the permissions help must be reachable').not.toBeNull();

      const described = (
        field<HTMLElement>('inheritViewPermissions')?.getAttribute('aria-describedby') ?? ''
      )
        .split(' ')
        .filter(Boolean);

      expect(described.length)
        .withContext('the switch is described by its field\u2019s help AND its message')
        .toBe(2);

      // Both references RESOLVE, and both resolve INSIDE this field rather than into a sibling.
      for (const id of described) {
        const target = q(`#${id}`);

        expect(target).withContext(`#${id} must exist`).not.toBeNull();
        expect(region?.contains(target as Node))
          .withContext(`#${id} must belong to the permissions field`)
          .toBeTrue();
      }

      // And the control now states its invalidity, which it never did before: the region was referenced
      // through `aria-errormessage` while nothing ever said the object was invalid.
      expect(control?.getAttribute('aria-invalid')).toBe('true');

      drain();
    });

    it('states invalidity on every control that can be refused, not only the reference', () => {
      // ⚠ THE ACCESSIBILITY DEFECT THIS BLOCK EXISTS TO CLOSE. Nine controls and groups on this screen used
      // to publish a reference to their error region and never publish `aria-invalid`, so assistive
      // technology was handed a message with nothing to attach it to - the object it described never said it
      // was invalid. Every error-capable control now states it, and states it from the SAME failure state
      // the message is rendered from, so the two cannot disagree.
      submit();
      reject({
        ModuleTitle: ['A module title is required.'],
        TabId: ['That page does not exist.'],
        AllTabs: ['The all-pages flag could not be applied.'],
        Header: ['The header could not be stored.'],
        Footer: ['The footer could not be stored.'],
        IconFile: ['That icon is outside the portal.'],
        Visibility: ['That visibility state is not defined.'],
        DisplayTitle: ['The container flag could not be applied.'],
        InheritViewPermissions: ['The inherit flag could not be applied.'],
      });

      for (const name of [
        'moduleTitle',
        'tabId',
        'allTabs',
        'header',
        'footer',
        'iconFile',
        'displayTitle',
        'inheritViewPermissions',
      ]) {
        expect(field<HTMLElement>(name)?.getAttribute('aria-invalid'))
          .withContext(`${name} was refused, so it must say it is invalid`)
          .toBe('true');
      }

      // The choice group states it on every radio, because a radio is the object a reader lands on and
      // there is no single element for the set that assistive technology examines instead.
      const radios = qa<HTMLInputElement>('[role="radiogroup"] input[type="radio"]');

      expect(radios.length).withContext('the visibility group must be rendered').toBe(3);

      for (const radio of radios) {
        expect(radio.getAttribute('aria-invalid'))
          .withContext(`${radio.id} belongs to a refused group`)
          .toBe('true');
      }

      drain();
    });

    it('withdraws invalidity when the refusal is gone, rather than leaving it asserted', () => {
      // The other direction, and the one a one-way binding gets wrong. A control left asserting invalidity
      // after its reason has been withdrawn is worse than one that never asserted it: a reader is told the
      // value is refused with nothing anywhere saying why.
      submit();
      reject({ TabId: ['That page does not exist.'] });

      expect(field<HTMLElement>('tabId')?.getAttribute('aria-invalid')).toBe('true');

      submit();
      answer('PUT', '/api/v1/modules/0', ECHO);

      expect(messagesFor('tabId')).toEqual([]);
      expect(field<HTMLElement>('tabId')?.getAttribute('aria-invalid')).toBe('false');

      drain();
    });
  });

  it('declares no router link, because it is a leaf screen reached by route', () => {
    setInput('settings', moduleOf());

    expect(fixture.debugElement.queryAll(By.css('a[routerLink]')).length).toBe(0);
  });

  // =====================================================================================================
  // THE WIRE CONTRACT
  // =====================================================================================================
  //
  // Everything above drives the screen through its inputs and asserts what it RENDERS. This block does the
  // other half, and it is the half that cannot be faked: it lets the real `ModuleService` and the real
  // `TabService` issue real `HttpClient` calls, intercepts them, and asserts the url, the verb and - above
  // all - THE REQUEST BODY. A substituted service could not prove any of those; it would only echo back
  // whatever it was configured to return.
  //
  // MIGRATION: EVERY URL ASSERTED HERE IS RELATIVE, AND THAT IS A DEPLOYMENT REQUIREMENT RATHER THAN A
  //   STYLE CHOICE. `angular.json` declares `fileReplacements` under its `development` configuration only,
  //   so the `test` target compiles against the workspace's default environment module - which IS the
  //   production one, the polarity being inverted from the usual Angular scaffold - and that module
  //   publishes the relative base path `/api/v1` because the reverse proxy forwards `/api/` to the API
  //   container on the same origin. An absolute origin would pass a spec written against it while breaking
  //   the container deployment, so not one appears anywhere in this file - neither in an assertion nor in
  //   an import, since importing the environment module here would defeat the point of the base path being
  //   relative in the first place.
  //
  // MIGRATION: `GET /api/v1/module-definitions/{moduleDefId}` IS PART OF THIS SCREEN'S READ SET, and it is
  //   worth stating plainly because a reader working only from the module endpoint list would not expect
  //   it. The three-state cache rule below is unimplementable without it: `defaultCacheTime` is recorded on
  //   the DEFINITION and on nothing else, exactly as `ModuleSettings.ascx.vb:L136-L142` read
  //   `objModuleDef.DefaultCacheTime` from a definition it fetched separately. The endpoint is the
  //   published read-only definition lookup, not an invention.
  describe('wire contract', () => {
    /** The routed address used throughout: module 0, which is a REAL identifier. */
    const MODULE_URL = '/api/v1/modules/0';
    const SETTINGS_URL = '/api/v1/modules/0/settings';
    const DEFINITION_URL = '/api/v1/module-definitions/14';
    const TABS_URL = '/api/v1/portals/0/tabs';

    let notify: jasmine.Spy;

    /**
     * Drives the routed activation and answers the four reads the screen issues, in order.
     *
     * The order is the screen's own: the module and its settings are requested from the address, and the
     * definition and the tenant's page list become addressable only once the module has resolved and
     * disclosed its `moduleDefId` and `portalId`. Answering them in any other order would not reflect the
     * real sequence.
     *
     * @param detail The module payload to publish.
     * @param definition The definition payload to publish.
     * @param tabs The tenant's pages to publish.
     */
    function activate(
      detail: ModuleDetail = moduleDetailOf(),
      definition: ModuleDefinition = definitionOf(),
      tabs: readonly TabListItem[] = [tabOf()],
      bag: ModuleSettingsBag = settingsBagOf(),
    ): void {
      fixture.componentRef.setInput('pages', null);
      fixture.componentRef.setInput('moduleId', 0);
      fixture.detectChanges();

      httpMock.expectOne(MODULE_URL).flush({ data: detail });
      httpMock.expectOne(SETTINGS_URL).flush({ data: bag });
      fixture.detectChanges();

      httpMock.expectOne(DEFINITION_URL).flush({ data: definition });
      httpMock.expectOne(TABS_URL).flush({ data: tabs });
      fixture.detectChanges();
    }

    /**
     * Submits the form and returns the intercepted module replacement.
     *
     * @returns The request the screen actually issued.
     */
    function interceptSubmission(): TestRequest {
      submit();

      return httpMock.expectOne(
        (candidate) => candidate.method === 'PUT' && candidate.url === MODULE_URL,
      );
    }

    beforeEach(() => {
      notify = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
    });

    // ---------------------------------------------------------------------------------------------------
    // THE DECLARED-PERMISSION ADVISORY
    // ---------------------------------------------------------------------------------------------------
    //
    // The advisory names the permission vocabulary the module's DEFINITION declares, beside the inherit
    // switch that chooses whether to use it. It is read-only and nothing on the form depends on it, which
    // is exactly what the last two cases here pin down: a refusal and a fault each leave the screen
    // working and silent rather than banner-ing over a form that saved perfectly well.
    describe('the declared-permission advisory', () => {
      /** The catalogue address. The filter travels as a query parameter and is asserted separately. */
      const PERMISSIONS_URL = '/api/v1/permissions';

      let tokenStorage: TokenStorageService;

      beforeEach(() => {
        tokenStorage = TestBed.inject(TokenStorageService);
      });

      /** The one outstanding catalogue read, asserted by verb and address. */
      function expectCatalogue(): TestRequest {
        return httpMock.expectOne(
          (candidate) => candidate.method === 'GET' && candidate.url === PERMISSIONS_URL,
          'the definition catalogue read',
        );
      }

      /** Asserts that no catalogue read is outstanding. */
      function expectNoCatalogue(): void {
        httpMock.expectNone(
          (candidate) => candidate.method === 'GET' && candidate.url === PERMISSIONS_URL,
        );
      }

      /** The advisory element, or `null` when the screen renders none. */
      function advisory(): HTMLElement | null {
        return q<HTMLElement>('.module-settings__declared-permissions');
      }

      /** Every key the advisory names, in document order. */
      function advisedKeys(): readonly string[] {
        return qa<HTMLElement>('.module-settings__declared-permission').map((chip) =>
          (chip.textContent ?? '').trim(),
        );
      }

      it('reads the catalogue filtered by the DEFINITION and names the keys it answers', () => {
        tokenStorage.store(sessionWith(true));
        activate();

        const request: TestRequest = expectCatalogue();

        // ⚠ THE FILTER IS THE DEFINITION, NOT THE MODULE, and this is the assertion that pins it. The
        // unfiltered listing answers from the API's closed key enumeration and touches no store at all,
        // so it would report the same four keys for every module ever loaded and would say nothing about
        // this one.
        expect(request.request.params.get('moduleDefinitionId')).toBe('14');
        expect(request.request.params.get('permissionCode'))
          .withContext('no filter this screen did not intend')
          .toBeNull();
        expect(request.request.params.get('permissionKey')).toBeNull();

        request.flush({ data: ['VIEW', 'EDIT'] });
        fixture.detectChanges();

        expect(advisory()).withContext('the advisory is rendered').not.toBeNull();
        expect(advisedKeys()).toEqual(['VIEW', 'EDIT']);
        expect((advisory()?.textContent ?? '')).toContain('Permissions defined for this module type:');
      });

      it('withholds the read entirely from a caller who does not administer the tenant', () => {
        // ⚠ WITHHELD, NOT RECOVERED FROM. The endpoint is declared under the administrator policy, so
        // asking anyway would write a 403 into the network log of an editor entitled to be on this
        // screen. `httpMock.verify()` in the suite's teardown is what makes this assertion binding.
        tokenStorage.store(sessionWith(false));
        activate();

        expectNoCatalogue();
        expect(advisory()).withContext('nothing is claimed about the definition').toBeNull();
      });

      it('renders nothing when the definition declares no keys, without claiming it read none', () => {
        tokenStorage.store(sessionWith(true));
        activate();

        expectCatalogue().flush({ data: [] });
        fixture.detectChanges();

        // An empty answer and an unavailable answer both render nothing, which is correct: in neither
        // case does this client have anything to say. What must NOT happen is a line asserting that the
        // definition declares no permissions, because that reads as a fact about the definition.
        expect(advisory()).toBeNull();
        expect(host().textContent ?? '').not.toContain('Permissions defined for this module type:');
      });

      it('leaves the screen working and silent when the catalogue read fails', () => {
        tokenStorage.store(sessionWith(true));
        activate();

        expectCatalogue().flush(
          { type: 'about:blank', title: 'Server Error', status: 500 },
          { status: 500, statusText: 'Internal Server Error' },
        );
        fixture.detectChanges();

        // Nothing on the form depends on the answer, so a fault costs the advisory and nothing else.
        expect(advisory()).withContext('the region is simply absent').toBeNull();
        expect(notify).withContext('an advisory failure raises no message').not.toHaveBeenCalled();
        expect(field<HTMLInputElement>('moduleTitle')?.value)
          .withContext('the form is still seeded and usable')
          .toBe(moduleDetailOf().moduleTitle ?? '');

        // And the screen still saves: the failed read is not allowed to block the submission.
        expect(interceptSubmission().request.method).toBe('PUT');
      });

      it('issues the read once per definition rather than on every form change', () => {
        tokenStorage.store(sessionWith(true));
        activate();

        expectCatalogue().flush({ data: ['EDIT'] });
        fixture.detectChanges();

        // An effect re-runs on every dependency change, so an unguarded fetch here would re-issue the
        // request on each keystroke. Two edits, and no second read.
        type('moduleTitle', 'Renamed once');
        type('moduleTitle', 'Renamed twice');
        fixture.detectChanges();

        expectNoCatalogue();
        expect(advisedKeys()).withContext('the answer already held is kept').toEqual(['EDIT']);
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // THE ADDRESS
    // ---------------------------------------------------------------------------------------------------

    describe('addressing', () => {
      /**
       * MIGRATION: MODULE 0 IS A REAL MODULE, AND THIS IS THE GUARD AGAINST EVERY TRUTH TEST.
       * `dbo.Modules.ModuleID` is declared `IDENTITY (0, 1)`, so the first module ever created carries the
       * identifier 0. `if (moduleId)`, `moduleId > 0` and `moduleId ?? -1` would each silently refuse to
       * load it, and none of the three would fail to compile. The screen must read `=== undefined` and
       * nothing else, which is what these two expectations prove: had it applied a truth test, no request
       * would exist to match and both would fail.
       */
      it('reads module 0, because zero is a real identifier and never an absence', () => {
        fixture.componentRef.setInput('moduleId', 0);
        fixture.detectChanges();

        expect(httpMock.expectOne(MODULE_URL).request.method).toBe('GET');
        expect(httpMock.expectOne(SETTINGS_URL).request.method).toBe('GET');

        httpMock.match(() => true).forEach((request) => request.flush({ data: null }));
        fixture.detectChanges();
      });

      it('coerces the string the router supplies, so the path parameter still addresses module 0', () => {
        fixture.componentRef.setInput('moduleId', '0');
        fixture.detectChanges();

        // Proven by the url alone: '/api/v1/modules/0' and not '/api/v1/modules/undefined' or NaN.
        //
        // Counted, so the address is a recorded expectation rather than a consequence of how `expectOne`
        // happens to fail: that member asserts by throwing and the runner therefore reports this spec as
        // claiming nothing at all.
        const detail = httpMock.expectOne(MODULE_URL);

        expect(detail.request.url)
          .withContext("the coerced parameter addresses module 0, not 'undefined' and not NaN")
          .toBe(MODULE_URL);

        detail.flush({ data: null });
        httpMock.expectOne(SETTINGS_URL).flush({ data: null });
        fixture.detectChanges();
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // A REFUSED READ
    // ---------------------------------------------------------------------------------------------------
    describe('a read the server refuses', () => {
      /**
       * A REFUSAL IS NOT AN ABSENCE, and this screen used to present it as one - twice over.
       *
       * The server answers `GET /api/v1/modules/{id}` with 403 when the caller may not see the module and
       * with 404 when there is none. Both leave the screen holding nothing, so the derivation behind the
       * not-found affordance could not tell them apart. The refusal therefore arrived as a transient
       * warning advisory with the problem document DISCARDED, while the page itself stated "the requested
       * item could not be found" - one backend condition, two surfaces, one of them untrue.
       *
       * It is now the banner and nothing else, which is what the legacy access-denied page did: a heading
       * and one `YellowWarning` module message (`Website/admin/Security/AccessDenied.ascx.vb:L41-L45`).
       * The sibling module form and export screens present the same status the same way.
       */
      it('presents the refusal in the banner alone, with no empty state beside it', () => {
        fixture.componentRef.setInput('pages', null);
        fixture.componentRef.setInput('moduleId', 0);
        fixture.detectChanges();

        // BOTH reads are refused, because one authorization filter guards both endpoints - and both are
        // answered with the document, because the store holds ONE failure slot and the later answer
        // replaces the earlier one. A fixture that gave only the first read a document would prove
        // nothing about what an operator sees: the second, bodiless refusal would overwrite it and the
        // banner would fall back to the shared wording with no trace identifier at all.
        const refusal = {
          type: 'about:blank',
          title: 'Forbidden',
          status: 403,
          detail: 'You are not permitted to view this module.',
          traceId: '00-1a2b3c4d5e6f-01',
        };

        httpMock
          .expectOne(MODULE_URL)
          .flush(refusal, { status: 403, statusText: 'Forbidden' });
        httpMock
          .expectOne(SETTINGS_URL)
          .flush(refusal, { status: 403, statusText: 'Forbidden' });
        fixture.detectChanges();

        const banner: HTMLElement | null = fixture.nativeElement.querySelector('.error-banner');

        expect(banner).not.toBeNull();
        expect(banner?.getAttribute('data-severity'))
          .withContext('the legacy YellowWarning severity, resolved by the shared utility')
          .toBe('warning');
        expect(banner?.textContent ?? '').toContain('You are not permitted to view this module.');
        expect(banner?.textContent ?? '')
          .withContext('the trace identifier the discarded document used to cost')
          .toContain('00-1a2b3c4d5e6f-01');

        // Neither empty state accompanies it, and no form is seeded from a module that was never read.
        expect(fixture.nativeElement.querySelector('app-empty-state')).toBeNull();
        expect(fixture.nativeElement.querySelector('form.module-settings')).toBeNull();
      });

      /**
       * ⚠ THE DEFINITION IS READ FOR THE ADDRESSED MODULE, NEVER FOR THE ONE LEFT BEHIND. Runtime testing
       * measured this on every move between two modules: the store's module slot still holds the previous
       * module until the new address answers, so the definition read fired once with the OLD `moduleDefId`.
       * Moving from module 2 (definition 4, administrative) to module 7 (definition 2) issued
       * `GET /module-definitions/4`, which answers `404`, and the screen raised "The requested item could
       * not be found." over a module whose own reads had all succeeded. The per-identifier memo those
       * effects keep could not prevent it - it suppresses a REPEAT of one identifier, and a stale
       * identifier is a different one.
       *
       * Asserted as an ABSENCE of the wrong request rather than as the presence of the right one, because
       * only the absence distinguishes the fix from the defect: both arrangements eventually read the
       * correct definition.
       */
      it('reads no definition until the loaded module is the one addressed', () => {
        activate();

        // Move to a different module carrying a DIFFERENT definition. The store still holds module 0.
        fixture.componentRef.setInput('moduleId', 5);
        fixture.detectChanges();

        httpMock.expectNone(
          (candidate) => candidate.url === DEFINITION_URL,
          'the module left behind must not have its definition re-read against the new address',
        );
        httpMock.expectNone(
          (candidate) =>
            candidate.method === 'GET' &&
            candidate.url === '/api/v1/permissions' &&
            candidate.params.get('moduleDefinitionId') === '14',
        );

        // Only once the addressed module answers does its own definition become addressable.
        httpMock.expectOne('/api/v1/modules/5').flush({
          data: { ...moduleDetailOf(), moduleId: 5, moduleDefId: 21 },
        });
        httpMock.expectOne('/api/v1/modules/5/settings').flush({ data: settingsBagOf() });
        fixture.detectChanges();

        expect(httpMock.expectOne('/api/v1/module-definitions/21').request.method)
          .withContext('the definition read follows the address, not the residue of the last one')
          .toBe('GET');

        httpMock.match(() => true).forEach((request) => request.flush({ data: [] }));
        fixture.detectChanges();
      });

      /**
       * ⚠ THE REFUSAL THIS SCREEN RECEIVES ON ITS **OWN** READ IS HONOURED, AND THIS CASE EXISTS BECAUSE
       * IT WAS NOT. Measured against the running application: on an administrative module the three reads
       * answer 200 for the module, **403 `module.settings_protected`** for the settings, and 404 for the
       * definition, because the tenant catalogue publishes no entry for an administrative definition. The
       * store holds ONE failure slot, the 404 lands LAST and takes it, and the not-found branch of the
       * announcer deliberately DISCARDS the document in favour of a transient advisory - so the banner
       * emptied itself, the render gate consulted only the module read, and a full sixteen-control
       * EDITABLE form rendered under no banner at all, offering a save the server had already refused.
       *
       * Three separate expectations, because each would pass on its own while the screen was still wrong:
       * the form must be absent, the banner must be present, and the banner must carry the SETTINGS
       * refusal rather than the definition's absence. The last is the load-bearing one - it is what proves
       * the gate keys on this screen's own read and not on whichever failure happened to arrive last.
       */
      it('withholds the form and states the refusal when the SETTINGS read alone is refused', () => {
        fixture.componentRef.setInput('pages', null);
        fixture.componentRef.setInput('moduleId', 0);
        fixture.detectChanges();

        const refusal = {
          type: 'urn:dnnmigration:error:module.settings_protected',
          title: 'Forbidden',
          status: 403,
          detail: 'Administrative module settings are available only through their typed privileged endpoint.',
          traceId: '00-settings-refusal-01',
        };

        // The module itself reads cleanly, which is exactly the condition that used to admit the form.
        httpMock.expectOne(MODULE_URL).flush({ data: moduleDetailOf() });
        httpMock
          .expectOne(SETTINGS_URL)
          .flush(refusal, { status: 403, statusText: 'Forbidden' });
        fixture.detectChanges();

        // The definition read becomes addressable once the module resolves, and it is answered LAST with
        // the absence that used to overwrite the refusal above.
        httpMock.expectOne(DEFINITION_URL).flush(null, { status: 404, statusText: 'Not Found' });
        httpMock.expectOne(TABS_URL).flush({ data: [tabOf()] });
        fixture.detectChanges();

        expect(fixture.nativeElement.querySelector('form.module-settings'))
          .withContext('no editable form may be offered for settings the server refused to disclose')
          .toBeNull();

        const banner: HTMLElement | null = fixture.nativeElement.querySelector('.error-banner');

        expect(banner)
          .withContext('the refusal is stated rather than swallowed by the later absence')
          .not.toBeNull();
        expect(banner?.textContent ?? '')
          .withContext('the SETTINGS document, not the definition 404, is what the operator reads')
          .toContain('Administrative module settings are available only through their typed privileged endpoint.');
        expect(banner?.textContent ?? '')
          .withContext('its own trace identifier, so the refusal can be correlated in the server log')
          .toContain('00-settings-refusal-01');
      });

      /**
       * The converse, which is what stops the fix above becoming a screen that never opens: a settings read
       * that SUCCEEDS clears any refusal held from a previous address, so the form renders normally. Without
       * this expectation a component that latched the refusal permanently would pass the case above.
       */
      it('reopens the form once a settings read succeeds', () => {
        activate();

        expect(fixture.nativeElement.querySelector('form.module-settings'))
          .withContext('a permitted settings read renders the form, refusal state cleared')
          .not.toBeNull();
        expect(fixture.nativeElement.querySelector('.error-banner')).toBeNull();
      });

      /**
       * The 404 path is deliberately UNCHANGED: that status does answer the question of existence, so the
       * shared not-found sentence still describes it and the affordance still appears.
       */
      it('still reports a genuine absence through the not-found affordance', () => {
        fixture.componentRef.setInput('pages', null);
        fixture.componentRef.setInput('moduleId', 0);
        fixture.detectChanges();

        httpMock
          .expectOne(MODULE_URL)
          .flush(null, { status: 404, statusText: 'Not Found' });
        httpMock.expectOne(SETTINGS_URL).flush(null, { status: 404, statusText: 'Not Found' });
        fixture.detectChanges();

        expect(fixture.nativeElement.querySelector('app-empty-state')).not.toBeNull();
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // THE THREE-STATE CACHE RULE
    // ---------------------------------------------------------------------------------------------------
    //
    // MIGRATION: THREE STATES, KEPT APART, EXACTLY AS THE LEGACY SCREEN KEPT THEM. `ModuleSettings.ascx
    //   .vb:L136-L142` reads, verbatim: `If objModuleDef.DefaultCacheTime = Null.NullInteger Then rowCache
    //   .Visible = False Else txtCacheTime.Text = objModule.CacheTime.ToString`. Three distinct facts fall
    //   out of those five lines and each gets its own expectation below, because only separate expectations
    //   can prove the absence of a coalesce. `cacheTime` and `defaultCacheTime` are DIFFERENT PROPERTIES on
    //   DIFFERENT OBJECTS - `ModuleInfo.vb` initialises `_CacheTime` to 0 while `ModuleDefinitionInfo.vb`
    //   initialises `_DefaultCacheTime` to 0 in its constructor and the schema admits -1 - so any
    //   `cacheTime ?? defaultCacheTime`, any `effectiveCacheTime`, or any `!defaultCacheTime` truth test
    //   would collapse two of the three states into one and pass a weaker suite than this one.
    describe('the three-state cache rule', () => {
      /** (a) -1 means the definition records no default at all, so the field must not exist. */
      it('removes the cache field from the DOM when the definition records no default', () => {
        activate(moduleDetailOf(), definitionOf({ defaultCacheTime: -1 }));
        openEverything();

        // ABSENT, not hidden. `@if` removes the node outright, whereas a CSS hide would still be queryable,
        // so querying for null is what distinguishes the two.
        expect(field('cacheTime'))
          .withContext('a definition default of -1 must remove the cache field, not merely hide it')
          .toBeNull();
      });

      /** (b) 0 means caching IS supported with a zero-second default, so the field must show. */
      it('keeps the cache field when the definition default is zero, which is a supported default', () => {
        activate(moduleDetailOf(), definitionOf({ defaultCacheTime: 0 }));
        openEverything();

        expect(field('cacheTime'))
          .withContext('a definition default of 0 supports caching and must NOT remove the field')
          .not.toBeNull();
      });

      /** (c) A stored period of 0 is a legitimate saved value: it must display AND be posted. */
      it('displays a stored cache period of zero as 0 and posts it as 0, never as blank', () => {
        activate(moduleDetailOf({ cacheTime: 0 }), definitionOf({ defaultCacheTime: 0 }));
        openEverything();

        expect(field<HTMLInputElement>('cacheTime')?.value)
          .withContext('a stored period of 0 must render as "0" and never as an empty control')
          .toBe('0');

        const request = interceptSubmission();
        const body = request.request.body as UpdateModuleRequest;

        expect(body.cacheTime)
          .withContext('0 seconds is a saved value and must reach the server as the number 0')
          .toBe(0);

        request.flush({ data: moduleDetailOf({ cacheTime: 0 }) });
        fixture.detectChanges();
        drainListingReread();
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // FALSY VALUES ON THE WIRE
    // ---------------------------------------------------------------------------------------------------
    //
    // MIGRATION: A FALSY VALUE IS DATA, AND THIS IS WHERE THAT IS ENFORCED RATHER THAN ASSERTED IN PROSE.
    //   `Library/Components/Shared/Null.vb` returns the EMPTY STRING as its string sentinel (L71-L75) and
    //   FALSE as its boolean sentinel (L76-L80), and the integer sentinel is -1 while 0 is an ordinary
    //   value. So `''`, `false`, `0` and `-1` are all values this schema legitimately stores and
    //   transmits, and a serialiser that dropped a member because it was falsy would silently clear a
    //   column. The API's own posture matches: it writes every member with
    //   `JsonIgnoreCondition.Never`, so a member missing from a request is a client defect, never a
    //   shorthand for a default.
    //
    // MIGRATION: THE LEGACY ALIGNMENT PICKER HAS NO COUNTERPART HERE, AND THE REASON IS A CONTRACT GAP
    //   RATHER THAN AN OMISSION IN THIS SPEC. `modulesettings.ascx:L122-L127` declared `cboAlign` with four
    //   items - left, center, right, and `value=""` labelled "Not Specified" under the resource key
    //   `Not_Specified` - and that fourth item is `Null.NullString`, a transmitted value rather than an
    //   absence. It cannot be asserted on this screen because `UpdateModuleRequest` does not project
    //   `alignment`, and neither does `ModuleDetail`: the six placement columns the legacy screen edited -
    //   paneName, alignment, color, border, displayPrint and displaySyndicate - are absent from BOTH
    //   contracts, so there is no control to select and no member to inspect. Posting one would draw an
    //   HTTP 400 under the API's `JsonUnmappedMemberHandling.Disallow`. The RULE the alignment case exists
    //   to prove is therefore proven below on the members the contract does project, which carry exactly
    //   the same hazard: an emptied string, three falsy booleans and two zero-valued numbers.
    describe('falsy values survive the request body', () => {
      it('carries an emptied nullable string as an explicit member rather than dropping it', () => {
        activate(moduleDetailOf({ header: 'Header markup' }));
        openEverything();

        type('header', '');

        const request = interceptSubmission();
        const body = request.request.body as UpdateModuleRequest;

        expect('header' in body)
          .withContext('an emptied field must be TRANSMITTED so the column is cleared, never omitted')
          .toBeTrue();

        request.flush({ data: moduleDetailOf({ header: null }) });
        fixture.detectChanges();
        drainListingReread();
      });

      /**
       * MIGRATION: THE MEASURED ABSENCE OF VALIDATION IS ITSELF A PARITY REQUIREMENT, NOT AN OVERSIGHT.
       * `modulesettings.ascx:L32` declares `txtTitle` with neither a `maxlength` attribute nor any
       * validator, and the feature-wide census finds 0 `RequiredFieldValidator` and 0
       * `RegularExpressionValidator`, so an empty title was accepted and saved by the legacy screen. Adding
       * `Validators.required` here would reject input the legacy screen took, which is precisely the
       * opportunistic tightening the migration discipline forbids. The companion case, `txtColor` at L132,
       * likewise carried no validator - and it has no control at all here, for the contract reason recorded
       * on the fourth data-type check.
       */
      it('accepts an empty title and submits it, because the legacy screen required nothing', () => {
        activate(moduleDetailOf({ moduleTitle: 'Latest News' }));
        openEverything();

        type('moduleTitle', '');

        expect(component['form'].controls.moduleTitle.valid)
          .withContext('an empty title carried no legacy validator and must not be refused')
          .toBeTrue();
        expect(messageFor('moduleTitle'))
          .withContext('no message may be surfaced for a field the legacy screen never validated')
          .toBeNull();

        // And it must actually reach the server, which is the difference between "not refused locally" and
        // "accepted": a form blocked by a hidden rule would issue no request for this to match.
        const request = interceptSubmission();
        request.flush({ data: moduleDetailOf({ moduleTitle: null }) });
        fixture.detectChanges();
        drainListingReread();
      });

      it('carries every falsy boolean and every zero-valued number as itself', () => {
        activate(
          moduleDetailOf({
            allTabs: false,
            displayTitle: false,
            inheritViewPermissions: false,
            isDeleted: false,
            moduleOrder: 0,
            cacheTime: 0,
            tabId: 0,
            visibility: MODULE_VISIBILITY.maximized,
          }),
        );

        const request = interceptSubmission();
        const body = request.request.body as UpdateModuleRequest;

        // `false` is the boolean sentinel AND an ordinary stored value; both must reach the server.
        expect(body.allTabs).toBeFalse();
        expect(body.displayTitle).toBeFalse();
        expect(body.inheritViewPermissions).toBeFalse();

        // 0 is an ordinary value for all four of these. `undefined` here would mean a member was stripped.
        expect(body.moduleOrder).toBe(0);
        expect(body.cacheTime).toBe(0);
        expect(body.tabId).toBe(0);
        expect(body.visibility).toBe(0);

        // And the members must be PRESENT, which is a stronger claim than merely being falsy-equal.
        for (const member of [
          'allTabs',
          'displayTitle',
          'inheritViewPermissions',
          'moduleOrder',
          'cacheTime',
          'tabId',
          'visibility',
        ]) {
          expect(member in body)
            .withContext(`${member} must be present in the body even when its value is falsy`)
            .toBeTrue();
        }

        request.flush({ data: moduleDetailOf() });
        fixture.detectChanges();
        drainListingReread();
      });

      /**
       * MIGRATION: THE VISIBILITY ORDINALS ARE LOAD-BEARING BECAUSE THE LEGACY ENUM DECLARED NO VALUES.
       * `Library/Components/Modules/ModuleInfo.vb:L30-L34` declares a three-member visibility enumeration -
       * Maximized, then Minimized, then None - with NO explicit assignments, so the compiler's implicit
       * ordinals 0, 1 and 2 are the values actually written to the column. `ModuleSettings.ascx.vb:L228`
       * confirms the base with its own comment: `cboVisibility.SelectedIndex = 0 ' maximized`. 0 therefore
       * means Maximized and is never "unset", and 2 means None - a chosen state in which the module is not
       * rendered - and is never "missing". The target names the same three codes through `ModuleVisibility`,
       * which is the only spelling used anywhere here.
       */
      it('binds the stored code 0 to Maximized, which is a choice and not an unset field', () => {
        activate(moduleDetailOf({ visibility: MODULE_VISIBILITY.maximized }));
        openEverything();

        const chosen = radios('visibility').find((radio) => radio.checked);

        expect(chosen)
          .withContext('a stored code of 0 must select a radio, not leave the group blank')
          .toBeTruthy();
        expect(chosen?.id)
          .withContext('0 is the FIRST choice, Maximized - the enum declared no explicit values')
          .toBe('module-settings-visibility-0');
      });

      it('binds the stored code 2 to None, the third choice, in which the module is not rendered', () => {
        activate(moduleDetailOf({ visibility: MODULE_VISIBILITY.none }));
        openEverything();

        expect(radios('visibility').find((radio) => radio.checked)?.id)
          .withContext('2 is None, a real presentation choice, and must select the third radio')
          .toBe('module-settings-visibility-2');
      });

      it('sends the visibility code as its number, so 0 means Maximized and 2 means None', () => {
        activate(moduleDetailOf({ visibility: MODULE_VISIBILITY.none }));

        const asRead = interceptSubmission();
        expect((asRead.request.body as UpdateModuleRequest).visibility)
          .withContext('2 is None, a real presentation choice, and must round-trip as the number 2')
          .toBe(2);
        asRead.flush({ data: moduleDetailOf({ visibility: MODULE_VISIBILITY.none }) });
        fixture.detectChanges();
        drainListingReread();
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // THE MOVE-TO-PAGE PICKER
    // ---------------------------------------------------------------------------------------------------
    //
    // MIGRATION: THE MOVE TRAVELS AS A FIELD ON THE UPDATE AND NEVER AS AN INVENTED ENDPOINT. The legacy
    //   relocation was a second call - `MoveModule(ModuleId, TabId, newTabId, "")` at
    //   `ModuleController.vb:L1078`, fired from `ModuleSettings.ascx.vb:L403-L408` after the update had
    //   returned - and no counterpart exists in the module API. A DEDICATED RELOCATION COMMAND UNDER THE
    //   MODULE RESOURCE DOES NOT EXIST, and none is expected anywhere in this file - not in an assertion and
    //   not as a literal path in a comment, so a search for one across this spec finds nothing. The closing
    //   `verify()` is what turns that from an intention into a proof, because a screen that invented such a
    //   call would leave an unmatched request behind. The page travels on the update instead.
    describe('the move-to-page picker', () => {
      it('reads the tenant page list unpaged, sending no query parameter of any kind', () => {
        fixture.componentRef.setInput('pages', null);
        fixture.componentRef.setInput('moduleId', 0);
        fixture.detectChanges();

        httpMock.expectOne(MODULE_URL).flush({ data: moduleDetailOf() });
        httpMock.expectOne(SETTINGS_URL).flush({ data: settingsBagOf() });
        fixture.detectChanges();

        httpMock.expectOne(DEFINITION_URL).flush({ data: definitionOf() });

        const tabsRequest = httpMock.expectOne(TABS_URL);

        // UNPAGED, and asserted as such: the portal-scoped page list carries no page coordinate, no
        // ordering and no filter, so any parameter at all would be a fabricated contract.
        expect(tabsRequest.request.params.keys().length)
          .withContext('the portal page list is unpaged, so no query parameter may be sent')
          .toBe(0);
        expect(tabsRequest.request.method).toBe('GET');

        tabsRequest.flush({ data: [tabOf()] });
        fixture.detectChanges();
      });

      /**
       * MIGRATION: PAGE 0 AND PARENT -1 ARE BOTH REAL AND BOTH MUST SURVIVE. `dbo.Tabs.TabID` is
       * `IDENTITY (0, 1)`, so the first page in a tenant carries the identifier 0 and must appear as a
       * selectable option rather than being filtered out by a truth test. A root-level page records -1 as
       * its parent, which collides with the legacy `Null.NullInteger` sentinel - so -1 must be carried as
       * an ordinary value here and never read as "no parent supplied".
       */
      it('offers page 0 as a selectable option and accepts a root page whose parent is -1', () => {
        activate(moduleDetailOf({ tabId: 0 }), definitionOf(), [
          tabOf({ tabId: 0, tabName: 'Home', parentId: -1, level: 0 }),
          tabOf({ tabId: 1, tabName: 'About', parentId: 0, level: 1, tabPath: '//About' }),
        ]);

        // The picker sits in a region that starts closed, and a closed region's body is REMOVED rather than
        // hidden, so it has to be opened before the options exist to be queried at all.
        openEverything();

        const options = qa<HTMLOptionElement>('select option');

        expect(options.length)
          .withContext('both pages must be offered, including the one identified by 0')
          .toBe(2);
        expect(options.map((option) => (option.textContent ?? '').trim())).toEqual([
          'Home',
          'About',
        ]);
      });

      // ---------------------------------------------------------------------------------------------
      // THE MODULE-SPECIFIC SECTION — STORED SETTINGS ARE DISCLOSED, NOT DISCARDED
      // ---------------------------------------------------------------------------------------------
      // Measured before these facts existed: the screen requested the settings bag on arrival, held the
      // response, wrote both maps back on every save — and rendered NEITHER. A module carrying two
      // module-scoped settings, one with a 750-character value, showed an empty panel. The request was
      // made, the data was in hand, and the operator was shown nothing.
      it('discloses the stored settings the module actually carries, in both scopes', () => {
        activate(
          moduleDetailOf(),
          definitionOf(),
          [tabOf()],
          settingsBagOf({
            moduleSettings: { zeta_setting: 'module value', alpha_setting: 'another' },
            tabModuleSettings: { placement_setting: 'placement value' },
          }),
        );

        openEverything();

        const names = qa<HTMLElement>('.module-settings__stored-name').map((node) =>
          (node.textContent ?? '').trim(),
        );
        const values = qa<HTMLElement>('.module-settings__stored-value').map((node) =>
          (node.textContent ?? '').trim(),
        );

        // Sorted within each scope, module-scoped first, so the order cannot depend on the order the
        // response happened to serialise its keys in.
        expect(names.length).toBe(3);
        expect(names[0]).toContain('alpha_setting');
        expect(names[1]).toContain('zeta_setting');
        expect(names[2]).toContain('placement_setting');

        expect(values).toEqual(['another', 'module value', 'placement value']);

        // Each name carries its scope, because the contract documents the two as genuinely different
        // things and collapsing them would misreport which is which.
        expect(names[0]).toContain('this module, on every page');
        expect(names[2]).toContain('this placement only');

        // And the "nothing recorded" statement is NOT shown when there is something recorded.
        expect(q('.module-settings__stored-empty')).toBeNull();
      });

      it('says so explicitly when the module carries no stored settings of its own', () => {
        activate(moduleDetailOf(), definitionOf(), [tabOf()], settingsBagOf());

        openEverything();

        // The section is still rendered. An operator being able to see that a module has no settings of
        // its own is information the legacy placeholder could not convey — it either received a control
        // or stayed silently empty, so "none" and "failed to load" looked identical.
        const empty = q('.module-settings__stored-empty');

        expect(empty).not.toBeNull();
        expect((empty?.textContent ?? '').trim()).toBe('This module has no stored settings of its own.');
        expect(qa('.module-settings__stored-name').length).toBe(0);
      });

      it('does not present a neighbouring module\'s settings under this module\'s address', () => {
        // The store is provided at the root, so it may still hold the bag read for another module. The
        // guard is an exact identity comparison and never a truth test — module 0 is a real module.
        activate(
          moduleDetailOf(),
          definitionOf(),
          [tabOf()],
          settingsBagOf({ moduleId: 99, moduleSettings: { foreign: 'not ours' } }),
        );

        openEverything();

        expect(qa('.module-settings__stored-name').length).toBe(0);
        expect(q('.module-settings__stored-empty')).not.toBeNull();
        expect(host().textContent).not.toContain('not ours');
      });

      // ---------------------------------------------------------------------------------------------
      // A BLANK HEADING IS ACCEPTED, AND ITS CONSEQUENCE IS STATED
      // ---------------------------------------------------------------------------------------------
      it('states what a module with no heading will be listed as, without refusing the blank', () => {
        activate(moduleDetailOf({ moduleTitle: '', friendlyName: 'Announcements' }));

        openEverything();

        const notice = q('.module-settings__notice');

        expect(notice).not.toBeNull();
        expect(notice?.getAttribute('aria-live')).toBe('polite');
        expect(notice?.textContent).toContain('Announcements');
        expect(notice?.textContent).toContain('listed as');

        // A STATEMENT, NOT A REFUSAL. The heading is optional on every tier — the legacy markup declares
        // no presence validator, both server validators gate their only title rule on the value being
        // non-empty, and the column is nullable — and a module in the measured data stores the empty
        // string, so a required rule here would make an existing record unsavable.
        expect(component['form'].controls.moduleTitle.valid).toBeTrue();
        expect(q('#module-settings-moduleTitle-messages')).toBeNull();
      });

      it('says nothing about the heading once one has been entered', () => {
        activate(moduleDetailOf({ moduleTitle: 'Latest News', friendlyName: 'Announcements' }));

        openEverything();

        expect(q('.module-settings__notice')).toBeNull();
      });

      // ---------------------------------------------------------------------------------------------
      // THE ICON REFERENCE MUST STAY INSIDE THE PORTAL'S OWN FOLDER
      // ---------------------------------------------------------------------------------------------
      // Measured before the rule existed: a traversal path submitted from this screen reached the API and
      // was stored VERBATIM, because the module contracts were the one path still missing the shared
      // containment rule that the role and page contracts already applied.
      it('refuses an icon reference that escapes the portal folder, and issues no request', () => {
        activate();
        openEverything();

        expect(field<HTMLInputElement>('iconFile')).not.toBeNull();

        type('iconFile', '../../../etc/passwd');
        component['form'].controls.iconFile.markAsTouched();
        fixture.detectChanges();

        expect(component['form'].controls.iconFile.valid).toBeFalse();
        expect(host().textContent).toContain("Icon File must be a relative path within the portal's own folder.");

        submit();

        // The closed-contract guard in this file's teardown would fail the spec on an outstanding
        // request, but the absence is asserted directly so the reason is unmistakable.
        httpMock.expectNone((candidate) => candidate.method === 'PUT');
      });

      it('accepts an ordinary relative icon reference', () => {
        activate();
        openEverything();

        type('iconFile', 'sub/dir/valid.gif');
        component['form'].controls.iconFile.markAsTouched();
        fixture.detectChanges();

        expect(component['form'].controls.iconFile.valid).toBeTrue();
      });

      // TWO PROPERTIES IN ONE SPEC, BOTH LOAD-BEARING. The relocation travels on the update the operator
      // already submits - there is no move endpoint to invent, and `verify()` in this file's teardown would
      // fail the spec if one were called. AND it travels as its own member: an earlier revision sent the
      // chosen page as `tabId`, which is the member the server uses to SELECT the placement being edited, so
      // choosing any page other than the module's own asked it to update a placement that does not exist. The
      // save was refused `module.placement_not_found` and the module stayed put, which is why asserting
      // `tabId` is 1 here would be asserting the bug.
      it('posts the chosen page as a relocation member of the update, not through a move endpoint', () => {
        activate(moduleDetailOf({ tabId: 0 }), definitionOf(), [
          tabOf({ tabId: 0, tabName: 'Home', parentId: -1 }),
          tabOf({ tabId: 1, tabName: 'About', parentId: 0, level: 1, tabPath: '//About' }),
        ]);

        component['form'].controls.tabId.setValue(1);
        fixture.detectChanges();

        const request = interceptSubmission();
        const body = request.request.body as UpdateModuleRequest;

        expect(body.moveToTabId)
          .withContext('the chosen page is the destination')
          .toBe(1);
        expect(body.tabId)
          .withContext('the placement being edited is still the page the module was loaded from')
          .toBe(0);

        request.flush({ data: moduleDetailOf({ tabId: 1 }) });
        fixture.detectChanges();
        drainListingReread();
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // THE DATE SENTINEL, OVER THE WIRE
    // ---------------------------------------------------------------------------------------------------
    //
    // MIGRATION: THE SENTINEL TEST IS ON THE CALENDAR DATE ALONE, WHICH IS WHAT THE LEGACY TEST WAS.
    //   `Null.vb` sets `NullDate` to `Date.MinValue` (L64-L69) and its date overload of `IsNull`
    //   (L222-L224) compares `objDate.Date.Equals(NullDate.Date)` under the source's own standing comment
    //   about avoiding "subtle time differences". A stored instant of 0001-01-01 is therefore the sentinel
    //   EVEN WITH A NON-ZERO TIME COMPONENT, and the second expectation below is the guard against a
    //   `getTime()` equality, which would match midnight and miss 13:45 on the very same day.
    describe('the date sentinel', () => {
      it('blanks the sentinel date rather than rendering year one', () => {
        activate(moduleDetailOf({ startDate: '0001-01-01T00:00:00', endDate: null }));
        openEverything();

        expect(field<HTMLInputElement>('startDate')?.value)
          .withContext('the sentinel must render as an empty control, never as 01/01/0001')
          .toBe('');
      });

      it('blanks the sentinel date even when it carries a non-zero time component', () => {
        activate(moduleDetailOf({ startDate: '0001-01-01T13:45:00', endDate: null }));
        openEverything();

        // The whole point: a timestamp equality against Date.MinValue would let this one through.
        expect(field<HTMLInputElement>('startDate')?.value)
          .withContext('the comparison is on the calendar date alone, so 13:45 on 0001-01-01 is sentinel')
          .toBe('');
      });

      it('renders 9999-12-31 normally, because the upper bound is a real stored value', () => {
        activate(moduleDetailOf({ startDate: null, endDate: '9999-12-31T00:00:00' }));
        openEverything();

        expect(field<HTMLInputElement>('endDate')?.value)
          .withContext('9999-12-31 is a real value in this schema and must not be blanked')
          .toBe('9999-12-31');
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // REMOVAL
    // ---------------------------------------------------------------------------------------------------
    //
    // MIGRATION: THE CONFIRMATION IS PARITY, NOT AN ADDITION. `ModuleSettings.ascx.vb:L205` is
    //   `ClientAPI.AddButtonConfirm(cmdDelete, Localization.GetString("DeleteItem"))`, and
    //   `DeleteItem.Text` in the 353-entry `Website/App_GlobalResources/SharedResources.resx` is 'Are You
    //   Sure You Wish To Delete This Item?'. The legacy delete was guarded by a browser confirm, so
    //   guarding it with a dialog reproduces the affordance rather than inventing one.
    //
    // MIGRATION: THE REMOVAL IS SOFT AND THERE IS NO WAY BACK FROM THIS SCREEN. The legacy handler called
    //   `DeleteTabModule(TabId, ModuleId)` (`ModuleController.vb:L837`), which detaches the placement,
    //   rather than `DeleteModule` (L819). No restore and no purge endpoint exists, so none is expected.
    // ---------------------------------------------------------------------------------------------------
    // THE TWO NON-VALIDATING ACTIONS
    // ---------------------------------------------------------------------------------------------------
    //
    // MIGRATION: ONLY ONE OF THE THREE ACTION BUTTONS EVER VALIDATED. `modulesettings.ascx:L222-L224`
    //   declares `cmdUpdate` with no `causesvalidation` attribute, so it defaulted to true, while BOTH
    //   `cmdCancel` (L223) and `cmdDelete` (L224) carry an explicit `causesvalidation="False"`. Abandoning
    //   or removing therefore worked from a form the validators would have rejected, and reproducing that
    //   means proving two things about each: that no message is raised, and that the action still happens.
    describe('the non-validating actions', () => {
      it('abandons from an invalid form without validating it or issuing a request', () => {
        activate();
        openEverything();

        // The value is assigned to the CONTROL rather than typed into the element, which is the convention
        // the validator specs above already established and it is forced by the markup: the field renders as
        // `<input type="date">`, and a date input normalises unparseable text to the empty string, which the
        // data-type check correctly passes. `2024-02-31` is the honest way to reach the invalid state - it
        // matches the expected pattern yet names a day February does not have.
        //
        // It is deliberately left UNTOUCHED. That is what makes this test discriminating: a message is
        // rendered only for a touched control, so submitting - which marks every control touched to reveal
        // all messages at once - would surface one, while abandoning must not. Marking it touched here would
        // have manufactured the very message the assertion then looks for.
        component['form'].controls.startDate.setValue('2024-02-31');
        fixture.detectChanges();

        expect(component['form'].controls.startDate.valid)
          .withContext('the precondition for this test is a form the validators would refuse')
          .toBeFalse();
        expect(messageFor('startDate'))
          .withContext('an untouched control shows nothing yet, which is this test\'s baseline')
          .toBeNull();

        let cancelled = 0;
        component.cancel.subscribe(() => (cancelled += 1));

        actionButton('Cancel')?.click();
        fixture.detectChanges();

        expect(cancelled).withContext('cancel must act, not be swallowed by validation').toBe(1);
        expect(component['form'].controls.startDate.touched)
          .withContext('causesvalidation="False" means abandoning runs no validation pass')
          .toBeFalse();
        expect(messageFor('startDate'))
          .withContext('and so no message is revealed, unlike a submission from the same state')
          .toBeNull();

        // And nothing was sent: no write may escape an abandonment.
        httpMock.expectNone(
          (candidate) => candidate.method === 'PUT' || candidate.method === 'DELETE',
        );
      });
    });

    describe('removal', () => {
      it('opens the confirmation before issuing anything, and issues nothing until it is confirmed', () => {
        activate();
        fixture.componentRef.setInput('canDelete', true);
        fixture.detectChanges();

        // An invalid form must not block the destructive path: `cmdDelete` carried
        // `causesvalidation="False"` at `modulesettings.ascx:L224`.
        openEverything();
        type('startDate', 'not-a-date');

        actionButton('Delete')?.click();
        fixture.detectChanges();

        expect(q('app-confirm-dialog'))
          .withContext('the confirmation must be presented before the write')
          .not.toBeNull();

        // The proof that nothing was issued: an unexpected request here would fail this expectation, and
        // an unmatched one would fail the closing verify().
        httpMock.expectNone((candidate) => candidate.method === 'DELETE');
      });

      it('issues exactly one DELETE once confirmed, and reads the 204 as success', () => {
        activate();
        fixture.componentRef.setInput('canDelete', true);
        fixture.detectChanges();

        actionButton('Delete')?.click();
        fixture.detectChanges();

        confirmDialog();

        const request = httpMock.expectOne(
          (candidate) => candidate.method === 'DELETE' && candidate.url === MODULE_URL,
        );

        // 204 with NO BODY, which is what the endpoint publishes. Flushing a payload here would test a
        // contract the server does not implement.
        request.flush(null, { status: 204, statusText: 'No Content' });
        fixture.detectChanges();

        expect(notify).toHaveBeenCalledWith('success', jasmine.any(String), null, true);

        // MIGRATION: THE DELETED MODULE IS NOT RE-READ, and this is the assertion that proves it. The
        // store does re-read the LISTING afterwards - the placement is detached rather than destroyed, so
        // only the listing knows whether the row should still appear - but re-reading the module that was
        // just removed would be a contract breach, and there is no restore endpoint to make it meaningful.
        httpMock.expectNone((candidate) => candidate.method === 'GET' && candidate.url === MODULE_URL);
        drainListingReread();
      });

      it('issues nothing when the confirmation is abandoned', () => {
        activate();
        fixture.componentRef.setInput('canDelete', true);
        fixture.detectChanges();

        actionButton('Delete')?.click();
        fixture.detectChanges();

        cancelDialog();

        httpMock.expectNone((candidate) => candidate.method === 'DELETE');
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // REFUSALS AND REJECTIONS
    // ---------------------------------------------------------------------------------------------------
    describe('refusals and rejections', () => {
      /**
       * MIGRATION: A REFUSAL IS AN ADVISORY AT WARNING SEVERITY, NOT AN ERROR, AND THE LEGACY SCREEN IS
       * UNAMBIGUOUS ABOUT IT. `Website/admin/Security/AccessDenied.ascx.vb` is fifty lines, performs no
       * permission check of its own, and its `Page_Load` (L41-L47) has exactly two branches - one for a
       * supplied message and one for the default wording - BOTH of which raise
       * `ModuleMessage.ModuleMessageType.YellowWarning`. Neither raises `RedError`, though the vocabulary
       * offered it and the tree uses it 27 times elsewhere. So a 403 is presented as a warning and is
       * deliberately NOT dressed as a danger.
       *
       * ⚠ THE SURFACE IS THE IN-PAGE BANNER, AND THAT IS A CORRECTION THIS SPEC USED TO PIN THE WRONG WAY
       * ROUND. It asserted the notification queue, on the same YellowWarning evidence. The severity
       * reading was right and the surface was wrong: `UI.Skins.Skin.AddModuleMessage` inserted the message
       * INTO the page, and the shared banner is what ports that - it resolves 403 to the warning band
       * through `core/utils/form-errors.util.ts`, so the legacy severity survives either way, while the
       * document's trace identifier, its title and its permanence do not survive a transient advisory.
       * The sibling module form and export screens present the same status through the same banner, which
       * is what gives one backend condition one presentation across the feature.
       */
      it('presents a refused write in the banner at warning severity, never as an error', () => {
        activate();

        const request = interceptSubmission();

        request.flush(
          {
            type: 'about:blank',
            title: 'Forbidden',
            status: 403,
            detail: 'This module appears on every page and cannot be moved.',
            traceId: '00-9f2c4d1b7a3e-01',
          },
          { status: 403, statusText: 'Forbidden' },
        );
        fixture.detectChanges();

        // The band is the legacy severity, resolved by the shared utility rather than chosen here.
        const banner: HTMLElement | null = fixture.nativeElement.querySelector('.error-banner');

        expect(banner).not.toBeNull();
        expect(banner?.getAttribute('data-severity')).toBe('warning');
        expect(fixture.nativeElement.textContent).toContain(
          'This module appears on every page and cannot be moved.',
        );

        // The three things the discarded document used to cost. The trace identifier above all: it is the
        // only join key between what the operator saw and what the server logged.
        expect(fixture.nativeElement.textContent).toContain('00-9f2c4d1b7a3e-01');
        expect(fixture.nativeElement.textContent).toContain('Forbidden');

        expect(notify).not.toHaveBeenCalledWith('error', jasmine.any(String));
        expect(notify).not.toHaveBeenCalledWith('success', jasmine.any(String), null, true);
      });

      /**
       * A REFUSAL WITH AN EMPTY BODY STILL REACHES THE BANNER, which is why the component needs no
       * null-document fallback for this status. `problemFromCause` in `core/state/module.store.ts`
       * synthesises `{ status: 403 }` from the status alone when neither a document nor a parsable text
       * body is present, expressly so that severity and wording still resolve - so the band is still the
       * legacy warning and the shared sentence for the status is still shown.
       */
      it('presents a refusal that carried no document at all, from its status alone', () => {
        activate();

        const request = interceptSubmission();

        request.flush(null, { status: 403, statusText: 'Forbidden' });
        fixture.detectChanges();

        const banner: HTMLElement | null = fixture.nativeElement.querySelector('.error-banner');

        expect(banner).not.toBeNull();
        expect(banner?.getAttribute('data-severity')).toBe('warning');
        expect(notify).not.toHaveBeenCalledWith('error', jasmine.any(String));
      });

      it('announces a settled write at success severity', () => {
        activate();

        const request = interceptSubmission();
        request.flush({ data: moduleDetailOf() });
        fixture.detectChanges();

        expect(notify).toHaveBeenCalledWith('success', jasmine.any(String), null, true);
        drainListingReread();
      });

      /**
       * MIGRATION: THE PER-FIELD DICTIONARY IS READ WITH BRACKET ACCESS, WHICH THE COMPILER ENFORCES.
       * `ValidationProblemDetails.errors` is an index signature and `noPropertyAccessFromIndexSignature`
       * is enabled, so `problem.errors.ModuleTitle` would not compile at all. The keys are .NET
       * `ModelStateDictionary` keys and are NOT camel-cased, which is why the fixture below spells
       * `ModuleTitle` with a leading capital exactly as the server publishes it.
       */
      it('lands a rejected field on its own control, keyed as the server spelled it', () => {
        activate();

        // `detail` and `instance` are deliberately OMITTED rather than set to null. Unlike the DTO
        // contracts, whose members the API writes unconditionally, the problem-details members are declared
        // optional - `readonly detail?: string` - because RFC 7807 makes them optional and the model
        // mirrors the specification. Assigning null would not type-check, which is the model doing its job.
        const problem: ValidationProblemDetails = {
          type: 'about:blank',
          title: 'One or more validation errors occurred.',
          status: 400,
          traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
          errors: {
            // Bracket-written on purpose; a dotted read would be a compile error under the strict flags.
            ['ModuleTitle']: ['The title could not be applied.'],
          },
        };

        const request = interceptSubmission();
        request.flush(problem, { status: 400, statusText: 'Bad Request' });
        fixture.detectChanges();

        expect(problem.errors['ModuleTitle'])
          .withContext('the dictionary is read with brackets, as the strict flags require')
          .toEqual(['The title could not be applied.']);

        openEverything();
        expect(messageFor('moduleTitle')).toBe('The title could not be applied.');
      });

      it('offers a not-found affordance and stays standing when the address resolves to nothing', () => {
        expect(() => {
          fixture.componentRef.setInput('moduleId', 0);
          fixture.detectChanges();

          httpMock
            .expectOne(MODULE_URL)
            .flush(
              { type: 'about:blank', title: 'Not Found', status: 404 },
              { status: 404, statusText: 'Not Found' },
            );
          httpMock
            .expectOne(SETTINGS_URL)
            .flush(
              { type: 'about:blank', title: 'Not Found', status: 404 },
              { status: 404, statusText: 'Not Found' },
            );
          fixture.detectChanges();
        }).not.toThrow();

        // A stale bookmark is a legitimate state, so the screen explains itself rather than spinning or
        // rendering a form over nothing.
        expect(q('app-empty-state'))
          .withContext('an unresolved address must produce an affordance, not a crash')
          .not.toBeNull();
        expect(q('form.module-settings')).toBeNull();
      });
    });

    // ---------------------------------------------------------------------------------------------------
    // THE FOURTH VALIDATOR
    // ---------------------------------------------------------------------------------------------------

    /**
     * MIGRATION: THE FOURTH DATA-TYPE CHECK IS PRESERVED AS A RULE THOUGH IT HAS NO TRANSPORTABLE CONTROL.
     * A case-insensitive census of `Website/admin/Modules/` returns `asp:RequiredFieldValidator` 0,
     * `asp:RegularExpressionValidator` 0, `asp:CompareValidator` 4, `asp:CustomValidator` 0,
     * `asp:RangeValidator` 0 and `asp:ValidationSummary` 0 - so four `CompareValidator`s were the entire
     * validation surface of the feature. Three of the four guard controls that still exist and are
     * asserted above: `valtxtStartDate` (L78), `valtxtEndDate` (L88) and `valCacheTime` (L172). The
     * fourth, `valBorder` (L138), guarded `txtBorder`, and `border` is one of six placement columns
     * `UpdateModuleRequest` does not project - so there is no input to reject and no member to send. Its
     * wording is nevertheless carried verbatim, break tag stripped, so that the rule is not lost and the
     * control can be restored without re-deriving it the moment the server projects the column.
     *
     * Note the legacy inconsistency reproduced rather than corrected: the wording promises a range of 0
     * to 9 while the declared validator was a plain integer data-type check that enforced no range at all.
     */
    it('preserves the fourth data-type check as wording, its control having no wire contract', () => {
      expect(component['borderInvalidMessage'])
        .withContext('valBorder.ErrorMessage, verbatim, without the layout break tag it carries')
        .toBe('Invalid Border (must be a number between 0 and 9)');

      // And there is genuinely no control to guard, which is the contract gap rather than an oversight.
      setInput('settings', moduleOf());
      openEverything();
      expect(field('border')).toBeNull();
      expect(field('alignment')).toBeNull();
      expect(field('color')).toBeNull();
    });
  });
});
