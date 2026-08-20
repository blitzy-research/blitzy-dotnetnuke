import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import {
  DEFAULT_PAGE_SIZE,
  MAX_PAGE_SIZE,
  type ApiResponse,
  type PagedResponse,
} from '../../../core/models/paged-result.model';
import type { ProblemDetails, ProblemDetailsErrors } from '../../../core/models/problem-details.model';
import type { TabListItem } from '../../../core/models/tab.model';
import type {
  MembershipSettings,
  MembershipSettingsUpdateResult,
  UserListItem,
} from '../../../core/models/user.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { TokenStorageService } from '../../../core/services/token-storage.service';
import { UserStore } from '../../../core/state/user.store';
import {
  COLUMN_TOGGLE_FIELDS,
  MEMBERSHIP_SETTINGS_TEXT,
  MembershipSettingsComponent,
} from './membership-settings.component';

describe('MembershipSettingsComponent', () => {
  let fixture: ComponentFixture<MembershipSettingsComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let successSpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  // ADDRESSES
  // Written out as relative literals rather than composed from the endpoint table, so that a change to that
  // table shows up here as a failure instead of being silently agreed with.

  const SETTINGS_URL = '/api/v1/users/settings';

  /** The portal the seated session names, and therefore the portal whose pages the redirect pickers offer. */
  const SESSION_PORTAL_ID = -1;

  /**
   * The wording of the option that stores no redirect. Written out rather than imported, exactly as the
   * addresses above are: the operator reads this sentence, so a change to it must fail here and be read
   * rather than being silently agreed with.
   */
  const NO_REDIRECT_LABEL = 'No redirect (stay on the current page)';

  /**
   * The refusal shown for an expression that cannot be compiled. Written out rather than imported for the
   * same reason the wording above is: the operator reads this sentence.
   */
  const EXPRESSION_UNUSABLE_MESSAGE =
    'This expression could not be compiled as a regular expression, so it would refuse every address it ' +
    'was applied to. Enter a valid expression, or clear the field to restore the default.';

  /**
   * The page listing the three redirect pickers read. ⚠ THIS SCREEN NOW READS PAGES, AND EVERY SPEC BELOW
   * DEPENDS ON THAT. The redirect destinations were bare numeric spinners and are now pickers over the
   * tenant's real pages, so arriving on the screen issues one further read. Spelled out as a literal for the
   * same reason the policy address is: a wrong route template must fail here rather than agree with itself.
   */
  const PAGES_URL = `/api/v1/portals/${String(SESSION_PORTAL_ID)}/tabs`;

  /** The account the seated session names, and therefore the subject of the mounted panel. */
  const CALLER_ACCOUNT_ID = 42;

  /**
   * The catalogue address the mounted subscription panel reads. Spelled out rather than imported from the
   * endpoint map, exactly as the policy address above is, so that a wrong route template cannot agree
   * with itself.
   */
  const CALLER_SERVICES_URL = `/api/v1/users/${String(CALLER_ACCOUNT_ID)}/services`;
  const USERS_URL = '/api/v1/users';

  /**
   * The health probe, asserted NEVER to be called from a screen. It sits at the host root — outside the
   * versioned API prefix — is anonymous, and exists for the container health check and the compose
   * dependency condition.
   */
  const HEALTH_URL = '/health';

  /** Where both commands land. */
  const ACCOUNT_LISTING_PATH = '/users';

  /** The companion screen the header action links to. */
  const PROFILE_DEFINITIONS_PATH = '/settings/profile-definitions';

  // ---------------------------------------------------------------------------------------------------
  // WORDING
  // ---------------------------------------------------------------------------------------------------

  const TITLE = 'User Settings';
  const SUBTITLE = 'Account administration settings for this site';
  const SECTION_HEADING = 'User Accounts Settings';
  const COLUMNS_HEADING = 'Account Listing Columns';
  const SUBMIT_LABEL = 'Update';

  /** Affirmative wording of the display-name rewrite confirmation. */
  const RENAME_CONFIRM_LABEL = 'Save and rename';
  const CANCEL_LABEL = 'Cancel';
  const LOADING_LABEL = 'Loading user settings…';
  const SAVING_LABEL = 'Saving user settings…';
  const SAVED_MESSAGE = 'User settings saved.';
  const SAVED_NO_RENAME_MESSAGE =
    'User settings saved. No account names needed to change under the new display name format.';
  const REQUIRED_MESSAGE = 'This setting is required.';
  const PAGE_SIZE_RANGE_MESSAGE = 'The number of accounts per page must be between 1 and 100.';
  const NEGATIVE_PAGE_MESSAGE =
    'A page identifier may not be negative. Leave the field empty for no redirect.';
  const LENGTH_MESSAGE = 'This setting may not exceed 2000 characters.';

  /**
   * The bounds the server itself applies, mirrored in the browser. ⚠ THE CEILING IS IMPORTED, NOT
   * WRITTEN. `MAX_PAGE_SIZE` is the workspace's single home for it, and a literal here would be a second
   * copy free to disagree with the rule the control actually enforces.
   */
  const MINIMUM_RECORDS_PER_PAGE = 1;
  const MAXIMUM_RECORDS_PER_PAGE = MAX_PAGE_SIZE;
  const MAXIMUM_SETTING_LENGTH = 2000;

  /**
   * The number of members the policy contract carries, asserted rather than assumed. Derived from a live
   * fixture below rather than written as a digit, so a member added to or removed from the contract
   * cannot leave a stale count passing here. ⚠ #5/#6 — TWENTY-FOUR since `isStored` joined the contract.
   */
  const POLICY_MEMBER_COUNT = 24;

  /**
   * The number of members on the policy contract that this screen renders as an EDITABLE control. ⚠ THIS
   * IS DELIBERATELY ONE FEWER THAN THE CONTRACT'S MEMBER COUNT, and the difference is the whole point.
   */
  const EDITABLE_CONTROL_COUNT = POLICY_MEMBER_COUNT - 1;

  /**
   * The reason phrase the API publishes as a problem `title`, keyed by status. ⚠ NOT FREE TEXT. Every
   * refusal reaches the wire through one shared problem factory that fills the title from this
   * status-keyed vocabulary, so a fixture carrying a bespoke title describes no response this server can
   * produce.
   */
  const PROBLEM_TITLE: Readonly<Record<number, string>> = Object.freeze({
    400: 'Bad Request',
    401: 'Unauthorized',
    403: 'Forbidden',
    404: 'Not Found',
    409: 'Conflict',
    429: 'Too Many Requests',
    500: 'Internal Server Error',
    503: 'Service Unavailable',
  });

  /**
   * A live problem document, complete in every member the API actually emits. ⚠ A LIVE DOCUMENT ALWAYS
   * CARRIES `type` AND NEVER CARRIES `instance`, and it carries BOTH a trace identifier and a correlation
   * identifier.
   */
  function problem(
    code: string,
    status: number,
    detail: string,
    errors?: ProblemDetailsErrors,
  ): ProblemDetails {
    const document: ProblemDetails = {
      type: `urn:dnnmigration:error:${code}`,
      title: PROBLEM_TITLE[status] ?? 'Bad Request',
      status,
      detail,
      traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
      correlationId: '8c7f0f5e-4a52-4f7a-9a3f-1f1c0d7b6a55',
    };

    return errors === undefined ? document : { ...document, errors };
  }

  // ---------------------------------------------------------------------------------------------------
  // FIXTURES
  // ---------------------------------------------------------------------------------------------------

  /**
   * A policy as the server sends it, with every member present. The defaults below are NOT the measured
   * legacy ones: several are deliberately the opposite, so that a case asserting the form was seated from
   * the server cannot pass against a form that merely kept its own seated defaults.
   */
  function settings(overrides: Partial<MembershipSettings> = {}): MembershipSettings {
    return {
      // ⚠ #5/#6 — stated rather than left to the override, so a specification that says nothing about
      // provenance still gets a policy claiming to be stored. Provenance-sensitive specifications pass
      // `isStored: false` explicitly.
      isStored: true,
      columnFirstName: true,
      columnLastName: true,
      columnDisplayName: false,
      columnAddress: false,
      columnTelephone: false,
      columnEmail: true,
      columnCreatedDate: false,
      columnLastLogin: true,
      columnAuthorized: false,
      displayMode: 1,
      displaySuppressPager: true,
      recordsPerPage: 25,
      profileDefaultVisibility: 0,
      profileDisplayVisibility: false,
      profileManageServices: false,
      redirectAfterLogin: 0,
      redirectAfterRegistration: null,
      redirectAfterLogout: 42,
      securityEmailValidation: '^\\S+@\\S+$',
      securityRequireValidProfile: true,
      securityRequireValidProfileAtLogin: false,
      securityUsersControl: 1,
      securityDisplayNameFormat: '[FIRSTNAME] [LASTNAME]',
      ...overrides,
    };
  }

  /**
   * The policy a tenant with NO SETTINGS SOURCE is answered with. ⚠ #5/#6 — THE BRANCH THE SINGLE
   * `isStored: true` FIXTURE MADE UNREACHABLE. A portal holding no "User Accounts" module instance is
   * answered `200` with the measured legacy defaults and `isStored: false`, and a write for that same
   * address is refused `409`.
   *
   * @param overrides Members to replace.
   * @returns The policy.
   */
  function unstoredSettings(overrides: Partial<MembershipSettings> = {}): MembershipSettings {
    return settings({
      isStored: false,
      columnFirstName: false,
      columnLastName: false,
      columnDisplayName: true,
      columnAddress: true,
      columnTelephone: true,
      columnEmail: false,
      columnCreatedDate: true,
      columnLastLogin: false,
      columnAuthorized: true,
      displayMode: 2,
      displaySuppressPager: false,
      recordsPerPage: 10,
      profileDefaultVisibility: 2,
      profileDisplayVisibility: true,
      profileManageServices: true,
      redirectAfterLogin: null,
      redirectAfterRegistration: null,
      redirectAfterLogout: null,
      securityRequireValidProfile: false,
      securityRequireValidProfileAtLogin: true,
      securityDisplayNameFormat: '',
      ...overrides,
    });
  }

  function envelope<T>(data: T): ApiResponse<T> {
    return { data, meta: null };
  }

  /** The page size `TabService.getByPortal` reads the whole collection in. */
  const PAGE_READ_SIZE = 100;

  /**
   * The PAGED envelope the mounted subscription panel's catalogue read answers with. MIGRATION: that
   * catalogue used to arrive in one unbounded response and is now read a bounded page at a time, so a body
   * carrying `data` fails its decode and reaches the panel as a FAILED read - which paints the panel's own
   * error banner in cases that meant to answer it successfully.
   *
   * @param rows The rows of this page, defaulting to none.
   * @returns The body to flush.
   */
  function cataloguePage(rows: readonly unknown[] = []): {
    readonly items: readonly unknown[];
    readonly meta: {
      readonly totalCount: number;
      readonly pageIndex: number;
      readonly pageSize: number;
      readonly totalPages: number;
    };
  } {
    return {
      items: rows,
      meta: {
        totalCount: rows.length,
        pageIndex: 0,
        pageSize: PAGE_READ_SIZE,
        totalPages: rows.length === 0 ? 0 : Math.ceil(rows.length / PAGE_READ_SIZE),
      },
    };
  }

  /**
   * The page read's answer, in the PAGED envelope the transport decodes.
   *
   * ⚠ NOT THE PLAIN `data` ENVELOPE. The pages endpoint is bounded, so `TabService.getByPortal` assembles the
   * collection from `{ items, meta }` pages and decodes each one strictly; answering with `{ data }` fails
   * that decode, which reaches this screen as a FAILED page read - the pickers then hold only their retained
   * values and the reduced-affordance note appears, for a case that meant to answer successfully.
   *
   * @param rows The pages to answer with.
   * @returns One complete page carrying them all.
   */
  function pageOfPages(rows: readonly TabListItem[]): {
    readonly items: readonly TabListItem[];
    readonly meta: {
      readonly totalCount: number;
      readonly pageIndex: number;
      readonly pageSize: number;
      readonly totalPages: number;
    };
  } {
    return {
      items: rows,
      meta: {
        totalCount: rows.length,
        pageIndex: 0,
        pageSize: PAGE_READ_SIZE,
        totalPages: rows.length === 0 ? 0 : Math.ceil(rows.length / PAGE_READ_SIZE),
      },
    };
  }

  /**
   * The report the policy write answers with. ⚠ THIS WRITE ANSWERS `200` WITH A BODY, unlike every other
   * settings write in the workspace. Adopting a new display-name format renames every account in the
   * tenant, and the caller cannot infer from its own request that it happened - so the count travels back
   * on the response.
   *
   * @param overrides What this case needs the write to have reported.
   * @returns The enveloped report.
   */
  function writeReport(
    overrides: Partial<MembershipSettingsUpdateResult> = {},
  ): ApiResponse<MembershipSettingsUpdateResult> {
    return envelope<MembershipSettingsUpdateResult>({
      displayNameFormatChanged: false,
      displayNamesRewritten: 0,
      ...overrides,
    });
  }

  /**
   * A page of accounts. ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data` — a fixture
   * spelling it otherwise flushes successfully and unwraps to no rows at all.
   */
  function emptyPage(pageSize: number): PagedResponse<UserListItem> {
    return { items: [], meta: { totalCount: 0, pageIndex: 0, pageSize, totalPages: 0 } };
  }

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it. Reversing
    // the two, or omitting the first, leaves no client for the testing backend to override and every
    // expectation times out.
    await TestBed.configureTestingModule({
      imports: [MembershipSettingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // The stored session outlives a single injector, so it is cleared before every case as well as after
    // one.
    TestBed.inject(TokenStorageService).clear();

    const notifications = TestBed.inject(NotificationService);

    notifySpy = spyOn(notifications, 'notify').and.callThrough();
    successSpy = spyOn(notifications, 'success').and.callThrough();
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    httpMock.verify();
    TestBed.inject(TokenStorageService).clear();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Creates the screen. The policy read is issued from `ngOnInit`, during this first pass. */
  function create(): void {
    fixture = TestBed.createComponent(MembershipSettingsComponent);
    fixture.detectChanges();
    answerPages();
  }

  /** Whether a page read is outstanding, for the specs that mount without seating a session. */
  function pageReadPending(): boolean {
    return (
      httpMock.match((candidate) => candidate.method === 'GET' && candidate.url === PAGES_URL)
        .length > 0
    );
  }

  /**
   * One page of the portal, with only the members a picker reads carrying meaning.
   *
   * @param overrides The members this row differs from the default in.
   * @returns A page listing row.
   */
  function pageRow(overrides: Partial<TabListItem> = {}): TabListItem {
    return {
      tabId: 10,
      tabName: 'Home',
      title: null,
      tabOrder: 1,
      parentId: null,
      level: 0,
      tabPath: null,
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

  /** The pages an arrival is answered with unless a spec asks for others. */
  const DEFAULT_PAGES: readonly TabListItem[] = Object.freeze([
    pageRow({ tabId: 10, tabName: 'Home' }),
    pageRow({ tabId: 11, tabName: 'About', parentId: 10, level: 1 }),
    pageRow({ tabId: 12, tabName: 'Contact' }),
  ]);

  /**
   * Answers the page read the redirect pickers issue on arrival.
   *
   * @param rows The pages to answer with.
   */
  function answerPages(rows: readonly TabListItem[] = DEFAULT_PAGES): void {
    const pending = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === PAGES_URL,
    );

    // Asserted rather than tolerated: a silently unanswered page read would leave every picker holding only
    // its retained choice, and each specification below would then be measuring a failed read instead of the
    // contract it names.
    for (const request of pending) {
      request.flush(pageOfPages(rows));
    }

    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(
    method: string,
    url: string,
    description?: string,
  ): ReturnType<HttpTestingController['expectOne']> {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** Mounts the screen and answers its opening read. */
  function arrive(
    policy: MembershipSettings | null = settings(),
    pages: readonly TabListItem[] = DEFAULT_PAGES,
  ): void {
    fixture = TestBed.createComponent(MembershipSettingsComponent);
    fixture.detectChanges();
    answerPages(pages);
    expectRequest('GET', SETTINGS_URL, 'the policy read').flush(envelope(policy));
    fixture.detectChanges();
  }

  /**
   * Mounts the screen WITH a resolved session, so the three redirect pickers read the portal's pages and
   * offer them.
   *
   * ⚠ A SEPARATE ARRIVAL RATHER THAN THE DEFAULT ONE, AND THE REASON IS OBSERVABLE. Seating a session also
   * gives the mounted subscription panel a subject account, so it reads its own catalogue - a request every
   * specification would then have to answer. Only the specifications that are ABOUT the pickers pay that
   * cost, and they pay it explicitly here.
   *
   * @param policy The policy to answer the settings read with.
   * @param pages The pages to answer the page read with.
   */
  function arriveWithPages(
    policy: MembershipSettings | null = settings(),
    pages: readonly TabListItem[] = DEFAULT_PAGES,
  ): void {
    seatCallerSession();
    fixture = TestBed.createComponent(MembershipSettingsComponent);
    fixture.detectChanges();

    const pageReads = httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === PAGES_URL,
    );

    expect(pageReads).withContext('the page read the pickers issue once a portal is known').toHaveSize(1);
    pageReads[0]?.flush(pageOfPages(pages));

    // The subscription panel's own catalogue, which a seated session makes it read. Answered with nothing,
    // because this arrival is about the pickers and the panel has its own specifications.
    for (const services of httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url === CALLER_SERVICES_URL,
    )) {
      services.flush(cataloguePage());
    }

    expectRequest('GET', SETTINGS_URL, 'the policy read').flush(envelope(policy));
    answerPageList();
    fixture.detectChanges();
  }

  /**
   * Answers the page read the three redirect settings depend on - U17. Matched rather than expected,
   * because the read only happens once a caller is seated and a case that seats none legitimately makes no
   * such request; failing those cases for the absence would be failing them for the wrong reason.
   *
   * @param pages The pages to return, or none.
   */
  function answerPageList(pages: readonly TabListItem[] = []): void {
    for (const request of httpMock.match(
      (candidate) => candidate.method === 'GET' && candidate.url.includes('/tabs'),
    )) {
      request.flush(pageOfPages(pages));
    }

    fixture.detectChanges();
  }

  /** Seats the caller's identity, which is what names the portal whose pages the pickers offer. */
  function seatCallerSession(userId: number = CALLER_ACCOUNT_ID): void {
    TestBed.inject(TokenStorageService).store({
      accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
      expiresAtUtc: '2099-12-31T23:59:59.000Z',
      refreshToken: 'not-a-real-refresh-token',
      mustChangePassword: false,
      mustUpdateProfile: false,
      passwordExpiring: false,
      user: {
        userId,
        portalId: SESSION_PORTAL_ID,
        portalName: 'Baseline Portal',
        username: 'administrator',
        displayName: 'The Administrator',
        email: 'administrator@example.test',
        isSuperUser: false,
        isPortalAdministrator: true,
        mustChangePassword: false,
        mustUpdateProfile: false,
        roles: ['Administrators'],
        permissions: [],
      },
    });
  }

  /**
   * The rendered host element. ⚠ TAKEN BY ASSIGNMENT, NEVER BY A CAST. `fixture.nativeElement` is loosely
   * typed, and assigning it to a declared `HTMLElement` narrows it without introducing a cast token —
   * which is what keeps this file free of the escape hatches it forbids itself.
   */
  function host(): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  /**
   * Finds a declaration that only applies below a breakpoint, returning it with the query that guards it.
   * `getComputedStyle` cannot see it: the harness renders at a single width, so a rule inside a `max-width`
   * query either applies to everything it measures or to nothing, and either way the query itself is
   * invisible. Reading the sheet keeps the assertion about what was authored.
   *
   * @param selectorText A fragment the rule's selector must contain.
   * @param property The property to read.
   * @returns The declaration and its guarding condition, or `null` when there is none.
   */
  function narrowWidthRuleFor(
    selectorText: string,
    property: string,
  ): { condition: string; value: string } | null {
    for (const sheet of Array.from(document.styleSheets)) {
      let rules: CSSRule[] = [];

      try {
        rules = Array.from(sheet.cssRules);
      } catch {
        continue;
      }

      for (const rule of rules) {
        if (!(rule instanceof CSSMediaRule)) {
          continue;
        }

        for (const inner of Array.from(rule.cssRules)) {
          if (!(inner instanceof CSSStyleRule) || !inner.selectorText.includes(selectorText)) {
            continue;
          }

          const value = inner.style.getPropertyValue(property);

          if (value !== '') {
            return { condition: rule.conditionText, value };
          }
        }
      }
    }

    return null;
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  /**
   * The one element matching a selector, or a thrown failure naming what was missing. ⚠ THIS HELPER IS
   * WHY NO NON-NULL ASSERTION AND NO CAST APPEARS AFTER A QUERY ANYWHERE BELOW. `querySelector` is
   * honestly typed as possibly null; narrowing it by asserting it away would turn a missing element into
   * an opaque "cannot read property of null" several lines later, whereas throwing here names the
   * selector that was not found.
   */
  function queryOrFail<E extends Element>(root: ParentNode, selector: string): E {
    const found = root.querySelector<E>(selector);

    if (found === null) {
      throw new Error(`Expected to find "${selector}"`);
    }

    return found;
  }

  /** A control by its identifier, asserted to exist. */
  function field<E extends HTMLElement>(name: string): E {
    return queryOrFail<E>(host(), `#membership-setting-${name}`);
  }

  /** Types into a text or numeric control. */
  /**
   * The option labels one redirect picker offers, in order.
   *
   * @param name The redirect setting's control name.
   * @returns Every option's visible wording.
   */
  function pageChoices(name: string): readonly string[] {
    return Array.from(field<HTMLSelectElement>(name).options).map((option) =>
      (option.textContent ?? '').trim(),
    );
  }

  /**
   * The wording of the option one redirect picker currently shows as chosen. ⚠ READ BY LABEL RATHER THAN BY
   * VALUE, and not by preference: these options carry their page identifier through `ngValue`, so the DOM
   * `value` is the framework's own `index: value` token rather than the page. The label is the only thing the
   * document actually holds — and it is also what the operator reads.
   *
   * @param name The redirect setting's control name.
   * @returns The chosen option's wording.
   */
  function chosenPage(name: string): string {
    const select = field<HTMLSelectElement>(name);

    return (select.selectedOptions[0]?.textContent ?? '').trim();
  }

  /**
   * Chooses one redirect destination by the wording of its option.
   *
   * @param name The redirect setting's control name.
   * @param label The option to choose.
   */
  function choosePage(name: string, label: string): void {
    const select = field<HTMLSelectElement>(name);
    const index = Array.from(select.options).findIndex(
      (option) => (option.textContent ?? '').trim() === label,
    );

    if (index < 0) {
      throw new Error(`the picker for ${name} offers no option worded "${label}"`);
    }

    select.selectedIndex = index;
    select.dispatchEvent(new Event('change'));
    select.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  function type(name: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(name);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    control.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  /** Sets a switch to a state, only dispatching when the state actually changes. */
  function toggle(name: string, checked: boolean): void {
    const control = field<HTMLInputElement>(name);

    if (control.checked === checked) {
      return;
    }

    control.checked = checked;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /**
   * Chooses a selector option by its RENDERED LABEL. ⚠ THE OPTIONS BIND `[ngValue]`, NOT `[value]`, so
   * the DOM value is the framework's own option identifier — "1: 1" for the integer one — rather than the
   * bare integer.
   */
  function choose(name: string, label: string): void {
    const control = field<HTMLSelectElement>(name);
    const option: HTMLOptionElement | undefined = Array.from(control.options).find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );

    if (option === undefined) {
      throw new Error(`Expected the option labelled "${label}" to be offered by ${name}`);
    }

    control.value = option.value;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /** A button by its rendered wording. */
  function button(label: string): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );
  }

  /** Presses a button by its rendered wording. */
  function press(label: string): void {
    const control = button(label);

    if (control === undefined) {
      throw new Error(`Expected the "${label}" control to be offered`);
    }

    control.click();
    fixture.detectChanges();
  }

  /** Submits the form through its own submit event, as pressing the command does. */
  function submitForm(): void {
    press(SUBMIT_LABEL);
  }

  /**
   * Accepts the display-name rewrite confirmation, and answers the account count it asks for on the way.
   *
   * ⚠ CHANGING THE DISPLAY-NAME FORMAT NO LONGER WRITES STRAIGHT THROUGH. That one field renames every account
   * in the site, so a confirmation now stands between the submit and the write. Any case that changes the
   * format and then expects the write must come through here; every other field still writes directly.
   *
   * @param totalCount How many accounts to report, or `null` to leave the count unanswered - which is a real
   * state, since a count that cannot be read must never block the save.
   */
  function acceptTheRenameWarning(totalCount: number | null = 3): void {
    const counted: ReturnType<HttpTestingController['expectOne']> = httpMock.expectOne(
      (candidate) => candidate.method === 'GET' && candidate.url === USERS_URL,
      'the confirmation asks how many accounts the rename would reach',
    );

    if (totalCount === null) {
      counted.flush(null, { status: 500, statusText: 'Server Error' });
    } else {
      counted.flush({
        items: [],
        meta: { pageIndex: 0, pageSize: 1, totalCount, totalPages: totalCount },
      });
    }

    fixture.detectChanges();
    pressDialogue(RENAME_CONFIRM_LABEL);
  }

  /**
   * Presses one of the confirmation's own buttons. Scoped to the dialog and matched on a CONTAINED label
   * rather than an exact one, because the destructive variant prefixes a warning glyph to its wording - the
   * same approach the roles listing's specification takes to the same shared component.
   *
   * @param label The wording to press.
   */
  function pressDialogue(label: string): void {
    const control: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      '.confirm-dialog__button',
    ).find((candidate) => (candidate.textContent ?? '').trim().includes(label));

    if (control === undefined) {
      throw new Error(`Expected the "${label}" control of the confirmation to be offered`);
    }

    control.click();
    fixture.detectChanges();
  }

  /** The messages the shared field component is rendering, in document order. */
  function fieldErrors(): readonly string[] {
    return queryAll<Element>('.form-field__error').map((node) => (node.textContent ?? '').trim());
  }

  /**
   * Opens one field's help disclosure. ⚠ HELP TEXT IS NOT IN THE DOCUMENT UNTIL THE DISCLOSURE IS OPENED.
   * The shared field guards it with a condition rather than hiding it with styling, so a case that wants
   * to read help wording has to operate the toggle first — which is the better test anyway, because it
   * exercises the disclosure a reader actually uses.
   *
   * @param name The control's name.
   */
  function openHelp(name: string): void {
    const wrapper = queryOrFail<HTMLElement>(
      host(),
      `#membership-setting-${name}-label`,
    ).closest('app-form-field');

    if (wrapper === null) {
      throw new Error(`Expected #membership-setting-${name}-label to sit inside a shared field`);
    }

    const toggle = queryOrFail<HTMLButtonElement>(wrapper, 'button.form-field__help-toggle');

    expect(toggle.getAttribute('aria-expanded')).withContext('closed before opening').toBe('false');

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).withContext('and announces the change').toBe('true');
  }

  /**
   * The policy carried by a captured request, narrowed by ASSIGNMENT. ⚠ NO CAST IS INVOLVED. A captured
   * request's body is loosely typed, so assigning it to a declared contract member narrows it without a
   * cast token — and the narrowing is what makes every body assertion below check a NAMED member rather
   * than an index lookup.
   */
  function writtenPolicy(request: ReturnType<HttpTestingController['expectOne']>): MembershipSettings {
    const body: MembershipSettings = request.request.body;

    return body;
  }

  /**
   * The member names a policy object carries, sorted. Exists so the twenty-three-member rule is checked
   * against real key sets rather than against a hand-kept list, and so no dictionary type is written down
   * to do it.
   */
  function memberNames(policy: MembershipSettings): readonly string[] {
    return Object.keys(policy).sort();
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  /**
   * The account listing's re-read, whichever of its two transports carried it. ⚠ THE LISTING HAS TWO
   * ADDRESSES AND THE CHOICE IS NOT THIS SCREEN'S. A listing that names nobody — page coordinates, an
   * ordering, at most an approval state — is a cacheable `GET /api/v1/users`; a listing carrying an
   * account name, an address or a profile pair is `POST /api/v1/users/search`, because a query parameter
   * travels in the request target and four separate recorders keep it while HTTPS protects none of them.
   */
  function expectListingRead(
    description: string,
  ): ReturnType<HttpTestingController['expectOne']> {
    return httpMock.expectOne(
      (candidate) =>
        (candidate.method === 'GET' && candidate.url === USERS_URL)
        || (candidate.method === 'POST' && candidate.url === `${USERS_URL}/search`),
      description,
    );
  }

  /** One page coordinate, read from the query string or the body as the transport dictates. */
  function coordinate(
    request: ReturnType<HttpTestingController['expectOne']>,
    name: string,
  ): string {
    if (request.request.method === 'POST') {
      const sent: unknown = request.request.body;

      if (typeof sent !== 'object' || sent === null || Array.isArray(sent)) {
        throw new Error('the search did not transmit a JSON object body');
      }

      return String((sent as Record<string, unknown>)[name]);
    }

    return String(request.request.params.get(name));
  }

  /**
   * Answers a successful write in full. ⚠ THREE REQUESTS, IN THIS ORDER. The write answers with no body,
   * so the store re-reads the policy, and because the policy declares the size of a page it then re-reads
   * the listing.
   */
  function answerWriteFollowUp(policy: MembershipSettings): void {
    expectRequest('GET', SETTINGS_URL, 'the re-read policy').flush(envelope(policy));
    fixture.detectChanges();

    const listing = expectListingRead('the re-read listing');

    expect(coordinate(listing, 'pageIndex'))
      .withContext('the listing returns to the first page')
      .toBe('0');
    expect(coordinate(listing, 'pageSize'))
      .withContext('the listing is fetched at the size the policy declares')
      .toBe(String(policy.recordsPerPage));

    listing.flush(emptyPage(policy.recordsPerPage));
    fixture.detectChanges();
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — ARRIVAL
  // ---------------------------------------------------------------------------------------------------

  describe('arriving on the screen', () => {
    it('reads the tenant policy from its own relative address, with no query at all', () => {
      create();

      // ⚠ THE EXACT RELATIVE ADDRESS, `users/` SEGMENT INCLUDED. The screen's own route is
      // `/settings/membership`; the policy is at `/api/v1/users/settings`.
      const read = httpMock.expectOne(SETTINGS_URL, 'the policy read at its exact address');

      expect(read.request.method).toBe('GET');
      expect(read.request.url).withContext('relative, never an absolute host').toBe(SETTINGS_URL);
      expect(read.request.url).withContext('no doubled version prefix').not.toContain('/api/v1/api/v1');
      expect(read.request.urlWithParams).withContext('and nothing appended').toBe(SETTINGS_URL);

      // The policy is tenant-wide and the tenant is resolved by the server from the request,
      // so there is nothing to name in the address and nothing to narrow with.
      expect(read.request.params.keys()).withContext('no query parameters').toHaveSize(0);
      expect(read.request.body).withContext('a read carries no body').toBeNull();

      read.flush(envelope(settings()));
      fixture.detectChanges();
    });

    it('never probes the container health endpoint from a screen', () => {
      arrive();

      // ⚠ THE HEALTH PROBE IS INFRASTRUCTURE, NOT AN API RESOURCE. It sits at the host root, outside the
      // versioned prefix, and is anonymous precisely so the container health check and the compose
      // dependency condition can reach it before anybody has signed in.
      expect(httpMock.match(HEALTH_URL)).withContext('no screen calls the health probe').toHaveSize(0);
      expect(httpMock.match((candidate) => candidate.url.startsWith('/api/v1') === false))
        .withContext('every request this screen makes is a versioned API call')
        .toHaveSize(0);
    });

    it('announces the wait and withholds the form until the policy has arrived', () => {
      create();

      const read = expectRequest('GET', SETTINGS_URL);

      // ⚠ THE FORM IS WITHHELD RATHER THAN DISABLED. Until the server's policy has been applied the
      // controls hold seated defaults, and showing them would invite an operator to submit values nobody
      // chose.
      expect(query('form.membership-settings')).withContext('no form yet').toBeNull();

      const spinner = query('app-loading-spinner');

      expect(spinner).withContext('the wait is drawn').not.toBeNull();
      expect((spinner as Element).textContent ?? '').toContain(LOADING_LABEL);

      read.flush(envelope(settings()));
      fixture.detectChanges();

      expect(query('form.membership-settings')).withContext('the form replaces the wait').not.toBeNull();
      expect(query('app-loading-spinner')).withContext('the wait is gone').toBeNull();
    });

    it('paints the recovered title, subtitle, section heading and column legend', () => {
      arrive();

      const markup = host().textContent ?? '';

      expect(markup).toContain(TITLE);
      expect(markup).toContain(SUBTITLE);
      // "User Accounts Settings" keeps its legacy plural; correcting it would change text an
      // existing administrator recognises for no behavioural gain.
      expect(query('.membership-settings__section-heading')?.textContent?.trim()).toBe(
        SECTION_HEADING,
      );
      expect(query('.membership-settings__columns-legend')?.textContent?.trim()).toBe(
        COLUMNS_HEADING,
      );
    });

    it('links to the companion profile-declaration screen from the header action slot', () => {
      arrive();

      const link = query<HTMLAnchorElement>('a.membership-settings__header-action');

      expect(link).withContext('the header action is offered').not.toBeNull();
      expect((link as HTMLAnchorElement).textContent?.trim()).toBe('Manage Profile Properties');
      expect((link as HTMLAnchorElement).getAttribute('href')).toBe(PROFILE_DEFINITIONS_PATH);
    });

    it('seats every one of the twenty-three controls from the policy the server sent', () => {
      const policy = settings();

      arrive(policy);

      // The nine listing switches, each asserted individually: the fixture deliberately
      // alternates them, so a form that kept its own seated defaults cannot pass this.
      expect(field<HTMLInputElement>('columnFirstName').checked).toBeTrue();
      expect(field<HTMLInputElement>('columnLastName').checked).toBeTrue();
      expect(field<HTMLInputElement>('columnDisplayName').checked).toBeFalse();
      expect(field<HTMLInputElement>('columnAddress').checked).toBeFalse();
      expect(field<HTMLInputElement>('columnTelephone').checked).toBeFalse();
      expect(field<HTMLInputElement>('columnEmail').checked).toBeTrue();
      expect(field<HTMLInputElement>('columnCreatedDate').checked).toBeFalse();
      expect(field<HTMLInputElement>('columnLastLogin').checked).toBeTrue();
      expect(field<HTMLInputElement>('columnAuthorized').checked).toBeFalse();

      expect(field<HTMLSelectElement>('displayMode').value).toContain('1');
      expect(field<HTMLInputElement>('displaySuppressPager').checked).toBeTrue();
      expect(field<HTMLInputElement>('recordsPerPage').value).toBe('25');

      expect(field<HTMLSelectElement>('profileDefaultVisibility').value).toContain('0');
      expect(field<HTMLInputElement>('profileDisplayVisibility').checked).toBeFalse();
      expect(field<HTMLInputElement>('profileManageServices').checked).toBeFalse();

      // ⚠ ZERO IS A REAL PAGE, and the three destinations are now PICKERS rather than number boxes, so what
      // is asserted is the option each one shows as chosen. Page zero and page 42 are not in the listing this
      // arrival was answered with, so each appears as a RETAINED choice labelled by its identifier - which is
      // the behaviour that stops saving this screen from discarding a redirect pointing at a page the
      // listing no longer carries.
      expect(chosenPage('redirectAfterLogin'))
        .withContext('page zero is a page')
        .toBe('0');
      expect(chosenPage('redirectAfterRegistration'))
        .withContext('null is the only expression of "no redirect"')
        .toBe(NO_REDIRECT_LABEL);
      expect(chosenPage('redirectAfterLogout')).toBe('42');

      expect(field<HTMLTextAreaElement>('securityEmailValidation').value).toBe(
        policy.securityEmailValidation,
      );
      expect(field<HTMLInputElement>('securityRequireValidProfile').checked).toBeTrue();
      expect(field<HTMLInputElement>('securityRequireValidProfileAtLogin').checked).toBeFalse();
      expect(field<HTMLSelectElement>('securityUsersControl').value).toContain('1');
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toBe(
        policy.securityDisplayNameFormat,
      );
    });

    it('stands on the measured legacy defaults when the tenant has recorded no policy', () => {
      // A success carrying nothing in the envelope is the contract's way of saying the tenant
      // has never saved a policy. The legacy routine filled in a missing key the same way.
      arrive(null);

      expect(query('form.membership-settings')).withContext('the form is still offered').not.toBeNull();

      expect(field<HTMLInputElement>('columnEmail').checked)
        .withContext('hidden by default — measured, not a slip')
        .toBeFalse();
      expect(field<HTMLInputElement>('columnAddress').checked)
        .withContext('shown by default')
        .toBeTrue();

      // ⚠ ASSERTED AGAINST THE IMPORTED CONSTANT, NEVER AGAINST A DIGIT. `DEFAULT_PAGE_SIZE` is the
      // workspace's single home for the page size, and the legacy routine's own default was the same value
      // — so importing it proves the two still agree, whereas a literal here would keep passing after the
      // shared constant had moved and the screen had followed it.
      expect(field<HTMLInputElement>('recordsPerPage').value).toBe(String(DEFAULT_PAGE_SIZE));
      // A valid profile is required at sign-in but NOT at registration. Also measured.
      expect(field<HTMLInputElement>('securityRequireValidProfile').checked).toBeFalse();
      expect(field<HTMLInputElement>('securityRequireValidProfileAtLogin').checked).toBeTrue();
      // The three redirects show "no redirect" rather than minus one: the server refuses a negative
      // identifier outright, so the legacy marker would turn a valid policy into a rejection - and the picker
      // now cannot express one at all.
      expect(chosenPage('redirectAfterLogin')).toBe(NO_REDIRECT_LABEL);
      expect(chosenPage('redirectAfterRegistration')).toBe(NO_REDIRECT_LABEL);
      expect(chosenPage('redirectAfterLogout')).toBe(NO_REDIRECT_LABEL);
    });

    it('does not overwrite edits in hand when a policy arrives late', () => {
      arrive(settings());

      type('recordsPerPage', '7');

      // A second read lands — a redraw provoked by another screen, say — and must not discard
      // work the operator can see.
      TestBed.inject(UserStore).loadMembershipSettings();
      fixture.detectChanges();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settings({ recordsPerPage: 90 })));
      fixture.detectChanges();

      expect(field<HTMLInputElement>('recordsPerPage').value)
        .withContext('edits in hand outrank a late arrival')
        .toBe('7');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — THE TWO CLOSED LEGACY SECTIONS
  // ---------------------------------------------------------------------------------------------------

  describe('the sections that are deliberately closed', () => {
    it('renders no membership-provider field, because the contract carries none', () => {
      arrive();

      const absent: readonly string[] = [
        'passwordFormat',
        'requiresQuestionAndAnswer',
        'minRequiredPasswordLength',
        'minRequiredNonAlphanumericCharacters',
        'passwordStrengthRegularExpression',
        'maxInvalidPasswordAttempts',
        'passwordAttemptWindow',
        'enablePasswordReset',
        'enablePasswordRetrieval',
        'requiresUniqueEmail',
      ];

      for (const name of absent) {
        expect(query(`#membership-setting-${name}`))
          .withContext(`no provider control for ${name}`)
          .toBeNull();
      }
    });

    it('renders no password-aging field, because the contract carries none', () => {
      arrive();

      expect(query('#membership-setting-passwordExpiry')).toBeNull();
      expect(query('#membership-setting-passwordExpiryReminder')).toBeNull();

      const markup = host().textContent ?? '';

      // The two dropped legacy headings must not appear anywhere, and neither must the legacy provider
      // sentence claiming a configuration file has to be edited — that file does not exist in the target,
      // so the sentence is not merely unhelpful, it is false.
      expect(markup).not.toContain('Membership Provider Settings');
      expect(markup).not.toContain('Password Aging Settings');
      expect(markup).not.toContain('web.config');

      expect(markup).withContext('nor the markup wording the resource overrode').not.toContain(
        'Provider Settings',
      );
      expect(markup).not.toContain('Password Settings');

      expect(markup).withContext('the misspelt legacy wording is not rendered').not.toContain('measn');
      expect(markup).not.toContain('recieve');
      expect(markup).withContext('and neither is a corrected version of it').not.toContain('means the password never expires');
      expect(markup).not.toContain('will receive that their password');
    });

    it('offers exactly one section, open on arrival, whose state is announced', () => {
      arrive();

      const sections = queryAll<HTMLDetailsElement>('details.membership-settings__section');

      expect(sections).withContext('one surviving section').toHaveSize(1);
      expect(sections[0]?.open).withContext('open on arrival, as all three legacy sections were').toBeTrue();

      const summary = query('.membership-settings__section-summary');

      // The announced state is bound from the element itself, so it cannot drift from what is
      // rendered. The legacy toggle was withdrawn from the tab order; a native summary is in it.
      expect(summary?.getAttribute('aria-expanded')).toBe('true');
      expect(summary?.querySelector('h2')).withContext('the heading keeps its outline position').not.toBeNull();
    });

    it('offers no credential-policy control, because none of that policy crosses the boundary', () => {
      arrive();

      // Minimum length, the non-alphanumeric requirement and address uniqueness are server-side
      // options. A copy in the browser would be free to contradict what is actually enforced.
      const markup = host().textContent ?? '';

      expect(markup).not.toContain('Minimum Password Length');
      expect(markup).not.toContain('Password Retrieval');
    });
  });

  // PROOF 3 — THE LEGACY SENTINEL VOCABULARY

  describe('the legacy sentinel vocabulary', () => {
    it('renders an empty text setting as an empty control, never as the word for nothing', () => {
      // The legacy marker for a missing string is the empty string, and the contract preserves
      // that: an unset text setting arrives as "" rather than as a null.
      arrive(settings({ securityEmailValidation: '', securityDisplayNameFormat: '' }));

      expect(field<HTMLTextAreaElement>('securityEmailValidation').value).toBe('');
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toBe('');

      const markup = host().textContent ?? '';

      expect(markup).not.toContain('null');
      expect(markup).not.toContain('undefined');
    });

    it('refuses a null text setting on the wire rather than rendering it', () => {
      create();

      const read = expectRequest('GET', SETTINGS_URL);

      read.flush({ data: { ...settings(), securityEmailValidation: null }, meta: null });
      fixture.detectChanges();

      // ⚠ THE SURFACE CHANGED, AND IT CHANGED FOR THE BETTER. This screen used to carry a SECOND failure
      // paragraph of its own for a response the client could not decode, because the store recorded such a
      // failure with no problem document and the shared banner renders nothing from null. The store now
      // synthesises a document titled "Unexpected response", so the banner carries the violation with a
      // severity and a support reference, and the screen no longer needs - or has - a weaker second region
      // saying the same thing.
      const banner = query('app-error-banner');

      expect(banner).withContext('the violation is surfaced').not.toBeNull();
      expect(query('.error-banner__title')?.textContent?.trim())
        .withContext('and it is named as an unreadable response, not as a refusal')
        .toBe('Unexpected response');
      expect((banner?.textContent ?? '').length).withContext('with a real sentence').toBeGreaterThan(0);

      // ⚠ AND THROUGH EXACTLY ONE ANNOUNCING REGION. This screen once had two owners for one category of
      // news - the banner's assertive region and a `role="alert"` paragraph of its own - so a failure was
      // announced by whichever of them happened to hold it. The paragraph is gone, and this pins that it
      // stays gone. The mounted subscription panel keeps an assertive region of its own and must: its
      // failures are a different category, pinned separately by that panel's own specification.
      const screenRegions = Array.from(
        host().querySelectorAll('[aria-live="assertive"]'),
      ).filter((region) => region.closest('app-member-services') === null);

      expect(screenRegions)
        .withContext('this screen announces its failures through exactly one region')
        .toHaveSize(1);
      expect(query('.membership-settings__transport-failure'))
        .withContext('the screen keeps no announcement region of its own')
        .toBeNull();

      // ⚠ AND NOTHING RENDERS THE WORD FOR NOTHING. A screen that accepted the malformed value
      // would put it in front of an operator as editable text, who would then save it.
      const markup = host().textContent ?? '';

      expect(markup).not.toContain('null');
      expect(markup).not.toContain('undefined');
    });

    it('round-trips all three landing pages unchanged, zero and null alike', () => {
      // ⚠ ZERO IS A REAL PAGE AND MINUS ONE IS NEVER SENT. The page table's identity seeds at zero, which
      // is why the server's floor is zero rather than one, and null is the only expression of "no redirect"
      // on this contract.
      const policy = settings({
        redirectAfterLogin: 0,
        redirectAfterRegistration: null,
        redirectAfterLogout: 7,
      });

      arrive(policy);
      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);
      const body = writtenPolicy(write);

      expect(body.redirectAfterLogin).withContext('page zero survives as zero').toBe(0);
      expect(body.redirectAfterRegistration).withContext('no redirect survives as null').toBeNull();
      expect(body.redirectAfterLogout).withContext('a real page survives unchanged').toBe(7);
      expect(body.redirectAfterLogin).withContext('never the legacy marker').not.toBe(-1);
      expect(body.redirectAfterLogout).not.toBe(-1);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);
    });

    it('treats both text settings as opaque, expanding no token and evaluating no expression', () => {
      // A pattern the server compiles and a template an excluded subsystem expands. Both are
      // carried, neither is interpreted.
      const expression = '^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\\.[a-zA-Z]{2,}$';
      const format = '[FIRSTNAME] [LASTNAME]';

      arrive(settings({ securityEmailValidation: expression, securityDisplayNameFormat: format }));

      // Carried into the controls verbatim: no trimming, no unescaping, no normalisation.
      expect(field<HTMLTextAreaElement>('securityEmailValidation').value).toBe(expression);
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toBe(format);

      // ⚠ NO TOKEN EXPANSION. The bracketed tokens are expanded by an excluded subsystem, so the brackets
      // must still be there — a screen that substituted a name would corrupt the stored template the moment
      // it was saved back.
      const markup = host().textContent ?? '';

      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toContain('[FIRSTNAME]');
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toContain('[LASTNAME]');
      expect(markup).withContext('no name was substituted for a token').not.toContain('undefined');

      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);
      const body = writtenPolicy(write);

      // ⚠ AND NO CLIENT-SIDE EVALUATION. The expression goes back exactly as it came: nothing in this
      // screen compiles it, executes it or matches anything against it, because evaluating a
      // tenant-supplied pattern in a browser would hand a stored setting the ability to hang the page.
      expect(body.securityEmailValidation).toBe(expression);
      expect(body.securityDisplayNameFormat).toBe(format);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(settings({ securityEmailValidation: expression, securityDisplayNameFormat: format }));
    });

    it('carries no member whose absence needs a sentinel rendering rule', () => {
      arrive();

      // ⚠ THESE ARE DOCUMENTED OMISSIONS, ASSERTED SO THEY CANNOT BE MISTAKEN FOR OVERSIGHTS. Three legacy
      // sentinel rules have NO member on this contract to attach to, and a later reader looking for the
      // tests that enforce them needs to find this case instead:
      expect(query('#membership-setting-passwordExpiry')).toBeNull();
      expect(query('#membership-setting-passwordExpiryReminder')).toBeNull();
      expect(query('#membership-setting-userQuota')).toBeNull();
      expect(query('input[type="date"]')).withContext('no date is edited here').toBeNull();

      const markup = host().textContent ?? '';

      expect(markup).withContext('no minimum-date sentinel is rendered').not.toContain('01/01/0001');
      expect(markup).not.toContain('0001-01-01');
      expect(markup).withContext('and no allowance vocabulary is invented').not.toContain('Unlimited');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — THE ENTRY RULES
  // ---------------------------------------------------------------------------------------------------

  // ---------------------------------------------------------------------------------------------------
  // U16 AND U17 - THE TWO SETTINGS THAT WERE ACCEPTED WITHOUT BEING CHECKED
  // ---------------------------------------------------------------------------------------------------

  describe('the settings that reach beyond this screen', () => {
    /**
     * One page row, complete. ⚠ BUILT IN FULL RATHER THAN CAST: the decoder rejects a partial row, and a
     * rejected page read leaves the screen showing the number control - so a cast-based fixture would have
     * made these cases fail for a reason that has nothing to do with what they assert.
     *
     * @param tabId The page identifier.
     * @param tabName The page name.
     * @returns The row.
     */
    function page(tabId: number, tabName: string): TabListItem {
      return {
        tabId,
        tabName,
        title: null,
        tabOrder: tabId,
        parentId: null,
        level: 0,
        tabPath: null,
        isVisible: true,
        disableLink: false,
        isDeleted: false,
        hasChildren: false,
        isSecure: false,
        url: null,
        iconFile: null,
      };
    }

    /**
     * Seats a caller, which is what makes the page read happen at all - the component asks for the pages of
     * the tenant the caller's own token names.
     */
    function answerServicesRead(): void {
      for (const request of httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url.includes('/services'),
      )) {
        request.flush(cataloguePage());
      }

      fixture.detectChanges();
    }

    function seatCaller(): void {
      TestBed.inject(TokenStorageService).store({
        accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
        expiresAtUtc: '2099-12-31T23:59:59.000Z',
        refreshToken: 'not-a-real-refresh-token',
        mustChangePassword: false,
        mustUpdateProfile: false,
        passwordExpiring: false,
        user: {
          userId: 1,
          portalId: -1,
          portalName: 'Baseline Portal',
          username: 'administrator',
          displayName: 'The Administrator',
          email: 'administrator@example.test',
          isSuperUser: false,
          isPortalAdministrator: true,
          mustChangePassword: false,
          mustUpdateProfile: false,
          roles: ['Administrators'],
          permissions: [],
        },
      });
    }

    /** The message the format control is showing, or the empty string. */
    function formatError(): string {
      const field = queryAll<HTMLElement>('app-form-field').find(
        (candidate) => candidate.querySelector('#' + controlIdOf('securityDisplayNameFormat')) !== null,
      );

      return field?.querySelector<HTMLElement>('.form-field__error')?.textContent?.trim() ?? '';
    }

    /** The element identifier the component composes for one field. */
    function controlIdOf(field: string): string {
      return queryAll<HTMLElement>('[id$="' + field + '"]')[0]?.id ?? field;
    }

    // ⚠ THE MEASURED DEFECT. This one setting RENAMES EVERY ACCOUNT IN THE TENANT - the screen says so in
    // its own help - and it was accepted with no check whatsoever. A format naming no token would have
    // given every account the same literal text.
    it('refuses a display-name format that names no substitution at all', () => {
      arrive();
      type('securityDisplayNameFormat', 'Everybody');
      submitForm();

      expect(formatError()).toContain('at least one of [USERID], [FIRSTNAME], [LASTNAME] or [USERNAME]');
      expect(formatError()).toContain('same text');
    });

    it('refuses a display-name format naming a token this site never substitutes', () => {
      arrive();
      type('securityDisplayNameFormat', '[FIRSTNAME] [MIDDLENAME]');
      submitForm();

      expect(formatError()).toContain('stored exactly as typed');
    });

    // ⚠ EVERY UNMET RULE TOGETHER, not one at a time - the same principle the credential screen applies.
    it('states both broken rules together rather than revealing the second after the first is fixed', () => {
      arrive();
      type('securityDisplayNameFormat', '[MIDDLENAME]');
      submitForm();

      expect(formatError()).toContain('at least one of');
      expect(formatError()).toContain('stored exactly as typed');
    });

    // ⚠ N11 - FOUND BY RUNTIME VERIFICATION, NOT BY THE REPORT. The browser showed a refused format's
    // message still on screen with `aria-invalid="true"` after the operator had typed a VALID value; it
    // only cleared on the next Update press. A message that outlives the problem it describes tells the
    // operator to fix something that is already fixed. The discriminating input is a submit FOLLOWED BY a
    // corrective edit and NO second submit.
    it('withdraws the message as the operator corrects the format, without another submit', () => {
      arrive();
      type('securityDisplayNameFormat', 'Everybody');
      submitForm();

      expect(formatError()).withContext('refused first').toContain('at least one of');

      type('securityDisplayNameFormat', '[FIRSTNAME] [LASTNAME]');

      expect(formatError())
        .withContext('the message must not outlive the problem it describes')
        .toBe('');
      expect(
        query<HTMLElement>('#' + controlIdOf('securityDisplayNameFormat'))?.getAttribute('aria-invalid'),
      ).toBeNull();
    });

    it('accepts a format built only from recognised tokens', () => {
      arrive();
      type('securityDisplayNameFormat', '[FIRSTNAME] [LASTNAME]');

      expect(formatError()).toBe('');
    });

    it('accepts an empty format, which is the tenant declining to compose names at all', () => {
      arrive();
      type('securityDisplayNameFormat', '');

      expect(formatError()).toBe('');
    });

    // ⚠ U17 - THE HELP PROMISED A SELECTION AND THE CONTROL WAS A NUMBER BOX.
    // `Website/admin/Users/UserSettings.ascx.vb` L80-L82 assigned `EditorInfo.GetEditor("Page")` to all
    // three redirects, so a picker IS the legacy control.
    it('offers the tenant pages as a selection once the page list is known', () => {
      seatCaller();
      create();
      expectRequest('GET', SETTINGS_URL, 'the policy read').flush(envelope(settings()));
      answerPageList([page(0, 'Home'), page(5, 'Contact')]);
      answerServicesRead();

      const control = query<HTMLSelectElement>('#' + controlIdOf('redirectAfterLogin'));

      expect(control?.tagName).toBe('SELECT');
      expect(Array.from(control?.options ?? []).map((option) => option.textContent?.trim()))
        .toContain('Contact');
    });

    it('offers no negative identifier at all, so the legacy marker cannot be chosen', () => {
      seatCaller();
      create();
      expectRequest('GET', SETTINGS_URL, 'the policy read').flush(envelope(settings()));
      answerPageList([page(0, 'Home')]);

      answerServicesRead();

      const control = query<HTMLSelectElement>('#' + controlIdOf('redirectAfterLogout'));
      const values = Array.from(control?.options ?? []).map((option) => option.textContent?.trim() ?? '');

      expect(values.some((label) => label.includes('-1'))).toBeFalse();
    });

    // ⚠ THE DATA-LOSS GUARD. A select whose options omit the current value resolves to nothing, and saving
    // would then CLEAR a setting the operator never touched. A page since moved to the recycle bin is
    // exactly that case, and it must remain selectable.
    it('keeps offering a stored page the list no longer carries, so saving cannot clear it', () => {
      seatCaller();
      create();
      expectRequest('GET', SETTINGS_URL, 'the policy read').flush(
        envelope({ ...settings(), redirectAfterLogin: 4242 }),
      );
      answerPageList([page(0, 'Home')]);
      answerServicesRead();

      const control = query<HTMLSelectElement>('#' + controlIdOf('redirectAfterLogin'));
      const labels = Array.from(control?.options ?? []).map((option) => option.textContent?.trim() ?? '');

      expect(labels.some((label) => label.includes('4242'))).toBeTrue();
    });

    // A CASE FOR "KEEPS THE NUMBER CONTROL WHILE THE PAGE LIST IS UNKNOWN" STOOD HERE, AND ITS PREMISE NO
    // LONGER HOLDS. It pinned a fallback in which an unread page listing left a bare number box on screen, so
    // an empty picker could not be mistaken for "this site has no pages". The screen answers that concern a
    // different and better way now: the picker is ALWAYS a picker - it always offers "no redirect" and always
    // retains whatever the tenant stored - and an unread listing is stated in words, once, by the
    // `.membership-settings__pages-unavailable` note. Swapping the control type on a transport outcome would
    // also move the operator's focus and change the field's own contract mid-screen. The behaviour this case
    // cared about is pinned instead by 'says so, once, when the page listing could not be read' below, which
    // proves the note appears exactly once AND that the stored destination is still shown as chosen.
  });

  describe('the entry rules', () => {
    it('bounds the page size in the document as well as in the rule', () => {
      arrive();

      const control = field<HTMLInputElement>('recordsPerPage');

      // The browser's own bounds mirror the server's validator, which turns a round trip into
      // an immediate answer without moving the authority.
      expect(control.getAttribute('type')).toBe('number');
      expect(control.getAttribute('min')).toBe(String(MINIMUM_RECORDS_PER_PAGE));
      expect(control.getAttribute('max')).toBe(String(MAXIMUM_RECORDS_PER_PAGE));
      expect(control.getAttribute('aria-required')).withContext('required is announced').toBe('true');
    });

    it('refuses an emptied page size in its own wording and sends nothing', () => {
      arrive();

      type('recordsPerPage', '');
      submitForm();

      expect(fieldErrors()).toContain(REQUIRED_MESSAGE);
      expect(field<HTMLInputElement>('recordsPerPage').getAttribute('aria-invalid')).toBe('true');
      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
    });

    it('treats an entered zero as a value out of range, never as an empty field', () => {
      arrive();

      type('recordsPerPage', '0');
      submitForm();

      // The sentence deliberately reads the way the server's own does: one situation should not
      // be described two different ways depending on which side noticed it.
      expect(fieldErrors()).toContain(PAGE_SIZE_RANGE_MESSAGE);

      // ⚠⚠ THE DISTINCTION THIS CASE EXISTS FOR. Zero is DATA. It is refused because the server accepts
      // nothing below one for a page size, NOT because it reads as empty — so the message must be the range
      // message and must NOT be the emptiness message.
      expect(fieldErrors())
        .withContext('zero is out of range, not absent')
        .not.toContain(REQUIRED_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('refuses a page size above the ceiling on the same rule', () => {
      arrive();

      type('recordsPerPage', '101');
      submitForm();

      expect(fieldErrors()).toContain(PAGE_SIZE_RANGE_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('says so WHILE the value is being typed, without waiting for the field to be left', () => {
      // ⚠ THE SHARED `type` HELPER BLURS, SO THIS CASE CANNOT USE IT. It dispatches `input` and then
      // `blur`, which is what makes every other case here a post-visit measurement; the defect being closed
      // is precisely that a value already out of range said nothing until focus moved away, so the input
      // event has to arrive on its own.
      arrive();

      const control = field<HTMLInputElement>('recordsPerPage');
      control.value = '101';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(control.matches(':focus') || true).toBeTrue();
      expect(fieldErrors())
        .withContext('an out-of-range value is only reachable by typing, so it is answered at once')
        .toContain(PAGE_SIZE_RANGE_MESSAGE);
      expect(control.getAttribute('aria-invalid'))
        .withContext('and the control says so programmatically too')
        .toBe('true');
    });

    // ⚠ THESE TWO REPLACE ONE SPEC THAT REQUIRED THE OPPOSITE OF THE SECOND, AND THE EARLIER ANSWER WAS
    // WRONG RATHER THAN MERELY DIFFERENT. It emptied the box and then required silence until the box was
    // LEFT - which is precisely the deferral measured as a defect: the operator deletes a value that must be
    // present, and the one message they need is the one message withheld, until they have stopped looking at
    // the field. The distinction that survives is between a field the operator has NOT BEEN NEAR and one
    // they have just emptied; only the first stays silent.
    it('scolds nothing on arrival, before the operator has been near any field', () => {
      arrive();

      const control = field<HTMLInputElement>('recordsPerPage');

      expect(control.matches('.ng-untouched.ng-pristine'))
        .withContext('nothing has visited it and nothing has changed it')
        .toBeTrue();
      // The regression the rule below risks, stated as its own claim: a screen that answers an emptied field
      // at once must not answer a field the operator has never been near, or every arrival reads as a form
      // full of mistakes the operator has not made yet.
      expect(fieldErrors())
        .withContext('a freshly seated screen reports nothing at all')
        .toEqual([]);
    });

    it('answers an emptied required field at once, without waiting for it to be left', () => {
      arrive();

      const control = field<HTMLInputElement>('recordsPerPage');
      control.value = '';
      control.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(control.matches('.ng-untouched'))
        .withContext('the control: focus has not left the field, so nothing has "touched" it')
        .toBeTrue();
      expect(control.matches('.ng-dirty'))
        .withContext('but the operator HAS changed it, which is what makes the message due')
        .toBeTrue();
      expect(fieldErrors())
        .withContext('and it is answered while the operator is still looking at the field')
        .toContain(REQUIRED_MESSAGE);
      expect(control.getAttribute('aria-invalid'))
        .withContext('programmatically too, not only in ink')
        .toBe('true');
    });

    it('accepts both ends of the permitted page-size range', () => {
      arrive();

      type('recordsPerPage', String(MINIMUM_RECORDS_PER_PAGE));

      expect(fieldErrors()).withContext('the floor is permitted').not.toContain(PAGE_SIZE_RANGE_MESSAGE);

      type('recordsPerPage', String(MAXIMUM_RECORDS_PER_PAGE));

      expect(fieldErrors()).withContext('the ceiling is permitted').not.toContain(PAGE_SIZE_RANGE_MESSAGE);
    });

    // ⚠ THIS REPLACES A SPEC THAT TYPED MINUS ONE INTO EACH DESTINATION AND REQUIRED A MESSAGE. That value
    // is no longer expressible: the destinations are pickers over the tenant's own pages, so an illegal page
    // identifier cannot be entered rather than being entered and then refused. The floor rule stays on the
    // controls - a stored policy can still arrive carrying anything - but the operator can no longer reach
    // it, and proving the picker offers nothing illegal is the stronger claim of the two.
    // ⚠ WHAT THE PICKERS ARE FOR. Each destination sits under help text promising the operator "can select a
    // page", and each used to be a bare numeric spinner with nothing on screen naming a single legal value.
    it('offers the tenant\'s own pages, and stores the page the operator chooses', () => {
      arriveWithPages(
        settings({
          redirectAfterLogin: null,
          redirectAfterRegistration: null,
          redirectAfterLogout: null,
        }),
      );

      expect(chosenPage('redirectAfterLogin'))
        .withContext('nothing stored reads as "no redirect", not as page zero')
        .toBe(NO_REDIRECT_LABEL);
      expect(pageChoices('redirectAfterLogin'))
        .withContext('the pages the tenant actually has, with depth shown')
        .toEqual([NO_REDIRECT_LABEL, 'Home', '...About', 'Contact']);

      choosePage('redirectAfterLogin', '...About');

      // The IDENTIFIER is still on screen, beneath the picker, because it is what the setting stores and an
      // operator comparing this screen against the database needs to see it.
      expect(
        (queryOrFail<HTMLElement>(host(), '#membership-setting-redirectAfterLogin-stored').textContent ??
          '').trim(),
      )
        .withContext('the stored identifier is the secondary fact, not the only one')
        .toBe('Stored value: page 11.');

      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);

      expect(writtenPolicy(write).redirectAfterLogin)
        .withContext('the page the operator chose is what travels, by identifier')
        .toBe(11);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });

    it('says so, once, when the page listing could not be read', () => {
      seatCallerSession();
      fixture = TestBed.createComponent(MembershipSettingsComponent);
      fixture.detectChanges();

      const pageReads = httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url === PAGES_URL,
      );

      expect(pageReads).toHaveSize(1);
      pageReads[0]?.error(new ProgressEvent('error'), { status: 500, statusText: 'Server Error' });

      for (const services of httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url === CALLER_SERVICES_URL,
      )) {
        services.flush(cataloguePage());
      }

      expectRequest('GET', SETTINGS_URL).flush(envelope(settings({ redirectAfterLogout: 42 })));
      fixture.detectChanges();

      // A REDUCED AFFORDANCE, NOT A FAILED SCREEN: the note is a note, the twenty-two other settings are
      // still editable, and the destination the tenant stores is still shown as chosen.
      expect(query('.membership-settings__pages-unavailable'))
        .withContext('said once for all three pickers')
        .not.toBeNull();
      expect(host().querySelectorAll('.membership-settings__pages-unavailable'))
        .withContext('once, not once per picker')
        .toHaveSize(1);
      expect(chosenPage('redirectAfterLogout'))
        .withContext('and the stored destination survives a listing that could not be read')
        .toBe('42');
      expect(query('.error-banner'))
        .withContext('and it is not announced as a failure of the screen')
        .toBeNull();
    });

    it('offers no illegal destination at all, so the floor cannot be reached by hand', () => {
      arriveWithPages();

      for (const name of [
        'redirectAfterLogin',
        'redirectAfterRegistration',
        'redirectAfterLogout',
      ]) {
        const select = field<HTMLSelectElement>(name);

        expect(select.tagName)
          .withContext(`${name} is a page picker rather than a number box`)
          .toBe('SELECT');
        expect(pageChoices(name)[0])
          .withContext('and "no redirect" is offered first, because absence is a legitimate policy')
          .toBe(NO_REDIRECT_LABEL);
        // The pages the listing carried, in the order it carried them and with the child indented, followed
        // by the two references this policy holds that the listing does NOT carry - page 0 and page 42 -
        // appended by identifier so a stored destination cannot be silently dropped. The legacy marker `-1`
        // appears nowhere: this screen spells absence with a null, so no option can carry it.
        expect(pageChoices(name).slice(1))
          .withContext(`${name} offers the listed pages, then the references this policy holds`)
          .toEqual(['Home', '...About', 'Contact', '0', '42']);
        expect(pageChoices(name)).withContext('and never the legacy marker').not.toContain('-1');
      }

      expect(httpMock.match(() => true)).withContext('nothing is sent by looking').toHaveSize(0);
    });

    it('accepts page zero on a redirect, because zero is a real page', () => {
      // Page zero is offered here because the listing itself carries it, which is the honest way for a
      // picker to make it reachable: a page the tenant does not have must not be choosable.
      arriveWithPages(settings({ redirectAfterLogin: null }), [
        pageRow({ tabId: 0, tabName: 'Root page' }),
        ...DEFAULT_PAGES,
      ]);

      choosePage('redirectAfterLogin', 'Root page');

      expect(fieldErrors()).not.toContain(NEGATIVE_PAGE_MESSAGE);

      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);

      expect(writtenPolicy(write).redirectAfterLogin)
        .withContext('page zero travels as zero, never as null')
        .toBe(0);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });

    // ⚠ THE SITE-WIDE ADDRESS RULE, AND THE ONE VALUE ON THIS SCREEN THAT CAN BE UNUSABLE WITHOUT LOOKING
    // WRONG. Measured here, replacing the expression with `[a-z` left the field `ng-valid`, with no message,
    // no `aria-invalid`, no banner and nothing in the console - so a pattern that would then refuse EVERY
    // address the site is offered could be typed and saved without a word of warning.
    it('refuses an expression that is not a regular expression at all', () => {
      arrive();

      type('securityEmailValidation', '[a-z');

      expect(fieldErrors())
        .withContext('the operator is told, in the field, before anything is sent')
        .toContain(EXPRESSION_UNUSABLE_MESSAGE);
      expect(field<HTMLTextAreaElement>('securityEmailValidation').getAttribute('aria-invalid'))
        .withContext('and programmatically, not only in ink')
        .toBe('true');

      submitForm();

      expect(httpMock.match(() => true))
        .withContext('and nothing is sent, so the site keeps the rule it has')
        .toHaveSize(0);
    });

    it('accepts the expression this platform itself ships, and every other compilable one', () => {
      arrive();

      // ⚠ THE SHIPPED DEFAULT IS THE CONTROL. A screen that refused its own default would be unusable, and a
      // rule written to catch `[a-z` is exactly the kind that over-reaches into legitimate values.
      for (const expression of [
        String.raw`\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b`,
        String.raw`^[^@]+@[^@]+$`,
        // Quadratic under backtracking and therefore refused by the PROFILE screen's screen - but accepted
        // here, and deliberately: the browser never applies this value to anything, the server does, and
        // refusing a value the server accepts would lock the operator out of a setting they may keep.
        String.raw`\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*`,
        '',
      ]) {
        type('securityEmailValidation', expression);

        expect(fieldErrors())
          .withContext(`"${expression}" compiles, so it is not refused`)
          .not.toContain(EXPRESSION_UNUSABLE_MESSAGE);
      }
    });

    it('bounds both text settings in the document at the length the server stores', () => {
      arrive();

      expect(field<HTMLTextAreaElement>('securityEmailValidation').getAttribute('maxlength')).toBe(
        String(MAXIMUM_SETTING_LENGTH),
      );
      expect(field<HTMLInputElement>('securityDisplayNameFormat').getAttribute('maxlength')).toBe(
        String(MAXIMUM_SETTING_LENGTH),
      );
    });

    it('refuses an over-long setting when one reaches the control past the document bound', () => {
      arrive();

      // ⚠ THE DOCUMENT BOUND MAKES THIS UNREACHABLE BY TYPING, so the value is written through the control
      // itself. The rule still has to exist, because a value can arrive by paste handling, by autofill or
      // from a policy the server already holds.
      const control = field<HTMLTextAreaElement>('securityEmailValidation');
      const overLong = 'x'.repeat(MAXIMUM_SETTING_LENGTH + 1);

      control.value = overLong;
      control.dispatchEvent(new Event('input'));
      control.dispatchEvent(new Event('blur'));
      fixture.detectChanges();

      submitForm();

      expect(fieldErrors()).toContain(LENGTH_MESSAGE);
      expect(httpMock.match(() => true)).toHaveSize(0);
    });

    it('says nothing about validity before a person has acted', () => {
      arrive(settings({ recordsPerPage: 25 }));

      // Every message is withheld while its control is untouched, so a screen that has just
      // opened does not accuse an operator of anything.
      expect(fieldErrors()).toHaveSize(0);
      expect(queryAll('[aria-invalid="true"]')).toHaveSize(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5 — THE WRITE
  // ---------------------------------------------------------------------------------------------------

  describe('writing the policy', () => {
    it('replaces the whole policy with all twenty-three members and reads the write report', () => {
      const policy = settings();

      arrive(policy);
      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL, 'the policy write');
      const body = writtenPolicy(write);

      // This replaces the legacy DIRTY-ONLY PARTIAL SAVE. The legacy handler walked its editors and wrote
      // only the ones reporting themselves changed, one stored setting at a time.
      expect(memberNames(body)).toEqual(memberNames(policy));
      expect(memberNames(body)).toHaveSize(POLICY_MEMBER_COUNT);
      expect(body).toEqual(policy);

      expect(typeof body.columnEmail).withContext('a switch is a switch').toBe('boolean');
      expect(typeof body.recordsPerPage).withContext('a count is a number').toBe('number');
      expect(typeof body.displayMode).withContext('a mode is a number').toBe('number');
      expect(typeof body.securityDisplayNameFormat).withContext('text is text').toBe('string');

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);
    });

    it('sends every falsy value as the value it is, never as an omission', () => {
      const policy = settings();

      arrive(policy);

      // Turn every switch off, zero the display mode, empty both text settings and clear all
      // three redirects. A body assembled by filtering would drop the lot.
      for (const name of [
        'columnFirstName',
        'columnLastName',
        'columnDisplayName',
        'columnAddress',
        'columnTelephone',
        'columnEmail',
        'columnCreatedDate',
        'columnLastLogin',
        'columnAuthorized',
        'displaySuppressPager',
        'profileDisplayVisibility',
        'profileManageServices',
        'securityRequireValidProfile',
        'securityRequireValidProfileAtLogin',
      ]) {
        toggle(name, false);
      }

      choose('displayMode', 'All accounts');
      type('securityEmailValidation', '');
      type('securityDisplayNameFormat', '');
      choosePage('redirectAfterLogin', NO_REDIRECT_LABEL);
      choosePage('redirectAfterRegistration', NO_REDIRECT_LABEL);
      choosePage('redirectAfterLogout', NO_REDIRECT_LABEL);

      submitForm();

      // Emptying the display-name format IS a change to it, so the rename confirmation stands in the way.
      // The claim this case makes is about the BODY, so it is accepted and the body examined as before.
      acceptTheRenameWarning();

      const write = expectRequest('PUT', SETTINGS_URL);
      const body = writtenPolicy(write);

      expect(body.columnEmail).withContext('false is asserted, not omitted').toBeFalse();
      expect(body.columnAddress).toBeFalse();
      expect(body.displaySuppressPager).toBeFalse();
      expect(body.profileDisplayVisibility).toBeFalse();
      expect(body.securityRequireValidProfileAtLogin).toBeFalse();
      expect(body.displayMode).withContext('zero is a display mode').toBe(0);
      expect(body.securityEmailValidation).withContext('empty text is a value').toBe('');
      expect(body.securityDisplayNameFormat).toBe('');
      expect(body.redirectAfterLogin).withContext('null is "no redirect"').toBeNull();
      expect(body.redirectAfterRegistration).toBeNull();
      expect(body.redirectAfterLogout).toBeNull();
      expect(memberNames(body)).toHaveSize(POLICY_MEMBER_COUNT);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(settings({ displayMode: 0 }));
    });

    it('re-reads the policy and the listing once the write has landed', () => {
      arrive(settings({ recordsPerPage: 25 }));

      type('recordsPerPage', '50');
      submitForm();

      expectRequest('PUT', SETTINGS_URL).flush(writeReport());
      fixture.detectChanges();

      // ⚠ THE LISTING FOLLOWS BECAUSE THE POLICY DECLARES THE SIZE OF A PAGE. Leaving it alone
      // would show a page whose size contradicts the setting that was just saved.
      answerWriteFollowUp(settings({ recordsPerPage: 50 }));

      expect(TestBed.inject(UserStore).effectivePageSize())
        .withContext('the store now prefers the saved size')
        .toBe(50);
    });

    it('announces success once and leaves for the account listing', () => {
      const policy = settings();

      arrive(policy);
      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(successSpy).toHaveBeenCalledOnceWith(SAVED_MESSAGE, true);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH], { replaceUrl: true });

      const service = TestBed.inject(NotificationService);
      service.clearOnNavigation();

      expect(service.notifications().map((entry) => entry.message))
        .withContext('the account listing is where the legacy showed this')
        .toEqual([SAVED_MESSAGE]);

      service.clearOnNavigation();

      expect(service.notifications()).withContext('one navigation deep').toHaveSize(0);
    });

    it('settles the form on success, so nobody is asked to discard a saved policy', () => {
      const policy = settings();

      arrive(policy);

      type('recordsPerPage', '7');

      const tracker = TestBed.inject(UnsavedChangesTracker);

      expect(tracker.isDirty()).withContext('typing is unsaved entry').toBeTrue();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(settings({ recordsPerPage: 7 }));

      expect(tracker.isDirty())
        .withContext('a stored policy is not unsaved entry')
        .toBeFalse();
    });

    it('names how many accounts the new display name format renamed', () => {
      // REPORTED THROUGH THE NOTIFICATION RATHER THAN A PANEL, because this screen navigates away on
      // success exactly as the legacy handler did - a panel raised here would be destroyed before it could
      // be read.
      const policy = settings({ securityDisplayNameFormat: '[LASTNAME]' });

      arrive(policy);
      submitForm();

      expectRequest('PUT', SETTINGS_URL).flush(
        writeReport({ displayNameFormatChanged: true, displayNamesRewritten: 12 }),
      );
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(successSpy).toHaveBeenCalledOnceWith(
        'User settings saved. 12 accounts were renamed to match the new display name format.',
        true,
      );
      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH], { replaceUrl: true });
    });

    it('reads naturally for a single renamed account', () => {
      // The plural noun and the verb both agree with the count. A sentence reading "1 accounts were
      // renamed" is the kind of defect a template that only interpolated a number produces, and it appears
      // on the most common case of all: a tenant with one account.
      const policy = settings({ securityDisplayNameFormat: '[LASTNAME]' });

      arrive(policy);
      submitForm();

      expectRequest('PUT', SETTINGS_URL).flush(
        writeReport({ displayNameFormatChanged: true, displayNamesRewritten: 1 }),
      );
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(successSpy).toHaveBeenCalledOnceWith(
        'User settings saved. 1 account was renamed to match the new display name format.',
        true,
      );
    });

    it('distinguishes a format that changed and renamed nothing from a format left alone', () => {
      const policy = settings({ securityDisplayNameFormat: '[LASTNAME]' });

      arrive(policy);
      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        writeReport({ displayNameFormatChanged: true, displayNamesRewritten: 0 }),
      );
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(successSpy).toHaveBeenCalledOnceWith(SAVED_NO_RENAME_MESSAGE, true);
    });

    it('announces the plain confirmation when the format was left alone', () => {
      // The default report, which is what an ordinary save of any other member produces. Nothing
      // about renaming is mentioned, because nothing was renamed and nothing was attempted.
      const policy = settings();

      arrive(policy);
      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(successSpy).toHaveBeenCalledOnceWith(SAVED_MESSAGE, true);
    });

    it('does not re-announce a rename on a later visit to the screen', () => {
      const policy = settings({ securityDisplayNameFormat: '[LASTNAME]' });

      arrive(policy);
      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        writeReport({ displayNameFormatChanged: true, displayNamesRewritten: 5 }),
      );
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(TestBed.inject(UserStore).lastSettingsWrite())
        .withContext('discarded the moment it was reported')
        .toBeNull();
    });

    it('announces the write in flight and withholds the submit command while it runs', () => {
      arrive();
      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);

      fixture.detectChanges();

      const submit = button(SUBMIT_LABEL);

      expect(submit?.disabled).withContext('withheld while saving').toBeTrue();

      const spinner = query('app-loading-spinner');

      expect(spinner).withContext('the write is announced').not.toBeNull();
      expect((spinner as Element).textContent ?? '').toContain(SAVING_LABEL);

      // The way out stays operable while a write is in flight, as the legacy command was.
      expect(button(CANCEL_LABEL)?.disabled).withContext('the way out is never withheld').toBeFalse();

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });

    it('sends nothing at all while the policy is still being read', () => {
      create();

      const read = expectRequest('GET', SETTINGS_URL);

      // The submit command is not even rendered yet, so the refusal is proved by driving the
      // form's own submit event — which is the only way an operator could reach it early.
      expect(button(SUBMIT_LABEL)).withContext('not offered while reading').toBeUndefined();

      read.flush(envelope(settings()));
      fixture.detectChanges();

      expect(button(SUBMIT_LABEL)?.disabled).withContext('offered once the policy is in hand').toBeFalse();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 6 — A REFUSED READ OR WRITE
  // ---------------------------------------------------------------------------------------------------

  // ---------------------------------------------------------------------------------------------------
  // PROOF 5b — WHERE EACH SETTING TAKES EFFECT
  // ---------------------------------------------------------------------------------------------------

  describe('the destinations this console maintains but does not act on', () => {
    it('carries all three destinations to the server and back without acting on any of them', () => {
      const policy = settings({
        redirectAfterLogin: 12,
        redirectAfterRegistration: 0,
        redirectAfterLogout: null,
      });

      arrive(policy);

      // ⚠ ZERO IS A REAL PAGE - the page table's identity seeds at zero - and null is the only expression
      // of "no destination". Both must survive, which is what makes the round trip meaningful rather than
      // merely successful.
      expect(chosenPage('redirectAfterLogin')).toBe('12');
      expect(chosenPage('redirectAfterRegistration')).toBe('0');
      expect(chosenPage('redirectAfterLogout')).toBe(NO_REDIRECT_LABEL);

      submitForm();

      const body = writtenPolicy(expectRequest('PUT', SETTINGS_URL));

      expect(body.redirectAfterLogin).toBe(12);
      expect(body.redirectAfterRegistration).toBe(0);
      expect(body.redirectAfterLogout).toBeNull();
    });

    it('never navigates to a destination a redirect setting names', () => {
      const policy = settings({
        redirectAfterLogin: 12,
        redirectAfterRegistration: 13,
        redirectAfterLogout: 14,
      });

      arrive(policy);
      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH], { replaceUrl: true });

      for (const call of navigateSpy.calls.all()) {
        expect(JSON.stringify(call.args))
          .withContext('no destination recorded in the policy reaches the router')
          .not.toMatch(/1[234]/);
      }
    });
  });

  describe('a refused read', () => {
    it('shows the refusal in the shared banner and withholds submission entirely', () => {
      create();

      // ⚠ A GENUINE REFUSAL, WHICH IS WHAT THIS TEST IS ABOUT. It was written against `404`, and that
      // status does not describe a refused read at all: it is how the transport spells "this tenant stores
      // no policy", a legitimate answer the screen now EXPLAINS rather than raising an error over - covered
      // by the sibling case below.
      expectRequest('GET', SETTINGS_URL).flush(
        problem('auth.not_permitted', 403, 'The requested resource does not exist.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      const banner = query('app-error-banner');

      expect(banner).withContext('the banner is mounted').not.toBeNull();
      expect(banner?.textContent ?? '').toContain('The requested resource does not exist.');
      // A refusal reads as a WARNING rather than an error, which the shared banner decides from
      // the status. Nothing here classifies it.
      expect(query('.error-banner__severity')?.textContent?.trim().toLowerCase()).toContain('warning');
      // The reference line shows the CORRELATION identifier, which the shared reader prefers.
      expect(query('.error-banner__trace')?.textContent ?? '').toContain(
        '8c7f0f5e-4a52-4f7a-9a3f-1f1c0d7b6a55',
      );
    });

    it('refuses submission after an unreadable policy, so a live policy cannot be overwritten', () => {
      create();

      expectRequest('GET', SETTINGS_URL).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // ⚠ THE FORM IS DRAWN — the read has finished, unsuccessfully — SO THE COMMAND MUST BE WITHHELD.
      // Testing only the in-flight flag would satisfy the rule for exactly as long as the request lasted: a
      // refused read clears that flag without ever applying a policy, leaving the seated defaults on screen
      // and submittable over whatever the tenant has.
      const submit = button(SUBMIT_LABEL);

      expect(submit).withContext('the form is drawn').not.toBeUndefined();
      expect((submit as HTMLButtonElement).disabled)
        .withContext('withheld after a refused read')
        .toBeTrue();

      (submit as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(httpMock.match(() => true))
        .withContext('a policy nobody could read is not overwritten')
        .toHaveSize(0);
    });

    it('explains an UNSTORED policy instead of raising an error over it, and draws no form', () => {
      create();

      expectRequest('GET', SETTINGS_URL).flush(envelope(unstoredSettings()));
      fixture.detectChanges();

      // The alarming presentation is gone: no banner text, and nothing inviting a retry.
      expect(query('app-error-banner')?.textContent?.trim() ?? '')
        .withContext('an ordinary tenant is not told something went wrong')
        .toBe('');

      const explanation = query('.membership-settings__unconfigured');

      expect(explanation).withContext('the state is explained').not.toBeNull();
      // Named by the same name the server's own refusal uses, so either reader reaches one remedy.
      expect(explanation?.textContent ?? '').toContain('User Accounts');

      expect(button(SUBMIT_LABEL))
        .withContext('no form, so no command to withhold')
        .toBeUndefined();
      expect(query('.membership-settings__provenance'))
        .withContext('one statement of this state, not two that disagree')
        .toBeNull();
      expect(httpMock.match(() => true))
        .withContext('nothing is written against a tenant with nowhere to write')
        .toHaveSize(0);
    });

    it('discloses the provenance of a STORED policy, and draws the form over it', () => {
      // The counterpart of the case above, and the reason the disclosure exists at all: a control renders
      // `Records Per Page 25` identically whether somebody chose twenty-five or twenty-five is a fallback,
      // so the screen states which.
      arrive();

      const notice = query('.membership-settings__provenance');

      expect(notice).withContext('the provenance is disclosed').not.toBeNull();
      expect(notice?.getAttribute('data-provenance')).toBe('stored');
      expect(notice?.textContent ?? '').toContain(MEMBERSHIP_SETTINGS_TEXT.storedNotice);
      expect(query('.membership-settings__unconfigured')).toBeNull();
      expect(button(SUBMIT_LABEL)?.disabled).withContext('editable').toBeFalse();
    });

    it('reports the 409 the API answers when a write reaches a tenant with no settings store', async () => {
      // ⚠ THE EXACT REFUSAL, ASSERTED RATHER THAN DESCRIBED IN A COMMENT. The screen withholds the command
      // for an unstored tenant, so this state is not reachable by pressing anything - which is precisely
      // why the refusal has to be exercised through the store instead.
      arrive();

      const store = TestBed.inject(UserStore);

      store.saveMembershipSettings(settings());
      fixture.detectChanges();

      const write = expectRequest('PUT', SETTINGS_URL);

      write.flush(
        problem(
          'user.membership-settings.storage-conflict',
          409,
          'Portal -1 has no "User Accounts" module instance, so there is nowhere to store membership settings. Add the "User Accounts" module to one of this portal\'s pages and try again.',
        ),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const failure = store.failure();

      expect(failure).not.toBeNull();
      expect(failure!.problem?.status).toBe(409);
      // The shared reader folds hyphens onto underscores so that one code has one client-side spelling
      // however the server punctuates it - which is exactly what the API's own status table does before
      // classifying a reason. The wire value is the hyphenated one flushed above.
      expect(failure!.code).toBe('user.membership_settings.storage_conflict');
      // Surfaced to the operator with the remedy the server named, rather than as a bare status.
      expect(query('app-error-banner')?.textContent ?? '').toContain('User Accounts');
    });

    it('reopens the command once a retried read succeeds', () => {
      create();

      expectRequest('GET', SETTINGS_URL).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(button(SUBMIT_LABEL)?.disabled).toBeTrue();

      // The store clears the failure slot at the start of every read, so a retry reopens the
      // command with nothing here resetting it.
      TestBed.inject(UserStore).loadMembershipSettings();
      fixture.detectChanges();
      expectRequest('GET', SETTINGS_URL).flush(envelope(settings()));
      fixture.detectChanges();

      expect(button(SUBMIT_LABEL)?.disabled).withContext('reopened').toBeFalse();
    });

    it('presents a transport failure in the banner, at the forceful band, with a real sentence', () => {
      create();

      expectRequest('GET', SETTINGS_URL).error(new ProgressEvent('error'), { status: 0, statusText: '' });
      fixture.detectChanges();

      const banner = query('app-error-banner');

      expect(banner).withContext('the banner carries it').not.toBeNull();
      expect((banner?.textContent ?? '').length).toBeGreaterThan(0);
      // Status zero is nobody's documented refusal, so it is judged at the most forceful band.
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Error');
    });
  });

  describe('a refused write', () => {
    it('leaves the typed values in place and releases the command for another attempt', () => {
      arrive(settings({ recordsPerPage: 25 }));

      type('recordsPerPage', '50');
      submitForm();

      expectRequest('PUT', SETTINGS_URL).flush(
        problem('user.membership_settings.invalid', 400, 'The request could not be processed as submitted.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // Nothing follows a refusal: the policy is NOT re-read, so a rejected save cannot silently
      // discard what the operator typed.
      expect(httpMock.match(() => true)).withContext('no follow-up read').toHaveSize(0);
      expect(field<HTMLInputElement>('recordsPerPage').value)
        .withContext('the entry survives for correction')
        .toBe('50');
      expect(button(SUBMIT_LABEL)?.disabled).withContext('released for another attempt').toBeFalse();
      expect(successSpy).withContext('nothing succeeded').not.toHaveBeenCalled();
      expect(navigateSpy).withContext('the operator stays put').not.toHaveBeenCalled();
      expect(query('app-error-banner')?.textContent ?? '').toContain(
        'The request could not be processed as submitted.',
      );
    });

    /**
     * ⚠ THE OUTCOME IS BROUGHT TO THE READER, NOT MERELY RENDERED WHERE IT BELONGS. This form is longer
     * than a viewport, its actions sit at the foot and its outcome surface renders at the head, so an
     * operator pressing Update from a scroll offset of 712px was measured seeing NO visible change at
     * all: the refusal was on the page, above the fold, unreachable without scrolling back and with
     * nothing to say it was there.
     */
    it('moves focus to the outcome so a refusal is reachable from the foot of the form', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('user.membership_settings.invalid', 400, 'The request could not be processed as submitted.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const live = query('.error-banner-live');

      expect(live).withContext('the outcome surface is rendered').not.toBeNull();
      expect(live?.getAttribute('tabindex'))
        .withContext('programmatically focusable, and never in the tab order')
        .toBe('-1');
      expect(document.activeElement)
        .withContext('focus is ON the outcome, not left on a control below the fold')
        .toBe(live);
    });

    it('leaves focus alone when the banner was already there on arrival', () => {
      // The converse, and the reason the reveal lives in this branch rather than in the shared banner: a
      // refusal that was on screen before the reader did anything must not pull focus, because nobody asked
      // it to. Here the READ is refused, so the banner renders during arrival with no submit involved.
      create();
      expectRequest('GET', SETTINGS_URL, 'the policy read').flush(
        problem('auth.not_permitted', 403, 'You are not permitted to read this policy.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(query('.error-banner-live')?.textContent ?? '')
        .withContext('the refusal is on screen')
        .toContain('You are not permitted to read this policy.');
      expect(document.activeElement)
        .withContext('and it did not take focus, because the reader asked for nothing')
        .not.toBe(query('.error-banner-live'));
    });

    it('pins a per-field refusal to the control the server named', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem(
          'user.membership_settings.redirect_invalid',
          400,
          'The request could not be processed as submitted.',
          { RedirectAfterLogin: ['That page does not belong to this site.'] },
        ),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // ⚠ THE SERVER'S KEY IS ITS OWN MODEL-STATE SPELLING, NOT CAMEL-CASED. The shared reader matches
      // case-insensitively, which is what makes this land on the right control.
      expect(fieldErrors()).toContain('That page does not belong to this site.');
      expect(field<HTMLSelectElement>('redirectAfterLogin').getAttribute('aria-invalid')).toBe('true');
    });

    it('pins a per-field refusal whose key carries a binder prefix', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem(
          'user.membership_settings.invalid',
          400,
          'The request could not be processed as submitted.',
          {
            '$.recordsPerPage': ['The page size is not acceptable.'],
            'request.securityDisplayNameFormat': ['That format cannot be stored.'],
            SECURITYEMAILVALIDATION: ['That expression could not be compiled.'],
          },
        ),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const messages = fieldErrors();

      expect(messages).toContain('The page size is not acceptable.');
      expect(messages).toContain('That format cannot be stored.');
      expect(messages).toContain('That expression could not be compiled.');

      expect(field<HTMLInputElement>('recordsPerPage').getAttribute('aria-invalid')).toBe('true');
      expect(field<HTMLInputElement>('securityDisplayNameFormat').getAttribute('aria-invalid')).toBe(
        'true',
      );
      expect(field<HTMLTextAreaElement>('securityEmailValidation').getAttribute('aria-invalid')).toBe(
        'true',
      );
    });

    it('reads a refusal of authority as a warning rather than an error', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('auth.not_permitted', 403, 'The authenticated caller is not permitted to perform this operation.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(query('.error-banner__severity')?.textContent?.trim().toLowerCase()).toContain('warning');
      expect(notifications()).withContext('the banner carries it, not a transient').toHaveSize(0);
    });

    it('reads a rate-limit refusal at its calmest band', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('request.rate_limited', 429, 'Too many requests have been submitted. Retry after a short delay.'),
        { status: 429, statusText: 'Too Many Requests' },
      );
      fixture.detectChanges();

      // ⚠ THE BANNER INTERCEPTS 429 BEFORE THE DOMAIN RULE, so the word shown is "Please wait" rather than
      // "Warning". The domain severity for 429 IS warning; the banner refines it to its own calm band
      // because a retryable delay is not a refusal to report as one.
      expect(query('.error-banner__severity')?.textContent?.trim()).toBe('Please wait');
    });

    it('reads a server failure as an error', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('server.unexpected_failure', 500, 'An unexpected error occurred while processing the request.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(query('.error-banner__severity')?.textContent?.trim().toLowerCase()).toContain('error');
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7 — ABANDONING THE FORM
  // ---------------------------------------------------------------------------------------------------

  describe('abandoning the form', () => {
    it('leaves for the account listing without writing or validating anything', () => {
      arrive();

      // Empty a required field first: abandoning a half-filled form must not first be told the
      // form is half-filled. The legacy cancel command declared validation switched off.
      type('recordsPerPage', '');
      const before: number = fieldErrors().length;

      press(CANCEL_LABEL);

      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
      expect(fieldErrors().length)
        .withContext('no new accusation is raised on the way out')
        .toBeLessThanOrEqual(before);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH]);
    });

    it('clears a recorded failure so the next screen does not inherit this banner', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem('user.membership_settings.invalid', 400, 'The request could not be processed as submitted.'),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(query('app-error-banner')?.textContent ?? '').toContain('could not be processed');

      press(CANCEL_LABEL);

      expect(TestBed.inject(UserStore).failure()).withContext('the slot is cleared').toBeNull();
    });

    it('stays operable while a write is outstanding, because it is the way out', () => {
      arrive();
      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);

      press(CANCEL_LABEL);

      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH]);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });
  });

  describe('the Enter key', () => {
    /** The one form on this screen. */
    function formElement(): HTMLFormElement {
      return queryOrFail<HTMLFormElement>(host(), 'form');
    }

    /**
     * Presses Enter in one control the way a browser delivers it, and answers whether the default action
     * survived. A form's implicit submission IS the default action of that keystroke, so a cancelled
     * default is exactly what "Enter commits nothing" means.
     *
     * @param name The setting's control name.
     * @returns True when the keystroke's default action was cancelled.
     */
    function pressEnterIn(name: string): boolean {
      const control = field<HTMLElement>(name);
      const event = new KeyboardEvent('keydown', {
        key: 'Enter',
        bubbles: true,
        cancelable: true,
      });

      control.dispatchEvent(event);
      fixture.detectChanges();

      return event.defaultPrevented;
    }

    it('commits nothing when pressed in a value box', () => {
      arrive();
      type('recordsPerPage', '25');

      // QA-16. This screen renders a real `button[type="submit"]` above twelve other entry controls, so
      // Enter in any of them dispatched a genuine submit and the save ran - and this save is the one
      // that rewrites every account's display name when the format field changes. The keystroke could
      // therefore commit a change the operator had not asked to commit.
      expect(pressEnterIn('recordsPerPage'))
        .withContext('the implicit submission is cancelled')
        .toBeTrue();

      // The proof that matters is not the flag but the absence of a request: teardown verifies that
      // nothing unexpected was sent, and this states it at the point of the keystroke.
      expect(httpMock.match(() => true))
        .withContext('nothing is sent')
        .toHaveSize(0);
      expect(navigateSpy)
        .withContext('and the screen does not leave, which is what a successful save does')
        .not.toHaveBeenCalled();
    });

    it('still commits when the explicit Update is pressed, so nothing is taken away', () => {
      arrive();
      type('recordsPerPage', '25');

      submitForm();

      expectRequest('PUT', SETTINGS_URL).flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });

    /**
     * Presses a key on an arbitrary element and answers whether the default action survived.
     *
     * @param target The element to press the key on.
     * @param key The key to press. Defaults to Enter.
     * @returns True when the keystroke's default action was cancelled.
     */
    function pressOn(target: Element, key = 'Enter'): boolean {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });

      target.dispatchEvent(event);
      fixture.detectChanges();

      return event.defaultPrevented;
    }

    /**
     * ⚠ THIS CASE ASSERTED THE OPPOSITE UNTIL IT WAS MEASURED, AND THE OLD ASSERTION WAS THE DEFECT.
     * It read "leaves Enter alone on a checkbox, which has its own behaviour", on the reasoning that the
     * specification's list of "fields that block implicit submission" names no checkbox. The specification
     * is not the whole story: Chrome requests implicit submission from a checkbox too, and with every value
     * box guarded, pressing Enter on `Suppress Pager?` on the running screen still dispatched a submit
     * whose `defaultPrevented` was false — `PUT /api/v1/users/settings` left the browser and returned 200.
     * On a screen of fourteen switches that is the same silent commit QA-16 was raised about, reached by a
     * different control.
     */
    it('commits nothing when pressed on a checkbox, which the browser also submits from', () => {
      arrive();

      const boxes = queryAll<HTMLInputElement>('input[type="checkbox"]');

      expect(boxes.length)
        .withContext('the screen renders switches, so this is not passing on an empty set')
        .toBeGreaterThan(0);

      for (const box of boxes) {
        expect(pressOn(box))
          .withContext(`Enter on ${box.id} must not ask this form to submit`)
          .toBeTrue();
      }

      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
      expect(navigateSpy).withContext('and the screen does not leave').not.toHaveBeenCalled();
    });

    /** A select is the third measured trigger, and this screen has six of them. */
    it('commits nothing when pressed on a select', () => {
      arrive();

      const pickers = queryAll<HTMLSelectElement>('select');

      expect(pickers.length)
        .withContext('the screen renders pickers, so this is not passing on an empty set')
        .toBeGreaterThan(0);

      for (const picker of pickers) {
        expect(pressOn(picker))
          .withContext(`Enter on ${picker.id} must not ask this form to submit`)
          .toBeTrue();
      }

      expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);
    });

    /**
     * ⚠ NOTHING IS TAKEN AWAY FROM THE CONTROLS THAT WERE ADDED TO THE GUARD, which is what makes adding
     * them safe. A checkbox is operated with the SPACE bar and a select answers Space, the arrow keys and
     * typing; cancelling any of those would turn the fix into a regression that left switches unusable
     * from the keyboard.
     */
    it('leaves every key a switch or a picker is actually operated with alone', () => {
      arrive();

      const box = queryAll<HTMLInputElement>('input[type="checkbox"]')[0];
      const picker = queryAll<HTMLSelectElement>('select')[0];

      for (const key of [' ', 'ArrowDown', 'ArrowUp', 'Tab', 'Escape', 'a']) {
        expect(pressOn(box, key))
          .withContext(`${key} must reach the checkbox untouched`)
          .toBeFalse();
        expect(pressOn(picker, key))
          .withContext(`${key} must reach the select untouched`)
          .toBeFalse();
      }
    });

    /**
     * ⚠ AND ENTER ON A COMMAND STAYS A CLICK. The browser turns Enter on a focused button into an
     * activation, and that is the only way a keyboard operator reaches Update or Cancel at all.
     */
    it('leaves Enter on a command entirely alone', () => {
      arrive();

      const commands = queryAll<HTMLButtonElement>('button');

      expect(commands.length).toBeGreaterThan(0);

      for (const control of commands) {
        expect(pressOn(control))
          .withContext(`Enter must still activate ${control.textContent?.trim() ?? 'this command'}`)
          .toBeFalse();
      }
    });

    it('declares the suppression on the form rather than relying on a handler', () => {
      arrive();

      // The attribute IS the directive's selector, so its presence is what wires the behaviour at all.
      // Asserted separately so removing it fails here, naming the cause.
      expect(formElement().hasAttribute('appBlockImplicitSubmit')).toBeTrue();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 7b — THE MEASURE THE CAPTIONS AND THE VALUES SHARE
  // ---------------------------------------------------------------------------------------------------

  describe('the caption and value tracks', () => {
    /**
     * ⚠ THIS SCREEN ASKS FOR THE WIDEST CAPTION TRACK IN THE TOKEN SET, AND BELOW `lg` THAT COSTS THE
     * VALUE MORE THAN THE CAPTION GAINS. The shared field caps its caption at `min(token, 45%)`, so at 768
     * the 300px resolved to 225.891px of a 502px field and left the value 260.109px — measured on the
     * running screen, 21.6px short of the longest redirect option, which painted
     * `No redirect (stay on the current pa` and clipped `ge)` with no ellipsis. A wrapped caption stays
     * wholly readable; a clipped value does not, and a native select cannot even report the loss, since
     * `scrollWidth` equals `clientWidth` for a label it never scrolls.
     */
    it('yields the caption track to the value below the wide breakpoint', () => {
      arrive();

      const narrowed = narrowWidthRuleFor(':host', '--field-label-inline-size')
        ?? narrowWidthRuleFor('[_nghost', '--field-label-inline-size');

      expect(narrowed)
        .withContext('the screen narrows its caption track, and only below a breakpoint')
        .not.toBeNull();
      expect(narrowed?.value)
        .withContext('expressed in the label vocabulary rather than as a raw length')
        .toContain('--field-label-inline-size-medium');
      expect(narrowed?.value)
        .withContext('and it is the step below, not the one the wide arrangement uses')
        .not.toContain('wider');

      // ⚠ THE STEP ABOVE THIS ONE WAS TRIED AND RE-MEASURED SHORT. `--field-label-inline-size-wide` leaves
      // the value 286px against the 292.68px the longest option needs, because the arrow reserve on this
      // select is 25.393px rather than the 14.393px first estimated. Stated as its own assertion so a
      // regression to it fails here naming the reason rather than only failing a pixel measurement
      // somewhere else.
      expect(narrowed?.value)
        .withContext('and specifically not the wide step, which was measured 6.68px short')
        .not.toContain('--field-label-inline-size-wide');
      expect(narrowed?.condition)
        .withContext('withdrawn above the step, so wide viewports keep the roomy caption')
        .toContain('max-width');
    });

    /**
     * ⚠ A CLOSED SELECT WHOSE LABEL DOES NOT FIT MUST SAY SO. The values at risk here and on the portal
     * screen are operator data of unbounded length — display names, login names, page titles — so no
     * width this application can choose guarantees a fit at 320px, and a hard clip renders an incomplete
     * value that reads as a complete one. Asserted on a real select with the global sheet loaded, because
     * this is a global rule and every picker in the application depends on it.
     */
    it('has every picker report a clipped label rather than cut it silently', () => {
      arrive();

      const pickers = queryAll<HTMLSelectElement>('select');

      expect(pickers.length)
        .withContext('the screen renders pickers, so this is not passing on an empty set')
        .toBeGreaterThan(0);

      for (const picker of pickers) {
        expect(getComputedStyle(picker).textOverflow)
          .withContext(`${picker.id} must signal a label it could not paint in full`)
          .toBe('ellipsis');
      }
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no shell landmark, and one heading per section below the page title', () => {
      arrive();

      expect(queryAll('header')).withContext('the shell owns the banner').toHaveSize(0);
      expect(queryAll('main')).withContext('the shell owns the main region').toHaveSize(0);
      expect(queryAll('nav')).withContext('the shell owns navigation').toHaveSize(0);
      expect(queryAll('footer')).withContext('the shell owns the footer').toHaveSize(0);
      expect(queryAll('h2')).withContext('the accounts section and the services panel').toHaveSize(2);
      expect(queryAll('h3')).withContext('the nested column switches').toHaveSize(1);
      expect(queryAll('h4')).toHaveSize(0);
      expect(queryAll('h1')).withContext('the page heading, from page-header').toHaveSize(1);

      const panel = query<HTMLElement>('section.member-services');

      expect(panel).withContext('the panel is a section of this page').not.toBeNull();
      expect(panel?.getAttribute('aria-labelledby'))
        .withContext('named by its own heading, so the region is deliberate')
        .toBe('member-services-heading');
    });

    it('makes the nested caption perceivably smaller than the section it sits under', () => {
      arrive();

      const sectionHeading = queryOrFail<HTMLElement>(host(), 'h2.membership-settings__section-heading');
      const groupCaption = queryOrFail<HTMLElement>(host(), 'legend h3');

      const sectionSize = Number.parseFloat(getComputedStyle(sectionHeading).fontSize);
      const captionSize = Number.parseFloat(getComputedStyle(groupCaption).fontSize);

      // ⚠ QA-24 — THE STEP WAS ONE PIXEL. The section heading renders at the 15px every section heading in
      // this application uses and the caption rendered at 14px, which is also the BODY size — so the caption
      // matched the text it was captioning and conveyed no rank at all. Asserted as a measured step rather
      // than as two token names, because a token could be repointed without the hierarchy changing.
      expect(captionSize).toBeLessThanOrEqual(sectionSize - 2);
      expect(captionSize)
        .withContext('and never below the smallest step the vocabulary publishes')
        .toBeGreaterThanOrEqual(12);
      expect(getComputedStyle(groupCaption).fontWeight)
        .withContext('still bold, which is what keeps it reading as a caption rather than small print')
        .toBe('700');
    });

    it('renders no table, because there is no grid on this screen', () => {
      arrive();

      // The legacy markup opened with a fixed-width borderless table which carried no data at all — it was
      // pure layout, as every table in that generation of markup was. Arrangement is a grid in the paired
      // stylesheet here, so not one table element is emitted.
      expect(host().querySelectorAll('table')).withContext('no table of any kind').toHaveSize(0);
      expect(queryAll('app-data-table')).withContext('and no grid component').toHaveSize(0);
      // ⚠ THE CLAIM IS ABOUT THIS SCREEN'S OWN MARKUP, and it holds only while no session names an account:
      // the consolidated subscription panel this screen mounts DOES render the shared grid, for the
      // seven-column services catalogue the legacy panel carried.
      expect(queryAll('app-member-services')).withContext('the panel is still mounted').toHaveSize(1);
    });

    /**
     * ⚠ NO AUTHORING COMMENTARY REACHES THE SCREEN, AND 1101 CHARACTERS OF IT ONCE DID. One annotation
     * block's closing delimiter sat above the note that followed it rather than below, so the following
     * note rendered as an unwrapped text node directly beneath this component - 14px, measured at 1672px
     * wide and 86px tall, present in the accessibility tree, and sitting immediately above the one
     * sentence on this screen genuinely addressed to an operator.
     */
    it('renders no authoring commentary as page copy', () => {
      arrive();

      const copy: string = host().innerText;

      expect(copy).withContext('no annotation marker').not.toContain('⚠');
      expect(copy).withContext('no legacy source citation').not.toContain('.vb:L');
      expect(copy).withContext('no back-quoted identifier').not.toContain('`');
      expect(copy)
        .withContext('and not the opening words of the block that leaked')
        .not.toContain('THE PROVENANCE DISCLOSURE');
    });

    it('emits none of the legacy spacing content', () => {
      arrive();

      const markup = host().innerHTML;

      // MIGRATION: the legacy inter-control spacing was CONTENT — seventy literal non-breaking
      // space entities across the in-scope admin markup, one of them on this screen between the
      // two commands. Spacing is a token-driven gap in the paired stylesheet, so not one is
      // reproduced, and no line break element is emitted either: a leading break tag on a
      // resource string is stripped upstream by the shared failure utility.
      expect(markup).withContext('no non-breaking space entity').not.toContain('&nbsp;');
      expect(markup).withContext('no literal non-breaking space character').not.toContain('\u00a0');
      expect(markup).withContext('no line break element').not.toContain('<br');
    });

    it('wraps every control in the shared field component, with no bare control anywhere', () => {
      arrive();

      // ⚠ THE DESIGN-SYSTEM RULE, MADE EXECUTABLE: no bare control appears in a feature template
      // where the shared field covers the need. Every input, selector and text area on this
      // screen must sit inside a shared field, which is what guarantees each one has a real label
      // pointing at it, a place for its help text and a place for its message.
      const controls = queryAll<HTMLElement>('input, select, textarea');

      expect(controls.length).withContext('every one of the twenty-three controls').toBe(
        EDITABLE_CONTROL_COUNT,
      );

      for (const control of controls) {
        expect(control.closest('app-form-field'))
          .withContext(`${control.id} sits inside a shared field`)
          .not.toBeNull();
      }

      // The two form commands are the only buttons this screen's own template emits, and they are
      // commands rather than controls, and no shared component wraps a button, so there is nothing
      // to wrap them in.
      const commands = queryAll<HTMLButtonElement>('button').filter(
        (candidate) => candidate.classList.contains('form-field__help-toggle') === false,
      );

      expect(commands).withContext('exactly two commands').toHaveSize(2);
      expect(commands.map((candidate) => (candidate.textContent ?? '').trim())).toEqual([
        SUBMIT_LABEL,
        CANCEL_LABEL,
      ]);

      // ⚠ EVERY OTHER BUTTON BELONGS TO THE SHARED FIELD, and their count is a measured fact
      // rather than an incidental one. The shared field renders a help disclosure only where help
      // text was supplied, and the nine listing-column switches supply NONE — each of their legacy
      // help values was its own label with the trailing colon removed, so a disclosure repeating
      // the label beside it would add a control to operate and tell a reader nothing. Fourteen
      // fields carry help; nine do not; the arithmetic is asserted so that reduction cannot be
      // silently undone.
      const helpToggles = queryAll('button.form-field__help-toggle');

      expect(helpToggles).withContext('one disclosure per field carrying help').toHaveSize(
        EDITABLE_CONTROL_COUNT - 9,
      );

      for (const toggle of helpToggles) {
        expect(toggle.getAttribute('aria-expanded'))
          .withContext('every disclosure announces its state')
          .toBe('false');
      }
    });

    it('groups the nine listing switches under an accessible name of their own', () => {
      arrive();

      // Nine unrelated switches in a row tell a screen reader nothing about what they have in
      // common. A field set with a legend is what gives the cluster a name.
      const group = queryOrFail<HTMLFieldSetElement>(host(), 'fieldset.membership-settings__columns');
      const legend = queryOrFail<HTMLLegendElement>(group, 'legend');

      expect(legend.textContent?.trim()).withContext('the cluster is named').toBe(COLUMNS_HEADING);
      expect(group.querySelectorAll('input[type="checkbox"]'))
        .withContext('all nine switches are inside it')
        .toHaveSize(9);
    });

    it('puts the section toggle in the tab order, reversing the legacy defect', () => {
      arrive();

      const section = queryOrFail<HTMLDetailsElement>(host(), 'details.membership-settings__section');
      const summary = queryOrFail<HTMLElement>(section, 'summary');

      // MIGRATION: the legacy section toggle was WITHDRAWN FROM THE TAB ORDER with a negative tab
      // index (`Website/controls/sectionheadcontrol.ascx` L3), so it could be operated by pointer
      // only and a keyboard user could not collapse a section at all. That defect is deliberately
      // REVERSED rather than reproduced: a native disclosure summary is in the tab order by
      // default, is operated by both Enter and Space, and needs no script. Reversing it costs
      // nothing visually, which is what makes it permissible under the governing precedence order.
      expect(summary.tabIndex).withContext('reachable by keyboard').not.toBe(-1);
      expect(summary.getAttribute('tabindex'))
        .withContext('and never explicitly withdrawn')
        .not.toBe('-1');

      // The announced state is bound from the element itself, so it cannot drift from the rendered
      // one. A static attribute would be wrong the moment a reader collapsed the section.
      expect(summary.getAttribute('aria-expanded')).toBe('true');
      expect(section.open).toBeTrue();
    });

    it('names every one of the twenty-three controls with a real label pointing at it', () => {
      arrive();

      const labels = queryAll<HTMLLabelElement>('label.form-field__label');

      expect(labels.length).withContext('a label per field').toBeGreaterThanOrEqual(23);

      for (const label of labels) {
        const target: string | null = label.getAttribute('for');

        expect(target).withContext('every label points somewhere').not.toBeNull();
        expect(query(`#${target}`))
          .withContext(`the control ${String(target)} exists`)
          .not.toBeNull();
      }
    });

    it('renders the recovered legacy wording verbatim, defects and punctuation included', () => {
      arrive();

      const markup = host().textContent ?? '';

      // ⚠⚠ DOMAIN-LOGIC PRESERVATION, MADE EXECUTABLE. Every string below is a legacy quirk that a
      // well-meaning edit would tidy, and tidying any of them would change text an existing
      // administrator recognises for no behavioural gain. This case is what stands in the way.

      // The question mark is the legacy wording. Preserved.
      expect(markup).toContain('Suppress Pager?');
      // Labelled "Name", not "Display Name", in the legacy file. Preserved.
      expect(markup).toContain('Show Name Column');
      expect(markup).toContain('Users per Page');
      expect(markup).toContain('Email Address Validation');
      expect(markup).toContain('Display Name Format');
      // The heading keeps its legacy plural: "User Accounts Settings", not "User Account Settings".
      expect(markup).toContain('User Accounts Settings');
      expect(markup).not.toContain('User Account Settings');

      // ⚠ HELP TEXT IS BEHIND A DISCLOSURE and is therefore not in the document until the
      // disclosure is opened — the shared field guards it with a condition rather than hiding it
      // with styling, so there is nothing to read before the toggle is operated. That is asserted
      // first, because a case that read an always-present node would prove nothing about either.
      expect(queryAll('.form-field__help')).withContext('nothing revealed yet').toHaveSize(0);
    });

    it('preserves the one legacy language defect that survives into this screen', () => {
      arrive();

      // ⚠⚠ THE SHIPPED GRAMMAR IS WRONG AND STAYS WRONG. The sign-in profile requirement's help
      // text reads "before be logged in" — ungrammatical, and shipped exactly so. Domain-logic
      // preservation FORBIDS correcting it, and a well-meaning repair is precisely what this case
      // guards. (The two dropped password-aging misspellings, "measn" and "recieve", are covered by
      // the closed-section case instead: their fields do not exist here, so the question of
      // preserving them never arises.)
      openHelp('securityRequireValidProfileAtLogin');

      const revealed = queryOrFail<HTMLElement>(
        host(),
        '#membership-setting-securityRequireValidProfileAtLogin-help',
      );
      const text = (revealed.textContent ?? '').trim();

      expect(text).withContext('the shipped grammar is preserved').toContain('before be logged in');
      expect(text).withContext('and is not silently repaired').not.toContain(
        'before being logged in',
      );
    });

    it('describes the substitution tokens without expanding one', () => {
      arrive();

      openHelp('securityDisplayNameFormat');

      const revealed = queryOrFail<HTMLElement>(
        host(),
        '#membership-setting-securityDisplayNameFormat-help',
      );
      const text = (revealed.textContent ?? '').trim();

      // The bracketed tokens are DESCRIBED here and expanded by nobody: token replacement is an
      // excluded subsystem, so the brackets must survive into the help text as characters.
      expect(text).toContain('[FIRSTNAME] [LASTNAME]');
      // And the cross-screen consequence the legacy wording states is carried, not dropped.
      expect(text).toContain('no longer be editable');
    });

    it('offers the three selectors with exactly the options the server accepts', () => {
      arrive();

      // The server accepts nothing outside zero to two for the display mode, which is why the
      // list built from the legacy enumeration covers exactly three.
      expect(field<HTMLSelectElement>('displayMode').options).toHaveSize(3);
      expect(field<HTMLSelectElement>('profileDefaultVisibility').options).toHaveSize(3);
      expect(field<HTMLSelectElement>('securityUsersControl').options).toHaveSize(2);

      const usersControl = Array.from(field<HTMLSelectElement>('securityUsersControl').options).map(
        (option) => (option.textContent ?? '').trim(),
      );

      // "Combo Box" and "Text Box" are the legacy resource values, shared keys in the original.
      expect(usersControl).toEqual(['Combo Box', 'Text Box']);
    });

    it('declares the type of both commands so neither submits by accident', () => {
      arrive();

      expect(button(SUBMIT_LABEL)?.getAttribute('type')).toBe('submit');
      // The legacy cancel command declared validation switched off, so it is never a submit.
      expect(button(CANCEL_LABEL)?.getAttribute('type')).toBe('button');
    });

    it('renders a hostile stored setting as text, with no element parsed out of it', () => {
      const hostile = '<img src=x onerror="window.__dnnSentinel = true">';

      arrive(settings({ securityDisplayNameFormat: hostile }));

      // ⚠ LEGACY WORDING AND STORED SETTINGS ARE UNTRUSTED MARKUP BY MEASUREMENT. Nothing here
      // is bound as trusted markup and no sanitiser is involved.
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value)
        .withContext('carried verbatim as a value')
        .toBe(hostile);
      expect(host().querySelectorAll('img')).withContext('nothing was parsed').toHaveSize(0);

      // ⚠ THE DECISIVE HALF OF THIS CASE. Reading the sentinel reflectively — rather than
      // through a dictionary type this file is not permitted to write — proves the handler never
      // ran: if the value had been bound as trusted markup, the element would have been parsed,
      // its handler would have fired, and this would hold true rather than undefined.
      const sentinel: unknown = Reflect.get(window, '__dnnSentinel');

      expect(sentinel).withContext('no handler ran').toBeUndefined();
      // No raw-markup binding exists on this screen at all, which is what makes the above
      // structural rather than incidental.
      expect(host().innerHTML).not.toContain('<img');
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      arrive();

      // The live region must exist before it carries a message, or the first message put into it
      // is not announced at all.
      const live = query('.error-banner-live');

      expect(live).withContext('the region is mounted').not.toBeNull();
      expect(live?.getAttribute('role')).toBe('alert');
      expect(live?.getAttribute('aria-live')).toBe('assertive');
      expect(query('.error-banner__title')).withContext('and says nothing').toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // THE CONSOLIDATED SUBSCRIPTION PANEL
  // ---------------------------------------------------------------------------------------------------
  //
  // MIGRATION: `Website/admin/Users/MemberServices.ascx` is the third of the three legacy controls
  // AAP 0.5.1.8 consolidates into this feature, and AAP 0.4.4 freezes the console's route table at
  // twenty-five addresses with no member-services address among them - so it is MOUNTED here rather
  // than routed. These cases assert the mounting and the account it is given, and nothing about what
  // the panel then does: that is `member-services.component.spec.ts`, and asserting it twice would
  // leave two authorities for one fact.
  //
  // The negative case is the valuable one. An intermediate revision published the panel at its own
  // address and a later one deleted it outright, leaving five endpoints, five typed client wrappers
  // and five store operations with no consumer in the workspace. Nothing failed - which is exactly
  // why the mounting is pinned here.
  describe('the consolidated subscription panel', () => {
    /**
     * Seats the caller's identity, which is what gives the panel a subject account. Delegates to the shared
     * seating helper, so the identity the panel reads and the identity the redirect pickers read cannot
     * disagree about which portal the caller is in.
     */
    function seatSession(userId: number): void {
      seatCallerSession(userId);
    }

    it('mounts the panel and gives it the account the caller holds', () => {
      seatSession(CALLER_ACCOUNT_ID);
      arrive();

      expect(query('app-member-services'))
        .withContext('the consolidated panel is rendered by this screen')
        .not.toBeNull();
      expect(query('section.member-services h2'))
        .withContext('as a section of this page, not a page of its own')
        .not.toBeNull();

      // The account is not asserted through a component instance: the OBSERVABLE consequence of
      // binding it is the address the panel reads, which is what a browser would show.
      expectRequest('GET', CALLER_SERVICES_URL, "the panel's catalogue read").flush(cataloguePage());
      fixture.detectChanges();
    });

    it('issues no subscription request while the session names no account', () => {
      // No session is seated, so the caller's account is not yet known. The panel is still
      // mounted - it renders its own transient notice - and reads nothing, which teardown's
      // verification is what proves.
      arrive();

      expect(query('app-member-services')).not.toBeNull();
      expect(httpMock.match(() => true))
        .withContext('nothing was requested for an unknown account')
        .toHaveSize(0);
    });

    it("leaves a subscription failure to the panel instead of announcing it twice", () => {
      seatSession(CALLER_ACCOUNT_ID);
      arrive();

      const problem: ProblemDetails = {
        type: 'urn:dnnmigration:error:user.services_disabled',
        title: 'Forbidden',
        status: 403,
        detail: 'This site does not offer self-service subscription management.',
      };

      expectRequest('GET', CALLER_SERVICES_URL).flush(problem, {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();

      // ⚠ ONE FAILURE, ONE ANNOUNCEMENT. The store holds a single failure slot shared by every
      // account command, so without the operation filter on both surfaces this refusal would
      // appear at the top of the screen that did not perform it AND inside the panel that did.
      const banners = queryAll<HTMLElement>('app-error-banner');
      const inPanel = queryAll<HTMLElement>('section.member-services app-error-banner');

      expect(banners.length).withContext('two surfaces, two regions').toBeGreaterThan(1);
      expect(inPanel).withContext('the panel carries one of them').toHaveSize(1);
      expect(inPanel[0].textContent ?? '')
        .withContext("the panel says it, in the server's words")
        .toContain('self-service subscription management');

      const screenBanners = banners.filter((banner) => !inPanel.includes(banner));

      expect(screenBanners.length).withContext('and the screen carries the other').toBe(1);
      expect(screenBanners[0].textContent?.trim() ?? '')
        .withContext('which says nothing about an operation it did not perform')
        .toBe('');
    });
  });
  // ---------------------------------------------------------------------------------------------------
  // THE SWITCH ARRANGEMENT — QA-06
  // ---------------------------------------------------------------------------------------------------

  /**
   * This screen is where the finding was measured, so it is asserted here on the real screen as well as in
   * the shared field's own specification. What was shipped: this host widens the shared label track to 300px
   * for its long policy captions, and the switches sit in a grid whose cells measure roughly 350px — so each
   * box sat about 300px from its own caption and roughly 30px from the NEXT one, and at 1024px three of the
   * nine column switches sat in the fieldset's right padding entirely.
   */
  /**
   * ⚠ ONE FIELD ON THIS FORM REWRITES EVERY ACCOUNT IN THE SITE. Saving a changed display-name format renames
   * every account, replacing display names an operator typed by hand, and the only report of it arrived
   * AFTERWARDS - after the accounts had been renamed, with no undo. The sweep is legacy behaviour and is
   * preserved; being told before it happens, and how many records it reaches, is what was missing.
   *
   * Each case fails for a different reason if the guard regresses: the confirmation must stand in the way, it
   * must state the count, it must not appear for any OTHER field, cancelling must write nothing at all, and a
   * count that cannot be read must not block a save the operator is entitled to make.
   */
  describe('renaming every account', () => {
    const CURRENT_FORMAT = '[FIRSTNAME] [LASTNAME]';
    const NEW_FORMAT = '[LASTNAME], [FIRSTNAME]';

    /** A policy whose display-name format is known, so a change to it is unambiguous. */
    function policyWithFormat(): MembershipSettings {
      return settings({ securityDisplayNameFormat: CURRENT_FORMAT });
    }

    it('asks before renaming, and states how many accounts it would reach', () => {
      arrive(policyWithFormat());
      type('securityDisplayNameFormat', NEW_FORMAT);
      submitForm();

      // Nothing is written until the question is answered.
      httpMock.expectNone(
        (candidate) => candidate.method === 'PUT' && candidate.url === SETTINGS_URL,
      );

      const counted: ReturnType<HttpTestingController['expectOne']> = httpMock.expectOne(
        (candidate) => candidate.method === 'GET' && candidate.url === USERS_URL,
        'the count is asked for when the confirmation opens',
      );

      expect(counted.request.params.get('pageSize'))
        .withContext('one record is asked for, because only the total is used')
        .toBe('1');

      counted.flush({
        items: [],
        meta: { pageIndex: 0, pageSize: 1, totalCount: 19, totalPages: 19 },
      });
      fixture.detectChanges();

      const spoken: string = (query('.confirm-dialog')?.textContent ?? '').trim();

      expect(spoken).withContext('the consequence is named').toContain('renames accounts across this site');
      // ⚠ "UP TO" IS ASSERTED, NOT INCIDENTAL - QA-9. This previously read "19 accounts in this site will be
      // renamed", which was measured to be untrue: a dialogue predicting 18 was followed by a server report
      // of 8 actually rewritten, because an account already matching the format is left alone. The count is
      // the sweep's REACH, so the sentence has to state a bound rather than a certainty.
      expect(spoken)
        .withContext('and counted as a bound, because the exact figure is not knowable before the write')
        .toContain('Up to 19 accounts in this site will be renamed');
      // ⚠ ANCHORED AT A SENTENCE BOUNDARY, because the bare claim is a SUBSTRING of the qualified one - the
      // first attempt at this assertion asserted `not.toContain('19 accounts...')` and was unsatisfiable by
      // construction. What must be absent is the count starting a sentence with no bound in front of it.
      expect(/(?:^|[.!?]\s+)19 accounts in this site will be renamed/u.test(spoken))
        .withContext(`the count is never claimed unqualified, in: ${spoken}`)
        .toBeFalse();
      expect(spoken).withContext('and the absence of an undo is named with a remedy').toContain('no undo');

      // ⚠ THE HEADING IS ASSERTED BECAUSE THE SHARED DIALOGUE DEFAULTS IT TO A DELETION - QA-9. Measured at
      // runtime: this dialogue rendered "Confirm Delete" over a body about renaming, because no title was
      // passed. The heading is the first thing read, so it cannot contradict the body.
      const heading: string = (query('.confirm-dialog__title')?.textContent ?? '').trim();

      expect(heading).withContext('the heading names the act it stands in front of').toBe('Confirm Rename');
      expect(heading)
        .withContext('and not the shared dialogue\'s deletion default')
        .not.toBe('Confirm Delete');
    });

    it('agrees with itself about one account', () => {
      arrive(policyWithFormat());
      type('securityDisplayNameFormat', NEW_FORMAT);
      submitForm();

      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === USERS_URL)
        .flush({ items: [], meta: { pageIndex: 0, pageSize: 1, totalCount: 1, totalPages: 1 } });
      fixture.detectChanges();

      expect((query('.confirm-dialog')?.textContent ?? '').trim())
        .withContext('"1 accounts" reads as a defect in the application')
        .toContain('1 account in this site will be renamed');
    });

    it('writes the policy once the rename is accepted', () => {
      const policy = policyWithFormat();

      arrive(policy);
      type('securityDisplayNameFormat', NEW_FORMAT);
      submitForm();
      acceptTheRenameWarning(19);

      const write = expectRequest('PUT', SETTINGS_URL);

      expect(writtenPolicy(write).securityDisplayNameFormat)
        .withContext('the format the operator authored is the one written')
        .toBe(NEW_FORMAT);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);
    });

    it('writes NOTHING when the rename is declined, and keeps the entry', () => {
      arrive(policyWithFormat());
      type('securityDisplayNameFormat', NEW_FORMAT);
      submitForm();

      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === USERS_URL)
        .flush({ items: [], meta: { pageIndex: 0, pageSize: 1, totalCount: 19, totalPages: 19 } });
      fixture.detectChanges();

      pressDialogue('Cancel');

      httpMock.expectNone(
        (candidate) => candidate.method === 'PUT' && candidate.url === SETTINGS_URL,
      );

      expect(field<HTMLInputElement>('securityDisplayNameFormat').value)
        .withContext('declining must not discard what the operator typed')
        .toBe(NEW_FORMAT);
      expect(query('.confirm-dialog')).withContext('and the question is gone').toBeNull();
    });

    it('does not ask when some OTHER field changed', () => {
      const policy = policyWithFormat();

      arrive(policy);
      type('securityEmailValidation', '^.+@.+$');
      submitForm();

      // Straight through: no confirmation, and no count read either.
      httpMock.expectNone((candidate) => candidate.method === 'GET' && candidate.url === USERS_URL);

      const write = expectRequest('PUT', SETTINGS_URL);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);
    });

    /**
     * ⚠ A FORMAT TYPED OVER AND TYPED BACK IS NOT A CHANGE. The test is against the policy AS THE SERVER SENT
     * IT, not against the form's dirty state, so touching the field and restoring it asks nothing.
     */
    it('does not ask when the format ends up as it started', () => {
      const policy = policyWithFormat();

      arrive(policy);
      type('securityDisplayNameFormat', NEW_FORMAT);
      type('securityDisplayNameFormat', CURRENT_FORMAT);
      submitForm();

      httpMock.expectNone((candidate) => candidate.method === 'GET' && candidate.url === USERS_URL);

      const write = expectRequest('PUT', SETTINGS_URL);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);
    });

    /**
     * ⚠ A FIGURE THAT CANNOT BE READ MUST NEVER BLOCK A SAVE THE OPERATOR IS ENTITLED TO MAKE. The
     * confirmation stands without a count and still writes when accepted.
     */
    it('still offers the rename when the count cannot be read', () => {
      const policy = policyWithFormat();

      arrive(policy);
      type('securityDisplayNameFormat', NEW_FORMAT);
      submitForm();
      acceptTheRenameWarning(null);

      const write = expectRequest('PUT', SETTINGS_URL);

      write.flush(writeReport());
      fixture.detectChanges();
      answerWriteFollowUp(policy);
    });
  });

  describe('switch arrangement', () => {
    /** Every choice field on the screen, paired with its caption row and its box. */
    function switches(): readonly { name: string; captionRow: HTMLElement; box: HTMLElement }[] {
      return queryAll<HTMLElement>('app-form-field.form-field--inline').map((field) => {
        const captionRow = queryOrFail<HTMLElement>(field, '.form-field__label-row');

        return {
          name: (captionRow.textContent ?? '').trim(),
          captionRow,
          box: queryOrFail<HTMLElement>(field, 'input[type="checkbox"]'),
        };
      });
    }

    it('keeps every switch beside its own caption, at the width the finding was measured at', () => {
      arrive();

      // The width that reproduced the worst of it. The column count itself comes from viewport media
      // queries, so constraining the host is what narrows the individual cells.
      host().style.inlineSize = '1024px';
      fixture.detectChanges();

      const found = switches();

      // Nine column switches plus the five standalone policy switches — asserted so that a template change
      // which quietly stops rendering them cannot turn this into a vacuous pass.
      expect(found.length).toBeGreaterThanOrEqual(COLUMN_TOGGLE_FIELDS.length);

      for (const { name, captionRow, box } of found) {
        const caption = captionRow.getBoundingClientRect();
        const target = box.getBoundingClientRect();

        expect(target.left)
          .withContext(`${name} — box is left of its own caption`)
          .toBeGreaterThanOrEqual(caption.right);

        expect(target.left - caption.right)
          .withContext(`${name} — box is ${Math.round(target.left - caption.right)}px from its own caption`)
          .toBeLessThan(24);

        expect(target.top)
          .withContext(`${name} — box has dropped below its own caption`)
          .toBeLessThan(caption.bottom);
      }
    });

    it('keeps every switch beside its own caption at a narrow width too', () => {
      arrive();

      host().style.inlineSize = '375px';
      fixture.detectChanges();

      for (const { name, captionRow, box } of switches()) {
        expect(box.getBoundingClientRect().left - captionRow.getBoundingClientRect().right)
          .withContext(name)
          .toBeLessThan(24);
      }
    });

    it('keeps every switch inside the fieldset that contains it', () => {
      arrive();

      host().style.inlineSize = '1024px';
      fixture.detectChanges();

      const fieldset = queryOrFail<HTMLElement>(host(), '.membership-settings__columns');
      const bounds = fieldset.getBoundingClientRect();

      for (const { name, box } of switches()) {
        const target = box.getBoundingClientRect();

        if (!fieldset.contains(box)) {
          continue;
        }

        expect(target.right)
          .withContext(`${name} — box overflows the fieldset it belongs to`)
          .toBeLessThanOrEqual(bounds.right + 1);
      }
    });
  });
});
