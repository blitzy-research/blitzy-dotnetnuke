using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

// MIGRATION: this type replaces the legacy module-definition provider block
//   (Library/Components/Providers/Data/DataProvider.vb:L156-L183), the three static controllers that
//   wrapped it (DesktopModuleController.vb, ModuleDefinitionController.vb, ModuleControlController.vb)
//   and the stored-procedure layer beneath them, every call of which reached the source-less binary
//   Microsoft.ApplicationBlocks.Data.dll through SqlHelper with the procedure name built by string
//   concatenation. Table and column binding belongs to the four configurations under
//   Persistence/Configurations, so this file names no table, no column and no procedure, and issues no
//   SQL of its own.
//
// MIGRATION: long positional argument lists and SCOPE_IDENTITY returns collapse to entity staging. The
//   four legacy write procedures took nine to thirteen positional arguments and each returned the
//   generated key. Here every write takes the entity and stages the change; nothing is executed and no
//   key is returned, because the store assigns the identity during the flush. A member that returned one
//   would have to flush on the caller's behalf and would split a package, its definitions, its controls
//   and its per-portal grants into one transaction per row. Read the entity's own identity property
//   after IUnitOfWork.SaveChangesAsync has completed.
//
// MIGRATION: both legacy row-hydration paths disappear rather than being ported. The controllers
//   hydrated through the reflection-driven CBO helper into untyped ArrayLists, and
//   ModuleControlController.vb carried a second hand-written path with ten sentinel-translating
//   assignments. The materialiser performs both jobs, so every multi-row read here returns
//   IReadOnlyList<T> and every single-row read a nullable entity. No Fill member, no collection
//   wrapper, no data reader and no sentinel translation appears below.
//
// MIGRATION: the cache invalidation the legacy controllers performed inline, and the clearCache
//   overloads that guarded it, are deliberately absent. Cache invalidation is a coordinated concern
//   owned by ICacheService and the Application services that orchestrate a write; a repository that
//   cleared a cache would invalidate on staging rather than on commit, and a clearCache argument would
//   make a caller answerable for the correctness of a subsystem it cannot see. No member below accepts a
//   cache hint or touches a cache.
//
// MIGRATION: the obsolete friendly-name package lookup is not implemented. The legacy source had
//   already marked it Obsolete and nominated its own replacement, and the schema agrees:
//   03.01.00.SqlDataProvider:L34 dropped the unique constraint that once backed
//   DesktopModules.FriendlyName and L38 replaced it with a plain index, so a package friendly name is
//   not a key. GetDesktopModuleByModuleNameAsync is the only name-based package lookup, over the one
//   uniqueness the terminal schema does impose (IX_DesktopModules_ModuleName).
//
// MIGRATION: DesktopModule.SupportedFeatures and ModuleControl.ControlType travel as the raw persisted
//   integers they are, and nothing here reads, masks, validates or reinterprets either. Persisted
//   ordinals are not a zero-based range - 04.06.00.SqlDataProvider inserts -1 and 3 - and the terminal
//   query filters on a negative one, which is the single place below where the ordinal is compared at
//   all; see GetModuleControlsByKeyAsync. Promoting either column to an enumeration here would re-couple
//   this layer to an excluded tree and put a named-member contract in front of values the database is
//   free to hold. Likewise DesktopModule.BusinessControllerClass and ModuleControl.ControlSrc are inert
//   mapped text: this file resolves, loads, probes for and activates nothing they name. Runtime
//   activation belongs to IModuleBusinessControllerFactory, which draws from a closed, injected set.
//
// MIGRATION: the legacy sentinel-to-database-null conversion is not reproduced, so no identifier or
//   lookup key below carries a second meaning. Null.vb:L38-L45 sets the integer sentinel to -1 and the
//   string sentinel to the empty string, and GetNull turned a sentinel argument into SQL NULL on the way
//   to the store - which the terminal procedures then read two incompatible ways. GetPortalDesktopModules
//   and GetModuleDefinitions treated it as a match-all wildcard; GetModuleControlsByKey instead matched
//   the rows whose own column is null, which is how a definition's default control was found. Neither
//   reading survives: every identifier here is applied exactly, no value is treated as absent, and no
//   predicate compares a column with null. That is also why the string parameters are guarded rather
//   than trusted - a null argument would translate to "column IS NULL" and silently resurrect the
//   null-row reading the contract excludes. ModuleControl.ModuleDefinitionId stays nullable in the
//   entity, as the terminal column is, so the host-level controls belonging to no definition remain
//   representable and remain outside every definition-scoped result below.

