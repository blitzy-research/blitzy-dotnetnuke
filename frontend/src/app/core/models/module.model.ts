/**
 * Wire contracts for the module administration surface: modules, their placements on pages, the
 * definitions they are instantiated from, their stored settings, and the transfer of their content.
 */

import {
  arrayOf,
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeNumericCode,
  decodeString,
  nullable,
  objectOf,
  recordOf,
  type Decoder,
} from '../utils/decode.util';

import type { PagedResult } from './paged-result.model';

/**
 * How a placement of a module is presented on its page. Mirrors the API enumeration
 * `Domain/Enums/ModuleVisibility.cs` and, through it, the values stored in `dbo.TabModules.Visibility`,
 * declared `int NOT NULL` by 03.00.01.SqlDataProvider line 31.
 */
// D10 - THE WIRE FORM IS THE INTEGER, VERIFIED AT THE REGISTRATION SITE RATHER THAN ASSUMED. The API's
// serialiser registers exactly two enumeration converters, for the billing frequency and the permission
// key, and deliberately does NOT register a blanket string-enumeration converter, because that would claim
// the billing frequency too and put a member name on the wire where a legacy char(1) code belongs.
export enum ModuleVisibility {
  /** The placement renders with its container chrome expanded. */
  Maximized = 0,
  /** The placement renders with its container chrome collapsed. */
  Minimized = 1,
  /** The placement renders without its container chrome. */
  None = 2,
}

/**
 * The three visibility codes this console publishes wording for, in the order the legacy radio group
 * listed them. Exported so the decoder, the presentation layer and the write forms all read one set.
 */
export const PUBLISHED_MODULE_VISIBILITY: readonly ModuleVisibility[] = Object.freeze([
  ModuleVisibility.Maximized,
  ModuleVisibility.Minimized,
  ModuleVisibility.None,
]);

/**
 * Whether a stored visibility code is one this console can name.
 *
 * A type predicate rather than a boolean, so a successful test narrows the widened `number` the wire
 * carries back to {@link ModuleVisibility} and a label table can then be indexed exhaustively.
 *
 * @param code The code exactly as the server sent it.
 * @returns True when the code is one of the three published codes, narrowing `code` on success.
 */
export function isPublishedModuleVisibility(code: number): code is ModuleVisibility {
  return (PUBLISHED_MODULE_VISIBILITY as readonly number[]).includes(code);
}

/** The validation error key an editing form reports for a stored code it cannot offer. */
export const UNPUBLISHED_VISIBILITY_ERROR = 'unpublishedVisibility';

/**
 * The wording an editing form shows when the stored visibility code is not one it can offer.
 *
 * ⚠ WHY THIS IS AN INLINE FIELD MESSAGE AND NOT A SILENT COERCION. The two editing screens bind the stored
 * code straight into a three-member radio group, so a code outside the published three simply matched no
 * radio: nothing was selected, nothing said why, and a save of any OTHER field sent the unrecognised code
 * back - which the API's `IsInEnum` rule then refused with a per-field `400` naming a field the operator had
 * never touched. Coercing to a published code instead would silently rewrite stored data, and zero is
 * `Maximized`, the most visible state of the three. Reporting it on the field is the only option that
 * neither hides the state nor rewrites it.
 */
export const UNPUBLISHED_VISIBILITY_MESSAGE =
  'The stored visibility of this module is a value this screen cannot offer. Choose one of the options ' +
  'above before saving; saving will replace the stored value.';

/** The visibility codes keyed by their lower-case names. */
export const MODULE_VISIBILITY = {
  /** Renders expanded. */
  maximized: ModuleVisibility.Maximized,
  /** Renders collapsed. */
  minimized: ModuleVisibility.Minimized,
  /** Renders without chrome. */
  none: ModuleVisibility.None,
} as const;

/** One row of the module listing, as returned by `GET /api/v1/modules`. */
export interface ModuleListItem {
  /**
   * The module's own identity, from `dbo.Modules.ModuleID`. D36 - the column is `IDENTITY(0, 1)`
   * (01.00.00.SqlDataProvider line 221), so **0 is a legitimate module** and simultaneously the value the
   * legacy code would have read as "absent".
   */
  readonly moduleId: number;

  /**
   * The identity of THIS placement of the module on a page, from `dbo.TabModules.TabModuleID`. The stable
   * key for a row of this listing, and the key under which placement-scoped settings are recorded.
   */
  readonly tabModuleId: number;

  /**
   * The page this placement sits on, from `dbo.TabModules.TabID`. the legacy interface called this a PAGE
   * rather than a tab - its picker was captioned "Move To Page:" - while the schema and this contract
   * both retain the legacy column word.
   */
  readonly tabId: number;

