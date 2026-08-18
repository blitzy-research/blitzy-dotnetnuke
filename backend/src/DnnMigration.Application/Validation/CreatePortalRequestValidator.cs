using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Options;
using DnnMigration.Domain.ValueObjects;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Validates the inbound <see cref="CreatePortalRequest"/> contract for <c>POST /api/v1/portals</c>,
/// reproducing the rule set measured on the legacy portal signup screen exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Name lengths follow the SCHEMA, not the markup.</b> The administrator's given and family names are
/// capped at 50 characters even though the legacy text boxes allowed 100.
/// </para>
/// <para>
/// <b>What this type deliberately cannot do.</b> It performs no input or output of any kind: no uniqueness
/// probe, no template lookup on disk, no tenant resolution. Those need a repository or the file system,
/// both of which belong to layers this one does not reference.
/// </para>
/// </remarks>
public class CreatePortalRequestValidator : AbstractValidator<CreatePortalRequest>
{
    /// <summary>
    /// Message of the <c>valPortalName</c> required-field validator, reproduced verbatim including its
    /// trailing full stop.
    /// </summary>
    private const string PortalAliasRequiredMessage = "Portal Name Is Required.";

    /// <summary>
    /// Message of the <c>valTemplate</c> required-field validator, reproduced verbatim. It is the one
    /// message on the screen that carries no trailing full stop, and that is preserved.
    /// </summary>
    private const string TemplateRequiredMessage = "Please select a template file";

    /// <summary>Message of the <c>valFirstName</c> required-field validator, reproduced verbatim.</summary>
    private const string FirstNameRequiredMessage = "First Name Is Required.";

    /// <summary>Message of the <c>valLastName</c> required-field validator, reproduced verbatim.</summary>
    private const string LastNameRequiredMessage = "Last Name Is Required.";

    /// <summary>Message of the <c>valUsername</c> required-field validator, reproduced verbatim.</summary>
    private const string UsernameRequiredMessage = "Username Is Required.";

    /// <summary>Message of the <c>valPassword</c> required-field validator, reproduced verbatim.</summary>
    private const string PasswordRequiredMessage = "Password Is Required.";

    // Two DIFFERENT legacy resources share the key InvalidPassword.Text, and the one this screen used is
    // not the one that describes a policy failure.

    /// <summary>
    /// Legacy wording for a credential that fails the bound policy, from
    /// <c>Website/App_GlobalResources/SharedResources.resx:L285-L286</c>. The two bracketed tokens are
    /// substituted from the bound policy at construction time, exactly as the legacy membership path
    /// substituted them at run time.
    /// </summary>
    private const string InvalidPasswordMessageTemplate =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at "
        + "least [PasswordLength] characters in length and contain at least [NoneAlphabet] "
        + "non-alphanumeric characters.";

    /// <summary>The token the legacy code replaced with the configured minimum password length.</summary>
    private const string PasswordLengthToken = "[PasswordLength]";

    /// <summary>
    /// The token the legacy code replaced with the configured minimum count of non-alphanumeric characters.
    /// </summary>
    private const string NoneAlphabetToken = "[NoneAlphabet]";

    /// <summary>
    /// Upper bound on the time any single strength-pattern evaluation may consume, so that a pathological
    /// input cannot hold a request thread. The value matches the sibling credential validators.
    /// </summary>
    private static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Message of the <c>valEmail</c> required-field validator, reproduced verbatim.</summary>
    private const string EmailRequiredMessage = "Email Is Required.";

    /// <summary>
    /// Wording for a malformed mail address, taken verbatim from the shared, application-wide
    /// <c>InvalidEmail.Text</c> entry at <c>Website/App_GlobalResources/SharedResources.resx:L282</c>,
    /// including its two spaces after the sentence period.
    /// </summary>
    /// <remarks>
    /// The spacing in that resource file is inconsistent per key and is therefore never normalised in bulk.
    /// L276, L282, L291 and L294 use two spaces after a sentence period while L297 and L300 use one, and
    /// L300 additionally carries the long-standing spelling of "Futher".
    /// </remarks>
    private const string InvalidEmailMessage =
        "The email address specified is invalid.  Please specify a valid email address.";

