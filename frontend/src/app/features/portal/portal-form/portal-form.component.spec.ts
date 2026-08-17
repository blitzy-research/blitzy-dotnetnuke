import { DOCUMENT } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { BannerAdvertisingMode, UserRegistrationMode } from '../../../core/models/portal.model';
import { UnsavedChangesTracker } from '../../../core/guards/unsaved-changes.guard';
import { NotificationService } from '../../../core/services/notification.service';
import { ListReturnStore } from '../../../core/state/list-return.store';
import { PortalStore } from '../../../core/state/portal.store';
import { PortalFormComponent } from './portal-form.component';

import type { TestRequest } from '@angular/common/http/testing';
import type { ComponentFixture } from '@angular/core/testing';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { PortalDetail, PortalListItem } from '../../../core/models/portal.model';
import type { ProblemDetails, ProblemDetailsErrors } from '../../../core/models/problem-details.model';

// ADDRESSES

/** The portal collection, addressed exactly as the deployed application addresses it. */
const PORTALS_URL = '/api/v1/portals';

/** One portal, addressed by identifier. Interpolated so a sentinel identifier survives. */
function portalUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}`;
}

/** Where both a completed write and an abandoned one lead. */
const PORTAL_LIST_ROUTE = '/portals';

// The host name this screen seeds a child alias from

/** The host name the stubbed document reports. */
const STUBBED_HOST = 'portals.example.test';

/**
 * A document that reports {@link STUBBED_HOST} and behaves like the real one otherwise. The component
 * reads `inject(DOCUMENT).location.host`, so only `location` needs substituting — but the test harness
 * renders the fixture into that SAME document, so a bare object carrying nothing but a host name would
 * break rendering outright.
 */
function documentReporting(hostName: string, real: Document): Document {
  return new Proxy(real, {
    get(target: Document, member: string | symbol): unknown {
      if (member === 'location') {
        return { host: hostName };
      }

      const held: unknown = Reflect.get(target, member, target);

      return typeof held === 'function' ? held.bind(target) : held;
    },
  });
}

// MEASURED WORDING

/** `Signup.ascx.resx`: `PortalSetup.Text` and the two command labels for creation. */
const CREATE_HEADING = 'Add New Portal';
const CREATE_SUBMIT_LABEL = 'Create Portal';

/** `SiteSettings.ascx.resx`: the edit screen's own heading and command label. */
const EDIT_HEADING = 'Edit Portals';
const EDIT_SUBMIT_LABEL = 'Update';

/** Shared by both contracts. */
const CANCEL_LABEL = 'Cancel';

/**
 * The seven required-field sentences, measured from `Signup.ascx.resx`. All eight stored values open with
 * break markup — `<br>Portal Name Is Required.` and so on — because a Web Forms validator rendered inline
 * after a postback and its author expressed vertical spacing as content.
 */
const ALIAS_REQUIRED_MESSAGE = 'Portal Name Is Required.';

const SCHEME_ONLY_ALIAS = 'http://';
const FIRST_NAME_REQUIRED_MESSAGE = 'First Name Is Required.';
const LAST_NAME_REQUIRED_MESSAGE = 'Last Name Is Required.';
const USERNAME_REQUIRED_MESSAGE = 'Username Is Required.';
const PASSWORD_REQUIRED_MESSAGE = 'Password Is Required.';
const CONFIRM_REQUIRED_MESSAGE = 'Password Confirmation Is Required.';
const EMAIL_REQUIRED_MESSAGE = 'Email Is Required.';

/** Every required sentence, in the order the controls appear. */
const ALL_REQUIRED_MESSAGES: readonly string[] = [
  ALIAS_REQUIRED_MESSAGE,
  FIRST_NAME_REQUIRED_MESSAGE,
  LAST_NAME_REQUIRED_MESSAGE,
  USERNAME_REQUIRED_MESSAGE,
  PASSWORD_REQUIRED_MESSAGE,
  CONFIRM_REQUIRED_MESSAGE,
  EMAIL_REQUIRED_MESSAGE,
];

/** `Signup.ascx.resx`: `InvalidName.Text`. */
const INVALID_ALIAS_MESSAGE = 'The Portal Name Must Not Contain Spaces Or Punctuation.';

/** `Signup.ascx.resx`: `InvalidPassword.Text`. */
const PASSWORD_MISMATCH_MESSAGE = 'The Password Values Entered Do Not Match.';

/**
 * `Signup.ascx.resx`: `CreateError.Text`, carried across verbatim. The legacy handler did NOT show this
 * sentence for a failed creation.
 */
const CREATE_ERROR_MESSAGE =
  'An Error Was Encountered During The Creation Of Your Portal. This May Have Been ' +
  'Caused By Specifying An Incorrect Password For An Existing User Account. Please ' +
  'Verify Your Details Before You Try Again.';

/**
 * The sentence shown when the server refuses a change to a host-administered term. Net-new wording for a
 * measured refusal, opening on the one denial stem every app-authored refusal shares, so a reader who is
 * told no anywhere in the application is told no the same way.
 */
const HOST_FIELD_REFUSED_MESSAGE =
  'You do not have permission to change this portal\u2019s host-administered terms, which only a ' +
  'host account may change. Nothing was changed.';

/** The sentence shown when the record being edited has gone. */
/**
 * ⚠ THE SHARED SHAPE, NOT THIS SCREEN'S OWN SENTENCE. Each of the four detail screens worded a missing
 * record differently; one builder now words all three of the app-authored ones.
 */
const PORTAL_NOT_FOUND_MESSAGE = 'The portal could not be found. It may have been removed.';

/** `CONFLICT_MESSAGE['portal.alias_duplicate']`, measured from the legacy alias screen. */
const DUPLICATE_ALIAS_MESSAGE =
  'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.';

/** Confirmations for a completed write. */
const CREATE_SUCCEEDED_MESSAGE = 'The portal was created.';
const UPDATE_SUCCEEDED_MESSAGE = 'The portal was updated.';

// Measured limits and codes

/**
 * The length limit rendered on each control. Seven of the ten are the figures declared in `signup.ascx`
 * verbatim.
 */
const RENDERED_LIMITS: readonly (readonly [string, string])[] = [
  ['portal-form-alias', '128'],
  ['portal-form-title', '128'],
  ['portal-form-description', '500'],
  ['portal-form-keywords', '500'],
  ['portal-form-first-name', '50'],
  ['portal-form-last-name', '50'],
  ['portal-form-username', '100'],
  ['portal-form-password', '256'],
  ['portal-form-confirm', '256'],
  ['portal-form-email', '100'],
];

/** The two legacy list-item values, `signup.ascx`. */
const PARENT_TYPE = 'P';
const CHILD_TYPE = 'C';

/** The template the component supplies for the contract member it cannot ask about. */
const DEFAULT_TEMPLATE_FILE = 'Default Website.template';

/** `Null.NullDate`, the legacy sentinel for an unset date. */
const NULL_DATE = '0001-01-01T00:00:00';

// FAILURE DOCUMENTS

/** The namespace every machine-readable failure code sits under. */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/** Reason phrases, so a flushed document is shaped like one the API would really send. */
const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
};

/** A trace identifier of the shape the API emits. */
const TRACE_ID = '00-1d5c9f2b7a934dd6bb18eb211c80319c-77bd6b7169203331-01';

/** A correlation identifier of the shape the API emits. */
const CORRELATION_ID = 'a6e4b912-5c3d-4a71-8b02-9d1f4e7c6a35';

/**
 * An RFC 7807 document.
 *
 * @param code The failure code, appended to {@link FAILURE_TYPE_PREFIX}.
 * @param status The HTTP status, mirrored into the document as the API mirrors it.
 * @param detail The human-readable sentence.
 * @param errors Per-field messages, keyed as the server keys them.
 */
function problem(
  code: string,
  status: number,
  detail: string,
  errors?: ProblemDetailsErrors,
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

// RECORD FIXTURES

/** A portal record, complete. */
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
    concurrencyToken: 'revision-1',
    ...overrides,
  };
}

/** The single-record envelope every read and write reply arrives in. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/** An empty listing page. */
function emptyPage(): PagedResponse<PortalListItem> {
  return { items: [], meta: { totalCount: 0, pageIndex: 0, pageSize: 10, totalPages: 0 } };
}

/** The body a server returns when it faults without producing a failure document. */
interface RawFault {
  readonly message: string;
}

/** A recorded notification, reduced to the two members these tests assert on. */
interface RecordedNotification {
  readonly severity: string;
  readonly message: string;
}

describe('PortalFormComponent', () => {
  let fixture: ComponentFixture<PortalFormComponent>;
  let httpMock: HttpTestingController;
  let notifySpy: jasmine.Spy;
  let navigateSpy: jasmine.Spy;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      // A STANDALONE component is IMPORTED. Nothing in this workspace is declared through a wrapping module,
      // so nothing is declared here either.
      imports: [PortalFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // The screen navigates on both a completed write and an abandoned one, so the router has to be real
        // enough to be spied on.
        provideRouter([]),
        PortalStore,
        {
          provide: DOCUMENT,
          useFactory: (): Document => documentReporting(STUBBED_HOST, document),
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // Left calling through: the real service records the notification, so a test can assert either the call
    // or the resulting queue and both agree.
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

    // Resolved rather than called through, so no test depends on a route actually existing in the empty
    // routing table above.
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    // Every request this screen issues is asserted. Anything unexpected — a second read, a stray listing
    // refresh, a write that should not have been attempted — is reported here rather than passing unnoticed.
    httpMock.verify();
  });

  // Arriving at the screen

  /** Arrive with no portal named, which is the creation contract. */
  function createMode(): void {
    fixture = TestBed.createComponent(PortalFormComponent);
    fixture.detectChanges();
  }

  /**
   * Arrive with a portal named. The identifier is passed as the STRING a router would supply, so the
   * component's own parsing is exercised rather than bypassed.
   */
  function editMode(portalId: string): void {
    fixture = TestBed.createComponent(PortalFormComponent);
    fixture.componentRef.setInput('portalId', portalId);
    fixture.detectChanges();
  }

  // REQUESTS

  /** Expect exactly one request with this method and address. */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /** Answer the detail read with a record, then render. */
  function answerDetail(detail: PortalDetail): TestRequest {
    const call = expectRequest('GET', portalUrl(detail.portalId), 'the detail read');

    call.flush(envelope(detail));
    fixture.detectChanges();

    return call;
  }

  /** Arrive editing a portal and hydrate it in one step. */
  function arriveEditing(portalId: number, overrides: Partial<PortalDetail> = {}): PortalDetail {
    const detail: PortalDetail = portalDetail(portalId, overrides);

    editMode(String(portalId));
    answerDetail(detail);

    return detail;
  }

  function answerListingReread(): void {
    expect(httpMock.match((candidate) => candidate.url === PORTALS_URL))
      .withContext('a write asks for no listing read; the listing reads itself on entry')
      .toHaveSize(0);
    fixture.detectChanges();
  }

  // Reading the rendered document

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  /** The element at this selector, or a thrown failure naming what was missing. */
  function required<E extends Element>(selector: string): E {
    const found: E | null = query<E>(selector);

    if (found === null) {
      throw new Error(`Expected ${selector} to be rendered, but it was not.`);
    }

    return found;
  }

  /** Trimmed text of every element matching the selector. */
  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => (node.textContent ?? '').trim());
  }

  /** A form control, by the identifier the template gives it. */
  function field<E extends HTMLElement>(controlId: string): E {
    return required<E>(`#${controlId}`);
  }

  /** Enter a value the way a person does, so the control is marked touched and dirty. */
  function type(controlId: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(controlId);

    control.value = value;
    control.dispatchEvent(new Event('input'));
    control.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  }

  /** Choose a portal kind, which the screen reacts to immediately. */
  function choosePortalType(kind: string): void {
    field<HTMLInputElement>(`portal-form-portal-type-${kind}`).click();
    fixture.detectChanges();
  }

  /** A command button, found by the words on it. */
  function button(label: string): HTMLButtonElement | undefined {
    return queryAll<HTMLButtonElement>('button').find(
      (candidate) => (candidate.textContent ?? '').trim() === label,
    );
  }

  /** Press a command button, or fail naming the one that was not offered. */
  function press(label: string): void {
    const control: HTMLButtonElement | undefined = button(label);

    if (control === undefined) {
      throw new Error(`Expected a "${label}" command to be offered, but it was not.`);
    }

    control.click();
    fixture.detectChanges();
  }

  /** The value currently shown in the alias box. */
  function aliasValue(): string {
    return field<HTMLInputElement>('portal-form-alias').value;
  }

  /** Fill the administrator block with values that satisfy every rule. */
  function fillAdministrator(): void {
    type('portal-form-first-name', 'Ada');
    type('portal-form-last-name', 'Lovelace');
    type('portal-form-username', 'ada');
    type('portal-form-password', 'Passw0rd!');
    type('portal-form-confirm', 'Passw0rd!');
    type('portal-form-email', 'ada@example.test');
  }

  /** Fill everything a creation needs, alias included, and nothing optional. */
  function fillMinimalCreation(alias: string): void {
    type('portal-form-alias', alias);
    fillAdministrator();
  }

  /** Every field-level message currently shown, as plain text. */
  function fieldMessages(): readonly string[] {
    return textOf('.form-field__error');
  }

  /** Every notification recorded so far, oldest first. */
  function notifications(): readonly RecordedNotification[] {
    return notifySpy.calls.allArgs().map((args: readonly unknown[]) => ({
      severity: String(args[0]),
      message: String(args[1]),
    }));
  }

  /** Every route the screen has navigated to, flattened to a comparable string. */
  function navigations(): readonly string[] {
    return navigateSpy.calls.allArgs().map((args: readonly unknown[]) => JSON.stringify(args[0]));
  }

  // Choosing between the two contracts

  describe('choosing between the two contracts', () => {
    it('offers the creation form and reads nothing when no portal is named', () => {
      createMode();

      // The administrator block belongs to creation alone: an existing portal already has an administrator,
      // and this screen never re-states one.
      expect(query('#portal-form-first-name')).withContext('given name').not.toBeNull();
      expect(query('#portal-form-last-name')).withContext('family name').not.toBeNull();
      expect(query('#portal-form-username')).withContext('sign-in name').not.toBeNull();
      expect(query('#portal-form-password')).withContext('credential').not.toBeNull();
      expect(query('#portal-form-confirm')).withContext('confirmation').not.toBeNull();
      expect(query('#portal-form-email')).withContext('mail address').not.toBeNull();
      expect(query('#portal-form-alias')).withContext('alias').not.toBeNull();

      // No read is issued, which the end-of-test verification enforces — a request of whatever kind would be
      // an unexpected one.
    });

    it('reads the record and offers the edit form for an ordinary portal', () => {
      arriveEditing(5);

      expect(expectHeading()).toBe(EDIT_HEADING);
      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Baseline Portal');
    });

    // `Portals.PortalID` is `IDENTITY(-1,1)`, so 0 is the FIRST ORDINARY PORTAL and not an absence. A
    // truthiness test on the identifier would offer a creation form for a record that already exists.
    it('reads the record for portal 0, which is an ordinary portal and not an absence', () => {
      editMode('0');

      const call = expectRequest('GET', portalUrl(0), 'the read for portal zero');

      expect(call.request.url).toBe('/api/v1/portals/0');

      call.flush(envelope(portalDetail(0)));
      fixture.detectChanges();

      expect(expectHeading()).withContext('the edit contract, not creation').toBe(EDIT_HEADING);
      expect(query('#portal-form-username')).withContext('no administrator block').toBeNull();
    });

    // MIGRATION: -1 is simultaneously a legitimate identifier and the legacy `Null.NullInteger` sentinel. A
    // lower-bound test on the identifier would make the baseline portal uneditable.
    it('reads the record for portal -1, the baseline portal, rather than treating it as unset', () => {
      editMode('-1');

      const call = expectRequest('GET', portalUrl(-1), 'the read for the baseline portal');

      expect(call.request.url).toBe('/api/v1/portals/-1');

      call.flush(envelope(portalDetail(-1)));
      fixture.detectChanges();

      expect(expectHeading()).withContext('the edit contract, not creation').toBe(EDIT_HEADING);
      expect(query('#portal-form-username')).withContext('no administrator block').toBeNull();
    });

    it('titles and labels the creation contract with the measured wording', () => {
      createMode();

      expect(expectHeading()).toBe(CREATE_HEADING);
      expect(button(CREATE_SUBMIT_LABEL)).withContext('the creation command').not.toBeUndefined();
      expect(button(CANCEL_LABEL)).withContext('the abandon command').not.toBeUndefined();
      expect(button(EDIT_SUBMIT_LABEL)).withContext('not the edit command').toBeUndefined();
    });

    it('titles and labels the edit contract with the measured wording', () => {
      arriveEditing(5);

      expect(expectHeading()).toBe(EDIT_HEADING);
      expect(button(EDIT_SUBMIT_LABEL)).withContext('the edit command').not.toBeUndefined();
      expect(button(CANCEL_LABEL)).withContext('the abandon command').not.toBeUndefined();
      expect(button(CREATE_SUBMIT_LABEL)).withContext('not the creation command').toBeUndefined();
    });

    it('hydrates the three editable members and offers no others', () => {
      arriveEditing(5, {
        portalName: 'Contoso',
        description: 'The Contoso tenant.',
        keyWords: 'contoso,tenant',
      });

      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Contoso');
      expect(field<HTMLTextAreaElement>('portal-form-description').value).toBe(
        'The Contoso tenant.',
      );
      expect(field<HTMLTextAreaElement>('portal-form-keywords').value).toBe('contoso,tenant');

      // The host-administered terms, the tab references and the alias are all absent: they belong to the
      // settings screen and to the alias screen respectively.
      expect(query('#portal-form-alias')).withContext('alias').toBeNull();
      expect(query('#portal-form-host-fee')).withContext('host fee').toBeNull();
      expect(query('#portal-form-expiry-date')).withContext('expiry date').toBeNull();
    });

    it('shows the wait while the record is being read, and only then', () => {
      editMode('5');

      expect(query('app-loading-spinner')).withContext('waiting on the read').not.toBeNull();
      expect(query('#portal-form-title')).withContext('nothing to edit yet').toBeNull();

      answerDetail(portalDetail(5));

      expect(query('app-loading-spinner')).withContext('the read has landed').toBeNull();
      expect(query('#portal-form-title')).withContext('now editable').not.toBeNull();
    });

    /** The single heading this screen contributes. */
    function expectHeading(): string {
      return (required<HTMLElement>('.page-header__title').textContent ?? '').trim();
    }
  });

  // What the screen insists on

  describe('what the screen insists on', () => {
    it('reports all seven unmet requirements at once, in the measured wording, and sends nothing', () => {
      createMode();

      press(CREATE_SUBMIT_LABEL);

      const shown: readonly string[] = fieldMessages();

      ALL_REQUIRED_MESSAGES.forEach((sentence: string) => {
        expect(shown).withContext(`the measured sentence "${sentence}"`).toContain(sentence);
      });

      // Exactly seven, so nothing extra is being reported and nothing is missing. The eighth legacy
      // validator guarded the template selector, which is gone.
      expect(shown).withContext('seven requirements, no more').toHaveSize(
        ALL_REQUIRED_MESSAGES.length,
      );

      // No write is attempted while a requirement is unmet, which the end-of-test verification enforces.
      expect(navigateSpy).withContext('nobody is taken anywhere').not.toHaveBeenCalled();
    });

    // All eight measured values in `Signup.ascx.resx` open with break markup — `<br>Portal Name Is
    // Required.` and so on — because a Web Forms validator rendered inline after a postback. The markup is
    // stripped and the wording carried as text.
    it('carries the measured sentences as text, without the break markup they were stored with', () => {
      createMode();

      press(CREATE_SUBMIT_LABEL);

      fieldMessages().forEach((sentence: string) => {
        expect(sentence).withContext('no leading break markup').not.toMatch(/^<br\s*\/?>/i);
        expect(sentence).withContext('no break markup anywhere').not.toContain('<br');
      });

      // Proof the sentences are text rather than parsed markup: had the stored value been bound as raw
      // markup, the break would have become an element in the document.
      expect(queryAll('.form-field__errors br')).withContext('no break elements').toHaveSize(0);
    });

    it('keeps the legacy wording of the alias requirement even though it names the wrong field', () => {
      createMode();

      press(CREATE_SUBMIT_LABEL);

      expect(fieldMessages()).toContain('Portal Name Is Required.');
      expect(fieldMessages()).withContext('not silently corrected').not.toContain(
        'Portal Alias Is Required.',
      );
    });

    it('does not insist on the title, the description or the keywords', () => {
      createMode();

      // Everything required, and the three descriptive members deliberately left blank.
      fillMinimalCreation('contoso.example.test');

      press(CREATE_SUBMIT_LABEL);

      expect(fieldMessages()).withContext('nothing is being complained about').toEqual([]);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');

      const body = call.request.body as {
        readonly portalName: string;
        readonly description: string;
        readonly keyWords: string;
      };

      expect(body.portalName).withContext('an empty title, not a null one').toBe('');
      expect(body.description).withContext('an empty description').toBe('');
      expect(body.keyWords).withContext('empty keywords').toBe('');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('marks the seven insisted-upon controls for assistive technology', () => {
      createMode();

      // The state is declared, and the rule is not: a native `required` attribute beside a reactive control
      // would install a SECOND required validator, putting one rule in two places. So the marker is an ARIA
      // one.
      expect(queryAll('[aria-required="true"]')).withContext('seven marked controls').toHaveSize(7);
      expect(queryAll('.form-field__required')).withContext('seven visible markers').toHaveSize(7);
      expect(queryAll('[formcontrolname][required]')).withContext('no duplicated rule').toHaveSize(
        0,
      );
    });
  });

  // The password confirmation

  describe('the password confirmation', () => {
    it('refuses a confirmation that does not match, in the measured wording', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      type('portal-form-first-name', 'Ada');
      type('portal-form-last-name', 'Lovelace');
      type('portal-form-username', 'ada');
      // SEVEN characters, differing only in the last one. The length matters: the measured legacy policy
      // sets a minimum of seven, so a shorter pair would be refused for being short and the mismatch — the
      // rule under test here — would never be reached.
      type('portal-form-password', 'abc123d');
      type('portal-form-confirm', 'abc123e');
      type('portal-form-email', 'ada@example.test');

      press(CREATE_SUBMIT_LABEL);

      expect(fieldMessages()).toContain(PASSWORD_MISMATCH_MESSAGE);

      // Once only. The rule lives on the group, so it is reported beside the confirmation rather than
      // duplicated onto both credential controls.
      expect(
        fieldMessages().filter((sentence: string) => sentence === PASSWORD_MISMATCH_MESSAGE),
      ).withContext('reported once').toHaveSize(1);

      // Nothing is sent, which the end-of-test verification enforces.
    });

    it('accepts the entry once the two agree, and then sends it', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      type('portal-form-first-name', 'Ada');
      type('portal-form-last-name', 'Lovelace');
      type('portal-form-username', 'ada');
      type('portal-form-password', 'abc123def');
      type('portal-form-confirm', 'abc124def');
      type('portal-form-email', 'ada@example.test');

      press(CREATE_SUBMIT_LABEL);
      expect(fieldMessages()).withContext('refused first').toContain(PASSWORD_MISMATCH_MESSAGE);

      // Correct the confirmation and the complaint clears without anything else changing.
      type('portal-form-confirm', 'abc123def');

      expect(fieldMessages()).withContext('the complaint is gone').not.toContain(
        PASSWORD_MISMATCH_MESSAGE,
      );

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();

      expect(navigations()).toEqual([JSON.stringify([PORTAL_LIST_ROUTE])]);
    });

    it('reports the credential as merely missing, not as mismatched, while it is still blank', () => {
      createMode();

      press(CREATE_SUBMIT_LABEL);

      // An empty confirmation is unmet rather than wrong, so the screen says so. Reporting a mismatch
      // against a box nobody has typed in yet would be noise.
      expect(fieldMessages()).toContain(CONFIRM_REQUIRED_MESSAGE);
      expect(fieldMessages()).not.toContain(PASSWORD_MISMATCH_MESSAGE);
    });

    it('never puts the credential in the address', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');

      expect(call.request.urlWithParams).withContext('no credential in the address').toBe(
        PORTALS_URL,
      );
      expect(call.request.urlWithParams).not.toContain('Passw0rd');

      const body = call.request.body as { readonly administratorPassword: string };

      expect(body.administratorPassword).withContext('in the body, where it belongs').toBe(
        'Passw0rd!',
      );

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });
  });

  // Tidying the alias before judging it

  // ⚠ MAJOR (CWE-316 cleartext storage) — WHAT HAPPENS TO THE CREDENTIAL AFTER THE WRITE SUCCEEDS.

  describe('discarding the credential after a successful creation', () => {
    it('empties both credential controls and both boxes before navigating away', () => {
      createMode();
      fillMinimalCreation('contoso.example.test');

      // The credential is on the screen before the write, which is what makes the assertion after it mean
      // something.
      expect(field<HTMLInputElement>('portal-form-password').value).toBe('Passw0rd!');
      expect(field<HTMLInputElement>('portal-form-confirm').value).toBe('Passw0rd!');

      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(envelope(portalDetail(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      // The controls, and the boxes bound to them. Both, because a cleared control with a stale input is
      // still a credential on the screen.
      expect(field<HTMLInputElement>('portal-form-password').value)
        .withContext('the box no longer shows it')
        .toBe('');
      expect(field<HTMLInputElement>('portal-form-confirm').value)
        .withContext('nor does the confirmation')
        .toBe('');

      // Nowhere else in the rendered document either - not in a value attribute, not in a title, not in a
      // hidden field somebody added.
      expect(host().outerHTML)
        .withContext('the credential appears nowhere in the document')
        .not.toContain('Passw0rd!');
    });

    it('empties them even when the navigation is REFUSED, which is the case that matters', () => {
      // A guard answering false is the ordinary way this happens. The write has already succeeded, so the
      // credential is stored and useless here - and the screen stays mounted, which is exactly why relying
      // on the navigation to dispose of it was not enough.
      navigateSpy.and.resolveTo(false);

      createMode();
      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(envelope(portalDetail(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      // The screen is still here.
      expect(field<HTMLInputElement>('portal-form-alias')).not.toBeNull();

      expect(field<HTMLInputElement>('portal-form-password').value).toBe('');
      expect(field<HTMLInputElement>('portal-form-confirm').value).toBe('');
      expect(host().outerHTML).not.toContain('Passw0rd!');
    });

    it('empties them even when the navigation REJECTS, and reports nothing further', () => {
      // The other way a navigation fails to complete: a rejected promise, which a failed lazy chunk
      // produces. The clearing happens before the call, so the rejection cannot affect it.
      navigateSpy.and.rejectWith(new Error('a chunk failed to load'));

      createMode();
      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(envelope(portalDetail(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      expect(field<HTMLInputElement>('portal-form-password').value).toBe('');
      expect(field<HTMLInputElement>('portal-form-confirm').value).toBe('');
      expect(host().outerHTML).not.toContain('Passw0rd!');
    });

    it('leaves the entry an operator can re-read alone, so a refused trip is not a lost draft', () => {
      // The counterpart, and the reason the whole form is not wiped. Only the two credential controls are
      // secrets; clearing the alias, the name or the mail address would turn a refused navigation into lost
      // work for no security gain, since every one of those values is on the screen to be read.
      navigateSpy.and.resolveTo(false);

      createMode();
      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(envelope(portalDetail(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      expect(field<HTMLInputElement>('portal-form-alias').value).toBe('contoso.example.test');
      expect(field<HTMLInputElement>('portal-form-first-name').value).toBe('Ada');
      expect(field<HTMLInputElement>('portal-form-email').value).toBe('ada@example.test');
    });

    it('shows no complaint about the credential it has just discarded', () => {
      // Cleared with `reset`, not with a value assignment, so the control goes back to pristine and
      // untouched along with its value.
      navigateSpy.and.resolveTo(false);

      createMode();
      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(envelope(portalDetail(9)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      expect(fieldMessages())
        .withContext('no message about a credential that is gone on purpose')
        .toEqual([]);
    });
  });

  describe('tidying the alias before judging it', () => {
    /** Submit a creation whose only interesting member is the alias, and read the body. */
    function aliasSentFor(entered: string): string {
      fillMinimalCreation(entered);
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalAlias: string };
      const sent: string = body.portalAlias;

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();

      return sent;
    }

    it('lowercases what was entered', () => {
      createMode();

      expect(aliasSentFor('Contoso.Example.TEST')).toBe('contoso.example.test');
    });

    /**
     * ⚠ R-M20: THE TITLE IS TIDIED TOO, AND THIS SCREEN WAS THE ONE THAT DID NOT DO IT. Runtime testing
     * measured three screens against one another: the role editor trims its name into the control before
     * judging it, the site-settings screen trims its title into the control before judging it, and this
     * screen sent whatever was typed — twenty-three characters typed, twenty-three sent, against
     * seventeen on the sibling resource for the same class of field.
     */
    it('trims the site title into its control and sends the trimmed value', () => {
      createMode();
      type('portal-form-title', '   Padded Portal Title   ');
      fillMinimalCreation('padded.example.test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalName: string };

      expect(body.portalName).toBe('Padded Portal Title');
      expect(field<HTMLInputElement>('portal-form-title').value)
        .withContext('written back, so the operator sees what will be sent')
        .toBe('Padded Portal Title');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    /** ⚠ A WHITESPACE-ONLY TITLE IS ACCEPTED ON THE CREATION PATH, AND THAT IS THE SERVER'S RULE */
    it('sends a whitespace-only title as the empty string, and still creates', () => {
      createMode();
      type('portal-form-title', '     ');
      fillMinimalCreation('blank-title.example.test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalName: string };

      expect(body.portalName).withContext('five spaces became nothing, not five spaces').toBe('');
      expect(field<HTMLInputElement>('portal-form-title').value).toBe('');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('strips a legacy scheme wherever it appears, not merely at the front', () => {
      createMode();

      // Two occurrences, one of them interior. A leading-only strip would leave the second.
      expect(aliasSentFor('http://a/http://b')).toBe('a/b');
    });

    it('strips the scheme and lowercases together', () => {
      createMode();

      expect(aliasSentFor('HTTP://Contoso.Example.Test')).toBe('contoso.example.test');
    });

    // The ordering is the whole point: uppercase letters and the `:` and `/` of a scheme are not in the
    // permitted set, so an entry that needed tidying would be refused outright if the character rule ran
    // first.
    it('tidies before judging, so an entry that is only untidy is accepted', () => {
      createMode();

      type('portal-form-alias', 'HTTP://MySite.COM');

      expect(fieldMessages())
        .withContext('untidiness alone is not an offence')
        .not.toContain(INVALID_ALIAS_MESSAGE);

      fillAdministrator();
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalAlias: string };

      expect(body.portalAlias).toBe('mysite.com');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('REFUSES a bare scheme rather than creating a portal with no host name', () => {
      createMode();

      fillMinimalCreation(SCHEME_ONLY_ALIAS);

      // Before the press, every declarative rule is satisfied: the entry is seven characters
      // long, so nothing on screen says anything is wrong. That is the trap.
      expect(fieldMessages())
        .withContext('the raw entry offends no declarative rule')
        .not.toContain(ALIAS_REQUIRED_MESSAGE);

      press(CREATE_SUBMIT_LABEL);

      // ⚠ NOTHING IS SENT. This is the assertion the defect failed: the request went out
      // carrying `portalAlias: ""`.
      httpMock.expectNone(() => true);

      // And the operator is told why, beside the field they did fill in — they typed
      // something, so they are owed an explanation of why it amounts to nothing.
      expect(fieldMessages())
        .withContext('the requirement is reported once tidying has emptied the box')
        .toContain(ALIAS_REQUIRED_MESSAGE);

      // The box shows what will be judged, so the message and the field agree.
      expect(aliasValue()).toBe('');
    });

    it('refuses an entry made of NOTHING BUT schemes, however many', () => {
      // The strip removes every occurrence, so a repeated scheme also tidies to nothing. The
      // guard must be about the RESULT of tidying rather than about a single leading prefix.
      createMode();

      fillMinimalCreation(`${SCHEME_ONLY_ALIAS}${SCHEME_ONLY_ALIAS}`);

      press(CREATE_SUBMIT_LABEL);

      httpMock.expectNone(() => true);
      expect(fieldMessages()).toContain(ALIAS_REQUIRED_MESSAGE);
    });

    it('still ACCEPTS an entry that tidying merely shortens, which is the case that must not regress', () => {
      createMode();

      fillMinimalCreation(`${SCHEME_ONLY_ALIAS}contoso.example.test`);

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalAlias: string };

      expect(body.portalAlias).toBe('contoso.example.test');
      expect(fieldMessages()).not.toContain(ALIAS_REQUIRED_MESSAGE);

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('lets a corrected entry through immediately, so the refusal does not latch', () => {
      createMode();

      fillMinimalCreation(SCHEME_ONLY_ALIAS);
      press(CREATE_SUBMIT_LABEL);
      httpMock.expectNone(() => true);

      // The operator reads the message and types a real host name. Nothing else is re-entered.
      type('portal-form-alias', 'contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalAlias: string };

      expect(body.portalAlias).toBe('contoso.example.test');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('writes the tidied alias back, so what is shown is what was sent', () => {
      createMode();

      fillMinimalCreation('HTTP://Contoso.Example.Test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalAlias: string };

      // The box and the payload agree, so nobody is left looking at an entry that differs from the one that
      // was accepted.
      expect(aliasValue()).withContext('what is shown').toBe('contoso.example.test');
      expect(body.portalAlias).withContext('what was sent').toBe(aliasValue());

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });
  });

  // Which characters an alias may carry

  describe('which characters an alias may carry', () => {
    it('lets a parent alias carry the dot, slash and colon a host name needs', () => {
      createMode();

      choosePortalType(PARENT_TYPE);
      type('portal-form-alias', 'example.com:8080/site');

      expect(fieldMessages())
        .withContext('a parent alias may be a full authority')
        .not.toContain(INVALID_ALIAS_MESSAGE);

      fillAdministrator();
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the parent creation');
      const body = call.request.body as {
        readonly portalAlias: string;
        readonly isChildPortal: boolean;
      };

      expect(body.portalAlias).toBe('example.com:8080/site');
      expect(body.isChildPortal).withContext('a parent').toBeFalse();

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('judges only the last segment of a child alias, so its host name may carry punctuation', () => {
      createMode();

      choosePortalType(CHILD_TYPE);
      type('portal-form-alias', 'example.com/mychild');

      expect(fieldMessages())
        .withContext('the host name before the slash is not judged')
        .not.toContain(INVALID_ALIAS_MESSAGE);

      fillAdministrator();
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the child creation');
      const body = call.request.body as {
        readonly portalAlias: string;
        readonly isChildPortal: boolean;
      };

      expect(body.portalAlias).toBe('example.com/mychild');
      expect(body.isChildPortal).withContext('a child').toBeTrue();

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('refuses a child whose own name carries a dot', () => {
      createMode();

      choosePortalType(CHILD_TYPE);
      type('portal-form-alias', 'example.com/my.child');

      expect(fieldMessages())
        .withContext('the dot is in the segment being judged')
        .toContain(INVALID_ALIAS_MESSAGE);
    });

    it('refuses a child whose own name carries a colon', () => {
      createMode();

      choosePortalType(CHILD_TYPE);
      type('portal-form-alias', 'example.com/my:child');

      expect(fieldMessages()).toContain(INVALID_ALIAS_MESSAGE);
    });

    it('refuses a space in either kind', () => {
      createMode();

      choosePortalType(PARENT_TYPE);
      type('portal-form-alias', 'contoso example');
      expect(fieldMessages()).withContext('as a parent').toContain(INVALID_ALIAS_MESSAGE);

      choosePortalType(CHILD_TYPE);
      type('portal-form-alias', 'example.com/contoso child');
      expect(fieldMessages()).withContext('as a child').toContain(INVALID_ALIAS_MESSAGE);
    });

    // MIGRATION: this is a defect deliberately not reproduced.
    it('reports the character rule exactly once however many characters offend it', () => {
      createMode();

      // Four offending characters: three spaces and an exclamation mark.
      type('portal-form-alias', 'a b c!');

      const repeats: readonly string[] = fieldMessages().filter(
        (sentence: string) => sentence === INVALID_ALIAS_MESSAGE,
      );

      expect(repeats).withContext('one offence, one sentence').toHaveSize(1);
    });

    it('re-judges the alias when the kind changes, without anybody retyping', () => {
      createMode();

      choosePortalType(CHILD_TYPE);
      type('portal-form-alias', 'example.com/my.child');
      expect(fieldMessages()).withContext('refused as a child').toContain(INVALID_ALIAS_MESSAGE);

      choosePortalType(PARENT_TYPE);

      expect(fieldMessages()).withContext('the stale verdict is gone').not.toContain(
        INVALID_ALIAS_MESSAGE,
      );
      expect(fieldMessages()).withContext('re-judged against the new value').toContain(
        ALIAS_REQUIRED_MESSAGE,
      );
    });
  });

  // Seeding the alias from the browsed host

  describe('seeding the alias from the browsed host', () => {
    it('seeds a child alias with the browsed host and a separator', () => {
      createMode();

      choosePortalType(CHILD_TYPE);

      expect(aliasValue()).toBe(`${STUBBED_HOST}/`);
    });

    it('clears the alias when the kind returns to parent', () => {
      createMode();

      choosePortalType(CHILD_TYPE);
      expect(aliasValue()).withContext('seeded').not.toBe('');

      choosePortalType(PARENT_TYPE);

      expect(aliasValue()).withContext('cleared').toBe('');
    });

    it('accepts the seeded host once a child name is appended to it', () => {
      createMode();

      choosePortalType(CHILD_TYPE);
      type('portal-form-alias', `${aliasValue()}contoso`);
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the child creation');
      const body = call.request.body as {
        readonly portalAlias: string;
        readonly isChildPortal: boolean;
      };

      expect(body.portalAlias).toBe(`${STUBBED_HOST}/contoso`);
      expect(body.isChildPortal).toBeTrue();

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('leaves an edited portal alone, because the kind is not its to change', () => {
      arriveEditing(5, { portalName: 'Contoso' });

      expect(queryAll('[name="portalType"]')).withContext('no kind to choose').toHaveSize(0);
      expect(query('#portal-form-alias')).withContext('no alias to seed').toBeNull();
      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Contoso');
    });
  });

  // The measured length limits

  describe('the measured length limits', () => {
    it('renders every measured limit on its control', () => {
      createMode();

      RENDERED_LIMITS.forEach(([controlId, limit]: readonly [string, string]) => {
        expect(field<HTMLElement>(controlId).getAttribute('maxlength'))
          .withContext(`the limit on #${controlId}`)
          .toBe(limit);
      });
    });

    it('renders the two editable limits on the edit contract as well', () => {
      arriveEditing(5);

      expect(field<HTMLElement>('portal-form-title').getAttribute('maxlength')).toBe('128');
      expect(field<HTMLElement>('portal-form-description').getAttribute('maxlength')).toBe('500');
      expect(field<HTMLElement>('portal-form-keywords').getAttribute('maxlength')).toBe('500');
    });

    it('refuses an over-long alias and sends nothing', () => {
      createMode();

      fillAdministrator();
      type('portal-form-alias', 'a'.repeat(129));

      expect(fieldMessages()).toContain('Enter at most 128 characters.');

      press(CREATE_SUBMIT_LABEL);

      // Nothing is sent, which the end-of-test verification enforces.
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('accepts an alias of exactly the permitted length', () => {
      createMode();

      const longest = 'a'.repeat(128);

      fillMinimalCreation(longest);
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalAlias: string };

      expect(body.portalAlias).toHaveSize(128);
      expect(body.portalAlias).toBe(longest);

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    // MIGRATION: `signup.ascx` declares `maxlength="20"` on the two credential boxes, and that figure is
    // deliberately not reproduced.
    it('permits a credential far longer than the legacy storage width allowed', () => {
      createMode();

      const long = 'P'.repeat(64);

      type('portal-form-alias', 'contoso.example.test');
      type('portal-form-first-name', 'Ada');
      type('portal-form-last-name', 'Lovelace');
      type('portal-form-username', 'ada');
      type('portal-form-password', long);
      type('portal-form-confirm', long);
      type('portal-form-email', 'ada@example.test');

      expect(fieldMessages()).withContext('sixty-four characters is not too long').toEqual([]);

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly administratorPassword: string };

      expect(body.administratorPassword).toBe(long);

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('refuses a credential past the bound the API itself declares', () => {
      createMode();

      const tooLong = 'P'.repeat(257);

      type('portal-form-password', tooLong);

      expect(fieldMessages()).toContain('Enter at most 256 characters.');
    });

    it('refuses a credential shorter than the measured policy permits', () => {
      createMode();

      // `Website/release.config` declares `minRequiredPasswordLength="7"`. Six is short.
      type('portal-form-password', 'abc123');

      expect(fieldMessages().join(' ')).toContain('at least 7 characters in length');
    });
  });

  // The semantic inversion

  describe('which box fills which member', () => {
    // A swap compiles, submits, and writes every new portal's alias into its title column with no
    // diagnostic anywhere, which is why the two values below are chosen so that neither could be mistaken
    // for the other.
    it('sends the alias box as the alias and the title box as the name, not the reverse', () => {
      createMode();

      type('portal-form-alias', 'alias-value.example.test');
      type('portal-form-title', 'A Distinguishable Title');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as {
        readonly portalAlias: string;
        readonly portalName: string;
      };

      expect(body.portalAlias).withContext('the alias box fills the alias').toBe(
        'alias-value.example.test',
      );
      expect(body.portalName).withContext('the title box fills the name').toBe(
        'A Distinguishable Title',
      );

      // Stated the other way round as well, so the test fails loudly rather than subtly if the two are ever
      // exchanged.
      expect(body.portalName).not.toBe('alias-value.example.test');
      expect(body.portalAlias).not.toBe('A Distinguishable Title');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('labels the two boxes the way the legacy screen labelled them', () => {
      createMode();

      expect(labelNaming('portal-form-alias')).toMatch(/^Portal Alias\b/);
      expect(labelNaming('portal-form-title')).toMatch(/^Title\b/);

      expect(textOf('label[for]').join(' '))
        .withContext('the orphaned label is not used')
        .not.toContain('Portal Name');
    });

    /** The text of the label that names one control. */
    function labelNaming(controlId: string): string {
      const label: HTMLLabelElement | undefined = queryAll<HTMLLabelElement>('label[for]').find(
        (candidate: HTMLLabelElement) => candidate.getAttribute('for') === controlId,
      );

      if (label === undefined) {
        throw new Error(`Expected a label naming #${controlId}, but found none.`);
      }

      return (label.textContent ?? '').trim();
    }

    it('carries the title box back into the name on a replacement too', () => {
      const detail: PortalDetail = arriveEditing(5, { portalName: 'Before' });

      type('portal-form-title', 'After');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');
      const body = call.request.body as { readonly portalName: string };

      expect(body.portalName).withContext('the edited title becomes the name').toBe('After');

      call.flush(envelope({ ...detail, portalName: 'After' }));
      fixture.detectChanges();
      answerListingReread();
    });

    /**
     * ⚠ R-M20 ON THE REPLACEMENT PATH: THE TITLE IS TIDIED HERE TOO, so one resource does not trim where
     * another does.
     */
    it('trims the title into its control on a replacement as well', () => {
      const detail: PortalDetail = arriveEditing(5, { portalName: 'Before' });

      type('portal-form-title', '  Tidied Title  ');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');
      const body = call.request.body as { readonly portalName: string };

      expect(body.portalName).toBe('Tidied Title');
      expect(field<HTMLInputElement>('portal-form-title').value)
        .withContext('written back, so the operator sees what will be sent')
        .toBe('Tidied Title');

      call.flush(envelope({ ...detail, portalName: 'Tidied Title' }));
      fixture.detectChanges();
      answerListingReread();
    });

    /**
     * ⚠ AND A BLANK TITLE IS ACCEPTED ON THIS PATH TOO, WHICH IS THE LEGACY BEHAVIOUR RESTORED. This case
     * used to require the opposite. It refused a whitespace-only title locally, mirroring a `NotEmpty()`
     * the update contract carried on `PortalName` — and BOTH the rule and this case were wrong, so the
     * server rule is withdrawn with them.
     */
    it('accepts a whitespace-only title on a replacement and sends the empty string', () => {
      const detail: PortalDetail = arriveEditing(5, { portalName: 'Before' });

      type('portal-form-title', '   ');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');
      const body = call.request.body as { readonly portalName: string };

      expect(body.portalName)
        .withContext('trimmed on the way out, exactly as a non-blank title is')
        .toBe('');
      expect(fieldMessages())
        .withContext('and no local refusal, because neither tier refuses one')
        .not.toContain('Site Title is required.');

      call.flush(envelope({ ...detail, portalName: '' }));
      fixture.detectChanges();
      answerListingReread();
    });

    /**
     * ⚠ THE REVISION MARKER, WHICH IS THE ONLY THING STANDING BETWEEN THIS PAYLOAD AND A LOST UPDATE. The
     * replacement carries the portal's WHOLE editable state - the three boxes this screen shows plus
     * roughly twenty values carried forward from the read that it never displays - so before the token
     * existed a second administrator saving an older read silently destroyed the first administrator's
     * committed edits to fields neither of them had opened, and the API answered `200` to both.
     */
    describe('the revision marker', () => {
      it('carries the token from the read into the replacement', () => {
        const detail: PortalDetail = arriveEditing(5, {
          concurrencyToken: 'revision-from-the-read',
        });

        type('portal-form-title', 'After');
        press(EDIT_SUBMIT_LABEL);

        const call = expectRequest('PUT', portalUrl(5), 'the replacement');
        const body = call.request.body as { readonly concurrencyToken: string | null };

        // It must come from the record that was READ. Re-reading it immediately before the save would
        // obtain the CURRENT revision, and the check would then always pass while looking watertight.
        expect(body.concurrencyToken).toBe('revision-from-the-read');

        call.flush(envelope({ ...detail, portalName: 'After' }));
        fixture.detectChanges();
        answerListingReread();
      });

      it('offers no form and composes no replacement when the read served no token', () => {
        editMode('5');

        expectRequest('GET', portalUrl(5), 'the detail read').flush(
          envelope({ ...portalDetail(5), concurrencyToken: null }),
        );
        fixture.detectChanges();

        // Nothing to edit, because nothing decoded - and nothing left spinning either.
        expect(query('#portal-form-title'))
          .withContext('no form is offered against a record that did not decode')
          .toBeNull();
        expect(query('app-loading-spinner')).withContext('not left waiting forever').toBeNull();

        // The operator is told, rather than left with a blank panel.
        expect(notifications().length)
          .withContext('the refusal is reported')
          .toBeGreaterThan(0);

        // And the thing this protects: no write of any kind is issued, so no replacement can carry a
        // token that was never read.
        expect(httpMock.match(() => true))
          .withContext('no write is composed from a record that did not decode')
          .toHaveSize(0);
        expect(navigateSpy).not.toHaveBeenCalled();
      });

      it('always carries a token on a replacement, because a read that served none is refused', () => {
        // The positive half of the pair, stated as a total: every replacement this screen can compose
        // carries a marker, because the only path to composing one is a read that decoded.
        const detail: PortalDetail = arriveEditing(5);

        type('portal-form-title', 'After');
        press(EDIT_SUBMIT_LABEL);

        const call = expectRequest('PUT', portalUrl(5), 'the replacement');
        const body = call.request.body as { readonly concurrencyToken: string | null };

        expect(typeof body.concurrencyToken)
          .withContext('a marker, never a null this screen invented and never one it omitted')
          .toBe('string');
        expect(body.concurrencyToken).toBe(detail.concurrencyToken);

        call.flush(envelope({ ...detail, portalName: 'After' }));
        fixture.detectChanges();
        answerListingReread();
      });

      it('sends no token on a creation, because there is no prior revision', () => {
        createMode();
        type('portal-form-title', 'Fresh Portal');
        fillMinimalCreation('fresh.example.test');
        press(CREATE_SUBMIT_LABEL);

        const call = expectRequest('POST', PORTALS_URL, 'the creation');

        expect(call.request.body).not.toEqual(
          jasmine.objectContaining({ concurrencyToken: jasmine.anything() }),
        );

        call.flush(envelope(portalDetail(9)), { status: 201, statusText: 'Created' });
        fixture.detectChanges();
        answerListingReread();
      });

      /**
       * The refusal is reported and the operator's entry is still on screen, so re-reading and
       * re-applying is possible without leaving the application.
       */
      it('reports a refused stale replacement and keeps the entry on screen', () => {
        arriveEditing(5, { concurrencyToken: 'stale' });

        type('portal-form-title', 'Mine');
        press(EDIT_SUBMIT_LABEL);

        expectRequest('PUT', portalUrl(5), 'the replacement').flush(
          {
            type: 'urn:dnnmigration:error:portal.concurrency_conflict',
            title: 'Conflict',
            status: 409,
            detail:
              'Portal 5 was changed by someone else after you read it, so nothing was written. '
              + 'Reload the portal to see the current values, then apply your change again.',
          },
          { status: 409, statusText: 'Conflict' },
        );
        fixture.detectChanges();

        // No listing re-read: the store refreshes the listing only from the success path, and
        // httpMock.verify() in afterEach is what proves no second request went out.
        expect(field<HTMLInputElement>('portal-form-title').value)
          .withContext('the operator does not have to retype their change')
          .toBe('Mine');
      });
    });
  });

  // The request and what comes back

  describe('the request and what comes back', () => {
    it('creates with one request to the collection, and sends the declared members only', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      type('portal-form-title', 'Contoso');
      type('portal-form-description', 'The Contoso tenant.');
      type('portal-form-keywords', 'contoso,tenant');
      fillAdministrator();

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');

      expect(call.request.method).toBe('POST');
      expect(call.request.url).toBe('/api/v1/portals');

      // Compared whole rather than member by member, so an ADDED member fails this too.
      expect(call.request.body).toEqual({
        portalName: 'Contoso',
        portalAlias: 'contoso.example.test',
        description: 'The Contoso tenant.',
        keyWords: 'contoso,tenant',
        homeDirectory: null,
        // Likewise declared and likewise unaskable: the legacy list was built by enumerating the filesystem
        // for `*.template` and no endpoint replaces it.
        templateFile: DEFAULT_TEMPLATE_FILE,
        isChildPortal: false,
        administratorFirstName: 'Ada',
        administratorLastName: 'Lovelace',
        administratorUsername: 'ada',
        administratorPassword: 'Passw0rd!',
        administratorEmail: 'ada@example.test',
      });

      call.flush(envelope(portalDetail(1, { portalName: 'Contoso' })), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: CREATE_SUCCEEDED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('STILL holds unsaved entry while a creation is in flight, so leaving cannot lose it', () => {
      // ⚠ THIS CASE USED TO ASSERT THE OPPOSITE, AND THAT IS THE DEFECT IT NOW PROVES CLOSED. It required
      // the tracker to report CLEAN for as long as the write was in flight, on the reasoning that "work on
      // its way to the server is not unsaved work". It is: the request is bound to this component's lifetime,
      // so navigating away destroys the component, `takeUntilDestroyed` cancels the request, and the write
      // never reaches the database. Reporting clean is what made that departure silent - an operator lost the
      // creation and was told nothing. A screen holding an unfinished write is the LEAST safe moment to
      // leave.
      //
      // The exclusion existed to stop this screen's OWN post-save navigation being challenged, and the case
      // directly above proves that is covered by a better mechanism: the success path navigates with
      // `replaceUrl: true` imperatively, which `unsavedChangesGuard` admits explicitly.
      createMode();

      fillMinimalCreation('contoso.example.test');
      type('portal-form-title', 'Contoso');

      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');

      expect(TestBed.inject(UnsavedChangesTracker).isDirty())
        .withContext('a cancellable write in flight is unsaved work, and is protected as such')
        .toBeTrue();

      call.flush(envelope(portalDetail(1, { portalName: 'Contoso' })), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      expect(TestBed.inject(UnsavedChangesTracker).isDirty())
        .withContext('and the protection is released once the write has landed and the form is settled')
        .toBeFalse();
    });

    it('settles the form and keeps its confirmation once the creation succeeds', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      type('portal-form-title', 'Contoso');

      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(
        envelope(portalDetail(1, { portalName: 'Contoso' })),
        { status: 201, statusText: 'Created' },
      );
      fixture.detectChanges();
      answerListingReread();

      // ⚠ CLEARING `saveRequested` IS WHAT MAKES THIS NECESSARY. The success handler clears it before
      // navigating, so from that moment the probe sees a dirty form with no save in flight - and the
      // navigation it is about to request is the save's own.
      expect(TestBed.inject(UnsavedChangesTracker).isDirty())
        .withContext('the entry is stored, so there is nothing to ask about')
        .toBeFalse();

      // ⚠ AND THE CONFIRMATION SURVIVES THE NAVIGATION IT IS RAISED WITH. The shell retires notifications
      // on a completed navigation, so a confirmation announced in the same task as the departure was swept
      // before it could be painted - the portal was created and the operator was returned to a listing that
      // said nothing.
      const service = TestBed.inject(NotificationService);
      service.clearOnNavigation();

      expect(service.notifications().map((entry) => entry.message))
        .withContext('the listing is where the written row - and its confirmation - is read')
        .toEqual([CREATE_SUCCEEDED_MESSAGE]);

      // A second sweep retires it, so the exemption is exactly one navigation deep and cannot leave a
      // stale confirmation following the operator around.
      service.clearOnNavigation();

      expect(service.notifications()).withContext('one navigation, not forever').toHaveSize(0);
    });

    it('replaces with one request to the record, carrying every untouched member forward verbatim', () => {
      const detail: PortalDetail = arriveEditing(5);

      type('portal-form-title', 'Renamed');
      type('portal-form-description', 'A new description.');
      type('portal-form-keywords', 'new,keywords');

      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');

      expect(call.request.method).toBe('PUT');
      expect(call.request.url).toBe('/api/v1/portals/5');

      expect(call.request.body).toEqual({
        portalId: 5,
        portalName: 'Renamed',
        description: 'A new description.',
        keyWords: 'new,keywords',
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
        // Never round-tripped: a credential is not echoed back by a read, so echoing one forward would be
        // inventing it. The contract carries the member; this screen says it has none.
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
        concurrencyToken: detail.concurrencyToken,
      });

      call.flush(envelope({ ...detail, portalName: 'Renamed' }));
      fixture.detectChanges();
      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: UPDATE_SUCCEEDED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('addresses the record by the identifier it arrived with, sentinels included', () => {
      const detail: PortalDetail = arriveEditing(0);

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(0), 'the replacement of portal zero');
      const body = call.request.body as { readonly portalId: number };

      expect(call.request.url).toBe('/api/v1/portals/0');
      expect(body.portalId).withContext('zero, not coerced away').toBe(0);

      call.flush(envelope(detail));
      fixture.detectChanges();
      answerListingReread();
    });

    it('treats a created portal whose identifier is a sentinel as the success it is', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(envelope(portalDetail(-1)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: CREATE_SUCCEEDED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('treats a created portal identified by zero as the success it is', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(envelope(portalDetail(0)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: CREATE_SUCCEEDED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE], {
        queryParams: {},
        replaceUrl: true,
      });
    });

    it('reports a duplicate alias in the measured wording and keeps the entry', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(
        problem('portal.alias_duplicate', 409, 'That alias is taken.'),
        { status: 409, statusText: 'Conflict' },
      );
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'error', message: DUPLICATE_ALIAS_MESSAGE }]);

      // Nobody is taken away from an entry they can still correct.
      expect(navigateSpy).not.toHaveBeenCalled();
      expect(aliasValue()).toBe('contoso.example.test');

      // Runtime testing found the recovery path itself sound - the entry survives, every control stays
      // editable and the submit stays enabled - but the refusal reached only the banner and the toast.
      expect(fieldMessages()).toEqual([DUPLICATE_ALIAS_MESSAGE]);

      const alias = field<HTMLInputElement>('portal-form-alias');

      expect(alias.getAttribute('aria-invalid')).toBe('true');

      expect(document.activeElement).toBe(alias);
    });

    it('does not move focus for a refusal no single field can be blamed for', () => {
      const held: PortalDetail = arriveEditing(5);
      const before = document.activeElement;

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      expectRequest('PUT', portalUrl(5), 'the replacement').flush(
        problem('portal.host_field_forbidden', 403, 'Host-administered fields may not change.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      // The banner is an assertive live region and is announced regardless, so taking focus for a failure
      // with no field to correct would move an operator away from what they were reading and tell them
      // nothing. Nothing is marked invalid either - no field is at fault.
      expect(document.activeElement).toBe(before);
      expect(fieldMessages()).toEqual([]);
      expect(held.portalId).toBe(5);
    });

    it('reports a refused host-administered change as a warning, and does not mistake it for a lapsed session', () => {
      const detail: PortalDetail = arriveEditing(5);

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      expectRequest('PUT', portalUrl(5), 'the replacement').flush(
        problem('portal.host_field_forbidden', 403, 'Host-administered fields may not change.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();

      expect(notifications()).toEqual([
        { severity: 'warning', message: HOST_FIELD_REFUSED_MESSAGE },
      ]);

      // NOT a session message, and NOT a sign-in redirect. Conflating "you may not change this" with "you
      // are no longer signed in" throws away work the operator can still save by leaving the six terms
      // alone.
      const reported: string = notifications()[0]?.message ?? '';

      expect(reported.toLowerCase()).not.toContain('expired');
      expect(reported.toLowerCase()).not.toContain('session');
      expect(reported.toLowerCase()).not.toContain('log in');
      expect(reported.toLowerCase()).not.toContain('logged in');
      expect(navigations()).withContext('nobody is sent to sign in again').toEqual([]);

      // The edit survives, so the operator can retry without retyping.
      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Renamed');
      expect(detail.portalName).withContext('the stored record is untouched').toBe(
        'Baseline Portal',
      );
    });

    it('shows each per-field server message beside the control the server named', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(
        problem('validation.failed', 400, 'One or more fields are invalid.', {
          PortalAlias: ['That alias is not permitted here.'],
          AdministratorEmail: ['That mail address is already registered.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const shown: readonly string[] = fieldMessages();

      expect(shown).toContain('That alias is not permitted here.');
      expect(shown).toContain('That mail address is already registered.');

      // Each beside its own control rather than pooled at the top of the form.
      expect(messageBesideControl('portal-form-alias')).toContain(
        'That alias is not permitted here.',
      );
      expect(messageBesideControl('portal-form-email')).toContain(
        'That mail address is already registered.',
      );
      expect(messageBesideControl('portal-form-username')).withContext(
        'a field the server did not name',
      ).toEqual([]);

      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('states a record that has gone once, in the shared wording, and offers the one way out', () => {
      editMode('404');

      expectRequest('GET', portalUrl(404), 'the detail read').flush(
        problem('portal.not_found', 404, 'No such portal.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      // ⚠ ONE STATEMENT, AND IT IS THE BANNER'S. This screen used to state it twice - in the banner from
      // the server's own document, and again in a toast carrying a support reference for an occurrence
      // nobody can look up. A record that is not there is a legitimate state rather than a fault, so no
      // notification is raised and no reference is quoted.
      expect((query('.error-banner')?.textContent ?? '')).toContain(PORTAL_NOT_FOUND_MESSAGE);
      expect((query('.error-banner')?.textContent ?? '')).not.toContain('No such portal.');
      expect(query('.error-banner__trace')).toBeNull();
      expect(notifications()).toEqual([]);

      // Nothing to edit is offered, and nothing is thrown: the screen simply says so.
      expect(query('#portal-form-title')).toBeNull();
      expect(query('app-loading-spinner')).withContext('not left waiting forever').toBeNull();

      // ⚠ AND THE TWO SIBLING ACTIONS ARE WITHHELD. Both address the portal by its identifier, so on a
      // portal that has gone they were two links that could only fail; the slot they vacated carries the
      // one recovery action instead.
      const headerActions = Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>(
          'app-page-header a.page-action',
        ),
      );

      expect(headerActions.length).toBe(1);
      expect(headerActions[0]?.textContent?.trim()).toBe('Back to Portals');
      expect(headerActions[0]?.getAttribute('href')).toBe('/portals');
    });

    it('reports a server fault in the measured wording, and never repeats what the server said', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      const leaked =
        'System.Data.SqlClient.SqlException: Violation of PRIMARY KEY constraint ' +
        "'PK_Users' at AddUser, line 42";
      const fault: RawFault = { message: leaked };

      expectRequest('POST', PORTALS_URL, 'the creation').flush(fault, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'error', message: CREATE_ERROR_MESSAGE }]);

      // The thrown text reaches neither the notification nor the document.
      expect(notifications()[0]?.message ?? '').not.toContain('SqlException');
      expect(notifications()[0]?.message ?? '').not.toContain('PK_Users');
      expect(host().textContent ?? '').withContext('nor the rendered page').not.toContain(
        'SqlException',
      );
      expect(navigateSpy).not.toHaveBeenCalled();
    });

    it('reports a refused replacement without taking anybody away from their edit', () => {
      arriveEditing(5);

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      expectRequest('PUT', portalUrl(5), 'the replacement').flush(
        problem('portal.update_failed', 500, 'Something went wrong.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(navigateSpy).not.toHaveBeenCalled();
      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Renamed');
    });

    it('withholds the submit command while a write is outstanding, so nothing is sent twice', () => {
      arriveEditing(5);

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');

      fixture.detectChanges();

      expect(button(EDIT_SUBMIT_LABEL)?.disabled).withContext('held while in flight').toBeTrue();

      call.flush(envelope(portalDetail(5, { portalName: 'Renamed' })));
      fixture.detectChanges();
      answerListingReread();
    });

    it('withholds the submit command until the record has actually been read', () => {
      editMode('5');

      // Nothing has arrived, so there is nothing to replace and the command is inert.
      expect(button(EDIT_SUBMIT_LABEL)?.disabled).toBeTrue();

      answerDetail(portalDetail(5));

      expect(button(EDIT_SUBMIT_LABEL)?.disabled).toBeFalse();
    });

    /** The messages rendered beside one control, found through its own error region. */
    function messageBesideControl(controlId: string): readonly string[] {
      const control: HTMLElement = field<HTMLElement>(controlId);
      const wrapper: Element | null = control.closest('.form-field');

      if (wrapper === null) {
        throw new Error(`Expected #${controlId} to sit inside a labelled field.`);
      }

      return Array.from(wrapper.querySelectorAll('.form-field__error')).map((node) =>
        (node.textContent ?? '').trim(),
      );
    }
  });

  // Which failure sentence belongs to which operation

  /**
   * The last-resort failure sentence is chosen BY MODE. Found at runtime rather than by reading: a portal
   * UPDATE that failed with no server document displayed `Signup.ascx.resx`'s CREATE wording, so it named
   * "the Creation Of Your Portal" for an operation that created nothing and told the operator to check a
   * password "For An Existing User Account" when neither credential control is rendered in edit mode at
   * all.
   */
  describe('which failure sentence belongs to which operation', () => {
    const UPDATE_ERROR_MESSAGE =
      'The portal could not be updated. Nothing was changed. Check the connection and try again.';

    /**
     * A fault carrying no problem document, which is what forces the fallback arm. ⚠ THE SURFACE UNDER
     * TEST IS THE NOTIFICATION, NOT THE BANNER. The two are fed from different places and say different
     * things: the banner renders the problem document, so on a documentless fault it shows a
     * status-derived sentence, while `failureMessage` — the mode- dependent sentence these specs exist
     * for — reaches the operator through the notification queue.
     */
    const DOCUMENTLESS_FAULT: RawFault = { message: 'upstream unavailable' };

    /** The one sentence the failure put on the notification queue. */
    function reportedMessage(): string {
      return notifications()[0]?.message ?? '';
    }

    it('names the UPDATE when an update fails with no document of its own', () => {
      arriveEditing(5);

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      expectRequest('PUT', portalUrl(5), 'the replacement').flush(DOCUMENTLESS_FAULT, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      const message: string = reportedMessage();

      expect(message).withContext('the operation that actually failed').toBe(UPDATE_ERROR_MESSAGE);
      expect(message)
        .withContext('and NOT the creation wording, which names the wrong operation')
        .not.toContain('Creation Of Your Portal');
      expect(message)
        .withContext('nor a password field this mode does not render')
        .not.toContain('Incorrect Password');
    });

    it('still names the CREATION when a creation fails with no document of its own', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(DOCUMENTLESS_FAULT, {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();

      // The legacy resource is reinstated on the path it was authored for, and is unchanged.
      expect(reportedMessage()).toBe(CREATE_ERROR_MESSAGE);
    });

    it('leaves a rejected update to the server, whose per-field messages carry the detail', () => {
      arriveEditing(5);

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      expectRequest('PUT', portalUrl(5), 'the replacement').flush(
        problem('validation.failed', 400, 'One or more fields are invalid.', {
          PortalName: ['That title is not permitted here.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const message: string = reportedMessage();

      expect(message).withContext("the server's own sentence wins").toContain(
        'One or more fields are invalid.',
      );
      expect(message)
        .withContext('no authored sentence competes with it')
        .not.toContain(UPDATE_ERROR_MESSAGE);
    });
  });

  describe('the fields the legacy screen showed that are gone', () => {
    it('offers no template selector and never asks for one', () => {
      createMode();

      const shown: string = host().textContent ?? '';

      expect(shown).not.toContain('Template');
      expect(shown).not.toContain('template');
      expect(shown).withContext('nor the eighth legacy requirement').not.toContain(
        'Please select a template file',
      );
      expect(queryAll('select')).withContext('no selector at all').toHaveSize(0);
    });

    it('offers no home directory and neither of its two commands', () => {
      createMode();

      const shown: string = host().textContent ?? '';

      expect(query('#portal-form-home-directory')).toBeNull();
      expect(shown).not.toContain('Home Directory');
      expect(shown).not.toContain('Customize');
      expect(shown).not.toContain('Auto Generate');
    });

    it('says nothing about demonstration signups, confirmation mail or colliding child paths', () => {
      createMode();

      const shown: string = host().textContent ?? '';

      expect(shown).not.toContain('demo');
      expect(shown).not.toContain('Demo');
      expect(shown).toBeDefined();
      expect(shown).not.toContain('confirmation email');
      expect(shown).not.toContain('already exists on the server');
    });

    it('offers none of the host-administered terms, which belong to the settings screen', () => {
      arriveEditing(5);

      const shown: string = host().textContent ?? '';

      ['Host Fee', 'Host Space', 'Page Quota', 'User Quota', 'Site Log History', 'Expiry'].forEach(
        (term: string) => {
          expect(shown).withContext(`${term} is not offered here`).not.toContain(term);
        },
      );

      // Nor the presentation, navigation or commerce members.
      ['Skin', 'Container', 'Payment', 'Processor', 'Advertising', 'Banner'].forEach(
        (term: string) => {
          expect(shown).withContext(`${term} is not offered here`).not.toContain(term);
        },
      );
    });

    it('sends no member the creation contract does not declare', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const sent: readonly string[] = Object.keys(call.request.body as object);

      // The two members the legacy screen computed for itself have no counterpart in the contract and are
      // not invented here: `strServerPath` came from `GetAbsoluteServerPath(Request)` and `strChildPath`
      // from concatenating it with the alias, both of which described the legacy filesystem layout.
      ['serverPath', 'childPath', 'templatePath'].forEach((member: string) => {
        expect(sent).withContext(`${member} is not a member of the contract`).not.toContain(member);
      });

      // And nothing host-administered rides along on a creation either.
      ['hostFee', 'hostSpace', 'pageQuota', 'userQuota', 'siteLogHistory', 'expiryDate'].forEach(
        (member: string) => {
          expect(sent).withContext(`${member} is not sent on a creation`).not.toContain(member);
        },
      );

      expect(sent).withContext('twelve declared members').toHaveSize(12);

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('sends no confirmation member, because the contract has none to send', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const sent: readonly string[] = Object.keys(call.request.body as object);

      // The confirmation box exists to compare against the credential and for nothing else; `CreatePortal`
      // never had a parameter for it.
      expect(sent).not.toContain('confirm');
      expect(sent).not.toContain('administratorConfirm');
      expect(sent).not.toContain('administratorPasswordConfirmation');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });
  });

  // SENTINEL VALUES

  describe('sentinel values', () => {
    // `Null.NullString` is the EMPTY STRING and not null, so a legacy read could not tell a stored null
    // from a stored empty string. Both arrive here as something the control can show, and an absent member
    // is shown as nothing rather than as the word null.
    it('shows an absent member as an empty control, never as the word null', () => {
      arriveEditing(5, { portalName: null, description: null, keyWords: null });

      expect(field<HTMLInputElement>('portal-form-title').value).toBe('');
      expect(field<HTMLTextAreaElement>('portal-form-description').value).toBe('');
      expect(field<HTMLTextAreaElement>('portal-form-keywords').value).toBe('');
      expect(host().textContent ?? '').not.toContain('null');
    });

    it('shows an empty stored member as empty and sends it back as empty, not as null', () => {
      const detail: PortalDetail = arriveEditing(5, { description: '', keyWords: '' });

      expect(field<HTMLTextAreaElement>('portal-form-description').value).toBe('');

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');
      const body = call.request.body as {
        readonly description: string | null;
        readonly keyWords: string | null;
      };

      // The empty string round-trips AS an empty string. Turning it into null on the way back would change
      // the stored value of a member nobody edited.
      expect(body.description).withContext('an empty string').toBe('');
      expect(body.keyWords).withContext('an empty string').toBe('');
      expect(body.description).not.toBeNull();

      call.flush(envelope(detail));
      fixture.detectChanges();
      answerListingReread();
    });

    it('carries an unset date through a replacement without rendering it', () => {
      const detail: PortalDetail = arriveEditing(5, { expiryDate: NULL_DATE });

      expect(host().textContent ?? '').withContext('never shown as a date').not.toContain(
        '01/01/0001',
      );
      expect(host().textContent ?? '').not.toContain('0001');

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');
      const body = call.request.body as { readonly expiryDate: string | null };

      expect(body.expiryDate).withContext('carried through verbatim').toBe(NULL_DATE);

      call.flush(envelope(detail));
      fixture.detectChanges();
      answerListingReread();
    });

    // MIGRATION: a quota of 0 means UNLIMITED and a quota of -1 means NOT SET. They are different facts and
    // neither is an absence, so neither may be coerced to null or dropped on the way back.
    it('carries an unlimited quota and an unset one through as the distinct values they are', () => {
      const detail: PortalDetail = arriveEditing(5, {
        hostSpace: 0,
        pageQuota: 0,
        userQuota: -1,
        siteLogHistory: -1,
        hostFee: 0,
      });

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');
      const body = call.request.body as {
        readonly hostSpace: number | null;
        readonly pageQuota: number | null;
        readonly userQuota: number | null;
        readonly siteLogHistory: number | null;
        readonly hostFee: number | null;
      };

      expect(body.hostSpace).withContext('unlimited, not absent').toBe(0);
      expect(body.pageQuota).withContext('unlimited, not absent').toBe(0);
      expect(body.userQuota).withContext('not set, not absent').toBe(-1);
      expect(body.siteLogHistory).withContext('not set, not absent').toBe(-1);
      expect(body.hostFee).withContext('nothing to pay, not absent').toBe(0);

      call.flush(envelope(detail));
      fixture.detectChanges();
      answerListingReread();
    });

    it('sends every declared member, present but possibly null, and omits none', () => {
      const detail: PortalDetail = arriveEditing(5, {
        logoFile: null,
        footerText: null,
        currency: null,
        paymentProcessor: null,
        defaultLanguage: null,
      });

      type('portal-form-title', 'Renamed');
      press(EDIT_SUBMIT_LABEL);

      const call = expectRequest('PUT', portalUrl(5), 'the replacement');
      const body = call.request.body as Record<string, unknown>;

      // A null member is PRESENT AND NULL rather than missing, because the API serialises with its
      // ignore-condition set to never and its own reader distinguishes the two.
      ['logoFile', 'footerText', 'currency', 'paymentProcessor', 'defaultLanguage'].forEach(
        (member: string) => {
          expect(Object.keys(body))
            .withContext(`${member} is present`)
            .toContain(member);
          expect(body[member]).withContext(`${member} is null`).toBeNull();
        },
      );

      call.flush(envelope(detail));
      fixture.detectChanges();
      answerListingReread();
    });
  });

  // Structure, typing and reach

  // WHICH PORTAL, AND HOW TO REACH ITS SIBLINGS
  // ⚠ THE HEADING IDENTIFIES NOTHING ON ITS OWN. It reads "Edit Portals" for every tenant, and the three
  // controls beneath it are a title, a description and a keyword list — so an operator arriving from a
  // bookmark had nothing on screen saying WHICH tenant they were about to rewrite, only a number in the
  // address bar.

  describe('identifying the portal and reaching its siblings', () => {
    /** Every action projected into the shared header, in document order. */
    function headerActions(): readonly HTMLAnchorElement[] {
      return queryAll<HTMLAnchorElement>('app-page-header a');
    }

    /** The addresses those actions carry, in document order. */
    function headerAddresses(): readonly string[] {
      return headerActions().map((action) => action.getAttribute('href') ?? '');
    }

    /** The whole header's trimmed text. */
    function headerText(): string {
      return (required<HTMLElement>('app-page-header').textContent ?? '').trim();
    }

    it('shows the stored name beside the heading', () => {
      arriveEditing(5, { portalName: 'Contoso Intranet' });

      expect(headerText()).toContain('Edit Portals');
      expect(headerText()).toContain('Contoso Intranet');
    });

    it('renders no name at all while the portal is still being read', () => {
      // Nothing is invented to fill the gap and no placeholder is shown: the shared header
      // collapses an absent subtitle, so the heading simply stands alone until the record lands.
      editMode('5');

      expect(headerText()).not.toContain('Contoso');

      answerDetail(portalDetail(5, { portalName: 'Contoso' }));

      expect(headerText()).toContain('Contoso');
    });

    it('renders no name for a stored value that is blank or only whitespace', () => {
      // A blank line under the heading would occupy space and add a node with no accessible name.
      arriveEditing(5, { portalName: '   ' });

      expect(query('.page-header__subtitle')).toBeNull();
    });

    it('links to both siblings, at addresses built from the portal in the address', () => {
      arriveEditing(5);

      expect(headerAddresses()).toEqual(['/portals/5/settings', '/portals/5/aliases']);
      expect(textOf('app-page-header a')).toEqual(['Site Settings', 'Portal Aliases']);
    });

    it('builds those addresses correctly for the two sentinel identifiers', () => {
      // `Portals.PortalID` is `IDENTITY (-1, 1)`, so 0 is the first real tenant and -1 is a real tenant as
      // well as the legacy absent-marker.
      arriveEditing(0);
      expect(headerAddresses()).toEqual(['/portals/0/settings', '/portals/0/aliases']);

      arriveEditing(-1);
      expect(headerAddresses()).toEqual(['/portals/-1/settings', '/portals/-1/aliases']);
    });

    it('offers neither sibling in creation mode, where neither exists yet', () => {
      // A link composed from an absent identifier would read `/portals/undefined/aliases`, which
      // the router resolves to the wildcard entry - a link that goes nowhere and reports nothing.
      createMode();

      expect(headerActions()).toHaveSize(0);
      expect(headerText()).toContain('Add New Portal');
    });

    it('keeps the sibling links out of the form\u2019s own action row', () => {
      // Three affordances that must not merge: page-level navigation belongs in the header slot, while
      // submit and cancel belong to the form footer.
      arriveEditing(5);

      expect(required<HTMLElement>('.portal-form__actions').querySelectorAll('a')).toHaveSize(0);
      expect(headerActions()).toHaveSize(2);
    });
  });

  describe('structure, typing and reach', () => {
    it('renders a newly read record only when change detection runs', () => {
      editMode('5');

      expectRequest('GET', portalUrl(5), 'the detail read').flush(
        envelope(portalDetail(5, { portalName: 'Contoso' })),
      );

      // Deliberately no render here.
      expect(query('#portal-form-title')).withContext('nothing rendered yet').toBeNull();

      fixture.detectChanges();

      expect(field<HTMLInputElement>('portal-form-title').value)
        .withContext('rendered once checked')
        .toBe('Contoso');
    });

    it('reads back every control as its declared type rather than as null', () => {
      createMode();

      // Only the insisted-upon members are filled; the three descriptive ones are left exactly as the form
      // declared them.
      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as Record<string, unknown>;

      ['portalName', 'description', 'keyWords'].forEach((member: string) => {
        expect(typeof body[member]).withContext(`${member} is a string`).toBe('string');
        expect(body[member]).withContext(`${member} is not null`).not.toBeNull();
      });

      // The kind is a declared choice and reads back as a boolean decision, never as null.
      expect(typeof body['isChildPortal']).toBe('boolean');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('leaves for the listing when abandoned, sending nothing and complaining about nothing', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');

      press(CANCEL_LABEL);

      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE], { queryParams: {} });

      // Abandoning is not a submission: no request is issued, which the end-of-test verification enforces,
      // and nothing is marked as being at fault.
      expect(fieldMessages()).withContext('no complaints raised on the way out').toEqual([]);
      expect(notifications()).toEqual([]);
    });

    // ⚠ THE PLACE THE OPERATOR CAME FROM, WHICH USED TO BE THROWN AWAY. Every departure from this screen was
    // a bare `navigate([PORTAL_LIST_ROUTE])` carrying no query parameters, so an operator who had filtered
    // and paged to reach a record was returned to page one of an unfiltered listing - and after a SAVE that
    // is worse than an inconvenience, because the row that was just written is not on the page they land on.
    // The two specs above assert the empty-bag case (no listing visited); these assert a real coordinate.
    it('carries the listing\u2019s own place back when abandoning', () => {
      TestBed.inject(ListReturnStore).remember(PORTAL_LIST_ROUTE, {
        currentpage: '4',
        filter: 'Q',
        sortby: 'portalName',
        sortdir: 'desc',
      });

      createMode();
      press(CANCEL_LABEL);

      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE], {
        queryParams: { currentpage: '4', filter: 'Q', sortby: 'portalName', sortdir: 'desc' },
      });
    });

    it('carries the listing\u2019s own place back after a successful save', () => {
      TestBed.inject(ListReturnStore).remember(PORTAL_LIST_ROUTE, { currentpage: '3', filter: 'D' });

      createMode();
      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(envelope(portalDetail(1)), {
        status: 201,
        statusText: 'Created',
      });
      fixture.detectChanges();
      answerListingReread();

      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE], {
        queryParams: { currentpage: '3', filter: 'D' },
        // Replaced rather than pushed, so Back cannot return to a form for a record already written.
        replaceUrl: true,
      });
    });

    it('leaves an edit for the listing when abandoned, sending nothing', () => {
      arriveEditing(5);

      type('portal-form-title', 'Renamed');
      press(CANCEL_LABEL);

      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE], { queryParams: {} });
      expect(fieldMessages()).toEqual([]);
    });

    it('offers both commands as real buttons with their kind declared', () => {
      createMode();

      // The abandon command is a plain button and NOT a submit, so pressing it leaves without first putting
      // the form through validation it does not need to pass.
      expect(button(CREATE_SUBMIT_LABEL)?.getAttribute('type')).toBe('submit');
      expect(button(CANCEL_LABEL)?.getAttribute('type')).toBe('button');
      expect(queryAll('[onclick]')).withContext('no inline handlers').toHaveSize(0);
    });

    it('emits no landmark and exactly one heading, because the surrounding shell owns both', () => {
      createMode();

      expect(queryAll('main, nav, header, footer')).withContext('no landmark').toHaveSize(0);
      expect(queryAll('h1')).withContext('one heading').toHaveSize(1);
    });

    it('names every control with a real label', () => {
      createMode();

      const targets: readonly string[] = queryAll<HTMLLabelElement>('label[for]').map(
        (label: HTMLLabelElement) => label.getAttribute('for') ?? '',
      );

      RENDERED_LIMITS.forEach(([controlId]: readonly [string, string]) => {
        expect(targets).withContext(`#${controlId} is named by a label`).toContain(controlId);
      });

      // The two kind choices are labelled too, and each label points at its own choice.
      expect(targets).toContain(`portal-form-portal-type-${PARENT_TYPE}`);
      expect(targets).toContain(`portal-form-portal-type-${CHILD_TYPE}`);
    });

    it('ties each complaint to the control it is about, so it is announced with it', () => {
      createMode();

      press(CREATE_SUBMIT_LABEL);

      const wrapper: Element | null = field<HTMLElement>('portal-form-alias').closest('.form-field');

      if (wrapper === null) {
        throw new Error('Expected the alias to sit inside a labelled field.');
      }

      const region: Element | null = wrapper.querySelector('.form-field__errors');
      const group: Element | null = wrapper.querySelector('.form-field__control');

      expect(region).withContext('the complaint has a region').not.toBeNull();
      expect(group).withContext('the control has a group').not.toBeNull();

      const regionId: string = region?.getAttribute('id') ?? '';
      const describedBy: string = group?.getAttribute('aria-describedby') ?? '';

      expect(regionId).withContext('the region is addressable').not.toBe('');
      expect(describedBy.split(/\s+/))
        .withContext('and the control points at it')
        .toContain(regionId);
      expect(region?.getAttribute('role')).toBe('alert');
    });

    it('keeps a live region mounted with nothing to announce until there is', () => {
      createMode();

      const region: Element = required<Element>('.error-banner-live');

      expect(region.getAttribute('aria-live')).withContext('announced when it changes').toBe(
        'assertive',
      );
      expect(region.getAttribute('role')).toBe('alert');
      expect(query('.error-banner__message')).withContext('nothing to announce yet').toBeNull();
    });

    it('announces a server failure document through that region', () => {
      createMode();

      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      expectRequest('POST', PORTALS_URL, 'the creation').flush(
        problem('validation.failed', 400, 'One or more fields are invalid.', {
          PortalAlias: ['That alias is not permitted here.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(required<Element>('.error-banner-live').getAttribute('aria-live')).toBe('assertive');
      expect(textOf('.error-banner__message').join(' ')).toContain(
        'One or more fields are invalid.',
      );
    });

    it('paints the two credential controls as credentials', () => {
      createMode();

      expect(field<HTMLInputElement>('portal-form-password').type).toBe('password');
      expect(field<HTMLInputElement>('portal-form-confirm').type).toBe('password');
      expect(field<HTMLInputElement>('portal-form-password').getAttribute('autocomplete')).toBe(
        'new-password',
      );
    });

    it('groups the two field sets the legacy section heads delimited', () => {
      createMode();

      // MIGRATION: `signup.ascx` and declared two `dnn:sectionhead` controls. Neither collapsed anything
      // that mattered, so each becomes a semantic field set carrying the measured legend wording verbatim.
      expect(textOf('legend')).toEqual(['Portal Setup', 'Security Settings']);
    });
  });
});
