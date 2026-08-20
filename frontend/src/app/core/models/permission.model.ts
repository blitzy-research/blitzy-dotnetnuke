/**
 * Client-side type declarations for the DotNetNuke permission catalogue. Two declarations live here —
 * {@link PermissionKey} and {@link Permission}.
 */

import { decodeInteger, decodeString, objectOf, type Decoder } from '../utils/decode.util';

// The legacy catalogue class was named `PermissionInfo`
// (`Library/Components/Security/Permissions/Permission.vb:L28`). It is named `Permission` here, dropping
// the `Info` suffix that the legacy codebase applied to every data-carrying class.

// MIGRATION: the legacy delimited permission string is DELIBERATELY NOT CARRIED FORWARD. The legacy code
// flattened a set of grants into a single string — granted role names run together with a semicolon
// separator, each granted user identifier rendered instead as its number wrapped in square brackets, the
// whole lot semicolon-terminated — and then re-parsed that string to make decisions.

// Two different permission vocabularies exist in this system and only ONE of them is modelled here. The
// first is the persisted key — the four values of `PermissionKey` below, stored in a database column.

// There is NO negative-permission or refusal concept in this DotNetNuke generation, and none is modelled.

/**
 * The DotNetNuke permission keys: the discrete access rights a catalogue entry can name. THE VALUE IS THE
 * MEMBER NAME, IN UPPER CASE, AND IT IS LOAD-BEARING DATA. These four strings are what a production
 * database already contains: `Permission.PermissionKey` is declared `varchar(20) NOT NULL`
 * (`Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider:L688`).
 */
export type PermissionKey = 'VIEW' | 'EDIT' | 'READ' | 'WRITE';

/**
 * The four permission keys as a runtime value, in the order the schema seeds them. {@link PermissionKey}
 * is erased at compile time, so a union alone cannot answer a question about a value that arrived over
 * the wire.
 */
export const PERMISSION_KEYS = Object.freeze(['VIEW', 'EDIT', 'READ', 'WRITE'] as const);

/**
 * Whether a value is one of the four recognised permission keys. THE ONE SUPPORTED ROUTE FROM AN OPEN
 * PRODUCER TO THE CLOSED VOCABULARY. Every server-supplied key — {@link Permission.permissionKey}, and
 * each key carried by a caller's effective-permission list — is typed `string` because the column stores
 * whatever an installation seeded. The catalogue listing publishes {@link Permission} records rather than
 * bare keys, so a key outside this vocabulary reaches the client intact and is recognised as unrecognised
 * rather than silently dropped from the catalogue.
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

/** The result {@link toPermissionKeys} returns when there is nothing to keep. */
const EMPTY_PERMISSION_KEYS: readonly PermissionKey[] = Object.freeze([]);

/**
 * One entry in the permission catalogue: a definition of an access right, scoped to a subsystem. This is
 * a DEFINITION, not a grant.
 */
export interface Permission {
  /**
   * Identifier of the permission definition, from `Permission.PermissionID`. The column is `IDENTITY(1,
   * 1)`, so — unusually for this schema — no value here collides with a sentinel.
   */
  readonly permissionId: number;

  /**
   * The subsystem this definition is scoped to, from `Permission.PermissionCode`. DELIBERATELY A PLAIN
   * `string` AND NOT A UNION OF THE OBSERVED VALUES. The three values this codebase seeds are
   * `SYSTEM_TAB`, `SYSTEM_MODULE_DEFINITION` and `SYSTEM_FOLDER`, and that list is illustrative
   * documentation only — it is neither exhaustive nor enforced.
   */
  readonly permissionCode: string;

  /**
   * The module definition this permission was declared under, from `Permission.ModuleDefID`. Zero for a
   * definition that belongs to no module definition, which is how the page-scoped and folder-scoped rows
   * are stored — so `0` is meaningful here and must not be read as "missing".
   */
  readonly moduleDefId: number;

  /**
   * The access right itself. See {@link PermissionKey} for why this is an upper-case string rather than a
   * number, and why the four values must never be re-spelled.
   */
  readonly permissionKey: string;

  /**
   * Human-readable name of the definition, from `Permission.PermissionName`, suitable for display in a
   * permissions grid header.
   */
  readonly permissionName: string;
}

/**
 * Decodes one permission catalogue entry. `permissionKey` is decoded as a plain string rather than
 * against {@link PermissionKey}, and the choice is deliberate.
 */
export const decodePermission: Decoder<Permission> = objectOf<Permission>({
  permissionId: decodeInteger,
  permissionCode: decodeString,
  moduleDefId: decodeInteger,
  permissionKey: decodeString,
  permissionName: decodeString,
});
