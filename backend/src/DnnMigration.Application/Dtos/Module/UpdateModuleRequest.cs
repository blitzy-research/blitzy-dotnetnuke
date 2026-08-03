using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The state submitted to <c>PUT /api/v1/portals/{portalId}/modules/{moduleId}</c> to revise a module and the placement it
/// is addressed through. A boundary contract and nothing more: no navigation property, no tracked
/// state, no behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS A FULL REPLACEMENT, NOT A PATCH, AND THE LEGACY SCREEN IS WHAT MAKES THAT FAITHFUL. The
/// legacy save assigned TWENTY-THREE properties unconditionally on every use, and the form offered
/// no "leave unchanged" affordance for any field, so replacing the editable subset wholesale
/// reproduces the original behaviour rather than departing from it. Two consequences follow for
/// callers and must not be discovered the hard way. First, OMITTING A PROPERTY DOES NOT PRESERVE
/// ITS CURRENT VALUE: it takes the default of its type, or the initialiser where one is stated
/// below. Second, and most sharply, OMITTING <see cref="ModuleOrder"/> APPENDS THE MODULE TO THE
/// BOTTOM OF ITS PANE rather than holding its present position, because the legacy default for that
/// field is an instruction rather than an absent value.
/// </para>
/// <para>
/// THE SIBLING SETTINGS ENDPOINT IS DELIBERATELY DIFFERENT AND MUST NOT BE HARMONISED WITH THIS
/// ONE. <c>PUT /api/v1/portals/{portalId}/modules/{moduleId}/settings</c> carries <see cref="ModuleSettingsDto"/> and is an
/// UPSERT-PER-KEY, because the two legacy settings writers were measured to be plain upserts with
/// no removal branch at all. This endpoint replaces; that one merges. The divergence is
/// intentional, and anyone tempted to align them should change neither.
/// </para>
/// <para>
/// ONE REQUEST WRITES TWO ROWS, AND THE SIXTEEN MEMBERS BELOW ARE DERIVED FROM THAT FACT RATHER
/// THAN CHOSEN. The legacy update was two provider calls, because the legacy object model flattened
/// a four-table join into one class of fifty-eight properties. The first call took NINE arguments -
/// module identity, title, all-pages flag, header, footer, start date, end date, inherit-view flag
/// and deleted flag. The second took FOURTEEN value arguments - page, module, order, pane, cache
/// period, alignment, colour, border, icon, visibility, container source, title flag, print flag
/// and syndicate flag. Subtracting what a REST update must not accept: the module identity comes
/// off the first call, because the route supplies it, leaving EIGHT; the module identity comes off
/// the second for the same reason and the SEVEN pane-layout, rendering and skinning arguments this
/// migration excludes come off with it, leaving SIX; and the TWO post-update intent flags are added
/// back, because the legacy screen genuinely posted them. EIGHT plus SIX plus TWO is SIXTEEN, which
/// is exactly the member count below. The count is therefore defensible rather than arbitrary, and
/// it must not be padded towards the twenty-three properties the legacy save assigned or the
/// fifty-eight the legacy class declared.
/// </para>
/// <para>
/// FOUR THINGS DISTINGUISH THIS CONTRACT FROM <see cref="CreateModuleRequest"/>, WHICH SHARES THE
/// OTHER THIRTEEN MEMBERS EXACTLY. (1) <c>ModuleDefId</c> IS ABSENT - the headline difference,
/// because A MODULE'S DEFINITION IS IMMUTABLE AFTER CREATION and the provider surface proves it.
/// (2) <see cref="IsDeleted"/> IS PRESENT, being a genuine argument of the first provider call. (3)
/// <see cref="SetAsDefaultSettings"/> IS PRESENT, the renamed first intent flag. (4)
/// <see cref="ApplyToAllModules"/> IS PRESENT, the renamed second intent flag. Every one of the
/// other thirteen members matches its counterpart on the create contract in type, nullability,
/// initialiser and documented meaning, so a reader diffing the two files sees only these four
/// genuine differences.
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
/// THE LEGACY SCREEN DECLARED NO PRESENCE VALIDATOR AT ALL, SO <see cref="ModuleTitle"/> IS NOT
/// REQUIRED. This is measured, not inferred: <c>Website/admin/Modules/modulesettings.ascx</c>
/// contains ZERO required-field validator declarations and ZERO validation-summary declarations.
/// Its only four validators are data-type checks - two date-format checks on the start and end
/// dates, an integer check on the border, and an integer check on the cache period. Because the
/// border is excluded from this contract, only THREE of those four rules have any counterpart here,
/// and NONE of the three is a presence check. Adding a non-empty rule to the title would be a
/// tightening that the migration's minimal-change discipline forbids, which demands that validation
/// rules MATCH and error messages be EQUIVALENT.
/// </para>
/// <para>
/// NO CROSS-FIELD DATE RULE EXISTS IN THE LEGACY SCREEN AND NONE IS SPECIFIED HERE. Both date
/// validators are format checks: neither names a control to compare against, and the markup
/// contains no such attribute anywhere. An end date preceding a start date was therefore storable,
/// producing a module that could never be visible. That is a legacy defect, recorded and
/// deliberately NOT corrected by this contract, because ordering the two dates is a new requirement
/// rather than a ported one.
/// </para>
/// <para>
/// <see cref="TabId"/> IS THE ONLY MANDATORY MEMBER, AND ITS NECESSITY IS STRUCTURAL RATHER THAN
/// PORTED. It derives from the second provider call's argument list and from the non-nullable
/// column behind it, not from any legacy validator. A presence check on it must be expressed as
/// "this page exists and belongs to the portal", NEVER as a numeric floor: the page identity seeds
/// at ZERO, so a page identifier of 0 is legitimate and rejecting it would make the first page of
/// every portal unusable.
/// </para>
/// <para>
/// THE LAST TWO MEMBERS ARE INTENT, NOT STORED MODULE STATE, AND ARE THE SUBTLEST ON THIS CONTRACT.
/// Neither is a column on either table. Both were fields the legacy object explicitly excluded from
/// serialisation, and the legacy update read them only to decide what ELSE to do once the two rows
/// had been written. They are renamed here from identifiers that actively misled - one sounded like
/// a collection and the other like persisted state - to the wording the product showed its own
/// users. <see cref="ApplyToAllModules"/> in particular now propagates only THREE of the NINE
/// values the legacy propagated, which is a documented functional reduction rather than an
/// oversight.
/// </para>
/// <para>
/// FOUR MEMBERS WERE ADMINISTRATOR-ONLY IN THE LEGACY SCREEN AND ARE NOW GATED BY POLICY RATHER
/// THAN BY SHAPE. The legacy page disabled exactly four controls for anyone outside the
/// administrator role, and all four map to members here: <see cref="AllTabs"/>,
/// <see cref="TabId"/>, <see cref="SetAsDefaultSettings"/> and <see cref="ApplyToAllModules"/>.
/// This contract still ACCEPTS all four; policy-based authorisation at the API layer decides
/// whether the write may proceed. That is deliberately not a validator rule and deliberately not an
/// extra member, because a permission is not a property of the submitted state.
/// </para>
/// <para>
/// NEVER ACCEPTED ON THIS CONTRACT: the module definition, the two generated identities, the portal
/// identifier, the seven pane-layout and rendering arguments, the container source, the
/// business-controller class name, permission entries, the two key-value setting stores, explicit
/// move-or-copy targets, any tab shape, paging metadata, any audit or acting-user field, and any
/// concurrency token. Each exclusion is measured and annotated below rather than merely asserted.
/// </para>
/// <para>
/// Declarative validation lives in <c>Application/Validation/UpdateModuleRequestValidator.cs</c>,
/// never as attributes on this type. Stateful checks - whether the page belongs to the portal,
/// whether the module exists - and the whole of the move, copy, withdraw and propagate
/// orchestration live in <c>Application/Services/ModuleService.cs</c>. The wire casing of every
/// member below is decided by one central serialisation policy at the API edge, so this type
/// carries no serialisation attribute.
/// </para>
/// </remarks>
// MIGRATION: 5.2 - THE FLATTENED LEGACY CLASS IS SPLIT ALONG THE REAL TABLE BOUNDARIES, WHICH IS
//   WHY ONE UPDATE WRITES TWO ROWS. The legacy ModuleInfo was a single class of fifty-eight
//   properties standing for a Modules-to-TabModules-to-ModuleDefinitions-to-ModuleControls join, so
//   a consumer holding a value could not tell which table it came from. The upgrade chain proves
//   the boundary rather than merely suggesting it: one statement drops ModuleOrder, PaneName,
//   CacheTime, Alignment, Color, Border, IconFile, Personalize, ShowTitle and ContainerSrc from the
//   module table, a second drops its page identifier as well, a third adds a nullable portal
//   identifier, and a fourth drops the foreign key that had tied modules to pages - relocating
//   every one of those values to the placement table. Only the cumulative terminal state of all
//   eighty-eight upgrade scripts is meaningful, because the chain is destructive; the baseline
//   script alone still shows those columns on the module table and deriving a column set from it
//   would be wrong. In the terminal schema the page identifier, the order, the cache period, the
//   icon, the visibility and the container flag live on the PLACEMENT table only. One request
//   therefore writes two rows, which is why the unit of work owns the commit rather than this
//   contract.
//
// MIGRATION: 5.15 - THE MODULE DEFINITION IS DELIBERATELY ABSENT, BECAUSE IT IS IMMUTABLE AFTER
//   CREATION, AND THE PROVIDER SURFACE PROVES IT RATHER THAN MERELY IMPLYING IT. The create path
//   passes TEN arguments to the first provider call; the update path passes NINE. Both carry the
//   SAME EIGHT value arguments. The create path's two extras are the PORTAL identifier and the
//   DEFINITION identifier; the update path's single extra is the module identity itself. The legacy
//   product therefore had no way to re-point an existing module at a different definition, and
//   neither does this endpoint - and the same comparison independently shows the PORTAL was equally
//   unchangeable, which corroborates excluding it below on tenant-isolation grounds. The screen
//   corroborates it again by rendering the definition's friendly name as a disabled field. This is
//   the single most important difference between this contract and the create contract, so
//   re-pointing a definition must be refused rather than quietly supported.
//
// MIGRATION: 5.16 - ONE LEGACY SAVE BECAME THREE TARGET OPERATIONS, WHICH IS A REAL LOSS OF
//   ATOMICITY AND IS RECORDED RATHER THAN GLOSSED. The legacy postback wrote, in a single
//   operation: the module row, the placement row, the PERMISSION GRID the screen posted alongside
//   them, and the per-definition settings, by calling back into a settings control the page had
//   loaded dynamically. The target splits that across three: this endpoint writes the two rows;
//   permission entries are governed by the permission evaluator and the read-only permission
//   catalogue under policy-based authorisation; and the key-value settings are a separate request
//   against the module's settings sub-resource. An operator who previously saved once must now
//   issue up to three requests, and a failure between them leaves work partially applied in a way
//   the single postback could not. The two rows THIS request writes are still committed together.
//
// MIGRATION: 5.17 - NO CONCURRENCY TOKEN EXISTS TO PORT, AND NONE IS INVENTED. Measured across all
//   eighty-eight upgrade scripts with a case-insensitive sweep: neither table carries a row-version
//   or time-stamp column, and there are ZERO such declarations anywhere in the chain. Because this
//   endpoint replaces rather than patches, two concurrent callers can therefore silently overwrite
//   one another - a lost update. That risk is stated plainly because a reader will reasonably
//   wonder why no token is offered, and the honest answer is that the legacy schema has none and
//   the schema is immutable, so adding one would be a schema change this migration forbids. The
//   legacy postback carried the same risk.
//
// MIGRATION: 5.13 - THE LEGACY SCREENS COMPILED WITH OPTION STRICT OFF, SO CONVERSION BEHAVIOUR AT
//   THIS BOUNDARY HAS DELIBERATELY CHANGED. The legacy web configuration sets strict to false,
//   which permitted implicit narrowing and late binding that C# rejects outright. The screen
//   consequently parsed a date with no culture argument and an integer with no guarded-parse
//   fallback, and converted an integer to a string implicitly when preselecting the page picker -
//   the latter twice, the second being an unguarded duplicate of a guarded lookup a few lines
//   earlier. Here the boundary is typed JSON, so those coercions move into deserialisation and the
//   validator: an unparseable date or integer now yields an RFC 7807 validation problem response at
//   the API edge rather than a client-side format message or a server-side exception. That is a
//   documented divergence in failure MODE, not in which values are accepted.
//
// MIGRATION: 5.14 - THE TWO LEGACY HYDRATION PATHS ARE DELETED RATHER THAN TRANSLATED. Rows were
//   materialised either by a reflection-driven object hydrator or by a hand-rolled reader block
//   that assigned each column through a sentinel-substituting conversion, one line per column
//   across forty-seven lines, and the collection forms returned an untyped list and a non-generic
//   map. The object-relational mapper's own materialiser replaces both, so no fill member, no
//   hydration interface and no collection wrapper exists anywhere in the target - and none appears
//   on this contract, which declares no collection member at all. Note that the legacy save also
//   built a list of target pages in order to copy or withdraw placements; that list is derived on
//   the server from the delta between the stored state and this request, and is never posted.
//
// MIGRATION: EXCLUSION SUMMARY, EACH ITEM MEASURED. (a) The DEFINITION is absent, per 5.15. (b)
//   Both IDENTITIES are absent: the module identity comes from the route, and the placement
//   identity is derived on the server from the module and the page. Their columns seed at ZERO and
//   ONE respectively, so no single numeric rule could serve both, and accepting either in the body
//   would permit a body-versus-route mismatch. The legacy "is this new?" test compared against the
//   integer sentinel -1, which was never expressible as a non-positive check because 0 is a real
//   module identity; excluding the identities dissolves that hazard entirely. (c) The PORTAL
//   identifier is absent - accepting it would be a cross-tenant write vector, and tenant isolation
//   must be preserved. It is resolved once per request from the addressed alias. This is especially
//   acute here because SetAsDefaultSettings causes a PORTAL-SCOPED write, so the portal must come
//   from context and never from the body; note also that the portal identity seeds at -1, so -1 is
//   a real portal rather than an absent one. (d) SEVEN pane-layout and rendering arguments are
//   excluded - pane, alignment, colour, border, print flag and syndicate flag, together with the
//   container source. These are REAL terminal placement columns, REAL arguments of the second
//   provider call, and were genuinely edited by real controls on the legacy screen, so this is a
//   real functional narrowing and not a tidy-up: dropping the border is precisely why only three of
//   the screen's four validators survive, the fourth having been an integer check whose message
//   read "Invalid Border (must be a number between 0 and 9)". Six of these seven are also exactly
//   the values ApplyToAllModules can no longer propagate, per 5.9. The pane is the sharpest case,
//   because its column is non-nullable: the write path supplies the conventional content pane
//   rather than letting the caller choose, consistent with the legacy value having come from the
//   skin's pane picker - a skinning concern this migration excludes - rather than from a freely
//   editable field. (e) The CONTAINER SOURCE is excluded as a skin object, and the write path must
//   leave the stored column ALONE rather than clearing it, since assigning an absent value would
//   silently wipe a stored container on every update. (f) The BUSINESS-CONTROLLER CLASS NAME is
//   excluded: it named a type the legacy code activated by reflection at five call sites, and
//   accepting a type name on an update request would be arbitrary remote activation. A
//   dependency-injected factory over a closed set replaces it. (g) PERMISSION ENTRIES are excluded
//   even though the legacy screen posted them through a permissions grid on this very save, so this
//   too is a real narrowing; only the inherit-view FLAG is retained, and no permission collection
//   is declared. The pseudo-principal numbering is a further reason to relocate rather than embed
//   them: -1 meant all users, -2 a superuser and -3 unauthenticated users, and the legacy
//   permission writer passed -1 with two different meanings depending on argument position. (h) The
//   two KEY-VALUE SETTING STORES are excluded, and with them the legacy panel that dynamically
//   loaded a per-definition settings control and called back into it on save, per 5.16. (i)
//   EXPLICIT MOVE-OR-COPY TARGETS are excluded: the move is expressed by changing TabId and the
//   copy by AllTabs, and the service derives the operations from the delta, so adding explicit
//   targets would duplicate intent and create contradictory-input states. (j) Any TAB SHAPE is
//   excluded - no page name and no nested page object; pages are a lookup resolved from the page
//   list contract. (k) PAGING METADATA is excluded, this being a single-resource write. (l) AUDIT
//   AND ACTING-USER FIELDS are excluded: measured across the eighty-eight scripts, neither table
//   carries any audit column at all, and the legacy code took the acting user from ambient state
//   rather than from the submitted form. The caller is identified by the authenticated principal's
//   claims. (m) CONTROL METADATA is excluded, which dissolves a genuine hazard: the legacy
//   control-type reader mapped -3, -2 and -1 to real access levels while -1 was simultaneously the
//   integer sentinel for an absent value. (n) The token-replacement interface and its cache-policy
//   member are dropped with that excluded subsystem; the member named for cacheability there is
//   unrelated to the genuine cache period carried at CacheTime. (o) DEFINITION AND PACKAGE METADATA
//   is excluded, being selected by the definition this endpoint cannot change; the legacy screen
//   made the point by disabling the friendly-name field. (p) The install-time feature bit-flags are
//   excluded; note that a value of -1 there meant "install not yet complete", another -1 that is
//   data rather than absence. (q) No CONCURRENCY TOKEN is invented, per 5.17.
public sealed class UpdateModuleRequest
{
    // --- Placement target: changing this MOVES the module ---

