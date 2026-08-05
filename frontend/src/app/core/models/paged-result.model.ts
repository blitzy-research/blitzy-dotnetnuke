/**
 * The paging, sorting and filtering contract shared by every collection endpoint,
 * together with the success-envelope companions that describe a response.
 *
 * This module is type declarations plus three small constants and two factories. It
 * imports nothing: the workspace configures neither `baseUrl` nor `paths`, so every
 * import in this application is relative, and a contract this low in the dependency
 * graph should not need one at all.
 *
 * ## The paging facts are NESTED under `meta`
 *
 * A collection endpoint serialises the server's boundary envelope, which carries the
 * rows on `items` and every paging fact on a single `meta` member:
 *
 * ```json
 * {
 *   "items": [ { "portalName": "Baseline Portal" } ],
 *   "meta": {
 *     "totalCount": 1,
 *     "pageIndex": 0,
 *     "pageSize": 10,
 *     "totalPages": 1
 *   }
 * }
 * ```
 *
 * {@link PagedResult} mirrors that arrangement, and {@link ApiMeta} mirrors the four
 * members of the metadata companion. **There is exactly one paged wire shape and
 * exactly one declaration of it here.** Two competing shapes — one nested, one with
 * the paging facts as siblings of `items` — would be indistinguishable to a type
 * checker and to an assertion on a successful response code, because reading a
 * member the body does not carry yields `undefined` at run time while compiling
 * perfectly. The nesting is therefore a load-bearing part of this contract, not a
 * formatting preference, and a consumer reads a coordinate through
 * {@link PagedResult.meta} and never off the envelope itself.
 *
 * The envelope carries the two members above and nothing else. No paging fact is
 * repeated at the top level, because two copies of one fact on a single response
 * give a pager two sources of truth and no way to choose between them when they
 * disagree.
 *
 * MIGRATION: the server draws a deliberate line between the paging envelope an
 * application service returns and the contract a controller publishes, and only the
 * latter reaches this file. That is what lets the internal envelope change without
 * breaking a client, and it is why the internal envelope's three derived navigation
 * members — an unpaged marker and a previous-page and next-page flag — appear
 * nowhere in this module. A consumer needing "is there a further page" compares
 * {@link ApiMeta.pageIndex} against `totalPages - 1`; nothing here recomputes a fact
 * the server already published.
 *
 * ## Identifiers inside a page may be zero or negative
 *
 * A consumer iterating {@link PagedResult.items} must not treat an identifier as
 * absent by testing its truthiness or its sign. The legacy identity seeds make both
 * zero and minus one legitimate keys: the portal table seeds its identity at minus
 * one while the role, page and module tables seed theirs at zero
 * (`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider` L77,
 * L115, L140 and L221). A live listing of portals really does return a portal whose
 * identifier is minus one, and a live listing of roles really does return a role
 * whose identifier is zero. Absence is expressed by a member being absent, so test
 * for `undefined` or `null` explicitly and never by comparing against a number.
 *
 * ## MIGRATION notes
 *
 * MIGRATION: Page indexing is ZERO-BASED across this contract, and the base is
 * stated on every member that carries it. The legacy stack was split on the
 * question and wrote neither base down. The Web Forms account screen counted from
 * one — `Website/admin/Users/Users.ascx.vb` L51 declares
 * `Private _CurrentPage As Integer = 1` — and then subtracted one on every call
 * down to the provider, at L265, L269, L271 and L274, exactly as the portal screen
 * did at `Website/admin/Portal/Portals.ascx.vb` L142. The shipped schema settles
 * the provider's base independently, because the paging procedures set a row lower
 * bound to the page size multiplied by the page index
 * (`Website/Providers/DataProviders/SqlDataProvider/03.01.01.SqlDataProvider` L38),
 * so index zero addresses the first row. This contract carries the DATA layer's
 * base, matching what the domain envelope documents as "a fixed part of its
 * contract". The one-based counter survives only as a presentation concern inside
 * the shared pagination component, which is mapped in the feature stores and is
 * never a wire fact. A disagreement here would neither fail to compile nor fail a
 * test asserting a successful response code; it would quietly serve the
 * neighbouring page.
 *
 * MIGRATION: The legacy "return everything, unpaged" call shape is deliberately NOT
 * carried forward. It was expressed by handing the integer null sentinel of minus
 * one to the page index, the page size and the total alike —
 * `Library/Components/Users/UserController.vb` L687 reads
 * `Return GetUsers(portalId, False, ...)` with that sentinel repeated three times,
 * and L706 repeats it — where the sentinel is the value
 * `Library/Components/Shared/Null.vb` L41 defines and that module's own absence
 * test reports as absent. A negative page coordinate was therefore
 * indistinguishable from a missing one. No negative coordinate is sent or expected
 * anywhere in this contract; {@link unpagedResult} names the unpaged case instead,
 * and the server rejects a negative page index outright.
 *
 * MIGRATION: {@link PagedRequest.query} holds the caller's RAW text. The legacy
 * prefix match was composed at the call site — the account screen appended a
 * trailing per-cent wildcard to the search text before calling down
 * (`Website/admin/Users/Users.ascx.vb` L269, L271 and L274), and the portal screen
 * did the same at `Website/admin/Portal/Portals.ascx.vb` L142. Pattern composition
 * now belongs behind the repository interfaces, so neither this contract, nor the
 * shared search input, nor the query-string helper may decorate the value.
 *
 * Server-side matching is NOT one uniform semantic, and must not be described as one.
 * The general-purpose {@link PagedRequest.query} filter is a SUBSTRING match - the
 * portal name, and a user's username, display name and email are each matched with
 * `Contains` - while only the explicitly named username, email and profile-value
 * filters are PREFIX matches. Placeholder text must therefore promise neither.
 *
 * MIGRATION: {@link ApiMeta.totalCount} travels with the records rather than
 * arriving separately, and is a 32-bit integer rather than a wider type because
 * every legacy declaration of it is `Integer`. The legacy membership surface could
 * not answer "which page, and how many altogether?" in one call: its paged readers
 * at `Library/Providers/MembershipProviders/DataProvider/DataProvider.vb` L76, L83,
 * L84 and L86 each returned a forward-only reader carrying no total whatsoever,
 * forcing the independent count member at L82. Both facts now arrive together,
 * which is also what lets a client decide pager visibility the way the legacy
 * screen did at `Website/admin/Users/Users.ascx.vb` L279, by comparing the page
 * size against the total.
 *
 * MIGRATION: The shared pagination component accepts a ONE-BASED `page` input while
 * this contract carries a ZERO-BASED {@link ApiMeta.pageIndex}. That is an
 * explicit, deliberate mapping performed in the feature stores, not a defect and not
 * an inconsistency to be tidied away: a human-facing pager that began at page zero
 * would be wrong, and a wire index that began at one would contradict the data
 * layer. Neither side is to be "corrected" to match the other. The legacy stack drew
 * the same distinction in the same place, which is why its screen counted from one
 * and subtracted one immediately before calling down.
 *
 * MIGRATION: The four pre-generics collection wrappers in the legacy tree —
 * `PortalAliasCollection`, `ModulePermissionCollection`, `TabPermissionCollection`
 * and `ProfilePropertyDefinitionCollection` — are subsumed by `readonly T[]` here
 * and produce no counterpart of their own. The legacy listing members returned an
 * untyped `ArrayList` that declared neither an element type nor a total
 * (`Library/Components/Portal/PortalController.vb` L1263 and
 * `Library/Components/Users/UserController.vb` L685), which is what this generic
 * envelope replaces.
 *
 * MIGRATION: This envelope also retires the `ByRef totalRecords As Integer` status
 * argument, which handed a grand total back through the argument list beside an
 * untyped return value. Direct measurement of
 * `Library/Components/Users/UserController.vb` found EIGHT such overloads, at L725,
 * L746, L769, L793, L816, L840, L864 and L889, where the action plan records three.
 * The larger figure is reported as a refinement of the plan rather than as a
 * correction to it; the directive the plan states is unchanged, and nothing here was
 * decided on the strength of the count. A status argument is in any case not
 * expressible over HTTP, so the total has to travel inside the body or not at all.
 */

