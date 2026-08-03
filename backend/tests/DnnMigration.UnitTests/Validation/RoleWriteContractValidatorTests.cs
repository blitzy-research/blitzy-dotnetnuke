// MIGRATION: this suite is the parity proof for the three role-family validators that the review found
// missing altogether - UpdateRoleRequestValidator, RoleGroupDtoValidator and
// RoleAssignmentRequestValidator - together with the shared rule definition they and
// CreateRoleRequestValidator all consume, RoleTermsRules.
//
// MIGRATION: the suite's most important single assertion is the CROSS-VERB one. Before the repair, the
// icon-path containment rule existed on the creation validator and not on the update validator, so a
// rooted or upward-traversing path that POST refused was stored verbatim by PUT against the very same
// dbo.Roles.IconFile column. A rule that can be bypassed by choosing the other verb is not a rule, so
// the parity between the two validators is asserted directly rather than being left to inspection of
// two files that happen to look alike.
//
// MIGRATION: the legacy declarations these validators reproduce, measured rather than assumed, and read
// case-insensitively because the legacy markup writes its tags in lower case - a case-sensitive search
// for "Validator" returns nothing on either screen and would falsely suggest neither declared any rule.
//   Website/admin/Security/editroles.ascx        L29/L31  valRoleName       RequiredFieldValidator
//                                               L95/L96  valServiceFee2    GreaterThanEqual 0
//                                               L113/114 valBillingPeriod2 GreaterThan 0
//                                               L127/128 valTrialFee2      GreaterThanEqual 0
//                                               L145/146 valTrialPeriod2   GreaterThan 0
//   Website/admin/Security/EditGroups.ascx       L11      txtRoleGroupName  maxlength 50
//                                                L12      valRoleGroupName  requiredfieldvalidator
//                                                L17      txtDescription    maxlength 1000, NO validator
//   Website/admin/Security/securityroles.ascx    L45      valEffectiveDate  DataTypeCheck Date
//                                                L46      valExpiryDate     DataTypeCheck Date
//                                                L47      valDates          GreaterThan, expiry vs effective
//
// MIGRATION: every message is quoted character for character rather than matched by substring. A
// substring match cannot tell "You Must Enter a Valid Name" apart from "<br>You Must Enter a Valid
// Name", and stripping that leading markup tag is itself one of the recorded divergences.
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule parity for the three role-family write contracts whose validators the code review
/// found absent, and proves that the shared rule definition leaves no rule reachable on one write verb
/// and not the other.
/// </summary>
/// <remarks>
/// <para>
/// Both halves of every failure are asserted, never just the fact of failure. The property name becomes
/// the key and the message the value in the <c>errors</c> dictionary of the RFC 7807 payload a client
/// consumes, so a test that checked only <see cref="ValidationResult.IsValid"/> would prove the rule
/// fires without proving it reports what the legacy screen reported.
/// </para>
/// <para>
/// Each acceptance baseline is asserted before the refusals that vary from it, so a later failure can be
/// attributed to the one member the test altered rather than to an already-invalid starting point.
/// </para>
/// </remarks>
public class RoleWriteContractValidatorTests
{
    /// <summary>Wording of <c>valRoleName</c> and <c>valRoleGroupName</c>, both markup tags removed.</summary>
    private const string NameRequired = "You Must Enter a Valid Name";

    /// <summary>Wording of <c>valServiceFee2</c>. Message and operator agree.</summary>
    private const string ServiceFeeNegative = "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>Wording of <c>valBillingPeriod2</c>, whose operator is strictly greater than zero.</summary>
    private const string BillingPeriodNotPositive =
        "Billing Period Must Be Greater Than or Equal to Zero";

    /// <summary>Wording of <c>valTrialFee2</c>, whose operator admits zero.</summary>
    private const string TrialFeeNegative = "Trial Fee Must Be Greater Than Zero";

    /// <summary>Wording of <c>valTrialPeriod2</c>. Message and operator agree.</summary>
    private const string TrialPeriodNotPositive = "Trial Period Must Be Greater Than Zero";

    /// <summary>Message reported for an icon path that is rooted or traverses upwards.</summary>
    private const string IconFileNotRelative =
        "Icon File must be a relative path within the portal's own folder.";

    /// <summary>Wording of <c>valDates</c>, its markup tag removed.</summary>
    private const string ExpiryNotAfterEffective =
        "Expiry Date must be Greater than Effective Date";

    /// <summary>Message reported when no user is named on an assignment.</summary>
    private const string UserIdNotPositive = "A user must be selected.";

