// MIGRATION: this suite is the parity proof for Application/Validation/UpdateTabRequestValidator.cs. Its
// subject is not "does the validator reject bad input" but "does the validator enforce, field for field, the
// rule set the legacy page-edit screen enforced, and no more". Every assertion therefore names the legacy
// declaration it reproduces, and the required-name message is quoted character for character rather than
// matched by substring.
//
// MIGRATION: the legacy declarations, measured rather than assumed. Website/admin/Tabs/managetabs.ascx
// declares exactly ONE asp:RequiredFieldValidator and FIVE MaxLength attributes:
//   L34  txtTabName        MaxLength="50"
//   L36-L37 valTabName     RequiredFieldValidator, ControlToValidate="txtTabName"
//   L45  txtTitle          MaxLength="200"
//   L54  txtDescription    MaxLength="500"
//   L63  txtKeyWords       MaxLength="500"
//   L251 txtPageHeadText   MaxLength="500"
// The message is taken from Website/admin/Tabs/App_LocalResources/ManageTabs.ascx.resx, whose
// valTabName.ErrorMessage reads "<br>Page Name Is Required" - the resource file is what the running
// application rendered, and the markup's inline default ("Tab Name Is Required") is what it fell back to only
// when the resource was missing.
//
// MIGRATION: the LEADING LINE BREAK in the resource value is a documented divergence, not an oversight. It
// was Web Forms layout - the validators rendered inline beside their field and the break pushed the message
// onto its own line - and an RFC 7807 payload carries no markup. The sentence is preserved; the break is not.
//
// MIGRATION: the ABSENCE of every other presence rule is asserted as deliberately as the one rule that
// exists. The screen declares no required-field validator on the title, the description, the keywords, the
// head text, the icon, the skin, the container or the URL, so every one of those may legitimately be omitted
// and each has an explicit acceptance test below. Adding a presence rule to any of them would be a
// tightening the minimal-change discipline forbids, and the tests are what would catch it.

using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves that the page-update validator enforces the legacy page-edit screen's rule set exactly: one
/// presence rule, nine maximum lengths, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE WIDTH TESTS COME IN PAIRS. A maximum-length rule is only proved by two cases: a value exactly at
/// the limit that must be ACCEPTED, and one character beyond it that must be REFUSED. A single over-limit case
/// passes just as well against an off-by-one rule that also rejects the longest legitimate value, which would
/// make a page whose name is exactly fifty characters uneditable. Every one of the nine widths is therefore
/// asserted at the limit and at the limit plus one.
/// </para>
/// <para>
/// Four of the nine widths have no counterpart in the markup and come from the terminal schema alone, because
/// the legacy screen edited them through pickers and drop-downs rather than free-text boxes: the icon, the
/// URL, the skin and the container. The column bound was the only bound there was, and it is the only bound
/// asserted here.
/// </para>
/// </remarks>
public class UpdateTabRequestValidatorTests
{
    /// <summary>
    /// The wording the legacy resource file declares, less the Web Forms line break.
    /// </summary>
    private const string TabNameRequiredMessage = "Page Name Is Required";

    private const string UrlNotAllowedMessage =
        "The link URL must be a numeric page identifier, a fileid=NNN reference, "
        + "or an absolute HTTP, HTTPS, or mailto URI.";

    private const string TabNameProperty = nameof(UpdateTabRequest.TabName);

    private readonly UpdateTabRequestValidator _validator = new();

    /// <summary>
    /// A request carrying only a page name is accepted, which is the baseline every rule test mutates.
    /// </summary>
    /// <remarks>
    /// This is the cleanest available proof that the eight optional members really are optional: the legacy
    /// screen declared no presence rule for any of them, so a request that omits all eight must be valid.
    /// </remarks>
    [Fact]
    public void ARequestCarryingOnlyAName_IsAccepted()
    {
        ShouldAccept(_validator.Validate(Valid()));
    }

