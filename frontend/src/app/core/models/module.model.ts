/**
 * Wire contracts for the module administration surface: modules, their placements on pages, the
 * definitions they are instantiated from, their stored settings, and the transfer of their content.
 *
 * Every declaration below mirrors one backend contract, member for member, so that a change on either
 * side of the boundary surfaces as a type error rather than as a silently dropped value:
 *
 *   - `ModuleListItem`        mirrors `Dtos/Module/ModuleListItemDto.cs`   (13 members)
 *   - `ModuleDetail`          mirrors `Dtos/Module/ModuleDetailDto.cs`     (23 members)
 *   - `CreateModuleRequest`   mirrors `Dtos/Module/CreateModuleRequest.cs` (14 members)
 *   - `UpdateModuleRequest`   mirrors `Dtos/Module/UpdateModuleRequest.cs` (16 members)
 *   - `ModuleSettingsBag`     mirrors `Dtos/Module/ModuleSettingsDto.cs`   (4 members)
 *   - `ModuleDefinition`      mirrors `Dtos/Module/ModuleDefinitionDto.cs` (10 members)
 *   - `ModuleExportRequest`   mirrors `Dtos/Module/ModuleExportRequest.cs` (2 members)
 *   - `ModuleImportRequest`   mirrors `Dtos/Module/ModuleImportRequest.cs` (4 members)
 *
 * `ModuleVisibility` mirrors the API enumeration of the same name. `ModuleSettings`,
 * `MODULE_ALIGNMENT` and `MODULE_VISIBILITY` are the module-settings screen's own vocabulary and are
 * documented individually below.
 *
 * The endpoints these contracts serve are, in full:
 *
 *     GET    /api/v1/portals/{portalId}/modules                    -> a page of ModuleListItem
 *     GET    /api/v1/portals/{portalId}/modules/{moduleId}         -> ModuleDetail
 *     POST   /api/v1/portals/{portalId}/modules                    <- CreateModuleRequest
 *     PUT    /api/v1/portals/{portalId}/modules/{moduleId}         <- UpdateModuleRequest
 *     DELETE /api/v1/portals/{portalId}/modules/{moduleId}
 *     GET    /api/v1/portals/{portalId}/modules/{id}/settings      -> ModuleSettingsBag
 *     PUT    /api/v1/portals/{portalId}/modules/{id}/settings      <- ModuleSettingsBag
 *     POST   /api/v1/portals/{portalId}/modules/{id}/export        <- ModuleExportRequest
 *     POST   /api/v1/portals/{portalId}/modules/import             <- ModuleImportRequest
 *     GET    /api/v1/module-definitions                            -> ModuleDefinition[]
 *     GET    /api/v1/module-definitions/{moduleDefinitionId}       -> ModuleDefinition
 *     GET    /api/v1/module-definitions/desktop-modules/{id}       -> ModuleDefinition[]
 *
 * This file is type-only apart from three declarations that necessarily reach the emitted bundle: the
 * `ModuleVisibility` enumeration and the two frozen lookup maps. It declares no service, no injectable,
 * no component and no runtime behaviour of any kind.
 *
 * MIGRATION: ONE LEGACY CLASS BECOMES FOUR TABLES AND SEVERAL CONTRACTS. The legacy
 *   Library/Components/Modules/ModuleInfo.vb was a single class of 58 properties that flattened a join
 *   across dbo.Modules, dbo.TabModules, dbo.ModuleDefinitions and dbo.ModuleControls, so a caller could
 *   not tell which fact belonged to the module, which to one of its placements, and which to the
 *   definition behind it. The target splits it along the real table boundaries, and the settings that the
 *   legacy class carried as an untyped Hashtable become two genuine key/value tables - dbo.ModuleSettings,
 *   which the upgrade chain alters thirteen times, and dbo.TabModuleSettings, which it alters eight. The
 *   contracts below therefore describe several distinct resources rather than reproducing one flattened
 *   shape, and NO declaration here has 58 members.
 *
 * MIGRATION: THE XML SERIALISATION AND TOKEN SURFACES ARE DROPPED, NOT TRANSLATED. ModuleInfo.vb:L36
 *   carried `<XmlRoot("module", IsNullable:=False)>` and every property an `<XmlElement(...)>` attribute;
 *   L37 declared `Implements IPropertyAccess` against DotNetNuke.Services.Tokens, whose one visible
 *   member was the read-only `Cacheability` at L925 returning a System.Web `CacheLevel`. None of that has
 *   a counterpart here. The wire form is JSON produced by one central serialiser policy, and the
 *   token-replacement subsystem that IPropertyAccess served is out of scope. There is deliberately no
 *   `cacheability` member, no `objectHydrated` member and no `isDirty` member anywhere below.
 *
 * MIGRATION: THE LEGACY NULL SENTINELS DO NOT CROSS THIS BOUNDARY, AND THEIR VALUES REMAIN LEGITIMATE
 *   DATA. Library/Components/Shared/Null.vb encoded absence in-band: NullInteger is -1 (L41-L45),
 *   NullDate is Date.MinValue (L66-L70), NullBoolean is False (L76-L80) and - the trap - NullString is
 *   the EMPTY STRING and not null (L71-L75, literally `Return ""`). Its IsNull test (L208-L237)
 *   accordingly reports -1, "" and False as "absent". Two consequences bind every consumer of this file.
 *   First, an absent value is transported as `null` and never as a sentinel, so a member declared
 *   `string | null` distinguishes "" from absence and a member declared `boolean` is never nullable -
 *   False is a value, not a gap. Second, the sentinel integers are real keys in this schema:
 *   dbo.Modules.ModuleID and dbo.Tabs.TabID are both IDENTITY(0, 1) and dbo.Portals.PortalID is
 *   IDENTITY(-1, 1) (01.00.00.SqlDataProvider lines 221, 140 and 77), so 0 is an ordinary module and an
 *   ordinary page and -1 is an ordinary portal. NEVER test an identifier for truthiness, never compare it
 *   against 0 as though that meant "unsaved", and never coalesce one to -1. Test `=== null` or
 *   `=== undefined` explicitly.
 *
 * MIGRATION: THE SERVER OMITS NOTHING, SO NOTHING HERE IS OPTIONAL MERELY BECAUSE IT MAY BE EMPTY. The
 *   API serialises with `DefaultIgnoreCondition = JsonIgnoreCondition.Never` under a camel-case naming
 *   policy, so every declared member of every response is present on the wire even when it holds null,
 *   0, "" or false. Response members are therefore declared REQUIRED and nullable - `string | null`, not
 *   `string?` - because `undefined` would mean the contract itself changed rather than that the value was
 *   empty. The one narrowly scoped exception is documented on `UpdateModuleRequest`.
 */

import type { PagedResult } from './paged-result.model';

/**
 * How a placement of a module is presented on its page.
 *
 * Mirrors the API enumeration `Domain/Enums/ModuleVisibility.cs` and, through it, the values
 * stored in `dbo.TabModules.Visibility`, declared `int NOT NULL` by 03.00.01.SqlDataProvider line 31.
 * The codes are stored data rather than a presentation detail, which is why each ordinal is written out
 * rather than left to declaration order.
 */
