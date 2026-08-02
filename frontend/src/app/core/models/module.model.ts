/**
 * Wire contracts for the module administration surface.
 *
 * These interfaces mirror the backend DTOs exactly, field for field, so that a change on either side of
 * the boundary shows up as a type error rather than as a silently dropped value:
 *
 *   - `ModuleSettings`      mirrors `DnnMigration.Application.Dtos.Module.ModuleDetailDto`
 *   - `UpdateModuleRequest` mirrors `DnnMigration.Application.Dtos.Module.UpdateModuleRequest`
 *
 * This file is type-only. It declares no service, no injectable and no runtime dependency beyond the two
 * frozen lookup maps below, which exist because the legacy screen's choice lists were fixed sets declared
 * inline in the markup rather than fetched from anywhere.
 *
 * MIGRATION: the five appearance fields (`alignment`, `color`, `border`, `displayPrint`,
 * `displaySyndicate`) were absent from every module contract until this screen needed them. They are
 * editable on the legacy screen — `Website/admin/Modules/modulesettings.ascx` renders cboAlign, txtColor,
 * txtBorder, chkDisplayPrint and chkDisplaySyndicate, and
 * `Website/admin/Modules/ModuleSettings.ascx.vb:L345-L347,L381-L382` writes every one of them — so the
 * backend contracts were extended rather than the screen being reduced.
 */

/**
 * How a placement renders, mirroring `DnnMigration.Domain.Enums.ModuleVisibility`.
 *
 * The numeric codes are the values stored in the `int NOT NULL` column `dbo.TabModules.Visibility`, so they
 * are load-bearing data and are declared explicitly rather than left to declaration order.
 */
export const MODULE_VISIBILITY = {
  /** Renders expanded. The legacy default when no placement existed yet. */
  maximized: 0,
  /** Renders collapsed. */
  minimized: 1,
  /** Renders without its chrome. */
  none: 2,
} as const;

/** The stored visibility codes, as a union of the three legal values. */
export type ModuleVisibility = (typeof MODULE_VISIBILITY)[keyof typeof MODULE_VISIBILITY];

/**
 * The four alignment values the legacy screen offered.
 *
 * MIGRATION: the empty string is a real, selectable option, not an absence. `modulesettings.ascx` declares
 * `<asp:listitem resourcekey="Not_Specified" value="">Not Specified</asp:listitem>` and
 * `ModuleSettings.ascx.vb:L345` stores `cboAlign.SelectedItem.Value` verbatim, so choosing "Not Specified"
 * wrote an empty string. That is preserved: an empty string means the operator explicitly chose not to
 * specify, and `null` means the column was never written.
 */
export const MODULE_ALIGNMENT = {
  /** The legacy "Not Specified" choice, stored as an empty string. */
  notSpecified: '',
  left: 'left',
  center: 'center',
  right: 'right',
} as const;

/** The stored alignment values, as a union of the four legal values. */
export type ModuleAlignment = (typeof MODULE_ALIGNMENT)[keyof typeof MODULE_ALIGNMENT];

/**
 * One placement of one module, as returned by `GET /api/v1/portals/{portalId}/modules/{moduleId}`.
 *
 * MIGRATION: a module with `allTabs` set has many placements, so this describes ONE placement. Row identity
 * on the server is the placement, never the module, which is why both identifiers are carried.
 */