/// <summary>
/// Reads and writes the module registration catalogue - installed packages, the per-portal grants
/// over them, the definitions each package publishes and the controls each definition publishes.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IModuleDefinitionRepository"/> over <see cref="DnnDbContext"/> and nothing
/// else. The type is <see langword="internal"/> and <see langword="sealed"/> so that the persistence
/// session cannot escape this assembly and the contract stays the only route to the catalogue: the
/// container resolves it by its interface, and no consumer can name a query surface, a transaction
/// handle or a provider type through it.
/// </para>
/// <para>
/// <b>Reads never track.</b> Every query member states <c>AsNoTracking</c> explicitly, because
/// <see cref="DnnDbContext"/> declares no global tracking behaviour, and because a repository whose
/// reads tracked would let a caller's incidental mutation reach the database on the next commit
/// without any write member having been called. Mutation is therefore always deliberate: the caller
/// asks for it through one of the update members. The load performed inside a delete member is the
/// single exception and is tracked on purpose, since removal is expressed by transitioning a tracked
/// row rather than by issuing a statement.
/// </para>
/// <para>
/// <b>Writes stage; they do not flush.</b> No member calls <c>SaveChanges</c> in either form, opens
/// a transaction, or executes a statement immediately, so a package, its definitions, its controls
/// and its grants can be written as one indivisible batch by <see cref="IUnitOfWork"/>. Bulk removal
/// materialises its rows and stages them rather than issuing a set-based delete, for the same
/// reason: a set-based statement executes at once, bypassing the staging the batch depends on, and
/// leaves the change tracker holding rows the store no longer has. Where no explicit transaction is
/// open it is also durable the instant it runs, ahead of everything the caller has staged.
/// </para>
/// <para>
/// <b>Every multi-row order is total.</b> Where the terminal procedure declared an order it is
/// reproduced and then extended with the entity key, because each legacy sort column - a package
/// friendly name, a nullable control key, a nullable view order - admits ties that would otherwise
/// leave row sequence to the store and make successive reads of unchanged data disagree. Where the
/// terminal procedure declared no order at all, one is imposed for the same reason. Each member
/// records the clause it reproduces.
/// </para>
/// <para>
/// <b>Uniqueness decides the single-row operator.</b> A member returning at most one entity uses
/// <c>SingleOrDefaultAsync</c>, and every one of them is backed either by a primary key or by a
/// unique index declared in the configuration and measured in the upgrade chain. None of them treats
/// a missing row as an error - absence is the null return. A second row would mean the database had
/// lost a constraint it declares, which is worth failing loudly rather than silently answering with
/// whichever row the store happened to yield first.
/// </para>
/// </remarks>
internal sealed class ModuleDefinitionRepository : IModuleDefinitionRepository
{
    /// <summary>
    /// The lowest control ordinal the terminal control-key query admits.
    /// </summary>
    /// <remarks>
    /// Named rather than inlined because it is a reserved-value boundary carried over from the store,
    /// not an arithmetic constant: <c>04.05.00.SqlDataProvider:L1380</c> ends the terminal
    /// <c>GetModuleControlsByKey</c> predicate with <c>AND ControlType &gt;= -1</c>. The earlier form
    /// at <c>02.02.00.SqlDataProvider:L545</c> excluded a single ordinal by inequality
    /// (<c>ControlType &lt;&gt; -2</c>); the terminal form generalises it to a floor, and the
    /// terminal form is the one that governs. See <see cref="GetModuleControlsByKeyAsync"/>.
    /// </remarks>
    private const int LowestSelectableControlType = -1;

    /// <summary>The persistence session this repository stages against.</summary>
    private readonly DnnDbContext _dbContext;

