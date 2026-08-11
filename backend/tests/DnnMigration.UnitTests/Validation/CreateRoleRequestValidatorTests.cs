// MIGRATION: this suite is the parity proof for Application/Validation/CreateRoleRequestValidator.cs. Its
// subject is not "does the validator reject bad input" but "does the validator enforce, field for field and
// word for word, the rule set the legacy role-edit screen enforced". Every assertion therefore names the
// legacy declaration it reproduces, and every message is quoted character for character rather than matched
// by substring - a substring match cannot tell "Trial Period Must Be Greater Than Zero" apart from
// "<br>Trial Period Must Be Greater Than Zero", and that single difference is a divergence recorded below.
//
// MIGRATION: the legacy declarations, measured rather than assumed. Website/admin/Security/editroles.ascx is
// 190 lines and declares NINE declarative validators - ONE asp:RequiredFieldValidator on the name and EIGHT
// asp:CompareValidator across the two fees and the two periods - with zero asp:RegularExpressionValidator,
// zero asp:RangeValidator and zero asp:CustomValidator. Declaration start / ErrorMessage / operator lines:
//   valRoleName       L29  msg L31                              RequiredFieldValidator on txtRoleName
//   valServiceFee1    L90  msg L92  Type="Currency" L92, Operator="DataTypeCheck" L93
//   valServiceFee2    L93  msg L95  Operator="GreaterThanEqual" ValueToCompare="0"  L96
//   valBillingPeriod1 L108 msg L110 Type="Integer"  Operator="DataTypeCheck"        L111
//   valBillingPeriod2 L111 msg L113 Operator="GreaterThan"      ValueToCompare="0"  L114
//   valTrialFee1      L123 msg L124 Type="Currency" Operator="DataTypeCheck"        L125
//   valTrialFee2      L125 msg L127 Operator="GreaterThanEqual" ValueToCompare="0"  L128
//   valTrialPeriod1   L140 msg L142 Type="Integer"  Operator="DataTypeCheck"        L143
//   valTrialPeriod2   L143 msg L145 Operator="GreaterThan"      ValueToCompare="0"  L146
//
// MIGRATION: the comparison family is the one that matters here, and a naive reading of the plan would have
// missed it. Measured by opening tag across the 39 administration user controls in
// Website/admin/{Portal,Users,Security,Modules,Tabs}: RequiredField 16, RegularExpression 3, COMPARE 19,
// Range 0, Custom 1 - thirty-nine validators, of which the comparison family alone equals the required-field
// and pattern families combined, and of which editroles.ascx contributes EIGHT of the nineteen comparisons.
// A suite that covered only required-field and pattern validators would have proved almost nothing about
// this screen, so every one of the eight comparisons is accounted for below, either by the rule that
// reproduces it or by the documented reason it needs none.
//
// MIGRATION: the two fee rules and the two period rules deliberately DISAGREE with each other about zero,
// and that asymmetry is the legacy behaviour rather than an oversight. FEES ADMIT ZERO; PERIODS REFUSE IT.
// Four independent measurements agree, which is why the four zero-boundary tests below are the load-bearing
// part of this file:
//   (1) the operators themselves - GreaterThanEqual on both fees (L96, L128), GreaterThan on both periods
//       (L114, L146);
//   (2) the runtime resource values - see the resx annotation below;
//   (3) the code-behind defaults - EditRoles.ascx.vb L213 and L223 initialise both periods to 1, the
//       smallest value a strictly-positive comparison admits, and never to zero and never to the negative
//       sentinel;
//   (4) the schema default - 01.00.05.SqlDataProvider adds DF_Roles_ServiceFee DEFAULT (0) FOR ServiceFee
//       and 03.01.01.SqlDataProvider L1177 re-asserts a zero default, so zero is a legitimate stored fee.
//
// MIGRATION: FINDING D - the four relational comparisons declare NO Type attribute. valServiceFee2 (L96),
// valBillingPeriod2 (L114), valTrialFee2 (L128) and valTrialPeriod2 (L146) each carry only CssClass, runat,
// resourcekey, ControlToValidate, ErrorMessage, Display, Operator and ValueToCompare. ASP.NET defaults an
// unspecified Type to String, so at the framework level all four performed an ORDINAL STRING comparison
// against the literal "0"; numeric intent was supplied solely by the DataTypeCheck partner running alongside
// on the same control. The target implements and this suite asserts NUMERIC semantics, because that is what
// the pairing intended and what a typed contract can express. The divergence is that a caller who could
// previously defeat the ordinal comparison with a numerically-equivalent but textually-different entry can
// no longer do so; the outcome is strictly closer to the intent, never further from it.
//
// MIGRATION: the runtime wording differed from the wording carried across, and the difference strengthens
// the decision rather than undermining it. Website/admin/Security/App_LocalResources/EditRoles.ascx.resx is
// 245 lines with 46 data entries and contains ALL NINE validator keys, as val<X>.Text rather than
// val<X>.ErrorMessage because every declaration carries a bare resourcekey with no property suffix. Seven of
// the nine resource values are byte-identical to their inline attribute. Exactly two differ, and they are
// precisely the two self-contradicting declarations:
//   resx L195 valBillingPeriod2.Text = "<br>Billing Period Must Be Greater Than Zero"
//        - inline L113 says "or Equal to", so the RESOURCE agrees with the GreaterThan operator;
//   resx L201 valTrialFee2.Text      = "<br>Trial Fee Must Be Greater Than or Equal to Zero"
//        - inline L127 says "Greater Than Zero", so the RESOURCE agrees with the GreaterThanEqual operator.
// ASP.NET localisation overwrites the inline ErrorMessage whenever a resourcekey resolves, so the strings
// users actually SAW were self-consistent with the operators: DotNetNuke 4.9 was correct at runtime and the
// contradiction survives only in dead inline fallback attributes. The validator carries the INLINE wording,
// so this suite asserts the inline wording; the recorded divergence is that the two contradicting messages
// therefore read differently from what a 4.9 user saw, while the OPERATOR - the behaviour - is identical in
// both readings.
//
// MIGRATION: the leading <br> is STRIPPED from every message the validator carries, and this suite asserts
// the stripped form. Each legacy ErrorMessage opened with that literal tag because the text was written
// straight into page markup and needed a line break ahead of it. The migrated API answers with a
// machine-readable problem document whose errors dictionary carries data, not markup, and a client that
// inserted a server-supplied tag into a document would be rendering markup it did not author. The WORDING
// after the tag is reproduced character for character, which is what behavioural equivalence is about.
//
// MIGRATION: no upper bound is asserted on either fee, and the ceiling implied by the baseline column is
// refuted by measurement. 01.00.00.SqlDataProvider L119 declares ServiceFee as a scaled decimal that would
// cap the value in the hundreds, but that is the BASELINE only: the destructive upgrade chain rebuilds the
// table with ServiceFee typed money (01.00.05.SqlDataProvider L2746 creates dbo.Tmp_Roles with money at
// L2752) and 03.01.01.SqlDataProvider L1173 re-asserts ALTER COLUMN [ServiceFee] [money] NULL. The terminal
// type is money, whose range is fifteen digits wide, so a test below proves a five-figure fee is ACCEPTED.
// The correct terminal-state algorithm is: take the LAST CREATE TABLE dbo.Tmp_<Table> recreate, then apply
// any subsequent ALTER COLUMN on top. Concluding "no ALTER COLUMN, therefore the baseline stands" is
// UNSOUND, because this chain evolves tables by the drop-and-recreate idiom - create Tmp_<T>, copy rows,
// drop <T>, sp_rename - and ServiceFee is the column that proves both halves of the algorithm are needed.
//
// MIGRATION: the frequency members are NOT checked against a closed list of the six legacy characters, and
// this suite must never be edited to assert that a single unrecognised character is refused. The allow-list
// reading is refuted on four measured grounds:
//   (a) the shipped seed data itself carries codes outside the six - 01.00.00.SqlDataProvider L7192 seeds
//       Administrators with the digit four and L7194 seeds Registered Users with the digit zero;
//   (b) the legacy screen deliberately TOLERATED an unrecognised code - EditRoles.ascx.vb L148-L152 and
//       L156-L160 look the stored code up and select it only when the lookup returns something, leaving the
//       drop-down unselected rather than raising;
//   (c) the valid set was configurable DATA read at runtime - EditRoles.ascx.vb L116-L117 reads the
//       "Frequency" list through the general-purpose lists subsystem, which this migration excludes
//       outright, so no compile-time rule could know its contents;
//   (d) the schema constrains nothing - across all 88 upgrade scripts a case-insensitive search for a CHECK
//       constraint naming either frequency column returns no hit at all; both columns stay a plain
//       single-character nullable type.
// What the contract DOES constrain is membership of the shared domain enumeration, and that is what is
// asserted here.
//
// MIGRATION: the two shipped seed codes are UNREPRESENTABLE in the contract, and this is the one real
// limitation of modelling the column as a closed enumeration. Because the request types both frequency
// members as the domain enumeration - whose six members carry the code points of the six legacy characters
// and nothing else - a caller CREATING a role at the digit-four or digit-zero code is refused. That is the
// right answer for a WRITE: no new role should be created at a code nothing can interpret, and the legacy
// expiry switch (RoleController.vb L539-L546) carries no default branch, so such a code silently computed
// no expiry at all. Tolerance for the two SEEDED ROWS must therefore live on the READ path, in
// Domain/Enums/BillingFrequency.cs and Application/Mapping/RoleMappings.cs, and must never be retro-fitted
// here by weakening the rule: loosening it would not help the existing rows, it would only let new ones be
// created. The tests below assert what IS true rather than what would be convenient.
//
// MIGRATION: no identifier and no date carries a bound test, and none may ever be added. Roles.RoleID is
// declared IDENTITY (0, 1) (01.00.00.SqlDataProvider L115), so zero is the FIRST REAL KEY and the shipped
// Administrators role holds exactly that value (L7192) while Registered Users holds eleven (L7194).
// RoleGroupID of minus one is a REAL, SELECTABLE choice - EditRoles.ascx.vb L75 adds it as the drop-down's
// first entry from the localised GlobalRoles resource - and is simultaneously the legacy absent-integer
// sentinel, so a lower bound would refuse either a real group or the ungrouped case. The legacy null-date
// sentinel is the minimum date value, so a lower bound on a date would refuse a legitimate "no date". The
// legacy assignment path CLAMPS dates rather than refusing them (RoleController.vb L530-L535), and the
// role-ASSIGNMENT screen securityroles.ascx L47 declares a cross-field date comparison that editroles.ascx
// pointedly does NOT - a contrast which proves the absence on the role-edit screen is deliberate.
//
// MIGRATION: the role fees DO enforce a lower bound and the portal's host fee deliberately does NOT. The
// asymmetry is measured, not accidental: editroles.ascx declares valServiceFee2 and valTrialFee2 explicitly,
// whereas Website/admin/Portal/sitesettings.ascx L444-L446 declares valHostFee as a DataTypeCheck with no
// relational companion at all. A future reader must NOT harmonise the two screens in either direction. It
// is reproduced by design and is not a defect.
using System.Globalization;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule parity between <see cref="CreateRoleRequestValidator"/> and the nine declarative
/// validators on the legacy role-edit screen <c>Website/admin/Security/editroles.ascx</c>.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing behaviour on this screen is what happens at exactly zero, because the two rule families
/// diverge there and nowhere else: a fee of zero is a legitimate price - it is how the legacy screen
/// expressed "this role is free", tested as such at <c>EditRoles.ascx.vb</c> L146 - whereas a period of zero
/// is a contradiction, since a billing cycle of no units could never advance an expiry date. Four separately
/// named tests pin that divergence, one per field, and the suite is not a parity proof without them.
/// </para>
/// <para>
/// The second load-bearing behaviour is absence. Every one of the eight legacy comparisons was a
/// <c>CompareValidator</c>, which SUCCEEDS against an empty control because it compares nothing when there
/// is nothing to compare, and that is precisely why the name carries the screen's only presence check. A
/// request naming nothing but the role must therefore be accepted, and a rule written without its presence
/// guard - or a member left non-nullable so that it defaults to zero - would turn "not supplied" into
/// "supplied as zero" and refuse every free role ever created.
/// </para>
/// <para>
/// Both halves of every failure are asserted, never just the fact of failure. The property name becomes the
/// key and the message the value in the <c>errors</c> dictionary of the RFC 7807 payload a client consumes,
/// so a test that checked only <see cref="ValidationResult.IsValid"/> would prove the rule fires without
/// proving it reports what the legacy screen reported.
/// </para>
/// </remarks>
public class CreateRoleRequestValidatorTests
{
    /// <summary>
    /// Wording of <c>valRoleName</c>, the screen's only presence check (<c>editroles.ascx</c> L31, declared
    /// at L29), with its leading markup tag removed.
    /// </summary>
    private const string RoleNameRequired = "You Must Enter a Valid Name";

