using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Pins the ONE visible-text policy across every write contract that accepts an administrator-authored
/// label, so a character refused on one screen cannot be accepted on a comparable one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists rather than one case per validator.</b> The defect it closes was not a missing
/// rule; it was a rule that existed and was applied to some members and not their siblings. A security role
/// refused a name carrying a NUL while a page accepted one, and the page's name is worse than most because
/// it is route-generating: the name persists verbatim while <c>TabPath</c> is produced by stripping every
/// non-word character from it, so a name holding a NUL stored as six characters and generated a
/// five-character path. The label an operator read and the route the page answered on no longer represented
/// the same text, and neither value was wrong on its own terms. A per-validator test cannot fail when a NEW
/// contract forgets the rule; a policy test enumerated over contracts can.
/// </para>
/// <para>
/// <b>The hostile set.</b> Four groups, each refused for its own reason. C0 and C1 control characters
/// display as nothing or as a replacement glyph. The zero-width group occupies no width, so an operator
/// cannot see that it is there. The Unicode <c>Bidi_Control</c> group is the dangerous one: it does not
/// merely hide, it REORDERS, so a stored value can render as text that is not the text stored - U+202E being
/// the well-known case. A tab and a line break are control characters too, and they are refused on
/// single-line members and admitted on multi-line ones, which is the only distinction between the two rules.
/// </para>
/// <para>
/// <b>What must still be accepted.</b> Accents, CJK, emoji and - specifically - emoji joined by U+200D. The
/// zero-width joiner is invisible in isolation and would otherwise qualify for refusal, but it is what holds
/// a family or flag glyph together, and this installation's own stored data carries emoji in page and module
/// titles. A rule that refused it would refuse text an operator can see perfectly well.
/// </para>
/// </remarks>
public class TextIntegrityPolicyTests
{
    /// <summary>The NUL byte measured in a stored page name, and the first C0 control character.</summary>
    private const string Nul = "\u0000";

    /// <summary>A C0 control other than the NUL, so the rule is not read as a NUL special case.</summary>
    private const string Escape = "\u001B";

    /// <summary>A C1 control, which is the other half of what <c>char.IsControl</c> covers.</summary>
    private const string C1Control = "\u0085";

    /// <summary>The zero-width space.</summary>
    private const string ZeroWidthSpace = "\u200B";

    /// <summary>The byte-order mark, which is invisible anywhere but the start of a stream.</summary>
    private const string ByteOrderMark = "\uFEFF";

    /// <summary>The right-to-left override, the bidirectional control the QA pass measured being accepted.</summary>
    private const string RightToLeftOverride = "\u202E";

    /// <summary>A first-strong isolate, so the isolate half of the bidi group is covered too.</summary>
    private const string FirstStrongIsolate = "\u2068";

    /// <summary>The Arabic letter mark, the lowest-numbered member of the bidi group.</summary>
    private const string ArabicLetterMark = "\u061C";

    /// <summary>A line break, refused on a single line and admitted in a text block.</summary>
    private const string LineFeed = "\n";

    /// <summary>A tab, refused on a single line and admitted in a text block.</summary>
    private const string Tab = "\t";

    /// <summary>Every character class no administrator-authored value may carry, on any member.</summary>
    /// <returns>One hostile fragment per row.</returns>
    public static TheoryData<string> UniversallyRefused()
    {
        var data = new TheoryData<string>();

        foreach (string fragment in new[]
        {
            Nul,
            Escape,
            C1Control,
            ZeroWidthSpace,
            ByteOrderMark,
            RightToLeftOverride,
            FirstStrongIsolate,
            ArabicLetterMark,
        })
        {
            data.Add(fragment);
        }

        return data;
    }

    /// <summary>The two whitespace controls a text block admits and a single-line value does not.</summary>
    /// <returns>One fragment per row.</returns>
    public static TheoryData<string> RefusedOnASingleLineOnly()
    {
        var data = new TheoryData<string>();
        data.Add(LineFeed);
        data.Add(Tab);
        return data;
    }

    /// <summary>Text an operator can read, which every member must accept.</summary>
    /// <returns>One legitimate value per row.</returns>
    public static TheoryData<string> Legitimate()
    {
        var data = new TheoryData<string>();

        // Accented Latin, CJK, a lone emoji, and - the case that matters - an emoji sequence held together
        // by U+200D, which is invisible and deliberately not refused.
        foreach (string value in new[]
        {
            "Ordinary Name",
            "Ünïcödé Nämé",
            "\u6a21\u5757",
            "Rocket \U0001F680",
            "Family \U0001F468\u200D\U0001F469\u200D\U0001F467",
            "Name (with punctuation) - 2026",
        })
        {
            data.Add(value);
        }

        return data;
    }