/**
 * The direction in which a sort field is applied.
 *
 * Declared here beside {@link PagedRequest}, its only consumer, mirroring the
 * server's own decision to nest the equivalent enumeration inside its request
 * contract rather than give two members a file of their own.
 *
 * The two tokens are the server's member names, reproduced verbatim because the
 * value is bound from the query string and the binder accepts the member name. An
 * abbreviated or lower-cased spelling is rejected: `sortDir=asc` is answered with
 * `400 Bad Request` and `The value 'asc' is not valid for SortDir.`, so the casing
 * below is not a style choice.
 *
 * A string-literal union rather than an enumeration: it needs no run-time
 * representation, and `isolatedModules` is enabled, which rules out the `const`
 * form of an enumeration that would otherwise avoid the emit.
 */
export type SortDirection = 'Ascending' | 'Descending';

/**
 * The paging, sorting and filtering arguments a collection endpoint accepts.
 *
 * Bound from the query string, so each member appears as a named parameter —
 * `?pageIndex=0&pageSize=10&sortBy=portalName&sortDir=Ascending&query=text`.
 *
 * Nothing here is corrected, coerced or clamped on either side of the wire. The
 * server enforces its bounds in a request validator and reports a violation as a
 * field-level `400` carrying an RFC 7807 problem document, so a caller learns that
 * a request was malformed instead of silently receiving a page it never asked for.
 * A client must therefore not rely on an out-of-range value being quietly
 * corrected, and in particular must never send the legacy sentinel of minus one for
 * a page coordinate.
 */
