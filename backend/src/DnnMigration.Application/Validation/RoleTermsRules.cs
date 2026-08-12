using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Validation;

/// <summary>
/// The field rules shared by the security-role write contracts - column widths, the measured legacy
/// wording for each comparison, and the icon-path containment predicate - declared once and applied
/// by both the create and the update role validators.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, two callers.</b> A service fee submitted on create and a service fee submitted
/// on update are the same value bound for the same column, so a rule that held on one path and not
/// the other would be a rule a caller could bypass simply by choosing the other verb. That is not a
/// hypothetical: the containment rule on the icon path existed on the creation validator and was
/// absent from the update path, so a rooted or traversing path that <c>POST</c> refused was accepted
/// verbatim by <c>PUT</c> against the very same column. Declaring the rules here is what makes the
/// two paths provably identical rather than incidentally similar, and it follows the pattern this
/// folder already established for the portal alias contracts in
/// <c>Application/Validation/PortalAliasRules.cs</c>.
/// </para>
/// <para>
/// <b>This type shares rules, not contracts.</b> The two request contracts deliberately declare no
/// base type and no inheritance between them, for the reason recorded on each: a shared base would
/// present members neither contract owns, which is the readability cost the legacy
/// <c>UserRoleInfo : RoleInfo</c> pair demonstrates. Sharing a static rule definition preserves that
/// separation completely: neither contract gains a member, and each validator still chooses for itself
/// which of the rules below apply to the members it actually carries. The name rule is consumed by both
/// role validators, because both write the same <c>NOT NULL</c> column; the role-group validators
/// consume its message alone, because their column belongs to a different table and carries its own
/// width.
/// </para>
/// <para>
/// <b>Nothing here reads state, configuration or a clock.</b> Every member is a compile-time
/// constant or a pure character test, which is what keeps both validators parameterless. Questions
/// about stored state - portal-scoped name uniqueness on the creation path, whether the role exists
/// on the update path - are expected failures owned by <c>Application/Services/RoleService.cs</c>,
/// and the trial and billing date arithmetic is owned by the same service. None of it belongs to a
/// field rule.
/// </para>
/// <para>
/// <b>Message wording is legacy wording.</b> Each message below reproduces the <c>errormessage</c>
/// attribute of the corresponding validator on <c>Website/admin/Security/editroles.ascx</c> with its
/// leading markup tag removed, so an operator who knew the legacy screen reads the same sentence
/// from the migrated API. Two of them disagree with the operator they accompany; that disagreement
/// is preserved deliberately and is annotated on each constant rather than tidied away.
/// </para>
/// </remarks>
internal static class RoleTermsRules
{
    /// <summary>
    /// Wording of <c>valRoleName</c>, the screen's only presence check
    /// (<c>editroles.ascx</c> L31), with its leading markup tag removed.
    /// </summary>
    /// <remarks>
    /// Consumed by both role write validators, so a missing name reads the same sentence whichever verb
    /// was used. The legacy edit screen disabled this very validator at <c>EditRoles.ascx.vb</c>
    /// L131-L134 because it displayed the name read-only; the migrated update contract carries a writable
    /// name - a documented behavioural difference recorded on that contract - so the rule applies to it
    /// as well. The sibling role-group screen declared the identical wording at
    /// <c>EditGroups.ascx</c> L12, which is why its validator reads the constant from here rather than
    /// restating it.
    /// </remarks>
    internal const string RoleNameRequiredMessage = "You Must Enter a Valid Name";

    /// <summary>
    /// Wording of <c>DuplicateRole.Text</c> (<c>Website/admin/Security/App_LocalResources/EditRoles.ascx.resx</c>),
    /// reported when a submitted role name is already held within the tenant.
    /// </summary>
    /// <remarks>
    /// The legacy screen showed this sentence and nothing else - no identifier, no tenant, no repetition of
    /// the submitted name - and it is carried verbatim, trailing full stops included, because the
    /// behaviour-preservation obligation covers error wording as squarely as it covers outcomes. It lives
    /// beside the validator constants rather than inside the service that raises it so that the one place
    /// legacy role wording is transcribed stays one place.
    /// </remarks>
    internal const string DuplicateRoleMessage =
        "A role with the same name already exists. The role was not added.";

