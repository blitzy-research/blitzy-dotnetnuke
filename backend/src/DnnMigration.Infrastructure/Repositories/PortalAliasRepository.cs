using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes the <see cref="PortalAlias"/> host names that resolve a request to a tenant.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the alias slice of the legacy core data provider and the data-access half of
/// <c>Library/Components/Portal/PortalAliasController.vb</c>, including the three reflection-hydrator
/// call sites that file used.
/// <para>
/// <see cref="GetByAliasAsync"/> matches an alias exactly. The legacy tenant-resolution procedure
/// matched with
/// <c>select @PortalID = min(PortalID) from Portals where PortalAlias like '%' + @PortalAlias + '%'</c>
/// - first in that form at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 4569 - which
/// resolves an alias that is merely a substring of another tenant's alias to the wrong tenant, and
/// then silently picks the lowest identifier among the matches. Carrying a cross-tenant
/// mis-resolution into new code was not acceptable, so the match is exact here and the change is
/// recorded as a deliberate behavioural difference in <c>MIGRATION_NOTES.md</c>.
/// </para>
/// </remarks>
internal sealed class PortalAliasRepository : IPortalAliasRepository
{
    /// <summary>
    /// How many matching aliases are read before the ambiguity question is answered.
    /// </summary>
    /// <remarks>
    /// Two is sufficient and no more is useful: one row means resolved, two means ambiguous, and a
    /// third would not change either answer while making every request pay for it.
    /// </remarks>
    private const int CandidateProbeLimit = 2;

    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="PortalAliasRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public PortalAliasRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> ListAsync(int? portalId, CancellationToken cancellationToken = default)
    {
        IQueryable<PortalAlias> query = _context.PortalAliases;

        if (portalId.HasValue)
        {
            // PortalID is IDENTITY(-1, 1): -1 and 0 are real tenants, so the presence of a value is
            // what selects the filter, never its magnitude.
            int wanted = portalId.Value;
            query = query.Where(a => a.PortalId == wanted);
        }

        return await query
            .OrderBy(a => a.HttpAlias)
            .ThenBy(a => a.PortalAliasId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PortalAlias?> GetAsync(int portalAliasId, CancellationToken cancellationToken = default)
    {
        return _context.PortalAliases
            .FirstOrDefaultAsync(a => a.PortalAliasId == portalAliasId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PortalAlias?> GetByAliasAsync(string httpAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        string wanted = httpAlias.Trim().ToLowerInvariant();

        // Host names are case-insensitive, so the comparison is lower-cased on both sides rather
        // than left to the installation's collation. The match is exact - see the type remarks.
        //
        // The null test is required rather than defensive: dbo.PortalAlias.HTTPAlias is declared
        // without a NOT NULL clause at
        // Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider line 3807, so the
        // domain property is nullable and is left that way here. It translates to IS NOT NULL, which
        // is what SQL already does with a null on either side of an equality, so no row that used to
        // match stops matching - and a row holding no host name cannot be reached by a blank one.
        return _context.PortalAliases
            .FirstOrDefaultAsync(a => a.HttpAlias != null && a.HttpAlias.ToLower() == wanted, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> AliasExistsAsync(string httpAlias, int? excludingPortalAliasId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        string wanted = httpAlias.Trim().ToLowerInvariant();

        // Null-tested for the same reason as the lookup above: the column permits null, so a row
        // holding no host name is not a candidate for any host name.
        IQueryable<PortalAlias> query = _context.PortalAliases
            .Where(a => a.HttpAlias != null && a.HttpAlias.ToLower() == wanted);

        if (excludingPortalAliasId.HasValue)
        {
            // Excluding the row being edited is what lets an update keep its own alias without
            // reporting a collision against itself.
            int excluded = excludingPortalAliasId.Value;
            query = query.Where(a => a.PortalAliasId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(PortalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        _context.PortalAliases.Add(alias);
    }

    /// <inheritdoc />
    public void Remove(PortalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        _context.PortalAliases.Remove(alias);
    }


    /// <inheritdoc />
    public async Task<Result<PortalAlias>> ResolveByHttpAliasAsync(
        string httpAlias,
        CancellationToken cancellationToken)
    {
        // A blank host name cannot match a configured alias, and asking the database to prove it would
        // be a round trip spent on a value already known to be unusable. Refused as not-found rather
        // than as an argument fault: the value arrives from the network on every request, so an
        // unusable one is an ordinary refusal and not a programming error.
        if (string.IsNullOrWhiteSpace(httpAlias))
        {
            return Result<PortalAlias>.Failure(
                IPortalAliasRepository.NotFoundReasonCode,
                "The request did not carry a host name that identifies a portal.");
        }

        // Plain equality on the whole stored value. The parameter is bound as data, so no character
        // within it - a wildcard included - can broaden what matches.
        List<PortalAlias> candidates = await _context.PortalAliases
            .AsNoTracking()
            .Where(alias => alias.HttpAlias == httpAlias)
            .Include(alias => alias.Portal!)
                // The portal's administrator and registered role NAMES are needed by the request-scoped
                // tenant context, and they are not columns on Portals: the legacy views produced them
                // with correlated sub-queries over Roles, whose terminal form is at
                // 04.05.00.SqlDataProvider lines 1584 and 1585. Loading the portal's roles here keeps
                // resolution to one round trip, which is what the contract promises its consumer.
                .ThenInclude(portal => portal.Roles)
            .OrderBy(alias => alias.PortalAliasId)
            .Take(CandidateProbeLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Decided from the count, so there is no branch in which a candidate is selected. The ordering
        // above exists only to make the two probed rows deterministic for diagnostics; it is never used
        // to prefer one of them, and the ambiguous branch below discards both.
        return candidates.Count switch
        {
            0 => Result<PortalAlias>.Failure(
                IPortalAliasRepository.NotFoundReasonCode,
                "The host name in the request does not identify a configured portal."),

            1 => Result<PortalAlias>.Success(candidates[0]),

            // Two or more. Refused, not resolved: an installation in this state has a data defect an
            // operator must correct, and serving either candidate would cross a tenant boundary.
            _ => Result<PortalAlias>.Failure(
                IPortalAliasRepository.AmbiguousReasonCode,
                "The host name in the request identifies more than one configured portal."),
        };
    }
}
