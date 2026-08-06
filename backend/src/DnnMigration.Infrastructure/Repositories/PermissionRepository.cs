using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

// MIGRATION: this type carries rows and interprets none of them. Each of the three legacy static
//            controllers it replaces mixed retrieval with interpretation, and consequently each carried
//            its own copy of the same allow-and-deny precedence rule. Deciding what a caller holds, and
//            rendering a principal's display name from a negative identifier, both live in
//            Infrastructure/Security/PermissionEvaluator.cs, which is the single authority on them.
//            That is why no member below takes a principal, a claims set or a list of role names, none
//            returns a verdict, and none composes a display string.
//
// MIGRATION: no entry is evicted from any cache here either. The two legacy grant controllers made
//            twenty-one static cache calls between them, interleaved with the reads and writes they
//            performed, so a caller could not tell retrieval from invalidation. Invalidation is
//            coordinated by ICacheService at the Application service layer, where the breadth of a
//            change is known - a single grant, a page, or an entire tenant. A repository that evicted
//            on its own behalf would either evict too narrowly to be correct or too widely to be
//            useful, and would do it invisibly to the service that actually knows which happened.
//
// MIGRATION: materialisation is the object-relational mapper's job. The legacy permission code reached
//            rows three different ways - a reflection-driven row hydrator with six call sites in
//            PermissionController.vb alone, column-by-column reader loops that assigned one property
//            per line, and pre-generics CollectionBase wrappers over the two grant families - and none
//            of the three produces a target file. Every read below hands back a materialised
//            IReadOnlyList<T> or a single nullable entity: no untyped collection, no dictionary keyed
//            by a stringly-typed column name, no reflection, and never an open query that would let a
//            caller keep composing against a live connection it does not own.
//
// MIGRATION: table names, ANSI string types, lengths and the permission-key text conversion are pinned
//            once in Persistence/Configurations and are deliberately not restated here. The three
//            physical tables are SINGULAR - and stating that in this file would create a second place
//            for it to be wrong. In particular the key column is varchar rather than nvarchar, so
//            nothing below applies a case conversion, a collation hint or any other manual coercion to
//            a permission string: doing so would make each parameter a different type from the column
//            it is compared against and cost the unique index its usefulness for lookup.
//
// MIGRATION: the two legacy grant readers selected from vw_ModulePermissions and vw_TabPermissions.
//            Neither view is mapped as an entity and neither is queried. Both were flat projections
//            that left-joined the catalogue and the roles table onto a grant and folded the negative
//            role identifiers into a display name, so mapping one would introduce a fourth entity that
//            owns no table, duplicates three that do, and bakes presentation into the persistence
//            model. The base tables are queried instead and the joined payload is reached through the
//            navigations the entity configurations already declare.

/// <summary>
/// Reads and writes the permission catalogue together with the grants recorded against a module
/// instance and against a page.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this one type replaces the data-access halves of three static controllers -
/// <c>PermissionController.vb</c> (9 public members), <c>ModulePermissionController.vb</c> (18) and
/// <c>TabPermissionController.vb</c> (15) - and the thirty-three permission procedures they reached
/// through a reflection-instantiated provider singleton. The singleton accessor is gone: the context
/// arrives by constructor injection, so a test substitutes a database rather than defeating a static
/// initialiser.
/// </para>
/// <para>
/// <strong>Negative role identifiers are real stored data.</strong> Neither grant table declares a
/// foreign key on its role column, and in this schema that is deliberate rather than an omission: a
/// grant's role identifier may name a pseudo-principal that has no row in the roles table at all, and
/// the legacy grant views folded exactly three of them into a display name - <c>-1</c> for all users,
/// <c>-2</c> for the superuser and <c>-3</c> for unauthenticated users. Nothing below reads a negative
/// role identifier as absent, converts one to a null, or filters one out. Absence is expressed only by
/// the nullable CLR property being null, which is the one thing a stored negative value is not.
/// </para>
/// <para>
/// <strong>The wildcard in the two-argument readers is a different thing entirely.</strong> It happens
/// to be <c>-1</c> as well, which is precisely why the two are separated by name below rather than
/// left to a shared constant. A <c>-1</c> arriving as a filter argument means "every one of them"; a
/// <c>-1</c> stored in a role column means a specific principal. Collapsing the two would turn a query
/// widening into a principal lookup, or the reverse.
/// </para>
/// <para>
/// <strong>Reads do not track.</strong> Every read-only query is issued without change tracking, so a
/// materialised grant cannot be mutated into an accidental update by unrelated work sharing the scoped
/// context. The single-row load that precedes a single-row removal is the deliberate exception: staging
/// one removal requires the entity the change tracker will act on.
/// </para>
/// <para>
/// <strong>Reads project the grant and nothing beyond it.</strong> No read below pulls the catalogue
/// entry, the role or the account alongside a grant. A grant is five scalars and its own key; the
/// principal it names is a bare identifier, deliberately, because a negative identifier names a
/// pseudo-principal that has no row to join to at all. Every consumer in this solution decides access
/// from those scalars and resolves the catalogue separately by identifier when it needs display text, so
/// a joined payload was cost without a reader: on a tenant-wide or page-wide grant read it multiplied
/// every returned row by an account row, and an account row carries the credential and contact columns
/// that an authorisation read has no business loading. Reaching the joined values through the configured
/// navigations remains available to a future consumer that genuinely needs them - the relationships are
/// declared in <c>Persistence/Configurations</c> - but it is that consumer's read that should ask for
/// them, at the breadth it actually needs.
/// </para>
/// <para>
/// <strong>Single-row writes and removals are staged; bulk removals are immediate.</strong> No member
/// below commits. The inserts, the updates and the two single-grant removals stage, which is what allows
/// a batch - a page's grants copied onto each of its children, or the tenant-creation sequence that
/// writes portals, aliases, roles, pages and modules together - to succeed or fail as one operation, and
/// each staged entity carries its generated key once the unit of work commits. The four BULK removals are
/// different in kind and say so: each issues one set-based statement that reaches the store when it is
/// called. That is the only way to remove an unbounded number of rows without first loading every one of
/// them into the change tracker, and it costs nothing in atomicity, because a set-based statement enlists
/// in whatever transaction the context already has open. A caller combining a bulk removal with other
/// work therefore opens a transaction through <see cref="IUnitOfWork"/> - which is exactly what the sole
/// caller of the two account-scoped removals does, and what its own remarks already describe these
/// members as requiring.
/// </para>
/// </remarks>
internal sealed class PermissionRepository : IPermissionRepository
{
    /// <summary>
    /// The value the module position of the module-grant reader accepts in place of a real module
    /// identifier to mean "every module".
    /// </summary>
    /// <remarks>
    /// MIGRATION: measured from the terminal procedure body rather than assumed. The module-grant
    /// reader recreated by <c>04.04.00.SqlDataProvider</c> guards its scope argument with
    /// <c>(@ModuleID = -1 OR ModuleID = @ModuleID …)</c>, so a caller asking for every grant in the
    /// installation has no other way to say so and the wildcard is part of the contract the domain
    /// interface inherits. It is a query widening and nothing else - emphatically not an absence
    /// marker, and unrelated to <see cref="ModulePermission.RoleId"/> holding the same number.
    /// </remarks>
    private const int AnyModuleId = -1;