  /**
   * The definition this module was instantiated from, from `dbo.Modules.ModuleDefID`. The referenced
   * `dbo.ModuleDefinitions.ModuleDefID` is `IDENTITY(1, 1)` (01.00.00.SqlDataProvider line 66), so -
   * unlike the module and page keys - no legal value here coincides with a legacy sentinel.
   */
  readonly moduleDefId: number;

  /** The administrator-supplied title of this module instance, or `null` when none was given. */
  readonly moduleTitle: string | null;

  readonly friendlyName: string | null;

  readonly desktopModuleId: number | null;

  /**
   * The installed package's name, projected read-only from `dbo.DesktopModules.ModuleName`. `null` when
   * the definition or its package could not be resolved, even though the column is `NOT NULL` in its own
   * table - the absence describes the join, not the row.
   */
  readonly moduleName: string | null;

  /** The installed package's description, projected read-only from `dbo.DesktopModules.Description`. */
  readonly description: string | null;

  /** The installed package's version, projected read-only from `dbo.DesktopModules.Version`. */
  readonly version: string | null;

  /**
   * Whether this module was created from an ADMINISTRATION package, from `dbo.DesktopModules.IsAdmin`.
   *
   * ⚠ ONLY AN EXPLICIT `true` MEANS ADMINISTRATIVE, AND `null` IS NOT `false`. The column is `bit NOT NULL`,
   * so a resolved package always answers one way or the other; `null` means the definition or its package
   * could not be resolved and the server is claiming nothing. Reading `null` as "ordinary" is how a screen
   * comes to offer the generic settings surface for a module that surface refuses outright.
   *
   * WHAT IT IS FOR: the generic module settings screen answers `module.settings_protected` for an
   * administrative module, because such a module's settings belong to the screen that administers them - the
   * User Accounts package's settings ARE the portal's membership settings. Administrative definitions are
   * also withheld from the portal-placeable definition catalogue, so their nature cannot be inferred from a
   * second read.
   */
  readonly isAdmin: boolean | null;

  /** The placement's position within its pane on the page, from `dbo.TabModules.ModuleOrder`. */
  readonly moduleOrder: number;

  /**
   * Whether the module is displayed in the same location on every page of the portal, from
   * `dbo.Modules.AllTabs`. The legacy label read "Display Module On All Pages?".
   */
  readonly allTabs: boolean;

  /**
   * How this placement is presented on its page, from `dbo.TabModules.Visibility`.
   *
   * ⚠ DECLARED `number` AND NOT THE ENUMERATION, AND THE WIDTH IS THE POINT. The column is `int` with no
   * check constraint (03.00.01.SqlDataProvider line 31), so a real installation whose modules registered
   * their own presentation codes holds values this console publishes no wording for. Narrowing the member
   * to the three published codes made the decoder REFUSE such a row and - because a row is decoded inside
   * its page - refusing one row blanked the entire listing while the response was a perfectly good HTTP
   * 200. Narrow with {@link isPublishedModuleVisibility} before naming a code, and mark one you cannot
   * name.
   */
  readonly visibility: number;

  /**
   * Whether the module is in the soft-deleted state the legacy recycle bin represented, from
   * `dbo.Modules.IsDeleted`.
   */
  readonly isDeleted: boolean;

  /**
   * Whether the module's CONTAINER chrome is displayed around this placement. the member name understates
   * what the value controls.
   */
  readonly displayTitle: boolean;

  /**
   * The date from which the module begins to be displayed, as an ISO 8601 string, or `null` when no start
   * date is set.
   */
  readonly startDate: string | null;

  /**
   * The date after which the module stops being displayed, as an ISO 8601 string, or `null` when no end
   * date is set. Subject to the identical sentinel treatment as {@link ModuleListItem.startDate}.
   */
  readonly endDate: string | null;
}

/**
 * A page of the module listing, as `GET /api/v1/modules` returns it. The listing is paged and filtered
 * server-side; the query members - the page index and size, the sort, the optional page filter and
 * whether soft-deleted rows are included - are built by the shared HTTP parameter helpers rather than
 * declared here, so that one place owns the spelling of every query key.
 */
export type ModuleListPage = PagedResult<ModuleListItem>;

/**
 * One placement of one module in full, as returned by `GET /api/v1/modules/{moduleId}` and echoed by the
 * create and update endpoints. Mirrors `Dtos/Module/ModuleDetailDto.cs`, which carries exactly these
 * twenty-three members.
 */
export interface ModuleDetail {
  /**
   * The module's own identity, from `dbo.Modules.ModuleID`, whose identity seeds at 0. D36 - 0 is a
   * legitimate module.
   */
  readonly moduleId: number;

  /** The identity of this placement, from `dbo.TabModules.TabModuleID` (03.00.01 line 21). */
  readonly tabModuleId: number;

