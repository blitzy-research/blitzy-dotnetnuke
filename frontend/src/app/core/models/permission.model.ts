/**
 * Client-side type declarations for the DotNetNuke permission model: the
 * permission catalogue and the two permission-grant shapes that bind a
 * catalogue entry to a role or to a single user.
 *
 * Four declarations live here — {@link PermissionKey}, {@link Permission},
 * {@link ModulePermission} and {@link TabPermission}. They are consumed by the
 * `hasPermission` structural directive, by the permission route guard, and by
 * the module and tab feature screens.
 *
 * THIS FILE IS TYPE-ONLY AND EMITS NO RUNTIME JAVASCRIPT. A string-literal
 * union and three interfaces are erased entirely by the compiler, so nothing
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
 * consequence for the members below: a nullable member arrives PRESENT with the
 * value `null`, so it is declared `| null` rather than `?:`. That matches every
 * other model in this folder except the problem-details model, which documents
 * its own opposite convention and is the one exception.
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
 * — `permissionId`, `moduleDefId`, `modulePermissionId`, `tabPermissionId`,
 * `roleId`, `userId`, `tabId`, `moduleId`. This is not cosmetic. The camelCase
 * policy lower-cases a LEADING RUN of capitals, so a server property spelled
 * `PermissionID` would serialise as `permissionID` while `PermissionId`
 * serialises as `permissionId`. The server DTOs use the `...Id` spelling, so
 * these names match; had they not, the mismatch would surface as `undefined` at
 * runtime with no compile error anywhere to catch it.
 *
 * ROUTE PARAMETER NAMES. The server's authorisation handler reads the scope
 * identifier out of route data, trying `moduleId` and then `id` for a
 * module-scoped policy, and `tabId` and then `id` for a tab-scoped one. The
 * route parameters this application uses are therefore `portalId`, `moduleId`,
 * `userId` and `roleId`. A route that spelled a parameter `moduleID` would
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
//   already had. The underlying model is a first-class `UserID` column
//   discriminated against a sentinel plus a separate `AllowAccess` flag, and
//   that structured shape is what crosses the wire now. So this file declares
//   `roleId`, `roleName`, `userId` and `allowAccess` as distinct members and
//   declares NO delimited string member and NO parser. Nothing downstream
//   should reconstruct that format.

// MIGRATION: TypeScript composition replaces VB inheritance. Both legacy grant
//   classes declared `Inherits PermissionInfo`
//   (`ModulePermission.vb:L29`, `TabPermission.vb:L30`), so each carried its own
//   8 members plus the base 5, for 13 in total. VB inheritance does not survive
//   a language change on its own, so the relationship is restated explicitly
//   with TypeScript `extends` below. That the base members are genuinely part of
//   each grant is not an assumption: the legacy copy constructor at
//   `ModulePermission.vb:L55-L63` copies all five across, and the `Equals`
//   override at `:L158-L165` compares the inherited `PermissionID` when
//   de-duplicating a grant collection.

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
//   does not have.

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
 * access is granted by a separate {@link ModulePermission} or
 * {@link TabPermission} row. These values are NOT bit-mask flags: they must
 * never be combined, or-ed together or treated as a set packed into one value.
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
 * holding of a right is a {@link ModulePermission} or {@link TabPermission},
 * each of which extends this shape to add the role or user it was granted to.
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

/**
 * A grant of one permission over one module placement, to either a role or a
 * single user.
 *
 * Extends {@link Permission}, so all five catalogue members — `permissionId`,
 * `permissionCode`, `moduleDefId`, `permissionKey` and `permissionName` — are
 * present on this shape in addition to the eight declared below. That mirrors
 * the legacy `ModulePermissionInfo`, which declared 8 members of its own and
 * inherited 5, for 13 in total
 * (`Library/Components/Security/Permissions/ModulePermission.vb:L28-L136`).
 *
 * ROLE GRANT OR USER GRANT — READ {@link userId} FIRST. A row is one or the
 * other, and {@link userId} is what distinguishes them. Everything else about
 * the row is interpreted in light of that single member, so a consumer that
 * ignores it will attribute a user's grant to a role and vice versa.
 *
 * A GRANT IS NOT A PERMISSION UNTIL {@link allowAccess} IS `true`. The presence
 * of a row is not sufficient — the flag is a separate, independent gate.
 */
export interface ModulePermission extends Permission {
  /**
   * Identifier of this grant row, from `ModulePermission.ModulePermissionID`.
   *
   * `IDENTITY(1, 1)`, so no value collides with a sentinel. Opaque: it
   * identifies the grant for update and delete, and carries no other meaning.
   */
  readonly modulePermissionId: number;

  /**
   * The module placement this grant applies to, from
   * `ModulePermission.ModuleID`.
   *
   * `Modules.ModuleID` is `IDENTITY(0, 1)`, so `0` IS A REAL MODULE. Never test
   * this for truthiness and never treat `0` as absent.
   */
  readonly moduleId: number;