// MIGRATION: D8 RENAME. The legacy enumeration was named VisibilityState and was declared at
//   Library/Components/Modules/ModuleInfo.vb:L30-L34 with NO explicit values at all - three bare member
//   names whose numbering came from their declaration order alone. It is renamed ModuleVisibility here
//   and on the API, and every ordinal is stated: Maximized = 0, Minimized = 1, None = 2. Restating them
//   is the point of the exercise. These integers are persisted in a NOT NULL column, so reordering the
//   members of the legacy declaration would have silently remapped every stored row; writing the numbers
//   down removes that possibility.
//
// MIGRATION: `None` IS A REAL VALUE AND NOT AN ABSENCE SENTINEL. It means the placement renders without
//   its container chrome - a rendering instruction the operator chose - and the legacy screen offered it
//   as the third of three radio choices. It is emphatically NOT "no visibility recorded". Consequently
//   `visibility` is declared non-nullable everywhere it appears, no member is made optional to express
//   this state, and a consumer must never write `visibility ?? ModuleVisibility.Maximized`: that
//   expression cannot fire for a legal payload and would mask a contract fault if it ever did.
//
// MIGRATION: D10 - THE WIRE FORM IS THE INTEGER, VERIFIED AT THE REGISTRATION SITE RATHER THAN ASSUMED.
//   The API's serialiser registers exactly two enumeration converters, for the billing frequency and the
//   permission key, and deliberately does NOT register a blanket string-enumeration converter, because
//   that would claim the billing frequency too and put a member name on the wire where a legacy char(1)
//   code belongs. The module visibility therefore travels as 0, 1 or 2. This is an ordinary numeric
//   enumeration and not a constant one: `isolatedModules` is enabled, under which a constant enumeration
//   is not a sound declaration, so this is one of the three declarations in this file that survive into
//   the emitted bundle.
export enum ModuleVisibility {
  /** The placement renders with its container chrome expanded. The legacy default. */
  Maximized = 0,
  /** The placement renders with its container chrome collapsed. */
  Minimized = 1,
  /** The placement renders without its container chrome. A chosen state, never an absent one. */
  None = 2,
}

/**
 * The visibility codes keyed by their lower-case names.
 *
 * Offered alongside the enumeration because the module-settings screen builds its radio group from a
 * lower-case-keyed map, and because the API's own serialiser registration documents the client shape in
 * exactly this form. The values ARE the enumeration's members, so the two declarations cannot drift: a
 * code appears once in this file and nowhere else.
 */
export const MODULE_VISIBILITY = {
  /** Renders expanded. See {@link ModuleVisibility.Maximized}. */
  maximized: ModuleVisibility.Maximized,
  /** Renders collapsed. See {@link ModuleVisibility.Minimized}. */
  minimized: ModuleVisibility.Minimized,
  /** Renders without chrome. See {@link ModuleVisibility.None}. */
  none: ModuleVisibility.None,
} as const;

/**
 * The four alignment values the legacy module-settings screen offered.
 *
 * MIGRATION: the empty string is a real, selectable choice rather than an absence.
 *   Website/admin/Modules/modulesettings.ascx declares
 *   `<asp:listitem resourcekey="Not_Specified" value="">Not Specified</asp:listitem>` and
 *   Website/admin/Modules/ModuleSettings.ascx.vb:L345 stored `cboAlign.SelectedItem.Value` verbatim, so
 *   choosing "Not Specified" wrote an empty string to the column. That distinction is preserved: an
 *   empty string records that the operator explicitly declined to specify, which is not the same fact as
 *   the column never having been written.
 *
 * MIGRATION: NO CURRENT MODULE CONTRACT TRANSPORTS AN ALIGNMENT. `dbo.TabModules.Alignment` exists and
 *   the domain model maps it, but none of the eight module contracts mirrored in this file projects it,
 *   so these codes are today the screen's own vocabulary rather than a wire value. They are declared
 *   here, beside the contracts, so that the gap is visible at the boundary instead of being rediscovered
 *   per screen; the corresponding member on `UpdateModuleRequest` records the same finding.
 */
export const MODULE_ALIGNMENT = {
  /** The legacy "Not Specified" choice, stored as an empty string. */
  notSpecified: '',
  /** Content is aligned to the leading edge. */
  left: 'left',
  /** Content is centred. */
  center: 'center',
  /** Content is aligned to the trailing edge. */
  right: 'right',
} as const;

/** The alignment values, as a union of the four the legacy screen admitted. */
export type ModuleAlignment = (typeof MODULE_ALIGNMENT)[keyof typeof MODULE_ALIGNMENT];

/**
 * One row of the module listing, as returned by `GET /api/v1/portals/{portalId}/modules`.
 *
 * Mirrors `Dtos/Module/ModuleListItemDto.cs`, which carries exactly these thirteen members. The listing
 * is a projection chosen for a grid: it deliberately omits the schedule text, the header and footer
 * markup, the catalogue metadata and the cache period, all of which the detail contract carries.
 *
 * MIGRATION: A ROW OF THIS LISTING IS A PLACEMENT, NOT A MODULE. One module with `allTabs` set appears
 *   on every page of the portal and therefore contributes one row per page, each with its own
 *   `tabModuleId` and its own `moduleOrder`, while `moduleId` repeats across them. The stable row key is
 *   `tabModuleId`; keying a grid by `moduleId` collapses those rows onto one another.
 */
export interface ModuleListItem {
  /**
   * The module's own identity, from `dbo.Modules.ModuleID`.
   *
   * MIGRATION: D36 - the column is `IDENTITY(0, 1)` (01.00.00.SqlDataProvider line 221), so **0 is a
   *   legitimate module** and simultaneously the value the legacy code would have read as "absent". Never
   *   test this for truthiness and never compare it against 0 as though that meant unsaved.
   */
  readonly moduleId: number;

  /**
   * The identity of THIS placement of the module on a page, from `dbo.TabModules.TabModuleID`.
   *
   * The stable key for a row of this listing, and the key under which placement-scoped settings are
   * recorded. Its identity seeds at 1, a different seed from `moduleId` and `tabId`, which both start
   * at 0 - so the three are not interchangeable even where their ranges overlap.
   */
  readonly tabModuleId: number;

  /**
   * The page this placement sits on, from `dbo.TabModules.TabID`.
   *
   * MIGRATION: the legacy interface called this a PAGE rather than a tab - its picker was captioned
   *   "Move To Page:" - while the schema and this contract both retain the legacy column word. The
   *   referenced `dbo.Tabs.TabID` is `IDENTITY(0, 1)` (01.00.00.SqlDataProvider line 140), so 0 is a
   *   legitimate page.
   */
  readonly tabId: number;

  /**
   * The definition this module was instantiated from, from `dbo.Modules.ModuleDefID`.
   *
   * The referenced `dbo.ModuleDefinitions.ModuleDefID` is `IDENTITY(1, 1)` (01.00.00.SqlDataProvider
   * line 66), so - unlike the module and page keys - no legal value here coincides with a legacy
   * sentinel. It is still an opaque key and is never compared against a bound.
   */
  readonly moduleDefId: number;