    /// <summary>
    /// The page this placement is on. Required.
    /// </summary>
    /// <remarks>
    /// This is the placement key, and in the terminal schema it lives on the placement table only:
    /// the module table's own page column was dropped by the upgrade chain. Callers resolve the
    /// permitted set from the portal's page list, which is the same lookup the legacy page picker
    /// performed when it bound a drop-down to page names and page identifiers. The user-facing word
    /// for this concept is "Page", not "Tab": the markup label read "Move To Tab:" but the
    /// authoritative resource file overrode it to "Move To Page:", helped by "Move this module
    /// instance to another Page." The resource wording wins because it is what users actually saw.
    /// A presence check must be expressed as "this page exists and belongs to the portal", never as
    /// a numeric floor, and never as a comparison against -1.
    /// </remarks>
    // MIGRATION: 5.11 - CHANGING THIS MEMBER MOVES THE MODULE, AND THE MOVE IS DERIVED FROM THE
    //   DELTA RATHER THAN COMMANDED EXPLICITLY. The legacy save ended with an ordered sequence of
    //   side effects: it moved the placement when the selected page differed from the current one,
    //   copied the module across every content page when the all-pages flag had just been set,
    //   withdrew it from those pages when the flag had just been cleared, and finally called back
    //   into the dynamically loaded settings control. The target reproduces the first three by
    //   comparing the stored state against this request inside the module service - which is
    //   precisely why no explicit move-or-copy target member exists - and the fourth moved to the
    //   separate settings endpoint, per 5.16.
    //
    // MIGRATION: 5.12 - THE LEGACY UPDATE COULD SKIP THE PLACEMENT WRITE ENTIRELY, AND THE TARGET
    //   CANNOT. The legacy path guarded the WHOLE second half - the placement write, the ordering
    //   fix-up, the default-module keys and the propagation loop - behind a test that the page
    //   identifier was not the integer sentinel. A value of -1 there therefore meant "there is no
    //   placement to update", and every one of those effects was silently skipped. Here this member
    //   is mandatory and -1 is NEVER an absence marker, so the placement row is always written.
    //   That is a deliberate divergence: the target refuses the ambiguous state rather than
    //   reproducing a silent no-op.
    //
    // MIGRATION: 5.8 - THIS FIELD WAS ADMINISTRATOR-ONLY IN THE LEGACY SCREEN, AND THAT IS ENFORCED
    //   ON THE WRITE PATH RATHER THAN BY THIS MEMBER. The legacy page disabled the page picker, the
    //   all-pages checkbox and both intent flags outright for anyone not in the administrator role
    //   - exactly four controls, and it did so in both the page load and the save handler. The
    //   target expresses that with policy-based authorisation at the API layer: the contract still
    //   ACCEPTS the value, and authorisation decides whether the write may proceed, rejecting the
    //   request or disregarding these four fields for a non-administrator. It is deliberately NOT a
    //   validator rule and NOT a second member, because a permission is not a property of the
    //   submitted state.
    //
    // MIGRATION: THE SEED TRAP HERE IS THE SHARPEST ON THIS CONTRACT. The page identity seeds at
    //   ZERO, so 0 IS A LEGITIMATE PAGE and a positive-only rule would reject the first page of
    //   every portal. Separately, -1 was an "any page" WILDCARD in the legacy query surface - the
    //   all-pages lookup was the single-page lookup passed the integer sentinel - so -1 there meant
    //   "match every page" rather than "no page". Neither value may ever be read as absence.
    public int TabId { get; set; }