  /**
   * The role the permission is granted to, from `ModulePermission.RoleID`.
   *
   * MEANINGFUL ONLY WHEN {@link userId} IS `null`. On a user grant this member
   * carries no useful information and must be ignored.
   *
   * DELIBERATELY A PLAIN `number`, INCLUDING NEGATIVES AND ZERO — do not
   * narrow it to a union or an enumeration, and do not filter negatives out.
   * Two separate facts make that necessary. First, `Roles.RoleID` is
   * `IDENTITY(0, 1)`, so `0` is an ordinary role. Second,
   * `Library/Components/Shared/Globals.vb:L95-L98` reserves four NEGATIVE
   * identifiers for roles that are not rows in the `Roles` table at all:
   * `-1` for all users, `-2` for super users, `-3` for unauthenticated users
   * and `-4` for no role. These are genuinely used as role identifiers — the
   * portal-creation path passes the all-users value straight into a permission
   * row at `PortalController.vb:L1416-L1418` — so a type that excluded them
   * would make a legitimate grant unrepresentable. Note that only three of the
   * four have a matching display name constant; the no-role value has none,
   * which is one reason {@link roleName} can arrive empty.
   *
   * A note on nullability, because the layers differ and the difference is
   * deliberate rather than an oversight. The database column and the server's
   * internal entity both allow a null here, which is how the storage layer
   * records "this row grants to a user, not a role". The legacy in-memory model
   * never did: it initialised the field to the reserved no-role value `-4`
   * (`ModulePermission.vb:L47`) and left it a plain integer. This member follows
   * the legacy contract, so absence of a role arrives in-band as one of the
   * reserved values rather than as a null. Use {@link userId} to decide which
   * kind of grant you are looking at — never the presence or absence of this
   * member.
   */
  readonly roleId: number;

  /**
   * Display name of the granted role, for rendering in a permissions grid.
   *
   * A convenience projection joined in by the server, not a stored column of
   * the grant row. Arrives as `""` rather than as `null` when there is no role
   * name to show — on a user grant, or for the reserved no-role identifier
   * which has no display-name constant. A plain required `string` for exactly
   * that reason; see the sentinel note in this file's header. Never compare
   * against this to make a decision — it is a label, and {@link roleId} is the
   * identity.
   */
  readonly roleName: string;

  /**
   * The user the permission is granted to, from `ModulePermission.UserID`, or
   * `null` when this row grants to a role rather than to an individual.
   *
   * THIS MEMBER IS THE ROLE-VERSUS-USER DISCRIMINATOR, AND IT IS THE ONLY
   * RELIABLE ONE. `null` means "this is a role grant" — read {@link roleId} and
   * {@link roleName}. A number means "this is a user grant" — read
   * {@link username} and {@link displayName}, and ignore {@link roleId}. The
   * legacy code made exactly this branch, testing the identifier against its
   * null sentinel at `ModulePermissionController.vb:L37` and again at `:L244`,
   * with the tab equivalent at `TabPermissionController.vb:L42` and `:L219`.
   *
   * Compare with `=== null`, never with truthiness. `if (userId)` is wrong for
   * two independent reasons: it treats a legitimate `0` as absent, and it reads
   * as correct while being wrong, which is how this class of bug survives
   * review. The habit matters more than this one field — `0` and `-1` are both
   * real identifiers elsewhere in this model.
   *
   * One narrow caution. The legacy sentinel for an absent integer was `-1`, and
   * this member is the one place in this file where that sentinel is translated
   * into an honest `null`. Do NOT generalise that translation: `-1` is a valid
   * portal identifier (`Portals.PortalID` is `IDENTITY(-1, 1)`) and a reserved
   * role identifier, so `-1` elsewhere means what it says.
   */
  readonly userId: number | null;

  /**
   * Whether this row grants access, from `ModulePermission.AllowAccess`.
   *
   * NON-NULLABLE AND NON-OPTIONAL BY DESIGN — never widen this to
   * `boolean | null` and never make it optional. In the legacy encoding
   * `false` was itself the null sentinel for a boolean, so
   * `Library/Components/Shared/Null.vb` reports a plain `false` as null
   * (L227-L228) and a legacy `false` is therefore indistinguishable from
   * "unknown". Admitting `null` here would import that ambiguity into new code,
   * where it does not exist and cannot be resolved. The server keeps the same
   * discipline from the other direction: its serialiser is explicitly forbidden
   * from omitting default values, precisely so that every `false` is written
   * out rather than silently dropped.
   *
   * A grant is only effective when this is `true`. The legacy checks tested it
   * alongside the key rather than assuming it — `ModulePermissionController.vb`
   * requires `AllowAccess = True` at `:L243` and again at `:L333`.
   */
  readonly allowAccess: boolean;

