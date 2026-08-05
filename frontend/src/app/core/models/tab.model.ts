/**
 * Wire contracts for the DotNetNuke page hierarchy - the abstraction that the database, the legacy
 * source and this contract all still call a "tab".
 *
 * Each interface mirrors one backend shape, field for field, so that a change on either side of the
 * boundary surfaces as a type error rather than as a silently dropped value:
 *
 *   - `TabListItem`      mirrors `Dtos/Tab/TabListItemDto.cs`   (14 members)
 *   - `TabDetail`        mirrors `Dtos/Tab/TabDetailDto.cs`     (23 members)
 *   - `UpdateTabRequest` mirrors `Dtos/Tab/UpdateTabRequest.cs` (17 members)
 *
 * This file is type-only. It declares those three interfaces and nothing else, it imports nothing at
 * all, and it contributes no runtime behaviour: there is no class, no constructor, no decorator, no
 * enumeration, no frozen lookup map and no helper function. Nothing here walks a hierarchy, resolves
 * a link or formats a date - each of those is a consumer concern, and the legacy page entity is a
 * cautionary tale about the alternative (see the note on omitted members below).
 *
 * Scope: a lookup, not a feature. The page surface is closed at three endpoints, and the application
 * deliberately owns no page-management route of its own:
 *
 *   GET /api/v1/portals/{portalId}/tabs   ->  TabListItem[]
 *   GET /api/v1/tabs/{tabId}              ->  TabDetail
 *   PUT /api/v1/tabs/{tabId}              <-  UpdateTabRequest   ->  TabDetail
 *
 * Pages are consumed as a lookup by the module screens, which need to know which page a placement
 * sits on. That is also why no creation shape and no deletion shape appear below: the surface is read
 * plus update only, and the reversible recycle-bin transition travels on `isDeleted` of the update
 * shape instead.
 *
 * MIGRATION: the page list is deliberately UNPAGED, unlike every other list in this application. The
 * endpoint returns a plain read-only collection inside the standard response envelope
 * (`TabsController.cs:L183`), not the shared paging envelope declared in `paged-result.model.ts`, so
 * no shape below is wrapped in that envelope and no page-list alias is declared here - deliberately
 * unlike the portal and user models, each of which does declare one. A hierarchy is read whole
 * because a partially fetched tree cannot be indented correctly. The controller signature is
 * authoritative for that shape.
 *
 * MIGRATION: ZERO IS A REAL PAGE IDENTIFIER AND MINUS ONE IS A REAL TENANT IDENTIFIER. `dbo.Tabs` is
 * declared `[TabID] [int] IDENTITY (0, 1)` (`01.00.00.SqlDataProvider:L140`) and `dbo.Portals` is
 * declared `[PortalID] [int] IDENTITY (-1, 1)` (`:L77`), so the first page of an installation is
 * numbered `0` and `-1` is a genuine tenant key. Never test an identifier for presence by truthiness,
 * by magnitude, or by comparison against `0`, `-1` or `-2`; compare against `null` with `=== null`
 * instead. The legacy encoding cannot be carried across: the legacy sentinel helper returns `-1` for
 * a missing integer (`Null.vb:L41-L45`), the minimum instant for a missing date, and - counter to
 * every instinct - the EMPTY STRING rather than a null for a missing string (`Null.vb:L71-L75`,
 * literally `Return ""`).
 *
 * MIGRATION: A ROOT-LEVEL PAGE ARRIVES AS `parentId: null`, NEVER AS `-1`. The legacy property was a
 * non-nullable integer whose "no parent" value was the in-band `-1` sentinel, seeded by the
 * constructor (`TabInfo.vb:L91`) and reassigned by the template importer. The backend converts that
 * sentinel to a genuine null at the boundary, because `-1` is simultaneously a legitimate tenant
 * identifier and the two were otherwise indistinguishable on the wire; the member is typed
 * `number | null` below to mirror exactly what the wire carries. Test the root case with
 * `parentId === null` - never `parentId > 0`, never a truthiness test, and never a coalesce in
 * either direction, because `0` names a real parent page.
 *
 * MIGRATION: AN UNSET DATE ARRIVES AS `null`, NEVER AS THE MINIMUM INSTANT. The legacy entity
 * declared both date members non-nullable and seeded them with the null-date sentinel
 * (`TabInfo.vb:L101-L102`), and the legacy editor wrote that sentinel back whenever the input box was
 * left blank, even though both columns are nullable. The backend converts the sentinel to a genuine
 * null and never emits `0001-01-01`. Both members are typed `string | null` below - an ISO 8601
 * instant when set - and neither is optional. No ordering between the two dates is asserted anywhere:
 * the legacy editor attached only a format check to each field in isolation, so a rule that the end
 * date must follow the start date never existed and is not introduced here.
 *
 * MIGRATION: THIRTEEN OF THE LEGACY ENTITY'S THIRTY-SIX PROPERTIES REACH NO SHAPE BELOW, and each
 * omission has its own distinct reason:
 *
 *   - The three request-time navigation collections declared at `TabInfo.vb:L77-L79`, beneath the
 *     source's own comment `' properties loaded in PortalSettings` - the ancestor trail, the layout
 *     regions and the placed content. Not one of the three is a stored column. The legacy page
 *     pipeline filled them per request so that a skin could render them, and skinning is out of
 *     scope. A mechanical translation would have faithfully reproduced all three.
 *   - The two derived resolved-path companions `SkinPath` and `ContainerPath`
 *     (`TabInfo.vb:L347` and `:L356`), which belong to that same excluded skin-and-container
 *     subsystem and whose values only ever existed in order to be rendered on the server.
 *   - The read-only page-kind enumeration declared at `TabInfo.vb:L32-L38` and exposed at `:L406`.
 *     It is computed from the link target rather than stored, and it is not among the enumerations
 *     this migration ports, so no counterpart type is declared here. Confirmed absent from all three
 *     backend shapes before it was dropped.
 *   - The cache-level member at `TabInfo.vb:L605`, an artefact of the token-accessor interface the
 *     legacy class implemented, which carries no meaning once server-side rendering is gone.
 *   - The two derived members at `TabInfo.vb:L412` and `:L435`. The second is the instructive one:
 *     its getter read a cache, then a controller, then the database, all from inside a property
 *     getter. No member below may behave that way, which is the reason every one of them is inert.
 *   - The host-page flag at `TabInfo.vb:L392`, superseded on the detail shape by a null tenant
 *     identifier.
 *   - The structured permission collection at `TabInfo.vb:L282` and the two delimited role strings at
 *     `:L327` and `:L336`; see the note immediately below.
 *
 * MIGRATION: the two semicolon-delimited legacy role strings are abandoned rather than parsed. The
 * legacy entity carried access twice over - once structurally, and once as a pair of joined display
 * strings that discarded every attribute except the role name. Neither string reaches any shape
 * below. The current permission API publishes catalogue definitions and bare keys but no page-grant
 * DTO, so `permission.model.ts` deliberately declares no grant shape either. No shape below carries
 * permissions in any form: all three backend page shapes declare none, and this file mirrors what
 * they declare rather than what the legacy entity happened to hold. That was verified rather than
 * assumed, which is why nothing is imported here.
 *
 * MIGRATION: every member below is always present on the wire, so not one of them is declared
 * optional. The API serialises with an ignore condition of `Never` and a camel-case naming policy
 * (`ServiceCollectionExtensions.cs:L313-L314` for the minimal-API surface and `:L408` and `:L417` for
 * the controller surface), a choice that `Program.cs:L84-L94` both explains and forbids changing: an
 * omit-nulls or omit-defaults policy would erase `0`, `""` and `false`, every one of which is a
 * legitimate value in this schema. A field is therefore present-but-possibly-null and never missing,
 * which is precisely why absence is modelled as `| null` and never as `?`.
 *
 * MIGRATION: every boolean below is non-nullable. The legacy sentinel helper treats `False` itself as
 * absent (`Null.vb:L227-L228`), so a nullable boolean would be indistinguishable from a false one.
 * The backend declares all eight page flags as plain non-nullable booleans, and they are typed
 * `boolean` here - never `boolean | null`, and never optional.
 *
 * MIGRATION: two wire names differ from the spelling that a reader of the legacy source would
 * predict, and each would have failed silently rather than loudly. The legacy source spells the
 * identifier `TabID` with a capital pair (`TabInfo.vb:L111`) yet spells the parent reference
 * `ParentId` with a single lowercase `d` (`:L156`); the backend normalises every identifier to the
 * single-`d` form, so the camel-case policy yields `tabId` and `parentId` and never `tabID`.
 * Separately, both the column and the legacy property are spelled `KeyWords` with an interior capital
 * (`TabInfo.vb:L58`), while the backend renames the member to the single-word form and maps it back
 * to the column in the persistence layer, so the wire name is `keywords`. Both spellings below are
 * taken from the backend shapes rather than inferred from the legacy source; a mismatch would have
 * yielded `undefined` at runtime with no compile error to warn of it.
 *
 * MIGRATION: the route parameter is named `tabId`. Both single-page endpoints are declared
 * `tabs/{tabId:int}` (`TabsController.cs:L249` and `:L315`), and the backend permission handler
 * resolves the page it is guarding by looking for a route value named `tabId` before falling back to
 * `id`. A caller that names the parameter anything else - `tabID` in particular - loses the
 * authorisation scope silently instead of failing outright.
 *
 * Cross-reference: the six page references that a tenant holds - its administration, host-root,
 * splash, home, login and account pages - are declared on the portal shapes in `portal.model.ts` and
 * not here. Each is nullable there for the same reason `parentId` is nullable here: the legacy `-1`
 * meant "no such page is configured", while `0` is a real page. Resolve any of those six against a
 * page lookup only after testing it against `null`.
 *
 * Terminology: say "Page" to a user. These type names keep the legacy `Tab` vocabulary so that they
 * line up with the database schema, the domain entity and the wire contract, but the concept an
 * administrator actually sees is a Page: the legacy screens are titled "Page Management" and "Edit
 * Page", and their captions read "Page Name", "Page Title" and "Parent Page". Client-facing labels
 * should follow the screens rather than the schema.
 */