  /**
   * The administrator-supplied title of this module instance, or `null` when none was given.
   *
   * Genuinely optional: the legacy settings screen declared no presence validator on the field, so a
   * module with no title is ordinary data rather than a defect, and the screen fell back to the
   * definition's own name for display.
   */
  readonly moduleTitle: string | null;

  /**
   * The display name of the module's definition, projected read-only from
   * `dbo.ModuleDefinitions.FriendlyName` - what the legacy screen showed for "Module:".
   *
   * Carried inline so a grid can name each row without a second request. `null` when the name could not
   * be resolved.
   */
  readonly friendlyName: string | null;

  /**
   * The placement's position within its pane on the page, from `dbo.TabModules.ModuleOrder`.
   *
   * Lower values render nearer the top of the pane. A per-placement fact, so two placements of one
   * module can legitimately sit at different positions on different pages.
   */
  readonly moduleOrder: number;

  /**
   * Whether the module is displayed in the same location on every page of the portal, from
   * `dbo.Modules.AllTabs`. The legacy label read "Display Module On All Pages?".
   *
   * Non-nullable: the column is `bit NOT NULL` with a stored default of 0, and under the legacy sentinel
   * rules False was indistinguishable from absence, so there is no third state to transport.
   */
  readonly allTabs: boolean;

  /** How this placement is presented on its page, from `dbo.TabModules.Visibility`. */
  readonly visibility: ModuleVisibility;

  /**
   * Whether the module is in the soft-deleted state the legacy recycle bin represented, from
   * `dbo.Modules.IsDeleted`.
   *
   * A row carrying this flag still exists and can be restored; the listing includes such rows only when
   * the caller asks for them.
   */
  readonly isDeleted: boolean;

  /**
   * Whether the module's CONTAINER chrome is displayed around this placement.
   *
   * MIGRATION: the member name understates what the value controls. Despite the column being named for
   *   the title, the authoritative legacy wording for this field was "Display Container?" - it governs
   *   the whole container, not the title line alone. The name is kept because it is the column's and the
   *   contract's; the meaning is recorded here so a screen labels it correctly.
   */
  readonly displayTitle: boolean;

  /**
   * The date from which the module begins to be displayed, as an ISO 8601 string, or `null` when no
   * start date is set.
   *
   * MIGRATION: the legacy code stored the absence of a schedule as `Null.NullDate`, which is
   *   `Date.MinValue` and not a database null (Null.vb:L66-L70). That value is translated to `null` here
   *   rather than transported, and the reverse translation must never be inverted: a caller that turns
   *   `null` back into a minimum date writes a schedule where none was intended. Together with
   *   {@link ModuleListItem.endDate} this pair gates whether the module renders at all.
   */
  readonly startDate: string | null;

  /**
   * The date after which the module stops being displayed, as an ISO 8601 string, or `null` when no end
   * date is set.
   *
   * Subject to the identical sentinel treatment as {@link ModuleListItem.startDate}.
   */
  readonly endDate: string | null;
}

/**
 * A page of the module listing, as `GET /api/v1/portals/{portalId}/modules` returns it.
 *
 * The listing is paged and filtered server-side; the query members - the page index and size, the sort,
 * the optional page filter and whether soft-deleted rows are included - are built by the shared HTTP
 * parameter helpers rather than declared here, so that one place owns the spelling of every query key.
 */
export type ModuleListPage = PagedResult<ModuleListItem>;

/**
 * One placement of one module in full, as returned by
 * `GET /api/v1/portals/{portalId}/modules/{moduleId}` and echoed by the create and update endpoints.
 *
 * Mirrors `Dtos/Module/ModuleDetailDto.cs`, which carries exactly these twenty-three members. Nine of
 * them are read-only projections drawn from the definition and the installed package behind it - the
 * friendly name, the programmatic name, the description and the version among them - and are not
 * writable through any module endpoint.
 *
 * MIGRATION: THIS DESCRIBES ONE PLACEMENT, and the two identities are both carried for that reason. Row
 *   identity on the server is the placement (`tabModuleId`), never the module (`moduleId`), because a
 *   module with `allTabs` set has one placement per page of the portal.
 */
export interface ModuleDetail {
  /**
   * The module's own identity, from `dbo.Modules.ModuleID`, whose identity seeds at 0.
   *
   * MIGRATION: D36 - 0 is a legitimate module. See the header note on sentinels.
   */
  readonly moduleId: number;

  /** The identity of this placement, from `dbo.TabModules.TabModuleID` (03.00.01 line 21). */
  readonly tabModuleId: number;

  /**
   * The page this placement sits on, from `dbo.TabModules.TabID`, whose identity seeds at 0.
   *
   * The legacy screen captioned its picker "Move To Page:", so this value is what a page-move writes.
   */
  readonly tabId: number;

  /**
   * The portal that owns the module, from `dbo.Modules.PortalID`, or `null` when the module is not
   * portal-scoped - that is, a host-level module.
   *
   * MIGRATION: THE ONLY GENUINELY NULLABLE IDENTIFIER ON THIS CONTRACT, and its non-null values include
   *   -1: `dbo.Portals.PortalID` is `IDENTITY(-1, 1)` (01.00.00.SqlDataProvider line 77), which is also
   *   the legacy NullInteger. A consumer must therefore read absence from `null` alone and never from the
   *   value -1, which names the first portal.
   */
  readonly portalId: number | null;

  /** The definition this module was instantiated from, from `dbo.Modules.ModuleDefID` (seeds at 1). */
  readonly moduleDefId: number;

  /**
   * The installed package behind the definition, projected from `dbo.ModuleDefinitions.DesktopModuleID`.
   *
   * MIGRATION: unlike the other keys on this contract, this column was added with a STORED DEFAULT OF 0
   *   (02.00.00.SqlDataProvider line 5174), so 0 here can mean either a real package or a row that
   *   predates the association and was defaulted. It is carried as the catalogue cross-reference it is
   *   and is not interpreted as a presence test.
   */
  readonly desktopModuleId: number;

  /**
   * The administrator-supplied heading of this module instance, or `null` when none was given.
   *
   * The legacy help text read: "Enter a title for the Module. This will appear in the Title Bar of the
   * Container for this Module, if supported by the container." No presence validator applied, so `null`
   * is ordinary.
   */
  readonly moduleTitle: string | null;

  /**
   * Whether the module appears in the same location on every page of the portal, from
   * `dbo.Modules.AllTabs`.
   *
   * When set, the server maintains one placement row per page, which is why a change to this flag is a
   * far-reaching write rather than a single-column edit.
   */
  readonly allTabs: boolean;

  /**
   * Free text or markup rendered above the module's content, from `dbo.Modules.Header`, or `null` for
   * none.
   *
   * The column is an unbounded national text type and the legacy control was a six-row multi-line input
   * with no length validator, so no maximum length applies. The legacy help text read: "Enter the text or
   * HTML that you would like to appear above the module content."
   */
  readonly header: string | null;

