// MIGRATION: replaces the static facade Library/Components/Providers/Users/MembershipProviderConfig.vb,
// which reached the policy through a reflection-resolved provider singleton
// (MembershipProviderConfig.vb:L44) rather than through configuration. The same values are now plain
// bound configuration, and the storage format travels with them no further: passwordFormat="Encrypted"
// (Website/release.config:L245) is superseded by one-way hashing in
// Infrastructure/Security/BcryptPasswordHasher.cs. Reversible Triple-DES storage only worked because a
// decryption key was committed to source control at Website/release.config:L91-L92; that key is
// referenced here by line number and is deliberately reproduced nowhere in this solution.

// MIGRATION: enablePasswordRetrieval="true" (Website/release.config:L239) is deliberately not carried
// forward, so this type exposes no retrieval switch and no storage-format selector. Under a one-way
// hash, returning an existing password is impossible rather than merely disallowed - and the legacy
// facade had already reached the same conclusion, forcing retrieval off whenever the stored format was
// hashed (MembershipProviderConfig.vb:L182-L190). A permanently-false switch would only invite someone
// to set it true and expect it to work.

using System.Text.RegularExpressions;
using DnnMigration.Application.Validation;

namespace DnnMigration.Application.Options;

/// <summary>
/// The password policy in force for the migrated application, preserved verbatim from the legacy
/// DotNetNuke installation and bound from the configuration section named by
/// <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type supplies the numbers and switches that the request validators
/// in <c>DnnMigration.Application.Validation</c> enforce. It applies no password rule of its own, holds
/// no message wording and caches no compiled pattern. It is declared in this
/// layer and bound by the Api layer, which is why it carries no framework type and no attribute.
/// </para>
/// <para>
/// The one method it does declare, <see cref="Validate"/>, reports whether the POLICY ITSELF is
/// coherent - not whether any password satisfies it. The distinction matters: judging a password is
/// the validators' work and happens per request, whereas an unsatisfiable or nonsensical policy is a
/// deployment defect that should stop the host from starting. <see cref="Validate"/> is deliberately
/// conservative about what counts as a defect, because the policy is preserved verbatim and every
/// shipped value has to pass unchanged.
/// </para>
/// <para>
/// <b>Where the defaults come from.</b> Every default below is a value measured from the
/// <c>AspNetSqlMembershipProvider</c> element in <c>Website/release.config</c>, which spans L236-L247
/// and whose policy attributes occupy L239-L245; the attribute reference comment immediately above it
/// occupies L222-L234. Two of those attributes are deliberately unrepresented here -
/// <c>enablePasswordRetrieval</c> (L239) and <c>passwordFormat</c> (L245) - for the reasons given in
/// the migration notes at the head of this file.
/// </para>
/// <para>
/// <b>Only one rule is actually active.</b> The legacy check at
/// <c>Library/Components/Users/UserController.vb:L1067-L1091</c> wrote three rules, but with the
/// shipped configuration only the first of them can ever fire:
/// </para>
/// <list type="number">
/// <item><description>
/// Minimum length (L1073) - <b>active</b>. Governed by <see cref="MinRequiredPasswordLength"/>.
/// </description></item>
/// <item><description>
/// Minimum count of non-alphanumeric characters (L1078-L1079) - <b>vacuous</b>. The configured
/// minimum is zero, and a match count is never below zero, so the comparison can never fail.
/// </description></item>
/// <item><description>
/// Strength pattern (L1084-L1086) - <b>never reached</b>. The setting is configured in neither
/// <c>Website/release.config</c> nor <c>Website/development.config</c>, so the legacy emptiness test
/// at L1084 was always false and the body never ran.
/// </description></item>
/// </list>
/// <para>
/// Net effect, and the single most useful fact for whoever writes the validators: under the shipped
/// configuration the only active password rule is a minimum length of seven characters.
/// </para>
/// <para>
/// <b>Deliberately absent - lockout and throttling.</b> The legacy facade also exposed
/// <c>MaxInvalidPasswordAttempts</c> (<c>MembershipProviderConfig.vb:L74</c>) and
/// <c>PasswordAttemptWindow</c> (L128), and neither is represented here. Neither is set on the
/// <c>Website/release.config</c> element: the corresponding <c>passwordAttemptThreshold</c> and
/// <c>passwordAttemptWindow</c> attributes appear only inside the reference comment at L224-L225, so
/// neither is measured policy. Attempt throttling is a transport-edge concern in the target and is
/// owned by <c>Api/Extensions/RateLimitingExtensions.cs</c>. The exclusion is recorded so that it
/// reads as a decision rather than an oversight.
/// </para>
/// <para>
/// <b>Deliberately absent - everything else the legacy facade carried.</b> No generated-password
/// length: the legacy <c>MinPasswordLength + 4</c> at <c>UserController.vb:L316</c> belongs to the
/// service that generates passwords. No hashing parameters: those belong to
/// <c>Infrastructure/Security/BcryptPasswordHasher.cs</c>. No message wording: that belongs to the
/// validators, and this type supplies only the numbers they substitute.
/// </para>
/// <para>
/// <b>Deliberately absent - the maximum credential length.</b> Every rule on this type is legacy
/// policy that a deployment may legitimately vary, which is exactly why it is bindable. The upper
/// bound on a submitted credential is neither of those things: it is net-new, it is a security
/// bound rather than a policy preference, and a maximum that configuration can raise provides no
/// guarantee at all. It is therefore a compile-time constant on
/// <see cref="Validation.CredentialBounds"/> rather than a property here, and every credential
/// entry point reads it from that one place. Placing it here would have made this type's own
/// contract - "the six values below are the legacy policy preserved verbatim" - false.
/// </para>
/// </remarks>
public sealed class PasswordPolicyOptions
{
    /// <summary>
    /// The name of the configuration section this type is bound from: <c>PasswordPolicy</c>.
    /// </summary>
    /// <remarks>
    /// The Api layer resolves the section by this constant rather than by a repeated string literal,
    /// so the section name is stated in exactly one place. Individual keys are overridable per
    /// environment in the double-underscore form - generally
    /// <c>PasswordPolicy__&lt;PropertyName&gt;</c>, for example
    /// <c>PasswordPolicy__MinRequiredPasswordLength</c>. The section name is net-new: the legacy
    /// installation held these values as attributes of a provider element, not as a named
    /// configuration section.
    /// </remarks>
    public const string SectionName = "PasswordPolicy";

