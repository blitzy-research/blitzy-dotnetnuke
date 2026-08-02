import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  BANNER_ADVERTISING_MODE,
  USER_REGISTRATION_MODE,
} from '../../../core/models/portal.model';
import type {
  PortalSettings,
  PortalSettingsLookups,
  UpdatePortalRequest,
} from '../../../core/models/portal.model';
import { PortalSettingsComponent } from './portal-settings.component';

/**
 * Builds a fully-populated settings snapshot.
 *
 * Every field carries a distinct, recognisable value so a submission assertion
 * proves the right value reached the right property rather than merely proving
 * that something reached it. Two values are deliberately awkward: the description
 * is surrounded by whitespace, to prove trimming, and the page quota is zero, to
 * prove a genuine zero survives instead of being read as "absent".
 */
function settingsOf(overrides: Partial<PortalSettings> = {}): PortalSettings {
  return {
    portalId: 7,
    portalName: 'Contoso',
    description: '  A site about widgets  ',
    keyWords: 'widgets,gadgets',
    footerText: 'Copyright Contoso',
    logoFile: 'logo.gif',
    backgroundFile: 'bg.gif',
    expiryDate: '2027-03-01T00:00:00',
    userRegistration: USER_REGISTRATION_MODE.public,
    bannerAdvertising: BANNER_ADVERTISING_MODE.site,
    currency: 'GBP',
    administratorId: 42,
    hostFee: 19.5,
    hostSpace: 512,
    pageQuota: 0,
    userQuota: 250,
    paymentProcessor: 'PayPal',
    processorUserId: 'merchant-1',
    siteLogHistory: 30,
    splashTabId: 11,
    homeTabId: 12,
    loginTabId: 13,
    userTabId: 14,
    defaultLanguage: 'en-GB',
    timeZoneOffset: -300,
    homeDirectory: 'Portals/7',
    guid: '5f1b7c9e-0000-4000-8000-000000000001',
    ...overrides,
  };
}

/** Lookup lists that cover every held value in {@link settingsOf}. */
function lookupsOf(overrides: Partial<PortalSettingsLookups> = {}): PortalSettingsLookups {
  return {
    pages: [
      { value: 11, label: 'Splash' },
      { value: 12, label: 'Home' },
      { value: 13, label: 'Login' },
      { value: 14, label: 'Account' },
    ],
    administrators: [
      { value: 42, label: 'Grace Hopper' },
      { value: 43, label: 'Ada Lovelace' },
    ],
    currencies: [
      { value: 'GBP', label: 'Pound Sterling' },
      { value: 'USD', label: 'US Dollar' },
    ],
    paymentProcessors: [
      { value: 'PayPal', label: 'PayPal' },
      { value: 'WorldPay', label: 'WorldPay' },
    ],
    languages: [
      { value: 'en-GB', label: 'English (United Kingdom)' },
      { value: 'fr-FR', label: 'French (France)' },
    ],
    timeZones: [
      { value: -300, label: 'Eastern Time' },
      { value: 0, label: 'UTC' },
    ],
    ...overrides,
  };
}

