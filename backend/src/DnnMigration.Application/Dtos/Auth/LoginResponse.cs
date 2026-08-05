namespace DnnMigration.Application.Dtos.Auth;

// MIGRATION: this contract replaces the legacy sign-in outcome wholesale rather than translating it.
// Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L187 checked credentials and
// then reported the outcome through a ByRef out-parameter while the *session* was established as a
// side effect, by writing an encrypted forms-authentication cookie. Nothing was returned to the
// caller, because there was no caller: the page wrote a cookie and redirected. Here the session IS
// the response - a short-lived bearer token the client presents on every subsequent request - so the
// outcome is data rather than an ambient side effect. The three overloads that carried the
// out-parameter (UserController.vb:L1110, L1132 and L1171) are retired by this type; no target
// public member takes an out-parameter or a by-reference parameter.
//
// MIGRATION: the legacy post-credential validation enumeration is NOT carried onto this contract,
// and neither is any other legacy status enumeration. That enumeration is declared at L23-L29 of its
// own source file under Library/Components/Users/Membership/ (namespace
// DotNetNuke.Security.Membership) and is returned by UserController.vb:L1171. Its five values map
// onto the three response flags below, value by value:
//     value 0, valid                  -> no flag set; all flags stay false
//     value 1, password-expired       -> MustChangePassword   (legacy behaviour was blocking)
//     value 2, password-expiring      -> PasswordExpiring     (legacy behaviour was NON-blocking)
//     value 3, update-profile-needed  -> MustUpdateProfile    (legacy behaviour was blocking)
//     value 4, password-update-forced -> MustChangePassword   (legacy behaviour was blocking)
// ALL THREE FLAGS ARE POPULATED, and an earlier revision of this note said only two were. AuthService
// assigns MustChangePassword and PasswordExpiring on both the sign-in and the rotation path, from
// EvaluateCredentialAdvisoriesAsync, and assigns MustUpdateProfile on both paths from
// EvaluateProfileRemediationAsync, which asks IUserService.RequiresProfileCompletionAsync - the one
// implementation of the legacy per-tenant gate and profile-completeness walk. The claim that no
// production path assigned it described the tree before that producer existed; a flag that can only
// ever answer one way is a false capability signal, which is precisely why the member is carried only
// now that an authoritative producer stands behind it. See its own remarks.
// The blocking distinction is measured, not inferred: Website/admin/Authentication/Login.ascx.vb
// renders the same interstitial for values 1, 2 and 4 (PageNo = 2) but enables its proceed panel for
// value 2 alone (L544, against L539 and L548), and sends value 3 to a different step (L552).
// GAP REPORTED: that enumeration is deliberately NOT one of the nine domain enumerations, so no
// domain type exists for it and none was invented here. Its meaning survives only as these flags.
//
// MIGRATION: a semantic divergence that is deliberate and is not absorbed silently. The legacy
// enumeration was SINGLE-VALUED WITH PRECEDENCE - UserController.vb:L1175-L1194 assigns the forced
// update first, then the expired and expiring cases, then tests the profile case only while the
// value is still the valid one - so exactly one condition could ever be reported and the others were
// masked. Two independent booleans can therefore express a combination the legacy could not, namely a
// credential that has expired and one that is also inside its reminder window. That is strictly more
// information, never less, and a client that handles the flags independently cannot regress.
//
// MIGRATION: no legacy status enumeration reaches the wire, and this is a correctness requirement
// rather than a preference, because the three legacy enumerations disagree about what a successful
// value even is: the post-credential validation enumeration treats 0 as valid, the sign-in status
// enumeration treats 1 as the successful case and 0 as its failure case, and the account-creation
// enumeration treats 13 as the successful case while 0 names an operation rather than an outcome.
// Publishing an ordinal from any of them would put three mutually incompatible conventions behind a
// single number. Expected failures instead travel as the service layer's outcome wrapper inside the
// application layer and are rendered at the API edge as RFC 7807 problem details, so a rejected
// sign-in never produces this type at all. Distinguishability comes from the problem type, title and
// detail, never from a status field on a 200 response.
//
// MIGRATION: the two success-with-caveat sign-in statuses - the shipped-administrator-credential and
// shipped-host-credential cases, values 5 and 6 of the sign-in status enumeration, promoted at
// UserController.vb:L1144-L1152 when a known default credential is presented - fold onto
// MustChangePassword. Forcing a credential change IS the legacy remediation intent for both, so the
// flag expresses it exactly. A fourth, dedicated boolean was CONSIDERED AND REJECTED: it would add a
// wire member that no client could act on differently, and this contract is held to a minimal
// surface.
//
// MIGRATION: a legacy defect is recorded here and deliberately NOT fixed, because the target makes
// it unreachable rather than patching it. At Login.ascx.vb:L186-L187 the else arm sets its
// authenticated flag from a single inequality test of the reported sign-in status against that
// enumeration's failure value alone. The preceding branch at L168 special-cases only the
// not-approved value, so every other value falls into that else arm - including the locked-out
// value 3, which is not the failure value 0 and was therefore treated as authenticated. A locked
// account could pass the gate. No behavioural fix is applied to the legacy tree, which stays
// byte-identical. In the target the defect is structurally impossible: a locked account yields a
// failure outcome that becomes an RFC 7807 problem document, so it can never produce this type, and
// there is no equivalent comparison left to get wrong.
//
// MIGRATION: ending a session has no server-side counterpart. FormsAuthentication.SignOut cleared a
// cookie and took effect at once; a bearer token cannot be recalled once signed, so ending a session
// revokes the rotation credential server-side and discards the access token client-side, leaving the
// access token technically valid until it lapses. That window is why ExpiresAtUtc below is short
// and is published rather than left implicit. Two consequences: this contract carries no session
// handle of any kind, and the legacy cookie settings are not reproduced - neither the sliding
// expiration at Website/release.config:L215 nor the persistent-cookie timeout at L51. Rotation of
// the credential below replaces both.
//
// MIGRATION: the reversible credential store is replaced by a one-way adaptive hash. The legacy
// provider was registered at Website/release.config:L236-L246 with an encrypted format (L245) and
// with retrieval enabled (L239), decryptable through the symmetric material committed at L89-L93
// (the field at L91, the cipher named at L92) - material that is read as historical fact and is
// never reproduced by the target configuration. Retrieval is therefore not carried
// forward to any endpoint or screen, unlike the legacy flow that decrypted and mailed the stored
// credential outright. During an explicitly enabled migration window, the isolated legacy verifier
// can accept a bounded clear, SHA-1 or encrypted representation and AuthService immediately replaces
// it with BCrypt; administrative reset remains the fallback. No credential material or migration
// key appears on this contract either way.
//
// MIGRATION: the expiry is published as one absolute instant in UTC rather than as a remaining
// number of seconds. A relative lifetime is only correct at the moment the response is written and
// silently drifts by the transit and parse time, whereas an absolute instant stays correct however
// long delivery takes. Exactly one representation is published, so no client has to decide which of
// two agrees.
//
// MIGRATION: the caller's identity is embedded as one member rather than being flattened into this
// type or fetched separately. Flattening would restate an identity shape that already exists and
// create a second place for it to drift; a separate fetch would cost a second round trip before the
// application shell could render. The fuller administrative identity shape used by the user
// endpoints is deliberately NOT embedded - it is far larger than a sign-in needs.

