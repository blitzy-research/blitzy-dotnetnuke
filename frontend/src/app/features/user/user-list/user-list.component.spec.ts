/**
 * Specification for {@link UserListComponent} — the User Accounts listing at `/users`.
 *
 * ## WHY THIS FILE CARRIES MORE WEIGHT THAN AN ORDINARY SPECIFICATION
 *
 * It is the ONLY route by which `user-list.component.ts` receives gated type-checking.
 * `tsconfig.app.json` declares `files: ["src/main.ts"]` and type-checks by IMPORT GRAPH, so a screen
 * nothing imports is silently unchecked by `ng build`; `tsconfig.spec.json` includes every
 * specification under `src` with `types: ["jasmine"]`, which is what pulls the component and its
 * template into a compilation at all. A weak specification here means an unchecked component, so
 * every branch of the screen is driven rather than merely instantiated.
 *
 * ## HOW IT IS DRIVEN
 *
 *   - Mounted as the standalone unit it is, with the REAL {@link UserStore} pinned to each case's
 *     injector and every request answered through `HttpTestingController`. The component ISSUES NO
 *     REQUEST OF ITS OWN — Minimal Change Clause item 5 confines transport to the store and its
 *     service — so each assertion below travels through the real store, the real transport, the real
 *     endpoint table and the real decoders. A double in any of those positions would have proved the
 *     double instead of the screen.
 *   - `NotificationService.notify` is spied and called through, because the removal outcome is
 *     reported through it rather than rendered.
 *   - No router is spied. Every cross-screen movement this screen offers is a LINK, and a link is
 *     asserted as an address.
 *   - `AuthStore` is replaced by a one-member double, described where it is declared.
 *
 * ## THE FACTS THAT SHAPE EVERY CASE
 *
 * ⚠ ARRIVAL IS THREE REQUESTS IN A FIXED ORDER, AND THE MIDDLE ONE IS CHAINED. `ngOnInit` calls
 * `initialise()` then `loadProfileDefinitions()`, so `GET /api/v1/users/settings` and
 * `GET /api/v1/profile-definitions` are both pending immediately, while `GET /api/v1/users` is issued
 * only once the POLICY has answered — the page size is a per-tenant setting
 * (`Website/admin/Users/Users.ascx.vb` L116 reads `Records_PerPage`), so the listing cannot be
 * requested correctly before it is known. A case that expects the listing first finds nothing.
 *
 * ⚠ THE WIRE PAGE INDEX IS ZERO-BASED AND NO ARITHMETIC EXISTS ON EITHER SIDE OF IT. The legacy screen
 * ran both bases at once — L51 seeded a one-based `CurrentPage` while L265, L269, L271 and L274 each
 * passed `CurrentPage - 1` — and the reconciliation now lives inside the shared pager, whose `page`
 * input is zero-based and whose `pageChange` output emits a zero-based index. VERIFIED, not assumed:
 * `pagination.component.ts` states "the boundary is ZERO-BASED and the display is ONE-BASED", so the
 * screen binds and forwards the wire's own base unchanged.
 *
 * ⚠ THE SEARCH TERM TRAVELS RAW. All three legacy modes appended a single trailing `%` SERVER-SIDE
 * (`SearchText + "%"` at L269, L271 and L274), so the match is a STARTS-WITH and the client sends
 * exactly what was typed.
 *
 * ⚠ THE FOUR SEARCH STATES ARE DISTINCT. `"None"` (L266) means issue NO QUERY AT ALL; `"All"`
 * (L264-L265) means a paged, unfiltered query; the two account axes and the open profile axis each
 * carry their own filter members. Absence is expressed by OMITTING a parameter, never by sending a
 * reserved word.
 *
 * ⚠ REMOVAL PRESERVES THE PAGE WHILE SEARCH RESETS IT. `grdUsers_DeleteCommand` (L646-L669) re-bound
 * the grid without touching `CurrentPage`, whereas a new search reset it at L631 and the letter strip
 * forced page one through `FilterURL(Container.DataItem,"1")` (`users.ascx` L16). The asymmetry is
 * real behaviour and is asserted in both directions.
 *
 * ## EFFECT DRAINING
 *
 * The removal outcome is reported from a component `effect`. The draining call used below is
 * `TestBed.flushEffects()`, and that choice was VERIFIED against the installed framework rather than
 * assumed: `flushEffects(): void` is declared on the `TestBed` interface in
 * `node_modules/@angular/core/testing/index.d.ts` at version 19.2.25, and `TestBed.tick()` does NOT
 * exist in this version. Nothing here uses a timer of any kind.
 *
 * ## WHAT IS DELIBERATELY NOT ASSERTED
 *
 * Interceptor headers. `Authorization` and `X-Correlation-Id` are attached by interceptors wired in
 * `app.config.ts`, and `provideHttpClientTesting()` installs none of them, so asserting either would
 * be asserting a fiction.
 */
import { signal } from '@angular/core';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { NotificationService } from '../../../core/services/notification.service';
/*
 * Imported as an INJECTION TOKEN TO REPLACE, never as a contract consumed. Three of this screen's
 * affordances are gated on the SERVER'S administration verdict, the component reads exactly one
 * projection off this store to obtain it, and the real store derives that projection from a held
 * session this specification has no business fabricating. The sibling listing and form
 * specifications replace it the same way.
 *
 * ⚠ THE PROJECTION REPLACED HERE CHANGED, AND THE CHANGE IS THE POINT. It used to be
 * `permissions()` — the four persisted permission keys — because the three affordances were gated
 * with the shared directive on the key `EDIT`. Those keys are grants held against MODULE and TAB
 * records and cannot express the `PortalAdministrator` policy the account endpoints declare, so the
 * gate is now `holdsPortalAdministration()`, which republishes
 * `CurrentUserDto.IsPortalAdministrator`.
 */
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

// =====================================================================================================
// ADDRESSES
//
// ⚠ ROOT-RELATIVE, AND THAT IS THE ONLY CORRECT SHAPE HERE. The `test` target declares no
// `fileReplacements`, so a specification compiles against the PRODUCTION environment, whose
// `apiBaseUrl` is the relative `'/api/v1'` — the proxy serves the application and the API on one
// origin, and an absolute base would resolve only inside the container network. No absolute origin
// appears anywhere in this file, and the workspace's environment module is deliberately NOT imported:
// an address asserted from the same constant the code builds it from asserts nothing.
// =====================================================================================================

const USERS_URL = '/api/v1/users';

/**
 * The body-bound account search.
 *
 * ⚠ THE LISTING NOW HAS TWO ADDRESSES, AND WHICH ONE IS USED IS A PRIVACY DECISION RATHER THAN A
 * ROUTING ONE. A search by account name, address or profile property names a person, and a query
 * parameter travels in the REQUEST TARGET — recorded by the browser's history, by every forward and
 * reverse proxy's access log, by the server's access log and by any URL-sampling telemetry, all of
 * which sit at an END of the encrypted channel rather than in the middle of it. That is CWE-598, and
 * HTTPS does not address it. Such a search goes here, in a body. The unfiltered listing and the
 * pager, which carry page coordinates, an ordering and at most an approval state, name nobody and
 * stay on the cacheable `GET`.
 *
 * This screen does nothing to obtain that: it asks the shared account transport, which chooses the
 * address from the query. The cases below therefore claim reads through {@link expectListRead}, which
 * accepts either, and read their values through {@link paramOf} — which reads a body member when the
 * request carried one. What the screen SENDS is its own business and is asserted here; WHERE the
 * value travels is the transport's and is asserted exhaustively in `core/services/user.service.spec`.
 * One case below pins the boundary from this side too, so a regression cannot be invisible here.
 */
const USERS_SEARCH_URL = '/api/v1/users/search';
const MEMBERSHIP_SETTINGS_URL = '/api/v1/users/settings';
const PROFILE_DEFINITIONS_URL = '/api/v1/profile-definitions';

/** The removal address of one account. The identifier is interpolated exactly as given. */
function userUrl(userId: number): string {
  return `${USERS_URL}/${userId}`;
}

// =====================================================================================================
// THE WIRE VOCABULARY
//
// Spelled out as constants so a rename on either side of the contract fails here rather than
// silently changing which parameter a case inspects.
// =====================================================================================================

const PAGE_INDEX_PARAM = 'pageIndex';
const PAGE_SIZE_PARAM = 'pageSize';
const USER_NAME_PARAM = 'userName';
const EMAIL_PARAM = 'email';
const PROFILE_PROPERTY_NAME_PARAM = 'profilePropertyName';
const PROFILE_PROPERTY_VALUE_PARAM = 'profilePropertyValue';

/**
 * The generic free-text parameter the paging contract publishes for OTHER listings.
 *
 * Named here only so its ABSENCE from every account request can be asserted. The account listing
 * carries four named prefix filters instead, and a term arriving under this name would be a
 * substring match against a screen whose legacy behaviour is a starts-with.
 */
const GENERIC_QUERY_PARAM = 'query';

/** Every filter name the account listing can legitimately carry. */
const SEARCH_PARAMS: readonly string[] = Object.freeze([
  USER_NAME_PARAM,
  EMAIL_PARAM,
  PROFILE_PROPERTY_NAME_PARAM,
  PROFILE_PROPERTY_VALUE_PARAM,
]);

/**
 * Parameter names that would betray a paging model this contract does not use.
 *
 * The envelope carries a total and a zero-based index, which is offset paging — the model the legacy
 * pager consumed. A cursor, a continuation token, a link relation or a skip/take pair would each be a
 * different model wearing the same clothes, and none is detectable from a passing listing assertion.
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

// =====================================================================================================
// THE WORDING THIS SCREEN PUBLISHES
//
// ⚠ THE RESOURCE VALUE IS THE AUTHORITY, NEVER THE MARKUP ATTRIBUTE. `Users.ascx.vb` L585 ran
// `Localization.LocalizeDataGrid`, which rewrote every heading from the resource file at run time, so
// five of the `headertext` values in `users.ascx` are CONTRADICTED by `Users.ascx.resx` and the
// resource file wins. Each of those five is marked below. Taking the markup attribute would have
// produced five wrong headings that no compiler could have caught.
// =====================================================================================================

const PAGE_TITLE = 'User Accounts';
const ADD_USER_LABEL = 'Add New User';
const MEMBERSHIP_SETTINGS_LABEL = 'User Settings';
const PROFILE_DEFINITIONS_LABEL = 'Manage Profile Properties';
const SEARCH_FIELD_CAPTION = 'Search by';
const SEARCH_PLACEHOLDER = 'Search accounts';
const RETRY_LABEL = 'Try again';

/** `SharedResources.resx` `Edit.Text` — the local file carries no `Edit` key, so this is a fall-through. */
const EDIT_COMMAND_LABEL = 'Edit';

/** `Users.ascx.resx` `Delete.Text`, present locally. */
const DELETE_COMMAND_LABEL = 'Delete';

/** `Users.ascx.resx` `UserRoles.Text` — keyed by the legacy COMMAND NAME, which is why it is not "User Roles". */
const MANAGE_ROLES_COMMAND_LABEL = 'Manage Roles';

/** `SharedResources.resx` `DeleteItem.Text`, reached from `Users.ascx.vb` L523. */
const REMOVAL_CONFIRM_MESSAGE = 'Are You Sure You Wish To Delete This Item?';

/** `Users.ascx.resx` `UserDeleted.Text`, reported at `Users.ascx.vb` L655 at the success severity. */
const USER_DELETED_MESSAGE = 'User Deleted Successfully';

/** `SharedResources.resx` `UserDeleteError.Text`, reported at L657. Another three-level fall-through. */
const USER_DELETE_ERROR_MESSAGE = 'Error Deleting User';

/** `SharedResources.resx` `All.Text`, appended to the alphabet strip at `Users.ascx.vb` L308. */
const ALL_FILTER_LABEL = 'All';

const USERNAME_HEADING = 'Username';

/** `FirstName.Header` — the markup says "FirstName". DL-3 case one. */
const FIRST_NAME_HEADING = 'First Name';

/** `LastName.Header` — the markup says "LastName". DL-3 case two. */
const LAST_NAME_HEADING = 'Last Name';

/** `DisplayName.Header` — the markup says "DisplayName". DL-3 case three. */
const DISPLAY_NAME_HEADING = 'Name';

const ADDRESS_HEADING = 'Address';
const TELEPHONE_HEADING = 'Telephone';
const EMAIL_HEADING = 'Email';

/** `CreatedDate.Header` — the markup says "CreatedDate". DL-3 case four. */
const CREATED_DATE_HEADING = 'Created Date';

/** `LastLogin.Header` — the markup says "LastLogin". DL-3 case five. */
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

/** The shared empty state's own default wording; this screen passes it no message. */
const EMPTY_STATE_MESSAGE = 'No records found.';

/**
 * The persisted permission key the three mutating affordances USED to be gated on, held only so
 * this specification can assert that it no longer appears anywhere in the rendered screen.
 *
 * ⚠ IT WAS THE WRONG VOCABULARY. `EDIT` is a grant over a module or page INSTANCE, carried in
 * `ModulePermissions` and `TabPermissions`. Every destination these affordances address —
 * `/users/new`, `/users/{id}`, and the removal endpoint — is declared under the
 * `PortalAdministrator` POLICY, which is answered from `Portals.AdministratorRoleId` and not from
 * any persisted key. The old gate could therefore hide a screen the caller may reach and offer one
 * the server will refuse, in the same session.
 */
const EDIT_PERMISSION = 'EDIT';

