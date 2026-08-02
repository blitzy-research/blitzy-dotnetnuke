using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// Grants a portal the right to place instances of a desktop module.
/// </summary>
/// <remarks>
/// MIGRATION: replaces <c>DotNetNuke.Entities.Modules.PortalDesktopModuleInfo</c>. Bound to
/// <c>dbo.PortalDesktopModules</c> (02.02.02), whose unique index over
/// <c>(PortalID, DesktopModuleID)</c> makes the pair the natural key behind the surrogate.
/// Premium modules are exactly those that need a row here.
/// </remarks>
public sealed class PortalDesktopModule : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>PortalDesktopModuleID</c>, identity from 1).</summary>
    public int PortalDesktopModuleId { get; set; }

    /// <inheritdoc />
    public override int Identity => PortalDesktopModuleId;

    /// <summary>Gets or sets the portal granted access (<c>PortalID</c>, required, cascade delete).</summary>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the granted desktop module (<c>DesktopModuleID</c>, required, cascade delete).</summary>
    public int DesktopModuleId { get; set; }

    /// <summary>Gets or sets the portal granted access.</summary>
    public Portal? Portal { get; set; }

    /// <summary>Gets or sets the desktop module made available.</summary>
    public DesktopModule? DesktopModule { get; set; }
}
