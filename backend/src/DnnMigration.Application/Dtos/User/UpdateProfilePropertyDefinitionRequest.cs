namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Request contract for <c>PUT /api/v1/profile-definitions/{propertyDefinitionId}</c>: the writable state
/// of an existing profile property declaration.
/// </summary>
/// <remarks>
/// <b>The module-definition reference is deliberately absent, and its absence is the difference from the
/// creation contract.</b> The terminal update procedure declares no such parameter and assigns no such
/// column, so a property cannot be moved between a module and its portal once it has been declared.
/// </remarks>
// No inheritance and no shared base type with the sibling creation contract, for the reason recorded on
// that contract: the one member that differs between the two verbs must not be the one a reader has to go
// looking for. Shared RULES are shared instead.
public sealed class UpdateProfilePropertyDefinitionRequest : IProfilePropertyDefinitionWriteMembers
{
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
    /// Replacement value pre-populated into the property when a profile is first presented, or <see
    /// langword="null"/> when the property has no default.
    /// </summary>
    /// <remarks>
    /// Terminal procedure parameter <c>@DefaultValue nvarchar(50)</c>, column <c>DefaultValue</c>,
    /// nullable.
    /// </remarks>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Replacement grouping heading under which the property is presented; the "Category" column of the
    /// legacy definition grid. Required.
    /// </summary>
    // Non-nullable and initialised to the empty string rather than to a null-forgiving default, because the
    // column is NOT NULL and an omitted category is a MISSING required field rather than a null one.
    public string PropertyCategory { get; set; } = string.Empty;

    /// <summary>
    /// Replacement name of the property, which is also the key by which a stored profile value is matched
    /// back to its definition. Required, and unique within the portal and module definition.
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Replacement maximum number of characters the property's value may hold, or the display width for the
    /// types that use one; zero when unconstrained.
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
    /// Terminal procedure parameter <c>@ValidationExpression nvarchar(100)</c>, column nullable - and again
    /// the COLUMN decides rather than the parameter: it was created as <c>nvarchar(100)</c> at
    /// <c>03.02.03.SqlDataProvider</c> L1074 and widened to <c>nvarchar(2000)</c> by
    /// <c>04.03.05.SqlDataProvider</c> L17, so 2000 is the terminal width the validator uses: a limit of
    /// 100 would refuse expressions an upgraded database already holds.
    /// </remarks>
    // MIGRATION: this value is itself a rule to be applied to profile input, not a rule applied to this
    // field. It is stored verbatim and is never compiled or executed by this layer.
    public string? ValidationExpression { get; set; }

    /// <summary>Replacement position of the property within its portal's profile.</summary>
    // -1 IS AN INSTRUCTION RATHER THAN AN ABSENCE MARKER on the sibling creation path, where the terminal
    // insert procedure branches on "IF @vieworder=-1" and substitutes the current maximum order plus one
    // (04.06.00.SqlDataProvider L1122-L1126).
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
