//
// Specification for the portal settings screen.
//
// The screen is exercised through its real collaborators' CONTRACTS, replaced by doubles:
// the portal signal store, the identity store, the page transport and the notification
// queue. No HTTP is configured and none can occur — the component reaches no transport type
// — so every assertion below is about this screen's own behaviour and never about a
// framework's.
//
// The cases are grouped by the measurement each defends, and the awkward ones are the point:
// a portal identified as `0`, a portal identified as `-1`, a stored quota of `0`, an absent
// expiry date, and the page selector's selectable absent option. Those are exactly the values
// a truthiness test would get wrong.
//

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { BannerAdvertisingMode, UserRegistrationMode } from '../../../core/models/portal.model';
import { NotificationService } from '../../../core/services/notification.service';
import { TabService } from '../../../core/services/tab.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { PortalSettingsComponent } from './portal-settings.component';

import type { WritableSignal } from '@angular/core';
import type {
  PortalDetail,
  PortalSettings,
  UpdatePortalSettingsRequest,
} from '../../../core/models/portal.model';
import type { TabListItem } from '../../../core/models/tab.model';
import type { PortalFailure } from '../../../core/state/portal.store';

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

/**
 * A settings resource whose awkward values are the interesting ones.
 *
 * The quotas are `0`, which is a real stored value and must render as `0`. The expiry date
 * is absent, which must render as an empty box. The splash page is absent and the home page
 * is `0`, which is a real page identifier because page identifiers start at zero.
 */
function settingsFixture(overrides: Partial<PortalSettings> = {}): PortalSettings {
  return {
    portalId: 0,
    portalName: 'Baseline Portal',
    description: 'A description.',
    keyWords: 'one,two',
    footerText: 'Copyright notice',
    logoFile: 'logo.gif',
    backgroundFile: 'back.gif',
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.Site,
    currency: 'USD',
    administratorId: 2,
    hostFee: 0,
    hostSpace: 0,
    pageQuota: 0,
    userQuota: 0,
    paymentProcessor: 'PayPal',
    processorUserId: 'merchant',
    siteLogHistory: -1,
    splashTabId: null,
    homeTabId: 0,
    loginTabId: null,
    userTabId: null,
    defaultLanguage: 'en-US',
    timeZoneOffset: -480,
    homeDirectory: 'Portals/0',
    guid: 'a2f9c1d4-5b6e-4a70-8c91-0d3e2f4b6a80',
    ...overrides,
  };
}

