// MIGRATION: this service implements the sign-in vertical that
// Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb reached through
// UserController.ValidateUser (UserController.vb:L1132) and, beneath it, the membership provider's
// UserLogin (AspNetMembershipProvider.vb:L1429). The account-gate order below is taken verbatim from
// that provider: resolve the account within the portal, test the lockout, test approval - admitting a
// verification code in its place - and only then compare the credential
// (AspNetMembershipProvider.vb:L1445-L1508).
//
// MIGRATION: the by-reference status argument is gone. The legacy pair reported its outcome by mutating
// a ByRef enumeration while also returning an object, and it signalled failure by setting that object to
// Nothing - so a caller that ignored the status argument silently mistook a locked-out account for a
// missing one. The outcome is now the result's own failure reason and cannot be ignored.
//
// MIGRATION: the outcome is deliberately coarser than the legacy enumeration on the public sign-in path.
// The legacy screen was told exactly which gate closed and rendered a distinct message for each, which
// let an unauthenticated caller enumerate account names and discover that an account existed but was
// locked or unapproved. Every denial here reports one generic reason; the specific gate is recorded in
// the structured request log against the correlation identifier, which is where an administrator reads
// it.
//
// MIGRATION: the automatic unlock the legacy provider performed once a lockout aged out
// (AspNetMembershipProvider.vb:L1454-L1463) is NOT reproduced. It depended on a lockout-duration setting
// that the preserved password policy does not carry, and it performed a security-relevant write on an
// anonymous request path. A locked account is therefore cleared by the administrative unlock member on
// the user-management contract. Recorded as a deliberate behavioural difference.
//
// MIGRATION: the two weak-credential advisories are preserved as advisories on a SUCCESSFUL sign-in
// rather than as failures, which is what they were: UserController.vb:L1145-L1152 replaced an
// already-successful status with LOGIN_INSECUREADMINPASSWORD or LOGIN_INSECUREHOSTPASSWORD when the
// well-known default account was still using its shipped credential, and the caller was signed in
// regardless. The literal account names and credentials are carried over unchanged, because the whole
// value of the check is that it recognises exactly those shipped defaults.
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Services;

/// <summary>
/// Authenticates a caller against a portal and manages the lifetime of the tokens issued to it.
/// </summary>
/// <remarks>
/// <para>
/// This service owns the decision to issue a token: credential verification, the account gates, the
/// failure bookkeeping, and the resolution of the roles and permission keys that become claims. Minting,
/// signing and rotating the tokens themselves belong to the token service, which this service reaches
/// through an abstraction so that no signing library is resolvable from this project.
/// </para>
/// <para>
/// No member returns, echoes or logs a credential, a stored hash, a verification answer or a token value,
/// and credentials are compared only through the domain layer's hashing abstraction rather than by string
/// equality. The one exception to the string-comparison rule is the default-account advisory, which by
/// its nature must recognise two specific shipped literals and which never reports the value it matched.
/// </para>
/// </remarks>
public sealed class AuthService : IAuthService
{
    /// <summary>
    /// Reported when the submitted request is malformed.
    /// </summary>
    private const string RequestInvalidCode = "auth.request_invalid";

    /// <summary>
    /// The single generic denial. Every closed gate and every wrong credential reports this.
    /// </summary>
    private const string InvalidCredentialsCode = "auth.invalid_credentials";

    /// <summary>
    /// Reported in place of the generic denial only for a caller entitled to the distinction.
    /// </summary>
    private const string LockedOutCode = "auth.locked_out";

    /// <summary>
    /// Reported when a refresh token is unknown, expired, already redeemed or revoked.
    /// </summary>
    private const string InvalidRefreshTokenCode = "auth.invalid_refresh_token";

    /// <summary>
    /// Reported when a valid token names an account that no longer exists.
    /// </summary>
    private const string UserNotFoundCode = "auth.user_not_found";

    /// <summary>
    /// Advisory carried on a successful sign-in when the shipped administrator account is still using a
    /// shipped credential. Carries forward the legacy <c>LOGIN_INSECUREADMINPASSWORD</c> outcome.
    /// </summary>
    private const string InsecureAdminPasswordCode = "auth.insecure_admin_password";

    /// <summary>
    /// Advisory carried on a successful sign-in when the shipped host account is still using a shipped
    /// credential. Carries forward the legacy <c>LOGIN_INSECUREHOSTPASSWORD</c> outcome.
    /// </summary>
    private const string InsecureHostPasswordCode = "auth.insecure_host_password";

    /// <summary>
    /// Reason code the token service reports when a token record could not be persisted.
    /// </summary>
    private const string TokenStoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";