/**
 * One row of the page list returned by `GET /api/v1/portals/{portalId}/tabs`.
 *
 * Mirrors `Dtos/Tab/TabListItemDto.cs`. This is a read projection: it is never sent as a request
 * body, and it carries no paging metadata of its own because the endpoint is unpaged.
 *
 * MIGRATION: this shape carries no tenant identifier, and that omission is deliberate rather than an
 * oversight. Every row returned by the portal-scoped route already belongs to the portal named in the
 * path, so repeating the identifier on each row would be pure redundancy. The sibling detail shape
 * does carry one, because its own route is not portal-scoped; the asymmetry is intended.
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
   * This is the text the legacy list bound as its display field, so treat it as the row's label. At
   * most 50 characters.
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
   * Captioned "Parent Page" on the legacy screens, where the root choice was labelled "Root". See the
   * file header: `null` is the boundary representation of the legacy `-1`, and `0` names a real
   * parent page. Test the root case with `parentId === null`.
   */
  readonly parentId: number | null;

  /**
   * The page's depth in the hierarchy, where zero denotes root level.
   *
   * Server-derived from the parent chain, and carried on the row so that an indented list can be
   * rendered from a single response. The update shape deliberately omits it.
   */
  readonly level: number;

  /**
   * The materialised hierarchical path of this page, or `null` when none is stored.
   *
   * Server-generated from the parent chain and the page name, never authored by a client, and
   * therefore absent from the update shape. At most 255 characters.
   */
  readonly tabPath: string | null;

  /**
   * Whether the page appears in the navigation menu, captioned "Include In Menu".
   *
   * Despite the member name this is a menu-inclusion flag and not a general visibility flag: a page
   * with it clear stays reachable by direct link. It is exposed rather than filtered because the
   * legacy page list deliberately included menu-excluded pages, leaving the decision to the caller.
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
   * Deletion is a flag rather than a removal, so a soft-deleted page is still a row. Filtering
   * belongs to the caller: the legacy page list excluded soft-deleted pages while including
   * menu-excluded ones, and reproducing that asymmetry takes both flags on the row.
   */
  readonly isDeleted: boolean;

  /**
   * Whether any other page names this one as its parent.
   *
   * A computed existence projection rather than a stored column, carried so that a node can decide
   * whether to draw an expander without a further request per row.
   *
   * MIGRATION: the legacy read view emitted this as the STRING `'true'` or `'false'`, which the
   * legacy reader then coerced with a boolean conversion. This contract carries a genuine JSON
   * boolean; never parse it as text.
   */
  readonly hasChildren: boolean;

  /**
   * Whether the page must be served over a secure connection, captioned "Secure?".
   *
   * The newest column on the page table: installations upgraded from an earlier schema acquired it
   * with a default rather than with a per-page decision.
   */
  readonly isSecure: boolean;

  /**
   * The navigation target used when the page acts as a link to another resource, captioned
   * "Link Url", or `null` when the page hosts content of its own.
   *
   * At most 255 characters.
   */
  readonly url: string | null;

  /**
   * The menu icon reference, captioned "Icon", or `null` for none.
   *
   * MIGRATION: this may arrive as an unresolved `fileid=NNN` token rather than as a usable path. The
   * base table stores the raw value and only the legacy read view resolved it against the file table,
   * so resolution belongs to the repository or the mapper and a consumer must tolerate either form.
   * At most 100 characters.
   */
  readonly iconFile: string | null;
}

