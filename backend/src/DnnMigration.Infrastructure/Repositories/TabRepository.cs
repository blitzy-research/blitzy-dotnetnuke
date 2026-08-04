using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

// MIGRATION: this is the only implementation of ITabRepository, and it replaces the entire legacy page
// data path: the fourteen-member 'tab block of the abstract data provider
// (Library/Components/Providers/Data/DataProvider.vb lines 109-123), the procedure invocations that
// backed it in Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb, and the data-access
// half of the 1,302-line Library/Components/Tabs/TabController.vb. The reflection-created static
// singleton those call sites reached through, DataProvider.Instance() (DataProvider.vb lines 31-50), is
// gone entirely: this type is registered with the container and receives its context by construction.

// MIGRATION: the long positional writes collapse into entity staging, and the generated key moves.
// AddTab took EIGHTEEN positional arguments (DataProvider.vb:L110) and returned the new key because its
// procedure ended in SCOPE_IDENTITY(); UpdateTab existed twice, at SIXTEEN arguments (L111) and at
// NINETEEN (L112), differing only by a RefreshInterval/PageHeadText/IsSecure tail while writing the very
// same row; UpdateTabOrder took five (L113). Every one of those arguments is a property of Tab, so the
// members here take the entity or its key, the overload pair does not survive, and no write returns an
// identifier - the key is read from Tab.TabId once the unit of work has committed. The unit of work owns
// the commit: nothing in this file calls SaveChanges, which is what allows a portal's first pages to be
// written in the same transaction as the portal, its alias, its roles and its modules.

// MIGRATION: no row is hydrated by hand and no untyped collection crosses the boundary. The legacy
// controller filled a TabInfo column by column from an IDataReader, and the reflection hydrator in
// Library/Components/Shared/CBO.vb did the same job generically elsewhere; the Entity Framework
// materialiser replaces both, so this file contains no fill method, no reader loop and no CBO call. The
// legacy return shapes go with them - the ArrayList of GetTabs (TabController.vb:L516) and
// GetTabsByParentId (L524), the Dictionary(Of Integer, TabInfo) that GetTabsByPortal built (L528) and
// the Hashtable the ordering routine kept positions in - replaced by typed IReadOnlyList<Tab>,
// IReadOnlyList<TabModule> and one IReadOnlyCollection<int>. No portal-keyed or path-keyed dictionary is
// constructed anywhere below.

// MIGRATION: absence is a null, never a sentinel, and no identifier value is reserved.
// Library/Components/Shared/Null.vb encoded absence as -1 for an integer, Date.MinValue for a date and
// the EMPTY STRING for text, and every legacy read pushed those values into the SQL-nullable columns on
// the way out. Tab.PortalId and Tab.ParentId are int? here, so a host page and a root page are
// recognised by null - not by -1, which dbo.Portals.PortalID being IDENTITY(-1, 1) also makes a genuine
// portal key. Nothing below collapses a null back to a number, and because dbo.Tabs.TabID is
// IDENTITY(0, 1) nothing reads 0, -1 or default(int) as "absent", "unsaved" or "not supplied": TabId 0
// is a real, persisted page and is addressed like any other.

// MIGRATION: the TWELVE DataCache sites in TabController.vb are intentionally absent, as is the
// ignoreCache flag its GetTab overload carried (L467, and the branches at L472 and L489). Caching a page
// listing is a cross-cutting concern with an invalidation contract of its own - the legacy page write
// evicted the portal's page collection AND then the portal itself, so that the portal's page count could
// not go stale - and that contract belongs to the coordinated cache service the application layer
// already consults. A repository that decided for itself when to serve a stale answer would hide the
// decision from every caller, so no member here takes a cache flag, promises a cached read or evicts
// anything.

// MIGRATION: page names are NOT unique in the terminal schema. IX_Tabs over (PortalID, TabName) was
// added by 01.00.08.SqlDataProvider line 6072 and dropped again by 02.00.01.SqlDataProvider line 64, so
// duplicates are legal storage rather than corrupt data. Every name lookup below therefore orders
// deterministically and takes the FIRST match - which is exactly what the terminal procedure's
// "order by TabID" and its caller's first-item selection did - and none uses a single-match terminal
// that would throw on data the store permits. Rejecting a duplicate is an application-layer rule, which
// is why TabNameExistsAsync reports the fact instead of raising.

