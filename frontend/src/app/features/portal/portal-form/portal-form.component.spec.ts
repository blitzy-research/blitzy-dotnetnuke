/**
 * Specification for {@link PortalFormComponent} — creating a portal, and editing its three text fields.
 *
 * ## THE ONE SCREEN, TWO CONTRACTS PROBLEM
 *
 * This component serves both a creation and a replacement, and the two are NOT variations of one form:
 *
 *   - Creation posts a TWELVE-member contract, five of whose members establish the portal's first
 *     administrator account, and it answers `201` with the created record.
 *   - Replacement puts a TWENTY-EIGHT-member contract that describes the WHOLE portal, of which this
 *     screen edits exactly THREE. The remaining twenty-five must be carried forward verbatim from the
 *     record that was read, because the endpoint replaces the row rather than patching it — a member
 *     left out or defaulted silently discards a setting an operator never touched.
 *
 * Which contract is used is derived from whether the address carries a portal at all, never from the
 * VALUE it carries: `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`, so minus one is the first portal of an
 * installation and zero the second, while minus one is ALSO the legacy absent-integer marker. Both are
 * asserted below for that reason.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - The component is mounted as the standalone unit it is, with the real {@link PortalStore} pinned to
 *     each case's injector, and every request answered through `HttpTestingController`.
 *   - The portal identifier is delivered through `componentRef.setInput` as the STRING a route parameter
 *     is, so the input's own transform is exercised rather than bypassed. Creation mode sets NO input at
 *     all, which is exactly what routing to the create address does.
 *   - `Router.navigate` is spied because the screen navigates with an ARRAY of commands;
 *     `NotificationService.notify` is spied and called through.
 *   - Every value is typed into a real control and every submission is a real press of a real button.
 *
 * ⚠ BOTH WRITES TRIGGER A LISTING RE-READ. The store re-reads `GET /api/v1/portals` after a successful
 * create and after a successful update, so a case that consumed only the write would leave an
 * unconsumed request and fail verification. That second read is asserted rather than merely tolerated.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { BannerAdvertisingMode, UserRegistrationMode } from '../../../core/models/portal.model';
import { NotificationService } from '../../../core/services/notification.service';
import { PortalStore } from '../../../core/state/portal.store';
import { PortalFormComponent } from './portal-form.component';

import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { ApiResponse } from '../../../core/models/paged-result.model';
import type { PagedResponse } from '../../../core/models/paged-result.model';
import type { PortalDetail, PortalListItem } from '../../../core/models/portal.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';

// =====================================================================================================
// ADDRESSES
// =====================================================================================================

const PORTALS_URL = '/api/v1/portals';

function portalUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}`;
}

/** Where both a completed write and an abandonment go. */
const PORTAL_LIST_ROUTE = '/portals';

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// Restated rather than imported, so a change to any of it is detected here. Each value is the legacy
// resource value where one exists.
// =====================================================================================================

const CREATE_HEADING = 'Add New Portal';
const EDIT_HEADING = 'Edit Portals';
const CREATE_SUBMIT_LABEL = 'Create Portal';
const EDIT_SUBMIT_LABEL = 'Update';
const CANCEL_LABEL = 'Cancel';

const ALIAS_REQUIRED_MESSAGE = 'Portal Name Is Required.';
const FIRST_NAME_REQUIRED_MESSAGE = 'First Name Is Required.';
const LAST_NAME_REQUIRED_MESSAGE = 'Last Name Is Required.';
const USERNAME_REQUIRED_MESSAGE = 'Username Is Required.';
const PASSWORD_REQUIRED_MESSAGE = 'Password Is Required.';
const CONFIRM_REQUIRED_MESSAGE = 'Password Confirmation Is Required.';
const EMAIL_REQUIRED_MESSAGE = 'Email Is Required.';
const INVALID_ALIAS_MESSAGE = 'The Portal Name Must Not Contain Spaces Or Punctuation.';
const PASSWORD_MISMATCH_MESSAGE = 'The Password Values Entered Do Not Match.';

