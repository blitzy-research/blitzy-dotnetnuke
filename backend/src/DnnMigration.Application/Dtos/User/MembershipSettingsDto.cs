namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Typed, wire-facing projection of the membership settings that govern one portal's user
/// administration experience. Served by <c>GET /api/v1/settings/membership</c> and accepted by the
/// corresponding update.
/// </summary>
/// <remarks>
/// <para>
/// Despite the name, this contract is neither portal configuration, nor host configuration, nor
/// per-user membership state. Every property below corresponds to exactly one <c>ModuleSettings</c>
/// row - a <c>SettingName</c>/<c>SettingValue</c> pair - belonging to the
/// <see cref="UserAccountsModuleDefinitionName"/> module instance installed in the portal, as
/// resolved by <c>UserController.GetUserSettings</c> at
/// <c>Library/Components/Users/UserController.vb:L656-L671</c>. The vocabulary and every default
/// value are measured from <c>UserModuleBase.GetSettings</c> at
/// <c>Library/Components/Users/UserModuleBase.vb:L94-L194</c>, which is the authoritative
/// specification for this type.
/// </para>
/// <para>
/// The legacy administration screen selects settings by name prefix and admits exactly six
/// families - <c>Column_</c>, <c>Display_</c>, <c>Profile_</c>, <c>Records_</c>, <c>Redirect_</c>
/// and <c>Security_</c> - at <c>Website/admin/Users/UserSettings.ascx.vb:L116-L119</c>. A setting
/// whose name falls outside those six families is not a membership setting and therefore has no
/// member here. In particular, the host-level membership provider and password configuration
/// rendered by the same screen comes from separate editor data sources
/// (<c>Website/admin/Users/UserSettings.ascx.vb:L58-L75</c>), is reserved to super-users, and is
/// modelled by <c>Application/Options/PasswordPolicyOptions.cs</c> rather than by this contract.
/// </para>
/// <para>
/// Each property is initialised to its measured legacy default, so a default-constructed instance
/// reproduces the legacy default settings set exactly. That is deliberate and load-bearing rather
/// than a convenience: it is what makes the absent-module case safe, as the migration annotations
/// immediately below explain.
/// </para>
/// </remarks>
// MIGRATION: the legacy accessor returns an untyped, string-keyed settings table (see the
// signature of GetUserSettings at Library/Components/Users/UserController.vb:L656). AAP 0.5.1.2
// replaces it with this typed projection, so every setting now has a compile-time name and type
// instead of an object retrieved by string key and cast at each call site.
//
// MIGRATION: the legacy accessor returns a null reference when the module named by
// UserAccountsModuleDefinitionName is not installed in the portal, because
// Library/Components/Users/UserController.vb:L663 only assigns the settings when the module lookup
// succeeds. The legacy codebase then handles that null inconsistently. The fixed path, which is
// annotated "[cnurse] 02/07/2008 DNN-7003 fixed" at
// Library/Components/Users/UserModuleBase.vb:L204, substitutes the full default set at
// Library/Components/Users/UserModuleBase.vb:L210-L212. The unfixed path does not: GetSettings
// dereferences settings("Column_FirstName") with no null guard at
// Library/Components/Users/UserModuleBase.vb:L98, and Website/admin/Users/UserSettings.ascx.vb:L106
// passes the possibly-null result straight into it, which raises a NullReferenceException on the
// administration screen whenever the module is absent. This type resolves the inconsistency in
// favour of the DNN-7003 precedent: because every member is either non-nullable with its measured
// default or explicitly nullable with a documented meaning, a default-constructed instance already
// is the legacy default set. The service therefore returns a populated instance when the module is
// absent, never a null reference and never a null payload, which preserves the fixed path's
// behaviour exactly and eliminates the unfixed path's NullReferenceException.
//
// MIGRATION: no cache member exists here. The legacy accessor caches the settings under the key
// built by UserController.SettingsKey with an expiry taken from the excluded global performance
// multiplier (Library/Components/Users/UserController.vb:L658 and L665). Per AAP 0.2.2.1 that
// subsystem is excluded and the multiplier becomes the bound configuration value
// Caching:PerformanceMultiplier, so cache keys, expiries and invalidation are Infrastructure
// concerns and are absent from this contract by design.
public sealed class MembershipSettingsDto
{
    /// <summary>
    /// The module definition name used to locate the settings-bearing module instance within a
    /// portal, spelled exactly as the legacy code spells it: <c>User Accounts</c>, two words
    /// separated by a single space.
    /// </summary>
    /// <remarks>
    /// This value is load-bearing data, not a label. <c>UserController.GetUserSettings</c> passes
    /// it to <c>GetModuleByDefinition</c> at
    /// <c>Library/Components/Users/UserController.vb:L662</c>, and the administration screen passes
    /// the same literal when writing settings back at
    /// <c>Website/admin/Users/UserSettings.ascx.vb:L169</c>. Renaming, rewording, re-casing or
    /// localising it silently breaks settings resolution for every portal, so it is declared once
    /// here and must be referenced from this constant rather than repeated as a literal at any
    /// call site.
    /// </remarks>
    public const string UserAccountsModuleDefinitionName = "User Accounts";

