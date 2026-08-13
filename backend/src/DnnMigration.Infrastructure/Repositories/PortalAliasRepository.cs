using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes the <c>dbo.PortalAlias</c> rows that bind a host name to a portal, through <see
/// cref="DnnDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The framework materialises every row read here. The reflection-based row hydrator the legacy controller
/// reached at its lines 41, 64 and 91, and the hand-written reader loops at its lines 45-60 and 68-83 that
/// filled an untyped list and a pre-generics keyed alias collection, are all replaced by it.
/// </para>
/// <para>
/// THE PHYSICAL SCHEMA IS DESCRIBED ELSEWHERE AND NEVER HERE. The table name, the column names, the key,
/// the cascading foreign key and the unique index all live in
/// <c>Persistence/Configurations/PortalAliasConfiguration.cs</c>.
/// </para>
/// </remarks>
internal sealed class PortalAliasRepository : IPortalAliasRepository
{
    /// <summary>The unit-of-work scoped context every query below is composed over.</summary>
    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="PortalAliasRepository"/> class.</summary>
    /// <param name="dbContext">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    public PortalAliasRepository(DnnDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<PortalAlias?> GetByIdAsync(int portalAliasId, CancellationToken cancellationToken = default)
    {
        // A zero-or-one lookup on the primary key, which is what legacy GetPortalAliasByPortalAliasID did
        // (02.02.02.SqlDataProvider lines 3874-3882). Absence is reported as null and never as a numeric
        // sentinel: no value of the argument is read as "any row" or "no row".
        return await _dbContext.PortalAliases
            .AsNoTracking()
            .FirstOrDefaultAsync(alias => alias.PortalAliasId == portalAliasId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PortalAlias?> GetByAliasAsync(
        string httpAlias,
        int portalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        // The host name is compared by EQUALITY on the whole stored value, and that equality is what
        // replaces the historical substring resolution.
        return await _dbContext.PortalAliases
            .AsNoTracking()
            .FirstOrDefaultAsync(
                alias => alias.PortalId == portalId && alias.HttpAlias == httpAlias,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> GetAllByHttpAliasAsync(
        IReadOnlyList<string> httpAliasCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAliasCandidates);

        // A blank candidate cannot name a configured host, so it is dropped; the remainder is de-duplicated
        // under the same case insensitivity the store compares with, because two candidates differing only
        // in case are one question to the database.
        List<string> wanted = httpAliasCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0)
        {
            // Nothing can match, so the store is not read at all - which the contract requires rather
            // than merely permits.
            return Array.Empty<PortalAlias>();
        }

        // EVERY match is returned, and that is the point.
        return await _dbContext.PortalAliases
            .AsNoTracking()
            .Where(alias => wanted.Any(candidate => candidate == alias.HttpAlias))
            .Include(alias => alias.Portal)
                .ThenInclude(portal => portal.Roles)
            .OrderBy(alias => alias.PortalAliasId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AliasExistsAsync(
        string httpAlias,
        int? excludingPortalAliasId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        // Deliberately NOT portal-scoped: IX_PortalAlias is installation-wide, so a host name already bound
        // to another tenant is still taken.
        if (excludingPortalAliasId is int excluded)
        {
            // The exclusion is what lets an edit keep its own host name without colliding with itself. Its
            // presence selects this branch and its magnitude means nothing beyond the row it names.
            return await _dbContext.PortalAliases
                .AsNoTracking()
                .AnyAsync(
                    alias => alias.HttpAlias == httpAlias && alias.PortalAliasId != excluded,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await _dbContext.PortalAliases
            .AsNoTracking()
            .AnyAsync(alias => alias.HttpAlias == httpAlias, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> GetByPortalIdAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        // The portal-scoped half of a split.
        return await _dbContext.PortalAliases
            .AsNoTracking()
            .Where(alias => alias.PortalId == portalId)
            .OrderBy(alias => alias.PortalAliasId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> GetByPortalIdsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalIds);

        if (portalIds.Count == 0)
        {
            // Asking for no portals is answered without a statement. It is a legitimate request: a listing
            // whose window landed past the end of the collection has no tenant to name.
            return Array.Empty<PortalAlias>();
        }

        int[] wanted = portalIds.Distinct().ToArray();

        // MIGRATION: net-new, and no wildcard is reachable through it. Every member of the set denotes the
        // portal bearing it, exactly as the single-portal read does, so -1 and 0 address those tenants
        // rather than widening the answer; "every alias in the installation" remains GetAllAsync.
        return await _dbContext.PortalAliases
            .AsNoTracking()
            .Where(alias => wanted.Contains(alias.PortalId))
            .OrderBy(alias => alias.PortalAliasId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.PortalAliases
            .AsNoTracking()
            .OrderBy(alias => alias.PortalAliasId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Portal?> GetPortalByAliasIdAsync(
        int portalAliasId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.PortalAliases
            .AsNoTracking()
            .Where(alias => alias.PortalAliasId == portalAliasId)
            .Select(alias => alias.Portal)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AddAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalAlias);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged, never written, and yielding no key. Legacy AddPortalAlias returned an integer only
        // because its procedure ended with select SCOPE_IDENTITY() (02.02.02.SqlDataProvider lines
        // 4080-4092), so the insert and the key retrieval were one indivisible statement.
        _dbContext.PortalAliases.Add(portalAlias);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalAlias);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged, never written: the unit of work owns the transaction boundary. A detached instance which
        // is what a caller holds after any read on this repository, since the reads are detached is
        // attached and marked modified.
        EntityEntry<PortalAlias> entry = _dbContext.Entry(portalAlias);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int portalAliasId, CancellationToken cancellationToken = default)
    {
        // The row is resolved before removal because the contract names its target by identifier, as legacy
        // DeletePortalAlias did (02.02.02.SqlDataProvider lines 4110-4116), so a caller holding only a key
        // need not read the row itself first.
        PortalAlias? alias = await _dbContext.PortalAliases
            .FirstOrDefaultAsync(candidate => candidate.PortalAliasId == portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        if (alias is null)
        {
            // An identifier no row bears completes without effect, which makes the removal idempotent.
            // Absence is not an error: a caller that must distinguish "removed" from "was never there"
            // reads the row first.
            return;
        }

        _dbContext.PortalAliases.Remove(alias);
    }
}
