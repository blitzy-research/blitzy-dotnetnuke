using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Validation;

/// <summary>
/// The field rules shared by the security-role write contracts - column widths, the measured legacy wording
/// for each comparison, and the icon-path containment predicate - declared once and applied by both the
/// create and the update role validators.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, two callers.</b> A service fee submitted on create and a service fee submitted on
/// update are the same value bound for the same column, so a rule that held on one path and not the other
/// would be a rule a caller could bypass simply by choosing the other verb.
/// </para>
/// <para>
/// <b>This type shares rules, not contracts.</b> The two request contracts deliberately declare no base
/// type and no inheritance between them, for the reason recorded on each: a shared base would present
/// members neither contract owns, which is the readability cost the legacy <c>UserRoleInfo: RoleInfo</c>
/// pair demonstrates.
/// </para>
/// </remarks>
internal static class RoleTermsRules
{
    /// <summary>
    /// Wording of <c>valRoleName</c>, the screen's only presence check, with its leading markup tag
    /// removed.
    /// </summary>
    /// <remarks>
    /// Consumed by both role write validators, so a missing name reads the same sentence whichever verb was
    /// used. The legacy edit screen disabled this very validator at <c>EditRoles.ascx.vb</c> L131-L134
    /// because it displayed the name read-only; the migrated update contract carries a writable name - a
    /// documented behavioural difference recorded on that contract - so the rule applies to it as well.
    /// </remarks>
    internal const string RoleNameRequiredMessage = "You Must Enter a Valid Name";

    /// <summary>
    /// Wording of <c>DuplicateRole.Text</c>, reported when a submitted role name is already held within the
    /// tenant.
    /// </summary>
    internal const string DuplicateRoleMessage =
        "A role with the same name already exists. The role was not added.";

    /// <summary>Tests whether a submitted period is admissible beside the frequency submitted with it.</summary>
    /// <param name="period">The submitted period, or <see langword="null"/> when none was supplied.</param>
    /// <param name="frequency">
    /// The frequency submitted alongside it, or <see langword="null"/> when none was supplied.
    /// </param>
    /// <returns><see langword="true"/> when the pair is admissible.</returns>
    internal static bool IsPeriodAdmissibleForFrequency(int? period, BillingFrequency? frequency)
    {
        if (period is not int units)
        {
            return true;
        }

        if (units > 0)
        {
            return true;
        }

        // A negative period is refused whatever the frequency: it is neither a cycle length nor the store's
        // no-cycle value, and the legacy operator refused it too.
        return units == 0 && frequency is null or BillingFrequency.None;
    }

    /// <summary>Wording of <c>valServiceFee2</c>. Message and operator agree.</summary>
    internal const string ServiceFeeNegativeMessage =
        "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>Wording of <c>valBillingPeriod2</c>.</summary>
    internal const string BillingPeriodNotPositiveMessage =
        "Billing Period Must Be Greater Than Zero";

    /// <summary>Wording of <c>valTrialFee2</c>.</summary>
    internal const string TrialFeeNegativeMessage =
        "Trial Fee Must Be Greater Than or Equal to Zero";

    /// <summary>Wording of <c>valTrialPeriod2</c>. Message and operator agree.</summary>
    internal const string TrialPeriodNotPositiveMessage = "Trial Period Must Be Greater Than Zero";

    /// <summary>Reported when a submitted billing frequency is not a member of the domain enumeration.</summary>
    internal const string BillingFrequencyInvalidMessage =
        "Billing Frequency must be one of None, One Time, Day, Week, Month or Year.";

    /// <summary>Reported when a submitted trial frequency is not a member of the domain enumeration.</summary>
    internal const string TrialFrequencyInvalidMessage =
        "Trial Frequency must be one of None, One Time, Day, Week, Month or Year.";

    /// <summary>Reported when a submitted icon path is rooted or traverses above the portal's own folder.</summary>
    internal const string IconFileNotRelativeMessage = IconReferenceRules.NotContainedMessage;

