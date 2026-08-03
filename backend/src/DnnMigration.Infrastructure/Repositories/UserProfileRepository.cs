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
/// <para>
/// MIGRATION: implements a contract deliberately assembled from BOTH legacy provider stacks - the
/// six profile-property-definition members of the core provider
/// (<c>Library/Components/Providers/Data/DataProvider.vb:L250-L256</c>) and the two profile-value
/// members of the membership provider
/// (<c>Library/Providers/MembershipProviders/DataProvider/DataProvider.vb:L117-L119</c>) - together
/// with the data-access half of <c>Library/Components/Users/Profile/ProfileController.vb</c>,
/// including its two reflection-hydrator call sites. The legacy <c>UserProfile</c> class exposed
/// nineteen fixed properties; here the answers are a key-value row set keyed by definition, which is
/// what the terminal <c>dbo.UserProfile</c> table has actually held since the 03.02.03 script.
/// </para>
/// <para>
/// Definition removal is a hard delete, and <c>FK_UserProfile_ProfilePropertyDefinition</c> is
/// declared <c>ON DELETE CASCADE</c> (<c>04.00.04.SqlDataProvider:L1429</c>), so the store removes
/// the answers with the definition. Both <see cref="GetDefinitionByIdAsync"/> and
/// <see cref="DeleteDefinitionAsync"/> nevertheless ensure
/// <see cref="ProfilePropertyDefinition.ProfileValues"/> is loaded, so that the cascade is also
/// performed by the change tracker as explicit statements. Without that, the delete would depend
/// entirely on a database-level constraint and would silently orphan rows on any provider that does
/// not enforce one - which is exactly the situation an integration run against a non-SQL-Server
/// provider creates.
/// </para>
/// <para>
/// MIGRATION: no member deletes an individual profile VALUE row, because no legacy member did. The
/// membership provider's profile block declares exactly two members - the reader at L118 and the
/// upsert at L119 - and a case-insensitive sweep of all eighty-eight upgrade scripts finds no
/// procedure that deletes a profile value and no <c>DELETE</c> statement against the
/// <c>UserProfile</c> table at all. Clearing an answer is an update carrying an empty value, which
/// is what <see cref="UpdateProfileValueAsync"/> stages.
/// </para>
/// <para>
/// Every write member STAGES its change and returns without saving, so the unit of work remains the
/// single commit boundary. No read member applies <c>AsNoTracking</c>, for the reason given on
/// <see cref="PortalRepository"/>.
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

    // =================================================================================
    // SECTION A - PROFILE VALUES  (membership DataProvider.vb:L117-L119)
    // =================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// The definition is loaded with each answer, because an answer is meaningless without the
    /// property it answers: the caller needs the name, the data type and the validation expression in
    /// order to present or validate it, and the stored value itself - whichever of
    /// <see cref="UserProfileValue.PropertyValue"/> and
    /// <see cref="UserProfileValue.PropertyText"/> holds it - is only interpretable alongside them.
    /// Ordering follows the same display order as <see cref="GetDefinitionsByPortalIdAsync"/>.
    /// <para>
    /// MIGRATION: the legacy reader took the account alone
    /// (<c>membership DataProvider.vb:L118 GetUserProfile(ByVal UserId As Integer)</c>) and no
    /// portal filter is applied here for that reason - narrowing by tenant would narrow a result the
    /// legacy reader never narrowed.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int userId,
        CancellationToken cancellationToken = default)
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
    /// <remarks>
    /// MIGRATION: the insert arm of the single legacy upsert at
    /// <c>membership DataProvider.vb:L119</c>, whose procedure branch is
    /// <c>UpdateUserProfileProperty</c> at <c>04.00.04.SqlDataProvider:L1634</c>. Staged rather than
    /// written: the legacy procedure could answer with <c>SELECT @ProfileID</c> because it had
    /// already inserted the row, whereas here
    /// <see cref="UserProfileValue.ProfileId"/> is assigned when the unit of work commits.
    /// </remarks>
    public Task AddProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileValue);
        cancellationToken.ThrowIfCancellationRequested();

        _context.UserProfileValues.Add(profileValue);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the update arm of that same upsert
    /// (<c>04.00.04.SqlDataProvider:L1622</c>). An answer read through this repository is already
    /// tracked, so its modifications are staged by the tracker and this call is the caller's explicit
    /// statement of intent. An untracked instance is attached and marked modified so the same call
    /// works for it too. This is also the member that CLEARS an answer, by carrying an empty value -
    /// the legacy behaviour, since no delete path for a value row ever existed.
    /// </remarks>
    public Task UpdateProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileValue);
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Entry(profileValue).State is EntityState.Detached)
        {
            _context.UserProfileValues.Update(profileValue);
        }

        return Task.CompletedTask;
    }

    // =================================================================================
    // SECTION B - PROFILE PROPERTY DEFINITIONS  (core DataProvider.vb:L250-L256)
    // =================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: replaces core <c>DataProvider.vb:L251 AddPropertyDefinition</c> and its eleven
    /// positional arguments, all of which are properties of the entity. Staged rather than written,
    /// so that provisioning a tenant's whole default declaration set commits atomically.
    /// </remarks>
    public Task AddDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        _context.ProfilePropertyDefinitions.Add(definition);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: replaces core <c>DataProvider.vb:L256 UpdatePropertyDefinition</c> and its ten
    /// positional arguments. A declaration read through this repository is already tracked; an
    /// untracked instance is attached and marked modified so the member is honest for a caller that
    /// rebuilt the entity elsewhere.
    /// </remarks>
    public Task UpdateDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Entry(definition).State is EntityState.Detached)
        {
            _context.ProfilePropertyDefinitions.Update(definition);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: replaces core <c>DataProvider.vb:L252 DeletePropertyDefinition(definitionId)</c>,
    /// which took the identifier alone - so the declaration is resolved before removal. When the
    /// caller already holds it, that resolves from the change tracker without a round trip.
    /// <para>
    /// Removal is a hard delete, matching the legacy procedure.
    /// <see cref="ProfilePropertyDefinition.IsDeleted"/> exists and the contract permits a
    /// flag-based reading, but the legacy member deleted the row and this implementation preserves
    /// that. The dependent answers are loaded first so the cascade is staged by the change tracker
    /// as well as enforced by <c>FK_UserProfile_ProfilePropertyDefinition</c>, which keeps the
    /// outcome identical on a provider that does not enforce the constraint itself.
    /// </para>
    /// <para>
    /// Absence is not an error: a caller that has already established absence need not distinguish
    /// the two cases.
    /// </para>
    /// </remarks>
    public async Task DeleteDefinitionAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken = default)
    {
        ProfilePropertyDefinition? definition = await _context.ProfilePropertyDefinitions
            .FirstOrDefaultAsync(d => d.PropertyDefinitionId == propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (definition is null)
        {
            return;
        }

        await EnsureAnswersLoadedAsync(definition, cancellationToken).ConfigureAwait(false);

        _context.ProfilePropertyDefinitions.Remove(definition);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: replaces core <c>DataProvider.vb:L253 GetPropertyDefinition(definitionId)</c>. The
    /// answers are loaded with the declaration so that removal cascades through the change tracker as
    /// well as through the store constraint - see the type remarks.
    /// </remarks>
    public async Task<ProfilePropertyDefinition?> GetDefinitionByIdAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ProfilePropertyDefinitions
            .Include(d => d.ProfileValues)
            .FirstOrDefaultAsync(d => d.PropertyDefinitionId == propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: replaces core <c>DataProvider.vb:L254 GetPropertyDefinitionByName(portalId, name)</c>,
    /// and like the legacy member it answers with the declaration rather than with a boolean, so a
    /// caller rejecting a duplicate name during an edit can tell "found" from "found, but it is the
    /// row I am editing".
    /// <para>
    /// <c>IX_ProfilePropertyDefinition</c> is unique over
    /// <c>(PortalID, ModuleDefID, PropertyName)</c>, so a name can legitimately repeat within a
    /// portal across different module declarations. This member deliberately reports the weaker
    /// portal-wide answer, because the profile screens the legacy application exposed presented one
    /// flat property list per portal and a repeated name there would be indistinguishable to an
    /// administrator. Retired declarations are included, so a soft-deleted declaration still reserves
    /// its name and restoring it cannot introduce a duplicate. The match is case-insensitive and
    /// ignores surrounding whitespace, as the legacy screens' comparison did.
    /// </para>
    /// </remarks>
    public async Task<ProfilePropertyDefinition?> GetDefinitionByNameAsync(
        int portalId,
        string propertyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        string wanted = propertyName.Trim().ToLowerInvariant();

        return await _context.ProfilePropertyDefinitions
            .Where(d => d.PortalId == portalId && d.PropertyName.ToLower() == wanted)
            .OrderBy(d => d.PropertyDefinitionId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: replaces core <c>DataProvider.vb:L255 GetPropertyDefinitionsByPortal(portalId)</c>,
    /// whose reader was hydrated by reflection through <c>CBO</c> into a
    /// <c>ProfilePropertyDefinitionCollection</c>; both are replaced by a materialised read-only list.
    /// <para>
    /// Declarations are returned in the display order the legacy profile editor used -
    /// <c>ViewOrder</c> first, then the property name - so that a caller renders a profile form
    /// without re-sorting. Declarations withdrawn through
    /// <see cref="ProfilePropertyDefinition.IsDeleted"/> are excluded, so a retired property is never
    /// presented for answering; a caller that needs to see one addresses it by key through
    /// <see cref="GetDefinitionByIdAsync"/> or by name through
    /// <see cref="GetDefinitionByNameAsync"/>, neither of which filters the flag.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ProfilePropertyDefinition>> GetDefinitionsByPortalIdAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ProfilePropertyDefinitions
            .Where(d => d.PortalId == portalId && !d.IsDeleted)
            .OrderBy(d => d.ViewOrder)
            .ThenBy(d => d.PropertyName)
            .ThenBy(d => d.PropertyDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures a declaration's recorded answers are tracked before it is removed.
    /// </summary>
    /// <param name="definition">The declaration about to be removed.</param>
    /// <param name="cancellationToken">Token observed while the collection is loaded.</param>
    /// <returns>A task that completes once the answers are tracked.</returns>
    /// <remarks>
    /// The schema cascades from a declaration to its answers, but a cascade the store performs is
    /// invisible to the change tracker, and a provider that does not enforce the constraint would
    /// leave the answers behind entirely. Loading them first makes the removal explicit on every
    /// provider. The collection is loaded only when it is not already present, so a declaration
    /// obtained through <see cref="GetDefinitionByIdAsync"/> costs no second round trip.
    /// </remarks>
    private async Task EnsureAnswersLoadedAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken)
    {
        var answers = _context.Entry(definition).Collection(d => d.ProfileValues);

        if (!answers.IsLoaded)
        {
            await answers.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
