using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes the <c>dbo.PortalAlias</c> rows that bind a host name to a portal, through
/// <see cref="DnnDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the alias block of the legacy abstract data provider
/// (<c>Library/Components/Providers/Data/DataProvider.vb</c> lines 355-362) together with the
/// data-access half of <c>Library/Components/Portal/PortalAliasController.vb</c>. Stored procedures
/// invoked as concatenated names through a reflection-created static singleton become composed queries
/// over the mapped model, resolved by dependency injection.
/// </para>
/// <para>
/// MIGRATION: the framework materialises every row read here. The reflection-based row hydrator the
/// legacy controller reached at its lines 41, 64 and 91, and the hand-written reader loops at its lines
/// 45-60 and 68-83 that filled an untyped list and a pre-generics keyed alias collection, are all
/// replaced by it. No fill method, no reader loop and no hand-rolled collection type is recreated, and
/// no sentinel is substituted for a null on the way out: nullability is carried by the CLR type.
/// </para>
/// <para>
/// MIGRATION: the install-only rewrite path is deliberately absent. Legacy <c>UpdatePortalAlias</c>
/// (DataProvider.vb line 359) executed <c>UpdatePortalAliasOnInstall</c>
/// (<c>SqlDataProvider.vb</c> lines 1312-1314), which reassigns the placeholder row whose host name is
/// <c>'_default'</c> (<c>02.02.02.SqlDataProvider</c> lines 4069-4077). The installer is out of scope,
/// so that member has no counterpart on this contract and none is invented here; the per-row update is
/// <see cref="UpdateAsync"/>.
/// </para>
/// <para>
/// THE PHYSICAL SCHEMA IS DESCRIBED ELSEWHERE AND NEVER HERE. The table name, the column names, the
/// key, the cascading foreign key and the unique index all live in
/// <c>Persistence/Configurations/PortalAliasConfiguration.cs</c>. This type composes queries over the
/// mapped model and declares no physical detail of its own, so nothing in this file can reshape the
/// existing database.
/// </para>
/// <para>
/// READS ARE DETACHED, WHICH IS A CORRECTNESS DECISION AS MUCH AS AN ECONOMY. Tenant resolution runs
/// <see cref="GetAllByHttpAliasAsync"/> on every request through the same scoped context an ensuing
/// write uses, so a tracked alias graph left behind by resolution would collide with the same row
/// attached later by <see cref="UpdateAsync"/>. The one read that serves a write - the lookup inside
/// <see cref="DeleteAsync"/> - is tracked deliberately, and <see cref="UpdateAsync"/> attaches a
/// detached instance, so the read-modify-write sequence the application layer performs is unaffected.
/// </para>
/// <para>
/// LIST READS ORDER BY THE KEY. No legacy alias procedure carried an <c>ORDER BY</c>, so there is no
/// legacy order to preserve; the key is unique, which makes ordering by it total rather than merely
/// stable, and it is the clustered index, so it costs no sort. A display order by host name is applied
/// by the Application layer's mapper where a projection wants one, rather than imposed on every caller
/// from here.
/// </para>
/// <para>
/// NOTHING HERE CACHES OR EVICTS. The legacy controller cleared the host cache on every write - its
/// lines 29, 35 and 95 - whereas cache reads and invalidation belong to the caching service the
/// application services use, so this type performs neither.
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
        // A zero-or-one lookup on the primary key, which is what legacy GetPortalAliasByPortalAliasID
        // did (02.02.02.SqlDataProvider lines 3874-3882). Absence is reported as null and never as a
        // numeric sentinel: no value of the argument is read as "any row" or "no row".
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

        // MIGRATION: the host name is compared by EQUALITY on the whole stored value, and that equality
        // is what replaces the historical substring resolution. The legacy GetPortalSettings procedure
        // read the tenant as "select @PortalID = min(PortalID) from Portals where PortalAlias like '%' +
        // @PortalAlias + '%'" (01.00.00.SqlDataProvider lines 4569-4582, dropped for good at
        // 02.02.00.SqlDataProvider line 267), under which one tenant's host name being a substring of
        // another's resolved a request to the WRONG tenant and min() silently chose among the matches.
        // No wildcard, prefix, suffix or fragment comparison appears anywhere in this file, so no
        // character in a supplied value can widen what matches.
        //
        // MIGRATION: the stored column is compared as it stands and is never wrapped in a case-folding
        // call. Case insensitivity is the column's own collation - the default on SQL Server, and
        // exactly what the legacy procedure's "HTTPAlias = @HTTPAlias" relied on
        // (02.02.02.SqlDataProvider lines 3846-3856) - so folding the column here would change nothing
        // except to forbid a seek on IX_PortalAlias, the index this lookup and per-request tenant
        // resolution both depend on. Normalising a submitted host name before it is stored belongs to
        // the Application layer, so the argument is not rewritten either.
        //
        // Portal-scoped by design: the legacy procedure filtered on the host name AND the portal, which
        // makes this "does this tenant answer on this host name" rather than a resolution of a host name
        // to a tenant. Both -1 and 0 are genuine portal keys, so neither widens the query.
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

        // A blank candidate cannot name a configured host, so it is dropped; the remainder is
        // de-duplicated under the same case insensitivity the store compares with, because two
        // candidates differing only in case are one question to the database. No candidate is trimmed or
        // otherwise rewritten: the caller matches the rows returned here against its own candidate
        // values, so a value altered on the way in would come back unrecognised.
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

        // EVERY match is returned, and that is the point. The legacy resolution collapsed multiple
        // matches with min(PortalID), so an installation holding two rows for one host name served one
        // tenant's content under another tenant's name; returning the matches lets the caller refuse an
        // ambiguous address instead of resolving it. The installation-wide unique index on the host name
        // bounds this at one row per candidate for a well-formed installation, so a second row for the
        // SAME candidate is the signal that the data is defective.
        //
        // Every candidate is asked in ONE query - equality against the parameter collection translates
        // to a server-side IN over the indexed column - which is what keeps a virtual-path alias to a
        // single round trip. A request for host/child/api is matched against the whole chain at once
        // rather than costing one query per path segment. A row holding no host name cannot be reached
        // this way, because SQL's three-valued logic never matches a null against a list.
        //
        // The owning portal is loaded with each match because the contract promises it, and its roles
        // with it because the request-scoped tenant snapshot needs the administrator and registered role
        // NAMES, which are not columns on the portal table - the legacy views produced them with
        // correlated sub-queries. Loading both here is what makes tenant resolution a single round trip.
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

        // Deliberately NOT portal-scoped: IX_PortalAlias is installation-wide, so a host name already
        // bound to another tenant is still taken. Matched by the same equality, on the same unwrapped
        // column, as the reads above - a uniqueness question that compared differently from the lookups
        // would report a host name free and then fail the insert on the index.
        if (excludingPortalAliasId is int excluded)
        {
            // The exclusion is what lets an edit keep its own host name without colliding with itself.
            // Its presence selects this branch and its magnitude means nothing beyond the row it names.
            // The two branches are written out rather than folded into one predicate carrying a null
            // test, so each keeps its own index seek and its own cached plan.
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
        // MIGRATION: the portal-scoped half of a split. Legacy GetPortalAliasByPortalID carried an
        // "all portals" wildcard inside its own predicate - "where (PortalID = @PortalID or @PortalID =
        // -1)" (02.02.02.SqlDataProvider lines 3861-3869) - while -1 is simultaneously a REAL portal
        // key, because Portals.PortalID is declared IDENTITY(-1, 1), and was the legacy null-integer
        // sentinel. The legacy predicate could not tell those apart. There is therefore no wildcard
        // branch here: every value, -1 and 0 included, denotes exactly the portal bearing it, and "every
        // alias regardless of portal" is the separate GetAllAsync member below.
        return await _dbContext.PortalAliases
            .AsNoTracking()
            .Where(alias => alias.PortalId == portalId)
            .OrderBy(alias => alias.PortalAliasId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalAlias>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // MIGRATION: the other half of that split, and the explicit form of what the legacy code
        // expressed by passing the wildcard: PortalAliasController.vb lines 86-88 called
        // GetPortalAliasByPortalID(-1) to mean "every alias". A distinct question is asked here by
        // calling a distinct member, so no caller supplies a magic identifier and no reader of a call
        // site has to know that one integer was special.
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
        // Ports GetPortalByPortalAliasID, whose procedure joined the portal to the alias and projected
        // the portal's own columns (02.02.02.SqlDataProvider lines 3831-3841; the terminal form reads the
        // portal view at 04.04.00.SqlDataProvider lines 290-299). Expressed as a projection along the
        // configured navigation, which keeps the join server-side, materialises only the portal, never
        // exposes the alias, and hands back a materialised entity rather than a query the caller could
        // extend. Read-only by construction: every write member on this contract addresses an alias.
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
        // 4080-4092), so the insert and the key retrieval were one indivisible statement. Returning a
        // key here would require a flush, which would take the commit decision away from the unit of
        // work and split the multi-table tenant creation - this table is one of the five it writes - into
        // independently durable statements. The generated key is on the entity once the unit of work has
        // saved, and must not be read before then.
        _dbContext.PortalAliases.Add(portalAlias);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalAlias);
        cancellationToken.ThrowIfCancellationRequested();

        // MIGRATION: this is legacy UpdatePortalAliasInfo (DataProvider.vb line 360), which despite its
        // name is the real per-row update - it set the host name of one identified row
        // (02.02.02.SqlDataProvider lines 4096-4106). Its three positional arguments are carried by the
        // entity instead, so a caller cannot transpose them.
        //
        // Staged, never written: the unit of work owns the transaction boundary. A detached instance -
        // which is what a caller holds after any read on this repository, since the reads are detached -
        // is attached and marked modified. An instance the context is already tracking needs no second
        // declaration and must not be re-attached, because attaching a second instance bearing a tracked
        // key throws; asking for the entry does not begin tracking, so the test itself is free of side
        // effects.
        //
        // MIGRATION: the attachment ASSIGNS THE STATE and must not call DbSet.Update. Update walks the
        // navigation graph and decides Added-versus-Modified for everything it reaches by asking whether
        // the key "is set", reading an int key of 0 as unset. An alias carries PortalAlias.Portal, and
        // GetAllByHttpAliasAsync loads it (with the portal's roles behind it), so a caller handing back a
        // detached alias whose portal is the installation's key-0 tenant would have had that TENANT
        // inserted as a duplicate - a far larger blow than the alias edit it asked for. Assigning
        // EntityState.Modified attaches this row alone and consults neither the key nor the graph.
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
        // The row is resolved before removal because the contract names its target by identifier, as
        // legacy DeletePortalAlias did (02.02.02.SqlDataProvider lines 4110-4116), so a caller holding
        // only a key need not read the row itself first. This is the one lookup in the file that is
        // deliberately TRACKED: it exists to stage a write, and identity resolution means that a row the
        // context is already tracking comes back as that same instance, so the removal applies to the
        // entity the caller may already hold.
        PortalAlias? alias = await _dbContext.PortalAliases
            .FirstOrDefaultAsync(candidate => candidate.PortalAliasId == portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        if (alias is null)
        {
            // An identifier no row bears completes without effect, which makes the removal idempotent.
            // Absence is not an error: a caller that must distinguish "removed" from "was never there"
            // reads the row first. Nothing is committed here either way.
            return;
        }

        _dbContext.PortalAliases.Remove(alias);
    }
}
