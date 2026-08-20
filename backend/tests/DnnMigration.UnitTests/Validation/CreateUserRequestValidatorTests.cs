using System.Globalization;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule parity between <see cref="CreateUserRequestValidator"/> and the legacy DotNetNuke
/// 4.9 account-creation validation contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>The policy is preserved verbatim and is deliberately loose.</b> A minimum length of seven,
/// <em>zero</em> required non-alphanumeric characters, no recovery question, and no uniqueness requirement
/// on either the address or the login name.
/// </para>
/// <para>
/// <b>Two spaces after a sentence period is not a typing slip.</b> The global resource file spaces some
/// entries that way and others with one space, and the wording is reproduced per entry rather than
/// normalised across the set, so that an existing operator sees the message they already know.
/// </para>
/// </remarks>
public class CreateUserRequestValidatorTests
{
    /// <summary>Screen wording for a missing login name, from <c>User.ascx.resx</c> L154.</summary>
    private const string UsernameRequired = "User name is required";

    /// <summary>Screen wording for a missing given name, from <c>User.ascx.resx</c> L157.</summary>
    private const string FirstNameRequired = "First name is required";

    /// <summary>Screen wording for a missing family name, from <c>User.ascx.resx</c> L160.</summary>
    private const string LastNameRequired = "Last name is required";

    /// <summary>Screen wording for a missing address, from <c>User.ascx.resx</c> L163.</summary>
    private const string EmailRequired = "Email is required";

    /// <summary>Screen wording for a malformed address, from <c>User.ascx.resx</c> L220.</summary>
    private const string EmailFormat = "You must enter a valid email address";

    /// <summary>
    /// Global wording for an invalid login name, from <c>SharedResources.resx</c> L291. Two spaces
    /// follow the sentence period, exactly as the resource file has them.
    /// </summary>
    private const string InvalidUsername = "The username specified is invalid.  Please specify a valid username.";

    /// <summary>
    /// Global wording for an invalid address, from <c>SharedResources.resx</c> L282, double spacing
    /// included.
    /// </summary>
    private const string InvalidEmail =
        "The email address specified is invalid.  Please specify a valid email address.";

    /// <summary>
    /// Authored wording for an over-long display name. The legacy screen enforced this ceiling with a
    /// markup attribute that silently truncated typing and produced no message, so there is no legacy
    /// string to reproduce; the wording follows the pattern of the global entries above.
    /// </summary>
    private const string InvalidDisplayName =
        "The display name specified is invalid.  Please specify a valid display name.";

    /// <summary>
    /// Global wording for a credential/confirmation mismatch, from <c>SharedResources.resx</c> L852.
    /// It intentionally carries <em>no</em> trailing period.
    /// </summary>
    /// <remarks>
    /// MIGRATION: two competing legacy entries exist. L852 is the one the legacy code resolved, because
    /// it passed a bare resource key and DotNetNuke resolves a bare key to the primary-text entry. The
    /// differently worded duplicate at L969, which does carry a trailing period, is deliberately not
    /// used here; it is named so that nobody later "corrects" this expectation to the other variant.
    /// </remarks>
    private const string PasswordMismatch = "The Password and Confirmation Passwords do not match";

    /// <summary>The token the legacy code replaced with the configured minimum credential length.</summary>
    private const string PasswordLengthToken = "[PasswordLength]";

    /// <summary>
    /// The token the legacy code replaced with the configured minimum count of non-alphanumeric characters.
    /// </summary>
    private const string NoneAlphabetToken = "[NoneAlphabet]";

    /// <summary>
    /// The unsubstituted credential message, from <c>SharedResources.resx</c> L285. Both bracketed tokens
    /// are still present, and both sentence periods are followed by two spaces.
    /// </summary>
    /// <remarks>
    /// This suite holds the template rather than any rendered sentence, and substitutes it from the same
    /// policy the validator was handed. Hard-coding a rendered message would let a policy change pass a
    /// test that no longer describes the code.
    /// </remarks>
    private const string InvalidPasswordTemplate =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at least [PasswordLength] characters in length and contain at least [NoneAlphabet] non-alphanumeric characters.";

    /// <summary>The policy in force, preserved verbatim from the legacy membership provider registration.</summary>
    private readonly PasswordPolicyOptions _policy = ShippedPolicy();