const CREATE_SUCCEEDED_MESSAGE = 'The portal was created.';
const UPDATE_SUCCEEDED_MESSAGE = 'The portal was updated.';
const PORTAL_NOT_FOUND_MESSAGE = 'That portal no longer exists, so nothing could be loaded.';

/** The template the creation contract carries, which this screen does not offer a choice of. */
const DEFAULT_TEMPLATE_FILE = 'Default Website.template';

/** The two portal kinds, in the schema's own single-character spelling. */
const PARENT_TYPE = 'P';
const CHILD_TYPE = 'C';

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
};

const TRACE_ID = '00-1d5c9f2b7a934dd6bb18eb211c80319c-77bd6b7169203331-01';
const CORRELATION_ID = 'a6e4b912-5c3d-4a71-8b02-9d1f4e7c6a35';

function problem(
  code: string,
  status: number,
  detail: string,
  errors?: Readonly<Record<string, readonly string[]>>,
): ProblemDetails {
  const document: ProblemDetails = {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: STATUS_TITLE[status] ?? 'Error',
    status,
    detail,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };

  return errors === undefined ? document : { ...document, errors };
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

/**
 * A whole portal record.
 *
 * EVERY MEMBER THE CONTRACT DECLARES IS PRESENT, including those holding a legacy sentinel, because the
 * API serialises with its ignore condition set to never: a member with no value travels as its sentinel
 * or as null and never goes missing. The sentinels are load-bearing here — they are exactly what the
 * replacement must carry forward untouched.
 */
function portalDetail(portalId: number, overrides: Partial<PortalDetail> = {}): PortalDetail {
  return {
    portalId,
    portalName: 'Baseline Portal',
    description: 'The portal established by the baseline installation.',
    keyWords: 'baseline,portal',
    footerText: 'Copyright 2026',
    logoFile: 'logo.gif',
    backgroundFile: null,
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.None,
    currency: 'USD',
    administratorId: 1,
    email: 'admin@example.test',
    hostFee: 0,
    hostSpace: 0,
    pageQuota: 0,
    userQuota: 0,
    users: 3,
    pages: 12,
    administratorRoleId: 0,
    administratorRoleName: 'Administrators',
    registeredRoleId: 1,
    registeredRoleName: 'Registered Users',
    guid: '2f1c3d4e-5a6b-4c8d-9e0f-1a2b3c4d5e6f',
    paymentProcessor: null,
    processorUserId: null,
    siteLogHistory: -1,
    adminTabId: 2,
    superTabId: -1,
    splashTabId: -1,
    homeTabId: -1,
    loginTabId: -1,
    userTabId: -1,
    defaultLanguage: 'en-US',
    timeZoneOffset: -480,
    homeDirectory: 'Portals/0',
    aliases: [{ portalAliasId: 7, portalId, httpAlias: 'localhost', isCurrent: false }],
    ...overrides,
  };
}

/** The single-resource envelope. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * An empty page of portals, which is all the post-write listing re-read needs to answer with.
 *
 * ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data`. Every single-resource route answers
 * `{ data, meta }`, but a collection's body IS the page envelope, whose records live under `items` — a
 * fixture spelling it otherwise flushes successfully and unwraps to no records at all. The declared type
 * is the contract's own so this cannot drift from it silently.
 */
function emptyPage(): PagedResponse<PortalListItem> {
  return { items: [], meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 } };
}

