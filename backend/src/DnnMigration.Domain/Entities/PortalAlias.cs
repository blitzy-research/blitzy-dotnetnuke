using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One HTTP host name (optionally with a virtual path) that resolves to a portal.
/// </summary>
/// <remarks>
/// MIGRATION: replaces <c>DotNetNuke.Entities.Portals.PortalAliasInfo</c>
/// (Library/Components/Portal/PortalAliasInfo.vb). Bound to <c>dbo.PortalAlias</c>, created in
/// 02.02.02 with three columns and never widened; <c>HTTPAlias</c> gained a unique constraint in
/// 03.00.07, which is why alias resolution can be an exact-match lookup instead of the legacy
/// <c>like '%alias%'</c> scan.
/// </remarks>
public sealed class PortalAlias : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>PortalAliasID</c>, identity from 1).</summary>
    public int PortalAliasId { get; set; }

    /// <inheritdoc />
    public override int Identity => PortalAliasId;

    /// <summary>Gets or sets the owning portal (<c>PortalID</c>, required, cascade delete).</summary>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the host name and optional virtual path, for example
    /// <c>www.example.com</c> or <c>host/child</c> (<c>HTTPAlias</c>, unique, 200 characters).
    /// </summary>
    public string HttpAlias { get; set; } = string.Empty;

    /// <summary>Gets or sets the portal this alias resolves to.</summary>
    public Portal? Portal { get; set; }
}
