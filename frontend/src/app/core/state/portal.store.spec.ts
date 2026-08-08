/**
 * Specification for {@link PortalStore} — the signal store that owns portal (tenant)
 * state for the dnn-migration administration front end.
 *
 * The legacy tree carries ZERO automated tests of any kind, so nothing here is a port
 * of an existing assertion; every one is net-new. What each assertion is FOR, though,
 * is a legacy behaviour measured in the sources named beside it, so the file doubles as
 * the executable record of which legacy semantics survived the crossing intact.
 *
 * ---------------------------------------------------------------------------
 * WHAT IS UNDER TEST
 * ---------------------------------------------------------------------------
 * The store is the COMPOSITION layer over `core/services/portal.service.ts`. It
 * subscribes, cancels a superseded read, sequences a write with the read that confirms
 * it, holds loading and classified-failure state per concern, removes optimistically
 * from a collection already in hand, and derives projections. Those seven things are
 * what this file asserts.
 *
 * Five proofs carry more weight than the rest, and each has its own group below:
 *
 *   1. THE WIRE PAGE INDEX IS ZERO-BASED. The first page addresses itself as 0, the
 *      third as 2, and 1 is never transmitted for the first page.
 *   2. BOTH -1 AND 0 ARE REAL PORTAL IDENTIFIERS and survive unchanged, singly and
 *      together in one page.
 *   3. THE OPERATOR'S FILTER TEXT REACHES THE WIRE UNDECORATED — no pattern character
 *      is added on this side, and none is trimmed away.
 *   4. THE TWO QUOTA MEANINGS ARE NEVER MERGED. Nought and minus one are distinct
 *      values and no derived member collapses them.
 *   5. `asReadonly()` IS LOAD-BEARING. No public slice carries a setter or an updater,
 *      and the store replaces a collection rather than editing one in place.
 *
 * ---------------------------------------------------------------------------
 * THE HARNESS, AND WHY IT IS SHAPED THIS WAY
 * ---------------------------------------------------------------------------
 * Karma with Jasmine, matching the mandated gate command
 * `ng test --watch=false --browsers=ChromeHeadless --code-coverage` — `--browsers` is a
 * Karma argument, so a different runner would make the gate command invalid. Every
 * double below is a Jasmine one.
 *
 * ⚠️ `provideHttpClient()` IS LISTED BEFORE `provideHttpClientTesting()`, and the order
 * is load-bearing rather than tidy. The testing provider replaces the real backend that
 * the first one registered; reversing the pair leaves the live backend in place and the
 * specification starts issuing real requests.
 *
 * ⚠️ `httpMock.verify()` RUNS IN `afterEach` AND IS THE MOST IMPORTANT LINE IN THE FILE.
 * Without it an unflushed or unexpected request passes in silence, which is the usual way
 * an Angular HTTP specification reports a false green. Here it earns its place twice
 * over, because four of the store's write commands re-read the listing on success and
 * one re-reads the alias collection: a specification that flushed the write and walked
 * away would leave that follow-up unaccounted for, and `verify()` is what refuses to let
 * that happen. Several groups below therefore expect and flush TWO requests for one
 * command, and say so.
 *
 * THE REAL SERVICE RUNS AGAINST THE TESTING BACKEND. No spy stands in for
 * `PortalService`, because the query parameters are a primary assertion target here —
 * proofs 1 and 3 are statements about what reaches the wire — and a spy would replace
 * the very thing being measured. The store's own contribution stays distinguishable
 * because the service is a transport with no behaviour of its own beyond composing the
 * request and unwrapping the envelope.
 *
 * NO INTERCEPTOR CHAIN IS REGISTERED. The correlation identifier, the bearer token and
 * the translation of a failure into wording are attached by interceptors wired in
 * `app.config.ts`, and each is asserted by its own specification. What this file observes
 * is therefore the store's own handling of the framework's failed response, which is
 * exactly what the store classifies in production: the error interceptor announces a
 * failure and then re-throws the original value.
 *
 * ---------------------------------------------------------------------------
 * ⚠️ EVERY EXPECTED ADDRESS IS A RELATIVE LITERAL
 * ---------------------------------------------------------------------------
 * The `test` target in `angular.json` declares no file replacements and no
 * configurations at all, so a specification compiles against `environment.ts` — which IS
 * the production environment file in this workspace, the development one being the
 * replacement rather than the other way round. Its base is the RELATIVE `/api/v1`, and
 * it has to stay relative: the reverse proxy serves the bundle and forwards `/api/` to
 * the API on that same origin, and the compose service name it forwards to does not
 * resolve inside a browser at all.
 *
 * So each address below is written out as a literal relative string rather than read back
 * from the endpoint catalogue. Importing the catalogue would make this file agree with
 * whatever that module happens to emit, including a doubled version segment or an
 * absolute host — the two mistakes in this area that no compiler, no linter and no
 * successful build detects. Writing the literal makes this file an independent check
 * instead of a mirror.
 *
 * ⚠️ A STRING MATCHER PASSED TO `expectOne` FILTERS ON THE ADDRESS *WITH* ITS QUERY
 * STRING. That makes it the strictest available assertion for a call that sends no
 * parameters — matching `/api/v1/portals/3/aliases` proves the path AND the total
 * absence of a query string in one step, which is how the unpaged alias group asserts
 * being unpaged. A call that does send parameters uses the predicate overload against
 * the bare path and asserts each parameter by name and value, because spelling out a
 * serialised query string would assert encoding order as though it were contract.
 *
 * ---------------------------------------------------------------------------
 * MIGRATION CONTEXT — the legacy behaviour each group preserves
 * ---------------------------------------------------------------------------
 * Every citation was read in this repository rather than taken on trust.
 *
 * MIGRATION 1 — THE PAGE INDEX ON THE WIRE IS ZERO-BASED, and the legacy screen's
 *   one-based counter was always a presentation detail.
 *   `Website/admin/Portal/Portals.ascx.vb:L47` seeded `Private _CurrentPage As Integer = 1`
 *   and `:L142` subtracted one immediately before the call:
 *   `PortalController.GetPortalsByName(Filter <trailing wildcard>, CurrentPage - 1, PageSize, TotalRecords)`.
 *   No arithmetic is applied to a page index anywhere in the store, and none is applied
 *   here either: the assertions compare against the value as transmitted. The one-based
 *   count survives in the shared pager component, where the translation between the two
 *   bases is performed.
 *
 * MIGRATION 2 — THE SEARCH PATTERN IS THE SERVER'S TO COMPOSE. That same legacy line
 *   concatenated a trailing wildcard onto the operator's text at the call site, before it
 *   reached the data layer. Match semantics now sit behind the data-access abstraction,
 *   so the client transmits the text byte for byte: untrimmed, its case unchanged, and
 *   undecorated. This file asserts the transmission and deliberately asserts NOTHING
 *   about which rows match, because that judgement belongs to the repository and is the
 *   server's to change.
 *
 * MIGRATION 3 — EXACT-MATCH ALIAS RESOLUTION REPLACED A SUBSTRING PREDICATE, and the
 *   change closed a multi-tenant mis-resolution hazard rather than tidying a query. The
 *   legacy tenant resolver — created as `GetPortalSettings`, a name describing a settings
 *   reader over a body that resolves a tenant, at
 *   `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569` —
 *   wrapped the requested host name in wildcards on both sides at `:L4582`
 *   (`where PortalAlias like '%' + @PortalAlias + '%'`) and took the lowest matching
 *   identifier, so one portal's alias being a fragment of another's was enough to resolve
 *   a request to the wrong tenant. Resolution is now an exact match performed by the
 *   alias-resolution middleware. Nothing below reproduces the fragment predicate.
 *
 * MIGRATION 4 — `-1` AND `0` ARE BOTH REAL PORTAL IDENTIFIERS.
 *   `01.00.00.SqlDataProvider:L77` declares `[PortalID] [int] IDENTITY (-1, 1) NOT NULL`,
 *   so the first portal ever created carries -1 and the second carries 0; and
 *   `Library/Components/Shared/Null.vb:L41-L45` defines the absent-integer marker as -1,
 *   its body being literally `Return -1`. One number therefore means both "the first
 *   portal" and "no portal". A truthiness test drops the second portal and a comparison
 *   against the marker drops the first, so the store performs neither and this file
 *   proves it by addressing both.
 *
 * MIGRATION 5 — THE QUOTA COLUMN CARRIES TWO MEANINGS AND THEY ARE NEVER MERGED.
 *   `Library/Components/Portal/PortalController.vb:L87` hydrates the read path through
 *   `Convert.ToInt32(Null.SetNull(dr("UserQuota"), objPortalInfo.UserQuota))`, so a
 *   database null became the absent-integer marker of -1; the provisioning path at
 *   `:L355-L357` seeds `Dim intUserQuota As Integer = 0`. Nought therefore means
 *   UNLIMITED and minus one means NOT SET. Neither is coalesced, defaulted or folded
 *   into the other. The same trap exists on the module side, where a cache time of
 *   nought and a default of minus one coexist on one column — asserted by the module
 *   store's own specification, not here.
 *
 * MIGRATION 6 — POSITIONAL PARAMETER LISTS BECAME NAMED REQUEST CONTRACTS.
 *   `PortalController.vb:L980` declared `CreatePortal` with FIFTEEN positional
 *   parameters, eleven of them strings, so adjacent arguments were interchangeable to
 *   the compiler and a transposed pair produced a portal with its description in its
 *   keywords and no error anywhere; `:L1568` declared `UpdatePortalInfo` with
 *   TWENTY-SEVEN and passed all twenty-seven straight through in the same order. The
 *   create and update groups below assert that the request body is an OBJECT WITH NAMED
 *   MEMBERS and never a positional sequence.
 *
 * MIGRATION 7 — THE UNTYPED COLLECTION BECAME A PAGED ENVELOPE.
 *   `PortalController.vb:L1263` declared `Public Function GetPortals() As ArrayList`,
 *   carrying neither an element type nor a total, so a caller could not tell how many
 *   records existed beyond the page in hand. The page and its total now travel together,
 *   which is what the coordinate-retention assertions measure.
 *
 * MIGRATION 8 — THERE IS NO PORTAL-SETTINGS TABLE, so the settings endpoint is not a
 *   key/value accessor. Positively established at `PortalController.vb:L1209-L1210`,
 *   where `GetCurrentPortalSettings()` returns
 *   `CType(HttpContext.Current.Items("PortalSettings"), PortalSettings)` — a composite
 *   assembled per request and held in ambient request state, never a persisted
 *   aggregate. Portal configuration is columns on the portal row. The settings group
 *   below asserts a whole-projection read and a whole-projection replace, and asserts
 *   structurally that no per-key accessor exists to be called.
 *
 * MIGRATION 9 — HOST-NAME ALIASES ARE UNPAGED. They arrive as a bare array and the
 *   store holds no page index, page size or total for them. Asserted both by matching
 *   the bare address, which proves no parameter was sent, and by a structural probe over
 *   the store's own member names.
 *
 * MIGRATION 10 — VIEW STATE AND SESSION STATE ARE GONE. The legacy alias editor wrote
 *   `ViewState.Add("PortalAliasID", …)` and `ViewState.Add("PortalID", …)` and read them
 *   back on every postback; those two facts are now the selected-alias and
 *   selected-portal signals, with no serialised control tree and no round trip. The one
 *   view-state entry that does NOT become a signal is the return address the legacy
 *   screens kept under `UrlReferrer`: return navigation is the router's concern, and
 *   holding it here would give the application two answers to one question. `Session(`
 *   has zero occurrences anywhere in the legacy tree, so that half of the requirement is
 *   satisfied vacuously — measured, reported, and not substituted for.
 *
 * MIGRATION 11 — NONE OF THE LEGACY CACHING IS REPRODUCED CLIENT-SIDE. The legacy portal
 *   controller reached the shared cache thirteen times and invalidated whole scopes at a
 *   stroke, one of a hundred-odd in-scope call sites into
 *   `Library/Components/Providers/Caching/DataCache.vb`. Caching is now a server concern
 *   behind a named-key service with explicit invalidation, so there is no cache map, no
 *   time to live and no staleness flag for this file to assert — and nothing here can
 *   serve a portal the server has since changed.
 *
 * MIGRATION 12 — PAGER VISIBILITY IS PRESENTATION. `Portals.ascx.vb:L155-L157` set
 *   `ctlPagingControl.Visible = (PageSize < TotalRecords)`. The store publishes that same
 *   comparison as a boolean over the SERVED coordinates, and this file asserts the
 *   boolean and nothing more: whether a control appears belongs to
 *   `shared/components/pagination`, and no rendering is exercised here.
 *
 * MIGRATION 13 — A REFUSAL CODE IS ONE DOTTED STRING AND IS NEVER SPLIT AT ITS FULL
 *   STOP. The two refusals this store can receive are asserted verbatim, and each is
 *   additionally asserted to be a single value rather than a path into a nested
 *   structure. See the divergence note below for the spelling actually published.
 *
 * MIGRATION 14 — A REFUSAL IS A WARNING AND A FAULT IS AN ERROR, and the legacy
 *   application is the authority rather than a house style.
 *   `Website/admin/Security/AccessDenied.ascx.vb` performs no permission check at all —
 *   it merely presents a denial — and BOTH of its branches render at
 *   `ModuleMessage.ModuleMessageType.YellowWarning`, at `:L43` for the message handed in
 *   through the query string and `:L45` for the localised default. A refused request
 *   therefore resolves to warning severity here and never to danger styling that would
 *   tell an operator something is broken when the system is working as configured.
 *   Throttling is asserted nowhere in this file: it applies to the sign-in endpoints and
 *   no portal endpoint can produce it, so inventing a case would describe a response
 *   this resource cannot send.
 *
 * MIGRATION 15 — THE REGISTRATION MODE WAS RENAMED AND ITS ZERO IS A REAL VALUE. The
 *   legacy `PortalRegistrationType` became `UserRegistrationMode`, declared in
 *   `core/models/portal.model.ts` and nowhere else, and its first member is a genuine
 *   choice rather than an absence — as is the first member of the advertising mode. Both
 *   are asserted to survive a round trip while holding zero.
 *
 * MIGRATION 16 — XML SERIALISATION ATTRIBUTES ARE GONE FROM THE DOMAIN. The legacy
 *   portal object carried an element attribute per property and two properties marked
 *   ignorable; the target hands serialisation to the API boundary. Nothing here asserts
 *   an XML shape, an element name or an ignore marker, because none exists to assert.
 *
 * MIGRATION 17 — LOCALISATION IS NOT PORTED. The legacy screens resolved every label
 *   through a per-control resource file; this application authors its English wording in
 *   its templates with those files as the reference. No translation runtime is added and
 *   no message identifier appears below.
 *
 * MIGRATION 18 — EVERY OPTION-STRICT-OFF COERCION IS NOW EXPLICIT. The legacy
 *   administration pages compiled with `<compilation debug="false" strict="false">`
 *   (`Website/release.config:L125`), so the portal code-behinds could legally hold late
 *   binding and implicit narrowing that no compiler reported. Strict TypeScript is what
 *   forces such a coercion to surface, and the typed fixtures below are part of that
 *   forcing function: each is declared as the real model contract, so a mis-spelled
 *   member is a build failure here rather than an `undefined` read at run time. No type
 *   assertion, no non-null assertion and no suppression comment appears in this file.
 *
 * ---------------------------------------------------------------------------
 * ⚠️ DIVERGENCES FROM THE BRIEF FOR THIS FILE, recorded rather than absorbed
 * ---------------------------------------------------------------------------
 * D-A — THE REFUSAL CODES ARE SPELLED AS THE SERVER PUBLISHES THEM. The brief names the
 *   last-remaining refusal `Portal.LastPortal` and the collision refusal
 *   `DuplicateAlias` / `DuplicatePortalAlias`. Those are LEGACY RESOURCE KEYS, and the
 *   first of them is not even that: the measured legacy key is the unqualified
 *   `LastPortal`, read at `PortalController.vb:L200` through
 *   `Localization.GetString("LastPortal")` against the global resource file, and the
 *   qualified spelling occurs nowhere in this repository. The API publishes its own
 *   reason codes, both legacy alias keys collapsing onto one of them because the server
 *   reports one code for the collision however it was reached. This file therefore
 *   asserts the PUBLISHED codes, verbatim and unsplit, which is what the store can
 *   actually receive. Asserting a legacy key would be a green test over a value that
 *   never crosses the wire.
 *
 * D-B — THE FILTER'S MATCH SEMANTICS ARE THE SERVER'S, AND THEY ARE NOT WHAT THE LEGACY
 *   PATTERN WAS. The brief describes the portal-name filter as anchored at the start of
 *   the name, which is what the legacy trailing wildcard produced. The implemented
 *   repository trims the text, folds its case and tests the name for the fragment
 *   anywhere within it, and the transport contract documents the parameter accordingly.
 *   That difference changes nothing this file can assert, because the client-side
 *   obligation is identical under either predicate: transmit the operator's text
 *   unaltered and add no pattern character. This file asserts exactly that and describes
 *   the predicate nowhere, so it stays correct if the server changes its mind.
 *
 * D-C — THE LEGACY EXPIRED PSEUDO-FILTER HAS NO SUCCESSOR TO TEST.
 *   `Portals.ascx.vb:L138-L140` compared the filter text against a LOCALISED resource
 *   string and, on a match, called `PortalController.GetExpiredPortals()` and hid the
 *   pager outright — so which query ran depended on the display language, and
 *   translating a resource file changed the behaviour of the screen. No endpoint exposes
 *   that listing and the store models no such filter, so this file writes no expired-case
 *   assertion. Recorded here rather than invented below.
 *
 * D-D — NO DERIVED QUOTA MEMBER EXISTS, so there is no leaked presentation concern to
 *   report on that front. Asserted structurally rather than assumed.
 *
 * ---------------------------------------------------------------------------
 * NO USER-SPECIFIED RULES GOVERN THIS WORKSPACE. The rules document returned the same
 * one-line absence statement to every request made of it, including requests for ranges
 * beginning past its first line, which is what establishes there is no body to page
 * through. No rule is invented here to fill the gap, and the absence is not treated as
 * licence: the enterprise baseline applies instead, and this file holds to it —
 * behavioural equivalence with the legacy screens, migration context recorded in place,
 * strict TypeScript with no assertion and no suppression, and a scope of exactly one
 * authored file.
 */

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { BannerAdvertisingMode, UserRegistrationMode } from '../models/portal.model';
import { PortalStore } from './portal.store';

