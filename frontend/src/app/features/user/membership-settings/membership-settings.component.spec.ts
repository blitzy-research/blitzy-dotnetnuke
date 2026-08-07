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
import type { MembershipSettings, UserListItem } from '../../../core/models/user.model';
import { NotificationService } from '../../../core/services/notification.service';
import { UserStore } from '../../../core/state/user.store';
import { MembershipSettingsComponent } from './membership-settings.component';

/**
 * Specification for the tenant's account-administration policy screen.
 *
 * PROVENANCE, stated plainly because it bears on how much these cases are worth: the legacy
 * tree contains NO automated tests of any kind — not for this screen, not for anything — so
 * there is no predecessor harness to port and nothing here is a translation of an existing
 * assertion. Every case below was authored from measured legacy behaviour: the markup and
 * code-behind of `Website/admin/Users/UserSettings.ascx`, the eighty-four-entry resource file
 * beside it, the default-filling routine at `Library/Components/Users/UserModuleBase.vb`, and
 * the sentinel table at `Library/Components/Shared/Null.vb`.
 *
 * This file is also a COMPILE GATE and not only a behaviour proof. `tsconfig.app.json` names
 * `src/main.ts` alone and type-checks by import graph, while `tsconfig.spec.json` includes
 * every specification by pattern. Until some other consumer imports this screen, this file is
 * the only route by which the component AND its template are type-checked at all, so a
 * trivial specification here would let real type errors ship unseen.
 *
 * VERIFIED TESTING API, recorded rather than guessed at: on the installed
 * `@angular/core@19.2.25`, `TestBed.flushEffects()` EXISTS (declared in
 * `@angular/core/testing/index.d.ts`) and `TestBed.tick()` does NOT — the only `tick` is the
 * `fakeAsync` helper. Neither is called below, and deliberately: the component's effects are
 * flushed by `fixture.detectChanges()`, every derived slice it renders is a `computed()` read,
 * and `HttpTestingController` is synchronous, so no timer and no manual effect flush is
 * involved anywhere in this file.
 *
 * This screen edits ONE record with twenty-three members and no identifier, which makes its
 * failure modes unusual enough to name up front. Every case below exists because getting one
 * of these wrong produces a screen that looks correct and quietly corrupts a live policy:
 *
 *   - ⚠ THE WHOLE POLICY IS SENT, ALWAYS. The write is a REPLACE, not a patch, so a member
 *     dropped for "looking empty" is not an omission the server tolerates — it is a value
 *     being asserted. This contract is dense with legitimate falsy values: `false` for each
 *     of the nine listing columns and the three profile switches, `0` for a display mode,
 *     `null` for each of the three landing pages. A body assembled by filtering would
 *     silently re-show a column an administrator had hidden.
 *   - ⚠ SUBMISSION IS REFUSED UNTIL THE POLICY HAS ARRIVED. The form is seated with the
 *     measured legacy defaults before the server answers, so submitting in that state would
 *     overwrite a live policy with values nobody chose. That is why `canSubmit` consults the
 *     read as well as the write.
 *   - ⚠ A SUCCESSFUL WRITE IS THREE REQUESTS, NOT ONE. The response carries no body, so the
 *     store re-reads the policy, and because the policy declares the size of a page it then
 *     re-reads the account listing too. A specification that answers only the write leaves
 *     two requests outstanding and `verify` reports them.
 *   - ⚠ ZERO IS A REAL PAGE IDENTIFIER. The page table's identity seeds at zero, which is
 *     why the server's floor is zero rather than one and why `null` — not `-1`, the legacy
 *     whole-codebase marker for "no integer" — is the only expression of "no redirect".
 *   - ⚠ TWO WHOLE LEGACY SECTIONS ARE DELIBERATELY ABSENT. "Membership Provider Settings"
 *     and "Password Aging Settings" have no member on this contract, so this screen must
 *     render no provider field and no expiry field at all. Their absence is asserted, not
 *     assumed: a later reader looking for them needs the specification to say they are gone
 *     on purpose.
 *
 * MIGRATION: the legacy screen (`Website/admin/Users/UserSettings.ascx` and its code-behind)
 * wrote one stored setting at a time for whichever editor reported itself dirty (L172) and
 * coerced every value to text on the way (L174). Both behaviours are gone; the cases below
 * assert the replacement rather than the original.
 *
 * ---------------------------------------------------------------------------------------------
 * WHAT THIS SPECIFICATION DELIBERATELY DOES NOT COVER
 * ---------------------------------------------------------------------------------------------
 *
 * MIGRATION — ⭐ THE MATERIAL CORRECTION TO THE TRANSFORMATION PLAN, restated here so the scope
 * of this file is auditable rather than merely asserted. The plan maps this screen to THREE
 * legacy controls and TWO OF THE THREE ARE WRONG:
 *
 *   * `Website/admin/Users/Membership.ascx` is twenty-eight lines and is a read-only PER-ACCOUNT
 *     panel, not a tenant policy screen. Its editor carries `editmode="View"` and its four
 *     command buttons — authorise, unauthorise, unlock and force a password change, every one of
 *     them declared `causesvalidation="False"` — act on ONE account. The decisive proof is
 *     `Website/admin/Users/manageusers.ascx` L59-L63, which hosts `dnn:user ctlUser` and
 *     `dnn:membership ctlMembership` SIDE BY SIDE inside a single account row: the membership
 *     panel is part of the account detail screen. Those nine per-account membership fields and
 *     four actions therefore belong to the ACCOUNT FORM, and NOT ONE CASE BELOW ASSERTS THEM.
 *   * `Website/admin/Users/MemberServices.ascx` is seventy-seven lines of role subscription — an
 *     invitation-code box, a subscribe command and a seven-column services grid carrying a trial
 *     command. Role subscription, invitation codes, trials and billing have NO ENDPOINT in this
 *     API at all, so there is nothing for a screen to call and NO CASE BELOW COVERS THEM.
 *
 * Parity is therefore measured against `UserSettings.ascx` ALONE, and the correction is reported
 * rather than absorbed silently.
 *
 * MIGRATION: the legacy save started a BACKGROUND THREAD that rewrote every account's display
 * name whenever the display-name format changed (`UserSettings.ascx.vb` L175-L182). Nothing here
 * attempts it and no case asserts it: it is a bulk write across a whole tenant, which belongs
 * behind an endpoint rather than in a browser that can be closed halfway through, and no endpoint
 * exists for it. Reported as a possible gap on the server side rather than quietly dropped.
 *
 * MIGRATION: the legacy handler cleared the settings CACHE itself (`UserSettings.ascx.vb` L189).
 * Cache lifetime is the server's business in the target, nothing is cached in the browser, and no
 * case below asserts a client-side cache — because there is none to assert.
 *
 * MIGRATION: the legacy screen hid TWELVE of these fields when it was reached through the host
 * menu rather than a tenant's own (`UserSettings.ascx.vb` L87-L100). That rule is NOT reproduced
 * and no case tests it: host-level administration is out of scope, this screen is reached only as
 * a tenant administrator, and in that context the legacy screen showed everything. The
 * "everything is shown" half IS asserted, by the case that seats all twenty-three controls.
 *
 * MIGRATION: the two legacy CAPTCHA toggles went with the excluded challenge control and have no
 * member on this contract. The compensating control is server-side and stronger — the sign-in
 * endpoints are governed by a rate-limiting policy that partitions by client address — and it is
 * NOT exercised here, because this screen makes no authentication call at all. Inventing a
 * rate-limit case for a screen that cannot provoke one would be theatre; the 429 case below
 * exists only because the shared banner must classify a refusal correctly whatever produced it.
 *
 * MIGRATION: the screen injects the account STORE rather than the transport service, and that
 * shapes this file directly. The store owns the page size the account listing also reads, so a
 * successful write is three requests rather than one and every write case below answers all
 * three. Nothing is substituted for the store, so the whole chain — screen, store, transport,
 * client — is exercised for real and the address assertions mean what they say.
 *
 * MIGRATION — DRIFT IN BOTH DIRECTIONS, reported rather than hidden. REVERSE DRIFT: the ten
 * membership-provider fields and the two password-aging fields existed on the legacy screen and
 * have no member on this contract, so no control can exist for them; their absence is asserted
 * below rather than assumed. FORWARD DRIFT: none. Every one of the twenty-three controls this
 * screen renders traces to a legacy resource entry, so no control was invented for a contract
 * member with no legacy label — which is why every label assertion below quotes recovered legacy
 * wording rather than authored wording, with the two exceptions the paired class marks as
 * authored (the subtitle and the success sentence, neither of which the legacy screen had).
 */