    /// <summary>
    /// Initialises a new instance of the <see cref="ModuleDefinitionRepository"/> class.
    /// </summary>
    /// <param name="dbContext">The request-scoped persistence session.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// MIGRATION: the only dependency, and the only one permitted. The legacy code reached its data
    /// layer through a reflection-resolved static singleton - a provider-type constant, a shared
    /// constructor and a shadowed Instance function at DataProvider.vb:L29-L50 - which
    /// ModuleControlController.vb:L34 then cached in a Private Shared field of its own, making the
    /// dependency invisible at the call site and impossible to substitute in a test. It is declared
    /// here instead. Nothing else is injected: no cache, no logger, no clock, no business-controller
    /// factory, no HTTP accessor and nothing from the security layer. Each is a separate concern, and
    /// a repository that held one would be doing a second job.
    /// </remarks>
    public ModuleDefinitionRepository(DnnDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    // -------------------------------------------------------------------------------------------
    // DesktopModule - the installed module package. DataProvider.vb:L157-L164.
    // -------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Reproduces <c>GetDesktopModule</c> (<c>02.00.00.SqlDataProvider:L5309-L5316</c>), a plain
    /// equality read on the primary key. The key is unique by definition, so the single-row operator
    /// is exact rather than optimistic; a package that does not exist is reported by the null return,
    /// which is the distinction the legacy reflection hydrator could not make - it produced an object
    /// whose every property held a sentinel, indistinguishable from a package whose columns happened
    /// to hold those values.
    /// </remarks>
    public Task<DesktopModule?> GetDesktopModuleByIdAsync(int desktopModuleId, CancellationToken cancellationToken = default) =>
        _dbContext.DesktopModules
            .AsNoTracking()
            .SingleOrDefaultAsync(package => package.DesktopModuleId == desktopModuleId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reproduces <c>GetDesktopModuleByModuleName</c>
    /// (<c>03.01.00.SqlDataProvider:L285-L293</c>), whose predicate is <c>ModuleName = @ModuleName</c>
    /// and nothing more. The argument is matched exactly: the legacy procedure widened it in no way,
    /// applied no pattern and trimmed nothing, and an empty name is the key it is rather than an
    /// absent one.
    /// </para>
    /// <para>
    /// The single-row operator is exact because this is the one uniqueness the terminal schema
    /// imposes on the table - <c>IX_DesktopModules_ModuleName</c> at
    /// <c>03.01.00.SqlDataProvider:L30-L31</c>, declared unique by
    /// <c>DesktopModuleConfiguration</c> and dropped by no later script.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="moduleName"/> is <see langword="null"/>.</exception>
    public Task<DesktopModule?> GetDesktopModuleByModuleNameAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        // MIGRATION: guarded rather than trusted. A null argument would translate to
        // "ModuleName IS NULL" and quietly answer with a row the caller did not ask for, which is
        // the null-row reading the contract excludes; an empty string, by contrast, is a legitimate
        // key and is matched as given.
        ArgumentNullException.ThrowIfNull(moduleName);

        return _dbContext.DesktopModules
            .AsNoTracking()
            .SingleOrDefaultAsync(package => package.ModuleName == moduleName, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetDesktopModules</c> (<c>02.00.00.SqlDataProvider:L5365-L5372</c>), reached
    /// through the ArrayList-returning wrapper at <c>DesktopModuleController.vb:L58</c>. The
    /// legacy <c>order by FriendlyName</c> is reproduced and extended with the key, because
    /// <c>DesktopModules.FriendlyName</c> carries only a plain index in the terminal schema
    /// (<c>03.01.00.SqlDataProvider:L38</c>) and duplicates are ordinary - the same script
    /// back-fills the column at <c>L18-L19</c> - so the legacy clause alone is not a total order.
    /// An installation with no packages yields an empty list, never null.
    /// </para>
    /// <para>
    /// MIGRATION: this member returns <b>every</b> installed package, whereas the legacy procedure
    /// ended its predicate with <c>where IsAdmin = 0</c>
    /// (<c>02.00.00.SqlDataProvider:L5371</c>) and so hid the administrative ones. The divergence is
    /// deliberate and is confined to this member. The contract defines it as the host-level
    /// catalogue - what a host administrator works from when deciding what to grant - and an
    /// administrator cannot decide about a package the catalogue will not show. The exclusion
    /// remains exactly where the question is portal-scoped, in
    /// <see cref="GetDesktopModulesByPortalIdAsync"/>, which is the member the migration record
    /// attributes it to; a caller wanting the legacy reading filters
    /// <see cref="DesktopModule.IsAdmin"/> itself, which it can do because the column is mapped.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<DesktopModule>> GetDesktopModulesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.DesktopModules
            .AsNoTracking()
            .OrderBy(package => package.FriendlyName)
            .ThenBy(package => package.DesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reproduces the terminal <c>GetDesktopModulesByPortal</c>
    /// (<c>04.05.00.SqlDataProvider:L1041-L1064</c>) clause for clause. Administrative packages are
    /// excluded outright (<c>WHERE IsAdmin = 0</c>, <c>L1062</c>); of the remainder a package
    /// qualifies when it is not premium, or when a grant exists for this portal
    /// (<c>AND ( IsPremium = 0 OR (PortalId = @PortalId AND PortalDesktopModuleId IS NOT Null))</c>,
    /// <c>L1063</c>); and the result is ordered by friendly name (<c>L1064</c>), extended here with
    /// the key for the same reason as the catalogue read above.
    /// </para>
    /// <para>
    /// The premium arm traverses the configured <see cref="DesktopModule.PortalDesktopModules"/>
    /// association, which the provider renders as an existence subquery, so the disjunction is
    /// evaluated by the store in one round trip. Reading the catalogue and filtering in memory would
    /// give the same answer and is expressly not done. The existence test also removes the reason
    /// the legacy statement needed <c>SELECT distinct</c>: its left outer join multiplied a package
    /// by its grants, and an existence test cannot.
    /// </para>
    /// <para>
    /// MIGRATION: the portal identifier is applied exactly. It is not a wildcard, and -1 in
    /// particular selects the portal whose key is -1 rather than every portal, even though the
    /// legacy integer sentinel was that same value (<c>Null.vb:L38-L41</c>) and
    /// <c>dbo.Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c> - which is precisely why the
    /// sentinel could not tell a real tenant from a missing one.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<DesktopModule>> GetDesktopModulesByPortalIdAsync(int portalId, CancellationToken cancellationToken = default) =>
        await _dbContext.DesktopModules
            .AsNoTracking()
            .Where(package =>
                !package.IsAdmin
                && (!package.IsPremium
                    || package.PortalDesktopModules.Any(grant => grant.PortalId == portalId)))
            .OrderBy(package => package.FriendlyName)
            .ThenBy(package => package.DesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Replaces <c>AddDesktopModule</c> (<c>DataProvider.vb:L162</c>), twelve positional arguments
    /// assembled at <c>DesktopModuleController.vb:L33</c> and executed as a scalar read of
    /// <c>SCOPE_IDENTITY()</c> at <c>SqlDataProvider.vb:L789</c>. The insertion is staged only, so
    /// <see cref="DesktopModule.DesktopModuleId"/> holds its final value once
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> has completed. Nothing here reads or normalises
    /// <see cref="DesktopModule.SupportedFeatures"/>: the value the caller composed is the value
    /// stored.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="desktopModule"/> is <see langword="null"/>.</exception>
    public async Task AddDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desktopModule);

        await _dbContext.DesktopModules.AddAsync(desktopModule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Replaces <c>UpdateDesktopModule</c> (<c>DataProvider.vb:L163</c>), thirteen positional
    /// arguments assembled at <c>DesktopModuleController.vb:L75</c> and reached through the
    /// overload pair at <c>L70</c> and <c>L74</c> whose only difference was a cache flag. Both the
    /// argument list and the flag are gone: the entity carries the new state and its own identity.
    /// </para>
    /// <para>
    /// MIGRATION: the supplied entity alone is staged, by transitioning its entry rather than by
    /// asking the set to update it. The distinction is load-bearing here rather than stylistic,
    /// because the set-level call also walks the object graph and stages what it reaches - and a
    /// package obtained from <see cref="ModuleDefinition.DesktopModule"/> has its own definitions
    /// hanging off it, so updating one package would have written rows in
    /// <c>dbo.ModuleDefinitions</c> that no caller asked to change.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="desktopModule"/> is <see langword="null"/>.</exception>
    public Task UpdateDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desktopModule);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(desktopModule).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Replaces <c>DeleteDesktopModule</c> (<c>DataProvider.vb:L164</c>, wrapped at
    /// <c>DesktopModuleController.vb:L40</c>). The row is loaded before it is staged for removal, so
    /// removing a package that is already absent is an idempotent no-op rather than a failure - which
    /// is how the legacy statement behaved, since a delete matching nothing is not an error. The load
    /// is deliberately tracked: removal is expressed by transitioning a tracked row. The package's
    /// definitions and per-portal grants go with it through the cascading foreign keys the schema
    /// already declares (<c>02.00.00.SqlDataProvider:L5258</c> and
    /// <c>02.02.02.SqlDataProvider</c>), so this member neither enumerates nor deletes them.
    /// </remarks>
    public async Task DeleteDesktopModuleAsync(int desktopModuleId, CancellationToken cancellationToken = default)
    {
        DesktopModule? package = await _dbContext.DesktopModules
            .SingleOrDefaultAsync(candidate => candidate.DesktopModuleId == desktopModuleId, cancellationToken)
            .ConfigureAwait(false);

        if (package is not null)
        {
            _dbContext.DesktopModules.Remove(package);
        }
    }

    // -------------------------------------------------------------------------------------------
    // PortalDesktopModule - the per-portal grant. DataProvider.vb:L166-L168.
    // -------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetPortalDesktopModules</c> (<c>02.02.02.SqlDataProvider:L3148-L3163</c>),
    /// reached through the ArrayList-returning wrapper at <c>DesktopModuleController.vb:L66</c>. Both
    /// identifiers are applied exactly and the legacy
    /// <c>order by PortalId, DesktopModuleId</c> (<c>L3163</c>) is reproduced, then extended with the
    /// key so the order is total by construction rather than by relying on the constraint.
    /// </para>
    /// <para>
    /// The pair is unique in the terminal schema - <c>IX_PortalDesktopModules</c>, declared unique by
    /// <c>PortalDesktopModuleConfiguration</c> - so at most one grant can match. The list shape is the
    /// contract's, because the uniqueness is the database's guarantee to make rather than this
    /// signature's to assert. Both references are loaded because the legacy statement projected the
    /// portal and package names alongside the grant; those are join projections rather than columns of
    /// the grant, so they are absent from <see cref="PortalDesktopModule"/> and are answered by loading
    /// the grant's own references, which spares a caller a further read per row without inventing a
    /// column.
    /// </para>
    /// <para>
    /// MIGRATION: neither identifier is a wildcard. The legacy wrapper passed both through the
    /// sentinel converter (<c>SqlDataProvider.vb:L799</c>) and the procedure read the resulting NULL
    /// as match-all (<c>L3161-L3162</c>), so -1 in either position returned rows the caller had not
    /// asked for. Here -1 selects the row keyed -1, and a portal holding no such grant yields an
    /// empty list.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PortalDesktopModule>> GetPortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default) =>
        await _dbContext.PortalDesktopModules
            .AsNoTracking()
            .Include(grant => grant.Portal)
            .Include(grant => grant.DesktopModule)
            .Where(grant => grant.PortalId == portalId && grant.DesktopModuleId == desktopModuleId)
            .OrderBy(grant => grant.PortalId)
            .ThenBy(grant => grant.DesktopModuleId)
            .ThenBy(grant => grant.PortalDesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Replaces <c>AddPortalDesktopModule</c> (<c>DataProvider.vb:L167</c>, wrapped at
    /// <c>DesktopModuleController.vb:L36</c>). The grant carries no state beyond the pair it joins,
    /// so its existence is the whole of the fact being recorded. The insertion is staged only, and
    /// nothing is returned: the provider function did yield the generated identity
    /// (<c>SqlDataProvider.vb:L802</c>), but the wrapper is a <c>Sub</c> that discarded it, so the
    /// key was already surplus to the operation. <see cref="PortalDesktopModule.PortalDesktopModuleId"/>
    /// holds its final value once <see cref="IUnitOfWork.SaveChangesAsync"/> has completed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="portalDesktopModule"/> is <see langword="null"/>.</exception>
    public async Task AddPortalDesktopModuleAsync(PortalDesktopModule portalDesktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalDesktopModule);

        await _dbContext.PortalDesktopModules.AddAsync(portalDesktopModule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Replaces <c>DeletePortalDesktopModules</c> (<c>DataProvider.vb:L168</c>, wrapped at
    /// <c>DesktopModuleController.vb:L45</c>). Withdrawing an entitlement means removing the row;
    /// the grant has no field to blank instead. Both identifiers are applied exactly - the legacy
    /// wrapper passed both through the sentinel converter (<c>SqlDataProvider.vb:L805</c>), so -1
    /// once withdrew every portal's grants at once, and it no longer does. Withdrawing an
    /// entitlement that was never held stages nothing and is not an error.
    /// </para>
    /// <para>
    /// MIGRATION: the matching rows are materialised and then staged for removal, rather than deleted
    /// by a set-based statement. A set-based delete would execute at once, ahead of everything else
    /// the caller has staged and outside the change tracker that then still holds the removed rows -
    /// and, absent an explicit transaction, it would be durable immediately. That matters precisely
    /// here, because withdrawing a grant is normally one step of a larger tenant or package change.
    /// The member is named for a set because the legacy procedure was, and it returns no count: the
    /// number of rows a staged change ultimately affects is what
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> reports, and reporting it here would invite a
    /// caller to read it before the change had been made.
    /// </para>
    /// </remarks>
    public async Task DeletePortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default)
    {
        List<PortalDesktopModule> grants = await _dbContext.PortalDesktopModules
            .Where(grant => grant.PortalId == portalId && grant.DesktopModuleId == desktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (grants.Count > 0)
        {
            _dbContext.PortalDesktopModules.RemoveRange(grants);
        }
    }

    // -------------------------------------------------------------------------------------------
    // ModuleDefinition - what a package publishes. DataProvider.vb:L170-L175.
    // -------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetModuleDefinitions</c> (<c>02.02.00.SqlDataProvider:L624-L632</c>), reached
    /// through the ArrayList-returning wrapper at <c>ModuleDefinitionController.vb:L49</c>. A package
    /// that publishes nothing yields an empty list, which is a legitimate state rather than an error:
    /// a package exists independently of the definitions it may later gain.
    /// </para>
    /// <para>
    /// MIGRATION: the identifier is applied exactly, and the legacy wildcard is not reproduced. The
    /// terminal predicate reads
    /// <c>where DesktopModuleId = @DesktopModuleId or @DesktopModuleId = -1</c>
    /// (<c>L632</c>) - the sentinel spelled out as a literal rather than hidden behind a NULL
    /// conversion - so -1 returned every definition in the installation. Here -1 asks for the
    /// definitions of the package keyed -1, and the host-level question it used to smuggle is asked
    /// instead through <see cref="GetModuleDefinitionsByPortalIdAsync"/> with no portal.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy procedure declared no order at all, so successive reads could disagree.
    /// One is imposed here - friendly name, then key - because a caller that renders a list cannot be
    /// left to discover that the sequence is the store's choice. The friendly name is unique among
    /// definitions (<c>IX_ModuleDefinitions</c>), so the key only ever settles a tie the constraint
    /// already forbids; it is present so the order is total by construction.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByDesktopModuleIdAsync(int desktopModuleId, CancellationToken cancellationToken = default) =>
        await _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .Where(definition => definition.DesktopModuleId == desktopModuleId)
            .OrderBy(definition => definition.FriendlyName)
            .ThenBy(definition => definition.ModuleDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: this member has no single legacy counterpart, and it collapses an N+1 the legacy
    /// screens performed by hand: <c>GetDesktopModulesByPortal</c> (<c>DataProvider.vb:L161</c>) to
    /// find the packages a portal could use, then <c>GetModuleDefinitions</c> (<c>L170</c>) once per
    /// package to find what each published. Expressing it as one query is a consequence of the target
    /// architecture rather than an optimisation of the ported rule - the rule itself is unchanged.
    /// </para>
    /// <para>
    /// Availability carries the same rule as <see cref="GetDesktopModulesByPortalIdAsync"/>: an
    /// administrative package is excluded outright, then a remaining definition is placeable when its
    /// owning package is not premium or when a grant exists for the pair
    /// (<c>04.05.00.SqlDataProvider:L1062-L1063</c>). SEC-007 requires the exclusion here because this
    /// member is the single-query replacement for the legacy two-step package-then-definition read; omitting
    /// the package predicate from the collapsed query reintroduced rows the first legacy step had removed.
    /// </para>
    /// <para>
    /// A null portal is neither a sentinel nor a wildcard over rows: it states that the question is
    /// host-level rather than portal-level, which is how a host administrator sees every definition in
    /// order to decide what to grant. Modelling it as a nullable value is what makes it unnecessary to
    /// reserve a numeric value to stand for its absence - the mistake the legacy -1 made. The owning
    /// package reference is dereferenced without a null check because the foreign key is required, so
    /// the provider renders an inner join and a definition without a package cannot be returned.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByPortalIdAsync(int? portalId, CancellationToken cancellationToken = default)
    {
        IQueryable<ModuleDefinition> definitions = _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule);

        if (portalId.HasValue)
        {
            int addressedPortalId = portalId.Value;

            definitions = definitions.Where(definition =>
                !definition.DesktopModule!.IsAdmin
                && (!definition.DesktopModule!.IsPremium
                    || definition.DesktopModule!.PortalDesktopModules.Any(
                        grant => grant.PortalId == addressedPortalId)));
        }

        return await definitions
            .OrderBy(definition => definition.FriendlyName)
            .ThenBy(definition => definition.ModuleDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// SEC-007: administrative definitions are intentionally reachable only through this named lookup. The
    /// portal predicate is proved by a live module instance rather than by a package grant: administrative
    /// modules are installed into the portal by the host and are not content packages a portal may elect to
    /// place. A deleted instance does not make its security settings source available.
    /// </remarks>
    public Task<ModuleDefinition?> GetAdministrativeDefinitionByFriendlyNameAsync(
        int portalId,
        string friendlyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);

        return _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .Where(definition =>
                definition.DesktopModule!.IsAdmin
                && definition.FriendlyName == friendlyName
                && _dbContext.Modules.Any(module =>
                    module.PortalId == portalId
                    && module.ModuleDefinitionId == definition.ModuleDefinitionId
                    && !module.IsDeleted))
            .OrderBy(definition => definition.ModuleDefinitionId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reproduces <c>GetModuleDefinition</c> (<c>02.00.00.SqlDataProvider:L5470-L5477</c>), a plain
    /// equality read on the primary key, reached through <c>ModuleDefinitionController.vb:L41</c>. The
    /// owning package is loaded with it, so a caller can name what published the definition without a
    /// second round trip.
    /// </remarks>
    public Task<ModuleDefinition?> GetModuleDefinitionByIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default) =>
        _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .SingleOrDefaultAsync(definition => definition.ModuleDefinitionId == moduleDefinitionId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reproduces <c>GetModuleDefinitionByName</c> (<c>02.00.00.SqlDataProvider:L5485-L5494</c>),
    /// whose predicate is <c>DesktopModuleId = @DesktopModuleId and FriendlyName = @FriendlyName</c>,
    /// reached through <c>ModuleDefinitionController.vb:L45</c>. Both values are matched exactly and
    /// an empty friendly name is the key it is.
    /// </para>
    /// <para>
    /// The single-row operator is exact because a definition's friendly name is unique in the terminal
    /// schema on its own - <c>01.00.08.SqlDataProvider:L5867</c> adds
    /// <c>CONSTRAINT IX_ModuleDefinitions UNIQUE NONCLUSTERED (FriendlyName)</c> and no later script
    /// drops it - so narrowing by the owning package can only reduce the match further. Note the
    /// asymmetry with the package table, where the equivalent constraint was dropped: it is what makes
    /// this name-based lookup sound while the package friendly-name lookup was retired.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="friendlyName"/> is <see langword="null"/>.</exception>
    public Task<ModuleDefinition?> GetModuleDefinitionByNameAsync(int desktopModuleId, string friendlyName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);

        return _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .SingleOrDefaultAsync(
                definition => definition.DesktopModuleId == desktopModuleId
                    && definition.FriendlyName == friendlyName,
                cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Replaces <c>AddModuleDefinition</c> (<c>DataProvider.vb:L173</c>), assembled at
    /// <c>ModuleDefinitionController.vb:L33</c> from the owning package, the friendly name and the
    /// default cache time, and executed as a scalar read of <c>SCOPE_IDENTITY()</c> at
    /// <c>SqlDataProvider.vb:L818</c>. The insertion is staged only, so
    /// <see cref="ModuleDefinition.ModuleDefinitionId"/> holds its final value once
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> has completed. The owning package is fixed at
    /// insertion, because the legacy update procedure did not accept it and neither does the update
    /// member below.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="moduleDefinition"/> is <see langword="null"/>.</exception>
    public async Task AddModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinition);

        await _dbContext.ModuleDefinitions.AddAsync(moduleDefinition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Replaces <c>UpdateModuleDefinition</c> (<c>DataProvider.vb:L175</c>), reached through the
    /// overload pair at <c>ModuleDefinitionController.vb:L53</c> and <c>L57</c> whose only difference
    /// was a cache flag. The flag is gone and the entity carries the new state.
    /// </para>
    /// <para>
    /// MIGRATION: staging the supplied entity by transitioning its entry, rather than asking the set
    /// to update it, is what keeps this member from doing more than the legacy one did. The legacy
    /// procedure rewrote only the friendly name and the default cache time
    /// (<c>SqlDataProvider.vb:L824</c>); the set-level call would also walk the object graph, and
    /// every definition read by this repository arrives with its owning package attached, so it would
    /// have staged a write to <c>dbo.DesktopModules</c> on every definition update. The owning package
    /// is not moved: <see cref="ModuleDefinition.DesktopModuleId"/> is written back as it stands on
    /// the entity, so a caller that has not changed it changes nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="moduleDefinition"/> is <see langword="null"/>.</exception>
    public Task UpdateModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinition);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(moduleDefinition).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Replaces <c>DeleteModuleDefinition</c> (<c>DataProvider.vb:L174</c>, wrapped at
    /// <c>ModuleDefinitionController.vb:L36</c>). The row is loaded - deliberately tracked, because
    /// removal transitions a tracked row - before it is staged for removal, so removing a definition
    /// that is already absent is an idempotent no-op. Its controls go with it through
    /// <c>FK_ModuleControls_ModuleDefinitions</c>, declared
    /// <c>ON DELETE CASCADE NOT FOR REPLICATION</c> at <c>02.00.00.SqlDataProvider:L5025-L5031</c>, so
    /// this member neither enumerates nor deletes them.
    /// </remarks>
    public async Task DeleteModuleDefinitionAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        ModuleDefinition? definition = await _dbContext.ModuleDefinitions
            .SingleOrDefaultAsync(candidate => candidate.ModuleDefinitionId == moduleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (definition is not null)
        {
            _dbContext.ModuleDefinitions.Remove(definition);
        }
    }

    // -------------------------------------------------------------------------------------------
    // ModuleControl - how a definition is reached. DataProvider.vb:L177-L183.
    // -------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Reproduces <c>GetModuleControl</c> (<c>02.00.00.SqlDataProvider:L5597-L5604</c>), a plain
    /// equality read on the primary key, reached through <c>ModuleControlController.vb:L119</c> and the
    /// hand-written hydration path this replaces. The owning definition is not loaded: a control is
    /// asked for by its own key here, and the definition is nullable on this table, so eager loading it
    /// would add a join that is optional in the schema and unnecessary to the question.
    /// </remarks>
    public Task<ModuleControl?> GetModuleControlByIdAsync(int moduleControlId, CancellationToken cancellationToken = default) =>
        _dbContext.ModuleControls
            .AsNoTracking()
            .SingleOrDefaultAsync(control => control.ModuleControlId == moduleControlId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetModuleControls</c> (<c>04.05.00.SqlDataProvider:L1352-L1360</c>), reached
    /// through the ArrayList-returning wrapper at <c>ModuleControlController.vb:L127</c>. The terminal
    /// <c>ORDER BY ControlKey, ViewOrder</c> (<c>L1360</c>) is reproduced in that order - control key
    /// first, which is the sequence the legacy screens listed - and extended with the key. The
    /// extension is required rather than tidy: both sort columns are nullable, so ties are ordinary,
    /// and the legacy clause alone left their relative order to the store.
    /// </para>
    /// <para>
    /// MIGRATION: the definition identifier is applied exactly. The legacy wrapper passed it through
    /// the sentinel converter (<c>SqlDataProvider.vb:L831</c>) and the procedure paired the resulting
    /// NULL with its own null column - <c>((ModuleDefId is null and @ModuleDefId is null) or
    /// (ModuleDefId = @ModuleDefId))</c> at <c>L1359</c> - so -1 returned the host-level controls that
    /// belong to no definition at all. That reading is not reproduced: because
    /// <see cref="ModuleControl.ModuleDefinitionId"/> is nullable in the schema and the argument here
    /// is not, the definition-less controls satisfy this predicate for no identifier whatsoever. A
    /// definition's controls and the definition-less controls are disjoint sets, and a caller needing
    /// the latter asks through a member of its own rather than by encoding it in this argument.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModuleControl>> GetModuleControlsByDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default) =>
        await _dbContext.ModuleControls
            .AsNoTracking()
            .Where(control => control.ModuleDefinitionId == moduleDefinitionId)
            .OrderBy(control => control.ControlKey)
            .ThenBy(control => control.ViewOrder)
            .ThenBy(control => control.ModuleControlId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetModuleControlsByKey</c> (<c>04.05.00.SqlDataProvider:L1370-L1381</c>), reached
    /// through the ArrayList-returning wrapper at <c>ModuleControlController.vb:L131</c>. A control key
    /// is not unique by itself, which is why this member returns a list where the key-and-source lookup
    /// below returns at most one row: a definition may publish several controls under one key,
    /// distinguished by their source. Both values are applied exactly, and an empty control key is the
    /// key it is. The terminal <c>ORDER BY ViewOrder</c> (<c>L1381</c>) is reproduced - note that it
    /// differs from the by-definition read above, which sorts by control key first - and extended with
    /// the key, because the view order is nullable and admits ties.
    /// </para>
    /// <para>
    /// MIGRATION: the reserved-ordinal exclusion the store carried is reproduced explicitly, because it
    /// is a property of the legacy query rather than of the contract and therefore invisible in the
    /// signature. The terminal predicate ends
    /// <c>AND ControlType &gt;= -1</c> (<c>L1380</c>), which withholds the reserved ordinals below
    /// that floor; the earlier form at <c>02.02.00.SqlDataProvider:L545</c> excluded a single value by
    /// inequality (<c>ControlType &lt;&gt; -2</c>) and the terminal form generalises it. This is the
    /// only place in this file where the ordinal is examined at all, and it is a comparison against a
    /// boundary the store defined - see <see cref="LowestSelectableControlType"/> - never an
    /// interpretation of what the ordinal means. Omitting it would widen the result beyond what the
    /// legacy screens saw, which is why it is honoured here rather than surfaced as an argument a
    /// caller could vary.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="controlKey"/> is <see langword="null"/>.</exception>
    public async Task<IReadOnlyList<ModuleControl>> GetModuleControlsByKeyAsync(string controlKey, int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlKey);

        return await _dbContext.ModuleControls
            .AsNoTracking()
            .Where(control =>
                control.ModuleDefinitionId == moduleDefinitionId
                && control.ControlKey == controlKey
                && control.ControlType >= LowestSelectableControlType)
            .OrderBy(control => control.ViewOrder)
            .ThenBy(control => control.ModuleControlId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reproduces <c>GetModuleControlByKeyAndSrc</c> (<c>04.05.00.SqlDataProvider:L1331-L1342</c>),
    /// reached through <c>ModuleControlController.vb:L123</c>. The three values together are the natural
    /// key the schema enforces with a unique index - <c>IX_ModuleControls</c> over
    /// <c>(ModuleDefID, ControlKey, ControlSrc)</c>, added at <c>02.00.00.SqlDataProvider:L5017</c>,
    /// dropped by no later script and declared unique and unfiltered by
    /// <c>ModuleControlConfiguration</c> - which is why the single-row operator is exact here while the
    /// key-only lookup above returns a list. All three are matched exactly and an empty value in either
    /// text position is the key it is.
    /// </para>
    /// <para>
    /// MIGRATION: the control source completes the key and nothing more. It holds a Web Forms control
    /// path in the legacy data and is carried through untouched; nothing here resolves, loads or probes
    /// for what it names, because the presentation layer is no longer server-rendered. The reserved-
    /// ordinal floor that the key-only read applies is deliberately absent: the terminal procedure for
    /// this lookup declares no such predicate, so applying one would hide a row the legacy read
    /// returned.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controlKey"/> or <paramref name="controlSrc"/> is <see langword="null"/>.
    /// </exception>
    public Task<ModuleControl?> GetModuleControlByKeyAndSrcAsync(int moduleDefinitionId, string controlKey, string controlSrc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlKey);
        ArgumentNullException.ThrowIfNull(controlSrc);

        return _dbContext.ModuleControls
            .AsNoTracking()
            .SingleOrDefaultAsync(
                control => control.ModuleDefinitionId == moduleDefinitionId
                    && control.ControlKey == controlKey
                    && control.ControlSrc == controlSrc,
                cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Replaces <c>AddModuleControl</c> (<c>DataProvider.vb:L181</c>), nine positional arguments
    /// assembled at <c>ModuleControlController.vb:L111</c> - one of them a cast of the access-level
    /// enumeration back to the integer it had always been - and executed as a scalar read of
    /// <c>SCOPE_IDENTITY()</c> at <c>SqlDataProvider.vb:L840</c>. The insertion is staged only, so
    /// <see cref="ModuleControl.ModuleControlId"/> holds its final value once
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> has completed.
    /// <see cref="ModuleControl.ControlType"/> is stored as the plain ordinal supplied, which
    /// legitimately includes negative values, so nothing here validates, clamps or reinterprets it -
    /// including against the floor the key-only read applies, which is a property of that query alone.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="moduleControl"/> is <see langword="null"/>.</exception>
    public async Task AddModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleControl);

        await _dbContext.ModuleControls.AddAsync(moduleControl, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Replaces <c>UpdateModuleControl</c> (<c>DataProvider.vb:L182</c>), ten positional arguments -
    /// the nine of the insertion plus the identity - assembled at
    /// <c>ModuleControlController.vb:L140</c> and reached through the overload pair at <c>L135</c> and
    /// <c>L139</c> whose only difference was a cache flag. Both the argument list and the flag are
    /// gone.
    /// </para>
    /// <para>
    /// MIGRATION: the supplied entity alone is staged, by transitioning its entry rather than by asking
    /// the set to update it, so a control that arrives carrying its owning definition does not drag a
    /// write to <c>dbo.ModuleDefinitions</c> along with it. The nullable columns are written as the
    /// entity holds them: a null control key, control title, control source, icon file, view order or
    /// help address reaches the store as SQL NULL rather than as the empty string or -1 the legacy
    /// sentinel system produced, which is what keeps a definition's default control - identified by a
    /// null control key at <c>04.05.00.SqlDataProvider:L1491</c> - findable at all.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="moduleControl"/> is <see langword="null"/>.</exception>
    public Task UpdateModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleControl);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(moduleControl).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Replaces <c>DeleteModuleControl</c> (<c>DataProvider.vb:L183</c>, wrapped at
    /// <c>ModuleControlController.vb:L114</c>). The row is loaded - deliberately tracked, because
    /// removal transitions a tracked row - before it is staged for removal, so removing a control that
    /// is already absent is an idempotent no-op rather than something the caller has to guard against.
    /// </remarks>
    public async Task DeleteModuleControlAsync(int moduleControlId, CancellationToken cancellationToken = default)
    {
        ModuleControl? control = await _dbContext.ModuleControls
            .SingleOrDefaultAsync(candidate => candidate.ModuleControlId == moduleControlId, cancellationToken)
            .ConfigureAwait(false);

        if (control is not null)
        {
            _dbContext.ModuleControls.Remove(control);
        }
    }
}