/**
 * The shared pager's own accessible names for its four steps.
 *
 * ⚠ THE PAGER OFFERS NO NUMBERED ENTRIES. It renders first, previous, next and last plus a position
 * readout, so a case moves pages by pressing a step rather than by pressing a number — verified in the
 * component's template rather than assumed.
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
const CELL_SELECTOR = 'td.data-table__cell';
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

/**
 * A distributed-trace identifier, in the W3C shape the server emits.
 *
 * Used WITHOUT a correlation identifier wherever the retention of this value is the point, because the
 * shared summariser prefers a correlation identifier when one is present and would otherwise have
 * masked whether the trace identifier survived at all.
 */
const TRACE_ID = '00-4b1f9d1cb7f24a9e8e1a6c5d3f207b41-9f2c7d5a1e0b4c63-01';

const CORRELATION_ID = '2f8b1c74-5d93-4e02-9a6f-7c1b0d84e5a2';

/** The shared vocabulary's sentence for a refusal, used to prove the wording is not re-authored here. */
const FORBIDDEN_MESSAGE = 'You do not have permission to perform this action.';

/** The severity word the shared banner paints for a refusal. */
const WARNING_SEVERITY_LABEL = 'Warning';

/** The severity word it paints for everything that is genuinely an error. */
const ERROR_SEVERITY_LABEL = 'Error';

/**
 * Builds an RFC 7807 problem document.
 *
 * ⚠ `errors` IS KEYED AS THE SERVER KEYS IT. The map arrives from .NET's model-state dictionary, whose
 * keys are the PROPERTY NAMES of the request contract in their original casing — `UserName`, not
 * `userName` — so every read of it in this file is a BRACKET access. `noPropertyAccessFromIndexSignature`
 * is enabled, which makes `problem.errors.UserName` a compilation error rather than a silent
 * `undefined`; the bracket form is the only correct one and is used throughout.
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
 * A problem document carrying NEITHER a summary nor a sentence.
 *
 * ⚠ THIS SHAPE IS WHAT REACHES THE SHARED STATUS VOCABULARY AT ALL. The summariser prefers the server's
 * `detail`, then its `title`, and only then falls back to a sentence of its own — so a document carrying
 * the bare status word "Forbidden" as its title renders THAT, and a case meaning to assert the shared
 * wording has to omit both members. The API does emit documents of this shape: an authorisation refusal
 * raised by the policy handler carries a type and a status and nothing a person can read.
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
 * One account row, in the exact shape `decodeUserListItem` accepts.
 *
 * ⚠ EVERY MEMBER IS PRESENT AND EVERY SPELLING IS THE WIRE'S. The listing decoder is strict — it
 * refuses an undeclared member value and rejects the WHOLE page — so an omitted member or the
 * plausible-looking `userName` in place of `username` would fail the read rather than the assertion,
 * which is a far harder failure to diagnose.
 *
 * ⚠ THE DEFAULT TENANT KEY IS MINUS ONE, AND THAT IS A SCHEMA FACT RATHER THAN A CURIOSITY.
 * `dbo.Portals.PortalID` is declared `IDENTITY (-1, 1)`
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L77), so the first
 * portal ever created carries minus one — while `Library/Components/Shared/Null.vb` L41-L45
 * simultaneously defines `NullInteger` as minus one. The same number therefore means both "the first
 * tenant" and "no tenant", which is why nothing in this file tests an identifier for truthiness, for
 * positivity, or against minus one.
 *
 * @param userId The account identifier. Defaults to a value no seed produces, so a case that means to
 * exercise an identity edge has to say so.
 * @param overrides Members to replace.
 * @returns The row.
 */
/**
 * The signed-in caller's identity.
 *
 * ⚠ THE TENANT KEY IS -1 AND THE ACCOUNT KEY IS 99, BOTH DELIBERATE. `Portals.PortalID` is
 * `IDENTITY(-1, 1)`, so -1 is the FIRST REAL TENANT as well as the legacy marker for a missing
 * integer — a fixture using a tidier value would not exercise the screen's explicit presence tests.
 * The account key differs from every row fixture's default, so a case that means to make the caller
 * its own row has to say so.
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
    roles: [],
    permissions: [],
    ...overrides,
  };
}

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
 * The tenant's account policy, in the shape `decodeMembershipSettings` accepts.
 *
 * ⚠ ALL NINE OPTIONAL COLUMNS ARE SWITCHED ON HERE, and that is a fixture choice rather than the
 * product default. `UserModuleBase.GetSettings` (`Library/Components/Users/UserModuleBase.vb`
 * L98-L124) left FOUR of the nine off — the two name parts, the address column's neighbour and the
 * last-login column — and the component reproduces those defaults for a tenant whose policy cannot be
 * read. Switching them all on is what lets the ordinary cases below assert the complete column set;
 * the defaults are asserted separately, in the case that fails the policy read.
 *
 * ⚠ THE PAGE SIZE IS DECLARED HERE AND NOWHERE ELSE. `Users.ascx.vb` L114-L119 read it from the
 * tenant's `Records_PerPage` setting, so it was never a constant in the legacy screen and is never a
 * literal in an assertion below: a case asserts the value it supplied.
 *
 * @param overrides Members to replace.
 * @returns The policy.
 */