    // ------------------------------------------------------------------------
    // Policy bounds on the policy itself. These constrain what a DEPLOYMENT may
    // configure; they are not password rules and they are not applied to any
    // credential. Held here, beside the settings they constrain, and const
    // rather than configurable, because a bound a configuration file can widen
    // bounds nothing. Enforcement reads them from
    // Api/Extensions/ServiceCollectionExtensions.cs at startup.
    // ------------------------------------------------------------------------

    /// <summary>
    /// Floor beneath which <see cref="MinRequiredPasswordLength"/> may not be
    /// configured: <c>7</c>, the measured legacy value.
    /// </summary>
    /// <remarks>
    /// The guidance below forbids <em>raising</em> the minimum, because that
    /// would lock out users the legacy installation already accepted. This
    /// constant closes the opposite direction: lowering it below the legacy
    /// figure would accept credentials the legacy installation rejected, which is
    /// a security regression rather than migration fidelity. Preserving the
    /// policy verbatim means both directions are closed, and the shipped default
    /// sits exactly on this floor.
    /// </remarks>
    public const int MinimumConfigurablePasswordLength = 7;

    /// <summary>
    /// Longest acceptable <see cref="PasswordStrengthRegularExpression"/>
    /// pattern: 512 characters.
    /// </summary>
    /// <remarks>
    /// The pattern is compiled once and then evaluated against every submitted
    /// credential, so its cost is paid on an unauthenticated path. A ceiling
    /// keeps that cost bounded and is far above any legitimate strength pattern -
    /// the legacy installation shipped none at all. It is a size bound only; that
    /// the pattern also has to compile, and has to run under a match timeout, is
    /// enforced separately.
    /// </remarks>
    public const int MaximumPasswordStrengthRegularExpressionLength = 512;