    /// <summary>
    /// The <c>InvalidName</c> message the signup code-behind raised when the alias contained a character it
    /// did not permit, taken verbatim from
    /// <c>Website/admin/Portal/App_LocalResources/Signup.ascx.resx:L234-L235</c>.
    /// </summary>
    private const string InvalidAliasCharacterMessage =
        "The Portal Name Must Not Contain Spaces Or Punctuation.";

    /// <summary>
    /// Message for the bare-file-name rule on the template selection. The legacy screen had no equivalent
    /// wording because its drop-down list made a path impossible to submit; see the note above that rule.
    /// </summary>
    private const string TemplateFileNameOnlyMessage =
        "Template must be a file name and must not contain a directory path.";

    /// <summary>
    /// Message for the contained-relative-directory rule on the home directory. This is the legacy screen's
    /// own wording, reproduced verbatim from
    /// <c>Website/admin/Portal/App_LocalResources/Signup.ascx.resx:L288-L289</c>, so a caller submitting an
    /// unusable folder sees the text the legacy application showed.
    /// </summary>
    private const string InvalidHomeFolderMessage =
        "The Home Folder you specified is not valid.";

    /// <summary>
    /// Maximum length of the portal title, which the markup and the schema agree on: the <c>txtTitle</c>
    /// text box declares <c>maxlength</c> 128 and the column is <c>nvarchar(128)</c>
    /// (<c>01.00.00.SqlDataProvider:L79</c>, preserved by the table rebuild at
    /// <c>01.00.05.SqlDataProvider:L1364</c>).
    /// </summary>
    private const int PortalNameMaximumLength = 128;

    /// <summary>
    /// Maximum length of the portal alias, taken from the <c>maxlength</c> 128 on the <c>txtPortalName</c>
    /// text box. The column is wider at <c>nvarchar(200)</c> (<c>01.00.00.SqlDataProvider:L78</c>), so the
    /// measured markup limit is the binding one.
    /// </summary>
    private const int PortalAliasMaximumLength = 128;

    /// <summary>
    /// Maximum length shared by the description and keywords fields: <c>maxlength</c> 500 on both text
    /// boxes and <c>nvarchar(500)</c> on both columns (<c>01.00.02.SqlDataProvider:L884-L885</c>).
    /// </summary>
    private const int MetadataMaximumLength = 500;

    /// <summary>
    /// Maximum length of the administrator's given and family names. This is the schema width of 50 rather
    /// than the markup's 100; the reasoning is on the type.
    /// </summary>
    private const int PersonNameMaximumLength = 50;

    /// <summary>
    /// Maximum length of the administrator's sign-in name: <c>Username nvarchar(100) NOT NULL</c>
    /// (<c>01.00.06.SqlDataProvider:L197</c>), matching <c>maxlength</c> 100 on the text box at
    /// <c>signup.ascx:L89</c>.
    /// </summary>
    /// <remarks>
    /// This width is genuinely terminal. The column is introduced by the second <c>Tmp_Users</c> rebuild
    /// and the only later statement touching it adds the uniqueness constraint
    /// <c>IX_{objectQualifier}Users</c> (<c>03.00.09.SqlDataProvider:L422</c>); no <c>ALTER COLUMN</c> ever
    /// widens it.
    /// </remarks>
    private const int UsernameMaximumLength = 100;

    /// <summary>
    /// Maximum length of the administrator's electronic mail address: the terminal <c>Email nvarchar(256)
    /// NULL</c> column of <c>dbo.Users</c>.
    /// </summary>
    private const int EmailMaximumLength = 256;

    /// <summary>
    /// The characters a child portal's alias may contain, copied verbatim from <c>Signup.ascx.vb:L192</c>
    /// and L207. Lower case only, which is what makes the legacy lower-casing at L183 lossless.
    /// </summary>
    private const string ChildPortalAliasCharacters = "abcdefghijklmnopqrstuvwxyz0123456789-";