  /**
   * Free text or markup rendered below the module's content, from `dbo.Modules.Footer`, or `null` for
   * none. Identical in kind to {@link ModuleDetail.header} in every respect.
   */
  readonly footer: string | null;

  /**
   * The date from which the module begins to be displayed, as an ISO 8601 string, or `null` for no start
   * restriction.
   *
   * MIGRATION: `null` here is the translation of the legacy `Null.NullDate` sentinel, which was
   *   `Date.MinValue` rather than a database null. It must not be translated back.
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
   *
   * A FLAG ONLY. The permission entries themselves are not on this contract - no module contract carries
   * them - and are administered through the permission endpoints.
   */
  readonly inheritViewPermissions: boolean;

  /**
   * Whether the module is in the soft-deleted state the legacy recycle bin represented, from
   * `dbo.Modules.IsDeleted`. The row still exists and can be restored.
   */
  readonly isDeleted: boolean;

  /**
   * The placement's position within its pane on the page, from `dbo.TabModules.ModuleOrder`
   * (03.00.01 line 25). Lower values render nearer the top of the pane.
   */
  readonly moduleOrder: number;

  /**
   * How long this placement's rendered output may be cached, in seconds. Zero means no caching.
   *
   * A per-placement fact: the same module can be cached for different periods on different pages. The
   * legacy caption read "Cache Time (secs):".
   *
   * MIGRATION: NOT THE SAME FACT AS {@link ModuleDefinition.defaultCacheTime}, and the two are never
   *   coalesced. See the dedicated note on that member.
   */
  readonly cacheTime: number;

  /**
   * The icon displayed with the module's title for this placement, or `null` for none.
   *
   * A STORED NAME OR RELATIVE PATH ONLY - this contract promises no resolvable URL, and the legacy help
   * text read: "Select an Icon for this Module to display in the Title Bar".
   */
  readonly iconFile: string | null;

  /** How this placement is presented on its page, from `dbo.TabModules.Visibility`. */
  readonly visibility: ModuleVisibility;

  /**
   * Whether the module's CONTAINER chrome is displayed around this placement.
   *
   * MIGRATION: as on the listing, the authoritative legacy wording was "Display Container?" despite the
   *   column and member being named for the title. The value governs the container as a whole.
   */
  readonly displayTitle: boolean;

  /**
   * The definition's display name, projected read-only from `dbo.ModuleDefinitions.FriendlyName`, or
   * `null` when it could not be resolved.
   *
   * The first field the legacy screen rendered, and it rendered it read-only. Its help text read:
   * "Displays the name of the module."
   */
  readonly friendlyName: string | null;

  /**
   * The installed package's unique programmatic name, projected read-only from
   * `dbo.DesktopModules.ModuleName`, or `null` when it could not be resolved.
   *
   * LOAD-BEARING FOR TRANSFER RATHER THAN MERELY DESCRIPTIVE: the legacy export composed its document
   * around this name, so an import is only meaningful against a package that answers to it.
   */
  readonly moduleName: string | null;

  /**
   * The human-readable description of the installed package, projected read-only from
   * `dbo.DesktopModules.Description`, or `null` when none is recorded.
   *
   * Read-only catalogue metadata. Note which table it comes from: the description on this contract is the
   * PACKAGE's, not the definition's.
   */
  readonly description: string | null;

  /**
   * The installed package's version string, projected read-only from `dbo.DesktopModules.Version`, or
   * `null` when none is recorded.
   *
   * Read-only catalogue metadata, and load-bearing for transfer in the same way as
   * {@link ModuleDetail.moduleName} when handing a document between installations.
   */
  readonly version: string | null;
}

/**
 * The body of `POST /api/v1/portals/{portalId}/modules`, which places a new module on a page.
 *
 * Mirrors `Dtos/Module/CreateModuleRequest.cs`, which carries exactly these fourteen members. The
 * portal is named by the route rather than by the body, so it does not appear here.
 *
 * Every member is declared required, in keeping with every other request contract in this folder: the
 * server's defaults are documented on each member so a caller can reproduce them deliberately, rather
 * than being invited to omit members and inherit a default it cannot see.
 *
 * The server validates only four things on this body - the title at most 256 characters, the icon name at
 * most 100, the visibility a defined code, and each date storable by SQL Server. Nothing else is
 * constrained, and in particular neither identifier is bounds-checked, because 0 is a legitimate page.
 */
export interface CreateModuleRequest {
  /**
   * The definition to instantiate. Required.
   *
   * Callers resolve the permitted set from `GET /api/v1/module-definitions`. The identity behind this
   * value seeds at 1, so a real definition identifier is always positive.
   */
  moduleDefId: number;

  /**
   * The page on which to place the module. Required.
   *
   * MIGRATION: this is the placement key, and in the terminal schema it lives on the placement table
   *   only - the module table's own page column was dropped by the upgrade chain. D36 applies: page 0 is
   *   legitimate, so a caller must send the value rather than relying on a falsy check to detect it.
   */
  tabId: number;

  /**
   * The heading for the module, or `null` to leave it unset. Not required. At most 256 characters.
   *
   * The 256-character bound is the column width and is the only bound the server applies; the legacy text
   * box declared a rendered width but no validator.
   */
  moduleTitle: string | null;

  /**
   * Whether the module appears in the same position on every page of the portal, which produces one
   * placement row per page. The server defaults this to `false`.
   */
  allTabs: boolean;

  /** Text or markup rendered above the module's content, or `null` for none. No length bound applies. */
  header: string | null;

  /** Text or markup rendered below the module's content, or `null` for none. No length bound applies. */
  footer: string | null;

  /**
   * The date from which the module is displayed, as an ISO 8601 string, or `null` for no start
   * restriction.
   *
   * The server requires only that a supplied value be a date it can store, which is one of the three
   * rules the legacy screen enforced. `null` is the representation of "unset" - never a minimum date.
   */
  startDate: string | null;

  /**
   * The date until which the module is displayed, as an ISO 8601 string, or `null` for no end
   * restriction. The same rule, the same sentinel translation and the same caution as
   * {@link CreateModuleRequest.startDate}.
   */
  endDate: string | null;

  /**
   * Whether the module takes its View permission from its page instead of carrying its own. The server
   * defaults this to `false`.
   *
   * A FLAG ONLY: the permission entries live behind the permission endpoints and are not part of this
   * body.
   */
  inheritViewPermissions: boolean;

  /**
   * The module's position within its pane on the page.
   *
   * MIGRATION: -1 APPENDS, AND 0 IS A POSITION. The server's default for this member is -1, which appends
   *   the module at the bottom of its pane; sending 0 explicitly means position zero. The distinction is
   *   the one place on the module write surface where the legacy NullInteger value survives as a
   *   deliberate instruction rather than as an absence marker, so it is spelled out here: send -1 to
   *   append, send a non-negative index to place.
   */
  moduleOrder: number;

