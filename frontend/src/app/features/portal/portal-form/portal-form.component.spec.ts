/**
 * @fileoverview Proofs for the portal creation and edit screen.
 *
 * WHAT THIS FILE IS FOR. The screen it exercises replaces two legacy Web Forms pages
 * at once — `Website/admin/Portal/signup.ascx` for creation and
 * `Website/admin/Portal/SiteSettings.ascx` for editing — and the migration discipline
 * that governs the port requires that validation rules MATCH and that error messages be
 * EQUIVALENT rather than merely similar. Every sentence asserted below was read out of
 * the legacy resource files in this checkout, so a reviewer can check each expectation
 * against its measured source instead of taking the wording on trust.
 *
 * Four of the tests below exist because the mistake they catch COMPILES CLEANLY and
 * looks plausible in review. They are called out here so nobody deletes one as
 * redundant:
 *
 *   1. `portalId = 0` selects the EDIT contract. `Portals.PortalID` is
 *      `IDENTITY(-1,1)`, so the FIRST REAL PORTAL HAS THE ID `0`. A truthiness test
 *      (`if (portalId)`) reads that portal as absent and offers a creation form for a
 *      record that already exists.
 *   2. `portalId = -1` also selects the EDIT contract. `-1` is simultaneously a
 *      legitimate identifier AND the legacy `Null.NullInteger` sentinel, so a
 *      lower-bound test (`portalId > 0`) makes the baseline portal uneditable.
 *   3. The invalid-alias sentence is emitted EXACTLY ONCE however many characters
 *      offend. The legacy loop appended it once PER offending character
 *      (`Signup.ascx.vb:L209-L214`), so an alias of `a b c!` produced four copies of the
 *      same sentence. That is a defect, it is deliberately not reproduced, and only a
 *      count assertion keeps it from creeping back.
 *   4. The alias and the title reach the OPPOSITE request members from the ones their
 *      names suggest. See the semantic-inversion note immediately below.
 *
 * THE SEMANTIC INVERSION, AND WHY IT HAS ITS OWN TEST. The legacy `txtPortalName` box
 * collected the ALIAS and the legacy `txtTitle` box collected the NAME. Four
 * independent measurements agree:
 *
 *   * `Library/Components/Portal/PortalController.vb:L980` declares `CreatePortal` with
 *     `PortalName` FIRST and `PortalAlias` twelfth;
 *   * `Website/admin/Portal/Signup.ascx.vb:L274` passes `txtTitle.Text` into that first
 *     position, while `strPortalAlias` — taken from `txtPortalName.Text` — goes into the
 *     `PortalAlias` position;
 *   * `signup.ascx:L39-L41` binds the label `plPortalAlias` ("Portal Alias:") to
 *     `controlname="txtPortalName"`;
 *   * `SiteSettings.ascx.resx` labels `txtPortalName` as `plPortalName.Text` = "Title:".
 *
 * A swap therefore compiles, submits, and writes every new portal's alias into its title
 * column with no diagnostic anywhere. The test named for it fills the two boxes with
 * values that cannot be confused for one another and pins each to its correct member.
 *
 * HOW FAILURES ARE OBSERVED. This screen reports a refused write through the injected
 * notification service and not by printing a sentence of its own into the form, so the
 * message assertions read the recorded notification. The per-field messages the server
 * returns in an RFC 7807 document DO land in the document, beside the control the server
 * named, and are read from there.
 *
 * WHAT IS DELIBERATELY NOT MOCKED. The store and the API service are real, and every
 * request is intercepted at the HTTP boundary. That is what lets these tests prove the
 * METHOD, the URL and the BODY of each call rather than merely that some collaborator
 * was invoked — a service double would have happily accepted the inverted payload
 * described above. The addresses asserted are RELATIVE (`/api/v1/...`) because the test
 * target declares no configuration-file replacement and therefore compiles against the
 * deployed configuration, whose base address is relative so that the reverse proxy in
 * front of both containers can serve the browser from a single origin.
 *
 * NO USER-SPECIFIED RULES EXIST for this project; the rules document is the single line
 * "No user rules provided." Nothing here is written to satisfy an invented rule, and the
 * absence of rules is not treated as licence to assert less.
 */

import { DOCUMENT } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { BannerAdvertisingMode, UserRegistrationMode } from '../../../core/models/portal.model';
import { NotificationService } from '../../../core/services/notification.service';
import { PortalStore } from '../../../core/state/portal.store';
import { PortalFormComponent } from './portal-form.component';

import type { TestRequest } from '@angular/common/http/testing';
import type { ComponentFixture } from '@angular/core/testing';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { PortalDetail, PortalListItem } from '../../../core/models/portal.model';
import type { ProblemDetails, ProblemDetailsErrors } from '../../../core/models/problem-details.model';

// =============================================================================
//  ADDRESSES
// =============================================================================

