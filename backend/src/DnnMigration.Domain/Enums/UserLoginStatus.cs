namespace DnnMigration.Domain.Enums;

/// <summary>Reports the outcome of a single authentication attempt.</summary>
/// <remarks>
/// <see cref="UserLoginStatus.Failure"/> is the zero value and must stay there. Seven separate legacy sites
/// seed a status variable to failure before attempting authentication, so that any path which neglects to
/// assign a result fails closed.
/// </remarks>
public enum UserLoginStatus
{
    /// <summary>
    /// Authentication was refused. This is the zero value, so it is also the result of
    /// <c>default(UserLoginStatus)</c>, which is what makes an unassigned status fail closed.
    /// </summary>
    Failure = 0,

    /// <summary>Authentication succeeded for an ordinary portal user.</summary>
    Success = 1,

    /// <summary>
    /// Authentication succeeded for a host, or super user, account. Such an account is authorised across
    /// every portal in the installation rather than within a single one, so the outcome is reported
    /// separately from <see cref="UserLoginStatus.Success"/> even though both are completed sign-ins.
    /// </summary>
    SuperUser = 2,

    /// <summary>
    /// The account is locked out by the membership store and was refused for that reason, independently of
    /// whether the supplied credentials were correct.
    /// </summary>
    UserLockedOut = 3,

    /// <summary>
    /// The account exists but has not been approved and is awaiting verification. The legacy sign-in
    /// control intercepts this outcome before its authenticated test and drives the verification-code flow
    /// from it, so it counts as neither a refusal nor a completed sign-in.
    /// </summary>
    UserNotApproved = 4,

    /// <summary>
    /// Authentication succeeded for the built-in administrator account, which presented the product's
    /// well-known default credential. The legacy code reaches this outcome by promoting <see
    /// cref="UserLoginStatus.Success"/> after the credentials have already been accepted, so it is a
    /// completed sign-in carrying a security warning and not a refusal.
    /// </summary>
    InsecureAdminPassword = 5,

    /// <summary>
    /// Authentication succeeded for the built-in host account, which presented the product's well-known
    /// default credential. The legacy code reaches this outcome by promoting <see
    /// cref="UserLoginStatus.SuperUser"/> after the credentials have already been accepted, so it is a
    /// completed sign-in carrying a security warning and not a refusal.
    /// </summary>
    InsecureHostPassword = 6
}
