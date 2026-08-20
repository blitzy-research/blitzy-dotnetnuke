import { signal } from '@angular/core';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
import { AuthStore } from '../../../core/state/auth.store';
import { PortalStore } from '../../../core/state/portal.store';
import { UserStore } from '../../../core/state/user.store';
import { UserListComponent } from './user-list.component';

import type { WritableSignal } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import type { TestRequest } from '@angular/common/http/testing';
import type { CurrentUser } from '../../../core/models/auth.model';
import type { ApiResponse, PagedResponse } from '../../../core/models/paged-result.model';
import type { ProblemDetails } from '../../../core/models/problem-details.model';
import type { MembershipSettings, UserListItem } from '../../../core/models/user.model';

// ADDRESSES

const USERS_URL = '/api/v1/users';

/**
 * The body-bound account search. ⚠ THE LISTING NOW HAS TWO ADDRESSES, AND WHICH ONE IS USED IS A PRIVACY
 * DECISION RATHER THAN A ROUTING ONE. A search by account name, address or profile property names a
 * person, and a query parameter travels in the REQUEST TARGET — recorded by the browser's history, by
 * every forward and reverse proxy's access log, by the server's access log and by any URL-sampling
 * telemetry, all of which sit at an END of the encrypted channel rather than in the middle of it.
 */
const USERS_SEARCH_URL = '/api/v1/users/search';
const MEMBERSHIP_SETTINGS_URL = '/api/v1/users/settings';
const PROFILE_DEFINITIONS_URL = '/api/v1/profile-definitions';

/** The removal address of one account. */
function userUrl(userId: number): string {
  return `${USERS_URL}/${userId}`;
}

// THE WIRE VOCABULARY

const PAGE_INDEX_PARAM = 'pageIndex';
const PAGE_SIZE_PARAM = 'pageSize';
/** Address parameter carrying the search term, shared with the other three listings. */
const FILTER_QUERY_KEY = 'filter';

/** Address parameter carrying the search axis. */
const SEARCH_BY_QUERY_KEY = 'searchby';

/** Address parameter carrying the page, counted from ONE, shared with the other three listings. */
const PAGE_QUERY_KEY = 'currentpage';

const USER_NAME_PARAM = 'userName';
const EMAIL_PARAM = 'email';
const PROFILE_PROPERTY_NAME_PARAM = 'profilePropertyName';
const PROFILE_PROPERTY_VALUE_PARAM = 'profilePropertyValue';

/** The generic free-text parameter the paging contract publishes for OTHER listings. */
const GENERIC_QUERY_PARAM = 'query';

/** Every filter name the account listing can legitimately carry. */
const SEARCH_PARAMS: readonly string[] = Object.freeze([
  USER_NAME_PARAM,
  EMAIL_PARAM,
  PROFILE_PROPERTY_NAME_PARAM,
  PROFILE_PROPERTY_VALUE_PARAM,
]);

/**
 * Parameter names that would betray a paging model this contract does not use. The envelope carries a
 * total and a zero-based index, which is offset paging — the model the legacy pager consumed.
 */
const FOREIGN_PAGING_PARAMS: readonly string[] = Object.freeze([
  'cursor',
  'continuationToken',
  'nextPageUrl',
  'previousPageUrl',
  'links',
  'skip',
  'take',
  'offset',
  'limit',
  'page',
]);

// THE WORDING THIS SCREEN PUBLISHES

const PAGE_TITLE = 'User Accounts';
const ADD_USER_LABEL = 'Add New User';
const MEMBERSHIP_SETTINGS_LABEL = 'User Settings';
const PROFILE_DEFINITIONS_LABEL = 'Manage Profile Properties';
const SEARCH_FIELD_CAPTION = 'Search by';
const SEARCH_PLACEHOLDER = 'Begins with';
const RETRY_LABEL = 'Try again';

/** `SharedResources.resx` `Edit.Text` — the local file carries no `Edit` key, so this is a fall-through. */
const EDIT_COMMAND_LABEL = 'Edit';

/** `Users.ascx.resx` `Delete.Text`, present locally. */
const DELETE_COMMAND_LABEL = 'Delete';

/**
 * `Users.ascx.resx` `UserRoles.Text` — keyed by the legacy COMMAND NAME, which is why it is not "User
 * Roles".
 */
const MANAGE_ROLES_COMMAND_LABEL = 'Manage Roles';

const REMOVAL_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

const USER_DELETED_MESSAGE = 'User Deleted Successfully';

/** `SharedResources.resx` `UserDeleteError.Text`, reported at L657. */
const USER_DELETE_ERROR_MESSAGE = 'Error Deleting User';

const ALL_FILTER_LABEL = 'All';

const USERNAME_HEADING = 'Username';

/** `FirstName.Header` — the markup says "FirstName". */
const FIRST_NAME_HEADING = 'First Name';

/** `LastName.Header` — the markup says "LastName". */
const LAST_NAME_HEADING = 'Last Name';

/** `DisplayName.Header` — the markup says "DisplayName". */
const DISPLAY_NAME_HEADING = 'Name';

const ADDRESS_HEADING = 'Address';
const TELEPHONE_HEADING = 'Telephone';
const EMAIL_HEADING = 'Email';

/** `CreatedDate.Header` — the markup says "CreatedDate". */
const CREATED_DATE_HEADING = 'Created Date';

/** `LastLogin.Header` — the markup says "LastLogin". */
const LAST_LOGIN_HEADING = 'Last Login';

const AUTHORIZED_HEADING = 'Authorized';

/** The ten data headings in the order `users.ascx` L40-L79 declared their columns. */
const DATA_HEADINGS: readonly string[] = Object.freeze([
  USERNAME_HEADING,
  FIRST_NAME_HEADING,
  LAST_NAME_HEADING,
  DISPLAY_NAME_HEADING,
  ADDRESS_HEADING,
  TELEPHONE_HEADING,
  EMAIL_HEADING,
  CREATED_DATE_HEADING,
  LAST_LOGIN_HEADING,
  AUTHORIZED_HEADING,
]);

/** The three command headings, in the order `users.ascx` L32-L34 declared them. */
const COMMAND_HEADINGS: readonly string[] = Object.freeze([
  EDIT_COMMAND_LABEL,
  DELETE_COMMAND_LABEL,
  MANAGE_ROLES_COMMAND_LABEL,
]);

const AFFIRMATIVE_TEXT = 'Yes';
const NEGATIVE_TEXT = 'No';

/**
 * ⚠ THIS SCREEN NOW PASSES THE EMPTY STATE ITS OWN WORDING, AND THE CONSTANT MOVED WITH IT — QA-28. The
 * shared default, "No records found.", is accurate and useless on this listing: the table opens EMPTY by
 * design, before any query has been issued, so the first thing a reader met was a sentence that reported the
 * outcome of a read that had never happened, above a table whose only affordances were outside it.
 *
 * There are two reasons the table can be empty and they need different sentences, so the component chooses
 * between them. This is the one for a narrowing that matched nothing; {@link NO_QUERY_STATE_MESSAGE} is the
 * one for a screen that has asked for nothing yet.
 */
const EMPTY_STATE_MESSAGE = 'No accounts match the current filter.';

/** The wording shown while the tenant's opening-view policy has issued no query at all. */
const NO_QUERY_STATE_MESSAGE =
  'No accounts have been requested yet. Choose a letter, or select All, to list this site’s accounts.';

/** The affordance that fills an empty table by listing every account. */
const SHOW_ALL_ACCOUNTS_LABEL = 'List all accounts';

const EDIT_PERMISSION = 'EDIT';

/**
 * The shared pager's own accessible names for its four steps. ⚠ THE PAGER OFFERS NO NUMBERED ENTRIES. It
 * renders first, previous, next and last plus a position readout, so a case moves pages by pressing a
 * step rather than by pressing a number — verified in the component's template rather than assumed.
 */
const PAGER_NEXT_LABEL = 'Next page';
const PAGER_PREVIOUS_LABEL = 'Previous page';
const PAGER_LAST_LABEL = 'Last page';
const PAGER_FIRST_LABEL = 'First page';

// =====================================================================================================
// SELECTORS
// =====================================================================================================

const SEARCH_FIELD_CONTROL_ID = 'user-list-search-field';
const SEARCH_INPUT_SELECTOR = 'input.search-input__field';
const SEARCH_SUBMIT_SELECTOR = 'button.search-input__submit';
const LETTER_SELECTOR = 'button.user-list__letter';
const HEADER_SELECTOR = 'th.data-table__header';
const ROW_SELECTOR = 'tr.data-table__row';
const CELL_SELECTOR = 'td.data-table__cell,th.data-table__cell';
const ACTION_CELL_SELECTOR = 'td.data-table__cell--actions';
const PLACEHOLDER_SELECTOR = 'td.data-table__message';
const ROW_ACTION_SELECTOR = '.user-list__row-action';
const EMAIL_LINK_SELECTOR = 'a.user-list__email';
const RETRY_SELECTOR = 'button.user-list__failure-retry';

// =====================================================================================================
// THE FAILURE VOCABULARY, TAKEN FROM THE SERVER
// =====================================================================================================

const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

const STATUS_TITLE: Readonly<Record<number, string>> = {
  400: 'Bad Request',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  500: 'Internal Server Error',
};

/** A distributed-trace identifier, in the W3C shape the server emits. */
const TRACE_ID = '00-4b1f9d1cb7f24a9e8e1a6c5d3f207b41-9f2c7d5a1e0b4c63-01';

const CORRELATION_ID = '2f8b1c74-5d93-4e02-9a6f-7c1b0d84e5a2';

const FORBIDDEN_MESSAGE = 'You do not have permission to perform this action.';

/** The severity word the shared banner paints for a refusal. */
const WARNING_SEVERITY_LABEL = 'Warning';

/** The severity word it paints for everything that is genuinely an error. */
const ERROR_SEVERITY_LABEL = 'Error';

/**
 * Builds an RFC 7807 problem document. ⚠ `errors` IS KEYED AS THE SERVER KEYS IT. The map arrives from
 * .NET's model-state dictionary, whose keys are the PROPERTY NAMES of the request contract in their
 * original casing — `UserName`, not `userName` — so every read of it in this file is a BRACKET access.
 *
 * @param code The failure code the server writes into `type`.
 * @param status The HTTP status.
 * @param detail The sentence the server supplied, or the empty string to supply none.
 * @param errors Per-field messages, omitted when the failure is not a validation failure.
 * @returns The document.
 */
function problem(
  code: string,
  status: number,
  detail: string,
  errors?: Readonly<Record<string, readonly string[]>>,
): ProblemDetails {
  const title: string | undefined = STATUS_TITLE[status];
  const document: ProblemDetails = {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: title === undefined ? 'Error' : title,
    status,
    detail,
    traceId: TRACE_ID,
  };

  return errors === undefined ? document : { ...document, errors };
}

/**
 * A problem document carrying NEITHER a summary nor a sentence. ⚠ THIS SHAPE IS WHAT REACHES THE SHARED
 * STATUS VOCABULARY AT ALL. The summariser prefers the server's `detail`, then its `title`, and only then
 * falls back to a sentence of its own — so a document carrying the bare status word "Forbidden" as its
 * title renders THAT, and a case meaning to assert the shared wording has to omit both members.
 *
 * @param code The failure code the server writes into `type`.
 * @param status The HTTP status.
 * @returns The document.
 */
function bareProblem(code: string, status: number): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    status,
    traceId: TRACE_ID,
  };
}

// =====================================================================================================
// FIXTURES
// =====================================================================================================

/**
 * The signed-in caller's identity. ⚠ THE TENANT KEY IS -1 AND THE ACCOUNT KEY IS 99, BOTH DELIBERATE.
 * `Portals.PortalID` is `IDENTITY(-1, 1)`, so -1 is the FIRST REAL TENANT as well as the legacy marker
 * for a missing integer — a fixture using a tidier value would not exercise the screen's explicit
 * presence tests.
 *
 * @param overrides Members a case cares about.
 * @returns A complete identity.
 */
function callerAccount(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userId: 99,
    portalId: -1,
    portalName: 'Baseline Portal',
    username: 'caller',
    displayName: 'The Caller',
    email: 'caller@example.test',
    isSuperUser: false,
    isPortalAdministrator: true,
    mustChangePassword: false,
    mustUpdateProfile: false,
    roles: [],
    permissions: [],
    ...overrides,
  };
}

/**
 * One account row, in the exact shape `decodeUserListItem` accepts. ⚠ EVERY MEMBER IS PRESENT AND EVERY
 * SPELLING IS THE WIRE'S. The listing decoder is strict — it refuses an undeclared member value and
 * rejects the WHOLE page — so an omitted member or the plausible-looking `userName` in place of
 * `username` would fail the read rather than the assertion, which is a far harder failure to diagnose. ⚠
 * THE DEFAULT TENANT KEY IS MINUS ONE, AND THAT IS A SCHEMA FACT RATHER THAN A CURIOSITY.
 * `dbo.Portals.PortalID` is declared `IDENTITY (-1, 1)`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L77), so the first portal
 * ever created carries minus one — while `Library/Components/Shared/Null.vb` L41-L45 simultaneously
 * defines `NullInteger` as minus one.
 *
 * @param userId The account identifier.
 * @param overrides Members to replace.
 * @returns The row.
 */
function userRow(userId = 7, overrides: Partial<UserListItem> = {}): UserListItem {
  return {
    userId,
    portalId: -1,
    username: 'jbloggs',
    firstName: 'Joe',
    lastName: 'Bloggs',
    displayName: 'Joe Bloggs',
    address: 'Flat 2, 14 High Street, Bristol, Avon, United Kingdom, BS1 4TR',
    telephone: '0117 496 0000',
    email: 'jbloggs@example.test',
    createdDate: '2006-03-02T09:15:00Z',
    lastLoginDate: '2006-04-18T16:42:30Z',
    isApproved: true,
    isOnline: false,
    isSuperUser: false,
    isLockedOut: false,
    canDelete: true,
    ...overrides,
  };
}

/**
 * The tenant's account policy, in the shape `decodeMembershipSettings` accepts. ⚠ ALL NINE OPTIONAL
 * COLUMNS ARE SWITCHED ON HERE, and that is a fixture choice rather than the product default.
 *
 * @param overrides Members to replace.
 * @returns The policy.
 */
function membershipSettings(overrides: Partial<MembershipSettings> = {}): MembershipSettings {
  return {
    // ⚠ #5/#6 — stated rather than left to the override, so a specification that says nothing about
    // provenance still gets a policy claiming to be stored. Provenance-sensitive specifications pass
    // `isStored: false` explicitly.
    isStored: true,
    columnFirstName: true,
    columnLastName: true,
    columnDisplayName: true,
    columnAddress: true,
    columnTelephone: true,
    columnEmail: true,
    columnCreatedDate: true,
    columnLastLogin: true,
    columnAuthorized: true,
    displayMode: 0,
    displaySuppressPager: false,
    recordsPerPage: 4,
    profileDefaultVisibility: 2,
    profileDisplayVisibility: true,
    profileManageServices: false,
    redirectAfterLogin: null,
    redirectAfterRegistration: null,
    redirectAfterLogout: null,
    securityEmailValidation: '',
    securityRequireValidProfile: false,
    securityRequireValidProfileAtLogin: false,
    securityUsersControl: 0,
    securityDisplayNameFormat: '',
    ...overrides,
  };
}

/** The page size {@link membershipSettings} declares, so no assertion restates the number. */
const TENANT_PAGE_SIZE = membershipSettings().recordsPerPage;

/**
 * The policy a tenant with NO SETTINGS SOURCE is answered with: the platform defaults, marked as
 * defaults. ⚠ #5/#6 — A SUCCESSFUL `200`, NOT A `404`, AND THAT IS THE WHOLE POINT OF THIS BUILDER. The
 * server answers a portal holding no "User Accounts" module instance with the measured legacy defaults
 * and `isStored: false`; the write for the same address answers `409`.
 *
 * @param overrides Members to replace.
 * @returns The policy.
 */
function unstoredMembershipSettings(
  overrides: Partial<MembershipSettings> = {},
): MembershipSettings {
  return membershipSettings({
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
    recordsPerPage: 10,
    profileDefaultVisibility: 2,
    profileDisplayVisibility: true,
    profileManageServices: true,
    securityRequireValidProfileAtLogin: true,
    ...overrides,
  });
}

/**
 * One tenant-declared profile property, in the shape `decodeProfilePropertyDefinition` accepts. Declared
 * as a local shape rather than imported, because the profile contract is not one of this screen's own
 * dependencies: the declarations reach the component as NAMES through the store's derived view, and what
 * this file needs is a body the decoder accepts.
 *
 * @param propertyName The property's name, which is the value the search axis transmits.
 * @param propertyDefinitionId The declaration's identifier.
 * @returns The declaration.
 */
function profileDefinition(
  propertyName: string,
  propertyDefinitionId: number,
): Readonly<Record<string, unknown>> {
  return {
    propertyDefinitionId,
    portalId: -1,
    moduleDefId: null,
    dataType: 349,
    defaultValue: null,
    propertyCategory: 'Contact Information',
    propertyName,
    length: 0,
    required: false,
    validationExpression: null,
    viewOrder: propertyDefinitionId,
    visible: true,
    visibility: 2,
  };
}

const ODD_PROPERTY_NAME = 'Xx_Legacy Field 42';

/** A second declared property, so the option list is provably a list rather than a single entry. */
const SECOND_PROPERTY_NAME = 'City';

/** The tenant's declarations, in the order the server returned them. */
const PROFILE_DEFINITIONS: readonly Readonly<Record<string, unknown>>[] = Object.freeze([
  profileDefinition(ODD_PROPERTY_NAME, 11),
  profileDefinition(SECOND_PROPERTY_NAME, 12),
]);

/** The single-payload envelope. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * A page of accounts. ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data`.
 *
 * @param items The rows on this page.
 * @param totalCount The total across every page.
 * @param pageIndex The ZERO-BASED index the server is reporting, which is what the pager binds.
 * @param pageSize The size the server applied.
 * @returns The page.
 */
function pageOf(
  items: readonly UserListItem[],
  totalCount: number = items.length,
  pageIndex = 0,
  pageSize: number = TENANT_PAGE_SIZE,
): PagedResponse<UserListItem> {
  return {
    items,
    meta: {
      totalCount,
      pageIndex,
      pageSize,
      totalPages: Math.ceil(totalCount / pageSize),
    },
  };
}

// =====================================================================================================
// STRUCTURAL PROBES
// =====================================================================================================

/** The field the Angular compiler writes the component definition onto. */
const COMPONENT_DEFINITION_FIELD = 'ɵcmp';

/** The flag the compiler sets from `changeDetection: ChangeDetectionStrategy.OnPush`. */
const ON_PUSH_FIELD = 'onPush';

/**
 * One named field of an unknown value, or `undefined` where the value cannot carry fields. ⚠ READ THROUGH
 * `Reflect.get` RATHER THAN THROUGH A CAST. Asserting an unknown into an index signature is the shape of
 * assertion that hides a mistake: it type-checks against a value that may be a number, a string or
 * nothing at all, and then fails at run time instead.
 *
 * @param carrier The value to read from.
 * @param field The field name.
 * @returns The field's value, or undefined.
 */
function fieldOf(carrier: unknown, field: string): unknown {
  if (carrier === null || (typeof carrier !== 'object' && typeof carrier !== 'function')) {
    return undefined;
  }

  return Reflect.get(carrier, field);
}

/**
 * Whether a component type was compiled with `OnPush` change detection. ⚠ READ STRUCTURALLY BECAUSE
 * NOTHING ELSE CAN WITNESS IT. The usual demonstration moves an input with `setInput` and shows one
 * repaint — but this is a ROUTED SCREEN WITH NO INPUTS AT ALL, so `setInput` would throw rather than
 * prove anything.
 *
 * @param componentType The component class.
 * @returns Whether the flag is set.
 */
function declaresOnPush(componentType: unknown): boolean {
  const definition: unknown = fieldOf(componentType, COMPONENT_DEFINITION_FIELD);

  return fieldOf(definition, ON_PUSH_FIELD) === true;
}

