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
// MIGRATION: the audit record IS emitted from this layer, through IAuditSink. UserController.vb:L66-L82
// wrote an audit entry whose log type key was `loginStatus.ToString` (L80), carrying the properties IP,
// tenant identifier, tenant name, account name and account identifier; it was raised only for the
// locked-out and failure outcomes (UserController.vb:L1138-L1141) and was always passed
// Null.NullInteger, minus one, as the account identifier - so the legacy trail never recorded WHICH
// account failed. Both halves of the replacement live here: the local status below is computed for
// every outcome and its member name IS the stable event name, and the outcome is then recorded through
// Abstractions/IAuditSink.cs. Every outcome is recorded, not just the two the legacy recorded, and the
// account identifier is real rather than the sentinel - two deliberate improvements on a trail that
// could not say who failed to sign in.
//
// An earlier revision reported this as an unclosable gap on the grounds that ILogger<T> is not
// resolvable here, and that observation is still true: DnnMigration.Application declares
// FluentValidation and nothing else, per AAP 0.6.1, and Microsoft.Extensions.Logging is absent from the
// reference pack a class library targets - proven by compiling a probe, which fails with CS0234 on the
// namespace and CS0246 on the generic. The conclusion drawn from it was wrong. What the constraint
// forbids is naming a LOGGING PACKAGE here, not recording an event: IAuditSink is declared in this
// project, takes no dependency of any kind, and is implemented in Infrastructure by a type that does
// hold an ILogger. The layering rule is satisfied and the trail is preserved, which is strictly better
// than reporting the gap.
//
// The Api layer's request log remains supplemental rather than the record of record:
// Api/Middleware/RequestLoggingMiddleware.cs records every request with its method, path, status code
// and elapsed time, raising a refused sign-in to warning level, and Api/Middleware/
// CorrelationIdMiddleware.cs binds the correlation identifier those events are read against - which is
// what lets an audit event and the request that produced it be read together.
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
    /// Returned to the caller, which an earlier revision of this service could not safely do. See
    /// <see cref="DetermineApprovalOutcome"/> for the ordering that makes it safe.
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
    /// Reported when a correct verification code was presented alongside a correct credential and the
    /// resulting approval could not be written to the membership store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from the three approval outcomes on purpose. Those three describe something the caller
    /// did - no code, a wrong code, or a tenant that admits no self-verification - and each of them is
    /// actionable by the caller. This one describes a dependency that did not answer, where the caller
    /// did everything correctly, so reporting any of the three here would tell the account's own owner
    /// that a code it copied correctly was wrong and invite it to hunt for a mistake it did not make.
    /// </para>
    /// <para>
    /// Its reason token ends in <c>store_unavailable</c>, which is the marker the Api layer's status
    /// mapping classifies as a dependency failure, so this surfaces as <c>503</c> rather than as an
    /// authentication refusal. That is the honest reading: nothing about the submission was wrong.
    /// </para>
    /// </remarks>
    private const string ApprovalStoreUnavailableCode = "auth.approval_store_unavailable";

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
    /// Audit-only code recording that a submitted credential did not match. Never returned to a caller.
    /// </summary>
    /// <remarks>
    /// The four codes below exist so the trail can say WHICH question closed while every caller receives
    /// the same uniform refusal. They are deliberately separate from the reason codes above: those are
    /// part of the wire contract and must stay stable for the Api edge's status mapping, whereas these are
    /// read only by an operator and must never leak into a response. Two of them reuse a wire code
    /// (<see cref="LockedOutCode"/> for the lock, which the Api edge does disclose to an entitled caller),
    /// which is safe in the one direction that matters: reading a wire code into the trail discloses
    /// nothing, while the reverse would.
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

    /// <summary>
    /// Resource type recorded on the credential-maintenance audit event.
    /// </summary>
    private const string CredentialResourceType = "Credential";

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
    /// Installation-wide setting naming the number of minutes after which a locked account unlocks by
    /// itself. An explicit zero disables automatic unlocking altogether
    /// (<c>AspNetMembershipProvider.vb</c> L66-L72).
    /// </summary>
    private const string AutoAccountUnlockDurationHostSettingName = "AutoAccountUnlockDuration";

    /// <summary>
    /// The automatic-unlock window applied when the installation configures none, measured from the
    /// legacy fallback (<c>AspNetMembershipProvider.vb</c> L74).
    /// </summary>
    private const int DefaultAutoAccountUnlockDurationMinutes = 10;

    /// <summary>
    /// Fingerprints of the two credentials the shipped administrator account was distributed with
    /// (<c>UserController.vb</c> L1145).
    /// </summary>
    /// <remarks>
    /// <para>
    /// C-04: these are lower-case hexadecimal SHA-256 digests, NOT the credentials. An earlier revision held
    /// the two values verbatim as string literals, which put a working credential for the account every
    /// installation begins with into source control - the weakness catalogued as CWE-798 - and it did so in a
    /// file whose own prompt permits a plaintext credential on exactly one member, the inbound request's
    /// password. Anyone reading the repository learned two credentials to try.
    /// </para>
    /// <para>
    /// A digest suffices because the check is an EQUALITY TEST, never a lookup: the submitted credential is
    /// hashed and compared, so the check recognises exactly the same two values it always did and the
    /// behaviour is unchanged. The literal below is not usable at a sign-in prompt, which is the property the
    /// finding asks for.
    /// </para>
    /// <para>
    /// SHA-256 rather than the credential hasher, deliberately. The credential hasher is a slow,
    /// per-value-salted one-way function - right for verifying a stored credential, wrong here on two
    /// counts: a salted digest differs on every computation so it cannot be written as a constant, and paying
    /// a work factor to evaluate an ADVISORY would slow every sign-in in the installation. The threat model
    /// differs too. This is not credential storage; the value being fingerprinted is public knowledge,
    /// published in the product's own documentation, so there is nothing here for a salt to defend and no
    /// offline attack to slow down.
    /// </para>
    /// </remarks>
    private static readonly string[] ShippedAdministratorCredentialFingerprints =
    [
        "8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918",
        "6529da56f1a1f6ae50652d544c131050256bc52568611b75049a90ca047bb221",
    ];

    /// <summary>
    /// Fingerprints of the two credentials the shipped host account was distributed with
    /// (<c>UserController.vb</c> L1150). See
    /// <see cref="ShippedAdministratorCredentialFingerprints"/> for why these are digests.
    /// </summary>
    private static readonly string[] ShippedHostCredentialFingerprints =
    [
        "4740ae6347b0172c01254ff55bae5aff5199f4446e7f6d643d40185b3f475145",
        "06f25bcf0acf336cdee0f93659c7647d8ab7b9c3c7d1d6274887a8c1d1b758cb",
    ];

    private readonly IUserRepository _users;
    private readonly IPortalRepository _portals;
    private readonly IPermissionService _permissions;
    private readonly IUserService _accounts;
    private readonly ITokenService _tokens;
    private readonly IPasswordHasher _passwordHasher;
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
    /// <param name="portals">
    /// Tenant resolution. It supplies the tenant name carried on the caller snapshot, the administrator
    /// identifier the lock disclosure is judged against, and the registration mode the approval ladder
    /// reads - the last of which the legacy took ambiently and this service takes from the aggregate.
    /// </param>
    /// <param name="permissions">Tenant-scope permission-key resolution for the claims.</param>
    /// <param name="accounts">
    /// The account-administration vertical, asked ONE question: whether the admitted account must complete
    /// its profile. The rule itself stays there, so it has a single implementation.
    /// </param>
    /// <param name="tokens">Token minting, rotation and revocation.</param>
    /// <param name="passwordHasher">One-way credential verification and replacement detection.</param>
    /// <param name="clock">The instant every write and every expiry comparison is judged against.</param>
    /// <param name="hostSettings">
    /// Installation-wide settings, read for the two credential-expiry windows the legacy
    /// post-credential validation consulted.
    /// </param>
    /// <param name="unitOfWork">Commit point for the tracked account row a verified registration approves.</param>
    /// <param name="currentUser">The already-authenticated caller of the current request, where there is one.</param>
    /// <param name="audit">
    /// Records the sign-in, renewal and sign-out outcomes under the legacy event names. Package-neutral by
    /// construction, which is what allows an audit trail to be kept from a project that can name no
    /// logging package - see the audit entry in this file's header.
    /// </param>
    /// <param name="passwordPolicy">Bound policy supplying the lock threshold and the attempt window.</param>
    /// <param name="diagnostics">
    /// The route by which this service reports a security-relevant anomaly it has decided not to fail the
    /// request over. It is a Domain contract carrying a closed set of occurrences and no message, so it is
    /// neither a logger nor a way of becoming one.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// No LOGGER is taken, and that omission is forced rather than chosen: <c>ILogger&lt;T&gt;</c> is not
    /// resolvable in this project, and the audit entry in this file's header records the compilation that
    /// proves it and names the Api-layer control that records sign-in outcomes instead. What this service
    /// takes in its place are TWO reporting collaborators, and neither is a way around the constraint: the
    /// constraint forbids depending on a logging FRAMEWORK, not reporting a fact. <see cref="IAuditSink"/>
    /// carries the business trail - who signed in, who was refused, what was written - and
    /// <see cref="ISecurityDiagnostics"/> carries the anomalies that fail no request and appear in no other
    /// record, whose shape makes it impossible to pass a message, an exception or an object through it.
    /// Leaving either unreported would mean nobody could ever discover the conditions it names.
    /// </remarks>
    public AuthService(
        IUserRepository users,
        IPortalRepository portals,
        IPermissionService permissions,
        IUserService accounts,
        ITokenService tokens,
        IPasswordHasher passwordHasher,
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
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hostSettings);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(passwordPolicy);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _users = users;
        _portals = portals;
        _permissions = permissions;
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _tokens = tokens;
        _passwordHasher = passwordHasher;
        _clock = clock;
        _hostSettings = hostSettings;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _audit = audit;
        _passwordPolicy = passwordPolicy;
        _diagnostics = diagnostics;
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

        // NOTE THE ABSENCE OF AN EARLY RETURN HERE AND AT THE NEXT TWO READS. Each of the three lookups can
        // fail to find anything, and an earlier revision returned the uniform denial from each the moment it
        // did. That was correct about WHAT to answer and wrong about WHEN: every one of those returns skipped
        // the credential comparison, which is the one deliberately expensive step on this path, so an unknown
        // tenant, an unknown account and an account with no credential record all answered measurably sooner
        // than a real one. Identical wording does not conceal that - the response TIME distinguishes them - so
        // an unauthenticated caller could enumerate accounts by measuring, which is precisely the oracle the
        // uniform wording exists to close. The three outcomes are therefore collected and answered together,
        // AFTER one comparison has been performed unconditionally, and the audit record each of them used to
        // raise separately is raised once from the consolidated refusal below with the same arguments.
        User? account = portal is null
            ? null
            : await ResolveAccountByNameAsync(portalId, request.Username, cancellationToken)
                .ConfigureAwait(false);

        (bool exists, string? storedHash, bool isApproved, bool isLockedOut) = account is null
            ? (false, null, false, false)
            : await _users
                .GetCredentialStateAsync(account.UserId, cancellationToken)
                .ConfigureAwait(false);

        // EXACTLY ONE CREDENTIAL COMPARISON, ON EVERY STRUCTURALLY VALID ATTEMPT, WHATEVER WAS FOUND. When
        // there is no stored representation to compare against, the decoy the hashing abstraction publishes
        // stands in for one: it is produced at the current work factor from input the implementation does not
        // retain, so the comparison costs what a genuine one costs and cannot succeed. See
        // IPasswordHasher.UnmatchableHash for why the decoy belongs to that abstraction rather than to this
        // call site.
        //
        // The comparison happens BEFORE the gates below rather than inside them, and the gates read its
        // result rather than deciding whether to perform it. That is what makes the work unconditional: a
        // comparison performed only when the gates allow it would still be skipped for a locked account, and
        // the timing difference would come straight back.
        //
        // ONE residual difference is acknowledged rather than hidden: an unknown tenant performs one database
        // read where a known account performs three. Those reads are ordinary indexed lookups, and their
        // combined cost is a small fraction of a single comparison at this work factor, so the signal they
        // leave is far below the one this change removes. Equalising them would mean issuing reads whose
        // results are discarded, which trades a measurable improvement for a much larger and permanent cost
        // on every legitimate sign-in.
        bool credentialAccepted = _passwordHasher.Verify(
            request.Password,
            storedHash ?? _passwordHasher.UnmatchableHash);

        if (portal is null || account is null || !exists || storedHash is null)
        {
            // ONE REFUSAL, FOUR CAUSES, AND THE TRAIL STILL SEPARATES THEM. The caller is told nothing about
            // which of the four closed - that uniformity is the whole point of collecting them here - but the
            // audit record carries whatever was actually established, so an operator can still distinguish a
            // probe against an unknown tenant from one against an unknown account from an account that exists
            // and holds no credential. The legacy trail reported all of these identically, because it recorded
            // neither identifier; each term below is null-conditional precisely because it may be the term
            // that was never resolved.
            RecordSignInOutcome(
                UserLoginStatus.Failure,
                portalId,
                portal?.PortalName ?? string.Empty,
                request.Username,
                account?.UserId);
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

        // Gate one, the lock, INCLUDING the automatic unlock the provider performed. Measured at
        // AspNetMembershipProvider.vb:L1453-L1463, whose AutoUnlockUser helper is at L64-L86: it reads the
        // installation-wide AutoAccountUnlockDuration setting, treats an absent or unparsable value as ten
        // minutes, treats an explicit zero as "never unlock automatically", and unlocks once LastLockoutDate
        // has aged past the window. An account whose lock has aged out therefore continues into the credential
        // decision exactly as it did before this migration; one whose lock is still current does not.

        // Separates the two causes that both close the approval gate. The gate's OUTCOME is the same either
        // way - no sign-in - but its EXPLANATION is not, and now that the explanation is returned rather than
        // withheld, conflating them would misdirect the one caller who did everything right. See
        // ApprovalStoreUnavailableCode.
        bool approvalCouldNotBeRecorded = false;

        // THE ORDER OF THE THREE GATES BELOW IS NOT THE LEGACY ORDER, AND THE CHANGE IS THE POINT.
        //
        // The provider evaluated lock, then approval, then the credential
        // (AspNetMembershipProvider.vb:L1453-L1502), and an earlier revision of this method reproduced that
        // sequence faithfully - including the consequence that the approval gate PERSISTED AN APPROVAL before
        // any credential had been compared. The verification code it compared is not a secret: the provider
        // composed it at L1468 as the tenant identifier, a hyphen and the account identifier, both of which
        // appear in ordinary URLs. So a caller who knew nothing but a user name could submit that code with a
        // deliberately wrong password and the account would be approved and committed, then refused. The
        // refusal made it look harmless; the state change was permanent, and it turned a pending registration
        // into a live account awaiting only a password guess. AAP 0.9.1 exempts an authentication bypass from
        // the preserve-the-defect rule because it blocks delivery, IAuthService states the same requirement as
        // its contract, and MIGRATION_NOTES.md records both the defect and this ordering.
        //
        // The credential is therefore proven FIRST, and the approval gate is reached only by a caller who has
        // already presented the correct password. Every observable outcome is preserved for a correct
        // credential - a verified code still approves and signs in, an absent or wrong code is still refused -
        // and the outcome that changes is exactly the one that should never have existed: a wrong password now
        // approves nothing.
        //
        // The lock is still evaluated ahead of both, so a locked account is never signed in and its credential
        // result is discarded unread. Reading the comparison rather than skipping it is what keeps a locked
        // account from answering faster than an unlocked one; discarding the result is what keeps it from being
        // a credential oracle. Both properties hold at once.
        // The automatic unlock is evaluated INSIDE the lock condition rather than after it, so that an account
        // whose lock has aged out falls through to the credential branch below. Written as two statements - a
        // lock test and then a separate credential test - the unlock would have been performed and then
        // ignored, because the account was still "locked" as far as the branch selection was concerned: an
        // account the provider would have unlocked and signed in would have been refused. The short-circuit
        // also means the unlock read is issued only for an account that is actually locked.
        if (isLockedOut
            && !await TryAutomaticUnlockAsync(account, cancellationToken).ConfigureAwait(false))
        {
            loginStatus = UserLoginStatus.UserLockedOut;
        }
        else if (credentialAccepted)
        {
            // The credential is proven from here down. The superuser branch is the provider's own: it
            // validated a host account against the installation rather than against the tenant.
            //
            // Approval, measured at AspNetMembershipProvider.vb:L1465-L1477. The superuser exemption is
            // measured too: a host account is created by the installer, so it has no verification code to
            // present and no tenant to be approved into.
            if (!isApproved && !account.IsSuperUser)
            {
                // Composed exactly as the provider composed it at AspNetMembershipProvider.vb:L1468 --
                // `verificationCode = (portalId.ToString & "-" & user.UserID)`. The code is stored nowhere,
                // no column for it appears anywhere in the schema chain, so it is recomputed here and any
                // account still awaiting verification at cut-over holds a code this comparison accepts.
                // Ordinal and untrimmed on purpose: the value is machine generated and echoed back from a
                // link, so any difference at all means the caller did not follow the link that was sent.
                //
                // Its predictability is why this comparison must sit behind the credential and not in front
                // of it. Nothing here makes the code less guessable - it cannot, since existing pending
                // accounts hold codes of exactly this shape - so the protection is the password, and the
                // ordering is what applies it.
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
            else
            {
                loginStatus = account.IsSuperUser
                    ? UserLoginStatus.SuperUser
                    : UserLoginStatus.Success;
            }
        }

        // Otherwise the status remains Failure: the credential was wrong, whatever the account's approval
        // state. MIGRATION: this is a second deliberate consequence of the reordering, and it is an
        // improvement rather than a side effect. Under the legacy order a wrong password against an UNAPPROVED
        // account produced the not-approved outcome, which meant the failed-attempt counter was never
        // incremented - so pending accounts could be guessed against without limit and could never lock. They
        // now count like every other account.

        loginStatus = PromoteShippedCredentialOutcome(loginStatus, request.Username, request.Password);

        // ONE emission covers every REFUSAL from here down, because this is the last statement that can
        // change the outcome: nothing below reassigns it, and every refusing path below reads it rather
        // than recomputing it. Placing the call here rather than in each branch is what makes the trail
        // complete by construction - a refusing branch added later is audited without anyone remembering
        // to audit it. An ACCEPTED outcome is deliberately excluded here and recorded further down, after
        // the tokens have been issued, so that the accepted record can carry the weak-credential advisory
        // and cannot describe a sign-in that then failed to issue.
        if (loginStatus is not (UserLoginStatus.Success or UserLoginStatus.SuperUser))
        {
            RecordSignInOutcome(
                loginStatus,
                portalId,
                portal.PortalName ?? string.Empty,
                request.Username,
                account.UserId);
        }

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
                // MIGRATION: reaching this arm means the automatic unlock attempted at gate one declined,
                // so the lock is genuinely current - either the window has not elapsed or the
                // installation has switched automatic unlocking off with an explicit zero. The lock can
                // then only be cleared by the administrative unlock member on IUserService.
                return CallerIsEntitledToDetail(portal)
                    ? Result<LoginResponse>.Failure(
                        LockedOutCode,
                        FormattableString.Invariant(
                            $"Account {account.UserId} is locked and must be unlocked by an administrator."))
                    : Denied();

            case UserLoginStatus.UserNotApproved:
                // MIGRATION: the outcome is recorded on the trail as well as returned, so an operator reading
                // the audit stream sees the refusal even though the caller is told only which step of the
                // ladder is outstanding.
                //
                // MIGRATION: the L168-L185 ladder is determined here in full AND RETURNED, which restores the
                // legacy's three message keys to the surface a user actually reads. An earlier revision
                // determined the ladder and then discarded it, and that was the correct answer to the question
                // as it then stood: the approval gate closed BEFORE any credential was compared, so answering
                // "enter your code" or "that code is wrong" would have confirmed to a caller who had proved
                // nothing that a named account existed and was merely awaiting verification. The uniform denial
                // was the only safe answer available.
                //
                // THE REORDERING ABOVE REMOVED THAT CONSTRAINT RATHER THAN WEAKENING IT. This arm is now
                // reachable only from inside the branch guarded by an accepted credential, so every caller who
                // arrives here has already presented the account's correct password. The existence of the
                // account, and its pending state, are facts that caller either owns or has already compromised;
                // withholding them protects nobody and costs the account's own owner the one sentence that
                // tells it what to do next. Minimal Change Clause item 4 asks for equivalent messages, and this
                // is where they become payable. IAuthService admits exactly these three codes, which preserve
                // the legacy message keys EnterCode, InvalidCode and UserNotAuthorized respectively.
                //
                // The uniform denial still answers every gate that closes WITHOUT credential proof - an unknown
                // tenant, an unknown account, an absent credential row, a wrong password - so the enumeration
                // surface is unchanged. What differs is only what a caller learns about an account it has just
                // authenticated to.
                //
                // These codes are deliberately NOT in the Api layer's unauthorised list, so they render as 400
                // rather than 401. That is correct rather than incidental: the credential was accepted, so the
                // caller is not unauthenticated - the submission is incomplete, and a 401 would invite a client
                // to re-prompt for a credential that was never the problem.
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
                MembershipWriteOutcome failureRecorded = await _users.RecordFailedLoginAsync(
                    account.UserId,
                    _passwordPolicy.MaxInvalidPasswordAttempts,
                    TimeSpan.FromMinutes(_passwordPolicy.PasswordAttemptWindowMinutes),
                    _clock.UtcNow,
                    cancellationToken).ConfigureAwait(false);

                // MIGRATION: recorded with the REAL account identifier. The legacy passed
                // Null.NullInteger for it (UserController.vb:L79), so its trail recorded that a sign-in
                // had failed without recording whose - which makes a credential-stuffing run against one
                // account indistinguishable from scattered mistyping. Deliberate improvement, and the
                // account NAME is recorded as well because the legacy recorded that too.
                // THE ANSWER IS READ, WHICH IT PREVIOUSLY WAS NOT. This write is not bookkeeping around the
                // security control - it IS the control: the counter it increments is the only thing that ever
                // locks an account, so a failure to increment it means this attempt did not count and no
                // number of further attempts will either. Discarding the answer meant an unreachable
                // membership store looked exactly like a correctly counted wrong password, and an attacker
                // could guess indefinitely while every response looked ordinary.
                EnsureMembershipBookkeepingRan(failureRecorded, account.UserId);

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

        MembershipWriteOutcome successRecorded = await _users
            .RecordSuccessfulLoginAsync(account.UserId, now, cancellationToken)
            .ConfigureAwait(false);

        // Read for the same reason the failure counterpart is. Clearing the counters is the other half of the
        // lock-out control: it is what stops failures accumulated over weeks from eventually locking an account
        // whose owner has been signing in successfully in between. A silent failure here therefore does not
        // merely lose a timestamp - it leaves an account drifting towards a lock it has not earned.
        EnsureMembershipBookkeepingRan(successRecorded, account.UserId);

        if (!await TryReplaceSupersededCredentialAsync(
                account.UserId,
                request.Password,
                storedHash,
                now,
                cancellationToken).ConfigureAwait(false))
        {
            // A cost upgrade that could not be stored, which must not fail a sign-in whose credential was
            // correct - see that method for why - but must not vanish either: an installation whose stored
            // credentials are stuck below the cost it believes it enforces has no other way of finding out.
            _diagnostics.Record(
                SecurityDiagnosticEvent.CredentialWorkFactorUpgradeFailed,
                portalId,
                account.UserId);
        }

        if (approvedByVerification)
        {
            // The one branch of a sign-in that changes a tracked row, and therefore the only one that
            // opens the transaction boundary. It reproduces the account persistence the provider
            // performed immediately after approving a verified registration. Everything else this
            // method writes goes to the external membership store through explicit statements, which is
            // why an ordinary sign-in commits nothing.
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        Result<LoginResponse> issued = await IssueAsync(portal, account, now, cancellationToken)
            .ConfigureAwait(false);

        if (issued.IsFailure)
        {
            // The credential was accepted and the session could not be recorded. The reason travels
            // unchanged, exactly as IAuthService documents, so the caller learns that a dependency is
            // unavailable rather than being told its credential was wrong.
            return issued;
        }

        LoginResponse response = issued.Value;

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
        response.MustUpdateProfile = await MustUpdateProfileAsync(portal.PortalId, account, cancellationToken)
            .ConfigureAwait(false);

        // M-07: the ACCEPTED outcome, recorded after the sign-in has fully succeeded so that no accepted
        // event can describe a sign-in that then failed to issue. The legacy sign-in path recorded no
        // accepted outcome at all - it logged only its two failing ones - so this is the deliberate
        // addition, because a trail holding nothing but refusals cannot answer when an account was last
        // admitted. The two accepted members are distinct events - LOGIN_SUPERUSER and LOGIN_SUCCESS -
        // exactly as the legacy status enumeration distinguished them, so a host sign-in is separable in
        // the trail without reading a property, and both advisory flags travel with the record so that
        // "admitted but must complete a profile" is distinguishable from "admitted cleanly".
        RecordLoginOutcome(
            loginStatus,
            portalId,
            portal.PortalName ?? string.Empty,
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
    /// <para>
    /// <b>The membership gates are re-read too, and they refuse the exchange.</b> Approval and lock-out are
    /// held in the external membership store rather than on the account row, so re-reading the account did
    /// not observe them. An account locked since its token was issued, or whose approval has been withdrawn
    /// since, is refused here and has every refresh token it holds revoked - the refusal is durable rather
    /// than per-request, because an account that may not sign in must not keep the means to keep trying.
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
            // The store outage travels unchanged; every other reason describes the presented token and is
            // collapsed into one, so a caller cannot learn whether a guessed value ever existed.
            return IsTokenStoreFailure(rotated.Reason)
                ? Result<LoginResponse>.Failure(rotated.Reason!)
                : Result<LoginResponse>.Failure(InvalidRefreshTokenCode, "The refresh token is not valid.");
        }

        LoginResponse response = rotated.Value;

        // The identity, tenant and account name on the rotated response come from the token service's own
        // record of the value being exchanged, never from anything the caller supplied - there is no
        // parameter through which they could be supplied, which is the point. They are read back here
        // only to re-read the mutable facts against them.
        int portalId = response.User.PortalId;
        int userId = response.User.UserId;

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
                response.User.Username,
                AccountUnresolvedFailure,
                cancellationToken).ConfigureAwait(false);
        }

        // THE ELIGIBILITY RE-READ. Rotation renews a session, and a session must not outlive the
        // conditions that were required to start it. The sign-in ladder above tests three things before it
        // will accept a credential - the credential exists, the account is not locked, and the account is
        // approved - and until this re-read existed, rotation tested NONE of them. An account locked or
        // disabled after sign-in therefore kept minting access tokens for as long as its family lived,
        // which the absolute ceiling bounds at thirty days: the account was disabled everywhere except in
        // the one place that mattered.
        //
        // The whole point of a short access token is that authority is RE-DECIDED at the exchange, and these
        // were the facts it never re-decided: approval and lock-out live in the external membership store
        // rather than on the account row, so re-reading the account and its roles alone did not see them.
        //
        // The same three questions are asked in the same order and against the same source as gate one and
        // gate two of LoginAsync, including the measured superuser exemption from approval - a host account
        // is created by the installer, so it has no tenant to be approved into
        // (AspNetMembershipProvider.vb:L1465-L1477). The credential VALUE is not re-verified and cannot be:
        // rotation carries no password, which is precisely why the ceiling exists as the point at which a
        // caller must present one again.
        (bool exists, string? storedHash, bool isApproved, bool isLockedOut) = await _users
            .GetCredentialStateAsync(account.UserId, cancellationToken)
            .ConfigureAwait(false);

        string? ineligible = !exists || storedHash is null
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
                account.Username,
                ineligible,
                cancellationToken).ConfigureAwait(false);
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
        response.MustUpdateProfile = await MustUpdateProfileAsync(portalId, account, cancellationToken)
            .ConfigureAwait(false);

        _audit.Record(new AuditEvent(AuditEventNames.SessionRenewed)
        {
            PortalId = portalId,
            ActorUserId = account.UserId,
            ActorUserName = account.Username,
            SubjectUserId = account.UserId,
        });

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

        // Recorded from the ALREADY-AUTHENTICATED caller rather than from the presented token, because
        // this member is deliberately silent about whether the token was genuine and must not learn
        // anything from it that it would then record. An anonymous sign-out records an event with no actor,
        // which is the honest reading: something was presented, nothing is now exchangeable, and who did it
        // is unknown. The event carries no token material of any kind.
        _audit.Record(new AuditEvent(AuditEventNames.SessionEnded)
        {
            PortalId = _currentUser.IsAuthenticated ? _currentUser.PortalId : null,
            ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
            ActorUserName = _currentUser.IsAuthenticated ? _currentUser.UserName : null,
        });

        // A store outage travels unchanged, because a sign-out that silently failed to revoke anything
        // must not be reported as a sign-out. Every other reason means the value was unknown, already
        // redeemed or already revoked, and each of those is a completed sign-out: the token cannot mint a
        // successor, which is the whole of what this member promises.
        return revoked.IsFailure && IsTokenStoreFailure(revoked.Reason)
            ? Result.Failure(revoked.Reason!)
            : Result.Success();
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
    /// <para>
    /// The code and the wording are intentionally identical for every cause, so nothing distinguishes an
    /// unknown account from a rejected credential, a non-member, an account with no credential on file or
    /// a tenant that does not exist. Every one of those is a gate that closes WITHOUT the caller having
    /// proved anything, which is precisely the set that must be indistinguishable.
    /// </para>
    /// <para>
    /// An unapproved registration is deliberately NOT in that set, and its absence is the point rather
    /// than an omission. It is reported specifically, because the gate that produces it now closes only
    /// after a correct credential has been presented; see the call site for the ordering that earns it.
    /// </para>
    /// </remarks>
    private static Result<LoginResponse> Denied()
        => Result<LoginResponse>.Failure(InvalidCredentialsCode, "The account name or credential is not correct.");

    /// <summary>
    /// Records one sign-in outcome on the audit trail.
    /// </summary>
    /// <param name="outcome">The outcome the attempt produced.</param>
    /// <param name="portalId">The tenant the credential was presented to.</param>
    /// <param name="portalName">The tenant's name, or an empty string when the tenant does not exist.</param>
    /// <param name="username">The account name that was submitted, exactly as submitted.</param>
    /// <param name="userId">The account the attempt resolved to, or <see langword="null"/> for none.</param>
    /// <remarks>
    /// <para>
    /// MIGRATION: the audited outcome name is <c>outcome.ToString()</c>, which is precisely what the
    /// legacy wrote as its log type key (<c>UserController.vb</c> L80). The member name is carried rather
    /// than its number, so the trail stays readable and stays stable across a reordering of the
    /// enumeration.
    /// </para>
    /// <para>
    /// The credential is not a parameter and could not be one: the audit contract declares no member that
    /// could carry a password, a hash or a verification code. The submitted account NAME is carried,
    /// unfiltered, because the value actually presented is the value an investigator needs - and it is
    /// safe to carry unfiltered because the implementation emits it as a structured property rather than
    /// interpolating it into a message, so it can never become part of a template. That replaces the
    /// legacy's input filtering, which existed only because its record was later rendered.
    /// </para>
    /// </remarks>
    private void RecordSignInOutcome(
        UserLoginStatus outcome,
        int portalId,
        string portalName,
        string username,
        int? userId)
    {
        Dictionary<string, string?> properties = new(StringComparer.Ordinal)
        {
            ["Username"] = username,
        };

        if (!string.IsNullOrEmpty(portalName))
        {
            properties["PortalName"] = portalName;
        }

        bool accepted = outcome is UserLoginStatus.Success or UserLoginStatus.SuperUser;

        _audit.Record(new AuditEvent(AuditEventNameFor(outcome))
        {
            Outcome = accepted ? AuditOutcome.Succeeded : AuditOutcome.Denied,
            PortalId = portalId,
            ActorUserId = userId,
            ActorUserName = username,
            SubjectUserId = userId,
            FailureCode = accepted ? null : AuditFailureCodeFor(outcome),
            Properties = properties,
        });
    }

    /// <summary>
    /// Determines which of the three legacy approval outcomes the submission produced.
    /// </summary>
    /// <param name="portal">The tenant the sign-in addresses.</param>
    /// <param name="verificationCode">The verification code the submission carried, if any.</param>
    /// <returns>
    /// The reason carrying <see cref="VerificationRequiredCode"/>,
    /// <see cref="VerificationCodeInvalidCode"/> or <see cref="AccountNotApprovedCode"/>, preserving the
    /// legacy message keys <c>EnterCode</c>, <c>InvalidCode</c> and <c>UserNotAuthorized</c>
    /// respectively.
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
    /// The answer reaches the caller, and it is safe to because of WHERE this method is called from:
    /// the sole call site sits behind an accepted credential, so the reader of these three sentences has
    /// already proved it holds the account's password. Nothing here may be reported from a gate that
    /// closes ahead of that proof, which is why this method takes no part in the uniform denial.
    /// </para>
    /// <para>
    /// The wording is authored here rather than at the call site so that the code and the sentence
    /// explaining it cannot drift apart. None of the three quotes any value the caller supplied: a
    /// submitted verification code is echoed nowhere, because echoing a rejected input is how an error
    /// message becomes a reflection vector.
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
    /// re-authored at the presentation layer. It names no account, no identifier and no submitted value:
    /// the caller already knows which account and which code it presented, so repeating either would add
    /// nothing but a logging hazard.
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

    /// <summary>
    /// Reports whether the submitted credential is one of the supplied shipped defaults.
    /// </summary>
    /// <param name="password">The submitted credential.</param>
    /// <param name="fingerprints">Lower-case hexadecimal SHA-256 digests of the shipped defaults.</param>
    /// <returns><see langword="true"/> when the credential fingerprints to one of them.</returns>
    /// <remarks>
    /// <para>
    /// C-04: the comparison the legacy made against two plaintext literals, made against their digests
    /// instead. It recognises exactly the same two values - the encoding is settled to UTF-8 explicitly so the
    /// digest cannot vary with an ambient default - and the comparison is case-SENSITIVE on the credential,
    /// as the legacy's <c>=</c> under binary comparison was, because a digest of a differently-cased value
    /// simply does not match.
    /// </para>
    /// <para>
    /// The computed digest is compared with an ordinal comparison rather than a fixed-time one, and that is
    /// correct here rather than a lapse: both operands are already public. The digests are constants in this
    /// file, and the credential is one the submitter just supplied, so there is no secret whose length or
    /// content a timing difference could leak. A fixed-time comparison would protect nothing and would
    /// suggest a secret exists where none does.
    /// </para>
    /// <para>
    /// Neither the submitted credential nor any digest of it is retained, logged or returned. The only thing
    /// that leaves this method is a boolean.
    /// </para>
    /// </remarks>
    private static bool IsShippedCredential(string password, string[] fingerprints)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        string fingerprint = Convert.ToHexString(digest).ToLowerInvariant();

        return fingerprints.Contains(fingerprint, StringComparer.Ordinal);
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
    /// <returns>
    /// A successful outcome carrying the issued pair and a freshly read snapshot of the caller, or the
    /// token service's own failure reason propagated unchanged.
    /// </returns>
    /// <remarks>
    /// MIGRATION: this helper returns a typed outcome rather than throwing. The token service already
    /// reports its own failure as a reason, and <see cref="IAuthService"/> documents
    /// <c>TOKEN_STORE_UNAVAILABLE</c> as propagating unchanged when a credential was accepted and the
    /// session could not be recorded; converting it into an exception discarded a classified outcome and
    /// produced an unclassifiable 500, so the caller could not tell a dependency outage from a defect. The
    /// reason travels verbatim, including any future reason this service does not know about, which is the
    /// only arrangement under which the token service can add one without this file being changed.
    /// </remarks>
    private async Task<Result<LoginResponse>> IssueAsync(
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
            return Result<LoginResponse>.Failure(issued.Reason!);
        }

        LoginResponse response = issued.Value;
        response.User = snapshot;

        return Result<LoginResponse>.Success(response);
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
    /// the client offers less than it might, and the API refuses anything it should not allow. That
    /// substitution is NOT silent: the failure is recorded through the Domain diagnostics contract, because an
    /// empty set is otherwise indistinguishable from a caller who genuinely holds nothing, and a fault that
    /// looks like a legitimate result is a fault nobody investigates.
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

        if (permissions.IsFailure)
        {
            // AN EMPTY SET IS STILL THE RIGHT ANSWER, AND IT IS NO LONGER AN UNRECORDED ONE. Refusing a sign-in
            // whose credential has already been accepted would be the worse outcome, because these keys only
            // tell a client which affordances to render and never stand in for the server-side policy, which
            // re-evaluates on every request. But an empty set is indistinguishable from a caller who genuinely
            // holds nothing - so the previous revision, which substituted the empty list and kept no trace of
            // why, turned a dependency failure into a plausible-looking result that nobody could ever notice.
            // A caller whose console renders as though they hold no permissions, with nothing anywhere saying
            // why, is a support incident with no evidence.
            //
            // Nothing reaches this point on an expected absence: no module and no page are named, and the
            // tenant was loaded moments ago, so every documented failure of that contract is a genuine
            // dependency or consistency fault. Only the failure CODE travels - a short stable token, never the
            // message - and the contract that receives it discards anything not code-shaped.
            _diagnostics.Record(
                SecurityDiagnosticEvent.EffectivePermissionResolutionFailed,
                portal.PortalId,
                account.UserId,
                permissions.Reason?.Code);
        }

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
    /// C-03: the profile advisory is decided by <see cref="MustUpdateProfileAsync"/> rather than here,
    /// because this method answers only the CREDENTIAL question and the legacy separated the two the same
    /// way - the credential arms of <c>UserController.vb</c> L1175-L1188 and the profile arm at
    /// L1189-L1193 were consecutive but independent. An earlier revision reported the profile arm as an
    /// unevaluated gap on the grounds that its setting and its definitions belong to the
    /// account-administration vertical. That observation was correct and still holds; the conclusion drawn
    /// from it was not. The rule stays in the owning vertical, which now exposes the QUESTION through
    /// <c>IUserService.RequiresProfileCompletionAsync</c>, so the gate is evaluated on both the sign-in and
    /// the refresh path while the completeness rule still has exactly one implementation. The two extra
    /// reads land on an ALREADY-AUTHENTICATED path, after the credential has been accepted, never on the
    /// anonymous one.
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

        int expiryDays = await ReadHostSettingIntegerAsync(PasswordExpiryHostSettingName, 0, cancellationToken)
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
    /// <see langword="true"/> when the tenant requires a valid profile at sign-in and the account leaves a
    /// required property empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// C-03: reproduces <c>UserController.vb</c> L1189-L1193 by ASKING the vertical that owns the rule
    /// rather than by re-implementing it. The two halves of the legacy condition - the per-tenant
    /// <c>Security_RequireValidProfileAtLogin</c> setting and the completeness walk at
    /// <c>ProfileController.vb</c> L305-L319 - both live behind
    /// <c>IUserService.RequiresProfileCompletionAsync</c>.
    /// </para>
    /// <para>
    /// MIGRATION: the gate is SKIPPED FOR A HOST ACCOUNT, for the same measured reason the credential
    /// advisories are - <c>Website/admin/Authentication/Login.ascx.vb</c> L511 wraps the whole
    /// post-credential validation in <c>If Not objUser.IsSuperUser Then</c>, so a superuser never reached
    /// the profile arm either. Applying it to a host account would be able to lock an installation out of
    /// the only account that can administer it, over reference data that account may not even have a
    /// profile row for.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy status enumeration was SINGLE-VALUED and tested the profile arm last, only
    /// while no credential advisory had been raised, so it could never report both at once. This contract
    /// can, and does: the flag is set from its own condition regardless of the credential advisories. That
    /// widening is already recorded on <c>LoginResponse</c> and is deliberate - suppressing a true profile
    /// requirement because a credential advisory happened to fire first would hide a blocking condition
    /// behind a non-blocking one.
    /// </para>
    /// <para>
    /// A failed question is answered <see langword="false"/> rather than propagated. The member declares no
    /// failure code, so a failure here can only be an unexpected one, and refusing an otherwise valid
    /// sign-in because a profile advisory could not be computed would convert an advisory into an outage.
    /// </para>
    /// </remarks>
    private async Task<bool> MustUpdateProfileAsync(
        int portalId,
        User account,
        CancellationToken cancellationToken)
    {
        if (account.IsSuperUser)
        {
            return false;
        }

        Result<bool> outcome = await _accounts
            .RequiresProfileCompletionAsync(portalId, account.UserId, cancellationToken)
            .ConfigureAwait(false);

        return outcome.IsSuccess && outcome.Value;
    }

    /// <summary>
    /// Reads an installation-wide setting expressed as a whole number.
    /// </summary>
    /// <param name="settingName">The setting name, spelled exactly as the legacy wrote it.</param>
    /// <param name="fallback">The value to apply when the setting is absent or unusable.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The configured value, or <paramref name="fallback"/>.</returns>
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

    /// <summary>
    /// Clears a lock that has aged past the installation's automatic-unlock window.
    /// </summary>
    /// <param name="account">The locked account, carrying the instant it was locked.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// <see langword="true"/> when the lock was cleared and the sign-in may continue;
    /// <see langword="false"/> when the lock stands.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: reproduces <c>AspNetMembershipProvider.vb</c> L64-L86 branch for branch, called from
    /// L1453-L1463. The legacy read <c>HostSettings("AutoAccountUnlockDuration")</c> into an integer
    /// initialised to its absent-integer sentinel, so an absent or blank setting left the sentinel in
    /// place; it then treated an explicit ZERO as "never unlock automatically" and the sentinel as "use
    /// ten minutes"; and finally it unlocked when
    /// <c>LastLockoutDate &lt; Date.Now.AddMinutes(-1 * timeout)</c>. All three behaviours are preserved,
    /// with the sentinel expressed as a fallback argument rather than as -1 (Rule T7) - and note that the
    /// two legacy states collapse onto one here only because both produce the same ten-minute window,
    /// while the zero case remains distinct and is tested first.
    /// </para>
    /// <para>
    /// A negative configured duration is treated as the disabling zero rather than as an instantly
    /// elapsed window. The legacy comparison would have unlocked immediately on a negative value, which
    /// is the one reading of a mistyped configuration row that silently removes the lock-out control
    /// altogether; refusing it is a narrowing, is recorded in MIGRATION_NOTES.md, and cannot lock anybody
    /// out who was not already locked out.
    /// </para>
    /// <para>
    /// The lock instant comes from the account the repository already populated from the external
    /// membership store, so this method costs one setting read and, at most, one write - no additional
    /// account read. An account whose lock instant is unknown is NOT unlocked: with no instant there is no
    /// window to measure, and unlocking on an unknown would turn a missing fact into an open door. The
    /// comparison is in coordinated universal time on both sides where the legacy used the server's local
    /// clock; the window is a duration rather than a calendar boundary, so the offset cannot change the
    /// outcome.
    /// </para>
    /// <para>
    /// Nothing about the lock, the window or the outcome reaches the caller. A failed unlock simply leaves
    /// the lock standing and the sign-in is refused by the arm that owns that decision, which withholds
    /// the detail from anyone not already entitled to it.
    /// </para>
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
            // Kept in step with the store so that nothing downstream in this request - the caller
            // snapshot in particular - describes the account as locked after it has been cleared. The
            // property is excluded from the entity configuration, so this assignment cannot mark the
            // entity modified and cannot provoke an update.
            account.IsLockedOut = false;
        }

        return unlocked;
    }

    /// <summary>
    /// Replaces a stored credential representation that the hashing abstraction reports as superseded.
    /// </summary>
    /// <param name="userId">The account whose stored representation is replaced.</param>
    /// <param name="password">The credential just accepted, which the replacement is computed from.</param>
    /// <param name="storedHash">The stored representation that was just verified against.</param>
    /// <param name="asOfUtc">The instant the replacement is stamped with.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// <see langword="true"/> when nothing needed replacing or the replacement was stored;
    /// <see langword="false"/> when a replacement was due and could not be stored.
    /// </returns>
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
    /// </para>
    /// <para>
    /// CONTAINED IS NOT UNRECORDED. An earlier revision swallowed the failure in silence and reported the
    /// inability to record it as an unclosable gap, on the grounds that no logger is resolvable in this
    /// project. The consequence was that an installation could keep verifying credentials at a superseded
    /// work factor indefinitely with no signal of any kind - the one failure mode where silence is
    /// indistinguishable from success, because the sign-in works perfectly either way. The containment now
    /// emits a security event through <see cref="IAuditSink"/>, which takes no package dependency and is
    /// therefore available here. The event carries the account identifier and the exception TYPE NAME and
    /// nothing else: never the submitted password, never the stored hash, and not the exception message
    /// either, since a store failure's message can quote the value it failed to write.
    /// </para>
    /// <para>
    /// THE OUTCOME IS REPORTED RATHER THAN ABSORBED, which is the difference from an earlier revision. That
    /// revision discarded the store's own return value AND caught every non-cancellation exception into an
    /// empty handler, so a cost upgrade that never happened was indistinguishable from one that did. The
    /// consequence is quiet and long-lived: an installation raises its work factor, believes every credential
    /// re-hashes on next sign-in, and none of them does - with no error, no log line and no failing request to
    /// suggest otherwise. Containing the failure is still correct; being unable to observe it is not, so the
    /// two are now separated. This method still never throws for a store failure, and its caller records the
    /// occurrence through the Domain diagnostics contract.
    /// </para>
    /// <para>
    /// NOTHING ABOUT THE CREDENTIAL IS CARRIED OUT OF HERE. The return value is a single boolean and the
    /// diagnostic the caller records carries a tenant, an account and nothing else: no password, no
    /// representation, no exception message. The exception is deliberately not returned or rethrown for that
    /// reason as much as for flow control - a provider exception can carry statement text and connection
    /// detail, and this is the one path on a successful sign-in where such a value could otherwise escape.
    /// </para>
    /// </remarks>
    private async Task<bool> TryReplaceSupersededCredentialAsync(
        int userId,
        string password,
        string storedHash,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        if (!_passwordHasher.NeedsRehash(storedHash))
        {
            // Nothing was due, so nothing failed. Reported as success rather than as a distinct third state:
            // the caller's only question is whether the stored representation is now at the current cost, and
            // it is.
            return true;
        }

        try
        {
            // The store's own answer is part of the outcome. It reports false when no credential record was
            // updated, which is a silent no-op rather than an exception - so a method that only guarded against
            // exceptions would have called that a successful upgrade.
            return await _users
                .SetPasswordHashAsync(userId, _passwordHasher.Hash(password), asOfUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Contained on purpose: the only correct response to a failed cost upgrade is to leave the
            // working credential alone and proceed with a sign-in that was already valid. The condition
            // above re-raises cancellation, because a cancelled request is not a failed write.
            //
            // Recorded at Failed outcome, which the sink raises to warning level, so an operator has the
            // signal the silence used to withhold. Only the exception TYPE is carried - a message may
            // quote the value that could not be written.
            _audit.Record(new AuditEvent(AuditEventNames.PasswordRehashFailure)
            {
                Outcome = AuditOutcome.Failed,
                SubjectUserId = userId,
                ResourceType = CredentialResourceType,
                ResourceId = userId.ToString(CultureInfo.InvariantCulture),
                FailureCode = exception.GetType().Name,
            });
            // Contained on purpose: the only correct response to a failed cost upgrade is to leave the working
            // credential alone and proceed with a sign-in that was already valid. What has changed is that the
            // containment is no longer silent - the caller is told, and records it. The exception itself is
            // deliberately not propagated in any form, including into the diagnostic. The condition above
            // re-raises cancellation, because a cancelled request is not a failed write.
            return false;
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
    /// <para>
    /// AN UNREACHABLE STORE IS A SERVER FAULT, NOT AN AUTHENTICATION OUTCOME, and is raised rather than
    /// returned. This is the same ruling, for the same reason, as the token-store escalation below: the
    /// condition describes neither the caller nor the credential, so reporting it as a refusal would tell a
    /// caller its credential was wrong when nothing was counted, and - on the success path - would tell a
    /// caller whose credential had just been accepted that it was not. Raising it produces a generic
    /// problem document from the Api layer's handler, which discloses nothing about the account while making
    /// the outage impossible to miss.
    /// </para>
    /// <para>
    /// FAILING CLOSED IS THE POINT, INCLUDING ON THE SUCCESS PATH. It may look severe to refuse a sign-in whose
    /// credential was correct because a counter reset could not be written, but the alternative is worse: the
    /// store answered a credential read moments earlier in the same request, so an outage here means it became
    /// unreachable mid-request, and continuing would issue tokens while the lock-out control is known to be
    /// down. A brief refusal that an operator can see beats an indefinite window in which credential guessing
    /// is unbounded.
    /// </para>
    /// <para>
    /// AN ABSENT RECORD IS NOT ESCALATED, because the control ran. On this path the record was read
    /// successfully a moment earlier, so its absence now means it was removed in between - a race, and one
    /// whose only correct handling is to proceed. It is recorded rather than ignored, because a race that
    /// recurs is not a race.
    /// </para>
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
                // Both recorded outcomes are the control working. Which of the two it is belongs to the caller
                // that asked, and neither is reported to an unauthenticated caller: the account's refusal is
                // uniform whether or not this attempt was the one that locked it.
                break;
        }
    }

    /// <summary>
    /// Refuses a rotation and ends the whole refresh family behind it.
    /// </summary>
    /// <param name="userId">The account the presented token belonged to.</param>
    /// <param name="portalId">The tenant the presented token named.</param>
    /// <param name="userName">The account name recorded on the presented token, for the trail.</param>
    /// <param name="failureCode">Which eligibility question closed, recorded but never disclosed.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>The single uniform refusal this contract reports for every rejected token.</returns>
    /// <remarks>
    /// <para>
    /// REVOKING THE FAMILY IS THE OPERATIVE HALF, not the refusal. Rotation has already happened by the
    /// time eligibility can be re-read - the store maps an opaque token to its subject and nothing above
    /// it can, so the exchange must complete before there is an account to ask about - and that exchange
    /// mints a successor. Returning a failure alone would leave that successor sitting in the store,
    /// unknown to the caller but alive, and would leave the caller free to keep presenting whatever else
    /// its family holds. Revoking the family closes both: nothing belonging to this account remains
    /// exchangeable, so the next attempt is refused at the store rather than by this re-read, and the
    /// holder of a disabled account must authenticate again - which is exactly the point at which a
    /// credential, a lock and an approval are examined properly.
    /// </para>
    /// <para>
    /// The reason is recorded and never returned. Answering "your account is locked" to a caller holding
    /// only a token would confirm that the token was genuine and name the account's state, so the caller
    /// receives the same uniform refusal as an unknown, replayed or expired value. An operator reading the
    /// trail sees which question closed.
    /// </para>
    /// <para>
    /// A revocation that cannot be persisted is escalated rather than absorbed. A refusal that failed to
    /// end the family would leave exactly the hole this method exists to close, and reporting a server
    /// fault as a rejected token would be a lie about which of the two went wrong.
    /// </para>
    /// </remarks>
    private async Task<Result<LoginResponse>> RefuseRotationAsync(
        int userId,
        int portalId,
        string? userName,
        string failureCode,
        CancellationToken cancellationToken)
    {
        Result revoked = await _tokens
            .RevokeAllRefreshTokensAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        // A revocation that could not be persisted is escalated rather than absorbed: reporting a store
        // outage as a rejected token would leave the family exchangeable while telling the caller its
        // token was the problem.
        if (revoked.IsFailure && IsTokenStoreFailure(revoked.Reason))
        {
            return Result<LoginResponse>.Failure(revoked.Reason!);
        }

        _audit.Record(new AuditEvent(AuditEventNames.SessionRefused)
        {
            Outcome = AuditOutcome.Denied,
            PortalId = portalId,
            ActorUserId = userId,
            ActorUserName = userName,
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
    /// <param name="portalName">The tenant's name, carried so accepted and refused records read alike.</param>
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
    /// <remarks>
    /// The event name comes from <see cref="AuditEventNames"/> rather than from
    /// <c>loginStatus.ToString()</c>, which is how the legacy produced it (<c>UserController.vb</c> L80).
    /// The names are identical - that is the requirement - but deriving them from the enumeration would
    /// make every member of a Domain enumeration part of an observable contract, so that renaming a member
    /// silently renamed an audit event. Naming them explicitly is what lets the tests assert the literal.
    /// </remarks>
    private void RecordLoginOutcome(
        UserLoginStatus loginStatus,
        int portalId,
        string portalName,
        User account,
        AuditOutcome outcome,
        string? advisoryCode = null,
        bool? mustChangePassword = null,
        bool? mustUpdateProfile = null)
    {
        Dictionary<string, string?> properties = new(StringComparer.Ordinal)
        {
            ["Username"] = account.Username,
        };

        // M-07: the two advisories are recorded as properties rather than as separate events, because they
        // qualify an outcome that has already been decided rather than being outcomes of their own. They
        // are omitted altogether on a refusal, where an absent property says "not applicable" and a
        // literal "false" would say "asked and answered no".
        if (mustChangePassword is bool changeRequired)
        {
            properties["MustChangePassword"] = changeRequired ? "true" : "false";
        }

        if (mustUpdateProfile is bool profileRequired)
        {
            properties["MustUpdateProfile"] = profileRequired ? "true" : "false";
        }

        if (!string.IsNullOrEmpty(portalName))
        {
            properties["PortalName"] = portalName;
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
            ActorUserName = account.Username,
            SubjectUserId = account.UserId,
            FailureCode = outcome == AuditOutcome.Succeeded ? null : AuditFailureCodeFor(loginStatus),
            Properties = properties,
        });
    }

    /// <summary>
    /// Maps a sign-in outcome onto the stable legacy event name.
    /// </summary>
    /// <param name="loginStatus">The outcome.</param>
    /// <returns>The event name.</returns>
    /// <remarks>
    /// The four members the legacy status enumeration published for a sign-in map one to one; anything
    /// else is recorded as a plain failure, which is what the legacy inequality at
    /// <c>Login.ascx.vb</c> L187 collapsed every unnamed member into.
    /// </remarks>
    private static string AuditEventNameFor(UserLoginStatus loginStatus) => loginStatus switch
    {
        UserLoginStatus.SuperUser => AuditEventNames.LoginSuperUser,
        UserLoginStatus.Success => AuditEventNames.LoginSuccess,
        UserLoginStatus.UserLockedOut => AuditEventNames.LoginUserLockedOut,
        UserLoginStatus.UserNotApproved => AuditEventNames.LoginUserNotApproved,
        _ => AuditEventNames.LoginFailure,
    };

    /// <summary>
    /// Names which eligibility question closed, for the trail only.
    /// </summary>
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
    /// <see langword="true"/> when the reason is the documented store outage; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every other reason the token service reports describes the presented token and is collapsed into
    /// this service's single token failure. An unavailable store describes neither the caller nor the
    /// token, so reporting it as a denial would tell a caller its token was invalid when it was not, and
    /// would tell a caller whose credential had just been accepted that the credential was wrong.
    /// </para>
    /// <para>
    /// MIGRATION: an earlier revision converted this reason into an <c>InvalidOperationException</c>. That
    /// threw away a typed outcome the token service had gone to the trouble of producing and that
    /// <see cref="IAuthService"/> documents as propagating unchanged: the exception became an
    /// unclassifiable 500, so a caller could not distinguish a dependency outage from a defect, and
    /// sign-out stopped being idempotent during an outage because a client trying to surrender a token
    /// received a fault instead of a completion. Returning the reason unchanged also renders correctly
    /// without any Api-layer special case - the status mapper reduces the code to its reason token,
    /// recognises the store-outage marker in it, and answers <c>503 Service Unavailable</c>.
    /// </para>
    /// </remarks>
    private static bool IsTokenStoreFailure(ResultReason? reason)
        => reason is ResultReason failure
            && string.Equals(failure.Code, TokenStoreUnavailableCode, StringComparison.Ordinal);
}
