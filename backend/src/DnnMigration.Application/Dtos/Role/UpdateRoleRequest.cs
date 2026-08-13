using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>PUT /api/v1/roles/{roleId}</c>: the writable state of an existing security role,
/// including its paid-membership terms.
/// </summary>
/// <remarks>
/// Fourteen members: thirteen writable values — the role's name plus the twelve the terminal
/// <c>UpdateRole</c> procedure assigns (<c>04.00.04.SqlDataProvider</c> L454) — plus
/// <see cref="ConcurrencyToken"/>, which is compared rather than written. The owning portal is addressed by
/// the route and is not restated in the body.
/// </remarks>
// Four groups of members are absent by design.
public sealed class UpdateRoleRequest
{
    /// <summary>Replacement name of the role. Required, and unique within the owning portal.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleName</c>, capped at fifty characters by its <c>MaxLength</c>.
    /// Terminal column <c>Roles.RoleName nvarchar(50) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> L117),
    /// covered together with the portal column by the uniqueness constraint <c>IX_RoleName</c>
    /// (<c>03.00.09.SqlDataProvider</c> L304).
    /// </remarks>
    // Non-nullable, and initialised to the empty string rather than to a null-forgiving default, because
    // the column is NOT NULL and a request that omits the name is a MISSING required field rather than a
    // null one.
    public string RoleName { get; set; } = string.Empty;

    /// <summary>Replacement description of the role, or <see langword="null"/> to clear it.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at one thousand characters.
    /// Terminal column <c>Roles.Description nvarchar(1000) NULL</c> (<c>01.00.00.SqlDataProvider</c> line
    /// 118).
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Identifier of the role group to place the role in, or <see langword="null"/> to leave it ungrouped -
    /// the state the legacy screen labelled "Global Roles".
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboRoleGroups</c>. Terminal column <c>Roles.RoleGroupID int
    /// NULL</c>, added at <c>03.02.03.SqlDataProvider</c> line 34 and constrained by the foreign key at
    /// line 37 against <c>RoleGroups.RoleGroupID</c>, itself seeded <c>IDENTITY(0,1) NOT NULL</c> at line
    /// 18 of the same script.
    /// </remarks>
    // Two traps follow. Zero is a LEGITIMATE group identifier, because the referenced key is seeded
    // IDENTITY(0,1), so a non-positive test must never stand in for an absence test on this member.
    public int? RoleGroupId { get; set; }

    /// <summary><see langword="true"/> when users of the portal may subscribe to the role themselves.</summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkIsPublic</c>. Terminal column <c>Roles.IsPublic bit NOT NULL</c>
    /// (<c>01.00.08.SqlDataProvider</c> line 6831), re-asserted as not-nullable with a store default of
    /// zero at <c>03.01.01.SqlDataProvider</c> lines 1174 and 1179.
    /// </remarks>
    // Not a nullable boolean, because the column is NOT NULL and the legacy checkbox always posted a
    // definite state.
    public bool IsPublic { get; set; }

    /// <summary><see langword="true"/> to enrol every user of the portal in the role automatically.</summary>
    /// <remarks>
    /// Screen control <c>asp:CheckBox chkAutoAssignment</c>. Terminal column <c>Roles.AutoAssignment bit
    /// NOT NULL</c> (<c>01.00.08.SqlDataProvider</c> line 6832), re-asserted as not-nullable with a store
    /// default of zero at <c>03.01.01.SqlDataProvider</c> lines 1175 and 1181.
    /// </remarks>
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Replacement recurring fee for membership of the role, or <see langword="null"/> when the role
    /// carries no fee.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtServiceFee</c>, carrying a currency type check and a not-negative
    /// comparison. Terminal column <c>Roles.ServiceFee money NULL</c> with store default zero
    /// (<c>03.01.01.SqlDataProvider</c> lines 1173 and 1177).
    /// </remarks>
    public decimal? ServiceFee { get; set; }

    /// <summary>
    /// Replacement count of billing-frequency units between charges, or <see langword="null"/> for no
    /// recurring billing term.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtBillingPeriod</c>, carrying an integer type check and a
    /// strictly-positive comparison. Terminal column <c>Roles.BillingPeriod int NULL</c>
    /// (<c>01.00.08.SqlDataProvider</c> line 6829, with existing rows backfilled to one at lines 6900 to
    /// 6901).
    /// </remarks>
    // Null and zero are different instructions. The legacy expiry derivation short-circuited to no expiry
    // when the period equalled the null-integer sentinel, before the frequency code was examined at all,
    // whereas zero would have been carried into the arithmetic.
    public int? BillingPeriod { get; set; }

    /// <summary>Replacement unit of the recurring billing term, or <see langword="null"/> for none.</summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboBillingFrequency</c>. Terminal column
    /// <c>Roles.BillingFrequency char(1) NULL</c> (<c>01.00.05.SqlDataProvider</c> line 2753).
    /// </remarks>
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>Replacement one-off fee for the trial period, or <see langword="null"/> for none.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtTrialFee</c>, carrying a currency type check and a not-negative
    /// comparison. Terminal column <c>Roles.TrialFee money NULL</c> (<c>01.00.08.SqlDataProvider</c> line
    /// 6830).
    /// </remarks>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Replacement count of trial-frequency units the trial runs for, or <see langword="null"/> for no
    /// trial term.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtTrialPeriod</c>, carrying an integer type check and a
    /// strictly-positive comparison. Terminal column <c>Roles.TrialPeriod int NULL</c>
    /// (<c>01.00.05.SqlDataProvider</c> line 2754).
    /// </remarks>
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Replacement unit of the trial term, or <see langword="null"/> for none. The enumeration's "none"
    /// member is a real stored code meaning the role has no trial, and is not the same thing as this member
    /// being <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:DropDownList cboTrialFrequency</c>. Terminal column <c>Roles.TrialFrequency
    /// char(1) NULL</c> (<c>01.00.05.SqlDataProvider</c> line 2755).
    /// </remarks>
    // Typed with the SAME enumeration as the billing unit above, because the schema proves the two columns
    // share one code set by joining the same frequency lookup twice from a single role row. A second
    // enumeration would duplicate a single source of truth and could drift.
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>Replacement code a user may redeem to join the role, or <see langword="null"/> to clear it.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRSVPCode</c>, capped at fifty characters and carrying no validator.
    /// Terminal column <c>Roles.RSVPCode nvarchar(50) NULL</c> (<c>03.02.03.SqlDataProvider</c> line 45).
    /// </remarks>
    public string? RsvpCode { get; set; }

    /// <summary>Replacement relative path of the role's icon, or <see langword="null"/> to clear it.</summary>
    // Carried as the stored relative path exactly as the legacy column held it, with neither resolution nor
    // rooting applied here. Length and the rejection of rooted or traversing forms are the validator's
    // rules, and turning the stored path into something a browser can request is the client's concern.
    public string? IconFile { get; set; }

    /// <summary>
    /// The optimistic-concurrency token the caller received when it read this role, or <see
    /// langword="null"/> to apply the update unconditionally.
    /// </summary>
    /// <remarks>
    /// OPTIONAL, and permissive when omitted, so a caller that predates the token still works exactly as it
    /// did. When supplied and no longer current, the write is refused with <c>role.concurrency_conflict</c>
    /// and nothing is written.
    /// </remarks>
    public string? ConcurrencyToken { get; set; }
}
