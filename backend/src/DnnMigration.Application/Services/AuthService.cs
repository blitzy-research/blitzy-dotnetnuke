using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>Authenticates a caller against one tenant and manages the lifetime of the tokens issued to it.</summary>
/// <remarks>
/// <para>
/// Every rejected credential receives one answer. An unknown account name, a wrong credential, an account
/// belonging to another tenant, an unapproved registration and a tenant that does not exist are reported
/// with the same code and the same wording, because any difference between them turns this service into an
/// oracle for account names or for the installation's tenants.
/// </para>
/// <para>
/// No member returns, echoes or records a credential, a stored hash, a verification code or a token value,
/// and credentials are compared only through <see cref="IPasswordHasher"/> or the bounded <see
/// cref="ILegacyCredentialVerifier"/> rather than by string equality.
/// </para>
/// </remarks>
public sealed class AuthService : IAuthService
{
    /// <summary>
    /// Reported when the submission cannot be acted on at all - no account name, no credential, or no
    /// tenant for it to be presented to. It describes the shape of the request, never the state of an
    /// account.
    /// </summary>
    private const string RequestInvalidCode = "auth.request_invalid";

    /// <summary>The single uniform denial. Every closed gate and every rejected credential reports this.</summary>
    private const string InvalidCredentialsCode = "auth.invalid_credentials";

    /// <summary>Reported in place of the uniform denial only for a caller entitled to the distinction.</summary>
    private const string LockedOutCode = "auth.locked_out";

    /// <summary>
    /// Reported when a presented refresh token is unknown, expired, already redeemed or revoked. The four
    /// are deliberately indistinguishable.
    /// </summary>
    private const string InvalidRefreshTokenCode = "auth.invalid_refresh_token";

    /// <summary>Reported when an authenticated caller's own account no longer exists.</summary>
    private const string UserNotFoundCode = "auth.user_not_found";

    /// <summary>Reported when the account or tenant behind a session can no longer be resolved.</summary>
    private const string RemediationSubjectUnresolvedCode = "auth.remediation.subject_unresolved";

    /// <summary>Reported when required profile state cannot be evaluated safely.</summary>
    private const string RemediationStoreUnavailableCode = "auth.remediation.store_unavailable";

    /// <summary>
    /// The approval outcome for an unapproved registration on a verified-registration tenant that supplied
    /// no verification code. Preserves the legacy message key <c>EnterCode</c>.
    /// </summary>
    private const string VerificationRequiredCode = "auth.verification_required";

    /// <summary>
    /// The approval outcome for an unapproved registration that supplied a verification code which was
    /// rejected. Preserves the legacy message key <c>InvalidCode</c>.
    /// </summary>
    private const string VerificationCodeInvalidCode = "auth.verification_code_invalid";

    /// <summary>
    /// The approval outcome for an unapproved registration on any tenant whose registration mode is not
    /// verified registration. Preserves the legacy message key <c>UserNotAuthorized</c>.
    /// </summary>
    private const string AccountNotApprovedCode = "auth.account_not_approved";

    /// <summary>
    /// Reported when a correct verification code was presented alongside a correct credential and the
    /// resulting approval could not be written to the membership store.
    /// </summary>
    /// <remarks>
    /// Distinct from the three approval outcomes on purpose. Those three describe something the caller did
    /// - no code, a wrong code, or a tenant that admits no self-verification - and each of them is
    /// actionable by the caller.
    /// </remarks>
    private const string ApprovalStoreUnavailableCode = "auth.approval_store_unavailable";

    /// <summary>
    /// Advisory carried on a successful sign-in when the shipped administrator account is still using a
    /// credential the product was distributed with. Carries forward the legacy
    /// <c>LOGIN_INSECUREADMINPASSWORD</c> outcome.
    /// </summary>
    private const string InsecureAdminPasswordCode = "auth.insecure_admin_password";

    /// <summary>
    /// Advisory carried on a successful sign-in when the shipped host account is still using a credential
    /// the product was distributed with. Carries forward the legacy <c>LOGIN_INSECUREHOSTPASSWORD</c>
    /// outcome.
    /// </summary>
    private const string InsecureHostPasswordCode = "auth.insecure_host_password";

    /// <summary>
    /// The reason code <see cref="ITokenService"/> reports when a token record could not be persisted. It
    /// describes neither the caller nor the presented token, so it is never folded into an authentication
    /// denial.
    /// </summary>
    private const string TokenStoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";

    /// <summary>
    /// Reported when a sign-out could not be confirmed because this instance does not hold the presented
    /// family.
    /// </summary>
    /// <remarks>
    /// It is a DISTINCT code from an outage on purpose: the caller must retain its credential and retry,
    /// exactly as it would for an outage, but an operator reading the trail needs to be able to tell "the
    /// session store is down" from "the session belongs to another replica".
    /// </remarks>
    private const string RevocationUnconfirmedCode = "SESSION_REVOCATION_STORE_UNAVAILABLE";

    /// <summary>
    /// Audit-only code recording that a submitted credential did not match. Never returned to a caller.
    /// </summary>
    /// <remarks>
    /// The four codes below exist so the trail can say WHICH question closed while every caller receives
    /// the same uniform refusal. They are deliberately separate from the reason codes above: those are part
    /// of the wire contract and must stay stable for the Api edge's status mapping, whereas these are read
    /// only by an operator and must never leak into a response.
    /// </remarks>
    private const string CredentialRejectedFailure = "credential_rejected";

    /// <summary>Audit-only code recording that the account was not approved for the tenant.</summary>
    private const string NotApprovedFailure = "not_approved";

    /// <summary>Audit-only code recording that the account holds no usable credential.</summary>
    private const string CredentialMissingFailure = "credential_missing";

    /// <summary>
    /// Audit-only code recording that the tenant or the account behind a presented refresh token could no
    /// longer be resolved - the account is deleted, or is no longer a member of the tenant it names.
    /// </summary>
    private const string AccountUnresolvedFailure = "account_unresolved";

    /// <summary>Resource type recorded on the credential-maintenance audit event.</summary>
    private const string CredentialResourceType = "Credential";

    /// <summary>Stable failure code recorded when the credential store refuses a replacement write.</summary>
    /// <remarks>
    /// One code for one condition, which is what an audit column is for: it names the CONDITION - the
    /// replacement could not be written - and leaves the replacement kind to the record's own properties
    /// and the exception type to the security-diagnostics channel.
    /// </remarks>
    private const string CredentialReplacementStoreFailureCode = "credential_replacement_store_failure";

    /// <summary>
    /// Reported when a sign-in proved a stored credential that must be replaced on use, and the replacement
    /// could not be written - so the sign-in is refused rather than completed over a credential that is
    /// still in its legacy reversible form.
    /// </summary>
    /// <remarks>
    /// Its reason token ends in <c>store_unavailable</c>, which the Api edge classifies as a dependency
    /// failure and answers <c>503</c>.
    /// </remarks>
    private const string CredentialMigrationStoreUnavailableCode =
        "auth.credential_migration_store_unavailable";

    /// <summary>Account name of the shipped tenant administrator, matched by the shipped-default advisory.</summary>
    private const string ShippedAdministratorAccountName = "admin";

    /// <summary>Account name of the shipped host account, matched by the shipped-default advisory.</summary>
    private const string ShippedHostAccountName = "host";

    /// <summary>
    /// Installation-wide setting naming the number of days after which a credential expires. Zero, and an
    /// absent setting, both disable the check.
    /// </summary>
    private const string PasswordExpiryHostSettingName = "PasswordExpiry";

    /// <summary>Installation-wide setting naming how many days before expiry the holder is reminded.</summary>
    private const string PasswordExpiryReminderHostSettingName = "PasswordExpiryReminder ";

    /// <summary>
    /// The reminder window applied when the installation configures none, measured from the legacy
    /// property's own initialiser.
    /// </summary>
    private const int DefaultPasswordExpiryReminderDays = 7;

    /// <summary>
    /// Installation-wide setting naming the number of minutes after which a locked account unlocks by
    /// itself. An explicit zero disables automatic unlocking altogether.
    /// </summary>
    private const string AutoAccountUnlockDurationHostSettingName = "AutoAccountUnlockDuration";

    /// <summary>
    /// The automatic-unlock window applied when the installation configures none, measured from the legacy
    /// fallback.
    /// </summary>
    private const int DefaultAutoAccountUnlockDurationMinutes = 10;

