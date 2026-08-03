using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="RoleAssignmentRequest"/>, the body of
/// <c>POST /api/v1/portals/{portalId}/roles/{roleId}/users</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> The assignment action advertised a field-error response but no
/// <see cref="IValidator{T}"/> resolved for its body, so a submission naming no user, or naming an
/// expiry that falls before its own effective date, passed the boundary untested. The first produced a
/// foreign-key failure on <c>dbo.UserRoles</c>; the second stored an already-lapsed membership without
/// complaint, which the legacy screen would have refused before posting. Both rules below are the
/// screen's own.
/// </para>
/// <para>
/// <b>Two rules, because the screen declared two that survive translation.</b> The date range check is
/// carried across as a cross-field rule. The user reference gains a positive-value rule, which is the
/// faithful equivalent of the screen's insistence on a real drop-down selection. The screen's two
/// remaining validators - the date type checks - deliberately have no counterpart, for the reason
/// annotated below.
/// </para>
/// <para>
/// <b>What this type deliberately does not do.</b> It reads no store, so it asserts neither that the
/// named user exists, nor that the user belongs to this portal, nor that the role does, nor that the
/// membership is not already present. Every one of those is a question about stored state and an
/// expected failure owned by <c>Application/Services/RoleService.cs</c> - and the tenant questions in
/// particular are a cross-tenant boundary that a field rule could not enforce even in principle,
/// because the portal arrives in the route and this type never sees it. It also computes no dates: a
/// null expiry means "derive one from the role's trial and billing terms", and that derivation is the
/// service's.
/// </para>
/// </remarks>
// MIGRATION: both rules are measured from Website/admin/Security/securityroles.ascx, and the file must
// be read case-insensitively - the markup writes its tags in lower case, so a search for
// "CompareValidator" returns nothing while "comparevalidator" returns all three. L45 declares
// valEffectiveDate, L46 valExpiryDate and L47 valDates.
//
// MIGRATION: the date range rule is valDates (L47) reproduced exactly. Its attributes are
// operator="GreaterThan", controltovalidate="txtExpiryDate" and controltocompare="txtEffectiveDate",
// so the assertion is that the EXPIRY is strictly greater than the EFFECTIVE date - not the reverse,
// and not "greater than or equal". Equality is therefore refused: a window that opens and closes at
// the same instant is a membership that is never in force, and the legacy screen refused it. Its
// errormessage, "<br>Expiry Date must be Greater than Effective Date", is reproduced below with the
// leading markup tag removed.
//
// MIGRATION: the range rule fires only when BOTH dates are present, which is ASP.NET validator
// semantics rather than an added convenience. A CompareValidator treats an empty control as valid and
// leaves emptiness to a RequiredFieldValidator, and neither date box carried one - the code-behind
// confirms the intent by substituting the null sentinel for an empty box at
// Website/admin/Security/SecurityRoles.ascx.vb L529-L539. So an open-ended window in either direction
// was legal and must stay legal. The rule is attached to the expiry member so the failure is reported
// against the field the legacy screen highlighted, which is the one the operator would correct.
//
// MIGRATION: the two DataTypeCheck comparisons, valEffectiveDate (L45) and valExpiryDate (L46), have
// no counterpart and are intentionally absent rather than overlooked. They existed because a text box
// submits text, so "13/45/2008" had to be rejected before Date.Parse could be reached at
// SecurityRoles.ascx.vb L530 and L536. The migrated members are nullable DateTime values, so a
// malformed instant now fails deserialisation at the API edge and is reported against the same member;
// reproducing the check here would assert what the type system has already guaranteed.
//
// MIGRATION: the user rule is the faithful translation of a control affordance, not of a validator.
// The screen offered no validator on its user selector, because a drop-down populated from stored rows
// (cboUsers at securityroles.ascx L26) could only ever yield a real identifier, and the screen's own
// marker for "nothing selected" was the sentinel -1 at L53 of its code-behind. A JSON caller has no
// such constraint and supplies the number directly, so the affordance must become a rule or it
// disappears. The bound is strictly positive because dbo.Users.UserID is seeded IDENTITY(1,1) at
// 01.00.00.SqlDataProvider L98: the first user ever created is 1, so no legitimate identifier is zero
// or negative, and both the type's default and the legacy sentinel are refused by the same test.
//
// MIGRATION: that positive bound is specific to THIS member and must never be generalised across the
// codebase. The request contract records the trap explicitly: dbo.Portals.PortalID is IDENTITY(-1,1)
// and dbo.Roles.RoleID is IDENTITY(0,1), so a "less than or equal to zero means absent" shortcut is
// wrong for a sibling identifier on this endpoint's own route. The bound is justified here by the
// Users table's own seed and by nothing more general than that.
//
// MIGRATION: no rule on the notification flag. It is a boolean, so it is its own constraint, and no
// column backs it - the legacy check box defaulted to checked (securityroles.ascx, chkNotify) and fed
// a mail decision at SecurityRoles.ascx.vb L542, which is behaviour the service owns.
//
// MIGRATION: the two dates also carry a REPRESENTABILITY bound, and it is the one rule here with no legacy
// counterpart at all. The CLR date type begins in the year one while the stored column begins in 1753, so a
// value the type accepts can still be unstorable; unbounded, such a value satisfied every rule above and was
// refused by the provider instead - a server fault naming no field, where the caller needs to be told which
// date it cannot use. It changes no outcome, only the shape of an existing refusal, which is why it sits
// beside the measured rules rather than in tension with them.
//
// MIGRATION: the upper bound is stated at the last representable instant of 9999-12-31 rather than at that
// day's midnight, and that is load-bearing rather than incidental. The request contract preserves the literal
// 9999-12-31 as an ordinary stored value meaning "no expiry" - distinct from the sentinel minimum date, which
// is carried as null - and it must round-trip verbatim. A bound stated at midnight would refuse the very
// value the contract exists to preserve.
//
// MIGRATION: no rule refusing a past effective date, and none refusing a past expiry. Backdating was
// legal on the legacy screen - the operator typed any date the calendar popup produced - and the
// membership window is evaluated in SQL as (EffectiveDate <= getdate() or EffectiveDate is null), so a
// past effective date simply means "already in force". Refusing either would make a correction to a
// historical assignment impossible.
public class RoleAssignmentRequestValidator : AbstractValidator<RoleAssignmentRequest>
{
    /// <summary>
    /// Reported when the submitted user reference is not a usable identifier. The legacy screen
    /// expressed the same requirement through a drop-down that could only offer real users, so this
    /// wording is net-new; no <c>errormessage</c> attribute exists to reproduce.
    /// </summary>
    private const string UserIdNotPositiveMessage = "A user must be selected.";

