// MIGRATION: this service replaces the module half of Library/Components/Modules/ModuleController.vb
// (1,456 lines of Shared members) together with the business rules that lived in the three admin
// code-behinds Website/admin/Modules/{ModuleSettings,Export,Import}.ascx.vb. Every member below is an
// instance method reached through an injected interface, so the legacy static surface and its ambient
// HttpContext dependencies are gone.
//
// MIGRATION: the legacy hand-rolled row hydration is not reproduced. ModuleController.vb:L54 and
// L66-L72 instantiated an entity and then assigned each column through
// Convert.ToInt32(Null.SetNull(dr("Column"), currentValue)), one line per column. The EF Core
// materialiser behind the repository abstractions replaces that entirely, which is why no Fill,
// FillObject or CBO equivalent appears anywhere in this file.
//
// MIGRATION: the two late-bound activation sites at ModuleController.vb:L231 (content export) and
// L431 (content import) are replaced by IModuleBusinessControllerFactory. Nothing in this file loads
// an assembly, resolves a type from a string or constructs a type dynamically. These are ORDINARY
// .NET REFLECTION sites, and the three further sites in EventMessageProcessor.vb (L32, L52, L77) are
// too: the exclusion covering VB6 and ActiveX activation is vacuous against this codebase, so nothing
// here should be read as removing that kind of interop, because there was never any to remove.
//
// MIGRATION: the two - not one - ambient System.Web calls in the 1,456-line source are named here
// together, because they are one pipeline seen from its two ends and were measured as its only System.Web
// dependencies (`grep -n HttpContext` returns exactly L244 and L428):
//
//     L244  Content    = HttpContext.Current.Server.HtmlEncode(Content)     ' PORTAL TEMPLATE WRITER
//     L428  strcontent = HttpContext.Current.Server.HtmlDecode(strcontent)  ' PORTAL TEMPLATE READER
//
// NEITHER IS REPRODUCED, AND THE REASON IS THE WORKFLOW THEY BELONG TO RATHER THAN THE DEPENDENCY THEY
// CARRY. That pair reads and writes the PORTAL TEMPLATE format, in which L244 encodes the payload and
// L245-L246 wrap the result in a CDATA section for L414 and L428 to undo. The content endpoints in this
// file migrate the MODULE ADMIN workflow instead - Website/admin/Modules/Export.ascx.vb L157-L165 and
// Import.ascx.vb L196-L200 - which escapes nothing on the way out and reads DocumentElement.InnerXml on
// the way in. An earlier revision applied the template pair here, which escaped every exported payload
// twice and corrupted every entity reference on import; both halves are withdrawn together and the
// divergence is recorded in MIGRATION_NOTES.md. The consequence for this file is that it performs NO
// HTML escaping at all and consequently needs no substitute for HttpServerUtility: the ambient
// dependency disappears with the transformation rather than being replaced by a framework-agnostic
// equivalent.
//
// MIGRATION: reported and not corrected - the migration plan describes ModuleInfo.vb as 58 properties,
// whereas the measured count of `Public Property` declarations in that 936-line class is 54. The
// measurement stands; nothing in this file depends on the figure, because the flattened class is
// consumed here only through the four entities it was split into.
//
// MIGRATION: two collaborators the plan names are deliberately NOT injected, and the reasons are
// recorded so their absence is not read as an oversight.
//   - ILogger<ModuleService> and IOptions<T> CANNOT be named in this project. Its manifest declares
//     exactly two packages, both FluentValidation, and neither Microsoft.Extensions.Logging.Abstractions
//     nor Microsoft.Extensions.Options is in the reference pack a class library targets or is supplied
//     transitively, so either generic fails to compile with CS0234 and CS0246. The dependency is
//     therefore inverted rather than imported: IAuditSink is declared in this layer and implemented in
//     Infrastructure over Serilog, which is exactly where the structured-logging obligation is assigned,
//     and the bound CachingOptions instance is taken directly instead of a wrapper around it.
//   - IClock is not injected because this service never reads a clock. Every date it handles is
//     supplied by the caller on a request DTO and is only ever compared against another supplied date,
//     and audit records are stamped by the sink. Injecting a clock to leave it unused would be a
//     dependency the constructor could not justify.
//
// MIGRATION: the legacy caching of a module's settings under the keys "GetModuleSettings<id>" and
// "GetTabModuleSettings<id>" (ModuleController.vb:L1241 and L1338, both expiring after
// 20 * PerformanceSetting minutes) is deliberately NOT reproduced, and the omission is recorded here
// rather than left to be discovered. The legacy cached a name/value Hashtable per store, whereas this
// service composes one ModuleSettingsDto that also carries mutable module and placement state; the
// two cannot be cached under the legacy keys without either caching a tracked entity graph belonging
// to a scoped unit of work or reading the rows twice. The definition catalogue, which is
// installation-time reference data, is cached instead.
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Mapping;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>
/// Orchestrates the module aggregate: the catalogue of installed definitions, the module instances a
/// portal has created from them, the pages those instances are placed on, the two key/value settings
/// stores that hang off a module and off a placement, and the portable-content export and import pair.
/// </summary>
/// <remarks>
/// <para>
/// A module and its placement are separate records here. The legacy <c>ModuleInfo</c> class was a
/// flattened join over <c>dbo.Modules</c>, <c>dbo.TabModules</c>, <c>dbo.ModuleDefinitions</c> and
/// <c>dbo.ModuleControls</c>, which made "the module" and "the module on this page" indistinguishable.
/// Members that address a single placement therefore take a placement identifier, and members that
/// address the module itself do not.
/// </para>
/// <para>
/// Every multi-table write commits exactly once through <see cref="IUnitOfWork"/>, so a module can
/// never exist without the placement it was created with and a fan-out across pages can never be left
/// half applied.
/// </para>
/// </remarks>
public sealed class ModuleService : IModuleService
{
    /// <summary>
    /// Reported when the addressed portal does not exist.
    /// </summary>
    private const string PortalNotFoundCode = "module.portal_not_found";

    /// <summary>
    /// Reported when the submitted request is malformed in a way no validator caught.
    /// </summary>
    private const string RequestInvalidCode = "module.request_invalid";

    /// <summary>
    /// Reported when the placement a request addresses does not exist: a placement identifier naming a row
    /// that belongs to another module, or a page the addressed module is not placed on.
    /// </summary>
    /// <remarks>
    /// One code covers both because they are one condition seen through the two ways a caller can name a
    /// placement - by its own identifier on the read and settings paths, by the page it sits on for a full
    /// replacement. The <c>not_found</c> token is what the shared status table answers with 404, so the
    /// status is decided by the code rather than written out at a call site. The message names the module and
    /// the page, which leaks nothing: every route carrying this code is already gated on a grant over the
    /// module in question.
    /// </remarks>
    private const string PlacementNotFoundCode = "module.placement_not_found";

    /// <summary>
    /// Reported when the addressed module does not exist in the portal.
    /// </summary>
    private const string NotFoundCode = "module.not_found";

    /// <summary>
    /// Reported when the caller holds no edit grant on the page or module a mutation targets.
    /// </summary>
    /// <remarks>
    /// The <c>forbidden</c> token is what the shared status translator classifies as a 403, so the code
    /// itself - rather than a status written out at a call site - is what decides how this refusal reaches
    /// the caller. The message names the target but never says WHY the grant is missing, and a target that
    /// does not exist produces this same refusal rather than a not-found: distinguishing the two would tell
    /// an unauthorised caller which identifiers exist.
    /// </remarks>
    private const string EditForbiddenCode = "module.edit_forbidden";

    /// <summary>
    /// Reported when a caller asks for one of the four portal-wide effects without administering the
    /// portal.
    /// </summary>
    /// <remarks>
    /// A code of its own rather than a reuse of <see cref="EditForbiddenCode"/>, because the two refusals
    /// mean different things to a client and are corrected differently: the edit refusal says "you may not
    /// touch this module", while this one says "you may edit this module, but not in a way that reaches
    /// pages you do not administer". A client can act on the difference - by disabling exactly the four
    /// controls the legacy screen disabled - which it cannot do if both arrive under one code. The
    /// <c>forbidden</c> token is what the shared status translator classifies as a 403, so the code itself
    /// decides the status.
    /// </remarks>
    private const string AdministratorForbiddenCode = "module.administrator_forbidden";

    /// <summary>
    /// Reported when the named definition does not exist or is not available to the portal.
    /// </summary>
    private const string DefinitionNotFoundCode = "module.definition_not_found";

    /// <summary>
    /// Reported when the named page does not belong to the portal.
    /// </summary>
    private const string TabNotFoundCode = "module.tab_not_found";

    /// <summary>
    /// Reported when a submitted setting name or value cannot be stored.
    /// </summary>
    private const string SettingInvalidCode = "module.setting_invalid";

    /// <summary>
    /// Reported when the generic settings surface is asked to read or mutate security-owned settings.
    /// </summary>
    /// <remarks>
    /// The <c>protected</c> token is intentionally part of the code because the API result translator maps
    /// that token to HTTP 403. Administrative modules are configured through typed, portal-administrator
    /// endpoints; allowing their open key/value stores through the generic ModuleEdit boundary would bypass
    /// those stronger policies.
    /// </remarks>
    private const string SettingsProtectedCode = "module.settings_protected";

    /// <summary>
    /// Reported when the module cannot take part in a content export or import.
    /// </summary>
    private const string NotPortableCode = "module.not_portable";

    /// <summary>
    /// Reported when a submitted content document cannot be read.
    /// </summary>
    private const string ContentInvalidCode = "module.content_invalid";

    /// <summary>
    /// Reported when a submitted content document names a module type that is not the target module's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: restores the fourth outcome of the legacy import screen, "The import file specified is not
    /// the correct type for this module" (<c>Website/admin/Modules/Import.ascx.vb</c>, reported from both
    /// line 205 and line 217). Distinct from <see cref="ContentInvalidCode"/> because the document is
    /// perfectly well-formed and perfectly valid - it is simply somebody else's. Conflating the two would
    /// tell an operator to repair a file that needs no repair.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy screen sourced this refusal from the document's FILE NAME - it tested whether
    /// the name contained the cleaned module name - and additionally from the type attribute once the file
    /// was open. The target has no shared file system, so the name-based half has nothing to test; the
    /// attribute-based half is what survives, which is recorded on <c>ModuleImportRequest</c> as the
    /// re-sourcing of an outcome rather than the loss of one.
    /// </para>
    /// </remarks>
    private const string ContentTypeMismatchCode = "module.content_type_mismatch";

    /// <summary>
    /// Reported when a module hands over content that cannot be carried by the export document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="ContentInvalidCode"/> on purpose, and the distinction is about WHO can put
    /// it right. A document the CALLER submitted and that cannot be interpreted is a request to correct, so
    /// it is reported as invalid content. Content the MODULE produced and that XML cannot represent - a
    /// control character, for instance, which no escaping in the specification can encode - is a fault in
    /// the module, and the caller can do nothing about it. The reason token <c>export_failed</c> is
    /// classified as a server fault by the API's failure-code table, so this surfaces as a 500 rather than
    /// telling the caller to fix a request that was correct.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy export wrapped its whole document assembly in <c>Try</c> with a <c>Catch</c>
    /// body of <c>'ignore errors</c> (ModuleController.vb L250-L252), so a module whose content could not
    /// be written produced a portal template with the content element silently missing and reported
    /// success. That swallow is a defect and is not reproduced: the failure is surfaced with this code. The
    /// divergence is recorded rather than absorbed, per the rule that a discovered defect is annotated and
    /// the swallow is resolved in favour of surfacing.
    /// </para>
    /// </remarks>
    private const string ExportFailedCode = "module.export_failed";

    /// <summary>
    /// Carried on an otherwise successful update whose blast radius exceeded the addressed module.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy screen recorded nothing when a save named a portal default or propagated
    /// appearance, even though both touch records the operator never addressed. This project's
    /// Application layer declares no logging package - <c>DnnMigration.Application.csproj</c> carries
    /// FluentValidation and nothing else - so the audit record is this reason, which the API layer's
    /// request logging emits, rather than a log call made from here. Adding a logging dependency to
    /// this project would be the manifest drift the review already faulted once.
    /// </remarks>
    private const string WideEffectCode = "module.update.wide_effect";

    /// <summary>
    /// Maximum length of a setting name in both <c>dbo.ModuleSettings</c> and
    /// <c>dbo.TabModuleSettings</c>; both declare <c>SettingName nvarchar(50) NOT NULL</c>.
    /// </summary>
    private const int SettingNameMaximumLength = 50;

    /// <summary>
    /// Maximum length of <c>dbo.ModuleSettings.SettingValue</c>, declared <c>nvarchar(2000)</c> by the
    /// terminal schema.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <b>the width is 2000, not 256.</b> The
    /// 01.00.00 create script did declare <c>nvarchar(256)</c> at line 353, but the later chain does
    /// widen it: <c>01.00.08.SqlDataProvider</c> lines 6248-6286 destroy and rebuild the whole table
    /// through a <c>Tmp_ModuleSettings</c> copy declaring <c>SettingValue nvarchar(2000) NOT NULL</c>
    /// at line 6256, and no subsequent script narrows it. The terminal writers agree - both
    /// <c>UpdateModuleSetting</c> (<c>01.00.08</c> line 6295) and, in templated form,
    /// <c>AddModuleSetting</c> and <c>UpdateModuleSetting</c> (<c>02.00.00</c> lines 4147 and 4171)
    /// declare <c>@SettingValue nvarchar(2000)</c>. Validating at 256 refused values the legacy
    /// application accepted and stored, which Minimal Change Clause item 3 forbids, so the two stores
    /// turn out to permit the same width rather than differing ones.
    /// </remarks>
    private const int ModuleSettingValueMaximumLength = 2000;

    /// <summary>
    /// Maximum length of <c>dbo.TabModuleSettings.SettingValue</c>, declared <c>nvarchar(2000)</c> by
    /// the 03.00.01 create script.
    /// </summary>
    private const int PlacementSettingValueMaximumLength = 2000;

    /// <summary>
    /// The submitted position that means "put this module at the bottom of its pane" rather than
    /// naming a position.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this value is a COMMAND on the request contract and must never reach a column. The
    /// legacy documentation states it outright - <c>UpdateModuleOrder</c>'s own parameter comment reads
    /// "position within the controls list on page, -1 if to be added at the end"
    /// (ModuleController.vb:L1155) - and the legacy code acted on it as a command in two places, both
    /// immediately after writing the row: <c>AddModule</c> tested
    /// <c>If objModule.ModuleOrder = -1 Then UpdateModuleOrder(...)</c> (L667-L669) and
    /// <c>UpdateModule</c> called the same resolver unconditionally (L1124). The number is the integer
    /// absence sentinel from <c>Library/Components/Shared/Null.vb</c> L41 being reused as an
    /// instruction, which is why it must be consumed by this layer rather than mapped onto the column
    /// like an ordinary value: persisted literally it would sort every appended module ahead of every
    /// deliberately positioned one, since the stored positions are non-negative.
    /// </remarks>
    private const int AppendPositionSentinel = -1;

    /// <summary>
    /// The gap the legacy renumbering pass left between two adjacent positions in one pane.
    /// </summary>
    /// <remarks>
    /// MIGRATION: measured, not chosen. <c>UpdateModuleOrder</c> resolved an append by reading the
    /// pane's occupied positions and then adding exactly two (ModuleController.vb:L1170), and the
    /// renumbering pass that normalises a pane assigns <c>(counter * 2) - 1</c> (L1205 and L1441). The
    /// step of two is therefore what keeps an appended module's position on the same odd sequence a
    /// renumbering pass produces, leaving the even numbers between them free for an insertion. A step
    /// of one would appear to work and would quietly collide with the next renumbering.
    /// </remarks>
    private const int PositionStep = 2;

    /// <summary>
    /// Largest number of settings either scope may carry in one submission.
    /// </summary>
    /// <remarks>
    /// MIGRATION: a net-new bound with no legacy counterpart, and generous by design: seventy-two distinct
    /// setting names exist across EVERY bundled module in the whole legacy application, so this admits more
    /// than three times that vocabulary for a single module. It bounds row growth and transaction size, not
    /// any real configuration. The request validator states the same figure so a caller reached through the
    /// API gets a field-level answer; this copy covers every other caller, and the two must agree.
    /// </remarks>
    private const int SettingsPerScopeMaximum = 250;

    /// <summary>
    /// Largest number of settings the two scopes may carry between them in one submission.
    /// </summary>
    /// <remarks>
    /// MIGRATION: deliberately lower than twice the per-scope bound, so that it binds when both maps are
    /// large rather than being implied by them. This is the figure that bounds one transaction's row count,
    /// which the per-scope bounds alone do not protect. Mirrored by the request validator.
    /// </remarks>
    private const int SettingsAggregateMaximum = 400;

    /// <summary>
    /// Friendly name of the module instance that holds a portal's own settings rows.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>PortalSettings.UpdateSiteSetting</c> (PortalSettings.vb:L970-L978) resolved this
    /// module by friendly name and then wrote an ordinary <c>dbo.ModuleSettings</c> row against it, so
    /// what the legacy screen called a "portal level" key has always been a module setting on a
    /// well-known module instance. There is no portal settings table to write to.
    /// </remarks>
    private const string SiteSettingsDefinitionName = "Site Settings";

    /// <summary>
    /// Setting name that records which module a portal opens by default.
    /// </summary>
    private const string DefaultModuleSettingName = "defaultmoduleid";

    /// <summary>
    /// Setting name that records which page a portal opens by default.
    /// </summary>
    private const string DefaultTabSettingName = "defaulttabid";

    /// <summary>
    /// Cache key holding a portal's projected definition catalogue.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this key is new rather than carried over. The legacy
    /// <c>DataCache.ModuleCacheKey</c> ("Modules{0}", DataCache.vb:L63) held a dictionary of module
    /// instances keyed by friendly name, which is a different payload from the definition catalogue
    /// projected here, so reusing that name would make two unrelated payloads collide.
    /// </remarks>
    private const string DefinitionCatalogueCacheKeyFormat = "ModuleDefinitions{0}";

    /// <summary>
    /// Base expiry of the definition catalogue, matching every module-related legacy timeout.
    /// </summary>
    private const int DefinitionCatalogueCacheTimeOutMinutes = 20;

    /// <summary>
    /// Element name of an exported content document.
    /// </summary>
    private const string ContentElementName = "content";

    /// <summary>
    /// Attribute naming the module type an exported document came from.
    /// </summary>
    private const string ContentTypeAttributeName = "type";

    /// <summary>
    /// Attribute naming the module version an exported document was produced by.
    /// </summary>
    private const string ContentVersionAttributeName = "version";