export interface PagedRequest {
  /**
   * The page of records to return.
   *
   * Zero-based: 0 is the first page. Must not be negative — a negative index is
   * rejected with `The page index may not be negative. Page indexes are
   * zero-based, so the first page is 0.` rather than reinterpreted, so the legacy
   * sentinel cannot arrive disguised as a page address.
   */
  readonly pageIndex: number;

  /**
   * The size of the page.
   *
   * Must be at least 1 and at most {@link MAX_PAGE_SIZE}; both zero and a negative
   * size are rejected. {@link DEFAULT_PAGE_SIZE} is the value to send when the
   * caller expresses no preference.
   */
  readonly pageSize: number;

  /**
   * The name of the field to order by, or omitted when the caller expresses no
   * preference and the server's own ordering applies.
   *
   * A field name, not an expression. The set of sortable names is decided by the
   * server, which answers an unrecognised name with a field-level `400`. Blank
   * text and omission mean the same thing.
   */
  readonly sortBy?: string;

  /**
   * The direction {@link sortBy} is applied in. Consulted only when a sort field is
   * present; `'Ascending'` is the server's default when omitted.
   */
  readonly sortDir?: SortDirection;

  /**
   * The caller's free-text filter, raw and exactly as typed, or omitted when no
   * filter applies.
   *
   * No wildcard, escape or pattern syntax belongs here — see the MIGRATION note at
   * the head of this module. Matching for THIS member is a SUBSTRING match, applied
   * case-insensitively after the server trims the value; the prefix-matching filters are
   * the separately named ones. The server bounds the length at
   * {@link QUERY_MAX_LENGTH} characters; blank text and omission mean the same thing.
   */
  readonly query?: string;
}

/**
 * One page of results together with the paging facts that describe where that page
 * sits within the whole match set.
 *
 * Mirrors the body a collection endpoint returns, member for member — see the shape
 * documented at the head of this module. Both members are present on every response:
 * the server omits only null members, and neither of these is ever null. An empty
 * page is a legitimate answer rather than an error, so a consumer distinguishes "past
 * the end of the set" from "nothing matched at all" by consulting
 * {@link ApiMeta.totalCount}, never by finding a member missing.
 *
 * Every member is `readonly`: a response has already happened, and a consumer that
 * could rewrite one would be describing something the server never said.
 *
 * @typeParam T The row contract carried on the page. Always a data transfer
 * contract, never a persisted entity — keeping entities off the wire is what allows
 * the legacy sentinel semantics to be honoured at the boundary without contaminating
 * the model behind it. Deliberately unconstrained, so a page of any row contract is
 * expressible and none must belong to a particular hierarchy.
 */
export interface PagedResult<T> {
  /**
   * The records on this page, in the order the query produced them.
   *
   * A materialised, read-only array. Read-only because the page is already final;
   * materialised because a lazily evaluated sequence could be walked twice with
   * different answers and would hide the very count this envelope exists to
   * publish.
   */
  readonly items: readonly T[];

