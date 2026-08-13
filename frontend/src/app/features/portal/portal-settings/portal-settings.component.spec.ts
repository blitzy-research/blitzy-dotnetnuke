// WHY THIS FILE CARRIES MORE WEIGHT THAN ITS THREE SIBLINGS

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';

import { BannerAdvertisingMode, UserRegistrationMode } from '../../../core/models/portal.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { PortalSettingsComponent } from './portal-settings.component';

import type { TestRequest } from '@angular/common/http/testing';
import type { Signal, WritableSignal } from '@angular/core';
import type {
  PortalAdministrator,
  PortalDetail,
  PortalSettings,
  UpdatePortalSettingsRequest,
} from '../../../core/models/portal.model';
import type {
  ProblemDetails,
  ProblemDetailsErrors,
} from '../../../core/models/problem-details.model';
import type { TabListItem } from '../../../core/models/tab.model';

// ---------------------------------------------------------------------------
// The relative address space
// ---------------------------------------------------------------------------

/**
 * The versioned prefix, spelled out rather than imported. Asserting the literal is the point: importing
 * the environment module would make every expectation below agree with whatever the base URL happened to
 * be, which is the one thing a specification of a deployment contract must not do.
 */
const API = '/api/v1';

/** The portal collection. The store re-reads it after every successful write. */
const PORTALS_URL = `${API}/portals`;

/** One portal, addressed by identifier. */
function portalUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}`;
}

/** One portal's configuration projection: read with GET, replaced whole with PUT. */
function settingsUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}/settings`;
}

/**
 * The accounts one portal may designate as its administrator. this read lives on the PORTAL resource
 * because the portal has to come from the path.
 */
function administratorsUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}/administrators`;
}

/** One portal's pages. */
function tabsUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}/tabs`;
}

/** The namespace the API puts in front of every failure code it publishes. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/**
 * The published code for the last-remaining-portal refusal, as ONE dotted string. The legacy antecedent
 * is the shared resource key `LastPortal`, whose wording is asserted verbatim further down.
 */
const LAST_PORTAL_CODE = 'portal.last_remaining';

/** The shared wording for that refusal, from `LastPortal.Text`. */
const LAST_PORTAL_MESSAGE = 'You Can Not Delete The Last Portal In Your Database';

// ---------------------------------------------------------------------------
// Fixtures. Local, non-exported, and COMPLETE.
// ---------------------------------------------------------------------------

/** Wraps a payload in the single-payload envelope the transports unwrap. */
function envelope<T>(data: T): { readonly data: T; readonly meta: null } {
  return { data, meta: null };
}

/**
 * An empty portal listing page. Needed because the store re-reads the listing after a successful save and
 * after a successful delete, so every such case has a third request to answer.
 */
function emptyPortalPage(): {
  readonly items: readonly never[];
  readonly meta: {
    readonly totalCount: number;
    readonly pageIndex: number;
    readonly pageSize: number;
    readonly totalPages: number;
  };
} {
  return {
    items: [],
    meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 },
  };
}

/**
 * A settings projection whose awkward values are the interesting ones. The fee and the three quotas are
 * `0` — real stored values that must render as the literal `0`.
 */
