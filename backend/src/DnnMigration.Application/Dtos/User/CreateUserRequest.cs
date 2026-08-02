namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for user creation: the request body bound by
/// <c>UsersController</c> on <c>POST /api/v1/users</c>, which answers
/// <c>201 Created</c> with a <c>UserDetailDto</c>.
/// </summary>
/// <remarks>
/// <para>
/// SENSITIVE PAYLOAD - AN INSTANCE MUST NEVER BE LOGGED IN FULL. This is one of
/// only two shapes in this folder permitted to carry a plaintext password, and
/// it is therefore precisely the object a naive request logger would leak. The
/// structured-logging requirement forbids emitting sensitive data, so request
/// logging must redact or omit the password and confirmation members rather than
/// serialising the instance.
/// </para>
/// <para>
/// This type is inert by design: it declares data and nothing else. It performs
/// no validation, no normalisation, no derivation and no hashing, and it holds
/// no lazy or computed accessor. Every rule below is recorded here as measured
/// evidence for the author of
/// <c>Application/Validation/CreateUserRequestValidator.cs</c>, which is the one
/// place those rules are expressed, using FluentValidation.
/// </para>
/// <para>
/// FIELD SET PROVENANCE. The members mirror the legacy create form. The first
/// five reproduce the property-editor surface of <c>Website/admin/Users/User.ascx</c>,
/// declared in the legacy sort order Username, FirstName, LastName, DisplayName,
/// Email; the remainder reproduce the create-only controls of the same page that
/// survive into the target - the authorise checkbox and the password and
/// confirmation inputs. Workflow provenance is
/// <c>Website/admin/Users/User.ascx.vb</c>.
/// </para>
/// <para>
/// FOUR LEGACY CREATE CONTROLS ARE DELIBERATELY NOT REPRODUCED, and their absence
/// is a contract decision rather than an omission. The recovery question and
/// answer inputs, the random-password checkbox and the notify checkbox each
/// backed a capability the owning service contract states is not carried forward:
/// <c>IUserService</c> records that the recovery pair has no counterpart and that
/// no member declares a question or answer parameter, that the generation members
/// are absent because a generated credential has to be transmitted to be useful,
/// and that the notify switch is dropped with the excluded mail subsystem. An
/// earlier revision of this type declared all four anyway, so the wire contract
/// advertised four operations that nothing behind it could perform - a caller
/// could set the generation flag and receive an account whose credential no
/// endpoint would ever disclose, or set the notify flag and be told a message was
/// sent that no subsystem exists to send. The four members are therefore removed
/// rather than left inert, because an inert member on a published contract is
/// indistinguishable from a supported one.
/// </para>
/// <para>
/// MEASURED PASSWORD POLICY, preserved verbatim from the legacy membership
/// provider registration in <c>Website/release.config</c> lines 217 to 249:
/// minimum length 7; minimum non-alphanumeric characters 0; question and answer
/// NOT required; unique email NOT required; application name DotNetNuke.
/// Tightening any of these during the migration would lock existing users out,
/// so the validator must reproduce them exactly and add nothing. The one bound
/// the validator adds is not a policy rule: the credential hasher is BCrypt,
/// which consumes only the first 72 encoded bytes of its input, so a request
/// carrying more than that would have its excess silently ignored and two
/// different credentials could hash identically. That ceiling is a property of
/// the hashing primitive and is enforced consistently by the hasher and by every
/// validator that accepts a credential.
/// </para>
/// <para>
/// MEASURED COLUMN WIDTHS from the terminal schema of the Users table, for the
/// validator's length rules: Username 100, FirstName 50, LastName 50,
/// DisplayName 128, Email 256. Username additionally carries a UNIQUE
/// NONCLUSTERED constraint, added by script 03.00.09, which replaced an earlier
/// unique constraint on Email; Email is therefore NOT unique in the terminal
/// schema, corroborating the provider setting above. No length rule and no
/// uniqueness rule is expressed on this type. The email width is 256 and not the
/// baseline 100 because the column was dropped and re-added wider; the chain is
/// cited in full on the <c>Email</c> member below, and an earlier revision of
/// this paragraph read only the baseline.
/// </para>
/// <para>
/// THE EMAIL WIDTH IS 256, AND AN EARLIER REVISION OF THIS PARAGRAPH SAID 100.
/// The column has two lives and only the second exists today: an original
/// <c>Email nvarchar(100) NOT NULL</c> from <c>01.00.00:L107</c> was REMOVED by
/// the nine-column drop at <c>02.02.01:L50-51</c>, and a replacement
/// <c>Email nvarchar(256) NULL</c> was added at <c>03.00.13:L109-110</c> and
/// back-filled from the membership store. Nothing later alters it, and the
/// terminal <c>AddUser</c> declares <c>@Email nvarchar(256)</c> at
/// <c>04.00.04:L704</c>. The legacy editor attribute of 256 therefore AGREES with
/// the terminal column rather than contradicting it, and
/// <c>Infrastructure/Persistence/Configurations/UserConfiguration.cs</c> maps the
/// column at that width and as nullable. Requiredness on this contract is an
/// API-level rule from the legacy screen's <c>Required(True)</c>, not a
/// restatement of the column's nullability.
/// </para>
/// <para>
/// THE EMAIL SHAPE RULE IS NOT REPRODUCED ON THIS TYPE OR RESTATED FOR THE
/// VALIDATOR AUTHOR. It lives once, in
/// <see cref="Domain.ValueObjects.EmailAddress"/>, which transcribes the legacy
/// constant from <c>Library/Components/Shared/Globals.vb</c> line 132 clause by
/// clause and records its two deliberate departures from it - the legacy
/// four-character cap on the final domain label is replaced by bounded,
/// standards-derived label checks, so modern suffixes such as <c>.museum</c> are
/// accepted. The validators call that type; they declare no pattern of their own,
/// because two copies of one rule is exactly how the create and update paths came
/// to accept different addresses.
/// </para>
/// <para>
/// MEASURED VALIDATION SEQUENCE reproduced by the legacy page: the confirmation
/// is compared first (User.ascx.vb line 152), then password strength
/// (<c>UserController.ValidatePassword</c>). The legacy sequence continued with
/// the recovery question and answer, and only when the provider required them;
/// that step has no target counterpart because the pair is not carried forward,
/// so the sequence ends at strength. LATENT DEFECT to flag for the validator
/// author: <c>ValidatePassword</c> at <c>Library/Components/Users/UserController.vb</c>
/// lines 1067 to 1091 evaluates three rules but its third branch, line 1086,
/// ASSIGNS the regular-expression outcome to the running flag instead of
/// combining it, so a password failing the length or non-alphanumeric rule would
/// still be reported valid whenever a strength expression is configured. It is
/// latent only because the legacy configuration sets no strength expression. The
/// target must combine all three rules conjunctively.
/// </para>
/// <para>
/// SENTINEL CONVENTION at this boundary. The legacy null-sentinel module
/// (<c>Library/Components/Shared/Null.vb</c>) represents an absent integer as -1,
/// an absent boolean as false and, decisively, an absent string as the empty
/// string rather than null. For an inbound shape the question inverts to what
/// the absence of a field means, so members backed by NOT NULL columns are
/// declared non-nullable and seeded with the empty string: an omitted JSON field
/// then deserialises to the same empty string the legacy code treated as absent,
/// and the validator rejects it. Members that are legitimately absent are
/// declared nullable. No serialisation condition that suppresses nulls or
/// default values is applied, because on a request shape that would erase
/// legitimate false values and break round-trip fidelity in tests.
/// </para>
/// <para>
/// DELIBERATE OMISSIONS, each recorded so that none is later mistaken for an
/// oversight. See the inline notes on each group below.
/// </para>
/// </remarks>
public sealed class CreateUserRequest
{
    // ---------------------------------------------------------------------
    // DELIBERATE OMISSIONS
    // ---------------------------------------------------------------------
    //
    // MIGRATION: no tenant identifier is accepted in the body. The legacy user
    // object exposed a portal identifier, but the Users table has no such
    // column - per-portal facts live on the UserPortals table, which is why the
    // target splits UserPortal into its own entity. For an inbound request the
    // only safe source is the route or the scoped IPortalContext: honouring a
    // caller-supplied tenant identifier would let a caller create a user inside
    // another tenant, and tenant isolation is an explicit preservation
    // requirement. UsersController resolves it from IPortalContext and passes it
    // to IUserService as a separate argument. Do not add it here.
    //
    // MIGRATION: no server-assigned identity is accepted. The user key is
    // IDENTITY(1,1); if a caller supplies one it is ignored, not honoured. The
    // wider identity-seed trap is why no absence test anywhere in the target may
    // use "less than or equal to zero" or "equal to -1": the portal key is
    // IDENTITY(-1,1) so -1 is a real key, role and tab keys are IDENTITY(0,1) so
    // 0 is a real key, yet the legacy sentinel for an absent integer is -1 and
    // the legacy null test reports -1 as absent.
    //
    // MIGRATION: no host-level super-user flag is accepted. The Users table
    // carries such a flag as bit NOT NULL defaulting to 0, but accepting it from
    // a caller would be a privilege-escalation vector, and host administration
    // is excluded from this refactor's scope. It is therefore not declared.
    //
    // MIGRATION: no human-verification challenge field is accepted. The legacy
    // create form gated submission behind an image challenge whose control
    // library is an excluded tree (all of Library/Controls, 102 files across ten
    // sub-libraries). The same decision is recorded for the login request, and
    // the two related legacy module-settings keys are correspondingly inert on
    // MembershipSettingsDto.
    //
    // MIGRATION: no creation-status or login-status enum, and no result
    // envelope, is referenced. The legacy creation entry point returned an
    // 18-member status enum whose success member is 13 rather than 0, and it
    // also passed the user object by reference so the caller could read back the
    // assigned key. Both mechanisms are retired: the service returns the
    // Application result envelope, the Api layer translates a failure into an
    // RFC 7807 problem document, and no by-reference parameter appears in any
    // target public API. A request shape references none of that.
    //
    // MIGRATION: no password hash, no cryptographic seed value and no storage
    // format member. The legacy store was reversible - the provider was
    // registered with an encrypted (Triple-DES) storage format and with password
    // retrieval enabled, and the corresponding decryption key is committed in
    // plain sight at Website/release.config lines 89 to 93. That store is
    // replaced by a one-way adaptive hash computed in the Infrastructure layer.
    // Hashing is exclusively an Infrastructure concern and is unreachable from
    // this layer by project reference.
    //
    // MIGRATION: CREDENTIALS THAT ALREADY EXIST ARE MIGRATED BY ADMINISTRATIVE
    // RESET, AND BY NOTHING ELSE. An earlier revision of the note above claimed
    // re-hashing on first successful login with administrative reset as a mere
    // fallback. THAT CLAIM WAS FALSE and is removed rather than softened: the
    // hasher verifies BCrypt digests only, no legacy credential column is mapped
    // by the persistence layer, and no service in this solution can check a
    // submitted password against a value held under the legacy reversible scheme -
    // so the first-login sequence it described could never execute. Every
    // pre-existing account therefore needs an administrative password reset before
    // its owner can sign in. This is a deliberate functional reduction and is
    // recorded in MIGRATION_NOTES.md.
    //
    // MIGRATION: password RETRIEVAL is not carried forward to any endpoint or
    // screen, so there is no retrieval request shape and no password-hint member
    // anywhere in this folder. The legacy retrieval function threw when the
    // feature was disabled and has no target equivalent. Password RESET is a
    // different feature and IS carried forward: the legacy configuration enables
    // reset as well as retrieval, and only retrieval is dropped. Do not conflate
    // the two.
    //
    // MIGRATION: the legacy password inputs carried a short fixed maximum
    // length, and the underlying column was narrow - nvarchar(50) in the
    // terminal schema, narrower still in the baseline before the table rebuild.
    // That ceiling described a plaintext column that no longer exists, so it is
    // deliberately NOT carried onto this type or into its validator.
    //
    // MIGRATION: a DIFFERENT ceiling does apply, and it is net-new rather than
    // ported. Validation/CredentialBounds.cs declares one maximum credential
    // length for the whole application, measured in UTF-8 bytes because that is
    // what the hashing algorithm consumes, and every credential entry point -
    // sign-in, this registration contract, password change and portal creation -
    // applies that same number. It bounds the work an unauthenticated caller can
    // ask an intentionally expensive hash to perform; it is not a storage limit
    // and is not policy. It is deliberately not restated on this type, because
    // restating it is how such numbers come to disagree. Recorded in
    // MIGRATION_NOTES.md.
    //
    // MIGRATION: no server-computed membership facts are accepted - not lockout
    // state, online state, last login, last activity, last lockout, last
    // password change, nor creation timestamp. The creation timestamp in
    // particular is stamped through the injected clock abstraction, never by the
    // caller. The administrator "force password change" flag is set by its own
    // administrative action, not at creation.
    //
    // MIGRATION: no role set and no profile values are accepted. Role
    // assignment is a separate operation on the roles endpoint, and the legacy
    // creation path proves the point: it auto-assigns portal roles server-side
    // wherever a role is marked for automatic assignment, so roles at creation
    // were never client-supplied. Profile data belongs to the profile endpoint
    // and its own shape; this type deliberately takes no dependency on it. The
    // legacy roles member was an untyped array and has no place on a typed wire
    // contract.
    //
    // MIGRATION: no affiliate identifier is surfaced. It is not a column of the
    // Users table anywhere in the 88-script chain - it appears only as a
    // stored-procedure parameter and as a column of the Affiliates table - it is
    // marked non-browsable on the legacy user object so the property editor
    // never rendered it on the create form, and affiliate administration is
    // excluded from scope. Were it ever surfaced it would be a nullable integer
    // translating the legacy -1 sentinel to null, which is safe for that member
    // precisely because it is not an identity column with a negative seed.
    //
    // MIGRATION: the legacy hydration flag, change-tracking state, the
    // deprecated combined-name property and the property-access members are all
    // dropped. The first two are artefacts of hand-rolled materialisation, the
    // third was already marked obsolete in favour of the display name and
    // computed itself in a lazy accessor of exactly the kind this type forbids,
    // and the last belongs to an excluded subsystem.
    //
    // MIGRATION: no audit-author members. The modern audit quartet has zero
    // occurrences across all 88 schema scripts, and the shared auditable base
    // type is opt-in and carries only nullable creation and update timestamps.
    // No correlation identifier is carried either: it travels in the
    // X-Correlation-Id header, applied by middleware.

