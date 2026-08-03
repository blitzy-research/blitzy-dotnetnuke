using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: the evidence for every rule below is Website/admin/Tabs/managetabs.ascx, the screen the
// legacy product used to edit a page, together with the terminal column widths the eighty-eight upgrade
// scripts leave behind. The screen declares exactly ONE required-field validator and FIVE maximum lengths,
// and both facts are load-bearing: the presence rule is the one the legacy system enforced, and the
// absence of any other presence rule is why none is invented here.
//
//   L34 txtTabName        MaxLength="50"
//   L36-L37 valTabName    RequiredFieldValidator over txtTabName. The inline default message reads
//                         "Tab Name Is Required"; the resource file is authoritative and reads
//                         "Page Name Is Required"
//                         (Website/admin/Tabs/App_LocalResources/ManageTabs.ascx.resx, valTabName.ErrorMessage)
//   L45 txtTitle          MaxLength="200"
//   L54 txtDescription    MaxLength="500"
//   L63 txtKeyWords       MaxLength="500"
//   L251 txtPageHeadText  MaxLength="500"
//
// MIGRATION: THE EMPTY NAME IS REFUSED, and this is a CORRECTION of the target rather than a change to the
// legacy behaviour. An asp:RequiredFieldValidator fails on an empty or whitespace-only value - that is what
// "required" means in Web Forms - so the legacy screen never let a blank page name reach the controller. The
// target previously accepted it: the service threw only on a NULL name and stored an empty string as given,
// on the reasoning that an empty string was "the legacy no-text value arriving explicitly". That reasoning
// was wrong, because there was no way to submit it. A page with no name cannot be selected in a navigation
// menu or a page list, so accepting one produces a page an operator can create and then not find.
//
// MIGRATION: four widths have NO counterpart in the markup and are taken from the schema alone, because the
// legacy screen edited them through pickers and drop-downs rather than free-text boxes - so the column bound
// was the only bound there was, and it is the only bound imposed here: IconFile 100, Url 255, SkinSrc 200 and
// ContainerSrc 200. TabPath is 255 in the schema and is deliberately NOT validated, because the update
// contract does not carry it: it is composed by the service from the ancestry, so a rule here would judge a
// value no caller can submit.
//
// MIGRATION: NO RULE IS PLACED ON ParentId, AND THAT IS A CORRECTNESS REQUIREMENT RATHER THAN AN OMISSION.
// Tabs.TabID is IDENTITY(0,1), so ZERO IS A LEGITIMATE PAGE and a NotEmpty rule - which rejects default(int)
// - would make the first page of every portal unusable as a parent. The real parent questions are all
// stateful anyway: does the page exist, does it belong to the same portal, and would the move create a cycle.
// Those belong to Application/Services/TabService.cs and are already answered there.
//
// MIGRATION: NO ORDERING RULE BETWEEN THE TWO DATES IS DECLARED. Measured: managetabs.ascx L273 and L285
// declare the start and end boxes with MaxLength="11" - a rendered width for a typed date, not a rule about
// the value - and the screen declares no ControlToCompare anywhere, so an end date preceding a start date was
// storable and produced a page that could never be visible. That is a legacy defect, recorded and deliberately
// NOT corrected, because the minimal-change discipline requires validation rules to MATCH rather than improve
// on the original. The same restraint applies to the refresh interval and to every boolean flag: none carried
// a validator, and none is given one.
//
// MIGRATION: THREE RULES BELOW ARE NOT MEASURED FROM THE SCREEN, and each is here for a reason the
// minimal-change discipline admits rather than in spite of it. A security review of this surface found the
// page update accepting an icon reference that escapes the portal's folder, a link target carrying control
// characters, and a date the stored column cannot represent. None of the three was submittable through the
// legacy screen at all - a picker cannot produce a traversal, a single-line text box cannot produce a
// carriage return, and no date box could type the year one - so refusing them changes no outcome an operator
// could previously produce. The date bound in particular changes only the SHAPE of an existing refusal: such
// a value was already rejected, by the provider, as a server fault naming no field.
//
// MIGRATION: TWO FURTHER RULES WERE CONSIDERED AND DELIBERATELY NOT ADOPTED, because they would change what
// the API accepts rather than where it refuses it. An ordering rule between the two dates was rejected for
// the reason recorded above - the screen declares no ControlToCompare, so a reversed pair was storable - and
// a ceiling on the refresh interval was rejected because no measurement supports one; the column is an
// integer and the screen placed no validator on the field. Both are recorded in MIGRATION_NOTES.md as legacy
// behaviour preserved rather than as omissions.

// MIGRATION: the reserved-device-name rule is NOT duplicated here. It is a genuine rule, and it lives in the
// service, which reports it as a declared failure code. Restating it as a shape rule would put the same
// decision in two places that could disagree, and the service's version is the one with a reason code the
// contract declares.

