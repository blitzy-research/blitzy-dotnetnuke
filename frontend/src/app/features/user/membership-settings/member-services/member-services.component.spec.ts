//
// Specification for the account holder's own subscriptions panel, MOUNTED by the tenant's
// account-policy screen at /settings/membership and not routed. AAP 0.5.1.8 consolidates
// `Website/admin/Users/MemberServices.ascx` into that feature and AAP 0.4.4 freezes the
// console's route table at twenty-five addresses with no member-services address among them,
// so the panel takes its subject account as an INPUT from its host instead of from a route
// segment. Everything else about it is unchanged, which is why the cases below are unchanged
// too: what the panel DOES was never a property of how it was addressed.
//
// ---------------------------------------------------------------------------
// WHAT THIS FILE PROVES
// ---------------------------------------------------------------------------
// That the screen reproduces the four affordances of
// `Website/admin/Users/MemberServices.ascx` against the account's own subscriptions, and
// that it reproduces the PRESENTATION RULES its code-behind expressed in four helpers —
// the command word (`ServiceText`), the two command visibilities (`ShowSubscribe`,
// `ShowTrial`) and the two composed fee sentences (`FormatPrice`, `FormatTrial`) — without
// re-deriving any of the decisions the server makes.
//
// The negative half of that claim is the half worth having, and every case closes with a
// verification that nothing is left outstanding: a screen that read a catalogue for an
// account it may not read, submitted an empty code the API would refuse, or offered a
// command for a service that charges a fee, fails here rather than in a browser.
//
// ---------------------------------------------------------------------------
// NO PROJECT RULES DOCUMENT EXISTS
// ---------------------------------------------------------------------------
// The engagement supplied none, and the rules review answers with a single line saying so.
// Nothing here is justified by a project rule and nothing is relaxed by their absence.
//
// ---------------------------------------------------------------------------
// WHAT IS DELIBERATELY NOT ASSERTED
// ---------------------------------------------------------------------------
//   * NO PAYMENT FLOW. The legacy screen handed a fee-bearing role to
//     `~/admin/Sales/PayPalSubscription.aspx`; sales administration is out of scope, so
//     there is no redirect to assert and the screen states the refusal instead.
//   * NO LAPSED TEST OF ITS OWN. The screen never compares an expiry against the browser's
//     clock, so no case seats a clock: the server decides, and every case flushes the flag
//     it decided.
//   * NO AUTHORISATION DECISION. The gate on the HOST's route and the policy on each
//     endpoint are the authorities. The ownership test here is advisory, and what is
//     asserted is that a doomed request is not ISSUED — never that the panel enforces
//     anything.
//   * NOTHING ABOUT WHERE THE PANEL IS MOUNTED. That is a property of the host screen and is
//     asserted in `membership-settings.component.spec.ts`, which proves the element is
//     rendered and receives the caller's own account. Asserting it in both places would
//     leave two authorities for one fact.
//   * NO TENANT SELF-SERVICE SWITCH. The account-policy endpoint is gated on tenant
//     administration, so this screen cannot read it; the refusal it produces arrives as a
//     problem document like any other and is asserted as one.
//

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import type { ProblemDetails } from '../../../../core/models/problem-details.model';
import type { MemberService } from '../../../../core/models/user.model';
import { TokenStorageService } from '../../../../core/services/token-storage.service';
import { MemberServicesComponent } from './member-services.component';

// ---------------------------------------------------------------------------
// THE ADDRESSES
// ---------------------------------------------------------------------------
//
// Spelled out here rather than imported from the endpoint map, so that a wrong route
// template cannot agree with itself. Every one is RELATIVE, because the production
// environment's base is relative and the test target replaces nothing.

/** The account this specification acts as. */
const ACCOUNT_ID = 7;

/** The catalogue read. Unpaged. */
const SERVICES_URL = `/api/v1/users/${ACCOUNT_ID}/services`;

/** The invitation-code submission. */
const REDEMPTIONS_URL = `/api/v1/users/${ACCOUNT_ID}/services/redemptions`;

/**
 * The service every fixture below uses, and its identifier is ZERO deliberately.
 *
 * `Roles.RoleID` seeds `IDENTITY(0, 1)`, so role zero is a real role — and it is exactly the
 * value a truthiness test drops. Addressing every command with it is what proves the screen
 * interpolates the identifier it was given.
 */
