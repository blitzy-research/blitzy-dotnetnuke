using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One user's membership of one role, bounded by an optional effective and expiry date.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the eight-property <c>DotNetNuke.Entities.Users.UserRoleInfo</c>. Bound to
/// <c>dbo.UserRoles</c>; <c>EffectiveDate</c> was added by 03.02.03. Both dates are nullable and a
/// null means "unbounded on that side", which is why membership is evaluated with
/// <see cref="IsEffectiveAt"/> rather than by comparing against a sentinel date.
/// </remarks>
public sealed class UserRole : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>UserRoleID</c>, identity from 1).</summary>
    public int UserRoleId { get; set; }

    /// <inheritdoc />
    public override int Identity => UserRoleId;

    /// <summary>Gets or sets the member (<c>UserID</c>, required, cascade delete).</summary>
    public int UserId { get; set; }

    /// <summary>Gets or sets the role (<c>RoleID</c>, required, cascade delete).</summary>
    public int RoleId { get; set; }

    /// <summary>Gets or sets the instant from which the membership counts (<c>EffectiveDate</c>, null meaning immediately).</summary>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>Gets or sets the instant at which the membership lapses (<c>ExpiryDate</c>, null meaning never).</summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Gets or sets whether the member has already consumed the role's trial period (<c>IsTrialUsed</c>, nullable).</summary>
    public bool? IsTrialUsed { get; set; }

    /// <summary>Gets or sets the member.</summary>
    public User? User { get; set; }

    /// <summary>Gets or sets the role.</summary>
    public Role? Role { get; set; }

    /// <summary>
    /// Determines whether this membership is in force at the supplied instant.
    /// </summary>
    /// <param name="instant">The instant to test, normally the current UTC time from the injected clock.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="instant"/> is at or after
    /// <see cref="EffectiveDate"/> (or that date is absent) and strictly before
    /// <see cref="ExpiryDate"/> (or that date is absent).
    /// </returns>
    public bool IsEffectiveAt(DateTime instant) =>
        (EffectiveDate is null || EffectiveDate.Value <= instant)
        && (ExpiryDate is null || ExpiryDate.Value > instant);
}
