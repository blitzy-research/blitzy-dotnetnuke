using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// dbo.UserPortals - the row that makes one account a member of one tenant.

/// <summary>
/// One account's membership of one portal - the <c>dbo.UserPortals</c> row that makes an identity visible
/// to a tenant, records when it joined and whether the tenant admits it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two notions of identity, and why they must not be conflated.</b> The database primary key of
/// <c>dbo.UserPortals</c> is the composite <c>(UserId, PortalId)</c> declared as the clustered
/// <c>PK_UserPortals</c> in the baseline script (<c>01.00.00.SqlDataProvider</c> lines 405-411) and never
/// replaced since.
/// </para>
/// <para>
/// <see cref="Identity"/> deliberately returns <see cref="UserPortalId"/>, because <see
/// cref="Entity{TId}"/> compares a single scalar and the surrogate is the only single scalar that
/// distinguishes two memberships of the same account. That choice is an equality concern and carries no
/// mapping authority whatsoever.
/// </para>
/// </remarks>
public sealed class UserPortal : Entity<int>
{
    // PERSISTED SCALARS (5): one property per terminal column and no property that is not a terminal
    // column, declared surrogate first so the Identity projection sits beside the value it projects.

    /// <summary>
    /// Gets or sets the store-generated surrogate key of this membership row (<c>UserPortalId</c>).
    /// </summary>
    /// <remarks>
    /// Mapped to <c>UserPortalId int NOT NULL IDENTITY (1, 1)</c>, added at <c>02.00.00.SqlDataProvider</c>
    /// lines 7209-7210. It is unique, and it is <b>not</b> the primary key - see the class remarks.
    /// </remarks>
    public int UserPortalId { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see cref="UserPortalId"/>. The surrogate is used rather than either key column because <see
    /// cref="Entity{TId}"/> compares one scalar, and neither <see cref="UserId"/> nor <see
    /// cref="PortalId"/> identifies a membership on its own - an account with two tenancies would otherwise
    /// report both rows as the same entity.
    /// </remarks>
    public override int Identity => UserPortalId;

    /// <summary>Gets or sets the identifier of the account this membership belongs to (<c>UserId</c>).</summary>
    /// <remarks>
    /// Mapped to <c>UserId int NOT NULL</c> from the baseline table (<c>01.00.00.SqlDataProvider</c> lines
    /// 153-157).
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>Gets or sets the identifier of the portal this membership is scoped to (<c>PortalId</c>).</summary>
    /// <remarks>
    /// Mapped to <c>PortalId int NOT NULL</c> from the baseline table (<c>01.00.00.SqlDataProvider</c>
    /// lines 153-157).
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this portal admits the account - the ordinary user's
    /// approval flag (<c>Authorised</c>).
    /// </summary>
    /// <remarks>
    /// Mapped to the British-spelled column <c>Authorised</c>, and only to that name. The terminal
    /// declaration is <c>Authorised bit NOT NULL CONSTRAINT DF_UserPortals_Authorised DEFAULT 1</c>
    /// (<c>03.02.03.SqlDataProvider</c> lines 637-642, re-asserted for skipped lineages at
    /// <c>04.00.04.SqlDataProvider</c> lines 680-684).
    /// </remarks>
    public bool IsAuthorised { get; set; } = true;

    /// <summary>Gets or sets the instant at which the account joined this portal (<c>CreatedDate</c>).</summary>
    /// <remarks>
    /// Mapped to <c>CreatedDate datetime NOT NULL</c> with the store default <c>DF_UserPortals_CreatedDate
    /// DEFAULT (getdate())</c>.
    /// </remarks>
    public DateTime CreatedDate { get; set; }

    // NAVIGATIONS (2): both required, because each relationship's foreign-key column is NOT NULL and
    // cascades, and both declared non-nullable to say so in the type system.

    /// <summary>Gets or sets the account this membership belongs to.</summary>
    public User User { get; set; }

    /// <summary>Gets or sets the portal this membership is scoped to.</summary>
    /// <remarks>
    /// The principal end of <c>FK_UserPortals_Portals</c>, keyed by <see cref="PortalId"/> and cascading on
    /// delete (<c>01.00.00.SqlDataProvider</c> lines 616-622). Its inverse is <see
    /// cref="Portal.UserPortals"/>.
    /// </remarks>
    public Portal Portal { get; set; }
}