    // --- Module scope: identical on every page the module appears on ---

    /// <summary>
    /// The heading for the module, or <see langword="null"/> to leave it unset. NOT required. At
    /// most 256 characters.
    /// </summary>
    /// <remarks>
    /// The 256-character bound is the column width and is the ONLY bound: the legacy text box
    /// declared a rendered width but no maximum length, so the schema is the sole authority. The
    /// legacy help text reads: "Enter a title for the Module. This will appear in the Title Bar of
    /// the Container for this Module, if supported by the Container." A missing value surfaced from
    /// the legacy layer as the EMPTY STRING rather than as an absent one, because the legacy string
    /// sentinel was literally the empty string; the projection layer owns that translation. On a
    /// full replacement that distinction bites harder than on a create: a caller sending
    /// <see langword="null"/> and a caller sending an empty string must both end up storing what
    /// the legacy blank text box stored, and the MAPPER decides that, not this contract.
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// Whether the module appears in the same position on every page of the portal, which produces
    /// one placement row per page. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/> because the column is non-nullable with a default
    /// constraint of 0, which coincides with the default of this member's type - so no initialiser
    /// is needed and none is present. The legacy label read "Display Module On All Pages?", helped
    /// by "Select whether the module should appear in the same location on all pages of the site".
    /// </remarks>
    // MIGRATION: 5.11 - TOGGLING THIS MEMBER HAS SIDE EFFECTS BEYOND THE ADDRESSED PLACEMENT, AND
    //   THEY ARE DERIVED FROM THE DELTA. The legacy save compared the posted flag against the
    //   stored one and, when it had changed, either copied the module onto every content page or
    //   withdrew it from them - never both, and only on a change rather than on every save. The
    //   target reproduces that comparison in the module service and performs the resulting writes
    //   in the same unit of work as the two rows, which the legacy could not, its statements having
    //   been independent.
    //
    // MIGRATION: 5.8 - ADMINISTRATOR-ONLY IN THE LEGACY SCREEN, gated by policy on the write path
    //   rather than by this member - see the fuller note on TabId. This is one of the exactly four
    //   controls the legacy page disabled for a non-administrator.
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
    /// The parseable-date rule is one of the three legacy rules that survive into this contract,
    /// and it is the whole of what the legacy screen checked for this field. The legacy input
    /// declared a maximum length of 11, which bounded the RENDERED TEXT rather than the column -
    /// the column is a nullable date and time - so that number is not a rule on this member. The
    /// legacy help text reads: "Enter the start date for displaying this module. You may use the
    /// Calendar to pick a date." This value GATES WHETHER THE MODULE RENDERS AT ALL, which is why
    /// its translation is spelled out rather than left to a default.
    /// </remarks>
    // MIGRATION: 5.4 - THE LEGACY ABSENT DATE WAS A SENTINEL, NOT SQL NULL, AND THIS WAS PROVEN IN
    //   BOTH DIRECTIONS. Reading, the legacy screen filled the box only when the value was not the
    //   sentinel, so the minimum date value rendered as an EMPTY BOX. Writing, a blank box stored
    //   the sentinel - the minimum date value - rather than a null. A nullable date is the correct
    //   wire shape, and the projection layer in ModuleMappings owns the explicit translation from
    //   an absent value to the legacy sentinel on the way in. The translation must stay explicit
    //   precisely because these dates decide visibility: silently storing a null where the legacy
    //   stored the minimum date, or the reverse, would change whether a module is ever shown. The
    //   same applies to EndDate, and note that NO ordering rule between the two exists to port.
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date until which the module is displayed, or <see langword="null"/> for no end
    /// restriction. NOT required; when supplied it need only be a parseable date.
    /// </summary>
    /// <remarks>
    /// The same rule, the same sentinel and the same translation as <see cref="StartDate"/> - see
    /// the annotation there. THERE IS NO RULE DEMANDING THAT THIS DATE FALL ON OR AFTER
    /// <see cref="StartDate"/>: measured, the legacy screen's two date validators are independent
    /// format checks, neither naming a control to compare against, and the markup contains no such
    /// attribute at all. An end date before a start date was therefore storable and produced a
    /// module that could never be visible. That is a legacy defect, recorded and deliberately not
    /// corrected by this contract; introducing an ordering rule would be a new requirement rather
    /// than a ported one. The legacy help text reads: "Enter the end date for displaying this
    /// module. You may use the Calendar to pick a date."
    /// </remarks>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Whether the module takes its <b>View</b> permission from its page instead of carrying its
    /// own. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// This is a FLAG ONLY. The permission entries themselves are not on this contract: they live
    /// behind the permission evaluator and the read-only permission catalogue, and the legacy
    /// screen posted them through a separate permissions grid whose omission is a real narrowing
    /// recorded in the class remarks. The legacy caption read: "Inherit &lt;b&gt;View&lt;/b&gt;
    /// permissions from &lt;b&gt;Page&lt;/b&gt;". In the legacy screen the checkbox posted back
    /// immediately in order to enable or disable that grid, which is presentation behaviour with no
    /// counterpart here. Note that the legacy update did act on this flag beyond storing it: when
    /// set, it discarded the View entries rather than writing them, which is behaviour the
    /// permission layer now owns.
    /// </remarks>
    // MIGRATION: THE TERMINAL COLUMN IS NULLABLE WITH NO DEFAULT CONSTRAINT, SO THE DEFAULT HERE IS
    //   FALSE - AND THE UPGRADE SCRIPT CONTAINS A TRAP THAT SUGGESTS OTHERWISE. Measured with a
    //   case-insensitive sweep across all four object-naming forms: the column was added as a
    //   NULLABLE bit with NO default constraint. Immediately after adding it, the same script runs
    //   a ONE-TIME BACK-FILL setting it to 1 for rows that had no explicit view roles and to 0 for
    //   those still null. That back-fill applies to PRE-EXISTING ROWS ONLY and is not a default for
    //   new writes, so it must not be mistaken for one; this member therefore relies on the default
    //   of its own type, false, and carries no initialiser, exactly as its counterpart on the
    //   create contract does. Because this endpoint REPLACES rather than patches, omitting the
    //   property clears the flag - which is the honest consequence of full- replacement semantics
    //   and is called out in the class remarks.
    public bool InheritViewPermissions { get; set; }

