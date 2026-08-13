namespace DnnMigration.Domain.Entities;

/// <summary>
/// One name and value pair stored against a module instance: the persistence shape of a single row of the
/// legacy <c>dbo.ModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The type deliberately has no base class and no identity member. Every other entity in this model has one
/// identity column and derives from the identity-bearing base type; this table has none, so declaring an
/// identity here would assert a column that does not exist.
/// </para>
/// <para>
/// All three columns are <c>NOT NULL</c>, so all three properties are non-nullable, and the navigation is
/// non-nullable because the column that carries it is. Nothing is optional and no value stands in for
/// absence: a setting that is not stored has no row.
/// </para>
/// </remarks>
public sealed class ModuleSetting
{
    /// <summary>
    /// Gets or sets the <c>ModuleID</c> column: <c>int NOT NULL</c>, the first column of the composite
    /// primary key and the foreign key to <c>dbo.Modules</c>.
    /// </summary>
    /// <remarks>
    /// Zero is a real module. <c>dbo.Modules.ModuleID</c> is <c>IDENTITY(0, 1)</c>, so the first module of
    /// an installation is numbered zero and this value may never be read as "no module".
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// Gets or sets the <c>SettingName</c> column: <c>nvarchar(50) NOT NULL</c>, the second column of the
    /// composite primary key.
    /// </summary>
    /// <remarks>
    /// The column stores the name exactly as written, so it preserves case, and two names differing only in
    /// case are two distinct rows. Whether a lookup folds case is a query-layer decision and is
    /// deliberately not settled here.
    /// </remarks>
    public string SettingName { get; set; }

    /// <summary>Gets or sets the <c>SettingValue</c> column: <c>nvarchar(2000) NOT NULL</c>.</summary>
    public string SettingValue { get; set; }

    /// <summary>Gets or sets the module this setting belongs to.</summary>
    /// <remarks>
    /// Required rather than optional, because <c>ModuleID</c> is <c>NOT NULL</c> and its constraint
    /// cascades: a setting cannot outlive its module, so it cannot exist without one. It is nonetheless
    /// left <see langword="null"/> by any query that does not load it, which is why reading it defensively
    /// is still correct.
    /// </remarks>
    public Module Module { get; set; }
}
