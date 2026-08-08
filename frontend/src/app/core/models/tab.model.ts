/**
 * Wire contracts for the DotNetNuke page hierarchy - the abstraction that the database, the legacy source and
 * this contract all still call a "tab".
 *
 * Three interfaces mirror three backend shapes field for field, so a change on either side of the boundary
 * surfaces as a type error rather than as a silently dropped value: `TabListItem` mirrors
 * `Dtos/Tab/TabListItemDto.cs`, `TabDetail` mirrors `Dtos/Tab/TabDetailDto.cs`, and `UpdateTabRequest`
 * mirrors `Dtos/Tab/UpdateTabRequest.cs`. Beyond those and the two response decoders at the foot of the file
 * there is no behaviour here: nothing walks a hierarchy, resolves a link or formats a date, because each of
 * those is a consumer concern.
 *
 * A lookup, not a feature. The page surface is closed at three endpoints and the application owns no
 * page-management route of its own - pages are read by the module screens, which need to know which page a
 * placement sits on:
 *
 * GET /api/v1/portals/{portalId}/tabs   ->  TabListItem[]
 * GET /api/v1/tabs/{tabId}              ->  TabDetail
 * PUT /api/v1/tabs/{tabId}              <-  UpdateTabRequest   ->  TabDetail
 *
 * There is consequently no creation and no deletion shape: the surface is read plus update, and the
 * reversible recycle-bin transition travels on `isDeleted` of the update shape.
 *
 * MIGRATION: the page list is deliberately UNPAGED, unlike every other list in this application. It arrives
 * as a plain read-only collection inside the standard response envelope rather than the paging envelope in
 * `paged-result.model.ts`, so no shape below is wrapped in that envelope and no page-list alias is declared.
 * A hierarchy is read whole because a partially fetched tree cannot be indented correctly.
 *
 * Identifier and absence rules, every one of which the legacy encoding gets wrong. `dbo.Tabs` is declared
 * `[TabID] [int] IDENTITY (0, 1)` and `dbo.Portals` is declared `[PortalID] [int] IDENTITY (-1, 1)`, so `0`
 * is a real page and `-1` a real tenant. Never test an identifier for presence by truthiness, by magnitude,
 * or against `0`, `-1` or `-2` - compare with `=== null`. A root-level page arrives as `parentId: null`,
 * never as the legacy in-band `-1`; an unset date arrives as `null`, never as the minimum instant; and every
 * boolean is non-nullable, because the legacy sentinel helper treats `False` itself as absent so a nullable
 * boolean would be indistinguishable from a false one. Every member is always present on the wire, which is
 * why absence is modelled as `| null` and never as `?`. No ordering between the two dates is asserted: the
 * legacy editor checked each field's format in isolation and no such rule ever existed.
 *
 * Two wire names would otherwise be mis-spelled, and each fails silently as `undefined` rather than as a
 * compile error. The backend normalises every identifier to a single lowercase `d`, giving `tabId` and
 * `parentId` and never `tabID`, and it renames the legacy `KeyWords` property to the single-word `keywords`
 * while still mapping it to the original column. Both spellings below come from the backend shapes.
 *
 * The route parameter must be named `tabId`. Both single-page endpoints declare `tabs/{tabId:int}`, and the
 * backend permission handler resolves the page it is guarding by looking for a route value of that name
 * before falling back to `id`, so any other spelling loses the authorisation scope silently.
 *
 * Terminology: say "Page" to a user. These names keep the legacy `Tab` vocabulary so that they line up with
 * the schema, the domain entity and the wire contract, but the legacy screens are titled "Page Management"
 * and "Edit Page", so client-facing labels follow the screens rather than the schema.
 */

import {
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeString,
  nullable,
  objectOf,
  type Decoder,
} from '../utils/decode.util';

