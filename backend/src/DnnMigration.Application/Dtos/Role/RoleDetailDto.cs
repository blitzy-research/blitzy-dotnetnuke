using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// The complete persisted state of one security role, served by <c>GET /api/v1/roles/{roleId}</c> and
/// returned by the create and update endpoints so a caller observes the stored result of its own write.
/// </summary>
/// <remarks>
/// <para>
/// Fifteen members: fourteen legacy-derived fields, each a real terminal column on <c>dbo.Roles</c>, plus
/// <see cref="ConcurrencyToken"/>. <c>PortalId</c> is deliberately omitted — the owning portal is addressed
/// by the route, so the body never restates it.
/// </para>
/// <para>
/// <see cref="RoleListItemDto"/> is the narrower grid projection, dropping the group identifier, the
/// invitation code and the icon path; the editable subsets are <see cref="CreateRoleRequest"/> and
/// <see cref="UpdateRoleRequest"/>. This type is declared independently of all three rather than inheriting
/// from any of them.
/// </para>
/// </remarks>
public sealed class RoleDetailDto
{
    /// <summary>Primary key of the role.</summary>
    // Identifier trap - never test this member for absence. The column is seeded IDENTITY (0, 1), so the
    // first role inserted carries identifier ZERO and zero is a perfectly legitimate, addressable role; any
    // guard treating zero, a non-positive range or the type default as "absent" rejects a real role.
    public int RoleId { get; set; }

    /// <summary>
    /// The role group this role is filed under, or <see langword="null"/> when the role belongs to no group
    /// - the case the legacy editor presented as "Global Roles".
    /// </summary>
    // Nullable, resolving what looks like a contradiction between the legacy user interface and the legacy
    // schema.
    public int? RoleGroupId { get; set; }

    /// <summary>Display name of the role, unique within its portal.</summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>Free-text description of the role.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The unit in which the billing cycle is measured, or <see langword="null"/> when no billing frequency
    /// is recorded.
    /// </summary>
    // Expiry arithmetic is NOT performed here and must never be.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// The recurring subscription fee for the role, or <see langword="null"/> when the role is free.
    /// </summary>
    // Null is faithful, not a modernisation: the legacy Single sentinel was Single.MinValue, and the grid's
    // FormatPrice helper rendered an absent fee as a blank cell. No sentinel value is carried into this
    // contract.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// The unit in which the trial period is measured, or <see langword="null"/> when no trial frequency is
    /// recorded.
    /// </summary>
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// How many <see cref="TrialFrequency"/> units the trial spans, or <see langword="null"/> when the role
    /// offers no trial.
    /// </summary>
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// How many <see cref="BillingFrequency"/> units one billing cycle spans, or <see langword="null"/>
    /// when the role is free.
    /// </summary>
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// The fee charged for the trial period, or <see langword="null"/> when the trial is free or the role
    /// offers none.
    /// </summary>
    public decimal? TrialFee { get; set; }

    /// <summary>Whether users may subscribe themselves to the role.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Whether every new member of the portal receives the role automatically.</summary>
    // Non-nullable, for the same reason as IsPublic. The column is not merely decorative:
    // 01.00.08.SqlDataProvider sets it to 1 for the role named "Registered Users" immediately after adding
    // it, so a migrated installation arrives with the flag already meaningful on a well-known role.
    public bool AutoAssignment { get; set; }

    /// <summary>The invitation code that lets a user self-assign the role.</summary>
    // MIGRATION: the derived invitation LINK is not carried, and this is the only member it concerned.
    public string? RsvpCode { get; set; }

    /// <summary>Path of the role's icon.</summary>
    // Nullable string, with the same empty-string-versus-null caveat as Description, resolved by
    // RoleMappings rather than here.
    public string? IconFile { get; set; }

    /// <summary>
    /// The optimistic-concurrency token for this record: send it back on an update to be refused rather
    /// than to silently overwrite an edit someone else committed in the meantime.
    /// </summary>
    /// <remarks>
    /// Derived from the record's own current values rather than stored in a column, because the legacy
    /// schema is immutable under the migration's Rule T4 and carries no version column on this table. The
    /// derivation lives in <c>RoleMappings.ConcurrencyTokenFor</c> and is used by both the read that
    /// publishes the token and the write that verifies it.
    /// </remarks>
    public string ConcurrencyToken { get; set; } = string.Empty;
}
