using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes profile property definitions and the per-user values recorded against them.
/// </summary>
/// <remarks>
/// MIGRATION: the legacy <c>UserProfile</c> class exposed nineteen fixed properties; the underlying
/// <c>UserProfile</c> table is a key-value store keyed by <c>PropertyDefinitionID</c>. This contract
/// follows the table rather than the class, so a portal can define its own properties as the legacy
/// administration screens allowed.
/// </remarks>
public interface IUserProfileRepository
{
    /// <summary>Returns a portal's profile property definitions, in view order.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="includeDeleted">Whether to include definitions flagged as deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ProfilePropertyDefinition>> ListDefinitionsAsync(
        int portalId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one profile property definition by key, or <see langword="null"/>.</summary>
    /// <param name="propertyDefinitionId">Property definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProfilePropertyDefinition?> GetDefinitionAsync(int propertyDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a property name is already defined within a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="propertyName">The property name to test.</param>
    /// <param name="excludingPropertyDefinitionId">A definition to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> DefinitionNameExistsAsync(
        int portalId,
        string propertyName,
        int? excludingPropertyDefinitionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a new profile property definition for insertion.</summary>
    /// <param name="definition">The definition to insert.</param>
    void AddDefinition(ProfilePropertyDefinition definition);

    /// <summary>Stages a profile property definition for deletion.</summary>
    /// <param name="definition">The definition to delete.</param>
    void RemoveDefinition(ProfilePropertyDefinition definition);

    /// <summary>Returns every profile value recorded for a user.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<UserProfileValue>> ListValuesAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Returns one profile value, or <see langword="null"/> when the user has not supplied it.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="propertyDefinitionId">Property definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserProfileValue?> GetValueAsync(int userId, int propertyDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new profile value for insertion.</summary>
    /// <param name="value">The value to insert.</param>
    void AddValue(UserProfileValue value);

    /// <summary>Stages a profile value for deletion.</summary>
    /// <param name="value">The value to delete.</param>
    void RemoveValue(UserProfileValue value);
}