    /// <summary>
    /// Account name of the shipped portal administrator, matched by the default-account advisory.
    /// </summary>
    private const string ShippedAdministratorAccountName = "admin";

    /// <summary>
    /// Account name of the shipped host account, matched by the default-account advisory.
    /// </summary>
    private const string ShippedHostAccountName = "host";

    /// <summary>
    /// The credentials the shipped administrator account was distributed with
    /// (UserController.vb:L1145).
    /// </summary>
    private static readonly string[] ShippedAdministratorCredentials = ["admin", "dnnadmin"];

    /// <summary>
    /// The credentials the shipped host account was distributed with (UserController.vb:L1150).
    /// </summary>
    private static readonly string[] ShippedHostCredentials = ["host", "dnnhost"];

    private readonly IUserRepository _users;
    private readonly IPortalRepository _portals;
    private readonly IPermissionService _permissions;
    private readonly ITokenService _tokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly PasswordPolicyOptions _passwordPolicy;

    /// <summary>
    /// Initialises the service with the collaborators it verifies credentials and issues tokens through.
    /// </summary>
    /// <param name="users">Account resolution, credential state and the failure bookkeeping.</param>
    /// <param name="portals">Portal resolution, needed for the portal name and the entitlement test.</param>
    /// <param name="permissions">Portal-scope permission-key resolution for the claims.</param>
    /// <param name="tokens">Token minting, rotation and revocation.</param>
    /// <param name="passwordHasher">One-way credential verification and re-hash detection.</param>
    /// <param name="clock">The instant role windows and bookkeeping are evaluated against.</param>
    /// <param name="unitOfWork">Commit point for the account row a verified sign-in approves.</param>
    /// <param name="currentUser">The caller of the current request.</param>
    /// <param name="passwordPolicy">Bound policy supplying the lockout threshold and attempt window.</param>
    public AuthService(
        IUserRepository users,
        IPortalRepository portals,
        IPermissionService permissions,
        ITokenService tokens,
        IPasswordHasher passwordHasher,
        IClock clock,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        PasswordPolicyOptions passwordPolicy)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _passwordPolicy = passwordPolicy ?? throw new ArgumentNullException(nameof(passwordPolicy));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The gates are evaluated in the fixed order the legacy provider used and a denial never discloses
    /// which one closed, other than the lockout distinction offered to a caller already entitled to it.
    /// A portal that does not exist is answered with the same generic denial, so the member cannot be used
    /// to enumerate portals either.
    /// </para>
    /// <para>
    /// The verification code admitted in place of approval is the value the legacy provider compared
    /// against - the portal identifier and the account identifier joined by a hyphen - and presenting it
    /// approves the account exactly as the legacy provider did, persisting the account row afterwards as
    /// it did at AspNetMembershipProvider.vb:L1473.
    /// </para>
    /// <para>
    /// A wrong credential is recorded against the account, which is what locks it once the configured
    /// threshold of consecutive failures is reached inside the configured window. A correct credential
    /// clears the counters and, when the stored hash is one the hashing abstraction reports as outdated,
    /// replaces it - that replacement is the credential-migration path, because a legacy reversibly
    /// encrypted value cannot be verified against a one-way hash and an account whose value cannot be
    /// verified at all needs an administrative reset instead.
    /// </para>
    /// </remarks>
    public async Task<Result<LoginResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Result<LoginResponse>.Failure(
                RequestInvalidCode,
                "An account name and a credential are both required.");
        }

        // The tenant is assigned by the api layer from the alias-resolved request, and is unbindable from
        // the request body. Its absence means the transport could not determine which tenant the
        // credential was presented to, which is a malformed request rather than a rejected credential --
        // and it must never be defaulted, because zero and minus one are both real tenants.
        if (request.PortalId is not int portalId)
        {
            return Result<LoginResponse>.Failure(
                RequestInvalidCode,
                "The tenant the credential is being presented to could not be determined.");
        }

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        if (portal is null)
        {
            return Denied();
        }

        User? account = await ResolveAccountByNameAsync(portalId, request.Username, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return Denied();
        }

        (bool exists, string? storedHash, bool isApproved, bool isLockedOut) = await _users
            .GetCredentialStateAsync(account.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists || storedHash is null)
        {
            return Denied();
        }

        if (isLockedOut)
        {
            return CallerIsEntitledToDetail(portal)
                ? Result<LoginResponse>.Failure(
                    LockedOutCode,
                    FormattableString.Invariant(
                        $"Account {account.UserId} is locked and must be unlocked by an administrator."))
                : Denied();
        }

        bool approvedByVerification = false;
        if (!isApproved && !account.IsSuperUser)
        {
            string expected = FormattableString.Invariant($"{portalId}-{account.UserId}");
            if (!string.Equals(request.VerificationCode, expected, StringComparison.Ordinal))
            {
                return Denied();
            }

            if (!await _users.SetApprovalAsync(account.UserId, true, cancellationToken).ConfigureAwait(false))
            {
                return Denied();
            }

            account.IsApproved = true;
            approvedByVerification = true;
        }

        if (!_passwordHasher.Verify(request.Password, storedHash))
        {
            await _users.RecordFailedLoginAsync(
                account.UserId,
                _passwordPolicy.MaxInvalidPasswordAttempts,
                TimeSpan.FromMinutes(_passwordPolicy.PasswordAttemptWindowMinutes),
                _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);

            return Denied();
        }

        DateTime now = _clock.UtcNow;
        await _users.RecordSuccessfulLoginAsync(account.UserId, now, cancellationToken).ConfigureAwait(false);

        if (_passwordHasher.NeedsRehash(storedHash))
        {
            await _users
                .SetPasswordHashAsync(
                    account.UserId,
                    _passwordHasher.Hash(request.Password),
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (approvedByVerification)
        {
            // Reproduces the account-row persistence the legacy provider performed immediately after
            // approving a verified registration.
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        LoginResponse response = await IssueAsync(portal, account, now, cancellationToken)
            .ConfigureAwait(false);

        ResultReason? advisory = DetectShippedCredential(account, request.Username, request.Password);
        if (advisory is not ResultReason reason)
        {
            return Result<LoginResponse>.Success(response);
        }

        // MIGRATION: the legacy sign-in status enumeration carried two success-with-caveat values that
        // UserController.vb:L1144-L1152 promoted when a shipped default credential was presented - one for
        // the portal administrator account, one for the host account. Sign-in legitimately succeeded in
        // both cases, and forcing a credential change was the remediation, so both fold onto the response's
        // must-change advisory rather than onto a status field: no legacy status enumeration reaches the
        // wire. The reason accompanying this successful outcome keeps the two cases distinguishable to the
        // API edge, which is where they are told apart.
        response.MustChangePassword = true;

        return Result<LoginResponse>.Success(response, reason);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Rotation is performed by the token service, which retires the presented value and mints the
    /// successor in one atomic operation. Its four distinct rejection reasons - unknown, already redeemed,
    /// revoked and expired - are collapsed into the single reason this contract documents, because
    /// distinguishing them for an unauthenticated caller would say whether a guessed value had ever
    /// existed.
    /// </para>
    /// <para>
    /// The caller snapshot on the response is re-read from stored state here rather than copied from the
    /// retired token, so a role or permission change since the previous exchange is visible immediately.
    /// An account that has been deleted since its token was issued cannot be refreshed.
    /// </para>
    /// </remarks>
    public async Task<Result<LoginResponse>> RefreshAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Result<LoginResponse>.Failure(InvalidRefreshTokenCode, "The refresh token is not valid.");
        }

        Result<LoginResponse> rotated = await _tokens
            .RefreshAsync(request.RefreshToken, cancellationToken)
            .ConfigureAwait(false);

        if (rotated.IsFailure)
        {
            EnsureNotTokenStoreFailure(rotated.Reason);
            return Result<LoginResponse>.Failure(InvalidRefreshTokenCode, "The refresh token is not valid.");
        }

        LoginResponse response = rotated.Value;
        int portalId = response.User.PortalId;

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        User? account = await ResolveAccountByIdAsync(portalId, response.User.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (portal is null || account is null)
        {
            return Result<LoginResponse>.Failure(InvalidRefreshTokenCode, "The refresh token is not valid.");
        }

        response.User = await BuildSnapshotAsync(portal, account, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Result<LoginResponse>.Success(response);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately idempotent, so a token that is unknown, already redeemed or already revoked still
    /// succeeds. A sign-out that fails is worse than useless, because a client that cannot complete one is
    /// likely to keep the token; and reporting that a value was not found would make this member an oracle
    /// for whether a guessed value exists.
    /// </remarks>
    public async Task<Result> LogoutAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Result.Success();
        }

        Result revoked = await _tokens
            .RevokeRefreshTokenAsync(request.RefreshToken, cancellationToken)
            .ConfigureAwait(false);

        if (revoked.IsFailure)
        {
            EnsureNotTokenStoreFailure(revoked.Reason);
        }

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// An anonymous request is answered with no value rather than with a failure, because asking who the
    /// caller is when there is no caller is a legitimate question with a legitimate answer. The state is
    /// re-read rather than echoed from the token's claims, which is how a client observes a role or
    /// permission change without signing out and what keeps the answer honest about an account that has
    /// since been deleted.
    /// </remarks>
    public async Task<Result<CurrentUserDto?>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated
            || _currentUser.UserId is not int userId
            || _currentUser.PortalId is not int portalId)
        {
            return Result<CurrentUserDto?>.Success(null);
        }

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        User? account = await ResolveAccountByIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (portal is null || account is null)
        {
            return Result<CurrentUserDto?>.Failure(
                UserNotFoundCode,
                FormattableString.Invariant($"Account {userId} no longer exists in portal {portalId}."));
        }

        CurrentUserDto snapshot = await BuildSnapshotAsync(portal, account, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Result<CurrentUserDto?>.Success(snapshot);
    }

    /// <summary>
    /// Produces the single generic denial every closed gate reports.
    /// </summary>
    /// <returns>The generic failure.</returns>
    /// <remarks>
    /// The message is intentionally identical for every cause, so neither the code nor the wording
    /// distinguishes an unknown account from a wrong credential, a non-member, an unapproved account or a
    /// locked one.
    /// </remarks>
    private static Result<LoginResponse> Denied()
        => Result<LoginResponse>.Failure(InvalidCredentialsCode, "The account name or credential is not correct.");

    /// <summary>
    /// Decides whether the caller of this request may be told that an account is locked.
    /// </summary>
    /// <param name="portal">The portal the sign-in addresses.</param>
    /// <returns><see langword="true"/> when the caller already administers the portal or the installation.</returns>
    /// <remarks>
    /// The distinction is withheld from an anonymous caller because it would reveal that a named account
    /// exists. An already-authenticated administrator of the portal, or a host account, learns nothing it
    /// could not read from the account list, so it receives the actionable answer instead.
    /// </remarks>
    private bool CallerIsEntitledToDetail(Portal portal)
        => _currentUser.IsAuthenticated
            && (_currentUser.IsSuperUser
                || (_currentUser.UserId is int actor && portal.AdministratorId == actor));

    /// <summary>
    /// Resolves an account by name within a portal, admitting a host account that holds no membership.
    /// </summary>
    /// <param name="portalId">The portal signed in to.</param>
    /// <param name="username">The submitted account name.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The account, or <see langword="null"/> when none is in scope.</returns>
    private async Task<User?> ResolveAccountByNameAsync(
        int portalId,
        string username,
        CancellationToken cancellationToken)
    {
        User? account = await _users
            .GetByUsernameAsync(portalId, username, cancellationToken)
            .ConfigureAwait(false);

        if (account is not null)
        {
            return account;
        }

        account = await _users
            .GetByUsernameAsync(portalId: null, username, cancellationToken)
            .ConfigureAwait(false);

        return account?.IsSuperUser == true ? account : null;
    }

    /// <summary>
    /// Resolves an account by identifier within a portal, admitting a host account that holds no
    /// membership.
    /// </summary>
    /// <param name="portalId">The portal in question.</param>
    /// <param name="userId">The account identifier.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The account, or <see langword="null"/> when none is in scope.</returns>
    private async Task<User?> ResolveAccountByIdAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is not null)
        {
            return account;
        }

        account = await _users.GetAsync(portalId: null, userId, cancellationToken).ConfigureAwait(false);
        return account?.IsSuperUser == true ? account : null;
    }

    /// <summary>
    /// Issues a token pair for an account whose credential has already been accepted.
    /// </summary>
    /// <param name="portal">The portal signed in to.</param>
    /// <param name="account">The authenticated account.</param>
    /// <param name="asOfUtc">The instant role windows are evaluated against.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The issued pair, carrying a freshly read snapshot of the caller.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the token store could not record the pair. That is a server fault rather than a denial,
    /// so it is not one of the reasons this contract documents.
    /// </exception>
    private async Task<LoginResponse> IssueAsync(
        Portal portal,
        User account,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        CurrentUserDto snapshot = await BuildSnapshotAsync(portal, account, asOfUtc, cancellationToken)
            .ConfigureAwait(false);

        Result<LoginResponse> issued = await _tokens.IssueTokensAsync(
            account.UserId,
            portal.PortalId,
            account.Username,
            account.IsSuperUser,
            snapshot.Roles,
            snapshot.Permissions,
            cancellationToken).ConfigureAwait(false);

        if (issued.IsFailure)
        {
            EnsureNotTokenStoreFailure(issued.Reason);
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The token service declined to issue a token pair: {issued.Reason?.Code}."));
        }

        LoginResponse response = issued.Value;
        response.User = snapshot;

        // MIGRATION: the administrator-forced credential update was the highest-precedence advisory the
        // legacy post-credential check reported, read from the account row's own update flag
        // (UserController.vb:L1175-L1177, over the membership property at UserMembership.vb:L323). The
        // token service is deliberately not told it - it holds no account row - so the advisory is set
        // here, where the row is already in hand, and travels as a boolean rather than as a status value.
        response.MustChangePassword = account.UpdatePassword;

        return response;
    }

    /// <summary>
    /// Builds the caller snapshot carried on a token response and returned by the current-user read.
    /// </summary>
    /// <param name="portal">The portal the caller is signed in to.</param>
    /// <param name="account">The account.</param>
    /// <param name="asOfUtc">The instant role windows are evaluated against.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The snapshot.</returns>
    /// <remarks>
    /// Roles are resolved as of the supplied instant, so an assignment whose effective window has not
    /// opened or has already closed does not become a claim. Permission keys are resolved at portal scope
    /// through the permission service, which owns the caller-to-role-names rule and delegates the
    /// allow-and-deny precedence to the single evaluator. Those keys tell a client which affordances to
    /// render; they never stand in for the server-side authorisation policy, which re-evaluates on every
    /// request, so a resolution that cannot be completed yields no claims rather than blocking a sign-in
    /// whose credential was already accepted.
    /// </remarks>
    private async Task<CurrentUserDto> BuildSnapshotAsync(
        Portal portal,
        User account,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roles = await _users
            .ListRoleNamesAsync(portal.PortalId, account.UserId, asOfUtc, cancellationToken)
            .ConfigureAwait(false);

        Result<IReadOnlyList<string>> permissions = await _permissions
            .GetEffectivePermissionKeysAsync(
                portal.PortalId,
                account.UserId,
                moduleId: null,
                tabId: null,
                cancellationToken)
            .ConfigureAwait(false);

        return new CurrentUserDto
        {
            UserId = account.UserId,
            PortalId = portal.PortalId,
            PortalName = portal.PortalName,
            Username = account.Username,
            DisplayName = account.DisplayName,
            Email = account.Email ?? string.Empty,
            IsSuperUser = account.IsSuperUser,
            Roles = roles,
            Permissions = permissions.IsSuccess ? permissions.Value : [],
        };
    }

    /// <summary>
    /// Detects a well-known shipped account still using a shipped credential.
    /// </summary>
    /// <param name="account">The authenticated account.</param>
    /// <param name="username">The submitted account name.</param>
    /// <param name="password">The submitted credential.</param>
    /// <returns>The advisory to carry on the successful outcome, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The account names and credentials are the shipped defaults, matched exactly as the legacy check
    /// matched them. The name is compared case-insensitively because account names are resolved
    /// case-insensitively everywhere else; the credential is compared exactly. Neither the matched name
    /// nor the matched credential appears in the reported message.
    /// </remarks>
    private static ResultReason? DetectShippedCredential(User account, string username, string password)
    {
        if (!account.IsSuperUser
            && string.Equals(username, ShippedAdministratorAccountName, StringComparison.OrdinalIgnoreCase)
            && ShippedAdministratorCredentials.Contains(password, StringComparer.Ordinal))
        {
            return new ResultReason(
                InsecureAdminPasswordCode,
                "The portal administrator account is still using a shipped credential and should change it.");
        }

        if (account.IsSuperUser
            && string.Equals(username, ShippedHostAccountName, StringComparison.OrdinalIgnoreCase)
            && ShippedHostCredentials.Contains(password, StringComparer.Ordinal))
        {
            return new ResultReason(
                InsecureHostPasswordCode,
                "The host account is still using a shipped credential and should change it.");
        }

        return null;
    }

    /// <summary>
    /// Escalates a token-store outage, which is a server fault rather than an authentication outcome.
    /// </summary>
    /// <param name="reason">The reason the token service reported.</param>
    /// <exception cref="InvalidOperationException">Thrown when the store could not be written.</exception>
    /// <remarks>
    /// Every other reason the token service reports describes the presented token and is collapsed into
    /// this contract's single token failure. An unavailable store describes neither the caller nor the
    /// token, so reporting it as an authentication denial would tell a caller their token was invalid when
    /// it was not.
    /// </remarks>
    private static void EnsureNotTokenStoreFailure(ResultReason? reason)
    {
        if (reason is ResultReason failure
            && string.Equals(failure.Code, TokenStoreUnavailableCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The token store is unavailable, so no token could be issued, rotated or revoked.");
        }
    }
}
