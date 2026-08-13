namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Typed, wire-facing projection of the membership settings that govern one portal's user administration
/// experience. Served by <c>GET /api/v1/users/settings</c> and accepted by the corresponding update.
/// </summary>
public sealed class MembershipSettingsDto
{
    /// <summary>
    /// The module definition name used to locate the settings-bearing module instance within a portal,
    /// spelled exactly as the legacy code spells it: <c>User Accounts</c>, two words separated by a single
    /// space.
    /// </summary>
    public const string UserAccountsModuleDefinitionName = "User Accounts";

    /// <summary>
    /// The legacy default expression used to validate an email address, preserved verbatim from
    /// <c>glbEmailRegEx</c> - the value <c>UserModuleBase.GetSettings</c> assigns when the
    /// <c>Security_EmailValidation</c> setting is absent.
    /// </summary>
    /// <remarks>
    /// Named for the same reason as <see cref="UserAccountsModuleDefinitionName"/>: a measured legacy value
    /// must have exactly one definition rather than being repeated wherever the default is needed. This is
    /// pattern data carried across the wire, not a credential and not a validation attribute.
    /// </remarks>
    public const string DefaultEmailValidationExpression =
        @"\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b";

    /// <summary>
    /// The value applied to <see cref="SecurityRequireValidProfileAtLogin"/> when the tenant stores no
    /// <c>Security_RequireValidProfileAtLogin</c> setting, and when the tenant has no settings source at
    /// all.
    /// </summary>
    /// <remarks>
    /// Named for the same reason as <see cref="DefaultEmailValidationExpression"/>. This particular default
    /// is load-bearing beyond the settings screen: the sign-in profile-completeness gate reads it for a
    /// tenant whose settings source is absent, so a measured default that existed only as a property
    /// initialiser could not be reached from there and would have had to be repeated.
    /// </remarks>
    public const bool DefaultRequireValidProfileAtLogin = true;

    /// <summary>
    /// Whether these values were read from a tenant settings store, or are the installation defaults
    /// because the tenant has no settings source.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> means every value below is the measured legacy default and NOTHING is stored
    /// for this tenant. It does not mean the read failed: the settings are still the settings that apply,
    /// which is exactly what the legacy defaults were for.
    /// </remarks>
    public bool IsStored { get; set; }

    /// <summary>
    /// Whether the users grid shows the first-name column. Legacy key <c>Column_FirstName</c>, measured
    /// default <see langword="false"/>, legacy label "Show First Name Column:".
    /// </summary>
    public bool ColumnFirstName { get; set; }

    /// <summary>
    /// Whether the users grid shows the last-name column. Legacy key <c>Column_LastName</c>, measured
    /// default <see langword="false"/>, legacy label "Show Last Name Column:".
    /// </summary>
    public bool ColumnLastName { get; set; }

