using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

// MIGRATION: this repository spans the TWO legacy provider stacks its contract was assembled, and nothing
// else. The per-user answers come from the membership provider's "Profile" block -
// Library/Providers/MembershipProviders/DataProvider/DataProvider.vb, implemented at
// Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb - while the declarations those
// answers are keyed by come from the core provider's "profile property definitions" block at
// Library/Components/Providers/Data/DataProvider.vb, implemented at
// Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb.
//
// MIGRATION: the older SERIALIZED profile is not ported. The core provider's personalization block -
// GetAllProfiles, GetProfile(UserId, PortalId), AddProfile(UserId, PortalId) and UpdateProfile(UserId,
// PortalId, ProfileData As String) - stored a whole profile as one opaque string keyed by user AND portal.

/// <summary>
/// Reads and writes the user-profile slice of the User aggregate over the legacy
/// <c>dbo.UserProfile</c> and <c>dbo.ProfilePropertyDefinition</c> tables.
/// </summary>
/// <remarks>
/// <para>
/// The type is <see langword="internal"/> and sealed. Its provenance in the two legacy provider
/// stacks, and every legacy member deliberately left out, are set out in the migration notes above.
/// </para>
/// <para>
/// Every read applies <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/> and
/// materialises before returning, so a caller is handed values rather than a live query and the
/// change tracker carries nothing a read merely looked at. That is safe here - and it is worth
/// stating why, because the sibling repositories in this folder deliberately track their reads -
/// because BOTH write members below stage a detached instance explicitly.
/// </para>
/// </remarks>
internal sealed class UserProfileRepository : IUserProfileRepository
{
    // MIGRATION: THERE IS NO HOST SENTINEL CONSTANT HERE ANY LONGER, and its removal is the whole of a
    // measured defect fix. The legacy definition readers did not pass a portal identifier straight through:
    // GetPropertyDefinitionByName and GetPropertyDefinitionsByPortal both wrapped it as GetNull(portalId)
    // (SqlDataProvider.vb), GetNull is Null.GetNull(Field, DBNull.Value), and that helper turns an int equal
    // to -1 into DBNull.Value (Null.vb).
    //
    // That was wrong in this schema: dbo.Portals.PortalID is IDENTITY(-1, 1) (01.00.00.SqlDataProvider), so
    // -1 is simultaneously the FIRST REAL TENANT of an installation. The consequence was measured, not
    // theoretical - a real tenant numbered -1 could not read its own declarations or answers at all, and
    // every such request was served the host scope's rows instead: inaccessibility in one direction and
    // cross-scope exposure in the other.


    private readonly DnnDbContext _dbContext;

