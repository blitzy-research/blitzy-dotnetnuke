using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// The catalogue facts a module response reports about the definition and package a module was created
/// from, resolved once by the caller and projected identically by every module contract.
/// </summary>
/// <param name="DesktopModuleId">
/// The key of the package the definition belongs to, or <see langword="null"/> when the definition could
/// not be resolved. Never zero: <c>dbo.DesktopModules.DesktopModuleID</c> is a plain <c>IDENTITY</c>, so
/// it seeds at one, and <c>dbo.ModuleDefinitions.DesktopModuleID</c> is declared <c>NOT NULL</c> with a
/// foreign key onto it - a resolved definition therefore always carries a positive key, and the absence
/// of one is an absence rather than a zero.
/// </param>
/// <param name="FriendlyName">The definition's display name, or <see langword="null"/> when unresolved.</param>
/// <param name="ModuleName">The package's name, or <see langword="null"/> when unresolved.</param>
/// <param name="Description">The package's description, or <see langword="null"/> when unresolved.</param>
/// <param name="Version">The package's version, or <see langword="null"/> when unresolved.</param>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS. The five values below live on <c>dbo.ModuleDefinitions</c> and
/// <c>dbo.DesktopModules</c>, two tables away from the module row a module response is built around, and
/// each module projection previously reached them a different way: the create path had them in hand but
/// projected through an unloaded navigation and reported a fabricated zero; the single read reached them
/// through a navigation that loaded the definition but not the package, so three of the five were always
/// null; and the listing carried none of them at all. The same module consequently described its own
/// package differently depending on which endpoint was asked, which no consumer can reconcile.
/// </para>
/// <para>
/// Passing the facts in as one argument makes the resolution the CALLER's, once per request, and makes
/// the projection incapable of silently depending on whether some navigation happened to be loaded. A
/// listing resolves the whole page's definitions in one read and hands each row its entry; a single read
/// resolves one; the create path already holds both rows and simply forwards them. Every path therefore
/// projects from the same five values, so the three contracts cannot disagree again.
/// </para>
/// <para>
/// Absence is expressed as <see langword="null"/> throughout, on every member including the key. A
/// module whose definition was withdrawn from its tenant, or whose package row is missing, genuinely has
/// no name, description or version to report, and saying so is not the same as reporting an empty one.
/// </para>
/// </remarks>
public sealed record ModuleCatalogueFacts(
    int? DesktopModuleId,
    string? FriendlyName,
    string? ModuleName,
    string? Description,
    string? Version)
{
    /// <summary>
    /// Resolves the catalogue facts from a definition row and the package row it belongs to.
    /// </summary>
    /// <param name="definition">The definition, or <see langword="null"/> when it could not be resolved.</param>
    /// <param name="package">
    /// The package the definition belongs to, or <see langword="null"/> when it could not be resolved.
    /// A definition may resolve while its package does not, so the two are supplied separately rather
    /// than reached one through the other.
    /// </param>
    /// <returns>The facts, with <see langword="null"/> for anything the two rows do not supply.</returns>
    /// <remarks>
    /// The package is preferred over the definition's own navigation when supplied, and the navigation is
    /// the fallback, so a caller that resolved the package explicitly is never second-guessed while a
    /// caller that relied on eager loading still gets an answer.
    /// </remarks>
    public static ModuleCatalogueFacts From(ModuleDefinition? definition, DesktopModule? package = null)
    {
        DesktopModule? resolved = package ?? definition?.DesktopModule;

        return new ModuleCatalogueFacts(
            definition?.DesktopModuleId,
            definition?.FriendlyName,
            resolved?.ModuleName,
            resolved?.Description,
            resolved?.Version);
    }

    /// <summary>
    /// Resolves the catalogue facts from the navigations a module carries.
    /// </summary>
    /// <param name="module">The module whose definition navigation is read.</param>
    /// <returns>The facts, with <see langword="null"/> for anything the navigations do not supply.</returns>
    /// <remarks>
    /// The fallback for a projection whose caller resolved nothing. It answers correctly only when the
    /// module was read with its definition and package loaded, which is why the module repository loads
    /// both on every read that a projection can reach.
    /// </remarks>
    public static ModuleCatalogueFacts FromNavigation(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return From(module.ModuleDefinition, module.ModuleDefinition?.DesktopModule);
    }
}
