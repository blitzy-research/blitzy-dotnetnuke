using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Pins the field rules on the two module content-movement contracts, and the two properties that make
/// those rules worth having: that a refusal names the member it refused, and that no rule strays into a
/// decision the service owns.
/// </summary>
public sealed class ModuleContentRequestValidatorTests
{
    /// <summary>The bound the legacy export markup declared on its file-name box.</summary>
    private const int FileNameMaximumLength = 200;

    /// <summary>A module identifier that is legitimate and would defeat any sign-based rule.</summary>
    /// <remarks>
    /// <c>Modules.ModuleID</c> is <c>IDENTITY (0, 1)</c>, so zero is the first module an installation ever
    /// creates. It is used as the ordinary value in these facts precisely because it is the value a
    /// careless presence rule would refuse.
    /// </remarks>
    private const int ModuleIdentitySeed = 0;

    /// <summary>
    /// A blank, whitespace-only or absent export file name is refused, and the refusal names the member.
    /// </summary>
    /// <param name="fileName">The submitted name.</param>
    /// <remarks>
    /// The whitespace row is the one that matters most and the reason the rule is a predicate rather than
    /// <c>NotEmpty</c>: FluentValidation treats a run of spaces as a supplied value, while the legacy
    /// sentinel for an absent string was literally the empty string and the service tests
    /// <c>IsNullOrWhiteSpace</c>.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void ExportWithoutAFileName_IsRefusedNamingTheMember(string? fileName)
    {
        ValidationResult result = new ModuleExportRequestValidator()
            .Validate(new ModuleExportRequest { FileName = fileName });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(
                nameof(ModuleExportRequest.FileName),
                "the operation advertises a field-keyed document, which is only producible if the rule "
                + "names the member it refused");

        result.Errors[0].ErrorMessage.Should().Be(
            "A file name is required so the returned document can be labelled.",
            "the service raises this same sentence for this same condition, and one condition enforced at "
            + "two points must read as one rule");
    }

    /// <summary>
    /// An export file name exactly filling the legacy bound is accepted and one character past it is
    /// refused.
    /// </summary>
    /// <remarks>
    /// Asserted from both sides, because a bound asserted only from the refusing side is satisfied by a
    /// rule that is one character too strict.
    /// </remarks>
    [Fact]
    public void ExportFileName_IsBoundedByTheLegacyBoxLength()
    {
        new ModuleExportRequestValidator()
            .Validate(new ModuleExportRequest { FileName = new string('a', FileNameMaximumLength) })
            .IsValid.Should().BeTrue("a name that exactly fills the legacy box was accepted by the legacy");

        ValidationResult tooLong = new ModuleExportRequestValidator()
            .Validate(new ModuleExportRequest { FileName = new string('a', FileNameMaximumLength + 1) });

        tooLong.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(ModuleExportRequest.FileName));
    }

    /// <summary>
    /// The export folder carries no rule of any kind, and an export naming only a file name is valid.
    /// </summary>
    /// <remarks>
    /// The legacy guard was <c>If cboFolders.SelectedIndex &lt;&gt; 0 And txtFile.Text &lt;&gt; ""</c> -
    /// two halves - and only the second survives. The first tested a LIST INDEX whose entry zero was a
    /// non-selectable prompt, while the portal root's own entry was valued with the EMPTY STRING; there is
    /// no index here, so a presence rule on this member would refuse the legacy's own root selection.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Documents/")]
    public void ExportFolder_IsNeverRefused(string? folder)
        => new ModuleExportRequestValidator()
            .Validate(new ModuleExportRequest { FileName = "content", Folder = folder })
            .IsValid.Should().BeTrue();

    /// <summary>
    /// An import naming no module is refused naming <c>ModuleId</c>, and one naming module zero is
    /// accepted.
    /// </summary>
    /// <remarks>
    /// The pair is one fact because the two halves are the same rule read from either side, and separating
    /// them would let the refusing half be satisfied by a rule that also refuses the accepting half.
    /// </remarks>
    [Fact]
    public void ImportWithoutAModule_IsRefusedWhileModuleZeroIsAccepted()
    {
        ValidationResult unnamed = new ModuleImportRequestValidator()
            .Validate(new ModuleImportRequest { Content = "<content />" });

        unnamed.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(ModuleImportRequest.ModuleId));

        unnamed.Errors[0].ErrorMessage.Should().Be(
            "The module to import into must be supplied.",
            "the service raises this same sentence for this same condition");

        new ModuleImportRequestValidator()
            .Validate(new ModuleImportRequest { ModuleId = ModuleIdentitySeed, Content = "<content />" })
            .IsValid.Should().BeTrue(
                "Modules.ModuleID is IDENTITY(0, 1), so zero names the first module an installation created "
                + "and no rule may read it as an absence");
    }

    /// <summary>
    /// The legacy integer sentinel is accepted by the boundary, so it fails as a LOOKUP rather than as a
    /// malformed request.
    /// </summary>
    /// <remarks>
    /// <c>-1</c> is simultaneously <c>Null.NullInteger</c>, the legacy import field's "unresolved" initial
    /// value and a real <c>Portals.PortalID</c>. A caller sending it has made a lookup that will not
    /// resolve, and the two outcomes must stay distinguishable: a boundary refusal would tell the caller
    /// its request was malformed when the request was well formed and the module simply does not exist.
    /// </remarks>
    [Fact]
    public void ImportNamingTheLegacySentinel_IsAcceptedByTheBoundary()
        => new ModuleImportRequestValidator()
            .Validate(new ModuleImportRequest { ModuleId = -1, Content = "<content />" })
            .IsValid.Should().BeTrue();

    /// <summary>An import carrying no document is refused naming <c>Content</c>.</summary>
    /// <param name="content">The submitted document.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ImportWithoutADocument_IsRefusedNamingTheMember(string? content)
    {
        ValidationResult result = new ModuleImportRequestValidator()
            .Validate(new ModuleImportRequest { ModuleId = ModuleIdentitySeed, Content = content });

        result.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(ModuleImportRequest.Content));

        result.Errors[0].ErrorMessage.Should().Be(
            "The submitted document is empty.",
            "the service raises this same sentence for this same condition");
    }

    /// <summary>
    /// The import validator judges PRESENCE only, and leaves every semantic question to the service.
    /// </summary>
    /// <param name="content">A document that is present but that the service will refuse.</param>
    [Theory]
    [InlineData("<content>unclosed")]
    [InlineData("<payload>x</payload>")]
    [InlineData("<content type=\"SomeOtherModule\">x</content>")]
    public void ImportValidator_LeavesTheSemanticJudgementsToTheService(string content)
        => new ModuleImportRequestValidator()
            .Validate(new ModuleImportRequest { ModuleId = ModuleIdentitySeed, Content = content })
            .IsValid.Should().BeTrue();

    /// <summary>Neither import text member is bounded or required.</summary>
    /// <param name="fileName">The descriptive document name.</param>
    /// <param name="folder">The descriptive source folder.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("content.DNNHTML.Announcements.xml", "Documents/")]
    public void ImportDescriptiveMembers_AreNeverRefused(string? fileName, string? folder)
        => new ModuleImportRequestValidator()
            .Validate(new ModuleImportRequest
            {
                ModuleId = ModuleIdentitySeed,
                Content = "<content />",
                FileName = fileName,
                Folder = folder,
            })
            .IsValid.Should().BeTrue();
}