  /**
   * How long the module's output may be cached, in seconds. Zero means no caching, which is the server's
   * default. A supplied value need only be an integer.
   *
   * MIGRATION: this is the per-placement cache period and is a different fact from a definition's
   *   {@link ModuleDefinition.defaultCacheTime}. The two are never coalesced; see that member's note.
   */
  cacheTime: number;

  /**
   * The icon displayed with the module's title, or `null` for none. Not required. At most 100 characters.
   *
   * The 100-character bound is the column width. A stored name or relative path, never a resolved URL.
   */
  iconFile: string | null;

  /**
   * How the module is presented on the page.
   *
   * MIGRATION: the server's default relies on a coincidence that must not be disturbed - the legacy
   *   default was the maximised state, and that member is numbered 0, which is also the default of the
   *   underlying value type. Renumbering {@link ModuleVisibility} would therefore change the behaviour of
   *   every request that omits this member, which is one more reason the ordinals are written out.
   */
  visibility: ModuleVisibility;

  /**
   * Whether the module's container is displayed. The server defaults this to `true`.
   *
   * MIGRATION: despite the column and member name, this controls the CONTAINER rather than the title
   *   alone. The legacy markup labelled it "Display Title?" while the authoritative resource wording was
   *   "Display Container?"; the wider meaning is the operative one.
   */
  displayTitle: boolean;
}

/**
 * The body of `PUT /api/v1/portals/{portalId}/modules/{moduleId}`.
 *
 * Mirrors `Dtos/Module/UpdateModuleRequest.cs`, whose sixteen members are the first group below. The
 * second group holds six placement fields and two instruction flags that the module-settings screen still
 * posts and that the current server contract no longer carries; they are marked deprecated and are
 * documented individually.
 *
 * MIGRATION: THIS IS A WHOLE-ROW REPLACEMENT, exactly as the legacy postback was. An omitted nullable
 *   member is not "leave it alone" - the server's projection writes the absent value, clearing the
 *   column. That is deliberate and matches the legacy screen, where an empty text box posted an empty
 *   value; without it an operator could set a header but never remove one.
 *
 * MIGRATION: WHY FOUR MEMBERS OF THE MIRRORED GROUP ARE OPTIONAL. Every other request contract in this
 *   folder declares all of its members required, and this one deviates for exactly four - `tabId`,
 *   `isDeleted`, `setAsDefaultSettings` and `applyToAllModules`. Three of them are genuinely optional:
 *   their own contract documentation states a default of `false`, none is covered by a validation rule,
 *   and the server's ignore condition governs what it WRITES rather than what it requires to READ, so
 *   omitting them is a supported and safe choice whose effect is written on each member. `tabId` is the
 *   exception and is called out on its own member: it is documented as required, no validator enforces
 *   it, and omitting it is NOT safe. The four are optional because the module-settings screen - which
 *   this contract must keep compiling and which is owned elsewhere - does not yet send them; the
 *   optionality is a transitional accommodation, not a statement that the values are unimportant.
 *
 * MIGRATION: D7 - NO DELIMITED PERMISSION OR ROLE STRING APPEARS ON THIS CONTRACT, OR ON ANY OTHER IN
 *   THIS FILE. The legacy module carried four semicolon-delimited display strings - `Permissions`
 *   (ModuleInfo.vb:L473), `AuthorizedRoles` (L627), `AuthorizedEditRoles` (L545) and
 *   `AuthorizedViewRoles` (L554) - each a flattened rendering of the permission table that a caller had to
 *   split, and each ambiguous the moment a role name contained the delimiter. Those strings are
 *   deliberately abandoned rather than reproduced or parsed. The structured replacement is the
 *   `ModulePermission` shape in `./permission.model`, which carries a first-class `roleId`, a nullable
 *   `userId` that distinguishes a user grant from a role grant, and a separate `allowAccess` flag that
 *   makes a DENIAL expressible - something no delimited list of granted names could express. No module
 *   contract carries permission entries at all: this body transports only the
 *   {@link UpdateModuleRequest.inheritViewPermissions} flag, and the entries are administered through the
 *   permission endpoints.
 */
export interface UpdateModuleRequest {
  // ---------------------------------------------------------------------------------------------------
  // Mirrored from Dtos/Module/UpdateModuleRequest.cs
  // ---------------------------------------------------------------------------------------------------

  /**
   * The page this placement is on. Documented as required by the contract it mirrors.
   *
   * MIGRATION: OMITTING THIS ADDRESSES PAGE ZERO, SILENTLY. No validation rule covers this member -
   *   deliberately, because `dbo.Tabs.TabID` is `IDENTITY(0, 1)` (01.00.00.SqlDataProvider line 140) and a
   *   positive-value rule would reject the legitimate page 0. An absent member therefore deserialises to
   *   the value type's default of 0, which the server cannot tell apart from a caller genuinely naming
   *   page 0. Send it on every update. It is declared optional only for the transitional reason given in
   *   the note on this interface, and it is the one member of the four for which omission is unsafe.
   */
  tabId?: number;

  /** The heading for the module, or `null` to leave it unset. Not required. At most 256 characters. */
  moduleTitle: string | null;

  /**
   * Whether the module appears in the same position on every page of the portal, which produces one
   * placement row per page. The server defaults this to `false`.
   */
  allTabs: boolean;

  /** Text or markup rendered above the module's content, or `null` for none. No length bound applies. */
  header: string | null;

  /** Text or markup rendered below the module's content, or `null` for none. No length bound applies. */
  footer: string | null;

  /**
   * The date from which the module is displayed, as an ISO 8601 string, or `null` for no start
   * restriction. A supplied value need only be a date the server can store.
   */
  startDate: string | null;

  /**
   * The date until which the module is displayed, as an ISO 8601 string, or `null` for no end
   * restriction. The same rule and the same sentinel translation as
   * {@link UpdateModuleRequest.startDate}.
   */
  endDate: string | null;

  /**
   * Whether the module takes its View permission from its page instead of carrying its own. The server
   * defaults this to `false`. A flag only - see the D7 note on this interface.
   */
  inheritViewPermissions: boolean;

  /**
   * Whether the module is in the recycle bin. The server defaults this to `false`.
   *
   * The soft-delete marker. The column is non-nullable with a stored default of 0, which coincides with
   * the default of the mirrored value type, so omitting this member leaves a module out of the recycle
   * bin - the safe outcome, and the reason omission is supported here.
   */
  isDeleted?: boolean;

  /**
   * The module's position within its pane on the page.
   *
   * MIGRATION: -1 APPENDS, AND 0 IS A POSITION - identical to the create contract. The server's default
   *   is -1, which appends at the bottom of the pane; sending 0 explicitly means position zero.
   */
  moduleOrder: number;

  /**
   * How long the module's output may be cached, in seconds. Zero means no caching, which is the server's
   * default.
   *
   * MIGRATION: the per-placement cache period, and a DIFFERENT FACT from a definition's
   *   {@link ModuleDefinition.defaultCacheTime}. The two are never merged; see that member's note.
   */
  cacheTime: number;

  /**
   * The icon displayed with the module's title, or `null` for none. Not required. At most 100 characters.
   */
  iconFile: string | null;

  /** How the module is presented on the page. Must be a defined code; the server checks that. */
  visibility: ModuleVisibility;