    /// <summary>Width of <c>Roles.Description</c> and of <c>RoleGroups.Description</c> alike.</summary>
    private const int DescriptionWidth = 1000;

    /// <summary>Width of <c>Roles.RSVPCode</c>.</summary>
    private const int RsvpCodeWidth = 50;

    /// <summary>Width of <c>Roles.IconFile</c>.</summary>
    private const int IconFileWidth = 100;

    /// <summary>Width of <c>RoleGroups.RoleGroupName</c>.</summary>
    private const int RoleGroupNameWidth = 50;

    // ------------------------------------------------------------------------
    // UPDATE ROLE - ACCEPTANCE BASELINE
    // ------------------------------------------------------------------------

    /// <summary>
    /// An entirely empty update is accepted, because the legacy edit screen declared no presence check
    /// that survived translation and the terminal procedure defaults every value parameter to null.
    /// </summary>
    /// <remarks>
    /// This baseline is load-bearing rather than incidental. The contract is a full replacement, so
    /// omitting a nullable member CLEARS the stored value; a presence rule on any member would make the
    /// clearing operation unreachable. The default-valued object is exactly what a caller sends to clear
    /// every optional term at once.
    /// </remarks>
    [Fact]
    public void UpdateRole_AcceptsAnEntirelyEmptyRequest()
        => ShouldAccept(new UpdateRoleRequestValidator().Validate(new UpdateRoleRequest()));

    /// <summary>
    /// A fully populated, legitimate update is accepted, so every later refusal is attributable to the
    /// one member that test alters.
    /// </summary>
    [Fact]
    public void UpdateRole_AcceptsAFullyPopulatedRequest()
        => ShouldAccept(new UpdateRoleRequestValidator().Validate(ValidUpdate()));

    // ------------------------------------------------------------------------
    // UPDATE ROLE - THE RULE THAT WAS MISSING
    // ------------------------------------------------------------------------

    /// <summary>
    /// An icon path that is rooted, drive-qualified, scheme-qualified or traverses upwards is refused on
    /// the update verb, which is the defect this validator was written to close.
    /// </summary>
    /// <param name="iconFile">The path the caller submitted.</param>
    /// <remarks>
    /// Both separators are tested regardless of the host platform, because the legacy application stored
    /// Windows-style separators while the migrated API runs on Linux, so both have to be treated as
    /// rooting characters. The traversal cases cover a leading, an embedded and a trailing parent
    /// segment, because the rule is a containment rule and not a prefix rule.
    /// </remarks>
    [Theory]
    [InlineData("/images/role.gif")]
    [InlineData("\\images\\role.gif")]
    [InlineData("C:\\images\\role.gif")]
    [InlineData("http://elsewhere.example/role.gif")]
    [InlineData("../role.gif")]
    [InlineData("images/../../role.gif")]
    [InlineData("images/role.gif/..")]
    public void UpdateRole_RefusesAnIconPathThatEscapesThePortalFolder(string iconFile)
    {
        UpdateRoleRequest request = ValidUpdate();
        request.IconFile = iconFile;

        ShouldReport(
            new UpdateRoleRequestValidator().Validate(request),
            nameof(UpdateRoleRequest.IconFile),
            IconFileNotRelative);
    }

    /// <summary>
    /// Every icon form the legacy picker could have produced is still accepted, so the containment rule
    /// closes the gap without refusing legitimate input.
    /// </summary>
    /// <param name="iconFile">The path the caller submitted.</param>
    /// <remarks>
    /// The empty string is included deliberately: the legacy screen stored it when no icon had been
    /// picked, so refusing it would refuse the commonest case of all. A single full stop is included
    /// because the rule refuses a parent segment, not any occurrence of the character.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("role.gif")]
    [InlineData("images/role.gif")]
    [InlineData("images\\role.gif")]
    [InlineData("icon.v2.gif")]
    public void UpdateRole_AcceptsEveryContainedIconPath(string? iconFile)
    {
        UpdateRoleRequest request = ValidUpdate();
        request.IconFile = iconFile;

        ShouldNotReport(
            new UpdateRoleRequestValidator().Validate(request),
            nameof(UpdateRoleRequest.IconFile));
    }