import type { TestRequest } from '@angular/common/http/testing';

import type { ApiResponse, PagedResponse } from '../models/paged-result.model';
import type {
  CreatePortalAliasRequest,
  CreatePortalRequest,
  PortalAlias,
  PortalDetail,
  PortalListItem,
  PortalSettings,
  UpdatePortalAliasRequest,
  UpdatePortalRequest,
  UpdatePortalSettingsRequest,
} from '../models/portal.model';
import type {
  ProblemDetails,
  ValidationProblemDetails,
} from '../models/problem-details.model';
import type { PortalFailure } from './portal.store';

// ---------------------------------------------------------------------------
// THE ADDRESSES UNDER TEST, AS RELATIVE LITERALS.
// ---------------------------------------------------------------------------

/** The portal collection. Paged. */
const PORTALS_URL = '/api/v1/portals';

/**
 * The identifier of the FIRST portal any legacy installation ever created.
 *
 * `01.00.00.SqlDataProvider:L77` seeds the identity at minus one, and
 * `Null.vb:L41-L45` defines the absent-integer marker as the same number. Both facts
 * are true at once, which is the whole reason this constant is named rather than
 * written inline: a reader who meets a bare -1 in an assertion cannot tell which of
 * the two it stands for.
 */
const SEEDED_PORTAL_ID = -1;

/** One portal, addressed by the seeded identity. The highest-value address in the file. */
const SEEDED_PORTAL_URL = '/api/v1/portals/-1';

/** The identifier of the SECOND portal, which the same identity seed makes nought. */
const SECOND_PORTAL_ID = 0;

/** One portal, addressed by nought. */
const SECOND_PORTAL_URL = '/api/v1/portals/0';

/**
 * A plainly ordinary identifier, used wherever the value itself is beside the point.
 *
 * Its purpose is contrast: the two extraordinary identifiers stand out as the
 * deliberate cases they are instead of blending into every other assertion.
 */
const PORTAL_ID = 3;

/** One portal, addressed ordinarily. */
const PORTAL_URL = '/api/v1/portals/3';

/** One portal's settings projection. Read and replaced whole; never per key. */
const PORTAL_SETTINGS_URL = '/api/v1/portals/3/settings';

/** One portal's host-name alias collection. Deliberately unpaged. */
const PORTAL_ALIASES_URL = '/api/v1/portals/3/aliases';

/** The identifier used for every single-alias case. */
const PORTAL_ALIAS_ID = 7;

/** One alias of one portal. */
const PORTAL_ALIAS_URL = '/api/v1/portals/3/aliases/7';

/** A second alias, for the collection-mutation cases. */
const OTHER_PORTAL_ALIAS_ID = 8;

/** The second alias of one portal. */
const OTHER_PORTAL_ALIAS_URL = '/api/v1/portals/3/aliases/8';

// ---------------------------------------------------------------------------
// THE WIRE PARAMETER NAMES, SPELLED OUT.
// ---------------------------------------------------------------------------

/**
 * The parameter names a portal-listing request may carry.
 *
 * Written out here rather than imported from the module that emits them, for the same
 * reason the addresses are: a specification that reads a name back from its producer
 * cannot detect a rename, because both sides move together. These are the names the
 * SERVER binds, so they are contract. The two easiest to get wrong are recorded
 * explicitly:
 *
 *   * `pageIndex`, not `page`. The shared pager component's input is a ONE-based
 *     `page`; this is the ZERO-based value on the wire. Neither side is corrected to
 *     match the other, and the translation between them happens in the feature layer.
 *   * `sortDir`, not `sortDirection`, and its values are the server enumeration's own
 *     capitalised member names — an abbreviated or lower-cased value is answered with a
 *     400 rather than quietly defaulted.
 */
const QUERY_KEY = {
  pageIndex: 'pageIndex',
  pageSize: 'pageSize',
  sortBy: 'sortBy',
  sortDir: 'sortDir',
  query: 'query',
  name: 'name',
} as const;

/**
 * The query-string keys the legacy screens used, retained so their absence can be
 * asserted BY NAME instead of inferred from a count.
 *
 * `Portals.ascx.vb:L215-L232` built its links with `"filter=" & Filter` and
 * `"currentpage=" & CurrentPage`, the latter carrying the screen's ONE-based value, and
 * `EditPortalAlias.ascx.vb:L57` addressed an alias row through
 * `Request.QueryString("paid")`. All three are legacy URL keys rather than target wire
 * keys: the target spells its parameters in camel case, counts from zero, and names the
 * alias in full as a path segment. Reintroducing any of them would produce a request
 * that still reaches the right path and still returns 200, differing solely by a
 * parameter the server ignores — which is why each is named.
 */
const LEGACY_URL_KEY = {
  filter: 'filter',
  currentPage: 'currentpage',
  aliasId: 'paid',
} as const;

// ---------------------------------------------------------------------------
// SENTINEL VALUES, NAMED.
// ---------------------------------------------------------------------------

/**
 * A quota of nought: UNLIMITED.
 *
 * The provisioning path seeded this (`PortalController.vb:L355-L357`). A truthiness
 * test reads it as absent and a defaulting operator replaces it, so neither appears in
 * the store or below.
 */
const QUOTA_UNLIMITED = 0;

/**
 * A quota of minus one: NOT SET.
 *
 * The read path produced this from a database null (`PortalController.vb:L87`). It is
 * a different fact from the value above and the two are never folded together.
 */
const QUOTA_NOT_SET = -1;

/**
 * A page reference holding minus one: NO SUCH PAGE IS CONFIGURED.
 *
 * Six members of the portal contract are page references and each admits this value.
 * It is numerically identical to {@link SEEDED_PORTAL_ID} and semantically unrelated
 * to it, which is precisely the confusion one group below exists to rule out.
 */
const NO_SUCH_PAGE = -1;

/**
 * The prefix the API wraps a reason code in when it writes a failure document.
 *
 * Measured rather than assumed: the API writes no `code` member, so the code travels
 * inside `type`. Lower case throughout, because that is how the server spells it.
 */
const FAILURE_TYPE_PREFIX = 'urn:dnnmigration:error:';

/**
 * The refusal answered when the sole surviving portal is asked to be removed.
 *
 * ONE dotted string, not a path into a nested structure, and never split at its full
 * stop. See divergence D-A for why this spelling and not the legacy resource key.
 */
const LAST_PORTAL_REFUSAL = 'portal.last_remaining';

/**
 * The refusal answered when a host name is already bound.
 *
 * Both legacy alias keys collapse onto this one code, because the server reports one
 * code for the collision however it was reached.
 */
const DUPLICATE_ALIAS_REFUSAL = 'portal.alias_duplicate';

/**
 * The distributed-trace identifier a problem document carries.
 *
 * Derived server-side from the ambient activity, falling back to the request identifier
 * the host assigned. Named as a constant because two groups below assert that it
 * survives: it and the value beneath it are the sole join keys between something a
 * person saw in a browser and a request the server logged, so losing either leaves an
 * operator's report unattachable to anything.
 */
const TRACE_ID = '00-8f4b2c1d9e6a47f3b5c8d1e2f3a4b5c6-1a2b3c4d5e6f7a8b-01';

/**
 * The correlation identifier a problem document carries.
 *
 * A different value in a different format from the one above, echoed from the header
 * the front end sent, and the one an operator quotes. The store prefers it as the
 * support reference and falls back to the trace identifier, which is asserted both ways.
 */
const CORRELATION_ID = 'b7f3d2a1-4c5e-4a9b-8d6f-2e3c4a5b6d7e';

// ---------------------------------------------------------------------------
// PAYLOAD FIXTURES.
//
// Every member name below is taken from the model contracts, and every factory is
// declared as the contract it produces, so a renamed or mis-spelled member is a
// compilation failure here rather than an `undefined` read at run time. That matters
// more than usual for this resource: the identifier member carries a single lower-case
// letter d, and a payload key spelled with two capitals would type-check nowhere and
// fail silently everywhere.
//
// Each factory takes its extraordinary values as arguments and accepts an override bag
// for the rest, so no assertion below depends on a default that a later edit might
// change. No fixture object is shared between specifications, and none is mutated: a
// factory is called afresh wherever a payload is needed.
// ---------------------------------------------------------------------------

/**
 * One row of the portal listing.
 *
 * The listing row is a genuinely different contract from the detail record — it carries
 * the alias host names as bare strings and the tallies the legacy grid displayed, and
 * omits everything that grid never showed. Using the detail contract here would assert
 * against a body the collection endpoint does not send.
 *
 * @param portalId The identifier the row carries. Passed rather than defaulted, so the
 * two extraordinary values read as the deliberate choices they are.
 * @param portalName The display name.
 * @param overrides Members to replace, for the fidelity cases.
 * @returns The row.
 */
function portalListItem(
  portalId: number,
  portalName: string,
  overrides: Partial<PortalListItem> = {},
): PortalListItem {
  return {
    portalId,
    portalName,
    aliases: ['localhost'],
    users: 3,
    pages: 12,
    hostSpace: 0,
    hostFee: 0,
    expiryDate: null,
    ...overrides,
  };
}

/**
 * A page of portal rows, in the envelope the collection endpoint actually writes.
 *
 * ⚠️ THE PAGING FACTS ARE NESTED UNDER `meta`; they are not siblings of `items`. The two
 * arrangements are indistinguishable to a type checker and to an assertion on a
 * successful status, because reading a member the body does not carry yields `undefined`
 * at run time while compiling perfectly — so flushing the flat shape would produce a
 * green result over a body the server never sends.
 *
 * The page count is supplied rather than derived, because it is a SERVER-computed value:
 * deriving it here would let this file agree with a client-side recomputation instead of
 * checking that the server's own answer is carried through untouched.
 *
 * @param items The rows on the page.
 * @param pageIndex The zero-based index of the page these rows came from.
 * @param pageSize The size of the page the server served.
 * @param totalCount The size of the whole match set.
 * @param totalPages The page count the server reported.
 * @returns The body to flush.
 */
function portalPage(
  items: readonly PortalListItem[],
  pageIndex: number,
  pageSize: number,
  totalCount: number,
  totalPages: number,
): PagedResponse<PortalListItem> {
  return { items, meta: { totalCount, pageIndex, pageSize, totalPages } };
}

/**
 * A single-row first page, for the many specifications whose subject is the request
 * rather than the response.
 *
 * @param portalId The identifier of the row.
 * @returns The body to flush.
 */
function singleRowPage(portalId: number): PagedResponse<PortalListItem> {
  return portalPage([portalListItem(portalId, 'Baseline Portal')], 0, 10, 1, 1);
}

/**
 * One portal in full.
 *
 * EVERY member the contract declares is present, including the ones holding a legacy
 * sentinel, because the API serialises with its ignore condition set never to elide a
 * written member: a member with no value travels as its sentinel or as an explicit null
 * and never goes missing. A fixture that omitted them would test a body the server does
 * not send and would hide exactly the coalescing this file exists to rule out.
 *
 * The defaults deliberately carry sentinels rather than tidy values — a quota of nought,
 * five page references holding minus one, an empty-string member and a false flag — so
 * that any specification reading this record is reading sentinel-bearing data.
 *
 * @param portalId The identifier. Passed rather than defaulted.
 * @param overrides Members to replace, for the sentinel cases.
 * @returns The record.
 */
function portalDetail(portalId: number, overrides: Partial<PortalDetail> = {}): PortalDetail {
  return {
    portalId,
    portalName: 'Baseline Portal',
    description: 'The portal established by the baseline installation.',
    keyWords: 'baseline,portal',
    // An empty string is the legacy spelling of an absent string — `Null.vb:L71-L75`
    // has the literal body `Return ""` — so it is DATA and must survive as itself. The
    // same file's absent-boolean is `False`, and its `IsNull` consequently answers true
    // for false exactly as it answers true for minus one, which is the distinction the
    // target contract removes by declaring its wire booleans non-nullable.
    footerText: '',
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
    pageQuota: QUOTA_UNLIMITED,
    userQuota: QUOTA_UNLIMITED,
    users: 3,
    pages: 12,
    // The measured legacy administrator role name is the PLURAL form, created at
    // `PortalController.vb:L1390` through an eleven-argument positional call. It is
    // reproduced in the fixture for fidelity and is asserted nowhere: the server decides
    // what a role is called, and comparing the wording here would pin a server decision
    // into a client specification.
    administratorRoleId: 0,
    administratorRoleName: 'Administrators',
    registeredRoleId: 1,
    registeredRoleName: 'Registered Users',
    guid: '2f1c3d4e-5a6b-4c8d-9e0f-1a2b3c4d5e6f',
    paymentProcessor: null,
    processorUserId: null,
    siteLogHistory: -1,
    adminTabId: 2,
    superTabId: NO_SUCH_PAGE,
    splashTabId: NO_SUCH_PAGE,
    homeTabId: NO_SUCH_PAGE,
    loginTabId: NO_SUCH_PAGE,
    userTabId: NO_SUCH_PAGE,
    defaultLanguage: 'en-US',
    // Nought is a meaningful offset rather than a missing value, so this member is
    // subject to the same prohibition on truthiness tests as the identifiers.
    timeZoneOffset: 0,
    homeDirectory: 'Portals/0',
    aliases: null,
    ...overrides,
  };
}

/**
 * One portal's settings projection.
 *
 * ⚠️ There is NO settings TABLE behind this contract; see migration note 8. This is a
 * projection of columns on the portal row, which is why no factory here produces a
 * key/value pair, a dictionary or an entry list, and why nothing below asserts one.
 *
 * @param portalId The portal the projection belongs to.
 * @param overrides Members to replace.
 * @returns The projection.
 */