    // =============================================================================================
    // THE PAGE CONTRACT, which is where the defect was measured.
    // =============================================================================================

    /// <summary>A page name carrying any hostile character is refused.</summary>
    /// <param name="hostile">The hostile fragment.</param>
    [Theory]
    [MemberData(nameof(UniversallyRefused))]
    [MemberData(nameof(RefusedOnASingleLineOnly))]
    public void PageName_CarryingAnUnreadableCharacter_IsRefused(string hostile)
    {
        ValidationResult result = Validate(TabRequest("P9" + hostile + "NUL"));

        result.IsValid.Should().BeFalse(
            "a page name generates TabPath by stripping non-word characters, so a name no operator can "
            + "retype also produces a route that does not represent it");
        result.Errors.Should().Contain(failure =>
            failure.PropertyName == nameof(UpdateTabRequest.TabName));
    }

    /// <summary>A page name an operator can read is accepted.</summary>
    /// <param name="legitimate">The legitimate name.</param>
    [Theory]
    [MemberData(nameof(Legitimate))]
    public void PageName_CarryingReadableText_IsAccepted(string legitimate)
    {
        Validate(TabRequest(legitimate)).IsValid.Should().BeTrue(
            "the rule refuses characters that cannot be seen, not characters outside ASCII");
    }

    /// <summary>Every single-line member of the page contract applies the rule.</summary>
    /// <param name="member">The member under test.</param>
    [Theory]
    [InlineData(nameof(UpdateTabRequest.TabName))]
    [InlineData(nameof(UpdateTabRequest.Title))]
    [InlineData(nameof(UpdateTabRequest.IconFile))]
    [InlineData(nameof(UpdateTabRequest.Url))]
    public void PageSingleLineMembers_RefuseALineBreakAndABidiControl(string member)
    {
        foreach (string hostile in new[] { LineFeed, RightToLeftOverride, Nul })
        {
            UpdateTabRequest request = TabRequest("Acceptable Page");
            string value = member == nameof(UpdateTabRequest.Url)
                ? "https://example.test/" + hostile
                : "Value" + hostile + "More";

            switch (member)
            {
                case nameof(UpdateTabRequest.TabName):
                    request.TabName = value;
                    break;
                case nameof(UpdateTabRequest.Title):
                    request.Title = value;
                    break;
                case nameof(UpdateTabRequest.IconFile):
                    request.IconFile = value;
                    break;
                default:
                    request.Url = value;
                    break;
            }

            ValidationResult result = Validate(request);

            result.IsValid.Should().BeFalse($"{member} is a single line and carried {Describe(hostile)}");
            result.Errors.Should().Contain(
                failure => failure.PropertyName == member,
                $"{member} is the member that must be named in the refusal");
        }
    }

    /// <summary>
    /// Every text-block member of the page contract admits a tab and a line break and refuses the rest.
    /// </summary>
    /// <param name="member">The member under test.</param>
    [Theory]
    [InlineData(nameof(UpdateTabRequest.Description))]
    [InlineData(nameof(UpdateTabRequest.Keywords))]
    [InlineData(nameof(UpdateTabRequest.PageHeadText))]
    public void PageTextBlockMembers_AdmitWhitespaceControlsAndRefuseTheRest(string member)
    {
        Assign(member, "First line\r\n\tSecond line").IsValid.Should().BeTrue(
            "these three were authored in text areas, so a tab and a line break are content");

        foreach (string hostile in new[] { Nul, ZeroWidthSpace, RightToLeftOverride })
        {
            ValidationResult refused = Assign(member, "Text" + hostile + "more");

            refused.IsValid.Should().BeFalse($"{member} carried {Describe(hostile)}");
            refused.Errors.Should().Contain(failure => failure.PropertyName == member);
        }

        ValidationResult Assign(string target, string value)
        {
            UpdateTabRequest request = TabRequest("Acceptable Page");

            switch (target)
            {
                case nameof(UpdateTabRequest.Description):
                    request.Description = value;
                    break;
                case nameof(UpdateTabRequest.Keywords):
                    request.Keywords = value;
                    break;
                default:
                    request.PageHeadText = value;
                    break;
            }

            return Validate(request);
        }
    }

    // =============================================================================================
    // THE POLICY, asserted across contracts rather than within one.
    // =============================================================================================