  /**
   * The page this placement sits on, from `dbo.TabModules.TabID`, whose identity seeds at 0. The legacy
   * screen captioned its picker "Move To Page:", so this value is what a page-move writes.
   */
  readonly tabId: number;

  /**
   * The portal that owns the module, from `dbo.Modules.PortalID`, or `null` when the module is not
   * portal-scoped - that is, a host-level module. THE ONLY GENUINELY NULLABLE IDENTIFIER ON THIS
   * CONTRACT, and its non-null values include -1: `dbo.Portals.PortalID` is `IDENTITY(-1, 1)`
   * (01.00.00.SqlDataProvider line 77), which is also the legacy NullInteger.
   */
  readonly portalId: number | null;

  /** The definition this module was instantiated from, from `dbo.Modules.ModuleDefID` (seeds at 1). */
  readonly moduleDefId: number;

  /**
   * The installed package behind the definition, projected from `dbo.ModuleDefinitions.DesktopModuleID`.
   * `null` means the definition could not be resolved. **It is never 0**:
   * `dbo.DesktopModules.DesktopModuleID` is a plain `IDENTITY`, so it seeds at 1, and the terminal
   * `dbo.ModuleDefinitions.DesktopModuleID` is `NOT NULL` with a foreign key onto it.
   */
  readonly desktopModuleId: number | null;

  /** The administrator-supplied heading of this module instance, or `null` when none was given. */
  readonly moduleTitle: string | null;

  /**
   * Whether the module appears in the same location on every page of the portal, from
   * `dbo.Modules.AllTabs`. When set, the server maintains one placement row per page, which is why a
   * change to this flag is a far-reaching write rather than a single-column edit.
   */
  readonly allTabs: boolean;

  /**
   * Free text or markup rendered above the module's content, from `dbo.Modules.Header`, or `null` for
   * none. The column is an unbounded national text type and the legacy control was a six-row multi-line
   * input with no length validator, so no maximum length applies.
   */
  readonly header: string | null;

  /**
   * Free text or markup rendered below the module's content, from `dbo.Modules.Footer`, or `null` for
   * none.
   */
  readonly footer: string | null;

  /**
   * The date from which the module begins to be displayed, as an ISO 8601 string, or `null` for no start
   * restriction. `null` here is the translation of the legacy `Null.NullDate` sentinel, which was
   * `Date.MinValue` rather than a database null.
   */
  readonly startDate: string | null;

  /**
   * The date after which the module stops being displayed, as an ISO 8601 string, or `null` for no end
   * restriction. Subject to the identical sentinel treatment as {@link ModuleDetail.startDate}.
   */
  readonly endDate: string | null;

  /**
   * Whether the module takes its View permission from the page it sits on instead of carrying its own,
   * from `dbo.Modules.InheritViewPermissions`.
   */
  readonly inheritViewPermissions: boolean;

  /**
   * Whether the module is in the soft-deleted state the legacy recycle bin represented, from
   * `dbo.Modules.IsDeleted`.
   */
  readonly isDeleted: boolean;

  /**
   * The placement's position within its pane on the page, from `dbo.TabModules.ModuleOrder` (03.00.01
   * line 25).
   */
  readonly moduleOrder: number;

  /** How long this placement's rendered output may be cached, in seconds. */
  readonly cacheTime: number;

  /** The icon displayed with the module's title for this placement, or `null` for none. */
  readonly iconFile: string | null;

  /**
   * How this placement is aligned within its pane, from `TabModules.Alignment`, or `null` when none is
   * stored.
   *
   * ⚠ THIS MEMBER AND THE TWO BELOW WERE ABSENT, AND THE THREE COLUMNS THEY CARRY WERE UNREACHABLE. The
   * legacy settings screen edited all three (`modulesettings.ascx:L120-L139`, saved at
   * `ModuleSettings.ascx.vb:L345-L347`); the first port modelled none of them, so an installation's stored
   * container appearance was invisible and could not be changed — while the screen's own settings section
   * asserted the module had no stored settings of its own.
   *
   * The values `left`, `center`, `right` and the empty string are the legacy radio list's own and are
   * LOAD-BEARING DATA: they are what the column contains, so they are never re-spelled.
   */
  readonly alignment: string | null;

  /**
   * The container background colour, from `TabModules.Color`, or `null` when none is stored. An opaque
   * legacy token, bounded at twenty characters and carried through unparsed.
   */
  readonly color: string | null;

  /**
   * The container border width, from `TabModules.Border`, or `null` when none is stored. One character
   * wide, and a digit: the legacy box carried `MaxLength="1"` and an integer validator reading "must be a
   * number between 0 and 9".
   */
  readonly border: string | null;

