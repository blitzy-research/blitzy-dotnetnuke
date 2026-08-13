namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Response contract for a successful invitation-code redemption: the services the code enrolled the
/// account in.
/// </summary>
/// <remarks>
/// The plural in that sentence is not a hedge. The legacy loop had NO early exit (<c>:L410-L420</c>): it
/// walked every role of the tenant and subscribed the account to each one whose code matched, so one code
/// legitimately enrolls an account in several services.
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

/// <summary>One service an invitation code enrolled an account in.</summary>
public sealed class RedeemedServiceDto
{
    /// <summary>Gets or sets the identifier of the role the account was enrolled in.</summary>
    /// <remarks>
    /// <b>Zero is a real key</b>: <c>Roles.RoleID</c> is <c>IDENTITY(0,1)</c> at
    /// <c>01.00.00.SqlDataProvider:L114</c>.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>Gets or sets the name of the role the account was enrolled in.</summary>
    public string RoleName { get; set; } = string.Empty;
}