/// <summary>
/// The successful payload of both <c>POST /api/v1/auth/login</c> and <c>POST /api/v1/auth/refresh</c>:
/// the bearer credential pair the client uses from that point on, the moment the access token lapses,
/// three remediation and expiry flags, and a snapshot of who the caller is.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY - THIS TYPE CARRIES BEARER CREDENTIALS AND MUST NEVER BE LOGGED. Both token members are
/// live credentials: anything holding <see cref="AccessToken"/> can act as the caller until it lapses,
/// and anything holding <see cref="RefreshToken"/> can obtain a fresh pair. Request and response
/// logging must exclude this body in full, and no structured log event may capture either member or
/// any fragment of one.
/// </para>
/// <para>
/// The refresh credential is what the caller later submits back, as the body of the refresh request
/// contract in this same folder. It is rotated on every successful exchange - the presented value is
/// retired and a replacement is issued - so a captured value is single-use and a replay is detectable.
/// Because a refresh returns this same type, a client needs one shape for both operations.
/// </para>
/// <para>
/// NO CONFIGURATION CROSSES THIS BOUNDARY. The signing material, the two token-envelope identity
/// values the server signs and checks against, and the configured lifetimes of both credentials are
/// server facts held by the application options type that owns them, and not one of them appears here
/// or may be added. The distinction is exact and load-bearing: when THIS token lapses is a fact about
/// this response, whereas how long tokens live is a fact about the deployment. The former is
/// published; the latter never is. Nor does any envelope member from the OAuth 2.0 token-endpoint
/// vocabulary appear - this is a first-party contract for one known client, not a token endpoint, so
/// the scheme is fixed by the API documentation instead of restated in every response.
/// </para>
/// <para>
/// NO LEGACY STATUS ENUMERATION APPEARS HERE, in any form - not as a type, a member, an ordinal or a
/// name. A rejected sign-in does not produce this type: expected failures are the service layer's
/// outcome type, rendered at the API edge as RFC 7807 problem details with a distinguishing type,
/// title and detail. A body that announced its own failure alongside a 200 status is the exact
/// anti-pattern RFC 7807 exists to prevent. Envelope and transport concerns are likewise absent: the
/// HTTP status is the status, and the correlation identifier travels as a response header.
/// </para>
/// <para>
/// Ending a session is token expiry plus client-side discard - there is no server-side revocation of
/// an already-issued access token - so this contract deliberately carries no session handle, no
/// rotation counter and no server-side rotation state of any kind.
/// </para>
/// <para>
/// The three remediation and expiry flags are the boundary expression of a successful sign-in that
/// nevertheless carries follow-up state. Each is a plain non-nullable boolean whose
/// <see langword="false"/> value means "not required", and each is written on the wire even when false,
/// so absence is stated rather than inferred from a missing field. A nullable form would be wrong rather than merely
/// generous: the legacy null test reported "absent" for <see langword="false"/> itself
/// (<c>Null.vb</c> L227-L228), so a legacy false and a legacy unknown were never distinguishable, and
/// modelling a third state here would advertise a distinction the source data cannot make. For the
/// same reason no conditional-omission serialisation policy may ever be applied to this type: omitting
/// defaults would erase every negative advisory.
/// </para>
/// <para>
/// This is a response contract, so nothing validates it and no validator exists for it. It computes
/// nothing, reads no clock, no configuration and no store, constructs and parses no token, and
/// exposes no derived member - every value is assigned by the service that issues it.
/// </para>
/// </remarks>
public sealed class LoginResponse
{
    /// <summary>
    /// The signed bearer credential, presented on subsequent requests in the <c>Authorization</c>
    /// header. Always populated on a successful outcome.
    /// </summary>
    /// <remarks>
    /// SECURITY: a live credential. Never log it, never place it in a URL, and never persist it where
    /// another origin can read it. The scheme it is presented under is fixed by the API contract and is
    /// deliberately not restated as a member of this response.
    /// </remarks>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// The single-use credential submitted to the refresh endpoint to obtain a replacement pair.
    /// Always populated on a successful outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rotated on every successful exchange, so presenting one twice fails and marks a replay. Its
    /// server-side state - when it lapses, whether it has been redeemed, and which family it belongs
    /// to - is held by the store that owns it and is deliberately not published: an expiry a client can
    /// read is an expiry a client can reason around, and a rotation counter would be an oracle.
    /// </para>
    /// <para>
    /// SECURITY: a live credential, and a longer-lived one than <see cref="AccessToken"/>. The same
    /// prohibition on logging applies with more force.
    /// </para>
    /// </remarks>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// The absolute moment at which <see cref="AccessToken"/> stops being accepted, expressed in
    /// COORDINATED UNIVERSAL TIME (UTC) and never in a local or portal-preferred zone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An absolute instant, not a remaining duration: a client schedules its refresh against this value
    /// instead of discovering expiry through a rejected request, and the value does not drift with
    /// transit or parse time the way a seconds-remaining figure would. Exactly one expiry
    /// representation is published, so there is no second member to reconcile it against.
    /// </para>
    /// <para>
    /// Assigned by the issuing service from the clock abstraction it depends on. This type performs no
    /// arithmetic on it and exposes no derived "has it lapsed" member, because that question must be
    /// answered against the reader's own clock at the moment it is asked, not against a value frozen
    /// when the response was serialised.
    /// </para>
    /// <para>
    /// On a successful outcome this is always a real future moment. It is never the minimum date, which
    /// is what the legacy layer used to encode an absent date (<c>Null.vb</c> L66-L69), so no sentinel
    /// interpretation applies to it.
    /// </para>
    /// </remarks>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Whether the caller must change their credential before continuing. <see langword="false"/> means
    /// no such requirement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derives from two legacy values of the post-credential validation enumeration, both of which were
    /// BLOCKING: value 4, the administrator-forced update, which the legacy code read from the
    /// membership flag of the same intent (<c>UserMembership.vb</c> L323) and gave the highest
    /// precedence (<c>UserController.vb</c> L1175-L1177); and value 1, a credential already past its
    /// expiry. Neither offered a way past the interstitial
    /// (<c>Website/admin/Authentication/Login.ascx.vb</c> L539 and L548).
    /// </para>
    /// <para>
    /// It additionally carries the two success-with-caveat sign-in statuses, values 5 and 6, raised when
    /// a shipped default credential is still in use (<c>UserController.vb</c> L1144-L1152). Sign-in
    /// legitimately succeeds in those cases, and forcing a change is the legacy remediation, so the
    /// caveat belongs on this flag rather than on a dedicated member.
    /// </para>
    /// <para>
    /// The identifier matches the member of the same meaning on the administrative user detail contract,
    /// so one signal has one name across every contract that carries it.
    /// </para>
    /// <para>
    /// This is not merely a client prompt. The access token carries the same signed decision and the API
    /// authorization gate re-evaluates the stored flag on every protected request, admitting only the
    /// authentication lifecycle and the account owner's credential-change route until it clears.
    /// </para>
    /// </remarks>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// Whether the caller's credential is approaching its expiry. <see langword="false"/> means no such
    /// advisory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derives from value 2 of the post-credential validation enumeration, and it is the ONLY advisory
    /// that was NON-BLOCKING in the legacy flow: the same interstitial appeared, but its proceed panel
    /// was enabled for this value alone
    /// (<c>Website/admin/Authentication/Login.ascx.vb</c> L544, against L539 and L548 for the blocking
    /// cases). A client must therefore treat this flag as a prompt the caller may decline, never as a
    /// gate, which is exactly why it is a separate member from
    /// <see cref="MustChangePassword"/> rather than folded into it.
    /// </para>
    /// <para>
    /// The legacy value was raised from a reminder window and could be suppressed outright by the
    /// caller of the validation overload (<c>UserController.vb</c> L1183). That window is not part of
    /// the migrated configuration surface, so this flag is carried by the contract and set by the server
    /// when such a window is configured; no expiry window is invented here, and the reduction is
    /// recorded rather than absorbed.
    /// </para>
    /// </remarks>
    public bool PasswordExpiring { get; set; }