  /**
   * How this placement is presented on its page, from `dbo.TabModules.Visibility`. Declared `number` for
   * the reason recorded on {@link ModuleListItem.visibility}: the column admits codes this console
   * publishes no wording for, and one such row must not cost the reader the record.
   */
  readonly visibility: number;

  /**
   * Whether the module's CONTAINER chrome is displayed around this placement. as on the listing, the
   * authoritative legacy wording was "Display Container?" despite the column and member being named for
   * the title.
   */
  readonly displayTitle: boolean;

  /**
   * The definition's display name, projected read-only from `dbo.ModuleDefinitions.FriendlyName`, or
   * `null` when it could not be resolved. The first field the legacy screen rendered, and it rendered it
   * read-only.
   */
  readonly friendlyName: string | null;

  /**
   * The installed package's unique programmatic name, projected read-only from
   * `dbo.DesktopModules.ModuleName`, or `null` when it could not be resolved. LOAD-BEARING FOR TRANSFER
   * RATHER THAN MERELY DESCRIPTIVE: the legacy export composed its document around this name, so an
   * import is only meaningful against a package that answers to it.
   */
  readonly moduleName: string | null;

  /**
   * The human-readable description of the installed package, projected read-only from
   * `dbo.DesktopModules.Description`, or `null` when none is recorded. Read-only catalogue metadata.
   */
  readonly description: string | null;

  /**
   * The installed package's version string, projected read-only from `dbo.DesktopModules.Version`, or
   * `null` when none is recorded. Read-only catalogue metadata, and load-bearing for transfer in the same
   * way as {@link ModuleDetail.moduleName} when handing a document between installations.
   */
  readonly version: string | null;

  /**
   * Whether this module was created from an ADMINISTRATION package, from `dbo.DesktopModules.IsAdmin`.
   *
   * ⚠ ONLY AN EXPLICIT `true` MEANS ADMINISTRATIVE, AND `null` IS NOT `false`. The column is `bit NOT NULL`,
   * so a resolved package always answers one way or the other; `null` means the definition or its package
   * could not be resolved and the server is claiming nothing. Reading `null` as "ordinary" is how a screen
   * comes to offer the generic settings surface for a module that surface refuses outright.
   *
   * WHAT IT IS FOR: the generic module settings screen answers `module.settings_protected` for an
   * administrative module, because such a module's settings belong to the screen that administers them - the
   * User Accounts package's settings ARE the portal's membership settings. Administrative definitions are
   * also withheld from the portal-placeable definition catalogue, so their nature cannot be inferred from a
   * second read.
   */
  readonly isAdmin: boolean | null;
}

/** The body of `POST /api/v1/modules`, which places a new module on a page. */
export interface CreateModuleRequest {
  /** The definition to instantiate. Required. */
  readonly moduleDefId: number;

  /** The page on which to place the module. Required. */
  readonly tabId: number;

  /** The heading for the module, or `null` to leave it unset. Not required. */
  readonly moduleTitle: string | null;

  /**
   * Whether the module appears in the same position on every page of the portal, which produces one
   * placement row per page.
   */
  readonly allTabs: boolean;

  /** Text or markup rendered above the module's content, or `null` for none. */
  readonly header: string | null;

  /** Text or markup rendered below the module's content, or `null` for none. */
  readonly footer: string | null;

  readonly startDate: string | null;

  /**
   * The date until which the module is displayed, as an ISO 8601 string, or `null` for no end
   * restriction. The same rule, the same sentinel translation and the same caution as {@link
   * CreateModuleRequest.startDate}.
   */
  readonly endDate: string | null;

  /** Whether the module takes its View permission from its page instead of carrying its own. */
  readonly inheritViewPermissions: boolean;

  /**
   * The module's position within its pane on the page. -1 APPENDS, AND 0 IS A POSITION. The server's
   * default for this member is -1, which appends the module at the bottom of its pane; sending 0
   * explicitly means position zero.
   */
  readonly moduleOrder: number;

  /** How long the module's output may be cached, in seconds. */
  readonly cacheTime: number;

  /** The icon displayed with the module's title, or `null` for none. Not required. */
  readonly iconFile: string | null;

  /**
   * How the module is presented on the page. the server's default relies on a coincidence that must not
   * be disturbed - the legacy default was the maximised state, and that member is numbered 0, which is
   * also the default of the underlying value type.
   */
  readonly visibility: ModuleVisibility;

  /** Whether the module's container is displayed. The server defaults this to `true`. */
  readonly displayTitle: boolean;
}

export interface UpdateModuleRequest {
  // ---------------------------------------------------------------------------------------------------
  // Mirrored from Dtos/Module/UpdateModuleRequest.cs
  // ---------------------------------------------------------------------------------------------------

