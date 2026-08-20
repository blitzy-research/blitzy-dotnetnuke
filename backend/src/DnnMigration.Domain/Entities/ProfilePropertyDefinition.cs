using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// The persisted column set is taken from the upgrade chain, not from the VB class.

/// <summary>
/// The definition of one property that a portal's user profile can hold - its name, its editor type, its
/// category, its ordering, and the rules the value must satisfy.
/// </summary>
/// <remarks>
/// <para>
/// Bound by the Infrastructure layer to <c>dbo.ProfilePropertyDefinition</c>, whose table name is singular
/// in this schema. The definition describes the property; the value an individual account supplies for it
/// lives in a <see cref="UserProfileValue"/> row.
/// </para>
/// <para>
/// The unique index <c>IX_ProfilePropertyDefinition</c> spans <c>(PortalID, ModuleDefID, PropertyName)</c>
/// - 03.02.03 line 1082 - so a property name is unique per portal per contributing module definition rather
/// than globally. Because <see cref="ModuleDefinitionId"/> is nullable, a portal-wide property that belongs
/// to no module definition and a module-contributed property of the same name can coexist in one portal.
/// </para>
/// </remarks>
public sealed class ProfilePropertyDefinition : Entity<int>
{
    /// <summary>Gets or sets the surrogate key of this definition.</summary>
    /// <value>
    /// The value of the <c>PropertyDefinitionID</c> column, declared <c>int IDENTITY(1,1) NOT NULL</c> at
    /// 03.02.03 line 1064 and made the clustered primary key <c>PK_ProfilePropertyDefinition</c> at line
    /// 1080.
    /// </value>
    /// <remarks>
    /// MIGRATION: the legacy field initialised to <c>Null.NullInteger</c> - that is, to -1 - at
    /// ProfilePropertyDefinition.vb line 54. The column is NOT NULL, so the property stays a non-nullable
    /// <see cref="int"/> and the sentinel is not reproduced.
    /// </remarks>
    public int PropertyDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the portal that owns this definition, or <see langword="null"/> when the definition is
    /// host-level and therefore shared rather than owned by one portal.
    /// </summary>
    /// <value>
    /// The value of the <c>PortalID</c> column, constrained by <c>FK_ProfilePropertyDefinition_Portals</c>
    /// to <c>Portals(PortalID)</c> with <c>ON DELETE CASCADE</c>, and the leading member of the unique
    /// index over portal, module definition and property name.
    /// </value>
    /// <remarks>
    /// This column changed from required to nullable, and the data changed with it. 03.02.03 line 1065
    /// declares <c>PortalID int NOT NULL</c>, in which -1 denoted the host-level definition. 03.03.03 lines
    /// 77-78 then run <c>ALTER COLUMN PortalID int NULL</c> under the heading "Change
    /// ProfilePropertyDefinition to use NULL instead of -1 for the Host Portal", and lines 81-83 migrate
    /// the existing rows with <c>UPDATE ... SET PortalId = NULL WHERE PortalId = -1</c>.
    /// </remarks>
    public int? PortalId { get; set; }

    /// <summary>
    /// Gets or sets the module definition that contributes this property, or <see langword="null"/> when
    /// the property is portal-wide and belongs to no module definition.
    /// </summary>
    /// <value>The value of the <c>ModuleDefID</c> column, declared <c>int NULL</c> at 03.02.03 line 1066.</value>
    public int? ModuleDefinitionId { get; set; }

    /// <summary>Gets or sets a value indicating whether this definition has been withdrawn.</summary>
    /// <value>The value of the <c>Deleted</c> column, declared <c>bit NOT NULL</c> at 03.02.03 line 1067.</value>
    /// <remarks>
    /// Withdrawal is logical because <see cref="UserProfileValue"/> rows reference the definition. A caller
    /// listing definitions for a portal therefore has to say whether withdrawn ones are wanted; the flag is
    /// never filtered unconditionally in the layers above.
    /// </remarks>
    public bool IsDeleted { get; set; }

    /// <summary>Gets or sets the editor type that renders and validates this property.</summary>
    /// <value>The value of the <c>DataType</c> column, declared <c>int NOT NULL</c> at 03.02.03 line 1068.</value>
    /// <remarks>
    /// Deliberately an <see cref="int"/> and not an enumeration. The legacy property carried
    /// <c>List("DataType", "", ListBoundField.Id, ListBoundField.Value)</c>, which resolved the value
    /// against rows of the <c>Lists</c> table at run time rather than against a fixed set of members, so
    /// the admissible values are data rather than code and an enumeration would misrepresent them.
    /// </remarks>
    public int DataType { get; set; }