describe('UserListComponent', () => {
  let fixture: ComponentFixture<UserListComponent>;
  let httpMock: HttpTestingController;
  let administersPortal: WritableSignal<boolean>;
  let callerIdentity: WritableSignal<CurrentUser | null>;
  let designatedAdministrator: WritableSignal<number | null>;
  let loadCurrentPortalContext: jasmine.Spy;
  let notifySpy: jasmine.Spy;

  /**
   * Whether {@link create} has run in the CURRENT case. ⚠ A CLOSURE VARIABLE OUTLIVES THE CASE THAT
   * ASSIGNED IT, so `fixture` still holds the previous case's component even in a case that never mounted
   * one.
   */
  let mounted = false;

  beforeEach(async () => {
    mounted = false;

    // ⚠ TENANT ADMINISTRATION IS THE INPUT TO THIS SCREEN, so it lives in a signal the cases can move. Five
    // affordances are gated on it — the three header actions, the row edit link and the row delete button —
    // and every destination they address is declared under the `PortalAdministrator` policy.
    administersPortal = signal<boolean>(true);

    // ⚠ THE CALLER'S OWN IDENTITY, which this screen reads for exactly two facts: the tenant to ask the
    // protected facts for, and the account key the row-level removal guard compares against.
    callerIdentity = signal<CurrentUser | null>(callerAccount());

    // ⚠ THE TENANT'S DESIGNATED ADMINISTRATOR, held separately because it is the fact the removal guard
    // turns on and it arrives ASYNCHRONOUSLY — `null` until the tenant's own record has been read.
    designatedAdministrator = signal<number | null>(null);

    // The request for those facts, spied rather than served.
    loadCurrentPortalContext = jasmine.createSpy('loadCurrentPortalContext');

    await TestBed.configureTestingModule({
      imports: [UserListComponent],
      providers: [
        // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
        provideHttpClient(),
        provideHttpClientTesting(),
        // ⚠ A ROUTE THAT ALWAYS MATCHES, because this screen now keeps its search, axis and page in the
        // ADDRESS and writes them with a real navigation. An empty route table refuses every navigation, so
        // the write would silently fail and the read that follows the address change would never be issued.
        provideRouter([{ path: '**', component: UserListComponent }]),
        // The REAL store, pinned to this injector rather than left to its root registration, so each case
        // starts from a store that has read nothing.
        UserStore,
        {
          provide: AuthStore,
          useValue: { administersCurrentPortal: administersPortal, currentUser: callerIdentity },
        },
        {
          provide: PortalStore,
          useValue: { administratorUserId: designatedAdministrator, loadCurrentPortalContext },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    notifySpy = spyOn(TestBed.inject(NotificationService), 'notify').and.callThrough();
  });

  afterEach(() => {
    // ⚠ THE FIXTURE IS TORN DOWN BEFORE THE BACKEND IS VERIFIED, AND THAT ORDER IS DELIBERATE.
    if (mounted) {
      fixture.destroy();
      mounted = false;
    }

    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Creates the screen. `ngOnInit` issues the policy read and the declarations read during this pass. */
  /** Lets a navigation this screen started actually happen. */
  async function settleAddress(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** Navigates to an address BEFORE the screen mounts, which is how an entry is simulated. */
  async function enterAt(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
  }

  /** The query parameters the screen has actually navigated to. */
  function addressParams(): Readonly<Record<string, string>> {
    const router: Router = TestBed.inject(Router);

    return router.parseUrl(router.url).queryParams as Readonly<Record<string, string>>;
  }

  function create(): void {
    fixture = TestBed.createComponent(UserListComponent);
    mounted = true;
    fixture.detectChanges();
  }

  /**
   * Consumes exactly one pending request, asserted by verb AND address. Matched on `url`, which is the
   * address WITHOUT the query string, so a case states the endpoint here and inspects the parameters
   * separately.
   *
   * @param method The HTTP verb.
   * @param url The address, without a query string.
   * @param description Wording for the failure message.
   * @returns The request.
   */
  function expectRequest(method: string, url: string, description?: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.method === method && candidate.url === url,
      description ?? `${method} ${url}`,
    );
  }

  /**
   * Answers the tenant's account policy, which is what releases the listing read.
   *
   * @param settings The policy to answer with.
   * @returns The policy request, for a case that wants to inspect it.
   */
  function answerSettings(settings: MembershipSettings = membershipSettings()): TestRequest {
    const request = expectRequest('GET', MEMBERSHIP_SETTINGS_URL, 'the account-policy read');

    request.flush(envelope(settings));
    fixture.detectChanges();

    return request;
  }

  /**
   * Answers the tenant's profile declarations. ⚠ UNPAGED, AND THE ABSENCE OF EVERY COORDINATE IS PART OF
   * THE CONTRACT. The transport returns a plain array inside the single-payload envelope and the store
   * holds no page index, page size or total for it.
   *
   * @param definitions The declarations to answer with.
   * @returns The declarations request.
   */
  function answerDefinitions(
    definitions: readonly Readonly<Record<string, unknown>>[] = PROFILE_DEFINITIONS,
  ): TestRequest {
    const request = expectRequest('GET', PROFILE_DEFINITIONS_URL, 'the profile-declarations read');

    request.flush(envelope(definitions));
    fixture.detectChanges();

    return request;
  }

  /**
   * Answers the listing read.
   *
   * @param page The page to answer with.
   * @returns The listing request, which is what most cases inspect.
   */
  function answerListing(page: PagedResponse<UserListItem> = pageOf([userRow()])): TestRequest {
    const request = expectListRead('the listing read');

    request.flush(page);
    fixture.detectChanges();

    return request;
  }

  /**
   * Mounts the screen and settles all three arrival reads. ⚠ THE ORDER IS THE COMPONENT'S, NOT THIS
   * HELPER'S. The policy is answered first because the listing is not issued until it has been; the
   * declarations are independent and are answered between the two only because that keeps the pending set
   * small.
   *
   * @param page The page the listing answers with.
   * @param settings The policy the tenant declares.
   * @param definitions The tenant's profile declarations.
   * @returns The listing request.
   */
  function arrive(
    page: PagedResponse<UserListItem> = pageOf([userRow()]),
    settings: MembershipSettings = membershipSettings(),
    definitions: readonly Readonly<Record<string, unknown>>[] = PROFILE_DEFINITIONS,
  ): TestRequest {
    create();
    answerSettings(settings);
    answerDefinitions(definitions);

    return answerListing(page);
  }

  /**
   * The component's host element. ⚠ BY ASSIGNMENT, NOT BY CAST. `ComponentFixture.nativeElement` is
   * declared `any`, so the annotated local is what gives it a type — and it is a real check rather than a
   * cosmetic one, because a cast would equally have accepted a wrong element type and pushed the failure
   * into whichever assertion happened to touch it first.
   *
   * @returns The host element.
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
   * The one element matching `selector`, narrowed by a real check. ⚠ THIS EXISTS BECAUSE A NON-NULL
   * ASSERTION IS NOT ALLOWED HERE. `element!.textContent` would silence the compiler and then read
   * `textContent` of `null` at run time, and Jasmine reports that as a bare `TypeError` naming neither
   * the selector nor the case's intent.
   *
   * @param root The subtree to search.
   * @param selector The selector to find.
   * @returns The element.
   */
  function queryOrFail<E extends Element>(root: ParentNode, selector: string): E {
    const found: E | null = root.querySelector<E>(selector);

    if (found === null) {
      throw new Error(`Expected to find "${selector}"`);
    }

    return found;
  }

  /** The trimmed text of every element matching `selector`. */
  function textOf(selector: string): readonly string[] {
    return queryAll<Element>(selector).map((node) => (node.textContent ?? '').trim());
  }

  /** The wording of every strip entry currently reporting itself as applied. */
  function pressedAffordances(): readonly string[] {
    return queryAll<HTMLButtonElement>(LETTER_SELECTOR)
      .filter((entry) => entry.getAttribute('aria-pressed') === 'true')
      .map((entry) => textIn(entry));
  }

  /** The trimmed text of one element. */
  function textIn(node: Element): string {
    return (node.textContent ?? '').trim();
  }

  /** The painted account rows. */
  function rows(): readonly HTMLTableRowElement[] {
    return queryAll<HTMLTableRowElement>(ROW_SELECTOR);
  }

  /**
   * The DATA cells of one row — the three command cells excluded. The commands carry their own cell
   * class, so the two families are distinguishable without counting positions, which keeps a case that
   * adds a column from breaking every other case.
   *
   * @param row The row to read.
   * @returns Its data cells, in document order.
   */
  function dataCellsOf(row: Element): readonly HTMLTableCellElement[] {
    return Array.from(row.querySelectorAll<HTMLTableCellElement>(CELL_SELECTOR)).filter(
      (cell) => !cell.classList.contains('data-table__cell--actions'),
    );
  }

  /**
   * One data cell of the single painted row, addressed by its heading. Located by matching the heading
   * text against the VISIBLE heading order, so a case names the column it means rather than an index that
   * a visibility change would silently move.
   *
   * @param heading The column heading.
   * @returns That column's cell in the first row.
   */
  function cellUnder(heading: string): HTMLTableCellElement {
    const headings: readonly string[] = textOf(HEADER_SELECTOR);
    const position: number = headings.indexOf(heading);

    if (position < 0) {
      throw new Error(`Expected a column headed "${heading}"`);
    }

    const painted: readonly HTMLTableRowElement[] = rows();
    const first: HTMLTableRowElement | undefined = painted[0];

    if (first === undefined) {
      throw new Error('Expected at least one painted row');
    }

    const cells: readonly HTMLTableCellElement[] = Array.from(
      first.querySelectorAll<HTMLTableCellElement>('td,th'),
    );
    const cell: HTMLTableCellElement | undefined = cells[position];

    if (cell === undefined) {
      throw new Error(`Expected a cell in position ${position} for "${heading}"`);
    }

    return cell;
  }

  /**
   * The value of one query parameter, narrowed without a non-null assertion. ⚠ `HttpParams.get` RETURNS
   * `string | null`, and `!` is forbidden in this file.
   *
   * @param request The request to read.
   * @param name The parameter name.
   * @returns The transmitted value.
   */
  function paramOf(request: TestRequest, name: string): string {
    const sent = searchMembers(request);

    if (sent !== null) {
      const member: unknown = sent[name];

      if (member === undefined) {
        throw new Error(`Expected the search body to carry "${name}"`);
      }

      // Stringified so that a case reads the same value whichever address carried it. A query parameter is
      // always text, and a body member is typed — a page index is a number there — so without this every
      // coordinate assertion would have to be written twice.
      return String(member);
    }

    const value: string | null = request.request.params.get(name);

    if (value === null) {
      throw new Error(`Expected the request to carry "${name}"`);
    }

    return value;
  }

  /** Whether a request carries a value at all, present-and-empty included. */
  function carries(request: TestRequest, name: string): boolean {
    const sent = searchMembers(request);

    if (sent !== null) {
      return Object.prototype.hasOwnProperty.call(sent, name);
    }

    return request.request.params.has(name);
  }

  /**
   * The members of a search body, or `null` when the request was not a search.
   *
   * @param request The request to inspect.
   * @returns The body members, or null for a query-string request.
   */
  function searchMembers(request: TestRequest): Readonly<Record<string, unknown>> | null {
    if (request.request.method !== 'POST' || request.request.url !== USERS_SEARCH_URL) {
      return null;
    }

    const sent: unknown = request.request.body;

    if (typeof sent !== 'object' || sent === null || Array.isArray(sent)) {
      throw new Error('the search did not transmit a JSON object body');
    }

    return { ...sent };
  }

  /**
   * Claims the one outstanding listing read, whichever of its two addresses it went to.
   *
   * @param description What the read is, for the failure message.
   * @returns The one matching request.
   */
  function expectListRead(description: string): TestRequest {
    return httpMock.expectOne(
      (candidate) =>
        (candidate.method === 'GET' && candidate.url === USERS_URL)
        || (candidate.method === 'POST' && candidate.url === USERS_SEARCH_URL),
      description,
    );
  }

  /** Asserts that the screen has issued no listing read at all, to either address. */
  function expectNoListRead(): void {
    // ⚠ COUNTED, NOT `expectNone`, AND THE DIFFERENCE IS VISIBLE IN THE LOG. `expectNone` asserts by
    // throwing, so Jasmine records no expectation for a spec whose entire claim is this call - and the
    // runner then reports that spec exactly as it reports one that forgot to assert anything.
    expect(
      httpMock.match(
        (candidate) =>
          (candidate.method === 'GET' && candidate.url === USERS_URL)
          || (candidate.method === 'POST' && candidate.url === USERS_SEARCH_URL),
      ),
    )
      .withContext('no listing read of either shape was issued')
      .toEqual([]);
  }

  /**
   * A button inside `root` whose rendered wording is exactly `label`. ⚠ SCOPED TO A SUBTREE ON PURPOSE.
   * The row delete command and the confirmation's affirmative control BOTH read "Delete" — the
   * affirmative wording is deliberately the legacy `Delete.Text` — so a document-wide search by wording
   * would find whichever came first in the document and the flow would pass while testing the wrong
   * control.
   *
   * @param root The subtree to search.
   * @param label The exact wording.
   * @returns The button, or undefined.
   */
  function buttonIn(root: ParentNode, label: string): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find(
      (candidate) => textIn(candidate) === label,
    );
  }

  /**
   * Presses a button inside `root` by its rendered wording.
   *
   * @param root The subtree to search.
   * @param label The wording to press.
   */
  function pressIn(root: ParentNode, label: string): void {
    const control: HTMLButtonElement | undefined = buttonIn(root, label);

    if (control === undefined) {
      throw new Error(`Expected a control labelled "${label}"`);
    }

    control.click();
    fixture.detectChanges();
  }

  /**
   * Types a term into the shared search control and submits it immediately. ⚠ SUBMITTED RATHER THAN LEFT
   * TO DEBOUNCE, which is what keeps this file free of timers.
   *
   * @param term The text to type, passed through exactly as given.
   */
  async function typeSearch(term: string): Promise<void> {
    const field = queryOrFail<HTMLInputElement>(host(), SEARCH_INPUT_SELECTOR);

    field.value = term;
    field.dispatchEvent(new Event('input'));

    const submit = queryOrFail<HTMLButtonElement>(host(), SEARCH_SUBMIT_SELECTOR);

    submit.click();
    fixture.detectChanges();
    await settleAddress();
  }

  /**
   * Chooses a search axis by its rendered wording.
   *
   * @param label The option wording to select.
   */
  function chooseAxis(label: string): void {
    const control = queryOrFail<HTMLSelectElement>(host(), `#${SEARCH_FIELD_CONTROL_ID}`);
    const option: HTMLOptionElement | undefined = Array.from(control.options).find(
      (candidate) => textIn(candidate) === label,
    );

    if (option === undefined) {
      throw new Error(`Expected an option labelled "${label}"`);
    }

    control.value = option.value;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /**
   * Presses one entry of the alphabet strip.
   *
   * @param affordance A single letter, or the unfiltered affordance's own wording.
   */
  async function pressLetter(affordance: string): Promise<void> {
    const entry: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      LETTER_SELECTOR,
    ).find((candidate) => textIn(candidate) === affordance);

    if (entry === undefined) {
      throw new Error(`Expected an alphabet entry "${affordance}"`);
    }

    entry.click();
    fixture.detectChanges();
    await settleAddress();
  }

  /**
   * Presses one step of the shared pager, addressed by its accessible name.
   *
   * @param label One of the pager's four step names.
   */
  async function pressPager(label: string): Promise<void> {
    const step: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      'button.pagination__button',
    ).find((candidate) => candidate.getAttribute('aria-label') === label);

    if (step === undefined) {
      throw new Error(`Expected a pager step named "${label}"`);
    }

    step.click();
    fixture.detectChanges();
    await settleAddress();
  }

  /** The pager's one-based position readout, as rendered. */
  function pagerPosition(): string {
    return textIn(queryOrFail<Element>(host(), 'span.pagination__position'));
  }

  /** The row action of the first painted row whose accessible name begins with `label`. */
  function rowAction(label: string): HTMLElement {
    const actions: readonly HTMLElement[] = queryAll<HTMLElement>(ROW_ACTION_SELECTOR);
    const found: HTMLElement | undefined = actions.find((candidate) => {
      const name: string | null = candidate.getAttribute('aria-label');

      return name !== null && name.startsWith(label);
    });

    if (found === undefined) {
      throw new Error(`Expected a row action named "${label}…"`);
    }

    return found;
  }

  /**
   * The confirmation dialogue, whose PRESENCE in the document is what "open" means.
   *
   * @returns The dialogue element, or null when none is open.
   */
  function dialog(): HTMLElement | null {
    return query<HTMLElement>('dialog.confirm-dialog');
  }

  /** The confirmation dialogue, asserted to be open. */
  function openDialog(): HTMLElement {
    return queryOrFail<HTMLElement>(host(), 'dialog.confirm-dialog');
  }

  /** Presses the row delete command of the first painted row, which opens the confirmation. */
  function requestRemoval(): void {
    rowAction(DELETE_COMMAND_LABEL).click();
    fixture.detectChanges();
  }

  /**
   * Presses the confirmation's affirmative control. ⚠ ADDRESSED BY ITS DANGER MODIFIER RATHER THAN BY
   * EXACT WORDING, and the reason is worth recording: the dialogue prefixes a warning glyph to the label
   * when the danger input is set, so the control's text is the glyph AND the legacy `Delete.Text` rather
   * than the label alone.
   */
  function acceptRemoval(): void {
    const affirmative = queryOrFail<HTMLButtonElement>(
      openDialog(),
      'button.confirm-dialog__button--danger',
    );

    expect(textIn(affirmative)).toContain(DELETE_COMMAND_LABEL);

    affirmative.click();
    fixture.detectChanges();
  }

  /**
   * Opens the confirmation for the first row and accepts it, returning the removal request. The
   * confirmation is a real dialogue rather than a browser prompt, so acceptance is a press on its own
   * affirmative control — scoped to the dialogue, because the row command carries the same wording.
   *
   * @param userId The account the first row carries.
   * @returns The removal request.
   */
  function confirmRemoval(userId: number): TestRequest {
    requestRemoval();
    acceptRemoval();

    return expectRequest('DELETE', userUrl(userId), 'the removal');
  }

  /**
   * Drains the effect that reports a settled removal. ⚠ `TestBed.flushEffects()` IS THE API THIS
   * FRAMEWORK VERSION PUBLISHES. It is declared on the `TestBed` interface in the installed
   * `@angular/core@19.2.25`; `TestBed.tick()` does not exist here, so there is nothing else to use — and
   * nothing in this file is asynchronous, so nothing else is needed.
   */
  function settleOutcome(): void {
    TestBed.flushEffects();
    fixture.detectChanges();
  }

  // ===================================================================================================
  // ARRIVAL
  // ===================================================================================================

  describe('arrival', () => {
    it('reads the tenant policy and the profile declarations at once, and the listing only after the policy', () => {
      create();

      const settings = expectRequest('GET', MEMBERSHIP_SETTINGS_URL, 'the account-policy read');
      const definitions = expectRequest(
        'GET',
        PROFILE_DEFINITIONS_URL,
        'the profile-declarations read',
      );

      expectNoListRead();

      settings.flush(envelope(membershipSettings()));
      fixture.detectChanges();

      // Released by the policy answering, not by anything this specification did.
      answerListing();

      definitions.flush(envelope(PROFILE_DEFINITIONS));
      fixture.detectChanges();

      expect(rows()).toHaveSize(1);
    });

    it('lists the accounts even when the policy read is REFUSED, at the shared fallback size', () => {
      create();

      expectRequest('GET', MEMBERSHIP_SETTINGS_URL).flush(
        problem('not_found', 404, 'That portal could not be resolved.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();
      answerDefinitions();

      const listing = answerListing(pageOf([userRow()], 1, 0, 10));

      // ⚠ A REFUSAL, NOT AN UNSTORED TENANT. The status here means the policy could not be read at all; a
      // tenant that merely stores nothing is answered `200` with the server's own defaults and is asserted
      // separately below.
      expect(carries(listing, PAGE_SIZE_PARAM)).toBeTrue();
      expect(paramOf(listing, PAGE_SIZE_PARAM)).not.toBe(String(TENANT_PAGE_SIZE));
      expect(rows()).toHaveSize(1);
    });

    it('shows only the four columns the legacy defaults left visible when the policy read is REFUSED', () => {
      create();
      expectRequest('GET', MEMBERSHIP_SETTINGS_URL).flush(
        problem('not_found', 404, 'That portal could not be resolved.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();
      answerDefinitions();
      answerListing(pageOf([userRow()], 1, 0, 10));

      // MEASURED, NOT ASSUMED, AND NOT UNIFORMLY TRUE. `UserModuleBase.GetSettings` filled each unset key
      // with a value that left FOUR of the nine optional columns HIDDEN: both name parts, the
      // electronic-mail column and the last-login column.
      const headings: readonly string[] = textOf(HEADER_SELECTOR);

      expect(headings).toContain(USERNAME_HEADING);
      expect(headings).toContain(DISPLAY_NAME_HEADING);
      expect(headings).toContain(ADDRESS_HEADING);
      expect(headings).toContain(TELEPHONE_HEADING);
      expect(headings).toContain(CREATED_DATE_HEADING);
      expect(headings).not.toContain(FIRST_NAME_HEADING);
      expect(headings).not.toContain(LAST_NAME_HEADING);
      expect(headings).not.toContain(EMAIL_HEADING);
      expect(headings).not.toContain(LAST_LOGIN_HEADING);
    });

    it('takes the default columns and page size from the SERVER when the tenant stores no policy', async () => {
      // ⚠ NO LISTING IS ISSUED ON ARRIVAL, and that is the policy being honoured. The measured default
      // display mode is the no-query one, so the screen waits to be asked — which is why the page size is
      // proved through the unfiltered affordance rather than through an arrival read.
      create();
      answerSettings(unstoredMembershipSettings());
      answerDefinitions();
      expectNoListRead();

      await pressLetter(ALL_FILTER_LABEL);

      const unfiltered = expectListRead('the unfiltered read');

      // Ten, from the document the server sent — not the four this file's stored fixture declares, and
      // not a literal chosen here.
      expect(paramOf(unfiltered, PAGE_SIZE_PARAM)).toBe(
        String(unstoredMembershipSettings().recordsPerPage),
      );

      unfiltered.flush(pageOf([userRow()], 1, 0, 10));
      fixture.detectChanges();

      const headings: readonly string[] = textOf(HEADER_SELECTOR);

      expect(headings).toContain(USERNAME_HEADING);
      expect(headings).toContain(DISPLAY_NAME_HEADING);
      expect(headings).toContain(ADDRESS_HEADING);
      expect(headings).toContain(TELEPHONE_HEADING);
      expect(headings).toContain(CREATED_DATE_HEADING);
      expect(headings).not.toContain(FIRST_NAME_HEADING);
      expect(headings).not.toContain(LAST_NAME_HEADING);
      expect(headings).not.toContain(EMAIL_HEADING);
      expect(headings).not.toContain(LAST_LOGIN_HEADING);
      expect(rows()).toHaveSize(1);
    });

    it('discloses NOTHING when the tenant stores no policy, because nothing was lost', () => {
      create();
      answerSettings(unstoredMembershipSettings());
      answerDefinitions();
      expectNoListRead();

      expect(query('.user-list__policy-notice'))
        .withContext('the values on screen are the server\u2019s own defaults, so nothing degraded')
        .toBeNull();
      expect(query('.user-list__failure')).toBeNull();
      expect(query(RETRY_SELECTOR)).toBeNull();
    });

    it('paints the page heading and the three header actions from the resource wording', () => {
      arrive();

      const actions: readonly string[] = textOf('a.user-list__page-action');

      expect(textIn(queryOrFail<Element>(host(), 'app-page-header'))).toContain(PAGE_TITLE);
      expect(actions).toEqual([
        ADD_USER_LABEL,
        MEMBERSHIP_SETTINGS_LABEL,
        PROFILE_DEFINITIONS_LABEL,
      ]);
    });

    it('declares OnPush change detection', () => {
      // Structural, because a routed screen with no inputs offers nothing else to witness it through.
      expect(declaresOnPush(UserListComponent)).toBeTrue();
    });

    it('re-reads the policy, the declarations and the listing when the reader retries', () => {
      // ⚠ #5 — THE FAULT THIS CASE INJECTS IS A LISTING FAILURE, NOT A POLICY FAILURE. The account
      // policy is not one of the operations the screen-level failure surface answers for, so failing that
      // read instead would raise no surface for the retry to press.
      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush(
        problem('internal', 500, 'The accounts could not be read.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      pressIn(host(), RETRY_LABEL);

      // The POLICY is re-read as well as the listing, because a listing read at the fallback size beside an
      // unreadable policy is exactly the state the retry recovers from.
      answerSettings();
      answerDefinitions();
      answerListing();

      expect(query(RETRY_SELECTOR)).toBeNull();
    });

    it('does NOT raise the screen-level failure surface when only the account policy read fails', () => {
      // Nothing about the accounts is affected by an unreadable policy. The store dispatches the listing on
      // BOTH policy outcomes for exactly that reason, and this screen holds a documented fallback for each
      // of the two things the policy decides — the page size and the optional-column selection.
      create();
      expectRequest('GET', MEMBERSHIP_SETTINGS_URL).flush(
        problem('not_found', 404, 'That portal could not be resolved.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();
      answerDefinitions();
      answerListing(pageOf([userRow()], 1, 0, 10));

      // No banner, and no retry command: the listing is healthy and there is nothing to retry.
      expect(query(RETRY_SELECTOR)).toBeNull();
      expect(query('.user-list__failure')).toBeNull();

      // The row is on screen, which is the whole reason the banner was wrong.
      expect(rows()).toHaveSize(1);
    });

    it('discloses the degradation quietly instead, naming only what the policy decides', () => {
      // ⚠ #5 — SILENCE WOULD BE THE MIRROR OF THE DEFECT. A listing quietly showing ten rows a page when
      // the tenant asked for fifty, with nothing on screen to say the preference was not honoured, is as
      // misleading as a banner claiming the listing failed.
      create();
      expectRequest('GET', MEMBERSHIP_SETTINGS_URL).flush(
        problem('not_found', 404, 'That portal could not be resolved.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();
      answerDefinitions();
      answerListing(pageOf([userRow()], 1, 0, 10));

      const notice = queryOrFail<HTMLElement>(host(), '.user-list__policy-notice');

      expect(notice.getAttribute('role')).withContext('not an alert').toBeNull();
      expect(notice.getAttribute('aria-live')).withContext('not a live region').toBeNull();

      // It names the two things the policy decides here and says the accounts are unaffected, so it
      // cannot be read as claiming the listing is broken.
      const wording: string = (notice.textContent ?? '').trim();

      expect(wording).toContain('default page size');
      expect(wording).toContain('default columns');
      expect(wording).toContain('accounts themselves are unaffected');
    });

    it('shows no degradation notice while the policy reads successfully', () => {
      arrive();

      expect(query('.user-list__policy-notice')).toBeNull();
    });
  });

  // ===================================================================================================
  // §3.1 — SERVER-SIDE PAGING THROUGH THE PagedResult ENVELOPE
  // ===================================================================================================

  describe('the paging envelope', () => {
    it('takes the rows and the total from one envelope rather than from a by-reference argument', () => {
      arrive(pageOf([userRow(1), userRow(2)], 9, 0, TENANT_PAGE_SIZE));

      expect(rows()).toHaveSize(2);

      // The total is the pager's, and it is the envelope's `totalCount` that reaches it — both in the
      // page count it derives and in the readout it paints.
      expect(pagerPosition()).toBe(`1 / ${Math.ceil(9 / TENANT_PAGE_SIZE)}`);
      expect(textIn(queryOrFail<Element>(host(), 'p.pagination__status'))).toContain('of 9');
    });

    it('requests the page size the tenant policy declares, never a hard-coded literal', () => {
      const listing = arrive();

      expect(paramOf(listing, PAGE_SIZE_PARAM)).toBe(String(TENANT_PAGE_SIZE));
    });

    it('follows the tenant policy when it declares a different page size', () => {
      const listing = arrive(
        pageOf([userRow()], 1, 0, 25),
        membershipSettings({ recordsPerPage: 25 }),
      );

      expect(paramOf(listing, PAGE_SIZE_PARAM)).toBe('25');
    });

    it('sends offset paging only — no cursor, continuation token, link relation or skip/take pair', () => {
      const listing = arrive();

      // ⚠ THE ENVELOPE CARRIES A TOTAL AND A ZERO-BASED INDEX, WHICH IS OFFSET PAGING — the model the
      // legacy pager consumed. Every name below is a different paging model wearing the same clothes, and
      // none of them is detectable from a listing assertion that merely passes.
      for (const foreign of FOREIGN_PAGING_PARAMS) {
        expect(carries(listing, foreign))
          .withContext(`the listing must not send "${foreign}"`)
          .toBeFalse();
      }

      expect(carries(listing, PAGE_INDEX_PARAM)).toBeTrue();
      expect(carries(listing, PAGE_SIZE_PARAM)).toBeTrue();
    });

    it('sends no ordering parameters when the address asks for none, yet offers exactly five sortable headings', () => {
      const listing = arrive();

      expect(carries(listing, 'sortBy')).toBeFalse();
      expect(carries(listing, 'sortDir')).toBeFalse();

      // The five headings, named rather than counted. A bare count of five would pass if the WRONG five
      // were sortable, which would be worse than none: each of these was issued against the running
      // endpoint and observed to return 200 with a genuinely different order.
      const sortableHeadings: readonly string[] = queryAll('button.data-table__sort').map((button) =>
        (button.textContent ?? '').replace(/[\u25b2\u25bc]/g, '').trim(),
      );

      expect(sortableHeadings).toHaveSize(5);

      for (const heading of [
        USERNAME_HEADING,
        FIRST_NAME_HEADING,
        LAST_NAME_HEADING,
        DISPLAY_NAME_HEADING,
        EMAIL_HEADING,
      ]) {
        expect(sortableHeadings)
          .withContext(`"${heading}" must offer an ordering`)
          .toContain(heading);
      }

      for (const heading of [
        ADDRESS_HEADING,
        TELEPHONE_HEADING,
        CREATED_DATE_HEADING,
        LAST_LOGIN_HEADING,
        AUTHORIZED_HEADING,
      ]) {
        expect(sortableHeadings)
          .withContext(`"${heading}" must NOT offer an ordering — the endpoint refuses it`)
          .not.toContain(heading);
      }
    });

    /**
     * The offered set is bounded by the endpoint, not by the grid. `SortableFields.Users` in
     * `backend/src/DnnMigration.Application/Validation/SortableFields.cs` permits `UserId`, `Username`,
     * `FirstName`, `LastName`, `DisplayName`, `Email` and `IsSuperUser`; the five below are exactly those
     * of the seven this grid paints a column for.
     */
    it('offers a sort on exactly the columns the endpoint permits and paints', () => {
      arrive();

      const names: readonly string[] = queryAll<HTMLButtonElement>('button.data-table__sort').map(
        (control) => control.getAttribute('aria-label') ?? '',
      );

      expect(names).toEqual([
        'Sort by Username',
        'Sort by First Name',
        'Sort by Last Name',
        'Sort by Name',
        'Sort by Email',
      ]);
    });

    /**
     * ⚠ THE TRANSMITTED FIELD IS THE SERVER'S PASCAL-CASE MEMBER NAME, NOT THE CAMEL-CASED COLUMN KEY,
     * and this is the assertion that catches the confusion. The grid identifies its columns by keys this
     * screen spells `userName`, `displayName` and so on, while the endpoint’s allowlist is typed as
     * `UserSortField` and holds `Username`, `DisplayName` and the rest.
     */
    it('transmits the endpoint field name for the pressed column, in the server spelling', async () => {
      arrive();

      const controls = queryAll<HTMLButtonElement>('button.data-table__sort');

      // The fourth control is the column keyed `displayName`, whose endpoint field is `DisplayName` and
      // whose heading the resource file renames to "Name" - so all three spellings differ here.
      controls[3]?.click();
      fixture.detectChanges();
      await settleAddress();

      const ordered = expectListRead('the ordered listing read');

      expect(paramOf(ordered, 'sortBy')).toBe('DisplayName');
      expect(paramOf(ordered, 'sortDir')).toBe('Ascending');
      expect(paramOf(ordered, PAGE_INDEX_PARAM))
        .withContext('a row page depends on the ordering, so the coordinate returns to the first page')
        .toBe('0');

      ordered.flush(pageOf([userRow()]));
      fixture.detectChanges();

      // The heading announces it, which proves the projection BACK from the field name to the column key:
      // handed the field name instead, the grid would compare it against a column key and report nothing.
      const announced: readonly string[] = queryAll<HTMLElement>('th.data-table__header')
        .map((cell) => cell.getAttribute('aria-sort') ?? '')
        .filter((value) => value === 'ascending' || value === 'descending');

      expect(announced).toEqual(['ascending']);
    });

    it('clears the ordering on the third press, sending neither parameter again', async () => {
      arrive();

      const press = async (): Promise<void> => {
        queryAll<HTMLButtonElement>('button.data-table__sort')[0]?.click();
        fixture.detectChanges();
        await settleAddress();
      };

      const answer = (request: TestRequest): void => {
        request.flush(pageOf([userRow()]));
        fixture.detectChanges();
      };

      await press();
      answer(expectListRead('the ascending read'));
      await press();
      const descending = expectListRead('the descending read');
      expect(paramOf(descending, 'sortDir')).toBe('Descending');
      answer(descending);

      await press();
      const cleared = expectListRead('the unordered read');

      expect(carries(cleared, 'sortBy')).withContext('no key is sent').toBeFalse();
      expect(carries(cleared, 'sortDir')).withContext('no direction is sent').toBeFalse();

      answer(cleared);

      const announced: readonly string[] = queryAll<HTMLElement>('th.data-table__header')
        .map((cell) => cell.getAttribute('aria-sort') ?? '')
        .filter((value) => value === 'ascending' || value === 'descending');

      expect(announced).withContext('no column reports itself sorted').toEqual([]);
    });

    /**
     * ONE READER ACTION, ONE REQUEST. The store's single-coordinate setters each dispatch a read of their
     * own, so a screen expressing one ordering through both would issue two - and the first of the pair
     * asks for the new field in the OLD direction, a question nobody wanted answered. The address is
     * written once and the subscription watching it reads once, and only a count can prove it.
     */
    it('issues exactly one request per sort press', async () => {
      arrive();

      queryAll<HTMLButtonElement>('button.data-table__sort')[0]?.click();
      fixture.detectChanges();
      await settleAddress();

      const issued = httpMock.match(
        (candidate) => candidate.url === USERS_URL || candidate.url === USERS_SEARCH_URL,
      );

      expect(issued).toHaveSize(1);
      issued[0]?.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });
  });

  // ===================================================================================================
  // §3.2 — THE ZERO-BASED WIRE PAGE INDEX, WITH ITS NEGATIVE CONTROL
  // ===================================================================================================

  describe('the wire page index', () => {
    it('sends 0 for the first page', () => {
      const listing = arrive();

      expect(paramOf(listing, PAGE_INDEX_PARAM)).toBe('0');
    });

    it('never sends 1 for the first page', () => {
      const listing = arrive();

      expect(paramOf(listing, PAGE_INDEX_PARAM)).not.toBe('1');
    });

    it('sends 1 for the second page', async () => {
      arrive(pageOf([userRow(1)], 9, 0, TENANT_PAGE_SIZE));

      await pressPager(PAGER_NEXT_LABEL);

      const second = expectListRead('the second-page read');

      expect(paramOf(second, PAGE_INDEX_PARAM)).toBe('1');

      second.flush(pageOf([userRow(5)], 9, 1, TENANT_PAGE_SIZE));
      fixture.detectChanges();

      // And the reader is shown the one-based number for that same zero-based index.
      expect(pagerPosition()).toBe(`2 / ${Math.ceil(9 / TENANT_PAGE_SIZE)}`);
    });

    it('forwards the pager index unchanged, adding and subtracting nothing', async () => {
      // ⚠ THE BRANCH THIS COMPONENT TOOK, STATED EXPLICITLY: BOTH ENDS ARE ALREADY ZERO-BASED, so there is
      // NO ±1 ARITHMETIC in the screen at all.
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      await pressPager(PAGER_LAST_LABEL);

      const third = expectListRead('the last-page read');

      expect(paramOf(third, PAGE_INDEX_PARAM)).toBe('2');

      third.flush(pageOf([userRow(9)], 12, 2, TENANT_PAGE_SIZE));
      fixture.detectChanges();

      // The pager shows the ONE-BASED number for the same page, which is its own conversion and not this
      // screen's: the index bound to it is still 2.
      expect(pagerPosition()).toBe('3 / 3');
    });

    it('binds the index the SERVER reported, so the pager cannot claim a page whose request failed', async () => {
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      await pressPager(PAGER_NEXT_LABEL);

      expectListRead('the second-page read').flush(
        problem('server_error', 500, 'The server could not complete the request.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      // Still page one, because the page in hand is the one the server last reported.
      expect(pagerPosition()).toBe('1 / 3');
    });

    it('returns to the first page through the pager as well, sending 0 again', async () => {
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      await pressPager(PAGER_NEXT_LABEL);
      expectListRead('the second-page read').flush(
        pageOf([userRow(5)], 12, 1, TENANT_PAGE_SIZE),
      );
      fixture.detectChanges();

      await pressPager(PAGER_FIRST_LABEL);

      const back = expectListRead('the return to the first page');

      expect(paramOf(back, PAGE_INDEX_PARAM)).toBe('0');

      back.flush(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    });

    it('steps backwards to index 1 from the last page, never to a negative index', async () => {
      // The pager emits only a whole index inside the available range, and the store passes the index
      // straight to the transport without clamping it — so stepping back from the last of three pages must
      // ask for index one.
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      await pressPager(PAGER_LAST_LABEL);
      expectListRead('the last-page read').flush(
        pageOf([userRow(9)], 12, 2, TENANT_PAGE_SIZE),
      );
      fixture.detectChanges();

      await pressPager(PAGER_PREVIOUS_LABEL);

      const previous = expectListRead('the step backwards');

      expect(paramOf(previous, PAGE_INDEX_PARAM)).toBe('1');

      previous.flush(pageOf([userRow(5)], 12, 1, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    });
  });

  // ===================================================================================================
  // §3.3 — SEARCH AND THE ALPHABET STRIP RETURN TO THE FIRST PAGE
  // ===================================================================================================

  describe('returning to the first page', () => {
    /**
     * Moves the listing to its second page and leaves it there.
     *
     * @returns Nothing ; the second page is settled when this returns.
     */
    async function goToSecondPage(): Promise<void> {
      await pressPager(PAGER_NEXT_LABEL);

      const second = expectListRead('the second-page read');

      expect(paramOf(second, PAGE_INDEX_PARAM)).toBe('1');

      second.flush(pageOf([userRow(5)], 12, 1, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    }

    it('resets to the first page when a letter is activated from a later page', async () => {
      // `FilterURL` was called from the strip with a LITERAL page argument of "1", so a letter always
      // returned to the first page — and asking for the fifth page of a match set that now has one would
      // answer with nothing while the pager insisted there was something there.
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      await goToSecondPage();

      await pressLetter('B');

      const filtered = expectListRead('the letter-filtered read');

      expect(paramOf(filtered, PAGE_INDEX_PARAM)).toBe('0');
      expect(paramOf(filtered, USER_NAME_PARAM)).toBe('B');

      filtered.flush(pageOf([userRow(2)], 1, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    });

    it('resets to the first page for a new search term from a later page', async () => {
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      await goToSecondPage();

      await typeSearch('blog');

      const searched = expectListRead('the searched read');

      expect(paramOf(searched, PAGE_INDEX_PARAM)).toBe('0');
      expect(paramOf(searched, USER_NAME_PARAM)).toBe('blog');

      searched.flush(pageOf([userRow(3)], 1, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    });

    it('resets to the first page for the unfiltered affordance from a later page', async () => {
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      await goToSecondPage();

      await pressLetter(ALL_FILTER_LABEL);

      const unfiltered = expectListRead('the unfiltered read');

      expect(paramOf(unfiltered, PAGE_INDEX_PARAM)).toBe('0');

      unfiltered.flush(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    });
  });

  // ===================================================================================================
  // §3.4 — REMOVAL PRESERVES THE CURRENT PAGE: THE ASYMMETRY
  // ===================================================================================================

  describe('removal', () => {
    it('issues no request until the confirmation is accepted', () => {
      arrive(pageOf([userRow(7)]));

      requestRemoval();

      // THE CONFIRMATION IS A REAL DIALOGUE. `Page_Init` L522-L524 attached it as a JavaScript string on
      // the command column, which the framework emitted as a browser confirmation prompt; the shared
      // dialogue replaces it with a focus trap, escape handling and an accessible name the prompt had none
      // of.
      expect(dialog()).not.toBeNull();
      // ⚠ THE QUESTION IS ASSERTED AS A PREFIX AND THE RECORD BY NAME, WHICH IS STRONGER THAN THE EQUALITY
      // THIS REPLACES. The body used to be the bare legacy sentence and named nothing - searched against
      // every identifier on the page it matched none of them - while the dialog is a real modal that covers
      // the grid, including the row being destroyed. Keeping the sentence as a PREFIX is what still proves
      // the measured wording survives verbatim; asserting the name is what proves the operator can tell
      // which record is at risk without seeing the row.
      const body: string = textIn(queryOrFail<Element>(openDialog(), 'p.confirm-dialog__message'));

      expect(body.startsWith(REMOVAL_CONFIRM_MESSAGE))
        .withContext(`the measured question, verbatim, at the front of: ${body}`)
        .toBeTrue();
      expect(body).toContain('jbloggs');
      httpMock.expectNone(userUrl(7));
    });

    it('renders no delete command at all for a row the server will not let go', () => {
      arrive(pageOf([userRow(7, { canDelete: false })]));

      const actions: readonly HTMLElement[] = queryAll<HTMLElement>(ROW_ACTION_SELECTOR);
      const names: readonly (string | null)[] = actions.map((candidate) =>
        candidate.getAttribute('aria-label'),
      );

      expect(names.some((name) => name !== null && name.startsWith(DELETE_COMMAND_LABEL)))
        .withContext('the delete command is absent from a protected row')
        .toBeFalse();

      // The OTHER two row commands are untouched. Withholding a removal must not withhold editing
      // or role management: the legacy hid one image column, not the whole command group.
      expect(names.some((name) => name !== null && name.startsWith(EDIT_COMMAND_LABEL)))
        .withContext('editing remains available on a protected row')
        .toBeTrue();
      expect(names.some((name) => name !== null && name.startsWith(MANAGE_ROLES_COMMAND_LABEL)))
        .withContext('role management remains available on a protected row')
        .toBeTrue();
    });

    it('offers the delete command for an ordinary row and withholds it only from the protected one', () => {
      // Both rows in one page, so the withholding is proved to be PER ROW rather than per listing. The two
      // carry DIFFERENT account names, because the accessible name is what identifies which row a command
      // belongs to and identical names would make the surviving one unattributable.
      arrive(
        pageOf([
          userRow(7, { canDelete: true, username: 'ordinary_member' }),
          userRow(9, { canDelete: false, username: 'site_administrator' }),
        ]),
      );

      const names: readonly string[] = queryAll<HTMLElement>(ROW_ACTION_SELECTOR)
        .map((candidate) => candidate.getAttribute('aria-label') ?? '')
        .filter((name) => name.startsWith(DELETE_COMMAND_LABEL));

      expect(names.length).withContext('one command for one of the two rows').toBe(1);
      expect(names[0]).toContain('ordinary_member');
      expect(names[0]).not.toContain('site_administrator');
    });

    it('issues no request at all when the confirmation is abandoned', () => {
      arrive(pageOf([userRow(7)]));

      requestRemoval();
      pressIn(openDialog(), 'Cancel');

      expect(dialog()).toBeNull();
      httpMock.expectNone(userUrl(7));
      expect(notifySpy).not.toHaveBeenCalled();
    });

    it('removes the confirmed account and handles the empty 204 body without error', () => {
      arrive(pageOf([userRow(7)]));

      const removal = confirmRemoval(7);

      removal.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      answerListing(pageOf([userRow(8)]));
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledWith(
        'success',
        USER_DELETED_MESSAGE,
        null,
        false,
        null,
        null,
      );
    });

    it('re-reads the SAME page after a removal, never the first', async () => {
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      await pressPager(PAGER_NEXT_LABEL);
      expectListRead('the second-page read').flush(
        pageOf([userRow(5)], 12, 1, TENANT_PAGE_SIZE),
      );
      fixture.detectChanges();

      confirmRemoval(5).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      const reload = expectListRead('the re-read after removal');

      expect(paramOf(reload, PAGE_INDEX_PARAM)).toBe('1');

      reload.flush(pageOf([userRow(6)], 11, 1, TENANT_PAGE_SIZE));
      fixture.detectChanges();
      settleOutcome();
    });

    it('re-reads the listing rather than splicing the row out locally', () => {
      arrive(pageOf([userRow(7), userRow(8)], 2));

      expect(rows()).toHaveSize(2);

      confirmRemoval(7).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      const reread = expectListRead('the re-read after removal');

      expect(rows())
        .withContext('the rows stay on screen while the re-read is in flight')
        .toHaveSize(2);
      expect(query('table.data-table')?.getAttribute('aria-busy'))
        .withContext('and the grid reports the read instead')
        .toBe('true');

      reread.flush(pageOf([userRow(8)], 1));
      fixture.detectChanges();
      settleOutcome();

      expect(rows()).toHaveSize(1);
      expect(textIn(cellUnder(USERNAME_HEADING))).toBe('jbloggs');
    });

    it('reports a failed removal with the legacy wording and the failure severity', () => {
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(
        problem('server_error', 500, 'The account could not be removed.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledWith(
        'error',
        `${USER_DELETE_ERROR_MESSAGE} The account could not be removed.`,
        TRACE_ID,
      );
    });

    it('surfaces a refused removal at WARNING severity, not error', () => {
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(bareProblem('forbidden', 403), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledWith(
        'warning',
        `${USER_DELETE_ERROR_MESSAGE} ${FORBIDDEN_MESSAGE}`,
        TRACE_ID,
      );
    });

    it('reports a refusal that DOES carry a sentence at warning severity too', () => {
      // Severity comes from the status, never from the presence or absence of wording.
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(
        problem('forbidden', 403, 'That account is the portal administrator.'),
        { status: 403, statusText: 'Forbidden' },
      );
      fixture.detectChanges();
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledWith(
        'warning',
        `${USER_DELETE_ERROR_MESSAGE} That account is the portal administrator.`,
        TRACE_ID,
      );
    });

    it('prefers the correlation identifier over the trace identifier when the server sends both', () => {
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(
        {
          ...problem('server_error', 500, 'Storage is unavailable.'),
          correlationId: CORRELATION_ID,
        },
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();
      settleOutcome();

      const [, , reference] = notifySpy.calls.mostRecent().args;

      expect(reference).toBe(CORRELATION_ID);
    });

    it('retains the trace identifier the server supplied, passed as its own argument', () => {
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(problem('server_error', 500, 'Storage is unavailable.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();
      settleOutcome();

      const [, , reference] = notifySpy.calls.mostRecent().args;

      expect(reference).toBe(TRACE_ID);
    });

    it('leaves the inline failure surface alone for a failed removal', () => {
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(problem('server_error', 500, 'Storage is unavailable.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();
      settleOutcome();

      // ⚠ THE SURFACE IS THE BANNER'S CONTENTS, NOT THE BANNER ELEMENT. The element is mounted
      // unconditionally so that its assertive live region survives between failures rather than being
      // created with its first message; what a failed WRITE must leave alone is the painted read-failure
      // banner inside it, which is what this asserts.
      expect(query('app-error-banner .error-banner'))
        .withContext('a failed removal paints no read-failure banner')
        .toBeNull();
      expect(rows()).toHaveSize(1);
    });

    it('does not report a failed re-read as a failed removal', () => {
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expectListRead('the re-read after removal').flush(
        problem('server_error', 500, 'Storage is unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledWith(
        'success',
        USER_DELETED_MESSAGE,
        null,
        false,
        null,
        null,
      );
      // The PAINTED banner, not merely the element: the element is always mounted, so asserting its
      // presence would now hold whether or not the re-read failure was reported at all.
      expect(query('app-error-banner .error-banner'))
        .withContext("the re-read's failure is painted in the shared banner")
        .not.toBeNull();
    });

    it('reports one outcome per removal, however many unrelated signals move afterwards', () => {
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerListing(pageOf([userRow(8)]));
      settleOutcome();

      // Draining again, and moving a signal the screen reads, must not report the same outcome twice:
      // the effect acts on a TRANSITION and clears its own marker before reporting.
      settleOutcome();
      // Moving a signal the screen reads must not report the same outcome twice. The tenant record is no
      // longer one of them — see U-M5 — so the caller's own identity is poked instead, which the row-level
      // removal guard genuinely still consults.
      callerIdentity.set(callerAccount({ userId: 4242 }));
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledTimes(1);
    });
  });

  // ===================================================================================================
  // §3.5 — THE COMPOSED SEARCH AXIS AND ITS DYNAMIC OPTION LIST
  // ===================================================================================================

  describe('the search axis', () => {
    /** The rendered option list, in document order. */
    function axisOptions(): readonly string[] {
      return Array.from(
        queryOrFail<HTMLSelectElement>(host(), `#${SEARCH_FIELD_CONTROL_ID}`).options,
      ).map((option) => textIn(option));
    }

    /** The transmitted values of the rendered option list, in document order. */
    function axisValues(): readonly string[] {
      return Array.from(
        queryOrFail<HTMLSelectElement>(host(), `#${SEARCH_FIELD_CONTROL_ID}`).options,
      ).map((option) => option.value);
    }

    it('offers the two account fields first, then one entry per tenant-declared property', () => {
      arrive();

      expect(axisValues()).toEqual([
        'Username',
        'Email',
        ODD_PROPERTY_NAME,
        SECOND_PROPERTY_NAME,
      ]);
    });

    it('renders a tenant property whose name COLLIDES with an account field, keeping both entries', () => {
      // ⚠ THE TRACKING-KEY REGRESSION GUARD, AND THE CONFIGURATION IS REAL RATHER THAN CONTRIVED. The
      // profile axis is an OPEN SET, so a tenant may declare a property literally named `Username`, and the
      // legacy order — account fields first, then declarations, with `Items.Insert` never used — means the
      // collision is PRESERVED rather than de-duplicated: the legacy switch tested the account field first,
      // so the account field wins the query and the duplicate entry still appears in the list.
      arrive(pageOf([userRow()]), membershipSettings(), [
        profileDefinition('Username', 21),
        profileDefinition(SECOND_PROPERTY_NAME, 22),
      ]);

      expect(axisValues()).toEqual(['Username', 'Email', 'Username', SECOND_PROPERTY_NAME]);
      expect(axisOptions()).toEqual(['Username', 'Email', 'Username', SECOND_PROPERTY_NAME]);
    });

    it('labels a property the resource file knows, and falls back to the raw name for one it does not', () => {
      // `AddSearchItem` resolved each entry through a resource lookup and FELL BACK TO THE RAW NAME when
      // the lookup returned nothing.
      arrive();

      expect(axisOptions()).toEqual([
        'Username',
        'Email',
        ODD_PROPERTY_NAME,
        SECOND_PROPERTY_NAME,
      ]);
    });

    it('offers the free-text control with its own placeholder and its own label', () => {
      // ⚠ U-M13 — THAT LAST SENTENCE IS WITHDRAWN AND THE PLACEHOLDER NOW STATES THE PREDICATE. Leaving the
      // rule unstated was defensible only while the rule was genuinely the server's private business, and
      // it is not: the endpoint appends exactly one trailing wildcard, which is a starts-with, and it is
      // the same rule the twenty-seven-entry alphabet strip beside the box depends on.
      arrive();

      const field = queryOrFail<HTMLInputElement>(host(), SEARCH_INPUT_SELECTOR);

      expect(field.type).toBe('search');
      expect(field.placeholder).toBe(SEARCH_PLACEHOLDER);
      expect(field.placeholder).withContext('states the predicate').toContain('Begins with');
      expect(textOf('label.search-input__label')).toEqual(['Search:']);
    });

    it('names the selector, which the legacy control never was', () => {
      arrive();

      const label = queryOrFail<HTMLLabelElement>(host(), 'label.form-field__label');

      expect(textIn(label)).toContain(SEARCH_FIELD_CAPTION);
      expect(label.getAttribute('for')).toBe(SEARCH_FIELD_CONTROL_ID);
    });

    it('takes the declarations from the UNPAGED slice, sending no coordinate of any kind', () => {
      create();
      answerSettings();

      const definitions = expectRequest('GET', PROFILE_DEFINITIONS_URL);

      // ⚠ UNPAGED, AND EVERY COORDINATE IS ABSENT RATHER THAN DEFAULTED. The transport documents that no
      // page coordinate, no ordering and no filter is emitted on this call — not an empty one, not a
      // defaulted one — because the answer is already final.
      expect(definitions.request.params.keys()).toHaveSize(0);

      definitions.flush(envelope(PROFILE_DEFINITIONS));
      fixture.detectChanges();
      answerListing();
    });

    it('offers only the two account fields when the tenant has declared no property', () => {
      arrive(pageOf([userRow()]), membershipSettings(), []);

      expect(axisValues()).toEqual(['Username', 'Email']);
    });

    it('still lists the accounts when the declarations cannot be read', () => {
      create();
      answerSettings();

      expectRequest('GET', PROFILE_DEFINITIONS_URL).flush(
        problem('server_error', 500, 'The declarations are unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();
      answerListing();

      expect(rows()).toHaveSize(1);
      expect(axisValues()).toEqual(['Username', 'Email']);
    });

    it('searches the account name on the axis the screen opens with', async () => {
      // Seeded to the account name because L577 added that entry FIRST and `AddSearchItem` selected an
      // entry only when it matched a `filterProperty` query-string value, so with no query string the first
      // entry was the selected one.
      arrive();

      await typeSearch('blog');

      const searched = expectListRead('the account-name search');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('blog');
      expect(carries(searched, EMAIL_PARAM)).toBeFalse();
      expect(carries(searched, PROFILE_PROPERTY_NAME_PARAM)).toBeFalse();
      expect(carries(searched, PROFILE_PROPERTY_VALUE_PARAM)).toBeFalse();

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('searches the address when that axis is chosen', async () => {
      arrive();

      chooseAxis('Email');
      await typeSearch('jbloggs@');

      const searched = expectListRead('the address search');

      expect(paramOf(searched, EMAIL_PARAM)).toBe('jbloggs@');
      expect(carries(searched, USER_NAME_PARAM)).toBeFalse();
      expect(carries(searched, PROFILE_PROPERTY_NAME_PARAM)).toBeFalse();

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('searches a profile property by name and value when a declared property is chosen', async () => {
      arrive();

      chooseAxis(ODD_PROPERTY_NAME);
      await typeSearch('Bris');

      const searched = expectListRead('the profile-property search');

      expect(paramOf(searched, PROFILE_PROPERTY_NAME_PARAM)).toBe(ODD_PROPERTY_NAME);
      expect(paramOf(searched, PROFILE_PROPERTY_VALUE_PARAM)).toBe('Bris');
      expect(carries(searched, USER_NAME_PARAM)).toBeFalse();
      expect(carries(searched, EMAIL_PARAM)).toBeFalse();

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('transmits the property name VERBATIM — not validated, not case-folded, not restricted', async () => {
      arrive();

      chooseAxis(ODD_PROPERTY_NAME);
      await typeSearch('anything');

      const searched = expectListRead('the verbatim property search');
      const transmitted: string = paramOf(searched, PROFILE_PROPERTY_NAME_PARAM);

      expect(transmitted).toBe(ODD_PROPERTY_NAME);
      expect(transmitted).not.toBe(ODD_PROPERTY_NAME.toLowerCase());
      expect(transmitted).not.toBe(ODD_PROPERTY_NAME.trim().replace(/\s+/g, ''));

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('keeps every searched value out of the request target', async () => {
      arrive();

      await typeSearch('jbloggs');

      const byName = expectListRead('the account-name search');

      expect(byName.request.method).toBe('POST');
      expect(byName.request.urlWithParams)
        .withContext('a searched account name must never reach a request target')
        .not.toContain('jbloggs');
      byName.flush(pageOf([userRow()]));
      fixture.detectChanges();

      chooseAxis('Email');
      await typeSearch('jbloggs@example.test');

      const byAddress = expectListRead('the address search');

      expect(byAddress.request.urlWithParams)
        .withContext('a searched address must never reach a request target')
        .not.toContain('jbloggs@example.test');
      byAddress.flush(pageOf([userRow()]));
      fixture.detectChanges();

      chooseAxis(ODD_PROPERTY_NAME);
      await typeSearch('Bris');

      const byProperty = expectListRead('the profile-property search');
      const target = byProperty.request.urlWithParams;

      // BOTH halves. The name discloses what the tenant collects about its members and the value is
      // arbitrary tenant data whose meaning neither side knows.
      expect(target)
        .withContext('a profile property NAME must never reach a request target')
        .not.toContain(ODD_PROPERTY_NAME);
      expect(target)
        .withContext('a profile property VALUE must never reach a request target')
        .not.toContain('Bris');
      byProperty.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('leaves the unfiltered listing on the cacheable GET, because it names nobody', async () => {
      // The boundary in the other direction, and it matters: moving a read that identifies nobody
      // into a body would give up caching and idempotence for no privacy gain whatsoever.
      arrive();

      await typeSearch('jbloggs');
      expectListRead('the search').flush(pageOf([userRow()]));
      fixture.detectChanges();

      await pressLetter(ALL_FILTER_LABEL);

      const unfiltered = expectListRead('the unfiltered read');

      expect(unfiltered.request.method)
        .withContext('page coordinates and an ordering identify nobody')
        .toBe('GET');
      expect(unfiltered.request.url).toBe(USERS_URL);
      unfiltered.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('issues nothing when the axis alone is changed', () => {
      arrive();

      chooseAxis('Email');

      expectNoListRead();
    });
  });

  // ===================================================================================================
  // §3.6 — THE TERM TRAVELS RAW: NO CLIENT-SIDE WILDCARD
  // ===================================================================================================

  describe('the search term', () => {
    // Every case here starts from a settled arrival, so each one is about the term and nothing else.
    beforeEach(() => {
      arrive();
    });

    /**
     * Runs one search and hands back the request, having asserted nothing yet.
     *
     * @param term The text to type.
     * @returns The listing request the search provoked.
     */
    async function searchFor(term: string): Promise<TestRequest> {
      await typeSearch(term);

      return expectListRead(`the search for ${JSON.stringify(term)}`);
    }

    it('transmits exactly what was typed, appending no wildcard', async () => {
      const searched = await searchFor('Blog');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('Blog');

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('sends no per-cent character in any transmitted value', async () => {
      const searched = await searchFor('Blog');

      for (const name of searched.request.params.keys()) {
        const value: string | null = searched.request.params.get(name);

        expect(value === null ? '' : value)
          .withContext(`the parameter "${name}" must carry no wildcard`)
          .not.toContain('%');
      }

      const sent = searchMembers(searched);

      expect(sent).withContext('an identifying search travels in a body').not.toBeNull();

      for (const [name, value] of Object.entries(sent ?? {})) {
        if (typeof value !== 'string') {
          continue;
        }

        expect(value)
          .withContext(`the body member "${name}" must carry no wildcard`)
          .not.toContain('%');
      }

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('preserves a per-cent character the reader typed, rather than escaping or stripping it', async () => {
      const searched = await searchFor('100%');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('100%');

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('does not trim the term', async () => {
      // Trimming would make a leading space unsearchable, and a stored name may legitimately carry one.
      const searched = await searchFor('  Blog ');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('  Blog ');

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('does not case-fold the term', async () => {
      // Case-folding would presume a collation this side does not know; the comparison is the server's.
      const searched = await searchFor('BlOgGs');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('BlOgGs');

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('does not hand-encode the term, leaving any encoding to the transport', async () => {
      const searched = await searchFor('a b&c=d');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('a b&c=d');
      expect(searched.request.method).toBe('POST');
      expect(searched.request.urlWithParams)
        .withContext('the whole target, query included, is now free of the term')
        .toBe(USERS_SEARCH_URL);
      expect(JSON.stringify(searched.request.body))
        .withContext('nothing per-cent-escapes the text on its way into the body')
        .not.toContain('%25');

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('searches for a term the legacy screen could never search for', async () => {
      const searched = await searchFor(ALL_FILTER_LABEL);

      expect(paramOf(searched, USER_NAME_PARAM)).toBe(ALL_FILTER_LABEL);

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('searches for the empty term as a real value rather than dropping the filter', async () => {
      await typeSearch('present');
      expectListRead('the first search').flush(pageOf([userRow()]));
      fixture.detectChanges();

      await typeSearch('');

      const cleared = expectListRead('the emptied search');

      expect(carries(cleared, USER_NAME_PARAM)).toBeTrue();
      expect(paramOf(cleared, USER_NAME_PARAM)).toBe('');

      cleared.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });
  });

  // ===================================================================================================
  // §3.7 — THE "None" SENTINEL BECOMES PARAMETER OMISSION, AND "All" IS A FOURTH BRANCH
  // ===================================================================================================

  describe('the reserved words', () => {
    it('never transmits the literal "None"', async () => {
      arrive();

      await typeSearch('None');

      const searched = expectListRead('the search for the word "None"');

      // Transmitted as a TERM under the account-name filter, never as a mode.
      expect(paramOf(searched, USER_NAME_PARAM)).toBe('None');

      for (const name of searched.request.params.keys()) {
        expect(name).withContext('no parameter is named after a legacy mode').not.toBe('mode');
        expect(name).not.toBe('filter');
        expect(name).not.toBe('filterProperty');
      }

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('issues a paged, unfiltered request for the "All" affordance, carrying no search parameter', async () => {
      arrive();

      await typeSearch('narrowed');
      expectListRead('the narrowing search').flush(pageOf([userRow()]));
      fixture.detectChanges();

      await pressLetter(ALL_FILTER_LABEL);

      const unfiltered = expectListRead('the unfiltered read');

      for (const name of SEARCH_PARAMS) {
        expect(carries(unfiltered, name))
          .withContext(`the unfiltered read must not carry "${name}"`)
          .toBeFalse();
      }

      expect(carries(unfiltered, GENERIC_QUERY_PARAM)).toBeFalse();

      // The page coordinates are still present: unfiltered is not unpaged.
      expect(carries(unfiltered, PAGE_INDEX_PARAM)).toBeTrue();
      expect(carries(unfiltered, PAGE_SIZE_PARAM)).toBeTrue();

      unfiltered.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('opens on the unfiltered listing, so the screen is never blank on arrival', () => {
      // MIGRATION, AND A DOCUMENTED DIVERGENCE THE STORE OWNS. `Page_Init` L494-L506 chose the opening view
      // from the tenant's `Display_Mode` setting, and `UserModuleBase.vb` L126-L130 defaulted it to
      // `DisplayMode.None` — so a tenant that had configured nothing opened this screen with NO QUERY
      // ISSUED and NO ROWS at all until the operator acted.
      const listing = arrive();

      for (const name of SEARCH_PARAMS) {
        expect(carries(listing, name)).toBeFalse();
      }

      expect(carries(listing, GENERIC_QUERY_PARAM)).toBeFalse();
      expect(rows()).toHaveSize(1);
    });

    it('transmits no reserved word of its own, in any parameter', () => {
      const listing = arrive();

      for (const name of listing.request.params.keys()) {
        const value: string | null = listing.request.params.get(name);
        const sent: string = value === null ? '' : value;

        expect(sent).withContext(`"${name}" must carry no legacy mode`).not.toBe('None');
        expect(sent).not.toBe(ALL_FILTER_LABEL);
        expect(sent).not.toBe('Online');
        expect(sent).not.toBe('Unauthorized');
      }
    });

    it('sends no tenant identifier, because the API resolves the tenant from the request', () => {
      // The sentinel rule still applies to the value ITSELF, which arrives on every row and must survive
      // being minus one; that is asserted with the identity values.
      const listing = arrive(pageOf([userRow(7, { portalId: -1 })]));

      expect(carries(listing, 'portalId')).toBeFalse();
      expect(listing.request.urlWithParams).not.toContain('portalId');
      expect(listing.request.url).toBe(USERS_URL);
    });

    it('sends no approval restriction, because the legacy listing showed both states', () => {
      // The unauthorised-only affordance the legacy strip offered is NOT reproduced — it was answered from
      // an unpaged reader that took no page coordinate and no endpoint serves it — so the listing never
      // restricts on approval and the column reports the state instead.
      const listing = arrive();

      expect(carries(listing, 'isApproved')).toBeFalse();
    });

    it('ANNOUNCES the applied entry, which the legacy strip never did', async () => {
      arrive();

      // On arrival the unfiltered listing is what the tenant policy asked for, so its entry is the
      // pressed one and the twenty-six letters are not.
      expect(pressedAffordances())
        .withContext('exactly one entry is ever pressed')
        .toEqual([ALL_FILTER_LABEL]);

      await pressLetter('C');
      expectListRead('the letter search').flush(pageOf([userRow(1)]));
      fixture.detectChanges();

      expect(pressedAffordances()).toEqual(['C']);
    });

    it('emits aria-pressed on EVERY entry, so the attribute is a state and not a marker', () => {
      arrive();

      const entries = queryAll<HTMLButtonElement>(LETTER_SELECTOR);

      expect(entries).toHaveSize(27);

      for (const entry of entries) {
        expect(entry.getAttribute('aria-pressed'))
          .withContext(`"${textIn(entry)}" must carry a value rather than nothing`)
          .not.toBeNull();
      }

      expect(
        entries.filter((entry) => entry.getAttribute('aria-pressed') === 'false'),
      ).toHaveSize(26);
    });

    it('shows NO entry as applied when the search is on an axis the strip does not offer', async () => {
      arrive();

      chooseAxis('Email');
      await typeSearch('a@example.test');
      expectListRead('the address search').flush(pageOf([userRow(1)]));
      fixture.detectChanges();

      expect(pressedAffordances()).toEqual([]);
    });

    it('matches a letter case-INSENSITIVELY, so the strip agrees with the listing it describes', async () => {
      arrive();

      await typeSearch('c');
      expectListRead('the lower-case letter search').flush(pageOf([userRow(1)]));
      fixture.detectChanges();

      expect(pressedAffordances()).toEqual(['C']);
    });

    it('shows no entry as applied for a MULTI-CHARACTER prefix, which no letter describes', async () => {
      // "Ca" is a sign-in prefix search, but it is not the letter "C": pressing "C" would change the
      // listing, so reporting "C" as applied would be a lie about what the grid is showing.
      arrive();

      await typeSearch('Ca');
      expectListRead('the two-character search').flush(pageOf([userRow(1)]));
      fixture.detectChanges();

      expect(pressedAffordances()).toEqual([]);
    });

    it('offers twenty-six letters and the unfiltered affordance, and nothing else', () => {
      arrive();

      const affordances: readonly string[] = textOf(LETTER_SELECTOR);

      expect(affordances).toHaveSize(27);
      expect(affordances[0]).toBe('A');
      expect(affordances[25]).toBe('Z');
      expect(affordances[26]).toBe(ALL_FILTER_LABEL);
      expect(affordances).not.toContain('Online');
      expect(affordances).not.toContain('Unauthorized');
      expect(affordances).not.toContain('0-9');
    });

    it('treats a letter as a prefix search on the axis currently chosen, not as a query of its own', async () => {
      arrive();

      chooseAxis('Email');
      await pressLetter('A');

      const filtered = expectListRead('the letter search on the address axis');

      expect(paramOf(filtered, EMAIL_PARAM)).toBe('A');
      expect(carries(filtered, USER_NAME_PARAM)).toBeFalse();

      filtered.flush(pageOf([]));
      fixture.detectChanges();
    });
  });

  // ===================================================================================================
  // §3.8 — IDENTITY VALUES THAT COLLIDE WITH THE LEGACY NULL MARKER
  // ===================================================================================================

  describe('identity values', () => {
    it('treats an account identifier of nought as a real identifier in its route', () => {
      // ⚠ DEFENSIVE, AND THE NUANCE MATTERS. `dbo.Users.UserID` is declared `IDENTITY (1, 1)`
      // (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L98), so an account
      // keyed nought DOES NOT OCCUR NATURALLY — this is a test of the SENTINEL RULE, not a schema fact.
      arrive(pageOf([userRow(0)]));

      const edit = rowAction(EDIT_COMMAND_LABEL);

      expect(edit.getAttribute('href')).toBe('/users/0');
    });

    it('removes an account identified as nought at its own address', () => {
      arrive(pageOf([userRow(0)]));

      const removal = confirmRemoval(0);

      // The identifier reaches the address as written: `/users/0`, never `/users/` and never a fallback.
      expect(removal.request.url).toBe(`${USERS_URL}/0`);

      removal.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerListing(pageOf([]));
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledWith(
        'success',
        USER_DELETED_MESSAGE,
        null,
        false,
        null,
        null,
      );
    });

    it('retains a tenant identifier of minus one on the row it renders', () => {
      // ⚠ THIS ONE IS A SCHEMA FACT RATHER THAN A DEFENSIVE CASE. `dbo.Portals.PortalID` is `IDENTITY (-1,
      // 1)` at L77, so minus one is the FIRST REAL PORTAL — and it is simultaneously `Null.NullInteger`.
      arrive(pageOf([userRow(7, { portalId: -1 })]));

      expect(rows()).toHaveSize(1);
      expect(rowAction(EDIT_COMMAND_LABEL).getAttribute('href')).toBe('/users/7');
    });

    it('renders a row whose every nullable member is absent, and never a placeholder WORD', () => {
      // ⚠ U-M2 — THE TWO PROFILE CELLS ARE NO LONGER EXPECTED TO BE EMPTY, AND THIS CASE IS WHERE THAT
      // CHANGED. It used to require them to paint nothing at all.
      arrive(
        pageOf([
          userRow(7, {
            address: null,
            telephone: null,
            createdDate: null,
            lastLoginDate: null,
            firstName: '',
            lastName: '',
            displayName: '',
            email: '',
          }),
        ]),
      );

      const painted: readonly string[] = dataCellsOf(
        queryOrFail<Element>(host(), ROW_SELECTOR),
      ).map((cell) => textIn(cell));

      expect(painted).not.toContain('null');
      expect(painted).not.toContain('undefined');
      expect(painted).not.toContain('NaN');
      expect(painted).withContext('no placeholder word').not.toContain('None');
      expect(painted).not.toContain('N/A');
      expect(painted).not.toContain('Unknown');

      // The mark is hidden from assistive technology and the sentence beside it is hidden from sight, so
      // each cell carries exactly one of the two for each kind of reader.
      //
      // ⚠ THE TWO SPANS THIS SCREEN COMPOSED ITSELF ARE NOW THE SHARED ABSENT-VALUE COMPONENT — QA-15. The
      // rendering is unchanged in every respect a reader can perceive; what changed is that the mark, its
      // colour and the wording are now emitted from ONE place for all four listings, which is the defect a
      // per-screen pair could not fix however correct each copy was.
      for (const heading of [ADDRESS_HEADING, TELEPHONE_HEADING]) {
        const cell = cellUnder(heading);
        const absent = queryOrFail<HTMLElement>(cell, 'app-absent-value');
        const mark = queryOrFail<HTMLElement>(absent, 'span.absent-value__mark');
        const description = queryOrFail<HTMLElement>(absent, 'span.absent-value__description');

        expect(mark.textContent).withContext('an em dash, not a word').toBe('\u2014');
        expect(mark.getAttribute('aria-hidden')).toBe('true');
        expect((description.textContent ?? '').trim()).toBe('not recorded');
        expect(description.getAttribute('aria-hidden')).withContext('exposed').toBeNull();
      }
    });

    it('paints a recorded profile value and no mark beside it', () => {
      // U-M2's counterpart: the mark must appear ONLY where nothing would otherwise be painted, or it
      // would be claiming an absence that is not there.
      arrive(pageOf([userRow(7, { address: '12 Example Street', telephone: '555-0100' })]));

      expect(textIn(cellUnder(ADDRESS_HEADING))).toBe('12 Example Street');
      expect(textIn(cellUnder(TELEPHONE_HEADING))).toBe('555-0100');
      expect(cellUnder(ADDRESS_HEADING).querySelector('app-absent-value')).toBeNull();
      expect(cellUnder(TELEPHONE_HEADING).querySelector('app-absent-value')).toBeNull();
    });

    it('marks a whitespace-only profile value as absent, because nothing would be painted', () => {
      arrive(pageOf([userRow(7, { address: '   ', telephone: '' })]));

      for (const heading of [ADDRESS_HEADING, TELEPHONE_HEADING]) {
        expect(cellUnder(heading).querySelector('app-absent-value')).not.toBeNull();
      }
    });
  });

  // ===================================================================================================
  // §3.9 — THE MINIMUM-VALUE DATE RENDERS BLANK, AND THAT IS PARITY
  // ===================================================================================================

  describe('the sentinel date', () => {
    /**
     * Asserts that a date cell states its absence through the SHARED component rather than through emptiness,
     * and that no year-one date reaches the screen.
     *
     * Structural rather than text-matched, on the same terms as the profile-value section above: the mark and
     * its expansion belong to one component, and naming its elements is what keeps this specification true if
     * the wording is ever revised.
     *
     * @param heading The column heading whose cell to inspect.
     */
    function expectAbsentInstant(heading: string): void {
      const cell = cellUnder(heading);
      const absent = queryOrFail<HTMLElement>(cell, 'app-absent-value');

      expect(queryOrFail<HTMLElement>(absent, 'span.absent-value__mark').getAttribute('aria-hidden'))
        .withContext('the mark is decoration')
        .toBe('true');
      expect(
        (queryOrFail<HTMLElement>(absent, 'span.absent-value__description').textContent ?? '').trim(),
      ).toBe('not recorded');
      expect(textIn(cell)).withContext('and no date in the year one anywhere').not.toContain('0001');
    }

    /**
     * Paints one row carrying the given instants and returns the two date cells as text.
     *
     * @param createdDate The creation instant, or null.
     * @param lastLoginDate The last sign-in instant, or null.
     * @returns The created-date cell text and the last-login cell text.
     */
    function dateCells(
      createdDate: string | null,
      lastLoginDate: string | null,
    ): readonly [string, string] {
      arrive(pageOf([userRow(7, { createdDate, lastLoginDate })]));

      return [textIn(cellUnder(CREATED_DATE_HEADING)), textIn(cellUnder(LAST_LOGIN_HEADING))];
    }

    it('renders the sentinel instant as the shared absent value, never as a year-one date', () => {
      // ⚠ THIS EXPECTATION CHANGED, AND THE HALF OF IT THAT MATTERS DID NOT. What must never appear is a
      // date in the year one: `DisplayDate` seeded its result with `Null.NullString` and returned `""` when
      // `Null.IsNull` recognised the instant, so `01/01/0001` would be the real behavioural change, and that
      // is still asserted below.
      //
      // What changed is what appears INSTEAD. An empty cell was defended here as legacy parity, and as
      // parity it was correct — but QA-20 found the consequence: on a grid where an absent address and an
      // absent telephone number both paint the shared mark, these two columns alone painted nothing, so one
      // row disagreed with itself about how it reports a missing value and a reader could not tell an
      // unrecorded instant from a cell that failed to draw. The affordance is the same one used by every
      // other listing, and the divergence from the blank legacy cell is recorded in MIGRATION_NOTES.
      dateCells('0001-01-01T00:00:00Z', '0001-01-01T00:00:00Z');

      expectAbsentInstant(CREATED_DATE_HEADING);
      expectAbsentInstant(LAST_LOGIN_HEADING);
    });

    it('renders the sentinel DATE with a non-zero time component the same way', () => {
      dateCells('0001-01-01T13:45:30Z', '0001-01-01T23:59:59Z');

      expectAbsentInstant(CREATED_DATE_HEADING);
      expectAbsentInstant(LAST_LOGIN_HEADING);
    });

    it('renders an absent instant identically to the sentinel', () => {
      dateCells(null, null);

      // The two states are told apart in STORAGE and deliberately not on screen: "no instant recorded" is
      // the same fact to a reader whichever way the database spells it.
      expectAbsentInstant(CREATED_DATE_HEADING);
      expectAbsentInstant(LAST_LOGIN_HEADING);
    });

    it('refuses an unparseable instant at the boundary rather than rendering one', () => {
      // ⚠ DIVERGENCE FROM THIS FILE'S BRIEF, RESOLVED IN FAVOUR OF THE CODE, AND REPORTED. The brief asks
      // for "an unparseable value renders the same empty string".
      create();
      answerSettings();
      answerDefinitions();

      expectListRead('the listing read').flush(
        pageOf([userRow(7, { createdDate: 'not-a-date' })]),
      );
      fixture.detectChanges();

      // ⚠ THE PLACEHOLDER CHANGED, AND THE OLD ONE ASSERTED A FALSEHOOD. A response this client could not
      // decode used to fall through to the shared empty state, which reads "Nothing to Display / No records
      // found." - so a contract violation was presented to an operator as a tenant with no accounts in it.
      // The grid is now told the read FAILED and says only that the records could not be read, leaving the
      // reason to the banner above it.
      expect(rows()).toHaveSize(0);
      expect(host().textContent ?? '').not.toContain('not-a-date');
      expect(query('app-empty-state'))
        .withContext('a failure is never presented as an empty database')
        .toBeNull();
      expect(host().textContent ?? '').toContain('could not be read');
    });

    it('renders a real instant, and renders it with its time as well as its date', () => {
      const [created, lastLogin] = dateCells(
        '2006-03-02T09:15:00Z',
        '2006-04-18T16:42:30Z',
      );

      expect(created).not.toBe('');
      expect(lastLogin).not.toBe('');
      expect(created).toContain('2006');
      expect(lastLogin).toContain('2006');

      // The time component distinguishes the requested shape from the pipe's short-date default.
      expect(created).toMatch(/\d{1,2}:\d{2}/);
      expect(lastLogin).toMatch(/\d{1,2}:\d{2}/);
    });

    it('renders a year-9999 instant like any other, because it is a real perpetual expiry', () => {
      // Only the LOW sentinel is erased. A far-future date is meaningful data and is shown.
      const [created] = dateCells('9999-12-31T00:00:00Z', null);

      expect(created).toContain('9999');
    });
  });

  // ===================================================================================================
  // §3.10 — THE THREE ROW COMMANDS
  // ===================================================================================================

  describe('the row commands', () => {
    it('offers exactly three commands per row, in the legacy order', () => {
      // THREE SEPARATE COLUMNS rather than one column of three controls, because that is what `users.ascx`
      // L32, L33 and L34 declared: three distinct `dnn:imagecommandcolumn` elements.
      arrive();

      expect(queryAll(ACTION_CELL_SELECTOR)).toHaveSize(3);
      expect(textOf(ROW_ACTION_SELECTOR)).toEqual([
        EDIT_COMMAND_LABEL,
        DELETE_COMMAND_LABEL,
        MANAGE_ROLES_COMMAND_LABEL,
      ]);
    });

    it('links edit to the account editor keyed by the account', () => {
      arrive(pageOf([userRow(42)]));

      const href: string | null = rowAction(EDIT_COMMAND_LABEL).getAttribute('href');

      expect(href).toBe('/users/42');
      expect(href === null ? '' : href).not.toContain('?');
    });

    it('CARRIES THE ACCOUNT to the role listing, as a query parameter and not a path segment', () => {
      arrive(pageOf([userRow(42)]));

      expect(rowAction(MANAGE_ROLES_COMMAND_LABEL).getAttribute('href')).toBe('/roles?userId=42');
    });

    it('carries an account identifier of ZERO, which a truthiness test would have dropped', () => {
      // The sentinel discipline at the one place it could silently remove context for exactly one row.
      arrive(pageOf([userRow(0)]));

      expect(rowAction(MANAGE_ROLES_COMMAND_LABEL).getAttribute('href')).toBe('/roles?userId=0');
    });

    it('renders edit and manage-roles as links and delete as a button', () => {
      // Removing an account changes state and no address, so it is a button; the other two are addresses.
      arrive();

      expect(rowAction(EDIT_COMMAND_LABEL).tagName).toBe('A');
      expect(rowAction(MANAGE_ROLES_COMMAND_LABEL).tagName).toBe('A');
      expect(rowAction(DELETE_COMMAND_LABEL).tagName).toBe('BUTTON');
    });

    it('names every command with the account it acts on', () => {
      arrive(pageOf([userRow(42, { username: 'asmith' })]));

      expect(rowAction(EDIT_COMMAND_LABEL).getAttribute('aria-label')).toBe(
        `${EDIT_COMMAND_LABEL} asmith`,
      );
      expect(rowAction(DELETE_COMMAND_LABEL).getAttribute('aria-label')).toBe(
        `${DELETE_COMMAND_LABEL} asmith`,
      );
      expect(rowAction(MANAGE_ROLES_COMMAND_LABEL).getAttribute('aria-label')).toBe(
        `${MANAGE_ROLES_COMMAND_LABEL} asmith`,
      );
    });

    it('withdraws the mutating affordances from a caller that does not administer the tenant', () => {
      // REMOVAL, NOT CONCEALMENT, and an AFFORDANCE ONLY — the server re-authorises every request and
      // answers 403, and its verdict is the only authority. What withholding buys is that the operator is
      // not offered two screens the router will refuse and a command the API will decline.
      administersPortal.set(false);
      arrive();

      expect(textOf(ROW_ACTION_SELECTOR)).toEqual([MANAGE_ROLES_COMMAND_LABEL]);
      expect(textOf('a.user-list__page-action')).toEqual([]);
    });

    it('names the superseded permission key nowhere in the rendered screen', () => {
      arrive();

      expect(host().innerHTML).not.toContain(EDIT_PERMISSION);
      expect(host().innerHTML).not.toContain('hasPermission');
    });

    it('offers the mutating affordances to an administrator holding NO persisted key', () => {
      // ⚠ THE OTHER HALF OF THE VOCABULARY SEPARATION, AND THE HALF THAT WAS A LOCKOUT. The client's key
      // list is derived from GRANT ROWS ALONE, so a tenant administrator who has never been named in one
      // holds no keys whatsoever — measured on the seeded baseline, the administrator account holds the
      // designated administrator role and ZERO portal-level permission keys.
      administersPortal.set(true);
      arrive();

      expect(textOf('a.user-list__page-action')).toContain(ADD_USER_LABEL);
      expect(textOf(ROW_ACTION_SELECTOR)).toContain(EDIT_COMMAND_LABEL);
      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);
    });

    it('follows a change of administration without being recreated', () => {
      arrive();

      expect(textOf('a.user-list__page-action')).toContain(ADD_USER_LABEL);

      administersPortal.set(false);
      fixture.detectChanges();

      expect(textOf('a.user-list__page-action')).not.toContain(ADD_USER_LABEL);
      expect(textOf(ROW_ACTION_SELECTOR)).toEqual([MANAGE_ROLES_COMMAND_LABEL]);
    });

    it('disables the delete command while a removal is already in flight', () => {
      arrive(pageOf([userRow(7)]));

      const removal = confirmRemoval(7);

      expect(rowAction(DELETE_COMMAND_LABEL).getAttribute('disabled')).not.toBeNull();

      removal.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      answerListing(pageOf([userRow(8)]));
      settleOutcome();

      expect(rowAction(DELETE_COMMAND_LABEL).getAttribute('disabled')).toBeNull();
    });
  });

  // The first protects the tenant's DESIGNATED ADMINISTRATOR — removing it would leave
  // `Portals.AdministratorId` naming an account that no longer exists.

  describe('the two protected accounts', () => {
    it('reads the tenant record NOT AT ALL, because the row already carries the verdict', () => {
      arrive();

      expect(loadCurrentPortalContext).not.toHaveBeenCalled();
    });

    it('asks for nothing extra while the caller\u2019s identity is unresolved either', () => {
      // The identity is fetched, so it is null for a window after the screen mounts. Nothing about the
      // removal verdict depends on the tenant record any more, so there is nothing to ask for in that
      // window — which is a stronger property than asking correctly.
      callerIdentity.set(null);
      arrive();

      expect(loadCurrentPortalContext).not.toHaveBeenCalled();
    });

    it('withholds removal from the tenant\u2019s designated administrator', () => {
      // ⚠ U-M5 — DRIVEN THROUGH THE ROW'S OWN VERDICT RATHER THAN THROUGH A SEPARATELY-READ TENANT RECORD.
      // The server withholds `canDelete` for the account named by `Portals.AdministratorId`, so that flag
      // IS the designation as far as this screen is concerned, and it arrives with the row.
      arrive(pageOf([userRow(7, { canDelete: false })]));

      expect(textOf(ROW_ACTION_SELECTOR)).toEqual([EDIT_COMMAND_LABEL, MANAGE_ROLES_COMMAND_LABEL]);
      expect(textOf(ROW_ACTION_SELECTOR)).not.toContain(DELETE_COMMAND_LABEL);
    });

    it('keeps removal on every OTHER account in the same page', () => {
      // The guard is per row, so protecting one account must not disarm the column. The verdict is now
      // per row on the wire as well, which is what makes that guarantee structural rather than incidental.
      arrive(
        pageOf([
          userRow(7, { canDelete: false }),
          userRow(8, { username: 'asmith', canDelete: true }),
        ]),
      );

      const names: readonly (string | null)[] = queryAll<HTMLElement>(ROW_ACTION_SELECTOR).map(
        (action) => action.getAttribute('aria-label'),
      );

      expect(names).not.toContain(`${DELETE_COMMAND_LABEL} jbloggs`);
      expect(names).toContain(`${DELETE_COMMAND_LABEL} asmith`);
    });

    it('protects a designated administrator whose key is ZERO, which is not an absence', () => {
      arrive(pageOf([userRow(0, { canDelete: false })]));

      expect(textOf(ROW_ACTION_SELECTOR)).not.toContain(DELETE_COMMAND_LABEL);
    });

    it('withholds removal from a signed-in HOST account acting on its own row', () => {
      callerIdentity.set(callerAccount({ userId: 7, isSuperUser: true }));
      arrive(pageOf([userRow(7, { isSuperUser: true })]));

      expect(textOf(ROW_ACTION_SELECTOR)).not.toContain(DELETE_COMMAND_LABEL);
    });

    it('offers removal to a host account acting on ANOTHER host account', () => {
      // Only the caller's OWN row is protected by that clause. A host account removing a different
      // host account was never guarded, and inventing the guard would remove a legacy capability.
      callerIdentity.set(callerAccount({ userId: 99, isSuperUser: true }));
      arrive(pageOf([userRow(7, { isSuperUser: true })]));

      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);
    });

    it('offers removal to an ORDINARY account acting on its own row', () => {
      callerIdentity.set(callerAccount({ userId: 7, isSuperUser: false }));
      arrive(pageOf([userRow(7, { isSuperUser: false })]));

      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);
    });

    it('offers removal on a row the server says may be removed', () => {
      // ⚠ U-M5 — THERE IS NO LONGER AN "UNRESOLVED" WINDOW TO BE FAIL-SAFE ABOUT, WHICH IS THE POINT. This
      // case used to assert the fail-safe direction for the interval between the screen painting and the
      // tenant record arriving: the designation was null, the client clause protected nobody, and the
      // command was offered on every row until the read settled.
      arrive(pageOf([userRow(7, { canDelete: true })]));

      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);
    });

    it('re-arms the guard from a fresh page, without remounting', async () => {
      // The verdict travels with the page, so a re-read is what changes it. Asserted without remounting
      // because the command column is assembled once and its per-row branch has to keep answering.
      arrive(pageOf([userRow(7, { canDelete: true })]));

      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);

      await pressLetter(ALL_FILTER_LABEL);
      answerListing(pageOf([userRow(7, { canDelete: false })]));

      expect(textOf(ROW_ACTION_SELECTOR)).not.toContain(DELETE_COMMAND_LABEL);
    });

    it('withholds removal from a protected row even from an administrator', () => {
      // The two protections are about the RECORD, not about the caller's authority: an administrator
      // is exactly who reaches this screen, and the legacy guard applied to them too.
      administersPortal.set(true);
      arrive(pageOf([userRow(7, { canDelete: false })]));

      expect(textOf(ROW_ACTION_SELECTOR)).not.toContain(DELETE_COMMAND_LABEL);
      expect(textOf(ROW_ACTION_SELECTOR)).toContain(EDIT_COMMAND_LABEL);
    });
  });

  // ===================================================================================================
  // §3.11 — COLUMNS, WORDING AND THE DROPPED COLUMN
  // ===================================================================================================

  describe('the column set', () => {
    it('paints the ten data headings in the legacy order, using the RESOURCE wording', () => {
      arrive();

      const headings: readonly string[] = textOf(HEADER_SELECTOR);

      expect(headings).toEqual([...COMMAND_HEADINGS, ...DATA_HEADINGS]);
    });

    it('spells the five contradicted headings the way the resource file does', () => {
      arrive();

      const headings: readonly string[] = textOf(HEADER_SELECTOR);

      expect(headings).toContain('First Name');
      expect(headings).toContain('Last Name');
      expect(headings).toContain('Name');
      expect(headings).toContain('Created Date');
      expect(headings).toContain('Last Login');

      // And none of the markup spellings survives.
      expect(headings).not.toContain('FirstName');
      expect(headings).not.toContain('LastName');
      expect(headings).not.toContain('DisplayName');
      expect(headings).not.toContain('CreatedDate');
      expect(headings).not.toContain('LastLogin');
    });

    it('keeps every heading a column-scoped header cell', () => {
      arrive();

      for (const header of queryAll<HTMLTableCellElement>(HEADER_SELECTOR)) {
        expect(header.getAttribute('scope')).toBe('col');
      }
    });

    it('hides the three command headings visually while keeping them announced', () => {
      // The legacy grid supplied NO heading text for any of its three command columns, so painting one
      // would be an addition; keeping the label in the accessibility tree means a command cell is still
      // read out with its column name, which closes a real gap at no visual cost.
      arrive();

      const headers: readonly HTMLTableCellElement[] = queryAll<HTMLTableCellElement>(
        HEADER_SELECTOR,
      );

      for (let position = 0; position < COMMAND_HEADINGS.length; position += 1) {
        const header: HTMLTableCellElement | undefined = headers[position];

        if (header === undefined) {
          throw new Error(`Expected a header in position ${position}`);
        }

        expect(queryOrFail<Element>(header, 'span').classList).toContain(
          'data-table__label--hidden',
        );
      }
    });

    it('keeps the account-name column visible whatever the tenant policy says', () => {
      // ⚠ UNCONDITIONAL, AND DELIBERATELY NOT GATED. `Page_Init` L510-L511 made a column visible WITHOUT
      // consulting any setting when its heading was empty or lower-cased to `username`, and the settings
      // screen declares nine `Column_*` keys with no `Column_Username` among them.
      arrive(
        pageOf([userRow()]),
        membershipSettings({
          columnFirstName: false,
          columnLastName: false,
          columnDisplayName: false,
          columnAddress: false,
          columnTelephone: false,
          columnEmail: false,
          columnCreatedDate: false,
          columnLastLogin: false,
          columnAuthorized: false,
        }),
      );

      expect(textOf(HEADER_SELECTOR)).toEqual([...COMMAND_HEADINGS, USERNAME_HEADING]);
    });

    /**
     * ⚠ A SORT CONTROL CANNOT SIT ON A COLUMN THAT IS NOT PAINTED, and this was measured in the running
     * application rather than reasoned about. Against the live tenant, `GET /api/v1/users/settings`
     * answers 404, so `membershipSettings()` is null and `LEGACY_DEFAULT_COLUMN_VISIBILITY` applies -
     * which declares `firstName: false`, `lastName: false` and `columnEmail: false`.
     */
    it('offers a sort only on the columns this tenant actually paints', () => {
      arrive(
        pageOf([userRow()]),
        membershipSettings({
          columnFirstName: false,
          columnLastName: false,
          columnEmail: false,
        }),
      );

      const names: readonly string[] = queryAll<HTMLButtonElement>('button.data-table__sort').map(
        (control) => control.getAttribute('aria-label') ?? '',
      );

      expect(names).toEqual(['Sort by Username', 'Sort by Name']);

      const headings: readonly string[] = textOf(HEADER_SELECTOR);

      expect(headings).not.toContain(FIRST_NAME_HEADING);
      expect(headings).not.toContain(LAST_NAME_HEADING);
      expect(headings).not.toContain(EMAIL_HEADING);
    });

    it('hides one optional column when the tenant switches exactly that one off', () => {
      // Every flag is compared EXPLICITLY against true, because false is DATA on this contract rather than
      // an absence — the legacy absent-Boolean marker was itself `False`, so in the legacy model a
      // switched-off column and an unset one were indistinguishable, whereas here the wire value means what
      // it says.
      arrive(pageOf([userRow()]), membershipSettings({ columnTelephone: false }));

      const headings: readonly string[] = textOf(HEADER_SELECTOR);

      expect(headings).not.toContain(TELEPHONE_HEADING);
      expect(headings).toContain(ADDRESS_HEADING);
      expect(headings).toContain(EMAIL_HEADING);
    });

    it('does not reproduce the users-online column', () => {
      // `users.ascx` L35-L39 declared an unlabelled template column holding a single
      // `~/images/userOnline.gif` image whose visibility came from L702.
      arrive(pageOf([userRow(7, { isOnline: true })]));

      // Fourteen legacy columns resolve to thirteen: three commands plus ten data columns.
      expect(queryAll(HEADER_SELECTOR)).toHaveSize(13);
      expect(queryAll('img')).toHaveSize(0);
    });

    it('applies no zebra striping', () => {
      arrive(pageOf([userRow(1), userRow(2), userRow(3)], 3));

      const classLists: readonly string[] = rows().map((row) =>
        Array.from(row.classList).sort().join(' '),
      );

      expect(classLists).toHaveSize(3);
      expect(new Set(classLists).size).toBe(1);
    });

    it('renders the address exactly as the server composed it', () => {
      // ⚠ DIVERGENCE FROM THIS FILE'S BRIEF, RESOLVED IN FAVOUR OF THE CODE, AND REPORTED. The brief asks
      // for a client-side join of six trimmed profile parts.
      const composed = 'Flat 2, 14 High Street, Bristol, Avon, United Kingdom, BS1 4TR';

      arrive(pageOf([userRow(7, { address: composed })]));

      expect(textIn(cellUnder(ADDRESS_HEADING))).toBe(composed);
    });

    it('renders a partly composed address without a stray separator', () => {
      // `Globals.FormatAddress` appended each NON-BLANK part behind a comma and space and then stripped the
      // leading separator, so a partial address never carried a dangling comma. The client must not add one
      // either — no padding, no placeholder for a missing part.
      const partial = 'Bristol, United Kingdom';

      arrive(pageOf([userRow(7, { address: partial })]));

      const painted: string = textIn(cellUnder(ADDRESS_HEADING));

      expect(painted).toBe(partial);
      expect(painted.startsWith(',')).toBeFalse();
      expect(painted.endsWith(',')).toBeFalse();
      expect(painted).not.toContain(', ,');
    });

    it('renders the address as text, never as a link', () => {
      arrive();

      expect(cellUnder(ADDRESS_HEADING).querySelector('a')).toBeNull();
    });

    it('renders the electronic-mail address as a mailto link through a property binding', () => {
      // This is `HtmlUtils.FormatEmail` expressed as DATA rather than as markup. The legacy helper
      // concatenated an anchor around the stored value and returned it as a string a label control then
      // emitted, which is a script-injection vector for any address containing markup.
      arrive(pageOf([userRow(7, { email: 'jbloggs@example.test' })]));

      const link = queryOrFail<HTMLAnchorElement>(cellUnder(EMAIL_HEADING), EMAIL_LINK_SELECTOR);

      expect(link.getAttribute('href')).toBe('mailto:jbloggs@example.test');
      expect(textIn(link)).toBe('jbloggs@example.test');
    });

    it('does not link a stored value that carries no mailbox separator', () => {
      arrive(pageOf([userRow(7, { email: 'not-an-address' })]));

      const cell = cellUnder(EMAIL_HEADING);

      expect(cell.querySelector(EMAIL_LINK_SELECTOR)).toBeNull();
      expect(textIn(cell)).toBe('not-an-address');
    });

    it('links nothing at all for a blank stored address', () => {
      // The legacy helper declined to link a blank or whitespace-only value, and rendered nothing for it.
      arrive(pageOf([userRow(7, { email: '   ' })]));

      const cell = cellUnder(EMAIL_HEADING);

      expect(cell.querySelector(EMAIL_LINK_SELECTOR)).toBeNull();
      expect(textIn(cell)).toBe('');
    });

    // `dbo.Users.Email` is `[nvarchar] (256) NOT NULL` with no format constraint, and the legacy
    // application applied none: `AddUser` stored whatever the caller supplied.

    describe('a hostile stored address is shown but never linked', () => {
      /**
       * Renders one stored address and returns its cell.
       *
       * @param email The stored value.
       * @returns The rendered electronic-mail cell.
       */
      function emailCellFor(email: string): HTMLElement {
        arrive(pageOf([userRow(7, { email })]));

        return cellUnder(EMAIL_HEADING);
      }

      /**
       * Asserts that a stored value is displayed verbatim with nothing to follow.
       *
       * @param email The stored value.
       * @param why What makes the value hostile, for the failure message.
       */
      function showsButDoesNotLink(email: string, why: string): void {
        const cell: HTMLElement = emailCellFor(email);

        expect(cell.querySelector(EMAIL_LINK_SELECTOR))
          .withContext(`nothing navigable for ${why}`)
          .toBeNull();
        expect(textIn(cell))
          .withContext(`and the stored value is still shown for ${why}`)
          .toBe(email);
        expect(cell.querySelector('a')).withContext('no anchor of any class either').toBeNull();
      }

      it('refuses a value carrying a mailto QUERY that would copy a third party', () => {
        // The headline case. `bcc` is honoured by every mail client, so this composed a message that
        // silently copied an address the operator never saw and never chose.
        showsButDoesNotLink(
          'grace@example.test?bcc=harvester@elsewhere.test',
          'a bcc field smuggled into the address',
        );
      });

      it('refuses a value carrying a subject and a body', () => {
        // The same mechanism, pre-filling the message rather than its recipients: the operator presses a
        // link that says one thing and their client opens a composed message saying another.
        showsButDoesNotLink(
          'grace@example.test?subject=Urgent&body=Send%20the%20token',
          'a pre-composed subject and body',
        );
      });

      it('refuses a value carrying a HEADER injected as an escape', () => {
        // `%0A` is a line feed once the client decodes the address, and a line feed in a mailto target
        // starts a new header. Percent-encoding is refused as a family for exactly this reason.
        showsButDoesNotLink(
          'grace@example.test%0Abcc:harvester@elsewhere.test',
          'a percent-encoded line break',
        );
      });

      it('refuses a value carrying a LITERAL carriage return and line feed', () => {
        // The un-encoded form of the same attack. Stored control characters are entirely possible in an
        // `nvarchar` column that no constraint governs.
        showsButDoesNotLink(
          'grace@example.test\r\nbcc:harvester@elsewhere.test',
          'a literal header break',
        );
      });

      // ⚠ ONE CASE PER VALUE, GENERATED, AND THE SHAPE IS FORCED BY THE HARNESS. Each mount reads the
      // membership settings, the profile definitions and the listing, so a case cannot render a second
      // stored value without a second mount - and the end-of-test verification would report the first
      // mount's requests as outstanding.
      const REFUSED_VALUES: readonly (readonly [string, string])[] = [
        // Neither whitespace nor punctuation, so the two rules above do not catch them: refused by code
        // point, because a stored control character is entirely possible in a column no constraint governs.
        ['grace\u0000@example.test', 'an embedded null'],
        ['grace@example.test\u007f', 'an embedded delete'],
        // A comma and a semicolon are both recipient separators in a mailto address, and both shapes
        // really occur in legacy address columns because operators typed them.
        ['grace@example.test,harvester@elsewhere.test', 'a comma-separated pair of mailboxes'],
        ['grace@example.test;harvester@elsewhere.test', 'a semicolon-separated pair of mailboxes'],
        // Two at-signs are not a mailbox, and the previous rule - "contains an at-sign" - admitted them.
        ['grace@@example.test', 'a doubled separator'],
        ['grace@example@elsewhere.test', 'two separators'],
        // Nothing on one side of the separator.
        ['@example.test', 'no local part'],
        ['grace@', 'no domain'],
        // Three shapes a lenient reading would admit and a mail client would read differently from the
        // characters on the screen.
        ['"Grace Hopper" <grace@example.test>', 'a display-name form'],
        ['grace@[192.168.0.1]', 'a domain literal'],
        ['javascript:alert(1)@example.test', 'a scheme-bearing value'],
      ];

      for (const [email, why] of REFUSED_VALUES) {
        it(`refuses a stored value carrying ${why}`, () => {
          showsButDoesNotLink(email, why);
        });
      }

      it('refuses a value carrying markup, and parses none of it', () => {
        const hostile = '<img src=x onerror="window.__dnnMailSentinel = true">@example.test';
        const cell: HTMLElement = emailCellFor(hostile);

        expect(cell.querySelector(EMAIL_LINK_SELECTOR)).toBeNull();
        expect(cell.querySelector('img'))
          .withContext('interpolation escapes it; nothing is parsed as an element')
          .toBeNull();
        expect(textIn(cell)).toBe(hostile);
        expect((window as unknown as Record<string, unknown>)['__dnnMailSentinel']).toBeUndefined();
      });

      it('refuses a value padded with whitespace, because the padding is part of the target', () => {
        const cell: HTMLElement = emailCellFor(' grace@example.test ');

        expect(cell.querySelector(EMAIL_LINK_SELECTOR))
          .withContext('nothing navigable, because the padding would enter the target')
          .toBeNull();
        expect(cell.textContent ?? '')
          .withContext('and the padding survives into the document, untrimmed')
          .toContain(' grace@example.test ');
        expect((cell.textContent ?? '').trim()).toBe('grace@example.test');
      });

      // ⚠ THE COUNTERPART SET, AND IT IS WHAT STOPS THE GATE FROM BEING A REGRESSION. A rule strict enough
      // to refuse the values above must still admit the addresses this column actually holds: dots,
      // hyphens, plus-addressing and underscores in the local part, and a multi-label domain.
      const ADMITTED_VALUES: readonly string[] = [
        'grace@example.test',
        'grace.hopper@example.test',
        'grace-hopper@sub.example.co.uk',
        'grace+admin@example.test',
        'grace_hopper@example.test',
        'GRACE@EXAMPLE.TEST',
      ];

      for (const address of ADMITTED_VALUES) {
        it(`still links the ordinary address ${address}`, () => {
          const cell: HTMLElement = emailCellFor(address);
          const link = queryOrFail<HTMLAnchorElement>(cell, EMAIL_LINK_SELECTOR);

          // The target is the address itself: no query, no fragment, no second recipient, and nothing
          // percent-escaped, because the gate admits only characters that need no escaping.
          expect(link.getAttribute('href'))
            .withContext('the target is the address and nothing else')
            .toBe(`mailto:${address}`);
          expect(textIn(link)).toBe(address);

          const target: string = (link.getAttribute('href') ?? '').slice('mailto:'.length);

          expect(target).withContext('no query').not.toContain('?');
          expect(target).withContext('no fragment').not.toContain('#');
          expect(target).withContext('no second recipient').not.toContain(',');
          expect(target.split('@').length).withContext('exactly one mailbox').toBe(2);
        });
      }
    });

    it('never renders the telephone number as a mailto link, even one containing an at-sign', () => {
      arrive(pageOf([userRow(7, { telephone: '0117 496 0000 x@204' })]));

      const cell = cellUnder(TELEPHONE_HEADING);

      expect(cell.querySelector('a')).toBeNull();
      expect(textIn(cell)).toBe('0117 496 0000 x@204');
    });

    it('renders the authorisation flag as an announced word', () => {
      arrive(pageOf([userRow(7, { isApproved: true })]));

      expect(textIn(cellUnder(AUTHORIZED_HEADING))).toBe(AFFIRMATIVE_TEXT);
    });

    it('renders an unauthorised account as "No" rather than as an empty cell', () => {
      // ⚠ FALSE IS DATA, NOT "UNSET". The legacy absent-Boolean marker was itself `False`, so a truthiness
      // test would render an unauthorised account as a blank cell and an operator could not tell "not
      // authorised" from "not known".
      arrive(pageOf([userRow(7, { isApproved: false })]));

      const cell = cellUnder(AUTHORIZED_HEADING);

      // ⚠ THE VISIBLE WORD IS STILL EXACTLY THE LEGACY WORD, AND THAT IS THE HALF OF THIS FACT THAT MUST NOT
      // MOVE — QA-19. `users.ascx` L74-L79 bound the `Authorized` column through a yes/no formatter, so the
      // painted text is `No` and nothing else. What is ADDED is the state treatment around it and a sentence
      // beside it, because this column distinguished an account that can be used from one that cannot by a
      // single character in identical colour, weight and slant, while the three listings beside it had each
      // grown a deliberate non-colour vocabulary for exactly this shape of fact.
      const state = queryOrFail<HTMLElement>(cell, 'span.user-list__row-state');
      expect(textIn(state)).toBe(NEGATIVE_TEXT);

      // The value stays atomic so it can never be broken across lines, and the sentence is exposed only to
      // assistive technology.
      expect(state.hasAttribute('data-atomic-value')).toBeTrue();
      expect(textIn(queryOrFail<Element>(cell, 'span.user-list__row-mark-description'))).toBe(
        'not authorised, cannot sign in',
      );
      expect(textIn(cell)).not.toBe('');
    });

    // The counterpart, and it is the reason the treatment is applied to the NEGATIVE value only: an
    // authorised account is the ordinary state and must read as ordinary text, with no state span and no
    // sentence beside it.
    it('leaves an authorised account as plain text, with no state treatment', () => {
      arrive(pageOf([userRow(7, { isApproved: true })]));

      const cell = cellUnder(AUTHORIZED_HEADING);

      expect(textIn(cell)).toBe(AFFIRMATIVE_TEXT);
      expect(cell.querySelector('span.user-list__row-state')).toBeNull();
      expect(cell.querySelector('span.user-list__row-mark-description')).toBeNull();
    });

    // ⚠ EXACTLY ONE COLUMN TRACK IS LEFT FLEXIBLE, AND THAT IS A REQUIREMENT RATHER THAN AN OMISSION — QA-09.
    //
    // Under `table-layout: fixed` the percentage tracks resolve against the table width and whatever is LEFT
    // OVER goes to the columns that declared something else. With every column weighted, that leftover went to
    // the command columns: each of the three asked for 3.25rem and painted 119.797px, wider than the account name beside them. One unweighted column absorbs the slack instead, so every
    // other track resolves to exactly the share it declares.
    it('leaves exactly one column track flexible so the declared tracks resolve as written', () => {
      arrive();

      const tracks = queryAll<HTMLTableColElement>('colgroup col');

      expect(tracks.length).withContext('one track per rendered column').toBeGreaterThan(0);
      expect(tracks.length).toBe(queryAll<Element>('thead th').length);

      const flexible: readonly number[] = tracks
        .map((track, index) => ({ index, declared: track.style.inlineSize }))
        .filter((entry) => entry.declared === '')
        .map((entry) => entry.index);

      expect(flexible.length).withContext('one and only one flexible track').toBe(1);

      // The command tracks declare their own token rather than inheriting the slack.
      for (let index = 0; index < 3; index += 1) {
        expect(tracks[index]?.style.inlineSize).toContain('--table-command-column-inline-size');
      }

      // Every remaining track declares a percentage, so nothing else can quietly become flexible.
      tracks.forEach((track, index) => {
        if (index < 3 || flexible.includes(index)) {
          return;
        }

        expect(track.style.inlineSize).withContext(`track ${index}`).toMatch(/%$/);
      });
    });

    // ⚠ A NAME IS ONE TOKEN, AND THE GRID USED TO SPLIT IT. The shared stylesheet lets any cell break inside
    // a word so a narrow column cannot overflow, which is right for prose and wrong for a value a person
    // reads as a single thing. Measured at a 768 viewport before this guard: `qa_succes` + `s_probe`,
    // `setup_mem` + `ber`, `setup_admi` + `n`, `qa_longna` + `me` in the sign-in column, and the forename
    // `Lawrence` as `Lawrenc` + `e` - one orphaned letter on a line of its own. Declaring these columns
    // atomic keeps each value on one line and ellipsises what will not fit, so what shows is a recognisable
    // prefix rather than two fragments that read as corruption.
    it('keeps the identifier columns whole instead of breaking them mid-word', () => {
      arrive();

      const headers: readonly Element[] = queryAll<Element>('thead th');
      const tracks: readonly HTMLTableColElement[] = queryAll<HTMLTableColElement>('colgroup col');

      // ⚠ FOUND BY EXACT HEADING TEXT, WHICH IS NEITHER A SUBSTRING SEARCH NOR A FIXED POSITION, and both of
      // those have already failed here. A SUBSTRING search for the display-name column's heading — the bare
      // word "Name" — matches "Username", "First Name" and "Last Name" too, and silently asserted the wrong
      // track; that is how the first draft of this case failed against a correct implementation. A fixed
      // POSITION then replaced it, and the shared grid has since begun hoisting the row-header column to the
      // front while its scroll region clips, so an index is only stable while a width measurement comes out
      // one particular way. An exact match is unambiguous for all four of these headings and survives both.
      const indexOf = (heading: string): number => {
        const found = headers.findIndex((cell) => textIn(cell) === heading);

        if (found < 0) {
          throw new Error(
            `no heading reads exactly "${heading}"; rendered: ${headers
              .map((cell) => textIn(cell))
              .join(', ')}`,
          );
        }

        return found;
      };
      const atomicAt = (index: number): boolean => headers[index]?.getAttribute('data-atomic') === 'true';
      const headingAt = (index: number): string => textIn(headers[index] as Element);

      expect(atomicAt(indexOf(USERNAME_HEADING)))
        .withContext('a sign-in name is one token')
        .toBeTrue();
      expect(atomicAt(indexOf(FIRST_NAME_HEADING)))
        .withContext('a given name is one token')
        .toBeTrue();
      expect(atomicAt(indexOf(LAST_NAME_HEADING)))
        .withContext('a family name is one token')
        .toBeTrue();

      // ⚠ THE COUNTERPART, AND THE REASON THIS IS NOT A BLANKET RULE. Found as the one track that declares no
      // width - the column absorbing the slack - so this stays true if the set is ever reordered. It is the
      // widest column on the grid and holds a phrase rather than an identifier, so wrapping is correct there
      // and ellipsising it would hide text that fits perfectly well on a second line.
      const flexible: number = tracks.findIndex((track) => track.style.inlineSize === '');

      expect(headingAt(flexible)).toBe(DISPLAY_NAME_HEADING);
      expect(atomicAt(flexible))
        .withContext('the flexible column holds a phrase and should wrap')
        .toBeFalse();
    });

    // ⚠ THE MEASURED WIDTH REQUIREMENTS, WRITTEN DOWN SO A RESCALE CANNOT QUIETLY UNDO THEM AGAIN — QA-4c.
    //
    // This is not a style preference being frozen. Four of this grid's atomic tracks were narrower than their
    // own content, and one of the four had been widened once already and then narrowed straight back when every
    // weight was scaled by a single ratio — a ratio cannot know what a track holds. The numbers below are the
    // rendered measurements: each column's content width plus the 8px of inline padding a cell contributes.
    //
    //     Created Date   135.94   a full timestamp, "8/14/2026 4:56:09 PM"
    //     Last Login     135.94   the same shape through the same pipe
    //     Telephone       81.91   the HEADING, which is wider than any number it holds
    //     Username       108.10   an ordinary sign-in name; the 100-character fixture is deliberately not sized for
    //
    // Judged against the table at 1198px, which is what a 1440 viewport resolves to on this screen, because
    // that is the width QA measured the losses at.
    it('gives every atomic track at least the width its own content needs', () => {
      arrive();

      const headers: readonly Element[] = queryAll<Element>('thead th');
      const tracks: readonly HTMLTableColElement[] = queryAll<HTMLTableColElement>('colgroup col');
      const tableWidth = 1198;
      const required: Readonly<Record<string, number>> = {
        [CREATED_DATE_HEADING]: 135.94,
        // The same value shape through the same pipe, so the same requirement. It is hidden by default on a
        // real tenant, which is the only reason nobody reported it; the policy this fixture arrives with shows
        // every column, so it is measured here.
        [LAST_LOGIN_HEADING]: 135.94,
        [TELEPHONE_HEADING]: 81.91,
        [USERNAME_HEADING]: 108.1,
      };

      for (const [heading, needs] of Object.entries(required)) {
        const index = headers.findIndex((cell) => textIn(cell) === heading);

        expect(index).withContext(`"${heading}" is rendered`).toBeGreaterThanOrEqual(0);

        const declared = tracks[index]?.style.inlineSize ?? '';
        const weight = Number.parseFloat(declared);

        expect(declared).withContext(`"${heading}" declares a percentage`).toMatch(/%$/);
        expect((weight / 100) * tableWidth)
          .withContext(`"${heading}" needs ${needs}px and declares ${declared}`)
          .toBeGreaterThanOrEqual(needs);
      }
    });

    it('keeps the weights inside the floor that protects the flexible column', () => {
      // The other half of the arithmetic, and the half that bites silently: under a fixed layout a starved
      // slack column has not overflowed anything, so no measurement reports it. Measured before the floor was
      // derived, the display name resolved to 0.0625px, its characters wrapped one per line and rows grew to
      // 398-414px tall. The table is floored at 60rem (960px) and the three command tracks take 156px, so the
      // weights must leave the slack column a readable measure at that width.
      arrive();

      const tracks: readonly HTMLTableColElement[] = queryAll<HTMLTableColElement>('colgroup col');
      const weights: readonly number[] = tracks
        .map((track) => track.style.inlineSize)
        .filter((declared) => declared.endsWith('%'))
        .map((declared) => Number.parseFloat(declared));
      const commandTracks = 3;
      const slackAtTheFloor =
        960 * (1 - weights.reduce((total, weight) => total + weight, 0) / 100) -
        commandTracks * 52;

      expect(slackAtTheFloor)
        .withContext('the display name stays readable with every column visible at the narrowest width')
        .toBeGreaterThan(90);
    });

    it('names the table for a screen reader without painting a heading', () => {
      // The caption carries the screen's own title and is visually hidden, so no heading is added on screen.
      arrive();

      const caption = queryOrFail<Element>(host(), 'caption.data-table__caption');

      expect(textIn(caption)).toBe(PAGE_TITLE);
      expect(caption.hasAttribute('data-visually-hidden')).toBeTrue();
    });
  });

  // ===================================================================================================
  // §3.12 — WAITING, EMPTINESS AND FAILURE
  // ===================================================================================================

  // ===================================================================================================
  // §12 — THE OPENING VIEW THE TENANT CONFIGURED
  // ===================================================================================================

  describe('the opening view the tenant configured', () => {
    it('opens on the first letter of the alphabet strip when the tenant chose that view', () => {
      create();
      answerSettings(membershipSettings({ displayMode: 1 }));
      answerDefinitions();

      // ⚠ EITHER TRANSPORT, READ THROUGH THE SHARED ACCESSOR. A first-letter view is an account-name
      // filter, and an account name identifies a person, so it travels in a request BODY rather than in a
      // request target — see {@link USERS_SEARCH_URL}.
      const listing = expectListRead('the listing read');

      expect(paramOf(listing, 'userName'))
        .withContext('the letter is A, and the axis is the account name')
        .toBe('A');

      listing.flush(pageOf([userRow()]));
      fixture.detectChanges();

      expect(query('.user-list__notice'))
        .withContext('a query WAS issued, so no notice is shown')
        .toBeNull();
    });

    it('issues no query and explains why when the tenant chose the no-query view', () => {
      create();
      answerSettings(membershipSettings({ displayMode: 2 }));
      answerDefinitions();

      httpMock.expectNone(USERS_URL);
      fixture.detectChanges();

      const notice = queryOrFail<HTMLElement>(host(), '.user-list__notice');

      expect(textIn(notice)).toContain('No accounts have been requested yet');
      expect(notice.getAttribute('aria-live'))
        .withContext('a reader arriving with assistive technology is told why the grid is bare')
        .toBe('polite');
      expect(notice.getAttribute('role')).toBe('status');

      // The notice names the two affordances that resolve it, using the wording those controls carry.
      expect(textIn(notice)).toContain('letter');
      expect(textIn(notice)).toContain(ALL_FILTER_LABEL);

      expect(rows()).toHaveSize(0);
      expect(query('app-loading-spinner'))
        .withContext('nothing may spin for a request that will never be made')
        .toBeNull();
    });

    it('withdraws the notice the moment the operator asks for something', async () => {
      create();
      answerSettings(membershipSettings({ displayMode: 2 }));
      answerDefinitions();
      httpMock.expectNone(USERS_URL);
      fixture.detectChanges();

      expect(query('.user-list__notice')).not.toBeNull();

      // The unfiltered affordance the notice names. Pressed, it dispatches the listing the legacy's
      // L264 branch served.
      await pressLetter(ALL_FILTER_LABEL);

      const listing = expectRequest('GET', USERS_URL, 'the unfiltered listing');

      listing.flush(pageOf([userRow()]));
      fixture.detectChanges();

      expect(query('.user-list__notice'))
        .withContext('a query has been issued, so the notice no longer applies')
        .toBeNull();
      expect(rows()).toHaveSize(1);
    });
  });

  describe('the transient states', () => {
    it('shows a progress indicator while the listing is in flight', () => {
      create();
      answerSettings();
      answerDefinitions();

      // The listing is pending: not answered, not failed. Captured rather than expected twice.
      const listing = expectListRead('the listing read');

      const placeholder = queryOrFail<Element>(host(), PLACEHOLDER_SELECTOR);

      expect(placeholder.querySelector('app-loading-spinner')).not.toBeNull();
      expect(placeholder.querySelector('app-empty-state')).toBeNull();
      expect(rows()).toHaveSize(0);

      listing.flush(pageOf([userRow()]));
      fixture.detectChanges();

      expect(query('app-loading-spinner')).toBeNull();
      expect(rows()).toHaveSize(1);
    });

    it('lets waiting win over emptiness', async () => {
      arrive(pageOf([]));

      expect(query('app-empty-state')).not.toBeNull();

      await typeSearch('pending');

      const pending = expectListRead('the pending search');

      expect(query('app-loading-spinner')).not.toBeNull();
      expect(query('app-empty-state')).toBeNull();

      // The policy is not re-read for a search; only the listing is.
      httpMock.expectNone(MEMBERSHIP_SETTINGS_URL);

      pending.flush(pageOf([]));
      fixture.detectChanges();

      expect(query('app-empty-state')).not.toBeNull();
    });

    it('shows the empty state for a page that matched nothing', () => {
      arrive(pageOf([]));

      const placeholder = queryOrFail<Element>(host(), PLACEHOLDER_SELECTOR);

      expect(placeholder.querySelector('app-empty-state')).not.toBeNull();
      expect(textIn(placeholder)).toContain(EMPTY_STATE_MESSAGE);

      // The headings survive, so a reader can still see which columns the absent rows would have filled.
      expect(queryAll(HEADER_SELECTOR)).toHaveSize(13);

      // ⚠ THE ZERO-RESULT SURFACE OFFERS A WAY FORWARD, AND IT HAD NONE — QA-28. The shared grid projects an
      // action slot into its empty state and this screen left it unfilled, so a reader who narrowed to a
      // letter that matched nothing met a sentence and nothing else. Two affordances now sit inside the table:
      // list every account, and add one.
      const actions = queryAll<HTMLElement>('app-empty-state .user-list__filter-action');
      expect(actions.map((node) => textIn(node))).toEqual([
        SHOW_ALL_ACCOUNTS_LABEL,
        ADD_USER_LABEL,
      ]);
    });

    // The other reason this table is empty, and it needs the OTHER sentence: a screen that has issued no
    // query at all has not "matched nothing", and telling a reader it did is simply false.
    it('explains an empty table that no query has been issued for', () => {
      // The tenant view that issues nothing at all, which is the state the shared default sentence was
      // most wrong about: nothing had been read, so nothing could have "matched nothing".
      create();
      answerSettings(membershipSettings({ displayMode: 2 }));
      answerDefinitions();
      httpMock.expectNone(USERS_URL);
      fixture.detectChanges();

      const placeholder = queryOrFail<Element>(host(), PLACEHOLDER_SELECTOR);

      expect(textIn(placeholder)).toContain(NO_QUERY_STATE_MESSAGE);
      expect(queryAll<HTMLElement>('app-empty-state .user-list__filter-action')).toHaveSize(2);
    });

    it('spans the empty and waiting messages across every rendered column', () => {
      arrive(pageOf([]));

      expect(queryOrFail<Element>(host(), PLACEHOLDER_SELECTOR).getAttribute('colspan')).toBe(
        '13',
      );
    });

    it('renders a failed read as an RFC 7807 problem, at the error severity', () => {
      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush(
        problem('server_error', 500, 'The account store is unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      const banner = queryOrFail<Element>(host(), 'div.error-banner');

      expect(banner.getAttribute('data-severity')).toBe('danger');
      expect(textIn(queryOrFail<Element>(banner, 'p.error-banner__severity'))).toBe(
        ERROR_SEVERITY_LABEL,
      );
      expect(textIn(queryOrFail<Element>(banner, 'p.error-banner__message'))).toBe(
        'The account store is unavailable.',
      );
    });

    it('renders a refused read at WARNING severity rather than as an error', () => {
      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush(bareProblem('forbidden', 403), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();

      const banner = queryOrFail<Element>(host(), 'div.error-banner');

      expect(banner.getAttribute('data-severity')).toBe('warning');
      expect(textIn(queryOrFail<Element>(banner, 'p.error-banner__severity'))).toBe(
        WARNING_SEVERITY_LABEL,
      );
      expect(textIn(queryOrFail<Element>(banner, 'p.error-banner__message'))).toBe(
        FORBIDDEN_MESSAGE,
      );
    });

    it('renders the per-field messages of a validation failure, under the normalised client keys', () => {
      // ⚠ THE WIRE KEYS ARE .NET MODEL-STATE KEYS AND THE RENDERED KEYS ARE NOT THE SAME STRING, which is
      // the fact this case pins.
      const errors: Readonly<Record<string, readonly string[]>> = {
        UserName: ['The user name is not valid.'],
        PageIndex: ['The page index may not be negative.'],
      };

      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush(
        problem('validation_failed', 400, 'One or more fields are invalid.', errors),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      const fields: readonly string[] = textOf('dt.error-banner__field');
      const details: readonly string[] = textOf('dd.error-banner__detail');

      expect(fields).toEqual(['userName', 'pageIndex']);
      expect(details).toEqual([...errors['UserName'], ...errors['PageIndex']]);
    });

    it('surfaces a form-level message that belongs to no field', () => {
      // The empty-string key is what a model-state failure uses for a message about the request as a whole.
      // It has no first character to lower-case and no control to match, so it survives untouched and is
      // shown beside the summary rather than beside a field.
      const errors: Readonly<Record<string, readonly string[]>> = {
        '': ['The request could not be understood.'],
      };

      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush(
        problem('validation_failed', 400, 'One or more fields are invalid.', errors),
        { status: 400, statusText: 'Bad Request' },
      );
      fixture.detectChanges();

      expect(textIn(queryOrFail<Element>(host(), 'div.error-banner'))).toContain(
        'The request could not be understood.',
      );
      expect(textOf('dt.error-banner__field')).not.toContain('');
    });

    it('shows the server support reference beside a failed read', () => {
      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush(
        problem('server_error', 500, 'The account store is unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(textIn(queryOrFail<Element>(host(), 'p.error-banner__trace'))).toContain(TRACE_ID);
    });

    it('carries the failure surface in a live region so it is announced', () => {
      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush(
        problem('server_error', 500, 'The account store is unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      const region = queryOrFail<Element>(host(), 'div.error-banner-live');

      expect(region.getAttribute('role')).toBe('alert');
      expect(region.getAttribute('aria-live')).toBe('assertive');
    });

    it('renders no failure surface at all while nothing has failed', () => {
      arrive();

      expect(query('div.error-banner')).toBeNull();
      expect(query(RETRY_SELECTOR)).toBeNull();
    });

    it('offers the retry affordance beside a failed read, enabled once the read has settled', () => {
      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush(
        problem('server_error', 500, 'The account store is unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      expect(queryOrFail<HTMLButtonElement>(host(), RETRY_SELECTOR).disabled).toBeFalse();
    });

    it('disables the retry affordance while a read is still in flight beneath a recorded failure', () => {
      create();
      answerSettings();
      expectRequest('GET', PROFILE_DEFINITIONS_URL, 'the profile-declarations read').flush(
        problem('server_error', 500, 'The declarations store is unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      const inFlight = expectListRead('the listing read');

      expect(query('div.error-banner')).not.toBeNull();
      expect(queryOrFail<HTMLButtonElement>(host(), RETRY_SELECTOR).disabled).toBeTrue();

      inFlight.flush(pageOf([userRow()], 1, 0, 10));
      fixture.detectChanges();

      expect(queryOrFail<HTMLButtonElement>(host(), RETRY_SELECTOR).disabled).toBeFalse();
    });
  });
  // ===================================================================================================
  // §3.9 — REQUEST ORDERING, AXIS CONSISTENCY, WRITE IDENTITY AND THE SILENT FAILURE
  //
  // Four corrections, each of which produced a screen that was confidently wrong rather than broken.
  // ===================================================================================================

  describe('request ordering between the box and the strip', () => {
    /**
     * Types into the box WITHOUT submitting, so the debounced emission is left pending.
     *
     * @param term The text to type.
     */
    function beginTyping(term: string): void {
      const field = queryOrFail<HTMLInputElement>(host(), SEARCH_INPUT_SELECTOR);

      field.value = term;
      field.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    it('lets a letter win over a term still waiting in the box', fakeAsync(() => {
      // ⚠ THE ORDERING DEFECT, END TO END. Typing "bl" starts a delay inside the shared box; pressing "C" a
      // moment later dispatches a query for C; the delay then elapsed and emitted "bl", so the OLDER intent
      // silently replaced the NEWER one and the strip showed C selected over a listing of accounts
      // beginning with B. The screen now calls the pending emission off before it dispatches its own query.
      arrive();
      beginTyping('bl');

      // ⚠ NOT AWAITED, because `await` is illegal inside `fakeAsync`. The helper's synchronous

      // half - the click and the change detection - runs immediately, and the `tick` below flushes both

      // the navigation it started and the settle it is waiting on.

      void pressLetter('B');

      tick();

      const filtered = expectListRead('the letter-filtered read');

      expect(paramOf(filtered, USER_NAME_PARAM)).toBe('B');
      filtered.flush(pageOf([userRow(2)], 1, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();

      // Well past any debounce window. Nothing further may be issued: `httpMock.verify()` in the
      // shared teardown is what turns a late emission into a failure.
      tick(5000);
      fixture.detectChanges();
    }));

    it('shows in the box what the strip filtered on, without querying twice', fakeAsync(() => {
      arrive();
      beginTyping('bl');

      // ⚠ NOT AWAITED, because `await` is illegal inside `fakeAsync`. The helper's synchronous

      // half - the click and the change detection - runs immediately, and the `tick` below flushes both

      // the navigation it started and the settle it is waiting on.

      void pressLetter('B');

      tick();
      expectListRead('the letter-filtered read').flush(pageOf([userRow(2)], 1, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
      tick(5000);
      fixture.detectChanges();

      expect(queryOrFail<HTMLInputElement>(host(), SEARCH_INPUT_SELECTOR).value)
        .withContext('the box states what is actually in force')
        .toBe('B');
    }));

    it('empties the box for the unfiltered affordance, and issues one read', fakeAsync(() => {
      arrive();
      beginTyping('bl');

      // ⚠ NOT AWAITED, because `await` is illegal inside `fakeAsync`. The helper's synchronous

      // half - the click and the change detection - runs immediately, and the `tick` below flushes both

      // the navigation it started and the settle it is waiting on.

      void pressLetter(ALL_FILTER_LABEL);

      tick();

      const unfiltered = expectListRead('the unfiltered read');

      expect(unfiltered.request.method).toBe('GET');
      unfiltered.flush(pageOf([userRow()], 1, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
      tick(5000);
      fixture.detectChanges();

      expect(queryOrFail<HTMLInputElement>(host(), SEARCH_INPUT_SELECTOR).value)
        .withContext('no term is in force, so the box shows none')
        .toBe('');
    }));
  });

  describe('the chosen axis and what is still declared', () => {
    // =======================================================================
    // A COLD ARRIVAL, WITH THE DECLARATIONS STILL ON THE WIRE
    //
    // ⚠ THE RECONCILIATION USED TO JUDGE AN AXIS UNDECLARED BEFORE ANYTHING HAD BEEN DECLARED. An empty
    // declaration list means two opposite things - "this tenant declares none" and "nothing has been read
    // yet" - and a membership test cannot tell them apart. On the cold entry an operator reaches after
    // their session lapses, the axis is restored from the address, the read has not returned, and the axis
    // is reset to the account name while the address, the request and the rendered results all stay on the
    // property that was searched. The disagreement was silent AND destructive: the next press of Search
    // sent the SELECTOR's axis, so the search quietly became a name search and the axis parameter vanished
    // from the address.
    // =======================================================================

    it('keeps a restored profile-property axis while the declarations are still outstanding', async () => {
      await enterAt(`/users?${SEARCH_BY_QUERY_KEY}=${encodeURIComponent(SECOND_PROPERTY_NAME)}&${FILTER_QUERY_KEY}=A`);
      create();
      answerSettings();

      // The declarations read is deliberately LEFT OUTSTANDING. This IS the cold-entry state.
      const read: TestRequest = expectListRead('the restored read');

      expect(paramOf(read, PROFILE_PROPERTY_NAME_PARAM))
        .withContext('the request was always right; it was the selector that disagreed with it')
        .toBe(SECOND_PROPERTY_NAME);
      expect(paramOf(read, PROFILE_PROPERTY_VALUE_PARAM)).toBe('A');

      read.flush(pageOf([userRow()]));
      fixture.detectChanges();

      const selector = queryOrFail<HTMLSelectElement>(host(), `#${SEARCH_FIELD_CONTROL_ID}`);

      expect(selector.value)
        .withContext('the selector holds the restored axis rather than being reset to the default')
        .toBe(SECOND_PROPERTY_NAME);

      // And it survives the read arriving, which is the single revalidation the restored axis needs.
      answerDefinitions();

      expect(queryOrFail<HTMLSelectElement>(host(), `#${SEARCH_FIELD_CONTROL_ID}`).value)
        .withContext('a declared axis is confirmed, not replaced')
        .toBe(SECOND_PROPERTY_NAME);
    });

    it('sends the restored axis on the next search rather than silently switching to the name', async () => {
      // THE CONSEQUENCE THE DISAGREEMENT HAD. Pressing Search without touching anything must repeat the
      // search the operator is looking at.
      await enterAt(`/users?${SEARCH_BY_QUERY_KEY}=${encodeURIComponent(SECOND_PROPERTY_NAME)}&${FILTER_QUERY_KEY}=A`);
      create();
      answerSettings();
      expectListRead('the restored read').flush(pageOf([userRow()]));
      fixture.detectChanges();
      answerDefinitions();

      await typeSearch('Ar');

      const next: TestRequest = expectListRead('the search pressed after the restore');

      expect(paramOf(next, PROFILE_PROPERTY_NAME_PARAM))
        .withContext('still the property axis')
        .toBe(SECOND_PROPERTY_NAME);
      expect(paramOf(next, PROFILE_PROPERTY_VALUE_PARAM)).toBe('Ar');
      expect(carries(next, USER_NAME_PARAM))
        .withContext('and never the account name, which is what it silently became')
        .toBeFalse();
      expect(addressParams()[SEARCH_BY_QUERY_KEY])
        .withContext('the address keeps the axis it arrived with')
        .toBe(SECOND_PROPERTY_NAME);

      next.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('still resets an axis the declarations, once read, do not declare', async () => {
      // THE NEGATIVE CONTROL. Waiting must not become never: an axis that is genuinely undeclared - an
      // address kept from before the property was removed - is still corrected, just not before the
      // evidence arrives.
      await enterAt(`/users?${SEARCH_BY_QUERY_KEY}=Nonexistent%20Property&${FILTER_QUERY_KEY}=A`);
      create();
      answerSettings();
      expectListRead('the restored read').flush(pageOf([userRow()]));
      fixture.detectChanges();

      answerDefinitions();

      expect(queryOrFail<HTMLSelectElement>(host(), `#${SEARCH_FIELD_CONTROL_ID}`).value)
        .withContext('once the declarations are known, an undeclared axis IS reset')
        .toBe('Username');
    });

    it('falls back to the account name when the chosen property stops being declared', async () => {
      arrive();
      chooseAxis(ODD_PROPERTY_NAME);

      // The catalogue is re-read without that property, exactly as it would be after a removal on the
      // neighbouring screen.
      TestBed.inject(UserStore).refreshProfileDefinitions();
      expectRequest('GET', PROFILE_DEFINITIONS_URL, 'the re-read declarations').flush(
        envelope([profileDefinition(SECOND_PROPERTY_NAME, 12)]),
      );
      fixture.detectChanges();

      await typeSearch('Bris');

      const searched = expectListRead('the search after the property vanished');

      expect(carries(searched, PROFILE_PROPERTY_NAME_PARAM))
        .withContext('a deleted property must never be queried')
        .toBeFalse();
      expect(paramOf(searched, USER_NAME_PARAM))
        .withContext('the axis returns to the legacy default, which is the account name')
        .toBe('Bris');

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('leaves the rendered selector agreeing with what is queried', () => {
      arrive();
      chooseAxis(ODD_PROPERTY_NAME);

      TestBed.inject(UserStore).refreshProfileDefinitions();
      expectRequest('GET', PROFILE_DEFINITIONS_URL, 'the re-read declarations').flush(
        envelope([profileDefinition(SECOND_PROPERTY_NAME, 12)]),
      );
      fixture.detectChanges();

      const selector = queryOrFail<HTMLSelectElement>(host(), `#${SEARCH_FIELD_CONTROL_ID}`);
      const selected: HTMLOptionElement | undefined = Array.from(selector.options).find(
        (option) => option.selected,
      );

      expect(textIn(selected ?? selector))
        .withContext('what the operator reads is what the next search will use')
        .toBe('Username');
    });

    it('keeps a chosen property that is still declared', async () => {
      // The reconciliation must not be a reset on every catalogue read, or an operator's choice would
      // be discarded whenever anything re-read the declarations.
      arrive();
      chooseAxis(ODD_PROPERTY_NAME);

      TestBed.inject(UserStore).refreshProfileDefinitions();
      expectRequest('GET', PROFILE_DEFINITIONS_URL, 'the re-read declarations').flush(
        envelope(PROFILE_DEFINITIONS),
      );
      fixture.detectChanges();

      await typeSearch('Bris');

      const searched = expectListRead('the search on the retained axis');

      expect(paramOf(searched, PROFILE_PROPERTY_NAME_PARAM)).toBe(ODD_PROPERTY_NAME);
      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });
  });

  describe('a read that failed without a problem document', () => {
    it('shows the store\u2019s authored summary instead of nothing at all', () => {
      // ⚠ THE FAILURE THIS MAKES VISIBLE WAS COMPLETELY SILENT. The runtime decoders that check each
      // response against its published contract run inside the service's own mapping, DOWNSTREAM of the
      // interceptor's error handling — so a `200` whose body does not match its contract throws a plain
      // error with no document, no status and no support reference.
      create();
      answerSettings();
      answerDefinitions();

      expectListRead('the listing read').flush({ items: [userRow()] });
      fixture.detectChanges();

      const banner = queryOrFail<HTMLElement>(host(), '.error-banner');

      expect(textIn(banner))
        .withContext('the failure says something rather than rendering an empty box')
        .not.toBe('');

      // The live region is what announces it, so a reader is told as well as shown.
      expect(query('.error-banner-live')?.getAttribute('role')).toBe('alert');
    });

    it('leaves the search and paging affordances usable, so there is a way to retry', async () => {
      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush({ items: [userRow()] });
      fixture.detectChanges();

      await typeSearch('bl');

      const retried = expectListRead('the retry');

      expect(paramOf(retried, USER_NAME_PARAM)).toBe('bl');
      retried.flush(pageOf([userRow()]));
      fixture.detectChanges();

      expect(query('.error-banner'))
        .withContext('a successful read clears the failure')
        .toBeNull();
    });
  });

  describe('the persistent announcing region', () => {
    it('is mounted before anything has failed, with the retry offered only for a failure', () => {
      arrive(pageOf([userRow(7)]));

      expect(query('app-error-banner'))
        .withContext('the region is present on a healthy screen')
        .not.toBeNull();
      expect(query('app-error-banner [role="alert"]')?.getAttribute('aria-live'))
        .withContext('with its announcement semantics already declared')
        .toBe('assertive');
      expect(query('app-error-banner .error-banner'))
        .withContext('and nothing painted inside it')
        .toBeNull();
      expect(query('.user-list__failure-retry'))
        .withContext('while the recovery command is offered only for a failure that happened')
        .toBeNull();
    });
  });

  describe('whose write settled', () => {
    it('reports a removal refused AFTER an unrelated write settled first', () => {
      arrive(pageOf([userRow(7)]));

      const removal = confirmRemoval(7);

      // A sibling screen's write, dispatched at the shared store and settled while ours is open.
      TestBed.inject(UserStore).unlockUser(9);
      expectRequest('POST', `${userUrl(9)}/unlock`, 'the sibling write').flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      expectListRead('the sibling re-read').flush(pageOf([userRow(7)]));
      fixture.detectChanges();

      settleOutcome();

      expect(notifySpy)
        .withContext('another screen\u2019s write must not be reported as our removal')
        .not.toHaveBeenCalled();

      removal.flush(
        problem('user.last-administrator', 409, 'The last administrator cannot be removed.'),
        { status: 409, statusText: 'Conflict' },
      );
      settleOutcome();

      expect(notifySpy.calls.count())
        .withContext('and the refusal that follows IS reported')
        .toBe(1);
    });
  });

  // PROOF — THE ADDRESS CARRIES THE SEARCH, THE AXIS AND THE PAGE

  describe('the address', () => {
    it('writes a typed search and its default axis, stating the axis by omission', async () => {
      arrive();

      await typeSearch('Blog');
      expectListRead('the searched read').flush(pageOf([userRow()]));
      fixture.detectChanges();

      expect(addressParams()[FILTER_QUERY_KEY]).toBe('Blog');
      expect(addressParams()[SEARCH_BY_QUERY_KEY])
        .withContext('the default axis is omitted, so a plain name search reads as ?filter=Blog')
        .toBeUndefined();
    });

    it('states a non-default axis explicitly', async () => {
      arrive();

      chooseAxis('Email');
      await typeSearch('someone@example.test');
      expectListRead('the email read').flush(pageOf([userRow()]));
      fixture.detectChanges();

      expect(addressParams()[SEARCH_BY_QUERY_KEY]).toBe('Email');
      expect(addressParams()[FILTER_QUERY_KEY]).toBe('someone@example.test');
    });

    it('states the unfiltered listing as a word, because absence means something else here', async () => {
      arrive();

      await pressLetter(ALL_FILTER_LABEL);
      expectListRead('the unfiltered read').flush(pageOf([userRow()]));
      fixture.detectChanges();

      expect(addressParams()[SEARCH_BY_QUERY_KEY])
        .withContext('an empty address would mean "nothing asked for", which is a different query')
        .toBe('all');
      expect(addressParams()[FILTER_QUERY_KEY]).toBeUndefined();
    });

    it('restores a whole view from the address on entry: axis, term and page together', async () => {
      // ⚠ ONE READ, AT THE RIGHT COORDINATE. Every search command on the store returns the listing to the
      // first page, so restoring the search and the page separately would discard the page the address
      // asked for. The staged search also has to outrank the policy's own opening view.
      await enterAt('/users?searchby=Email&filter=a&currentpage=3');
      create();
      answerSettings();
      answerDefinitions();

      const read: TestRequest = expectListRead('the restored read');

      expect(paramOf(read, EMAIL_PARAM)).toBe('a');
      expect(paramOf(read, PAGE_INDEX_PARAM))
        .withContext('the page survived the search that would otherwise have reset it')
        .toBe('2');

      read.flush(pageOf([userRow()], 40, 2, TENANT_PAGE_SIZE));
      fixture.detectChanges();

      expect(httpMock.match(() => true))
        .withContext('and nothing further, so the restore cost exactly one listing read')
        .toHaveSize(0);
      // The restored term and axis are both shown, so a filter in force is visible and clearable.
      expect(queryOrFail<HTMLInputElement>(host(), SEARCH_INPUT_SELECTOR).value).toBe('a');
    });

    it('leaves the opening view to the policy when the address states no search', async () => {
      // THE THIRD STATE. A bare address must NOT be read as "show everything": the policy decides, exactly as
      // it does on a first ever visit, and that is the legacy behaviour this preserves.
      await enterAt('/users');
      create();
      answerSettings();
      answerDefinitions();

      expect(addressParams()[SEARCH_BY_QUERY_KEY])
        .withContext('the screen does not invent an address the operator did not ask for')
        .toBeUndefined();

      httpMock.match(() => true).forEach((pending) => pending.flush(pageOf([userRow()])));
      fixture.detectChanges();
    });

    it('corrects away a page on an address that states no search', async () => {
      await enterAt('/users?currentpage=4');
      create();
      await settleAddress();

      expect(addressParams()[PAGE_QUERY_KEY]).toBeUndefined();

      answerSettings();
      answerDefinitions();
      httpMock.match(() => true).forEach((pending) => pending.flush(pageOf([userRow()])));
      fixture.detectChanges();
    });

    it('writes the search alongside the page when a page is turned', async () => {
      // ⚠ THE CASE THAT KEEPS A POLICY-CHOSEN VIEW PAGEABLE. The policy chose the opening view, so the
      // address still states no search; writing only the page would produce an address that the rule above
      // corrects away, and the operator's page turn would be undone.
      arrive(pageOf([userRow()], 40, 0, TENANT_PAGE_SIZE));

      await pressPager(PAGER_NEXT_LABEL);
      expectListRead('the second-page read').flush(pageOf([userRow(5)], 40, 1, TENANT_PAGE_SIZE));
      fixture.detectChanges();

      expect(addressParams()[PAGE_QUERY_KEY]).toBe('2');
      expect(addressParams()[SEARCH_BY_QUERY_KEY])
        .withContext('the page is only meaningful beside the search it belongs to')
        .toBe('all');
    });

    it('clears the rows when the address becomes bare, rather than captioning stale ones', async () => {
      arrive();

      await pressLetter(ALL_FILTER_LABEL);
      expectListRead('the unfiltered read').flush(pageOf([userRow(), userRow(5)], 257, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();

      expect(host().querySelectorAll('tr.data-table__row').length)
        .withContext('the previous query really did leave rows on screen')
        .toBe(2);

      // Back to the bare address, which states no search at all.
      await enterAt('/users');
      await settleAddress();

      // ⚠ COUNTED BY THE ROW CLASS, NOT BY `tbody tr`. The shared table also renders a waiting placeholder
      // and two virtualisation spacer rows inside its body, so a bare `tbody tr` count never reaches nought
      // and would pass for any implementation.
      expect(host().querySelectorAll('tr.data-table__row').length)
        .withContext('a bare address states no search, so there is no result set to show')
        .toBe(0);
      expect(httpMock.match(() => true))
        .withContext('and nothing is read, because nothing has been asked for')
        .toHaveSize(0);
    });

    it('starts clean on a fresh entry, even though the store outlives the route', async () => {
      // THE MEASURED DEFECT: a fresh sidebar arrival landed on the page and filter of a previous visit.
      arrive();

      await typeSearch('Zeta');
      expectListRead('the searched read').flush(pageOf([userRow()], 40, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
      fixture.destroy();

      await enterAt('/users');
      create();

      // ⚠ NEITHER TENANT-WIDE READ IS RE-ISSUED, AND THAT IS THE POINT. The store outlives the route, so on a
      // fresh arrival the account policy and the declaration catalogue are already in hand; re-asking for
      // them was measured as a defect. What must still start clean is the QUERY, which is what this
      // specification goes on to assert.
      const read: TestRequest = expectListRead('the fresh read');

      expect(read.request.params.has(USER_NAME_PARAM))
        .withContext('the bare address carries no term, whatever the store still held')
        .toBeFalse();
      expect(paramOf(read, PAGE_INDEX_PARAM)).toBe('0');

      read.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });
  });

  // ---------------------------------------------------------------------------------------------------
  // THE EMPTY-TABLE FLASH
  // ---------------------------------------------------------------------------------------------------

  // ⚠ THE MEASURED DEFECT THESE PROVE CLOSED, AND THIS SCREEN HELD ITS WORST INSTANCE. Arriving here clears
  // the criteria and empties the page, and the accounts read is not issued until the tenant's POLICY has been
  // read - so for a whole round trip the grid held no rows with no request in flight, and painted its
  // zero-result surface over a tenant whose accounts had simply not been requested yet.
  describe('an un-asked listing waits rather than claiming to be empty', () => {
    it('shows the waiting placeholder, and NO zero-result surface, for the whole policy round trip', () => {
      create();

      // The policy and the declarations are outstanding; the accounts read does not exist yet.
      expect(queryAll('td[data-placeholder] app-loading-spinner').length)
        .withContext('the listing has not been asked about, so the grid is waiting')
        .toBe(1);
      expect(queryAll('app-empty-state').length)
        .withContext('nothing may assert that this tenant has no accounts before one has been read')
        .toBe(0);
      expect(queryAll('.user-list__notice').length)
        .withContext('and the no-query notice must not claim nothing was asked for while it is being decided')
        .toBe(0);

      answerSettings();
      answerDefinitions();
      answerListing(pageOf([userRow()]));

      expect(queryAll('td[data-placeholder] app-loading-spinner').length).toBe(0);
    });

    it('shows the zero-result surface once a read has genuinely answered with nothing', () => {
      create();
      answerSettings();
      answerDefinitions();
      answerListing(pageOf([]));

      expect(queryAll('td[data-placeholder] app-loading-spinner').length).toBe(0);
      expect(queryAll('app-empty-state').length)
        .withContext('a settled read that matched nothing IS the empty state')
        .toBe(1);
    });

    it('shows the no-query notice, and no waiting placeholder, when the policy asks for nothing', () => {
      create();
      answerSettings(membershipSettings({ displayMode: 2 }));
      answerDefinitions();

      // No accounts read is issued at all, so nothing is outstanding to wait for.
      expect(queryAll('.user-list__notice').length)
        .withContext('the policy has answered, and its answer is that nothing is listed until asked')
        .toBe(1);
      expect(queryAll('td[data-placeholder] app-loading-spinner').length)
        .withContext('a request that will never be made must not be waited for')
        .toBe(0);
    });
  });

  // =========================================================================
  // ABSENT AND WHITESPACE-PADDED NAME VALUES
  // =========================================================================

  describe('absent and whitespace-padded name values', () => {
    // The address and telephone cells already reported an absent value as a painted mark plus a hidden
    // explanation, while the three NAME cells rendered a bare interpolation - so one row could report
    // "not recorded" for its address and simply nothing for its name, leaving the reader to interpret an
    // empty cell. These cases pin the two annotations and, just as importantly, pin that a cell never
    // carries both at once.

    /** Enables all three name columns, which the legacy defaults hide. */
    function withNameColumns(): MembershipSettings {
      return membershipSettings({
        columnFirstName: true,
        columnLastName: true,
        columnDisplayName: true,
      });
    }

    it('reports an absent display name with the SAME mark and description the address cell uses', () => {
      arrive(pageOf([userRow(7, { displayName: '', address: '' })]), withNameColumns());

      const name = cellUnder(DISPLAY_NAME_HEADING);
      const address = cellUnder(ADDRESS_HEADING);

      // The painted mark, hidden from assistive technology. Rendered by the SHARED absent-value component,
      // which is the same element the address cell renders - two local spans beside one shared component in
      // the same row is precisely how a listing comes to report absence two different ways.
      expect(name.querySelector('app-absent-value .absent-value__mark')?.textContent).toBe('\u2014');
      expect(name.querySelector('app-absent-value .absent-value__mark')?.getAttribute('aria-hidden'))
        .toBe('true');

      // The exposed description, hidden from the painted page.
      expect(name.querySelector('app-absent-value .absent-value__description')?.textContent)
        .toBe('not recorded');

      // ⚠ THE CONVERGENCE ASSERTION. The two cells must be indistinguishable in how they report absence,
      // because a row that reports absence two different ways disagrees with itself.
      expect(name.innerHTML).toBe(address.innerHTML);
    });

    it('treats a name of nothing but whitespace as absent, not as padded', () => {
      // The column is not nullable, so a name typed as spaces and a name never given both paint as nothing.
      arrive(pageOf([userRow(7, { displayName: '    ' })]), withNameColumns());

      const name = cellUnder(DISPLAY_NAME_HEADING);

      expect(name.querySelector('app-absent-value')).not.toBeNull();
      expect(name.querySelector('.user-list__padded-value'))
        .withContext('one annotation per cell, never two')
        .toBeNull();
      expect(name.textContent).toContain('not recorded');
    });

    it('annotates a value stored with stray padding, and paints the trimmed text', () => {
      arrive(pageOf([userRow(7, { displayName: '   Padded Jones   ' })]), withNameColumns());

      const name = cellUnder(DISPLAY_NAME_HEADING);

      expect(name.querySelector('.user-list__padded-value')?.getAttribute('aria-hidden')).toBe('true');
      expect(name.querySelector('.user-list__row-mark-description')?.textContent)
        .toBe('stored with leading or trailing spaces');
      // Painted text is the trimmed form - which is what the browser would have shown anyway, the
      // difference being that the padding is now declared rather than silently swallowed.
      expect(name.textContent).toContain('Padded Jones');
      expect(name.querySelector('app-absent-value'))
        .withContext('padded is not absent')
        .toBeNull();
    });

    it('annotates padding on either side independently', () => {
      arrive(
        pageOf([userRow(7, { firstName: '  Leading', lastName: 'Trailing  ', displayName: 'Clean Value' })]),
        withNameColumns(),
      );

      expect(cellUnder(FIRST_NAME_HEADING).querySelector('.user-list__padded-value')).not.toBeNull();
      expect(cellUnder(LAST_NAME_HEADING).querySelector('.user-list__padded-value')).not.toBeNull();
      expect(cellUnder(DISPLAY_NAME_HEADING).querySelector('.user-list__padded-value'))
        .withContext('an unpadded value carries no remark')
        .toBeNull();
    });

    it('leaves an ordinary name completely unannotated', () => {
      // The negative control. A remark on every row would be noise, and would make the remark meaningless.
      arrive(pageOf([userRow(7, { firstName: 'Ada', lastName: 'Lovelace', displayName: 'Ada Lovelace' })]),
        withNameColumns());

      for (const heading of [FIRST_NAME_HEADING, LAST_NAME_HEADING, DISPLAY_NAME_HEADING]) {
        const cell = cellUnder(heading);

        expect(cell.querySelector('app-absent-value')).withContext(heading).toBeNull();
        expect(cell.querySelector('.user-list__padded-value')).withContext(heading).toBeNull();
        expect(cell.querySelector('.user-list__row-mark-description')).withContext(heading).toBeNull();
      }

      expect(cellUnder(DISPLAY_NAME_HEADING).textContent?.trim()).toBe('Ada Lovelace');
    });

    it('keeps the three name columns sortable after the change of column kind', () => {
      // Converting a field column to a template column must not cost the column its ordering, which is
      // exactly the kind of thing such a conversion silently drops.
      arrive(pageOf([userRow()]), withNameColumns());

      for (const heading of [FIRST_NAME_HEADING, LAST_NAME_HEADING, DISPLAY_NAME_HEADING]) {
        const header = Array.from(host().querySelectorAll<HTMLElement>(HEADER_SELECTOR)).find(
          (candidate) => (candidate.textContent ?? '').trim() === heading,
        );

        expect(header?.querySelector('button'))
          .withContext(`${heading} is still orderable`)
          .not.toBeNull();
      }
    });
  });

});
