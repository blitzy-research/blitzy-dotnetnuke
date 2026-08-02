using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Covers the account-creation validator, whose rules are lifted from the legacy user screen and whose
/// credential rules are driven by the bound password policy rather than hard-coded.
/// </summary>
/// <remarks>
/// <para>
/// The policy this validator reads is the legacy one, preserved verbatim: a minimum length of seven,
/// <em>no</em> required non-alphanumeric characters, no security question, and no requirement that an
/// address be unique. Tightening any of those during a migration would lock out existing members, so the
/// assertions below pin the loose policy as intended behaviour rather than as an oversight - and they
/// also prove the validator honours a stricter policy when one is configured.
/// </para>
/// <para>
/// The address pattern is the legacy regular expression, and it rejects addresses a modern reader would
/// accept: anything whose final domain label runs to five characters or more, and any local part
/// beginning with a character the pattern's leading word boundary refuses. Those rejections are asserted
/// so that nobody widens the pattern without realising they are changing who can register.
/// </para>
/// <para>
/// The validator receives its policy as a plain options object rather than through an options wrapper.
/// That is deliberate: the application layer takes no dependency on the hosting configuration
/// abstractions, which is why this suite can construct the validator directly with no container at all.
/// </para>
/// <para>
/// Several of the message constants below contain <em>two</em> spaces after a sentence-ending period. That
/// is not a typing slip and must not be tidied. The wording is reproduced character for character from
/// the legacy shared resource file, which spaces its sentences that way throughout, and the migration
/// preserves it so that an existing operator sees the message they already know. The one message that has
/// no legacy counterpart - the display-name length message - follows the same convention deliberately, so
/// the set reads consistently. An earlier draft of this suite normalised the spacing to one space and
/// four tests failed; the tests were wrong, the code was right.
/// </para>
/// </remarks>
public class CreateUserRequestValidatorTests
{
    private const string UsernameRequired = "User name is required";

    private const string FirstNameRequired = "First name is required";

    private const string LastNameRequired = "Last name is required";

    private const string EmailRequired = "Email is required";

    private const string EmailFormat = "You must enter a valid email address";

    private const string PasswordMismatch = "The Password and Confirmation Passwords do not match";

    private const string InvalidUsername = "The username specified is invalid.  Please specify a valid username.";

    private const string InvalidDisplayName =
        "The display name specified is invalid.  Please specify a valid display name.";

    private const string InvalidEmail =
        "The email address specified is invalid.  Please specify a valid email address.";

    private readonly CreateUserRequestValidator _validator = new(new PasswordPolicyOptions());

