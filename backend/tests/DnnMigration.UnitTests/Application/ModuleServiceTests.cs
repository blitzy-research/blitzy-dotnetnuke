// Legacy-parity suite for the module Application service.
//
// Every fact below is pinned to a line of the VB.NET original that this service replaces, and asserts
// the migration decision taken at that line rather than restating the service's functional surface. The
// authoritative sources, all re-verified against this checkout rather than quoted from a plan:
//
//   Library/Components/Modules/ModuleController.vb        1,456 lines. AddContent L226-L254 is the export
//                                                        path; the import path is L410-L441. The cache
//                                                        lifetime idiom appears at L998, L1052, L1264
//                                                        and L1355.
//   Library/Components/Modules/IPortable.vb              L22-L25. Two members, and the only lifecycle
//                                                        contract the export and import flows exercise.
//   Library/Components/Modules/EventMessageProcessor.vb  L29-L48. The deferred import, and a third total
//                                                        exception swallow.
//   Library/Components/Modules/DesktopModuleController.vb Package registration and lifecycle.
//   Library/Components/Modules/ModuleInfo.vb             The 58-property flattened join that is split
//                                                        into four entities here.
//   Library/Components/Shared/Null.vb                    L71-L75 - NullString is the EMPTY STRING, not
//                                                        null. L41 - NullInteger is -1.
//   Website/admin/Modules/{ModuleSettings,Export,Import}.ascx.vb  The admin screens, which compiled with
//                                                        Option Strict OFF per Website/release.config
//                                                        L125 and therefore carried implicit coercions
//                                                        that C# refuses.
//
// Scope discipline. This class mocks Domain and Application abstractions only. It reaches no DbContext,
// no Infrastructure type and no HTTP pipeline: entity-to-column mapping, repository SQL and anything
// needing a database belong to DnnMigration.IntegrationTests/Persistence, permission evaluation belongs
// to Security/PermissionEvaluatorTests, and mapper round-trips belong to Mapping/MappingTests.
using System.Globalization;
using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;

// The DTO namespace above declares a type named Module, so the entity has to be named explicitly or every
// mention of it below would bind to DnnMigration.Application.Dtos.Module instead.
using Module = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.UnitTests.Application;

/// <summary>
/// Asserts that <see cref="ModuleService"/> preserves the behaviour of the legacy VB.NET module
/// controller, and that each place where it deliberately departs from that behaviour departs in the
/// documented direction.
/// </summary>
public class ModuleServiceApplicationTests
{
    /// <summary>
    /// The tenant under test. <c>Portals.PortalID</c> is declared <c>IDENTITY(-1,1)</c>, so -1 is a real
    /// tenant identifier as well as the legacy <c>Null.NullInteger</c> sentinel, and the two meanings
    /// collide on the same value.
    /// </summary>
    private const int PortalId = -1;

    /// <summary>A second tenant, used to prove that a module is never read across a tenant boundary.</summary>
    private const int OtherPortalId = 7;

    /// <summary>
    /// The module under test. <c>Modules.ModuleID</c> is declared <c>IDENTITY(0,1)</c>, so zero is a real
    /// module and must never be read as "no module was named".
    /// </summary>
    private const int ModuleId = 0;

    /// <summary>A second module, used to prove placement ownership is checked.</summary>
    private const int SecondModuleId = 11;

    /// <summary>The placement of <see cref="ModuleId"/> on <see cref="TabId"/>.</summary>
    private const int TabModuleId = 1;

    /// <summary>A second placement of the same module, on <see cref="SecondTabId"/>.</summary>
    private const int SecondTabModuleId = 2;

    /// <summary>The page carrying the module under test.</summary>
    private const int TabId = 5;

    /// <summary>A second page carrying the same module.</summary>
    private const int SecondTabId = 6;

    /// <summary>The definition the module under test instantiates.</summary>
    private const int ModuleDefinitionId = 9;

    /// <summary>The package declaring that definition.</summary>
    private const int DesktopModuleId = 13;

    /// <summary>The authenticated caller.</summary>
    private const int CallerUserId = 42;

    /// <summary>
    /// The registration key. Legacy code read this string from
    /// <c>DesktopModules.BusinessControllerClass</c> and late-bound it; here it is a lookup key into a
    /// closed registration map.
    /// </summary>
    private const string BusinessController = "Dnn.Modules.Html.HtmlController";

    /// <summary>The package name, from which the export document's <c>type</c> attribute is derived.</summary>
    private const string PackageName = "DNN_HTML";

    /// <summary>
    /// The package name after the legacy sanitiser, which is what the export document's <c>type</c>
    /// attribute actually carries.
    /// </summary>
    /// <remarks>
    /// <c>Website/admin/Modules/Export.ascx.vb</c> L159 writes <c>CleanName(objModule.ModuleName)</c>, and
    /// its helper at L209-L221 removes the underscore among a fixed punctuation set - so the seeded
    /// <c>DNN_HTML</c> is written as <c>DNNHTML</c>. Spelled out as its own constant rather than computed
    /// from <see cref="PackageName"/>, because a computed expectation would agree with whatever the
    /// sanitiser happened to do; the difference between the two constants is the whole point.
    /// </remarks>
    private const string SanitisedPackageName = "DNNHTML";

    /// <summary>The package version, which the export document carries as its <c>version</c> attribute.</summary>
    private const string PackageVersion = "04.09.00";

    /// <summary>The definition's friendly name.</summary>
    private const string FriendlyName = "Text/HTML";

    /// <summary>The pane every placement sits in.</summary>
    private const string PaneName = "ContentPane";

    /// <summary>Reported when the request itself is malformed, before any lookup is attempted.</summary>
    private const string RequestInvalidCode = "module.request_invalid";

    /// <summary>Reported when the addressed module does not exist in the addressed tenant.</summary>
    private const string NotFoundCode = "module.not_found";

    /// <summary>Reported when the caller holds no edit grant on the module.</summary>
    private const string EditForbiddenCode = "module.edit_forbidden";

    /// <summary>
    /// Reported when the module cannot move content in either direction, which is the single outcome
    /// covering all three halves of the legacy guards: no registration key, the capability bit clear, and
    /// the capability field still holding the undetermined sentinel.
    /// </summary>
    private const string NotPortableCode = "module.not_portable";

    /// <summary>Reported when the submitted import document is absent or not well-formed.</summary>
    private const string ContentInvalidCode = "module.content_invalid";

    /// <summary>Reported when exported content cannot be represented in the export document format.</summary>
    private const string ExportFailedCode = "module.export_failed";

    /// <summary>
    /// A failure code invented by a module's own controller, used to prove it is propagated verbatim.
    /// </summary>
    private const string ControllerFailureCode = "module.controller_exploded";

    /// <summary>The audit event name a module change is recorded under.</summary>
    private const string ModuleUpdatedEventName = "MODULE_UPDATED";

    /// <summary>
    /// The audit event name a module EXPORT is recorded under.
    /// </summary>
    /// <remarks>
    /// Distinct from the update name on purpose. An export changes nothing, so recording it as an update
    /// asserted something untrue in a trail whose value is that it is believed.
    /// </remarks>
    private const string ModuleExportedEventName = "MODULE_EXPORTED";

    /// <summary>
    /// The cache key the definition catalogue is stored under. The legacy name is preserved verbatim so
    /// that cache behaviour remains auditable against the original.
    /// </summary>
    private const string DefinitionCatalogueCacheKeyFormat = "ModuleDefinitions{0}";

    /// <summary>
    /// The per-entity cache lifetime in minutes. <c>DataCache.ModuleCacheTimeOut</c> and
    /// <c>DataCache.TabModuleCacheTimeOut</c> are both declared <c>= 20</c>
    /// (Library/Components/Providers/Caching/DataCache.vb L58 and L64).
    /// </summary>
    private const int LegacyCacheTimeOutMinutes = 20;

    /// <summary>
    /// The multiplier a legacy installation applied when no host setting had been written.
    /// <c>Common.Globals.PerformanceSetting</c> substitutes 3 for a missing setting
    /// (Library/Components/Shared/Globals.vb L229).
    /// </summary>
    private const int MeasuredLegacyPerformanceMultiplier = 3;

    /// <summary>
    /// The acting user recorded when the caller carries no identity. This is the legacy
    /// <c>Null.NullInteger</c> value, kept rather than replaced so an unattributed write is recognisable
    /// in the same way it was before.
    /// </summary>
    private const int UnattributedUserId = -1;

    /// <summary>
    /// The value <c>DesktopModules.SupportedFeatures</c> holds while a package's capabilities have not
    /// been determined. <c>ModuleController.vb</c> L422 compared the field against
    /// <c>Null.NullInteger</c> and, on a match, parked the payload on the event queue.
    /// </summary>
    private const int UndeterminedCapabilities = -1;

    /// <summary>The capability bit meaning "this package can move its own content".</summary>
    private const int PortableCapability = 1;

    /// <summary>The capability bit meaning "this package is searchable", which export must ignore.</summary>
    private const int SearchableCapability = 2;

    /// <summary>The capability bit meaning "this package is upgradeable", which export must ignore.</summary>
    private const int UpgradeableCapability = 4;