/**
 * One page in full, returned by `GET /api/v1/tabs/{tabId}` and echoed back by
 * `PUT /api/v1/tabs/{tabId}`.
 *
 * Mirrors `Dtos/Tab/TabDetailDto.cs`. A read projection with nine members more than the list row:
 * the description, the search keywords, the two dates, the refresh interval, the head markup, the
 * tenant identifier and the two opaque presentation tokens.
 *
 * Every member is inert. Nothing here resolves a file, walks the hierarchy or computes a value on
 * access - see the file header for why that constraint is stated so emphatically.
 */
export interface TabDetail {
  /**
   * The page's identifier.
   *
   * Zero is a legitimate value, because `dbo.Tabs.TabID` is `IDENTITY (0, 1)`. Never read zero as
   * "not yet saved".
   */
  readonly tabId: number;

  /**
   * The sort position of this page among its siblings under the same parent.
   *
   * Server-owned, and therefore absent from the update shape: the legacy update path accepted no
   * order argument at all, and the ordering routine was deliberately invoked with zero so that it
   * would recalculate the value itself.
   */
  readonly tabOrder: number;

  /**
   * The owning tenant, or `null` when this is a host-level page.
   *
   * MIGRATION: the legacy property was a non-nullable integer that used `-1` to mean "host page". The
   * backend converts that sentinel to a genuine null, because `dbo.Portals.PortalID` is
   * `IDENTITY (-1, 1)` and `-1` is therefore at once a real tenant key and the legacy absence marker;
   * preserving the sentinel would have made the two cases indistinguishable. Test the host case with
   * `portalId === null`. This member appears on this shape alone - the list row omits it because its
   * route is already portal-scoped, and the update shape omits it because the legacy editor never let
   * an administrator choose it either.
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
   * Captioned "Parent Page". See the file header: `null` is the boundary representation of the legacy
   * `-1`, and `0` names a real parent page. Test the root case with `parentId === null`.
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
   * MIGRATION: may arrive as an unresolved `fileid=NNN` token rather than as a usable path; see the
   * list row's note. At most 100 characters.
   */
  readonly iconFile: string | null;