  /**
   * Login name of the granted user, for rendering in a permissions grid.
   *
   * MEANINGFUL ONLY WHEN {@link userId} IS NOT `null`. Like
   * {@link roleName} this is a convenience projection rather than a stored
   * column of the grant row, and it arrives as `""` rather than as `null` on a
   * role grant. A label, not an identity — {@link userId} is the identity.
   */
  readonly username: string;

  /**
   * Display name of the granted user, preferred over {@link username} when
   * showing the grant to a person.
   *
   * MEANINGFUL ONLY WHEN {@link userId} IS NOT `null`, and arrives as `""`
   * rather than as `null` on a role grant or where the user has no display name
   * recorded. A consumer rendering this should fall back to {@link username}
   * when it is empty — and must test for the empty string, not for null.
   */
  readonly displayName: string;
}

/**
 * A grant of one permission over one page, to either a role or a single user.
 *
 * The page-scoped counterpart of {@link ModulePermission}, and structurally
 * identical to it apart from the two identifiers that name the scope. Extends
 * {@link Permission}, so all five catalogue members are present here too,
 * mirroring the legacy `TabPermissionInfo` — 8 own members plus 5 inherited, 13
 * in total (`Library/Components/Security/Permissions/TabPermission.vb:L29-L128`).
 *
 * "Tab" is the DotNetNuke term for a page in a portal's navigation hierarchy,
 * not a tab strip in a user interface. The legacy vocabulary is kept because the
 * table, the columns and the route parameters all use it, and renaming it here
 * would put this model out of step with every identifier it has to match.
 *
 * The two rules that govern {@link ModulePermission} govern this shape
 * identically: {@link userId} decides whether the row is a role grant or a user
 * grant, and {@link allowAccess} must be `true` before the row grants anything.
 */
export interface TabPermission extends Permission {
  /**
   * Identifier of this grant row, from `TabPermission.TabPermissionID`.
   *
   * `IDENTITY(1, 1)`, so no value collides with a sentinel. Opaque: it
   * identifies the grant for update and delete, and carries no other meaning.
   */
  readonly tabPermissionId: number;

  /**
   * The page this grant applies to, from `TabPermission.TabID`.
   *
   * `Tabs.TabID` is `IDENTITY(0, 1)`, so `0` IS A REAL PAGE. Never test this for
   * truthiness and never treat `0` as absent.
   */
  readonly tabId: number;

  /**
   * The role the permission is granted to, from `TabPermission.RoleID`.
   *
   * MEANINGFUL ONLY WHEN {@link userId} IS `null`. A plain `number` that
   * legitimately carries zero and the four reserved negative values; the full
   * reasoning, including why this member is not nullable while the underlying
   * column is, is given on {@link ModulePermission.roleId} and applies here
   * unchanged. The legacy default was likewise the reserved no-role value
   * (`TabPermission.vb:L48`).
   */
  readonly roleId: number;

  /**
   * Display name of the granted role, for rendering in a permissions grid.
   *
   * A server-joined convenience projection that arrives as `""` rather than as
   * `null` when there is no role name to show. A label, not an identity.
   */
  readonly roleName: string;

  /**
   * The user the permission is granted to, from `TabPermission.UserID`, or
   * `null` when this row grants to a role rather than to an individual.
   *
   * THIS MEMBER IS THE ROLE-VERSUS-USER DISCRIMINATOR. `null` means role grant;
   * a number means user grant. The legacy code branched on exactly this at
   * `TabPermissionController.vb:L42` and again at `:L219`. Compare with
   * `=== null`, never with truthiness — see
   * {@link ModulePermission.userId} for why the distinction is not pedantic.
   */
  readonly userId: number | null;

  /**
   * Whether this row grants access, from `TabPermission.AllowAccess`.
   *
   * NON-NULLABLE AND NON-OPTIONAL BY DESIGN, for the reason set out on
   * {@link ModulePermission.allowAccess}: the legacy null sentinel for a
   * boolean was `false` itself, so admitting `null` would import an ambiguity
   * that does not exist here. A grant is effective only when this is `true`;
   * `TabPermissionController.vb:L218` tests it alongside the key.
   */
  readonly allowAccess: boolean;

  /**
   * Login name of the granted user, for rendering in a permissions grid.
   *
   * MEANINGFUL ONLY WHEN {@link userId} IS NOT `null`; arrives as `""` rather
   * than as `null` on a role grant.
   */
  readonly username: string;

  /**
   * Display name of the granted user, preferred over {@link username} when
   * showing the grant to a person.
   *
   * MEANINGFUL ONLY WHEN {@link userId} IS NOT `null`; arrives as `""` rather
   * than as `null` on a role grant or where no display name is recorded. Fall
   * back to {@link username} when empty, testing for the empty string rather
   * than for null.
   */
  readonly displayName: string;
}
