namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how a portal admits new user accounts. This is the discriminator persisted in the
/// <c>Portals.UserRegistration</c> column and presented to administrators as the "User Registration"
/// security setting.
/// </summary>
/// <remarks>
/// <para>
/// The backing column reaches a terminal shape of <c>int NOT NULL</c> with a database default of zero, so
/// there is no absent case to model and the zero member is a real, chosen mode rather than a stand-in for a
/// missing value.
/// </para>
/// <para>
/// No serialisation, mapping, validation or display attribute is declared here: the Domain layer takes no
/// reference, so column mapping belongs to the Infrastructure configuration for <c>Portals</c>, the wire
/// representation to the Application-layer DTOs, and the label to the client.
/// </para>
/// </remarks>
public enum UserRegistrationMode
{
    /// <summary>
    /// Registration is disabled. The portal offers no sign-up affordance at all - the register link and the
    /// registration page are both suppressed - and accounts are created only by an administrator.
    /// </summary>
    NoRegistration = 0,

    /// <summary>
    /// Private registration. A visitor may submit a sign-up, but the resulting account stays unauthorised
    /// until an administrator approves it; the visitor is told that an administrator must verify their
    /// credentials.
    /// </summary>
    PrivateRegistration = 1,

    /// <summary>
    /// Public registration. A sign-up takes effect immediately: the account is authorised without further
    /// approval, and the legacy flow signed the new user in as soon as it was created.
    /// </summary>
    PublicRegistration = 2,

    /// <summary>
    /// Verified registration. A sign-up takes effect only once the address on the account is confirmed: the
    /// portal mails a verification code that the user must present on first sign-in, and until then
    /// authentication reports the account as not yet approved.
    /// </summary>
    VerifiedRegistration = 3
}
