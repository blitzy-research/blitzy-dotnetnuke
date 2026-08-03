using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The state submitted to <c>POST /api/v1/modules</c> to place a module on a page. A boundary
/// contract and nothing more: no navigation property, no tracked state, no behaviour and no domain
/// entity, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// ONE REQUEST WRITES TWO ROWS, AND THE FOURTEEN MEMBERS BELOW ARE DERIVED FROM THAT FACT RATHER
/// THAN CHOSEN. The legacy create was two provider calls, because the legacy object model flattened
/// a four-table join into one class of fifty-eight properties. The first call took TEN arguments -
/// portal, definition, title, all-pages flag, header, footer, start date, end date, inherit-view
/// flag and deleted flag - and the second took FOURTEEN: page, module, order, pane, cache period,
/// alignment, colour, border, icon, visibility, container source, title flag, print flag and
/// syndicate flag. Each reconciles exactly against the terminal schema once its generated identity
/// is added back: ten plus one is the eleven columns of <c>dbo.Modules</c>, and fourteen plus one is
/// the fifteen columns of <c>dbo.TabModules</c>.
/// </para>
/// <para>
/// From those twenty-four arguments a REST create must not accept six. Two come off the first call:
/// the portal, which is resolved per request from the addressed alias rather than from the body, and
/// the deleted flag, which a newly created module cannot meaningfully carry. Eight come off the
/// second: the module identity, which the database generates, and the seven pane-layout, rendering
/// and skinning arguments that this migration excludes. That leaves EIGHT module-scoped members and
/// SIX placement-scoped members - FOURTEEN in total, which is exactly the member count below. The
/// count is therefore defensible rather than arbitrary, and it must not be padded towards the
/// fifty-eight properties of the legacy class.
/// </para>
/// <para>
/// THE PRODUCT ITSELF NAMED THE TWO SCOPES, WHICH IS THE STRONGEST EVIDENCE THAT SPLITTING THEM IS
/// FIDELITY RATHER THAN REDESIGN. The legacy settings screen was divided into two captioned
/// sections whose help text survives verbatim in
/// <c>Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx</c>. "Module Settings"
/// reads: "In this section, you can define the settings that relate to the Module content and
/// permissions (ie. those settings that will be the same on all pages that the Module appears )."
/// That is the module-scope group. "Page Settings" reads: "In this section, you can define settings
/// specific to this particular occurrence of the Module for this Page." That is the placement-scope
/// group. The grouping below reproduces that division, and in the terminal schema
/// <see cref="TabId"/>, <see cref="ModuleOrder"/>, <see cref="CacheTime"/>, <see cref="IconFile"/>,
/// <see cref="Visibility"/> and <see cref="DisplayTitle"/> genuinely live on the placement table
/// rather than on the module table.
/// </para>
/// <para>
/// THE LEGACY SCREEN DECLARED NO REQUIRED-FIELD VALIDATOR AT ALL, SO <see cref="ModuleTitle"/> IS
/// NOT REQUIRED. This is measured, not inferred: <c>Website/admin/Modules/modulesettings.ascx</c>
/// contains zero <c>RequiredFieldValidator</c> declarations and zero <c>ValidationSummary</c>
/// declarations. Its only four validators are <c>CompareValidator</c>s with
/// <c>Operator="DataTypeCheck"</c>: two date-format checks on the start and end dates, an integer
/// check on the border, and an integer check on the cache period. Because the border is excluded
/// from this contract, only THREE of those four rules have any counterpart here, and NONE of the
/// three is a presence check. Adding a non-empty rule to the title would be a tightening that the
/// migration's minimal-change discipline forbids, which requires validation rules to MATCH and error
/// messages to be EQUIVALENT.
/// </para>
/// <para>
/// NO CROSS-FIELD DATE RULE EXISTS IN THE LEGACY SCREEN AND NONE IS SPECIFIED HERE. Both date
/// validators are format checks: neither declares a <c>ControlToCompare</c>, and the file contains
/// no such attribute anywhere. An end date preceding a start date was therefore storable, producing
/// a module that could never be visible. That is recorded as a legacy defect rather than corrected
/// by this contract, because ordering the two dates is a new requirement rather than a ported one.
/// </para>
/// <para>
/// ONLY <see cref="ModuleDefId"/> AND <see cref="TabId"/> ARE REQUIRED, AND THEIR REQUIREMENT IS
/// STRUCTURAL RATHER THAN PORTED. It derives from the two provider argument lists and from the
/// <c>NOT NULL</c> declarations behind them, not from any legacy validator. A presence check on
/// either must be expressed as "this identifier exists", NEVER as a numeric floor: the page
/// identity seeds at 0 and the definition identity seeds at 1, so no single numeric rule can serve
/// both, and rejecting a page identifier of 0 would make the first page of a portal unusable.
/// </para>
/// <para>
/// NEVER ACCEPTED ON THIS CONTRACT: the two generated identities, the portal identifier, the deleted
/// flag, and any acting-user identifier. The identities are generated by the database. The portal is
/// taken from the per-request portal context, so accepting it in the body would be a cross-tenant
/// write vector and would breach the tenant isolation the migration is required to preserve. The
/// deleted flag belongs to the update contract. The acting user comes from the authenticated
/// principal's claims; the two tables carry no audit columns at all, and the legacy code likewise
/// took the acting user from ambient state rather than from the submitted form.
/// </para>
/// <para>
/// ALSO ELSEWHERE, NOT HERE: permission entries live behind the permission evaluator and the
/// read-only permission catalogue, of which only the <see cref="InheritViewPermissions"/> FLAG is
/// retained; the two key-value setting stores are a separate request against the module's settings
/// sub-resource; and the two update-only intent flags - "make this the portal default" and "apply to
/// every module" - belong to the update contract, because a create has no prior state to propagate
/// from. Definition and desktop-module metadata is selected by <see cref="ModuleDefId"/> and never
/// posted per instance, which the legacy screen confirmed by rendering the definition's friendly
/// name as a disabled field.
/// </para>
/// <para>
/// Declarative validation lives in <c>Application/Validation/CreateModuleRequestValidator.cs</c>,
/// never as attributes on this type, and stateful checks - whether the definition exists and is
/// available to the portal, whether the page belongs to the portal - live in
/// <c>Application/Services/ModuleService.cs</c>. The wire casing of every member below is decided by
/// one central serialisation policy at the API edge, so this type carries no serialisation
/// attribute.
/// </para>
/// </remarks>
// MIGRATION: 5.2 - THE FLATTENED LEGACY CLASS IS SPLIT ALONG THE REAL TABLE BOUNDARIES, WHICH IS WHY
//   ONE CREATE WRITES TWO ROWS. The legacy ModuleInfo was a single class of fifty-eight properties
//   standing for a Modules-to-TabModules-to-ModuleDefinitions-to-ModuleControls join, so a consumer
//   holding a value could not tell which table it came from. The upgrade chain proves the boundary
//   rather than merely suggesting it: one statement drops ModuleOrder, PaneName, CacheTime,
//   Alignment, Color, Border, IconFile, Personalize, ShowTitle and ContainerSrc from Modules, and a
//   second drops TabID from it as well, relocating all of them to the placement table. Only the
//   cumulative terminal state of all eighty-eight upgrade scripts is meaningful, because the chain
//   is destructive; deriving a column set from the baseline script alone would be wrong. The two
//   groups below therefore write two rows, committed as one unit of work so that a module can never
//   exist without the placement it was requested with.
//
// MIGRATION: 5.6 - THE DELETED FLAG IS NOT ON THIS CONTRACT. The column is NOT NULL with a default
//   of 0, and the legacy save could only ever clear it: the code-behind contained the bare
//   assignment of False with no path that set it. A newly created module is never deleted, so
//   accepting the flag here would offer a state the create cannot legitimately express. It appears
//   on the update contract instead.
//
// MIGRATION: 5.11 - EXCLUSION SUMMARY, EACH ITEM MEASURED. (a) Both generated identities are absent;
//   the legacy "is this new?" test was a comparison against the integer sentinel -1, which was never
//   expressible as a non-positive check because 0 is a real module identity. Excluding the
//   identities dissolves that hazard entirely. (b) The portal identifier is absent - a cross-tenant
//   write vector - and note that the portal identity seeds at -1, so -1 is a real portal rather than
//   an absent one. (c) The deleted flag is deferred to update, per 5.6. (d) Seven pane-layout and
//   rendering arguments are excluded - pane, alignment, colour, border, print flag and syndicate
//   flag, together with the container source. These are REAL terminal placement columns that the
//   legacy screen genuinely edited through real controls, so this is a real functional narrowing and
//   not a tidy-up: dropping the border is precisely why only three of the screen's four validators
//   survive, the fourth having been an integer check whose message read "Invalid Border (must be a
//   number between 0 and 9)". The pane is the sharpest case, because its column is NOT NULL: the
//   write path supplies the conventional content pane rather than letting the caller choose, which
//   is consistent with the legacy value having come from the skin's pane picker - a skinning concern
//   this migration excludes - rather than from a field a user could edit freely. (e) The
//   business-controller class name is excluded: it named a type that the legacy code activated by
//   reflection, and accepting a type name on a create request would be arbitrary remote activation.
//   A dependency-injected factory over a closed set replaces it. (f) Permission rows are excluded
//   even though the legacy screen posted them through a permissions grid, so this too is a real
//   narrowing; only the inherit-view FLAG is retained. (g) The two key-value setting stores are
//   excluded, and with them the legacy panel that dynamically loaded a per-definition settings
//   control and called back into it on save. (h) The two update-only intent flags are excluded. (i)
//   Control metadata is excluded, which dissolves a genuine hazard: the legacy control-type reader
//   mapped -3, -2 and -1 to real access levels while -1 was simultaneously the integer sentinel for
//   an absent value. (j) The token-replacement interface and its cache-policy member are dropped
//   with that excluded subsystem; the member named for cacheability there is unrelated to the
//   genuine cache-period column carried at CacheTime.
//
// MIGRATION: 5.12 - THE LEGACY SCREENS COMPILED WITH OPTION STRICT OFF, SO CONVERSION BEHAVIOUR AT
//   THIS BOUNDARY HAS DELIBERATELY CHANGED. The legacy web configuration sets strict="false", which
//   permitted implicit narrowing and late binding that C# rejects outright. The screen consequently
//   parsed a date with no culture argument and an integer with no try-parse guard, and converted an
//   integer to a string implicitly when preselecting the page picker. Here the boundary is typed
//   JSON, so those coercions move into deserialisation and the validator: an unparseable date or
//   integer now yields an RFC 7807 validation problem response at the API edge rather than a
//   client-side format message or a server-side exception. That is a documented divergence in
//   failure MODE, not in which values are accepted.
//
// MIGRATION: 5.13 - THE TWO LEGACY HYDRATION PATHS ARE DELETED RATHER THAN TRANSLATED. Rows were
//   materialised either by a reflection-driven object hydrator or by a hand-rolled reader block that
//   assigned each column through a sentinel-substituting conversion, one line per column, and the
//   collection forms returned an untyped list and a non-generic dictionary. The object-relational
//   mapper's own materialiser replaces both, so no fill member, no hydration interface and no
//   collection wrapper exists anywhere in the target - and none appears on this contract.
//
// MIGRATION: 5.14 - THE LEGACY CREATE SWALLOWED A DUPLICATE PLACEMENT, AND THE TARGET DOES NOT. The
//   second provider call sat inside a try/catch whose only handler body was the comment "module
//   already in the page, ignore error", so requesting a module on a page that already had it
//   silently reported success while writing nothing. The target surfaces a real outcome instead - a
//   failure reason from the module service, translated into an RFC 7807 response - which is a
//   deliberate behaviour change from silent success and is recorded as such rather than reproduced.
public sealed class CreateModuleRequest
{
    // --- Required placement and definition ---

