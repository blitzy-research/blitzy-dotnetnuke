namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// One row of the portals administration grid, as returned inside a page by <c>GET /api/v1/portals</c>.
/// </summary>
/// <remarks>
/// Everything the grid does not render is deliberately absent: the payment-processor credentials, the
/// special-page identifiers, and the localisation and skinning settings all belong to the portal detail and
/// settings contracts. A listing endpoint that returned them would publish a portal's whole configuration
/// to any caller merely permitted to see that the portal exists.
/// </remarks>
// MIGRATION: three members here are non-nullable where the sibling detail and settings contracts declare
// their counterparts nullable - PortalName, HostFee and HostSpace.
public sealed class PortalListItemDto
{
    /// <summary>Identifier of the portal.</summary>
    /// <remarks>
    /// Backing column <c>Portals.PortalID int NOT NULL IDENTITY (-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 77). Rendered by the legacy grid's first data column,
    /// <c>portals.ascx</c> lines 23 to 29.
    /// </remarks>
    // The column's identity seed is negative, so the first identifier the column generates is minus one,
    // while the shipped "_default" portal row is inserted with an explicit identifier of nought at
    // 01.00.00.SqlDataProvider line 7125. Both are legitimate, addressable tenants.
    public int PortalId { get; set; }

    /// <summary>Name of the portal, as the grid's title column shows it.</summary>
    /// <remarks>
    /// Backing column <c>Portals.PortalName nvarchar(128) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c>
    /// line 79). Rendered by the legacy grid's title column, bound to <c>PortalName</c> at
    /// <c>portals.ascx</c> line 34 under the caption "Title".
    /// </remarks>
    // The stored column name is kept and the legacy caption "Title" is not adopted as the member name. The
    // caption is display wording, and its localised form belongs in the client template sourced from
    // Website/admin/Portal/App_LocalResources/Portals.ascx.resx.
    public string PortalName { get; set; } = string.Empty;

    /// <summary>Host names by which the portal is reachable, empty when it has none.</summary>
    /// <remarks>
    /// Projected from <c>PortalAlias.HTTPAlias</c> for the portal's alias rows. Rendered by the legacy
    /// grid's third data column, <c>portals.ascx</c> lines 37 to 43, under the caption "Portal Aliases".
    /// </remarks>
    // This member is mandatory, not optional. It carries the third of the eight data columns the legacy
    // grid rendered, so omitting it would drop a column the administrator can see today and would breach
    // the functional-parity requirement that governs this migration.
    public IReadOnlyList<string> Aliases { get; set; } = [];

    /// <summary>
    /// Number of users registered in the portal. Populated by the application service; it is not a column
    /// on <c>Portals</c>.
    /// </summary>
    /// <remarks>
    /// Rendered by the legacy grid's member-tally column, bound to <c>Users</c> at <c>portals.ascx</c> line
    /// 44.
    /// </remarks>
    // The legacy negative "not yet loaded" marker is GONE from the wire, and this member is never negative.
    public int Users { get; set; }

    /// <summary>
    /// Number of pages defined in the portal. Populated by the application service; it is not a column on
    /// <c>Portals</c>.
    /// </summary>
    /// <remarks>
    /// Rendered by the legacy grid's page-tally column, bound to <c>Pages</c> at <c>portals.ascx</c> line
    /// 45.
    /// </remarks>
    // The value CAN be negative on the wire, and that is preserved legacy arithmetic.
    public int Pages { get; set; }

    /// <summary>
    /// Disk-space allowance for the portal in megabytes, where nought carries the legacy meaning of no
    /// imposed limit.
    /// </summary>
    /// <remarks>
    /// Backing column <c>Portals.HostSpace int NOT NULL</c> with a <c>DF_{objectQualifier}Portals_HostSpace
    /// DEFAULT (0)</c> constraint (<c>03.01.01.SqlDataProvider</c> lines 1119 and 1131, tightened from the
    /// nullable baseline at <c>01.00.00.SqlDataProvider</c> line 90).
    /// </remarks>
    // Non-nullable, because the column has been NOT NULL with a zero default since 03.01.01 and the Domain
    // entity declares it non-nullable too.
    public int HostSpace { get; set; }

    /// <summary>Recurring hosting fee charged for the portal, nought when none is charged.</summary>
    /// <remarks>
    /// Backing column <c>Portals.HostFee money NOT NULL</c> with a <c>DF_{objectQualifier}Portals_HostFee
    /// DEFAULT (0)</c> constraint (<c>03.01.01.SqlDataProvider</c> lines 1118 and 1129, converted from the
    /// <c>nvarchar(10)</c> baseline at <c>01.00.00.SqlDataProvider</c> line 89).
    /// </remarks>
    // Non-nullable, matching the column's NOT NULL and DEFAULT (0) and matching the Domain entity, so an
    // unbilled portal reports nought rather than a null.
    public decimal HostFee { get; set; }

    /// <summary>
    /// Instant at which the portal's hosting subscription lapses, or <see langword="null"/> when it does
    /// not expire.
    /// </summary>
    // Genuinely nullable, unlike the two members above - this column really is NULL in the terminal state
    // and was never tightened.
    public DateTime? ExpiryDate { get; set; }
}
