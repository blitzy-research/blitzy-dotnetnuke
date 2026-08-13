namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// RESPONSE contract for a DotNetNuke role group: the portal-scoped container that gathers security roles
/// together so an administrator can present and manage them as a set.
/// </summary>
/// <remarks>
/// The backing column definitions come from the terminal state of the upgrade chain rather than from the
/// baseline script, because <c>RoleGroups</c> is absent from <c>01.00.00.SqlDataProvider</c> entirely. The
/// table is created twice, under <c>{databaseOwner}{objectQualifier}</c> templating and guarded by an
/// existence check, at <c>03.02.03.SqlDataProvider</c> line 16 and <c>04.00.04.SqlDataProvider</c> line 49.
/// </remarks>
public sealed class RoleGroupDto
{
    /// <summary>Identifier of the role group.</summary>
    // The column is seeded IDENTITY(0,1), so the very first role group inserted carries the identifier 0,
    // and zero is therefore a perfectly legitimate, addressable identifier rather than a missing value.
    public int RoleGroupId { get; set; }

    /// <summary>Identifier of the portal that owns the role group.</summary>
    // Non-nullable on purpose, and deliberately asymmetric with the neighbouring table.
    public int PortalId { get; set; }

    /// <summary>Name of the role group, unique within its portal.</summary>
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>Free-text description of the role group, or <see langword="null"/> when the group has none.</summary>
    // The legacy read path could never yield null here.
    public string? Description { get; set; }
}
