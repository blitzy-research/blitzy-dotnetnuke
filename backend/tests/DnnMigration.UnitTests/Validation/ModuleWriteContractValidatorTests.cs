using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Covers the two rules the module write contracts gained after runtime testing found them missing: the
/// icon reference must stay inside the portal's own folder, and the relocation destination carries no
/// numeric rule at all.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ WHY THE CONTAINMENT RULE IS ASSERTED ON BOTH CONTRACTS AND NOT JUST ONE. It was absent from both,
/// and the consequence was measured rather than inferred: <c>POST /api/v1/modules</c> with an
/// <c>iconFile</c> of <c>../../../etc/passwd</c> answered HTTP 201 and stored the value verbatim, and
/// <c>PUT</c> answered 200 and stored it verbatim for <c>../../bad</c>, <c>../../../etc/passwd</c>,
/// <c>..\\..\\bad</c> and <c>/etc/passwd</c> - while the role and page contracts refused every one of
/// them through this same shared rule. <c>CreateRoleRequestValidator</c> records why the rule was hoisted
/// into a shared helper in the first place: while it was private to one validator, the paths that lacked
/// it "accepted references this one refused, and the weaker path defined the application's actual
/// behaviour". The module paths were the remaining weaker paths, so both are pinned here.
/// </para>
/// <para>
/// MIGRATION: adding the rule PRESERVES the legacy outcome rather than narrowing it. The legacy screen did
/// not validate this field because it could not receive an arbitrary value:
/// <c>Website/admin/Modules/modulesettings.ascx</c> line 116 declares it as
/// <c>&lt;portal:url id="ctlIcon" showurls="False" showtabs="False" ...&gt;</c>, a picker over the
/// portal's own files, and the code-behind stored whatever the picker yielded at line 348. A rooted or
/// upward-traversing path was unreachable by construction. This screen offers a text box instead, so the
/// constraint the picker enforced structurally is enforced by a rule - which refuses only values the
/// legacy could never have produced.
/// </para>
/// </remarks>
public sealed class ModuleWriteContractValidatorTests
{
    /// <summary>The create contract's validator under test.</summary>
    private readonly CreateModuleRequestValidator _create = new();

    /// <summary>The update contract's validator under test.</summary>
    private readonly UpdateModuleRequestValidator _update = new();

