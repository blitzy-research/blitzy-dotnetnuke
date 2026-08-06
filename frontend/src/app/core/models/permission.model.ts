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

import { decodeInteger, decodeString, objectOf, type Decoder } from '../utils/decode.util';

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
 * THIS UNION IS THE CLIENT'S VOCABULARY, NOT THE PRODUCER'S TYPE. The distinction
 * matters and is easy to lose. These four are the complete set of keys THIS
 * codebase seeds and evaluates, so they are the right type for a value the
 * application decides — the key a screen requires, the key a route guard tests.
 * They are the wrong type for a value the SERVER supplies: the permission
 * projection declares its key member as a plain string and the catalogue listing
 * publishes bare strings, because the column stores whatever was seeded and an
 * installation may hold a key this codebase has never seen. So a payload member is
 * typed `string` (see {@link Permission.permissionKey}) and is narrowed to this
 * union through the guard named there, never asserted into it.
 *
 * Note that the read-only permission LIST endpoint publishes bare key strings, NOT
 * full {@link Permission} records; see {@link Permission} for which endpoints
 * return which shape.
 */
export type PermissionKey = 'VIEW' | 'EDIT' | 'READ' | 'WRITE';

/**
 * The four permission keys as a runtime value, in the order the schema seeds them.
 *
 * {@link PermissionKey} is erased at compile time, so a union alone cannot answer a
 * question about a value that arrived over the wire. This array is the runtime half of
 * the same vocabulary and {@link isPermissionKey} is the only thing that reads it, which
 * is what keeps the two halves from drifting: the type is DERIVED from this array
 * through an indexed access, so adding an entry here widens the union and removing one
 * narrows it, and no second list exists to forget to update.
 *
 * Frozen, because a permission vocabulary that a caller could push onto would be a
 * vocabulary a caller could widen — and widening this list grants access.
 */
export const PERMISSION_KEYS = Object.freeze(['VIEW', 'EDIT', 'READ', 'WRITE'] as const);

/**
 * Whether a value is one of the four recognised permission keys.
 *
 * THE ONE SUPPORTED ROUTE FROM AN OPEN PRODUCER TO THE CLOSED VOCABULARY. Every
 * server-supplied key — {@link Permission.permissionKey}, and each entry of the bare-key
 * array the catalogue listing publishes — is typed `string` because the column stores
 * whatever an installation seeded. A permission DECISION, by contrast, must be made over
 * the closed set. This predicate is the boundary between the two, and it is a type guard
 * rather than a boolean helper so the compiler carries the narrowing forward and a caller
 * cannot forget to apply it.
 *
 * FAILS CLOSED, and that is the whole point. An unrecognised value — a key this codebase
 * has never seen, a policy name passed where a key was expected, a permission CODE, a
 * lower-case spelling, a padded string — answers false and is therefore discarded rather
 * than admitted. The failure mode being prevented is specific: an unknown string present
 * in the caller's granted list would otherwise satisfy a naive membership test and be
 * treated as a render grant.
 *
 * Accepts `unknown` rather than `string`, so it is equally usable on a value parsed from
 * a response, read from route data, or handed to a component input whose declared type
 * the caller may have subverted. A non-string answers false without any coercion — no
 * `String()` call, no trim, no case fold, because each of those would ADMIT a value the
 * server would refuse.
 *
 * Comparison is ordinal and case-sensitive, matching the legacy semantics exactly:
 * `Library/DotNetNuke.Library.vbproj:L22` declares `<OptionCompare>Binary</OptionCompare>`,
 * under which VB string equality is ordinal, and every legacy comparison site depended on
 * it. Case-folding here would widen access under the guise of convenience.
 *
 * @param value A candidate key from any source, trusted or not.
 * @returns True only when the value is exactly one of the four keys.
 */
export function isPermissionKey(value: unknown): value is PermissionKey {
  return typeof value === 'string' && (PERMISSION_KEYS as readonly string[]).includes(value);
}

/**
 * Keeps only the recognised permission keys from a list of wire values.
 *
 * The list form of {@link isPermissionKey}, provided because the shape a caller's granted
 * permissions actually arrive in is an array of open strings, and narrowing each element
 * at every call site would put the fail-closed rule in several places instead of one.
 *
 * Discarding rather than rejecting is deliberate. A granted list carrying one unknown key
 * alongside three recognised ones is not a malformed response — it is a response from an
 * installation that seeded a key this codebase does not evaluate. Refusing the whole list
 * would withdraw three valid grants over one irrelevant entry; discarding the unknown
 * entry withdraws exactly the grant that cannot be reasoned about. Either way the unknown
 * key never admits anything.
 *
 * A null or undefined list yields an empty result rather than throwing: absent grants and
 * no grants are the same statement, and every consumer of this function treats an empty
 * list as "nothing is admitted".
 *
 * @param values The granted keys exactly as the server published them.
 * @returns The subset that is recognised, in the order given, with duplicates preserved.
 */
export function toPermissionKeys(
  values: readonly string[] | null | undefined,
): readonly PermissionKey[] {
  if (values === null || values === undefined) {
    return EMPTY_PERMISSION_KEYS;
  }

  return values.filter(isPermissionKey);
}

/**
 * The result {@link toPermissionKeys} returns when there is nothing to keep.
 *
 * Shared and frozen rather than allocated per call, so a signal derived from it does not
 * appear to change on every evaluation.
 */
const EMPTY_PERMISSION_KEYS: readonly PermissionKey[] = Object.freeze([]);

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
   *
   * DELIBERATELY A PLAIN `string` AND NOT {@link PermissionKey}, for the same reason
   * {@link permissionCode} is not a union of its observed values — the producer is
   * open. The server declares this member on its permission projection as a plain
   * string, and the catalogue listing publishes a bare array of strings, because the
   * column stores whatever key is seeded and an installation may hold one this
   * codebase has never seen. Narrowing it here would statically type such a value as
   * a {@link PermissionKey} while it is not one, and a check against the four known
   * keys would then look exhaustive to a reader and to the compiler while silently
   * failing at run time. That is strict typing producing exactly the unsafety it
   * exists to prevent.
   *
   * NARROW IT WITH A GUARD, NEVER WITH A CAST. {@link isPermissionKey} takes an unknown
   * value and narrows it to {@link PermissionKey} only when it is exactly one of the four,
   * and {@link toPermissionKeys} does the same for a list — those two are the supported
   * route from this member to the closed vocabulary, and both fail closed. Comparing this
   * member directly against a key literal remains correct and needs no guard, because both
   * sides are strings; what needs the guard is treating the value AS the narrower type,
   * which is precisely what a permission DECISION does.
   *
   * Arrives as `""` rather than as `null` when the server has nothing to say, per the
   * sentinel note in this file's header.
   */
  readonly permissionKey: string;

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
 * Decodes one permission catalogue entry.
 *
 * `permissionKey` is decoded as a plain string rather than against {@link PermissionKey}, and
 * the choice is deliberate. The four keys that type names are the ones this application
 * SWITCHES on, but the catalogue is extensible: a module package may register its own key, and
 * closing the set here would refuse a whole catalogue because one third-party entry carried a
 * key this client had never heard of. Consumers narrow the string where they need to.
 *
 * `moduleDefId` uses {@link decodeInteger} with no positivity test, because the identity seeds
 * in this schema make zero an ordinary identifier.
 */
export const decodePermission: Decoder<Permission> = objectOf<Permission>({
  permissionId: decodeInteger,
  permissionCode: decodeString,
  moduleDefId: decodeInteger,
  permissionKey: decodeString,
  permissionName: decodeString,
});
