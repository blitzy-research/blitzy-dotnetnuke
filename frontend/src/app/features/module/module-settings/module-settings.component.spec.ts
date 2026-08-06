import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';

import { ModuleSettingsComponent } from './module-settings.component';
import { MODULE_VISIBILITY, type UpdateModuleRequest } from '../../../core/models/module.model';
import { NotificationService } from '../../../core/services/notification.service';
import type { ModuleSettingsViewModel } from './module-settings.view-model';

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
function moduleOf(overrides: Partial<ModuleSettingsViewModel> = {}): ModuleSettingsViewModel {
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

describe('ModuleSettingsComponent', () => {
  let fixture: ComponentFixture<ModuleSettingsComponent>;
  let component: ModuleSettingsComponent;

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
    // Non-null asserted on the argument, guarded by the expectation immediately above.
    if (head!.getAttribute('aria-expanded') === 'false') {
      head!.click();
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
   * A field's rendered help text.
   *
   * @param fieldName The field name.
   * @returns The trimmed text, or `null` when the hint is not rendered.
   */
  function hintFor(fieldName: string): string | null {
    const element = q(`#module-settings-${fieldName}-hint`);
    return element === null ? null : (element.textContent ?? '').trim();
  }

  /**
   * A field's rendered validation message.
   *
   * @param fieldName The field name.
   * @returns The trimmed text, or `null` when no message is rendered.
   */
  function messageFor(fieldName: string): string | null {
    const element = q(`#module-settings-${fieldName}-message`);
    return element === null ? null : (element.textContent ?? '').trim();
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
    // Non-null asserted on the argument, guarded by the expectation immediately above.
    element!.value = value;
    element!.dispatchEvent(new Event('input'));
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
    // Non-null asserted on the argument, guarded by the expectation immediately above.
    form!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  beforeEach(async () => {
    // The screen orchestrates its own reads and writes through `core/state/module.store.ts`, which reaches
    // `ModuleService` and `TabService` and therefore `HttpClient`. The testing backend is supplied so the
    // graph resolves and NO REQUEST REACHES A NETWORK: every call the component issues is parked on the
    // controller and simply never answered, which leaves the store's own signals at their initial values and
    // lets these specs drive the screen through its inputs exactly as before. `provideRouter([])` satisfies
    // the return-to-listing navigation the cancel and delete paths perform.
    await TestBed.configureTestingModule({
      imports: [ModuleSettingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(ModuleSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('pages', [
      { value: 7, label: 'Home' },
      { value: 8, label: 'About' },
    ]);
    fixture.detectChanges();
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
    });
  });

  describe('shell states', () => {
    it('renders the shared page header outside the loading branch, so the screen is never untitled', () => {
      setInput('loading', true);

      const header = q('app-page-header h1');
      expect(header).not.toBeNull();
      expect((header!.textContent ?? '').trim()).toBe('Module Settings');
    });

    it('takes a supplied heading in place of the legacy module definition name', () => {
      setInput('heading', 'Announcements Settings');

      expect((q('app-page-header h1')!.textContent ?? '').trim()).toBe('Announcements Settings');
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
      expect((q('app-empty-state')!.textContent ?? '').trim()).toContain('No module settings are available.');
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

    it('removes a closed region body rather than hiding it', () => {
      // .module-settings__body declares display:grid, and the user-agent [hidden] rule loses to any author
      // rule, so a hidden body would still be visible. Removal is the only correct collapse.
      expect(q('#module-settings-header')).toBeNull();

      open('security');

      expect(q('#module-settings-header')).not.toBeNull();
      expect(qa('.module-settings__body[hidden]').length).toBe(0);
    });

    it('reports its state through aria-expanded and offers no aria-controls', () => {
      const head = toggleFor('pageSettings')!;
      expect(head.getAttribute('aria-expanded')).toBe('false');

      head.click();
      fixture.detectChanges();

      expect(toggleFor('pageSettings')!.getAttribute('aria-expanded')).toBe('true');
      // aria-controls would point outside the document while the region is closed, which is worse than
      // omitting it. aria-expanded alone is a complete disclosure pattern.
      expect(toggleFor('pageSettings')!.hasAttribute('aria-controls')).toBeFalse();
    });

    it('closes an open region again', () => {
      open('pageSettings');
      expect(toggleFor('appearance')).not.toBeNull();

      toggleFor('pageSettings')!.click();
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
      expect(field<HTMLInputElement>('moduleTitle')!.value).toBe('Latest News');
      expect(field<HTMLTextAreaElement>('header')!.value).toBe('Header markup');
      expect(field<HTMLTextAreaElement>('footer')!.value).toBe('Footer markup');
      expect(field<HTMLInputElement>('iconFile')!.value).toBe('module.gif');
      expect(field<HTMLInputElement>('cacheTime')!.value).toBe('120');
    });

    it('seeds visibility from the stored numeric code', () => {
      const chosen = radios('visibility').find((radio) => radio.checked);
      expect(chosen).toBeTruthy();
      expect(chosen!.id).toBe('module-settings-visibility-1', 'Minimized is the second choice');
    });

    it('seeds the two boolean placement switches', () => {
      expect(field<HTMLInputElement>('displayTitle')!.checked).toBeFalse();
      expect(field<HTMLInputElement>('inheritViewPermissions')!.checked).toBeFalse();
      expect(field<HTMLInputElement>('allTabs')!.checked).toBeTrue();
    });

    it('narrows an instant to a date by slicing, so the shown day cannot shift with the viewer zone', () => {
      // Parsing the instant and formatting it locally would move a midnight-UTC date by a day in any zone
      // behind UTC, so a module scheduled for the first would be shown as the last day of the month before.
      expect(field<HTMLInputElement>('startDate')!.value).toBe('2024-03-01');
      expect(field<HTMLInputElement>('endDate')!.value).toBe('2024-12-31');
    });

    it('leaves the two intent flags unset rather than echoing a previous instruction', () => {
      // Neither is a column on the module: each describes work the server performs after the update, so
      // echoing one back onto the form would silently reapply it on the next submission.
      expect(field<HTMLInputElement>('setAsDefaultSettings')!.checked).toBeFalse();
      expect(field<HTMLInputElement>('applyToAllModules')!.checked).toBeFalse();
    });

    it('shows absent stored values as empty controls and a missing cache period as zero', () => {
      setInput('settings', moduleOf({ moduleTitle: null, iconFile: null, cacheTime: null }));
      openEverything();

      expect(field<HTMLInputElement>('moduleTitle')!.value).toBe('');
      expect(field<HTMLInputElement>('iconFile')!.value).toBe('');
      expect(field<HTMLInputElement>('cacheTime')!.value).toBe('0');
    });

    it('re-seeds when a different module is supplied', () => {
      setInput('settings', moduleOf({ moduleTitle: 'Replaced', iconFile: 'replacement.gif' }));
      openEverything();

      expect(field<HTMLInputElement>('moduleTitle')!.value).toBe('Replaced');
      expect(field<HTMLInputElement>('iconFile')!.value).toBe('replacement.gif');
    });

    it('leaves the operator\'s open regions open across a re-seed', () => {
      // Re-seeding replaces the values, not the disclosure state. Snapping every region shut whenever the
      // data refreshed would throw the operator back to the top of a seven-region form mid-edit.
      expect(toggleFor('other')).not.toBeNull();

      setInput('settings', moduleOf({ moduleTitle: 'Refreshed' }));

      expect(toggleFor('other')).withContext('a nested open region must stay open').not.toBeNull();
      expect(toggleFor('pageSettings')!.getAttribute('aria-expanded')).toBe('true');
      expect(field<HTMLInputElement>('moduleTitle')!.value).toBe('Refreshed');
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
      expect((label!.textContent ?? '')).toContain('Display Container?');
      expect((label!.textContent ?? '')).not.toContain('Display Title?');
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
      expect(hintFor('permissions')).toBe(
        'Select the View and Edit permissions by checking/unchecking the boxes in the grid.  The module can '
          + 'inherit its permissions from the Page.  To do this check the Inherit View Permissions checkbox.',
      );
    });

    it('preserves the space the legacy resource carries before its closing parenthesis', () => {
      expect(qa('.module-settings__intro')[0].textContent!.trim()).toBe(
        'In this section, you can define the settings that relate to the Module content and permissions '
          + '(ie. those settings that will be the same on all pages that the Module appears ).',
      );
    });

    it('renders the inherit label as plain text, losing the legacy embedded bold', () => {
      const label = q<HTMLLabelElement>('label[for="module-settings-inheritViewPermissions"]');
      expect((label!.textContent ?? '').trim()).toBe('Inherit View permissions from Page');
      expect(label!.querySelector('b')).withContext('an interpolated value is not parsed as markup').toBeNull();
    });

    it('renders every declared hint exactly as declared, across every region', () => {
      // The sweep. Each hint element's identifier names the field it belongs to, so the rendered text is
      // compared against the declaration rather than against a copy retyped into this test.
      const declared = component['hints'] as Record<string, string>;
      let checked = 0;

      for (const element of qa('.form-help')) {
        const key = element.id.replace(/^module-settings-/, '').replace(/-hint$/, '');
        expect(declared[key]).withContext(`${element.id} must correspond to a declared hint`).toBeDefined();
        expect((element.textContent ?? '').trim()).toBe(declared[key]);
        checked += 1;
      }

      // Fifteen of the sixteen declared hints are rendered: the permissions hint serves both the region and
      // its inherit switch, so inheritViewPermissions has no hint element of its own.
      expect(checked).toBe(15);
      expect(Object.keys(declared).length).toBe(16);
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
      expect(field<HTMLInputElement>('moduleTitle')!.getAttribute('maxlength')).toBe('256');
      expect(field<HTMLInputElement>('iconFile')!.getAttribute('maxlength')).toBe('100');
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

      expect(field<HTMLSelectElement>('tabId')!.disabled).toBeTrue();
      expect(field<HTMLInputElement>('allTabs')!.disabled).toBeTrue();
      expect(field<HTMLInputElement>('setAsDefaultSettings')!.disabled).toBeTrue();
      expect(field<HTMLInputElement>('applyToAllModules')!.disabled).toBeTrue();
    });

    it('releases them for a portal administrator', () => {
      setInput('canManageAllPages', true);
      openEverything();

      expect(field<HTMLSelectElement>('tabId')!.disabled).toBeFalse();
      expect(field<HTMLInputElement>('allTabs')!.disabled).toBeFalse();
      expect(field<HTMLInputElement>('setAsDefaultSettings')!.disabled).toBeFalse();
      expect(field<HTMLInputElement>('applyToAllModules')!.disabled).toBeFalse();
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

      expect(field<HTMLInputElement>('allTabs')!.disabled).toBeTrue();
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
      //   `displaySyndicate` are absent from ModuleSettingsViewModel and from the template, because none of
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

      field<HTMLInputElement>('setAsDefaultSettings')!.click();
      fixture.detectChanges();
      submit();

      expect(emitted[0].setAsDefaultSettings).toBeTrue();
    });

    it('carries an operator instruction under the member the server reads', () => {
      // The control is still named for the legacy check box; the wire member is applyToAllModules. The
      // superseded isDefaultModule and allModules spellings are gone from the contract entirely.
      const emitted: UpdateModuleRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      field<HTMLInputElement>('applyToAllModules')!.click();
      fixture.detectChanges();
      submit();

      expect(emitted[0].applyToAllModules).toBeTrue();
    });

    it('disables the submit affordance while a submission is in flight', () => {
      setInput('saving', true);

      expect(actionButton('Update')!.disabled).toBeTrue();
    });
  });

  describe('submission conclusion', () => {
    /**
     * MIGRATION: the legacy redirect at `ModuleSettings.ascx.vb:L421` -
     * `Response.Redirect(NavigateURL(), True)`, commented "Navigate back to admin page" - sat INSIDE the
     * `If Page.IsValid Then` / `Try` block AFTER `UpdateModule` had returned, so a postback that threw fell
     * through to `Catch` and never redirected. These two specs pin both halves of that: a settled write leaves
     * for the listing and announces, a rejected one stays put so the per-field messages remain reachable.
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

    it('announces and returns to the listing once both writes have settled', () => {
      submit();

      // Nothing is claimed while either write is still outstanding. The legacy redirect ran after the update
      // had returned, never before it, so an optimistic announcement would not be the same behaviour.
      expect(notify).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();

      expect(answer('PUT', '/api/v1/modules/0', ECHO)).toBe(1);

      // Still outstanding: the settings bag. One settled write is not a settled submission.
      expect(navigate).not.toHaveBeenCalled();

      expect(answer('PUT', '/api/v1/modules/0/settings', null)).toBe(1);

      expect(notify).toHaveBeenCalledWith('success', jasmine.any(String));
      expect(navigate).toHaveBeenCalledWith(['/modules']);

      drain();
    });

    it('stays on the screen when a write is rejected, so the messages remain reachable', () => {
      submit();

      for (const request of http.match((candidate) => candidate.method === 'PUT')) {
        request.flush(
          { type: 'about:blank', title: 'Bad Request', status: 400, detail: null, errors: {} },
          { status: 400, statusText: 'Bad Request' },
        );
      }

      fixture.detectChanges();

      expect(navigate).not.toHaveBeenCalled();
      expect(notify).not.toHaveBeenCalledWith('success', jasmine.any(String));

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

      actionButton('Cancel')!.click();
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

      actionButton('Delete')!.click();
      fixture.detectChanges();

      const dialog = q('app-confirm-dialog');
      expect(dialog).not.toBeNull();
      expect(dialog!.textContent).toContain('Latest News');
    });

    it('falls back to the definition name when the module carries no title', () => {
      setInput('canDelete', true);
      setInput('settings', moduleOf({ moduleTitle: null }));

      actionButton('Delete')!.click();
      fixture.detectChanges();

      expect(q('app-confirm-dialog')!.textContent).toContain('Announcements');
    });

    it('emits nothing when the confirmation is abandoned, and closes it', () => {
      setInput('canDelete', true);
      let removed = 0;
      component.remove.subscribe(() => (removed += 1));

      actionButton('Delete')!.click();
      fixture.detectChanges();

      const cancelButton = qa<HTMLButtonElement>('.confirm-dialog__button').find(
        (button) => (button.textContent ?? '').trim() === 'Cancel',
      );
      expect(cancelButton).toBeTruthy();
      cancelButton!.click();
      fixture.detectChanges();

      expect(removed).toBe(0);
      expect(q('app-confirm-dialog')).toBeNull();
    });

    it('emits the removal once confirmed and closes the dialog', () => {
      setInput('canDelete', true);
      let removed = 0;
      component.remove.subscribe(() => (removed += 1));

      actionButton('Delete')!.click();
      fixture.detectChanges();

      // The shared dialog prefixes a warning glyph to a dangerous confirm label, so the button's text is not
      // the bare label. It is located by its danger class and matched on its ending.
      const confirmButton = q<HTMLButtonElement>('.confirm-dialog__button--danger');
      expect(confirmButton).not.toBeNull();
      expect((confirmButton!.textContent ?? '').trim().endsWith('Delete')).toBeTrue();

      confirmButton!.click();
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
      expect(Array.from(selector!.options).map((option) => option.textContent?.trim())).toEqual(['Home', 'About']);
      expect(host().textContent).toContain('Move To Page');
    });

    it('offers a plain multi-line control rather than a rich text editor', () => {
      const header = field<HTMLTextAreaElement>('header');
      expect(header).not.toBeNull();
      expect(header!.tagName.toLowerCase()).toBe('textarea');
      expect(header!.getAttribute('rows')).toBe('6', 'the legacy control declared rows="6"');
    });

    it('names the visibility choice group with a label that claims no control of its own', () => {
      // for is optional on a label. Pointing it at the first radio would name the group by side effect and
      // make clicking its title select an option.
      const label = q<HTMLLabelElement>('#module-settings-visibility-label');
      expect(label).withContext('visibility must have a visible name').not.toBeNull();
      expect(label!.hasAttribute('for')).toBeFalse();

      const group = q('[role="radiogroup"][aria-labelledby="module-settings-visibility-label"]');
      expect(group).withContext('visibility must be a named radiogroup').not.toBeNull();
    });

    it('does not render legacy appearance controls that have no read or write contract', () => {
      for (const name of ['paneName', 'alignment', 'color', 'border', 'displayPrint', 'displaySyndicate']) {
        expect(field(name)).withContext(`${name} would submit an unmapped member and receive 400`).toBeNull();
      }
    });

    it('describes every control by its own hint', () => {
      for (const name of ['moduleTitle', 'cacheTime', 'iconFile', 'header', 'footer']) {
        const control = field<HTMLElement>(name);
        expect(control).withContext(`${name} must be rendered`).not.toBeNull();
        expect(control!.getAttribute('aria-describedby')).toBe(`module-settings-${name}-hint`);
      }
    });
  });

  it('declares no router link, because it is a leaf screen reached by route', () => {
    setInput('settings', moduleOf());

    expect(fixture.debugElement.queryAll(By.css('a[routerLink]')).length).toBe(0);
  });
});