    /// <summary>
    /// Every reference that leaves the portal's own folder is refused on create and on update.
    /// </summary>
    /// <param name="iconFile">The reference under test.</param>
    /// <remarks>
    /// The four values measured as accepted-and-stored before the rule existed lead the list, followed by
    /// the remaining shapes the shared rule names: a parent segment in any position rather than only at
    /// the start, a leading separator of either kind, and a colon anywhere - which covers both a
    /// drive-qualified path and a URI, so an <c>iconFile</c> can never become an off-origin reference.
    /// </remarks>
    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("../../bad")]
    [InlineData(@"..\..\bad")]
    [InlineData("/etc/passwd")]
    [InlineData("..")]
    [InlineData("images/../../secret.gif")]
    [InlineData("a..b.gif")]
    [InlineData(@"\\server\share\icon.gif")]
    [InlineData(@"C:\windows\icon.gif")]
    [InlineData("https://elsewhere.test/icon.gif")]
    [InlineData("javascript:alert(1)")]
    public void AnEscapingIconReference_IsRefusedOnBothContracts(string iconFile)
    {
        ShouldRefuseIcon(_create.Validate(CreateWith(iconFile)));
        ShouldRefuseIcon(_update.Validate(UpdateWith(iconFile)));
    }

    /// <summary>
    /// A contained reference, and an absent one, are accepted on create and on update.
    /// </summary>
    /// <param name="iconFile">The reference under test.</param>
    /// <remarks>
    /// The empty value is the important row. It is the normal case - the legacy screens stored the empty
    /// string when no icon had been picked, and the preserved sentinel convention makes the empty string
    /// the representation of an absent one - so a rule that refused it would make removing an icon
    /// impossible. A nested relative path is accepted too, because the picker could produce one.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("icon.gif")]
    [InlineData("images/icon.gif")]
    [InlineData("sub/dir/valid.gif")]
    [InlineData("icon with spaces.gif")]
    public void AContainedIconReference_IsAcceptedOnBothContracts(string iconFile)
    {
        _create.Validate(CreateWith(iconFile)).IsValid.Should().BeTrue();
        _update.Validate(UpdateWith(iconFile)).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// An omitted icon reference is accepted, so the rule never makes the member mandatory.
    /// </summary>
    [Fact]
    public void AnOmittedIconReference_IsAccepted()
    {
        _create.Validate(CreateWith(iconFile: null)).IsValid.Should().BeTrue();
        _update.Validate(UpdateWith(iconFile: null)).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// The relocation destination carries no numeric rule, so every page identifier the schema can
    /// produce is admitted and the service alone decides whether the page is a usable destination.
    /// </summary>
    /// <param name="moveToTabId">The destination under test.</param>
    /// <remarks>
    /// ⚠ ZERO AND MINUS ONE ARE THE ROWS THAT MATTER. <c>dbo.Tabs.TabID</c> is <c>IDENTITY (0, 1)</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 140), so ZERO IS A LEGITIMATE PAGE and a positive-only rule
    /// would make the first page of every portal an illegal destination. Minus one was an "any page"
    /// wildcard in the legacy query surface and must not be refused as an absence marker either. The real
    /// check - that the page exists, belongs to this portal, is not deleted, is not administrative and is
    /// one the caller may edit - is stateful on every clause and therefore the service's to make, which is
    /// the same division already documented for the placement selector beside it.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ARelocationDestination_CarriesNoNumericRule(int moveToTabId)
    {
        UpdateModuleRequest request = UpdateWith(iconFile: null);
        request.MoveToTabId = moveToTabId;

        _update.Validate(request).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// An omitted relocation destination is accepted, which is what keeps an ordinary save unchanged.
    /// </summary>
    /// <remarks>
    /// The member is nullable and absence means "do not move", so every caller written against the
    /// contract before the member existed keeps its exact previous behaviour.
    /// </remarks>
    [Fact]
    public void AnOmittedRelocationDestination_IsAccepted()
    {
        UpdateModuleRequest request = UpdateWith(iconFile: null);

        request.MoveToTabId.Should().BeNull();
        _update.Validate(request).IsValid.Should().BeTrue();
    }

    /// <summary>Asserts a result was refused, and refused for the icon member specifically.</summary>
    /// <param name="result">The validation result to inspect.</param>
    private static void ShouldRefuseIcon(ValidationResult result)
    {
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(failure => failure.PropertyName == nameof(CreateModuleRequest.IconFile));
    }

    /// <summary>Builds an otherwise-valid create request carrying the supplied icon reference.</summary>
    /// <param name="iconFile">The reference to place on the request.</param>
    /// <returns>The request.</returns>
    private static CreateModuleRequest CreateWith(string? iconFile) => new()
    {
        // A definition identity and a page identity, both ordinary. Page zero is used deliberately: it is
        // the first page of every portal and a validator that refused it would fail here rather than in
        // production.
        ModuleDefId = 1,
        TabId = 0,
        ModuleTitle = "Announcements",
        Visibility = ModuleVisibility.Maximized,
        IconFile = iconFile,
    };

    /// <summary>Builds an otherwise-valid update request carrying the supplied icon reference.</summary>
    /// <param name="iconFile">The reference to place on the request.</param>
    /// <returns>The request.</returns>
    private static UpdateModuleRequest UpdateWith(string? iconFile) => new()
    {
        TabId = 0,
        ModuleTitle = "Announcements",
        Visibility = ModuleVisibility.Maximized,
        IconFile = iconFile,
    };
}
