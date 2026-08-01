namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for <c>POST /api/v1/users/{id}/password</c>. Carries every
/// credential mutation the legacy DotNetNuke password-management screen offered,
/// and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY - AN INSTANCE OF THIS TYPE IS SECRET MATERIAL. It is the only shape in
/// this folder able to hold three plaintext credentials at once, so an instance must
/// never be written to a log, echoed back in a response, placed in a cache, or
/// interpolated into an exception message or a validation message. Two obligations
/// follow for the API layer, and they belong there rather than here: the
/// request-logging middleware must exclude the body of
/// <c>POST /api/v1/users/{id}/password</c> from structured logging, because the
/// non-functional requirement is structured logging that excludes sensitive data;
/// and the rate-limiting configuration must cover this route alongside the
/// authentication routes, because comparing <c>CurrentPassword</c> against the stored
/// hash makes this endpoint a credential-verification surface and therefore a
/// brute-force target. The response to this request carries a status only - never
/// password material, never a generated credential, never a hint.
/// </para>
/// <para>
/// ONE REQUEST TYPE, THREE LEGACY FLOWS. The legacy screen
/// <c>Website/admin/Users/Password.ascx</c> declares three independent panels, each a
/// separate operation: <c>pnlChange</c> (old, new and confirm), <c>pnlReset</c>
/// (answer the stored password question) and <c>pnlQA</c> (re-authenticate with the
/// current password, then replace the password question and answer). This DTO folder
/// is fixed at eight files and holds no ResetPasswordRequest and no
/// ChangePasswordQuestionAndAnswerRequest, so this single type is the union of all
/// three field sets. Every member is consequently nullable, and
/// <c>ChangePasswordRequestValidator</c> - not this type - decides which combination
/// of members is legal for the declared <c>Operation</c>, using conditional rule
/// sets. Do not split this type into three; doing so would add files the plan does
/// not contain.
/// </para>
/// <para>
/// NULL AND THE EMPTY STRING ARE BOTH MEANINGFUL, AND THEY ARE NOT THE SAME. No
/// member is initialised, so an omitted JSON member arrives as <c>null</c> ("this
/// flow does not use that field") while a supplied empty JSON string arrives as
/// <c>""</c> ("the user submitted the field and left it blank"). That distinction is
/// load-bearing and must be preserved: the legacy sentinel module
/// <c>Library/Components/Shared/Null.vb</c> returns <c>""</c> from <c>NullString</c>
/// (L71-L75) and its <c>IsNull</c> overload (L208) reports <c>""</c> as null, so the
/// legacy code could not tell the two apart, and every legacy guard consequently
/// tests against the empty string - a Web Forms text box never yields <c>null</c>.
/// Two rules follow. The validator must treat <c>null</c> and <c>""</c> identically
/// as "not supplied", which is what reproduces legacy behaviour. And serialisation
/// must not collapse the distinction: never configure this type with a
/// when-writing-null or when-writing-default ignore condition, and never normalise a
/// <c>null</c> to <c>""</c> or a <c>""</c> to <c>null</c> on the way in.
/// </para>
/// <para>
/// WHAT THIS TYPE DELIBERATELY DOES NOT CARRY. The target user identifier arrives
/// from the route, never from the body: a body identifier would create a
/// route-versus-body ambiguity and a mass-assignment vector. The portal identifier
/// arrives from the request-scoped portal context, never from the body: the legacy
/// <c>Users</c> table has no portal column at all (per-portal facts live on
/// <c>UserPortals</c>), and accepting a caller-supplied portal would be a
/// multi-tenant isolation defect. Caller privilege arrives from the current-user
/// abstraction and the caller's role claims, never from the body; the legacy screen
/// derives it from two separate facts, <c>IsAdmin</c> (the caller holds the
/// administrator role or is a super user) and <c>IsUser</c> (the routed target is the
/// caller), and a body-supplied privilege flag would be trivially forged. Also
/// absent, each for its own reason: any hash, salt, work factor or storage-format
/// member, because hashing belongs to the infrastructure password hasher and a
/// request never carries a hash; any password-retrieval shape, because retrieval is
/// not carried forward at all; the administrative force-password-change flag, which
/// the legacy change path clears as a side effect
/// (<c>UserController.vb</c> L113) and which the membership screen sets as its own
/// separate action; password expiry and expiry-reminder, which are host-level
/// settings and out of scope, and whose legacy labels are display-only chrome fed by
/// the user detail response; any CAPTCHA member, because the legacy CAPTCHA control
/// library is out of scope; the legacy status enumerations, because failure travels
/// generically as a result code and message pair and is translated to RFC 7807
/// problem details at the API edge; the hydration and dirty-tracking flags, which the
/// object-relational materialiser replaces outright; the correlation identifier,
/// which travels in the <c>X-Correlation-Id</c> header; audit stamps, which have no
/// column in this schema; paging members, which the shared paged contracts own; and
/// any domain entity, because no entity crosses the wire in either direction.
/// </para>
/// <para>
/// THE IDENTITY-SEED TRAP, WHICH BINDS EVEN THOUGH NO IDENTIFIER APPEARS HERE.
/// The legacy schema seeds <c>Portals.PortalID</c> with <c>IDENTITY (-1, 1)</c>
/// (<c>01.00.00.SqlDataProvider</c> L77), so -1 is a real portal identifier, and it
/// seeds <c>Roles.RoleID</c>, <c>Tabs.TabID</c> and <c>Modules.ModuleID</c> with
/// <c>IDENTITY (0, 1)</c>, so 0 is a real identifier too - while the legacy sentinel
/// for a missing integer is -1 (<c>Null.vb</c> L41-L45) and <c>IsNull(-1)</c> is
/// true. Nothing downstream of this request may therefore treat an identifier of 0 or
/// -1 as absent; absence is expressed by a nullable type, never by a magic value.
/// </para>
/// <para>
/// THE MEASURED VALIDATION RULES ARE RECORDED HERE AND IMPLEMENTED IN THE VALIDATOR.
/// This type states no rule and enforces none: it declares no validation attribute,
/// no self-validation hook and no normalising accessor. The rules below are
/// transcribed from <c>Website/admin/Users/Password.ascx.vb</c> with their measured
/// line numbers so that <c>ChangePasswordRequestValidator</c> can reproduce them rule
/// for rule and message for message, as functional parity requires. The names in the
/// outcome column are members of the legacy <c>PasswordUpdateStatus</c> enumeration
/// (<c>Library/Components/Users/Membership/PasswordUpdateStatus.vb</c>), whose eight
/// members are implicitly valued 0 to 7 - note that <c>Success</c> is 0 there, unlike
/// the user-creation status enumeration where success is 13. That enumeration is not
/// reproduced in the target; it is listed only so the validator and service can emit
/// equivalent failure messages.
/// </para>
/// <para>
/// CHANGE flow - the legacy <c>cmdUpdate_Click</c> handler, L269-L307. The order
/// matters: each legacy check exits on first failure, so a request that breaks
/// several rules must report the first one in this sequence.
/// </para>
/// <list type="table">
///   <item>
///     <term>L272</term>
///     <description>
///     The new password and its confirmation must be equal, otherwise
///     <c>PasswordMismatch</c>.
///     </description>
///   </item>
///   <item>
///     <term>L278</term>
///     <description>
///     The new password must satisfy the policy check described below, otherwise
///     <c>PasswordInvalid</c>.
///     </description>
///   </item>
///   <item>
///     <term>L284</term>
///     <description>
///     ONLY WHEN THE CALLER IS NOT AN ADMINISTRATOR, the current password must be
///     supplied, otherwise <c>PasswordMissing</c>. The measured guard is
///     <c>Not IsAdmin And txtOldPassword.Text = ""</c>. An administrator changing
///     another user's password supplies no current password at all - the legacy screen
///     hides that row for an administrator editing someone else (L150-L152) - so
///     making it unconditionally required would break administrative password
///     changes.
///     </description>
///   </item>
///   <item>
///     <term>L290</term>
///     <description>
///     ONLY WHEN THE CALLER IS NOT AN ADMINISTRATOR, the new password must differ
///     from the current one, otherwise <c>PasswordNotDifferent</c>. The measured
///     guard is <c>Not IsAdmin And txtNewPassword.Text = txtOldPassword.Text</c>. An
///     administrator is permitted to set a new password equal to the old one.
///     </description>
///   </item>
///   <item>
///     <term>L300</term>
///     <description>
///     Execution: the legacy call passes the current and new passwords through to the
///     membership provider, returning <c>Success</c> or <c>PasswordResetFailed</c>.
///     </description>
///   </item>
/// </list>
/// <para>
/// RESET flow - the legacy <c>cmdReset_Click</c> handler, L237-L257.
/// </para>
/// <list type="table">
///   <item>
///     <term>L239</term>
///     <description>
///     The answer variable is seeded with the empty string, and that empty string is
///     what reaches the provider whenever the guard below does not fire.
///     </description>
///   </item>
///   <item>
///     <term>L240</term>
///     <description>
///     The answer is required only under a compound guard,
///     <c>RequiresQuestionAndAnswer And Not IsAdmin</c> - so it is required only when
///     the membership configuration demands a question and answer AND the caller is
///     not an administrator. Because the measured configuration sets
///     <c>requiresQuestionAndAnswer</c> to false, the answer is never required in the
///     observed installation and an otherwise empty reset request is legal. This is
///     precisely why <c>Operation</c> exists: see its own documentation.
///     </description>
///   </item>
///   <item>
///     <term>L241</term>
///     <description>
///     Inside that guard, a blank answer yields <c>InvalidPasswordAnswer</c>.
///     </description>
///   </item>
///   <item>
///     <term>L249</term>
///     <description>
///     Execution: a wrong answer surfaces as <c>InvalidPasswordAnswer</c> (L251) and
///     any other failure as <c>PasswordResetFailed</c> (L253).
///     </description>
///   </item>
/// </list>
/// <para>
/// CHANGE QUESTION AND ANSWER flow - the legacy <c>cmdUpdateQA_Click</c> handler,
/// L319-L345. All three members are unconditionally required, in this order.
/// </para>
/// <list type="table">
///   <item>
///     <term>L321</term>
///     <description>
///     The current password is required, otherwise <c>PasswordInvalid</c>.
///     </description>
///   </item>
///   <item>
///     <term>L326</term>
///     <description>
///     The new question is required, otherwise <c>InvalidPasswordQuestion</c>.
///     </description>
///   </item>
///   <item>
///     <term>L331</term>
///     <description>
///     The new answer is required, otherwise <c>InvalidPasswordAnswer</c>.
///     </description>
///   </item>
///   <item>
///     <term>L337-L338</term>
///     <description>
///     Execution. Note that L337 resolves the target user from the ambient portal and
///     user identifiers rather than from the submitted form, which is the legacy
///     precedent for sourcing both from the route and the portal context here.
///     </description>
///   </item>
/// </list>
/// <para>
/// THE PASSWORD POLICY, PRESERVED VERBATIM AND OWNED BY THE VALIDATOR. Measured from
/// the membership provider registration in <c>Website/release.config</c> (L236-L247):
/// minimum length SEVEN; minimum non-alphanumeric characters ZERO; question and
/// answer NOT required; unique email NOT enforced. Those four numbers are recorded
/// here for the validator author and appear nowhere in this file as code. They must
/// not be tightened - no minimum of twelve, no required symbol, no character-class
/// requirement, no breach-list check - because tightening a password policy during a
/// migration locks out existing users, and any hardening is a separate, explicit
/// decision.
/// </para>
/// <para>
/// A LATENT DEFECT IN THE LEGACY POLICY CHECK, AND THE TARGET RESOLUTION.
/// <c>UserController.ValidatePassword</c> (L1067-L1091) runs three checks: the length
/// is less than the configured minimum (L1073); the count of non-alphanumeric matches
/// of the pattern <c>[^0-9a-zA-Z]</c> is fewer than the configured minimum (L1079);
/// and, only when a strength pattern is configured, the password matches that pattern
/// (L1084). The third branch ASSIGNS the result at L1086 instead of combining it with
/// the first two, so a password that fails the length or non-alphanumeric check but
/// matches the strength pattern would be wrongly accepted. The defect is unreachable
/// in the observed installation, because neither
/// <c>Website/release.config</c> nor <c>Website/development.config</c> declares a
/// strength pattern at all. The target must combine all three checks with logical
/// AND. This is recorded for the validator author; there is no logic here to fix.
/// </para>
/// <para>
/// NO LENGTH CEILING IS INHERITED. The original column was
/// <c>[Password] [nvarchar] (20) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> L106),
/// and the legacy markup repeats that ceiling as a twenty-character input limit on
/// all seven of its text boxes. A password hash column supersedes both, so no
/// schema-derived maximum length applies to any member of this type and none is
/// declared. The password question and answer are not stored in this schema at all -
/// they live in the externally installed membership tables that the new user entity
/// maps alongside - so they inherit no length truth either. Any bound the product
/// wants belongs in the validator, and it must be generous.
/// </para>
/// <para>
/// THE PASSWORD STORE CHANGES, AND SO DOES WHAT A RESET RETURNS. The legacy store is
/// reversible: the membership provider is registered with an encrypted password
/// format and password retrieval enabled, and the key that decrypts every stored
/// password is committed to the legacy configuration file in the clear. The target
/// replaces that with one-way password hashing, and the documented transition is to
/// re-hash on the first successful login with an administrative reset as the
/// fallback. Retrieval is NOT carried forward: the legacy entry point
/// <c>UserController.GetPassword</c> (L433) has no target equivalent, by design, and
/// no member of this folder asks for a password back. Reset IS carried forward,
/// because the same configuration enables password reset, and the reset flow this
/// type serves is exactly the administrative fallback named above - do not conflate
/// the two flags. One measured caveat: the legacy
/// <c>UserController.ResetPassword</c> (L906) is structurally identical to the
/// retrieval method - both assign to the user's password member and return it as a
/// string (L915) - so the legacy reset returns generated plaintext to its caller. The
/// target preserves the reset OPERATION and drops that RETURN VALUE; a generated
/// credential is delivered out of band and never through this endpoint's response.
/// </para>
/// </remarks>
public sealed class ChangePasswordRequest
{
    // MIGRATION: This type is the union of three legacy operations rather than three
    // request types, because the plan fixes this folder at eight files. The
    // combination of members that is legal for each operation is enforced by
    // Application/Validation/ChangePasswordRequestValidator.cs, not here.
    //
    // MIGRATION: Every member is nullable and NONE is initialised to string.Empty,
    // which deviates from the usual convention for request DTOs in this folder. The
    // deviation is deliberate and measured: null means "this flow does not use the
    // field" while the empty string means "submitted blank", and the legacy guards
    // test explicitly against the empty string. Normalising either direction would
    // break the validator.
    //
    // MIGRATION: The target user identifier is route-sourced, the portal identifier
    // is resolved from the request-scoped portal context, and caller privilege is
    // resolved from the current-user abstraction and the caller's role claims. None
    // of the three is a member of this request; a body-supplied identifier would be a
    // mass-assignment vector, a body-supplied portal would break tenant isolation,
    // and a body-supplied privilege flag would be trivially forged.
    //
    // MIGRATION: No hashing, salting, comparison, length measurement, complexity
    // measurement, trimming or normalisation happens in this type. The password
    // hasher is an infrastructure concern that this layer cannot even reference, and
    // every rule is a validator concern. The accessors below are plain automatic
    // properties for exactly that reason.
    //
    // MIGRATION: The reversible encrypted password store, whose decryption key is
    // committed to the legacy configuration in the clear, is replaced by one-way
    // hashing, with re-hash on first successful login and administrative reset as the
    // fallback. Password RETRIEVAL is dropped outright and has no target equivalent;
    // password RESET is kept, because the same legacy configuration enables it and it
    // is the administrative fallback - but the legacy reset's plaintext return value
    // is dropped, so this endpoint's response carries a status and nothing more.
    //
    // MIGRATION: Three legacy affordances are deliberately not reproduced. The
    // twenty-character password ceiling, present both on the original column and as an
    // input limit on all seven legacy text boxes, is superseded by the hash column and
    // is not restated on any member here or in the validator. The CAPTCHA control is
    // out of scope, so no CAPTCHA member exists on this request. Password expiry and
    // its reminder are host-level settings and out of scope; the legacy screen's
    // last-changed and expires labels were display-only chrome and belong to the user
    // detail response.
    //
    // MIGRATION: The password policy is preserved verbatim rather than hardened -
    // minimum length seven, zero required non-alphanumeric characters, no question and
    // answer requirement, email uniqueness not enforced - because tightening it during
    // a migration would lock out existing users. The one corrected behaviour is the
    // legacy policy check's third branch, which assigned its result instead of
    // combining it, and which the target must combine with logical AND. Both the
    // preserved numbers and the correction are recorded in this type's documentation
    // for the validator author and appear nowhere here as code.

