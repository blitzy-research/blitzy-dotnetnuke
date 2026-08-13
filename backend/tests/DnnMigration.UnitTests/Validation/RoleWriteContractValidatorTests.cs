// MIGRATION: the role-group half of this suite originally exercised ONE validator over RoleGroupDto,
// because both write verbs bound that response projection and so advertised a group identifier and an
// owning portal that neither AddRoleGroup nor UpdateRoleGroup writes.
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule parity for the three role-family write contracts, and proves that the shared rule
/// definition leaves no rule reachable on one write verb and not the other.
/// </summary>
/// <remarks>
/// Each acceptance baseline is asserted before the refusals that vary from it, so a later failure can be
/// attributed to the one member the test altered rather than to an already-invalid starting point.
/// </remarks>
public class RoleWriteContractValidatorTests
{
    /// <summary>Wording of <c>valRoleName</c> and <c>valRoleGroupName</c>, both markup tags removed.</summary>
    private const string NameRequired = "You Must Enter a Valid Name";

    /// <summary>Wording of <c>valServiceFee2</c>. Message and operator agree.</summary>
    private const string ServiceFeeNegative = "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>Wording of <c>valBillingPeriod2</c>, whose operator is strictly greater than zero.</summary>
    private const string BillingPeriodNotPositive =
        "Billing Period Must Be Greater Than Zero";

    /// <summary>Wording of <c>valTrialFee2</c>, whose operator admits zero.</summary>
    private const string TrialFeeNegative = "Trial Fee Must Be Greater Than or Equal to Zero";

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

    /// <summary>
    /// Width of <c>Roles.RoleName nvarchar(50) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> L117), which
    /// both role write verbs enforce.
    /// </summary>
    private const int RoleNameWidth = 50;

    // ------------------------------------------------------------------------
    // UPDATE ROLE - ACCEPTANCE BASELINE
    // ------------------------------------------------------------------------

    /// <summary>
    /// An update carrying nothing but the required name is accepted, because the legacy edit screen
    /// declared no other presence check and the terminal procedure defaults every value parameter to null.
    /// </summary>
    /// <remarks>
    /// This baseline is load-bearing rather than incidental. The contract is a full replacement, so
    /// omitting a nullable member CLEARS the stored value; a presence rule on any member BESIDES the name
    /// would make the clearing operation unreachable.
    /// </remarks>
    [Fact]
    public void UpdateRole_AcceptsARequestCarryingNothingButItsName()
        => ShouldAccept(new UpdateRoleRequestValidator().Validate(
            new UpdateRoleRequest { RoleName = "Subscribers" }));

    /// <summary>
    /// An update carrying no name at all is refused, naming the member, with the legacy wording of
    /// <c>valRoleName</c>.
    /// </summary>
    /// <remarks>
    /// The update contract carries a writable name - a documented behavioural difference from the legacy
    /// edit screen, which displayed the name read-only and disabled this very validator at
    /// <c>EditRoles.ascx.vb</c> L134 - so the one presence rule that screen declared applies to both write
    /// verbs. The column is <c>NOT NULL</c>, so there is no cleared state for an omitted name to mean.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateRole_RefusesAnAbsentName(string? roleName)
    {
        UpdateRoleRequest request = ValidUpdate();
        request.RoleName = roleName!;

        ShouldReport(
            new UpdateRoleRequestValidator().Validate(request),
            nameof(UpdateRoleRequest.RoleName),
            NameRequired);
    }

    /// <summary>
    /// The update verb bounds the name by the same column width the creation verb does, and accepts the
    /// boundary value.
    /// </summary>
    [Fact]
    public void UpdateRole_BoundsTheNameByItsColumnWidth()
    {
        UpdateRoleRequest atLimit = ValidUpdate();
        atLimit.RoleName = new string('n', RoleNameWidth);
        ShouldAccept(new UpdateRoleRequestValidator().Validate(atLimit));

        UpdateRoleRequest overLimit = ValidUpdate();
        overLimit.RoleName = new string('n', RoleNameWidth + 1);
        ShouldNotAccept(
            new UpdateRoleRequestValidator().Validate(overLimit),
            nameof(UpdateRoleRequest.RoleName));
    }