    /// <summary>
    /// Whether the module is in the recycle bin. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The soft-delete marker. The column is non-nullable with a default constraint of 0, which
    /// coincides with the default of this member's type, so no initialiser is needed and none is
    /// present. The module and every placement it has survive a soft delete and can be restored,
    /// which is what distinguishes it from removing a placement outright.
    /// </remarks>
    // MIGRATION: 5.6 - THE LEGACY SETTINGS SCREEN COULD ONLY EVER CLEAR THIS FLAG, SO ACCEPTING IT
    //   HERE IS A DELIBERATE WIDENING - AND A DIVERGENCE IN BOTH DIRECTIONS. Measured: the legacy
    //   save contains the bare, unconditional assignment of False, and the field was not on the
    //   form at all, so EVERY SAVE SILENTLY UN-DELETED THE MODULE. That is annotated as the primary
    //   legacy defect for this endpoint and is deliberately NOT reproduced. The value was actually
    //   toggled elsewhere: the screen's delete button removed the PLACEMENT rather than the module,
    //   and restoration was a separate administrative recycle-bin function. Consolidating both onto
    //   this endpoint is justified because the column is real, the read contract reports it, and it
    //   is genuinely the ninth argument of the first provider call. The divergence is therefore
    //   twofold: this endpoint can now SET a flag the legacy screen could only clear, and it no
    //   longer FORCES it to false on every save.
    public bool IsDeleted { get; set; }