    /// <summary>
    /// Tests whether a submitted period is admissible beside the frequency submitted with it.
    /// </summary>
    /// <param name="period">The submitted period, or <see langword="null"/> when none was supplied.</param>
    /// <param name="frequency">
    /// The frequency submitted alongside it, or <see langword="null"/> when none was supplied.
    /// </param>
    /// <returns><see langword="true"/> when the pair is admissible.</returns>
    /// <remarks>
    /// <para>
    /// A period governs a RECURRING CYCLE, so what counts as a legal value depends on whether a cycle was
    /// declared at all. <c>valBillingPeriod2</c> (<c>editroles.ascx</c> L111-L114) and
    /// <c>valTrialPeriod2</c> (L143-L146) are both <c>Operator="GreaterThan" ValueToCompare="0"</c>, and the
    /// authoritative resource declares both messages as "Must Be Greater Than Zero", so a role that declares
    /// a cycle must state a positive number of units - a cycle of zero units could never advance an expiry
    /// date, and the derivation would answer a membership that lapsed the instant it was created. That rule
    /// is preserved exactly, and it is the rule the Angular form reproduces for every value an operator can
    /// type.
    /// </para>
    /// <para>
    /// MIGRATION: ZERO IS ADMITTED WHERE NO CYCLE IS DECLARED, WHICH THE LEGACY SCREEN NEVER HAD TO DECIDE.
    /// The legacy screen showed the period box empty for a role whose service fee formatted to "0.00"
    /// (<c>EditRoles.ascx.vb</c> L146-L148 fills it only otherwise) and wrote 1 rather than 0 on save
    /// (L213, L216-L218), so an operator could neither see nor submit the zero. The STORE holds it anyway:
    /// the portal template's own roles are created with <c>BillingPeriod</c> and <c>TrialPeriod</c> at 0
    /// beside a frequency of <c>N</c>, and this API's read projection reports those columns faithfully as
    /// Rule T7 requires. Refusing 0 unconditionally therefore made a value the API EMITS a value the API
    /// would not ACCEPT: runtime testing read a role, echoed its own response back verbatim, and was
    /// refused 400 on both period members - so no consumer could carry out a read-modify-write of any role
    /// the portal template had created. Admitting 0 only alongside <c>None</c> (or alongside no frequency at
    /// all, which the contract also reads as no cycle) closes that asymmetry without admitting the
    /// degenerate cycle the legacy validator existed to refuse.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Wording of <c>valServiceFee2</c> (<c>editroles.ascx</c> L95). Message and operator agree.
    /// </summary>
    internal const string ServiceFeeNegativeMessage =
        "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>
    /// Wording of <c>valBillingPeriod2</c> (<c>editroles.ascx</c> L113).
    /// </summary>
    /// <remarks>
    /// MIGRATION: THIS SENTENCE WAS MISTRANSCRIBED AND IS NOW THE LEGACY'S OWN. It previously read
    /// "Billing Period Must Be Greater Than or Equal to Zero", under a remark explaining that the
    /// legacy message disagreed with its own operator. The legacy resource says otherwise:
    /// <c>Website/admin/Security/App_LocalResources/EditRoles.ascx.resx</c> declares
    /// <c>valBillingPeriod2.Text</c> as "Billing Period Must Be Greater Than Zero", which AGREES with
    /// the <c>Operator="GreaterThan" ValueToCompare="0"</c> it accompanies. There was no legacy
    /// self-contradiction to preserve; the mismatch existed only here, and the explanation for it was
    /// written to account for a transcription error rather than to a measured source.
    ///
    /// It mattered in three ways at once. The sentence told an operator that zero was acceptable for a
    /// value the server then refused - measured on the wire: <c>PUT /api/v1/roles/2</c> carrying
    /// <c>billingPeriod: 0</c> answered <c>400</c> with this very message, advising that zero was
    /// allowed. It diverged from the legacy wording the migration discipline requires. And it
    /// disagreed with the browser, which had transcribed the same resource CORRECTLY as
    /// <c>BILLING_PERIOD_NOT_POSITIVE_MESSAGE</c> in <c>role-form.component.ts</c>, so the two sides
    /// described one rule in two different sentences. All three close with the legacy string.
    ///
    /// The OPERATOR is unchanged and remains strictly positive: a billing cycle of zero units could
    /// never advance an expiry date.
    /// </remarks>
    internal const string BillingPeriodNotPositiveMessage =
        "Billing Period Must Be Greater Than Zero";

