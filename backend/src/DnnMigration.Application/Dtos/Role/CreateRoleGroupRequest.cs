namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>POST /api/v1/role-groups</c>: the two values that declare a new portal-scoped
/// container for security roles.
/// </summary>
// No inheritance and no shared base type with the sibling update contract, even though the two carry the
// same two members today.
public sealed class CreateRoleGroupRequest
{
    /// <summary>Name of the role group. Required, and unique within the owning portal.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleGroupName</c>, capped at fifty characters by its own
    /// <c>maxlength</c> and carrying the screen's one validator, <c>valRoleGroupName</c>.
    /// </remarks>
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>Free-text description of the role group, or <see langword="null"/> when it has none.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at a thousand characters by
    /// its own <c>maxlength</c>, carrying no validator whatsoever. Terminal column
    /// <c>RoleGroups.Description nvarchar(1000) NULL</c> (<c>03.02.03.SqlDataProvider</c> L21) - the only
    /// nullable column on the table.
    /// </remarks>
    public string? Description { get; set; }
}