    /// <summary>Fingerprints of the two credentials the shipped administrator account was distributed with.</summary>
    /// <remarks>
    /// A digest suffices because the check is an EQUALITY TEST, never a lookup: the submitted credential is
    /// hashed and compared, so the check recognises exactly the same two values it always did and the
    /// behaviour is unchanged. The literal below is not usable at a sign-in prompt, which is the property
    /// the finding asks for.
    /// </remarks>
    private static readonly string[] ShippedAdministratorCredentialFingerprints =
    [
        "8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918",
        "6529da56f1a1f6ae50652d544c131050256bc52568611b75049a90ca047bb221",
    ];

    /// <summary>
    /// Fingerprints of the two credentials the shipped host account was distributed with. See <see
    /// cref="ShippedAdministratorCredentialFingerprints"/> for why these are digests.
    /// </summary>
    private static readonly string[] ShippedHostCredentialFingerprints =
    [
        "4740ae6347b0172c01254ff55bae5aff5199f4446e7f6d643d40185b3f475145",
        "06f25bcf0acf336cdee0f93659c7647d8ab7b9c3c7d1d6274887a8c1d1b758cb",
    ];

    private enum CredentialReplacementOutcome
    {
        NotRequired,
        WorkFactorUpgraded,
        LegacyCredentialMigrated,
        WorkFactorUpgradeFailed,
        LegacyMigrationFailed,

        /// <summary>
        /// The credential changed between this sign-in reading it and replacing it, so nothing was written.
        /// </summary>
        /// <remarks>
        /// Distinct from the two failure members because nothing failed: the store refused the write on
        /// purpose, and the representation this sign-in verified is no longer the account's credential. It
        /// is therefore not a condition to record and continue past - it is a reason to refuse the sign-in.
        /// </remarks>
        CredentialSuperseded,
    }

    /// <summary>
    /// What a credential-replacement attempt produced, and - when the store itself threw - the TYPE of the
    /// exception it threw.
    /// </summary>
    /// <param name="Outcome">Which of the five replacement outcomes occurred.</param>
    /// <param name="StoreFailureType">
    /// The name of the exception type the credential store raised, or <see langword="null"/> when no
    /// exception occurred.
    /// </param>
    /// <remarks>
    /// The second member exists so the type name can reach the security-diagnostics channel from the ONE
    /// place that also holds the tenant. The failure has two shapes - the store threw, or the store
    /// answered that no row was updated - and both must be recorded once, with the tenant, under the same
    /// closed diagnostic member.
    /// </remarks>
    /// <param name="WrittenValue">
    /// The representation this replacement stored, or <see langword="null"/> when nothing was written.
    /// </param>
    private readonly record struct CredentialReplacement(
        CredentialReplacementOutcome Outcome,
        string? StoreFailureType,
        string? WrittenValue);

    private readonly IUserRepository _users;
    private readonly IPortalRepository _portals;
    private readonly IRoleRepository _roles;
    private readonly IPermissionService _permissions;
    private readonly IUserService _accounts;
    private readonly ITokenService _tokens;
    private readonly IRefreshTokenStore _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILegacyCredentialVerifier _legacyCredentials;
    private readonly IClock _clock;
    private readonly IHostSettingsService _hostSettings;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditSink _audit;
    private readonly PasswordPolicyOptions _passwordPolicy;
    private readonly ISecurityDiagnostics _diagnostics;

    /// <summary>
    /// Initialises the service with the collaborators it verifies credentials and issues tokens through.
    /// </summary>
    /// <param name="users">Account resolution, credential state and the sign-in bookkeeping.</param>
    /// <param name="portals">Tenant resolution.</param>
    /// <param name="roles">
    /// Role assignments, asked ONE question: does the signed-in caller hold the role this portal designates
    /// as its administrator, with an assignment that is active at the instant being judged.
    /// </param>
    /// <param name="permissions">Tenant-scope permission-key resolution for the claims.</param>
    /// <param name="accounts">
    /// The account-administration vertical, asked ONE question: whether the admitted account must complete
    /// its profile.
    /// </param>
    /// <param name="tokens">Token minting, rotation and revocation.</param>
    /// <param name="refreshTokens">Refresh-token inspection before any state-changing rotation is attempted.</param>
    /// <param name="passwordHasher">One-way credential verification and replacement detection.</param>
    /// <param name="legacyCredentials">
    /// Migration-only verification of bounded legacy membership representations.
    /// </param>
    /// <param name="clock">The instant every write and every expiry comparison is judged against.</param>
    /// <param name="hostSettings">
    /// Installation-wide settings, read for the two credential-expiry windows the legacy post-credential
    /// validation consulted.
    /// </param>
    /// <param name="unitOfWork">Commit point for the tracked account row a verified registration approves.</param>
    /// <param name="currentUser">The already-authenticated caller of the current request, where there is one.</param>
    /// <param name="audit">Records the sign-in, renewal and sign-out outcomes under the legacy event names.</param>
    /// <param name="passwordPolicy">Bound policy supplying the lock threshold and the attempt window.</param>
    /// <param name="diagnostics">
    /// The route by which this service reports a security-relevant anomaly it has decided not to fail the
    /// request over.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// No LOGGER is taken, and that omission is forced rather than chosen: <c>ILogger&lt;T&gt;</c> is not
    /// resolvable in this project, and the audit entry in this file's header records the compilation that
    /// proves it and names the Api-layer control that records sign-in outcomes instead.
    /// </remarks>
    public AuthService(
        IUserRepository users,
        IPortalRepository portals,
        IRoleRepository roles,
        IPermissionService permissions,
        IUserService accounts,
        ITokenService tokens,
        IRefreshTokenStore refreshTokens,
        IPasswordHasher passwordHasher,
        ILegacyCredentialVerifier legacyCredentials,
        IClock clock,
        IHostSettingsService hostSettings,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IAuditSink audit,
        PasswordPolicyOptions passwordPolicy,
        ISecurityDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(legacyCredentials);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hostSettings);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(passwordPolicy);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _users = users;
        _portals = portals;
        _roles = roles;
        _permissions = permissions;
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _legacyCredentials = legacyCredentials;
        _clock = clock;
        _hostSettings = hostSettings;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _audit = audit;
        _passwordPolicy = passwordPolicy;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
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

        // The tenant is assigned by the Api layer from the alias-resolved request and is unbindable from
        // the request body.
        if (request.PortalId is not int portalId)
        {
            return Result<LoginResponse>.Failure(
                RequestInvalidCode,
                "The tenant the credential is being presented to could not be determined.");
        }

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        User? account = portal is null
            ? null
            : await ResolveAccountByNameAsync(portalId, request.Username, cancellationToken)
                .ConfigureAwait(false);

        (
            bool exists,
            string? storedValue,
            PasswordFormat? storedFormat,
            string? passwordSalt,
            bool isApproved,
            bool isLockedOut) = account is null
            ? (false, null, null, null, false, false)
            : await _users
                .GetCredentialStateAsync(account.UserId, cancellationToken)
                .ConfigureAwait(false);

        // EXACTLY ONE EXPENSIVE CURRENT-HASH COMPARISON, ON EVERY STRUCTURALLY VALID ATTEMPT, WHATEVER WAS
        // FOUND. When there is no current BCrypt representation to compare against, the decoy the hashing
        // abstraction publishes stands in for one: it is produced at the current work factor from input the
        // implementation does not retain, so the comparison costs what a genuine one costs and cannot
        // succeed.
        LegacyCredentialVerification legacyVerification =
            exists && storedValue is not null && storedFormat is PasswordFormat format
                ? _legacyCredentials.Verify(request.Password, storedValue, format, passwordSalt)
                : LegacyCredentialVerification.Current;

        // One current-cost BCrypt comparison still runs for every structurally valid request. A recognised
        // legacy representation is paired with the hasher's decoy rather than handed to BCrypt, then
        // checked by the isolated verifier.
        bool currentCredentialAccepted = _passwordHasher.Verify(
            request.Password,
            legacyVerification.IsLegacyCredential
                ? _passwordHasher.UnmatchableHash
                : storedValue ?? _passwordHasher.UnmatchableHash);
        bool credentialAccepted = currentCredentialAccepted || legacyVerification.IsMatch;