    /// <summary>
    /// A name carrying U+202E is refused by every contract that accepts an administrator-authored name.
    /// </summary>
    /// <remarks>
    /// This is the shape of the original defect stated as a policy: the same character, the same kind of
    /// member, one answer. The role contract already refused it before the bidi group was added to the
    /// shared rule; the page, role-group, module, tenant, account and profile-definition contracts did not.
    /// </remarks>
    [Fact]
    public void EveryAdministratorAuthoredName_RefusesABidirectionalOverride()
    {
        const string hostile = "Admin" + "\u202E" + "istrators";

        Validate(TabRequest(hostile)).IsValid.Should()
            .BeFalse("a page name is route-generating and must be legible");

        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { RoleName = hostile })
            .IsValid.Should().BeFalse("a role name decides authorisation and must read as itself");

        new CreateRoleRequestValidator()
            .Validate(new CreateRoleRequest { RoleName = hostile })
            .IsValid.Should().BeFalse("the create path must not admit what the update path refuses");

        new UpdateRoleGroupRequestValidator()
            .Validate(new UpdateRoleGroupRequest { RoleGroupName = hostile })
            .IsValid.Should().BeFalse("a role group is named on the same screen family as a role");

        new CreateRoleGroupRequestValidator()
            .Validate(new CreateRoleGroupRequest { RoleGroupName = hostile })
            .IsValid.Should().BeFalse("the create path must not admit what the update path refuses");

        new UpdateModuleRequestValidator()
            .Validate(new UpdateModuleRequest { TabId = 1, ModuleTitle = hostile })
            .IsValid.Should().BeFalse("a module title is rendered in a container heading");

        new CreateModuleRequestValidator()
            .Validate(new CreateModuleRequest { ModuleDefId = 1, TabId = 1, ModuleTitle = hostile })
            .IsValid.Should().BeFalse("the create path must not admit what the update path refuses");

        new UpdatePortalSettingsRequestValidator()
            .Validate(new UpdatePortalSettingsRequest { PortalName = hostile })
            .IsValid.Should().BeFalse("a tenant name appears in every page title it serves");

