namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Typed, wire-facing projection of the membership settings that govern one portal's user
/// administration experience. Served by <c>GET /api/v1/portals/{portalId}/membership-settings</c> and accepted by the
/// corresponding update.
/// </summary>
/// <remarks>
/// <para>
/// Despite the name this is neither portal configuration, nor host configuration, nor per-user
/// membership state. Every property corresponds to exactly one <c>ModuleSettings</c> row - a
/// <c>SettingName</c>/<c>SettingValue</c> pair - belonging to the
/// <see cref="UserAccountsModuleDefinitionName"/> module instance installed in the portal
/// (<c>UserController.GetUserSettings</c>, <c>UserController.vb:L656-L671</c>). The vocabulary and
/// every default value are measured from <c>UserModuleBase.GetSettings</c>
/// (<c>UserModuleBase.vb:L94-L194</c>), which is the authoritative source for this type.
/// </para>
/// <para>
/// The legacy administration screen selects settings by name prefix and admits exactly six
/// families - <c>Column_</c>, <c>Display_</c>, <c>Profile_</c>, <c>Records_</c>, <c>Redirect_</c>
/// and <c>Security_</c> (<c>UserSettings.ascx.vb:L116-L119</c>). A setting outside those six is
/// not a membership setting and has no member here. In particular the host-level membership and
/// password configuration rendered by the same screen comes from separate editor data sources
/// (<c>UserSettings.ascx.vb:L58-L75</c>), is reserved to super-users, and is modelled by
/// <c>Application/Options/PasswordPolicyOptions.cs</c> rather than by this contract.
/// </para>
/// <para>
/// EVERY PROPERTY IS INITIALISED TO ITS MEASURED LEGACY DEFAULT, so a default-constructed instance
/// reproduces the legacy default settings set exactly. That is load-bearing rather than
/// convenient, because it is what makes the absent-module case safe: the legacy accessor returns a
/// null reference when the module is not installed in the portal
/// (<c>UserController.vb:L663</c> assigns settings only when the lookup succeeds), and the legacy
/// codebase then handles that null two different ways. The path annotated
/// "[cnurse] 02/07/2008 DNN-7003 fixed" (<c>UserModuleBase.vb:L204</c>) substitutes the full
/// default set at L210-L212; the unfixed path dereferences <c>settings("Column_FirstName")</c>
/// with no null guard at L98, and <c>UserSettings.ascx.vb:L106</c> passes the possibly-null result
/// straight into it, raising a <see cref="NullReferenceException"/> on the administration screen
/// whenever the module is absent. This contract resolves the inconsistency in favour of the
/// DNN-7003 precedent: the service must return a populated instance when the module is absent -
/// never a null reference and never a null payload.
/// </para>
/// <para>
/// THREE MEMBERS ARE PLAIN INTEGER DISCRIMINATORS - <see cref="DisplayMode"/>,
/// <see cref="ProfileDefaultVisibility"/> and <see cref="SecurityUsersControl"/> - because their
/// legacy enumerations have no domain counterpart and declaring competing copies in this layer
/// would fracture the single vocabulary the domain owns. Their legal values and meanings are
/// documented on each member instead.
/// </para>
/// <para>
/// NO MEMBER MAY BE OMITTED FROM A RESPONSE WHEN ITS VALUE IS EMPTY, DEFAULT OR FALSE. An empty
/// display-name format, a display mode of 0, a users control of 0, a default visibility of 0 and
/// each false-defaulted flag are all meaningful values, so conditional serialisation that drops
/// empty or default values must not be applied to this type. There is also no cache member: the
/// legacy accessor cached the settings with an expiry taken from a global performance multiplier
/// (<c>UserController.vb:L658</c>, L665), and cache keys, expiries and invalidation are
/// infrastructure concerns.
/// </para>
/// </remarks>
public sealed class MembershipSettingsDto
{
    /// <summary>
    /// The module definition name used to locate the settings-bearing module instance within a
    /// portal, spelled exactly as the legacy code spells it: <c>User Accounts</c>, two words
    /// separated by a single space.
    /// </summary>
    /// <remarks>
    /// LOAD-BEARING DATA, NOT A LABEL. <c>UserController.GetUserSettings</c> passes it to
    /// <c>GetModuleByDefinition</c> (<c>UserController.vb:L662</c>) and the administration screen
    /// passes the same literal when writing settings back (<c>UserSettings.ascx.vb:L169</c>).
    /// Renaming, rewording, re-casing or localising it silently breaks settings resolution for
    /// every portal, so it is declared once here and must be referenced from this constant rather
    /// than repeated as a literal at any call site.
    /// </remarks>
    public const string UserAccountsModuleDefinitionName = "User Accounts";