        if (portal is null || account is null || !exists || storedValue is null)
        {
            // ONE REFUSAL, FOUR CAUSES, AND THE TRAIL STILL SEPARATES THEM. The caller is told nothing
            // about which of the four closed - that uniformity is the whole point of collecting them here -
            // but the audit record carries whatever was actually established, so an operator can still
            // distinguish a probe against an unknown tenant from one against an unknown account from an
            // account that exists and holds no credential.
            RecordSignInOutcome(
                UserLoginStatus.Failure,
                portalId,
                account?.UserId);
            return Denied();
        }

        UserLoginStatus loginStatus = UserLoginStatus.Failure;
        bool approvedByVerification = false;

        bool approvalCouldNotBeRecorded = false;

        if (isLockedOut
            && !await TryAutomaticUnlockAsync(account, cancellationToken).ConfigureAwait(false))
        {
            loginStatus = UserLoginStatus.UserLockedOut;
        }
        else if (credentialAccepted)
        {
            // The credential is proven from here down. The superuser branch is the provider's own: it
            // validated a host account against the installation rather than against the tenant.
            if (!isApproved && !account.IsSuperUser)
            {
                Result<bool> validEmail = await _accounts
                    .IsEmailValidAsync(portalId, account.Email ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);

                if (validEmail.IsFailure || !validEmail.Value)
                {
                    // Security_EmailValidation is an admission rule rather than a display hint. An existing
                    // pending account whose address no longer satisfies the portal's configured expression
                    // remains pending even when the predictable legacy verification code is presented.
                    loginStatus = UserLoginStatus.UserNotApproved;
                }
                else
                {
                    // Its predictability is why this comparison must sit behind the credential and not in
                    // front of it.
                    string expectedVerificationCode = FormattableString.Invariant($"{portalId}-{account.UserId}");

                    if (!string.Equals(request.VerificationCode, expectedVerificationCode, StringComparison.Ordinal))
                    {
                        loginStatus = UserLoginStatus.UserNotApproved;
                    }
                    else if (!await _users
                        .SetApprovalAsync(account.UserId, true, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        loginStatus = UserLoginStatus.UserNotApproved;
                        approvalCouldNotBeRecorded = true;
                    }
                    else
                    {
                        account.IsApproved = true;
                        approvedByVerification = true;

                        // Reached only from inside the non-superuser branch, so the ordinary success is the
                        // correct member here and no superuser test is needed.
                        loginStatus = UserLoginStatus.Success;
                    }
                }
            }
            else
            {
                loginStatus = account.IsSuperUser
                    ? UserLoginStatus.SuperUser
                    : UserLoginStatus.Success;
            }
        }

        // Otherwise the status remains Failure: the credential was wrong, whatever the account's approval
        // state. this is a second deliberate consequence of the reordering, and it is an improvement rather
        // than a side effect.

        loginStatus = PromoteShippedCredentialOutcome(loginStatus, request.Username, request.Password);

        // ONE emission covers every REFUSAL from here down, because this is the last statement that can
        // change the outcome: nothing below reassigns it, and every refusing path below reads it rather
        // than recomputing it.
        if (!Admitted(loginStatus))
        {
            RecordSignInOutcome(
                loginStatus,
                portalId,
                account.UserId);
        }

        switch (loginStatus)
        {
            case UserLoginStatus.UserLockedOut:
                return await CallerIsEntitledToDetailAsync(portal, cancellationToken).ConfigureAwait(false)
                    ? Result<LoginResponse>.Failure(
                        LockedOutCode,
                        FormattableString.Invariant(
                            $"Account {account.UserId} is locked and must be unlocked by an administrator."))
                    : Denied();

            case UserLoginStatus.UserNotApproved:
                if (approvalCouldNotBeRecorded)
                {
                    return Result<LoginResponse>.Failure(
                        ApprovalStoreUnavailableCode,
                        "The registration could not be verified because a required store did not respond. No change was made; try again.");
                }

                return Result<LoginResponse>.Failure(
                    DetermineApprovalOutcome(portal, request.VerificationCode),
                    ApprovalOutcomeDetail(portal, request.VerificationCode));

            case UserLoginStatus.Failure:
                // The legacy recorded an audit entry for exactly this member and the locked-out member and
                // nothing else.
                MembershipWriteOutcome failureRecorded = await _users.RecordFailedLoginAsync(
                    account.UserId,
                    _passwordPolicy.MaxInvalidPasswordAttempts,
                    TimeSpan.FromMinutes(_passwordPolicy.PasswordAttemptWindowMinutes),
                    _clock.UtcNow,
                    cancellationToken).ConfigureAwait(false);

                EnsureMembershipBookkeepingRan(failureRecorded, account.UserId);

                return Denied();

            default:
                break;
        }

        DateTime now = _clock.UtcNow;

        (bool mustChangePassword, bool passwordExpiring) = await EvaluateCredentialAdvisoriesAsync(
            account,
            now,
            cancellationToken).ConfigureAwait(false);

        ResultReason? advisory = ShippedCredentialAdvisory(loginStatus);

        // A shipped default credential has no durable marker of its own. Once it has been recognised after
        // a successful comparison, promote it onto the same UpdatePassword flag used by an administrator
        // forced change.
        bool forcedChangeRecorded = advisory is not null && !account.UpdatePassword;
        if (forcedChangeRecorded)
        {
            account.UpdatePassword = true;
        }

        MembershipWriteOutcome successRecorded = await _users
            .RecordSuccessfulLoginAsync(account.UserId, now, cancellationToken)
            .ConfigureAwait(false);

        // Read for the same reason the failure counterpart is. Clearing the counters is the other half of
        // the lock-out control: it is what stops failures accumulated over weeks from eventually locking an
        // account whose owner has been signing in successfully in between.
        EnsureMembershipBookkeepingRan(successRecorded, account.UserId);

        // THE REPLACEMENT REPORTS WHICH REPLACEMENT IT WAS, NOT MERELY WHETHER IT WORKED. Two revisions
        // wrote this call, one answering a boolean and one answering a closed outcome.
        CredentialReplacement replacement = await TryReplaceCredentialAsync(
            account.UserId,
            request.Password,
            storedValue,
            legacyVerification.IsMatch,
            now,
            cancellationToken).ConfigureAwait(false);

        if (replacement.Outcome == CredentialReplacementOutcome.WorkFactorUpgradeFailed)
        {
            // A cost upgrade that could not be stored, which must not fail a sign-in whose credential was
            // correct - see that method for why - but must not vanish either: an installation whose stored
            // credentials are stuck below the cost it believes it enforces has no other way of finding out.
            _diagnostics.Record(
                SecurityDiagnosticEvent.CredentialWorkFactorUpgradeFailed,
                portalId,
                account.UserId,
                replacement.StoreFailureType);
        }
        else if (replacement.Outcome == CredentialReplacementOutcome.LegacyMigrationFailed)
        {
            _diagnostics.Record(
                SecurityDiagnosticEvent.LegacyCredentialMigrationFailed,
                portalId,
                account.UserId,
                replacement.StoreFailureType);

            RecordSignInOutcome(UserLoginStatus.Failure, portalId, account.UserId);

            return Result<LoginResponse>.Failure(
                CredentialMigrationStoreUnavailableCode,
                "The sign-in could not be completed because the credential store did not accept the "
                + "replacement of a stored credential that must be replaced on use. Nothing was changed; try "
                + "again, or ask an administrator to reset the credential.");
        }
        else if (replacement.Outcome == CredentialReplacementOutcome.CredentialSuperseded)
        {
            // The credential changed between this request reading it and replacing it, so the store refused
            // the write ON PURPOSE and the representation this sign-in verified is not the account's
            // credential any more.
            _diagnostics.Record(
                SecurityDiagnosticEvent.CredentialChangedDuringSignIn,
                portalId,
                account.UserId,
                replacement.StoreFailureType);

            RecordSignInOutcome(UserLoginStatus.Failure, portalId, account.UserId);

            return Denied();
        }
        else if (replacement.Outcome == CredentialReplacementOutcome.LegacyCredentialMigrated)
        {
            // The successful migration is an audit event because it changes the credential representation
            // an operator must account for.
            _audit.Record(new AuditEvent(AuditEventNames.LegacyCredentialMigrated)
            {
                PortalId = portalId,
                ActorUserId = account.UserId,

                SubjectUserId = account.UserId,
                ResourceType = CredentialResourceType,
                ResourceId = account.UserId.ToString(CultureInfo.InvariantCulture),
                Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["PreviousFormat"] = storedFormat?.ToString(),
                },
            });
        }

