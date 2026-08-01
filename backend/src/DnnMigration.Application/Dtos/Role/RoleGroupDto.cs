namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Wire contract for a DotNetNuke role group: the portal-scoped container that
/// gathers security roles together so an administrator can present and manage
/// them as a set.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>Library/Components/Security/Roles/RoleGroupInfo.vb</c>, which
/// declares exactly four public members, at lines 53, 68, 83 and 98. This
/// contract mirrors those four and adds nothing: the legacy class is a flat
/// projection of the <c>RoleGroups</c> table, with no navigation members, no
/// audit columns and no derived totals. Unlike its sibling <c>RoleInfo</c>, the
/// legacy class carries no serialisation metadata whatsoever, so nothing had to
/// be stripped in translation and nothing is introduced here either; the property
/// names alone express the wire shape.
/// </para>
/// <para>
/// The backing column definitions come from the terminal state of the upgrade
/// chain rather than from the baseline script, because <c>RoleGroups</c> is
/// absent from <c>01.00.00.SqlDataProvider</c> entirely. The table is created
/// twice, under <c>{databaseOwner}{objectQualifier}</c> templating and guarded by
/// an existence check, at <c>03.02.03.SqlDataProvider</c> line 16 and
/// <c>04.00.04.SqlDataProvider</c> line 49. Both declare identical columns, a
/// non-clustered primary key over <c>RoleGroupID</c>, and a composite uniqueness
/// constraint over <c>(PortalID, RoleGroupName)</c>.
/// </para>
/// <para>
/// One shape serves reads and writes alike for the <c>/api/v1/role-groups</c>
/// resource; there is deliberately no separate creation or update variant,
/// because the legacy editor posted the very same four fields in both cases
/// (<c>EditGroups.ascx.vb</c> lines 107 to 111). The type is an inert data
/// carrier: it holds no behaviour, performs no validation and reaches no
/// database. Field rules live in the FluentValidation validators under
/// <c>Application/Validation</c>, and translation to and from the persisted model
/// lives in <c>Application/Mapping/RoleMappings.cs</c>.
/// </para>
/// <para>
/// Two legacy affordances are intentionally absent. The role tally the legacy
/// editor computed at <c>EditGroups.ascx.vb</c> line 75 served only to hide the
/// delete button while a group still held roles; it was never persisted and was
/// never a member of the legacy class, so it stays a server-side concern and is
/// still obtainable by listing roles filtered on a role group. A nested
/// collection of roles is absent for the same reason, which keeps this contract
/// flat instead of promoting it to an aggregate.
/// </para>
/// </remarks>
public sealed class RoleGroupDto
{
    /// <summary>
    /// Identifier of the role group.
    /// </summary>
    /// <remarks>
    /// Backing column <c>RoleGroups.RoleGroupID int IDENTITY(0,1) NOT NULL</c>,
    /// which is also the non-clustered primary key. Legacy member
    /// <c>RoleGroupInfo.RoleGroupID</c>, an <c>Integer</c> at
    /// <c>RoleGroupInfo.vb</c> line 53.
    /// </remarks>
    // MIGRATION: the column is seeded IDENTITY(0,1), so the very first role group
    // inserted carries the identifier 0, and zero is therefore a perfectly
    // legitimate, addressable identifier rather than a missing value. Never probe
    // this property for absence by comparing it against zero, against a
    // negative-or-zero range, or against the type's default value: every one of
    // those tests would silently exclude the first row in the table. The legacy
    // editor signalled "no group selected" with the sentinel -1, which is
    // Null.NullInteger at Null.vb line 41, and branched on it to choose between
    // AddRoleGroup and UpdateRoleGroup (EditGroups.ascx.vb lines 42, 68 and 113).
    // That magic value is deliberately not carried forward, because the routed
    // endpoint already distinguishes creation from update: on a create the
    // identifier simply is not meaningful and the service ignores whatever
    // arrives here. The property consequently stays a plain non-nullable int,
    // faithful to the NOT NULL column, and is never widened to a nullable int.
    public int RoleGroupId { get; set; }

    /// <summary>
    /// Identifier of the portal that owns the role group.
    /// </summary>
    /// <remarks>
    /// Backing column <c>RoleGroups.PortalID int NOT NULL</c>, which is also the
    /// leading member of the composite uniqueness constraint over
    /// <c>(PortalID, RoleGroupName)</c>. Legacy member
    /// <c>RoleGroupInfo.PortalID</c>, an <c>Integer</c> at <c>RoleGroupInfo.vb</c>
    /// line 68, assigned from the ambient portal of the hosting control at
    /// <c>EditGroups.ascx.vb</c> line 108.
    /// </remarks>
    // MIGRATION: non-nullable on purpose, and deliberately asymmetric with the
    // neighbouring table. Roles.PortalID is declared [int] NULL at
    // 01.00.00.SqlDataProvider line 116, so a role may be host-wide instead of
    // belonging to one portal, whereas RoleGroups.PortalID is NOT NULL: a role
    // group is always tenant-owned. A plain int is the honest type here, and
    // copying the nullability of the neighbouring table would misstate the schema.
    public int PortalId { get; set; }

    /// <summary>
    /// Name of the role group, unique within its portal.
    /// </summary>
    /// <remarks>
    /// Backing column <c>RoleGroups.RoleGroupName nvarchar(50) NOT NULL</c>,
    /// covered together with <c>PortalID</c> by the composite uniqueness
    /// constraint. Legacy member <c>RoleGroupInfo.RoleGroupName</c>, a
    /// <c>String</c> at <c>RoleGroupInfo.vb</c> line 83, bound to a
    /// fifty-character mandatory text box at <c>EditGroups.ascx</c> lines 11 and
    /// 12, the single field validator that screen declared. Both the length
    /// ceiling and the mandatory-value rule are reproduced declaratively by the
    /// FluentValidation validator, and a uniqueness clash surfaces as a conflict
    /// raised by the service layer, mirroring the duplicate-group message the
    /// legacy editor emitted at <c>EditGroups.ascx.vb</c> line 117. This contract
    /// asserts none of those rules itself.
    /// </remarks>
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text description of the role group, or <see langword="null"/> when the
    /// group has none.
    /// </summary>
    /// <remarks>
    /// Backing column <c>RoleGroups.Description nvarchar(1000) NULL</c>, the only
    /// nullable column on the table. Legacy member
    /// <c>RoleGroupInfo.Description</c>, a <c>String</c> at
    /// <c>RoleGroupInfo.vb</c> line 98, bound to a thousand-character multi-line
    /// text box at <c>EditGroups.ascx</c> line 17 that carried no validator.
    /// </remarks>
    // MIGRATION: the legacy read path could never yield null here. Every read
    // funnelled through Null.SetNull, and its string sentinel, Null.NullString at
    // Null.vb line 70, is the empty string rather than null, so a database NULL
    // and an empty description were indistinguishable once loaded and the editor
    // round-tripped both as an empty box. This contract models the nullable column
    // honestly with a nullable string instead of importing that sentinel, which
    // makes null and the empty string distinguishable on the wire for the first
    // time. Deciding which of the two stands for an absent description belongs to
    // Application/Mapping/RoleMappings.cs, the one place that translates between
    // this contract and the persisted model. It is settled there and stated here
    // in prose rather than imposed by a serialisation attribute or a custom
    // converter, so the divergence stays visible instead of being applied
    // silently while the payload is written.
    public string? Description { get; set; }
}