    // MIGRATION: the six values below are the legacy policy preserved verbatim - 7, 0, false, false,
    // true and empty - and they are deliberately NOT tightened. Raising the minimum length, demanding
    // non-alphanumeric characters, requiring a question and answer, enforcing a unique email address
    // or adding any complexity flag would reject credentials and registrations that the legacy
    // installation accepted, denying existing users access at the moment they are migrated. Hardening
    // the policy is a separate, explicit decision, taken deliberately and never as a side effect of
    // this migration.

    /// <summary>
    /// The minimum number of characters a password must contain. Defaults to <c>7</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured from <c>minRequiredPasswordLength="7"</c> at <c>Website/release.config:L242</c> and
    /// mirrors the legacy facade property at <c>MembershipProviderConfig.vb:L110</c>. It is the value
    /// the legacy length check compared against at <c>UserController.vb:L1073</c>, and under the
    /// shipped configuration it is the <b>only active password rule</b>.
    /// </para>
    /// <para>
    /// <b>Do not raise this value.</b> The policy is preserved verbatim precisely because tightening
    /// it during the migration would reject the credentials of users who are already registered.
    /// </para>
    /// <para>
    /// Token contract with the validators: this value substitutes the <c>[PasswordLength]</c> token in
    /// the legacy <c>InvalidPassword.Text</c> message
    /// (<c>Website/App_GlobalResources/SharedResources.resx:L285-L287</c>), exactly as the legacy code
    /// did at <c>UserController.vb:L608</c>. The wording belongs to
    /// <c>DnnMigration.Application.Validation</c>; this property supplies the number.
    /// </para>
    /// </remarks>
    public int MinRequiredPasswordLength { get; set; } = 7;

    /// <summary>
    /// The minimum number of non-alphanumeric characters a password must contain. Defaults to
    /// <c>0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured from <c>minRequiredNonalphanumericCharacters="0"</c> at
    /// <c>Website/release.config:L243</c> and mirrors the legacy facade property at
    /// <c>MembershipProviderConfig.vb:L92</c>. The initialiser is written explicitly even though zero
    /// is also the default for the type: this is a measured legacy value, not an incidental zero.
    /// </para>
    /// <para>
    /// Zero makes the corresponding legacy rule <b>vacuous</b>. The check at
    /// <c>UserController.vb:L1078-L1079</c> counted the characters matching the class
    /// <c>[^0-9a-zA-Z]</c> and failed the password when that count fell below this minimum, and a
    /// count never falls below zero. A validator should therefore treat zero as "no rule" and omit it
    /// entirely rather than emitting a condition that can never fail.
    /// </para>
    /// <para>
    /// <b>Do not raise this value</b>, for the same reason recorded on
    /// <see cref="MinRequiredPasswordLength"/>.
    /// </para>
    /// <para>
    /// Token contract with the validators: this value substitutes the <c>[NoneAlphabet]</c> token in
    /// the legacy <c>InvalidPassword.Text</c> message, exactly as the legacy code did at
    /// <c>UserController.vb:L609</c>.
    /// </para>
    /// </remarks>
    public int MinRequiredNonAlphanumericCharacters { get; set; } = 0;

    /// <summary>
    /// Whether the credential store requires a security question and answer. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Measured from <c>requiresQuestionAndAnswer="false"</c> at <c>Website/release.config:L241</c>
    /// and mirrors the legacy facade property at <c>MembershipProviderConfig.vb:L224</c>. The
    /// question-and-answer flow itself is not carried forward as a feature - no endpoint and no screen
    /// collects or verifies one - so this flag exists to preserve the policy surface faithfully and
    /// must remain <see langword="false"/>. Setting it <see langword="true"/> would assert a
    /// requirement that nothing in the target is able to enforce.
    /// </remarks>
    public bool RequiresQuestionAndAnswer { get; set; } = false;

    /// <summary>
    /// Whether an email address must be unique across the membership store. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Measured from <c>requiresUniqueEmail="false"</c> at <c>Website/release.config:L244</c> and
    /// mirrors the legacy facade property at <c>MembershipProviderConfig.vb:L242</c>. The legacy
    /// installation permitted duplicate email addresses, so the existing data may well contain them;
    /// enforcing uniqueness during the migration could reject users who are already registered and
    /// would fail on data the legacy application considered valid. The default must therefore remain
    /// <see langword="false"/>.
    /// </remarks>
    public bool RequiresUniqueEmail { get; set; } = false;

