namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// The write contract for one portal's membership settings, submitted to <c>PUT
/// /api/v1/portals/{portalId}/membership-settings</c>.
/// </summary>
/// <remarks>
/// Ranges are declared by <c>UpdateMembershipSettingsRequestValidator</c> and, for the three page
/// references, completed by the service - a page identifier can only be judged against the tenant that owns
/// it, and ownership is a data question rather than a field rule.
/// </remarks>
public sealed class UpdateMembershipSettingsRequest
{
    /// <summary>Whether the users grid shows the first-name column. Legacy key <c>Column_FirstName</c>.</summary>
    public bool ColumnFirstName { get; set; }

    /// <summary>Whether the users grid shows the last-name column. Legacy key <c>Column_LastName</c>.</summary>
    public bool ColumnLastName { get; set; }

    /// <summary>Whether the users grid shows the display-name column. Legacy key <c>Column_DisplayName</c>.</summary>
    public bool ColumnDisplayName { get; set; } = true;

    /// <summary>Whether the users grid shows the address column. Legacy key <c>Column_Address</c>.</summary>
    public bool ColumnAddress { get; set; } = true;

    /// <summary>Whether the users grid shows the telephone column. Legacy key <c>Column_Telephone</c>.</summary>
    public bool ColumnTelephone { get; set; } = true;

    /// <summary>Whether the users grid shows the electronic-mail column. Legacy key <c>Column_Email</c>.</summary>
    public bool ColumnEmail { get; set; }

    /// <summary>Whether the users grid shows the created-date column. Legacy key <c>Column_CreatedDate</c>.</summary>
    public bool ColumnCreatedDate { get; set; } = true;

    /// <summary>Whether the users grid shows the last-login column. Legacy key <c>Column_LastLogin</c>.</summary>
    public bool ColumnLastLogin { get; set; }

    /// <summary>
    /// Whether the users grid shows the authorised column. Legacy key <c>Column_Authorized</c>, whose US
    /// spelling is preserved so the mapping back to the setting row stays mechanical.
    /// </summary>
    public bool ColumnAuthorized { get; set; } = true;

    /// <summary>
    /// The grid's default display mode. Legal values are <c>0</c> (all users), <c>1</c> (first-letter
    /// selector) and <c>2</c> (no users until a search).
    /// </summary>
    public int DisplayMode { get; set; } = 2;

    /// <summary>
    /// Whether the grid hides its pager for a single-page result. Legacy key <c>Display_SuppressPager</c>.
    /// </summary>
    public bool DisplaySuppressPager { get; set; }

    /// <summary>The number of users the grid shows on one page. Legacy key <c>Records_PerPage</c>.</summary>
    public int RecordsPerPage { get; set; } = 10;

    /// <summary>
    /// The visibility applied to a newly created profile value. Legal values are <c>0</c> (all users),
    /// <c>1</c> (members only) and <c>2</c> (administrators only).
    /// </summary>
    public int ProfileDefaultVisibility { get; set; } = 2;

    /// <summary>
    /// Whether the profile screen offers the per-property visibility control. Legacy key
    /// <c>Profile_DisplayVisibility</c>.
    /// </summary>
    public bool ProfileDisplayVisibility { get; set; } = true;

    /// <summary>
    /// Whether the profile screen offers the manage-services section. Legacy key
    /// <c>Profile_ManageServices</c>.
    /// </summary>
    public bool ProfileManageServices { get; set; } = true;

    /// <summary>
    /// The page a user is sent to after a successful sign-in, or <see langword="null"/> for none. Legacy
    /// key <c>Redirect_AfterLogin</c>.
    /// </summary>
    public int? RedirectAfterLogin { get; set; }

    /// <summary>
    /// The page a user is sent to after completing registration, or <see langword="null"/> for none. Legacy
    /// key <c>Redirect_AfterRegistration</c>; the ownership rule of <see cref="RedirectAfterLogin"/>
    /// applies identically.
    /// </summary>
    public int? RedirectAfterRegistration { get; set; }

    /// <summary>
    /// The page a user is sent to after signing out, or <see langword="null"/> for none. Legacy key
    /// <c>Redirect_AfterLogout</c>; the ownership rule of <see cref="RedirectAfterLogin"/> applies
    /// identically.
    /// </summary>
    public int? RedirectAfterLogout { get; set; }

    /// <summary>
    /// The expression used to validate an electronic-mail address supplied during registration or profile
    /// maintenance. Legacy key <c>Security_EmailValidation</c>.
    /// </summary>
    public string SecurityEmailValidation { get; set; } =
        MembershipSettingsDto.DefaultEmailValidationExpression;

    /// <summary>
    /// Whether registration requires a valid profile. Legacy key <c>Security_RequireValidProfile</c>.
    /// </summary>
    public bool SecurityRequireValidProfile { get; set; }

    /// <summary>
    /// Whether an existing account whose profile is no longer valid must complete it before a sign-in
    /// proceeds. Legacy key <c>Security_RequireValidProfileAtLogin</c>.
    /// </summary>
    public bool SecurityRequireValidProfileAtLogin { get; set; } =
        MembershipSettingsDto.DefaultRequireValidProfileAtLogin;

    /// <summary>
    /// How accounts are presented for selection in the role-management screen. Legal values are <c>0</c>
    /// (picker) and <c>1</c> (free-text box).
    /// </summary>
    /// <remarks>
    /// The read projection DERIVES this value when the tenant stores none, promoting it above a
    /// thousand-account threshold; a write always states it explicitly, because choosing a value and
    /// observing a derived one are different acts.
    /// </remarks>
    public int SecurityUsersControl { get; set; }

    /// <summary>
    /// The format applied when composing an account's display name, or an empty string when display names
    /// are entered directly. Legacy key <c>Security_DisplayNameFormat</c>.
    /// </summary>
    /// <remarks>
    /// Non-nullable and defaulted to the empty string, because the legacy application-encoded null string
    /// IS the empty string rather than a null reference. Bounded by the setting column's width.
    /// </remarks>
    public string SecurityDisplayNameFormat { get; set; } = string.Empty;

    /// <summary>
    /// Accepted and IGNORED: the read document's marker saying whether the tenant stores these settings.
    /// </summary>
    public bool IsStored { get; set; }
}
