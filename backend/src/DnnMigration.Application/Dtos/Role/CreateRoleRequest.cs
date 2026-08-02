using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>POST /api/v1/portals/{portalId}/roles</c>: the writable state of a new
/// security role, including its paid-membership terms.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the creating half of <c>RoleController.vb</c> line 96, which accepted a whole legacy
/// entity and therefore accepted members a caller has no business setting. This contract carries
/// exactly the thirteen writable columns of <c>dbo.Roles</c> and omits the two that a caller may
/// not supply.
/// </para>
/// <para>
/// <c>RoleID</c> is omitted because it is an identity column the store assigns.
/// <c>PortalID</c> is omitted because the owning portal is taken from the route, which is what
/// makes it authoritative for tenant scoping - accepting it in the body as well would create a
/// second, contradictable source of truth for the tenant, and the tenant is the one value a
/// multi-tenant write must never let a caller restate.
/// </para>
/// <para>
/// Two rules the legacy screen performed for itself are enforced by the service rather than left
/// to the caller. The role name must be unique within the portal, reproducing the lookup-then-
/// insert guard at <c>Website/admin/Security/EditRoles.ascx.vb</c> lines 252 and 253, and reported
/// as the expected failure <c>role.name_duplicate</c>. And setting
/// <see cref="AutoAssignment"/> also enrols the portal's existing users, reproducing the private
/// helper the legacy invoked immediately after a successful insert
/// (<c>RoleController.vb</c> line 100).
/// </para>
/// <para>
/// The type is an inert data carrier. Field rules - lengths, ranges and the frequency-and-period
/// pairings - live in <c>Application/Validation</c>, and a shape violation is reported as a
/// validation problem at the boundary rather than as a reason code.
/// </para>
/// </remarks>
public sealed class CreateRoleRequest
{
    /// <summary>
    /// Name of the role. Required, and unique within the owning portal.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.RoleName nvarchar(50) NOT NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2750), covered with <c>PortalID</c> by the uniqueness
    /// constraint <c>IX_RoleName</c> (<c>03.00.09.SqlDataProvider</c> line 304).
    /// </remarks>
    // MIGRATION: the fifty-character ceiling is a schema fact and is reproduced declaratively by
    // the validator, so an overlong name is a field-level 400 rather than a truncation or a
    // database error. Uniqueness cannot be decided at the boundary because it requires a read, so
    // it is a service-reported reason code instead.
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text description of the role, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.Description nvarchar(1000) NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2751).
    /// </remarks>
    // MIGRATION: null and the empty string are distinct on this contract and neither is silently
    // converted into the other. The legacy text sentinel was the empty string, so a legacy write
    // could not express "no description"; this one can, and a null is stored as a SQL null.
    public string? Description { get; set; }

    /// <summary>
    /// Recurring fee charged for membership, or <see langword="null"/> when the role is free.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.ServiceFee money NULL</c> with the store default
    /// <c>DF_Roles_ServiceFee DEFAULT (0)</c> (<c>01.00.05.SqlDataProvider</c> lines 2752 and
    /// 2760).
    /// </remarks>
    // MIGRATION: decimal rather than float, because the column is the fixed-point money type; the
    // legacy member was declared As Single over a superseded decimal(5,2) column. A negative fee
    // is rejected by the validator, reproducing the currency comparison validator the legacy
    // editor declared on this field.
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Number of billing frequency units between charges, or <see langword="null"/> when the role
    /// has no recurring billing term.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.BillingPeriod int NULL</c> (<c>01.00.08.SqlDataProvider</c> line
    /// 6829).
    /// </remarks>
    // MIGRATION: null is load-bearing and is not interchangeable with 0. An absent period
    // short-circuits the entire expiry derivation before the frequency code is consulted, so a
    // caller sending 0 asks for a zero-length term whereas a caller sending null asks for no term
    // at all.
    public int? BillingPeriod { get; set; }

    /// <summary>
    /// Unit of the recurring billing term, or <see langword="null"/> when the role has none.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.BillingFrequency char(1) NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2753).
    /// </remarks>
    // MIGRATION: the single-character codes are load-bearing data preserved as an
    // explicitly-valued enumeration, shared with TrialFrequency because the legacy queries
    // resolved both columns against one frequency lookup. Accepted from the wire as the legacy
    // CHARACTER rather than as a numeric code point; the converter is applied centrally at the Api
    // edge and no attribute appears here.
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Fee charged for the trial period, or <see langword="null"/> when the role offers no trial
    /// fee.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.TrialFee money NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6830).
    /// </remarks>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Number of trial frequency units the trial runs for, or <see langword="null"/> when the role
    /// offers no trial.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.TrialPeriod int NULL</c> (<c>01.00.05.SqlDataProvider</c> line
    /// 2754).
    /// </remarks>
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Unit of the trial term, or <see langword="null"/> when the role offers no trial.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.TrialFrequency char(1) NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2755).
    /// </remarks>
    // MIGRATION: the trial terms take precedence over the billing terms when computing an
    // assignment's expiry, but only while the assignment's trial has not been consumed and this
    // frequency is not the never code. That precedence is service behaviour reproducing
    // RoleController.vb lines 503 to 558; nothing about it is decided here.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>
    /// <see langword="true"/> when users may subscribe to the role themselves.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.IsPublic bit NOT NULL</c> with store default 0
    /// (<c>01.00.08.SqlDataProvider</c> line 6831).
    /// </remarks>
    // MIGRATION: non-nullable, and the CLR default of false agrees with the store default, so a
    // request that omits this member creates a private role - the same outcome the legacy editor
    // produced from an unchecked box. The agreement is what makes a plain bool safe here.
    public bool IsPublic { get; set; }

    /// <summary>
    /// <see langword="true"/> to enrol every user of the portal in the role automatically.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.AutoAssignment bit NOT NULL</c> with store default 0
    /// (<c>01.00.08.SqlDataProvider</c> line 6832).
    /// </remarks>
    // MIGRATION: this member is not inert. Setting it causes the service to enrol the portal's
    // existing users as part of the same operation, reproducing RoleController.vb line 100, so a
    // create that sets it writes rows to UserRoles as well as to Roles. The CLR default of false
    // agrees with the store default, so omitting it creates the role without enrolling anyone.
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Identifier of the role group to place the role in, or <see langword="null"/> to leave it
    /// ungrouped.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.RoleGroupID int NULL</c>, constrained by
    /// <c>FK_Roles_RoleGroups</c> (<c>03.02.03.SqlDataProvider</c> lines 34 and 37).
    /// </remarks>
    // MIGRATION: null means ungrouped. The legacy screens expressed no-group with the -1 integer
    // sentinel, which must never be submitted here: RoleGroups.RoleGroupID is seeded
    // IDENTITY(0,1), so 0 is a real group and a negative value is not a sentinel but simply
    // invalid. A group that does not exist in the owning portal is reported as
    // role_group.not_found.
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// Code a user may redeem to be granted the role, or <see langword="null"/> when the role has
    /// none.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.RSVPCode nvarchar(50) NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> line 45).
    /// </remarks>
    // MIGRATION: nothing in the schema makes this code unique, so it is not an identifier and the
    // service does not treat a clash as a conflict. The property is spelled RsvpCode in the
    // target's casing convention while the column remains RSVPCode.
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Relative path of the icon that represents the role, or <see langword="null"/> when it has
    /// none.
    /// </summary>
    /// <remarks>
    /// Target column <c>Roles.IconFile nvarchar(100) NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> line 45).
    /// </remarks>
    // MIGRATION: stored as the supplied relative path. It is a path written into a
    // hundred-character column, so the validator constrains its length and rejects rooted and
    // traversing forms rather than trusting the caller; resolution against the portal home
    // directory is a presentation concern performed by the client, not here.
    public string? IconFile { get; set; }
}