    /// <summary>
    /// The legacy default expression used to validate an email address, preserved verbatim.
    /// </summary>
    /// <remarks>
    /// Measured from <c>glbEmailRegEx</c> at
    /// <c>Library/Components/Shared/Globals.vb:L132</c>, which is the value
    /// <c>UserModuleBase.GetSettings</c> assigns when the <c>Security_EmailValidation</c> setting
    /// is absent (<c>Library/Components/Users/UserModuleBase.vb:L167-L169</c>). It is named here
    /// for the same reason as <see cref="UserAccountsModuleDefinitionName"/>: a measured legacy
    /// value must have exactly one definition rather than being repeated as a literal wherever the
    /// default is needed. This is pattern data carried across the wire, not a credential and not a
    /// validation attribute; see <see cref="SecurityEmailValidation"/>.
    /// </remarks>
    public const string DefaultEmailValidationExpression =
        @"\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b";

    /// <summary>
    /// Whether the users grid shows the first-name column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_FirstName</c>; measured default <c>false</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L98-L100</c>). Legacy label
    /// "Show First Name Column:".
    /// </remarks>
    public bool ColumnFirstName { get; set; }

    /// <summary>
    /// Whether the users grid shows the last-name column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_LastName</c>; measured default <c>false</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L101-L103</c>). Legacy label
    /// "Show Last Name Column:".
    /// </remarks>
    public bool ColumnLastName { get; set; }

    /// <summary>
    /// Whether the users grid shows the display-name column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_DisplayName</c>; measured default <c>true</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L104-L106</c>). Legacy label
    /// "Show Name Column:".
    /// </remarks>
    public bool ColumnDisplayName { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the address column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_Address</c>; measured default <c>true</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L107-L109</c>). Legacy label
    /// "Show Address Column:".
    /// </remarks>
    public bool ColumnAddress { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the telephone column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_Telephone</c>; measured default <c>true</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L110-L112</c>). Legacy label
    /// "Show Telephone Column:".
    /// </remarks>
    public bool ColumnTelephone { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the email column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_Email</c>; measured default <c>false</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L113-L115</c>). Legacy label
    /// "Show Email Column:".
    /// </remarks>
    public bool ColumnEmail { get; set; }

    /// <summary>
    /// Whether the users grid shows the created-date column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_CreatedDate</c>; measured default <c>true</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L116-L118</c>). Legacy label
    /// "Show Created Date Column:".
    /// </remarks>
    public bool ColumnCreatedDate { get; set; } = true;

    /// <summary>
    /// Whether the users grid shows the last-login column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_LastLogin</c>; measured default <c>false</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L119-L121</c>). Legacy label
    /// "Show Last Login Column:".
    /// </remarks>
    public bool ColumnLastLogin { get; set; }

    /// <summary>
    /// Whether the users grid shows the authorised column.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Column_Authorized</c>; measured default <c>true</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L122-L124</c>). Legacy label
    /// "Show Authorized Column:". The member name preserves the legacy key's spelling so the
    /// mapping back to the <c>ModuleSettings</c> row stays mechanical.
    /// </remarks>
    public bool ColumnAuthorized { get; set; } = true;