  /**
   * Whether the page is disabled, captioned "Disabled".
   *
   * MIGRATION: a known legacy behaviour is preserved rather than corrected here. The legacy editor
   * assigned this flag only when the page being edited was none of the five protected system pages -
   * the administration, splash, home, login and account pages - and for those five it silently
   * discarded the posted value and left the flag false, showing the administrator no message at all.
   * Behavioural equivalence takes precedence over opportunistic correction, so the behaviour is
   * recorded rather than repaired; changing it would be a separate, deliberately documented decision.
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
   * MIGRATION: the column and the legacy property are both spelled with an interior capital
   * (`TabInfo.vb:L58`); the backend renames the member to this single-word form and maps it back to
   * the column in the persistence layer, so the wire name is `keywords`. Never reproduce the legacy
   * spelling on this contract - it would resolve to `undefined` with no compile error. At most 500
   * characters.
   */
  readonly keywords: string | null;

  /**
   * Whether the page is soft-deleted into the recycle bin.
   *
   * Neither filtered nor hidden by this contract: a soft-deleted page is still a row, and the service
   * and endpoint decide which rows a caller may see.
   */
  readonly isDeleted: boolean;

  /**
   * The navigation target used when the page acts as a link to another resource, captioned
   * "Link Url", or `null` when the page hosts content of its own.
   *
   * At most 255 characters.
   */
  readonly url: string | null;

