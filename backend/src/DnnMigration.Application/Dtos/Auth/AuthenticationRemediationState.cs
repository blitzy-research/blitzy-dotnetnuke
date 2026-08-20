namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// The authoritative blocking work an authenticated account must complete before it may use the ordinary
/// application surface.
/// </summary>
/// <param name="MustChangePassword">Whether the account must replace its credential before continuing.</param>
/// <param name="MustUpdateProfile">
/// Whether the account must complete or correct its required profile properties before continuing.
/// </param>
/// <remarks>
/// This is an application value, not a wire envelope and not an authorisation decision. The authentication
/// service computes it from current stored state; the token service records it as signed claims; and the
/// API authorisation gate re-evaluates it before every protected endpoint so a stale token can neither
/// preserve a cleared requirement nor ignore one imposed after issue.
/// </remarks>
public readonly record struct AuthenticationRemediationState(
    bool MustChangePassword,
    bool MustUpdateProfile)
{
    /// <summary>Gets a value indicating whether either blocking requirement is active.</summary>
    public bool IsRequired => MustChangePassword || MustUpdateProfile;
}
