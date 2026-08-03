namespace DnnMigration.Domain.Entities;

/// <summary>
/// One name and value pair stored against a single placement of a module on a page: the persistence
/// shape of a single row of the legacy <c>dbo.TabModuleSettings</c> table.
/// </summary>
/// <remarks>
/// <para>
/// These are the settings of a <i>placement</i> rather than of a module instance, which is what
/// separates this store from <c>dbo.ModuleSettings</c>. The same module placed on two pages can be
/// configured differently, and a setting recorded here applies to one placement only. The two stores
/// are not interchangeable and neither is a fallback for the other.
/// </para>
/// <para>
/// MIGRATION: the type deliberately has no base class and no identity member. The table has no
/// surrogate key and never had one - it was created with a composite clustered primary key over
/// <see cref="TabModuleId"/> and <see cref="SettingName"/> and nothing else - so declaring an
/// <c>Identity</c>, an <c>Id</c> or a <c>TabModuleSettingId</c> here would assert a column that does
/// not exist and would compete with the explicit key the Infrastructure mapping declares. Identity
/// for these rows <b>is</b> the composite key, and keys are declared by the persistence layer:
/// Infrastructure must configure the primary key as <c>(TabModuleID, SettingName)</c>, and this
/// layer must not emulate one.
/// </para>
/// <para>
/// MIGRATION: the shape is terminal as created. <c>03.00.01.SqlDataProvider</c> lines 722-727 create
/// the table when a module stopped belonging to a single page, lines 730-735 add
/// <c>PK_TabModuleSettings PRIMARY KEY CLUSTERED (TabModuleID, SettingName)</c> and lines 746-754 add
/// the foreign key to <c>dbo.TabModules</c>. <c>03.00.09.SqlDataProvider</c> lines 328-336 then drop
/// and re-add both constraints under object-qualifier-aware names -
/// <c>PK_{objectQualifier}TabModuleSettings</c> and
/// <c>FK_{objectQualifier}TabModuleSettings_{objectQualifier}TabModules</c> - which <b>renames the
/// constraints without changing the composite-key shape or the cascade behaviour</b>; the key stays
/// <c>([TabModuleID], [SettingName])</c>. No script anywhere in the 88-script chain adds, alters or
/// drops a column on this table, and <c>03.00.03.SqlDataProvider</c> line 304 drops the redundant
/// <c>IX_TabModuleSettings</c> index that duplicated the clustered key columns, so the composite key
/// is the whole of the terminal access path.
/// </para>
/// <para>
/// MIGRATION: all three columns are <c>NOT NULL</c> in the terminal schema, so all three properties
/// are non-nullable and the navigation is non-nullable because the column carrying it is. Nothing
/// here is optional and no value stands in for absence: a setting that is not stored has no row.
/// That is the gain over the legacy representation, in which
/// <c>ModuleController.GetTabModuleSettings</c> (ModuleController.vb line 1336) handed back an
/// untyped <c>Hashtable</c> where a name that was missing and a name holding an empty string read
/// back identically. Per Rule T8 that bag of names becomes one typed row per setting; per Rule T7 no
/// property is seeded with a placeholder, because <c>Null.NullString</c> is the empty string and
/// seeding one would plant that legacy sentinel in the domain model.
/// </para>
/// <para>
/// Dictionary behaviour is deliberately absent. This type is a row, not a collection: projecting the
/// rows of one placement into a name-keyed map belongs above the domain, and the legacy one-row write
/// <c>UpdateTabModuleSetting(TabModuleId, SettingName, SettingValue)</c> (ModuleController.vb line
/// 1373) maps onto exactly one instance of this type.
/// </para>
/// </remarks>
public sealed class TabModuleSetting
{
    /// <summary>
    /// Gets or sets the <c>TabModuleID</c> column: <c>int NOT NULL</c>, the first column of the
    /// composite primary key and the foreign key to <c>dbo.TabModules</c>.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the Infrastructure mapping must bind this idiomatically named property to the
    /// legacy <c>TabModuleID</c> spelling, and must preserve the existing constraint exactly as the
    /// schema declares it - <c>FOREIGN KEY (TabModuleID) REFERENCES dbo.TabModules (TabModuleID) ON
    /// DELETE CASCADE NOT FOR REPLICATION</c>. Because the database cascades, removing a placement
    /// removes its settings without this layer doing anything, and no delete logic belongs here.
    /// </remarks>
    public int TabModuleId { get; set; }

    /// <summary>
    /// Gets or sets the <c>SettingName</c> column: <c>nvarchar(50) NOT NULL</c>, the second column of
    /// the composite primary key.
    /// </summary>
    /// <remarks>
    /// The column stores the name exactly as written, so it preserves case, and two names differing
    /// only in case are two distinct rows. Whether a lookup folds case is a query-layer decision and
    /// is deliberately not settled here.
    /// </remarks>
    public string SettingName { get; set; }

    /// <summary>
    /// Gets or sets the <c>SettingValue</c> column: <c>nvarchar(2000) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the width is 2000, declared at <c>03.00.01.SqlDataProvider</c> line 726 and never
    /// narrowed, so the Infrastructure mapping must constrain it to 2000 characters and mark it
    /// required. Because the column is <c>NOT NULL</c>, the empty string is the only way to record
    /// "stored, but blank", and nothing in this model may collapse it to <see langword="null"/>.
    /// </remarks>
    public string SettingValue { get; set; }

    /// <summary>
    /// Gets or sets the placement this setting belongs to, the loaded form of
    /// <see cref="TabModuleId"/>.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>TabModuleID</c> is <c>NOT NULL</c>: a setting
    /// without a placement to configure cannot exist. It is nonetheless left <see langword="null"/>
    /// by any query that does not load it, so reading it defensively remains correct. The inverse is
    /// <see cref="Entities.TabModule.Settings"/>.
    /// </remarks>
    public TabModule TabModule { get; set; }
}
