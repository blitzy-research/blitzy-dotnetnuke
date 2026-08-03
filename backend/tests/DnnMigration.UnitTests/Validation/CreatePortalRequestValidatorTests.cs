// MIGRATION: this suite is the parity proof for Validation/CreatePortalRequestValidator.cs. Its subject is
// not "does the validator reject bad input" but "does the validator enforce, field for field and word for
// word, the rule set the legacy portal signup screen enforced". Every assertion below therefore names the
// legacy declaration it reproduces, and every message is quoted character for character rather than matched
// by substring, because a substring match cannot tell "Portal Name Is Required." apart from
// "<br>Portal Name Is Required." - and that single difference is the divergence recorded further down.
//
// MIGRATION: the legacy declarations, measured rather than assumed. Website/admin/Portal/signup.ascx is 115
// lines and declares EIGHT asp:requiredfieldvalidator controls and nothing else - zero
// asp:RegularExpressionValidator, zero asp:CompareValidator. Website/admin/Portal/sitesettings.ascx is 568
// lines and declares the mirror image: zero required-field validators and exactly TWO asp:CompareValidator,
// both Operator="DataTypeCheck". Both families are covered here, the second by proving its absence from this
// contract rather than by inventing a rule for it. A census across the five in-scope admin directories -
// Website/admin/{Portal,Users,Security,Modules,Tabs} - returns RequiredField 16, RegularExpression 3,
// Compare 19, Range 0 and Custom 1, so asp:CompareValidator is in fact the LARGEST family in the surface
// being migrated and the plan's naming of only the first two families understates it.
//
// MIGRATION: the eight messages this screen displayed at run time are NOT the eight inline errormessage
// attributes. Every validator carries a resourcekey, and ASP.NET localisation overwrites the inline text
// with the resource value, which in every one of the eight cases is the inline text prefixed with a literal
// <br> element - Website/admin/Portal/App_LocalResources/Signup.ascx.resx L144/145, L183/184, L186/187,
// L207/208, L210/211, L213/214, L222/223 and L267/268, each holding <data name> on the first line and
// <value> on the second. The target drops the <br>, and that IS a divergence rather than an oversight: these
// strings now travel as data in the errors member of an RFC 7807 problem document, where an HTML fragment
// would be a defect. For the Portal validators the leading <br> is the ONLY difference between the inline
// and resource forms, so the divergence is exactly that and nothing more. The assertions below pin the
// stripped form, and the <br>-bearing form is quoted in this note so the measurement survives.
//
// MIGRATION: the legacy screen accumulated its messages - Signup.ascx.vb:L193, L214 and L221 all append
// with "&=" - so one postback could report several problems at once, and a per-character guard loop could
// report the SAME problem several times. The first behaviour is reproduced and asserted; the second is not,
// and its non-reproduction is asserted too, so that the difference is a tested decision rather than an
// accident.

using System.Globalization;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule and word-for-word parity between
/// <see cref="CreatePortalRequestValidator"/> and the validator declarations measured on the legacy
/// portal signup screen, together with the two comparison validators on the legacy site-settings
/// screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both fields of every failure are asserted.</b> A test that proves only "the request was
/// rejected" does not prove parity, because the pair the client actually consumes is the property
/// name and the message: those two populate the <c>errors</c> dictionary of the problem document the
/// Api layer returns. Every assertion here therefore goes through
/// <see cref="ShouldReport(ValidationResult, string, string)"/>, which requires a single failure
/// carrying both the expected property and the expected wording. The problem-document transport
/// itself belongs to the Api layer and is deliberately unreachable from this project.
/// </para>
/// <para>
/// <b>Wording that looks wrong is preserved on purpose.</b> The alias field reports "Portal Name Is
/// Required." because <c>valPortalName</c> declares <c>controltovalidate="txtPortalName"</c> at
/// <c>signup.ascx:L41</c> while <c>L39</c> labels that same box "Portal Alias:". The message names a
/// field it does not guard, and correcting it would break the equivalence of error messages that the
/// migration requires. Two spaces after a sentence period, and the one message that ends without a
/// full stop, are reproduced for the same reason and must not be tidied.
/// </para>
/// <para>
/// <b>Three asymmetries are pinned here because each looks like a defect until the legacy source is
/// checked.</b> The portal title is length-capped but <em>not</em> required, because the screen
/// declared no validator on it. No lower bound is enforced on any fee or quota, because the only
/// comparison validator over a fee is a type check with no companion. And the administrator's given
/// and family names are capped at the schema's fifty characters rather than the markup's hundred.
/// </para>
/// <para>
/// <b>The validator's one dependency is the bound password policy.</b> It is constructed directly
/// with a plain options object, so this suite needs no container, no configuration source and no
/// test double of any kind. The defaults of that options type are themselves the shipped legacy
/// policy - a minimum length of seven and no required non-alphanumeric characters, from
/// <c>Website/release.config:L242-L243</c> - which is why <see cref="ShippedPolicy"/> constructs it
/// with no adjustment and why the substituted message below names 7 and 0.
/// </para>
/// </remarks>
public class CreatePortalRequestValidatorTests
{
    /// <summary>
    /// The <c>valPortalName</c> message (<c>signup.ascx:L40-L41</c>), which guards the alias.
    /// </summary>
    private const string AliasRequired = "Portal Name Is Required.";

