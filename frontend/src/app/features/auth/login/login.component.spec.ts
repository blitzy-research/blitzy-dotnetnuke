import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import type { TestRequest } from '@angular/common/http/testing';
import type { ComponentFixture } from '@angular/core/testing';

import type { CurrentUser, LoginResponse } from '../../../core/models/auth.model';
import type {
  ProblemDetails,
  ValidationProblemDetails,
} from '../../../core/models/problem-details.model';
// A root-provided service, listed here for ONE reason: its single sink is spied so that "this screen
// announces nothing globally" is an assertion rather than a claim. It is never substituted - the spy calls
// through - because replacing it would prove nothing about the real one.
import { NotificationService } from '../../../core/services/notification.service';
import { REVOCATION_FAILED_MESSAGE, AuthStore } from '../../../core/state/auth.store';
import {
  TOO_MANY_ATTEMPTS,
  authFailureMessage,
  isValidationProblemDetails,
  statusMessage,
} from '../../../core/utils/form-errors.util';
import {
  DEFAULT_SIGNED_IN_ROUTE,
  LOGIN_BOUND_MESSAGES,
  LOGIN_CONTROL_IDS,
  LOGIN_PASSWORD_MAX_BYTES,
  LOGIN_REQUIRED_MESSAGES,
  LOGIN_USERNAME_MAX_LENGTH,
  PORTAL_ID_QUERY_KEY,
  RETURN_URL_QUERY_KEY,
  SIGNED_DECIMAL_INTEGER,
  USERNAME_QUERY_KEY,
  VERIFICATION_CODE_QUERY_KEY,
  LoginComponent,
} from './login.component';

// ADDRESSES
// Hand-written relative literals rather than values composed from the endpoint table. A literal is what
// catches a change to that table, whereas a composed address would move with it and assert nothing.

/** `POST /api/v1/auth/login`. The credential exchange. */
const LOGIN_URL = '/api/v1/auth/login';

/**
 * `GET /api/v1/auth/me`. The identity read that COMPLETES a sign-in. ⚠ A COMPLETED SIGN-IN IS TWO
 * REQUESTS, NOT ONE, and every case below has to answer both or the sign-in never completes at all.
 */
const ME_URL = '/api/v1/auth/me';

/** `POST /api/v1/auth/logout`. The withdrawal of the renewal credential. */
const LOGOUT_URL = '/api/v1/auth/logout';

// THE FAILURE VOCABULARY

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

/** A request the server's own validators refused. 400. */
const REQUEST_INVALID_CODE = 'request.invalid';

/** An unexpected server fault. 500. */
const SERVER_FAILURE_CODE = 'server.unexpected_failure';

/** The media type every refusal below is flushed with. */
const PROBLEM_MEDIA_TYPE = 'application/problem+json';

/**
 * The model-state keys the server reports the two credential fields under. ⚠ PASCAL-CASED, AND
 * DELIBERATELY NOT CAMEL-CASED. These name model members on the server rather than members of the
 * serialised body, so the serialiser's camel-casing policy does not reach them.
 */
const SERVER_USERNAME_KEY = 'UserName';
const SERVER_PASSWORD_KEY = 'Password';

/**
 * The three ladder sentences, verbatim from
 * `Website/admin/Authentication/App_LocalResources/Login.ascx.resx` L162, L165 and L222, as the shared
 * form-errors utility reproduces them.
 */
const VERIFICATION_REQUIRED_MESSAGE = 'Enter Your Verification Code';
const VERIFICATION_CODE_INVALID_MESSAGE = 'Invalid Verification Code';
const ACCOUNT_NOT_APPROVED_MESSAGE = 'You are not currently authorized to login to this site.';

/** The calm sentence the shared utility publishes for a rate-limited refusal. */
const RATE_LIMIT_MESSAGE = 'Too many attempts. Wait a moment and try again.';

/** The shared utility's wording for any status at or above 500. */
const SERVER_ERROR_MESSAGE = 'The server could not complete the request. Try again shortly.';

/**
 * The per-status title from the server's own vocabulary table. The title describes the CLASS of failure
 * and is derived from the status alone, which is exactly why nothing on this screen may branch on it -
 * the four 401 outcomes below all share one title and are told apart only by their `type`.
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

// CREDENTIALS AND TOKENS USED IN FIXTURES
// Every one of them is obviously fake and says so in its own value. None is a credential, none resembles a
// real token, and the last group exists so that "no credential reaches the document" can be asserted
// against a value that would be unmistakable if it did.

const FAKE_ACCESS_TOKEN = 'fake-access-token-not-a-real-credential';
const FAKE_RENEWAL_TOKEN = 'fake-renewal-token-not-a-real-credential';
const EXPIRES_AT_UTC = '2100-01-01T00:00:00.000Z';

/** An account name with deliberate mixed case, so case preservation is observable. */
const ACCOUNT_NAME = 'Admin';

/** A password with deliberate leading and trailing space, so trimming would be observable. */
const SUBMITTED_PASSWORD = ' not-a-real-password ';

/** A verification code, for the ladder's second rung. */
const SUBMITTED_CODE = 'wrong-code';

/**
 * A six-character value, one character short of the legacy CREATION policy's minimum. Named for its
 * LENGTH because its length is the only property under test: the case that uses it proves a
 * minimum-length rule has not crept onto the sign-in form, where it would lock out every account whose
 * credential predates the policy.
 */
const SIX_CHARACTER_VALUE = 'sixchr';

/**
 * The minimum length the legacy membership provider required, measured at `Website/release.config:L242`
 * (`minRequiredPasswordLength="7"`). ⚠ IT GOVERNS CREATING AND CHANGING A CREDENTIAL, NEVER SIGNING IN
 * WITH ONE, and it is declared here only so the case below can state what it is one character short of.
 * Nothing on this screen enforces it, deliberately.
 */
const LEGACY_CREATION_MINIMUM_LENGTH = 7;

// THE RESPONSE ENVELOPE

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
 * A credential-exchange payload, wrapped. All three advisories are stated as `false` rather than omitted,
 * because `false` is DATA on this contract and an omission would not compile.
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
 * A problem document carrying a code, in the exact shape the API emits. ⚠ NO `instance` MEMBER, AND ITS
 * ABSENCE IS MEASURED. Every call site supplies null for it, and although the surrounding serializer
 * policy writes members holding null, the framework's problem type carries a per-member null-omission
 * condition on each of its five standard members which overrides that policy - so a live document has no
 * `instance` at all. ⚠ `type` IS ALWAYS PRESENT. The factory fills an unspecified type from the status
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

/**
 * A field-level refusal, in the shape the validation factory emits. ⚠ THE ENVELOPE IS CLOSED AT FIVE
 * MEMBERS - the type, the title, the status, the detail and the error map - plus the two identifiers the
 * factory attaches.
 *
 * @param detail The form-level sentence.
 * @param errors The per-field messages, keyed as the server keys them.
 * @returns The document.
 */
