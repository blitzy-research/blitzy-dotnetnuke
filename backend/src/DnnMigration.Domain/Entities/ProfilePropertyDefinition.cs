using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: re-authored from Library/Components/Users/Profile/ProfilePropertyDefinition.vb, a
// 402-line VB.NET class declaring fifteen properties. The thirteen persisted scalars below are not
// those fifteen minus two: the legacy property set and the real column set differ in four specific
// places, and each difference is called out at the member it concerns, because a line-by-line
// translation would either reproduce something that is not state or drop something that is.
//
//   15 legacy properties
//    - IsDirty        edit bookkeeping, never persisted (line 127)
//    - Visibility     no column on this table backs it (line 336)
//    - PropertyValue  a value of the property, which lives in dbo.UserProfile (line 246)
//    + Deleted        a required column the legacy class declares no property for
//   = 13 persisted scalars
//
// MIGRATION: the persisted column set is taken from the upgrade chain, not from the VB class. The
// table arrives whole at
// Website/Providers/DataProviders/SqlDataProvider/03.02.03.SqlDataProvider line 1062 with thirteen
// columns - re-emitted identically, and still guarded by IF NOT EXISTS, at 04.00.04 line 1107 - and
// is then altered exactly four times across the remaining scripts. A case-insensitive search over
// all four object-naming forms the chain uses (bare, dbo.-qualified, bracketed and
// {databaseOwner}{objectQualifier}-templated) finds no other change to its shape:
//
//   03.03.03 lines 77-78   ALTER COLUMN PortalID int NULL
//   03.03.03 lines 81-83   UPDATE ... SET PortalId = NULL WHERE PortalId = -1
//   04.03.05 lines 16-17   ALTER COLUMN ValidationExpression nvarchar(2000)
//   04.05.00 lines 1593-94 ALTER COLUMN DefaultValue ntext NULL
//
// The 03.03.03 pair is repeated verbatim at 04.03.03 lines 77-88 for the 4.x upgrade line, and the
// same block adds FK_ProfilePropertyDefinition_Portals over PortalID with ON DELETE CASCADE.
// Everything after that - 03.02.06, 03.03.02, 04.03.02, 04.06.00 and 04.07.00 - rewrites stored
// procedures or moves data and never touches a column.
//
// MIGRATION: Deleted has no counterpart among the fifteen VB properties, yet
// 03.02.03 line 1066 declares it "Deleted bit NOT NULL" and the read paths filter on it. Omitting
// it because the legacy class omitted it would silently drop a required column from the model and
// turn every withdrawn definition back into a live one. It is carried here as IsDeleted.
//
// MIGRATION: the constructors are deliberately gone. ProfilePropertyDefinition.vb line 67 reads the
// ambient tenant through PortalController.GetCurrentPortalSettings() and line 351 reads a module
// setting through UserModuleBase.GetSetting(PortalId, "Profile_DefaultVisibility"), so merely
// constructing the legacy object performed I/O against request state and configuration. Rule T6
// forbids that here; the tenant and any default come from the Application service that builds the
// entity.
//
// MIGRATION: the dirty-tracking apparatus is gone too. Every legacy setter ran
// "If _X <> Value Then _IsDirty = True", exposed the result through the read-only IsDirty property
// at line 127, and offered ClearIsDirty() at line 371 to reset it, with Clone() at line 376
// rebuilding an instance and clearing the flag by hand. That is change tracking, which the
// persistence layer now owns, so the properties below are plain auto-properties with no behaviour.
//
// MIGRATION OBLIGATION FOR INFRASTRUCTURE: four of the properties below are renamed relative to
// their columns, so the entity configuration has to state every one of those mappings explicitly -
// convention will not find them:
//
//   ModuleDefinitionId   -> ModuleDefID
//   IsDeleted            -> Deleted
//   IsRequired           -> Required
//   IsVisible            -> Visible
//
// It must also reproduce the existing unique index over (PortalID, ModuleDefID, PropertyName)
// (03.02.03 line 1082) and the non-unique index over PropertyName (line 1083), and it must pin the
// two terminal column types that the upgrade chain widened - DefaultValue as ntext and
// ValidationExpression at 2000 characters - rather than the narrower originals. The index is not
// decoration: it is what makes a property name unique per portal per contributing module definition,
// and the uniqueness rule is unenforceable without it.
//
// MIGRATION: every attribute is dropped. The type carried
// <XmlRoot("profiledefinition", IsNullable:=False)> at line 43 and the properties carried Editor,
// List, IsReadOnly, Required, SortOrder, Browsable, XmlElement, XmlIgnore and
// RegularExpressionValidator from DotNetNuke.UI.WebControls and System.Xml.Serialization. The wire
// contract belongs to the DTOs, validation belongs to the Application layer, and editor metadata
// belongs to the Angular components, so no attribute of any kind survives - see the note on
// PropertyName for the one rule that had to be rescued from an attribute before it was discarded.