/**
 * One row of the page list returned by `GET /api/v1/portals/{portalId}/tabs`.
 *
 * Mirrors `Dtos/Tab/TabListItemDto.cs`. This is a read projection: it is never sent as a request body, and
 * it carries no paging metadata of its own because the endpoint is unpaged.
 *
 * MIGRATION: this shape carries no tenant identifier, and that omission is deliberate rather than an
 * oversight. Every row returned by the portal-scoped route already belongs to the portal named in the path,
 * so repeating the identifier on each row would be pure redundancy. The sibling detail shape does carry one,
 * because its own route is not portal-scoped; the asymmetry is intended.
 */
export interface TabListItem {
  /**
   * The page's identifier.
   *
   * Zero is a legitimate value, because `dbo.Tabs.TabID` is `IDENTITY (0, 1)`: the first page of an
   * installation is numbered zero. Never read zero as "not yet saved".
   */
  readonly tabId: number;

  /**
   * The page name, captioned "Page Name" on the legacy screens.
   *
   * This is the text the legacy list bound as its display field, so treat it as the row's label. At most 50
   * characters.
   */
  readonly tabName: string;

  /**
   * The browser-window title, captioned "Page Title", or `null` when none is stored.
   *
   * At most 200 characters.
   */
  readonly title: string | null;

  /**
   * The sort position of this page among its siblings under the same parent.
   *
   * Server-owned: the ordering routine computes it, and the update shape deliberately omits it.
   */
  readonly tabOrder: number;

  /**
   * The page this one hangs beneath, or `null` when it sits at the root of the hierarchy.
   *
   * Captioned "Parent Page" on the legacy screens, where the root choice was labelled "Root". See the file
   * header: `null` is the boundary representation of the legacy `-1`, and `0` names a real parent page. Test
   * the root case with `parentId === null`.
   */
  readonly parentId: number | null;

  /**
   * The page's depth in the hierarchy, where zero denotes root level.
   *
   * Server-derived from the parent chain, and carried on the row so that an indented list can be rendered
   * from a single response. The update shape deliberately omits it.
   */
  readonly level: number;

  /**
   * The materialised hierarchical path of this page, or `null` when none is stored.
   *
   * Server-generated from the parent chain and the page name, never authored by a client, and therefore
   * absent from the update shape. At most 255 characters.
   */
  readonly tabPath: string | null;

  /**
   * Whether the page appears in the navigation menu, captioned "Include In Menu".
   *
   * Despite the member name this is a menu-inclusion flag and not a general visibility flag: a page with it
   * clear stays reachable by direct link. It is exposed rather than filtered because the legacy page list
   * deliberately included menu-excluded pages, leaving the decision to the caller.
   */
  readonly isVisible: boolean;

  /**
   * Whether the page is disabled, captioned "Disabled".
   *
   * A disabled page typically still renders in the menu, as inert text rather than as a link.
   */
  readonly disableLink: boolean;

  /**
   * Whether the page is soft-deleted into the recycle bin.
   *
   * Deletion is a flag rather than a removal, so a soft-deleted page is still a row. Filtering belongs to
   * the caller: the legacy page list excluded soft-deleted pages while including menu-excluded ones, and
   * reproducing that asymmetry takes both flags on the row.
   */
  readonly isDeleted: boolean;

  /**
   * Whether any other page names this one as its parent.
   *
   * A computed existence projection rather than a stored column, carried so that a node can decide whether
   * to draw an expander without a further request per row.
   *
   * MIGRATION: the legacy read view emitted this as the STRING `'true'` or `'false'`, which the legacy
   * reader then coerced with a boolean conversion. This contract carries a genuine JSON boolean; never parse
   * it as text.
   */
  readonly hasChildren: boolean;

  /**
   * Whether the page must be served over a secure connection, captioned "Secure?".
   *
   * The newest column on the page table: installations upgraded from an earlier schema acquired it with a
   * default rather than with a per-page decision.
   */
  readonly isSecure: boolean;

  /**
   * The navigation target used when the page acts as a link to another resource, captioned "Link Url", or
   * `null` when the page hosts content of its own.
   *
   * At most 255 characters.
   */
  readonly url: string | null;

