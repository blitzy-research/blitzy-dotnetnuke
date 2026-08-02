using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One user's membership of one portal - the row that makes an identity visible to a tenant.
/// </summary>
/// <remarks>
/// MIGRATION: extracts the per-portal facts that the flattened legacy <c>UserInfo</c> carried
/// alongside the identity itself. Bound to <c>dbo.UserPortals</c>: 02.02.01 dropped the original
/// <c>Authorized</c>, <c>CreatedDate</c> and <c>LastLoginDate</c> columns, 03.00.10 reinstated
/// <c>CreatedDate</c> with a <c>getdate()</c> default, and 03.02.03 added <c>Authorised</c> - the
/// British spelling, which is the terminal column name and must not be "corrected".
/// </remarks>
public sealed class UserPortal : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>UserPortalId</c>, identity from 1, added by 02.00.00).</summary>
    public int UserPortalId { get; set; }

    /// <inheritdoc />
    public override int Identity => UserPortalId;

    /// <summary>Gets or sets the user (<c>UserId</c>, required, cascade delete).</summary>
    public int UserId { get; set; }

    /// <summary>Gets or sets the portal (<c>PortalId</c>, required, cascade delete).</summary>
    public int PortalId { get; set; }

    /// <summary>Gets or sets when the user joined this portal (<c>CreatedDate</c>, required, database default <c>getdate()</c>).</summary>
    public DateTime CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets whether the membership is approved (<c>Authorised</c>, required, default true).
    /// </summary>
    public bool Authorised { get; set; } = true;

    /// <summary>Gets or sets the user.</summary>
    public User? User { get; set; }

    /// <summary>Gets or sets the portal.</summary>
    public Portal? Portal { get; set; }
}
