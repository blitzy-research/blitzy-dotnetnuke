using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

// ---------------------------------------------------------------------------
// Rule T5 annotations for the shape of this contract.
//
// The legacy sign-in surface diverges from its replacement in more places than
// any other part of this migration, so every difference is recorded rather than
// left to be inferred from the absence of a member. Each entry names the legacy
// file and line it was measured from. The behavioural consequences of each are
// documented on the member that carries them and in MIGRATION_NOTES.md.
// ---------------------------------------------------------------------------

// MIGRATION: the by-reference loginStatus argument is gone. UserController.ValidateUser at
// UserController.vb:L1132, called from Login.ascx.vb:L164, took eight positional arguments and
// reported its verdict by mutating a status variable the caller had declared. That collapses into
// the single sign-in member below, in the canonical shape for this migration: the mutated object
// becomes the result value and the status enum becomes the reason code. The same treatment retires
// the two other by-reference sites, UserLogin at UserController.vb:L991 and the seven-argument
// ValidateUser at L1110. No member of this contract has a by-reference or output argument.

// MIGRATION: every legacy member behind this contract was declared Shared -- the whole of
// UserController is static, as are the ValidateUser and UserLogin overloads above. All of them
// become instance members resolved from the container, so a test can substitute the store, the
// credential comparison and the clock instead of reaching a real database.

// MIGRATION: the fixed authentication-mechanism argument is dropped. Login.ascx.vb passed the
// MIGRATION: literal "DNN" twice -- at L164 as the fourth argument, and again at L191 when it built
// MIGRATION: the arguments of the authenticated-event it raised -- because the legacy platform
// multiplexed several pluggable sign-in mechanisms behind one screen. The target has exactly one
// path, so such a discriminator could hold only a single value. No selector is accepted from the
// caller, none is inferred, and there is no external-provider member.

// MIGRATION: the sign-in ticket is not reproduced. SetAuthCookie at UserController.vb:L919 issued
// MIGRATION: the Forms-authentication ticket, and the CreatePersistentCookie argument at
// MIGRATION: UserController.vb:L991 and L1024 governed its lifetime. A stateless bearer token has no
// such ticket: the short-lived access token replaces the sustained session and the refresh token
// replaces its persistence, both minted by the token contract. This surface therefore names no
// browser state at all and carries no persistence flag.

// MIGRATION: the credential store changes from reversible to one-way, and that is a security fix
// rather than a refactor. The original schema held the value in clear text -- Users.Password is
// declared nvarchar(20) NOT NULL at 01.00.00.SqlDataProvider:L97-L110 -- and the membership
// provider was later configured with passwordFormat="Encrypted" and enablePasswordRetrieval="true"
// at Website/release.config:L236-L246, decryptable with a key committed to source control at
// Website/release.config:L89-L93. The replacement is a one-way hash behind the domain hashing
// abstraction. That legacy file is evidence and is deliberately left byte-identical.

// MIGRATION: credential retrieval is not carried forward at all. GetPassword at
// UserController.vb:L433 returned the caller's own credential in clear text, and the recovery
// MIGRATION: screen invoked it at SendPassword.ascx.vb:L198-L200 whenever retrieval was enabled. No
// member of this contract returns, echoes, reconstructs or logs a credential, under any
// configuration. A one-way hash makes recovery impossible by construction, which is the point of
// adopting one.

// MIGRATION: the credential question-and-answer pair is omitted. The recovery screen required it at
// MIGRATION: SendPassword.ascx.vb:L167, and ChangePasswordQuestionAndAnswer at
// MIGRATION: UserController.vb:L139 maintained it, but the shipped policy required no such pair and
// its only real use was to authorise the retrieval that is no longer offered. No member accepts an
// answer argument.

// MIGRATION: no credential-recovery member is declared, and the omission is deliberate. The legacy
// MIGRATION: screen's outcome was an electronic message sent at SendPassword.ascx.vb:L211, and the
// mail subsystem it used is excluded from this migration, so nothing could be delivered. A member that
// must not disclose whether an account exists, cannot transmit a credential and cannot send a
// notification would report success unconditionally while doing nothing, which is worse than its
// absence because it reads as a working feature. Bounded first-login migration and administrative
// reset are the supported credential-transition paths; neither is password recovery or readback.