  /**
   * The menu icon reference, captioned "Icon", or `null` for none.
   *
   * MIGRATION: this may arrive as an unresolved `fileid=NNN` token rather than as a usable path. The base
   * table stores the raw value and only the legacy read view resolved it against the file table, so
   * resolution belongs to the repository or the mapper and a consumer must tolerate either form. At most 100
   * characters.
   */
  readonly iconFile: string | null;
}

/**
 * One page in full, returned by `GET /api/v1/tabs/{tabId}` and echoed back by `PUT /api/v1/tabs/{tabId}`.
 *
 * Mirrors `Dtos/Tab/TabDetailDto.cs`. A read projection with nine members more than the list row: the
 * description, the search keywords, the two dates, the refresh interval, the head markup, the tenant
 * identifier and the two opaque presentation tokens.
 *
 * Every member is inert. Nothing here resolves a file, walks the hierarchy or computes a value on access -
 * see the file header for why that constraint is stated so emphatically.
 */
export interface TabDetail {
  /**
   * The page's identifier.
   *
   * Zero is a legitimate value, because `dbo.Tabs.TabID` is `IDENTITY (0, 1)`. Never read zero as "not yet
   * saved".
   */
  readonly tabId: number;

  /**
   * The sort position of this page among its siblings under the same parent.
   *
   * Server-owned, and therefore absent from the update shape: the legacy update path accepted no order
   * argument at all, and the ordering routine was deliberately invoked with zero so that it would
   * recalculate the value itself.
   */
  readonly tabOrder: number;

  /**
   * The owning tenant, or `null` when this is a host-level page.
   *
   * MIGRATION: the legacy property was a non-nullable integer that used `-1` to mean "host page". The
   * backend converts that sentinel to a genuine null, because `dbo.Portals.PortalID` is `IDENTITY (-1, 1)`
   * and `-1` is therefore at once a real tenant key and the legacy absence marker; preserving the sentinel
   * would have made the two cases indistinguishable. Test the host case with `portalId === null`. This
   * member appears on this shape alone - the list row omits it because its route is already portal-scoped,
   * and the update shape omits it because the legacy editor never let an administrator choose it either.
   */
  readonly portalId: number | null;

  /**
   * The page name, captioned "Page Name" on the legacy screens. At most 50 characters.
   */
  readonly tabName: string;

  /**
   * Whether the page appears in the navigation menu, captioned "Include In Menu".
   *
   * A menu-inclusion flag only: a page with it clear stays reachable by direct link.
   */
  readonly isVisible: boolean;

  /**
   * The page this one hangs beneath, or `null` when it sits at the root of the hierarchy.
   *
   * Captioned "Parent Page". See the file header: `null` is the boundary representation of the legacy `-1`,
   * and `0` names a real parent page. Test the root case with `parentId === null`.
   */
  readonly parentId: number | null;

  /**
   * The page's depth in the hierarchy, where zero denotes root level.
   *
   * Server-derived from the parent chain, and therefore absent from the update shape.
   */
  readonly level: number;

  /**
   * The menu icon reference, captioned "Icon", or `null` for none.
   *
   * MIGRATION: may arrive as an unresolved `fileid=NNN` token rather than as a usable path; see the list
   * row's note. At most 100 characters.
   */
  readonly iconFile: string | null;

  /**
   * Whether the page is disabled, captioned "Disabled".
   *
   * MIGRATION: a known legacy behaviour is preserved rather than corrected here. The legacy editor assigned
   * this flag only when the page being edited was none of the five protected system pages - the
   * administration, splash, home, login and account pages - and for those five it silently discarded the
   * posted value and left the flag false, showing the administrator no message at all. Behavioural
   * equivalence takes precedence over opportunistic correction, so the behaviour is recorded rather than
   * repaired; changing it would be a separate, deliberately documented decision.
   */
  readonly disableLink: boolean;