    /// <summary>Maximum number of characters accepted in one portable-content XML document.</summary>
    private const long ImportDocumentCharacterMaximum = 1_048_576;

    /// <summary>Maximum number of XML nodes accepted before module-owned content is invoked.</summary>
    private const int ImportDocumentNodeMaximum = 10_000;

    /// <summary>Maximum nesting depth accepted in portable-content XML.</summary>
    private const int ImportDocumentDepthMaximum = 64;

    /// <summary>Maximum number of attributes accepted on any one XML element.</summary>
    private const int ImportElementAttributeMaximum = 64;

    /// <summary>Maximum number of characters accepted in any one text-like XML node.</summary>
    private const int ImportTextNodeCharacterMaximum = 262_144;

    /// <summary>Fixed caller-safe message for every XML parser or work-budget refusal.</summary>
    private const string ImportParseFailureMessage =
        "The submitted document could not be parsed safely as portable module content.";

    /// <summary>
    /// The declaration an exported document opens with, reproduced character for character from the legacy
    /// export screen - including the space before the closing angle bracket pair.
    /// </summary>
    /// <remarks>
    /// <c>Website/admin/Modules/Export.ascx.vb</c> L158 emits
    /// <c>"&lt;?xml version=""1.0"" encoding=""utf-8"" ?&gt;"</c> as a literal. It is a literal here too,
    /// rather than being produced by an XML writer, because a writer normalises the spacing and the whole
    /// point of this member is that a document leaving this endpoint is byte-comparable with one the legacy
    /// application wrote. The declared encoding is honest: the response is served as UTF-8.
    /// </remarks>
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>";

    /// <summary>
    /// The punctuation the legacy name sanitiser removes, in its own order.
    /// </summary>
    /// <remarks>
    /// Reproduced character for character from <c>Website/admin/Modules/Export.ascx.vb</c> L212, which
    /// declares <c>". ~`!@#$%^&amp;*()-_+={[}]|\:;&lt;,&gt;?/" &amp; Chr(34) &amp; Chr(39)</c> - the set
    /// ending in the double quote and the apostrophe. The same set appears in
    /// <c>Library/Components/Shared/Globals.vb</c> L1688, which the legacy history records as the shared
    /// version of this very helper, so the two agree. Note what the set does NOT contain: the leading
    /// character is a full stop and there is no closing angle bracket omission - every character is removed
    /// rather than substituted for, so the sanitiser shortens a name and never lengthens it.
    /// </remarks>
    private const string NamePunctuation = ". ~`!@#$%^&*()-_+={[}]|\\:;<,>?/\"'";

    /// <summary>
    /// The characters the legacy portability screens stripped out of a module name before writing it into
    /// a document's type attribute or comparing it against one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: measured character for character from <c>CleanName</c>, which both portability screens
    /// declared identically - <c>Website/admin/Modules/Export.ascx.vb</c> lines 208-209 and
    /// <c>Website/admin/Modules/Import.ascx.vb</c>, each as
    /// <c>". ~`!@#$%^&amp;*()-_+={[}]|\:;&lt;,&gt;?/" &amp; Chr(34) &amp; Chr(39)</c>. The two appended
    /// characters are the double and single quotation marks, spelled as character codes in the source
    /// because the surrounding literal could not carry them.
    /// </para>
    /// <para>
    /// The set is reproduced rather than replaced by a general-purpose rule, because it is part of the FILE
    /// FORMAT: the value it produces is what the legacy exporter wrote into the attribute and what the
    /// legacy importer compared against, so a document written here stays readable by a legacy
    /// installation and vice versa. A stricter or looser rule would break that in one direction or the
    /// other. Note that it happens to remove every character an XML attribute value would need escaped -
    /// the angle brackets, the ampersand and both quotation marks are all members - so the cleaned value
    /// is inert in the position it occupies, but that is a consequence of the legacy set rather than the
    /// reason for it.
    /// </para>
    /// </remarks>
    private const string ContentTypeStrippedCharacters = ". ~`!@#$%^&*()-_+={[}]|\\:;<,>?/\"'";

    /// <summary>
    /// Longest caller-supplied provenance value recorded on an import audit event.
    /// </summary>
    /// <remarks>
    /// The submitted folder and document names are recorded because the contract promises the provenance,
    /// but they are caller-controlled and the request applies no length bound of its own, so a bound is
    /// applied HERE rather than trusted upstream. Without one, a caller could move arbitrary content into
    /// a metadata field and have it copied verbatim into retained logs - a disclosure channel and, at
    /// scale, an amplification of one request into an arbitrarily large log event.
    /// </remarks>
    private const int AuditProvenanceMaximumLength = 128;

    /// <summary>
    /// Longest declared document version recorded on an import audit event.
    /// </summary>
    /// <remarks>
    /// <c>DesktopModules.Version</c> is a six-character column, so a legitimate value is far shorter than
    /// this; the allowance exists only so a mismatched document is described rather than discarded.
    /// </remarks>
    private const int AuditVersionMaximumLength = 32;

    /// <summary>
    /// Marker appended to an audited value that was longer than its bound.
    /// </summary>
    /// <remarks>
    /// A truncated value must not read as a complete one, or an operator reading the trail would draw a
    /// conclusion from a value that was never submitted in that form.
    /// </remarks>
    private const string AuditTruncationMarker = "...";

    /// <summary>
    /// Largest content, in characters, that an export will carry out of a module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One mebibyte of characters, which is the API's own request-body allowance restated here: that
    /// figure is the largest content this application is willing to move in either direction, and an
    /// export beyond it could not be handed back to the import endpoint anyway. The Application layer
    /// cannot name the hosting constant that declares it - this project references the Domain and
    /// nothing else - so the figure is restated rather than imported, and the two must be changed
    /// together.
    /// </para>
    /// <para>
    /// The ceiling exists because an export payload arrives from MODULE code rather than from the
    /// caller's request, so no request-body limit bounds it, and every step after it - the document
    /// assembly and the well-formedness parse that checks it - is proportional to its length. It is three orders of magnitude above the few
    /// kilobytes an administration module's content actually runs to.
    /// </para>
    /// </remarks>
    private const int ExportPayloadMaximumLength = 1024 * 1024;

    /// <summary>
    /// Page size that requests every match unpaged, per the repository contracts.
    /// </summary>
    private const int UnpagedPageSize = 0;

    /// <summary>
    /// Property the module listing orders by when a caller names none, reproducing the order the legacy
    /// module settings screen presented.
    /// </summary>
    private const string DefaultModuleSortProperty = "ModuleTitle";

    /// <summary>
    /// Attribution used when an import arrives without an authenticated caller.
    /// </summary>
    /// <remarks>
    /// <c>dbo.Users.UserID</c> is <c>IDENTITY(1,1)</c>, so no real account can own this value and it
    /// cannot be mistaken for one. Zero is not used because zero is a legitimate identifier elsewhere
    /// in this schema.
    /// </remarks>
    private const int UnattributedUserId = -1;

    /// <summary>
    /// Greatest length of a version string this service will carry into an audit record.
    /// </summary>
    /// <remarks>
    /// The value is read from an attribute of a caller-supplied document, so it is neither validated by
    /// the request contract nor bounded by the schema. A version is a handful of characters in every
    /// real package, so a ceiling this generous can only be exceeded deliberately.
    /// </remarks>
    private const int MaximumAuditedVersionLength = 32;

    /// <summary>
    /// Recorded in place of a declared version that is not a plain version string.
    /// </summary>
    private const string UnusableVersion = "(unusable)";

    /// <summary>
    /// Resource type recorded on every module audit event, naming what the identifier identifies.
    /// </summary>
    private const string ModuleResourceType = "Module";

    private readonly IModuleRepository _modules;
    private readonly IModuleDefinitionRepository _definitions;
    private readonly ITabRepository _tabs;
    private readonly IPortalRepository _portals;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionService _permissions;
    private readonly IModuleBusinessControllerFactory _businessControllers;
    private readonly IAuditSink _audit;
    private readonly CachingOptions _caching;

    /// <summary>
    /// Initialises the service with the collaborators it reaches the store and the cache through.
    /// </summary>
    /// <param name="modules">Module, placement and settings persistence.</param>
    /// <param name="definitions">Definition catalogue and per-portal availability.</param>
    /// <param name="tabs">Page lookups, needed to validate placements and to fan out across pages.</param>
    /// <param name="portals">Portal existence and the administrative page identifier.</param>
    /// <param name="unitOfWork">The single commit point for every write below.</param>
    /// <param name="cache">Cache reads and invalidation.</param>
    /// <param name="currentUser">
    /// The caller. Used for attribution on a content import and, on the two mutations whose target is named
    /// in the request body rather than in the route, as the subject of the permission question below.
    /// </param>
    /// <param name="permissions">
    /// Evaluates whether the caller may edit the page a module is being created on, or the module content is
    /// being imported into. Those two targets arrive in the request BODY, so no route-based authorisation
    /// policy can reach them and the check has to happen here, after binding.
    /// </param>
    /// <param name="businessControllers">Resolution of a module's own portable-content contract.</param>
    /// <param name="audit">
    /// The audit trail. Written to for the changes whose effect reaches beyond the addressed module and
    /// for both directions of content movement, which is where the legacy application kept a record and
    /// where this contract promises one.
    /// </param>
    /// <param name="caching">
    /// Bound caching configuration supplying the performance multiplier. Taken as the bound instance
    /// rather than through an options wrapper because this project's package surface does not include
    /// one, as recorded at the head of this file.
    /// </param>
    public ModuleService(
        IModuleRepository modules,
        IModuleDefinitionRepository definitions,
        ITabRepository tabs,
        IPortalRepository portals,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        ICurrentUser currentUser,
        IPermissionService permissions,
        IModuleBusinessControllerFactory businessControllers,
        IAuditSink audit,
        CachingOptions caching)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _businessControllers = businessControllers ?? throw new ArgumentNullException(nameof(businessControllers));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One row is emitted per placement, so a module placed on four pages contributes four rows, each
    /// carrying its own placement identifier. A module with no placement at all contributes no row,
    /// which is what the legacy join-based read did too.
    /// </para>
    /// <para>
        /// THE WINDOW IS TAKEN OVER THE PLACEMENT ROWS, WHICH ARE THE UNIT THIS LISTING RETURNS, so
        /// <c>totalCount</c> is the exact number of rows the whole filtered collection holds and
        /// <c>pageSize</c> is exactly the width the caller asked for. <see cref="ReadPlacementRowsAsync"/>
        /// therefore returns the whole filtered, ordered ROW set rather than a page of it, and the
        /// expansion from modules to rows happens before the window is cut.
        /// </para>
        /// <para>
        /// MIGRATION: THIS REPLACES A PAGING CONTRACT THAT WAS ARITHMETICALLY UNSOUND, and the unsoundness is
        /// worth recording because the shape of it is easy to reintroduce. The window used to be taken over
        /// MODULES and the rows emitted were PLACEMENTS, so the two figures the envelope publishes were not in
        /// the unit of the thing being counted. A caller asking for one row could receive four - every placement
        /// of the one module the window admitted - and the metadata was then patched with
        /// <c>Math.Max(total, rows)</c> and <c>Math.Max(pageSize, rows)</c> to stop the envelope's own guards
        /// rejecting the mismatch. That made <c>totalCount</c> a lower bound rather than a count, made
        /// <c>pageSize</c> a value the caller never sent, and made <c>totalPages</c> - which is computed from
        /// both - unstable: the same collection reported a different page count depending on which page was
        /// asked for, so a pager built from the envelope could not enumerate the collection. The exact total
        /// costs nothing per module to obtain: composing the row set takes a FIXED number of reads whatever
        /// the window is - the tenant's modules, then the placements of every module that survived the
        /// filters in ONE set-based read, or none at all when a page was named because that page's own
        /// placement read already holds them, and then the definition names, and only when the window has a
        /// row in it to name. Nothing is read per module. The reads remain bounded by the tenant, as the
        /// unpaged module read above it already was.
        /// </para>
        /// <para>
        /// Row order is total and deterministic: the caller's chosen module ordering first - defaulting to
        /// title then key - and within each module its placements by page, then position, then placement
        /// identifier. Only the MODULE ordering is caller-selectable; a name outside
        /// <c>SortableFields.Modules</c> is REFUSED with <c>module.request_invalid</c> rather than accepted and
        /// ignored, because accepting it would return a page the caller believes was ordered and cannot tell was
        /// not. The placement tie-break is fixed and is not offered, for the reasons that field set records: a
        /// placement position belongs to one pane of one page and carries the append sentinel, so ordering a
        /// cross-page listing by it would sort unrelated positions against each other.
    /// </para>
    /// </remarks>
    public async Task<Result<PagedResult<ModuleListItemDto>>> ListModulesAsync(
        int portalId,
        PagedRequest request,
        int? tabId = null,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidatePagedRequest(request) is ResultReason invalid)
        {
            return Result<PagedResult<ModuleListItemDto>>.Failure(invalid);
        }

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PagedResult<ModuleListItemDto>>.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        // The complete ordered row set, in the unit the response carries. Both the window below and the
        // total it is described by are taken from this one sequence, so the two cannot disagree.
        IReadOnlyList<ModulePlacement> rows = await ReadPlacementRowsAsync(
            portalId,
            tabId,
            includeDeleted,
            request,
            cancellationToken).ConfigureAwait(false);

        bool unpaged = request.PageSize == UnpagedPageSize;

        IReadOnlyList<ModulePlacement> window = unpaged
            ? rows
            : rows
                .Skip(Paging.SkipCount(request.PageIndex, request.PageSize))
                .Take(request.PageSize)
                .ToList();

        // Read once, and only when there is a row to name. A window that lands past the end of the
        // collection projects nothing, so it asks the definition catalogue nothing either.
        IReadOnlyDictionary<int, string> friendlyNames = window.Count == 0
            ? new Dictionary<int, string>()
            : await ReadDefinitionNamesAsync(portalId, cancellationToken).ConfigureAwait(false);

        var items = new List<ModuleListItemDto>(window.Count);
        foreach (ModulePlacement row in window)
        {
            items.Add(ModuleMappings.ToListItem(
                row.Module,
                row.Placement,
                ResolveFriendlyName(row.Module, friendlyNames)));
        }

