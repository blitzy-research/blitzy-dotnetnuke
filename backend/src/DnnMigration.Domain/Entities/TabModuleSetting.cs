namespace DnnMigration.Domain.Entities;

/// <summary>
/// One name and value pair stored against a single placement of a module on a page: the persistence shape
/// of a single row of the legacy <c>dbo.TabModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The type deliberately has no base class and no identity member.
/// </para>
/// <para>
/// The shape is terminal as created. <c>03.00.01.SqlDataProvider</c> lines 722-727 create the table when a
/// module stopped belonging to a single page, lines 730-735 add <c>PK_TabModuleSettings PRIMARY KEY
/// CLUSTERED (TabModuleID, SettingName)</c> and lines 746-754 add the foreign key to <c>dbo.TabModules</c>.
/// <c>03.00.09.SqlDataProvider</c> lines 328-336 then drop and re-add both constraints under
/// object-qualifier-aware names - <c>PK_{objectQualifier}TabModuleSettings</c> and
/// <c>FK_{objectQualifier}TabModuleSettings_{objectQualifier}TabModules</c> - which <b>renames the
/// constraints without changing the composite-key shape or the cascade behaviour</b>; the key stays
/// <c>([TabModuleID], [SettingName])</c>.
/// </para>
/// </remarks>
public sealed class TabModuleSetting
{
    /// <summary>
    /// Gets or sets the <c>TabModuleID</c> column: <c>int NOT NULL</c>, the first column of the composite
    /// primary key and the foreign key to <c>dbo.TabModules</c>.
    /// </summary>
    /// <remarks>
    /// The Infrastructure mapping must bind this idiomatically named property to the legacy
    /// <c>TabModuleID</c> spelling, and must preserve the existing constraint exactly as the schema
    /// declares it - <c>FOREIGN KEY (TabModuleID) REFERENCES dbo.TabModules (TabModuleID) ON DELETE CASCADE
    /// NOT FOR REPLICATION</c>.
    /// </remarks>
    public int TabModuleId { get; set; }

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
    /// <remarks>
    /// The width is 2000, declared at <c>03.00.01.SqlDataProvider</c> line 726 and never narrowed, so the
    /// Infrastructure mapping must constrain it to 2000 characters and mark it required. Because the column
    /// is <c>NOT NULL</c>, the empty string is the only way to record "stored, but blank", and nothing in
    /// this model may collapse it to <see langword="null"/>.
    /// </remarks>
    public string SettingValue { get; set; }

    /// <summary>
    /// Gets or sets the placement this setting belongs to, the loaded form of <see cref="TabModuleId"/>.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>TabModuleID</c> is <c>NOT NULL</c>: a setting without a
    /// placement to configure cannot exist. It is nonetheless left <see langword="null"/> by any query that
    /// does not load it, so reading it defensively remains correct.
    /// </remarks>
    public TabModule TabModule { get; set; }
}