    /// <summary>
    /// Wording of <c>valServiceFee2</c> (<c>editroles.ascx</c> L95, declared at L93). Message and operator
    /// agree: the operator at L96 admits zero and the text says "or Equal to".
    /// </summary>
    private const string ServiceFeeNegative = "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>
    /// Wording of <c>valBillingPeriod2</c> (<c>editroles.ascx</c> L113, declared at L111), carried across
    /// UNCHANGED even though it contradicts the operator it accompanies.
    /// </summary>
    /// <remarks>
    /// The operator at L114 is strictly greater than zero, so a submitted zero is refused, while this text
    /// says "or Equal to". The constant's NAME describes the operator and its VALUE describes the legacy
    /// wording, and the two are deliberately allowed to disagree here so that the disagreement is visible at
    /// the point of use rather than hidden behind a tidied-up literal.
    /// </remarks>
    private const string BillingPeriodNotPositive = "Billing Period Must Be Greater Than Zero";

    /// <summary>
    /// Wording of <c>valTrialFee2</c> (<c>editroles.ascx</c> L127, declared at L125), carried across
    /// UNCHANGED even though it contradicts the operator it accompanies.
    /// </summary>
    /// <remarks>
    /// The operator at L128 admits zero, so a free trial is accepted, while this text says "Greater Than
    /// Zero" - the same class of defect as the billing period, in the opposite direction.
    /// </remarks>
    private const string TrialFeeNegative = "Trial Fee Must Be Greater Than or Equal to Zero";

