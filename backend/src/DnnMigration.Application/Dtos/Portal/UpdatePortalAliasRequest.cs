namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// The payload submitted to change an existing alias, through <c>PUT
/// /api/v1/portals/{portalId}/aliases/{portalAliasId}</c>.
/// </summary>
public sealed class UpdatePortalAliasRequest
{
    /// <summary>Gets or sets the host name by which the portal is to be reached.</summary>
    /// <value>
    /// A host name, an IP address or a server name, optionally followed by a port and a child path, with no
    /// protocol prefix.
    /// </value>
    public string HttpAlias { get; set; } = string.Empty;
}