    /// <summary>
    /// The default display mode of the users grid, as a discriminator: <c>0</c> lists all users,
    /// <c>1</c> groups users behind a first-letter selector, and <c>2</c> shows no users until a
    /// search is performed.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Display_Mode</c>; measured default <c>2</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L126-L130</c>). Legacy label
    /// "Default Display Mode", help text "Select the default display mode for the Users Grid". The
    /// three values are the members of the legacy <c>DisplayMode</c> enumeration declared at
    /// <c>Library/Components/Users/UserModuleBase.vb:L36-L40</c>: <c>All = 0</c>,
    /// <c>FirstLetter = 1</c>, <c>None = 2</c>.
    /// </remarks>
    // MIGRATION: carried as a plain integer discriminator because the legacy DisplayMode
    // enumeration has no counterpart in the Domain layer, whose enumeration set is closed at the
    // nine types listed in AAP 0.4.1.1. Declaring a competing copy in the Application layer would
    // fracture the single source of truth the Domain owns, so the three legal values and their
    // meanings are documented above instead. The gap is deliberate and is reported for
    // MIGRATION_NOTES.md.
    public int DisplayMode { get; set; } = 2;

    /// <summary>
    /// Whether the users grid hides its pager when the result set fits on a single page.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Display_SuppressPager</c>; measured default <c>false</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L131-L133</c>). Legacy label
    /// "Suppress Pager?", help text "Check to hide the pager if only one page of records".
    /// </remarks>
    public bool DisplaySuppressPager { get; set; }

    /// <summary>
    /// The number of users the grid shows on one page.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Records_PerPage</c>; measured default <c>10</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L134-L136</c>). Legacy label
    /// "Users per Page:", help text "Enter the number of users to display on a page". This is the
    /// sole member of the <c>Records_</c> family. The value is carried as data only: bounds are
    /// enforced by the request validator in <c>Application/Validation/</c>, never by this contract.
    /// </remarks>
    public int RecordsPerPage { get; set; } = 10;

    /// <summary>
    /// The visibility applied to a newly created profile property value, as a discriminator:
    /// <c>0</c> is visible to all users, <c>1</c> to authenticated members only, and <c>2</c> to
    /// administrators only.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Profile_DefaultVisibility</c>; measured default <c>2</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L138-L142</c>). Legacy label
    /// "Default Profile Visibility Mode", help text
    /// "Select the default Profile Visibility Mode for the Users Profile". The three values are the
    /// members of the legacy <c>UserVisibilityMode</c> enumeration declared at
    /// <c>Library/Components/Users/UserVisibilityMode.vb:L23-L27</c>: <c>AllUsers = 0</c>,
    /// <c>MembersOnly = 1</c>, <c>AdminOnly = 2</c>. This is the same vocabulary as the per-value
    /// visibility carried by the profile contracts in this folder, so the two must stay consistent.
    /// Note the deliberate asymmetry that the legacy code creates: this <em>setting</em> defaults to
    /// <c>2</c> (administrators only), whereas the stored profile-value visibility column defaults
    /// to <c>0</c>.
    /// </remarks>
    // MIGRATION: carried as a plain integer discriminator for the same reason as
    // DisplayMode - the legacy UserVisibilityMode enumeration has no Domain counterpart and is not
    // recreated here. It is a distinct concept from the Domain's ModuleVisibility enumeration and
    // must not be merged into it. The gap is deliberate and is reported for MIGRATION_NOTES.md.
    public int ProfileDefaultVisibility { get; set; } = 2;

    /// <summary>
    /// Whether the profile screen shows the per-property visibility control to the user.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Profile_DisplayVisibility</c>; measured default <c>true</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L143-L145</c>). Legacy label
    /// "Display Profile Visibility", help text
    /// "Check to display the Profile Visibility control in the Users profile".
    /// </remarks>
    public bool ProfileDisplayVisibility { get; set; } = true;