    /// <summary>
    /// Initialises a new instance of the <see cref="UserProfileRepository"/> class.
    /// </summary>
    /// <param name="dbContext">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dbContext"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>MIGRATION: the context is the whole dependency.</remarks>
    public UserProfileRepository(DnnDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    // Profile values (membership DataProvider.vb).

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>GetUserProfile(ByVal UserId As Integer) As IDataReader</c>
    /// (membership <c>DataProvider.vb</c>, executed at
    /// <c>MembershipProviders/DataProvider/SqlDataProvider.vb</c>). The filter is the account and
    /// only the account, exactly as the procedure's <c>WHERE UserId = @UserId</c>
    /// (<c>04.00.04.SqlDataProvider</c>) - narrowing by tenant here would narrow a result the
    /// legacy reader never narrowed, because this generation of the profile store is not
    /// portal-scoped.
    /// </para>
    /// <para>MIGRATION: both storage columns are returned as stored.</para>
    /// </remarks>
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
    /// <para>
    /// MIGRATION: THE PORTAL SCOPE IS THE ONE <see cref="DefinitionsInPortalScope(int?)"/> DEFINES,
    /// and delegating to it rather than restating it is the point. The scope itself is nullable
    /// because the column is: <see langword="null"/> addresses the host-level declarations that
    /// <c>03.03.03.SqlDataProvider</c> migrated onto a SQL <c>NULL</c> portal, and every non-null
    /// value - <c>-1</c> and <c>0</c> included - is an exact tenant key, because
    /// <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c> (<c>01.00.00.SqlDataProvider</c>).
    /// </para>
    /// <para>
    /// Expressed as a subquery over the scoped declaration keys, so the two reads cannot drift
    /// apart again: there is one definition of what a scope's declarations are, and this read asks
    /// it rather than re-deriving it. Deleted declarations are deliberately not filtered out here -
    /// an answer to a withdrawn property is still an answer, and every caller that cares about
    /// required properties consults the declaration list, which does filter them.
    /// </para>
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
    /// <para>
    /// The scope is resolved through the SAME <see cref="DefinitionsInPortalScope(int?)"/> subquery
    /// the single-account overload uses, so the two reads cannot drift apart about what a scope's
    /// declarations are - which is the property that keeps a batched read and a per-account read
    /// answering identically.
    /// </para>
    /// <para>
    /// The account key leads the ordering, so a caller grouping these rows sees each account's
    /// slice in the same definition-then-row-identity sequence the single-account overload
    /// produces. Loading each row's declaration matches that overload too, because an answer is
    /// only interpretable beside the declaration it answers.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: the INSERT arm of the single membership upsert (<c>DataProvider.vb</c>, procedure
    /// branch <c>04.00.04.SqlDataProvider</c>). Six positional arguments become one entity, and the
    /// arm is chosen by the caller calling this member rather than rediscovered by a natural-key
    /// probe on every write: this method stages the row it is given and never inspects
    /// <see cref="UserProfileValue.ProfileId"/> to decide anything.
    /// </para>
    /// <para>MIGRATION: staged, not written, and no identifier is returned.</para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: the UPDATE arm of that same upsert (procedure branch <c>04.00.04.SqlDataProvider</c>).
    /// </para>
    /// <para>
    /// MIGRATION: this is also how an answer is CLEARED.
    /// </para>
    /// </remarks>
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
        // MIGRATION: scoped through DefinitionsInPortalScope for the same reason the portal-scoped read is.
        // The scope is nullable because the column is - null is the host-level declarations that
        // 03.03.03.SqlDataProvider migrated onto a SQL NULL portal, and every non-null value is an exact
        // tenant key, -1 included, because dbo.Portals.PortalID is IDENTITY(-1, 1)
        // (01.00.00.SqlDataProvider).
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

    // SECTION B - PROFILE PROPERTY DEFINITIONS (core DataProvider.vb)

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>AddPropertyDefinition</c> (core <c>DataProvider.vb</c>, executed at
    /// <c>SqlDataProvider.vb</c>), whose ELEVEN positional arguments are all properties of
    /// <see cref="ProfilePropertyDefinition"/>.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy member was an insert-OR-update in disguise and this one is not. Here a
    /// create stages a create; the duplicate test the Application layer performs before calling
    /// this member is <see cref="GetDefinitionByNameAsync(int?, string, CancellationToken)"/>,
    /// whose answer it can act on.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>UpdatePropertyDefinition</c> (core <c>DataProvider.vb</c>, executed at
    /// <c>SqlDataProvider.vb</c>) and its TEN positional arguments, nine of which the eleven-argument insert
    /// also carried.
    /// </para>
    /// <para>
    /// MIGRATION: the required-implies-visible coupling the legacy update applied
    /// (<c>ProfileController.vb</c>) is NOT reproduced.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>DeletePropertyDefinition(ByVal definitionId As Integer)</c> (core
    /// <c>DataProvider.vb</c>, executed at <c>SqlDataProvider.vb</c>). The identifier alone was the
    /// whole legacy signature and it is the whole signature here: no flag chooses between a
    /// physical and a logical removal, and no companion member restores one, because the legacy
    /// surface offered neither.
    /// </para>
    /// <para>
    /// MIGRATION: the removal is PHYSICAL, matching that legacy member.
    /// <see cref="ProfilePropertyDefinition.IsDeleted"/> is a real column and the contract permits
    /// an implementation to honour this member by setting it, but the legacy call deleted the row
    /// and the Application layer above depends on that reading - it guards this call precisely
    /// because "removal here is physical and cascades the stored answers".
    /// </para>
    /// </remarks>
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
    /// <para>
    /// MIGRATION: the raw provider member accepted only the definition identifier, but its only controller
    /// wrapper accepted the portal too and first searched the portal-scoped catalogue
    /// (<c>ProfileController.vb</c>).
    /// </para>
    /// <para>
    /// MIGRATION: the scope predicate is applied before the identifier one, and it says exactly what it
    /// means.
    /// </para>
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
    /// <para>
    /// MIGRATION: replaces
    /// <c>GetPropertyDefinitionByName(ByVal portalId As Integer, ByVal name As String) As IDataReader</c>
    /// (core <c>DataProvider.vb</c>, executed at <c>SqlDataProvider.vb</c>).
    /// </para>
    /// <para>
    /// MIGRATION: the name comparison is exact, and stays the store's own comparison. The terminal
    /// procedure matched <c>PropertyName = @Name</c> (<c>04.03.03.SqlDataProvider</c>), so
    /// case-sensitivity was and remains the column collation's decision; forcing a case fold or a
    /// trim here would both diverge from that and defeat the unique index over
    /// <c>(PortalID, ModuleDefID, PropertyName)</c>.
    /// </para>
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
    /// <para>
    /// MIGRATION: replaces
    /// <c>GetPropertyDefinitionsByPortal(ByVal portalId As Integer) As IDataReader</c> (core
    /// <c>DataProvider.vb</c>, executed at <c>SqlDataProvider.vb</c>), whose reader was hydrated by
    /// reflection through <c>CBO</c> and handed back inside a 313-line <c>CollectionBase</c>
    /// subclass.
    /// </para>
    /// <para>
    /// MIGRATION: withdrawn declarations are excluded, reproducing the procedure's
    /// <c>AND Deleted = 0</c> (<c>04.03.03.SqlDataProvider</c>).
    /// </para>
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
    /// <para>
    /// MIGRATION: this is the single place the portal scope of a declaration read is decided, and
    /// it reproduces the terminal procedures' own predicate,
    /// <c>(PortalId = @PortalId OR (PortalId IS NULL AND @PortalId IS NULL))</c>
    /// (<c>04.03.03.SqlDataProvider</c>), faithfully: a supplied identifier matches the column
    /// exactly, and a supplied <c>NULL</c> matches the rows whose column is <c>NULL</c>.
    /// </para>
    /// <para>
    /// MIGRATION: -1 IS NOT THE HOST SCOPE. <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider</c>), so -1 is the first real tenant of an installation as well
    /// as the legacy absence marker.
    /// </para>
    /// </remarks>
    private IQueryable<ProfilePropertyDefinition> DefinitionsInPortalScope(int? portalId)
    {
        IQueryable<ProfilePropertyDefinition> definitions =
            _dbContext.ProfilePropertyDefinitions.AsNoTracking();

        return portalId is int tenantId
            ? definitions.Where(definition => definition.PortalId == tenantId)
            : definitions.Where(definition => definition.PortalId == null);
    }

    /// <summary>
    /// Stages a caller-supplied entity for update without traversing the graph hanging off it.
    /// </summary>
    /// <typeparam name="TEntity">The entity type being staged.</typeparam>
    /// <param name="entity">The entity whose stored row is to be rewritten.</param>
    /// <remarks>
    /// <para>
    /// An entity this repository is handed is normally DETACHED, because every read member here is
    /// untracked, so the update members cannot rely on the change tracker having noticed a
    /// mutation.
    /// </para>
    /// </remarks>
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