/** A page row, defaulting to an ordinary visible root page. */
function pageFixture(overrides: Partial<TabListItem> = {}): TabListItem {
  return {
    tabId: 0,
    tabName: 'Home',
    title: null,
    tabOrder: 1,
    parentId: null,
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

/** A portal detail carrying the administration page identifier the filter needs. */
function detailFixture(overrides: Partial<PortalDetail> = {}): PortalDetail {
  return {
    portalId: 0,
    portalName: 'Baseline Portal',
    description: null,
    keyWords: null,
    footerText: null,
    logoFile: null,
    backgroundFile: null,
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.Site,
    currency: null,
    administratorId: 2,
    email: null,
    hostFee: 0,
    hostSpace: 0,
    pageQuota: 0,
    userQuota: 0,
    users: 3,
    pages: 4,
    administratorRoleId: 0,
    administratorRoleName: 'Administrators',
    registeredRoleId: 1,
    registeredRoleName: 'Registered Users',
    guid: 'a2f9c1d4-5b6e-4a70-8c91-0d3e2f4b6a80',
    paymentProcessor: null,
    processorUserId: null,
    siteLogHistory: -1,
    adminTabId: 90,
    superTabId: null,
    splashTabId: null,
    homeTabId: 0,
    loginTabId: null,
    userTabId: null,
    defaultLanguage: 'en-US',
    timeZoneOffset: -480,
    homeDirectory: 'Portals/0',
    aliases: null,
    ...overrides,
  };
}

/** A classified failure, as the store publishes one. */
function failureFixture(overrides: Partial<PortalFailure> = {}): PortalFailure {
  return {
    problem: { status: 400, title: 'Bad Request' },
    status: 400,
    severity: 'error',
    conflictCode: null,
    validation: null,
    supportReference: null,
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Doubles
// ---------------------------------------------------------------------------

/** The slice of the portal store this screen reads and commands. */
interface PortalStoreDouble {
  settings: WritableSignal<PortalSettings | null>;
  settingsLoading: WritableSignal<boolean>;
  settingsFailure: WritableSignal<PortalFailure | null>;
  selectedPortal: WritableSignal<PortalDetail | null>;
  detailLoading: WritableSignal<boolean>;
  detailFailure: WritableSignal<PortalFailure | null>;
  loadSettings: jasmine.Spy;
  loadPortal: jasmine.Spy;
  saveSettings: jasmine.Spy;
  deletePortal: jasmine.Spy;
  clearFailures: jasmine.Spy;
}

function portalStoreDouble(): PortalStoreDouble {
  return {
    settings: signal<PortalSettings | null>(null),
    settingsLoading: signal(false),
    settingsFailure: signal<PortalFailure | null>(null),
    selectedPortal: signal<PortalDetail | null>(null),
    detailLoading: signal(false),
    detailFailure: signal<PortalFailure | null>(null),
    loadSettings: jasmine.createSpy('loadSettings'),
    loadPortal: jasmine.createSpy('loadPortal'),
    saveSettings: jasmine.createSpy('saveSettings'),
    deletePortal: jasmine.createSpy('deletePortal'),
    clearFailures: jasmine.createSpy('clearFailures'),
  };
}

interface AuthStoreDouble {
  isSuperUser: WritableSignal<boolean>;
  portalId: WritableSignal<number | null>;
}

function authStoreDouble(): AuthStoreDouble {
  return { isSuperUser: signal(false), portalId: signal<number | null>(0) };
}

describe('PortalSettingsComponent', () => {
  let fixture: ComponentFixture<PortalSettingsComponent>;
  let portals: PortalStoreDouble;
  let identity: AuthStoreDouble;
  let pages: jasmine.SpyObj<Pick<TabService, 'getByPortal'>>;
  let notifications: jasmine.SpyObj<
    Pick<NotificationService, 'success' | 'warning' | 'error' | 'notify'>
  >;
  let router: jasmine.SpyObj<Pick<Router, 'navigateByUrl'>>;

  beforeEach(async () => {
    portals = portalStoreDouble();
    identity = authStoreDouble();
    pages = jasmine.createSpyObj<Pick<TabService, 'getByPortal'>>('TabService', ['getByPortal']);
    notifications = jasmine.createSpyObj<
      Pick<NotificationService, 'success' | 'warning' | 'error' | 'notify'>
    >('NotificationService', ['success', 'warning', 'error', 'notify']);
    router = jasmine.createSpyObj<Pick<Router, 'navigateByUrl'>>('Router', ['navigateByUrl']);

    pages.getByPortal.and.returnValue(of([pageFixture()]));
    router.navigateByUrl.and.returnValue(Promise.resolve(true));

    await TestBed.configureTestingModule({
      imports: [PortalSettingsComponent],
      providers: [
        { provide: PortalStore, useValue: portals },
        { provide: AuthStore, useValue: identity },
        { provide: TabService, useValue: pages },
        { provide: NotificationService, useValue: notifications },
        { provide: Router, useValue: router },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(PortalSettingsComponent);
  });

  /** Binds the route input the way the router would, and settles the view. */
  function route(portalId: string | number | null | undefined): void {
    fixture.componentRef.setInput('portalId', portalId);
    fixture.detectChanges();
  }

  /** The reactive form, reached through the component instance. */
  function form(): PortalSettingsComponent['form'] {
    return (fixture.componentInstance as unknown as { form: PortalSettingsComponent['form'] }).form;
  }

  function text(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  // -------------------------------------------------------------------------
  describe('identity and route binding', () => {
    it('exposes the class name the route contract loads', () => {
      expect(PortalSettingsComponent.name).toBe('PortalSettingsComponent');
    });

    it('accepts a portal identified as 0, which is a real portal', () => {
      route('0');

      expect(fixture.componentInstance.portalId).toBe(0);
      expect(portals.loadSettings).toHaveBeenCalledWith(0);
      expect(pages.getByPortal).toHaveBeenCalledWith(0);
    });

    it('accepts a portal identified as -1, the first identity value the schema issues', () => {
      route('-1');

      expect(fixture.componentInstance.portalId).toBe(-1);
      expect(portals.loadSettings).toHaveBeenCalledWith(-1);
    });

    it('reads the portal detail as well, because the page filter needs its admin page', () => {
      route('0');

      expect(portals.loadPortal).toHaveBeenCalledWith(0);
    });

    it('reports absence rather than guessing when the segment is not a number', () => {
      route('not-a-portal');

      expect(fixture.componentInstance.portalId).toBeUndefined();
      expect(portals.loadSettings).not.toHaveBeenCalled();
      expect(text()).toContain('does not identify a portal');
    });

    it('reports absence when no segment was bound at all', () => {
      route(undefined);

      expect(fixture.componentInstance.portalId).toBeUndefined();
      expect(portals.loadSettings).not.toHaveBeenCalled();
    });

    it('reads once per identifier and not again for the same one', () => {
      route('0');
      route('0');

      expect(portals.loadSettings).toHaveBeenCalledTimes(1);
      expect(pages.getByPortal).toHaveBeenCalledTimes(1);
    });
  });

  // -------------------------------------------------------------------------
  describe('hydration', () => {
    beforeEach(() => {
      route('0');
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
    });

    it('fills the four site-detail boxes from the resource', () => {
      expect(form().controls.portalName.value).toBe('Baseline Portal');
      expect(form().controls.description.value).toBe('A description.');
      expect(form().controls.keyWords.value).toBe('one,two');
      expect(form().controls.footerText.value).toBe('Copyright notice');
    });

    it('renders a stored quota of 0 as the literal 0, never as blank and never as a word', () => {
      expect(form().controls.hostSpace.value).toBe('0');
      expect(form().controls.pageQuota.value).toBe('0');
      expect(form().controls.userQuota.value).toBe('0');
      expect(form().controls.hostFee.value).toBe('0');
    });

    it('leaves the expiry box EMPTY when the portal has no expiry', () => {
      expect(form().controls.expiryDate.value).toBe('');
    });

    it('shows the date alone when the portal does have an expiry', () => {
      portals.settings.set(settingsFixture({ expiryDate: '2031-07-04T00:00:00' }));
      fixture.detectChanges();

      expect(form().controls.expiryDate.value).toBe('2031-07-04');
    });

    it('selects the absent-page option for an absent page reference', () => {
      expect(form().controls.splashTabId.value).toBe(-1);
      expect(form().controls.loginTabId.value).toBe(-1);
    });

    it('keeps page 0 as page 0, because page identifiers start at zero', () => {
      expect(form().controls.homeTabId.value).toBe(0);
    });

    it('upper-cases the identifier and never puts it in an input', () => {
      expect(text()).toContain('A2F9C1D4-5B6E-4A70-8C91-0D3E2F4B6A80');
      expect(fixture.debugElement.query(By.css('#portal-settings-guid'))?.nativeElement.tagName)
        .toBe('OUTPUT');
    });

    it('leaves the form pristine so no message shows before the operator acts', () => {
      expect(form().pristine).toBeTrue();
      expect(form().untouched).toBeTrue();
    });

    it('re-hydrates when the store publishes a newer resource', () => {
      portals.settings.set(settingsFixture({ portalName: 'Renamed' }));
      fixture.detectChanges();

      expect(form().controls.portalName.value).toBe('Renamed');
    });
  });

  // -------------------------------------------------------------------------
  describe('the two tabs and their disclosures', () => {
    beforeEach(() => {
      route('0');
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
    });

    it('offers exactly two tabs', () => {
      const tabs = fixture.debugElement.queryAll(By.css('[role="tab"]'));

      expect(tabs.length).toBe(2);
      expect(tabs.map((tab) => (tab.nativeElement as HTMLElement).textContent?.trim())).toEqual([
        'Basic Settings',
        'Advanced Settings',
      ]);
    });

    it('starts on the basic tab', () => {
      expect(fixture.debugElement.query(By.css('#portal-settings-panel-basic'))).not.toBeNull();
      expect(fixture.debugElement.query(By.css('#portal-settings-panel-advanced'))).toBeNull();
    });

    it('moves to the advanced tab when it is chosen', () => {
      const tabs = fixture.debugElement.queryAll(By.css('[role="tab"]'));

      tabs[1]?.triggerEventHandler('click');
      fixture.detectChanges();

      expect(fixture.debugElement.query(By.css('#portal-settings-panel-advanced'))).not.toBeNull();
    });

    it('moves between tabs with the arrow keys and wraps', () => {
      const strip = fixture.debugElement.query(By.css('[role="tablist"]'));

      strip.triggerEventHandler('keydown', new KeyboardEvent('keydown', { key: 'ArrowRight' }));
      fixture.detectChanges();
      expect(fixture.debugElement.query(By.css('#portal-settings-panel-advanced'))).not.toBeNull();

      strip.triggerEventHandler('keydown', new KeyboardEvent('keydown', { key: 'ArrowRight' }));
      fixture.detectChanges();
      expect(fixture.debugElement.query(By.css('#portal-settings-panel-basic'))).not.toBeNull();
    });

    it('leaves keys it does not handle to the browser', () => {
      const strip = fixture.debugElement.query(By.css('[role="tablist"]'));
      const event = new KeyboardEvent('keydown', { key: 'a', cancelable: true });

      strip.triggerEventHandler('keydown', event);

      expect(event.defaultPrevented).toBeFalse();
    });

    it('keeps the values of the tab that is not showing', () => {
      form().controls.userQuota.setValue('42');
      fixture.debugElement.queryAll(By.css('[role="tab"]'))[1]?.triggerEventHandler('click');
      fixture.detectChanges();

      expect(form().controls.userQuota.value).toBe('42');
    });

    it('opens the sections the markup declared open and closes the ones it declared closed', () => {
      expect(fixture.debugElement.query(By.css('#portal-settings-section-siteDetails'))).not.toBeNull();
      expect(fixture.debugElement.query(By.css('#portal-settings-section-marketing'))).not.toBeNull();

      fixture.debugElement.queryAll(By.css('[role="tab"]'))[1]?.triggerEventHandler('click');
      fixture.detectChanges();

      expect(fixture.debugElement.query(By.css('#portal-settings-section-security'))).not.toBeNull();
      expect(fixture.debugElement.query(By.css('#portal-settings-section-pages'))).not.toBeNull();
      expect(fixture.debugElement.query(By.css('#portal-settings-section-other'))).toBeNull();
    });

    it('toggles a disclosure and reports it through aria-expanded', () => {
      const toggle = fixture.debugElement.query(By.css('.portal-settings__toggle'));

      expect(toggle.attributes['aria-expanded']).toBe('true');

      toggle.triggerEventHandler('click');
      fixture.detectChanges();

      expect(
        fixture.debugElement.query(By.css('.portal-settings__toggle')).attributes['aria-expanded'],
      ).toBe('false');
      expect(fixture.debugElement.query(By.css('#portal-settings-section-siteDetails'))).toBeNull();
    });

    it('uses the measured section captions, resource wording winning over markup', () => {
      expect(text()).toContain('Site Marketing');
      expect(text()).toContain('Site Details');
    });
  });

  // -------------------------------------------------------------------------
  describe('the four page selectors', () => {
    beforeEach(() => {
      pages.getByPortal.and.returnValue(
        of([
          pageFixture({ tabId: 0, tabName: 'Home', level: 0 }),
          pageFixture({ tabId: 1, tabName: 'About', level: 1, parentId: 0 }),
          pageFixture({ tabId: 2, tabName: 'Deep', level: 2, parentId: 1 }),
          pageFixture({ tabId: 3, tabName: 'Hidden', isVisible: false }),
          pageFixture({ tabId: 4, tabName: 'Recycled', isDeleted: true }),
          pageFixture({ tabId: 5, tabName: 'Link', url: 'https://example.test' }),
          pageFixture({ tabId: 90, tabName: 'Admin' }),
          pageFixture({ tabId: 91, tabName: 'Under Admin', parentId: 90 }),
        ]),
      );
      route('0');
      portals.selectedPortal.set(detailFixture());
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
      fixture.debugElement.queryAll(By.css('[role="tab"]'))[1]?.triggerEventHandler('click');
      fixture.detectChanges();
    });

    function optionLabels(controlId: string): string[] {
      return fixture.debugElement
        .queryAll(By.css(`#${controlId} option`))
        .map((option) => (option.nativeElement as HTMLElement).textContent ?? '');
    }

    it('reads the page listing ONCE for all four selectors', () => {
      expect(pages.getByPortal).toHaveBeenCalledTimes(1);
    });

    it('offers the absent-page option first, and it is selectable', () => {
      const options = fixture.debugElement.queryAll(By.css('#portal-settings-homeTabId option'));

      expect((options[0]?.nativeElement as HTMLElement).textContent).toBe('<None Specified>');
      expect((options[0]?.nativeElement as HTMLOptionElement).disabled).toBeFalse();
    });

    it('indents by three dots per level, and not at all at the root', () => {
      const labels = optionLabels('portal-settings-homeTabId');

      expect(labels).toContain('Home');
      expect(labels).toContain('...About');
      expect(labels).toContain('......Deep');
    });

    it('includes invisible pages, because the legacy call asked for them', () => {
      expect(optionLabels('portal-settings-homeTabId')).toContain('Hidden');
    });

    it('excludes recycled pages, link pages and the administration band', () => {
      const labels = optionLabels('portal-settings-homeTabId');

      expect(labels).not.toContain('Recycled');
      expect(labels).not.toContain('Link');
      expect(labels).not.toContain('Admin');
      expect(labels).not.toContain('Under Admin');
    });

    it('gives all four selectors the same options', () => {
      const home = optionLabels('portal-settings-homeTabId');

      expect(optionLabels('portal-settings-splashTabId')).toEqual(home);
      expect(optionLabels('portal-settings-loginTabId')).toEqual(home);
      expect(optionLabels('portal-settings-userTabId')).toEqual(home);
    });

    it('keeps a chosen page visible even once it is recycled', () => {
      pages.getByPortal.and.returnValue(
        of([pageFixture({ tabId: 7, tabName: 'Gone', isDeleted: true })]),
      );
      portals.settings.set(settingsFixture({ homeTabId: 7 }));
      route('1');
      fixture.debugElement.queryAll(By.css('[role="tab"]'))[1]?.triggerEventHandler('click');
      fixture.detectChanges();

      expect(optionLabels('portal-settings-homeTabId')).toContain('Gone');
    });

    it('says so, and still works, when the listing cannot be read', () => {
      pages.getByPortal.and.returnValue(throwError(() => new Error('unreachable')));
      route('2');
      fixture.detectChanges();

      expect(notifications.warning).toHaveBeenCalled();
      expect(text()).toContain('list of pages could not be loaded');
    });
  });

  // -------------------------------------------------------------------------
  describe('validation', () => {
    beforeEach(() => {
      route('0');
      identity.isSuperUser.set(true);
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
    });

    it('accepts a BLANK expiry date, because the legacy type check passed on empty input', () => {
      form().controls.expiryDate.setValue('');

      expect(form().controls.expiryDate.valid).toBeTrue();
    });

    it('refuses a malformed expiry date with the measured wording, break markup removed', () => {
      form().controls.expiryDate.setValue('not-a-date');

      expect(form().controls.expiryDate.valid).toBeFalse();
      expect(form().controls.expiryDate.errors?.['expiryDateType']).toBe('Invalid expiry date!');
    });

    it('accepts a BLANK hosting fee', () => {
      form().controls.hostFee.setValue('');

      expect(form().controls.hostFee.valid).toBeTrue();
    });

    it('refuses a non-currency fee with the measured wording', () => {
      form().controls.hostFee.setValue('free');

      expect(form().controls.hostFee.errors?.['hostFeeType']).toBe(
        'Invalid fee, needs to be a currency value!',
      );
    });

    it('accepts a decimal fee and a negative one, because the legacy declared no floor', () => {
      form().controls.hostFee.setValue('12.50');
      expect(form().controls.hostFee.valid).toBeTrue();

      form().controls.hostFee.setValue('-5');
      expect(form().controls.hostFee.valid).toBeTrue();
    });

    it('refuses a fractional quota', () => {
      form().controls.userQuota.setValue('1.5');

      expect(form().controls.userQuota.valid).toBeFalse();
    });

    it('declares NO presence rule anywhere, matching a screen with no required validators', () => {
      form().setValue({
        portalName: '',
        description: '',
        keyWords: '',
        footerText: '',
        bannerAdvertising: BannerAdvertisingMode.None,
        userRegistration: UserRegistrationMode.NoRegistration,
        splashTabId: -1,
        homeTabId: -1,
        loginTabId: -1,
        userTabId: -1,
        timeZoneOffset: '',
        expiryDate: '',
        hostFee: '',
        hostSpace: '',
        pageQuota: '',
        userQuota: '',
      });

      expect(form().valid).toBeTrue();
    });

    it('enforces every measured character limit', () => {
      form().controls.portalName.setValue('x'.repeat(129));
      expect(form().controls.portalName.valid).toBeFalse();

      form().controls.description.setValue('x'.repeat(476));
      expect(form().controls.description.valid).toBeFalse();

      form().controls.footerText.setValue('x'.repeat(101));
      expect(form().controls.footerText.valid).toBeFalse();

      form().controls.userQuota.setValue('1234567');
      expect(form().controls.userQuota.valid).toBeFalse();
    });

    it('shows no message until the control is touched or changed', () => {
      form().controls.hostFee.setValue('free');
      form().controls.hostFee.markAsUntouched();
      form().controls.hostFee.markAsPristine();

      expect(fixture.componentInstance).toBeTruthy();
      expect(text()).not.toContain('Invalid fee');
    });
  });

  // -------------------------------------------------------------------------
  describe('the banner lock', () => {
    it('disables the choice and shows the notice when the stored value is Host', () => {
      route('0');
      identity.isSuperUser.set(false);
      portals.settings.set(settingsFixture({ bannerAdvertising: BannerAdvertisingMode.Host }));
      fixture.detectChanges();

      expect(form().controls.bannerAdvertising.disabled).toBeTrue();
      expect(text()).toContain('Banner option was set by the hostingprovider');
    });

    it('strips the leading break markup from that notice', () => {
      route('0');
      identity.isSuperUser.set(false);
      portals.settings.set(settingsFixture({ bannerAdvertising: BannerAdvertisingMode.Host }));
      fixture.detectChanges();

      expect(text()).not.toContain('<br>');
    });

    it('leaves the choice open for a host account, and hides the notice', () => {
      identity.isSuperUser.set(true);
      route('0');
      portals.settings.set(settingsFixture({ bannerAdvertising: BannerAdvertisingMode.Host }));
      fixture.detectChanges();

      expect(form().controls.bannerAdvertising.enabled).toBeTrue();
      expect(text()).not.toContain('Banner option was set by');
    });

    it('reacts when the identity resolves AFTER the resource has landed', () => {
      route('0');
      identity.isSuperUser.set(false);
      portals.settings.set(settingsFixture({ bannerAdvertising: BannerAdvertisingMode.Host }));
      fixture.detectChanges();
      expect(form().controls.bannerAdvertising.disabled).toBeTrue();

      identity.isSuperUser.set(true);
      fixture.detectChanges();

      expect(form().controls.bannerAdvertising.enabled).toBeTrue();
    });

    it('does not lock mid-edit when Host is merely chosen', () => {
      route('0');
      identity.isSuperUser.set(false);
      portals.settings.set(settingsFixture({ bannerAdvertising: BannerAdvertisingMode.None }));
      fixture.detectChanges();

      form().controls.bannerAdvertising.setValue(BannerAdvertisingMode.Host);
      fixture.detectChanges();

      expect(form().controls.bannerAdvertising.enabled).toBeTrue();
    });
  });

  // -------------------------------------------------------------------------
  describe('the host-settings gate', () => {
    beforeEach(() => {
      route('0');
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
      fixture.debugElement.queryAll(By.css('[role="tab"]'))[1]?.triggerEventHandler('click');
      fixture.detectChanges();
    });

    it('hides the section from an account without the host role', () => {
      expect(text()).not.toContain('Host Settings');
      expect(fixture.debugElement.query(By.css('#portal-settings-hostFee'))).toBeNull();
    });

    it('shows it to a host account', () => {
      identity.isSuperUser.set(true);
      fixture.detectChanges();

      expect(text()).toContain('Host Settings');
    });

    it('still HYDRATES the hidden host controls, so they are returned unchanged', () => {
      expect(form().controls.hostFee.value).toBe('0');
      expect(form().controls.userQuota.value).toBe('0');
    });
  });

  // -------------------------------------------------------------------------
  describe('saving', () => {
    beforeEach(() => {
      route('0');
      identity.isSuperUser.set(true);
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
    });

    function submitted(): UpdatePortalSettingsRequest {
      fixture.debugElement.query(By.css('form')).triggerEventHandler('submit', new Event('submit'));

      return portals.saveSettings.calls.mostRecent().args[1] as UpdatePortalSettingsRequest;
    }

    it('sends the edited site-detail values', () => {
      form().controls.portalName.setValue('New Name');

      expect(submitted().portalName).toBe('New Name');
    });

    it('reports a blank box as absence, matching the legacy empty-string null', () => {
      form().controls.footerText.setValue('   ');

      expect(submitted().footerText).toBeNull();
    });

    it('saves ZERO for a blank fee and blank quotas, the measured legacy default', () => {
      form().controls.hostFee.setValue('');
      form().controls.hostSpace.setValue('');
      form().controls.pageQuota.setValue('');
      form().controls.userQuota.setValue('');

      const request = submitted();

      expect(request.hostFee).toBe(0);
      expect(request.hostSpace).toBe(0);
      expect(request.pageQuota).toBe(0);
      expect(request.userQuota).toBe(0);
    });

    it('sends ABSENCE for a blank expiry, never the unstorable legacy date sentinel', () => {
      form().controls.expiryDate.setValue('');

      const request = submitted();

      expect(request.expiryDate).toBeNull();
      expect(request.expiryDate).not.toBe('0001-01-01T00:00:00');
    });

    it('sends absence for the absent-page option, and never the option value itself', () => {
      form().controls.splashTabId.setValue(-1);

      expect(submitted().splashTabId).toBeNull();
    });

    it('sends page 0 as page 0', () => {
      form().controls.homeTabId.setValue(0);

      expect(submitted().homeTabId).toBe(0);
    });

    it('returns every member it does not show, unchanged', () => {
      const request = submitted();

      expect(request.logoFile).toBe('logo.gif');
      expect(request.backgroundFile).toBe('back.gif');
      expect(request.currency).toBe('USD');
      expect(request.paymentProcessor).toBe('PayPal');
      expect(request.processorUserId).toBe('merchant');
      expect(request.siteLogHistory).toBe(-1);
      expect(request.defaultLanguage).toBe('en-US');
      expect(request.homeDirectory).toBe('Portals/0');
      expect(request.administratorId).toBe(2);
    });

    it('never returns the processor credential', () => {
      expect(submitted().processorCredentialReference).toBeNull();
    });

    it('addresses the portal from the route', () => {
      submitted();

      expect(portals.saveSettings.calls.mostRecent().args[0]).toBe(0);
    });

    it('refuses to send a rejected form, and says why once', () => {
      form().controls.hostFee.setValue('free');
      fixture.debugElement.query(By.css('form')).triggerEventHandler('submit', new Event('submit'));
      fixture.detectChanges();

      expect(portals.saveSettings).not.toHaveBeenCalled();
      expect(notifications.warning).toHaveBeenCalled();
      expect(form().touched).toBeTrue();
      expect(text()).toContain('Correct the highlighted fields');
    });

    it('announces success once the store confirms', () => {
      const stored = settingsFixture({ portalName: 'Saved' });

      submitted();
      const confirm = portals.saveSettings.calls.mostRecent().args[2] as (
        value: PortalSettings,
      ) => void;
      confirm(stored);

      expect(notifications.success).toHaveBeenCalledWith('The site settings were saved.');
    });
  });

  // -------------------------------------------------------------------------
  describe('failures', () => {
    beforeEach(() => {
      route('0');
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
    });

    it('names the host-only-field rule for a refusal, and never a session problem', () => {
      portals.settingsFailure.set(failureFixture({ status: 403, severity: 'warning' }));
      fixture.detectChanges();

      const [severity, message] = notifications.notify.calls.mostRecent().args as [string, string];

      expect(severity).toBe('warning');
      expect(message).toContain('Only a host account may change');
      expect(message).not.toContain('session');
      expect(router.navigateByUrl).not.toHaveBeenCalled();
    });

    it('uses the shared wording for the last-remaining-portal refusal', () => {
      portals.detailFailure.set(
        failureFixture({ status: 409, conflictCode: 'portal.last_remaining', severity: 'warning' }),
      );
      fixture.detectChanges();

      expect(notifications.notify.calls.mostRecent().args[1]).toBe(
        'You Can Not Delete The Last Portal In Your Database',
      );
    });

    it('hands the problem document to the shared failure surface untouched', () => {
      const failure = failureFixture();

      portals.settingsFailure.set(failure);
      fixture.detectChanges();

      expect(fixture.debugElement.query(By.css('app-error-banner'))).not.toBeNull();
    });

    it('announces one failure once, not on every pass', () => {
      portals.settingsFailure.set(failureFixture());
      fixture.detectChanges();
      fixture.detectChanges();

      expect(notifications.notify).toHaveBeenCalledTimes(1);
    });
  });

  // -------------------------------------------------------------------------
  describe('cancel and delete', () => {
    beforeEach(() => {
      route('5');
      portals.settings.set(settingsFixture({ portalId: 5 }));
      fixture.detectChanges();
    });

    it('returns to the listing on cancel, holding no referrer of its own', () => {
      fixture.debugElement
        .queryAll(By.css('.portal-settings__actions button'))[1]
        ?.triggerEventHandler('click');

      expect(router.navigateByUrl).toHaveBeenCalledWith('/portals');
    });

    it('withholds delete from an account without the host role', () => {
      expect(fixture.debugElement.query(By.css('app-page-header button'))).toBeNull();
    });

    it('offers delete to a host account when the target is another portal', () => {
      identity.isSuperUser.set(true);
      identity.portalId.set(0);
      fixture.detectChanges();

      expect(fixture.debugElement.query(By.css('app-page-header button'))).not.toBeNull();
    });

    it('withholds delete when the target IS the portal being browsed', () => {
      identity.isSuperUser.set(true);
      identity.portalId.set(5);
      fixture.detectChanges();

      expect(fixture.debugElement.query(By.css('app-page-header button'))).toBeNull();
    });

    it('withholds delete when the browsing portal is unknown', () => {
      identity.isSuperUser.set(true);
      identity.portalId.set(null);
      fixture.detectChanges();

      expect(fixture.debugElement.query(By.css('app-page-header button'))).toBeNull();
    });

    it('asks this screen’s own question, with the space before its question mark', () => {
      identity.isSuperUser.set(true);
      identity.portalId.set(0);
      fixture.detectChanges();

      fixture.debugElement.query(By.css('app-page-header button')).triggerEventHandler('click');
      fixture.detectChanges();

      const dialog = fixture.debugElement.query(By.css('app-confirm-dialog'));

      expect(dialog).not.toBeNull();
      expect((dialog.nativeElement as HTMLElement).textContent).toContain(
        'Are You Sure You Wish To Delete This Portal ?',
      );
    });

    it('deletes and leaves once confirmed', () => {
      identity.isSuperUser.set(true);
      identity.portalId.set(0);
      fixture.detectChanges();
      fixture.debugElement.query(By.css('app-page-header button')).triggerEventHandler('click');
      fixture.detectChanges();

      fixture.debugElement.query(By.css('app-confirm-dialog')).triggerEventHandler('confirm');

      expect(portals.deletePortal.calls.mostRecent().args[0]).toBe(5);

      const done = portals.deletePortal.calls.mostRecent().args[1] as () => void;
      done();

      expect(notifications.success).toHaveBeenCalledWith('The portal was deleted.');
      expect(router.navigateByUrl).toHaveBeenCalledWith('/portals');
    });

    it('deletes nothing when the confirmation is dismissed', () => {
      identity.isSuperUser.set(true);
      identity.portalId.set(0);
      fixture.detectChanges();
      fixture.debugElement.query(By.css('app-page-header button')).triggerEventHandler('click');
      fixture.detectChanges();

      fixture.debugElement.query(By.css('app-confirm-dialog')).triggerEventHandler('cancel');
      fixture.detectChanges();

      expect(portals.deletePortal).not.toHaveBeenCalled();
      expect(fixture.debugElement.query(By.css('app-confirm-dialog'))).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  describe('the fields that are gone', () => {
    beforeEach(() => {
      route('0');
      identity.isSuperUser.set(true);
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
    });

    it('declares no control for any dropped field', () => {
      const declared = Object.keys(form().controls);

      for (const absent of [
        'logoFile',
        'backgroundFile',
        'currency',
        'paymentProcessor',
        'processorUserId',
        'processorCredentialReference',
        'defaultLanguage',
        'siteLogHistory',
        'homeDirectory',
        'styleSheet',
        'sslEnabled',
        'sslEnforced',
        'sslUrl',
        'inlineEditor',
        'controlPanelMode',
      ]) {
        expect(declared).not.toContain(absent);
      }
    });

    it('renders no appearance, payment, usability or SSL section', () => {
      fixture.debugElement.queryAll(By.css('[role="tab"]'))[1]?.triggerEventHandler('click');
      fixture.detectChanges();

      const rendered = text();

      expect(rendered).not.toContain('Appearance');
      expect(rendered).not.toContain('Payment Settings');
      expect(rendered).not.toContain('Usability Settings');
      expect(rendered).not.toContain('SSL Settings');
      expect(rendered).not.toContain('Stylesheet Editor');
    });

    it('offers no wizard, template, export or import affordance', () => {
      const rendered = text().toLowerCase();

      expect(rendered).not.toContain('wizard');
      expect(rendered).not.toContain('template');
      expect(rendered).not.toContain('export');
      expect(rendered).not.toContain('import');
    });
  });

  // -------------------------------------------------------------------------
  describe('loading and empty states', () => {
    it('shows a spinner while the first read is outstanding', () => {
      portals.settingsLoading.set(true);
      route('0');

      expect(fixture.debugElement.query(By.css('app-loading-spinner'))).not.toBeNull();
    });

    it('does not show the first-read spinner once a resource is on screen', () => {
      route('0');
      portals.settings.set(settingsFixture());
      portals.settingsLoading.set(true);
      fixture.detectChanges();

      expect(fixture.debugElement.query(By.css('form'))).not.toBeNull();
    });

    it('disables both actions while a write is in flight', () => {
      route('0');
      portals.settings.set(settingsFixture());
      fixture.detectChanges();
      portals.settingsLoading.set(true);
      fixture.detectChanges();

      const actions = fixture.debugElement.queryAll(By.css('.portal-settings__actions button'));

      expect(actions.every((action) => (action.nativeElement as HTMLButtonElement).disabled)).toBeTrue();
    });
  });
});