const SERVICE_ID = 0;

/** The subscription of this account to that service. */
const SUBSCRIPTION_URL = `/api/v1/users/${ACCOUNT_ID}/services/${SERVICE_ID}/subscription`;

/** That service's trial. */
const TRIAL_URL = `/api/v1/users/${ACCOUNT_ID}/services/${SERVICE_ID}/trial`;

/** The legacy tenant key, which is minus one and is a REAL tenant rather than a marker. */
const NULL_INTEGER = -1;

// ---------------------------------------------------------------------------
// FIXTURES
// ---------------------------------------------------------------------------

/**
 * One catalogue row.
 *
 * Defaults to a FREE, PUBLIC service the account does not hold — the simplest row the legacy
 * grid could show and the only one whose command is unconditionally performable. Every other
 * shape below is expressed as an override of it, so each case names only what it varies.
 */
function offer(overrides: Partial<MemberService> = {}): MemberService {
  return {
    roleId: SERVICE_ID,
    roleName: 'Newsletter',
    description: 'Occasional announcements',
    serviceFee: null,
    billingPeriod: null,
    billingFrequency: null,
    trialFee: null,
    trialPeriod: null,
    trialFrequency: null,
    effectiveDate: null,
    expiryDate: null,
    isSubscribed: false,
    isTrialUsed: false,
    isExpired: false,
    subscriptionAction: 'Subscribe',
    subscriptionOffered: true,
    subscriptionRequiresPayment: false,
    trialOffered: false,
    ...overrides,
  };
}

/** A refusal document, shaped as the API's exception handler emits one. */
function refusal(code: string, status: number, title: string, detail: string): ProblemDetails {
  return {
    type: `urn:dnnmigration:error:${code}`,
    title,
    status,
    detail,
    traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
    correlationId: '7f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d',
  };
}