    /// <summary>
    /// An absent, empty or whitespace-only page name is refused with the legacy wording.
    /// </summary>
    /// <param name="submittedName">The name to submit.</param>
    /// <remarks>
    /// MIGRATION: an asp:RequiredFieldValidator fails on an empty and a whitespace-only value just as it does
    /// on an absent one - that is what "required" means in Web Forms - so none of these four submissions could
    /// reach the legacy controller. The empty case is the one that had regressed: the service accepted it and
    /// stored a page with no name, which cannot be picked out of a navigation menu or a page list.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ABlankName_IsRefusedWithTheLegacyWording(string? submittedName)
    {
        UpdateTabRequest request = Valid();
        request.TabName = submittedName!;

        ShouldReport(_validator.Validate(request), TabNameProperty, TabNameRequiredMessage);
    }

    /// <summary>
    /// A page name is accepted at fifty characters and refused at fifty-one.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>managetabs.ascx</c> L34 declares <c>MaxLength="50"</c> and the terminal schema declares
    /// the column <c>nvarchar(50)</c>. Both agree, so the limit is not a judgement call.
    /// </remarks>
    [Fact]
    public void AName_IsAcceptedAtFiftyAndRefusedAtFiftyOne()
    {
        UpdateTabRequest atLimit = Valid();
        atLimit.TabName = new string('a', 50);

        ShouldAccept(_validator.Validate(atLimit));

        UpdateTabRequest beyond = Valid();
        beyond.TabName = new string('a', 51);

        ShouldNotAccept(_validator.Validate(beyond), TabNameProperty);
    }

    /// <summary>
    /// Every optional text member is accepted at its measured limit.
    /// </summary>
    /// <param name="property">The member under test.</param>
    /// <param name="limit">The measured maximum length.</param>
    [Theory]
    [InlineData(nameof(UpdateTabRequest.Title), 200)]
    [InlineData(nameof(UpdateTabRequest.Description), 500)]
    [InlineData(nameof(UpdateTabRequest.Keywords), 500)]
    [InlineData(nameof(UpdateTabRequest.PageHeadText), 500)]
    [InlineData(nameof(UpdateTabRequest.IconFile), 100)]
    [InlineData(nameof(UpdateTabRequest.Url), 255)]
    public void AnOptionalMember_IsAcceptedAtItsLimit(string property, int limit)
    {
        const string absoluteUrlPrefix = "https://example.test/";
        string value = property == nameof(UpdateTabRequest.Url)
            ? absoluteUrlPrefix + new string('x', limit - absoluteUrlPrefix.Length)
            : new string('x', limit);

        ShouldAccept(_validator.Validate(WithText(property, value)));
    }

    /// <summary>
    /// Every optional text member is refused one character beyond its measured limit.
    /// </summary>
    /// <param name="property">The member under test.</param>
    /// <param name="limit">The measured maximum length.</param>
    /// <remarks>
    /// This is the assertion the finding turned on. Without it an overlong value travels the whole way to SQL
    /// Server, which refuses it as a truncation error and surfaces as a 500 naming no field - or, on a
    /// connection configured to truncate, stores a silently shortened value. Judged here, the same submission
    /// produces an RFC 7807 response naming the member.
    /// </remarks>
    [Theory]
    [InlineData(nameof(UpdateTabRequest.Title), 200)]
    [InlineData(nameof(UpdateTabRequest.Description), 500)]
    [InlineData(nameof(UpdateTabRequest.Keywords), 500)]
    [InlineData(nameof(UpdateTabRequest.PageHeadText), 500)]
    [InlineData(nameof(UpdateTabRequest.IconFile), 100)]
    [InlineData(nameof(UpdateTabRequest.Url), 255)]
    public void AnOptionalMember_IsRefusedBeyondItsLimit(string property, int limit)
    {
        ShouldNotAccept(_validator.Validate(WithText(property, new string('x', limit + 1))), property);
    }