    /// <summary>
    /// Wording of <c>valTrialFee2</c> (<c>editroles.ascx</c> L127).
    /// </summary>
    /// <remarks>
    /// MIGRATION: MISTRANSCRIBED IN THE OPPOSITE DIRECTION TO
    /// <see cref="BillingPeriodNotPositiveMessage"/>, and corrected the same way. It previously read
    /// "Trial Fee Must Be Greater Than Zero" while its operator admits zero, and the remark here
    /// explained that as inherited legacy inconsistency. The legacy resource declares
    /// <c>valTrialFee2.Text</c> as "Trial Fee Must Be Greater Than or Equal to Zero", which AGREES
    /// with its <c>Operator="GreaterThanEqual" ValueToCompare="0"</c>.
    ///
    /// So the legacy was internally consistent on ALL FOUR of these rules, and the two apparent
    /// contradictions were this file's own - the two strings had in effect been swapped between the
    /// period rule and the fee rule. This one told an operator that a free trial would be refused
    /// while the server accepted it, which is the same lie as the other constant told, pointing the
    /// other way. The browser again had the resource right, as
    /// <c>TRIAL_FEE_NEGATIVE_MESSAGE</c> in <c>role-form.component.ts</c>.
    ///
    /// The OPERATOR is unchanged and still admits zero: a free trial is a real configuration.
    /// </remarks>
    internal const string TrialFeeNegativeMessage =
        "Trial Fee Must Be Greater Than or Equal to Zero";

    /// <summary>
    /// Wording of <c>valTrialPeriod2</c> (<c>editroles.ascx</c> L145). Message and operator agree.
    /// </summary>
    internal const string TrialPeriodNotPositiveMessage = "Trial Period Must Be Greater Than Zero";

    /// <summary>
    /// Reported when a submitted billing frequency is not a member of the domain enumeration.
    /// </summary>
    internal const string BillingFrequencyInvalidMessage =
        "Billing Frequency must be one of None, One Time, Day, Week, Month or Year.";

    /// <summary>
    /// Reported when a submitted trial frequency is not a member of the domain enumeration.
    /// </summary>
    internal const string TrialFrequencyInvalidMessage =
        "Trial Frequency must be one of None, One Time, Day, Week, Month or Year.";

    /// <summary>
    /// Reported when a submitted icon path is rooted or traverses above the portal's own folder.
    /// </summary>
    /// <remarks>
    /// Aliased to <see cref="IconReferenceRules.NotContainedMessage"/> rather than restated, because the
    /// page-update path reports the same refusal and two copies of one sentence can drift apart.
    /// </remarks>
    internal const string IconFileNotRelativeMessage = IconReferenceRules.NotContainedMessage;

    /// <summary>
    /// Width of <c>Roles.RoleName nvarchar(50) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L117).
    /// </summary>
    internal const int RoleNameMaximumLength = 50;

    /// <summary>
    /// Width of <c>Roles.Description nvarchar(1000) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> L118).
    /// </summary>
    internal const int DescriptionMaximumLength = 1000;

    /// <summary>
    /// Width of <c>Roles.RSVPCode nvarchar(50) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45).
    /// </summary>
    internal const int RsvpCodeMaximumLength = 50;

    /// <summary>
    /// Width of <c>Roles.IconFile nvarchar(100) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45).
    /// </summary>
    internal const int IconFileMaximumLength = 100;

    /// <summary>
    /// Determines whether a submitted icon path stays inside the portal's own folder, accepting an
    /// absent or empty value.
    /// </summary>
    /// <param name="iconFile">The submitted path, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or a relative path that neither
    /// begins at a root nor traverses upwards; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Expressed as character tests rather than through the file-system APIs or a pattern-matching
    /// engine, both so that this layer touches neither and so that the check behaves identically
    /// whichever platform the API runs on - the legacy application stored Windows-style separators,
    /// while the migrated API runs on Linux, so both separators must be treated as rooting
    /// characters regardless of the host.
    /// </para>
    /// <para>
    /// The rule is net-new and is the only rule in this type without a legacy ancestor. The legacy
    /// screen declared no validator on its icon picker, restricting the choice by file type in the
    /// code-behind instead (<c>EditRoles.ascx.vb</c> L129) and storing whatever the control yielded
    /// verbatim (L248) - an affordance a JSON contract cannot reproduce, because a caller now
    /// supplies the string directly. An unbounded caller-supplied relative path reaching a stored
    /// column is a genuine gap rather than a matter of taste, and it must close on both write verbs
    /// or on neither.
    /// </para>
    /// </remarks>
    internal static bool IsContainedRelativePath(string? iconFile)
        // ONE IMPLEMENTATION, NOT TWO. The identical character tests were hoisted twice, once for the two
        // role write paths and once for the page update, and two copies of a containment rule are a
        // liability precisely because the property that matters is that every path judges a reference the
        // SAME way. This member survives as the name the role validators already use and delegates, so the
        // rule cannot be changed on one surface and missed on another.
        => IconReferenceRules.IsContained(iconFile);

