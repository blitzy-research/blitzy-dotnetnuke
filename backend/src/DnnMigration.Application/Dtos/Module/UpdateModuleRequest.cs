using System.Text.Json.Serialization;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The state submitted to <c>PUT /api/v1/modules/{moduleId}</c> to revise a module and the placement it is
/// addressed through. A boundary contract and nothing more: no navigation property, no tracked state, no
/// behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// THE SIBLING SETTINGS ENDPOINT IS DELIBERATELY DIFFERENT AND MUST NOT BE HARMONISED WITH THIS ONE. <c>PUT
/// /api/v1/modules/{moduleId}/settings</c> carries <see cref="ModuleSettingsDto"/> and is an
/// UPSERT-PER-KEY, because the two legacy settings writers were measured to be plain upserts with no
/// removal branch at all. This endpoint replaces; that one merges.
/// </remarks>
public sealed class UpdateModuleRequest
{
    // --- Placement target: this SELECTS the placement being updated ---

    /// <summary>The page this placement should occupy. Required.</summary>
    // 5.12 - THE LEGACY UPDATE COULD SKIP THE PLACEMENT WRITE ENTIRELY, AND THE TARGET CANNOT. The legacy
    // path guarded the WHOLE second half - the placement write, the ordering fix-up, the default-module
    // keys and the propagation loop - behind a test that the page identifier was not the integer sentinel.
    [JsonRequired]
    public int TabId { get; set; }

    /// <summary>
    /// The page this placement should be moved ONTO, when the caller is relocating it. <see
    /// langword="null"/>, or the same value as <see cref="TabId"/>, means "do not move".
    /// </summary>
    /// <remarks>
    /// The move itself reproduces <c>ModuleController.MoveModule</c>, which was a COPY followed by a DELETE
    /// rather than a page reassignment - <c>CopyModule(..., includeSettings:=True)</c> then
    /// <c>DeleteTabModule(fromTabId, moduleId)</c>.
    /// </remarks>
    public int? MoveToTabId { get; set; }

    // --- Module scope: identical on every page the module appears on ---

    /// <summary>The heading for the module, or <see langword="null"/> to leave it unset. NOT required.</summary>
    /// <remarks>
    /// The 256-character bound is the column width and is the ONLY bound: the legacy text box declared a
    /// rendered width but no maximum length, so the schema is the sole authority. The legacy help text
    /// reads: "Enter a title for the Module.
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// Whether the module appears in the same position on every page of the portal, which produces one
    /// placement row per page. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/> because the column is non-nullable with a default constraint
    /// of 0, which coincides with the default of this member's type - so no initialiser is needed and none
    /// is present. The legacy label read "Display Module On All Pages?", helped by "Select whether the
    /// module should appear in the same location on all pages of the site".
    /// </remarks>
    public bool AllTabs { get; set; }

    /// <summary>
    /// Text or markup rendered above the module's content, or <see langword="null"/> for none. NOT
    /// required, and no length bound applies.
    /// </summary>
    public string? Header { get; set; }

    /// <summary>
    /// Text or markup rendered below the module's content, or <see langword="null"/> for none. NOT
    /// required, and no length bound applies.
    /// </summary>
    /// <remarks>
    /// Identical in kind to <see cref="Header"/>: an unbounded national text column, a six-row multi-line
    /// control, no maximum length, no validator, and the empty string rather than an absent value as the
    /// legacy representation of "not set". The legacy help text reads "Enter footer text for this Module".
    /// </remarks>
    public string? Footer { get; set; }

    /// <summary>
    /// The date from which the module is displayed, or <see langword="null"/> for no start restriction. NOT
    /// required; when supplied it need only be a parseable date.
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date until which the module is displayed, or <see langword="null"/> for no end restriction. NOT
    /// required; when supplied it need only be a parseable date.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Whether the module takes its <b>View</b> permission from its page instead of carrying its own.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// This is a FLAG ONLY. The permission entries themselves are not on this contract: they live behind
    /// the permission evaluator and the read-only permission catalogue, and the legacy screen posted them
    /// through a separate permissions grid whose omission is a real narrowing recorded in the class
    /// remarks.
    /// </remarks>
    // THE TERMINAL COLUMN IS NULLABLE WITH NO DEFAULT CONSTRAINT, SO THE DEFAULT HERE IS FALSE - AND THE
    // UPGRADE SCRIPT CONTAINS A TRAP THAT SUGGESTS OTHERWISE. Measured with a case-insensitive sweep across
    // all four object-naming forms: the column was added as a NULLABLE bit with NO default constraint.
    public bool InheritViewPermissions { get; set; }

    /// <summary>Whether the module is in the recycle bin. Defaults to <see langword="false"/>.</summary>
    /// <remarks>
    /// The soft-delete marker. The column is non-nullable with a default constraint of 0, which coincides
    /// with the default of this member's type, so no initialiser is needed and none is present.
    /// </remarks>
    // MIGRATION: 5.6 - THE LEGACY SETTINGS SCREEN COULD ONLY EVER CLEAR THIS FLAG, SO ACCEPTING IT HERE IS
    // A DELIBERATE WIDENING - AND A DIVERGENCE IN BOTH DIRECTIONS. Measured: the legacy save contains the
    // bare, unconditional assignment of False, and the field was not on the form at all, so EVERY SAVE
    // SILENTLY UN-DELETED THE MODULE. That is annotated as the primary legacy defect for this endpoint and
    // is deliberately NOT reproduced.
    public bool IsDeleted { get; set; }

