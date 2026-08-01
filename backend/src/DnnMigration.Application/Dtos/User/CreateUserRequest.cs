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
/// logging must redact or omit the password, confirmation, question and answer
/// members rather than serialising the instance.
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
/// Email; the remainder reproduce the explicit create-only controls of the same
/// page - the authorise and notify checkboxes, the random-password checkbox, and
/// the password, confirmation, question and answer inputs. Workflow provenance is
/// <c>Website/admin/Users/User.ascx.vb</c>.
/// </para>
/// <para>
/// MEASURED PASSWORD POLICY, preserved verbatim from the legacy membership
/// provider registration in <c>Website/release.config</c> lines 217 to 249:
/// minimum length 7; minimum non-alphanumeric characters 0; question and answer
/// NOT required; unique email NOT required; application name DotNetNuke.
/// Tightening any of these during the migration would lock existing users out,
/// so the validator must reproduce them exactly and add nothing.
/// </para>
/// <para>
/// MEASURED COLUMN WIDTHS from the terminal schema of the Users table, for the
/// validator's length rules: Username 100, FirstName 50, LastName 50,
/// DisplayName 128, Email 100. Username additionally carries a UNIQUE
/// NONCLUSTERED constraint, added by script 03.00.09, which replaced an earlier
/// unique constraint on Email; Email is therefore NOT unique in the terminal
/// schema, corroborating the provider setting above. No length rule and no
/// uniqueness rule is expressed on this type.
/// </para>
/// <para>
/// MEASURED EMAIL PATTERN, for the validator only, from
/// <c>Library/Components/Shared/Globals.vb</c> line 132 and applied to the
/// legacy Email property by its editor attribute:
/// <c>\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b</c>. Note that the
/// trailing quantifier caps the top-level domain at four characters, so
/// reproducing it verbatim rejects longer modern domains; that is a deliberate
/// fidelity decision for the validator author to make explicitly, not a defect
/// to fix silently here.
/// </para>
/// <para>
/// MEASURED VALIDATION SEQUENCE reproduced by the legacy page: the confirmation
/// is compared first (User.ascx.vb line 152), then password strength
/// (<c>UserController.ValidatePassword</c>), then question and answer, and only
/// when the provider requires them. LATENT DEFECT to flag for the validator
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
    // replaced by a one-way adaptive hash computed in the Infrastructure layer,
    // with re-hashing on first successful login and administrative reset as the
    // fallback. Hashing is exclusively an Infrastructure concern and is
    // unreachable from this layer by project reference.
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
    /// The family name.
    /// </summary>
    /// <remarks>
    /// MEASURED SCHEMA FINDING - IMPORTANT FOR THE VALIDATOR AUTHOR. The
    /// baseline script declares this column nullable, but the baseline is not
    /// the terminal state: scripts 01.00.05 and 01.00.06 rebuild the table
    /// through a temporary copy, drop the original and rename the copy into
    /// place, and the rebuilt column is nvarchar(50) NOT NULL. No later script
    /// alters it back. The legacy required attribute on the user object
    /// therefore AGREES with the terminal column, so the attribute-versus-column
    /// collision described in the plan is NOT present in the terminal schema.
    /// CONSEQUENCE: the validator MUST treat this member as required. If it is
    /// left optional, an insert will violate a NOT NULL constraint at run time.
    /// It is nullable here only because nullability on a request shape expresses
    /// wire optionality, letting the validator distinguish an omitted field from
    /// an explicitly blank one and report the more precise error; required-ness
    /// is the validator's concern, never the type's.
    /// </remarks>
    public string? LastName { get; set; }

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
    /// Terminal schema: nvarchar(100) NOT NULL.
    /// SCHEMA-WINS RECONCILIATION: the legacy user object carried a maximum-length
    /// editor attribute of 256 on this property while the column is 100. The
    /// schema is authoritative, so the validator's length rule is 100.
    /// Uniqueness must NOT be enforced: the legacy provider sets unique email to
    /// false, and the terminal schema corroborates it, the unique constraint
    /// having been moved off Email and onto Username.
    /// MIGRATION: as with the login name, the legacy setter mirrored this value
    /// onto a second, deprecated property on the membership object; the two
    /// collapse into this single member.
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The plaintext password, inbound only. Conditionally required.
    /// </summary>
    /// <remarks>
    /// Nullable because the random-password branch makes it legitimately absent.
    /// CONDITIONALITY for the validator, expressed there with a conditional
    /// rule: required only when <see cref="GenerateRandomPassword"/> is false.
    /// Measured strength rules are minimum length 7 and minimum
    /// non-alphanumeric characters 0; no maximum length applies.
    /// MIGRATION: never echoed back. The creation response carries a detail shape
    /// that is asserted free of all password material, and this value is
    /// exchanged for a one-way hash in the Infrastructure layer before
    /// persistence.
    /// </remarks>
    public string? Password { get; set; }

    /// <summary>
    /// The repeated password used to catch typing errors. Conditionally required.
    /// </summary>
    /// <remarks>
    /// Carried on the request rather than resolved in the service so that a
    /// mismatch surfaces as a field-level 400 validation error in the problem
    /// document, exactly as the legacy validation summary presented it. The
    /// legacy counterpart is the confirmation input, compared to the password
    /// input at User.ascx.vb line 152.
    /// The comparison is deliberately NOT implemented on this type - no
    /// self-checking hook and no comparing accessor. It belongs to the
    /// validator, alongside the same conditional rule that governs
    /// <see cref="Password"/>.
    /// </remarks>
    public string? ConfirmPassword { get; set; }

    /// <summary>
    /// The password-recovery question. Optional.
    /// </summary>
    /// <remarks>
    /// Optional because the legacy provider sets question and answer to not
    /// required, and the legacy page both hid these inputs and skipped their
    /// checks unless the provider required them. The validator must not make
    /// this member required.
    /// </remarks>
    public string? PasswordQuestion { get; set; }

    /// <summary>
    /// The answer to <see cref="PasswordQuestion"/>. Optional.
    /// </summary>
    /// <remarks>
    /// Optional for the same measured reason as the question, and treated as
    /// sensitive: it participates in recovery and must not be logged or echoed.
    /// </remarks>
    public string? PasswordAnswer { get; set; }

    /// <summary>
    /// When true, the server generates the password and
    /// <see cref="Password"/> and <see cref="ConfirmPassword"/> are ignored.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not cosmetic: the legacy page took a genuinely separate
    /// branch that bypassed the password input entirely and called the
    /// controller's password generator. That generator produced a password of
    /// the configured minimum length plus four characters, so 11 under the
    /// measured policy. Generation is a service concern and is deliberately not
    /// invoked here.
    /// The legacy markup pre-checked the corresponding box, but the page reset it
    /// to false on first load (User.ascx.vb line 261), so false is both the
    /// measured effective default and the wire default.
    /// </remarks>
    public bool GenerateRandomPassword { get; set; }

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

    /// <summary>
    /// When true, a notification message is sent to the new user.
    /// </summary>
    /// <remarks>
    /// Maps to the notify checkbox the legacy page read when raising its
    /// created event. MEASURED DEFAULT DIVERGENCE: as with approval, the legacy
    /// markup pre-checked this box and never reset it, so its effective legacy
    /// default was true; the wire default here is false so that an omitted field
    /// never causes an unrequested outbound message. A client that wants the
    /// legacy behaviour sends true explicitly.
    /// </remarks>
    public bool Notify { get; set; }
}