/// <summary>
/// Validates the shape of a page update submitted to <c>PUT /api/v1/tabs/{tabId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Rule-level cascade stops at the first failure for a member, so a member never reports two complaints;
/// class-level cascade continues, so every offending member is reported in one response rather than one per
/// round trip. That is the convention every validator in this project follows.
/// </para>
/// <para>
/// WHY THE WIDTHS MATTER AS MUCH AS THE PRESENCE RULE. Without them an overlong value travels the whole way
/// to SQL Server and is refused there, which surfaces as a 500 - or, on a legacy connection configured to
/// truncate, as a silently shortened value. Neither tells the caller which field was wrong. Judged here, the
/// same submission produces an RFC 7807 validation response naming the member and its limit.
/// </para>
/// <para>
/// Stateful rules - whether the page exists, whether the parent belongs to the same portal, whether a move
/// would create a cycle, and whether the name is a reserved device name - belong to
/// <c>Application/Services/TabService.cs</c> and are not restated here.
/// </para>
/// </remarks>
public class UpdateTabRequestValidator : AbstractValidator<UpdateTabRequest>
{
    /// <summary>
    /// The wording the legacy resource file declares for the missing-name refusal.
    /// </summary>
    /// <remarks>
    /// Taken from <c>valTabName.ErrorMessage</c> rather than from the markup's inline default, because the
    /// resource file is what the running application rendered. The leading line break the resource carries is
    /// dropped: it was Web Forms layout, not part of the sentence.
    /// </remarks>
    private const string TabNameRequiredMessage = "Page Name Is Required";

    /// <summary>
    /// The wording reported when a link target carries a control character.
    /// </summary>
    /// <remarks>
    /// Net-new, because the legacy screen had no such rule and needed none: a single-line text box cannot
    /// submit a carriage return. It is stated as a shape rule because the stored value is later emitted by
    /// consumers this validator cannot see, and a line break in it is the vehicle for splitting whatever it
    /// is emitted into.
    /// </remarks>
    private const string UrlNotSingleLineMessage =
        "The link URL must be a single line and must not contain control characters.";

    /// <summary>
    /// The wording reported when a submitted date falls outside the range the column can hold.
    /// </summary>
    /// <remarks>
    /// Net-new wording for a refusal that already happened one layer lower, where it named no field.
    /// </remarks>
    private static readonly string DateUnrepresentableMessage = FormattableString.Invariant($"A date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    // Measured maxima. The first five are declared BOTH in the markup and in the terminal schema and agree
    // in every case; the last four exist only in the schema, for the reason recorded in the file header.
    private const int TabNameMaximumLength = 50;
    private const int TitleMaximumLength = 200;
    private const int DescriptionMaximumLength = 500;
    private const int KeywordsMaximumLength = 500;
    private const int PageHeadTextMaximumLength = 500;
    private const int IconFileMaximumLength = 100;
    private const int UrlMaximumLength = 255;
    private const int SkinSrcMaximumLength = 200;
    private const int ContainerSrcMaximumLength = 200;

    /// <summary>
    /// Declares the rule set.
    /// </summary>
    public UpdateTabRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // The one presence rule the legacy screen declared. NotEmpty rather than NotNull, because a
        // required-field validator refused an empty and a whitespace-only value as well as an absent one -
        // and because the member is declared non-nullable on the contract, so a null arriving from
        // deserialisation is the same submission as a blank from the caller's point of view. The service
        // keeps its own null guard as defence in depth for callers that bypass the pipeline.
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
        // accepts an icon reference applies to it. A "fileid=NNN" token and an ordinary file name both
        // satisfy it unchanged; a parent-directory segment, a leading separator and a volume or scheme
        // separator do not.
        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength)
            .Must(IconReferenceRules.IsContained)
            .WithMessage(IconReferenceRules.NotContainedMessage);

        // The link target is bounded by its column width and by being a single line, and by NOTHING else.
        // No format, scheme or containment rule is applied, and that restraint is deliberate: the legacy
        // form applied no format validation, and the value legitimately takes three unrelated shapes - an
        // absolute URL, a "fileid=NNN" token and a numeric page reference - so a containment rule of the kind
        // the icon reference carries would refuse every absolute URL outright.
        RuleFor(request => request.Url)
            .MaximumLength(UrlMaximumLength)
            .Must(BeASingleLine)
            .WithMessage(UrlNotSingleLineMessage);

        RuleFor(request => request.SkinSrc)
            .MaximumLength(SkinSrcMaximumLength);

        RuleFor(request => request.ContainerSrc)
            .MaximumLength(ContainerSrcMaximumLength);

        // REPRESENTABILITY, NOT A BUSINESS RULE, and the distinction is the whole justification. The CLR
        // date type begins nearly eight centuries before the column does, so a date the type accepts can
        // still be unstorable; unbounded, it passed every rule here and was refused by the provider instead,
        // which surfaces as a server fault naming no field. The upper bound is stated at the last
        // representable instant rather than at that day's midnight so that the preserved perpetual value of
        // 9999-12-31 is admitted by the rule meant to protect it. The two are still NOT compared to one
        // another, for the reason recorded in the header.
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
}
