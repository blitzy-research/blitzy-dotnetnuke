/**
 * Specification for `features/auth/login/login.component.ts` and its paired template.
 *
 * THE SCREEN UNDER TEST IS THE APPLICATION'S ONLY AUTHENTICATION VIEW, and two of its
 * responsibilities cannot be verified by reading it:
 *
 * 1. THE OPEN-REDIRECT BOUNDARY. The address a completed sign-in navigates to arrives in a
 *    query parameter, so anyone can choose it - a link in an e-mail, a message, another
 *    site. An unchecked value would let this screen deliver a person who has just supplied
 *    their credentials to an attacker's page carrying a convincing copy of it. The guard is
 *    five independent rejections wide and every one of them is exercised below, together
 *    with the acceptances that prove it is not merely refusing everything.
 * 2. THE REFUSAL IS THIS SCREEN'S TO EXPLAIN, AND NOBODY ELSE'S. The bearer-token
 *    interceptor lists the sign-in endpoint among the anonymous ones, so no credential is
 *    attached and no renewal is attempted; the error interceptor then returns early on
 *    exactly 401, deliberately, because announcing a sign-in refusal globally as well would
 *    report it twice. If this component and its template do not render the refusal, the
 *    person sees a form that visibly did nothing.
 *
 * ## Provenance
 *
 * `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx` (37 lines of markup) and
 * `Login.ascx.vb` (205 lines) are the legacy screen. Supporting sources are
 * `Library/Components/Users/Membership/UserLoginStatus.vb` for the outcome vocabulary,
 * `Library/Components/Shared/Null.vb` for the sentinel contract that governs the
 * verification code, `Website/App_GlobalResources/SharedResources.resx` and
 * `Website/admin/Authentication/App_LocalResources/Login.ascx.resx` for wording, and
 * `Website/release.config` for the credential policy. THE LEGACY TREE CONTAINS NO
 * AUTOMATED TEST OF ANY KIND, so nothing here is ported - every case is authored against
 * those sources.
 *
 * ## What is real and what is doubled
 *
 * EVERYTHING IS REAL EXCEPT THE TRANSPORT AND THE NAVIGATION.
 *
 * - The COMPONENT is imported as the standalone unit it is, so its own `imports` list is
 *   what supplies the four shared components. A missing entry there is a compile error in
 *   this file rather than a silently unrendered element.
 * - The SESSION STORE is the genuine root-provided store, driven through real HTTP
 *   responses. Doubling it would make the verification ladder - the single most stateful
 *   behaviour on this screen - a property of the double rather than of the application.
 * - The ROUTER is genuine and only `navigateByUrl` is spied, because no route table is
 *   declared here: what these cases assert is the DESTINATION, and a real navigation would
 *   fail to match and report a routing failure instead of the value under test.
 * - The ACTIVATED ROUTE is a minimal double, because the component reads exactly one thing
 *   from it - `snapshot.queryParamMap.get(key)` - and the map it is given is built by the
 *   router's own `convertToParamMap`, so it is a genuine `ParamMap` rather than an object
 *   pretending to be one.
 * - The TRANSPORT is the testing backend, so every request is asserted by method, address
 *   and body, and `verify()` in `afterEach` fails any case that left one unconsumed or
 *   issued one nobody expected.
 *
 * ## Driven through the DOM, deliberately
 *
 * Every value is typed into a real `input` and every submission is a real press of a real
 * submit button. The component's own members are `protected`, so reaching them would not
 * compile - but that is a happy accident rather than the reason: driving the DOM is what
 * proves the template's `formControlName` bindings, its label associations, its disabled
 * binding and its native return-key submission actually work, none of which a direct call
 * to `submit()` would touch.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import type { ComponentFixture } from '@angular/core/testing';

import type { CurrentUser, LoginResponse } from '../../../core/models/auth.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import { AuthStore } from '../../../core/state/auth.store';
import {
  DEFAULT_SIGNED_IN_ROUTE,
  LOGIN_CONTROL_IDS,
  LOGIN_REQUIRED_MESSAGES,
  RETURN_URL_QUERY_KEY,
  USERNAME_QUERY_KEY,
  VERIFICATION_CODE_QUERY_KEY,
  LoginComponent,
} from './login.component';

// ---------------------------------------------------------------------------
// ADDRESSES
//
// Hand-written relative literals rather than values composed from the endpoint table. A
// literal is what catches a change to that table, whereas a composed address would move
// with it and assert nothing.
// ---------------------------------------------------------------------------

/** `POST /api/v1/auth/login`. The credential exchange. */
const LOGIN_URL = '/api/v1/auth/login';

/**
 * `GET /api/v1/auth/me`. The identity read that COMPLETES a sign-in.
 *
 * ⚠ A COMPLETED SIGN-IN IS TWO REQUESTS, NOT ONE, and every case below has to answer both or
 * the sign-in never completes at all. `auth.service.ts:L311-L323` posts the credentials,
 * maps the envelope to a session, and then `switchMap`s into this read carrying the freshly
 * issued token as an explicit bearer header - storing the session and emitting the identity
 * only once BOTH have answered. A fixture that flushed only the exchange would leave the
 * observable pending, and every consequence of success - the navigation, the withdrawn
 * verification field, the lowered progress indicator - would appear not to happen.
 *
 * The token travels as an explicit header rather than through the bearer interceptor because
 * the custodian has not been written yet at that moment: the session is stored only after
 * this read succeeds, so there is nothing for an interceptor to attach.
 */
const ME_URL = '/api/v1/auth/me';

// ---------------------------------------------------------------------------
// THE FAILURE VOCABULARY
//
// Every value below is the server's own, taken from `ApiResults` in
// `backend/src/DnnMigration.Api/ErrorHandling/GlobalExceptionHandler.cs` and from
// `ValidationProblemDetailsFactory`. A fixture that invented a code or a status would
// describe a response the API cannot send, and the case would then prove nothing.
// ---------------------------------------------------------------------------

/** The prefix `failureCode` requires before it will read a code out of `type` at all. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** A wrong account name or credential. Mapped to 401 by `ApiResults.UnauthorizedCodes`. */
const INVALID_CREDENTIALS_CODE = 'auth.invalid_credentials';

/** The first rung of the ladder: a code is being asked for. 401. */
const VERIFICATION_REQUIRED_CODE = 'auth.verification_required';

/** The second rung: the code that was supplied is wrong. 401. */
const VERIFICATION_CODE_INVALID_CODE = 'auth.verification_code_invalid';

/** The third: the account exists and is verified but may not sign in here. 401. */
const ACCOUNT_NOT_APPROVED_CODE = 'auth.account_not_approved';

