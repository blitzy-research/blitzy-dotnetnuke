namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Request contract for <c>POST /api/v1/profile-definitions</c>: the ten values that declare a new profile
/// property for a portal to collect.
/// </summary>
/// <remarks>
/// <b>The member set is the terminal insert procedure's own, exactly.</b> The authority is the last form of
/// <c>AddPropertyDefinition</c> in the destructive eighty-eight-script chain, at
/// <c>Website/Providers/DataProviders/SqlDataProvider/04.06.00.SqlDataProvider</c> line 1101: it declares
/// eleven parameters - the portal, which arrives in the route here, plus exactly the ten members below -
/// and its <c>INSERT</c> column list names those ten alongside the portal and a literal zero for the
/// soft-delete flag.
/// </remarks>
public sealed class CreateProfilePropertyDefinitionRequest : IProfilePropertyDefinitionWriteMembers
{
    /// <summary>
    /// Identifier of the module definition that contributes this property, or <see langword="null"/> when
    /// the property belongs to the portal rather than to a module.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@ModuleDefId int</c> (<c>04.06.00.SqlDataProvider</c> L1103).
    /// </remarks>
    public int? ModuleDefId { get; set; }

    /// <summary>
    /// Identifier of the data type that governs how the property's value is edited, validated and
    /// displayed.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@DataType int</c>, column <c>DataType int NOT NULL</c>. It is not an
    /// enumeration: it is a foreign key into the shared <c>Lists</c> lookup table, restricted to the
    /// entries whose list name is <c>DataType</c>.
    /// </remarks>
    // The Lists subsystem is outside this migration's scope, so there is no lookup service to resolve this
    // key against and no enumeration may be invented to stand in for one - inventing a numeric range would
    // refuse whichever identifiers a given installation's list happens to carry.
    public int DataType { get; set; }

    /// <summary>
    /// Value pre-populated into the property when a profile is first presented, or <see langword="null"/>
    /// when the property has no default.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@DefaultValue ntext</c>, column <c>DefaultValue</c>, nullable.
    /// Created as <c>nvarchar(50)</c> at <c>03.02.03.SqlDataProvider</c> L1069 and widened to <c>ntext</c>
    /// by <c>04.05.00.SqlDataProvider</c> L1593, so the terminal column is effectively unbounded and
    /// carries no length rule: a limit of 50 would refuse values an upgraded database already holds.
    /// </remarks>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Grouping heading under which the property is presented; the "Category" column of the legacy
    /// definition grid. Required.
    /// </summary>
    // Non-nullable and initialised to the empty string rather than to a null-forgiving default, because the
    // column is NOT NULL and an omitted category is a MISSING required field rather than a null one.
    public string PropertyCategory { get; set; } = string.Empty;

    /// <summary>
    /// Stable, machine-readable name of the property, and the key by which a stored profile value is
    /// matched back to its definition. Required, and unique within the portal and module definition.
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of characters the property's value may hold, or the display width for the types that
    /// use one; zero when unconstrained.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@Length int</c>, column <c>Length int NOT NULL</c> with a store
    /// default of zero.
    /// </remarks>
    // Zero legitimately means "no explicit length" and is not an unset value, so this member must never be
    // omitted from a payload on the grounds of holding its default.
    public int Length { get; set; }

    /// <summary>Whether a profile may not be saved while this property is empty.</summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@Required bit</c>, column <c>Required bit NOT NULL</c>, rendered as
    /// an inline check box in the legacy definition grid.
    /// </remarks>
    // The member describes a rule the validation layer applies to a PROFILE SUBMISSION; it is data on this
    // contract and imposes nothing on the contract itself. A boolean is its own constraint, so it carries
    // no rule.
    public bool Required { get; set; }

    /// <summary>
    /// Regular expression a submitted value has to satisfy, or <see langword="null"/> when the property is
    /// unconstrained beyond its data type.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@ValidationExpression nvarchar(2000)</c>, column nullable. Created
    /// as <c>nvarchar(100)</c> at <c>03.02.03.SqlDataProvider</c> L1074 and widened to
    /// <c>nvarchar(2000)</c> by <c>04.03.05.SqlDataProvider</c> L17, so 2000 is the terminal width the
    /// validator uses: a limit of 100 would refuse expressions an upgraded database already holds.
    /// </remarks>
    // MIGRATION: this value is itself a rule to be applied to profile input, not a rule applied to this
    // field. It is stored verbatim and is never compiled or executed by this layer.
    public string? ValidationExpression { get; set; }

    /// <summary>
    /// Position of the property within its portal's profile, or -1 to append it after the last existing
    /// property.
    /// </summary>
    // -1 IS AN INSTRUCTION HERE, NOT AN ABSENCE MARKER, and no lower bound may be placed on this member.
    // The terminal insert procedure branches on "IF @vieworder=-1" and substitutes the current maximum
    // order plus one (04.06.00.SqlDataProvider L1122-L1126), so -1 means "append to the end".
    public int ViewOrder { get; set; }

    /// <summary>Whether the property is presented at all.</summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@Visible bit</c>, column <c>Visible bit NOT NULL</c>, rendered as an
    /// inline check box in the legacy definition grid. Distinct from the response projection's
    /// default-visibility member, which answers a different question: not whether the field is shown, but
    /// to whom a stored value is disclosed.
    /// </remarks>
    public bool Visible { get; set; }
}