  /**
   * Whether the module's container is displayed. The server defaults this to `true`.
   *
   * Despite the name, this governs the container rather than the title alone.
   */
  displayTitle: boolean;

  /**
   * An instruction rather than module state: name this module and its page as the portal's default
   * settings for newly added modules. The server defaults this to `false`.
   *
   * The legacy label read "Set As Default Settings?". Because it describes work the server performs after
   * the update rather than a column on the module, it must be seeded unset on every form rather than
   * echoed back from the module - echoing a previous instruction would reapply it on the next submission.
   */
  setAsDefaultSettings?: boolean;

  /**
   * An instruction rather than module state: copy this placement's appearance to every module on every
   * non-administrative page of the portal. The server defaults this to `false`.
   *
   * THE MOST FAR-REACHING MEMBER ON THE MODULE API. Like
   * {@link UpdateModuleRequest.setAsDefaultSettings} it is seeded unset rather than echoed back, and for
   * the same reason. Omission is safe precisely because the default declines to act.
   */
  applyToAllModules?: boolean;

  // ---------------------------------------------------------------------------------------------------
  // Retained for the module-settings screen; NOT carried by the current server contract
  //
  // MIGRATION: A PROJECTION GAP, RECORDED RATHER THAN PAPERED OVER. Every member in this group exists as
  //   a mapped column on the domain placement entity - PaneName, Alignment, Color, Border, DisplayPrint
  //   and DisplaySyndicate are all declared on the TabModule entity and bound by its entity
  //   configuration - but none of them is projected onto `Dtos/Module/UpdateModuleRequest.cs`, so the
  //   server does not read them and a value sent here has no effect. They are declared, optional and
  //   deprecated, so that the module-settings screen continues to compile against a contract that tells
  //   the truth about what the server accepts, rather than the contract being widened to imply that these
  //   values are transported. The legacy screen genuinely edited all eight -
  //   Website/admin/Modules/modulesettings.ascx renders the alignment, colour and border inputs and the
  //   two display check boxes - so the gap is a real reduction in the write surface and is reported as
  //   one, not silently absorbed.
  // ---------------------------------------------------------------------------------------------------

  /**
   * Which pane of the page's layout the placement occupies.
   *
   * @deprecated Not carried by the current server contract; see the note above this group.
   */
  paneName?: string;

  /**
   * The placement's alignment, one of {@link MODULE_ALIGNMENT}'s values.
   *
   * @deprecated Not carried by the current server contract; see the note above this group.
   */
  alignment?: string | null;

  /**
   * The placement's recorded colour. An opaque persisted value, never a style input.
   *
   * @deprecated Not carried by the current server contract; see the note above this group.
   */
  color?: string | null;

  /**
   * The placement's border flag: a single digit, or `null` for none.
   *
   * @deprecated Not carried by the current server contract; see the note above this group.
   */
  border?: string | null;

  /**
   * Whether the placement offers a print affordance.
   *
   * @deprecated Not carried by the current server contract; see the note above this group.
   */
  displayPrint?: boolean;

  /**
   * Whether the placement offers a syndication affordance.
   *
   * @deprecated Not carried by the current server contract; see the note above this group.
   */
  displaySyndicate?: boolean;

  /**
   * The former spelling of {@link UpdateModuleRequest.setAsDefaultSettings}.
   *
   * @deprecated Renamed on the server contract to `setAsDefaultSettings`; send that member instead.
   */
  isDefaultModule?: boolean;

  /**
   * The former spelling of {@link UpdateModuleRequest.applyToAllModules}.
   *
   * @deprecated Renamed on the server contract to `applyToAllModules`; send that member instead.
   */
  allModules?: boolean;
}

/**
 * The stored settings of a module and of one of its placements.
 *
 * Mirrors `Dtos/Module/ModuleSettingsDto.cs`, which carries exactly these four members. It serves BOTH
 * directions of the same resource: it is the body of
 * `GET /api/v1/portals/{portalId}/modules/{moduleId}/settings` and the body of the matching `PUT`.
 *
 * MIGRATION: THE UNTYPED SETTINGS HASHTABLE BECOMES TWO REAL KEY/VALUE TABLES, AND THE SPLIT IS THE
 *   POINT. The legacy module class carried its settings as a single ambient `Hashtable` in which nothing
 *   distinguished a value recorded against the module from one recorded against a single placement of it.
 *   The terminal schema keeps them apart in two tables that the upgrade chain maintains separately -
 *   `dbo.ModuleSettings`, which it alters thirteen times, and `dbo.TabModuleSettings`, which it alters
 *   eight - each with a composite key. This contract preserves that separation: a value in
 *   {@link ModuleSettingsBag.moduleSettings} is identical on every page the module appears on, and a value
 *   in {@link ModuleSettingsBag.tabModuleSettings} belongs to one occurrence on one page. Collapsing the
 *   two maps into one would destroy the distinction the schema was migrated to express.
 *
 * MIGRATION: WHY THIS TYPE CARRIES A SUFFIX. Every other contract in this folder drops the backend `Dto`
 *   suffix, which would name this one `ModuleSettings`. That name is taken by the module-settings screen's
 *   own view model, declared at the end of this file and consumed by a screen owned elsewhere, so
 *   reusing it would silently rebind an existing import to a structurally unrelated shape. The suffix
 *   names what the type is - two property bags - and should be retired in favour of the plain name once
 *   that screen is rebuilt against the current contract.
 */
export interface ModuleSettingsBag {
  /**
   * The module whose module-scoped settings are carried in {@link ModuleSettingsBag.moduleSettings}.
   *
   * MIGRATION: D36 - the column is an identity seeded at zero, so 0 is a legitimate module and must never
   *   be read as an absent or unsaved one.
   */
  readonly moduleId: number;

  /**
   * The placement whose placement-scoped settings are carried in
   * {@link ModuleSettingsBag.tabModuleSettings}, or `null` when no placement was addressed.
   *
   * The only nullable member on this contract. `null` means no placement was named, in which case
   * {@link ModuleSettingsBag.tabModuleSettings} is empty rather than absent.
   */
  readonly tabModuleId: number | null;

  /**
   * The settings recorded against the module itself, and therefore identical on every page the module
   * appears on, from `dbo.ModuleSettings`.
   *
   * Keys are bounded at 50 characters and values at 2000 by the terminal column widths. An empty map
   * means the module has no module-scoped settings - the member is always present.
   *
   * Accessed with an index expression rather than a property access: `noPropertyAccessFromIndexSignature`
   * is enabled, so read a value as `bag.moduleSettings['SomeKey']`, which yields `string | undefined`
   * under `strict` and forces the missing-key case to be handled.
   */
  readonly moduleSettings: Readonly<Record<string, string>>;

  /**
   * The settings recorded against one placement alone, and therefore specific to a single occurrence of
   * the module on a single page, from `dbo.TabModuleSettings`.
   *
   * Bounded and accessed exactly as {@link ModuleSettingsBag.moduleSettings} is. Empty when
   * {@link ModuleSettingsBag.tabModuleId} is `null`.
   */
  readonly tabModuleSettings: Readonly<Record<string, string>>;
}