/**
 * The portal collection, addressed exactly as the deployed application addresses it.
 *
 * RELATIVE BY REQUIREMENT, NOT BY CONVENIENCE. The test target in `angular.json`
 * declares no `fileReplacements`, so a spec compiles against the deployed configuration
 * file under `src/environments/`
 * rather than the development one. Its base address is
 * the relative `/api/v1`, because the reverse proxy forwards `/api/` to the API container
 * and the browser must therefore reach the API through the same origin that served the
 * application. An absolute address asserted here would pass while describing a
 * configuration the deployed system does not use.
 */
const PORTALS_URL = '/api/v1/portals';

/** One portal, addressed by identifier. Interpolated so a sentinel identifier survives. */
function portalUrl(portalId: number): string {
  return `${PORTALS_URL}/${portalId}`;
}

/** Where both a completed write and an abandoned one lead. */
const PORTAL_LIST_ROUTE = '/portals';

// =============================================================================
//  THE HOST NAME THIS SCREEN SEEDS A CHILD ALIAS FROM
// =============================================================================

/**
 * The host name the stubbed document reports.
 *
 * Deliberately NOT the real browsing context. Karma serves the suite from an
 * arbitrary port, so an assertion against the real host would be checking a value the
 * runner chose rather than the value the component derived, and it would read
 * differently on the next run. Substituting the document lets the prefill be asserted
 * exactly — see the note on {@link documentReporting}.
 */
const STUBBED_HOST = 'portals.example.test';

/**
 * A document that reports {@link STUBBED_HOST} and behaves like the real one otherwise.
 *
 * The component reads `inject(DOCUMENT).location.host`, so only `location` needs
 * substituting — but the test harness renders the fixture into that SAME document, so a
 * bare object carrying nothing but a host name would break rendering outright. The
 * proxy therefore intercepts `location` alone and forwards every other member to the
 * genuine document.
 *
 * Two details make the forwarding correct rather than merely plausible, and both were
 * confirmed by running the suite rather than reasoned about in the abstract:
 *
 *   * members are read with the real document as the RECEIVER, so native accessors such
 *     as `body` and `documentElement` resolve against real internal state instead of
 *     against the proxy;
 *   * methods are bound to the real document before being handed back, because a native
 *     method invoked with the proxy as its `this` throws an illegal-invocation error.
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

// =============================================================================
//  MEASURED WORDING
// =============================================================================
//
// Restated here rather than imported from the component on purpose. A test that imports
// the sentence it asserts proves only that a constant equals itself; these copies were
// transcribed from the legacy resource files, so if the component's wording drifts the
// assertion fails and a human decides whether the drift was intended.

/** `Signup.ascx.resx` : `PortalSetup.Text` and the two command labels for creation. */
const CREATE_HEADING = 'Add New Portal';
const CREATE_SUBMIT_LABEL = 'Create Portal';

/** `SiteSettings.ascx.resx` : the edit screen's own heading and command label. */
const EDIT_HEADING = 'Edit Portals';
const EDIT_SUBMIT_LABEL = 'Update';

/** Shared by both contracts. */
const CANCEL_LABEL = 'Cancel';

/**
 * The seven required-field sentences, measured from `Signup.ascx.resx`.
 *
 * ALL EIGHT STORED VALUES OPEN WITH BREAK MARKUP — `<br>Portal Name Is Required.` and so
 * on — because a Web Forms validator rendered inline after a postback and its author
 * expressed vertical spacing as content. The markup is stripped on the way across:
 * spacing is the stylesheet's concern. The tests below assert both halves of that
 * decision, the wording AND the absence of the markup.
 *
 * The first sentence says "Portal Name" although the box it guards is the ALIAS. That is
 * the measured legacy wording, it is what the operator read, and functional parity means
 * carrying it verbatim rather than correcting it.
 */
const ALIAS_REQUIRED_MESSAGE = 'Portal Name Is Required.';
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

/** `Signup.ascx.resx` : `InvalidName.Text`. */
const INVALID_ALIAS_MESSAGE = 'The Portal Name Must Not Contain Spaces Or Punctuation.';

/** `Signup.ascx.resx` : `InvalidPassword.Text`. */
const PASSWORD_MISMATCH_MESSAGE = 'The Password Values Entered Do Not Match.';

/**
 * `Signup.ascx.resx` : `CreateError.Text`, carried across verbatim.
 *
 * The legacy handler did NOT show this sentence for a failed creation. It caught the
 * exception and assigned `strMessage = ex.Message` (`Signup.ascx.vb:L275-L278`), putting
 * whatever the server threw — a stack-bearing database message included — in front of
 * whoever was signing up. This sentence was already sitting in the resource file for the
 * purpose, so showing it instead is both closer to the screen's evident intent and a
 * refusal to leak internals.
 */
const CREATE_ERROR_MESSAGE =
  'An Error Was Encountered During The Creation Of Your Portal. This May Have Been ' +
  'Caused By Specifying An Incorrect Password For An Existing User Account. Please ' +
  'Verify Your Details Before You Try Again.';

