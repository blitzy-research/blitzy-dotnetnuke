namespace DnnMigration.Application.Dtos.User;

// MIGRATION: THIS TYPE EXISTS BECAUSE A RESPONSE CONTRACT WAS BEING BOUND AS A REQUEST BODY, and the
// consequence was not cosmetic. PUT /api/v1/portals/{portalId}/membership-settings previously bound
// MembershipSettingsDto - a projection whose documentation states that bounds are enforced "by a request
// validator under Application/Validation/" - while no validator for that type existed. Every one of the
// twenty-three values therefore reached the ModuleSettings table unchecked: the three discriminators
// accepted any integer, the page size accepted zero and negative numbers, both free-text members accepted
// two thousand characters of anything, the three redirect members accepted a page belonging to another
// tenant, and the email-validation member accepted an expression that was never compiled - so a
// catastrophically backtracking pattern could be stored by an administrator and then applied by the
// registration and profile validators to every subsequent submission.
//
// MIGRATION: it is a SEPARATE TYPE rather than a validator bolted onto the response projection, for two
// measured reasons. First, the response type is documented as never omitting a member and as carrying
// values a write must not accept - SecurityUsersControl is DERIVED for the response from the tenant's
// account count above a thousand-account threshold, so echoing a read back is not the same act as
// choosing a value. Second, a validator registered for the response type would also fire on any future
// endpoint that happens to bind it, which is exactly the accident this replaces.
//
// MIGRATION: the two constants the legacy defaults live on - MembershipSettingsDto's module-definition
// name and its email-expression default - are REFERENCED rather than repeated, so the write contract and
// the read projection cannot drift apart.

/// <summary>
/// The write contract for one portal's membership settings, submitted to
/// <c>PUT /api/v1/portals/{portalId}/membership-settings</c>.
/// </summary>
/// <remarks>
/// <para>
/// A COMPLETE REPLACEMENT, NOT A PATCH. Every member is written on every request, exactly as the legacy
/// screen wrote its whole form back (<c>UserSettings.ascx.vb:L183</c>), so an omitted member takes the
/// initialiser below rather than preserving whatever is stored. The initialisers are the measured legacy
/// defaults, which makes an omitted member equivalent to resetting that one setting - the same outcome the
/// legacy screen produced for a control left at its default.
/// </para>
/// <para>
/// Each member corresponds to exactly one <c>ModuleSettings</c> row belonging to the portal's
/// <see cref="MembershipSettingsDto.UserAccountsModuleDefinitionName"/> module instance. The measured
/// provenance of every value - the legacy key, its default and the screen that edited it - is documented
/// once on the matching member of <see cref="MembershipSettingsDto"/> and is not repeated here; only the
/// facts a WRITE adds are stated below, which is where each value's legal range comes from.
/// </para>
/// <para>
/// Ranges are declared by <c>UpdateMembershipSettingsRequestValidator</c> and, for the three page
/// references, completed by the service - a page identifier can only be judged against the tenant that
/// owns it, and ownership is a data question rather than a field rule.
/// </para>
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
    /// selector) and <c>2</c> (no users until a search). Legacy key <c>Display_Mode</c>.
    /// </summary>
    /// <remarks>
    /// A discriminator rather than an enumeration, matching the read projection: the legacy
    /// <c>DisplayMode</c> vocabulary has no domain counterpart and declaring a competing copy in this layer
    /// would fracture the vocabulary the domain owns. The closed set is enforced by the validator, which is
    /// what a discriminator needs and previously did not have.
    /// </remarks>
    public int DisplayMode { get; set; } = 2;

    /// <summary>Whether the grid hides its pager for a single-page result. Legacy key <c>Display_SuppressPager</c>.</summary>
    public bool DisplaySuppressPager { get; set; }

    /// <summary>
    /// The number of users the grid shows on one page. Legacy key <c>Records_PerPage</c>.
    /// </summary>
    /// <remarks>
    /// Bounded below at one - a page of zero rows renders an empty grid on every page and a negative page
    /// size is meaningless - and above at the same ceiling every paged read on this API already enforces,
    /// so a stored setting cannot ask for a page the listing endpoint would refuse.
    /// </remarks>
    public int RecordsPerPage { get; set; } = 10;

    /// <summary>
    /// The visibility applied to a newly created profile value. Legal values are <c>0</c> (all users),
    /// <c>1</c> (members only) and <c>2</c> (administrators only). Legacy key
    /// <c>Profile_DefaultVisibility</c>.
    /// </summary>
    public int ProfileDefaultVisibility { get; set; } = 2;

    /// <summary>Whether the profile screen offers the per-property visibility control. Legacy key <c>Profile_DisplayVisibility</c>.</summary>
    public bool ProfileDisplayVisibility { get; set; } = true;

    /// <summary>Whether the profile screen offers the manage-services section. Legacy key <c>Profile_ManageServices</c>.</summary>
    public bool ProfileManageServices { get; set; } = true;

    /// <summary>
    /// The page a user is sent to after a successful sign-in, or <see langword="null"/> for none. Legacy
    /// key <c>Redirect_AfterLogin</c>.
    /// </summary>
    /// <remarks>
    /// A PAGE OF THIS TENANT. The legacy screen edited all three redirect keys with a page picker bound to
    /// the portal's own pages (<c>UserSettings.ascx.vb:L80-L82</c>), so a page belonging to another tenant
    /// was never selectable; the service reproduces that by refusing an identifier the tenant does not own.
    /// Absence is <see langword="null"/> and nothing else - the page identity seeds at zero, so zero is a
    /// legitimate page, and the legacy <c>-1</c> marker is translated by the mapper rather than accepted
    /// here.
    /// </remarks>
    public int? RedirectAfterLogin { get; set; }

    /// <summary>
    /// The page a user is sent to after completing registration, or <see langword="null"/> for none.
    /// Legacy key <c>Redirect_AfterRegistration</c>; the ownership rule of
    /// <see cref="RedirectAfterLogin"/> applies identically.
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
    /// <remarks>
    /// THE ONE MEMBER WHOSE STORED VALUE IS LATER EXECUTED. Everything else here is data a screen renders;
    /// this is a pattern the registration and profile validators apply to every submission. An
    /// administrator who stored a catastrophically backtracking expression would therefore have made every
    /// later submission expensive to evaluate, which is why the validator compiles the submitted value
    /// under a timeout and refuses one that cannot be compiled. Storing it remains permitted - the legacy
    /// screen permitted it, and the pattern is genuinely tenant-configurable - so the rule proves the value
    /// is a usable expression rather than dictating which expression it is.
    /// </remarks>
    public string SecurityEmailValidation { get; set; } =
        MembershipSettingsDto.DefaultEmailValidationExpression;

    /// <summary>Whether registration requires a valid profile. Legacy key <c>Security_RequireValidProfile</c>.</summary>
    public bool SecurityRequireValidProfile { get; set; }

    /// <summary>
    /// Whether an existing account whose profile is no longer valid must complete it before a sign-in
    /// proceeds. Legacy key <c>Security_RequireValidProfileAtLogin</c>.
    /// </summary>
    public bool SecurityRequireValidProfileAtLogin { get; set; } =
        MembershipSettingsDto.DefaultRequireValidProfileAtLogin;

    /// <summary>
    /// How accounts are presented for selection in the role-management screen. Legal values are <c>0</c>
    /// (picker) and <c>1</c> (free-text box). Legacy key <c>Security_UsersControl</c>.
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
}