  /**
   * The paging facts locating this page within the whole match set: the total across
   * every page, the zero-based page index, the page size the server applied and the
   * page count derived from them.
   *
   * The paging facts live here and ONLY here — they are never repeated as members of
   * this envelope. Reusing the shared metadata companion also means a collection
   * response describes its page exactly as the rest of the contract does.
   */
  readonly meta: ApiMeta;
}

/**
 * Metadata describing a successful response, rather than the payload it carries.
 *
 * The paging companion carried on {@link PagedResult.meta} by every collection
 * response, and the optional companion to {@link ApiResponse}. A collection response
 * populates every member; a payload with no page to describe omits the metadata
 * altogether, because a scalar genuinely has no total, page index or page size and
 * reporting zeroes for them would be indistinguishable from a real, empty first page.
 *
 * The member set is confined to the three facts the legacy pager consumed plus the
 * one value derived from them. The Web Forms account screen handed its pager exactly
 * total records, page size and current page, and the shared pagination component
 * that replaces it accepts the same three.
 *
 * This type describes SUCCESS ONLY. There is deliberately no outcome flag, no
 * message, no per-field validation map and no transport-level status code: an
 * expected failure is carried inside the server by its result types, and an
 * unexpected one is shaped into an RFC 7807 problem document at the boundary. See
 * `problem-details.model.ts` for the one and only wire error contract.
 *
 * MIGRATION: there is no correlation-identifier member, and the omission is
 * deliberate rather than an oversight. The correlation value travels in the
 * `X-Correlation-Id` header, written by the server's correlation middleware and read
 * by the matching client interceptor; repeating it in the body would create a second
 * source of truth that no consumer reads.
 */
export interface ApiMeta {
  /**
   * The total no of records that satisfy the criteria, counted across every page and
   * not only the page returned.
   *
   * Therefore NOT the length of {@link PagedResult.items}: the two coincide only when
   * the whole match set fits on the page in hand. This is the value a pager divides,
   * and the value an empty-state check consults — no records on the fourth page of
   * three means "past the end", whereas a total of zero means "no matches at all",
   * and those deserve different wording.
   */
  readonly totalCount: number;

  /**
   * The page of records returned, counted from zero.
   *
   * Zero-based: 0 is the first page, 1 the second. An unpaged envelope reports 0.
   * This is the same base {@link PagedRequest.pageIndex} sends, so a client may echo
   * the index it sent into the index it reads back without arithmetic.
   */
  readonly pageIndex: number;

  /**
   * The size of the page that produced the payload: the size the server actually
   * applied, which can differ from the size a caller asked for once the request has
   * been validated.
   *
   * An UNPAGED response reports the total here rather than zero. That is the server's
   * own reshaping and not a client convention: its internal envelope signals "unpaged"
   * with a page size of zero, but a zero on the wire would drive
   * {@link ApiMeta.totalPages} to zero for a response that plainly contains records,
   * so the boundary reports the truthful shape of what was sent — one page holding
   * everything — and every derived value stays self-consistent for a consumer that
   * divides. An unpaged, zero-record answer reports zero for both, which is correct.
   * {@link unpagedResult} produces the same coordinates for a locally lifted array, so
   * a component cannot tell a seeded value from a received one.
   */
  readonly pageSize: number;

  /**
   * The number of pages the total divides into at the current page size, or zero when
   * there is nothing to page.
   *
   * SERVER-COMPUTED. Read it; never recompute it from {@link ApiMeta.totalCount} and
   * {@link ApiMeta.pageSize}. The server guards the division twice — a page size of
   * zero is answered before any division occurs, and the quotient-plus-remainder form
   * is used because adding the page size to the total first would overflow for a total
   * near the largest representable integer. Recomputing here would reintroduce both
   * hazards, and a locally derived value that disagreed with the server's would give a
   * pager two sources of truth and no way to choose between them.
   */
  readonly totalPages: number;
}

