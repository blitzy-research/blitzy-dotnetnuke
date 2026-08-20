using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>One row of the security-roles listing served by <c>GET /api/v1/roles</c>.</summary>
/// <remarks>
/// <para>
/// The member set is the legacy grid, not the legacy entity. The eleven members and their declaration order
/// are measured from <c>Website/admin/Security/roles.ascx</c>, so rendering them in declaration order
/// reproduces the legacy column order.
/// </para>
/// <para>
/// The type is inert: no validation, no persistence and no serialisation attributes, and the legacy
/// element-name serialisation attributes are dropped.
/// </para>
/// </remarks>
// The two frequency members carry the stored CODE, not the lookup's display text.
public sealed class RoleListItemDto
{
    /// <summary>
    /// Primary key of the role, carried by the legacy grid as the key of both action affordances rather
    /// than as a rendered column.
    /// </summary>
    // Identifier trap. The column is seeded IDENTITY(0,1), so the first role ever inserted carries
    // identifier zero and zero is a legitimate addressable role.
    public int RoleId { get; set; }

    /// <summary>
    /// Display name of the role, and the natural default ordering for the paged endpoint because the
    /// terminal listing procedure sorts on this column.
    /// </summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>Free-text description of the role.</summary>
    public string? Description { get; set; }

    /// <summary>Recurring subscription fee for the role, or <see langword="null"/> when the role is free.</summary>
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Number of billing units between charges, or <see langword="null"/> when the role is free. It is a
    /// multiplier over <see cref="BillingFrequency"/> and is meaningless alone.
    /// </summary>
    // A nullable integer, never text.
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Unit the billing period is counted in, or <see langword="null"/> when no code is stored against the
    /// role.
    /// </summary>
    // The codes are LOAD-BEARING DATA - the literal bytes already sitting in this column in every existing
    // database - so the shared domain enumeration values its members at those code points and no member is
    // ever renamed or renumbered.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>Fee charged for the trial period, or <see langword="null"/> when the role offers no trial.</summary>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Number of trial units before the first charge, or <see langword="null"/> when the role offers no
    /// trial. It is a multiplier over <see cref="TrialFrequency"/> and is meaningless alone.
    /// </summary>
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Unit the trial period is counted in, or <see langword="null"/> when no code is stored against the
    /// role. Null is never the same thing as the <c>None</c> member, for the reason given on <see
    /// cref="BillingFrequency"/>.
    /// </summary>
    // This member deliberately reuses the SAME shared enumeration as its billing counterpart, and no
    // trial-specific type exists.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// Whether the role is publicly visible, so that a user may subscribe to it themselves rather than
    /// being assigned to it by an administrator.
    /// </summary>
    // The wire type changes from text to a real boolean.
    public bool IsPublic { get; set; }

    /// <summary>Whether the role is granted automatically to every new user of the portal.</summary>
    public bool AutoAssignment { get; set; }
}