    /// <summary>
    /// Whether the profile screen shows the manage-services section, through which a user
    /// subscribes to and cancels role-based services.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Profile_ManageServices</c>; measured default <c>true</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L146-L148</c>). Legacy label
    /// "Display Manage Services", help text
    /// "Check to display the Manage Services section in the Users profile". This flag governs only
    /// whether that section is offered; the subscription records it lists are a role concern and are
    /// modelled by the role contracts, never by this type.
    /// </remarks>
    public bool ProfileManageServices { get; set; } = true;

    /// <summary>
    /// Identifier of the page a user is sent to after a successful login, or <see langword="null"/>
    /// when no redirect is configured.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Redirect_AfterLogin</c>; measured default <c>-1</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L149-L151</c>). Legacy label
    /// "Redirect After Login:", help text
    /// "You can select a page to redirect to after successful login". The legacy administration
    /// screen edits this key with a page picker
    /// (<c>Website/admin/Users/UserSettings.ascx.vb:L80</c>), which is why the value is a page
    /// identifier rather than a URL.
    /// </remarks>
    // MIGRATION: the legacy sentinel -1, which is the application-encoded null integer defined at
    // Library/Components/Shared/Null.vb, means "no redirect page" and is translated to null here.
    // The translation happens exactly once, in the mapper, so the sentinel never reaches the wire as
    // a magic number. Absence must be tested as null and nothing else: the page identifier column
    // is seeded from 0, so 0 is a legitimate page and a test for a non-positive value would wrongly
    // treat the first page in the portal as missing.
    public int? RedirectAfterLogin { get; set; }

    /// <summary>
    /// Identifier of the page a user is sent to after completing registration, or
    /// <see langword="null"/> when no redirect is configured.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Redirect_AfterRegistration</c>; measured default <c>-1</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L153-L155</c>). Legacy label
    /// "Redirect After Registration:", help text
    /// "You can select a page to redirect the user to, on successful registration.". Edited with the
    /// same page picker (<c>Website/admin/Users/UserSettings.ascx.vb:L82</c>).
    /// </remarks>
    // MIGRATION: legacy sentinel -1 translated to null, exactly as described on
    // RedirectAfterLogin.
    public int? RedirectAfterRegistration { get; set; }

    /// <summary>
    /// Identifier of the page a user is sent to after logging off, or <see langword="null"/> when no
    /// redirect is configured.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Redirect_AfterLogout</c>; measured default <c>-1</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L156-L158</c>). Legacy label
    /// "Redirect After Logout:", help text
    /// "You can select a page to redirect the user to, on logout.". Edited with the same page picker
    /// (<c>Website/admin/Users/UserSettings.ascx.vb:L81</c>).
    /// </remarks>
    // MIGRATION: legacy sentinel -1 translated to null, exactly as described on
    // RedirectAfterLogin.
    public int? RedirectAfterLogout { get; set; }

    // MIGRATION: the legacy keys Security_CaptchaLogin and Security_CaptchaRegister, both measured
    // defaulting to false at Library/Components/Users/UserModuleBase.vb:L160-L166, are deliberately
    // absent from this contract. They exist solely to switch on the DotNetNuke CAPTCHA server
    // control, declared as dnn:captchacontrol at Website/admin/Users/User.ascx:L66 and evaluated at
    // Website/admin/Users/User.ascx.vb:L139. That control lives in Library/Controls, an excluded
    // tree per AAP 0.2.2.2, and the login CAPTCHA is dropped by AAP 0.5.1.2, so the target honours
    // neither setting anywhere. Surfacing them would advertise a control to the administration
    // screen that cannot be applied. The omission is deliberate and is reported for
    // MIGRATION_NOTES.md.

    /// <summary>
    /// The expression used to validate an email address supplied during registration or profile
    /// maintenance.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Security_EmailValidation</c>; measured default
    /// <see cref="DefaultEmailValidationExpression"/>, taken from <c>glbEmailRegEx</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L167-L169</c> and
    /// <c>Library/Components/Shared/Globals.vb:L132</c>). Legacy label
    /// "Email Address Validation:", help text "You can modify the provided Email Validation
    /// Expression, which is used to check the validity of email addresses provided.". Because an
    /// administrator may edit it, the pattern is carried as data and is applied by the request
    /// validators in <c>Application/Validation/</c>; it is never expressed as a validation
    /// attribute on this contract. It is a pattern, not a credential, so it is safe on the wire.
    /// </remarks>
    public string SecurityEmailValidation { get; set; } = DefaultEmailValidationExpression;