    /// <summary>
    /// Shortest invitation code an AUTHORED or ROTATED value may be: 12 characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SEC: AN INVITATION CODE IS A SHARED SECRET THAT GRANTS ROLE MEMBERSHIP, and it was bounded only above.
    /// A one-character code was accepted, stored, and thereafter redeemable by anybody who submitted that
    /// character - a role grant, including a private, paid or permission-bearing role, reachable by
    /// enumerating an alphabet. Twelve characters over a mixed alphabet is beyond exhaustive submission at any
    /// rate a bounded endpoint permits, and it is well inside the column's fifty.
    /// </para>
    /// <para>
    /// It is deliberately not larger. The value is typed by a person from something they were handed, and a
    /// bound that makes an issuer paste a long opaque string invites them to reuse one code everywhere
    /// instead - which is the failure this rule exists to avoid, arrived at from the other direction.
    /// </para>
    /// </remarks>
    internal const int RsvpCodeMinimumAuthoredLength = 12;

    /// <summary>
    /// Reported when a newly authored or rotated invitation code is too easily guessed.
    /// </summary>
    /// <remarks>
    /// The sentence states the rule rather than the reason: an issuer correcting a refusal needs to know what
    /// to type, and explaining that a short code can be enumerated tells an attacker reading the same message
    /// what the endpoint's weakness used to be. It names the field the way the field is labelled, as the
    /// redemption refusal does.
    /// </remarks>
    internal const string RsvpCodeTooWeakMessage =
        "An RSVP Code must be at least 12 characters long and must mix letters with digits or punctuation.";

    /// <summary>
    /// Determines whether an invitation code is strong enough to be AUTHORED, accepting an absent or empty
    /// value.
    /// </summary>
    /// <param name="rsvpCode">The submitted code, which may be <see langword="null"/> or empty.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or at least
    /// <see cref="RsvpCodeMinimumAuthoredLength"/> characters carrying at least two of the three character
    /// classes; <see langword="false"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// AN ABSENT OR EMPTY VALUE PASSES, and that is the rule rather than a gap in it. The column is nullable
    /// and the legacy screen left the box empty for a service that is not invitation-only, so clearing the
    /// code is how an issuer WITHDRAWS one - refusing an empty value would make an existing code impossible
    /// to remove. The legacy string sentinel was itself the empty string, so a caller sending <c>null</c> and
    /// a caller sending <c>""</c> mean the same thing and both mean "no code".
    /// </para>
    /// <para>
    /// ⚠ THIS RULE APPLIES TO AUTHORING ONLY, AND REDEMPTION IS DELIBERATELY LEFT ALONE. Codes already stored
    /// by the legacy application are short and weak by construction - it declared no rule at all - and
    /// tightening the REDEMPTION contract would make every one of them unredeemable, which is a functional
    /// regression for accounts holding a code they were legitimately given. The asymmetry IS the migration
    /// path: existing codes keep working until an issuer next edits the role, at which point the new value
    /// must be a strong one. The divergence is recorded in <c>MIGRATION_NOTES.md</c> per Rule T5.
    /// </para>
    /// <para>
    /// TWO OF THREE CLASSES RATHER THAN ALL THREE, and length carrying most of the weight. Requiring every
    /// class of character produces codes people mistype and issuers work around; the length bound is what
    /// makes the value unguessable, and the class requirement exists only to refuse the degenerate cases a
    /// length bound alone admits - twelve repetitions of one letter, or a twelve-digit number that is somebody
    /// 's telephone number. Expressed as character tests rather than through a pattern-matching engine, so
    /// this rule cannot become a pattern whose evaluation time depends on caller-supplied input.
    /// </para>
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

        // A letter alone, a digit alone or punctuation alone is one class and is refused; any two of the three
        // together pass. Counted rather than tested pairwise so a fourth class could never be added to the
        // loop above and quietly leave this expression describing three.
        int classes = (hasLetter ? 1 : 0) + (hasDigit ? 1 : 0) + (hasOther ? 1 : 0);

        return classes >= 2;
    }
}