  /**
   * The browser-window title, captioned "Page Title", or `null` when none is stored.
   *
   * At most 200 characters.
   */
  readonly title: string | null;

  /**
   * The page description, or `null` when none is stored.
   *
   * At most 500 characters.
   */
  readonly description: string | null;

  /**
   * The comma-separated search keywords, captioned "Keywords", or `null` when none are stored.
   *
   * MIGRATION: the column and the legacy property are both spelled with an interior capital; the backend
   * renames the member to this single-word form and maps it back to the column in the persistence layer, so
   * the wire name is `keywords`. Never reproduce the legacy spelling on this contract - it would resolve to
   * `undefined` with no compile error. At most 500 characters.
   */
  readonly keywords: string | null;

  /**
   * Whether the page is soft-deleted into the recycle bin.
   *
   * Neither filtered nor hidden by this contract: a soft-deleted page is still a row, and the service and
   * endpoint decide which rows a caller may see.
   */
  readonly isDeleted: boolean;

  /**
   * The navigation target used when the page acts as a link to another resource, captioned "Link Url", or
   * `null` when the page hosts content of its own.
   *
   * At most 255 characters.
   */
  readonly url: string | null;

  /**
   * The opaque skin token applied to this page, captioned "Page Skin", or `null` for none.
   *
   * MIGRATION: this is a genuinely stored column that the terminal update procedure writes, so it belongs on
   * both the read and the write surface and is carried here rather than dropped. Skinning itself is
   * nevertheless out of scope: the value is INERT in this target. It is stored and returned verbatim as an
   * opaque token, and nothing anywhere resolves it, loads it or renders it, because there is no server-side
   * rendering left to render it with. Its derived resolved-path companion is omitted for exactly that
   * reason. At most 200 characters.
   */
  readonly skinSrc: string | null;

  /**
   * The opaque container token applied to the modules on this page, captioned "Page Container", or `null`
   * for none.
   *
   * MIGRATION: as with the skin token, this is a genuinely stored column that the terminal update procedure
   * writes, so it is carried on both surfaces; and as with the skin token it is INERT here, stored and
   * returned verbatim with no resolution, no file lookup and no rendering anywhere in this target. Its
   * derived resolved-path companion is omitted. At most 200 characters.
   */
  readonly containerSrc: string | null;

  /**
   * The materialised hierarchical path of this page, or `null` when none is stored.
   *
   * Server-generated from the parent chain and the page name - the legacy code assigned it exclusively
   * through its path-generating helper and never accepted it from a client - so it is absent from the update
   * shape. At most 255 characters.
   */
  readonly tabPath: string | null;

  /**
   * The instant from which the page becomes available as an ISO 8601 string, or `null` when no start date is
   * set. Captioned "Start Date:" on the legacy screens.
   *
   * MIGRATION: `null` here is the boundary representation of the legacy null-date sentinel that the legacy
   * editor wrote whenever the box was left blank; the minimum instant is never emitted. No ordering against
   * the end date is asserted, because the legacy editor asserted none.
   */
  readonly startDate: string | null;

  /**
   * The instant after which the page ceases to be available as an ISO 8601 string, or `null` when no end
   * date is set. Captioned "End Date:" on the legacy screens.
   *
   * MIGRATION: as with the start date, `null` is the boundary representation of the legacy null-date
   * sentinel and the minimum instant is never emitted. Never assume this date follows the start date: the
   * legacy application checked only the format of each field in isolation, and no cross-field constraint is
   * asserted anywhere in this contract.
   */
  readonly endDate: string | null;

  /**
   * The automatic refresh interval in SECONDS, or `null` when the page does not refresh automatically.
   * Captioned "Refresh Interval (seconds)" on the legacy screens.
   *
   * The unit is stated explicitly because the member name alone does not carry it and a consumer could
   * otherwise reasonably guess milliseconds or minutes.
   *
   * MIGRATION: the legacy representation of "no automatic refresh" was the `-1` integer sentinel, which
   * survived into storage because the legacy editor overwrote it only when the supplied text was both
   * non-empty and numeric. The backend converts it to a genuine null, so `-1` is never emitted here. Test
   * with `refreshInterval === null`. The legacy field carried no validation rule of any kind, and none has
   * been invented.
   */
  readonly refreshInterval: number | null;

