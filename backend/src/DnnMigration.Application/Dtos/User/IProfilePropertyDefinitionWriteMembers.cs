namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// The nine members that <see cref="CreateProfilePropertyDefinitionRequest"/> and
/// <see cref="UpdateProfilePropertyDefinitionRequest"/> both carry, declared once so that the mapper writes
/// them at ONE site rather than at two sites that could drift apart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The two write contracts are separate types because the terminal procedures
/// do not honour the same member set: <c>AddPropertyDefinition</c> (<c>04.06.00:L1101</c>) declares
/// <c>@ModuleDefId</c> and <c>UpdatePropertyDefinition</c> (<c>04.05.00:L1685</c>) does not. Everything
/// else the two procedures write is identical, and a mapper that assigned those nine members twice would be
/// a mapper in which a future correction could be applied to one verb and forgotten on the other. The
/// alternative - a private helper taking nine positional parameters - is exactly the shape this migration
/// exists to remove, so the shared member set is named instead.
/// </para>
/// <para>
/// <b>This is not a wire contract and does not appear in one.</b> The interface is internal, so it is
/// invisible outside this assembly and changes nothing about either request's public surface, its OpenAPI
/// schema or its JSON shape. It is a mapping convenience, and it deliberately excludes the create-only
/// module-definition key so that the shared write site cannot reach a member only one verb honours.
/// </para>
/// <para>
/// <b>Nothing here is a validation contract either.</b> Presence, width and pattern rules are declared by
/// the two validators over the concrete request types, reading the shared constants on
/// <c>Validation/ProfileDefinitionTermsRules</c>. This interface constrains only what the mapper may read.
/// </para>
/// </remarks>
internal interface IProfilePropertyDefinitionWriteMembers
{
    /// <summary>Key of the <c>Lists</c> entry naming the property's data type.</summary>
    int DataType { get; }

    /// <summary>Value pre-filled for an account that has not supplied its own, or <c>null</c>.</summary>
    string? DefaultValue { get; }

    /// <summary>Heading the property is grouped under on the profile screens.</summary>
    string PropertyCategory { get; }

    /// <summary>Name the property is addressed by.</summary>
    string PropertyName { get; }

    /// <summary>Display width, where zero means unbounded.</summary>
    int Length { get; }

    /// <summary>Whether an account must supply a value.</summary>
    bool Required { get; }

    /// <summary>Regular expression a submitted value must match, or <c>null</c>.</summary>
    string? ValidationExpression { get; }

    /// <summary>Position the property occupies in its category.</summary>
    int ViewOrder { get; }

    /// <summary>Whether the property is shown at all.</summary>
    bool Visible { get; }
}
