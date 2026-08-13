// MIGRATION: enablePasswordRetrieval="true" is deliberately not carried forward, so this type exposes no
// retrieval switch and no storage-format selector.

using System.Text.RegularExpressions;
using DnnMigration.Application.Validation;

namespace DnnMigration.Application.Options;

/// <summary>
/// The password policy in force for the migrated application, preserved verbatim from the legacy DotNetNuke
/// installation and bound from the configuration section named by <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// The one method it does declare, <see cref="Validate"/>, reports whether the POLICY ITSELF is coherent -
/// not whether any password satisfies it.
/// </para>
/// <para>
/// <b>Deliberately absent - the maximum credential length.</b> Every rule on this type is legacy policy
/// that a deployment may legitimately vary, which is exactly why it is bindable. The upper bound on a
/// submitted credential is neither of those things: it is net-new, it is a security bound rather than a
/// policy preference, and a maximum that configuration can raise provides no guarantee at all.
/// </para>
/// </remarks>
public sealed class PasswordPolicyOptions
{
    /// <summary>The name of the configuration section this type is bound from: <c>PasswordPolicy</c>.</summary>
    /// <remarks>
    /// The Api layer resolves the section by this constant rather than by a repeated string literal, so the
    /// section name is stated in exactly one place. Individual keys are overridable per environment in the
    /// double-underscore form - generally <c>PasswordPolicy__&lt;PropertyName&gt;</c>, for example
    /// <c>PasswordPolicy__MinRequiredPasswordLength</c>.
    /// </remarks>
    public const string SectionName = "PasswordPolicy";

    // Policy bounds on the policy itself. These constrain what a DEPLOYMENT may configure; they are not
    // password rules and they are not applied to any credential.

    /// <summary>
    /// Floor beneath which <see cref="MinRequiredPasswordLength"/> may not be configured: <c>7</c>, the
    /// measured legacy value.
    /// </summary>
    /// <remarks>
    /// The guidance below forbids <em>raising</em> the minimum, because that would lock out users the
    /// legacy installation already accepted. This constant closes the opposite direction: lowering it below
    /// the legacy figure would accept credentials the legacy installation rejected, which is a security
    /// regression rather than migration fidelity.
    /// </remarks>
    public const int MinimumConfigurablePasswordLength = 7;

    /// <summary>Longest acceptable <see cref="PasswordStrengthRegularExpression"/> pattern: 512 characters.</summary>
    /// <remarks>
    /// The pattern is compiled once and then evaluated against every submitted credential, so its cost is
    /// paid on an unauthenticated path. A ceiling keeps that cost bounded and is far above any legitimate
    /// strength pattern - the legacy installation shipped none at all.
    /// </remarks>
    public const int MaximumPasswordStrengthRegularExpressionLength = 512;

    // The six values below are the legacy policy preserved verbatim - 7, 0, false, false, true and empty -
    // and they are deliberately NOT tightened.

    /// <summary>The minimum number of characters a password must contain. Defaults to <c>7</c>.</summary>
    /// <remarks>
    /// <b>Do not raise this value.</b> The policy is preserved verbatim precisely because tightening it
    /// during the migration would reject the credentials of users who are already registered.
    /// </remarks>
    public int MinRequiredPasswordLength { get; set; } = 7;

    /// <summary>
    /// The minimum number of non-alphanumeric characters a password must contain. Defaults to <c>0</c>.
    /// </summary>
    public int MinRequiredNonAlphanumericCharacters { get; set; } = 0;

    /// <summary>
    /// Whether the credential store requires a security question and answer. Defaults to <see
    /// langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Measured from <c>requiresQuestionAndAnswer="false"</c> at <c>Website/release.config:L241</c> and
    /// mirrors the legacy facade property at <c>MembershipProviderConfig.vb:L224</c>.
    /// </remarks>
    public bool RequiresQuestionAndAnswer { get; set; } = false;

    /// <summary>
    /// Whether an email address must be unique across the membership store. Defaults to <see
    /// langword="false"/>.
    /// </summary>
    public bool RequiresUniqueEmail { get; set; } = false;

    /// <summary>
    /// Whether a password may be reset, that is replaced with a newly issued credential. Defaults to <see
    /// langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Measured from <c>enablePasswordReset="true"</c> at <c>Website/release.config:L240</c> and mirrors
    /// the legacy facade property at <c>MembershipProviderConfig.vb:L164</c>.
    /// </remarks>
    public bool PasswordResetEnabled { get; set; } = true;

    // The legacy strength check assigned where it should have accumulated UserController.vb:L1086 reads
    // "isValid = rx.IsMatch(password)", discarding the length and non-alphanumeric results computed above
    // it, so a password that had already failed could still be declared valid.

