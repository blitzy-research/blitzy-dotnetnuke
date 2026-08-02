using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Covers the tenant-creation validator, whose rules and messages are lifted verbatim from the legacy
/// portal sign-up screen rather than rewritten.
/// </summary>
/// <remarks>
/// <para>
/// Every message asserted here is quoted exactly, capital letters and trailing full stops included,
/// because functional parity means an operator who knew the old screen recognises the new one. The
/// wording is occasionally wrong on its own terms - the alias field reports "Portal Name Is Required."
/// - and that is preserved deliberately, not corrected.
/// </para>
/// <para>
/// Two asymmetries are pinned below because they look like defects until you check the legacy source.
/// The tenant name is <em>not</em> required on this path, only length-capped; and the administrator
/// e-mail address is <em>not</em> pattern-checked on this path even though the account-creation
/// validator checks it strictly. Both match what the legacy sign-up screen actually enforced.
/// </para>
/// </remarks>
public class CreatePortalRequestValidatorTests
{
    private const string AliasRequired = "Portal Name Is Required.";

    private const string AliasCharacters = "The Portal Name Must Not Contain Spaces Or Punctuation.";

    private const string TemplateRequired = "Please select a template file";

    private const string TemplateBareName = "Template must be a file name and must not contain a directory path.";

    private const string FirstNameRequired = "First Name Is Required.";

    private const string LastNameRequired = "Last Name Is Required.";

    private const string UsernameRequired = "Username Is Required.";

    private const string PasswordRequired = "Password Is Required.";

    private const string EmailRequired = "Email Is Required.";

    private readonly CreatePortalRequestValidator _validator = new(new PasswordPolicyOptions());

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
    /// The alias is required, and it reports the legacy wording rather than its own field name.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Alias_IsRequired(string? alias)
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = alias;

