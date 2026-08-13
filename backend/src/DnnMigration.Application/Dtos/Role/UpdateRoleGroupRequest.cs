namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>PUT /api/v1/role-groups/{roleGroupId}</c>: the writable state of an existing
/// portal-scoped container for security roles.
/// </summary>
// MIGRATION - behavioural difference already in place and restated here because this contract is where a
// reader meets it.
public sealed class UpdateRoleGroupRequest
{
    /// <summary>Replacement name of the role group. Required, and unique within the owning portal.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleGroupName</c>, capped at fifty characters by its own
    /// <c>maxlength</c> and carrying the screen's one validator, <c>valRoleGroupName</c>.
    /// </remarks>
    // Non-nullable and initialised to the empty string, because the column is NOT NULL and an omitted name
    // is a MISSING required field rather than a null one.
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>Replacement description of the role group, or <see langword="null"/> to clear it.</summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at a thousand characters by
    /// its own <c>maxlength</c>, carrying no validator whatsoever. Terminal column
    /// <c>RoleGroups.Description nvarchar(1000) NULL</c> (<c>03.02.03.SqlDataProvider</c> L21).
    /// </remarks>
    // Nullable, and null is not normalised here. The legacy absent value for a text column was the empty
    // string rather than null, so a database null and an empty description were indistinguishable once
    // read.
    public string? Description { get; set; }
}