    /// <summary>
    /// The <c>InvalidName</c> message the signup code-behind raised for a disallowed alias character
    /// (<c>Signup.ascx.resx:L234-L235</c>, raised at <c>Signup.ascx.vb:L193</c> and L214).
    /// </summary>
    private const string AliasCharacters = "The Portal Name Must Not Contain Spaces Or Punctuation.";

    /// <summary>
    /// The <c>valTemplate</c> message (<c>signup.ascx:L67-L68</c>). It is the one message on the
    /// screen with no trailing full stop, and that is asserted rather than normalised.
    /// </summary>
    private const string TemplateRequired = "Please select a template file";

    /// <summary>
    /// Wording for the bare-file-name rule on the template selection, which has no legacy
    /// counterpart because a server-populated drop-down list could not submit a path.
    /// </summary>
    private const string TemplateBareName =
        "Template must be a file name and must not contain a directory path.";

    /// <summary>
    /// The <c>valFirstName</c> message (<c>signup.ascx:L79-L80</c>).
    /// </summary>
    private const string FirstNameRequired = "First Name Is Required.";

    /// <summary>
    /// The <c>valLastName</c> message (<c>signup.ascx:L84-L85</c>).
    /// </summary>
    private const string LastNameRequired = "Last Name Is Required.";

    /// <summary>
    /// The <c>valUsername</c> message (<c>signup.ascx:L89-L90</c>).
    /// </summary>
    private const string UsernameRequired = "Username Is Required.";

    /// <summary>
    /// The <c>valPassword</c> message (<c>signup.ascx:L95-L96</c>).
    /// </summary>
    private const string PasswordRequired = "Password Is Required.";

    /// <summary>
    /// The <c>valEmail</c> message (<c>signup.ascx:L106-L107</c>).
    /// </summary>
    private const string EmailRequired = "Email Is Required.";

    /// <summary>
    /// The shared <c>InvalidEmail.Text</c> wording
    /// (<c>Website/App_GlobalResources/SharedResources.resx:L282-L283</c>), including the two spaces
    /// after the sentence period that the legacy file uses.
    /// </summary>
    private const string EmailInvalid =
        "The email address specified is invalid.  Please specify a valid email address.";

    /// <summary>
    /// The screen's own <c>InvalidHomeFolder.Text</c> wording
    /// (<c>Signup.ascx.resx:L288-L289</c>).
    /// </summary>
    private const string HomeFolderInvalid = "The Home Folder you specified is not valid.";

    /// <summary>
    /// The shared <c>InvalidPassword.Text</c> wording
    /// (<c>SharedResources.resx:L285-L286</c>) with its two bracketed tokens resolved against the
    /// shipped policy, exactly as the legacy membership path resolved them at run time.
    /// </summary>
    /// <remarks>
    /// The numbers are 7 and 0 because <c>Website/release.config</c> declares
    /// <c>minRequiredPasswordLength="7"</c> at L242 and
    /// <c>minRequiredNonalphanumericCharacters="0"</c> at L243, and the options type defaults to the
    /// same pair. A test further down proves neither bracketed token survives into a message.
    /// </remarks>
    private const string CredentialInvalidUnderShippedPolicy =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at "
        + "least 7 characters in length and contain at least 0 non-alphanumeric characters.";

    /// <summary>
    /// The same wording resolved against the stricter policy used by the tests that prove the
    /// thresholds are read from configuration rather than hard-coded.
    /// </summary>
    private const string CredentialInvalidUnderStricterPolicy =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at "
        + "least 10 characters in length and contain at least 2 non-alphanumeric characters.";

    // MIGRATION: the three literals below are the candidate wordings for a confirmation-mismatch rule, and
    // this contract carries NO rule that can emit any of them. They are quoted here, and asserted absent
    // further down, because the legacy sources disagree with one another about which one the mismatch used
    // and an executing agent must not silently adopt one. The screen's own local entry is what actually
    // rendered on this flow; the two shared entries are a duplicate pair under one logical name, and the
    // first of the three is the only one with no trailing full stop.

    /// <summary>
    /// The <c>valConfirm</c> message (<c>signup.ascx:L101-L102</c>), whose text box has no
    /// counterpart on the request contract.
    /// </summary>
    private const string ConfirmationRequired = "Password Confirmation Is Required.";

    /// <summary>
    /// <c>InvalidPassword.Text</c> from the screen's own resource file
    /// (<c>Signup.ascx.resx:L237-L238</c>) - the wording this flow actually rendered for a mismatch,
    /// raised at <c>Signup.ascx.vb:L221</c>.
    /// </summary>
    private const string MismatchScreenWording = "The Password Values Entered Do Not Match.";

    /// <summary>
    /// <c>PasswordMismatch.Text</c> from the shared resource file
    /// (<c>SharedResources.resx:L852-L853</c>). The only one of the three with no trailing full stop.
    /// </summary>
    private const string MismatchSharedWording = "The Password and Confirmation Passwords do not match";