  /** The page this placement is on. Required, matching the non-nullable `int` on the contract it mirrors. */
  readonly tabId: number;

  /**
   * The page this placement should be moved ONTO, or `null` when nothing is being moved. TWO DIFFERENT
   * QUESTIONS, TWO DIFFERENT MEMBERS. `tabId` above answers "which placement am I editing", because a
   * module placed on several pages has one row per page.
   */
  readonly moveToTabId: number | null;

  /** The heading for the module, or `null` to leave it unset. */
  readonly moduleTitle: string | null;

  /**
   * Whether the module appears in the same position on every page of the portal, which produces one
   * placement row per page.
   */
  readonly allTabs: boolean;

  /** Text or markup rendered above the module's content, or `null` for none. */
  readonly header: string | null;

  /** Text or markup rendered below the module's content, or `null` for none. */
  readonly footer: string | null;

  /**
   * The date from which the module is displayed, as an ISO 8601 string, or `null` for no start
   * restriction.
   */
  readonly startDate: string | null;

  /**
   * The date until which the module is displayed, as an ISO 8601 string, or `null` for no end
   * restriction. The same rule and the same sentinel translation as {@link
   * UpdateModuleRequest.startDate}.
   */
  readonly endDate: string | null;

  /** Whether the module takes its View permission from its page instead of carrying its own. */
  readonly inheritViewPermissions: boolean;

  /** Whether the module is in the recycle bin. The server defaults this to `false`. */
  readonly isDeleted?: boolean;

  /** The module's position within its pane on the page. */
  readonly moduleOrder: number;

  /** How long the module's output may be cached, in seconds. */
  readonly cacheTime: number;

  /** The icon displayed with the module's title, or `null` for none. */
  readonly iconFile: string | null;

  /**
   * How the placement is aligned within its pane — `left`, `center`, `right`, or the empty string for
   * Not Specified — or `null` to leave nothing stored.
   */
  readonly alignment: string | null;

  /** The container background colour, or `null` for none. Bounded at twenty characters. */
  readonly color: string | null;

  /** The container border width, a single digit, or `null` for none. */
  readonly border: string | null;

  /** How the module is presented on the page. */
  readonly visibility: ModuleVisibility;

  /** Whether the module's container is displayed. */
  readonly displayTitle: boolean;

  /**
   * An instruction rather than module state: name this module and its page as the portal's default
   * settings for newly added modules. The server defaults this to `false`.
   */
  readonly setAsDefaultSettings?: boolean;

  /**
   * An instruction rather than module state: copy this placement's appearance to every module on every
   * non-administrative page of the portal.
   */
  readonly applyToAllModules?: boolean;
}

/**
 * The stored settings of a module and of one of its placements. Mirrors
 * `Dtos/Module/ModuleSettingsDto.cs`, which carries exactly these four members.
 */
export interface ModuleSettingsBag {
  /**
   * The module whose module-scoped settings are carried in {@link ModuleSettingsBag.moduleSettings}. D36
   * - the column is an identity seeded at zero, so 0 is a legitimate module and must never be read as an
   * absent or unsaved one.
   */
  readonly moduleId: number;

  /**
   * The placement whose placement-scoped settings are carried in {@link
   * ModuleSettingsBag.tabModuleSettings}, or `null` when no placement was addressed.
   */
  readonly tabModuleId: number | null;

  /**
   * The settings recorded against the module itself, and therefore identical on every page the module
   * appears on, from `dbo.ModuleSettings`. Keys are bounded at 50 characters and values at 2000 by the
   * terminal column widths.
   */
  readonly moduleSettings: Readonly<Record<string, string>>;

  /**
   * The settings recorded against one placement alone, and therefore specific to a single occurrence of
   * the module on a single page, from `dbo.TabModuleSettings`.
   */
  readonly tabModuleSettings: Readonly<Record<string, string>>;
}

/**
 * One column of a module's grant grid: a permission the module's definition declares. Mirrors
 * `Dtos/Module/ModulePermissionDefinitionDto.cs`.
 */
export interface ModulePermissionColumn {
  /** Identifier of the definition, from `Permission.PermissionID`, which is `IDENTITY(1, 1)`. */
  readonly permissionId: number;

  /**
   * The key itself — `VIEW`, `EDIT`, `READ` or `WRITE`. DELIBERATELY A PLAIN `string`, exactly as {@link
   * Permission.permissionKey} is: the column stores whatever an installation seeded, so a value outside
   * the four-member vocabulary is data rather than a contract violation, and it must not discard the row.
   */
  readonly permissionKey: string;

  /** Display name of the definition, from `Permission.PermissionName`, used as the column header. */
  readonly permissionName: string;
}