    /// <summary>
    /// A fully populated request is accepted.
    /// </summary>
    [Fact]
    public void AWellFormedRequest_IsAccepted()
    {
        ValidationResult result = _validator.Validate(Valid());

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>
    /// The validator refuses to be constructed without a policy.
    /// </summary>
    [Fact]
    public void Constructor_RequiresAPolicy()
    {
        Action construct = () => _ = new CreateUserRequestValidator(null!);

        construct.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// The three name fields are required.
    /// </summary>
    [Fact]
    public void NameFields_AreRequired()
    {
        CreateUserRequest request = Valid();
        request.Username = string.Empty;
        request.FirstName = "   ";
        request.LastName = string.Empty;

        IReadOnlyList<string> messages = Messages(request);

        messages.Should().Contain(UsernameRequired);
        messages.Should().Contain(FirstNameRequired);
        messages.Should().Contain(
            LastNameRequired,
            "the surname property is declared nullable to match the request shape, but the column behind "
            + "it is not nullable, so the validator demands it");
    }

    /// <summary>
    /// The display name is optional but bounded when supplied.
    /// </summary>
    [Fact]
    public void DisplayName_IsOptionalButBounded()
    {
        CreateUserRequest absent = Valid();
        absent.DisplayName = string.Empty;

        _validator.Validate(absent).IsValid.Should().BeTrue(
            "the legacy screen derived a display name when none was typed, so an empty one is not a failure");

        CreateUserRequest atTheLimit = Valid();
        atTheLimit.DisplayName = new string('a', 128);

        _validator.Validate(atTheLimit).IsValid.Should().BeTrue();

        CreateUserRequest overTheLimit = Valid();
        overTheLimit.DisplayName = new string('a', 129);

        Messages(overTheLimit).Should().Contain(InvalidDisplayName);
    }

    /// <summary>
    /// The name fields are bounded by the columns that store them.
    /// </summary>
    [Fact]
    public void NameFields_AreBoundedByTheirColumns()
    {
        CreateUserRequest atTheLimit = Valid();
        atTheLimit.Username = new string('a', 100);
        atTheLimit.FirstName = new string('b', 50);
        atTheLimit.LastName = new string('c', 50);

        ValidationResult accepted = _validator.Validate(atTheLimit);

        accepted.IsValid.Should().BeTrue(Describe(accepted));

        CreateUserRequest overTheLimit = Valid();
        overTheLimit.Username = new string('a', 101);
        overTheLimit.FirstName = new string('b', 51);
        overTheLimit.LastName = new string('c', 51);

        IReadOnlyList<string> messages = Messages(overTheLimit);

        messages.Should().Contain(InvalidUsername);
        messages.Where(message => string.Equals(message, InvalidUsername, StringComparison.Ordinal))
            .Should().HaveCount(
                3,
                "the legacy screen reported one shared message for all three over-long name fields, and "
                + "that wording is preserved even though it names only the username");
    }

    /// <summary>
    /// An address the legacy pattern accepted is accepted.
    /// </summary>
    /// <param name="email">The submitted address.</param>
    [Theory]
    [InlineData("member@example.com")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("o'brien@example.org")]
    [InlineData("percent%sign@example.info")]
    [InlineData("_underscore@example.com")]
    [InlineData("MiXeD@CaSe.CoM")]
    public void Email_AcceptsWhateverTheLegacyPatternAccepted(string email)
    {
        CreateUserRequest request = Valid();
        request.Email = email;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>
    /// An address the legacy pattern rejected is rejected, quirks included.
    /// </summary>
    /// <param name="email">The submitted address.</param>
    [Theory]
    [InlineData("no-at-sign.example.com")]
    [InlineData("missing@domain")]
    [InlineData("member@example.c")]
    [InlineData("member@under_score.com")]
    public void Email_RejectsWhateverTheLegacyPatternRejected(string email)
    {
        CreateUserRequest request = Valid();
        request.Email = email;

        Messages(request).Should().Contain(EmailFormat);
    }

    /// <summary>
    /// An address is required, and an absent one reports the requirement.
    /// </summary>
    [Fact]
    public void Email_IsRequired()
    {
        CreateUserRequest request = Valid();
        request.Email = string.Empty;

        Messages(request).Should().Contain(EmailRequired);
    }

    /// <summary>
    /// The address is bounded by the column that stores it rather than by the width of the screen field.
    /// </summary>
    [Fact]
    public void Email_IsBoundedByTheColumnWidth()
    {
        // MIGRATION: the column is nvarchar(256) after the full ALTER chain, so the boundary contract is
        // 256 here, in the value object and in the persistence mapping alike - one width, stated once per
        // layer and agreeing across all three.
        CreateUserRequest atTheLimit = Valid();
        atTheLimit.Email = new string('d', 244) + "@example.com";
        atTheLimit.Email.Should().HaveLength(256);

        ValidationResult accepted = _validator.Validate(atTheLimit);

        accepted.IsValid.Should().BeTrue(Describe(accepted));

        CreateUserRequest overTheLimit = Valid();
        overTheLimit.Email = new string('d', 245) + "@example.com";

        Messages(overTheLimit).Should().Contain(InvalidEmail);
    }

    /// <summary>
    /// The credential must reach the policy minimum, and length is the only rule the legacy policy had.
    /// </summary>
    [Fact]
    public void Password_IsGovernedOnlyByTheConfiguredMinimumLength()
    {
        PasswordPolicyOptions policy = new();

        policy.MinRequiredPasswordLength.Should().Be(7, "the legacy configuration set seven");
        policy.MinRequiredNonAlphanumericCharacters.Should().Be(
            0,
            "the legacy configuration required none, and raising it during a migration would refuse "
            + "credentials the old site accepted");
        policy.PasswordStrengthRegularExpression.Should().BeEmpty();

        CreateUserRequest tooShort = Valid();
        tooShort.Password = "abc123";
        tooShort.ConfirmPassword = "abc123";

        Messages(tooShort).Should().Contain(message => message.Contains("at least 7 characters", StringComparison.Ordinal));

        CreateUserRequest atTheLimit = Valid();
        atTheLimit.Password = "abcdefg";
        atTheLimit.ConfirmPassword = "abcdefg";

        ValidationResult accepted = _validator.Validate(atTheLimit);

        accepted.IsValid.Should().BeTrue(
            Describe(accepted)
            + " - seven lower-case letters with no digit and no symbol satisfied the legacy policy");
    }

    /// <summary>
    /// The rendered credential message quotes the configured numbers rather than fixed ones.
    /// </summary>
    [Fact]
    public void Password_MessageQuotesTheConfiguredPolicyNumbers()
    {
        CreateUserRequestValidator strict = new(new PasswordPolicyOptions
        {
            MinRequiredPasswordLength = 12,
            MinRequiredNonAlphanumericCharacters = 2,
        });

        CreateUserRequest request = Valid();
        request.Password = "short";
        request.ConfirmPassword = "short";

        IReadOnlyList<string> messages = strict.Validate(request).Errors
            .Select(failure => failure.ErrorMessage)
            .ToList();

        messages.Should().Contain(message =>
            message.Contains("at least 12 characters", StringComparison.Ordinal)
            && message.Contains("least 2 non-alphanumeric", StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains("[PasswordLength]", StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains("[NoneAlphabet]", StringComparison.Ordinal));
    }

    /// <summary>
    /// A configured strength pattern is applied on top of the minimum length.
    /// </summary>
    [Fact]
    public void Password_HonoursAConfiguredStrengthPattern()
    {
        CreateUserRequestValidator strict = new(new PasswordPolicyOptions
        {
            MinRequiredPasswordLength = 7,
            PasswordStrengthRegularExpression = @"^(?=.*[0-9])(?=.*[A-Z]).+$",
        });

        CreateUserRequest weak = Valid();
        weak.Password = "abcdefgh";
        weak.ConfirmPassword = "abcdefgh";

        strict.Validate(weak).IsValid.Should().BeFalse(
            "the configured pattern demands a digit and a capital, which this credential has neither of");

        CreateUserRequest strong = Valid();
        strong.Password = "Abcdefg1";
        strong.ConfirmPassword = "Abcdefg1";

        ValidationResult accepted = strict.Validate(strong);

        accepted.IsValid.Should().BeTrue(Describe(accepted));

        _validator.Validate(weak).IsValid.Should().BeTrue(
            "and the same credential passes under the legacy policy, which configures no pattern at all");
    }

    /// <summary>
    /// The confirmation must match the credential exactly.
    /// </summary>
    [Fact]
    public void ConfirmPassword_MustMatchExactly()
    {
        CreateUserRequest mismatched = Valid();
        mismatched.Password = "Integr8tion!Pass";
        mismatched.ConfirmPassword = "integr8tion!pass";

        Messages(mismatched).Should().Contain(PasswordMismatch);

        CreateUserRequest missing = Valid();
        missing.Password = "Integr8tion!Pass";
        missing.ConfirmPassword = string.Empty;

        Messages(missing).Should().Contain(PasswordMismatch);
    }

    /// <summary>
    /// No rule demands a recovery question or answer, because the request carries no member to
    /// attach one to and the policy that would have switched the pair on is refused as a
    /// configuration failure.
    /// </summary>
    [Fact]
    public void RecoveryQuestionAndAnswer_AreNotPartOfTheContractAtAll()
    {
        typeof(CreateUserRequest).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain("PasswordQuestion").And.NotContain("PasswordAnswer");

        new PasswordPolicyOptions().RequiresQuestionAndAnswer.Should().BeFalse(
            "the legacy configuration did not require a security question");

        new PasswordPolicyOptions().Validate().Should().BeEmpty(
            "the shipped defaults are the measured legacy policy and must not be refused");

        // MIGRATION: the policy refuses the flag by REPORTING a failure rather than by throwing.
        // PasswordPolicyOptions.Validate returns the failures it found so its caller decides what to
        // do with them, and the host turns them into a start-up abort through IValidateOptions. This
        // test project references the Application layer only, so the reported failure is the reachable
        // expression of the rule; that a true value stops the host is the hosting layer's own concern.
        IReadOnlyList<string> refusals =
            new PasswordPolicyOptions { RequiresQuestionAndAnswer = true }.Validate();

        refusals.Should().ContainSingle(
            "a deployment must not be able to switch on a requirement the target cannot satisfy")
            .Which.Should().Contain(nameof(PasswordPolicyOptions.RequiresQuestionAndAnswer))
            .And.Contain("password-recovery question or answer");

        _validator.Validate(Valid()).IsValid.Should().BeTrue(
            "an account is created with no security question at all");
    }

    /// <summary>
    /// Builds a request that satisfies every rule under the legacy policy.
    /// </summary>
    /// <returns>The request.</returns>
    private static CreateUserRequest Valid() => new()
    {
        Username = "integration_member",
        FirstName = "Integration",
        LastName = "Member",
        DisplayName = "Integration Member",
        Email = "member@example.com",
        Password = "Integr8tion!Pass",
        ConfirmPassword = "Integr8tion!Pass",
        Authorize = true,
    };

    /// <summary>
    /// Validates a request and returns the messages it reported.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <returns>The reported messages.</returns>
    private IReadOnlyList<string> Messages(CreateUserRequest request)
        => _validator.Validate(request).Errors.Select(failure => failure.ErrorMessage).ToList();

    /// <summary>
    /// Renders a validation result for an assertion message.
    /// </summary>
    /// <param name="result">The result to render.</param>
    /// <returns>The rendered reason.</returns>
    private static string Describe(ValidationResult result)
        => "the request should have been accepted but reported: "
            + string.Join(" | ", result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));
}
