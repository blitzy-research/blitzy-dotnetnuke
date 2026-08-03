using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Pins the field rules on the four requests that carried no validator at all until a security review
/// found them, other than the storage-range rules pinned beside this file.
/// </summary>
/// <remarks>
/// <para>
/// Each of these requests is bound by an endpoint, so before the validators existed every field on them
/// reached the service and then the provider unbounded: an over-long value was refused by the column
/// rather than by a field-level answer, and a blank page name was accepted outright. The rules asserted
/// here are the column widths, the shared icon containment rule, the single-line rule on a link target,
/// and the settings cardinality bounds.
/// </para>
/// <para>
/// The width assertions submit one character PAST each column, not some round larger number, so each test
/// pins the width itself rather than merely proving that something long enough is refused.
/// </para>
/// </remarks>
public class WriteSurfaceBoundTests
{
    /// <summary>
    /// A blank page name is refused, which restores the legacy screen's own behaviour.
    /// </summary>
    /// <param name="submittedName">The blank name under test.</param>
    /// <remarks>
    /// The legacy required-field validator declares no initial value, so its initial value is the empty
    /// string and it failed precisely when the trimmed control value equalled that. The empty string was
    /// the one value the screen refused, and because the comparison follows a trim, a name of spaces was
    /// refused with it.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void PageName_WhenBlank_IsRefused(string submittedName)
    {
        ValidationResult result = new UpdateTabRequestValidator()
            .Validate(new UpdateTabRequest { TabName = submittedName });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(UpdateTabRequest.TabName));
    }

    /// <summary>
    /// Every page text field is bounded by its own column width.
    /// </summary>
    /// <param name="member">The member under test, named for the failure assertion.</param>
    /// <param name="width">The column width, one past which the value is refused.</param>
    [Theory]
    [InlineData(nameof(UpdateTabRequest.TabName), 50)]
    [InlineData(nameof(UpdateTabRequest.Title), 200)]
    [InlineData(nameof(UpdateTabRequest.Description), 500)]
    [InlineData(nameof(UpdateTabRequest.Keywords), 500)]
    [InlineData(nameof(UpdateTabRequest.PageHeadText), 500)]
    [InlineData(nameof(UpdateTabRequest.Url), 255)]
    [InlineData(nameof(UpdateTabRequest.SkinSrc), 200)]
    [InlineData(nameof(UpdateTabRequest.ContainerSrc), 200)]
    [InlineData(nameof(UpdateTabRequest.IconFile), 100)]
    public void PageTextFields_AreBoundedByTheirColumnWidth(string member, int width)
    {
        new UpdateTabRequestValidator()
            .Validate(PageWith(member, new string('a', width)))
            .IsValid.Should().BeTrue(member + " must accept a value that exactly fills its column");

        ValidationResult overflowing = new UpdateTabRequestValidator()
            .Validate(PageWith(member, new string('a', width + 1)));

        overflowing.Errors.Should().Contain(
            failure => failure.PropertyName == member,
            member + " must be refused one character past its column");
    }

    /// <summary>
    /// A page icon reference that escapes the portal's folder is refused, and a file-reference token is
    /// not.
    /// </summary>
    /// <param name="iconFile">The reference under test.</param>
    /// <param name="contained">Whether the reference stays inside the portal's folder.</param>
    /// <remarks>
    /// The token form is asserted because the request contract states that an icon value is opaque and may
    /// be a raw <c>fileid=NNN</c> token stored verbatim. A containment rule that refused the token would
    /// have broken a documented contract, so its acceptance is pinned rather than assumed.
    /// </remarks>
    [Theory]
    [InlineData("images/page.gif", true)]
    [InlineData("fileid=1234", true)]
    [InlineData("", true)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("\\\\server\\share\\icon.gif", false)]
    [InlineData("C:\\icon.gif", false)]
    [InlineData("http://elsewhere.example/icon.gif", false)]
    public void PageIconReference_IsRefusedOnlyWhenItEscapesThePortalFolder(string iconFile, bool contained)
        => new UpdateTabRequestValidator()
            .Validate(new UpdateTabRequest { TabName = "Home", IconFile = iconFile })
            .IsValid.Should().Be(contained);

    /// <summary>
    /// The icon containment rule is the SAME rule on every path that accepts an icon.
    /// </summary>
    /// <remarks>
    /// This is the property the shared rule exists to provide, and it is asserted because the three paths
    /// disagreed before it: the check was private to the role create validator, so the role update and the
    /// page update accepted references the create path refused, and the weakest path defined the
    /// application's actual behaviour.
    /// </remarks>
    [Fact]
    public void IconContainment_IsEnforcedIdenticallyOnEveryPathThatAcceptsAnIcon()
    {
        const string escaping = "../../etc/passwd";

        new CreateRoleRequestValidator()
            .Validate(new CreateRoleRequest { RoleName = "Subscribers", IconFile = escaping })
            .IsValid.Should().BeFalse("the role create path refuses an escaping reference");

        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { RoleName = "Subscribers", IconFile = escaping })
            .IsValid.Should().BeFalse("so must the role update path");

        new UpdateTabRequestValidator()
            .Validate(new UpdateTabRequest { TabName = "Home", IconFile = escaping })
            .IsValid.Should().BeFalse("and so must the page update path");
    }

    /// <summary>
    /// A link target carrying a line break is refused, while an ordinary absolute URL is accepted.
    /// </summary>
    /// <remarks>
    /// No format, scheme or containment rule is applied to a link target, and that restraint is asserted
    /// here: the legacy form applied no format validation and the value legitimately takes three unrelated
    /// shapes. What is refused is a control character, which no shape of the value can contain and which is
    /// the vehicle for splitting a response header should the stored value ever be emitted into one.
    /// </remarks>
    [Theory]
    [InlineData("http://example.test/page", true)]
    [InlineData("fileid=99", true)]
    [InlineData("42", true)]
    [InlineData("http://example.test/\r\nSet-Cookie: x=1", false)]
    [InlineData("http://example.test/\npage", false)]
    [InlineData("http://example.test/\0page", false)]
    public void PageLinkTarget_RefusesControlCharactersAndNothingElse(string url, bool acceptable)
        => new UpdateTabRequestValidator()
            .Validate(new UpdateTabRequest { TabName = "Home", Url = url })
            .IsValid.Should().Be(acceptable);

    /// <summary>
    /// A page refresh interval is never refused by shape, whatever its magnitude or sign.
    /// </summary>
    /// <param name="refreshInterval">The interval under test.</param>
    /// <remarks>
    /// <para>
    /// AN EARLIER REVISION OF THIS FACT PINNED A CEILING OF ONE DAY AND REFUSED A NEGATIVE, and it has been
    /// retargeted rather than deleted, because the rule it pinned was withdrawn on measurement rather than
    /// on preference. The page-administration screen declares no validator over the refresh field and the
    /// column is a plain integer, so no legacy submission was refused on this member; the ceiling was
    /// self-described as net-new. The migration discipline requires validation rules to MATCH the original
    /// rather than improve on it, and a bound nothing measured is exactly the improvement it forbids.
    /// </para>
    /// <para>
    /// What survives from the finding is the part that was measured: the widths, the presence rule, the icon
    /// containment rule, the single-line link rule and the date-representability bound, each asserted
    /// elsewhere in this file and in the storage-range suite. The withdrawal is recorded in
    /// MIGRATION_NOTES.md as legacy behaviour preserved.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(86_400)]
    [InlineData(86_401)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void PageRefreshInterval_IsNeverRefusedByShape(int refreshInterval)
        => new UpdateTabRequestValidator()
            .Validate(new UpdateTabRequest { TabName = "Home", RefreshInterval = refreshInterval })
            .IsValid.Should().BeTrue();

    /// <summary>
    /// The role update path bounds every text field by its own column width.
    /// </summary>
    /// <param name="member">The member under test.</param>
    /// <param name="width">The column width, one past which the value is refused.</param>
    [Theory]
    [InlineData(nameof(UpdateRoleRequest.Description), 1_000)]
    [InlineData(nameof(UpdateRoleRequest.RsvpCode), 50)]
    [InlineData(nameof(UpdateRoleRequest.IconFile), 100)]
    public void RoleUpdateTextFields_AreBoundedByTheirColumnWidth(string member, int width)
    {
        new UpdateRoleRequestValidator()
            .Validate(RoleUpdateWith(member, new string('a', width)))
            .IsValid.Should().BeTrue(member + " must accept a value that exactly fills its column");

        new UpdateRoleRequestValidator()
            .Validate(RoleUpdateWith(member, new string('a', width + 1)))
            .Errors.Should().Contain(
                failure => failure.PropertyName == member,
                member + " must be refused one character past its column");
    }

    /// <summary>
    /// A frequency outside the domain enumeration is refused on the role update path.
    /// </summary>
    [Fact]
    public void RoleUpdateFrequencies_OutsideTheEnumeration_AreRefused()
    {
        ValidationResult result = new UpdateRoleRequestValidator().Validate(new UpdateRoleRequest
        {
            RoleName = "Subscribers",
            BillingFrequency = (BillingFrequency)99,
        });

        result.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(UpdateRoleRequest.BillingFrequency));
    }

    /// <summary>
    /// A role update carrying nothing but the name is valid, because every OTHER member is optional - and
    /// one carrying nothing at all is refused, naming the name.
    /// </summary>
    /// <remarks>
    /// Asserted as one fact from both sides so that the width and range rules cannot drift into presence
    /// rules, and so the single presence rule cannot quietly disappear. The name is required because
    /// <c>Roles.RoleName</c> is <c>NOT NULL</c> and the contract is a replacement; every remaining member
    /// has a stored default or is nullable, so a submission that changes nothing else is a legitimate
    /// no-op rather than a malformed request.
    /// </remarks>
    [Fact]
    public void RoleUpdate_CarryingNothingButItsName_IsValid()
    {
        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { RoleName = "Subscribers" })
            .IsValid.Should().BeTrue();

        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest())
            .Errors.Should().Contain(
                failure => failure.PropertyName == nameof(UpdateRoleRequest.RoleName),
                "the one presence rule the legacy screen declared applies to both write verbs");
    }

    /// <summary>
    /// An explicitly absent settings map is refused rather than faulting, on both scopes.
    /// </summary>
    /// <remarks>
    /// Both members are non-nullable reference types carrying an initialiser, which makes null look
    /// unreachable - but an initialiser only runs when the deserialiser does not assign, and a body carrying
    /// an explicit null assigns over it.
    /// </remarks>
    [Fact]
    public void ModuleSettings_WhenAMapIsAbsent_AreRefused()
    {
        var missingModuleMap = new ModuleSettingsDto { ModuleSettings = null! };
        var missingPlacementMap = new ModuleSettingsDto { TabModuleSettings = null! };

        new ModuleSettingsDtoValidator().Validate(missingModuleMap).Errors.Should().Contain(
            failure => failure.PropertyName == nameof(ModuleSettingsDto.ModuleSettings));

        new ModuleSettingsDtoValidator().Validate(missingPlacementMap).Errors.Should().Contain(
            failure => failure.PropertyName == nameof(ModuleSettingsDto.TabModuleSettings));
    }

    /// <summary>
    /// Two empty settings maps are valid, because that is how every setting is cleared.
    /// </summary>
    [Fact]
    public void ModuleSettings_WhenBothMapsAreEmpty_AreValid()
        => new ModuleSettingsDtoValidator()
            .Validate(new ModuleSettingsDto())
            .IsValid.Should().BeTrue("replacing the settings with none is how they are cleared");

    /// <summary>
    /// A settings map carrying more entries than a scope permits is refused, at the boundary.
    /// </summary>
    [Fact]
    public void ModuleSettings_PastThePerScopeBound_AreRefused()
    {
        const int perScopeMaximum = 250;

        new ModuleSettingsDtoValidator()
            .Validate(new ModuleSettingsDto { ModuleSettings = Settings("s", perScopeMaximum) })
            .IsValid.Should().BeTrue("a scope filled exactly to its bound is accepted");

        new ModuleSettingsDtoValidator()
            .Validate(new ModuleSettingsDto { ModuleSettings = Settings("s", perScopeMaximum + 1) })
            .Errors.Should().Contain(
                failure => failure.PropertyName == nameof(ModuleSettingsDto.ModuleSettings));
    }

    /// <summary>
    /// Two maps that are individually permissible but jointly excessive are refused.
    /// </summary>
    /// <remarks>
    /// This is the case the per-scope bound alone does not catch, and the one that bounds the size of the
    /// single transaction the service opens. Both maps below sit inside the per-scope bound of 250 and
    /// exceed the aggregate bound of 400 between them.
    /// </remarks>
    [Fact]
    public void ModuleSettings_PastTheAggregateBound_AreRefused()
        => new ModuleSettingsDtoValidator().Validate(new ModuleSettingsDto
        {
            ModuleSettings = Settings("module", 220),
            TabModuleSettings = Settings("placement", 220),
        }).IsValid.Should().BeFalse("the two scopes together exceed the aggregate bound");

    /// <summary>
    /// A blank setting name and an over-long name or value are each refused.
    /// </summary>
    [Fact]
    public void ModuleSettings_WithAMalformedEntry_AreRefused()
    {
        var blankName = new ModuleSettingsDto
        {
            ModuleSettings = new Dictionary<string, string> { [" "] = "value" },
        };

        var longName = new ModuleSettingsDto
        {
            ModuleSettings = new Dictionary<string, string> { [new string('n', 51)] = "value" },
        };

        var longValue = new ModuleSettingsDto
        {
            ModuleSettings = new Dictionary<string, string> { ["colour"] = new string('v', 2_001) },
        };

        new ModuleSettingsDtoValidator().Validate(blankName).IsValid.Should().BeFalse();
        new ModuleSettingsDtoValidator().Validate(longName).IsValid.Should().BeFalse();
        new ModuleSettingsDtoValidator().Validate(longValue).IsValid.Should().BeFalse();
    }

    /// <summary>
    /// A setting whose name and value exactly fill their columns is accepted.
    /// </summary>
    [Fact]
    public void ModuleSettings_FillingTheirColumnsExactly_AreAccepted()
        => new ModuleSettingsDtoValidator().Validate(new ModuleSettingsDto
        {
            ModuleSettings = new Dictionary<string, string>
            {
                [new string('n', 50)] = new string('v', 2_000),
            },
        }).IsValid.Should().BeTrue();

    /// <summary>
    /// Builds a settings map of the requested size.
    /// </summary>
    /// <param name="prefix">Prefix for each generated name.</param>
    /// <param name="count">Number of entries to generate.</param>
    /// <returns>The map.</returns>
    private static Dictionary<string, string> Settings(string prefix, int count)
    {
        var map = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            map[FormattableString.Invariant($"{prefix}{index}")] = "value";
        }

        return map;
    }

    /// <summary>
    /// Builds a page update carrying the given value on the named member and valid values elsewhere.
    /// </summary>
    /// <param name="member">The member to populate.</param>
    /// <param name="value">The value to place on it.</param>
    /// <returns>The request.</returns>
    private static UpdateTabRequest PageWith(string member, string value)
    {
        var request = new UpdateTabRequest { TabName = "Home" };

        switch (member)
        {
            case nameof(UpdateTabRequest.TabName): request.TabName = value; break;
            case nameof(UpdateTabRequest.Title): request.Title = value; break;
            case nameof(UpdateTabRequest.Description): request.Description = value; break;
            case nameof(UpdateTabRequest.Keywords): request.Keywords = value; break;
            case nameof(UpdateTabRequest.PageHeadText): request.PageHeadText = value; break;
            case nameof(UpdateTabRequest.Url): request.Url = value; break;
            case nameof(UpdateTabRequest.SkinSrc): request.SkinSrc = value; break;
            case nameof(UpdateTabRequest.ContainerSrc): request.ContainerSrc = value; break;
            case nameof(UpdateTabRequest.IconFile): request.IconFile = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(member), member, "Unmapped member.");
        }

        return request;
    }

    /// <summary>
    /// Builds a role update carrying the given value on the named member.
    /// </summary>
    /// <param name="member">The member to populate.</param>
    /// <param name="value">The value to place on it.</param>
    /// <returns>The request.</returns>
    private static UpdateRoleRequest RoleUpdateWith(string member, string value)
    {
        // The name is populated because the update contract requires one, so a width fact isolates the
        // width it names instead of also tripping the presence rule.
        var request = new UpdateRoleRequest { RoleName = "Subscribers" };

        switch (member)
        {
            case nameof(UpdateRoleRequest.Description): request.Description = value; break;
            case nameof(UpdateRoleRequest.RsvpCode): request.RsvpCode = value; break;
            case nameof(UpdateRoleRequest.IconFile): request.IconFile = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(member), member, "Unmapped member.");
        }

        return request;
    }
}