    /// <summary>
    /// The creation and the update verb reach exactly the same verdict on every icon path, which is the
    /// property whose absence was the defect.
    /// </summary>
    /// <param name="iconFile">The path submitted to both validators.</param>
    /// <remarks>
    /// This asserts the invariant directly rather than inferring it from two suites that happen to agree.
    /// A future revision that re-declared the rule privately on one of the two validators would leave
    /// every other test in both files passing and would fail only here.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("role.gif")]
    [InlineData("images/role.gif")]
    [InlineData("/images/role.gif")]
    [InlineData("\\images\\role.gif")]
    [InlineData("C:\\images\\role.gif")]
    [InlineData("http://elsewhere.example/role.gif")]
    [InlineData("../role.gif")]
    [InlineData("images/../../role.gif")]
    public void IconPathVerdict_IsIdenticalOnBothWriteVerbs(string? iconFile)
    {
        CreateRoleRequest creation = new() { RoleName = "Subscribers", IconFile = iconFile };
        UpdateRoleRequest update = new() { IconFile = iconFile };

        bool refusedOnCreate = new CreateRoleRequestValidator().Validate(creation).Errors
            .Any(failure => failure.PropertyName == nameof(CreateRoleRequest.IconFile));
        bool refusedOnUpdate = new UpdateRoleRequestValidator().Validate(update).Errors
            .Any(failure => failure.PropertyName == nameof(UpdateRoleRequest.IconFile));

        refusedOnUpdate.Should().Be(
            refusedOnCreate,
            "a rule reachable on one write verb and not the other could be bypassed by choosing the "
            + "other verb, and the icon path \"{0}\" is exactly the value that used to be treated "
            + "differently",
            iconFile ?? "<null>");
    }

    // ------------------------------------------------------------------------
    // UPDATE ROLE - FEES, PERIODS, FREQUENCIES AND WIDTHS
    // ------------------------------------------------------------------------

    /// <summary>
    /// A negative service fee is refused with the legacy wording, while zero - a free role - is accepted.
    /// </summary>
    [Fact]
    public void UpdateRole_RefusesANegativeServiceFeeAndAcceptsAFreeOne()
    {
        UpdateRoleRequest negative = ValidUpdate();
        negative.ServiceFee = -0.01m;
        ShouldReport(
            new UpdateRoleRequestValidator().Validate(negative),
            nameof(UpdateRoleRequest.ServiceFee),
            ServiceFeeNegative);

        UpdateRoleRequest free = ValidUpdate();
        free.ServiceFee = 0m;
        ShouldNotReport(
            new UpdateRoleRequestValidator().Validate(free),
            nameof(UpdateRoleRequest.ServiceFee));
    }

    /// <summary>
    /// A negative trial fee is refused, while zero - a free trial - is accepted, and the message
    /// deliberately keeps the legacy wording that disagrees with its own operator.
    /// </summary>
    [Fact]
    public void UpdateRole_RefusesANegativeTrialFeeAndAcceptsAFreeTrial()
    {
        UpdateRoleRequest negative = ValidUpdate();
        negative.TrialFee = -1m;
        ShouldReport(
            new UpdateRoleRequestValidator().Validate(negative),
            nameof(UpdateRoleRequest.TrialFee),
            TrialFeeNegative);

        UpdateRoleRequest free = ValidUpdate();
        free.TrialFee = 0m;
        ShouldNotReport(
            new UpdateRoleRequestValidator().Validate(free),
            nameof(UpdateRoleRequest.TrialFee));
    }

    /// <summary>
    /// A billing period of zero or less is refused, because a cycle of zero units could never advance an
    /// expiry date, and the message keeps the legacy wording that disagrees with that operator.
    /// </summary>
    /// <param name="billingPeriod">The period the caller submitted.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UpdateRole_RefusesANonPositiveBillingPeriod(int billingPeriod)
    {
        UpdateRoleRequest request = ValidUpdate();
        request.BillingPeriod = billingPeriod;

        ShouldReport(
            new UpdateRoleRequestValidator().Validate(request),
            nameof(UpdateRoleRequest.BillingPeriod),
            BillingPeriodNotPositive);
    }

    /// <summary>A trial period of zero or less is refused, and here message and operator agree.</summary>
    /// <param name="trialPeriod">The period the caller submitted.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UpdateRole_RefusesANonPositiveTrialPeriod(int trialPeriod)
    {
        UpdateRoleRequest request = ValidUpdate();
        request.TrialPeriod = trialPeriod;

        ShouldReport(
            new UpdateRoleRequestValidator().Validate(request),
            nameof(UpdateRoleRequest.TrialPeriod),
            TrialPeriodNotPositive);
    }

