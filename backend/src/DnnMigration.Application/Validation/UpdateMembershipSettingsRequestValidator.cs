using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// No validator stood in front of this endpoint at all, and the endpoint nonetheless advertised a validation
// failure.

/// <summary>
/// Declares the field rules for <see cref="UpdateMembershipSettingsRequest"/>, the payload submitted to
/// <c>PUT /api/v1/portals/{portalId}/membership-settings</c>.
/// </summary>
/// <remarks>
/// Four kinds of rule appear: the closed sets behind the three integer discriminators, the page-size bound,
/// the two column widths, and the compilability of the one member whose stored value is later executed as a
/// pattern. Booleans carry no rule - a boolean has no invalid value - and the three page references carry
/// none here, for the reason recorded above.
/// </remarks>
public sealed class UpdateMembershipSettingsRequestValidator
    : AbstractValidator<UpdateMembershipSettingsRequest>
{
    /// <summary>Smallest legal grid page size.</summary>
    public const int MinimumRecordsPerPage = 1;

    /// <summary>
    /// Largest legal grid page size, taken from the ceiling every paged read on this API already enforces.
    /// </summary>
    public const int MaximumRecordsPerPage = PagedRequestValidator.MaximumPageSize;

    /// <summary>Largest legal display mode, the last member of the legacy <c>DisplayMode</c> vocabulary.</summary>
    public const int MaximumDisplayMode = 2;

    /// <summary>
    /// Largest legal profile visibility, the last member of the legacy <c>UserVisibilityMode</c>
    /// vocabulary.
    /// </summary>
    public const int MaximumProfileVisibility = 2;

    /// <summary>
    /// Largest legal users-control choice, the last member of the legacy <c>UsersControl</c> vocabulary.
    /// </summary>
    public const int MaximumUsersControl = 1;

    /// <summary>Largest number of characters either free-text member may carry.</summary>
    /// <remarks>
    /// The width of <c>ModuleSettings.SettingValue</c> in the terminal schema, which is the real limit: a
    /// longer value is refused by the store with a message naming no field, so bounding it here turns a
    /// server fault into a field-level answer. It is NOT a judgement about how long a sensible expression
    /// or display-name format is.
    /// </remarks>
    public const int MaximumSettingValueLength = 2000;

    /// <summary>
    /// The ceiling on how long the submitted electronic-mail expression may take to compile and to match
    /// its probe input before it is judged unusable.
    /// </summary>
    /// <remarks>
    /// Two seconds is generous for compiling any expression an administrator would write by hand, and short
    /// enough that a pathological one is refused rather than occupying the request thread. The same timeout
    /// travels with the compiled instance, so the probe match below cannot run away either.
    /// </remarks>
    public static readonly TimeSpan ExpressionCompilationTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Message reported when the display mode is outside its closed set.</summary>
    public static readonly string DisplayModeOutOfRangeMessage = FormattableString.Invariant(
        $"The display mode must be between 0 and {MaximumDisplayMode}: 0 lists all accounts, 1 groups them behind a first-letter selector, and {MaximumDisplayMode} shows none until a search is performed.");

    /// <summary>Message reported when the default profile visibility is outside its closed set.</summary>
    public static readonly string ProfileVisibilityOutOfRangeMessage = FormattableString.Invariant(
        $"The default profile visibility must be between 0 and {MaximumProfileVisibility}: 0 is visible to all users, 1 to members only, and {MaximumProfileVisibility} to administrators only.");

    /// <summary>Message reported when the users-control choice is outside its closed set.</summary>
    public static readonly string UsersControlOutOfRangeMessage = FormattableString.Invariant(
        $"The accounts display mode must be 0 (a picker listing every account) or {MaximumUsersControl} (a free-text box).");

    /// <summary>Message reported when the grid page size is outside its bounds.</summary>
    public static readonly string RecordsPerPageOutOfRangeMessage = FormattableString.Invariant(
        $"The number of accounts per page must be between {MinimumRecordsPerPage} and {MaximumRecordsPerPage}.");

    /// <summary>Message reported when a free-text setting exceeds the stored column's width.</summary>
    public static readonly string SettingValueTooLongMessage = FormattableString.Invariant(
        $"A membership setting may not exceed {MaximumSettingValueLength} characters.");

    /// <summary>Message reported when the submitted expression is not a usable regular expression.</summary>
    public const string EmailExpressionUnusableMessage =
        "The electronic-mail validation expression could not be compiled as a regular expression, so it "
        + "would refuse every address it was applied to. Submit a valid expression, or omit the member to "
        + "restore the default.";

    /// <summary>Initialises the rule set.</summary>
    public UpdateMembershipSettingsRequestValidator()
    {
        RuleFor(request => request.DisplayMode)
            .InclusiveBetween(0, MaximumDisplayMode)
            .WithMessage(DisplayModeOutOfRangeMessage);

        RuleFor(request => request.ProfileDefaultVisibility)
            .InclusiveBetween(0, MaximumProfileVisibility)
            .WithMessage(ProfileVisibilityOutOfRangeMessage);

        RuleFor(request => request.SecurityUsersControl)
            .InclusiveBetween(0, MaximumUsersControl)
            .WithMessage(UsersControlOutOfRangeMessage);

        RuleFor(request => request.RecordsPerPage)
            .InclusiveBetween(MinimumRecordsPerPage, MaximumRecordsPerPage)
            .WithMessage(RecordsPerPageOutOfRangeMessage);

        // Both free-text members are bounded by the same column, so they carry the same bound.
        RuleFor(request => request.SecurityDisplayNameFormat)
            .NotNull()
            .WithMessage("The display-name format must not be null.")
            .MaximumLength(MaximumSettingValueLength)
            .WithMessage(SettingValueTooLongMessage);

        RuleFor(request => request.SecurityEmailValidation)
            .MaximumLength(MaximumSettingValueLength)
            .WithMessage(SettingValueTooLongMessage)
            .Must(BeAUsableExpression)
            .WithMessage(EmailExpressionUnusableMessage);

        // The three redirect members carry a NON-NEGATIVE bound and nothing stronger. Zero is a legitimate
        // page - Tabs.TabID seeds at zero - so the floor is zero rather than one, and whether the page
        // exists and belongs to the tenant is a stateful question the service answers, not a field rule.
        RuleFor(request => request.RedirectAfterLogin)
            .GreaterThanOrEqualTo(0)
            .When(request => request.RedirectAfterLogin.HasValue)
            .WithMessage("The post-login redirect page identifier must not be negative.");

        RuleFor(request => request.RedirectAfterRegistration)
            .GreaterThanOrEqualTo(0)
            .When(request => request.RedirectAfterRegistration.HasValue)
            .WithMessage("The post-registration redirect page identifier must not be negative.");

        RuleFor(request => request.RedirectAfterLogout)
            .GreaterThanOrEqualTo(0)
            .When(request => request.RedirectAfterLogout.HasValue)
            .WithMessage("The post-logout redirect page identifier must not be negative.");
    }

    /// <summary>Determines whether a submitted pattern can be compiled and applied as a regular expression.</summary>
    /// <param name="pattern">The submitted pattern, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the pattern compiles and matches a probe input inside <see
    /// cref="ExpressionCompilationTimeout"/>; otherwise <see langword="false"/>.
    /// </returns>
    private static bool BeAUsableExpression(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            var expression = new Regex(pattern, RegexOptions.None, ExpressionCompilationTimeout);

            _ = expression.IsMatch(
                string.Create(CultureInfo.InvariantCulture, $"probe.address@example.com"));

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
