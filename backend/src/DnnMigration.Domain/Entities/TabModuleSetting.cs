using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One name/value setting stored against a single placement of a module on a page.
/// </summary>
/// <remarks>
/// MIGRATION: bound to <c>dbo.TabModuleSettings</c>, created by 03.00.01 together with
/// <c>TabModules</c> when a module stopped belonging to one page. Its value column is
/// <c>nvarchar(2000)</c> - eight times the width of <see cref="ModuleSetting.SettingValue"/> - so a
/// setting must not be moved between the two tables without checking length.
/// </remarks>
public sealed class TabModuleSetting : Entity<(int TabModuleId, string SettingName)>
{
    /// <summary>Gets or sets the owning placement (<c>TabModuleID</c>, first key column, cascade delete).</summary>
    public int TabModuleId { get; set; }

    /// <summary>Gets or sets the setting name (<c>SettingName</c>, second key column, 50 characters).</summary>
    public string SettingName { get; set; } = string.Empty;

    /// <inheritdoc />
    public override (int TabModuleId, string SettingName) Identity => (TabModuleId, SettingName);

    /// <summary>Gets or sets the setting value (<c>SettingValue</c>, required, 2000 characters).</summary>
    public string SettingValue { get; set; } = string.Empty;

    /// <summary>Gets or sets the placement this setting belongs to.</summary>
    public TabModule? TabModule { get; set; }
}