/**
 * One cell of a module's grant grid: whether the grant is in force, and whether it may be changed.
 *
 * BOTH FACTS TRAVEL BECAUSE NEITHER IMPLIES THE OTHER. An administrator cell is granted and locked; a
 * view cell under inheritance is refused and locked; an ordinary cell is either and unlocked. The rules
 * that produce them live in the server — they depend on `Portals.AdministratorRoleId` and
 * `Modules.InheritViewPermissions` — so re-deriving them here would let two clients disagree.
 */
export interface ModulePermissionCell {
  /** The column this cell belongs to. */
  readonly permissionId: number;

  /** The key of the column this cell belongs to, repeated so a row can be read without a lookup. */
  readonly permissionKey: string;

  /** Whether the grant is currently in force. */
  readonly allowAccess: boolean;

  /** Whether the cell may be changed. */
  readonly editable: boolean;
}

/**
 * One role row of a module's grant grid. Mirrors `Dtos/Module/ModulePermissionRoleDto.cs`.
 */
export interface ModulePermissionRoleRow {
  /**
   * Identifier of the role, from `Roles.RoleID`, or one of the built-in negative pseudo-role identifiers
   * (`-1` All Users, `-3` Unauthenticated Users). `0` IS A REAL ROLE — the column is `IDENTITY(0, 1)` and
   * on a default installation zero is the Administrators role — so a falsiness test on this member is
   * never a valid presence check.
   */
  readonly roleId: number;

  /** Display name of the role, from `Roles.RoleName`. */
  readonly roleName: string;

  /** Whether this row is the portal's administrator role, whose grants are implicit and not editable. */
  readonly isAdministrator: boolean;

  /** Whether this row is one of the two built-in pseudo-roles rather than a row in `dbo.Roles`. */
  readonly isPseudoRole: boolean;

  /** The state of every cell in this row, one entry per declared column. */
  readonly cells: readonly ModulePermissionCell[];
}

/**
 * One account row of a module's grant grid — an account that holds a grant of its own rather than one
 * inherited through a role. Mirrors `Dtos/Module/ModulePermissionUserDto.cs`.
 */
export interface ModulePermissionUserRow {
  /** Identifier of the account, from `Users.UserID`. */
  readonly userId: number;

  /** The account's display name, or its user name when the display name is blank. */
  readonly displayName: string;

  /** The state of every cell in this row, one entry per declared column. */
  readonly cells: readonly ModulePermissionCell[];
}

/**
 * The complete grant grid of one module. Mirrors `Dtos/Module/ModulePermissionsDto.cs`.
 *
 * MIGRATION: this is the client half of the `<dnn:modulepermissionsgrid id="dgPermissions">` server
 * control declared at `Website/admin/Modules/modulesettings.ascx:L42`. The first port of the module
 * settings screen carried no equivalent at all, so a portal administrator could see the four declared
 * permission keys but no roles and no cells, and could not grant or withdraw module access.
 */
export interface ModulePermissionGrid {
  /** The module whose grants these are. `0` is a legitimate module. */
  readonly moduleId: number;

  /** Whether the module currently takes its view rights from the page it is placed on. */
  readonly inheritViewPermissions: boolean;

  /**
   * The key of the column the inheritance switch collapses — `VIEW` — or `null` when this module's
   * definition declares no such column.
   */
  readonly inheritedPermissionKey: string | null;

  /** The columns, in render order. */
  readonly definitions: readonly ModulePermissionColumn[];

  /** The role rows, already ordered case-insensitively by name by the server. */
  readonly roles: readonly ModulePermissionRoleRow[];

  /** The account rows, present only for accounts a grant already names. */
  readonly users: readonly ModulePermissionUserRow[];
}

/** One grant in a replacement set: who it is for, which permission, and whether it allows. */
export interface ModulePermissionGrantInput {
  /** The definition being granted. */
  readonly permissionId: number;

  /** The role the grant is for, or `null` when the grant is for one named account. */
  readonly roleId: number | null;

  /** The account the grant is for, or `null` when the grant is for a role. */
  readonly userId: number | null;

  /** `true` to allow, `false` to deny. */
  readonly allowAccess: boolean;
}

/**
 * The body of `PUT /modules/{moduleId}/permissions`. Mirrors
 * `Dtos/Module/ReplaceModulePermissionsRequest.cs`.
 *
 * ⚠ A REPLACE, NOT A MERGE. A grant absent from {@link ModulePermissionReplacement.grants} is withdrawn,
 * matching `ModuleSettings.ascx.vb:L378-L379`, which assigned the grid's whole collection onto the module
 * and saved the inheritance switch in the same operation.
 */
export interface ModulePermissionReplacement {
  /** Whether the module should take its view rights from the page it is placed on. */
  readonly inheritViewPermissions: boolean;

