namespace DnnMigration.Application.Dtos.Portal;

// MIGRATION: this contract exists because the alias write path previously accepted the alias
// PROJECTION, and a projection is the wrong shape for a write in three separate ways. It reported
// the host name as nullable, because the column is nullable and a reader must be able to represent
// what it finds; it carried a database-assigned identifier that a create cannot supply; and it
// carried an owning-portal identifier that duplicated the one the route already fixes. A request
// type carries exactly the one value a caller decides and nothing a caller may not decide, so the
// three redundant members cannot disagree with the route and the host name cannot be absent.
//
// MIGRATION: the legacy screen collected this single value and nothing else.
// Website/admin/Portal/editportalalias.ascx declares one input for the alias at L7, and
// EditPortalAlias.ascx.vb read it at L208 and assigned it at L235 before inserting; the owning
// portal came from page state at L234 rather than from the operator. This contract reproduces that
// exactly: one submitted value, with the tenant supplied by the route.

/// <summary>
/// The payload submitted to bind a new alias to a portal, through
/// <c>POST /api/v1/portals/{portalId}/aliases</c>.
/// </summary>
/// <remarks>
/// <para>
/// The owning portal is <b>not</b> a member of this type. It is supplied by the route and is
/// authoritative there, so there is no second copy for a caller to disagree with and no way to bind
/// a host name to a tenant other than the one addressed.
/// </para>
/// <para>
/// The alias identifier is not a member either. It is assigned by the database - the column is
/// declared <c>IDENTITY (1, 1)</c> at
/// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider:L3805</c> - and a
/// value a caller cannot choose has no place on a contract a caller fills in. It is reported back
/// on the alias projection once the write has happened.
/// </para>
/// <para>
/// Shape rules live in <c>Application/Validation/CreatePortalAliasRequestValidator</c>, which shares
/// its rule set with the update contract. Uniqueness does not: the column carries a unique
/// constraint, <c>IX_PortalAlias UNIQUE NONCLUSTERED (HTTPAlias)</c> at
/// <c>03.00.07.SqlDataProvider:L14-L18</c>, and a question about what is already stored is a
/// question for the service, which answers it with <c>portal.alias_duplicate</c>.
/// </para>
/// </remarks>
public sealed class CreatePortalAliasRequest
{
    // MIGRATION: the value is stored lower-cased and this member does not lower-case it. The legacy
    // write path applied the casing itself, on insert at
    // Library/Components/Portal/PortalAliasController.vb:L31, so the stored value may differ in
    // case from what a caller submits. That transformation belongs to the service and to the
    // mapping layer, where it is recorded; a request contract that pre-applied it would leave the
    // validator inspecting a value the caller never sent.
    //
    // MIGRATION: a protocol prefix is REFUSED rather than stripped, reversing a silent rewrite the
    // legacy screen performed at EditPortalAlias.ascx.vb:L210-L215 for a scheme separator and a
    // network-share prefix alike. The reasoning is recorded once, on PortalAliasRules.

    /// <summary>
    /// Gets or sets the host name by which the portal is to be reached.
    /// </summary>
    /// <value>
    /// A host name, an IP address or a server name, optionally followed by a port and a child path,
    /// with no protocol prefix. Required and non-nullable: a create that supplied nothing has
    /// nothing to bind, which the validator reports as a field-level failure.
    /// </value>
    /// <remarks>
    /// Bound for <c>PortalAlias.HTTPAlias</c>, declared <c>[nvarchar] (200)</c> at
    /// <c>02.02.02.SqlDataProvider:L3807</c>. The column itself is nullable, which is why the
    /// <em>projection</em> reports a nullable value, but nothing about a nullable column obliges a
    /// write contract to accept an absent one - the legacy screen refused to act on an empty box at
    /// <c>EditPortalAlias.ascx.vb:L209</c>, so an absent alias was never a meaningful submission.
    /// </remarks>
    public string HttpAlias { get; set; } = string.Empty;
}
