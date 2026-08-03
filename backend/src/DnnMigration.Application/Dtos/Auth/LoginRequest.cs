using System.Text.Json.Serialization;

namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// Request body of <c>POST /api/v1/auth/login</c>: the credentials a caller
/// submits in exchange for a token pair.
/// </summary>
/// <remarks>
/// <para>
/// This type is the sole input to the sign-in service's <c>LoginAsync</c>
/// operation. A successful sign-in yields a <c>LoginResponse</c>; a rejected one
/// is reported as an RFC 7807 problem-details payload produced at the Api edge,
/// never as a status flag on a body returned alongside HTTP 200.
/// </para>
/// <para>
/// <b>This type carries a credential in clear text and must never be logged.</b>
/// Nothing on it may reach a log sink, an audit record, a trace, a metrics
/// dimension or an exception message. Request logging excludes the bodies of
/// credential-bearing endpoints for exactly this reason, and the contract is
/// kept deliberately narrow -- three members, one of them optional -- so that
/// there is as little to leak as possible.
/// </para>
/// <para>
/// The shape is derived from what the legacy screen actually posted rather than
/// from any stored shape, so no entity crosses this boundary.
/// <c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx</c> declares
/// exactly three caller-supplied inputs -- <c>txtUsername</c> (L10),
/// <c>txtVerification</c> (L16) and <c>txtPassword</c> (L29) -- and this type
/// declares exactly those three and nothing else. The legacy call they fed,
/// <c>UserController.ValidateUser</c> at <c>Login.ascx.vb:L164</c>, took eight
/// arguments: the other five were an authentication-type literal, two ambient
/// tenant values, the caller's network address and a status out-parameter, none
/// of which a client may supply. Each of those decisions is annotated below.
/// </para>
/// <para>
/// Validation is declared separately, in
/// <c>Application/Validation/LoginRequestValidator.cs</c>. This type enforces no
/// rule whatsoever: it does not trim, clamp, coerce, reject or re-shape any
/// value beyond the empty-string initialisers below, so what the caller sent is
/// exactly what the validator and the service observe. The legacy password
/// policy that validator reproduces is preserved verbatim from the shipped
/// configuration -- minimum length 7, zero required non-alphanumeric
/// characters, no question-and-answer requirement, e-mail uniqueness not
/// enforced -- and is deliberately never tightened here, because hardening a
/// policy mid-migration would stop existing accounts from signing in. Those
/// values live on <c>Application/Options/PasswordPolicyOptions.cs</c>.
/// </para>
/// <para>
/// The verification code is required only by tenants whose registration mode is
/// verified registration; see <see cref="VerificationCode"/>.
/// </para>
/// </remarks>
public sealed class LoginRequest
{
    /// <summary>
    /// Gets or sets the tenant the credential is being presented to. Populated by the Api layer,
    /// never by the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This member is not part of the wire contract.</b> It is excluded from serialisation, so a
    /// value placed in the request body is not merely ignored - it can never be bound in the first
    /// place. The Api layer assigns it after the body has been validated, taking the tenant that the
    /// alias-resolution middleware resolved from the request host and falling back to the explicit
    /// query parameter only when the host matches no configured alias. That ordering is what closes
    /// the tenant-crossing vector: the transport decides which tenant a credential is presented to,
    /// and a caller cannot name a site it is not addressing.
    /// </para>
    /// <para>
    /// <b>Why it is carried here rather than passed separately.</b> The sign-in operation takes this
    /// type and a cancellation token, and nothing else - a signature fixed by the migration plan
    /// because it is what the legacy eight-argument call collapses into. The tenant has to reach the
    /// service somehow, and it cannot be read from the request context inside the service: sign-in
    /// deliberately supports a host with no alias row, in which case that context is unresolved and
    /// only the explicit value exists. So the tenant travels on this type, assigned by the one layer
    /// entitled to determine it.
    /// </para>
    /// <para>
    /// <b>Nullable, and never defaulted.</b> Absence is a null reference, never a numeric stand-in:
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c>, so both zero and minus one are
    /// real tenants and neither can be borrowed to mean "unspecified". A sign-in whose tenant is
    /// absent is refused rather than attributed to a default.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public int? PortalId { get; set; }