    /// <summary>
    /// Proves that no member of the service contract reports an outcome through a mutated argument.
    /// </summary>
    /// <remarks>
    /// The legacy module and user controllers reported status by writing back through a <c>ByRef</c>
    /// parameter, 30 such signatures across the in-scope tree. The replacement is a
    /// <see cref="Result{T}"/> return, and this fact is what keeps that replacement from eroding: a
    /// reintroduced <c>out</c> or <c>ref</c> parameter fails here rather than passing review.
    /// </remarks>
    [Fact]
    public void ModuleContract_ReportsEveryOutcomeThroughItsReturnValue()
    {
        MethodInfo[] members = typeof(IModuleService).GetMethods();

        members.Should().NotBeEmpty();

        foreach (MethodInfo member in members)
        {
            foreach (ParameterInfo parameter in member.GetParameters())
            {
                parameter.IsOut.Should().BeFalse(
                    "no member may report status through a mutated argument, but {0} declares out parameter {1}",
                    member.Name,
                    parameter.Name);

                parameter.ParameterType.IsByRef.Should().BeFalse(
                    "no member may report status through a mutated argument, but {0} declares {1} by reference",
                    member.Name,
                    parameter.Name);
            }

            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue(
                "every I/O bound member must be awaitable, but {0} returns {1}",
                member.Name,
                member.ReturnType.Name);

            member.GetParameters()[^1].ParameterType.Should().Be(
                typeof(CancellationToken),
                "every member must accept cancellation, but {0} does not end with one",
                member.Name);
        }
    }

    /// <summary>
    /// Proves that the business-controller contract can only be addressed by registration key, which is
    /// what makes assembly probing structurally impossible rather than merely discouraged.
    /// </summary>
    /// <remarks>
    /// Five legacy sites late-bound a controller from a string:
    /// <c>ModuleController.vb</c> L231 and L431, and <c>EventMessageProcessor.vb</c> L32, L52 and L77,
    /// each calling <c>Framework.Reflection.CreateObject</c>. All five are subsumed by the four members
    /// asserted here. A member that accepted or returned a <see cref="Type"/>, an
    /// <see cref="Assembly"/> or an <see cref="object"/> would reopen the late-binding route, so the
    /// absence of any such member is the property under test.
    /// </remarks>
    [Fact]
    public void BusinessControllerContract_IsAddressableOnlyByRegistrationKey()
    {
        MethodInfo[] members = typeof(IModuleBusinessControllerFactory).GetMethods();

        members.Should().HaveCount(4);

        foreach (MethodInfo member in members)
        {
            ParameterInfo[] parameters = member.GetParameters();

            parameters[0].Name.Should().Be(
                "businessControllerClass",
                "resolution must start from the registration key, but {0} starts elsewhere",
                member.Name);

            parameters[0].ParameterType.Should().Be(
                typeof(string),
                "the registration key is a lookup name, not a resolved type");

            foreach (ParameterInfo parameter in parameters)
            {
                parameter.ParameterType.Should().NotBe(
                    typeof(Type),
                    "a member taking a Type would reopen run-time type resolution ({0})",
                    member.Name);

                parameter.ParameterType.Should().NotBe(
                    typeof(Assembly),
                    "a member taking an Assembly would reopen assembly probing ({0})",
                    member.Name);
            }

            member.ReturnType.Should().NotBe(
                typeof(Task<object>),
                "handing back an untyped instance would restore the legacy TypeOf probe ({0})",
                member.Name);
        }
    }