    /// <summary>
    /// The module definition to instantiate. Required.
    /// </summary>
    /// <remarks>
    /// Callers resolve the permitted set from <c>GET /api/v1/module-definitions</c>. The identity
    /// column behind this value seeds at 1, so a real definition identifier is positive - but the
    /// check belongs to the service and must be expressed as "this definition exists and is
    /// available to the portal", never as a bare numeric floor, because the sibling identifier on
    /// this same contract seeds at 0. Everything else about the definition - its friendly name,
    /// module name, description, version, folder, default cache period and premium or admin status -
    /// is selected BY this value and is never posted alongside it. The legacy screen made the point
    /// itself by rendering the definition's friendly name as a disabled field under the label
    /// "Module:", helped by the text "Displays the name of the module."
    /// </remarks>
    // MIGRATION: 5.9 - REQUIRED STRUCTURALLY, NOT BY A PORTED VALIDATOR. The legacy screen declared
    //   no required-field validator for this or for any other field. The requirement here comes from
    //   the first provider call's argument list and from the NOT NULL column behind it, which is an
    //   honest divergence and is recorded rather than presented as a ported rule.
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The page on which to place the module. Required.
    /// </summary>
    /// <remarks>
    /// This is the placement key, and in the terminal schema it lives on the placement table only:
    /// the module table's own page column was dropped by the upgrade chain. Callers resolve the
    /// permitted set from the portal's page list, which is the same lookup the legacy screen's page
    /// picker performed when it bound a drop-down to page names and page identifiers. The
    /// user-facing word for this concept is "Page", not "Tab" - the legacy label read "Move To
    /// Page:", helped by "Move this module instance to another Page."
    /// </remarks>
    // MIGRATION: 5.9 - REQUIRED STRUCTURALLY, AND THE SEED TRAP HERE IS THE SHARPEST ON THIS
    //   CONTRACT. The page identity seeds at 0, so 0 IS A LEGITIMATE PAGE and a positive-only rule
    //   would reject the first page of every portal. Separately, -1 was an "any page" WILDCARD in
    //   the legacy query surface - the all-pages lookup was the single-page lookup passed the
    //   integer sentinel - so -1 there meant "match every page" rather than "no page". Neither value
    //   may ever be read as absence.
    public int TabId { get; set; }