    /// <summary>
    /// The legacy default expression used to validate an email address, preserved verbatim from
    /// <c>glbEmailRegEx</c> (<c>Globals.vb:L132</c>) - the value <c>UserModuleBase.GetSettings</c>
    /// assigns when the <c>Security_EmailValidation</c> setting is absent
    /// (<c>UserModuleBase.vb:L167-L169</c>).
    /// </summary>
    /// <remarks>
    /// Named for the same reason as <see cref="UserAccountsModuleDefinitionName"/>: a measured
    /// legacy value must have exactly one definition rather than being repeated wherever the
    /// default is needed. This is pattern data carried across the wire, not a credential and not a
    /// validation attribute.
    /// </remarks>
    public const string DefaultEmailValidationExpression =
        @"\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b";

    /// <summary>
    /// The value applied to <see cref="SecurityRequireValidProfileAtLogin"/> when the tenant stores no
    /// <c>Security_RequireValidProfileAtLogin</c> setting, and when the tenant has no settings source at
    /// all (<c>UserModuleBase.vb:L175-L177</c>).
    /// </summary>
    /// <remarks>
    /// Named for the same reason as <see cref="DefaultEmailValidationExpression"/>. This particular
    /// default is load-bearing beyond the settings screen: the sign-in profile-completeness gate reads it
    /// for a tenant whose settings source is absent, so a measured default that existed only as a property
    /// initialiser could not be reached from there and would have had to be repeated.
    /// </remarks>
    public const bool DefaultRequireValidProfileAtLogin = true;

    /// <summary>
    /// Whether the users grid shows the first-name column. Legacy key <c>Column_FirstName</c>,
    /// measured default <see langword="false"/> (<c>UserModuleBase.vb:L98-L100</c>), legacy label
    /// "Show First Name Column:".
    /// </summary>
    public bool ColumnFirstName { get; set; }

    /// <summary>
    /// Whether the users grid shows the last-name column. Legacy key <c>Column_LastName</c>,
    /// measured default <see langword="false"/> (<c>UserModuleBase.vb:L101-L103</c>), legacy label
    /// "Show Last Name Column:".
    /// </summary>
    public bool ColumnLastName { get; set; }

