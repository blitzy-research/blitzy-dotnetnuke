namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// The payload submitted to bind a new alias to a portal, through <c>POST
/// /api/v1/portals/{portalId}/aliases</c>.
/// </summary>
/// <remarks>
/// <para>
/// The owning portal is <b>not</b> a member of this type. It is supplied by the route and is authoritative
/// there, so there is no second copy for a caller to disagree with and no way to bind a host name to a
/// tenant other than the one addressed.
/// </para>
/// <para>
/// The alias identifier is not a member either. It is assigned by the database - the column is declared
/// <c>IDENTITY (1, 1)</c> at
/// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider:L3805</c> - and a value a
/// caller cannot choose has no place on a contract a caller fills in.
/// </para>
/// </remarks>
public sealed class CreatePortalAliasRequest
{
    /// <summary>Gets or sets the host name by which the portal is to be reached.</summary>
    /// <value>
    /// A host name, an IP address or a server name, optionally followed by a port and a child path, with no
    /// protocol prefix.
    /// </value>
    public string HttpAlias { get; set; } = string.Empty;
}
