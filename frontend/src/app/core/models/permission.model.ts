/**
 * Client-side type declarations for the DotNetNuke permission catalogue.
 *
 * Two declarations live here — {@link PermissionKey} and {@link Permission}.
 * They are consumed by the `hasPermission` structural directive, the permission
 * route guard and the read-only permission API.
 *
 * THIS FILE IS TYPE-ONLY AND EMITS NO RUNTIME JAVASCRIPT. A string-literal
 * union and one interface are erased entirely by the compiler, so nothing
 * here reaches a bundle. That is deliberate: a permission decision is not the
 * browser's to make (see the enforcement note below), so this module has no
 * behaviour to contribute — only the shape of the data it describes. There is
 * no evaluation helper, no parser and no comparison routine, and none should be
 * added. It also takes NO imports, which is why the workspace's absent `paths`
 * and `baseUrl` compiler options cannot bite here.
 *
 * CLIENT-SIDE PERMISSION CHECKS ARE NEVER AUTHORITATIVE. The `hasPermission`
 * directive is NEVER the sole enforcement mechanism. It hides an affordance the
 * caller may not use, which is a courtesy to the person at the screen and
 * nothing more — anyone can edit the rendered DOM or call the endpoint
 * directly. Authorisation is decided on the server by
 * `Infrastructure/Security/PermissionEvaluator.cs` and
 * `Api/Authorization/PermissionAuthorizationHandler.cs`, and a refused request
 * comes back as HTTP 403 regardless of what the browser chose to render. Treat
 * every value described by this file as a rendering hint, never as a guarantee.
 *
 * THE WIRE CONTRACT: PRESENT-BUT-POSSIBLY-NULL, NEVER OMITTED. The API
 * serialises with `DefaultIgnoreCondition` set to `Never` and a camelCase
 * property naming policy, configured once in
 * `Api/Extensions/ServiceCollectionExtensions.cs`. The ignore condition is
 * pinned there precisely because the legacy encoding makes `-1`, `0`, the empty
 * string and `false` all legitimate values, so a condition that omitted nulls
 * or defaults would erase real data from every response. The practical
 * consequence for models generally is that a nullable member arrives PRESENT
 * with the value `null`, never as an omitted property. The two catalogue shapes
 * below happen to contain no nullable member.
 *
 * SENTINEL DISCIPLINE — WHY NO IDENTIFIER HERE IS TESTED FOR TRUTHINESS. The
 * legacy code encoded absence in-band rather than with a null:
 * `Library/Components/Shared/Null.vb` returns `-1` for `NullInteger` (L41-L45)
 * and — the trap — the EMPTY STRING for `NullString` (L71-L75), never a null.
 * Its `IsNull` helper (L207-L237) consequently reports `true` for `-1`, for `""`
 * AND for `false`. Meanwhile the schema seeds identities into that same value
 * space: `Roles.RoleID`, `Tabs.TabID` and `Modules.ModuleID` are all
 * `IDENTITY(0, 1)` and `Portals.PortalID` is `IDENTITY(-1, 1)`, so `0` is a
 * real role, tab and module identifier and `-1` is a real portal identifier. So
 * never write `if (id)`, `id > 0`, `id <= 0` or `id ?? -1` against anything
 * here — each of those silently discards a legitimate row. Compare explicitly
 * with `=== null` instead. For the same reason a text member that the server
 * has nothing to say about arrives as `""` rather than as `null`, which is why
 * every string member below is a plain required `string`.
 *
 * IDENTIFIER SPELLING. Every identifier member ends in a single lower-case `d`
 * — `permissionId` and `moduleDefId`. This is not cosmetic. The camelCase
 * policy lower-cases a LEADING RUN of capitals, so a server property spelled
 * `PermissionID` would serialise as `permissionID` while `PermissionId`
 * serialises as `permissionId`. The server DTOs use the `...Id` spelling, so
 * these names match; had they not, the mismatch would surface as `undefined` at
 * runtime with no compile error anywhere to catch it.
 *
 * ROUTE PARAMETER NAMES. The server's authorisation handler reads the scope
 * identifier out of route data, trying `moduleId` and then `id` for a
 * module-scoped policy, and `tabId` and then `id` for a tab-scoped one. A route
 * that spelled a parameter `moduleID` would
 * still match its own path and still render, but the handler would fail to find
 * the scope and refuse the request — so keep route parameter spellings in step
 * with the member names above.
 */

// MIGRATION: the legacy catalogue class was named `PermissionInfo`
//   (`Library/Components/Security/Permissions/Permission.vb:L28`). It is named
//   `Permission` here, dropping the `Info` suffix that the legacy codebase
//   applied to every data-carrying class. The five members are carried across
//   one for one; only the suffix and the XML serialisation decoration are gone.
//   The legacy `<XmlElement>` and `<XmlIgnore>` attributes on those properties
//   described a serialisation format this application does not speak, so they
//   have no counterpart here — the wire shape is owned by the API boundary.

