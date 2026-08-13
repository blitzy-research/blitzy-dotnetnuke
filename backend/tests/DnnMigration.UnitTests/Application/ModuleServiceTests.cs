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
/// Asserts that <see cref="ModuleService"/> preserves the behaviour of the legacy VB.NET module controller,
/// and that each place where it deliberately departs from that behaviour departs in the documented
/// direction.
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

    /// <summary>The audit event name a module EXPORT is recorded under.</summary>
    private const string ModuleExportedEventName = "MODULE_EXPORTED";

    /// <summary>
    /// The cache key the definition catalogue is stored under. The legacy name is preserved verbatim so
    /// that cache behaviour remains auditable against the original.
    /// </summary>
    private const string DefinitionCatalogueCacheKeyFormat = "ModuleDefinitions{0}";

    /// <summary>
    /// The per-entity cache lifetime in minutes. <c>DataCache.ModuleCacheTimeOut</c> and
    /// <c>DataCache.TabModuleCacheTimeOut</c> are both declared <c>= 20</c>.
    /// </summary>
    private const int LegacyCacheTimeOutMinutes = 20;

    /// <summary>
    /// The multiplier a legacy installation applied when no host setting had been written.
    /// <c>Common.Globals.PerformanceSetting</c> substitutes 3 for a missing setting.
    /// </summary>
    private const int MeasuredLegacyPerformanceMultiplier = 3;

    /// <summary>
    /// The acting user recorded when the caller carries no identity. This is the legacy
    /// <c>Null.NullInteger</c> value, kept rather than replaced so an unattributed write is recognisable in
    /// the same way it was before.
    /// </summary>
    private const int UnattributedUserId = -1;

    /// <summary>
    /// The value <c>DesktopModules.SupportedFeatures</c> holds while a package's capabilities have not been
    /// determined. <c>ModuleController.vb</c> L422 compared the field against <c>Null.NullInteger</c> and,
    /// on a match, parked the payload on the event queue.
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
    /// A silently incomplete export is data loss disguised as success, so this is treated as a legacy
    /// defect that must be surfaced rather than as a business rule to preserve. The divergence is recorded
    /// in MIGRATION_NOTES.md.
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
    [Fact]
    public async Task ExportModule_WithUnrepresentableContent_FailsWithoutQuotingThePayload()
    {
        // A C0 control character, which no XML escape can represent, followed by a marker that stands in
        // for whatever portal data a real module's content might carry. The marker is obviously synthetic
        // on purpose: the point of the fact is that it must not reappear in the failure message.
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
    /// MIGRATION: the legacy export had no ceiling; it encoded whatever the module returned and wrote it to
    /// disk, bounded only by a disk-space check that the excluded file subsystem performed. The divergence
    /// is recorded in MIGRATION_NOTES.md.
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

    /// <summary>Proves the export path writes nothing, so no commit is attempted on any of its outcomes.</summary>
    /// <returns>A task representing the assertion.</returns>
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
    /// The concern the old title raised is still honoured and is worth restating: nothing here touches an
    /// ambient request context. Only 8 of the 84 in-scope legacy files referenced <c>System.Web</c> at all,
    /// and the one place the target injects an HTTP accessor is a single piece of API middleware.
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
    /// The two are unified onto the caller, which is the only identity that is true in both cases. That is
    /// a deliberate divergence from L433 and is recorded in MIGRATION_NOTES.md.
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
    /// Zero would have been the wrong fallback. <c>Users.UserID</c> is an identity column and the legacy
    /// schema seeds identities low, so a zero fallback risks attributing an anonymous write to a real
    /// account - which is precisely the class of collision the sentinel analysis exists to prevent.
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
    /// Nothing is lost, because the condition the branch waited for cannot arise. Capabilities come from a
    /// closed registration map fixed at start-up, so a capability is either registered or it is not and
    /// waiting changes nothing.
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
    /// The three assertions are one statement in three parts: the caller is told, nothing was written, and
    /// no audit entry claims otherwise. The last matters most - an audit trail that records a success that
    /// did not happen is worse than no trail at all - and it is why the service emits the entry after the
    /// commit rather than before the attempt.
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

    /// <summary>Proves a successful import commits exactly once, through the unit of work.</summary>
    /// <returns>A task representing the assertion.</returns>
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

    /// <summary>Proves that every refusal the import path can produce leaves the database untouched.</summary>
    /// <param name="scenario">Which refusal to provoke.</param>
    /// <param name="expectedCode">The failure code that refusal must report.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A cancellation token is passed explicitly on every call, so a guard that returned before the token
    /// reached the repository would still be exercised with one.
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

    /// <summary>Proves a failed permission evaluation is treated as a refusal rather than as a grant.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The evaluation returns a <see cref="Result{T}"/>, so it has three outcomes and not two: granted,
    /// denied, and could-not-be-determined. Reading only the value would make the third case
    /// indistinguishable from the second - or, worse, from the first if the default of <see cref="bool"/>
    /// were consulted after a failure.
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

    /// <summary>Proves module zero is treated as a real module, because the identity column seeds at zero.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <c>Modules.ModuleID</c> is declared <c>IDENTITY(0,1)</c>, so the first module ever created in a
    /// DotNetNuke database has the identifier zero. Any code that treats zero as "unset" - the reflex in
    /// most schemas - silently refuses a legitimate row.
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
    /// rather than theoretical - the seeded baseline portal in this project's own database has <c>PortalID
    /// = -1</c>.
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

    /// <summary>Proves the configured multiplier defaults to the value a legacy installation actually used.</summary>
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

    /// <summary>Proves the catalogue is cached under the legacy key name, keyed by tenant.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The tenant is part of the key, which is the multi-tenant safety property: without it one portal's
    /// definition catalogue would be served to another.
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

    /// <summary>Records that disabling caching disables caching only, and does not also suppress the read.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: several legacy callers treated a zero lifetime as an instruction to skip the database
    /// read outright, on the grounds that the query was too expensive to repeat per request - so a
    /// configuration value silently changed what an endpoint reported, not merely how fast it reported it.
    /// That is not reproduced.
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

    /// <summary>Proves module settings reach the caller as a typed contract with the two scopes kept apart.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The contract therefore carries both scopes separately and names the placement the second scope
    /// belongs to. A setting present in one scope must not appear in the other, which is what the negative
    /// assertions establish.
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

    /// <summary>Proves the two settings scopes are separate types rather than one bag with a discriminator.</summary>
    /// <remarks>
    /// This is the structural counterpart to the projection fact above. Each scope is keyed by a different
    /// column - one by module, one by placement - so collapsing them into a single entity would require a
    /// nullable key and a convention about which one is in force.
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
    /// <c>ModuleInfo.vb</c> L36 also declared <c>Implements IPropertyAccess</c>, which is dropped along
    /// with the token-replacement subsystem that consumed it, so no equivalent is looked for here.
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

    /// <summary>Proves a listing reports its rows and the grand total together, in one value.</summary>
    /// <returns>A task representing the assertion.</returns>
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
    /// The FIRST fault was a raise.
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

    /// <summary>Proves the declared window is left exactly as the caller asked for it.</summary>
    /// <returns>A task representing the assertion.</returns>
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

    /// <summary>Proves an unpaged request is reported as unpaged rather than as a single very large page.</summary>
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
    /// <see cref="IClock"/> is deliberately absent, and the absence is recorded rather than worked around.
    /// <see cref="ModuleService"/> does not take one because it reads no current time: schedule bounds
    /// arrive on the request and are compared against each other, and the only timestamps in play belong to
    /// the audit sink, which is itself a stand-in here.
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

        /// <summary>
        /// Gets the business-controller factory stand-in, which replaces the five reflection sites.
        /// </summary>
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