        Messages(request).Should().Contain(AliasRequired);
    }

    /// <summary>
    /// An alias a parent tenant may legitimately use is accepted, normalisation included.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("site.example.com")]
    [InlineData("localhost:8080")]
    [InlineData("http://localhost/dnn")]
    [InlineData("my-site.example.com/tenant")]
    public void ParentAlias_AcceptsHostsPathsAndPorts(string alias)
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = alias;
        request.IsChildPortal = false;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>
    /// An alias containing anything the legacy screen refused is refused.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData("my site")]
    [InlineData("my,site")]
    [InlineData("my_site")]
    [InlineData("site?query=1")]
    [InlineData("site#fragment")]
    [InlineData("site@example.com")]
    public void ParentAlias_RefusesSpacesAndPunctuation(string alias)
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = alias;
        request.IsChildPortal = false;

        Messages(request).Should().Contain(AliasCharacters);
    }

    /// <summary>
    /// A child alias is measured after the last separator and admits a narrower character set.
    /// </summary>
    [Fact]
    public void ChildAlias_IsMeasuredAfterTheLastSeparatorAndIsNarrower()
    {
        CreatePortalRequest childOfHost = Valid();
        childOfHost.IsChildPortal = true;
        childOfHost.PortalAlias = "site.example.com/tenant-one";

        _validator.Validate(childOfHost).IsValid.Should().BeTrue(
            "only the segment after the last separator is measured for a child tenant, so the dots in "
            + "the host part are never examined");

        CreatePortalRequest childWithUnderscore = Valid();
        childWithUnderscore.IsChildPortal = true;
        childWithUnderscore.PortalAlias = "site.example.com/tenant_one";

        Messages(childWithUnderscore).Should().Contain(AliasCharacters);

        CreatePortalRequest dottedChild = Valid();
        dottedChild.IsChildPortal = true;
        dottedChild.PortalAlias = "site.example.com";

        Messages(dottedChild).Should().Contain(
            AliasCharacters,
            "with no separator the whole value is the measured segment, and a dot is not permitted in a "
            + "child alias even though it is permitted in a parent one");
    }

    /// <summary>
    /// The alias is bounded, and the first failure on a property stops the rest of that property's rules.
    /// </summary>
    [Fact]
    public void Alias_IsBoundedAndReportsOnlyItsFirstFailure()
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = new string('a', 128);

        _validator.Validate(request).IsValid.Should().BeTrue("one hundred and twenty-eight is the column width");

        request.PortalAlias = new string('a', 129);

        IReadOnlyList<ValidationFailure> failures = Failures(request, nameof(CreatePortalRequest.PortalAlias));

        failures.Should().HaveCount(1);

        request.PortalAlias = new string('_', 129);

        failures = Failures(request, nameof(CreatePortalRequest.PortalAlias));

        failures.Should().ContainSingle(
            "the validator stops at the first failure on a property, so an alias that is both too long "
            + "and badly formed reports the length once rather than reporting both problems");
        failures[0].ErrorMessage.Should().NotBe(AliasCharacters);
    }

    /// <summary>
    /// A template must be chosen on every creation.
    /// </summary>
    /// <param name="templateFile">The submitted template.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Template_IsRequired(string? templateFile)
    {
        CreatePortalRequest request = Valid();
        request.TemplateFile = templateFile;

        Messages(request).Should().Contain(TemplateRequired);
    }

    /// <summary>
    /// A template must name a file and must not reach outside the template directory.
    /// </summary>
    /// <param name="templateFile">The submitted template.</param>
    [Theory]
    [InlineData("Portals/_default/admin.template")]
    [InlineData("Portals\\_default\\admin.template")]
    [InlineData("C:\\templates\\admin.template")]
    [InlineData("../admin.template")]
    [InlineData("..template")]
    public void Template_MustBeABareFileName(string templateFile)
    {
        CreatePortalRequest request = Valid();
        request.TemplateFile = templateFile;

        Messages(request).Should().Contain(TemplateBareName);
    }

    /// <summary>
    /// A dotted file name is a legitimate template name; only separators and traversal are refused.
    /// </summary>
    /// <param name="templateFile">The submitted template.</param>
    [Theory]
    [InlineData("admin.template")]
    [InlineData("default.template")]
    [InlineData("my-site.v2.template")]
    public void Template_AcceptsADottedFileName(string templateFile)
    {
        CreatePortalRequest request = Valid();
        request.TemplateFile = templateFile;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>
    /// Every administrator field the legacy screen demanded is still demanded, with its own wording.
    /// </summary>
    [Fact]
    public void AdministratorFields_AreRequiredWithTheLegacyWording()
    {
        CreatePortalRequest request = Valid();
        request.AdministratorFirstName = null;
        request.AdministratorLastName = string.Empty;
        request.AdministratorUsername = "   ";
        request.AdministratorPassword = null;
        request.AdministratorEmail = null;

        IReadOnlyList<string> messages = Messages(request);

        messages.Should().Contain(FirstNameRequired);
        messages.Should().Contain(LastNameRequired);
        messages.Should().Contain(UsernameRequired);
        messages.Should().Contain(PasswordRequired);
        messages.Should().Contain(EmailRequired);
        messages.Should().HaveCount(
            5,
            "the validator continues across properties, so one bad request reports every bad field "
            + "rather than only the first");
    }

    /// <summary>
    /// The administrator name fields are bounded by the columns that store them.
    /// </summary>
    [Fact]
    public void AdministratorNames_AreBoundedByTheirColumns()
    {
        CreatePortalRequest atTheLimit = Valid();
        atTheLimit.AdministratorFirstName = new string('a', 50);
        atTheLimit.AdministratorLastName = new string('b', 50);
        atTheLimit.AdministratorUsername = new string('c', 100);
        atTheLimit.AdministratorEmail = new string('d', 244) + "@example.com";

        // MIGRATION: 256, the terminal width of dbo.Users.Email, not the 100 the sign-up control's
        // maxlength attribute enforced in the browser. That attribute guarded a column width the schema
        // no longer has - 02.02.01 dropped the 100-character column and 03.00.13 re-added it at 256 - and
        // it was never a server-side rule, so a caller that bypassed the screen was never held to it.
        atTheLimit.AdministratorEmail.Should().HaveLength(256);

        ValidationResult accepted = _validator.Validate(atTheLimit);

        accepted.IsValid.Should().BeTrue(Describe(accepted));

        CreatePortalRequest overTheLimit = Valid();
        overTheLimit.AdministratorFirstName = new string('a', 51);
        overTheLimit.AdministratorLastName = new string('b', 51);
        overTheLimit.AdministratorUsername = new string('c', 101);
        overTheLimit.AdministratorEmail = new string('d', 245) + "@example.com";

        Failures(overTheLimit, nameof(CreatePortalRequest.AdministratorFirstName)).Should().ContainSingle();
        Failures(overTheLimit, nameof(CreatePortalRequest.AdministratorLastName)).Should().ContainSingle();
        Failures(overTheLimit, nameof(CreatePortalRequest.AdministratorUsername)).Should().ContainSingle();
        Failures(overTheLimit, nameof(CreatePortalRequest.AdministratorEmail)).Should().ContainSingle();
    }

    /// <summary>
    /// The administrator address is shape-checked on this path, against the one shared authority, and a
    /// modern top-level domain is accepted.
    /// </summary>
    /// <param name="email">The submitted address.</param>
    /// <param name="expectedToBeValid">Whether the address is expected to survive validation.</param>
    /// <remarks>
    /// <para>
    /// <b>This is a documented tightening, and the legacy measurement is recorded here so it is not
    /// mistaken for an oversight.</b> <c>Website/admin/Portal/signup.ascx:L106-L107</c> declares
    /// <c>txtEmail</c> with a <c>maxlength</c> attribute and a single required-field validator, and no
    /// expression validator at all - so a malformed address passed that screen and went on to become the
    /// new administrator's membership address. The account-creation screen, by contrast, did check the
    /// shape.
    /// </para>
    /// <para>
    /// The asymmetry is not reproduced, for two measured reasons. The address this path stores is a real
    /// account's contact address - and the sole administrator's, on a tenant that has no other - so a
    /// malformed one means no recovery message can ever reach the only account able to administer the
    /// site. And the same field submitted to the account-creation endpoint is checked, so leaving this
    /// path unchecked would give one column two different notions of a valid address, which is the
    /// divergence the review findings on email width and match semantics required be removed.
    /// </para>
    /// <para>
    /// The rule delegates to <c>Domain/ValueObjects/EmailAddress.cs</c> rather than restating a pattern,
    /// so this path inherits the same loosening every other path has: a final label of five or more
    /// letters is accepted, which the legacy pattern refused.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("not-an-address", false)]
    [InlineData("missing@domain", false)]
    [InlineData("curator@national.museum", true)]
    [InlineData("admin@example.com", true)]
    public void AdministratorEmail_IsShapeCheckedAgainstTheSharedAuthority(string email, bool expectedToBeValid)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorEmail = email;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().Be(expectedToBeValid, Describe(result));
    }

    /// <summary>
    /// The tenant name is bounded but not required.
    /// </summary>
    [Fact]
    public void PortalName_IsBoundedButNotRequired()
    {
        CreatePortalRequest absent = Valid();
        absent.PortalName = null;

        _validator.Validate(absent).IsValid.Should().BeTrue(
            "the legacy screen derived the stored name from the alias when none was typed, so an absent "
            + "name is not a validation failure here");

        CreatePortalRequest atTheLimit = Valid();
        atTheLimit.PortalName = new string('a', 128);

        _validator.Validate(atTheLimit).IsValid.Should().BeTrue();

        CreatePortalRequest overTheLimit = Valid();
        overTheLimit.PortalName = new string('a', 129);

        Failures(overTheLimit, nameof(CreatePortalRequest.PortalName)).Should().ContainSingle();
    }

    /// <summary>
    /// The optional descriptive fields are bounded by their columns and are otherwise unconstrained.
    /// </summary>
    [Fact]
    public void DescriptiveFields_AreBoundedByTheirColumns()
    {
        CreatePortalRequest atTheLimit = Valid();
        atTheLimit.Description = new string('a', 500);
        atTheLimit.KeyWords = new string('b', 500);
        atTheLimit.HomeDirectory = new string('c', 100);

        _validator.Validate(atTheLimit).IsValid.Should().BeTrue();

        CreatePortalRequest overTheLimit = Valid();
        overTheLimit.Description = new string('a', 501);
        overTheLimit.KeyWords = new string('b', 501);
        overTheLimit.HomeDirectory = new string('c', 101);

        Failures(overTheLimit, nameof(CreatePortalRequest.Description)).Should().ContainSingle();
        Failures(overTheLimit, nameof(CreatePortalRequest.KeyWords)).Should().ContainSingle();
        Failures(overTheLimit, nameof(CreatePortalRequest.HomeDirectory)).Should().ContainSingle();
    }

    /// <summary>
    /// Builds a request that satisfies every rule.
    /// </summary>
    /// <returns>The request.</returns>
    private static CreatePortalRequest Valid() => new()
    {
        PortalName = "Integration Portal",
        PortalAlias = "localhost",
        TemplateFile = "admin.template",
        IsChildPortal = false,
        AdministratorFirstName = "Integration",
        AdministratorLastName = "Administrator",
        AdministratorUsername = "integration_admin",
        AdministratorPassword = "Integr8tion!Pass",
        AdministratorEmail = "admin@example.com",
    };

    /// <summary>
    /// Validates a request and returns the messages it reported.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <returns>The reported messages.</returns>
    private IReadOnlyList<string> Messages(CreatePortalRequest request)
        => _validator.Validate(request).Errors.Select(failure => failure.ErrorMessage).ToList();

    /// <summary>
    /// Validates a request and returns the failures reported against one property.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="propertyName">The property to filter on.</param>
    /// <returns>The reported failures.</returns>
    private IReadOnlyList<ValidationFailure> Failures(CreatePortalRequest request, string propertyName)
        => _validator.Validate(request).Errors
            .Where(failure => string.Equals(failure.PropertyName, propertyName, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Renders a validation result for an assertion message.
    /// </summary>
    /// <param name="result">The result to render.</param>
    /// <returns>The rendered reason.</returns>
    private static string Describe(ValidationResult result)
        => "the request should have been accepted but reported: "
            + string.Join(" | ", result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));
}