    // --- Module scope: identical on every page the module appears on ---

    /// <summary>
    /// The heading for the module, or <see langword="null"/> to leave it unset. NOT required. At most
    /// 256 characters.
    /// </summary>
    /// <remarks>
    /// The 256-character bound is the column width and is the ONLY bound: the legacy text box
    /// declared a rendered width but no maximum length, so the schema is the sole authority. The
    /// legacy help text reads: "Enter a title for the Module.  This will appear in the Title Bar of
    /// the Container for this Module, if supported by the Container." A missing value surfaced from
    /// the legacy layer as the EMPTY STRING rather than as an absent one, because the legacy string
    /// sentinel was literally the empty string; the projection layer owns that translation, so a
    /// caller sending <see langword="null"/> and a caller sending an empty string must both end up
    /// storing what the legacy blank text box stored.
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// Whether the module appears in the same position on every page of the portal, which produces
    /// one placement row per page. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/> because the column is <c>NOT NULL</c> with a default
    /// of 0, which coincides with the default of this member's type - so no initialiser is needed
    /// and none is present. The legacy label read "Display Module On All Pages?", helped by "Select
    /// whether the module should appear in the same location on all pages of the site".
    /// </remarks>
    // MIGRATION: 5.8 - THIS FIELD WAS ADMINISTRATOR-ONLY IN THE LEGACY SCREEN, AND THAT IS ENFORCED
    //   ON THE WRITE PATH RATHER THAN BY THIS MEMBER. The legacy page load disabled this checkbox,
    //   the two behaviour flags and the page picker outright for anyone not in the administrator
    //   role. The target expresses that with policy-based authorisation at the API layer: the
    //   contract still ACCEPTS the value, and authorisation decides whether the write may proceed.
    //   It is deliberately NOT a validator rule and deliberately NOT a second member, because a
    //   permission is not a property of the submitted state.
    public bool AllTabs { get; set; }