/**
 * The standard success envelope for a response that carries a payload.
 *
 * Describes success and nothing else: no outcome flag, no failure member, no status
 * code and no correlation identifier. A body claiming failure alongside a successful
 * response code is the exact ambiguity that a standard problem document exists to
 * remove, so failure never travels in this shape — `problem-details.model.ts` is the
 * only wire error contract.
 *
 * ## Returned by EVERY single-payload endpoint
 *
 * MIGRATION: an earlier revision of this comment stated that no endpoint returns this
 * envelope and that a single-item endpoint serialises its contract directly. That
 * stopped being true when the server moved the envelope into its one shared result
 * translator, and the stale claim mattered: it was the justification for typing
 * {@link ApiResponse.meta} as optional. Re-measured against a running server —
 * `POST /api/v1/auth/login` answers with top-level keys `["data", "meta"]` — and the
 * server's own published schema agrees, `SuccessEnvelopeContractTests` asserting that
 * every single-payload operation declares exactly `data` and `meta`.
 *
 * @typeParam T The transported payload contract. Always a data transfer contract and
 * never a persisted entity. Deliberately unconstrained, so a single item, a
 * read-only list or a scalar are all expressible without this type ever naming a
 * collection of its own.
 */
export interface ApiResponse<T> {
  /** The payload the endpoint produced. */
  readonly data: T;

  /**
   * The metadata describing the response, or `null` when the response has no page to
   * describe — which is every response that uses this envelope, because the envelope
   * is what a SINGLE-payload endpoint returns.
   *
   * PRESENT AND NULLABLE, NOT OPTIONAL. The member is always on the wire and its
   * value is `null`; it never goes missing. That follows from the server's serializer
   * policy, which writes every declared member including one holding null — absence is
   * expressed by the VALUE being null, never by the member disappearing — and it was
   * confirmed against a running server rather than inferred.
   *
   * MIGRATION: this was declared `meta?: ApiMeta` — optional and non-null — which is
   * the one shape the wire never produces. The distinction is not academic under
   * `strictNullChecks`: the compiled type promised that reading it yields `ApiMeta` or
   * `undefined`, so a consumer narrowing with `=== undefined` took the wrong branch for
   * the `null` that actually arrives, and a consumer that had proved the member present
   * would have been handed `null` where the type guaranteed an object. Declaring it
   * required and nullable makes the compiler force the one check the wire actually
   * requires.
   *
   * `null` rather than zeroes is deliberate on the server's side too: a scalar payload
   * genuinely has no total, page index or page size, and reporting zeroes for them would
   * be indistinguishable from a real, empty first page.
   */
  readonly meta: ApiMeta | null;
}

/**
 * The standard success envelope for a response that carries no payload, such as a
 * deletion that reports only that it happened.
 *
 * The payload-free companion to {@link ApiResponse}, declared beside it so the two
 * arities of one contract are read together. The two are deliberately separate rather
 * than related by inheritance: a payload-bearing envelope assigned to a payload-free
 * declared type would lose its payload silently.
 *
 * UNLIKE {@link ApiResponse}, THIS ARITY GENUINELY HAS NO PRODUCER, and that is
 * structural rather than an oversight: a payload-free response is what an endpoint
 * answers when it has nothing to send, which in this API is a `204`, and `204` forbids
 * a body — so there is nothing for the server to write this shape into.
 * `AuthService.logout` accordingly types its call as bare `void`. The declaration is
 * kept because the server declares the arity.
 *
 * `meta` is nevertheless declared PRESENT AND NULLABLE, exactly as its sibling is, and
 * the absence of a producer is not a reason to declare it differently. Two reasons, and
 * the second is the load-bearing one. The serializer policy that governs the member is
 * a property of the server, not of any particular endpoint: it writes every declared
 * member including one holding null, so IF this arity is ever produced it will carry
 * `meta` with the value `null` rather than omit it. And declaring the two arities of one
 * contract differently would let them disagree about the member they share — a consumer
 * narrowing this one with `=== undefined` would take the wrong branch the moment a
 * producer appeared, which is the precise defect that had to be corrected on
 * {@link ApiResponse.meta}. Repeating it here, on the strength of there being nothing to
 * measure yet, would reintroduce it in the one place nobody would think to look.
 */
export interface EmptyApiResponse {
  /**
   * The metadata describing the response, or `null` when there is no page to
   * describe — the usual case for this form. Populated for a response that has a total
   * to report but no records to return with it.
   *
   * Present and nullable for the same reason as {@link ApiResponse.meta}: the server
   * writes every declared member, so absence arrives as `null` rather than as a missing
   * key. Declared consistently with its payload-bearing twin so the two arities of one
   * contract cannot disagree about the member they share.
   */
  readonly meta: ApiMeta | null;
}