    /// <summary>
    /// Proves the first half of the legacy export guard on its own: a package with no registration key is
    /// refused, and the module is never asked for content.
    /// </summary>
    /// <param name="registrationKey">The absent key, in each of the three forms absence takes.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <c>ModuleController.vb</c> L229 reads
    /// <c>If objModule.BusinessControllerClass &lt;&gt; "" And objModule.IsPortable Then</c>. That is
    /// TWO conditions joined by VB's <c>And</c>, which - unlike <c>AndAlso</c> - evaluates both operands
    /// rather than short-circuiting. Each half is therefore asserted separately, because a single test
    /// that failed both halves at once would pass even if the target had collapsed them into one check.
    /// <para>
    /// The empty string is included deliberately. <c>Null.vb</c> L71-L75 defines <c>NullString</c> as
    /// <c>""</c> rather than null, so the empty string IS the legacy absent marker for a string column and
    /// has to be refused exactly as a null is. Whitespace is included because a key of spaces is no more
    /// resolvable than a key of none.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportModule_WithNoRegistrationKey_RefusesWithoutAskingTheModule(string? registrationKey)
    {
        Harness harness = Harness.Ready();
        harness.Package.BusinessControllerClass = registrationKey;

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(NotPortableCode);

        harness.BusinessControllers.Verify(
            factory => factory.ExportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Proves the second half of the legacy export guard on its own: a package that holds a registration
    /// key but does not declare the portable capability is refused.
    /// </summary>
    /// <param name="supportedFeatures">A capability field with the portable bit clear.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The registration key is present throughout, so only <c>IsPortable</c> - the second operand of
    /// <c>ModuleController.vb</c> L229 - can account for the refusal. The searchable and upgradeable bits
    /// are set in two of the cases to prove the capability field is read bitwise rather than as a truthy
    /// integer: <c>DesktopModule.IsPortable</c> masks bit 1, and a package that is searchable and
    /// upgradeable but not portable has a non-zero field and still cannot export.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(SearchableCapability)]
    [InlineData(UpgradeableCapability)]
    [InlineData(SearchableCapability | UpgradeableCapability)]
    public async Task ExportModule_WithThePortableCapabilityClear_RefusesWithoutAskingTheModule(int supportedFeatures)
    {
        Harness harness = Harness.Ready();
        harness.Package.BusinessControllerClass = BusinessController;
        harness.Package.SupportedFeatures = supportedFeatures;

        harness.Package.IsPortable.Should().BeFalse("the case under test must have the portable bit clear");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(NotPortableCode);

        harness.BusinessControllers.Verify(
            factory => factory.ExportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Proves that when both halves of the legacy guard hold, the module is asked for its content - and
    /// asked for exactly the module under export, once.
    /// </summary>
    /// <param name="supportedFeatures">A capability field with the portable bit set.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The combined case is the positive control for the two negative theories above. The second value
    /// carries the searchable bit alongside the portable one, because the legacy probe recorded all three
    /// capability flags in a single column and an implementation that compared the field for equality with
    /// 1 rather than masking it would refuse a package that is legitimately portable.
    /// <para>
    /// The single-invocation check is the anti-probing assertion: resolution happens once, by key, with no
    /// retry against a different name and no enumeration of candidates.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PortableCapability)]
    [InlineData(PortableCapability | SearchableCapability)]
    [InlineData(PortableCapability | SearchableCapability | UpgradeableCapability)]
    public async Task ExportModule_WithBothHalvesOfTheGuardSatisfied_AsksTheRegisteredControllerOnce(
        int supportedFeatures)
    {
        Harness harness = Harness.Ready();
        harness.Package.BusinessControllerClass = BusinessController;
        harness.Package.SupportedFeatures = supportedFeatures;

        harness.Package.IsPortable.Should().BeTrue("the case under test must have the portable bit set");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.BusinessControllers.Verify(
            factory => factory.ExportModuleContentAsync(
                BusinessController,
                ModuleId,
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.BusinessControllers.Verify(
            factory => factory.ExportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Proves that a capability field still holding the legacy undetermined sentinel refuses the export
    /// rather than treating the sentinel as a set of flags.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// -1 is <c>Null.NullInteger</c> (<c>Null.vb</c> L41) and, read as a bit field, has every bit set -
    /// so a naive mask would report the package portable, searchable and upgradeable all at once.
    /// <c>DesktopModule.IsPortable</c> guards <c>SupportedFeatures &gt; -1</c> ahead of the mask for
    /// exactly that reason. This is the sentinel-at-the-boundary rule in its sharpest form: the value is
    /// data in the column, so it is interpreted rather than assumed away.
    /// </remarks>
    [Fact]
    public async Task ExportModule_WithUndeterminedCapabilities_RefusesRatherThanReadingTheSentinelAsFlags()
    {
        Harness harness = Harness.Ready();
        harness.Package.BusinessControllerClass = BusinessController;
        harness.Package.SupportedFeatures = UndeterminedCapabilities;

        harness.Package.IsPortable.Should().BeFalse();
        harness.Package.IsSearchable.Should().BeFalse();
        harness.Package.IsUpgradeable.Should().BeFalse();

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(NotPortableCode);
    }

    /// <summary>
    /// Proves the exported document carries the module's payload under the legacy element and attribute
    /// names, and that the payload is spliced in without any escaping layer over it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The authority for this endpoint is the STANDALONE admin exporter,
    /// <c>Website/admin/Modules/Export.ascx.vb</c> lines 157-164, which built a <c>content</c> element
    /// carrying a <c>type</c> attribute holding <c>CleanName(objModule.ModuleName)</c> and a
    /// <c>version</c> attribute, and concatenated what the module returned DIRECTLY between the tags. The
    /// element and attribute names are part of the file format, so a legacy export file stays readable here
    /// and a file written here stays readable by the legacy importer.
    /// <para>
    /// MIGRATION: the legacy coercion in that method was
    /// <c>CType(CType(objObject, IPortable).ExportModule(ModuleID), String)</c> - a cast to String applied
    /// to a member already declared <c>As String</c>, which is the kind of redundant conversion Option
    /// Strict OFF made invisible. It is made explicit here as a typed <c>Result&lt;string&gt;</c>: the
    /// outer cast disappears because the contract cannot return anything else, and the nullability of what
    /// the module hands back is expressed by the factory returning <c>Result&lt;string?&gt;</c> rather than
    /// by a runtime cast that would have thrown.
    /// </para>
    /// <para>
    /// MIGRATION: THE EXPECTED DOCUMENT USED TO CARRY A DOUBLY-ESCAPED PAYLOAD AND THE RAW PACKAGE NAME, and
    /// this paragraph replaces the one that defended both. The service HTML-escaped the payload, turning
    /// <c>&lt;</c> into <c>&amp;lt;</c>, and the XML writer then escaped the ampersand that produced, giving
    /// <c>&amp;amp;lt;</c> - so only a reader that knew to undo a layer the format does not declare could
    /// recover the payload. That encode belongs to the PORTAL TEMPLATE writer at
    /// <c>ModuleController.vb</c> L244; the module admin exporter this endpoint migrates escapes nothing.
    /// The type attribute is now the SANITISED name, which is what the legacy wrote and what its importer
    /// compares against, and the document opens with the declaration the legacy emitted as a literal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExportModule_WrapsThePayloadUnderTheLegacyElementAndAttributeNames()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success("<item>one</item>");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().Be(
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
            + $"<content type=\"{SanitisedPackageName}\" version=\"{PackageVersion}\">"
            + "<item>one</item></content>");
    }

    /// <summary>
    /// Records the one place where the export path departs from the legacy behaviour: a module that hands
    /// back the empty string still produces a document, where the legacy silently produced nothing.
    /// </summary>
    /// <param name="payload">Content the legacy path would have discarded, or nearly so.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: <c>ModuleController.vb</c> L234 guarded the append with <c>If Content &lt;&gt; ""</c>
    /// and, when the module returned the empty string, appended no <c>content</c> node at all - so the
    /// portal template it was writing came out silently short of one module's content, with no record that
    /// anything had been dropped. This service does not reproduce that. An empty payload is a payload: the
    /// document is written, the element is present, and the caller can see that the module was asked and
    /// answered with nothing. The divergence exists because the legacy behaviour is indistinguishable from
    /// data loss at the point it happens, and it is recorded in MIGRATION_NOTES.md rather than absorbed.
    /// <para>
    /// The whitespace cases are the reason this matters in practice. <c>Null.NullString</c> is <c>""</c>
    /// (<c>Null.vb</c> L71-L75), so the legacy check conflated "the module has no content" with "the
    /// column was null", but it did NOT conflate either with a payload of spaces - which slipped through
    /// the guard and was written. Preserving content whose significance only the module knows is the point:
    /// whitespace is carried through unchanged rather than trimmed away.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task ExportModule_WithEmptyOrWhitespaceContent_StillProducesADocument(string payload)
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success(payload);

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().Be(
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
            + $"<content type=\"{SanitisedPackageName}\" version=\"{PackageVersion}\">{payload}</content>");
    }

    /// <summary>
    /// Proves that a failure raised inside a module's own controller is reported rather than discarded on
    /// the export path.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: <c>ModuleController.vb</c> L250-L252 closed the export block with a bare
    /// <c>Catch</c> whose entire body is the comment <c>'ignore errors</c>, so a module that threw while
    /// exporting produced a template missing that module's content and reported success. This service
    /// propagates the failure verbatim - code and message - instead. It is the same swallow that L435-L437
    /// applies on import and that <c>EventMessageProcessor.vb</c> L45-L47 applies on the deferred path;
    /// all three are refused rather than reproduced.
    /// <para>
    /// A silently incomplete export is data loss disguised as success, so this is treated as a legacy
    /// defect that must be surfaced rather than as a business rule to preserve. The divergence is recorded
    /// in MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExportModule_WhenTheControllerFails_ReportsItInsteadOfSwallowingIt()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Failure(
            ControllerFailureCode,
            "The module's own export raised.");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ControllerFailureCode);
        outcome.Error.Message.Should().Be("The module's own export raised.");
    }

    /// <summary>
    /// Proves that a registration key naming nothing in the closed set produces a distinct, explanatory
    /// failure rather than a search for the type.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy behaviour here was <c>Framework.Reflection.CreateObject</c>
    /// (<c>ModuleController.vb</c> L231) followed by <c>If TypeOf objObject Is IPortable</c> at L232:
    /// an unresolvable name threw inside the block and the swallow at L250-L252 turned that into silence.
    /// A resolvable name that did not implement the interface fell off the end of the <c>If</c>, equally
    /// silently.
    /// <para>
    /// Both become one observable outcome: the key was not registered, expressed as a failure whose
    /// message names the module and carries the factory's own advisory. Nothing is loaded, enumerated or
    /// constructed from the string, which is what "closed set" means operationally.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExportModule_WithAnUnregisteredKey_FailsWithAnAdvisoryRatherThanProbingForTheType()
    {
        Harness harness = Harness.Ready();
        harness.Package.BusinessControllerClass = "Contoso.Modules.NeverRegistered.Controller";
        harness.ExportOutcome = Result<string?>.Success(
            null,
            new ResultReason(NotPortableCode, "no registered business controller covers this module."));

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(NotPortableCode);
        outcome.Error.Message.Should().Contain("no registered business controller covers this module.");

        harness.BusinessControllers.Verify(
            factory => factory.ExportModuleContentAsync(
                "Contoso.Modules.NeverRegistered.Controller",
                ModuleId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Proves that content which cannot be represented in the document format is reported as a failure and
    /// that the module's own text is never quoted back in the message.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// HTML escaping covers markup but the XML specification provides no escape at all for a C0 control
    /// character, so a module returning one produces content this format cannot carry. The legacy swallow
    /// would have hidden it. The message deliberately excludes the payload because export content is
    /// module data and may carry the portal's users' data, and a failure message is published to the
    /// caller.
    /// </remarks>
    [Fact]
    public async Task ExportModule_WithUnrepresentableContent_FailsWithoutQuotingThePayload()
    {
        // A C0 control character, which no XML escape can represent, followed by a marker that stands in for
        // whatever portal data a real module's content might carry. The marker is obviously synthetic on
        // purpose: the point of the fact is that it must not reappear in the failure message.
        const string unrepresentableContent = "\u0001 portal-owned-content-marker";

        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success(unrepresentableContent);

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ExportFailedCode);
        outcome.Error.Message.Should().NotContain("portal-owned-content-marker");
    }

    /// <summary>
    /// Proves the export refuses content beyond its stated ceiling instead of assembling a document of
    /// whatever size a module chose to hand over.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The payload of an export arrives from MODULE code rather than from the caller's request, so the
    /// API's request-body limit does not bound it and nothing else did: every step after it - HTML escaping,
    /// XML escaping and document assembly - is proportional to its length, and an unbounded payload
    /// therefore bought unbounded processor and memory work from a single request.
    /// <para>
    /// The ceiling is restated here as the suite's own expectation rather than read from the
    /// implementation, so that moving the implementation's constant without considering the consequence
    /// fails this fact instead of silently redefining what it proves. It is refused with the export
    /// failure code, not a request-invalid one, because the caller supplied nothing that could be
    /// corrected - which is the same distinction that code already draws for content XML cannot represent.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy export had no ceiling; it encoded whatever the module returned and wrote it to
    /// disk, bounded only by a disk-space check that the excluded file subsystem performed. The divergence
    /// is recorded in MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExportModule_BeyondTheContentCeiling_IsRefusedRatherThanAssembled()
    {
        // One character past the documented ceiling of one mebibyte of characters.
        const int exportPayloadCeiling = 1024 * 1024;

        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success(new string('x', exportPayloadCeiling + 1));

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ExportFailedCode);
        outcome.Error.Message.Should().Contain(
            (exportPayloadCeiling + 1).ToString(CultureInfo.InvariantCulture),
            "the refusal states what arrived so an operator can size the module's content against the bound");

        // The bound is a ceiling and not a lower limit: a payload exactly at it is carried, which is what
        // stops the guard from being a silent narrowing of one character.
        Harness atTheBound = Harness.Ready();
        atTheBound.ExportOutcome = Result<string?>.Success(new string('x', exportPayloadCeiling));

        Result<string> permitted = await atTheBound.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        permitted.IsSuccess.Should().BeTrue(permitted.Reason?.ToString());
        permitted.Value.Should().Contain(new string('x', 32), "the payload is carried, not summarised");
    }

    /// <summary>
    /// Proves the export path writes nothing, so no commit is attempted on any of its outcomes.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Export reads a module, asks its controller for content and returns a document. A commit here would
    /// mean the read path had acquired a side effect, which is why the absence is asserted rather than
    /// assumed. The audit entry is emitted regardless, because the fact that content left the portal is
    /// itself worth recording - and it records the payload's LENGTH, never the payload.
    /// </remarks>
    [Fact]
    public async Task ExportModule_CommitsNothingBecauseItOnlyReads()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success("<item>one</item>");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        AuditEvent recorded = Assert.Single(harness.AuditTrail);
        recorded.EventName.Should().Be(ModuleExportedEventName);
        recorded.Properties["Operation"].Should().Be("Export");
        recorded.Properties["PayloadLength"].Should().Be("16");
        recorded.Properties.Values.Should().NotContain("<item>one</item>");
    }

    /// <summary>
    /// Proves the submitted document's payload reaches the module as the document's INNER XML, unaltered,
    /// and that no escaping decision is taken in either direction.
    /// </summary>
    /// <param name="documentPayload">The payload as it appears inside the document.</param>
    /// <param name="expected">What the module must receive.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: <c>Website/admin/Modules/Import.ascx.vb</c> L200 passes
    /// <c>xmlDoc.DocumentElement.InnerXml</c>, so an entity reference the document carries reaches the module
    /// still escaped - <c>InnerXml</c> returns markup, not text. The rows below are the markup as written and
    /// the markup as received, and every pair is identity: the format declares no escaping layer, so there is
    /// none to undo.
    /// <para>
    /// MIGRATION: THIS FACT USED TO ASSERT THE OPPOSITE, and it is retargeted rather than deleted because the
    /// property it guards - what the module receives - is still the right one to pin. It asserted that a
    /// payload was HTML-DECODED once, taking <c>ModuleController.vb</c> L428's
    /// <c>Server.HtmlDecode</c> as its authority. That line belongs to the PORTAL TEMPLATE reader, whose
    /// writer at L244 had encoded the payload first; applied to a module admin document it corrupted every
    /// entity reference the content legitimately carried, and the corruption was invisible only because the
    /// matching encode on the export side undid it again. The rows that used to read
    /// <c>"a &amp;amp; b" -&gt; "a &amp; b"</c> now read <c>"a &amp;amp; b" -&gt; "a &amp;amp; b"</c>, which
    /// is what <c>InnerXml</c> yields and what the module's own reader will resolve for itself.
    /// </para>
    /// <para>
    /// The concern the old title raised is still honoured and is worth restating: nothing here touches an
    /// ambient request context. Only 8 of the 84 in-scope legacy files referenced <c>System.Web</c> at all,
    /// and the one place the target injects an HTTP accessor is a single piece of API middleware. The
    /// dependency disappears with the transformation rather than being replaced.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("&amp;lt;item&amp;gt;one&amp;lt;/item&amp;gt;", "&amp;lt;item&amp;gt;one&amp;lt;/item&amp;gt;")]
    [InlineData("a &amp;amp; b", "a &amp;amp; b")]
    [InlineData("plain text", "plain text")]
    [InlineData("&amp;quot;quoted&amp;quot;", "&amp;quot;quoted&amp;quot;")]
    [InlineData("&quot;quoted&quot;", "\"quoted\"")]
    [InlineData("&#60;item&#62;", "&lt;item&gt;")]
    public async Task ImportModule_HandsThePayloadToTheModuleAsTheDocumentsInnerXml(
        string documentPayload,
        string expected)
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest
            {
                ModuleId = ModuleId,
                Content = $"<content type=\"{SanitisedPackageName}\" version=\"{PackageVersion}\">{documentPayload}</content>",
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.ImportedContent.Should().Be(expected);
    }

    /// <summary>
    /// Proves a payload that is itself markup is handed on as markup, with the entity reference it carries
    /// left exactly as written.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the counterpart to the theory above: the rule is uniform, so an element child serialises as
    /// markup for the same reason a text child serialises as escaped text - both are the root's inner XML.
    /// The fixture's <c>&amp;amp;</c> is the load-bearing part. Under the decode an earlier revision applied
    /// it would have arrived as a bare ampersand, leaving the module with markup that is no longer
    /// well-formed, so this assertion is what prevents the decode being reintroduced.
    /// </remarks>
    [Fact]
    public async Task ImportModule_WithAMarkupPayload_HandsItOnWithoutUnescapingIt()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest
            {
                ModuleId = ModuleId,
                Content = $"<content type=\"{SanitisedPackageName}\" version=\"{PackageVersion}\">"
                    + "<item>a &amp; b</item></content>",
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.ImportedContent.Should().Be("<item>a &amp; b</item>");
    }

    /// <summary>
    /// Proves a CDATA section survives as a CDATA section, delimiters included, because that is what
    /// <c>InnerXml</c> returns.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The third member of the payload group, covering the one node kind whose markup form differs most from
    /// its text form. There is no branch on document shape: an element, a text node and a CDATA section are
    /// all written back as the markup they are, which is exactly the semantics of the
    /// <c>DocumentElement.InnerXml</c> the legacy importer passed on.
    /// <para>
    /// MIGRATION: THIS FACT USED TO CLAIM THE SECTION WAS UNWRAPPED AND DECODED, and was named for accepting a
    /// document written by "the legacy exporter". It was wrong about which one. The CDATA-and-encode shape is
    /// written by <c>ModuleController.vb</c> L244-L246, the PORTAL TEMPLATE writer, and read by the portal
    /// template parser - never by the module import screen, whose own exporter wraps nothing. Unwrapping it
    /// here required a branch on whether the content element had child elements, and that branch silently
    /// mangled module content that legitimately contained a CDATA section of its own. Recorded in
    /// MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportModule_HandsOnACdataSectionAsMarkupRatherThanText()
    {
        Harness harness = Harness.Ready();
        const string cdataSection = "<![CDATA[&lt;item&gt;one&lt;/item&gt;]]>";

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest
            {
                ModuleId = ModuleId,
                Content = $"<content type=\"{SanitisedPackageName}\" version=\"{PackageVersion}\">"
                    + cdataSection + "</content>",
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.ImportedContent.Should().Be("<![CDATA[&lt;item&gt;one&lt;/item&gt;]]>");
    }

    /// <summary>
    /// Records the unification of the two conflicting legacy acting-user identities onto the caller.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: the legacy tree attributed an import to two different people depending on which entry
    /// point was used. <c>ModuleController.vb</c> L433 passed
    /// <c>objportal.AdministratorId</c> - the portal administrator, regardless of who was signed in - while
    /// <c>Website/admin/Modules/Import.ascx.vb</c> L200 passed <c>UserInfo.UserID</c>, the current user.
    /// The deferred path at L426 used the administrator too. Identical operations therefore produced
    /// different provenance, and the administrator variant attributed a change to someone who had not made
    /// it.
    /// <para>
    /// The two are unified onto the caller, which is the only identity that is true in both cases. That is
    /// a deliberate divergence from L433 and is recorded in MIGRATION_NOTES.md. It is also what makes the
    /// permission check meaningful: the caller is the party whose edit grant was verified, so the caller is
    /// the party the write is recorded against.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportModule_AttributesTheWriteToTheCallerRatherThanThePortalAdministrator()
    {
        Harness harness = Harness.Ready();
        harness.CallerId = CallerUserId;

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.ImportedByUserId.Should().Be(CallerUserId);

        harness.BusinessControllers.Verify(
            factory => factory.ImportModuleContentAsync(
                BusinessController,
                ModuleId,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                CallerUserId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Proves an unattributed caller is recorded as the legacy integer sentinel rather than as a real user.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <c>Null.NullInteger</c> is -1 (<c>Null.vb</c> L41), and it is kept for this case rather than
    /// replaced by a null so that an unattributed write reads the same way it always did in the underlying
    /// tables. The sentinel survives at the boundary; the domain model itself expresses absence with a
    /// nullable type, which is what <see cref="ICurrentUser.UserId"/> already does.
    /// <para>
    /// Zero would have been the wrong fallback. <c>Users.UserID</c> is an identity column and the legacy
    /// schema seeds identities low, so a zero fallback risks attributing an anonymous write to a real
    /// account - which is precisely the class of collision the sentinel analysis exists to prevent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportModule_WithAnUnattributedCaller_RecordsTheLegacySentinelRatherThanZero()
    {
        Harness harness = Harness.Ready();
        harness.CallerId = null;

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.ImportedByUserId.Should().Be(UnattributedUserId);
        harness.ImportedByUserId.Should().NotBe(0, "zero is a real identity value in this schema");
    }

    /// <summary>
    /// Records the omission of the deferred-import event queue: an undetermined capability field is refused
    /// with an actionable reason instead of parking the payload somewhere the caller cannot observe.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: <c>ModuleController.vb</c> L422 tested
    /// <c>If objModule.SupportedFeatures = Null.NullInteger</c> and, on a match, called
    /// <c>CreateEventQueueMessage</c> at L426 to park the payload for replay after an application restart -
    /// because it discovered a module's capabilities by late-binding its controller at run time, which was
    /// impossible during the request that installed the module. The queue subsystem is excluded from this
    /// migration wholesale, so there is nowhere to park anything, and no queue is reintroduced: inventing
    /// one would be scope creep dressed as fidelity.
    /// <para>
    /// Nothing is lost, because the condition the branch waited for cannot arise. Capabilities come from a
    /// closed registration map fixed at start-up, so a capability is either registered or it is not and
    /// waiting changes nothing. The sentinel is still handled rather than ignored - it reports the package
    /// as not portable - so the caller gets a refusal they can act on instead of a success that quietly
    /// deferred. Recorded in MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportModule_WithUndeterminedCapabilities_RefusesInsteadOfDeferringToAQueue()
    {
        Harness harness = Harness.Ready();
        harness.Package.SupportedFeatures = UndeterminedCapabilities;

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(NotPortableCode);

        harness.BusinessControllers.Verify(
            factory => factory.ImportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Proves a failure raised inside a module's own controller is reported on the import path, and that
    /// nothing is committed and nothing is recorded when it is.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: <c>ModuleController.vb</c> L435-L437 is a bare <c>Catch</c> whose body is the comment
    /// <c>'ignore errors</c>, and <c>EventMessageProcessor.vb</c> L45-L47 is the same construct with the
    /// comment <c>' an error occurred</c>. Either one turned a module that threw mid-import into a
    /// successful-looking no-op, losing the submitted content outright. Neither is reproduced.
    /// <para>
    /// The three assertions are one statement in three parts: the caller is told, nothing was written, and
    /// no audit entry claims otherwise. The last matters most - an audit trail that records a success that
    /// did not happen is worse than no trail at all - and it is why the service emits the entry after the
    /// commit rather than before the attempt.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportModule_WhenTheControllerFails_ReportsItAndLeavesNoTraceOfSuccess()
    {
        Harness harness = Harness.Ready();
        harness.ImportOutcome = Result.Failure(
            ControllerFailureCode,
            "The module's own import raised.");

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ControllerFailureCode);
        outcome.Error.Message.Should().Be("The module's own import raised.");

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        harness.AuditTrail.Should().BeEmpty();
    }

    /// <summary>
    /// Proves a successful import commits exactly once, through the unit of work.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy import had no commit boundary of its own: each statement travelled to the database
    /// independently, so a sequence that failed halfway left the tables in a state no single operation had
    /// intended. One commit, at one point, is what replaces that - and "exactly once" is asserted rather
    /// than "at least once" because a second commit would mean the method had two boundaries and therefore
    /// none.
    /// </remarks>
    [Fact]
    public async Task ImportModule_OnSuccess_CommitsExactlyOnce()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Proves that every refusal the import path can produce leaves the database untouched.
    /// </summary>
    /// <param name="scenario">Which refusal to provoke.</param>
    /// <param name="expectedCode">The failure code that refusal must report.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Each case is a distinct guard, and the theory exists so that adding a guard which forgets to return
    /// before the commit fails here. The scenario names map to the guards in the order the service applies
    /// them: the module was not named, the module is not the tenant's, the caller holds no grant, the
    /// document is empty, the document is not well-formed, and the document has the wrong root.
    /// <para>
    /// A cancellation token is passed explicitly on every call, so a guard that returned before the token
    /// reached the repository would still be exercised with one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("no-module-named", RequestInvalidCode)]
    [InlineData("another-tenants-module", NotFoundCode)]
    [InlineData("no-edit-grant", EditForbiddenCode)]
    [InlineData("empty-document", ContentInvalidCode)]
    [InlineData("malformed-document", ContentInvalidCode)]
    [InlineData("wrong-root", ContentInvalidCode)]
    [InlineData("controller-refused", ControllerFailureCode)]
    public async Task ImportModule_OnEveryRefusal_CommitsNothing(string scenario, string expectedCode)
    {
        Harness harness = Harness.Ready();
        var request = new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") };
        int addressedPortalId = PortalId;

        switch (scenario)
        {
            case "no-module-named":
                request.ModuleId = null;
                break;

            case "another-tenants-module":
                addressedPortalId = OtherPortalId;
                break;

            case "no-edit-grant":
                harness.EditGranted = false;
                break;

            case "empty-document":
                request.Content = "   ";
                break;

            case "malformed-document":
                request.Content = $"<content type=\"{SanitisedPackageName}\"><unclosed>";
                break;

            case "wrong-root":
                request.Content = "<module type=\"x\" version=\"y\">one</module>";
                break;

            default:
                harness.ImportOutcome = Result.Failure(ControllerFailureCode, "The module refused.");
                break;
        }

        Result outcome = await harness.Service.ImportModuleAsync(
            addressedPortalId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(expectedCode);

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Proves a failed permission evaluation is treated as a refusal rather than as a grant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The evaluation returns a <see cref="Result{T}"/>, so it has three outcomes and not two: granted,
    /// denied, and could-not-be-determined. Reading only the value would make the third case indistinguishable
    /// from the second - or, worse, from the first if the default of <see cref="bool"/> were consulted after
    /// a failure. Failing closed is the only safe reading, and this fact pins it.
    /// </remarks>
    [Fact]
    public async Task ImportModule_WhenThePermissionEvaluationFails_RefusesRatherThanAssumingAGrant()
    {
        Harness harness = Harness.Ready();
        harness.PermissionOutcome = Result<bool>.Failure(
            "permission.unavailable",
            "The permission store could not be reached.");

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(EditForbiddenCode);

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Proves module zero is treated as a real module, because the identity column seeds at zero.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <c>Modules.ModuleID</c> is declared <c>IDENTITY(0,1)</c>, so the first module ever created in a
    /// DotNetNuke database has the identifier zero. Any code that treats zero as "unset" - the reflex in
    /// most schemas - silently refuses a legitimate row. The whole of this suite addresses module zero for
    /// that reason, and this fact states the property directly so the reason survives.
    /// </remarks>
    [Fact]
    public async Task ImportModule_TreatsModuleZeroAsARealModule()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = 0, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.Modules.Verify(
            repository => repository.GetByIdAsync(0, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Proves tenant -1 is treated as a real tenant, because the portal identity column seeds at -1.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY(-1,1)</c>. The value -1 is therefore simultaneously
    /// the first real portal and the legacy <c>Null.NullInteger</c> sentinel, and the collision is genuine
    /// rather than theoretical - the seeded baseline portal in this project's own database has
    /// <c>PortalID = -1</c>. A guard that rejected negative identifiers as absent would lock the primary
    /// tenant out of its own modules.
    /// </remarks>
    [Fact]
    public async Task ImportModule_TreatsTenantMinusOneAsARealTenant()
    {
        Harness harness = Harness.Ready();
        harness.Module.PortalId = -1;

        Result outcome = await harness.Service.ImportModuleAsync(
            -1,
            new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Proves an omitted module identifier is refused by absence alone, never by the sign of a value.
    /// </summary>
    /// <param name="namedModuleId">An identifier a naive guard would reject as unset.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the consequence of the two preceding facts taken together. Because both 0 and -1 are real
    /// identifiers in this schema, neither can be used to signal "nothing was named" - so
    /// <c>ModuleImportRequest.ModuleId</c> is nullable and the omission is expressed by the null, not by a
    /// magic value. A caller that supplies either number has made a lookup, and it must be allowed to fail
    /// as a lookup rather than be misreported as a malformed request.
    /// <para>
    /// The two supplied values reach the repository and come back absent, so they report
    /// <c>module.not_found</c>; the omission reports <c>module.request_invalid</c>. Two different codes for
    /// two different situations is the whole point.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ImportModule_DistinguishesAnUnknownModuleFromAnUnnamedOne(int namedModuleId)
    {
        Harness unknown = Harness.Ready();
        unknown.StoredModule = null;

        Result lookupFailed = await unknown.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = namedModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        lookupFailed.IsFailure.Should().BeTrue();
        lookupFailed.Error!.Code.Should().Be(NotFoundCode);

        Harness unnamed = Harness.Ready();

        Result nothingNamed = await unnamed.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = null, Content = Harness.Document("one") },
            CancellationToken.None);

        nothingNamed.IsFailure.Should().BeTrue();
        nothingNamed.Error!.Code.Should().Be(RequestInvalidCode);
    }

    /// <summary>
    /// Proves the configured multiplier defaults to the value a legacy installation actually used.
    /// </summary>
    /// <remarks>
    /// <c>Common.Globals.PerformanceSetting</c> (Library/Components/Shared/Globals.vb L229) read the host
    /// setting and substituted 3 when none had been written, so 3 - not 1 and not 0 - is the behaviour an
    /// unconfigured legacy site exhibited. Carrying that default across is what makes cache lifetimes in
    /// the migrated system match the system it replaces without anyone having to configure anything.
    /// </remarks>
    [Fact]
    public void CachingOptions_DefaultToTheMultiplierAnUnconfiguredLegacySiteUsed()
    {
        var options = new CachingOptions();

        options.PerformanceMultiplier.Should().Be(MeasuredLegacyPerformanceMultiplier);
    }

    /// <summary>
    /// Proves the cache lifetime is the legacy per-entity timeout multiplied by the configured multiplier.
    /// </summary>
    /// <param name="multiplier">The configured multiplier.</param>
    /// <param name="expectedMinutes">The lifetime that multiplier must produce.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy idiom is a per-entity constant multiplied by a global setting, and it appears four times
    /// in the module controller alone:
    /// <c>DataCache.ModuleCacheTimeOut * Convert.ToInt32(Common.Globals.PerformanceSetting)</c> at L998,
    /// the tab-module equivalent at L1052, and a literal <c>20 *</c> form at L1264 and L1355. Both named
    /// constants are declared <c>= 20</c>, which is where the base figure comes from.
    /// <para>
    /// The default case is asserted alongside three others so that the arithmetic is pinned as arithmetic:
    /// a implementation that hard-coded 60 minutes would satisfy the default and fail every other row.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(MeasuredLegacyPerformanceMultiplier, 60)]
    [InlineData(1, 20)]
    [InlineData(2, 40)]
    [InlineData(5, 100)]
    public async Task ListModuleDefinitions_SetsTheCacheLifetimeToTheLegacyTimeoutTimesTheMultiplier(
        int multiplier,
        int expectedMinutes)
    {
        Harness harness = Harness.Ready();
        harness.Caching.PerformanceMultiplier = multiplier;

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome =
            await harness.Service.ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.CacheExpiration.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
        harness.CacheExpiration.Should().Be(
            TimeSpan.FromMinutes(LegacyCacheTimeOutMinutes * multiplier),
            "the lifetime must be computed rather than tabulated");
    }

    /// <summary>
    /// Proves the catalogue is cached under the legacy key name, keyed by tenant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy cache keys were bare strings composed at each call site, which made cache behaviour
    /// auditable only by reading every site. They are constants here, but the NAMES are preserved: a key
    /// that changed shape would make a migrated installation's cache contents unrecognisable against the
    /// original, and would silently bypass any operational tooling that inspects them.
    /// <para>
    /// The tenant is part of the key, which is the multi-tenant safety property: without it one portal's
    /// definition catalogue would be served to another.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListModuleDefinitions_CachesUnderThePreservedLegacyKeyNameKeyedByTenant()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome =
            await harness.Service.ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.CacheKey.Should().Be(
            string.Format(CultureInfo.InvariantCulture, DefinitionCatalogueCacheKeyFormat, PortalId));
        harness.CacheKey.Should().Be("ModuleDefinitions-1");
    }

    /// <summary>
    /// Records that disabling caching disables caching only, and does not also suppress the read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: several legacy callers treated a zero lifetime as an instruction to skip the database
    /// read outright, on the grounds that the query was too expensive to repeat per request - so a
    /// configuration value silently changed what an endpoint reported, not merely how fast it reported it.
    /// That is not reproduced. With the multiplier at zero the cache is bypassed and the catalogue is still
    /// read and still returned. Recorded in MIGRATION_NOTES.md.
    /// </remarks>
    [Fact]
    public async Task ListModuleDefinitions_WithCachingDisabled_StillReadsTheCatalogue()
    {
        Harness harness = Harness.Ready();
        harness.Caching.PerformanceMultiplier = 0;

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome =
            await harness.Service.ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        ModuleDefinitionDto only = Assert.Single(outcome.Value);
        only.FriendlyName.Should().Be(FriendlyName);

        harness.CacheKey.Should().BeNull("the cache must not have been consulted at all");
        harness.Definitions.Verify(
            repository => repository.GetModuleDefinitionsByPortalIdAsync(
                PortalId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Proves invalidation names the pages the module actually sits on, rather than clearing the tenant or
    /// the whole installation.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy controller reached for coarse clears - portal-wide and host-wide - because it had no
    /// cheap way to know which cache entries a change had invalidated. The consequence was that one
    /// module's import evicted every cached artefact belonging to the tenant, and sometimes to every tenant.
    /// <para>
    /// Explicit, targeted invalidation replaces that: the module's placements are read and each one's page
    /// is discarded. The negative assertions are the substance of the fact - a reintroduced coarse clear
    /// would still leave the cache correct, so only the absence of the broad calls can detect it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportModule_DiscardsOnlyThePagesTheModuleSitsOn()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = Harness.Document("one") },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.DiscardedTabIds.Should().Equal(TabId, SecondTabId);

        harness.Cache.Verify(cache => cache.InvalidatePortal(It.IsAny<int>()), Times.Never);
        harness.Cache.Verify(cache => cache.InvalidateHost(), Times.Never);
        harness.Cache.Verify(cache => cache.InvalidateTabs(It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// Proves module settings reach the caller as a typed contract with the two scopes kept apart.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy shape was an untyped <c>Hashtable</c> hung off the module object, which merged whatever
    /// was put into it and told a caller nothing about which scope a value came from. The two scopes are
    /// genuinely different: <c>ModuleSettings</c> is keyed by module and applies wherever the module
    /// appears, while <c>TabModuleSettings</c> is keyed by placement and applies on one page only. The DDL
    /// chain alters those two tables 13 and 8 times respectively, so they are real tables and not an
    /// implementation detail.
    /// <para>
    /// The contract therefore carries both scopes separately and names the placement the second scope
    /// belongs to. A setting present in one scope must not appear in the other, which is what the negative
    /// assertions establish.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetModuleSettings_ProjectsBothScopesOntoATypedContract()
    {
        Harness harness = Harness.Ready();
        harness.ModuleScopeSettings =
        [
            new ModuleSetting { ModuleId = ModuleId, SettingName = "editor", SettingValue = "plain" },
        ];
        harness.PlacementScopeSettings =
        [
            new TabModuleSetting
            {
                TabModuleId = TabModuleId,
                SettingName = "showBorder",
                SettingValue = "false",
            },
        ];

        Result<ModuleSettingsDto?> outcome = await harness.Service.GetModuleSettingsAsync(
            PortalId,
            ModuleId,
            TabModuleId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        ModuleSettingsDto settings = outcome.Value!;
        settings.ModuleId.Should().Be(ModuleId);
        settings.TabModuleId.Should().Be(TabModuleId);

        settings.ModuleSettings.Should().ContainKey("editor").WhoseValue.Should().Be("plain");
        settings.ModuleSettings.Should().NotContainKey("showBorder");

        settings.TabModuleSettings.Should().ContainKey("showBorder").WhoseValue.Should().Be("false");
        settings.TabModuleSettings.Should().NotContainKey("editor");
    }

    /// <summary>
    /// Proves the two settings scopes are separate types rather than one bag with a discriminator.
    /// </summary>
    /// <remarks>
    /// This is the structural counterpart to the projection fact above. Each scope is keyed by a different
    /// column - one by module, one by placement - so collapsing them into a single entity would require a
    /// nullable key and a convention about which one is in force. Keeping them apart makes the distinction
    /// a property of the type system instead of a rule someone has to remember.
    /// </remarks>
    [Fact]
    public void SettingsEntities_KeepTheModuleAndPlacementScopesSeparatelyTyped()
    {
        typeof(ModuleSetting).Should().NotBe(typeof(TabModuleSetting));

        typeof(ModuleSetting).GetProperty("ModuleId").Should().NotBeNull(
            "the module scope is keyed by module");
        typeof(ModuleSetting).GetProperty("TabModuleId").Should().BeNull(
            "the module scope must not carry a placement key");

        typeof(TabModuleSetting).GetProperty("TabModuleId").Should().NotBeNull(
            "the placement scope is keyed by placement");
        typeof(TabModuleSetting).GetProperty("ModuleId").Should().BeNull(
            "the placement scope must not carry a module key");
    }

    /// <summary>
    /// Proves the flattened legacy module class is split into four entities along the real table
    /// boundaries.
    /// </summary>
    /// <remarks>
    /// <c>ModuleInfo.vb</c> declared 58 properties on one class, which was in truth a join across
    /// <c>Modules</c>, <c>TabModules</c>, <c>ModuleDefinitions</c> and <c>ModuleControls</c> - so a caller
    /// holding one instance could not tell which table any given value came from, and writing one back
    /// meant writing to four. The split restores the boundaries.
    /// <para>
    /// Each assertion below names a property that belongs to exactly one of the four tables and confirms it
    /// appears on that entity and nowhere else. Together they make a reintroduced flattened composite a
    /// failure rather than a design opinion. <c>ModuleControl</c> is asserted to exist as its own type for
    /// the same reason, even though this service does not read it: the fourth boundary is part of the
    /// property.
    /// </para>
    /// <para>
    /// <c>ModuleInfo.vb</c> L36 also declared <c>Implements IPropertyAccess</c>, which is dropped along
    /// with the token-replacement subsystem that consumed it, so no equivalent is looked for here.
    /// </para>
    /// </remarks>
    [Fact]
    public void ModuleAggregate_IsSplitAcrossFourEntitiesAlongTheRealTableBoundaries()
    {
        Type[] boundaries =
        [
            typeof(Module),
            typeof(TabModule),
            typeof(ModuleDefinition),
            typeof(ModuleControl),
        ];

        boundaries.Should().OnlyHaveUniqueItems();

        typeof(Module).GetProperty("ModuleId").Should().NotBeNull();
        typeof(Module).GetProperty("PaneName").Should().BeNull(
            "the pane belongs to the placement table, not the module table");
        typeof(Module).GetProperty("ModuleOrder").Should().BeNull(
            "the ordering belongs to the placement table");
        typeof(Module).GetProperty("DesktopModuleId").Should().BeNull(
            "the package key belongs to the definition table");
        typeof(Module).GetProperty("ControlSrc").Should().BeNull(
            "the control source belongs to the control table");

        typeof(TabModule).GetProperty("PaneName").Should().NotBeNull();
        typeof(TabModule).GetProperty("TabId").Should().NotBeNull();

        typeof(ModuleDefinition).GetProperty("DesktopModuleId").Should().NotBeNull();
        typeof(ModuleDefinition).GetProperty("TabId").Should().BeNull(
            "a definition is not placed on a page");
    }

    /// <summary>
    /// Proves a listing reports its rows and the grand total together, in one value.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy listing methods returned an untyped <c>ArrayList</c> and reported the total separately by
    /// writing back through a <c>ByRef totalRecords</c> argument - three such signatures in the user
    /// controller alone, at L685, L725 and L746. A caller could therefore hold rows without a total, or a
    /// total that no longer described the rows.
    /// <para>
    /// <see cref="PagedResult{T}"/> carries both, so they cannot disagree. The rows are DTOs rather than
    /// entities, which is the boundary that keeps a mapped entity from being serialised onto the wire.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListModules_ReportsItsRowsAndTheGrandTotalInOneValue()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10 },
            tabId: null,
            includeDeleted: false,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        PagedResult<ModuleListItemDto> page = outcome.Value;
        page.Items.Should().HaveCount(2, "the module sits on two pages, so it contributes two rows");
        page.PageIndex.Should().Be(0);
        page.IsUnpaged.Should().BeFalse();

        page.TotalCount.Should().BeGreaterThanOrEqualTo(
            page.Items.Count,
            "a grand total that understated the page it describes would be self-contradictory");

        page.Items.Should().OnlyContain(row => row.ModuleId == ModuleId);
        page.Items.Select(row => row.TabId).Should().Equal(TabId, SecondTabId);
        page.Items.Should().OnlyContain(row => row.FriendlyName == FriendlyName);
    }

    /// <summary>
    /// A module placed on more pages than the window is wide is cut BY THE WINDOW, and the metadata stays
    /// exact.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This fact exists because the code it covers was wrong, and it is written to fail again if either
    /// generation of the fault returns.
    /// <para>
    /// The FIRST fault was a raise. The window was taken over MODULES while the response carried PLACEMENT
    /// rows, and the service's remarks reconciled the two by observing that a module has at most one
    /// placement on any one page - true, but only while a page is NAMED. With <c>tabId</c> null, the default
    /// listing of a portal's modules, a module contributes every placement it has, so the rows outnumbered
    /// the modules behind them. The envelope forbids a total smaller than the page it describes and a page
    /// carrying more records than its declared size, so both guards refused and
    /// <see cref="ArgumentOutOfRangeException"/> surfaced as a server error on a listing whose only unusual
    /// feature was a module placed on two pages - ordinary, and universal for a module marked to appear on
    /// every page.
    /// </para>
    /// <para>
    /// The SECOND fault was the fix for the first: both figures were raised with <c>Math.Max</c> to whatever
    /// the row count happened to be. That silenced the guards and left the contract broken in a quieter way.
    /// A caller asking for one row received two; the declared width was a number it never sent; the total was
    /// a lower bound rather than a count; and <c>totalPages</c>, computed from both, changed according to
    /// which page was asked for, so a pager could not enumerate the collection. An earlier revision of THIS
    /// FACT asserted that behaviour with <c>BeGreaterThanOrEqualTo</c> - a shape loose enough to pass against
    /// the fabricated figures - which is why it is now asserted by equality.
    /// </para>
    /// <para>
    /// The window is cut over the rows, so one row is what a width of one returns and the module's second
    /// placement is simply the first row of the next window. Neither fault is a legacy behaviour to preserve:
    /// the legacy listing had no paging envelope at all and so had nothing to reconcile. Both are therefore
    /// fixed rather than annotated, which is why no <c>// MIGRATION:</c> marker accompanies this.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListModules_WhenAModuleOutnumbersItsWindow_CutsTheRowsRatherThanWideningTheWindow()
    {
        Harness harness = Harness.Ready();

        Func<Task<Result<PagedResult<ModuleListItemDto>>>> listing = () => harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 0, PageSize = 1 },
            tabId: null,
            includeDeleted: false,
            CancellationToken.None);

        await listing.Should().NotThrowAsync();

        Result<PagedResult<ModuleListItemDto>> outcome = await listing();
        outcome.IsSuccess.Should().BeTrue();

        PagedResult<ModuleListItemDto> page = outcome.Value;
            page.Items.Should().HaveCount(1, "the window is one row wide and the rows are placements");
            page.PageSize.Should().Be(1, "the declared width is the width the caller asked for");
            page.PageIndex.Should().Be(0, "the coordinates address rows, so the first window is index zero");
            page.TotalCount.Should().Be(2, "the module sits on two pages, so the collection holds two rows");
            page.TotalPages.Should().Be(2, "two rows in windows of one is two windows");

            Result<PagedResult<ModuleListItemDto>> next = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 1, PageSize = 1 },
            tabId: null,
            includeDeleted: false,
            CancellationToken.None);

        next.Value.Items.Should().ContainSingle().Which.TabId.Should().Be(
            SecondTabId,
            "the placement the first window could not carry is the first row of the second");
    }

    /// <summary>
    /// Proves the declared window is left exactly as the caller asked for it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The companion of the fact above, on the shape where the rows fit. It is what stops a fix from becoming
    /// a licence to return whatever geometry is convenient: with the module restricted to a single page there
    /// is one row, and the envelope reports the requested size untouched.
    /// </remarks>
    [Fact]
    public async Task ListModules_WhenTheRowsFitTheWindow_ReportsTheRequestedGeometryUnchanged()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10 },
            tabId: TabId,
            includeDeleted: false,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        PagedResult<ModuleListItemDto> page = outcome.Value;
        ModuleListItemDto only = Assert.Single(page.Items);
        only.TabId.Should().Be(TabId);

        page.PageSize.Should().Be(10, "the requested window fit, so it must be reported verbatim");
        page.PageIndex.Should().Be(0);
        page.TotalCount.Should().Be(1);
    }

    /// <summary>
    /// Proves an unpaged request is reported as unpaged rather than as a single very large page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy listings had no notion of paging at all - they returned everything - so the migrated
    /// contract has to be able to express "everything" without lying about page geometry. A page size of
    /// zero means unpaged, and the envelope reports it as such instead of inventing a page index and a page
    /// count that describe nothing.
    /// </remarks>
    [Fact]
    public async Task ListModules_WithNoPageSize_ReportsAnUnpagedAnswer()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 0, PageSize = 0 },
            tabId: null,
            includeDeleted: false,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        PagedResult<ModuleListItemDto> page = outcome.Value;
        page.IsUnpaged.Should().BeTrue();
        page.PageSize.Should().Be(0);
        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(2);
    }

    /// <summary>
    /// The mocking surface for this suite: a <see cref="ModuleService"/> built entirely from stand-ins for
    /// Domain and Application abstractions, with every collaborator's answer settable per test.
    /// </summary>
    /// <remarks>
    /// The legacy equivalent of this arrangement did not exist and could not have. Data access went through
    /// <c>DataProvider.Instance()</c>, a reflection-resolved singleton over an abstract class carrying 269
    /// <c>MustOverride</c> members, and the module controller's own members were <c>Shared</c>; there was no
    /// seam at which anything could be substituted, which is why the legacy tree contains no automated
    /// tests at all. Constructor injection over narrow, aggregate-shaped interfaces is what creates the
    /// seam, and this class is the proof that the seam is usable.
    /// <para>
    /// Nothing here touches a database, a DbContext, an Infrastructure type or an HTTP context.
    /// </para>
    /// <para>
    /// <see cref="IClock"/> is deliberately absent, and the absence is recorded rather than worked around.
    /// <see cref="ModuleService"/> does not take one because it reads no current time: schedule bounds
    /// arrive on the request and are compared against each other, and the only timestamps in play belong to
    /// the audit sink, which is itself a stand-in here. Injecting a clock the service does not accept would
    /// mean asserting against a collaborator that does not exist. The rule the clock exists to serve is
    /// honoured instead by construction - every date in this suite is a fixed literal and nothing reads the
    /// wall clock - so no assertion in this file can drift with the time of day. Should a future change give
    /// this service a time-dependent decision, <see cref="IClock"/> is the seam it must take, and this note
    /// is the reason a reader will not find one already mocked.
    /// </para>
    /// </remarks>
    private sealed class Harness
    {
        private Harness()
        {
            this.Caching = new CachingOptions();

            this.Module = new Module
            {
                ModuleId = ModuleId,
                PortalId = PortalId,
                ModuleDefinitionId = ModuleDefinitionId,
                ModuleTitle = "Welcome",
                IsDeleted = false,
            };

            this.Placements =
            [
                NewPlacement(TabModuleId, TabId, order: 1),
                NewPlacement(SecondTabModuleId, SecondTabId, order: 1),
            ];

            this.StoredModule = this.Module;

            this.Definition = new ModuleDefinition
            {
                ModuleDefinitionId = ModuleDefinitionId,
                DesktopModuleId = DesktopModuleId,
                FriendlyName = FriendlyName,
                DefaultCacheTime = 0,
            };

            this.Package = new DesktopModule
            {
                DesktopModuleId = DesktopModuleId,
                FriendlyName = FriendlyName,
                ModuleName = PackageName,
                FolderName = "HTML",
                Version = PackageVersion,
                BusinessControllerClass = BusinessController,
                SupportedFeatures = PortableCapability,
            };

            this.ModuleScopeSettings = [];
            this.PlacementScopeSettings = [];
            this.AuditTrail = [];
            this.DiscardedTabIds = [];

            this.ExportOutcome = Result<string?>.Success("<item>one</item>");
            this.ImportOutcome = Result.Success();
            this.PermissionOutcome = Result<bool>.Success(true);
            this.EditGranted = true;
            this.CallerId = CallerUserId;

            this.Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            this.Definitions = new Mock<IModuleDefinitionRepository>(MockBehavior.Loose);
            this.Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            this.Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            this.UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            this.Cache = new Mock<ICacheService>(MockBehavior.Loose);
            this.CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            this.Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            this.BusinessControllers = new Mock<IModuleBusinessControllerFactory>(MockBehavior.Loose);
            this.Audit = new Mock<IAuditSink>(MockBehavior.Loose);
        }

        /// <summary>Gets the module row the repository returns, mutable so a test can reshape it.</summary>
        public Module Module { get; }

        /// <summary>Gets or sets what a module lookup resolves to; null makes every module unknown.</summary>
        public Module? StoredModule { get; set; }

        /// <summary>Gets the two placements the module sits on.</summary>
        public List<TabModule> Placements { get; }

        /// <summary>Gets the definition the module instantiates.</summary>
        public ModuleDefinition Definition { get; }

        /// <summary>Gets the package declaring the definition, whose capability field drives the guards.</summary>
        public DesktopModule Package { get; }

        /// <summary>Gets or sets the module-scoped settings the repository returns.</summary>
        public List<ModuleSetting> ModuleScopeSettings { get; set; }

        /// <summary>Gets or sets the placement-scoped settings the repository returns.</summary>
        public List<TabModuleSetting> PlacementScopeSettings { get; set; }

        /// <summary>Gets or sets what the module's controller reports when asked to export.</summary>
        public Result<string?> ExportOutcome { get; set; }

        /// <summary>Gets or sets what the module's controller reports when handed content to import.</summary>
        public Result ImportOutcome { get; set; }

        /// <summary>Gets or sets the raw outcome of the permission evaluation, failures included.</summary>
        public Result<bool> PermissionOutcome { get; set; }

        /// <summary>Gets or sets a value indicating whether the caller holds the edit grant.</summary>
        public bool EditGranted { get; set; }

        /// <summary>Gets or sets the caller's identifier; null models an unattributed caller.</summary>
        public int? CallerId { get; set; }

        /// <summary>Gets the caching options, mutable so the multiplier can be varied per test.</summary>
        public CachingOptions Caching { get; }

        /// <summary>Gets the audit entries the service emitted.</summary>
        public List<AuditEvent> AuditTrail { get; }

        /// <summary>Gets the page identifiers the service discarded from the cache, in order.</summary>
        public List<int> DiscardedTabIds { get; }

        /// <summary>Gets the cache key the service used, or null when the cache was never consulted.</summary>
        public string? CacheKey { get; private set; }

        /// <summary>Gets the cache lifetime the service asked for.</summary>
        public TimeSpan CacheExpiration { get; private set; }

        /// <summary>Gets the content the module's controller actually received on import.</summary>
        public string? ImportedContent { get; private set; }

        /// <summary>Gets the version the module's controller actually received on import.</summary>
        public string? ImportedVersion { get; private set; }

        /// <summary>Gets the acting user the module's controller was told about on import.</summary>
        public int? ImportedByUserId { get; private set; }

        /// <summary>Gets the module repository stand-in.</summary>
        public Mock<IModuleRepository> Modules { get; }

        /// <summary>Gets the definition repository stand-in.</summary>
        public Mock<IModuleDefinitionRepository> Definitions { get; }

        /// <summary>Gets the page repository stand-in.</summary>
        public Mock<ITabRepository> Tabs { get; }

        /// <summary>Gets the portal repository stand-in.</summary>
        public Mock<IPortalRepository> Portals { get; }

        /// <summary>Gets the unit-of-work stand-in, which records every commit.</summary>
        public Mock<IUnitOfWork> UnitOfWork { get; }

        /// <summary>Gets the cache stand-in, which records keys, lifetimes and invalidations.</summary>
        public Mock<ICacheService> Cache { get; }

        /// <summary>Gets the caller stand-in.</summary>
        public Mock<ICurrentUser> CurrentUser { get; }

        /// <summary>Gets the permission service stand-in.</summary>
        public Mock<IPermissionService> Permissions { get; }

        /// <summary>Gets the business-controller factory stand-in, which replaces the five reflection sites.</summary>
        public Mock<IModuleBusinessControllerFactory> BusinessControllers { get; }

        /// <summary>Gets the audit sink stand-in.</summary>
        public Mock<IAuditSink> Audit { get; }

        /// <summary>Gets the service under test, built from the stand-ins above.</summary>
        public ModuleService Service { get; private set; } = null!;

        /// <summary>
        /// Builds a harness whose collaborators all answer the way a healthy, fully installed portal would.
        /// </summary>
        /// <returns>A harness ready for a test to alter one thing and observe the consequence.</returns>
        /// <remarks>
        /// Every stand-in is wired from the harness's own mutable state rather than from a captured value,
        /// so a test changes state before calling the service and the change is honoured. That is what lets
        /// the two halves of the legacy export guard be varied one at a time.
        /// </remarks>
        public static Harness Ready()
        {
            var harness = new Harness();

            harness.Portals
                .Setup(repository => repository.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            harness.Modules
                .Setup(repository => repository.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.StoredModule);

            harness.Modules
                .Setup(repository => repository.GetByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.StoredModule is null
                    ? []
                    : new List<Module> { harness.StoredModule });

            harness.Modules
                .Setup(repository => repository.GetTabModulesByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Placements);

            // The set-based placement read, served from the same placement world as the single-module read
            // above so the harness cannot describe two different realities.
            harness.Modules
                .Setup(repository => repository.GetTabModulesByModuleIdsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<int> moduleIds, CancellationToken _) =>
                    harness.Placements
                        .Where(placement => moduleIds.Contains(placement.ModuleId))
                        .ToList());

            // THE LISTING'S PAGE COMES FROM THE STORE, filtered, ordered, counted and windowed there. The
            // fake reproduces the contract's documented semantics over the same one-module, many-placement
            // world every other stub serves, so the paging facts below still describe what the listing
            // publishes rather than what a canned answer contains: the row set is the module's placements,
            // the total is how many there are, and the window is the coordinates asked for.
            harness.Modules
                .Setup(repository => repository.ListPlacementsAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    int _,
                    int? tabId,
                    bool includeDeleted,
                    string? _,
                    string? _,
                    bool _,
                    int pageIndex,
                    int pageSize,
                    CancellationToken _) =>
                {
                    Module? module = harness.StoredModule;

                    List<TabModule> rows = module is null || (!includeDeleted && module.IsDeleted)
                        ? []
                        : harness.Placements
                            .Where(placement => placement.ModuleId == module.ModuleId
                                && (tabId is null || placement.TabId == tabId.Value))
                            .OrderBy(placement => placement.TabId)
                            .ThenBy(placement => placement.ModuleOrder)
                            .ThenBy(placement => placement.TabModuleId)
                            .Select(placement =>
                            {
                                placement.Module = module;
                                return placement;
                            })
                            .ToList();

                    if (pageSize == 0)
                    {
                        return PagedResult<TabModule>.Unpaged(rows);
                    }

                    List<TabModule> window = rows
                        .Skip(Paging.SkipCount(pageIndex, pageSize))
                        .Take(pageSize)
                        .ToList();

                    return PagedResult<TabModule>.Create(window, rows.Count, pageIndex, pageSize);
                });

            harness.Modules
                .Setup(repository => repository.GetTabModuleByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabModuleId, CancellationToken _) =>
                    harness.Placements.Find(placement => placement.TabModuleId == tabModuleId));

            harness.Modules
                .Setup(repository => repository.GetModuleSettingsAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ModuleScopeSettings);

            harness.Modules
                .Setup(repository => repository.GetTabModuleSettingsAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PlacementScopeSettings);

            harness.Definitions
                .Setup(repository => repository.GetModuleDefinitionByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Definition);

            harness.Definitions
                .Setup(repository => repository.GetModuleDefinitionsByPortalIdAsync(
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new List<ModuleDefinition> { harness.Definition });
            harness.Definitions
                .Setup(repository => repository.GetAdministrativeDefinitionByFriendlyNameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, string friendlyName, CancellationToken _) =>
                    string.Equals(
                        harness.Definition.FriendlyName,
                        friendlyName,
                        StringComparison.OrdinalIgnoreCase)
                        ? harness.Definition
                        : null);

            harness.Definitions
                .Setup(repository => repository.GetDesktopModuleByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Package);

            harness.Tabs
                .Setup(repository => repository.GetTabModulesAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, CancellationToken _) =>
                    harness.Placements.FindAll(placement => placement.TabId == tabId));

            harness.UnitOfWork
                .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(() => harness.CallerId);
            harness.CurrentUser.SetupGet(caller => caller.UserName).Returns("caller@example.test");
            harness.CurrentUser
                .SetupGet(caller => caller.IsAuthenticated)
                .Returns(() => harness.CallerId is not null);

            harness.Permissions
                .Setup(service => service.HasModulePermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PermissionOutcome.IsFailure
                    ? harness.PermissionOutcome
                    : Result<bool>.Success(harness.EditGranted));

            harness.BusinessControllers
                .Setup(factory => factory.ExportModuleContentAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ExportOutcome);

            harness.BusinessControllers
                .Setup(factory => factory.ImportModuleContentAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string?, int, string?, string?, int, CancellationToken>(
                    (_, _, content, version, userId, _) =>
                    {
                        harness.ImportedContent = content;
                        harness.ImportedVersion = version;
                        harness.ImportedByUserId = userId;
                    })
                .ReturnsAsync(() => harness.ImportOutcome);

            harness.Cache
                .Setup(cache => cache.InvalidateModules(It.IsAny<int>()))
                .Callback<int>(harness.DiscardedTabIds.Add);

            // The cache stand-in runs the factory it is handed rather than answering from a store, so a
            // cached read and an uncached read exercise the same production code path. What the test
            // observes is the key and the lifetime the service asked for.
            harness.Cache
                .Setup(cache => cache.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<ModuleDefinitionDto>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string key,
                    Func<CancellationToken, Task<IReadOnlyList<ModuleDefinitionDto>>> factory,
                    TimeSpan expiration,
                    CancellationToken token) =>
                {
                    harness.CacheKey = key;
                    harness.CacheExpiration = expiration;
                    return factory(token);
                });

            harness.Audit
                .Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(harness.AuditTrail.Add);

            harness.Service = new ModuleService(
                harness.Modules.Object,
                harness.Definitions.Object,
                harness.Tabs.Object,
                harness.Portals.Object,
                harness.UnitOfWork.Object,
                harness.Cache.Object,
                harness.CurrentUser.Object,
                harness.Permissions.Object,
                harness.BusinessControllers.Object,
                harness.Audit.Object,
                harness.Caching);

            return harness;
        }

        /// <summary>
        /// Builds an import document in the shape the exporter writes, carrying the supplied text as its
        /// escaped payload.
        /// </summary>
        /// <param name="payload">The text a module would have handed over on export.</param>
        /// <returns>A well-formed document the import path accepts.</returns>
        /// <remarks>
        /// The element and attribute names match <c>ModuleController.vb</c> L236-L242, so a document built
        /// here is the same shape as one a DotNetNuke 4.x installation produced.
        /// </remarks>
        public static string Document(string payload) =>
            $"<content type=\"{SanitisedPackageName}\" version=\"{PackageVersion}\">{payload}</content>";

        private static TabModule NewPlacement(int tabModuleId, int tabId, int order) => new()
        {
            TabModuleId = tabModuleId,
            TabId = tabId,
            ModuleId = ModuleId,
            PaneName = PaneName,
            ModuleOrder = order,
            CacheTime = 0,
            Visibility = ModuleVisibility.Maximized,
        };
    }
}
