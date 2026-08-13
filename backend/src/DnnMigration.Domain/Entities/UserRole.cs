using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One account's membership of one role, optionally bounded by an effective date at the start and an expiry
/// date at the end.
/// </summary>
/// <remarks>
/// A plain persistence entity for the six-column <c>dbo.UserRoles</c> link table. It holds the two foreign
/// keys, the two optional bounds and the trial flag, and it computes nothing except the temporal
/// classification returned by <see cref="GetStatus(DateTime)"/>.
/// </remarks>
public sealed class UserRole : Entity<int>
{
    /// <summary>Gets or sets the surrogate key of this membership row.</summary>
    /// <remarks>
    /// Backing column <c>UserRoleID int IDENTITY(1, 1) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> line
    /// 239), the clustered primary key <c>PK_UserRoles</c> (lines 441-446). The seed is 1, so unlike
    /// <c>dbo.Roles</c>, <c>dbo.Tabs</c> and <c>dbo.Modules</c> - each seeded at 0 - and <c>dbo.Portals</c>
    /// - seeded at -1 - the default value of this property is not a persisted key.
    /// </remarks>
    public int UserRoleId { get; set; }

    /// <inheritdoc />
    public override int Identity => UserRoleId;

    /// <summary>Gets or sets the account whose membership this row records.</summary>
    /// <remarks>
    /// Backing column <c>UserID int NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> line 240), the dependent
    /// end of <c>FK_UserRoles_Users</c> (lines 697-700), which cascades on delete so removing an account
    /// withdraws its memberships. The corresponding navigation is <see cref="User"/>.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>Gets or sets the role being granted.</summary>
    /// <remarks>
    /// Backing column <c>RoleID int NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> line 241), the dependent
    /// end of <c>FK_UserRoles_Roles</c> (lines 689-695), which also cascades on delete. The corresponding
    /// navigation is <see cref="Role"/>.
    /// </remarks>
    public int RoleId { get; set; }

    /// <summary>
    /// Gets or sets the instant at which this membership lapses, or <see langword="null"/> when it never
    /// lapses.
    /// </summary>
    /// <remarks>
    /// Backing column <c>ExpiryDate datetime NULL</c> (<c>01.00.00.SqlDataProvider</c> line 242), present
    /// since the baseline. The bound is inclusive: the terminal <c>GetRolesByUser</c> procedure admits a
    /// row while <c>ExpiryDate &gt;= getdate()</c>, so a membership expiring at this exact instant is still
    /// in force.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets whether this membership has already consumed the role's trial period, or <see
    /// langword="null"/> when nothing has been recorded either way.
    /// </summary>
    /// <remarks>
    /// Backing column <c>IsTrialUsed bit NULL</c> (<c>01.00.00.SqlDataProvider</c> line 243).
    /// </remarks>
    public bool? IsTrialUsed { get; set; }

    /// <summary>
    /// Gets or sets the instant from which this membership counts, or <see langword="null"/> when it counts
    /// immediately.
    /// </summary>
    /// <remarks>
    /// Backing column <c>EffectiveDate datetime NULL</c>, the sixth and last column of the table, added by
    /// <c>03.02.03.SqlDataProvider</c> lines 379-380 and re-added under a version guard by
    /// <c>04.00.04.SqlDataProvider</c> lines 415-419. Declared last here to match the physical column order
    /// that late arrival produced.
    /// </remarks>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>Gets or sets the account this membership belongs to.</summary>
    /// <remarks>
    /// The principal end of <c>FK_UserRoles_Users</c>, keyed by <see cref="UserId"/> and cascading on
    /// delete (<c>01.00.00.SqlDataProvider</c> lines 697-700). Its inverse is <see
    /// cref="Entities.User.UserRoles"/>.
    /// </remarks>
    public User User { get; set; }

    /// <summary>Gets or sets the role being granted.</summary>
    public Role Role { get; set; }

    /// <summary>Classifies this membership at the supplied instant.</summary>
    /// <param name="asOfUtc">The instant to classify against, supplied by the caller.</param>
    /// <returns>
    /// <see cref="RoleStatus.Expired"/> when the expiry bound is set and already in the past, <see
    /// cref="RoleStatus.Pending"/> when it is not yet expired and the effective bound is set and still in
    /// the future, and <see cref="RoleStatus.Active"/> otherwise - including when neither bound is set.
    /// </returns>
    /// <remarks>
    /// And it was harmful, because the allowance is indistinguishable from a genuine bound in the only
    /// direction that matters. This classification decides whether a membership is in force, and an
    /// administrator role assignment classified <see cref="RoleStatus.Active"/> is what the
    /// tenant-administration policy grants on.
    /// </remarks>
    public RoleStatus GetStatus(DateTime asOfUtc)
    {
        // Each test reads "the bound is set, and the instant falls strictly outside it". A bound counts as
        // set when it is not null, and nothing else - no sentinel is recognised here, for the reasons the
        // remarks set out and cite.

        // Set, and strictly before the instant: it has lapsed. Tested FIRST because expiry is terminal -
        // see the ordering paragraph in the remarks, which records why and cites the cancellation path that
        // makes the two bounds observably contradictory.
        if (ExpiryDate is DateTime expiry && expiry < asOfUtc)
        {
            return RoleStatus.Expired;
        }

        // Set, and strictly after the instant: granted, but not yet in force.
        if (EffectiveDate is DateTime effective && effective > asOfUtc)
        {
            return RoleStatus.Pending;
        }

        // In force: inside both bounds, on either bound, or bounded on neither side.
        return RoleStatus.Active;
    }

    /// <summary>
    /// Reports whether any of a set of assignments places its holder in one particular role, in force at
    /// one particular instant.
    /// </summary>
    /// <param name="assignments">The assignments to examine.</param>
    /// <param name="roleId">The role the holder must be in.</param>
    /// <param name="asOfUtc">The instant each assignment's validity window is judged against.</param>
    /// <returns><see langword="true"/> when at least one assignment names that role and is in force.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assignments"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// WHY THIS IS A DOMAIN MEMBER RATHER THAN A PREDICATE AT EACH CALL SITE. "Is this account in that role
    /// right now" is asked in two layers for two different purposes - by the API layer when it authorises a
    /// request, and by the Application layer when a service needs to know whether its caller holds
    /// administrative authority - and the two must never be able to answer differently.
    /// </remarks>
    public static bool AnyActiveInRole(
        IEnumerable<UserRole> assignments,
        int roleId,
        DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        foreach (UserRole assignment in assignments)
        {
            if (assignment.RoleId == roleId && assignment.GetStatus(asOfUtc) == RoleStatus.Active)
            {
                return true;
            }
        }

        return false;
    }
}