    /// <summary>
    /// <c>PasswordMismatch.Text1</c> from the shared resource file
    /// (<c>SharedResources.resx:L969-L970</c>), the duplicate of the entry above under one logical
    /// name.
    /// </summary>
    private const string MismatchSharedAlternateWording = "Password Values Entered Do Not Match.";

    // MIGRATION: the two literals below belong to the site-settings screen's DataTypeCheck comparison
    // validators. Neither field they guard is part of the creation contract - the legacy creation call took
    // fifteen positional arguments and neither an expiry date nor a hosting fee was among them
    // (Signup.ascx.vb:L274) - so no rule here can emit either wording, and that is asserted rather than
    // assumed. The first carries no resourcekey, so its inline text with the leading <br> is authoritative
    // and there is no resource divergence for it; the second's resource value
    // (SiteSettings.ascx.resx:L495-L496) is byte-identical to its inline text, so there is none for it
    // either.

    /// <summary>
    /// The <c>valExpiryDate</c> message (<c>sitesettings.ascx:L433-L435</c>),
    /// <c>Operator="DataTypeCheck" Type="Date"</c>, quoted with the leading element it carries inline.
    /// </summary>
    private const string ExpiryDateTypeCheck = "<br>Invalid expiry date!";

    /// <summary>
    /// The <c>valHostFee</c> message (<c>sitesettings.ascx:L444-L446</c>),
    /// <c>Operator="DataTypeCheck" Type="Currency"</c>. It has no lower-bound companion, which is why
    /// no fee rule exists to reproduce.
    /// </summary>
    private const string HostFeeTypeCheck = "Invalid fee, needs to be a currency value!";

    /// <summary>
    /// The validator under test, constructed with the shipped legacy policy.
    /// </summary>
    private readonly CreatePortalRequestValidator _validator = new(ShippedPolicy());