// MIGRATION: the legacy delimited permission string is DELIBERATELY NOT CARRIED
//   FORWARD. The legacy code flattened a set of grants into a single string —
//   granted role names run together with a semicolon separator, each granted
//   user identifier rendered instead as its number wrapped in square brackets,
//   the whole lot semicolon-terminated — and then re-parsed that string to make
//   decisions. It is not a stray idiom: there are 17 semicolon-join sites and 8
//   bracket-wrapping sites across
//   `Library/Components/Security/Permissions/*.vb`, including
//   `ModulePermissionController.vb:L239-L251` and `:L328`, with the tab
//   equivalent at `TabPermissionController.vb:L214-L226`. The format existed
//   only because those helpers had to hand a single string to a role-membership
//   test; it is lossy (a role whose name contains a semicolon cannot survive
//   it) and it forced every consumer to re-derive structure that the database
//   already had. Grant rows remain an authoritative server-side concern. The
//   current API produces catalogue definitions and bare keys, not module/tab
//   grant DTOs, so this client declares NO grant wire shape, NO delimited string
//   member and NO parser. Nothing downstream should reconstruct that format.

// MIGRATION: two different permission vocabularies exist in this system and
//   only ONE of them is modelled here. The first is the persisted key — the
//   four values of `PermissionKey` below, stored in a database column. The
//   second is the set of server-side authorisation POLICY names, which are
//   ASP.NET Core policy identifiers registered at start-up and are not data at
//   all. They are deliberately absent from this file. Merging the two into one
//   union would be actively dangerous: no dynamic policy provider is
//   registered, so a policy name that was never registered throws when the
//   request is authorised rather than failing gracefully, and a value from the
//   wrong vocabulary would read as plausible right up to that point. A
//   persisted key is data that arrives from the server; a policy name is a
//   server implementation detail the browser never needs.

// MIGRATION: there is NO negative-permission or refusal concept in this
//   DotNetNuke generation, and none is modelled. Later permission systems mark a
//   refusal by prefixing the key with an exclamation mark; searching this
//   codebase for that convention across `Library/Components/Security/` returns
//   zero hits for such a literal and zero for a prefix test against one. A grant
//   is expressed positively, by the presence of a row whose `allowAccess` is
//   `true`. So there is no refusal member, no negated key value and no notion of
//   a permission that subtracts, and adding one would invent a model the data
//   does not have. The server evaluates its stored grant rows; the client models
//   only the resulting catalogue vocabulary.

/**
 * The DotNetNuke permission keys: the discrete access rights a catalogue entry
 * can name.
 *
 * THE VALUE IS THE MEMBER NAME, IN UPPER CASE, AND IT IS LOAD-BEARING DATA.
 * These four strings are what a production database already contains:
 * `Permission.PermissionKey` is declared `varchar(20) NOT NULL`
 * (`Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider:L688`).
 * The legacy code compares them with exact string equality and never
 * case-insensitively — see `ModulePermissionController.vb:L36`, `:L243` and
 * `:L333`, `TabPermissionController.vb:L41` and `:L218`, and
 * `PortalController.vb:L1416`. Never rename these tokens, never case-fold them,
 * never pluralise them and never replace them with numbers. A value spelled
 * `'View'` or `'view'` matches no stored row and fails silently, granting
 * nothing.
 *
 * WHY A STRING UNION AND NOT A NUMERIC ENUM. The server models this as an
 * enumeration whose members carry no explicit numeric values, and its ordinals
 * are meaningless — they are never persisted and never serialised. A dedicated
 * converter pins the wire form to the member NAME rather than the number, so
 * these exact upper-case strings are what arrives in JSON. A string union
 * mirrors that precisely, and unlike a TypeScript `enum` it also compiles to
 * nothing at all, which suits a type-only module. (A compile-time constant
 * enumeration would be doubly wrong here: the workspace sets `isolatedModules`,
 * which forbids that form outright.)
 *
 * `EDIT` ALSO COVERS REMOVAL. There is no separate deletion key in this
 * generation: `EDIT` denotes full administrative control of an item and covers
 * creating, updating and removing it. Do not go looking for a deletion key and
 * do not add one.
 *
 * `READ` AND `WRITE` BELONG TO FOLDER SCOPE. They are seeded against the
 * `SYSTEM_FOLDER` permission code and never appear in a module or tab
 * authorisation policy. They are nonetheless part of the vocabulary rather than
 * decoration: `PortalController.vb:L1416` compares against `READ` while seeding
 * a new portal's root folder. They are declared here so the catalogue is
 * representable in full; a module or tab screen will simply never encounter
 * them.
 *
 * THE SET IS CLOSED AT FOUR. Keys belonging to later DotNetNuke versions or to
 * general-purpose permission vocabularies — rights for deployment, addition,
 * removal, management, administration, creation, export, import or blanket full
 * control — do not exist in this codebase and must not be added. Apparent
 * sightings in the legacy tree are something else entirely: the sole upper-case
 * addition literal is a radio-button value on an admin import screen, and
 * management and import also occur as module control keys. Neither is a
 * permission key.
 *
 * A GRANT IS A ROW, NOT A BIT. Each catalogue entry names exactly one key, and
 * the server evaluates separate stored module or page grant rows. These values
 * are NOT bit-mask flags: they must never be combined, or-ed together or treated
 * as a set packed into one value.
 *
 * As with everything in this file, a check against one of these values in the
 * browser is a rendering hint — the `hasPermission` directive is NEVER the sole
 * enforcement mechanism, and the server decides independently.
 *
 * Note that the read-only permission LIST endpoint publishes bare keys of this
 * type, NOT full {@link Permission} records; see {@link Permission} for which
 * endpoints return which shape.
 */