export interface ModuleSettings {
  /** The module's identifier. Zero is a legal value: `dbo.Modules.ModuleID` is `IDENTITY(0,1)`. */
  readonly moduleId: number;
  /** This placement's identifier, from `dbo.TabModules.TabModuleID`. */
  readonly tabModuleId: number;
  /** The page this placement sits on. */
  readonly tabId: number;
  /** The owning tenant, or `null` when the module is host-owned. */
  readonly portalId: number | null;
  /** Which definition the module instantiates. Fixed at creation and never editable. */
  readonly moduleDefId: number;
  /** The definition's display name, shown read-only. Empty when the name could not be resolved. */
  readonly friendlyName: string;
  /** The placement's heading, or `null` to take the definition's name. */
  readonly moduleTitle: string | null;
  /** Which pane of the page's layout the placement occupies. */
  readonly paneName: string;
  /** The placement's position within its pane. */
  readonly moduleOrder: number;
  /** Whether the module appears on every page of the tenant. */
  readonly allTabs: boolean;
  /** Whether the module is in the recycle bin. */
  readonly isDeleted: boolean;
  /** Whether view permissions come from the page rather than from the module. */
  readonly inheritViewPermissions: boolean | null;
  /** Markup rendered above the module's content, or `null` for none. */
  readonly header: string | null;
  /** Markup rendered below the module's content, or `null` for none. */
  readonly footer: string | null;
  /** The instant the module becomes visible, as an ISO 8601 string, or `null` for no start restriction. */
  readonly startDate: string | null;
  /** The instant the module stops being visible, as an ISO 8601 string, or `null` for no end restriction. */
  readonly endDate: string | null;
  /** How long the module's output may be cached, in seconds. Zero disables caching. */
  readonly cacheTime: number | null;
  /** The placement's icon, or `null` for none. */
  readonly iconFile: string | null;
  /** The placement's alignment, or `null` when the column was never written. */
  readonly alignment: string | null;
  /** The placement's recorded colour. An opaque persisted value, never a style input. */
  readonly color: string | null;
  /** The placement's recorded border flag: one digit, or `null` for none. */
  readonly border: string | null;
  /** How the placement renders. */
  readonly visibility: ModuleVisibility;
  /** Whether the placement shows its container chrome. */
  readonly displayTitle: boolean;
  /** Whether the placement offers a print affordance. */
  readonly displayPrint: boolean;
  /** Whether the placement offers a syndication affordance. */
  readonly displaySyndicate: boolean;
}

/**
 * The body of `PUT /api/v1/portals/{portalId}/modules/{moduleId}`.
 *
 * MIGRATION: this is a whole-row replacement, exactly as the legacy postback was. An omitted field is not
 * "leave it alone" — the server's projection writes the absent value, which clears a nullable column. That
 * is deliberate and matches the legacy screen, where an empty text box was posted as an empty value; without
 * it an operator could set a colour but never remove one.
 */
export interface UpdateModuleRequest {
  /** The placement's heading, or `null` to take the definition's name. At most 256 characters. */
  moduleTitle: string | null;
  /** Which pane the placement occupies. Required by the server. */
  paneName: string;
  /** The placement's position within its pane. */
  moduleOrder: number;
  /** Whether the module should appear on every page of the tenant. */
  allTabs: boolean;
  /** Whether view permissions should come from the page. */
  inheritViewPermissions: boolean;
  /** The placement's alignment. At most 10 characters. */
  alignment: string | null;
  /** The placement's recorded colour. At most 20 characters. */
  color: string | null;
  /** The placement's border flag. Exactly one digit, or absent. */
  border: string | null;
  /** How the placement renders. Must be a defined code. */
  visibility: ModuleVisibility;
  /** Whether the placement shows its container chrome. */
  displayTitle: boolean;
  /** Whether the placement offers a print affordance. */
  displayPrint: boolean;
  /** Whether the placement offers a syndication affordance. */
  displaySyndicate: boolean;
  /** How long the module's output may be cached, in seconds. Zero disables caching. */
  cacheTime: number | null;
  /** The placement's icon, or `null` for none. At most 100 characters. */
  iconFile: string | null;
  /** The instant the module becomes visible, or `null` for no start restriction. */
  startDate: string | null;
  /** The instant the module stops being visible, or `null` for no end restriction. */
  endDate: string | null;
  /** Markup rendered above the module's content, or `null` for none. */
  header: string | null;
  /** Markup rendered below the module's content, or `null` for none. */
  footer: string | null;
  /**
   * An instruction, not module state: name this module's page settings as the tenant's defaults for newly
   * added modules.
   */
  isDefaultModule: boolean;
  /**
   * An instruction, not module state: copy this placement's page settings onto every module of every
   * non-administrative page of the tenant. Deliberately far-reaching.
   */
  allModules: boolean;
}
