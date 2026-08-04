namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for
/// <c>POST /api/v1/portals/{portalId}/users/{userId}/password</c>. Carries every
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
/// <c>POST /api/v1/portals/{portalId}/users/{userId}/password</c> from structured
/// logging, because the
/// non-functional requirement is structured logging that excludes sensitive data;
/// and the rate-limiting configuration must cover this route alongside the
/// authentication routes, because comparing <c>CurrentPassword</c> against the stored
/// hash makes this endpoint a credential-verification surface and therefore a
/// brute-force target. The response to this request carries a status only - never
/// password material, never a generated credential, never a hint.
/// </para>
/// <para>
/// ONE REQUEST TYPE, TWO SUPPORTED OPERATIONS. The legacy screen
/// <c>Website/admin/Users/Password.ascx</c> declares three independent panels, each a
/// separate operation: <c>pnlChange</c> (old, new and confirm), <c>pnlReset</c>
/// (answer the stored password question) and <c>pnlQA</c> (re-authenticate with the
/// current password, then replace the password question and answer). Two of the three
/// are carried forward - a self-service CHANGE and an administrative RESET - and the
/// third is not, for the reason given in the next paragraph. This DTO folder is fixed
/// at eight files and holds no ResetPasswordRequest, so this single type is the union
/// of both surviving field sets. Every member is consequently nullable, and
/// <c>ChangePasswordRequestValidator</c> - not this type - decides which combination
/// of members is legal for the declared <c>Operation</c>, using conditional rule
/// sets. Do not split this type into two; doing so would add a file the plan does
/// not contain.
/// </para>
/// <para>
/// THE QUESTION-AND-ANSWER OPERATION IS NOT CARRIED FORWARD, AND ITS MEMBERS ARE
/// REMOVED RATHER THAN LEFT INERT. The recovery pair has no target counterpart at
/// all: <c>IUserService</c> records that the legacy question-and-answer member has no
/// counterpart, that no member declares a question or answer parameter, and that the
/// pair's only real purpose was to guard credential retrieval - which is dropped
/// outright, because the store is now one-way. Declaring a third operation constant
/// with an existing-answer member and a new-question and new-answer pair would publish
/// an operation whose entire effect is to store two strings that nothing stores and
/// nothing can check, which is strictly worse than an absent feature: a caller would
/// receive a success response for work that did not happen. The measured
/// legacy behaviour that makes the removal safe rather than merely convenient: the
/// provider is registered with the pair not required
/// (<c>Website/release.config</c> L241), the legacy panel was itself gated on that
/// same flag and on the caller editing their own account (L187), so the panel was
/// never rendered in the observed installation, and the reset handler's answer guard
/// (L240) is compound on the same flag and therefore never fired either. The bound
/// policy flag is consequently unsatisfiable in the target, and
/// <c>PasswordPolicyOptions.Validate</c> rejects a deployment that sets it rather
/// than letting it appear enforced.
/// </para>
/// <para>
/// THE TWO OPERATIONS ARE SEPARATED BY AUTHORISATION AS WELL AS BY FIELDS, AND THAT
/// SEPARATION IS THE POINT. A CHANGE is self-service: the caller proves possession of
/// the account by presenting its current credential, so <see cref="CurrentPassword"/>
/// is unconditionally required for that operation and the application service verifies
/// it against the stored hash before anything is written. A RESET is administrative:
/// the caller cannot present a credential they do not know, so proof of possession is
/// replaced by proof of privilege - and <see cref="CurrentPassword"/> must therefore be
/// ABSENT on a reset, because a value there would be meaningless and accepting it would
/// blur the two authorisation models. Where that privilege is enforced, precisely:
/// <c>UsersController.ChangePasswordAsync</c> evaluates the portal-administrator policy
/// for a request whose operation is <see cref="OperationReset"/> and answers
/// <c>403 Forbidden</c> when it is not satisfied, before the body is validated and
/// before the service is called. It is evaluated there and not by an attribute because
/// the requirement depends on the submitted operation, which an attribute cannot see;
/// and not in this layer, because deciding privilege needs the caller's identity and
/// the resolved tenant. Neither this type nor the validator can enforce it, and neither
/// claims to. If the two operations were not named separately, the validator could not
/// demand proof of possession on the one that can supply it without deleting the
/// administrative path that cannot - and a reset with no proof of any kind, neither a
/// current credential nor a privilege, would make an empty body a complete, destructive
/// credential reset. The legacy administrative change - an administrator editing another
/// account, for whom the current-credential row was hidden (L150-L152) - maps onto
/// RESET, which is what it always was, and it carries the same administrator
/// requirement the legacy screen imposed.
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
/// as "not supplied", which is what reproduces legacy behaviour. And deserialisation
/// must not collapse the distinction: never normalise a <c>null</c> to <c>""</c> or a
/// <c>""</c> to <c>null</c> on the way in, and never give a member an initialiser. The
/// application-wide <c>Never</c> ignore condition does not touch any of this,
/// because it governs WRITING only and no instance of this type is ever written to a
/// response - which is also why it must stay that way: this type is inbound-only, and
/// echoing one back would publish credential material.
/// </para>
/// <para>
/// WHAT THIS TYPE DELIBERATELY DOES NOT CARRY. The target user identifier arrives
/// from the route, never from the body: a body identifier would create a
/// route-versus-body ambiguity and a mass-assignment vector. The portal identifier
/// arrives from the request-scoped portal context, never from the body: the legacy
/// <c>Users</c> table has no portal column at all (per-portal facts live on
/// <c>UserPortals</c>), and accepting a caller-supplied portal would be a
/// multi-tenant isolation defect. Caller privilege arrives from the portal-administrator
/// authorisation policy, which resolves the caller's identity against the addressed
/// portal's administrator-role assignment, never from the body; the legacy screen
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
/// The legacy schema declares <c>Portals.PortalID</c> as <c>IDENTITY (-1, 1)</c>
/// (<c>01.00.00.SqlDataProvider</c> L77), so the seed - the first value the column
/// generates - is -1, while the shipped default portal is inserted with an explicit
/// <c>PortalID</c> of 0. Both are therefore valid keys, and so is every value above
/// them. <c>Roles.RoleID</c>, <c>Tabs.TabID</c> and <c>Modules.ModuleID</c> are
/// declared <c>IDENTITY (0, 1)</c>, so 0 is a real identifier there too - while the
/// legacy sentinel for a missing integer is -1 (<c>Null.vb</c> L41-L45) and
/// <c>IsNull(-1)</c> is true. Nothing downstream of this request may therefore treat
/// an identifier of 0 or -1 as absent; absence is expressed by a nullable type, never
/// by a magic value.
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
///     hides that row for an administrator editing someone else (L150-L152). In the
///     target that administrative path is the RESET operation rather than a
///     privilege-conditional variant of CHANGE, so this guard becomes unconditional
///     for CHANGE: every request that declares CHANGE is asserting self-service and
///     must present the credential it is replacing. No workflow is lost, because the
///     path this guard exempted is reachable under the other operation.
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
/// RESET flow - the legacy <c>cmdReset_Click</c> handler, L237-L257, and the one place
/// this migration deliberately changes the field set rather than the rules.
/// </para>
/// <list type="table">
///   <item>
///     <term>L239-L241</term>
///     <description>
///     The legacy handler seeded an answer variable with the empty string, required it
///     only under the compound guard
///     <c>RequiresQuestionAndAnswer And Not IsAdmin</c>, and rejected a blank one
///     inside that guard. Because the measured configuration sets
///     <c>requiresQuestionAndAnswer</c> to false, the guard never opened, the empty
///     string always reached the provider, and an entirely empty reset request was
///     legal. None of this is reproduced: the recovery pair is not carried forward, so
///     there is no answer member to seed, guard or reject.
///     </description>
///   </item>
///   <item>
///     <term>L249-L253</term>
///     <description>
///     Execution: the legacy call generated a credential inside the provider and
///     returned it, surfacing a wrong answer as <c>InvalidPasswordAnswer</c> (L251)
///     and any other failure as <c>PasswordResetFailed</c> (L253). The generation is
///     NOT carried forward. A generated credential has to be transmitted to be
///     useful, the mail subsystem that would transmit it is excluded from this
///     migration, and no endpoint in the target returns, echoes or reconstructs a
///     credential - so a generated credential would be knowable to nobody, including
///     the account's own holder, and the reset would be indistinguishable from
///     locking the account out. The target reset therefore takes the replacement
///     credential explicitly, in <see cref="NewPassword"/> and
///     <see cref="ConfirmPassword"/>, and applies the same policy and the same
///     confirmation comparison the change flow applies. Only the persistence outcome
///     <c>PasswordResetFailed</c> survives, as a service-layer failure code.
///     </description>
///   </item>
///   <item>
///     <term>L237</term>
///     <description>
///     What the reset keeps from the legacy handler is exactly what made it a reset:
///     it does not ask for the credential being replaced. That absence is now
///     asserted rather than merely permitted - a reset carrying a current password is
///     rejected - because the two operations are separated by authorisation and a
///     value in that member would imply the wrong one.
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
/// NO SCHEMA LENGTH CEILING IS INHERITED, AND THE ONE CEILING THAT APPLIES IS A WORK
/// BOUND RATHER THAN AN ALGORITHM LIMIT. The original column was
/// <c>[Password] [nvarchar] (20) NOT NULL</c>
/// (<c>01.00.00.SqlDataProvider</c> L106), and the legacy markup repeats that ceiling
/// as a twenty-character input limit on all seven of its text boxes. A password hash
/// column supersedes both, so no schema-derived maximum length applies to any member
/// of this type and none is declared. The single applicable bound is
/// <c>CredentialBounds.MaximumByteLength</c>, 256 UTF-8 bytes, shared by every
/// credential entry point and by the infrastructure hasher so the boundary and the
/// primitive cannot disagree. It is NOT BCrypt's 72-byte significance limit: the
/// hasher uses the enhanced hash and verify pair with a SHA-384 pre-hash, so the whole
/// of the input contributes to the digest and two credentials agreeing on their first
/// 72 bytes do not verify interchangeably. What 256 bytes bounds is the work an
/// unauthenticated caller can ask the server to do, and the bound is measured in
/// ENCODED BYTES rather than characters because a character outside the ASCII range
/// occupies between two and four of them - which is also the unit the hashing
/// primitive itself consumes. At more than twelve times the legacy input ceiling it
/// can reject nothing the legacy screen accepted.
/// </para>
/// <para>
/// THE PASSWORD STORE CHANGES, AND SO DOES WHAT A RESET RETURNS. The legacy store is
/// reversible: the membership provider is registered with an encrypted password
/// format and password retrieval enabled, and the key that decrypts every stored
/// password is committed to the legacy configuration file in the clear. The target
/// replaces that with one-way password hashing, and THE TRANSITION FOR EXISTING
/// CREDENTIALS IS AN ADMINISTRATIVE RESET, AND NOTHING ELSE. A re-hash on the first
/// successful sign-in - the intent recorded in AAP section 0.7.5.5 - cannot execute
/// here: no component in this solution verifies a credential held under the legacy
/// reversible scheme, and that same section forbids building one, so there is no
/// verification from which a lazy upgrade could follow. Retrieval is NOT carried
/// forward either: the legacy entry point <c>UserController.GetPassword</c> (L433) has
/// no target equivalent, by design, and no member of this folder asks for a password
/// back. Reset IS carried forward, because the same configuration enables password
/// reset, and the reset flow this type serves is therefore the SOLE migration path for
/// a pre-existing account rather than a fallback from one - which makes this request
/// shape load-bearing for the migration rather than incidental to it. Do not conflate
/// the two flags. One measured caveat: the legacy
/// <c>UserController.ResetPassword</c> (L906) is structurally identical to the
/// retrieval method - both assign to the user's password member and return it as a
/// string (L915) - so the legacy reset returns generated plaintext to its caller. The
/// target preserves the reset OPERATION and drops both the generation and that return
/// value: the administrator supplies the replacement credential explicitly, so there is
/// nothing for the response to carry. No out-of-band channel could carry it either -
/// the mail subsystem is excluded from this migration - and a credential nobody can
/// learn is a lockout rather than a reset.
/// </para>
/// </remarks>
public sealed class ChangePasswordRequest
{
    // MIGRATION: every divergence this contract embodies - one request type for two
    // operations, the removed recovery pair, the authorisation split between possession and
    // privilege, the deliberate null-versus-empty-string distinction, the route-sourced
    // identifiers, the shared 256-byte credential bound and the preserved password policy -
    // is stated once in this type's <remarks> above, with its measured legacy line numbers.
    // It is not restated here: two copies of one rationale drift apart, and the copy a
    // reader happens to find first then decides what they believe.

