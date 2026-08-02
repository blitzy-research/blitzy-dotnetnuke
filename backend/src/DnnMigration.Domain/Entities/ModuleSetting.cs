using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One name/value setting stored against a module instance.
/// </summary>
/// <remarks>
/// MIGRATION: the legacy <c>Hashtable</c> exposed by <c>ModuleInfo.ModuleSettings</c> becomes a real
/// entity over <c>dbo.ModuleSettings</c>, whose composite primary key
/// <c>(ModuleID, SettingName)</c> was added in 02.00.01. Settings that vary per placement live on
/// <see cref="TabModuleSetting"/> instead.
/// </remarks>
public sealed class ModuleSetting : Entity<(int ModuleId, string SettingName)>
{
    /// <summary>Gets or sets the owning module (<c>ModuleID</c>, first key column, cascade delete).</summary>
    public int ModuleId { get; set; }

    /// <summary>Gets or sets the setting name (<c>SettingName</c>, second key column, 50 characters).</summary>
    public string SettingName { get; set; } = string.Empty;

    /// <inheritdoc />
    public override (int ModuleId, string SettingName) Identity => (ModuleId, SettingName);

    /// <summary>
    /// Gets or sets the setting value (<c>SettingValue</c>, required, 256 characters).
    /// </summary>
    /// <remarks>
    /// MIGRATION: the column is <c>NOT NULL</c>, and the legacy <c>Null.NullString</c> sentinel is
    /// the empty string, so an absent value is stored as <see cref="string.Empty"/> rather than as
    /// <see langword="null"/>. Preserving that distinction is what keeps a round trip through this
    /// entity byte-identical to the legacy one.
    /// </remarks>
    public string SettingValue { get; set; } = string.Empty;

    /// <summary>Gets or sets the module this setting belongs to.</summary>
    public Module? Module { get; set; }
}