describe('PortalFormComponent', () => {
  let fixture: ComponentFixture<PortalFormComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  beforeEach(async () => {
    // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
    await TestBed.configureTestingModule({
      imports: [PortalFormComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), PortalStore],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

    // The screen navigates with an ARRAY of commands, so `navigate` is the spied member rather than
    // `navigateByUrl`. No routes are declared, so a genuine navigation would fail to match.
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Mounts the screen in CREATION mode, which is the absence of a portal in the address. */
  function createMode(): void {
    fixture = TestBed.createComponent(PortalFormComponent);
    fixture.detectChanges();
  }

  /**
   * Mounts the screen in EDIT mode.
   *
   * The identifier is delivered as the STRING a route parameter is, so the input's transform runs.
   */
  function editMode(portalId: string): void {
    fixture = TestBed.createComponent(PortalFormComponent);
    fixture.componentRef.setInput('portalId', portalId);
    fixture.detectChanges();
  }

  /** Consumes exactly one pending request, asserted by verb AND address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** Answers the detail read the edit route issues. */
  function answerDetail(detail: PortalDetail): TestRequest {
    const call = expectRequest('GET', portalUrl(detail.portalId), 'the detail read');

    call.flush(envelope(detail));
    fixture.detectChanges();

    return call;
  }

  /** Mounts the screen in edit mode and settles the detail read. */
  function arriveEditing(portalId: number, overrides: Partial<PortalDetail> = {}): PortalDetail {
    const detail: PortalDetail = portalDetail(portalId, overrides);

    editMode(String(portalId));
    answerDetail(detail);

    return detail;
  }

  /**
   * Answers the listing re-read the store issues after every successful write.
   *
   * ⚠ NOT OPTIONAL. Both writes call it, so leaving it unconsumed fails verification at the end of the
   * case — which is the mechanism that made this behaviour visible in the first place.
   */
  function answerListingReread(): void {
    expectRequest('GET', PORTALS_URL, 'the listing re-read after a write').flush(emptyPage());
    fixture.detectChanges();
  }

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => (node.textContent ?? '').trim());
  }

  /** A control by its identifier, asserted to exist. */
  function field<E extends HTMLElement>(controlId: string): E {
    const element = query<E>(`#${controlId}`);

    expect(element).withContext(`#${controlId} is rendered`).not.toBeNull();

    return element as E;
  }

  /** Types into a text control the way a person does. */
  function type(controlId: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(controlId);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /**
   * Chooses a portal kind.
   *
   * ⚠ LOOKED UP BY IDENTIFIER, NEVER BY THE ELEMENT'S `value` ATTRIBUTE. The framework's radio accessor
   * takes the choice through a directive input and does NOT reflect it to the attribute, so a lookup by
   * value finds nothing and the assertion that follows passes vacuously against `undefined`. The
   * template composes each identifier from the kind, which is what makes this lookup reliable.
   */
  function choosePortalType(kind: string): void {
    const radio = field<HTMLInputElement>(`portal-form-portal-type-${kind}`);

    radio.click();
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

    expect(control).withContext(`the "${label}" control is offered`).not.toBeUndefined();

    (control as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  /** Fills every member the creation contract requires, leaving the alias to the caller. */
  function fillAdministrator(): void {
    type('portal-form-first-name', 'Ada');
    type('portal-form-last-name', 'Lovelace');
    type('portal-form-username', 'ada');
    type('portal-form-password', 'Passw0rd!');
    type('portal-form-confirm', 'Passw0rd!');
    type('portal-form-email', 'ada@example.test');
  }

  /** The per-field messages currently on screen. */
  function fieldMessages(): readonly string[] {
    return textOf('.form-field__error');
  }

  /** The announcements requested, newest last. */
  function notifications(): readonly { severity: string; message: string }[] {
    return notifySpy.calls.allArgs().map((args) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  // ---------------------------------------------------------------------------------------------------
  // PROOF 1 — WHICH CONTRACT, AND WHY
  // ---------------------------------------------------------------------------------------------------

  describe('choosing between the two contracts', () => {
    it('offers the creation form and reads nothing when the address names no portal', () => {
      createMode();

      // ⚠ NO DETAIL READ AT ALL. There is nothing to read: a portal that does not exist yet has no
      // record, and a request built from an absent identifier would address `/portals/undefined`.
      httpMock.expectNone(() => true);

      expect((query('h1')?.textContent ?? '').trim()).toBe(CREATE_HEADING);
      expect(button(CREATE_SUBMIT_LABEL)).withContext('the create wording').not.toBeUndefined();
      // The administrator fields exist ONLY on the creation contract.
      expect(query('#portal-form-username')).withContext('the account fields').not.toBeNull();
      expect(query('#portal-form-alias')).withContext('the host name field').not.toBeNull();
    });

    it('reads the record and offers the edit form for portal -1, the first portal', () => {
      editMode('-1');

      const call = expectRequest('GET', portalUrl(-1));

      // ⚠ MINUS ONE IS A REAL IDENTIFIER AND ALSO THE LEGACY ABSENT-INTEGER MARKER. Deriving the mode
      // from the VALUE rather than from its presence would put this screen into creation mode for the
      // very first portal of an installation.
      expect(call.request.url).toBe('/api/v1/portals/-1');

      call.flush(envelope(portalDetail(-1)));
      fixture.detectChanges();

      expect((query('h1')?.textContent ?? '').trim()).toBe(EDIT_HEADING);
      expect(button(EDIT_SUBMIT_LABEL)).withContext('the replace wording').not.toBeUndefined();
      // The account fields and the host name are NOT part of the replacement contract.
      expect(query('#portal-form-username')).withContext('no account fields').toBeNull();
      expect(query('#portal-form-alias')).withContext('no host name field').toBeNull();
    });

    it('reads the record for portal 0, which is an ordinary portal and not an absence', () => {
      editMode('0');

      const call = expectRequest('GET', portalUrl(0));

      expect(call.request.url).toBe('/api/v1/portals/0');

      call.flush(envelope(portalDetail(0)));
      fixture.detectChanges();

      expect((query('h1')?.textContent ?? '').trim()).toBe(EDIT_HEADING);
    });

    it('hydrates the three editable fields from the record, and only those three', () => {
      arriveEditing(-1);

      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Baseline Portal');
      expect(field<HTMLTextAreaElement>('portal-form-description').value).toBe(
        'The portal established by the baseline installation.',
      );
      expect(field<HTMLTextAreaElement>('portal-form-keywords').value).toBe('baseline,portal');
      // Three controls, so three fields — the other twenty-five members are carried, never shown.
      expect(queryAll('input, textarea, select')).withContext('exactly three controls').toHaveSize(3);
    });

    it('renders an absent text member as an empty control rather than as the word null', () => {
      arriveEditing(-1, { portalName: null, description: null, keyWords: null });

      expect(field<HTMLInputElement>('portal-form-title').value).toBe('');
      expect(field<HTMLTextAreaElement>('portal-form-description').value).toBe('');
      expect(field<HTMLTextAreaElement>('portal-form-keywords').value).toBe('');
    });

    it('shows the wait while the record is being read, and only then', () => {
      editMode('-1');

      expect(query('app-loading-spinner')).withContext('the wait is shown').not.toBeNull();

      answerDetail(portalDetail(-1));

      expect(query('app-loading-spinner')).withContext('and taken down').toBeNull();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 2 — CREATING A PORTAL
  // ---------------------------------------------------------------------------------------------------

  describe('creating a portal', () => {
    it('posts the twelve declared members and nothing else', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      type('portal-form-title', 'Contoso');
      type('portal-form-description', 'The Contoso tenant.');
      type('portal-form-keywords', 'contoso,tenant');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');

      // Asserted as a WHOLE OBJECT rather than member by member, so a member added, renamed or dropped
      // by either side fails here rather than being silently ignored by the server.
      expect(call.request.body).toEqual({
        portalName: 'Contoso',
        portalAlias: 'contoso.example.test',
        description: 'The Contoso tenant.',
        keyWords: 'contoso,tenant',
        // Not offered on this screen: the server derives the directory from the portal it creates.
        homeDirectory: null,
        // Not offered either, and the contract requires one, so the default travels explicitly.
        templateFile: DEFAULT_TEMPLATE_FILE,
        isChildPortal: false,
        administratorFirstName: 'Ada',
        administratorLastName: 'Lovelace',
        administratorUsername: 'ada',
        administratorPassword: 'Passw0rd!',
        administratorEmail: 'ada@example.test',
      });

      call.flush(envelope(portalDetail(0, { portalName: 'Contoso' })), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();

      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: CREATE_SUCCEEDED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
    });

    it('marks a child portal as one, and seeds its host name from the browsed host', () => {
      createMode();

      choosePortalType(CHILD_TYPE);

      // A child portal lives UNDER the current host, so the field is seeded with that host and a
      // separator: the operator supplies only the child segment. Seeding it is what stops a person
      // typing a bare segment that would resolve to nothing.
      const alias = field<HTMLInputElement>('portal-form-alias');

      expect(alias.value).withContext('seeded with a separator').toContain('/');

      type('portal-form-alias', `${alias.value}contoso`);
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the child creation');
      const body = call.request.body as { isChildPortal: boolean; portalAlias: string };

      expect(body.isChildPortal).withContext('declared as a child').toBeTrue();
      expect(body.portalAlias).withContext('carries the child segment').toContain('/contoso');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('clears a seeded child host name when the kind returns to parent', () => {
      createMode();

      choosePortalType(CHILD_TYPE);
      expect(field<HTMLInputElement>('portal-form-alias').value).not.toBe('');

      choosePortalType(PARENT_TYPE);

      // A parent portal's host name has nothing to do with the browsed host, so the seed is removed
      // rather than left for the operator to delete — and leaving it would send a child-shaped alias
      // for a parent portal.
      expect(field<HTMLInputElement>('portal-form-alias').value).toBe('');
    });

    it('lowercases the host name and strips a legacy scheme before sending', () => {
      createMode();

      type('portal-form-alias', 'http://Contoso.Example.Test');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL);

      // ⚠ NORMALISATION IS NOT COSMETIC. Tenant resolution compares the stored alias against the
      // request's host header, which carries no scheme and is lower-case, so an alias stored with either
      // can never match and the tenant would simply never resolve.
      expect((call.request.body as { portalAlias: string }).portalAlias).toBe(
        'contoso.example.test',
      );
      // And what was normalised is shown back, so a person is not left believing they saved their text.
      expect(field<HTMLInputElement>('portal-form-alias').value).toBe('contoso.example.test');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('reports every unmet requirement at once and sends nothing', () => {
      createMode();

      press(CREATE_SUBMIT_LABEL);

      const messages: readonly string[] = fieldMessages();

      // Declarative validation replaces the legacy screen's imperative guard, and marking every control
      // first is what makes the messages visible for fields the operator never reached. The wording is
      // the legacy resource wording, capitalisation included.
      expect(messages).toContain(ALIAS_REQUIRED_MESSAGE);
      expect(messages).toContain(FIRST_NAME_REQUIRED_MESSAGE);
      expect(messages).toContain(LAST_NAME_REQUIRED_MESSAGE);
      expect(messages).toContain(USERNAME_REQUIRED_MESSAGE);
      expect(messages).toContain(PASSWORD_REQUIRED_MESSAGE);
      expect(messages).toContain(CONFIRM_REQUIRED_MESSAGE);
      expect(messages).toContain(EMAIL_REQUIRED_MESSAGE);
      httpMock.expectNone(() => true);
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('refuses a host name carrying a space or punctuation the schema forbids', () => {
      createMode();

      type('portal-form-alias', 'contoso example');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      expect(fieldMessages()).toContain(INVALID_ALIAS_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('refuses a confirmation that does not match, on the group rather than on one control', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      fillAdministrator();
      type('portal-form-confirm', 'Different!');

      press(CREATE_SUBMIT_LABEL);

      // The rule compares TWO controls, so it belongs to the group that owns both — a validator on
      // either control alone could not see the other, and would report the wrong field.
      expect(fieldMessages()).toContain(PASSWORD_MISMATCH_MESSAGE);
      httpMock.expectNone(() => true);
    });

    it('sends the credential in the body and never in the address or a log line', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL);

      // ⚠ A CREDENTIAL MUST NEVER REACH A URL: addresses are logged by proxies and kept in history. It
      // travels in the body, and the body is the only place it appears.
      expect(call.request.urlWithParams).not.toContain('Passw0rd');
      expect(call.request.params.keys()).toHaveSize(0);
      expect((call.request.body as { administratorPassword: string }).administratorPassword).toBe(
        'Passw0rd!',
      );
      // And it is never painted back into the document.
      expect(host().innerHTML).not.toContain('Passw0rd');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('reports a duplicate host name at 409 and stays on the screen', () => {
      createMode();

      type('portal-form-alias', 'localhost');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL).flush(
        problem('portal.alias_duplicate', 409, 'An alias with this host name already exists.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      // A conflict is an ERROR rather than a warning — the shared severity resolution classifies 401,
      // 403, 404 and 429 as warnings and everything else as an error — and the entry survives so the
      // correction is one keystroke away.
      expect(notifications()).toHaveSize(1);
      expect(notifications()[0]?.severity).toBe('error');
      expect(navigateSpy).withContext('nobody is taken away from a failed create').not.toHaveBeenCalled();
      expect(field<HTMLInputElement>('portal-form-alias').value).toBe('localhost');
    });

    it('reports a refusal of authority as a warning and keeps the entry', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL).flush(
        problem(
          'auth.not_permitted',
          403,
          'The authenticated caller is not permitted to perform this operation.',
        ),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()[0]?.severity).toBe('warning');
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('shows a per-field server message beside the field the server named', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL).flush(
        problem('portal.administrator_invalid', 400, 'One or more validation errors occurred.', {
          administratorEmail: ['That email address is not acceptable.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      // The map's keys are .NET model-state keys and are NOT camel-cased by the client; they are read
      // with an index expression because the map is an index signature under the strict setting.
      expect(fieldMessages()).toContain('That email address is not acceptable.');
    });

    it('reports a server fault at 500 in the banner with its quotable reference', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL).flush(
        problem(
          'portal.creation_failed',
          500,
          'An unexpected error occurred while processing the request.',
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(query('.error-banner__title')).not.toBeNull();
      // ⚠ THE REFERENCE IS THE CORRELATION IDENTIFIER, not the trace identifier: the shared resolution
      // prefers it and falls back to the trace only when it is absent, and a live document carries both.
      expect(textOf('.error-banner__trace').join(' ')).toContain(CORRELATION_ID);
      expect(notifications()[0]?.severity).toBe('error');
    });

    it('leaves for the listing without sending anything when abandoned', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      press(CANCEL_LABEL);

      httpMock.expectNone(() => true);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
      // Nothing was validated on the way out, exactly as the legacy abandon action was declared not to.
      expect(fieldMessages()).toHaveSize(0);
      expect(notifications()).toHaveSize(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 3 — REPLACING A PORTAL
  // ---------------------------------------------------------------------------------------------------

  describe('editing a portal', () => {
    it('puts the whole record, carrying twenty-five untouched members forward verbatim', () => {
      const detail: PortalDetail = arriveEditing(-1);

      type('portal-form-title', 'Renamed Portal');
      type('portal-form-description', 'A new description.');
      type('portal-form-keywords', 'renamed,portal');

      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(-1), 'the replacement');

      // ⚠ THE ENDPOINT REPLACES THE ROW, IT DOES NOT PATCH IT. Every member omitted here would be
      // stored as its default, so this whole-object assertion is the only one that can catch a member
      // being dropped: the three edited values, and twenty-five carried through untouched — INCLUDING
      // the legacy sentinels, which are values rather than absences.
      expect(call.request.body).toEqual({
        portalId: -1,
        portalName: 'Renamed Portal',
        description: 'A new description.',
        keyWords: 'renamed,portal',
        logoFile: detail.logoFile,
        footerText: detail.footerText,
        expiryDate: detail.expiryDate,
        userRegistration: detail.userRegistration,
        bannerAdvertising: detail.bannerAdvertising,
        currency: detail.currency,
        administratorId: detail.administratorId,
        hostFee: detail.hostFee,
        hostSpace: detail.hostSpace,
        pageQuota: detail.pageQuota,
        userQuota: detail.userQuota,
        paymentProcessor: detail.paymentProcessor,
        processorUserId: detail.processorUserId,
        // ⚠ DELIBERATELY NULL, AND NEVER ECHOED. The processor credential is write-only: the read
        // contract does not return it, so sending anything here would either be invented or be a
        // credential this screen never held.
        processorCredentialReference: null,
        backgroundFile: detail.backgroundFile,
        siteLogHistory: detail.siteLogHistory,
        splashTabId: detail.splashTabId,
        homeTabId: detail.homeTabId,
        loginTabId: detail.loginTabId,
        userTabId: detail.userTabId,
        defaultLanguage: detail.defaultLanguage,
        timeZoneOffset: detail.timeZoneOffset,
        homeDirectory: detail.homeDirectory,
      });

      // ⚠ A REPLACEMENT ANSWERS 200 WITH THE STORED RECORD, not 204.
      call.flush(envelope(portalDetail(-1, { portalName: 'Renamed Portal' })), {
        status: 200,
        statusText: 'OK',
      });
      fixture.detectChanges();

      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: UPDATE_SUCCEEDED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
    });

    it('carries the sentinel-bearing members through as the values they are', () => {
      arriveEditing(0, { siteLogHistory: -1, homeTabId: -1, hostFee: 0, userQuota: 0 });

      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(0));
      const body = call.request.body as Record<string, unknown>;

      // ⚠ MINUS ONE IS THE LEGACY ABSENT-INTEGER MARKER AND ZERO IS A GENUINE ZERO. Coalescing either to
      // null — or dropping it as "empty" — would write a different portal than the one that was read.
      expect(body['siteLogHistory']).toBe(-1);
      expect(body['homeTabId']).toBe(-1);
      expect(body['hostFee']).toBe(0);
      expect(body['userQuota']).toBe(0);

      call.flush(envelope(portalDetail(0)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('sends the identifier from the address, untouched, including zero', () => {
      arriveEditing(0);

      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(0));

      expect(call.request.url).toBe('/api/v1/portals/0');
      expect((call.request.body as { portalId: number }).portalId).toBe(0);

      call.flush(envelope(portalDetail(0)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('refuses an over-long title and sends nothing', () => {
      arriveEditing(-1);

      type('portal-form-title', 'a'.repeat(129));

      press(EDIT_SUBMIT_LABEL);

      // The bound is the stored column's, so exceeding it here is refused before a request rather than
      // by the server truncating or rejecting it.
      expect(fieldMessages()).withContext('a length message is shown').not.toHaveSize(0);
      httpMock.expectNone(() => true);
    });

    it('accepts the longest permitted title and sends it whole', () => {
      arriveEditing(-1);

      type('portal-form-title', 'a'.repeat(128));

      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(-1), 'the boundary replacement');

      // The bound is inclusive, so the boundary value must pass rather than be refused by an off-by-one.
      expect((call.request.body as { portalName: string }).portalName).toBe('a'.repeat(128));

      call.flush(envelope(portalDetail(-1)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('withholds the submit control until the record has actually been read', () => {
      editMode('-1');

      // ⚠ THIS IS THE ONE STATE IN WHICH SUBMITTING WOULD BE DESTRUCTIVE. The replacement carries
      // twenty-five members forward from the record, so submitting before the record exists could only
      // send defaults for every one of them — which is precisely how an untouched setting gets erased.
      expect(button(EDIT_SUBMIT_LABEL)?.disabled).withContext('nothing read yet').toBeTrue();

      answerDetail(portalDetail(-1));

      expect(button(EDIT_SUBMIT_LABEL)?.disabled).withContext('the record is in hand').toBeFalse();
    });

    it('withholds the submit control while the replacement is outstanding', () => {
      arriveEditing(-1);

      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(-1));

      // One press cannot become two replacements, and the state is announced rather than only drawn.
      expect(button(EDIT_SUBMIT_LABEL)?.disabled).withContext('in flight').toBeTrue();
      expect(button(EDIT_SUBMIT_LABEL)?.getAttribute('aria-busy')).toBe('true');

      call.flush(envelope(portalDetail(-1)), { status: 200, statusText: 'OK' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('reports a portal that no longer exists in this screen own words', () => {
      editMode('-1');

      expectRequest('GET', portalUrl(-1)).flush(
        problem('portal.not_found', 404, 'The requested resource does not exist.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      // A missing record is the one refusal this screen re-words, because the server's general sentence
      // does not tell an operator that the thing they navigated to is gone.
      expect(notifications()).toEqual([
        { severity: 'warning', message: PORTAL_NOT_FOUND_MESSAGE },
      ]);
      // And nothing can be submitted against a record that was never read.
      expect(button(EDIT_SUBMIT_LABEL)?.disabled).toBeTrue();
    });

    it('reports a failed replacement without taking anybody away from their edit', () => {
      arriveEditing(-1);

      type('portal-form-title', 'Renamed Portal');

      press(EDIT_SUBMIT_LABEL);

      expectRequest('PUT', portalUrl(-1)).flush(
        problem(
          'server.unexpected_failure',
          500,
          'An unexpected error occurred while processing the request.',
        ),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(navigateSpy).withContext('still on the screen').not.toHaveBeenCalled();
      // The edit survives, so the attempt can be repeated without retyping it.
      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Renamed Portal');
      expect(button(EDIT_SUBMIT_LABEL)?.disabled).withContext('and can be retried').toBeFalse();
    });

    it('leaves for the listing without sending anything when abandoned', () => {
      arriveEditing(-1);

      type('portal-form-title', 'Renamed Portal');
      press(CANCEL_LABEL);

      httpMock.expectNone(() => true);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
      expect(notifications()).toHaveSize(0);
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // PROOF 4 — THE RENDERED DOCUMENT
  // ---------------------------------------------------------------------------------------------------

  describe('the rendered document', () => {
    it('emits no landmark and exactly one heading, because the shell owns both', () => {
      createMode();

      expect(queryAll('main, nav, header, footer')).toHaveSize(0);
      expect(queryAll('h1')).toHaveSize(1);
    });

    it('associates every control with a real label', () => {
      createMode();

      const labels: readonly HTMLLabelElement[] = queryAll<HTMLLabelElement>('label[for]');
      const targets: readonly string[] = labels.map((label) => label.getAttribute('for') ?? '');

      // A placeholder is a hint and never an accessible name, so every control here is named by a real
      // label element pointing at its identifier.
      ['portal-form-alias', 'portal-form-username', 'portal-form-email'].forEach((controlId) => {
        expect(targets).withContext(`${controlId} is named by a label`).toContain(controlId);
      });
      expect(labels.length).withContext('labels exist at all').toBeGreaterThan(0);
    });

    it('marks the credential controls so a browser does not paint them as text', () => {
      createMode();

      expect(field<HTMLInputElement>('portal-form-password').type).toBe('password');
      expect(field<HTMLInputElement>('portal-form-confirm').type).toBe('password');
    });

    it('keeps the announcing region mounted with nothing to announce', () => {
      createMode();

      // The region guards its own content, because a region created in the same instant as its content
      // is the case assistive technology frequently fails to announce.
      expect(query('.error-banner-live')).withContext('the region exists').not.toBeNull();
      expect(query('.error-banner__title')).withContext('but says nothing').toBeNull();
    });

    it('offers both commands as real buttons with their type declared', () => {
      createMode();

      expect(button(CREATE_SUBMIT_LABEL)?.getAttribute('type')).toBe('submit');
      expect(button(CANCEL_LABEL)?.getAttribute('type')).toBe('button');
      expect(queryAll('[onclick]')).toHaveSize(0);
    });
  });
});
