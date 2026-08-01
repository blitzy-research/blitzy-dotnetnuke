using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using FluentValidation;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Legacy wording for an invalid recovery question, from <c>SharedResources.resx</c>
    /// L288.
    /// </summary>
    private const string InvalidQuestionMessage =
        "The question specified is invalid.  Please specify a valid question.";

    /// <summary>
    /// Legacy wording for an invalid recovery answer, from <c>SharedResources.resx</c>
    /// L279.
    /// </summary>
    private const string InvalidAnswerMessage =
        "The answer specified is invalid.  Please specify a valid answer to the question.";

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

    // MIGRATION: net-new wording, and marked as such. The legacy creation screen enforced
    // the display-name ceiling through a markup attribute on a property-editor field,
    // which silently truncated typing and produced no message at all, so there is no
    // legacy string to reproduce. The sentence below follows the measured pattern of the
    // global entries above so that it reads as part of the same family.

    /// <summary>
    /// Net-new wording for an over-long display name; no legacy equivalent exists.
    /// </summary>
    private const string InvalidDisplayNameMessage =
        "The display name specified is invalid.  Please specify a valid display name.";

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

    /// <summary>
    /// Terminal column width of the email address: <c>nvarchar(100)</c>.
    /// </summary>
    private const int EmailMaximumLength = 100;

    /// <summary>
    /// Upper bound on the time any single pattern evaluation may consume, so that a
    /// pathological input cannot hold a request thread.
    /// </summary>
    private static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(250);

    // MIGRATION: the email pattern is the legacy constant reproduced character for
    // character from Library/Components/Shared/Globals.vb L132. Two measured properties of
    // it are preserved rather than modernised. Its trailing quantifier caps the top-level
    // domain at four letters, so longer modern domains are rejected exactly as the legacy
    // application rejected them; and it is word-boundary delimited rather than anchored to
    // the whole string, so a valid address surrounded by other text still satisfies it.
    // Anchoring it would be a tightening and is not applied. Note also a path correction:
    // the plan cites this constant under a Common folder, which does not exist in the
    // repository - the constant lives under Shared.

    /// <summary>
    /// The legacy email pattern, reproduced verbatim from
    /// <c>Library/Components/Shared/Globals.vb</c> L132.
    /// </summary>
    private static readonly Regex LegacyEmailPattern = new(
        @"\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        PatternMatchTimeout);

    /// <summary>
    /// Builds the rule set, binding every password threshold and every substituted
    /// message token from the supplied policy rather than restating any of them.
    /// </summary>
    /// <param name="passwordPolicyOptions">
    /// The password policy in force, preserved verbatim from the legacy membership
    /// provider registration. It is read once here, at construction time; no rule
    /// dereferences it again while validating. This is the validator's only dependency:
    /// it takes no repository, no clock and no credential-store abstraction, because
    /// none of those is needed to decide whether a request is well formed.
    /// </param>
    public CreateUserRequestValidator(IOptions<PasswordPolicyOptions> passwordPolicyOptions)
    {
        ArgumentNullException.ThrowIfNull(passwordPolicyOptions);

        PasswordPolicyOptions policy = passwordPolicyOptions.Value;

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
        // MIGRATION: the family name is REQUIRED even though it arrives as an optional
        // value on the wire. The baseline script declared the column nullable, but the
        // rebuild made it non-nullable and no later script relaxed it, so accepting a
        // missing family name would defer a constraint violation to write time. Wire
        // optionality exists only so that an omitted field and an explicitly blank one can
        // be told apart and reported precisely.
        RuleFor(request => request.FirstName)
            .NotEmpty().WithMessage(FirstNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength).WithMessage(InvalidUserNameMessage);

        RuleFor(request => request.LastName!)
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
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage(EmailRequiredMessage)
            .MaximumLength(EmailMaximumLength).WithMessage(InvalidEmailMessage)
            .Matches(LegacyEmailPattern).WithMessage(EmailFormatMessage);

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
        // MIGRATION: no maximum length. The legacy inputs carried a markup ceiling of twenty
        // characters and the baseline column matched it, but the rebuild widened that column
        // and credentials later moved to an external store entirely, so the ceiling
        // described a plaintext column that no longer exists. Under a one-way hash the
        // stored width is independent of the input length. No defensive cap is introduced
        // either, because the request contract explicitly records that none applies.
        //
        // MIGRATION: password retrieval is not carried forward. The legacy facility at
        // UserController.vb L433 returned a stored credential and passed the user by
        // reference to do it; the feature is dropped outright, and the by-reference idiom is
        // retired everywhere, so no endpoint, screen or rule in the target reads a password
        // back. Password RESET survives and is a different feature.
        RuleFor(request => request.Password!)
            .NotEmpty().WithMessage(invalidPasswordMessage)
            .MinimumLength(policy.MinRequiredPasswordLength).WithMessage(invalidPasswordMessage)
            .When(request => !request.GenerateRandomPassword);

        // MIGRATION: the confirmation comparison is conditional on the same branch as the
        // password itself. The legacy screen offered a genuinely separate path on which the
        // server generated the credential and both inputs were bypassed, so applying either
        // rule on that path would reject a request the legacy screen accepted. The
        // comparison is ordinal, matching the legacy string inequality test.
        RuleFor(request => request.ConfirmPassword)
            .Equal(request => request.Password).WithMessage(PasswordMismatchMessage)
            .When(request => !request.GenerateRandomPassword);

        // MIGRATION: the recovery question and answer are inert on the shipped
        // configuration. The provider's question-and-answer attribute at
        // Website/release.config L241 is false, and the legacy screen corroborates it by
        // declaring both input rows hidden and skipping their checks unless the provider
        // demanded them. The guard below reproduces that conditionality faithfully rather
        // than hard-wiring the absence, so the rules exist but cannot fire by default. Four
        // related strings sit in the resource files; their presence records what the wording
        // WOULD be and is not evidence that anything was enforced.
        RuleFor(request => request.PasswordQuestion!)
            .NotEmpty().WithMessage(InvalidQuestionMessage)
            .When(_ => policy.RequiresQuestionAndAnswer);

        RuleFor(request => request.PasswordAnswer!)
            .NotEmpty().WithMessage(InvalidAnswerMessage)
            .When(_ => policy.RequiresQuestionAndAnswer);

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

            RuleFor(request => request.Password!)
                .Matches(configuredStrengthPattern).WithMessage(invalidPasswordMessage)
                .When(request => !request.GenerateRandomPassword
                    && !string.IsNullOrEmpty(request.Password));
        }

        // MIGRATION: no lower-bound test on any identifier, and none is even reachable -
        // this contract carries no key of any kind, because a caller-supplied tenant key
        // would breach tenant isolation and a caller-supplied user key would be ignored
        // rather than honoured. The prohibition is recorded anyway, because the identity
        // seeds make the usual idioms actively wrong: the portal key seeds at negative one
        // and the role and tab keys seed at zero, so both values are legitimate keys, while
        // the legacy absent-integer sentinel is also negative one. A lower-bound or
        // inequality test against either value would reject real rows. The remaining
        // members of this contract are three flags, and a flag has no invalid value to
        // reject: the generation flag selects the branch guarded above, and the approval and
        // notification flags are consumed by the service.
    }
}