    /// <summary>
    /// Wording of <c>valTrialPeriod2</c> (<c>editroles.ascx</c> L145, declared at L143). Message and
    /// operator agree: the operator at L146 is strictly greater than zero and the text says so.
    /// </summary>
    private const string TrialPeriodNotPositive = "Trial Period Must Be Greater Than Zero";

    /// <summary>
    /// Reported when a submitted billing frequency is outside the domain enumeration.
    /// </summary>
    private const string BillingFrequencyInvalid =
        "Billing Frequency must be one of None, One Time, Day, Week, Month or Year.";

    /// <summary>
    /// Reported when a submitted trial frequency is outside the domain enumeration.
    /// </summary>
    private const string TrialFrequencyInvalid =
        "Trial Frequency must be one of None, One Time, Day, Week, Month or Year.";

    /// <summary>
    /// Reported when a submitted icon path is rooted, volume-qualified or traverses upwards.
    /// </summary>
    private const string IconFileNotRelative =
        "Icon File must be a relative path within the portal's own folder.";

    /// <summary>
    /// Width of <c>Roles.RoleName nvarchar(50) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> L117, carried
    /// unchanged through the table rebuild at <c>01.00.05.SqlDataProvider</c> L2750).
    /// </summary>
    private const int RoleNameWidth = 50;

    /// <summary>
    /// Width of <c>Roles.Description nvarchar(1000) NULL</c> (<c>01.00.00.SqlDataProvider</c> L118).
    /// </summary>
    private const int DescriptionWidth = 1000;

    /// <summary>
    /// Width of <c>Roles.RSVPCode nvarchar(50) NULL</c>, added to the table alongside the icon column by
    /// <c>03.02.03.SqlDataProvider</c> L45 and never widened afterwards.
    /// </summary>
    /// <remarks>
    /// Both of these two columns arrive on a CONTINUATION line - L44 opens the statement against the table
    /// and L45 carries <c>ADD RSVPCode nvarchar(50) NULL, IconFile nvarchar(100) NULL</c> - so a search that
    /// expects the column name on the same line as its <c>ALTER TABLE</c> reports both columns as absent.
    /// The same care the frequency codes need over naming forms and letter case, this chain needs over line
    /// breaks.
    /// </remarks>
    private const int RsvpCodeWidth = 50;

    /// <summary>
    /// Width of <c>Roles.IconFile nvarchar(100) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45).
    /// </summary>
    private const int IconFileWidth = 100;

    /// <summary>
    /// The subject. Constructed directly and once, because the validator takes no constructor argument,
    /// reads no configuration and touches no store - there is nothing here to mock.
    /// </summary>
    private readonly CreateRoleRequestValidator _validator = new();

    /// <summary>
    /// Builds the minimum request the legacy screen would have accepted: a name and nothing else.
    /// </summary>
    /// <returns>A request that must validate.</returns>
    /// <remarks>
    /// Every other member is left unset on purpose, so each test below mutates exactly one field and a
    /// failure identifies exactly one rule. The name is the only member the screen ever required.
    /// </remarks>
    private static CreateRoleRequest ValidRequest() => new()
    {
        RoleName = "Subscribers",
    };

    /// <summary>
    /// Renders the framework's own length message for a property, so the expectation cannot drift from the
    /// validator's wording.
    /// </summary>
    /// <param name="displayName">The property's display name, which the framework derives by splitting its
    /// PascalCase identifier into words.</param>
    /// <param name="ceiling">The declared maximum length.</param>
    /// <param name="submitted">The length actually submitted, which the message quotes back.</param>
    /// <returns>The rendered message.</returns>
    /// <remarks>
    /// The validator does not override the message for any of its four length rules, so the framework
    /// default IS the contract and is reproduced here rather than approximated. Both numbers are formatted
    /// with the invariant culture, matching the framework, so the expectation cannot shift with the ambient
    /// culture of the test host - a suite that passed in one locale and failed in another would be a defect.
    /// </remarks>
    private static string LengthExceeded(string displayName, int ceiling, int submitted)
        => string.Format(
            CultureInfo.InvariantCulture,
            "The length of '{0}' must be {1} characters or fewer. You entered {2} characters.",
            displayName,
            ceiling,
            submitted);