    /// <summary>
    /// A frequency outside the domain enumeration is refused on both frequency members.
    /// </summary>
    /// <remarks>
    /// The refused value is cast from an integer that is not a member, which is the only way to express
    /// the case: the enumeration members carry the code points of the stored characters, so a literal
    /// character list would be a different rule. This is also the write-path half of a deliberate
    /// asymmetry - the shipped seed data holds two codes the enumeration cannot represent, and tolerance
    /// for those rows lives on the read path alone.
    /// </remarks>
    [Fact]
    public void UpdateRole_RefusesAFrequencyOutsideTheEnumeration()
    {
        UpdateRoleRequest billing = ValidUpdate();
        billing.BillingFrequency = (BillingFrequency)('4');
        new UpdateRoleRequestValidator().Validate(billing).Errors
            .Should().Contain(failure => failure.PropertyName == nameof(UpdateRoleRequest.BillingFrequency));

        UpdateRoleRequest trial = ValidUpdate();
        trial.TrialFrequency = (BillingFrequency)('0');
        new UpdateRoleRequestValidator().Validate(trial).Errors
            .Should().Contain(failure => failure.PropertyName == nameof(UpdateRoleRequest.TrialFrequency));
    }

    /// <summary>Every column width the contract carries is enforced, and the boundary value is accepted.</summary>
    /// <remarks>
    /// The exactly-at-the-limit case is asserted alongside the over-limit case, because an off-by-one
    /// width would refuse a value the column can hold and no over-limit test alone would notice.
    /// </remarks>
    [Fact]
    public void UpdateRole_EnforcesEveryColumnWidth()
    {
        UpdateRoleRequestValidator validator = new();

        UpdateRoleRequest atLimit = ValidUpdate();
        atLimit.Description = new string('d', DescriptionWidth);
        atLimit.RsvpCode = new string('r', RsvpCodeWidth);
        atLimit.IconFile = new string('i', IconFileWidth);
        ShouldAccept(validator.Validate(atLimit));

        UpdateRoleRequest overLimit = ValidUpdate();
        overLimit.Description = new string('d', DescriptionWidth + 1);
        overLimit.RsvpCode = new string('r', RsvpCodeWidth + 1);
        overLimit.IconFile = new string('i', IconFileWidth + 1);

        ValidationResult result = validator.Validate(overLimit);
        result.IsValid.Should().BeFalse(Render(result));
        result.Errors.Select(failure => failure.PropertyName).Should().BeEquivalentTo(
            new[]
            {
                nameof(UpdateRoleRequest.Description),
                nameof(UpdateRoleRequest.RsvpCode),
                nameof(UpdateRoleRequest.IconFile),
            },
            "class-level cascade continues, so one submission reports every over-long member at once");
    }

    /// <summary>
    /// A role-group reference of zero and one that is absent are both accepted, because the referenced
    /// key is seeded from zero and absence means "ungrouped".
    /// </summary>
    /// <param name="roleGroupId">The group reference the caller submitted.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(7)]
    public void UpdateRole_AcceptsEveryLegitimateRoleGroupReference(int? roleGroupId)
    {
        UpdateRoleRequest request = ValidUpdate();
        request.RoleGroupId = roleGroupId;

        ShouldNotReport(
            new UpdateRoleRequestValidator().Validate(request),
            nameof(UpdateRoleRequest.RoleGroupId));
    }

    // ------------------------------------------------------------------------
    // ROLE GROUP
    // ------------------------------------------------------------------------

    /// <summary>A legitimate role group is accepted with both members populated.</summary>
    [Fact]
    public void RoleGroup_AcceptsALegitimateSubmission()
        => ShouldAccept(new RoleGroupDtoValidator().Validate(ValidRoleGroup()));

    /// <summary>
    /// An absent, empty or whitespace-only group name is refused with the legacy wording of
    /// <c>valRoleGroupName</c>.
    /// </summary>
    /// <param name="roleGroupName">The name the caller submitted.</param>
    /// <remarks>
    /// The null case is what used to reach the store: <c>RoleGroups.RoleGroupName</c> is <c>NOT NULL</c>,
    /// so before this validator existed an absent name produced a persistence fault rather than the
    /// declared field error.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RoleGroup_RefusesAnAbsentName(string? roleGroupName)
    {
        RoleGroupDto group = ValidRoleGroup();
        group.RoleGroupName = roleGroupName!;

        ShouldReport(
            new RoleGroupDtoValidator().Validate(group),
            nameof(RoleGroupDto.RoleGroupName),
            NameRequired);
    }