        if (approvedByVerification || forcedChangeRecorded)
        {
            // The one branch of a sign-in that changes a tracked row, and therefore the only one that opens
            // the transaction boundary. It reproduces the account persistence the provider performed
            // immediately after approving a verified registration.
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // ⚠ THE LAST LOOK AT THE CREDENTIAL, IMMEDIATELY BEFORE A SESSION IS MINTED FROM IT. Everything
        // above decided from one read taken before the comparison, and that comparison is the one
        // deliberately expensive step on this path - so the interval between reading the credential and
        // issuing a session is long enough to matter, and the change most likely to land inside it is an
        // administrator resetting the credential of an account they believe is compromised.
        (bool stillExists, string? currentValue, _, _, _, _) = await _users
            .GetCredentialStateAsync(account.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (!CredentialIsStill(stillExists, currentValue, storedValue, replacement.WrittenValue))
        {
            _diagnostics.Record(
                SecurityDiagnosticEvent.CredentialChangedDuringSignIn,
                portalId,
                account.UserId,
                null);

            RecordSignInOutcome(UserLoginStatus.Failure, portalId, account.UserId);

            return Denied();
        }

        Result<bool> profileRemediation = await EvaluateProfileRemediationAsync(
            portal.PortalId,
            account,
            cancellationToken).ConfigureAwait(false);

        if (profileRemediation.IsFailure)
        {
            return Result<LoginResponse>.Failure(profileRemediation.Reason!);
        }

        AuthenticationRemediationState remediation = new(
            mustChangePassword || advisory is not null,
            profileRemediation.Value);

        Result<LoginResponse> issued = await IssueAsync(
            portal,
            account,
            remediation,
            cancellationToken)
            .ConfigureAwait(false);

        if (issued.IsFailure)
        {
            // The credential was accepted and the session could not be recorded. The reason travels
            // unchanged, exactly as IAuthService documents, so the caller learns that a dependency is
            // unavailable rather than being told its credential was wrong.
            return issued;
        }

        (bool presentAfterIssue, string? valueAfterIssue, _, _, _, _) = await _users
            .GetCredentialStateAsync(account.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (!CredentialIsStill(presentAfterIssue, valueAfterIssue, storedValue, replacement.WrittenValue))
        {
            _diagnostics.Record(
                SecurityDiagnosticEvent.CredentialChangedDuringSignIn,
                portalId,
                account.UserId,
                null);

            // A revocation that cannot be persisted is escalated rather than absorbed, exactly as the
            // rotation path does: reporting a store outage as a credential refusal would leave the family
            // exchangeable while telling the caller its credential was the problem.
            Result revoked = await _tokens
                .RevokeAllRefreshTokensAsync(account.UserId, cancellationToken)
                .ConfigureAwait(false);

            RecordSignInOutcome(UserLoginStatus.Failure, portalId, account.UserId);

            return revoked.IsFailure && IsTokenStoreFailure(revoked.Reason)
                ? Result<LoginResponse>.Failure(revoked.Reason!)
                : Denied();
        }

        LoginResponse response = issued.Value;

        response.PasswordExpiring = passwordExpiring;

        // M-07: the ACCEPTED outcome, recorded after the sign-in has fully succeeded so that no accepted
        // event can describe a sign-in that then failed to issue.
        RecordLoginOutcome(
            loginStatus,
            portalId,
            account,
            AuditOutcome.Succeeded,
            advisory?.Code,
            response.MustChangePassword,
            response.MustUpdateProfile);

        return advisory is null
            ? Result<LoginResponse>.Success(response)
            : Result<LoginResponse>.Success(response, advisory);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>The membership gates are re-read too, and they refuse the exchange.</b> Approval and lock-out are
    /// held in the external membership store rather than on the account row, so re-reading the account did
    /// not observe them.
    /// </remarks>
    public async Task<Result<LoginResponse>> RefreshAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken)
            || string.IsNullOrWhiteSpace(request.ClientBinding))
        {
            return Result<LoginResponse>.Failure(InvalidRefreshTokenCode, "The refresh token is not valid.");
        }

        RefreshTokenInspection inspection = await _refreshTokens
            .InspectAsync(request.RefreshToken, request.ClientBinding, cancellationToken)
            .ConfigureAwait(false);

        if (inspection.Outcome != RefreshTokenOutcome.Succeeded || inspection.Subject is null)
        {
            return inspection.Outcome == RefreshTokenOutcome.StoreUnavailable
                ? Result<LoginResponse>.Failure(
                    TokenStoreUnavailableCode,
                    "The refresh token store could not be reached.")
                : Result<LoginResponse>.Failure(InvalidRefreshTokenCode, "The refresh token is not valid.");
        }

        int portalId = inspection.Subject.PortalId;
        int userId = inspection.Subject.UserId;

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        User? account = await ResolveAccountByIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (portal is null || account is null)
        {
            // The tenant is gone, or the account is deleted or no longer a member of it. Both are
            // terminal for the session, so the family goes with it.
            return await RefuseRotationAsync(
                userId,
                portalId,
                AccountUnresolvedFailure,
                cancellationToken).ConfigureAwait(false);
        }

        // THE ELIGIBILITY RE-READ. Rotation renews a session, and a session must not outlive the conditions
        // that were required to start it.
        (
            bool exists,
            string? storedValue,
            _,
            _,
            bool isApproved,
            bool isLockedOut) = await _users
            .GetCredentialStateAsync(account.UserId, cancellationToken)
            .ConfigureAwait(false);

        string? ineligible = !exists || storedValue is null
            ? CredentialMissingFailure
            : isLockedOut
                ? AuditFailureCodeFor(UserLoginStatus.UserLockedOut)
                : !isApproved && !account.IsSuperUser
                    ? AuditFailureCodeFor(UserLoginStatus.UserNotApproved)
                    : null;

        if (ineligible is not null)
        {
            return await RefuseRotationAsync(
                account.UserId,
                portalId,
                ineligible,
                cancellationToken).ConfigureAwait(false);
        }

        DateTime now = _clock.UtcNow;
        (bool mustChangePassword, bool passwordExpiring) = await EvaluateCredentialAdvisoriesAsync(
            account,
            now,
            cancellationToken).ConfigureAwait(false);
        // FAILS CLOSED. Profile completion is an authorisation input rather than an advisory, so a store
        // that cannot be read is not evidence that the requirement is satisfied: the rotation is refused
        // and the token family ended, exactly as an unresolvable account is.
        Result<bool> profileRemediation = await EvaluateProfileRemediationAsync(
            portalId,
            account,
            cancellationToken).ConfigureAwait(false);

        if (profileRemediation.IsFailure)
        {
            return await RefuseRotationAsync(
                account.UserId,
                portalId,
                RemediationStoreUnavailableCode,
                cancellationToken).ConfigureAwait(false);
        }

        bool mustUpdateProfile = profileRemediation.Value;

        Result<LoginResponse> rotated = await _tokens
            .RefreshAsync(request.RefreshToken, request.ClientBinding, cancellationToken)
            .ConfigureAwait(false);

        if (rotated.IsFailure)
        {
            return IsTokenStoreFailure(rotated.Reason)
                ? Result<LoginResponse>.Failure(rotated.Reason!)
                : Result<LoginResponse>.Failure(InvalidRefreshTokenCode, "The refresh token is not valid.");
        }

        LoginResponse response = rotated.Value;
        response.User = BuildIdentitySnapshot(portal, account);
        response.MustChangePassword = mustChangePassword;
        response.PasswordExpiring = passwordExpiring;
        response.MustUpdateProfile = mustUpdateProfile;

        _audit.Record(new AuditEvent(AuditEventNames.SessionRenewed)
        {
            PortalId = portalId,
            ActorUserId = account.UserId,
            SubjectUserId = account.UserId,
        });

        return Result<LoginResponse>.Success(response);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: ending a session has no exact counterpart, and this is the single most consequential
    /// behavioural difference on this service. <c>PortalSecurity.vb</c> L77 declared <c>Public Sub
    /// SignOut()</c>, which called the Forms-authentication sign-out and then destroyed four further
    /// cookies by name - the language, authentication-type, tenant-alias and tenant-roles cookies -
    /// back-dating the last two by thirty years so the browser dropped them at once; the same file's shared
    /// <c>ClearRoles()</c> performed the roles half alone.
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

        if (revoked.IsFailure && IsTokenStoreFailure(revoked.Reason))
        {
            return Result.Failure(revoked.Reason!);
        }

        // Recorded from the ALREADY-AUTHENTICATED caller rather than from the presented token, because this
        // member is deliberately silent about whether the token was genuine and must not learn anything
        // from it that it would then record.
        if (revoked.IsFailure)
        {
            return Result.Failure(
                RevocationUnconfirmedCode,
                "The sign-out could not be confirmed, because this instance holds no such session. "
                + "Retain the refresh token and retry.");
        }

        _audit.Record(new AuditEvent(AuditEventNames.SessionEnded)
        {
            PortalId = _currentUser.IsAuthenticated ? _currentUser.PortalId : null,
            ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
        });

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationRemediationState>> EvaluateRemediationAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        User? account = await ResolveAccountByIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (portal is null || account is null)
        {
            return Result<AuthenticationRemediationState>.Failure(
                RemediationSubjectUnresolvedCode,
                "The account or tenant behind the session can no longer be resolved.");
        }

        DateTime now = _clock.UtcNow;
        (bool mustChangePassword, _) = await EvaluateCredentialAdvisoriesAsync(
            account,
            now,
            cancellationToken).ConfigureAwait(false);

        Result<bool> profileRemediation = await EvaluateProfileRemediationAsync(
            portalId,
            account,
            cancellationToken).ConfigureAwait(false);

        if (profileRemediation.IsFailure)
        {
            return Result<AuthenticationRemediationState>.Failure(profileRemediation.Reason!);
        }

        return Result<AuthenticationRemediationState>.Success(
            new AuthenticationRemediationState(mustChangePassword, profileRemediation.Value));
    }