  /** Every grant the module should hold. An empty array withdraws all of them. */
  readonly grants: readonly ModulePermissionGrantInput[];
}

/**
 * One module definition, as returned by the `module-definitions` endpoints. Mirrors
 * `Dtos/Module/ModuleDefinitionDto.cs`, which carries exactly these ten members: four describing the
 * definition and six projected read-only from the installed package that owns it.
 */
export interface ModuleDefinition {
  /**
   * The identity of this definition, from `dbo.ModuleDefinitions.ModuleDefID`. The column is `int
   * IDENTITY (1, 1) NOT NULL` and the table's primary key (01.00.00.SqlDataProvider line 66).
   */
  readonly moduleDefId: number;

  /**
   * The display name of this definition - the value the legacy settings screen showed for "Module:". From
   * `dbo.ModuleDefinitions.FriendlyName`, measured `nvarchar(128) NOT NULL`, so the effective maximum
   * length is 128 characters and the member is non-nullable.
   */
  readonly friendlyName: string;

  /**
   * The installed package that owns this definition, from `dbo.ModuleDefinitions.DesktopModuleID`. added
   * by 02.00.00.SqlDataProvider line 5173 as `int NOT NULL` with a stored default of 0 and made a foreign
   * key at line 5258, so 0 here may mean either a real package or a row that predates the association.
   */
  readonly desktopModuleId: number;

  /**
   * The definition's default cache timeout in seconds - the legacy "Cache Time (secs):" field, whose help
   * text read "Enter the time this object is kept in the Cache". From
   * `dbo.ModuleDefinitions.DefaultCacheTime`.
   */
  readonly defaultCacheTime: number;

  /**
   * The unique programmatic name of the owning package, from `dbo.DesktopModules.ModuleName`. Measured
   * `nvarchar(128)`, promoted to `NOT NULL` by 03.01.00.SqlDataProvider line 26 and made unique at line
   * 30, so the member is non-nullable and its effective maximum length is 128 characters.
   */
  readonly moduleName: string;

  /**
   * A human-readable description of the owning package, from `dbo.DesktopModules.Description`, or `null`
   * when none is recorded. Measured `nvarchar(2000) NULL`.
   */
  readonly description: string | null;

  /**
   * The installed version of the owning package, from `dbo.DesktopModules.Version`, or `null` when none
   * is recorded. Measured `nvarchar(8) NULL` - a deliberately narrow column, so the effective maximum
   * length is only eight characters.
   */
  readonly version: string | null;

  /**
   * Whether the owning package is a premium module, from `dbo.DesktopModules.IsPremium`. Measured `bit
   * NOT NULL`, hence non-nullable.
   */
  readonly isPremium: boolean;

  /**
   * Whether the owning package is an administration module, from `dbo.DesktopModules.IsAdmin`. Measured
   * `bit NOT NULL`, hence non-nullable.
   */
  readonly isAdmin: boolean;

  /** Whether the owning package supports content export and import. */
  readonly isPortable: boolean;
}

export interface ModuleExportRequest {
  readonly fileName: string | null;

  readonly folder: string | null;
}

/** The body of `POST /api/v1/modules/import`. */
/** The largest document, in characters, that an import may carry. */
export const MODULE_IMPORT_MAX_CONTENT_CHARACTERS = 1_048_576;

/** The largest file, in bytes, this client may offer for import. */
export const MODULE_IMPORT_MAX_FILE_BYTES = MODULE_IMPORT_MAX_CONTENT_CHARACTERS;

export interface ModuleImportRequest {
  /** The module whose content is being replaced. Mandatory - the only member of this contract that is. */
  readonly moduleId: number | null;

  /** The exported document to load, as text. Effectively mandatory: a blank value is refused. */
  readonly content: string | null;

  /** The portal-relative folder the document came from, or `null` when the caller has none. */
  readonly folder: string | null;

  /**
   * The name of the document the content came from, or `null` when the caller has none. Optional parity
   * metadata that no decision depends on, and never interpreted as a path.
   */
  readonly fileName: string | null;
}

/**
 * Decodes one listed module row.
 *
 * ⚠ `visibility` IS TOLERATED WHEN THE CODE IS UNRECOGNISED, AND NEITHER REFUSED NOR COERCED. The
 * distinction between those three is the whole of this decision. REFUSING was what shipped, and it cost
 * the reader the page: `objectOf` propagates out of `pageOf`, so one row holding `Visibility = 9` — a
 * value the `int` column admits and a real installation's own modules can register — turned a successful
 * HTTP 200 carrying every module into an empty listing that announced "No records found." COERCING to
 * zero would be worse still, because zero is `Maximized`, the most visible of the three states, so a code
 * this client never published would be presented as a confident fact. TOLERATING keeps the integer
 * exactly as stored, and the listing marks a code it cannot name the way the profile-definition screen
 * already marks an unnameable data type.
 */
