//
// Specification for the portal settings screen — the Angular 19 replacement for the
// DotNetNuke 4.9.0 "Site Settings" administration page.
//
// ---------------------------------------------------------------------------
// WHY THIS FILE CARRIES MORE WEIGHT THAN ITS THREE SIBLINGS
// ---------------------------------------------------------------------------
// `tsconfig.app.json` compiles by IMPORT GRAPH from `src/main.ts` and includes only
// declaration files besides, so it never reads a spec. `tsconfig.spec.json` is the one
// project that includes `src/**/*.spec.ts`, and the workspace test target compiles with it.
// This file is therefore the only thing that type-checks `portal-settings.component.ts`
// AND its template, and the only thing that executes either. A thin spec here leaves the
// other three files in the folder unverified, so the coverage below is deliberately
// exhaustive rather than representative.
//
// ---------------------------------------------------------------------------
// WHAT IS REAL AND WHAT IS STOOD IN FOR
// ---------------------------------------------------------------------------
// The screen is exercised through its REAL collaborators — the portal signal store, the
// portal transport, the page transport and the notification queue — with a real HTTP client
// whose backend is the testing controller. Nothing between the component and the wire is
// replaced, so every case below proves the whole path: a route input reaches the store, the
// store reaches the transport, the transport composes a URL and a body, and the answer (or
// the refusal) comes back through the same chain the browser uses.
//
// Exactly ONE collaborator is stood in for: the identity store. This screen reads two facts
// from it — whether the caller holds the host account, and which portal the session is
// browsing — and both are derived from a persisted credential. Driving them through the real
// store would couple this specification to how a session is stored rather than to what this
// screen does with the answer, so a value carrying those two signals and NOTHING ELSE takes
// its place. `useValue` is not checked against the token it stands in for, so a member added
// "just in case" would never be reported as unused; the double is kept to the two members
// the screen actually reads.
//
// The router is real and its navigation is spied on, because two of the cases below are
// about navigation that must NOT happen.
//
// ---------------------------------------------------------------------------
// THE URL CONTRACT: RELATIVE, ALWAYS
// ---------------------------------------------------------------------------
// The workspace build swaps the environment file for the DEVELOPMENT configuration, not for
// the production one — the production file is the default. The test target declares no such
// swap at all, so a spec compiles against the production environment, whose API base is the
// RELATIVE `/api/v1`.
//
// That is not an accident of configuration, it is the deployment contract: the reverse proxy
// in front of the two containers serves the application and forwards the API prefix to the
// API container, so the browser must address the API through the SAME ORIGIN that served the
// page. An absolute host would resolve only inside the container network and would fail from
// a browser while both containers reported healthy.
//
// Every expectation below therefore matches a relative path beginning `/api/v1`. No absolute
// host appears anywhere in this file, and the environment module is deliberately not
// imported — the literal prefix is asserted instead, so a change to the base URL is a failing
// test rather than a silently agreeing one.
//
// ---------------------------------------------------------------------------
// THE BODY CONTRACT
// ---------------------------------------------------------------------------
// Three shapes travel, and the fixtures below reproduce all three:
//
//   * a SINGLE PAYLOAD arrives inside `{ data, meta }` — the settings projection, the portal
//     detail and the UNPAGED page listing all use it, and the transports unwrap it;
//   * a PAGE arrives as `{ items, meta }` at the top level — the portal listing, which the
//     store re-reads after every successful write;
//   * a FAILURE arrives as an HTTP status plus a problem document. A result envelope never
//     crosses the wire, so no case below simulates a failure with a `200`.
//
// The API's serializer writes EVERY declared member and spells absence as the value `null`
// rather than by omitting the member, and the client decoders enforce that: a missing member
// is drift and is refused. Every fixture is therefore COMPLETE. That is not pedantry — an
// abbreviated fixture would hide exactly the defect this screen is most exposed to, because
// `-1`, `0`, `""` and `false` are all DATA here and a coalescing fallback over an absent
// member would look correct against a short fixture and be wrong against a real response.
//
// ---------------------------------------------------------------------------
// THE SENTINEL MATRIX THE CASES BELOW ARE BUILT AROUND
// ---------------------------------------------------------------------------
// `Library/Components/Shared/Null.vb` spells absence as `-1` for an integer, `255` for a
// byte, the minimum value for each floating type, `Date.MinValue` for a date, the EMPTY
// STRING for a string, `False` for a boolean and the empty identifier for a globally unique
// one. Two of those collide with real data in this very screen:
//
//   * `dbo.Portals.PortalID` is declared `IDENTITY (-1, 1)`, so the FIRST portal an
//     installation ever creates carries `-1` and the second carries `0` — and `-1` is
//     simultaneously the absent-integer marker;
//   * `dbo.Tabs.TabID` is declared `IDENTITY (0, 1)`, so page `0` is an ordinary page, while
//     the synthetic "none specified" option the legacy page list prepended carries `-1`.
//
// Every case that touches an identifier, a quota or a date is written to fail if a truthiness
// test or a coalescing default has crept in.
//
// ---------------------------------------------------------------------------
// PROVENANCE OF EVERY ASSERTED STRING AND RULE
// ---------------------------------------------------------------------------
//   Website/admin/Portal/sitesettings.ascx                568 lines. 13 section heads, of
//                                                         which exactly 3 are top level;
//                                                         9 measured character limits;
//                                                         EXACTLY 2 validators, both
//                                                         data-type comparisons
//   Website/admin/Portal/SiteSettings.ascx.vb             896 lines. Hydration, the four
//                                                         identical page reads, the banner
//                                                         lock, the host-field refusal, the
//                                                         host-account gate, the delete
//   Website/admin/Portal/App_LocalResources/
//     SiteSettings.ascx.resx                              141 entries. The authoritative
//                                                         wording, which BEATS the markup
//   Website/App_GlobalResources/SharedResources.resx      the shared wording
//   Website/controls/sectionheadcontrol.ascx              the toggle whose negative tab index
//                                                         the target reverses
//   Library/Components/Shared/Globals.vb  L812-L852       GetPortalTabs, the canonical body
//   Library/Components/Shared/Null.vb                     the sentinel contract
//   Library/Components/Portal/PortalInfo.vb               the CLR type of every column
//   Library/Components/Portal/PortalController.vb         the replaced write contract
//   Library/Components/Portal/PortalSettings.vb           the proof that no portal-settings
//                                                         table exists
//   Website/release.config              L125              the strictness-off compilation
//
// Every one of those measurements was re-taken against the checkout before being asserted
// here; none is carried over on trust.
//

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';

