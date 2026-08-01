namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how a module instance is presented on a page: expanded, collapsed, or
/// suppressed entirely. This is the legacy VB.NET type
/// <c>DotNetNuke.Entities.Modules.VisibilityState</c> under its target name.
/// </summary>
/// <remarks>
/// <para>
/// The three ordinals are legacy persisted values, not arbitrary identifiers. They are
/// held in the <c>TabModules.Visibility</c> column, which the legacy upgrade scripts
/// declare as <c>int NOT NULL</c>, and the legacy reader mapped them by number: 0 to
/// <see cref="ModuleVisibility.Maximized"/>, 1 to <see cref="ModuleVisibility.Minimized"/>
/// and 2 to <see cref="ModuleVisibility.None"/>. VB.NET assigned those numbers implicitly
/// from declaration order; they are spelled out explicitly here so that a later
/// re-ordering, an alphabetical sort or an inserted member cannot silently re-point
/// existing rows at a different meaning. The numbering is identical to the legacy
/// behaviour rather than a change to it.
/// </para>
/// <para>
/// <see cref="ModuleVisibility.Maximized"/> is 0 and is therefore the default value of
/// this type, which matches the legacy constructor: it initialised the backing field to
/// the same member. The legacy reader also folded the legacy integer sentinel for an
/// absent value, -1, into that same member rather than treating it as a further state, so
/// this type deliberately carries no "not yet chosen" member and none may be added. A
/// column that genuinely permits an absent value expresses that through a nullable
/// property on the consuming entity, never through an extra member here.
/// </para>
/// <para>
/// The member names are as much a part of the contract as the numbers: the legacy portal
/// template importer matched them as text, so they are reproduced exactly and must not be
/// renamed.
/// </para>
/// </remarks>
// MIGRATION: legacy type DotNetNuke.Entities.Modules.VisibilityState renamed to
//   ModuleVisibility. The three member names are unchanged. The legacy ordinals were
//   implicit (VB.NET declaration order) and are preserved here explicitly as
//   Maximized = 0, Minimized = 1, None = 2, which are the values persisted in the
//   TabModules.Visibility column, so they must never be renumbered.
public enum ModuleVisibility
{
    /// <summary>
    /// The module is presented in full, with its content pane expanded. Persisted as 0,
    /// and consequently the default value of <see cref="ModuleVisibility"/>.
    /// </summary>
    Maximized = 0,

    /// <summary>
    /// The module is presented collapsed: its title and container chrome remain visible
    /// while the content pane is hidden. Persisted as 1.
    /// </summary>
    Minimized = 1,

    /// <summary>
    /// The module is not presented at all. Persisted as 2. This is a genuine legacy
    /// display state meaning "not rendered"; it does not indicate that a visibility value
    /// is missing or has yet to be chosen.
    /// </summary>
    None = 2
}