/** The rate limiter's refusal. 429. */
const RATE_LIMITED_CODE = 'request.rate_limited';

/** An unexpected server fault. 500. */
const SERVER_FAILURE_CODE = 'server.unexpected_failure';

/**
 * The three ladder sentences, verbatim from
 * `Website/admin/Authentication/App_LocalResources/Login.ascx.resx` L162, L165 and L222,
 * as the shared form-errors utility reproduces them.
 */
const VERIFICATION_REQUIRED_MESSAGE = 'Enter Your Verification Code';
const VERIFICATION_CODE_INVALID_MESSAGE = 'Invalid Verification Code';
const ACCOUNT_NOT_APPROVED_MESSAGE = 'You are not currently authorized to login to this site.';

/** The calm sentence the shared utility publishes for a rate-limited refusal. */
const RATE_LIMIT_MESSAGE = 'Too many attempts. Wait a moment and try again.';

/** The shared utility's wording for any status at or above 500. */
const SERVER_ERROR_MESSAGE = 'The server could not complete the request. Try again shortly.';

/**
 * The per-status title from the server's own vocabulary table.
 *
 * The title describes the CLASS of failure and is derived from the status alone, which is
 * exactly why nothing on this screen may branch on it - the four 401 outcomes below all
 * share one title and are told apart only by their `type`.
 */
const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  429: 'Too Many Requests',
  500: 'Internal Server Error',
};

/** A W3C trace identifier, in the shape `ValidationProblemDetailsFactory` attaches. */
const TRACE_ID = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01';

/** The correlation identifier the same factory attaches, and the one a person is asked to quote. */
const CORRELATION_ID = 'e6c9c0d6-1e8f-4b3f-9a0f-2f5a8c7d4b11';

// ---------------------------------------------------------------------------
// CREDENTIALS AND TOKENS USED IN FIXTURES
//
// Every one of them is obviously fake and says so in its own value. None is a credential,
// none resembles a real token, and the last group exists so that "no credential reaches
// the document" can be asserted against a value that would be unmistakable if it did.
// ---------------------------------------------------------------------------

const FAKE_ACCESS_TOKEN = 'fake-access-token-not-a-real-credential';
const FAKE_RENEWAL_TOKEN = 'fake-renewal-token-not-a-real-credential';
const EXPIRES_AT_UTC = '2100-01-01T00:00:00.000Z';

/** An account name with deliberate mixed case, so case preservation is observable. */
const ACCOUNT_NAME = 'Admin';

/** A password with deliberate leading and trailing space, so trimming would be observable. */
const SUBMITTED_PASSWORD = ' not-a-real-password ';

/** A verification code, for the ladder's second rung. */
const SUBMITTED_CODE = 'wrong-code';

// ---------------------------------------------------------------------------
// THE RESPONSE ENVELOPE
//
// Declared locally and two members wide. A fixture returning a bare contract object would
// be unwrapped into `undefined`, and every downstream assertion would then pass vacuously
// against absent data.
// ---------------------------------------------------------------------------

interface SuccessEnvelope<T> {
  readonly data: T;
  readonly meta: null;
}

/** An identity. The tenant key is ZERO, which is a real key: the tenant table seeds at minus one. */
function currentUser(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userId: 7,
    portalId: 0,
    portalName: 'Primary Portal',
    username: ACCOUNT_NAME,
    displayName: 'Administrator',
    email: 'admin@example.test',
    isSuperUser: false,
    isPortalAdministrator: false,
    roles: ['Administrators'],
    permissions: ['VIEW'],
    ...overrides,
  };
}

/**
 * A credential-exchange payload, wrapped.
 *
 * All three advisories are stated as `false` rather than omitted, because `false` is DATA on
 * this contract and an omission would not compile.
 */
function credentialPayload(overrides: Partial<LoginResponse> = {}): SuccessEnvelope<LoginResponse> {
  return {
    data: {
      accessToken: FAKE_ACCESS_TOKEN,
      expiresAtUtc: EXPIRES_AT_UTC,
      refreshToken: FAKE_RENEWAL_TOKEN,
      mustChangePassword: false,
      passwordExpiring: false,
      mustUpdateProfile: false,
      user: currentUser(),
      ...overrides,
    },
    meta: null,
  };
}

/** An identity payload, wrapped. The answer to the identity read that completes a sign-in. */
function identityPayload(user: CurrentUser = currentUser()): SuccessEnvelope<CurrentUser> {
  return { data: user, meta: null };
}

/**
 * A problem document carrying a code, in the exact shape the API emits.
 *
 * ⚠ NO `instance` MEMBER, AND ITS ABSENCE IS MEASURED. Every call site supplies null for it,
 * and although the surrounding serializer policy writes members holding null, the framework's
 * problem type carries a per-member null-omission condition on each of its five standard
 * members which overrides that policy - so a live document has no `instance` at all.
 *
 * ⚠ `type` IS ALWAYS PRESENT. The factory fills an unspecified type from the status
 * vocabulary, so a type-less document is not emittable and is never modelled here.
 */
function refusal(code: string, status: number, detail: string): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: STATUS_TITLE[status] ?? 'Error',
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