  /**
   * Raw markup injected into the document head when the page is rendered, captioned "Page Header Tags", or
   * `null` when none is stored.
   *
   * MIGRATION: this is arbitrary author-supplied markup, carried verbatim. This contract neither parses,
   * validates, sanitises nor escapes it, so a consumer that chooses to render it owns that decision and its
   * consequences entirely - it must never be bound as trusted markup. At most 500 characters.
   */
  readonly pageHeadText: string | null;

  /**
   * Whether the page must be served over a secure connection, captioned "Secure?".
   */
  readonly isSecure: boolean;

  /**
   * Whether any other page names this one as its parent.
   *
   * A computed existence projection rather than a stored column. As on the list row it is a genuine JSON
   * boolean, even though the legacy read view emitted the strings `'true'` and `'false'`.
   */
  readonly hasChildren: boolean;
}


/**
 * The body of `PUT /api/v1/tabs/{tabId}`.
 *
 * Mirrors `Dtos/Tab/UpdateTabRequest.cs`. Members are writable rather than read-only, because a caller
 * assembles this shape.
 *
 * MIGRATION: this is a COMPLETE REPLACEMENT of the editable subset, exactly as the legacy postback was - not
 * a partial patch. An omitted field does not mean "leave that column alone": the server writes the absent
 * value, which clears a nullable column, sets a boolean to false, and moves the page to the root. Every
 * member below is consequently mandatory, and a caller must send the page's current values for anything it
 * does not intend to change. That is deliberate and matches the legacy screen, where an empty text box
 * posted an empty value; without it an administrator could set a link target but never clear one.
 *
 * MIGRATION: the identifier is route-supplied and never body-supplied. The legacy editor took it from the
 * page's own context and no form field ever contributed it, so this shape declares no identifier member at
 * all and the route value is authoritative. The tenant identifier is absent for the same reason. The sort
 * position, the depth and the materialised path are absent because the server recomputes all three after a
 * move, and the child-existence flag is absent because it is computed rather than stored.
 */
export interface UpdateTabRequest {
  /**
   * The page name, captioned "Page Name" on the legacy screens.
   *
   * Mandatory, and at most 50 characters. This is the one presence rule the legacy form declared, and the
   * backend validator reproduces it as a non-empty rule rather than a non-null one, so that a
   * whitespace-only name is refused just as an absent one is.
   */
  readonly tabName: string;

  /**
   * The browser-window title, captioned "Page Title", or `null` to clear it.
   *
   * At most 200 characters.
   */
  readonly title: string | null;

  /**
   * The page description, or `null` to clear it.
   *
   * At most 500 characters.
   */
  readonly description: string | null;

  /**
   * The comma-separated search keywords, captioned "Keywords", or `null` to clear them.
   *
   * At most 500 characters. Note the single-word spelling: the legacy column and property carry an interior
   * capital, and the backend maps this member onto that column, so the wire name is `keywords`.
   */
  readonly keywords: string | null;

  /**
   * The page this one should hang beneath, or `null` to make it a root-level page.
   *
   * MIGRATION: `null` is an instruction to MOVE THE PAGE TO THE ROOT. It does not mean "leave the parent as
   * it is" - this request replaces the editable subset wholesale. Because the page key is seeded at zero, a
   * submitted `0` names a real parent page and must never be read as "no parent". Changing this moves the
   * page and every descendant with it, after which the server recomputes the depth, the sibling order and
   * the materialised path of the affected subtree; none of those three is accepted from a client.
   *
   * Two rejections are enforced by the server rather than by this contract: a parent that would create a
   * cycle, whether by naming the page itself or by naming one of its own descendants, and a parent belonging
   * to a different tenant. The second matters more here than it did on the legacy screen, because the value
   * now arrives in a request body rather than from a tenant-filtered picker.
   *
   * MIGRATION: a legacy defect is recorded and deliberately left unfixed. When the legacy screen's
   * self-parent test or its recursive ancestry walk tripped, it simply skipped the update and reported
   * nothing at all - a silent no-op that a user could easily mistake for success. The condition is
   * preserved; how the failure is surfaced is a service concern, and this shape neither detects nor reports
   * it.
   */
  readonly parentId: number | null;

