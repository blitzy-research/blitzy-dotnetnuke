using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes <see cref="ProfilePropertyDefinition"/> profile metadata and the
/// <see cref="UserProfileValue"/> rows that hold each account's answers.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the four profile procedures reachable from the legacy core data provider
/// together with the profile procedures under <c>Library/Providers/MembershipProviders/</c>, and the
/// data-access half of <c>Library/Components/Users/Profile/ProfileController.vb</c> including its two
/// reflection-hydrator call sites. The legacy <c>UserProfile</c> exposed nineteen fixed properties;
/// here the answers are a key-value row set keyed by definition, which is what the terminal
/// <c>dbo.UserProfile</c> table has actually held since the 03.02.03 script.
/// <para>
/// Definition removal is a hard delete, and <c>FK_UserProfile_ProfilePropertyDefinition</c> is
/// declared <c>ON DELETE CASCADE</c>, so the store removes the answers with the definition.
/// <see cref="GetDefinitionAsync"/> nevertheless loads
/// <see cref="ProfilePropertyDefinition.ProfileValues"/> so that the cascade is also performed by
/// the change tracker as explicit statements. Without that, the delete would depend entirely on a
/// database-level constraint and would silently orphan rows on any provider that does not enforce one
/// - which is exactly the situation an integration run against a non-SQL-Server provider creates.
/// </para>
/// <para>
/// No read member applies <c>AsNoTracking</c>, for the reason given on <see cref="PortalRepository"/>.
/// </para>
/// </remarks>
internal sealed class UserProfileRepository : IUserProfileRepository
{
    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="UserProfileRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public UserProfileRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Definitions are returned in the display order the legacy profile editor used -
    /// <c>ViewOrder</c> first, then the property name - so that a caller renders a profile form
    /// without re-sorting. <c>Deleted</c> is a soft-delete flag, so a retired definition is excluded
    /// unless the caller asks for it, which is what lets administration still show and restore one.
    /// </remarks>
    public async Task<IReadOnlyList<ProfilePropertyDefinition>> ListDefinitionsAsync(
        int portalId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ProfilePropertyDefinition> query = _context.ProfilePropertyDefinitions
            .Where(d => d.PortalId == portalId);

        if (!includeDeleted)
        {
            query = query.Where(d => !d.IsDeleted);
        }

        return await query
            .OrderBy(d => d.ViewOrder)
            .ThenBy(d => d.PropertyName)
            .ThenBy(d => d.PropertyDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ProfilePropertyDefinition?> GetDefinitionAsync(int propertyDefinitionId, CancellationToken cancellationToken = default)
    {
        // The answers are loaded with the definition so that removal cascades through the change
        // tracker as well as through the store constraint - see the type remarks.
        return _context.ProfilePropertyDefinitions
            .Include(d => d.ProfileValues)
            .FirstOrDefaultAsync(d => d.PropertyDefinitionId == propertyDefinitionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_ProfilePropertyDefinition</c> is unique over
    /// <c>(PortalID, ModuleDefID, PropertyName)</c>, so a name can legitimately repeat within a portal
    /// across different module definitions. This member deliberately reports the weaker portal-wide
    /// answer, because the profile screens the legacy application exposed presented one flat property
    /// list per portal and a repeated name there would be indistinguishable to an administrator.
    /// Retired definitions are included, so a soft-deleted definition still reserves its name and
    /// restoring it cannot introduce a duplicate.
    /// </remarks>
    public Task<bool> DefinitionNameExistsAsync(
        int portalId,
        string propertyName,
        int? excludingPropertyDefinitionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        string wanted = propertyName.Trim().ToLowerInvariant();

        IQueryable<ProfilePropertyDefinition> query = _context.ProfilePropertyDefinitions
            .Where(d => d.PortalId == portalId && d.PropertyName.ToLower() == wanted);

        if (excludingPropertyDefinitionId.HasValue)
        {
            int excluded = excludingPropertyDefinitionId.Value;
            query = query.Where(d => d.PropertyDefinitionId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void AddDefinition(ProfilePropertyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _context.ProfilePropertyDefinitions.Add(definition);
    }

    /// <inheritdoc />
    public void RemoveDefinition(ProfilePropertyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _context.ProfilePropertyDefinitions.Remove(definition);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The definition is loaded with each answer, because an answer is meaningless without the
    /// property it answers: the caller needs the name, the data type and the validation expression in
    /// order to present or validate it, and the stored value itself - whichever of
    /// <see cref="UserProfileValue.PropertyValue"/> and
    /// <see cref="UserProfileValue.PropertyText"/> holds it - is only interpretable alongside them.
    /// Ordering follows the same display order as <see cref="ListDefinitionsAsync"/>.
    /// </remarks>
    public async Task<IReadOnlyList<UserProfileValue>> ListValuesAsync(int userId, CancellationToken cancellationToken = default)
    {
        return await _context.UserProfileValues
            .Include(v => v.PropertyDefinition)
            .Where(v => v.UserId == userId)
            .OrderBy(v => v.PropertyDefinition!.ViewOrder)
            .ThenBy(v => v.PropertyDefinition!.PropertyName)
            .ThenBy(v => v.ProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<UserProfileValue?> GetValueAsync(int userId, int propertyDefinitionId, CancellationToken cancellationToken = default)
    {
        return _context.UserProfileValues
            .Include(v => v.PropertyDefinition)
            .FirstOrDefaultAsync(
                v => v.UserId == userId && v.PropertyDefinitionId == propertyDefinitionId,
                cancellationToken);
    }

    /// <inheritdoc />
    public void AddValue(UserProfileValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _context.UserProfileValues.Add(value);
    }

    /// <inheritdoc />
    public void RemoveValue(UserProfileValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _context.UserProfileValues.Remove(value);
    }
}