// MIGRATION: orchestration and computed projections are not persistence, and none of them is implemented
// here. UpdatePortalTabOrder (TabController.vb:L550, seven parameters with an optional tail) drove the
// private MoveTab helper (L243) across an in-memory list of a portal's pages before persisting anything;
// GetTabByTabPath (L1101) and GetTabPathDictionary (L1111) computed a lookup from the full tab set with
// no procedure behind either; DeserializePanes (L984), the DeserializeTab overloads (L1009-L1025) and the
// SerializeTab overloads (L1141-L1166) exchanged XmlNode, XmlDocument and Hashtable for portal-template
// import and export; CopyDesignToChildren (L375), CopyPermissionsToChildren (L387) and CopyTab (L414)
// spanned tabs, modules and permissions at once; and GetTabPanes (DataProvider.vb:L123) projected
// distinct pane-name strings rather than rows - "select distinct(PaneName) ... from TabModules where
// TabId = @TabId" - which is a projection over the rows GetTabModulesAsync already returns. The
// persistence primitive the reordering path eventually reached, UpdateTabOrder, IS surfaced, as
// UpdateOrderAsync; deciding which pages move, to what depth and in what order is the TabService's work.

// MIGRATION: TabModule ownership is SPLIT, which is why only two placement members appear here and both
// are reads. "Which modules sit on this page" is a page-centric question and is answered here; creating,
// moving and removing a placement, and everything to do with module settings and per-placement settings,
// belong to IModuleRepository, which owns the module aggregate a placement is part of. A page owns where
// modules sit on it, not their lifecycle. Neither read below flattens Module, ModuleDefinition or
// ModuleControl columns into its result the way the legacy joined projection did - the single 58-property
// legacy class is split along the real table boundaries in the target - and neither loads or evaluates a
// page permission.