    /// <summary>
    /// The value of <see cref="Operation"/> that selects the change-password flow:
    /// the legacy <c>pnlChange</c> panel.
    /// </summary>
    public const string OperationChange = "change";

    /// <summary>
    /// The value of <see cref="Operation"/> that selects the reset-password flow: the
    /// legacy <c>pnlReset</c> panel, and the sole migration path for a credential stored
    /// under the legacy reversible scheme, which nothing in this solution can verify.
    /// </summary>
    /// <remarks>
    /// A request carrying this value must satisfy the portal-administrator policy, which
    /// <c>UsersController.ChangePasswordAsync</c> evaluates before the body is validated.
    /// </remarks>
    public const string OperationReset = "reset";

    // MIGRATION: There are deliberately only TWO operation constants. A third for the legacy
    // pnlQA panel would publish an operation whose entire effect is to store a recovery
    // question and answer that the target does not store and cannot check. Declaring no
    // constant for it - rather than declaring one and rejecting it - means the three places
    // that share these values (the validator, the service and the Angular model) cannot name
    // an operation that does not exist.

    /// <summary>
    /// Which of the two supported operations this request performs. Expected to be either
    /// <see cref="OperationChange"/> or <see cref="OperationReset"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: both. This member replaces the legacy screen's submit buttons -
    /// <c>cmdUpdate</c> and <c>cmdReset</c>, each of which unambiguously identified its
    /// own operation because it was wired to its own handler. A single endpoint has no
    /// equivalent signal, so the intent is stated explicitly rather than inferred from
    /// which members happen to be populated. The legacy screen's third button,
    /// <c>cmdUpdateQA</c>, has no counterpart because its operation is not carried
    /// forward.
    /// </para>
    /// <para>
    /// WHY AN EXPLICIT DISCRIMINATOR RATHER THAN INFERENCE. Inference cannot separate
    /// these two operations safely. Both carry a new credential and its confirmation;
    /// they differ only in whether the current credential is present, so inference would
    /// have to read "no current credential supplied" as "reset this account's
    /// credential" - which is exactly what a caller who simply failed to populate that
    /// field would send. A stated intent means the validator can demand the current
    /// credential for a change and forbid it for a reset, and the service can require a
    /// different authorisation for each, neither of which is possible while the operation
    /// is a guess.
    /// </para>
    /// <para>
    /// It is a string and not an enumeration on purpose: the plan's domain
    /// enumerations do not include a password-operation kind, and adding one would add
    /// a file this folder's fixed contents do not contain. The two legal values are
    /// published as the constants above so that the validator, the service, the
    /// controller, the integration tests and the Angular model share one definition
    /// instead of five copies of two magic strings. Rejecting an absent or
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
    /// FLOW: change only. LEGACY CONTROL: <c>txtOldPassword</c> in the
    /// <c>pnlChange</c> panel. The legacy <c>txtQAPassword</c> box in the
    /// <c>pnlQA</c> panel carried the same thing for the same purpose and would have
    /// merged into this member, but that panel's operation is not carried forward.
    /// </para>
    /// <para>
    /// UNCONDITIONALLY REQUIRED FOR A CHANGE, AND FORBIDDEN ON A RESET. The legacy
    /// change guard at L284 was compound - <c>Not IsAdmin And txtOldPassword.Text = ""</c>
    /// - because the same button served both a self-service change and an
    /// administrator changing someone else's credential, and the screen hid this row
    /// for the latter (L150-L152). The target splits those two cases across the two
    /// operations instead of across a privilege test, so the guard becomes
    /// unconditional for CHANGE: declaring CHANGE is declaring self-service, and
    /// self-service means presenting the credential being replaced. On a RESET this
    /// member must be absent, because an administrator does not hold the credential and
    /// a value here would imply an authorisation model the reset does not use.
    /// </para>
    /// <para>
    /// Verification is performed by the infrastructure password hasher through the
    /// application service; nothing here compares, hashes or measures it. It is never
    /// policy-checked either: it is an EXISTING stored credential, and the shipped
    /// database seeds the Host and Administrator accounts with four-character and
    /// five-character credentials, both below the configured minimum, so measuring this
    /// member against the policy would leave precisely those accounts unable to change
    /// their own credentials.
    /// </para>
    /// </remarks>
    public string? CurrentPassword { get; set; }

