namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how a module instance is presented on a page: expanded, collapsed, or suppressed entirely.
/// This is the legacy VB.NET type <c>DotNetNuke.Entities.Modules.VisibilityState</c> under its target name.
/// </summary>
/// <remarks>
/// <para>
/// The three ordinals are legacy persisted values, not arbitrary identifiers. They are held in the
/// <c>TabModules.Visibility</c> column, which the legacy upgrade scripts declare as <c>int NOT NULL</c>,
/// and the legacy reader mapped them by number: 0 to <see cref="ModuleVisibility.Maximized"/>, 1 to <see
/// cref="ModuleVisibility.Minimized"/> and 2 to <see cref="ModuleVisibility.None"/>.
/// </para>
/// <para>
/// <see cref="ModuleVisibility.Maximized"/> is 0 and is therefore the default value of this type, which
/// matches the legacy constructor: it initialised the backing field to the same member.
/// </para>
/// </remarks>
public enum ModuleVisibility
{
    /// <summary>
    /// The module is presented in full, with its content pane expanded. Persisted as 0, and consequently
    /// the default value of <see cref="ModuleVisibility"/>.
    /// </summary>
    Maximized = 0,

    /// <summary>
    /// The module is presented collapsed: its title and container chrome remain visible while the content
    /// pane is hidden. Persisted as 1.
    /// </summary>
    Minimized = 1,

    /// <summary>The module is not presented at all. Persisted as 2.</summary>
    None = 2
}