    /// <summary>
    /// Whether the users grid shows the display-name column. Legacy key <c>Column_DisplayName</c>,
    /// measured default <see langword="true"/> (<c>UserModuleBase.vb:L104-L106</c>), legacy label
    /// "Show Name Column:".
    /// </summary>
    public bool ColumnDisplayName { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the address column. Legacy key <c>Column_Address</c>, measured
    /// default <see langword="true"/> (<c>UserModuleBase.vb:L107-L109</c>), legacy label
    /// "Show Address Column:".
    /// </summary>
    public bool ColumnAddress { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the telephone column. Legacy key <c>Column_Telephone</c>,
    /// measured default <see langword="true"/> (<c>UserModuleBase.vb:L110-L112</c>), legacy label
    /// "Show Telephone Column:".
    /// </summary>
    public bool ColumnTelephone { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the email column. Legacy key <c>Column_Email</c>, measured
    /// default <see langword="false"/> (<c>UserModuleBase.vb:L113-L115</c>), legacy label
    /// "Show Email Column:".
    /// </summary>
    public bool ColumnEmail { get; set; }

    /// <summary>
    /// Whether the users grid shows the created-date column. Legacy key <c>Column_CreatedDate</c>,
    /// measured default <see langword="true"/> (<c>UserModuleBase.vb:L116-L118</c>), legacy label
    /// "Show Created Date Column:".
    /// </summary>
    public bool ColumnCreatedDate { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the last-login column. Legacy key <c>Column_LastLogin</c>,
    /// measured default <see langword="false"/> (<c>UserModuleBase.vb:L119-L121</c>), legacy label
    /// "Show Last Login Column:".
    /// </summary>
    public bool ColumnLastLogin { get; set; }

    /// <summary>
    /// Whether the users grid shows the authorised column. Legacy key <c>Column_Authorized</c>,
    /// measured default <see langword="true"/> (<c>UserModuleBase.vb:L122-L124</c>), legacy label
    /// "Show Authorized Column:".
    /// </summary>
    /// <remarks>
    /// The member name preserves the legacy key's US spelling so the mapping back to the
    /// <c>ModuleSettings</c> row stays mechanical - note that the terminal column it governs is
    /// spelled <c>Authorised</c>.
    /// </remarks>
    public bool ColumnAuthorized { get; set; } = true;

    /// <summary>
    /// The default display mode of the users grid, as a discriminator: <c>0</c> lists all users,
    /// <c>1</c> groups users behind a first-letter selector, and <c>2</c> shows no users until a
    /// search is performed. Legacy key <c>Display_Mode</c>, measured default <c>2</c>
    /// (<c>UserModuleBase.vb:L126-L130</c>), legacy label "Default Display Mode".
    /// </summary>
    /// <remarks>
    /// The three values are the members of the legacy <c>DisplayMode</c> enumeration
    /// (<c>UserModuleBase.vb:L36-L40</c>): <c>All = 0</c>, <c>FirstLetter = 1</c>,
    /// <c>None = 2</c>.
    /// </remarks>
    public int DisplayMode { get; set; } = 2;

    /// <summary>
    /// Whether the users grid hides its pager when the result set fits on a single page. Legacy key
    /// <c>Display_SuppressPager</c>, measured default <see langword="false"/>
    /// (<c>UserModuleBase.vb:L131-L133</c>), legacy label "Suppress Pager?".
    /// </summary>
    public bool DisplaySuppressPager { get; set; }

    /// <summary>
    /// The number of users the grid shows on one page. Legacy key <c>Records_PerPage</c>, measured
    /// default <c>10</c> (<c>UserModuleBase.vb:L134-L136</c>), legacy label "Users per Page:".
    /// </summary>
    /// <remarks>
    /// The sole member of the <c>Records_</c> family. Carried as data only: bounds are enforced by
    /// a request validator under <c>Application/Validation/</c>, never by this contract.
    /// </remarks>
    public int RecordsPerPage { get; set; } = 10;

    /// <summary>
    /// The visibility applied to a newly created profile property value, as a discriminator:
    /// <c>0</c> is visible to all users, <c>1</c> to authenticated members only, and <c>2</c> to
    /// administrators only. Legacy key <c>Profile_DefaultVisibility</c>, measured default <c>2</c>
    /// (<c>UserModuleBase.vb:L138-L142</c>), legacy label "Default Profile Visibility Mode".
    /// </summary>
    /// <remarks>
    /// The three values are the members of the legacy <c>UserVisibilityMode</c> enumeration
    /// (<c>UserVisibilityMode.vb:L23-L27</c>): <c>AllUsers = 0</c>, <c>MembersOnly = 1</c>,
    /// <c>AdminOnly = 2</c>. This is the same vocabulary as the per-value visibility carried by the
    /// profile contracts, so the two must stay consistent - but note the deliberate asymmetry the
    /// legacy code creates: this SETTING defaults to 2 (administrators only) whereas the stored
    /// profile-value visibility column defaults to 0. It is also a distinct concept from the
    /// domain's module-visibility enumeration and must not be merged into it.
    /// </remarks>
    public int ProfileDefaultVisibility { get; set; } = 2;

    /// <summary>
    /// Whether the profile screen shows the per-property visibility control to the user. Legacy key
    /// <c>Profile_DisplayVisibility</c>, measured default <see langword="true"/>
    /// (<c>UserModuleBase.vb:L143-L145</c>), legacy label "Display Profile Visibility".
    /// </summary>
    public bool ProfileDisplayVisibility { get; set; } = true;

    /// <summary>
    /// Whether the profile screen shows the manage-services section, through which a user
    /// subscribes to and cancels role-based services. Legacy key <c>Profile_ManageServices</c>,
    /// measured default <see langword="true"/> (<c>UserModuleBase.vb:L146-L148</c>), legacy label
    /// "Display Manage Services".
    /// </summary>
    /// <remarks>
    /// Governs only whether that section is offered; the subscription records it lists are a role
    /// concern and are modelled by the role contracts, never by this type.
    /// </remarks>
    public bool ProfileManageServices { get; set; } = true;

    /// <summary>
    /// Identifier of the page a user is sent to after a successful login, or <see langword="null"/>
    /// when no redirect is configured. Legacy key <c>Redirect_AfterLogin</c>, measured default
    /// <c>-1</c> (<c>UserModuleBase.vb:L149-L151</c>), legacy label "Redirect After Login:".
    /// </summary>
    /// <remarks>
    /// A page identifier rather than a URL, because the legacy screen edits this key with a page
    /// picker (<c>UserSettings.ascx.vb:L80</c>).
    /// </remarks>
    // MIGRATION: the legacy sentinel -1 (the application-encoded null integer defined in
    // Library/Components/Shared/Null.vb) means "no redirect page" and is translated to null for all
    // three Redirect_ members. The translation happens exactly once, in the mapper, so the sentinel
    // never reaches the wire as a magic number. Absence must be tested as null and nothing else:
    // the page identifier column is seeded from 0, so 0 is a legitimate page and a test for a
    // non-positive value would wrongly treat the first page in the portal as missing.
    public int? RedirectAfterLogin { get; set; }

    /// <summary>
    /// Identifier of the page a user is sent to after completing registration, or
    /// <see langword="null"/> when no redirect is configured. Legacy key
    /// <c>Redirect_AfterRegistration</c>, measured default <c>-1</c>
    /// (<c>UserModuleBase.vb:L153-L155</c>), legacy label "Redirect After Registration:", edited
    /// with the same page picker (<c>UserSettings.ascx.vb:L82</c>).
    /// </summary>
    public int? RedirectAfterRegistration { get; set; }

    /// <summary>
    /// Identifier of the page a user is sent to after logging off, or <see langword="null"/> when
    /// no redirect is configured. Legacy key <c>Redirect_AfterLogout</c>, measured default
    /// <c>-1</c> (<c>UserModuleBase.vb:L156-L158</c>), legacy label "Redirect After Logout:",
    /// edited with the same page picker (<c>UserSettings.ascx.vb:L81</c>).
    /// </summary>
    public int? RedirectAfterLogout { get; set; }

    // MIGRATION: the legacy keys Security_CaptchaLogin and Security_CaptchaRegister, both measured
    // defaulting to false at UserModuleBase.vb:L160-L166, are deliberately absent. They exist
    // solely to switch on the DotNetNuke CAPTCHA server control (declared as dnn:captchacontrol at
    // Website/admin/Users/User.ascx:L66 and evaluated at User.ascx.vb:L139), which lives in the
    // excluded Library/Controls tree, so the target honours neither setting anywhere. Surfacing
    // them would advertise to the administration screen a control that cannot be applied.

    /// <summary>
    /// The expression used to validate an email address supplied during registration or profile
    /// maintenance. Legacy key <c>Security_EmailValidation</c>, measured default
    /// <see cref="DefaultEmailValidationExpression"/> (<c>UserModuleBase.vb:L167-L169</c>), legacy
    /// label "Email Address Validation:".
    /// </summary>
    /// <remarks>
    /// Because an administrator may edit it, the pattern is carried as data and is applied by the
    /// request validators; it is never expressed as a validation attribute on this contract. It is
    /// a pattern rather than a credential, so it is safe on the wire.
    /// </remarks>
    public string SecurityEmailValidation { get; set; } = DefaultEmailValidationExpression;

    /// <summary>
    /// Whether a new user must supply a valid profile while registering. Legacy key
    /// <c>Security_RequireValidProfile</c>, measured default <see langword="false"/>
    /// (<c>UserModuleBase.vb:L171-L173</c>), legacy label
    /// "Require a valid Profile for Registration:".
    /// </summary>
    public bool SecurityRequireValidProfile { get; set; }

    /// <summary>
    /// Whether an existing user whose profile is no longer valid must complete it before the login
    /// is allowed to proceed. Legacy key <c>Security_RequireValidProfileAtLogin</c>, measured
    /// default <see langword="true"/> (<c>UserModuleBase.vb:L175-L177</c>), legacy label
    /// "Require a valid Profile for Login:".
    /// </summary>
    public bool SecurityRequireValidProfileAtLogin { get; set; } = DefaultRequireValidProfileAtLogin;

    /// <summary>
    /// How users are presented for selection in the role-management screen, as a discriminator:
    /// <c>0</c> offers a picker listing every user, <c>1</c> offers a free-text box. Legacy key
    /// <c>Security_UsersControl</c>, measured default <c>0</c>
    /// (<c>UserModuleBase.vb:L178-L186</c>), legacy label "Users display mode in Manage Roles".
    /// </summary>
    /// <remarks>
    /// The two values are the members of the legacy <c>UsersControl</c> enumeration
    /// (<c>UserModuleBase.vb:L42-L45</c>): <c>Combo = 0</c>, <c>TextBox = 1</c>.
    /// </remarks>
    // MIGRATION: this is the one legacy default that is conditional rather than constant. At
    // UserModuleBase.vb:L178-L186 the legacy code selects the free-text box only when a portal
    // context exists and that portal holds more than 1000 users, and otherwise selects the picker.
    // A field initialiser cannot count users without reading data, which this contract is forbidden
    // to do, so the initialiser takes the unconditional branch - the picker, 0 - which is also the
    // branch the legacy code takes when no portal context is available. Promoting the value to 1
    // above the 1000-user threshold belongs to the service that populates this contract, and the
    // threshold is recorded here so it is not lost.
    public int SecurityUsersControl { get; set; }

    /// <summary>
    /// The format applied when composing a user's display name, or an empty string when display
    /// names are entered directly. Legacy key <c>Security_DisplayNameFormat</c>, measured default
    /// the empty string (<c>UserModuleBase.vb:L188-L190</c>), legacy label "Display Name Format:".
    /// </summary>
    /// <remarks>
    /// The format may include substitution tokens such as <c>[FIRSTNAME]</c> and
    /// <c>[LASTNAME]</c>, and when one is specified the legacy interface stopped allowing display
    /// names to be edited directly. This member carries the format string only: substituting the
    /// tokens, and rewriting stored display names when the format changes as the legacy screen did
    /// on a background thread (<c>UserSettings.ascx.vb:L175-L182</c>), are service
    /// responsibilities. It is non-nullable and initialised to <see cref="string.Empty"/> because
    /// the legacy application-encoded null string is the empty string rather than a null reference.
    /// </remarks>
    public string SecurityDisplayNameFormat { get; set; } = string.Empty;
}
