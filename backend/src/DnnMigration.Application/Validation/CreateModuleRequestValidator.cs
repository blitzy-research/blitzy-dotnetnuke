using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>Validates the shape of a module creation submitted to <c>POST /api/v1/modules</c>.</summary>
/// <remarks>
/// <para>
/// Two of the four have no counterpart at all. The border check has no member to attach to, the creation
/// contract having excluded the rendering surface; its message promised a range of nought to nine that the
/// validator never actually enforced, the single-digit effect having come from the text box's
/// <c>MaxLength</c> of one rather than from any rule.
/// </para>
/// <para>
/// NO IDENTIFIER CARRIES A NUMERIC BOUND. The page and module identities both seed at zero and the portal
/// identity seeds at minus one, which is simultaneously the legacy sentinel for an absent integer, so a
/// numeric floor would refuse rows that genuinely exist. Presence is carried by the members being
/// non-nullable; existence is a question for the module service against the store.
/// </para>
/// </remarks>
public class CreateModuleRequestValidator : AbstractValidator<CreateModuleRequest>
{
    /// <summary>
    /// Message reported when a submitted date falls outside the range the terminal <c>datetime</c> column
    /// can hold.
    /// </summary>
    private static readonly string DateUnrepresentableMessage = FormattableString.Invariant($"A date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    /// <summary>
    /// Names the three permitted display states rather than their persisted numbers, so the message stays
    /// readable if the enumeration is ever extended.
    /// </summary>
    private const string VisibilityInvalidMessage = "Visibility must be Maximized, Minimized or None.";

    /// <summary>
    /// The refusal shown for a negative cache period. Worded from the legacy resource entry
    /// <c>valCacheTime.ErrorMessage</c> so that one rule is not described two ways.
    /// </summary>
    private const string CacheTimeNegativeMessage = "Invalid Cache Time";

    /// <summary>
    /// The measured bound on <c>Modules.ModuleTitle</c> (<c>nvarchar(256) NULL</c>). The legacy text box
    /// declared a rendered width but no maximum length, so the column is the sole authority.
    /// </summary>
    private const int ModuleTitleMaximumLength = 256;

    /// <summary>
    /// The measured bound on <c>TabModules.IconFile</c> (<c>nvarchar(100) NULL</c>). A stored name or path
    /// only: the legacy control declared no validator, and nothing here resolves a location.
    /// </summary>
    private const int IconFileMaximumLength = 100;

    /// <summary>
    /// Declares the rule set. Parameterless by design: a shape check needs no collaborator, and the
    /// assembly-wide scan that registers every validator in this folder constructs it with none.
    /// </summary>
    public CreateModuleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(request => request.ModuleTitle)
            .MaximumLength(ModuleTitleMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage)
            .When(request => !string.IsNullOrEmpty(request.ModuleTitle));

        // Same treatment, same reasoning, against the measured icon column width.
        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength)
            .When(request => !string.IsNullOrEmpty(request.IconFile));

        RuleFor(request => request.IconFile)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage)
            .Must(IconReferenceRules.IsContained)
            .WithMessage(IconReferenceRules.NotContainedMessage);

        // Membership of the enumeration is the whole constraint. The persisted numbers are 0, 1 and 2,
        // and they live on the named members rather than in a range written here.
        RuleFor(request => request.Visibility)
            .IsInEnum().WithMessage(VisibilityInvalidMessage);

        RuleFor(request => request.StartDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage);

        RuleFor(request => request.EndDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage);


        // ⚠ MIGRATION - A CACHE PERIOD MAY NOT BE NEGATIVE, WHICH THE LEGACY RULE ADMITTED.
        // `modulesettings.ascx:L172` declares exactly one validator on this box, a `CompareValidator` with
        // `Operator="DataTypeCheck" Type="Integer"`, and the code-behind stored whatever parsed - so `-1`
        // was accepted and written. That is an omission rather than a decision: `plCacheTime.Help` calls the
        // value "the time this object is kept in the Cache", and a duration cannot run backwards. The same
        // bound is applied on the client, so the two agree. NO UPPER BOUND IS DECLARED: neither the legacy
        // validator nor the `int` column states one, and a long cache period is an unusual choice rather
        // than an error. Recorded in MIGRATION_NOTES.md.
        RuleFor(request => request.CacheTime)
            .GreaterThanOrEqualTo(0).WithMessage(CacheTimeNegativeMessage);
    }
}
