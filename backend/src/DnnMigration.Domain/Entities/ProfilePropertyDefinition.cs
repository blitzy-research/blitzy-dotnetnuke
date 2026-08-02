using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// The definition of one user-profile property available in a portal.
/// </summary>
/// <remarks>
/// MIGRATION: replaces <c>DotNetNuke.Entities.Profile.ProfilePropertyDefinition</c>. Bound to
/// <c>dbo.ProfilePropertyDefinition</c> (singular table name, created by 03.02.03). The unique index
/// over <c>(PortalID, ModuleDefID, PropertyName)</c> means a name is unique per portal per
/// contributing definition, and <c>ModuleDefID</c> is nullable so portal-wide properties belong to
/// no module definition. Removal is a soft delete through <see cref="Deleted"/>, because profile
/// values reference the definition.
/// </remarks>
public sealed class ProfilePropertyDefinition : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>PropertyDefinitionID</c>, identity from 1).</summary>
    public int PropertyDefinitionId { get; set; }

    /// <inheritdoc />
    public override int Identity => PropertyDefinitionId;

    /// <summary>Gets or sets the owning portal (<c>PortalID</c>, required, cascade delete).</summary>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the module definition that contributes this property (<c>ModuleDefID</c>, nullable).</summary>
    public int? ModuleDefinitionId { get; set; }

    /// <summary>Gets or sets the soft-delete flag (<c>Deleted</c>, required).</summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// Gets or sets the editor type (<c>DataType</c>, required), a list-entry identifier in the
    /// legacy data rather than an enumeration, so it is carried through as an integer.
    /// </summary>
    public int DataType { get; set; }

    /// <summary>Gets or sets the value applied when the user supplies none (<c>DefaultValue</c>, 50 characters).</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Gets or sets the grouping heading shown on the profile form (<c>PropertyCategory</c>, required, 50 characters).</summary>
    public string PropertyCategory { get; set; } = string.Empty;

    /// <summary>Gets or sets the property name (<c>PropertyName</c>, required, 50 characters).</summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum accepted length, 0 meaning unbounded (<c>Length</c>, required, default 0).</summary>
    public int Length { get; set; }

    /// <summary>Gets or sets whether a value must be supplied (<c>Required</c>, required).</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets the regular expression the value must match (<c>ValidationExpression</c>, 100 characters).</summary>
    public string? ValidationExpression { get; set; }

    /// <summary>Gets or sets the ordinal within its category (<c>ViewOrder</c>, required).</summary>
    public int ViewOrder { get; set; }

    /// <summary>Gets or sets whether the property is shown at all (<c>Visible</c>, required).</summary>
    public bool Visible { get; set; }

    /// <summary>Gets or sets the owning portal.</summary>
    public Portal? Portal { get; set; }

    /// <summary>Gets or sets the module definition that contributes this property.</summary>
    public ModuleDefinition? ModuleDefinition { get; set; }

    /// <summary>Gets the stored values of this property across users.</summary>
    public ICollection<UserProfileValue> UserProfileValues { get; } = new List<UserProfileValue>();
}