    /// <summary>The validator under test, built on the shipped policy.</summary>
    private readonly CreateUserRequestValidator _validator = new(ShippedPolicy());

    /// <summary>Builds the legacy policy exactly as <c>Website/release.config</c> L239-L245 declares it.</summary>
    /// <returns>The shipped policy.</returns>
    /// <remarks>
    /// Three legacy attributes are deliberately absent, and are cited by line rather than quoted so that a
    /// search for them in this solution stays empty. The reversible-storage format and the
    /// credential-return switch have no counterpart under one-way hashing and are asserted nowhere.
    /// </remarks>
    private static PasswordPolicyOptions ShippedPolicy() => new();

    /// <summary>Builds a request that satisfies every rule under the shipped policy.</summary>
    /// <returns>The request.</returns>
    private static CreateUserRequest ValidRequest() => new()
    {
        Username = "member",
        FirstName = "Given",
        LastName = "Family",
        DisplayName = "Given Family",
        Email = "member@example.com",
        Password = "abc1234",
        ConfirmPassword = "abc1234",
        Authorize = true,
    };

    /// <summary>Renders the credential message the way the validator does, from the policy it was handed.</summary>
    /// <param name="policy">The policy whose numbers are substituted.</param>
    /// <returns>The rendered message.</returns>
    /// <remarks>
    /// The substitution is ordinal and the numbers are formatted with the invariant culture, matching the
    /// validator, so the expectation cannot drift with the ambient culture of the test host.
    /// </remarks>
    private static string InvalidPasswordFor(PasswordPolicyOptions policy) => InvalidPasswordTemplate
        .Replace(
            PasswordLengthToken,
            policy.MinRequiredPasswordLength.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal)
        .Replace(
            NoneAlphabetToken,
            policy.MinRequiredNonAlphanumericCharacters.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    /// <summary>Asserts that a result reports the given message against the given property.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property the failure must name.</param>
    /// <param name="message">The message the failure must carry, character for character.</param>
    private static void ShouldReport(ValidationResult result, string property, string message)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property);
        result.Errors.Should().Contain(
            failure => failure.PropertyName == property && failure.ErrorMessage == message,
            "the failure for {0} must carry exactly the legacy wording, but the result was {1}",
            property,
            Render(result));
    }

    /// <summary>Asserts that a result reports nothing at all against the given property.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property that must be unreported.</param>
    private static void ShouldNotReport(ValidationResult result, string property)
        => result.Errors.Should().NotContain(
            failure => failure.PropertyName == property,
            "no failure was expected for {0}, but the result was {1}",
            property,
            Render(result));

    /// <summary>Asserts that a result reports no failure of any kind.</summary>
    /// <param name="result">The result to inspect.</param>
    private static void ShouldAccept(ValidationResult result)
    {
        result.IsValid.Should().BeTrue("the request should have been accepted, but " + Render(result));
        result.Errors.Should().BeEmpty();
    }

    /// <summary>Renders every failure in a result, for use in an assertion reason.</summary>
    /// <param name="result">The result to render.</param>
    /// <returns>The rendered failures.</returns>
    private static string Render(ValidationResult result) => result.Errors.Count == 0
        ? "no failures were reported"
        : string.Join(
            " | ",
            result.Errors.Select(failure => failure.PropertyName + " => " + failure.ErrorMessage));

    // ------------------------------------------------------------------------
    // The accepted baseline, and the validator's own construction contract.
    // ------------------------------------------------------------------------

    /// <summary>A fully populated request is accepted with no failure of any kind.</summary>
    [Fact]
    public async Task AWellFormedRequest_IsAccepted()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        ShouldAccept(result);
    }

    /// <summary>
    /// The validator refuses to be constructed without a policy, because every credential rule and the
    /// credential message alike are read from it.
    /// </summary>
    [Fact]
    public void Constructor_WithoutAPolicy_Throws()
    {
        Action construct = () => _ = new CreateUserRequestValidator(null!);

        construct.Should().Throw<ArgumentNullException>();
    }

    /// <summary>The shipped policy is the measured legacy one, and it is coherent.</summary>
    [Fact]
    public void TheShippedPolicy_IsTheLegacyPolicyAndIsCoherent()
    {
        _policy.MinRequiredPasswordLength.Should().Be(
            7,
            "Website/release.config:L242 declares minRequiredPasswordLength=\"7\"");
        _policy.MinRequiredNonAlphanumericCharacters.Should().Be(
            0,
            "Website/release.config:L243 declares minRequiredNonalphanumericCharacters=\"0\"");
        _policy.RequiresUniqueEmail.Should().BeFalse(
            "Website/release.config:L244 declares requiresUniqueEmail=\"false\"");
        _policy.PasswordStrengthRegularExpression.Should().BeEmpty(
            "the setting appears in neither Website/release.config nor Website/development.config, so "
            + "the legacy emptiness test at UserController.vb:L1084 was always false");

        _policy.Validate().Should().BeEmpty(
            "the measured legacy policy must never be refused as a misconfiguration");
    }

    /// <summary>The login name is required, reporting the screen wording.</summary>
    /// <param name="username">The submitted login name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Username_WhenAbsent_ReportsTheScreenRequirement(string? username)
    {
        CreateUserRequest request = ValidRequest();
        request.Username = username!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Username), UsernameRequired);
    }

    /// <summary>The given name is required, reporting the screen wording.</summary>
    /// <param name="firstName">The submitted given name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FirstName_WhenAbsent_ReportsTheScreenRequirement(string? firstName)
    {
        CreateUserRequest request = ValidRequest();
        request.FirstName = firstName!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.FirstName), FirstNameRequired);
    }

    /// <summary>The family name is required, reporting the screen wording.</summary>
    /// <param name="lastName">The submitted family name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LastName_WhenAbsent_ReportsTheScreenRequirement(string? lastName)
    {
        CreateUserRequest request = ValidRequest();
        request.LastName = lastName!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.LastName), LastNameRequired);
    }

    /// <summary>
    /// The address is required, and an absent one reports both the requirement and the shape failure.
    /// </summary>
    /// <param name="email">The submitted address.</param>
    /// <remarks>
    /// Two failures rather than one, and deliberately asserted as two: the presence rule and the shape rule
    /// both run, because FluentValidation continues through a rule chain rather than stopping at the first
    /// failure. Both reach the caller under the same <c>Email</c> key, which is precisely what the RFC 7807
    /// <c>errors</c> dictionary models - an array of messages per field.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Email_WhenAbsent_ReportsBothTheRequirementAndTheShapeFailure(string? email)
    {
        CreateUserRequest request = ValidRequest();
        request.Email = email!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Email), EmailRequired);
        ShouldReport(result, nameof(CreateUserRequest.Email), EmailFormat);
    }

    /// <summary>The display name is the one name field that is optional.</summary>
    /// <param name="displayName">The submitted display name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DisplayName_WhenAbsent_IsAccepted(string? displayName)
    {
        CreateUserRequest request = ValidRequest();
        request.DisplayName = displayName!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    // Length ceilings, each bound to the TERMINAL column width.

    /// <summary>The login name is bounded at the width of the column that stores it.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    [Theory]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task Username_IsBoundedByItsTerminalColumnWidth(int length, bool accepted)
    {
        CreateUserRequest request = ValidRequest();
        request.Username = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(CreateUserRequest.Username), InvalidUsername);
        }
    }

    /// <summary>The given name is bounded at fifty characters.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task FirstName_IsBoundedAtFifty(int length, bool accepted)
    {
        CreateUserRequest request = ValidRequest();
        request.FirstName = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(CreateUserRequest.FirstName), InvalidUsername);
        }
    }

    /// <summary>The family name is bounded at fifty characters, reporting the same shared wording.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task LastName_IsBoundedAtFifty(int length, bool accepted)
    {
        CreateUserRequest request = ValidRequest();
        request.LastName = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(CreateUserRequest.LastName), InvalidUsername);
        }
    }

    /// <summary>The display name is optional but bounded at the width its attribute and column agree on.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    [Theory]
    [InlineData(127, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task DisplayName_IsBoundedAtOneHundredAndTwentyEight(int length, bool accepted)
    {
        CreateUserRequest request = ValidRequest();
        request.DisplayName = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(CreateUserRequest.DisplayName), InvalidDisplayName);
        }
    }

    /// <summary>The address is bounded at the width of the column that stores it, which is 256 and not 100.</summary>
    /// <param name="length">The total submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <remarks>
    /// 256, deliberately, and 100 would be wrong.
    /// </remarks>
    [Theory]
    [InlineData(255, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public async Task Email_IsBoundedByItsTerminalColumnWidth(int length, bool accepted)
    {
        const string domain = "@example.com";
        CreateUserRequest request = ValidRequest();
        request.Email = new string('d', length - domain.Length) + domain;
        request.Email.Should().HaveLength(length, "the fixture must exercise the intended boundary");

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(CreateUserRequest.Email), InvalidEmail);
        }
    }

    /// <summary>Every local-part character the legacy pattern permits is still accepted.</summary>
    /// <param name="email">The submitted address.</param>
    [Theory]
    [InlineData("member@example.com")]
    [InlineData("first.last@example.com")]
    [InlineData("_underscore@example.com")]
    [InlineData("percent%sign@example.com")]
    [InlineData("with-hyphen@example.com")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("o'brien@example.org")]
    [InlineData("MiXeD@CaSe.CoM")]
    [InlineData("  member@example.com  ")]
    public async Task Email_AcceptsEveryShapeTheLegacyPatternAccepted(string email)
    {
        CreateUserRequest request = ValidRequest();
        request.Email = email;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>Every shape the legacy pattern refused is still refused, quirks included.</summary>
    /// <param name="email">The submitted address.</param>
    [Theory]
    [InlineData("no-at-sign.example.com")]
    [InlineData("missing@domain")]
    [InlineData("member@example.c")]
    [InlineData("member@under_score.com")]
    [InlineData(".dot@example.com")]
    [InlineData("'apostrophe@example.com")]
    [InlineData("@example.com")]
    [InlineData("two@at@example.com")]
    public async Task Email_RefusesEveryShapeTheLegacyPatternRefused(string email)
    {
        CreateUserRequest request = ValidRequest();
        request.Email = email;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Email), EmailFormat);
    }

    /// <summary>
    /// A final domain label longer than four characters is <em>accepted</em>, which the legacy pattern
    /// would have refused.
    /// </summary>
    /// <param name="email">The submitted address.</param>
    /// <remarks>
    /// MIGRATION: DOCUMENTED DIVERGENCE, and the direction matters - this is a RELAXATION, so it can lock
    /// nobody out. The legacy pattern ends <c>[a-zA-Z]{2,4}\b</c>, capping the final domain label at four
    /// characters, so <c>.museum</c> and <c>.travel</c> were refused along with every other modern suffix.
    /// </remarks>
    [Theory]
    [InlineData("member@example.museum")]
    [InlineData("member@example.travel")]
    [InlineData("member@example.technology")]
    public async Task Email_WithALongFinalDomainLabel_IsAcceptedDespiteTheLegacyCap(string email)
    {
        CreateUserRequest request = ValidRequest();
        request.Email = email;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// Final domain labels of two and four characters - the two ends of the legacy cap - are accepted, and
    /// a one-character label is still refused.
    /// </summary>
    /// <param name="email">The submitted address.</param>
    /// <param name="accepted">Whether the address is accepted.</param>
    [Theory]
    [InlineData("member@example.co", true)]
    [InlineData("member@example.info", true)]
    [InlineData("member@example.c", false)]
    public async Task Email_StillRequiresAtLeastATwoLetterFinalLabel(string email, bool accepted)
    {
        CreateUserRequest request = ValidRequest();
        request.Email = email;

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(CreateUserRequest.Email), EmailFormat);
        }
    }

    /// <summary>
    /// An address buried in surrounding prose is <em>refused</em>, because the whole value is judged rather
    /// than any fragment of it.
    /// </summary>
    /// <param name="email">The submitted value.</param>
    /// <remarks>
    /// MIGRATION: DOCUMENTED DIVERGENCE from the pattern, and simultaneously a RESTORATION of the legacy
    /// screen's real behaviour - which is why it is not a tightening. <c>glbEmailRegEx</c> is delimited by
    /// <c>\b</c> word boundaries rather than anchored with <c>^</c> and <c>$</c>, so as a bare pattern it
    /// matches an address embedded in text.
    /// </remarks>
    [Theory]
    [InlineData("contact me at a@b.co please")]
    [InlineData("member@example.com and some junk")]
    [InlineData("prefix member@example.com")]
    public async Task Email_EmbeddedInSurroundingText_IsRefused(string email)
    {
        CreateUserRequest request = ValidRequest();
        request.Email = email;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Email), EmailFormat);
    }

    // L1073 length < MinPasswordLength -> ACTIVE (7) L1079 non-alphanumeric count < MinNonAlphanumeric ->
    // VACUOUS: the configured minimum is 0 and a match count is never below zero, so the branch is
    // unreachable.

    /// <summary>A credential is required, reporting the substituted legacy wording.</summary>
    /// <param name="password">The submitted credential.</param>
    /// <remarks>
    /// The confirmation is moved in step with the credential throughout these tests, so that a credential
    /// failure is never confused with a mismatch failure.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Password_WhenAbsent_ReportsTheSubstitutedLegacyWording(string? password)
    {
        CreateUserRequest request = ValidRequest();
        request.Password = password!;
        request.ConfirmPassword = password!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Password), InvalidPasswordFor(_policy));
    }

    /// <summary>
    /// THE MANDATED ASSERTION: a seven-character credential with no non-alphanumeric character at all is
    /// ACCEPTED.
    /// </summary>
    [Fact]
    public async Task Password_SevenCharactersPurelyAlphanumeric_IsAccepted()
    {
        CreateUserRequest request = ValidRequest();
        request.Password = "abc1234";
        request.ConfirmPassword = "abc1234";

        request.Password.Should().HaveLength(7);
        request.Password.Should().MatchRegex("^[A-Za-z0-9]+$", "the input must carry no symbol at all");

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// The credential boundary: one below the minimum fails, the minimum itself passes, and everything
    /// above it passes - with no upper bound short of the byte ceiling asserted separately below.
    /// </summary>
    /// <param name="password">The submitted credential.</param>
    /// <param name="accepted">Whether it is accepted.</param>
    [Theory]
    [InlineData("abc12", false)]
    [InlineData("abc123", false)]
    [InlineData("abc1234", true)]
    [InlineData("abcd1234", true)]
    [InlineData("abc1234!", true)]
    [InlineData("ABC1234", true)]
    [InlineData("       x", true)]
    public async Task Password_IsGovernedByLengthAlone(string password, bool accepted)
    {
        CreateUserRequest request = ValidRequest();
        request.Password = password;
        request.ConfirmPassword = password;

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(CreateUserRequest.Password), InvalidPasswordFor(_policy));
        }
    }

    /// <summary>
    /// There is no maximum credential <em>length</em>, only a maximum encoded <em>size</em>, and a long
    /// credential well inside that size is accepted.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <remarks>
    /// The legacy <c>Password nvarchar(20)</c> of <c>01.00.00.SqlDataProvider:L105</c> - widened to
    /// <c>nvarchar(50)</c> by the terminal rebuild at <c>01.00.06.SqlDataProvider:L191</c> and then dropped
    /// from the table altogether at <c>02.02.01.SqlDataProvider:L50-L51</c> - was a plaintext-storage
    /// artefact and is emphatically NOT a validation rule.
    /// </remarks>
    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(256)]
    public async Task Password_HasNoMaximumLength(int length)
    {
        string password = new('a', length);
        CreateUserRequest request = ValidRequest();
        request.Password = password;
        request.ConfirmPassword = password;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// The one credential ceiling that does exist is a net-new bound on encoded size, measured in UTF-8
    /// bytes rather than characters.
    /// </summary>
    /// <param name="length">The number of characters submitted.</param>
    /// <param name="fill">The character the credential is built from, whose encoded width is the point.</param>
    /// <remarks>
    /// MIGRATION: net-new, and necessarily so - the legacy application had no such bound because it stored
    /// credentials reversibly rather than hashing them. Hashing is deliberately expensive, so an
    /// unauthenticated caller must not be able to hand it unbounded input.
    /// </remarks>
    [Theory]
    [InlineData(257, 'a')]
    [InlineData(100, '\u4e2d')]
    public async Task Password_ExceedingTheEncodedSizeCeiling_IsRefused(int length, char fill)
    {
        string password = new(fill, length);
        CreateUserRequest request = ValidRequest();
        request.Password = password;
        request.ConfirmPassword = password;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(
            result,
            nameof(CreateUserRequest.Password),
            CredentialBounds.MaximumByteLengthMessage);
    }

    /// <summary>
    /// The credential message carries the policy's own numbers, substituted into the legacy template rather
    /// than written into the code.
    /// </summary>
    [Fact]
    public async Task Password_Message_SubstitutesTheLegacyTokensFromThePolicy()
    {
        CreateUserRequest request = ValidRequest();
        request.Password = "abc12";
        request.ConfirmPassword = "abc12";

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Password), InvalidPasswordFor(_policy));
        result.Errors.Should().NotContain(
            failure => failure.ErrorMessage.Contains(PasswordLengthToken, StringComparison.Ordinal),
            "an unsubstituted token would reach the caller as literal text");
        result.Errors.Should().NotContain(
            failure => failure.ErrorMessage.Contains(NoneAlphabetToken, StringComparison.Ordinal),
            "an unsubstituted token would reach the caller as literal text");
    }

    /// <summary>
    /// Both the rule and its message move with the bound policy, proving the validator reads the policy
    /// rather than embedding the shipped numbers.
    /// </summary>
    /// <remarks>
    /// Raising the minimum is <em>not</em> a recommendation - it is an instrument. Hardening the shipped
    /// policy would lock out members whose credentials the legacy installation accepted, which is why every
    /// other test here binds the measured legacy figure and why the shipped value is asserted separately as
    /// the legacy one.
    /// </remarks>
    [Fact]
    public async Task Password_RuleAndMessage_FollowTheBoundPolicy()
    {
        PasswordPolicyOptions raised = ShippedPolicy();
        raised.MinRequiredPasswordLength = 10;
        CreateUserRequestValidator validator = new(raised);

        CreateUserRequest request = ValidRequest();
        request.Password = "abc1234";
        request.ConfirmPassword = "abc1234";

        ValidationResult underRaisedPolicy = await validator.ValidateAsync(request);
        ValidationResult underShippedPolicy = await _validator.ValidateAsync(request);

        ShouldReport(
            underRaisedPolicy,
            nameof(CreateUserRequest.Password),
            InvalidPasswordFor(raised));
        InvalidPasswordFor(raised).Should().NotBe(
            InvalidPasswordFor(_policy),
            "the two policies must render different sentences for this test to prove anything");
        ShouldAccept(underShippedPolicy);
    }

    // MIGRATION: DOCUMENTED DIVERGENCE - the target ACCUMULATES failures where the legacy code OVERWROTE
    // them. UserController.vb:L1086 reads isValid = rx.IsMatch(password) a plain assignment rather than
    // `isValid = isValid AndAlso rx.IsMatch(...)`.

    /// <summary>
    /// With no strength pattern configured - the shipped state - no strength rule is applied at all.
    /// </summary>
    /// <remarks>
    /// This test locks the guard in place. An empty pattern matches everything, so applying it would be a
    /// silent no-op today, but registering a rule that is unconditionally satisfied invites someone to
    /// "simplify" the guard away, and the next configuration that sets a pattern would then behave
    /// differently from the one that did not.
    /// </remarks>
    [Fact]
    public async Task Password_WithNoStrengthPatternConfigured_HasNoStrengthRuleApplied()
    {
        _policy.PasswordStrengthRegularExpression.Should().BeEmpty(
            "the shipped configuration sets no pattern in either config file");

        CreateUserRequest request = ValidRequest();
        request.Password = "abc1234";
        request.ConfirmPassword = "abc1234";

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateUserRequest.Password));
    }

    /// <summary>
    /// With a strength pattern configured, it is applied - and it is applied <em>in addition to</em> the
    /// minimum length rather than in place of it.
    /// </summary>
    /// <remarks>
    /// The companion to the test above: together they prove the guard both skips and fires. The pattern
    /// used demands letters only, so the shipped-policy credential of letters and digits is refused under
    /// it while a credential of seven letters is accepted, and the same credential that the pattern refuses
    /// is still accepted under the shipped policy that configures no pattern at all.
    /// </remarks>
    [Fact]
    public async Task Password_WithAStrengthPatternConfigured_AppliesItAlongsideTheMinimumLength()
    {
        PasswordPolicyOptions patterned = ShippedPolicy();
        patterned.PasswordStrengthRegularExpression = "^[A-Za-z]+$";
        CreateUserRequestValidator validator = new(patterned);

        CreateUserRequest refused = ValidRequest();
        refused.Password = "abc1234";
        refused.ConfirmPassword = "abc1234";

        ShouldReport(
            await validator.ValidateAsync(refused),
            nameof(CreateUserRequest.Password),
            InvalidPasswordFor(patterned));
        ShouldAccept(await _validator.ValidateAsync(refused));

        CreateUserRequest accepted = ValidRequest();
        accepted.Password = "abcdefg";
        accepted.ConfirmPassword = "abcdefg";

        ShouldAccept(await validator.ValidateAsync(accepted));
    }

    /// <summary>
    /// A credential that both falls short of the minimum and fails the configured pattern is refused by
    /// each rule independently, and both refusals reach the caller.
    /// </summary>
    /// <remarks>
    /// The COUNT is the assertion that matters here, and it is why this test is not merely a third wording
    /// check. Both rules emit the same sentence, because the legacy resource entry is one sentence covering
    /// every credential failure, so a test satisfied by "some failure carries this message" would pass with
    /// either rule deleted — the surviving one would supply the message on its own.
    /// </remarks>
    [Fact]
    public async Task Password_FailingBothLengthAndPattern_ReportsBothRefusals()
    {
        PasswordPolicyOptions patterned = ShippedPolicy();
        patterned.PasswordStrengthRegularExpression = "^[A-Za-z]+$";
        CreateUserRequestValidator validator = new(patterned);

        CreateUserRequest request = ValidRequest();
        request.Password = "ab12";
        request.ConfirmPassword = "ab12";

        ValidationResult result = await validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Password), InvalidPasswordFor(patterned));

        result.Errors
            .Where(failure => failure.PropertyName == nameof(CreateUserRequest.Password))
            .Should().HaveCount(
                2,
                "the credential is four characters against a minimum of seven AND carries digits against "
                + "a letters-only pattern, so both rules must fire; with only one failure this input "
                + "could not tell a working pair of rules from a single surviving one, because the two "
                + "share the legacy wording. The result was {0}",
                Render(result));

        // Each rule alone produces exactly one failure, which is what makes two above an accumulation
        // rather than a coincidence of this particular value.
        CreateUserRequest tooShortOnly = ValidRequest();
        tooShortOnly.Password = "abcdef";
        tooShortOnly.ConfirmPassword = "abcdef";

        ValidationResult lengthOnly = await validator.ValidateAsync(tooShortOnly);
        lengthOnly.Errors
            .Where(failure => failure.PropertyName == nameof(CreateUserRequest.Password))
            .Should().HaveCount(1, "six letters satisfy the pattern and fail only the minimum length");

        CreateUserRequest patternOnly = ValidRequest();
        patternOnly.Password = "abc1234";
        patternOnly.ConfirmPassword = "abc1234";

        ValidationResult patternFailure = await validator.ValidateAsync(patternOnly);
        patternFailure.Errors
            .Where(failure => failure.PropertyName == nameof(CreateUserRequest.Password))
            .Should().HaveCount(1, "seven characters satisfy the minimum and fail only the pattern");
    }

    // ------------------------------------------------------------------------
    // The confirmation. The request DOES carry a confirmation member, so the
    // equality rule exists and is exercised here.
    // ------------------------------------------------------------------------

    /// <summary>The confirmation must equal the credential exactly, and the comparison is case-sensitive.</summary>
    /// <param name="password">The submitted credential.</param>
    /// <param name="confirmation">The submitted confirmation.</param>
    [Theory]
    [InlineData("abc1234", "")]
    [InlineData("abc1234", "abc1235")]
    [InlineData("abc1234", "abc123")]
    [InlineData("Secret1x", "secret1x")]
    [InlineData("abc1234", " abc1234")]
    public async Task ConfirmPassword_WhenItDiffers_ReportsTheLegacyMismatchWording(
        string password,
        string confirmation)
    {
        CreateUserRequest request = ValidRequest();
        request.Password = password;
        request.ConfirmPassword = confirmation;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.ConfirmPassword), PasswordMismatch);
    }

    /// <summary>A matching confirmation reports nothing.</summary>
    /// <param name="password">The credential, supplied identically as the confirmation.</param>
    [Theory]
    [InlineData("abc1234")]
    [InlineData("Integr8tion!Pass")]
    [InlineData("       x")]
    public async Task ConfirmPassword_WhenItMatches_IsAccepted(string password)
    {
        CreateUserRequest request = ValidRequest();
        request.Password = password;
        request.ConfirmPassword = password;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateUserRequest.ConfirmPassword));
    }

    /// <summary>
    /// When neither the credential nor the confirmation is supplied at all, the failure reported is the
    /// missing credential - not a spurious mismatch.
    /// </summary>
    /// <remarks>
    /// Two absent values are equal to one another, so the equality rule is satisfied and stays silent. That
    /// is the right outcome: a member who filled in neither box should be told the credential is required,
    /// not that two empty boxes disagree.
    /// </remarks>
    [Fact]
    public async Task ConfirmPassword_WhenNeitherIsSupplied_ReportsOnlyTheMissingCredential()
    {
        CreateUserRequest request = ValidRequest();
        request.Password = null!;
        request.ConfirmPassword = null!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Password), InvalidPasswordFor(_policy));
        ShouldNotReport(result, nameof(CreateUserRequest.ConfirmPassword));
    }

    // Anti-hardening. Each test below asserts the ABSENCE of a rule, which is the only way a loose policy
    // can be defended: a rule nobody wrote is indistinguishable from a rule nobody tested until someone
    // adds it.

    /// <summary>
    /// The validator does not enforce a unique address, so two requests carrying the same address are both
    /// accepted.
    /// </summary>
    /// <remarks>
    /// <c>Website/release.config:L244</c> declares <c>requiresUniqueEmail="false"</c>. Duplication is a
    /// service-level outcome discovered when the write is attempted - the legacy code reported it through a
    /// distinct creation-status value with its own wording at <c>SharedResources.resx</c> L276 - and it is
    /// emphatically not a rule a validator can decide, because a validator cannot see the store.
    /// </remarks>
    [Fact]
    public async Task Email_IsNotRequiredToBeUnique()
    {
        CreateUserRequest first = ValidRequest();
        CreateUserRequest second = ValidRequest();
        second.Username = "another_member";
        second.Email.Should().Be(first.Email, "both requests must carry the same address");

        ShouldAccept(await _validator.ValidateAsync(first));
        ShouldAccept(await _validator.ValidateAsync(second));
    }

    /// <summary>The validator does not enforce a unique login name either, for the same reason.</summary>
    [Fact]
    public async Task Username_IsNotRequiredToBeUnique()
    {
        CreateUserRequest first = ValidRequest();
        CreateUserRequest second = ValidRequest();
        second.Email = "other@example.com";
        second.Username.Should().Be(first.Username, "both requests must carry the same login name");

        ShouldAccept(await _validator.ValidateAsync(first));
        ShouldAccept(await _validator.ValidateAsync(second));
    }

    /// <summary>
    /// A request that supplies no recovery pair - which is every request, because the contract has no
    /// member for one - is accepted.
    /// </summary>
    [Fact]
    public async Task ARequestWithNoRecoveryPair_IsAccepted()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        ShouldAccept(result);
    }

    /// <summary>The authorisation flag carries no rule in either direction.</summary>
    /// <param name="authorize">Whether the new account is pre-authorised.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Authorize_IsAcceptedEitherWay(bool authorize)
    {
        CreateUserRequest request = ValidRequest();
        request.Authorize = authorize;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    // ------------------------------------------------------------------------
    // Accumulation across fields.
    // ------------------------------------------------------------------------

    /// <summary>Every broken rule is reported at once, each against its own field.</summary>
    [Fact]
    public async Task EveryBrokenRule_IsReportedTogetherAgainstItsOwnField()
    {
        CreateUserRequest request = new()
        {
            Username = string.Empty,
            FirstName = string.Empty,
            LastName = string.Empty,
            DisplayName = new string('a', 129),
            Email = "not-an-address",
            Password = "ab",
            ConfirmPassword = "cd",
            Authorize = false,
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateUserRequest.Username), UsernameRequired);
        ShouldReport(result, nameof(CreateUserRequest.FirstName), FirstNameRequired);
        ShouldReport(result, nameof(CreateUserRequest.LastName), LastNameRequired);
        ShouldReport(result, nameof(CreateUserRequest.DisplayName), InvalidDisplayName);
        ShouldReport(result, nameof(CreateUserRequest.Email), EmailFormat);
        ShouldReport(result, nameof(CreateUserRequest.Password), InvalidPasswordFor(_policy));
        ShouldReport(result, nameof(CreateUserRequest.ConfirmPassword), PasswordMismatch);

        result.Errors.Select(failure => failure.PropertyName).Distinct().Should().HaveCount(
            7,
            "each field must report under its own key, because that key is what a form binds to");
    }
}
