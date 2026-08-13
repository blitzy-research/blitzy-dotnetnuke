using System.Globalization;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule and word-for-word parity between <see cref="CreatePortalRequestValidator"/> and the
/// validator declarations measured on the legacy portal signup screen, together with the two comparison
/// validators on the legacy site-settings screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wording that looks wrong is preserved on purpose.</b> The alias field reports "Portal Name Is
/// Required." because <c>valPortalName</c> declares <c>controltovalidate="txtPortalName"</c> at
/// <c>signup.ascx:L41</c> while <c>L39</c> labels that same box "Portal Alias:".
/// </para>
/// <para>
/// <b>Three asymmetries are pinned here because each looks like a defect until the legacy source is
/// checked.</b> The portal title is length-capped but <em>not</em> required, because the screen declared no
/// validator on it. No lower bound is enforced on any fee or quota, because the only comparison validator
/// over a fee is a type check with no companion.
/// </para>
/// </remarks>
public class CreatePortalRequestValidatorTests
{
    /// <summary>The <c>valPortalName</c> message, which guards the alias.</summary>
    private const string AliasRequired = "Portal Name Is Required.";

    /// <summary>
    /// The <c>InvalidName</c> message the signup code-behind raised for a disallowed alias character.
    /// </summary>
    private const string AliasCharacters = "The Portal Name Must Not Contain Spaces Or Punctuation.";

    /// <summary>
    /// The <c>valTemplate</c> message. It is the one message on the screen with no trailing full stop, and
    /// that is asserted rather than normalised.
    /// </summary>
    private const string TemplateRequired = "Please select a template file";

    /// <summary>
    /// Wording for the bare-file-name rule on the template selection, which has no legacy counterpart
    /// because a server-populated drop-down list could not submit a path.
    /// </summary>
    private const string TemplateBareName =
        "Template must be a file name and must not contain a directory path.";

    /// <summary>The <c>valFirstName</c> message.</summary>
    private const string FirstNameRequired = "First Name Is Required.";

    /// <summary>The <c>valLastName</c> message.</summary>
    private const string LastNameRequired = "Last Name Is Required.";

    /// <summary>The <c>valUsername</c> message.</summary>
    private const string UsernameRequired = "Username Is Required.";

    /// <summary>The <c>valPassword</c> message.</summary>
    private const string PasswordRequired = "Password Is Required.";

    /// <summary>The <c>valEmail</c> message.</summary>
    private const string EmailRequired = "Email Is Required.";

    /// <summary>
    /// The shared <c>InvalidEmail.Text</c> wording, including the two spaces after the sentence period that
    /// the legacy file uses.
    /// </summary>
    private const string EmailInvalid =
        "The email address specified is invalid.  Please specify a valid email address.";

    /// <summary>The screen's own <c>InvalidHomeFolder.Text</c> wording.</summary>
    private const string HomeFolderInvalid = "The Home Folder you specified is not valid.";

    /// <summary>
    /// The shared <c>InvalidPassword.Text</c> wording with its two bracketed tokens resolved against the
    /// shipped policy, exactly as the legacy membership path resolved them at run time.
    /// </summary>
    /// <remarks>
    /// The numbers are 7 and 0 because <c>Website/release.config</c> declares
    /// <c>minRequiredPasswordLength="7"</c> at L242 and <c>minRequiredNonalphanumericCharacters="0"</c> at
    /// L243, and the options type defaults to the same pair. A test further down proves neither bracketed
    /// token survives into a message.
    /// </remarks>
    private const string CredentialInvalidUnderShippedPolicy =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at "
        + "least 7 characters in length and contain at least 0 non-alphanumeric characters.";

    /// <summary>
    /// The same wording resolved against the stricter policy used by the tests that prove the thresholds
    /// are read from configuration rather than hard-coded.
    /// </summary>
    private const string CredentialInvalidUnderStricterPolicy =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at "
        + "least 10 characters in length and contain at least 2 non-alphanumeric characters.";

    // The three literals below are the candidate wordings for a confirmation-mismatch rule, and this
    // contract carries NO rule that can emit any of them.

    /// <summary>The <c>valConfirm</c> message, whose text box has no counterpart on the request contract.</summary>
    private const string ConfirmationRequired = "Password Confirmation Is Required.";

    /// <summary>
    /// <c>InvalidPassword.Text</c> from the screen's own resource file - the wording this flow actually
    /// rendered for a mismatch, raised at <c>Signup.ascx.vb:L221</c>.
    /// </summary>
    private const string MismatchScreenWording = "The Password Values Entered Do Not Match.";

    /// <summary>
    /// <c>PasswordMismatch.Text</c> from the shared resource file. The only one of the three with no
    /// trailing full stop.
    /// </summary>
    private const string MismatchSharedWording = "The Password and Confirmation Passwords do not match";

    /// <summary>
    /// <c>PasswordMismatch.Text1</c> from the shared resource file, the duplicate of the entry above under
    /// one logical name.
    /// </summary>
    private const string MismatchSharedAlternateWording = "Password Values Entered Do Not Match.";

    // The two literals below belong to the site-settings screen's DataTypeCheck comparison validators.

    /// <summary>
    /// The <c>valExpiryDate</c> message, <c>Operator="DataTypeCheck" Type="Date"</c>, quoted with the
    /// leading element it carries inline.
    /// </summary>
    private const string ExpiryDateTypeCheck = "<br>Invalid expiry date!";

    /// <summary>
    /// The <c>valHostFee</c> message, <c>Operator="DataTypeCheck" Type="Currency"</c>. It has no
    /// lower-bound companion, which is why no fee rule exists to reproduce.
    /// </summary>
    private const string HostFeeTypeCheck = "Invalid fee, needs to be a currency value!";

    /// <summary>The validator under test, constructed with the shipped legacy policy.</summary>
    private readonly CreatePortalRequestValidator _validator = new(ShippedPolicy());

    // ---------------------------------------------------------------------------------------------
    // Acceptance, and the dependency the validator declares
    // ---------------------------------------------------------------------------------------------