    // --- Placement scope: specific to this occurrence on this page ---

    /// <summary>
    /// The module's position within its pane on the page. Defaults to -1, which appends it at the
    /// bottom of the pane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OMITTING THIS MEMBER APPENDS; SENDING 0 EXPLICITLY MEANS POSITION ZERO. That distinction is
    /// the entire reason for the initialiser, and deserialisation preserves it: an absent property
    /// leaves the initialised value intact, while a present one overwrites it. Under this
    /// contract's full-replacement semantics the consequence is sharper than on a create and must
    /// be stated plainly: omitting the property DOES NOT HOLD THE MODULE'S CURRENT POSITION - it
    /// moves it to the bottom of its pane. No validation rule may reject -1, and the legacy screen
    /// had no rule on this field at all.
    /// </para>
    /// <para>
    /// The instruction is consumed by the application service, which resolves it against the placement's
    /// own pane before anything is written, so -1 never reaches the column. When the request also asks to
    /// place the module on every page, each new placement is appended to its own page's pane rather than
    /// taking the position computed for the addressed page.
    /// </para>
    /// </remarks>
    // MIGRATION: 5.1 - -1 IS A LOAD-BEARING COMMAND MEANING "APPEND AT THE BOTTOM OF THE PANE", NOT
    //   AN ABSENT VALUE, AND FOUR INDEPENDENT PROOFS SAY SO. The legacy create branched on the
    //   value being -1 under the comment "position module at bottom of pane", taking a different
    //   ordering path from any other value; the ordering routine's own documentation described its
    //   parameter as "position within the controls list on page, -1 if to be added at the end";
    //   that routine implements the instruction by reading the highest existing order and stepping
    //   past it; and the move routine passes the literal -1 as the destination order, so a moved
    //   module lands at the bottom of its new pane. The collision is what makes this dangerous: the
    //   legacy integer sentinel for an absent value is ALSO -1, so mapping this member to a
    //   nullable integer would silently convert an instruction into an absence and change where
    //   every module lands. It therefore stays a non-nullable integer, keeps its -1 initialiser, is
    //   never mapped to null, and is never validated away.
    public int ModuleOrder { get; set; } = -1;