describe('MemberServicesComponent', () => {
  let fixture: ComponentFixture<MemberServicesComponent>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MemberServicesComponent],
      // The real client FIRST, then the testing backend that displaces it. Reversing these
      // two is the commonest false green in an Angular 19 suite.
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // The stored session outlives a single injector, so it is cleared before every case as
    // well as after one: a case that seated one identity would otherwise hand it to whichever
    // case Jasmine happens to run next.
    TestBed.inject(TokenStorageService).clear();

    // Created but NOT rendered. The account identifier is a REQUIRED signal input - required
    // as a BINDING, though its value may be null - and rendering before it is bound would throw
    // rather than answer.
    fixture = TestBed.createComponent(MemberServicesComponent);
  });

  afterEach(() => {
    // Unsatisfied or unexpected traffic fails the case. This is the mechanism that proves a
    // refused command sends NOTHING further, rather than merely proving the case did not look.
    httpMock.verify();
    fixture.destroy();
    TestBed.inject(TokenStorageService).clear();
  });

  // -------------------------------------------------------------------------
  // READING THE RENDERED DOCUMENT
  // -------------------------------------------------------------------------

  function host(): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  function query(selector: string): Element | null {
    return host().querySelector(selector);
  }

  function queryAll(selector: string): readonly Element[] {
    return Array.from(host().querySelectorAll(selector));
  }

  /** The trimmed text of the first match, or the empty string when there is none. */
  function text(selector: string): string {
    return query(selector)?.textContent?.trim() ?? '';
  }

  /** The body cells of the grid, row by row, as trimmed text. */
  function bodyRows(): readonly (readonly string[])[] {
    return queryAll('tbody tr').map((row) =>
      Array.from(row.querySelectorAll('td,th')).map((cell) => cell.textContent?.trim() ?? ''),
    );
  }

  /** The grid's heading row, as trimmed text. Hidden headings render as empty cells. */
  function headings(): readonly string[] {
    return queryAll('thead th').map((cell) => cell.textContent?.trim() ?? '');
  }

  function codeInput(): HTMLInputElement {
    const element = query('#member-services-code');

    if (!(element instanceof HTMLInputElement)) {
      throw new Error('the invitation-code field is not rendered');
    }

    return element;
  }

  function submitButton(): HTMLButtonElement {
    const element = query('.member-services__code-submit');

    if (!(element instanceof HTMLButtonElement)) {
      throw new Error('the code submit control is not rendered');
    }

    return element;
  }

  /** Every row command control, in document order. */
  function rowActions(): readonly HTMLButtonElement[] {
    return queryAll('.member-services__row-action').filter(
      (element): element is HTMLButtonElement => element instanceof HTMLButtonElement,
    );
  }

  // -------------------------------------------------------------------------
  // DRIVING THE SCREEN
  // -------------------------------------------------------------------------

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

  /** The number of requests still outstanding, whatever they are. */
  function outstandingRequestCount(): number {
    return httpMock.match(() => true).length;
  }

  /**
   * Seats the caller's identity.
   *
   * Read from the stored session rather than fetched, so seating it is what decides whether the
   * account the host supplied is the caller's own — and therefore whether the panel reads
   * anything at all. The expiry is a FIXED literal: reading the clock in a specification makes
   * it depend on when it runs.
   */
  function seatIdentity(userId: number): void {
    TestBed.inject(TokenStorageService).store({
      accessToken: 'not-a-real-token.not-a-real-payload.not-a-real-signature',
      expiresAtUtc: '2099-12-31T23:59:59.000Z',
      refreshToken: 'not-a-real-refresh-token',
      mustChangePassword: false,
      mustUpdateProfile: false,
      passwordExpiring: false,
      user: {
        userId,
        portalId: NULL_INTEGER,
        portalName: 'Baseline Portal',
        username: 'caller',
        displayName: 'The Caller',
        email: 'caller@example.test',
        isSuperUser: false,
        isPortalAdministrator: false,
        roles: ['Registered Users'],
        permissions: [],
      },
    });
  }

  /**
   * Arrives as the account holder and satisfies the catalogue read.
   *
   * The identifier is bound through `setInput` rather than by assigning the field, because the
   * component is change-detected on push and reads it as a signal: `setInput` is what marks
   * the view dirty. This is exactly how the host binds it - `[accountId]` over the account the
   * session resolved - so the fixture exercises the shipped contract rather than a stand-in.
   */
  function arrive(services: readonly MemberService[] = [offer()]): void {
    seatIdentity(ACCOUNT_ID);
    fixture.componentRef.setInput('accountId', ACCOUNT_ID);
    fixture.detectChanges();

    expectRequest('GET', SERVICES_URL, 'the catalogue read').flush({
      data: services,
      meta: null,
    });
    fixture.detectChanges();
  }

  /** Satisfies the re-read every command issues on success. */
  function settleAfterCommand(services: readonly MemberService[] = [offer()]): void {
    expectRequest('GET', SERVICES_URL, 'the re-read after a command').flush({
      data: services,
      meta: null,
    });
    fixture.detectChanges();
  }

  // =========================================================================
  // ARRIVAL AND THE GRID
  // =========================================================================

  describe('arrival', () => {
    it('reads the catalogue once and renders the legacy column set in the legacy order', () => {
      arrive([offer()]);

      // `MemberServices.ascx` L29-L70 declares, in order: the subscription command, the trial
      // command, `RoleName`, `Description`, the composed fee, the composed trial fee and the
      // expiry.
      //
      // ⚠ THE FIRST TWO HEADINGS ARE PRESENT IN THE DOCUMENT AND PAINTED NOWHERE, which is the
      // deliberate difference from the legacy grid rather than a drift from it. Neither legacy
      // command column declared `HeaderText`, and `Localization.vb` L1483-L1493 built its
      // heading key from that value — so a column with no heading text was skipped entirely and
      // its cells were announced against nothing. Each column here carries a name that the
      // shared grid renders visually hidden: a cell is announced with its column name either
      // way, so nothing is lost visually and an unlabelled command column is avoided. Reading
      // text content therefore sees the names.
      expect(headings()).toEqual([
        'Subscribe',
        'Use Trial',
        'Name',
        'Description',
        'Service Fee',
        'Trial Fee',
        'Expiry Date',
      ]);
      expect(bodyRows().length).toBe(1);
      expect(text('.member-services__help')).toContain('manage your subscriptions');
    });

    it('renders one row per offered service, with the name and description columns bound', () => {
      arrive([offer(), offer({ roleId: 9, roleName: 'Premium', description: null })]);

      const rows = bodyRows();

      expect(rows.length).toBe(2);
      expect(rows[0][2]).toBe('Newsletter');
      expect(rows[0][3]).toBe('Occasional announcements');
      expect(rows[1][2]).toBe('Premium');
      expect(rows[1][3])
        .withContext('an absent description renders empty, never as the word null')
        .toBe('');
    });

    it('issues no request while the host has not resolved an account', () => {
      seatIdentity(ACCOUNT_ID);
      fixture.componentRef.setInput('accountId', null);
      fixture.detectChanges();

      // The host derives the account from the session, which resolves asynchronously, so an
      // absent value is the state on arrival rather than a fault. There is no key to read a
      // catalogue for, and teardown's verification is what proves nothing was sent.
      expect(outstandingRequestCount()).toBe(0);
      expect(text('.member-services__notice')).toBe(
        'Your subscriptions will be shown here once your account has been identified.',
      );
      expect(query('app-data-table')).toBeNull();
    });

    it('renders its heading at the section level, never as a page of its own', () => {
      arrive();

      // The panel is mounted inside a screen that already emits the page's level-one heading, so
      // a second page header would both duplicate that landmark and claim the panel is
      // addressable - which AAP 0.4.4 says it is not. The heading names the region, and the
      // region points at it, so the section carries an accessible name.
      expect(query('app-page-header')).toBeNull();
      expect(text('h2.member-services__heading')).toBe('Manage Services');
      expect(query('section.member-services')?.getAttribute('aria-labelledby')).toBe(
        'member-services-heading',
      );
      expect(query('h2.member-services__heading')?.id).toBe('member-services-heading');
    });

    it('issues no request for an account the caller does not hold, and says why', () => {
      // MIGRATION: the legacy container hid the tab outright whenever an administrator reached
      // an account through the administrative edit entry point (`ManageUsers.ascx.vb`
      // L61-L66), which a mounted panel cannot do for itself. Every endpoint here is gated on
      // account ownership, so predicting the refusal is better than provoking it five times.
      //
      // The shipped host supplies the caller's own account, so this is a guard rather than a
      // state a reader can navigate to - and it is asserted precisely because nothing in the
      // markup would reveal its absence if a later host bound some other account.
      seatIdentity(99);
      fixture.componentRef.setInput('accountId', ACCOUNT_ID);
      fixture.detectChanges();

      expect(outstandingRequestCount()).toBe(0);
      expect(text('.member-services__notice')).toContain('your own services only');
      expect(query('.member-services__code')).toBeNull();
    });

    it('waits for the session before deciding, so no catalogue is read for an unknown caller', () => {
      // The identity read is asynchronous. Treating "not yet resolved" as ownership would issue
      // a request for an account the caller may not hold; treating it as non-ownership would
      // flash the wrong-account notice on every arrival. Neither happens: nothing is read and
      // neither notice claims the account belongs to somebody else.
      fixture.componentRef.setInput('accountId', ACCOUNT_ID);
      fixture.detectChanges();

      expect(outstandingRequestCount()).toBe(0);
      expect(text('.member-services__notice')).not.toContain('your own services only');
    });

    it('renders an empty grid rather than a failure when the tenant offers nothing', () => {
      arrive([]);

      expect(bodyRows().length)
        .withContext('the shared grid renders its own empty row')
        .toBe(1);
      expect(query('app-error-banner')?.textContent?.trim() ?? '').toBe('');
    });
  });

  // =========================================================================
  // THE COMPOSED FEE COLUMNS
  // =========================================================================

  describe('the fee columns', () => {
    it('renders a service with no recurring charge as free', () => {
      // `FormatPrice(price, period, frequency)` (`MemberServices.ascx.vb` L200-L214) answers
      // `NoFee.Text` for the codes `N` and the empty string. The contract's frequency is
      // NULLABLE where the legacy read a non-nullable string, so a stored null — which reached
      // the legacy as the empty string through its null contract — takes the same branch.
      arrive([offer({ billingFrequency: null }), offer({ roleId: 9, billingFrequency: 'N' })]);

      const rows = bodyRows();

      expect(rows[0][4]).toBe('Free');
      expect(rows[1][4]).toBe('Free');
    });

    it('renders a one-off charge as the bare amount, with no period and no unit', () => {
      // The `O` branch calls the single-argument formatter and stops. A period rendered here
      // would assert a recurrence the row does not have.
      arrive([offer({ serviceFee: 25, billingPeriod: 3, billingFrequency: 'O' })]);

      expect(bodyRows()[0][4]).toBe('25.00');
    });

    it('composes the recurring sentence from the amount, the period and the unit', () => {
      // `Fee.Text` is "{0} Every {1} {2}" and `Frequency_M.Text` is "Month(s)".
      arrive([offer({ serviceFee: 12, billingPeriod: 1, billingFrequency: 'M' })]);

      expect(bodyRows()[0][4]).toBe('12.00 Every 1 Month(s)');
    });

    it('carries a sub-unit fee through unrounded, where the legacy projection erased it', () => {
      // ⚠ THE DIVERGENCE THIS CASE PINS. The terminal `GetServices` procedure published the fee
      // only when `convert(int, R.ServiceFee) <> 0`, so fifty cents arrived as null and the
      // legacy grid rendered "Free" for a role the subscribe path still handed to a payment
      // page. The stored value crosses intact now, and the refusal below is what says a charge
      // applies.
      arrive([
        offer({
          serviceFee: 0.5,
          billingPeriod: 1,
          billingFrequency: 'M',
          subscriptionRequiresPayment: true,
        }),
      ]);

      expect(bodyRows()[0][4]).toBe('0.50 Every 1 Month(s)');
    });

    it('composes the trial sentence with its own wording, not the recurring wording', () => {
      // `TrialFee.Text` is "{0} for {1} {2}" — a duration, not a recurrence — and
      // `Frequency_D.Text` is "Day(s)". The two sentences are deliberately separate functions.
      arrive([offer({ trialFee: 0, trialPeriod: 14, trialFrequency: 'D' })]);

      expect(bodyRows()[0][5]).toBe('0.00 for 14 Day(s)');
    });

    it('renders a service with no trial as free in the trial column', () => {
      arrive([offer({ trialFrequency: 'N' })]);

      expect(bodyRows()[0][5]).toBe('Free');
    });

    it('renders an unknown frequency code without inventing a unit', () => {
      // `Localization.GetString` answered the empty string for a key it did not hold, and the
      // composed sentence still rendered. A one-character code the client does not know is DATA
      // — the column is `char(1)` and a later release may add one — so the amount and the period
      // are shown and no unit is guessed at.
      arrive([offer({ serviceFee: 5, billingPeriod: 2, billingFrequency: 'Q' })]);

      expect(bodyRows()[0][4]).toBe('5.00 Every 2');
    });
  });

  // =========================================================================
  // THE EXPIRY COLUMN
  // =========================================================================

  describe('the expiry column', () => {
    it('renders nothing when the account holds no expiring assignment', () => {
      arrive([offer({ expiryDate: null })]);

      expect(bodyRows()[0][6]).toBe('');
    });

    it('renders the date when the assignment is current', () => {
      arrive([offer({ isSubscribed: true, expiryDate: '2099-03-04T00:00:00Z' })]);

      // Rendered in UTC by the shared date pipe, in the short shape `FormatExpiryDate` used.
      expect(bodyRows()[0][6]).toBe('3/4/2099');
    });

    it("renders the lapsed word from the server's flag, never from the browser's clock", () => {
      // ⚠ ONE FLAG DRIVES BOTH THIS CELL AND THE COMMAND WORD, which unifies a disagreement the
      // legacy carried: `FormatExpiryDate` said "Expired" for an expiry falling exactly today
      // while `ServiceText` still offered "Unsubscribe" for the same row. The date below is in
      // the PAST and the flag is set, which is the only combination the server produces.
      arrive([
        offer({
          isSubscribed: true,
          isExpired: true,
          expiryDate: '2020-01-01T00:00:00Z',
          subscriptionAction: 'Renew',
        }),
      ]);

      expect(bodyRows()[0][6]).toBe('Expired');
      expect(rowActions()[0].textContent?.trim()).toBe('Renew');
    });
  });

  // =========================================================================
  // THE ROW COMMANDS
  // =========================================================================

  describe('the row commands', () => {
    it('renders the command word the server decided, and names the service in it', () => {
      arrive([offer({ isSubscribed: true, subscriptionAction: 'Unsubscribe' })]);

      const action = rowActions()[0];

      expect(action.textContent?.trim()).toBe('Unsubscribe');

      // The bare word repeated once per row tells a reader moving between controls nothing
      // about which service they are on, and the name CONTAINS the visible word so a spoken
      // command still matches what is seen.
      expect(action.getAttribute('aria-label')).toBe('Unsubscribe Newsletter');
    });

    it('offers no command at all for a service the server does not offer one for', () => {
      // `ShowSubscribe` (`MemberServices.ascx.vb` L307-L323) rendered the link only for a
      // public role that either charges nothing or has a payment processor configured. A tenant
      // that publishes a paid role without configuring one offered nothing, and neither does
      // this.
      arrive([offer({ subscriptionOffered: false })]);

      expect(rowActions().length).toBe(0);
      expect(query('.member-services__unavailable')).toBeNull();
    });

    it('states the refusal instead of a command when the service charges a fee', () => {
      // ⚠ THREE OUTCOMES WHERE THE LEGACY HAD TWO. The legacy sent this row to a payment page;
      // sales administration is out of scope, so a command that could only ever be refused is
      // not offered and the reason is stated in its place. Dropping the row would hide a service
      // the tenant genuinely offers.
      arrive([
        offer({
          serviceFee: 12,
          billingPeriod: 1,
          billingFrequency: 'M',
          subscriptionRequiresPayment: true,
        }),
      ]);

      expect(rowActions().length).toBe(0);
      expect(text('.member-services__unavailable')).toBe('Payment required');
      expect(bodyRows()[0][4])
        .withContext('the fee is still shown, so the offering remains visible')
        .toBe('12.00 Every 1 Month(s)');
    });

    it('subscribes with no body and re-reads the catalogue afterwards', () => {
      arrive([offer()]);

      rowActions()[0].click();
      fixture.detectChanges();

      const command = expectRequest('POST', SUBSCRIPTION_URL);

      expect(command.request.body).toBeNull();
      command.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      // The command answers with no body, so the state on screen can only come from a re-read.
      settleAfterCommand([offer({ isSubscribed: true, subscriptionAction: 'Unsubscribe' })]);

      expect(rowActions()[0].textContent?.trim()).toBe('Unsubscribe');
    });

    it('cancels through the removing verb at the same address', () => {
      arrive([offer({ isSubscribed: true, subscriptionAction: 'Unsubscribe' })]);

      rowActions()[0].click();
      fixture.detectChanges();

      const command = expectRequest('DELETE', SUBSCRIPTION_URL);

      expect(command.request.body).toBeNull();
      command.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      settleAfterCommand([offer()]);

      expect(rowActions()[0].textContent?.trim()).toBe('Subscribe');
    });

    it('renews through the subscribe request, because the legacy ran one handler for both', () => {
      arrive([
        offer({ isSubscribed: true, isExpired: true, subscriptionAction: 'Renew' }),
      ]);

      expect(rowActions()[0].textContent?.trim()).toBe('Renew');

      rowActions()[0].click();
      fixture.detectChanges();

      expectRequest('POST', SUBSCRIPTION_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      settleAfterCommand([offer({ isSubscribed: true, subscriptionAction: 'Unsubscribe' })]);
    });

    it('offers the trial only when the server offers it, and takes it at its own address', () => {
      arrive([offer({ trialOffered: false })]);

      expect(rowActions().length)
        .withContext('one command control, the subscription — no trial')
        .toBe(1);

      // Re-arrive with the trial offered. The store clears nothing for the same account, so the
      // second read replaces the rows in place.
      fixture.componentRef.setInput('accountId', ACCOUNT_ID);
      fixture.detectChanges();
      expect(outstandingRequestCount()).toBe(0);

      arriveAgainWith([
        offer({
          serviceFee: 12,
          billingFrequency: 'M',
          billingPeriod: 1,
          subscriptionOffered: false,
          trialFee: 0,
          trialPeriod: 14,
          trialFrequency: 'D',
          trialOffered: true,
        }),
      ]);

      const actions = rowActions();

      expect(actions.length).toBe(1);
      expect(actions[0].textContent?.trim()).toBe('Use Trial');
      expect(actions[0].getAttribute('aria-label')).toBe('Use Trial Newsletter');

      actions[0].click();
      fixture.detectChanges();

      const command = expectRequest('POST', TRIAL_URL);

      expect(command.request.body).toBeNull();
      command.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      settleAfterCommand([offer({ trialOffered: false })]);
    });

    it("presents a refused command in the server's own words and sends nothing further", () => {
      arrive([offer()]);

      rowActions()[0].click();
      fixture.detectChanges();

      expectRequest('POST', SUBSCRIPTION_URL).flush(
        refusal(
          'user.service.disabled-forbidden',
          403,
          'Forbidden',
          'This site does not offer self-service subscription management.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // No re-read: nothing changed server-side, and teardown's verification is what proves it.
      expect(outstandingRequestCount()).toBe(0);
      expect(query('app-error-banner')?.textContent ?? '').toContain(
        'does not offer self-service subscription management',
      );
    });

    it('disables every command control while one is in flight', () => {
      arrive([offer()]);

      rowActions()[0].click();
      fixture.detectChanges();

      // Genuinely disabled rather than merely announced as unavailable, which is what stops an
      // impatient second activation issuing a second request.
      expect(rowActions()[0].disabled).toBeTrue();
      expect(submitButton().disabled).toBeTrue();

      expectRequest('POST', SUBSCRIPTION_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      settleAfterCommand([offer({ isSubscribed: true, subscriptionAction: 'Unsubscribe' })]);

      expect(rowActions()[0].disabled).toBeFalse();
    });
  });

  // =========================================================================
  // THE INVITATION CODE
  // =========================================================================

  describe('the invitation code', () => {
    it('refuses an empty submission locally and sends nothing', () => {
      arrive([offer()]);

      submitButton().click();
      fixture.detectChanges();

      // The API refuses an empty code with a reason of its own; the local rule exists so an
      // obvious mistake costs no round trip, and the wording reads the way the server's does.
      expect(outstandingRequestCount()).toBe(0);
      expect(host().textContent).toContain('An RSVP Code is required.');
    });

    it('refuses a code longer than the stored column and sends nothing', () => {
      arrive([offer()]);

      const input = codeInput();

      input.value = 'x'.repeat(51);
      input.dispatchEvent(new Event('input'));
      submitButton().click();
      fixture.detectChanges();

      expect(outstandingRequestCount()).toBe(0);
      expect(host().textContent).toContain('may not exceed 50 characters');
    });

    it('declares the stored width on the field, as the legacy box did', () => {
      arrive([offer()]);

      // `MemberServices.ascx` L14 declares `maxlength="50"`, which is the width of
      // `Roles.RSVPCode` and the bound the API's own validator applies.
      expect(codeInput().getAttribute('maxlength')).toBe('50');
    });

    it('sends the code exactly as typed, reports every role it joined, and clears the field', () => {
      arrive([offer()]);

      const input = codeInput();

      input.value = '  Founders-2026  ';
      input.dispatchEvent(new Event('input'));
      submitButton().click();
      fixture.detectChanges();

      const command = expectRequest('POST', REDEMPTIONS_URL);

      // ⚠ UNTRIMMED AND UNFOLDED. The legacy comparison was ordinary string equality against
      // the stored code (`MemberServices.ascx.vb` L410), so leading space and case both
      // mattered; trimming here would admit codes the legacy application refused.
      expect(command.request.body).toEqual({ code: '  Founders-2026  ' });

      command.flush({
        data: {
          roles: [
            { roleId: SERVICE_ID, roleName: 'Newsletter' },
            { roleId: 9, roleName: 'Founders' },
          ],
        },
        meta: null,
      });
      fixture.detectChanges();
      settleAfterCommand([offer({ isSubscribed: true, subscriptionAction: 'Unsubscribe' })]);

      // `RSVPSuccess.Text`, kept verbatim — its closing instruction to sign out and back in is
      // still true, because role membership reaches this client as claims on the access token.
      expect(text('.member-services__report-text')).toContain(
        'you will need to Logout and then Login',
      );

      // The joined roles, which the legacy could not report: its walk had no early exit, so one
      // code may join several roles and it showed one fixed sentence for all of them.
      expect(queryAll('.member-services__report-list li').map((item) => item.textContent?.trim()))
        .toEqual(['Newsletter', 'Founders']);
      expect(codeInput().value)
        .withContext('a successful code is consumed, so the field is cleared')
        .toBe('');
    });

    it('announces the report politely, because focus stays in the form', () => {
      arrive([offer()]);
      redeem('Founders-2026');
      settleAfterCommand([offer()]);

      const report = query('.member-services__report');

      expect(report?.getAttribute('role')).toBe('status');
      expect(report?.getAttribute('aria-live')).toBe('polite');
    });

    it('keeps a refused code in the field and presents the refusal from the server', () => {
      arrive([offer()]);

      const input = codeInput();

      input.value = 'nope';
      input.dispatchEvent(new Event('input'));
      submitButton().click();
      fixture.detectChanges();

      expectRequest('POST', REDEMPTIONS_URL).flush(
        refusal(
          'user.service.code-not-matched',
          400,
          'Bad Request',
          'The invitation code entered is not valid or does not exist.',
        ),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // No re-read, and no report. The legacy screen distinguished the two outcomes with two
      // different messages, and this is the failing one.
      expect(outstandingRequestCount()).toBe(0);
      expect(query('.member-services__report')).toBeNull();
      expect(query('app-error-banner')?.textContent ?? '').toContain('is not valid or does not');

      // The value survives: the operator may have mistyped one character, and retyping fifty is
      // not a correction.
      expect(codeInput().value).toBe('nope');
    });

    it('dismisses its own report without discarding the catalogue', () => {
      arrive([offer()]);
      redeem('Founders-2026');
      settleAfterCommand([offer({ isSubscribed: true, subscriptionAction: 'Unsubscribe' })]);

      const dismiss = query('.member-services__report-dismiss');

      if (!(dismiss instanceof HTMLButtonElement)) {
        throw new Error('the dismiss control is not rendered');
      }

      dismiss.click();
      fixture.detectChanges();

      expect(query('.member-services__report')).toBeNull();
      expect(bodyRows().length)
        .withContext('dismissing a message is not a reason to discard the rows')
        .toBe(1);
    });

    it('discards the report once a later command changes the state it described', () => {
      arrive([offer()]);
      redeem('Founders-2026');
      settleAfterCommand([offer({ isSubscribed: true, subscriptionAction: 'Unsubscribe' })]);

      expect(query('.member-services__report')).not.toBeNull();

      rowActions()[0].click();
      fixture.detectChanges();

      expect(query('.member-services__report'))
        .withContext('a report of what a code joined is not true once one is cancelled')
        .toBeNull();

      expectRequest('DELETE', SUBSCRIPTION_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      fixture.detectChanges();
      settleAfterCommand([offer()]);
    });
  });

  // -------------------------------------------------------------------------
  // COMPOSITE DRIVERS
  //
  // Declared after the cases that use them because a function declaration is hoisted; kept
  // here so the cases above read as behaviour rather than as setup.
  // -------------------------------------------------------------------------

  /** Types a code, submits it, and answers with one joined role. */
  function redeem(code: string): void {
    const input = codeInput();

    input.value = code;
    input.dispatchEvent(new Event('input'));
    submitButton().click();
    fixture.detectChanges();

    expectRequest('POST', REDEMPTIONS_URL).flush({
      data: { roles: [{ roleId: SERVICE_ID, roleName: 'Newsletter' }] },
      meta: null,
    });
    fixture.detectChanges();
  }

  /**
   * Re-reads the catalogue for the SAME account with different rows.
   *
   * Used where a case needs a second shape without a second arrival: the screen reads on
   * arrival and after every command, so a bare re-read has to be provoked by a command. This
   * drives one through the store's own path by cancelling and re-subscribing the simplest
   * row, which is why it flushes two requests.
   */
  function arriveAgainWith(services: readonly MemberService[]): void {
    rowActions()[0].click();
    fixture.detectChanges();
    expectRequest('POST', SUBSCRIPTION_URL).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    fixture.detectChanges();
    settleAfterCommand(services);
  }
});