/**
 * One module definition, as returned by the `module-definitions` endpoints.
 *
 * Mirrors `Dtos/Module/ModuleDefinitionDto.cs`, which carries exactly these ten members: four describing
 * the definition and six projected read-only from the installed package that owns it. This is the
 * catalogue a caller reads to populate the definition picker before creating a module.
 *
 * MIGRATION: THE LEGACY DEFINITION CLASS HAD FIVE MEMBERS AND ONE OF THEM IS DELIBERATELY NOT HERE.
 *   Library/Components/Modules/ModuleDefinitionInfo.vb declared exactly five properties - ModuleDefID
 *   (L42), FriendlyName (L50), DesktopModuleID (L58), TempModuleID (L66) and DefaultCacheTime (L74). Four
 *   appear below. `TempModuleID` does not, and must not be added: it was a transient install-time artefact
 *   used to correlate rows while a package was being written, never a persisted fact about a definition,
 *   and it is absent from the contract this type mirrors.
 *
 * MIGRATION: THE WEB FORMS CONTROL SURFACE IS NOT MODELLED. A definition owned a set of module controls,
 *   described by Library/Components/Modules/ModuleControlInfo.vb's ten properties, and those controls were
 *   the mechanism by which the legacy runtime LOADED an .ascx into a page - the excluded Web Forms
 *   control-loading subsystem. The contract carries no control collection and this type therefore declares
 *   none. In particular there is no `controlType` member: its legacy type, `SecurityAccessLevel`, is not
 *   among the enumerations ported to the API, the contract does not carry the value, and inventing a
 *   member for it would fabricate a wire field that no endpoint produces.
 *
 * MIGRATION: THE FEATURE BIT FIELD IS NOT MODELLED EITHER, AND ONLY ONE OF ITS THREE FLAGS SURVIVES. The
 *   legacy package class carried `SupportedFeatures` as a bare Integer bit field whose vocabulary lived in
 *   a companion enumeration, `DesktopModuleSupportedFeature`, declared at DesktopModuleInfo.vb:L30; both
 *   the field and the enumeration are deliberately not ported, because a caller reading a bit field would
 *   be reimplementing the server's own interpretation of it. Of the three capabilities the legacy code
 *   derived from those bits - portable, searchable and upgradeable - the contract projects only
 *   {@link ModuleDefinition.isPortable}, which is the one the administration surface acts on.
 */
export interface ModuleDefinition {
  /**
   * The identity of this definition, from `dbo.ModuleDefinitions.ModuleDefID`.
   *
   * The column is `int IDENTITY (1, 1) NOT NULL` and the table's primary key
   * (01.00.00.SqlDataProvider line 66). The seed is 1, so no legal value coincides with a legacy
   * sentinel; it is nevertheless an opaque key and is never compared against a bound.
   *
   * MIGRATION: the route that addresses a single definition spells its parameter `moduleDefinitionId`,
   *   not `moduleDefId`. The wire member and the route parameter are deliberately different spellings of
   *   the same value, so a caller building the URL must use the route's spelling.
   */
  readonly moduleDefId: number;

  /**
   * The display name of this definition - the value the legacy settings screen showed for "Module:".
   *
   * From `dbo.ModuleDefinitions.FriendlyName`, measured `nvarchar(128) NOT NULL`, so the effective
   * maximum length is 128 characters and the member is non-nullable. An unnamed definition presents as
   * the empty string rather than as `null`.
   */
  readonly friendlyName: string;

  /**
   * The installed package that owns this definition, from `dbo.ModuleDefinitions.DesktopModuleID`.
   *
   * MIGRATION: added by 02.00.00.SqlDataProvider line 5173 as `int NOT NULL` with a stored default of 0
   *   and made a foreign key at line 5258, so 0 here may mean either a real package or a row that predates
   *   the association. It is the catalogue cross-reference and is not a presence test.
   */
  readonly desktopModuleId: number;

  /**
   * The definition's default cache timeout in seconds - the legacy "Cache Time (secs):" field, whose help
   * text read "Enter the time this object is kept in the Cache".
   *
   * From `dbo.ModuleDefinitions.DefaultCacheTime`.
   *
   * MIGRATION: THIS IS NOT THE SAME FACT AS A PLACEMENT'S CACHE PERIOD, AND THE TWO ARE NEVER COALESCED.
   *   The legacy class carried both at once and gave them distinct XML element names to keep them apart -
   *   ModuleInfo.vb:L203 declared `CacheTime` as `<XmlElement("cachetime")>` and L482 declared
   *   `DefaultCacheTime` as `<XmlElement("defaultcachetime")>` - and the split is preserved here across
   *   two contracts: `cacheTime` belongs to one placement of one module and lives on
   *   {@link ModuleDetail.cacheTime}, {@link CreateModuleRequest.cacheTime} and
   *   {@link UpdateModuleRequest.cacheTime}, while this member is the definition-level default that a new
   *   placement starts from. `defaultCacheTime` of -1 is a DIFFERENT FACT from `cacheTime` of 0: the first
   *   says the definition records no default, the second says this placement is not cached. A consumer
   *   must never write `cacheTime ?? defaultCacheTime`, never derive a single "effective" cache period
   *   inside a type, and never treat one member as the other's fallback - resolving them is the server's
   *   business, and merging them would lose the distinction the legacy contract took care to keep.
   */
  readonly defaultCacheTime: number;

  /**
   * The unique programmatic name of the owning package, from `dbo.DesktopModules.ModuleName`.
   *
   * Measured `nvarchar(128)`, promoted to `NOT NULL` by 03.01.00.SqlDataProvider line 26 and made unique
   * at line 30, so the member is non-nullable and its effective maximum length is 128 characters.
   */
  readonly moduleName: string;

  /**
   * A human-readable description of the owning package, from `dbo.DesktopModules.Description`, or `null`
   * when none is recorded. Measured `nvarchar(2000) NULL`.
   */
  readonly description: string | null;

  /**
   * The installed version of the owning package, from `dbo.DesktopModules.Version`, or `null` when none
   * is recorded.
   *
   * Measured `nvarchar(8) NULL` - a deliberately narrow column, so the effective maximum length is only
   * eight characters.
   */
  readonly version: string | null;

  /**
   * Whether the owning package is a premium module, from `dbo.DesktopModules.IsPremium`.
   *
   * Measured `bit NOT NULL`, hence non-nullable. A premium module is not automatically available to every
   * portal: its availability is granted per portal.
   */
  readonly isPremium: boolean;

  /**
   * Whether the owning package is an administration module, from `dbo.DesktopModules.IsAdmin`.
   *
   * Measured `bit NOT NULL`, hence non-nullable. Administration modules are the ones surfaced through the
   * administrative areas of a portal.
   */
  readonly isAdmin: boolean;

  /**
   * Whether the owning package supports content export and import.
   *
   * A read-only projection computed when the row is read; it is not writable through any contract in this
   * file. It is the flag the export and import endpoints depend on: a definition for which this is
   * `false` has no content to transfer.
   */
  readonly isPortable: boolean;
}