export const decodeModuleListItem: Decoder<ModuleListItem> = objectOf<ModuleListItem>({
  moduleId: decodeInteger,
  tabModuleId: decodeInteger,
  tabId: decodeInteger,
  moduleDefId: decodeInteger,
  moduleTitle: nullable(decodeString),
  friendlyName: nullable(decodeString),
  desktopModuleId: nullable(decodeInteger),
  moduleName: nullable(decodeString),
  description: nullable(decodeString),
  version: nullable(decodeString),
  isAdmin: nullable(decodeBoolean),
  moduleOrder: decodeInteger,
  allTabs: decodeBoolean,
  visibility: decodeNumericCode(PUBLISHED_MODULE_VISIBILITY),
  isDeleted: decodeBoolean,
  displayTitle: decodeBoolean,
  startDate: nullable(decodeDateString),
  endDate: nullable(decodeDateString),
});

/** Decodes one module in full. */
export const decodeModuleDetail: Decoder<ModuleDetail> = objectOf<ModuleDetail>({
  moduleId: decodeInteger,
  tabModuleId: decodeInteger,
  tabId: decodeInteger,
  portalId: nullable(decodeInteger),
  moduleDefId: decodeInteger,
  desktopModuleId: nullable(decodeInteger),
  moduleTitle: nullable(decodeString),
  allTabs: decodeBoolean,
  header: nullable(decodeString),
  footer: nullable(decodeString),
  startDate: nullable(decodeDateString),
  endDate: nullable(decodeDateString),
  inheritViewPermissions: decodeBoolean,
  isDeleted: decodeBoolean,
  moduleOrder: decodeInteger,
  cacheTime: decodeInteger,
  iconFile: nullable(decodeString),
  alignment: nullable(decodeString),
  color: nullable(decodeString),
  border: nullable(decodeString),
  visibility: decodeNumericCode(PUBLISHED_MODULE_VISIBILITY),
  displayTitle: decodeBoolean,
  friendlyName: nullable(decodeString),
  moduleName: nullable(decodeString),
  description: nullable(decodeString),
  version: nullable(decodeString),
  isAdmin: nullable(decodeBoolean),
});

export const decodeModuleSettingsBag: Decoder<ModuleSettingsBag> = objectOf<ModuleSettingsBag>({
  moduleId: decodeInteger,
  tabModuleId: nullable(decodeInteger),
  moduleSettings: recordOf(decodeString),
  tabModuleSettings: recordOf(decodeString),
});

const decodeModulePermissionColumn: Decoder<ModulePermissionColumn> =
  objectOf<ModulePermissionColumn>({
    permissionId: decodeInteger,
    permissionKey: decodeString,
    permissionName: decodeString,
  });

const decodeModulePermissionCell: Decoder<ModulePermissionCell> = objectOf<ModulePermissionCell>({
  permissionId: decodeInteger,
  permissionKey: decodeString,
  allowAccess: decodeBoolean,
  editable: decodeBoolean,
});

const decodeModulePermissionRoleRow: Decoder<ModulePermissionRoleRow> =
  objectOf<ModulePermissionRoleRow>({
    roleId: decodeInteger,
    roleName: decodeString,
    isAdministrator: decodeBoolean,
    isPseudoRole: decodeBoolean,
    cells: arrayOf(decodeModulePermissionCell),
  });

const decodeModulePermissionUserRow: Decoder<ModulePermissionUserRow> =
  objectOf<ModulePermissionUserRow>({
    userId: decodeInteger,
    displayName: decodeString,
    cells: arrayOf(decodeModulePermissionCell),
  });

export const decodeModulePermissionGrid: Decoder<ModulePermissionGrid> =
  objectOf<ModulePermissionGrid>({
    moduleId: decodeInteger,
    inheritViewPermissions: decodeBoolean,
    inheritedPermissionKey: nullable(decodeString),
    definitions: arrayOf(decodeModulePermissionColumn),
    roles: arrayOf(decodeModulePermissionRoleRow),
    users: arrayOf(decodeModulePermissionUserRow),
  });

export const decodeModuleDefinition: Decoder<ModuleDefinition> = objectOf<ModuleDefinition>({
  moduleDefId: decodeInteger,
  friendlyName: decodeString,
  desktopModuleId: decodeInteger,
  defaultCacheTime: decodeInteger,
  moduleName: decodeString,
  description: nullable(decodeString),
  version: nullable(decodeString),
  isPremium: decodeBoolean,
  isAdmin: decodeBoolean,
  isPortable: decodeBoolean,
});
