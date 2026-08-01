namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how a portal admits new user accounts. This is the discriminator persisted in the
/// <c>Portals.UserRegistration</c> column and presented to administrators as the "User Registration"
/// security setting on the site-settings screen.
/// </summary>
/// <remarks>
/// <para>
/// Storage contract. The backing column reaches a terminal shape of <c>int NOT NULL</c> with a
/// <c>DEFAULT (0)</c> constraint. It is declared <c>[UserRegistration] [int] NULL</c> at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L84</c>, is rebuilt as
/// <c>UserRegistration int NOT NULL</c> with <c>DF_Portals_UserRegistration DEFAULT 0</c> at
/// <c>01.00.05.SqlDataProvider:L1372,L1389</c>, and is finally re-asserted by
/// <c>ALTER COLUMN [UserRegistration] [int] NOT NULL</c> at <c>03.01.01.SqlDataProvider:L1116</c> with
/// the default re-added at <c>:L1125</c>. Because the column is not nullable there is no "absent" case
/// to model, and because the database default is zero the zero member is a real, chosen mode rather
/// than a stand-in for a missing value.
/// </para>
/// <para>
/// The ordinals below are live persisted data: they must never be renumbered, and the declaration order
/// must never change. The legacy administration screen bound the stored integer straight to a
/// radio-button list index - <c>optUserRegistration.SelectedIndex = objPortal.UserRegistration</c> at
/// <c>Website/admin/Portal/SiteSettings.ascx.vb:L277</c>, written back from
/// <c>optUserRegistration.SelectedIndex</c> at <c>:L773</c> - so each member's position in this list is
/// part of the stored contract and not a formatting choice. Renumbering or reordering would silently
/// reinterpret every existing row.
/// </para>
/// <para>
/// Legacy source of truth: <c>Public Enum PortalRegistrationType</c> at
/// <c>Library/Components/Shared/Globals.vb:L84-L89</c>, read as reference data only. It is
/// independently corroborated by the option list rendered at
/// <c>Website/admin/Portal/sitesettings.ascx:L230-L233</c>, whose four items carry <c>Value="0"</c>
/// through <c>Value="3"</c> under the resource keys <c>None</c>, <c>Private</c>, <c>Public</c> and
/// <c>Verified</c>. Those resource keys are the wording the legacy screens displayed, and each member
/// below documents its own label. This type replaces the untyped
/// <c>Public Property UserRegistration() As Integer</c> at
/// <c>Library/Components/Portal/PortalInfo.vb:L125</c>.
/// </para>
/// <para>
/// All four members are reachable from live legacy code, so none may be dropped:
/// <see cref="UserRegistrationMode.NoRegistration"/> gates the sign-up affordance at
/// <c>Website/admin/Skins/User.ascx.vb:L92,L125</c> and
/// <c>Website/admin/Authentication/Login.ascx.vb:L301,L353,L767</c>; the remaining three drive the
/// post-creation branch at <c>Library/Components/Users/UserModuleBase.vb:L651,L657,L662</c> and its
/// counterpart at <c>Website/admin/Users/ManageUsers.ascx.vb:L317,L319,L321</c>.
/// </para>
/// <para>
/// This type deliberately carries no serialisation, mapping, validation or display attributes. The
/// Domain layer declares no package or project reference, so column mapping belongs to the
/// Infrastructure entity configuration for <c>Portals</c>, the wire representation belongs to the
/// Application-layer data transfer objects, and the displayed label belongs to the client.
/// </para>
/// </remarks>
// MIGRATION: renamed type PortalRegistrationType (Library/Components/Shared/Globals.vb:L84-L89)
//   to UserRegistrationMode, after the Portals.UserRegistration column it discriminates.
//   Member identifiers and ordinals 0-3 are UNCHANGED: they are persisted values and the legacy
//   admin UI bound them directly as a list index (Website/admin/Portal/SiteSettings.ascx.vb:L277).
//   The declaration order is therefore also unchanged. No other behaviour from the otherwise
//   out-of-scope Globals module was ported; the legacy enum was read as reference data only.
public enum UserRegistrationMode
{
    /// <summary>
    /// Registration is disabled. The portal offers no sign-up affordance at all - the register link and
    /// the registration page are both suppressed - and accounts are created only by an administrator.
    /// Displayed by the legacy screen as "None".
    /// </summary>
    NoRegistration = 0,

    /// <summary>
    /// Private registration. A visitor may submit a sign-up, but the resulting account stays
    /// unauthorised until an administrator approves it; the visitor is told that an administrator must
    /// verify their credentials
    /// (<c>Library/Components/Users/UserModuleBase.vb:L651-L655</c>). Displayed as "Private".
    /// </summary>
    PrivateRegistration = 1,

    /// <summary>
    /// Public registration. A sign-up takes effect immediately: the account is authorised without
    /// further approval and the legacy flow signed the new user in as soon as it was created
    /// (<c>Library/Components/Users/UserModuleBase.vb:L657-L661</c>). This is the value seeded into the
    /// stock <c>_default</c> portal row at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L7125</c>.
    /// Displayed as "Public".
    /// </summary>
    PublicRegistration = 2,

    /// <summary>
    /// Verified registration. A sign-up takes effect only once the address on the account is confirmed:
    /// the portal mails a verification code
    /// (<c>Library/Components/Users/UserModuleBase.vb:L662-L666</c>) that the user must present on
    /// first sign-in, and until then authentication reports the account as not yet approved
    /// (<c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L110,L170</c>).
    /// Displayed as "Verified".
    /// </summary>
    VerifiedRegistration = 3
}