    /// <summary>
    /// Text or markup rendered above the module's content, or <see langword="null"/> for none. NOT
    /// required, and no length bound applies.
    /// </summary>
    /// <remarks>
    /// The column is an unbounded national text type, and the legacy control was a six-row
    /// multi-line text box with no maximum length and no validator, so there is no length rule to
    /// port. The legacy help text reads "Enter header text for this Module". A missing value
    /// surfaced from the legacy layer as the empty string rather than as an absent one.
    /// </remarks>
    public string? Header { get; set; }

    /// <summary>
    /// Text or markup rendered below the module's content, or <see langword="null"/> for none. NOT
    /// required, and no length bound applies.
    /// </summary>
    /// <remarks>
    /// Identical in kind to <see cref="Header"/>: an unbounded national text column, a six-row
    /// multi-line control, no maximum length, no validator, and the empty string rather than an
    /// absent value as the legacy representation of "not set". The legacy help text reads "Enter
    /// footer text for this Module".
    /// </remarks>
    public string? Footer { get; set; }

    /// <summary>
    /// The date from which the module is displayed, or <see langword="null"/> for no start
    /// restriction. NOT required; when supplied it need only be a parseable date.
    /// </summary>
    /// <remarks>
    /// The parseable-date rule is one of the three legacy rules that survive into this contract, and
    /// it is the whole of what the legacy screen checked for this field. The legacy input declared a
    /// maximum length of 11, which bounded the RENDERED TEXT rather than the column - the column is
    /// a nullable date and time - so that number is not a rule on this member. The legacy help text
    /// reads: "Enter the start date for displaying this module.  You may use the Calendar to pick a
    /// date." This value GATES WHETHER THE MODULE RENDERS AT ALL, which is why its translation is
    /// spelled out rather than left to a default.
    /// </remarks>
    // MIGRATION: 5.4 - THE LEGACY ABSENT DATE WAS A SENTINEL, NOT SQL NULL, AND THIS WAS PROVEN IN
    //   BOTH DIRECTIONS. Reading, the legacy screen filled the box only when the value was not the
    //   sentinel, so the minimum date value rendered as an EMPTY BOX. Writing, a blank box stored
    //   the sentinel - the minimum date value - rather than a null. A nullable date is the correct
    //   wire shape, and the projection layer in ModuleMappings owns the explicit translation from
    //   an absent value to the legacy sentinel on the way in. The translation must stay explicit
    //   precisely because these dates decide visibility: silently storing a null where the legacy
    //   stored the minimum date, or vice versa, would change whether a module is ever shown. The
    //   same applies to EndDate, and note that NO ordering rule between the two exists to port.
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date until which the module is displayed, or <see langword="null"/> for no end
    /// restriction. NOT required; when supplied it need only be a parseable date.
    /// </summary>
    /// <remarks>
    /// The same rule, the same sentinel and the same translation as <see cref="StartDate"/> - see
    /// the annotation there. THERE IS NO RULE REQUIRING THIS DATE TO FALL ON OR AFTER
    /// <see cref="StartDate"/>: measured, the legacy screen's two date validators are independent
    /// format checks, neither naming a control to compare against, and the markup contains no such
    /// attribute at all. An end date before a start date was therefore storable and produced a
    /// module that could never be visible. That is a legacy defect, recorded and deliberately not
    /// corrected by this contract; introducing an ordering rule would be a new requirement rather
    /// than a ported one. The legacy help text reads: "Enter the end date for displaying this
    /// module.  You may use the Calendar to pick a date."
    /// </remarks>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Whether the module takes its <b>View</b> permission from its page instead of carrying its
    /// own. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// This is a FLAG ONLY. The permission entries themselves are not on this contract: they live
    /// behind the permission evaluator and the read-only permission catalogue, and the legacy screen
    /// posted them through a separate permissions grid whose omission is a real narrowing recorded
    /// in the class remarks. The legacy caption read: "Inherit &lt;b&gt;View&lt;/b&gt; permissions
    /// from &lt;b&gt;Page&lt;/b&gt;". In the legacy screen the checkbox posted back immediately in
    /// order to enable or disable that grid, which is presentation behaviour with no counterpart
    /// here.
    /// </remarks>
    // MIGRATION: THE TERMINAL COLUMN IS NULLABLE WITH NO DEFAULT CONSTRAINT, SO THE DEFAULT HERE IS
    //   FALSE - AND THE UPGRADE SCRIPT CONTAINS A TRAP THAT SUGGESTS OTHERWISE. Measured with a
    //   case-insensitive sweep across all four object-naming forms: the column was added as a
    //   NULLABLE bit with NO default constraint. Immediately after adding it, the same script runs a
    //   ONE-TIME BACK-FILL setting it to 1 for rows that had no explicit view roles. That back-fill
    //   applies to PRE-EXISTING ROWS ONLY and is not a default for new inserts, so it must not be
    //   mistaken for one; this member therefore relies on the default of its own type, false, and
    //   carries no initialiser. The domain entity models the column faithfully as a nullable
    //   boolean and its read projection substitutes false for an absent value, which corroborates
    //   the choice. A create always states the flag, so the nullability is not surfaced on the wire.
    public bool InheritViewPermissions { get; set; }