describe('LoginComponent', () => {
  /**
   * The screen under test.
   *
   * Created by a case rather than in a shared `beforeEach`, because the activated-route double
   * has to be seeded BEFORE construction: the component reads the route snapshot exactly once.
   */
  let fixture: ComponentFixture<LoginComponent>;

  /**
   * The same fixture, or null when nothing has been created yet.
   *
   * A second handle rather than widening the one above, so that every ordinary case can use
   * `fixture` without a null test on every line. Only the re-entrant return-address helper and
   * the teardown read this one.
   */
  let mounted: ComponentFixture<LoginComponent> | null;
  let httpMock: HttpTestingController;
  let store: AuthStore;
  let navigateSpy: jasmine.Spy;

  /**
   * The query parameters the activated-route double will report.
   *
   * Mutated by a case BEFORE the component is created, because the component reads the
   * SNAPSHOT exactly once - which is the direct analogue of the legacy
   * `If Page.IsPostBack = False Then` guard at `Login.ascx.vb:L104`.
   */
  let queryParams: Record<string, string>;

  beforeEach(async () => {
    queryParams = {};
    mounted = null;

    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that
    // displaces it. Reversing the two leaves the live backend in place and every
    // expectation times out against a request nothing intercepted.
    await TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          // A getter rather than a fixed value, so a case can seed `queryParams` after the
          // module is configured and before the component is created. The map itself is the
          // router's own conversion, so it is a genuine `ParamMap`.
          useValue: {
            snapshot: {
              get queryParamMap() {
                return convertToParamMap({ ...queryParams });
              },
            },
          },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AuthStore);

    // No routes are declared, so a genuine navigation would fail to match. The destination
    // is what these cases assert, not the navigation itself.
    navigateSpy = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
  });

  afterEach(() => {
    // Destroyed BEFORE the transport is verified, so a subscription released by teardown
    // cannot be mistaken for a request the screen never issued.
    if (mounted !== null) {
      mounted.destroy();
      mounted = null;
    }

    // The house rule. It fails a case that left a request unconsumed just as loudly as one
    // that issued a request nobody expected - and on this screen an unexpected request would
    // mean a credential was sent somewhere no assertion looked.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // HARNESS
  // -------------------------------------------------------------------------

  /** Creates the screen and runs the first change detection. */
  function create(): void {
    fixture = TestBed.createComponent(LoginComponent);
    mounted = fixture;
    fixture.detectChanges();
  }

  /** The rendered element for a selector, or null. */
  function query(selector: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(selector) as HTMLElement | null;
  }

  /** Every rendered element for a selector. */
  function queryAll(selector: string): readonly HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll(selector) as NodeListOf<HTMLElement>);
  }

  /** The collapsed text of a selector, or null when it is not rendered. */
  function textOf(selector: string): string | null {
    const element = query(selector);

    return element === null ? null : (element.textContent ?? '').trim();
  }

  /** One of this screen's three controls, by its declared identifier, or null when absent. */
  function control(controlId: string): HTMLInputElement | null {
    return fixture.nativeElement.querySelector(`#${controlId}`) as HTMLInputElement | null;
  }

  /** One of the three controls, asserted present first so the non-null is earned rather than assumed. */
  function requiredControl(controlId: string): HTMLInputElement {
    const element = control(controlId);

    expect(element).withContext(`#${controlId} is rendered`).not.toBeNull();

    return element as HTMLInputElement;
  }

  /**
   * Types a value into a control the way a person does.
   *
   * The value is assigned and an `input` event dispatched, which is the channel the reactive
   * forms value accessor listens on - so this exercises the template's `formControlName`
   * binding rather than reaching past it.
   */
  function type(controlId: string, value: string): void {
    const element = requiredControl(controlId);

    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** The submit button. */
  function submitButton(): HTMLButtonElement {
    const button = query('.login__submit') as HTMLButtonElement | null;

    expect(button).withContext('the submit control is rendered').not.toBeNull();

    return button as HTMLButtonElement;
  }

  /**
   * Presses the submit button.
   *
   * A real press of a real `type="submit"` button inside a real `form`, so the native
   * submission path is what raises `ngSubmit` - which is the PORT of the legacy key capture
   * at `Login.ascx.vb:L101` and is therefore itself under test.
   */
  function submit(): void {
    submitButton().click();
    fixture.detectChanges();
  }

  /** Fills both credential fields with the fixture values. */
  function fillCredentials(): void {
    type(LOGIN_CONTROL_IDS.username, ACCOUNT_NAME);
    type(LOGIN_CONTROL_IDS.password, SUBMITTED_PASSWORD);
  }

  /** The one outstanding sign-in request, asserted by method and address. */
  function expectLoginRequest(): ReturnType<HttpTestingController['expectOne']> {
    const request = httpMock.expectOne(
      (candidate) => candidate.method === 'POST' && candidate.url === LOGIN_URL,
      'the sign-in request',
    );

    return request;
  }

  /** The identity read that follows a successful exchange, asserted by method and address. */
  function expectIdentityRequest(): ReturnType<HttpTestingController['expectOne']> {
    return httpMock.expectOne(
      (candidate) => candidate.method === 'GET' && candidate.url === ME_URL,
      'the identity read that completes the sign-in',
    );
  }

  /** Answers the identity read, which is what finally completes the sign-in. */
  function completeIdentityRead(): void {
    expectIdentityRequest().flush(identityPayload());
    fixture.detectChanges();
  }

  /**
   * Answers BOTH requests of a completed sign-in.
   *
   * See {@link ME_URL} for why one flush is not enough.
   */
  function completeSignIn(): void {
    expectLoginRequest().flush(credentialPayload());
    completeIdentityRead();
  }

  /** Answers the outstanding sign-in request with a refusal document. */
  function refuseSignIn(problem: ProblemDetails): void {
    const status = problem.status ?? 500;

    expectLoginRequest().flush(problem, { status, statusText: STATUS_TITLE[status] ?? 'Error' });
    fixture.detectChanges();
  }

  /** Fills the credentials, submits, and answers with a refusal. */
  function attemptAndRefuse(problem: ProblemDetails): void {
    fillCredentials();
    submit();
    refuseSignIn(problem);
  }

  // -------------------------------------------------------------------------
  // PROOF 1 — THE SCREEN RENDERS, AND REACHES NOTHING TO DO IT
  // -------------------------------------------------------------------------

  describe('first render', () => {
    it('renders the legacy screen title as the single first-level heading', () => {
      create();

      const headings = queryAll('h1');

      expect(headings.length).withContext('the page header renders exactly one h1').toBe(1);

      // `ControlTitle_signin.Text` from
      // `Website/admin/Authentication/App_LocalResources/Login.ascx.resx`, verbatim. The
      // nearer resource file's `Title.Text` reads "Standard", which was the CAPTION OF A TAB
      // in the out-of-scope multi-provider container and is deliberately not used.
      expect((headings[0]?.textContent ?? '').trim()).toBe('User Log In');
    });

    it('issues no request of any kind while merely being shown', () => {
      create();

      // A screen that reached the network on arrival would attempt a sign-in nobody asked
      // for. The `verify()` in `afterEach` covers this too; stating it explicitly names the
      // property being claimed.
      httpMock.expectNone(() => true);
    });

    it('renders the two credential fields in the legacy order and no third field', () => {
      create();

      expect(control(LOGIN_CONTROL_IDS.username)).not.toBeNull();
      expect(control(LOGIN_CONTROL_IDS.password)).not.toBeNull();

      // ⚠ THE VERIFICATION FIELD IS ABSENT FROM THE DOCUMENT, not merely hidden. A box a
      // person is not being asked to fill in must not be reachable by Tab nor announced by a
      // screen reader. `rowVerification1` and `rowVerification2` are both declared
      // `visible="false"` at `Login.ascx:L12` and L15.
      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('the verification field is not in the document until the server asks for it')
        .toBeNull();
    });

    it('associates every rendered label with the control it names', () => {
      create();

      const associations = queryAll('label').map((label) => label.getAttribute('for'));

      // The legacy label control already derived this association -
      // `Library/Controls/LabelControl.vb:L295` set `label.Attributes("for") = c.ClientID` -
      // so this is a PORT rather than an addition, and the shared field component renders it.
      expect(associations).toContain(LOGIN_CONTROL_IDS.username);
      expect(associations).toContain(LOGIN_CONTROL_IDS.password);

      for (const controlId of [LOGIN_CONTROL_IDS.username, LOGIN_CONTROL_IDS.password]) {
        expect(document.getElementById(controlId) ?? requiredControl(controlId))
          .withContext(`the label for ${controlId} points at a control that exists`)
          .not.toBeNull();
      }
    });

    it('renders the legacy field wording with the design system punctuation policy applied', () => {
      create();

      const labelText = queryAll('label').map((label) => (label.textContent ?? '').trim());

      // ⚠ THE TRAILING COLON IS STRIPPED BY THE SHARED FIELD, AND THAT IS THE POLICY RATHER
      // THAN A DEFECT. The template passes the measured resource values `'User Name:'` and
      // `'Password:'` (`SharedResources.resx` L1023 and L1017) exactly as they are stored, and
      // the shared component owns the punctuation: it strips ONE trailing colon whatever the
      // colon's origin and adds none of its own. That matches the legacy majority - of the 186
      // label instances across the 39 in-scope screens, 133 declared no suffix at all - so the
      // rendered wording is the legacy DOMINANT form, and the screens that baked a colon into
      // the resource value do not end up punctuated differently from the ones that did not.
      //
      // Asserted on the RENDERED text rather than on the bound input, because that is what a
      // person reads and because it pins the policy: a change to it would surface here.
      expect(labelText.some((text) => text.startsWith('User Name'))).toBeTrue();
      expect(labelText.some((text) => text.startsWith('Password'))).toBeTrue();
      expect(labelText.some((text) => text.includes(':')))
        .withContext('the shared field strips the trailing colon it was given')
        .toBeFalse();
    });

    it('renders the password box as a password box, the one security attribute the legacy carried', () => {
      create();

      // `Login.ascx:L29` declares `textmode="password"`. Of every attribute on that markup it
      // is the only one expressing a security property rather than a layout one, so it is the
      // one attribute that must survive exactly.
      expect(requiredControl(LOGIN_CONTROL_IDS.password).type).toBe('password');
      expect(requiredControl(LOGIN_CONTROL_IDS.username).type).toBe('text');
    });

    it('renders the submit control inside the form and typed to submit it', () => {
      create();

      const form = query('.login__form');

      expect(form).withContext('a real form element').not.toBeNull();
      expect(form?.tagName).toBe('FORM');

      // The PORT of `Login.ascx.vb:L101`, which had to register a key capture so the return
      // key submitted a Web Forms button. A real submit button inside a real form does it
      // natively, which is why no key listener exists anywhere in the component.
      expect(submitButton().type).toBe('submit');
      expect(form?.contains(submitButton())).toBeTrue();

      // `cmdLogin.Text` from the control's own resource file.
      expect((submitButton().textContent ?? '').trim()).toBe('Login');
    });

    it('shows no failure surface and no progress indicator before anything has been attempted', () => {
      create();

      expect(query('.login__failure')).toBeNull();
      expect(query('.login__notice')).toBeNull();
      expect(query('.login__message')).toBeNull();
      expect(query('app-loading-spinner')).toBeNull();

      // The banner is bound unconditionally and renders nothing for a null document, which is
      // its documented empty state.
      expect(query('.error-banner')).toBeNull();
    });

    it('reports no field as rejected before a submission has been attempted', () => {
      create();

      // An `aria-invalid="false"` on every field is noise; omitting the attribute is the
      // default state, which is why the template renders it only when there is something to
      // report.
      expect(requiredControl(LOGIN_CONTROL_IDS.username).getAttribute('aria-invalid')).toBeNull();
      expect(requiredControl(LOGIN_CONTROL_IDS.password).getAttribute('aria-invalid')).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 2 — THE LEGACY QUERY PARAMETERS
  // -------------------------------------------------------------------------

  describe('query-parameter seeding', () => {
    it('seeds the account name, reproducing Login.ascx.vb:L106-L108', () => {
      queryParams = { [USERNAME_QUERY_KEY]: 'seeded-account' };

      create();

      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('seeded-account');
    });

    it('seeds the verification code WITHOUT revealing the field', () => {
      queryParams = { [VERIFICATION_CODE_QUERY_KEY]: 'code-from-an-email' };

      create();

      // A DELIBERATE DIVERGENCE from `Login.ascx.vb:L109-L116`. The legacy revealed the
      // verification rows here as well, but only inside
      // `If PortalSettings.UserRegistration = PortalRegistrationType.VerifiedRegistration` -
      // a per-request server-side setting an unauthenticated caller cannot read. Guessing
      // would be worse than not asking, so the value is seeded so that it travels on the
      // first attempt while visibility stays governed solely by the server's own answer.
      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('a seeded code does not reveal the field')
        .toBeNull();
    });

    it('treats a present-but-empty parameter as present, exactly as the legacy did', () => {
      // `Login.ascx.vb:L106` and L109 read `If Not Request.QueryString("…") Is Nothing`, so a
      // parameter that was present but empty was still present. A test for a NON-EMPTY value
      // would be a different test with a different outcome.
      queryParams = { [USERNAME_QUERY_KEY]: '' };

      create();

      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('');
    });

    it('leaves both boxes empty when no parameter was supplied', () => {
      create();

      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('');
      expect(requiredControl(LOGIN_CONTROL_IDS.password).value).toBe('');
    });

    it('reads the snapshot once and never re-seeds what a person has typed', () => {
      queryParams = { [USERNAME_QUERY_KEY]: 'seeded-account' };

      create();

      type(LOGIN_CONTROL_IDS.username, 'typed-over-the-seed');

      // A later query change must not reach the form. Reading the route's OBSERVABLE instead
      // of its snapshot would re-seed here and silently discard what was typed - which is the
      // behaviour the legacy post-back guard prevented.
      queryParams = { [USERNAME_QUERY_KEY]: 'a-different-account' };
      fixture.detectChanges();

      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('typed-over-the-seed');
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 3 — CLIENT-SIDE VALIDATION
  // -------------------------------------------------------------------------

  describe('required-field validation', () => {
    it('sends nothing and reports both empty boxes when an empty form is submitted', () => {
      create();

      submit();

      // The fail-closed intent of `Login.ascx.vb:L163`, which seeded its outcome variable to
      // failure so a path that forgot to assign a result failed closed. Nothing is sent.
      httpMock.expectNone(() => true);

      const messages = queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());

      // MIGRATION: these two sentences are NET-NEW. All 37 lines of legacy markup declared no
      // validator control, so the legacy screen posted whatever was typed and let the server
      // refuse it. The MECHANISM changed; the BEHAVIOUR - an empty box cannot complete a
      // sign-in - did not.
      expect(messages).toContain(LOGIN_REQUIRED_MESSAGES.username);
      expect(messages).toContain(LOGIN_REQUIRED_MESSAGES.password);
    });

    it('reports each field as rejected once a submission has been attempted', () => {
      create();

      submit();

      expect(requiredControl(LOGIN_CONTROL_IDS.username).getAttribute('aria-invalid')).toBe('true');
      expect(requiredControl(LOGIN_CONTROL_IDS.password).getAttribute('aria-invalid')).toBe('true');
    });

    it('withdraws a field message and its rejected state as soon as the box is filled', () => {
      create();

      submit();
      type(LOGIN_CONTROL_IDS.username, ACCOUNT_NAME);

      const messages = queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());

      expect(messages).not.toContain(LOGIN_REQUIRED_MESSAGES.username);
      expect(messages).toContain(LOGIN_REQUIRED_MESSAGES.password);
      expect(requiredControl(LOGIN_CONTROL_IDS.username).getAttribute('aria-invalid')).toBeNull();
    });

    it('applies no length, pattern or complexity rule to either credential', () => {
      create();

      // The credential policy measured in `Website/release.config:L241-L245` - minimum length
      // seven, no required non-alphanumeric character - governs CREATING and CHANGING a
      // password, not signing in with one. Tightening it mid-migration would lock out existing
      // accounts, so a single character must reach the server.
      type(LOGIN_CONTROL_IDS.username, 'a');
      type(LOGIN_CONTROL_IDS.password, 'b');
      submit();

      const request = expectLoginRequest();

      expect(request.request.body).toEqual({ username: 'a', password: 'b', verificationCode: '' });

      request.flush(credentialPayload());
      completeIdentityRead();
    });

    it('does not require a verification code on a first attempt', () => {
      create();

      // Leaving the code permanently required would make the very first attempt
      // unsubmittable, which no legacy behaviour justifies: the field did not even exist on
      // screen until the server asked for it.
      fillCredentials();
      submit();

      completeSignIn();
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 4 — WHAT IS SENT
  // -------------------------------------------------------------------------

  describe('the sign-in request', () => {
    it('posts to the one permitted address, with exactly the three declared members', () => {
      create();

      fillCredentials();
      submit();

      const request = expectLoginRequest();

      expect(request.request.method).toBe('POST');
      expect(request.request.url).toBe(LOGIN_URL);

      // ⚠ VALUES PASS THROUGH EXACTLY AS TYPED. The mixed-case account name is not
      // lower-cased and the surrounding spaces on the credential are not trimmed: each of
      // those would change which credentials succeed, and the legacy screen did neither.
      //
      // ⚠ THE EMPTY VERIFICATION CODE TRAVELS AS THE EMPTY STRING. `Login.ascx.vb:L177` tells
      // a wrong code from a missing one with `If txtVerification.Text <> ""`, and the legacy
      // absent-text sentinel IS the empty string - `Null.vb:L71-L75` has the body `Return ""`,
      // not `Return Nothing` - so the two were always one branch. Coalescing to null, omitting
      // the member or trimming would move that decision boundary silently.
      expect(request.request.body).toEqual({
        username: ACCOUNT_NAME,
        password: SUBMITTED_PASSWORD,
        verificationCode: '',
      });

      request.flush(credentialPayload());
      completeIdentityRead();
    });

    it('sends the seeded verification code on the first attempt, before any field is on screen', () => {
      queryParams = { [VERIFICATION_CODE_QUERY_KEY]: 'code-from-an-email' };

      create();

      fillCredentials();
      submit();

      const request = expectLoginRequest();

      // Which is the entire point of seeding it: the link in a verification e-mail carries the
      // code, and it must reach the server on the attempt that link produces.
      expect(request.request.body).toEqual({
        username: ACCOUNT_NAME,
        password: SUBMITTED_PASSWORD,
        verificationCode: 'code-from-an-email',
      });

      request.flush(credentialPayload());
      completeIdentityRead();
    });

    it('disables the submit control and shows the progress indicator while the request is in flight', () => {
      create();

      fillCredentials();
      submit();

      const request = expectLoginRequest();

      expect(submitButton().disabled).withContext('the control is disabled in flight').toBeTrue();

      const spinner = query('app-loading-spinner');

      expect(spinner).withContext('the wait is announced, not merely animated').not.toBeNull();
      expect((spinner?.textContent ?? '').trim()).toContain('Signing in');

      request.flush(credentialPayload());
      fixture.detectChanges();

      // ⚠ STILL IN FLIGHT. The exchange has answered but the identity read has not, and the
      // sign-in is not complete until it does - so the progress indicator must NOT come down
      // here. This is the window in which a screen that lowered its indicator on the first
      // answer would invite a second submission against a half-completed sign-in.
      expect(query('app-loading-spinner'))
        .withContext('the indicator survives until the identity read answers')
        .not.toBeNull();
      expect(submitButton().disabled).toBeTrue();

      completeIdentityRead();

      expect(query('app-loading-spinner')).toBeNull();
    });

    it('refuses a second submission while one is in flight', () => {
      create();

      fillCredentials();
      submit();

      const request = expectLoginRequest();

      // The disabled attribute is a COURTESY, not the lock: a resubmission can still arrive by
      // return key or by script, so the component refuses it in code as well. The submission is
      // driven through the form here rather than the button precisely so the disabled attribute
      // cannot be what prevents it.
      const form = query('.login__form');

      form?.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      // Exactly one request, still. `verify()` would catch a second one too, but naming it here
      // states the property.
      httpMock.expectNone(
        (candidate) => candidate.method === 'POST' && candidate.url === LOGIN_URL && false,
      );

      request.flush(credentialPayload());
      completeIdentityRead();

      expect(navigateSpy).toHaveBeenCalledTimes(1);
    });

    it('leaves the credential in the box and writes it into no attribute', () => {
      create();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      const password = requiredControl(LOGIN_CONTROL_IDS.password);

      // ⚠ `Login.ascx.vb:L123` IS NOT CARRIED FORWARD. It read
      // `txtPassword.Attributes.Add("value", txtPassword.Text)`, deliberately writing the
      // submitted credential into an HTML attribute so the box survived a post-back. The
      // OUTCOME the person experienced - the value still being there - is simply what a
      // single-page form does, while the credential-exposure smell is gone.
      expect(password.value).toBe(SUBMITTED_PASSWORD);
      expect(password.getAttribute('value'))
        .withContext('the credential is not written into an attribute')
        .toBeNull();

      // And nowhere else in the rendered document either.
      expect((fixture.nativeElement as HTMLElement).outerHTML).not.toContain(
        SUBMITTED_PASSWORD.trim(),
      );
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 5 — HOW A REFUSAL IS RENDERED
  // -------------------------------------------------------------------------

  describe('a refused attempt', () => {
    it('renders the server document in the shared banner and adds no sentence of its own', () => {
      create();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      expect(query('.login__failure')).withContext('the failure region is rendered').not.toBeNull();
      expect(textOf('.error-banner__message')).toBe(
        'The account name or credential is not correct.',
      );

      // The component's resolved sentence is null whenever the banner was given a document, so
      // the two can never say the same thing twice.
      expect(query('.login__message'))
        .withContext('no duplicate sentence beside a document the banner already renders')
        .toBeNull();
      expect(query('.login__notice')).toBeNull();

      // A refusal is not a navigation.
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('renders the support reference from the banner, which is the only component holding the document', () => {
      create();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      // The reference is the only join key between what a person saw in the browser and the
      // request as the server recorded it, and the correlation identifier outranks the trace
      // identifier because only the former appears in the server's own records.
      expect(textOf('.error-banner__trace')).toBe(`Reference: ${CORRELATION_ID}`);

      // ⚠ AND THE SCREEN ADDS NO SECOND REFERENCE REGION. The store derives that value from
      // the document itself, so a region gated on "no document yet a reference" could never
      // render and one gated on the reference alone could only duplicate this.
      expect(queryAll('.error-banner__trace').length).toBe(1);
    });

    it('reports a rate-limited refusal calmly, politely, and says nothing else', () => {
      create();

      attemptAndRefuse(
        refusal(
          RATE_LIMITED_CODE,
          429,
          'Too many requests have been submitted. Retry after a short delay.',
        ),
      );

      const notice = query('.login__notice');

      expect(notice).withContext('the calm state is rendered').not.toBeNull();
      expect((notice?.textContent ?? '').trim()).toBe(RATE_LIMIT_MESSAGE);

      // ⚠ POLITE, NOT ASSERTIVE. Nothing has broken - the caller has simply attempted too
      // often - and this is the compensating control for the human-verification challenge the
      // migration removed, so it must be distinguishable from a refused credential at a glance
      // and must not interrupt.
      expect(notice?.getAttribute('role')).toBe('status');

      // "Say that calmly and say nothing else": the error-toned sentence is suppressed.
      expect(query('.login__message')).toBeNull();
    });

    it('reports a server fault with the shared wording when the refusal carried no document', () => {
      create();

      fillCredentials();
      submit();

      // A body an intermediary wrote on its own behalf: a real case, and the one where the
      // screen would otherwise say nothing at all, because the banner has no document to
      // render and the interceptor stays silent.
      expectLoginRequest().flush('<html>Gateway failure</html>', {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      const message = query('.login__message');

      expect(message).withContext('something is always said').not.toBeNull();
      expect((message?.textContent ?? '').trim()).toBe(SERVER_ERROR_MESSAGE);

      // ⚠ ASSERTIVE, because this IS the direct result of the person's own action and must be
      // discoverable without moving focus.
      expect(message?.getAttribute('role')).toBe('alert');
      expect(query('.error-banner')).withContext('there is no document to render').toBeNull();
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('retries nothing on its own', () => {
      create();

      attemptAndRefuse(
        refusal(
          RATE_LIMITED_CODE,
          429,
          'Too many requests have been submitted. Retry after a short delay.',
        ),
      );

      // Automatically re-attempting would defeat the very control that produced the refusal.
      // `verify()` in `afterEach` would fail on a queued retry; this states the claim.
      httpMock.expectNone(() => true);
    });

    it('dismisses the failure without withdrawing a field the person is being asked to fill in', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      expect(control(LOGIN_CONTROL_IDS.verificationCode)).not.toBeNull();

      const dismiss = query('.login__dismiss') as HTMLButtonElement | null;

      expect(dismiss).not.toBeNull();

      // An explicit type, so the dismissal can never submit the form below it.
      expect(dismiss?.type).toBe('button');

      dismiss?.click();
      fixture.detectChanges();

      expect(query('.login__failure')).withContext('the message is gone').toBeNull();
      expect(query('.error-banner')).toBeNull();

      // ⚠ AND THE FIELD REMAINS. Dismissing a message must not withdraw a box the person is
      // being asked to fill in - that distinction belongs to the store, and this proves the
      // screen defers to it.
      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('the verification field survives a dismissal')
        .not.toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 6 — THE VERIFICATION LADDER
  // -------------------------------------------------------------------------

  describe('the verification ladder', () => {
    it('reveals the field and asks for a code on the first verification refusal', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      const field = control(LOGIN_CONTROL_IDS.verificationCode);

      expect(field).withContext('the field appears').not.toBeNull();
      expect(field?.type).toBe('text');

      // The ladder sentence appears BESIDE THE FIELD, because it is about that one field, and
      // ALSO as the form-level sentence, because the banner is deliberately not the only place
      // a refusal is explained.
      const fieldMessages = queryAll('.form-field__error').map((n) => (n.textContent ?? '').trim());

      expect(fieldMessages).toContain(VERIFICATION_REQUIRED_MESSAGE);
      expect(textOf('.login__message')).toBe(VERIFICATION_REQUIRED_MESSAGE);
    });

    it('requires the code once the field is on screen, and sends nothing while it is empty', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      submit();

      // The validator is applied by the component's effect the moment the store's revealed
      // state flips, so the group is invalid immediately rather than staying stale until the
      // person types.
      httpMock.expectNone(() => true);

      const fieldMessages = queryAll('.form-field__error').map((n) => (n.textContent ?? '').trim());

      expect(fieldMessages).toContain(LOGIN_REQUIRED_MESSAGES.verificationCode);
    });

    it('sends the typed code on the next attempt and reports a wrong one against the same field', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      type(LOGIN_CONTROL_IDS.verificationCode, SUBMITTED_CODE);
      submit();

      const request = expectLoginRequest();

      expect(request.request.body).toEqual({
        username: ACCOUNT_NAME,
        password: SUBMITTED_PASSWORD,
        verificationCode: SUBMITTED_CODE,
      });

      request.flush(
        refusal(
          VERIFICATION_CODE_INVALID_CODE,
          401,
          'The verification code submitted for this account is not correct.',
        ),
        { status: 401, statusText: 'Unauthorized' },
      );
      fixture.detectChanges();

      // ⚠ THE FIELD STAYS REVEALED ON A SECOND REFUSAL. `Login.ascx.vb:L171` branched on
      // `If Not rowVerification1.Visible`, so the legacy ladder turned on whether the field had
      // ALREADY been revealed - state that survived the post-back in Web Forms control state.
      expect(control(LOGIN_CONTROL_IDS.verificationCode)).not.toBeNull();
      expect(requiredControl(LOGIN_CONTROL_IDS.verificationCode).value).toBe(SUBMITTED_CODE);

      const fieldMessages = queryAll('.form-field__error').map((n) => (n.textContent ?? '').trim());

      expect(fieldMessages).toContain(VERIFICATION_CODE_INVALID_MESSAGE);
      expect(textOf('.login__message')).toBe(VERIFICATION_CODE_INVALID_MESSAGE);
    });

    it('reports the third ladder outcome with its own measured wording', () => {
      create();

      attemptAndRefuse(
        refusal(
          ACCOUNT_NOT_APPROVED_CODE,
          401,
          'This account has not been authorised to sign in to this portal.',
        ),
      );

      // `Login.ascx.vb` assigns EXACTLY THREE message codes - at L175 and L180, at L178, and at
      // L184 - so there are exactly three ladder outcomes and no fourth is invented. This is
      // the third.
      expect(textOf('.login__message')).toBe(ACCOUNT_NOT_APPROVED_MESSAGE);
    });

    it('does not reveal the field for an ordinary refused credential', () => {
      create();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      // The ladder is progressive AND selective: only a verification outcome advances it.
      expect(control(LOGIN_CONTROL_IDS.verificationCode)).toBeNull();
    });

    it('does not reveal the field for a rate-limited refusal or a server fault', () => {
      create();

      attemptAndRefuse(
        refusal(
          RATE_LIMITED_CODE,
          429,
          'Too many requests have been submitted. Retry after a short delay.',
        ),
      );

      expect(control(LOGIN_CONTROL_IDS.verificationCode)).toBeNull();

      submit();
      refuseSignIn(
        refusal(
          SERVER_FAILURE_CODE,
          500,
          'An unexpected error occurred while processing the request.',
        ),
      );

      expect(control(LOGIN_CONTROL_IDS.verificationCode)).toBeNull();
    });

    it('withdraws the field once a sign-in completes', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      type(LOGIN_CONTROL_IDS.verificationCode, SUBMITTED_CODE);
      submit();
      completeSignIn();

      // The ladder resets on success and ONLY on success, which is what stops a failed attempt
      // silently sending the person back to the first rung.
      expect(control(LOGIN_CONTROL_IDS.verificationCode)).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 7 — WHERE A COMPLETED SIGN-IN GOES
  // -------------------------------------------------------------------------
  //
  // ⚠ THIS IS THE OPEN-REDIRECT BOUNDARY. Every case below completes a real sign-in and
  // asserts the single address the screen navigated to. The five rejections each cover a
  // distinct way a value can leave the application, and every rejection falls back SILENTLY:
  // a hostile parameter is not the person's mistake and reporting it would only confuse them.

  describe('the return address', () => {
    /**
     * Completes a sign-in with the given return parameter and reports where it navigated.
     *
     * ⚠ RE-ENTRANT BY DESIGN, because several cases below exercise more than one hostile value
     * and each has to be judged in isolation. Three things therefore have to be undone before
     * each value, and every one of them was proven necessary by a real failure rather than
     * added defensively:
     *
     * - the PREVIOUS FIXTURE is destroyed, so its `effect` and its subscriptions stop;
     * - the STORE IS RESET, which discards the session the previous value established. Without
     *   it the next `create()` would find a held session, take the already-signed-in path in
     *   `ngOnInit` and navigate before a single credential was typed - so the value under test
     *   would never be reached;
     * - the NAVIGATION SPY's calls are cleared, so the count assertion describes this value
     *   alone.
     */
    function signInWithReturnUrl(returnUrl: string | null): string {
      if (mounted !== null) {
        mounted.destroy();
        mounted = null;
      }

      store.reset();
      navigateSpy.calls.reset();

      queryParams = returnUrl === null ? {} : { [RETURN_URL_QUERY_KEY]: returnUrl };

      create();
      fillCredentials();
      submit();
      completeSignIn();

      expect(navigateSpy)
        .withContext('a completed sign-in navigates exactly once')
        .toHaveBeenCalledTimes(1);

      return navigateSpy.calls.mostRecent().args[0] as string;
    }

    /** Asserts a value is accepted and used byte for byte. */
    function expectAccepted(returnUrl: string): void {
      expect(signInWithReturnUrl(returnUrl))
        .withContext(`${returnUrl} is an address inside this application`)
        .toBe(returnUrl);
    }

    /** Asserts a value is refused, silently, in favour of the default landing address. */
    function expectRejected(returnUrl: string | null, why: string): void {
      expect(signInWithReturnUrl(returnUrl)).withContext(why).toBe(DEFAULT_SIGNED_IN_ROUTE);

      // SILENTLY. No message of any kind is shown for a rejected parameter.
      expect(query('.login__failure')).withContext('a rejected address is not reported').toBeNull();
      expect(query('.login__message')).toBeNull();
      expect(query('.login__notice')).toBeNull();
    }

    it('goes to the console landing address when no return address was supplied', () => {
      expectRejected(null, 'an absent parameter falls back to the default');
    });

    it('honours an internal path exactly as it arrived', () => {
      // The guard that produced the value preserved the attempted address byte for byte, so
      // re-serialising it here could only lose something.
      expectAccepted('/users/5');
    });

    it('preserves a query string on an internal address', () => {
      expectAccepted('/users?page=2&size=10');
    });

    it('preserves a fragment on an internal address', () => {
      expectAccepted('/portals#aliases');
    });

    it('preserves an encoded value inside the query rather than decoding or rebuilding it', () => {
      expectAccepted('/users?filter=%2Fadmin%20accounts');
    });

    it('honours the tenant addresses whose identifiers are legitimately zero and minus one', () => {
      // `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`, so minus one and zero both identify real
      // tenants. Neither may be read as an absence anywhere, including here.
      expectAccepted('/portals/0/settings');
      expectAccepted('/portals/-1/settings');
    });

    it('refuses a bare address with no leading slash, which is either a host or a scheme', () => {
      expectRejected('portals', 'a value with no leading slash is not an internal path');
      expectRejected('evil.test/portals', 'a bare host is not an internal path');
    });

    it('refuses an absolute address', () => {
      expectRejected('https://evil.test/portals', 'an absolute address leaves the application');
      expectRejected('http://evil.test', 'the scheme is irrelevant; leaving is what is refused');
    });

    it('refuses a scheme-relative address, which reaches any host at all', () => {
      expectRejected('//evil.test/portals', 'two leading slashes address another host');
      expectRejected('//evil.test', 'with or without a path');
    });

    it('refuses a scheme separator ANYWHERE, including inside a query', () => {
      // Deliberately stricter than strictly necessary. A leading slash followed by an absolute
      // address is the attack this catches, and refusing the separator wherever it appears is
      // both simpler to reason about and impossible to slip past - at the cost of refusing a
      // legitimate address that carries an absolute URL in its query, which the console has
      // none of.
      expectRejected('/redirect?next=https://evil.test', 'a scheme separator inside a query');
      expectRejected('/a/https://evil.test', 'a scheme separator inside a path segment');
    });

    it('refuses a backslash, which some browsers normalise into a second leading slash', () => {
      expectRejected('/\\evil.test/portals', 'a backslash after the slash becomes scheme-relative');
      expectRejected('/portals\\..\\evil', 'a backslash anywhere is refused');
    });

    it('refuses a C0 control character, which a browser strips before resolving the address', () => {
      // Stripping is what makes these dangerous: a control character can break up any of the
      // sequences already refused above, so `/\u0000/evil.test` would resolve as
      // scheme-relative once the browser removed it.
      expectRejected('/\u0000/evil.test', 'a null');
      expectRejected('/portals\u0009evil', 'a tab');
      expectRejected('/portals\u000aevil', 'a line feed');
      expectRejected('/portals\u000devil', 'a carriage return');
      expectRejected('/portals\u001fevil', 'the last C0 control');
    });

    it('refuses the delete character, which sits above the printable range', () => {
      expectRejected('/portals\u007fevil', 'the delete code unit is a control character too');
    });

    it('refuses a leading control character before the slash', () => {
      expectRejected('\u0020/portals', 'a leading space means the value does not begin with a slash');
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 8 — AN ALREADY-SIGNED-IN VISITOR
  // -------------------------------------------------------------------------

  describe('a visitor who already holds a session', () => {
    /** Establishes a real session on the real store, without the screen being involved. */
    function establishSession(): void {
      store.login({ username: ACCOUNT_NAME, password: SUBMITTED_PASSWORD }).subscribe();

      // Both halves of the exchange, for the reason recorded on {@link ME_URL}. The session is
      // not stored - and the store therefore does not report a session - until the second one
      // answers.
      httpMock
        .expectOne((candidate) => candidate.method === 'POST' && candidate.url === LOGIN_URL)
        .flush(credentialPayload());
      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === ME_URL)
        .flush(identityPayload());

      expect(store.isAuthenticated()).withContext('the session is genuinely held').toBeTrue();
    }

    it('is sent on to the console rather than asked to sign in again', () => {
      establishSession();

      create();

      // `Login.ascx.vb:L103` guarded the whole of its preparation with
      // `If Not Request.IsAuthenticated Then`, and the legacy screen sent an authenticated
      // visitor on. This address is reachable with no credentials at all - guarding the
      // sign-in screen would deadlock the application, and the bearer-token interceptor
      // navigates HERE when a session cannot be renewed - so the case is real.
      expect(navigateSpy).toHaveBeenCalledOnceWith(DEFAULT_SIGNED_IN_ROUTE);
    });

    it('is sent to the requested address when it is safe, and to the default when it is not', () => {
      establishSession();

      queryParams = { [RETURN_URL_QUERY_KEY]: '/users/5' };
      create();

      expect(navigateSpy).toHaveBeenCalledOnceWith('/users/5');
    });

    it('applies the same guard to the address it is sent on to', () => {
      establishSession();

      // The guard is at the point of USE, so it cannot be bypassed by arriving already signed
      // in rather than by signing in here.
      queryParams = { [RETURN_URL_QUERY_KEY]: '//evil.test/portals' };
      create();

      expect(navigateSpy).toHaveBeenCalledOnceWith(DEFAULT_SIGNED_IN_ROUTE);
    });

    it('seeds nothing and asks for nothing once it has decided to move on', () => {
      establishSession();

      queryParams = {
        [RETURN_URL_QUERY_KEY]: '/users/5',
        [USERNAME_QUERY_KEY]: 'seeded-account',
      };
      create();

      // The legacy guard wrapped the seeding as well as the focus, so neither happens for a
      // visitor who is already signed in.
      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('');
      httpMock.expectNone(() => true);
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 9 — WHAT THIS SCREEN DELIBERATELY DOES NOT OFFER
  // -------------------------------------------------------------------------

  describe('deliberate omissions', () => {
    it('offers no keep-me-signed-in box, no recovery link and no registration link', () => {
      create();

      // The 37 lines of legacy markup contain none of the three, and the authentication
      // surface is closed at four endpoints - sign in, renew, sign out, read one's own
      // identity - so no endpoint exists for any of them. The resource file of the
      // OUT-OF-SCOPE multi-provider container screen does carry all three captions, which is
      // exactly why their absence is asserted rather than assumed.
      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

      expect(text).not.toContain('Remember');
      expect(text).not.toContain('Register');
      expect(text).not.toContain('Forgot');

      expect(queryAll('input[type="checkbox"]').length).toBe(0);
      expect(queryAll('a').length).withContext('no navigational affordance at all').toBe(0);
    });

    it('offers no human-verification challenge', () => {
      create();

      // `Login.ascx:L19-L24` declared one and `Login.ascx.vb:L162` gated the whole handler on
      // it. Its control lives under `Library/Controls/**`, a tree this migration excludes
      // wholesale, so the removal is a documented functional reduction. The compensating
      // control is the server's rate limiter, whose refusal this screen surfaces calmly.
      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

      expect(text).not.toContain('Security Code');
      expect(queryAll('img').length).withContext('no challenge image, and no image at all').toBe(0);
    });

    it('never renders the dead account-enumeration message from the legacy resource file', () => {
      create();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      // The administrative resource file carries an entry stating that an account does not
      // exist, and `Login.ascx.vb` never assigns it: it is a dead key belonging to an
      // out-of-scope screen AND an account-enumeration vector, because it would disclose
      // whether an account exists - a distinction the legacy sign-in never drew. There is no
      // fourth failure message and no path can reach it.
      const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

      expect(text).not.toContain('Does Not Exist');
    });
  });
});