/**
 * The body of `POST /api/v1/portals/{portalId}/modules/{moduleId}/export`.
 *
 * Mirrors `Dtos/Module/ModuleExportRequest.cs`, which carries exactly these two members. The module is
 * named by the route rather than by the body.
 *
 * MIGRATION: NO FILE, STREAM OR MULTIPART TYPE APPEARS ON THIS CONTRACT. The legacy screen wrote a file
 *   to a portal folder through a postback; the replacement is a JSON request whose two members are plain
 *   text. How a caller obtains or stores the resulting document is a service-layer concern, so this file
 *   deliberately declares no upload primitive, no form-data shape and no binary type.
 */
export interface ModuleExportRequest {
  /**
   * The base name to label the exported document with. Mandatory and non-blank; at most 200 characters.
   *
   * MIGRATION: THIS IS THE MIDDLE OF A COMPOSED NAME, NOT THE FINAL FILE NAME. The legacy screen built the
   *   stored name from the module's programmatic name, this value and an extension, so a caller that sends
   *   a value expecting it to be used verbatim will not recognise the result. It is declared
   *   `string | null` because the contract it mirrors is nullable and rejects a blank value at validation
   *   rather than at binding - which yields a field-level message instead of a malformed-body error.
   */
  fileName: string | null;

  /**
   * The portal-relative folder the caller intends the document for, or `null` when it has none.
   *
   * ACCEPTED AND DELIBERATELY UNUSED. It is optional, retained for parity with the legacy screen's folder
   * picker, and is not used to store anything - no path is resolved from it. Sending it changes nothing;
   * it exists so a caller migrating from the legacy screen is not forced to discard the value.
   */
  folder: string | null;
}

/**
 * The body of `POST /api/v1/portals/{portalId}/modules/import`.
 *
 * Mirrors `Dtos/Module/ModuleImportRequest.cs`, which carries exactly these four members. Note that the
 * module is named by the BODY here rather than by the route, which is why `moduleId` appears below while
 * it is absent from {@link ModuleExportRequest}.
 *
 * MIGRATION: as with the export contract, no file, stream or multipart type is declared. The document
 *   travels as text in {@link ModuleImportRequest.content}.
 */
export interface ModuleImportRequest {
  /**
   * The module whose content is being replaced. Mandatory - the only member of this contract that is.
   *
   * MIGRATION: NULLABLE PRECISELY SO THAT ABSENCE CAN BE TOLD APART FROM A LEGITIMATE VALUE. D36 applies
   *   with unusual force here: `dbo.Modules.ModuleID` is `IDENTITY(0, 1)`
   *   (01.00.00.SqlDataProvider line 221), so 0 names a real module. Had the member been a plain integer,
   *   an omitted value would have arrived as 0 and been indistinguishable from a caller naming module
   *   zero; nullability is what lets the server reject the omission instead of silently importing into the
   *   wrong module. A caller must send a number - `null` exists to be refused, not to be sent.
   */
  moduleId: number | null;

  /**
   * The exported document to load, as text.
   *
   * Effectively mandatory: a blank value is refused. The module's own portability behaviour interprets
   * what the document holds; this contract carries it verbatim and makes no claim about its format.
   */
  content: string | null;

  /**
   * The portal-relative folder the document came from, or `null` when the caller has none.
   *
   * ACCEPTED AND DELIBERATELY UNUSED, exactly as on the export contract: it is resolved against nothing.
   */
  folder: string | null;

  /**
   * The name of the document the content came from, or `null` when the caller has none.
   *
   * Optional parity metadata that no decision depends on, and never interpreted as a path. In the legacy
   * screen the equivalent value was load-bearing because the server read the file itself; here the content
   * arrives in the body, so the name is descriptive only.
   */
  fileName: string | null;
}

/**
 * The state the module-settings screen renders and edits.
 *
 * MIGRATION: THIS IS A SCREEN VIEW MODEL AND NOT THE MIRROR OF ANY SINGLE CONTRACT - read that before
 *   using it. It is retained under its established name because the module-settings screen, which is owned
 *   elsewhere, imports it; rebinding the name would silently change the meaning of an existing import. It
 *   is NOT the mirror of `Dtos/Module/ModuleSettingsDto.cs` - that contract is the key/value
 *   {@link ModuleSettingsBag} - and it is not the mirror of `Dtos/Module/ModuleDetailDto.cs` either:
 *   nineteen of its members correspond to {@link ModuleDetail} members, and six do not appear on any
 *   current module contract at all.
 *
 * MIGRATION: THE SIX MEMBERS WITH NO WIRE COUNTERPART are `paneName`, `alignment`, `color`, `border`,
 *   `displayPrint` and `displaySyndicate`. Each exists as a mapped column on the domain placement entity
 *   and each was editable on the legacy screen, but none is projected onto the module read contract, so no
 *   endpoint supplies a value for it. The same projection gap is recorded on the corresponding members of
 *   {@link UpdateModuleRequest}. They are kept here rather than deleted so the gap stays visible at the
 *   boundary and the screen that depends on them keeps compiling; the corrective work is a backend
 *   projection change, not a frontend one.
 *
 * MIGRATION: THIS TYPE ALSO CARRIES FOUR MEMBERS WHOSE NULLABILITY IS LOOSER THAN THE READ CONTRACT'S -
 *   `cacheTime` and `inheritViewPermissions` are nullable here and non-nullable on {@link ModuleDetail},
 *   and `friendlyName` is non-nullable here and nullable there. That is a second, quieter symptom of the
 *   same drift. New code should consume {@link ModuleDetail}, whose nullability is the contract's; this
 *   type should be retired when the screen is rebuilt against it.
 *
 * @deprecated Prefer {@link ModuleDetail} for the module read contract and {@link ModuleSettingsBag} for
 *   the stored settings resource. Retained for the module-settings screen only.
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
  /** Which pane of the page's layout the placement occupies. No wire counterpart; see the notes above. */
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
  /**
   * How long this placement's output may be cached, in seconds. Zero disables caching.
   *
   * Never conflated with {@link ModuleDefinition.defaultCacheTime}, which is the definition-level default
   * and a separate fact.
   */
  readonly cacheTime: number | null;
  /** The placement's icon, or `null` for none. */
  readonly iconFile: string | null;
  /** The placement's alignment. No wire counterpart; see the notes above. */
  readonly alignment: string | null;
  /** The placement's recorded colour, an opaque value. No wire counterpart; see the notes above. */
  readonly color: string | null;
  /** The placement's border flag: one digit, or `null`. No wire counterpart; see the notes above. */
  readonly border: string | null;
  /** How the placement renders. */
  readonly visibility: ModuleVisibility;
  /** Whether the placement shows its container chrome. */
  readonly displayTitle: boolean;
  /** Whether the placement offers a print affordance. No wire counterpart; see the notes above. */
  readonly displayPrint: boolean;
  /** Whether the placement offers a syndication affordance. No wire counterpart; see the notes above. */
  readonly displaySyndicate: boolean;
}