  /**
   * Whether the page should appear in the navigation menu, captioned "Include In Menu".
   *
   * Menu inclusion only. Note the consequence of complete-replacement semantics: no default is supplied, so
   * omitting the field binds it to false and drops the page out of the menu.
   */
  readonly isVisible: boolean;

  /**
   * Whether the page should be disabled, captioned "Disabled".
   *
   * The server ignores this for the five protected system pages, preserving the legacy behaviour described
   * on the detail shape.
   */
  readonly disableLink: boolean;

  /**
   * The menu icon reference, captioned "Icon", or `null` to clear it.
   *
   * At most 100 characters, and the backend additionally refuses a value that escapes its permitted
   * location, so a caller must submit a contained reference rather than an arbitrary path.
   */
  readonly iconFile: string | null;

  // MIGRATION: NO `skinSrc` AND NO `containerSrc` ON THIS REQUEST. Both were declared here, mirroring a
  // server contract that carried them, on the reasoning that a column the terminal procedure persists must
  // be settable or a save would discard it. That reasoning is subordinate to an explicit exclusion: skinning
  // and containers are out of scope for this migration, and a member that a client can set is not an inert
  // one — declaring it made this the supported way to change a page's skin and published it as part of the
  // page-edit contract. Both columns are still READABLE, on {@link TabDetail}, so a stored choice remains
  // observable; it is simply no longer settable through this API, and the server's projection now leaves
  // both columns exactly as stored. That is strictly safer than what it replaced: because this body is a
  // whole-row replacement, a caller that omitted either member previously BLANKED an administrator's stored
  // token on every unrelated edit.

  /**
   * The navigation target when the page should act as a link to another resource, captioned "Link Url", or
   * `null` to clear it.
   *
   * At most 255 characters, and it must occupy a single line: the backend refuses any Unicode control
   * character, on the ground that no single-line form field could ever have submitted one even though the
   * column could store it.
   */
  readonly url: string | null;

  /**
   * The instant from which the page becomes available as an ISO 8601 string, or `null` for no start bound.
   * Captioned "Start Date:" on the legacy screens.
   *
   * MIGRATION: send `null` rather than the minimum instant to mean "unset" - the legacy editor wrote that
   * sentinel and this contract does not. The backend checks only that a supplied instant is storable by the
   * column, which begins centuries later than the platform date type does; that is a representability check
   * and not a business rule, and no ordering against the end date is imposed.
   */
  readonly startDate: string | null;

  /**
   * The instant after which the page ceases to be available as an ISO 8601 string, or `null` for no end
   * bound. Captioned "End Date:" on the legacy screens.
   *
   * MIGRATION: as with the start date, send `null` rather than the minimum instant to mean "unset". The two
   * dates are never compared against one another, here or on the server.
   */
  readonly endDate: string | null;

  /**
   * The automatic refresh interval in SECONDS, or `null` for no automatic refresh.
   *
   * MIGRATION: send `null` rather than `-1`, which was the legacy sentinel. The legacy field carried no
   * validation rule whatsoever and the backend invents none, so a caller is responsible for submitting a
   * sensible interval.
   */
  readonly refreshInterval: number | null;

  /**
   * Raw markup to inject into the document head, captioned "Page Header Tags", or `null` to clear it.
   *
   * Stored verbatim and never sanitised by the server. At most 500 characters.
   */
  readonly pageHeadText: string | null;

  /**
   * Whether the page must be served over a secure connection, captioned "Secure?".
   */
  readonly isSecure: boolean;