/**
 * The sentence shown when the server refuses a change to a host-administered term.
 *
 * NET-NEW WORDING FOR A MEASURED REFUSAL. `SiteSettings.ascx.vb:L759-L769` compares the
 * six host-administered fields — host fee, host space, page quota, user quota, site-log
 * history and expiry date — against the stored record when the operator is not a host
 * account, and on any difference executes a BARE `Throw New System.Exception` WITH NO
 * MESSAGE AT ALL. There is therefore no legacy sentence to carry across and the target
 * must supply its own.
 */
const HOST_FIELD_REFUSED_MESSAGE =
  'Only a host account may change this portal\u2019s host-administered terms, so the save ' +
  'was refused. Nothing was changed.';

/** The sentence shown when the record being edited has gone. */
const PORTAL_NOT_FOUND_MESSAGE = 'That portal no longer exists, so nothing could be loaded.';

/** `CONFLICT_MESSAGE['portal.alias_duplicate']`, measured from the legacy alias screen. */
const DUPLICATE_ALIAS_MESSAGE =
  'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.';

/** Confirmations for a completed write. */
const CREATE_SUCCEEDED_MESSAGE = 'The portal was created.';
const UPDATE_SUCCEEDED_MESSAGE = 'The portal was updated.';

// =============================================================================
//  MEASURED LIMITS AND CODES
// =============================================================================

/**
 * The length limit rendered on each control.
 *
 * Seven of the ten are the figures declared in `signup.ascx` verbatim. THREE DIVERGE, and
 * each divergence is the component's own documented decision rather than a transcription
 * slip, so the measured markup figure is recorded beside it:
 *
 *   * `firstName` and `lastName` are 50 where the markup declares 100. The terminal
 *     column is `nvarchar(50)`, so the markup's figure admitted a value the database
 *     could never store and the write would have refused it anyway.
 *   * `password` and `confirm` are 256 where the markup declares 20. That 20 mirrored the
 *     legacy STORAGE width `Users.Password nvarchar(20)`, not any rule applied to a
 *     credential — the measured legacy password policy declares a minimum of seven and NO
 *     maximum. The successor stores a one-way hash, so the column that produced the 20 no
 *     longer exists, and reproducing it would cap the entropy of every administrator
 *     credential this screen creates at twenty characters.
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

/** The two legacy list-item values, `signup.ascx:L34-L35`. */
const PARENT_TYPE = 'P';
const CHILD_TYPE = 'C';

/** The template the component supplies for the contract member it cannot ask about. */
const DEFAULT_TEMPLATE_FILE = 'Default Website.template';

/** `Null.NullDate`, the legacy sentinel for an unset date. */
const NULL_DATE = '0001-01-01T00:00:00';

// =============================================================================
//  FAILURE DOCUMENTS
// =============================================================================

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

// =============================================================================
//  RECORD FIXTURES
// =============================================================================

