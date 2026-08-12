using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="RedeemServiceCodeRequest"/>, the invitation code submitted to
/// <c>POST /api/v1/users/{userId}/services/redemptions</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, and both are measured rather than invented.
/// </para>
/// <para>
/// MIGRATION: requiring a non-empty code preserves a load-bearing legacy guard while turning silence into
/// an answer. The legacy handler wrapped its entire body in <c>If code &lt;&gt; "" Then</c>
/// (<c>Website/admin/Users/MemberServices.ascx.vb:L403</c>), so an empty box produced no subscription and
/// no message whatever - the account could not tell a rejected code from an unread one. The guard could not
/// simply be dropped, because an absent <c>Roles.RSVPCode</c> reached the comparison at <c>:L411</c> as
/// <c>Null.NullString</c>, which is the EMPTY STRING rather than null (<c>Null.vb:L66-L75</c>): an empty
/// submission would therefore have matched every role in the tenant that carries no code at all and
/// enrolled the account in all of them. So the guard is preserved and its silence is replaced by a
/// field-level failure.
/// </para>
/// <para>
/// FluentValidation's emptiness rule treats a whitespace-only string as empty, which is stricter than the
/// legacy inequality and deliberately so: whitespace could never match a real code, and the alternative -
/// reading every role in the tenant in order to answer "no match" - spends a query to reach a foregone
/// conclusion. The service repeats the same guard so it holds for a caller that reached it without this
/// pipeline in front of it.
/// </para>
/// <para>
/// The length bound is the stored column's own width, <c>Roles.RSVPCode nvarchar(50) NULL</c>, taken from
/// the shared role-terms constants rather than spelled again here. A longer value cannot match any stored
/// code, so naming the offending field is strictly more useful than a successful "no match". It is the same
/// bound the role editor applies to the value when it is WRITTEN
/// (<c>UpdateRoleRequestValidator.cs:L156-L157</c>), which is what keeps a redeemable code and a storable
/// one the same set.
/// </para>
/// <para>
/// The value is NOT trimmed and its case is NOT folded, here or in the service. The legacy comparison was a
/// Visual Basic string equality in memory with no <c>Option Compare Text</c> in the file, so it compared
/// ordinally and a leading space prevented a match. Normalising the submission would let a code match a
/// role its issuer did not intend, which is a widening of an access grant rather than a convenience.
/// </para>
/// <para>
/// Nothing here performs a lookup: whether any role bears the code is a question about state, and the
/// service answers it. The constructor is therefore parameterless, and no message reveals whether a code
/// exists.
/// </para>
/// </remarks>
public class RedeemServiceCodeRequestValidator : AbstractValidator<RedeemServiceCodeRequest>
{
    /// <summary>
    /// Reported when the submission carries no code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wording recovered from the legacy help text, which called the value an "RSVP Code"
    /// (<c>plRSVPCode.Text</c> in <c>Website/admin/Users/App_LocalResources/MemberServices.ascx.resx</c>
    /// reads "Enter RSVP Code:"). The message says what is missing and nothing about what would have
    /// matched.
    /// </para>
    /// <para>
    /// MIGRATION: this sentence NAMES THE FIELD THE WAY THE FIELD IS LABELLED, and that is the whole
    /// point of it. It previously read "An invitation code is required." while the control above it read
    /// "Enter RSVP Code:", so one value carried two names and an operator had to infer that the refusal
    /// was even about the box they had just filled in. The mismatch contradicted the very remark above,
    /// which had already recorded the legacy name as "RSVP Code". Nothing in the legacy forced either
    /// wording - the legacy guard was SILENT and had no message at all to recover - so the field's own
    /// label is the only authority available, and both sides now follow it. The browser copy is held at
    /// <c>member-services.component.ts</c> and is kept word-for-word identical, so the same situation is
    /// never described two different ways depending on which side noticed it.
    /// </para>
    /// </remarks>
    internal const string CodeRequiredMessage = "An RSVP Code is required.";

    /// <summary>
    /// Initialises a new instance of the <see cref="RedeemServiceCodeRequestValidator"/> class and
    /// declares its rules.
    /// </summary>
    public RedeemServiceCodeRequestValidator()
    {
        // The emptiness message is authored because the legacy screen had none to recover: its guard was
        // silent. The length rule carries the framework's own message, matching every sibling width rule
        // in this folder - the role editor's own invitation-code rule at
        // UpdateRoleRequestValidator.cs:L156-L157 declares the same bound the same way.
        //
        // SEC: NO STRENGTH RULE IS DECLARED HERE, AND ITS ABSENCE IS THE MIGRATION PATH RATHER THAN AN
        // OVERSIGHT. Guessing an invitation code is bounded by how fast a caller may try and by how large
        // the space is. The rate is bounded at the endpoint, by a limiter of its own partitioned on the
        // account and the client address (RateLimitingExtensions.RedemptionPolicyName). The space is bounded
        // on the WRITE path, where RoleTermsRules.IsStrongAuthoredRsvpCode refuses a newly authored or
        // rotated code that is short or drawn from one character class. Repeating that rule HERE would bound
        // nothing further - a caller guessing a short code is guessing one that already exists in the store -
        // and it would refuse the legitimate members of every role whose code was issued before the rule
        // existed. Existing codes therefore stay redeemable and the editor requires a stronger one the next
        // time the role is saved. Both directions are pinned by
        // RedeemServiceCodeRequestValidatorTests.Code_StillRedeemsWhenItIsWeakerThanTheEditorWouldNowAuthor.
        RuleFor(request => request.Code)
            .NotEmpty()
            .WithMessage(CodeRequiredMessage)
            .MaximumLength(RoleTermsRules.RsvpCodeMaximumLength);
    }
}
