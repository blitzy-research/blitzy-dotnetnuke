namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Response contract for a successful invitation-code redemption: the services the code enrolled the
/// account in.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: a redemption reports WHAT IT DID rather than merely that it worked. The legacy handler set a
/// single boolean and posted one of two fixed sentences - <c>RSVPSuccess</c> or <c>RSVPFailure</c>
/// (<c>Website/admin/Users/MemberServices.ascx.vb:L422-L428</c>) - so an account that redeemed a code was
/// told "you have been successfully added to the role(s) associated with the RSVP Code entered" without
/// being told which. It then rebound the grid, and the account had to find the changed rows itself.
/// </para>
/// <para>
/// MIGRATION: the plural in that sentence is not a hedge. The legacy loop had NO early exit
/// (<c>:L410-L420</c>): it walked every role of the tenant and subscribed the account to each one whose
/// code matched, so one code legitimately enrolls an account in several services. This contract carries a
/// collection for that reason and never a single value.
/// </para>
/// <para>
/// A redemption that matched nothing is a FAILED outcome carrying a stable code rather than a successful
/// empty collection, so <see cref="Roles"/> is never empty on a successful response. Reporting "success,
/// zero services" would tell a client its code was accepted when it was not.
/// </para>
/// </remarks>
public sealed class RedeemServiceCodeResultDto
{
    /// <summary>
    /// Gets or sets the services the redeemed code enrolled the account in, in the order the roles were
    /// examined.
    /// </summary>
    /// <remarks>
    /// Never empty on a successful outcome. Ordered by role name and then by role key, which is the order
    /// the subscribable-role read returns and therefore the order the catalogue displays, so a client can
    /// present the result against the grid it already shows.
    /// </remarks>
    public IReadOnlyList<RedeemedServiceDto> Roles { get; set; } = Array.Empty<RedeemedServiceDto>();
}

/// <summary>
/// One service an invitation code enrolled an account in.
/// </summary>
/// <remarks>
/// Deliberately narrow: the key so a client can locate the row in the catalogue it already holds, and the
/// name because that is what the legacy container's confirmation interpolated -
/// <c>String.Format(GetString("UserSubscribed"), e.RoleName)</c>
/// (<c>Website/admin/Users/ManageUsers.ascx.vb:L847</c>), reached through the
/// <c>SubscriptionUpdatedEventArgs</c> the panel raised (<c>MemberServices.ascx.vb:L418</c>). Anything
/// further about the service is already in the catalogue, and a second, thinner copy of it here would be a
/// second thing to keep consistent. The Web Forms event itself has no counterpart: a subscription is
/// reported to its caller in this response, and no server-side event bus is introduced to carry it.
/// </remarks>
public sealed class RedeemedServiceDto
{
    /// <summary>
    /// Gets or sets the identifier of the role the account was enrolled in.
    /// </summary>
    /// <remarks>
    /// <b>Zero is a real key</b>: <c>Roles.RoleID</c> is <c>IDENTITY(0,1)</c> at
    /// <c>01.00.00.SqlDataProvider:L114</c>.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>
    /// Gets or sets the name of the role the account was enrolled in.
    /// </summary>
    public string RoleName { get; set; } = string.Empty;
}