    /// <summary>
    /// The value the permission position of both two-argument grant readers accepts in place of a real
    /// permission identifier to mean "every permission".
    /// </summary>
    /// <remarks>
    /// MIGRATION: the module-grant reader in <c>04.04.00.SqlDataProvider</c> and the page-grant reader
    /// recreated by <c>04.05.00.SqlDataProvider</c> both guard this argument with
    /// <c>(PermissionID = @PermissionID OR @PermissionID = -1)</c>. Declared separately from
    /// <see cref="AnyModuleId"/> on purpose: the numbers coincide, the contracts do not - the page
    /// reader carries a wildcard here and none at all in its page position, so there is deliberately
    /// no page-side counterpart to <see cref="AnyModuleId"/> for anyone to reach for.
    /// </remarks>
    private const int AnyPermissionId = -1;

    /// <summary>
    /// The scope code carried by the catalogue entries every module definition shares.
    /// </summary>
    /// <remarks>
    /// MIGRATION: reference data seeded by the upgrade chain, appearing in the terminal bodies of the
    /// module-scoped catalogue reader (<c>04.05.03.SqlDataProvider</c>) and the module-grant reader
    /// (<c>04.04.00.SqlDataProvider</c>). It is a stored value rather than a configurable one, which is
    /// why it is named here once instead of repeated as a literal at each use. It is not a mapping
    /// declaration: no table or column name appears in this file.
    /// </remarks>
    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    /// <summary>The scope code carried by the catalogue entries every page shares.</summary>
    /// <remarks>
    /// MIGRATION: the counterpart literal in the terminal body of the page-scoped catalogue reader
    /// (<c>04.05.03.SqlDataProvider</c>), where it is the ONLY predicate the statement applies.
    /// </remarks>
    private const string TabScopeCode = "SYSTEM_TAB";

    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="PermissionRepository"/> class.</summary>
    /// <param name="dbContext">The context scoped to the current unit of work.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dbContext"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// MIGRATION: the only dependency, by design. The legacy controllers additionally reached a static
    /// cache module, a reflection-created provider singleton and the ambient request context; none of
    /// the three has a counterpart here, because caching is coordinated above this layer, the provider
    /// indirection is replaced by injection, and nothing about reading a row depends on who asked.
    /// </remarks>
    public PermissionRepository(DnnDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    // =================================================================================================
    // Permission - the catalogue. A row declares that an action exists; it grants nothing.
    // =================================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal single-row reader selects the five catalogue columns for one primary
    /// key, so at most one row can match and the answer is a nullable entity rather than a list. An
    /// identifier naming no row yields <see langword="null"/>, which is an answer rather than a fault.
    /// </remarks>
    public Task<Permission?> GetByIdAsync(int permissionId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Permissions
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.PermissionId == permissionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The set is snapshotted into an array before it reaches the predicate, so the translated
    /// membership test is built once from a stable sequence rather than from a collection the caller
    /// could still be mutating. Duplicates collapse first: a repeated identifier would lengthen the
    /// parameter list without widening the answer.
    /// </para>
    /// <para>
    /// An empty request short-circuits with no round trip, which is correctness as much as economy - the
    /// answer is knowably empty, so issuing a statement to learn it spends a round trip on nothing.
    /// </para>
    /// <para>
    /// Ordering by identifier matches every other catalogue read on this type, so the whole family
    /// returns a stable sequence.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByIdsAsync(
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissionIds);

        if (permissionIds.Count == 0)
        {
            return Array.Empty<Permission>();
        }

        int[] wanted = permissionIds.Distinct().ToArray();

        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => wanted.Contains(entry.PermissionId))
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal definition-scoped reader (<c>04.05.03.SqlDataProvider</c>) filters on the
    /// definition column alone and orders by permission identifier. Both halves are reproduced exactly.
    /// A definition that declares no entry yields an empty list, which the legacy statement also did.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByModuleDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => entry.ModuleDefinitionId == moduleDefinitionId)
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal module-scoped catalogue reader (<c>04.05.03.SqlDataProvider</c>) resolves
    /// the module's own definition through a scalar subquery and takes the union of that definition's
    /// entries with every entry carrying the product-wide module-definition scope code. Both halves are
    /// reproduced, because dropping the second would silently narrow the answer for every module in the
    /// installation.
    /// </para>
    /// <para>
    /// It reads the catalogue and not the grant table, even though its argument names a module instance.
    /// That asymmetry is the legacy shape and is kept: there is no join to a grant here, so a module
    /// with no grants at all still receives the entries that apply to it.
    /// </para>
    /// <para>
    /// A module identifier naming no row contributes nothing from the first half and still yields the
    /// second, which is what the legacy scalar subquery did - comparing a column to a null subquery
    /// result matched nothing rather than failing.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        // Composed as a subquery rather than resolved in a first round trip, so the union below stays a
        // single statement exactly as the legacy procedure was. Nothing is materialised from it: it
        // appears only inside a predicate, so no module entity is tracked or returned.
        IQueryable<int> owningDefinitionIds = _dbContext.Modules
            .Where(module => module.ModuleId == moduleId)
            .Select(module => module.ModuleDefinitionId);

        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => owningDefinitionIds.Contains(entry.ModuleDefinitionId)
                || entry.PermissionCode == ModuleDefinitionScopeCode)
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: <strong>the singular legacy name is a misnomer and the plural return is deliberate.</strong>
    /// The terminal procedure (<c>04.06.00.SqlDataProvider</c>) compares each column to its argument and
    /// can match many rows - the uniqueness rule on this table spans the scope code, the definition and
    /// the key, so one code-and-key pair may legitimately exist once per definition. The legacy wrapper
    /// returned a collection for exactly that reason. Preserving the singular name here and returning a
    /// single entity would silently discard rows.
    /// </para>
    /// <para>
    /// MIGRATION: both columns are compared by direct equality, which is what the terminal statement
    /// does. The scope code is matched as stored, so whether the comparison is case sensitive remains a
    /// property of the column's collation exactly as it was before this migration - applying a case
    /// conversion here would override that decision, diverge on a case-sensitive installation, and make
    /// the predicate unusable by the unique index. Trimming and other input normalisation belong to the
    /// Application layer, which already performs them before calling.
    /// </para>
    /// <para>
    /// MIGRATION: the key is compared as the domain enumeration. The configured text conversion turns it
    /// into the member's own spelling - the exact value a production database already holds - so the
    /// comparison happens against the stored text without this file naming any spelling, and no numeric
    /// ordinal reaches the column.
    /// </para>
    /// <para>
    /// The terminal statement declares no ordering. Ordering by identifier is added so repeated calls
    /// return the same sequence; it narrows nothing, since the legacy caller received the same rows in
    /// whatever order the engine chose.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByCodeAndKeyAsync(
        string permissionCode,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissionCode);

        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => entry.PermissionCode == permissionCode && entry.PermissionKey == permissionKey)
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal page-scoped catalogue reader (<c>04.05.03.SqlDataProvider</c>) filters on
    /// the product-wide page scope code, so every EXISTING page receives the same catalogue. SEC-033 adds
    /// only the missing existence predicate: an identifier naming no page now yields no metadata rather
    /// than the installation-wide page catalogue. It does not narrow the catalogue for a real page.
    /// </para>
    /// <para>
    /// The argument is therefore used to prove the resource exists, while the catalogue entries themselves
    /// remain selected by the shared page scope code. Portal ownership is proved by the application service,
    /// which has the tenant identity this repository deliberately does not.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        IQueryable<int> matchingTabIds = _dbContext.Tabs
            .Where(tab => tab.TabId == tabId)
            .Select(tab => tab.TabId);

        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => matchingTabIds.Any() && entry.PermissionCode == TabScopeCode)
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: staged for removal rather than removed. The entity is loaded WITH change tracking -
    /// the one deliberate exception to the no-tracking rule on this type - because the change tracker
    /// needs the instance it is being asked to act on. An identifier naming no row stages nothing and is
    /// not a fault, which is what the legacy procedure did.
    /// </remarks>
    public async Task DeleteAsync(int permissionId, CancellationToken cancellationToken = default)
    {
        Permission? entry = await _dbContext.Permissions
            .FirstOrDefaultAsync(candidate => candidate.PermissionId == permissionId, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return;
        }

        _dbContext.Permissions.Remove(entry);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the legacy insert took four positional arguments and returned the generated key from
    /// <c>SCOPE_IDENTITY()</c>. The four values travel as properties on one entity and no key is
    /// returned, because returning one would force this member to commit on its own and destroy the
    /// unit-of-work boundary. The identity property on the passed entity holds its key once the caller
    /// commits.
    /// </remarks>
    public Task AddAsync(Permission permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permission);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Permissions.Add(permission);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the legacy update took five positional arguments led by the identifier; all five
    /// travel as properties on one entity. Staged explicitly rather than left to change detection, so
    /// the call behaves identically whether the caller mutated a tracked entity or rebuilt a detached
    /// one.
    /// <para>
    /// MIGRATION: the staging ASSIGNS THE STATE and does not call <c>DbSet.Update</c>. <c>Update</c>
    /// decides between <c>Added</c> and <c>Modified</c> by asking whether the key "is set" - reading an
    /// <see cref="int"/> key of 0 as unset - and then walks the navigation graph applying the same test
    /// to everything it reaches. This schema seeds four identity columns at 0 and one at -1, so that
    /// test is unsafe as a general mechanism here even where a given table starts at 1: a grant carrying
    /// a loaded principal would be enough. Assigning <see cref="EntityState.Modified"/> attaches this
    /// entity alone, marks its scalar properties modified, and consults neither the key nor the graph.
    /// </para>
    /// </remarks>
    public Task UpdateAsync(Permission permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permission);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<Permission> entry = _dbContext.Entry(permission);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    // =================================================================================================
    // ModulePermission - one grant recorded against one module instance, to a role or to an account.
    //
    // MIGRATION: no read below loads the catalogue entry, the role or the account alongside the grant,
    //            and the legacy view is the reason the question arises rather than the reason to do it.
    //            The legacy reader selected every column of a view that left-joined the catalogue and the
    //            roles table onto the grant, so a role NAME and a permission NAME were part of what a
    //            caller received - but the caller that mattered used them to render a permission matrix,
    //            and that presentation concern does not exist on this side of the boundary: the display
    //            name for a negative role identifier is composed in Infrastructure/Security, and the
    //            catalogue is resolved there by identifier through the set-based catalogue read.
    //
    //            So the joined payload had no reader here and was not free. A tenant-wide or page-wide
    //            grant read multiplied every row it returned by a catalogue row, a role row and an
    //            ACCOUNT row, and an account row carries the credential and contact columns that an
    //            authorisation read must not be pulling across the wire at all. The raw identifier is the
    //            fact - it is the only thing that is always present, since a grant addressed to a
    //            pseudo-principal has no role row to join to - and the navigations remain declared in
    //            Persistence/Configurations for a consumer that one day genuinely needs them to ask at
    //            its own breadth.
    // =================================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal single-row grant reader (<c>04.04.00.SqlDataProvider</c>) selects one row
    /// by primary key. This member has no counterpart in the page-grant family, and that absence is
    /// measured rather than accidental - see the note above the page section.
    /// </remarks>
    public Task<ModulePermission?> GetModulePermissionByIdAsync(int modulePermissionId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ModulePermissions
            .AsNoTracking()
            .FirstOrDefaultAsync(grant => grant.ModulePermissionId == modulePermissionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: <strong>both arguments carry the legacy wildcard, and each is translated by composing
    /// the predicate rather than by pushing the comparison into it.</strong> The terminal body guards its
    /// scope argument with <c>(@ModuleID = -1 OR ModuleID = @ModuleID …)</c> and its permission argument
    /// with <c>(PermissionID = @PermissionID OR @PermissionID = -1)</c>. Applying each filter only when
    /// it is not the wildcard produces the same four answers this one member has always given - every
    /// grant on one module, one permission on one module, one permission across every module, and every
    /// grant in the installation - while letting each shape be satisfied by an index instead of an
    /// unconditional disjunction that can be satisfied by none.
    /// </para>
    /// <para>
    /// MIGRATION: the wildcard is a filter sentinel for a positive identity and nothing more. It is
    /// compared against the arguments only, never against a stored column, so a persisted role
    /// identifier of the same value is untouched by it and is returned as the real principal it names.
    /// The two concerns do not share a constant here precisely so that they cannot be conflated.
    /// </para>
    /// <para>
    /// MIGRATION: the terminal body additionally unions in grants whose module column is null carrying
    /// the product-wide module-definition scope code. That branch cannot arise against this model - the
    /// grant entity declares a non-nullable module identifier behind an enforced foreign key - so no
    /// member reproduces it, and inventing one would describe rows this schema cannot hold.
    /// </para>
    /// <para>
    /// Denying grants come back alongside allowing ones. A consumer that never saw a denial would be
    /// unable to suppress anything, which is the one failure mode an access-control read must not have.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByModuleIdAsync(
        int moduleId,
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ModulePermission> query = _dbContext.ModulePermissions.AsNoTracking();

        if (moduleId != AnyModuleId)
        {
            query = query.Where(grant => grant.ModuleId == moduleId);
        }

        if (permissionId != AnyPermissionId)
        {
            query = query.Where(grant => grant.PermissionId == permissionId);
        }

        // Finishing on the primary key makes the sequence total. The three columns before it group the
        // rows the way a permission matrix reads, but they cannot order it on their own: under either
        // wildcard two rows can agree on all three.
        return await query
            .OrderBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.ModulePermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal tenant-scoped grant reader (<c>04.04.00.SqlDataProvider</c>) joins the
    /// grant to the modules table and filters on its tenant column, which is what keeps one tenant's
    /// grants away from another tenant's answer. The join is expressed through the configured module
    /// relationship rather than restated as a manual join. The module's tenant column is nullable, and
    /// comparing it to a plain value excludes the null rows exactly as the legacy inner join did, so
    /// installation-wide modules are not attributed to a tenant that does not own them.
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ModulePermissions
            .AsNoTracking()
            .Where(grant => grant.Module.PortalId == portalId)
            .OrderBy(grant => grant.ModuleId)
            .ThenBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.ModulePermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal page-scoped grant reader (<c>04.04.00.SqlDataProvider</c>) joins the grant
    /// to the placement table on the module and filters on the page. It returns MODULE grants selected by
    /// page, never page grants, and the page-grant family has no mirror image of it - a genuine
    /// cross-scope query that exists on one side only.
    /// </para>
    /// <para>
    /// The placement path is composed as a membership test over the configured placement set, so a module
    /// placed on the page more than once contributes its grants once rather than once per placement. It
    /// resolves which modules are on the page and nothing else: no grant is judged here and no principal
    /// name is composed.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        IQueryable<int> placedModuleIds = _dbContext.TabModules
            .Where(placement => placement.TabId == tabId)
            .Select(placement => placement.ModuleId);

        return await _dbContext.ModulePermissions
            .AsNoTracking()
            .Where(grant => placedModuleIds.Contains(grant.ModuleId))
            .OrderBy(grant => grant.ModuleId)
            .ThenBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.ModulePermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal bulk removal filters on the module column alone and removes the matching
    /// rows in one statement. This does the same: ONE set-based delete carrying the same predicate, and no
    /// load. Removing nothing is a legitimate outcome, so a module with no grants issues its statement,
    /// affects no row and reports nothing - the contract yields no count, exactly as the legacy procedure
    /// yielded none.
    /// </para>
    /// <para>
    /// <strong>The number of grants on a module is not bounded by anything, which is why this cannot load
    /// them.</strong> A load-then-stage removal reads every matching row into memory and into the change
    /// tracker, then emits one parameterised statement per row: the cost of removing a module's grants
    /// would scale with how many it has, on a path that needs none of their values. This member is
    /// immediate rather than staged for that reason alone.
    /// </para>
    /// <para>
    /// Immediacy costs nothing in atomicity, and that is a property of the statement rather than an
    /// assumption about the caller. A set-based delete is issued on the same connection as the rest of the
    /// unit of work and enlists in whatever transaction the context already has open, so a caller that
    /// removes a module's grants alongside the module itself opens a transaction through
    /// <see cref="IUnitOfWork"/> and gets one rollback boundary over both. What immediacy does mean is
    /// that the caller must open that transaction: without one the statement is independently durable the
    /// moment it is called. The account-scoped counterpart below documents the same requirement, and its
    /// caller satisfies it.
    /// </para>
    /// </remarks>
    public Task DeleteModulePermissionsByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ModulePermissions
            .Where(grant => grant.ModuleId == moduleId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal account-scoped bulk removal (<c>04.08.00.SqlDataProvider</c>) joins the
    /// grant to the modules table so that only the named tenant's grants go. <strong>Both</strong>
    /// predicates are therefore load-bearing: filtering on the account alone would strip that account's
    /// grants in every other tenant it belongs to, which is a cross-tenant data loss rather than a
    /// missing filter.
    /// </para>
    /// <para>
    /// MIGRATION: only grants naming the account itself are removed. A grant the account receives through
    /// a role belongs to the role, and removing it would revoke access from every other holder of that
    /// role. The account column is nullable and is compared to a plain value, so role-addressed grants
    /// are not matched.
    /// </para>
    /// <para>
    /// ONE set-based delete, issued when this member is called rather than when changes are flushed, and
    /// no load: the number of grants an account holds directly is unbounded, and none of their values is
    /// needed to remove them. The tenant predicate is expressed through the configured module relationship
    /// rather than a restated join, so the statement is a delete guarded by an existence test over the
    /// modules table - which is what the legacy procedure's join amounted to.
    /// </para>
    /// <para>
    /// This member is one half of a two-table cleanup, and the half-cleaned state its caller must avoid is
    /// prevented by a TRANSACTION rather than by staging. A set-based delete enlists in the transaction the
    /// context already has open, so both halves sit inside one rollback boundary; the caller opens that
    /// boundary explicitly through <see cref="IUnitOfWork"/> precisely because these two statements are
    /// immediate, and its own remarks say so. Staging instead would put both halves in one flush but would
    /// pay for it by loading every row of both tables first.
    /// </para>
    /// </remarks>
    public Task DeleteModulePermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ModulePermissions
            .Where(grant => grant.UserId == userId && grant.Module.PortalId == portalId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal <c>DeleteRole</c> procedure (<c>03.00.10.SqlDataProvider</c>) removed this
    /// family by role identifier alone, and so does this. There is deliberately NO tenant predicate: a
    /// role belongs to exactly one tenant, so its identifier already bounds the removal, whereas the
    /// account-scoped sibling above needs one because an account belongs to many.
    /// </para>
    /// <para>
    /// MIGRATION: only grants ADDRESSED TO the role go. The role column is nullable and is compared to a
    /// plain value, so an account-addressed grant is not matched, and neither is a grant addressed to one
    /// of the negative pseudo-principals the terminal schema persists in this column - those name no
    /// <c>Roles</c> row, so no role removal can be the reason to discard them.
    /// </para>
    /// <para>
    /// ONE set-based delete, issued when this member is called rather than when changes are flushed, and
    /// no load: the number of grants a role holds is unbounded and none of their values is needed in order
    /// to remove them. Immediacy makes a transaction the caller's responsibility rather than an option -
    /// this member is one third of a three-table cleanup that must stand or fall with the role removal
    /// itself, and a set-based delete enlists in the transaction the context already has open, so all four
    /// statements sit inside one rollback boundary. The caller opens that boundary explicitly.
    /// </para>
    /// </remarks>
    public Task DeleteModulePermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ModulePermissions
            .Where(grant => grant.RoleId == roleId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: staged for removal, with the row loaded WITH change tracking because the change tracker
    /// needs the instance it is being asked to act on. An identifier naming no row stages nothing and is
    /// not a fault.
    /// </remarks>
    public async Task DeleteModulePermissionAsync(int modulePermissionId, CancellationToken cancellationToken = default)
    {
        ModulePermission? grant = await _dbContext.ModulePermissions
            .FirstOrDefaultAsync(candidate => candidate.ModulePermissionId == modulePermissionId, cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return;
        }

        _dbContext.ModulePermissions.Remove(grant);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the legacy insert took five positional arguments and returned the generated key from
    /// <c>SCOPE_IDENTITY()</c>. The five values travel as properties on one entity and no key is
    /// returned, so a batch of grants stages together and commits atomically. The nullable role and
    /// account properties are staged exactly as supplied: a negative role identifier is a real principal
    /// and is never coerced to a null, and a null is never turned into a number.
    /// </remarks>
    public Task AddModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modulePermission);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.ModulePermissions.Add(modulePermission);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the legacy update took six positional arguments led by the identifier; all six travel
    /// as properties on one entity. Staged explicitly rather than left to change detection, so the call
    /// behaves identically for a tracked entity and for a detached one, and the stored role and account
    /// values survive the round trip unchanged.
    /// <para>
    /// MIGRATION: the staging ASSIGNS THE STATE rather than calling <c>DbSet.Update</c>, which walks the
    /// navigation graph and reads an <see cref="int"/> key of 0 as unset. A grant points at
    /// <c>dbo.Modules</c>, whose <c>ModuleID</c> is <c>IDENTITY(0, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L221</c>), so a detached grant carrying its loaded module would have
    /// had that module INSERTED as a duplicate rather than left alone. Assigning
    /// <see cref="EntityState.Modified"/> touches this row and nothing else.
    /// </para>
    /// </remarks>
    public Task UpdateModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modulePermission);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<ModulePermission> entry = _dbContext.Entry(modulePermission);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    // =================================================================================================
    // TabPermission - one grant recorded against one page, to a role or to an account.
    //
    // MIGRATION: this section is deliberately NARROWER than the module section above, in three separate
    //            and independently measured ways. The legacy provider declared a single-row reader for a
    //            module grant and NO equivalent single-row reader for a page grant - seven members here
    //            against nine there - even though it declared a single-row REMOVAL for both. It also
    //            declared a cross-scope reader that selects module grants by page, with no page-side
    //            counterpart. And its two-argument page reader guards only its permission argument,
    //            while the module one guards both. None of the three gaps is closed: the asymmetry is
    //            what the legacy surface measurably is, so no page-grant getter by identifier is
    //            invented here, and no page wildcard constant exists for one to be built from. A
    //            reviewer looking for the missing members should stop here rather than add them.
    //
    // MIGRATION: as in the module section, the catalogue entry, role and account are NOT loaded alongside
    //            each grant, for the reasons recorded there - the joined payload existed to render a
    //            legacy screen, has no reader on this side of the boundary, and put account columns into
    //            an authorisation read. The legacy view also carried the tenant column, sourced from its
    //            inner join to the pages table; that column is still reached through the configured page
    //            relationship where a member filters on it, so no fourth entity is introduced to hold a
    //            column that belongs to the page.
    // =================================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal tenant-scoped page-grant reader (<c>04.04.00.SqlDataProvider</c>) filters
    /// on the tenant column its view sources from the pages table, so the tenant path is expressed here
    /// through the configured page relationship.
    /// </para>
    /// <para>
    /// MIGRATION: that statement also matches the installation-wide rows whose tenant column is null, but
    /// <strong>only when its argument is itself null</strong>. This contract takes a plain value, so the
    /// branch is unreachable and installation-wide grants are not returned - comparing a nullable column
    /// to a value excludes nulls, which is the same answer the legacy statement gave for a non-null
    /// argument. A caller needing those rows asks for them by their own scope rather than receiving them
    /// mixed into a tenant's answer.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<TabPermission>> GetTabPermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.TabPermissions
            .AsNoTracking()
            .Where(grant => grant.Tab.PortalId == portalId)
            .OrderBy(grant => grant.TabId)
            .ThenBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.TabPermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: <strong>the wildcard applies to the permission argument only.</strong> The terminal body
    /// (<c>04.05.00.SqlDataProvider</c>) guards its permission argument with
    /// <c>(PermissionID = @PermissionID OR @PermissionID = -1)</c> and matches its page exactly, with no
    /// guard of any kind. The page filter below is therefore unconditional while the permission filter is
    /// composed only when a real identifier is named - the shape of the code is the shape of the measured
    /// asymmetry, not an oversight in one of the two readers.
    /// </para>
    /// <para>
    /// MIGRATION: this is the second half of the reason the two wildcard constants are declared
    /// separately. Only the permission one is reachable from here; there is no page equivalent, so the
    /// code cannot accidentally widen an argument the legacy statement never widened.
    /// </para>
    /// <para>
    /// MIGRATION: the terminal body additionally unions in grants whose page column is null carrying the
    /// product-wide page scope code. That branch cannot arise against this model, whose grant entity
    /// declares a non-nullable page identifier behind an enforced foreign key, so no member reproduces
    /// it. Denying grants come back alongside allowing ones.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<TabPermission>> GetTabPermissionsByTabIdAsync(
        int tabId,
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TabPermission> query = _dbContext.TabPermissions
            .AsNoTracking()
            .Where(grant => grant.TabId == tabId);

        if (permissionId != AnyPermissionId)
        {
            query = query.Where(grant => grant.PermissionId == permissionId);
        }

        return await query
            .OrderBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.TabPermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The same predicate and the same wildcard convention as the single-page member, widened to a set, with
    /// the page leading the ordering so each page's slice reads identically to what that member would return.
    /// </remarks>
    public async Task<IReadOnlyList<TabPermission>> GetTabPermissionsByTabIdsAsync(
        IReadOnlyCollection<int> tabIds,
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabIds);

        if (tabIds.Count == 0)
        {
            return Array.Empty<TabPermission>();
        }

        int[] wanted = tabIds.Distinct().ToArray();

        IQueryable<TabPermission> query = _dbContext.TabPermissions
            .AsNoTracking()
            .Where(grant => wanted.Contains(grant.TabId));

        if (permissionId != AnyPermissionId)
        {
            query = query.Where(grant => grant.PermissionId == permissionId);
        }

        return await query
            .OrderBy(grant => grant.TabId)
            .ThenBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.TabPermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal bulk removal filters on the page column alone and removes the matching rows
    /// in one statement, which is what this issues - ONE set-based delete carrying that predicate, with no
    /// load. Removing nothing is a legitimate outcome and yields no count, as the legacy procedure yielded
    /// none.
    /// </para>
    /// <para>
    /// The count of grants on a page is unbounded - a page's grant set grows with the roles and accounts an
    /// installation has - and this path needs none of their values, so loading them to remove them would
    /// make the cost of clearing a page scale with its history. Immediate rather than staged for that
    /// reason; a caller pairing it with the page's own removal, or replacing a page's grants wholesale,
    /// opens a transaction through <see cref="IUnitOfWork"/> and both statements share one rollback
    /// boundary, because a set-based delete enlists in the transaction the context already holds.
    /// </para>
    /// </remarks>
    public Task DeleteTabPermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabPermissions
            .Where(grant => grant.TabId == tabId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the terminal account-scoped bulk removal (<c>04.08.00.SqlDataProvider</c>) joins the
    /// grant to the pages table so that only the named tenant's grants go, and both predicates are
    /// load-bearing for the same reason as in the module counterpart: filtering on the account alone would
    /// strip its grants in every other tenant it belongs to.
    /// </para>
    /// <para>
    /// MIGRATION: only grants naming the account itself are removed; a grant reaching the account through
    /// a role belongs to the role. ONE set-based delete with no load, for the same reason as its module
    /// counterpart: the row count is unbounded and no value on those rows is needed to remove them. This
    /// half and that one land inside a single TRANSACTION the caller opens rather than inside a single
    /// flush - the alternative leaves an account half-cleaned, and a later account reusing the identifier
    /// would inherit whatever was left behind, which is exactly what the enclosing transaction prevents.
    /// </para>
    /// </remarks>
    public Task DeleteTabPermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabPermissions
            .Where(grant => grant.UserId == userId && grant.Tab.PortalId == portalId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the page-scoped half of the role cleanup the terminal <c>DeleteRole</c> procedure
    /// performed (<c>03.00.10.SqlDataProvider</c>), by role identifier alone. Every consideration recorded
    /// on the module counterpart applies unchanged - no tenant predicate is needed because a role belongs
    /// to one tenant, account-addressed grants and the negative pseudo-principals are not matched, and the
    /// set-based delete is immediate, so the caller's transaction is what makes the three-table cleanup and
    /// the role removal one outcome.
    /// </remarks>
    public Task DeleteTabPermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabPermissions
            .Where(grant => grant.RoleId == roleId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the folder-scoped cleanup the terminal <c>DeleteRole</c> procedure performed, and the
    /// first statement in its body (<c>03.00.10.SqlDataProvider</c>). It is issued as a statement against
    /// the table rather than through a <c>DbSet</c> because the folder grant table is not part of the
    /// mapped model - file management is outside this migration's scope - while still being present in
    /// every upgraded DotNetNuke database this solution binds to.
    /// </para>
    /// <para>
    /// THE TABLE'S EXISTENCE IS TESTED BY THE STATEMENT ITSELF, in one round trip, and nothing is created,
    /// altered or dropped either way. A database that lacks the table is left untouched and the member
    /// reports success, because a role in such a database cannot hold a folder grant and so has nothing to
    /// lose; a database that has it loses exactly the rows the legacy procedure removed. Probing first and
    /// deleting second would be two round trips and a race, and issuing the delete unconditionally would
    /// fault the enclosing transaction on the greenfield schema.
    /// </para>
    /// <para>
    /// The provider guard is not defensive dressing: <c>OBJECT_ID</c> is a SQL Server function, and the
    /// integration suites that run against another relational provider would fault on the statement rather
    /// than skip it. The same guard, for the same reason, protects the external membership statements in
    /// <c>Persistence/MembershipStore.cs</c>.
    /// </para>
    /// <para>
    /// The role identifier crosses as a bound parameter through interpolated composition, so no value is
    /// concatenated into the statement text. The predicate is the legacy predicate exactly - equality on
    /// the role column - so a grant addressed to an account, and a grant addressed to one of the negative
    /// pseudo-principals, are both left alone.
    /// </para>
    /// </remarks>
    public async Task DeleteFolderPermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default)
    {
        if (!_dbContext.Database.IsSqlServer())
        {
            return;
        }

        _ = await _dbContext.Database
            .ExecuteSqlInterpolatedAsync(
                $@"
IF OBJECT_ID(N'[dbo].[FolderPermission]', N'U') IS NOT NULL
    DELETE FROM [dbo].[FolderPermission] WHERE [RoleID] = {roleId};",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the legacy block offers this removal by identifier while offering no read by identifier,
    /// which is part of the measured asymmetry recorded above the section. The row is loaded WITH change
    /// tracking and staged; an identifier naming no row stages nothing and is not a fault.
    /// </remarks>
    public async Task DeleteTabPermissionAsync(int tabPermissionId, CancellationToken cancellationToken = default)
    {
        TabPermission? grant = await _dbContext.TabPermissions
            .FirstOrDefaultAsync(candidate => candidate.TabPermissionId == tabPermissionId, cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return;
        }

        _dbContext.TabPermissions.Remove(grant);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the legacy insert took five positional arguments and returned the generated key from
    /// <c>SCOPE_IDENTITY()</c>. The five values travel as properties on one entity and no key is
    /// returned, which is what lets a page's grants be copied onto each of its children as one commit
    /// rather than one commit per grant. The nullable role and account properties are staged as supplied:
    /// a negative role identifier is a real principal and survives untouched.
    /// </remarks>
    public Task AddTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabPermission);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.TabPermissions.Add(tabPermission);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the legacy update took six positional arguments led by the identifier; all six travel as
    /// properties on one entity. Staged explicitly rather than left to change detection, so the call
    /// behaves identically for a tracked entity and for a detached one.
    /// <para>
    /// MIGRATION: the staging ASSIGNS THE STATE rather than calling <c>DbSet.Update</c>, which walks the
    /// navigation graph and reads an <see cref="int"/> key of 0 as unset. A grant points at
    /// <c>dbo.Tabs</c>, whose <c>TabID</c> is <c>IDENTITY(0, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L140</c>), so a detached grant carrying its loaded page would have
    /// had that page INSERTED into the portal's page tree as a duplicate. Assigning
    /// <see cref="EntityState.Modified"/> touches this row and nothing else.
    /// </para>
    /// </remarks>
    public Task UpdateTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabPermission);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<TabPermission> entry = _dbContext.Entry(tabPermission);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }
}