export type PermissionKey = 'VIEW' | 'EDIT' | 'READ' | 'WRITE';

/**
 * One entry in the permission catalogue: a definition of an access right,
 * scoped to a subsystem.
 *
 * This is a DEFINITION, not a grant. It says "an access right called `EDIT`
 * exists for module definitions"; it does not say that anybody holds it. The
 * current API does not publish module- or page-grant rows, so no client wire type
 * claims to extend this definition with a role or user.
 *
 * WHICH ENDPOINT RETURNS THIS SHAPE. The catalogue's own list endpoint
 * deliberately publishes a bare array of {@link PermissionKey} strings rather
 * than these records, because a list of keys is precisely what the vocabulary
 * is and what a client-side permission check tests against. Full records of
 * this shape come from the reads that identify a particular definition or the
 * definitions applicable to a particular module or tab, where a key alone would
 * be ambiguous. Do not attempt to parse the bare-key list response into an
 * array of this type; the two shapes are different on purpose.
 *
 * Mirrors the server's permission DTO in
 * `Application/Abstractions/IPermissionService.cs`. Every member is required
 * and non-nullable there, matching the five `NOT NULL` columns of the
 * `Permission` table, so no member here is optional or nullable.
 *
 * Derived from `PermissionInfo`
 * (`Library/Components/Security/Permissions/Permission.vb:L28-L85`), whose five
 * properties map one for one onto the five below.
 */
export interface Permission {
  /**
   * Identifier of the permission definition, from `Permission.PermissionID`.
   *
   * The column is `IDENTITY(1, 1)`, so — unusually for this schema — no value
   * here collides with a sentinel. It is still never compared against a bound:
   * treat it as an opaque key.
   */
  readonly permissionId: number;

  /**
   * The subsystem this definition is scoped to, from
   * `Permission.PermissionCode`.
   *
   * DELIBERATELY A PLAIN `string` AND NOT A UNION OF THE OBSERVED VALUES. The
   * three values this codebase seeds are `SYSTEM_TAB`,
   * `SYSTEM_MODULE_DEFINITION` and `SYSTEM_FOLDER`, and that list is
   * illustrative documentation only — it is neither exhaustive nor enforced.
   * The column is `varchar(50)`, the server models it as free text and invents
   * no enumeration for it, and an installation may carry a scope this codebase
   * has never seen. Narrowing it would turn such a value into a compile error
   * at the consumer and force a cast, which is strict typing producing exactly
   * the unsafety it exists to prevent.
   *
   * Arrives as `""` rather than as `null` when the server has nothing to say,
   * per the sentinel note in this file's header.
   */
  readonly permissionCode: string;

  /**
   * The module definition this permission was declared under, from
   * `Permission.ModuleDefID`.
   *
   * Zero for a definition that belongs to no module definition, which is how
   * the page-scoped and folder-scoped rows are stored — so `0` is meaningful
   * here and must not be read as "missing". Not nullable, because the column is
   * not.
   */
  readonly moduleDefId: number;

  /**
   * The access right itself.
   *
   * See {@link PermissionKey} for why this is an upper-case string rather than
   * a number, and why the four values must never be re-spelled.
   */
  readonly permissionKey: PermissionKey;

  /**
   * Human-readable name of the definition, from `Permission.PermissionName`,
   * suitable for display in a permissions grid header.
   *
   * Distinct from {@link permissionKey}: the key is the machine value that is
   * compared, this is the label that is shown. Never compare against this.
   */
  readonly permissionName: string;
}