    /// <summary>
    /// Width of <c>Roles.RoleName nvarchar(50) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> L117).
    /// </summary>
    internal const int RoleNameMaximumLength = 50;

    /// <summary>
    /// Width of <c>Roles.Description nvarchar(1000) NULL</c> (<c>01.00.00.SqlDataProvider</c> L118).
    /// </summary>
    internal const int DescriptionMaximumLength = 1000;

    /// <summary>Width of <c>Roles.RSVPCode nvarchar(50) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45).</summary>
    internal const int RsvpCodeMaximumLength = 50;

    /// <summary>Width of <c>Roles.IconFile nvarchar(100) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45).</summary>
    internal const int IconFileMaximumLength = 100;

    /// <summary>
    /// Determines whether a submitted icon path stays inside the portal's own folder, accepting an absent
    /// or empty value.
    /// </summary>
    /// <param name="iconFile">The submitted path, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or a relative path that neither begins at a
    /// root nor traverses upwards; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The rule is net-new and is the only rule in this type without a legacy ancestor. The legacy screen
    /// declared no validator on its icon picker, restricting the choice by file type in the code-behind
    /// instead and storing whatever the control yielded verbatim - an affordance a JSON contract cannot
    /// reproduce, because a caller now supplies the string directly.
    /// </remarks>
    internal static bool IsContainedRelativePath(string? iconFile)
        => IconReferenceRules.IsContained(iconFile);

    /// <summary>Shortest invitation code an AUTHORED or ROTATED value may be: 12 characters.</summary>
    /// <remarks>
    /// SEC: AN INVITATION CODE IS A SHARED SECRET THAT GRANTS ROLE MEMBERSHIP, and it was bounded only
    /// above. A one-character code was accepted, stored, and thereafter redeemable by anybody who submitted
    /// that character - a role grant, including a private, paid or permission-bearing role, reachable by
    /// enumerating an alphabet.
    /// </remarks>
    internal const int RsvpCodeMinimumAuthoredLength = 12;

    /// <summary>Reported when a newly authored or rotated invitation code is too easily guessed.</summary>
    internal const string RsvpCodeTooWeakMessage =
        "An RSVP Code must be at least 12 characters long and must mix letters with digits or punctuation.";

    /// <summary>
    /// Determines whether an invitation code is strong enough to be AUTHORED, accepting an absent or empty
    /// value.
    /// </summary>
    /// <param name="rsvpCode">The submitted code, which may be <see langword="null"/> or empty.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or at least <see
    /// cref="RsvpCodeMinimumAuthoredLength"/> characters carrying at least two of the three character
    /// classes; <see langword="false"/> otherwise.
    /// </returns>
    /// <remarks>
    /// ⚠ THIS RULE APPLIES TO AUTHORING ONLY, AND REDEMPTION IS DELIBERATELY LEFT ALONE. Codes already
    /// stored by the legacy application are short and weak by construction - it declared no rule at all -
    /// and tightening the REDEMPTION contract would make every one of them unredeemable, which is a
    /// functional regression for accounts holding a code they were legitimately given.
    /// </remarks>
    internal static bool IsStrongAuthoredRsvpCode(string? rsvpCode)
    {
        if (string.IsNullOrEmpty(rsvpCode))
        {
            return true;
        }

        if (rsvpCode.Length < RsvpCodeMinimumAuthoredLength)
        {
            return false;
        }

        bool hasLetter = false;
        bool hasDigit = false;
        bool hasOther = false;

        foreach (char character in rsvpCode)
        {
            if (char.IsLetter(character))
            {
                hasLetter = true;
            }
            else if (char.IsDigit(character))
            {
                hasDigit = true;
            }
            else
            {
                hasOther = true;
            }
        }

        int classes = (hasLetter ? 1 : 0) + (hasDigit ? 1 : 0) + (hasOther ? 1 : 0);

        return classes >= 2;
    }
}
