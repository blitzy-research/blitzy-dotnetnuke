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

namespace DnnMigration.Application.Options;

/// <summary>
/// The password policy in force for the migrated application, preserved verbatim from the legacy
/// DotNetNuke installation and bound from the configuration section named by
/// <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is data, not behaviour. It supplies the numbers and switches that the request validators
/// in <c>DnnMigration.Application.Validation</c> enforce, and it performs no validation of its own: it
/// declares no check method, holds no message wording and compiles no pattern. It is declared in this
/// layer and bound by the Api layer, which is why it carries no framework type, no attribute and no
/// import at all.
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
}