// MIGRATION: the authenticated-event model is not reproduced. UserUserControlBase.vb:L59-L65
// declared seven user-lifecycle events, and the sign-in screen raised one at Login.ascx.vb:L193 by
// MIGRATION: constructing UserAuthenticatedEventArgs and calling OnUserAuthenticated. No server-side
// publisher is introduced: the client observes its own sign-in directly, and inventing one here
// would be scope creep dressed as fidelity.

// MIGRATION: the legacy null sentinels are not carried onto this surface. Null.vb:L38-L90 mapped
// every primitive to a stand-in value, and the sign-in screen used two of them directly at
// Login.ascx.vb:L165-L166; the integer stand-in was minus one and the string stand-in was the empty
// string rather than a null reference. Neither can be reused as "absent" here, because minus one is
// a real tenant: Portals.PortalID is declared IDENTITY(-1, 1) at 01.00.00.SqlDataProvider:L77, so
// the first tenant created is zero and minus one is the one before it, while Users.UserID is
// declared IDENTITY(1, 1) at L98. Absence is therefore expressed by a nullable type throughout,
// and no numeric value is overloaded to mean it.

/// <summary>
/// The authentication contract: it decides whether a submitted credential is accepted, mints and
/// rotates the caller's session, retires a session on request, and describes the authenticated
/// caller to itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this contract owns, and what it does not.</b> It owns the sign-in <em>use case</em>. The
/// token <em>mechanism</em> - minting, rotation, validation and revocation - belongs to
/// <see cref="ITokenService"/>, which this contract delegates to; no member here restates one of
/// its operations, because a second declaration of the same operation is a second place for that
/// operation's contract to drift. The caller's asserted identity is read from
/// <see cref="ICurrentUser"/>, which is injected rather than passed. Account creation, profile
/// maintenance and the administrative credential reset belong to <see cref="IUserService"/>.
/// Nothing here creates, updates or deletes an account, grants a role, or issues a token directly.
/// </para>
/// <para>
/// <b>Outcome model.</b> Every member returns a result. An <em>expected</em> refusal - and a wrong
/// credential is the most expected refusal in the system - is a failed result carrying a stable
/// code, never an exception: throwing would surface a rejected sign-in as a server fault instead of
/// an authentication failure. An <em>unexpected</em> fault remains an exception and is shaped at
/// the edge by the global handler. A success may itself carry an informational reason, which is how
/// an accepted-but-insecure credential is reported without refusing it.
/// </para>
/// <para>
/// <b>One answer for every rejected credential.</b> An unknown account name, a wrong credential, an
/// account belonging to another tenant and a tenant that does not exist are all reported
/// identically, with code <c>auth.invalid_credentials</c> and identical wording. Any difference
/// between them turns the sign-in member into an oracle for account names or for the installation's
/// tenants. The sign-in request validator states the same rule for its own messages and the two
/// must not disagree.
/// </para>
/// <para>
/// <b>Refusal codes.</b> The vocabulary an implementation may raise:
/// </para>
/// <list type="bullet">
///   <item>
///     <term><c>auth.request_invalid</c></term>
///     <description>
///     The submission is not usable at all - no account name, no credential, or no tenant resolved
///     for it to be presented to. It reports the shape of the request, never the state of an
///     account.
///     </description>
///   </item>
///   <item>
///     <term><c>auth.invalid_credentials</c></term>
///     <description>The uniform denial described above.</description>
///   </item>
///   <item>
///     <term><c>auth.locked_out</c></term>
///     <description>
///     The account is locked and its automatic-unlock window has not elapsed. Raised only for a
///     caller already entitled to that detail; every other caller receives the uniform denial,
///     because "this account exists and is locked" is itself a disclosure.
///     </description>
///   </item>
///   <item>
///     <term><c>auth.invalid_refresh_token</c></term>
///     <description>
///     The presented refresh token is unknown, expired, already redeemed or revoked. The four are
///     deliberately indistinguishable, so a guessed value cannot be confirmed to have once existed.
///     </description>
///   </item>
///   <item>
///     <term><c>auth.user_not_found</c></term>
///     <description>
///     Reserved for the caller-description member, where it is not raised in place of the uniform
///     denial and discloses nothing an authenticated caller does not already know about itself.
///     </description>
///   </item>
///   <item>
///     <term><c>auth.verification_required</c>, <c>auth.verification_code_invalid</c>,
///     <c>auth.account_not_approved</c></term>
///     <description>
///     The three account-approval outcomes, preserving the legacy message keys
///     <c>EnterCode</c>, <c>InvalidCode</c> and <c>UserNotAuthorized</c> in that order. Each is
///     reported to the caller, which is safe only because the approval gate is required to close
///     <em>after</em> the credential has been proved: every caller who receives one of these has
///     presented the account's correct credential, so none of the three tells an unauthenticated
///     caller that an account exists. An implementation that evaluated approval ahead of the
///     credential would have to answer all three with the uniform denial instead.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Advisory codes on a successful result.</b> <c>auth.insecure_admin_password</c> and
/// <c>auth.insecure_host_password</c> report that the credential was correct but is one of the
/// values the platform shipped with. They accompany a <em>successful</em> result and never refuse
/// the sign-in: refusing would lock an installation out of the two accounts every installation
/// begins with.
/// </para>
/// <para>
/// <b>Two code styles appear here, and that is intentional.</b> This contract's own codes are the
/// lower-dotted <c>auth.</c> family, matching the domain-administration contracts in this folder.
/// The single code propagated unchanged from <see cref="ITokenService"/> keeps that contract's
/// upper-snake form, so a caller can tell "your credential was refused" from "your credential was
/// accepted and the session could not be recorded" without either contract re-coding the other's
/// failure.
/// </para>
/// <para>
/// <b>Nothing on this surface may reach a log.</b> The submitted credential exists in exactly one
/// place - a property of <see cref="LoginRequest"/> - and it is never a return value, never part of
/// a reason message, and never written to a log sink, an audit record, a trace, a metric or an
/// exception message. The same holds for the verification code and for every token value. What may
/// be recorded is the tenant, the account identifier where one was resolved, and the outcome code.
/// </para>
/// <para>
/// <b>Layering.</b> This contract names no store, no hashing algorithm, no token library, no
/// serialisation format and no web-framework type. Accounts, roles and tenants are reached through
/// the domain repository abstractions; the credential is compared through the domain hashing
/// abstraction; the current instant is read through the domain clock; the session is minted through
/// <see cref="ITokenService"/>. It follows that an implementation needs no reference to a
/// persistence or authentication library, and reaching for one is the clearest sign that a
/// transport or token type has leaked onto this surface.
/// </para>
/// <para>
/// <b>Implementer's checklist.</b> Compare the credential through the domain hashing abstraction,
/// never by string equality, and never with an early return that shortens the comparison for an
/// unknown account. Read the current instant from the injected clock. Honour the cancellation token
/// on every store round trip. Keep no per-caller state in a field - the implementation is registered
/// with a scoped lifetime and is shared for the duration of one request only. Emit the outcome code
/// and nothing sensitive. Return a failed result for every expected refusal.
/// </para>
/// </remarks>
public interface IAuthService
{
    /// <summary>
    /// Verifies a submitted credential against one tenant's accounts and, when it is accepted,
    /// mints the caller's session.
    /// </summary>
    /// <param name="request">
    /// The submission: the account name, the credential, the tenant it is being presented to, and
    /// the optional account-verification code. Its shape has already been checked by
    /// <c>LoginRequestValidator</c>; this member checks its truth. The tenant identifier on it is
    /// populated by the api layer from the alias-resolved request context and cannot be bound from
    /// the request body, so a caller cannot name a tenant it is not addressing. Implementations must
    /// refuse a submission whose tenant is absent rather than defaulting one, because zero and minus
    /// one are both real tenants.
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed while accounts, roles and the session store are read and written.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the token pair and the
    /// caller's own description - optionally with the <c>auth.insecure_admin_password</c> or
    /// <c>auth.insecure_host_password</c> advisory attached - or a failed result carrying
    /// <c>auth.request_invalid</c> when the submission is unusable, <c>auth.invalid_credentials</c>
    /// for every rejected credential, <c>auth.locked_out</c> for a locked account when the caller is
    /// entitled to that detail, one of <c>auth.verification_required</c>,
    /// <c>auth.verification_code_invalid</c> or <c>auth.account_not_approved</c> when a proved
    /// credential met an account the tenant has not yet admitted,
    /// <c>auth.approval_store_unavailable</c> when a correct code could not be recorded, or
    /// <c>TOKEN_STORE_UNAVAILABLE</c> propagated unchanged when the credential was accepted and the
    /// session could not be recorded.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The verification code is a one-time approval token, not a second factor.</b> That is a
    /// measured fact rather than an interpretation: the legacy provider compared the submitted code
    /// against the composed value <c>portalId &amp; "-" &amp; userId</c> and, on a match, marked the
    /// account approved and persisted that change. An account therefore verified once and stayed
    /// verified. The composition must be preserved exactly, because the code is stored nowhere - no
    /// column for it appears anywhere in the schema chain - and is recomputed from the tenant and
    /// account identifiers at the moment it is checked. Any account awaiting verification at the
    /// moment of migration still holds a code this implementation can accept, and changing the
    /// composition would strand every one of them.
    /// </para>
    /// <para>
    /// <b>Which accounts the approval flow applies to.</b> The legacy guard entered the approval
    /// branch only when the account was unapproved <em>and</em> was not a super user, so super users
    /// bypassed verification entirely; that exemption is preserved. Whether an unapproved account is
    /// asked for a code or refused outright depends on the tenant's registration mode, which the
    /// legacy screen consulted at <c>Login.ascx.vb:L170</c>: it produced its "enter a code" prompt
    /// only for verified registration and answered "not authorised" otherwise. That is the
    /// distinction between <c>auth.verification_required</c> and <c>auth.account_not_approved</c>.
    /// An absent code and a blank code are the same submission, so both lead to the former and only
    /// a supplied, non-matching code leads to <c>auth.verification_code_invalid</c>, reproducing the
    /// legacy branch at <c>Login.ascx.vb:L177-L181</c> exactly.
    /// </para>
    /// <para>
    /// <b>Read the registration mode through the tenant aggregate, not from ambient state.</b> The
    /// legacy screen read <c>PortalSettings.UserRegistration</c> - declared at
    /// <c>PortalSettings.vb:L174</c> - from a per-request object the page framework had already
    /// populated. That member is deliberately absent from <c>IPortalContext</c>, so an
    /// implementation loads the tenant through <c>IPortalRepository</c> and reads the mode from the
    /// aggregate. This layer never reaches into a request object for it.
    /// </para>
    /// <para>
    /// <b>Required decision order.</b> Resolve the tenant, then the account within it; evaluate
    /// lockout, applying the automatic-unlock window before concluding that an account is locked;
    /// verify the credential; then evaluate approval; then, where a correct code accompanied a
    /// correct credential on an unapproved account, approve the account and persist that; then mint
    /// the pair. Two properties of that order are load-bearing rather than incidental. A locked
    /// account must never have its credential compared, so it cannot be used as a credential oracle -
    /// which is what the legacy provider did at <c>AspNetMembershipProvider.vb:L1481</c> and is worth
    /// preserving. And NOTHING may be written before the credential has been accepted, which is what
    /// reverses the legacy order for approval; see the annotations below. A legitimate caller reaches
    /// the same outcome by either order.
    /// </para>
    /// <para>
    /// <b>The two replacement obligations.</b> After a successful current-scheme verification the
    /// implementation asks the domain hashing abstraction whether the stored value's work factor is
    /// outdated and replaces it when needed. Separately, a legacy representation may be verified through
    /// the deployment-secret-backed compatibility abstraction only while its absolute deadline remains
    /// open, and that successful answer requires an immediate BCrypt replacement in the same login.
    /// Both replacements are transparent to the caller.
    /// </para>
    /// <para>
    /// The separation is load-bearing. <see cref="IPasswordHasher"/> remains BCrypt-only and permanently
    /// contains no reversible capability; <see cref="ILegacyCredentialVerifier"/> is the bounded bridge and
    /// cannot mint or persist a credential. Administrative reset, owned by the account-administration
    /// contract, remains the fallback after the deadline, for an unsupported representation, or when the
    /// owner no longer knows the credential.
    /// </para>
    /// <para>
    /// <b>The effective legacy verdict, preserved for reference.</b> The legacy screen computed
    /// <c>authenticated = (loginStatus &lt;&gt; UserLoginStatus.LOGIN_FAILURE)</c> at
    /// <c>Login.ascx.vb:L187</c>, reached only through the else branch of the approval test at
    /// <c>L168</c>. Against the seven-member status enumeration that yields:
    /// </para>
    /// <list type="table">
    ///   <listheader>
    ///     <term>Status (value)</term>
    ///     <description>Legacy verdict at L187</description>
    ///   </listheader>
    ///   <item><term>LOGIN_FAILURE (0)</term><description>not authenticated</description></item>
    ///   <item><term>LOGIN_SUCCESS (1)</term><description>authenticated</description></item>
    ///   <item><term>LOGIN_SUPERUSER (2)</term><description>authenticated</description></item>
    ///   <item>
    ///     <term>LOGIN_USERLOCKEDOUT (3)</term>
    ///     <description>authenticated - a locked account passed this test</description>
    ///   </item>
    ///   <item>
    ///     <term>LOGIN_USERNOTAPPROVED (4)</term>
    ///     <description>not authenticated - handled by the L168 branch, never reaching L187</description>
    ///   </item>
    ///   <item>
    ///     <term>LOGIN_INSECUREADMINPASSWORD (5)</term>
    ///     <description>authenticated, with an advisory</description>
    ///   </item>
    ///   <item>
    ///     <term>LOGIN_INSECUREHOSTPASSWORD (6)</term>
    ///     <description>authenticated, with an advisory</description>
    ///   </item>
    /// </list>
    /// <para>
    /// The row for status three is a defect, and it is recorded rather than silently corrected; the
    /// annotation below states what this contract does about it and why. Statuses five and six are
    /// advisories rather than refusals and are carried as an informational reason on a successful
    /// result.
    /// </para>
    /// <para>
    /// <b>Brute-force resistance is not a parameter of this member.</b> The legacy screen gated the
    /// entire sign-in on an image challenge; its replacement is request rate limiting at the api
    /// edge, which this contract cannot express and must not try to. The companion controls are the
    /// credential-length bounds in the request validator and the failed-attempt bookkeeping that
    /// locks an account, both of which an implementation is expected to apply.
    /// </para>
    /// </remarks>
    // MIGRATION: the image-based human-verification challenge is dropped, and it is a security-
    // relevant reduction rather than a cosmetic one. The legacy gate existed at two sites -- the
    // whole sign-in was conditional on it at Login.ascx.vb:L162, and the recovery screen repeated
    // MIGRATION: the gate at SendPassword.ascx.vb:L176 -- and it was per-tenant configuration, read
    // MIGRATION: from AuthenticationConfig.GetConfig(PortalId).UseCaptcha at Login.ascx.vb:L59-L61.
    // The control implementing it belongs to Library/Controls, a tree this migration excludes, so
    // there is nothing for a parameter here to bind to. The named compensating control is request
    // rate limiting on the sign-in endpoint, configured in the api layer as a global limiter keyed
    // by method and path rather than as an attribute a new controller could simply omit.