        // The envelope is handed the figures unaltered: the total is the number of rows in the whole
        // collection and the declared size is the size the caller asked for. Neither is widened to
        // accommodate the projection, because the projection is what the window was taken over.
        return Result<PagedResult<ModuleListItemDto>>.Success(
            unpaged
                ? PagedResult<ModuleListItemDto>.Unpaged(items)
                : PagedResult<ModuleListItemDto>.Create(
                    items,
                    rows.Count,
                    request.PageIndex,
                    request.PageSize));
    }

    /// <summary>
    /// Composes the listing's complete row set - one row per placement - after applying the recycle-bin,
    /// page and title filters and the module ordering.
    /// </summary>
    /// <param name="portalId">The tenant whose modules are read.</param>
    /// <param name="tabId">Restrict to the modules placed on one page, or <see langword="null"/> for the whole tenant.</param>
    /// <param name="includeDeleted">Whether modules already in the recycle bin are included.</param>
        /// <param name="request">The paging request supplying the ordering and the optional title query.</param>
        /// <param name="cancellationToken">Token observed for cancellation.</param>
        /// <returns>
        /// Every row the request selects, in the order the response must carry them: the modules in their
        /// chosen order, and within each module its placements by page, then position, then placement key.
        /// The page window is NOT applied here - the caller takes it over these rows, which is what keeps the
        /// window and the answer in the same unit.
        /// </returns>
        /// <remarks>
        /// MIGRATION: the legacy module block of the data provider carries no paging member of any kind, so
        /// none is invented on the repository contract; the filters and the ordering are composed here,
        /// in the layer that owns the paging request, and the window is cut by the caller over what this
        /// returns. The predicates are applied in the same order and with the same meaning the single legacy
        /// query had.
    /// <para>
    /// NO WINDOW IS TAKEN HERE, AND THAT IS THE POINT OF THIS MEMBER'S SHAPE. The rows the listing returns
    /// are PLACEMENTS, and a module contributes one row per placement, so a window cut over modules is a
    /// window in the wrong unit - which is exactly the defect that made the published paging metadata
    /// unsound. The caller cuts its window over the expanded rows instead, which is why this returns the
    /// whole ordered set rather than a <c>PagedResult</c>. The ordering still happens HERE, before the
    /// expansion, so it orders the collection rather than one arbitrary page of it.
    /// </para>
    /// <para>
    /// The page filter is answered by the page repository rather than by reading each candidate module's
    /// placements in turn. "Which modules sit on this page" is a page-centric question, it belongs to
    /// that contract by the same ownership split that keeps placement mutation on the module contract,
    /// and it resolves in one read instead of one per candidate. That single read is returned alongside the
    /// modules so the row expansion can reuse it and the page repository is consulted exactly once.
    /// </para>
    /// <para>
    /// The placements the rows are built from are read SET-BASED, exactly once. When a page was named,
    /// that page's own placement read already holds every placement the rows can be drawn from, so it is
    /// reused rather than asked for again and no second read happens at all. When no page was named, one
    /// batched read covers every module that survived the filters. Neither shape reads per module, which
    /// is the property that keeps the cost of this listing independent of how many modules the tenant
    /// holds.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ModulePlacement>> ReadPlacementRowsAsync(
        int portalId,
        int? tabId,
        bool includeDeleted,
        PagedRequest request,
        CancellationToken cancellationToken)
    {
        IEnumerable<Module> candidates =
            await _modules.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        if (!includeDeleted)
        {
            candidates = candidates.Where(module => !module.IsDeleted);
        }

        IReadOnlyList<TabModule>? placementsOnNamedPage = null;

        if (tabId is int addressedTab)
        {
            // The presence of a value selects the filter, never its magnitude: TabID is IDENTITY(0, 1),
            // so a page identifier of zero is a real page and must not be read as "unspecified".
            placementsOnNamedPage =
                await _tabs.GetTabModulesAsync(addressedTab, cancellationToken).ConfigureAwait(false);

            HashSet<int> placedModuleIds = placementsOnNamedPage
                .Select(placement => placement.ModuleId)
                .ToHashSet();

            candidates = candidates.Where(module => placedModuleIds.Contains(module.ModuleId));
        }

        if (request.HasQuery)
        {
            // ModuleTitle is nullable, so the null test precedes the comparison. The match stays
            // case-insensitive, as the lower-cased legacy comparison was.
            string wanted = request.Query!.Trim();
            candidates = candidates.Where(module =>
                module.ModuleTitle is not null
                && module.ModuleTitle.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        List<Module> ordered = ApplyOrder(candidates, request).ToList();

        // One read for every row, or none when a page was named and its placements are already in hand.
        // An empty candidate set asks for nothing: the batched read short-circuits, but not issuing the
        // call at all is clearer about the intent.
        IReadOnlyList<TabModule> placements;
        if (placementsOnNamedPage is not null)
        {
            placements = placementsOnNamedPage;
        }
        else if (ordered.Count == 0)
        {
            placements = Array.Empty<TabModule>();
        }
        else
        {
            placements = await _modules
                .GetTabModulesByModuleIdsAsync(
                    ordered.Select(module => module.ModuleId).ToList(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var placementsByModuleId = new Dictionary<int, List<TabModule>>();
        foreach (TabModule placement in placements)
        {
            if (!placementsByModuleId.TryGetValue(placement.ModuleId, out List<TabModule>? group))
            {
                group = new List<TabModule>();
                placementsByModuleId[placement.ModuleId] = group;
            }

            group.Add(placement);
        }

        var rows = new List<ModulePlacement>(ordered.Count);
        foreach (Module module in ordered)
        {
            // A module placed nowhere contributes no row, which is what the legacy join-based read did.
            if (!placementsByModuleId.TryGetValue(module.ModuleId, out List<TabModule>? modulePlacements))
            {
                continue;
            }

            foreach (TabModule placement in OrderPlacements(modulePlacements, tabId))
            {
                rows.Add(new ModulePlacement(module, placement));
            }
        }

        return rows;
    }

    /// <summary>
    /// Applies the caller's chosen ordering to the narrowed module set, before the page is taken.
    /// </summary>
    /// <param name="candidates">The modules that survived the deletion, page and title filters.</param>
    /// <param name="request">The paging request carrying the sort field and its direction.</param>
    /// <returns>The ordered module set.</returns>
    /// <remarks>
    /// An ordering is applied unconditionally, including when the caller names nothing and when the
    /// caller names something this listing does not recognise, and every ordering ends on the primary key
    /// so the order is total. Ordering happens here rather than after the page has been taken, because a
    /// listing that ordered a page it had already cut would only be re-ordering the rows that one
    /// arbitrary page happened to contain.
    /// </remarks>
    // MIGRATION: the arms below are exactly the five names the boundary admits for this collection,
    // declared as Modules in Application/Validation/SortableFields.cs and enforced by the sealed
    // ModulePagedRequestValidator, and each one is a projected member of ModuleListItemDto. That
    // correspondence has to hold in both directions: a name the boundary admits without an arm here is a
    // field the listing accepts and then ignores, and an arm without a permitted name is unreachable.
    //
    // Two members of the projection are deliberately NOT sortable, and their absence is measured rather
    // than accidental. ModuleOrder is a per-pane placement position rather than a listing order: it is a
    // column of dbo.TabModules and not of dbo.Modules, it is only meaningful within one pane of one page,
    // and it carries the append sentinel -1 - so ordering a cross-page module listing by it would sort
    // unrelated positions against each other and put every pending append first. DisplayTitle is derived
    // at projection time from the module title and its definition's friendly name, so it exists only
    // after this ordering has run; sorting by it would require the derivation to move into the read.
    //
    // The default arm reproduces the order this listing has always had, the module title compared
    // case-insensitively. Note that ModuleTitle is nullable, so an untitled module sorts first ascending
    // and last descending; that is the framework comparer's own behaviour and is left as it is, because
    // grouping the untitled modules together at one end is the only ordering that carries information.
    private static IEnumerable<Module> ApplyOrder(IEnumerable<Module> candidates, PagedRequest request)
    {
        bool descending = request.SortDir == SortDirection.Descending;
        string property = request.HasSort ? request.SortBy!.Trim() : DefaultModuleSortProperty;

        return property.ToUpperInvariant() switch
        {
            "MODULEID" => descending
                ? candidates.OrderByDescending(module => module.ModuleId)
                : candidates.OrderBy(module => module.ModuleId),
            "ISDELETED" => descending
                ? candidates.OrderByDescending(module => module.IsDeleted)
                    .ThenByDescending(module => module.ModuleId)
                : candidates.OrderBy(module => module.IsDeleted).ThenBy(module => module.ModuleId),
            "STARTDATE" => descending
                ? candidates.OrderByDescending(module => module.StartDate)
                    .ThenByDescending(module => module.ModuleId)
                : candidates.OrderBy(module => module.StartDate).ThenBy(module => module.ModuleId),
            "ENDDATE" => descending
                ? candidates.OrderByDescending(module => module.EndDate)
                    .ThenByDescending(module => module.ModuleId)
                : candidates.OrderBy(module => module.EndDate).ThenBy(module => module.ModuleId),
            _ => descending
                ? candidates
                    .OrderByDescending(module => module.ModuleTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(module => module.ModuleId)
                : candidates
                    .OrderBy(module => module.ModuleTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(module => module.ModuleId),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// A module the portal does not own, and a module with no placement to address, both read as an
    /// absence rather than a failure. Naming a placement that belongs to another module is a different
    /// matter and is reported, because the caller supplied an identifier that does not fit.
    /// </remarks>
    public async Task<Result<ModuleDetailDto?>> GetModuleAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result<ModuleDetailDto?>.Success(null);
        }

        Result<TabModule?> resolved = await ResolvePlacementAsync(
            module,
            tabModuleId,
            PlacementNotFoundCode,
            cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            return Result<ModuleDetailDto?>.Failure(resolved.Error!);
        }

        if (resolved.Value is not TabModule placement)
        {
            return Result<ModuleDetailDto?>.Success(null);
        }

        IReadOnlyDictionary<int, string> friendlyNames =
            await ReadDefinitionNamesAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<ModuleDetailDto?>.Success(
            ModuleMappings.ToDetail(module, placement, ResolveFriendlyName(module, friendlyNames)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The module and the placement it was created with are written as one unit of work, so neither can
    /// outlive the other. When the request asks for every page, one further placement is written per
    /// page in the same commit.
    /// </para>
    /// <para>
    /// "Every page" means every content page. The legacy screen fanned out over
    /// <c>PortalSettings.DesktopTabs</c> (ModuleSettings.ascx.vb:L410), which is the portal's non
    /// administrative page set, and an administrative page acquiring a content module was never a
    /// reachable outcome. The same classification the page service uses is applied here: a page is
    /// administrative when it is the portal's administration page or a child of it.
    /// </para>
    /// <para>
    /// A definition the portal cannot use is indistinguishable from one that does not exist, because
    /// the definition catalogue is already restricted to the definitions granted to the portal. That
    /// keeps a premium module in another tenant's grant list from being discovered by probing.
    /// </para>
    /// </remarks>
    public async Task<Result<ModuleDetailDto>> CreateModuleAsync(
        int portalId,
        CreateModuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        ModuleDefinition? definition = definitions
            .FirstOrDefault(candidate => candidate.ModuleDefinitionId == request.ModuleDefId);

        DesktopModule? package = definition is null
            ? null
            : await _definitions
                .GetDesktopModuleByIdAsync(definition.DesktopModuleId, cancellationToken)
                .ConfigureAwait(false);

        // SEC-007: the repository is the primary enforcement point, but the service independently refuses
        // an administrative package so a stale cache, alternate repository implementation or future query
        // regression cannot turn host administration functionality into portal-placeable content.
        if (definition is null || package is null || package.IsAdmin)
        {
            return Result<ModuleDetailDto>.Failure(
                DefinitionNotFoundCode,
                FormattableString.Invariant(
                    $"Module definition {request.ModuleDefId} does not exist or is not available to portal {portalId}."));
        }

        Tab? tab = await _tabs.GetByIdAsync(request.TabId, cancellationToken).ConfigureAwait(false);
        if (tab is null || tab.PortalId != portalId)
        {
            return Result<ModuleDetailDto>.Failure(
                TabNotFoundCode,
                FormattableString.Invariant($"Page {request.TabId} does not belong to portal {portalId}."));
        }

        // The caller must hold the edit grant on the page they are placing a module on. Verified here rather
        // than by a policy because the page arrives in the BODY, which no route-reading policy can see;
        // tenant ownership alone is not sufficient, or any authenticated caller could place a module on any
        // tenant's page. When the request also
        // asks for every page, the grant on the addressed page is what authorises the fan-out, exactly as the
        // legacy screen's "all pages" switch was reached from one page already in edit mode.
        if (await EnsureMayEditPageAsync(portalId, request.TabId, cancellationToken).ConfigureAwait(false)
            is ResultReason forbidden)
        {
            return Result<ModuleDetailDto>.Failure(forbidden);
        }

        Module module = ModuleMappings.ToNewModule(portalId, request);
        TabModule placement = ModuleMappings.ToNewPlacement(request);

        // The submitted position may be the append instruction rather than a position, and the mapping
        // carries it through verbatim because a mapping cannot read the pane it would have to resolve
        // against. It is resolved here, before anything is staged, so the instruction is consumed by this
        // layer and never reaches the column.
        placement.ModuleOrder = await ResolvePositionAsync(
            placement.TabId,
            placement.PaneName,
            request.ModuleOrder,
            cancellationToken).ConfigureAwait(false);

        module.TabModules.Add(placement);

        var affectedTabIds = new HashSet<int> { placement.TabId };

        if (request.AllTabs)
        {
            foreach (Tab target in await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false))
            {
                if (target.TabId == placement.TabId)
                {
                    continue;
                }

                TabModule additional = ModuleMappings.ToNewPlacement(request);
                additional.TabId = target.TabId;

                // Resolved against the TARGET page's pane rather than copied from the addressed one,
                // because the legacy resolver was keyed on (TabId, PaneName): appending to one pane says
                // nothing about where the bottom of another page's pane is. No two placements created
                // here share a page, so each read sees a settled pane even though none of them is saved
                // yet.
                additional.ModuleOrder = await ResolvePositionAsync(
                    target.TabId,
                    additional.PaneName,
                    request.ModuleOrder,
                    cancellationToken).ConfigureAwait(false);

                module.TabModules.Add(additional);
                affectedTabIds.Add(target.TabId);
            }
        }

        await _modules.AddAsync(module, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidatePlacements(affectedTabIds);

        return Result<ModuleDetailDto>.Success(
            ModuleMappings.ToDetail(module, placement, definition.FriendlyName));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This member carries the module's own change plus up to four wider effects, and all of them
    /// commit together.
    /// </para>
    /// <para>
    /// THE REQUEST'S PAGE KEY SELECTS THE PLACEMENT, which is what makes a full replacement address one
    /// occurrence of a module rather than an arbitrary one. A module that is not placed on that page is
    /// reported as a placement not found, not amended elsewhere; the reasoning is recorded at the point the
    /// selection happens.
    /// </para>
    /// <para>
    /// Naming the module as the portal default writes the two settings rows the legacy
    /// <c>UpdateSiteSetting</c> wrote, against the portal's own settings module instance. A portal
    /// without that instance is not a failure - no reason code is documented for it and refusing the
    /// whole save would be disproportionate - so the update succeeds and says so on the result.
    /// </para>
    /// <para>
    /// Propagating appearance copies alignment, colour, border, icon, visibility, container, and the
    /// three display switches onto every other module placement on a content page, exactly as
    /// ModuleController.UpdateModule did, while leaving each target's own position, pane and caching
    /// period alone.
    /// </para>
    /// <para>
    /// Switching the all-pages flag also moves placements, which the legacy screen performed after the
    /// module row was written, with the comment that the controller assumes every module update has
    /// already been carried out. Turning it on places the module on the content pages it is missing
    /// from; turning it off removes every placement other than the addressed one, together with that
    /// placement's own settings.
    /// </para>
    /// <para>
    /// Changing the selected page while the resulting module is not an all-pages module performs the
    /// legacy move: the addressed placement and its placement-scoped settings are copied to the selected
    /// page, then the source placement is removed. The move is staged with every other effect and committed
    /// once, so a failure cannot leave copies on both pages or on neither page.
    /// </para>
    /// </remarks>
    public async Task<Result<ModuleDetailDto?>> UpdateModuleAsync(
        int portalId,
        int moduleId,
        UpdateModuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result<ModuleDetailDto?>.Success(null);
        }

        // THE PLACEMENT IS SELECTED BY THE PAGE THE REQUEST NAMES, and this is the one member of the update
        // contract whose omission cannot be recovered from.
        //
        // MIGRATION: THE SUBMITTED PAGE KEY USED TO BE READ BY NOTHING AT ALL. UpdateModuleRequest.TabId is
        // documented as required and as the key identifying WHICH placement is being updated, and
        // ModuleMappings.ApplyUpdate documents that it deliberately does not assign the value because "the
        // application service resolves the placement before calling here" - yet this method resolved the
        // placement with the lowest TabModuleId and never looked at the request. For a module on one page the
        // two agree by accident; for a module on several the caller's choice of page was DISCARDED and the
        // edit landed on whichever placement happened to have been created first. A caller editing the
        // instance on page four saw its own submission apparently accepted and page one silently rewritten,
        // with the response describing the placement it had not addressed. Nothing in the contract, the
        // projection or the response revealed it.
        //
        // Refusing rather than falling back is deliberate. A module that is not on the named page has no
        // placement for this request to update, and quietly amending a different one is precisely the
        // behaviour being removed - so the outcome is the documented placement-not-found reason, whose token
        // the shared status table answers with 404. That is the same status the previous absence produced, so
        // no caller sees a new class of failure; what changes is that the answer now names why.
        TabModule? placement = await ReadPlacementOnPageAsync(module, request.TabId, cancellationToken)
            .ConfigureAwait(false);

        if (placement is null)
        {
            return Result<ModuleDetailDto?>.Failure(
                PlacementNotFoundCode,
                FormattableString.Invariant(
                    $"Module {moduleId} is not placed on page {request.TabId} in portal {portalId}."));
        }

        bool wasPlacedEverywhere = module.AllTabs;

        // SEC: THE ADMINISTRATOR-ONLY FIELDS ARE AUTHORISED HERE, BEFORE ANY OF THEM IS APPLIED.
        // The legacy settings screen disabled cboTab, chkAllTabs, chkDefault and chkAllModules outright
        // for any caller outside the portal administrator role, at both ModuleSettings.ascx.vb:L214-L219
        // and :L332-L338, under the comment that tab administrators can only manage their own tab. That is
        // a rule about four FIELDS of this request rather than about reaching this route, so no [Authorize]
        // attribute can express it - the route's policy admits a page administrator by design, which is
        // correct, because a page administrator may legitimately edit the module in front of them.
        //
        // Until this gate existed the rule was documented in three places and enforced in none: the DTO
        // said authorisation would decide, the controller said the service owned the rule and reported a
        // distinct 403, and the service applied all four unconditionally. A caller holding only the module
        // edit grant on one page could therefore move a module to a page they do not administer, fan it out
        // across every page of the portal, name it as the portal's default, or rewrite the appearance of
        // every module on every content page - four portal-wide effects from a page-scoped grant.
        //
        // The gate is a DELTA test, not a presence test, and the distinction is what keeps ordinary edits
        // working. A non-administrator submitting the module's stored AllTabs value with both intent flags
        // unset is changing none of them, so nothing administrator-only is being applied and the request
        // proceeds. Only an actual change - a flipped fan-out flag, or either instruction asked for -
        // requires the authority. Reading it as a presence test would refuse every non-administrator save.
        //
        // MIGRATION: THE PAGE IS NOT ONE OF THE GATED FIELDS HERE, THOUGH THE REVISION THAT WROTE THIS GATE
        // COUNTED IT AS THE FOURTH. That revision read the submitted page as a MOVE command and gated the
        // move on portal administration. The surviving contract reads it as the SELECTOR of the placement
        // being edited, so a submitted page that differs from the placement's own page does not relocate
        // anything - it is refused before this gate is reached, with the placement-not-found reason. Keeping
        // a move term here would be a test that can never be true, and a reader would take its presence as
        // evidence that a move exists. Three portal-wide effects remain and are gated; see the note on the
        // withdrawn move below.
        if (request.AllTabs != module.AllTabs
            || request.SetAsDefaultSettings
            || request.ApplyToAllModules)
        {
            if (await EnsureAdministersPortalAsync(portalId, cancellationToken).ConfigureAwait(false)
                is ResultReason forbidden)
            {
                // Returned BEFORE the projection runs, so not one of the four values has touched the
                // tracked entities and there is nothing staged for a later commit to pick up. A refusal
                // that had already mutated the graph would depend on nobody calling SaveChanges afterwards,
                // which is not a property this method can guarantee for its callers.
                return Result<ModuleDetailDto?>.Failure(forbidden);
            }
        }

        // SEC: THE CALLER MUST HOLD THE EDIT GRANT ON THE PAGE WHOSE PLACEMENT IT IS EDITING, which is the
        // page the request named and the placement was selected by. The revision that read the page as a
        // move destination required this grant on the destination, citing the legacy page picker only ever
        // offering pages the caller could edit; under the selecting contract the destination and the edited
        // page are the same page, so the requirement survives its withdrawn condition and applies to every
        // update rather than only to a move. Without it, a caller holding the grant on page one could edit
        // the placement the module has on page four by naming page four, because the route's own policy
        // admits any page administrator of the tenant. The page's tenant needs no separate test: the
        // placement was found on it for a module already proven to belong to this portal.
        if (await EnsureMayEditPageAsync(portalId, request.TabId, cancellationToken).ConfigureAwait(false)
            is ResultReason pageForbidden)
        {
            // Returned BEFORE the projection runs, for the same reason the field gate above returns early:
            // nothing has touched the tracked entities, so a later commit by any caller cannot pick up a
            // half-applied refusal.
            return Result<ModuleDetailDto?>.Failure(pageForbidden);
        }

        ModuleMappings.ApplyUpdate(module, placement, request);

        // NO MOVE IS DERIVED HERE, AND NONE CAN BE. The submitted page SELECTS the placement above, so by
        // the time control reaches this line the placement's page and the submitted page are the same value
        // by construction - a request naming a page the module does not occupy was already refused with the
        // placement-not-found reason. An earlier revision tested the two for inequality and reproduced the
        // legacy MoveModule copy-then-delete when they differed; that test could never be true once the page
        // became the selector, so the branch was unreachable and is withdrawn rather than left to read as a
        // feature. Reinstating a move needs a second member naming the destination, because one page
        // identifier cannot be both the placement being edited and the page it should end up on. The
        // divergence from the legacy screen is recorded in MIGRATION_NOTES.md.
        //
        // The projection above assigned the submitted position verbatim, which may be the append
        // instruction, so it is resolved against the page the placement sits on.
        placement.ModuleOrder = await ResolvePositionAsync(
            placement.TabId,
            placement.PaneName,
            request.ModuleOrder,
            cancellationToken).ConfigureAwait(false);

        // BOTH pages are invalidated on a move, not just the destination. The placement list of the page
        // the module left is now wrong too, and an entry that keeps answering with a module that is no
        // longer there is the more visible of the two staleness bugs.
        // ONE PAGE IS AFFECTED, NOT TWO. The revision that assigned the page collected both the page being
        // left and the page being joined here, which is correct for a move and misleading without one: under
        // the selecting contract the placement never changes page, so the second entry was always the same
        // value as the first.
        var affectedTabIds = new HashSet<int> { placement.TabId };
        var effects = new List<string>();

        if (!wasPlacedEverywhere && module.AllTabs)
        {
            int added = await PlaceOnContentTabsAsync(
                portalId,
                module,
                placement,
                request.ModuleOrder,
                affectedTabIds,
                cancellationToken).ConfigureAwait(false);
            if (added > 0)
            {
                effects.Add(FormattableString.Invariant($"placed on {added} further page(s)"));
            }
        }
        else if (wasPlacedEverywhere && !module.AllTabs)
        {
            int removed = await WithdrawFromOtherTabsAsync(module, placement, affectedTabIds, cancellationToken)
                .ConfigureAwait(false);
            if (removed > 0)
            {
                effects.Add(FormattableString.Invariant($"withdrawn from {removed} further page(s)"));
            }
        }

        // MIGRATION: renamed from the legacy IsDefaultModule, whose name read as persisted state although it
        // was excluded from the legacy object's serialisation and carried no column. The target name follows
        // the wording the product showed its users, ModuleSettings.ascx.resx plDefault.Text, "Set As Default
        // Settings?". It remains intent rather than state: the write below targets PORTAL configuration, and
        // the portal comes from the per-request context rather than from the request body.
        if (request.SetAsDefaultSettings)
        {
            bool recorded = await NameAsPortalDefaultAsync(
                portalId,
                module.ModuleId,
                placement.TabId,
                cancellationToken).ConfigureAwait(false);

            effects.Add(recorded
                ? "named as the portal default module"
                : FormattableString.Invariant(
                    $"could not be named as the portal default because portal {portalId} has no \"{SiteSettingsDefinitionName}\" module instance"));
        }

        // MIGRATION: renamed from the legacy AllModules, whose name read as a COLLECTION of modules rather
        // than as an instruction about them, following ModuleSettings.ascx.resx plAllModules.Text, "Apply To
        // All Modules?".
        //
        // MIGRATION: the propagation this triggers is NARROWER than the legacy's. The legacy loop
        // copied NINE appearance values - alignment, colour, border, icon, visibility, container source and
        // the title, print and syndicate flags - to every module on every non-administrative page. SIX of
        // those nine are excluded from UpdateModuleRequest as pane-layout, rendering or skinning concerns,
        // leaving only the icon, the visibility and the container-display flag propagable. That is a
        // documented functional reduction rather than an oversight, and it follows mechanically from the
        // exclusions. This is still the only path in the module API that writes rows outside the addressed
        // module, so its portal-wide reach is why it is administrator-gated by policy.
        if (request.ApplyToAllModules)
        {
            int copied = await PropagateAppearanceAsync(portalId, placement, affectedTabIds, cancellationToken)
                .ConfigureAwait(false);
            effects.Add(FormattableString.Invariant($"appearance copied to {copied} placement(s) on content pages"));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidatePlacements(affectedTabIds);

        // The record is written only when the change reached beyond the addressed module, which is the
        // condition this contract documents: naming the module as the portal default writes portal-level
        // keys, and propagating appearance rewrites placements the caller never named. An ordinary edit of
        // one module is not recorded, because a trail that logs every field edit stops being a record of
        // consequential change and becomes a change log - and the legacy application logged neither.
        //
        // Emitted after the commit, so nothing here can describe a change that was rolled back. The facts
        // are the blast radius and the identifiers needed to recognise what moved; no free-text the caller
        // supplied - the title, header and footer - is carried.
        if (effects.Count > 0)
        {
            RecordModuleAudit(
                AuditEventNames.ModuleUpdated,
                portalId,
                module.ModuleId,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Operation"] = "Update",
                    ["TabModuleId"] = placement.TabModuleId.ToString(CultureInfo.InvariantCulture),
                    ["TabId"] = placement.TabId.ToString(CultureInfo.InvariantCulture),
                    ["SetAsDefaultSettings"] = request.SetAsDefaultSettings.ToString(),
                    ["ApplyToAllModules"] = request.ApplyToAllModules.ToString(),
                    ["AffectedTabCount"] = affectedTabIds.Count.ToString(CultureInfo.InvariantCulture),
                    ["EffectCount"] = effects.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        IReadOnlyDictionary<int, string> friendlyNames =
            await ReadDefinitionNamesAsync(portalId, cancellationToken).ConfigureAwait(false);

        ModuleDetailDto detail =
            ModuleMappings.ToDetail(module, placement, ResolveFriendlyName(module, friendlyNames));

        return effects.Count == 0
            ? Result<ModuleDetailDto?>.Success(detail)
            : Result<ModuleDetailDto?>.Success(
                detail,
                new ResultReason(WideEffectCode, string.Join("; ", effects) + "."));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Recycling the module is a soft delete through <c>dbo.Modules.IsDeleted</c>, a
    /// <c>bit NOT NULL</c> column added by the 02.00.00 upgrade script with a default of zero, so the
    /// module and every placement it has survive and can be restored. Removing one placement is a hard
    /// delete of that placement row and of its placement-scoped settings, and leaves the module and its
    /// other placements untouched. Recycling a module that is already recycled succeeds, because a
    /// delete is idempotent.
    /// </remarks>
    public async Task<Result> DeleteModuleAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        var affectedTabIds = new HashSet<int>();

        if (tabModuleId is int addressed)
        {
            TabModule? placement = await _modules.GetTabModuleByIdAsync(addressed, cancellationToken).ConfigureAwait(false);
            if (placement is null || placement.ModuleId != moduleId)
            {
                return Result.Failure(
                    PlacementNotFoundCode,
                    FormattableString.Invariant($"Placement {addressed} does not belong to module {moduleId}."));
            }

            await RemovePlacementAsync(placement, cancellationToken).ConfigureAwait(false);
            affectedTabIds.Add(placement.TabId);
        }
        else
        {
            module.IsDeleted = true;

            IReadOnlyList<TabModule> placements =
                await _modules.GetTabModulesByModuleIdAsync(moduleId, cancellationToken).ConfigureAwait(false);

            foreach (TabModule placement in placements)
            {
                affectedTabIds.Add(placement.TabId);
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        InvalidatePlacements(affectedTabIds);

        // MIGRATION: the legacy recycle bin recorded a module removal as EventLogType.MODULE_DELETED
        // (RecycleBin.ascx.vb:L156) and this boundary recorded NOTHING, which left the one operation in this
        // service that destroys a caller's work as the only one with no trail. The recycle-bin PAGE is
        // excluded by AAP 0.2.2.2; the deletion it audited is not, and this is where the deletion happens.
        //
        // Emitted after the commit, so no record can describe a removal that was rolled back, and the facts
        // are the blast radius: whether one placement or the whole module went, which placement was
        // addressed when one was, and how many pages were affected. No caller free-text is carried.
        RecordModuleAudit(
            AuditEventNames.ModuleDeleted,
            portalId,
            moduleId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = tabModuleId is null ? "Recycle" : "RemovePlacement",
                ["TabModuleId"] = tabModuleId?.ToString(CultureInfo.InvariantCulture),
                ["AffectedTabCount"] = affectedTabIds.Count.ToString(CultureInfo.InvariantCulture),
            });

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Both settings stores are carried: the module-scoped rows, which every placement of the module
    /// shares, and the rows scoped to the addressed placement alone. Anything that cannot be addressed
    /// reads as an absence, because no reason code is documented for this member.
    /// </remarks>
    public async Task<Result<ModuleSettingsDto?>> GetModuleSettingsAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result<ModuleSettingsDto?>.Success(null);
        }

        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package?.IsAdmin == true)
        {
            return Result<ModuleSettingsDto?>.Failure(
                SettingsProtectedCode,
                "Administrative module settings are available only through their typed privileged endpoint.");
        }

        Result<TabModule?> resolved = await ResolvePlacementAsync(
            module,
            tabModuleId,
            mismatchCode: null,
            cancellationToken).ConfigureAwait(false);

        if (resolved.Value is not TabModule placement)
        {
            return Result<ModuleSettingsDto?>.Success(null);
        }

        IReadOnlyList<ModuleSetting> moduleSettings =
            await _modules.GetModuleSettingsAsync(moduleId, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TabModuleSetting> placementSettings =
            await _modules.GetTabModuleSettingsAsync(placement.TabModuleId, cancellationToken).ConfigureAwait(false);

        // Security-owned names are never projected from the generic open-key contract. They are managed by
        // typed portal-administrator endpoints, whose DTOs intentionally expose only their documented fields.
        IReadOnlyList<ModuleSetting> publicModuleSettings = moduleSettings
            .Where(setting => !IsSecurityOwnedSettingName(setting.SettingName))
            .ToList();
        IReadOnlyList<TabModuleSetting> publicPlacementSettings = placementSettings
            .Where(setting => !IsSecurityOwnedSettingName(setting.SettingName))
            .ToList();

        return Result<ModuleSettingsDto?>.Success(
            ModuleMappings.ToSettings(module, placement, publicModuleSettings, publicPlacementSettings));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is a replace-the-set write, not an add, update and delete triad: the caller submits the
    /// whole desired state of each store, the difference against what is stored is computed here, and
    /// the whole difference commits once. A name the caller omitted is therefore deleted.
    /// </para>
    /// <para>
    /// Names are matched without regard to case, which is how the legacy <c>Hashtable</c> behaved and
    /// how the settings projection behaves. When two submitted names differ only in case the last one
    /// wins, for the same reason.
    /// </para>
    /// <para>
    /// Placement-scoped work needs a placement. When none is addressed the module's original placement
    /// is used; when the module has no placement at all and placement-scoped settings were nevertheless
    /// submitted, that is reported rather than silently dropped. Submitting no placement-scoped
    /// settings and naming no placement leaves the placement store alone, because there is no way to
    /// tell which store the caller meant to empty.
    /// </para>
    /// </remarks>
    public async Task<Result> UpdateModuleSettingsAsync(
        int portalId,
        int moduleId,
        int? tabModuleId,
        IReadOnlyDictionary<string, string> moduleSettings,
        IReadOnlyDictionary<string, string> tabModuleSettings,
        CancellationToken cancellationToken = default)
    {
        // MIGRATION: an absent map is REFUSED rather than thrown on. Both members are non-nullable
        // reference types carrying an initialiser on the request contract, which makes null look
        // unreachable - but an initialiser only runs when the deserialiser does not assign, and a body
        // carrying an explicit null assigns over it. Throwing here turned a syntactically valid body into
        // a server fault; the caller is now told which map is missing. The request validator states the
        // same rule at the edge, and this remains as defence in depth for callers it does not front.
        if (moduleSettings is null)
        {
            return Result.Failure(
                SettingInvalidCode,
                "The module settings map is required. Send an empty map to clear every module setting.");
        }

        if (tabModuleSettings is null)
        {
            return Result.Failure(
                SettingInvalidCode,
                "The placement settings map is required. Send an empty map to clear every placement "
                + "setting.");
        }

        // MIGRATION: cardinality is bounded before anything is read or written. Every setting name and
        // value was already bounded individually, but nothing bounded the COUNT, and the two limits
        // multiply: a body well inside the request size limit can carry tens of thousands of short
        // settings, each becoming a tracked entity and a row in one transaction. The bound is checked
        // against the two maps together as well as separately, because the transaction spans both.
        if (moduleSettings.Count > SettingsPerScopeMaximum
            || tabModuleSettings.Count > SettingsPerScopeMaximum)
        {
            return Result.Failure(
                SettingInvalidCode,
                FormattableString.Invariant(
                    $"A settings map must carry no more than {SettingsPerScopeMaximum} entries."));
        }

        if (moduleSettings.Count + tabModuleSettings.Count > SettingsAggregateMaximum)
        {
            return Result.Failure(
                SettingInvalidCode,
                FormattableString.Invariant(
                    $"The two settings maps must carry no more than {SettingsAggregateMaximum} entries")
                + " between them.");
        }

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package?.IsAdmin == true)
        {
            return Result.Failure(
                SettingsProtectedCode,
                "Administrative module settings must be changed through their typed privileged endpoint.");
        }

        // MIGRATION: the two stores are normalised and length-checked SEPARATELY, against their own column
        // width and under their own scope word, because they are separate tables - dbo.ModuleSettings keyed
        // (ModuleID, SettingName) and dbo.TabModuleSettings keyed (TabModuleID, SettingName). The legacy
        // readers at ModuleController.vb L1237 and L1336 each remarked that the other store was excluded,
        // and that separation is preserved here rather than collapsed into one map.
        NormalisedSettings desiredModule =
            NormaliseSettings(moduleSettings, ModuleSettingValueMaximumLength, "module");

        if (desiredModule.Reason is ResultReason moduleSettingInvalid)
        {
            return Result.Failure(moduleSettingInvalid);
        }

        NormalisedSettings desiredPlacement =
            NormaliseSettings(tabModuleSettings, PlacementSettingValueMaximumLength, "placement");

        if (desiredPlacement.Reason is ResultReason placementSettingInvalid)
        {
            return Result.Failure(placementSettingInvalid);
        }

        Dictionary<string, string> desiredModuleSettings = desiredModule.Values;
        Dictionary<string, string> desiredPlacementSettings = desiredPlacement.Values;

        string? protectedName = desiredModuleSettings.Keys
            .Concat(desiredPlacementSettings.Keys)
            .FirstOrDefault(IsSecurityOwnedSettingName);
        if (protectedName is not null)
        {
            return Result.Failure(
                SettingsProtectedCode,
                FormattableString.Invariant(
                    $"The setting name \"{protectedName}\" is reserved for a typed privileged endpoint."));
        }

        TabModule? placement = null;
        if (tabModuleId is int addressed)
        {
            placement = await _modules.GetTabModuleByIdAsync(addressed, cancellationToken).ConfigureAwait(false);
            if (placement is null || placement.ModuleId != moduleId)
            {
                return Result.Failure(
                    NotFoundCode,
                    FormattableString.Invariant($"Placement {addressed} does not belong to module {moduleId}."));
            }
        }
        else
        {
            // No placement was addressed, so the default placement is resolved - the same one a read without
            // an addressed placement projects. Resolving it unconditionally rather than only when the caller
            // submitted something matters: this endpoint's contract is that the submitted set IS the whole
            // set, so an empty set has to be able to clear what is stored. Resolving only when the set was
            // non-empty made a clearing request silently do nothing at the placement scope while doing exactly
            // what it said at the module scope, which is the one shape of request under which the two scopes
            // disagreed.
            Result<TabModule?> resolved = await ResolvePlacementAsync(
                module,
                tabModuleId: null,
                mismatchCode: null,
                cancellationToken).ConfigureAwait(false);

            placement = resolved.Value;

            // A module with no placement at all still cannot hold placement-scoped settings, and a caller that
            // asked to store some is told so rather than having the request quietly succeed. A caller that
            // submitted none has nothing to be told: there is simply nothing to reconcile.
            if (placement is null && desiredPlacementSettings.Count > 0)
            {
                return Result.Failure(
                    SettingInvalidCode,
                    FormattableString.Invariant(
                        $"Module {moduleId} is not placed on any page, so placement-scoped settings cannot be stored."));
            }
        }

        IReadOnlyList<ModuleSetting> storedModuleSettings =
            await _modules.GetModuleSettingsAsync(moduleId, cancellationToken).ConfigureAwait(false);

        var survivingModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleSetting stored in storedModuleSettings)
        {
            // A replace request governs only the generic settings namespace. Omitting a protected setting
            // must not delete it, otherwise an editor could erase administrator-owned security policy simply
            // by submitting an otherwise valid generic settings document.
            if (IsSecurityOwnedSettingName(stored.SettingName))
            {
                continue;
            }

            if (!desiredModuleSettings.TryGetValue(stored.SettingName, out string? desired))
            {
                await _modules
                    .DeleteModuleSettingAsync(stored.ModuleId, stored.SettingName, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            survivingModuleNames.Add(stored.SettingName);
            if (!string.Equals(stored.SettingValue, desired, StringComparison.Ordinal))
            {
                stored.SettingValue = desired;
            }
        }

        foreach (KeyValuePair<string, string> desired in desiredModuleSettings)
        {
            if (survivingModuleNames.Contains(desired.Key))
            {
                continue;
            }

            await _modules
                .AddModuleSettingAsync(
                    new ModuleSetting
                    {
                        ModuleId = moduleId,
                        SettingName = desired.Key,
                        SettingValue = desired.Value,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (placement is not null)
        {
            IReadOnlyList<TabModuleSetting> storedPlacementSettings = await _modules
                .GetTabModuleSettingsAsync(placement.TabModuleId, cancellationToken)
                .ConfigureAwait(false);

            var survivingPlacementNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TabModuleSetting stored in storedPlacementSettings)
            {
                if (IsSecurityOwnedSettingName(stored.SettingName))
                {
                    continue;
                }

                if (!desiredPlacementSettings.TryGetValue(stored.SettingName, out string? desired))
                {
                    await _modules
                        .DeleteTabModuleSettingAsync(stored.TabModuleId, stored.SettingName, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                survivingPlacementNames.Add(stored.SettingName);
                if (!string.Equals(stored.SettingValue, desired, StringComparison.Ordinal))
                {
                    stored.SettingValue = desired;
                }
            }

            foreach (KeyValuePair<string, string> desired in desiredPlacementSettings)
            {
                if (survivingPlacementNames.Contains(desired.Key))
                {
                    continue;
                }

                await _modules
                    .AddTabModuleSettingAsync(
                        new TabModuleSetting
                        {
                            TabModuleId = placement.TabModuleId,
                            SettingName = desired.Key,
                            SettingValue = desired.Value,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (placement is not null)
        {
            _cache.InvalidateModules(placement.TabId);
        }
        else
        {
            IReadOnlyList<TabModule> placements =
                await _modules.GetTabModulesByModuleIdAsync(moduleId, cancellationToken).ConfigureAwait(false);

            foreach (TabModule affected in placements)
            {
                _cache.InvalidateModules(affected.TabId);
            }
        }

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The catalogue is installation-time reference data, so it is read through the cache and no member
    /// of this service creates or modifies a definition. A portal with no definitions available to it
    /// reads as an empty catalogue rather than a failure.
    /// </remarks>
    public async Task<Result<IReadOnlyList<ModuleDefinitionDto>>> ListModuleDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = string.Format(CultureInfo.InvariantCulture, DefinitionCatalogueCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(
            DefinitionCatalogueCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        // MIGRATION: when the configured expiry resolves to zero the legacy callers skipped the
        // database read outright, on the grounds that the query was too expensive to repeat per
        // request. That is not reproduced: disabling caching here disables caching only, and the read
        // still runs, because returning nothing would make a configuration value silently change what
        // the endpoint reports.
        IReadOnlyList<ModuleDefinitionDto> catalogue = expiration > TimeSpan.Zero
            ? await _cache.GetOrCreateAsync(
                cacheKey,
                token => ReadDefinitionCatalogueAsync(portalId, token),
                expiration,
                cancellationToken).ConfigureAwait(false)
            : await ReadDefinitionCatalogueAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<ModuleDefinitionDto>>.Success(catalogue);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answered by NARROWING THE CATALOGUE rather than by reading the definition directly, and the choice
    /// is load-bearing. The catalogue read applies the premium-module rule - a definition is available to a
    /// portal only when its package is not premium or has been granted to that portal - and a direct
    /// repository read by identifier applies nothing at all. Re-implementing the rule here would place a
    /// second copy of it beside the first, and the two copies would eventually disagree about which
    /// definitions a tenant may see, which is a tenant-isolation defect rather than a cosmetic one. It also
    /// reuses the catalogue's cache entry, so a by-identifier read costs no database round trip once the
    /// catalogue is warm; the catalogue is small, bounded reference data written only by installation.
    /// </remarks>
    public async Task<Result<ModuleDefinitionDto?>> GetModuleDefinitionAsync(
        int portalId,
        int moduleDefinitionId,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<ModuleDefinitionDto>> catalogue = await this
            .ListModuleDefinitionsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        if (catalogue.IsFailure)
        {
            return Result<ModuleDefinitionDto?>.Failure(catalogue.Error!);
        }

        // A definition the portal cannot instantiate is reported as ABSENT rather than refused, so the
        // endpoint cannot be used to discover which definitions exist elsewhere in the installation: a
        // caller cannot tell "no such definition" from "not yours" and therefore learns nothing either way.
        ModuleDefinitionDto? definition = catalogue.Value
            .FirstOrDefault(candidate => candidate.ModuleDefId == moduleDefinitionId);

        return Result<ModuleDefinitionDto?>.Success(definition);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Narrows the catalogue for the same reason the single-definition read above does, and consequently
    /// preserves the catalogue's ordering - friendly name, then identifier - rather than imposing an order
    /// of its own. A package that declares nothing, or that the portal has not been granted, yields an
    /// empty sequence, which is a legitimate answer: the caller asked a question with a negative answer
    /// rather than asking an invalid question.
    /// </remarks>
    public async Task<Result<IReadOnlyList<ModuleDefinitionDto>>> ListDesktopModuleDefinitionsAsync(
        int portalId,
        int desktopModuleId,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<ModuleDefinitionDto>> catalogue = await this
            .ListModuleDefinitionsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        if (catalogue.IsFailure)
        {
            return catalogue;
        }

        IReadOnlyList<ModuleDefinitionDto> declared = catalogue.Value
            .Where(candidate => candidate.DesktopModuleId == desktopModuleId)
            .ToList();

        return Result<IReadOnlyList<ModuleDefinitionDto>>.Success(declared);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Portability is decided by the stored capability bit field, which is what the legacy export path
    /// tested: <c>objModule.BusinessControllerClass &lt;&gt; "" And objModule.IsPortable</c>
    /// (Export.ascx.vb:L150). A module whose stored bit says portable but whose controller no
    /// registration covers is reported as not portable too, because in this deployment there is no way
    /// to ask it for content.
    /// </para>
    /// <para>
    /// MIGRATION: the document is returned to the caller instead of being written to a server path.
    /// The legacy screen wrote it beneath the portal home directory under the web root and then
    /// registered it as a portal file; neither the web root nor that file registry exists in the target
    /// container topology. The requested folder is accepted and deliberately unused for that reason,
    /// and the requested file name is validated so the caller can label what it receives.
    /// </para>
    /// <para>
    /// MIGRATION: THE PAYLOAD IS EMBEDDED VERBATIM, AND THE AUTHORITY FOR THAT IS THE MODULE ADMIN
    /// SCREEN RATHER THAN THE PORTAL TEMPLATE WRITER. <c>Website/admin/Modules/Export.ascx.vb</c>
    /// L157-L165 composes the document by concatenation -
    /// <c>"&lt;?xml version=""1.0"" encoding=""utf-8"" ?&gt;" &amp; "&lt;content type=""..." version="..."&gt;" &amp; Content &amp; "&lt;/content&gt;"</c>
    /// - with NO escaping of any kind, and its import counterpart at
    /// <c>Import.ascx.vb</c> L196-L200 reads the payload back as
    /// <c>xmlDoc.DocumentElement.InnerXml</c> with no decoding. This endpoint is the migration of THAT
    /// workflow, so that is the document contract it reproduces.
    /// </para>
    /// <para>
    /// MIGRATION: AN EARLIER REVISION APPLIED <c>HtmlEncode</c> HERE, COPIED FROM THE PORTAL TEMPLATE
    /// PATH, AND IT PRODUCED A DOCUMENT NOTHING BUT ITSELF COULD READ. <c>ModuleController.vb</c> L244
    /// does read <c>Content = HttpContext.Current.Server.HtmlEncode(Content)</c> before wrapping the
    /// result in a CDATA section, and L428 undoes both - but that pair belongs to the portal template
    /// writer and reader, a different format with a different consumer. Applied here it escaped the
    /// payload TWICE: HTML encoding turned <c>&lt;</c> into <c>&amp;lt;</c> and the XML writer then
    /// escaped that ampersand into <c>&amp;amp;lt;</c>. A legacy importer reading such a document, or any
    /// standards-based reader, recovered <c>&amp;lt;item&amp;gt;</c> instead of <c>&lt;item&gt;</c> -
    /// only a round trip through this same service masked it, because only this service knew to undo a
    /// layer the format does not declare. The encode is therefore removed rather than kept, and with it
    /// the matching decode on the import path.
    /// </para>
    /// <para>
    /// MIGRATION: THE CONSEQUENCE OF EMBEDDING VERBATIM IS THAT A MODULE MUST HAND BACK WELL-FORMED XML,
    /// AND ONE THAT DOES NOT IS NOW REFUSED. The portability contract is an XML contract, so this is the
    /// obligation it always carried; what changes is when the breach is discovered. Under the withdrawn
    /// encoding a module returning bare text with an ampersand in it exported "successfully" and could be
    /// re-imported only here. The legacy wrote that text into a file unescaped and the legacy IMPORTER
    /// then refused the file with "The file you selected does not contain a valid XML structure", so the
    /// content was never importable either way - the composed document is validated below and the failure
    /// is reported at the point the caller can act on it instead. Recorded in MIGRATION_NOTES.md.
    /// </para>
    /// <para>
    /// MIGRATION: THE TYPE ATTRIBUTE CARRIES THE SANITISED MODULE NAME, NOT THE RAW ONE. The legacy wrote
    /// <c>CleanName(objModule.ModuleName)</c> (Export.ascx.vb L159, helper at L209-L221), which strips a
    /// fixed punctuation set outright, and its importer compared the attribute against the same sanitised
    /// value - so a document carrying the raw name was refused by the legacy importer as belonging to
    /// another module. Writing the raw name here, as an earlier revision did, made every document this
    /// endpoint produced unreadable by a DotNetNuke 4.x installation for a reason no reader could see.
    /// </para>
    /// <para>
    /// An empty payload still yields a document, because "asked and given nothing" is a real answer that
    /// the caller is entitled to see. This is a documented divergence from the legacy guard at L233,
    /// which wrote a content element only for non-empty content and therefore left the caller unable to
    /// distinguish "no content" from "never asked".
    /// </para>
    /// </remarks>
    public async Task<Result<string>> ExportModuleAsync(
        int portalId,
        int moduleId,
        ModuleExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ModuleExportRequestValidator refuses a blank name at the boundary with a field-keyed answer naming
        // `fileName`, which is what the operation's published 400 schema advertises. This guard is retained
        // rather than removed because this service is reachable from callers the MVC validation filter does
        // not sit in front of - the unit suites call it directly, and so would any future in-process consumer.
        // The wording is identical to the validator's on purpose: one condition enforced at two points must
        // read as one rule, not two, and the pair is annotated in both places.
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return Result<string>.Failure(
                RequestInvalidCode,
                "A file name is required so the returned document can be labelled.");
        }

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result<string>.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package is null || string.IsNullOrWhiteSpace(package.BusinessControllerClass) || !package.IsPortable)
        {
            return Result<string>.Failure(
                NotPortableCode,
                FormattableString.Invariant($"Module {moduleId} does not support content export."));
        }

        Result<string?> exported = await _businessControllers
            .ExportModuleContentAsync(package.BusinessControllerClass, moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (exported.IsFailure)
        {
            return Result<string>.Failure(exported.Error!);
        }

        if (exported.Value is null)
        {
            string advisory = exported.Reason?.Message
                ?? "no registered business controller covers this module.";

            return Result<string>.Failure(
                NotPortableCode,
                FormattableString.Invariant($"Module {moduleId} could not be asked for content: {advisory}"));
        }

        string payload = exported.Value;

        // BOUNDED BEFORE ANY WORK IS DONE ON IT. What the module hands over is not the caller's own
        // submission - it arrives from module code and its size is decided there - so nothing upstream
        // bounds it, and everything below is document assembly and parsing proportional to it. Refusing at a
        // stated ceiling is what keeps one export from allocating an arbitrarily large document, and it is
        // tested here rather than mid-assembly so the refusal costs nothing and is predictable.
        //
        // MIGRATION: the legacy export had no ceiling of any kind - it wrote whatever the module returned to
        // disk - so the ceiling is a deliberate divergence, recorded in MIGRATION_NOTES.md. It is classified
        // as an export failure rather than as a bad request because the content came from the MODULE and not
        // from the caller, which is the same distinction ExportFailedCode already draws.
        if (payload.Length > ExportPayloadMaximumLength)
        {
            return Result<string>.Failure(
                ExportFailedCode,
                FormattableString.Invariant(
                    $"Module {moduleId} returned {payload.Length} characters of content, which exceeds the {ExportPayloadMaximumLength} character ceiling this endpoint will carry."));
        }

        // The caller may already have gone while the module was assembling its content, and what follows is
        // processor and memory work rather than I/O - so the token is observed here, where that work begins.
        // Without this an abandoned request paid for the whole document anyway.
        cancellationToken.ThrowIfCancellationRequested();

        // MIGRATION: Export.ascx.vb:L157-L165 - the document is COMPOSED, not serialised from a tree, and
        // the module's payload is placed between the tags exactly as it was handed over. An XML writer
        // cannot express that: given a string it writes an escaped TEXT node, which is why the withdrawn
        // encoding revision could not be repaired by removing the encode alone. Concatenation is what the
        // legacy did and it is the only construction that carries a payload of markup through unaltered.
        //
        // The two attribute values are the only parts this layer supplies, and neither can carry a quote
        // that would break out of the attribute: the type is CleanName-sanitised, which strips the double
        // quote and the apostrophe outright, and the version is a package version string. The composed
        // document is parsed below before it is returned, so a value that did break the shape is caught
        // rather than shipped.
        string composed = string.Concat(
            XmlDeclaration,
            FormattableString.Invariant(
                $"<{ContentElementName} {ContentTypeAttributeName}=\"{CleanName(package.ModuleName)}\""),
            FormattableString.Invariant(
                $" {ContentVersionAttributeName}=\"{package.Version ?? string.Empty}\">"),
            payload,
            FormattableString.Invariant($"</{ContentElementName}>"));

        // Parsing is what turns "the module handed back something" into "the module handed back a document
        // this format can carry", and it is the step that still refuses a payload. Two classes of content
        // fail here: markup that is not well formed, and a C0 control character, for which the XML
        // specification provides no escape at all. Both are faults in the MODULE rather than in the request,
        // which is why the reason code below is classified as a server fault and the caller is not told to
        // correct anything. The tree the reader builds is transient - it is never returned, and the document
        // handed back is the composed string itself - and the ceiling above is what bounds it.
        //
        // Only the exception the reader raises for unreadable content is caught. Anything else is left to
        // propagate: a broad catch would convert a genuine defect into a tidy failure code and hide it.
        try
        {
            XDocument.Parse(composed);
        }
        catch (XmlException)
        {
            // The module's own text is deliberately NOT quoted in the message, and neither is the reader's
            // - a parser message quotes the offending fragment. It is module content and may carry the
            // portal's user data, and the message is published verbatim as the problem detail.
            return Result<string>.Failure(
                ExportFailedCode,
                FormattableString.Invariant(
                    $"Module {moduleId} returned content that cannot be represented in an export document."));
        }

        string serialised = composed;

        // The trail records that content left the module, and the SIZE of what left rather than any part
        // of it: an export payload is module content, which may be arbitrarily large and may carry data
        // belonging to the portal's users.
        //
        // MIGRATION: named MODULE_EXPORTED rather than MODULE_UPDATED. An export changes nothing, so
        // recording it as an update stated something untrue in a trail whose entire value is that it is
        // believed. The legacy export page wrote no audit record at all, so there is no legacy name to
        // preserve here and the honest one is used instead - the reasoning is on AuditEventNames.
        RecordModuleAudit(
            AuditEventNames.ModuleExported,
            portalId,
            module.ModuleId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Export",
                ["BusinessControllerRegistered"] = bool.TrueString,
                ["PayloadLength"] = payload.Length.ToString(CultureInfo.InvariantCulture),
            });

        return Result<string>.Success(serialised);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The whole import is one unit of work. The target module is named in the body because the
    /// endpoint carries no identifier in its route, and the portal argument is what stops a
    /// body-supplied identifier from reaching another tenant's module.
    /// </para>
    /// <para>
    /// The document is read the way this service writes it, which is the way the legacy module admin
    /// screen wrote and read one: the <c>type</c> attribute must name this module, the <c>version</c>
    /// attribute names the version the content was produced by and falls back to the installed version
    /// when the document omits it, and the root element's INNER XML is the payload, handed on verbatim.
    /// Nothing is escaped or unescaped in either direction.
    /// </para>
    /// <para>
    /// MIGRATION: <c>Import.ascx.vb</c> L196-L200 is the authority for all three steps, and the payload
    /// step is <c>xmlDoc.DocumentElement.InnerXml</c> - markup, not text. An earlier revision instead
    /// applied the PORTAL TEMPLATE reader's <c>Server.HtmlDecode</c> from <c>ModuleController.vb</c> L428,
    /// together with its fixed-offset CDATA strip at L414 - literally
    /// <c>strcontent.Substring(9, strcontent.Length - 12)</c>, the 9 characters of the opening delimiter
    /// and the 3 of the closing one. That decode belongs to a format whose writer had encoded the payload
    /// first; applied to a module admin document it corrupted every entity reference the content
    /// legitimately carried, and the corruption was invisible because the matching encode on the export
    /// side undid it again. Both halves are withdrawn. The fixed offsets are not reproduced either - they
    /// were a latent defect that removed 9 characters of real payload from any document whose content
    /// element was not exactly a CDATA section - and nothing replaces them, because inner XML keeps a CDATA
    /// section's delimiters exactly as <c>InnerXml</c> did.
    /// </para>
    /// <para>
    /// MIGRATION: THE TYPE IS ENFORCED, which an earlier revision of this implementation did not do at
    /// all. <c>Website/admin/Modules/Import.ascx.vb</c> lines 195-205 proceeded only when the attribute
    /// equalled <c>CleanName(ModuleName)</c> or <c>CleanName(FriendlyName)</c> and otherwise reported
    /// "The import file specified is not the correct type for this module". Both forms are accepted here
    /// for the same reason, and a document naming neither is refused with
    /// <c>module.content_type_mismatch</c>: without the check, a document exported from one module type
    /// could be loaded into a module of another, whose portability behaviour would then be handed
    /// content it cannot interpret.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy path swallowed every exception the module's own import code raised and
    /// carried on as though the content had been stored. That is a defect rather than a rule, so a
    /// failed import is reported here and nothing is committed.
    /// </para>
    /// </remarks>
    public async Task<Result> ImportModuleAsync(
        int portalId,
        ModuleImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // MIGRATION: the target module arrives in the BODY, because this endpoint carries no identifier in
        // its route, and ModuleImportRequest.ModuleId is consequently NULLABLE so that an omitted value is
        // distinguishable from a supplied one. Modules.ModuleID is IDENTITY(0,1), which makes ZERO A REAL
        // MODULE, so a non-nullable property would have deserialised a missing moduleId into a live request
        // against module zero and nothing could have told the two apart.
        //
        // ModuleImportRequestValidator now refuses the omission at the boundary with a field-keyed answer,
        // and this guard is retained rather than removed. It is not redundant: this service is reachable from
        // callers the MVC validation filter does not sit in front of - the unit suites call it directly, and
        // so would any future in-process consumer - so the condition is enforced at both points, with the
        // same wording, and the pair is annotated in both places. An earlier revision of this comment
        // asserted that no validator existed for the request and that the distinction therefore could not be
        // resolved anywhere but here; that is the drift the review closed.
        //
        // Refused by ABSENCE alone, never by sign. Both 0 and -1 are legitimate identifier values in this
        // schema - 0 is the module identity seed and -1 is a real portal identifier as well as the legacy
        // integer sentinel - so a caller that sends either has made a lookup, and it must be allowed to
        // fail as a lookup below rather than be misreported as a malformed request.
        if (request.ModuleId is not int moduleId)
        {
            return Result.Failure(
                RequestInvalidCode,
                "The module to import into must be supplied.");
        }

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || module.PortalId != portalId)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        // The caller must hold the edit grant on the module whose content they are replacing. Verified here
        // for the same reason as on creation - the target arrives in the BODY - and required at all because
        // without it any authenticated caller could overwrite any tenant's module content.
        if (await EnsureMayEditModuleAsync(portalId, moduleId, cancellationToken).ConfigureAwait(false)
            is ResultReason forbidden)
        {
            return Result.Failure(forbidden);
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Result.Failure(ContentInvalidCode, "The submitted document is empty.");
        }

        // MIGRATION: THE DEFERRED-IMPORT EVENT-QUEUE BRANCH AT ModuleController.vb:L422 IS OMITTED, and what
        // replaces it is the refusal below. The legacy code tested
        // `If objModule.SupportedFeatures = Null.NullInteger` - the integer sentinel -1, meaning "this
        // module's capabilities have not been determined yet because it was installed in this very request"
        // - and, when true, called CreateEventQueueMessage to park the payload on a queue for replay after
        // an application restart. That queue subsystem is excluded from this migration wholesale, so there
        // is nowhere to park anything.
        //
        // Nothing is lost, because the condition the branch existed to wait for cannot arise here. The
        // legacy had to defer only because it discovered a module's capabilities by late-binding its
        // controller class at run time, which was impossible mid-install; this service resolves behaviour
        // from a closed, dependency-injected set that is fixed at start-up, so a capability is either
        // registered or it is not and waiting changes nothing.
        //
        // The sentinel is nonetheless handled rather than ignored: DesktopModule.IsPortable guards
        // `SupportedFeatures > -1`, so an undetermined capability field reports NOT portable and this
        // request is refused with a reason the caller can act on - retry once the package is fully
        // installed - instead of succeeding silently having parked the document somewhere the caller
        // cannot observe. That is the documented divergence: a clear failure in place of a deferral.
        DesktopModule? package = await ReadPackageAsync(module, cancellationToken).ConfigureAwait(false);
        if (package is null || string.IsNullOrWhiteSpace(package.BusinessControllerClass) || !package.IsPortable)
        {
            return Result.Failure(
                NotPortableCode,
                FormattableString.Invariant($"Module {moduleId} does not support content import."));
        }

        XDocument document;
        try
        {
            document = ParseImportDocument(request.Content);
        }
        catch (XmlException exception)
        {
            RecordModuleImportParseFailure(portalId, moduleId, exception);
            return Result.Failure(ContentInvalidCode, ImportParseFailureMessage);
        }

        XElement? root = document.Root;
        if (root is null
            || root.Name.Namespace != XNamespace.None
            || !string.Equals(root.Name.LocalName, ContentElementName, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(
                ContentInvalidCode,
                FormattableString.Invariant($"The submitted document must have a <{ContentElementName}> root element."));
        }

        // SEC: THE ROOT ELEMENT MAY CARRY ONLY THE TWO ATTRIBUTES THIS CONTRACT DECLARES, and no namespace
        // declaration at all. A caller-supplied document is otherwise free to attach arbitrary attributes and
        // namespaces to the element this service reads, which is the shape a namespace-confusion or
        // attribute-smuggling attempt takes; refusing the document outright is cheaper and safer than
        // deciding, per attribute, whether it could matter.
        if (root.Attributes().Any(attribute =>
                attribute.IsNamespaceDeclaration
                || attribute.Name.Namespace != XNamespace.None
                || (!string.Equals(attribute.Name.LocalName, ContentTypeAttributeName, StringComparison.Ordinal)
                    && !string.Equals(attribute.Name.LocalName, ContentVersionAttributeName, StringComparison.Ordinal))))
        {
            return Result.Failure(
                ContentInvalidCode,
                "The submitted document contains an unsupported root namespace or attribute.");
        }

        // A payload that is itself markup is handed on as markup and is NOT decoded: it was never encoded,
        // so decoding it would corrupt any entity reference it legitimately contains. Only the text form -
        // which is what both this service and the legacy exporter write - carries the encoding to undo.
        //
        // MIGRATION: Import.ascx.vb:L196-L197 - the document's type attribute is compared against the
        // module's own sanitised name and its sanitised friendly name, and a mismatch is refused with
        // "The import file specified is not the correct type for this module". Reproduced here, and reproduced
        // at all because it was PROMISED and not delivered: ModuleImportRequest.FileName documents that the
        // legacy name-based check was re-sourced onto this attribute precisely so the refusal survived, and
        // until now nothing performed it - so a document belonging to another module was imported into this
        // one without complaint, handing a module content it could not interpret.
        //
        // The check is applied AFTER well-formedness and the root-element test, in the legacy's own order:
        // the legacy could not read an attribute off a document that had failed to load either.
        //
        // Both sides are sanitised, which is a bounded widening of the legacy comparison and the reason is
        // interoperability with this endpoint's OWN earlier output. The legacy compared the raw attribute
        // against the two sanitised names; an earlier revision of the export path wrote the RAW module name,
        // so documents this API has already produced carry a value the legacy rule would refuse. Sanitising
        // the submitted value too accepts both spellings of the same name while still refusing a different
        // name - the discriminating power is unchanged, because the sanitiser only removes punctuation and
        // the module name carries a unique constraint. The comparison stays case-SENSITIVE: VB's default
        // string comparison is binary, so the legacy refused a document whose type differed only in case,
        // and widening that would admit documents the legacy did not.
        string declaredType = root.Attribute(ContentTypeAttributeName)?.Value ?? string.Empty;
        string sanitisedType = CleanName(declaredType);

        if (!string.Equals(sanitisedType, CleanName(package.ModuleName), StringComparison.Ordinal)
            && !string.Equals(sanitisedType, CleanName(package.FriendlyName), StringComparison.Ordinal))
        {
            // The module's own names are named in the message but the SUBMITTED value is not echoed back:
            // it is caller-supplied text and the message is published verbatim as the problem detail.
            string expectedName = CleanName(package.ModuleName);
            string expectedFriendlyName = CleanName(package.FriendlyName);

            // The two accepted spellings are frequently the SAME value - a module whose friendly name matches
            // its name sanitises to one string - and offering an operator a choice between a value and itself
            // reads as a defect in the message rather than as a hint. They are therefore named once when they
            // agree, and both only when there is genuinely a second thing to try.
            string accepted = string.Equals(expectedName, expectedFriendlyName, StringComparison.Ordinal)
                ? FormattableString.Invariant($"\"{expectedName}\"")
                : FormattableString.Invariant($"\"{expectedName}\" or \"{expectedFriendlyName}\"");

            return Result.Failure(
                ContentTypeMismatchCode,
                FormattableString.Invariant(
                    $"The submitted document type does not match the target module package: module {moduleId} expects {accepted}."));
        }

        // MIGRATION: Import.ascx.vb:L200 - `CType(objObject, IPortable).ImportModule(ModuleId,
        // xmlDoc.DocumentElement.InnerXml, strVersion, UserInfo.UserID)`. The payload is the root element's
        // INNER XML, verbatim, with no decoding of any kind, and the concatenation below is that property's
        // exact semantics: each child node written back as the markup it is, so an element stays an element,
        // an entity reference stays escaped and a CDATA section keeps its delimiters.
        //
        // MIGRATION: THE `HtmlDecode` THIS REPLACES CAME FROM THE PORTAL TEMPLATE READER AND DID NOT BELONG
        // HERE. ModuleController.vb:L428 does apply `Server.HtmlDecode`, preceded by the fixed-offset CDATA
        // strip at L414 - but that pair reads the PORTAL TEMPLATE format, whose writer at L244-L246 had
        // encoded and CDATA-wrapped the payload in the first place. This endpoint migrates the module admin
        // workflow, whose exporter escapes nothing, so there was no encoding to undo: applying the decode
        // corrupted every entity reference a module's content legitimately contained, and it was invisible
        // only because the matching encode on the export side undid it again. Both halves are withdrawn
        // together; the divergence from the previous revision is recorded in MIGRATION_NOTES.md.
        //
        // MIGRATION: A CARRIAGE RETURN IN THE PAYLOAD BECOMES A LINE FEED, and that is PRESERVED legacy
        // behaviour rather than a new loss. The XML specification requires a reader to normalise every
        // line ending to a single line feed, and the legacy importer's own reader applied the same rule
        // before InnerXml was ever read, so the carriage return was lost at exactly the same point.
        // Preserving it would need the return escaped as a character reference, which would make documents
        // this service writes unreadable by the legacy importer - so the behaviour is annotated and left
        // alone, per the rule that a discovered legacy defect is recorded rather than quietly improved.
        string payload = string.Concat(
            root.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));

        string? version = root.Attribute(ContentVersionAttributeName)?.Value;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = package.Version;
        }

        Result imported = await _businessControllers.ImportModuleContentAsync(
            package.BusinessControllerClass,
            module.ModuleId,
            payload,
            version,
            _currentUser.UserId ?? UnattributedUserId,
            cancellationToken).ConfigureAwait(false);

        // NOTHING BELOW THIS POINT RUNS UNLESS CONTENT WAS ACTUALLY RESTORED. The factory reports every
        // state in which the module was not asked - no controller declared, a stored key no registration
        // covers, a registered controller that cannot restore, or an empty payload - as a failure rather
        // than as a success carrying an advisory, so this one guard now covers all of them as well as a
        // restore that broke. Before it did, the commit, the cache eviction and the Operation=Import audit
        // record below all ran for an installation whose closed controller set did not cover the module:
        // the caller was answered 200 and the trail recorded an import that had not happened.
        if (imported.IsFailure)
        {
            return Result.Failure(imported.Error!);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TabModule> placements =
            await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        foreach (TabModule placement in placements)
        {
            _cache.InvalidateModules(placement.TabId);
        }

            // The bounded operational facts this contract records: which module received content, which version
            // the document declared, how much arrived and how many placements were invalidated. The
            // caller-authored folder, filename and PAYLOAD are not recorded, because each can carry tenant or
            // user data and belongs under the module content's own access and deletion policy. Emitted after the
            // commit, so a failed import leaves no record claiming success.
            //
            // MIGRATION: DIVERGENCE, and a correction. Two facts an earlier revision recorded here are gone.
            // SourceFolder and SourceFileName were copied verbatim from the request, and ModuleImportRequest
            // documents both as accepted-and-deliberately-unused parity metadata with NO LENGTH BOUND and no
            // interpretation as a path - so nothing validated them, nothing read them, and a caller could put a
            // secret, a personal identifier, control text or an unbounded high-cardinality value into the audit
            // trail simply by naming a file that way. They are not recorded, because the trail loses nothing an
            // operator can act on: the module, the version, the size and the placement count all describe what
            // actually happened, whereas the caller's own description of where the document came from describes
            // only what the caller said.
            //
            // Withdrawing them bounds the WORK as well as the content. The sink joins every property and
            // writes the entry synchronously, so an unbounded property is unbounded work on the logging path -
            // one request whose body is within the API's limit could otherwise produce an audit entry of
            // nearly the same size. Nothing that reaches this dictionary is caller-sized.
            //
            // The declared version IS still recorded, because it is the one provenance fact that identifies the
            // content - but it comes from an attribute of a caller-supplied document, so it is reduced to digits,
            // dots and hyphens within a length bound before it is offered, and the sink then admits it only
            // because the name appears on its closed allowlist. Anything else becomes a fixed marker.
        RecordModuleAudit(
            AuditEventNames.ModuleUpdated,
            portalId,
            module.ModuleId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Import",
                ["Version"] = DescribeDeclaredVersion(version),
                ["PayloadLength"] = payload.Length.ToString(CultureInfo.InvariantCulture),
                ["PlacementCount"] = placements.Count.ToString(CultureInfo.InvariantCulture),
            });

        // A bare success, with no advisory to forward. The factory's four "nothing was restored" states are
        // failures, so reaching this line means the module was asked and answered, and there is nothing left
        // to qualify the answer with. Forwarding an advisory here would be dead code that reads as though a
        // successful import could still have imported nothing.
        return Result.Success();
    }

    /// <summary>
    /// Confirms that the caller may edit a page, which is what placing a module on it requires.
    /// </summary>
    /// <param name="portalId">The tenant the page belongs to.</param>
    /// <param name="tabId">The page a module is being created on.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>The reason the caller may not, or <see langword="null"/> when they may.</returns>
    /// <remarks>
    /// <para>
    /// WHY THIS CHECK LIVES HERE AND NOT IN A POLICY. Every other module mutation names its module in the
    /// route, so a route-reading authorisation policy can evaluate a permission against it before the action
    /// runs. Creation does not: the page it targets arrives in the request BODY, which no policy can read,
    /// and there is no module yet to evaluate against. The check therefore has to be performed after binding,
    /// and the only layer holding both the caller and the permission evaluator is this one.
    /// </para>
    /// <para>
    /// EDIT ON THE PAGE IS THE LEGACY RULE. The legacy module-settings screen was reachable only from a page
    /// already in edit mode, which required the edit permission on that page, and the permission service
    /// answers a host account affirmatively before reading a grant - so a host account is admitted exactly as
    /// it is everywhere else. A refusal and a page that does not exist produce the SAME answer, because
    /// telling an unauthorised caller which page identifiers exist is an enumeration oracle.
    /// </para>
    /// <para>
    /// NO IMPLICIT PORTAL-ADMINISTRATOR ARM, DELIBERATELY. Admitting a portal administrator here regardless of
    /// grants was considered and rejected on two grounds. Legacy
    /// <c>Library/Components/Security/PortalSecurity.vb</c> L123 short-circuits on <c>IsSuperUser</c> ALONE and
    /// admits every other caller only through a real grant or a pseudo-role, so an implicit administrator arm
    /// would be a widening rather than a port; and the sibling update, delete and settings endpoints are gated
    /// on the module edit policy, which likewise delegates wholly to <see cref="IPermissionService"/>. Adding
    /// the arm on creation alone would make placing a module easier than editing the one just placed. A portal
    /// administrator that should be able to place modules is granted the page edit permission, which is what
    /// the permissions resource exists to do.
    /// </para>
    /// </remarks>
    private async Task<ResultReason?> EnsureMayEditPageAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken)
    {
        Result<bool> granted = await _permissions
            .HasTabPermissionAsync(portalId, _currentUser.UserId, tabId, PermissionKey.EDIT, cancellationToken)
            .ConfigureAwait(false);

        return granted.IsSuccess && granted.Value
            ? null
            : new ResultReason(
                EditForbiddenCode,
                FormattableString.Invariant(
                    $"The caller may not place a module on page {tabId} in portal {portalId}."));
    }

    /// <summary>
    /// Confirms that the caller administers the portal, which is what the four portal-wide fields of an
    /// update require.
    /// </summary>
    /// <param name="portalId">The tenant whose administration is required.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>The reason the caller may not, or <see langword="null"/> when they may.</returns>
    /// <remarks>
    /// <para>
    /// WHY THIS IS SEPARATE FROM THE EDIT GRANT. Editing a module is a grant on a resource; changing where
    /// it is placed, fanning it out across the portal, naming it as the portal default, or rewriting every
    /// module's appearance are effects on pages the caller may hold no grant on at all. The legacy screen
    /// drew exactly that line by disabling four controls for anyone outside the portal administrator role
    /// while leaving the rest of the form editable, and this reproduces the line rather than collapsing it
    /// into the module grant - which would either refuse ordinary page administrators everything or admit
    /// them to everything.
    /// </para>
    /// <para>
    /// The decision is delegated to <see cref="IPermissionService.IsPortalAdministratorAsync"/> so that it
    /// is taken from stored state in one place: the portal's own administrator role identifier and the
    /// caller's active assignments, with a host account admitted first. A failed decision - which that
    /// member does not currently produce - is treated as a refusal rather than as an admission, because a
    /// question about authority that could not be answered must never be answered "yes".
    /// </para>
    /// <para>
    /// The message names the tenant and the nature of the refusal but not WHICH of the four fields
    /// triggered it. The caller submitted all four, so naming one would be arbitrary, and the correction is
    /// the same in every case.
    /// </para>
    /// </remarks>
    private async Task<ResultReason?> EnsureAdministersPortalAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<bool> administers = await _permissions
            .IsPortalAdministratorAsync(portalId, _currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        return administers.IsSuccess && administers.Value
            ? null
            : new ResultReason(
                AdministratorForbiddenCode,
                FormattableString.Invariant(
                    $"Changing a module's page, its portal-wide placement, or either propagation instruction requires administering portal {portalId}."));
    }

    /// <summary>
    /// Confirms that the caller may edit a module, which is what importing content into it requires.
    /// </summary>
    /// <param name="portalId">The tenant the module belongs to.</param>
    /// <param name="moduleId">The module content is being imported into.</param>
    /// <param name="cancellationToken">Abandons the reads when the caller disconnects.</param>
    /// <returns>The reason the caller may not, or <see langword="null"/> when they may.</returns>
    /// <remarks>
    /// The import's target arrives in the request body - the legacy import page chose it from a list on the
    /// form - so, exactly as for creation, no route-reading policy can reach it and the check belongs here.
    /// Import replaces a module's stored content, so the permission required is the module edit key, the same
    /// one the update and delete endpoints are gated on.
    /// </remarks>
    private async Task<ResultReason?> EnsureMayEditModuleAsync(
        int portalId,
        int moduleId,
        CancellationToken cancellationToken)
    {
        Result<bool> granted = await _permissions
            .HasModulePermissionAsync(
                portalId,
                _currentUser.UserId,
                moduleId,
                PermissionKey.EDIT,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return granted.IsSuccess && granted.Value
            ? null
            : new ResultReason(
                EditForbiddenCode,
                FormattableString.Invariant(
                    $"The caller may not import content into module {moduleId} in portal {portalId}."));
    }

    /// <summary>
    /// Rejects a paging request whose bounds no validator can have accepted.
    /// </summary>
    /// <param name="request">The submitted paging request.</param>
    /// <returns>The reason the request is unusable, or <see langword="null"/> when it is usable.</returns>
    private static ResultReason? ValidatePagedRequest(PagedRequest request)
    {
        if (request.PageIndex < 0)
        {
            return new ResultReason(RequestInvalidCode, "The page index must not be negative.");
        }

        if (request.PageSize < 0)
        {
            return new ResultReason(RequestInvalidCode, "The page size must not be negative.");
        }

        if (request.PageSize > PagedRequestValidator.MaximumPageSize)
        {
            return new ResultReason(
                RequestInvalidCode,
                FormattableString.Invariant(
                    $"The page size must not exceed {PagedRequestValidator.MaximumPageSize}."));
        }

        if (request.Query is not null && request.Query.Length > PagedRequestValidator.QueryMaximumLength)
        {
            return new ResultReason(
                RequestInvalidCode,
                FormattableString.Invariant(
                    $"The search text must not exceed {PagedRequestValidator.QueryMaximumLength} characters."));
        }

        // MIGRATION: the PER-COLLECTION ordering set is enforced HERE, against this collection's own set
        // rather than against the union. The shared request validator applies nothing narrower than the
        // union of every collection's set - one PagedRequest contract serves every listing and one
        // validator is resolved for it - so a portal-only, role-only or account-only field name would
        // otherwise be accepted here and then silently discarded, which returns a page the caller cannot
        // account for and cannot detect. Refusing states that plainly.
        //
        // SortableFields.Modules holds exactly the five names ApplyOrder has an arm for, and that
        // correspondence is the point: a name admitted here without an arm is a field the listing accepts
        // and ignores, and an arm without an admitted name is unreachable. The ordering is applied to the
        // modules BEFORE the page window is taken, so it orders the collection rather than re-sorting one
        // arbitrary page - and because each module contributes its placement rows together, the rows are
        // returned in the module order the caller asked for. The projection's two remaining members are
        // refused rather than faked, for the reasons recorded on ApplyOrder: a placement position is not a
        // listing order, and a title derived at projection time does not exist until after the ordering
        // has run.
        if (!SortableFields.IsPermittedFor(request.SortBy, SortableFields.Modules))
        {
            return new ResultReason(
                RequestInvalidCode,
                FormattableString.Invariant($"Modules cannot be ordered by '{request.SortBy}'."));
        }

        return null;
    }

    // MIGRATION: THERE IS NO ORDERING RULE ON THE DISPLAY WINDOW, AND THAT ABSENCE IS MEASURED RATHER THAN
    //            ASSUMED. A helper here used to refuse a request whose end date preceded its start date. The
    //            legacy screen refuses no such thing. Website/admin/Modules/modulesettings.ascx declares
    //            EXACTLY FOUR validators - valtxtStartDate (L78-L79), valtxtEndDate (L88-L89), valBorder
    //            (L137-L138) and valCacheTime (L172-L173) - every one of them a CompareValidator with
    //            Operator="DataTypeCheck", which asserts only that the submitted text parses as its declared
    //            type. There is no RangeValidator, no CompareValidator comparing one control to another, and
    //            no RequiredFieldValidator anywhere in that file. The code-behind is the same story:
    //            ModuleSettings.ascx.vb L367-L375 parses each field on its own - "If txtStartDate.Text <> ''
    //            Then objModule.StartDate = Convert.ToDateTime(txtStartDate.Text) Else objModule.StartDate =
    //            Null.NullDate", and the identical block for the end date - and never compares the two. A
    //            window ending before it begins was therefore ACCEPTED and STORED verbatim by the legacy
    //            application, which merely rendered the module in no period at all.
    //
    //            Reinstating the check would be a NARROWING: identical input that the legacy application
    //            accepted would be refused, which the behaviour-preservation obligation (AAP Rule T5, MC3)
    //            forbids as squarely as it forbids a widening, and MC4 requires the validation rules to match
    //            rather than to improve on what was measured. The same reasoning already governs the two
    //            neighbouring fields on this contract, whose legacy validators are likewise type-only: a
    //            negative cache period and a border outside the range its own error message advertises are
    //            both admitted, and both are pinned by tests. Nothing is silently absorbed - the storability
    //            bound on each date remains, enforced field by field by the request validator, because
    //            SQL Server's datetime column genuinely cannot hold a value below its calendar and that is a
    //            property of the store rather than a rule invented here.
    /// <summary>
    /// Outcome of normalising one submitted settings store: either the reason it is unusable, or the
    /// map to write.
    /// </summary>
    /// <param name="Reason">
    /// The reason the submission cannot be stored, or <see langword="null"/> when it can.
    /// </param>
    /// <param name="Values">
    /// The case-insensitive map to write. Empty whenever <paramref name="Reason"/> is present, because a
    /// refused submission contributes nothing.
    /// </param>
    /// <remarks>
    /// MIGRATION: this pair exists so that the normalising helper reports its outcome through its RETURN
    /// VALUE rather than through an argument it writes back. AAP 0.7.4 states that no out or by-reference
    /// parameter appears in the target, and the rule is honoured here rather than being treated as
    /// applying only to the public surface: an argument that is really a second return value is the exact
    /// idiom the migration set out to remove, and a private member is not a licence to keep it.
    /// </remarks>
    private readonly record struct NormalisedSettings(
        ResultReason? Reason,
        Dictionary<string, string> Values);

    /// <summary>
    /// One row of the module listing: a module together with the single placement that row describes.
    /// </summary>
    /// <param name="Module">The module the row projects.</param>
    /// <param name="Placement">
    /// The one placement this row carries. A module placed on several pages yields several rows, each
    /// pairing the same module with a different placement.
    /// </param>
    /// <remarks>
    /// The pair exists so that the row set can be ordered and windowed as ROWS before anything is
    /// projected. Carrying the two references rather than the finished contract type is deliberate: the
    /// definition names needed to project a row are read only once the window is known, so a row that
    /// falls outside the window is never mapped at all.
    /// </remarks>
    private readonly record struct ModulePlacement(Module Module, TabModule Placement);

    /// <summary>
    /// Normalises a submitted settings map, rejecting anything the columns cannot hold.
    /// </summary>
    /// <param name="submitted">The desired state of one settings store.</param>
    /// <param name="valueMaximumLength">Maximum storable value length for that store.</param>
    /// <param name="scope">Word naming the store, used in the reported message.</param>
    /// <returns>
    /// The map to write, or the reason the submission is unusable. A refused submission yields an empty
    /// map, so a caller that ignores the reason cannot write a partially validated set.
    /// </returns>
    private static NormalisedSettings NormaliseSettings(
        IReadOnlyDictionary<string, string> submitted,
        int valueMaximumLength,
        string scope)
    {
        // Names are compared without regard to case, matching the legacy Hashtable's behaviour and the
        // case-insensitive collation the settings tables use, so two submitted keys differing only in case
        // resolve to the one stored row rather than racing to overwrite each other.
        var normalised = new Dictionary<string, string>(submitted.Count, StringComparer.OrdinalIgnoreCase);
        var refused = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> pair in submitted)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return new NormalisedSettings(
                    new ResultReason(
                        SettingInvalidCode,
                        FormattableString.Invariant($"A {scope} setting name must not be blank.")),
                    refused);
            }

            if (pair.Key.Length > SettingNameMaximumLength)
            {
                return new NormalisedSettings(
                    new ResultReason(
                        SettingInvalidCode,
                        FormattableString.Invariant(
                            $"The {scope} setting name \"{pair.Key}\" exceeds {SettingNameMaximumLength} characters.")),
                    refused);
            }

            // MIGRATION: a null value becomes the empty string rather than being rejected or stored as null.
            // Null.vb defines NullString as "" rather than as a null reference, and both legacy settings
            // readers - ModuleController.vb L1237 and L1336 - substituted "" for a DBNull column, so the
            // empty string is the legacy representation of an absent setting value and is preserved as such.
            string value = pair.Value ?? string.Empty;
            if (value.Length > valueMaximumLength)
            {
                return new NormalisedSettings(
                    new ResultReason(
                        SettingInvalidCode,
                        FormattableString.Invariant(
                            $"The value of the {scope} setting \"{pair.Key}\" exceeds {valueMaximumLength} characters.")),
                    refused);
            }

            normalised[pair.Key] = value;
        }

        return new NormalisedSettings(null, normalised);
    }

    /// <summary>
    /// Identifies setting names whose authority belongs to typed portal-administration contracts rather
    /// than the generic module-settings surface.
    /// </summary>
    /// <param name="settingName">The stored or submitted setting name.</param>
    /// <returns><see langword="true"/> for the legacy security and user-list column namespaces.</returns>
    private static bool IsSecurityOwnedSettingName(string settingName)
        => settingName.StartsWith("Security_", StringComparison.OrdinalIgnoreCase)
            || settingName.StartsWith("Column_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses one import document with DTD resolution disabled and bounded character, node and nesting work.
    /// </summary>
    /// <param name="content">The caller-supplied XML document.</param>
    /// <returns>The parsed document with whitespace-only payload nodes preserved.</returns>
    /// <exception cref="XmlException">The document is malformed, prohibited or exceeds a work budget.</exception>
    private static XDocument ParseImportDocument(string content)
    {
        if (content.Length > ImportDocumentCharacterMaximum)
        {
            throw new XmlException("Portable-content XML exceeds the configured character budget.");
        }

        XmlReaderSettings settings = CreateImportXmlReaderSettings();

        // Validate work factors before constructing an object graph. The content string is immutable, so
        // the second pass cannot differ from the first; parsing twice is a bounded cost and prevents a deeply
        // nested document from reaching XDocument's tree builder before its depth has been refused.
        using (var validationInput = new StringReader(content))
        using (XmlReader validationReader = XmlReader.Create(validationInput, settings))
        {
            int nodeCount = 0;
            while (validationReader.Read())
            {
                nodeCount++;
                if (validationReader.NodeType == XmlNodeType.Element)
                {
                    nodeCount += validationReader.AttributeCount;
                    if (validationReader.AttributeCount > ImportElementAttributeMaximum)
                    {
                        throw new XmlException("Portable-content XML exceeds the per-element attribute budget.");
                    }
                }

                if (nodeCount > ImportDocumentNodeMaximum)
                {
                    throw new XmlException("Portable-content XML exceeds the node budget.");
                }

                if (validationReader.Depth > ImportDocumentDepthMaximum)
                {
                    throw new XmlException("Portable-content XML exceeds the nesting-depth budget.");
                }

                if ((validationReader.NodeType is XmlNodeType.Text
                        or XmlNodeType.CDATA
                        or XmlNodeType.Whitespace
                        or XmlNodeType.SignificantWhitespace)
                    && validationReader.Value.Length > ImportTextNodeCharacterMaximum)
                {
                    throw new XmlException("Portable-content XML exceeds the text-node budget.");
                }
            }
        }

        using var input = new StringReader(content);
        using XmlReader reader = XmlReader.Create(input, settings);

        // PreserveWhitespace is required, not cosmetic. Without it a whitespace-only payload becomes empty
        // and the module is handed content the caller did not send.
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    /// <summary>Builds the immutable security settings used for both import parsing passes.</summary>
    /// <returns>An XML reader configuration that performs no external resolution.</returns>
    private static XmlReaderSettings CreateImportXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = ImportDocumentCharacterMaximum,
        IgnoreWhitespace = false,
        ConformanceLevel = ConformanceLevel.Document,
        CloseInput = true,
    };

    /// <summary>Records a bounded parser diagnostic without publishing parser text or submitted content.</summary>
    /// <param name="portalId">The tenant in which import was attempted.</param>
    /// <param name="moduleId">The target module.</param>
    /// <param name="exception">The parser exception whose type and position are retained.</param>
    private void RecordModuleImportParseFailure(int portalId, int moduleId, XmlException exception)
    {
        AuditEvent record = new(AuditEventNames.ModuleUpdated)
        {
            Outcome = AuditOutcome.Denied,
            PortalId = portalId,
            ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
            ResourceType = ModuleResourceType,
            ResourceId = moduleId.ToString(CultureInfo.InvariantCulture),
            FailureCode = ContentInvalidCode,
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Import",
                ["DiagnosticType"] = exception.GetType().Name,
                ["LineNumber"] = exception.LineNumber.ToString(CultureInfo.InvariantCulture),
                ["LinePosition"] = exception.LinePosition.ToString(CultureInfo.InvariantCulture),
            },
        };

        _audit.Record(record);
    }

    /// <summary>
    /// Resolves the placement a member addresses.
    /// </summary>
    /// <param name="module">The module whose placement is wanted.</param>
    /// <param name="tabModuleId">The named placement, or <see langword="null"/> for the original one.</param>
    /// <param name="mismatchCode">
    /// Reason code to report when a named placement belongs to another module, or
    /// <see langword="null"/> when the caller documents no such code and that case reads as an absence.
    /// </param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The placement, an absence, or the reason the named placement does not fit.</returns>
    /// <remarks>
    /// With no placement named, the module's original placement is used, identified as the one with the
    /// lowest placement identifier. That is deterministic and stable, which matters because two members
    /// address a module without carrying a placement identifier at all.
    /// </remarks>
    private async Task<Result<TabModule?>> ResolvePlacementAsync(
        Module module,
        int? tabModuleId,
        string? mismatchCode,
        CancellationToken cancellationToken)
    {
        if (tabModuleId is int addressed)
        {
            TabModule? named = await _modules.GetTabModuleByIdAsync(addressed, cancellationToken).ConfigureAwait(false);
            if (named is not null && named.ModuleId == module.ModuleId)
            {
                return Result<TabModule?>.Success(named);
            }

            return mismatchCode is null
                ? Result<TabModule?>.Success(null)
                : Result<TabModule?>.Failure(
                    mismatchCode,
                    FormattableString.Invariant(
                        $"Placement {addressed} does not belong to module {module.ModuleId}."));
        }

        IReadOnlyList<TabModule> placements = module.TabModules.Count > 0
            ? module.TabModules.ToList()
            : await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        return Result<TabModule?>.Success(
            placements.OrderBy(candidate => candidate.TabModuleId).FirstOrDefault());
    }

    /// <summary>
    /// Reads the placement a module has on one named page.
    /// </summary>
    /// <param name="module">The module whose placement is wanted.</param>
    /// <param name="tabId">The page the placement must sit on.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The placement on that page, or <see langword="null"/> when the module is not placed there.</returns>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="ResolvePlacementAsync"/> and deliberately so. That member answers "which
    /// placement does a caller that named none mean", and its answer is the lowest placement identifier -
    /// deterministic, stable, and correct for the two members that address a module without carrying a page.
    /// This member answers a different question: "does this module sit on THIS page", which is what a request
    /// carrying a page key is entitled to have honoured. Folding the two together is what produced the defect
    /// recorded on the update path, because the fallback silently satisfied a request that had named a page.
    /// </para>
    /// <para>
    /// The page is matched by VALUE and never by sign. <c>dbo.Tabs.TabID</c> is <c>IDENTITY(0, 1)</c>, so page
    /// zero is the first page a portal ever created and a positive-value test would refuse it; <c>-1</c> was
    /// an "any page" wildcard in the legacy query surface rather than an absence. Neither may be read as
    /// unspecified, which is also why no validator places a numeric floor on the member this value arrives in.
    /// </para>
    /// <para>
    /// The tracked collection is preferred over a repository read when it is populated, exactly as the sibling
    /// resolver does, so that a placement already loaded into the unit of work is the instance that gets
    /// modified rather than a second copy of the same row. The identifier tie-break keeps the answer
    /// deterministic in the degenerate case of two rows on one page, which the schema's composite key should
    /// make impossible and which a read must not depend on being impossible.
    /// </para>
    /// </remarks>
    private async Task<TabModule?> ReadPlacementOnPageAsync(
        Module module,
        int tabId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> placements = module.TabModules.Count > 0
            ? module.TabModules.ToList()
            : await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        return placements
            .Where(candidate => candidate.TabId == tabId)
            .OrderBy(candidate => candidate.TabModuleId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Strips the legacy punctuation set from a name, reproducing the sanitiser both content workflows used
    /// to label and to identify an exported document.
    /// </summary>
    /// <param name="name">The name to sanitise.</param>
    /// <returns>The name with every character of <see cref="NamePunctuation"/> removed.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>Website/admin/Modules/Export.ascx.vb</c> L209-L221, duplicated at
    /// <c>Library/Components/Shared/Globals.vb</c> L1686-L1694. The legacy walked the punctuation set and
    /// called <c>String.Replace</c> once per character, which allocates a string per iteration; a single
    /// pass over the input produces the identical result. The result is identical rather than merely
    /// equivalent because removal is order-independent: no replacement can introduce a character a later
    /// replacement would have removed.
    /// </para>
    /// <para>
    /// It is deliberately NOT a general-purpose sanitiser and must not be reached for as one. It does not
    /// remove control characters, it does not remove path separators other than the two in the set, and it
    /// is not an escaping function - it exists to reproduce one legacy transformation exactly, and its only
    /// callers are the export composition and the import type check that the legacy pair keeps in step.
    /// </para>
    /// </remarks>
    private static string CleanName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (!NamePunctuation.Contains(character, StringComparison.Ordinal))
            {
                cleaned.Append(character);
            }
        }

        return cleaned.ToString();
    }

    /// <summary>
    /// Orders a module's placements deterministically, optionally narrowed to one page.
    /// </summary>
    /// <param name="placements">The module's placements.</param>
    /// <param name="tabId">The page to narrow to, or <see langword="null"/> for every page.</param>
    /// <returns>The placements in page, position and identifier order.</returns>
    private static IEnumerable<TabModule> OrderPlacements(IReadOnlyList<TabModule> placements, int? tabId)
        => placements
            .Where(candidate => tabId is null || candidate.TabId == tabId.Value)
            .OrderBy(candidate => candidate.TabId)
            .ThenBy(candidate => candidate.ModuleOrder)
            .ThenBy(candidate => candidate.TabModuleId);

    /// <summary>
    /// Reads a portal's definition friendly names, keyed by definition identifier.
    /// </summary>
    /// <param name="portalId">The portal whose catalogue is read.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>Friendly names by definition identifier.</returns>
    private async Task<IReadOnlyDictionary<int, string>> ReadDefinitionNamesAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        var names = new Dictionary<int, string>(definitions.Count);
        foreach (ModuleDefinition definition in definitions)
        {
            names[definition.ModuleDefinitionId] = definition.FriendlyName;
        }

        return names;
    }

    /// <summary>
    /// Picks the friendly name to project for a module.
    /// </summary>
    /// <param name="module">The module being projected.</param>
    /// <param name="friendlyNames">Names read for the portal's catalogue.</param>
    /// <returns>The definition's friendly name, or <see langword="null"/> when it cannot be resolved.</returns>
    /// <remarks>
    /// The module repository documents no eager loading of the definition, which is why the name is
    /// looked up from the catalogue first and the navigation is only a fallback.
    /// </remarks>
    private static string? ResolveFriendlyName(Module module, IReadOnlyDictionary<int, string> friendlyNames)
        => friendlyNames.TryGetValue(module.ModuleDefinitionId, out string? name)
            ? name
            : module.ModuleDefinition?.FriendlyName;

    /// <summary>
    /// Projects a portal's available definitions into the catalogue contract.
    /// </summary>
    /// <param name="portalId">The portal whose grants restrict the catalogue.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The catalogue in a stable order.</returns>
    private async Task<IReadOnlyList<ModuleDefinitionDto>> ReadDefinitionCatalogueAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        var packages = new Dictionary<int, DesktopModule?>();
        var catalogue = new List<ModuleDefinitionDto>(definitions.Count);

        foreach (ModuleDefinition definition in definitions
            .OrderBy(candidate => candidate.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ModuleDefinitionId))
        {
            if (!packages.TryGetValue(definition.DesktopModuleId, out DesktopModule? package))
            {
                package = await _definitions
                    .GetDesktopModuleByIdAsync(definition.DesktopModuleId, cancellationToken)
                    .ConfigureAwait(false);

                packages[definition.DesktopModuleId] = package;
            }

            catalogue.Add(ModuleMappings.ToDto(definition, package));
        }

        return catalogue;
    }

    /// <summary>
    /// Reads the installed package a module's definition belongs to.
    /// </summary>
    /// <param name="module">The module whose package is wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The package, or <see langword="null"/> when it cannot be resolved.</returns>
    private async Task<DesktopModule?> ReadPackageAsync(Module module, CancellationToken cancellationToken)
    {
        ModuleDefinition? definition = await _definitions
            .GetModuleDefinitionByIdAsync(module.ModuleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (definition is null)
        {
            return null;
        }

        return await _definitions
            .GetDesktopModuleByIdAsync(definition.DesktopModuleId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the portal's content pages, that is every page that is not administrative.
    /// </summary>
    /// <param name="portalId">The portal whose pages are read.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The portal's content pages.</returns>
    private async Task<IReadOnlyList<Tab>> ReadContentTabsAsync(int portalId, CancellationToken cancellationToken)
    {
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        // The repository returns recycled pages, because the legacy read applied no predicate to
        // IsDeleted and projected the column instead. A content page offered as a placement target must
        // not be one sitting in the recycle bin, so that exclusion is applied here alongside the
        // administrative one rather than asked of the contract.
        IReadOnlyList<Tab> tabs = await _tabs
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        int? adminTabId = portal?.AdminTabId;
        return tabs
            .Where(tab => !tab.IsDeleted && !IsAdministrative(tab, adminTabId))
            .ToList();
    }

    /// <summary>
    /// Classifies a page as administrative.
    /// </summary>
    /// <param name="tab">The page to classify.</param>
    /// <param name="adminTabId">The portal's administration page, when it has one.</param>
    /// <returns><see langword="true"/> when the page belongs to the administrative band.</returns>
    /// <remarks>
    /// This reproduces <c>TabInfo.IsAdminTab</c> (TabInfo.vb:L434-L464) for a portal-scoped page: the
    /// administration page itself and its immediate children. The host band the legacy property also
    /// covered cannot arise here, because every page read belongs to one portal.
    /// </remarks>
    private static bool IsAdministrative(Tab tab, int? adminTabId)
        => adminTabId is int administrationTabId
            && (tab.TabId == administrationTabId || tab.ParentId == administrationTabId);

    /// <summary>
    /// Turns a submitted position into the position that is actually stored, resolving the append
    /// instruction against the pane it is being appended to.
    /// </summary>
    /// <param name="tabId">The page whose pane is being positioned within.</param>
    /// <param name="paneName">The pane being positioned within.</param>
    /// <param name="requested">The position the caller submitted, which may be the append instruction.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// <paramref name="requested"/> unchanged when it names a position, or the next position at the
    /// bottom of the pane when it is the append instruction.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this reproduces <c>ModuleController.UpdateModuleOrder</c> (ModuleController.vb:L1160-L1173),
    /// which read the pane's occupied positions through <c>GetTabModuleOrder(TabId, PaneName)</c> and
    /// added the step. An EMPTY pane yields position one, and that is the legacy arithmetic rather than a
    /// special case invented here: the legacy loop left its variable at the incoming -1 when the reader
    /// returned no rows and then added two, so -1 + 2 = 1. The same subtraction is reproduced by seeding
    /// the running maximum with the sentinel, so one expression covers both the empty and the occupied
    /// pane exactly as the legacy one did.
    /// </para>
    /// <para>
    /// MIGRATION: the resolution happens BEFORE the row is written, whereas the legacy resolved it
    /// immediately AFTER writing the row and then issued a second statement to correct it. The outcome
    /// stored is identical and the sentinel is never durable in either arrangement; doing it first
    /// removes a write and, more importantly, removes the window in which a concurrent reader could
    /// observe the sentinel as though it were a position.
    /// </para>
    /// <para>
    /// MIGRATION: the running maximum is taken with <c>Max</c> rather than by reading the last row the
    /// repository returns. The legacy loop took the last row read and relied entirely on the
    /// procedure's <c>order by ModuleOrder</c> (03.00.01.SqlDataProvider:L495-L507) to make that row the
    /// greatest, which the repository's own ordering reproduces - so the two agree today. Taking the
    /// maximum explicitly states what the value has to be, and cannot be broken later by a change to an
    /// ordering that no longer looks load-bearing.
    /// </para>
    /// <para>
    /// The read is per pane and per page, because that is the key the legacy resolver took. Two
    /// placements of one module on two different pages therefore each land at the bottom of their own
    /// pane rather than sharing a position computed for one of them.
    /// </para>
    /// </remarks>
    private async Task<int> ResolvePositionAsync(
        int tabId,
        string paneName,
        int requested,
        CancellationToken cancellationToken)
    {
        if (requested != AppendPositionSentinel)
        {
            return requested;
        }

        IReadOnlyList<TabModule> occupied = await _modules
            .GetTabModuleOrderAsync(tabId, paneName, cancellationToken)
            .ConfigureAwait(false);

        int highest = occupied.Count == 0
            ? AppendPositionSentinel
            : occupied.Max(placement => placement.ModuleOrder);

        return highest + PositionStep;
    }

    /// <summary>
    /// Places a module on every content page it is missing from.
    /// </summary>
    /// <param name="portalId">The portal being fanned out across.</param>
    /// <param name="module">The module being placed.</param>
    /// <param name="template">The placement whose settings the new placements copy.</param>
    /// <param name="requestedPosition">
    /// The position the caller submitted, forwarded unresolved so that an append instruction is resolved
    /// against each target page's own pane rather than reusing the position computed for the addressed
    /// page. When the caller named a position instead, every new placement takes that position, which is
    /// what copying a template means.
    /// </param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of placements added.</returns>
    /// <remarks>
    /// MIGRATION - discovered legacy defect, not inherited. The legacy copy-to-another-page path passed
    /// the append instruction straight to the insert under the comment "Add a copy of the module to the
    /// bottom of the Pane for the new Tab" (ModuleController.vb:L712) and then, unlike both the add and
    /// the update paths, never called the resolver - so the sentinel stayed in the row until some later
    /// renumbering of that page happened to overwrite it. The stated intent is honoured here and the
    /// omission is not reproduced.
    /// </remarks>
    private async Task<int> PlaceOnContentTabsAsync(
        int portalId,
        Module module,
        TabModule template,
        int requestedPosition,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> existing =
            await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        var placed = existing.Select(placement => placement.TabId).ToHashSet();
        int added = 0;

        foreach (Tab target in await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            if (!placed.Add(target.TabId))
            {
                continue;
            }

            int position = await ResolvePositionAsync(
                target.TabId,
                template.PaneName,
                requestedPosition,
                cancellationToken).ConfigureAwait(false);

            await _modules
                .AddTabModuleAsync(
                    new TabModule
                    {
                        TabId = target.TabId,
                        ModuleId = module.ModuleId,
                        PaneName = template.PaneName,
                        ModuleOrder = position,
                        CacheTime = template.CacheTime,
                        Alignment = template.Alignment,
                        Color = template.Color,
                        Border = template.Border,
                        IconFile = template.IconFile,
                        Visibility = template.Visibility,
                        ContainerSrc = template.ContainerSrc,
                        DisplayTitle = template.DisplayTitle,
                        DisplayPrint = template.DisplayPrint,
                        DisplaySyndicate = template.DisplaySyndicate,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            affectedTabIds.Add(target.TabId);
            added++;
        }

        return added;
    }

    /// <summary>
    /// Removes every placement of a module other than the one being kept.
    /// </summary>
    /// <param name="module">The module being withdrawn.</param>
    /// <param name="kept">The placement that survives.</param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of placements removed.</returns>
    private async Task<int> WithdrawFromOtherTabsAsync(
        Module module,
        TabModule kept,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> existing =
            await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        int removed = 0;
        foreach (TabModule stale in existing.Where(placement => placement.TabModuleId != kept.TabModuleId))
        {
            await RemovePlacementAsync(stale, cancellationToken).ConfigureAwait(false);
            affectedTabIds.Add(stale.TabId);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Removes one placement together with the settings scoped to it.
    /// </summary>
    /// <param name="placement">The placement to remove.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    /// <remarks>
    /// The settings rows are removed explicitly rather than left to the cascade the schema declares,
    /// so the outcome does not depend on which provider the request is served by.
    /// </remarks>
    private async Task RemovePlacementAsync(TabModule placement, CancellationToken cancellationToken)
    {
        // The whole placement-scoped collection goes, so this is the bulk removal the legacy provider
        // exposed for exactly this case rather than a read followed by a row-at-a-time loop.
        await _modules
            .DeleteTabModuleSettingsAsync(placement.TabModuleId, cancellationToken)
            .ConfigureAwait(false);

        await _modules
            .DeleteTabModuleAsync(placement.TabId, placement.ModuleId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records a module and its page as the portal's default pair.
    /// </summary>
    /// <param name="portalId">The portal whose default is being set.</param>
    /// <param name="moduleId">The module becoming the default.</param>
    /// <param name="tabId">The page the default module sits on.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns><see langword="true"/> when the pair was recorded.</returns>
    /// <remarks>
    /// The two rows are written against the portal's own settings module instance, resolved by friendly
    /// name, which is exactly where <c>PortalSettings.UpdateSiteSetting</c> wrote them. A portal that
    /// has no such instance cannot hold the keys, and the caller reports that on the result rather than
    /// failing the whole save.
    /// </remarks>
    private async Task<bool> NameAsPortalDefaultAsync(
        int portalId,
        int moduleId,
        int tabId,
        CancellationToken cancellationToken)
    {
        // SEC-007: Site Settings is an administrative package. It is intentionally excluded from the
        // portal-placeable catalogue and reached only through the explicit privileged lookup used by the
        // two administrative settings paths.
        ModuleDefinition? siteSettings = await _definitions
            .GetAdministrativeDefinitionByFriendlyNameAsync(
                portalId,
                SiteSettingsDefinitionName,
                cancellationToken)
            .ConfigureAwait(false);

        if (siteSettings is null)
        {
            return false;
        }

        // MIGRATION: the legacy module block has no paging member, so the tenant's modules are read whole
        // and the recycle bin is excluded here. This call site never wanted a page - it asked for every
        // live instance so it could locate one by its definition.
        IReadOnlyList<Module> instances =
            await _modules.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        Module? host = instances.FirstOrDefault(candidate =>
            !candidate.IsDeleted
            && candidate.ModuleDefinitionId == siteSettings.ModuleDefinitionId);

        if (host is null)
        {
            return false;
        }

        IReadOnlyList<ModuleSetting> stored =
            await _modules.GetModuleSettingsAsync(host.ModuleId, cancellationToken).ConfigureAwait(false);

        await UpsertSettingAsync(stored, host.ModuleId, DefaultModuleSettingName, moduleId.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
        await UpsertSettingAsync(stored, host.ModuleId, DefaultTabSettingName, tabId.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Writes one module setting, updating the stored row when it already exists.
    /// </summary>
    /// <param name="stored">The rows already held against the module.</param>
    /// <param name="moduleId">The module the setting belongs to.</param>
    /// <param name="settingName">The setting name.</param>
    /// <param name="settingValue">The value to store.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A task that completes once the write is staged.</returns>
    private async Task UpsertSettingAsync(
        IReadOnlyList<ModuleSetting> stored,
        int moduleId,
        string settingName,
        string settingValue,
        CancellationToken cancellationToken)
    {
        ModuleSetting? existing = stored.FirstOrDefault(candidate =>
            string.Equals(candidate.SettingName, settingName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.SettingValue = settingValue;
            return;
        }

        await _modules
            .AddModuleSettingAsync(
                new ModuleSetting
                {
                    ModuleId = moduleId,
                    SettingName = settingName,
                    SettingValue = settingValue,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies one placement's appearance onto every other placement on the portal's content pages.
    /// </summary>
    /// <param name="portalId">The portal being propagated across.</param>
    /// <param name="source">The placement whose appearance is authoritative.</param>
    /// <param name="affectedTabIds">Set collecting the pages whose caches must be dropped.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of placements changed.</returns>
    /// <remarks>
    /// Only the appearance members travel. Each target keeps its own position within its pane, its pane
    /// and its caching period, which is what ModuleController.UpdateModule preserved when it fanned the
    /// same nine members out.
    /// </remarks>
    private async Task<int> PropagateAppearanceAsync(
        int portalId,
        TabModule source,
        ISet<int> affectedTabIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Tab> contentTabs = await ReadContentTabsAsync(portalId, cancellationToken).ConfigureAwait(false);
        var contentTabIds = contentTabs.Select(tab => tab.TabId).ToHashSet();

        // MIGRATION: the legacy module block has no paging member, so the tenant's modules are read whole
        // and the recycle bin is excluded here. This call site never wanted a page - it asked for every
        // live instance so it could locate one by its definition.
        IReadOnlyList<Module> instances =
            await _modules.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        int copied = 0;
        foreach (Module candidate in instances.Where(module => !module.IsDeleted))
        {
            IReadOnlyList<TabModule> placements =
                await _modules.GetTabModulesByModuleIdAsync(candidate.ModuleId, cancellationToken).ConfigureAwait(false);

            foreach (TabModule target in placements)
            {
                if (target.TabModuleId == source.TabModuleId || !contentTabIds.Contains(target.TabId))
                {
                    continue;
                }

                target.Alignment = source.Alignment;
                target.Color = source.Color;
                target.Border = source.Border;
                target.IconFile = source.IconFile;
                target.Visibility = source.Visibility;
                target.ContainerSrc = source.ContainerSrc;
                target.DisplayTitle = source.DisplayTitle;
                target.DisplayPrint = source.DisplayPrint;
                target.DisplaySyndicate = source.DisplaySyndicate;

                affectedTabIds.Add(target.TabId);
                copied++;
            }
        }

        return copied;
    }

    /// <summary>
    /// Drops the cached module set of every page a write touched.
    /// </summary>
    /// <param name="tabIds">The pages whose caches are stale.</param>
    /// <remarks>
    /// This is the target form of the legacy <c>ClearCache(TabId)</c> call that closed every module
    /// write. The legacy call also dropped that page's module permission cache; nothing here writes a
    /// permission row, so that eviction is deliberately not repeated.
    /// </remarks>
    private void InvalidatePlacements(IEnumerable<int> tabIds)
    {
        foreach (int tabId in tabIds)
        {
            _cache.InvalidateModules(tabId);
        }
    }

    /// <summary>
    /// Bounds and allowlists the version an imported document declares, before it is recorded.
    /// </summary>
    /// <param name="version">The version read from the document, or resolved from the package.</param>
    /// <returns>
    /// The declared version when it is a plain version string within
    /// <see cref="MaximumAuditedVersionLength"/>; otherwise <see cref="UnusableVersion"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An ALLOWLIST of digits, dots and hyphens, which is every character a version has ever needed and
    /// nothing that could carry a secret, a personal identifier or a control character. The alternative -
    /// stripping the characters that are unwelcome - reshapes a hostile value into something that looks
    /// authentic, and an audit record must not contain a value that reads as provenance but is not.
    /// </para>
    /// <para>
    /// A value that fails is REPLACED rather than dropped, so the record still says that the document
    /// declared something and that what it declared was not usable. Dropping it would make a hostile
    /// document indistinguishable from one that declared no version at all.
    /// </para>
    /// </remarks>
    private static string DescribeDeclaredVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > MaximumAuditedVersionLength)
        {
            return UnusableVersion;
        }

        foreach (char character in version)
        {
            if (!char.IsAsciiDigit(character) && character != '.' && character != '-')
            {
                return UnusableVersion;
            }
        }

        return version;
    }

    /// <summary>
    /// Records one module change on the audit trail.
    /// </summary>
    /// <param name="portalId">The tenant the module belongs to.</param>
    /// <param name="moduleId">The module the record is about.</param>
    /// <param name="facts">
    /// The facts describing what happened. Callers pass allowlisted codes, booleans, identifiers, counts and
    /// sizes only.
    /// </param>
    /// <param name="eventName">
    /// The catalogue name describing what happened, from <see cref="AuditEventNames"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy module trail was written by <c>EventLogController.AddLog</c> against the
    /// <c>MODULE_*</c> members of <c>EventLogType</c> (<c>EventLogController.vb:L38-L77</c>). The
    /// event-log storage provider is out of scope, so the record is emitted through
    /// <see cref="IAuditSink"/>, whose Infrastructure implementation writes it as a structured Serilog
    /// event. The names are preserved verbatim, so the trail remains queryable by the identifiers an
    /// operator already knows.
    /// </para>
    /// <para>
    /// MIGRATION: CORRECTION, on two counts. This helper previously hard-coded ONE name for every module
    /// operation, taken from a local constant that cited <c>EventMessageProcessor.vb:L69</c> - a file
    /// AAP 0.2.2.1 excludes - as its provenance. The name was always a member of the in-scope enumeration
    /// and is now drawn from <see cref="AuditEventNames"/> like every other, and the operation is named by
    /// the CALLER rather than assumed: a settings change and a content import are updates, an export
    /// changes nothing and has its own name, and a deletion is a deletion. One name for four operations
    /// meant the trail asserted that a module had changed when it had only been read.
    /// </para>
    /// <para>
    /// <b>WHAT IS NEVER RECORDED.</b> No caller passes content. An export or import payload is module
    /// content: it may be arbitrarily large and it may carry data belonging to the portal's users, so the
    /// trail carries its LENGTH and never any part of it. That is the one rule this helper exists to make
    /// unmissable - a record naming a payload would move user data into the log store, where it is
    /// neither access-controlled as module content nor removable with it. Credentials, hashes, tokens and
    /// connection strings never reach this service at all, so there is nothing here that could leak one.
    /// </para>
    /// <para>
    /// <b>CALLED ONLY AFTER THE COMMIT.</b> A record written before the commit could describe a change
    /// that was then rolled back, which is worse than no record: it is a trail that disagrees with the
    /// database. Every call site below sits after its unit of work has been saved.
    /// </para>
    /// <para>
    /// The actor identifier is carried only when the caller is authenticated. An unauthenticated caller
    /// reaching a write is already refused by the permission checks above, so a null actor here means a
    /// genuinely anonymous path rather than a missing lookup, and recording it as absent is more honest than
    /// recording a placeholder identifier.
    /// </para>
    /// </remarks>
    private void RecordModuleAudit(
        string eventName,
        int portalId,
        int moduleId,
        IReadOnlyDictionary<string, string?> facts)
    {
        AuditEvent record = new(eventName)
        {
            PortalId = portalId,
            ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
            ResourceType = ModuleResourceType,
            ResourceId = moduleId.ToString(CultureInfo.InvariantCulture),
            Properties = facts,
        };

        _audit.Record(record);
    }

}