/// <summary>
/// Reads the <c>dbo.Tabs</c> and <c>dbo.TabModules</c> rows that form a portal's page hierarchy and
/// stages writes against them, implementing <see cref="ITabRepository"/> over
/// <see cref="DnnDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The type takes exactly one dependency - the context - and deliberately takes no cache, no clock, no
/// logger, no permission evaluator, no HTTP accessor and no sibling repository. It contains no mapping
/// declaration, no table or column literal and no SQL: the schema binding lives in
/// <c>Persistence/Configurations/TabConfiguration.cs</c> and
/// <c>Persistence/Configurations/TabModuleConfiguration.cs</c>, which own it under Rules T3 and T4, and
/// every query below is composed in LINQ so that a column rename cannot silently break at run time.
/// </para>
/// <para>
/// WRITES ARE STAGED AND NEVER COMMITTED. <see cref="AddAsync"/>, <see cref="UpdateAsync"/>,
/// <see cref="UpdateOrderAsync"/> and <see cref="DeleteAsync"/> record an intention against the change
/// tracker; <see cref="IUnitOfWork"/> commits it, once, for the whole request. No write member returns a
/// generated key, because the key is not known until that commit.
/// </para>
/// <para>
/// DELETION HAS TWO DISTINCT MEANINGS AND THEY ARE KEPT APART, because the legacy schema kept them
/// apart. Sending a page to the recycle bin is a field change - set <see cref="Tab.IsDeleted"/> and
/// stage <see cref="UpdateAsync"/>, which is exactly what the legacy update procedure's
/// <c>IsDeleted</c> argument did. <see cref="DeleteAsync"/> is the permanent removal that the legacy
/// <c>DeleteTab</c> performed as a literal <c>delete from Tabs where TabId = @TabId</c>
/// (02.00.00.SqlDataProvider lines 1446-1455). Neither substitutes for the other, and no read member
/// hides a recycled page: <see cref="Tab.IsDeleted"/> is projected, so whether a listing shows recycled
/// pages is the caller's policy. The one exception is stated on
/// <see cref="ListParentTabIdsAsync"/>, whose question is about navigability rather than about rows.
/// </para>
/// <para>
/// ORDERING IS PRODUCED HERE AND CALLERS DO NOT RE-SORT. Each list read reproduces its terminal
/// procedure's own <c>ORDER BY</c> and then APPENDS the primary key, so the sequence is both the legacy
/// sequence and total - two rows can never come back in an arbitrary relative order, and a caller
/// paging over the same data cannot repeat or omit a row.
/// </para>
/// <para>
/// EVERY READ MEMBER IS DETACHED. <c>AsNoTracking</c> is applied to all thirteen reads. That is a
/// considered departure from the sibling repositories in this folder, which decline it because their
/// callers mutate a listed entity and commit it without staging it again. No caller of THIS contract
/// does: <c>TabService</c> stages every page it edits explicitly and records why - whether a read is
/// tracked is an infrastructure decision the application layer must not depend on - the two placement
/// reads are consumed only to project identifiers, and the remaining reads answer questions rather than
/// yield candidates for mutation. Detaching them has a correctness payoff beyond the change-tracker
/// work it saves on the portal-wide listing that a renumbering pass reads in full: it makes the
/// detached branch of <see cref="UpdateOrderAsync"/> the ordinary path, and that branch is what actually
/// confines a positional write to the four columns the legacy procedure wrote. Both staging members
/// accept a detached page precisely so this holds.
/// </para>
/// <para>
/// The single exception is the resolution inside <see cref="DeleteAsync"/>, which is TRACKED on purpose.
/// It resolves a page in order to remove it, so the instance it obtains must be the one the change
/// tracker already holds when the caller has touched that page earlier in the same unit of work; a
/// detached second instance bearing the same key could not be attached alongside the first.
/// </para>
/// </remarks>
internal sealed class TabRepository : ITabRepository
{
    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="TabRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public TabRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>GetTab</c>, whose terminal definition is the whole of
    /// <c>WHERE TabId = @TabId</c> (04.04.00.SqlDataProvider lines 462-467). Because
    /// <c>dbo.Tabs.TabID</c> is <c>IDENTITY(0, 1)</c>, zero is a legitimate key and is passed straight
    /// through; absence is reported as <see langword="null"/> rather than as an exception, since a
    /// caller acting on a client-supplied identifier cannot know in advance that the row exists. A
    /// recycled page is still a page and is returned.
    /// </remarks>
    public Task<Tab?> GetByIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        return _context.Tabs
            .AsNoTracking()
            .FirstOrDefaultAsync(tab => tab.TabId == tabId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabs</c>, whose terminal definition filtered on the portal alone and ordered by
    /// <c>TabOrder, TabName</c> (04.04.00.SqlDataProvider lines 440-448). The primary key is appended to
    /// that ordering and nothing is substituted into it, so the sequence stays the legacy sequence while
    /// becoming total.
    /// </para>
    /// <para>
    /// The ordering IS the hierarchy ordering. <c>Tabs.TabOrder</c> is a single portal-wide sequence
    /// that the legacy renumbering routine maintained in steps precisely so that a parent precedes its
    /// own children and siblings keep their relative positions, so ordering by it yields depth-first
    /// order directly and callers are documented not to re-sort.
    /// </para>
    /// <para>
    /// Recycled pages are INCLUDED, because the terminal procedure applied no predicate to
    /// <c>IsDeleted</c> and projected the column instead. They still occupy positions in the ordering,
    /// so a caller renumbering the tree must see them; a listing that must hide them filters on
    /// <see cref="Tab.IsDeleted"/> itself, which is where that policy belongs. The terminal predicate's
    /// second branch, which matched host-level pages when the argument was null, is unreachable through
    /// this member because the parameter is a non-nullable integer - exactly as it was unreachable
    /// through the legacy provider member, whose parameter was also non-nullable. Host-level pages are
    /// read through <see cref="GetAllAsync"/>.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetByPortalIdAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        // The portal is matched as a value, never as a range or a wildcard: -1 and 0 are both genuine
        // portal keys in this schema, so neither can be read as "every portal" or "no portal".
        return await _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId == portalId)
            .OrderBy(tab => tab.TabOrder)
            .ThenBy(tab => tab.TabName)
            .ThenBy(tab => tab.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ports <c>GetAllTabs</c>, whose terminal definition carried no portal predicate at all and ordered
    /// by <c>TabOrder, TabName</c> (04.04.00.SqlDataProvider lines 475-480). Host-level pages, whose
    /// portal column is itself null, are therefore included alongside every portal's pages, and the rows
    /// of different portals interleave exactly as the legacy read returned them - grouping them is a
    /// presentation concern and is not imposed here. This is deliberately a separate member rather than a
    /// sentinel understood by <see cref="GetByPortalIdAsync"/>, because no numeric value is free to mean
    /// "every portal". Being unbounded, it is an administrative read.
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .AsNoTracking()
            .OrderBy(tab => tab.TabOrder)
            .ThenBy(tab => tab.TabName)
            .ThenBy(tab => tab.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabByName</c>, whose terminal definition filtered on the name AND the portal and
    /// ordered its matches by key - <c>where TabName = @TabName and ((PortalId = @PortalId) or
    /// (@PortalId is null AND PortalId is null)) order by TabID</c> (04.04.00.SqlDataProvider lines
    /// 490-503). Both parameters are load-bearing, which makes this a portal-scoped question rather than
    /// the resolution of a name to a page anywhere in the installation.
    /// </para>
    /// <para>
    /// MIGRATION: page names are not unique at the terminal state, so the LOWEST-KEYED match is
    /// returned. That is not a tolerated ambiguity but the measured legacy outcome: the procedure
    /// returned every match ordered by key and the controller took the first
    /// (TabController.vb:L186-L212). A single-match terminal would throw on data the store legitimately
    /// permits, so none is used.
    /// </para>
    /// <para>
    /// The name is matched as a whole value - never as a prefix, suffix, fragment or pattern - so no
    /// character the caller supplies can broaden what matches. Comparison ignores case and surrounding
    /// whitespace, which is the normalisation every name lookup in this folder applies and which matches
    /// the case-insensitive collation the legacy equality predicate ran under. The terminal predicate's
    /// host-level branch is unreachable here because the portal parameter is a non-nullable integer, as
    /// it was on the legacy provider member; host-level uniqueness is answered by
    /// <see cref="TabNameExistsAsync"/>, which does accept a null portal.
    /// </para>
    /// </remarks>
    public Task<Tab?> GetByNameAsync(
        string tabName,
        int portalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        // The comparison is lowered on both sides rather than expressed as a string.Equals overload
        // taking a StringComparison. That is deliberate and is the idiom every name lookup in this
        // folder uses: this predicate is an expression tree that the provider must translate to SQL,
        // and a StringComparison argument has no SQL counterpart, so the query would compile and then
        // fail at run time. Lowering translates to LOWER(...) and therefore makes the case-insensitive
        // comparison a property of the query rather than of the database's collation, which is what the
        // contract requires. An analyser suggestion to the contrary must not be applied here.
        return _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId == portalId && tab.TabName.ToLower() == wanted)
            .OrderBy(tab => tab.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The parent-scoped variant the legacy controller exposed alongside the portal-scoped one
    /// (TabController.vb:L508), which narrowed the same lookup to one branch of the tree so that two
    /// sibling groups could each hold a page of the same name.
    /// </para>
    /// <para>
    /// MIGRATION: the narrowing is now absolute, where the legacy helper's was not.
    /// <c>GetTabByNameAndParent</c> scanned the portal-wide matches for one whose parent agreed and,
    /// finding none, RETURNED THE FIRST MATCH ANYWAY (TabController.vb:L205-L208) - so asking for a page
    /// under one parent could yield a page under another. This member returns <see langword="null"/>
    /// instead. The fallback is dropped rather than reproduced because it silently answers a different
    /// question from the one asked, and a caller that wants the portal-wide answer can ask for it
    /// explicitly through <see cref="GetByNameAsync(string, int, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// The parent is matched as a value, so 0 denotes the page whose key is 0 and never "no parent": a
    /// page at the root of the tree stores no parent at all and is consequently never returned by this
    /// overload. Root-level pages are found through the portal-scoped overload combined with a
    /// <see cref="Tab.ParentId"/> test.
    /// </para>
    /// </remarks>
    public Task<Tab?> GetByNameAsync(
        string tabName,
        int portalId,
        int parentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        // Lowered on both sides for the reason given on the portal-scoped overload: a StringComparison
        // argument cannot be translated to SQL.
        return _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId == portalId
                && tab.ParentId == parentId
                && tab.TabName.ToLower() == wanted)
            .OrderBy(tab => tab.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabsByParentId</c>, whose terminal definition filtered on the parent alone and ordered
    /// by the portal-wide sequence - <c>WHERE ParentId = @ParentId ORDER BY TabOrder</c>
    /// (04.04.00.SqlDataProvider lines 422-430). BOTH of its omissions are preserved deliberately: there
    /// is no portal predicate, so the result is not restricted to one portal, and there is no
    /// <c>IsDeleted</c> predicate, so recycled children are included. Only the primary key is appended,
    /// to make the ordering total.
    /// </para>
    /// <para>
    /// The parent is matched as a value and -1 is not treated as an absence here or anywhere else in this
    /// class: a page whose parent is recorded as -1 is a child of the page keyed -1. Pages at the root of
    /// the tree store no parent at all and are therefore not addressable through this member; they are
    /// read from <see cref="GetByPortalIdAsync"/> and identified by a <see langword="null"/>
    /// <see cref="Tab.ParentId"/>. Because a page's parent determines its portal in practice, the
    /// unscoped form is safe for a caller that already trusts the parent; one that does not should use
    /// the portal-scoped overload.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetByParentIdAsync(
        int parentId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.ParentId == parentId)
            .OrderBy(tab => tab.TabOrder)
            .ThenBy(tab => tab.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The portal-scoped variant the legacy controller exposed alongside the unscoped one
    /// (TabController.vb:L524), which applied the portal filter after the fact. It is an overload rather
    /// than a nullable portal parameter because a null portal would be ambiguous between "every portal"
    /// and the real stored meaning of <c>PortalId IS NULL</c>, which is "a host-level page" - and
    /// encoding two meanings in one value is the sentinel confusion this migration removes.
    /// <see cref="GetByParentIdAsync(int, CancellationToken)"/> is the explicit every-portal form, so
    /// neither meaning has to be inferred. Recycled children remain included, as in the unscoped form.
    /// </remarks>
    public async Task<IReadOnlyList<Tab>> GetByParentIdAsync(
        int parentId,
        int portalId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.ParentId == parentId && tab.PortalId == portalId)
            .OrderBy(tab => tab.TabOrder)
            .ThenBy(tab => tab.TabId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabCount</c>. THIS IS NOT A PLAIN ROW COUNT, and the whole of the legacy arithmetic is
    /// reproduced because the figure is the portal's advertised page total - it populated the
    /// <c>Pages</c> property that the portal quota is compared against - so "correcting" it would
    /// silently change every quota decision. The terminal definition (04.04.00.SqlDataProvider lines
    /// 511-528) resolves the portal's administration page and then counts the portal's pages while
    /// excluding that page AND ITS DIRECT CHILDREN, subtracting one further:
    /// </para>
    /// <para>
    /// <c>SET @AdminTabId = (SELECT AdminTabId FROM Portals WHERE PortalID = @PortalID)</c> then
    /// <c>SELECT COUNT(*) - 1 FROM Tabs WHERE (PortalID = @PortalID) AND (TabID &lt;&gt; @AdminTabId)
    /// AND (ParentId &lt;&gt; @AdminTabId OR ParentId IS NULL)</c>. All three predicates and the
    /// subtraction are load-bearing; the third is easy to overlook and excludes the administration area's
    /// own sub-pages from a tenant's page allowance.
    /// </para>
    /// <para>
    /// The administration page is resolved in its own read rather than as a correlated sub-query so that
    /// the null case can be made explicit instead of relying on the reader to recall three-valued logic.
    /// When the portal has no row, or records no administration page, every predicate above involves a
    /// null and evaluates to UNKNOWN, so the legacy statement counted no row at all and yielded -1. That
    /// is returned verbatim. Recycled pages are counted, because the legacy procedure applied no
    /// <c>IsDeleted</c> predicate, and a portal holding only its administration page yields a negative
    /// figure exactly as the legacy expression did - the value is a metric to be reported and compared,
    /// never an identifier and never a presence test.
    /// </para>
    /// </remarks>
    public async Task<int> CountByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        int? adminTabId = await _context.Portals
            .AsNoTracking()
            .Where(portal => portal.PortalId == portalId)
            .Select(portal => portal.AdminTabId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (adminTabId is not int administrationTabId)
        {
            // Reached both when the portal does not exist and when it records no administration page.
            // The legacy statement could not distinguish them either: comparing against a null
            // @AdminTabId made every row's predicate UNKNOWN, so COUNT(*) was 0 and the expression
            // returned 0 - 1.
            return -1;
        }

        int counted = await _context.Tabs
            .AsNoTracking()
            .CountAsync(
                tab => tab.PortalId == portalId
                    && tab.TabId != administrationTabId
                    // The null disjunct is spelled out rather than left to the provider's null
                    // semantics, because it is the legacy predicate's own "OR ParentId IS NULL" branch
                    // and it is what keeps every root page inside the count.
                    && (tab.ParentId == null || tab.ParentId != administrationTabId),
                cancellationToken)
            .ConfigureAwait(false);

        return counted - 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>GetTabModules</c> - "which modules are placed on this page" - whose terminal definition
    /// is <c>WHERE TabId = @TabId ORDER BY ModuleOrder</c> (04.04.00.SqlDataProvider lines 601-609). The
    /// ordering is that one with the placement's own key appended, and nothing is inserted ahead of
    /// <see cref="TabModule.ModuleOrder"/>: grouping placements by pane is a rendering concern, and
    /// imposing it here would reorder every result the legacy read produced.
    /// </para>
    /// <para>
    /// The placements come back as <see cref="TabModule"/> entities rather than as the flattened
    /// module-and-placement join the legacy procedure produced, because the single 58-property legacy
    /// class that spanned Modules, TabModules, ModuleDefinitions and ModuleControls is split in the
    /// target along the real table boundaries. No module column is folded in and no permission is
    /// consulted. The page is addressed by key alone, and 0 is a real page.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetTabModulesAsync(
        int tabId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabModules
            .AsNoTracking()
            .Where(placement => placement.TabId == tabId)
            .OrderBy(placement => placement.ModuleOrder)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>GetPortalTabModules</c>, whose terminal definition took the portal and the page together
    /// (03.00.08.SqlDataProvider line 313).
    /// </para>
    /// <para>
    /// MIGRATION: the portal argument is now ENFORCED. The legacy procedure declared <c>@PortalId</c> and
    /// then never referenced it - its predicate was <c>where TM.TabId = @TabId</c> alone - so a page
    /// identifier arriving from a client could read another tenant's page content merely by being well
    /// formed. Here the portal is a tenant-isolation guard: the placement's own page must belong to that
    /// portal, and nothing at all is returned when it does not. The test is expressed against the page
    /// because a placement carries no portal of its own, and the relationship is a required one, so a
    /// placement whose page is missing is excluded rather than admitted.
    /// </para>
    /// <para>
    /// The ordering matches <see cref="GetTabModulesAsync"/>, since the legacy pair ordered identically
    /// by <c>TM.ModuleOrder</c>. The legacy definition additionally carried
    /// <c>and ControlKey is null</c>, a predicate over the joined ModuleControls rows that selected the
    /// default view control; it has no counterpart because this member returns placement rows rather than
    /// the joined control projection, and module controls are read through their own contract.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetPortalTabModulesAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabModules
            .AsNoTracking()
            .Where(placement => placement.TabId == tabId && placement.Tab.PortalId == portalId)
            .OrderBy(placement => placement.ModuleOrder)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }


    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: this replaces a correlated sub-select that the legacy page reads carried in their own
    /// projection - <c>'HasChildren' = case when exists (select 1 from Tabs T2 where T2.ParentId =
    /// Tabs.TabId) then 'true' else 'false' end</c>, present in the terminal page view and in every read
    /// built over it. Evaluating it once per row is what this member exists to avoid: ONE round trip
    /// answers the question for a whole listing, and the answer comes back as a set of keys rather than
    /// as a flag smeared across rows that would each have to carry it. No page is materialised to answer
    /// it - only the parent column is read - and the distinct reduction happens in the database rather
    /// than after the fact.
    /// </para>
    /// <para>
    /// Recycled pages are NOT treated as children, which is the one place a read member in this class
    /// applies an <c>IsDeleted</c> predicate. That is deliberate and follows from the question rather
    /// than from a listing policy: a parent whose only children sit in the recycle bin has no child a
    /// caller can navigate to, which is precisely what the legacy flag was asked to report.
    /// </para>
    /// <para>
    /// A <see langword="null"/> portal is an explicit "not restricted" here, and it is available only
    /// because this member returns a derived projection rather than addressing rows by key. A supplied
    /// portal identifier still denotes exactly that portal, with -1 and 0 remaining genuine keys. The
    /// restriction is applied to the CHILDREN, which is equivalent to restricting the parents because a
    /// page's parent determines its portal, and cheaper because it needs no join.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyCollection<int>> ListParentTabIdsAsync(
        int? portalId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Tab> children = _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.ParentId != null && !tab.IsDeleted);

        if (portalId is int owningPortalId)
        {
            // Composed as a separate clause on the resolved value rather than as a comparison against
            // the nullable argument, so that the "not restricted" case cannot degenerate into a search
            // for pages whose portal column is null - which is a different and real stored state.
            children = children.Where(tab => tab.PortalId == owningPortalId);
        }

        List<int> parents = await children
            .Select(tab => tab.ParentId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A set, because the contract promises the caller's per-row membership test is a hash lookup
        // rather than a linear scan of a list it would otherwise have to convert itself.
        return new HashSet<int>(parents);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: reproduces the correlated sub-select that the legacy portal read view resolved into the
    /// <c>SuperTabId</c> column of its result set - <c>select TabId from Tabs where PortalId is null and
    /// ParentId is null</c> - and that every later revision of that view carried unchanged. The value is
    /// identical for every portal because it is not stored against a portal at all, which is why it is
    /// read once here instead of being projected onto every portal row.
    /// </para>
    /// <para>
    /// Host-level pages are the rows whose portal column is itself null, so this is a genuine absence of
    /// a portal and not a portal numbered 0 or -1. The projection to a nullable integer before the
    /// terminal is what makes the absent case reportable: <c>dbo.Tabs.TabID</c> is
    /// <c>IDENTITY(0, 1)</c>, so a default-valued result would be indistinguishable from the page whose
    /// key is 0. Ordering by key keeps the answer stable in the event that an installation holds more
    /// than one such row.
    /// </para>
    /// </remarks>
    public Task<int?> GetHostRootTabIdAsync(CancellationToken cancellationToken = default)
    {
        return _context.Tabs
            .AsNoTracking()
            .Where(tab => tab.PortalId == null && tab.ParentId == null)
            .OrderBy(tab => tab.TabId)
            .Select(tab => (int?)tab.TabId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The reporting form of the name lookup: it answers the uniqueness question without materialising a
    /// page, which is what a validator needs, and it reports a fact instead of raising because the
    /// terminal schema does not enforce uniqueness - <c>IX_Tabs</c> over <c>(PortalID, TabName)</c> was
    /// added by 01.00.08.SqlDataProvider line 6072 and dropped again by 02.00.01.SqlDataProvider line 64.
    /// The comparison is portal-wide rather than sibling-wide, matching the scope of that index.
    /// </para>
    /// <para>
    /// Recycled pages are EXCLUDED, so that sending a page to the recycle bin genuinely frees its name for
    /// reuse. Comparison ignores case and surrounding whitespace, as the contract requires and as the
    /// other name lookups in this class do. A null portal addresses the host-level pages - the rows whose
    /// portal column is itself null, a real stored state rather than a stand-in for a portal numbered 0 or
    /// -1 - which is the branch the legacy name predicate carried and the reason this member accepts a
    /// nullable portal where <see cref="GetByNameAsync(string, int, CancellationToken)"/> does not. A
    /// supplied exclusion of 0 excludes the page whose key is 0, since that is a real page.
    /// </para>
    /// </remarks>
    public Task<bool> TabNameExistsAsync(
        int? portalId,
        string tabName,
        int? excludingTabId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabName);

        string wanted = tabName.Trim().ToLowerInvariant();

        // Lowered on both sides for the reason given on GetByNameAsync: a StringComparison argument
        // cannot be translated to SQL, and the contract requires the comparison to ignore case
        // regardless of the collation the database happens to carry.
        IQueryable<Tab> candidates = _context.Tabs
            .AsNoTracking()
            .Where(tab => !tab.IsDeleted && tab.TabName.ToLower() == wanted);

        candidates = portalId is int owningPortalId
            ? candidates.Where(tab => tab.PortalId == owningPortalId)
            : candidates.Where(tab => tab.PortalId == null);

        if (excludingTabId is int disregarded)
        {
            // So that renaming a page does not collide with itself. Composed on the resolved value, so
            // that "exclude none" is the absence of a clause rather than a reserved number.
            candidates = candidates.Where(tab => tab.TabId != disregarded);
        }

        return candidates.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>AddTab</c>, whose eighteen positional arguments are replaced by the entity. The page is
    /// stored as supplied: assigning its position in the sequence, its depth, its path and its parent, and
    /// validating any of them, all happen before this member is called.
    /// </para>
    /// <para>
    /// Staged, not written, and returning no identifier. The legacy member returned the generated key only
    /// because its procedure ended in <c>select SCOPE_IDENTITY()</c>; a member that promised the key here
    /// would have to commit on the caller's behalf, which would dissolve the transaction boundary and make
    /// it impossible to write a portal's first pages in the same transaction as the portal, its alias, its
    /// roles and its modules - the five tables the legacy portal creation touched in sequence with no
    /// transaction spanning them. The key is read from <see cref="Tab.TabId"/> after
    /// <see cref="IUnitOfWork"/> has committed.
    /// </para>
    /// </remarks>
    public Task AddAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        _context.Tabs.Add(tab);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: this is BOTH legacy <c>UpdateTab</c> overloads at once - the sixteen-argument form at
    /// DataProvider.vb:L111 and the nineteen-argument form at L112, which differed only by a
    /// <c>RefreshInterval</c>, <c>PageHeadText</c> and <c>IsSecure</c> tail and wrote the same row. The
    /// entity carries every one of those arguments, so they cannot be transposed by a caller and the
    /// overload pair does not survive into the target.
    /// </para>
    /// <para>
    /// This is also how a page is sent to the recycle bin and how it is restored, because the legacy
    /// procedure's <c>IsDeleted</c> argument was itself just another column write: set
    /// <see cref="Tab.IsDeleted"/> on the page and stage it here. <see cref="DeleteAsync"/> is the
    /// different and permanent operation.
    /// </para>
    /// <para>
    /// A detached page is attached and marked modified in full, which reproduces the legacy procedure's
    /// own behaviour of writing every column it was handed. Since reads are detached this is the ordinary
    /// path; a page that some other operation has already tracked is left to the change tracker, whose
    /// pending modifications this call must not widen.
    /// </para>
    /// </remarks>
    public Task UpdateAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Entry(tab).State is EntityState.Detached)
        {
            _context.Tabs.Update(tab);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>UpdateTabOrder</c>, whose five positional arguments are all properties of the page and
    /// whose terminal procedure wrote exactly FOUR columns - <c>set TabOrder = @TabOrder, [Level] =
    /// @Level, ParentId = @ParentId, TabPath = @TabPath where TabId = @TabId</c>
    /// (04.05.00.SqlDataProvider lines 1815-1828). That narrowness is the whole reason this member is kept
    /// separate from <see cref="UpdateAsync"/>: renumbering a tree touches many pages, and it must not
    /// carry each one's unrelated edits along with it.
    /// </para>
    /// <para>
    /// The narrowness is enforced rather than merely documented. A detached page is attached and only
    /// those four properties are marked modified, so the emitted statement sets exactly the four columns
    /// the legacy procedure set no matter what else the supplied instance happens to carry. Because reads
    /// are detached this is the ordinary path. A page the change tracker already holds is left alone,
    /// since re-marking properties on a tracked entity could only widen what is written.
    /// </para>
    /// <para>
    /// MIGRATION: this is the persistence primitive that the legacy reordering path eventually reached,
    /// and the path itself is not surfaced. <c>UpdatePortalTabOrder</c> (TabController.vb:L550) took seven
    /// parameters with an optional tail and drove the private <c>MoveTab</c> helper (L243) across an
    /// in-memory list of the portal's pages before persisting anything, so it is multi-step orchestration
    /// rather than a persistence operation. Deciding which pages move, to what depth and in what order is
    /// the application layer's work; this member only records the outcome for one page.
    /// </para>
    /// </remarks>
    public Task UpdateOrderAsync(Tab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<Tab> entry = _context.Entry(tab);

        if (entry.State is EntityState.Detached)
        {
            entry = _context.Tabs.Attach(tab);

            entry.Property(candidate => candidate.TabOrder).IsModified = true;
            entry.Property(candidate => candidate.Level).IsModified = true;
            entry.Property(candidate => candidate.ParentId).IsModified = true;
            entry.Property(candidate => candidate.TabPath).IsModified = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Ports <c>DeleteTab</c>, which was a literal <c>delete from Tabs where TabId = @TabId</c>
    /// (02.00.00.SqlDataProvider lines 1446-1455). It is therefore a HARD delete and is not the recycle
    /// bin: sending a page to the recycle bin is a change to <see cref="Tab.IsDeleted"/> staged through
    /// <see cref="UpdateAsync"/>. This member is what permanently emptying the recycle bin reaches.
    /// </para>
    /// <para>
    /// The page is addressed by key so that a caller need not read one in order to remove it, and 0 is a
    /// real key. Absence is not an error: passing an identifier no row bears stages nothing, so a caller
    /// that has already established absence need not distinguish the two cases. Deciding that removal is
    /// permitted - that the page has no children left, that it is not the portal's home, splash, login,
    /// user or administration page, and that the caller may act on it - happens before this member is
    /// called, and removing what depended on the page is likewise the caller's affair: legacy deletion ran
    /// alongside separate cleanup of the page's module placements and permissions, and this member neither
    /// performs nor implies that cleanup, nor any tree reordering, nor any cache eviction.
    /// </para>
    /// <para>
    /// This resolution is the one TRACKED read in the class, and deliberately so. The instance must be the
    /// one the change tracker already holds if the caller has touched this page earlier in the same unit of
    /// work, because a second, detached instance bearing the same key could not be attached alongside it.
    /// When the caller does already hold it, this resolves from the tracker without a round trip.
    /// </para>
    /// </remarks>
    public async Task DeleteAsync(int tabId, CancellationToken cancellationToken = default)
    {
        Tab? tab = await _context.Tabs
            .FirstOrDefaultAsync(candidate => candidate.TabId == tabId, cancellationToken)
            .ConfigureAwait(false);

        if (tab is null)
        {
            return;
        }

        _context.Tabs.Remove(tab);
    }
}