/**
 * The wire contract of every PAGED endpoint, named as the response rather than as the
 * result.
 *
 * This is what `GET /api/v1/portals`, the account, role, module and role-membership
 * listings actually return, and it is the SAME shape as {@link PagedResult} rather than
 * a second one. There is deliberately ONE definition behind both names: two structurally
 * identical envelopes would let a change be made to one and missed on the other, and the
 * weaker of the two would then define what a consumer actually accepts. The alias exists
 * because "response" reads correctly at a service boundary and "result" reads correctly
 * in a component, and neither reading is worth a duplicate contract.
 *
 * @typeParam T The row contract carried on the page. Always a data transfer contract and
 * never a persisted entity.
 */
export type PagedResponse<T> = PagedResult<T>;

/**
 * Normalises a paged response into a complete envelope, deriving the page count when the
 * body did not carry one.
 *
 * The paging coordinates belong to the response rather than to the records, and every
 * coordinate a consumer reads is one the server sent — with one exception, stated here so
 * that no consumer recomputes it privately. `totalPages` is DERIVED from the total and the
 * page size when the body omits it, using the same arithmetic the server uses, so that a
 * component never divides by a page size of zero and never has to decide for itself what
 * an omitted page count means.
 *
 * Nothing else is invented. The navigation flags an earlier revision computed here —
 * whether a previous or a next page exists — are not members of this contract: they are
 * decided by whoever renders the pager, from the same two coordinates, and publishing them
 * as if the server had sent them would put a client-side derivation on the wire contract.
 *
 * An absent or partial metadata object is tolerated rather than faulted, and reported as an
 * empty first page: a paged endpoint always sends metadata, so a body without it is a
 * contract failure the error interceptor surfaces, and a model that threw here would turn
 * that into an unhandled exception inside a component.
 *
 * @param response The response body as received.
 * @returns The same page, with every coordinate present.
 */
export function toPagedResult<T>(response: PagedResponse<T>): PagedResult<T> {
  const items = response?.items ?? [];
  const totalCount = response?.meta?.totalCount ?? 0;
  const pageIndex = response?.meta?.pageIndex ?? 0;
  const pageSize = response?.meta?.pageSize ?? 0;
  const totalPages =
    response?.meta?.totalPages ??
    (pageSize > 0 && totalCount > 0
      ? Math.floor(totalCount / pageSize) + (totalCount % pageSize > 0 ? 1 : 0)
      : 0);

  return {
    items,
    meta: {
      totalCount,
      pageIndex,
      pageSize,
      totalPages,
    },
  };
}

/**
 * The page size to send when the caller expresses no preference.
 *
 * MIGRATION: ten is the measured legacy default rather than a convention.
 * `Library/Components/Users/UserModuleBase.vb` L134-L135 seed the `Records_PerPage`
 * module setting with ten whenever it is unset, and the server's own request
 * contract defaults to the same ten, so an unparameterised list request returns the
 * number of rows it always did.
 *
 * This is a FALLBACK, not a fixed rule. The effective page size is a per-portal
 * setting: the legacy account screen read its own size from `Records_PerPage`
 * (`Website/admin/Users/Users.ascx.vb` L114-L118) rather than from a constant, so a
 * screen with a portal-supplied size should prefer that value and use this only in
 * its absence.
 */
export const DEFAULT_PAGE_SIZE = 10;

/**
 * The largest page size the server accepts. A larger value is answered with a
 * field-level `400` reading `The page size may not exceed 100.` rather than being
 * silently reduced, so a client offering a page-size selector must not present a
 * larger option.
 *
 * MIGRATION: this bound is new and has no legacy predecessor — the legacy setting
 * defaulted to ten but no screen constrained it and no configuration key bounded it.
 */
export const MAX_PAGE_SIZE = 100;

/**
 * The longest filter text the server accepts on {@link PagedRequest.query}. Longer
 * text is answered with a field-level `400` reading `The filter text may not exceed
 * 256 characters.`, so a search input should bound its own length to match.
 */
export const QUERY_MAX_LENGTH = 256;