    /// <summary>
    /// Whether the caller must complete or correct their profile before continuing.
    /// <see langword="false"/> means no such requirement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derives from value 3 of the post-credential validation enumeration, which was BLOCKING and was
    /// the one advisory the legacy flow sent to a different step rather than to the credential
    /// interstitial (<c>Website/admin/Authentication/Login.ascx.vb</c> L552). Because the legacy
    /// enumeration was single-valued and tested this case last, only while no other advisory had been
    /// raised (<c>UserController.vb</c> L1189-L1194), the legacy flow could never report it together
    /// with a credential advisory. This contract can, and that widening is the deliberate divergence
    /// recorded above.
    /// </para>
    /// <para>
    /// The legacy condition combined a per-tenant setting with a profile-completeness check
    /// (<c>UserController.vb</c> L1190-L1193), and BOTH are reproduced. The gate,
    /// <c>Security_RequireValidProfileAtLogin</c>, is read from the tenant's membership settings and the
    /// completeness check walks the tenant's required profile-property definitions against the caller's
    /// stored values, exactly as <c>ProfileController.vb</c> L305-L319 did. Neither is evaluated here:
    /// the rule has ONE implementation, <c>IUserService.RequiresProfileCompletionAsync</c>, which lives
    /// in the account-administration vertical that owns the definitions, and the sign-in path asks it the
    /// question rather than re-deriving the answer.
    /// </para>
    /// <para>
    /// MIGRATION: an earlier revision exposed this member without enforcing it and treated an unreadable
    /// profile state as though no work were required. Both behaviours were unsafe: a blocking legacy state
    /// became a client hint, and missing evidence opened the ordinary application surface. The authentication
    /// service now evaluates the requirement for login, refresh and every protected request; a read failure
    /// fails closed, while a true result admits only authentication lifecycle and the account owner's profile
    /// read/update routes until the required values are complete.
    /// </para>
    /// </remarks>
    public bool MustUpdateProfile { get; set; }

    /// <summary>
    /// Who the caller is, as of the moment the credentials were issued. Always populated on a successful
    /// outcome and never <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Embedded as a single member so that one sign-in round trip is enough to render the application
    /// shell, and so that the identity vocabulary has exactly one definition. Its members are
    /// deliberately not flattened into this type, which would duplicate that shape and give it two
    /// places to drift.
    /// </para>
    /// <para>
    /// A snapshot rather than a live view: an entitlement granted after issue appears neither here nor
    /// in the token until the client refreshes. Server-side authorisation always re-evaluates against
    /// stored state, so a stale snapshot can only make a client offer an affordance the API then
    /// refuses - it can never widen access, which is the correct direction for it to fail.
    /// </para>
    /// </remarks>
    public CurrentUserDto User { get; set; } = new();
}