    // --- Placement scope: specific to this occurrence on this page ---

    /// <summary>
    /// The module's position within its pane on the page. Defaults to -1, which appends it at the
    /// bottom of the pane.
    /// </summary>
    /// <remarks>
    /// OMITTING THIS MEMBER APPENDS; SENDING 0 EXPLICITLY MEANS POSITION ZERO. That distinction is
    /// the entire reason for the initialiser, and deserialisation preserves it: an absent property
    /// leaves the initialised value intact, while a present one overwrites it. No validation rule
    /// may reject -1, and the legacy screen had no rule on this field at all.
    /// </remarks>
    // MIGRATION: 5.1 - -1 IS A LOAD-BEARING COMMAND MEANING "APPEND AT THE BOTTOM OF THE PANE", NOT
    //   AN ABSENT VALUE, AND TWO INDEPENDENT PROOFS SAY SO. The legacy create branched on the value
    //   being -1 under the comment "position module at bottom of pane", taking a different ordering
    //   path from any other value; and the ordering routine's own documentation described its
    //   parameter as "position within the controls list on page, -1 if to be added at the end". The
    //   collision is what makes this dangerous: the legacy integer sentinel for an absent value is
    //   ALSO -1, so mapping this member to a nullable integer would silently convert an instruction
    //   into an absence and change where every module lands. It therefore stays a non-nullable
    //   integer, keeps its -1 initialiser, is never mapped to null, and is never validated away.
    public int ModuleOrder { get; set; } = -1;