/**
 * Builds a zero-record, unpaged envelope.
 *
 * For the local placeholder a store needs before its first response arrives, or for
 * a list operation that legitimately matched nothing and applied no paging. The
 * coordinates match the shape a server response carries when nothing was matched and no
 * paging applied, so a component cannot tell a seeded value from a received one and needs
 * no separate branch for the un-loaded state. This is a CLIENT-SIDE seed: it is built here
 * and never received, because the response envelopes above are unadopted.
 *
 * For a paged operation whose requested page matched nothing, prefer the envelope the
 * server actually returned: it reports the coordinates that were requested, which is
 * what lets a pager stay accurate.
 *
 * @typeParam T The row contract the empty page would have carried.
 * @returns An envelope carrying no records and a total of zero.
 */
export function emptyPagedResult<T>(): PagedResult<T> {
  return {
    items: [],
    meta: {
      totalCount: 0,
      pageIndex: 0,
      pageSize: 0,
      totalPages: 0,
    },
  };
}

/**
 * Builds an unpaged, all-records envelope around records already in hand, deriving
 * the total from them because no further page exists to account for.
 *
 * This is the named replacement for the legacy all-records call shape, which
 * signalled the same intent by passing the integer null sentinel for the page index,
 * the page size and the total alike (see the MIGRATION note at the head of this
 * module). Here the intent is named, no sentinel is involved, and the caller supplies
 * no page coordinate at all.
 *
 * Its practical use on the client is to lift a bare array into the same envelope the
 * paged endpoints return, so that one component can render either. Several
 * collections are deliberately unpaged and arrive as plain arrays — see the note on
 * {@link PagedResult} usage below.
 *
 * The records are copied rather than referenced, mirroring the server's own factory:
 * a caller that keeps hold of the array it supplied cannot afterwards change what
 * this envelope reports.
 *
 * The coordinates it emits are exactly the ones the server publishes for an unpaged
 * answer, member for member, which is the whole point of the factory: the page size
 * is the total, because one page held everything, and the page count is one for a
 * non-empty set and zero for an empty one. That is not a client-side recomputation of
 * a server value — there is no server value here — and the page count is deliberately
 * not a division, because it is derived from a set that was never windowed.
 *
 * @typeParam T The row contract carried.
 * @param items Every matching record.
 * @returns An unpaged envelope whose total equals the number of records supplied.
 */
export function unpagedResult<T>(items: readonly T[]): PagedResult<T> {
  const snapshot: readonly T[] = [...items];

  return {
    items: snapshot,
    meta: {
      totalCount: snapshot.length,
      pageIndex: 0,
      pageSize: snapshot.length,
      totalPages: snapshot.length > 0 ? 1 : 0,
    },
  };
}

/*
 * WHICH COLLECTIONS ARE PAGED, AND WHICH ARE NOT
 *
 * Not every collection endpoint returns a PagedResult. Several return a BARE JSON
 * ARRAY, and typing one of those as PagedResult<T> would leave items empty and meta
 * undefined at run time while compiling perfectly. The division below was confirmed
 * endpoint by endpoint against a running server rather than assumed:
 *
 *   PAGED, returning the { items, meta } envelope
 *     - portals
 *     - users        (nested beneath a portal)
 *     - roles        (nested beneath a portal)
 *     - modules      (nested beneath a portal)
 *     - the accounts holding one role (nested beneath that role)
 *
 *   UNPAGED, returning a bare array - declare these as `readonly T[]`
 *     - role groups
 *     - portal aliases
 *     - profile definitions
 *     - tabs
 *     - permissions
 *
 * Tabs in particular have no top-level route of their own: they are consumed as a
 * lookup by the module screens, and the tab surface is closed at a small read and
 * update set.
 *
 * DIVERGENCE FROM THE PLAN, recorded rather than silently absorbed: the action plan
 * lists ROLES among the deliberately unpaged collections. The implemented endpoint
 * returns the paged envelope, and the four other collections the plan names - role
 * groups, portal aliases, profile definitions and the tab tree - do return bare
 * arrays as described. The list above reflects the server as built, because
 * describing roles as unpaged would lead a consumer to omit the envelope for the one
 * collection that has it.
 *
 * Use unpagedResult to lift a bare array into the envelope when a shared component
 * needs to render paged and unpaged collections through one contract. Do not use it
 * to reshape a response that already arrived paged.
 */