    /// <summary>
    /// How long the module's output may be cached, in seconds. Defaults to 0, which means no
    /// caching. When supplied it need only be an integer.
    /// </summary>
    /// <remarks>
    /// The integer rule is the third and last of the legacy rules that survive into this contract.
    /// The legacy label read "Cache Time (secs):", helped by "Enter the time this object is kept in
    /// the Cache", and its input declared a maximum length of 6, which bounded the RENDERED TEXT
    /// rather than the column, so that number is not a rule on this member. Zero is a real,
    /// meaningful value and not an absence, so the default of this member's type is already correct
    /// and no initialiser is present.
    /// </remarks>
    // MIGRATION: 5.5 - A BLANK LEGACY FIELD STORED ZERO, NOT A SENTINEL. The legacy save read the
    //   box and stored a parsed integer when it was non-empty and LITERALLY ZERO when it was empty.
    //   Zero therefore means "do not cache" and is indistinguishable from a deliberate choice of
    //   zero, which is precisely why this member is a non-nullable integer: making it nullable
    //   would invent an "unspecified" state the legacy contract never had, and treating zero as
    //   unset would silently enable caching on a module the caller asked not to cache.
    //
    // MIGRATION: 5.7 - A DEFINITION'S DEFAULT CACHE PERIOD OF -1 MEANT "CACHING NOT APPLICABLE" AND
    //   HID THIS FIELD ENTIRELY, WHICH IS DEFINITION METADATA RATHER THAN A RULE HERE. The legacy
    //   screen hid the whole cache row when the definition's default cache period equalled the
    //   integer sentinel, and that column is non-nullable with a default constraint of 0, so -1 was
    //   stored EXPLICITLY to carry that meaning. A client wishing to hide this field consults the
    //   definition contract; nothing about it constrains the value submitted here, and it is
    //   deliberately not a server-side fallback for an omitted value.
    public int CacheTime { get; set; }