    /// <summary>
    /// The value of <see cref="Operation"/> that selects the change-password flow:
    /// the legacy <c>pnlChange</c> panel.
    /// </summary>
    public const string OperationChange = "change";

    /// <summary>
    /// The value of <see cref="Operation"/> that selects the reset-password flow: the
    /// legacy <c>pnlReset</c> panel, and the administrative fallback for a credential
    /// that cannot be verified against the new hash.
    /// </summary>
    public const string OperationReset = "reset";

    /// <summary>
    /// The value of <see cref="Operation"/> that selects the change-question-and-answer
    /// flow: the legacy <c>pnlQA</c> panel.
    /// </summary>
    public const string OperationChangeQuestionAndAnswer = "change-question-and-answer";

    /// <summary>
    /// Which of the three legacy operations this request performs. Expected to be one
    /// of <see cref="OperationChange"/>, <see cref="OperationReset"/> or
    /// <see cref="OperationChangeQuestionAndAnswer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: all three. This member replaces the legacy screen's three submit buttons -
    /// <c>cmdUpdate</c>, <c>cmdReset</c> and <c>cmdUpdateQA</c> - each of which
    /// unambiguously identified its own operation because it was wired to its own
    /// handler. A single endpoint has no equivalent signal, so the intent is stated
    /// explicitly rather than inferred from which members happen to be populated.
    /// </para>
    /// <para>
    /// WHY AN EXPLICIT DISCRIMINATOR RATHER THAN INFERENCE. Inference is genuinely
    /// ambiguous for one measured case. Because the observed membership configuration
    /// does not require a question and answer, the legacy reset handler's compound
    /// guard never fires and its answer variable keeps the empty string it was seeded
    /// with (L239-L240), so a reset carries no field at all in that installation. An
    /// empty request body would therefore have to be read as "reset this user's
    /// password" - meaning any caller that failed to populate its fields would
    /// silently trigger a destructive credential reset. Naming the operation removes
    /// that hazard and lets the validator and the service branch on a stated intent.
    /// </para>
    /// <para>
    /// It is a string and not an enumeration on purpose: the plan's domain
    /// enumerations do not include a password-operation kind, and adding one would add
    /// a file this folder's fixed contents do not contain. The three legal values are
    /// published as the constants above so that the validator, the service, the
    /// controller, the integration tests and the Angular model share one definition
    /// instead of five copies of three magic strings. Rejecting an absent or
    /// unrecognised value is a validator rule, not a concern of this type.
    /// </para>
    /// </remarks>
    public string? Operation { get; set; }