    // --- Placement scope: specific to this occurrence on this page ---

    /// <summary>
    /// The module's position within its pane on the page. Defaults to -1, which appends it at the bottom of
    /// the pane.
    /// </summary>
    /// <remarks>
    /// The instruction is consumed by the application service, which resolves it against the placement's
    /// own pane before anything is written, so -1 never reaches the column. When the request also asks to
    /// place the module on every page, each new placement is appended to its own page's pane rather than
    /// taking the position computed for the addressed page.
    /// </remarks>
    // 5.1 - -1 IS A LOAD-BEARING COMMAND MEANING "APPEND AT THE BOTTOM OF THE PANE", NOT AN ABSENT VALUE,
    // AND FOUR INDEPENDENT PROOFS SAY SO. The legacy create branched on the value being -1 under the
    // comment "position module at bottom of pane", taking a different ordering path from any other value;
    // the ordering routine's own documentation described its parameter as "position within the controls
    // list on page, -1 if to be added at the end"; that routine implements the instruction by reading the
    // highest existing order and stepping past it; and the move routine passes the literal -1 as the
    // destination order, so a moved module lands at the bottom of its new pane.
    public int ModuleOrder { get; set; } = -1;

    /// <summary>
    /// How long the module's output may be cached, in seconds. Defaults to 0, which means no caching.
    /// </summary>
    /// <remarks>
    /// The integer rule is the third and last of the legacy rules that survive into this contract. The
    /// legacy label read "Cache Time (secs):", helped by "Enter the time this object is kept in the Cache",
    /// and its input declared a maximum length of 6, which bounded the RENDERED TEXT rather than the
    /// column, so that number is not a rule on this member.
    /// </remarks>
    // 5.5 - A BLANK LEGACY FIELD STORED ZERO, NOT A SENTINEL. The legacy save read the box and stored a
    // parsed integer when it was non-empty and LITERALLY ZERO when it was empty.
    public int CacheTime { get; set; }

    /// <summary>
    /// The icon displayed with the module's title, or <see langword="null"/> for none. NOT required.
    /// </summary>
    public string? IconFile { get; set; }

    /// <summary>
    /// How the placement is aligned within its pane - <c>left</c>, <c>center</c>, <c>right</c>, or the empty
    /// string for "Not Specified". NOT required.
    /// </summary>
    /// <remarks>
    /// The three spellings are the legacy radio list's own values (<c>modulesettings.ascx:L122-L127</c>) and
    /// are what the column already contains, so they are accepted verbatim. An unrecognised value is
    /// refused by the validator rather than silently normalised, because a value the legacy container
    /// rendering cannot interpret is stored appearance nobody can see or correct.
    /// </remarks>
    public string? Alignment { get; set; }

    /// <summary>The container background colour, or <see langword="null"/> for none. NOT required.</summary>
    /// <remarks>
    /// Bounded at the column's twenty characters and otherwise unparsed: the legacy field had no validator,
    /// and narrowing it here would reject values an installation already holds.
    /// </remarks>
    public string? Color { get; set; }

    /// <summary>The container border width, a single digit, or <see langword="null"/> for none.</summary>
    /// <remarks>
    /// The legacy box was one character wide with an integer validator reading "must be a number between 0
    /// and 9", which is the rule reproduced by the validator on this member.
    /// </remarks>
    public string? Border { get; set; }

    /// <summary>
    /// How the module is presented on the page. Defaults to <see cref="ModuleVisibility.Maximized"/>.
    /// </summary>
    // 5.3 - THE LEGACY READER COLLAPSED THREE DISTINCT INPUTS INTO ONE STATE AND HAD NO DEFAULT BRANCH. Its
    // selection block mapped both 0 AND the integer sentinel -1 - and, through the sentinel substitution
    // applied first, a stored null as well - to the maximised state, mapped 1 to minimised and 2 to none,
    // and declared NO fallback branch.
    public ModuleVisibility Visibility { get; set; }

    /// <summary>Whether the module's container is displayed. Defaults to <see langword="true"/>.</summary>
    /// <remarks>
    /// DESPITE THE COLUMN AND MEMBER NAME, THIS CONTROLS THE CONTAINER RATHER THAN THE TITLE ALONE. The
    /// legacy markup labelled it "Display Title?" but the authoritative resource file overrode that label
    /// to "Display Container?", helped by "Select this option if you would like to display the Module
    /// container." The resource wording wins because it is what users actually saw.
    /// </remarks>
    // 5.10 - THE DEFAULT IS TRUE, CONFIRMED TWICE, SO C#'s DEFAULT HAD TO BE CORRECTED. The terminal column
    // is non-nullable with a default constraint of 1, and the legacy constructor independently initialised
    // the backing field to True.
    public bool DisplayTitle { get; set; } = true;

    // --- Behaviour flags: intent, not stored module state ---

    /// <summary>
    /// An instruction rather than module state: name this module and its page as the portal's default
    /// settings for newly added modules. Defaults to <see langword="false"/>.
    /// </summary>
    // THIS FLAG WRITES OUTSIDE THE MODULE AGGREGATE, WHICH IS A GENUINE CROSS-AGGREGATE SIDE EFFECT. When
    // set, the legacy update wrote two PORTAL-level site-settings keys naming the default module and the
    // default page. It therefore changed PORTAL configuration, not the module row.
    public bool SetAsDefaultSettings { get; set; }

    /// <summary>
    /// An instruction rather than module state: copy this placement's appearance to every module on every
    /// non-administrative page of the portal. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ApplyToAllModules { get; set; }
}
