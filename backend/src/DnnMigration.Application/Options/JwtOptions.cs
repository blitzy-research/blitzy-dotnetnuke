// MIGRATION: ASP.NET 2.0 Forms Authentication gives way to stateless JWT bearer tokens configured
// by this type; the legacy scheme's reversible credential storage is not carried forward.

namespace DnnMigration.Application.Options;

/// <summary>
/// Strongly-typed settings for the JWT bearer tokens the API issues and validates, read from the <c>Jwt</c>
/// configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Ownership boundaries, stated once here rather than repeated per member.
/// </para>
/// <para>
/// The guard that rejects a missing <see cref="Secret"/> is declared here, beside the setting it governs,
/// rather than only in the authentication extension. Two reasons.
/// </para>
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>Name of the configuration section these settings are read from: <c>Jwt</c>.</summary>
    public const string SectionName = "Jwt";

    // Policy bounds. Declared here, next to the settings they constrain, so the policy is discoverable by
    // anyone reading a member rather than only by whoever finds the code that enforces it.

    /// <summary>
    /// Smallest acceptable <see cref="Secret"/> length, measured in UTF-8 bytes: 32, which is 256 bits.
    /// </summary>
    /// <remarks>
    /// The figure is the repository's own published expectation (docs/project-guide.md L142) and matches
    /// the output width of the SHA-256 hash the signing algorithm is built on, so a shorter key adds no
    /// strength the algorithm can use.
    /// </remarks>
    public const int MinimumSecretByteLength = 32;

    /// <summary>Smallest number of distinct characters a <see cref="Secret"/> must contain: 8.</summary>
    public const int MinimumSecretDistinctCharacters = 8;

    /// <summary>Longest acceptable <see cref="Issuer"/> or <see cref="Audience"/> value: 256 characters.</summary>
    public const int MaximumIssuerOrAudienceLength = 256;

    /// <summary>Smallest acceptable <see cref="ExpirationMinutes"/> value: 1.</summary>
    /// <remarks>
    /// Zero or a negative value would issue tokens that have already expired, so every request would fail
    /// authentication while the configuration looked plausible. One minute is the smallest value that still
    /// produces a usable token.
    /// </remarks>
    public const int MinimumExpirationMinutes = 1;

    /// <summary>Largest acceptable <see cref="ExpirationMinutes"/> value: 60.</summary>
    /// <remarks>
    /// Not an invented ceiling. 60 is the documented default (docs/technical-specifications.md L1038), it
    /// is exactly the legacy Forms Authentication ticket timeout these tokens replace, and the guidance on
    /// <see cref="ExpirationMinutes"/> itself states that deployments may <em>lower</em> the value - which
    /// makes 60 the ceiling by its own description.
    /// </remarks>
    public const int MaximumExpirationMinutes = 60;

    /// <summary>Smallest acceptable <see cref="RefreshTokenExpirationDays"/> value: 1.</summary>
    /// <remarks>
    /// One day already exceeds <see cref="MaximumExpirationMinutes"/> by a wide margin, so no separate rule
    /// is needed - or written - to require that a refresh token outlive the access token it replaces. A
    /// rule that can never fail is not a safeguard; it is unreachable code.
    /// </remarks>
    public const int MinimumRefreshTokenExpirationDays = 1;

    /// <summary>Largest acceptable <see cref="RefreshTokenExpirationDays"/> value: 30.</summary>
    /// <remarks>
    /// Net-new, with no legacy figure to inherit - the legacy application had no refresh mechanism at all,
    /// as the guidance on <see cref="RefreshTokenExpirationDays"/> records.
    /// </remarks>
    public const int MaximumRefreshTokenExpirationDays = 30;

    /// <summary>Largest acceptable <see cref="RefreshTokenAbsoluteExpirationDays"/> value: 30.</summary>
    /// <remarks>
    /// <c>Infrastructure/Security/RefreshTokenStore.cs</c> applies a one-year limit of its own to both
    /// settings. That is now purely a backstop for a store constructed directly, outside the start-up
    /// validation this file performs, and its own remarks record the distinction.
    /// </remarks>
    public const int MaximumRefreshTokenAbsoluteExpirationDays = 30;

    /// <summary>
    /// Symmetric key used to sign issued access tokens and to verify presented ones. Empty by default, and
    /// deliberately so: this repository ships no secret value.
    /// </summary>
    /// <remarks>
    /// The Api layer supplies the real value at startup from the deployment's configuration or from the
    /// <c>Jwt__Secret</c> environment variable.
    /// </remarks>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Value written to the <c>iss</c> claim of every issued token and required when a presented token is
    /// validated. Defaults to <c>DnnMigration</c>.
    /// </summary>
    public string Issuer { get; set; } = "DnnMigration";

    /// <summary>
    /// Value written to the <c>aud</c> claim of every issued token and required when a presented token is
    /// validated. Defaults to <c>DnnMigration</c>.
    /// </summary>
    public string Audience { get; set; } = "DnnMigration";

    /// <summary>Access-token lifetime, in minutes. Defaults to 60.</summary>
    /// <remarks>
    /// The default is the documented one (docs/technical-specifications.md L1038 and L1418,
    /// docs/project-guide.md L145) and satisfies the requirement for short-lived access tokens. It also
    /// preserves the legacy ticket lifetime exactly: the Forms Authentication element these tokens replace
    /// was configured with a 60-minute timeout.
    /// </remarks>
    public int ExpirationMinutes { get; set; } = 60;

    // FormsAuthentication.SignOut has no stateless counterpart, so logout becomes token expiry plus
    // client-side discard; a bounded refresh lifetime is what keeps that expiry meaningful, and is why this
    // exists. MIGRATION: 7 days is chosen here, not inherited.

    /// <summary>Refresh-token lifetime, in days. Defaults to 7.</summary>
    /// <remarks>
    /// Unlike the three defaults above, this one has no source to cite. The legacy application had no
    /// refresh mechanism at all, and the repository's published documentation names refresh tokens only as
    /// being longer-lived than the access token (docs/technical-specifications.md L1419) with no figure
    /// attached.
    /// </remarks>
    public int RefreshTokenExpirationDays { get; set; } = 7;

    // MIGRATION: 30 days is a net-new default, and the setting itself is net-new.

    /// <summary>
    /// Absolute lifetime of a refresh-token family, in days, measured from the sign-in that created it and
    /// never extended by rotation. Defaults to 30.
    /// </summary>
    /// <remarks>
    /// Reaching this ceiling is not an error and not a revocation: it means the caller must authenticate
    /// again, which is the only point at which a credential, an approval state and a lockout state are
    /// re-examined. Without it, a session established once could outlive the password that established it
    /// indefinitely.
    /// </remarks>
    public int RefreshTokenAbsoluteExpirationDays { get; set; } = 30;

    /// <summary>Minutes in a day, used to compare the two lifetimes, which are declared in different units.</summary>
    private const int MinutesPerDay = 24 * 60;

    /// <summary>
    /// Reports every way in which the values bound onto this instance are unusable, so that a misconfigured
    /// deployment fails while the host is starting rather than when the first caller tries to sign in.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or an empty
    /// collection when the instance is usable.
    /// </returns>
    /// <remarks>
    /// Every failure is reported, not just the first, because an operator fixing one setting per restart is
    /// the outcome a single-failure result produces. No message ever contains the secret or any part of it
    /// - only its length - because a start-up failure is written to the log, and a secret in a log is the
    /// arrangement this whole type exists to end.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(Secret))
        {
            failures.Add(
                $"{SectionName}:{nameof(Secret)} is not set. Supply it from the deployment's secret "
                + $"store, or through the {SectionName}__{nameof(Secret)} environment variable. It "
                + "must never be committed to source control.");
        }
        else if (System.Text.Encoding.UTF8.GetByteCount(Secret) < MinimumSecretByteLength)
        {
            // Every number reaches the message through an invariant conversion first, so the concatenation
            // below interpolates strings only and cannot pick up a culture. The secret's LENGTH is safe to
            // report; the secret itself never is.
            string actual = FormattableString.Invariant(
                $"{System.Text.Encoding.UTF8.GetByteCount(Secret)}");
            string required = FormattableString.Invariant($"{MinimumSecretByteLength}");

            failures.Add(
                $"{SectionName}:{nameof(Secret)} is {actual} UTF-8 bytes long, and at least "
                + $"{required} are required. The signing key is consumed as bytes, and this floor is "
                + "application policy: it matches the 256-bit output width of the hash the signing "
                + "algorithm is built on, so a shorter key adds no strength the algorithm can use.");
        }

        if (string.IsNullOrWhiteSpace(Issuer))
        {
            failures.Add(
                $"{SectionName}:{nameof(Issuer)} is not set. Every issued token carries this value "
                + "and every incoming token is checked against it, so an empty issuer disables that "
                + "check rather than passing it.");
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            failures.Add(
                $"{SectionName}:{nameof(Audience)} is not set. Every issued token carries this value "
                + "and every incoming token is checked against it, so an empty audience disables "
                + "that check rather than passing it.");
        }

        if (ExpirationMinutes <= 0)
        {
            string actual = FormattableString.Invariant($"{ExpirationMinutes}");

            failures.Add(
                $"{SectionName}:{nameof(ExpirationMinutes)} is {actual}, and a positive number of "
                + "minutes is required. A non-positive access-token lifetime issues tokens that "
                + "have already expired.");
        }

        if (RefreshTokenExpirationDays <= 0)
        {
            string actual = FormattableString.Invariant($"{RefreshTokenExpirationDays}");

            failures.Add(
                $"{SectionName}:{nameof(RefreshTokenExpirationDays)} is {actual}, and a positive "
                + "number of days is required. A non-positive refresh-token lifetime makes every "
                + "issued refresh token unusable, which leaves a caller no way to renew a session.");
        }

        if (ExpirationMinutes > 0
            && RefreshTokenExpirationDays > 0
            && ((long)RefreshTokenExpirationDays * MinutesPerDay) <= ExpirationMinutes)
        {
            string accessMinutes = FormattableString.Invariant($"{ExpirationMinutes}");
            string refreshDays = FormattableString.Invariant($"{RefreshTokenExpirationDays}");

            failures.Add(
                $"{SectionName}:{nameof(RefreshTokenExpirationDays)} is {refreshDays} day(s), which "
                + $"is not longer than {SectionName}:{nameof(ExpirationMinutes)} of "
                + $"{accessMinutes} minute(s). A refresh token that expires no later than the "
                + "access token it renews cannot renew anything.");
        }

        if (RefreshTokenAbsoluteExpirationDays <= 0)
        {
            string actual = FormattableString.Invariant($"{RefreshTokenAbsoluteExpirationDays}");

            failures.Add(
                $"{SectionName}:{nameof(RefreshTokenAbsoluteExpirationDays)} is {actual}, and a "
                + "positive number of days is required. A non-positive session ceiling expires every "
                + "refresh-token family at the instant it is created, so no caller could ever renew "
                + "a session.");
        }
        else if (RefreshTokenAbsoluteExpirationDays > MaximumRefreshTokenAbsoluteExpirationDays)
        {
            string ceiling = FormattableString.Invariant($"{RefreshTokenAbsoluteExpirationDays}");
            string permitted = FormattableString.Invariant($"{MaximumRefreshTokenAbsoluteExpirationDays}");

            failures.Add(
                $"{SectionName}:{nameof(RefreshTokenAbsoluteExpirationDays)} is {ceiling} day(s), "
                + $"and at most {permitted} are permitted. This is the only setting that ever forces "
                + "a caller to authenticate again, and authenticating again is the only moment a "
                + "credential, an approval state and a lockout state are examined from scratch - so "
                + "the value is exactly how long an account disabled after sign-in keeps renewing "
                + "its session.");
        }
        else if (RefreshTokenExpirationDays > 0
            && RefreshTokenAbsoluteExpirationDays < RefreshTokenExpirationDays)
        {
            string ceiling = FormattableString.Invariant($"{RefreshTokenAbsoluteExpirationDays}");
            string sliding = FormattableString.Invariant($"{RefreshTokenExpirationDays}");

            failures.Add(
                $"{SectionName}:{nameof(RefreshTokenAbsoluteExpirationDays)} is {ceiling} day(s), "
                + $"which is less than {SectionName}:{nameof(RefreshTokenExpirationDays)} of "
                + $"{sliding} day(s). The session ceiling would truncate every individual token, "
                + "making the per-token lifetime unreachable and the setting misleading.");
        }

        return failures;
    }
}
