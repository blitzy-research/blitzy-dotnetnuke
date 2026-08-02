// MIGRATION: ASP.NET 2.0 Forms Authentication gives way to stateless JWT bearer tokens configured
// by this type; the legacy scheme's reversible credential storage is not carried forward.

namespace DnnMigration.Application.Options;

/// <summary>
/// Strongly-typed settings for the JWT bearer tokens the API issues and
/// validates, read from the <c>Jwt</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// This is a plain settings carrier. It
/// names no type from the hosting stack, the token libraries or the persistence
/// stack, which is what keeps the Application layer resting on the Domain layer
/// alone. Only <see langword="int"/> and <see langword="string"/> members appear
/// below, plus <see cref="Validate"/>, which reports this object's own invariants
/// using base-class-library types alone.
/// </para>
/// <para>
/// Ownership boundaries, stated once here rather than repeated per member. This
/// file declares the settings and the conditions under which they are unusable;
/// the Api layer reads them onto an
/// instance of this type at startup, calls <see cref="Validate"/> as part of that
/// binding, and refuses to start when it reports a failure. Token creation and
/// signature checking
/// belong to <c>Infrastructure/Security/JwtTokenService.cs</c>; issuing and
/// rotating refresh tokens belongs to
/// <c>Infrastructure/Security/RefreshTokenStore.cs</c>. No signing, hashing
/// or registration logic appears in this file, and none should be
/// added to it.
/// </para>
/// <para>
/// MIGRATION: the guard that rejects a missing <see cref="Secret"/> is declared
/// here rather than in <c>Api/Extensions/AuthenticationExtensions.cs</c>, which is
/// where an earlier revision of these remarks placed it. Two reasons. The signing
/// key is materialised in the authentication extension, but so is nothing else
/// about the lifetimes, so splitting the checks would leave three of the five
/// settings unguarded; and a guard that fires while the signing key is being
/// materialised has already let the host begin composing, whereas start-up options
/// validation fails before any service is resolved. Declaring every condition
/// beside the value it governs also keeps one rule per setting, which is what stops
/// a consumer and a document drifting apart.
/// </para>
/// <para>
/// Both lifetimes are plain integers — minutes for the access token, days for
/// the refresh token — rather than an interval type. A configuration binder
/// reads an integer unambiguously, whereas an interval type expects one
/// particular string form and would force the JSON shape to diverge from the
/// documented <c>"ExpirationMinutes": 60</c> integer
/// (docs/technical-specifications.md L1038, docs/project-guide.md L145).
/// </para>
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>
    /// Name of the configuration section these settings are read from:
    /// <c>Jwt</c>.
    /// </summary>
    /// <remarks>
    /// The Api layer reads the section this constant names onto a
    /// <see cref="JwtOptions"/> instance, so the literal lives in exactly one
    /// place and a rename cannot leave a stale copy behind. Every member below
    /// is individually overridable by an environment variable of the form
    /// <c>Jwt__PropertyName</c>; the double underscore is the hierarchy
    /// separator, as used by <c>Jwt__Secret</c> in the container example at
    /// docs/project-guide.md L242 and by <c>ConnectionStrings__Default</c> for
    /// the database connection string.
    /// </remarks>
    public const string SectionName = "Jwt";

    // ------------------------------------------------------------------------
    // Policy bounds. Declared here, next to the settings they constrain, so the
    // policy is discoverable by anyone reading a member rather than only by
    // whoever finds the code that enforces it. They are const rather than
    // configurable for the same reason the credential ceiling in
    // Validation/CredentialBounds.cs is const: a bound that configuration can
    // widen is not a bound. Declaring them is still not enforcing them - this
    // type performs no validation - and the enforcement that reads every value
    // below lives in Api/Extensions/AuthenticationExtensions.cs, where a
    // rejected configuration can still stop the host from serving traffic.
    // ------------------------------------------------------------------------

    /// <summary>
    /// Smallest acceptable <see cref="Secret"/> length, measured in UTF-8 bytes:
    /// 32, which is 256 bits.
    /// </summary>
    /// <remarks>
    /// The figure is the repository's own published expectation
    /// (docs/project-guide.md L142) and matches the output width of the SHA-256
    /// hash the signing algorithm is built on, so a shorter key adds no strength
    /// the algorithm can use. Bytes rather than characters because the key is
    /// consumed as bytes; a value made of multi-byte characters therefore
    /// satisfies this bound with fewer characters, which is correct.
    /// </remarks>
    public const int MinimumSecretByteLength = 32;

    /// <summary>
    /// Smallest number of distinct characters a <see cref="Secret"/> must
    /// contain: 8.
    /// </summary>
    /// <remarks>
    /// A length test alone accepts a key of one repeated character, which is
    /// long and yet carries almost no entropy. This bound is the cheapest test
    /// that rejects that shape without pretending to measure entropy properly,
    /// which no startup check can do for an opaque string. It is a floor against
    /// an obviously degenerate value, never a substitute for generating the key
    /// with a cryptographic random source.
    /// </remarks>
    public const int MinimumSecretDistinctCharacters = 8;

    /// <summary>
    /// Longest acceptable <see cref="Issuer"/> or <see cref="Audience"/> value:
    /// 256 characters.
    /// </summary>
    /// <remarks>
    /// Both values are copied into every issued token, so an unbounded value
    /// inflates every response and every subsequent request that carries the
    /// token. 256 is far above any legitimate identifier - the documented values
    /// are twelve characters - and low enough that the inflation cannot matter.
    /// </remarks>
    public const int MaximumIssuerOrAudienceLength = 256;

    /// <summary>
    /// Smallest acceptable <see cref="ExpirationMinutes"/> value: 1.
    /// </summary>
    /// <remarks>
    /// Zero or a negative value would issue tokens that have already expired,
    /// so every request would fail authentication while the configuration looked
    /// plausible. One minute is the smallest value that still produces a usable
    /// token.
    /// </remarks>
    public const int MinimumExpirationMinutes = 1;

    /// <summary>
    /// Largest acceptable <see cref="ExpirationMinutes"/> value: 60.
    /// </summary>
    /// <remarks>
    /// Not an invented ceiling. 60 is the documented default
    /// (docs/technical-specifications.md L1038), it is exactly the legacy Forms
    /// Authentication ticket timeout these tokens replace
    /// (Website/release.config L147), and the guidance on
    /// <see cref="ExpirationMinutes"/> itself states that deployments may
    /// <em>lower</em> the value - which makes 60 the ceiling by its own
    /// description. A bearer token cannot be recalled once issued, so this
    /// ceiling is the whole of the guarantee that an access token is short-lived.
    /// </remarks>
    public const int MaximumExpirationMinutes = 60;

    /// <summary>
    /// Smallest acceptable <see cref="RefreshTokenExpirationDays"/> value: 1.
    /// </summary>
    /// <remarks>
    /// One day already exceeds <see cref="MaximumExpirationMinutes"/> by a wide
    /// margin, so no separate rule is needed - or written - to require that a
    /// refresh token outlive the access token it replaces. A rule that can never
    /// fail is not a safeguard; it is unreachable code.
    /// </remarks>
    public const int MinimumRefreshTokenExpirationDays = 1;

    /// <summary>
    /// Largest acceptable <see cref="RefreshTokenExpirationDays"/> value: 30.
    /// </summary>
    /// <remarks>
    /// Net-new, with no legacy figure to inherit - the legacy application had no
    /// refresh mechanism at all, as the guidance on
    /// <see cref="RefreshTokenExpirationDays"/> records. What the ceiling buys is
    /// concrete: a refresh family's absolute deadline is fixed from this value at
    /// the moment the family is created, so this number is the longest a stolen
    /// family can remain redeemable, and an unbounded setting would make that
    /// window unbounded too.
    /// </remarks>
    public const int MaximumRefreshTokenExpirationDays = 30;

    /// <summary>
    /// Symmetric key used to sign issued access tokens and to verify presented
    /// ones. Empty by default, and deliberately so: this repository ships no
    /// secret value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Api layer supplies the real value at startup from the deployment's
    /// configuration or from the <c>Jwt__Secret</c> environment variable. The
    /// repository's published guidance expects a 256-bit value of at least 32
    /// characters (docs/project-guide.md L142), generated per environment, held
    /// in a managed secret store such as Azure Key Vault or AWS Secrets Manager,
    /// and rotated on a policy (docs/project-guide.md L281).
    /// </para>
    /// <para>
    /// Never commit a value here — not a real key, not a sample, not a
    /// placeholder. The legacy application committed its signing material to
    /// source control while storing credentials reversibly rather than hashed, so
    /// a single committed key laid open every stored credential. Ending that
    /// arrangement is why this file exists, and the repository's own risk register
    /// already states the standard — never commit secrets, rotate regularly
    /// (docs/project-guide.md L336).
    /// </para>
    /// <para>
    /// A deployment that supplies no secret must fail at startup rather than
    /// sign tokens with an empty key, and <see cref="Validate"/> is where that
    /// check is made. It also enforces the 32-character floor, which is not a
    /// preference: the symmetric key backing HMAC-SHA256 has to be at least 256
    /// bits, so a shorter secret is rejected by the signing library itself - at
    /// the moment a token is first issued, which is to say in front of a user
    /// rather than at start-up.
    /// </para>
    /// </remarks>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Value written to the <c>iss</c> claim of every issued token and required
    /// when a presented token is validated. Defaults to <c>DnnMigration</c>.
    /// </summary>
    /// <remarks>
    /// The default is not invented: it is the value the repository's own
    /// published configuration already documents
    /// (docs/technical-specifications.md L1036, docs/project-guide.md L143).
    /// Choosing anything else would contradict that documentation.
    /// </remarks>
    public string Issuer { get; set; } = "DnnMigration";

    /// <summary>
    /// Value written to the <c>aud</c> claim of every issued token and required
    /// when a presented token is validated. Defaults to <c>DnnMigration</c>.
    /// </summary>
    /// <remarks>
    /// The default matches the documented configuration
    /// (docs/technical-specifications.md L1037, docs/project-guide.md L144).
    /// <see cref="Issuer"/> and <see cref="Audience"/> holding the same string
    /// is the documented arrangement rather than an oversight: this API is a
    /// backend-for-frontend with a single client — the Angular application — so
    /// it is the only audience for the tokens it issues. A deployment that
    /// fronts additional clients would separate the two values.
    /// </remarks>
    public string Audience { get; set; } = "DnnMigration";

    /// <summary>
    /// Access-token lifetime, in minutes. Defaults to 60.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is the documented one
    /// (docs/technical-specifications.md L1038 and L1418,
    /// docs/project-guide.md L145) and satisfies the requirement for
    /// short-lived access tokens. It also preserves the legacy ticket lifetime
    /// exactly: the Forms Authentication element these tokens replace was
    /// configured with a 60-minute timeout (Website/release.config L147).
    /// </para>
    /// <para>
    /// Deployments may lower the value. A shorter lifetime moves more traffic
    /// onto the refresh endpoint but narrows the window in which a leaked token
    /// remains usable; because a bearer token cannot be recalled once issued,
    /// that window is the whole of the exposure.
    /// </para>
    /// <para>
    /// Accepted range, enforced at startup: <see cref="MinimumExpirationMinutes"/>
    /// through <see cref="MaximumExpirationMinutes"/> inclusive. A value outside
    /// it stops the host rather than being clamped, because silently substituting
    /// a lifetime nobody configured is how a token ends up living longer than the
    /// operator believes.
    /// </para>
    /// </remarks>
    public int ExpirationMinutes { get; set; } = 60;

    // MIGRATION: FormsAuthentication.SignOut has no stateless counterpart, so
    // logout becomes token expiry plus client-side discard; a bounded refresh
    // lifetime is what keeps that expiry meaningful, and is why this exists.
    // MIGRATION: 7 days is chosen here, not inherited. No legacy setting and no published
    // document supplies a refresh-token lifetime, so it has no precedent.

    /// <summary>
    /// Refresh-token lifetime, in days. Defaults to 7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the three defaults above, this one has no source to cite. The
    /// legacy application had no refresh mechanism at all, and the repository's
    /// published documentation names refresh tokens only as being longer-lived
    /// than the access token (docs/technical-specifications.md L1419) with no
    /// figure attached. Read 7 as a deliberate starting point for a new
    /// mechanism, never as a measured legacy value.
    /// </para>
    /// <para>
    /// The setting bounds how long a refresh token stays usable; it does not
    /// implement rotation. Handing back a replacement on each use and
    /// invalidating the token that was presented belongs to
    /// <c>Infrastructure/Security/RefreshTokenStore.cs</c>, which together with
    /// this bound satisfies the requirement for short-lived access tokens with
    /// refresh rotation.
    /// </para>
    /// <para>
    /// Accepted range, enforced at startup:
    /// <see cref="MinimumRefreshTokenExpirationDays"/> through
    /// <see cref="MaximumRefreshTokenExpirationDays"/> inclusive.
    /// </para>
    /// </remarks>
    public int RefreshTokenExpirationDays { get; set; } = 7;

    // MIGRATION: 30 days is a net-new default, and the setting itself is net-new. The legacy
    // application had no refresh mechanism, so there is no legacy ceiling to cite; the closest
    // legacy analogue is the persistent authentication cookie's PersistentCookieTimeout, which
    // bounded a cookie that was neither single-use nor revocable.

    /// <summary>
    /// Absolute lifetime of a refresh-token family, in days, measured from the sign-in that created
    /// it and never extended by rotation. Defaults to 30.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ceiling on a whole SESSION, where <see cref="RefreshTokenExpirationDays"/> is the
    /// ceiling on one TOKEN. The distinction is the point. Rotation issues a replacement with a
    /// fresh sliding expiry on every exchange, so the sliding bound alone limits only how long a
    /// token may sit <em>unused</em>: a client that keeps exchanging never reaches it, and the
    /// session lives forever. <c>Application/Abstractions/ITokenService.cs</c> requires exactly the
    /// opposite - "give every refresh token an absolute expiry as well as a used marker, so rotation
    /// cannot extend a session indefinitely" - and this setting is what makes that satisfiable.
    /// </para>
    /// <para>
    /// Reaching this ceiling is not an error and not a revocation: it means the caller must
    /// authenticate again, which is the only point at which a credential, an approval state and a
    /// lockout state are re-examined. Without it, a session established once could outlive the
    /// password that established it indefinitely.
    /// </para>
    /// <para>
    /// It must be at least <see cref="RefreshTokenExpirationDays"/>. A ceiling below the sliding
    /// lifetime would silently truncate every token to the ceiling and make the sliding setting
    /// unreachable, so the pair is validated together rather than independently.
    /// </para>
    /// </remarks>
    public int RefreshTokenAbsoluteExpirationDays { get; set; } = 30;

    /// <summary>
    /// Shortest usable <see cref="Secret"/>, in characters.
    /// </summary>
    /// <remarks>
    /// Thirty-two characters of UTF-8 carry at least 256 bits, which is the key size HMAC-SHA256
    /// requires, and it is the figure the repository's own published guidance already states
    /// (docs/project-guide.md L142). It is a technical floor rather than a policy preference, which
    /// is why it is a constant here rather than a configurable setting.
    /// </remarks>
    private const int MinimumSecretLength = 32;

    /// <summary>
    /// Minutes in a day, used to compare the two lifetimes, which are declared in different units.
    /// </summary>
    private const int MinutesPerDay = 24 * 60;

    /// <summary>
    /// Reports every way in which the values bound onto this instance are unusable, so that a
    /// misconfigured deployment fails while the host is starting rather than when the first
    /// caller tries to sign in.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or
    /// an empty collection when the instance is usable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every failure is reported, not just the first, because an operator fixing one setting per
    /// restart is the outcome a single-failure result produces. No message ever contains the
    /// secret or any part of it - only its length - because a start-up failure is written to the
    /// log, and a secret in a log is the arrangement this whole type exists to end.
    /// </para>
    /// <para>
    /// The two lifetimes are checked against each other as well as individually. A refresh token
    /// that expires no later than the access token it renews cannot renew anything, so the pair is
    /// incoherent rather than merely unusual, and the comparison is made in minutes because the
    /// two settings are declared in different units.
    /// </para>
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
        else if (Secret.Length < MinimumSecretLength)
        {
            // Every number reaches the message through an invariant conversion first, so the
            // concatenation below interpolates strings only and cannot pick up a culture. The
            // secret's LENGTH is safe to report; the secret itself never is.
            string actual = FormattableString.Invariant($"{Secret.Length}");
            string required = FormattableString.Invariant($"{MinimumSecretLength}");

            failures.Add(
                $"{SectionName}:{nameof(Secret)} is {actual} characters long, and at least "
                + $"{required} are required. HMAC-SHA256 signs with a key of at least 256 bits, so "
                + "a shorter secret is rejected by the signing library when the first token is "
                + "issued.");
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
