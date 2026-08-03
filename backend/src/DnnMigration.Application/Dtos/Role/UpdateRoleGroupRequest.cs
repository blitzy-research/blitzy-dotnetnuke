namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>PUT /api/v1/portals/{portalId}/role-groups/{roleGroupId}</c>: the writable
/// state of an existing portal-scoped container for security roles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists separately from the response shape.</b> Both role-group write actions used
/// to bind <see cref="RoleGroupDto"/>, the response projection, which advertised the group's own
/// identifier and its owning portal as writable members. The mapper reads neither, so a caller could
/// submit an identifier that disagreed with the route, or a tenant other than the one it addressed, see
/// the request accepted, and find the value silently discarded. Publishing only what is honoured
/// removes both the over-posting surface and the disagreement between the schema and the endpoint.
/// </para>
/// <para>
/// <b>This is a full replacement, not a partial edit.</b> Both members are applied as supplied, so
/// omitting the description clears the stored value rather than preserving it. A caller amending one
/// field reads the group first and resubmits the rest - the same discipline the legacy screen followed
/// by loading the group into its form before posting it back (<c>EditGroups.ascx.vb</c> L68-L79 for the
/// load, L107-L111 for the save).
/// </para>
/// <para>
/// <b>The member set is identical to the creation contract's, and that is measured rather than
/// assumed.</b> The legacy editor served both operations from one postback and posted the same two
/// editable fields either way, choosing between them by testing its identifier against minus one
/// (<c>EditGroups.ascx.vb</c> L113). Two separate contracts are declared nonetheless, because the two
/// operations are separate routed endpoints whose contracts must be free to diverge without one
/// dragging the other with it.
/// </para>
/// <para>
/// <b>Neither identifier travels in the body.</b> The group and the portal both arrive in the route,
/// which is what makes them authoritative: a value that cannot be supplied cannot contradict the route,
/// so no reconciliation check is needed and the tenant is never restatable by a caller. Note that the
/// group identifier is an ordinary <c>int</c> whose value may legitimately be zero -
/// <c>RoleGroups.RoleGroupID</c> is seeded <c>IDENTITY(0,1)</c> at
/// <c>03.02.03.SqlDataProvider</c> L18 - so zero identifies the first group ever created and is never
/// a marker for an absent one.
/// </para>
/// <para>
/// The type is inert: no behaviour, no derived member, no guard and no constructor. Field rules are
/// declared by <c>Application/Validation/UpdateRoleGroupRequestValidator.cs</c>, which is public and is
/// discovered by the assembly scan in <c>Application/DependencyInjection.cs</c>, so the API's
/// validation filter resolves it and reports a breach as an RFC 7807 validation document naming the
/// member. Portal-scoped name uniqueness needs a read, so it is an expected failure raised by
/// <c>Application/Services/RoleService.cs</c> and answered as a conflict; that comparison excludes the
/// group being edited, so resubmitting a group's own name is a no-op. Translation onto the persisted
/// model lives in <c>Application/Mapping/RoleMappings.cs</c>.
/// </para>
/// </remarks>
// MIGRATION: no inheritance and no shared base type with the sibling creation contract, for the reason
// recorded on that contract. Shared RULES are shared instead, through Application/Validation.
//
// MIGRATION - behavioural difference already in place and restated here because this contract is where
// a reader meets it. The legacy save wrapped ONLY its add branch in a Try; the update branch at
// EditGroups.ascx.vb L122 had no duplicate handling whatsoever, so renaming a group onto an existing
// name violated the table's unique constraint and surfaced as an unhandled provider error through the
// module's load-exception path. The collision is an expected outcome on this path too, reported with the
// same code the create action reports and answered with the same conflict status.
//
// MIGRATION: re-classifying a group moves no role between groups. A role's group membership is a field
// of the ROLE, changed through the role resource, which is why no member here names a role.
public sealed class UpdateRoleGroupRequest
{
    /// <summary>
    /// Replacement name of the role group. Required, and unique within the owning portal.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtRoleGroupName</c>, capped at fifty characters by its own
    /// <c>maxlength</c> (<c>EditGroups.ascx</c> L11) and carrying the screen's one validator,
    /// <c>valRoleGroupName</c> (L12). Terminal column
    /// <c>RoleGroups.RoleGroupName nvarchar(50) NOT NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> L20, recreated identically at
    /// <c>04.00.04.SqlDataProvider</c> L53), covered together with <c>PortalID</c> by the composite
    /// uniqueness constraint over <c>(PortalID, RoleGroupName)</c>.
    /// </remarks>
    // MIGRATION: non-nullable and initialised to the empty string, because the column is NOT NULL and
    // an omitted name is a MISSING required field rather than a null one. Unlike the description below,
    // there is no cleared state for the name to be moved to, which is why the replacement discipline
    // does not make this member optional.
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>
    /// Replacement description of the role group, or <see langword="null"/> to clear it.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at a thousand
    /// characters by its own <c>maxlength</c>, carrying no validator whatsoever
    /// (<c>EditGroups.ascx</c> L17). Terminal column
    /// <c>RoleGroups.Description nvarchar(1000) NULL</c> (<c>03.02.03.SqlDataProvider</c> L21).
    /// </remarks>
    // MIGRATION: nullable, and null is not normalised here. The legacy absent value for a text column
    // was the empty string rather than null (Null.vb L70), so a database null and an empty description
    // were indistinguishable once read. This contract keeps the two distinct and forces neither into the
    // other; the mapper owns that decision for the column.
    public string? Description { get; set; }
}
