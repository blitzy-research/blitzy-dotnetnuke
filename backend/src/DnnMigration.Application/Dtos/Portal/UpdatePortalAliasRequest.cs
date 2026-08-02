namespace DnnMigration.Application.Dtos.Portal;

// MIGRATION: a separate type from the create contract, even though the two carry the same single
// value, because they answer to different routes and may diverge without either becoming wrong. The
// alternative - one shared type - would make every future member of one contract a member of the
// other by default, which is how an update quietly acquires the ability to set something only a
// create should decide. The rule SET is what is shared, not the type: both validators call
// Application/Validation/PortalAliasRules, so the two paths cannot enforce different shapes.
//
// MIGRATION: the alias cannot be moved between portals through this contract, and the legacy screen
// is the reason rather than a simplification. Website/admin/Portal/editportalalias.ascx offers one
// input, for the host name (L7), and EditPortalAlias.ascx.vb re-supplied the owning portal from page
// state on update at L221 rather than from the operator - so an operator could never retarget an
// alias either. No owning-portal member appears here, so the omission is enforced rather than
// merely documented.

/// <summary>
/// The payload submitted to change an existing alias, through
/// <c>PUT /api/v1/portals/{portalId}/aliases/{portalAliasId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The alias identifier is <b>not</b> a member of this type. It is supplied by the route and is
/// authoritative there, so a caller cannot redirect a write it is otherwise entitled to make onto a
/// different alias by editing the body. The same reasoning removed the redundant portal identifier
/// from the portal update contract, and it is recorded there in full.
/// </para>
/// <para>
/// Shape rules live in <c>Application/Validation/UpdatePortalAliasRequestValidator</c> and are the
/// create contract's rules exactly. Whether the new host name collides with one already bound is a
/// question about stored state, so the service answers it with <c>portal.alias_duplicate</c>; the
/// legacy screen learned the same fact the hard way, by catching the exception the unique constraint
/// raised, at <c>EditPortalAlias.ascx.vb:L223-L228</c>.
/// </para>
/// </remarks>
public sealed class UpdatePortalAliasRequest
{
    /// <summary>
    /// Gets or sets the host name by which the portal is to be reached.
    /// </summary>
    /// <value>
    /// A host name, an IP address or a server name, optionally followed by a port and a child path,
    /// with no protocol prefix. Required and non-nullable: an update carrying nothing has nothing to
    /// store, and the legacy screen simply did nothing at all in that case
    /// (<c>EditPortalAlias.ascx.vb:L209</c>), which a contract that must answer cannot reproduce.
    /// </value>
    /// <remarks>
    /// Bound for <c>PortalAlias.HTTPAlias</c>, declared <c>[nvarchar] (200)</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider:L3807</c>. The
    /// value is stored lower-cased by the write path, as the legacy update did at
    /// <c>Library/Components/Portal/PortalAliasController.vb:L97</c>, and this member neither applies
    /// that casing nor asserts it.
    /// </remarks>
    public string HttpAlias { get; set; } = string.Empty;
}