    /// <summary>
    /// Whether a new user must supply a valid profile while registering.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Security_RequireValidProfile</c>; measured default <c>false</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L171-L173</c>, preceded by the source comment
    /// "Forces a valid profile on registration"). Legacy label
    /// "Require a valid Profile for Registration:".
    /// </remarks>
    public bool SecurityRequireValidProfile { get; set; }

    /// <summary>
    /// Whether an existing user whose profile is no longer valid must complete it before the login
    /// is allowed to proceed.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Security_RequireValidProfileAtLogin</c>; measured default <c>true</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L175-L177</c>, preceded by the source comment
    /// "Forces a valid profile on login"). Legacy label "Require a valid Profile for Login:".
    /// </remarks>
    public bool SecurityRequireValidProfileAtLogin { get; set; } = true;

    /// <summary>
    /// How users are presented for selection in the role-management screen, as a discriminator:
    /// <c>0</c> offers a picker listing every user, <c>1</c> offers a free-text box.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Security_UsersControl</c>; measured default <c>0</c>
    /// (<c>Library/Components/Users/UserModuleBase.vb:L178-L186</c>). Legacy label
    /// "Users display mode in Manage Roles", help text
    /// "Select the Users Control to use in the Manage Roles module control". The two values are the
    /// members of the legacy <c>UsersControl</c> enumeration declared at
    /// <c>Library/Components/Users/UserModuleBase.vb:L42-L45</c>: <c>Combo = 0</c>,
    /// <c>TextBox = 1</c>.
    /// </remarks>
    // MIGRATION: carried as a plain integer discriminator for the same reason as DisplayMode and
    // ProfileDefaultVisibility - the legacy UsersControl enumeration has no Domain counterpart and
    // is not recreated here. The gap is deliberate and is reported for MIGRATION_NOTES.md.
    //
    // MIGRATION: this is the one legacy default that is conditional rather than constant. At
    // Library/Components/Users/UserModuleBase.vb:L178-L186 the legacy code selects the free-text box
    // only when a portal context exists and that portal holds more than 1000 users, and otherwise
    // selects the picker. A field initialiser cannot count users without reading data, which this
    // contract is forbidden to do, so the initialiser takes the unconditional branch - the picker,
    // 0 - which is also the branch the legacy code takes whenever no portal context is available.
    // Promoting the value to 1 above the 1000-user threshold belongs to the service that populates
    // this contract, and the threshold is recorded here so it is not lost.
    public int SecurityUsersControl { get; set; }

    /// <summary>
    /// The format applied when composing a user's display name, or an empty string when display
    /// names are entered directly.
    /// </summary>
    /// <remarks>
    /// Legacy key <c>Security_DisplayNameFormat</c>; measured default the empty string
    /// (<c>Library/Components/Users/UserModuleBase.vb:L188-L190</c>). Legacy label
    /// "Display Name Format:", help text "You can optionally specify a format for the users display
    /// name. The format can include tokens for dynamic substitution such as [FIRSTNAME] [LASTNAME].
    /// If a display name format is specified, the display name will no longer be editable through
    /// the user interface.". This member carries the format string only. Substituting the tokens,
    /// and rewriting stored display names when the format changes as the legacy screen does on a
    /// background thread at <c>Website/admin/Users/UserSettings.ascx.vb:L175-L182</c>, are service
    /// responsibilities.
    /// </remarks>
    // MIGRATION: the legacy application-encoded null string is the empty string rather than a null
    // reference, per Library/Components/Shared/Null.vb. This member is therefore a non-nullable
    // string initialised to string.Empty, which preserves that contract exactly. For the same
    // reason no member of this type may be omitted from a response when its value is empty,
    // default or false: an empty format, a display mode of 0, a users control of 0, a default
    // visibility of 0 and each of the nine false-defaulted flags are all meaningful values, so
    // conditional serialisation that drops empty or default values must not be applied to this
    // contract.
    public string SecurityDisplayNameFormat { get; set; } = string.Empty;
}