    /// <summary>
    /// The characters a parent portal's alias may contain. The legacy code builds this set by widening the
    /// child set with three punctuation characters when the portal is not a child, which lets a parent
    /// alias carry a host name, a port and a path segment.
    /// </summary>
    private const string ParentPortalAliasCharacters = ChildPortalAliasCharacters + "./:";

    /// <summary>
    /// The scheme prefix the legacy screen stripped from the alias before inspecting its characters.
    /// </summary>
    private const string LegacySchemePrefix = "http://";

    /// <summary>
    /// The characters whose presence turns a template name into a path. Forward and backward slash cover a
    /// relative or absolute path and a network share; the colon covers a drive qualifier and an alternate
    /// data stream.
    /// </summary>
    private const string PathQualifyingCharacters = "/\\:";

    /// <summary>
    /// The parent-directory token, rejected in a template name in its own right because appending it to a
    /// directory path that already ends in a separator escapes that directory without needing a separator
    /// of its own.
    /// </summary>
    private const string ParentDirectoryToken = "..";

    /// <summary>
    /// Declares the rule set, binding the administrator credential's thresholds and the numbers substituted
    /// into its message from the supplied policy rather than restating any of them.
    /// </summary>
    /// <param name="policy">
    /// The password policy in force, preserved verbatim from the legacy membership provider registration
    /// and bound by the Api layer from the configuration section named by <see
    /// cref="PasswordPolicyOptions.SectionName"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="policy"/> is <see langword="null"/>.
    /// </exception>
    // The credential policy is enforced here, not merely required to be present. Reasoning from the SCREEN
    // alone would give the opposite answer - signup.ascx declared exactly one validator on txtPassword,
    // which suggests requiredness and nothing more - but the screen is not the flow.
    public CreatePortalRequestValidator(PasswordPolicyOptions policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Each of the legacy validators ran in the browser and blocked the postback, so a field that failed
        // its required check never reached the server-side checks that came after it.
        RuleLevelCascadeMode = CascadeMode.Stop;

        ClassLevelCascadeMode = CascadeMode.Continue;

        // Read once, here, so no rule dereferences the policy while validating and so the message and
        // the rules that produce it can never disagree about the numbers.
        int minimumPasswordLength = policy.MinRequiredPasswordLength;
        int minimumNonAlphanumericCharacters = policy.MinRequiredNonAlphanumericCharacters;

        // Both bracketed tokens are substituted from the BOUND POLICY VALUES, reproducing
        // UserController.GetUserCreateStatus L607-L609, which read the same two numbers from the provider
        // facade and rewrote them into the message at run time.
        string invalidPasswordMessage = InvalidPasswordMessageTemplate
            .Replace(
                PasswordLengthToken,
                minimumPasswordLength.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                NoneAlphabetToken,
                minimumNonAlphanumericCharacters.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        RuleFor(request => request.PortalName)
            .MaximumLength(PortalNameMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        RuleFor(request => request.PortalAlias)
            .NotEmpty().WithMessage(PortalAliasRequiredMessage)
            .MaximumLength(PortalAliasMaximumLength)
            .Must((request, alias) => HasOnlyPermittedAliasCharacters(alias, request.IsChildPortal))
                .WithMessage(InvalidAliasCharacterMessage)
            .Must(alias => IsWithinAddressableTopology(alias))
                .WithMessage(PortalAliasRules.UnsupportedPathMessage);

        // The description and keywords fields carried no validator; only the
        // markup and column width constrain them, and the two agree.
        RuleFor(request => request.Description)
            .MaximumLength(MetadataMaximumLength)
            .Must(TextIntegrityRules.IsMultiLineSafe)
            .WithMessage(TextIntegrityRules.MultiLineMessage);

        RuleFor(request => request.KeyWords)
            .MaximumLength(MetadataMaximumLength)
            .Must(TextIntegrityRules.IsMultiLineSafe)
            .WithMessage(TextIntegrityRules.MultiLineMessage);

        // MIGRATION: what the legacy screen did NOT do, and what is added here. The screen's own
        // InvalidHomeFolder check resolved the submitted value to a physical path and reported failure only
        // if that resolution came back empty; it applied no shape rule whatsoever.
        RuleFor(request => request.HomeDirectory)
            .MaximumLength(HomeDirectoryMaximumLength)
            .Must(IsSafeRelativeHomeDirectory)
                .WithMessage(HomeDirectoryInvalidMessage)
            // ORDER IS DELIBERATE. The containment predicate above already refuses a control character and
            // words that refusal as the folder problem it is, so the shared rule is asked second and adds
            // what containment does not consider: the zero-width and bidirectional characters that pass every
            // path test and still leave a folder name no operator can retype.
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        // ValTemplate is declared with InitialValue="-1", which made the drop-down's unselected state fail
        // its required check.
        RuleFor(request => request.TemplateFile)
            .NotEmpty().WithMessage(TemplateRequiredMessage)
            .Must(IsBareFileName).WithMessage(TemplateFileNameOnlyMessage)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        // 50, not the markup's 100.
        RuleFor(request => request.AdministratorFirstName)
            .NotEmpty().WithMessage(FirstNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        // 50 here for the same reason, and requiredness is doubly grounded: the family-name column is
        // nullable in the baseline schema (01.00.00.SqlDataProvider:L100) and becomes NOT NULL in the
        // rebuild at 01.00.06.SqlDataProvider:L186, which matches the valLastName validator the screen
        // already declared.
        RuleFor(request => request.AdministratorLastName)
            .NotEmpty().WithMessage(LastNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        RuleFor(request => request.AdministratorUsername)
            .NotEmpty().WithMessage(UsernameRequiredMessage)
            .MaximumLength(UsernameMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        // MIGRATION: the markup also declares maxlength 20 on txtPassword, matching the plaintext Password
        // nvarchar(20) column of the baseline schema (01.00.00.SqlDataProvider:L106). That ceiling is
        // deliberately NOT reproduced.
        RuleFor(request => request.AdministratorPassword)
            .NotEmpty().WithMessage(PasswordRequiredMessage)
            .MinimumLength(minimumPasswordLength).WithMessage(invalidPasswordMessage)
            .Must(CredentialBounds.IsWithinMaximumByteLength)
                .WithMessage(CredentialBounds.MaximumByteLengthMessage);

        // A configured minimum of zero SKIPS THE RULE ENTIRELY rather than registering a comparison that
        // can never fail, for the same reason the strength rule below is skipped when no pattern is
        // configured: a rule that cannot fail is indistinguishable from enforcement under review, and
        // evaluating it would scan every character of every submitted credential to reach a foregone
        // conclusion.
        if (minimumNonAlphanumericCharacters > 0)
        {
            RuleFor(request => request.AdministratorPassword)
                .Must(password =>
                    HasEnoughNonAlphanumericCharacters(password, minimumNonAlphanumericCharacters))
                .WithMessage(invalidPasswordMessage)
                .When(request => !string.IsNullOrEmpty(request.AdministratorPassword));
        }

        // An unconfigured strength pattern SKIPS THE RULE ENTIRELY rather than compiling an empty one. An
        // empty pattern matches every input, so applying it would be a silent no-op wearing the appearance
        // of enforcement - strictly worse than no rule, because it would survive review.
        if (!string.IsNullOrEmpty(policy.PasswordStrengthRegularExpression))
        {
            Regex configuredStrengthPattern = new(
                policy.PasswordStrengthRegularExpression,
                RegexOptions.CultureInvariant,
                PatternMatchTimeout);

            RuleFor(request => request.AdministratorPassword)
                .Matches(configuredStrengthPattern).WithMessage(invalidPasswordMessage)
                .When(request => !string.IsNullOrEmpty(request.AdministratorPassword));
        }

        // No uniqueness rule either. The membership provider was registered with
        // requiresUniqueEmail="false", so two users could already share an address and existing rows may do
        // so.
        RuleFor(request => request.AdministratorEmail)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(EmailRequiredMessage)
            .MaximumLength(EmailMaximumLength).WithMessage(InvalidEmailMessage)
            .Must(BeAWellFormedEmailAddress).WithMessage(InvalidEmailMessage);
    }

    /// <summary>
    /// Reports whether every character of a submitted portal alias belongs to the set the legacy signup
    /// screen permitted.
    /// </summary>
    /// <param name="submittedAlias">The alias exactly as submitted.</param>
    /// <param name="isChildPortal">
    /// Selects the permitted character set and, for a child portal, the portion of the alias the legacy
    /// code measured.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the alias is absent — so that requiredness is reported once, by the rule
    /// that owns it — or when every measured character is permitted; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The case conversion is culture-invariant, whereas the legacy call was culture-sensitive. Every
    /// permitted character is a printable ASCII character, so the two agree on all of them; they can differ
    /// only on non-ASCII input, which both reject.
    /// </remarks>
    private static bool HasOnlyPermittedAliasCharacters(string? submittedAlias, bool isChildPortal)
    {
        if (string.IsNullOrEmpty(submittedAlias))
        {
            return true;
        }

        string normalisedAlias = NormaliseSubmittedAlias(submittedAlias);

        string measuredAlias = isChildPortal
            ? normalisedAlias[(normalisedAlias.LastIndexOf('/') + 1)..]
            : normalisedAlias;

        string permittedCharacters = isChildPortal
            ? ChildPortalAliasCharacters
            : ParentPortalAliasCharacters;

        foreach (char character in measuredAlias)
        {
            if (!permittedCharacters.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the two normalisations the legacy signup screen applied before it measured anything.
    /// </summary>
    /// <param name="submittedAlias">The alias exactly as submitted.</param>
    /// <returns>The value lower-cased and stripped of a leading scheme prefix.</returns>
    private static string NormaliseSubmittedAlias(string submittedAlias) =>
        submittedAlias
            .ToLowerInvariant()
            .Replace(LegacySchemePrefix, string.Empty, StringComparison.Ordinal);

    /// <summary>Reports whether a submitted alias names a path this deployment can deliver a request to.</summary>
    /// <param name="submittedAlias">The alias exactly as submitted.</param>
    /// <returns>
    /// <see langword="true"/> when the normalised value carries no path, or carries a path within
    /// <c>PortalAliasTopology</c>.
    /// </returns>
    private static bool IsWithinAddressableTopology(string? submittedAlias) =>
        string.IsNullOrEmpty(submittedAlias)
        || PortalAliasRules.IsWithinSupportedTopology(NormaliseSubmittedAlias(submittedAlias));

    /// <summary>
    /// Reports whether a submitted template selection names a file without qualifying it with a path.
    /// </summary>
    /// <param name="submittedTemplateName">
    /// The template name exactly as submitted, without the extension the service appends.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value is absent — so that requiredness is reported once, by the rule
    /// that owns it — or when it names a file only; otherwise <see langword="false"/>.
    /// </returns>
    private static bool IsBareFileName(string? submittedTemplateName)
    {
        if (string.IsNullOrEmpty(submittedTemplateName))
        {
            return true;
        }

        foreach (char character in submittedTemplateName)
        {
            if (PathQualifyingCharacters.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return !submittedTemplateName.Contains(ParentDirectoryToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reports whether the submitted administrator credential carries at least the configured number of
    /// characters outside the ranges <c>0-9</c>, <c>A-Z</c> and <c>a-z</c>.
    /// </summary>
    /// <param name="password">The submitted credential, which may be absent or blank.</param>
    /// <param name="minimum">The configured minimum count.</param>
    /// <returns>
    /// <see langword="true"/> when the credential carries at least <paramref name="minimum"/> such
    /// characters, and also when it is absent or blank, for the reason given on <see
    /// cref="CredentialBounds.IsWithinMaximumByteLength(string?)"/>.
    /// </returns>
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
    /// Reports whether the administrator's mail address satisfies the single legacy shape rule, by asking
    /// the Domain value object that owns that rule rather than by restating it here.
    /// </summary>
    /// <param name="submittedEmail">The address as supplied by the caller, which may be absent.</param>
    /// <returns>
    /// <see langword="true"/> when the address is well formed, and also when none was supplied; <see
    /// langword="false"/> only when a supplied address is malformed.
    /// </returns>
    private static bool BeAWellFormedEmailAddress(string? submittedEmail)
    {
        if (string.IsNullOrWhiteSpace(submittedEmail))
        {
            return true;
        }

        return EmailAddress.TryCreate(submittedEmail, out _);
    }

    /// <summary>Maximum length of the portal-relative home directory.</summary>
    private const int HomeDirectoryMaximumLength = 100;

    /// <summary>The message reported when a submitted home directory is rejected.</summary>
    private const string HomeDirectoryInvalidMessage = "The Home Folder you specified is not valid.";

    /// <summary>
    /// The single separator a submitted home directory may contain. The legacy screen, the legacy default
    /// and the configured format all use it.
    /// </summary>
    private const char DirectorySeparator = '/';

    /// <summary>
    /// The relative-path segment denoting the current directory. Rejected because it is redundant and
    /// because accepting it would mean storing a value whose stored form and canonical form differ.
    /// </summary>
    private const string CurrentSegment = ".";

    /// <summary>The relative-path segment denoting the parent directory: the traversal vector itself.</summary>
    private const string ParentSegment = "..";

    /// <summary>Characters a submitted home directory may never contain.</summary>
    private const string ForbiddenCharacters = "\\:*?\"<>|";

    /// <summary>A synthetic, never-touched root used only to prove containment lexically.</summary>
    private const string ContainmentProbeRoot = "/dnn-portal-home-containment-probe/";

    /// <summary>
    /// Reports whether a submitted home directory is a safe, portal-relative directory that cannot denote
    /// anything outside the root it is later combined with.
    /// </summary>
    /// <param name="submittedDirectory">The submitted value, which may be absent.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or a relative directory built only from
    /// ordinary segments separated by <c>/</c>; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Rejected forms, each for a specific reason: a value that is only white space, because it is neither
    /// a directory nor an omission; any character in <see cref="ForbiddenCharacters"/> or any control
    /// character, so no backslash, drive-qualified or wildcard form survives; a leading separator, which
    /// covers both the rooted form <c>/x</c> and the UNC form <c>//server/share</c>; an empty or blank
    /// segment, so <c>a//b</c> cannot be stored in a form that differs from its canonical form; and any
    /// segment equal to <see cref="CurrentSegment"/> or <see cref="ParentSegment"/>.
    /// </remarks>
    private static bool IsSafeRelativeHomeDirectory(string? submittedDirectory)
    {
        if (string.IsNullOrEmpty(submittedDirectory))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(submittedDirectory))
        {
            return false;
        }

        foreach (char character in submittedDirectory)
        {
            if (char.IsControl(character)
                || ForbiddenCharacters.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (submittedDirectory[0] == DirectorySeparator)
        {
            return false;
        }

        string candidate = submittedDirectory[^1] == DirectorySeparator
            ? submittedDirectory[..^1]
            : submittedDirectory;

        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (string segment in candidate.Split(DirectorySeparator))
        {
            if (string.IsNullOrWhiteSpace(segment)
                || string.Equals(segment, CurrentSegment, StringComparison.Ordinal)
                || string.Equals(segment, ParentSegment, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return RemainsContained(candidate);
    }

    /// <summary>
    /// Canonicalises a candidate relative directory against a synthetic root and reports whether the result
    /// is still strictly beneath that root.
    /// </summary>
    /// <param name="relativeDirectory">The candidate directory, already stripped of any trailing separator.</param>
    /// <returns>
    /// <see langword="true"/> when the canonical form remains beneath <see cref="ContainmentProbeRoot"/>;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <c>Path.GetFullPath</c> performs no input or output: it neither creates nor inspects anything on
    /// disk, and because the combined path is already rooted by the probe root the result cannot depend on
    /// the process's current directory either. That is what keeps this check inside a layer which must not
    /// reach the file system.
    /// </remarks>
    private static bool RemainsContained(string relativeDirectory)
    {
        try
        {
            string canonical = Path.GetFullPath(
                Path.Combine(ContainmentProbeRoot, relativeDirectory));

            return canonical.Length > ContainmentProbeRoot.Length
                && canonical.StartsWith(ContainmentProbeRoot, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }
}