    /// <summary>
    /// A fully populated, legitimate update is accepted, so every later refusal is attributable to the one
    /// member that test alters.
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
    /// property a rule declared privately on one of the two verbs would break.
    /// </summary>
    /// <param name="iconFile">The path submitted to both validators.</param>
    /// <remarks>
    /// This asserts the invariant directly rather than inferring it from two suites that happen to agree. A
    /// future revision that re-declared the rule privately on one of the two validators would leave every
    /// other test in both files passing and would fail only here.
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
            + "other verb, and the icon path \"{0}\" is a value the two verbs must judge "
            + "alike",
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

    /// <summary>A frequency outside the domain enumeration is refused on both frequency members.</summary>
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
    /// The exactly-at-the-limit case is asserted alongside the over-limit case, because an off-by-one width
    /// would refuse a value the column can hold and no over-limit test alone would notice.
    /// </remarks>
    [Fact]
    public void UpdateRole_EnforcesEveryColumnWidth()
    {
        UpdateRoleRequestValidator validator = new();

        UpdateRoleRequest atLimit = ValidUpdate();
        atLimit.Description = new string('d', DescriptionWidth);
        atLimit.RsvpCode = StrongCodeOfLength(RsvpCodeWidth);
        atLimit.IconFile = new string('i', IconFileWidth);
        ShouldAccept(validator.Validate(atLimit));

        UpdateRoleRequest overLimit = ValidUpdate();
        overLimit.Description = new string('d', DescriptionWidth + 1);
        overLimit.RsvpCode = StrongCodeOfLength(RsvpCodeWidth + 1);
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
    /// A role-group reference of zero and one that is absent are both accepted, because the referenced key
    /// is seeded from zero and absence means "ungrouped".
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

    /// <summary>A legitimate role group is accepted on both write verbs with both members populated.</summary>
    [Fact]
    public void RoleGroup_AcceptsALegitimateSubmissionOnBothVerbs()
    {
        ShouldAccept(new CreateRoleGroupRequestValidator().Validate(ValidRoleGroupCreate()));
        ShouldAccept(new UpdateRoleGroupRequestValidator().Validate(ValidRoleGroupUpdate()));
    }

    /// <summary>
    /// An absent, empty or whitespace-only group name is refused with the legacy wording of
    /// <c>valRoleGroupName</c>.
    /// </summary>
    /// <param name="roleGroupName">The name the caller submitted.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RoleGroup_RefusesAnAbsentName(string? roleGroupName)
    {
        CreateRoleGroupRequest create = ValidRoleGroupCreate();
        create.RoleGroupName = roleGroupName!;

        ShouldReport(
            new CreateRoleGroupRequestValidator().Validate(create),
            nameof(CreateRoleGroupRequest.RoleGroupName),
            NameRequired);

        // The verdict must be identical on the other verb, or the rule is one a caller could bypass by
        // choosing PUT over POST.
        UpdateRoleGroupRequest update = ValidRoleGroupUpdate();
        update.RoleGroupName = roleGroupName!;

        ShouldReport(
            new UpdateRoleGroupRequestValidator().Validate(update),
            nameof(UpdateRoleGroupRequest.RoleGroupName),
            NameRequired);
    }

    /// <summary>Both role-group widths are enforced, and both boundary values are accepted.</summary>
    [Fact]
    public void RoleGroup_EnforcesBothColumnWidths()
    {
        CreateRoleGroupRequestValidator createValidator = new();

        CreateRoleGroupRequest atLimit = ValidRoleGroupCreate();
        atLimit.RoleGroupName = new string('n', RoleGroupNameWidth);
        atLimit.Description = new string('d', DescriptionWidth);
        ShouldAccept(createValidator.Validate(atLimit));

        CreateRoleGroupRequest overLimit = ValidRoleGroupCreate();
        overLimit.RoleGroupName = new string('n', RoleGroupNameWidth + 1);
        overLimit.Description = new string('d', DescriptionWidth + 1);

        ValidationResult result = createValidator.Validate(overLimit);
        result.Errors.Select(failure => failure.PropertyName).Should().BeEquivalentTo(
            new[] { nameof(CreateRoleGroupRequest.RoleGroupName), nameof(CreateRoleGroupRequest.Description) },
            Render(result));

        // Both widths are enforced identically on the update verb, which writes the same two columns.
        UpdateRoleGroupRequest overLimitUpdate = ValidRoleGroupUpdate();
        overLimitUpdate.RoleGroupName = new string('n', RoleGroupNameWidth + 1);
        overLimitUpdate.Description = new string('d', DescriptionWidth + 1);

        ValidationResult updateResult = new UpdateRoleGroupRequestValidator().Validate(overLimitUpdate);
        updateResult.Errors.Select(failure => failure.PropertyName).Should().BeEquivalentTo(
            new[] { nameof(UpdateRoleGroupRequest.RoleGroupName), nameof(UpdateRoleGroupRequest.Description) },
            Render(updateResult));
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
        CreateRoleGroupRequest create = ValidRoleGroupCreate();
        create.Description = description;
        ShouldAccept(new CreateRoleGroupRequestValidator().Validate(create));

        UpdateRoleGroupRequest update = ValidRoleGroupUpdate();
        update.Description = description;
        ShouldAccept(new UpdateRoleGroupRequestValidator().Validate(update));
    }

    /// <summary>
    /// Neither write contract carries an identifier member at all, which is the reason no identifier bound
    /// needs asserting.
    /// </summary>
    /// <remarks>
    /// The bounds those withdrawn cases guarded against still matter and are still forbidden wherever an
    /// identifier does appear: <c>RoleGroups.RoleGroupID</c> is seeded <c>IDENTITY(0,1)</c> and
    /// <c>Portals.PortalID</c> is seeded <c>IDENTITY(-1,1)</c>, so a "greater than zero" bound on either
    /// would refuse a legitimate row and a "non-positive means absent" test would silently exclude one.
    /// </remarks>
    [Fact]
    public void RoleGroup_WriteContractsCarryNoIdentifierMemberAtAll()
    {
        string[] createMembers = typeof(CreateRoleGroupRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        string[] updateMembers = typeof(UpdateRoleGroupRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        createMembers.Should().BeEquivalentTo(new[] { "RoleGroupName", "Description" });
        updateMembers.Should().BeEquivalentTo(new[] { "RoleGroupName", "Description" });
    }

    // ------------------------------------------------------------------------
    // ROLE ASSIGNMENT
    // ------------------------------------------------------------------------

    /// <summary>A legitimate assignment with a forward-ordered window is accepted.</summary>
    [Fact]
    public void RoleAssignment_AcceptsALegitimateSubmission()
        => ShouldAccept(new RoleAssignmentRequestValidator().Validate(ValidAssignment()));

    /// <summary>
    /// An assignment naming no usable user is refused, which covers both the omitted member and the legacy
    /// "nothing selected" sentinel.
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
        => ShouldAccept(new CreateProfilePropertyDefinitionRequestValidator().Validate(ValidDefinition()));

    /// <summary>The two mandatory text members are refused when absent, empty or whitespace-only.</summary>
    /// <param name="value">The value submitted for both members.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProfileDefinition_RefusesAnAbsentCategoryOrName(string? value)
    {
        CreateProfilePropertyDefinitionRequest definition = ValidDefinition();
        definition.PropertyCategory = value!;
        definition.PropertyName = value!;

        ValidationResult result = new CreateProfilePropertyDefinitionRequestValidator().Validate(definition);

        result.Errors.Select(failure => failure.PropertyName).Should().Contain(
            new[]
            {
                nameof(CreateProfilePropertyDefinitionRequest.PropertyCategory),
                nameof(CreateProfilePropertyDefinitionRequest.PropertyName),
            },
            Render(result));
    }

    /// <summary>
    /// A property name containing a character the legacy pattern forbids is refused, and every permitted
    /// character is accepted.
    /// </summary>
    /// <param name="propertyName">The name the caller submitted.</param>
    /// <param name="permitted">Whether the legacy pattern admits it.</param>
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
        CreateProfilePropertyDefinitionRequest definition = ValidDefinition();
        definition.PropertyName = propertyName;

        ValidationResult result = new CreateProfilePropertyDefinitionRequestValidator().Validate(definition);

        result.Errors
            .Any(failure => failure.PropertyName == nameof(CreateProfilePropertyDefinitionRequest.PropertyName))
            .Should().Be(!permitted, Render(result));
    }

    /// <summary>
    /// The three numeric members of a profile definition carry NO bound, so a value outside every
    /// documented meaning survives a round trip.
    /// </summary>
    /// <remarks>
    /// The concrete harm the withdrawn rules would have caused is why the position is asserted here as well
    /// as there. The data-type key names a row in the excluded lookup subsystem, so no endpoint can offer a
    /// valid one and a positive bound would make both write verbs unusable rather than safer.
    /// </remarks>
    [Fact]
    public void ProfileDefinition_BoundsItsThreeNumericMembersInNoWay()
    {
        CreateProfilePropertyDefinitionRequest definition = ValidDefinition();
        definition.DataType = -1;
        definition.Length = -1;
        definition.ViewOrder = -1;

        ShouldAccept(new CreateProfilePropertyDefinitionRequestValidator().Validate(definition));
    }

    /// <summary>
    /// Definition text widths follow the terminal schema except for tenant-authored validation expressions,
    /// whose write boundary is intentionally narrower to cap regular-expression compilation and match work.
    /// </summary>
    /// <remarks>
    /// The terminal column remains two thousand characters for existing rows and schema compatibility,
    /// while new expressions are limited to 512 characters and evaluated with a bounded timeout.
    /// </remarks>
    [Fact]
    public void ProfileDefinition_EnforcesTheRegexWriteWorkFactorLimit()
    {
        CreateProfilePropertyDefinitionRequestValidator validator = new();

        CreateProfilePropertyDefinitionRequest widened = ValidDefinition();
        widened.ValidationExpression = new string('x', 511);
        ShouldAccept(validator.Validate(widened));

        CreateProfilePropertyDefinitionRequest atLimit = ValidDefinition();
        atLimit.ValidationExpression = new string('x', 512);
        ShouldAccept(validator.Validate(atLimit));

        CreateProfilePropertyDefinitionRequest overLimit = ValidDefinition();
        overLimit.ValidationExpression = new string('x', 513);
        ShouldReport(
            validator.Validate(overLimit),
            nameof(CreateProfilePropertyDefinitionRequest.ValidationExpression),
            "Validation Expression must be 512 characters or fewer");
    }

    /// <summary>
    /// A default value of unbounded length is accepted, because the terminal column carries no width.
    /// </summary>
    [Fact]
    public void ProfileDefinition_BoundsTheDefaultValueInNoWay()
    {
        CreateProfilePropertyDefinitionRequest definition = ValidDefinition();
        definition.DefaultValue = new string('v', 5000);

        ShouldAccept(new CreateProfilePropertyDefinitionRequestValidator().Validate(definition));
    }

    // ------------------------------------------------------------------------
    // BUILDERS
    // ------------------------------------------------------------------------

    /// <summary>Builds an update whose every member is legitimate.</summary>
    /// <returns>A valid update request.</returns>
    private static UpdateRoleRequest ValidUpdate() => new()
    {
        RoleName = "Subscribers",
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
        // Authoring-strength: twelve characters or more mixing letters with digits. The legacy-shaped
        // "JOIN2008" remains redeemable and can no longer be authored.
        RsvpCode = "JOIN2008-ALPHA",
        IconFile = "images/subscriber.gif",
    };

    /// <summary>Builds a role-group creation whose every member is legitimate.</summary>
    /// <returns>A valid create request.</returns>
    private static CreateRoleGroupRequest ValidRoleGroupCreate() => new()
    {
        RoleGroupName = "Subscription Roles",
        Description = "Roles that carry a subscription",
    };

    /// <summary>Builds a role-group update whose every member is legitimate.</summary>
    /// <returns>A valid update request.</returns>
    private static UpdateRoleGroupRequest ValidRoleGroupUpdate() => new()
    {
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
    /// <returns>A valid create request.</returns>
    private static CreateProfilePropertyDefinitionRequest ValidDefinition() => new()
    {
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
    };

    // ------------------------------------------------------------------------
    // ASSERTION HELPERS
    // ------------------------------------------------------------------------

    /// <summary>
    /// Builds an invitation code of an exact length that satisfies every rule EXCEPT a width bound.
    /// </summary>
    /// <param name="length">The length the value must have.</param>
    /// <returns>A code of exactly that length, mixing letters with digits.</returns>
    private static string StrongCodeOfLength(int length)
        => string.Concat(Enumerable.Range(0, length).Select(index => index % 2 == 0 ? 'a' : '1'));
    /// <summary>
    /// Asserts that a result reports the given message against the given property, character for character.
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

    /// <summary>
    /// Asserts that a result is a refusal naming the given property, without asserting the wording.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property the failure must name.</param>
    private static void ShouldNotAccept(ValidationResult result, string property)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property);
        result.Errors.Should().Contain(
            failure => failure.PropertyName == property,
            "the refusal must name {0}, but the result was {1}",
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
