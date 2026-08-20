namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// The successful payload of both <c>POST /api/v1/auth/login</c> and <c>POST /api/v1/auth/refresh</c>: the
/// bearer credential pair the client uses from that point on, the moment the access token lapses, three
/// remediation and expiry flags, and a snapshot of who the caller is.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY - THIS TYPE CARRIES BEARER CREDENTIALS AND MUST NEVER BE LOGGED. Both token members are live
/// credentials: anything holding <see cref="AccessToken"/> can act as the caller until it lapses, and
/// anything holding <see cref="RefreshToken"/> can obtain a fresh pair. Request and response logging must
/// exclude this body in full, and no structured log event may capture either member or any fragment of one.
/// </para>
/// <para>
/// The refresh credential is what the caller later submits back, as the body of the refresh request
/// contract in this same folder. It is rotated on every successful exchange - the presented value is
/// retired and a replacement is issued - so a captured value is single-use and a replay is detectable.
/// </para>
/// </remarks>
public sealed class LoginResponse
{
    /// <summary>
    /// The signed bearer credential, presented on subsequent requests in the <c>Authorization</c> header.
    /// Always populated on a successful outcome.
    /// </summary>
    /// <remarks>
    /// SECURITY: a live credential. Never log it, never place it in a URL, and never persist it where
    /// another origin can read it.
    /// </remarks>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// The single-use credential submitted to the refresh endpoint to obtain a replacement pair. Always
    /// populated on a successful outcome.
    /// </summary>
    /// <remarks>
    /// SECURITY: a live credential, and a longer-lived one than <see cref="AccessToken"/>. The same
    /// prohibition on logging applies with more force.
    /// </remarks>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// The absolute moment at which <see cref="AccessToken"/> stops being accepted, expressed in
    /// COORDINATED UNIVERSAL TIME (UTC) and never in a local or portal-preferred zone.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Whether the caller must change their credential before continuing. <see langword="false"/> means no
    /// such requirement.
    /// </summary>
    /// <remarks>
    /// This is not merely a client prompt. The access token carries the same signed decision and the API
    /// authorization gate re-evaluates the stored flag on every protected request, admitting only the
    /// authentication lifecycle and the account owner's credential-change route until it clears.
    /// </remarks>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// Whether the caller's credential is approaching its expiry. <see langword="false"/> means no such
    /// advisory.
    /// </summary>
    public bool PasswordExpiring { get; set; }

    /// <summary>
    /// Whether the caller must complete or correct their profile before continuing. <see langword="false"/>
    /// means no such requirement.
    /// </summary>
    /// <remarks>
    /// Derives from value 3 of the post-credential validation enumeration, which was BLOCKING and was the
    /// one advisory the legacy flow sent to a different step rather than to the credential interstitial.
    /// Because the legacy enumeration was single-valued and tested this case last, only while no other
    /// advisory had been raised, the legacy flow could never report it together with a credential advisory.
    /// </remarks>
    public bool MustUpdateProfile { get; set; }

    /// <summary>
    /// Who the caller is, as of the moment the credentials were issued. Always populated on a successful
    /// outcome and never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Embedded as a single member so that one sign-in round trip is enough to render the application
    /// shell, and so that the identity vocabulary has exactly one definition. Its members are deliberately
    /// not flattened into this type, which would duplicate that shape and give it two places to drift.
    /// </remarks>
    public CurrentUserDto User { get; set; } = new();
}