    // ---------------------------------------------------------------------------------------------
    // Acceptance, and the dependency the validator declares
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A request that satisfies every measured rule is accepted, and reports nothing at all.
    /// </summary>
    [Fact]
    public void AWellFormedRequest_IsAcceptedAndReportsNothing()
    {
        ValidationResult result = _validator.Validate(Valid());

        result.IsValid.Should().BeTrue(Describe(result));
        result.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// The asynchronous entry point reaches the same verdict as the synchronous one, so a caller on
    /// either path is held to the identical rule set.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    /// <remarks>
    /// The asynchronous overload is awaited rather than blocked on, which is the migration's
    /// asynchronous-throughout rule applied to a test: no result property is read, no wait is
    /// performed, and the method returns a task rather than void so the framework observes its
    /// completion.
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
    /// <remarks>
    /// MIGRATION: the legacy screen enforced no credential policy of its own and the policy was
    /// applied further down the call chain, inside the membership path. Concentrating it at the
    /// boundary means the boundary must be told what the policy is; silently defaulting would enforce
    /// a policy nobody configured, which is the failure mode this guard exists to prevent.
    /// </remarks>
    [Fact]
    public void AMissingPolicy_IsRefusedAtConstruction()
    {
        Action construction = () => _ = new CreatePortalRequestValidator(null!);

        construction.Should().Throw<ArgumentNullException>()
            .WithParameterName("policy");
    }

    /// <summary>
    /// The options type this suite constructs is the shipped legacy policy, not a convenient
    /// invention.
    /// </summary>
    /// <remarks>
    /// Pinning the defaults here is what entitles every credential assertion below to quote 7 and 0.
    /// If a future change tightened these defaults, this test would fail first and name the reason,
    /// rather than a dozen message assertions failing without explaining themselves.
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
    /// The parameter is declared nullable so that the absent case can be supplied at all: the
    /// contract's property is nullable, and a non-nullable theory parameter fed a null would not
    /// compile under this solution's warning policy.
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
    /// The alias length is bounded by the markup limit the legacy text box declared, and the boundary
    /// is exercised on both sides of the limit as well as at it.
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
    /// An alias a parent portal may legitimately carry is accepted, including the two normalisations
    /// the legacy screen applied before it measured anything.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    /// <remarks>
    /// The upper-case case matters more than it looks: the permitted character sets hold lower-case
    /// letters only, and the legacy screen lowered the value at <c>Signup.ascx.vb:L183</c> before
    /// testing it, so measuring the raw value would reject an alias the legacy screen accepted. The
    /// scheme-prefixed case is the second normalisation, from L184.
    /// </remarks>
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
    /// An alias containing anything outside the set the legacy guard loop permitted is refused, with
    /// the code-behind's own wording.
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
    /// A child portal's alias is measured only after the final separator, and against the narrower of
    /// the two character sets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are asserted together because they are one rule: the legacy code derived the
    /// measured segment at <c>Signup.ascx.vb:L202</c> and then widened the permitted set only when
    /// the portal was not a child (L207-L210). Testing either half alone would let the other regress.
    /// </para>
    /// <para>
    /// The fourth case is the one this rule exists to serve and was previously untested. A BARE SEGMENT -
    /// no separator at all - is what the legacy portal-page branch submitted
    /// (<c>Signup.ascx.vb:L187-L197</c>), and the service composes it beneath the resolved parent's
    /// authority. It must therefore be ACCEPTED here, and the third case shows why that is not vacuous:
    /// a no-separator value is measured whole, so a bare segment passes and a host name typed into the
    /// child field does not. Tightening this rule to demand a separator would make the composition
    /// unreachable and silently reinstate the defect, which is what pins the case here.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A disallowed character is reported once however many times it occurs.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy guard loop appended its message inside a per-character loop
    /// (<c>Signup.ascx.vb:L191-L195</c> and L212-L216), so an alias with three offending characters
    /// rendered the same sentence three times. That duplication was an artefact of concatenating into
    /// an HTML string and is deliberately not reproduced: the rule that fires is identical, and only
    /// the repetition is gone. A problem document reports one failure per rule per property, so
    /// reproducing the duplication would produce three identical entries for one problem.
    /// </remarks>
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
    /// A single property reports only its first failure, reproducing the legacy sequencing in which a
    /// field that failed in the browser never reached the checks that came after it.
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

    /// <summary>
    /// A template must be chosen, and the requirement is expressed as ordinary string presence.
    /// </summary>
    /// <param name="templateFile">The submitted template.</param>
    /// <remarks>
    /// MIGRATION: <c>valTemplate</c> is declared <c>InitialValue="-1"</c>
    /// (<c>signup.ascx:L67-L68</c>), which is how a required-field validator refuses a drop-down list
    /// still sitting on its unselected placeholder. That "-1" is a placeholder for a list item and NOT
    /// an identifier, and the target field is a template file name, so presence became ordinary string
    /// presence and no numeric comparison exists to test. The distinction is load-bearing rather than
    /// pedantic: the same negative value is the legacy absent-integer sentinel
    /// (<c>Library/Components/Shared/Null.vb</c>, whose <c>NullInteger</c> is -1) while being
    /// simultaneously the seed of the portals primary key
    /// (<c>01.00.00.SqlDataProvider:L77</c>, <c>IDENTITY (-1, 1)</c>) and therefore a real, addressable
    /// row identifier - the shipped default portal occupies the very next value, zero
    /// (<c>01.00.00.SqlDataProvider:L7125</c>). Treating -1 as a marker of absence anywhere would be a
    /// defect, so no test here asserts that any identifier must be positive or non-zero.
    /// </remarks>
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
    /// The placeholder value the legacy drop-down list used is not treated as a sentinel: submitted as
    /// a template name it is an ordinary non-empty string and is accepted.
    /// </summary>
    /// <param name="templateFile">The submitted template.</param>
    /// <remarks>
    /// This is the inverse of a rule that must not exist, and it is asserted so that nobody later
    /// "restores" a numeric placeholder comparison the target deliberately does not have. Both the
    /// bare placeholder and a name merely beginning with it are accepted, because neither qualifies a
    /// path.
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

    /// <summary>
    /// A template selection must name a file and must not qualify it with a path.
    /// </summary>
    /// <param name="templateFile">The submitted template.</param>
    /// <remarks>
    /// MIGRATION: this rule has no counterpart in the markup, because a server-populated drop-down
    /// list could not express a path. The structural guarantee the list provided has to be stated as a
    /// rule now that the same value arrives as a free-form string, since the service concatenates it
    /// onto a directory and opens the result. Preserving a guarantee is not the same as adding a
    /// restriction, and whether the named template exists is still the service's question, never this
    /// one's.
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

    /// <summary>
    /// The portal name is bounded but not required, which is measured rather than overlooked.
    /// </summary>
    /// <param name="portalName">The submitted portal name.</param>
    /// <remarks>
    /// MIGRATION: the box that supplies this value is <c>txtTitle</c>, labelled "Title:" at
    /// <c>signup.ascx:L51</c> and declaring no validator whatsoever at L52, yet its text is the first
    /// of the fifteen positional arguments the legacy screen passed
    /// (<c>Signup.ascx.vb:L274</c>). The similarly named <c>valPortalName</c> validator, whose message
    /// reads "Portal Name Is Required.", is bound to a different box altogether - the one L39 labels
    /// "Portal Alias:" - so deriving a requiredness rule for this field from that wording would invent
    /// a rule the legacy screen never enforced. The column is declared NOT NULL, but an empty string
    /// satisfies NOT NULL and the legacy screen could submit one.
    /// </remarks>
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

    /// <summary>
    /// The portal name length is bounded at the width the markup and the schema agree on.
    /// </summary>
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

    /// <summary>
    /// The two metadata fields are bounded by their columns and are otherwise unconstrained.
    /// </summary>
    /// <param name="length">The submitted length, applied to both fields at once.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    /// <remarks>
    /// Neither box declared a validator, so neither field is required and only the width applies. The
    /// markup limit of 500 at <c>signup.ascx:L56</c> and L61 matches
    /// <c>Description nvarchar(500) NULL</c> and <c>KeyWords nvarchar(500) NULL</c> in the rebuilt
    /// table.
    /// </remarks>
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
    /// MIGRATION: the legacy null contract represents an absent string as the empty string rather than
    /// as a database null - its string sentinel is literally "" - so a caller sending either is
    /// sending the same thing as far as the legacy data is concerned. This test pins that the two are
    /// treated alike here, which is why no rule in this file distinguishes them.
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

    /// <summary>
    /// An absent home directory is a request for the server-side default rather than a failure.
    /// </summary>
    /// <param name="homeDirectory">The submitted directory.</param>
    /// <remarks>
    /// MIGRATION: the legacy screen pre-filled this box with a placeholder and sent the empty string
    /// when the user left it untouched (<c>Signup.ascx.vb:L245-L249</c>), after which the controller
    /// derived the default from the new portal's identifier - a value that cannot exist before the row
    /// does. So the defaulting stays in the service and no requiredness rule appears at the boundary.
    /// </remarks>
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

    /// <summary>
    /// The home directory length is bounded at the width of the column that stored it.
    /// </summary>
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
    /// MIGRATION: this shape rule is net-new and is a deliberate divergence, recorded as such. The
    /// legacy screen's own check resolved the submitted value to a physical path and reported failure
    /// only when that resolution came back empty (<c>Signup.ascx.vb:L251-L257</c>); it applied no
    /// shape rule at all, so a rooted, drive-qualified or parent-traversing value was concatenated
    /// straight into a path. Preserving that is not something behaviour preservation can be read to
    /// require. What is preserved is the wording, taken verbatim from
    /// <c>Signup.ascx.resx:L288-L289</c>, so an operator sees the message the legacy application
    /// showed; only the moment of refusal moves earlier.
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

    /// <summary>
    /// A home directory carrying a control character is refused, with the same wording.
    /// </summary>
    /// <param name="homeDirectory">The submitted directory.</param>
    /// <remarks>
    /// <para>
    /// This is a separate branch of the shape rule from the traversal cases above, and it is reachable by
    /// an input none of them describes: every value below is an ORDINARY RELATIVE PATH — no leading
    /// separator, no drive letter, no backslash, no <c>.</c> or <c>..</c> segment, no blank segment and no
    /// wholly-blank value — so it clears every other clause and can only be refused by the
    /// control-character test. Without these cases that test could be deleted and every other
    /// home-directory assertion would stay green.
    /// </para>
    /// <para>
    /// The values matter beyond coverage. A NUL byte truncates a path at the operating-system boundary,
    /// where <c>content\0.txt</c> and <c>content</c> address different things to a validator and the same
    /// thing to a file system. A newline or carriage return smuggles a second line into anything that
    /// later writes the value out — a log entry or a configuration file — and a tab is refused for the same
    /// reason a wholly blank value is: it is neither a directory name nor an omission. Refusing the whole
    /// control range rather than enumerating the dangerous members is what makes the rule closed.
    /// </para>
    /// <para>
    /// Both halves are asserted: the exact legacy message reaches the caller, and validation returns a
    /// failure rather than throwing. The second half is not redundant — a path-handling routine that
    /// threw on a NUL byte would turn a bad submission into a server fault, and the refusal is performed
    /// by inspection precisely so that it cannot.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// An ordinary relative directory is accepted, including a nested one.
    /// </summary>
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

    /// <summary>
    /// The administrator's given name is required, with the legacy wording.
    /// </summary>
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

    /// <summary>
    /// The administrator's family name is required, with the legacy wording.
    /// </summary>
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

    /// <summary>
    /// The administrator's sign-in name is required, with the legacy wording.
    /// </summary>
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

    /// <summary>
    /// The administrator's address is required, with the legacy wording.
    /// </summary>
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
    /// <para>
    /// MIGRATION: the schema wins over the markup here, and the difference is fifty against a hundred.
    /// The text boxes at <c>signup.ascx:L79</c> and L84 admit a hundred characters, but the terminal
    /// columns are <c>FirstName nvarchar(50) NOT NULL</c> and <c>LastName nvarchar(50) NOT NULL</c>,
    /// from the <c>Tmp_Users</c> definition at
    /// <c>01.00.06.SqlDataProvider:L182-L197</c> - and the legacy class library agrees, decorating both
    /// properties <c>MaxLength(50), Required(True)</c> at
    /// <c>Library/Components/Users/UserInfo.vb:L144</c> and L178. Accepting a hundred would admit a
    /// value the database then truncates or refuses.
    /// </para>
    /// <para>
    /// The terminal width has to be derived correctly, and the obvious shortcut is unsound. Searching
    /// the eighty-eight upgrade scripts for an <c>ALTER COLUMN</c> touching either name returns
    /// nothing, but that proves nothing on its own: this schema evolves several tables by the
    /// drop-and-recreate idiom instead - build <c>Tmp_&lt;Table&gt;</c>, copy the rows, drop the
    /// original, then <c>sp_rename</c> the temporary into place, confirmed at
    /// <c>01.00.06.SqlDataProvider:L230</c>. The same empty search would derive twenty characters for
    /// the address fields, which were in fact widened to fifty by exactly that idiom. The sound rule is
    /// therefore: take the LAST <c>Tmp_&lt;Table&gt;</c> recreate, then apply any later
    /// <c>ALTER COLUMN</c> on top. Applied to these two columns it yields fifty, so the shortcut
    /// happens to reach the right answer here for the wrong reason.
    /// </para>
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

    /// <summary>
    /// The sign-in name is bounded at a hundred, where the markup and the schema agree.
    /// </summary>
    /// <param name="length">The submitted name length.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    /// <remarks>
    /// The column is <c>Username nvarchar(100) NOT NULL</c>, introduced by the second <c>Tmp_Users</c>
    /// rebuild, and the text box at <c>signup.ascx:L89</c> declares the same figure. This width is
    /// genuinely terminal: the only later statement touching the column adds its uniqueness
    /// constraint.
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
    /// The address is bounded at the terminal column width of 256, not at the markup's hundred, and
    /// the over-length case reports the shared malformed-address wording rather than a length message.
    /// </summary>
    /// <param name="length">The total submitted address length.</param>
    /// <param name="expectedToBeAccepted">Whether that length is expected to survive validation.</param>
    /// <remarks>
    /// MIGRATION: the hundred on the text box at <c>signup.ascx:L106</c> is a presentation limit on one
    /// screen rather than a storage constraint, and the column it once matched no longer exists: the
    /// original <c>Email nvarchar(100) NOT NULL</c> was dropped at
    /// <c>02.02.01.SqlDataProvider:L51</c> and re-added as <c>Email nvarchar(256) NULL</c> at
    /// <c>03.00.13.SqlDataProvider:L110</c>. A caller that bypassed the screen was never held to the
    /// hundred, so enforcing it now would refuse a value the legacy system stored.
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

    /// <summary>
    /// A malformed address is refused with the shared wording, and a well-formed one is accepted.
    /// </summary>
    /// <param name="email">The submitted address.</param>
    /// <param name="expectedToBeAccepted">Whether the address is expected to survive validation.</param>
    /// <remarks>
    /// <para>
    /// MIGRATION: this shape rule is net-new on this path and its absence was a gap rather than a
    /// faithful omission. <c>signup.ascx:L106-L107</c> declares a required-field validator over the box
    /// and no expression validator at all, so a malformed address passed the screen and went on to
    /// become the new administrator's contact address - on a portal that by definition has no other
    /// account able to administer it. The rule delegates to the single domain authority rather than
    /// restating a pattern, so this path and the account-creation path cannot disagree about what an
    /// address is. The wording is the shared entry, because the screen carried none of its own for a
    /// condition it never reported.
    /// </para>
    /// <para>
    /// Two consequences of that shared authority are asserted here deliberately. A final domain label
    /// of five or more letters is accepted, which the legacy pattern's four-letter ceiling refused -
    /// a documented loosening. And a local part beginning with a character the legacy pattern's
    /// leading word boundary rejected is still rejected, which is a preserved legacy defect: widening
    /// it would accept addresses the legacy application refused.
    /// </para>
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

    /// <summary>
    /// The administrator's credential is required, with the legacy wording.
    /// </summary>
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
    /// A credential shorter than the configured minimum is refused with the legacy policy wording,
    /// both of whose bracketed tokens are resolved from the bound policy.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the screen itself declared only a required-field validator over this box, so
    /// requiredness is the screen's contribution and keeps the screen's wording. The policy lived
    /// further down, in the membership path, and under the shipped configuration only its minimum
    /// length could ever fire. Concentrating it here is what stops a policy failure surfacing as an
    /// unhandled fault instead of a message naming the field, because the credential reaches a hasher
    /// that refuses the same values unconditionally.
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

    /// <summary>
    /// Neither bracketed token survives into a reported message.
    /// </summary>
    /// <remarks>
    /// The legacy membership path rewrote both tokens at run time from the same two configured
    /// numbers. A message that still carried a bracketed token would mean the substitution had been
    /// lost, which no wording assertion elsewhere would necessarily catch.
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

    /// <summary>
    /// No upper bound is imposed on the credential at the width the legacy text box declared.
    /// </summary>
    /// <param name="length">The submitted credential length.</param>
    /// <remarks>
    /// MIGRATION: the markup capped the two credential boxes at twenty characters
    /// (<c>signup.ascx:L94</c> and L100), matching the plaintext column of the baseline schema, which
    /// the second table rebuild then widened. That ceiling is deliberately not reproduced, because the
    /// column no longer holds the submitted value at all - the Infrastructure layer replaces it with a
    /// fixed-width one-way hash - so the storage limit that justified it has gone, and enforcing it now
    /// would cap credential strength for no remaining reason. This test exists so that nobody restores
    /// it believing they are restoring fidelity.
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

    /// <summary>
    /// The credential is nonetheless bounded, by a net-new ceiling expressed in encoded bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this ceiling has no legacy counterpart and is not a policy rule. The field feeds a
    /// deliberately expensive one-way function, so an unbounded value would let a caller choose how
    /// much work the server performs. Because the wording is net-new rather than legacy, it is
    /// asserted through the shared constant that declares it rather than transcribed here: the four
    /// credential boundaries in this solution read that one declaration, and a transcription would be
    /// the very drift the shared constant exists to prevent. The legacy-derived messages elsewhere in
    /// this file are transcribed independently, which is where independent transcription earns its
    /// keep.
    /// </para>
    /// <para>
    /// The unit is encoded bytes rather than characters because that is what the algorithm consumes.
    /// At more than twelve times the legacy input width it cannot refuse a credential the legacy
    /// screen accepted.
    /// </para>
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

    /// <summary>
    /// A multi-byte character counts as the bytes it occupies, not as one character.
    /// </summary>
    /// <remarks>
    /// A credential of well under the ceiling in characters can exceed it in bytes, and the rule is
    /// only meaningful if it measures what the algorithm consumes. This case would pass a
    /// character-counted ceiling and must not pass this one.
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
    /// MIGRATION: a configured minimum of zero skips the rule entirely rather than registering a
    /// comparison that can never fail, because a rule that cannot fail is indistinguishable from
    /// enforcement under review. The shipped configuration is exactly that case
    /// (<c>Website/release.config:L243</c>), so both halves have to be asserted: the shipped policy
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

    /// <summary>
    /// A configured strength pattern is enforced, and an unconfigured one registers no rule.
    /// </summary>
    /// <remarks>
    /// MIGRATION: an unconfigured pattern skips the rule rather than compiling an empty one, because an
    /// empty pattern matches every input and would be a silent no-op wearing the appearance of
    /// enforcement. The shipped configuration files declare no pattern at all, which is why the
    /// unconfigured half of this test is the shipped behaviour.
    /// </remarks>
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
    /// <para>
    /// MIGRATION: the eighth validator on the screen guarded a second credential box that was never one
    /// of the fifteen positional arguments - the call site at <c>Signup.ascx.vb:L274</c> passes the
    /// credential once - was never persisted, and is absent from the request contract by design, so
    /// there is no property for a rule to bind to. The equality check the code-behind ran at
    /// <c>Signup.ascx.vb:L220-L222</c> moves to the browser form, where both values exist; sending a
    /// credential twice over the wire would widen its exposure without adding any safety. Note that the
    /// check was server-side to begin with - the screen declares no comparison validator - so this is a
    /// change of mechanism and of location rather than a change of rule. The divergence is that this
    /// contract cannot report a mismatch at all.
    /// </para>
    /// <para>
    /// MIGRATION: the wording such a rule would have used is genuinely ambiguous in the legacy sources,
    /// so all three measured strings are asserted absent rather than one being quietly adopted. The
    /// screen's own local entry is the one this flow actually rendered; the shared file additionally
    /// carries a duplicate pair under one logical name, and only the first of the three ends without a
    /// full stop. An executing agent that later adds a mismatch rule must choose deliberately between
    /// them, and this test is what will tell them a choice is being made.
    /// </para>
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
    /// Neither of the site-settings comparison validators has a counterpart on the creation contract,
    /// and no rule here can emit either wording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: both are <c>Operator="DataTypeCheck"</c> validators over fields the creation call
    /// never carried - an expiry date at <c>sitesettings.ascx:L433</c> and a hosting fee at L444 - so
    /// they belong to the portal UPDATE surface rather than to this one. They are asserted absent here
    /// so that the comparison family is demonstrably covered rather than merely unmentioned: the
    /// measured census across the five in-scope admin directories puts comparison validators at
    /// nineteen against sixteen required-field validators, making them the largest family in the
    /// surface being migrated.
    /// </para>
    /// <para>
    /// MIGRATION: the fee validator has NO lower-bound companion. The clamping that exists in the
    /// legacy system is service behaviour on a role rather than validation on a portal -
    /// <c>Library/Components/Portal/PortalController.vb:L395</c> and L398 coerce a negative fee to zero
    /// on a role instance created at L390 while a template installs its roles - and the quota boxes on
    /// the same screen declare no validator at all. Role fees, by contrast, DO carry lower-bound
    /// validators on their own screen. That asymmetry is reproduced rather than harmonised: converting
    /// a silent coercion into a rejected request would be a behavioural change, and tidying the portal
    /// path toward the role path would import a rule the portal screen never had.
    /// </para>
    /// <para>
    /// MIGRATION: a type check is where the legacy relied on loose coercion, because the admin
    /// code-behinds compiled with strict typing disabled (<c>Website/release.config:L125</c>) while the
    /// class library did not. In C# an empty string parses to neither a number nor a date, so a blank
    /// optional numeric or date field has to be modelled as nullable rather than turned into a
    /// validation failure. This contract carries no numeric or date member at all, so no such coercion
    /// happens at this boundary - which is the cleanest available resolution of the asymmetry, and the
    /// reason the culture test further down has no date or currency input to pin.
    /// </para>
    /// </remarks>
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
    /// One submission reports every failing field, not merely the first, reproducing the legacy
    /// screen's accumulating message.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy code-behind appended each problem to one string with "&amp;=" and rendered
    /// the whole of it, so a user saw every problem at once. That behaviour maps onto a validation
    /// result carrying one failure per rule and onto the per-field errors member of a problem document.
    /// Seven failures are expected here and the count is asserted exactly, so that a future rule which
    /// fired twice for one problem, or a cascade change that suppressed a field, would be caught rather
    /// than absorbed.
    /// </remarks>
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

    /// <summary>
    /// The verdict and the legacy wording do not depend on the server's regional settings.
    /// </summary>
    /// <param name="cultureName">The culture to pin for the duration of the assertions.</param>
    /// <remarks>
    /// <para>
    /// A suite that passed under one machine's locale and failed under another's would be a defect, so
    /// the culture is pinned explicitly rather than inherited. The Turkish case is the one that earns
    /// this test: a culture-sensitive lower-casing maps a capital I to a dotless character that the
    /// permitted alias set does not contain, so an upper-case alias the legacy screen accepted would be
    /// refused. The validator lowers invariantly, and this proves it.
    /// </para>
    /// <para>
    /// Only wording the validator supplies explicitly is asserted here. The two length rules take their
    /// wording from the validation framework, which translates it according to the ambient interface
    /// culture, so asserting those strings under a pinned culture would test the framework's
    /// translations rather than this migration's parity.
    /// </para>
    /// </remarks>
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
    /// Builds the password policy the legacy application shipped, which is the options type's own
    /// default state.
    /// </summary>
    /// <returns>The shipped policy.</returns>
    private static PasswordPolicyOptions ShippedPolicy() => new();

    /// <summary>
    /// Builds a policy stricter than the shipped one, used to prove that the credential thresholds and
    /// the numbers in the credential message are read from configuration rather than hard-coded.
    /// </summary>
    /// <returns>The stricter policy.</returns>
    private static PasswordPolicyOptions StricterPolicy() => new()
    {
        MinRequiredPasswordLength = 10,
        MinRequiredNonAlphanumericCharacters = 2,
    };

    /// <summary>
    /// Builds a validator bound to a specific policy.
    /// </summary>
    /// <param name="policy">The policy to bind.</param>
    /// <returns>The validator.</returns>
    private static CreatePortalRequestValidator ValidatorFor(PasswordPolicyOptions policy) =>
        new(policy);

    /// <summary>
    /// Builds a request that satisfies every measured rule, so that each test can violate exactly one
    /// field and a failure names the rule that broke.
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

    /// <summary>
    /// Builds an otherwise valid request carrying a specific credential.
    /// </summary>
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
    /// Validates every probe this suite uses to prove a rule's absence, and returns every message any
    /// of them produced.
    /// </summary>
    /// <returns>The messages.</returns>
    /// <remarks>
    /// Proving that no rule can emit a wording needs more than one request: a valid one reports nothing,
    /// so the probes deliberately include a wholly blank request, one that violates every shape rule at
    /// once, and one that violates every length rule at once. Together they exercise every rule the
    /// validator declares, which is what makes the absence assertions meaningful rather than vacuous.
    /// </remarks>
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
    /// Asserts that a result reports exactly one failure carrying both the expected property name and
    /// the expected wording.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <param name="propertyName">The property the failure must name.</param>
    /// <param name="expectedMessage">The wording the failure must carry, character for character.</param>
    /// <remarks>
    /// Both fields are asserted together on purpose. The pair is what the Api layer projects into the
    /// per-field errors member of a problem document, so a test that checked only the wording would pass
    /// even if the failure were attributed to the wrong field, and one that checked only the property
    /// would pass even if the legacy wording had been rewritten.
    /// </remarks>
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
    /// Asserts that a result reports exactly one length failure against a property, and that the
    /// failure names the configured bound.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <param name="propertyName">The property the failure must name.</param>
    /// <param name="maximumLength">The bound the failure must name.</param>
    /// <remarks>
    /// The two length rules are the only rules in the validator whose wording it does not supply
    /// itself: the validation framework generates it and translates it according to the ambient
    /// interface culture. Transcribing that generated English text here would pin a translation table
    /// rather than this migration's parity, so the property, the singleness of the failure and the
    /// bound named in the message are asserted instead - the bound being the part that carries the
    /// migration decision. Every rule whose wording comes from a legacy resource is asserted character
    /// for character by <see cref="ShouldReport(ValidationResult, string, string)"/>.
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

    /// <summary>
    /// Returns every message a result reported.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <returns>The messages.</returns>
    private static IReadOnlyList<string> Messages(ValidationResult result) =>
        result.Errors.Select(failure => failure.ErrorMessage).ToList();

    /// <summary>
    /// Returns the failures a result reported against one property.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <param name="propertyName">The property to filter on.</param>
    /// <returns>The failures.</returns>
    private static IReadOnlyList<ValidationFailure> Failures(
        ValidationResult result,
        string propertyName) =>
        result.Errors
            .Where(failure => string.Equals(failure.PropertyName, propertyName, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Renders a result as a reason for an assertion that expected the request to be accepted.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <returns>The rendered reason.</returns>
    private static string Describe(ValidationResult result) =>
        "the request should have been accepted but reported: " + Rendered(result);

    /// <summary>
    /// Renders every failure a result reported as property-and-message pairs.
    /// </summary>
    /// <param name="result">The validation result.</param>
    /// <returns>The rendered failures.</returns>
    private static string Rendered(ValidationResult result) =>
        result.Errors.Count == 0
            ? "nothing"
            : string.Join(
                " | ",
                result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));
}