/// <summary>
/// The definition of one property that a portal's user profile can hold - its name, its editor
/// type, its category, its ordering, and the rules the value must satisfy.
/// </summary>
/// <remarks>
/// <para>
/// Bound by the Infrastructure layer to <c>dbo.ProfilePropertyDefinition</c>, whose table name is
/// singular in this schema. The definition describes the property; the value an individual account
/// supplies for it lives in a <see cref="UserProfileValue"/> row.
/// </para>
/// <para>
/// The unique index <c>IX_ProfilePropertyDefinition</c> spans
/// <c>(PortalID, ModuleDefID, PropertyName)</c> - 03.02.03 line 1082 - so a property name is unique
/// per portal per contributing module definition rather than globally. Because
/// <see cref="ModuleDefinitionId"/> is nullable, a portal-wide property that belongs to no module
/// definition and a module-contributed property of the same name can coexist in one portal.
/// </para>
/// <para>
/// Withdrawal is logical, through <see cref="IsDeleted"/>, not physical: profile values reference
/// the definition, so a definition is flagged rather than removed and the stored values survive.
/// </para>
/// </remarks>
public sealed class ProfilePropertyDefinition : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate key of this definition.
    /// </summary>
    /// <value>
    /// The value of the <c>PropertyDefinitionID</c> column, declared
    /// <c>int IDENTITY(1,1) NOT NULL</c> at 03.02.03 line 1064 and made the clustered primary key
    /// <c>PK_ProfilePropertyDefinition</c> at line 1080.
    /// </value>
    /// <remarks>
    /// MIGRATION: the legacy field initialised to <c>Null.NullInteger</c> - that is, to -1 - at
    /// ProfilePropertyDefinition.vb line 54. The column is NOT NULL, so the property stays a
    /// non-nullable <see cref="int"/> and the sentinel is not reproduced. Unlike the seeds of
    /// <c>Portals</c>, <c>Roles</c>, <c>Tabs</c> and <c>Modules</c>, this identity seeds at 1, so no
    /// real key of this table collides with 0 or -1; that is a property of this table alone and is
    /// not a licence to read either value as "absent" anywhere else.
    /// </remarks>
    public int PropertyDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the portal that owns this definition, or <see langword="null"/> when the
    /// definition is host-level and therefore shared rather than owned by one portal.
    /// </summary>
    /// <value>
    /// The value of the <c>PortalID</c> column, constrained by
    /// <c>FK_ProfilePropertyDefinition_Portals</c> to <c>Portals(PortalID)</c> with
    /// <c>ON DELETE CASCADE</c>, and the leading member of the unique index over portal, module
    /// definition and property name.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: this column changed from required to nullable, and the data changed with it.
    /// 03.02.03 line 1065 declares <c>PortalID int NOT NULL</c>, in which -1 denoted the host-level
    /// definition. 03.03.03 lines 77-78 then run <c>ALTER COLUMN PortalID int NULL</c> under the
    /// heading "Change ProfilePropertyDefinition to use NULL instead of -1 for the Host Portal",
    /// and lines 81-83 migrate the existing rows with
    /// <c>UPDATE ... SET PortalId = NULL WHERE PortalId = -1</c>. Host-level ownership is a SQL null
    /// in the terminal schema, which is why this property is <c>int?</c> and not <c>int</c>: a
    /// non-nullable mapping cannot even materialise such a row.
    /// </para>
    /// <para>
    /// MIGRATION: never write -1 back into this property to mean "no portal", and never read a
    /// negative or zero value as absence. <c>Portals.PortalID</c> is declared
    /// <c>IDENTITY(-1, 1)</c>, so -1 is a genuine, addressable portal and 0 is the next one an
    /// installation creates. Before 03.03.03 the two meanings of -1 were indistinguishable in this
    /// column; the upgrade separated them permanently, and reintroducing the sentinel inside the
    /// domain would collapse them again and hand every host-level definition to the portal
    /// identified by -1. Absence is expressed here only by <see langword="null"/>.
    /// </para>
    /// <para>
    /// MIGRATION: where a contract outside the domain still publishes the legacy -1 encoding -
    /// <c>ProfilePropertyDefinitionDto</c> does, deliberately, because -1 is what the legacy class
    /// published to its consumers - the translation between that encoding and this null is a mapping
    /// concern and belongs in the Application mapper. It is a one-way concession to an externally
    /// observable wire contract, and it must never travel back inward.
    /// </para>
    /// </remarks>
    public int? PortalId { get; set; }

    /// <summary>
    /// Gets or sets the module definition that contributes this property, or <see langword="null"/>
    /// when the property is portal-wide and belongs to no module definition.
    /// </summary>
    /// <value>
    /// The value of the <c>ModuleDefID</c> column, declared <c>int NULL</c> at 03.02.03 line 1066.
    /// </value>
    /// <remarks>
    /// MIGRATION: the legacy field also initialised to <c>Null.NullInteger</c>
    /// (ProfilePropertyDefinition.vb line 51) and the legacy property was
    /// <c>&lt;Browsable(False)&gt;</c> <see cref="int"/> at line 159, so -1 stood in for "no module
    /// definition". The column has always been nullable, so the sentinel is replaced by
    /// <see langword="null"/> outright. Note that the upgrade chain never declares a foreign key
    /// over this column: the relationship is real and is modelled, but the database enforces
    /// nothing, so nothing here may assume referential integrity.
    /// </remarks>
    public int? ModuleDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this definition has been withdrawn.
    /// </summary>
    /// <value>
    /// The value of the <c>Deleted</c> column, declared <c>bit NOT NULL</c> at 03.02.03 line 1067.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: this property has no counterpart in the legacy class, which declares no field and
    /// no property for the column across all 402 lines of
    /// Library/Components/Users/Profile/ProfilePropertyDefinition.vb. It exists here because the
    /// column exists and is required, and because the read paths filter on it: a model that omitted
    /// it would lose a real column and silently resurrect every withdrawn definition.
    /// </para>
    /// <para>
    /// Withdrawal is logical because <see cref="UserProfileValue"/> rows reference the definition.
    /// A caller listing definitions for a portal therefore has to say whether withdrawn ones are
    /// wanted; the flag is never filtered unconditionally in the layers above.
    /// </para>
    /// </remarks>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the editor type that renders and validates this property.
    /// </summary>
    /// <value>
    /// The value of the <c>DataType</c> column, declared <c>int NOT NULL</c> at 03.02.03 line 1068.
    /// </value>
    /// <remarks>
    /// MIGRATION: deliberately an <see cref="int"/> and not an enumeration. The legacy property
    /// (ProfilePropertyDefinition.vb line 91) carried
    /// <c>List("DataType", "", ListBoundField.Id, ListBoundField.Value)</c>, which resolved the
    /// value against rows of the <c>Lists</c> table at run time rather than against a fixed set of
    /// members, so the admissible values are data rather than code and an enumeration would
    /// misrepresent them. The legacy field also initialised to <c>Null.NullInteger</c> at line 47,
    /// but the column is NOT NULL, so the property is non-nullable and the sentinel is not carried.
    /// </remarks>
    public int DataType { get; set; }

    /// <summary>
    /// Gets or sets the value applied to a property the account has not filled in, or
    /// <see langword="null"/> when there is no default.
    /// </summary>
    /// <value>The value of the nullable <c>DefaultValue</c> column.</value>
    /// <remarks>
    /// MIGRATION: the terminal SQL type is <c>ntext</c>, not <c>nvarchar</c>. 03.02.03 line 1069
    /// declares <c>DefaultValue nvarchar(50) NULL</c>, and 04.05.00 lines 1593-1594 widen it with
    /// <c>ALTER COLUMN DefaultValue ntext NULL</c> under the heading "Update DefaultValue in
    /// ProfilePropertyDefinition to nText". The Infrastructure configuration must pin that terminal
    /// type explicitly and must not impose the original 50-character bound, which would truncate or
    /// reject a legitimate stored default on a real installation.
    /// </remarks>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Gets or sets the heading under which this property is grouped on the profile form.
    /// </summary>
    /// <value>
    /// The value of the <c>PropertyCategory</c> column, declared <c>nvarchar(50) NOT NULL</c> at
    /// 03.02.03 line 1070.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy property was marked <c>Required(True)</c> with <c>SortOrder(2)</c> at
    /// ProfilePropertyDefinition.vb line 193. The requirement survives as a validation rule in the
    /// Application layer and as <c>IsRequired()</c> in the entity configuration; the ordering hint
    /// was Web Forms editor metadata and does not survive at all.
    /// </para>
    /// <para>
    /// Non-nullable because the column is, and deliberately not initialised to an empty string: the
    /// legacy field at line 53 defaulted to <c>Nothing</c>, and pre-filling it with the empty string
    /// would install the legacy <c>Null.NullString</c> sentinel - in which "" stands for absence -
    /// as this entity's default. An empty value here is a category of zero length, nothing more.
    /// </para>
    /// </remarks>
    public string PropertyCategory { get; set; }

    /// <summary>
    /// Gets or sets the name that identifies this property within its portal.
    /// </summary>
    /// <value>
    /// The value of the <c>PropertyName</c> column, declared <c>nvarchar(50) NOT NULL</c> at
    /// 03.02.03 line 1071, the trailing member of the unique index over portal, module definition
    /// and property name, and indexed on its own by
    /// <c>IX_ProfilePropertyDefinition_PropertyName</c> at line 1083.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: a real business rule was attached to this property by an attribute that cannot
    /// come with it. ProfilePropertyDefinition.vb line 228 decorates it with
    /// <c>RegularExpressionValidator("^[a-zA-Z0-9._%\-+']+$")</c> - letters, digits, dot,
    /// underscore, percent, hyphen, plus and apostrophe, one or more of them, anchored at both ends
    /// - alongside <c>Required(True)</c>, <c>IsReadOnly(True)</c> and <c>SortOrder(0)</c>. The
    /// attribute is Web Forms editor metadata and is dropped, but the pattern is a constraint on
    /// what a name may contain, and dropping it would quietly widen what the system accepts.
    /// </para>
    /// <para>
    /// MIGRATION OBLIGATION: the Application layer must enforce that exact pattern, unmodified,
    /// through the FluentValidation rule for the profile-definition request, together with the
    /// 50-character bound the column imposes. Do not re-attach it here - the domain carries no
    /// attributes - and do not re-derive or "tidy" the expression, because it is a preserved legacy
    /// rule and any change to it changes which names the system accepts.
    /// </para>
    /// <para>
    /// MIGRATION: <c>IsReadOnly(True)</c> made the name uneditable in the legacy property editor
    /// once a definition existed. That is a screen-level affordance, not a column constraint, so it
    /// is reproduced by the Angular form and by the update path rather than by this property, which
    /// stays settable so that the persistence layer can materialise a row into it.
    /// </para>
    /// <para>
    /// Non-nullable because the column is, and, as with <see cref="PropertyCategory"/>, not
    /// initialised to an empty string: the legacy field at line 55 defaulted to <c>Nothing</c>, and
    /// the empty string is the legacy sentinel for an absent string, which the domain does not
    /// reproduce.
    /// </para>
    /// </remarks>
    public string PropertyName { get; set; }

    /// <summary>
    /// Gets or sets the maximum length accepted for a value of this property, zero meaning that no
    /// explicit bound is imposed.
    /// </summary>
    /// <value>
    /// The value of the <c>Length</c> column, declared <c>int NOT NULL</c> at 03.02.03 line 1072
    /// with the database default <c>DF_ProfilePropertyDefinition_Length DEFAULT 0</c>.
    /// </value>
    /// <remarks>
    /// MIGRATION: the column default is a database fact and stays in the entity configuration. The
    /// property is left at the CLR default of zero rather than being initialised here, so that a
    /// deliberate zero and an unset value remain the same thing, exactly as the column's own default
    /// makes them.
    /// </remarks>
    public int Length { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an account must supply a value for this property.
    /// </summary>
    /// <value>
    /// The value of the <c>Required</c> column, declared <c>bit NOT NULL</c> at 03.02.03 line 1073.
    /// </value>
    /// <remarks>
    /// MIGRATION: the legacy property is named <c>Required</c> (ProfilePropertyDefinition.vb
    /// line 264). It is renamed here to read as the predicate it is and to keep it clear of the
    /// <c>required</c> contextual keyword and of the validation attribute of the same name; the
    /// column keeps its legacy name and the entity configuration states the mapping explicitly.
    /// </remarks>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Gets or sets the regular expression a value of this property must match, or
    /// <see langword="null"/> when no pattern is imposed.
    /// </summary>
    /// <value>The value of the nullable <c>ValidationExpression</c> column.</value>
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal width is 2000 characters, not the original 100. 03.02.03 line 1074
    /// declares <c>ValidationExpression nvarchar(100) NULL</c> and 04.03.05 lines 16-17 widen it
    /// with <c>ALTER COLUMN ValidationExpression nvarchar(2000)</c>. The Infrastructure
    /// configuration must carry the terminal 2000, because the original bound would reject
    /// expressions a real installation already stores.
    /// </para>
    /// <para>
    /// This is administrator-supplied data - the pattern an account's value must satisfy - and is
    /// not the same thing as the fixed pattern that constrains <see cref="PropertyName"/> itself.
    /// It is stored, never compiled here: applying it is the Application layer's concern, and
    /// nothing in the domain executes it.
    /// </para>
    /// </remarks>
    public string? ValidationExpression { get; set; }

    /// <summary>
    /// Gets or sets the position of this property within its category on the profile form.
    /// </summary>
    /// <value>
    /// The value of the <c>ViewOrder</c> column, declared <c>int NOT NULL</c> at 03.02.03
    /// line 1075.
    /// </value>
    /// <remarks>
    /// MIGRATION: this is stored presentation order, which is why it survives while the legacy
    /// <c>SortOrder</c> attributes - editor metadata with no column behind them - do not. The
    /// legacy property was <c>Required(True)</c> with <c>SortOrder(8)</c> at
    /// ProfilePropertyDefinition.vb line 300.
    /// </remarks>
    public int ViewOrder { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this property is shown on the profile form at all.
    /// </summary>
    /// <value>
    /// The value of the <c>Visible</c> column, declared <c>bit NOT NULL</c> at 03.02.03 line 1076.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: renamed from the legacy <c>Visible</c> (ProfilePropertyDefinition.vb line 318) to
    /// read as a predicate; the column keeps its legacy name.
    /// </para>
    /// <para>
    /// MIGRATION: this is the only visibility this table stores, and it is a property of the
    /// definition, not of an account. The legacy class also exposed a <c>Visibility</c> property of
    /// type <c>UserVisibilityMode</c> at line 336, defaulted to <c>AdminOnly</c> at line 61 and
    /// populated by the constructor from the "Profile_DefaultVisibility" module setting at
    /// lines 348-359. No column of the definition table backs it - the thirteen columns above are
    /// the whole table - so it is not state of this entity and is absent here. Who may see an
    /// individual account's value is stored per value, on
    /// <see cref="UserProfileValue.Visibility"/>, and the per-portal default that the legacy
    /// constructor read is configuration supplied by an Application service.
    /// </para>
    /// </remarks>
    public bool IsVisible { get; set; }

    /// <summary>
    /// Gets the value that identifies this definition for the purposes of equality.
    /// </summary>
    /// <remarks>
    /// Forwards <see cref="PropertyDefinitionId"/>, keeping the legacy-named identity property as
    /// the single mapped column. Equality itself, and the rule that an identity counts only once
    /// the persistence layer has declared it persisted, belong to <see cref="Entity{TId}"/>.
    /// </remarks>
    public override int Identity => PropertyDefinitionId;

    /// <summary>
    /// Gets or sets the portal that owns this definition, or <see langword="null"/> when the
    /// definition is host-level or the reference has simply not been loaded.
    /// </summary>
    /// <remarks>
    /// Optional because <see cref="PortalId"/> is nullable: a host-level definition genuinely has no
    /// owning portal. A null here therefore does not by itself mean "not loaded", and the
    /// distinction has to come from <see cref="PortalId"/>.
    /// </remarks>
    public Portal? Portal { get; set; }

    /// <summary>
    /// Gets or sets the module definition that contributes this property, or
    /// <see langword="null"/> when the property is portal-wide or the reference has not been loaded.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="ModuleDefinition.ProfilePropertyDefinitions"/>. Optional, because
    /// <c>ModuleDefID</c> is nullable and no foreign key enforces it.
    /// </remarks>
    public ModuleDefinition? ModuleDefinition { get; set; }

    /// <summary>
    /// Gets the values that accounts have supplied for this property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inverse of <see cref="UserProfileValue.PropertyDefinition"/>, and the reason
    /// <see cref="IsDeleted"/> exists: these rows reference the definition, so withdrawing one is a
    /// flag rather than a delete.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy class flattened a single value onto the definition itself, as the
    /// <c>PropertyValue</c> string property at ProfilePropertyDefinition.vb line 246, because the
    /// collection loader hydrated one account's profile into the definition objects it returned.
    /// That conflated the definition with one account's answer to it and could not represent two
    /// accounts at once. The relationship replaces it, and no <c>PropertyValue</c> member appears on
    /// this entity.
    /// </para>
    /// <para>
    /// Get-only and initialised, so the collection is never null and no caller can swap the
    /// instance the change tracker is watching; add to and remove from it instead.
    /// </para>
    /// </remarks>
    public ICollection<UserProfileValue> ProfileValues { get; } = new List<UserProfileValue>();
}
