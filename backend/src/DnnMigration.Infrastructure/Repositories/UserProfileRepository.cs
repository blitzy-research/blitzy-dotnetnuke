using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

// MIGRATION: the older SERIALIZED profile is not ported. The core provider's personalization block
// GetAllProfiles, GetProfile(UserId, PortalId), AddProfile(UserId, PortalId) and UpdateProfile(UserId,
// PortalId, ProfileData As String) - stored a whole profile as one opaque string keyed by user AND portal.

/// <summary>
/// Reads and writes the user-profile slice of the User aggregate over the legacy <c>dbo.UserProfile</c> and
/// <c>dbo.ProfilePropertyDefinition</c> tables.
/// </summary>
/// <remarks>
/// Every read applies <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/> and
/// materialises before returning, so a caller is handed values rather than a live query and the change
/// tracker carries nothing a read merely looked at.
/// </remarks>
internal sealed class UserProfileRepository : IUserProfileRepository
{
    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="UserProfileRepository"/> class.</summary>
    /// <param name="dbContext">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    public UserProfileRepository(DnnDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.UserProfileValues
            .AsNoTracking()
            .Include(value => value.PropertyDefinition)
            .Where(value => value.UserId == userId)
            .OrderBy(value => value.PropertyDefinitionId)
            .ThenBy(value => value.ProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Expressed as a subquery over the scoped declaration keys, so the two reads cannot drift apart again:
    /// there is one definition of what a scope's declarations are, and this read asks it rather than
    /// re-deriving it.
    /// </remarks>
    public async Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int? portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<int> scopedDefinitions = DefinitionsInPortalScope(portalId)
            .Select(definition => definition.PropertyDefinitionId);

        return await _dbContext.UserProfileValues
            .AsNoTracking()
            .Include(value => value.PropertyDefinition)
            .Where(value =>
                value.UserId == userId
                && scopedDefinitions.Contains(value.PropertyDefinitionId))
            .OrderBy(value => value.PropertyDefinitionId)
            .ThenBy(value => value.ProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The scope is resolved through the SAME <see cref="DefinitionsInPortalScope(int?)"/> subquery the
    /// single-account overload uses, so the two reads cannot drift apart about what a scope's declarations
    /// are - which is the property that keeps a batched read and a per-account read answering identically.
    /// </remarks>
    public async Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int? portalId,
        IReadOnlyCollection<int> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        if (userIds.Count == 0)
        {
            // Asking for no accounts is answered without a statement. It is a legitimate request: a listing
            // whose window landed past the end of the collection has no account to name.
            return Array.Empty<UserProfileValue>();
        }

        int[] wanted = userIds.Distinct().ToArray();

        IQueryable<int> scopedDefinitions = DefinitionsInPortalScope(portalId)
            .Select(definition => definition.PropertyDefinitionId);

        return await _dbContext.UserProfileValues
            .AsNoTracking()
            .Include(value => value.PropertyDefinition)
            .Where(value =>
                wanted.Contains(value.UserId)
                && scopedDefinitions.Contains(value.PropertyDefinitionId))
            .OrderBy(value => value.UserId)
            .ThenBy(value => value.PropertyDefinitionId)
            .ThenBy(value => value.ProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task AddProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileValue);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.UserProfileValues.Add(profileValue);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileValue);
        cancellationToken.ThrowIfCancellationRequested();

        StageModified(profileValue);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteProfileValuesAsync(
        int? portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        // Scoped through DefinitionsInPortalScope for the same reason the portal-scoped read is.
        IQueryable<int> scopedDefinitions = DefinitionsInPortalScope(portalId)
            .Select(definition => definition.PropertyDefinitionId);

        List<UserProfileValue> values = await _dbContext.UserProfileValues
            .Include(value => value.PropertyDefinition)
            .Where(value =>
                value.UserId == userId
                && scopedDefinitions.Contains(value.PropertyDefinitionId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _dbContext.UserProfileValues.RemoveRange(values);
    }

    /// <inheritdoc/>
    public Task AddDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.ProfilePropertyDefinitions.Add(definition);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        StageModified(definition);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> CountProfileValuesForDefinitionAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken = default) =>
        _dbContext.UserProfileValues
            .AsNoTracking()
            .CountAsync(
                value => value.PropertyDefinitionId == propertyDefinitionId,
                cancellationToken);

    /// <inheritdoc />
    public async Task DeleteDefinitionAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken = default)
    {
        ProfilePropertyDefinition? definition = await _dbContext.ProfilePropertyDefinitions
            .Include(candidate => candidate.ProfileValues)
            .FirstOrDefaultAsync(
                candidate => candidate.PropertyDefinitionId == propertyDefinitionId,
                cancellationToken)
            .ConfigureAwait(false);

        if (definition is null)
        {
            return;
        }

        _dbContext.ProfilePropertyDefinitions.Remove(definition);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The scope predicate is applied before the identifier one, and it says exactly what it means.
    /// </remarks>
    public async Task<ProfilePropertyDefinition?> GetDefinitionByIdAsync(
        int? portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default)
    {
        return await DefinitionsInPortalScope(portalId)
            .FirstOrDefaultAsync(
                definition => definition.PropertyDefinitionId == propertyDefinitionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The name comparison is exact, and stays the store's own comparison. The terminal procedure matched
    /// <c>PropertyName = @Name</c> (<c>04.03.03.SqlDataProvider</c>), so case-sensitivity was and remains
    /// the column collation's decision; forcing a case fold or a trim here would both diverge from that and
    /// defeat the unique index over <c>(PortalID, ModuleDefID, PropertyName)</c>.
    /// </remarks>
    public async Task<ProfilePropertyDefinition?> GetDefinitionByNameAsync(
        int? portalId,
        string propertyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        return await DefinitionsInPortalScope(portalId)
            .Where(definition => definition.PropertyName == propertyName)
            .OrderBy(definition => definition.ViewOrder)
            .ThenBy(definition => definition.PropertyDefinitionId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// MIGRATION: withdrawn declarations are excluded, reproducing the procedure's <c>AND Deleted = 0</c>
    /// (<c>04.03.03.SqlDataProvider</c>).
    /// </remarks>
    public async Task<IReadOnlyList<ProfilePropertyDefinition>> GetDefinitionsByPortalIdAsync(
        int? portalId,
        CancellationToken cancellationToken = default)
    {
        return await DefinitionsInPortalScope(portalId)
            .Where(definition => !definition.IsDeleted)
            .OrderBy(definition => definition.ViewOrder)
            .ThenBy(definition => definition.PropertyName)
            .ThenBy(definition => definition.PropertyDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Builds the untracked declaration query for one scope.</summary>
    /// <param name="portalId">
    /// The tenant the caller named, matched exactly, or <see langword="null"/> for the host scope.
    /// </param>
    /// <returns>A no-tracking query narrowed to the declarations that scope reaches.</returns>
    /// <remarks>
    /// This is the single place the portal scope of a declaration read is decided, and it reproduces the
    /// terminal procedures' own predicate, <c>(PortalId = @PortalId OR (PortalId IS NULL AND @PortalId IS
    /// NULL))</c> (<c>04.03.03.SqlDataProvider</c>), faithfully: a supplied identifier matches the column
    /// exactly, and a supplied <c>NULL</c> matches the rows whose column is <c>NULL</c>.
    /// </remarks>
    private IQueryable<ProfilePropertyDefinition> DefinitionsInPortalScope(int? portalId)
    {
        IQueryable<ProfilePropertyDefinition> definitions =
            _dbContext.ProfilePropertyDefinitions.AsNoTracking();

        return portalId is int tenantId
            ? definitions.Where(definition => definition.PortalId == tenantId)
            : definitions.Where(definition => definition.PortalId == null);
    }

    /// <summary>Stages a caller-supplied entity for update without traversing the graph hanging off it.</summary>
    /// <typeparam name="TEntity">The entity type being staged.</typeparam>
    /// <param name="entity">The entity whose stored row is to be rewritten.</param>
    private void StageModified<TEntity>(TEntity entity)
        where TEntity : class
    {
        var entry = _dbContext.Entry(entity);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }
    }
}