describe('MembershipSettingsComponent', () => {
  let fixture: ComponentFixture<MembershipSettingsComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let successSpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  // ---------------------------------------------------------------------------------------------------
  // ADDRESSES
  // ---------------------------------------------------------------------------------------------------
  //
  // Written out as relative literals rather than composed from the endpoint table, so that a
  // change to that table shows up here as a failure instead of being silently agreed with.
  //
  // ⚠ THE ADDRESS IS RELATIVE AND MUST STAY RELATIVE. The `test` target declares no file
  // replacements, so a specification compiles against the PRODUCTION configuration, whose base
  // address is the relative `/api/v1` — the proxy in front of the container maps `/api/` onto
  // the API service, so the browser reaches it through the origin that served the application.
  // An absolute host would resolve only inside the container network and would fail from a
  // browser, which is why no case below names one.
  //
  // ⚠ THE SPA ROUTE AND THE API ADDRESS ARE DIFFERENT STRINGS AND MUST NOT BE CONFLATED. This
  // screen is reached at `/settings/membership`; the policy lives at `/api/v1/users/settings`.
  // Asserting the exact address — `users/` segment included — is what stops a later refactor
  // from routing the request at the screen's own path and producing a 404 that no compiler can
  // see. `expectOne` on the exact string also catches a doubled `/api/v1/api/v1/...` prefix,
  // which is the other failure this literal exists to trap.
  //
  // MIGRATION: the transformation plan for THIS file names the address
  // `/api/v1/users/settings/membership`. THAT ADDRESS DOES NOT EXIST, and the plan's own
  // instruction — confirm the exact address from the endpoint table and the transport service —
  // is what settles it. `core/config/api-endpoints.ts` composes `users.membershipSettings()`
  // from the accounts segment and the settings segment and nothing else; `core/services/
  // user.service.ts` reads and writes through exactly that; and the API declares the pair as
  // `HttpGet("settings")` and `HttpPut("settings")` on the accounts controller. There is no
  // `membership` segment anywhere on the wire. The real address is asserted below and the
  // discrepancy is reported rather than absorbed.

  const SETTINGS_URL = '/api/v1/users/settings';
  const USERS_URL = '/api/v1/users';

  /**
   * The health probe, asserted NEVER to be called from a screen.
   *
   * It sits at the host root — outside the versioned API prefix — is anonymous, and exists for
   * the container health check and the compose dependency condition. No component may reach it,
   * and a screen that did would be probing infrastructure from a browser.
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
  const CANCEL_LABEL = 'Cancel';
  const LOADING_LABEL = 'Loading user settings…';
  const SAVING_LABEL = 'Saving user settings…';
  const SAVED_MESSAGE = 'User settings saved.';
  const REQUIRED_MESSAGE = 'This setting is required.';
  const PAGE_SIZE_RANGE_MESSAGE = 'The number of accounts per page must be between 1 and 100.';
  const NEGATIVE_PAGE_MESSAGE =
    'A page identifier may not be negative. Leave the field empty for no redirect.';
  const LENGTH_MESSAGE = 'This setting may not exceed 2000 characters.';

  /**
   * The bounds the server itself applies, mirrored in the browser.
   *
   * ⚠ THE CEILING IS IMPORTED, NOT WRITTEN. `MAX_PAGE_SIZE` is the workspace's single home for
   * it, and a literal here would be a second copy free to disagree with the rule the control
   * actually enforces. The floor and the text ceiling are declared locally because no shared
   * constant carries either — stated so the asymmetry reads as deliberate.
   */
  const MINIMUM_RECORDS_PER_PAGE = 1;
  const MAXIMUM_RECORDS_PER_PAGE = MAX_PAGE_SIZE;
  const MAXIMUM_SETTING_LENGTH = 2000;

  /**
   * The number of members the policy contract carries, asserted rather than assumed.
   *
   * Derived from a live fixture below rather than written as a digit, so a member added to or
   * removed from the contract cannot leave a stale count passing here.
   */
  const POLICY_MEMBER_COUNT = 23;

  /**
   * The reason phrase the API publishes as a problem `title`, keyed by status.
   *
   * ⚠ NOT FREE TEXT. Every refusal reaches the wire through one shared problem factory that
   * fills the title from this status-keyed vocabulary, so a fixture carrying a bespoke title
   * describes no response this server can produce.
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
   * A live problem document, complete in every member the API actually emits.
   *
   * ⚠ A LIVE DOCUMENT ALWAYS CARRIES `type` AND NEVER CARRIES `instance`, and it carries BOTH
   * a trace identifier and a correlation identifier. The shared reference reader prefers the
   * correlation identifier, so a fixture carrying only a trace identifier exercises a branch
   * no operator reaches.
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
   * A policy as the server sends it, with every member present.
   *
   * The defaults below are NOT the measured legacy ones: several are deliberately the opposite,
   * so that a case asserting the form was seated from the server cannot pass against a form
   * that merely kept its own seated defaults.
   */
  function settings(overrides: Partial<MembershipSettings> = {}): MembershipSettings {
    return {
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

  function envelope<T>(data: T): ApiResponse<T> {
    return { data, meta: null };
  }

  /**
   * A page of accounts.
   *
   * ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data` — a fixture spelling it
   * otherwise flushes successfully and unwraps to no rows at all.
   */
  function emptyPage(pageSize: number): PagedResponse<UserListItem> {
    return { items: [], meta: { totalCount: 0, pageIndex: 0, pageSize, totalPages: 0 } };
  }

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces
    // it. Reversing the two, or omitting the first, leaves no client for the testing backend to
    // override and every expectation times out.
    //
    // ⚠ NOTHING IS SUBSTITUTED FOR THE STORE, THE TRANSPORT SERVICE OR THE NOTIFICATION
    // CHANNEL, and that is the single most important decision in this file. All three are
    // declared `providedIn: 'root'`, and the test harness builds a fresh root injector for every
    // case, so each case already gets its own policy and its own failure slot without a
    // provider being listed here. Listing one would add nothing; SUBSTITUTING one would be
    // worse than nothing — it would make the address assertions vacuous, and the address is the
    // highest-value thing this file proves. The real chain therefore runs end to end,
    // screen → store → transport → client, and is driven entirely through the testing backend.
    //
    // The component is STANDALONE, so it goes in `imports`. There is no declarations array
    // anywhere in this file and no module of any kind.
    await TestBed.configureTestingModule({
      imports: [MembershipSettingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    const notifications = TestBed.inject(NotificationService);

    notifySpy = spyOn(notifications, 'notify').and.callThrough();
    successSpy = spyOn(notifications, 'success').and.callThrough();
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Creates the screen. The policy read is issued from `ngOnInit`, during this first pass. */
  function create(): void {
    fixture = TestBed.createComponent(MembershipSettingsComponent);
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
  function arrive(policy: MembershipSettings | null = settings()): void {
    create();
    expectRequest('GET', SETTINGS_URL, 'the policy read').flush(envelope(policy));
    fixture.detectChanges();
  }

  /**
   * The rendered host element.
   *
   * ⚠ TAKEN BY ASSIGNMENT, NEVER BY A CAST. `fixture.nativeElement` is loosely typed, and
   * assigning it to a declared `HTMLElement` narrows it without introducing a cast token — which
   * is what keeps this file free of the escape hatches it forbids itself.
   */
  function host(): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  /**
   * The one element matching a selector, or a thrown failure naming what was missing.
   *
   * ⚠ THIS HELPER IS WHY NO NON-NULL ASSERTION AND NO CAST APPEARS AFTER A QUERY ANYWHERE BELOW.
   * `querySelector` is honestly typed as possibly null; narrowing it by asserting it away would
   * turn a missing element into an opaque "cannot read property of null" several lines later,
   * whereas throwing here names the selector that was not found.
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
   * Chooses a selector option by its RENDERED LABEL.
   *
   * ⚠ THE OPTIONS BIND `[ngValue]`, NOT `[value]`, so the DOM value is the framework's own
   * option identifier — "1: 1" for the integer one — rather than the bare integer. That is why
   * every assertion on a selector's value below uses containment rather than equality, and why
   * this helper resolves the option by LABEL and copies whatever identifier it carries: a label
   * is what an operator sees, and it survives both a reordering of the list and a change in how
   * the framework spells its identifiers.
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

  /** The messages the shared field component is rendering, in document order. */
  function fieldErrors(): readonly string[] {
    return queryAll<Element>('.form-field__error').map((node) => (node.textContent ?? '').trim());
  }

  /**
   * Opens one field's help disclosure.
   *
   * ⚠ HELP TEXT IS NOT IN THE DOCUMENT UNTIL THE DISCLOSURE IS OPENED. The shared field guards it
   * with a condition rather than hiding it with styling, so a case that wants to read help wording
   * has to operate the toggle first — which is the better test anyway, because it exercises the
   * disclosure a reader actually uses. The toggle is found by the identifier the shared field
   * derives from the control's own, rather than by position among the fourteen.
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
   * The policy carried by a captured request, narrowed by ASSIGNMENT.
   *
   * ⚠ NO CAST IS INVOLVED. A captured request's body is loosely typed, so assigning it to a
   * declared contract member narrows it without a cast token — and the narrowing is what makes
   * every body assertion below check a NAMED member rather than an index lookup.
   */
  function writtenPolicy(request: ReturnType<HttpTestingController['expectOne']>): MembershipSettings {
    const body: MembershipSettings = request.request.body;

    return body;
  }

  /**
   * The member names a policy object carries, sorted.
   *
   * Exists so the twenty-three-member rule is checked against real key sets rather than against
   * a hand-kept list, and so no dictionary type is written down to do it.
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
   * Answers a successful write in full.
   *
   * ⚠ THREE REQUESTS, IN THIS ORDER. The write answers with no body, so the store re-reads the
   * policy, and because the policy declares the size of a page it then re-reads the listing.
   */
  function answerWriteFollowUp(policy: MembershipSettings): void {
    expectRequest('GET', SETTINGS_URL, 'the re-read policy').flush(envelope(policy));
    fixture.detectChanges();

    const listing = expectRequest('GET', USERS_URL, 'the re-read listing');

    expect(listing.request.params.get('pageIndex'))
      .withContext('the listing returns to the first page')
      .toBe('0');
    expect(listing.request.params.get('pageSize'))
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
      // `/settings/membership`; the policy is at `/api/v1/users/settings`. Matching the exact
      // string is what stops the two being conflated, and it is also what catches a doubled
      // version prefix — `/api/v1/api/v1/users/settings` would not match and this would fail.
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

      // ⚠ THE HEALTH PROBE IS INFRASTRUCTURE, NOT AN API RESOURCE. It sits at the host root,
      // outside the versioned prefix, and is anonymous precisely so the container health check
      // and the compose dependency condition can reach it before anybody has signed in. A screen
      // calling it would be probing the deployment from a browser.
      expect(httpMock.match(HEALTH_URL)).withContext('no screen calls the health probe').toHaveSize(0);
      expect(httpMock.match((candidate) => candidate.url.startsWith('/api/v1') === false))
        .withContext('every request this screen makes is a versioned API call')
        .toHaveSize(0);
    });

    it('announces the wait and withholds the form until the policy has arrived', () => {
      create();

      const read = expectRequest('GET', SETTINGS_URL);

      // ⚠ THE FORM IS WITHHELD RATHER THAN DISABLED. Until the server's policy has been
      // applied the controls hold seated defaults, and showing them would invite an operator
      // to submit values nobody chose.
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

      // ⚠ ZERO IS A REAL PAGE. It must be seated as the digit zero, never as an empty field.
      expect(field<HTMLInputElement>('redirectAfterLogin').value)
        .withContext('page zero is a page')
        .toBe('0');
      expect(field<HTMLInputElement>('redirectAfterRegistration').value)
        .withContext('null is the only expression of "no redirect"')
        .toBe('');
      expect(field<HTMLInputElement>('redirectAfterLogout').value).toBe('42');

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

      // The measured legacy defaults, transcribed from `UserModuleBase.vb` L98-L190. Two are
      // counter-intuitive and are asserted precisely because of it: the electronic mail column
      // defaults to HIDDEN while the address column defaults to SHOWN.
      expect(field<HTMLInputElement>('columnEmail').checked)
        .withContext('hidden by default — measured, not a slip')
        .toBeFalse();
      expect(field<HTMLInputElement>('columnAddress').checked)
        .withContext('shown by default')
        .toBeTrue();

      // ⚠ ASSERTED AGAINST THE IMPORTED CONSTANT, NEVER AGAINST A DIGIT. `DEFAULT_PAGE_SIZE` is
      // the workspace's single home for the page size, and the legacy routine's own default was
      // the same value — so importing it proves the two still agree, whereas a literal here would
      // keep passing after the shared constant had moved and the screen had followed it.
      expect(field<HTMLInputElement>('recordsPerPage').value).toBe(String(DEFAULT_PAGE_SIZE));
      // A valid profile is required at sign-in but NOT at registration. Also measured.
      expect(field<HTMLInputElement>('securityRequireValidProfile').checked).toBeFalse();
      expect(field<HTMLInputElement>('securityRequireValidProfileAtLogin').checked).toBeTrue();
      // The three redirects are empty rather than minus one: the server refuses a negative
      // identifier outright, so the legacy marker would turn a valid policy into a rejection.
      expect(field<HTMLInputElement>('redirectAfterLogin').value).toBe('');
      expect(field<HTMLInputElement>('redirectAfterRegistration').value).toBe('');
      expect(field<HTMLInputElement>('redirectAfterLogout').value).toBe('');
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
    /**
     * ⚠ PROVIDER CLOSURE. Ten provider fields and two password-aging fields existed on the
     * legacy screen and have NO member on this contract, so there is nothing for a control to
     * bind to. Their absence is asserted rather than assumed, because a later reader searching
     * for them needs to find a case saying they are gone on purpose.
     */
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

      // The two dropped legacy headings must not appear anywhere, and neither must the legacy
      // provider sentence claiming a configuration file has to be edited — that file does not
      // exist in the target, so the sentence is not merely unhelpful, it is false.
      expect(markup).not.toContain('Membership Provider Settings');
      expect(markup).not.toContain('Password Aging Settings');
      expect(markup).not.toContain('web.config');

      // ⚠ THE RESOURCE-WINS RULE, MADE EXECUTABLE IN THE NEGATIVE. The legacy markup wrote its own
      // section headings — "Provider Settings" and "Password Settings" — while the resource values
      // the page actually rendered read "Membership Provider Settings" and "Password Aging
      // Settings". A port that trusted the markup would have shipped two wrong headings. Both
      // spellings of both headings are asserted absent, so neither the markup wording nor the
      // resource wording can reappear through a well-meaning re-addition of a dropped section.
      expect(markup).withContext('nor the markup wording the resource overrode').not.toContain(
        'Provider Settings',
      );
      expect(markup).not.toContain('Password Settings');

      // ⚠⚠ THE TWO LEGACY MISSPELLINGS ARE A DOCUMENTED OMISSION, NOT A CORRECTION. The dropped
      // password-aging help text reads "(value of 0 measn the password never expires)" and "the
      // number of days warning the user will recieve that their password is about to expire" —
      // "measn" and "recieve", both shipped, both of which domain-logic preservation would have
      // FORBIDDEN correcting had the fields survived. They do not survive: the contract carries no
      // expiry member, so there is no field for either string to attach to and the question of
      // preserving them never arises. What is asserted instead is that neither the misspelt nor
      // the corrected form appears — because a later reader who re-adds the section must re-derive
      // the wording from the resource file rather than from a tidied copy left behind here.
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

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — THE LEGACY SENTINEL VOCABULARY
  // ---------------------------------------------------------------------------------------------------
  //
  // `Library/Components/Shared/Null.vb` gives the legacy codebase a marker for every primitive,
  // and this contract collides with two of them at once: the marker for a missing integer is
  // MINUS ONE while the tenant table's identity seeds at minus one, and the marker for a missing
  // string is the EMPTY STRING rather than a null. The page table's identity seeds at ZERO, so
  // zero is a real page as well. Every case in this group exists because a translation that read
  // one of those values as "absent" would look correct and change what a tenant sees.

  describe('the legacy sentinel vocabulary', () => {
    it('renders an empty text setting as an empty control, never as the word for nothing', () => {
      // The legacy marker for a missing string is the empty string, and the contract preserves
      // that: an unset text setting arrives as "" rather than as a null.
      arrive(settings({ securityEmailValidation: '', securityDisplayNameFormat: '' }));

      expect(field<HTMLTextAreaElement>('securityEmailValidation').value).toBe('');
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toBe('');

      // ⚠ NEITHER CONTROL MAY EVER SHOW THE WORD FOR NOTHING. A screen that let an absent value
      // reach a control through string interpolation would render "null" or "undefined" as
      // editable text, and an operator would then save it as a stored setting.
      const markup = host().textContent ?? '';

      expect(markup).not.toContain('null');
      expect(markup).not.toContain('undefined');
    });

    it('refuses a null text setting on the wire rather than rendering it', () => {
      create();

      // ⚠ THE CONTRACT DECLARES BOTH TEXT MEMBERS NON-NULL and the reader enforces it, so a null
      // in either is a CONTRACT VIOLATION rather than an "unset" state to be interpreted. That is
      // what makes empty and null indistinguishable at the control: only one of them can arrive.
      // The three landing pages are the members that genuinely admit null, and they are asserted
      // separately below.
      //
      // MIGRATION: this is the target's answer to the legacy pair of emptiness tests — the same
      // codebase compared one screen's message against a literal empty string in one place and
      // against its own empty-string marker in another, and only the fact that the two were the
      // same value made both correct. Here there is one representation and it is checked.
      const read = expectRequest('GET', SETTINGS_URL);

      read.flush({ data: { ...settings(), securityEmailValidation: null }, meta: null });
      fixture.detectChanges();

      // The violation is raised while interpreting a successful response, so it carries no
      // problem document and no transport status — which is exactly the case the separate summary
      // paragraph exists for. The banner is reserved for a document.
      const paragraph = query('.membership-settings__transport-failure');

      expect(paragraph).withContext('the violation is surfaced').not.toBeNull();
      expect((paragraph?.textContent ?? '').length).withContext('with a real sentence').toBeGreaterThan(0);
      expect(query('.error-banner__title')).withContext('and not through the banner').toBeNull();

      // ⚠ AND NOTHING RENDERS THE WORD FOR NOTHING. A screen that accepted the malformed value
      // would put it in front of an operator as editable text, who would then save it.
      const markup = host().textContent ?? '';

      expect(markup).not.toContain('null');
      expect(markup).not.toContain('undefined');
    });

    it('round-trips all three landing pages unchanged, zero and null alike', () => {
      // ⚠ ZERO IS A REAL PAGE AND MINUS ONE IS NEVER SENT. The page table's identity seeds at
      // zero, which is why the server's floor is zero rather than one, and null is the only
      // expression of "no redirect" on this contract. A round trip is the strongest available
      // statement of that: whatever the server sent must come back byte for byte.
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

      write.flush(null, { status: 204, statusText: 'No Content' });
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

      // ⚠ NO TOKEN EXPANSION. The bracketed tokens are expanded by an excluded subsystem, so the
      // brackets must still be there — a screen that substituted a name would corrupt the stored
      // template the moment it was saved back.
      const markup = host().textContent ?? '';

      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toContain('[FIRSTNAME]');
      expect(field<HTMLInputElement>('securityDisplayNameFormat').value).toContain('[LASTNAME]');
      expect(markup).withContext('no name was substituted for a token').not.toContain('undefined');

      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);
      const body = writtenPolicy(write);

      // ⚠ AND NO CLIENT-SIDE EVALUATION. The expression goes back exactly as it came: nothing in
      // this screen compiles it, executes it or matches anything against it, because evaluating a
      // tenant-supplied pattern in a browser would hand a stored setting the ability to hang the
      // page. The only rule applied before the server sees it is a length bound.
      expect(body.securityEmailValidation).toBe(expression);
      expect(body.securityDisplayNameFormat).toBe(format);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(settings({ securityEmailValidation: expression, securityDisplayNameFormat: format }));
    });

    it('carries no member whose absence needs a sentinel rendering rule', () => {
      arrive();

      // ⚠ THESE ARE DOCUMENTED OMISSIONS, ASSERTED SO THEY CANNOT BE MISTAKEN FOR OVERSIGHTS.
      // Three legacy sentinel rules have NO member on this contract to attach to, and a later
      // reader looking for the tests that enforce them needs to find this case instead:
      //
      //   * A PASSWORD EXPIRY OF ZERO MEANING "NEVER EXPIRES" — the rule the legacy help text
      //     described. The expiry pair belongs to the dropped password-aging section and is
      //     absent from the contract, so there is no field to render "never expires" in. This is
      //     REVERSE DRIFT: legacy behaviour with no target member.
      //   * AN ALLOWANCE OF ZERO MEANING "UNLIMITED" AND MINUS ONE MEANING "NOT SET" — a genuine
      //     collision the transport service documents at the contract level, but the account
      //     policy carries no allowance member, so nothing on this screen can express it.
      //   * A DATE AT ITS MINIMUM MEANING "NO DATE" — the legacy marker for a missing date, which
      //     must render EMPTY rather than as the first day of year one. This contract carries no
      //     date member at all, so this screen never formats one.
      //
      // The assertions are therefore absence assertions, and the third also guards the rendering:
      // the minimum-date text must not appear even incidentally.
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

      // ⚠⚠ THE DISTINCTION THIS CASE EXISTS FOR. Zero is DATA. It is refused because the server
      // accepts nothing below one for a page size, NOT because it reads as empty — so the message
      // must be the range message and must NOT be the emptiness message. A screen that tested
      // truthiness would report the field as unfilled, which is a different and false claim, and
      // the same mistake elsewhere in this contract would read a false switch or page zero as
      // "unset". The framework's own emptiness test is a null-and-length test rather than a
      // truthiness test, which is what lets zero reach the range rule at all.
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

    it('accepts both ends of the permitted page-size range', () => {
      arrive();

      type('recordsPerPage', String(MINIMUM_RECORDS_PER_PAGE));

      expect(fieldErrors()).withContext('the floor is permitted').not.toContain(PAGE_SIZE_RANGE_MESSAGE);

      type('recordsPerPage', String(MAXIMUM_RECORDS_PER_PAGE));

      expect(fieldErrors()).withContext('the ceiling is permitted').not.toContain(PAGE_SIZE_RANGE_MESSAGE);
    });

    it('refuses a negative page identifier on each of the three redirects', () => {
      arrive();

      for (const name of [
        'redirectAfterLogin',
        'redirectAfterRegistration',
        'redirectAfterLogout',
      ]) {
        expect(field<HTMLInputElement>(name).getAttribute('min'))
          .withContext(`${name} declares the floor`)
          .toBe('0');

        type(name, '-1');
        submitForm();

        // ⚠ MINUS ONE IS THE LEGACY MARKER FOR "NO INTEGER" AND IS REFUSED HERE. The server's
        // floor is zero because the page table's identity seeds at zero.
        expect(fieldErrors()).withContext(`${name} is refused`).toContain(NEGATIVE_PAGE_MESSAGE);
        expect(httpMock.match(() => true)).withContext('nothing is sent').toHaveSize(0);

        type(name, '');
      }
    });

    it('accepts page zero on a redirect, because zero is a real page', () => {
      arrive(settings({ redirectAfterLogin: null }));

      type('redirectAfterLogin', '0');

      expect(fieldErrors()).not.toContain(NEGATIVE_PAGE_MESSAGE);

      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL);

      expect(writtenPolicy(write).redirectAfterLogin)
        .withContext('page zero travels as zero, never as null')
        .toBe(0);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(settings());
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

      // ⚠ THE DOCUMENT BOUND MAKES THIS UNREACHABLE BY TYPING, so the value is written through
      // the control itself. The rule still has to exist, because a value can arrive by paste
      // handling, by autofill or from a policy the server already holds.
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
    it('replaces the whole policy with all twenty-three members and answers 204', () => {
      const policy = settings();

      arrive(policy);
      submitForm();

      const write = expectRequest('PUT', SETTINGS_URL, 'the policy write');
      const body = writtenPolicy(write);

      // ⚠ TWENTY-THREE MEMBERS, EVERY TIME. "Unchanged settings keep their value" is expressed
      // by sending them all, so a zero, a false and an empty string are values being asserted
      // rather than absences to be filtered out.
      //
      // MIGRATION: this replaces the legacy DIRTY-ONLY PARTIAL SAVE. The legacy handler walked
      // its editors and wrote only the ones reporting themselves changed
      // (`Website/admin/Users/UserSettings.ascx.vb` L172), one stored setting at a time. The end
      // state is identical because every member travels with the value it currently holds — and
      // that is precisely why no member may ever be dropped for looking empty.
      // ⚠ THIS IS ALSO THE SPELLING CHECK. The server's naming policy lower-cases the leading
      // upper-case RUN of a member name, so a name ending in an initialism comes out differently
      // from one that does not — and a client built on the wrong spelling reads undefined with
      // nothing failing to reveal it. Comparing the key set the screen SENDS against the key set
      // the fixture RECEIVED is what makes a spelling drift a failure here rather than a silent
      // undefined at run time. Every fixture in this file is built from the contract interface, so
      // a misspelling would not even compile.
      expect(memberNames(body)).toEqual(memberNames(policy));
      expect(memberNames(body)).toHaveSize(POLICY_MEMBER_COUNT);
      expect(body).toEqual(policy);

      // MIGRATION: each member is sent as its OWN TYPE. The legacy handler coerced every value
      // to text on the way to storage (`Website/admin/Users/UserSettings.ascx.vb` L174), so a
      // switch was stored as the word for true and a count as its digits. Asserting the wire
      // types is what stops that coercion creeping back in through a form control's string value.
      expect(typeof body.columnEmail).withContext('a switch is a switch').toBe('boolean');
      expect(typeof body.recordsPerPage).withContext('a count is a number').toBe('number');
      expect(typeof body.displayMode).withContext('a mode is a number').toBe('number');
      expect(typeof body.securityDisplayNameFormat).withContext('text is text').toBe('string');

      write.flush(null, { status: 204, statusText: 'No Content' });
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
      type('redirectAfterLogin', '');
      type('redirectAfterRegistration', '');
      type('redirectAfterLogout', '');

      submitForm();

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
      // Every member is still on the wire. A body assembled by filtering truthiness would be
      // down to a handful by now, and the server — which binds without eliding a default —
      // would read the difference as an instruction it was never given.
      expect(memberNames(body)).toHaveSize(POLICY_MEMBER_COUNT);

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(settings({ displayMode: 0 }));
    });

    it('re-reads the policy and the listing once the write has landed', () => {
      arrive(settings({ recordsPerPage: 25 }));

      type('recordsPerPage', '50');
      submitForm();

      expectRequest('PUT', SETTINGS_URL).flush(null, { status: 204, statusText: 'No Content' });
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
      expectRequest('PUT', SETTINGS_URL).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(policy);

      expect(successSpy).toHaveBeenCalledOnceWith(SAVED_MESSAGE);
      expect(navigateSpy).toHaveBeenCalledOnceWith([ACCOUNT_LISTING_PATH]);
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

      write.flush(null, { status: 204, statusText: 'No Content' });
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

  describe('a refused read', () => {
    it('shows the refusal in the shared banner and withholds submission entirely', () => {
      create();

      expectRequest('GET', SETTINGS_URL).flush(
        problem('user.membership_settings.source_missing', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
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

      // ⚠ THE FORM IS DRAWN — the read has finished, unsuccessfully — SO THE COMMAND MUST BE
      // WITHHELD. Testing only the in-flight flag would satisfy the rule for exactly as long as
      // the request lasted: a refused read clears that flag without ever applying a policy,
      // leaving the seated defaults on screen and submittable over whatever the tenant has.
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

      // ⚠ A RESPONSE THAT NEVER ARRIVED STILL CARRIES A STATUS — zero — so the store attaches it
      // to a synthesised document and the BANNER carries the failure. The separate summary
      // paragraph is reserved for a failure with no status at all, which is a value thrown
      // outside a response rather than anything a request can produce; it is therefore absent
      // here, and asserting that is the point of this case.
      expect(query('.membership-settings__transport-failure'))
        .withContext('the paragraph is for a statusless failure, not for status zero')
        .toBeNull();

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

      // ⚠ THE SERVER'S KEY IS ITS OWN MODEL-STATE SPELLING, NOT CAMEL-CASED. The shared reader
      // matches case-insensitively, which is what makes this land on the right control. The
      // document's per-field dictionary is read with a BRACKET throughout the workspace — property
      // access on an index signature is refused by the compiler here — so a key that is not a
      // valid identifier is reached the same way as one that is.
      expect(fieldErrors()).toContain('That page does not belong to this site.');
      expect(field<HTMLInputElement>('redirectAfterLogin').getAttribute('aria-invalid')).toBe('true');
    });

    it('pins a per-field refusal whose key carries a binder prefix', () => {
      arrive();

      submitForm();
      expectRequest('PUT', SETTINGS_URL).flush(
        problem(
          'user.membership_settings.invalid',
          400,
          'The request could not be processed as submitted.',
          // ⚠ THE BINDER PREFIXES ARE REAL AND MUST BE TOLERATED. A body-bound parameter produces
          // a JSON-path key, and a parameter named for the request produces a dotted one; neither
          // prefix is part of the field's name and no form control is ever named with one. Both
          // forms are exercised in one document, together with a third key in the server's own
          // casing, because a reader that matched by exact spelling would find none of them and
          // the messages would vanish with nothing failing to reveal it.
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

      // ⚠ THE BANNER INTERCEPTS 429 BEFORE THE DOMAIN RULE, so the word shown is "Please wait"
      // rather than "Warning". The domain severity for 429 IS warning; the banner refines it to
      // its own calm band because a retryable delay is not a refusal to report as one.
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

      write.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerWriteFollowUp(settings());
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 8 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark and exactly one heading level below the page title', () => {
      arrive();

      // ⚠ THE SHELL OWNS EACH LANDMARK EXACTLY ONCE. A screen emitting its own would nest a
      // landmark inside the same landmark and give assistive technology two of something there
      // must be one of. All four are asserted, not just the two that are easy to forget.
      expect(queryAll('header')).withContext('the shell owns the banner').toHaveSize(0);
      expect(queryAll('main')).withContext('the shell owns the main region').toHaveSize(0);
      expect(queryAll('nav')).withContext('the shell owns navigation').toHaveSize(0);
      expect(queryAll('footer')).withContext('the shell owns the footer').toHaveSize(0);
      // One section heading, at level two, directly under the page heading with no level skipped.
      expect(queryAll('h2')).toHaveSize(1);
      expect(queryAll('h4')).toHaveSize(0);
    });

    it('renders no table, because there is no grid on this screen', () => {
      arrive();

      // MIGRATION: the legacy markup opened with a fixed-width borderless table
      // (`Website/admin/Users/UserSettings.ascx` L6) which carried no data at all — it was pure
      // layout, as every table in that generation of markup was. Arrangement is a grid in the
      // paired stylesheet here, so not one table element is emitted. This screen edits ONE record
      // and lists nothing, so the shared data table has no business here either.
      expect(host().querySelectorAll('table')).withContext('no table of any kind').toHaveSize(0);
      expect(queryAll('app-data-table')).withContext('and no grid component').toHaveSize(0);
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
        POLICY_MEMBER_COUNT,
      );

      for (const control of controls) {
        expect(control.closest('app-form-field'))
          .withContext(`${control.id} sits inside a shared field`)
          .not.toBeNull();
      }

      // The two form commands are the only buttons this screen's own template emits, and they are
      // commands rather than controls: the shared library is closed at ten members and has no
      // button member, so there is nothing to wrap them in.
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
        POLICY_MEMBER_COUNT - 9,
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
});