    // MIGRATION: a locked account is refused here, and the legacy screen's own flag said otherwise.
    // Login.ascx.vb:L187 computed its authenticated flag as "the status is not LOGIN_FAILURE", which
    // is true for the locked-account status; what actually stopped the sign-in was the provider
    // clearing its user object afterwards, leaving the screen holding an authenticated flag and no
    // user. Depending on a null reference to contradict a boolean is not a behaviour worth
    // reproducing, so this contract states the refusal once, as a failure, where it cannot be read
    // two ways. The defect itself is annotated and NOT otherwise altered: the automatic-unlock
    // window is preserved exactly, because the legacy check unlocked the account and continued
    // rather than refusing, and an implementation must do the same before concluding that an account
    // is locked. Which statuses ultimately authenticate is the implementation's decision and is
    // stated in MIGRATION_NOTES.md; this contract's job is to make either decision expressible
    // without ambiguity, which is why locked, unapproved, insecure-default and plain refusal all
    // have their own codes.

    // MIGRATION: verifying the credential BEFORE disclosing an account's approval state reverses the
    // legacy order and is a deliberate divergence. The legacy provider graded lockout and approval
    // first and then skipped the credential comparison altogether for either state, so a caller who
    // supplied nothing but a valid account name learned that the account existed and was awaiting
    // verification. A legitimate caller's experience is unchanged, because a legitimate caller
    // supplies the correct credential and reaches the same outcome by either order; only an
    // enumerating caller is affected. The legacy behaviour is not preserved because preserving it
    // would mean shipping the enumeration leak into new code.