    /// <summary>
    /// Whether a password may be reset, that is replaced with a newly issued credential. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Measured from <c>enablePasswordReset="true"</c> at <c>Website/release.config:L240</c> and
    /// mirrors the legacy facade property at <c>MembershipProviderConfig.vb:L164</c>. Reset is sharply
    /// distinct from <i>retrieval</i>: a reset issues a new credential and remains fully workable over
    /// a one-way hash, whereas retrieval returns the password already stored and is deliberately not
    /// carried forward in any form. That is why this type keeps the reset switch and exposes no
    /// retrieval switch at all.
    /// </remarks>
    public bool PasswordResetEnabled { get; set; } = true;

    // MIGRATION: the legacy strength check assigned where it should have accumulated -
    // UserController.vb:L1086 reads "isValid = rx.IsMatch(password)", discarding the length and
    // non-alphanumeric results computed above it, so a password that had already failed could still be
    // declared valid. The defect was unreachable in practice because this value was never configured.
    // The target validators contribute independently and therefore do NOT replicate it; that
    // correction is itself a behavioural divergence and is recorded as one rather than absorbed
    // silently.

    /// <summary>
    /// An optional pattern that a password must match in full. Defaults to <c>string.Empty</c>, which
    /// means no strength rule is applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the legacy facade property at <c>MembershipProviderConfig.vb:L206</c>, which in turn
    /// surfaced the provider property declared at <c>MembershipProvider.vb:L62</c>.
    /// </para>
    /// <para>
    /// <b>Inert by default, and it must stay that way.</b> The setting is configured in neither
    /// <c>Website/release.config</c> nor <c>Website/development.config</c> - verified by searching both
    /// files - so the legacy emptiness test at <c>UserController.vb:L1084</c> was always false and the
    /// rule never fired. In the legacy tree the setting exists only as a provider property, as an
    /// administration label
    /// (<c>Website/admin/Users/App_LocalResources/UserSettings.ascx.resx:L135</c> and <c>L150</c>) and
    /// at that single consumption site.
    /// </para>
    /// <para>
    /// The value is held as a <see langword="string"/> and is never turned into a matcher here.
    /// Compiling the pattern, caching it and handling an invalid one belong to the validator that
    /// applies it, not to a configuration holder.
    /// </para>
    /// <para>
    /// An empty value means "no strength rule", and a validator must <b>omit the rule entirely</b>
    /// rather than apply an empty pattern: an empty pattern matches every input, so applying it would
    /// be a silent no-op wearing the appearance of enforcement.
    /// </para>
    /// </remarks>
    public string PasswordStrengthRegularExpression { get; set; } = string.Empty;

    /// <summary>
    /// The number of consecutive failed sign-in attempts that locks an account out. Defaults to 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy membership provider registration at
    /// <c>Website/release.config</c> lines 236 to 246 declares neither
    /// <c>passwordAttemptThreshold</c> nor its framework spelling
    /// <c>maxInvalidPasswordAttempts</c>, so the effective legacy value was the
    /// <c>System.Web.Security.SqlMembershipProvider</c> default of five rather than a configured one.
    /// That measured effective value is reproduced here as the default so an unconfigured deployment
    /// behaves exactly as the legacy installation did, while remaining overridable through
    /// configuration.
    /// </para>
    /// <para>
    /// The value is consumed by the authentication service and handed to the repository member that
    /// records a failed attempt; the counting itself belongs to the external membership store, whose
    /// failed-attempt and lock-out bookkeeping DotNetNuke added in the 04.00.00 upgrade script.
    /// </para>
    /// </remarks>
    public int MaxInvalidPasswordAttempts { get; set; } = 5;

    /// <summary>
    /// The window, in minutes, within which consecutive failed sign-in attempts accumulate towards
    /// <see cref="MaxInvalidPasswordAttempts"/>. Defaults to 10.
    /// </summary>
    /// <remarks>
    /// MIGRATION: as with <see cref="MaxInvalidPasswordAttempts"/>, the legacy provider registration
    /// omits <c>passwordAttemptWindow</c>, so the effective legacy value was the framework default of
    /// ten minutes. It is expressed in minutes rather than as a time span because that is the unit the
    /// legacy attribute used, and it is converted once, at the single point of use.
    /// </remarks>
    public int PasswordAttemptWindowMinutes { get; set; } = 10;