  /**
   * The opaque skin token applied to this page, captioned "Page Skin", or `null` for none.
   *
   * MIGRATION: this is a genuinely stored column that the terminal update procedure writes, so it
   * belongs on both the read and the write surface and is carried here rather than dropped. Skinning
   * itself is nevertheless out of scope: the value is INERT in this target. It is stored and returned
   * verbatim as an opaque token, and nothing anywhere resolves it, loads it or renders it, because
   * there is no server-side rendering left to render it with. Its derived resolved-path companion
   * (`TabInfo.vb:L347`) is omitted for exactly that reason. At most 200 characters.
   */
  readonly skinSrc: string | null;

  /**
   * The opaque container token applied to the modules on this page, captioned "Page Container", or
   * `null` for none.
   *
   * MIGRATION: as with the skin token, this is a genuinely stored column that the terminal update
   * procedure writes, so it is carried on both surfaces; and as with the skin token it is INERT here,
   * stored and returned verbatim with no resolution, no file lookup and no rendering anywhere in this
   * target. Its derived resolved-path companion (`TabInfo.vb:L356`) is omitted. At most 200
   * characters.
   */
  readonly containerSrc: string | null;

  /**
   * The materialised hierarchical path of this page, or `null` when none is stored.
   *
   * Server-generated from the parent chain and the page name - the legacy code assigned it exclusively
   * through its path-generating helper and never accepted it from a client - so it is absent from the
   * update shape. At most 255 characters.
   */
  readonly tabPath: string | null;

  /**
   * The instant from which the page becomes available as an ISO 8601 string, or `null` when no start
   * date is set. Captioned "Start Date:" on the legacy screens.
   *
   * MIGRATION: `null` here is the boundary representation of the legacy null-date sentinel that the
   * legacy editor wrote whenever the box was left blank; the minimum instant is never emitted. No
   * ordering against the end date is asserted, because the legacy editor asserted none.
   */
  readonly startDate: string | null;

  /**
   * The instant after which the page ceases to be available as an ISO 8601 string, or `null` when no
   * end date is set. Captioned "End Date:" on the legacy screens.
   *
   * MIGRATION: as with the start date, `null` is the boundary representation of the legacy null-date
   * sentinel and the minimum instant is never emitted. Never assume this date follows the start date:
   * the legacy application checked only the format of each field in isolation, and no cross-field
   * constraint is asserted anywhere in this contract.
   */
  readonly endDate: string | null;

  /**
   * The automatic refresh interval in SECONDS, or `null` when the page does not refresh
   * automatically. Captioned "Refresh Interval (seconds)" on the legacy screens.
   *
   * The unit is stated explicitly because the member name alone does not carry it and a consumer could
   * otherwise reasonably guess milliseconds or minutes.
   *
   * MIGRATION: the legacy representation of "no automatic refresh" was the `-1` integer sentinel,
   * which survived into storage because the legacy editor overwrote it only when the supplied text was
   * both non-empty and numeric. The backend converts it to a genuine null, so `-1` is never emitted
   * here. Test with `refreshInterval === null`. The legacy field carried no validation rule of any
   * kind, and none has been invented.
   */
  readonly refreshInterval: number | null;

