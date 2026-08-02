using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One user-interface control published by a module definition, or a host-level control when
/// <see cref="ModuleDefinitionId"/> is <see langword="null"/>.
/// </summary>
/// <remarks>
/// MIGRATION: replaces <c>DotNetNuke.Entities.Modules.ModuleControlInfo</c>. Bound to
/// <c>dbo.ModuleControls</c>, introduced by 02.00.00 when the fixed <c>DesktopSrc</c>,
/// <c>MobileSrc</c> and <c>EditSrc</c> trio was lifted off <c>ModuleDefinitions</c> into an
/// open-ended set of control rows. <c>ControlSrc</c> is a Web Forms path in the legacy data and is
/// carried through unchanged; the target does not load it, because the presentation layer is
/// Angular.
/// </remarks>
public sealed class ModuleControl : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>ModuleControlID</c>, identity from 1).</summary>
    public int ModuleControlId { get; set; }

    /// <inheritdoc />
    public override int Identity => ModuleControlId;

    /// <summary>
    /// Gets or sets the owning definition (<c>ModuleDefID</c>, nullable: host-level controls belong
    /// to no definition), cascade delete.
    /// </summary>
    public int? ModuleDefinitionId { get; set; }

    /// <summary>Gets or sets the key that selects this control, empty for the default view (<c>ControlKey</c>, 20 characters).</summary>
    public string? ControlKey { get; set; }

    /// <summary>Gets or sets the display title (<c>ControlTitle</c>, 50 characters).</summary>
    public string? ControlTitle { get; set; }

    /// <summary>Gets or sets the legacy control path (<c>ControlSrc</c>, 256 characters).</summary>
    public string? ControlSrc { get; set; }

    /// <summary>Gets or sets the icon path (<c>IconFile</c>, 100 characters).</summary>
    public string? IconFile { get; set; }

    /// <summary>
    /// Gets or sets the security level required to reach the control (<c>ControlType</c>, required).
    /// Legacy <c>SecurityAccessLevel</c> values: 0 anonymous, 1 view, 2 edit, 3 admin, 4 host.
    /// </summary>
    public int ControlType { get; set; }

    /// <summary>Gets or sets the ordinal used when several controls share a key (<c>ViewOrder</c>).</summary>
    public int? ViewOrder { get; set; }

    /// <summary>Gets or sets the help URL shown beside the control (<c>HelpUrl</c>, 200 characters).</summary>
    public string? HelpUrl { get; set; }

    /// <summary>
    /// Gets or sets whether the legacy control supported partial rendering
    /// (<c>SupportsPartialRendering</c>, required, defaults to <see langword="false"/>).
    /// </summary>
    public bool SupportsPartialRendering { get; set; }

    /// <summary>Gets or sets the definition that publishes this control.</summary>
    public ModuleDefinition? ModuleDefinition { get; set; }
}