    /// <summary>
    /// Reports every way in which the policy bound onto this instance is incoherent, so that a
    /// misconfigured deployment fails while the host is starting rather than when a user first tries
    /// to choose a password.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or an
    /// empty collection when the policy is coherent.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every failure is reported rather than only the first, because an operator fixing one setting
    /// per restart is the outcome a single-failure result produces.
    /// </para>
    /// <para>
    /// What this method deliberately does NOT do is enforce the legacy figures as minimums. A
    /// deployment may set a length of 8 or demand a non-alphanumeric character; that is a hardening
    /// decision an operator is entitled to take, and the remarks above ask them not to take it
    /// during the migration rather than making it impossible. What is rejected is a policy no
    /// password can satisfy and a policy that admits an empty password, neither of which any legacy
    /// configuration could express.
    /// </para>
    /// <para>
    /// The pattern is compiled here purely to prove it is well formed, and the compiled instance is
    /// discarded. Caching a matcher for reuse belongs to the validator that applies the rule, as the
    /// remarks on <see cref="PasswordStrengthRegularExpression"/> state; proving at start-up that the
    /// pattern parses is a different concern, and leaving it unproven defers a configuration defect
    /// to the first sign-up attempt.
    /// </para>
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

        // MIGRATION: the minimum is checked against the SHARED CREDENTIAL CEILING as well as against
        // itself, because the two are declared in different places and only their combination can be
        // unsatisfiable. Every credential entry point rejects a submission longer than
        // Validation/CredentialBounds.cs allows, so a configured minimum above that ceiling would
        // reject every candidate a caller could possibly send - the policy and the bound would each be
        // internally consistent while jointly admitting nothing. The comparison is sound across
        // encodings rather than only for plain ASCII: UTF-8 spends at least one byte on every UTF-16
        // code unit, so a value whose length exceeds the ceiling in code units necessarily exceeds it
        // in bytes too. The ceiling is deliberately NOT the hashing algorithm's own 72-byte
        // significance limit: the hasher digests the credential with SHA-384 before hashing, so every
        // byte of the input contributes and no prefix can stand in for the whole.
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
        // only legal value is the measured legacy one. The provider is registered with
        // requiresQuestionAndAnswer="false" at Website/release.config L241, and the pair is not carried
        // forward at all: the owning service contract records that the legacy question-and-answer member
        // has no counterpart and that no member declares a question or answer parameter, because the
        // pair's only real purpose was to guard credential retrieval, which is dropped outright now the
        // store is one-way. No request in the target carries a question or an answer, so switching this
        // on cannot cause anything to be collected, stored or checked - it would merely make an operator
        // believe a requirement was in force. Failing at start-up is the only way that belief can be
        // corrected, and it is the same reasoning that removed the request members and the validator
        // rules rather than leaving them inert.
        //
        // Contrast RequiresUniqueEmail, which this method does NOT reject, although the deployment does.
        // The distinction is the one this method is scoped by: it refuses what is UNSATISFIABLE, and a
        // unique-email requirement is merely UNIMPLEMENTED. The address it governs is on the wire, so a
        // future uniqueness rule has something to check, and the contract itself is not entitled to
        // forbid a configuration that will one day be legitimate. Its measured legacy value is false and
        // the terminal schema corroborates it, the single unique constraint having moved off Email and
        // onto Username, so the flag records a fact rather than promising enforcement.
        //
        // The HOST additionally refuses a true value, on the narrower ground that a flag which reads as
        // a requirement and is enforced nowhere misleads whoever set it - see the options validator in
        // Api/Extensions/ServiceCollectionExtensions.cs, which applies this method's rules and then its
        // own deployment rules on top. That refusal is the operative behaviour today, and it is a
        // deployment policy rather than a property of the contract, which is why it lives there and not
        // here. The change that implements a uniqueness check deletes that one guard.
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
