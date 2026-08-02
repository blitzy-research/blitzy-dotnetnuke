using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One user's value for one profile property.
/// </summary>
/// <remarks>
/// MIGRATION: the legacy <c>UserProfile</c> class exposed 19 fixed properties
/// (Library/Components/Users/Profile/UserProfile.vb); the terminal schema stores them as rows.
/// Bound to <c>dbo.UserProfile</c> (03.02.03), which keeps short values in
/// <c>PropertyValue nvarchar(3750)</c> and long values in <c>PropertyText ntext</c>. Both columns
/// are preserved: collapsing them would change which column a round trip writes.
/// </remarks>
public sealed class UserProfileValue : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>ProfileID</c>, identity from 1).</summary>
    public int ProfileId { get; set; }

    /// <inheritdoc />
    public override int Identity => ProfileId;

    /// <summary>Gets or sets the owning user (<c>UserID</c>, required, cascade delete).</summary>
    public int UserId { get; set; }

    /// <summary>Gets or sets the property definition (<c>PropertyDefinitionID</c>, required, cascade delete).</summary>
    public int PropertyDefinitionId { get; set; }

    /// <summary>Gets or sets the stored value when it fits the bounded column (<c>PropertyValue</c>, 3750 characters).</summary>
    public string? PropertyValue { get; set; }

    /// <summary>Gets or sets the stored value when it exceeds the bounded column (<c>PropertyText</c>, ntext).</summary>
    public string? PropertyText { get; set; }

    /// <summary>
    /// Gets or sets who may see the value (<c>Visibility</c>, required, default 0), matching the
    /// legacy <c>UserVisibilityMode</c>: 0 all users, 1 members only, 2 administrators only.
    /// </summary>
    public int Visibility { get; set; }

    /// <summary>Gets or sets when the value was last written (<c>LastUpdatedDate</c>, required).</summary>
    public DateTime LastUpdatedDate { get; set; }

    /// <summary>Gets or sets the owning user.</summary>
    public User? User { get; set; }

    /// <summary>Gets or sets the property definition.</summary>
    public ProfilePropertyDefinition? PropertyDefinition { get; set; }

    /// <summary>
    /// Gets the effective value, preferring the long-text column when it holds content.
    /// </summary>
    /// <remarks>
    /// MIGRATION: reproduces the legacy read order, which took <c>PropertyText</c> when present and
    /// fell back to <c>PropertyValue</c>. The empty string - the legacy <c>Null.NullString</c>
    /// sentinel - is treated as content, not as absence, so a deliberately cleared value stays
    /// cleared.
    /// </remarks>
    public string? EffectiveValue => PropertyText ?? PropertyValue;
}
