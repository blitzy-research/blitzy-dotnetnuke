import {
  type ModuleVisibility,
  type UpdateModuleRequest,
} from '../../../core/models/module.model';

/**
 * The module-settings screen's own view model, and the adapter that turns its form state into the
 * canonical wire request.
 *
 * WHY THIS FILE EXISTS, AND WHY IT IS NOT IN `core/models`. `core/models` holds wire contracts: each
 * declaration there mirrors a server data-transfer contract member for member, so a reader can trust that
 * everything declared is transported and everything transported is declared. This screen needs more than
 * the wire carries - it holds two values it is seeded with but never edits, and it names two operator
 * instructions the wire spells differently - so a single type could not be both the screen's state and the
 * wire's shape without one of the two lying. Putting the screen's state beside the screen, and keeping the
 * wire contract narrow, is what lets both stay honest. The boundary between them is the adapter at the
 * bottom of this file, which is the one place the two shapes meet.
 *
 * MIGRATION: SIX LEGACY PLACEMENT VALUES ARE ABSENT FROM THIS FILE ALTOGETHER - `paneName`, `alignment`,
 *   `color`, `border`, `displayPrint` and `displaySyndicate`. Each is a mapped column on the TabModule
 *   entity and each was genuinely editable on the legacy screen -
 *   `Website/admin/Modules/modulesettings.ascx` renders the alignment, colour and border inputs and the
 *   two display check boxes, and `ModuleSettings.ascx.vb:L345` stored `cboAlign.SelectedItem.Value`
 *   verbatim - but none of them is projected onto `Dtos/Module/UpdateModuleRequest.cs`, and none is read
 *   back by any module contract either. The screen therefore renders no control for them and this view
 *   model declares none: an editable control whose value the API discards tells an administrator a save
 *   happened when it did not, and a view-model member no read contract can populate would have to be
 *   invented. The stored columns are preserved by not projecting them through the update at all. That is a
 *   real reduction in the write surface against the legacy screen, it is recorded in MIGRATION_NOTES.md,
 *   and closing it is a server-side projection change rather than a frontend one.
 *
 * MIGRATION: THREE MEMBERS HERE ARE MORE PERMISSIVE THAN THE READ CONTRACT, and that is drift rather than
 *   a second decision. `cacheTime` and `inheritViewPermissions` are nullable here and non-nullable on the
 *   module read contract, and `friendlyName` is non-nullable here and nullable there. The looser spellings
 *   are retained so the screen can render a partially resolved module without inventing values; the
 *   adapter and the form seed resolve each one explicitly, so nothing loose reaches the wire.
 */

/**
 * The state the module-settings screen renders and edits.
 *
 * Every member corresponds to a member of the module read contract, and every member is readonly: the
 * screen seeds a form from this value and never writes back through it.
 */
export interface ModuleSettingsViewModel {
  // -----------------------------------------------------------------------------------------------------
  // Identity — supplied by the read contract, never edited on this screen
  // -----------------------------------------------------------------------------------------------------

  /** The module's identifier. Zero is a legal value: `dbo.Modules.ModuleID` is `IDENTITY(0,1)`. */
  readonly moduleId: number;

  /** This placement's identifier, from `dbo.TabModules.TabModuleID`. */
  readonly tabModuleId: number;

  /**
   * The page this placement sits on, which seeds the page picker.
   *
   * It is a required member of the wire contract and zero is a legitimate page, so it can never be
   * defaulted or inferred - the form is seeded with the value the screen was given.
   */
  readonly tabId: number;

  /** The owning tenant, or `null` when the module is host-owned. */
  readonly portalId: number | null;

  /** Which definition the module instantiates. Fixed at creation and never editable. */
  readonly moduleDefId: number;

  /** The definition's display name, shown read-only. Empty when the name could not be resolved. */
  readonly friendlyName: string;

  // -----------------------------------------------------------------------------------------------------
  // Editable state that the wire contract carries
  // -----------------------------------------------------------------------------------------------------

  /** The placement's heading, or `null` to take the definition's name. */
  readonly moduleTitle: string | null;

  /** The placement's position within its pane. */
  readonly moduleOrder: number;

  /** Whether the module appears on every page of the tenant. */
  readonly allTabs: boolean;

  /**
   * Whether the module is in the recycle bin.
   *
   * Not editable on this screen, and carried for the same reason as {@link tabId}: the wire contract is a
   * whole-row replacement, so a submission that omitted it would clear the flag as a side effect of
   * saving an unrelated field.
   */
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
   * Never conflated with the definition-level default cache time, which is a separate fact.
   */
  readonly cacheTime: number | null;

  /** The placement's icon, or `null` for none. */
  readonly iconFile: string | null;

  /** How the placement renders. */
  readonly visibility: ModuleVisibility;

  /** Whether the placement shows its container chrome. */
  readonly displayTitle: boolean;
}

