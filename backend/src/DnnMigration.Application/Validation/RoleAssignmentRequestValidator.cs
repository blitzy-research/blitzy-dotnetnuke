using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="RoleAssignmentRequest"/>, the body of <c>POST
/// /api/v1/roles/{roleId}/users</c>.
/// </summary>
/// <remarks>
/// <b>Two rules, because the screen declared two that survive translation.</b> The date range check is
/// carried across as a cross-field rule. The user reference gains a positive-value rule, which is the
/// faithful equivalent of the screen's insistence on a real drop-down selection.
/// </remarks>
// Both rules are measured from Website/admin/Security/securityroles.ascx, and the file must be read
// case-insensitively - the markup writes its tags in lower case, so a search for "CompareValidator" returns
// nothing while "comparevalidator" returns all three.
public class RoleAssignmentRequestValidator : AbstractValidator<RoleAssignmentRequest>
{
    /// <summary>
    /// Reported when the submitted user reference is not a usable identifier. The legacy screen expressed
    /// the same requirement through a drop-down that could only offer real users, so this wording is
    /// net-new; no <c>errormessage</c> attribute exists to reproduce.
    /// </summary>
    private const string UserIdNotPositiveMessage = "A user must be selected.";

    /// <summary>
    /// Wording of <c>valDates</c>, with its leading markup tag removed. Message and operator agree: the
    /// comparison is strictly greater than.
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
    /// Initialises a new instance of the <see cref="RoleAssignmentRequestValidator"/> class and declares
    /// its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member; class-level cascade continues, so a
    /// submission that both names no user and inverts its dates reports both faults in one response rather
    /// than one per round trip.
    /// </remarks>
    public RoleAssignmentRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // The drop-down affordance expressed as a rule: dbo.Users.UserID is seeded IDENTITY(1,1), so a
        // usable identifier is strictly positive. This refuses both an omitted member, which binds as zero,
        // and the legacy "nothing selected" sentinel of -1.
        RuleFor(request => request.UserId)
            .GreaterThan(0)
            .WithMessage(UserIdNotPositiveMessage);

        RuleFor(request => request.ExpiryDate)
            .GreaterThan(request => request.EffectiveDate!.Value)
            .WithMessage(ExpiryNotAfterEffectiveMessage)
            .When(request => request.EffectiveDate.HasValue && request.ExpiryDate.HasValue);

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
    /// Nothing else is admitted. A value one day before the column's first instant is still refused, which
    /// is what keeps this a representability bound rather than an absent one.
    /// </remarks>
    private static bool BeStorableOrTheLegacyAbsenceMarker(DateTime? bound)
        => bound is not DateTime value
            || value.Date == DateTime.MinValue.Date
            || SqlServerRange.CanStore(value);
}