    /// <summary>A request that satisfies every measured rule is accepted, and reports nothing at all.</summary>
    [Fact]
    public void AWellFormedRequest_IsAcceptedAndReportsNothing()
    {
        ValidationResult result = _validator.Validate(Valid());

        result.IsValid.Should().BeTrue(Describe(result));
        result.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// The asynchronous entry point reaches the same verdict as the synchronous one, so a caller on either
    /// path is held to the identical rule set.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    /// <remarks>
    /// The asynchronous overload is awaited rather than blocked on, which is the migration's
    /// asynchronous-throughout rule applied to a test: no result property is read, no wait is performed,
    /// and the method returns a task rather than void so the framework observes its completion.
    /// </remarks>
    [Fact]
    public async Task TheAsynchronousEntryPoint_AgreesWithTheSynchronousOne()
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = null;

        ValidationResult synchronous = _validator.Validate(request);
        ValidationResult asynchronous = await _validator.ValidateAsync(request);

        ShouldReport(synchronous, nameof(CreatePortalRequest.PortalAlias), AliasRequired);
        ShouldReport(asynchronous, nameof(CreatePortalRequest.PortalAlias), AliasRequired);

        asynchronous.Errors.Should().HaveSameCount(synchronous.Errors);
    }

    /// <summary>
    /// The validator refuses to exist without a policy rather than falling back to built-in defaults.
    /// </summary>
    [Fact]
    public void AMissingPolicy_IsRefusedAtConstruction()
    {
        Action construction = () => _ = new CreatePortalRequestValidator(null!);

        construction.Should().Throw<ArgumentNullException>()
            .WithParameterName("policy");
    }

    /// <summary>
    /// The options type this suite constructs is the shipped legacy policy, not a convenient invention.
    /// </summary>
    /// <remarks>
    /// Pinning the defaults here is what entitles every credential assertion below to quote 7 and 0. If a
    /// future change tightened these defaults, this test would fail first and name the reason, rather than
    /// a dozen message assertions failing without explaining themselves.
    /// </remarks>
    [Fact]
    public void TheShippedPolicy_IsTheLegacyPolicy()
    {
        PasswordPolicyOptions policy = ShippedPolicy();

        policy.MinRequiredPasswordLength.Should().Be(
            7,
            "Website/release.config:L242 declares minRequiredPasswordLength=\"7\"");
        policy.MinRequiredNonAlphanumericCharacters.Should().Be(
            0,
            "Website/release.config:L243 declares minRequiredNonalphanumericCharacters=\"0\"");
        policy.PasswordStrengthRegularExpression.Should().BeEmpty(
            "neither Website/release.config nor Website/development.config configures a strength "
            + "pattern, so the rule that consumes one is never registered");
    }