import { BannerAdvertisingMode, UserRegistrationMode } from '../../../core/models/portal.model';
import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { FormFieldComponent } from '../../../shared/components/form-field/form-field.component';
import { PortalSettingsComponent } from './portal-settings.component';

import type { TestRequest } from '@angular/common/http/testing';
import type { Signal, WritableSignal } from '@angular/core';
import type {
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
 * The versioned prefix, spelled out rather than imported.
 *
 * Asserting the literal is the point: importing the environment module would make every
 * expectation below agree with whatever the base URL happened to be, which is the one thing
 * a specification of a deployment contract must not do.
 */
const API = '/api/v1';

/** The portal collection. The store re-reads it after every successful write. */
const PORTALS_URL = `${API}/portals`;

/** One portal, addressed by identifier. Also the delete target. */
function portalUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}`;
}

/** One portal's configuration projection: read with GET, replaced whole with PUT. */
function settingsUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}/settings`;
}

/**
 * One portal's pages.
 *
 * Nested under the portal because the LISTING belongs to a portal, while operations on an
 * individual page are addressed at the root. The two shapes are not one prefixed family and
 * are not modelled as one here.
 */
function tabsUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}/tabs`;
}

/**
 * The namespace the API puts in front of every failure code it publishes.
 *
 * Lower case, and part of the WIRE CONTRACT rather than a project identifier: the server
 * builds this exact string, the shared failure reader recognises a code only behind it, and
 * thirty-two sibling specifications in this workspace spell it the same way. A document whose
 * `type` lacks it carries no code at all, which is why the refusal cases below include it.
 */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/**
 * The published code for the last-remaining-portal refusal, as ONE dotted string.
 *
 * The legacy antecedent is the shared resource key `LastPortal`, whose wording is asserted
 * verbatim further down. The code itself is `portal.last_remaining` — the spelling the server
 * emits and the shared conflict table recognises — and it is never split, re-cased or
 * rebuilt from parts, because the reader lower-cases and folds separators and would silently
 * fail to match a hand-assembled variant.
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
 * An empty portal listing page.
 *
 * Needed because the store re-reads the listing after a successful save and after a
 * successful delete, so every such case has a third request to answer. `totalCount: 0` and
 * `pageIndex: 0` are ordinary values for an empty first page and are not treated as absence
 * by anything that reads them.
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
 * A settings projection whose awkward values are the interesting ones.
 *
 * The fee and the three quotas are `0` — real stored values that must render as the literal
 * `0`. The expiry is the LEGACY DATE SENTINEL, so the box must come out empty. The splash
 * page is absent and the home page is `0`, which is a real page. Every one of the twenty-seven
 * members is present, because the serializer writes every one.
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
    ...overrides,
  };
}

/**
 * A portal detail.
 *
 * Read alongside the projection because it is the only source of the portal's ADMINISTRATION
 * PAGE, without which the administration band cannot be excluded from the four page
 * selectors. The legacy screen had the whole portal in hand for the same reason.
 */
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

/**
 * The page listing every selector case shares.
 *
 * Deliberately built to exercise the whole of the measured filter at once, so a single fetch
 * proves seven rules:
 *
 *   * page `0` is a root page and a REAL page, not the absent marker;
 *   * page `4` is a level-one CHILD OF PAGE 0, which proves `0` is a real parent — root
 *     detection must not be "has a falsy parent";
 *   * page `5` is a level-two grandchild, so the indent must repeat;
 *   * page `6` is INVISIBLE and must still appear, because the legacy call asked for hidden
 *     pages;
 *   * page `7` is RECYCLED and must not appear;
 *   * page `8` carries a URL and must not appear, the legacy type test admitting only pages
 *     whose URL is empty;
 *   * page `90` is the administration page and page `91` is its child, and neither may appear.
 */
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

/**
 * A problem document, as the API writes one.
 *
 * All five members the error contract names are present — the type carrying the published
 * code, the title, the status, the detail and the per-field dictionary — plus the trace
 * identifier the document model also carries.
 */
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
  let navigate: jasmine.Spy<(url: string) => Promise<boolean>>;

  /** Whether the caller holds the host account, under test control. */
  let holdsHostAccount: WritableSignal<boolean>;

  /** The portal the session is browsing, under test control. `null` is "nobody signed in". */
  let browsingPortalId: WritableSignal<number | null>;

  // -------------------------------------------------------------------------
  // Reaching the component
  // -------------------------------------------------------------------------

  /**
   * Reads a member the component keeps for its template.
   *
   * Those members are not part of the class's outward surface — the screen is reached by lazy
   * route load and nothing outside it calls them — so they are read here through a narrow
   * indexed view rather than by widening the instance to the forbidden catch-all type.
   */
  function member<T>(name: string): T {
    return (component as unknown as Record<string, T>)[name];
  }

  /**
   * Invokes one of those members as a method, with the component as its receiver.
   *
   * The argument list is a mutable array because the reflective apply requires one; it is
   * built here and never escapes, so nothing outside can mutate it.
   */
  function invoke<T>(name: string, ...args: unknown[]): T {
    const method = member<(...called: unknown[]) => T>(name);

    return method.apply(component, args);
  }

  /** The typed form group the screen edits. */
  function form(): PortalSettingsComponent['form'] {
    return (component as unknown as { form: PortalSettingsComponent['form'] }).form;
  }

  /**
   * The rendered host element, typed.
   *
   * The framework leaves `nativeElement` untyped, and an untyped receiver cannot take the
   * type argument the query helpers need, so the narrowing happens once here.
   */
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

  /** The whole screen's rendered text, collapsed. Used for the absence cases. */
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

  /**
   * The grouped field set a disclosure caption belongs to.
   *
   * Located from the caption rather than from a position, so inserting a group elsewhere
   * cannot make one of these cases silently assert about a different section.
   */
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

  /**
   * The value one option carries, with the framework's index prefix removed.
   *
   * A selector bound with a NUMERIC option value keeps that number in the model and writes the
   * document value as `"<index>: <value>"`, so that two options can never collide after being
   * turned into text. The number is what the legacy option carried and what these cases assert;
   * the prefix is framework bookkeeping and is stripped here once rather than at each call site.
   */
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

  /**
   * Narrows a query result, failing the case with a legible reason when nothing rendered.
   *
   * Preferred over a non-null assertion throughout: an assertion silently produces a
   * `TypeError` several lines later, whereas this names the element that was expected.
   */
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

  /**
   * Opens a disclosure, whatever state it is already in.
   *
   * Written as "ensure open" rather than "toggle" because the disclosure state belongs to the
   * component and survives a change of route input: a setup step that toggled blindly would
   * CLOSE a section a previous step had already opened. The raw toggle is exercised on its own
   * further down, where the toggling itself is the subject.
   */
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
   * Answers the three reads a portal identifier starts, and renders the result.
   *
   * The identifier setter issues exactly three: the configuration projection it edits, the
   * portal detail that supplies the administration page, and the portal's pages. They are
   * matched by URL rather than by arrival order, because the order is the setter's business
   * and not a contract worth freezing here.
   */
  function arrive(
    portalId: number,
    options: {
      readonly settings?: PortalSettings;
      readonly detail?: PortalDetail;
      readonly pages?: readonly TabListItem[];
    } = {},
  ): void {
    fixture.componentRef.setInput('portalId', portalId);

    http.expectOne(settingsUrl(portalId)).flush(envelope(options.settings ?? settingsBody()));
    http.expectOne(portalUrl(portalId)).flush(envelope(options.detail ?? detailBody()));
    http.expectOne(tabsUrl(portalId)).flush(envelope(options.pages ?? pageListing()));

    fixture.detectChanges();
  }

  /** The portal listing re-read the store performs after every successful write. */
  function answerListingRefresh(): void {
    http
      .expectOne((candidate) => candidate.url === PORTALS_URL)
      .flush(emptyPortalPage());
  }

  /**
   * Takes the pending settings write off the queue.
   *
   * ⚠ CALL THIS ONCE PER CASE. The testing controller REMOVES a request as soon as it is
   * matched, so asking for the same write twice reports "found none" the second time. Every case
   * below therefore captures the write, inspects it, and completes it — which is also the only
   * ordering in which the captured request is still answerable.
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

  /**
   * One member of a captured write, read by name.
   *
   * Needed for the members the contract does NOT declare: the absence cases below have to ask
   * for a name that is not in the type, which a typed read cannot express. The value comes back
   * as `unknown`, so each case still has to say what it expected.
   */
  function memberOf(write: TestRequest, name: string): unknown {
    const body = write.request.body as Readonly<Record<string, unknown>>;

    return body[name];
  }

  /** Completes a captured write and answers the listing re-read the store performs after it. */
  function completeSave(write: TestRequest, stored: PortalSettings = settingsBody()): void {
    write.flush(envelope(stored));
    answerListingRefresh();
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

  /** The most recently queued notification, or null when the queue is empty. */
  function latestNotification(): { readonly severity: string; readonly message: string } | null {
    const queue = notifications.notifications();
    const last = queue[queue.length - 1];

    return last === undefined ? null : { severity: last.severity, message: last.message };
  }

  beforeEach(() => {
    holdsHostAccount = signal<boolean>(false);
    browsingPortalId = signal<number | null>(7);

    TestBed.configureTestingModule({
      imports: [PortalSettingsComponent],
      providers: [
        // ORDER IS LOAD-BEARING. The testing provider replaces the BACKEND of an
        // already-configured client, so the client must be configured first; reversed, the
        // client keeps its real backend and every request below would escape unintercepted.
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
    // The proof that nothing stray or duplicated was issued. It is what turns "the pages are
    // read once" from a claim into a check: a second identical read would be an unmatched
    // request and would fail here even if no case asserted a count.
    http.verify();
  });


  // =========================================================================
  // A. THE IDENTITY TRAP — `0` AND `-1` ARE BOTH REAL PORTALS
  // =========================================================================
  //
  // `dbo.Portals.PortalID` is declared `IDENTITY (-1, 1)`, so the first portal an
  // installation creates carries `-1` and the second carries `0`. `-1` is at the same time
  // the legacy absent-integer marker, whose definition is literally `Return -1`. The number
  // therefore means both "the first portal" and "no portal", distinguishable only by context
  // that neither a transport nor a route binding has.
  //
  // A truthiness test drops the SECOND portal. A comparison against the marker drops the
  // FIRST. Both cases below exist to make either mistake fail loudly.

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

      fixture.componentRef.setInput('portalId', -1);

      const forMinusOne: TestRequest = http.expectOne(`${API}/portals/-1/settings`);

      expect(forMinusOne.request.method).toBe('GET');
      expect(forMinusOne.request.url.startsWith('http')).toBeFalse();
      forMinusOne.flush(envelope(settingsBody({ portalId: -1 })));
      http.expectOne(`${API}/portals/-1`).flush(envelope(detailBody({ portalId: -1 })));
      http.expectOne(`${API}/portals/-1/tabs`).flush(envelope([]));

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
      // A path segment is text. `Number('0')` is `0`, which is falsy, while the string `'0'`
      // is truthy, so any conversion leaning on truthiness disagrees with itself depending on
      // which side of the conversion it sits.
      fixture.componentRef.setInput('portalId', '0');

      expect(component.portalId).toBe(0);
      http.expectOne(settingsUrl(0)).flush(envelope(settingsBody()));
      http.expectOne(portalUrl(0)).flush(envelope(detailBody()));
      http.expectOne(tabsUrl(0)).flush(envelope([]));

      fixture.componentRef.setInput('portalId', '-1');

      expect(component.portalId).toBe(-1);
      http.expectOne(settingsUrl(-1)).flush(envelope(settingsBody({ portalId: -1 })));
      http.expectOne(portalUrl(-1)).flush(envelope(detailBody({ portalId: -1 })));
      http.expectOne(tabsUrl(-1)).flush(envelope([]));
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

  // =========================================================================
  // B. THE EXPIRY-DATE SENTINEL
  // =========================================================================
  //
  // Measured at `SiteSettings.ascx.vb:L341-L343`: the assignment is guarded by the legacy
  // absence test, so a portal with no expiry showed an EMPTY box and never a placeholder date.
  // The absent date is `Date.MinValue`, which serialises as `0001-01-01T00:00:00`.
  //
  // ⚠ DIVERGENCE FROM THE FOLDER BRIEF, ASSERTED AS THE CODE ACTUALLY BEHAVES. The brief asks
  // that a blank box SEND the sentinel. The screen sends ABSENCE, and its own inline note gives
  // two measured reasons: the column is `datetime`, whose earliest representable instant is
  // 1753-01-01, so the sentinel is not storable and every save from a blank box would be
  // refused; and the legacy write path converted a date equal to the sentinel to a database
  // null BEFORE it reached the stored procedure, so the sentinel only ever existed in memory.
  // The sentinel is therefore preserved exactly where it was observable — in the blank box —
  // and absence is preserved where it was stored. The cases below assert that split, and the
  // divergence is reported rather than absorbed.

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
      // ⚠ REPORTED RATHER THAN ASSUMED. The folder brief expects a response carrying
      // `0001-01-01T00:00:00` and a write carrying it back. Neither is reachable through this
      // contract, and the reason is measured on both sides:
      //
      //   * the projection member is a NULLABLE date on the server and a nullable string here, so
      //     an absent expiry is written as the value `null`, not as a minimum-value date; and
      //   * the column is `datetime`, whose earliest representable instant is 1753-01-01, so the
      //     sentinel cannot be stored and therefore cannot be read back either. The legacy write
      //     path converted a date equal to the sentinel to a database null BEFORE it reached the
      //     stored procedure, so the sentinel only ever existed in memory.
      //
      // The sentinel is preserved exactly where it WAS observable — the blank box, asserted above
      // — and absence is preserved where it was stored. What matters for a save is that the
      // sentinel never travels, because a save carrying it would be refused outright, and that
      // is what this case pins.
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

  // =========================================================================
  // C. THE FOUR PAGE SELECTORS — ONE FETCH, ONE OPTION SET
  // =========================================================================
  //
  // All four legacy selectors were filled by the SAME call, written out four times with
  // identical arguments at `SiteSettings.ascx.vb:L299`, `L304`, `L309` and `L314`:
  // `GetPortalTabs(intPortalId, True, True, False, False, False)`. The canonical body at
  // `Library/Components/Shared/Globals.vb:L812-L852` resolves those five flags to:
  //
  //   * none-specified TRUE  → prepend a synthetic option whose identifier is `-1`, named
  //                            `"<" + None_Specified + ">"`, and SELECTABLE rather than a
  //                            disabled prompt — choosing it is how an operator says the
  //                            portal has no such page;
  //   * hidden TRUE          → invisible pages ARE included;
  //   * deleted FALSE        → recycled pages are excluded;
  //   * URL FALSE            → only pages of the Normal type, which the legacy type reader
  //                            returns exactly when the page's URL is empty;
  //   * authorised FALSE     → no role filtering whatsoever;
  //   * unconditionally      → administration pages are excluded.
  //
  // The indent is the only hierarchy cue the screen has: `"..."` repeated once per level and
  // prefixed to the name.
  //
  // The failure being guarded is four identical requests for one answer. It is asserted twice
  // over: by counting the matches, and by the afterEach verification, which fails on a second.

  describe('C. the four page selectors', () => {
    beforeEach(() => {
      arrive(0);
      showAdvanced();
    });

    it('issues EXACTLY ONE page-listing read, counted before it is answered', () => {
      // The count has to be taken while the request is still outstanding, which means a portal
      // this case has not already arrived at — the identifier setter compares values and does
      // nothing when handed the one it already holds.
      fixture.componentRef.setInput('portalId', 7);

      // ⚠ THE MEASUREMENT. Four identical reads — one per selector — is the failure mode the
      // legacy screen exhibited: `GetPortalTabs` was called four times over, at
      // SiteSettings.ascx.vb L299, L304, L309 and L314, with byte-identical arguments. One
      // answer serves all four selectors, so exactly one request may exist.
      const listings: readonly TestRequest[] = http.match(tabsUrl(7));

      expect(listings.length).toBe(1);
      expect(listings[0].request.method).toBe('GET');

      listings[0].flush(envelope(pageListing()));
      http.expectOne(settingsUrl(7)).flush(envelope(settingsBody({ portalId: 7 })));
      http.expectOne(portalUrl(7)).flush(envelope(detailBody({ portalId: 7 })));
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

      // The angle brackets are part of the legacy DISPLAY STRING, built as
      // `"<" + None_Specified + ">"` where the shared wording is `None Specified`. They are
      // not markup and are never interpreted as such.
      expect(first.textContent).toBe('<None Specified>');
      expect(optionValue(first)).toBe('-1');
      expect(first.disabled).toBeFalse();
      // Selectable, not a disabled prompt: choosing it is how an operator says the portal has no
      // such page.
      expect(first.disabled).toBeFalse();
    });

    it('selects that option for every one of the four absent page references', () => {
      // A DIFFERENT portal, so the route input genuinely changes and the screen re-reads. The
      // input binding is skipped for an identical value, which is correct behaviour and is why
      // no case here re-binds the same identifier expecting a fresh read.
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
      // The legacy call passed the check-authorised flag as False, so the role test at
      // `Globals.vb:L840` was never reached. The identity double holds no host account and no
      // permission of any kind, and every admissible page is still offered.
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
      // ⚠ DIVERGENCE FROM THE FOLDER BRIEF, ASSERTED AS THE CODE BEHAVES. The brief asks that
      // an unchosen selector SUBMIT `-1`. The sentinel stays inside the FORM — the option
      // genuinely carries it, as the case above proves — and becomes absence on the wire, for
      // two measured reasons recorded on the screen itself: the legacy write path rewrote `-1`
      // to a database null before it reached the stored procedure, so `-1` never reached the
      // column; and the general portal write verifies every page reference against the
      // portal's own pages, none of which bears the identifier `-1`. Sending it would be
      // refused rather than merely redundant.
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
      fixture.detectChanges();
      showAdvanced();

      expect(screenText()).toContain('The list of pages could not be loaded');
      expect(queuedSeverities()).toContain('warning');

      // The chosen page survives as an option even though the listing failed, so saving the
      // other seventeen fields does not silently clear it.
      expect(optionValuesOf('homeTabId')).toContain('0');
    });
  });


  // =========================================================================
  // D. THE QUOTAS — THE LITERAL ZERO
  // =========================================================================
  //
  // Measured at `SiteSettings.ascx.vb:L344-L347`: the fee and the three quotas were hydrated
  // with a plain `.ToString`, with no special case for zero, so a stored `0` appeared in the
  // box AS `0`. Substituting the word "unlimited" for it, or blanking the box, would both hide
  // a real value — the zero-means-unlimited rule is stated in the disk-space help text and
  // belongs there alone. That help value is measured verbatim as
  // `The amount of Disk Space in MB allowed for this site (enter 0 for unlimited space).`
  //
  // The reverse direction is measured too, at `L705-L722`: each of the four was a local
  // initialised to `0`, parsed over only when the box was non-empty. A blank box therefore
  // SAVED ZERO, and sending absence instead would be a different write.

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
      // `-1` is the "not set" reading of these columns, arrived at because the legacy row
      // hydration converted a database null to the absent-integer marker while the create path
      // initialised the same columns to `0`. Both numbers are therefore real stored values and
      // neither may be rewritten as the other.
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
      // S11, third manifestation. `UpdatePortalInfo` declared `HostFee As Double` and
      // `HostSpace As Double`, while `PortalInfo.HostFee` is `Single` and
      // `PortalInfo.HostSpace` is `Integer`. The wire contract follows the entity: the fee is a
      // decimal amount and the disk space is a whole number of megabytes.
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
      // S11, second manifestation. `Dim intUserQuota As Double = 0` was assigned from
      // `Integer.Parse` and then passed as the `Integer` argument, and neither narrowing was
      // written down. Here the value is an integer from the box to the request.
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

  // =========================================================================
  // E. THE BANNER HOST LOCK
  // =========================================================================
  //
  // Measured at `SiteSettings.ascx.vb:L291-L297`: the chosen index is the stored value; for a
  // host account the notice is hidden outright; for everybody else the list is enabled exactly
  // when the stored value is NOT the host option, and the notice is shown exactly when it IS.
  // The stored value is what decides, not the value currently in the form, so choosing the host
  // option mid-edit must not lock the control the operator is using.
  //
  // The notice is `lblBanners.Text`, whose stored value opens with break markup:
  // `<br>Banner option was set by the hostingprovider, and cannot be changed`. The one-word
  // "hostingprovider" is in the legacy string and is reproduced as measured.

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

      // The reactive radio accessor CAPTURES the bound value for the model and leaves the
      // document attribute at its default, so the ordinals are read from the choice list the
      // screen publishes and then PROVEN through the model by choosing each one in turn. Reading
      // the document attribute instead would assert the framework's default, not the ordinal.
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

  // =========================================================================
  // F. USER REGISTRATION — THE FOUR ORDINALS
  // =========================================================================
  //
  // Measured in the markup: the four list items declare `Value="0"` None, `"1"` Private,
  // `"2"` Public and `"3"` Verified, and the enumeration carries the same four numbers.

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


  // =========================================================================
  // G. THE TWO VALIDATORS — AND BOTH ARE DATA-TYPE CHECKS
  // =========================================================================
  //
  // A full case-insensitive sweep of the 568-line markup finds EXACTLY TWO validators on the
  // whole screen, and both are comparison validators performing a data-type check. There is no
  // required-field validator, no regular-expression validator, no range validator, no custom
  // validator and no validation summary anywhere on it.
  //
  //   * `valExpiryDate` at L433-L435 declares `Operator="DataTypeCheck" Type="Date"` and
  //     `Display="Dynamic"`, and carries NO resource key — so its inline message is the only
  //     wording that exists for it, which makes it the single measured exception to the
  //     resource-beats-markup rule. Its stored message opens with break markup.
  //   * `valHostFee` at L444-L446 declares `Operator="DataTypeCheck" Type="Currency"` and
  //     `ResourceKey="valHostFee.Error"`, and its message carries no leading break markup —
  //     none is invented here.
  //
  // Two consequences pull in opposite directions and both are asserted. A data-type comparison
  // PASSES on empty input, so a presence rule must not be added; and a presence rule is not a
  // substitute for the type check either, because reproducing these two as "required" would
  // accept "not a date" and reject a blank, inverting both.

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

    /**
     * The controls that hold text.
     *
     * Named as a closed union rather than derived with `keyof`, because six of the sixteen
     * controls hold a number or an enumeration member and writing text into one of those is a
     * type error that this union prevents at compile time.
     */
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

    it('declares NO presence rule on ANY control, matching a screen with no required validators', () => {
      // Every control is emptied at once and the form must still be submittable. A single
      // required rule anywhere would fail this.
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

      expect(form().valid).toBeTrue();
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

  // =========================================================================
  // H. THE MEASURED CHARACTER LIMITS
  // =========================================================================
  //
  // Nine limits, read one attribute at a time because the casing in the source is inconsistent
  // — the fee box spells `maxlength` and `width` in lower case while its neighbours do not, so
  // a single pattern match would have missed it.
  //
  //   txtPortalName 128 · txtDescription 475 · txtKeyWords 475 · txtFooterText 100 ·
  //   txtExpiryDate 15 · txtHostFee 10 · txtHostSpace 6 · txtPageQuota 6 · txtUserQuota 6
  //
  // The description and the keywords also declare `Rows="3" TextMode="MultiLine"`, so both are
  // multi-line inputs rather than single-line ones.

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


  // =========================================================================
  // I. THE TWO TABS AND THE SIX SURVIVING DISCLOSURES
  // =========================================================================
  //
  // The thirteen legacy section heads are a TWO-LEVEL hierarchy, established by pairing each
  // head's `Section="tblX"` with the `<table id="tblX">` it names. Exactly three carry
  // `IncludeRule="True"` and are therefore top level: Basic Settings (L14), Advanced Settings
  // (L204) and the Stylesheet Editor (L539). The third goes with the excluded skinning
  // subsystem, so TWO tabs remain and the ten nested heads become disclosures inside them.
  //
  // Their measured initial states, read one head at a time: `dshSite`, `dshSecurity` and
  // `dshPages` declare no `IsExpanded` at all and so default OPEN; `dshMarketing` declares
  // `IsExpanded="True"`; `dshOther` (L386) and `dshHost` (L421) declare `IsExpanded="False"`.
  // Four of the ten nested heads render nothing at all, their whole subject matter being out of
  // scope: appearance, payment, usability and secure transport.
  //
  // Two of the surviving six survive only PARTIALLY, which is why they are here at all rather
  // than dropped whole: Site Marketing keeps exactly one of its five rows, and Other Settings
  // keeps exactly two of its four.

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
      // The markup attribute reads `Text="Marketing"`; `Marketing.Text` in the resource file
      // reads `Site Marketing`. The resource wins, and this is the first of two proofs of that
      // precedence on this screen.
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

      expect(
        required(sectionFieldset('Other Settings'), 'the other-settings group')
          .querySelectorAll('app-form-field').length,
      ).toBe(2);
    });

    it('renders EXACTLY ONE field in Site Marketing: the banner choice', () => {
      const group = required(sectionFieldset('Site Marketing'), 'the marketing group');

      expect(group.querySelectorAll('app-form-field').length).toBe(1);
      // The shared wrapper NORMALISES the caption's trailing colon away on display, which is what
      // keeps the inconsistent legacy colon handling from reaching the screen. The supplied
      // caption still carries it, and section J asserts that side.
      expect(text(group.querySelector('label'))).toBe('Banners');
      expect(required(fieldLabelled('Banners:'), 'the banners field').label).toBe('Banners:');
    });

    it('renders EXACTLY TWO fields in Other Settings: the administrator and the time zone', () => {
      showAdvanced();
      ensureSectionOpen('other');

      const group = required(sectionFieldset('Other Settings'), 'the other-settings group');
      const captions = Array.from(group.querySelectorAll('app-form-field')).map((field) =>
        text(field.querySelector('label')),
      );

      expect(group.querySelectorAll('app-form-field').length).toBe(2);
      expect(captions).toEqual(['Administrator', 'Portal TimeZone']);
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

  // =========================================================================
  // J. WORDING, TAKEN FROM THE RESOURCE VALUE
  // =========================================================================
  //
  // The resource file is the authority and the markup attribute is not, and this screen carries
  // two proofs of it: the marketing caption asserted above, and the keywords caption below,
  // whose markup reads `Key Words:` while `plKeyWords.Text` reads `Keywords:`.
  //
  // The title is mode-dependent: `ControlTitle_.Text` is `Site Settings` and
  // `ControlTitle_edit.Text` is `Edit Portals`. This screen is the settings mode, so the first
  // is correct and the second must not appear.
  //
  // `plPortalName.Text` is `Title:` — a semantic inversion worth flagging, because the control
  // it names is the portal's NAME and not its host-name alias.

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


  // =========================================================================
  // K. THE GLOBALLY UNIQUE IDENTIFIER
  // =========================================================================
  //
  // Measured at `SiteSettings.ascx.vb:L273`: `lblGUID.Text = objPortal.GUID.ToString.ToUpper`.
  // It went into a LABEL, never into an input, and the screen offered no setter for it.

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

  // =========================================================================
  // L. THE HOST-ACCOUNT GATE
  // =========================================================================
  //
  // Measured at `SiteSettings.ascx.vb:L498-L516`: the host group's visibility is the
  // super-user test and nothing else.
  //
  // ⚠ THE GATE CANNOT BE A PERMISSION, and that is a fact about the data rather than a design
  // preference. The persisted permission vocabulary is closed at four keys — view, edit, read
  // and write — and none of them expresses a host account; the server's authorisation POLICY
  // names are a separate closed set and likewise contain no host member. A permission-shaped
  // gate would therefore match nothing and hide the group from everybody, including the
  // accounts it exists for. The gate reads the identity store instead, and the case below
  // asserts that the permission directive is nowhere on the screen.
  //
  // The gate is ADVISORY in any case: the server applies the same rule and answers a refusal,
  // which section M asserts is presented rather than pre-empted.

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
      // The update resource replaces every column it names, so a member this screen does not
      // SHOW must still be sent back as it was. The legacy postback did the same by keeping a
      // hidden control's value in the serialised control tree.
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

      // The directive is not among the component's imports, so it could not bind even if an
      // attribute were written; the attribute is absent as well, which is what a reader
      // checking the template would look for.
      expect(queryAll('[hasPermission]').length).toBe(0);
      expect(host().outerHTML).not.toContain('hasPermission');
    });
  });

  // =========================================================================
  // M. A REFUSAL OF THE HOST-OWNED FIELDS — NOT A SESSION PROBLEM
  // =========================================================================
  //
  // Measured verbatim at `SiteSettings.ascx.vb:L760-L769`: for a caller without the host
  // account the handler compared six host-owned members against the stored portal — the hosting
  // fee, the disk space, the page quota, the user quota, the site-log retention and the expiry
  // date — and on the first difference executed a bare `Throw New System.Exception`. An
  // unhandled fault, presented as a broken page.
  //
  // The server now answers a refusal for the same reason, and this screen says so. It is never
  // phrased as an expired session and it never redirects to a sign-in screen: the caller IS
  // authenticated and the request WAS understood — six named fields are simply not theirs.
  //
  // The severity is the store's classification, which resolves a refusal to a WARNING. A system
  // behaving exactly as configured is not a fault.

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


  // =========================================================================
  // N. SAVING, AND THE THREE WAYS A SAVE CAN BE REFUSED
  // =========================================================================
  //
  // A result envelope never crosses the wire, so no case here simulates a failure with a
  // successful status. Each refusal is an HTTP status plus a problem document, exactly as the
  // API answers.
  //
  // The per-field dictionary is read with INDEX ACCESS. Its keys are the server's model-state
  // keys and are NOT converted to the casing the rest of the payload uses, and the workspace
  // forbids property access on an index signature, so a dotted read would not even compile.

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
      // The legacy null contract spelled an absent string as the EMPTY STRING and converted it
      // back to a database null on the way out, so a blank box and a null column were the same
      // state.
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
      // Measured behaviour, asserted rather than assumed. The screen records the stored resource as
      // the one the form already reflects, so the hydration effect recognises it and leaves the
      // boxes alone. Re-writing them from the response would make every save flicker, and would
      // discard an edit made between the request and its answer.
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

    it('re-reads the portal listing after a successful write, because four of its columns changed', () => {
      submit();
      takeSave(0).flush(envelope(settingsBody()));

      const refresh = http.expectOne((candidate) => candidate.url === PORTALS_URL);

      expect(refresh.request.method).toBe('GET');
      refresh.flush(emptyPortalPage());
      fixture.detectChanges();
    });

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

      // INDEX ACCESS, not property access. The dictionary the server sends is keyed by its own
      // model-state names, which are NOT camelCased, and the workspace forbids property access
      // on an index signature — a dotted read of either key would fail to compile.
      expect(errors['HostFee']).toEqual(['The hosting fee must be a currency amount.']);
      expect(errors['PortalName']).toEqual(['A title is required.']);

      const banner = required(query('app-error-banner'), 'the failure banner');
      const rendered = text(banner);

      // The shared reader lower-cases the leading character of each key on the way in, so a
      // server key of `HostFee` is presented as `hostFee`. Asserted as it actually renders
      // rather than as the wire spells it, and the two spellings are both named here so the
      // conversion is visible instead of implied.
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

  // =========================================================================
  // O. DELETING THE PORTAL
  // =========================================================================
  //
  // Measured at `SiteSettings.ascx.vb:L503`: `cmdDelete.Visible = (intPortalId <> PortalId)` —
  // the action is withheld when the target IS the portal currently being browsed, because a
  // portal may not be deleted out from under the session viewing it. Measured at `L556`: the
  // action was guarded by a confirmation built from THIS screen's own resource entry.
  //
  // ⚠ The confirmation wording is `Are You Sure You Wish To Delete This Portal ?` — with a SPACE
  // BEFORE THE QUESTION MARK, which is in the stored value. It is deliberately NOT the shared
  // `DeleteItem.Text`, `Are You Sure You Wish To Delete This Item?`, which has no such space and
  // which the portal LISTING screen asks. The two screens ask different questions and the
  // difference is asserted rather than smoothed over.

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

      expect(message).toBe('Are You Sure You Wish To Delete This Portal ?');
      expect(message).not.toBe('Are You Sure You Wish To Delete This Item?');
      expect(message.endsWith(' ?')).toBeTrue();
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
      expect(navigate).toHaveBeenCalledWith('/portals');
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

      expect(required(latestNotification(), 'a notification').message).toBe(LAST_PORTAL_MESSAGE);
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


  // =========================================================================
  // P. THE FIELDS THAT ARE GONE — FROM THE DOCUMENT AND FROM THE PAYLOAD
  // =========================================================================
  //
  // ⚠ THERE IS NO PORTAL-SETTINGS TABLE, which is what makes seven of the dropped fields
  // structurally impossible rather than merely out of scope.
  // `Library/Components/Portal/PortalSettings.vb:L923` resolves the site-settings READ to the
  // module settings of the "Site Settings" module instance, and L970 resolves the WRITE the same
  // way through `UpdateModuleSetting`; a case-insensitive sweep for such a table across all
  // eighty-eight upgrade scripts, in all four object-naming forms, returns nothing; and
  // `PortalController.vb:L1209-L1210` shows the legacy composite of that name being read out of
  // per-request ambient storage rather than out of a table. The inline-editor flag, the three
  // control-panel modes and the four secure-transport members were module-setting ROWS.
  //
  // Consequently this screen reads and writes the whole projected resource and nothing else: no
  // key-and-value editor is offered and no key-and-value request is composed.
  //
  // ⚠ THE ADVERTISING ROW IS THE SHARPEST PROHIBITION, and it is sharper here than elsewhere
  // because its PARENT SECTION now renders. `Advertising.Text` in this screen's own resource file
  // holds a live third-party advertising SCRIPT block with a remote source, stored XML-escaped so
  // a naive search clears it wrongly. Nothing on this screen may render it, and nothing on this
  // screen binds any string as markup.

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
        'currency',
        // Usability and the control panel.
        'inlineEditor',
        'controlPanelMode',
        'controlPanelVisibility',
        'controlPanelSecurity',
        // Secure transport.
        'sslEnabled',
        'sslEnforced',
        'sslUrl',
        'stdUrl',
        // Localisation, logging, premium modules and the file system.
        'defaultLanguage',
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

    it('sends the nine unshown members back UNCHANGED rather than clearing them', () => {
      // Required rather than tidy: the update resource REPLACES every column it names, so a
      // member sent as absent is a member cleared. Two of these would do worse than clear —
      // the site-log retention is one of the six the server compares for a non-host caller, and
      // the administrator must be returned because the server refuses an update that would
      // leave the portal without one.
      submit();

      const write = takeSave(0);
      const body = bodyOf(write);

      expect(body.administratorId).toBe(2);
      expect(body.logoFile).toBe('logo.gif');
      expect(body.backgroundFile).toBe('back.gif');
      expect(body.currency).toBe('USD');
      expect(body.paymentProcessor).toBe('PayPal');
      expect(body.processorUserId).toBe('merchant-account');
      expect(body.siteLogHistory).toBe(-1);
      expect(body.defaultLanguage).toBe('en-US');
      expect(body.homeDirectory).toBe('Portals/0');
      completeSave(write);
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
      // The site wizard and the portal-template screen are reference inputs only: the wizard's
      // remaining subject matter IS these same columns, presented as two tabs a caller may visit
      // in any order, and no portal-template resource is published to call.
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

  // =========================================================================
  // Q. ACCESSIBILITY
  // =========================================================================
  //
  // The legacy section head rendered its toggle with a NEGATIVE TAB INDEX
  // (`sectionheadcontrol.ascx:L3`), which put every collapsible group beyond keyboard reach.
  // The target reverses that defect rather than reproducing it, and these cases hold it reversed.
  //
  // The screen also declares no semantic landmark of its own: the application shell owns each of
  // them exactly once, and a second would create two "main" regions on one page.

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

      expect(rendered.length).toBe(12);

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

    it('names a radio GROUP from its caption, the group having no single control to point at', () => {
      // Measured requirement rather than an omission: 5 of the 186 legacy labels declare no
      // control association at all, and a group of radios is exactly that case — a `for` naming
      // one member would label that member instead of the group. The wrapper leaves the attribute
      // off and references the caption from the group instead, so the association still exists.
      const banners = required(fieldLabelled('Banners:'), 'the banners field');

      expect(banners.for).toBe('');

      const element = required(fieldElementLabelled('Banners:'), 'the banners field element');
      const caption = required(element.querySelector('label'), 'the banners caption');
      const slot = required(
        element.querySelector('.form-field__control'),
        'the banners control group',
      );

      // No dangling reference: the caption is not pointed at a control, and the GROUP is pointed
      // at the caption instead.
      expect(caption.hasAttribute('for')).toBeFalse();
      expect(slot.getAttribute('aria-labelledby')).toBe(caption.id);
      expect(caption.id.length).toBeGreaterThan(0);
      expect(query(`#${caption.id}`)).not.toBeNull();

      // The same wiring names the four page selectors' groups, which DO also name their control.
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

  // =========================================================================
  // R. ARCHITECTURE
  // =========================================================================
  //
  // The facts a reader cannot check by reading the template, and the two that fail SILENTLY when
  // they are wrong: the route input's NAME, because router input binding matches by name and a
  // rename severs the binding with no compile error and no runtime complaint; and the tracking
  // expression on every repeat, because a missing one degrades rendering rather than failing.

  describe('R. architecture', () => {
    it('is the class the route contract loads, and carries its own root element', () => {
      // The framework mounts a component under test inside a plain container of its own making,
      // so the host tag here is NOT the component's selector and asserting it would assert the
      // test harness. The SELECTOR is proven separately, at the foot of this file, by rendering
      // the component from a host template that names it.
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
      fixture.detectChanges();
    });

    it('reaches no transport type of its own and imports no environment module', () => {
      // Data access goes through the store and the two services, so the screen holds no client
      // and composes no URL. The relative prefix is asserted through the requests the STORE and
      // the SERVICES issue, which every case above does.
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

      expect(Object.keys(raw).length).toBe(16);
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


// ---------------------------------------------------------------------------
// The selector, proven by naming it
// ---------------------------------------------------------------------------
//
// Its own suite because it needs its own host. The framework mounts a component under test inside
// a container it creates itself, so the host element in the suite above is not the component's
// selector and no assertion there could establish it. Rendering the screen from a template that
// NAMES it does establish it: the element resolves to this component only if the selector matches,
// and strict template checking refuses an element that resolves to nothing.
//
// The host imports the component directly. No module declaration is involved anywhere — the screen
// is standalone, as every component in this workspace is.

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
    // Read from the compiled definition, because the strategy is a compile-time decision with no
    // runtime accessor. `1` is the enumeration member for OnPush; `0` is the default strategy, so
    // the two are distinguishable and a silent regression to the default would fail here.
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

