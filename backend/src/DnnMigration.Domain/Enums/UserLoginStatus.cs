namespace DnnMigration.Domain.Enums;

/// <summary>
/// Reports the outcome of a single authentication attempt.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the legacy <c>DotNetNuke.Security.Membership.UserLoginStatus</c> enumeration from
/// <c>Library/Components/Users/Membership/UserLoginStatus.vb</c>. All seven outcomes and all
/// seven numeric values are ported unchanged; only the member names differ, and the complete
/// old-to-new mapping is set out in the migration note immediately below this comment.
/// </para>
/// <para>
/// The numeric values are part of the contract and must never be renumbered. Three
/// independent facts in the legacy source show they are externally observable. First, the
/// value crosses a serialisation boundary: the legacy sign-in control persists the status in
/// ViewState and casts it back on the way out
/// (<c>Website/admin/Authentication/Login.ascx.vb</c>, line 221), and ViewState round-trips an
/// enumeration as its underlying integer. Second, the original author wrote every value out
/// explicitly even though the implicit declaration order would have produced exactly the same
/// numbers, which signals deliberate intent rather than an accident of ordering. Third, eight
/// published signatures pass the value by reference, making it a cross-assembly contract:
/// <c>MembershipProvider.vb</c> lines 90 and 91, <c>UserController.vb</c> lines 991, 1110 and
/// 1132, <c>AspNetMembershipProvider.vb</c> lines 1408 and 1429, and the audit helper at
/// <c>UserController.vb</c> line 66.
/// </para>
/// <para>
/// <see cref="UserLoginStatus.Failure"/> is the zero value and must stay there. Seven separate
/// legacy sites seed a status variable to failure before attempting authentication, so that any
/// path which neglects to assign a result fails closed. Because failure is zero,
/// <c>default(UserLoginStatus)</c> is a refusal too, which extends that guarantee to a value
/// nobody has assigned yet. Reordering so a successful outcome took zero would make an
/// unassigned value mean "authenticated", which is a security regression.
/// </para>
/// <para>
/// Legacy semantics treat every value that reaches the sign-in decision, other than
/// <see cref="UserLoginStatus.Failure"/>, as authenticated: the control tests the status for
/// inequality against failure
/// (<c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb</c>, line 187), so
/// <see cref="UserLoginStatus.SuperUser"/> and both insecure-password outcomes are completed
/// sign-ins. The one value that never reaches that test is
/// <see cref="UserLoginStatus.UserNotApproved"/>, which the same control intercepts first
/// (line 168) and treats as neither a refusal nor a completed sign-in. This rule is easy to
/// invert by accident and it is security-relevant, so callers must reproduce it exactly rather
/// than assuming a two-way split.
/// </para>
/// <para>
/// In the target architecture this enumeration is the failure-reason channel carried by
/// <c>Result&lt;T&gt;</c>. It replaces the legacy by-reference status argument threaded through
/// the eight signatures above, because no out or reference parameter appears in any target
/// public API: <c>ValidateUser(..., ByRef loginStatus)</c> becomes
/// <c>Task&lt;Result&lt;LoginResponse&gt;&gt; LoginAsync(LoginRequest, CancellationToken)</c>.
/// The seven outcomes are mutually exclusive and the values are not powers of two, so this is
/// deliberately not a bit field. Presentation concerns stay out of this type: wire formatting
/// belongs to the DTO boundary and message wording belongs to the application layer.
/// </para>
/// </remarks>
// MIGRATION: the legacy members were renamed from SCREAMING_SNAKE_CASE to PascalCase for
// idiomatic C#. The ordinals 0-6 are UNCHANGED, for the reasons given above, and must never be
// renumbered. Legacy name (ordinal) -> target name:
//   LOGIN_FAILURE               (0) -> Failure
//   LOGIN_SUCCESS               (1) -> Success
//   LOGIN_SUPERUSER             (2) -> SuperUser
//   LOGIN_USERLOCKEDOUT         (3) -> UserLockedOut
//   LOGIN_USERNOTAPPROVED       (4) -> UserNotApproved
//   LOGIN_INSECUREADMINPASSWORD (5) -> InsecureAdminPassword
//   LOGIN_INSECUREHOSTPASSWORD  (6) -> InsecureHostPassword
// MIGRATION: the member NAME is observable as well as the value. The legacy audit helper
// assigns the status's ToString() straight to the audit log type key
// (UserController.vb line 80), so the rename above changes those key strings. Mapping them onto
// stable log event names is owned by the application layer, which is where the legacy audit
// sites are re-expressed as structured log events. See MIGRATION_NOTES.md.
public enum UserLoginStatus
{
    /// <summary>
    /// Authentication was refused. This is the zero value, so it is also the result of
    /// <c>default(UserLoginStatus)</c>, which is what makes an unassigned status fail closed.
    /// The legacy code audits this outcome, paired with <see cref="UserLoginStatus.UserLockedOut"/>
    /// (<c>UserController.vb</c>, line 1138).
    /// </summary>
    Failure = 0,

    /// <summary>
    /// Authentication succeeded for an ordinary portal user.
    /// </summary>
    Success = 1,

    /// <summary>
    /// Authentication succeeded for a host, or super user, account. Such an account is
    /// authorised across every portal in the installation rather than within a single one, so
    /// the outcome is reported separately from <see cref="UserLoginStatus.Success"/> even
    /// though both are completed sign-ins.
    /// </summary>
    SuperUser = 2,

    /// <summary>
    /// The account is locked out by the membership store and was refused for that reason,
    /// independently of whether the supplied credentials were correct. The consecutive
    /// failed-attempt and lockout bookkeeping behind this outcome is introduced by the schema
    /// upgrade chain, which patches the ASP.NET membership update procedures rather than
    /// creating them
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/04.00.00.SqlDataProvider</c>, lines
    /// 31 and 119). The legacy code audits this outcome alongside
    /// <see cref="UserLoginStatus.Failure"/>.
    /// </summary>
    UserLockedOut = 3,

    /// <summary>
    /// The account exists but has not been approved and is awaiting verification. The legacy
    /// sign-in control intercepts this outcome before its authenticated test and drives the
    /// verification-code flow from it
    /// (<c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb</c>, line 168), so
    /// it counts as neither a refusal nor a completed sign-in.
    /// </summary>
    UserNotApproved = 4,

    /// <summary>
    /// Authentication succeeded for the built-in administrator account, which presented the
    /// product's well-known default credential. The legacy code reaches this outcome by
    /// promoting <see cref="UserLoginStatus.Success"/> after the credentials have already been
    /// accepted (<c>UserController.vb</c>, lines 1144 to 1147), so it is a completed sign-in
    /// carrying a security warning and not a refusal.
    /// </summary>
    InsecureAdminPassword = 5,

    /// <summary>
    /// Authentication succeeded for the built-in host account, which presented the product's
    /// well-known default credential. The legacy code reaches this outcome by promoting
    /// <see cref="UserLoginStatus.SuperUser"/> after the credentials have already been accepted
    /// (<c>UserController.vb</c>, lines 1149 to 1152), so it is a completed sign-in carrying a
    /// security warning and not a refusal.
    /// </summary>
    InsecureHostPassword = 6
}