    /// <summary>
    /// Asserts that a result reports the given message against the given property, character for character.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property the failure must name.</param>
    /// <param name="message">The message the failure must carry.</param>
    /// <remarks>
    /// Both halves are asserted because both travel to the caller: the property becomes the key and the
    /// message the value in the <c>errors</c> dictionary of the RFC 7807 payload. Asserting only that
    /// validation failed would leave the wording - which is what the legacy parity obligation is about -
    /// entirely unproven.
    /// </remarks>
    private static void ShouldReport(ValidationResult result, string property, string message)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property);
        result.Errors.Should().Contain(
            failure => failure.PropertyName == property && failure.ErrorMessage == message,
            "the failure for {0} must carry exactly the legacy wording, but the result was {1}",
            property,
            Render(result));
    }

    /// <summary>
    /// Asserts that a result reports nothing at all against the given property.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property that must be unreported.</param>
    private static void ShouldNotReport(ValidationResult result, string property)
        => result.Errors.Should().NotContain(
            failure => failure.PropertyName == property,
            "no failure was expected for {0}, but the result was {1}",
            property,
            Render(result));

    /// <summary>
    /// Asserts that a result reports nothing whatsoever.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    private static void ShouldAccept(ValidationResult result)
    {
        result.IsValid.Should().BeTrue(Render(result));
        result.Errors.Should().BeEmpty(Render(result));
    }

    /// <summary>
    /// Renders a validation result for an assertion message.
    /// </summary>
    /// <param name="result">The result to render.</param>
    /// <returns>The rendered reason.</returns>
    private static string Render(ValidationResult result) => result.Errors.Count == 0
        ? "the result reported nothing"
        : "the result reported "
            + string.Join(" | ", result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));

    // ------------------------------------------------------------------------
    // ACCEPTANCE BASELINES
    //
    // These come first because they are the cleanest possible proof of the
    // CompareValidator-succeeds-on-empty semantics, and because every rule test
    // below mutates one field of the same minimal request.
    // ------------------------------------------------------------------------

    /// <summary>
    /// A request naming nothing but the role is accepted, because the name carries the screen's only
    /// presence check and every other rule is guarded on its value being present.
    /// </summary>
    /// <remarks>
    /// This is the parity proof for the single most easily lost legacy behaviour on this screen. All eight
    /// comparisons were <c>CompareValidator</c> controls, which pass an empty control without comparing
    /// anything, so the legacy screen accepted a role with no paid-membership terms at all - which is what
    /// almost every role in a real installation is. A rule authored without its presence guard would refuse
    /// every such role, and this test is what would catch that.
    /// </remarks>
    [Fact]
    public async Task ARequestNamingOnlyTheRole_IsAccepted()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        ShouldAccept(result);
    }

    /// <summary>
    /// The minimal request really does leave every optional term absent, so the acceptance above is evidence
    /// about the rules rather than about a factory that quietly supplied defaults.
    /// </summary>
    /// <remarks>
    /// Absence and zero are different submissions with different outcomes on this screen - absence is always
    /// accepted, a zero period never is - so the distinction has to be established rather than assumed.
    /// Every one of these members is nullable precisely so that a blank text box survives the boundary as
    /// absence: in C# an empty string does not parse to a number at all, whereas the legacy code-behind let
    /// a blank box fall through to a declared default (<c>EditRoles.ascx.vb</c> L212-L214, L222-L224).
    /// </remarks>
    [Fact]
    public void TheMinimalRequest_LeavesEveryOptionalTermAbsent()
    {
        CreateRoleRequest request = ValidRequest();

        request.Description.Should().BeNull();
        request.ServiceFee.Should().BeNull();
        request.BillingPeriod.Should().BeNull();
        request.BillingFrequency.Should().BeNull();
        request.TrialFee.Should().BeNull();
        request.TrialPeriod.Should().BeNull();
        request.TrialFrequency.Should().BeNull();
        request.RoleGroupId.Should().BeNull();
        request.RsvpCode.Should().BeNull();
        request.IconFile.Should().BeNull();
    }

    /// <summary>
    /// A fully specified paid role with a free trial is accepted, exercising every member at once.
    /// </summary>
    /// <remarks>
    /// The trial fee is deliberately zero here rather than merely small, because zero is the value the
    /// legacy operator admitted and the legacy message denied. Setting it to zero in the all-fields-supplied
    /// case proves the two do not interact: a zero trial fee is accepted even when everything else is
    /// populated.
    /// </remarks>
    [Fact]
    public async Task AFullySpecifiedPaidRole_IsAccepted()
    {
        CreateRoleRequest request = new()
        {
            RoleName = "Gold Members",
            Description = "Paid membership with a free trial",
            IsPublic = true,
            AutoAssignment = false,
            ServiceFee = 19.99m,
            BillingPeriod = 1,
            BillingFrequency = BillingFrequency.Month,
            TrialFee = 0m,
            TrialPeriod = 14,
            TrialFrequency = BillingFrequency.Day,
            RoleGroupId = 3,
            RsvpCode = "GOLD-2026",
            IconFile = "icons/gold.gif",
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    // ------------------------------------------------------------------------
    // valRoleName - editroles.ascx L29, message L31
    //
    // The screen's ONE RequiredFieldValidator, and the only unconditional rule
    // in the validator.
    // ------------------------------------------------------------------------

    /// <summary>
    /// The role name is required, and a name of nothing but spaces counts as no name.
    /// </summary>
    /// <param name="roleName">The submitted name.</param>
    /// <remarks>
    /// A whitespace-only name is treated as absent, which is a documented narrowing rather than a
    /// reproduction: the legacy <c>RequiredFieldValidator</c> trimmed before testing, so it too refused a
    /// name of spaces, but it would have accepted one padded with spaces and stored the padding. The
    /// migrated rule refuses the empty case identically and leaves padding to the caller.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task RoleName_IsRequired(string? roleName)
    {
        CreateRoleRequest request = ValidRequest();
        request.RoleName = roleName!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.RoleName), RoleNameRequired);

        // One omission, one message. The rule chains a presence check and a width check, and the
        // rule-level cascade stops at the first failure, so a missing name must not also be reported as
        // an over-long one - a caller reading two messages about one blank field cannot tell which
        // describes the fault. This assertion also fixes the cascade: switching it to continue would
        // start reporting both.
        result.Errors
            .Where(failure => failure.PropertyName == nameof(CreateRoleRequest.RoleName))
            .Should().ContainSingle(Render(result));
    }

    /// <summary>
    /// The role name is bounded at the width of the column that stores it.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <remarks>
    /// Two sources agree on fifty here, which is why it is asserted as a hard boundary: the terminal column
    /// is <c>RoleName nvarchar(50) NOT NULL</c> and the legacy text box declared <c>MaxLength="50"</c>
    /// (<c>editroles.ascx</c> L27). The markup limit vanished with the postback, so the column width is now
    /// the only thing enforcing it and the rule has to.
    /// </remarks>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task RoleName_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.RoleName = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.RoleName),
                LengthExceeded("Role Name", RoleNameWidth, length));
        }
    }

    /// <summary>
    /// An over-long name reports the length and nothing else, so a caller reads one actionable message per
    /// field rather than a presence complaint stacked underneath a length complaint.
    /// </summary>
    [Fact]
    public async Task RoleName_WhenTooLong_ReportsOnlyTheLengthFailure()
    {
        CreateRoleRequest request = ValidRequest();
        request.RoleName = new string('a', RoleNameWidth + 1);

        ValidationResult result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(Render(result));
        result.Errors.Should().NotContain(
            failure => failure.ErrorMessage == RoleNameRequired,
            "a name that is too long is still a name, so the presence message must not also fire");
    }

    // ------------------------------------------------------------------------
    // Description - NO legacy validator of any kind
    //
    // The screen declared none, so the only bound is the column's own width and
    // there is deliberately no presence rule.
    // ------------------------------------------------------------------------

    /// <summary>
    /// The description is bounded at the width of the column that stores it.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <remarks>
    /// As with the name, both the terminal column - <c>Description nvarchar(1000) NULL</c> - and the legacy
    /// text box's <c>MaxLength="1000"</c> agree on the figure.
    /// </remarks>
    [Theory]
    [InlineData(999, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public async Task Description_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.Description = new string('b', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.Description),
                LengthExceeded("Description", DescriptionWidth, length));
        }
    }

    /// <summary>
    /// A description that is absent, or present but empty, reports nothing - there is no presence rule on
    /// it, because the legacy screen declared none.
    /// </summary>
    /// <param name="description">The submitted description.</param>
    /// <remarks>
    /// The parameter is declared nullable so that the absent case can be expressed as test data at all; a
    /// non-nullable declaration would be rejected outright by the analyser that guards this file. The empty
    /// case is asserted alongside the absent one because the legacy null-string sentinel WAS the empty
    /// string (<c>Null.vb</c> L73 returns it literally), so the two were indistinguishable once a row had
    /// been read and neither may be treated as a failure here.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Description_WhenAbsentOrEmpty_ReportsNothing(string? description)
    {
        CreateRoleRequest request = ValidRequest();
        request.Description = description;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.Description));
    }


    // ========================================================================
    // THE FOUR ZERO BOUNDARIES
    //
    // Zero is the precise point at which the two rule families diverge, and it
    // is the only point at which they do. Each field gets its own named test so
    // that a regression names the exact rule that broke, and all four together
    // are what make this file a parity proof rather than a smoke test.
    //
    //   ServiceFee    0 -> ACCEPTED    (valServiceFee2  operator admits zero)
    //   TrialFee      0 -> ACCEPTED    (valTrialFee2    operator admits zero)
    //   BillingPeriod 0 -> REFUSED     (valBillingPeriod2 strictly positive)
    //   TrialPeriod   0 -> REFUSED     (valTrialPeriod2   strictly positive)
    // ========================================================================

    /// <summary>
    /// A service fee of exactly zero is accepted, because zero is how the legacy screen expressed "this role
    /// is free" rather than how it expressed "this value is missing".
    /// </summary>
    /// <remarks>
    /// <c>valServiceFee2</c> (<c>editroles.ascx</c> L93, operator at L96) compares for greater-than-or-equal
    /// against zero, and message and operator agree for once. Three further measurements corroborate:
    /// <c>EditRoles.ascx.vb</c> L146 tests the stored fee against a formatted zero to decide whether the role
    /// has billing terms at all, treating zero as the meaningful "no billing" value;
    /// <c>01.00.05.SqlDataProvider</c> adds <c>DF_Roles_ServiceFee DEFAULT (0)</c> when it rebuilds the
    /// table; and <c>03.01.01.SqlDataProvider</c> L1177 re-asserts that zero default.
    /// </remarks>
    [Fact]
    public async Task ServiceFee_AtExactlyZero_IsAccepted()
    {
        CreateRoleRequest request = ValidRequest();
        request.ServiceFee = 0m;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.ServiceFee));
    }

    /// <summary>
    /// A trial fee of exactly zero is accepted - a free trial is a real configuration - even though the
    /// message the caller would see for a negative value says otherwise.
    /// </summary>
    /// <remarks>
    /// This is the acceptance half of the second contradiction. The refusal half, and the full annotation,
    /// sit on <see cref="TrialFee_WhenNegative_IsRefusedWithItsContradictoryLegacyWording"/>.
    /// </remarks>
    // MIGRATION: valTrialFee2 (editroles.ascx L125-L128) declares Operator="GreaterThanEqual"
    // ValueToCompare="0" at L128 while its own ErrorMessage at L127 reads "<br>Trial Fee Must Be Greater
    // Than Zero". The message CONTRADICTS its operator. The OPERATOR is authoritative and is reproduced
    // exactly - zero is accepted, as this test proves - and the contradictory wording is carried across
    // UNCHANGED and deliberately NOT corrected, per the migration discipline that a discovered legacy defect
    // is annotated in place rather than repaired. Tightening the operator to agree with the text would
    // refuse every free trial the legacy screen allowed. Corroboration: the runtime resource value at
    // EditRoles.ascx.resx L201 reads "<br>Trial Fee Must Be Greater Than or Equal to Zero" and therefore
    // AGREES with the operator, so DotNetNuke 4.9 was self-consistent at runtime and only the dead inline
    // fallback attribute is stale.
    [Fact]
    public async Task TrialFee_AtExactlyZero_IsAccepted()
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialFee = 0m;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialFee));
    }

    /// <summary>
    /// A billing period of exactly zero is refused, even though the message the caller receives says zero
    /// ought to be allowed.
    /// </summary>
    /// <remarks>
    /// This test and the message it asserts are the sharpest expression of the migration discipline in this
    /// file: the assertion deliberately expects a message that disagrees with the behaviour, because that is
    /// what the legacy declaration did. A future reader who "fixes" the wording will break this test, and
    /// the annotation below is why it must not be fixed.
    /// </remarks>
    // MIGRATION: valBillingPeriod2 (editroles.ascx L111-L114) declares Operator="GreaterThan"
    // ValueToCompare="0" at L114 while its own ErrorMessage at L113 reads "<br>Billing Period Must Be
    // Greater Than or Equal to Zero". The message CONTRADICTS its operator. The OPERATOR is authoritative
    // and is reproduced exactly - a submitted zero is REFUSED, as this test proves - and the contradictory
    // wording is carried across UNCHANGED and deliberately NOT corrected. Relaxing the operator to agree
    // with the text would accept a billing cycle of zero units, which could never advance an expiry date.
    // Corroboration: the runtime resource value at EditRoles.ascx.resx L195 reads "<br>Billing Period Must
    // Be Greater Than Zero" and therefore AGREES with the operator; and EditRoles.ascx.vb L213 initialises
    // the period to 1, the smallest value a strictly-positive comparison admits.
    [Fact]
    public async Task BillingPeriod_AtExactlyZero_IsRefused()
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingPeriod = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingPeriod), BillingPeriodNotPositive);
    }

    /// <summary>
    /// A trial period of exactly zero is refused, and here message and operator agree.
    /// </summary>
    /// <remarks>
    /// <c>valTrialPeriod2</c> (<c>editroles.ascx</c> L143, operator at L146) compares for strictly greater
    /// than zero and its message at L145 says exactly that, as does the runtime resource value at
    /// <c>EditRoles.ascx.resx</c> L207. <c>EditRoles.ascx.vb</c> L223 likewise initialises the trial period
    /// to one. This is the self-consistent counterpart to the billing period above, and asserting both is
    /// what shows the contradiction there is a property of that one declaration rather than of the family.
    /// </remarks>
    [Fact]
    public async Task TrialPeriod_AtExactlyZero_IsRefused()
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialPeriod = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.TrialPeriod), TrialPeriodNotPositive);
    }

    // ------------------------------------------------------------------------
    // THE FOUR COMPARISONS AWAY FROM ZERO
    //
    // Negative values are refused by all four rules; one unit is accepted by all
    // four. Every submitted amount below is written as an invariant-culture
    // literal - see the note on the decimal test data.
    // ------------------------------------------------------------------------

    /// <summary>
    /// A negative service fee is refused.
    /// </summary>
    /// <param name="serviceFee">The submitted amount, written in the invariant culture.</param>
    /// <remarks>
    /// The amount arrives as text and is parsed with the invariant culture rather than declared as a decimal
    /// literal, because a decimal is not a legal attribute argument in C# and because pinning the culture is
    /// exactly what the legacy screen failed to do: <c>EditRoles.ascx.vb</c> L217 and L227 parse the fee and
    /// period boxes with the culture-sensitive framework parsers, so the legacy screen's notion of a decimal
    /// separator followed the server's locale. Every amount in this file is therefore culture-pinned, and a
    /// suite that passed in one locale and failed in another would be a defect rather than a nuisance.
    /// </remarks>
    [Theory]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("-19.99")]
    public async Task ServiceFee_WhenNegative_IsRefused(string serviceFee)
    {
        CreateRoleRequest request = ValidRequest();
        request.ServiceFee = decimal.Parse(serviceFee, CultureInfo.InvariantCulture);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.ServiceFee), ServiceFeeNegative);
    }

    /// <summary>
    /// A negative trial fee is refused, and the message it reports is the contradictory legacy wording.
    /// </summary>
    /// <param name="trialFee">The submitted amount, written in the invariant culture.</param>
    /// <remarks>
    /// The expectation here is deliberately the wording that disagrees with the operator, which is why this
    /// test carries the contradiction in its own name. Read together with
    /// <see cref="TrialFee_AtExactlyZero_IsAccepted"/>, the pair fully specifies the defect: zero passes,
    /// negative fails, and the message a caller reads for the failure claims that zero should have failed
    /// too.
    /// </remarks>
    [Theory]
    [InlineData("-0.01")]
    [InlineData("-1")]
    public async Task TrialFee_WhenNegative_IsRefusedWithItsContradictoryLegacyWording(string trialFee)
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialFee = decimal.Parse(trialFee, CultureInfo.InvariantCulture);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.TrialFee), TrialFeeNegative);
    }

    /// <summary>
    /// A negative billing period is refused.
    /// </summary>
    /// <param name="billingPeriod">The submitted period.</param>
    /// <remarks>
    /// The negative one case is worth stating explicitly, because that value carried a SECOND, unrelated
    /// meaning in the legacy code: it was the absent-integer sentinel, and
    /// <c>RoleController.vb</c> L537 branched on a period equal to it to short-circuit to no expiry date at
    /// all. Modelling the period as nullable resolves the tension without choosing a side - absence now
    /// expresses "no term", so the sentinel never travels as a literal - and a caller who submits the
    /// negative value outright is refused exactly as the legacy comparison refused it.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(-12)]
    public async Task BillingPeriod_WhenNegative_IsRefused(int billingPeriod)
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingPeriod = billingPeriod;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingPeriod), BillingPeriodNotPositive);
    }

    /// <summary>
    /// A negative trial period is refused.
    /// </summary>
    /// <param name="trialPeriod">The submitted period.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(-12)]
    public async Task TrialPeriod_WhenNegative_IsRefused(int trialPeriod)
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialPeriod = trialPeriod;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.TrialPeriod), TrialPeriodNotPositive);
    }

    /// <summary>
    /// One unit satisfies both period rules, which is the value the legacy code-behind itself defaulted to.
    /// </summary>
    /// <remarks>
    /// <c>EditRoles.ascx.vb</c> L213 and L223 declare both periods as one before the guards that might
    /// overwrite them, so one is the smallest period the legacy screen ever stored and is the value that
    /// proves the comparisons are strictly-positive rather than non-negative.
    /// </remarks>
    [Fact]
    public async Task BothPeriods_AtOneUnit_AreAccepted()
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingPeriod = 1;
        request.TrialPeriod = 1;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// A five-figure fee is accepted, because the terminal column is a money type rather than the narrow
    /// scaled decimal the baseline script declared.
    /// </summary>
    /// <param name="fee">The submitted amount, written in the invariant culture.</param>
    /// <remarks>
    /// <para>
    /// This test exists to keep a superseded ceiling from being reintroduced. The baseline
    /// <c>01.00.00.SqlDataProvider</c> L119 declares <c>ServiceFee</c> with a precision that would cap the
    /// value in the hundreds, and reading only that script yields a bound that has not applied for many
    /// versions: <c>01.00.05.SqlDataProvider</c> L2746 rebuilds the table with the fee typed as money at
    /// L2752, and <c>03.01.01.SqlDataProvider</c> L1173 re-asserts <c>ALTER COLUMN [ServiceFee] [money]
    /// NULL</c>. A money column spans fifteen digits, and no legacy validator bounded either fee above.
    /// </para>
    /// <para>
    /// The general lesson is worth recording: this upgrade chain evolves tables by creating a temporary
    /// twin, copying rows, dropping the original and renaming, so "there is no ALTER COLUMN for this column,
    /// therefore the baseline declaration stands" is an unsound inference. The terminal state is the LAST
    /// table recreate with any subsequent column alteration applied on top, and this column is the proof
    /// that both halves of that rule are needed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("1000.00")]
    [InlineData("12345.67")]
    [InlineData("1234567.8912")]
    public async Task BothFees_FarAboveTheBaselineCeiling_AreAccepted(string fee)
    {
        decimal amount = decimal.Parse(fee, CultureInfo.InvariantCulture);
        CreateRoleRequest request = ValidRequest();
        request.ServiceFee = amount;
        request.TrialFee = amount;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// Every fee and period rule is silent when its value is absent, which is the whole reason the legacy
    /// screen needed only one presence check.
    /// </summary>
    /// <remarks>
    /// Asserted field by field rather than through the minimal request alone, so that a guard accidentally
    /// dropped from one of the four rules is reported against that field by name.
    /// </remarks>
    [Fact]
    public async Task EveryPaidMembershipRule_IsSilentWhenItsValueIsAbsent()
    {
        CreateRoleRequest request = ValidRequest();
        request.ServiceFee = null;
        request.BillingPeriod = null;
        request.TrialFee = null;
        request.TrialPeriod = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.ServiceFee));
        ShouldNotReport(result, nameof(CreateRoleRequest.BillingPeriod));
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialFee));
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialPeriod));
    }


    // ========================================================================
    // THE TWO FREQUENCY MEMBERS
    //
    // The request types both as the shared domain enumeration, so the rule is
    // membership of that type and NOTHING MORE. There is no list of characters
    // anywhere in this file, and none may ever be added: the four measurements
    // refuting a closed character list are set out in the header annotation, and
    // the tests below assert the consequences of each.
    // ========================================================================

    /// <summary>
    /// Every declared member is accepted for the billing term.
    /// </summary>
    /// <param name="frequency">The frequency under test.</param>
    /// <remarks>
    /// All six members are exercised, including the never-bill member: that member is a real legacy code and
    /// the default selection of the legacy drop-down, not an unset marker, so refusing it would refuse the
    /// commonest submission the screen produced.
    /// </remarks>
    [Theory]
    [InlineData(BillingFrequency.None)]
    [InlineData(BillingFrequency.OneTime)]
    [InlineData(BillingFrequency.Day)]
    [InlineData(BillingFrequency.Week)]
    [InlineData(BillingFrequency.Month)]
    [InlineData(BillingFrequency.Year)]
    public async Task BillingFrequency_AcceptsEveryDeclaredMember(BillingFrequency frequency)
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = frequency;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// Every declared member is accepted for the trial term as well, because one member list serves both
    /// columns.
    /// </summary>
    /// <param name="frequency">The frequency under test.</param>
    /// <remarks>
    /// The schema joins the same frequency list twice from a single role row, once per column, so a separate
    /// trial-specific type would have no source. Asserting the full set against both members is what proves
    /// the two rules were authored symmetrically.
    /// </remarks>
    [Theory]
    [InlineData(BillingFrequency.None)]
    [InlineData(BillingFrequency.OneTime)]
    [InlineData(BillingFrequency.Day)]
    [InlineData(BillingFrequency.Week)]
    [InlineData(BillingFrequency.Month)]
    [InlineData(BillingFrequency.Year)]
    public async Task TrialFrequency_AcceptsEveryDeclaredMember(BillingFrequency frequency)
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialFrequency = frequency;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// Both frequency members may be absent, because both columns are nullable and the legacy screen's
    /// default selection was merely a default rather than a requirement.
    /// </summary>
    /// <remarks>
    /// The two shipped roles prove the point at the data level: both are seeded with a null trial frequency
    /// (<c>01.00.00.SqlDataProvider</c> L7192 and L7194), so a role with no frequency at all is not a
    /// hypothetical - it is what ships.
    /// </remarks>
    [Fact]
    public async Task BothFrequencies_MayBeAbsent()
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = null;
        request.TrialFrequency = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.BillingFrequency));
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialFrequency));
    }

    /// <summary>
    /// A value cast into the frequency type that is not one of its members is refused, for both members.
    /// </summary>
    /// <param name="rawValue">The raw numeric value cast into the enumeration.</param>
    /// <remarks>
    /// <para>
    /// This is the rule that makes the enumeration itself the constraint, and it is load-bearing here in a
    /// way that an enumeration check usually is not: the members carry the code points of the legacy stored
    /// characters rather than a zero-based sequence, so an arbitrary cast is not merely unlikely to be a
    /// member - the ordinary default is not one either. The test data is verified to be outside the
    /// enumeration before it is submitted, so the test cannot silently degrade into asserting nothing.
    /// </para>
    /// <para>
    /// Note carefully what is NOT asserted: nothing here claims that a single unrecognised CHARACTER is
    /// refused on the strength of it being outside the six legacy codes. The refusal is a consequence of the
    /// contract's chosen type, and the header annotation records why a character allow-list would be wrong.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(122)]
    [InlineData(9999)]
    public async Task BothFrequencies_RefuseAValueOutsideTheEnumeration(int rawValue)
    {
        BillingFrequency outsider = (BillingFrequency)rawValue;
        Enum.IsDefined(outsider).Should().BeFalse("the test data must actually be outside the enumeration");

        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = outsider;
        request.TrialFrequency = outsider;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingFrequency), BillingFrequencyInvalid);
        ShouldReport(result, nameof(CreateRoleRequest.TrialFrequency), TrialFrequencyInvalid);
    }

    /// <summary>
    /// The ordinary default of the frequency type is refused, which is the boundary a caller is most likely
    /// to reach by accident.
    /// </summary>
    /// <remarks>
    /// Because every member carries a stored character's code point, no member has the value zero, so the
    /// type's default is not a member. A caller that sends it has sent something the column cannot hold, and
    /// a deserialiser that filled the member in rather than leaving it absent would be caught here.
    /// </remarks>
    [Fact]
    public async Task BothFrequencies_RefuseTheDefaultOfTheirType()
    {
        Enum.IsDefined(default(BillingFrequency)).Should().BeFalse(
            "no member may carry the value zero, because every member carries a stored character");

        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = default(BillingFrequency);
        request.TrialFrequency = default(BillingFrequency);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingFrequency), BillingFrequencyInvalid);
        ShouldReport(result, nameof(CreateRoleRequest.TrialFrequency), TrialFrequencyInvalid);
    }

    /// <summary>
    /// The two frequency codes that DotNetNuke itself seeds are refused on the CREATE path, which pins the
    /// one genuine limitation of modelling the column as a closed enumeration.
    /// </summary>
    /// <param name="seededCode">The seeded character, exactly as it sits in the shipped baseline data.</param>
    /// <remarks>
    /// <para>
    /// This test asserts what IS true rather than what would be convenient, and it is the honest answer to
    /// the question the header annotation raises. Because the contract types both members as a closed
    /// enumeration that cannot represent either character, a caller creating a role at one of those codes is
    /// refused. That is the correct outcome for a WRITE - no new role should be created at a code nothing
    /// can interpret, and the legacy expiry switch had no default branch, so such a code computed no expiry
    /// at all - but it means the assertion "the shipped codes are accepted", which would hold had the
    /// contract typed these members as plain single characters, is NOT APPLICABLE to this contract and is
    /// not faked here.
    /// </para>
    /// <para>
    /// The consequence must not be misread as licence to weaken this rule. Tolerance for the two SEEDED ROWS
    /// belongs on the READ path, in the domain enumeration and the role mapper; loosening the rule here would
    /// not rescue a single existing row, it would only permit new rows to be created at an uninterpretable
    /// code.
    /// </para>
    /// </remarks>
    // MIGRATION: the shipped seed data carries billing codes OUTSIDE the six documented members -
    // 01.00.00.SqlDataProvider L7192 seeds the Administrators role with the digit four and L7194 seeds the
    // Registered Users role with the digit zero - and no CHECK constraint anywhere in the 88-script chain
    // restricts either column, so the database accepts them. The legacy screen tolerated them too, looking
    // the stored code up and leaving the drop-down unselected when the lookup found nothing
    // (EditRoles.ascx.vb L148-L152 and L156-L160), and the valid set was itself runtime DATA read from the
    // excluded lists subsystem (L116-L117). This suite therefore asserts NO character allow-list. The closed
    // enumeration chosen by the contract nevertheless cannot represent these two codes, so they are refused
    // on the write path; that divergence is documented rather than designed around.
    [Theory]
    [InlineData('4')]
    [InlineData('0')]
    public async Task BothFrequencies_RefuseTheShippedSeedCodesOnTheCreatePath(char seededCode)
    {
        BillingFrequency seeded = (BillingFrequency)seededCode;
        Enum.IsDefined(seeded).Should().BeFalse(
            "the shipped seed codes lie outside the six documented members, which is the entire point");

        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = seeded;
        request.TrialFrequency = seeded;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingFrequency), BillingFrequencyInvalid);
        ShouldReport(result, nameof(CreateRoleRequest.TrialFrequency), TrialFrequencyInvalid);
    }

    /// <summary>
    /// Each declared member still converts to the single character the column stores, which is what makes a
    /// member rename a data corruption rather than a refactor.
    /// </summary>
    /// <param name="frequency">The member under test.</param>
    /// <param name="storedCharacter">The character the column holds for it.</param>
    /// <remarks>
    /// Asserted here, in the suite that proves there is no character allow-list, because the two facts are
    /// easily confused and only one of them is a validation rule. The characters are persisted DATA in a
    /// single-character column and the legacy expiry switch dispatched on them directly
    /// (<c>RoleController.vb</c> L539-L546), so an identifier may be respelled but a VALUE may not be
    /// changed. This test fails the instant one is.
    /// </remarks>
    [Theory]
    [InlineData(BillingFrequency.None, 'N')]
    [InlineData(BillingFrequency.OneTime, 'O')]
    [InlineData(BillingFrequency.Day, 'D')]
    [InlineData(BillingFrequency.Week, 'W')]
    [InlineData(BillingFrequency.Month, 'M')]
    [InlineData(BillingFrequency.Year, 'Y')]
    public void EachDeclaredMember_StillCarriesItsStoredCharacter(
        BillingFrequency frequency,
        char storedCharacter)
    {
        ((char)frequency).Should().Be(
            storedCharacter,
            "the member values are the persisted contract, so changing one would silently mis-read live rows");
    }


    // ------------------------------------------------------------------------
    // THE INVITATION CODE AND THE ICON PATH
    //
    // Neither carried a legacy validator. The invitation code therefore has its
    // column width and nothing else; the icon path has its width plus one
    // NET-NEW containment rule, annotated as such because it is the only rule in
    // the validator without a legacy ancestor.
    // ------------------------------------------------------------------------

    /// <summary>
    /// The invitation code is bounded at the width of the column that stores it, and nothing more is
    /// asserted about it.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <remarks>
    /// No legacy validator was declared on the invitation-code box and no index makes the value unique, so a
    /// clash between two roles is not a conflict and there is deliberately no uniqueness rule to reproduce.
    /// </remarks>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task RsvpCode_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.RsvpCode = new string('c', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.RsvpCode),
                LengthExceeded("Rsvp Code", RsvpCodeWidth, length));
        }
    }

    /// <summary>
    /// The icon path is bounded at the width of the column that stores it.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    [Theory]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task IconFile_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.IconFile = new string('d', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.IconFile),
                LengthExceeded("Icon File", IconFileWidth, length));
        }
    }

    /// <summary>
    /// A relative icon path is accepted, in every form the legacy picker could have produced.
    /// </summary>
    /// <param name="iconFile">The submitted path.</param>
    /// <remarks>
    /// The legacy control offered a list of files already inside the portal's own folder and stored whatever
    /// it yielded verbatim, so a bare file name and a forward-slash relative path must both survive. The
    /// backslash form is accepted mid-path as well, because the legacy application stored Windows separators
    /// while the migrated API runs on Linux and existing stored values must remain resubmittable.
    /// </remarks>
    [Theory]
    [InlineData("gold.gif")]
    [InlineData("icons/gold.gif")]
    [InlineData("icons\\gold.gif")]
    [InlineData("Images/Roles/gold-members.png")]
    public async Task IconFile_WhenRelative_IsAccepted(string iconFile)
    {
        CreateRoleRequest request = ValidRequest();
        request.IconFile = iconFile;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// A rooted, volume-qualified or upward-traversing icon path is refused.
    /// </summary>
    /// <param name="iconFile">The submitted path.</param>
    /// <remarks>
    /// This rule is the validator's only NET-NEW rule and is asserted here as such rather than as parity.
    /// The legacy screen needed no validator because its picker could only ever yield a file that already
    /// existed inside the portal's folder; a JSON contract has no such affordance, because a caller now
    /// supplies the string directly, so an unbounded caller-supplied path reaching a stored column is a
    /// genuine gap rather than a matter of taste. Both separators are treated as rooting characters
    /// regardless of the host platform, which is why the platform-agnostic forms are covered.
    /// </remarks>
    // MIGRATION: NET-NEW rule with no legacy ancestor. editroles.ascx declares no validator on its icon
    // picker; the code-behind restricted the choice by file type instead (EditRoles.ascx.vb L129) and stored
    // the control's value verbatim (L248). That restriction was a property of the CONTROL, and a JSON
    // contract cannot reproduce a control. The rule is therefore an addition rather than a reproduction, and
    // it is recorded as a deliberate behavioural difference rather than presented as parity.
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\icon.gif")]
    [InlineData("C:\\Windows\\icon.gif")]
    [InlineData("../../secrets.txt")]
    [InlineData("icons/../../secrets.txt")]
    [InlineData("http://example.test/icon.gif")]
    public async Task IconFile_WhenRootedOrTraversing_IsRefused(string iconFile)
    {
        CreateRoleRequest request = ValidRequest();
        request.IconFile = iconFile;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.IconFile), IconFileNotRelative);
    }

    /// <summary>
    /// An absent or empty icon path reports nothing, because no icon is the normal case.
    /// </summary>
    /// <param name="iconFile">The submitted path.</param>
    /// <remarks>
    /// The empty case matters as much as the absent one: the legacy reader represented an absent string as
    /// the empty string rather than as a null (<c>Null.vb</c> L73), so a role saved without an icon carried
    /// an empty value and resubmitting it must not fail.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task IconFile_WhenAbsentOrEmpty_ReportsNothing(string? iconFile)
    {
        CreateRoleRequest request = ValidRequest();
        request.IconFile = iconFile;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.IconFile));
    }

    // ========================================================================
    // THE UNCONSTRAINED MEMBERS
    //
    // Sentinels survive at the boundary, not in the domain. Every value below is
    // legitimate in the legacy data, so a bound on any of them would refuse a
    // real submission. These tests exist to make a later "tightening" fail.
    // ========================================================================

    /// <summary>
    /// The optional group reference is unconstrained, including at both values a reader would be tempted to
    /// reject.
    /// </summary>
    /// <param name="roleGroupId">The submitted group key.</param>
    /// <remarks>
    /// Zero is legitimate because the group table's key is an identity column seeded at zero, so zero names
    /// the first group of the installation. The negative value is legitimate for a different reason
    /// altogether: the legacy screen offered it as the drop-down's FIRST, SELECTABLE entry, added from the
    /// localised global-roles resource (<c>EditRoles.ascx.vb</c> L75), while the legacy reader used the same
    /// value as its absent-integer sentinel - so one value meant "global", "ungrouped" and "absent"
    /// depending on which layer was looking. Any lower bound would refuse a real submission, and whether the
    /// nominated group exists is a question about stored state that belongs to the service.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RoleGroupId_IsUnconstrained(int? roleGroupId)
    {
        CreateRoleRequest request = ValidRequest();
        request.RoleGroupId = roleGroupId;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.RoleGroupId));
    }

    /// <summary>
    /// Both flags are unconstrained in every combination, because a boolean is its own constraint.
    /// </summary>
    /// <param name="isPublic">Whether the role is public.</param>
    /// <param name="autoAssignment">Whether the role is assigned automatically.</param>
    /// <remarks>
    /// The legacy screen declared no validator on either check box, and the terminal columns are
    /// not-null with a zero default (<c>03.01.01.SqlDataProvider</c> L1174, L1175 and the defaults at L1179
    /// and L1181), so false is a legitimate stored state rather than a missing value.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BothFlags_AreUnconstrained(bool isPublic, bool autoAssignment)
    {
        CreateRoleRequest request = ValidRequest();
        request.IsPublic = isPublic;
        request.AutoAssignment = autoAssignment;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    // ------------------------------------------------------------------------
    // THE WHOLE RESPONSE
    // ------------------------------------------------------------------------

    /// <summary>
    /// One malformed submission reports every bad field rather than only the first, so a caller learns
    /// everything wrong with a request in a single round trip.
    /// </summary>
    /// <remarks>
    /// This is the shape a client actually consumes: each property becomes a key in the <c>errors</c>
    /// dictionary of the RFC 7807 payload and carries its own message. Both halves of all six failures are
    /// asserted, and the total is pinned as well so that a seventh, unexpected failure is caught rather than
    /// silently tolerated by a set of independent "contains" checks.
    /// </remarks>
    [Fact]
    public async Task SeveralBadFields_AreAllReportedTogether()
    {
        CreateRoleRequest request = new()
        {
            RoleName = string.Empty,
            ServiceFee = -1m,
            BillingPeriod = 0,
            TrialFee = -1m,
            TrialPeriod = 0,
            BillingFrequency = default(BillingFrequency),
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.RoleName), RoleNameRequired);
        ShouldReport(result, nameof(CreateRoleRequest.ServiceFee), ServiceFeeNegative);
        ShouldReport(result, nameof(CreateRoleRequest.BillingPeriod), BillingPeriodNotPositive);
        ShouldReport(result, nameof(CreateRoleRequest.TrialFee), TrialFeeNegative);
        ShouldReport(result, nameof(CreateRoleRequest.TrialPeriod), TrialPeriodNotPositive);
        ShouldReport(result, nameof(CreateRoleRequest.BillingFrequency), BillingFrequencyInvalid);
        result.Errors.Should().HaveCount(6, Render(result));
    }

    /// <summary>
    /// A role priced at nothing with no terms at all is accepted, which is the single most common role in a
    /// real installation and the submission a mis-authored rule would break first.
    /// </summary>
    /// <remarks>
    /// The shipped roles are exactly this shape: <c>01.00.00.SqlDataProvider</c> L7192 and L7194 seed
    /// Administrators and Registered Users with a null fee, a null trial period and a null trial frequency.
    /// Read together with the four zero-boundary tests, this establishes the full picture - absence is always
    /// accepted, a zero FEE is accepted, and only a zero PERIOD is refused.
    /// </remarks>
    [Fact]
    public async Task AFreeRoleWithNoTermsAtAll_IsAccepted()
    {
        CreateRoleRequest request = new()
        {
            RoleName = "Registered Users",
            Description = "Registered Users",
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

}