    /// <summary>
    /// An optional pattern that a password must match in full. Defaults to <c>string.Empty</c>, which means
    /// no strength rule is applied.
    /// </summary>
    public string PasswordStrengthRegularExpression { get; set; } = string.Empty;

    /// <summary>The number of consecutive failed sign-in attempts that locks an account out. Defaults to 5.</summary>
    /// <remarks>
    /// The legacy membership provider registration at <c>Website/release.config</c> lines 236 to 246
    /// declares neither <c>passwordAttemptThreshold</c> nor its framework spelling
    /// <c>maxInvalidPasswordAttempts</c>, so the effective legacy value was the
    /// <c>System.Web.Security.SqlMembershipProvider</c> default of five rather than a configured one.
    /// </remarks>
    public int MaxInvalidPasswordAttempts { get; set; } = 5;

    /// <summary>
    /// The window, in minutes, within which consecutive failed sign-in attempts accumulate towards <see
    /// cref="MaxInvalidPasswordAttempts"/>. Defaults to 10.
    /// </summary>
    public int PasswordAttemptWindowMinutes { get; set; } = 10;

    /// <summary>
    /// Reports every way in which the policy bound onto this instance is incoherent, so that a
    /// misconfigured deployment fails while the host is starting rather than when a user first tries to
    /// choose a password.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or an empty
    /// collection when the policy is coherent.
    /// </returns>
    /// <remarks>
    /// Every failure is reported rather than only the first, because an operator fixing one setting per
    /// restart is the outcome a single-failure result produces.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        List<string> failures = [];

        if (MinRequiredPasswordLength < 1)
        {
            // Every number reaches the message through an invariant conversion first, so the
            // concatenation below interpolates strings only and cannot pick up a culture.
            string actual = FormattableString.Invariant($"{MinRequiredPasswordLength}");

            failures.Add(
                $"{SectionName}:{nameof(MinRequiredPasswordLength)} is {actual}, and at least 1 is "
                + "required. A minimum of zero or less admits an empty password, which no legacy "
                + "configuration permitted - the shipped value is 7.");
        }

        if (MinRequiredNonAlphanumericCharacters < 0)
        {
            string actual = FormattableString.Invariant($"{MinRequiredNonAlphanumericCharacters}");

            failures.Add(
                $"{SectionName}:{nameof(MinRequiredNonAlphanumericCharacters)} is {actual}, which is "
                + "negative. Use 0 to express 'no rule', which is the shipped value.");
        }

        if (MinRequiredPasswordLength >= 1
            && MinRequiredNonAlphanumericCharacters > MinRequiredPasswordLength)
        {
            string required = FormattableString.Invariant($"{MinRequiredNonAlphanumericCharacters}");
            string length = FormattableString.Invariant($"{MinRequiredPasswordLength}");

            failures.Add(
                $"{SectionName}:{nameof(MinRequiredNonAlphanumericCharacters)} is {required} while "
                + $"{SectionName}:{nameof(MinRequiredPasswordLength)} is {length}. A password cannot "
                + "contain more non-alphanumeric characters than it has characters, so no password "
                + "could satisfy this policy.");
        }

        // The minimum is checked against the SHARED CREDENTIAL CEILING as well as against itself, because
        // the two are declared in different places and only their combination can be unsatisfiable.
        if (MinRequiredPasswordLength > CredentialBounds.MaximumByteLength)
        {
            string length = FormattableString.Invariant($"{MinRequiredPasswordLength}");
            string ceiling = FormattableString.Invariant($"{CredentialBounds.MaximumByteLength}");

            failures.Add(
                $"{SectionName}:{nameof(MinRequiredPasswordLength)} is {length}, which exceeds the "
                + $"{ceiling}-byte ceiling every credential entry point applies. Every candidate "
                + "password would be rejected as too long before its length could be judged "
                + "sufficient, so no password could satisfy this policy.");
        }

        // MIGRATION: the recovery question-and-answer requirement is UNSATISFIABLE in the target, so the
        // only legal value is the measured legacy one.
        if (RequiresQuestionAndAnswer)
        {
            failures.Add(
                $"{SectionName}:{nameof(RequiresQuestionAndAnswer)} is true, and the target has no "
                + "password-recovery question or answer to satisfy it. The recovery pair is not carried "
                + "forward, so no request accepts a question or an answer and nothing could ever be "
                + "checked against one. Set it to false, which is the measured legacy value.");
        }

        if (!string.IsNullOrEmpty(PasswordStrengthRegularExpression))
        {
            try
            {
                _ = new Regex(PasswordStrengthRegularExpression);
            }
            catch (ArgumentException exception)
            {
                failures.Add(
                    $"{SectionName}:{nameof(PasswordStrengthRegularExpression)} is not a valid "
                    + $"regular expression: {exception.Message} An unparseable pattern would fail "
                    + "every password-setting request at run time instead of failing this start-up.");
            }
        }

        return failures;
    }
}