    /// <summary>Both role-group widths are enforced, and both boundary values are accepted.</summary>
    [Fact]
    public void RoleGroup_EnforcesBothColumnWidths()
    {
        RoleGroupDtoValidator validator = new();

        RoleGroupDto atLimit = ValidRoleGroup();
        atLimit.RoleGroupName = new string('n', RoleGroupNameWidth);
        atLimit.Description = new string('d', DescriptionWidth);
        ShouldAccept(validator.Validate(atLimit));

        RoleGroupDto overLimit = ValidRoleGroup();
        overLimit.RoleGroupName = new string('n', RoleGroupNameWidth + 1);
        overLimit.Description = new string('d', DescriptionWidth + 1);

        ValidationResult result = validator.Validate(overLimit);
        result.Errors.Select(failure => failure.PropertyName).Should().BeEquivalentTo(
            new[] { nameof(RoleGroupDto.RoleGroupName), nameof(RoleGroupDto.Description) },
            Render(result));
    }

    /// <summary>
    /// An absent description is accepted, because the legacy screen declared no validator on that box.
    /// </summary>
    /// <param name="description">The description the caller submitted.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RoleGroup_AcceptsAnAbsentDescription(string? description)
    {
        RoleGroupDto group = ValidRoleGroup();
        group.Description = description;

        ShouldAccept(new RoleGroupDtoValidator().Validate(group));
    }

    /// <summary>
    /// Neither identifier is bounded, so the first row ever created and the first portal ever created are
    /// both addressable.
    /// </summary>
    /// <param name="roleGroupId">The group identifier the caller submitted.</param>
    /// <param name="portalId">The portal identifier the caller submitted.</param>
    /// <remarks>
    /// <c>RoleGroups.RoleGroupID</c> is seeded <c>IDENTITY(0,1)</c> and <c>Portals.PortalID</c> is seeded
    /// <c>IDENTITY(-1,1)</c>, so a "greater than zero" bound on either would refuse a legitimate row and a
    /// "non-positive means absent" test would silently exclude one. Both members are route-authoritative
    /// in any case, which is why nothing here asserts a range.
    /// </remarks>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    [InlineData(12, 3)]
    public void RoleGroup_BoundsNeitherIdentifier(int roleGroupId, int portalId)
    {
        RoleGroupDto group = ValidRoleGroup();
        group.RoleGroupId = roleGroupId;
        group.PortalId = portalId;

        ShouldAccept(new RoleGroupDtoValidator().Validate(group));
    }

    // ------------------------------------------------------------------------
    // ROLE ASSIGNMENT
    // ------------------------------------------------------------------------

    /// <summary>A legitimate assignment with a forward-ordered window is accepted.</summary>
    [Fact]
    public void RoleAssignment_AcceptsALegitimateSubmission()
        => ShouldAccept(new RoleAssignmentRequestValidator().Validate(ValidAssignment()));

    /// <summary>
    /// An assignment naming no usable user is refused, which covers both the omitted member and the
    /// legacy "nothing selected" sentinel.
    /// </summary>
    /// <param name="userId">The user reference the caller submitted.</param>
    /// <remarks>
    /// <c>dbo.Users.UserID</c> is seeded <c>IDENTITY(1,1)</c>, so the first account ever created is 1 and
    /// nothing at or below zero can name an account. Before this validator existed, zero reached the store
    /// and failed the foreign key on <c>dbo.UserRoles</c> instead of being reported against the member.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RoleAssignment_RefusesAnUnusableUserReference(int userId)
    {
        RoleAssignmentRequest request = ValidAssignment();
        request.UserId = userId;

        ShouldReport(
            new RoleAssignmentRequestValidator().Validate(request),
            nameof(RoleAssignmentRequest.UserId),
            UserIdNotPositive);
    }