    /// <summary>
    /// The icon displayed with the module's title, or <see langword="null"/> for none. NOT
    /// required. At most 100 characters.
    /// </summary>
    /// <remarks>
    /// The 100-character bound is the column width, and the legacy control declared no validator.
    /// The legacy help text reads "Select an Icon for this Module to display in the Title Bar".
    /// This is a STORED NAME OR PATH STRING ONLY: file-system handling is outside the migration's
    /// scope, so this contract performs no upload, resolves no location and promises no reachable
    /// address. A missing value surfaced from the legacy layer as the empty string rather than as
    /// an absent one. This is also one of the three values <see cref="ApplyToAllModules"/> can
    /// still propagate.
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
    /// list index. A validation rule must accept exactly the three defined members and nothing
    /// else. The legacy label read "Visibility:", helped by "Choose the default visibility for this
    /// Module". This is one of the three values <see cref="ApplyToAllModules"/> can still
    /// propagate.
    /// </remarks>
    // MIGRATION: 5.3 - THE LEGACY READER COLLAPSED THREE DISTINCT INPUTS INTO ONE STATE AND HAD NO
    //   DEFAULT BRANCH. Its selection block mapped both 0 AND the integer sentinel -1 - and,
    //   through the sentinel substitution applied first, a stored null as well - to the maximised
    //   state, mapped 1 to minimised and 2 to none, and declared NO fallback branch. A stored value
    //   outside that set therefore silently produced the maximised state on read, and the same
    //   missing-default pattern recurs in the legacy save, where such a value left the field
    //   unchanged instead. Both are annotated as legacy defects and deliberately NOT fixed here.
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
    /// saw. The name is preserved because it is the terminal column name. This is the third of the
    /// three values <see cref="ApplyToAllModules"/> can still propagate.
    /// </remarks>
    // MIGRATION: 5.10 - THE DEFAULT IS TRUE, CONFIRMED TWICE, SO C#'s DEFAULT HAD TO BE CORRECTED.
    //   The terminal column is non-nullable with a default constraint of 1, and the legacy
    //   constructor independently initialised the backing field to True. The default of this
    //   member's type is false, which would HIDE the container of every module updated by a caller
    //   that omitted the property - a silent behaviour change, and a worse one here than on a
    //   create because this contract replaces rather than patches. Hence the explicit true
    //   initialiser, which deserialisation leaves intact when the property is absent and overwrites
    //   when it is present.
    public bool DisplayTitle { get; set; } = true;