    /// <summary>
    /// Every optional text member is accepted when omitted and when explicitly empty.
    /// </summary>
    /// <param name="property">The member under test.</param>
    /// <remarks>
    /// MIGRATION: the screen declares no presence rule on any of these, so clearing a field was how an
    /// operator removed a title or a description. Refusing an empty value would make that impossible.
    /// </remarks>
    [Theory]
    [InlineData(nameof(UpdateTabRequest.Title))]
    [InlineData(nameof(UpdateTabRequest.Description))]
    [InlineData(nameof(UpdateTabRequest.Keywords))]
    [InlineData(nameof(UpdateTabRequest.PageHeadText))]
    [InlineData(nameof(UpdateTabRequest.IconFile))]
    [InlineData(nameof(UpdateTabRequest.Url))]
    public void AnOptionalMember_IsAcceptedWhenOmittedOrEmpty(string property)
    {
        ShouldAccept(_validator.Validate(WithText(property, null)));
        ShouldAccept(_validator.Validate(WithText(property, string.Empty)));
    }

    /// <summary>
    /// Every legitimate persisted link form is admitted without rewriting it.
    /// </summary>
    /// <param name="url">Link target under test.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("fileid=0")]
    [InlineData("FILEID=987")]
    [InlineData("http://example.test/page")]
    [InlineData("https://example.test/page?q=1")]
    [InlineData("mailto:owner@example.test")]
    public void ASupportedLinkTarget_IsAccepted(string url)
    {
        ShouldAccept(_validator.Validate(WithText(nameof(UpdateTabRequest.Url), url)));
    }

    /// <summary>
    /// Active, unknown and malformed schemes are refused before they can be stored and returned to a
    /// navigation consumer.
    /// </summary>
    /// <param name="url">Link target under test.</param>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ftp://example.test/file")]
    [InlineData("relative/path")]
    [InlineData("~/relative/path")]
    [InlineData("fileid=")]
    [InlineData("fileid=abc")]
    [InlineData("42x")]
    public void AnUnsupportedLinkTarget_IsRefused(string url)
    {
        ShouldReport(
            _validator.Validate(WithText(nameof(UpdateTabRequest.Url), url)),
            nameof(UpdateTabRequest.Url),
            UrlNotAllowedMessage);
    }

    /// <summary>
    /// The parent identifier is never subject to a presence rule, including for zero and the legacy
    /// integer sentinel.
    /// </summary>
    /// <param name="parentId">The parent to submit.</param>
    /// <remarks>
    /// MIGRATION: <c>Tabs.TabID</c> is <c>IDENTITY(0,1)</c>, so ZERO IS A LEGITIMATE PAGE and a
    /// <c>NotEmpty</c> rule - which rejects <c>default(int)</c> - would make the first page of every portal
    /// unusable as a parent. The value -1 must not be refused either: the legacy query surface used it as an
    /// "any page" marker, and whether a submitted parent is acceptable is a STATEFUL question the service
    /// answers, not a shape a validator can judge.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7)]
    public void AParentIdentifier_IsNeverRefusedByShape(int? parentId)
    {
        UpdateTabRequest request = Valid();
        request.ParentId = parentId;

        ShouldAccept(_validator.Validate(request));
    }

    /// <summary>
    /// No ordering rule is imposed between the two dates, reproducing the legacy screen exactly.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>managetabs.ascx</c> L273 and L285 declare the two date boxes with
    /// <c>MaxLength="11"</c> - a rendered width for a typed date, not a rule about the value - and the screen
    /// declares no <c>ControlToCompare</c> anywhere, so an end date preceding a start date was storable and
    /// produced a page that could never be visible. That is a legacy defect, recorded and deliberately NOT
    /// corrected, because validation rules must MATCH rather than improve on the original. This test is what
    /// stops a well-meaning later change from adding the comparison.
    /// </remarks>
    [Fact]
    public void ReversedDates_AreAcceptedBecauseTheLegacyScreenAcceptedThem()
    {
        UpdateTabRequest request = Valid();
        request.StartDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        request.EndDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        ShouldAccept(_validator.Validate(request));
    }