    /// <summary>
    /// Gets or sets the value applied to a property the account has not filled in, or <see
    /// langword="null"/> when there is no default.
    /// </summary>
    /// <value>The value of the nullable <c>DefaultValue</c> column.</value>
    /// <remarks>
    /// The terminal SQL type is <c>ntext</c>, not <c>nvarchar</c>. 03.02.03 line 1069 declares
    /// <c>DefaultValue nvarchar(50) NULL</c>, and 04.05.00 lines 1593-1594 widen it with <c>ALTER COLUMN
    /// DefaultValue ntext NULL</c> under the heading "Update DefaultValue in ProfilePropertyDefinition to
    /// nText".
    /// </remarks>
    public string? DefaultValue { get; set; }

    /// <summary>Gets or sets the heading under which this property is grouped on the profile form.</summary>
    /// <value>
    /// The value of the <c>PropertyCategory</c> column, declared <c>nvarchar(50) NOT NULL</c> at 03.02.03
    /// line 1070.
    /// </value>
    /// <remarks>
    /// Non-nullable because the column is, and deliberately not initialised to an empty string: the legacy
    /// field at line 53 defaulted to <c>Nothing</c>, and pre-filling it with the empty string would install
    /// the legacy <c>Null.NullString</c> sentinel - in which "" stands for absence - as this entity's
    /// default. An empty value here is a category of zero length, nothing more.
    /// </remarks>
    public string PropertyCategory { get; set; }

    /// <summary>Gets or sets the name that identifies this property within its portal.</summary>
    /// <value>
    /// The value of the <c>PropertyName</c> column, declared <c>nvarchar(50) NOT NULL</c> at 03.02.03 line
    /// 1071, the trailing member of the unique index over portal, module definition and property name, and
    /// indexed on its own by <c>IX_ProfilePropertyDefinition_PropertyName</c> at line 1083.
    /// </value>
    /// <remarks>
    /// A real business rule was attached to this property by an attribute that cannot come with it.
    /// </remarks>
    public string PropertyName { get; set; }

    /// <summary>
    /// Gets or sets the maximum length accepted for a value of this property, zero meaning that no explicit
    /// bound is imposed.
    /// </summary>
    /// <value>
    /// The value of the <c>Length</c> column, declared <c>int NOT NULL</c> at 03.02.03 line 1072 with the
    /// database default <c>DF_ProfilePropertyDefinition_Length DEFAULT 0</c>.
    /// </value>
    /// <remarks>
    /// The column default is a database fact and stays in the entity configuration. The property is left at
    /// the CLR default of zero rather than being initialised here, so that a deliberate zero and an unset
    /// value remain the same thing, exactly as the column's own default makes them.
    /// </remarks>
    public int Length { get; set; }

    /// <summary>Gets or sets a value indicating whether an account must supply a value for this property.</summary>
    /// <value>The value of the <c>Required</c> column, declared <c>bit NOT NULL</c> at 03.02.03 line 1073.</value>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Gets or sets the regular expression a value of this property must match, or <see langword="null"/>
    /// when no pattern is imposed.
    /// </summary>
    /// <value>The value of the nullable <c>ValidationExpression</c> column.</value>
    /// <remarks>
    /// The terminal width is 2000 characters, not the original 100. 03.02.03 line 1074 declares
    /// <c>ValidationExpression nvarchar(100) NULL</c> and 04.03.05 lines 16-17 widen it with <c>ALTER
    /// COLUMN ValidationExpression nvarchar(2000)</c>.
    /// </remarks>
    public string? ValidationExpression { get; set; }

    /// <summary>Gets or sets the position of this property within its category on the profile form.</summary>
    /// <value>The value of the <c>ViewOrder</c> column, declared <c>int NOT NULL</c> at 03.02.03 line 1075.</value>
    public int ViewOrder { get; set; }

    /// <summary>Gets or sets a value indicating whether this property is shown on the profile form at all.</summary>
    /// <value>The value of the <c>Visible</c> column, declared <c>bit NOT NULL</c> at 03.02.03 line 1076.</value>
    public bool IsVisible { get; set; }

    /// <summary>Gets the value that identifies this definition for the purposes of equality.</summary>
    /// <remarks>
    /// Forwards <see cref="PropertyDefinitionId"/>, keeping the legacy-named identity property as the
    /// single mapped column. Equality itself, and the rule that an identity counts only once the
    /// persistence layer has declared it persisted, belong to <see cref="Entity{TId}"/>.
    /// </remarks>
    public override int Identity => PropertyDefinitionId;

    /// <summary>
    /// Gets or sets the portal that owns this definition, or <see langword="null"/> when the definition is
    /// host-level or the reference has simply not been loaded.
    /// </summary>
    public Portal? Portal { get; set; }

    /// <summary>
    /// Gets or sets the module definition that contributes this property, or <see langword="null"/> when
    /// the property is portal-wide or the reference has not been loaded.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="ModuleDefinition.ProfilePropertyDefinitions"/>. Optional, because
    /// <c>ModuleDefID</c> is nullable and no foreign key enforces it.
    /// </remarks>
    public ModuleDefinition? ModuleDefinition { get; set; }

    /// <summary>Gets the values that accounts have supplied for this property.</summary>
    public ICollection<UserProfileValue> ProfileValues { get; } = new List<UserProfileValue>();
}