    /// <inheritdoc />
    /// <remarks>
    /// An anonymous request is answered with no value rather than with a failure, because asking who the
    /// caller is when there is no caller is a legitimate question with a legitimate answer.
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
            // The one place a specific answer is safe: the caller has already proved who it is, so it
            // learns nothing here it did not already know about itself.
            return Result<CurrentUserDto?>.Failure(
                UserNotFoundCode,
                FormattableString.Invariant($"Account {userId} no longer exists in portal {portalId}."));
        }

        CurrentUserDto snapshot = await BuildSnapshotAsync(portal, account, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Result<CurrentUserDto?>.Success(snapshot);
    }

    /// <summary>Produces the single uniform denial that every closed gate reports.</summary>
    /// <returns>The uniform failure.</returns>
    /// <remarks>
    /// The code and the wording are intentionally identical for every cause, so nothing distinguishes an
    /// unknown account from a rejected credential, a non-member, an account with no credential on file or a
    /// tenant that does not exist. Every one of those is a gate that closes WITHOUT the caller having
    /// proved anything, which is precisely the set that must be indistinguishable.
    /// </remarks>
    private static Result<LoginResponse> Denied()
        => Result<LoginResponse>.Failure(InvalidCredentialsCode, "The account name or credential is not correct.");

    /// <summary>Records one sign-in outcome on the audit trail.</summary>
    /// <param name="outcome">The outcome the attempt produced.</param>
    /// <param name="portalId">The tenant the credential was presented to.</param>
    /// <param name="userId">The account the attempt resolved to, or <see langword="null"/> for none.</param>
    private void RecordSignInOutcome(
        UserLoginStatus outcome,
        int portalId,
        int? userId)
    {
        bool accepted = Admitted(outcome);

        _audit.Record(new AuditEvent(AuditEventNameFor(outcome))
        {
            Outcome = accepted ? AuditOutcome.Succeeded : AuditOutcome.Denied,
            PortalId = portalId,
            ActorUserId = userId,
            SubjectUserId = userId,
            FailureCode = accepted ? null : AuditFailureCodeFor(outcome),
        });
    }

    /// <summary>Determines which of the three legacy approval outcomes the submission produced.</summary>
    /// <param name="portal">The tenant the sign-in addresses.</param>
    /// <param name="verificationCode">The verification code the submission carried, if any.</param>
    /// <returns>
    /// The reason carrying <see cref="VerificationRequiredCode"/>, <see
    /// cref="VerificationCodeInvalidCode"/> or <see cref="AccountNotApprovedCode"/>, preserving the legacy
    /// message keys <c>EnterCode</c>, <c>InvalidCode</c> and <c>UserNotAuthorized</c> respectively.
    /// </returns>
    /// <remarks>
    /// The answer reaches the caller, and it is safe to because of WHERE this method is called from: the
    /// sole call site sits behind an accepted credential, so the reader of these three sentences has
    /// already proved it holds the account's password. Nothing here may be reported from a gate that closes
    /// ahead of that proof, which is why this method takes no part in the uniform denial.
    /// </remarks>
    private static string DetermineApprovalOutcome(Portal portal, string? verificationCode)
    {
        if (portal.UserRegistration != UserRegistrationMode.VerifiedRegistration)
        {
            return AccountNotApprovedCode;
        }

        return string.IsNullOrEmpty(verificationCode)
            ? VerificationRequiredCode
            : VerificationCodeInvalidCode;
    }

    /// <summary>
    /// Composes the message that accompanies the approval outcome <see cref="DetermineApprovalOutcome"/>
    /// selected.
    /// </summary>
    /// <param name="portal">The tenant the sign-in addresses.</param>
    /// <param name="verificationCode">The verification code the submission carried, if any.</param>
    /// <returns>The message for the selected outcome.</returns>
    /// <remarks>
    /// The wording carries the intent of the three legacy resource keys - <c>EnterCode</c>,
    /// <c>InvalidCode</c> and <c>UserNotAuthorized</c>, read from
    /// <c>Website/DesktopModules/AuthenticationServices/DNN/App_LocalResources/Login.ascx.resx</c> - rather
    /// than their exact English text, because the localisation mechanism is not ported and the wording is
    /// re-authored at the presentation layer.
    /// </remarks>
    private static string ApprovalOutcomeDetail(Portal portal, string? verificationCode)
        => DetermineApprovalOutcome(portal, verificationCode) switch
        {
            VerificationRequiredCode =>
                "This account is awaiting verification. Submit the verification code that was sent to it.",
            VerificationCodeInvalidCode =>
                "The verification code submitted for this account is not correct.",
            _ => "This account has not been authorised to sign in to this portal.",
        };

    /// <summary>
    /// Applies the legacy promotion of an already-successful outcome to its weak-credential counterpart.
    /// </summary>
    /// <param name="loginStatus">The outcome the gates produced.</param>
    /// <param name="username">The submitted account name.</param>
    /// <param name="password">The submitted credential.</param>
    /// <returns>The promoted outcome, or the supplied one when no shipped default was presented.</returns>
    /// <remarks>
    /// The account names and credentials are the values the product was distributed with, and the whole
    /// value of the check is that it recognises exactly those. the legacy compared the name with VB's
    /// <c>=</c> operator under the default binary comparison, making it case-SENSITIVE, so an account
    /// signing in as <c>Admin</c> escaped the advisory while being just as exposed.
    /// </remarks>
    private static UserLoginStatus PromoteShippedCredentialOutcome(
        UserLoginStatus loginStatus,
        string username,
        string password)
    {
        // The fingerprint is computed only when the account name already matches, so an ordinary sign-in
        // pays for no hashing at all and the advisory costs one digest of a short string at most.
        if (loginStatus == UserLoginStatus.Success
            && string.Equals(username, ShippedAdministratorAccountName, StringComparison.OrdinalIgnoreCase)
            && IsShippedCredential(password, ShippedAdministratorCredentialFingerprints))
        {
            return UserLoginStatus.InsecureAdminPassword;
        }

        if (loginStatus == UserLoginStatus.SuperUser
            && string.Equals(username, ShippedHostAccountName, StringComparison.OrdinalIgnoreCase)
            && IsShippedCredential(password, ShippedHostCredentialFingerprints))
        {
            return UserLoginStatus.InsecureHostPassword;
        }

        return loginStatus;
    }

    /// <summary>Reports whether the submitted credential is one of the supplied shipped defaults.</summary>
    /// <param name="password">The submitted credential.</param>
    /// <param name="fingerprints">Lower-case hexadecimal SHA-256 digests of the shipped defaults.</param>
    /// <returns><see langword="true"/> when the credential fingerprints to one of them.</returns>
    /// <remarks>
    /// C-04: the comparison the legacy made against two plaintext literals, made against their digests
    /// instead.
    /// </remarks>
    private static bool IsShippedCredential(string password, string[] fingerprints)
    {
        int maximum = Encoding.UTF8.GetMaxByteCount(password.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maximum);

        try
        {
            int written = Encoding.UTF8.GetBytes(password, buffer);
            byte[] digest = SHA256.HashData(buffer.AsSpan(0, written));
            string fingerprint = Convert.ToHexString(digest).ToLowerInvariant();

            return fingerprints.Contains(fingerprint, StringComparer.Ordinal);
        }
        finally
        {
            // Cleared before the buffer returns to the pool, so no later renter can observe the credential.
            // The whole rented length is cleared rather than only the written span, because a pooled buffer
            // can be longer than this value and may still hold a previous renter's bytes.
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Projects the two weak-credential outcomes onto the informational reason a successful result carries.
    /// </summary>
    /// <param name="loginStatus">The outcome the gates produced.</param>
    /// <returns>The advisory reason, or <see langword="null"/> when the outcome carries no caveat.</returns>
    private static ResultReason? ShippedCredentialAdvisory(UserLoginStatus loginStatus) => loginStatus switch
    {
        UserLoginStatus.InsecureAdminPassword => new ResultReason(
            InsecureAdminPasswordCode,
            "The tenant administrator account is still using a credential the product was distributed with, and should change it."),
        UserLoginStatus.InsecureHostPassword => new ResultReason(
            InsecureHostPasswordCode,
            "The host account is still using a credential the product was distributed with, and should change it."),
        _ => null,
    };

    /// <summary>Decides whether the caller of this request may be told that an account is locked.</summary>
    /// <param name="portal">The tenant the sign-in addresses.</param>
    /// <param name="cancellationToken">Cancellation token for the authoritative host-account read.</param>
    /// <returns><see langword="true"/> when the caller already administers the tenant or the installation.</returns>
    /// <remarks>
    /// The distinction is withheld from an anonymous caller because it would reveal that a named account
    /// exists. An already-authenticated administrator of the tenant, or a host account, learns nothing it
    /// could not read from the account list, so it receives the actionable answer instead.
    /// </remarks>
    private async Task<bool> CallerIsEntitledToDetailAsync(
        Portal portal,
        CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is not int actor)
        {
            return false;
        }

        if (portal.AdministratorId == actor)
        {
            return true;
        }

        User? account = await _users
            .GetAsync(portalId: null, actor, cancellationToken)
            .ConfigureAwait(false);

        return account?.IsSuperUser == true;
    }

    /// <summary>
    /// Resolves an account by name within a tenant, admitting a host account that holds no membership.
    /// </summary>
    /// <param name="portalId">The tenant being signed in to.</param>
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
    /// Resolves an account by identifier within a tenant, admitting a host account that holds no
    /// membership.
    /// </summary>
    /// <param name="portalId">The tenant in question.</param>
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

    /// <summary>Issues a token pair for an account whose credential has already been accepted.</summary>
    /// <param name="portal">The tenant signed in to.</param>
    /// <param name="account">The authenticated account.</param>
    /// <param name="remediation">The blocking state already evaluated from authoritative storage.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// A successful outcome carrying the issued pair and a freshly read snapshot of the caller, or the
    /// token service's own failure reason propagated unchanged.
    /// </returns>
    /// <remarks>
    /// This helper returns a typed outcome rather than throwing.
    /// </remarks>
    private async Task<Result<LoginResponse>> IssueAsync(
        Portal portal,
        User account,
        AuthenticationRemediationState remediation,
        CancellationToken cancellationToken)
    {
        Result<LoginResponse> issued = await _tokens.IssueTokensAsync(
            account.UserId,
            portal.PortalId,
            cancellationToken).ConfigureAwait(false);

        if (issued.IsFailure)
        {
            return Result<LoginResponse>.Failure(issued.Reason!);
        }

        LoginResponse response = issued.Value;
        response.MustChangePassword = remediation.MustChangePassword;
        response.MustUpdateProfile = remediation.MustUpdateProfile;
        response.User = BuildIdentitySnapshot(portal, account);

        return Result<LoginResponse>.Success(response);
    }

    /// <summary>Builds the authority-minimised identity carried on login and refresh responses.</summary>
    /// <param name="portal">The tenant the session addresses.</param>
    /// <param name="account">The authenticated account.</param>
    /// <returns>Identity and display fields with empty role and permission collections.</returns>
    private static CurrentUserDto BuildIdentitySnapshot(Portal portal, User account) => new()
    {
        UserId = account.UserId,
        PortalId = portal.PortalId,
        PortalName = portal.PortalName,
        Username = account.Username,
        DisplayName = account.DisplayName,
        Email = account.Email ?? string.Empty,
        IsSuperUser = account.IsSuperUser,
        Roles = [],
        Permissions = [],
    };

    /// <summary>Builds the expanded caller snapshot returned only by the current-user read.</summary>
    /// <param name="portal">The tenant the caller is signed in to.</param>
    /// <param name="account">The account.</param>
    /// <param name="asOfUtc">The instant role validity windows are evaluated against.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The snapshot.</returns>
    /// <remarks>
    /// Roles are resolved as of the supplied instant, so an assignment whose validity window has not opened
    /// or has already closed does not appear in the snapshot.
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

        if (permissions.IsFailure)
        {
            _diagnostics.Record(
                SecurityDiagnosticEvent.EffectivePermissionResolutionFailed,
                portal.PortalId,
                account.UserId,
                permissions.Reason?.Code);
        }

        bool administersPortal = await IsPortalAdministratorAsync(portal, account, asOfUtc, cancellationToken)
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
            IsPortalAdministrator = administersPortal,
            Roles = roles,
            Permissions = permissions.IsSuccess ? permissions.Value : [],
        };
    }

    /// <summary>Whether the account administers the tenant it is signed in to.</summary>
    /// <param name="portal">The tenant the caller is signed in to, already loaded.</param>
    /// <param name="account">The account.</param>
    /// <param name="asOfUtc">The instant assignment validity windows are evaluated against.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns><see langword="true"/> when the caller administers that tenant.</returns>
    /// <remarks>
    /// WHY THIS IS NOT A SECOND IMPLEMENTATION OF A SECURITY RULE. The enforcing evaluator in the API layer
    /// answers a materially different question: does the caller administer the portal THE REQUEST ACTS ON,
    /// which it resolves from the route and then reconciles against the tenant the token was minted for.
    /// </remarks>
    private async Task<bool> IsPortalAdministratorAsync(
        Portal portal,
        User account,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        if (account.IsSuperUser)
        {
            // A host account administers every tenant, which is the same answer the enforcing policy gives.
            return true;
        }

        if (portal.AdministratorRoleId is not { } administratorRoleId)
        {
            // The portal designates no administrator role. A configuration gap must not grant, and it is
            // never widened to any other role - not even to one that happens to be named for the purpose.
            return false;
        }

        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesAsync(portal.PortalId, account.UserId, cancellationToken)
            .ConfigureAwait(false);

        // RoleId is compared as an integer and never coerced: role zero is a REAL role, because
        // Roles.RoleID is IDENTITY (0, 1) (01.00.00.SqlDataProvider L114), so no presence test may read it
        // as absent.
        return assignments.Any(assignment =>
            assignment.RoleId == administratorRoleId
            && assignment.GetStatus(asOfUtc) == RoleStatus.Active);
    }

    /// <summary>Evaluates the post-credential advisories a successful sign-in carries.</summary>
    /// <param name="account">The authenticated account.</param>
    /// <param name="asOfUtc">The instant the expiry calendar comparison is judged against.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// Whether the caller must change its credential before continuing, and whether that credential is
    /// approaching expiry.
    /// </returns>
    /// <remarks>
    /// The reminder suppression argument is gone. The legacy overload took an <c>ignoreExpiring</c> flag,
    /// and the sign-in path this service replaces passed <see langword="false"/> for it - measured at
    /// <c>Login.ascx.vb</c> L893, the arm that handled the event the DotNetNuke sign-in control raised - so
    /// the reminder IS reported here.
    /// </remarks>
    private async Task<(bool MustChangePassword, bool PasswordExpiring)> EvaluateCredentialAdvisoriesAsync(
        User account,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        // The administrator-forced update, read from the account row's own flag - the legacy read the
        // membership property of the same intent and gave it the highest precedence.
        if (account.UpdatePassword)
        {
            return (true, false);
        }

        if (account.IsSuperUser)
        {
            return (false, false);
        }

        int expiryDays = await ReadHostSettingIntegerAsync(PasswordExpiryHostSettingName, 0, cancellationToken)
            .ConfigureAwait(false);

        if (expiryDays <= 0)
        {
            return (false, false);
        }

        // MIGRATION: the legacy read a non-nullable date that the null-sentinel helper had already
        // collapsed to Date.MinValue when the column held no value, so an account with no recorded change
        // date computed MinValue plus the window and was reported EXPIRED. The target models the column as
        // nullable and treats an absent date as "no expiry can be computed" instead.
        if (account.LastPasswordChangeDate is not DateTime lastChangedUtc)
        {
            return (false, false);
        }

        DateTime expiresOn = lastChangedUtc.AddDays(expiryDays);
        DateTime today = asOfUtc.Date;

        if (expiresOn < today)
        {
            return (true, false);
        }

        int reminderDays = await ReadHostSettingIntegerAsync(
            PasswordExpiryReminderHostSettingName,
            DefaultPasswordExpiryReminderDays,
            cancellationToken).ConfigureAwait(false);

        return (false, expiresOn < today.AddDays(reminderDays));
    }

    /// <summary>
    /// Decides whether the admitted account must complete or correct its profile before continuing.
    /// </summary>
    /// <param name="portalId">The tenant the account was admitted to.</param>
    /// <param name="account">The admitted account.</param>
    /// <param name="cancellationToken">Token observed while the question is asked.</param>
    /// <returns>
    /// A successful outcome carrying <see langword="true"/> when the tenant requires a valid profile and
    /// the account leaves a required property empty; otherwise a failed outcome when the required state
    /// could not be evaluated.
    /// </returns>
    /// <remarks>
    /// The legacy status enumeration was SINGLE-VALUED and tested the profile arm last, only while no
    /// credential advisory had been raised, so it could never report both at once. This contract can, and
    /// does: the flag is set from its own condition regardless of the credential advisories.
    /// </remarks>
    private async Task<Result<bool>> EvaluateProfileRemediationAsync(
        int portalId,
        User account,
        CancellationToken cancellationToken)
    {
        if (account.IsSuperUser)
        {
            return Result<bool>.Success(false);
        }

        Result<bool> outcome = await _accounts
            .RequiresProfileCompletionAsync(portalId, account.UserId, cancellationToken)
            .ConfigureAwait(false);

        return outcome.IsSuccess
            ? Result<bool>.Success(outcome.Value)
            : Result<bool>.Failure(
                RemediationStoreUnavailableCode,
                "The account's required remediation state could not be evaluated.");
    }

    /// <summary>Reads an installation-wide setting expressed as a whole number.</summary>
    /// <param name="settingName">The setting name, spelled exactly as the legacy wrote it.</param>
    /// <param name="fallback">The value to apply when the setting is absent or unusable.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The configured value, or <paramref name="fallback"/>.</returns>
    private async Task<int> ReadHostSettingIntegerAsync(
        string settingName,
        int fallback,
        CancellationToken cancellationToken)
    {
        string? configured = await _hostSettings
            .GetSettingAsync(settingName, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(configured)
            || !int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days)
                ? fallback
                : days;
    }

    /// <summary>Clears a lock that has aged past the installation's automatic-unlock window.</summary>
    /// <param name="account">The locked account, carrying the instant it was locked.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// <see langword="true"/> when the lock was cleared and the sign-in may continue; <see
    /// langword="false"/> when the lock stands.
    /// </returns>
    /// <remarks>
    /// A negative configured duration is treated as the disabling zero rather than as an instantly elapsed
    /// window.
    /// </remarks>
    private async Task<bool> TryAutomaticUnlockAsync(User account, CancellationToken cancellationToken)
    {
        int windowMinutes = await ReadHostSettingIntegerAsync(
            AutoAccountUnlockDurationHostSettingName,
            DefaultAutoAccountUnlockDurationMinutes,
            cancellationToken).ConfigureAwait(false);

        if (windowMinutes <= 0)
        {
            return false;
        }

        if (account.LastLockoutDate is not DateTime lockedAtUtc)
        {
            return false;
        }

        if (lockedAtUtc >= _clock.UtcNow.AddMinutes(-windowMinutes))
        {
            return false;
        }

        bool unlocked = await _users.UnlockAsync(account.UserId, cancellationToken).ConfigureAwait(false);

        if (unlocked)
        {
            // Kept in step with the store so that nothing downstream in this request - the caller snapshot
            // in particular - describes the account as locked after it has been cleared.
            account.IsLockedOut = false;
        }

        return unlocked;
    }

    /// <summary>
    /// Replaces an accepted stored credential when it is legacy or uses a superseded current work factor.
    /// </summary>
    /// <param name="userId">The account whose stored representation is replaced.</param>
    /// <param name="password">The credential just accepted, which the replacement is computed from.</param>
    /// <param name="storedCredential">The stored representation accepted by one of the two verifiers.</param>
    /// <param name="verifiedByLegacy">Whether the bounded legacy verifier accepted the credential.</param>
    /// <param name="asOfUtc">The instant the replacement is stamped with.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// A value identifying whether no replacement was due, which replacement succeeded, or which one
    /// failed.
    /// </returns>
    /// <remarks>
    /// The frozen cut-over contract requires a bounded legacy verification path. When that isolated
    /// verifier accepts a clear, legacy hash or format-2 encrypted representation, this method immediately
    /// replaces it with BCrypt and rewrites the membership format/salt through the repository.
    /// </remarks>
    private async Task<CredentialReplacement> TryReplaceCredentialAsync(
        int userId,
        string password,
        string storedCredential,
        bool verifiedByLegacy,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        if (!verifiedByLegacy && !_passwordHasher.NeedsRehash(storedCredential))
        {
            return new CredentialReplacement(CredentialReplacementOutcome.NotRequired, null, null);
        }

        CredentialReplacementOutcome succeeded = verifiedByLegacy
            ? CredentialReplacementOutcome.LegacyCredentialMigrated
            : CredentialReplacementOutcome.WorkFactorUpgraded;
        CredentialReplacementOutcome failed = verifiedByLegacy
            ? CredentialReplacementOutcome.LegacyMigrationFailed
            : CredentialReplacementOutcome.WorkFactorUpgradeFailed;

        try
        {
            // The store's own answer is part of the outcome.
            string replacementHash = _passwordHasher.Hash(password);

            CredentialWriteOutcome stored = await _users
                .SetPasswordHashAsync(userId, replacementHash, storedCredential, asOfUtc, cancellationToken)
                .ConfigureAwait(false);

            return stored switch
            {
                CredentialWriteOutcome.Replaced => new CredentialReplacement(succeeded, null, replacementHash),
                CredentialWriteOutcome.Superseded => new CredentialReplacement(
                    CredentialReplacementOutcome.CredentialSuperseded,
                    null,
                    null),
                _ => new CredentialReplacement(failed, null, null),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _audit.Record(new AuditEvent(AuditEventNames.PasswordRehashFailure)
            {
                Outcome = AuditOutcome.Failed,
                SubjectUserId = userId,
                ResourceType = CredentialResourceType,
                ResourceId = userId.ToString(CultureInfo.InvariantCulture),
                FailureCode = CredentialReplacementStoreFailureCode,
                Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ReplacementKind"] = verifiedByLegacy ? "LegacyMigration" : "WorkFactorUpgrade",
                },
            });

            // The exception TYPE is returned rather than recorded here, so that it reaches the security
            // diagnostics channel from the caller - the only place that also holds the tenant, and the
            // place that already records this condition.
            return new CredentialReplacement(failed, exception.GetType().Name, null);
        }
    }

    /// <summary>
    /// Escalates a membership-store outage and records a missing credential record, so that neither the
    /// failed-attempt counter nor its reset can stop working unnoticed.
    /// </summary>
    /// <param name="outcome">What the bookkeeping write reported.</param>
    /// <param name="userId">The account the write addressed, for the diagnostic.</param>
    /// <exception cref="InvalidOperationException">The membership store could not be reached.</exception>
    /// <remarks>
    /// AN UNREACHABLE STORE IS A SERVER FAULT, NOT AN AUTHENTICATION OUTCOME, and is raised rather than
    /// returned.
    /// </remarks>
    private void EnsureMembershipBookkeepingRan(MembershipWriteOutcome outcome, int userId)
    {
        switch (outcome)
        {
            case MembershipWriteOutcome.StoreUnavailable:
                throw new InvalidOperationException(
                    "The membership store is unavailable, so the credential attempt bookkeeping that produces "
                    + "an account lock-out could not be written.");

            case MembershipWriteOutcome.NoRecord:
                _diagnostics.Record(
                    SecurityDiagnosticEvent.MembershipRecordMissingDuringSignIn,
                    portalId: null,
                    userId);
                break;

            case MembershipWriteOutcome.Recorded:
            case MembershipWriteOutcome.RecordedAndLocked:
            default:
                break;
        }
    }

    /// <summary>
    /// Reports whether a credential read taken later in a sign-in still names the credential that sign-in
    /// is entitled to act on.
    /// </summary>
    /// <param name="present">Whether the later read found a credential record at all.</param>
    /// <param name="current">The representation the later read returned.</param>
    /// <param name="verified">The representation this sign-in compared the submitted credential against.</param>
    /// <param name="written">The representation this sign-in itself stored, when it replaced one.</param>
    /// <returns><see langword="true"/> when the credential is unchanged for this sign-in's purposes.</returns>
    /// <remarks>
    /// TWO VALUES ARE LEGITIMATE, WHICH IS THE WHOLE SUBTLETY. Ordinarily the credential must still be the
    /// representation this sign-in verified. Where this sign-in itself replaced it - a work-factor upgrade,
    /// or a legacy migration - the credential must be the value THIS request wrote, which is why the
    /// replacement reports what it stored.
    /// </remarks>
    private static bool CredentialIsStill(
        bool present,
        string? current,
        string verified,
        string? written)
        => present
            && current is not null
            && (string.Equals(current, verified, StringComparison.Ordinal)
                || (written is not null && string.Equals(current, written, StringComparison.Ordinal)));

    /// <summary>Refuses a rotation and ends the whole refresh family behind it.</summary>
    /// <param name="userId">The account the presented token belonged to.</param>
    /// <param name="portalId">The tenant the presented token named.</param>
    /// <param name="failureCode">Which eligibility question closed, recorded but never disclosed.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The single uniform refusal this contract reports for every rejected token.</returns>
    /// <remarks>
    /// REVOKING THE FAMILY IS THE OPERATIVE HALF, not the refusal. Rotation has already happened by the
    /// time eligibility can be re-read - the store maps an opaque token to its subject and nothing above it
    /// can, so the exchange must complete before there is an account to ask about - and that exchange mints
    /// a successor.
    /// </remarks>

    private async Task<Result<LoginResponse>> RefuseRotationAsync(
        int userId,
        int portalId,
        string failureCode,
        CancellationToken cancellationToken)
    {
        Result revoked = await _tokens
            .RevokeAllRefreshTokensAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        // A revocation that could not be persisted is escalated rather than absorbed: reporting a store
        // outage as a rejected token would leave the family exchangeable while telling the caller its token
        // was the problem.
        if (revoked.IsFailure && IsTokenStoreFailure(revoked.Reason))
        {
            return Result<LoginResponse>.Failure(revoked.Reason!);
        }

        _audit.Record(new AuditEvent(AuditEventNames.SessionRefused)
        {
            Outcome = AuditOutcome.Denied,
            PortalId = portalId,
            ActorUserId = userId,
            SubjectUserId = userId,
            FailureCode = failureCode,
        });

        return Result<LoginResponse>.Failure(InvalidRefreshTokenCode, "The refresh token is not valid.");
    }

    /// <summary>
    /// Records a sign-in outcome under the legacy event name that the status member already carries.
    /// </summary>
    /// <param name="loginStatus">The outcome the gates settled on.</param>
    /// <param name="portalId">The tenant the credential was presented to.</param>
    /// <param name="account">The account the name resolved to.</param>
    /// <param name="outcome">Whether the sign-in was accepted or refused.</param>
    /// <param name="advisoryCode">A weak-credential advisory carried on an accepted sign-in, if any.</param>
    /// <param name="mustChangePassword">
    /// Whether the accepted caller must replace its credential, or <see langword="null"/> on a refusal,
    /// where the question does not arise.
    /// </param>
    /// <param name="mustUpdateProfile">
    /// Whether the accepted caller must complete its profile, or <see langword="null"/> on a refusal.
    /// </param>
    private void RecordLoginOutcome(
        UserLoginStatus loginStatus,
        int portalId,
        User account,
        AuditOutcome outcome,
        string? advisoryCode = null,
        bool? mustChangePassword = null,
        bool? mustUpdateProfile = null)
    {
        Dictionary<string, string?> properties = new(StringComparer.Ordinal);

        if (mustChangePassword is bool changeRequired)
        {
            properties["MustChangePassword"] = changeRequired ? "true" : "false";
        }

        if (mustUpdateProfile is bool profileRequired)
        {
            properties["MustUpdateProfile"] = profileRequired ? "true" : "false";
        }

        if (advisoryCode is not null)
        {
            properties["Advisory"] = advisoryCode;
        }

        _audit.Record(new AuditEvent(AuditEventNameFor(loginStatus))
        {
            Outcome = outcome,
            PortalId = portalId,
            ActorUserId = account.UserId,
            SubjectUserId = account.UserId,
            FailureCode = outcome == AuditOutcome.Succeeded ? null : AuditFailureCodeFor(loginStatus),
            Properties = properties,
        });
    }

    /// <summary>Reports whether an outcome ADMITTED the caller.</summary>
    /// <param name="loginStatus">The outcome.</param>
    /// <returns><see langword="true"/> when the caller was signed in.</returns>
    private static bool Admitted(UserLoginStatus loginStatus) => loginStatus
        is UserLoginStatus.Success
        or UserLoginStatus.SuperUser
        or UserLoginStatus.InsecureAdminPassword
        or UserLoginStatus.InsecureHostPassword;

    /// <summary>Maps a sign-in outcome onto the stable legacy event name.</summary>
    /// <param name="loginStatus">The outcome.</param>
    /// <returns>The event name.</returns>
    private static string AuditEventNameFor(UserLoginStatus loginStatus) => loginStatus switch
    {
        UserLoginStatus.SuperUser or UserLoginStatus.InsecureHostPassword => AuditEventNames.LoginSuperUser,
        UserLoginStatus.Success or UserLoginStatus.InsecureAdminPassword => AuditEventNames.LoginSuccess,
        UserLoginStatus.UserLockedOut => AuditEventNames.LoginUserLockedOut,
        UserLoginStatus.UserNotApproved => AuditEventNames.LoginUserNotApproved,
        _ => AuditEventNames.LoginFailure,
    };

    /// <summary>Names which eligibility question closed, for the trail only.</summary>
    /// <param name="loginStatus">The outcome.</param>
    /// <returns>A stable code, never disclosed to a caller.</returns>
    private static string AuditFailureCodeFor(UserLoginStatus loginStatus) => loginStatus switch
    {
        UserLoginStatus.UserLockedOut => LockedOutCode,
        UserLoginStatus.UserNotApproved => NotApprovedFailure,
        _ => CredentialRejectedFailure,
    };

    /// <summary>
    /// Reports whether a token-service failure is the token-store outage this contract propagates
    /// unchanged.
    /// </summary>
    /// <param name="reason">The reason the token service reported.</param>
    /// <returns>
    /// <see langword="true"/> when the reason is the documented store outage; otherwise <see
    /// langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Every other reason the token service reports describes the presented token and is collapsed into
    /// this service's single token failure. An unavailable store describes neither the caller nor the
    /// token, so reporting it as a denial would tell a caller its token was invalid when it was not, and
    /// would tell a caller whose credential had just been accepted that the credential was wrong.
    /// </remarks>
    private static bool IsTokenStoreFailure(ResultReason? reason)
        => reason is ResultReason failure
            && string.Equals(failure.Code, TokenStoreUnavailableCode, StringComparison.Ordinal);
}
