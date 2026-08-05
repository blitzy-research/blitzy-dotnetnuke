namespace DnnMigration.Application.Dtos.Role;

/// <summary>
/// Request contract for <c>POST /api/v1/role-groups</c>: the two values that
/// declare a new portal-scoped container for security roles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists separately from the response shape.</b> Both role-group write actions used
/// to bind <see cref="RoleGroupDto"/>, the response projection, which advertised two further members -
/// the group's own identifier and its owning portal - as writable. Neither was ever applied: the
/// mapper reads only the two members below, so a caller could submit an identifier or a tenant, see it
/// accepted with <c>201</c> or <c>200</c>, and find it silently discarded. That is an over-posting
/// surface in the strict sense - a published field a client may reasonably believe it can set - and it
/// is also a contract a generated client cannot reason about, because the schema promised more than
/// the endpoint honours. Declaring the request separately makes the honoured set the published set.
/// </para>
/// <para>
/// <b>The member set is the legacy screen's editable field set exactly.</b>
/// <c>Website/admin/Security/EditGroups.ascx</c> declares two inputs: a fifty-character mandatory name
/// text box at L11 with its <c>requiredfieldvalidator</c> beside it at L12 - note that the markup
/// writes its tags in lower case, so the file must be read case-insensitively to find them - and a
/// thousand-character multi-line description at L17 carrying no validator at all. The save block at
/// <c>EditGroups.ascx.vb</c> L107-L111 assigns exactly four members onto its legacy entity: those two,
/// plus the group's identifier and the ambient portal, neither of which came from an input.
/// </para>
/// <para>
/// <b>Neither identifier travels in the body.</b> The store assigns the group's identifier, which is
/// precisely why the HTTP method rather than a sentinel distinguishes a create from an update: the
/// legacy screen overloaded the identifier value minus one as its add-versus-edit switch
/// (<c>EditGroups.ascx.vb</c> L42, L68 and L113), and the migrated design has two routed endpoints
/// instead. The portal arrives through the resolved request context; accepting it in the body as well
/// would give the tenant a second, contradictable source of truth, which is the one value a
/// multi-tenant write must never let a caller restate.
/// </para>
/// <para>
/// The type is inert: no behaviour, no derived member, no guard and no constructor. Field rules are
/// declared by <c>Application/Validation/CreateRoleGroupRequestValidator.cs</c>, which is public and
/// is discovered by the assembly scan in <c>Application/DependencyInjection.cs</c>, so the API's
/// validation filter resolves it and reports a breach as an RFC 7807 validation document naming the
/// member. Portal-scoped name uniqueness is not a field rule - it needs a read - so it is an expected
/// failure raised by <c>Application/Services/RoleService.cs</c> and answered as a conflict, mirroring
/// the duplicate-group message the legacy editor emitted at <c>EditGroups.ascx.vb</c> L117.
/// Translation onto the persisted model lives in <c>Application/Mapping/RoleMappings.cs</c>.
/// </para>
/// </remarks>
// MIGRATION: no inheritance and no shared base type with the sibling update contract, even though the
// two carry the same two members today. A base type would present members neither contract owns as
// soon as one of them gained a member, which is the readability cost the legacy UserRoleInfo : RoleInfo
// pair demonstrates - a type describing an eight-column assignment presents twenty-three effective
// properties. Shared RULES are shared instead, through Application/Validation, which is where sharing
// costs nothing.
//
// MIGRATION: the role tally the legacy editor computed at EditGroups.ascx.vb L75 is absent. It was
// never persisted, was never a member of the legacy class, and existed only to hide the delete button
// while a group still classified roles. Hiding a button is not enforcement, so the rule it stood for is
// enforced in the service on the removal path and reported as a conflict; the count itself remains
// obtainable by listing the portal's roles filtered on the group.
public sealed class CreateRoleGroupRequest
{
    /// <summary>
    /// Name of the role group. Required, and unique within the owning portal.
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
    // MIGRATION: non-nullable and initialised to the empty string rather than to a null-forgiving
    // default, because the column is NOT NULL and a request that omits the name is a MISSING required
    // field rather than a null one. The presence rule is NotEmpty rather than NotNull because NOT NULL
    // is satisfied by the empty string, and the legacy code-behind assigned its text box verbatim at
    // EditGroups.ascx.vb L110 - so only the client-side required-field validator, re-checked through
    // Page.IsValid at L106, kept an empty name out of the column.
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>
    /// Free-text description of the role group, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// Screen control <c>asp:TextBox txtDescription</c>, multi-line and capped at a thousand
    /// characters by its own <c>maxlength</c>, carrying no validator whatsoever
    /// (<c>EditGroups.ascx</c> L17). Terminal column
    /// <c>RoleGroups.Description nvarchar(1000) NULL</c> (<c>03.02.03.SqlDataProvider</c> L21) - the
    /// only nullable column on the table.
    /// </remarks>
    // MIGRATION: nullable, and null is not normalised here. The legacy read path funnelled every
    // string through Null.SetNull, whose string sentinel is the empty string rather than null
    // (Null.vb L70), so a stored NULL and a stored empty description were indistinguishable once
    // loaded and the editor round-tripped both as an empty box. This contract keeps the two distinct;
    // which of them a null submission becomes on the way to the column is settled once, in
    // Application/Mapping/RoleMappings.cs.
    public string? Description { get; set; }
}
