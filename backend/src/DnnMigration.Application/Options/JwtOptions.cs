// MIGRATION: ASP.NET 2.0 Forms Authentication and its <machineKey> element
// (Website/release.config L89-L93, Triple-DES decryption key committed at L91)
// give way to stateless JWT bearer tokens configured by this type.

namespace DnnMigration.Application.Options;

/// <summary>
/// Strongly-typed settings for the JWT bearer tokens the API issues and
/// validates, read from the <c>Jwt</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// This is a plain settings carrier: it holds values and does nothing else. It
/// names no type from the hosting stack, the token libraries or the persistence
/// stack, which is what keeps the Application layer resting on the Domain layer
/// alone. Only <see langword="int"/> and <see langword="string"/> members appear
/// below.
/// </para>
/// <para>
/// Ownership boundaries, stated once here rather than repeated per member. This
/// file only declares the settings, and the Api layer reads them onto an
/// instance of this type at startup; token creation and signature checking
/// belong to <c>Infrastructure/Security/JwtTokenService.cs</c>; issuing and
/// rotating refresh tokens belongs to
/// <c>Infrastructure/Security/RefreshTokenStore.cs</c>; and the startup guard
/// that rejects a missing <see cref="Secret"/> belongs to
/// <c>Api/Extensions/AuthenticationExtensions.cs</c>. No signing, hashing,
/// validation or registration logic appears in this file, and none should be
/// added to it.
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
    /// placeholder. The legacy application did precisely that: its Forms
    /// Authentication element carried a literal Triple-DES decryption key in
    /// source control (Website/release.config L91), and because the membership
    /// provider stored passwords reversibly encrypted rather than hashed
    /// (Website/release.config L245, with retrieval enabled at L239), that one
    /// committed key laid open every stored credential. Ending that arrangement
    /// is why this file exists, and the repository's own risk register already
    /// states the standard — never commit secrets, rotate regularly
    /// (docs/project-guide.md L336).
    /// </para>
    /// <para>
    /// A deployment that supplies no secret must fail at startup rather than
    /// sign tokens with an empty key. That check is not made here, because this
    /// type performs no validation; it belongs to
    /// <c>Api/Extensions/AuthenticationExtensions.cs</c>, where the signing key
    /// is materialised and where failing fast can still stop the host from
    /// serving traffic.
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
    /// </remarks>
    public int ExpirationMinutes { get; set; } = 60;

    // MIGRATION: FormsAuthentication.SignOut has no stateless counterpart, so
    // logout becomes token expiry plus client-side discard; a bounded refresh
    // lifetime is what keeps that expiry meaningful, and is why this exists.
    // MIGRATION: 7 days is a net-new default. No legacy setting and no published
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
    /// </remarks>
    public int RefreshTokenExpirationDays { get; set; } = 7;
}