    // ---------------------------------------------------------------------------------------------
    // signup.ascx L40-L41, valPortalName -> PortalAlias
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The alias is required, and it reports the legacy wording verbatim - including the wording's own
    /// mistake about which field it guards.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    /// <remarks>
    /// The parameter is declared nullable so that the absent case can be supplied at all: the contract's
    /// property is nullable, and a non-nullable theory parameter fed a null would not compile under this
    /// solution's warning policy.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Alias_IsRequired_AndCarriesTheLegacyWording(string? alias)
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = alias;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.PortalAlias),
            AliasRequired);
    }

    /// <summary>
    /// The alias length is bounded by the markup limit the legacy text box declared, and the boundary is
    /// exercised on both sides of the limit as well as at it.
    /// </summary>
    /// <param name="length">The submitted alias length.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    [Theory]
    [InlineData(127, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Alias_LengthIsBoundedAt128(int length, bool expectedToBeAccepted)
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = new string('a', length);

        ValidationResult result = _validator.Validate(request);

        if (expectedToBeAccepted)
        {
            result.IsValid.Should().BeTrue(Describe(result));
            return;
        }

        ShouldReportLengthFailure(result, nameof(CreatePortalRequest.PortalAlias), 128);
    }

    /// <summary>
    /// An alias a parent portal may legitimately carry is accepted, including the two normalisations the
    /// legacy screen applied before it measured anything.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("Site.Example.Com")]
    [InlineData("site.example.com")]
    [InlineData("localhost:8080")]
    [InlineData("http://localhost/dnn")]
    [InlineData("my-site.example.com/tenant")]
    public void ParentAlias_AcceptsHostsPortsPathsAndTheLegacyNormalisations(string alias)
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = alias;
        request.IsChildPortal = false;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>
    /// An alias containing anything outside the set the legacy guard loop permitted is refused, with the
    /// code-behind's own wording.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData("my site")]
    [InlineData("my,site")]
    [InlineData("my_site")]
    [InlineData("site?query=1")]
    [InlineData("site#fragment")]
    [InlineData("site@example.com")]
    [InlineData("site!")]
    public void ParentAlias_RefusesSpacesAndPunctuation(string alias)
    {
        CreatePortalRequest request = Valid();
        request.PortalAlias = alias;
        request.IsChildPortal = false;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.PortalAlias),
            AliasCharacters);
    }

    /// <summary>
    /// A child portal's alias is measured only after the final separator, and against the narrower of the
    /// two character sets.
    /// </summary>
    [Fact]
    public void ChildAlias_IsMeasuredAfterTheFinalSeparatorAndAgainstTheNarrowerSet()
    {
        CreatePortalRequest beneathAHost = Valid();
        beneathAHost.IsChildPortal = true;
        beneathAHost.PortalAlias = "site.example.com/tenant-one";

        ValidationResult accepted = _validator.Validate(beneathAHost);

        accepted.IsValid.Should().BeTrue(
            "only the segment after the final separator is measured for a child portal, so the dots "
            + "in the host part are never examined: " + Describe(accepted));

        CreatePortalRequest offendingSegment = Valid();
        offendingSegment.IsChildPortal = true;
        offendingSegment.PortalAlias = "site.example.com/tenant_one";

        ShouldReport(
            _validator.Validate(offendingSegment),
            nameof(CreatePortalRequest.PortalAlias),
            AliasCharacters);

        CreatePortalRequest noSeparator = Valid();
        noSeparator.IsChildPortal = true;
        noSeparator.PortalAlias = "site.example.com";

        ShouldReport(
            _validator.Validate(noSeparator),
            nameof(CreatePortalRequest.PortalAlias),
            AliasCharacters);

        CreatePortalRequest bareSegment = Valid();
        bareSegment.IsChildPortal = true;
        bareSegment.PortalAlias = "tenant-one";

        ValidationResult segmentAccepted = _validator.Validate(bareSegment);

        segmentAccepted.IsValid.Should().BeTrue(
            "a bare segment is what the legacy portal-page branch submitted and what the service "
            + "composes beneath the resolved parent authority, so it must be accepted: "
            + Describe(segmentAccepted));
    }

    /// <summary>A disallowed character is reported once however many times it occurs.</summary>
    [Fact]
    public void ADisallowedAliasCharacter_IsReportedOnceNotOncePerOccurrence()
    {
        CreatePortalRequest request = Valid();
        request.IsChildPortal = false;
        request.PortalAlias = "my site_with,punctuation";

        ValidationResult result = _validator.Validate(request);

        result.Errors
            .Where(failure => failure.ErrorMessage == AliasCharacters)
            .Should().ContainSingle(
                "four disallowed characters are present, and the legacy screen would have rendered "
                + "the sentence four times");
    }

    /// <summary>
    /// A single property reports only its first failure, reproducing the legacy sequencing in which a field
    /// that failed in the browser never reached the checks that came after it.
    /// </summary>
    [Fact]
    public void AlongsideALengthFailure_TheCharacterSetRuleDoesNotAlsoFire()
    {
        CreatePortalRequest request = Valid();
        request.IsChildPortal = false;
        request.PortalAlias = new string('_', 129);

        ValidationResult result = _validator.Validate(request);

        IReadOnlyList<ValidationFailure> aliasFailures =
            Failures(result, nameof(CreatePortalRequest.PortalAlias));

        aliasFailures.Should().ContainSingle(
            "an alias that is both too long and ill-formed reports the length once rather than "
            + "reporting both problems");
        aliasFailures[0].ErrorMessage.Should().NotBe(AliasCharacters);
    }

    // ---------------------------------------------------------------------------------------------
    // signup.ascx L67-L68, valTemplate -> TemplateFile
    // ---------------------------------------------------------------------------------------------

    /// <summary>A template must be chosen, and the requirement is expressed as ordinary string presence.</summary>
    /// <param name="templateFile">The submitted template.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Template_IsRequired_AsStringPresenceOnly(string? templateFile)
    {
        CreatePortalRequest request = Valid();
        request.TemplateFile = templateFile;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.TemplateFile),
            TemplateRequired);
    }

    /// <summary>
    /// The placeholder value the legacy drop-down list used is not treated as a sentinel: submitted as a
    /// template name it is an ordinary non-empty string and is accepted.
    /// </summary>
    /// <param name="templateFile">The submitted template.</param>
    /// <remarks>
    /// This is the inverse of a rule that must not exist, and it is asserted so that nobody later
    /// "restores" a numeric placeholder comparison the target deliberately does not have. Both the bare
    /// placeholder and a name merely beginning with it are accepted, because neither qualifies a path.
    /// </remarks>
    [Theory]
    [InlineData("-1")]
    [InlineData("-1.template")]
    public void Template_DoesNotTreatTheLegacyPlaceholderAsASentinel(string templateFile)
    {
        CreatePortalRequest request = Valid();
        request.TemplateFile = templateFile;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>A template selection must name a file and must not qualify it with a path.</summary>
    /// <param name="templateFile">The submitted template.</param>
    /// <remarks>
    /// MIGRATION: this rule has no counterpart in the markup, because a server-populated drop-down list
    /// could not express a path. The structural guarantee the list provided has to be stated as a rule now
    /// that the same value arrives as a free-form string, since the service concatenates it onto a
    /// directory and opens the result.
    /// </remarks>
    [Theory]
    [InlineData("Portals/_default/admin.template")]
    [InlineData("Portals\\_default\\admin.template")]
    [InlineData("C:\\templates\\admin.template")]
    [InlineData("../admin.template")]
    [InlineData("..template")]
    [InlineData("templates:admin.template")]
    public void Template_MustNameAFileWithoutAPath(string templateFile)
    {
        CreatePortalRequest request = Valid();
        request.TemplateFile = templateFile;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.TemplateFile),
            TemplateBareName);
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

    // ---------------------------------------------------------------------------------------------
    // signup.ascx L52, txtTitle -> PortalName: capped, and deliberately NOT required
    // ---------------------------------------------------------------------------------------------

    /// <summary>The portal name is bounded but not required, which is measured rather than overlooked.</summary>
    /// <param name="portalName">The submitted portal name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PortalName_IsNotRequired(string? portalName)
    {
        CreatePortalRequest request = Valid();
        request.PortalName = portalName;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
        Failures(result, nameof(CreatePortalRequest.PortalName)).Should().BeEmpty();
    }

    /// <summary>The portal name length is bounded at the width the markup and the schema agree on.</summary>
    /// <param name="length">The submitted name length.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    /// <remarks>
    /// Markup and schema agree here: <c>txtTitle</c> declares a maximum of 128 characters at
    /// <c>signup.ascx:L52</c> and the column is <c>PortalName nvarchar(128) NOT NULL</c>, created at
    /// <c>01.00.00.SqlDataProvider:L79</c> and preserved by the table rebuild at
    /// <c>01.00.05.SqlDataProvider:L1364</c>.
    /// </remarks>
    [Theory]
    [InlineData(127, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void PortalName_LengthIsBoundedAt128(int length, bool expectedToBeAccepted)
    {
        CreatePortalRequest request = Valid();
        request.PortalName = new string('a', length);

        ValidationResult result = _validator.Validate(request);

        if (expectedToBeAccepted)
        {
            result.IsValid.Should().BeTrue(Describe(result));
            return;
        }

        ShouldReportLengthFailure(result, nameof(CreatePortalRequest.PortalName), 128);
    }

    // ---------------------------------------------------------------------------------------------
    // signup.ascx L56 and L61, txtDescription and txtKeyWords: capped, no validator declared
    // ---------------------------------------------------------------------------------------------

    /// <summary>The two metadata fields are bounded by their columns and are otherwise unconstrained.</summary>
    /// <param name="length">The submitted length, applied to both fields at once.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    [Theory]
    [InlineData(499, true)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void MetadataFields_LengthIsBoundedAt500(int length, bool expectedToBeAccepted)
    {
        CreatePortalRequest request = Valid();
        request.Description = new string('a', length);
        request.KeyWords = new string('b', length);

        ValidationResult result = _validator.Validate(request);

        if (expectedToBeAccepted)
        {
            result.IsValid.Should().BeTrue(Describe(result));
            return;
        }

        ShouldReportLengthFailure(result, nameof(CreatePortalRequest.Description), 500);
        ShouldReportLengthFailure(result, nameof(CreatePortalRequest.KeyWords), 500);
    }

    /// <summary>
    /// Neither metadata field is required, and an absent value is indistinguishable from an empty one.
    /// </summary>
    /// <remarks>
    /// The legacy null contract represents an absent string as the empty string rather than as a database
    /// null - its string sentinel is literally "" - so a caller sending either is sending the same thing as
    /// far as the legacy data is concerned. This test pins that the two are treated alike here, which is
    /// why no rule in this file distinguishes them.
    /// </remarks>
    [Fact]
    public void MetadataFields_TreatAnAbsentValueAndAnEmptyOneAlike()
    {
        CreatePortalRequest absent = Valid();
        absent.Description = null;
        absent.KeyWords = null;

        CreatePortalRequest empty = Valid();
        empty.Description = string.Empty;
        empty.KeyWords = string.Empty;

        _validator.Validate(absent).IsValid.Should().BeTrue();
        _validator.Validate(empty).IsValid.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // signup.ascx L47, txtHomeDirectory: capped, no validator declared, defaulted by the service
    // ---------------------------------------------------------------------------------------------

    /// <summary>An absent home directory is a request for the server-side default rather than a failure.</summary>
    /// <param name="homeDirectory">The submitted directory.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HomeDirectory_IsOptional(string? homeDirectory)
    {
        CreatePortalRequest request = Valid();
        request.HomeDirectory = homeDirectory;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>The home directory length is bounded at the width of the column that stored it.</summary>
    /// <param name="length">The submitted directory length.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    [Theory]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void HomeDirectory_LengthIsBoundedAt100(int length, bool expectedToBeAccepted)
    {
        CreatePortalRequest request = Valid();
        request.HomeDirectory = new string('c', length);

        ValidationResult result = _validator.Validate(request);

        if (expectedToBeAccepted)
        {
            result.IsValid.Should().BeTrue(Describe(result));
            return;
        }

        ShouldReportLengthFailure(result, nameof(CreatePortalRequest.HomeDirectory), 100);
    }

    /// <summary>
    /// A home directory that could escape its root is refused, with the legacy screen's own wording.
    /// </summary>
    /// <param name="homeDirectory">The submitted directory.</param>
    /// <remarks>
    /// MIGRATION: this shape rule is net-new and is a deliberate divergence, recorded as such. The legacy
    /// screen's own check resolved the submitted value to a physical path and reported failure only when
    /// that resolution came back empty; it applied no shape rule at all, so a rooted, drive-qualified or
    /// parent-traversing value was concatenated straight into a path.
    /// </remarks>
    [Theory]
    [InlineData("/rooted")]
    [InlineData("../escape")]
    [InlineData("portals/../../escape")]
    [InlineData("portals/./here")]
    [InlineData("C:\\absolute")]
    [InlineData("portals//double")]
    [InlineData("   ")]
    public void HomeDirectory_RefusesAnythingThatCouldEscapeItsRoot(string homeDirectory)
    {
        CreatePortalRequest request = Valid();
        request.HomeDirectory = homeDirectory;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.HomeDirectory),
            HomeFolderInvalid);
    }

    /// <summary>A home directory carrying a control character is refused, with the same wording.</summary>
    /// <param name="homeDirectory">The submitted directory.</param>
    [Theory]
    [InlineData("cont\0ent")]
    [InlineData("Portals/0\0")]
    [InlineData("cont\nent")]
    [InlineData("Portals/\r\n0")]
    [InlineData("cont\tent")]
    [InlineData("Portals\t/0")]
    [InlineData("content\u007f")]
    public void HomeDirectory_RefusesAControlCharacter(string homeDirectory)
    {
        CreatePortalRequest request = Valid();
        request.HomeDirectory = homeDirectory;

        Func<ValidationResult> validate = () => _validator.Validate(request);

        ValidationResult result = validate.Should().NotThrow(
            "a hostile value is a refusal, not a fault").Subject;

        ShouldReport(result, nameof(CreatePortalRequest.HomeDirectory), HomeFolderInvalid);
    }

    /// <summary>An ordinary relative directory is accepted, including a nested one.</summary>
    /// <param name="homeDirectory">The submitted directory.</param>
    [Theory]
    [InlineData("Portals/0")]
    [InlineData("Portals/my-site")]
    [InlineData("Portals/0/")]
    [InlineData("content")]
    public void HomeDirectory_AcceptsAContainedRelativeDirectory(string homeDirectory)
    {
        CreatePortalRequest request = Valid();
        request.HomeDirectory = homeDirectory;

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    // ---------------------------------------------------------------------------------------------
    // signup.ascx L79-L80, L84-L85, L89-L90 and L106-L107: the administrator's identifying fields
    // ---------------------------------------------------------------------------------------------

    /// <summary>The administrator's given name is required, with the legacy wording.</summary>
    /// <param name="firstName">The submitted name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AdministratorFirstName_IsRequired(string? firstName)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorFirstName = firstName;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.AdministratorFirstName),
            FirstNameRequired);
    }

    /// <summary>The administrator's family name is required, with the legacy wording.</summary>
    /// <param name="lastName">The submitted name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AdministratorLastName_IsRequired(string? lastName)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorLastName = lastName;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.AdministratorLastName),
            LastNameRequired);
    }

    /// <summary>The administrator's sign-in name is required, with the legacy wording.</summary>
    /// <param name="username">The submitted name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AdministratorUsername_IsRequired(string? username)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorUsername = username;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.AdministratorUsername),
            UsernameRequired);
    }

    /// <summary>The administrator's address is required, with the legacy wording.</summary>
    /// <param name="email">The submitted address.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AdministratorEmail_IsRequired(string? email)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorEmail = email;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.AdministratorEmail),
            EmailRequired);
    }

    /// <summary>
    /// The two person-name fields are bounded by the SCHEMA width of fifty rather than the markup's
    /// hundred, and the boundary is exercised on both sides of the limit as well as at it.
    /// </summary>
    /// <param name="length">The submitted name length, applied to both fields at once.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    /// <remarks>
    /// The terminal width has to be derived correctly, and the obvious shortcut is unsound.
    /// </remarks>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void AdministratorNames_LengthIsBoundedBySchemaAt50(int length, bool expectedToBeAccepted)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorFirstName = new string('a', length);
        request.AdministratorLastName = new string('b', length);

        ValidationResult result = _validator.Validate(request);

        if (expectedToBeAccepted)
        {
            result.IsValid.Should().BeTrue(Describe(result));
            return;
        }

        ShouldReportLengthFailure(result, nameof(CreatePortalRequest.AdministratorFirstName), 50);
        ShouldReportLengthFailure(result, nameof(CreatePortalRequest.AdministratorLastName), 50);
    }

    /// <summary>The sign-in name is bounded at a hundred, where the markup and the schema agree.</summary>
    /// <param name="length">The submitted name length.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    /// <remarks>
    /// The column is <c>Username nvarchar(100) NOT NULL</c>, introduced by the second <c>Tmp_Users</c>
    /// rebuild, and the text box at <c>signup.ascx:L89</c> declares the same figure. This width is
    /// genuinely terminal: the only later statement touching the column adds its uniqueness constraint.
    /// </remarks>
    [Theory]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void AdministratorUsername_LengthIsBoundedAt100(int length, bool expectedToBeAccepted)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorUsername = new string('c', length);

        ValidationResult result = _validator.Validate(request);

        if (expectedToBeAccepted)
        {
            result.IsValid.Should().BeTrue(Describe(result));
            return;
        }

        ShouldReportLengthFailure(result, nameof(CreatePortalRequest.AdministratorUsername), 100);
    }

    /// <summary>
    /// The address is bounded at the terminal column width of 256, not at the markup's hundred, and the
    /// over-length case reports the shared malformed-address wording rather than a length message.
    /// </summary>
    /// <param name="length">The total submitted address length.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    /// <remarks>
    /// MIGRATION: the hundred on the text box at <c>signup.ascx:L106</c> is a presentation limit on one
    /// screen rather than a storage constraint, and the column it once matched no longer exists: the
    /// original <c>Email nvarchar(100) NOT NULL</c> was dropped at <c>02.02.01.SqlDataProvider:L51</c> and
    /// re-added as <c>Email nvarchar(256) NULL</c> at <c>03.00.13.SqlDataProvider:L110</c>.
    /// </remarks>
    [Theory]
    [InlineData(255, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void AdministratorEmail_LengthIsBoundedAt256(int length, bool expectedToBeAccepted)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorEmail = AddressOfLength(length);

        ValidationResult result = _validator.Validate(request);

        if (expectedToBeAccepted)
        {
            result.IsValid.Should().BeTrue(Describe(result));
            return;
        }

        ShouldReport(
            result,
            nameof(CreatePortalRequest.AdministratorEmail),
            EmailInvalid);
    }

    /// <summary>A malformed address is refused with the shared wording, and a well-formed one is accepted.</summary>
    /// <param name="email">The submitted address.</param>
    /// <param name="expectedToBeAccepted">Whether the address is expected to survive validation.</param>
    /// <remarks>
    /// MIGRATION: this shape rule is net-new on this path and its absence was a gap rather than a faithful
    /// omission. <c>signup.ascx:L106-L107</c> declares a required-field validator over the box and no
    /// expression validator at all, so a malformed address passed the screen and went on to become the new
    /// administrator's contact address - on a portal that by definition has no other account able to
    /// administer it.
    /// </remarks>
    [Theory]
    [InlineData("admin@example.com", true)]
    [InlineData("curator@national.museum", true)]
    [InlineData("first.last%tag+suffix@sub.example.co", true)]
    [InlineData("not-an-address", false)]
    [InlineData("missing@domain", false)]
    [InlineData("two@@example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData(".leading@example.com", false)]
    [InlineData("admin@example.c", false)]
    [InlineData("admin@example.c0m", false)]
    public void AdministratorEmail_IsShapeCheckedAgainstTheSharedAuthority(
        string email,
        bool expectedToBeAccepted)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorEmail = email;

        ValidationResult result = _validator.Validate(request);

        if (expectedToBeAccepted)
        {
            result.IsValid.Should().BeTrue(Describe(result));
            return;
        }

        ShouldReport(result, nameof(CreatePortalRequest.AdministratorEmail), EmailInvalid);
    }

    // ---------------------------------------------------------------------------------------------
    // signup.ascx L95-L96, valPassword -> AdministratorPassword, plus the policy it now reads
    // ---------------------------------------------------------------------------------------------

    /// <summary>The administrator's credential is required, with the legacy wording.</summary>
    /// <param name="password">The submitted credential.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AdministratorPassword_IsRequired(string? password)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorPassword = password;

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.AdministratorPassword),
            PasswordRequired);
    }

    /// <summary>
    /// A credential shorter than the configured minimum is refused with the legacy policy wording, both of
    /// whose bracketed tokens are resolved from the bound policy.
    /// </summary>
    /// <remarks>
    /// The screen itself declared only a required-field validator over this box, so requiredness is the
    /// screen's contribution and keeps the screen's wording. The policy lived further down, in the
    /// membership path, and under the shipped configuration only its minimum length could ever fire.
    /// </remarks>
    [Fact]
    public void AdministratorPassword_MustMeetTheConfiguredMinimumLength()
    {
        CreatePortalRequest tooShort = Valid();
        tooShort.AdministratorPassword = "Abc!12";

        tooShort.AdministratorPassword.Should().HaveLength(
            6,
            "one character short of the seven the shipped policy configures");

        ShouldReport(
            _validator.Validate(tooShort),
            nameof(CreatePortalRequest.AdministratorPassword),
            CredentialInvalidUnderShippedPolicy);

        CreatePortalRequest exactlyAtTheMinimum = Valid();
        exactlyAtTheMinimum.AdministratorPassword = "Abc!123";

        ValidationResult accepted = _validator.Validate(exactlyAtTheMinimum);

        accepted.IsValid.Should().BeTrue(Describe(accepted));
    }

    /// <summary>Neither bracketed token survives into a reported message.</summary>
    /// <remarks>
    /// The legacy membership path rewrote both tokens at run time from the same two configured numbers. A
    /// message that still carried a bracketed token would mean the substitution had been lost, which no
    /// wording assertion elsewhere would necessarily catch.
    /// </remarks>
    [Fact]
    public void TheCredentialPolicyMessage_CarriesNoUnresolvedToken()
    {
        CreatePortalRequest tooShort = Valid();
        tooShort.AdministratorPassword = "Abc!12";

        IReadOnlyList<string> messages = Messages(_validator.Validate(tooShort));

        messages.Should().NotBeEmpty();
        messages.Should().NotContain(
            message => message.Contains("[PasswordLength]", StringComparison.Ordinal));
        messages.Should().NotContain(
            message => message.Contains("[NoneAlphabet]", StringComparison.Ordinal));
        messages.Should().Contain(
            message => message.Contains("at least 7 characters", StringComparison.Ordinal));
        messages.Should().Contain(
            message => message.Contains("at least 0 non-alphanumeric", StringComparison.Ordinal));
    }

    /// <summary>No upper bound is imposed on the credential at the width the legacy text box declared.</summary>
    /// <param name="length">The submitted credential length.</param>
    /// <remarks>
    /// MIGRATION: the markup capped the two credential boxes at twenty characters, matching the plaintext
    /// column of the baseline schema, which the second table rebuild then widened.
    /// </remarks>
    [Theory]
    [InlineData(21)]
    [InlineData(64)]
    [InlineData(200)]
    public void AdministratorPassword_IsNotCappedAtTheLegacyMarkupWidth(int length)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorPassword = new string('q', length);

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>The credential is nonetheless bounded, by a net-new ceiling expressed in encoded bytes.</summary>
    /// <remarks>
    /// This ceiling has no legacy counterpart and is not a policy rule. The field feeds a deliberately
    /// expensive one-way function, so an unbounded value would let a caller choose how much work the server
    /// performs.
    /// </remarks>
    [Fact]
    public void AdministratorPassword_IsBoundedByTheSharedCredentialCeiling()
    {
        CredentialBounds.MaximumByteLength.Should().Be(256);

        CreatePortalRequest atTheCeiling = Valid();
        atTheCeiling.AdministratorPassword = new string('q', CredentialBounds.MaximumByteLength);

        ValidationResult accepted = _validator.Validate(atTheCeiling);

        accepted.IsValid.Should().BeTrue(Describe(accepted));

        CreatePortalRequest overTheCeiling = Valid();
        overTheCeiling.AdministratorPassword =
            new string('q', CredentialBounds.MaximumByteLength + 1);

        ShouldReport(
            _validator.Validate(overTheCeiling),
            nameof(CreatePortalRequest.AdministratorPassword),
            CredentialBounds.MaximumByteLengthMessage);
    }

    /// <summary>A multi-byte character counts as the bytes it occupies, not as one character.</summary>
    /// <remarks>
    /// A credential of well under the ceiling in characters can exceed it in bytes, and the rule is only
    /// meaningful if it measures what the algorithm consumes. This case would pass a character-counted
    /// ceiling and must not pass this one.
    /// </remarks>
    [Fact]
    public void AdministratorPassword_CountsEncodedBytesRatherThanCharacters()
    {
        CreatePortalRequest request = Valid();
        request.AdministratorPassword = new string('\u00e9', 129);

        request.AdministratorPassword.Should().HaveLength(
            129,
            "well inside a character-counted ceiling of 256");

        ShouldReport(
            _validator.Validate(request),
            nameof(CreatePortalRequest.AdministratorPassword),
            CredentialBounds.MaximumByteLengthMessage);
    }

    /// <summary>
    /// The non-alphanumeric rule is registered only when the configured minimum is above zero, and its
    /// message names the configured numbers rather than any built-in pair.
    /// </summary>
    /// <remarks>
    /// A configured minimum of zero skips the rule entirely rather than registering a comparison that can
    /// never fail, because a rule that cannot fail is indistinguishable from enforcement under review. The
    /// shipped configuration is exactly that case, so both halves have to be asserted: the shipped policy
    /// accepts a wholly alphanumeric credential, and a stricter policy refuses the same one.
    /// </remarks>
    [Fact]
    public void TheNonAlphanumericRule_IsDrivenByConfigurationAndSkippedAtZero()
    {
        const string whollyAlphanumeric = "Abcdefghij";

        CreatePortalRequest underShippedPolicy = Valid();
        underShippedPolicy.AdministratorPassword = whollyAlphanumeric;

        ValidationResult accepted = _validator.Validate(underShippedPolicy);

        accepted.IsValid.Should().BeTrue(
            "the shipped policy requires no non-alphanumeric character, so the rule is never "
            + "registered: " + Describe(accepted));

        CreatePortalRequestValidator stricter = ValidatorFor(StricterPolicy());

        CreatePortalRequest underStricterPolicy = Valid();
        underStricterPolicy.AdministratorPassword = whollyAlphanumeric;

        ShouldReport(
            stricter.Validate(underStricterPolicy),
            nameof(CreatePortalRequest.AdministratorPassword),
            CredentialInvalidUnderStricterPolicy);

        CreatePortalRequest satisfyingTheStricterPolicy = Valid();
        satisfyingTheStricterPolicy.AdministratorPassword = "Abcdefgh!@";

        ValidationResult satisfied = stricter.Validate(satisfyingTheStricterPolicy);

        satisfied.IsValid.Should().BeTrue(Describe(satisfied));
    }

    /// <summary>A configured strength pattern is enforced, and an unconfigured one registers no rule.</summary>
    [Fact]
    public void TheStrengthPatternRule_IsRegisteredOnlyWhenAPatternIsConfigured()
    {
        const string withoutADigit = "Abcdefgh";

        ValidationResult acceptedWithNoPatternConfigured = _validator.Validate(
            WithCredential(withoutADigit));

        acceptedWithNoPatternConfigured.IsValid.Should().BeTrue(
            "no pattern is configured, so no strength rule exists: "
            + Describe(acceptedWithNoPatternConfigured));

        PasswordPolicyOptions demandingADigit = ShippedPolicy();
        demandingADigit.PasswordStrengthRegularExpression = "^(?=.*[0-9]).+$";

        CreatePortalRequestValidator patterned = ValidatorFor(demandingADigit);

        ShouldReport(
            patterned.Validate(WithCredential(withoutADigit)),
            nameof(CreatePortalRequest.AdministratorPassword),
            CredentialInvalidUnderShippedPolicy);

        ValidationResult satisfied = patterned.Validate(WithCredential("Abcdefg1"));

        satisfied.IsValid.Should().BeTrue(Describe(satisfied));
    }

    // ---------------------------------------------------------------------------------------------
    // signup.ascx L101-L102, valConfirm: the one validator with no counterpart on this contract
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// No rule in the validator reproduces the confirmation validator or any of the three candidate
    /// mismatch wordings.
    /// </summary>
    /// <remarks>
    /// The eighth validator on the screen guarded a second credential box that was never one of the fifteen
    /// positional arguments - the call site at <c>Signup.ascx.vb:L274</c> passes the credential once - was
    /// never persisted, and is absent from the request contract by design, so there is no property for a
    /// rule to bind to.
    /// </remarks>
    [Fact]
    public void NoRuleReproducesTheConfirmationValidatorOrItsCandidateWordings()
    {
        IReadOnlyList<string> messages = AllMessagesFromEveryProbe();

        messages.Should().NotContain(ConfirmationRequired);
        messages.Should().NotContain(MismatchScreenWording);
        messages.Should().NotContain(MismatchSharedWording);
        messages.Should().NotContain(MismatchSharedAlternateWording);
    }

    // ---------------------------------------------------------------------------------------------
    // sitesettings.ascx L433 and L444: the two DataTypeCheck comparison validators
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Neither of the site-settings comparison validators has a counterpart on the creation contract, and
    /// no rule here can emit either wording.
    /// </summary>
    [Fact]
    public void NeitherSiteSettingsComparisonValidatorHasACounterpartHere()
    {
        IReadOnlyList<string> messages = AllMessagesFromEveryProbe();

        messages.Should().NotContain(ExpiryDateTypeCheck);
        messages.Should().NotContain(HostFeeTypeCheck);
        messages.Should().NotContain(
            message => message.Contains("expiry", StringComparison.OrdinalIgnoreCase));
        messages.Should().NotContain(
            message => message.Contains("currency", StringComparison.OrdinalIgnoreCase));
        messages.Should().NotContain(
            message => message.Contains("quota", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // The accumulator, and determinism
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One submission reports every failing field, not merely the first, reproducing the legacy screen's
    /// accumulating message.
    /// </summary>
    [Fact]
    public void EveryFailingField_IsReportedTogether()
    {
        CreatePortalRequest everythingBlank = new()
        {
            PortalName = null,
            PortalAlias = null,
            TemplateFile = string.Empty,
            IsChildPortal = false,
            AdministratorFirstName = null,
            AdministratorLastName = string.Empty,
            AdministratorUsername = "   ",
            AdministratorPassword = null,
            AdministratorEmail = string.Empty,
        };

        ValidationResult result = _validator.Validate(everythingBlank);

        ShouldReport(result, nameof(CreatePortalRequest.PortalAlias), AliasRequired);
        ShouldReport(result, nameof(CreatePortalRequest.TemplateFile), TemplateRequired);
        ShouldReport(result, nameof(CreatePortalRequest.AdministratorFirstName), FirstNameRequired);
        ShouldReport(result, nameof(CreatePortalRequest.AdministratorLastName), LastNameRequired);
        ShouldReport(result, nameof(CreatePortalRequest.AdministratorUsername), UsernameRequired);
        ShouldReport(result, nameof(CreatePortalRequest.AdministratorPassword), PasswordRequired);
        ShouldReport(result, nameof(CreatePortalRequest.AdministratorEmail), EmailRequired);

        result.Errors.Should().HaveCount(
            7,
            "seven of the eight legacy required-field validators guard a property on this contract, "
            + "and the eighth guards a browser-only confirmation control that was never submitted");
    }

    /// <summary>The verdict and the legacy wording do not depend on the server's regional settings.</summary>
    /// <param name="cultureName">The culture to pin for the duration of the assertions.</param>
    [Theory]
    [InlineData("")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void TheVerdictDoesNotDependOnTheAmbientCulture(string cultureName)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            var pinned = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = pinned;
            CultureInfo.CurrentUICulture = pinned;

            ValidationResult accepted = _validator.Validate(Valid());

            accepted.IsValid.Should().BeTrue(Describe(accepted));

            CreatePortalRequest upperCaseAlias = Valid();
            upperCaseAlias.IsChildPortal = false;
            upperCaseAlias.PortalAlias = "LOCALHOST-I";

            ValidationResult loweredInvariantly = _validator.Validate(upperCaseAlias);

            loweredInvariantly.IsValid.Should().BeTrue(
                "a capital I must lower to an ordinary i in every culture, or an alias the legacy "
                + "screen accepted would be refused: " + Describe(loweredInvariantly));

            CreatePortalRequest offendingAlias = Valid();
            offendingAlias.IsChildPortal = false;
            offendingAlias.PortalAlias = "my site";

            ShouldReport(
                _validator.Validate(offendingAlias),
                nameof(CreatePortalRequest.PortalAlias),
                AliasCharacters);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures and assertion helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the password policy the legacy application shipped, which is the options type's own default
    /// state.
    /// </summary>
    /// <returns>The shipped policy.</returns>
    private static PasswordPolicyOptions ShippedPolicy() => new();

    /// <summary>
    /// Builds a policy stricter than the shipped one, used to prove that the credential thresholds and the
    /// numbers in the credential message are read from configuration rather than hard-coded.
    /// </summary>
    /// <returns>The stricter policy.</returns>
    private static PasswordPolicyOptions StricterPolicy() => new()
    {
        MinRequiredPasswordLength = 10,
        MinRequiredNonAlphanumericCharacters = 2,
    };

    /// <summary>Builds a validator bound to a specific policy.</summary>
    /// <param name="policy">The policy to bind.</param>
    /// <returns>The validator.</returns>
    private static CreatePortalRequestValidator ValidatorFor(PasswordPolicyOptions policy) =>
        new(policy);

    /// <summary>
    /// Builds a request that satisfies every measured rule, so that each test can violate exactly one field
    /// and a failure names the rule that broke.
    /// </summary>
    /// <returns>The request.</returns>
    private static CreatePortalRequest Valid() => new()
    {
        PortalName = "Migration Portal",
        PortalAlias = "localhost",
        Description = "A portal created by the migration parity suite.",
        KeyWords = "migration, parity",
        HomeDirectory = "Portals/migration",
        TemplateFile = "admin.template",
        IsChildPortal = false,
        AdministratorFirstName = "Migration",
        AdministratorLastName = "Administrator",
        AdministratorUsername = "migration_admin",
        AdministratorPassword = "Migr8tion!Pass",
        AdministratorEmail = "admin@example.com",
    };

    /// <summary>Builds an otherwise valid request carrying a specific credential.</summary>
    /// <param name="password">The credential to submit.</param>
    /// <returns>The request.</returns>
    private static CreatePortalRequest WithCredential(string password)
    {
        CreatePortalRequest request = Valid();
        request.AdministratorPassword = password;

        return request;
    }

    /// <summary>
    /// Builds a well-formed mail address of an exact total length, so that a length boundary can be
    /// exercised without also tripping the shape rule.
    /// </summary>
    /// <param name="totalLength">The required total length, which must exceed the fixed domain part.</param>
    /// <returns>The address.</returns>
    private static string AddressOfLength(int totalLength)
    {
        const string domain = "@example.com";

        string address = new string('d', totalLength - domain.Length) + domain;

        address.Should().HaveLength(totalLength, "the fixture must produce the length it was asked for");

        return address;
    }

    /// <summary>
    /// Validates every probe this suite uses to prove a rule's absence, and returns every message any of
    /// them produced.
    /// </summary>
    /// <returns>The messages.</returns>
    private IReadOnlyList<string> AllMessagesFromEveryProbe()
    {
        CreatePortalRequest blank = new();

        CreatePortalRequest malformed = Valid();
        malformed.PortalAlias = "my site";
        malformed.TemplateFile = "Portals/_default/admin.template";
        malformed.HomeDirectory = "../escape";
        malformed.AdministratorEmail = "not-an-address";
        malformed.AdministratorPassword = "Abc!12";

        CreatePortalRequest overlong = Valid();
        overlong.PortalName = new string('a', 129);
        overlong.PortalAlias = new string('a', 129);
        overlong.Description = new string('a', 501);
        overlong.KeyWords = new string('a', 501);
        overlong.HomeDirectory = new string('a', 101);
        overlong.AdministratorFirstName = new string('a', 51);
        overlong.AdministratorLastName = new string('a', 51);
        overlong.AdministratorUsername = new string('a', 101);
        overlong.AdministratorEmail = AddressOfLength(257);
        overlong.AdministratorPassword = new string('q', CredentialBounds.MaximumByteLength + 1);

        List<string> messages = [];

        foreach (CreatePortalRequest probe in new[] { Valid(), blank, malformed, overlong })
        {
            messages.AddRange(Messages(_validator.Validate(probe)));
        }

        messages.Should().NotBeEmpty("the probes must actually exercise the rules");

        return messages;
    }

    /// <summary>
    /// Asserts that a result reports exactly one failure carrying both the expected property name and the
    /// expected wording.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <param name="propertyName">The property the failure must name.</param>
    /// <param name="expectedMessage">The wording the failure must carry, character for character.</param>
    private static void ShouldReport(
        ValidationResult result,
        string propertyName,
        string expectedMessage)
    {
        result.Errors.Should().ContainSingle(
            failure => failure.PropertyName == propertyName
                && failure.ErrorMessage == expectedMessage,
            "the failure reaching the client is the pair of property and message, and it reported: "
            + Rendered(result));
    }

    /// <summary>
    /// Asserts that a result reports exactly one length failure against a property, and that the failure
    /// names the configured bound.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <param name="propertyName">The property the failure must name.</param>
    /// <param name="maximumLength">The bound the failure must name.</param>
    /// <remarks>
    /// The two length rules are the only rules in the validator whose wording it does not supply itself:
    /// the validation framework generates it and translates it according to the ambient interface culture.
    /// </remarks>
    private static void ShouldReportLengthFailure(
        ValidationResult result,
        string propertyName,
        int maximumLength)
    {
        IReadOnlyList<ValidationFailure> failures = Failures(result, propertyName);

        failures.Should().ContainSingle(
            "a single property reports only its first failure, and it reported: " + Rendered(result));
        failures[0].ErrorMessage.Should().Contain(
            maximumLength.ToString(CultureInfo.InvariantCulture),
            "the bound the rule enforces is the part of a generated message that carries the "
            + "migration decision");
    }

    /// <summary>Returns every message a result reported.</summary>
    /// <param name="result">The validation result.</param>
    /// <returns>The messages.</returns>
    private static IReadOnlyList<string> Messages(ValidationResult result) =>
        result.Errors.Select(failure => failure.ErrorMessage).ToList();

    /// <summary>Returns the failures a result reported against one property.</summary>
    /// <param name="result">The validation result.</param>
    /// <param name="propertyName">The property to filter on.</param>
    /// <returns>The failures.</returns>
    private static IReadOnlyList<ValidationFailure> Failures(
        ValidationResult result,
        string propertyName) =>
        result.Errors
            .Where(failure => string.Equals(failure.PropertyName, propertyName, StringComparison.Ordinal))
            .ToList();

    /// <summary>Renders a result as a reason for an assertion that expected the request to be accepted.</summary>
    /// <param name="result">The validation result.</param>
    /// <returns>The rendered reason.</returns>
    private static string Describe(ValidationResult result) =>
        "the request should have been accepted but reported: " + Rendered(result);

    /// <summary>Renders every failure a result reported as property-and-message pairs.</summary>
    /// <param name="result">The validation result.</param>
    /// <returns>The rendered failures.</returns>
    private static string Rendered(ValidationResult result) =>
        result.Errors.Count == 0
            ? "nothing"
            : string.Join(
                " | ",
                result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));
}
