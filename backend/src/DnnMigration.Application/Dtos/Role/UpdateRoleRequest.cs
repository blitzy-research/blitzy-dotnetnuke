using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>PUT /api/v1/portals/{portalId}/roles/{roleId}</c>: the replacement
/// writable state of an existing security role, including its paid-membership terms.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>RoleController.vb</c> line 254, which accepted a whole legacy entity and returned
/// nothing at all, so a caller could not distinguish an applied update from a silently discarded
/// one. The owning member returns the updated role, which is what lets the endpoint answer 200 with
/// the new state.
/// </para>
/// <para>
/// This is a full replacement, not a patch. Every writable member is applied as supplied, so
/// omitting a nullable member clears the stored value rather than preserving it. A caller
/// performing a partial edit must therefore read the role first and resubmit the members it does
/// not intend to change - the same discipline the legacy editor followed by loading the role into
/// its form before posting it back.
/// </para>
/// <para>
/// <b>Neither identifier is carried in the body, deliberately.</b> The portal and the role are both
/// taken from the route, which is what makes them authoritative. <c>IRoleService</c> permits a body
/// identifier only conditionally - it requires that "any identifier carried by the request must
/// agree" with the route - and this contract satisfies that requirement by carrying none, which is
/// the stronger of the two options: a value that does not exist cannot disagree, so no equality
/// check is needed, no ambiguity about which value wins can arise, and the tenant identifier in
/// particular is never restatable by a caller. Omitting it also avoids reproducing the redundant
/// body identifier that the portal update contract carries.
/// </para>
/// <para>
/// Two service-enforced rules apply. The new name must not already be taken by a <em>different</em>
/// role in the same portal, reported as <c>role.name_duplicate</c>; and a named role group must
/// exist in that portal, reported as <c>role_group.not_found</c>.
/// </para>
/// <para>
/// Changing the billing or trial terms here does <em>not</em> retrospectively re-compute the expiry
/// of assignments already in force. The terms are re-read on the next assignment or renewal, which
/// is the legacy behaviour and is preserved deliberately.
/// </para>
/// <para>
/// The type is an inert data carrier; field rules live in <c>Application/Validation</c>.
/// </para>
/// </remarks>
public sealed class UpdateRoleRequest
{
    /// <summary>
    /// Replacement name of the role. Required, and unique within the owning portal.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.RoleName nvarchar(50) NOT NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2750), covered with <c>PortalID</c> by the uniqueness
    /// constraint <c>IX_RoleName</c> (<c>03.00.09.SqlDataProvider</c> line 304).
    /// </remarks>
    // MIGRATION: the duplicate-name check on update must exclude the role being updated, or
    // resubmitting an unchanged name would falsely conflict with itself. That exclusion is the
    // service's responsibility and is why the rule is a reason code rather than a boundary rule.
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Replacement description, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.Description nvarchar(1000) NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2751).
    /// </remarks>
    // MIGRATION: because this is a replacement rather than a patch, a null CLEARS the stored
    // description. Null and the empty string remain distinct and neither is converted into the
    // other.
    public string? Description { get; set; }

    /// <summary>
    /// Replacement recurring fee, or <see langword="null"/> to record no fee.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.ServiceFee money NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2752, re-asserted at
    /// <c>03.01.01.SqlDataProvider</c> line 1173).
    /// </remarks>
    // MIGRATION: decimal rather than float, because money is a fixed-point type. Changing the fee
    // does not re-price assignments already in force; the value is re-read at the next assignment
    // or renewal.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Replacement number of billing frequency units between charges, or <see langword="null"/> for
    /// no recurring billing term.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.BillingPeriod int NULL</c> (<c>01.00.08.SqlDataProvider</c> line
    /// 6829).
    /// </remarks>
    // MIGRATION: clearing this member removes the billing term entirely, because an absent period
    // short-circuits the expiry derivation before the frequency is consulted. Sending 0 is a
    // different instruction and is not equivalent.
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Replacement unit of the recurring billing term, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.BillingFrequency char(1) NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2753).
    /// </remarks>
    // MIGRATION: accepted from the wire as the legacy single CHARACTER rather than as a numeric
    // code point, through the converter applied centrally at the Api edge. The enumeration is
    // shared with TrialFrequency.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Replacement trial fee, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.TrialFee money NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6830).
    /// </remarks>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Replacement number of trial frequency units, or <see langword="null"/> for no trial.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.TrialPeriod int NULL</c> (<c>01.00.05.SqlDataProvider</c> line
    /// 2754).
    /// </remarks>
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Replacement unit of the trial term, or <see langword="null"/> for no trial.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.TrialFrequency char(1) NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2755).
    /// </remarks>
    // MIGRATION: revising the trial terms does not un-consume a trial an assignment has already
    // used. The consumed flag lives on the assignment row, not on the role, so an existing member
    // whose trial is spent continues to be governed by the billing terms.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// <see langword="true"/> when users may subscribe to the role themselves.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.IsPublic bit NOT NULL</c> with store default 0
    /// (<c>01.00.08.SqlDataProvider</c> line 6831).
    /// </remarks>
    // MIGRATION: non-nullable, so a request that omits this member sets the role PRIVATE rather
    // than leaving it as it was - the direct consequence of this being a replacement rather than a
    // patch. Clearing a public flag by omission is the more conservative outcome, but it is still
    // a change, so a caller performing a partial edit must resubmit the current value.
    public bool IsPublic { get; set; }

    /// <summary>
    /// <see langword="true"/> to enrol every user of the portal in the role automatically.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.AutoAssignment bit NOT NULL</c> with store default 0
    /// (<c>01.00.08.SqlDataProvider</c> line 6832).
    /// </remarks>
    // MIGRATION: non-nullable, so omitting it clears the flag. Note the asymmetry with creation,
    // which is legacy behaviour and is preserved: the legacy auto-assign helper ran on insert
    // only, so clearing this flag on an update does NOT retrospectively remove the users it
    // previously enrolled, and setting it on an update is not documented by the legacy as
    // enrolling them either. Existing assignments are managed through the assignment members.
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Identifier of the role group to place the role in, or <see langword="null"/> to leave it
    /// ungrouped.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.RoleGroupID int NULL</c>, constrained by
    /// <c>FK_Roles_RoleGroups</c> (<c>03.02.03.SqlDataProvider</c> lines 34 and 37).
    /// </remarks>
    // MIGRATION: null means ungrouped and clears any existing group. The -1 sentinel must never be
    // submitted, because RoleGroups.RoleGroupID is seeded IDENTITY(0,1) and so 0 is a real group.
    // A group absent from the owning portal is reported as role_group.not_found.
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// Replacement redemption code, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.RSVPCode nvarchar(50) NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> line 45).
    /// </remarks>
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Replacement relative icon path, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.IconFile nvarchar(100) NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> line 45).
    /// </remarks>
    // MIGRATION: constrained by the validator for length and rejected for rooted and traversing
    // forms, on the same terms as the creation contract; path resolution remains a client concern.
    public string? IconFile { get; set; }
}