  /**
   * Whether the page should sit in the recycle bin: `true` soft-deletes it, `false` restores it.
   *
   * MIGRATION: this is the only member of this shape with no counterpart control on the legacy page-settings
   * form, and it is present for a measured reason. In the legacy application both recycle-bin transitions
   * were plain writes of this one flag through the very same update call that this endpoint replaces - soft
   * delete set it and saved, and restore cleared it and saved through the identical call. Because this
   * surface exposes no deletion route and no restoration route, this member is the sole means of reaching
   * either transition, and dropping it would have removed the capability from the product altogether.
   * Permanent deletion is a different operation entirely and is out of scope; this member expresses only the
   * reversible transition.
   *
   * Mind the complete-replacement semantics: no default is supplied, so omitting this field binds it to
   * false and a routine edit would RESTORE a page that was sitting in the recycle bin. A caller editing a
   * recycled page must send `true` to keep it there. The legacy screen behaved identically, hard-coding the
   * flag to false on every save.
   *
   * Two constraints are enforced by the server rather than here, because each needs other rows read:
   * restoration was blocked while the page's own parent remained deleted, and deletion was refused outright
   * both for the five protected system pages and for a page that still had descendants.
   */
  readonly isDeleted: boolean;
}

/**
 * Decodes one listed page.
 *
 * `tabId` uses {@link decodeInteger} with no positivity test: `dbo.Tabs` is declared `[TabID] [int] IDENTITY
 * (0, 1)`, so ZERO is the first page an installation ever creates. `parentId` is nullable because a root
 * page has no parent, and that null is the only expression of "root" — coalescing it to zero would reparent
 * every root page under the first page ever created.
 *
 * `level` and `tabOrder` are required, because both drive the render: a `level` reaching an indentation
 * calculation as `undefined` produces `NaN` padding, and a missing `tabOrder` silently moves the page to one
 * end of its sibling set.
 */
export const decodeTabListItem: Decoder<TabListItem> = objectOf<TabListItem>({
  tabId: decodeInteger,
  tabName: decodeString,
  title: nullable(decodeString),
  tabOrder: decodeInteger,
  parentId: nullable(decodeInteger),
  level: decodeInteger,
  tabPath: nullable(decodeString),
  isVisible: decodeBoolean,
  disableLink: decodeBoolean,
  isDeleted: decodeBoolean,
  hasChildren: decodeBoolean,
  isSecure: decodeBoolean,
  url: nullable(decodeString),
  iconFile: nullable(decodeString),
});

/**
 * Decodes one page in full.
 *
 * `portalId` is nullable AND admits minus one, which is not a contradiction: `dbo.Portals` is declared
 * `[PortalID] [int] IDENTITY (-1, 1)`, so minus one is the FIRST portal and a real owner, while null means
 * the page names no portal. The legacy absent-integer marker is also minus one, which is exactly why the two
 * must be distinguished by nullability rather than by the value.
 */
export const decodeTabDetail: Decoder<TabDetail> = objectOf<TabDetail>({
  tabId: decodeInteger,
  tabOrder: decodeInteger,
  portalId: nullable(decodeInteger),
  tabName: decodeString,
  isVisible: decodeBoolean,
  parentId: nullable(decodeInteger),
  level: decodeInteger,
  iconFile: nullable(decodeString),
  disableLink: decodeBoolean,
  title: nullable(decodeString),
  description: nullable(decodeString),
  keywords: nullable(decodeString),
  isDeleted: decodeBoolean,
  url: nullable(decodeString),
  skinSrc: nullable(decodeString),
  containerSrc: nullable(decodeString),
  tabPath: nullable(decodeString),
  startDate: nullable(decodeDateString),
  endDate: nullable(decodeDateString),
  refreshInterval: nullable(decodeInteger),
  pageHeadText: nullable(decodeString),
  isSecure: decodeBoolean,
  hasChildren: decodeBoolean,
});
