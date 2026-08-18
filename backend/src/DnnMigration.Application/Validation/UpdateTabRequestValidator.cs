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

        // THE PAGE NAME IS A ROUTE-GENERATING LABEL, which is why the shared visible-text rule matters more
        // here than on any other member of this contract. The stored name and the stored TabPath are two
        // representations of the same text, but they are not produced the same way: the name is persisted
        // verbatim while the path is produced by stripping every non-word character from it
        // (TabService.StripNonWord, legacy ManageTabs.ascx.vb L272). A name carrying a NUL therefore stores
        // as six characters and generates a five-character path, so the label an operator reads and the
        // route the page answers on no longer represent the same text - and neither value is wrong on its
        // own terms, which is exactly why the divergence cannot be repaired downstream. Refusing the name
        // is the only fix that keeps both correct, and the path generator stays byte-for-byte legacy.
        RuleFor(request => request.TabName)
            .NotEmpty().WithMessage(TabNameRequiredMessage)
            .MaximumLength(TabNameMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        RuleFor(request => request.Title)
            .MaximumLength(TitleMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        // The three metadata members were authored in text areas, so a tab and a line break are content
        // here and only the unreadable remainder is refused.
        RuleFor(request => request.Description)
            .MaximumLength(DescriptionMaximumLength)
            .Must(TextIntegrityRules.IsMultiLineSafe)
            .WithMessage(TextIntegrityRules.MultiLineMessage);

        RuleFor(request => request.Keywords)
            .MaximumLength(KeywordsMaximumLength)
            .Must(TextIntegrityRules.IsMultiLineSafe)
            .WithMessage(TextIntegrityRules.MultiLineMessage);

        RuleFor(request => request.PageHeadText)
            .MaximumLength(PageHeadTextMaximumLength)
            .Must(TextIntegrityRules.IsMultiLineSafe)
            .WithMessage(TextIntegrityRules.MultiLineMessage);

        // Column width, the shared containment rule that every other path accepting an icon reference
        // applies, and the visible-text rule. Containment and legibility are independent questions:
        // IconReferenceRules answers "does this path stay inside the portal folder", which a value carrying
        // a NUL can satisfy while still being a path no operator can retype.
        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage)
            .Must(IconReferenceRules.IsContained)
            .WithMessage(IconReferenceRules.NotContainedMessage);

        // The link target is a small tagged union, not arbitrary text: a numeric page reference, a
        // fileid=NNN token, or an absolute URI using one of the schemes the page renderer supports.
        // The single-line test is the SHARED one, so a bidirectional control in a link target is refused on
        // the same footing as one in the page name. The wording stays this contract's own because a link
        // target is not free text and "retype it using visible characters" would be the wrong instruction
        // for a value that must additionally be one of three fixed forms.
        RuleFor(request => request.Url)
            .MaximumLength(UrlMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
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

    // The private single-line predicate this contract used to carry has been REMOVED rather than kept
    // alongside the shared one. It tested char.IsControl only, so it admitted every zero-width and every
    // bidirectional formatting character, and having two predicates for one question is how the two
    // members of this contract came to disagree about what a single line is in the first place.

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