    /// <summary>
    /// An expiry that is not strictly later than the effective date is refused with the legacy wording of
    /// <c>valDates</c>, equality included.
    /// </summary>
    /// <param name="expiryOffsetDays">Days to add to the effective date to obtain the expiry.</param>
    /// <remarks>
    /// Equality is refused because the legacy comparison was <c>GreaterThan</c> and not
    /// <c>GreaterThanEqual</c>: a window that opens and closes at the same instant is a membership that is
    /// never in force. The failure is reported against the expiry, which is the field the legacy screen
    /// highlighted and therefore the one an operator would correct.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RoleAssignment_RefusesAnExpiryNotAfterItsEffectiveDate(int expiryOffsetDays)
    {
        RoleAssignmentRequest request = ValidAssignment();
        request.EffectiveDate = new DateTime(2008, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        request.ExpiryDate = request.EffectiveDate.Value.AddDays(expiryOffsetDays);

        ShouldReport(
            new RoleAssignmentRequestValidator().Validate(request),
            nameof(RoleAssignmentRequest.ExpiryDate),
            ExpiryNotAfterEffective);
    }

    /// <summary>
    /// An open-ended window in either direction is accepted, because a compare validator treats an empty
    /// control as valid and neither date box carried a presence check.
    /// </summary>
    /// <param name="hasEffective">Whether an effective date was supplied.</param>
    /// <param name="hasExpiry">Whether an expiry was supplied.</param>
    /// <remarks>
    /// All three open shapes are asserted, including the wholly open one. A null expiry additionally
    /// instructs the service to derive one from the role's own terms, so refusing it would remove a
    /// workflow rather than tighten a rule.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RoleAssignment_AcceptsAnOpenEndedWindow(bool hasEffective, bool hasExpiry)
    {
        RoleAssignmentRequest request = ValidAssignment();
        request.EffectiveDate = hasEffective
            ? new DateTime(2008, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            : null;
        request.ExpiryDate = hasExpiry
            ? new DateTime(2008, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            : null;

        ShouldAccept(new RoleAssignmentRequestValidator().Validate(request));
    }

    /// <summary>
    /// A backdated window is accepted, because the legacy screen let an operator type any date and the
    /// membership predicate reads an elapsed effective date as "already in force".
    /// </summary>
    [Fact]
    public void RoleAssignment_AcceptsABackdatedWindow()
    {
        RoleAssignmentRequest request = ValidAssignment();
        request.EffectiveDate = new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        request.ExpiryDate = new DateTime(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        ShouldAccept(new RoleAssignmentRequestValidator().Validate(request));
    }

    /// <summary>
    /// The literal far-future expiry the legacy "one time" code stored is accepted verbatim and is not
    /// mistaken for an absence marker.
    /// </summary>
    /// <remarks>
    /// The legacy expiry switch assigned <c>9999-12-31</c> for that code, and the legacy null test compared
    /// only against the minimum date, so the far-future value read as present. A rule that refused or
    /// normalised it would change what a legacy consumer reading the same row sees.
    /// </remarks>
    [Fact]
    public void RoleAssignment_AcceptsTheLiteralFarFutureExpiry()
    {
        RoleAssignmentRequest request = ValidAssignment();
        request.EffectiveDate = null;
        request.ExpiryDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        ShouldAccept(new RoleAssignmentRequestValidator().Validate(request));
    }

    /// <summary>
    /// The notification flag is unconstrained in both states, because a boolean is its own constraint and
    /// no column backs the member.
    /// </summary>
    /// <param name="notifyUser">The flag the caller submitted.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoleAssignment_ConstrainsTheNotificationFlagInNeitherState(bool notifyUser)
    {
        RoleAssignmentRequest request = ValidAssignment();
        request.NotifyUser = notifyUser;

        ShouldAccept(new RoleAssignmentRequestValidator().Validate(request));
    }

    // ------------------------------------------------------------------------
    // PROFILE PROPERTY DEFINITION
    // ------------------------------------------------------------------------

    /// <summary>A legitimate profile property definition is accepted.</summary>
    [Fact]
    public void ProfileDefinition_AcceptsALegitimateSubmission()
        => ShouldAccept(new ProfilePropertyDefinitionDtoValidator().Validate(ValidDefinition()));

    /// <summary>
    /// The two mandatory text members are refused when absent, empty or whitespace-only.
    /// </summary>
    /// <param name="value">The value submitted for both members.</param>
    /// <remarks>
    /// Both columns are <c>NOT NULL</c>, so before this validator existed an absent value produced a
    /// persistence fault. Both legacy members carry <c>Required(True)</c>, and unlike the two mandatory
    /// integers that rule genuinely bites on a text control.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProfileDefinition_RefusesAnAbsentCategoryOrName(string? value)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyCategory = value!;
        definition.PropertyName = value!;

        ValidationResult result = new ProfilePropertyDefinitionDtoValidator().Validate(definition);

        result.Errors.Select(failure => failure.PropertyName).Should().Contain(
            new[]
            {
                nameof(ProfilePropertyDefinitionDto.PropertyCategory),
                nameof(ProfilePropertyDefinitionDto.PropertyName),
            },
            Render(result));
    }

    /// <summary>
    /// A property name containing a character the legacy pattern forbids is refused, and every permitted
    /// character is accepted.
    /// </summary>
    /// <param name="propertyName">The name the caller submitted.</param>
    /// <param name="permitted">Whether the legacy pattern admits it.</param>
    /// <remarks>
    /// The space case is the one most likely to be "corrected" later and is asserted deliberately: the
    /// property name is an identifier used as a resource key, and the legacy screen carried a separate
    /// localisation step for the human-readable label. The pattern is anchored at both ends, so a name
    /// that merely contains a permitted run is still refused.
    /// </remarks>
    [Theory]
    [InlineData("City", true)]
    [InlineData("Preferred.Name", true)]
    [InlineData("Preferred_Name", true)]
    [InlineData("Discount%", true)]
    [InlineData("Mid-Name", true)]
    [InlineData("A+B", true)]
    [InlineData("O'Brien", true)]
    [InlineData("Prop123", true)]
    [InlineData("Preferred Name", false)]
    [InlineData("Name!", false)]
    [InlineData("Na/me", false)]
    [InlineData("Na\\me", false)]
    [InlineData("<script>", false)]
    public void ProfileDefinition_AppliesTheLegacyNamePatternExactly(string propertyName, bool permitted)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyName = propertyName;

        ValidationResult result = new ProfilePropertyDefinitionDtoValidator().Validate(definition);

        result.Errors.Any(failure => failure.PropertyName == nameof(ProfilePropertyDefinitionDto.PropertyName))
            .Should().Be(!permitted, Render(result));
    }

    /// <summary>
    /// The four numeric members of a profile definition carry NO bound, so a value outside every documented
    /// meaning survives a round trip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this replaces four theories that asserted the opposite - a 0-to-2 range on the visibility
    /// hint, a non-negative data-type key, a non-negative length and a view order no lower than the append
    /// instruction. Every one of those is a BUSINESS rule the legacy did not have, and AAP 0.9.1 permits
    /// bounds to be added for representability or for security but not domain rules to be invented. The
    /// measured position is recorded per member in <c>ProfilePropertyDefinitionDtoValidatorTests</c>, which is
    /// the parity proof for this validator and cites the legacy declaration behind each absence: the editor
    /// screen declares no validators at all, the authoritative attributes on
    /// <c>ProfilePropertyDefinition.vb</c> mark exactly three members required, and none of these four is
    /// among them.
    /// </para>
    /// <para>
    /// The concrete harm the withdrawn rules would have caused is why the position is asserted here as well
    /// as there. The visibility hint was assigned by converting a module setting without any check, and is
    /// not persisted on this table, so a caller that read a definition and sent it back UNCHANGED would have
    /// been refused by a rule this migration introduced. The data-type key names a row in the excluded lookup
    /// subsystem, so no endpoint can offer a valid one and a positive bound would make both write verbs
    /// unusable rather than safer. A length of zero legitimately means unbounded. And minus one on the view
    /// order is an INSTRUCTION rather than an absence marker - the terminal upsert branches on it to append -
    /// so a lower bound would have removed the append affordance outright.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProfileDefinition_BoundsItsFourNumericMembersInNoWay()
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.Visibility = 7;
        definition.DataType = -1;
        definition.Length = -1;
        definition.ViewOrder = -1;

        ShouldAccept(new ProfilePropertyDefinitionDtoValidator().Validate(definition));
    }


    /// <summary>
    /// The definition widths are the terminal ones, so the widened validation expression is accepted well
    /// beyond the length the legacy update procedure still declares.
    /// </summary>
    /// <remarks>
    /// The terminal update procedure declares the expression parameter at a hundred characters while the
    /// column holds two thousand - a real legacy truncation defect. This migration writes through the ORM,
    /// so the column's width governs, and a value of a thousand characters proves it.
    /// </remarks>
    [Fact]
    public void ProfileDefinition_EnforcesTheTerminalWidthsRatherThanTheProcedureParameters()
    {
        ProfilePropertyDefinitionDtoValidator validator = new();

        ProfilePropertyDefinitionDto widened = ValidDefinition();
        widened.ValidationExpression = new string('x', 1000);
        ShouldAccept(validator.Validate(widened));

        ProfilePropertyDefinitionDto atLimit = ValidDefinition();
        atLimit.ValidationExpression = new string('x', 2000);
        ShouldAccept(validator.Validate(atLimit));

        ProfilePropertyDefinitionDto overLimit = ValidDefinition();
        overLimit.ValidationExpression = new string('x', 2001);
        // The message is the AUTHORED one, not the framework default. Every other rule on this validator
        // reports wording carried over from the legacy screen, and reporting one member in the framework's
        // voice - which also quotes the submitted length back - would make the response inconsistent with
        // its siblings for no gain.
        ShouldReport(
            validator.Validate(overLimit),
            nameof(ProfilePropertyDefinitionDto.ValidationExpression),
            "Validation Expression must be 2000 characters or fewer");
    }

    /// <summary>
    /// A default value of unbounded length is accepted, because the terminal column carries no width.
    /// </summary>
    [Fact]
    public void ProfileDefinition_BoundsTheDefaultValueInNoWay()
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.DefaultValue = new string('v', 5000);

        ShouldAccept(new ProfilePropertyDefinitionDtoValidator().Validate(definition));
    }