/**
 * The raw value of the module-settings form, as `getRawValue()` produces it.
 *
 * Declared here rather than inferred at the call site so that the adapter's contract is explicit and so
 * that adding a control to the form without deciding whether it is transported does not compile.
 *
 * Every member is non-nullable because every control is declared `nonNullable`; the empty string is the
 * form's spelling of "nothing entered", and the adapter is where it becomes `null` for the members whose
 * wire contract is nullable.
 */
export interface ModuleSettingsFormState {
  /**
   * The page whose placement is being edited, from the picker the legacy screen labelled "Move To Page".
   *
   * Editable, and transported: the server SELECTS the placement by it. Zero is a legitimate page, so it is
   * never defaulted or read as absence.
   */
  readonly tabId: number;

  readonly moduleTitle: string;
  readonly moduleOrder: number;
  readonly allTabs: boolean;
  readonly inheritViewPermissions: boolean;
  readonly header: string;
  readonly footer: string;
  readonly startDate: string;
  readonly endDate: string;
  readonly iconFile: string;
  readonly visibility: ModuleVisibility;
  readonly displayTitle: boolean;
  readonly cacheTime: number;

  /**
   * The operator's instruction to make these the portal's default settings for new modules.
   *
   * Carried under the member the server reads, so the adapter passes it through rather than renaming it.
   * The legacy control was named `isDefaultModule`; that spelling is not a wire member and appears nowhere
   * on this contract.
   */
  readonly setAsDefaultSettings: boolean;

  /**
   * The operator's instruction to copy this placement's settings to every module on every page.
   *
   * Carried under the member the server reads, for the same reason as {@link setAsDefaultSettings}. The
   * legacy control was named `allModules`.
   */
  readonly applyToAllModules: boolean;
}

/**
 * The two values the wire contract requires that the form does not hold.
 *
 * Both come from the view model the screen was seeded with, and both exist because the update is a
 * whole-row replacement: omitting either would write a value the operator never chose.
 */
export interface ModuleSettingsRequestContext {
  /** The recycle-bin flag, from {@link ModuleSettingsViewModel.isDeleted}. */
  readonly isDeleted: boolean;
}

/**
 * Projects the screen's form state onto the canonical module update request.
 *
 * This is the ONLY place the screen's shape and the wire's shape meet, which is what makes the boundary
 * reviewable in one reading rather than spread across a template and a component. Three things happen here
 * and nowhere else:
 *
 * 1. the fifteen editable values the form holds are projected onto their wire members, and nothing else is
 *    sent - a control added to the form without a decision about transport does not compile;
 * 2. emptied text becomes `null`, so an emptied field clears its column;
 * 3. `isDeleted` is taken from the seeded view model, because the wire contract replaces the whole row and a
 *    submission that omitted it would clear the flag as a side effect of saving an unrelated field.
 *
 * MIGRATION: EMPTY TEXT BECOMES `null`, AND THAT REPRODUCES THE LEGACY OUTCOME RATHER THAN DEPARTING FROM
 *   IT. The legacy null contract encoded absent text AS the empty string, and the legacy screen posted an
 *   empty text box as an empty value that cleared the column. The wire members here are nullable, so the
 *   empty string is translated to `null`, which clears the same column. Numbers are NOT translated: zero
 *   is a legitimate cache time and a legitimate module order, so neither is ever read as absence.
 *
 * @param state The form's raw value.
 * @param context The two required values the form does not hold.
 * @returns A request carrying exactly the sixteen members the server accepts.
 */
export function toUpdateModuleRequest(
  state: ModuleSettingsFormState,
  context: ModuleSettingsRequestContext,
): UpdateModuleRequest {
  return {
    tabId: state.tabId,
    moduleTitle: textOrNull(state.moduleTitle),
    allTabs: state.allTabs,
    header: textOrNull(state.header),
    footer: textOrNull(state.footer),
    startDate: textOrNull(state.startDate),
    endDate: textOrNull(state.endDate),
    inheritViewPermissions: state.inheritViewPermissions,
    isDeleted: context.isDeleted,
    moduleOrder: state.moduleOrder,
    cacheTime: state.cacheTime,
    iconFile: textOrNull(state.iconFile),
    visibility: state.visibility,
    displayTitle: state.displayTitle,
    setAsDefaultSettings: state.setAsDefaultSettings,
    applyToAllModules: state.applyToAllModules,
  };
}

/**
 * Reads a form text value, treating blank input as absence.
 *
 * Trimmed before the emptiness test so that a value of spaces alone is absence rather than whitespace
 * that survives to the column, and the trimmed value is what is returned so no stray padding is stored.
 *
 * @param value A form control's string value.
 * @returns The trimmed text, or `null` when nothing was entered.
 */
function textOrNull(value: string): string | null {
  const trimmed = value.trim();

  return trimmed.length > 0 ? trimmed : null;
}