function membershipSettings(overrides: Partial<MembershipSettings> = {}): MembershipSettings {
  return {
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
 * One tenant-declared profile property, in the shape `decodeProfilePropertyDefinition` accepts.
 *
 * Declared as a local shape rather than imported, because the profile contract is not one of this
 * screen's own dependencies: the declarations reach the component as NAMES through the store's
 * derived view, and what this file needs is a body the decoder accepts. Every member the decoder
 * requires is present.
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

/**
 * A deliberately unusual property name.
 *
 * The third search axis is an OPEN SET — `Users.ascx.vb` L272-L274 passed its field name straight
 * through as the property name — so a name is neither validated, case-folded nor checked against a
 * list. A name carrying mixed case, a digit, an underscore and a space is what proves that: a screen
 * that normalised anything would visibly alter this one.
 */
const ODD_PROPERTY_NAME = 'Xx_Legacy Field 42';

/** A second declared property, so the option list is provably a list rather than a single entry. */
const SECOND_PROPERTY_NAME = 'City';

/** The tenant's declarations, in the order the server returned them. */
const PROFILE_DEFINITIONS: readonly Readonly<Record<string, unknown>>[] = Object.freeze([
  profileDefinition(ODD_PROPERTY_NAME, 11),
  profileDefinition(SECOND_PROPERTY_NAME, 12),
]);

/** The single-payload envelope. `meta` is framing the caller never sees; the decoder tolerates null. */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

/**
 * A page of accounts.
 *
 * ⚠ THE PAYLOAD MEMBER OF A PAGED LISTING IS `items`, NOT `data`. A fixture spelling it otherwise
 * flushes successfully and unwraps to no rows at all, which reads as an empty result rather than as a
 * malformed body.
 *
 * ⚠ `meta` IS REQUIRED HERE, unlike on the single-payload envelope: the total and the coordinates ARE
 * the page, and treating their absence as an empty first page is exactly the defect the page decoder
 * exists to prevent.
 *
 * @param items The rows on this page.
 * @param totalCount The total across every page. Defaults to the rows in hand, which is the
 * single-page case.
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
 * One named field of an unknown value, or `undefined` where the value cannot carry fields.
 *
 * ⚠ READ THROUGH `Reflect.get` RATHER THAN THROUGH A CAST. Asserting an unknown into an index
 * signature is the shape of assertion that hides a mistake: it type-checks against a value that may be
 * a number, a string or nothing at all, and then fails at run time instead. `Reflect.get` needs only
 * that the target IS an object, which the guard establishes, so nothing here is unchecked — and this
 * file contains no `any`, no non-null assertion and no suppression comment, in a specification exactly
 * as in production code.
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
 * Whether a component type was compiled with `OnPush` change detection.
 *
 * ⚠ READ STRUCTURALLY BECAUSE NOTHING ELSE CAN WITNESS IT. The usual demonstration moves an input with
 * `setInput` and shows one repaint — but this is a ROUTED SCREEN WITH NO INPUTS AT ALL, so `setInput`
 * would throw rather than prove anything. Nor is the strategy observable through the store: every
 * slice this screen renders is a signal, and a signal read in a template marks its consumer dirty
 * under either strategy. The compiled definition is the only honest witness, and the declaration is
 * worth witnessing — the non-functional requirements make `OnPush` mandatory and nothing else in this
 * suite would notice its removal.
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
   * Whether {@link create} has run in the CURRENT case.
   *
   * ⚠ A CLOSURE VARIABLE OUTLIVES THE CASE THAT ASSIGNED IT, so `fixture` still holds the previous
   * case's component even in a case that never mounted one. This flag is what lets teardown tell
   * "nothing was mounted" from "something was", rather than destroying a fixture a previous case has
   * already destroyed.
   */
  let mounted = false;

  beforeEach(async () => {
    mounted = false;

    /*
     * ⚠ TENANT ADMINISTRATION IS THE INPUT TO THIS SCREEN, so it lives in a signal the cases can move.
     * Five affordances are gated on it — the three header actions, the row edit link and the row
     * delete button — and every destination they address is declared under the `PortalAdministrator`
     * policy. The fact that decides them is therefore the server's own determination, re-exposed by
     * the identity projection as `administersCurrentPortal`, and never a persisted permission key and
     * never a role name.
     *
     * Seeded as ADMINISTERING, so the ordinary cases describe the screen an administrator sees. The
     * gating itself is proved separately, by taking the determination away.
     */
    administersPortal = signal<boolean>(true);

    /*
     * ⚠ THE CALLER'S OWN IDENTITY, which this screen reads for exactly two facts: the tenant to ask
     * the protected facts for, and the account key the row-level removal guard compares against. Both
     * are read through `currentUser()` rather than passed in, because this screen names no portal and
     * no caller in its route.
     *
     * Seeded as an ordinary administrator of tenant -1 — a REAL tenant key, `Portals.PortalID` being
     * seeded at -1 — who is not the account any fixture row describes.
     */
    callerIdentity = signal<CurrentUser | null>(callerAccount());

    /*
     * ⚠ THE TENANT'S DESIGNATED ADMINISTRATOR, held separately because it is the fact the removal
     * guard turns on and it arrives ASYNCHRONOUSLY — `null` until the tenant's own record has been
     * read. Seeded null, which is the state the screen paints in before the read lands, so the
     * ordinary cases describe the pre-read screen and the protection is proved by stating the fact.
     */
    designatedAdministrator = signal<number | null>(null);

    /*
     * The request for those facts, spied rather than served. The portal store is doubled here because
     * the real one would issue a tenant read on arrival that all 100-odd cases below would have to
     * answer, and because the spy is a sharper assertion than a flushed response: it records the
     * tenant asked for, and whether it was asked at all.
     */
    loadCurrentPortalContext = jasmine.createSpy('loadCurrentPortalContext');

    await TestBed.configureTestingModule({
      imports: [UserListComponent],
      providers: [
        // ⚠ ORDER IS LOAD-BEARING: the real client FIRST, then the testing backend that displaces it.
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        /*
         * The REAL store, pinned to this injector rather than left to its root registration, so each
         * case starts from a store that has read nothing. It is the subject of half the assertions
         * below — the page it asks for, the filter it sends, the page it preserves — and a double
         * would have proved the double.
         */
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
    /*
     * ⚠ THE FIXTURE IS TORN DOWN BEFORE THE BACKEND IS VERIFIED, AND THAT ORDER IS DELIBERATE.
     *
     * The confirmation this screen raises is a native `<dialog>`, and the top layer it opens into
     * belongs to the DOCUMENT rather than to the fixture — one Karma page hosts every specification in
     * the suite, so a confirmation left open here is still open when an unrelated specification runs
     * and its modal backdrop swallows that specification's clicks. Destroying closes it. Teardown also
     * cancels the store's outstanding reads, which is what makes the verification below a statement
     * about requests the SCREEN issued rather than about ones its teardown left behind.
     */
    if (mounted) {
      fixture.destroy();
      mounted = false;
    }

    /*
     * ⚠ MANDATORY, AND IT DOUBLES AS A POSITIVE ASSERTION. Every case answers exactly the requests it
     * provoked, so an unanticipated call — a duplicated read, a mutation issued twice, a request the
     * screen should not have made at all — fails here even where nothing asserted its absence. Without
     * it an unflushed or unexpected request passes silently, which is the commonest false green there
     * is in an Angular suite.
     */
    httpMock.verify();
  });

  // ---------------------------------------------------------------------------------------------------
  // HARNESS
  // ---------------------------------------------------------------------------------------------------

  /** Creates the screen. `ngOnInit` issues the policy read and the declarations read during this pass. */
  function create(): void {
    fixture = TestBed.createComponent(UserListComponent);
    mounted = true;
    fixture.detectChanges();
  }

  /**
   * Consumes exactly one pending request, asserted by verb AND address.
   *
   * Matched on `url`, which is the address WITHOUT the query string, so a case states the endpoint here
   * and inspects the parameters separately. Matching on `urlWithParams` instead would have made every
   * expectation restate every parameter and would have coupled unrelated cases to the parameter order.
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
   * Answers the tenant's profile declarations.
   *
   * ⚠ UNPAGED, AND THE ABSENCE OF EVERY COORDINATE IS PART OF THE CONTRACT. The transport returns a
   * plain array inside the single-payload envelope and the store holds no page index, page size or
   * total for it.
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
   * Mounts the screen and settles all three arrival reads.
   *
   * ⚠ THE ORDER IS THE COMPONENT'S, NOT THIS HELPER'S. The policy is answered first because the listing
   * is not issued until it has been; the declarations are independent and are answered between the two
   * only because that keeps the pending set small.
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
   * The component's host element.
   *
   * ⚠ BY ASSIGNMENT, NOT BY CAST. `ComponentFixture.nativeElement` is declared `any`, so the annotated
   * local is what gives it a type — and it is a real check rather than a cosmetic one, because a cast
   * would equally have accepted a wrong element type and pushed the failure into whichever assertion
   * happened to touch it first.
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
   * The one element matching `selector`, narrowed by a real check.
   *
   * ⚠ THIS EXISTS BECAUSE A NON-NULL ASSERTION IS NOT ALLOWED HERE. `element!.textContent` would silence
   * the compiler and then read `textContent` of `null` at run time, and Jasmine reports that as a bare
   * `TypeError` naming neither the selector nor the case's intent. Throwing on the absence names the
   * selector that was missing, which is the difference between a diagnosis and a puzzle.
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

  /**
   * The wording of every strip entry currently reporting itself as applied.
   *
   * Reads the ATTRIBUTE'S VALUE rather than its presence, because the value is the state: the
   * attribute is emitted on all twenty-seven entries, and a presence test would answer with the whole
   * strip.
   */
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
   * The DATA cells of one row — the three command cells excluded.
   *
   * The commands carry their own cell class, so the two families are distinguishable without counting
   * positions, which keeps a case that adds a column from breaking every other case.
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
   * One data cell of the single painted row, addressed by its heading.
   *
   * Located by matching the heading text against the VISIBLE heading order, so a case names the column
   * it means rather than an index that a visibility change would silently move.
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
      first.querySelectorAll<HTMLTableCellElement>('td'),
    );
    const cell: HTMLTableCellElement | undefined = cells[position];

    if (cell === undefined) {
      throw new Error(`Expected a cell in position ${position} for "${heading}"`);
    }

    return cell;
  }

  /**
   * The value of one query parameter, narrowed without a non-null assertion.
   *
   * ⚠ `HttpParams.get` RETURNS `string | null`, and `!` is forbidden in this file. Throwing on the
   * absence names the parameter, which is what makes a missing-parameter failure legible; a case that
   * means to assert ABSENCE uses `has` instead, because a `get` returning null is a DIFFERENT assertion
   * that also passes for a parameter that is present and empty.
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

      // Stringified so that a case reads the same value whichever address carried it. A query
      // parameter is always text, and a body member is typed — a page index is a number there — so
      // without this every coordinate assertion would have to be written twice.
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
   * Returning `null` rather than an empty record is what lets {@link paramOf} and {@link carries}
   * tell "this was a `GET`, read the query" from "this was a search whose body omits the member" —
   * two answers a single empty record would collapse into one.
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
    httpMock.expectNone(
      (candidate) =>
        (candidate.method === 'GET' && candidate.url === USERS_URL)
        || (candidate.method === 'POST' && candidate.url === USERS_SEARCH_URL),
    );
  }

  /**
   * A button inside `root` whose rendered wording is exactly `label`.
   *
   * ⚠ SCOPED TO A SUBTREE ON PURPOSE. The row delete command and the confirmation's affirmative control
   * BOTH read "Delete" — the affirmative wording is deliberately the legacy `Delete.Text` — so a
   * document-wide search by wording would find whichever came first in the document and the flow would
   * pass while testing the wrong control.
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
   * Types a term into the shared search control and submits it immediately.
   *
   * ⚠ SUBMITTED RATHER THAN LEFT TO DEBOUNCE, which is what keeps this file free of timers. The shared
   * control debounces its typing path but emits at once on Enter and on its submit control, so pressing
   * the submit control is the synchronous path — and it is also the legacy path: `users.ascx` L9
   * declared an image button that ran the query, and typing alone ran nothing.
   *
   * ⚠ THE SHARED CONTROL SUPPRESSES A DUPLICATE TERM. It remembers the last term it emitted and
   * discards a repeat, so two cases wanting two requests must use two DIFFERENT terms.
   *
   * @param term The text to type, passed through exactly as given.
   */
  function typeSearch(term: string): void {
    const field = queryOrFail<HTMLInputElement>(host(), SEARCH_INPUT_SELECTOR);

    field.value = term;
    field.dispatchEvent(new Event('input'));

    const submit = queryOrFail<HTMLButtonElement>(host(), SEARCH_SUBMIT_SELECTOR);

    submit.click();
    fixture.detectChanges();
  }

  /**
   * Chooses a search axis by its rendered wording.
   *
   * Changing the axis DISPATCHES NOTHING, which is parity rather than an omission: `users.ascx` L8
   * declared the selector with no auto-post-back, so a new selection had no effect until the search
   * control was used.
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
  function pressLetter(affordance: string): void {
    const entry: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      LETTER_SELECTOR,
    ).find((candidate) => textIn(candidate) === affordance);

    if (entry === undefined) {
      throw new Error(`Expected an alphabet entry "${affordance}"`);
    }

    entry.click();
    fixture.detectChanges();
  }

  /**
   * Presses one step of the shared pager, addressed by its accessible name.
   *
   * @param label One of the pager's four step names.
   */
  function pressPager(label: string): void {
    const step: HTMLButtonElement | undefined = queryAll<HTMLButtonElement>(
      'button.pagination__button',
    ).find((candidate) => candidate.getAttribute('aria-label') === label);

    if (step === undefined) {
      throw new Error(`Expected a pager step named "${label}"`);
    }

    step.click();
    fixture.detectChanges();
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
   * Presses the confirmation's affirmative control.
   *
   * ⚠ ADDRESSED BY ITS DANGER MODIFIER RATHER THAN BY EXACT WORDING, and the reason is worth recording:
   * the dialogue prefixes a warning glyph to the label when the danger input is set, so the control's
   * text is the glyph AND the legacy `Delete.Text` rather than the label alone. Matching the wording
   * exactly would fail here for a reason that has nothing to do with the flow, and matching it loosely
   * across the document would find the ROW command, which carries the same word. The label itself is
   * asserted to be present, so the wording is still checked.
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
   * Opens the confirmation for the first row and accepts it, returning the removal request.
   *
   * The confirmation is a real dialogue rather than a browser prompt, so acceptance is a press on its
   * own affirmative control — scoped to the dialogue, because the row command carries the same wording.
   *
   * The outcome is NOT settled here: nothing can be reported until the removal itself has answered, so
   * a case answers the request and then calls {@link settleOutcome}.
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
   * Drains the effect that reports a settled removal.
   *
   * ⚠ `TestBed.flushEffects()` IS THE API THIS FRAMEWORK VERSION PUBLISHES. It is declared on the
   * `TestBed` interface in the installed `@angular/core@19.2.25`; `TestBed.tick()` does not exist here,
   * so there is nothing else to use — and nothing in this file is asynchronous, so nothing else is
   * needed.
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

      /*
       * Both independent reads are already in flight, and the listing is NOT: the page size is a
       * per-tenant setting (`Users.ascx.vb` L116 reads `Records_PerPage`), so the listing cannot be
       * requested correctly until the policy that declares it is in hand.
       *
       * ⚠ AN EXPECTATION CONSUMES ITS REQUEST, so the two in flight are captured and answered through these
       * handles rather than expected a second time. Expecting the same request twice is a self-inflicted
       * "found none" that says nothing at all about the screen.
       */
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

    it('lists the accounts even when the tenant policy cannot be read, at the shared fallback size', () => {
      create();

      expectRequest('GET', MEMBERSHIP_SETTINGS_URL).flush(
        problem('not_found', 404, 'That portal could not be resolved.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();
      answerDefinitions();

      const listing = answerListing(pageOf([userRow()], 1, 0, 10));

      /*
       * ⚠ THE LISTING FOLLOWS ON BOTH OUTCOMES. A tenant whose policy is unavailable still has accounts,
       * and a listing at the shared fallback size beside a recorded failure is a better answer than no
       * listing at all. The fallback is the paging contract's own `DEFAULT_PAGE_SIZE`, which is where
       * the legacy default of ten from `UserModuleBase.vb` L134-L136 now lives — so this case asserts
       * that a size was sent WITHOUT restating the number, because the number belongs to that contract.
       */
      expect(carries(listing, PAGE_SIZE_PARAM)).toBeTrue();
      expect(paramOf(listing, PAGE_SIZE_PARAM)).not.toBe(String(TENANT_PAGE_SIZE));
      expect(rows()).toHaveSize(1);
    });

    it('shows only the four columns the legacy defaults left visible when the policy cannot be read', () => {
      create();
      expectRequest('GET', MEMBERSHIP_SETTINGS_URL).flush(
        problem('not_found', 404, 'That portal could not be resolved.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();
      answerDefinitions();
      answerListing(pageOf([userRow()], 1, 0, 10));

      /*
       * MEASURED, NOT ASSUMED, AND NOT UNIFORMLY TRUE. `UserModuleBase.GetSettings`
       * (`Library/Components/Users/UserModuleBase.vb` L98-L124) filled each unset key with a value that
       * left FOUR of the nine optional columns HIDDEN: both name parts, the electronic-mail column and
       * the last-login column. A default of true everywhere would have shown four columns the legacy
       * screen did not, so the four absences are asserted as firmly as the five presences.
       */
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
      create();
      expectRequest('GET', MEMBERSHIP_SETTINGS_URL).flush(
        problem('not_found', 404, 'That portal could not be resolved.'),
        { status: 404, statusText: 'Not Found' },
      );
      fixture.detectChanges();
      answerDefinitions();
      answerListing(pageOf([userRow()], 1, 0, 10));

      pressIn(host(), RETRY_LABEL);

      /*
       * The POLICY is re-read as well as the listing, because a listing read at the fallback size beside
       * an unreadable policy is exactly the state the retry recovers from.
       */
      answerSettings();
      answerDefinitions();
      answerListing();

      expect(query(RETRY_SELECTOR)).toBeNull();
    });
  });

  // ===================================================================================================
  // §3.1 — SERVER-SIDE PAGING THROUGH THE PagedResult ENVELOPE
  // ===================================================================================================

  describe('the paging envelope', () => {
    it('takes the rows and the total from one envelope rather than from a by-reference argument', () => {
      /*
       * MIGRATION: the legacy total arrived through an argument passed BY REFERENCE —
       * `GetUsers(portalId, …, pageIndex, pageSize, ByRef totalRecords)` — so the count was a side
       * effect on one of the screen's own fields. It now travels inside the envelope beside the rows,
       * which is what lets one answer settle both.
       *
       * ⚠ MEASURED CORRECTION TO THE ACTION PLAN: that idiom is cited there as having three sites.
       * `Library/Components/Users/UserController.vb` carries EIGHT — L725, L746, L769, L793, L816,
       * L840, L864 and L889 — every one of them a paged account reader.
       */
      arrive(pageOf([userRow(1), userRow(2)], 9, 0, TENANT_PAGE_SIZE));

      expect(rows()).toHaveSize(2);

      // The total is the pager's, and it is the envelope's `totalCount` that reaches it — both in the
      // page count it derives and in the readout it paints.
      expect(pagerPosition()).toBe(`1 / ${Math.ceil(9 / TENANT_PAGE_SIZE)}`);
      expect(textIn(queryOrFail<Element>(host(), 'p.pagination__status'))).toContain('of 9');
    });

    it('requests the page size the tenant policy declares, never a hard-coded literal', () => {
      const listing = arrive();

      /*
       * `Users.ascx.vb` L114-L119 read the size from the tenant's `Records_PerPage` setting through
       * `UserModuleBase.GetSetting`, so it was never a constant in the legacy screen either. The
       * expected value is read back off the fixture rather than written out, so this case cannot drift
       * into asserting a literal.
       */
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

      /*
       * ⚠ THE ENVELOPE CARRIES A TOTAL AND A ZERO-BASED INDEX, WHICH IS OFFSET PAGING — the model the
       * legacy pager consumed. Every name below is a different paging model wearing the same clothes,
       * and none of them is detectable from a listing assertion that merely passes.
       *
       * `page` is in that list deliberately: the shared pager's INPUT is called `page`, and a screen
       * that forwarded its own input name to the wire would look right and request under a name the
       * server does not bind.
       */
      for (const foreign of FOREIGN_PAGING_PARAMS) {
        expect(carries(listing, foreign))
          .withContext(`the listing must not send "${foreign}"`)
          .toBeFalse();
      }

      expect(carries(listing, PAGE_INDEX_PARAM)).toBeTrue();
      expect(carries(listing, PAGE_SIZE_PARAM)).toBeTrue();
    });

    it('sends no ordering parameters, because the legacy grid offered no sorting', () => {
      const listing = arrive();

      /*
       * `users.ascx` L22-L23 declares no `AllowSorting` and the code-behind has no sort handler, so the
       * legacy grid could not be reordered. Offering sorting would be an enhancement rather than a
       * port, and the ordering the server chooses applies.
       */
      expect(carries(listing, 'sortBy')).toBeFalse();
      expect(carries(listing, 'sortDir')).toBeFalse();
      expect(queryAll('button.data-table__sort')).toHaveSize(0);
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
      /*
       * ⚠ THE NEGATIVE CONTROL, AND THE WHOLE REASON THIS DESCRIBE EXISTS. The legacy screen ran BOTH
       * bases at once — `Users.ascx.vb` L51 seeded a one-based `CurrentPage` while L265, L269, L271 and
       * L274 each passed `CurrentPage - 1` — so an off-by-one here is the single likeliest defect in the
       * whole migration of this screen, and it is invisible to a case that only asserts what the first
       * page DOES send: `expect(index).toBe('0')` would still read '0' if the code sent the one-based
       * number and subtracted one twice somewhere. Asserting the forbidden value explicitly is what
       * catches the class rather than the instance.
       */
      const listing = arrive();

      expect(paramOf(listing, PAGE_INDEX_PARAM)).not.toBe('1');
    });

    it('sends 1 for the second page', () => {
      arrive(pageOf([userRow(1)], 9, 0, TENANT_PAGE_SIZE));

      pressPager(PAGER_NEXT_LABEL);

      const second = expectListRead('the second-page read');

      expect(paramOf(second, PAGE_INDEX_PARAM)).toBe('1');

      second.flush(pageOf([userRow(5)], 9, 1, TENANT_PAGE_SIZE));
      fixture.detectChanges();

      // And the reader is shown the one-based number for that same zero-based index.
      expect(pagerPosition()).toBe(`2 / ${Math.ceil(9 / TENANT_PAGE_SIZE)}`);
    });

    it('forwards the pager index unchanged, adding and subtracting nothing', () => {
      /*
       * ⚠ THE BRANCH THIS COMPONENT TOOK, STATED EXPLICITLY: BOTH ENDS ARE ALREADY ZERO-BASED, so there
       * is NO ±1 ARITHMETIC in the screen at all. Verified in the shared pager rather than assumed — it
       * documents that "the boundary is ZERO-BASED and the display is ONE-BASED", its `page` input takes
       * the wire's index, it derives the one-based number a reader sees internally, and its change
       * output emits a zero-based index. Adding one in the screen would show the wrong page number AND
       * request the wrong page.
       *
       * Proved on a LATER page than the second, because index and display differ by one everywhere and
       * only a third page distinguishes "forwarded unchanged" from "off by one in both directions".
       */
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      pressPager(PAGER_LAST_LABEL);

      const third = expectListRead('the last-page read');

      expect(paramOf(third, PAGE_INDEX_PARAM)).toBe('2');

      third.flush(pageOf([userRow(9)], 12, 2, TENANT_PAGE_SIZE));
      fixture.detectChanges();

      // The pager shows the ONE-BASED number for the same page, which is its own conversion and not this
      // screen's: the index bound to it is still 2.
      expect(pagerPosition()).toBe('3 / 3');
    });

    it('binds the index the SERVER reported, so the pager cannot claim a page whose request failed', () => {
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      pressPager(PAGER_NEXT_LABEL);

      expectListRead('the second-page read').flush(
        problem('server_error', 500, 'The server could not complete the request.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();

      // Still page one, because the page in hand is the one the server last reported.
      expect(pagerPosition()).toBe('1 / 3');
    });

    it('returns to the first page through the pager as well, sending 0 again', () => {
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      pressPager(PAGER_NEXT_LABEL);
      expectListRead('the second-page read').flush(
        pageOf([userRow(5)], 12, 1, TENANT_PAGE_SIZE),
      );
      fixture.detectChanges();

      pressPager(PAGER_FIRST_LABEL);

      const back = expectListRead('the return to the first page');

      expect(paramOf(back, PAGE_INDEX_PARAM)).toBe('0');

      back.flush(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    });

    it('steps backwards to index 1 from the last page, never to a negative index', () => {
      /*
       * The pager emits only a whole index inside the available range, and the store passes the index
       * straight to the transport without clamping it — so stepping back from the last of three pages must
       * ask for index one. A screen that adjusted the index would ask for nought or for minus one here, and
       * minus one is the value the server refuses with a field-level message rather than reinterpreting.
       */
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      pressPager(PAGER_LAST_LABEL);
      expectListRead('the last-page read').flush(
        pageOf([userRow(9)], 12, 2, TENANT_PAGE_SIZE),
      );
      fixture.detectChanges();

      pressPager(PAGER_PREVIOUS_LABEL);

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
     * @returns Nothing; the second page is settled when this returns.
     */
    function goToSecondPage(): void {
      pressPager(PAGER_NEXT_LABEL);

      const second = expectListRead('the second-page read');

      expect(paramOf(second, PAGE_INDEX_PARAM)).toBe('1');

      second.flush(pageOf([userRow(5)], 12, 1, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    }

    it('resets to the first page when a letter is activated from a later page', () => {
      /*
       * `FilterURL` (`Users.ascx.vb` L446-L456) was called from the strip with a LITERAL page argument of
       * "1" (`users.ascx` L16), so a letter always returned to the first page — and asking for the fifth
       * page of a match set that now has one would answer with nothing while the pager insisted there was
       * something there.
       */
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      goToSecondPage();

      pressLetter('B');

      const filtered = expectListRead('the letter-filtered read');

      expect(paramOf(filtered, PAGE_INDEX_PARAM)).toBe('0');
      expect(paramOf(filtered, USER_NAME_PARAM)).toBe('B');

      filtered.flush(pageOf([userRow(2)], 1, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    });

    it('resets to the first page for a new search term from a later page', () => {
      // `Users.ascx.vb` L631 set `CurrentPage = 1` before redirecting, for the same reason.
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      goToSecondPage();

      typeSearch('blog');

      const searched = expectListRead('the searched read');

      expect(paramOf(searched, PAGE_INDEX_PARAM)).toBe('0');
      expect(paramOf(searched, USER_NAME_PARAM)).toBe('blog');

      searched.flush(pageOf([userRow(3)], 1, 0, TENANT_PAGE_SIZE));
      fixture.detectChanges();
    });

    it('resets to the first page for the unfiltered affordance from a later page', () => {
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));
      goToSecondPage();

      pressLetter(ALL_FILTER_LABEL);

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

      /*
       * MIGRATION: THE CONFIRMATION IS A REAL DIALOGUE. `Page_Init` L522-L524 attached it as a JavaScript
       * string on the command column, which the framework emitted as a browser confirmation prompt; the
       * shared dialogue replaces it with a focus trap, escape handling and an accessible name the prompt
       * had none of. Its PRESENCE in the document is what "open" means.
       */
      expect(dialog()).not.toBeNull();
      expect(textIn(queryOrFail<Element>(openDialog(), 'p.confirm-dialog__message'))).toBe(
        REMOVAL_CONFIRM_MESSAGE,
      );
      httpMock.expectNone(userUrl(7));
    });

    it('renders no delete command at all for a row the server will not let go', () => {
      /*
       * ⚠ THE PARITY THIS RESTORES. `grdUsers_ItemDataBound` (`Users.ascx.vb` L691-L692) read
       * `delImage.Visible = Not (user.UserID = PortalSettings.AdministratorId) AndAlso Not
       * (user.UserID = Me.UserId And user.IsSuperUser)` — the command was HIDDEN, not disabled, for
       * a protected account. Neither fact was reachable from this feature, so the server publishes
       * the capability on the row and the row is not rendered a command it cannot use.
       *
       * RENDERED AS NOTHING RATHER THAN AS A DISABLED CONTROL, because a disabled button still
       * reaches assistive technology as an inoperable control that destroys a record, and invites a
       * reader to work out why it is there.
       */
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
      // Both rows in one page, so the withholding is proved to be PER ROW rather than per listing.
      // The two carry DIFFERENT account names, because the accessible name is what identifies which
      // row a command belongs to and identical names would make the surviving one unattributable.
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

      /*
       * Nothing is dispatched and nothing is reported: the legacy prompt's cancel branch suppressed the
       * post-back and reported nothing either. `httpMock.verify()` in teardown is the second half of this
       * assertion — an unexpected request would fail there even if this expectation were removed.
       */
      expect(dialog()).toBeNull();
      httpMock.expectNone(userUrl(7));
      expect(notifySpy).not.toHaveBeenCalled();
    });

    it('removes the confirmed account and handles the empty 204 body without error', () => {
      arrive(pageOf([userRow(7)]));

      const removal = confirmRemoval(7);

      /*
       * The endpoint answers with NO BODY, so the observable emits once carrying nothing. Flushing null at
       * 204 is that shape exactly; a fixture flushing an object here would prove a response the API does
       * not send.
       */
      removal.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      answerListing(pageOf([userRow(8)]));
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledWith('success', USER_DELETED_MESSAGE);
    });

    it('re-reads the SAME page after a removal, never the first', () => {
      /*
       * ⚠ THE ASYMMETRY, AND IT IS REAL BEHAVIOUR RATHER THAN AN OVERSIGHT. `grdUsers_DeleteCommand`
       * (`Users.ascx.vb` L646-L669) called `BindData` after removing an account and never touched
       * `CurrentPage`, whereas the search button (L631) and the alphabet strip (`users.ascx` L16, through
       * `FilterURL`'s literal page argument) both reset it. A screen that helpfully returned to the first
       * page here would look tidier and would lose the operator's place mid-way through a long list.
       */
      arrive(pageOf([userRow(1)], 12, 0, TENANT_PAGE_SIZE));

      pressPager(PAGER_NEXT_LABEL);
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
      /*
       * The response carries no body, and splicing the row out here would additionally require adjusting a
       * total the SERVER owns — leaving a pager with two sources of truth.
       *
       * The evidence is threefold, and the middle part is the one a naive case misses: a re-read IS issued;
       * while it is in flight the grid shows its progress indicator rather than a quietly shortened list,
       * which is what "waiting wins over empty" means here; and the row count that finally appears is the
       * server's answer rather than arithmetic performed on this side.
       */
      arrive(pageOf([userRow(7), userRow(8)], 2));

      expect(rows()).toHaveSize(2);

      confirmRemoval(7).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      const reread = expectListRead('the re-read after removal');

      expect(query('app-loading-spinner')).not.toBeNull();

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

      /*
       * `Users.ascx.vb` L657 reported failure with `UserDeleteError`, which the LOCAL resource file does
       * not carry — so the wording is a three-level fall-through to `SharedResources.resx`. The server's
       * own sentence is appended BEHIND that wording rather than replacing it, so the operator sees the
       * sentence they used to see and the detail the server supplied.
       */
      expect(notifySpy).toHaveBeenCalledWith(
        'error',
        `${USER_DELETE_ERROR_MESSAGE} The account could not be removed.`,
        TRACE_ID,
      );
    });

    it('surfaces a refused removal at WARNING severity, not error', () => {
      /*
       * ⚠ MEASURED FROM THE LEGACY SEVERITY VOCABULARY. `Website/admin/Security/AccessDenied.ascx.vb`
       * L41-L47 rendered a denial with `ModuleMessageType.YellowWarning` in BOTH of its branches, and the
       * vocabulary is three-valued across the in-scope screens — `RedError` 27 uses, `YellowWarning` 21,
       * `GreenSuccess` 12. A refusal is not a fault: the caller is known, the request was understood, and
       * the answer is no. The severity is resolved by the shared summariser, so the screen reports at the
       * severity it is GIVEN rather than deciding a second time — which is what stops the two disagreeing.
       */
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(bareProblem('forbidden', 403), {
        status: 403,
        statusText: 'Forbidden',
      });
      fixture.detectChanges();
      settleOutcome();

      /*
       * The document deliberately carries no sentence of its own, so the wording is the shared vocabulary's
       * — which proves the screen appends the SERVER's message rather than composing one, and that the
       * status alone is enough to reach a sentence.
       */
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
      /*
       * Both are diagnostic handles and the correlation identifier is the one an operator can quote to
       * support, because it spans the whole request rather than one server-side trace. The resolver prefers
       * it and falls back to the trace identifier, which is exactly why the case above omits it.
       */
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
      /*
       * Passed SEPARATELY rather than concatenated into the sentence, which is what keeps truncation from
       * ever reaching it. The document below carries a trace identifier and NO correlation identifier on
       * purpose: the shared resolver prefers a correlation identifier when one is present, so including
       * both would have hidden whether the trace identifier survived at all.
       */
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
      /*
       * A failed WRITE is reported transiently and a failed READ is a permanent surface, which is the
       * legacy division: `Users.ascx.vb` L657 raised a module message rather than replacing the grid.
       */
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(problem('server_error', 500, 'Storage is unavailable.'), {
        status: 500,
        statusText: 'Internal Server Error',
      });
      fixture.detectChanges();
      settleOutcome();

      expect(query('app-error-banner')).toBeNull();
      expect(rows()).toHaveSize(1);
    });

    it('does not report a failed re-read as a failed removal', () => {
      /*
       * A SUCCESSFUL removal triggers a re-read, and that re-read can itself fail. The outcome is matched
       * on the OPERATION as well as on the presence of a failure, so the removal is still reported as the
       * success it was and the re-read's failure surfaces as a read failure instead.
       */
      arrive(pageOf([userRow(7)]));

      confirmRemoval(7).flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expectListRead('the re-read after removal').flush(
        problem('server_error', 500, 'Storage is unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();
      settleOutcome();

      expect(notifySpy).toHaveBeenCalledWith('success', USER_DELETED_MESSAGE);
      expect(query('app-error-banner')).not.toBeNull();
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
      designatedAdministrator.set(4242);
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
      /*
       * `Users.ascx.vb` L577 adds the account name, L578 adds the address, and L579-L582 then add one
       * entry per declaration exactly as `GetPropertyDefinitionsByPortal` returned it. `Items.Insert` is
       * never used, so the two account fields LEAD rather than being spliced in afterwards — and the order
       * is load-bearing, because it decides the winner when a tenant declares a property whose name
       * collides with an account field.
       */
      arrive();

      expect(axisValues()).toEqual([
        'Username',
        'Email',
        ODD_PROPERTY_NAME,
        SECOND_PROPERTY_NAME,
      ]);
    });

    it('labels a property the resource file knows, and falls back to the raw name for one it does not', () => {
      /*
       * `AddSearchItem` (L205-L218) resolved each entry through a resource lookup and FELL BACK TO THE RAW
       * NAME when the lookup returned nothing. That fallback is what keeps this axis an OPEN SET: a tenant
       * may declare any property, and one the label map does not know is labelled with its own name rather
       * than rejected.
       */
      arrive();

      expect(axisOptions()).toEqual([
        'Username',
        'Email',
        ODD_PROPERTY_NAME,
        SECOND_PROPERTY_NAME,
      ]);
    });

    it('offers the free-text control with its own placeholder and its own label', () => {
      /*
       * ⚠ THE WORDING "Search:" IS THE SHARED CONTROL'S OWN LABEL AND IS NOT WRITTEN TWICE. `Search.Text` is
       * the wording of `lblSearch` (`users.ascx` L5), and the shared control paints exactly that as its
       * `for`-associated label — so this screen must not render it beside the control, which would put two
       * labels on one field and read the words twice to a screen reader. The placeholder is authored: the
       * legacy text box had none, and it promises neither a starts-with nor a contains test, because the
       * matching rule is the server's to state.
       */
      arrive();

      const field = queryOrFail<HTMLInputElement>(host(), SEARCH_INPUT_SELECTOR);

      expect(field.type).toBe('search');
      expect(field.placeholder).toBe(SEARCH_PLACEHOLDER);
      expect(textOf('label.search-input__label')).toEqual(['Search:']);
    });

    it('names the selector, which the legacy control never was', () => {
      /*
       * `users.ascx` L8 declared `ddlSearchType` with no associated label of any kind and the resource file
       * supplies no key for it, so the legacy selector reached assistive technology unnamed. The wording is
       * authored rather than ported, and the `for`/`id` pair is what makes the association real.
       */
      arrive();

      const label = queryOrFail<HTMLLabelElement>(host(), 'label.form-field__label');

      expect(textIn(label)).toContain(SEARCH_FIELD_CAPTION);
      expect(label.getAttribute('for')).toBe(SEARCH_FIELD_CONTROL_ID);
    });

    it('takes the declarations from the UNPAGED slice, sending no coordinate of any kind', () => {
      create();
      answerSettings();

      const definitions = expectRequest('GET', PROFILE_DEFINITIONS_URL);

      /*
       * ⚠ UNPAGED, AND EVERY COORDINATE IS ABSENT RATHER THAN DEFAULTED. The transport documents that no
       * page coordinate, no ordering and no filter is emitted on this call — not an empty one, not a
       * defaulted one — because the answer is already final.
       *
       * ⚠ DIVERGENCE FROM THIS FILE'S BRIEF, RESOLVED IN FAVOUR OF THE CODE: the brief describes this read
       * as `?portalId=…`. It carries NO tenant parameter, because the API resolves one portal per request
       * from the host reconciled against the alias table, so a portal identifier here would either be
       * redundant or be a second, disagreeing opinion about which tenant the caller meant.
       */
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

    it('searches the account name on the axis the screen opens with', () => {
      /*
       * Seeded to the account name because L577 added that entry FIRST and `AddSearchItem` selected an
       * entry only when it matched a `filterProperty` query-string value, so with no query string the first
       * entry was the selected one.
       */
      arrive();

      typeSearch('blog');

      const searched = expectListRead('the account-name search');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('blog');
      expect(carries(searched, EMAIL_PARAM)).toBeFalse();
      expect(carries(searched, PROFILE_PROPERTY_NAME_PARAM)).toBeFalse();
      expect(carries(searched, PROFILE_PROPERTY_VALUE_PARAM)).toBeFalse();

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('searches the address when that axis is chosen', () => {
      // `Users.ascx.vb` L268-L269 `GetUsersByEmail`.
      arrive();

      chooseAxis('Email');
      typeSearch('jbloggs@');

      const searched = expectListRead('the address search');

      expect(paramOf(searched, EMAIL_PARAM)).toBe('jbloggs@');
      expect(carries(searched, USER_NAME_PARAM)).toBeFalse();
      expect(carries(searched, PROFILE_PROPERTY_NAME_PARAM)).toBeFalse();

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('searches a profile property by name and value when a declared property is chosen', () => {
      // `Users.ascx.vb` L272-L274 `GetUsersByProfileProperty(…, SearchField, SearchText + "%", …)`.
      arrive();

      chooseAxis(ODD_PROPERTY_NAME);
      typeSearch('Bris');

      const searched = expectListRead('the profile-property search');

      expect(paramOf(searched, PROFILE_PROPERTY_NAME_PARAM)).toBe(ODD_PROPERTY_NAME);
      expect(paramOf(searched, PROFILE_PROPERTY_VALUE_PARAM)).toBe('Bris');
      expect(carries(searched, USER_NAME_PARAM)).toBeFalse();
      expect(carries(searched, EMAIL_PARAM)).toBeFalse();

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('transmits the property name VERBATIM — not validated, not case-folded, not restricted', () => {
      /*
       * ⚠ THE FIXTURE NAME IS DELIBERATELY UNUSUAL — mixed case, an underscore, a digit and a space — so
       * that any normalisation at all is visible. The legacy screen passed its field name straight through
       * as the property name, so an unrecognised name is the SERVER's to refuse rather than this screen's
       * to reject.
       */
      arrive();

      chooseAxis(ODD_PROPERTY_NAME);
      typeSearch('anything');

      const searched = expectListRead('the verbatim property search');
      const transmitted: string = paramOf(searched, PROFILE_PROPERTY_NAME_PARAM);

      expect(transmitted).toBe(ODD_PROPERTY_NAME);
      expect(transmitted).not.toBe(ODD_PROPERTY_NAME.toLowerCase());
      expect(transmitted).not.toBe(ODD_PROPERTY_NAME.trim().replace(/\s+/g, ''));

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('keeps every searched value out of the request target', () => {
      // ⚠ CWE-598, PINNED FROM THE SCREEN'S SIDE AS WELL AS THE TRANSPORT'S. The screen's three search
      // axes all name a person: an account name, an email address, and an arbitrary profile-property
      // name paired with the value to match. A request target is written to the browser's history, to
      // every forward and reverse proxy's access log, to the server's access log and to any telemetry
      // that samples URLs — every one of which sits at an END of the encrypted channel, so transport
      // encryption addresses none of it.
      //
      // Asserted here as well as in the transport's own specification because this is the screen an
      // operator actually types into: if the transport ever stopped choosing the body, the failure
      // would be invisible in this file without this case, and every case above reads its values
      // through a helper that is deliberately blind to which address carried them.
      arrive();

      typeSearch('jbloggs');

      const byName = expectListRead('the account-name search');

      expect(byName.request.method).toBe('POST');
      expect(byName.request.urlWithParams)
        .withContext('a searched account name must never reach a request target')
        .not.toContain('jbloggs');
      byName.flush(pageOf([userRow()]));
      fixture.detectChanges();

      chooseAxis('Email');
      typeSearch('jbloggs@example.test');

      const byAddress = expectListRead('the address search');

      expect(byAddress.request.urlWithParams)
        .withContext('a searched address must never reach a request target')
        .not.toContain('jbloggs@example.test');
      byAddress.flush(pageOf([userRow()]));
      fixture.detectChanges();

      chooseAxis(ODD_PROPERTY_NAME);
      typeSearch('Bris');

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

    it('leaves the unfiltered listing on the cacheable GET, because it names nobody', () => {
      // The boundary in the other direction, and it matters: moving a read that identifies nobody
      // into a body would give up caching and idempotence for no privacy gain whatsoever.
      arrive();

      typeSearch('jbloggs');
      expectListRead('the search').flush(pageOf([userRow()]));
      fixture.detectChanges();

      pressLetter(ALL_FILTER_LABEL);

      const unfiltered = expectListRead('the unfiltered read');

      expect(unfiltered.request.method)
        .withContext('page coordinates and an ordering identify nobody')
        .toBe('GET');
      expect(unfiltered.request.url).toBe(USERS_URL);
      unfiltered.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('issues nothing when the axis alone is changed', () => {
      /*
       * PARITY RATHER THAN AN OMISSION. `users.ascx` L8 declared the selector with no auto-post-back, so a
       * new selection had no effect at all until the search control at L9 was used; L586 read
       * `ddlSearchType.SelectedItem.Value` at query time.
       */
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
    function searchFor(term: string): TestRequest {
      typeSearch(term);

      return expectListRead(`the search for ${JSON.stringify(term)}`);
    }

    it('transmits exactly what was typed, appending no wildcard', () => {
      /*
       * ⚠ THE TRAILING WILDCARD IS THE SERVER'S. All three legacy modes appended a single `%` at the call
       * site — `SearchText + "%"` at L269, L271 and L274 — and the target endpoint reproduces that
       * appending, so the match is a STARTS-WITH and the client sends raw text. Appending one here would
       * produce a doubled pattern; leading with one would silently turn a starts-with into a contains.
       */
      const searched = searchFor('Blog');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('Blog');

      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });

    it('sends no per-cent character in any transmitted value', () => {
      // ⚠ THE SWEEP FOLLOWS THE VALUES TO WHEREVER THEY TRAVEL. A search now goes in a body, so a loop
      // over the query parameters alone would find nothing to inspect and pass vacuously — the exact
      // shape of a check that has silently stopped checking. Both are swept, so this case is
      // meaningful whichever address the transport chose.
      const searched = searchFor('Blog');

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

    it('preserves a per-cent character the reader typed, rather than escaping or stripping it', () => {
      /*
       * The complement of the case above, and the one that proves the absence there is the CLIENT declining
       * to add a wildcard rather than the client scrubbing the parameter. A term a person typed is theirs;
       * the server states the matching rule.
       */
      const searched = searchFor('100%');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('100%');

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('does not trim the term', () => {
      // Trimming would make a leading space unsearchable, and a stored name may legitimately carry one.
      const searched = searchFor('  Blog ');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('  Blog ');

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('does not case-fold the term', () => {
      // Case-folding would presume a collation this side does not know; the comparison is the server's.
      const searched = searchFor('BlOgGs');

      expect(paramOf(searched, USER_NAME_PARAM)).toBe('BlOgGs');

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('does not hand-encode the term, leaving any encoding to the transport', () => {
      /*
       * The term is carried DECODED, exactly as the operator typed it. Encoding by hand would double-encode
       * it: the per-cent sign of each escape would itself be escaped, and the server would search for the
       * escape sequence rather than for the text. The absence of `%25` anywhere is what rules that out.
       *
       * ⚠ THE TERM NOW TRAVELS IN A BODY, SO THERE IS NO URL ENCODING TO GET WRONG AT ALL — a JSON string
       * member carries the characters verbatim. This case previously asserted that the serialised target
       * contained `userName=`, which is exactly what must no longer be true: a searched account name names
       * a person and a request target is recorded by the browser, by every proxy and by the server
       * (CWE-598). The assertion is inverted rather than deleted, because the property worth pinning is
       * still that nothing re-encodes the operator's text on the way out — it has simply moved.
       *
       * The four characters chosen are the ones that would have been escaped in a query string and that
       * would separate or terminate a parameter if they were not: a space, an ampersand, an equals sign.
       */
      const searched = searchFor('a b&c=d');

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

    it('searches for a term the legacy screen could never search for', () => {
      /*
       * MIGRATION: the legacy branch was chosen by comparing the search TEXT against localised words — L258
       * `Unauthorized`, L261 `OnLine`, L264 `All` — so which query an operator got depended on the language
       * the page had been rendered in, and none of those three words could be searched for at all even
       * though each is an ordinary thing to type. The branch is a typed discriminator here, so every one of
       * them is searchable like any other text.
       */
      const searched = searchFor(ALL_FILTER_LABEL);

      expect(paramOf(searched, USER_NAME_PARAM)).toBe(ALL_FILTER_LABEL);

      searched.flush(pageOf([]));
      fixture.detectChanges();
    });

    it('searches for the empty term as a real value rather than dropping the filter', () => {
      /*
       * Empty text is a LEGITIMATE value on this contract, and the omission rule treats only `undefined`
       * and `null` as absent. Clearing the box and pressing search is therefore a search for the empty
       * prefix, which is a request the server answers — not a silent reversion to the unfiltered listing.
       */
      typeSearch('present');
      expectListRead('the first search').flush(pageOf([userRow()]));
      fixture.detectChanges();

      typeSearch('');

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
    it('never transmits the literal "None"', () => {
      /*
       * `Users.ascx.vb` L266 read `ElseIf SearchText <> "None"`, so the marker meant DO NOT QUERY AT ALL —
       * every branch fell through and the grid was left unbound. The successor state issues no request
       * either, which is why the assertion below is about a request that does not exist rather than about a
       * parameter that does.
       *
       * The state is reached by searching for the word, which on this screen is an ORDINARY TERM: the axis
       * is a typed discriminator, so no reserved word is ever compared against user text.
       */
      arrive();

      typeSearch('None');

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

    it('issues a paged, unfiltered request for the "All" affordance, carrying no search parameter', () => {
      /*
       * `Users.ascx.vb` L264-L265 called the unfiltered PAGED reader, which is a DISTINCT FOURTH BRANCH and
       * not the same as the `"None"` fall-through: it really does ask the server for everything.
       */
      arrive();

      typeSearch('narrowed');
      expectListRead('the narrowing search').flush(pageOf([userRow()]));
      fixture.detectChanges();

      pressLetter(ALL_FILTER_LABEL);

      const unfiltered = expectListRead('the unfiltered read');

      /*
       * ⚠ ABSENCE IS PROVED WITH `has`, NEVER WITH `get`. A `get` returning null is a DIFFERENT assertion
       * and passes just as well for a parameter that is present and empty — which is exactly the state the
       * case above establishes is meaningful on this contract.
       */
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
      /*
       * MIGRATION, AND A DOCUMENTED DIVERGENCE THE STORE OWNS. `Page_Init` L494-L506 chose the opening view
       * from the tenant's `Display_Mode` setting, and `UserModuleBase.vb` L126-L130 defaulted it to
       * `DisplayMode.None` — so a tenant that had configured nothing opened this screen with NO QUERY
       * ISSUED and NO ROWS at all until the operator acted. The no-query state is promoted to the
       * unfiltered listing when a listing screen comes up, which is why arrival issues a listing read at
       * all, and the assertion below is that promotion.
       */
      const listing = arrive();

      for (const name of SEARCH_PARAMS) {
        expect(carries(listing, name)).toBeFalse();
      }

      expect(carries(listing, GENERIC_QUERY_PARAM)).toBeFalse();
      expect(rows()).toHaveSize(1);
    });

    it('transmits no reserved word of its own, in any parameter', () => {
      /*
       * ⚠ THE DIRECT FORM OF "THE MARKER IS NEVER TRANSMITTED". The four legacy words were CONTROL VALUES
       * that travelled in the same channel as a search term — `Users.ascx.vb` L258, L261 and L264 compared
       * the search text against localised lookups and L266 against the bare marker `"None"` — so a screen
       * that carried the mode as text would send one of them. Here the mode is the store's own command
       * surface and no reserved word reaches the wire from the screen at all: the only way any of these
       * strings can appear is if a reader typed it, which the case above proves is an ordinary search.
       */
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
      /*
       * ⚠ NOT AN OMISSION, AND NOT THE SAME QUESTION AS THE SENTINEL RULE. The legacy screen passed
       * `UsersPortalId` into every reader; the target resolves ONE portal per request from the host
       * reconciled against the alias table, before it dispatches to a controller — so a tenant identifier
       * in a path or a query string here would either be redundant or be a second, disagreeing opinion
       * about which tenant the caller meant.
       *
       * The sentinel rule still applies to the value ITSELF, which arrives on every row and must survive
       * being minus one; that is asserted with the identity values.
       */
      const listing = arrive(pageOf([userRow(7, { portalId: -1 })]));

      expect(carries(listing, 'portalId')).toBeFalse();
      expect(listing.request.urlWithParams).not.toContain('portalId');
      expect(listing.request.url).toBe(USERS_URL);
    });

    it('sends no approval restriction, because the legacy listing showed both states', () => {
      /*
       * The unauthorised-only affordance the legacy strip offered (L309-L310) is NOT reproduced — it was
       * answered from an unpaged reader that took no page coordinate and no endpoint serves it — so the
       * listing never restricts on approval and the column reports the state instead.
       */
      const listing = arrive();

      expect(carries(listing, 'isApproved')).toBeFalse();
    });

    it('ANNOUNCES the applied entry, which the legacy strip never did', () => {
      /*
       * ⚠ THIS CLOSES A GAP THE TEMPLATE USED TO REPORT RATHER THAN FIX. The markup carried a note
       * saying the applied entry could not be announced because "the letter in force lives inside the
       * store's search discriminator and is not re-published on the screen's surface", and declined to
       * emit `aria-pressed`. That described a missing predicate on the paired class, not a limit of
       * anything: the store publishes its search and the sibling portal listing already derives exactly
       * this state from its own equivalent.
       *
       * Every legacy entry rendered identically whatever was applied (`users.ascx` L16), so an operator
       * could not tell from the strip which letter they were looking at, and a reader was handed
       * twenty-seven controls with no indication that one of them was in force.
       */
      arrive();

      // On arrival the unfiltered listing is what the tenant policy asked for, so its entry is the
      // pressed one and the twenty-six letters are not.
      expect(pressedAffordances())
        .withContext('exactly one entry is ever pressed')
        .toEqual([ALL_FILTER_LABEL]);

      pressLetter('C');
      expectListRead('the letter search').flush(pageOf([userRow(1)]));
      fixture.detectChanges();

      expect(pressedAffordances()).toEqual(['C']);
    });

    it('emits aria-pressed on EVERY entry, so the attribute is a state and not a marker', () => {
      // ⚠ THE ATTRIBUTE'S VALUE IS THE STATE, AND ITS PRESENCE IS NOT. Emitting it only on the applied
      // entry would make "not pressed" indistinguishable from "not a toggle" for a reader, and would
      // let a presence-based stylesheet selector paint the whole strip as applied.
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

    it('shows NO entry as applied when the search is on an axis the strip does not offer', () => {
      // An electronic-mail prefix is a real search that no strip entry describes, so the truthful
      // answer is that none of them is pressed — including the unfiltered entry, which is emphatically
      // not what is in force.
      arrive();

      chooseAxis('Email');
      typeSearch('a@example.test');
      expectListRead('the address search').flush(pageOf([userRow(1)]));
      fixture.detectChanges();

      expect(pressedAffordances()).toEqual([]);
    });

    it('matches a letter case-INSENSITIVELY, so the strip agrees with the listing it describes', () => {
      // The strip renders upper case while the free-text field admits any case, and both land in an
      // identical search. A case-sensitive comparison would leave the strip claiming nothing was
      // applied while the grid showed a letter-filtered listing.
      arrive();

      typeSearch('c');
      expectListRead('the lower-case letter search').flush(pageOf([userRow(1)]));
      fixture.detectChanges();

      expect(pressedAffordances()).toEqual(['C']);
    });

    it('shows no entry as applied for a MULTI-CHARACTER prefix, which no letter describes', () => {
      // "Ca" is a sign-in prefix search, but it is not the letter "C": pressing "C" would change the
      // listing, so reporting "C" as applied would be a lie about what the grid is showing.
      arrive();

      typeSearch('Ca');
      expectListRead('the two-character search').flush(pageOf([userRow(1)]));
      fixture.detectChanges();

      expect(pressedAffordances()).toEqual([]);
    });

    it('offers twenty-six letters and the unfiltered affordance, and nothing else', () => {
      /*
       * `CreateLetterSearch` (L304-L316) read a pure 26-letter resource value — no "All" entry, no "0-9"
       * entry, no punctuation beyond the separators — and then APPENDED the unfiltered entry at L308. L309
       * and L310 appended a signed-in entry and an unauthorised entry as well, and NEITHER is reproduced:
       * both were answered from unpaged readers, one of them from session tracking and a scheduled purge
       * this migration does not carry forward. Two documented functional reductions.
       */
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

    it('treats a letter as a prefix search on the axis currently chosen, not as a query of its own', () => {
      /*
       * `Users.ascx.vb` L586 passed the filter and `ddlSearchType.SelectedItem.Value` into the SAME
       * `BindData` the search button used, so pressing "A" with the address axis chosen listed accounts
       * whose ADDRESS began with A.
       */
      arrive();

      chooseAxis('Email');
      pressLetter('A');

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
      /*
       * ⚠ DEFENSIVE, AND THE NUANCE MATTERS. `dbo.Users.UserID` is declared `IDENTITY (1, 1)`
       * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L98), so an account
       * keyed nought DOES NOT OCCUR NATURALLY — this is a test of the SENTINEL RULE, not a schema fact.
       *
       * It is worth testing anyway because the rule it protects is a fact: `dbo.Roles.RoleID` is
       * `IDENTITY (0, 1)` and `dbo.Portals.PortalID` is `IDENTITY (-1, 1)`, so nought and minus one are
       * both real keys SOMEWHERE in this schema, while `Library/Components/Shared/Null.vb` L41-L45 defines
       * minus one as the marker for a missing integer. One vocabulary cannot carry both meanings, so no
       * identifier anywhere in this screen may be tested for truthiness or for positivity — and an
       * account keyed nought is the cheapest way to catch a screen that started doing so.
       */
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

      expect(notifySpy).toHaveBeenCalledWith('success', USER_DELETED_MESSAGE);
    });

    it('retains a tenant identifier of minus one on the row it renders', () => {
      /*
       * ⚠ THIS ONE IS A SCHEMA FACT RATHER THAN A DEFENSIVE CASE. `dbo.Portals.PortalID` is
       * `IDENTITY (-1, 1)` at L77, so minus one is the FIRST REAL PORTAL — and it is simultaneously
       * `Null.NullInteger`. A row carrying it must render exactly like any other; eliding it, normalising
       * it or treating it as "no tenant" would discard the tenant that ships with the product.
       */
      arrive(pageOf([userRow(7, { portalId: -1 })]));

      expect(rows()).toHaveSize(1);
      expect(rowAction(EDIT_COMMAND_LABEL).getAttribute('href')).toBe('/users/7');
    });

    it('renders a row whose every nullable member is absent, without a placeholder word', () => {
      /*
       * `Null.NullString` is the EMPTY STRING rather than a null reference (`Null.vb` L71-L75), so a stored
       * empty value and an absent one were indistinguishable once read by the legacy screen. They stay
       * distinct on the wire here and are rendered identically, which is the legacy outcome — and neither
       * ever renders as the word "null" or "undefined", which is what an unguarded interpolation produces.
       */
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
      expect(textIn(cellUnder(ADDRESS_HEADING))).toBe('');
      expect(textIn(cellUnder(TELEPHONE_HEADING))).toBe('');
    });
  });

  // ===================================================================================================
  // §3.9 — THE MINIMUM-VALUE DATE RENDERS BLANK, AND THAT IS PARITY
  // ===================================================================================================

  describe('the sentinel date', () => {
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

    it('renders the sentinel instant as nothing at all', () => {
      /*
       * ⚠ PARITY, NOT A DIVERGENCE, AND IT MUST NOT BE REPORTED AS ONE. `DisplayDate`
       * (`Users.ascx.vb` L396-L408) seeded its result with `Null.NullString` and returned `""` when
       * `Null.IsNull` recognised the instant, so the legacy cell was ALREADY BLANK. Rendering
       * `01/01/0001` would be the change in behaviour.
       */
      const [created, lastLogin] = dateCells('0001-01-01T00:00:00Z', '0001-01-01T00:00:00Z');

      expect(created).toBe('');
      expect(lastLogin).toBe('');
      expect(created).not.toContain('0001');
      expect(lastLogin).not.toContain('0001');
    });

    it('renders the sentinel DATE with a non-zero time component as nothing either', () => {
      /*
       * ⚠ THE CASE A NAIVE FULL-TIMESTAMP COMPARISON FAILS, AND THE ONE MOST LIKELY TO BE LEFT OUT.
       * Detection is DATE-PART ONLY: `Null.vb` compares `objDate.Date.Equals(NullDate.Date)`, and its own
       * comment gives the reason — "this avoids subtle time differences". An equality check against the
       * whole minimum-value instant would pass the case above and render a confident, alarming
       * `01/01/0001 13:45` here.
       */
      const [created, lastLogin] = dateCells('0001-01-01T13:45:30Z', '0001-01-01T23:59:59Z');

      expect(created).toBe('');
      expect(lastLogin).toBe('');
      expect(created).not.toContain('0001');
      expect(lastLogin).not.toContain('0001');
    });

    it('renders an absent instant identically to the sentinel', () => {
      const [created, lastLogin] = dateCells(null, null);

      expect(created).toBe('');
      expect(lastLogin).toBe('');
    });

    it('refuses an unparseable instant at the boundary rather than rendering one', () => {
      /*
       * ⚠ DIVERGENCE FROM THIS FILE'S BRIEF, RESOLVED IN FAVOUR OF THE CODE, AND REPORTED. The brief asks
       * for "an unparseable value renders the same empty string". It cannot reach a cell on this screen at
       * all: the listing decoder validates each instant and refuses a value the platform cannot parse,
       * which fails the WHOLE page rather than one cell. The honest — and stronger — assertion is therefore
       * that no fabricated date is ever displayed: the read fails, no row is painted, and the offending
       * text reaches no cell.
       *
       * ⚠ AND A SECOND FINDING, REPORTED RATHER THAN WORKED AROUND: a CONTRACT VIOLATION carries no problem
       * document, because there is no response to take one from — so the store records a failure whose
       * document is null and the shared banner, bound to a null problem, emits nothing at all. A reader
       * therefore sees the empty state rather than an explanation. That is the store's and the banner's
       * behaviour, both outside this file, and this case pins the observable outcome truthfully instead of
       * asserting a surface that does not appear.
       *
       * The pipe's own handling of an unparseable value is real and is covered by the pipe's specification;
       * this case is about the boundary in front of it.
       */
      create();
      answerSettings();
      answerDefinitions();

      expectListRead('the listing read').flush(
        pageOf([userRow(7, { createdDate: 'not-a-date' })]),
      );
      fixture.detectChanges();

      expect(rows()).toHaveSize(0);
      expect(host().textContent ?? '').not.toContain('not-a-date');
      expect(query('app-empty-state')).not.toBeNull();
    });

    it('renders a real instant, and renders it with its time as well as its date', () => {
      /*
       * `DisplayDate` (L400) rendered the instant with the plain general format — a short date AND a long
       * time — whereas the shared pipe defaults to a short date alone, so both cells ask for the
       * date-and-time shape BY NAME. A cell showing only a date would mean that request was dropped.
       */
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
      /*
       * THREE SEPARATE COLUMNS rather than one column of three controls, because that is what
       * `users.ascx` L32, L33 and L34 declared: three distinct `dnn:imagecommandcolumn` elements.
       */
      arrive();

      expect(queryAll(ACTION_CELL_SELECTOR)).toHaveSize(3);
      expect(textOf(ROW_ACTION_SELECTOR)).toEqual([
        EDIT_COMMAND_LABEL,
        DELETE_COMMAND_LABEL,
        MANAGE_ROLES_COMMAND_LABEL,
      ]);
    });

    it('links edit to the account editor keyed by the account', () => {
      /*
       * Replaces `Users.ascx.vb` L530, which built the address with a dummy token and then substituted a
       * format placeholder into the RENDERED URL.
       *
       * The address is asserted as the router RESOLVED it, from a segment array rather than from a
       * concatenated string — which is why it carries no query string at all. The legacy screen assembled
       * `filter`, `filterproperty` and `currentpage` by hand into every one of its addresses (L164-L188), and
       * a screen still doing that would show them here.
       */
      arrive(pageOf([userRow(42)]));

      const href: string | null = rowAction(EDIT_COMMAND_LABEL).getAttribute('href');

      expect(href).toBe('/users/42');
      expect(href === null ? '' : href).not.toContain('?');
    });

    it('CARRIES THE ACCOUNT to the role listing, as a query parameter and not a path segment', () => {
      /*
       * ⚠ THE ACCOUNT MUST NOT BE DROPPED. This case previously asserted the opposite — a bare `/roles`
       * with the row discarded — and recorded it as a deliberate reduction. It was not acceptable: an
       * operator pressing "Manage Roles" on one person arrived at every role in the tenant, with the
       * account they had chosen nowhere on screen and nothing to narrow by. `Users.ascx.vb` L542 built
       * `NavigateURL(TabId, "User Roles", "UserId=KEYFIELD", …)`, and the screen it reached served TWO
       * MODES from one page keyed by either a role or an account (`SecurityRoles.ascx.vb` L413-L418).
       *
       * ⚠ A QUERY PARAMETER, AND THE "NO PER-ACCOUNT SEGMENT" HALF OF THE OLD CLAIM STILL HOLDS. The
       * target's route set is closed and contains no per-account membership address, so the account
       * travels on an address that already exists rather than on a new one. That is also how the legacy
       * carried it: `UserId=KEYFIELD` was a query argument, not a distinct page.
       *
       * ⚠ THE ADDRESS IS STILL ASSERTED AS A STRING, AND THIS FILE STILL IMPORTS NOTHING FROM THE ROLE
       * FEATURE. An import would couple two features through their specifications.
       */
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
      /*
       * MIGRATION: all three legacy commands were UNLABELLED IMAGES, so the delete command reached
       * assistive technology as an unnamed control that destroyed a record. Each name carries the account
       * and CONTAINS the visible word, which is what keeps a spoken command matching what is seen.
       */
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
      /*
       * REMOVAL, NOT CONCEALMENT, and an AFFORDANCE ONLY — the server re-authorises every request and
       * answers 403, and its verdict is the only authority. What withholding buys is that the operator
       * is not offered two screens the router will refuse and a command the API will decline.
       *
       * The roles command is NOT gated, because reaching the role listing is not itself a mutation and
       * that route carries authentication alone.
       */
      administersPortal.set(false);
      arrive();

      expect(textOf(ROW_ACTION_SELECTOR)).toEqual([MANAGE_ROLES_COMMAND_LABEL]);
      expect(textOf('a.user-list__page-action')).toEqual([]);
    });

    it('names the superseded permission key nowhere in the rendered screen', () => {
      // ⚠ THE VOCABULARY REGRESSION GUARD. The three header actions and the two mutating row commands
      // were gated on the persisted `EDIT` key, which answers a different question — a grant over a
      // module or page instance — from the one every destination here actually asks. Asserted against
      // the rendered markup so a directive quietly reinstated on any of the five fails by name.
      arrive();

      expect(host().innerHTML).not.toContain(EDIT_PERMISSION);
      expect(host().innerHTML).not.toContain('hasPermission');
    });

    it('offers the mutating affordances to an administrator holding NO persisted key', () => {
      /*
       * ⚠ THE OTHER HALF OF THE VOCABULARY SEPARATION, AND THE HALF THAT WAS A LOCKOUT. The client's
       * key list is derived from GRANT ROWS ALONE, so a tenant administrator who has never been named
       * in one holds no keys whatsoever — measured on the seeded baseline, the administrator account
       * holds the designated administrator role and ZERO portal-level permission keys. The API admits
       * that operator to create, update and delete (`UsersController.cs:L451`, `:L493`, `:L541`),
       * because each declares the administration policy; the old key gate removed all three controls
       * from the very operator this screen exists for.
       */
      administersPortal.set(true);
      arrive();

      expect(textOf('a.user-list__page-action')).toContain(ADD_USER_LABEL);
      expect(textOf(ROW_ACTION_SELECTOR)).toContain(EDIT_COMMAND_LABEL);
      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);
    });

    it('follows a change of administration without being recreated', () => {
      // The verdict can change within one page load — a renewal re-reads the caller's authority, and an
      // administrator can be demoted — and this screen is not rebuilt for it. The gate is read from a
      // signal and the component renders on-push, which is what makes the change observable.
      arrive();

      expect(textOf('a.user-list__page-action')).toContain(ADD_USER_LABEL);

      administersPortal.set(false);
      fixture.detectChanges();

      expect(textOf('a.user-list__page-action')).not.toContain(ADD_USER_LABEL);
      expect(textOf(ROW_ACTION_SELECTOR)).toEqual([MANAGE_ROLES_COMMAND_LABEL]);
    });

    it('disables the delete command while a removal is already in flight', () => {
      // The legacy screen post-backed, so the reader could not press twice.
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

  // ===================================================================================================
  // §3.10a — THE TWO PROTECTED ACCOUNTS
  // ===================================================================================================
  //
  // MIGRATION: `grdUsers_ItemDataBound` (`Website/admin/Users/Users.ascx.vb` L693-L694) hid the delete
  // image on exactly two conditions, joined with `AndAlso`:
  //
  //   delImage.Visible = Not (user.UserID = PortalSettings.AdministratorId) AndAlso _
  //                      Not (user.UserID = Me.UserId And user.IsSuperUser)
  //
  // The first protects the tenant's DESIGNATED ADMINISTRATOR — removing it would leave
  // `Portals.AdministratorId` naming an account that no longer exists. The second stops a signed-in
  // HOST account deleting ITSELF, and both halves of that clause are load-bearing: one host account
  // may legitimately remove another, and an ordinary account removing itself was never guarded here.
  //
  // ⚠ WITHHOLDING IS NOT INTERCHANGEABLE WITH A SERVER REFUSAL. A refusal arrives only after the
  // operator has confirmed a deletion and waited, and it arrives on the two accounts where a mistaken
  // attempt is most alarming.
  // ===================================================================================================

  describe('the two protected accounts', () => {
    it('asks for the tenant\u2019s protected facts on arrival, for the caller\u2019s OWN tenant', () => {
      // Read from the identity rather than from a route, because this screen names no portal segment.
      arrive();

      expect(loadCurrentPortalContext).toHaveBeenCalledOnceWith(-1);
    });

    it('asks for nothing at all while the caller\u2019s identity is unresolved', () => {
      // The identity is fetched, so it is null for a window after the screen mounts. Asking with no
      // tenant in hand would either fault or ask for the wrong one.
      callerIdentity.set(null);
      arrive();

      expect(loadCurrentPortalContext).not.toHaveBeenCalled();
    });

    it('withholds removal from the tenant\u2019s designated administrator', () => {
      designatedAdministrator.set(7);
      arrive(pageOf([userRow(7)]));

      expect(textOf(ROW_ACTION_SELECTOR)).toEqual([EDIT_COMMAND_LABEL, MANAGE_ROLES_COMMAND_LABEL]);
      expect(textOf(ROW_ACTION_SELECTOR)).not.toContain(DELETE_COMMAND_LABEL);
    });

    it('keeps removal on every OTHER account in the same page', () => {
      // The guard is per row, so protecting one account must not disarm the column.
      designatedAdministrator.set(7);
      arrive(pageOf([userRow(7), userRow(8, { username: 'asmith' })]));

      const names: readonly (string | null)[] = queryAll<HTMLElement>(ROW_ACTION_SELECTOR).map(
        (action) => action.getAttribute('aria-label'),
      );

      expect(names).not.toContain(`${DELETE_COMMAND_LABEL} jbloggs`);
      expect(names).toContain(`${DELETE_COMMAND_LABEL} asmith`);
    });

    it('protects a designated administrator whose key is ZERO, which is not an absence', () => {
      // ⚠ SENTINEL DISCIPLINE. The guard compares with explicit equality against a resolved key: a
      // truthiness test would read a legitimate key of zero as "no designation" and expose the one
      // account the legacy screen most carefully protected.
      designatedAdministrator.set(0);
      arrive(pageOf([userRow(0)]));

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
      // ⚠ BOTH HALVES OF THE SECOND CLAUSE ARE REQUIRED. The legacy condition guarded self-removal
      // only for an installation administrator, so withholding it from an ordinary account would be a
      // capability this migration invented rather than preserved.
      callerIdentity.set(callerAccount({ userId: 7, isSuperUser: false }));
      arrive(pageOf([userRow(7, { isSuperUser: false })]));

      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);
    });

    it('withholds nothing while the tenant\u2019s facts are still unresolved', () => {
      // ⚠ THE FAIL-SAFE DIRECTION, AND IT IS THE OPPOSITE OF THE USUAL ONE. Until the tenant record
      // has been read the designation is null, so the first clause protects nobody and behaviour is
      // exactly what it was before the guard existed: the command is offered and the API's refusal
      // governs. Hiding it until the read completed would strip a capability from every row for the
      // duration of a request.
      designatedAdministrator.set(null);
      arrive(pageOf([userRow(7)]));

      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);
    });

    it('arms and disarms the guard as the facts arrive, without remounting', () => {
      arrive(pageOf([userRow(7)]));

      expect(textOf(ROW_ACTION_SELECTOR)).toContain(DELETE_COMMAND_LABEL);

      designatedAdministrator.set(7);
      fixture.detectChanges();

      expect(textOf(ROW_ACTION_SELECTOR)).not.toContain(DELETE_COMMAND_LABEL);
    });

    it('withholds removal from a protected row even from an administrator', () => {
      // The two protections are about the RECORD, not about the caller's authority: an administrator
      // is exactly who reaches this screen, and the legacy guard applied to them too.
      administersPortal.set(true);
      designatedAdministrator.set(7);
      arrive(pageOf([userRow(7)]));

      expect(textOf(ROW_ACTION_SELECTOR)).not.toContain(DELETE_COMMAND_LABEL);
      expect(textOf(ROW_ACTION_SELECTOR)).toContain(EDIT_COMMAND_LABEL);
    });
  });

  // ===================================================================================================
  // §3.11 — COLUMNS, WORDING AND THE DROPPED COLUMN
  // ===================================================================================================

  describe('the column set', () => {
    it('paints the ten data headings in the legacy order, using the RESOURCE wording', () => {
      /*
       * ⚠ FIVE OF THESE CONTRADICT THE MARKUP, AND THE RESOURCE FILE WINS. `Users.ascx.vb` L585 ran
       * `Localization.LocalizeDataGrid`, which rewrote every heading from
       * `GetString(HeaderText & ".Header", ResourceFile)` at run time — so the markup's `FirstName`,
       * `LastName`, `DisplayName`, `CreatedDate` and `LastLogin` render as "First Name", "Last Name",
       * "Name", "Created Date" and "Last Login". Taking the markup attribute would have produced five
       * wrong headings that no compiler could have caught.
       */
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
      /*
       * The legacy grid supplied NO heading text for any of its three command columns, so painting one
       * would be an addition; keeping the label in the accessibility tree means a command cell is still
       * read out with its column name, which closes a real gap at no visual cost.
       */
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
      /*
       * ⚠ UNCONDITIONAL, AND DELIBERATELY NOT GATED. `Page_Init` L510-L511 made a column visible WITHOUT
       * consulting any setting when its heading was empty or lower-cased to `username`, and the settings
       * screen declares nine `Column_*` keys with no `Column_Username` among them.
       */
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

    it('hides one optional column when the tenant switches exactly that one off', () => {
      /*
       * Every flag is compared EXPLICITLY against true, because false is DATA on this contract rather than
       * an absence — the legacy absent-Boolean marker was itself `False` (`Null.vb` L76-L80), so in the
       * legacy model a switched-off column and an unset one were indistinguishable, whereas here the wire
       * value means what it says.
       */
      arrive(pageOf([userRow()]), membershipSettings({ columnTelephone: false }));

      const headings: readonly string[] = textOf(HEADER_SELECTOR);

      expect(headings).not.toContain(TELEPHONE_HEADING);
      expect(headings).toContain(ADDRESS_HEADING);
      expect(headings).toContain(EMAIL_HEADING);
    });

    it('does not reproduce the users-online column', () => {
      /*
       * `users.ascx` L35-L39 declared an unlabelled template column holding a single
       * `~/images/userOnline.gif` image whose visibility came from L702. It carries no heading, no
       * alternative text and no information a heading could announce, and users-online is out of scope with
       * no endpoint serving it. A documented functional reduction — the row contract DOES carry a signed-in
       * flag, so this is a scope decision rather than a data limitation, which is why the case asserts the
       * column's absence rather than the datum's.
       */
      arrive(pageOf([userRow(7, { isOnline: true })]));

      // Fourteen legacy columns resolve to thirteen: three commands plus ten data columns.
      expect(queryAll(HEADER_SELECTOR)).toHaveSize(13);
      expect(queryAll('img')).toHaveSize(0);
    });

    it('applies no zebra striping', () => {
      /*
       * `users.ascx` L25-L26 gave the item style and the ALTERNATING item style the SAME class, so alternate
       * rows were never tinted, and L23 set `GridLines="None"` so no cell carried a rule. Every painted row
       * therefore carries an identical class list.
       */
      arrive(pageOf([userRow(1), userRow(2), userRow(3)], 3));

      const classLists: readonly string[] = rows().map((row) =>
        Array.from(row.classList).sort().join(' '),
      );

      expect(classLists).toHaveSize(3);
      expect(new Set(classLists).size).toBe(1);
    });

    it('renders the address exactly as the server composed it', () => {
      /*
       * ⚠ DIVERGENCE FROM THIS FILE'S BRIEF, RESOLVED IN FAVOUR OF THE CODE, AND REPORTED. The brief asks
       * for a client-side join of six trimmed profile parts. THE COMPOSITION IS THE SERVER'S: `users.ascx`
       * L44-L49 composed the six values through `Globals.FormatAddress`, but those six are profile VALUES
       * that the 02.02.01 upgrade script moved off the account table, so composing them client-side would
       * require fetching a profile per row. The row contract carries the finished text and this screen
       * renders it unchanged.
       *
       * The assertion is therefore the stronger one for the code as it stands: the six-part text arrives in
       * the legacy order — unit, street, city, region, country, postal code — and is painted VERBATIM, with
       * nothing re-ordered, re-joined, trimmed or truncated.
       */
      const composed = 'Flat 2, 14 High Street, Bristol, Avon, United Kingdom, BS1 4TR';

      arrive(pageOf([userRow(7, { address: composed })]));

      expect(textIn(cellUnder(ADDRESS_HEADING))).toBe(composed);
    });

    it('renders a partly composed address without a stray separator', () => {
      /*
       * `Globals.FormatAddress` appended each NON-BLANK part behind a comma and space and then stripped the
       * leading separator, so a partial address never carried a dangling comma. The client must not add one
       * either — no padding, no placeholder for a missing part.
       */
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
      /*
       * MIGRATION: this is `HtmlUtils.FormatEmail` (`Library/Components/Shared/HtmlUtils.vb` L89-L102)
       * expressed as DATA rather than as markup. The legacy helper concatenated an anchor around the stored
       * value and returned it as a string a label control then emitted, which is a script-injection vector
       * for any address containing markup. Here the address and the target travel separately, the target is
       * bound through `[href]` so the framework's URL sanitiser sees it, and the address is interpolated as
       * text and therefore escaped.
       */
      arrive(pageOf([userRow(7, { email: 'jbloggs@example.test' })]));

      const link = queryOrFail<HTMLAnchorElement>(cellUnder(EMAIL_HEADING), EMAIL_LINK_SELECTOR);

      expect(link.getAttribute('href')).toBe('mailto:jbloggs@example.test');
      expect(textIn(link)).toBe('jbloggs@example.test');
    });

    it('does not link a stored value that carries no mailbox separator', () => {
      /*
       * `HtmlUtils.vb` L94 tested for the separator and L97 returned the value UNCHANGED when it was absent,
       * so a stored value that is not an address renders as plain text in the legacy screen and in this one.
       */
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

    it('never renders the telephone number as a mailto link, even one containing an at-sign', () => {
      /*
       * ⚠ DEFECT D1, CORRECTED AND ANNOTATED RATHER THAN SILENTLY FIXED. The legacy cell applied
       * `DisplayEmail` to the TELEPHONE NUMBER — the electronic-mail formatter to a value that is not an
       * address (`users.ascx` L50-L55). The consequence was LATENT rather than visible: the helper emitted
       * an anchor only when the value carried a mailbox separator, so a well-formed number passed through
       * unchanged and the grid looked correct. A number carrying an at-sign — a stored extension note, say —
       * WOULD have been wrapped in a `mailto:` link pointing at a telephone number. The pathological value
       * is used here precisely because it is the one the legacy screen would have got wrong.
       */
      arrive(pageOf([userRow(7, { telephone: '0117 496 0000 x@204' })]));

      const cell = cellUnder(TELEPHONE_HEADING);

      expect(cell.querySelector('a')).toBeNull();
      expect(textIn(cell)).toBe('0117 496 0000 x@204');
    });

    it('renders the authorisation flag as an announced word', () => {
      /*
       * MIGRATION — THE OPTION-STRICT COERCION IS MADE EXPLICIT. The legacy markup drew one of two images by
       * evaluating `Membership.Approved=true` and `Membership.Approved=false`, comparing a strongly-typed
       * Boolean against UNQUOTED Boolean literals; it compiled only because the administration pages were
       * built with strict type checking switched off (`Website/release.config` L125). The flag is a Boolean
       * here and both states render as words instead of as one of a pair of untitled images.
       */
      arrive(pageOf([userRow(7, { isApproved: true })]));

      expect(textIn(cellUnder(AUTHORIZED_HEADING))).toBe(AFFIRMATIVE_TEXT);
    });

    it('renders an unauthorised account as "No" rather than as an empty cell', () => {
      /*
       * ⚠ FALSE IS DATA, NOT "UNSET". The legacy absent-Boolean marker was itself `False` (`Null.vb`
       * L76-L80), so a truthiness test would render an unauthorised account as a blank cell and an operator
       * could not tell "not authorised" from "not known".
       */
      arrive(pageOf([userRow(7, { isApproved: false })]));

      const painted: string = textIn(cellUnder(AUTHORIZED_HEADING));

      expect(painted).toBe(NEGATIVE_TEXT);
      expect(painted).not.toBe('');
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
    /*
     * MIGRATION: `Page_Init` L494-L506 chose the screen's opening filter from `Display_Mode`, and
     * `BindData` L248-L290 branched on that filter: the localised "All" word listed everything
     * (L264), any other non-"None" value fell through to the search-axis switch (L267) whose default
     * axis was `Username` (L577), and the bare marker "None" matched no branch at all so no query
     * was issued and the grid rendered unbound.
     *
     * ⚠ AN EARLIER REVISION IGNORED THE SETTING and always opened on the unfiltered listing. These
     * cases close that gap from the screen's side; the store's own suite pins the query each mode
     * dispatches.
     */

    it('opens on the first letter of the alphabet strip when the tenant chose that view', () => {
      create();
      answerSettings(membershipSettings({ displayMode: 1 }));
      answerDefinitions();

      // ⚠ EITHER TRANSPORT, READ THROUGH THE SHARED ACCESSOR. A first-letter view is an
      // account-name filter, and an account name identifies a person, so it travels in a request
      // BODY rather than in a request target — see {@link USERS_SEARCH_URL}. The accessor answers
      // from whichever of the two the screen used, so this case asserts the FILTER rather than the
      // transport, which is what it was always about.
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
      /*
       * ⚠ THE NOTICE EXISTS BECAUSE THE SHARED GRID'S EMPTY STATE WOULD STATE A FALSEHOOD HERE. That
       * state reports that nothing was FOUND; in this state nothing was LOOKED FOR. The legacy showed
       * no message at all, and since `UserModuleBase.vb` L126-L130 defaulted the setting to this
       * mode, every unconfigured tenant opened on a silent empty grid.
       */
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

    it('withdraws the notice the moment the operator asks for something', () => {
      create();
      answerSettings(membershipSettings({ displayMode: 2 }));
      answerDefinitions();
      httpMock.expectNone(USERS_URL);
      fixture.detectChanges();

      expect(query('.user-list__notice')).not.toBeNull();

      // The unfiltered affordance the notice names. Pressed, it dispatches the listing the legacy's
      // L264 branch served.
      pressLetter(ALL_FILTER_LABEL);

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

    it('lets waiting win over emptiness', () => {
      /*
       * An empty page is a legitimate answer rather than an error, but claiming it while a request is still
       * in flight would tell the reader "nothing found" about a query that has not answered yet.
       */
      arrive(pageOf([]));

      expect(query('app-empty-state')).not.toBeNull();

      typeSearch('pending');

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
      /*
       * The same three-valued vocabulary as the removal path: `AccessDenied.ascx.vb` L41-L47 presented a
       * denial at the warning level in BOTH of its branches. A refusal is not a fault.
       */
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
      /*
       * ⚠ THE WIRE KEYS ARE .NET MODEL-STATE KEYS AND THE RENDERED KEYS ARE NOT THE SAME STRING, which is
       * the fact this case pins. The dictionary arrives in the request contract's own Pascal casing —
       * `UserName`, `PageIndex` — and the shared contract lower-cases the FIRST CHARACTER ONLY at the
       * boundary, deliberately and in one function, so a client key matches the client's own member
       * spelling; lower-casing the whole key would turn `PageSize` into `pagesize` and match nothing. What
       * a reader sees is therefore `userName`, and anything reading the document directly still sees
       * `UserName`.
       *
       * ⚠ EVERY READ OF THE FIXTURE IS A BRACKET ACCESS. `noPropertyAccessFromIndexSignature` is enabled, so
       * `errors.UserName` is a compilation error rather than a silent `undefined` — and the dotted form
       * would be wrong here in any case, because the key is not a member name.
       */
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
      /*
       * The empty-string key is what a model-state failure uses for a message about the request as a whole.
       * It has no first character to lower-case and no control to match, so it survives untouched and is
       * shown beside the summary rather than beside a field.
       */
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
      /*
       * ⚠ REACHED THROUGH THE POLICY FAILURE, WHICH IS THE ONE STATE WHERE BOTH HOLD AT ONCE. Every store
       * command clears the failure slot before it dispatches, so pressing retry REMOVES the surface the
       * affordance lives on — the disabled binding could never be observed that way. The policy path is
       * different by design: a failed policy read is RECORDED and the listing is then dispatched anyway,
       * without clearing it, because a tenant whose policy is unavailable still has accounts. So the banner
       * is on screen while the listing is genuinely in flight, and that is when a second press must not be
       * able to queue behind the first.
       */
      create();
      expectRequest('GET', MEMBERSHIP_SETTINGS_URL, 'the account-policy read').flush(
        problem('server_error', 500, 'The policy store is unavailable.'),
        { status: 500, statusText: 'Internal Server Error' },
      );
      fixture.detectChanges();
      answerDefinitions();

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
     * The submit path is immediate and would defeat the whole point of these cases: what is being
     * tested is what happens to an emission that has not fired yet.
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
      // ⚠ THE ORDERING DEFECT, END TO END. Typing "bl" starts a delay inside the shared box;
      // pressing "C" a moment later dispatches a query for C; the delay then elapsed and emitted
      // "bl", so the OLDER intent silently replaced the NEWER one and the strip showed C selected
      // over a listing of accounts beginning with B. The screen now calls the pending emission off
      // before it dispatches its own query.
      arrive();
      beginTyping('bl');

      pressLetter('B');

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

      pressLetter('B');
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

      pressLetter(ALL_FILTER_LABEL);

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
    it('falls back to the account name when the chosen property stops being declared', () => {
      // ⚠ THE SCREEN LIED ABOUT WHAT IT WAS SEARCHING. The third axis is one entry per
      // tenant-declared profile property, and the declarations live in the application-scoped store
      // that the neighbouring profile-declarations screen writes. The chosen axis was written only by
      // the selector's change handler and never revisited, so when the property it named stopped
      // being declared its `<option>` vanished — and a `<select>` whose selected value is no longer
      // among its options falls back to the FIRST option in the browser, while the component went on
      // holding the removed name. The operator read "User Name" and every search queried the deleted
      // property.
      arrive();
      chooseAxis(ODD_PROPERTY_NAME);

      // The catalogue is re-read without that property, exactly as it would be after a removal on the
      // neighbouring screen.
      TestBed.inject(UserStore).loadProfileDefinitions();
      expectRequest('GET', PROFILE_DEFINITIONS_URL, 'the re-read declarations').flush(
        envelope([profileDefinition(SECOND_PROPERTY_NAME, 12)]),
      );
      fixture.detectChanges();

      typeSearch('Bris');

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

      TestBed.inject(UserStore).loadProfileDefinitions();
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

    it('keeps a chosen property that is still declared', () => {
      // The reconciliation must not be a reset on every catalogue read, or an operator's choice would
      // be discarded whenever anything re-read the declarations.
      arrive();
      chooseAxis(ODD_PROPERTY_NAME);

      TestBed.inject(UserStore).loadProfileDefinitions();
      expectRequest('GET', PROFILE_DEFINITIONS_URL, 'the re-read declarations').flush(
        envelope(PROFILE_DEFINITIONS),
      );
      fixture.detectChanges();

      typeSearch('Bris');

      const searched = expectListRead('the search on the retained axis');

      expect(paramOf(searched, PROFILE_PROPERTY_NAME_PARAM)).toBe(ODD_PROPERTY_NAME);
      searched.flush(pageOf([userRow()]));
      fixture.detectChanges();
    });
  });

  describe('a read that failed without a problem document', () => {
    it('shows the store\u2019s authored summary instead of nothing at all', () => {
      // ⚠ THE FAILURE THIS MAKES VISIBLE WAS COMPLETELY SILENT. The runtime decoders that check each
      // response against its published contract run inside the service's own mapping, DOWNSTREAM of
      // the interceptor's error handling — so a `200` whose body does not match its contract throws a
      // plain error with no document, no status and no support reference. The banner bound only a
      // document, so it rendered nothing: the grid stayed empty because no rows were committed, and
      // no surface on the screen said why.
      //
      // A page envelope missing its `meta` is exactly that input: the transport answers success and
      // the page decoder refuses the body.
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

    it('leaves the search and paging affordances usable, so there is a way to retry', () => {
      create();
      answerSettings();
      answerDefinitions();
      expectListRead('the listing read').flush({ items: [userRow()] });
      fixture.detectChanges();

      typeSearch('bl');

      const retried = expectListRead('the retry');

      expect(paramOf(retried, USER_NAME_PARAM)).toBe('bl');
      retried.flush(pageOf([userRow()]));
      fixture.detectChanges();

      expect(query('.error-banner'))
        .withContext('a successful read clears the failure')
        .toBeNull();
    });
  });

  describe('whose write settled', () => {
    it('reports a removal refused AFTER an unrelated write settled first', () => {
      // ⚠ DEFECT (a) FROM THE STORE'S OWN NOTE, AT THE SCREEN THAT SUFFERED IT. This screen used to
      // settle its removal by watching the store's aggregate write flag fall — which happens when the
      // FIRST write anywhere in the application finishes. An unrelated write settling therefore
      // consumed this screen's removal marker, so the refusal that arrived afterwards had nothing to
      // attribute itself to: the row the server refused to delete silently stayed, with nothing said.
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


});