    /// <summary>
    /// Gets or sets the sign-in name supplied by the caller.
    /// </summary>
    /// <remarks>
    /// Corresponds to <c>txtUsername</c> at <c>Login.ascx:L10</c> and to the
    /// <c>Username</c> parameter of the legacy
    /// <c>UserController.ValidateUser</c> overload at
    /// <c>UserController.vb:L1132</c>; the legacy parameter name is kept so the
    /// wire contract stays recognisable to anyone reading the original.
    /// Initialised to <see cref="string.Empty"/> so the member is never null
    /// under the enabled nullable reference context even when a caller omits it
    /// entirely: absence and emptiness are the same thing for a mandatory
    /// credential field, and it is the validator -- not this type -- that
    /// rejects an empty value.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the clear-text password supplied by the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to <c>txtPassword</c> at <c>Login.ascx:L29</c>, rendered
    /// there with <c>textmode="password"</c>, and to the <c>Password</c>
    /// parameter of the legacy overload at <c>UserController.vb:L1132</c>.
    /// </para>
    /// <para>
    /// Inbound clear text is correct and expected here -- checking a submitted
    /// credential is the entire purpose of a sign-in request -- and this is the
    /// only member in the contract where one appears. Checking it against the
    /// stored one-way hash happens in the Infrastructure layer; this type does
    /// no hashing, no comparison, no normalisation and no re-encoding, and it
    /// never carries a hashed, encrypted, derived or recoverable form of a
    /// credential, nor a knowledge-based challenge pair, nor a confirmation
    /// copy. Changing a stored credential is a different operation with its own
    /// request type.
    /// </para>
    /// <para>
    /// A maximum length IS enforced, and it is net-new rather than ported. The
    /// legacy table capped its password column at 20 characters, but that was a
    /// storage limit of a schema which no longer holds the credential at all,
    /// so it is not the source of the bound and is not reproduced. The bound
    /// exists because this is an unauthenticated endpoint whose input is handed
    /// to a deliberately expensive one-way hash, and an unbounded field lets the
    /// caller choose how much work the server performs.
    /// </para>
    /// <para>
    /// The bound is not declared on this member as an attribute or a comment
    /// carrying its own number. It lives once, on
    /// <see cref="Validation.CredentialBounds"/>, is measured in UTF-8 bytes
    /// because that is the form the hashing algorithm consumes, and is applied by
    /// <c>Validation/LoginRequestValidator.cs</c> together with every other
    /// credential entry point. Stating the number here as well is exactly how the
    /// two came to disagree before, so it is deliberately not stated. The
    /// tightening is recorded in <c>MIGRATION_NOTES.md</c>.
    /// </para>
    /// </remarks>
    public string Password { get; set; } = string.Empty;

    // MIGRATION: The verification code survives the port, but only as an
    // OPTIONAL member. Login.ascx ships both of its table rows with
    // visible="false" (L12 and L15), and Login.ascx.vb reveals them only on the
    // branch at L168 -- where the legacy sign-in status came back as
    // LOGIN_USERNOTAPPROVED -- combined with L170, where the tenant's
    // registration mode is verified registration -- in the target Domain enum
    // that is UserRegistrationMode.VerifiedRegistration, value 3, carried over
    // from the legacy PortalRegistrationType. Outside that branch the legacy
    // screen never collected it, so making it mandatory here would reject
    // sign-ins the legacy application accepted. The message keys that branch
    // selects -- "EnterCode" (L175 and L180), "InvalidCode" (L178) and
    // "UserNotAuthorized" (L184) -- remain the wording authority for the
    // equivalent target messages; their resolved English is in
    // Website/admin/Authentication/App_LocalResources/Login.ascx.resx at
    // L162-L164, L165-L167 and L222-L224 respectively, and must not be replaced
    // with invented text.

    // MIGRATION: null and the empty string are equivalent on this member, by
    // deliberate decision rather than by accident. The legacy branch at
    // Login.ascx.vb:L177 reads `If txtVerification.Text <> "" Then` to tell an
    // incorrect code from a missing one, and the legacy null-string sentinel at
    // Library/Components/Shared/Null.vb:L71-L75 returns the empty string rather
    // than a null reference, so the original contract could not distinguish
    // "absent" from "empty" at all. The CLR type is nullable so that callers
    // CAN express absence, but the two forms are defined to mean the same
    // thing, and this type converts neither into the other: normalising either
    // way here would silently move the L177 decision boundary. Rule T7 keeps
    // that judgement in exactly one place, so the validator and the sign-in
    // service cannot disagree about it.

    /// <summary>
    /// Gets or sets the optional verification code, which only tenants whose
    /// registration mode is verified registration ever require.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to <c>txtVerification</c> at <c>Login.ascx:L16</c>. Both of
    /// its table rows ship <c>visible="false"</c> (L12 and L15), so the legacy
    /// screen collected it only after a first attempt reported that the account
    /// was not yet approved. It is therefore genuinely optional on the wire, and
    /// nullable here so that "not supplied" can be expressed at all.
    /// </para>
    /// <para>
    /// <b>A null value and an empty string mean exactly the same thing on this
    /// member: no code was supplied.</b> The legacy branch at
    /// <c>Login.ascx.vb:L177</c> reads
    /// <c>If txtVerification.Text &lt;&gt; "" Then</c> to tell an incorrect code
    /// from a missing one, and the legacy null-string sentinel at
    /// <c>Library/Components/Shared/Null.vb:L71-L75</c> returns the empty string
    /// rather than a null reference, so the two were indistinguishable in the
    /// original contract. This type treats them as equivalent and converts
    /// neither into the other, leaving one single definition for the validator
    /// and the sign-in service to agree on.
    /// </para>
    /// <para>
    /// <b>The sign-in service is the only consumer.</b> <c>IAuthService.LoginAsync</c> reads
    /// this member and nothing else does, and it is that contract - not this one - that
    /// declares the two outcomes the code produces: <c>auth.verification_required</c> when the
    /// account awaits verification and no code accompanied the credential, and
    /// <c>auth.verification_code_invalid</c> when a code was supplied and did not match.
    /// Composition of the value itself is measured and preserved there.
    /// </para>
    /// </remarks>
    public string? VerificationCode { get; set; }

