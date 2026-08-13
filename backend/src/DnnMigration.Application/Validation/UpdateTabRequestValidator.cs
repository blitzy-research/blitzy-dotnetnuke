using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>Validates the shape of a page update submitted to <c>PUT /api/v1/tabs/{tabId}</c>.</summary>
/// <remarks>
/// Rule-level cascade stops at the first failure for a member, so a member never reports two complaints;
/// class-level cascade continues, so every offending member is reported in one response rather than one per
/// round trip. That is the convention every validator in this project follows.
/// </remarks>
public class UpdateTabRequestValidator : AbstractValidator<UpdateTabRequest>
{
    /// <summary>The wording the legacy resource file declares for the missing-name refusal.</summary>
    /// <remarks>
    /// Taken from <c>valTabName.ErrorMessage</c> rather than from the markup's inline default, because the
    /// resource file is what the running application rendered. The leading line break the resource carries
    /// is dropped: it was Web Forms layout, not part of the sentence.
    /// </remarks>
    private const string TabNameRequiredMessage = "Page Name Is Required";

    /// <summary>The wording reported when a link target carries a control character.</summary>
    private const string UrlNotSingleLineMessage =
        "The link URL must be a single line and must not contain control characters.";

    /// <summary>
    /// The wording reported when a link target is not one of the persisted forms the page system
    /// understands.
    /// </summary>
    private const string UrlNotAllowedMessage =
        "The link URL must be a numeric page identifier, a fileid=NNN reference, "
        + "or an absolute HTTP, HTTPS, or mailto URI.";

    /// <summary>The wording reported when a submitted date falls outside the range the column can hold.</summary>
    /// <remarks>
    /// Net-new wording for a refusal that already happened one layer lower, where it named no field.
    /// </remarks>
    private static readonly string DateUnrepresentableMessage = FormattableString.Invariant($"A date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    // Measured maxima. The first five are declared BOTH in the markup and in the terminal schema and agree
    // in every case; the last two exist only in the schema, for the reason recorded in the file header.
    private const int TabNameMaximumLength = 50;
    private const int TitleMaximumLength = 200;
    private const int DescriptionMaximumLength = 500;
    private const int KeywordsMaximumLength = 500;
    private const int PageHeadTextMaximumLength = 500;
    private const int IconFileMaximumLength = 100;
    private const int UrlMaximumLength = 255;

    /// <summary>Declares the rule set.</summary>
    public UpdateTabRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(request => request.TabName)
            .NotEmpty().WithMessage(TabNameRequiredMessage)
            .MaximumLength(TabNameMaximumLength);

        RuleFor(request => request.Title)
            .MaximumLength(TitleMaximumLength);

        RuleFor(request => request.Description)
            .MaximumLength(DescriptionMaximumLength);

        RuleFor(request => request.Keywords)
            .MaximumLength(KeywordsMaximumLength);

        RuleFor(request => request.PageHeadText)
            .MaximumLength(PageHeadTextMaximumLength);

        // Column width plus the shared containment rule, which is the same rule every other path that
        // accepts an icon reference applies to it.
        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength)
            .Must(IconReferenceRules.IsContained)
            .WithMessage(IconReferenceRules.NotContainedMessage);

        // The link target is a small tagged union, not arbitrary text: a numeric page reference, a
        // fileid=NNN token, or an absolute URI using one of the schemes the page renderer supports.
        RuleFor(request => request.Url)
            .MaximumLength(UrlMaximumLength)
            .Must(BeASingleLine)
            .WithMessage(UrlNotSingleLineMessage)
            .Must(BeAnAllowedLinkTarget)
            .WithMessage(UrlNotAllowedMessage);

        // REPRESENTABILITY, NOT A BUSINESS RULE, and the distinction is the whole justification.
        RuleFor(request => request.StartDate)
            .Must(SqlServerRange.CanStore)
            .WithMessage(DateUnrepresentableMessage);

        RuleFor(request => request.EndDate)
            .Must(SqlServerRange.CanStore)
            .WithMessage(DateUnrepresentableMessage);
    }

    /// <summary>
    /// Determines whether a submitted value occupies a single line, accepting an absent or empty value.
    /// </summary>
    /// <param name="value">The submitted value, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or free of control characters; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Tests the whole Unicode control category rather than just the two line-break characters, so a null
    /// character or an escape sequence is refused on the same footing. The column can store any of them; no
    /// single-line form field could ever have submitted one.
    /// </remarks>
    private static bool BeASingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether a submitted link uses one of the page system's supported persisted forms.
    /// </summary>
    /// <param name="value">The submitted value, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> for an absent value, an ASCII-decimal page identifier, a <c>fileid=NNN</c>
    /// token, or an absolute HTTP, HTTPS or mailto URI; otherwise <see langword="false"/>.
    /// </returns>
    private static bool BeAnAllowedLinkTarget(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (ContainsOnlyAsciiDigits(value))
        {
            return true;
        }

        const string fileReferencePrefix = "fileid=";
        if (value.StartsWith(fileReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ContainsOnlyAsciiDigits(value[fileReferencePrefix.Length..]);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(uri.Host);
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase)
            && value.Length > Uri.UriSchemeMailto.Length + 1;
    }

    /// <summary>Reports whether a non-empty value contains ASCII decimal digits only.</summary>
    /// <param name="value">Value to inspect.</param>
    /// <returns><see langword="true"/> when every character is between <c>0</c> and <c>9</c>.</returns>
    private static bool ContainsOnlyAsciiDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
