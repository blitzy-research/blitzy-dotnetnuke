using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Domain.ValueObjects;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Validates <see cref="CreateUserRequest"/>, the inbound contract of
/// <c>POST /api/v1/users</c>, reproducing the legacy DotNetNuke user-creation rules
/// exactly and adding nothing to them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only active password rule is a minimum length of seven characters.</b> That
/// single sentence is the most important fact about this class, and it is a measured
/// finding rather than a simplification. The legacy check at
/// <c>Library/Components/Users/UserController.vb</c> L1067-L1091 wrote three password
/// rules, and under the shipped configuration only the first can ever fire:
/// </para>
/// <list type="number">
/// <item><description>
/// Minimum length (L1073) - <b>active</b>. The <c>minRequiredPasswordLength</c>
/// attribute at <c>Website/release.config</c> L242 carries the value seven, and this
/// class binds that number from <see cref="PasswordPolicyOptions"/> rather than
/// restating it.
/// </description></item>
/// <item><description>
/// Minimum count of non-alphanumeric characters (L1078-L1079) - <b>vacuous</b>. The
/// <c>minRequiredNonalphanumericCharacters</c> attribute at L243 carries the value
/// zero, and the legacy comparison failed a password only when its match count fell
/// <i>below</i> that minimum. A count never falls below zero, so the rule could not
/// fail. No rule is emitted for it here: a condition that can never fail is noise that
/// reads like enforcement.
/// </description></item>
/// <item><description>
/// Strength pattern (L1084-L1086) - <b>never reached</b>. The setting is configured in
/// neither <c>Website/release.config</c> nor <c>Website/development.config</c>, proven
/// by searching both files, so the legacy emptiness guard at L1084 was always false.
/// </description></item>
/// </list>
/// <para>
/// Consequently this validator applies <b>no character-class requirement of any kind</b>
/// to the password, and no composition, repetition or dictionary rule. Each of those
/// would be a tightening, and a tightening during a migration denies access to users who
/// are already registered. Hardening the policy is a separate, explicit decision.
/// </para>
/// <para>
/// <b>Name lengths follow the schema, not the markup.</b> The legacy property editor
/// allowed one hundred characters for the given and family names and the legacy stored
/// procedures accepted the same, but the terminal column for both is fifty characters -
/// rebuilt to that width by scripts 01.00.05 and 01.00.06 and never widened afterwards,
/// verified by sweeping every one of the 88 schema scripts for an altering statement
/// against either column and finding none. The schema is authoritative, so the rules
/// below cap both names at fifty. The same reconciliation applies to the email address,
/// whose legacy editor attribute allowed 256 characters against a column of one hundred.
/// </para>
/// <para>
/// <b>What this class deliberately does not do.</b> It performs no persistence lookup,
/// so it enforces neither login-name nor email distinctness; those are outcomes the
/// service reports after attempting to write, and they surface as an RFC 7807 document
/// shaped by the transport layer. It computes no hash, holds no credential store
/// knowledge, derives nothing, sends nothing and logs nothing - the request it inspects
/// carries a plaintext password, so emitting any part of it would breach the
/// no-sensitive-data logging requirement. It shapes no HTTP response.
/// </para>
/// <para>
/// <b>Message provenance is two-tier, exactly as the legacy application was.</b> A
/// missing required field reproduces the wording the property editor rendered on the
/// creation screen, measured from
/// <c>Website/admin/Users/App_LocalResources/User.ascx.resx</c>. An invalid <i>value</i>
/// reproduces the wording the service returned through
/// <c>UserController.GetUserCreateStatus</c>, measured from
/// <c>Website/App_GlobalResources/SharedResources.resx</c>. Both sets are reproduced
/// character for character, including their inconsistent inter-sentence spacing.
/// </para>
/// </remarks>
public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    // MIGRATION: message wording is reproduced verbatim from the legacy resource files,
    // and the inter-sentence spacing in those files is INCONSISTENT. The global entries
    // reproduced below (SharedResources.resx L279, L282, L285, L288, L291, L852) use two
    // spaces after a sentence period. Two neighbouring entries do not: the duplicate
    // login-name entry at L297 and the registration-failure entry at L300 use a single
    // space, and L300 additionally contains the legacy misspelling "Futher". Neither of
    // those two belongs to this validator - both are service-layer outcomes reported
    // after a write is attempted - so neither is reproduced here, and the spacing is
    // therefore copied per entry rather than normalised across the set.

    /// <summary>
    /// Legacy wording for an invalid login name, from <c>SharedResources.resx</c> L291.
    /// </summary>
    private const string InvalidUserNameMessage =
        "The username specified is invalid.  Please specify a valid username.";

    /// <summary>
    /// Legacy wording for an invalid email address, from <c>SharedResources.resx</c> L282.
    /// </summary>
    private const string InvalidEmailMessage =
        "The email address specified is invalid.  Please specify a valid email address.";

    /// <summary>
    /// Legacy wording for an invalid password, from <c>SharedResources.resx</c> L285. The
    /// two bracketed tokens are substituted from the bound policy at construction time.
    /// </summary>
    private const string InvalidPasswordMessageTemplate =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at least [PasswordLength] characters in length and contain at least [NoneAlphabet] non-alphanumeric characters.";

    // MIGRATION: the confirmation-mismatch wording has two competing legacy entries -
    // SharedResources.resx L852 and a second, differently worded entry at L969 carrying a
    // trailing period. The legacy code passed the bare resource key, and DotNetNuke
    // resolves a bare key to the entry whose name ends in the primary text suffix, which
    // is the L852 form. The L852 form is therefore the one reproduced here, deliberately
    // including its absence of a trailing period. The duplicate is recorded rather than
    // silently resolved so that nobody later "corrects" this string to the other variant.

    /// <summary>
    /// Legacy wording for a password/confirmation mismatch, from
    /// <c>SharedResources.resx</c> L852. It intentionally carries no trailing period.
    /// </summary>
    private const string PasswordMismatchMessage =
        "The Password and Confirmation Passwords do not match";

    // MIGRATION: the recovery-question and recovery-answer wordings are deliberately NOT
    // declared here. An earlier revision carried both, taken from SharedResources.resx L288
    // and L279, and attached them to rules gated on the bound question-and-answer policy
    // flag. The request no longer carries either member, because the owning service contract
    // states the recovery pair has no counterpart at all, so a message for a field that
    // cannot be submitted would be unreachable text asserting a rule that does not exist.
    // The two resource entries still exist in the legacy tree and are cited here so their
    // absence is provably deliberate rather than an oversight; the wording itself is not
    // reproduced, not even as a quotation, so that its absence is verifiable by search.


    // MIGRATION: the ceiling on a submitted credential is NOT declared in this file. The number, the
    // unit and the wording live once, on Validation/CredentialBounds.cs, and every credential entry
    // point in this layer reads them from there - sign-in, registration, the password sub-resource and
    // portal creation - so a ceiling that only some entry points applied cannot recur. The unit is
    // UTF-8 BYTES rather than characters, because that is the form the hashing algorithm consumes and
    // a character outside the ASCII range occupies two to four of them. It is deliberately NOT the
    // algorithm's own 72-byte significance limit: the hasher digests the credential with SHA-384
    // before hashing (Infrastructure/Security/BcryptPasswordHasher.cs), so every byte of the input
    // contributes and no two distinct credentials can become interchangeable by truncation. The bound
    // is therefore a bound on the work an unauthenticated caller may ask an expensive function to
    // perform, it is net-new, it sits far above any credential a person would choose, and it is
    // emphatically NOT the legacy markup's twenty-character input limit.

    /// <summary>
    /// Screen wording for a missing login name, from <c>User.ascx.resx</c> L154.
    /// </summary>
    private const string UsernameRequiredMessage = "User name is required";

    /// <summary>
    /// Screen wording for a missing given name, from <c>User.ascx.resx</c> L157.
    /// </summary>
    private const string FirstNameRequiredMessage = "First name is required";

    /// <summary>
    /// Screen wording for a missing family name, from <c>User.ascx.resx</c> L160.
    /// </summary>
    private const string LastNameRequiredMessage = "Last name is required";

    /// <summary>
    /// Screen wording for a missing email address, from <c>User.ascx.resx</c> L163.
    /// </summary>
    private const string EmailRequiredMessage = "Email is required";

    /// <summary>
    /// Screen wording for a malformed email address, from <c>User.ascx.resx</c> L220.
    /// </summary>
    private const string EmailFormatMessage = "You must enter a valid email address";

    // MIGRATION: the wording below is authored here. The legacy creation screen enforced
    // the display-name ceiling through a markup attribute on a property-editor field,
    // which silently truncated typing and produced no message at all, so there is no
    // legacy string to reproduce. The sentence below follows the measured pattern of the
    // global entries above so that it reads as part of the same family.

    /// <summary>
    /// Authored wording for an over-long display name; no legacy equivalent exists.
    /// </summary>
    private const string InvalidDisplayNameMessage =
        "The display name specified is invalid.  Please specify a valid display name.";

    // MIGRATION: net-new wording, and necessarily so - the legacy application had no ceiling
    // of this kind to report, because it stored credentials in a reversible format rather than
    // hashing them. The sentence names the limit and its unit, because a caller told only that
    // a password is "too long" cannot act on it when the value is well inside any character
    // count they would think to check.

    /// <summary>
    /// Net-new wording for a credential that cannot be hashed without loss; no legacy
    /// equivalent exists.
    /// </summary>
    private const string PasswordTooLongMessage =
        "The password specified is too long.  Please specify a password of no more than 72 bytes.";

    /// <summary>
    /// The token the legacy code replaced with the configured minimum password length.
    /// </summary>
    private const string PasswordLengthToken = "[PasswordLength]";

    /// <summary>
    /// The token the legacy code replaced with the configured minimum count of
    /// non-alphanumeric characters.
    /// </summary>
    private const string NoneAlphabetToken = "[NoneAlphabet]";

    /// <summary>
    /// Terminal column width of the login name: <c>nvarchar(100)</c>.
    /// </summary>
    private const int UsernameMaximumLength = 100;

    /// <summary>
    /// Terminal column width of the given and family names: <c>nvarchar(50)</c>. This is
    /// the schema width, deliberately not the wider legacy markup allowance.
    /// </summary>
    private const int PersonNameMaximumLength = 50;

    /// <summary>
    /// Terminal column width of the display name: <c>nvarchar(128)</c>.
    /// </summary>
    private const int DisplayNameMaximumLength = 128;

    // MIGRATION: THE EMAIL WIDTH IS 256, AND 100 IS A SUPERSEDED VALUE THAT THIS CONSTANT
    // PREVIOUSLY CARRIED IN ERROR. The Email column has a two-part history and only its
    // terminal state is authoritative under Rule T4. An Email nvarchar(100) NOT NULL existed
    // from 01.00.00.SqlDataProvider:L107, was carried at that width through both rebuilds
    // (01.00.05:L25 and 01.00.06:L193), and was then REMOVED ALTOGETHER by the nine-column
    // drop at 02.02.01:L50-51 when credentials and profile data moved to the externally
    // installed membership tables. A REPLACEMENT column was added later, at
    // 03.00.13:L109-110, as
    //     ALTER TABLE {databaseOwner}{objectQualifier}Users ADD Email nvarchar(256) NULL
    // and back-filled from the membership store immediately afterwards at :L113-L117. No
    // later script in the 88-script chain alters it again, and the terminal stored procedures
    // agree: the AddUser family declares @Email nvarchar(256) at 03.02.03:L659 and again at
    // 04.00.04:L704. 256 is therefore the width, and the legacy screen attribute
    // MaxLength(256) at UserInfo.vb:L121 turns out to AGREE with the terminal column rather
    // than to contradict it.
    //
    // Infrastructure/Persistence/Configurations/UserConfiguration.cs already maps this column
    // at 256 and nullable, so the number here is now consistent with the mapping instead of
    // rejecting values the database would have accepted.

    /// <summary>
    /// Terminal column width of the email address: <c>nvarchar(256)</c>.
    /// </summary>
    /// <remarks>
    /// The terminal width is not the baseline width. <c>dbo.Users.Email</c> is created at
    /// <c>nvarchar(100) NOT NULL</c> (<c>01.00.00.SqlDataProvider:L107</c>), DROPPED outright
    /// (<c>02.02.01.SqlDataProvider:L50-51</c>) when contact details moved into the ASP.NET
    /// membership tables, and re-added as <c>nvarchar(256) NULL</c>
    /// (<c>03.00.13.SqlDataProvider:L109-110</c>), after which nothing narrows it. The terminal
    /// <c>AddUser</c> and <c>UpdateUser</c> procedures agree, both declaring
    /// <c>@Email nvarchar(256)</c> (<c>04.00.04.SqlDataProvider:L704</c> and <c>L1078</c>), as
    /// does the persistence configuration and <c>UpdateUserRequestValidator</c>. Bounding this
    /// rule at 100 would refuse addresses the store already holds.
    /// </remarks>
    private const int EmailMaximumLength = 256;

    /// <summary>
    /// Upper bound on the time any single pattern evaluation may consume, so that a
    /// pathological input cannot hold a request thread.
    /// </summary>
    private static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(250);

    // MIGRATION: THE EMAIL SHAPE RULE IS NO LONGER RESTATED HERE. A local copy of the legacy
    // pattern from Library/Components/Shared/Globals.vb L132 used to live at this point, and a
    // second copy lived in Validation/UpdateUserRequestValidator.cs. Two copies of one rule is
    // how create and update came to behave differently for the same address, so both copies are
    // deleted and both validators now call the single authority,
    // Domain/ValueObjects/EmailAddress.cs, whose own audit trail transcribes that pattern clause
    // by clause and records every deliberate departure from it. There is exactly one email rule
    // in this solution now, and it is not in this file.
    //
    // Delegating also CORRECTS TWO DEFECTS that the local copy carried:
    //   - It matched a SUBSTRING. FluentValidation's pattern rule tests whether the pattern
    //     occurs anywhere in the value, and the legacy pattern is word-boundary delimited rather
    //     than anchored, so "a@b.co and some junk" satisfied it. The legacy screen did NOT behave
    //     that way: its only consumer was the ASP.NET expression validator, which accepts a value
    //     only when the match covers the whole of it. Whole-value checking is therefore a
    //     RESTORATION of legacy behaviour, not a tightening.
    //   - It capped the final domain label at four letters, rejecting .museum, .travel and every
    //     other modern suffix. That cap is removed at the authority; the divergence, and the
    //     bounded standards-based checks that replace it, are documented there and in
    //     MIGRATION_NOTES.md.

    /// <summary>
    /// Builds the rule set, binding every password threshold and every substituted
    /// message token from the supplied policy rather than restating any of them.
    /// </summary>
    /// <param name="passwordPolicy">
    /// The password policy in force, preserved verbatim from the legacy membership
    /// provider registration and bound by the Api layer from the configuration section
    /// named by <see cref="PasswordPolicyOptions.SectionName"/>. It is read once here, at
    /// construction time; no rule dereferences it again while validating. This is the
    /// validator's only dependency: it takes no repository, no clock and no
    /// credential-store abstraction, because none of those is needed to decide whether a
    /// request is well formed.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="passwordPolicy"/> is <see langword="null"/>. Failing
    /// loudly at construction is deliberate: a validator that silently fell back to
    /// built-in defaults would enforce a policy nobody configured, which is exactly the
    /// kind of silent divergence this migration must surface rather than absorb.
    /// </exception>
    // MIGRATION NOTE ON THE CONSTRUCTOR SHAPE - deliberate, and matching this solution's
    // established convention. An earlier revision took IOptions<PasswordPolicyOptions>
    // and added Microsoft.Extensions.Options to this project to make that compile. AAP
    // 0.6.1 fixes this layer's package surface at the two FluentValidation entries, so
    // that package was not approved and the manifest was the wrong thing to change.
    //
    // The bound policy instance is taken directly instead, which is what the sibling
    // ChangePasswordRequestValidator already did and what the policy type itself
    // documents: PasswordPolicyOptions is DECLARED IN THIS LAYER AND BOUND BY THE API
    // LAYER. The options PATTERN and the options PACKAGE are separate things - this layer
    // participates in the pattern by owning the policy type, while IOptions<T> belongs to
    // the layer that actually reads configuration, which AAP 0.5.2.2 places above this
    // one. Every IOptions<T> in the backend accordingly sits in Api or Infrastructure.
    //
    // The obligation this places on the composition root is real and is recorded here
    // rather than worked around: the Api layer must register the bound
    // PasswordPolicyOptions instance as a resolvable service in its own right, projecting
    // it from the options it already binds, so the container can supply it to both
    // validators. If it does not, construction fails immediately and visibly - which is
    // the intended outcome, because it never degrades to an unconfigured policy.
    public CreateUserRequestValidator(PasswordPolicyOptions passwordPolicy)
    {
        ArgumentNullException.ThrowIfNull(passwordPolicy);

        // A local alias, kept because the rules below read the policy in several places and
        // the parameter name matches the sibling validator's for consistency. It replaces an
        // earlier `passwordPolicyOptions.Value` dereference, so the snapshot semantics the
        // rules rely on are unchanged: the policy is read once, here, and never again while
        // validating.
        PasswordPolicyOptions policy = passwordPolicy;

        // MIGRATION: the two bracketed tokens are substituted from the BOUND POLICY
        // VALUES, reproducing UserController.GetUserCreateStatus L607-L609, which read the
        // same two numbers from the provider facade and rewrote them into the message at
        // run time. Neither number is restated as a literal anywhere in this file: doing
        // so would desynchronise the message from the rule the moment the configuration
        // changed, leaving the text asserting a threshold that is no longer enforced.
        //
        // MIGRATION: the legacy conversions were implicit. The admin code-behinds compiled
        // with Option Strict disabled - Website/release.config L125 declares
        // strict="false", whereas the class library at
        // Library/DotNetNuke.Library.vbproj L24 declares it enabled - so the legacy
        // formatting call picked up the ambient culture silently. Both conversions are made
        // explicit and culture-invariant here, which is the same treatment every implicit
        // narrowing or coercion receives when it crosses into this solution. The user field
        // set of this screen never existed as declared markup at all: it was produced at
        // run time by a reflective property editor whose control library is excluded, which
        // is why these rules are derived from the schema, the measured configuration and
        // the resource wording rather than transcribed from a markup census.
        string invalidPasswordMessage = InvalidPasswordMessageTemplate
            .Replace(
                PasswordLengthToken,
                policy.MinRequiredPasswordLength.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                NoneAlphabetToken,
                policy.MinRequiredNonAlphanumericCharacters.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        // MIGRATION: no distinctness rule on the login name. A duplicate is a service-layer
        // outcome, not a request defect: the legacy path attempted the write and mapped the
        // resulting provider status through GetUserCreateStatus L619-L620 onto a dedicated
        // message. Reproducing that here would require a persistence lookup, which this
        // layer cannot reach and must not acquire, and would additionally introduce a
        // check-then-write race that the database constraint already settles authoritatively.
        RuleFor(request => request.Username)
            .NotEmpty().WithMessage(UsernameRequiredMessage)
            .MaximumLength(UsernameMaximumLength).WithMessage(InvalidUserNameMessage);

        // MIGRATION: both person names are capped at the SCHEMA width of fifty, not the one
        // hundred the legacy property editor and stored procedures allowed. Scripts
        // 01.00.05 and 01.00.06 rebuilt the table through a temporary copy and the rebuilt
        // columns are fifty characters wide; sweeping all 88 schema scripts for an altering
        // statement against either column returns nothing, so fifty is terminal. The schema
        // is immutable in this migration and therefore wins. Relative to the markup this is
        // a tightening, and it is recorded as one.
        //
        // MIGRATION: the family name is REQUIRED. The baseline script declared the column
        // nullable at 01.00.00:L100, but the 01.00.05 rebuild re-declared it NOT NULL at
        // 01.00.05:L18 and the 01.00.06 rebuild preserved that at 01.00.06:L186, so accepting
        // a missing family name would defer a constraint violation to write time. The two
        // person names are therefore identical in every respect that matters here - same
        // width, same requiredness, same message shape - and the rules below are deliberately
        // symmetric. An earlier revision typed the family name as nullable on the request and
        // reached it through a null-forgiving operator, on the stated ground that wire
        // optionality let an omitted field and an explicitly blank one be told apart. NotEmpty
        // cannot make that distinction: it treats a null and an empty string alike and emits
        // one message either way, so the operator suppressed a warning without buying a
        // behaviour. The request member is now non-nullable and the operator is gone.
        RuleFor(request => request.FirstName)
            .NotEmpty().WithMessage(FirstNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength).WithMessage(InvalidUserNameMessage);

        RuleFor(request => request.LastName)
            .NotEmpty().WithMessage(LastNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength).WithMessage(InvalidUserNameMessage);

        // MIGRATION: the display name is NOT required, even though the legacy screen
        // carries a "required" string for it. The terminal column is non-nullable but
        // defaults to the empty string, and the service derives a display name when one is
        // absent, so demanding it would be a tightening that rejects requests the legacy
        // application accepted. Only the width is enforced, and only when a value was
        // actually supplied - the legacy absent-string sentinel is the empty string rather
        // than a null, so an omitted value and a blank value mean the same thing here.
        RuleFor(request => request.DisplayName)
            .MaximumLength(DisplayNameMaximumLength).WithMessage(InvalidDisplayNameMessage)
            .When(request => !string.IsNullOrEmpty(request.DisplayName));

        // MIGRATION: no distinctness rule on the email address either, and this one is
        // policy rather than pragmatism: the provider's unique-email attribute at
        // Website/release.config L244 is false, so the legacy installation permitted
        // duplicates and its data may well contain them. The terminal schema agrees, having
        // moved its single uniqueness constraint off the email column and onto the login
        // name. Note also that the shipped host account is seeded with an email address of
        // "host", which fails even the format pattern below: validators bind inbound
        // requests only and are never a precondition for reading or writing existing rows.
        //
        // MIGRATION: the width is the TERMINAL 256, not the baseline 100, and the difference is
        // a destructive-chain reading rather than a preference. Users.Email began as
        // [Email] [nvarchar] (100) NOT NULL at 01.00.00.SqlDataProvider:L107 and kept that width
        // through both table rebuilds, 01.00.05:L25 and 01.00.06:L193. The nine-column drop at
        // 02.02.01:L50-51 then removed it from the table entirely, when credentials moved into the
        // externally installed membership store, and 03.00.13:L109-110 re-created it as
        // Email nvarchar(256) NULL, populated immediately afterwards from that store by the
        // UPDATE at 03.00.13:L112-117. No ALTER COLUMN in any of the 88 scripts touches it
        // again. The 100 therefore constrains a column that no longer exists, and AAP section
        // 0.7.1.2 - the DDL chain is destructive, so only the terminal state is meaningful -
        // makes the later declaration the one Rule T4 points at.
        //
        // MIGRATION: three independent authorities corroborate the 256, which is what turns the
        // legacy MaxLength(256) at UserInfo.vb:L121 from an apparent over-promise into agreement:
        // the terminal AddUser and UpdateUser procedures declare @Email nvarchar(256)
        // (04.00.04:L704 and L1078), the persistence configuration for this column declares the
        // same width, and the domain value object that describes an address enforces it too. A
        // request bearing a 150-character address is consequently storable by the terminal
        // database, so capping at 100 here would reject a value the schema accepts - the failure
        // mode this rule has to avoid is being narrower than the column, not wider.
        //
        // MIGRATION: requiredness is NOT relaxed to match the re-created column's nullability.
        // That column permits NULL only because it is a denormalised copy of the membership
        // store's own column; the creation screen still demanded an address, so the required rule
        // reproduces the screen and only the width is corrected.
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage(EmailRequiredMessage)
            .MaximumLength(EmailMaximumLength).WithMessage(InvalidEmailMessage)
            .Must(candidate => EmailAddress.TryCreate(candidate, out _))
                .WithMessage(EmailFormatMessage);

        // MIGRATION: MINIMUM LENGTH IS THE ONLY PASSWORD RULE, and the threshold is bound
        // from the policy rather than written down. The non-alphanumeric rule is vacuous
        // because its configured minimum is zero and the legacy comparison could never fail
        // against it, so no rule is emitted for it; its number still reaches the operator
        // through the substituted message above, exactly as it did before. No requirement
        // on character classes, composition, repetition or a word list is added: every one
        // of those would be a tightening, and the shipped product proves how damaging that
        // would be - the seeded host and administrator accounts carry four- and
        // five-character passwords and would both be rejected by the very minimum this rule
        // enforces.
        //
        // MIGRATION: the legacy check contained a real defect, which is annotated here and
        // deliberately neither reproduced nor repaired in the legacy file. Its first two
        // rules accumulated a failure flag, but the third, at UserController.vb L1086,
        // ASSIGNED the pattern outcome to that flag instead of combining it, discarding
        // everything computed above and letting an already-failed password be reported
        // valid. The defect is dormant only because no pattern is configured. This
        // validator accumulates correctly, because each rule contributes its own failure
        // independently - and THAT CORRECTION IS ITSELF A DIVERGENCE from measured legacy
        // behaviour, which is why it is recorded rather than quietly absorbed.
        //
        // MIGRATION: THERE IS NOW A MAXIMUM LENGTH, and this paragraph previously argued
        // against having one. The reasoning it gave was sound as far as it went - the legacy
        // markup ceiling of twenty characters and the baseline column that matched it both
        // describe a plaintext column that no longer exists, and under a one-way hash the
        // stored width is independent of the input length - but it answered the wrong
        // question. The ceiling is not about storage. It is about bounding the work an
        // unauthenticated caller can ask a deliberately expensive hash to perform, and about
        // there being ONE answer to "how long may a credential be" across sign-in,
        // registration, password change and portal creation rather than four.
        //
        // The number, the unit and the wording come from Validation/CredentialBounds.cs, which
        // records why the unit is UTF-8 bytes and why the value is not the algorithm's own
        // 72-byte significance limit. The bound is net-new, is a tightening, is generous enough
        // that no credential a person chooses reaches it, and is recorded in
        // MIGRATION_NOTES.md.
        //
        // MIGRATION: password retrieval is not carried forward. The legacy facility at
        // UserController.vb L433 returned a stored credential and passed the user by
        // reference to do it; the feature is dropped outright, and the by-reference idiom is
        // retired everywhere, so no endpoint, screen or rule in the target reads a password
        // back. Password RESET survives and is a different feature.
        //
        // MIGRATION: THE RULE IS NOW UNCONDITIONAL. An earlier revision guarded every
        // credential rule with "when random generation was not requested". The generation
        // branch is removed from the contract - a generated credential cannot be delivered,
        // because the mail subsystem is excluded and no endpoint returns a credential - so
        // there is one creation path and it always carries a credential. The guard is gone
        // rather than left as an always-true condition, and the null-forgiving operator this
        // rule used to need is gone with it, because the member is no longer nullable.
        RuleFor(request => request.Password)
            .NotEmpty().WithMessage(invalidPasswordMessage)
            .MinimumLength(policy.MinRequiredPasswordLength).WithMessage(invalidPasswordMessage)
            .Must(CredentialBounds.IsWithinMaximumByteLength)
                .WithMessage(CredentialBounds.MaximumByteLengthMessage);

        // MIGRATION: the non-alphanumeric count, ValidatePassword L1078-L1079. This rule was previously
        // MISSING while its number was still substituted into the message above, so a deployment that
        // configured a minimum above zero produced a message asserting a requirement this boundary never
        // checked - and the credential then reached the hasher, which DOES check it and throws, turning a
        // field-level problem into an unhandled fault. The rule is expressed even though the shipped
        // configuration makes it inert (minRequiredNonalphanumericCharacters="0",
        // Website/release.config:L243) precisely because the value is configurable: omitting a rule on
        // the ground that today's configuration cannot reach it is what allowed the gap to exist.
        //
        // A configured minimum of zero SKIPS THE RULE ENTIRELY rather than registering a comparison that
        // can never fail, mirroring the hasher, which skips the identical check for the identical reason.
        if (policy.MinRequiredNonAlphanumericCharacters > 0)
        {
            int minimumNonAlphanumericCharacters = policy.MinRequiredNonAlphanumericCharacters;

            RuleFor(request => request.Password)
                .Must(password =>
                    HasEnoughNonAlphanumericCharacters(password, minimumNonAlphanumericCharacters))
                .WithMessage(invalidPasswordMessage)
                .When(request => !string.IsNullOrEmpty(request.Password));
        }

        // MIGRATION: the confirmation comparison is unconditional for the same reason. The
        // comparison is ordinal, matching the legacy string inequality test at
        // User.ascx.vb L152. Two absent values compare equal and pass here, which is
        // correct: the credential's own presence rule reports the omission, so one omission
        // yields one message rather than two.
        RuleFor(request => request.ConfirmPassword)
            .Equal(request => request.Password).WithMessage(PasswordMismatchMessage);

        // MIGRATION: NO RECOVERY QUESTION OR ANSWER RULE, AND NO REQUEST MEMBER TO ATTACH ONE
        // TO. The provider's question-and-answer attribute at Website/release.config L241 is
        // false and the legacy screen hid both inputs and skipped their checks unless the
        // provider demanded them, so nothing was enforced in the observed installation. An
        // earlier revision reproduced that conditionality faithfully, gating two rules on the
        // bound policy flag - but the pair has no target counterpart at all: the owning
        // service contract states no member declares a question or answer parameter, because
        // the pair existed to guard credential retrieval, which is dropped outright. A rule
        // that could only fire for a field the contract does not accept, storing an answer
        // nothing can later check, is worse than no rule, so both rules and both request
        // members are removed. The bound flag is consequently unsatisfiable in the target and
        // PasswordPolicyOptions.Validate rejects it at start-up, which is what stops a
        // deployment from believing it switched the requirement on.

        // MIGRATION: an unconfigured strength pattern SKIPS THE RULE ENTIRELY rather than
        // compiling an empty one. An empty pattern matches every input, so applying it would
        // be a silent no-op wearing the appearance of enforcement - strictly worse than
        // having no rule, because it would survive review. The pattern is compiled once
        // here, never per evaluation, and it carries the same timeout as the email pattern.
        // An unparseable configured value throws at construction, which is the same outcome
        // the legacy code produced when it built the pattern, merely reached sooner and
        // reported against the configuration instead of against a user's request.
        if (!string.IsNullOrEmpty(policy.PasswordStrengthRegularExpression))
        {
            Regex configuredStrengthPattern = new(
                policy.PasswordStrengthRegularExpression,
                RegexOptions.CultureInvariant,
                PatternMatchTimeout);

            RuleFor(request => request.Password)
                .Matches(configuredStrengthPattern).WithMessage(invalidPasswordMessage)
                .When(request => !string.IsNullOrEmpty(request.Password));
        }

        // MIGRATION: no lower-bound test on any identifier, and none is even reachable -
        // this contract carries no key of any kind, because a caller-supplied tenant key
        // would breach tenant isolation and a caller-supplied user key would be ignored
        // rather than honoured. The prohibition is recorded anyway, because the identity
        // seeds make the usual idioms actively wrong: the portal key seeds at negative one
        // and the role and tab keys seed at zero, so both values are legitimate keys, while
        // the legacy absent-integer sentinel is also negative one. A lower-bound or
        // inequality test against either value would reject real rows. The only remaining
        // member of this contract is the approval flag, and a flag has no invalid value to
        // reject: it is consumed by the service, which decides what an unapproved new account
        // means.
    }


    /// <summary>
    /// Reports whether a submitted credential carries at least the configured number of
    /// characters outside the ranges <c>0-9</c>, <c>A-Z</c> and <c>a-z</c>.
    /// </summary>
    /// <param name="password">The submitted credential, which may be absent or blank.</param>
    /// <param name="minimum">
    /// The configured minimum count. The caller registers this rule only when the minimum is
    /// above zero, so a zero minimum performs no scan at all.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the credential carries at least <paramref name="minimum"/>
    /// such characters, and also when it is absent or blank, for the reason given on
    /// <see cref="CredentialBounds.IsWithinMaximumByteLength(string?)"/>.
    /// </returns>
    /// <remarks>
    /// The classification reproduces the legacy character class <c>[^0-9a-zA-Z]</c> declared at
    /// <c>Library/Components/Users/UserController.vb:L1078</c> and compared against the
    /// configured minimum at L1079. It is deliberately <b>not</b> a Unicode-aware test: the
    /// framework's culture-aware letter-or-digit check would classify an accented letter as
    /// alphanumeric, so such a character would stop contributing to the total and the rule would
    /// silently widen. This is the identical test
    /// <c>DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs</c> performs, so the
    /// boundary and the primitive agree on which credentials satisfy the rule. The scan stops as
    /// soon as the minimum is reached, and the value itself is never retained or echoed.
    /// </remarks>
    private static bool HasEnoughNonAlphanumericCharacters(string? password, int minimum)
    {
        if (string.IsNullOrEmpty(password))
        {
            return true;
        }

        int found = 0;

        foreach (char character in password)
        {
            if (!char.IsAsciiLetterOrDigit(character) && ++found >= minimum)
            {
                return true;
            }
        }

        return found >= minimum;
    }

    /// <summary>
    /// Reports whether a supplied address satisfies the single legacy shape rule, by asking the
    /// Domain value object that owns that rule rather than by restating it.
    /// </summary>
    /// <param name="email">The address as supplied by the caller.</param>
    /// <returns>
    /// <see langword="true"/> when the address is well formed, or when no address was supplied
    /// at all; <see langword="false"/> only when a supplied address is malformed.
    /// </returns>
    /// <remarks>
    /// An absent or blank value returns <see langword="true"/> on purpose. Requiredness is the
    /// business of the separate emptiness rule, and reporting the same field twice for one
    /// mistake would put two messages on it. Note that the check is applied to the WHOLE value:
    /// the factory anchors the legacy rule rather than searching within the string, which is the
    /// documented tightening described at the rule site.
    /// </remarks>
    private static bool BeAWellFormedEmailAddress(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return true;
        }

        return EmailAddress.TryCreate(email, out _);
    }
}