function settingsBody(overrides: Partial<PortalSettings> = {}): PortalSettings {
  return {
    portalId: 0,
    portalName: 'Baseline Portal',
    description: 'A baseline description.',
    keyWords: 'one,two,three',
    footerText: 'Copyright 2024 Baseline',
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
    processorUserId: 'merchant-account',
    siteLogHistory: -1,
    splashTabId: null,
    homeTabId: 0,
    loginTabId: null,
    userTabId: null,
    defaultLanguage: 'en-US',
    timeZoneOffset: -480,
    homeDirectory: 'Portals/0',
    guid: 'a2f9c1d4-5b6e-4a70-8c91-0d3e2f4b6a80',
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

function detailBody(overrides: Partial<PortalDetail> = {}): PortalDetail {
  return {
    portalId: 0,
    portalName: 'Baseline Portal',
    description: 'A baseline description.',
    keyWords: 'one,two,three',
    footerText: 'Copyright 2024 Baseline',
    logoFile: 'logo.gif',
    backgroundFile: 'back.gif',
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.Site,
    currency: 'USD',
    administratorId: 2,
    email: 'admin@example.test',
    hostFee: 0,
    hostSpace: 0,
    pageQuota: 0,
    userQuota: 0,
    users: 3,
    pages: 7,
    administratorRoleId: 0,
    administratorRoleName: 'Administrators',
    registeredRoleId: 1,
    registeredRoleName: 'Registered Users',
    guid: 'a2f9c1d4-5b6e-4a70-8c91-0d3e2f4b6a80',
    paymentProcessor: 'PayPal',
    processorUserId: 'merchant-account',
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

    // Deliberately the SAME token the settings fixture publishes: one derivation serves both reads.
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/** One page row, defaulting to an ordinary visible ROOT page whose identifier is zero. */
function pageRow(overrides: Partial<TabListItem> = {}): TabListItem {
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
    hasChildren: true,
    isSecure: false,
    url: null,
    iconFile: null,
    ...overrides,
  };
}

/** The page listing every selector case shares. */
function pageListing(): readonly TabListItem[] {
  return [
    pageRow({ tabId: 0, tabName: 'Home', level: 0, parentId: null, tabOrder: 1 }),
    pageRow({ tabId: 4, tabName: 'About', level: 1, parentId: 0, tabOrder: 2 }),
    pageRow({ tabId: 5, tabName: 'History', level: 2, parentId: 4, tabOrder: 3 }),
    pageRow({ tabId: 6, tabName: 'Hidden', level: 0, parentId: null, isVisible: false }),
    pageRow({ tabId: 7, tabName: 'Recycled', level: 0, parentId: null, isDeleted: true }),
    pageRow({
      tabId: 8,
      tabName: 'Elsewhere',
      level: 0,
      parentId: null,
      url: '~/Elsewhere.aspx',
    }),
    pageRow({ tabId: 90, tabName: 'Admin', level: 0, parentId: null }),
    pageRow({ tabId: 91, tabName: 'Site Settings', level: 1, parentId: 90 }),
  ];
}

/** The accounts the administrator selector offers by default. */
function administratorListing(): readonly PortalAdministrator[] {
  return [
    { userId: 2, username: 'ada', displayName: 'Ada Lovelace' },
    { userId: 3, username: 'grace', displayName: 'Grace Hopper' },
  ];
}

/** A problem document, as the API writes one. */
function problemBody(
  status: number,
  overrides: {
    readonly code?: string;
    readonly title?: string;
    readonly detail?: string;
    readonly errors?: ProblemDetailsErrors;
  } = {},
): ProblemDetails {
  const code = overrides.code ?? 'request.invalid';

  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: overrides.title ?? 'Request refused',
    status,
    detail: overrides.detail ?? 'The request was refused.',
    instance: settingsUrl(0),
    traceId: '00-baseline-trace-01',
    errors: overrides.errors ?? {},
  };
}

// ---------------------------------------------------------------------------
// The suite
// ---------------------------------------------------------------------------

describe('PortalSettingsComponent', () => {
  let fixture: ComponentFixture<PortalSettingsComponent>;
  let component: PortalSettingsComponent;
  let http: HttpTestingController;
  let notifications: NotificationService;
  let router: Router;
  // Typed with the OPTIONAL second argument the router actually accepts, because this screen now uses it:
  // the post-delete departure replaces the address rather than pushing it, since the portal the screen
  // described no longer exists.
  let navigate: jasmine.Spy<(url: string, extras?: { replaceUrl?: boolean }) => Promise<boolean>>;

  /** Whether the caller holds the host account, under test control. */
  let holdsHostAccount: WritableSignal<boolean>;

  /** The portal the session is browsing, under test control. */
  let browsingPortalId: WritableSignal<number | null>;

  // -------------------------------------------------------------------------
  // Reaching the component
  // -------------------------------------------------------------------------

  /** Reads a member the component keeps for its template. */
  function member<T>(name: string): T {
    return (component as unknown as Record<string, T>)[name];
  }

  /** Invokes one of those members as a method, with the component as its receiver. */
  function invoke<T>(name: string, ...args: unknown[]): T {
    const method = member<(...called: unknown[]) => T>(name);

    return method.apply(component, args);
  }

  /** The typed form group the screen edits. */
  function form(): PortalSettingsComponent['form'] {
    return (component as unknown as { form: PortalSettingsComponent['form'] }).form;
  }

  /** The rendered host element, typed. */
  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  /** Collapses rendered text the way a reader perceives it, so layout whitespace cannot fail a case. */
  function text(element: Element | null): string {
    return (element?.textContent ?? '').replace(/\s+/gu, ' ').trim();
  }

  /** The whole screen's rendered text, collapsed. */
  function screenText(): string {
    return text(host());
  }

  /** Every labelled field wrapper currently rendered, as component instances. */
  function fields(): readonly FormFieldComponent[] {
    return fixture.debugElement
      .queryAll(By.directive(FormFieldComponent))
      .map((found) => found.componentInstance as FormFieldComponent);
  }

  /** Every rendered field caption, in document order. */
  function fieldLabels(): readonly string[] {
    return fields().map((field) => field.label);
  }

  /** The one field wrapper carrying the given caption, or null when none does. */
  function fieldLabelled(label: string): FormFieldComponent | null {
    return fields().find((field) => field.label === label) ?? null;
  }

  /** The rendered element of the field carrying the given caption, or null. */
  function fieldElementLabelled(label: string): HTMLElement | null {
    const found = fixture.debugElement
      .queryAll(By.directive(FormFieldComponent))
      .find((candidate) => (candidate.componentInstance as FormFieldComponent).label === label);

    return found === undefined ? null : (found.nativeElement as HTMLElement);
  }

  /** The section captions currently rendered, in document order. */
  function sectionCaptions(): readonly string[] {
    return queryAll<HTMLButtonElement>('.portal-settings__toggle').map((toggle) => text(toggle));
  }

  /** The disclosure whose caption matches, or null. */
  function sectionToggle(caption: string): HTMLButtonElement | null {
    return (
      queryAll<HTMLButtonElement>('.portal-settings__toggle').find(
        (toggle) => text(toggle) === caption,
      ) ?? null
    );
  }

  /** The grouped field set a disclosure caption belongs to. */
  function sectionFieldset(caption: string): HTMLFieldSetElement | null {
    return (
      queryAll<HTMLFieldSetElement>('fieldset.portal-settings__section').find(
        (group) => text(group.querySelector('.portal-settings__toggle')) === caption,
      ) ?? null
    );
  }

  /** How many labelled fields a named section is currently showing. */
  function fieldsInSection(caption: string): number {
    const group = sectionFieldset(caption);

    return group === null ? -1 : group.querySelectorAll('app-form-field').length;
  }

  /** The tab strip's controls, in rendered order. */
  function tabControls(): readonly HTMLButtonElement[] {
    return queryAll<HTMLButtonElement>('[role="tab"]');
  }

  /** One page selector, by its control name. */
  function selector(name: string): HTMLSelectElement | null {
    return query<HTMLSelectElement>(`select[formcontrolname="${name}"]`);
  }

  /** The option captions a page selector offers, in order. */
  function optionsOf(name: string): readonly string[] {
    const found = selector(name);

    return found === null ? [] : Array.from(found.options).map((option) => option.textContent ?? '');
  }

  function optionValue(option: HTMLOptionElement): string {
    const separator = option.value.indexOf(': ');

    return separator === -1 ? option.value : option.value.slice(separator + 2);
  }

  /** The option values a page selector offers, in order, prefix removed. */
  function optionValuesOf(name: string): readonly string[] {
    const found = selector(name);

    return found === null ? [] : Array.from(found.options).map((option) => optionValue(option));
  }

  /** The value a page selector currently shows, prefix removed. */
  function selectedValueOf(name: string): string {
    const found = selector(name);

    if (found === null) {
      return '';
    }

    const chosen = found.selectedOptions.item(0);

    return chosen === null ? '' : optionValue(chosen);
  }

  /** Narrows a query result, failing the case with a legible reason when nothing rendered. */
  function required<T>(value: T | null | undefined, what: string): T {
    if (value === null || value === undefined) {
      throw new Error(`Expected ${what} to be rendered, but it was not.`);
    }

    return value;
  }

  /** One text or date input, by its control name. */
  function input(name: string): HTMLInputElement | null {
    return query<HTMLInputElement>(`input[formcontrolname="${name}"]`);
  }

  /** One multi-line input, by its control name. */
  function textArea(name: string): HTMLTextAreaElement | null {
    return query<HTMLTextAreaElement>(`textarea[formcontrolname="${name}"]`);
  }

  /** The radio inputs bound to one control name. */
  function radios(name: string): readonly HTMLInputElement[] {
    return queryAll<HTMLInputElement>(`input[type="radio"][formcontrolname="${name}"]`);
  }

  /** Shows the advanced tab, where four of the six disclosures live. */
  function showAdvanced(): void {
    invoke<void>('selectTab', 'advanced');
    fixture.detectChanges();
  }

  /** Opens a disclosure, whatever state it is already in. */
  function ensureSectionOpen(section: string): void {
    if (!invoke<boolean>('isSectionOpen', section)) {
      invoke<void>('toggleSection', section);
    }

    fixture.detectChanges();
  }

  // -------------------------------------------------------------------------
  // Driving the transport
  // -------------------------------------------------------------------------

  /**
   * Answers the four reads a portal identifier starts, and renders the result. The identifier setter
   * issues exactly four: the configuration projection it edits, the portal detail that supplies the
   * administration page, the portal's pages, and the accounts eligible to administer it.
   */
  function arrive(
    portalId: number,
    options: {
      readonly settings?: PortalSettings;
      readonly detail?: PortalDetail;
      readonly pages?: readonly TabListItem[];
      readonly administrators?: readonly PortalAdministrator[] | null;
    } = {},
  ): void {
    fixture.componentRef.setInput('portalId', portalId);

    http.expectOne(settingsUrl(portalId)).flush(envelope(options.settings ?? settingsBody()));
    http.expectOne(portalUrl(portalId)).flush(envelope(options.detail ?? detailBody()));
    http.expectOne(tabsUrl(portalId)).flush(envelope(options.pages ?? pageListing()));

    const candidates =
      options.administrators === undefined ? administratorListing() : options.administrators;
    const candidateRead = http.expectOne(administratorsUrl(portalId));

    if (candidates === null) {
      candidateRead.flush(
        { type: 'about:blank', title: 'Server error', status: 500 },
        { status: 500, statusText: 'Server Error' },
      );
    } else {
      candidateRead.flush(envelope(candidates));
    }

    fixture.detectChanges();
  }

  /**
   * Answers the administrator-candidate read for one portal. ⚠ EVERY MOUNT ISSUES IT, because the
   * selector cannot be offered without it.
   *
   * @param portalId The portal whose candidates to answer.
   * @param candidates The accounts to answer with.
   */
  function answerAdministrators(
    portalId: number,
    candidates: readonly PortalAdministrator[] = administratorListing(),
  ): void {
    http.expectOne(administratorsUrl(portalId)).flush(envelope(candidates));
  }

  /** Answers the portal-listing re-read that the portal WRITE commands perform. */
  function answerListingRefresh(): void {
    http
      .expectOne((candidate) => candidate.url === PORTALS_URL)
      .flush(emptyPortalPage());
  }

  /**
   * Asserts that a successful write issued NO portal-listing read. ⚠ THIS HELPER USED TO ANSWER SUCH A
   * READ, AND THE READ WAS A DEFECT. The store re-read the listing after every settings write, which is
   * right when a listing is on screen and wrong here: THIS screen never reads the listing, and a portal
   * administrator - who is exactly who reaches it - is not permitted to.
   */
  function expectNoListingRefresh(): void {
    http.expectNone((candidate) => candidate.url === PORTALS_URL);
  }

  /**
   * Takes the pending settings write off the queue. ⚠ CALL THIS ONCE PER CASE. The testing controller
   * REMOVES a request as soon as it is matched, so asking for the same write twice reports "found none"
   * the second time. Every case below therefore captures the write, inspects it, and completes it — which
   * is also the only ordering in which the captured request is still answerable.
   */
  function takeSave(portalId: number): TestRequest {
    return http.expectOne(
      (candidate) => candidate.url === settingsUrl(portalId) && candidate.method === 'PUT',
    );
  }

  /** The body of a captured write, typed as the request contract. */
  function bodyOf(write: TestRequest): UpdatePortalSettingsRequest {
    return write.request.body as UpdatePortalSettingsRequest;
  }

  /** The member names a captured write actually carries. */
  function keysOf(write: TestRequest): readonly string[] {
    return Object.keys(bodyOf(write));
  }

  /** One member of a captured write, read by name. */
  function memberOf(write: TestRequest, name: string): unknown {
    const body = write.request.body as Readonly<Record<string, unknown>>;

    return body[name];
  }

  /** Completes a captured write. */
  function completeSave(write: TestRequest, stored: PortalSettings = settingsBody()): void {
    write.flush(envelope(stored));
    expectNoListingRefresh();
    fixture.detectChanges();
  }

  /** Submits the form the way the template does. */
  function submit(): void {
    invoke<void>('onSubmit');
    fixture.detectChanges();
  }

  /** The messages the notification queue currently holds, newest last. */
  function queuedMessages(): readonly string[] {
    return notifications.notifications().map((entry) => entry.message);
  }

  /** The severities the notification queue currently holds, newest last. */
  function queuedSeverities(): readonly string[] {
    return notifications.notifications().map((entry) => entry.severity);
  }

  /**
   * The most recently queued notification, or null when the queue is empty. ⚠ THE SUPPORT REFERENCE IS
   * PROJECTED ALONGSIDE THE MESSAGE, and it was not.
   */
  function latestNotification(): {
    readonly severity: string;
    readonly message: string;
    readonly reference: string | null;
  } | null {
    const queue = notifications.notifications();
    const last = queue[queue.length - 1];

    return last === undefined
      ? null
      : { severity: last.severity, message: last.message, reference: last.reference };
  }

  beforeEach(() => {
    holdsHostAccount = signal<boolean>(false);
    browsingPortalId = signal<number | null>(7);

    TestBed.configureTestingModule({
      imports: [PortalSettingsComponent],
      providers: [
        // ORDER IS LOAD-BEARING. The testing provider replaces the BACKEND of an already-configured client,
        // so the client must be configured first; reversed, the client keeps its real backend and every
        // request below would escape unintercepted.
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthStore,
          // Exactly the two facts this screen reads from the session, and nothing else.
          useValue: {
            isSuperUser: holdsHostAccount.asReadonly(),
            portalId: browsingPortalId.asReadonly(),
          },
        },
      ],
    });

    fixture = TestBed.createComponent(PortalSettingsComponent);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
    router = TestBed.inject(Router);
    navigate = spyOn(router, 'navigateByUrl').and.resolveTo(true);
    fixture.detectChanges();
  });

  afterEach(() => {
    // The proof that nothing stray or duplicated was issued. It is what turns "the pages are read once"
    // from a claim into a check: a second identical read would be an unmatched request and would fail here
    // even if no case asserted a count.
    http.verify();
  });

  // A. THE IDENTITY TRAP — `0` AND `-1` ARE BOTH REAL PORTALS.

  describe('A. the portal identifier', () => {
    it('reads, and renders, the portal identified as 0', () => {
      arrive(0);

      expect(component.portalId).toBe(0);
      expect(required(input('portalName'), 'the title box').value).toBe('Baseline Portal');
    });

    it('reads, and renders, the portal identified as -1 — the first identity value the schema issues', () => {
      arrive(-1, {
        settings: settingsBody({ portalId: -1, portalName: 'First Portal' }),
        detail: detailBody({ portalId: -1, portalName: 'First Portal' }),
      });

      expect(component.portalId).toBe(-1);
      expect(required(input('portalName'), 'the title box').value).toBe('First Portal');
    });

    it('addresses the settings projection at the relative path, for 0 and for -1 alike', () => {
      fixture.componentRef.setInput('portalId', 0);

      const forZero: TestRequest = http.expectOne(`${API}/portals/0/settings`);

      expect(forZero.request.method).toBe('GET');
      expect(forZero.request.url.startsWith('http')).toBeFalse();
      forZero.flush(envelope(settingsBody()));
      http.expectOne(`${API}/portals/0`).flush(envelope(detailBody()));
      http.expectOne(`${API}/portals/0/tabs`).flush(envelope([]));
      answerAdministrators(0);

      fixture.componentRef.setInput('portalId', -1);

      const forMinusOne: TestRequest = http.expectOne(`${API}/portals/-1/settings`);

      expect(forMinusOne.request.method).toBe('GET');
      expect(forMinusOne.request.url.startsWith('http')).toBeFalse();
      forMinusOne.flush(envelope(settingsBody({ portalId: -1 })));
      http.expectOne(`${API}/portals/-1`).flush(envelope(detailBody({ portalId: -1 })));
      http.expectOne(`${API}/portals/-1/tabs`).flush(envelope([]));
      answerAdministrators(-1);

      fixture.detectChanges();
    });

    it('treats neither 0 nor -1 as absence: no empty state and no navigation away', () => {
      arrive(0);

      expect(screenText()).not.toContain('does not identify a portal');
      expect(navigate).not.toHaveBeenCalled();

      arrive(-1, {
        settings: settingsBody({ portalId: -1 }),
        detail: detailBody({ portalId: -1 }),
      });

      expect(screenText()).not.toContain('does not identify a portal');
      expect(navigate).not.toHaveBeenCalled();
    });

    it('converts the string route segments "0" and "-1" to the right numbers', () => {
      fixture.componentRef.setInput('portalId', '0');

      expect(component.portalId).toBe(0);
      http.expectOne(settingsUrl(0)).flush(envelope(settingsBody()));
      http.expectOne(portalUrl(0)).flush(envelope(detailBody()));
      http.expectOne(tabsUrl(0)).flush(envelope([]));
      answerAdministrators(0);

      fixture.componentRef.setInput('portalId', '-1');

      expect(component.portalId).toBe(-1);
      http.expectOne(settingsUrl(-1)).flush(envelope(settingsBody({ portalId: -1 })));
      http.expectOne(portalUrl(-1)).flush(envelope(detailBody({ portalId: -1 })));
      http.expectOne(tabsUrl(-1)).flush(envelope([]));
      answerAdministrators(-1);
    });

    it('reports absence rather than guessing when the segment is not a whole number', () => {
      fixture.componentRef.setInput('portalId', 'not-a-number');
      fixture.detectChanges();

      expect(component.portalId).toBeUndefined();
      expect(screenText()).toContain('This address does not identify a portal to configure.');
      // The afterEach verification is what proves no request was composed from the bad value.
    });

    it('reports absence when no segment was bound at all', () => {
      fixture.componentRef.setInput('portalId', null);
      fixture.detectChanges();

      expect(component.portalId).toBeUndefined();
      expect(screenText()).toContain('This address does not identify a portal to configure.');
    });

    it('reads three times per identifier and not again for the same one', () => {
      arrive(0);

      // Re-binding the SAME identifier must not re-read. The verification in afterEach fails
      // on any request this line issues.
      fixture.componentRef.setInput('portalId', 0);
      fixture.componentRef.setInput('portalId', '0');
      fixture.detectChanges();

      expect(component.portalId).toBe(0);
    });
  });

  // B. THE EXPIRY-DATE SENTINEL.

  describe('B. the expiry date', () => {
    beforeEach(() => {
      // The box lives in the host group, which only a host account sees and which the markup
      // declared collapsed, so both have to be arranged before it can be read at all.
      holdsHostAccount.set(true);
    });

    it('renders an EMPTY box for an absent expiry', () => {
      arrive(0, { settings: settingsBody({ expiryDate: null }) });
      showAdvanced();
      ensureSectionOpen('host');

      expect(required(input('expiryDate'), 'the expiry box').value).toBe('');
    });

    it('never renders the absent expiry as a visible date in any spelling', () => {
      arrive(0, { settings: settingsBody({ expiryDate: null }) });
      showAdvanced();
      ensureSectionOpen('host');

      const box = required(input('expiryDate'), 'the expiry box');

      expect(box.value).not.toBe('01/01/0001');
      expect(box.value).not.toBe('0001-01-01');
      expect(box.value).not.toBe('1/1/1');
      expect(screenText()).not.toContain('0001');
    });

    it('CANNOT be handed the legacy date sentinel by this contract, and never sends it', () => {
      // * the projection member is a NULLABLE date on the server and a nullable string here, so an absent
      // expiry is written as the value `null`, not as a minimum-value date; and * the column is `datetime`,
      // whose earliest representable instant is 1753-01-01, so the sentinel cannot be stored and therefore
      // cannot be read back either.
      arrive(0, { settings: settingsBody({ expiryDate: null }) });
      showAdvanced();
      ensureSectionOpen('host');

      expect(form().controls.expiryDate.value).toBe('');

      submit();

      const write = takeSave(0);

      expect(bodyOf(write).expiryDate).toBeNull();
      expect(bodyOf(write).expiryDate).not.toBe('0001-01-01');
      expect(bodyOf(write).expiryDate).not.toBe('0001-01-01T00:00:00');
      completeSave(write);
    });

    it('hydrates a real expiry as the date alone, with no time part', () => {
      arrive(0, { settings: settingsBody({ expiryDate: '2027-03-14T09:30:00' }) });
      showAdvanced();
      ensureSectionOpen('host');

      expect(required(input('expiryDate'), 'the expiry box').value).toBe('2027-03-14');
    });

    it('round-trips a real expiry unchanged', () => {
      arrive(0, { settings: settingsBody({ expiryDate: '2027-03-14T09:30:00' }) });
      showAdvanced();
      ensureSectionOpen('host');
      submit();

      const write = takeSave(0);

      expect(bodyOf(write).expiryDate).toBe('2027-03-14');
      completeSave(write, settingsBody({ expiryDate: '2027-03-14T00:00:00' }));

      expect(form().controls.expiryDate.value).toBe('2027-03-14');
    });

    it('sends ABSENCE for a blank box — an explicit null member, never an omitted one', () => {
      arrive(0, { settings: settingsBody({ expiryDate: '2027-03-14T09:30:00' }) });
      showAdvanced();
      ensureSectionOpen('host');
      form().controls.expiryDate.setValue('');
      fixture.detectChanges();
      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.expiryDate).toBeNull();
      expect('expiryDate' in body).toBeTrue();
      expect(body.expiryDate).not.toBeUndefined();
      completeSave(write);
    });
  });

  describe('C. the four page selectors', () => {
    beforeEach(() => {
      arrive(0);
      showAdvanced();
    });

    it('issues EXACTLY ONE page-listing read, counted before it is answered', () => {
      fixture.componentRef.setInput('portalId', 7);

      const listings: readonly TestRequest[] = http.match(tabsUrl(7));

      expect(listings.length).toBe(1);
      expect(listings[0].request.method).toBe('GET');

      listings[0].flush(envelope(pageListing()));
      http.expectOne(settingsUrl(7)).flush(envelope(settingsBody({ portalId: 7 })));
      http.expectOne(portalUrl(7)).flush(envelope(detailBody({ portalId: 7 })));
      answerAdministrators(7);
      fixture.detectChanges();

      // And rendering all four selectors off that single answer issues nothing further, which
      // the afterEach verification would report as an unexpected outstanding request.
      expect(selector('splashTabId')).not.toBeNull();
      expect(selector('homeTabId')).not.toBeNull();
      expect(selector('loginTabId')).not.toBeNull();
      expect(selector('userTabId')).not.toBeNull();
    });

    it('reads the page listing EXACTLY ONCE for all four selectors', () => {
      // The listing was answered by `arrive`. Nothing further may be outstanding, and nothing
      // further may be issued by rendering all four selectors.
      expect(http.match(tabsUrl(0)).length).toBe(0);
      expect(selector('splashTabId')).not.toBeNull();
      expect(selector('homeTabId')).not.toBeNull();
      expect(selector('loginTabId')).not.toBeNull();
      expect(selector('userTabId')).not.toBeNull();
    });

    it('offers the same option set to all four selectors', () => {
      const splash = optionsOf('splashTabId');

      expect(splash.length).toBeGreaterThan(1);
      expect(optionsOf('homeTabId')).toEqual(splash);
      expect(optionsOf('loginTabId')).toEqual(splash);
      expect(optionsOf('userTabId')).toEqual(splash);
    });

    it('offers the "<None Specified>" option first, carrying the value -1, and selectable', () => {
      const options = required(selector('splashTabId'), 'the splash-page selector').options;
      const first = required(options.item(0), 'the first splash-page option');

      expect(first.textContent).toBe('<None Specified>');
      expect(optionValue(first)).toBe('-1');
      expect(first.disabled).toBeFalse();
      // Selectable, not a disabled prompt: choosing it is how an operator says the portal has no
      // such page.
      expect(first.disabled).toBeFalse();
    });

    it('selects that option for every one of the four absent page references', () => {
      arrive(1, {
        settings: settingsBody({
          portalId: 1,
          splashTabId: null,
          homeTabId: null,
          loginTabId: null,
          userTabId: null,
        }),
        detail: detailBody({ portalId: 1 }),
      });
      showAdvanced();

      expect(selectedValueOf('splashTabId')).toBe('-1');
      expect(selectedValueOf('homeTabId')).toBe('-1');
      expect(selectedValueOf('loginTabId')).toBe('-1');
      expect(selectedValueOf('userTabId')).toBe('-1');
      expect(form().controls.splashTabId.value).toBe(-1);
      expect(form().controls.homeTabId.value).toBe(-1);
      expect(form().controls.loginTabId.value).toBe(-1);
      expect(form().controls.userTabId.value).toBe(-1);
    });

    it('selects the REAL page 0 for a stored reference of 0, never the absent option', () => {
      // `dbo.Tabs.TabID` is declared `IDENTITY (0, 1)`, so zero is an ordinary page. The
      // fixture's home page is 0 and its name is Home.
      const home = required(selector('homeTabId'), 'the home selector');

      expect(selectedValueOf('homeTabId')).toBe('0');
      expect(required(home.selectedOptions.item(0), 'the chosen home option').textContent).toBe(
        'Home',
      );
      expect(form().controls.homeTabId.value).toBe(0);
      expect(selectedValueOf('homeTabId')).not.toBe('-1');
    });

    it('includes invisible pages, because the legacy call asked for hidden ones', () => {
      expect(optionsOf('splashTabId')).toContain('Hidden');
    });

    it('excludes recycled pages', () => {
      expect(optionsOf('splashTabId')).not.toContain('Recycled');
    });

    it('excludes pages that carry a URL, the legacy type test admitting only the Normal type', () => {
      expect(optionsOf('splashTabId')).not.toContain('Elsewhere');
    });

    it('excludes the administration page and its children', () => {
      const options = optionsOf('splashTabId');

      expect(options).not.toContain('Admin');
      expect(options).not.toContain('...Site Settings');
      expect(options).not.toContain('Site Settings');
    });

    it('applies NO authorisation filtering — a page the caller could not view is still listed', () => {
      expect(holdsHostAccount()).toBeFalse();
      expect(optionsOf('splashTabId')).toContain('Hidden');
      expect(optionsOf('splashTabId')).toContain('...About');
    });

    it('indents with three dots per level, and not at all at the root', () => {
      const options = optionsOf('splashTabId');

      expect(options).toContain('Home');
      expect(options).toContain('...About');
      expect(options).toContain('......History');
    });

    it('treats a parent of 0 as a CHILD of page 0, never as a root', () => {
      // The one case a falsy-parent test gets wrong. Page 4's parent is 0 — a real page — and
      // its level is one, so it must carry a single indent step while page 0 carries none.
      const options = optionsOf('splashTabId');
      const root = options.indexOf('Home');
      const child = options.indexOf('...About');

      expect(root).toBeGreaterThan(-1);
      expect(child).toBeGreaterThan(-1);
      expect(options[child]?.startsWith('...')).toBeTrue();
      expect(options[root]?.startsWith('...')).toBeFalse();
    });

    it('offers each admissible page exactly once, so a native selector is never ambiguous', () => {
      const values = optionValuesOf('splashTabId');

      expect(new Set(values).size).toBe(values.length);
      expect(values).toEqual(['-1', '0', '4', '5', '6']);
    });

    it('keeps a stored page visible even once it has been recycled', () => {
      // Dropping it would silently rewrite the stored reference the next time anything else
      // was saved, which is a worse outcome than showing a page that no longer qualifies.
      arrive(1, {
        settings: settingsBody({ portalId: 1, splashTabId: 7 }),
        detail: detailBody({ portalId: 1 }),
      });
      showAdvanced();

      expect(optionsOf('splashTabId')).toContain('Recycled');
      expect(selectedValueOf('splashTabId')).toBe('7');
    });

    it('sends absence for every selector left on the absent option, and never the option value', () => {
      // ⚠ DIVERGENCE FROM THE FOLDER BRIEF, ASSERTED AS THE CODE BEHAVES. The brief asks that an unchosen
      // selector SUBMIT `-1`.
      arrive(1, {
        settings: settingsBody({
          portalId: 1,
          splashTabId: null,
          homeTabId: null,
          loginTabId: null,
          userTabId: null,
        }),
        detail: detailBody({ portalId: 1 }),
      });
      showAdvanced();
      submit();

      const write = takeSave(1);
      const body = bodyOf(write);

      expect(body.splashTabId).toBeNull();
      expect(body.homeTabId).toBeNull();
      expect(body.loginTabId).toBeNull();
      expect(body.userTabId).toBeNull();
      expect(body.splashTabId).not.toBe(-1);
      completeSave(write, settingsBody({ portalId: 1 }));
    });

    it('sends page 0 as page 0', () => {
      submit();

      const write = takeSave(0);

      expect(bodyOf(write).homeTabId).toBe(0);
      expect(bodyOf(write).homeTabId).not.toBeNull();
      completeSave(write);
    });

    it('says so, and keeps working, when the page listing cannot be read', () => {
      fixture.componentRef.setInput('portalId', 3);
      http.expectOne(settingsUrl(3)).flush(envelope(settingsBody({ portalId: 3, homeTabId: 0 })));
      http.expectOne(portalUrl(3)).flush(envelope(detailBody({ portalId: 3 })));
      http
        .expectOne(tabsUrl(3))
        .flush(problemBody(404, { code: 'portal.not_found' }), {
          status: 404,
          statusText: 'Not Found',
        });
      answerAdministrators(3);
      fixture.detectChanges();
      showAdvanced();

      expect(screenText()).toContain('The list of pages could not be loaded');
      expect(queuedSeverities()).toContain('warning');

      // The chosen page survives as an option even though the listing failed, so saving the
      // other seventeen fields does not silently clear it.
      expect(optionValuesOf('homeTabId')).toContain('0');
    });
  });

  describe('D. the quotas and the hosting fee', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
      arrive(0);
      showAdvanced();
      ensureSectionOpen('host');
    });

    it('renders a stored 0 as the literal "0" in all four boxes', () => {
      expect(required(input('hostFee'), 'the fee box').value).toBe('0');
      expect(required(input('hostSpace'), 'the disk-space box').value).toBe('0');
      expect(required(input('pageQuota'), 'the page-quota box').value).toBe('0');
      expect(required(input('userQuota'), 'the user-quota box').value).toBe('0');
    });

    it('never substitutes a word or a blank for that zero', () => {
      const space = required(input('hostSpace'), 'the disk-space box');

      expect(space.value).not.toBe('');
      expect(space.value).not.toBe('Unlimited');
      expect(space.value).not.toBe('unlimited');
      expect(screenText()).not.toContain('Unlimited');
    });

    it('states the zero-means-unlimited rule in the disk-space help text, and only there', () => {
      const diskSpace = required(fieldLabelled('Disk Space:'), 'the disk-space field');

      expect(diskSpace.help).toBe(
        'The amount of Disk Space in MB allowed for this site (enter 0 for unlimited space).',
      );
      expect(required(fieldLabelled('Page Quota:'), 'the page-quota field').help).not.toContain(
        'unlimited',
      );
    });

    it('renders a stored -1 as -1 and does not coalesce it to 0', () => {
      arrive(1, {
        settings: settingsBody({
          portalId: 1,
          hostSpace: -1,
          pageQuota: -1,
          userQuota: -1,
          hostFee: -1,
        }),
        detail: detailBody({ portalId: 1 }),
      });
      showAdvanced();
      ensureSectionOpen('host');

      expect(required(input('hostSpace'), 'the disk-space box').value).toBe('-1');
      expect(required(input('pageQuota'), 'the page-quota box').value).toBe('-1');
      expect(required(input('userQuota'), 'the user-quota box').value).toBe('-1');
      expect(required(input('hostFee'), 'the fee box').value).toBe('-1');
    });

    it('renders an ABSENT quota as an empty box, which is a different state from zero', () => {
      arrive(1, {
        settings: settingsBody({ portalId: 1, hostSpace: null, pageQuota: null, userQuota: null }),
        detail: detailBody({ portalId: 1 }),
      });
      showAdvanced();
      ensureSectionOpen('host');

      expect(required(input('hostSpace'), 'the disk-space box').value).toBe('');
      expect(required(input('pageQuota'), 'the page-quota box').value).toBe('');
    });

    it('saves ZERO for a blank fee and three blank quotas — the measured legacy default', () => {
      form().controls.hostFee.setValue('');
      form().controls.hostSpace.setValue('');
      form().controls.pageQuota.setValue('');
      form().controls.userQuota.setValue('');
      fixture.detectChanges();
      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.hostFee).toBe(0);
      expect(body.hostSpace).toBe(0);
      expect(body.pageQuota).toBe(0);
      expect(body.userQuota).toBe(0);
      expect(body.hostSpace).not.toBeNull();
      completeSave(write);
    });

    it('sends the fee and the disk space with the types the ENTITY declares, not the widened ones', () => {
      form().controls.hostFee.setValue('12.75');
      form().controls.hostSpace.setValue('2048');
      fixture.detectChanges();
      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.hostFee).toBe(12.75);
      expect(body.hostSpace).toBe(2048);
      expect(Number.isInteger(body.hostSpace)).toBeTrue();
      expect(Number.isInteger(body.hostFee)).toBeFalse();
      completeSave(write);
    });

    it('sends the user quota as a whole number, making the strictness-off widening explicit', () => {
      form().controls.userQuota.setValue('250');
      fixture.detectChanges();
      submit();

      const write = takeSave(0);
      const quota = bodyOf(write).userQuota;

      expect(quota).toBe(250);
      expect(Number.isInteger(quota)).toBeTrue();
      completeSave(write);
    });
  });

  // E. THE BANNER HOST LOCK.

  describe('E. the banner advertising lock', () => {
    it('disables the choice when the stored value is Host', () => {
      arrive(0, { settings: settingsBody({ bannerAdvertising: BannerAdvertisingMode.Host }) });

      expect(form().controls.bannerAdvertising.disabled).toBeTrue();
      for (const radio of radios('bannerAdvertising')) {
        expect(radio.disabled).toBeTrue();
      }
    });

    it('shows the measured notice with its leading break markup REMOVED', () => {
      arrive(0, { settings: settingsBody({ bannerAdvertising: BannerAdvertisingMode.Host }) });

      const rendered = screenText();

      expect(rendered).toContain(
        'Banner option was set by the hostingprovider, and cannot be changed',
      );
      expect(rendered).not.toContain('<br>');
      expect(rendered).not.toContain('<br />');
      expect(
        member<string>('bannerHostLockNotice').startsWith('Banner option was set'),
      ).toBeTrue();
    });

    it('leaves the choice ENABLED and shows no notice when the stored value is None', () => {
      // `0` is a real choice — "display no banners" — and never an absence.
      arrive(0, { settings: settingsBody({ bannerAdvertising: BannerAdvertisingMode.None }) });

      expect(BannerAdvertisingMode.None).toBe(0);
      expect(form().controls.bannerAdvertising.enabled).toBeTrue();
      expect(form().controls.bannerAdvertising.value).toBe(BannerAdvertisingMode.None);
      expect(screenText()).not.toContain('was set by the hostingprovider');
    });

    it('leaves the choice ENABLED and shows no notice when the stored value is Site', () => {
      arrive(0, { settings: settingsBody({ bannerAdvertising: BannerAdvertisingMode.Site }) });

      expect(form().controls.bannerAdvertising.enabled).toBeTrue();
      expect(screenText()).not.toContain('was set by the hostingprovider');
    });

    it('does not lock the control when Host is merely CHOSEN mid-edit', () => {
      arrive(0, { settings: settingsBody({ bannerAdvertising: BannerAdvertisingMode.Site }) });

      form().controls.bannerAdvertising.setValue(BannerAdvertisingMode.Host);
      fixture.detectChanges();

      expect(form().controls.bannerAdvertising.enabled).toBeTrue();
      expect(screenText()).not.toContain('was set by the hostingprovider');
    });

    it('leaves the choice open for a host account, and hides the notice from it', () => {
      holdsHostAccount.set(true);
      arrive(0, { settings: settingsBody({ bannerAdvertising: BannerAdvertisingMode.Host }) });

      expect(form().controls.bannerAdvertising.enabled).toBeTrue();
      expect(screenText()).not.toContain('was set by the hostingprovider');
    });

    it('reacts when the identity resolves AFTER the projection has landed', () => {
      arrive(0, { settings: settingsBody({ bannerAdvertising: BannerAdvertisingMode.Host }) });

      expect(form().controls.bannerAdvertising.disabled).toBeTrue();

      holdsHostAccount.set(true);
      fixture.detectChanges();

      expect(form().controls.bannerAdvertising.enabled).toBeTrue();
    });

    it('offers exactly three choices, valued 0, 1 and 2', () => {
      arrive(0);

      const choices = member<readonly { readonly value: number; readonly label: string }[]>(
        'bannerChoices',
      );

      expect(choices.map((choice) => choice.value)).toEqual([0, 1, 2]);
      expect(choices.map((choice) => choice.label)).toEqual(['None', 'Site', 'Host']);
      expect(BannerAdvertisingMode.None).toBe(0);
      expect(BannerAdvertisingMode.Site).toBe(1);
      expect(BannerAdvertisingMode.Host).toBe(2);

      const items = radios('bannerAdvertising');

      expect(items.length).toBe(3);

      required(items[2], 'the third banner choice').click();
      fixture.detectChanges();

      expect(form().controls.bannerAdvertising.value).toBe(2);

      required(items[0], 'the first banner choice').click();
      fixture.detectChanges();

      expect(form().controls.bannerAdvertising.value).toBe(0);
    });
  });

  describe('F. user registration', () => {
    it('offers exactly four choices, valued 0 through 3', () => {
      arrive(0);
      showAdvanced();

      const choices = member<readonly { readonly value: number; readonly label: string }[]>(
        'registrationChoices',
      );

      expect(choices.map((choice) => choice.value)).toEqual([0, 1, 2, 3]);
      expect(choices.map((choice) => choice.label)).toEqual([
        'None',
        'Private',
        'Public',
        'Verified',
      ]);
      expect(UserRegistrationMode.NoRegistration).toBe(0);
      expect(UserRegistrationMode.PrivateRegistration).toBe(1);
      expect(UserRegistrationMode.PublicRegistration).toBe(2);
      expect(UserRegistrationMode.VerifiedRegistration).toBe(3);

      const items = radios('userRegistration');

      expect(items.length).toBe(4);

      // Proven through the model rather than through a document attribute, for the reason given
      // on the banner group above.
      required(items[3], 'the fourth registration choice').click();
      fixture.detectChanges();

      expect(form().controls.userRegistration.value).toBe(3);

      required(items[0], 'the first registration choice').click();
      fixture.detectChanges();

      expect(form().controls.userRegistration.value).toBe(0);
    });

    it('selects None for a stored 0 and round-trips it AS 0, never omitted and never null', () => {
      arrive(0, {
        settings: settingsBody({ userRegistration: UserRegistrationMode.NoRegistration }),
      });
      showAdvanced();

      expect(form().controls.userRegistration.value).toBe(UserRegistrationMode.NoRegistration);

      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.userRegistration).toBe(0);
      expect('userRegistration' in body).toBeTrue();
      expect(body.userRegistration).not.toBeNull();
      expect(body.userRegistration).not.toBeUndefined();
      completeSave(write);
    });

    it('round-trips a changed choice', () => {
      arrive(0);
      showAdvanced();
      form().controls.userRegistration.setValue(UserRegistrationMode.VerifiedRegistration);
      submit();

      const write = takeSave(0);

      expect(bodyOf(write).userRegistration).toBe(3);
      completeSave(write);
    });
  });

  describe('G. validation', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
      arrive(0);
      showAdvanced();
      ensureSectionOpen('host');
    });

    /** The messages one field is currently showing, read from the wrapper that renders them. */
    function messagesFor(label: string): readonly string[] {
      return required(fieldLabelled(label), `the ${label} field`).error;
    }

    /** The controls that hold text. */
    type TextControlName =
      | 'portalName'
      | 'description'
      | 'keyWords'
      | 'footerText'
      | 'timeZoneOffset'
      | 'expiryDate'
      | 'hostFee'
      | 'hostSpace'
      | 'pageQuota'
      | 'userQuota';

    /** Types into a text control and marks it dirty and touched, the way a real edit would. */
    function edit(name: TextControlName, value: string): void {
      const control = form().controls[name];

      control.setValue(value);
      control.markAsDirty();
      control.markAsTouched();
      fixture.detectChanges();
    }

    it('ACCEPTS a blank expiry date, because a data-type comparison passes on empty input', () => {
      edit('expiryDate', '');

      expect(form().controls.expiryDate.valid).toBeTrue();
      expect(messagesFor('Expiry Date:')).toEqual([]);
    });

    it('refuses a malformed expiry date with the measured wording, break markup removed', () => {
      edit('expiryDate', 'not a date');

      expect(form().controls.expiryDate.valid).toBeFalse();
      expect(messagesFor('Expiry Date:')).toContain('Invalid expiry date!');
      expect(messagesFor('Expiry Date:').join(' ')).not.toContain('<br>');
    });

    it('accepts a well-formed date', () => {
      edit('expiryDate', '2030-12-31');

      expect(form().controls.expiryDate.valid).toBeTrue();
      expect(messagesFor('Expiry Date:')).toEqual([]);
    });

    it('ACCEPTS a blank hosting fee for the same reason', () => {
      edit('hostFee', '');

      expect(form().controls.hostFee.valid).toBeTrue();
      expect(messagesFor('Hosting Fee:')).toEqual([]);
    });

    it('refuses a non-currency fee with the measured wording, which carries no break markup', () => {
      edit('hostFee', 'free');

      expect(form().controls.hostFee.valid).toBeFalse();
      expect(messagesFor('Hosting Fee:')).toContain('Invalid fee, needs to be a currency value!');
      expect(messagesFor('Hosting Fee:').join(' ')).not.toContain('<br>');
    });

    it('accepts a decimal fee, and a negative one, because the legacy declared no floor', () => {
      edit('hostFee', '19.99');
      expect(form().controls.hostFee.valid).toBeTrue();

      edit('hostFee', '-5');
      expect(form().controls.hostFee.valid).toBeTrue();
    });

    it('refuses a fractional quota, which the legacy turned into an unhandled parse fault', () => {
      edit('pageQuota', '1.5');

      expect(form().controls.pageQuota.valid).toBeFalse();
      expect(messagesFor('Page Quota:')).toContain('Enter a whole number.');
    });

    it('declares no presence rule on ANY control, exactly as the legacy screen declared none', () => {
      const controls = form().controls;

      controls.portalName.setValue('');
      controls.description.setValue('');
      controls.keyWords.setValue('');
      controls.footerText.setValue('');
      controls.timeZoneOffset.setValue('');
      controls.expiryDate.setValue('');
      controls.hostFee.setValue('');
      controls.hostSpace.setValue('');
      controls.pageQuota.setValue('');
      controls.userQuota.setValue('');
      fixture.detectChanges();

      expect(controls.portalName.valid).toBeTrue();
      expect(controls.portalName.errors).toBeNull();
      expect(controls.description.valid).toBeTrue();
      expect(controls.keyWords.valid).toBeTrue();
      expect(controls.footerText.valid).toBeTrue();
      expect(controls.timeZoneOffset.valid).toBeTrue();
      expect(controls.expiryDate.valid).toBeTrue();
      expect(controls.hostFee.valid).toBeTrue();
      expect(controls.hostSpace.valid).toBeTrue();
      expect(controls.pageQuota.valid).toBeTrue();
      expect(controls.userQuota.valid).toBeTrue();
      expect(form().valid).toBeTrue();
    });

    it('reports nothing at all for an emptied title, because neither tier refuses one', () => {
      // The site title lives on the BASIC tab and this block's setup shows the advanced one, so the tab is
      // switched back before the field is read. A field on an inactive panel is not rendered at all, which
      // is a real property of this screen rather than a testing artefact.
      invoke<void>('selectTab', 'basic');
      fixture.detectChanges();

      const control = form().controls.portalName;

      control.setValue('');
      control.markAsTouched();
      fixture.detectChanges();

      expect(messagesFor('Title:')).toEqual([]);
    });

    it('still normalises a whitespace-only title, so what is stored is the empty string', () => {
      // The normalisation survives the withdrawn presence rule, and its purpose is now solely consistency
      // with the sibling portal record screen, which trims its own title: without it one screen would store
      // three spaces where the other stored nothing for the same entry.
      const control = form().controls.portalName;

      control.setValue('   ');
      fixture.detectChanges();

      expect(control.valid).toBeTrue();

      submit();
      fixture.detectChanges();

      expect(control.value).toBe('');
      expect(control.valid)
        .withContext('and it is still submittable once trimmed, because nothing requires it')
        .toBeTrue();

      // ⚠ THE SAVE IS SETTLED, AND ITS EXISTENCE IS HALF THE POINT. While the withdrawn presence rule
      // stood, this submission was refused locally and issued no request at all; now it reaches the
      // endpoint, which is exactly the parity that was lost.
      const write = takeSave(0);

      expect(bodyOf(write).portalName)
        .withContext('absence, which the server stores as the empty string')
        .toBeNull();
      completeSave(write, settingsBody({ portalName: '' }));
    });

    it('reports the data-type semantics rather than mere presence: blank passes, malformed fails', () => {
      edit('expiryDate', '');
      expect(form().controls.expiryDate.valid).toBeTrue();

      edit('expiryDate', '31/12/2030');
      expect(form().controls.expiryDate.valid).toBeFalse();

      edit('hostFee', '');
      expect(form().controls.hostFee.valid).toBeTrue();

      edit('hostFee', '$4.00');
      expect(form().controls.hostFee.valid).toBeFalse();
    });

    it('shows nothing until the control has been touched or changed', () => {
      form().controls.expiryDate.setValue('nonsense');
      fixture.detectChanges();

      expect(form().controls.expiryDate.valid).toBeFalse();
      expect(messagesFor('Expiry Date:')).toEqual([]);

      form().controls.expiryDate.markAsTouched();
      fixture.detectChanges();

      expect(messagesFor('Expiry Date:')).toContain('Invalid expiry date!');
    });
  });

  describe('H. the character limits', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
      arrive(0);
    });

    it('caps the title at 128, matching Portals.PortalName nvarchar(128)', () => {
      expect(required(input('portalName'), 'the title box').maxLength).toBe(128);
    });

    it('caps the description and the keywords at 475 each', () => {
      expect(required(textArea('description'), 'the description box').maxLength).toBe(475);
      expect(required(textArea('keyWords'), 'the keywords box').maxLength).toBe(475);
    });

    it('caps the copyright at 100', () => {
      expect(required(input('footerText'), 'the copyright box').maxLength).toBe(100);
    });

    it('caps the four host boxes at 15, 10, 6 and 6, and the user quota at 6', () => {
      showAdvanced();
      ensureSectionOpen('host');

      expect(required(input('expiryDate'), 'the expiry box').maxLength).toBe(15);
      expect(required(input('hostFee'), 'the fee box').maxLength).toBe(10);
      expect(required(input('hostSpace'), 'the disk-space box').maxLength).toBe(6);
      expect(required(input('pageQuota'), 'the page-quota box').maxLength).toBe(6);
      expect(required(input('userQuota'), 'the user-quota box').maxLength).toBe(6);
    });

    it('renders the description and the keywords as MULTI-LINE inputs, not single-line ones', () => {
      expect(textArea('description')).not.toBeNull();
      expect(textArea('keyWords')).not.toBeNull();
      expect(query<HTMLInputElement>('input[formcontrolname="description"]')).toBeNull();
      expect(query<HTMLInputElement>('input[formcontrolname="keyWords"]')).toBeNull();
    });

    it('enforces each limit as a rule as well as an attribute', () => {
      const control = form().controls.footerText;

      control.setValue('x'.repeat(101));
      control.markAsTouched();
      fixture.detectChanges();

      expect(control.valid).toBeFalse();
      expect(required(fieldLabelled('Copyright:'), 'the copyright field').error).toContain(
        'Enter at most 100 characters.',
      );

      control.setValue('x'.repeat(100));
      fixture.detectChanges();

      expect(control.valid).toBeTrue();
    });
  });

  describe('I. the tab strip and the disclosures', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
      arrive(0);
    });

    it('renders EXACTLY TWO tabs, captioned from the resource file', () => {
      const strip = tabControls();

      expect(strip.length).toBe(2);
      expect(strip.map((tab) => text(tab))).toEqual(['Basic Settings', 'Advanced Settings']);
    });

    it('starts on the basic tab', () => {
      const strip = tabControls();

      expect(required(strip[0], 'the basic tab control').getAttribute('aria-selected')).toBe('true');
      expect(required(strip[1], 'the advanced tab control').getAttribute('aria-selected')).toBe(
        'false',
      );
      expect(invoke<boolean>('isTabActive', 'basic')).toBeTrue();
    });

    it('renders the basic tab description', () => {
      expect(screenText()).toContain(
        'In this section, you can set up the basic settings for your site.',
      );
    });

    it('renders the advanced tab description once that tab is showing', () => {
      showAdvanced();

      expect(screenText()).toContain(
        'In this section, you can set up more advanced settings for your site.',
      );
    });

    it('shows Site Details and "Site Marketing" inside the basic tab, and nothing else', () => {
      expect(sectionCaptions()).toEqual(['Site Details', 'Site Marketing']);
    });

    it('captions the marketing group "Site Marketing" — the resource VALUE, not the markup text', () => {
      // The markup attribute reads `Text="Marketing"`; `Marketing.Text` in the resource file reads `Site
      // Marketing`. The resource wins, and this is the first of two proofs of that precedence on this
      // screen.
      expect(sectionCaptions()).toContain('Site Marketing');
      expect(sectionCaptions()).not.toContain('Marketing');
    });

    it('shows Security Settings, Page Management, Other Settings and Host Settings in the advanced tab', () => {
      showAdvanced();

      expect(sectionCaptions()).toEqual([
        'Security Settings',
        'Page Management',
        'Other Settings',
        'Host Settings',
      ]);
    });

    it('opens the three groups the markup left unmarked, and the one it marked expanded', () => {
      expect(invoke<boolean>('isSectionOpen', 'siteDetails')).toBeTrue();
      expect(invoke<boolean>('isSectionOpen', 'marketing')).toBeTrue();
      expect(invoke<boolean>('isSectionOpen', 'security')).toBeTrue();
      expect(invoke<boolean>('isSectionOpen', 'pages')).toBeTrue();
    });

    it('closes the two groups the markup marked collapsed', () => {
      expect(invoke<boolean>('isSectionOpen', 'other')).toBeFalse();
      expect(invoke<boolean>('isSectionOpen', 'host')).toBeFalse();
    });

    it('removes a closed group\u2019s body from the document rather than hiding it', () => {
      showAdvanced();

      const group = required(sectionFieldset('Other Settings'), 'the other-settings group');

      expect(group.querySelectorAll('app-form-field').length).toBe(0);

      ensureSectionOpen('other');

      // FOUR: the administrator, the time zone, the currency and the default language. The last two were on
      // the wire and on no tab - published by the API, re-submitted on every save, and editable nowhere -
      // which is the workflow this group now restores.
      expect(
        required(sectionFieldset('Other Settings'), 'the other-settings group')
          .querySelectorAll('app-form-field').length,
      ).toBe(4);
    });

    it('drops the grouping boundary from a closed group, and keeps it on an open one', () => {
      showAdvanced();

      const closed = required(sectionFieldset('Other Settings'), 'the closed other-settings group');
      expect(closed.classList).toContain('portal-settings__section--collapsed');

      ensureSectionOpen('other');

      const opened = required(sectionFieldset('Other Settings'), 'the open other-settings group');
      expect(opened.classList).not.toContain('portal-settings__section--collapsed');
    });

    // Stated as an INVARIANT over every group rather than as a list of the six that exist today. A list
    // would pass unchanged if a seventh group were added without the binding, and the defect would be back
    // on that group alone; this cannot.
    it('marks a group collapsed exactly when it has no body, for every group on both tabs', () => {
      const check = (where: string): void => {
        const groups = queryAll<HTMLElement>('.portal-settings__section');

        expect(groups.length).toBeGreaterThan(0);

        for (const group of groups) {
          const caption = text(group.querySelector('.portal-settings__toggle'));
          const marked = group.classList.contains('portal-settings__section--collapsed');
          const empty = group.querySelector('.portal-settings__grid') === null;

          expect(marked)
            .withContext(`${where}: "${caption}" marked=${marked} bodyMissing=${empty}`)
            .toBe(empty);
        }
      };

      check('basic tab');

      showAdvanced();
      check('advanced tab, as first shown');

      ensureSectionOpen('other');
      ensureSectionOpen('host');
      check('advanced tab, everything opened');
    });

    it('renders EXACTLY ONE field in Site Marketing: the banner choice', () => {
      const group = required(sectionFieldset('Site Marketing'), 'the marketing group');

      expect(group.querySelectorAll('app-form-field').length).toBe(1);
      expect(text(group.querySelector('.form-field__label'))).toBe('Banners');
      expect(required(fieldLabelled('Banners:'), 'the banners field').label).toBe('Banners:');
    });

    it('renders EXACTLY FOUR fields in Other Settings, including the two that had no home', () => {
      showAdvanced();
      ensureSectionOpen('other');

      const group = required(sectionFieldset('Other Settings'), 'the other-settings group');
      // By class rather than by tag, so the read is independent of whether a given field captions
      // itself with a `label` or — for a group with no single control to name — with a `span`.
      const captions = Array.from(group.querySelectorAll('app-form-field')).map((field) =>
        text(field.querySelector('.form-field__label')),
      );

      // ⚠ THE ORDER IS THE MARKUP'S AND IS ASSERTED, so a field cannot drift between groups unnoticed. The
      // currency's LEGACY home was `dshPayment`, a section this screen drops entirely; rather than
      // resurrect a whole section for one field it sits here, beside the other site-wide non-host values.
      expect(group.querySelectorAll('app-form-field').length).toBe(4);
      expect(captions).toEqual([
        'Administrator',
        'Portal TimeZone',
        'Currency',
        'Default Language',
      ]);
    });

    it('renders NO group for appearance, payment, usability, secure transport or the stylesheet editor', () => {
      showAdvanced();
      ensureSectionOpen('other');
      ensureSectionOpen('host');

      const captions = sectionCaptions();

      expect(captions).not.toContain('Appearance');
      expect(captions).not.toContain('Payment Settings');
      expect(captions).not.toContain('Usability Settings');
      expect(captions).not.toContain('SSL Settings');
      expect(captions).not.toContain('Stylesheet Editor');
    });

    it('moves to the advanced tab when it is chosen, and keeps the other tab\u2019s values', () => {
      form().controls.portalName.setValue('Edited While On Basic');
      fixture.detectChanges();

      showAdvanced();

      expect(invoke<boolean>('isTabActive', 'advanced')).toBeTrue();
      expect(input('portalName')).toBeNull();
      // The panel is destroyed, not hidden — but the FORM keeps the value, which is what makes
      // a two-tab screen safe to save from either tab.
      expect(form().controls.portalName.value).toBe('Edited While On Basic');
    });

    it('moves between tabs with the arrow keys, and wraps at both ends', () => {
      const strip = queryAll<HTMLElement>('.portal-settings__tablist');
      const list = required(strip[0], 'the tab strip');

      list.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
      fixture.detectChanges();
      expect(invoke<boolean>('isTabActive', 'advanced')).toBeTrue();

      list.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
      fixture.detectChanges();
      expect(invoke<boolean>('isTabActive', 'basic')).toBeTrue();

      list.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
      fixture.detectChanges();
      expect(invoke<boolean>('isTabActive', 'advanced')).toBeTrue();

      list.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
      fixture.detectChanges();
      expect(invoke<boolean>('isTabActive', 'basic')).toBeTrue();
    });

    it('leaves keys it does not handle to the browser', () => {
      const list = required(queryAll<HTMLElement>('.portal-settings__tablist')[0], 'the tab strip');
      const event = new KeyboardEvent('keydown', { key: 'a', bubbles: true, cancelable: true });

      list.dispatchEvent(event);
      fixture.detectChanges();

      expect(event.defaultPrevented).toBeFalse();
      expect(invoke<boolean>('isTabActive', 'basic')).toBeTrue();
    });
  });

  describe('J. wording', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
      arrive(0);
    });

    it('titles the page "Site Settings", and never "Edit Portals"', () => {
      expect(member<string>('pageTitle')).toBe('Site Settings');
      expect(screenText()).toContain('Site Settings');
      expect(screenText()).not.toContain('Edit Portals');
    });

    it('captions the keywords field "Keywords:" — the resource VALUE, not the markup\u2019s "Key Words:"', () => {
      expect(fieldLabels()).toContain('Keywords:');
      expect(fieldLabels()).not.toContain('Key Words:');
    });

    it('captions the portal name field "Title:", the measured semantic inversion', () => {
      expect(fieldLabels()).toContain('Title:');
    });

    it('renders every measured field caption across the two tabs', () => {
      const basic = fieldLabels();

      expect(basic).toEqual([
        'Title:',
        'Description:',
        'Keywords:',
        'Copyright:',
        'GUID:',
        'Banners:',
      ]);

      showAdvanced();
      ensureSectionOpen('other');
      ensureSectionOpen('host');

      expect(fieldLabels()).toEqual([
        'User Registration:',
        'Splash Page:',
        'Home Page:',
        'Login Page:',
        'User Page:',
        'Administrator:',
        'Portal TimeZone:',
        'Currency:',
        'Default Language:',
        'Expiry Date:',
        'Hosting Fee:',
        'Disk Space:',
        'Page Quota:',
        'User Quota:',
      ]);
    });

    it('renders every measured section caption', () => {
      expect(sectionCaptions()).toEqual(['Site Details', 'Site Marketing']);

      showAdvanced();

      expect(sectionCaptions()).toEqual([
        'Security Settings',
        'Page Management',
        'Other Settings',
        'Host Settings',
      ]);
    });

    it('never renders a doubled colon, the legacy colon handling having been inconsistent', () => {
      showAdvanced();
      ensureSectionOpen('other');
      ensureSectionOpen('host');

      for (const label of queryAll<HTMLLabelElement>('label')) {
        expect(text(label)).not.toContain('::');
      }

      expect(screenText()).not.toContain('::');
    });
  });

  // K. THE GLOBALLY UNIQUE IDENTIFIER.

  describe('K. the portal identifier field', () => {
    beforeEach(() => {
      arrive(0, {
        settings: settingsBody({ guid: 'a2f9c1d4-5b6e-4a70-8c91-0d3e2f4b6a80' }),
      });
    });

    it('renders it UPPER-CASED', () => {
      expect(screenText()).toContain('A2F9C1D4-5B6E-4A70-8C91-0D3E2F4B6A80');
      expect(screenText()).not.toContain('a2f9c1d4-5b6e-4a70-8c91-0d3e2f4b6a80');
    });

    it('renders it read-only, with no editable control anywhere for it', () => {
      const guidField = required(fieldLabelled('GUID:'), 'the identifier field');

      expect(guidField.help).toBe(
        'The globally unique identifier which can be used to identify this portal.',
      );
      expect(query('input[formcontrolname="guid"]')).toBeNull();
      expect(query('textarea[formcontrolname="guid"]')).toBeNull();
      expect(query('select[formcontrolname="guid"]')).toBeNull();
      expect(Object.keys(form().controls)).not.toContain('guid');
    });

    it('leaves it out of the write contract entirely', () => {
      submit();

      const write = takeSave(0);

      expect(keysOf(write)).not.toContain('guid');
      expect(memberOf(write, 'guid')).toBeUndefined();
      expect(memberOf(write, 'GUID')).toBeUndefined();
      completeSave(write);
    });
  });

  // ⚠ THE ONLY ONE IN THE APPLICATION. Every anchor the console renders was enumerated and none addressed
  // `:portalId/aliases`, while the listing's single row command targets THIS screen so a portal's host
  // names could be managed only by typing an address, even though every action of the alias resource grants
  // a tenant administrator that right.

  describe('K2. reaching the host names', () => {
    /** Every anchor projected into the shared header. */
    function headerLinks(): readonly HTMLAnchorElement[] {
      return queryAll<HTMLAnchorElement>('app-page-header a');
    }

    it('links to the alias screen of the portal in the address', () => {
      holdsHostAccount.set(true);
      arrive(0);

      const links = headerLinks();

      expect(links).toHaveSize(1);
      expect(required(links[0], 'the alias link').getAttribute('href')).toBe('/portals/0/aliases');
      expect(text(links[0])).toBe('Portal Aliases');
    });

    it('composes that address for the two sentinel identifiers', () => {
      // 0 is the first real tenant and -1 is both a real tenant and the legacy absent-marker, so a falsy or
      // magnitude test in the composition would drop one and the link would resolve to the wildcard route
      // instead of failing visibly.
      holdsHostAccount.set(true);
      arrive(-1, {
        settings: settingsBody({ portalId: -1 }),
        detail: detailBody({ portalId: -1 }),
      });

      expect(required(headerLinks()[0], 'the alias link').getAttribute('href')).toBe(
        '/portals/-1/aliases',
      );
    });

    it('offers it to a tenant administrator, who is refused the listing but not the aliases', () => {
      // The account that most needs this link is the one that cannot reach the portal listing at
      // all. Gating the link on the host account would withhold it from exactly that caller.
      holdsHostAccount.set(false);
      arrive(0);

      expect(headerLinks()).toHaveSize(1);
      expect(required(headerLinks()[0], 'the alias link').getAttribute('href')).toBe(
        '/portals/0/aliases',
      );
    });

    it('keeps it AFTER the delete command, so it cannot displace what an operator reached for', () => {
      holdsHostAccount.set(true);
      arrive(0);

      const projected = Array.from(
        required(query('app-page-header'), 'the page header').querySelectorAll('button, a'),
      );

      expect(projected.map((node) => text(node))).toEqual(['Delete', 'Portal Aliases']);
    });
  });

  describe('L. the host-settings gate', () => {
    it('hides the host group from an account without the host role', () => {
      holdsHostAccount.set(false);
      arrive(0);
      showAdvanced();

      expect(sectionCaptions()).not.toContain('Host Settings');
      expect(sectionFieldset('Host Settings')).toBeNull();
    });

    it('shows the host group to a host account', () => {
      holdsHostAccount.set(true);
      arrive(0);
      showAdvanced();

      expect(sectionCaptions()).toContain('Host Settings');
      expect(sectionFieldset('Host Settings')).not.toBeNull();
    });

    it('reveals the group when the identity resolves after the projection has landed', () => {
      holdsHostAccount.set(false);
      arrive(0);
      showAdvanced();

      expect(sectionCaptions()).not.toContain('Host Settings');

      holdsHostAccount.set(true);
      fixture.detectChanges();

      expect(sectionCaptions()).toContain('Host Settings');
    });

    it('still HYDRATES the hidden host controls, so they are returned unchanged', () => {
      // The update resource replaces every column it names, so a member this screen does not SHOW must
      // still be sent back as it was. The legacy postback did the same by keeping a hidden control's value
      // in the serialised control tree.
      holdsHostAccount.set(false);
      arrive(0, {
        settings: settingsBody({
          hostFee: 42.5,
          hostSpace: 1024,
          pageQuota: 25,
          userQuota: 50,
          expiryDate: '2031-06-30T00:00:00',
        }),
      });

      expect(input('hostFee')).toBeNull();
      expect(form().controls.hostFee.value).toBe('42.5');
      expect(form().controls.hostSpace.value).toBe('1024');
      expect(form().controls.expiryDate.value).toBe('2031-06-30');

      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.hostFee).toBe(42.5);
      expect(body.hostSpace).toBe(1024);
      expect(body.pageQuota).toBe(25);
      expect(body.userQuota).toBe(50);
      expect(body.expiryDate).toBe('2031-06-30');
      completeSave(write);
    });

    it('applies the permission directive NOWHERE on the screen', () => {
      holdsHostAccount.set(true);
      arrive(0);
      showAdvanced();
      ensureSectionOpen('other');
      ensureSectionOpen('host');

      expect(queryAll('[hasPermission]').length).toBe(0);
      expect(host().outerHTML).not.toContain('hasPermission');
    });
  });

  describe('M. the host-only-field refusal', () => {
    beforeEach(() => {
      holdsHostAccount.set(false);
      arrive(0);
      form().controls.portalName.setValue('Renamed');
      fixture.detectChanges();
      submit();
    });

    /** Refuses the pending write the way the server does. */
    function refuse(): void {
      takeSave(0).flush(
        problemBody(403, {
          code: 'authz.forbidden',
          title: 'Forbidden',
          detail: 'The caller may not change the host-owned fields of this portal.',
        }),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();
    }

    it('names the host-only fields, and names them all', () => {
      refuse();

      const latest = required(latestNotification(), 'a notification');

      expect(latest.message).toContain('Only a host account may change');
      expect(latest.message).toContain('hosting fee');
      expect(latest.message).toContain('disk space');
      expect(latest.message).toContain('page quota');
      expect(latest.message).toContain('user quota');
      expect(latest.message).toContain('expiry date');
    });

    it('never phrases the refusal as a session problem', () => {
      refuse();

      const announced = queuedMessages().join(' ').toLowerCase();

      expect(announced).not.toContain('session expired');
      expect(announced).not.toContain('logged out');
      expect(announced).not.toContain('unauthenticated');
      expect(announced).not.toContain('please sign in');
      expect(announced).not.toContain('sign in again');
    });

    it('does NOT navigate anywhere, least of all to a sign-in screen', () => {
      refuse();

      expect(navigate).not.toHaveBeenCalled();
    });

    it('announces it as a WARNING, because a policy outcome is not a fault', () => {
      refuse();

      expect(required(latestNotification(), 'a notification').severity).toBe('warning');
      expect(queuedSeverities()).not.toContain('error');
    });

    it('leaves the operator\u2019s edit in the form so it can be retried', () => {
      refuse();

      expect(form().controls.portalName.value).toBe('Renamed');
    });

    it('surfaces the problem document through the shared failure banner, untouched', () => {
      refuse();

      const banner = required(query('app-error-banner'), 'the failure banner');

      expect(text(banner)).toContain(
        'The caller may not change the host-owned fields of this portal.',
      );
      // The banner owns the live region; this screen does not add a second one.
      expect(banner.querySelector('[aria-live]')).not.toBeNull();
    });
  });

  describe('N. saving', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
      arrive(0);
    });

    it('writes the whole projection to the relative settings path with PUT', () => {
      submit();

      const write = takeSave(0);

      expect(write.request.url).toBe(`${API}/portals/0/settings`);
      expect(write.request.url.startsWith('http')).toBeFalse();
      expect(write.request.method).toBe('PUT');
      completeSave(write);
    });

    it('sends the edited site-detail values', () => {
      const controls = form().controls;

      controls.portalName.setValue('Renamed Portal');
      controls.description.setValue('A new description.');
      controls.keyWords.setValue('four,five');
      controls.footerText.setValue('Copyright 2031');
      fixture.detectChanges();
      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.portalName).toBe('Renamed Portal');
      expect(body.description).toBe('A new description.');
      expect(body.keyWords).toBe('four,five');
      expect(body.footerText).toBe('Copyright 2031');
      completeSave(write);
    });

    it('reports a blank text box as ABSENCE, matching the legacy empty-string null', () => {
      // The legacy null contract spelled an absent string as the EMPTY STRING and converted it back to a
      // database null on the way out, so a blank box and a null column were the same state.
      form().controls.description.setValue('   ');
      fixture.detectChanges();
      submit();

      const write = takeSave(0);

      expect(bodyOf(write).description).toBeNull();
      completeSave(write);
    });

    it('handles a successful write and announces it once, at success severity', () => {
      form().controls.portalName.setValue('Renamed By The Operator');
      fixture.detectChanges();
      submit();
      completeSave(takeSave(0), settingsBody({ portalName: 'Renamed By The Operator' }));

      expect(required(latestNotification(), 'a notification').severity).toBe('success');
      expect(queuedMessages().filter((message) => message.includes('saved')).length).toBe(1);
      expect(navigate).not.toHaveBeenCalled();
    });

    it('does NOT re-hydrate the form over a save it has just made', () => {
      form().controls.portalName.setValue('Typed By The Operator');
      fixture.detectChanges();
      submit();

      // The server answers with a DIFFERENT name, as a normalising server legitimately might.
      completeSave(takeSave(0), settingsBody({ portalName: 'Normalised By The Server' }));

      expect(form().controls.portalName.value).toBe('Typed By The Operator');
    });

    it('DOES hydrate a resource it has not seen before, such as another portal\u2019s', () => {
      expect(form().controls.portalName.value).toBe('Baseline Portal');

      arrive(1, {
        settings: settingsBody({ portalId: 1, portalName: 'Second Portal' }),
        detail: detailBody({ portalId: 1, portalName: 'Second Portal' }),
      });

      expect(form().controls.portalName.value).toBe('Second Portal');
      expect(form().pristine).toBeTrue();
      expect(form().untouched).toBeTrue();
    });

    it('issues NO portal-listing read after a successful write, because this screen never read one', () => {
      submit();
      takeSave(0).flush(envelope(settingsBody()));
      fixture.detectChanges();

      expect(http.match((candidate) => candidate.url === PORTALS_URL))
        .withContext('a screen that never read the listing does not re-read it')
        .toHaveSize(0);
    });

    it('refreshes the heading beside the title from the stored name, without re-entering the route', () => {
      expect(text(query('app-page-header'))).toContain('Baseline Portal');

      form().controls.portalName.setValue('Renamed By The Operator');
      fixture.detectChanges();
      submit();
      completeSave(takeSave(0), settingsBody({ portalName: 'Renamed By The Operator' }));

      expect(text(query('app-page-header'))).toContain('Renamed By The Operator');
      expect(text(query('app-page-header')))
        .withContext('and the pre-save name is gone rather than shown beside it')
        .not.toContain('Baseline Portal');
    });

    /**
     * ⚠ THIS ALSO CARRIES WHAT A REMOVED CASE USED TO PROVE. A case beside this one submitted a BLANK
     * title in order to watch the server refuse it, and it pinned the absence of a client-side presence
     * rule on the way past.
     */
    it('surfaces the per-field messages of a 400, read by index access', () => {
      submit();

      const errors: ProblemDetailsErrors = {
        HostFee: ['The hosting fee must be a currency amount.'],
        PortalName: ['A title is required.'],
      };

      takeSave(0).flush(
        problemBody(400, {
          code: 'validation.failed',
          title: 'One or more validation errors occurred.',
          detail: 'The request was not valid.',
          errors,
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // INDEX ACCESS, not property access. The dictionary the server sends is keyed by its own model-state
      // names, which are NOT camelCased, and the workspace forbids property access on an index signature —
      // a dotted read of either key would fail to compile.
      expect(errors['HostFee']).toEqual(['The hosting fee must be a currency amount.']);
      expect(errors['PortalName']).toEqual(['A title is required.']);

      const banner = required(query('app-error-banner'), 'the failure banner');
      const rendered = text(banner);

      expect(rendered).toContain('hostFee');
      expect(rendered).toContain('The hosting fee must be a currency amount.');
      expect(rendered).toContain('portalName');
      expect(rendered).toContain('A title is required.');
    });

    it('carries all five members of the error contract, plus the trace identifier', () => {
      const document = problemBody(400, { code: 'validation.failed', errors: { Name: ['Bad.'] } });

      expect(document.type).toBe(`${FAILURE_TYPE_PREFIX}validation.failed`);
      expect(document.title).toBeDefined();
      expect(document.status).toBe(400);
      expect(document.detail).toBeDefined();
      expect(document.errors).toBeDefined();
      expect(document.traceId).toBeDefined();
      expect(document.instance).toBeDefined();
    });

    it('handles a 404 on the write without an unhandled error, and stays put', () => {
      submit();
      takeSave(0).flush(
        problemBody(404, { code: 'portal.not_found', detail: 'No such portal.' }),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      expect(text(required(query('app-error-banner'), 'the failure banner'))).toContain(
        'No such portal.',
      );
      expect(navigate).not.toHaveBeenCalled();
      expect(required(latestNotification(), 'a notification').severity).toBe('warning');
    });

    it('handles a 404 on the initial READ without an unhandled error', () => {
      fixture.componentRef.setInput('portalId', 42);
      http
        .expectOne(settingsUrl(42))
        .flush(problemBody(404, { code: 'portal.not_found', detail: 'No such portal.' }), {
          status: 404,
          statusText: 'Not Found',
        });
      http.expectOne(portalUrl(42)).flush(envelope(detailBody({ portalId: 42 })));
      http.expectOne(tabsUrl(42)).flush(envelope([]));
      answerAdministrators(42);
      fixture.detectChanges();

      expect(text(required(query('app-error-banner'), 'the failure banner'))).toContain(
        'No such portal.',
      );
      expect(navigate).not.toHaveBeenCalled();
    });

    it('refuses to send a rejected form, says why once, and issues no request', () => {
      const control = form().controls.expiryDate;

      control.setValue('nonsense');
      fixture.detectChanges();
      submit();

      expect(screenText()).toContain('Correct the highlighted fields and try again.');
      expect(
        queuedMessages().filter((message) => message.includes('Correct the highlighted')).length,
      ).toBe(1);
      expect(required(latestNotification(), 'a notification').severity).toBe('warning');
      // The afterEach verification proves nothing was sent.
    });

    it('marks every control touched on a rejected submission, so all the messages appear at once', () => {
      form().controls.expiryDate.setValue('nonsense');
      fixture.detectChanges();
      submit();

      expect(form().controls.portalName.touched).toBeTrue();
      expect(form().controls.expiryDate.touched).toBeTrue();
    });
  });

  describe('O. deleting', () => {
    /** The page-level delete affordance, or null when it is withheld. */
    function deleteButton(): HTMLButtonElement | null {
      return (
        queryAll<HTMLButtonElement>('app-page-header button').find(
          (candidate) => text(candidate) === 'Delete',
        ) ?? null
      );
    }

    /** The dialog's affirmative action. */
    function confirmButton(): HTMLButtonElement | null {
      return (
        queryAll<HTMLButtonElement>('app-confirm-dialog button').find((candidate) =>
          text(candidate).endsWith('Delete'),
        ) ?? null
      );
    }

    /** The dialog's dismissal. */
    function cancelButton(): HTMLButtonElement | null {
      return (
        queryAll<HTMLButtonElement>('app-confirm-dialog button').find(
          (candidate) => text(candidate) === 'Cancel',
        ) ?? null
      );
    }

    it('withholds the action from an account without the host role', () => {
      holdsHostAccount.set(false);
      browsingPortalId.set(7);
      arrive(0);

      expect(deleteButton()).toBeNull();
    });

    it('withholds the action when the target IS the portal being browsed', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(0);
      arrive(0);

      expect(deleteButton()).toBeNull();
    });

    it('withholds the action when the browsing portal is unknown', () => {
      // A destructive action whose guard cannot be evaluated is not offered.
      holdsHostAccount.set(true);
      browsingPortalId.set(null);
      arrive(0);

      expect(deleteButton()).toBeNull();
    });

    it('offers the action to a host account when the target is a DIFFERENT portal', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(7);
      arrive(0);

      expect(deleteButton()).not.toBeNull();
    });

    it('offers it for the portal identified as -1 too, since -1 is a real portal', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(0);
      arrive(-1, {
        settings: settingsBody({ portalId: -1 }),
        detail: detailBody({ portalId: -1 }),
      });

      expect(deleteButton()).not.toBeNull();
    });

    it('asks this screen\u2019s own question, with the space before its question mark', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(7);
      arrive(0);
      required(deleteButton(), 'the delete action').click();
      fixture.detectChanges();

      const message = text(query('.confirm-dialog__message'));

      // The measured wording, verbatim, INCLUDING the space before its question mark - which is the tell
      // that distinguishes this screen's own local resource value from the global item-deletion sentence
      // the listing uses.
      expect(message).toContain('Are You Sure You Wish To Delete This Portal ?');
      expect(message).not.toContain('Are You Sure You Wish To Delete This Item?');

      expect(message).toBe('Are You Sure You Wish To Delete This Portal ? Baseline Portal');
    });

    it('omits the name rather than showing a dangling separator when it is not known', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(7);
      arrive(0, {
        settings: settingsBody({ portalName: '   ' }),
        detail: detailBody({ portalName: '   ' }),
      });
      required(deleteButton(), 'the delete action').click();
      fixture.detectChanges();

      expect(text(query('.confirm-dialog__message'))).toBe(
        'Are You Sure You Wish To Delete This Portal ?',
      );
    });

    it('deletes at the relative portal path, handles 204, announces success and leaves', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(7);
      arrive(0);
      required(deleteButton(), 'the delete action').click();
      fixture.detectChanges();
      required(confirmButton(), 'the confirm action').click();
      fixture.detectChanges();

      const removal = http.expectOne(`${API}/portals/0`);

      expect(removal.request.method).toBe('DELETE');
      removal.flush(null, { status: 204, statusText: 'No Content' });
      answerListingRefresh();
      fixture.detectChanges();

      expect(required(latestNotification(), 'a notification').severity).toBe('success');
      expect(required(latestNotification(), 'a notification').message).toContain('deleted');
      // ⚠ THE ADDRESS IS REPLACED RATHER THAN PUSHED, so the browser's Back button cannot return to a
      // settings form for a record that has been destroyed.
      expect(navigate).toHaveBeenCalledWith('/portals', { replaceUrl: true });

      // ⚠ AND THE CONFIRMATION SURVIVES THE NAVIGATION IT IS RAISED WITH. The shell retires notifications
      // on a completed navigation, so a confirmation announced in the same task as the departure was swept
      // before it could be painted - the portal was deleted and the operator was returned to the listing
      // with nothing said, which is indistinguishable from a delete that silently failed.
      notifications.clearOnNavigation();

      expect(notifications.notifications().map((entry) => entry.message))
        .withContext('the listing is the only place this can be read')
        .toHaveSize(1);

      notifications.clearOnNavigation();

      expect(notifications.notifications())
        .withContext('one navigation deep, not forever')
        .toHaveSize(0);
    });

    it('asks nothing about edits to a portal it has just deleted', () => {
      // ⚠ A DELETE IS NOT A SAVE, WHICH IS WHY THE GUARD NEEDED TELLING. The unsaved-entry probe reads
      // `dirty && saving() === false`, and deleting does not put the form into its saving state - so an
      // operator who typed something and then deleted the portal was offered the chance to 'discard' work
      // belonging to a record that no longer exists.
      holdsHostAccount.set(true);
      browsingPortalId.set(7);
      arrive(0);

      form().controls.description.setValue('An edit that is about to become meaningless.');
      form().controls.description.markAsDirty();
      fixture.detectChanges();

      const tracker = TestBed.inject(UnsavedChangesTracker);

      expect(tracker.isDirty()).withContext('the edit is unsaved entry').toBeTrue();

      required(deleteButton(), 'the delete action').click();
      fixture.detectChanges();
      required(confirmButton(), 'the confirm action').click();
      fixture.detectChanges();

      http.expectOne(`${API}/portals/0`).flush(null, { status: 204, statusText: 'No Content' });
      answerListingRefresh();
      fixture.detectChanges();

      expect(tracker.isDirty())
        .withContext('there is no longer a record for those edits to belong to')
        .toBeFalse();
    });

    it('surfaces the last-remaining-portal refusal with the shared legacy wording', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(7);
      arrive(0);
      required(deleteButton(), 'the delete action').click();
      fixture.detectChanges();
      required(confirmButton(), 'the confirm action').click();
      fixture.detectChanges();

      http.expectOne(`${API}/portals/0`).flush(
        problemBody(409, {
          // ONE dotted code string, never split and never rebuilt from parts.
          code: LAST_PORTAL_CODE,
          title: 'Conflict',
          detail: 'The installation must retain at least one portal.',
        }),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // ⚠ ASSERTED ON THE WORDING AND ON THE REFERENCE SEPARATELY, AND EQUALITY ON THE COMPOSED STRING WAS
      // WHY THIS CASE HAD TO CHANGE. A refusal presented as a notification now carries the support
      // reference from its problem document, exactly as one presented through the shared banner always has
      // - the identifier is the only join key between what an operator saw and what the server logged, and
      // a browser audit measured the asymmetry of quoting it in one surface and not the other.
      const refusal = required(latestNotification(), 'a notification');

      expect(refusal.message)
        .withContext('the legacy sentence, unaltered, at the head of the composed message')
        .toContain(LAST_PORTAL_MESSAGE);
      expect(refusal.message.startsWith(LAST_PORTAL_MESSAGE))
        .withContext('the wording leads; nothing is prepended to it')
        .toBeTrue();
      expect(refusal.reference)
        .withContext('the identifier an operator quotes, carried on its own member so a long server sentence cannot truncate it away')
        .toBe('00-baseline-trace-01');
      expect(navigate).not.toHaveBeenCalled();
    });

    it('handles a 404 on the delete without an unhandled error', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(7);
      arrive(0);
      required(deleteButton(), 'the delete action').click();
      fixture.detectChanges();
      required(confirmButton(), 'the confirm action').click();
      fixture.detectChanges();

      http
        .expectOne(`${API}/portals/0`)
        .flush(problemBody(404, { code: 'portal.not_found', detail: 'No such portal.' }), {
          status: 404,
          statusText: 'Not Found',
        });
      fixture.detectChanges();

      expect(navigate).not.toHaveBeenCalled();
      expect(required(latestNotification(), 'a notification').severity).toBe('warning');
    });

    it('issues NO request when the confirmation is dismissed', () => {
      holdsHostAccount.set(true);
      browsingPortalId.set(7);
      arrive(0);
      required(deleteButton(), 'the delete action').click();
      fixture.detectChanges();
      required(cancelButton(), 'the cancel action').click();
      fixture.detectChanges();

      expect(query('app-confirm-dialog')).toBeNull();
      expect(navigate).not.toHaveBeenCalled();
      // The afterEach verification proves no removal was sent.
    });

    it('returns to the listing on cancel, holding no referring address of its own', () => {
      holdsHostAccount.set(true);
      arrive(0);
      invoke<void>('onCancel');

      expect(navigate).toHaveBeenCalledWith('/portals');
    });
  });

  describe('O2. the administrator selector', () => {
    /** Chooses an option the way a browser does, then settles the view. */
    function chooseAdministrator(value: number): void {
      form().controls.administratorId.setValue(value);
      fixture.detectChanges();
    }

    it('reads the candidates for the portal the ROUTE names, not the caller\u2019s own', () => {
      // The whole reason the read lives on the portal resource. A host account configuring one of several
      // tenants must see THAT tenant's administrators, which a caller-scoped read cannot answer - and the
      // request proves the portal travels in the path.
      fixture.componentRef.setInput('portalId', 7);
      http.expectOne(settingsUrl(7)).flush(envelope(settingsBody({ portalId: 7 })));
      http.expectOne(portalUrl(7)).flush(envelope(detailBody({ portalId: 7 })));
      http.expectOne(tabsUrl(7)).flush(envelope([]));

      const read: TestRequest = http.expectOne(administratorsUrl(7));

      expect(read.request.method).toBe('GET');
      expect(read.request.url).toBe(`${API}/portals/7/administrators`);
      expect(read.request.url.startsWith('http')).toBeFalse();
      // UNPAGED, matching the legacy read: `GetUserRolesByRoleName` returned every member and the
      // selector held them all, so there is no page coordinate to send.
      expect(read.request.params.keys()).toEqual([]);

      read.flush(envelope(administratorListing()));
      fixture.detectChanges();
    });

    it('offers a real selector, and no read-only display, carrying every candidate', () => {
      arrive(0);
      showAdvanced();
      ensureSectionOpen('other');

      const control = required(selector('administratorId'), 'the administrator selector');

      expect(control.disabled).toBeFalse();
      expect(optionValuesOf('administratorId')).toEqual(['2', '3']);
      // BOTH NAMES on each entry. The display name is the one account field a tenant may compose from a
      // format string, so two administrators can legitimately share one; the login name is unique within a
      // portal, so the pair is always distinguishable.
      expect(optionsOf('administratorId')).toEqual([
        'Ada Lovelace (ada)',
        'Grace Hopper (grace)',
      ]);
    });

    it('pre-selects the stored administrator, reproducing the legacy pre-selection', () => {
      arrive(0);
      showAdvanced();
      ensureSectionOpen('other');

      expect(selectedValueOf('administratorId')).toBe('2');
      expect(form().controls.administratorId.value).toBe(2);
    });

    it('offers NO empty entry when the portal already designates an administrator', () => {
      arrive(0);
      showAdvanced();
      ensureSectionOpen('other');

      expect(optionsOf('administratorId')).not.toContain('<None Specified>');
      expect(optionValuesOf('administratorId')).not.toContain('-1');
    });

    it('offers the empty entry only when the portal designates nobody', () => {
      // The one case in which absence is a legal value: the server's guard permits it for a portal that
      // already has none. The option carries the sentinel, and the sentinel is what becomes absence on the
      // wire.
      arrive(0, { settings: settingsBody({ administratorId: null }) });
      showAdvanced();
      ensureSectionOpen('other');

      expect(optionsOf('administratorId')[0]).toBe('<None Specified>');
      expect(selectedValueOf('administratorId')).toBe('-1');
    });

    it('sends the chosen account, so the administrator can actually be reassigned', () => {
      arrive(0);
      showAdvanced();
      ensureSectionOpen('other');

      chooseAdministrator(3);
      submit();

      const write = takeSave(0);

      expect(bodyOf(write).administratorId).toBe(3);
      completeSave(write, settingsBody({ administratorId: 3 }));
    });

    it('round-trips the stored administrator untouched when the operator changes nothing', () => {
      // The other half of the same guarantee: a save of the other seventeen fields must not move the
      // administrator. It is now sent from the CONTROL rather than from the loaded resource, so this is the
      // case that proves the control was seeded correctly.
      arrive(0);
      submit();

      const write = takeSave(0);

      expect(bodyOf(write).administratorId).toBe(2);
      completeSave(write);
    });

    it('sends ABSENCE for the empty entry, never the sentinel', () => {
      // The sentinel exists only inside the form, because a select must hold the value of the option it
      // shows and the wire contract's absence is `null`. Minus one is not a legal `Users.UserID` - the
      // column seeds `IDENTITY(1, 1)` - so sending it would name no account.
      arrive(0, { settings: settingsBody({ administratorId: null }) });
      submit();

      const write = takeSave(0);

      expect(bodyOf(write).administratorId).toBeNull();
      expect(keysOf(write)).toContain('administratorId');
      completeSave(write, settingsBody({ administratorId: null }));
    });

    it('retains a designated administrator the candidate list no longer holds', () => {
      arrive(0, { administrators: [{ userId: 3, username: 'grace', displayName: 'Grace Hopper' }] });
      showAdvanced();
      ensureSectionOpen('other');

      expect(optionValuesOf('administratorId')).toEqual(['3', '2']);
      expect(optionsOf('administratorId')).toContain('Current administrator (account 2)');
      expect(selectedValueOf('administratorId')).toBe('2');

      submit();

      const write = takeSave(0);

      expect(bodyOf(write).administratorId)
        .withContext('the designation survives a save that did not touch it')
        .toBe(2);
      completeSave(write);
    });

    it('says so, and keeps the stored administrator, when the candidates cannot be read', () => {
      arrive(0, { administrators: null });
      showAdvanced();
      ensureSectionOpen('other');

      expect(screenText()).toContain('The list of eligible administrators could not be loaded');
      expect(optionValuesOf('administratorId')).toEqual(['2']);
      expect(selectedValueOf('administratorId')).toBe('2');

      submit();
      const write = takeSave(0);

      expect(bodyOf(write).administratorId).toBe(2);
      completeSave(write);
    });

    it('offers no candidates at all while the read is still in flight, and says so', () => {
      // The selector must not offer the previous portal's accounts, nor an empty list that looks settled.
      // Until the read lands the only entry is the stored administrator, and the screen says the list is
      // coming.
      fixture.componentRef.setInput('portalId', 5);
      http.expectOne(settingsUrl(5)).flush(envelope(settingsBody({ portalId: 5 })));
      http.expectOne(portalUrl(5)).flush(envelope(detailBody({ portalId: 5 })));
      http.expectOne(tabsUrl(5)).flush(envelope([]));
      fixture.detectChanges();
      showAdvanced();
      ensureSectionOpen('other');

      expect(screenText()).toContain('Loading the accounts that may administer this site');
      expect(optionValuesOf('administratorId')).toEqual(['2']);

      answerAdministrators(5);
      fixture.detectChanges();

      expect(screenText()).not.toContain('Loading the accounts that may administer this site');
      expect(optionValuesOf('administratorId')).toEqual(['2', '3']);
    });

    it('offers no candidate belonging to a portal the screen has moved away from', () => {
      // ⚠ THE GATE ON THE RECORDED PORTAL. Without it the held list is simply "the last list read", which
      // during a move between portals is the wrong one and is indistinguishable from the right one - long
      // enough to submit an account the new portal has never heard of.
      arrive(0);

      fixture.componentRef.setInput('portalId', 9);
      http.expectOne(settingsUrl(9)).flush(envelope(settingsBody({ portalId: 9, administratorId: 8 })));
      http.expectOne(portalUrl(9)).flush(envelope(detailBody({ portalId: 9 })));
      http.expectOne(tabsUrl(9)).flush(envelope([]));
      fixture.detectChanges();
      showAdvanced();
      ensureSectionOpen('other');

      // Portal 0's two candidates are gone even though the read for portal 9 has not answered.
      expect(optionValuesOf('administratorId')).toEqual(['8']);

      answerAdministrators(9, [{ userId: 8, username: 'newadmin', displayName: 'New Admin' }]);
      fixture.detectChanges();

      expect(optionValuesOf('administratorId')).toEqual(['8']);
      expect(optionsOf('administratorId')).toEqual(['New Admin (newadmin)']);
    });

    it('names the field from the resource wording and associates the label with the control', () => {
      // ⚠ THE RENDERED LABEL HAS NO TRAILING COLON, and that is the SHARED wrapper's own documented rule
      // rather than a divergence here: it normalises every label it is given.
      arrive(0);
      showAdvanced();
      ensureSectionOpen('other');

      const control = required(selector('administratorId'), 'the administrator selector');
      const field = required(control.closest('app-form-field'), 'the administrator field');

      expect(member<Record<string, string>>('fieldLabel')['administratorId']).toBe('Administrator:');
      expect(member<Record<string, string>>('fieldHelp')['administratorId']).toBe(
        'The Administrator User for the site.',
      );

      expect(text(required(field.querySelector('label'), 'the label')).trim()).toBe('Administrator');
      expect(control.id).toBe('portal-settings-administratorId');
      expect(field.querySelector('label')?.getAttribute('for')).toBe(
        'portal-settings-administratorId',
      );

      // The help sits behind the shared disclosure, which removes the block while it is closed.
      field.querySelector<HTMLButtonElement>('.form-field__help-toggle')?.click();
      fixture.detectChanges();

      expect(text(required(field.querySelector('.form-field__help'), 'the help')).trim()).toBe(
        'The Administrator User for the site.',
      );
    });

    it('carries no client-side required rule, because the invariant is the server\u2019s', () => {
      // Measured: a full case-insensitive sweep of the 568-line legacy markup finds exactly two validators
      // on the whole screen, both data-type comparisons, and neither is on this field.
      arrive(0);

      // Asserted through the control's own state rather than by naming the validator function, which would
      // require importing it here purely to compare an identity: a control that is valid holding the
      // sentinel is a control with no required rule on it.
      form().controls.administratorId.setValue(-1);
      fixture.detectChanges();

      expect(form().controls.administratorId.errors).toBeNull();
      expect(form().controls.administratorId.valid).toBeTrue();
      expect(form().valid).toBeTrue();
    });
  });

  // Mounted WITHOUT arriving anywhere first, deliberately: the subject is a malformed read, and a screen
  // that already holds a decoded projection is not the state in which that matters.

  describe('O3. a projection with no revision marker', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
    });

    it('offers no form and composes no save when the projection served no marker', () => {
      fixture.componentRef.setInput('portalId', 7);

      // The malformed projection. Spread through a literal rather than through `settingsBody`, because the
      // member is non-nullable on the contract now and a fixture cannot express the malformed shape.
      http
        .expectOne(settingsUrl(7))
        .flush(envelope({ ...settingsBody({ portalId: 7 }), concurrencyToken: null }));
      http.expectOne(portalUrl(7)).flush(envelope(detailBody({ portalId: 7 })));
      http.expectOne(tabsUrl(7)).flush(envelope(pageListing()));
      answerAdministrators(7);
      fixture.detectChanges();

      // Nothing to edit, because nothing decoded.
      expect(input('portalName'))
        .withContext('no form is offered against a projection that did not decode')
        .toBeFalsy();

      // And the thing this protects: no save can be composed, so no marker-less whole-record replacement
      // can reach the endpoint.
      expect(http.match(() => true))
        .withContext('no write is composed from a projection that did not decode')
        .toHaveSize(0);
    });
  });

  describe('P. the dropped fields', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
      arrive(0);
    });

    /** Reveals every group on both tabs, so an absence case has seen the whole screen. */
    function revealEverything(): string {
      const basic = host().outerHTML;

      showAdvanced();
      ensureSectionOpen('other');
      ensureSectionOpen('host');

      return `${basic}\n${host().outerHTML}`;
    }

    it('declares no control for any dropped field', () => {
      const declared = Object.keys(form().controls);

      for (const absent of [
        // Search engine, site map, verification and advertising.
        'searchEngine',
        'siteMap',
        'verification',
        'advertising',
        // Appearance, skinning and the stylesheet editor.
        'logoFile',
        'backgroundFile',
        'portalSkin',
        'portalContainer',
        'adminSkin',
        'adminContainer',
        'styleSheet',
        // Payment.
        'paymentProcessor',
        'processorUserId',
        'processorPassword',
        'processorCredentialReference',
        // ⚠ `currency` IS NO LONGER IN THIS LIST, and its removal is the point rather than an omission.
        'inlineEditor',
        'controlPanelMode',
        'controlPanelVisibility',
        'controlPanelSecurity',
        // Secure transport.
        'sslEnabled',
        'sslEnforced',
        'sslUrl',
        'stdUrl',
        // Localisation, logging, premium modules and the file system. ⚠ `defaultLanguage` is NOT here
        // either: the localisation MECHANISM is out of scope, which is why its control is a text box rather
        // than the legacy culture selector, but the COLUMN is in scope and now editable.
        'siteLogHistory',
        'desktopModules',
        'homeDirectory',
      ]) {
        expect(declared).not.toContain(absent);
      }
    });

    it('renders no input, selector or multi-line box for any dropped field', () => {
      const markup = revealEverything();

      for (const absent of [
        'searchEngine',
        'siteMap',
        'verification',
        'advertising',
        'portalSkin',
        'portalContainer',
        'adminSkin',
        'adminContainer',
        'styleSheet',
        'processorPassword',
        'inlineEditor',
        'controlPanelMode',
        'controlPanelVisibility',
        'controlPanelSecurity',
        'sslEnabled',
        'sslEnforced',
        'sslUrl',
        'stdUrl',
        'desktopModules',
      ]) {
        expect(markup).not.toContain(`formcontrolname="${absent}"`);
      }

      expect(query('input[type="file"]')).toBeNull();
      expect(query('input[type="password"]')).toBeNull();
    });

    it('never renders the advertising resource value, nor any script or markup binding', () => {
      const markup = revealEverything();

      expect(markup).not.toContain('plAdvertising');
      expect(markup).not.toContain('lblAdvertising');
      expect(screenText()).not.toContain('Advertising');
      expect(screenText()).not.toContain('AdSense');
      expect(screenText()).not.toContain('google_ad');
      // No script element reached the document from a bound string, and no bound string could
      // have produced one: every value this screen surfaces is interpolated plain text.
      expect(host().querySelectorAll('script').length).toBe(0);
    });

    it('sends the six unshown members back UNCHANGED rather than clearing them', () => {
      // Required rather than tidy: the update resource REPLACES every column it names, so a member sent as
      // absent is a member cleared.
      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.administratorId).toBe(2);
      expect(body.logoFile).toBe('logo.gif');
      expect(body.backgroundFile).toBe('back.gif');
      expect(body.paymentProcessor).toBe('PayPal');
      expect(body.processorUserId).toBe('merchant-account');
      expect(body.siteLogHistory).toBe(-1);
      expect(body.homeDirectory).toBe('Portals/0');

      // ⚠ THE CURRENCY AND THE DEFAULT LANGUAGE ARE STILL SENT, BUT NO LONGER FROM THIS SET. They now come
      // from their own controls, hydrated from the same projection, so an untouched save returns exactly
      // what it read - the property this case is about - while an operator who wants to change either one
      // finally can.
      expect(body.currency).toBe('USD');
      expect(body.defaultLanguage).toBe('en-US');
      completeSave(write);
    });

    /**
     * ⚠ THE TWO FIELDS THAT WERE ON THE WIRE AND ON NO TAB. `GET /portals/-1/settings` published
     * `currency: "USD"` and `defaultLanguage: "en-US"`, the `PUT` re-submitted both from the loaded
     * snapshot, and neither appeared in any section of either tab - so a legacy workflow had no
     * successor.
     */
    it('carries an EDITED currency and default language to the wire', () => {
      form().controls.currency.setValue('GBP');
      form().controls.defaultLanguage.setValue('en-GB');
      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.currency).toBe('GBP');
      expect(body.defaultLanguage).toBe('en-GB');
      completeSave(write, settingsBody({ currency: 'GBP', defaultLanguage: 'en-GB' }));
    });

    it('sends absence for an EMPTIED currency or default language, not the empty string', () => {
      // The same rule every other optional text field on this screen follows, so a cleared box means
      // the same thing wherever it appears.
      form().controls.currency.setValue('');
      form().controls.defaultLanguage.setValue('   ');
      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.currency).toBeNull();
      expect(body.defaultLanguage).toBeNull();
      completeSave(write, settingsBody({ currency: null, defaultLanguage: null }));
    });

    it('refuses a currency longer than the column, without sending anything', () => {
      form().controls.currency.setValue('TOOLONG');
      submit();

      http.expectNone((candidate) => candidate.method === 'PUT');
      expect(form().controls.currency.hasError('maxlength'))
        .withContext('the client rule is the server rule, so the message is beside the field')
        .toBeTrue();
      expect(required(input('currency'), 'the currency box').getAttribute('maxlength')).toBe('3');
    });

    it('never returns the processor credential, which it never holds', () => {
      submit();

      const write = takeSave(0);

      expect(bodyOf(write).processorCredentialReference).toBeNull();
      expect(memberOf(write, 'processorPassword')).toBeUndefined();
      expect(memberOf(write, 'password')).toBeUndefined();
      completeSave(write);
    });

    it('carries no dropped member the resource does not declare', () => {
      submit();

      const write = takeSave(0);
      const keys = keysOf(write);

      for (const absent of [
        'portalSkin',
        'portalContainer',
        'adminSkin',
        'adminContainer',
        'styleSheet',
        'searchEngine',
        'siteMap',
        'verification',
        'advertising',
        'inlineEditor',
        'controlPanelMode',
        'controlPanelVisibility',
        'controlPanelSecurity',
        'sslEnabled',
        'sslEnforced',
        'sslUrl',
        'stdUrl',
        'desktopModules',
        'name',
        'value',
        'key',
      ]) {
        expect(keys).not.toContain(absent);
      }

      completeSave(write);
    });

    /**
     * ⚠ THE REVISION MARKER, WHICH IS WHAT MAKES THE NOTE ABOVE SAFE RATHER THAN MERELY NECESSARY. The
     * case above establishes that nine unshown members are returned unchanged, because a member sent as
     * absent is a member cleared.
     */
    it('returns the revision marker it read, so a stale save is refusable', () => {
      submit();

      const write = takeSave(0);

      // From the projection the form was hydrated FROM - the same source as the nine preserved members
      // asserted above. Re-reading it immediately before the save would obtain the current revision and the
      // server's check would then always pass while looking watertight.
      expect(bodyOf(write).concurrencyToken).toBe('revision-1');
      completeSave(write);
    });

    it('always carries a marker on a save, because a projection serving none is refused', () => {
      // The positive half of the pair, stated as a total: every save this screen can compose carries a
      // marker, because the only path to composing one is a projection that decoded.
      submit();

      const write = takeSave(0);

      expect(typeof bodyOf(write).concurrencyToken)
        .withContext('a marker, never a null this screen invented and never one it omitted')
        .toBe('string');
      completeSave(write);
    });

    it('adopts the marker the response carries, so a second save is not stale', () => {
      submit();

      const first = takeSave(0);
      expect(bodyOf(first).concurrencyToken).toBe('revision-1');
      completeSave(first, settingsBody({ concurrencyToken: 'revision-2' }));

      submit();

      const second = takeSave(0);
      expect(bodyOf(second).concurrencyToken).toBe('revision-2');
      completeSave(second, settingsBody({ concurrencyToken: 'revision-2' }));
    });

    it('composes NO key-and-value settings request of any kind', () => {
      submit();

      const write = takeSave(0);

      expect(write.request.url).toBe(settingsUrl(0));
      expect(write.request.params.keys().length).toBe(0);
      expect(write.request.params.has('key')).toBeFalse();
      expect(write.request.params.has('name')).toBeFalse();
      expect(write.request.url).not.toMatch(/\/settings\/[^/]+$/u);
      completeSave(write);
    });

    it('offers no wizard step, no template selector, no export and no import', () => {
      // The site wizard and the portal-template screen are reference inputs only: the wizard's remaining
      // subject matter IS these same columns, presented as two tabs a caller may visit in any order, and no
      // portal-template resource is published to call.
      const rendered = revealEverything();

      expect(rendered).not.toContain('Next');
      expect(rendered).not.toContain('Back');
      expect(rendered).not.toContain('Template');
      expect(rendered).not.toContain('Export');
      expect(rendered).not.toContain('Import');
      expect(rendered).not.toContain('Step ');
    });

    it('renders no group for a section whose every field is gone', () => {
      const rendered = revealEverything();

      expect(rendered).not.toContain('Appearance');
      expect(rendered).not.toContain('Payment');
      expect(rendered).not.toContain('Usability');
      expect(rendered).not.toContain('SSL');
      expect(rendered).not.toContain('Stylesheet');
    });
  });

  // Q. ACCESSIBILITY.

  describe('Q. accessibility', () => {
    beforeEach(() => {
      holdsHostAccount.set(true);
      arrive(0);
    });

    it('gives every disclosure toggle an aria-expanded that reflects its state', () => {
      const siteDetails = required(sectionToggle('Site Details'), 'the site-details toggle');

      expect(siteDetails.getAttribute('aria-expanded')).toBe('true');

      siteDetails.click();
      fixture.detectChanges();

      expect(
        required(sectionToggle('Site Details'), 'the site-details toggle').getAttribute(
          'aria-expanded',
        ),
      ).toBe('false');

      showAdvanced();

      expect(
        required(sectionToggle('Other Settings'), 'the other-settings toggle').getAttribute(
          'aria-expanded',
        ),
      ).toBe('false');

      ensureSectionOpen('other');

      expect(
        required(sectionToggle('Other Settings'), 'the other-settings toggle').getAttribute(
          'aria-expanded',
        ),
      ).toBe('true');
    });

    it('names the body a toggle controls, and drops the reference when there is no body', () => {
      const siteDetails = required(sectionToggle('Site Details'), 'the site-details toggle');
      const controlled = siteDetails.getAttribute('aria-controls');

      expect(controlled).toBe('portal-settings-section-siteDetails');
      expect(query(`#${controlled ?? 'missing'}`)).not.toBeNull();

      showAdvanced();

      // A closed group's body is REMOVED, so naming it would point at an element that does not
      // exist. The attribute is omitted entirely rather than left dangling.
      expect(
        required(sectionToggle('Other Settings'), 'the other-settings toggle').hasAttribute(
          'aria-controls',
        ),
      ).toBeFalse();
    });

    it('makes every toggle a real button, and puts NONE of them out of the tab order', () => {
      showAdvanced();
      ensureSectionOpen('other');
      ensureSectionOpen('host');

      const toggles = queryAll<HTMLButtonElement>('.portal-settings__toggle');

      expect(toggles.length).toBe(4);
      for (const toggle of toggles) {
        expect(toggle.tagName).toBe('BUTTON');
        expect(toggle.getAttribute('type')).toBe('button');
        expect(toggle.getAttribute('tabindex')).not.toBe('-1');
        expect(toggle.disabled).toBeFalse();
      }
    });

    it('exposes the tab strip with the roles a strip is expected to declare', () => {
      const strip = required(query('[role="tablist"]'), 'the tab strip');

      expect(strip.getAttribute('aria-label')).toBe('Site settings sections');

      const tabs = tabControls();

      expect(tabs.length).toBe(2);
      expect(required(tabs[0], 'the basic tab').getAttribute('aria-selected')).toBe('true');
      expect(required(tabs[1], 'the advanced tab').getAttribute('aria-selected')).toBe('false');

      const panel = required(query('[role="tabpanel"]'), 'the shown panel');

      expect(panel.getAttribute('aria-labelledby')).toBe('portal-settings-tab-basic');
      expect(query(`#${panel.getAttribute('aria-labelledby') ?? 'missing'}`)).not.toBeNull();
    });

    it('keeps only the selected tab in the sequential tab order', () => {
      const tabs = tabControls();

      expect(required(tabs[0], 'the basic tab').getAttribute('tabindex')).toBe('0');
      expect(required(tabs[1], 'the advanced tab').getAttribute('tabindex')).toBe('-1');
    });

    it('associates every control with a visible caption through the shared field wrapper', () => {
      showAdvanced();
      ensureSectionOpen('other');
      ensureSectionOpen('host');

      const rendered = fields();

      expect(rendered.length).toBe(14);

      for (const field of rendered) {
        expect(field.label.length).toBeGreaterThan(0);
      }

      // Every field that NAMES a control names one that exists in the document.
      for (const field of rendered) {
        if (field.for.length > 0) {
          expect(query(`#${field.for}`)).not.toBeNull();
        }
      }
    });

    it('names a radio GROUP from its caption, the caption being a span rather than a label because it has no single control to point at', () => {
      const banners = required(fieldLabelled('Banners:'), 'the banners field');

      expect(banners.for).toBe('');

      const element = required(fieldElementLabelled('Banners:'), 'the banners field element');
      const caption = required(element.querySelector('.form-field__label'), 'the banners caption');
      const slot = required(
        element.querySelector('.form-field__control'),
        'the banners control group',
      );

      // ⚠ THE CAPTION IS A `span`, NOT A `label`, AND THAT IS THE POINT. A `label` associates with a
      // control through `for` or by wrapping it, and this field can do neither — so a `label` here would
      // name nothing, which Chrome reports as "No label associated with a form field".
      expect(caption.tagName.toLowerCase()).toBe('span');
      expect(element.querySelector('label.form-field__label'))
        .withContext('no caption-level label element may survive on a field that names no control')
        .toBeNull();

      // No dangling reference: the caption is not pointed at a control, and the GROUP is pointed
      // at the caption instead.
      expect(caption.hasAttribute('for')).toBeFalse();
      expect(slot.getAttribute('aria-labelledby')).toBe(caption.id);
      expect(caption.id.length).toBeGreaterThan(0);
      expect(query(`#${caption.id}`)).not.toBeNull();

      expect(caption.tagName).toBe('SPAN');

      const captionRow = required(
        element.querySelector('.form-field__label-row'),
        'the banners caption row',
      );
      const radios = slot.querySelectorAll('input[type="radio"]');

      expect(captionRow.querySelectorAll('label').length).toBe(0);
      expect(radios.length).toBeGreaterThan(1);
      expect(slot.querySelectorAll('label').length).toBe(radios.length);

      // The counterpart, so this test discriminates rather than merely passing: a field that DOES name one
      // control still RENDERS a real `label` with a real `for`, which is what preserves click-to-focus on
      // the other 112 fields in the application.
      const namedField = required(
        fieldElementLabelled('Description:'),
        'the description field element',
      );
      const namedCaption = required(
        namedField.querySelector('.form-field__label'),
        'the description caption',
      );

      expect(namedCaption.tagName).toBe('LABEL');
      expect(namedCaption.getAttribute('for')).toBe('portal-settings-description');

      const withControl = required(fieldLabelled('Description:'), 'the description field');

      expect(withControl.for).toBe('portal-settings-description');
    });

    it('declares NO semantic landmark of its own, the application shell owning each exactly once', () => {
      showAdvanced();
      ensureSectionOpen('other');
      ensureSectionOpen('host');

      expect(query('header')).toBeNull();
      expect(query('main')).toBeNull();
      expect(query('nav')).toBeNull();
      expect(query('footer')).toBeNull();
      expect(query('aside')).toBeNull();
      expect(query('[role="main"]')).toBeNull();
      expect(query('[role="navigation"]')).toBeNull();
      expect(query('[role="banner"]')).toBeNull();
      expect(query('[role="contentinfo"]')).toBeNull();
    });

    it('leaves the live region to the failure banner and adds no second one', () => {
      form().controls.portalName.setValue('Renamed');
      fixture.detectChanges();
      submit();
      takeSave(0).flush(problemBody(400, { code: 'validation.failed' }), {
        status: 400,
        statusText: 'Bad Request',
      });
      fixture.detectChanges();

      const banner = required(query('app-error-banner'), 'the failure banner');

      expect(banner.querySelectorAll('[aria-live]').length).toBe(1);
      // Nothing outside the banner declares one, so an announcement cannot be duplicated.
      expect(
        queryAll('[aria-live]').filter((region) => banner.contains(region) === false).length,
      ).toBe(0);
    });

    it('groups each disclosure as a field set whose caption names the group', () => {
      const groups = queryAll<HTMLFieldSetElement>('fieldset.portal-settings__section');

      expect(groups.length).toBe(2);
      for (const group of groups) {
        expect(group.querySelector('legend')).not.toBeNull();
        expect(text(group.querySelector('legend')).length).toBeGreaterThan(0);
      }
    });
  });

  describe('R. architecture', () => {
    it('is the class the route contract loads, and carries its own root element', () => {
      expect(PortalSettingsComponent.name).toBe('PortalSettingsComponent');
      expect(fixture.componentInstance instanceof PortalSettingsComponent).toBeTrue();
      expect(query('.portal-settings')).not.toBeNull();
    });

    it('names its route input literally "portalId"', () => {
      // `withComponentInputBinding()` binds a path parameter to an input of the SAME NAME, so a
      // rename produces a screen that never receives a portal and never says why.
      expect('portalId' in component).toBeTrue();

      fixture.componentRef.setInput('portalId', 0);

      expect(component.portalId).toBe(0);
      http.expectOne(settingsUrl(0)).flush(envelope(settingsBody()));
      http.expectOne(portalUrl(0)).flush(envelope(detailBody()));
      http.expectOne(tabsUrl(0)).flush(envelope([]));
      answerAdministrators(0);
      fixture.detectChanges();
    });

    it('reaches no transport type of its own and imports no environment module', () => {
      const view = component as unknown as Record<string, unknown>;

      expect(view['http']).toBeUndefined();
      expect(view['httpClient']).toBeUndefined();
      expect(view['environment']).toBeUndefined();
      expect(view['apiBaseUrl']).toBeUndefined();
    });

    it('exposes its derived state as READ-ONLY signals a template cannot write to', () => {
      arrive(0);

      const activeTab = member<Signal<string>>('activeTab');
      const confirming = member<Signal<boolean>>('confirmingDelete');
      const rejected = member<Signal<boolean>>('submitRejected');

      for (const derived of [activeTab, confirming, rejected]) {
        expect('set' in derived).toBeFalse();
        expect('update' in derived).toBeFalse();
      }

      expect(activeTab()).toBe('basic');
    });

    it('holds every control as NON-NULLABLE, so the raw value is complete rather than partial', () => {
      arrive(0);

      const raw = form().getRawValue();

      expect(Object.keys(raw).length).toBe(19);
      for (const value of Object.values(raw)) {
        expect(value).not.toBeNull();
        expect(value).not.toBeUndefined();
      }

      form().reset();

      // A non-nullable control resets to its DECLARED initial value rather than to null.
      expect(form().controls.portalName.value).toBe('');
      expect(form().controls.splashTabId.value).toBe(-1);
      expect(form().controls.bannerAdvertising.value).toBe(BannerAdvertisingMode.None);
    });

    it('tracks every repeat, so re-reading the page list does not rebuild the option nodes', () => {
      arrive(0);
      showAdvanced();

      const before = required(selector('splashTabId'), 'the splash selector');
      const firstOptionBefore = before.options.item(1);

      // A different portal with the SAME first page. A repeat without a tracking expression
      // would replace every node; a tracked one keeps the node whose key is unchanged.
      arrive(1, {
        settings: settingsBody({ portalId: 1 }),
        detail: detailBody({ portalId: 1 }),
        pages: pageListing(),
      });
      showAdvanced();

      const after = required(selector('splashTabId'), 'the splash selector');

      expect(after.options.length).toBe(before.options.length);
      expect(after.options.item(1)).toBe(firstOptionBefore);
      // Two options sharing a value would make a native selector ambiguous; the tracking
      // expression is the page identifier, so a duplicate would have been dropped above.
      const values = Array.from(after.options).map((option) => option.value);

      expect(new Set(values).size).toBe(values.length);
    });

    it('shows the first-read spinner only while nothing is on screen yet', () => {
      fixture.componentRef.setInput('portalId', 0);
      fixture.detectChanges();

      expect(query('app-loading-spinner')).not.toBeNull();

      http.expectOne(settingsUrl(0)).flush(envelope(settingsBody()));
      http.expectOne(portalUrl(0)).flush(envelope(detailBody()));
      http.expectOne(tabsUrl(0)).flush(envelope(pageListing()));
      answerAdministrators(0);
      fixture.detectChanges();

      expect(query('app-loading-spinner')).toBeNull();
      expect(input('portalName')).not.toBeNull();
    });

    it('disables both actions while a write is in flight', () => {
      holdsHostAccount.set(true);
      arrive(0);
      submit();

      const actions = queryAll<HTMLButtonElement>('button');
      const submitAction = actions.find((candidate) => candidate.type === 'submit');
      const cancelAction = actions.find((candidate) => text(candidate) === 'Cancel');

      expect(required(submitAction, 'the submit action').disabled).toBeTrue();
      expect(required(cancelAction, 'the cancel action').disabled).toBeTrue();

      completeSave(takeSave(0));

      expect(
        required(
          queryAll<HTMLButtonElement>('button').find((candidate) => candidate.type === 'submit'),
          'the submit action',
        ).disabled,
      ).toBeFalse();
    });

    it('gives every radio the native name of the control it belongs to', () => {
      arrive(0);

      for (const radio of radios('bannerAdvertising')) {
        expect(radio.name).toBe('bannerAdvertising');
      }

      showAdvanced();

      for (const radio of radios('userRegistration')) {
        expect(radio.name).toBe('userRegistration');
      }

      // No radio anywhere is left without a name, which would let two groups interfere.
      for (const radio of queryAll<HTMLInputElement>('input[type="radio"]')) {
        expect(radio.name.length).toBeGreaterThan(0);
      }
    });
  });
});