  /**
   * Raw markup injected into the document head when the page is rendered, captioned "Page Header
   * Tags", or `null` when none is stored.
   *
   * MIGRATION: this is arbitrary author-supplied markup, carried verbatim. This contract neither
   * parses, validates, sanitises nor escapes it, so a consumer that chooses to render it owns that
   * decision and its consequences entirely - it must never be bound as trusted markup. At most 500
   * characters.
   */
  readonly pageHeadText: string | null;

  /**
   * Whether the page must be served over a secure connection, captioned "Secure?".
   */
  readonly isSecure: boolean;

  /**
   * Whether any other page names this one as its parent.
   *
   * A computed existence projection rather than a stored column. As on the list row it is a genuine
   * JSON boolean, even though the legacy read view emitted the strings `'true'` and `'false'`.
   */
  readonly hasChildren: boolean;
}


/**
 * The body of `PUT /api/v1/tabs/{tabId}`.
 *
 * Mirrors `Dtos/Tab/UpdateTabRequest.cs`. Members are writable rather than read-only, because a
 * caller assembles this shape.
 *
 * MIGRATION: this is a COMPLETE REPLACEMENT of the editable subset, exactly as the legacy postback
 * was - not a partial patch. An omitted field does not mean "leave that column alone": the server
 * writes the absent value, which clears a nullable column, sets a boolean to false, and moves the page
 * to the root. Every member below is consequently mandatory, and a caller must send the page's current
 * values for anything it does not intend to change. That is deliberate and matches the legacy screen,
 * where an empty text box posted an empty value; without it an administrator could set a link target
 * but never clear one.
 *
 * MIGRATION: the identifier is route-supplied and never body-supplied. The legacy editor took it from
 * the page's own context and no form field ever contributed it, so this shape declares no identifier
 * member at all and the route value is authoritative. The tenant identifier is absent for the same
 * reason. The sort position, the depth and the materialised path are absent because the server
 * recomputes all three after a move, and the child-existence flag is absent because it is computed
 * rather than stored.
 */
export interface UpdateTabRequest {
  /**
   * The page name, captioned "Page Name" on the legacy screens.
   *
   * Mandatory, and at most 50 characters. This is the one presence rule the legacy form declared, and
   * the backend validator reproduces it as a non-empty rule rather than a non-null one, so that a
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
   * At most 500 characters. Note the single-word spelling: the legacy column and property carry an
   * interior capital, and the backend maps this member onto that column, so the wire name is
   * `keywords`.
   */
  readonly keywords: string | null;

  /**
   * The page this one should hang beneath, or `null` to make it a root-level page.
   *
   * MIGRATION: `null` is an instruction to MOVE THE PAGE TO THE ROOT. It does not mean "leave the
   * parent as it is" - this request replaces the editable subset wholesale. Because the page key is
   * seeded at zero, a submitted `0` names a real parent page and must never be read as "no parent".
   * Changing this moves the page and every descendant with it, after which the server recomputes the
   * depth, the sibling order and the materialised path of the affected subtree; none of those three is
   * accepted from a client.
   *
   * Two rejections are enforced by the server rather than by this contract: a parent that would create
   * a cycle, whether by naming the page itself or by naming one of its own descendants, and a parent
   * belonging to a different tenant. The second matters more here than it did on the legacy screen,
   * because the value now arrives in a request body rather than from a tenant-filtered picker.
   *
   * MIGRATION: a legacy defect is recorded and deliberately left unfixed. When the legacy screen's
   * self-parent test or its recursive ancestry walk tripped, it simply skipped the update and reported
   * nothing at all - a silent no-op that a user could easily mistake for success. The condition is
   * preserved; how the failure is surfaced is a service concern, and this shape neither detects nor
   * reports it.
   */
  readonly parentId: number | null;