        new UpdateUserRequestValidator()
            .Validate(new UpdateUserRequest
            {
                FirstName = "Given",
                LastName = "Family",
                DisplayName = hostile,
                Email = "person@example.test",
            })
            .IsValid.Should().BeFalse("a display name identifies an account to an operator");
    }

    /// <summary>
    /// A NUL is refused by every contract that accepts an administrator-authored name, on the create path
    /// and the update path alike.
    /// </summary>
    [Fact]
    public void EveryAdministratorAuthoredName_RefusesANulByte()
    {
        const string hostile = "Name" + "\u0000" + "Suffix";

        Validate(TabRequest(hostile)).IsValid.Should().BeFalse();

        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { RoleName = hostile })
            .IsValid.Should().BeFalse();

        new UpdateRoleGroupRequestValidator()
            .Validate(new UpdateRoleGroupRequest { RoleGroupName = hostile })
            .IsValid.Should().BeFalse();

        new UpdateModuleRequestValidator()
            .Validate(new UpdateModuleRequest { TabId = 1, ModuleTitle = hostile })
            .IsValid.Should().BeFalse();

        new UpdatePortalSettingsRequestValidator()
            .Validate(new UpdatePortalSettingsRequest { PortalName = hostile })
            .IsValid.Should().BeFalse();

        new UpdateProfilePropertyDefinitionRequestValidator()
            .Validate(new UpdateProfilePropertyDefinitionRequest
            {
                PropertyName = "Nickname",
                PropertyCategory = hostile,
                DataType = 349,
            })
            .IsValid.Should().BeFalse("a profile category groups fields on a screen an operator reads");

        new CreateUserRequestValidator(new PasswordPolicyOptions())
            .Validate(new CreateUserRequest
            {
                Username = hostile,
                FirstName = "Given",
                LastName = "Family",
                Email = "person@example.test",
                Password = "Passw0rd!",
                ConfirmPassword = "Passw0rd!",
            })
            .IsValid.Should().BeFalse("a login name is typed by the person who owns it");
    }

    /// <summary>
    /// An emoji sequence joined by U+200D survives every contract, so the rule refuses invisibility rather
    /// than non-ASCII text.
    /// </summary>
    /// <remarks>
    /// The joiner is invisible on its own and would qualify for refusal on that test alone. It is excluded
    /// by name, and this is the assertion that keeps it excluded: the stored fixtures this project runs
    /// against already carry emoji in page and module titles.
    /// </remarks>
    [Fact]
    public void AnEmojiSequenceJoinedByTheZeroWidthJoiner_IsAcceptedEverywhere()
    {
        const string legitimate = "Team \U0001F468\u200D\U0001F469\u200D\U0001F467";

        Validate(TabRequest(legitimate)).IsValid.Should().BeTrue();

        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { RoleName = legitimate })
            .IsValid.Should().BeTrue();

        new UpdateRoleGroupRequestValidator()
            .Validate(new UpdateRoleGroupRequest { RoleGroupName = legitimate })
            .IsValid.Should().BeTrue();

        new UpdateModuleRequestValidator()
            .Validate(new UpdateModuleRequest { TabId = 1, ModuleTitle = legitimate })
            .IsValid.Should().BeTrue();

        new UpdatePortalSettingsRequestValidator()
            .Validate(new UpdatePortalSettingsRequest { PortalName = legitimate })
            .IsValid.Should().BeTrue();

        new UpdateUserRequestValidator()
            .Validate(new UpdateUserRequest
            {
                FirstName = "Given",
                LastName = "Family",
                DisplayName = legitimate,
                Email = "person@example.test",
            })
            .IsValid.Should().BeTrue();
    }

    /// <summary>
    /// A text block admits a tab and a line break wherever one is offered, and refuses the unreadable
    /// remainder.
    /// </summary>
    [Fact]
    public void EveryTextBlock_AdmitsWhitespaceControlsAndRefusesTheUnreadableRemainder()
    {
        const string readable = "First line\r\n\tIndented second line";
        const string hostile = "First line\u200Bhidden";

        Validate(WithDescription(readable)).IsValid.Should().BeTrue();
        Validate(WithDescription(hostile)).IsValid.Should().BeFalse();

        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { RoleName = "Ordinary", Description = readable })
            .IsValid.Should().BeTrue();
        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { RoleName = "Ordinary", Description = hostile })
            .IsValid.Should().BeFalse();

        new UpdateRoleGroupRequestValidator()
            .Validate(new UpdateRoleGroupRequest { RoleGroupName = "Ordinary", Description = readable })
            .IsValid.Should().BeTrue();
        new UpdateRoleGroupRequestValidator()
            .Validate(new UpdateRoleGroupRequest { RoleGroupName = "Ordinary", Description = hostile })
            .IsValid.Should().BeFalse();

        new UpdatePortalSettingsRequestValidator()
            .Validate(new UpdatePortalSettingsRequest { PortalName = "Site", Description = readable })
            .IsValid.Should().BeTrue();
        new UpdatePortalSettingsRequestValidator()
            .Validate(new UpdatePortalSettingsRequest { PortalName = "Site", Description = hostile })
            .IsValid.Should().BeFalse();

        static UpdateTabRequest WithDescription(string description)
        {
            UpdateTabRequest request = TabRequest("Acceptable Page");
            request.Description = description;
            return request;
        }
    }

    /// <summary>
    /// The refusal names direction-changing characters, so an operator who submitted one is told what to
    /// look for.
    /// </summary>
    /// <remarks>
    /// A bidirectional control CAN be displayed - that is the problem with it - so a message saying only
    /// "cannot be displayed" would send the operator looking for the wrong thing.
    /// </remarks>
    [Fact]
    public void TheRefusalWordingNamesWhatWasWrong()
    {
        ValidationResult singleLine = Validate(TabRequest("Name\u202ESuffix"));

        singleLine.IsValid.Should().BeFalse();
        singleLine.Errors.Should().Contain(failure =>
            failure.PropertyName == nameof(UpdateTabRequest.TabName)
            && failure.ErrorMessage.Contains("direction", StringComparison.Ordinal)
            && failure.ErrorMessage.Contains("visible characters only", StringComparison.Ordinal));

        UpdateTabRequest withBlock = TabRequest("Acceptable Page");
        withBlock.Description = "Text\u0000more";

        ValidationResult multiLine = Validate(withBlock);

        multiLine.IsValid.Should().BeFalse();
        multiLine.Errors.Should().Contain(failure =>
            failure.PropertyName == nameof(UpdateTabRequest.Description)
            && failure.ErrorMessage.Contains("tabs and line breaks", StringComparison.Ordinal));
    }

    /// <summary>Runs the page validator.</summary>
    /// <param name="request">The request under test.</param>
    /// <returns>The validation result.</returns>
    private static ValidationResult Validate(UpdateTabRequest request)
        => new UpdateTabRequestValidator().Validate(request);

    /// <summary>Builds an otherwise-valid page request carrying the supplied name.</summary>
    /// <param name="tabName">The page name under test.</param>
    /// <returns>The request.</returns>
    private static UpdateTabRequest TabRequest(string tabName) => new() { TabName = tabName };

    /// <summary>Names a hostile fragment for an assertion message.</summary>
    /// <param name="fragment">The fragment.</param>
    /// <returns>A short description naming the code point.</returns>
    private static string Describe(string fragment)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"U+{(int)fragment[0]:X4}");
}