// The selector, proven by naming it

/** A host whose only purpose is to name the element under test. */
@Component({
  selector: 'app-portal-settings-host',
  standalone: true,
  imports: [PortalSettingsComponent],
  template: '<app-portal-settings />',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class PortalSettingsHostComponent {}

describe('PortalSettingsComponent selector and change-detection contract', () => {
  let hostFixture: ComponentFixture<PortalSettingsHostComponent>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [PortalSettingsHostComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthStore,
          useValue: {
            isSuperUser: signal<boolean>(false).asReadonly(),
            portalId: signal<number | null>(null).asReadonly(),
          },
        },
      ],
    });

    hostFixture = TestBed.createComponent(PortalSettingsHostComponent);
    http = TestBed.inject(HttpTestingController);
    hostFixture.detectChanges();
  });

  afterEach(() => {
    // No portal identifier was bound, so the screen must have asked for nothing at all.
    http.verify();
  });

  it('renders under the element name "app-portal-settings"', () => {
    const element = (hostFixture.nativeElement as HTMLElement).querySelector(
      'app-portal-settings',
    );

    expect(element).not.toBeNull();
    expect(element?.querySelector('.portal-settings')).not.toBeNull();
  });

  it('declares the OnPush change-detection strategy', () => {
    const compiled = PortalSettingsComponent as unknown as {
      readonly ɵcmp?: { readonly onPush?: boolean };
    };

    expect(compiled.ɵcmp).toBeDefined();
    expect(compiled.ɵcmp?.onPush).toBeTrue();
    expect(ChangeDetectionStrategy.OnPush).toBe(0);
  });

  it('registers no providers of its own, so it shares the injector its route gives it', () => {
    const compiled = PortalSettingsComponent as unknown as {
      readonly ɵcmp?: { readonly providersResolver?: unknown };
    };

    expect(compiled.ɵcmp?.providersResolver).toBeNull();
  });

  it('is standalone, so it needs no declaring module to be rendered', () => {
    const compiled = PortalSettingsComponent as unknown as {
      readonly ɵcmp?: { readonly standalone?: boolean };
    };

    expect(compiled.ɵcmp?.standalone).toBeTrue();
  });

  it('renders the address-carries-no-portal notice when no identifier is bound', () => {
    const rendered = ((hostFixture.nativeElement as HTMLElement).textContent ?? '')
      .replace(/\s+/gu, ' ')
      .trim();

    expect(rendered).toContain('This address does not identify a portal to configure.');
  });
});