function portalSettings(
  portalId: number,
  overrides: Partial<PortalSettings> = {},
): PortalSettings {
  return {
    portalId,
    portalName: 'Baseline Portal',
    description: 'The portal established by the baseline installation.',
    keyWords: 'baseline,portal',
    footerText: '',
    logoFile: 'logo.gif',
    backgroundFile: null,
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.None,
    currency: 'USD',
    administratorId: 1,
    hostFee: 0,
    hostSpace: 0,
    pageQuota: QUOTA_UNLIMITED,
    userQuota: QUOTA_UNLIMITED,
    paymentProcessor: null,
    processorUserId: null,
    siteLogHistory: -1,
    splashTabId: NO_SUCH_PAGE,
    homeTabId: NO_SUCH_PAGE,
    loginTabId: NO_SUCH_PAGE,
    userTabId: NO_SUCH_PAGE,
    defaultLanguage: 'en-US',
    timeZoneOffset: 0,
    homeDirectory: 'Portals/0',
    guid: '2f1c3d4e-5a6b-4c8d-9e0f-1a2b3c4d5e6f',
    ...overrides,
  };
}

/**
 * One host-name alias.
 *
 * All three member names are modernised away from the legacy all-capitals acronym
 * spelling, so a payload key copied from the legacy object would read as `undefined`.
 * Declaring the return type is what makes that a compilation failure instead.
 *
 * @param portalId The portal the alias resolves to.
 * @param portalAliasId The alias identifier.
 * @param httpAlias The host name, optionally with a port.
 * @param isCurrent Whether the request that read the row resolved the tenant through it.
 * @returns The alias.
 */
// The current-alias flag defaults to FALSE, which is the answer for every row a fixture
// builds unless it says otherwise: it is the server's per-request projection, and a
// helper that defaulted it to true would arrange the withheld case by accident.
function portalAlias(
  portalId: number,
  portalAliasId: number,
  httpAlias: string,
  isCurrent = false,
): PortalAlias {
  return { portalAliasId, portalId, httpAlias, isCurrent };
}

/**
 * Wraps a single record in the success envelope the API writes.
 *
 * The metadata companion is PRESENT AND NULL rather than absent, because that is what
 * the server writes: it serialises with its ignore condition set to never, so a response
 * with no page to describe writes the member holding null. Flushing the member out
 * altogether would assert against a body the server never sends.
 *
 * @param data The payload.
 * @returns The envelope to flush.
 * @typeParam T The payload contract.
 */
function envelope<T>(data: T): ApiResponse<T> {
  return { data, meta: null };
}

// ---------------------------------------------------------------------------
// REQUEST FIXTURES.
// ---------------------------------------------------------------------------

/**
 * A portal-creation request, carrying every member the contract declares.
 *
 * Replaces a FIFTEEN-parameter positional call; see migration note 6. The member count
 * is beside the point — what changed is that a member is addressed by name, so a
 * transposed pair is no longer expressible.
 *
 * @param overrides Members to replace.
 * @returns The request body.
 */
function createPortalRequest(overrides: Partial<CreatePortalRequest> = {}): CreatePortalRequest {
  return {
    portalName: 'Contoso',
    portalAlias: 'contoso.example.test',
    description: 'A tenant for the composition cases.',
    keyWords: 'contoso',
    homeDirectory: 'Portals/1',
    templateFile: 'Default Website.template',
    // A false flag is DATA on this contract and travels as itself. The legacy contract
    // could not make that distinction; this one declares the member non-nullable.
    isChildPortal: false,
    administratorFirstName: 'Ada',
    administratorLastName: 'Lovelace',
    administratorUsername: 'ada',
    administratorPassword: 'not-a-real-credential',
    administratorEmail: 'ada@example.test',
    ...overrides,
  };
}

/**
 * A portal-update request, carrying every member the contract declares.
 *
 * Replaces a TWENTY-SEVEN-parameter positional call; see migration note 6. The arity did
 * not shrink — the addressing changed from position to name, which is the whole point.
 *
 * @param overrides Members to replace, for the sentinel cases.
 * @returns The request body.
 */
function updatePortalRequest(overrides: Partial<UpdatePortalRequest> = {}): UpdatePortalRequest {
  return {
    portalId: PORTAL_ID,
    portalName: 'Contoso',
    logoFile: 'logo.gif',
    footerText: '',
    expiryDate: null,
    userRegistration: UserRegistrationMode.PublicRegistration,
    bannerAdvertising: BannerAdvertisingMode.None,
    currency: 'USD',
    administratorId: 1,
    hostFee: 0,
    hostSpace: 0,
    pageQuota: QUOTA_UNLIMITED,
    userQuota: QUOTA_UNLIMITED,
    paymentProcessor: null,
    processorUserId: null,
    processorCredentialReference: null,
    description: 'A tenant for the sentinel cases.',
    keyWords: 'contoso',
    backgroundFile: null,
    siteLogHistory: -1,
    splashTabId: NO_SUCH_PAGE,
    homeTabId: NO_SUCH_PAGE,
    loginTabId: NO_SUCH_PAGE,
    userTabId: NO_SUCH_PAGE,
    defaultLanguage: 'en-US',
    timeZoneOffset: 0,
    homeDirectory: 'Portals/1',
    ...overrides,
  };
}

/**
 * A settings-replacement request.
 *
 * The contract is the portal-update contract WITHOUT its identifier, because the portal
 * is already named by the path and accepting a second copy in the body would let the two
 * disagree.
 *
 * @param overrides Members to replace.
 * @returns The request body.
 */
function updatePortalSettingsRequest(
  overrides: Partial<UpdatePortalSettingsRequest> = {},
): UpdatePortalSettingsRequest {
  // Destructured with a rest element rather than rebuilt member by member, so that a
  // member added to the update contract is carried here automatically instead of being
  // silently absent from every settings assertion in the file.
  const { portalId: identifierInPath, ...withoutIdentifier } = updatePortalRequest();
  void identifierInPath;

  return { ...withoutIdentifier, ...overrides };
}

/**
 * A host-name binding request.
 *
 * @param httpAlias The host name to bind.
 * @returns The request body.
 */
function createAliasRequest(httpAlias: string): CreatePortalAliasRequest {
  return { httpAlias };
}

/**
 * A host-name replacement request.
 *
 * @param httpAlias The host name to store in place of the current one.
 * @returns The request body.
 */
function updateAliasRequest(httpAlias: string): UpdatePortalAliasRequest {
  return { httpAlias };
}

// ---------------------------------------------------------------------------
// FAILURE FIXTURES.
//
// The failure contract is imported rather than re-declared, because the store narrows
// against the real predicate and a locally invented shape could satisfy an assertion
// while failing that predicate — which would report the store as losing a document it
// had correctly refused to accept.
// ---------------------------------------------------------------------------

/**
 * Builds the RFC 7807 document the API writes for a reason-coded failure.
 *
 * The code travels inside `type` behind the published prefix, because the API writes no
 * `code` member. Both correlation identifiers are carried: they are independent values
 * in different formats, and an operator's report is joinable to a server log through
 * whichever of the two survives.
 *
 * @param code The reason code, dotted, as the server spells it.
 * @param status The status the document accompanies.
 * @param detail The explanatory sentence, which is UNTRUSTED text — see the group on
 * message text.
 * @returns The document to flush.
 */