    /// <summary>
    /// How long the module's output may be cached, in seconds. Defaults to 0, which means no
    /// caching. When supplied it need only be an integer.
    /// </summary>
    /// <remarks>
    /// The integer rule is the third and last of the legacy rules that survive into this contract.
    /// The legacy label read "Cache Time (secs):" and its input declared a maximum length of 6,
    /// which bounded the RENDERED TEXT rather than the column, so that number is not a rule on this
    /// member. Zero is a real, meaningful value and not an absence, so the default of this member's
    /// type is already correct and no initialiser is present.
    /// </remarks>
    // MIGRATION: 5.5 - A BLANK LEGACY FIELD STORED ZERO, NOT A SENTINEL. The legacy save read the
    //   box and stored a parsed integer when it was non-empty and LITERALLY ZERO when it was empty.
    //   Zero therefore means "do not cache" and is indistinguishable from a deliberate choice of
    //   zero, which is precisely why this member is a non-nullable integer: making it nullable would
    //   invent an "unspecified" state the legacy contract never had, and treating zero as unset
    //   would silently enable caching on a module the caller asked not to cache.
    //
    // MIGRATION: 5.7 - A DEFINITION'S DEFAULT CACHE PERIOD OF -1 MEANT "CACHING NOT APPLICABLE" AND
    //   HID THIS FIELD ENTIRELY, WHICH IS DEFINITION METADATA RATHER THAN A RULE HERE. The legacy
    //   screen hid the whole cache row when the definition's default cache period equalled the
    //   integer sentinel, and that column is NOT NULL with a default of 0, so -1 was stored
    //   EXPLICITLY to carry that meaning. A client wishing to hide this field consults the
    //   definition contract; nothing about it constrains the value submitted here, and it is
    //   deliberately not a server-side fallback for an omitted value.
    public int CacheTime { get; set; }