    /// <summary>
    /// The caller's existing password, supplied to re-authenticate before the change
    /// is applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: change, and change question and answer. LEGACY CONTROLS:
    /// <c>txtOldPassword</c> in the <c>pnlChange</c> panel, and <c>txtQAPassword</c> in
    /// the <c>pnlQA</c> panel.
    /// </para>
    /// <para>
    /// The two legacy text boxes are merged into one member because they carry the same
    /// thing for the same purpose - the credential the caller already holds, presented
    /// as proof of identity. Contrast <see cref="PasswordAnswer"/> and
    /// <see cref="NewPasswordAnswer"/>, which stay separate precisely because their
    /// meanings differ.
    /// </para>
    /// <para>
    /// Required for the change flow ONLY when the caller is not an administrator
    /// (L284), and unconditionally required for the change-question-and-answer flow
    /// (L321). Unused by the reset flow. Verification is performed by the
    /// infrastructure password hasher through the application service; nothing here
    /// compares, hashes or measures it.
    /// </para>
    /// </remarks>
    public string? CurrentPassword { get; set; }

    /// <summary>
    /// The replacement password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: change. LEGACY CONTROL: <c>txtNewPassword</c> in the <c>pnlChange</c>
    /// panel.
    /// </para>
    /// <para>
    /// Must equal <see cref="ConfirmPassword"/> (L272), must satisfy the preserved
    /// policy (L278) and, when the caller is not an administrator, must differ from
    /// <see cref="CurrentPassword"/> (L290). All three are validator rules. Unused by
    /// the reset flow, which generates a credential server-side, and unused by the
    /// change-question-and-answer flow, which leaves the password untouched.
    /// </para>
    /// </remarks>
    public string? NewPassword { get; set; }

