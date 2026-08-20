using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The state submitted to <c>POST /api/v1/modules</c> to place a module on a page. A boundary contract and
/// nothing more: no navigation property, no tracked state, no behaviour and no domain entity, in either
/// direction.
/// </summary>
/// <remarks>
/// ONE REQUEST WRITES TWO ROWS, AND THE FOURTEEN MEMBERS BELOW ARE DERIVED FROM THAT FACT RATHER THAN
/// CHOSEN. The legacy create was two provider calls, because the legacy object model flattened a four-table
/// join into one class of fifty-eight properties.
/// </remarks>
// 5.6 - THE DELETED FLAG IS NOT ON THIS CONTRACT. The column is NOT NULL with a default of 0, and the
// legacy save could only ever clear it: the code-behind contained the bare assignment of False with no path
// that set it.
public sealed class CreateModuleRequest
{
    // --- Required placement and definition ---

    /// <summary>The module definition to instantiate. Required.</summary>
    // MIGRATION: 5.9 - REQUIRED STRUCTURALLY, NOT BY A PORTED VALIDATOR. The legacy screen declared no
    // required-field validator for this or for any other field.
    public int ModuleDefId { get; set; }

    /// <summary>The page on which to place the module. Required.</summary>
    // 5.9 - REQUIRED STRUCTURALLY, AND THE SEED TRAP HERE IS THE SHARPEST ON THIS CONTRACT. The page
    // identity seeds at 0, so 0 IS A LEGITIMATE PAGE and a positive-only rule would reject the first page
    // of every portal.
    public int TabId { get; set; }

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
    /// The default is <see langword="false"/> because the column is <c>NOT NULL</c> with a default of 0,
    /// which coincides with the default of this member's type - so no initialiser is needed and none is
    /// present. The legacy label read "Display Module On All Pages?", helped by "Select whether the module
    /// should appear in the same location on all pages of the site".
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

    // --- Placement scope: specific to this occurrence on this page ---

    /// <summary>
    /// The module's position within its pane on the page. Defaults to -1, which appends it at the bottom of
    /// the pane.
    /// </summary>
    /// <remarks>
    /// The instruction is consumed by the application service, which resolves it against the target pane
    /// before anything is written, so -1 never reaches the column. It is a value on this contract and an
    /// instruction to that service; it is not stored state, and a read of the created module reports the
    /// position it actually landed at.
    /// </remarks>
    // 5.1 - -1 IS A LOAD-BEARING COMMAND MEANING "APPEND AT THE BOTTOM OF THE PANE", NOT AN ABSENT VALUE,
    // AND TWO INDEPENDENT PROOFS SAY SO. The legacy create branched on the value being -1 under the comment
    // "position module at bottom of pane", taking a different ordering path from any other value; and the
    // ordering routine's own documentation described its parameter as "position within the controls list on
    // page, -1 if to be added at the end".
    public int ModuleOrder { get; set; } = -1;

    /// <summary>
    /// How long the module's output may be cached, in seconds. Defaults to 0, which means no caching.
    /// </summary>
    /// <remarks>
    /// The integer rule is the third and last of the legacy rules that survive into this contract. The
    /// legacy label read "Cache Time (secs):" and its input declared a maximum length of 6, which bounded
    /// the RENDERED TEXT rather than the column, so that number is not a rule on this member.
    /// </remarks>
    // 5.5 - A BLANK LEGACY FIELD STORED ZERO, NOT A SENTINEL. The legacy save read the box and stored a
    // parsed integer when it was non-empty and LITERALLY ZERO when it was empty.
    public int CacheTime { get; set; }

    /// <summary>
    /// The icon displayed with the module's title, or <see langword="null"/> for none. NOT required.
    /// </summary>
    public string? IconFile { get; set; }

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
    // is NOT NULL with a default constraint of 1, and the legacy constructor independently initialised the
    // backing field to True.
    public bool DisplayTitle { get; set; } = true;
}