describe('PortalSettingsComponent', () => {
  let fixture: ComponentFixture<PortalSettingsComponent>;
  let component: PortalSettingsComponent;

  /**
   * Assigns an input the way a template binding would.
   *
   * `componentRef.setInput` marks the component dirty and runs the declared input
   * transform. Assigning the field directly does neither, so an `OnPush`
   * component would not re-render and a `booleanAttribute` transform would never
   * run — which is the difference between testing the component and testing a
   * field.
   */
  function setInput(
    name:
      | 'settings'
      | 'lookups'
      | 'heading'
      | 'loading'
      | 'saving'
      | 'canEditHostFields'
      | 'canDelete',
    value: unknown,
  ): void {
    fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
  }

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function q<T extends HTMLElement>(selector: string): T | null {
    return host().querySelector<T>(selector);
  }

  function qa<T extends HTMLElement>(selector: string): T[] {
    return Array.from(host().querySelectorAll<T>(selector));
  }

  function tabs(): HTMLButtonElement[] {
    return qa<HTMLButtonElement>('.portal-settings__tab');
  }

  function panel(): HTMLElement | null {
    return q<HTMLElement>('.portal-settings__panel');
  }

  function toggles(): HTMLButtonElement[] {
    return qa<HTMLButtonElement>('.portal-settings__toggle');
  }

  function toggleFor(label: string): HTMLButtonElement | undefined {
    return toggles().find((button) => (button.textContent ?? '').trim() === label);
  }

  function field<T extends HTMLElement>(name: string): T | null {
    return q<T>(`#portal-settings-${name}`);
  }

  function messageFor(name: string): HTMLElement | null {
    return q<HTMLElement>(`#portal-settings-${name}-message`);
  }

  function hintFor(name: string): HTMLElement | null {
    return q<HTMLElement>(`#portal-settings-${name}-hint`);
  }

  /** Types into a text or number control and notifies the form. */
  function type(name: string, value: string): void {
    const input = field<HTMLInputElement | HTMLTextAreaElement>(name);
    expect(input).withContext(`control ${name} should be rendered`).not.toBeNull();
    input!.value = value;
    input!.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * Chooses an option by index and notifies the form.
   *
   * Index rather than value, because `[ngValue]` writes opaque `"0: 11"` style
   * option values — the whole point of it being that the real value never becomes
   * a string.
   */
  function choose(name: string, index: number): void {
    const select = field<HTMLSelectElement>(name);
    expect(select).withContext(`select ${name} should be rendered`).not.toBeNull();
    select!.selectedIndex = index;
    select!.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function radios(name: string): HTMLInputElement[] {
    return qa<HTMLInputElement>(`input[type="radio"][name="${name}"]`);
  }

  function actionButton(label: string): HTMLButtonElement | undefined {
    return qa<HTMLButtonElement>('.portal-settings__actions button').find(
      (button) => (button.textContent ?? '').trim() === label,
    );
  }

  function submit(): void {
    const button = actionButton('Update');
    expect(button).withContext('an Update button should be rendered').not.toBeUndefined();
    button!.click();
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PortalSettingsComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(PortalSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('creates', () => {
    expect(component).toBeTruthy();
  });

  describe('screen title', () => {
    it('renders the legacy module title by default', () => {
      const title = q<HTMLElement>('.page-header__title');
      expect(title).not.toBeNull();
      expect((title!.textContent ?? '').trim()).toBe('Site Settings');
    });

    it('renders a supplied heading instead', () => {
      setInput('heading', 'Contoso Settings');

      const title = q<HTMLElement>('.page-header__title');
      expect((title!.textContent ?? '').trim()).toBe('Contoso Settings');
    });

    it('keeps the title on screen while the settings are being fetched', () => {
      setInput('loading', true);

      expect(q('.page-header__title')).not.toBeNull();
      expect(q('app-loading-spinner')).not.toBeNull();
    });
  });

  describe('before any settings arrive', () => {
    it('shows the empty state and no form', () => {
      expect(q('app-empty-state')).not.toBeNull();
      expect(q('form')).toBeNull();
    });

    it('shows the fetching affordance in preference to the empty state', () => {
      setInput('loading', true);

      expect(q('app-loading-spinner')).not.toBeNull();
      expect(q('app-empty-state')).toBeNull();
    });
  });

  describe('tab strip', () => {
    beforeEach(() => {
      setInput('settings', settingsOf());
    });

    it('offers exactly the two surviving groups, in legacy order', () => {
      const labels = tabs().map((button) => (button.textContent ?? '').trim());
      expect(labels).toEqual(['Basic Settings', 'Advanced Settings']);
    });

    it('marks each tab with the tab role inside a tablist', () => {
      expect(q('.portal-settings__tablist')!.getAttribute('role')).toBe('tablist');
      for (const button of tabs()) {
        expect(button.getAttribute('role')).toBe('tab');
      }
    });

    it('selects the basic group first', () => {
      const [basic, advanced] = tabs();
      expect(basic.getAttribute('aria-selected')).toBe('true');
      expect(advanced.getAttribute('aria-selected')).toBe('false');
    });

    it('keeps only the selected tab in the tab order', () => {
      const [basic, advanced] = tabs();
      expect(basic.getAttribute('tabindex')).toBe('0');
      expect(advanced.getAttribute('tabindex')).toBe('-1');
    });

    it('points every tab at the single panel, so no reference can dangle', () => {
      const panelId = panel()!.id;
      expect(panelId.length).toBeGreaterThan(0);
      for (const button of tabs()) {
        expect(button.getAttribute('aria-controls')).toBe(panelId);
        expect(host().querySelector(`#${button.getAttribute('aria-controls')}`)).not.toBeNull();
      }
    });

    it('labels the panel by whichever tab is selected', () => {
      const [basic, advanced] = tabs();
      expect(panel()!.getAttribute('aria-labelledby')).toBe(basic.id);

      advanced.click();
      fixture.detectChanges();

      expect(panel()!.getAttribute('aria-labelledby')).toBe(advanced.id);
    });

    it('switches the panel contents when a tab is clicked', () => {
      expect(toggleFor('Site Details')).not.toBeUndefined();
      expect(toggleFor('Security Settings')).toBeUndefined();

      tabs()[1].click();
      fixture.detectChanges();

      expect(toggleFor('Site Details')).toBeUndefined();
      expect(toggleFor('Security Settings')).not.toBeUndefined();
    });

    it('renders exactly one panel region at a time', () => {
      expect(qa('.portal-settings__panel').length).toBe(1);

      tabs()[1].click();
      fixture.detectChanges();

      expect(qa('.portal-settings__panel').length).toBe(1);
    });

    it('shows each group its own introduction', () => {
      expect((q('.portal-settings__intro')!.textContent ?? '').trim()).toBe(
        'In this section, you can set up the basic settings for your site.',
      );

      tabs()[1].click();
      fixture.detectChanges();

      expect((q('.portal-settings__intro')!.textContent ?? '').trim()).toBe(
        'In this section, you can set up more advanced settings for your site.',
      );
    });

    it('steps forward with the right arrow and wraps', () => {
      const strip = tabs();
      strip[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
      fixture.detectChanges();
      expect(tabs()[1].getAttribute('aria-selected')).toBe('true');

      tabs()[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
      fixture.detectChanges();
      expect(tabs()[0].getAttribute('aria-selected')).toBe('true');
    });

    it('steps back with the left arrow and wraps', () => {
      tabs()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft' }));
      fixture.detectChanges();
      expect(tabs()[1].getAttribute('aria-selected')).toBe('true');
    });

    it('jumps to the ends with Home and End', () => {
      tabs()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'End' }));
      fixture.detectChanges();
      expect(tabs()[1].getAttribute('aria-selected')).toBe('true');

      tabs()[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Home' }));
      fixture.detectChanges();
      expect(tabs()[0].getAttribute('aria-selected')).toBe('true');
    });

    it('moves focus with selection, so the arrow keys leave the caller on the new tab', () => {
      tabs()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
      fixture.detectChanges();

      expect(document.activeElement).toBe(tabs()[1]);
    });

    it('ignores a key it does not handle', () => {
      tabs()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'a' }));
      fixture.detectChanges();

      expect(tabs()[0].getAttribute('aria-selected')).toBe('true');
    });
  });

  describe('section collapse', () => {
    beforeEach(() => {
      setInput('settings', settingsOf());
    });

    it('opens the two basic sections the legacy screen opened and closes the third', () => {
      expect(toggleFor('Site Details')!.getAttribute('aria-expanded')).toBe('true');
      expect(toggleFor('Site Marketing')!.getAttribute('aria-expanded')).toBe('true');
      expect(toggleFor('Appearance')!.getAttribute('aria-expanded')).toBe('false');
    });

    it('opens the two advanced sections the legacy screen opened and closes the rest', () => {
      tabs()[1].click();
      fixture.detectChanges();

      expect(toggleFor('Security Settings')!.getAttribute('aria-expanded')).toBe('true');
      expect(toggleFor('Page Management')!.getAttribute('aria-expanded')).toBe('true');
      expect(toggleFor('Payment Settings')!.getAttribute('aria-expanded')).toBe('false');
      expect(toggleFor('Other Settings')!.getAttribute('aria-expanded')).toBe('false');
    });

    it('removes a collapsed section body rather than hiding it', () => {
      expect(field('logoFile')).toBeNull();

      toggleFor('Appearance')!.click();
      fixture.detectChanges();

      expect(field('logoFile')).not.toBeNull();
    });

    it('collapses an open section again', () => {
      expect(field('portalName')).not.toBeNull();

      toggleFor('Site Details')!.click();
      fixture.detectChanges();

      expect(field('portalName')).toBeNull();
      expect(toggleFor('Site Details')!.getAttribute('aria-expanded')).toBe('false');
    });

    it('never points a section head at a body that may be absent', () => {
      for (const button of toggles()) {
        expect(button.hasAttribute('aria-controls')).toBeFalse();
      }
    });
  });

  describe('populating the form', () => {
    beforeEach(() => {
      setInput('lookups', lookupsOf());
      setInput('settings', settingsOf());
      setInput('canEditHostFields', true);
    });

    it('writes the site details', () => {
      expect(field<HTMLInputElement>('portalName')!.value).toBe('Contoso');
      expect(field<HTMLTextAreaElement>('description')!.value).toBe('  A site about widgets  ');
      expect(field<HTMLTextAreaElement>('keyWords')!.value).toBe('widgets,gadgets');
      expect(field<HTMLInputElement>('footerText')!.value).toBe('Copyright Contoso');
    });

    it('shows the identifier as a read-only field that is never submitted', () => {
      const guid = field<HTMLInputElement>('guid');
      expect(guid).not.toBeNull();
      expect(guid!.value).toBe('5f1b7c9e-0000-4000-8000-000000000001');
      expect(guid!.readOnly).toBeTrue();
      expect(guid!.getAttribute('formcontrolname')).toBeNull();
    });

    it('associates every visible label with its control', () => {
      for (const label of qa<HTMLLabelElement>('.portal-settings__label label[for]')) {
        expect(host().querySelector(`#${label.getAttribute('for')}`))
          .withContext(`label "${(label.textContent ?? '').trim()}" should reach its control`)
          .not.toBeNull();
      }
    });

    it('selects the held banner mode', () => {
      const chosen = radios('bannerAdvertising').filter((radio) => radio.checked);
      expect(chosen.length).toBe(1);
      expect(radios('bannerAdvertising').indexOf(chosen[0])).toBe(BANNER_ADVERTISING_MODE.site);
    });

    it('writes the appearance fields once the section is opened', () => {
      toggleFor('Appearance')!.click();
      fixture.detectChanges();

      expect(field<HTMLInputElement>('logoFile')!.value).toBe('logo.gif');
      expect(field<HTMLInputElement>('backgroundFile')!.value).toBe('bg.gif');
    });

    it('selects the held registration mode', () => {
      tabs()[1].click();
      fixture.detectChanges();

      const chosen = radios('userRegistration').filter((radio) => radio.checked);
      expect(chosen.length).toBe(1);
      expect(radios('userRegistration').indexOf(chosen[0])).toBe(USER_REGISTRATION_MODE.public);
    });

    it('offers the empty choice first on every select', () => {
      tabs()[1].click();
      fixture.detectChanges();

      for (const name of ['splashTabId', 'homeTabId', 'loginTabId', 'userTabId']) {
        const select = field<HTMLSelectElement>(name);
        expect((select!.options[0].textContent ?? '').trim()).toBe('<None Specified>');
      }
    });

    it('selects the held pages and home directory', () => {
      tabs()[1].click();
      fixture.detectChanges();

      expect(field<HTMLSelectElement>('splashTabId')!.selectedIndex).toBe(1);
      expect(field<HTMLSelectElement>('homeTabId')!.selectedIndex).toBe(2);
      expect(field<HTMLSelectElement>('loginTabId')!.selectedIndex).toBe(3);
      expect(field<HTMLSelectElement>('userTabId')!.selectedIndex).toBe(4);
      expect(field<HTMLInputElement>('homeDirectory')!.value).toBe('Portals/7');
    });

    it('leaves the payment-processor password blank, because none is returned', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Payment Settings')!.click();
      fixture.detectChanges();

      expect(field<HTMLSelectElement>('currency')!.selectedIndex).toBe(1);
      expect(field<HTMLInputElement>('processorUserId')!.value).toBe('merchant-1');
      expect(field<HTMLInputElement>('processorPassword')!.value).toBe('');
      expect(field<HTMLInputElement>('processorPassword')!.type).toBe('password');
    });

    it('narrows the stored instant to a date without shifting it into the local zone', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Host Settings')!.click();
      fixture.detectChanges();

      expect(field<HTMLInputElement>('expiryDate')!.value).toBe('2027-03-01');
    });

    it('writes the host-administered numbers, including a genuine zero', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Host Settings')!.click();
      fixture.detectChanges();

      expect(field<HTMLInputElement>('hostFee')!.value).toBe('19.5');
      expect(field<HTMLInputElement>('hostSpace')!.value).toBe('512');
      expect(field<HTMLInputElement>('pageQuota')!.value).toBe('0');
      expect(field<HTMLInputElement>('userQuota')!.value).toBe('250');
      expect(field<HTMLInputElement>('siteLogHistory')!.value).toBe('30');
    });

    it('leaves the expiry date blank when the contract does not expire', () => {
      setInput('settings', settingsOf({ expiryDate: null }));
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Host Settings')!.click();
      fixture.detectChanges();

      expect(field<HTMLInputElement>('expiryDate')!.value).toBe('');
    });

    it('discards an unsaved edit when fresh settings arrive', () => {
      type('portalName', 'Half typed');
      expect(field<HTMLInputElement>('portalName')!.value).toBe('Half typed');

      setInput('settings', settingsOf({ portalName: 'Fabrikam' }));

      expect(field<HTMLInputElement>('portalName')!.value).toBe('Fabrikam');
    });

    it('clears the form when the settings are withdrawn', () => {
      setInput('settings', undefined);

      expect(q('form')).toBeNull();
      expect(q('app-empty-state')).not.toBeNull();
    });
  });

  describe('help text', () => {
    beforeEach(() => {
      setInput('settings', settingsOf());
    });

    it('preserves the legacy double space after a sentence, character for character', () => {
      expect(hintFor('portalName')!.textContent).toBe(
        'This is the Title for your portal.  The text you enter will show up in the Title Bar.',
      );
    });

    it('preserves the legacy missing trailing period too', () => {
      tabs()[1].click();
      fixture.detectChanges();

      expect(hintFor('userRegistration')!.textContent).toBe(
        'The type of user registration allowed for this site',
      );
      expect(hintFor('homeDirectory')!.textContent).toBe(
        'Enter the Home Directory for this site',
      );
    });

    it('renders every hint exactly as the component declares it, on every section', () => {
      const declared = component['hints'] as Readonly<Record<string, string>>;
      let checked = 0;

      // The host-administered group holds six of the twenty-seven hints and is
      // withheld from an unprivileged operator, so the privilege is granted here
      // in order for the comparison to reach every one of them.
      setInput('canEditHostFields', true);

      for (const tabIndex of [0, 1]) {
        tabs()[tabIndex].click();
        fixture.detectChanges();
        // Open every section on this tab so no hint escapes the comparison.
        for (const toggle of toggles()) {
          if (toggle.getAttribute('aria-expanded') === 'false') {
            toggle.click();
            fixture.detectChanges();
          }
        }

        for (const element of qa<HTMLElement>('.form-help')) {
          const key = element.id
            .replace(/^portal-settings-/, '')
            .replace(/-hint$/, '');
          expect(declared[key])
            .withContext(`hint ${key} should be declared on the component`)
            .toBeDefined();
          expect(element.textContent)
            .withContext(`hint ${key} should render uncollapsed`)
            .toBe(declared[key]);
          checked += 1;
        }
      }

      // Every declared hint is reachable: 27 in total, and none is orphaned.
      expect(checked).toBe(Object.keys(declared).length);
    });

    it('points every control at its own help text', () => {
      const described = field<HTMLInputElement>('portalName')!.getAttribute('aria-describedby');
      expect(described).toBe('portal-settings-portalName-hint');
      expect(host().querySelector(`#${described}`)).not.toBeNull();
    });
  });

  describe('banner lock', () => {
    it('locks the control and shows the advisory for a site operator when host-managed', () => {
      setInput('canEditHostFields', false);
      setInput('settings', settingsOf({ bannerAdvertising: BANNER_ADVERTISING_MODE.host }));

      expect(q('.portal-settings__notice')).not.toBeNull();
      expect((q('.portal-settings__notice')!.textContent ?? '').trim()).toBe(
        'Banner option was set by the hostingprovider, and cannot be changed',
      );
      for (const radio of radios('bannerAdvertising')) {
        expect(radio.disabled).toBeTrue();
      }
    });

    it('leaves the control open for a site operator when it is site-managed', () => {
      setInput('canEditHostFields', false);
      setInput('settings', settingsOf({ bannerAdvertising: BANNER_ADVERTISING_MODE.site }));

      expect(q('.portal-settings__notice')).toBeNull();
      for (const radio of radios('bannerAdvertising')) {
        expect(radio.disabled).toBeFalse();
      }
    });

    it('never locks the control for a host operator, even when host-managed', () => {
      setInput('canEditHostFields', true);
      setInput('settings', settingsOf({ bannerAdvertising: BANNER_ADVERTISING_MODE.host }));

      expect(q('.portal-settings__notice')).toBeNull();
      for (const radio of radios('bannerAdvertising')) {
        expect(radio.disabled).toBeFalse();
      }
    });

    it('re-evaluates the lock when the privilege changes', () => {
      setInput('canEditHostFields', false);
      setInput('settings', settingsOf({ bannerAdvertising: BANNER_ADVERTISING_MODE.host }));
      expect(radios('bannerAdvertising')[0].disabled).toBeTrue();

      setInput('canEditHostFields', true);

      expect(radios('bannerAdvertising')[0].disabled).toBeFalse();
    });

    it('still submits a locked banner setting unchanged', () => {
      const emitted: UpdatePortalRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      setInput('canEditHostFields', false);
      setInput('settings', settingsOf({ bannerAdvertising: BANNER_ADVERTISING_MODE.host }));
      submit();

      expect(emitted.length).toBe(1);
      expect(emitted[0].bannerAdvertising).toBe(BANNER_ADVERTISING_MODE.host);
    });
  });

  describe('host-administered group', () => {
    beforeEach(() => {
      setInput('settings', settingsOf());
    });

    it('is withheld from an operator who may not administer it', () => {
      tabs()[1].click();
      fixture.detectChanges();

      expect(toggleFor('Host Settings')).toBeUndefined();
      expect(field('hostFee')).toBeNull();
    });

    it('is offered to an operator who may', () => {
      setInput('canEditHostFields', true);
      tabs()[1].click();
      fixture.detectChanges();

      expect(toggleFor('Host Settings')).not.toBeUndefined();
    });

    it('is still submitted in full when it is not offered', () => {
      const emitted: UpdatePortalRequest[] = [];
      component.save.subscribe((request) => emitted.push(request));

      setInput('canEditHostFields', false);
      submit();

      expect(emitted.length).toBe(1);
      expect(emitted[0].hostFee).toBe(19.5);
      expect(emitted[0].hostSpace).toBe(512);
      expect(emitted[0].pageQuota).toBe(0);
      expect(emitted[0].userQuota).toBe(250);
      expect(emitted[0].siteLogHistory).toBe(30);
      expect(emitted[0].expiryDate).toBe('2027-03-01');
    });
  });

  describe('submitting', () => {
    let emitted: UpdatePortalRequest[];

    beforeEach(() => {
      emitted = [];
      setInput('lookups', lookupsOf());
      setInput('settings', settingsOf());
      component.save.subscribe((request) => emitted.push(request));
    });

    it('carries the identifier of the site being written', () => {
      submit();

      expect(emitted.length).toBe(1);
      expect(emitted[0].portalId).toBe(7);
    });

    it('sends all twenty-seven properties, so a whole-row replacement is complete', () => {
      submit();

      const expected: readonly (keyof UpdatePortalRequest)[] = [
        'portalId',
        'portalName',
        'logoFile',
        'footerText',
        'expiryDate',
        'userRegistration',
        'bannerAdvertising',
        'currency',
        'administratorId',
        'hostFee',
        'hostSpace',
        'pageQuota',
        'userQuota',
        'paymentProcessor',
        'processorUserId',
        'processorPassword',
        'description',
        'keyWords',
        'backgroundFile',
        'siteLogHistory',
        'splashTabId',
        'homeTabId',
        'loginTabId',
        'userTabId',
        'defaultLanguage',
        'timeZoneOffset',
        'homeDirectory',
      ];

      expect(Object.keys(emitted[0]).sort()).toEqual([...expected].sort());
      expect(expected.length).toBe(27);
    });

    it('trims text and reports a blank field as absent', () => {
      submit();
      expect(emitted[0].description).toBe('A site about widgets');

      type('footerText', '   ');
      submit();
      expect(emitted[1].footerText).toBeNull();
    });

    it('preserves a numeric zero rather than reading it as absent', () => {
      submit();

      expect(emitted[0].pageQuota).toBe(0);
    });

    it('sends a blank processor password as absent, leaving the stored secret alone', () => {
      submit();

      expect(emitted[0].processorPassword).toBeNull();
    });

    it('sends a typed processor password', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Payment Settings')!.click();
      fixture.detectChanges();
      type('processorPassword', 'sekrit');
      submit();

      expect(emitted[0].processorPassword).toBe('sekrit');
    });

    it('keeps a chosen page identifier a number, not a string', () => {
      tabs()[1].click();
      fixture.detectChanges();
      choose('homeTabId', 3);
      submit();

      expect(emitted[0].homeTabId).toBe(13);
      expect(typeof emitted[0].homeTabId).toBe('number');
    });

    it('sends the empty page choice as absent', () => {
      tabs()[1].click();
      fixture.detectChanges();
      choose('splashTabId', 0);
      submit();

      expect(emitted[0].splashTabId).toBeNull();
    });

    it('keeps a chosen time-zone offset a number', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Other Settings')!.click();
      fixture.detectChanges();
      choose('timeZoneOffset', 2);
      submit();

      expect(emitted[0].timeZoneOffset).toBe(0);
      expect(typeof emitted[0].timeZoneOffset).toBe('number');
    });

    it('keeps a chosen registration mode a number', () => {
      tabs()[1].click();
      fixture.detectChanges();
      radios('userRegistration')[USER_REGISTRATION_MODE.verified].click();
      fixture.detectChanges();
      submit();

      expect(emitted[0].userRegistration).toBe(USER_REGISTRATION_MODE.verified);
      expect(typeof emitted[0].userRegistration).toBe('number');
    });

    it('emits nothing while a submission is already in flight', () => {
      setInput('saving', true);
      const button = actionButton('Update');
      expect(button!.disabled).toBeTrue();

      component['onSubmit']();

      expect(emitted.length).toBe(0);
    });

    it('emits nothing when the form is invalid, and marks it so messages appear', () => {
      type('portalName', 'x'.repeat(129));
      submit();

      expect(emitted.length).toBe(0);
      expect(messageFor('portalName')).not.toBeNull();
    });
  });

  describe('validation, mirrored from the server rules', () => {
    beforeEach(() => {
      setInput('lookups', lookupsOf());
      setInput('settings', settingsOf());
      setInput('canEditHostFields', true);
    });

    it('says nothing about a field the operator has not touched', () => {
      expect(messageFor('portalName')).toBeNull();
    });

    it('bounds the title at 128 characters', () => {
      type('portalName', 'x'.repeat(129));

      expect((messageFor('portalName')!.textContent ?? '').trim()).toBe(
        'Enter at most 128 characters.',
      );
    });

    it('accepts a title of exactly 128 characters', () => {
      type('portalName', 'x'.repeat(128));

      expect(messageFor('portalName')).toBeNull();
    });

    it('bounds the description and keywords at 500 characters', () => {
      type('description', 'x'.repeat(501));
      type('keyWords', 'x'.repeat(501));

      expect((messageFor('description')!.textContent ?? '').trim()).toBe(
        'Enter at most 500 characters.',
      );
      expect((messageFor('keyWords')!.textContent ?? '').trim()).toBe(
        'Enter at most 500 characters.',
      );
    });

    it('bounds the copyright line at 100 characters', () => {
      type('footerText', 'x'.repeat(101));

      expect((messageFor('footerText')!.textContent ?? '').trim()).toBe(
        'Enter at most 100 characters.',
      );
    });

    it('bounds the two image paths at 50 characters', () => {
      toggleFor('Appearance')!.click();
      fixture.detectChanges();
      type('logoFile', 'x'.repeat(51));
      type('backgroundFile', 'x'.repeat(51));

      expect((messageFor('logoFile')!.textContent ?? '').trim()).toBe(
        'Enter at most 50 characters.',
      );
      expect((messageFor('backgroundFile')!.textContent ?? '').trim()).toBe(
        'Enter at most 50 characters.',
      );
    });

    it('bounds the home directory at 100 characters', () => {
      tabs()[1].click();
      fixture.detectChanges();
      type('homeDirectory', 'x'.repeat(101));

      expect((messageFor('homeDirectory')!.textContent ?? '').trim()).toBe(
        'Enter at most 100 characters.',
      );
    });

    it('bounds the two processor text fields at 50 characters', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Payment Settings')!.click();
      fixture.detectChanges();
      type('processorUserId', 'x'.repeat(51));
      type('processorPassword', 'x'.repeat(51));

      expect((messageFor('processorUserId')!.textContent ?? '').trim()).toBe(
        'Enter at most 50 characters.',
      );
      expect((messageFor('processorPassword')!.textContent ?? '').trim()).toBe(
        'Enter at most 50 characters.',
      );
    });

    it('reports the server sentence when the hosting fee is negative', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Host Settings')!.click();
      fixture.detectChanges();
      type('hostFee', '-1');

      expect((messageFor('hostFee')!.textContent ?? '').trim()).toBe(
        'Hosting Fee must be zero or greater.',
      );
    });

    it('reports the server sentence for each negative quota', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Host Settings')!.click();
      fixture.detectChanges();
      type('hostSpace', '-1');
      type('pageQuota', '-1');
      type('userQuota', '-1');
      type('siteLogHistory', '-1');

      expect((messageFor('hostSpace')!.textContent ?? '').trim()).toBe(
        'Disk Space must be zero or greater.',
      );
      expect((messageFor('pageQuota')!.textContent ?? '').trim()).toBe(
        'Page Quota must be zero or greater.',
      );
      expect((messageFor('userQuota')!.textContent ?? '').trim()).toBe(
        'User Quota must be zero or greater.',
      );
      expect((messageFor('siteLogHistory')!.textContent ?? '').trim()).toBe(
        'Site Log History must be zero or greater.',
      );
    });

    it('accepts zero for every host-administered number', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Host Settings')!.click();
      fixture.detectChanges();
      type('hostFee', '0');
      type('hostSpace', '0');

      expect(messageFor('hostFee')).toBeNull();
      expect(messageFor('hostSpace')).toBeNull();
    });

    it('requires a currency of exactly three letters when one is given', () => {
      setInput('lookups', lookupsOf({ currencies: [{ value: 'GB', label: 'Two letters' }] }));
      setInput('settings', settingsOf({ currency: 'GB' }));
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Payment Settings')!.click();
      fixture.detectChanges();
      choose('currency', 1);

      expect((messageFor('currency')!.textContent ?? '').trim()).toBe(
        'Currency must be a three letter code.',
      );
    });

    it('accepts a blank currency, as the server rule does', () => {
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Payment Settings')!.click();
      fixture.detectChanges();
      choose('currency', 0);

      expect(messageFor('currency')).toBeNull();
    });

    it('bounds the default language at six characters', () => {
      setInput('lookups', lookupsOf({ languages: [{ value: 'en-GB-x', label: 'Too long' }] }));
      setInput('settings', settingsOf({ defaultLanguage: 'en-GB-x' }));
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Other Settings')!.click();
      fixture.detectChanges();
      choose('defaultLanguage', 1);

      expect((messageFor('defaultLanguage')!.textContent ?? '').trim()).toBe(
        'Enter at most 6 characters.',
      );
    });

    it('requires nothing, because the legacy screen required nothing', () => {
      type('portalName', '');
      type('description', '');
      type('keyWords', '');
      type('footerText', '');

      expect(messageFor('portalName')).toBeNull();
      expect(messageFor('description')).toBeNull();
      expect(messageFor('keyWords')).toBeNull();
      expect(messageFor('footerText')).toBeNull();
    });

    it('marks an offending control invalid for assistive technology', () => {
      type('portalName', 'x'.repeat(129));

      expect(field<HTMLInputElement>('portalName')!.getAttribute('aria-invalid')).toBe('true');
    });

    it('does not mark a valid control invalid', () => {
      type('portalName', 'Fine');

      expect(field<HTMLInputElement>('portalName')!.hasAttribute('aria-invalid')).toBeFalse();
    });

    it('adds the message to the control description only while it is showing', () => {
      expect(field<HTMLInputElement>('portalName')!.getAttribute('aria-describedby')).toBe(
        'portal-settings-portalName-hint',
      );

      type('portalName', 'x'.repeat(129));

      expect(field<HTMLInputElement>('portalName')!.getAttribute('aria-describedby')).toBe(
        'portal-settings-portalName-hint portal-settings-portalName-message',
      );
    });
  });

  describe('option lists', () => {
    it('offers a held value the supplied list has forgotten', () => {
      // The other three page settings are cleared so the assertion isolates the
      // one fold under test; all four share a single list, and each held page is
      // folded in, which the next test but one covers explicitly.
      setInput('lookups', lookupsOf({ pages: [{ value: 12, label: 'Home' }] }));
      setInput(
        'settings',
        settingsOf({ splashTabId: 99, homeTabId: null, loginTabId: null, userTabId: null }),
      );
      tabs()[1].click();
      fixture.detectChanges();

      const labels = Array.from(field<HTMLSelectElement>('splashTabId')!.options).map((option) =>
        (option.textContent ?? '').trim(),
      );
      expect(labels).toEqual(['<None Specified>', 'Home', '99']);
    });

    it('keeps that held value selected, so opening and saving cannot change it', () => {
      const emitted: UpdatePortalRequest[] = [];
      setInput('lookups', lookupsOf({ pages: [{ value: 12, label: 'Home' }] }));
      setInput('settings', settingsOf({ splashTabId: 99 }));
      component.save.subscribe((request) => emitted.push(request));

      submit();

      expect(emitted[0].splashTabId).toBe(99);
    });

    it('does not duplicate a held value the list already offers', () => {
      setInput('lookups', lookupsOf());
      setInput('settings', settingsOf());
      tabs()[1].click();
      fixture.detectChanges();

      expect(field<HTMLSelectElement>('splashTabId')!.options.length).toBe(5);
    });

    it('folds all four held pages into the shared page list', () => {
      setInput('lookups', lookupsOf({ pages: [] }));
      setInput(
        'settings',
        settingsOf({ splashTabId: 1, homeTabId: 2, loginTabId: 3, userTabId: 4 }),
      );
      tabs()[1].click();
      fixture.detectChanges();

      const labels = Array.from(field<HTMLSelectElement>('homeTabId')!.options).map((option) =>
        (option.textContent ?? '').trim(),
      );
      expect(labels).toEqual(['<None Specified>', '1', '2', '3', '4']);
    });

    it('recomputes when a fresh list arrives', () => {
      setInput('lookups', lookupsOf({ administrators: [] }));
      setInput('settings', settingsOf({ administratorId: null }));
      tabs()[1].click();
      fixture.detectChanges();
      toggleFor('Other Settings')!.click();
      fixture.detectChanges();
      expect(field<HTMLSelectElement>('administratorId')!.options.length).toBe(1);

      setInput('lookups', lookupsOf());

      expect(field<HTMLSelectElement>('administratorId')!.options.length).toBe(3);
    });
  });

  describe('action bar', () => {
    beforeEach(() => {
      setInput('settings', settingsOf());
    });

    it('offers Update and Cancel in the legacy order', () => {
      const labels = qa<HTMLButtonElement>('.portal-settings__actions button').map((button) =>
        (button.textContent ?? '').trim(),
      );
      expect(labels).toEqual(['Update', 'Cancel']);
    });

    it('withholds Delete unless the caller permits it', () => {
      expect(actionButton('Delete')).toBeUndefined();

      setInput('canDelete', true);

      expect(actionButton('Delete')).not.toBeUndefined();
    });

    it('emits cancellation without validating, as the legacy button did', () => {
      let cancelled = 0;
      component.cancel.subscribe(() => (cancelled += 1));

      type('portalName', 'x'.repeat(129));
      actionButton('Cancel')!.click();

      expect(cancelled).toBe(1);
    });

    it('disables both actions while a submission is in flight', () => {
      setInput('saving', true);

      expect(actionButton('Update')!.disabled).toBeTrue();
      expect(actionButton('Cancel')!.disabled).toBeTrue();
    });

    it('does not submit the form from Cancel or Delete', () => {
      setInput('canDelete', true);

      expect(actionButton('Cancel')!.type).toBe('button');
      expect(actionButton('Delete')!.type).toBe('button');
      expect(actionButton('Update')!.type).toBe('submit');
    });
  });

  describe('deletion', () => {
    beforeEach(() => {
      setInput('settings', settingsOf());
      setInput('canDelete', true);
    });

    it('shows no dialog until Delete is pressed', () => {
      expect(q('app-confirm-dialog')).toBeNull();
    });

    it('opens the confirmation carrying the legacy wording, character for character', () => {
      actionButton('Delete')!.click();
      fixture.detectChanges();

      expect(q('app-confirm-dialog')).not.toBeNull();
      expect((q('.confirm-dialog__message')!.textContent ?? '').trim()).toBe(
        'Are You Sure You Wish To Delete This Portal ?',
      );
    });

    it('emits nothing until the confirmation is accepted', () => {
      let removals = 0;
      component.remove.subscribe(() => (removals += 1));

      actionButton('Delete')!.click();
      fixture.detectChanges();

      expect(removals).toBe(0);
    });

    it('emits the removal once confirmed and closes the dialog', () => {
      let removals = 0;
      component.remove.subscribe(() => (removals += 1));

      actionButton('Delete')!.click();
      fixture.detectChanges();
      // Located by the destructive modifier rather than by label: the shared
      // dialog prefixes a warning glyph to a dangerous confirm label, so its text
      // is not the bare `confirmLabel`.
      const confirmButton = q<HTMLButtonElement>('.confirm-dialog__button--danger');
      expect(confirmButton).not.toBeNull();
      expect((confirmButton!.textContent ?? '').trim().endsWith('Delete')).toBeTrue();
      confirmButton!.click();
      fixture.detectChanges();

      expect(removals).toBe(1);
      expect(q('app-confirm-dialog')).toBeNull();
    });

    it('emits nothing when the confirmation is dismissed', () => {
      let removals = 0;
      component.remove.subscribe(() => (removals += 1));

      actionButton('Delete')!.click();
      fixture.detectChanges();
      const cancelButton = qa<HTMLButtonElement>('.confirm-dialog__button').find(
        (button) => (button.textContent ?? '').trim() === 'Cancel',
      );
      cancelButton!.click();
      fixture.detectChanges();

      expect(removals).toBe(0);
      expect(q('app-confirm-dialog')).toBeNull();
    });

    it('marks the confirmation as destructive', () => {
      actionButton('Delete')!.click();
      fixture.detectChanges();

      expect(q('.confirm-dialog__button--danger')).not.toBeNull();
    });
  });
});