    // MIGRATION: approving an account only AFTER the credential has been verified is the second
    // deliberate divergence, and it closes a genuine legacy defect. Because the legacy provider
    // approved the account before it compared any credential, a caller who could guess the composed
    // code -- and it is composed from two integers, so it is guessable -- could permanently approve
    // somebody else's pending account without ever holding its credential. Requiring the credential
    // first makes the guessable code insufficient on its own, while the composition itself is
    // preserved for the parity reason given above.

    // MIGRATION: an accepted sign-in can require either of two replacement writes. A current BCrypt
    // value is re-hashed when its work factor is superseded. A bounded legacy clear, SHA-1 or encrypted
    // value is checked by the isolated, opt-in ILegacyCredentialVerifier and is always replaced with
    // BCrypt when accepted. Neither path exposes credential material or changes the response shape;
    // administrative reset remains the fallback for every legacy row that cannot be verified.

    // MIGRATION: the three approval outcomes keep their legacy identities but not their legacy
    // wording. Login.ascx.vb:L168-L185 selected between the resource keys EnterCode, InvalidCode and
    // UserNotAuthorized, which the page framework then resolved through its localisation pipeline
    // against the App_LocalResources files. That pipeline is not ported: these are stable machine
    // codes, the client supplies the wording, and the legacy resource files remain the authority for
    // what that wording should say.