  /**
   * Whether the page should appear in the navigation menu, captioned "Include In Menu".
   *
   * Menu inclusion only. Note the consequence of complete-replacement semantics: no default is
   * supplied, so omitting the field binds it to false and drops the page out of the menu.
   */
  readonly isVisible: boolean;

  /**
   * Whether the page should be disabled, captioned "Disabled".
   *
   * The server ignores this for the five protected system pages, preserving the legacy behaviour
   * described on the detail shape.
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
  //   server contract that carried them, on the reasoning that a column the terminal procedure persists
  //   must be settable or a save would discard it. That reasoning is subordinate to an explicit
  //   exclusion: skinning and containers are out of scope for this migration, and a member that a client
  //   can set is not an inert one — declaring it made this the supported way to change a page's skin and
  //   published it as part of the page-edit contract. Both columns are still READABLE, on
  //   {@link TabDetail}, so a stored choice remains observable; it is simply no longer settable through
  //   this API, and the server's projection now leaves both columns exactly as stored. That is strictly
  //   safer than what it replaced: because this body is a whole-row replacement, a caller that omitted
  //   either member previously BLANKED an administrator's stored token on every unrelated edit.

  /**
   * The navigation target when the page should act as a link to another resource, captioned
   * "Link Url", or `null` to clear it.
   *
   * At most 255 characters, and it must occupy a single line: the backend refuses any Unicode control
   * character, on the ground that no single-line form field could ever have submitted one even though
   * the column could store it.
   */
  readonly url: string | null;

  /**
   * The instant from which the page becomes available as an ISO 8601 string, or `null` for no start
   * bound. Captioned "Start Date:" on the legacy screens.
   *
   * MIGRATION: send `null` rather than the minimum instant to mean "unset" - the legacy editor wrote
   * that sentinel and this contract does not. The backend checks only that a supplied instant is
   * storable by the column, which begins centuries later than the platform date type does; that is a
   * representability check and not a business rule, and no ordering against the end date is imposed.
   */
  readonly startDate: string | null;

  /**
   * The instant after which the page ceases to be available as an ISO 8601 string, or `null` for no
   * end bound. Captioned "End Date:" on the legacy screens.
   *
   * MIGRATION: as with the start date, send `null` rather than the minimum instant to mean "unset".
   * The two dates are never compared against one another, here or on the server.
   */
  readonly endDate: string | null;

  /**
   * The automatic refresh interval in SECONDS, or `null` for no automatic refresh.
   *
   * MIGRATION: send `null` rather than `-1`, which was the legacy sentinel. The legacy field carried
   * no validation rule whatsoever and the backend invents none, so a caller is responsible for
   * submitting a sensible interval.
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
   * MIGRATION: this is the only member of this shape with no counterpart control on the legacy
   * page-settings form, and it is present for a measured reason. In the legacy application both
   * recycle-bin transitions were plain writes of this one flag through the very same update call that
   * this endpoint replaces - soft delete set it and saved (`TabController.vb:L836-L837`), and restore
   * cleared it and saved through the identical call (`RecycleBin.ascx.vb:L278-L279`). Because this
   * surface exposes no deletion route and no restoration route, this member is the sole means of
   * reaching either transition, and dropping it would have removed the capability from the product
   * altogether. Permanent deletion is a different operation entirely and is out of scope; this member
   * expresses only the reversible transition.
   *
   * Mind the complete-replacement semantics: no default is supplied, so omitting this field binds it
   * to false and a routine edit would RESTORE a page that was sitting in the recycle bin. A caller
   * editing a recycled page must send `true` to keep it there. The legacy screen behaved identically,
   * hard-coding the flag to false on every save.
   *
   * Two constraints are enforced by the server rather than here, because each needs other rows read:
   * restoration was blocked while the page's own parent remained deleted, and deletion was refused
   * outright both for the five protected system pages and for a page that still had descendants.
   */
  readonly isDeleted: boolean;
}