    /// <summary>
    /// The login name. Required.
    /// </summary>
    /// <remarks>
    /// Terminal schema: nvarchar(100) NOT NULL, and the single UNIQUE
    /// NONCLUSTERED constraint on the Users table. Uniqueness is enforced by the
    /// service against the data store, not by this type.
    /// MIGRATION: the legacy user object set this property and mirrored it onto
    /// a second, deprecated property of the same name on the membership object,
    /// which sits inside that type's deprecated region. The two collapse into
    /// this single member. The legacy editor also marked the property read-only,
    /// which applied when editing an existing user; on creation it is supplied.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The given name. Required.
    /// </summary>
    /// <remarks>
    /// Terminal schema: nvarchar(50) NOT NULL. The legacy editor attributes for
    /// this property agree with the column, so there is nothing to reconcile.
    /// </remarks>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// The family name. Required.
    /// </summary>
    /// <remarks>
    /// MEASURED SCHEMA FINDING - IMPORTANT FOR THE VALIDATOR AUTHOR. The
    /// baseline script declares this column nullable, but the baseline is not
    /// the terminal state: scripts 01.00.05 and 01.00.06 rebuild the table
    /// through a temporary copy, drop the original and rename the copy into
    /// place, and the rebuilt column is nvarchar(50) NOT NULL
    /// (01.00.05:L18, 01.00.06:L186). No later script alters it back. The legacy
    /// required attribute on the user object therefore AGREES with the terminal
    /// column, so the attribute-versus-column collision described in the plan is
    /// NOT present in the terminal schema.
    /// CONSEQUENCE: the validator MUST treat this member as required. If it is
    /// left optional, an insert will violate a NOT NULL constraint at run time.
    /// MIGRATION: this member is non-nullable, matching FirstName, which is the
    /// same shape against the same width with the same requiredness. An earlier
    /// revision typed it nullable to express "wire optionality", so that an
    /// omitted field and an explicitly blank one could be reported differently.
    /// That distinction was never realised and cannot be: the rule applied is
    /// NotEmpty, which treats a null and an empty string identically and emits
    /// one message either way, and the legacy absent-string sentinel IS the empty
    /// string, so an omitted value and a blank value already mean the same thing
    /// on this contract -- the DisplayName remark below states that same rule.
    /// The nullability bought nothing and cost the validator a null-forgiving
    /// operator on this member alone.
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// The name shown to other users.
    /// </summary>
    /// <remarks>
    /// Terminal schema: nvarchar(128) NOT NULL with a default of the empty
    /// string, added by script 03.02.03. Because the column defaults to empty,
    /// omitting a display name is legitimate and the service derives one.
    /// MIGRATION: that derivation is a service concern and is deliberately not
    /// performed here. The legacy user object derived it by substituting the
    /// user key, given name, family name and login name into a configured format
    /// string; reproducing that in an accessor would violate the inert-shape
    /// rule.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The email address. Required.
    /// </summary>
    /// <remarks>
    /// Terminal schema: <c>Email nvarchar(256) NULL</c>, added as a REPLACEMENT
    /// column at <c>03.00.13:L109-110</c> after the nine-column drop at
    /// <c>02.02.01:L50-51</c> removed the original <c>nvarchar(100) NOT NULL</c>
    /// one. The schema is authoritative, so the validator's length rule is 256.
    /// SCHEMA-WINS RECONCILIATION, CORRECTED: the legacy user object's
    /// maximum-length editor attribute of 256 AGREES with the terminal column; it
    /// was the superseded 100 that disagreed, and an earlier revision of this
    /// remark enforced that superseded value.
    /// REQUIREDNESS IS AN API-LEVEL RULE, NOT THE COLUMN'S NULLABILITY: the column
    /// permits an absent address and the legacy creation screen did not, declaring
    /// <c>Required(True)</c>; the screen's rule is what a create request must
    /// satisfy and is stated as an intentional API limit so it is not later
    /// relaxed by appealing to the schema.
    /// Uniqueness must NOT be enforced: the legacy provider sets unique email to
    /// false, and the terminal schema corroborates it, the unique constraint
    /// having been moved off Email and onto Username.
    /// MIGRATION: as with the login name, the legacy setter mirrored this value
    /// onto a second, deprecated property on the membership object; the two
    /// collapse into this single member.
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    // MIGRATION: the credential members below became UNCONDITIONALLY required when the
    // random-password branch was removed. An earlier revision declared both nullable and
    // guarded every credential rule with "when generation was not requested", which was the
    // only reason either could be legitimately absent. With that branch gone there is exactly
    // one creation path and it always carries a credential, so both members are non-nullable
    // and seeded with the empty string like every other required member of this type. Two
    // consequences follow and are deliberate: the validator no longer needs a null-forgiving
    // operator on either member, and an omitted JSON field now deserialises to the empty
    // string that the legacy absent-string sentinel already used, which the presence rule
    // rejects with one message rather than two.