    /// <summary>
    /// The icon displayed with the module's title, or <see langword="null"/> for none. NOT required.
    /// At most 100 characters.
    /// </summary>
    /// <remarks>
    /// The 100-character bound is the column width, and the legacy control declared no validator.
    /// The legacy help text reads "Select an Icon for this Module to display in the Title Bar". This
    /// is a STORED NAME OR PATH STRING ONLY: file-system handling is outside the migration's scope,
    /// so this contract performs no upload, resolves no location and promises no reachable address.
    /// A missing value surfaced from the legacy layer as the empty string rather than as an absent
    /// one.
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// How the module is presented on the page. Defaults to
    /// <see cref="ModuleVisibility.Maximized"/>.
    /// </summary>
    /// <remarks>
    /// THE DEFAULT RELIES ON A COINCIDENCE THAT MUST NOT BE DISTURBED: the legacy default was the
    /// maximised state, and that member is numbered 0, which is also the default of the enumeration
    /// type - so no initialiser is needed and none is present. Renumbering the enumeration would
    /// silently change this default. The legacy control was a radio-button list whose three items
    /// carried the values 0, 1 and 2, and the legacy screen used the stored number directly as the
    /// list index. A validation rule must accept exactly the three defined members and nothing else.
    /// The legacy label read "Visibility:", helped by "Choose the default visibility for this
    /// Module".
    /// </remarks>
    // MIGRATION: 5.3 - THE LEGACY READER COLLAPSED THREE DISTINCT INPUTS INTO ONE STATE AND HAD NO
    //   DEFAULT BRANCH. Its selection block mapped both 0 AND the integer sentinel -1 - and, through
    //   the sentinel substitution applied first, a stored null as well - to the maximised state,
    //   mapped 1 to minimised and 2 to none, and declared NO fallback branch. A stored value outside
    //   that set therefore silently produced the maximised state on read and left the field
    //   unchanged on save. Both are annotated as legacy defects and deliberately NOT fixed here.
    //   Note also that the "none" member is a REAL display state meaning "not rendered": it is not
    //   an unset or absent marker, and no such member exists on the enumeration.
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Whether the module's container is displayed. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// DESPITE THE COLUMN AND MEMBER NAME, THIS CONTROLS THE CONTAINER RATHER THAN THE TITLE ALONE.
    /// The legacy markup labelled it "Display Title?" but the authoritative resource file overrode
    /// that label to "Display Container?", helped by "Select this option if you would like to
    /// display the Module container." The resource wording wins because it is what users actually
    /// saw. The name is preserved because it is the terminal column name.
    /// </remarks>
    // MIGRATION: 5.10 - THE DEFAULT IS TRUE, CONFIRMED TWICE, SO C#'s DEFAULT HAD TO BE CORRECTED.
    //   The terminal column is NOT NULL with a default constraint of 1, and the legacy constructor
    //   independently initialised the backing field to True. The default of this member's type is
    //   false, which would HIDE the container of every module created by a caller that omitted the
    //   property - a silent behaviour change. Hence the explicit true initialiser, which
    //   deserialisation leaves intact when the property is absent and overwrites when it is present.
    public bool DisplayTitle { get; set; } = true;
}