/**
 * A portal record, complete.
 *
 * EVERY MEMBER IS PRESENT, and that is the contract rather than fixture pedantry. The
 * client's decoder requires each declared member to be present — an absent one is not the
 * same as a null one and is refused — which mirrors the API serialising with its
 * ignore-condition set to never. So a fixture that omitted the members this screen does
 * not edit would not merely be untidy, it would fail to decode.
 *
 * The default values are the ones a real baseline installation holds, sentinels included:
 * the site-log history and the four tab references sit at `-1`, and the quotas at `0`.
 * Those are meaningful values and not padding — see the tests that carry them through a
 * replacement unchanged.
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

/** The single-record envelope every read and write reply arrives in. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * An empty listing page.
 *
 * Needed because a completed write asks the store to re-read the listing, so a test that
 * flushed only the write itself would leave that second request outstanding and the
 * end-of-test verification would report it.
 */
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
      // A STANDALONE component is IMPORTED. Nothing in this workspace is declared through
      // a wrapping module, so nothing is declared here either.
      imports: [PortalFormComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // The screen navigates on both a completed write and an abandoned one, so the
        // router has to be real enough to be spied on.
        provideRouter([]),
        // Provided explicitly so each test gets its own store rather than sharing one
        // across the suite. The component's own baseline reading of the failure signal
        // is taken at construction, so a store carrying a stale failure would make one
        // test's refusal visible to the next.
        PortalStore,
        {
          provide: DOCUMENT,
          useFactory: (): Document => documentReporting(STUBBED_HOST, document),
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    // Left calling through: the real service records the notification, so a test can
    // assert either the call or the resulting queue and both agree.
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();

    // Resolved rather than called through, so no test depends on a route actually
    // existing in the empty routing table above.
    navigateSpy = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    // Every request this screen issues is asserted. Anything unexpected — a second read,
    // a stray listing refresh, a write that should not have been attempted — is reported
    // here rather than passing unnoticed.
    httpMock.verify();
  });

  // ===========================================================================
  //  ARRIVING AT THE SCREEN
  // ===========================================================================

  /** Arrive with no portal named, which is the creation contract. */
  function createMode(): void {
    fixture = TestBed.createComponent(PortalFormComponent);
    fixture.detectChanges();
  }

  /**
   * Arrive with a portal named.
   *
   * The identifier is passed as the STRING a router would supply, so the component's own
   * parsing is exercised rather than bypassed. That matters for the two sentinel
   * identifiers: `'0'` and `'-1'` have to survive parsing before mode selection can even
   * be tested.
   */
  function editMode(portalId: string): void {
    fixture = TestBed.createComponent(PortalFormComponent);
    fixture.componentRef.setInput('portalId', portalId);
    fixture.detectChanges();
  }

  // ===========================================================================
  //  REQUESTS
  // ===========================================================================

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

  /**
   * Answer the listing re-read a COMPLETED write triggers.
   *
   * Only a completed one: the store refreshes the listing from the success path, so a
   * refused write issues no second request and calling this after one would fail.
   */
  function answerListingReread(): void {
    expectRequest('GET', PORTALS_URL, 'the listing re-read after a write').flush(emptyPage());
    fixture.detectChanges();
  }

  // ===========================================================================
  //  READING THE RENDERED DOCUMENT
  // ===========================================================================

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function query<E extends Element>(selector: string): E | null {
    return host().querySelector<E>(selector);
  }

  function queryAll<E extends Element>(selector: string): readonly E[] {
    return Array.from(host().querySelectorAll<E>(selector));
  }

  /**
   * The element at this selector, or a thrown failure naming what was missing.
   *
   * Throwing rather than asserting-and-continuing keeps the type honest without a
   * non-null assertion, and a missing control produces one legible failure instead of a
   * cascade of null-property errors further down the test.
   */
  function required<E extends Element>(selector: string): E {
    const found: E | null = query<E>(selector);

    if (found === null) {
      throw new Error(`Expected ${selector} to be rendered, but it was not.`);
    }

    return found;
  }

  /** Trimmed text of every element matching the selector. Never raw markup. */
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

  // ===========================================================================
  //  CHOOSING BETWEEN THE TWO CONTRACTS
  // ===========================================================================

  describe('choosing between the two contracts', () => {
    it('offers the creation form and reads nothing when no portal is named', () => {
      createMode();

      // The administrator block belongs to creation alone: an existing portal already has
      // an administrator, and this screen never re-states one.
      expect(query('#portal-form-first-name')).withContext('given name').not.toBeNull();
      expect(query('#portal-form-last-name')).withContext('family name').not.toBeNull();
      expect(query('#portal-form-username')).withContext('sign-in name').not.toBeNull();
      expect(query('#portal-form-password')).withContext('credential').not.toBeNull();
      expect(query('#portal-form-confirm')).withContext('confirmation').not.toBeNull();
      expect(query('#portal-form-email')).withContext('mail address').not.toBeNull();
      expect(query('#portal-form-alias')).withContext('alias').not.toBeNull();

      // No read is issued, which the end-of-test verification enforces — a request of
      // whatever kind would be an unexpected one.
    });

    it('reads the record and offers the edit form for an ordinary portal', () => {
      arriveEditing(5);

      expect(expectHeading()).toBe(EDIT_HEADING);
      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Baseline Portal');
    });

    // MIGRATION: `Portals.PortalID` is `IDENTITY(-1,1)`, so 0 is the FIRST ORDINARY
    // PORTAL and not an absence. A truthiness test on the identifier would offer a
    // creation form for a record that already exists.
    it('reads the record for portal 0, which is an ordinary portal and not an absence', () => {
      editMode('0');

      const call = expectRequest('GET', portalUrl(0), 'the read for portal zero');

      expect(call.request.url).toBe('/api/v1/portals/0');

      call.flush(envelope(portalDetail(0)));
      fixture.detectChanges();

      expect(expectHeading()).withContext('the edit contract, not creation').toBe(EDIT_HEADING);
      expect(query('#portal-form-username')).withContext('no administrator block').toBeNull();
    });

    // MIGRATION: -1 is simultaneously a legitimate identifier and the legacy
    // `Null.NullInteger` sentinel (`Library/Components/Shared/Null.vb`). A lower-bound
    // test on the identifier would make the baseline portal uneditable.
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

      // The host-administered terms, the tab references and the alias are all absent:
      // they belong to the settings screen and to the alias screen respectively.
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

  // ===========================================================================
  //  WHAT THE SCREEN INSISTS ON
  // ===========================================================================

  describe('what the screen insists on', () => {
    it('reports all seven unmet requirements at once, in the measured wording, and sends nothing', () => {
      createMode();

      press(CREATE_SUBMIT_LABEL);

      const shown: readonly string[] = fieldMessages();

      ALL_REQUIRED_MESSAGES.forEach((sentence: string) => {
        expect(shown).withContext(`the measured sentence "${sentence}"`).toContain(sentence);
      });

      // Exactly seven, so nothing extra is being reported and nothing is missing. The
      // eighth legacy validator guarded the template selector, which is gone.
      expect(shown).withContext('seven requirements, no more').toHaveSize(
        ALL_REQUIRED_MESSAGES.length,
      );

      // No write is attempted while a requirement is unmet, which the end-of-test
      // verification enforces.
      expect(navigateSpy).withContext('nobody is taken anywhere').not.toHaveBeenCalled();
    });

    // MIGRATION: all eight measured values in `Signup.ascx.resx` open with break markup —
    // `<br>Portal Name Is Required.` and so on — because a Web Forms validator rendered
    // inline after a postback. The markup is stripped and the wording carried as text.
    it('carries the measured sentences as text, without the break markup they were stored with', () => {
      createMode();

      press(CREATE_SUBMIT_LABEL);

      fieldMessages().forEach((sentence: string) => {
        expect(sentence).withContext('no leading break markup').not.toMatch(/^<br\s*\/?>/i);
        expect(sentence).withContext('no break markup anywhere').not.toContain('<br');
      });

      // Proof the sentences are text rather than parsed markup: had the stored value been
      // bound as raw markup, the break would have become an element in the document.
      expect(queryAll('.form-field__errors br')).withContext('no break elements').toHaveSize(0);
    });

    // MIGRATION: the alias box is labelled "Portal Alias:" but its measured message says
    // "Portal Name Is Required." (`signup.ascx:L41`, `Signup.ascx.resx`). That mismatch is
    // the legacy wording the operator read; parity means keeping it, not correcting it.
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

      // The three blank members travel as the empty strings they are. A nullable control
      // would have sent null here, so this doubles as the proof that every control in the
      // group is declared non-nullable and therefore reads back as its declared type.
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

      // The state is declared, and the rule is not: a native `required` attribute beside a
      // reactive control would install a SECOND required validator, putting one rule in two
      // places. So the marker is an ARIA one.
      expect(queryAll('[aria-required="true"]')).withContext('seven marked controls').toHaveSize(7);
      expect(queryAll('.form-field__required')).withContext('seven visible markers').toHaveSize(7);
      expect(queryAll('[formcontrolname][required]')).withContext('no duplicated rule').toHaveSize(
        0,
      );
    });
  });

  // ===========================================================================
  //  THE PASSWORD CONFIRMATION
  // ===========================================================================

  describe('the password confirmation', () => {
    // MIGRATION: the legacy screen compared the two boxes IMPERATIVELY inside its click
    // handler (`Signup.ascx.vb:L219-L222`) and declared no `asp:CompareValidator` at all —
    // there are zero in the whole file. The rule is now declarative, on the group.
    it('refuses a confirmation that does not match, in the measured wording', () => {
      createMode();

      type('portal-form-alias', 'contoso.example.test');
      type('portal-form-first-name', 'Ada');
      type('portal-form-last-name', 'Lovelace');
      type('portal-form-username', 'ada');
      // SEVEN characters, differing only in the last one. The length matters: the measured
      // legacy policy sets a minimum of seven (`Website/release.config`), so a shorter pair
      // would be refused for being short and the mismatch — the rule under test here —
      // would never be reached. Isolating one rule means satisfying every other.
      type('portal-form-password', 'abc123d');
      type('portal-form-confirm', 'abc123e');
      type('portal-form-email', 'ada@example.test');

      press(CREATE_SUBMIT_LABEL);

      expect(fieldMessages()).toContain(PASSWORD_MISMATCH_MESSAGE);

      // Once only. The rule lives on the group, so it is reported beside the confirmation
      // rather than duplicated onto both credential controls.
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

      // An empty confirmation is unmet rather than wrong, so the screen says so. Reporting
      // a mismatch against a box nobody has typed in yet would be noise.
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

  // ===========================================================================
  //  TIDYING THE ALIAS BEFORE JUDGING IT
  // ===========================================================================

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

    // MIGRATION: `Signup.ascx.vb:L183` reads `txtPortalName.Text = LCase(txtPortalName.Text)`.
    it('lowercases what was entered', () => {
      createMode();

      expect(aliasSentFor('Contoso.Example.TEST')).toBe('contoso.example.test');
    });

    // MIGRATION: `Signup.ascx.vb:L184` reads
    // `Replace(txtPortalName.Text, "http://", "")`. VB's `Replace` removes EVERY
    // occurrence, not merely a leading one, so the successor must do the same.
    it('strips a legacy scheme wherever it appears, not merely at the front', () => {
      createMode();

      // Two occurrences, one of them interior. A leading-only strip would leave the second.
      expect(aliasSentFor('http://a/http://b')).toBe('a/b');
    });

    it('strips the scheme and lowercases together', () => {
      createMode();

      expect(aliasSentFor('HTTP://Contoso.Example.Test')).toBe('contoso.example.test');
    });

    // The ordering is the whole point: uppercase letters and the `:` and `/` of a scheme
    // are not in the permitted set, so an entry that needed tidying would be refused
    // outright if the character rule ran first.
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

    it('writes the tidied alias back, so what is shown is what was sent', () => {
      createMode();

      fillMinimalCreation('HTTP://Contoso.Example.Test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as { readonly portalAlias: string };

      // The box and the payload agree, so nobody is left looking at an entry that differs
      // from the one that was accepted.
      expect(aliasValue()).withContext('what is shown').toBe('contoso.example.test');
      expect(body.portalAlias).withContext('what was sent').toBe(aliasValue());

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });
  });

  // ===========================================================================
  //  WHICH CHARACTERS AN ALIAS MAY CARRY
  // ===========================================================================

  describe('which characters an alias may carry', () => {
    // MIGRATION: `Signup.ascx.vb:L206-L210` builds the permitted set as
    // `abcdefghijklmnopqrstuvwxyz0123456789-` and then executes
    // `If Not blnChild Then strValidChars += "./:"`.
    //
    // NOTE THE POLARITY. The PARENT case is the PERMISSIVE one, because a parent alias
    // carries a host name, optionally a port and optionally a path. Reading the condition
    // the other way round rejects every legitimate parent alias.
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

    // MIGRATION: for a child, `Signup.ascx.vb:L202` takes
    // `Mid(txtPortalName.Text, InStrRev(txtPortalName.Text, "/") + 1)` — the segment after
    // the LAST slash — and judges only that. The preceding host name is not the child's
    // name and is not its to validate.
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

    // MIGRATION: THIS IS A DEFECT DELIBERATELY NOT REPRODUCED. The legacy loop at
    // `Signup.ascx.vb:L209-L214` sits inside `For intCounter = 1 To strPortalAlias.Length`
    // and executes `strMessage &= "<br>" & Localization.GetString("InvalidName", ...)` on
    // EVERY offending character, so an alias of `a b c!` produced FOUR copies of the same
    // sentence stacked on the page. The rule is now reported once, as one fact about one
    // control.
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

      // Changing the kind also clears the box, exactly as the legacy screen did
      // (`Signup.ascx.vb:L342-L352`), so the pair "same text, new verdict" is not reachable
      // through this screen and the polarity is proven by the two tests above instead. What
      // IS proven here is that the rule was re-applied rather than left stale: the verdict
      // changed from a character offence to an unmet requirement with no typing at all.
      choosePortalType(PARENT_TYPE);

      expect(fieldMessages()).withContext('the stale verdict is gone').not.toContain(
        INVALID_ALIAS_MESSAGE,
      );
      expect(fieldMessages()).withContext('re-judged against the new value').toContain(
        ALIAS_REQUIRED_MESSAGE,
      );
    });
  });

  // ===========================================================================
  //  SEEDING THE ALIAS FROM THE BROWSED HOST
  // ===========================================================================

  describe('seeding the alias from the browsed host', () => {
    // MIGRATION: `Signup.ascx.vb:L344-L345` reads
    // `txtPortalName.Text = GetDomainName(Request) & "/"`. The successor takes the host from
    // the injected document rather than from a request object.
    it('seeds a child alias with the browsed host and a separator', () => {
      createMode();

      choosePortalType(CHILD_TYPE);

      expect(aliasValue()).toBe(`${STUBBED_HOST}/`);
    });

    // MIGRATION: `Signup.ascx.vb:L346-L347` reads `txtPortalName.Text = ""`.
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

      // The edit contract offers no kind and no alias at all — an existing portal's alias
      // is the alias screen's business — so there is nothing here for the seeding to
      // damage, and the hydrated record is untouched.
      expect(queryAll('[name="portalType"]')).withContext('no kind to choose').toHaveSize(0);
      expect(query('#portal-form-alias')).withContext('no alias to seed').toBeNull();
      expect(field<HTMLInputElement>('portal-form-title').value).toBe('Contoso');
    });
  });

  // ===========================================================================
  //  THE MEASURED LENGTH LIMITS
  // ===========================================================================

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

    // The rendered attribute stops a person typing past the limit but does nothing about a
    // value arriving any other way, so the rule has to exist on the control too. Assigning
    // the value directly is exactly the case the attribute does not cover.
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

    // MIGRATION: `signup.ascx:L94` and `L100` declare `maxlength="20"` on the two credential
    // boxes, and that figure IS DELIBERATELY NOT REPRODUCED. It mirrored the legacy storage
    // width `Users.Password nvarchar(20)` rather than any rule the legacy applied to a
    // credential — the measured policy in `Website/release.config` sets a minimum of seven
    // and declares no maximum. The successor stores a one-way hash, so the column that
    // produced the 20 no longer exists, and keeping it would cap the entropy of every
    // administrator credential this screen creates at twenty characters.
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

  // ===========================================================================
  //  THE SEMANTIC INVERSION
  // ===========================================================================

  describe('which box fills which member', () => {
    // MIGRATION: THE SINGLE MOST CONSEQUENTIAL MAPPING ON THIS SCREEN. The legacy
    // `txtPortalName` box collected the ALIAS and `txtTitle` collected the NAME:
    // `PortalController.vb:L980` declares `CreatePortal(PortalName, ...)` with `PortalAlias`
    // twelfth, and `Signup.ascx.vb:L274` passes `txtTitle.Text` into that FIRST position
    // while the value taken from `txtPortalName` goes into the `PortalAlias` position.
    // `signup.ascx:L39-L41` confirms it from the other side, binding the label
    // "Portal Alias:" to `controlname="txtPortalName"`.
    //
    // A swap compiles, submits, and writes every new portal's alias into its title column
    // with no diagnostic anywhere, which is why the two values below are chosen so that
    // neither could be mistaken for the other.
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

      // Stated the other way round as well, so the test fails loudly rather than subtly if
      // the two are ever exchanged.
      expect(body.portalName).not.toBe('alias-value.example.test');
      expect(body.portalAlias).not.toBe('A Distinguishable Title');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });

    it('labels the two boxes the way the legacy screen labelled them', () => {
      createMode();

      // "Portal Alias:" names the box that fills the ALIAS and "Title:" names the box that
      // fills the NAME, so reading the label tells you which member the box feeds. Asserted
      // per control rather than over the joined text, because the point is the PAIRING and a
      // substring search over everything would pass even if the two were swapped.
      //
      // The rendered text carries no trailing colon: the shared field wrapper strips one,
      // deliberately, on the grounds that the punctuation is presentation. So the measured
      // wording is matched at its start and the marker suffix is allowed to follow.
      expect(labelNaming('portal-form-alias')).toMatch(/^Portal Alias\b/);
      expect(labelNaming('portal-form-title')).toMatch(/^Title\b/);

      // The resource file also carries `plPortalName.Text` = "Portal Name:", but
      // `signup.ascx` declares no `plPortalName` control at all, so that entry is ORPHANED
      // and labelling anything with it here would name a field the legacy screen never
      // named that way.
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
  });

  // ===========================================================================
  //  THE REQUEST AND WHAT COMES BACK
  // ===========================================================================

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
        // Declared by the contract, so it is sent. There is no directory to browse and no
        // filesystem to create one in, so the screen states that it has nothing to say
        // rather than omitting a member the contract requires.
        homeDirectory: null,
        // Likewise declared and likewise unaskable: the legacy list was built by
        // enumerating the filesystem for `*.template` and no endpoint replaces it.
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
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
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

      // A FULL REPLACEMENT, so every member the contract declares is present. The three
      // edited here carry the new values and EVERY OTHER MEMBER CARRIES THE STORED VALUE
      // UNCHANGED — which is the property that matters: a replacement that omitted them
      // would silently blank the host-administered terms, the tab references and the
      // payment configuration that this screen deliberately does not offer.
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
        // Never round-tripped: a credential is not echoed back by a read, so echoing one
        // forward would be inventing it. The contract carries the member; this screen says
        // it has none.
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

      call.flush(envelope({ ...detail, portalName: 'Renamed' }));
      fixture.detectChanges();
      answerListingReread();

      expect(notifications()).toEqual([{ severity: 'success', message: UPDATE_SUCCEEDED_MESSAGE }]);
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
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

    // MIGRATION: `Signup.ascx.vb:L282` reads `If intPortalId <> -1 Then`, treating the
    // identifier in the RESULT as the success signal because the legacy call reported
    // failure by returning `Null.NullInteger`. The successor reports failure with a status
    // code, so the identifier is data and nothing more — and since -1 and 0 are both real
    // identifiers, a resurrected check on either would report a completed creation as a
    // failure.
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
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
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
      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
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
    });

    // MIGRATION: measured at `SiteSettings.ascx.vb:L759-L769`. When the operator is not a
    // host account the legacy screen compared the six host-administered fields — host fee,
    // host space, page quota, user quota, site-log history and expiry date — against the
    // stored record and, on any difference, executed a BARE `Throw New System.Exception`
    // WITH NO MESSAGE. There is no legacy sentence to carry across, so the target supplies
    // its own, and a refusal of authority is a WARNING rather than an error: nothing is
    // broken and nothing was changed.
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

      // NOT a session message, and NOT a sign-in redirect. Conflating "you may not change
      // this" with "you are no longer signed in" throws away work the operator can still
      // save by leaving the six terms alone.
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

      // Keyed the way a .NET model-state document keys them — the member name in its own
      // casing, not the casing the client happens to use. The client compares them
      // case-insensitively for exactly this reason.
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

    it('reports a record that has gone in this screen own words', () => {
      editMode('404');

      expectRequest('GET', portalUrl(404), 'the detail read').flush(
        problem('portal.not_found', 404, 'No such portal.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();

      expect(notifications()).toEqual([{ severity: 'warning', message: PORTAL_NOT_FOUND_MESSAGE }]);

      // Nothing to edit is offered, and nothing is thrown: the screen simply says so.
      expect(query('#portal-form-title')).toBeNull();
      expect(query('app-loading-spinner')).withContext('not left waiting forever').toBeNull();
    });

    // MIGRATION: the legacy handler caught the exception and assigned
    // `strMessage = ex.Message` (`Signup.ascx.vb:L275-L278`), putting whatever the server
    // threw in front of whoever was signing up. `CreateError.Text` was already in the
    // resource file for this purpose, so showing it instead is both closer to the screen's
    // evident intent and a refusal to leak internals.
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

  // ===========================================================================
  //  THE FIELDS THE LEGACY SCREEN SHOWED THAT ARE GONE
  // ===========================================================================

  describe('the fields the legacy screen showed that are gone', () => {
    // MIGRATION: `signup.ascx:L65-L69` declared the template selector `cboTemplate`, its
    // description label `lblTemplateDescription` and the eighth required validator
    // `valTemplate` ("Please select a template file"). The list was populated by
    // ENUMERATING THE FILESYSTEM for `*.template` and no endpoint replaces it.
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

    // MIGRATION: `signup.ascx:L44-L48` declared the home-directory box `txtHomeDirectory`
    // and its `btnCustomizeHomeDir` toggle. The filesystem subsystem is out of scope, so
    // there is nothing to browse and no directory to create.
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

      // The two members the legacy screen computed for itself have no counterpart in the
      // contract and are not invented here: `strServerPath` came from
      // `GetAbsoluteServerPath(Request)` and `strChildPath` from concatenating it with the
      // alias, both of which described the legacy filesystem layout.
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

      // The confirmation box exists to compare against the credential and for nothing else;
      // `CreatePortal` never had a parameter for it.
      expect(sent).not.toContain('confirm');
      expect(sent).not.toContain('administratorConfirm');
      expect(sent).not.toContain('administratorPasswordConfirmation');

      call.flush(envelope(portalDetail(1)), { status: 201, statusText: 'Created' });
      fixture.detectChanges();
      answerListingReread();
    });
  });

  // ===========================================================================
  //  SENTINEL VALUES
  // ===========================================================================

  describe('sentinel values', () => {
    // MIGRATION: `Null.NullString` is the EMPTY STRING and not null
    // (`Library/Components/Shared/Null.vb`), so a legacy read could not tell a stored null
    // from a stored empty string. Both arrive here as something the control can show, and
    // an absent member is shown as nothing rather than as the word null.
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

      // The empty string round-trips AS an empty string. Turning it into null on the way
      // back would change the stored value of a member nobody edited.
      expect(body.description).withContext('an empty string').toBe('');
      expect(body.keyWords).withContext('an empty string').toBe('');
      expect(body.description).not.toBeNull();

      call.flush(envelope(detail));
      fixture.detectChanges();
      answerListingReread();
    });

    // MIGRATION: `Null.NullDate` is `DateTime.MinValue`. It is carried through as the value
    // it is rather than being rendered — this screen offers no date at all, because the
    // expiry date is one of the six host-administered terms that belong to the settings
    // screen — and it is never presented anywhere as `01/01/0001`.
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

    // MIGRATION: a quota of 0 means UNLIMITED and a quota of -1 means NOT SET. They are
    // different facts and neither is an absence, so neither may be coerced to null or
    // dropped on the way back.
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

      // A null member is PRESENT AND NULL rather than missing, because the API serialises
      // with its ignore-condition set to never and its own reader distinguishes the two.
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

  // ===========================================================================
  //  STRUCTURE, TYPING AND REACH
  // ===========================================================================

  describe('structure, typing and reach', () => {
    // The component declares on-push change detection, so a change reaching it out of band
    // marks the view for checking and renders on the next check rather than immediately.
    // Asserting the withholding is the observable proof of that declaration.
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

      // Only the insisted-upon members are filled; the three descriptive ones are left
      // exactly as the form declared them.
      fillMinimalCreation('contoso.example.test');
      press(CREATE_SUBMIT_LABEL);

      const call = expectRequest('POST', PORTALS_URL, 'the creation');
      const body = call.request.body as Record<string, unknown>;

      // A control declared nullable reads back as null when untouched, and these do not:
      // every one of them is a string. That is the observable consequence of declaring the
      // whole group non-nullable, and it is why the payload never carries a surprise null.
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

      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);

      // Abandoning is not a submission: no request is issued, which the end-of-test
      // verification enforces, and nothing is marked as being at fault.
      expect(fieldMessages()).withContext('no complaints raised on the way out').toEqual([]);
      expect(notifications()).toEqual([]);
    });

    it('leaves an edit for the listing when abandoned, sending nothing', () => {
      arriveEditing(5);

      type('portal-form-title', 'Renamed');
      press(CANCEL_LABEL);

      expect(navigateSpy).toHaveBeenCalledOnceWith([PORTAL_LIST_ROUTE]);
      expect(fieldMessages()).toEqual([]);
    });

    it('offers both commands as real buttons with their kind declared', () => {
      createMode();

      // The abandon command is a plain button and NOT a submit, so pressing it leaves
      // without first putting the form through validation it does not need to pass.
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

      // MIGRATION: `signup.ascx:L8-L9` and `L73-L74` declared two `dnn:sectionhead`
      // controls. Neither collapsed anything that mattered, so each becomes a semantic field
      // set carrying the measured legend wording verbatim.
      expect(textOf('legend')).toEqual(['Portal Setup', 'Security Settings']);
    });
  });
});