function validationRefusal(
  detail: string,
  errors: Readonly<Record<string, readonly string[]>>,
): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${REQUEST_INVALID_CODE}`,
    title: STATUS_TITLE[400] ?? 'Error',
    status: 400,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
    errors,
  };
}

describe('LoginComponent', () => {
  /**
   * The screen under test. Created by a case rather than in a shared `beforeEach`, because the
   * activated-route double has to be seeded BEFORE construction: the component reads the route snapshot
   * exactly once.
   */
  let fixture: ComponentFixture<LoginComponent>;

  /** The same fixture, or null when nothing has been created yet. */
  let mounted: ComponentFixture<LoginComponent> | null;
  let httpMock: HttpTestingController;
  let store: AuthStore;
  let navigateSpy: jasmine.Spy;

  /**
   * The global announcement channel's single sink, spied. ⚠ THE REAL ROOT SERVICE, CALLED THROUGH - not a
   * double. Substituting it would prove something about the substitute; spying on the genuine one proves
   * that nothing on this screen's failure path reaches it.
   */
  let notifySpy: jasmine.Spy;

  /**
   * The router's command-array navigation, spied. ⚠ SPIED FOR A REASON THAT WAS DISCOVERED RATHER THAN
   * ASSUMED, and it is worth recording because the alternative fails in a way that looks like a component
   * defect.
   */
  let relativeNavigateSpy: jasmine.Spy;

  /**
   * The query parameters the activated-route double will report. Mutated by a case BEFORE the component
   * is created, because the component reads the SNAPSHOT exactly once - which is the direct analogue of
   * the legacy `If Page.IsPostBack = False Then` guard at `Login.ascx.vb:L104`.
   */
  let queryParams: Record<string, string>;

  beforeEach(async () => {
    queryParams = {};
    mounted = null;

    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it. Reversing
    // the two leaves the live backend in place and every expectation times out against a request nothing
    // intercepted.
    await TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          // A getter rather than a fixed value, so a case can seed `queryParams` after the module is
          // configured and before the component is created. The map itself is the router's own conversion,
          // so it is a genuine `ParamMap`.
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
    const router = TestBed.inject(Router);

    navigateSpy = spyOn(router, 'navigateByUrl').and.resolveTo(true);
    relativeNavigateSpy = spyOn(router, 'navigate').and.resolveTo(true);

    // Called through rather than stubbed, so the genuine service still behaves exactly as it
    // would in the application and the spy only observes.
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    // Destroyed BEFORE the transport is verified, so a subscription released by teardown
    // cannot be mistaken for a request the screen never issued.
    if (mounted !== null) {
      mounted.destroy();
      mounted = null;
    }

    // The house rule. It fails a case that left a request unconsumed just as loudly as one that issued a
    // request nobody expected - and on this screen an unexpected request would mean a credential was sent
    // somewhere no assertion looked.
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

  /** The screen's own host element, by ASSIGNMENT rather than by cast. */
  function host(): HTMLElement {
    const element: HTMLElement = fixture.nativeElement;

    return element;
  }

  /** The rendered element for a selector, or null. */
  function query(selector: string): HTMLElement | null {
    return host().querySelector<HTMLElement>(selector);
  }

  /**
   * The rendered element for a selector, or a failure naming what was missing. ⚠ NARROWED, NEVER
   * ASSERTED. A document query answers with an element or null, and several of this screen's elements
   * genuinely may not be rendered - the verification field and the whole failure region are both behind
   * template conditions.
   *
   * @param selector The selector to find.
   * @returns The element.
   */
  function queryOrFail<T extends HTMLElement>(selector: string): T {
    const found = host().querySelector<T>(selector);

    if (found === null) {
      throw new Error(`Expected to find "${selector}" in the rendered screen`);
    }

    return found;
  }

  /** Every rendered element for a selector, in document order. */
  function queryAll(selector: string): readonly HTMLElement[] {
    return Array.from(host().querySelectorAll<HTMLElement>(selector));
  }

  /** The collapsed text of a selector, or null when it is not rendered. */
  function textOf(selector: string): string | null {
    const element = query(selector);

    return element === null ? null : (element.textContent ?? '').trim();
  }

  /** One of this screen's three controls, by its declared identifier, or null when absent. */
  function control(controlId: string): HTMLInputElement | null {
    return host().querySelector<HTMLInputElement>(`#${controlId}`);
  }

  /** One of the three controls, narrowed rather than asserted. */
  function requiredControl(controlId: string): HTMLInputElement {
    return queryOrFail<HTMLInputElement>(`#${controlId}`);
  }

  /** Types a value into a control the way a person does. */
  function type(controlId: string, value: string): void {
    const element = requiredControl(controlId);

    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** The submit button. */
  function submitButton(): HTMLButtonElement {
    return queryOrFail<HTMLButtonElement>('.login__submit');
  }

  function submit(): void {
    submitButton().click();
    fixture.detectChanges();
  }

  function submitThroughForm(): void {
    queryOrFail('.login__form').dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  /**
   * The screen's caller-supplied controls and its submit control, in document order. ⚠ SCOPED TO THE
   * BOXES AND THE SUBMIT CONTROL DELIBERATELY. A blanket query for every focusable element would also
   * collect each shared field's own help toggle, which belongs to that component and would make this a
   * specification of the design system rather than of this screen's field order.
   *
   * @returns Each control's identifier, with the submit control named for what it is.
   */
  function controlSequence(): readonly string[] {
    return queryAll('input, .login__submit').map((element) =>
      element.id.length > 0 ? element.id : 'submit',
    );
  }

  /** Fills both credential fields with the fixture values. */
  function fillCredentials(): void {
    type(LOGIN_CONTROL_IDS.username, ACCOUNT_NAME);
    type(LOGIN_CONTROL_IDS.password, SUBMITTED_PASSWORD);
  }

  /**
   * Every sign-in request issued so far, taken as a SET so it can be counted. ⚠ COUNTED RATHER THAN
   * INFERRED, because "exactly one" is the whole claim in two places: a refused attempt must not be
   * retried automatically, and a second submission arriving while one is in flight must not become a
   * second request.
   *
   * @returns The matched requests, in the order they were issued.
   */
  function matchLoginRequests(): readonly TestRequest[] {
    return httpMock.match(
      (candidate) => candidate.method === 'POST' && candidate.url === LOGIN_URL,
    );
  }

  /**
   * Asserts that exactly one sign-in request was issued, and returns it.
   *
   * @param context What the count is being claimed about.
   * @returns The single request, so the caller can answer it.
   */
  function expectExactlyOneLoginRequest(context: string): TestRequest {
    const issued = matchLoginRequests();

    expect(issued.length).withContext(context).toBe(1);

    const only = issued[0];

    if (only === undefined) {
      throw new Error(`Expected exactly one sign-in request: ${context}`);
    }

    return only;
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

  /** Answers BOTH requests of a completed sign-in. */
  function completeSignIn(): void {
    expectLoginRequest().flush(credentialPayload());
    completeIdentityRead();
  }

  /** Answers the outstanding sign-in request with a refusal document. */
  function refuseSignIn(problem: ProblemDetails): void {
    const status = problem.status ?? 500;

    expectLoginRequest().flush(problem, {
      status,
      statusText: STATUS_TITLE[status] ?? 'Error',
      headers: { 'Content-Type': PROBLEM_MEDIA_TYPE },
    });
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

      expect((headings[0]?.textContent ?? '').trim()).toBe('User Log In');
    });

    it('issues no request of any kind while merely being shown', () => {
      create();

      expect(httpMock.match(() => true))
        .withContext('merely being shown reaches the network for nothing')
        .toEqual([]);
    });

    it('renders the two credential fields in the legacy order and no third field', () => {
      create();

      expect(control(LOGIN_CONTROL_IDS.username)).not.toBeNull();
      expect(control(LOGIN_CONTROL_IDS.password)).not.toBeNull();

      // ⚠ THE VERIFICATION FIELD IS ABSENT FROM THE DOCUMENT, not merely hidden. A box a person is not
      // being asked to fill in must not be reachable by Tab nor announced by a screen reader.
      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('the verification field is not in the document until the server asks for it')
        .toBeNull();
    });

    it('associates every rendered label with the control it names', () => {
      create();

      const associations = queryAll('label').map((label) => label.getAttribute('for'));

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

      // ⚠ THE TRAILING COLON IS STRIPPED BY THE SHARED FIELD, AND THAT IS THE POLICY RATHER THAN A DEFECT.
      // The template passes the measured resource values `'User Name:'` and `'Password:'` exactly as they
      // are stored, and the shared component owns the punctuation: it strips ONE trailing colon whatever
      // the colon's origin and adds none of its own.
      expect(labelText.some((text) => text.startsWith('User Name'))).toBeTrue();
      expect(labelText.some((text) => text.startsWith('Password'))).toBeTrue();
      expect(labelText.some((text) => text.includes(':')))
        .withContext('the shared field strips the trailing colon it was given')
        .toBeFalse();
    });

    it('renders the password box as a password box, the one security attribute the legacy carried', () => {
      create();

      // `Login.ascx:L29` declares `textmode="password"`. Of every attribute on that markup it is the only
      // one expressing a security property rather than a layout one, so it is the one attribute that must
      // survive exactly.
      expect(requiredControl(LOGIN_CONTROL_IDS.password).type).toBe('password');
      expect(requiredControl(LOGIN_CONTROL_IDS.username).type).toBe('text');
    });

    it('renders the submit control inside the form and typed to submit it', () => {
      create();

      const form = query('.login__form');

      expect(form).withContext('a real form element').not.toBeNull();
      expect(form?.tagName).toBe('FORM');

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

      // An `aria-invalid="false"` on every field is noise; omitting the attribute is the default state,
      // which is why the template renders it only when there is something to report.
      expect(requiredControl(LOGIN_CONTROL_IDS.username).getAttribute('aria-invalid')).toBeNull();
      expect(requiredControl(LOGIN_CONTROL_IDS.password).getAttribute('aria-invalid')).toBeNull();
    });

    it('places the caret in the account-name box, as the legacy screen did', () => {
      create();

      expect(document.activeElement?.id).toBe(LOGIN_CONTROL_IDS.username);
    });

    it('renders the two credential boxes and the submit control in that order', () => {
      create();

      // ⚠ ORDER IS BEHAVIOUR, not decoration: it is the order a person tabs through and the order a screen
      // reader announces.
      expect(controlSequence()).toEqual([
        LOGIN_CONTROL_IDS.username,
        LOGIN_CONTROL_IDS.password,
        'submit',
      ]);
    });

    it('asks the browser for the right completion behaviour on each box', () => {
      create();

      const username = requiredControl(LOGIN_CONTROL_IDS.username);
      const password = requiredControl(LOGIN_CONTROL_IDS.password);

      // MIGRATION: net additions, and two of them are load-bearing rather than cosmetic.
      expect(username.getAttribute('autocomplete')).toBe('username');
      expect(username.getAttribute('autocapitalize')).toBe('none');
      expect(username.getAttribute('spellcheck')).toBe('false');

      // The standard token for this field: it asks the BROWSER's own credential manager to fill the box.
      // Nothing in the workspace reads, copies, stores or logs the value, and no web storage of any kind is
      // touched.
      expect(password.getAttribute('autocomplete')).toBe('current-password');
    });

    it('points every label at the control it labels, inside its own field', () => {
      create();

      const fields = queryAll('app-form-field');

      expect(fields.length).withContext('one shared field per rendered control').toBe(2);

      for (const field of fields) {
        const label = field.querySelector<HTMLLabelElement>('label');
        const input = field.querySelector<HTMLInputElement>('input');

        expect(label).not.toBeNull();
        expect(input).not.toBeNull();
        expect(label?.getAttribute('for'))
          .withContext('the label names the control beside it')
          .toBe(input?.id ?? null);
      }
    });

    it('declares no rule beyond the two the server itself enforces', () => {
      create();

      const username = requiredControl(LOGIN_CONTROL_IDS.username);
      const password = requiredControl(LOGIN_CONTROL_IDS.password);

      // ⚠ NO MINIMUM LENGTH, NO PATTERN AND NO ADDRESS FORMAT ON EITHER BOX. The credential policy measured
      // at `Website/release.config:L241-L245` - minimum length seven, no required non-alphanumeric
      // character, no question and answer, no unique address governs CREATING and CHANGING a password,
      // never signing in with one.
      for (const box of [username, password]) {
        expect(box.getAttribute('minlength')).toBeNull();
        expect(box.getAttribute('pattern')).toBeNull();
        expect(box.type).not.toBe('email');
      }

      expect(username.getAttribute('maxlength')).toBe(String(LOGIN_USERNAME_MAX_LENGTH));

      // And it is NOT applied to the credential, whose ceiling is counted in UTF-8 bytes and
      // cannot be expressed as a character count at all.
      expect(password.getAttribute('maxlength')).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 2 — THE LEGACY QUERY PARAMETERS
  // -------------------------------------------------------------------------

  describe('query-parameter seeding', () => {
    it('seeds the account name, reproducing Login.ascx.vb:L106-L108', async () => {
      queryParams = { [USERNAME_QUERY_KEY]: 'seeded-account' };

      create();

      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('seeded-account');

      // ⚠ AWAITED, AND THE REASON IS MEASURED RATHER THAN DEFENSIVE. Seeding also scrubs the address, and
      // that scrub leaves a settled promise outstanding - so the render hook carrying the focus move is
      // deferred past this render rather than running inside it.
      await fixture.whenStable();

      expect(document.activeElement?.id).toBe(LOGIN_CONTROL_IDS.password);
    });

    it('seeds the verification code WITHOUT revealing the field', () => {
      queryParams = { [VERIFICATION_CODE_QUERY_KEY]: 'code-from-an-email' };

      create();

      // A DELIBERATE DIVERGENCE from `Login.ascx.vb:L109-L116`.
      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('a seeded code does not reveal the field')
        .toBeNull();
    });

    it('treats a present-but-empty parameter as present, exactly as the legacy did', () => {
      queryParams = { [USERNAME_QUERY_KEY]: '' };

      create();

      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('');
    });

    it('leaves both boxes empty when no parameter was supplied', () => {
      create();

      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('');
      expect(requiredControl(LOGIN_CONTROL_IDS.password).value).toBe('');
    });

    it('removes the seeded account name and code from the address, replacing the entry', () => {
      queryParams = {
        [USERNAME_QUERY_KEY]: 'seeded-account',
        [VERIFICATION_CODE_QUERY_KEY]: 'code-from-an-email',
        [RETURN_URL_QUERY_KEY]: '/users/5',
      };

      create();

      // ⚠ AN ADDRESS IS NOT A PRIVATE CHANNEL. A query string is kept in the browser's history, offered by
      // the address bar to whoever next uses the machine, handed to third-party origins in the referrer of
      // any later request, and is the single most commonly recorded part of a request in proxy and server
      // logs.
      expect(relativeNavigateSpy).toHaveBeenCalledTimes(1);

      const [commands, options] = relativeNavigateSpy.calls.mostRecent().args as [
        readonly unknown[],
        Readonly<Record<string, unknown>>,
      ];

      expect(commands).withContext('the address itself is unchanged').toEqual([]);

      const dropped: unknown = options['queryParams'];

      expect(dropped).toEqual({
        [USERNAME_QUERY_KEY]: null,
        [VERIFICATION_CODE_QUERY_KEY]: null,
      });

      // ⚠ MERGED, SO THE RETURN ADDRESS SURVIVES. Dropping every other parameter here would turn a privacy
      // measure into a redirect defect, because the return address is still needed after a successful
      // attempt.
      expect(options['queryParamsHandling']).toBe('merge');

      // ⚠ AND THE ENTRY IS REPLACED, NOT PUSHED. Pushing would leave the original address - material
      // and all - one press of Back away, and in session history for as long as the tab lives.
      expect(options['replaceUrl']).toBeTrue();

      // The form keeps what was seeded; only the address is cleaned.
      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('seeded-account');
    });

    it('leaves the address alone when nothing was seeded from it', () => {
      queryParams = { [RETURN_URL_QUERY_KEY]: '/users/5' };

      create();

      // Nothing sensitive arrived, so there is nothing to remove - and a navigation issued anyway
      // would replace a history entry for no reason.
      expect(relativeNavigateSpy).not.toHaveBeenCalled();
    });

    it('reads the snapshot once and never re-seeds what a person has typed', () => {
      queryParams = { [USERNAME_QUERY_KEY]: 'seeded-account' };

      create();

      type(LOGIN_CONTROL_IDS.username, 'typed-over-the-seed');

      // A later query change must not reach the form. Reading the route's OBSERVABLE instead of its
      // snapshot would re-seed here and silently discard what was typed - which is the behaviour the legacy
      // post-back guard prevented.
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

      httpMock.expectNone(() => true);

      const messages = queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());

      // MIGRATION: these two sentences are NET-NEW. All 37 lines of legacy markup declared no validator
      // control, so the legacy screen posted whatever was typed and let the server refuse it. The MECHANISM
      // changed; the BEHAVIOUR - an empty box cannot complete a sign-in - did not.
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

      // The credential policy measured in `Website/release.config:L241-L245` - minimum length seven, no
      // required non-alphanumeric character - governs CREATING and CHANGING a password, not signing in with
      // one.
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

      // Leaving the code permanently required would make the very first attempt unsubmittable, which no
      // legacy behaviour justifies: the field did not even exist on screen until the server asked for it.
      fillCredentials();
      submit();

      // ⚠ THE ASSERTION IS THE DISPATCHED BODY, NOT MERELY THAT A REQUEST HAPPENED. A form held back by a
      // validator would have produced no request at all, so the presence of one is half the claim - and the
      // EMPTY code in it is the other half: the member travels as the empty string rather than being
      // demanded of a person the server has not asked.
      const attempt = expectLoginRequest();

      expect(attempt.request.body).toEqual({
        username: ACCOUNT_NAME,
        password: SUBMITTED_PASSWORD,
        verificationCode: '',
      });

      attempt.flush(credentialPayload());
      completeIdentityRead();
    });

    it('accepts a six-character credential, one short of the creation policy', () => {
      create();

      // ⚠ SIX CHARACTERS, DELIBERATELY, because the creation policy measured at
      // `Website/release.config:L242` sets a minimum length of SEVEN. If that rule ever crept onto this
      // form, six would be refused here and every existing account whose credential predates the rule would
      // be locked out by a migration that was supposed to preserve behaviour.
      expect(SIX_CHARACTER_VALUE.length)
        .withContext('the fixture is one character short of the creation minimum')
        .toBe(LEGACY_CREATION_MINIMUM_LENGTH - 1);

      type(LOGIN_CONTROL_IDS.username, ACCOUNT_NAME);
      type(LOGIN_CONTROL_IDS.password, SIX_CHARACTER_VALUE);
      submit();

      const request = expectLoginRequest();

      expect(request.request.body).toEqual({
        username: ACCOUNT_NAME,
        password: SIX_CHARACTER_VALUE,
        verificationCode: '',
      });

      // And nothing is reported beside either box, because nothing is wrong with either.
      expect(queryAll('.form-field__error').length).toBe(0);

      request.flush(credentialPayload());
      completeIdentityRead();
    });

    it('reports the two server bounds in the server’s own words, and sends nothing', () => {
      create();

      // ⚠ THESE ARE THE ONLY TWO BOUNDS ON THE FORM, and both REPRODUCE a server rule rather than adding
      // one - the sign-in contract's own validator caps the account name, and the credential ceiling is
      // counted in UTF-8 bytes by the server.
      type(LOGIN_CONTROL_IDS.username, 'a'.repeat(LOGIN_USERNAME_MAX_LENGTH + 1));
      type(LOGIN_CONTROL_IDS.password, 'b'.repeat(LOGIN_PASSWORD_MAX_BYTES + 1));
      submit();

      httpMock.expectNone(() => true);

      const messages = queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());

      expect(messages).toContain(LOGIN_BOUND_MESSAGES.usernameTooLong);
      expect(messages).toContain(LOGIN_BOUND_MESSAGES.passwordTooLong);

      // ⚠ AND NEITHER BOX IS DESCRIBED AS EMPTY. Each carries more than one rule, so a single
      // sentence per control would report a full box as blank the moment it grew too long.
      expect(messages).not.toContain(LOGIN_REQUIRED_MESSAGES.username);
      expect(messages).not.toContain(LOGIN_REQUIRED_MESSAGES.password);
    });

    it('refuses an account name of whitespace without trimming it away', () => {
      create();

      type(LOGIN_CONTROL_IDS.username, '   ');
      type(LOGIN_CONTROL_IDS.password, SUBMITTED_PASSWORD);
      submit();

      httpMock.expectNone(() => true);

      const messages = queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());

      expect(messages).toContain(LOGIN_REQUIRED_MESSAGES.username);

      // The box still holds exactly what was typed. Nothing rewrote it.
      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).toBe('   ');
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

      expect(request.request.body).toEqual({
        username: ACCOUNT_NAME,
        password: SUBMITTED_PASSWORD,
        verificationCode: '',
      });

      request.flush(credentialPayload());
      completeIdentityRead();
    });

    it('transmits an empty verification code as the empty string, present and untouched', () => {
      create();

      fillCredentials();
      submit();

      const request = expectLoginRequest();
      const body: unknown = request.request.body;

      // Typed as unknown and narrowed, because a body is whatever was serialised and claiming otherwise
      // would be a claim the runtime does not honour.
      expect(body).not.toBeNull();
      expect(typeof body).toBe('object');

      if (body === null || typeof body !== 'object') {
        throw new Error('the sign-in body is an object');
      }

      const members: Readonly<Record<string, unknown>> = { ...body };

      // Present, and the empty STRING - not null, not undefined, not absent.
      expect('verificationCode' in members).toBeTrue();
      expect(members['verificationCode']).toBe('');
      expect(members['verificationCode']).not.toBeNull();
      expect(members['verificationCode']).not.toBeUndefined();

      // ⚠ AND THERE IS NO FOURTH MEMBER. The legacy call took EIGHT arguments; five have no counterpart.
      expect(Object.keys(members).sort()).toEqual(['password', 'username', 'verificationCode']);

      // ⚠ AND THE CODE IS NOT A QUERY PARAMETER EITHER. It is a member of the request, and the
      // only query parameter this endpoint takes is the tenant selector.
      expect(request.request.params.has('verificationCode')).toBeFalse();
      expect(request.request.params.has('portalId')).toBeFalse();

      request.flush(credentialPayload());
      completeIdentityRead();
    });

    it('is submitted by the form itself, which is how the return key reaches it', () => {
      create();

      fillCredentials();

      submitThroughForm();

      const request = expectExactlyOneLoginRequest(
        'the native submission issues exactly one request',
      );

      request.flush(credentialPayload());
      completeIdentityRead();

      // And it completes, so the native path is wired end to end rather than merely reaching
      // the network.
      expect(navigateSpy).toHaveBeenCalledOnceWith(DEFAULT_SIGNED_IN_ROUTE, { replaceUrl: true });
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

      // ⚠ STILL IN FLIGHT. The exchange has answered but the identity read has not, and the sign-in is not
      // complete until it does - so the progress indicator must NOT come down here.
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

      // The disabled attribute is a COURTESY, not the lock: a resubmission can still arrive by return key
      // or by script, so the component refuses it in code as well. The submission is driven through the
      // form here rather than the button precisely so the disabled attribute cannot be what prevents it.
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

      // ⚠ `Login.ascx.vb:L123` IS NOT CARRIED FORWARD. It read `txtPassword.Attributes.Add("value",
      // txtPassword.Text)`, deliberately writing the submitted credential into an HTML attribute so the box
      // survived a post-back.
      expect(password.value).toBe(SUBMITTED_PASSWORD);
      expect(password.getAttribute('value'))
        .withContext('the credential is not written into an attribute')
        .toBeNull();

      // And nowhere else in the rendered document either.
      expect(host().outerHTML).not.toContain(
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

      // The shared banner is bound unconditionally and renders nothing for a null document, so its
      // INNER region appearing is what proves a document reached it.
      expect(query('app-error-banner')).withContext('the banner is on the screen').not.toBeNull();
      expect(query('.error-banner')).withContext('and it has a document to render').not.toBeNull();

      // ⚠ ANNOUNCED, NOT MERELY DISPLAYED. The banner keeps its live region OUTSIDE its own condition, so
      // the region exists before the failure does and the insertion is what the reader hears - a region
      // that appeared together with its content would announce nothing on some readers.
      expect(queryOrFail('.error-banner-live').getAttribute('role')).toBe('alert');

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

    it('renders the refusal INLINE and announces nothing globally', () => {
      create();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      // ⚠ THIS IS THE CASE THAT PROTECTS THE WHOLE ARRANGEMENT. The bearer-token interceptor lists the
      // sign-in endpoint among the anonymous ones, so no credential is attached and no renewal is attempted
      // - a 401 here is a wrong password, not a lapsed token.
      expect(query('.login__failure')).withContext('the refusal is rendered here').not.toBeNull();

      // ⚠ AND THIS SCREEN RAISES NOTHING ITSELF. On a 401 the interceptor is silent, so a notification here
      // would be the second report of one refusal; on the statuses where the interceptor DOES announce -
      // 403, 404, 409, 422, 429 and server faults - it would be the third.
      expect(notifySpy).withContext('no global announcement from this screen').not.toHaveBeenCalled();
    });

    it('renders every message the server reported for one field, unreduced', () => {
      create();

      fillCredentials();
      submit();

      // ⚠ THE KEYS ARE PASCAL-CASED AND ARE READ WITH BRACKETS. They name model members on the server
      // rather than members of the serialised body, so the camel-casing policy does not reach them - and
      // `errors` is an index-signature type, so bracket access is the only form the compiler allows.
      const document_ = validationRefusal('The request was refused.', {
        [SERVER_USERNAME_KEY]: ['The account name is not recognised.'],
        [SERVER_PASSWORD_KEY]: ['The credential is not correct.', 'Passwords are case sensitive.'],
      });

      // Narrowed by the shared guard rather than trusted, so the fixture is proven to BE the
      // validation shape rather than merely resembling it.
      expect(isValidationProblemDetails(document_))
        .withContext('the fixture is a genuine field-level refusal')
        .toBeTrue();

      expect(document_.errors[SERVER_PASSWORD_KEY]?.length)
        .withContext('two messages on one field, which is the case that matters')
        .toBe(2);

      refuseSignIn(document_);

      const messages = queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());

      // D-S4: the shared field's message input accepts a read-only ARRAY, so BOTH sentences for the
      // credential reach the screen. Reducing to one - which a folder-level expectation assumed was
      // required - would silently discard a message the person needs to read.
      expect(messages).toContain('The account name is not recognised.');
      expect(messages).toContain('The credential is not correct.');
      expect(messages).toContain('Passwords are case sensitive.');

      const rendered = host().textContent ?? '';

      for (const forbidden of ['stackTrace', 'at Object.', 'Exception', 'innerException']) {
        expect(rendered).not.toContain(forbidden);
      }

      // Still no global announcement, on a status the interceptor would not have covered either.
      expect(notifySpy).not.toHaveBeenCalled();
    });

    it('renders the support reference from the banner, which is the only component holding the document', () => {
      create();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      expect(textOf('.error-banner__trace')).toBe(`Reference: ${CORRELATION_ID}`);

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

      // ⚠ POLITE, NOT ASSERTIVE. Nothing has broken - the caller has simply attempted too often - and this
      // is the compensating control for the human-verification challenge the migration removed, so it must
      // be distinguishable from a refused credential at a glance and must not interrupt.
      expect(notice?.getAttribute('role')).toBe('status');

      // "Say that calmly and say nothing else": the error-toned sentence is suppressed.
      expect(query('.login__message')).toBeNull();

      expect(RATE_LIMIT_MESSAGE).toBe(TOO_MANY_ATTEMPTS);
      expect(RATE_LIMIT_MESSAGE).not.toBe(statusMessage(500));
      expect(authFailureMessage(VERIFICATION_REQUIRED_CODE)).not.toBe(RATE_LIMIT_MESSAGE);
    });

    it('issues exactly one request for a rate-limited refusal, and never a second on its own', () => {
      create();

      fillCredentials();
      submit();

      // ⚠ COUNTED, NOT INFERRED. An expectation for "a" request would pass on the first of two, and the
      // teardown's verify() cannot say how many there were - so the count is asserted before the refusal is
      // answered.
      const request = expectExactlyOneLoginRequest('one attempt, one request');

      request.flush(
        refusal(
          RATE_LIMITED_CODE,
          429,
          'Too many requests have been submitted. Retry after a short delay.',
        ),
        {
          status: 429,
          statusText: 'Too Many Requests',
          headers: { 'Content-Type': PROBLEM_MEDIA_TYPE },
        },
      );
      fixture.detectChanges();

      // ⚠ AND NOTHING IS RE-ATTEMPTED. There is no retry operator, no backoff and no timed resubmission
      // anywhere on this screen: automatically re-attempting would defeat the very control that produced
      // the refusal.
      expect(matchLoginRequests().length).withContext('no automatic re-attempt').toBe(0);
      httpMock.expectNone(() => true);

      // ⚠ AND THIS SCREEN STILL ANNOUNCES NOTHING. The global interceptor DOES announce on 429, so a
      // notification raised here would be the second report of one refusal.
      expect(notifySpy).not.toHaveBeenCalled();
    });

    it('reports a server fault with the shared wording when the refusal carried no document', () => {
      create();

      fillCredentials();
      submit();

      // A body an intermediary wrote on its own behalf: a real case, and the one where the screen would
      // otherwise say nothing at all, because the banner has no document to render and the interceptor
      // stays silent.
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

      // Automatically re-attempting would defeat the very control that produced the refusal. Counted rather
      // than asserted by `expectNone`, so the claim is recorded as an expectation instead of only as an
      // absence check that throws.
      expect(httpMock.match(() => true))
        .withContext('a refusal provokes no retry of any kind')
        .toEqual([]);
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

      // ⚠ AND THE FIELD REMAINS. Dismissing a message must not withdraw a box the person is being asked to
      // fill in - that distinction belongs to the store, and this proves the screen defers to it.
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

      const fieldMessages = queryAll('.form-field__error').map((n) => (n.textContent ?? '').trim());

      expect(fieldMessages).toContain(VERIFICATION_REQUIRED_MESSAGE);
      expect(textOf('.login__message')).toBe(VERIFICATION_REQUIRED_MESSAGE);

      const liveWithSentence = queryAll('[role="alert"]').filter((node) =>
        (node.textContent ?? '').includes(VERIFICATION_REQUIRED_MESSAGE),
      );

      expect(liveWithSentence.length)
        .withContext('exactly one live region announces the ladder sentence')
        .toBe(1);
      expect(liveWithSentence[0]?.classList).toContain('form-field__errors');
      expect(queryOrFail('.login__message').hasAttribute('role'))
        .withContext('the duplicated form-level line is not live on a ladder rung')
        .toBeFalse();

      expect(authFailureMessage(VERIFICATION_REQUIRED_CODE)).toBe(VERIFICATION_REQUIRED_MESSAGE);
    });

    it('says the ladder sentence twice on screen but announces it once', () => {
      create();

      attemptAndRefuse(refusal(VERIFICATION_REQUIRED_CODE, 401, 'Verification is required.'));

      const formLevel = queryOrFail<HTMLElement>('.login__message');

      expect((formLevel.textContent ?? '').trim())
        .withContext('the sentence is still on screen at form level')
        .toBe(VERIFICATION_REQUIRED_MESSAGE);
      expect(formLevel.getAttribute('role'))
        .withContext('and it is announced by nothing on this one path')
        .toBeNull();
      expect(formLevel.getAttribute('aria-hidden'))
        .withContext('hidden from assistive technology rather than removed from the page')
        .toBe('true');

      const fieldRegions = queryAll('.form-field__error')
        .map((node) => (node.textContent ?? '').trim())
        .filter((text) => text === VERIFICATION_REQUIRED_MESSAGE);

      expect(fieldRegions.length)
        .withContext('the field beside the control is the single assertive source')
        .toBe(1);
    });

    it('keeps the form-level sentence assertive when nothing echoes it at field level', () => {
      create();

      fillCredentials();
      submit();

      // A body an intermediary wrote on its own behalf, which is what produces a refusal carrying
      // no problem document.
      expectLoginRequest().flush('<html>Gateway failure</html>', {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      const formLevel = queryOrFail<HTMLElement>('.login__message');

      expect((formLevel.textContent ?? '').trim()).toBe(SERVER_ERROR_MESSAGE);
      expect(formLevel.getAttribute('role'))
        .withContext('this is the only surface saying it, so it must announce')
        .toBe('alert');
      expect(formLevel.getAttribute('aria-hidden'))
        .withContext('and it is not hidden from the technology that has to announce it')
        .toBeNull();
      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('no verification field is on screen to echo it')
        .toBeNull();
      expect(query('.error-banner'))
        .withContext('and no banner either, which is why this line has to speak')
        .toBeNull();
    });

    it('leaves the form-level sentence assertive again once the ladder stops being the reason', () => {
      // ⚠ THE PREDICATE IS RE-DERIVED FROM THE CURRENT FAILURE, NOT FROM THE FIELD BEING VISIBLE, and this
      // is the case that separates the two.
      create();

      attemptAndRefuse(refusal(VERIFICATION_REQUIRED_CODE, 401, 'Verification is required.'));

      expect(queryOrFail<HTMLElement>('.login__message').getAttribute('aria-hidden'))
        .withContext('echoed on the rung itself')
        .toBe('true');

      type(LOGIN_CONTROL_IDS.verificationCode, '123456');
      submit();
      expectLoginRequest().flush('<html>Gateway failure</html>', {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('the revealed field is deliberately still there')
        .not.toBeNull();

      const formLevel = queryOrFail<HTMLElement>('.login__message');

      expect(formLevel.getAttribute('role'))
        .withContext('but nothing echoes this sentence, so it announces again')
        .toBe('alert');
      expect(formLevel.getAttribute('aria-hidden')).toBeNull();
    });

    it('announces the revealed field rather than letting it appear silently', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      const field = queryOrFail(`#${LOGIN_CONTROL_IDS.verificationCode}`);
      const wrapper = field.closest('app-form-field');

      expect(wrapper).withContext('the revealed control is inside a shared field').not.toBeNull();

      // ⚠ A NEW FIELD THAT APPEARS SILENTLY IS A FIELD A PERSON USING A SCREEN READER NEVER LEARNS ABOUT.
      // The shared field wraps its messages in a live region, so the sentence that asks for the code is
      // ANNOUNCED as well as displayed - which costs nothing visually.
      const liveRegion = wrapper?.querySelector<HTMLElement>('.form-field__errors');

      expect(liveRegion).withContext('the field carries a live region').not.toBeNull();
      expect(liveRegion?.getAttribute('role')).toBe('alert');
      expect((liveRegion?.textContent ?? '').trim()).toContain(VERIFICATION_REQUIRED_MESSAGE);

      // And the label travels with it, so what is announced names the box it belongs to.
      expect((wrapper?.querySelector('label')?.textContent ?? '').trim()).toContain(
        'Verification Code',
      );

      // ⚠ AND FOCUS MOVES ONTO THE BOX, which is the second half of announcing it: a screen reader reads
      // the newly focused control and its label, so the person learns a field has appeared without any
      // visual change whatsoever.
      expect(document.activeElement?.id)
        .withContext('the revealed field takes focus')
        .toBe(LOGIN_CONTROL_IDS.verificationCode);
    });

    it('renders the revealed field between the account name and the credential', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      // ⚠ THE LEGACY ODDITY IS PRESERVED. The legacy rows ran account name, verification code, challenge,
      // password, submit, so the code genuinely sat BEFORE the password.
      expect(controlSequence()).toEqual([
        LOGIN_CONTROL_IDS.username,
        LOGIN_CONTROL_IDS.verificationCode,
        LOGIN_CONTROL_IDS.password,
        'submit',
      ]);
    });

    it('holds the revealed state on the shared store, where navigation cannot lose it', () => {
      create();

      // ⚠ READ-ONLY, PROVEN WITHOUT A CAST. The screen ALIASES the store's signal rather than mirroring it
      // into a field of its own, and the signal it aliases cannot be written: a template or a component
      // that could set it would be able to reveal the field without the server ever having asked for a
      // code, which is the ladder's one invariant.
      const revealed = store.verificationRequired;

      expect('set' in revealed).withContext('the ladder state cannot be assigned').toBeFalse();
      expect('update' in revealed).withContext('nor updated in place').toBeFalse();
      expect(revealed()).withContext('and it starts closed').toBeFalse();
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
        {
          status: 401,
          statusText: 'Unauthorized',
          headers: { 'Content-Type': PROBLEM_MEDIA_TYPE },
        },
      );
      fixture.detectChanges();

      expect(control(LOGIN_CONTROL_IDS.verificationCode)).not.toBeNull();
      expect(requiredControl(LOGIN_CONTROL_IDS.verificationCode).value).toBe(SUBMITTED_CODE);

      const fieldMessages = queryAll('.form-field__error').map((n) => (n.textContent ?? '').trim());

      expect(fieldMessages).toContain(VERIFICATION_CODE_INVALID_MESSAGE);
      expect(textOf('.login__message')).toBe(VERIFICATION_CODE_INVALID_MESSAGE);

      // The stand-down applies to the second rung as well, not just the first: the field is
      // still on screen and still carrying the same sentence, so the duplication is identical.
      expect(
        queryAll('[role="alert"]').filter((node) =>
          (node.textContent ?? '').includes(VERIFICATION_CODE_INVALID_MESSAGE),
        ).length,
      )
        .withContext('exactly one live region announces the second rung')
        .toBe(1);
    });

    it('keeps the form-level line assertive on the rung that reveals no field', () => {
      create();

      attemptAndRefuse(
        refusal(
          ACCOUNT_NOT_APPROVED_CODE,
          401,
          'This account has not been authorised to sign in to this portal.',
        ),
      );

      expect(control(LOGIN_CONTROL_IDS.verificationCode)).toBeNull();

      const message = queryOrFail('.login__message');

      expect((message.textContent ?? '').trim()).toBe(ACCOUNT_NOT_APPROVED_MESSAGE);
      expect(message.getAttribute('role')).toBe('alert');
    });

    it('asks again, and keeps the field, when the server repeats the request for a code', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      // What the wire CAN still carry is the server repeating its request for a code after a second
      // attempt, and this is that: the field must stay on screen and the sentence must be asked again
      // rather than replaced by a wrong-code message.
      type(LOGIN_CONTROL_IDS.verificationCode, 'a-code-the-server-has-not-approved');
      submit();

      refuseSignIn(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('the field survives a second refusal')
        .not.toBeNull();
      expect(textOf('.login__message')).toBe(VERIFICATION_REQUIRED_MESSAGE);

      // ⚠ AND THE REVEALED STATE PERSISTS ACROSS THE SCREEN ITSELF BEING DESTROYED. The signal lives on the
      // root-provided store precisely so that navigating away and back does not silently restart the ladder
      // at its first rung - a flag on the component would.
      fixture.destroy();
      mounted = null;
      create();

      expect(control(LOGIN_CONTROL_IDS.verificationCode))
        .withContext('and survives the screen being rebuilt')
        .not.toBeNull();
    });

    it('sends a whitespace-only code exactly as typed, because the legacy comparison was untrimmed', () => {
      create();

      attemptAndRefuse(
        refusal(
          VERIFICATION_REQUIRED_CODE,
          401,
          'This account is awaiting verification. Submit the verification code that was sent to it.',
        ),
      );

      type(LOGIN_CONTROL_IDS.verificationCode, ' ');
      submit();

      const request = expectExactlyOneLoginRequest('the whitespace code is submitted');

      expect(request.request.body).toEqual({
        username: ACCOUNT_NAME,
        password: SUBMITTED_PASSWORD,
        verificationCode: ' ',
      });

      request.flush(
        refusal(
          VERIFICATION_CODE_INVALID_CODE,
          401,
          'The verification code submitted for this account is not correct.',
        ),
        {
          status: 401,
          statusText: 'Unauthorized',
          headers: { 'Content-Type': PROBLEM_MEDIA_TYPE },
        },
      );
      fixture.detectChanges();

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

  // PROOF 6b — WHICH TENANT THE CREDENTIALS ARE OFFERED TO
  // ⚠ THIS IS A SECURITY BOUNDARY. The tenant selector is attacker-controlled query text and it decides
  // which portal receives the credentials, so a malformed value must yield NO selector rather than a
  // DIFFERENT one.

  describe('the tenant selector', () => {
    /**
     * Seeds the selector, completes one submission and reports what reached the wire.
     *
     * @param raw The query text, exactly as an attacker could supply it.
     * @returns The transmitted selector, or null when none was sent.
     */
    function selectorSentFor(raw: string): string | null {
      queryParams = { [PORTAL_ID_QUERY_KEY]: raw };

      create();
      fillCredentials();
      submit();

      const request = expectLoginRequest();
      const sent: string | null = request.request.params.get(PORTAL_ID_QUERY_KEY);

      request.flush(credentialPayload());
      completeIdentityRead();

      return sent;
    }

    it('accepts both identity seeds, because -1 and 0 are real tenants', () => {
      // `Portals.PortalID` is `IDENTITY(-1, 1)`, so the first tenant is -1 and the second is 0. A
      // digits-only grammar would refuse the first and a truthiness test would discard the second.
      expect(selectorSentFor('-1')).toBe('-1');
    });

    it('accepts tenant zero', () => {
      expect(selectorSentFor('0')).toBe('0');
    });

    it('accepts an ordinary positive key and a signed positive key alike', () => {
      expect(selectorSentFor('7')).toBe('7');
    });

    it('sends no selector at all when the parameter is absent', () => {
      queryParams = {};

      create();
      fillCredentials();
      submit();

      const request = expectLoginRequest();

      expect(request.request.params.has(PORTAL_ID_QUERY_KEY)).toBeFalse();

      request.flush(credentialPayload());
      completeIdentityRead();
    });

    it('omits the selector for every malformed value rather than substituting a tenant', () => {
      // Each entry is a value the previous implementation converted into a CONFIDENT selection of a real,
      // different tenant.
      const malformed: readonly string[] = [
        '1junk',
        'junk1',
        '1 2',
        '0x10',
        '0b11',
        '1e3',
        '1.9',
        '1.0',
        '  7  ',
        '7\n',
        '',
        '-',
        '+',
        '--1',
        'NaN',
        'Infinity',
        '1_0',
        '9007199254740993',
        '-9007199254740993',
        '99999999999999999999',
      ];

      let previous: ComponentFixture<LoginComponent> | null = null;

      for (const raw of malformed) {
        queryParams = { [PORTAL_ID_QUERY_KEY]: raw };

        // Rebuilt per value so each is judged in isolation: the selector is read from the ACTIVATION
        // SNAPSHOT during initialisation, so reseeding a live component would change nothing.
        previous?.destroy();
        create();
        previous = fixture;
        fillCredentials();
        submit();

        const request = expectLoginRequest();

        expect(request.request.params.has(PORTAL_ID_QUERY_KEY))
          .withContext(`"${raw}" must not select a tenant`)
          .toBeFalse();

        request.flush(credentialPayload());
        completeIdentityRead();

        httpMock.verify();
      }
    });

    it('states the accepted grammar as a whole-string, both-ends-anchored expression', () => {
      // The anchoring IS the property, so it is asserted directly rather than only through the
      // component: an unanchored expression would match the numeric part of `'1junk'`.
      expect(SIGNED_DECIMAL_INTEGER.source.startsWith('^')).toBeTrue();
      expect(SIGNED_DECIMAL_INTEGER.source.endsWith('$')).toBeTrue();
      expect(SIGNED_DECIMAL_INTEGER.global).toBeFalse();

      for (const accepted of ['0', '-1', '+1', '000', '9007199254740991']) {
        expect(SIGNED_DECIMAL_INTEGER.test(accepted)).withContext(accepted).toBeTrue();
      }

      for (const refused of ['1junk', '0x10', '1e3', '1.0', ' 1', '1 ', '', '-', '1_0']) {
        expect(SIGNED_DECIMAL_INTEGER.test(refused)).withContext(refused).toBeFalse();
      }
    });
  });

  // PROOF 7 — WHERE A COMPLETED SIGN-IN GOES

  describe('the return address', () => {
    /**
     * Completes a sign-in with the given return parameter and reports where it navigated. ⚠ RE-ENTRANT BY
     * DESIGN, because several cases below exercise more than one hostile value and each has to be judged
     * in isolation.
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

    describe('an address the account may not enter', () => {
      /**
       * @param returnUrl The address the gate asked to be returned to.
       * @param at What `Router.url` reports once the refusal has settled.
       * @param inFlight Whether a further navigation is already under way.
       * @returns Every address passed to `navigateByUrl`, in order.
       */
      async function signInAndBeRefused(
        returnUrl: string,
        at: string,
        inFlight: boolean,
      ): Promise<readonly string[]> {
        if (mounted !== null) {
          mounted.destroy();
          mounted = null;
        }

        store.reset();
        navigateSpy.calls.reset();

        // The FIRST navigation is refused and every later one succeeds, so the fallback's own
        // outcome cannot be mistaken for the refusal being retried.
        navigateSpy.and.returnValues(Promise.resolve(false), Promise.resolve(true));

        // The same instance the outer setup spied `navigateByUrl` on; the injector holds one.
        const routerUnderTest = TestBed.inject(Router);

        spyOnProperty(routerUnderTest, 'url', 'get').and.returnValue(at);
        spyOn(routerUnderTest, 'getCurrentNavigation').and.returnValue(
          inFlight ? ({} as ReturnType<Router['getCurrentNavigation']>) : null,
        );

        queryParams = { [RETURN_URL_QUERY_KEY]: returnUrl };

        create();
        fillCredentials();
        submit();
        completeSignIn();

        // The fallback is decided in a `then`, so the microtask queue has to drain before the second call
        // can have been made. Awaiting a resolved promise is enough and is more honest than a timer.
        await Promise.resolve();
        await Promise.resolve();

        return navigateSpy.calls.allArgs().map((args) => args[0] as string);
      }

      it('falls back to the console landing address when the gate refuses the address', async () => {
        const attempted = await signInAndBeRefused('/portals', '/login?returnUrl=%2Fportals', false);

        // Both navigations, in order: what the gate asked for, then where the operator actually belongs.
        // The second is the application root, which resolves the landing screen from the caller's own
        // authority and therefore cannot repeat this defect.
        expect(attempted).toEqual(['/portals', DEFAULT_SIGNED_IN_ROUTE]);
      });

      it('leaves a gate that redirects alone, rather than overriding its destination', async () => {
        // A gate that returns an address expresses it by cancelling THIS navigation and scheduling its own,
        // so a refusal with a navigation already in flight must not be answered - doing so would race the
        // gate and could send the operator somewhere the gate had just decided against.
        const attempted = await signInAndBeRefused('/portals', '/login?returnUrl=%2Fportals', true);

        expect(attempted).toEqual(['/portals']);
      });

      it('does nothing when the refusal left the operator somewhere other than sign-in', async () => {
        const attempted = await signInAndBeRefused('/portals', '/users', false);

        expect(attempted).toEqual(['/portals']);
      });

      it('does not retry the landing address against itself', async () => {
        // The one arrangement that could loop: the fallback is refused as well. It is never
        // attempted a second time.
        const attempted = await signInAndBeRefused(DEFAULT_SIGNED_IN_ROUTE, '/login', false);

        expect(attempted).toEqual([DEFAULT_SIGNED_IN_ROUTE]);
      });
    });

    it('honours an internal path exactly as it arrived', () => {
      // The guard that produced the value preserved the attempted address byte for byte, so
      // re-serialising it here could only lose something.
      expectAccepted('/users/5');
    });

    it('round-trips the address the route guard writes, byte for byte', () => {
      // ⚠ A NON-DEFAULT TARGET WITH A QUERY AND A ZERO IDENTIFIER, chosen so the assertion cannot pass by
      // accident: it differs from the fallback address in its path, its query and its identifier at once.
      expectAccepted('/users/0?page=2');
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
      expectRejected('/redirect?next=https://evil.test', 'a scheme separator inside a query');
      expectRejected('/a/https://evil.test', 'a scheme separator inside a path segment');
    });

    it('refuses a backslash, which some browsers normalise into a second leading slash', () => {
      expectRejected('/\\evil.test/portals', 'a backslash after the slash becomes scheme-relative');
      expectRejected('/portals\\..\\evil', 'a backslash anywhere is refused');

      expectRejected('\\\\evil.test', 'two backslashes are the scheme-relative form on some engines');
      expectRejected('\\evil.test/portals', 'and one backslash is no more internal than two');
    });

    it('refuses a C0 control character, which a browser strips before resolving the address', () => {
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

      expect(navigateSpy).toHaveBeenCalledOnceWith(DEFAULT_SIGNED_IN_ROUTE, { replaceUrl: true });
    });

    it('is sent to the requested address when it is safe, and to the default when it is not', () => {
      establishSession();

      queryParams = { [RETURN_URL_QUERY_KEY]: '/users/5' };
      create();

      expect(navigateSpy).toHaveBeenCalledOnceWith('/users/5', { replaceUrl: true });
    });

    it('applies the same guard to the address it is sent on to', () => {
      establishSession();

      // The guard is at the point of USE, so it cannot be bypassed by arriving already signed
      // in rather than by signing in here.
      queryParams = { [RETURN_URL_QUERY_KEY]: '//evil.test/portals' };
      create();

      expect(navigateSpy).toHaveBeenCalledOnceWith(DEFAULT_SIGNED_IN_ROUTE, { replaceUrl: true });
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

  describe('an unconfirmed withdrawal from the session before this one', () => {
    /** Signs in on the real store, then signs out with a withdrawal the server refuses. */
    function signOutWithAnUnconfirmedWithdrawal(): void {
      store.login({ username: ACCOUNT_NAME, password: SUBMITTED_PASSWORD }).subscribe();
      httpMock
        .expectOne((candidate) => candidate.method === 'POST' && candidate.url === LOGIN_URL)
        .flush(credentialPayload());
      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === ME_URL)
        .flush(identityPayload());

      store.logout().subscribe();
      httpMock
        .expectOne((candidate) => candidate.method === 'POST' && candidate.url === LOGOUT_URL)
        .flush(null, { status: 503, statusText: 'Service Unavailable' });

      expect(store.isAuthenticated())
        .withContext('signed out locally whatever the server said')
        .toBeFalse();
      expect(store.revocationOutstanding())
        .withContext('and the withdrawal is genuinely unconfirmed')
        .toBeTrue();
    }

    /**
     * Answers the withdrawal retry this screen drives the moment it is created. ⚠ EVERY CASE THAT ARRIVES
     * HERE WITH A RETAINED CREDENTIAL MUST CALL THIS, and its existence is itself the proof that the
     * retry is wired: a screen that issued no request on arrival would need no such helper, and
     * `verify()` in `afterEach` would pass while the mechanism lay dead.
     *
     * @param status The transport status to answer the attempt with.
     */
    function answerRetainedWithdrawal(status = 204): void {
      httpMock
        .expectOne((candidate) => candidate.method === 'POST' && candidate.url === LOGOUT_URL)
        .flush(null, { status, statusText: status === 204 ? 'No Content' : 'Refused' });
    }

    it('drives the outstanding withdrawal the moment it is created', () => {
      signOutWithAnUnconfirmedWithdrawal();

      create();

      // ⚠ THE MEASURED DEFECT, AND THIS IS THE CASE THAT WOULD HAVE CAUGHT IT. The store retained the
      // credential and published a bounded retry for it, and a security review found that NOTHING in the
      // application ever called that retry: every refused withdrawal was retained and then left retained
      // until the tab closed, so the session it named stayed renewable until its absolute expiry.
      const attempt = httpMock.expectOne(
        (candidate) => candidate.method === 'POST' && candidate.url === LOGOUT_URL,
      );

      expect(attempt.request.body)
        .withContext('and it presents the credential the refused sign-out held aside')
        .toEqual({ refreshToken: FAKE_RENEWAL_TOKEN });

      attempt.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.revocationOutstanding())
        .withContext('the server has now ended the session, so there is no residue to report')
        .toBeFalse();

      fixture.detectChanges();

      expect(query('.login__notice'))
        .withContext('and the notice goes with it rather than standing over a residue that is gone')
        .toBeNull();
    });

    it('keeps the notice on screen while that retry is still unanswered', () => {
      signOutWithAnUnconfirmedWithdrawal();

      create();

      // Starting a retry must not retract the report: the residue is unconfirmed until the server says
      // otherwise, and an optimistic clear would tell the operator the session was ended by a request that
      // has not been answered.
      expect(query('.login__notice'))
        .withContext('the report stands over an in-flight retry')
        .not.toBeNull();
      expect(store.revocationOutstanding()).toBeTrue();

      answerRetainedWithdrawal();
    });

    it('drives nothing at all for a visitor with no outstanding withdrawal', () => {
      create();

      httpMock.expectNone((candidate) => candidate.url === LOGOUT_URL);
    });

    it('reports it calmly, in the same words the announcement channel used', () => {
      signOutWithAnUnconfirmedWithdrawal();

      create();

      const notice = query('.login__notice');

      expect(notice).withContext('the report is rendered').not.toBeNull();
      expect((notice?.textContent ?? '').trim()).toBe(REVOCATION_FAILED_MESSAGE);

      // Polite, not assertive: nothing the operator did was rejected and the local sign-out
      // succeeded, so this must not interrupt the way a refusal does.
      expect(notice?.getAttribute('role')).toBe('status');
      expect(query('.login__message'))
        .withContext('and it is not dressed as a refused attempt')
        .toBeNull();

      answerRetainedWithdrawal();
    });

    it('withdraws it the moment a new attempt begins, before its outcome is known', () => {
      signOutWithAnUnconfirmedWithdrawal();

      create();
      expect(query('.login__notice')).withContext('the precondition is on screen').not.toBeNull();

      fillCredentials();
      submit();

      const attempt = expectExactlyOneLoginRequest('one attempt, one request');

      fixture.detectChanges();

      // ⚠ ASSERTED WITH THE CREDENTIAL EXCHANGE STILL UNANSWERED. Retiring the report only on a SUCCESSFUL
      // sign-in would leave the previous session's sentence standing above the form for the whole duration
      // of the attempt, which is precisely when it is being read.
      expect(query('.login__notice'))
        .withContext('the previous session\'s report does not accompany this attempt')
        .toBeNull();

      attempt.flush(credentialPayload());
      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === ME_URL)
        .flush(identityPayload());
      fixture.detectChanges();

      expect(query('.login__notice')).toBeNull();

      // answered last, and its outcome deliberately cannot restore the report - the session epoch moved
      // when this attempt began, so the drain's report is withheld behind that boundary. See
      // `core/state/auth.store.spec.ts` for the case that pins it.
      answerRetainedWithdrawal();

      fixture.detectChanges();

      expect(query('.login__notice'))
        .withContext('a withdrawal settled after the boundary says nothing to the new session')
        .toBeNull();
    });

    it('paints NO required message after a SUCCESSFUL sign-in, though it has just emptied both boxes', () => {
      // ⚠ THE MEASURED DEFECT, AND IT IS THE SCREEN ACCUSING ITSELF. The credential is cleared the moment a
      // session is held, deliberately and for a stated security reason - a review found the account name
      // and sixteen masked characters still in the DOM after a successful authentication.
      create();
      fillCredentials();
      submit();

      const attempt = expectExactlyOneLoginRequest('one attempt, one request');

      attempt.flush(credentialPayload());
      httpMock
        .expectOne((candidate) => candidate.method === 'GET' && candidate.url === ME_URL)
        .flush(identityPayload());
      fixture.detectChanges();

      // The clearing itself is asserted too, so this case can never be "fixed" by keeping the credential.
      expect(requiredControl(LOGIN_CONTROL_IDS.username).value).withContext('emptied').toBe('');
      expect(requiredControl(LOGIN_CONTROL_IDS.password).value).withContext('emptied').toBe('');

      const messages = queryAll('.form-field__error').map((node) => (node.textContent ?? '').trim());

      expect(messages)
        .withContext('a successful sign-in reports no missing account name')
        .not.toContain(LOGIN_REQUIRED_MESSAGES.username);
      expect(messages)
        .withContext('nor a missing credential')
        .not.toContain(LOGIN_REQUIRED_MESSAGES.password);
      expect(messages).withContext('nor anything else').toEqual([]);
    });

    it('does not bring it back when that new attempt is refused', () => {
      // The other half. A refused attempt renders its own inline refusal, and re-presenting a
      // withdrawal notice beside it would read as an explanation of the refusal.
      signOutWithAnUnconfirmedWithdrawal();

      create();
      expect(query('.login__notice')).not.toBeNull();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      expect(query('.login__notice'))
        .withContext('retired by the attempt starting, and not restored by its failure')
        .toBeNull();

      // ⚠ THE REFUSAL ITSELF IS STILL REPORTED, in the region that owns refusals. Asserted so
      // that "the notice is gone" cannot pass by the screen having reported nothing at all.
      expect(query('.login__failure'))
        .withContext('the refused attempt is reported in its own region')
        .not.toBeNull();

      // SEC-03: the arrival's withdrawal retry, answered last for the reason given in the case
      // above - a confirmation must not be what retires the report this case is measuring.
      answerRetainedWithdrawal();
    });

    it('is absent for a visitor who never signed out at all', () => {
      create();

      expect(query('.login__notice')).toBeNull();
    });
  });

  // -------------------------------------------------------------------------
  // PROOF 9 — WHAT THIS SCREEN DELIBERATELY DOES NOT OFFER
  // -------------------------------------------------------------------------

  describe('deliberate omissions', () => {
    it('offers no keep-me-signed-in box, no recovery link and no registration link', () => {
      create();

      // The 37 lines of legacy markup contain none of the three, and the authentication surface is closed
      // at four endpoints - sign in, renew, sign out, read one's own identity - so no endpoint exists for
      // any of them.
      const text = host().textContent ?? '';

      expect(text).not.toContain('Remember');
      expect(text).not.toContain('Register');
      expect(text).not.toContain('Forgot');

      expect(queryAll('input[type="checkbox"]').length).toBe(0);
      expect(queryAll('a').length).withContext('no navigational affordance at all').toBe(0);
    });

    it('offers no human-verification challenge', () => {
      create();

      const text = host().textContent ?? '';

      expect(text).not.toContain('Security Code');
      expect(queryAll('img').length).withContext('no challenge image, and no image at all').toBe(0);
    });

    it('never renders the dead account-enumeration message from the legacy resource file', () => {
      create();

      attemptAndRefuse(
        refusal(INVALID_CREDENTIALS_CODE, 401, 'The account name or credential is not correct.'),
      );

      const text = host().textContent ?? '';

      expect(text).not.toContain('Does Not Exist');
    });
  });
});
