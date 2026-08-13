namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// The nine members that <see cref="CreateProfilePropertyDefinitionRequest"/> and <see
/// cref="UpdateProfilePropertyDefinitionRequest"/> both carry, declared once so that the mapper writes them
/// at ONE site rather than at two sites that could drift apart.
/// </summary>
/// <remarks>
/// <b>This is not a wire contract and does not appear in one.</b> The interface is internal, so it is
/// invisible outside this assembly and changes nothing about either request's public surface, its OpenAPI
/// schema or its JSON shape. It is a mapping convenience, and it deliberately excludes the create-only
/// module-definition key so that the shared write site cannot reach a member only one verb honours.
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