    /// <summary>
    /// The replacement password, repeated, to catch a typing error before it becomes a
    /// lockout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: change. LEGACY CONTROL: <c>txtNewConfirm</c> in the <c>pnlChange</c>
    /// panel.
    /// </para>
    /// <para>
    /// The equality check against <see cref="NewPassword"/> is the legacy screen's
    /// first check (L272) and belongs to the validator. It is deliberately not an
    /// accessor-side comparison here: a request DTO that validated itself would place
    /// a rule outside the one place rules are meant to live, and would report failure
    /// through an exception rather than through the problem-details response the API
    /// contract requires.
    /// </para>
    /// </remarks>
    public string? ConfirmPassword { get; set; }

    /// <summary>
    /// The answer to the user's EXISTING stored password question, supplied to
    /// authorise a reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: reset. LEGACY CONTROL: <c>txtAnswer</c> in the <c>pnlReset</c> panel, read
    /// at L245 and passed to the reset call at L249.
    /// </para>
    /// <para>
    /// This is an EXISTING value being checked, not a new value being stored, which is
    /// why it is a separate member from <see cref="NewPasswordAnswer"/>. Conflating the
    /// two would make the reset and change-question-and-answer flows indistinguishable
    /// on the wire and would let a caller overwrite the stored answer while pretending
    /// to answer it.
    /// </para>
    /// <para>
    /// Required only under the legacy compound guard at L240 - the membership
    /// configuration requires a question and answer AND the caller is not an
    /// administrator - so in the observed installation, which does not require a
    /// question and answer, it is optional and the legacy code forwards the empty
    /// string. The stored question itself is never submitted: the legacy panel renders
    /// it as a read-only label, so it belongs to the user detail response rather than
    /// to this request.
    /// </para>
    /// </remarks>
    public string? PasswordAnswer { get; set; }