    /// <summary>
    /// The replacement password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: both change and reset. LEGACY CONTROL: <c>txtNewPassword</c> in the
    /// <c>pnlChange</c> panel; the reset panel had no equivalent input because the
    /// legacy provider generated the credential itself.
    /// </para>
    /// <para>
    /// Must equal <see cref="ConfirmPassword"/> (L272), must satisfy the preserved
    /// policy (L278), must fit within the credential hasher's encoded-byte ceiling, and
    /// - when a current credential was supplied - must differ from it (L290). All four
    /// are validator rules.
    /// </para>
    /// <para>
    /// REQUIRED ON A RESET TOO, WHICH IS A DELIBERATE DIVERGENCE. The legacy reset
    /// generated a credential inside the membership provider and returned it to the
    /// caller (L906, returned at L915). Generation is not carried forward: a generated
    /// credential has to be transmitted to be useful, the mail subsystem is excluded
    /// from this migration, and no endpoint returns, echoes or reconstructs a
    /// credential - so a generated value would be knowable to nobody and the reset
    /// would lock the account out rather than restore access to it. The administrator
    /// therefore supplies the replacement explicitly, and it is held to the same policy,
    /// the same confirmation comparison and the same ceiling as a self-service change.
    /// </para>
    /// </remarks>
    public string? NewPassword { get; set; }

