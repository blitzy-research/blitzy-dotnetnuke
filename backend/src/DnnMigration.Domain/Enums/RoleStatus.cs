namespace DnnMigration.Domain.Enums;

/// <summary>
/// The temporal state of a single user-to-role assignment, derived at read time from that assignment's
/// effective and expiry dates.
/// </summary>
/// <remarks>
/// <para>
/// Computed, never persisted. No <c>UserRoles</c> or <c>Roles</c> column backs this value, so no entity
/// configuration may map it - not with <c>Property</c>, <c>HasColumnName</c> or <c>HasConversion</c>,
/// anywhere.
/// </para>
/// <para>
/// The ordinal is meaningless. No explicit numeric values are assigned, precisely so that nothing can read
/// significance into the ordering, and declaration order is lifecycle order rather than a stored code.
/// </para>
/// </remarks>
public enum RoleStatus
{
    /// <summary>
    /// The assignment has been made but is not yet in force: its effective date is set and falls strictly
    /// after the current instant. Derived from the guard at <c>RoleController.vb</c> L530, which leaves a
    /// future effective date in place instead of clearing it, and from the inclusive effective-date test in
    /// <c>GetRolesByUser</c>.
    /// </summary>
    Pending,

    /// <summary>
    /// The assignment is in force: its effective date is unset or at or before the current instant, and its
    /// expiry date is unset or at or after it. This is also the result when both dates are unset.
    /// </summary>
    Active,

    /// <summary>
    /// The assignment has ended: its expiry date is set and falls strictly before the current instant. This
    /// is a retained state rather than a deleted row - the cancellation path at <c>RoleController.vb</c>
    /// L493-L501 back-dates the expiry date by one day precisely so the trial-usage row is kept.
    /// </summary>
    Expired
}