    /// <summary>
    /// The replacement password question.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: change question and answer. LEGACY CONTROL: <c>txtEditQuestion</c> in the
    /// <c>pnlQA</c> panel, passed as the new question at L338.
    /// </para>
    /// <para>
    /// Unconditionally required for this flow (L326). Note that the whole flow is
    /// configuration-gated in the legacy screen: its panel is shown only when the
    /// membership configuration requires a question and answer and the caller is
    /// editing their own account (L187), so with the observed configuration the panel
    /// is never rendered and the flow is latent. It is carried forward because it is
    /// reachable under a different membership configuration, and because dropping a
    /// reachable operation would breach functional parity.
    /// </para>
    /// </remarks>
    public string? NewPasswordQuestion { get; set; }

    /// <summary>
    /// The replacement password answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: change question and answer. LEGACY CONTROL: <c>txtEditAnswer</c> in the
    /// <c>pnlQA</c> panel, passed as the new answer at L338.
    /// </para>
    /// <para>
    /// Unconditionally required for this flow (L331). Distinct from
    /// <see cref="PasswordAnswer"/> by design: this member SETS the stored answer,
    /// whereas that one PROVES knowledge of it. The legacy method's own parameter
    /// documentation is explicit on the point, describing its question and answer
    /// arguments as the new values while its password argument is the existing
    /// credential.
    /// </para>
    /// </remarks>
    public string? NewPasswordAnswer { get; set; }
}