    /// <summary>
    /// The plaintext password, inbound only. Required.
    /// </summary>
    /// <remarks>
    /// Measured strength rules are minimum length 7 and minimum
    /// non-alphanumeric characters 0. No length ceiling is inherited from the
    /// schema, because the credential is exchanged for a fixed-length digest, but
    /// the validator applies the hasher's own 72-encoded-byte ceiling: BCrypt
    /// consumes no more than that, so a longer value would have its excess
    /// silently discarded and two distinct credentials could produce the same
    /// digest. That is a property of the hashing primitive rather than a
    /// tightening of the legacy policy, and it is enforced identically by the
    /// hasher itself.
    /// MIGRATION: never echoed back. The creation response carries a detail shape
    /// that is asserted free of all password material, and this value is
    /// exchanged for a one-way hash in the Infrastructure layer before
    /// persistence.
    /// </remarks>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The repeated password used to catch typing errors. Required.
    /// </summary>
    /// <remarks>
    /// Carried on the request rather than resolved in the service so that a
    /// mismatch surfaces as a field-level 400 validation error in the problem
    /// document, exactly as the legacy validation summary presented it. The
    /// legacy counterpart is the confirmation input, compared to the password
    /// input at User.ascx.vb line 152.
    /// The comparison is deliberately NOT implemented on this type - no
    /// self-checking hook and no comparing accessor. It belongs to the
    /// validator, which compares the two ordinally exactly as the legacy string
    /// inequality test did.
    /// </remarks>
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>
    /// When true, the new account is approved immediately.
    /// </summary>
    /// <remarks>
    /// Maps to the approval assignment the legacy page drove from its authorise
    /// checkbox. The service decides what an unapproved new account means.
    /// MEASURED DEFAULT DIVERGENCE: the legacy markup pre-checked that checkbox
    /// and the page never reset it, so its effective legacy default was true,
    /// which matches the legacy membership object's own field initialiser. The
    /// wire default here is false, because an absent boolean must mean the least
    /// privileged outcome on a creation request rather than silent approval. A
    /// client that wants the legacy behaviour sends true explicitly.
    /// </remarks>
    public bool Authorize { get; set; }

