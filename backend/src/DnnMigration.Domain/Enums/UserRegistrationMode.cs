namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how a portal admits new user accounts. This is the discriminator persisted in the
/// <c>Portals.UserRegistration</c> column and presented to administrators as the "User Registration"
/// security setting.
/// </summary>
/// <remarks>
/// <para>
/// The backing column reaches a terminal shape of <c>int NOT NULL</c> with a database default of
/// zero, so there is no absent case to model and the zero member is a real, chosen mode rather than
/// a stand-in for a missing value.
/// </para>
/// <para>
/// The ordinals are live persisted data and must never be renumbered, and the declaration order must
/// never change either: the legacy administration screen bound the stored integer straight to a
/// radio-button list index - <c>optUserRegistration.SelectedIndex = objPortal.UserRegistration</c> at
/// <c>Website/admin/Portal/SiteSettings.ascx.vb</c> L277, written back from the same index at L773 -
/// so each member's position in this list is part of the stored contract.
/// </para>
/// <para>
/// All four members are reachable from live legacy code, so none may be dropped:
/// <see cref="NoRegistration"/> gates the sign-up affordance on the skin and login controls, and the
/// remaining three drive the post-creation branch at
/// <c>Library/Components/Users/UserModuleBase.vb</c> L651, L657 and L662 and its counterpart in the
/// user-management screen.
/// </para>
/// <para>
/// No serialisation, mapping, validation or display attribute is declared here: the Domain layer
/// takes no reference, so column mapping belongs to the Infrastructure configuration for
/// <c>Portals</c>, the wire representation to the Application-layer DTOs, and the label to the client.
/// </para>
/// </remarks>
// MIGRATION: renamed from the legacy PortalRegistrationType, declared in the otherwise out-of-scope
// Globals module (Library/Components/Shared/Globals.vb L84-L89) and read as reference data only, to
// UserRegistrationMode after the Portals.UserRegistration column it discriminates. Member
// identifiers, ordinals 0-3 and declaration order are unchanged because they are persisted values
// that the legacy admin UI bound directly as a list index. It replaces the untyped Integer property
// at Library/Components/Portal/PortalInfo.vb L125; no other Globals behaviour was ported.
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
    /// further approval, and the legacy flow signed the new user in as soon as it was created
    /// (<c>Library/Components/Users/UserModuleBase.vb</c> L657-L661). This is the value seeded into
    /// the stock <c>_default</c> portal row. Displayed as "Public".
    /// </summary>
    PublicRegistration = 2,

    /// <summary>
    /// Verified registration. A sign-up takes effect only once the address on the account is confirmed:
    /// the portal mails a verification code (<c>Library/Components/Users/UserModuleBase.vb</c>
    /// L662-L666) that the user must present on first sign-in, and until then authentication reports
    /// the account as not yet approved. Displayed as "Verified".
    /// </summary>
    VerifiedRegistration = 3
}