    // ------------------------------------------------------------------------
    // BUILDERS
    // ------------------------------------------------------------------------

    /// <summary>Builds an update whose every member is legitimate.</summary>
    /// <returns>A valid update request.</returns>
    private static UpdateRoleRequest ValidUpdate() => new()
    {
        Description = "Paying subscribers",
        RoleGroupId = 3,
        IsPublic = true,
        AutoAssignment = false,
        ServiceFee = 9.99m,
        BillingPeriod = 1,
        BillingFrequency = BillingFrequency.Month,
        TrialFee = 0m,
        TrialPeriod = 14,
        TrialFrequency = BillingFrequency.Day,
        RsvpCode = "JOIN2008",
        IconFile = "images/subscriber.gif",
    };

    /// <summary>Builds a role group whose every member is legitimate.</summary>
    /// <returns>A valid role group.</returns>
    private static RoleGroupDto ValidRoleGroup() => new()
    {
        RoleGroupId = 4,
        PortalId = 0,
        RoleGroupName = "Subscription Roles",
        Description = "Roles that carry a subscription",
    };

    /// <summary>Builds an assignment whose every member is legitimate.</summary>
    /// <returns>A valid assignment request.</returns>
    private static RoleAssignmentRequest ValidAssignment() => new()
    {
        UserId = 3,
        EffectiveDate = new DateTime(2008, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        ExpiryDate = new DateTime(2009, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        NotifyUser = true,
    };

    /// <summary>Builds a profile property definition whose every member is legitimate.</summary>
    /// <returns>A valid definition.</returns>
    private static ProfilePropertyDefinitionDto ValidDefinition() => new()
    {
        PropertyDefinitionId = 11,
        PortalId = 0,
        ModuleDefId = null,
        DataType = 349,
        DefaultValue = string.Empty,
        PropertyCategory = "Address",
        PropertyName = "City",
        Length = 50,
        Required = false,
        ValidationExpression = null,
        ViewOrder = 15,
        Visible = true,
        Visibility = 2,
    };

    // ------------------------------------------------------------------------
    // ASSERTION HELPERS
    // ------------------------------------------------------------------------

    /// <summary>
    /// Asserts that a result reports the given message against the given property, character for
    /// character.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property the failure must name.</param>
    /// <param name="message">The message the failure must carry.</param>
    private static void ShouldReport(ValidationResult result, string property, string message)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property);
        result.Errors.Should().Contain(
            failure => failure.PropertyName == property && failure.ErrorMessage == message,
            "the failure for {0} must carry exactly the expected wording, but the result was {1}",
            property,
            Render(result));
    }

    /// <summary>Asserts that a result reports nothing at all against the given property.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property that must be unreported.</param>
    private static void ShouldNotReport(ValidationResult result, string property)
        => result.Errors.Should().NotContain(
            failure => failure.PropertyName == property,
            "no failure was expected for {0}, but the result was {1}",
            property,
            Render(result));

    /// <summary>Asserts that a result reports nothing whatsoever.</summary>
    /// <param name="result">The result to inspect.</param>
    private static void ShouldAccept(ValidationResult result)
    {
        result.IsValid.Should().BeTrue(Render(result));
        result.Errors.Should().BeEmpty(Render(result));
    }

    /// <summary>Renders a result for an assertion message.</summary>
    /// <param name="result">The result to render.</param>
    /// <returns>A readable rendering of every failure the result carries.</returns>
    private static string Render(ValidationResult result) => result.Errors.Count == 0
        ? "the result reported nothing"
        : "the result reported "
            + string.Join(
                " | ",
                result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));
}
