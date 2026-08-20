namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Transport contract for a single profile-property definition: the portal-scoped metadata that declares
/// which fields a user profile is composed of, how each field is typed and constrained, and in what order
/// it is presented.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the VB.NET class <c>DotNetNuke.Entities.Profile.ProfilePropertyDefinition</c> in
/// <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb</c>, which declares fifteen public
/// properties. Thirteen of them survive onto this contract; the two that do not are listed in the
/// divergence register below.
/// </para>
/// <para>
/// Divergences from the legacy shape are annotated inline at the point each applies, and each is recorded
/// in the repository migration notes.
/// </para>
/// </remarks>
public sealed class ProfilePropertyDefinitionDto
{
    // MIGRATION: three members of the legacy shape are deliberately absent from this contract.

    /// <summary>Surrogate key of the definition.</summary>
    public int PropertyDefinitionId { get; set; }

    /// <summary>Identifier of the portal that owns this definition.</summary>
    /// <remarks>
    /// Maps to <c>PortalID</c>, constrained by a foreign key to <c>Portals(PortalID)</c> with cascade
    /// delete, and part of the unique index over portal, module definition and property name.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Identifier of the module definition that contributed this property, or <c>null</c> when the property
    /// is not owned by a module.
    /// </summary>
    /// <remarks>
    /// Maps to <c>ModuleDefID int NULL</c>, and part of the unique index over portal, module definition and
    /// property name. <c>ModuleDefinitions.ModuleDefID</c> is declared <c>IDENTITY(1, 1)</c>, so 1 is the
    /// lowest real identifier and no legitimate value can be confused with the legacy -1 marker.
    /// </remarks>
    public int? ModuleDefId { get; set; }

    /// <summary>
    /// Identifier of the data type that governs how the property's value is edited, validated and
    /// displayed.
    /// </summary>
    /// <remarks>
    /// Maps to <c>DataType int NOT NULL</c>. It is not an enumeration.
    /// </remarks>
    // The Lists subsystem lies outside the scope of this migration, so there is no lookup service to
    // resolve this key against and no enumeration may be invented to stand in for one. The key travels as
    // the opaque int the schema declares.
    public int DataType { get; set; }

    /// <summary>
    /// Value pre-populated into the property when a profile is first presented, or <c>null</c> when the
    /// property has no default.
    /// </summary>
    /// <remarks>
    /// Maps to <c>DefaultValue</c>, nullable. The column is created as <c>nvarchar(50)</c> and later
    /// widened to <c>ntext</c> by <c>04.05.00.SqlDataProvider</c>, so the terminal column is effectively
    /// unbounded and a maximum-length rule of 50 would reject values that an upgraded database already
    /// holds.
    /// </remarks>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Grouping heading under which the property is presented; the "Category" column of the legacy
    /// definition grid.
    /// </summary>
    public string PropertyCategory { get; set; } = string.Empty;

    /// <summary>
    /// Stable, machine-readable name of the property; the "Name" column of the legacy definition grid, and
    /// the key by which a stored profile value is matched back to its definition.
    /// </summary>
    /// <remarks>
    /// Maps to <c>PropertyName nvarchar(50) NOT NULL</c>, and part of the unique index over portal, module
    /// definition and property name. Initialised to <c>string.Empty</c> for the same reason as the
    /// category.
    /// </remarks>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of characters the property's value may hold, or the display width for the types that
    /// use one; zero when unconstrained.
    /// </summary>
    /// <remarks>
    /// Maps to <c>Length int NOT NULL</c>, which carries a database default of 0. Zero is a legitimate
    /// stored value meaning "no explicit length" and not an unset one, so this member must never be omitted
    /// from a serialised payload on the grounds of holding its default.
    /// </remarks>
    public int Length { get; set; }

    /// <summary>Whether a profile may not be saved while this property is empty.</summary>
    /// <remarks>
    /// Maps to <c>Required bit NOT NULL</c>, and rendered as an inline check box in the legacy definition
    /// grid. The flag describes a rule the validation layer applies to a profile submission; it is data on
    /// this contract and imposes nothing on the contract itself.
    /// </remarks>
    public bool Required { get; set; }

    /// <summary>
    /// Regular expression a submitted value has to satisfy, or <c>null</c> when the property is
    /// unconstrained beyond its data type.
    /// </summary>
    /// <remarks>
    /// Maps to <c>ValidationExpression</c>, nullable. The column is created as <c>nvarchar(100)</c> and
    /// later widened to <c>nvarchar(2000)</c> by <c>04.03.05.SqlDataProvider</c>, so 2000 is the length the
    /// validation layer has to use: a limit of 100 would reject expressions that an upgraded database
    /// already holds.
    /// </remarks>
    public string? ValidationExpression { get; set; }

    /// <summary>
    /// Position of the property within its portal's profile, driving both the display order and the reorder
    /// controls of the legacy definition grid.
    /// </summary>
    // -1 is not an absence marker on this member, it is an instruction. The terminal upsert procedure
    // branches on "IF @vieworder = -1" and substitutes the current maximum order plus one, so -1 means
    // "append to the end".
    public int ViewOrder { get; set; }

    /// <summary>Whether the property is presented at all.</summary>
    /// <remarks>
    /// Maps to <c>Visible bit NOT NULL</c>, and rendered as an inline check box in the legacy definition
    /// grid. Distinct from <c>Visibility</c>, which answers a different question: not whether the field is
    /// shown, but to whom a stored value is disclosed.
    /// </remarks>
    public bool Visible { get; set; }

    /// <summary>
    /// Default audience permitted to see a stored value of this property: 0 for all users, 1 for
    /// authenticated members only, 2 for administrators only.
    /// </summary>
    // MIGRATION: two separate divergences meet on this member.
    public int Visibility { get; set; }
}