    // --- Behaviour flags: intent, not stored module state ---

    /// <summary>
    /// An instruction rather than module state: name this module and its page as the portal's
    /// default settings for newly added modules. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The legacy label read "Set As Default Settings?", helped by "Select this option if you would
    /// like the Page Settings for this module to be used as the default settings when adding new
    /// modules." That wording is the source of this member's name. Nothing about this flag is
    /// stored on either the module or the placement row, and no read contract echoes it back; it is
    /// write-only intent, and a caller cannot observe whether it was previously set by reading the
    /// module.
    /// </remarks>
    // MIGRATION: RENAMED FROM THE LEGACY IDENTIFIER, WHICH ACTIVELY MISLED. The legacy property was
    //   called IsDefaultModule, which reads as persisted state - as though the module carried a
    //   "this one is the default" column. It carried no such column: the field was explicitly
    //   excluded from the legacy object's serialisation, one of nine so excluded, which is itself
    //   evidence it was never persisted state. It is renamed to the wording the product showed its
    //   own users, quoted above, so that the contract says what the flag DOES rather than what it
    //   appeared to be.
    //
    // MIGRATION: THIS FLAG WRITES OUTSIDE THE MODULE AGGREGATE, WHICH IS A GENUINE CROSS-AGGREGATE
    //   SIDE EFFECT. When set, the legacy update wrote two PORTAL-level site-settings keys naming
    //   the default module and the default page. It therefore changed PORTAL configuration, not the
    //   module row. The service must perform that write through the appropriate abstraction, never
    //   this contract, and the portal it writes against must come from the per-request portal
    //   context - which is a further reason the portal identifier is refused in the body, since
    //   accepting it here would turn this flag into a cross-tenant write vector.
    //
    // MIGRATION: 5.8 - ADMINISTRATOR-ONLY IN THE LEGACY SCREEN, gated by policy on the write path
    //   rather than by this member - see the fuller note on TabId. This is one of the exactly four
    //   controls the legacy page disabled for a non-administrator.
    public bool SetAsDefaultSettings { get; set; }

    /// <summary>
    /// An instruction rather than module state: copy this placement's appearance to every module on
    /// every non-administrative page of the portal. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// THE MOST DANGEROUS MEMBER ON THE MODULE API: its blast radius is the WHOLE PORTAL, and it is
    /// the only member here that writes rows belonging to modules other than the one addressed. The
    /// legacy label read "Apply To All Modules?", helped by "Select this option if you would like
    /// the Page Settings for this module to be applied to all existing modules in the site." That
    /// wording is the source of this member's name. Like its sibling flag it is stored nowhere and
    /// echoed by no read contract, so a caller cannot observe whether it was previously set.
    /// </remarks>
    // MIGRATION: RENAMED FROM THE LEGACY IDENTIFIER, WHICH ACTIVELY MISLED. The legacy property was
    //   called AllModules, which reads as a COLLECTION of modules rather than as an instruction
    //   about them, and it sat next to a genuinely collection-valued permission property,
    //   compounding the confusion. It carried no column, having been explicitly excluded from the
    //   legacy object's serialisation. It is renamed to the wording the product showed its own
    //   users, quoted above.
    //
    // MIGRATION: 5.9 - THIS FLAG NOW PROPAGATES ONLY THREE OF THE NINE VALUES THE LEGACY
    //   PROPAGATED, WHICH IS A DOCUMENTED FUNCTIONAL REDUCTION RATHER THAN AN OVERSIGHT. Measured
    //   from the legacy loop: it walked every page of the portal, skipped the administrative ones,
    //   and for every module on each remaining page rewrote the placement row with NINE of this
    //   module's appearance values - alignment, colour, border, icon, visibility, container source,
    //   and the title, print and syndicate flags - while preserving each target's own page, module,
    //   order, pane and cache period. SIX of those nine are excluded from this contract as
    //   pane-layout, rendering or skinning concerns, leaving only the ICON, the VISIBILITY and the
    //   CONTAINER-DISPLAY FLAG propagable. The reduction follows mechanically from those exclusions
    //   rather than being a separate decision, and it means an operator who used this flag to
    //   standardise alignment or colour across a portal can no longer do so.
    //
    // MIGRATION: 5.8 - ADMINISTRATOR-ONLY IN THE LEGACY SCREEN, gated by policy on the write path
    //   rather than by this member - see the fuller note on TabId. This is the last of the exactly
    //   four controls the legacy page disabled for a non-administrator, and the one where that gate
    //   matters most, given the portal-wide reach described above.
    public bool ApplyToAllModules { get; set; }
}