function reasonedProblem(code: string, status: number, detail: string): ProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}${code}`,
    title: 'Request refused',
    status,
    detail,
    instance: PORTALS_URL,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
  };
}

/**
 * Builds a document carrying no reason code, which is what a plain fault looks like.
 *
 * The type member holds the specification's OWN default value — the value a document is
 * assumed to carry when it names no specific problem type. The API writes a
 * status-specific reference there instead, and this fixture deliberately does not
 * reproduce it: no assertion in this file reads the member on a plain fault, and no
 * absolute address of any kind appears in this specification, so that a grep for one
 * finds nothing and an API origin cannot hide inside a fixture. What the member must do
 * here is be a string, so that the document-narrowing predicate recognises the body at
 * all, and it is.
 *
 * @param status The status the document accompanies.
 * @param detail The explanatory sentence.
 * @param traceId The trace identifier to carry.
 * @returns The document to flush.
 */
function plainProblem(status: number, detail: string, traceId: string): ProblemDetails {
  return {
    type: 'about:blank',
    title: 'An unexpected fault occurred',
    status,
    detail,
    instance: PORTALS_URL,
    traceId,
  };
}

/**
 * Builds the per-field failure document the model-state factory writes.
 *
 * ⚠️ The dictionary is an index signature and this workspace enables the compiler option
 * that forbids reading one through dot access, so every assertion against it below uses
 * BRACKET access. Its keys are the server's model-state keys and are Pascal-cased,
 * because they name model members rather than JSON members — a camel-cased lookup finds
 * nothing.
 *
 * @param detail The explanatory sentence.
 * @param reported The per-field messages, keyed as the server keys them.
 * @returns The document to flush.
 */
function validationProblem(
  detail: string,
  reported: Readonly<Record<string, readonly string[]>>,
): ValidationProblemDetails {
  return {
    type: `${FAILURE_TYPE_PREFIX}validation.failed`,
    title: 'One or more validation failures occurred',
    status: 400,
    detail,
    instance: PORTALS_URL,
    traceId: TRACE_ID,
    correlationId: CORRELATION_ID,
    errors: reported,
  };
}


// ---------------------------------------------------------------------------
// NARROWING HELPERS.
//
// ⚠️ WHY THESE EXIST AT ALL. Three values this file reads are legitimately nullable —
// a query parameter that may not have been sent, a failure slice that is null until a
// request fails, and the problem document inside a failure that may not have arrived.
// The non-null assertion operator would silence all three in one character, and it is
// forbidden in this workspace, so each is narrowed EXPLICITLY instead and reports its
// own absence as a failure that names what was missing. A type assertion would be worse
// still: it would let a specification assert against a value the compiler had never
// agreed existed.
// ---------------------------------------------------------------------------

/**
 * Reads a query parameter that the request under assertion is required to carry.
 *
 * Narrows rather than asserts. The parameter collection answers `string | null`, and the
 * absence is reported by name so that a missing parameter fails with a legible message
 * instead of an assertion against null further down.
 *
 * @param request The observed request.
 * @param key The wire parameter name.
 * @returns The transmitted value, as transmitted.
 */
function queryValue(request: TestRequest, key: string): string {
  const held: string | null = request.request.params.get(key);

  if (held === null) {
    throw new Error(`The request carried no ${key} parameter; the addressed URL was ${request.request.urlWithParams}.`);
  }

  return held;
}

/**
 * Narrows a failure slice that a specification has arranged to be populated.
 *
 * @param held The slice as read.
 * @param concern What the slice describes, for the failure message.
 * @returns The classified failure.
 */
function heldFailure(held: PortalFailure | null, concern: string): PortalFailure {
  if (held === null) {
    throw new Error(`The ${concern} failure slice held null; the specification expected a classified failure.`);
  }

  return held;
}

/**
 * Narrows the problem document inside a classified failure.
 *
 * A failure legitimately carries none — that is what a request which never reached the
 * server looks like — so the document is narrowed rather than assumed.
 *
 * @param failure The classified failure.
 * @returns The document as received.
 */
function heldProblem(failure: PortalFailure): ProblemDetails {
  const document: ProblemDetails | null = failure.problem;

  if (document === null) {
    throw new Error('The classified failure carried no problem document.');
  }

  return document;
}

/**
 * Narrows the per-field document inside a classified failure.
 *
 * @param failure The classified failure.
 * @returns The narrowed document, whose dictionary is present by construction.
 */
function heldValidation(failure: PortalFailure): ValidationProblemDetails {
  const document: ValidationProblemDetails | null = failure.validation;

  if (document === null) {
    throw new Error('The classified failure was not narrowed to a per-field document.');
  }

  return document;
}

/**
 * Narrows a slice that holds a record once a read has completed.
 *
 * @param held The slice as read.
 * @param concern What the slice describes, for the failure message.
 * @returns The record.
 * @typeParam T The record contract.
 */
function heldRecord<T>(held: T | null, concern: string): T {
  if (held === null) {
    throw new Error(`The ${concern} slice held null; the specification expected a record.`);
  }

  return held;
}

/**
 * Narrows the alias collection, which is null until it has been read.
 *
 * The three states of that slice are load-bearing and the first two must not be
 * collapsed: null means the collection was never read and says nothing about how many
 * aliases exist, an empty array means it was read and there are none, and a populated
 * array means it was read and these are they.
 *
 * @param held The slice as read.
 * @returns The collection.
 */
function heldAliases(held: readonly PortalAlias[] | null): readonly PortalAlias[] {
  if (held === null) {
    throw new Error('The alias collection slice held null; the specification expected a read collection.');
  }

  return held;
}

// ---------------------------------------------------------------------------
// STRUCTURAL PROBES.
// ---------------------------------------------------------------------------

/**
 * Enumerates the member names a store instance carries: its fields plus the methods on
 * its prototype.
 *
 * ⚠️ HOW THIS IS AND IS NOT USED. Every probe built on it below asserts the ABSENCE of a
 * member, never the presence or the behaviour of one. That distinction is what keeps this
 * helper from becoming a test of implementation detail: a field the store keeps for its
 * own purposes may well appear in the list, and its appearing there cannot make an
 * absence probe pass falsely. Nothing below reads a value through a name this returns.
 *
 * @param target The instance to enumerate.
 * @returns Every own field name and every prototype method name, the constructor aside.
 */
function memberNames(target: object): readonly string[] {
  const prototype: object = Object.getPrototypeOf(target);
  const fields: readonly string[] = Object.keys(target);
  const methods: readonly string[] = Object.getOwnPropertyNames(prototype).filter(
    (name: string) => name !== 'constructor',
  );

  return [...fields, ...methods];
}

/**
 * Reports whether a value carries the two members a writable signal exposes.
 *
 * ⚠️ WHY PROPERTY PRESENCE AND NOT AN ATTEMPTED WRITE. Attempting a write would need a
 * type assertion to get past the read-only declaration, and an assertion is forbidden
 * here — it would also prove less, because it would demonstrate that one particular
 * cast fails rather than that the member is absent. Presence is the direct question: a
 * writable signal carries a setter and an updater as own properties, and the value
 * returned by the read-only projection carries neither, inheriting nothing that would
 * supply them.
 *
 * @param candidate The signal to inspect.
 * @returns Which of the two mutating members the value carries.
 */
function mutatingMembers(candidate: object): { readonly setter: boolean; readonly updater: boolean } {
  return { setter: 'set' in candidate, updater: 'update' in candidate };
}

// ---------------------------------------------------------------------------
// REQUEST ASSERTIONS.
// ---------------------------------------------------------------------------

/**
 * Asserts that a request carried no query string whatsoever.
 *
 * Four checks rather than one, because they fail differently and each names its own
 * regression: the count catches an added parameter, the equality names which one was
 * added, comparing the addressed URL against the bare path catches a parameter appended
 * to the path itself rather than through the parameter collection, and the three legacy
 * keys are asserted absent BY NAME so that a reintroduction is reported as itself rather
 * than as an off-by-one in a count.
 *
 * @param request The observed request.
 * @param description What the request was, for the failure message.
 */
function expectNoQueryString(request: TestRequest, description: string): void {
  expect(request.request.params.keys().length)
    .withContext(`${description}: carries no query parameter`)
    .toBe(0);
  expect(request.request.params.keys())
    .withContext(`${description}: parameter names`)
    .toEqual([]);
  expect(request.request.urlWithParams)
    .withContext(`${description}: the addressed URL is the bare path`)
    .toBe(request.request.url);
  expect(request.request.params.has(LEGACY_URL_KEY.aliasId))
    .withContext(`${description}: the legacy alias key is not sent`)
    .toBeFalse();
}

/**
 * Asserts that neither legacy listing key was transmitted.
 *
 * Both were lower case and the page key was ONE-based, so either arriving would mean the
 * spelling or the base had regressed — and neither regression changes the status the
 * server answers with, which is why it is asserted rather than left to be noticed.
 *
 * @param request The observed request.
 */
function expectNoLegacyListingKeys(request: TestRequest): void {
  expect(request.request.params.has(LEGACY_URL_KEY.filter))
    .withContext('the legacy lower-case filter key is not sent')
    .toBeFalse();
  expect(request.request.params.has(LEGACY_URL_KEY.currentPage))
    .withContext('the legacy lower-case one-based page key is not sent')
    .toBeFalse();
}


describe('PortalStore', () => {
  let store: PortalStore;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        // ⚠️ THE ORDER OF THESE TWO IS LOAD-BEARING. The real client registers a live
        // backend; the testing provider then replaces it. Listing them the other way
        // round leaves the live backend in place, and the specification silently starts
        // issuing real requests to a host that is not there.
        provideHttpClient(),
        provideHttpClientTesting(),
        // The store under test. Declared root-provided, and named here so that the
        // instance under assertion is created inside this test module rather than
        // wherever a previous specification happened to leave one. NO interceptor is
        // registered: each of the three is asserted by its own specification, and
        // attaching them here would mean asserting several units at once.
        PortalStore,
      ],
    });

    store = TestBed.inject(PortalStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // ⚠️ THE MOST IMPORTANT LINE IN THE FILE. It fails when a request was issued that no
    // specification expected, or expected and never answered. Every group below therefore
    // contributes to one collective assertion beyond its own: that the store issues
    // exactly the requests it claims to and no others — which is how the "a refusal
    // triggers no re-read" and "a command that has nothing to do issues no request" cases
    // are proven at all, since each of those asserts an absence that nothing else could
    // detect.
    httpMock.verify();
  });

  // -------------------------------------------------------------------------
  // Harness helpers, closing over the mock backend.
  // -------------------------------------------------------------------------

  /**
   * Takes the pending portal-listing request.
   *
   * Uses the PREDICATE overload against the bare path rather than a string matcher,
   * because a listing request always carries at least a page index and a string matcher
   * compares against the address WITH its query string — which would force this helper to
   * spell out a serialised query string and thereby assert encoding order as though it
   * were contract.
   *
   * @param description What the request was, for the failure message.
   * @returns The pending request.
   */
  function expectListing(description: string): TestRequest {
    return httpMock.expectOne(
      (candidate) => candidate.url === PORTALS_URL && candidate.method === 'GET',
      description,
    );
  }

  /**
   * Answers the listing re-read that follows a successful write.
   *
   * Four of the store's write commands re-read the listing on success, so a
   * specification that flushed the write alone would leave a request outstanding and be
   * failed by `verify()`. Naming that step makes the sequencing visible instead of
   * looking like boilerplate.
   *
   * @param description What the re-read follows, for the failure message.
   */
  function settleListingReread(description: string): void {
    expectListing(description).flush(singleRowPage(PORTAL_ID));
  }

  /**
   * Reads a page of rows into the store and returns the rows as the store holds them.
   *
   * @param body The page to answer with.
   * @param description What the read was, for the failure message.
   */
  function readListing(body: PagedResponse<PortalListItem>, description: string): void {
    store.loadPortals();
    expectListing(description).flush(body);
  }

  // =========================================================================
  // THE STORE ITSELF
  // =========================================================================

  describe('the store itself', () => {
    it('resolves from the root injector as a single instance', () => {
      expect(store).toBeTruthy();
      // Declared root-provided, so two resolutions are the same object. Two instances
      // would give two screens two different answers about which portal is selected, and
      // a screen that re-created the store on navigation would discard a page an
      // operator had just paged to.
      expect(TestBed.inject(PortalStore))
        .withContext('one instance is shared by every consumer')
        .toBe(store);
    });

    it('seeds the first page with nothing selected, and issues no request until asked', () => {
      expect(store.pageIndex()).withContext('the first page is index nought').toBe(0);
      expect(store.requestedPageSize())
        .withContext('no size preference, so the server applies its own default')
        .toBeNull();
      expect(store.nameFilter()).withContext('no filter').toBeNull();
      expect(store.isFiltered()).toBeFalse();
      expect(store.sort())
        .withContext('no ordering preference, so the server orders as it chooses')
        .toEqual({ sortBy: null, sortDir: null });
      expect(store.portals()).withContext('no rows in hand').toEqual([]);
      expect(store.totalCount()).toBe(0);
      expect(store.totalPages()).toBe(0);
      expect(store.isListEmpty()).toBeTrue();
      expect(store.isPastEnd())
        .withContext('nothing matched at all is not the same as a page past the end')
        .toBeFalse();
      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.hasSelection()).toBeFalse();
      expect(store.selectedPortal()).toBeNull();
      expect(store.settings()).toBeNull();
      expect(store.aliases()).withContext('the collection was never read').toBeNull();
      expect(store.aliasesLoaded()).toBeFalse();
      expect(store.aliasCount())
        .withContext('an unread collection reports no count, rather than a count of nought')
        .toBeNull();
      expect(store.selectedAliasId()).toBeUndefined();
      expect(store.selectedAlias()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.hasFailure()).toBeFalse();

      // No request is issued by construction, and `verify()` in `afterEach` is what
      // asserts it: a store that read eagerly on injection would leave a request
      // outstanding here.
    });

    it('does nothing at all when asked to re-read a portal while nothing is selected', () => {
      expect(store.hasSelection()).toBeFalse();

      store.loadSelectedPortal();

      // Deliberately no expectation of a request. A store that guessed an identifier
      // here would ask for a portal nobody named, and `verify()` is the assertion.
      expect(store.detailLoading()).toBeFalse();
      expect(store.selectedPortal()).toBeNull();
    });

    it('returns every slice to its seeded value on reset, issuing no request', () => {
      readListing(
        portalPage([portalListItem(SEEDED_PORTAL_ID, 'First')], 0, 10, 1, 1),
        'the listing that reset will discard',
      );
      store.selectPortal(SEEDED_PORTAL_ID);
      expect(store.hasSelection()).toBeTrue();

      store.reset();

      expect(store.pageIndex()).toBe(0);
      expect(store.requestedPageSize()).toBeNull();
      expect(store.nameFilter()).toBeNull();
      expect(store.portals()).toEqual([]);
      expect(store.totalCount()).toBe(0);
      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.selectedPortal()).toBeNull();
      expect(store.settings()).toBeNull();
      expect(store.aliases()).toBeNull();
      expect(store.busy()).toBeFalse();
      expect(store.hasFailure()).toBeFalse();
    });
  });

  // =========================================================================
  // LIST PAGING — the zero-based wire index, and the coordinates it answers with
  // =========================================================================

  describe('list paging', () => {
    it('sends a zero-based page index of nought for the first page, and never one', () => {
      store.loadPortals();

      const request = expectListing('the first page of the portal listing');

      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('a relative address, reached through the reverse proxy on one origin')
        .toBe(PORTALS_URL);

      const transmitted: string = queryValue(request, QUERY_KEY.pageIndex);

      expect(transmitted).withContext('the first page addresses itself as nought').toBe('0');
      // NEGATIVE CONTROL, and the reason this is a proof rather than an incidental pass.
      // The legacy screen seeded its counter at one and subtracted one at the call site
      // (`Portals.ascx.vb:L47` and `:L142`). A store that had carried the one-based value
      // through would send this instead, and every other assertion in the group would
      // still hold.
      expect(transmitted)
        .withContext('the one-based screen value never reaches the wire')
        .not.toBe('1');
      expect(request.request.urlWithParams)
        .withContext('and it appears nowhere in the addressed URL either')
        .not.toContain('pageIndex=1');
      expectNoLegacyListingKeys(request);

      request.flush(singleRowPage(SEEDED_PORTAL_ID));

      expect(store.listLoading()).toBeFalse();
      expect(store.listFailure()).toBeNull();
    });

    it('sends two for the third page', () => {
      store.goToPage(2);

      const request = expectListing('the third page of the portal listing');

      expect(queryValue(request, QUERY_KEY.pageIndex))
        .withContext('index two addresses the third page and is transmitted as itself')
        .toBe('2');
      expect(store.pageIndex()).toBe(2);

      request.flush(portalPage([portalListItem(PORTAL_ID, 'Third page row')], 2, 10, 27, 3));

      expect(store.servedPageIndex()).toBe(2);
    });

    it('omits the page size when none was requested, and sends the requested size by name', () => {
      store.loadPortals();

      const unsized = expectListing('an unsized listing');

      expect(unsized.request.params.has(QUERY_KEY.pageSize))
        .withContext('an unsupplied size is an omission, so the server applies its own')
        .toBeFalse();
      unsized.flush(singleRowPage(PORTAL_ID));

      store.setPageSize(25);

      const sized = expectListing('a listing with an explicit size');

      expect(queryValue(sized, QUERY_KEY.pageSize)).toBe('25');
      expect(store.requestedPageSize()).toBe(25);
      sized.flush(portalPage([portalListItem(PORTAL_ID, 'Sized')], 0, 25, 1, 1));
    });

    it('retains the coordinates and the total the server reported, unchanged', () => {
      readListing(
        portalPage([portalListItem(PORTAL_ID, 'Row')], 2, 20, 137, 7),
        'a listing whose coordinates are the subject',
      );

      // MIGRATION 7: the legacy reader returned an untyped collection and reported the
      // total through a by-reference argument, so a caller held rows and a number that
      // nothing tied together. The page and its total now travel as one value, and every
      // coordinate below is the SERVER'S, carried through without recomputation.
      expect(store.totalCount()).withContext('the total, as reported').toBe(137);
      expect(store.servedPageIndex()).withContext('the served index, as reported').toBe(2);
      expect(store.servedPageSize()).withContext('the served size, as reported').toBe(20);
      expect(store.totalPages()).withContext('the page count, as reported').toBe(7);
      expect(store.isListEmpty()).toBeFalse();
      expect(store.listMeta()).toEqual({
        totalCount: 137,
        pageIndex: 2,
        pageSize: 20,
        totalPages: 7,
      });
    });

    it('returns to the first page when the page size changes', () => {
      store.goToPage(4);
      expectListing('a deep page').flush(portalPage([], 4, 10, 3, 1));
      expect(store.pageIndex()).toBe(4);

      store.setPageSize(50);

      const request = expectListing('the listing re-read at the new size');

      expect(queryValue(request, QUERY_KEY.pageIndex))
        .withContext('a size change invalidates the page address, so the read starts again')
        .toBe('0');
      expect(store.pageIndex()).toBe(0);
      request.flush(singleRowPage(PORTAL_ID));
    });

    it('returns to the first page when the filter changes', () => {
      store.goToPage(4);
      expectListing('a deep page').flush(portalPage([], 4, 10, 3, 1));

      store.setNameFilter('Cont');

      const request = expectListing('the filtered listing');

      expect(queryValue(request, QUERY_KEY.pageIndex)).toBe('0');
      expect(queryValue(request, QUERY_KEY.name)).toBe('Cont');
      request.flush(singleRowPage(PORTAL_ID));
    });

    it('returns to the first page when the ordering changes', () => {
      store.goToPage(4);
      expectListing('a deep page').flush(portalPage([], 4, 10, 3, 1));

      store.setSort({ sortBy: 'portalName', sortDir: 'Descending' });

      const request = expectListing('the reordered listing');

      expect(queryValue(request, QUERY_KEY.pageIndex)).toBe('0');
      request.flush(singleRowPage(PORTAL_ID));
    });

    it('transmits the ordering direction under the server own capitalised member name', () => {
      store.setSort({ sortBy: 'portalName', sortDir: 'Ascending' });

      const request = expectListing('an ordered listing');

      expect(queryValue(request, QUERY_KEY.sortBy)).toBe('portalName');
      // The binder accepts the enumeration member name and answers a lower-cased or
      // abbreviated spelling with a 400 rather than defaulting quietly, so the casing is
      // contract rather than style.
      expect(queryValue(request, QUERY_KEY.sortDir)).toBe('Ascending');
      expect(store.sort()).toEqual({ sortBy: 'portalName', sortDir: 'Ascending' });
      request.flush(singleRowPage(PORTAL_ID));

      store.clearSort();

      const unordered = expectListing('the listing returned to the server own ordering');

      expect(unordered.request.params.has(QUERY_KEY.sortBy))
        .withContext('no preference is an omission, not a blank value')
        .toBeFalse();
      expect(unordered.request.params.has(QUERY_KEY.sortDir)).toBeFalse();
      unordered.flush(singleRowPage(PORTAL_ID));
    });

    it('sends the general free-text parameter for no listing request of its own accord', () => {
      // The listing accepts TWO textual restrictions and they are different parameters:
      // the paging contract's general one and the portal-name one. This store drives the
      // name-specific parameter, and asserting the other absent is what stops a later
      // edit from quietly swapping which of the two the screen filters on.
      store.setNameFilter('Cont');

      const request = expectListing('a name-filtered listing');

      expect(request.request.params.has(QUERY_KEY.query)).toBeFalse();
      expect(request.request.params.keys().length)
        .withContext('the page index and the name, and nothing else')
        .toBe(2);
      request.flush(singleRowPage(PORTAL_ID));
    });

    it('reports the pager requirement from the served coordinates, and nothing more', () => {
      // MIGRATION 12: `Portals.ascx.vb:L155-L157` set
      // `ctlPagingControl.Visible = (PageSize < TotalRecords)`. The same comparison is
      // published here as a boolean over the SERVED coordinates. Whether a control
      // appears is the shared pager's business, and no rendering is exercised below.
      readListing(
        portalPage([portalListItem(PORTAL_ID, 'Row')], 0, 10, 3, 1),
        'a set that fits on one page',
      );
      expect(store.pagerRequired())
        .withContext('a total inside one page needs no pager')
        .toBeFalse();

      store.reloadPortals();
      expectListing('a set that overflows one page').flush(
        portalPage([portalListItem(PORTAL_ID, 'Row')], 0, 10, 42, 5),
      );
      expect(store.pagerRequired()).toBeTrue();
    });

    it('distinguishes a page past the end of the set from a set that matched nothing', () => {
      readListing(portalPage([], 9, 10, 0, 0), 'a listing that matched nothing');
      expect(store.isListEmpty()).withContext('nothing matched at all').toBeTrue();
      expect(store.isPastEnd()).toBeFalse();

      store.reloadPortals();
      expectListing('a listing addressed past the end').flush(portalPage([], 9, 10, 12, 2));
      expect(store.isListEmpty())
        .withContext('records exist, so the set is not empty')
        .toBeFalse();
      expect(store.isPastEnd())
        .withContext('but the requested page holds none of them')
        .toBeTrue();
    });

    it('cancels a superseded listing read rather than letting a late answer land', () => {
      store.loadPortals();
      const abandoned = expectListing('the first read');

      store.goToPage(2);

      expect(abandoned.cancelled)
        .withContext('the superseded read is cancelled, so a slow answer cannot overwrite a fast one')
        .toBeTrue();

      const current = expectListing('the superseding read');

      expect(queryValue(current, QUERY_KEY.pageIndex)).toBe('2');
      current.flush(portalPage([portalListItem(PORTAL_ID, 'Row')], 2, 10, 27, 3));
      expect(store.servedPageIndex()).toBe(2);
    });
  });


  // =========================================================================
  // THE NAME FILTER — transmitted as typed, decorated by nobody on this side
  // =========================================================================

  describe('name filter', () => {
    it('transmits the operator text byte for byte, adding no pattern character', () => {
      store.setNameFilter('Cont');

      const request = expectListing('a filtered listing');

      const transmitted: string = queryValue(request, QUERY_KEY.name);

      expect(transmitted)
        .withContext('the text travels as typed; the pattern is the server to compose')
        .toBe('Cont');
      // MIGRATION 2: `Portals.ascx.vb:L142` concatenated a trailing wildcard onto the
      // operator's text at the call site, BEFORE it reached the data layer. That
      // decoration now happens behind the data-access abstraction, so this side may
      // contribute no character to it — a client that decorated the value would double
      // whatever pattern the repository already builds.
      expect(transmitted).withContext('no wildcard is appended on this side').not.toContain('%');
      expect(transmitted.endsWith('%'))
        .withContext('nor is one trailing')
        .toBeFalse();
      expect(transmitted.startsWith('%'))
        .withContext('nor leading')
        .toBeFalse();
      expect(transmitted).withContext('and no underscore pattern character either').not.toContain('_');
      expectNoLegacyListingKeys(request);

      request.flush(singleRowPage(PORTAL_ID));

      expect(store.nameFilter())
        .withContext('and the slice holds the raw text, not a pattern')
        .toBe('Cont');
      expect(store.isFiltered()).toBeTrue();
    });

    it('preserves surrounding space and mixed case exactly as typed', () => {
      const typed = '  CoNtOsO  ';

      store.setNameFilter(typed);

      const request = expectListing('a listing filtered by untidy text');

      const transmitted: string = queryValue(request, QUERY_KEY.name);

      expect(transmitted).withContext('as typed, character for character').toBe(typed);
      expect(transmitted)
        .withContext('no trimming happens on this side; the server trims if it wishes')
        .not.toBe(typed.trim());
      expect(transmitted)
        .withContext('no case folding happens on this side either')
        .not.toBe(typed.toLowerCase());
      expect(transmitted).not.toContain('%');
      request.flush(portalPage([], 0, 10, 0, 0));
    });

    it('treats an empty filter as a value rather than as an absence', () => {
      // `Null.vb:L71-L75` defines the absent string as the EMPTY STRING, its body being
      // literally `Return ""`, and the legacy signup screen tested the same message two
      // ways in one file — `Signup.ascx.vb:L227` reads `If strMessage = ""` while `:L315`
      // reads `If strMessage = Null.NullString`. So an empty string was never a
      // "nothing"; it was a value that happened to mean nothing was set. On this side the
      // sole absence test is against null and undefined, which is what lets an empty
      // filter reach the wire as an empty value.
      store.setNameFilter('');

      const request = expectListing('a listing filtered by an empty value');

      expect(request.request.params.has(QUERY_KEY.name))
        .withContext('the parameter is present')
        .toBeTrue();
      expect(queryValue(request, QUERY_KEY.name))
        .withContext('holding the empty string, transmitted as itself')
        .toBe('');
      expect(store.nameFilter()).toBe('');
      expect(store.isFiltered())
        .withContext('an empty filter is a filter, and is distinct from no filter')
        .toBeTrue();
      request.flush(portalPage([], 0, 10, 0, 0));
    });

    it('drops the parameter altogether and returns to the first page when the filter is cleared', () => {
      store.setNameFilter('Cont');
      expectListing('a filtered listing').flush(singleRowPage(PORTAL_ID));
      store.goToPage(3);
      expectListing('a deep filtered page').flush(portalPage([], 3, 10, 4, 1));

      store.clearNameFilter();

      const request = expectListing('the unfiltered listing');

      expect(request.request.params.has(QUERY_KEY.name))
        .withContext('null is an omission, not a blank value')
        .toBeFalse();
      expect(queryValue(request, QUERY_KEY.pageIndex)).toBe('0');
      expect(store.nameFilter()).toBeNull();
      expect(store.isFiltered()).toBeFalse();
      request.flush(singleRowPage(PORTAL_ID));
    });
  });

  // =========================================================================
  // PORTAL IDENTIFIER SENTINELS — the highest-value group in the file
  // =========================================================================

  describe('portal identifier sentinels', () => {
    it('addresses the seeded portal, whose identifier is minus one, at its literal path', () => {
      // MIGRATION 4: `01.00.00.SqlDataProvider:L77` declares
      // `[PortalID] [int] IDENTITY (-1, 1) NOT NULL`, so this is the FIRST portal any
      // installation created — and `Null.vb:L41-L45` defines the absent-integer marker as
      // the same number. A store that treated the marker as "no portal" would be unable
      // to address the first portal at all.
      store.loadPortal(SEEDED_PORTAL_ID);

      const request = httpMock.expectOne(SEEDED_PORTAL_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('the identifier is interpolated as itself, sign and all')
        .toBe('/api/v1/portals/-1');
      expectNoQueryString(request, 'a single-portal read');

      request.flush(envelope(portalDetail(SEEDED_PORTAL_ID)));

      expect(store.selectedPortalId())
        .withContext('minus one is retained as the selection, not read as an absence')
        .toBe(-1);
      expect(store.hasSelection()).toBeTrue();
      expect(heldRecord(store.selectedPortal(), 'selected portal').portalId)
        .withContext('and minus one survives the round trip on the record')
        .toBe(-1);
      expect(store.detailFailure()).toBeNull();
    });

    it('addresses the second portal, whose identifier is nought, at its literal path', () => {
      store.loadPortal(SECOND_PORTAL_ID);

      const request = httpMock.expectOne(SECOND_PORTAL_URL);

      expect(request.request.method).toBe('GET');
      expect(request.request.url)
        .withContext('nought is a real identifier and is interpolated as itself')
        .toBe('/api/v1/portals/0');
      expectNoQueryString(request, 'a single-portal read');

      request.flush(envelope(portalDetail(SECOND_PORTAL_ID)));

      expect(store.selectedPortalId())
        .withContext('nought is retained as the selection, not read as an absence')
        .toBe(0);
      expect(store.hasSelection()).toBeTrue();
      expect(heldRecord(store.selectedPortal(), 'selected portal').portalId).toBe(0);
    });

    it('retains both extraordinary identifiers in one page, through every derived view', () => {
      // A truthiness filter would drop the row identified by nought; a comparison against
      // the absent-integer marker would drop the row identified by minus one. One page
      // holding both is the arrangement in which either mistake shows up.
      readListing(
        portalPage(
          [
            portalListItem(SEEDED_PORTAL_ID, 'First portal'),
            portalListItem(SECOND_PORTAL_ID, 'Second portal'),
            portalListItem(PORTAL_ID, 'An ordinary portal'),
          ],
          0,
          10,
          3,
          1,
        ),
        'a page holding both extraordinary identifiers',
      );

      const rows: readonly PortalListItem[] = store.portals();

      expect(rows.length).withContext('all three rows survive').toBe(3);
      expect(rows.map((row: PortalListItem) => row.portalId))
        .withContext('in order, with neither sentinel-shaped identifier removed')
        .toEqual([-1, 0, 3]);
      expect(rows.map((row: PortalListItem) => row.portalName)).toEqual([
        'First portal',
        'Second portal',
        'An ordinary portal',
      ]);
      expect(store.totalCount()).toBe(3);
      expect(store.isListEmpty()).toBeFalse();
      expect(store.page().items).toBe(rows);
    });

    it('distinguishes a selection of nought from no selection at all', () => {
      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.hasSelection()).toBeFalse();

      store.selectPortal(SECOND_PORTAL_ID);

      expect(store.selectedPortalId()).toBe(0);
      expect(store.selectedPortalId())
        .withContext('nought is a selection; the absence of one is a distinct undefined')
        .not.toBeUndefined();
      expect(store.hasSelection())
        .withContext('and the derived test is an explicit absence test, never a truthiness test')
        .toBeTrue();

      store.clearSelection();

      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.hasSelection()).toBeFalse();

      // Selecting issues no request; `verify()` asserts it.
    });

    it('distinguishes a selection of minus one from no selection at all', () => {
      store.selectPortal(SEEDED_PORTAL_ID);

      expect(store.selectedPortalId()).toBe(-1);
      expect(store.selectedPortalId())
        .withContext('minus one is a selection, notwithstanding being the legacy absence marker')
        .not.toBeUndefined();
      expect(store.hasSelection()).toBeTrue();

      store.clearSelection();

      expect(store.selectedPortalId()).toBeUndefined();
      expect(store.hasSelection()).toBeFalse();
    });

    it('retains page references holding minus one without conflating them with the identifier', () => {
      // Six members of the portal contract are page references, and on each of them minus
      // one means NO SUCH PAGE IS CONFIGURED — a different fact from the same number
      // appearing as a portal identifier. This record carries an ordinary identifier and
      // five unconfigured references, so the two cannot be confused for one another.
      store.loadPortal(PORTAL_ID);

      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            adminTabId: 4,
            superTabId: NO_SUCH_PAGE,
            splashTabId: NO_SUCH_PAGE,
            homeTabId: NO_SUCH_PAGE,
            loginTabId: NO_SUCH_PAGE,
            userTabId: NO_SUCH_PAGE,
          }),
        ),
      );

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.portalId).withContext('the identifier is its own value').toBe(3);
      expect([
        held.superTabId,
        held.splashTabId,
        held.homeTabId,
        held.loginTabId,
        held.userTabId,
      ])
        .withContext('five unconfigured page references, each retained as minus one')
        .toEqual([-1, -1, -1, -1, -1]);
      expect(held.adminTabId)
        .withContext('and a configured reference is unaffected by its neighbours')
        .toBe(4);
      expect(store.selectedPortalId())
        .withContext('the selection tracks the portal and never a page reference')
        .toBe(3);
    });

    it('retains a page reference of nought, which names a real page', () => {
      store.loadPortal(SEEDED_PORTAL_ID);

      httpMock
        .expectOne(SEEDED_PORTAL_URL)
        .flush(envelope(portalDetail(SEEDED_PORTAL_ID, { homeTabId: 0, adminTabId: 0 })));

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      // The page identity seed is nought as well, so a page numbered nought is as real as
      // a portal numbered nought. Both appear on this one record.
      expect(held.homeTabId).withContext('page nought is a page').toBe(0);
      expect(held.adminTabId).toBe(0);
      expect(held.portalId).withContext('beside a portal identified as minus one').toBe(-1);
    });

    it('retains a zero-valued enumeration member on both enumerations', () => {
      // MIGRATION 15: the first member of each enumeration is a genuine choice, not an
      // absence. The registration enumeration was renamed from its legacy spelling and is
      // declared in the portal model and nowhere else.
      store.loadPortal(PORTAL_ID);

      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            userRegistration: UserRegistrationMode.NoRegistration,
            bannerAdvertising: BannerAdvertisingMode.None,
          }),
        ),
      );

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.userRegistration).toBe(UserRegistrationMode.NoRegistration);
      expect(held.userRegistration).withContext('whose ordinal is nought').toBe(0);
      expect(held.bannerAdvertising).toBe(BannerAdvertisingMode.None);
      expect(held.bannerAdvertising).toBe(0);
      expect(held.userRegistration).not.toBeUndefined();
      expect(held.bannerAdvertising).not.toBeUndefined();
    });

    it('retains an empty string as a value and a nought offset as a measurement', () => {
      store.loadPortal(PORTAL_ID);

      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            footerText: '',
            keyWords: '',
            backgroundFile: null,
            timeZoneOffset: 0,
          }),
        ),
      );

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.footerText).withContext('an empty string is retained as itself').toBe('');
      expect(held.footerText)
        .withContext('and is not normalised into a null on the way in')
        .not.toBeNull();
      expect(held.keyWords).toBe('');
      expect(held.backgroundFile)
        .withContext('while a null that was sent as a null stays a null — the two are not merged')
        .toBeNull();
      expect(held.timeZoneOffset)
        .withContext('nought is a real offset — it is the United Kingdom')
        .toBe(0);
    });
  });


  // =========================================================================
  // USER QUOTA — two meanings on one column, never merged
  // =========================================================================

  describe('user quota', () => {
    it('retains a quota of nought, which means unlimited', () => {
      store.loadPortal(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_URL)
        .flush(envelope(portalDetail(PORTAL_ID, { userQuota: QUOTA_UNLIMITED })));

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      // MIGRATION 5: the provisioning path seeded nought (`PortalController.vb:L355-L357`)
      // and it means UNLIMITED. A defaulting operator or a truthiness test would read it
      // as "not stated" and a screen would then offer to set a quota that is already set.
      expect(held.userQuota).withContext('nought is retained as nought').toBe(0);
      expect(held.userQuota).not.toBeNull();
      expect(held.userQuota).not.toBeUndefined();
    });

    it('retains a quota of minus one, which means not set', () => {
      store.loadPortal(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_URL)
        .flush(envelope(portalDetail(PORTAL_ID, { userQuota: QUOTA_NOT_SET })));

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      // The read path produced this from a database null
      // (`PortalController.vb:L87`, through `Null.SetNull`), and it means NOT SET.
      expect(held.userQuota).withContext('minus one is retained as minus one').toBe(-1);
      expect(held.userQuota)
        .withContext('and is emphatically not folded onto the unlimited value')
        .not.toBe(QUOTA_UNLIMITED);
      expect(held.userQuota).not.toBeNull();
    });

    it('retains the two meanings independently when both appear on one record', () => {
      store.loadPortal(PORTAL_ID);

      httpMock.expectOne(PORTAL_URL).flush(
        envelope(
          portalDetail(PORTAL_ID, {
            userQuota: QUOTA_NOT_SET,
            pageQuota: QUOTA_UNLIMITED,
            hostSpace: QUOTA_UNLIMITED,
          }),
        ),
      );

      const held: PortalDetail = heldRecord(store.selectedPortal(), 'selected portal');

      expect(held.userQuota).withContext('accounts: not set').toBe(-1);
      expect(held.pageQuota).withContext('pages: unlimited').toBe(0);
      expect(held.hostSpace).withContext('disk space: unlimited').toBe(0);
      expect(held.userQuota).not.toBe(held.pageQuota);
    });

    it('carries both quota values onto the wire when a portal is written', () => {
      const composed = updatePortalRequest({
        userQuota: QUOTA_NOT_SET,
        pageQuota: QUOTA_UNLIMITED,
      });

      store.updatePortal(PORTAL_ID, composed);

      const written = httpMock.expectOne(PORTAL_URL);

      expect(written.request.method).toBe('PUT');
      expect(written.request.body)
        .withContext('the body reaches the transport unaltered, sentinels included')
        .toBe(composed);
      expect(composed.userQuota).toBe(-1);
      expect(composed.pageQuota).toBe(0);
      expect(Object.keys(written.request.body))
        .withContext('a member holding nought is present rather than elided as empty')
        .toContain('pageQuota');

      written.flush(
        envelope(portalDetail(PORTAL_ID, { userQuota: QUOTA_NOT_SET, pageQuota: QUOTA_UNLIMITED })),
      );
      settleListingReread('the listing re-read that follows a portal write');
    });

    it('publishes no derived member that merges the two quota meanings', () => {
      // A quota rendered for display — "unlimited" against a number — is a PRESENTATION
      // concern and belongs to the feature layer or a pipe, not to state. The absence is
      // asserted structurally so that adding one is a failing test rather than a review
      // comment.
      const merged: readonly string[] = memberNames(store).filter((name: string) =>
        name.toLowerCase().includes('quota'),
      );

      expect(merged)
        .withContext('no effective, resolved or displayed quota member exists on the store')
        .toEqual([]);
    });
  });

  // =========================================================================
  // CREATE, UPDATE AND DELETE
  // =========================================================================

  describe('create, update and delete', () => {
    it('posts the creation request as a named contract and adopts the portal it receives', () => {
      const composed = createPortalRequest();
      const created: PortalDetail[] = [];

      store.createPortal(composed).subscribe((portal: PortalDetail) => {
        created.push(portal);
      });

      expect(store.detailLoading()).withContext('the write is in flight').toBeTrue();

      const posted = httpMock.expectOne(PORTALS_URL);

      expect(posted.request.method).toBe('POST');
      expectNoQueryString(posted, 'a portal creation');

      const transmitted: unknown = posted.request.body;

      // MIGRATION 6: fifteen positional parameters became one named contract. A positional
      // sequence is what the legacy call site actually was, so ruling it out is the direct
      // statement of what changed.
      expect(Array.isArray(transmitted))
        .withContext('a named contract, never a positional sequence')
        .toBeFalse();
      expect(transmitted)
        .withContext('and the body reaches the transport unaltered — nothing rebuilt, nothing stripped')
        .toBe(composed);

      const names: readonly string[] = Object.keys(posted.request.body);

      expect(names.length).withContext('every member the contract declares').toBe(12);
      expect(names)
        .withContext('including the false-valued flag, which is data rather than an absence')
        .toContain('isChildPortal');
      expect(composed.isChildPortal)
        .withContext('and it holds false — the legacy contract could not tell that from unset')
        .toBeFalse();

      posted.flush(envelope(portalDetail(SECOND_PORTAL_ID)), {
        status: 201,
        statusText: 'Created',
      });

      expect(store.selectedPortalId())
        .withContext('the created portal becomes the selection, identifier nought and all')
        .toBe(0);
      expect(heldRecord(store.selectedPortal(), 'selected portal').portalId).toBe(0);
      expect(store.settings())
        .withContext('and the previous portal scoped state is discarded rather than carried over')
        .toBeNull();
      expect(store.aliases()).toBeNull();
      expect(store.detailLoading()).toBeFalse();
      expect(created.length).withContext('the caller is notified once').toBe(1);
      expect(created[0].portalId).toBe(0);

      settleListingReread('the listing re-read that follows a creation');
    });

    it('puts the update request unaltered, keeps what the server stored, and refreshes the listing', () => {
      const composed = updatePortalRequest({ portalId: PORTAL_ID, portalName: 'Renamed' });
      const stored: PortalDetail[] = [];

      store.updatePortal(PORTAL_ID, composed).subscribe((portal: PortalDetail) => {
        stored.push(portal);
      });

      const written = httpMock.expectOne(PORTAL_URL);

      expect(written.request.method).toBe('PUT');
      expect(written.request.body).toBe(composed);

      const names: readonly string[] = Object.keys(written.request.body);

      // MIGRATION 6: twenty-seven positional parameters became twenty-seven named members.
      // The arity did not shrink, which is the point — what changed is that a member is
      // addressed by name, so a transposed pair is no longer expressible.
      expect(names.length).withContext('every member the contract declares').toBe(27);
      expect(names).toContain('portalId');

      written.flush(envelope(portalDetail(PORTAL_ID, { portalName: 'Renamed' })));

      expect(heldRecord(store.selectedPortal(), 'selected portal').portalName)
        .withContext('the record held is the one the server stored, not the one composed')
        .toBe('Renamed');
      expect(store.detailLoading()).toBeFalse();
      expect(stored.length).toBe(1);

      settleListingReread('the listing re-read that follows an update');
    });

    it('removes the row identified by nought and leaves the row identified by minus one', () => {
      const first = portalListItem(SEEDED_PORTAL_ID, 'First portal');
      const second = portalListItem(SECOND_PORTAL_ID, 'Second portal');

      readListing(portalPage([first, second], 0, 10, 2, 1), 'the listing that will be depleted');

      const readEarlier: readonly PortalListItem[] = store.portals();

      expect(readEarlier.length).toBe(2);

      let removals = 0;

      store.deletePortal(SECOND_PORTAL_ID).subscribe(() => {
        removals += 1;
      });

      const removed = httpMock.expectOne(SECOND_PORTAL_URL);

      expect(removed.request.method).toBe('DELETE');
      expectNoQueryString(removed, 'a portal removal');

      removed.flush(null, { status: 204, statusText: 'No Content' });

      const readLater: readonly PortalListItem[] = store.portals();

      expect(readLater.map((row: PortalListItem) => row.portalId))
        .withContext('the row identified by nought is gone; the one identified by minus one stays')
        .toEqual([-1]);
      expect(readLater)
        .withContext('the collection was REPLACED, so an on-push consumer re-renders')
        .not.toBe(readEarlier);
      expect(readEarlier.length)
        .withContext('and the array a caller read earlier was not edited underneath it')
        .toBe(2);
      expect(removals).toBe(1);

      settleListingReread('the listing re-read that follows a removal');
    });

    it('removes the row identified by minus one and leaves the row identified by nought', () => {
      const first = portalListItem(SEEDED_PORTAL_ID, 'First portal');
      const second = portalListItem(SECOND_PORTAL_ID, 'Second portal');

      readListing(portalPage([first, second], 0, 10, 2, 1), 'the listing that will be depleted');

      store.deletePortal(SEEDED_PORTAL_ID);

      httpMock.expectOne(SEEDED_PORTAL_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.portals().map((row: PortalListItem) => row.portalId))
        .withContext('a comparison against the absent-integer marker would have removed the wrong row')
        .toEqual([0]);

      settleListingReread('the listing re-read that follows a removal');
    });

    it('clears the selection when the portal removed was the selected one', () => {
      store.loadPortal(PORTAL_ID);
      httpMock.expectOne(PORTAL_URL).flush(envelope(portalDetail(PORTAL_ID)));
      expect(store.hasSelection()).toBeTrue();

      store.deletePortal(PORTAL_ID);
      httpMock.expectOne(PORTAL_URL).flush(null, { status: 204, statusText: 'No Content' });

      expect(store.selectedPortalId())
        .withContext('a removed portal cannot remain the selection')
        .toBeUndefined();
      expect(store.hasSelection()).toBeFalse();
      expect(store.selectedPortal()).toBeNull();
      expect(store.settings()).toBeNull();
      expect(store.aliases()).toBeNull();

      settleListingReread('the listing re-read that follows a removal');
    });

    it('leaves an unrelated selection alone when a different portal is removed', () => {
      store.selectPortal(PORTAL_ID);

      store.deletePortal(SECOND_PORTAL_ID);
      httpMock.expectOne(SECOND_PORTAL_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.selectedPortalId())
        .withContext('nought being removed says nothing about three being selected')
        .toBe(3);

      settleListingReread('the listing re-read that follows a removal');
    });

    it('surfaces the last-remaining refusal verbatim, as one dotted value, and re-reads nothing', () => {
      store.deletePortal(SEEDED_PORTAL_ID);

      httpMock.expectOne(SEEDED_PORTAL_URL).flush(
        reasonedProblem(
          LAST_PORTAL_REFUSAL,
          409,
          'You Can Not Delete The Last Portal In Your Database',
        ),
        { status: 409, statusText: 'Conflict' },
      );

      const failure: PortalFailure = heldFailure(store.detailFailure(), 'single-portal');
      const surfaced: string | null = failure.conflictCode;

      expect(failure.status).toBe(409);
      // MIGRATION 13, and divergence D-A: the value compared here is the code the API
      // PUBLISHES, not a legacy resource key. It is ONE dotted string rather than a path
      // into a nested structure, so neither fragment of a naive split is the code.
      expect(surfaced).withContext('the published code, verbatim').toBe(LAST_PORTAL_REFUSAL);
      expect(LAST_PORTAL_REFUSAL.split('.').length)
        .withContext('a naive split would yield two fragments')
        .toBe(2);
      expect(surfaced).withContext('and the first fragment is not the code').not.toBe('portal');
      expect(surfaced).withContext('nor is the second').not.toBe('last_remaining');
      expect(heldProblem(failure).type)
        .withContext('the code travels inside the document type, behind the published prefix')
        .toBe(`${FAILURE_TYPE_PREFIX}${LAST_PORTAL_REFUSAL}`);
      expect(failure.severity)
        .withContext('a state refusal an operator cannot act around is an error, not a caution')
        .toBe('error');
      expect(store.detailLoading()).toBeFalse();
      expect(store.hasFailure()).toBeTrue();

      // NO listing re-read follows a refusal. Nothing is expected here on purpose, and
      // `verify()` is what asserts it: a store that refreshed after a failed write would
      // leave a request outstanding and fail this specification.
    });

    it('holds the rows it already had when a removal is refused', () => {
      const first = portalListItem(SEEDED_PORTAL_ID, 'First portal');

      readListing(portalPage([first], 0, 10, 1, 1), 'the sole portal');

      store.deletePortal(SEEDED_PORTAL_ID);
      httpMock
        .expectOne(SEEDED_PORTAL_URL)
        .flush(reasonedProblem(LAST_PORTAL_REFUSAL, 409, 'Refused'), {
          status: 409,
          statusText: 'Conflict',
        });

      expect(store.portals().map((row: PortalListItem) => row.portalId))
        .withContext('the optimistic removal did not happen, because the server refused')
        .toEqual([-1]);
      expect(store.totalCount()).toBe(1);
    });
  });

  // =========================================================================
  // THE SETTINGS PROJECTION — columns on a row, never a key/value bag
  // =========================================================================

  describe('settings projection', () => {
    it('reads the whole projection from the settings path', () => {
      store.loadSettings(PORTAL_ID);

      const read = httpMock.expectOne(PORTAL_SETTINGS_URL);

      expect(read.request.method).toBe('GET');
      expect(read.request.url).toBe('/api/v1/portals/3/settings');
      expectNoQueryString(read, 'a settings read');

      const projection = portalSettings(PORTAL_ID, {
        userQuota: QUOTA_NOT_SET,
        pageQuota: QUOTA_UNLIMITED,
        footerText: '',
      });

      read.flush(envelope(projection));

      const held: PortalSettings = heldRecord(store.settings(), 'settings');

      expect(held)
        .withContext('the projection is held member for member as the server wrote it')
        .toEqual(projection);
      expect(held.userQuota).toBe(-1);
      expect(held.pageQuota).toBe(0);
      expect(held.footerText).toBe('');
      expect(store.selectedPortalId())
        .withContext('reading settings selects the portal they belong to')
        .toBe(3);
      expect(store.settingsLoading()).toBeFalse();
      expect(store.settingsFailure()).toBeNull();
    });

    it('replaces the whole projection and refreshes the listing', () => {
      const composed = updatePortalSettingsRequest({ portalName: 'Renamed' });
      const saved: PortalSettings[] = [];

      store.saveSettings(PORTAL_ID, composed).subscribe((projection: PortalSettings) => {
        saved.push(projection);
      });

      const written = httpMock.expectOne(PORTAL_SETTINGS_URL);

      expect(written.request.method).toBe('PUT');
      expect(written.request.body).toBe(composed);

      const names: readonly string[] = Object.keys(written.request.body);

      expect(names.length)
        .withContext('the update contract without its identifier — the path already names the portal')
        .toBe(26);
      expect(names)
        .withContext('so a second, contradictable copy of the identifier is not sent')
        .not.toContain('portalId');

      written.flush(envelope(portalSettings(PORTAL_ID, { portalName: 'Renamed' })));

      expect(heldRecord(store.settings(), 'settings').portalName).toBe('Renamed');
      expect(store.settingsLoading()).toBeFalse();
      expect(saved.length).toBe(1);

      settleListingReread('the listing re-read that follows a settings write');
    });

    it('publishes no per-key settings accessor to be called', () => {
      // MIGRATION 8: there is no portal-settings table, so there is no key to be read by.
      // The legacy composite of the same name was assembled per request and held in
      // ambient request state; portal configuration is columns on the portal row. The
      // absence is asserted structurally rather than assumed, so that adding a per-key
      // read or write becomes a failing test.
      const perKey: readonly string[] = memberNames(store).filter(
        (name: string) =>
          /^(get|set|read|write|patch)Setting$/i.test(name) ||
          /settingByKey|settingValue|settingEntries|settingsMap|settingsDictionary/i.test(name),
      );

      expect(perKey).withContext('the projection is read and replaced whole').toEqual([]);
    });

    it('reports a settings read that faults without disturbing the listing it holds', () => {
      readListing(
        portalPage([portalListItem(PORTAL_ID, 'Row')], 0, 10, 1, 1),
        'a listing that must survive a settings fault',
      );

      store.loadSettings(PORTAL_ID);
      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(plainProblem(500, 'The server could not complete the request.', TRACE_ID), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      expect(heldFailure(store.settingsFailure(), 'settings').status).toBe(500);
      expect(store.listFailure())
        .withContext('each concern holds its own failure; one faulting does not blank another')
        .toBeNull();
      expect(store.portals().length).toBe(1);
      expect(store.settings()).toBeNull();
    });
  });


  // =========================================================================
  // HOST-NAME ALIASES — deliberately unpaged
  // =========================================================================

  describe('host-name aliases (unpaged)', () => {
    it('reads the alias collection with no query string at all', () => {
      store.loadAliases(PORTAL_ID);

      // ⚠️ THE STRING MATCHER IS THE ASSERTION. `expectOne` filters on the address WITH
      // its query string, so matching the bare path proves both the path and the total
      // absence of a parameter in one step. That is the direct proof of being unpaged: no
      // page index, no page size, no ordering and no filter is sent, because the server
      // binds none of them and a parameter it does not bind is discarded in silence.
      const read = httpMock.expectOne(PORTAL_ALIASES_URL);

      expect(read.request.method).toBe('GET');
      expect(read.request.url).toBe('/api/v1/portals/3/aliases');
      expectNoQueryString(read, 'an unpaged alias read');

      const collection: readonly PortalAlias[] = [
        portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost'),
        portalAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID, 'localhost:4200'),
      ];

      read.flush(envelope(collection));

      const held: readonly PortalAlias[] = heldAliases(store.aliases());

      expect(Array.isArray(held))
        .withContext('a bare array reaches the store, never a paged envelope')
        .toBeTrue();
      expect(held).toEqual(collection);
      expect(store.aliasesLoaded()).toBeTrue();
      expect(store.aliasCount()).toBe(2);
      expect(store.aliasesPortalId())
        .withContext('the collection records which portal it belongs to')
        .toBe(3);
      expect(store.aliasLoading()).toBeFalse();
    });

    it('holds no page index, no page size and no total for the alias collection', () => {
      // MIGRATION 9. Asserted structurally as well as over the wire, because the wire
      // assertion above would still pass if the store invented client-side coordinates it
      // never transmitted — and a component reading those would page a collection that
      // arrives whole.
      const aliasPaging: readonly string[] = memberNames(store).filter((name: string) => {
        const lowered: string = name.toLowerCase();

        return (
          lowered.includes('alias') &&
          (lowered.includes('page') || lowered.includes('total') || lowered.includes('size'))
        );
      });

      expect(aliasPaging)
        .withContext('the alias collection carries no paging coordinate of any kind')
        .toEqual([]);
    });

    it('distinguishes a collection never read from one read and found empty', () => {
      expect(store.aliases()).withContext('never read').toBeNull();
      expect(store.aliasesLoaded()).toBeFalse();
      expect(store.aliasCount())
        .withContext('and reports no count, which is a different fact from a count of nought')
        .toBeNull();

      store.loadAliases(PORTAL_ID);
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([]));

      expect(store.aliases()).withContext('read, and there are none').toEqual([]);
      expect(store.aliasesLoaded()).toBeTrue();
      expect(store.aliasCount()).toBe(0);
    });

    it('appends a created alias to the collection already in hand', () => {
      store.loadAliases(PORTAL_ID);
      const existing = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([existing]));

      const composed = createAliasRequest('contoso.example.test');
      const created: PortalAlias[] = [];

      store.createAlias(PORTAL_ID, composed).subscribe((alias: PortalAlias) => {
        created.push(alias);
      });

      const posted = httpMock.expectOne(PORTAL_ALIASES_URL);

      expect(posted.request.method).toBe('POST');
      expect(posted.request.body).toBe(composed);
      expect(Object.keys(posted.request.body))
        .withContext('the request carries the host name and nothing else')
        .toEqual(['httpAlias']);

      const stored = portalAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID, 'contoso.example.test');

      posted.flush(envelope(stored), { status: 201, statusText: 'Created' });

      const held: readonly PortalAlias[] = heldAliases(store.aliases());

      expect(held.map((alias: PortalAlias) => alias.portalAliasId))
        .withContext('appended to the collection already in hand, with no second read')
        .toEqual([PORTAL_ALIAS_ID, OTHER_PORTAL_ALIAS_ID]);
      expect(store.selectedAliasId()).toBe(OTHER_PORTAL_ALIAS_ID);
      expect(heldRecord(store.selectedAlias(), 'selected alias')).toEqual(stored);
      expect(created.length).toBe(1);
      expect(store.aliasLoading()).toBeFalse();

      // No re-read follows a creation: the server answered with the stored record, so
      // asking for the collection again would ask for what is already in hand.
    });

    it('re-reads the collection after an update answered with no body', () => {
      store.loadAliases(PORTAL_ID);
      httpMock
        .expectOne(PORTAL_ALIASES_URL)
        .flush(envelope([portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost')]));

      const composed = updateAliasRequest('renamed.example.test');
      let notifications = 0;

      store.updateAlias(PORTAL_ID, PORTAL_ALIAS_ID, composed).subscribe(() => {
        notifications += 1;
      });

      const written = httpMock.expectOne(PORTAL_ALIAS_URL);

      expect(written.request.method).toBe('PUT');
      expect(written.request.url).toBe('/api/v1/portals/3/aliases/7');
      expect(written.request.body).toBe(composed);

      // This is the sole write in the resource that answers with no body, so the record
      // has to be read back. That sequencing is the store's to perform — a transport may
      // not chain two calls — and it is the reason the two-request shape below exists.
      written.flush(null, { status: 204, statusText: 'No Content' });

      const reread = httpMock.expectOne(PORTAL_ALIASES_URL);

      expect(reread.request.method).toBe('GET');
      reread.flush(
        envelope([portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'renamed.example.test')]),
      );

      expect(heldAliases(store.aliases())[0].httpAlias)
        .withContext('the collection now reports what the server stored')
        .toBe('renamed.example.test');
      expect(notifications).toBe(1);
      expect(store.aliasLoading()).toBeFalse();
    });

    it('removes an unbound alias from the collection already in hand and clears its selection', () => {
      store.loadAliases(PORTAL_ID);
      const kept = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');
      const going = portalAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID, 'localhost:4200');
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([kept, going]));

      store.selectAlias(OTHER_PORTAL_ALIAS_ID);
      expect(store.selectedAliasId()).toBe(OTHER_PORTAL_ALIAS_ID);

      let removals = 0;

      store.deleteAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID).subscribe(() => {
        removals += 1;
      });

      const removed = httpMock.expectOne(OTHER_PORTAL_ALIAS_URL);

      expect(removed.request.method).toBe('DELETE');
      expectNoQueryString(removed, 'an alias removal');
      removed.flush(null, { status: 204, statusText: 'No Content' });

      expect(heldAliases(store.aliases()).map((alias: PortalAlias) => alias.portalAliasId))
        .withContext('removed from the collection in hand, with no second read')
        .toEqual([PORTAL_ALIAS_ID]);
      expect(store.selectedAliasId())
        .withContext('an unbound alias cannot remain selected')
        .toBeUndefined();
      expect(store.selectedAlias()).toBeNull();
      expect(removals).toBe(1);
    });

    it('reads one alias by its own path, with the identifier named as a segment', () => {
      store.loadAlias(PORTAL_ID, PORTAL_ALIAS_ID);

      const read = httpMock.expectOne(PORTAL_ALIAS_URL);

      expect(read.request.method).toBe('GET');
      // The legacy edit screen addressed this row through an abbreviated query key; the
      // target names it in full as a path segment. `expectNoQueryString` asserts the
      // abbreviated key absent by name.
      expectNoQueryString(read, 'a single-alias read');

      const stored = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');

      read.flush(envelope(stored));

      expect(store.selectedAliasId()).toBe(PORTAL_ALIAS_ID);
      expect(heldRecord(store.aliasDetail(), 'alias detail')).toEqual(stored);
      expect(heldRecord(store.selectedAlias(), 'selected alias'))
        .withContext('the derived view resolves through the record fetched, absent a collection')
        .toEqual(stored);
      expect(store.selectedPortalId())
        .withContext('reading an alias selects the portal that owns it')
        .toBe(3);
    });

    it('surfaces the duplicate-host-name refusal verbatim, as one dotted value', () => {
      store.loadAliases(PORTAL_ID);
      httpMock
        .expectOne(PORTAL_ALIASES_URL)
        .flush(envelope([portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost')]));

      store.createAlias(PORTAL_ID, createAliasRequest('localhost'));

      httpMock.expectOne(PORTAL_ALIASES_URL).flush(
        reasonedProblem(
          DUPLICATE_ALIAS_REFUSAL,
          409,
          'The Portal Alias Name You Specified Already Exists. Please Choose A Different Portal Alias.',
        ),
        { status: 409, statusText: 'Conflict' },
      );

      const failure: PortalFailure = heldFailure(store.aliasFailure(), 'alias');
      const surfaced: string | null = failure.conflictCode;

      // Divergence D-A again: BOTH legacy resource keys — the one the signup screen read
      // and the one the alias editor read — collapse onto this single published code,
      // because the server reports one code for the collision however it was reached.
      expect(surfaced).withContext('the published code, verbatim').toBe(DUPLICATE_ALIAS_REFUSAL);
      expect(surfaced).not.toBe('portal');
      expect(surfaced).not.toBe('alias_duplicate');
      expect(failure.status).toBe(409);
      expect(heldAliases(store.aliases()).length)
        .withContext('the collection is unchanged: nothing was appended, because nothing was stored')
        .toBe(1);
      expect(store.aliasLoading()).toBeFalse();
    });
  });

  // =========================================================================
  // FAILURES — the structured document, classified once and held whole
  // =========================================================================

  // =========================================================================
  // WRITES REPORT THROUGH A TICKET, NOT THROUGH A RETAINED CALLBACK
  //
  // Every write here used to take an optional continuation and invoke it from inside its
  // response handler. This store is provided at the ROOT, so it outlives every screen that
  // calls it, and a callback handed to it is a closure over a COMPONENT — over that
  // component's signals, its router and its notification service. Retaining one made a
  // root-lived object hold a destroyed component's scope, and a response arriving after the
  // operator had navigated away ran that component's continuation anyway: announcing a success
  // into a screen that was gone and, where the continuation navigated, moving a route nobody
  // had asked to move. The component could not see the reference, so it could not cancel it.
  //
  // It was also the only store in this application that did this.
  //
  // Each write now returns an `AsyncSubject`-backed ticket. The three properties asserted
  // below are the ones that make it a safe replacement rather than merely a different shape.
  // =========================================================================

  describe('write outcome tickets', () => {
    it('takes NO callback argument on any of the seven writes', () => {
      // ⚠ ARITY IS THE CONTRACT, and asserting it is what stops a continuation creeping back
      // in. Each count is the write's real arguments with no trailing callback slot.
      expect(store.createPortal.length).withContext('createPortal(request)').toBe(1);
      expect(store.updatePortal.length).withContext('updatePortal(id, request)').toBe(2);
      expect(store.deletePortal.length).withContext('deletePortal(id)').toBe(1);
      expect(store.saveSettings.length).withContext('saveSettings(id, request)').toBe(2);
      expect(store.createAlias.length).withContext('createAlias(id, request)').toBe(2);
      expect(store.updateAlias.length)
        .withContext('updateAlias(id, aliasId, request)')
        .toBe(3);
      expect(store.deleteAlias.length).withContext('deleteAlias(id, aliasId)').toBe(2);
    });

    it('emits the outcome ONCE, and only after the state has settled', () => {
      // ⚠ THE ORDERING PROPERTY. The value is published as the LAST act of the response
      // handler, so a continuation can never observe a half-finished write. Here the
      // continuation reads the store back and finds it already correct.
      const observedSelection: (number | undefined)[] = [];
      const emissions: PortalDetail[] = [];
      let completions = 0;

      store.createPortal(createPortalRequest()).subscribe({
        next: (created: PortalDetail) => {
          emissions.push(created);
          observedSelection.push(store.selectedPortalId());
        },
        complete: () => {
          completions += 1;
        },
      });

      httpMock
        .expectOne(PORTALS_URL)
        .flush(envelope(portalDetail(0)), { status: 201, statusText: 'Created' });

      expect(emissions.length).withContext('exactly one outcome per write').toBe(1);
      expect(completions).toBe(1);
      expect(observedSelection)
        .withContext('the continuation sees settled state, not state mid-update')
        .toEqual([0]);
      expect(store.detailLoading()).toBeFalse();

      settleListingReread('the listing re-read that follows a creation');
    });

    it('COMPLETES WITHOUT EMITTING when the write fails, and does not error', () => {
      // ⚠ THE FAILURE CONTRACT. Erroring would hand every caller an unhandled rejection to
      // guard against and would report the same failure TWICE, because a failure already
      // reaches the screen through the failure slice below. Completing empty means the success
      // continuation simply does not run, which is the whole of what a caller needs.
      let succeeded = 0;
      let errored = 0;
      let completed = 0;

      store.createPortal(createPortalRequest()).subscribe({
        next: () => {
          succeeded += 1;
        },
        error: () => {
          errored += 1;
        },
        complete: () => {
          completed += 1;
        },
      });

      httpMock.expectOne(PORTALS_URL).flush(
        { title: 'Conflict', status: 409, detail: 'That host name is already bound.' },
        { status: 409, statusText: 'Conflict' },
      );

      expect(succeeded).withContext('the success continuation must not run').toBe(0);
      expect(errored).withContext('and the caller is handed no error to catch').toBe(0);
      expect(completed).withContext('the ticket still settles, so nothing waits forever').toBe(1);

      // The failure keeps its single existing route to the operator.
      expect(store.detailFailure()).withContext('reported once, through the slice').not.toBeNull();
      expect(store.detailLoading()).toBeFalse();
    });

    it('UPDATES STATE EVEN WHEN NOBODY SUBSCRIBES, so ignoring the ticket is safe', () => {
      // ⚠ WHAT MAKES THE LISTING SCREEN'S IGNORED RETURN VALUE CORRECT. The store subscribes to
      // the transport itself, so the request is issued and every slice is written whether or not
      // a caller is listening. A ticket that only ran its effects on subscription would turn
      // every unsubscribed write into a silent no-op.
      readListing(portalPage([portalListItem(SEEDED_PORTAL_ID, 'First portal')], 0, 10, 1, 1), 'a listing');

      store.deletePortal(SEEDED_PORTAL_ID);

      const removed = httpMock.expectOne(SEEDED_PORTAL_URL);

      expect(removed.request.method)
        .withContext('the request went out with no subscriber at all')
        .toBe('DELETE');

      removed.flush(null, { status: 204, statusText: 'No Content' });

      expect(store.portals().length).withContext('and the optimistic edit still ran').toBe(0);
      expect(store.detailLoading()).toBeFalse();

      settleListingReread('the listing re-read that follows a removal');
    });

    it('REPLAYS to a subscriber that arrives after the response, which a plain Subject would drop', () => {
      // ⚠ WHY AN `AsyncSubject` AND NOT A `Subject`. A caller subscribes after the command
      // returns, and a transport answering synchronously would already have completed a plain
      // subject by then — silently dropping the outcome and, in production, a screen's success
      // notification. This case forces that ordering: the response is flushed BEFORE anybody
      // subscribes.
      const ticket = store.createPortal(createPortalRequest());

      httpMock
        .expectOne(PORTALS_URL)
        .flush(envelope(portalDetail(0)), { status: 201, statusText: 'Created' });

      const received: PortalDetail[] = [];

      ticket.subscribe((created: PortalDetail) => {
        received.push(created);
      });

      expect(received.length).withContext('the late subscriber still gets its outcome').toBe(1);
      expect(received[0].portalId).toBe(0);

      settleListingReread('the listing re-read that follows a creation');
    });

    it('gives each write its OWN ticket, so two writes cannot cross-report', () => {
      // One ticket per operation. A shared subject would deliver the second write's outcome to
      // the first write's continuation — the same class of cross-record confusion the
      // continuations themselves caused.
      const firstOutcomes: PortalDetail[] = [];
      const secondOutcomes: PortalDetail[] = [];

      store
        .updatePortal(PORTAL_ID, updatePortalRequest({ portalId: PORTAL_ID, portalName: 'First' }))
        .subscribe((stored: PortalDetail) => {
          firstOutcomes.push(stored);
        });
      store
        .updatePortal(PORTAL_ID, updatePortalRequest({ portalId: PORTAL_ID, portalName: 'Second' }))
        .subscribe((stored: PortalDetail) => {
          secondOutcomes.push(stored);
        });

      const writes = httpMock.match(
        (candidate) => candidate.method === 'PUT' && candidate.url === PORTAL_URL,
      );

      expect(writes.length).withContext('a write is never superseded by a later one').toBe(2);

      writes[0].flush(envelope(portalDetail(PORTAL_ID, { portalName: 'First' })));
      writes[1].flush(envelope(portalDetail(PORTAL_ID, { portalName: 'Second' })));

      expect(firstOutcomes.map((entry) => entry.portalName)).toEqual(['First']);
      expect(secondOutcomes.map((entry) => entry.portalName)).toEqual(['Second']);

      for (const reread of httpMock.match(
        (candidate) => candidate.method === 'GET' && candidate.url === PORTALS_URL,
      )) {
        if (!reread.cancelled) {
          reread.flush(portalPage([], 0, 10, 0, 0));
        }
      }
    });

    it('lets a caller UNSUBSCRIBE, which a retained callback gave no way to do', () => {
      // ⚠ THE PROPERTY THE WHOLE CHANGE EXISTS FOR. A component binds its subscription to its
      // own lifetime, so a response arriving after teardown reaches nothing. With a retained
      // callback there was no handle and therefore no way to express this at all — the
      // continuation ran regardless, into a component that no longer existed.
      let ran = 0;

      const subscription = store.createPortal(createPortalRequest()).subscribe(() => {
        ran += 1;
      });

      // The screen goes away while the write is in flight.
      subscription.unsubscribe();

      httpMock
        .expectOne(PORTALS_URL)
        .flush(envelope(portalDetail(0)), { status: 201, statusText: 'Created' });

      expect(ran).withContext("the departed screen's continuation does not run").toBe(0);

      // The store's own state still settled, because its subscription is its own.
      expect(store.selectedPortalId()).toBe(0);
      expect(store.detailLoading()).toBeFalse();

      settleListingReread('the listing re-read that follows a creation');
    });
  });

  describe('failures', () => {
    it('retains the trace identifier, and reports it as the support reference when it stands alone', () => {
      store.loadPortals();

      expectListing('a listing that faults').flush(
        plainProblem(500, 'The server could not complete the request.', TRACE_ID),
        { status: 500, statusText: 'Internal Server Error' },
      );

      const failure: PortalFailure = heldFailure(store.listFailure(), 'listing');
      const document: ProblemDetails = heldProblem(failure);

      expect(document.traceId)
        .withContext('the join key between a browser report and the request the server logged')
        .toBe(TRACE_ID);
      expect(failure.supportReference)
        .withContext('with no correlation identifier present, the trace identifier is quoted instead')
        .toBe(TRACE_ID);
      expect(failure.status).toBe(500);
      expect(failure.severity).toBe('error');
      expect(failure.conflictCode)
        .withContext('a fault carries no state-refusal code')
        .toBeNull();
      expect(failure.validation).toBeNull();
      expect(store.listLoading()).toBeFalse();
      expect(store.hasFailure()).toBeTrue();
    });

    it('retains both identifiers and prefers the correlation one as the support reference', () => {
      store.deletePortal(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_URL)
        .flush(reasonedProblem(LAST_PORTAL_REFUSAL, 409, 'Refused'), {
          status: 409,
          statusText: 'Conflict',
        });

      const failure: PortalFailure = heldFailure(store.detailFailure(), 'single-portal');
      const document: ProblemDetails = heldProblem(failure);

      expect(document.traceId).withContext('both survive on the held document').toBe(TRACE_ID);
      expect(document.correlationId).toBe(CORRELATION_ID);
      expect(failure.supportReference)
        .withContext('and the correlation identifier is the one an operator quotes')
        .toBe(CORRELATION_ID);
      expect(document.instance)
        .withContext('the document is held WHOLE, so its other members survive too')
        .toBe(PORTALS_URL);
      expect(document.title).toBe('Request refused');
    });

    it('classifies a refused request at warning severity rather than as a fault', () => {
      store.loadSettings(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(plainProblem(403, 'You do not have permission to perform this action.', TRACE_ID), {
          status: 403,
          statusText: 'Forbidden',
        });

      const failure: PortalFailure = heldFailure(store.settingsFailure(), 'settings');

      // MIGRATION 14: `AccessDenied.ascx.vb` performs no permission check at all and BOTH
      // of its branches render at the yellow warning message type — `:L43` for the message
      // handed in through the query string and `:L45` for the localised default. The legacy
      // application is therefore the authority for this classification, not a house style.
      expect(failure.status).toBe(403);
      expect(failure.severity)
        .withContext('a refusal is a caution: the system is working exactly as configured')
        .toBe('warning');
      expect(failure.severity)
        .withContext('and never danger styling, which would report a fault that has not occurred')
        .not.toBe('error');
    });

    it('classifies a portal that does not exist at warning severity', () => {
      store.loadPortal(PORTAL_ID);

      httpMock
        .expectOne(PORTAL_URL)
        .flush(plainProblem(404, 'The requested item could not be found.', TRACE_ID), {
          status: 404,
          statusText: 'Not Found',
        });

      const failure: PortalFailure = heldFailure(store.detailFailure(), 'single-portal');

      expect(failure.status).toBe(404);
      expect(failure.severity).toBe('warning');
      expect(store.selectedPortal())
        .withContext('and nothing is invented to stand in for the record that is not there')
        .toBeNull();
    });

    it('narrows a per-field document and reads its dictionary by bracket', () => {
      store.createPortal(createPortalRequest({ portalName: '' }));

      httpMock.expectOne(PORTALS_URL).flush(
        validationProblem('One or more validation failures occurred.', {
          PortalName: ['The Site Name is required.'],
          'Administrator.Email': ['A valid email address is required.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );

      const failure: PortalFailure = heldFailure(store.detailFailure(), 'single-portal');
      const reported: ValidationProblemDetails = heldValidation(failure);

      // ⚠️ BRACKET ACCESS THROUGHOUT. The dictionary is an index signature and this
      // workspace enables the compiler option that makes dot access on one a compile
      // error, deliberately — so that a lookup is visibly a lookup.
      expect(reported.errors['PortalName']).toBeDefined();
      expect(reported.errors['PortalName']).toEqual(['The Site Name is required.']);
      expect(reported.errors['portalName'])
        .withContext('the keys name model members and are NOT camel-cased')
        .toBeUndefined();
      expect(reported.errors['Administrator.Email'])
        .withContext('a dotted key is one key, not a path into a nested structure')
        .toEqual(['A valid email address is required.']);
      expect(Object.keys(reported.errors).length).toBe(2);
      expect(failure.status).toBe(400);
      expect(failure.severity).toBe('error');
      expect(failure.conflictCode)
        .withContext('a per-field rejection is not one of the published state refusals')
        .toBeNull();
      expect(store.selectedPortal()).toBeNull();
    });

    it('holds message text as an inert plain string, neither escaped nor wrapped', () => {
      // ⚠️ THE WORDING RELAYED BY THIS API DESCENDS FROM LEGACY RESOURCE FILES IN WHICH
      // RAW MARKUP IS COMMONPLACE — anchors, list items, paragraphs, line breaks, and in a
      // handful of values a script block, one of them in the portal tree's own settings
      // resource file. That text is therefore untrusted, and the store's obligation is to
      // hold it unaltered rather than to launder it: escaping belongs to the renderer, and
      // the framework's default text interpolation escapes by construction. So the store
      // exposes no pre-sanitised member, no trusted-markup wrapper and no sanitiser call.
      const hostile = '<script>alert(1)</script>';

      store.loadPortals();
      expectListing('a listing whose failure carries markup').flush(
        plainProblem(500, hostile, TRACE_ID),
        { status: 500, statusText: 'Internal Server Error' },
      );

      const document: ProblemDetails = heldProblem(
        heldFailure(store.listFailure(), 'listing'),
      );

      expect(typeof document.detail)
        .withContext('a plain string, not a wrapped or pre-escaped value')
        .toBe('string');
      expect(document.detail)
        .withContext('held exactly as received, character for character')
        .toBe(hostile);
      expect(document.detail)
        .withContext('nothing was stripped out of it')
        .toContain('<script>');
    });

    it('holds a legacy break-tag prefix as received, in either spelling', () => {
      // The legacy screens prefixed a message with a line break in BOTH spellings, and the
      // portal tree is the source of the unclosed one: `Signup.ascx.vb` writes `"<br>"` at
      // its validation sites and again when composing the label, while the account screen
      // writes `"<br/>"`. Stripping either is the business of the form-error helper that
      // renders a message; the store holds the structured document it received.
      store.loadPortals();
      expectListing('a failure prefixed with the unclosed spelling').flush(
        plainProblem(500, '<br>Invalid Site Name', TRACE_ID),
        { status: 500, statusText: 'Internal Server Error' },
      );

      expect(heldProblem(heldFailure(store.listFailure(), 'listing')).detail)
        .withContext('unstripped, prefix and all')
        .toBe('<br>Invalid Site Name');

      store.clearFailures();
      store.reloadPortals();
      expectListing('a failure prefixed with the closed spelling').flush(
        plainProblem(500, '<br/>Invalid Site Name', TRACE_ID),
        { status: 500, statusText: 'Internal Server Error' },
      );

      expect(heldProblem(heldFailure(store.listFailure(), 'listing')).detail).toBe(
        '<br/>Invalid Site Name',
      );
    });

    it('holds a message that accumulated several break tags without collapsing them', () => {
      // In `Signup.ascx.vb` the break is appended INSIDE a per-character loop, so a portal
      // name holding several rejected characters accumulates one prefixed message per
      // character. A store that collapsed or de-duplicated them would misreport how many
      // characters the server objected to.
      const accumulated = '<br>Invalid Site Name<br>Invalid Site Name<br>Invalid Site Name';

      store.createPortal(createPortalRequest({ portalName: 'a b/c' }));
      httpMock
        .expectOne(PORTALS_URL)
        .flush(plainProblem(400, accumulated, TRACE_ID), {
          status: 400,
          statusText: 'Bad Request',
        });

      const detail: string | undefined = heldProblem(
        heldFailure(store.detailFailure(), 'single-portal'),
      ).detail;

      expect(detail).withContext('all three, in order, unaltered').toBe(accumulated);
      expect(detail).toBeDefined();
      if (detail !== undefined) {
        expect(detail.split('<br>').length - 1)
          .withContext('three prefixes, one per rejected character')
          .toBe(3);
      }
    });

    it('reports a failure that arrived with no document at all, rather than inventing one', () => {
      store.loadPortals();

      // A transport status of nought is what an unreachable server looks like, and the
      // framework puts a progress event in the body slot when no response arrived. A
      // progress event carries a string member the document predicate would otherwise
      // accept, so mistaking one for a document would hand every consumer a title and a
      // detail that were never sent.
      expectListing('a listing that never reached the server').error(
        new ProgressEvent('error'),
        { status: 0, statusText: 'Unknown Error' },
      );

      const failure: PortalFailure = heldFailure(store.listFailure(), 'listing');

      expect(failure.problem)
        .withContext('no document is manufactured for a request that never arrived')
        .toBeNull();
      expect(failure.status).toBe(0);
      expect(failure.supportReference).toBeNull();
      expect(failure.conflictCode).toBeNull();
      expect(failure.validation).toBeNull();
      expect(failure.severity)
        .withContext('an unreachable server is a fault')
        .toBe('error');
      expect(store.listLoading()).toBeFalse();
    });

    it('clears every failure slice on request, issuing no request of its own', () => {
      store.loadPortals();
      expectListing('a listing that faults').flush(plainProblem(500, 'Faulted', TRACE_ID), {
        status: 500,
        statusText: 'Internal Server Error',
      });
      store.loadSettings(PORTAL_ID);
      httpMock
        .expectOne(PORTAL_SETTINGS_URL)
        .flush(plainProblem(500, 'Faulted', TRACE_ID), {
          status: 500,
          statusText: 'Internal Server Error',
        });

      expect(store.hasFailure()).toBeTrue();

      store.clearFailures();

      expect(store.listFailure()).toBeNull();
      expect(store.detailFailure()).toBeNull();
      expect(store.settingsFailure()).toBeNull();
      expect(store.aliasFailure()).toBeNull();
      expect(store.hasFailure()).toBeFalse();

      // Clearing a failure re-reads nothing: an operator dismissing a message has not
      // asked for the data again. `verify()` asserts it.
    });

    // NOTE ON THROTTLING, recorded rather than asserted: a too-many-requests answer
    // arrives from the sign-in endpoints and from no portal endpoint, so no case for it
    // appears above. Writing one would describe a response this resource cannot send, and
    // the sign-in store's own specification is where that classification belongs.
  });

  // =========================================================================
  // THE READ-ONLY SURFACE
  // =========================================================================

  describe('read-only surface', () => {
    /**
     * Every public state slice, by name, so that a failure says which one leaked.
     *
     * @param subject The store under assertion.
     * @returns Each slice against the name it is published under.
     */
    function publishedSlices(subject: PortalStore): ReadonlyMap<string, object> {
      return new Map<string, object>([
        ['page', subject.page],
        ['pageIndex', subject.pageIndex],
        ['requestedPageSize', subject.requestedPageSize],
        ['nameFilter', subject.nameFilter],
        ['sortBy', subject.sortBy],
        ['sortDir', subject.sortDir],
        ['listLoading', subject.listLoading],
        ['listFailure', subject.listFailure],
        ['selectedPortalId', subject.selectedPortalId],
        ['selectedPortal', subject.selectedPortal],
        ['detailLoading', subject.detailLoading],
        ['detailFailure', subject.detailFailure],
        ['settings', subject.settings],
        ['settingsLoading', subject.settingsLoading],
        ['settingsFailure', subject.settingsFailure],
        ['aliases', subject.aliases],
        ['aliasesPortalId', subject.aliasesPortalId],
        ['selectedAliasId', subject.selectedAliasId],
        ['aliasDetail', subject.aliasDetail],
        ['aliasLoading', subject.aliasLoading],
        ['aliasFailure', subject.aliasFailure],
      ]);
    }

    /**
     * Every derived view, by name.
     *
     * @param subject The store under assertion.
     * @returns Each view against the name it is published under.
     */
    function publishedViews(subject: PortalStore): ReadonlyMap<string, object> {
      return new Map<string, object>([
        ['portals', subject.portals],
        ['listMeta', subject.listMeta],
        ['totalCount', subject.totalCount],
        ['servedPageSize', subject.servedPageSize],
        ['totalPages', subject.totalPages],
        ['servedPageIndex', subject.servedPageIndex],
        ['isListEmpty', subject.isListEmpty],
        ['isPastEnd', subject.isPastEnd],
        ['pagerRequired', subject.pagerRequired],
        ['sort', subject.sort],
        ['isFiltered', subject.isFiltered],
        ['hasSelection', subject.hasSelection],
        ['aliasesLoaded', subject.aliasesLoaded],
        ['aliasCount', subject.aliasCount],
        ['selectedAlias', subject.selectedAlias],
        ['busy', subject.busy],
        ['hasFailure', subject.hasFailure],
      ]);
    }

    it('the probe discriminates: a writable signal exposes both mutating members', () => {
      // POSITIVE CONTROL, and without it the two negative cases below would be
      // unfalsifiable — a probe that always answered false would pass them both.
      const writable = signal<number>(0);
      const observed = mutatingMembers(writable);

      expect(observed.setter).withContext('a writable signal carries a setter').toBeTrue();
      expect(observed.updater).withContext('and an updater').toBeTrue();
    });

    it('exposes no setter and no updater on any of the twenty-one state slices', () => {
      const slices: ReadonlyMap<string, object> = publishedSlices(store);

      expect(slices.size).withContext('every published slice is covered').toBe(21);

      for (const [name, slice] of slices) {
        const observed = mutatingMembers(slice);

        expect(observed.setter).withContext(`${name} exposes no setter`).toBeFalse();
        expect(observed.updater).withContext(`${name} exposes no updater`).toBeFalse();
      }
    });

    it('exposes no setter and no updater on any of the seventeen derived views', () => {
      const views: ReadonlyMap<string, object> = publishedViews(store);

      expect(views.size).withContext('every published view is covered').toBe(17);

      for (const [name, view] of views) {
        const observed = mutatingMembers(view);

        expect(observed.setter).withContext(`${name} exposes no setter`).toBeFalse();
        expect(observed.updater).withContext(`${name} exposes no updater`).toBeFalse();
      }
    });

    it('replaces the collection it holds rather than editing the one a caller already read', () => {
      // ⚠️ WHAT THIS CAN AND CANNOT PROVE, STATED PLAINLY. A read-only array declaration is
      // a compile-time guarantee, and attempting a run-time write to demonstrate its
      // absence would need a type assertion — forbidden here, and it would prove less than
      // this does anyway. What is proven instead is the property that actually matters: the
      // store derives its next collection from ITS OWN slice and publishes a NEW array, so
      // an array a consumer read earlier still reports what it reported. That is also what
      // makes an on-push consumer re-render, since identity change is the signal it reacts
      // to.
      store.loadAliases(PORTAL_ID);
      const kept = portalAlias(PORTAL_ID, PORTAL_ALIAS_ID, 'localhost');
      const going = portalAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID, 'localhost:4200');
      httpMock.expectOne(PORTAL_ALIASES_URL).flush(envelope([kept, going]));

      const readEarlier: readonly PortalAlias[] = heldAliases(store.aliases());

      expect(readEarlier.length).toBe(2);

      store.deleteAlias(PORTAL_ID, OTHER_PORTAL_ALIAS_ID);
      httpMock.expectOne(OTHER_PORTAL_ALIAS_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      const readLater: readonly PortalAlias[] = heldAliases(store.aliases());

      expect(readLater.length).toBe(1);
      expect(readLater).withContext('a NEW array, not the one edited in place').not.toBe(readEarlier);
      expect(readEarlier.length)
        .withContext('and the array read earlier still reports what it reported')
        .toBe(2);
      // Deep equality rather than identity, and the distinction is worth stating. The
      // transport now DECODES every response against the contract its model publishes, so
      // what the store holds is a value this client constructed after checking every
      // member — not a reference to whatever object the network handed it. Asserting
      // identity with the literal that was flushed would therefore be asserting the
      // absence of validation. What matters here is unchanged and is still proven: the
      // record the earlier array reports at that position is still the one that was
      // unbound, unaffected by the removal.
      expect(readEarlier[1])
        .withContext('holding the very record that was unbound')
        .toEqual(going);
      expect(store.aliasCount()).toBe(1);
    });

    it('replaces the page it holds rather than editing the rows a caller already read', () => {
      const first = portalListItem(SEEDED_PORTAL_ID, 'First portal');
      const second = portalListItem(SECOND_PORTAL_ID, 'Second portal');

      readListing(portalPage([first, second], 0, 10, 2, 1), 'the page that will be depleted');

      const rowsReadEarlier: readonly PortalListItem[] = store.portals();
      const pageReadEarlier = store.page();

      store.deletePortal(SECOND_PORTAL_ID);
      httpMock.expectOne(SECOND_PORTAL_URL).flush(null, {
        status: 204,
        statusText: 'No Content',
      });

      expect(store.portals()).not.toBe(rowsReadEarlier);
      expect(store.page()).withContext('a new envelope as well as a new array').not.toBe(pageReadEarlier);
      expect(rowsReadEarlier.length).toBe(2);
      expect(pageReadEarlier.items.length).toBe(2);
      expect(store.listMeta())
        .withContext('while the coordinates the server reported are carried forward untouched')
        .toEqual(pageReadEarlier.meta);

      settleListingReread('the listing re-read that follows a removal');
    });
  });
});