    /// <summary>
    /// Whether the users grid shows the display-name column. Legacy key <c>Column_DisplayName</c>, measured
    /// default <see langword="true"/>, legacy label "Show Name Column:".
    /// </summary>
    public bool ColumnDisplayName { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the address column. Legacy key <c>Column_Address</c>, measured default
    /// <see langword="true"/>, legacy label "Show Address Column:".
    /// </summary>
    public bool ColumnAddress { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the telephone column. Legacy key <c>Column_Telephone</c>, measured
    /// default <see langword="true"/>, legacy label "Show Telephone Column:".
    /// </summary>
    public bool ColumnTelephone { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the email column. Legacy key <c>Column_Email</c>, measured default <see
    /// langword="false"/>, legacy label "Show Email Column:".
    /// </summary>
    public bool ColumnEmail { get; set; }

    /// <summary>
    /// Whether the users grid shows the created-date column. Legacy key <c>Column_CreatedDate</c>, measured
    /// default <see langword="true"/>, legacy label "Show Created Date Column:".
    /// </summary>
    public bool ColumnCreatedDate { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the last-login column. Legacy key <c>Column_LastLogin</c>, measured
    /// default <see langword="false"/>, legacy label "Show Last Login Column:".
    /// </summary>
    public bool ColumnLastLogin { get; set; }

    /// <summary>
    /// Whether the users grid shows the authorised column. Legacy key <c>Column_Authorized</c>, measured
    /// default <see langword="true"/>, legacy label "Show Authorized Column:".
    /// </summary>
    /// <remarks>
    /// The member name preserves the legacy key's US spelling so the mapping back to the
    /// <c>ModuleSettings</c> row stays mechanical - note that the terminal column it governs is spelled
    /// <c>Authorised</c>.
    /// </remarks>
    public bool ColumnAuthorized { get; set; } = true;

    /// <summary>
    /// The default display mode of the users grid, as a discriminator: <c>0</c> lists all users, <c>1</c>
    /// groups users behind a first-letter selector, and <c>2</c> shows no users until a search is
    /// performed. Legacy key <c>Display_Mode</c>, measured default <c>2</c>, legacy label "Default Display
    /// Mode".
    /// </summary>
    public int DisplayMode { get; set; } = 2;

    /// <summary>
    /// Whether the users grid hides its pager when the result set fits on a single page. Legacy key
    /// <c>Display_SuppressPager</c>, measured default <see langword="false"/>, legacy label "Suppress
    /// Pager?".
    /// </summary>
    public bool DisplaySuppressPager { get; set; }

    /// <summary>
    /// The number of users the grid shows on one page. Legacy key <c>Records_PerPage</c>, measured default
    /// <c>10</c>, legacy label "Users per Page:".
    /// </summary>
    public int RecordsPerPage { get; set; } = 10;

    /// <summary>
    /// The visibility applied to a newly created profile property value, as a discriminator: <c>0</c> is
    /// visible to all users, <c>1</c> to authenticated members only, and <c>2</c> to administrators only.
    /// Legacy key <c>Profile_DefaultVisibility</c>, measured default <c>2</c>, legacy label "Default
    /// Profile Visibility Mode".
    /// </summary>
    public int ProfileDefaultVisibility { get; set; } = 2;

    /// <summary>
    /// Whether the profile screen shows the per-property visibility control to the user. Legacy key
    /// <c>Profile_DisplayVisibility</c>, measured default <see langword="true"/>, legacy label "Display
    /// Profile Visibility".
    /// </summary>
    public bool ProfileDisplayVisibility { get; set; } = true;

    /// <summary>
    /// Whether the profile screen shows the manage-services section, through which a user subscribes to and
    /// cancels role-based services. Legacy key <c>Profile_ManageServices</c>, measured default <see
    /// langword="true"/>, legacy label "Display Manage Services".
    /// </summary>
    public bool ProfileManageServices { get; set; } = true;

    /// <summary>
    /// Identifier of the page a user is sent to after a successful login, or <see langword="null"/> when no
    /// redirect is configured. Legacy key <c>Redirect_AfterLogin</c>, measured default <c>-1</c>, legacy
    /// label "Redirect After Login:".
    /// </summary>
    // The legacy sentinel -1 means "no redirect page" and is translated to null for all three Redirect_
    // members. The translation happens exactly once, in the mapper, so the sentinel never reaches the wire
    // as a magic number.
    public int? RedirectAfterLogin { get; set; }

    /// <summary>
    /// Identifier of the page a user is sent to after completing registration, or <see langword="null"/>
    /// when no redirect is configured. Legacy key <c>Redirect_AfterRegistration</c>, measured default
    /// <c>-1</c>, legacy label "Redirect After Registration:", edited with the same page picker.
    /// </summary>
    public int? RedirectAfterRegistration { get; set; }

    /// <summary>
    /// Identifier of the page a user is sent to after logging off, or <see langword="null"/> when no
    /// redirect is configured. Legacy key <c>Redirect_AfterLogout</c>, measured default <c>-1</c>, legacy
    /// label "Redirect After Logout:", edited with the same page picker.
    /// </summary>
    public int? RedirectAfterLogout { get; set; }

    // MIGRATION: the legacy keys Security_CaptchaLogin and Security_CaptchaRegister, both measured
    // defaulting to false at UserModuleBase.vb:L160-L166, are deliberately absent.

    /// <summary>
    /// The expression used to validate an email address supplied during registration or profile
    /// maintenance. Legacy key <c>Security_EmailValidation</c>, measured default <see
    /// cref="DefaultEmailValidationExpression"/>, legacy label "Email Address Validation:".
    /// </summary>
    /// <remarks>
    /// Because an administrator may edit it, the pattern is carried as data and is applied by the request
    /// validators; it is never expressed as a validation attribute on this contract. It is a pattern rather
    /// than a credential, so it is safe on the wire.
    /// </remarks>
    public string SecurityEmailValidation { get; set; } = DefaultEmailValidationExpression;

    /// <summary>
    /// Whether a new user must supply a valid profile while registering. Legacy key
    /// <c>Security_RequireValidProfile</c>, measured default <see langword="false"/>, legacy label "Require
    /// a valid Profile for Registration:".
    /// </summary>
    public bool SecurityRequireValidProfile { get; set; }

    /// <summary>
    /// Whether an existing user whose profile is no longer valid must complete it before the login is
    /// allowed to proceed. Legacy key <c>Security_RequireValidProfileAtLogin</c>, measured default <see
    /// langword="true"/>, legacy label "Require a valid Profile for Login:".
    /// </summary>
    public bool SecurityRequireValidProfileAtLogin { get; set; } = DefaultRequireValidProfileAtLogin;

    /// <summary>
    /// How users are presented for selection in the role-management screen, as a discriminator: <c>0</c>
    /// offers a picker listing every user, <c>1</c> offers a free-text box. Legacy key
    /// <c>Security_UsersControl</c>, measured default <c>0</c>, legacy label "Users display mode in Manage
    /// Roles".
    /// </summary>
    public int SecurityUsersControl { get; set; }

    /// <summary>
    /// The format applied when composing a user's display name, or an empty string when display names are
    /// entered directly. Legacy key <c>Security_DisplayNameFormat</c>, measured default the empty string,
    /// legacy label "Display Name Format:".
    /// </summary>
    public string SecurityDisplayNameFormat { get; set; } = string.Empty;
}