    // MIGRATION: the two insecure-default statuses become advisories on a successful result rather
    // than refusals, and the independent five-member validity status is treated the same way. The
    // legacy re-grading marked a successful administrator whose credential was still the shipped one
    // and a successful host in the same position, and the screen warned rather than refused. The
    // validity status - valid, expired, expiring, update-profile and update-credential - is likewise
    // a "you are in, and you must act" outcome for its middle members; neither enumeration appears
    // on this surface, and both are reported as reason codes.
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a valid refresh token for a new token pair, retiring the presented token.
    /// </summary>
    /// <param name="request">The refresh token to redeem.</param>
    /// <param name="cancellationToken">Token observed while the session store is read and written.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the new pair, or a failed
    /// result carrying <c>auth.invalid_refresh_token</c> when the presented token is unknown,
    /// expired, already redeemed or revoked, or when the account it names no longer exists. Those
    /// causes are deliberately indistinguishable to the caller. <c>TOKEN_STORE_UNAVAILABLE</c>
    /// propagates unchanged when the successor pair could not be recorded.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Rotation is mandatory, not optional.</b> The presented token is retired as part of the
    /// same operation that mints its successor, so replaying a captured refresh token fails and the
    /// replay is detectable. An implementation delegates both halves to <see cref="ITokenService"/>
    /// so that retirement and issue cannot be separated by a failure between them.
    /// </para>
    /// <para>
    /// <b>The caller's description is re-read, not copied.</b> Roles and permission keys are resolved
    /// from stored state during the exchange rather than carried over from the retired token, which
    /// is how a role granted - or withdrawn - since the previous exchange becomes effective. This is
    /// the only mechanism by which an entitlement change reaches a signed-in caller before its access
    /// token expires.
    /// </para>
    /// <para>
    /// No credential is involved: this member neither accepts nor consults one, and it is the reason
    /// the access token can be short-lived without asking the caller to sign in repeatedly.
    /// </para>
    /// </remarks>
    Task<Result<LoginResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires a session by revoking the refresh token that sustains it.
    /// </summary>
    /// <param name="request">
    /// The refresh token to revoke. There is no separate sign-out contract: the token is the only
    /// thing a sign-out can act upon, so the refresh contract is reused deliberately.
    /// </param>
    /// <param name="cancellationToken">Token observed while the session store is written.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>, including when the token was already
    /// unknown or already revoked - sign-out is idempotent, and reporting "no such token" would let a
    /// caller probe which tokens exist. <c>TOKEN_STORE_UNAVAILABLE</c> propagates unchanged when the
    /// revocation could not be recorded, because a sign-out that silently failed to revoke anything
    /// must not be reported as a sign-out.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Read the honest semantics before relying on this member.</b> It revokes the presented
    /// refresh token, which breaks the rotation chain and prevents any further session from being
    /// minted from it. It does <em>nothing</em> to the access token the caller is currently holding:
    /// a stateless bearer token cannot be recalled, so that token remains valid until its published
    /// expiry and the client must discard it. This is the one respect in which sign-out is weaker
    /// than the cookie clearance it replaces, and it is precisely why the access token's lifetime is
    /// short.
    /// </para>
    /// <para>
    /// <b>There is no deny list, and adding one would be a regression.</b> Recording issued access
    /// tokens so they could be rejected individually would reintroduce the per-caller server-side
    /// session state this migration exists to remove, and would make every authenticated request
    /// depend on a store read. No member of this contract, and no type it names, maintains such a
    /// record.
    /// </para>
    /// </remarks>
    // MIGRATION: FormsAuthentication.SignOut at PortalSecurity.vb:L77 has no stateless counterpart.
    // MIGRATION: that helper performed six server-side actions: the ticket abandonment itself, and
    // clearing the language, authentication, portalaliasid and portalroles cookies -- the last two
    // MIGRATION: by back-dating their Expires thirty years, with the static ClearRoles helper
    // repeating the portalroles clear. Five of the six manipulated browser state that no longer
    // exists in this design, and the sixth abandoned a server-side ticket that no longer exists
    // either. What survives is the revocation of the refresh token; the access token lapses at its
    // own expiry and the client discards it.
    Task<Result> LogoutAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-evaluates the blocking credential and profile work for one authenticated account from current
    /// stored state.
    /// </summary>
    /// <param name="portalId">Tenant the session is scoped to.</param>
    /// <param name="userId">Authenticated account identifier.</param>
    /// <param name="cancellationToken">Token observed while account policy and profile state are read.</param>
    /// <returns>
    /// A successful outcome carrying the current remediation state, or a failed outcome when the tenant,
    /// membership or required policy state cannot be resolved.
    /// </returns>
    /// <remarks>
    /// Called by the API authorisation gate on protected requests. Login and rotation apply the same
    /// authoritative evaluation inside their existing workflows before a pair leaves the server. The
    /// access-token claims are signed guidance for the client; this read is the enforcement point, so
    /// imposing or clearing remediation takes effect without waiting for an existing access token to expire.
    /// </remarks>
    Task<Result<AuthenticationRemediationState>> EvaluateRemediationAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes the caller of the current request to itself, resolving the identity the caller's
    /// own access token asserts.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token observed while the account, its roles and its permission keys are read.
    /// </param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the caller's own description,
    /// or a successful result whose value is <see langword="null"/> when the caller is not
    /// authenticated or the account its token names no longer exists. Absence is reported as a
    /// successful result with no value rather than as a refusal, so this member documents no code of
    /// its own; <c>auth.user_not_found</c> is reserved for an implementation that elects to report
    /// the vanished-account case explicitly instead.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>It takes no identifier, and that is the contract.</b> The subject is the caller, read from
    /// the request's own claims through <see cref="ICurrentUser"/>, which is injected rather than
    /// passed. Accepting an account or tenant identifier would turn a member that describes the
    /// caller into one that describes anybody, and reading another account is what the
    /// account-administration contract's own read is for, behind its own authorisation.
    /// </para>
    /// <para>
    /// <b>Why absence is a success and not a failure.</b> A bearer token asserts its own validity and
    /// is not recalled by anything the server does, so an account may be deleted, or a tenant
    /// removed, while a token naming it is still within its lifetime. The caller is then
    /// legitimately authenticated and the subject is legitimately gone - a true answer, not a fault.
    /// Reporting it as a failed result would oblige the api edge to choose an error status for a
    /// request that did not fail. A null value on a successful result is the documented way this
    /// codebase expresses "absent", and this member relies on it.
    /// </para>
    /// <para>
    /// <b>Claims are not trusted as the whole answer.</b> Roles and permission keys are read from
    /// the store rather than copied out of the token, because a token minted before a role was
    /// withdrawn still asserts it: the token establishes <em>who</em> is asking and the store
    /// establishes what they may now do. This member is therefore a read, not a projection of the
    /// token.
    /// </para>
    /// </remarks>
    // MIGRATION: the legacy answer to this question was an ambient read with no call and no failure
    // MIGRATION: mode. GetCurrentUserInfo at UserController.vb:L381 returned the per-request user
    // object the page base classes had already populated, and for an anonymous caller it returned a
    // MIGRATION: newly constructed, empty UserInfo rather than Nothing -- an object that was
    // indistinguishable from a real account until its fields were inspected. That sentinel is not
    // reproduced: absence is stated explicitly, and no empty stand-in is ever returned in place of a
    // caller.
    Task<Result<CurrentUserDto?>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
