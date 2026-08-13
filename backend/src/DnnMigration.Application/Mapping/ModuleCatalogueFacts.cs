using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// The catalogue facts a module response reports about the definition and package a module was created
/// from, resolved once by the caller and projected identically by every module contract.
/// </summary>
/// <param name="DesktopModuleId">
/// The key of the package the definition belongs to, or <see langword="null"/> when the definition could
/// not be resolved.
/// </param>
/// <param name="FriendlyName">The definition's display name, or <see langword="null"/> when unresolved.</param>
/// <param name="ModuleName">The package's name, or <see langword="null"/> when unresolved.</param>
/// <param name="Description">The package's description, or <see langword="null"/> when unresolved.</param>
/// <param name="Version">The package's version, or <see langword="null"/> when unresolved.</param>
/// <remarks>
/// Absence is expressed as <see langword="null"/> throughout, on every member including the key. A module
/// whose definition was withdrawn from its tenant, or whose package row is missing, genuinely has no name,
/// description or version to report, and saying so is not the same as reporting an empty one.
/// </remarks>
public sealed record ModuleCatalogueFacts(
    int? DesktopModuleId,
    string? FriendlyName,
    string? ModuleName,
    string? Description,
    string? Version)
{
    /// <summary>Resolves the catalogue facts from a definition row and the package row it belongs to.</summary>
    /// <param name="definition">The definition, or <see langword="null"/> when it could not be resolved.</param>
    /// <param name="package">
    /// The package the definition belongs to, or <see langword="null"/> when it could not be resolved.
    /// </param>
    /// <returns>The facts, with <see langword="null"/> for anything the two rows do not supply.</returns>
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

    /// <summary>Resolves the catalogue facts from the navigations a module carries.</summary>
    /// <param name="module">The module whose definition navigation is read.</param>
    /// <returns>The facts, with <see langword="null"/> for anything the navigations do not supply.</returns>
    public static ModuleCatalogueFacts FromNavigation(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return From(module.ModuleDefinition, module.ModuleDefinition?.DesktopModule);
    }
}