    /// <summary>
    /// The replacement password, repeated, to catch a typing error before it becomes a
    /// lockout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLOW: both change and reset. LEGACY CONTROL: <c>txtNewConfirm</c> in the
    /// <c>pnlChange</c> panel.
    /// </para>
    /// <para>
    /// The equality check against <see cref="NewPassword"/> is the legacy screen's
    /// first check (L272) and belongs to the validator. It is deliberately not an
    /// accessor-side comparison here: a request DTO that validated itself would place
    /// a rule outside the one place rules are meant to live, and would report failure
    /// through an exception rather than through the problem-details response the API
    /// contract requires. It applies to a reset as well as to a change, because a reset
    /// now carries an explicitly supplied credential and a typing error there would lock
    /// the account out just as surely.
    /// </para>
    /// </remarks>
    public string? ConfirmPassword { get; set; }

    // MIGRATION: THREE RECOVERY MEMBERS ARE DELIBERATELY ABSENT from the end of this type,
    // and each is named here so that none is later mistaken for an oversight.
    //
    //   PasswordAnswer     - the answer to the EXISTING stored question, read by the
    //                        legacy reset panel at L245 and forwarded at L249.
    //   NewPasswordQuestion - the replacement question, required by the legacy
    //                        question-and-answer panel at L326 and written at L338.
    //   NewPasswordAnswer  - the replacement answer, required at L331 and written at L338.
    //
    // All three belong to the recovery pair, which has no counterpart in the target: the
    // owning service contract states that the legacy question-and-answer member has no
    // counterpart and that no member declares a question or answer parameter, and the
    // pair's only real purpose was to guard credential retrieval, which is dropped
    // outright because the store is now one-way. The measured legacy configuration
    // registers the pair as not required (Website/release.config L241), the panel that
    // wrote it was gated on that same flag (L187) and so never rendered, and the reset
    // handler's answer guard (L240) was compound on the same flag and so never fired -
    // meaning nothing in the observed installation ever supplied, stored or checked any of
    // the three. They are removed rather than kept and ignored, because a member on a
    // published contract is indistinguishable from a supported one, and a caller that
    // supplied a new question would receive a success response for a write that never
    // happened.
}