    /// <summary>
    /// Wording of <c>valDates</c> (<c>securityroles.ascx</c> L47), with its leading markup tag
    /// removed. Message and operator agree: the comparison is strictly greater than.
    /// </summary>
    private const string ExpiryNotAfterEffectiveMessage =
        "Expiry Date must be Greater than Effective Date";

    /// <summary>
    /// Reported when a submitted instant falls outside the range the terminal <c>datetime</c> column can
    /// hold. The wording is net-new: the legacy screen had no such rule and therefore no message to
    /// reproduce.
    /// </summary>
    private static readonly string DateUnrepresentableMessage = FormattableString.Invariant($"A date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    /// <summary>
    /// Initialises a new instance of the <see cref="RoleAssignmentRequestValidator"/> class and
    /// declares its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member; class-level cascade continues, so a
    /// submission that both names no user and inverts its dates reports both faults in one response
    /// rather than one per round trip.
    /// </remarks>
    public RoleAssignmentRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // The drop-down affordance expressed as a rule: dbo.Users.UserID is seeded IDENTITY(1,1), so
        // a usable identifier is strictly positive. This refuses both an omitted member, which binds
        // as zero, and the legacy "nothing selected" sentinel of -1.
        RuleFor(request => request.UserId)
            .GreaterThan(0)
            .WithMessage(UserIdNotPositiveMessage);

        // valDates (securityroles.ascx L47): the expiry must be strictly LATER than the effective
        // date. Conditional on both being present, which is the legacy validator's own behaviour -
        // an open-ended window in either direction was legal and stays legal.
        RuleFor(request => request.ExpiryDate)
            .GreaterThan(request => request.EffectiveDate!.Value)
            .WithMessage(ExpiryNotAfterEffectiveMessage)
            .When(request => request.EffectiveDate.HasValue && request.ExpiryDate.HasValue);

        // Representability, stated over the OPTIONAL member rather than over its unwrapped value so that
        // absence passes the rule directly and the failing field a caller reads back is the one it
        // submitted rather than "EffectiveDate.Value".
        RuleFor(request => request.EffectiveDate)
            .Must(BeStorableOrTheLegacyAbsenceMarker)
            .WithMessage(DateUnrepresentableMessage);

        RuleFor(request => request.ExpiryDate)
            .Must(BeStorableOrTheLegacyAbsenceMarker)
            .WithMessage(DateUnrepresentableMessage);
    }

    /// <summary>
    /// Determines whether a submitted bound is one the terminal <c>datetime</c> column can hold, admitting
    /// the legacy absent-date marker as well as absence itself.
    /// </summary>
    /// <param name="bound">The submitted bound, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the bound is absent, is the legacy absent-date marker, or falls inside
    /// the storable calendar; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// THE MARKER CARVE-OUT IS NOT A WEAKENING OF THE BOUND, and leaving it out would have broken a
    /// submission this application deliberately supports. <c>Null.NullDate</c> is <c>Date.MinValue</c>
    /// (<c>Null.vb</c> lines 66-70), which falls nearly eight centuries before the column's first instant, so
    /// a plain representability test refuses it - yet a caller that copied a bound out of a legacy object
    /// submits exactly that value to mean "no date", and
    /// <c>Application/Services/RoleService.cs</c> reads it as absence before deriving anything from it. The
    /// marker therefore never reaches the column at all, and refusing it here would refuse a value the layer
    /// below is documented to interpret rather than store.
    /// </para>
    /// <para>
    /// The comparison is on the DATE PART alone, matching the service's own test and the legacy emptiness
    /// tests it reproduces (<c>Null.vb</c> lines 183-186 and 222-224, both comparing <c>.Date</c> against
    /// <c>NullDate.Date</c> under the source comment "this avoids subtle time differences"). A copied marker
    /// may carry a time component, and an exact-equality test would let the service interpret a value this
    /// rule had refused - or refuse one it would have interpreted.
    /// </para>
    /// <para>
    /// Nothing else is admitted. A value one day before the column's first instant is still refused, which is
    /// what keeps this a representability bound rather than an absent one.
    /// </para>
    /// </remarks>
    private static bool BeStorableOrTheLegacyAbsenceMarker(DateTime? bound)
        => bound is not DateTime value
            || value.Date == DateTime.MinValue.Date
            || SqlServerRange.CanStore(value);
}