    // ------------------------------------------------------------------------
    // Deliberate divergences: inputs the legacy sign-in path had that this
    // contract does NOT carry. Recorded here under Rule T5 so that every
    // absence is a documented decision rather than an omission. Where naming an
    // identifier on this type would defeat the point of leaving it off, it is
    // cited by file and line instead.
    // ------------------------------------------------------------------------

    // MIGRATION: The image-based human-verification challenge is dropped.
    // Login.ascx declared that control at L22, inside the two table rows opened
    // at L18 and L21; Login.ascx.vb toggled those rows at L137-L138 from a
    // per-tenant switch and, at L162, gated the ENTIRE sign-in on the control
    // reporting itself valid. The control belongs to Library/Controls/**, a tree
    // this migration excludes wholesale -- 102 files across ten control
    // sub-libraries -- so there is no equivalent for a member here to bind to.
    // This is a DELIBERATE FUNCTIONAL REDUCTION, not an oversight. The named
    // compensating control is request rate limiting, configured in
    // Api/Extensions/RateLimitingExtensions.cs and registered as a GLOBAL
    // limiter that selects requests by method and path segment -- deliberately
    // NOT as an attribute on a controller. The distinction is the whole value of
    // the control: an attribute is a thing that can be omitted, and omitting it
    // yields a credential endpoint with no bound and no indication that it is
    // missing one, whereas a global limiter cannot be opted out of by writing a
    // new controller. Bounding the password length is the companion control and
    // lives in the validators; the two address different halves of the same
    // problem and neither substitutes for the other. The identical decision is
    // recorded for the sibling registration request DTO, CreateUserRequest, and
    // the two must stay consistent; the legacy per-tenant switches that turned
    // the challenge on are correspondingly inert on MembershipSettingsDto.

    // MIGRATION: The hard-coded "DNN" authentication-type argument is dropped.
    // Login.ascx.vb passed that literal twice -- at L164, as the fourth
    // argument of UserController.ValidateUser, and again at L191 when
    // constructing the authenticated-event arguments -- because the legacy
    // platform multiplexed several pluggable sign-in mechanisms behind one
    // screen. The target has exactly one path, JWT bearer tokens, so such a
    // discriminator could only ever hold a single value. No selector for it is
    // accepted from the caller, and none is inferred.

    // MIGRATION: Neither the tenant identity nor the caller's network address is
    // accepted from the client, even though Login.ascx.vb:L164 passed all three
    // of those values into the legacy call as its first, sixth and seventh
    // arguments -- the tenant key, the tenant display name and the network
    // address. All three were ambient server-side values read from the Web Forms
    // page, never posted by the browser. Accepting the first two from a request
    // body would open a tenant-crossing vector, because a caller could name a
    // site it was not actually addressing; the target instead resolves them once
    // per request in the Api layer's alias-resolution middleware. The tenant key
    // is therefore declared on this type but excluded from serialisation and
    // assigned by that layer, which is a stronger guarantee than omitting the
    // member would give -- an unbindable member cannot be supplied at all,
    // whereas a member the service reads from elsewhere still has to be trusted
    // to have been read from the right place. The tenant display name is not
    // carried at all: nothing in the sign-in decision needs it, and the response
    // projection reads it from the tenant aggregate. Accepting the third would
    // let an attacker choose the audit trail recorded against their own sign-in
    // attempt, which is a spoofing surface, so it is read from the connection by
    // the Api layer's structured request log and never travels on this type.

    // MIGRATION: No "keep me signed in" flag is carried forward. The legacy
    // checkbox for it belongs to Website/admin/Authentication/Login.ascx:L29 --
    // a different screen, outside this migration's reference inventory, which
    // this contract does not derive from -- while the sign-in screen this type
    // IS derived from never offered one. Its paired configuration value at
    // Website/release.config:L51, shipped as 0, governed a Forms-authentication
    // sign-in cookie whose lifetime has no counterpart under stateless bearer
    // tokens, and it is therefore not reproduced on the token options either.
    // Refresh-token lifetime is the modern equivalent and is configured there
    // instead.
}
