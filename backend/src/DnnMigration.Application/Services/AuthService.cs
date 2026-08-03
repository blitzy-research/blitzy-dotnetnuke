// ---------------------------------------------------------------------------------------------------
// Rule T5 register for the sign-in vertical. Every divergence from the legacy behaviour is named here
// with the file and line it was measured from, annotated again at the point of divergence below, and
// recorded in MIGRATION_NOTES.md. None is absorbed silently.
// ---------------------------------------------------------------------------------------------------
//
// MIGRATION: this service implements the sign-in vertical that
// Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L196 reached through
// UserController.ValidateUser (UserController.vb:L1132) and, beneath it, the membership provider's
// UserLogin (AspNetMembershipProvider.vb:L1429). The gate order below is taken verbatim from that
// provider: resolve the account within the tenant, test the lock, test approval - admitting a
// verification code in its place - and only then compare the credential
// (AspNetMembershipProvider.vb:L1445-L1508). The order is load-bearing rather than incidental:
// comparing the credential before the lock lets an attacker confirm a guess against an already locked
// account, and comparing it before the approval gate turns an unapproved registration into a
// credential oracle for anyone able to register.
//
// MIGRATION: the by-reference status argument is eliminated, and that elimination is the reason this
// file exists. Login.ascx.vb:L163 declared `Dim loginStatus As UserLoginStatus =
// UserLoginStatus.LOGIN_FAILURE` and L164 passed it into
// `UserController.ValidateUser(..., ByRef loginStatus)`, which reported its verdict by mutating that
// variable while separately returning an object it set to Nothing on refusal. A caller that ignored
// the argument therefore mistook a locked account for a missing one. The local status below occupies
// exactly the same place in the flow, is computed through exactly the same gates, and is then mapped
// once onto the returned Result - so the outcome travels as a value that cannot be ignored. No member
// of this file takes an `out` or a `ref` parameter (AAP 0.7.4).
//
// MIGRATION: the fixed authentication-mechanism argument is dropped. Login.ascx.vb passed the literal
// "DNN" twice - as the fourth argument at L164 and again at L191 when constructing the arguments of
// the event it raised - because the legacy platform multiplexed several pluggable sign-in mechanisms
// behind one screen, and the provider branched on it (AspNetMembershipProvider.vb:L1438-L1442,
// L1483-L1502). The target has exactly one path, a JSON Web Token, so such a discriminator could hold
// only a single value. No `authType`, `provider` or `scheme` parameter appears anywhere on this
// service, and no external-provider member exists.
//
// MIGRATION: the CAPTCHA gate is dropped, WITH A NAMED COMPENSATING CONTROL. `UseCaptcha` was read at
// Login.ascx.vb:L59-L61 from the per-tenant authentication configuration and gated the whole handler
// at L162 - `If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha) Then` - with a second
// identical gate on the recovery screen at SendPassword.ascx.vb:L176. The control that implemented it
// lives under Library/Controls/Captcha and is inside the 102-file Library/Controls exclusion, so the
// argument is dropped entirely: there is no `captcha`, `verificationImage` or equivalent parameter on
// this service. Dropping a brute-force defence without a replacement would be a security regression,
// so the replacement is named explicitly: request rate limiting on the credential endpoints,
// configured in Api/Extensions/RateLimitingExtensions.cs and applied to every action of
// Api/Controllers/AuthController.cs through its `EnableRateLimiting` attribute. That control is
// stronger than the one it replaces in one respect and weaker in another, and both are recorded in
// MIGRATION_NOTES.md: it throttles automated guessing without a human challenge, but it cannot
// distinguish a human from a machine within its allowance.
//
// MIGRATION: the verification-code ladder of Login.ascx.vb:L168-L185 is preserved in full as a
// determination, and its three legacy message keys survive as the three stable codes declared below.
// Two mechanism changes are involved and neither is silent. First, the legacy distinguished the "first
// pass" through the ladder by inspecting `rowVerification1.Visible` at L171 - a ViewState-backed user
// interface flag, which a stateless request does not have and must not pretend to have; the
// distinction is therefore derived from whether the request supplied a verification code at all,
// which is the same question the flag was standing in for. Second, the legacy read the tenant's
// registration mode ambiently at L170 through `PortalSettings.UserRegistration`
// (PortalSettings.vb:L174); that property is one of the members deliberately excluded from
// IPortalContext, so it is read from the Portal aggregate loaded through IPortalRepository instead.
// PortalSettings and PortalRegistrationType appear nowhere on any surface here; the Domain enumeration
// UserRegistrationMode is used.
//
// MIGRATION: the authenticated-event model is not reproduced. Login.ascx.vb:L190-L194 constructed a
// UserAuthenticatedEventArgs and called OnUserAuthenticated, one of the seven user-lifecycle events
// declared at UserUserControlBase.vb:L59-L65. No server-side publisher is introduced - no event, no
// delegate, no observable - because the caller now observes its own sign-in directly in the returned
// value, and inventing a bus here would be scope creep dressed as fidelity (AAP 0.7.5.3).
//
// MIGRATION: the audit record cannot be emitted from this layer, and the gap is reported rather than
// quietly dropped. UserController.vb:L66-L82 wrote an audit entry whose log type key was
// `loginStatus.ToString` (L80), carrying the properties IP, tenant identifier, tenant name, account
// name and account identifier; it was raised only for the locked-out and failure outcomes
// (UserController.vb:L1138-L1141) and was always passed Null.NullInteger, minus one, as the account
// identifier - so the legacy trail never recorded WHICH account failed. MIGRATION_NOTES.md assigns the
// mapping of these outcomes onto stable event names to the application layer, and that mapping is
// discharged here: the local status below is computed for every outcome and its member name is that
// stable name. Emission is a different matter and is IMPOSSIBLE in this project. ILogger<T> is not
// resolvable here: DnnMigration.Application declares FluentValidation and nothing else, per AAP 0.6.1,
// and Microsoft.Extensions.Logging is absent from the reference pack a class library targets. That was
// proven by compiling a probe rather than assumed - the attempt fails with CS0234 on the namespace and
// CS0246 on the generic - and this project's own manifest records an identical earlier attempt, made
// for IOptions<T>, that was reverted with the ruling that the consumer changes rather than the
// manifest. GAP REPORTED: the compensating control is the Api layer, which does have a logger:
// Api/Middleware/RequestLoggingMiddleware.cs records every request with its method, path, status code
// and elapsed time, raising a refused sign-in to warning level, and Api/Middleware/
// CorrelationIdMiddleware.cs binds the correlation identifier those events are read against.
//
// MIGRATION: PortalSecurity.InputFilter is not reproduced. The legacy audit passed the submitted
// account name through it at UserController.vb:L77 with NoScripting, NoAngleBrackets and NoMarkup set,
// because the name was concatenated into a log record that was later rendered. PortalSecurity is
// excluded, and the concern it addressed - log forging by way of a hostile value - is answered
// structurally instead: the Api layer emits structured properties rather than interpolated strings, so
// a value is a property of an event and can never become part of its message template.
//
// MIGRATION: the caller's network address is absent from this layer altogether. It was the seventh
// argument at Login.ascx.vb:L164 and the legacy service did nothing with it but record it. LoginRequest
// carries no address property, adding one to a contract outside this file's scope is not permitted, and
// reaching for HttpContext is forbidden everywhere but the tenant-resolution middleware (AAP 0.7.5.1).
// The address is recorded by the Api layer's request log instead. GAP REPORTED: absence is a stronger
// guarantee than acceptance here, because an address that could influence the outcome would be an
// input the caller controls.
//
// MIGRATION: the reversible credential store is replaced by a one-way hash, and credential retrieval
// is not carried forward in any form. The original schema held the value in clear text - Users.Password
// is declared nvarchar(20) NOT NULL at 01.00.00.SqlDataProvider:L97-L110 - and the membership provider
// was later registered with a reversible storage format and retrieval enabled at
// Website/release.config:L236-L246, decryptable with symmetric material committed to source control at
// L89-L93. That file is evidence and is left byte-identical; none of its key material is reproduced
// here, not even in a comment. The recovery screen's retrieval branch at
// SendPassword.ascx.vb:L198-L200 called UserController.GetPassword and therefore has no target
// equivalent, and the reminder message it sent at L211 used a mail subsystem that is out of scope. No
// recovery member is declared on this service, because IAuthService declares none: a member that could
// neither disclose whether an account exists, nor transmit a credential, nor send a notification would
// report success while doing nothing, which reads as a working feature. Administrative reset through
// IUserService is the supported path, and this service owns no part of it.
//
// MIGRATION: the account approval question-and-answer pair is omitted. SendPassword.ascx.vb:L167
// guarded on it, but the shipped policy set requiresQuestionAndAnswer="false"
// (Website/release.config:L241) so the path was already dormant, and its only real use was authorising
// the retrieval that is no longer offered. No member here accepts an answer.
//
// MIGRATION: the sign-in credential POLICY is preserved verbatim and is enforced elsewhere. The
// measured values are requiresQuestionAndAnswer="false", minRequiredPasswordLength="7",
// minRequiredNonalphanumericCharacters="0" and requiresUniqueEmail="false"
// (Website/release.config:L239-L245). Enforcement belongs to Validation/LoginRequestValidator and the
// account validators; this service neither duplicates nor tightens it, because tightening a policy
// during a migration locks existing account holders out of a system they could previously use.
//
// MIGRATION: both primary sources for this file are web code-behinds - Login.ascx.vb and
// SendPassword.ascx.vb - and Website/release.config:L125 compiles that tree with
// `<compilation debug="false" strict="false">`, that is with Option Strict OFF, whereas the class
// library sets <OptionStrict>On</OptionStrict> at Library/DotNetNuke.Library.vbproj:L24. CORRECTION TO
// THE FOLDER REQUIREMENTS: they cite L23 for that element; L23 is <OptionExplicit>On</OptionExplicit>
// and OptionStrict is on the following line. The consequence is that the measured sources may contain
// implicit narrowing and late binding that C# rejects, so every conversion below is explicit and every
// coercion whose result could differ is annotated where it occurs. No Microsoft.VisualBasic helper
// survives: none of IIf, DateAdd, InStr or Mid appears here, and the two date arithmetic sites use
// DateTime.AddDays.
using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>
/// Authenticates a caller against one tenant and manages the lifetime of the tokens issued to it.
/// </summary>
/// <remarks>
/// <para>
/// This service owns the sign-in decision: credential verification, the account gates, the failure
/// bookkeeping, the credential and profile advisories a successful sign-in may carry, and the
/// resolution of the roles and permission keys that become claims. Minting, rotating and revoking the
/// tokens themselves belongs to <see cref="ITokenService"/>, reached through an abstraction so that no
/// signing library is resolvable from this project.
/// </para>
/// <para>
/// Ported from the credential-validation sequence of
/// <c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb</c> (lines 160-196) over the
/// membership provider beneath it, together with the post-credential validation of
/// <c>Library/Components/Users/UserController.vb</c> (lines 1171-1197) and the recovery flow of
/// <c>Website/admin/Security/SendPassword.ascx.vb</c>. Credential retrieval is deliberately not
/// carried forward, the CAPTCHA gate is replaced by rate limiting at the Api layer, and a stored
/// credential produced at a superseded cost is replaced on the first successful sign-in that presents
/// it.
/// </para>
/// <para>
/// Every rejected credential receives one answer. An unknown account name, a wrong credential, an
/// account belonging to another tenant, an unapproved registration and a tenant that does not exist
/// are reported with the same code and the same wording, because any difference between them turns
/// this service into an oracle for account names or for the installation's tenants. The single
/// exception is the lock, which is reported explicitly to a caller already entitled to know it.
/// </para>
/// <para>
/// No member returns, echoes or records a credential, a stored hash, a verification code or a token
/// value, and credentials are compared only through <see cref="IPasswordHasher"/> rather than by
/// string equality. The one comparison of a literal credential is the shipped-default advisory, whose
/// entire purpose is to recognise two specific values the product was distributed with, and which
/// never reports the value it matched.
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

    /// <summary>
    /// The single uniform denial. Every closed gate and every rejected credential reports this.
    /// </summary>
    private const string InvalidCredentialsCode = "auth.invalid_credentials";

    /// <summary>
    /// Reported in place of the uniform denial only for a caller entitled to the distinction.
    /// </summary>
    private const string LockedOutCode = "auth.locked_out";

    /// <summary>
    /// Reported when a presented refresh token is unknown, expired, already redeemed or revoked. The
    /// four are deliberately indistinguishable.
    /// </summary>
    private const string InvalidRefreshTokenCode = "auth.invalid_refresh_token";

    /// <summary>
    /// Reported when an authenticated caller's own account no longer exists.
    /// </summary>
    private const string UserNotFoundCode = "auth.user_not_found";

    /// <summary>
    /// The approval outcome for an unapproved registration on a verified-registration tenant that
    /// supplied no verification code. Preserves the legacy message key <c>EnterCode</c>
    /// (<c>Login.ascx.vb</c> L175 and L180).
    /// </summary>
    /// <remarks>
    /// Declared, determined and recorded, but never returned to the caller. See
    /// <see cref="DetermineApprovalOutcome"/> for why.
    /// </remarks>
    private const string VerificationRequiredCode = "auth.verification_required";

    /// <summary>
    /// The approval outcome for an unapproved registration that supplied a verification code which was
    /// rejected. Preserves the legacy message key <c>InvalidCode</c> (<c>Login.ascx.vb</c> L178).
    /// </summary>
    private const string VerificationCodeInvalidCode = "auth.verification_code_invalid";

    /// <summary>
    /// The approval outcome for an unapproved registration on any tenant whose registration mode is not
    /// verified registration. Preserves the legacy message key <c>UserNotAuthorized</c>
    /// (<c>Login.ascx.vb</c> L184).
    /// </summary>
    private const string AccountNotApprovedCode = "auth.account_not_approved";

    /// <summary>
    /// Advisory carried on a successful sign-in when the shipped administrator account is still using a
    /// credential the product was distributed with. Carries forward the legacy
    /// <c>LOGIN_INSECUREADMINPASSWORD</c> outcome.
    /// </summary>
    private const string InsecureAdminPasswordCode = "auth.insecure_admin_password";

    /// <summary>
    /// Advisory carried on a successful sign-in when the shipped host account is still using a
    /// credential the product was distributed with. Carries forward the legacy
    /// <c>LOGIN_INSECUREHOSTPASSWORD</c> outcome.
    /// </summary>
    private const string InsecureHostPasswordCode = "auth.insecure_host_password";

    /// <summary>
    /// The reason code <see cref="ITokenService"/> reports when a token record could not be persisted.
    /// It describes neither the caller nor the presented token, so it is never folded into an
    /// authentication denial.
    /// </summary>
    private const string TokenStoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";

    /// <summary>
    /// Account name of the shipped tenant administrator, matched by the shipped-default advisory
    /// (<c>UserController.vb</c> L1145).
    /// </summary>
    private const string ShippedAdministratorAccountName = "admin";

    /// <summary>
    /// Account name of the shipped host account, matched by the shipped-default advisory
    /// (<c>UserController.vb</c> L1150).
    /// </summary>
    private const string ShippedHostAccountName = "host";

    /// <summary>
    /// Installation-wide setting naming the number of days after which a credential expires. Zero, and
    /// an absent setting, both disable the check (<c>PasswordConfig.vb</c> L52-L64).
    /// </summary>
    private const string PasswordExpiryHostSettingName = "PasswordExpiry";

    /// <summary>
    /// Installation-wide setting naming how many days before expiry the holder is reminded.
    /// </summary>
    /// <remarks>
    /// The trailing space is deliberate and is not a transcription error. <c>PasswordConfig.vb</c>
    /// reads this setting at L81-L82 and writes it at L88 under the name <c>"PasswordExpiryReminder "</c>
    /// - with the space - so the row the legacy application created carries the space in its key. The
    /// legacy getter and setter agree with each other, which is why the defect was never visible. This
    /// migration reads what the legacy wrote rather than what it meant to write; trimming the name here
    /// would silently stop honouring a configured reminder window on every existing installation. The
    /// quirk is recorded in MIGRATION_NOTES.md rather than repaired, per AAP 0.9.1.
    /// </remarks>
    private const string PasswordExpiryReminderHostSettingName = "PasswordExpiryReminder ";

    /// <summary>
    /// The reminder window applied when the installation configures none, measured from the legacy
    /// property's own initialiser (<c>PasswordConfig.vb</c> L80).
    /// </summary>
    private const int DefaultPasswordExpiryReminderDays = 7;

    /// <summary>
    /// The credentials the shipped administrator account was distributed with
    /// (<c>UserController.vb</c> L1145).
    /// </summary>
    private static readonly string[] ShippedAdministratorCredentials = ["admin", "dnnadmin"];

    /// <summary>
    /// The credentials the shipped host account was distributed with (<c>UserController.vb</c> L1150).
    /// </summary>
    private static readonly string[] ShippedHostCredentials = ["host", "dnnhost"];

    private readonly IUserRepository _users;
    private readonly IPortalRepository _portals;
    private readonly IPermissionService _permissions;
    private readonly ITokenService _tokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;
    private readonly IHostSettingsService _hostSettings;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly PasswordPolicyOptions _passwordPolicy;

    /// <summary>
    /// Initialises the service with the collaborators it verifies credentials and issues tokens through.
    /// </summary>
    /// <param name="users">Account resolution, credential state and the sign-in bookkeeping.</param>
    /// <param name="portals">
    /// Tenant resolution. It supplies the tenant name carried on the caller snapshot, the administrator
    /// identifier the lock disclosure is judged against, and the registration mode the approval ladder
    /// reads - the last of which the legacy took ambiently and this service takes from the aggregate.
    /// </param>
    /// <param name="permissions">Tenant-scope permission-key resolution for the claims.</param>
    /// <param name="tokens">Token minting, rotation and revocation.</param>
    /// <param name="passwordHasher">One-way credential verification and replacement detection.</param>
    /// <param name="clock">The instant every write and every expiry comparison is judged against.</param>
    /// <param name="hostSettings">
    /// Installation-wide settings, read for the two credential-expiry windows the legacy
    /// post-credential validation consulted.
    /// </param>
    /// <param name="unitOfWork">Commit point for the tracked account row a verified registration approves.</param>
    /// <param name="currentUser">The already-authenticated caller of the current request, where there is one.</param>
    /// <param name="passwordPolicy">Bound policy supplying the lock threshold and the attempt window.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// No logger is taken, and the omission is forced rather than chosen: see the audit entry in this
    /// file's header, which records the compilation that proves <c>ILogger&lt;T&gt;</c> is unresolvable
    /// in this project and names the Api-layer control that records sign-in outcomes instead.
    /// </remarks>
    public AuthService(
        IUserRepository users,
        IPortalRepository portals,
        IPermissionService permissions,
        ITokenService tokens,
        IPasswordHasher passwordHasher,
        IClock clock,
        IHostSettingsService hostSettings,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        PasswordPolicyOptions passwordPolicy)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hostSettings);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(passwordPolicy);

        _users = users;
        _portals = portals;
        _permissions = permissions;
        _tokens = tokens;
        _passwordHasher = passwordHasher;
        _clock = clock;
        _hostSettings = hostSettings;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _passwordPolicy = passwordPolicy;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The gates run in the order the legacy provider used and a denial never discloses which one
    /// closed, other than the lock distinction offered to a caller already entitled to it. A tenant that
    /// does not exist receives the same uniform denial, so this member cannot be used to enumerate an
    /// installation's tenants either.
    /// </para>
    /// <para>
    /// The verification code admitted in place of approval is the value the legacy provider compared
    /// against - the tenant identifier and the account identifier joined by a hyphen - and presenting it
    /// approves the account exactly as the provider did at
    /// <c>AspNetMembershipProvider.vb</c> L1465-L1473, persisting the tracked row afterwards.
    /// </para>
    /// <para>
    /// A rejected credential is recorded against the account, which is what locks it once the configured
    /// threshold of consecutive failures is reached inside the configured window. An accepted credential
    /// clears those counters and, when the stored value was produced at a cost the hashing abstraction
    /// now considers superseded, replaces it.
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

        // The tenant is assigned by the Api layer from the alias-resolved request and is unbindable from
        // the request body. Its absence means the transport could not determine which tenant the
        // credential was presented to, which is a malformed request rather than a rejected credential --
        // and it must never be defaulted, because Portals.PortalID is declared IDENTITY(-1, 1), so both
        // zero and minus one are real tenants and neither can stand for "none" (Rule T7).
        if (request.PortalId is not int portalId)
        {
            return Result<LoginResponse>.Failure(
                RequestInvalidCode,
                "The tenant the credential is being presented to could not be determined.");
        }

        // MIGRATION: the tenant aggregate is loaded rather than read ambiently. Login.ascx.vb:L164 and
        // L170 reached PortalSettings.PortalName and PortalSettings.UserRegistration
        // (PortalSettings.vb:L174) through the per-request mutable composite that
        // PortalController.GetCurrentPortalSettings() pulled out of HttpContext.Current.Items. Its
        // replacement, IPortalContext, deliberately carries neither the registration mode nor anything
        // else the approval ladder needs, so the aggregate is the route: this is the one place in this
        // service where a repository is consulted for configuration rather than for an entity.
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

        // MIGRATION: this local occupies the exact place in the flow that the legacy by-reference
        // argument did - Login.ascx.vb:L163 initialised it to the failure member and handed it to
        // UserController.ValidateUser at L164, which passed it on to the provider at
        // AspNetMembershipProvider.vb:L1429 to be mutated. It is initialised to the same member, moved
        // by the same gates in the same order, and then mapped ONCE onto the returned result. Its member
        // name is also the stable audit event name the legacy wrote as loginStatus.ToString
        // (UserController.vb:L80); the audit entry in this file's header records why this layer cannot
        // emit it and which Api-layer control does.
        UserLoginStatus loginStatus = UserLoginStatus.Failure;
        bool approvedByVerification = false;

        // Gate one, the lock. Measured at AspNetMembershipProvider.vb:L1453-L1463.
        if (isLockedOut)
        {
            loginStatus = UserLoginStatus.UserLockedOut;
        }

        // Gate two, approval. Measured at AspNetMembershipProvider.vb:L1465-L1477. The guard reproduces
        // the legacy's own idiom of testing the accumulated status rather than a separate flag, and the
        // superuser exemption is measured: a host account is created by the installer, so it has no
        // verification code to present and no tenant to be approved into.
        if (loginStatus == UserLoginStatus.Failure && !isApproved && !account.IsSuperUser)
        {
            // Composed exactly as the provider composed it at AspNetMembershipProvider.vb:L1468 --
            // `verificationCode = (portalId.ToString & "-" & user.UserID)`. The code is stored nowhere,
            // no column for it appears anywhere in the schema chain, so it is recomputed here and any
            // account still awaiting verification at cut-over holds a code this comparison accepts.
            // Ordinal and untrimmed on purpose: the value is machine generated and echoed back from a
            // link, so any difference at all means the caller did not follow the link that was sent.
            string expectedVerificationCode = FormattableString.Invariant($"{portalId}-{account.UserId}");

            if (!string.Equals(request.VerificationCode, expectedVerificationCode, StringComparison.Ordinal))
            {
                loginStatus = UserLoginStatus.UserNotApproved;
            }
            else if (!await _users
                .SetApprovalAsync(account.UserId, true, cancellationToken)
                .ConfigureAwait(false))
            {
                // The code was correct but the approval could not be recorded. Continuing would sign in
                // an account the store still holds as unapproved, so the gate stays closed. The legacy
                // could not reach this branch: its UpdateUser call at
                // AspNetMembershipProvider.vb:L1473 returned nothing and reported no failure.
                loginStatus = UserLoginStatus.UserNotApproved;
            }
            else
            {
                account.IsApproved = true;
                approvedByVerification = true;
            }
        }

        // Gate three, the credential. Measured at AspNetMembershipProvider.vb:L1479-L1502, which
        // compares nothing at all unless the status is still clear of the lock and the approval gate --
        // the short circuit below reproduces that, so a locked or unapproved account is never usable as
        // a credential oracle. The superuser branch is the provider's own: it validated a host account
        // against the installation rather than against the tenant.
        if (loginStatus == UserLoginStatus.Failure
            && _passwordHasher.Verify(request.Password, storedHash))
        {
            loginStatus = account.IsSuperUser
                ? UserLoginStatus.SuperUser
                : UserLoginStatus.Success;
        }

        loginStatus = PromoteShippedCredentialOutcome(loginStatus, request.Username, request.Password);

        switch (loginStatus)
        {
            case UserLoginStatus.UserLockedOut:
                // MIGRATION: DEFECT 2, ANNOTATED AND DELIBERATELY DIVERGED FROM. Login.ascx.vb:L187 reads
                // `authenticated = (loginStatus <> UserLoginStatus.LOGIN_FAILURE)`, and it sits in the
                // ELSE arm of the L168 test, which special-cases the not-approved member alone. Every
                // other member therefore reaches that inequality - including LOGIN_USERLOCKEDOUT, which
                // is 3 rather than 0, so THE LEGACY TREATED A LOCKED-OUT ACCOUNT AS AUTHENTICATED. The
                // provider compounds it: a locked account never has its credential compared
                // (AspNetMembershipProvider.vb:L1481) and is returned as Nothing (L1505-L1508), so the
                // legacy raised an authenticated event carrying no account at all.
                //
                // THE READING IMPLEMENTED HERE: a locked-out account is a REFUSAL. This is a deliberate,
                // documented divergence rather than a silent repair, and it is taken because AAP 0.9.1
                // exempts a defect that blocks delivery - a lock-out that does not lock out is the
                // failure of the only control standing between an attacker and unlimited credential
                // guessing, and preserving it would make the account-lock bookkeeping this same method
                // performs pointless. The legacy tree is not touched, and MIGRATION_NOTES.md records both
                // the defect and this mapping.
                //
                // MIGRATION: the automatic unlock the provider performed once a lock had aged out
                // (AspNetMembershipProvider.vb:L1454-L1463) is NOT reproduced. It depended on a
                // lock-duration setting the preserved credential policy does not carry, and it performed
                // a security-relevant write on an anonymous request path. A locked account is cleared by
                // the administrative unlock member on IUserService instead.
                return CallerIsEntitledToDetail(portal)
                    ? Result<LoginResponse>.Failure(
                        LockedOutCode,
                        FormattableString.Invariant(
                            $"Account {account.UserId} is locked and must be unlocked by an administrator."))
                    : Denied();

            case UserLoginStatus.UserNotApproved:
                // MIGRATION: the L168-L185 ladder is determined here in full and then deliberately not
                // returned. Determining it discharges the mapping this layer owns and keeps the three
                // legacy message keys live rather than decorative; withholding it is required because
                // the approval gate closes BEFORE any credential is compared, so answering
                // "enter your code" or "that code is wrong" would confirm to an unauthenticated caller
                // that a named account exists and is merely awaiting verification - without that caller
                // ever having proved anything. IAuthService states the same resolution, and the Api
                // layer reinforces it: its unauthorised code list contains none of the three, so a
                // returned approval code would additionally render as 400 rather than 401.
                _ = DetermineApprovalOutcome(portal, request.VerificationCode);

                return Denied();

            case UserLoginStatus.Failure:
                // MIGRATION: the legacy recorded an audit entry for exactly this member and the
                // locked-out member (UserController.vb:L1138-L1141) and nothing else. The bookkeeping
                // below is not that audit entry - it is the store-side failure count that produces the
                // lock, which the legacy delegated to the ASP.NET membership procedures DotNetNuke
                // patched for the purpose (04.00.00.SqlDataProvider:L135-L138). Those objects are
                // installed externally and are mapped alongside, never recreated (Rule T4).
                //
                // MIGRATION: IClock.UtcNow is coordinated universal time where the legacy read the
                // server's local Date.Now. The attempt window is a duration rather than a calendar
                // boundary, so it is unaffected by the offset; the calendar comparison that IS affected
                // is annotated where it occurs, in the credential-expiry advisory.
                await _users.RecordFailedLoginAsync(
                    account.UserId,
                    _passwordPolicy.MaxInvalidPasswordAttempts,
                    TimeSpan.FromMinutes(_passwordPolicy.PasswordAttemptWindowMinutes),
                    _clock.UtcNow,
                    cancellationToken).ConfigureAwait(false);

                return Denied();

            default:
                break;
        }

        // The clock is read ONCE for the whole of an accepted sign-in and the instant is passed down.
        // Reading it again would stamp two rows written for one event with two different instants, which
        // is the kind of skew that makes an audit trail unreadable.
        DateTime now = _clock.UtcNow;

        (bool mustChangePassword, bool passwordExpiring) = await EvaluateCredentialAdvisoriesAsync(
            account,
            now,
            cancellationToken).ConfigureAwait(false);

        await _users.RecordSuccessfulLoginAsync(account.UserId, now, cancellationToken).ConfigureAwait(false);

        await TryReplaceSupersededCredentialAsync(
            account.UserId,
            request.Password,
            storedHash,
            now,
            cancellationToken).ConfigureAwait(false);

        if (approvedByVerification)
        {
            // The one branch of a sign-in that changes a tracked row, and therefore the only one that
            // opens the transaction boundary. It reproduces the account persistence the provider
            // performed immediately after approving a verified registration. Everything else this
            // method writes goes to the external membership store through explicit statements, which is
            // why an ordinary sign-in commits nothing.
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        LoginResponse response = await IssueAsync(portal, account, now, cancellationToken)
            .ConfigureAwait(false);

        ResultReason? advisory = ShippedCredentialAdvisory(loginStatus);

        // MIGRATION: the two weak-credential outcomes are ADVISORIES ON A SUCCESSFUL SIGN-IN, never
        // refusals, which is exactly what the legacy made them: UserController.vb:L1144-L1152 REPLACED an
        // already-successful status with LOGIN_INSECUREADMINPASSWORD or LOGIN_INSECUREHOSTPASSWORD and
        // the caller was signed in regardless. Refusing them would lock an installation out of the two
        // accounts every installation begins with. They travel two ways at once and both are deliberate:
        // as an informational reason on a successful result, which keeps the two cases distinguishable
        // to the Api edge, and as the must-change advisory on the body, because forcing a credential
        // change is the legacy remediation intent for both. No legacy status ordinal reaches the wire.
        response.MustChangePassword = mustChangePassword || advisory is not null;
        response.PasswordExpiring = passwordExpiring;

        return advisory is null
            ? Result<LoginResponse>.Success(response)
            : Result<LoginResponse>.Success(response, advisory);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Rotation itself is performed by <see cref="ITokenService"/>, which retires the presented value and
    /// mints its successor in one atomic unit of work, so two concurrent presentations of one value
    /// cannot both succeed. Its four distinct rejections - unknown, already redeemed, revoked and past
    /// its absolute expiry - are collapsed into the single reason this contract documents, because
    /// distinguishing them for an unauthenticated caller would confirm whether a guessed value had ever
    /// existed. That collapse is a deliberate narrowing of information, not an inability to tell them
    /// apart.
    /// </para>
    /// <para>
    /// MIGRATION: nothing here is ported. The legacy had no rotation and no refresh: the session lived in
    /// a Forms-authentication cookie issued at <c>UserController.vb</c> L919 and L1033, whose
    /// <c>CreatePersistentCookie</c> argument governed whether it outlived the browser session. A
    /// persistent cookie becomes a refresh token - surviving beyond the browser session is exactly what
    /// it was for, and a rotating token does the same job while being single-use and revocable, which the
    /// cookie was neither.
    /// </para>
    /// <para>
    /// The caller snapshot and the credential advisories on the response are re-read from stored state
    /// rather than copied from the retired token, so a role change, a permission change, an
    /// administrator-forced credential update or a newly crossed expiry boundary all take effect at the
    /// next exchange rather than only at the next sign-in. An account deleted since its token was issued
    /// cannot be refreshed.
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

        // The identity, tenant and account name on the rotated response come from the token service's own
        // record of the value being exchanged, never from anything the caller supplied - there is no
        // parameter through which they could be supplied, which is the point. They are read back here
        // only to re-read the mutable facts against them.
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

        DateTime now = _clock.UtcNow;

        response.User = await BuildSnapshotAsync(portal, account, now, cancellationToken)
            .ConfigureAwait(false);

        (bool mustChangePassword, bool passwordExpiring) = await EvaluateCredentialAdvisoriesAsync(
            account,
            now,
            cancellationToken).ConfigureAwait(false);

        response.MustChangePassword = mustChangePassword;
        response.PasswordExpiring = passwordExpiring;

        return Result<LoginResponse>.Success(response);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: ending a session has no exact counterpart, and this is the single most consequential
    /// behavioural difference on this service. <c>PortalSecurity.vb</c> L77 declared
    /// <c>Public Sub SignOut()</c>, which called the Forms-authentication sign-out and then destroyed four
    /// further cookies by name - the language, authentication-type, tenant-alias and tenant-roles cookies
    /// - back-dating the last two by thirty years so the browser dropped them at once; the same file's
    /// shared <c>ClearRoles()</c> performed the roles half alone. Destroying a cookie ended the session
    /// instantly, because the session lived in the cookie.
    /// </para>
    /// <para>
    /// A bearer access token is self-contained and asserts its own validity, so once one has been handed
    /// to a caller NO SERVER ACTION RETRACTS IT. This member therefore revokes the REFRESH TOKEN ONLY,
    /// through <see cref="ITokenService"/>, so that no successor access token can be minted; the access
    /// token already issued remains technically valid until it lapses, and ending a session is that lapse
    /// plus the client discarding its own copy. No revocation list, no server-side session store, no
    /// per-request revocation lookup and no cookie manipulation is introduced - any of them would turn
    /// stateless bearer authentication back into the server-held session this migration exists to leave
    /// behind, and would add a store read to every single request. The configured access-token lifetime
    /// is the only thing that bounds the residual window, which is why it must stay small. The limitation
    /// is recorded in MIGRATION_NOTES.md.
    /// </para>
    /// <para>
    /// Deliberately idempotent: a token that is unknown, already redeemed or already revoked still
    /// succeeds. A sign-out that fails is worse than useless, because a client that cannot complete one
    /// is likely to keep the token it was trying to surrender; and reporting that a value was not found
    /// would make this member an oracle for whether a guessed value exists.
    /// </para>
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
    /// <para>
    /// An anonymous request is answered with no value rather than with a failure, because asking who the
    /// caller is when there is no caller is a legitimate question with a legitimate answer. The state is
    /// re-read from the store rather than echoed from the token's claims, which is how a client observes a
    /// role or permission change without signing out, and what keeps the answer honest about an account
    /// that has since been deleted.
    /// </para>
    /// <para>
    /// <see cref="ICurrentUser"/> is injected and read here; it is never a parameter and never a return
    /// type. This is the only member on this service that describes the caller to itself.
    /// </para>
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

    /// <summary>
    /// Produces the single uniform denial that every closed gate reports.
    /// </summary>
    /// <returns>The uniform failure.</returns>
    /// <remarks>
    /// The code and the wording are intentionally identical for every cause, so nothing distinguishes an
    /// unknown account from a rejected credential, a non-member, an unapproved registration, an account
    /// with no credential on file or a tenant that does not exist.
    /// </remarks>
    private static Result<LoginResponse> Denied()
        => Result<LoginResponse>.Failure(InvalidCredentialsCode, "The account name or credential is not correct.");

    /// <summary>
    /// Determines which of the three legacy approval outcomes the submission produced.
    /// </summary>
    /// <param name="portal">The tenant the sign-in addresses.</param>
    /// <param name="verificationCode">The verification code the submission carried, if any.</param>
    /// <returns>
    /// <see cref="VerificationRequiredCode"/>, <see cref="VerificationCodeInvalidCode"/> or
    /// <see cref="AccountNotApprovedCode"/>, preserving the legacy message keys <c>EnterCode</c>,
    /// <c>InvalidCode</c> and <c>UserNotAuthorized</c> respectively.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the ladder of <c>Login.ascx.vb</c> L168-L185, reproduced branch for branch. The legacy
    /// tested the tenant's registration mode at L170 and, where it was verified registration,
    /// distinguished the first pass through the ladder from a later one by inspecting
    /// <c>rowVerification1.Visible</c> at L171 - a ViewState-backed user interface flag. A stateless
    /// request has no such flag and must not pretend to: the distinction is derived from whether the
    /// submission carried a code at all, which is the question the flag stood in for. That collapses the
    /// legacy's two <c>EnterCode</c> branches - rows not yet shown at L175, and rows shown but the field
    /// left empty at L180 - into one, which is faithful because both answered identically.
    /// </para>
    /// <para>
    /// The emptiness test is <see cref="string.IsNullOrEmpty(string)"/> rather than a whitespace test,
    /// because the legacy compared against <c>Null.NullString</c>, which is the EMPTY STRING and not a
    /// null reference (Rule T7): a submission of blanks was non-empty to the legacy and reported
    /// <c>InvalidCode</c>, and it does so here.
    /// </para>
    /// <para>
    /// MIGRATION: the registration mode is read from the loaded aggregate through the Domain enumeration
    /// <see cref="UserRegistrationMode"/>. The legacy type <c>PortalRegistrationType</c> and the ambient
    /// <c>PortalSettings</c> composite it hung off appear nowhere on any surface in this migration.
    /// </para>
    /// <para>
    /// The caller of this method discards its answer, deliberately and for the reason recorded at that
    /// call site. It is computed rather than skipped because the mapping of these outcomes is owned by
    /// this layer, and a mapping that is never evaluated is a mapping nobody can rely on.
    /// </para>
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
    /// Applies the legacy promotion of an already-successful outcome to its weak-credential counterpart.
    /// </summary>
    /// <param name="loginStatus">The outcome the gates produced.</param>
    /// <param name="username">The submitted account name.</param>
    /// <param name="password">The submitted credential.</param>
    /// <returns>The promoted outcome, or the supplied one when no shipped default was presented.</returns>
    /// <remarks>
    /// <para>
    /// Reproduces <c>UserController.vb</c> L1144-L1152 exactly, including which outcome gates which
    /// account: the ordinary success is promoted only for the shipped tenant administrator, and the
    /// superuser success only for the shipped host account. Testing the accumulated outcome rather than
    /// the account's own superuser flag is what the legacy did and is the more precise test, because the
    /// outcome already encodes which of the two credential comparisons succeeded.
    /// </para>
    /// <para>
    /// The account names and credentials are the values the product was distributed with, and the whole
    /// value of the check is that it recognises exactly those. MIGRATION: the legacy compared the name
    /// with VB's <c>=</c> operator under the default binary comparison, making it case-SENSITIVE, so an
    /// account signing in as <c>Admin</c> escaped the advisory while being just as exposed. The
    /// comparison here is case-insensitive, which is a deliberate widening: account names are resolved
    /// case-insensitively everywhere else in this migration, so a case-sensitive advisory would be
    /// inconsistent with the resolution that admitted the account. The credential itself is compared
    /// exactly, as the legacy did. Neither the matched name nor the matched credential ever appears in a
    /// reported message.
    /// </para>
    /// </remarks>
    private static UserLoginStatus PromoteShippedCredentialOutcome(
        UserLoginStatus loginStatus,
        string username,
        string password)
    {
        if (loginStatus == UserLoginStatus.Success
            && string.Equals(username, ShippedAdministratorAccountName, StringComparison.OrdinalIgnoreCase)
            && ShippedAdministratorCredentials.Contains(password, StringComparer.Ordinal))
        {
            return UserLoginStatus.InsecureAdminPassword;
        }

        if (loginStatus == UserLoginStatus.SuperUser
            && string.Equals(username, ShippedHostAccountName, StringComparison.OrdinalIgnoreCase)
            && ShippedHostCredentials.Contains(password, StringComparer.Ordinal))
        {
            return UserLoginStatus.InsecureHostPassword;
        }

        return loginStatus;
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

    /// <summary>
    /// Decides whether the caller of this request may be told that an account is locked.
    /// </summary>
    /// <param name="portal">The tenant the sign-in addresses.</param>
    /// <returns>
    /// <see langword="true"/> when the caller already administers the tenant or the installation.
    /// </returns>
    /// <remarks>
    /// The distinction is withheld from an anonymous caller because it would reveal that a named account
    /// exists. An already-authenticated administrator of the tenant, or a host account, learns nothing it
    /// could not read from the account list, so it receives the actionable answer instead. The test is by
    /// identifier rather than by role name, because a role name is tenant-configurable.
    /// </remarks>
    private bool CallerIsEntitledToDetail(Portal portal)
        => _currentUser.IsAuthenticated
            && (_currentUser.IsSuperUser
                || (_currentUser.UserId is int actor && portal.AdministratorId == actor));

    /// <summary>
    /// Resolves an account by name within a tenant, admitting a host account that holds no membership.
    /// </summary>
    /// <param name="portalId">The tenant being signed in to.</param>
    /// <param name="username">The submitted account name.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The account, or <see langword="null"/> when none is in scope.</returns>
    /// <remarks>
    /// The tenant-scoped lookup runs first so that an ordinary account of another tenant is never
    /// admitted. Only an installation-wide host account is admitted from outside the tenant, which
    /// reproduces the provider's own treatment of one (<c>AspNetMembershipProvider.vb</c> L1489).
    /// </remarks>
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

    /// <summary>
    /// Issues a token pair for an account whose credential has already been accepted.
    /// </summary>
    /// <param name="portal">The tenant signed in to.</param>
    /// <param name="account">The authenticated account.</param>
    /// <param name="asOfUtc">The instant role validity windows are evaluated against.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The issued pair, carrying a freshly read snapshot of the caller.</returns>
    /// <exception cref="InvalidOperationException">
    /// The token service declined to issue a pair. That is a server fault rather than a denial, so it is
    /// not one of the reasons this contract documents.
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

        return response;
    }

    /// <summary>
    /// Builds the caller snapshot carried on a token response and returned by the current-user read.
    /// </summary>
    /// <param name="portal">The tenant the caller is signed in to.</param>
    /// <param name="account">The account.</param>
    /// <param name="asOfUtc">The instant role validity windows are evaluated against.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The snapshot.</returns>
    /// <remarks>
    /// <para>
    /// Roles are resolved as of the supplied instant, so an assignment whose validity window has not
    /// opened or has already closed does not become a claim. Permission keys are resolved at tenant scope
    /// through <see cref="IPermissionService"/>, which owns the caller-to-role-names rule and delegates
    /// the allow-and-deny precedence to the single evaluator; resolving them from a repository here would
    /// duplicate that precedence, and a security rule with two implementations is a security rule with
    /// two answers. The keys are required by <see cref="ITokenService.IssueTokensAsync"/>, which by
    /// contract never invents, filters, reorders or deduplicates what it is handed.
    /// </para>
    /// <para>
    /// Those keys tell a client which affordances to render; they never stand in for the server-side
    /// authorisation policy, which re-evaluates on every request. A resolution that cannot be completed
    /// therefore yields no keys rather than refusing a sign-in whose credential was already accepted -
    /// the client offers less than it might, and the API refuses anything it should not allow.
    /// </para>
    /// <para>
    /// MIGRATION: <c>Email</c> is projected as the empty string when the column holds no value, which
    /// preserves the legacy string sentinel exactly: <c>Null.NullString</c> is the EMPTY STRING and not a
    /// null reference, so a legacy consumer never saw a null here and does not begin to now (Rule T7).
    /// </para>
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
    /// Evaluates the post-credential advisories a successful sign-in carries.
    /// </summary>
    /// <param name="account">The authenticated account.</param>
    /// <param name="asOfUtc">The instant the expiry calendar comparison is judged against.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// Whether the caller must change its credential before continuing, and whether that credential is
    /// approaching expiry.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is <c>UserController.vb</c> L1171-L1197, the overload that returned the legacy
    /// post-credential validation enumeration. MIGRATION: that enumeration is NOT one of the nine Domain
    /// enumerations and no type is invented for it here - its five members are surfaced as the advisory
    /// flags this method reports, which the sign-in response carries. The forced update is reported first
    /// and suppresses the expiry evaluation entirely, preserving the legacy's <c>ElseIf</c>: the legacy
    /// enumeration was single-valued with precedence, so it could never report both, and neither does
    /// this.
    /// </para>
    /// <para>
    /// MIGRATION: the whole evaluation is skipped for a host account, which is measured rather than
    /// assumed - <c>Website/admin/Authentication/Login.ascx.vb</c> L511 wraps the call in
    /// <c>If Not objUser.IsSuperUser Then</c>, leaving the outcome at its valid member for a superuser.
    /// The weak-credential advisory for the shipped host account is a different rule and still applies,
    /// which is why it is decided by the caller of this method rather than inside it.
    /// </para>
    /// <para>
    /// MIGRATION: the reminder suppression argument is gone. The legacy overload took an
    /// <c>ignoreExpiring</c> flag, and the sign-in path this service replaces passed
    /// <see langword="false"/> for it - measured at <c>Login.ascx.vb</c> L893, the arm that handled the
    /// event the DotNetNuke sign-in control raised - so the reminder IS reported here. The alternative
    /// call sites that passed <see langword="true"/> were the interstitial's own re-entry paths, which
    /// have no counterpart in a stateless API. No suppression parameter is added to the dictated
    /// signature; the reminder is non-blocking by contract, so a client may decline it.
    /// </para>
    /// <para>
    /// MIGRATION: the expiry comparison is the one place the coordinated-universal-time clock can differ
    /// observably from the legacy. <c>UserController.vb</c> L1181 and L1183 compared against VB's
    /// <c>Today</c>, which is the server's LOCAL calendar date; the comparison here is against the
    /// coordinated universal date, so for an installation east or west of the meridian a credential can
    /// be reported expired, or reminded about, up to ONE CALENDAR DAY earlier or later than the legacy
    /// would have. Coordinated universal time is nevertheless correct for the target: a container has no
    /// meaningful local zone, and two replicas in two zones would otherwise disagree about the same
    /// account. Recorded in MIGRATION_NOTES.md rather than absorbed.
    /// </para>
    /// <para>
    /// GAP REPORTED: the profile advisory is not evaluated here. The legacy condition at
    /// <c>UserController.vb</c> L1189-L1193 combined a per-tenant setting,
    /// <c>Security_RequireValidProfileAtLogin</c>, with the completeness check at
    /// <c>ProfileController.vb</c> L305-L319. That setting is not tenant configuration in the schema
    /// sense: it is a module setting on the tenant's User Accounts module instance, reached through
    /// <c>UserModuleBase.GetSetting</c> over <c>UserController.GetUserSettings</c>, and both it and the
    /// profile-property definitions the check reads belong to the account-administration vertical rather
    /// than to sign-in. Evaluating them here would put a profile rule and two further reads on the
    /// anonymous credential path and would give the completeness rule a second implementation. The flag
    /// is carried by the response contract and is set by the surface that owns the check; no setting is
    /// invented here, and the reduction is recorded in MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    private async Task<(bool MustChangePassword, bool PasswordExpiring)> EvaluateCredentialAdvisoriesAsync(
        User account,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        if (account.IsSuperUser)
        {
            return (false, false);
        }

        // The administrator-forced update, read from the account row's own flag - the legacy read the
        // membership property of the same intent and gave it the highest precedence.
        if (account.UpdatePassword)
        {
            return (true, false);
        }

        int expiryDays = await ReadHostSettingDaysAsync(PasswordExpiryHostSettingName, 0, cancellationToken)
            .ConfigureAwait(false);

        if (expiryDays <= 0)
        {
            return (false, false);
        }

        // MIGRATION: the legacy read a non-nullable date that the null-sentinel helper had already
        // collapsed to Date.MinValue when the column held no value, so an account with no recorded change
        // date computed MinValue plus the window and was reported EXPIRED. The target models the column
        // as nullable and treats an absent date as "no expiry can be computed" instead. That is a
        // deliberate divergence in the safe direction: the alternative forces a credential change on
        // every account whose date was never recorded, on the strength of a sentinel rather than of a
        // fact. The case is unreachable against a real installation, because the externally installed
        // membership objects populate the column when the credential is created (Rule T4).
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

        int reminderDays = await ReadHostSettingDaysAsync(
            PasswordExpiryReminderHostSettingName,
            DefaultPasswordExpiryReminderDays,
            cancellationToken).ConfigureAwait(false);

        return (false, expiresOn < today.AddDays(reminderDays));
    }

    /// <summary>
    /// Reads an installation-wide setting expressed as a whole number of days.
    /// </summary>
    /// <param name="settingName">The setting name, spelled exactly as the legacy wrote it.</param>
    /// <param name="fallback">The value to apply when the setting is absent or unusable.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The configured number of days, or <paramref name="fallback"/>.</returns>
    /// <remarks>
    /// MIGRATION: the legacy read was <c>CType(hostSettings("PasswordExpiry"), Integer)</c> at
    /// <c>PasswordConfig.vb</c> L57, a late conversion compiled with Option Strict OFF that threw on any
    /// non-numeric value and applied the property's own initialiser when the setting was absent. The
    /// conversion is made explicit and total here, and it is culture-invariant because a setting row is
    /// machine data rather than a localised value: an absent, blank or unparseable setting yields the
    /// fallback rather than faulting a sign-in over a mistyped configuration row. Both spellings of
    /// absence are tested, since <c>Null.NullString</c> is the empty string rather than a null reference
    /// (Rule T7). The <c>out</c> token below declares an out VARIABLE while consuming a base-class-library
    /// parse method; it is not an out PARAMETER, and no member of this service declares one, which is what
    /// AAP 0.7.4 forbids.
    /// </remarks>
    private async Task<int> ReadHostSettingDaysAsync(
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

    /// <summary>
    /// Replaces a stored credential representation that the hashing abstraction reports as superseded.
    /// </summary>
    /// <param name="userId">The account whose stored representation is replaced.</param>
    /// <param name="password">The credential just accepted, which the replacement is computed from.</param>
    /// <param name="storedHash">The stored representation that was just verified against.</param>
    /// <param name="asOfUtc">The instant the replacement is stamped with.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A task that completes once the attempt has been made.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the credential upgrade performed on a successful sign-in, and its scope must be
    /// stated precisely because a broader reading of it has already been corrected once in
    /// MIGRATION_NOTES.md. It upgrades the WORK FACTOR of an existing one-way hash. It does NOT migrate a
    /// legacy value: the legacy store was reversible - registered with an encrypted format and retrieval
    /// enabled at <c>Website/release.config</c> L236-L246 - and verifying such a value would need the
    /// symmetric material committed at L89-L93, which is out of scope and is not reproduced anywhere in
    /// this tree. The hashing abstraction holds exactly one algorithm and no legacy branch, so a
    /// pre-migration value cannot be verified at all and its holder regains access through an
    /// administrative reset on <see cref="IUserService"/>. This method is reachable only after a
    /// successful verification, which is precisely why it can never see one.
    /// </para>
    /// <para>
    /// The upgrade is TRANSPARENT: no extra round trip for the caller, no change to the response, and no
    /// new failure mode. A persistence failure must not fail a sign-in whose credential was correct - the
    /// account simply keeps a still-valid representation at the superseded cost and the next successful
    /// sign-in tries again - so the failure is contained here. Cancellation is deliberately excluded from
    /// the containment and continues to propagate, because a cancelled request is not a failed write.
    /// GAP REPORTED: the containment cannot be recorded, because no logger is resolvable in this project;
    /// the audit entry in this file's header sets out the proof and names the Api-layer control that
    /// records the request itself.
    /// </para>
    /// </remarks>
    private async Task TryReplaceSupersededCredentialAsync(
        int userId,
        string password,
        string storedHash,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        if (!_passwordHasher.NeedsRehash(storedHash))
        {
            return;
        }

        try
        {
            await _users
                .SetPasswordHashAsync(userId, _passwordHasher.Hash(password), asOfUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Contained on purpose, and this handler is empty by design rather than by omission: the only
            // correct response to a failed cost upgrade is to leave the working credential alone and
            // proceed with a sign-in that was already valid. The condition above re-raises cancellation.
        }
    }

    /// <summary>
    /// Escalates a token-store outage, which is a server fault rather than an authentication outcome.
    /// </summary>
    /// <param name="reason">The reason the token service reported.</param>
    /// <exception cref="InvalidOperationException">The store could not be read or written.</exception>
    /// <remarks>
    /// Every other reason the token service reports describes the presented token and is collapsed into
    /// this service's single token failure. An unavailable store describes neither the caller nor the
    /// token, so reporting it as a denial would tell a caller its token was invalid when it was not, and
    /// would tell a caller whose credential had just been accepted that the credential was wrong.
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