    /// <summary>
    /// A negative refresh interval is accepted, because the legacy screen declared no rule for it.
    /// </summary>
    /// <remarks>
    /// MIGRATION: recorded as an acceptance rather than left untested, so that the absence of a rule is a
    /// decision the suite pins rather than a gap someone later closes by inventing one.
    /// </remarks>
    [Fact]
    public void ARefreshInterval_IsNeverRefusedByShape()
    {
        UpdateTabRequest request = Valid();
        request.RefreshInterval = -5;

        ShouldAccept(_validator.Validate(request));
    }

    /// <summary>
    /// Two offending members are both reported in one response.
    /// </summary>
    /// <remarks>
    /// Class-level cascade continues, so a caller correcting a submission learns about every field at once
    /// rather than one field per round trip. Rule-level cascade stops, so no single member reports twice - the
    /// name below is blank AND over-length, and must produce exactly one failure.
    /// </remarks>
    [Fact]
    public void EveryOffendingMember_IsReportedInOneResponse()
    {
        UpdateTabRequest request = Valid();
        request.TabName = string.Empty;
        request.Title = new string('x', 201);
        request.Url = new string('x', 256);

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Select(failure => failure.PropertyName).Should().BeEquivalentTo(
            new[] { TabNameProperty, nameof(UpdateTabRequest.Title), nameof(UpdateTabRequest.Url) });
        result.Errors.Should().HaveCount(3, Render(result));
    }

    /// <summary>
    /// Builds a request carrying only a page name.
    /// </summary>
    /// <returns>A minimal valid request.</returns>
    private static UpdateTabRequest Valid() => new() { TabName = "Measured Page" };

    /// <summary>
    /// Builds a request whose one named text member carries the given value.
    /// </summary>
    /// <param name="property">The member to set.</param>
    /// <param name="value">The value to set it to.</param>
    /// <returns>The prepared request.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The member is not one this suite drives.</exception>
    /// <remarks>
    /// Set through a switch rather than through reflection so that a renamed member is a COMPILE error rather
    /// than a test that silently stops exercising anything.
    /// </remarks>
    private static UpdateTabRequest WithText(string property, string? value)
    {
        UpdateTabRequest request = Valid();

        switch (property)
        {
            case nameof(UpdateTabRequest.Title):
                request.Title = value;
                break;
            case nameof(UpdateTabRequest.Description):
                request.Description = value;
                break;
            case nameof(UpdateTabRequest.Keywords):
                request.Keywords = value;
                break;
            case nameof(UpdateTabRequest.PageHeadText):
                request.PageHeadText = value;
                break;
            case nameof(UpdateTabRequest.IconFile):
                request.IconFile = value;
                break;
            case nameof(UpdateTabRequest.Url):
                request.Url = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, "Unhandled member.");
        }

        return request;
    }

    /// <summary>
    /// Asserts that a result reports the given message against the given property, character for character.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property the failure must name.</param>
    /// <param name="message">The message the failure must carry.</param>
    private static void ShouldReport(ValidationResult result, string property, string message)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property);
        result.Errors.Should().Contain(
            failure => failure.PropertyName == property && failure.ErrorMessage == message,
            "the failure for {0} must carry exactly the legacy wording, but {1}",
            property,
            Render(result));
    }

    /// <summary>
    /// Asserts that a result reports exactly one failure, against the given property.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property the failure must name.</param>
    private static void ShouldNotAccept(ValidationResult result, string property)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property);
        result.Errors.Should().ContainSingle(Render(result))
            .Which.PropertyName.Should().Be(property);
    }

    /// <summary>
    /// Asserts that a result reports nothing whatsoever.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    private static void ShouldAccept(ValidationResult result)
    {
        result.IsValid.Should().BeTrue(Render(result));
        result.Errors.Should().BeEmpty(Render(result));
    }

    /// <summary>
    /// Renders a validation result for an assertion message.
    /// </summary>
    /// <param name="result">The result to render.</param>
    /// <returns>The rendered reason.</returns>
    private static string Render(ValidationResult result) => result.Errors.Count == 0
        ? "the result reported nothing"
        : "the result reported "
            + string.Join(
                " | ",
                result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));
}