    // MIGRATION: THE NOTIFY FLAG IS NOT A MEMBER OF THIS CONTRACT. The legacy page read a
    // pre-checked notify checkbox when it raised its created event, but the mail subsystem
    // that switch drove is excluded from this migration, and the owning service contract
    // states the switch is dropped with it. An earlier revision declared the flag anyway,
    // which meant a caller could ask for a notification, receive a 201, and never learn that
    // nothing was sent - a silent failure with no observable symptom, because the wire
    // contract advertised a capability nothing behind it implements. Removing the member
    // makes the absence visible in the contract itself, which is the only place a client can
    // discover it. Should outbound mail ever come into scope, the flag returns together with
    // a service member that can honour it, not before.

    // MIGRATION: THE RECOVERY QUESTION AND ANSWER ARE NOT MEMBERS OF THIS CONTRACT either,
    // and the reason is the same shape as the notify flag's. The legacy page carried both
    // inputs but hid them and skipped their checks unless the membership provider demanded
    // them, and the provider is registered with the pair not required. The owning service
    // contract records that the pair has no counterpart at all and that no member declares a
    // question or answer parameter, because the pair's only real purpose was to guard
    // credential retrieval - which is dropped outright. An earlier revision declared both
    // members and its validator asserted them whenever the bound policy flag was set, so a
    // deployment that switched the flag on would have collected a question and an answer that
    // nothing stores and nothing can ever check. The policy flag itself is consequently
    // unsatisfiable in the target, and PasswordPolicyOptions.Validate rejects it at start-up
    // rather than letting it look enforced.

    // MIGRATION: THE RANDOM-PASSWORD FLAG IS NOT A MEMBER OF THIS CONTRACT. The legacy page
    // took a genuinely separate branch that bypassed both credential inputs and called the
    // controller's generator, which produced a credential of the configured minimum length
    // plus four characters. The generator is not carried forward: a generated credential has
    // to be transmitted to be useful, the mail subsystem that would transmit it is excluded,
    // and no endpoint in the target returns, echoes or reconstructs a credential. An earlier
    // revision declared the flag and made both credential members conditional on it, so a
    // caller could create an account with a credential that no one - including the account's
    // own holder - could ever learn. Creation therefore always carries an explicit
    // credential, and the administrative-reset path on the password sub-resource is where an
    // administrator sets one on a caller's behalf.
}
