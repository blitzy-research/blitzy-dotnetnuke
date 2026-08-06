using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

// =================================================================================================
// MIGRATION: THE CONTRACT COMES FROM THE MEMBERSHIP PROVIDER, NOT FROM THE CORE PROVIDER. The core
// abstract data surface - Library/Components/Providers/Data/DataProvider.vb, 269 MustOverride
// members - declares NO role member at all, and its concrete implementation
// Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb invokes no role procedure. The
// authoritative surface is the separate membership stack,
// Library/Providers/MembershipProviders/DataProvider/DataProvider.vb, whose three role regions carry
// exactly the twenty-one members realised below: eight roles at L91-L98, six role groups at
// L101-L106 and seven assignments at L109-L115. An implementation reconstructed from the core
// provider alone would be almost empty, so both stacks were read and the membership stack governs.
//
// MIGRATION: THE STRINGLY AND FLOATINGLY TYPED PROVIDER ARGUMENTS ARE GONE, AND THE SCHEMA DECIDED
// WHAT REPLACES THEM. The membership provider declared AddRole(..., ServiceFee As Single,
// BillingPeriod As String, BillingFrequency As String, TrialFee As Single, TrialPeriod As Integer,
// TrialFrequency As String, ...) at DataProvider.vb:L95 and UpdateRole likewise at L97 - a fourteen
// and a thirteen argument positional list mixing a floating-point fee with a string billing period.
// The terminal schema disagrees with both: ServiceFee and TrialFee are money, BillingPeriod and
// TrialPeriod are int NULL, and BillingFrequency and TrialFrequency are char(1) NULL. Under Rule T4
// the store wins, so this file works exclusively in the entity's decimal? fees and int? periods.
// No float, no double, no VB Single and no string period appears anywhere below.
//
// MIGRATION: ALL SIX PERSISTED FREQUENCY CODES ARE LOAD-BEARING AND ARE PRESERVED AS THEY ARE, AND
// NOTHING HERE INTERPRETS ONE. The legacy Select Case at
// Library/Components/Security/Roles/RoleController.vb:L540-L547 branches on the literal characters N,
// O, D, W, M and Y, and those characters are the bytes sitting in the two char(1) role columns of
// every existing database - the installer seeds them as Lists rows under ListName 'Frequency' and the
// terminal role-listing procedures join on them to resolve display text. They reach the store through
// the BillingFrequency enum, whose members carry the code points as their values, and this file
// neither renames a code, re-letters one, reorders the members nor persists an ordinal. The six-way
// branch that ACTS on a code is a subscription rule and lives in RoleService; no code is examined
// below this line.
//
// MIGRATION: THE TWO SENTINEL EXPIRY VALUES ARE TREATED DIFFERENTLY, DELIBERATELY - AND THE DECISION
// IS MADE ABOVE THIS LAYER. Code O assigned the literal New System.DateTime(9999, 12, 31)
// (RoleController.vb:L542), a real and externally observable instant that a caller can read and
// compare, so it survives the migration exactly and is not turned into a null. Code N and an absent
// period assigned Null.NullDate - Date.MinValue, per Library/Components/Shared/Null.vb:L66-L70 -
// which was the legacy way of writing "no expiry" and could never reach the column, because SQL
// Server datetime begins at 1753-01-01. Under Rule T7 that one becomes a null expiry. BOTH
// translations belong to RoleService, which is the single owner of the subscription rules; this file
// stages whichever value it is handed and asserts nothing about which of the two it is.
//
// MIGRATION: THE SUBSCRIPTION ENGINE IS NOT HERE, AND ITS ABSENCE IS THE POINT. An earlier revision
// of this file carried its own copy of the legacy billing engine - a captured clock reading, the
// absent-date-marker translation, the past-effective-date reset, the trial-versus-billing term
// selection, the six-code expiry switch with its calendar clamps, and the expire-rather-than-delete
// cancellation - while RoleService carried the same rules for the same reasons. Two copies of one
// business rule in two layers is worse than either copy alone: they agree until one is amended, and
// then they disagree on exactly the case that prompted the amendment. Under Rule T2 the Application
// layer owns business logic, so the rules now live once, in RoleService (DeriveAssignmentDates,
// NormalizeLegacyDateMarker and the removal path's expire-rather-than-delete branch), and this file
// keeps no clock at all: no member below reads DateTime.Now, DateTime.Today or DateTime.UtcNow, and
// none is injected. Every value in a UserRole reaching a write member here is already FINAL.
//
// MIGRATION: A CANCELLED PAID TRIAL IS EXPIRED, NOT DELETED - DECIDED BY RoleService, NOT HERE.
// RoleController.vb:L494-L496 refuses to remove an assignment when the role carries a service fee and
// the trial has already been consumed; it back-dates the expiry by one day instead, so the
// consumed-trial fact survives and a cancelled subscriber cannot restart a trial by re-subscribing.
// RoleService.RemoveUserFromRoleAsync evaluates that test and takes the back-dating branch itself,
// reaching DeleteUserRoleAsync only when the test is false - so the removal member below is an
// unconditional delete, as the legacy provider member it stands in for was. See the note on
// DeleteUserRoleAsync.
//
// MIGRATION: POSITIONAL PROCEDURE CALLS, SCOPE_IDENTITY AND READERS ALL DISAPPEAR. Every legacy read
// returned a forward-only IDataReader that the caller turned into objects either by hand, one
// Null.SetNull assignment per column, or through the 729-line reflection binder in
// Library/Components/Shared/CBO.vb; every legacy insert ended in SCOPE_IDENTITY() and returned the
// generated key. Both mechanisms are deleted rather than translated. The persistence layer's own
// materialiser yields typed entities, so no reader, no row binder, no pre-generics collection
// wrapper and no sentinel-translating helper survives; and no member here commits, so no member can
// return a generated key. Each write stages an intention and the transaction is closed exactly once
// by IUnitOfWork.SaveChangesAsync, which is what lets one commit span the several tables a portal
// creation writes.
//
// MIGRATION: THE ORCHESTRATION THAT SURROUNDED THE LEGACY DATA CALLS IS DELIBERATELY ABSENT. The
// legacy role controller also cached every read through DataCache, synchronised roles into the
// configured role provider (the SynchronizationMode arguments at RoleController.vb:L849 and L854),
// sent membership notifications, refused to remove designated administrator and registered-member
// assignments, and read the ambient per-request portal composite. None of that is data access, so
// none of it is here: caching is an injected ICacheService, notification and audit are Application
// concerns, the protected-assignment rule and the tenant checks are enforced by the Application
// service above this layer, and the provider-synchronisation model is replaced rather than
// reproduced. This file reads and writes rows.
// =================================================================================================

/// <summary>
/// Reads and writes <see cref="Role"/> permission groupings, their <see cref="RoleGroup"/> containers
/// and the <see cref="UserRole"/> assignments that connect them to accounts.
/// </summary>
/// <remarks>
/// <para>
/// Every member realises one declaration from
/// <c>Library/Providers/MembershipProviders/DataProvider/DataProvider.vb</c> L91-L115, and the
/// members appear in that provider's own order - roles, then groups, then assignments - so the two
/// can be read side by side. The contract itself, together with the reasoning behind each
/// divergence, is documented on <see cref="IRoleRepository"/>; this type adds only the query detail.
/// </para>
/// <para>
/// A role belongs to a portal through a nullable <c>Roles.PortalID</c>, and an installation-wide
/// role carries null there. That matters because <c>PortalID</c> is <c>IDENTITY(-1, 1)</c>, so minus
/// one is a real portal and could never have served as an "unscoped" marker. The two portal-scoped
/// role reads treat installation-wide roles differently, deliberately, and the difference is drawn
/// from the terminal procedures - see the migration note on <see cref="GetByPortalIdAsync"/>.
/// Identity zero is likewise a real key for both a role and a role group, so no member here tests an
/// identifier against a reserved value to decide whether it was supplied.
/// </para>
/// <para>
/// <b>No read member applies <c>AsNoTracking</c>, and that is a correctness decision rather than an
/// oversight.</b> It is the rule the whole repository layer follows - stated on
/// <see cref="PortalRepository"/> and honoured by every sibling - because the Application layer
/// obtains an entity from a read member, mutates it and commits through <see cref="IUnitOfWork"/>
/// without necessarily calling an update member: <c>RoleService.UpdateAsync</c> projects a request
/// onto the role returned by <see cref="GetByIdAsync"/>, <c>RoleService.AssignUserToRoleAsync</c>
/// revises the assignment returned by <see cref="GetUserRoleAsync"/>, and
/// <c>RoleService.UpdateRoleGroupAsync</c> revises the group returned by
/// <see cref="GetRoleGroupAsync"/>, each committing through the unit of work alone. Detaching those
/// reads to save change-tracker work would silently discard every one of those mutations. Tracking
/// also gives the context a single instance per key, so a graph assembled from several reads cannot
/// raise a duplicate-tracking failure on the way to one commit.
/// </para>
/// <para>
/// <b>The three assignment writes stage exactly what they are handed and decide nothing.</b> Both
/// bounds are persisted as supplied, the trial-used flag is persisted as supplied, and the removal is
/// an unconditional delete. That is what the legacy provider's own write members did - membership
/// <c>DataProvider/SqlDataProvider.vb</c> L280-L286 applied nothing but the <c>Null.GetNull</c>
/// conversion, and <c>DeleteUserRole(UserId, RoleId)</c> deleted - and it is what Rule T2 requires:
/// the marker translation, the past-effective-date reset, the term derivation and the
/// expire-rather-than-delete cancellation are subscription rules, and <c>RoleService</c> is their one
/// owner. A caller therefore reads a repository member here as a statement about rows, never as a
/// statement about billing.
/// </para>
/// <para>
/// No member commits, evaluates a permission, classifies an assignment - callers use
/// <see cref="UserRole.GetStatus(DateTime)"/> - reads a clock, derives a date, caches a result, sends
/// a notification or validates a request.
/// </para>
/// </remarks>
internal sealed class RoleRepository : IRoleRepository
{
    private readonly DnnDbContext _context;

    /// <summary>
    /// The role property a role listing orders by when the caller names none.
    /// </summary>
    /// <remarks>
    /// The role name, which is the order the legacy administration grid presented and the order the
    /// unpaged reads on this repository already use.
    /// </remarks>
    private const string DefaultRoleSortProperty = "RoleName";

    /// <summary>
    /// The account property a role-membership listing orders by when the caller names none.
    /// </summary>
    /// <remarks>
    /// The display name, which is what the legacy membership grid rendered and led with.
    /// </remarks>
    private const string DefaultMembershipSortProperty = "DisplayName";

    /// <summary>Initialises a new instance of the <see cref="RoleRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// MIGRATION: no clock is injected, and that absence is deliberate. An earlier revision took an
    /// <c>IClock</c> in order to normalise subscription dates, derive expiries from billing terms and
    /// back-date a cancelled paid trial - all of which duplicated rules <c>RoleService</c> already
    /// owned. Rule T2 places those in the Application layer, so they were removed from here rather than
    /// left to drift out of step with the copy that decides them.
    /// </remarks>
    public RoleRepository(DnnDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    // ---------------------------------------------------------------------------------------------
    // Roles - membership DataProvider.vb L91-L98
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the installation-wide-role predicate is not an embellishment, it is what the
    /// terminal procedure wrote. <c>GetPortalRoles</c> at <c>04.08.00.SqlDataProvider</c> L18-L42
    /// filters on <c>( R.PortalId = @PortalId OR R.PortalId is null )</c> and orders by
    /// <c>R.RoleName</c>, so a role with no owning portal is visible to every portal. <c>GetRole</c>
    /// at <c>04.00.04.SqlDataProvider</c> L311-L336 filters on <c>PortalId = @PortalId</c> alone, a
    /// strict equality that excludes those roles because a null never equals an identifier. The two
    /// are reproduced as they were rather than made consistent with each other, because the
    /// difference is observable.
    /// </remarks>
    public async Task<IReadOnlyList<Role>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .Include(r => r.RoleGroup)
            .Where(r => r.PortalId == portalId || r.PortalId == null)
            .OrderBy(r => r.RoleName)
            .ThenBy(r => r.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Read-only, so the rows are not tracked: the one call site projects them and reads the total, and it
    /// mutates nothing.
    /// </para>
    /// <para>
    /// The tenant predicate is STRICT equality and admits no installation-wide role, which is what makes
    /// this member a different question from <see cref="GetByPortalIdAsync"/> rather than a paged version of
    /// it. The composition this replaces read that broader member and then reapplied the strict test in
    /// memory, so the narrowing is the same one - it has simply moved to the side of the boundary that can
    /// apply it before the rows are materialised.
    /// </para>
    /// <para>
    /// The name fragment is matched with a relational containment test over folded case, so the caller's
    /// text stays DATA - a per-cent or underscore in it matches literally instead of widening the search -
    /// and the answer does not depend on the collation the installation carries. The legacy comparison was
    /// also case-insensitive.
    /// </para>
    /// <para>
    /// MIGRATION: ordering moves into the statement, and the two arms where that could have been observable
    /// were checked rather than assumed. The billing and trial frequency arms order by an enumeration whose
    /// members are declared AS THE STORED CHARACTER CODES - <c>Day = 'D'</c>, <c>Week = 'W'</c>,
    /// <c>Month = 'M'</c>, <c>Year = 'Y'</c>, <c>None = 'N'</c>, <c>OneTime = 'O'</c> - so the enumeration's
    /// numeric order and the column's alphabetical order are the same order, and moving the sort into SQL
    /// cannot reorder them. The name and description arms move from an ordinal, case-insensitive comparer to
    /// the column's own collation, which agrees on case under the collation this schema is installed with and
    /// can differ on punctuation and accents; that is the same trade the tenant listing already makes, and it
    /// is the correct side of the boundary, because ordering here would require reading every row first.
    /// </para>
    /// </remarks>
    public async Task<PagedResult<Role>> ListAsync(
        int portalId,
        int? roleGroupId,
        bool ungroupedOnly,
        string? nameQuery,
        string? sortBy,
        bool descending,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Role> query = _context.Roles
            .AsNoTracking()
            .Where(role => role.PortalId == portalId);

        if (roleGroupId is int wantedGroup)
        {
            // RoleGroupID is IDENTITY(0, 1), so zero is a legitimate group key; the presence of a value
            // selects the filter, never its magnitude.
            query = query.Where(role => role.RoleGroupId == wantedGroup);
        }
        else if (ungroupedOnly)
        {
            // The legacy "< Global Roles >" selection. A test for the ABSENCE of a group, because
            // Roles.RoleGroupID is nullable and an ungrouped role stores SQL null there.
            query = query.Where(role => role.RoleGroupId == null);
        }

        if (!string.IsNullOrWhiteSpace(nameQuery))
        {
            string wanted = nameQuery.Trim().ToLowerInvariant();
            query = query.Where(role => role.RoleName.ToLower().Contains(wanted));
        }

        query = ApplyRoleOrder(query, sortBy, descending);

        // The group is loaded WITH THE WINDOW, so the projection's group name costs one join over the rows
        // being returned rather than over every role the tenant owns.
        IQueryable<Role> projection = query.Include(role => role.RoleGroup);

        if (pageSize == 0)
        {
            List<Role> all = await projection.ToListAsync(cancellationToken).ConfigureAwait(false);

            return PagedResult<Role>.Unpaged(all);
        }

        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<Role> rows = await projection
            .Skip(Paging.SkipCount(pageIndex, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<Role>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <summary>
    /// Applies the caller's chosen role ordering, terminating on the primary key.
    /// </summary>
    /// <param name="query">The filtered role query.</param>
    /// <param name="sortBy">The role property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the ordering runs downwards.</param>
    /// <returns>The ordered query.</returns>
    /// <remarks>
    /// Every arm terminates on <c>RoleID</c>, so the order is TOTAL and two roles sharing a sort value have a
    /// defined relative position - without which the same page coordinates can return different rows on two
    /// calls. An unrecognised name falls to the default rather than being refused here, because refusing a
    /// sort field is a request-validation decision and the layer that owns the paging request has already
    /// made it against this collection's own permitted set.
    /// </remarks>
    private static IQueryable<Role> ApplyRoleOrder(IQueryable<Role> query, string? sortBy, bool descending)
    {
        string property = string.IsNullOrWhiteSpace(sortBy) ? DefaultRoleSortProperty : sortBy.Trim();

        return property.ToUpperInvariant() switch
        {
            "ROLEID" => descending
                ? query.OrderByDescending(role => role.RoleId)
                : query.OrderBy(role => role.RoleId),
            "DESCRIPTION" => descending
                ? query.OrderByDescending(role => role.Description).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.Description).ThenBy(role => role.RoleId),
            "SERVICEFEE" => descending
                ? query.OrderByDescending(role => role.ServiceFee).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.ServiceFee).ThenBy(role => role.RoleId),
            "BILLINGFREQUENCY" => descending
                ? query.OrderByDescending(role => role.BillingFrequency).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.BillingFrequency).ThenBy(role => role.RoleId),
            "BILLINGPERIOD" => descending
                ? query.OrderByDescending(role => role.BillingPeriod).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.BillingPeriod).ThenBy(role => role.RoleId),
            "TRIALFEE" => descending
                ? query.OrderByDescending(role => role.TrialFee).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.TrialFee).ThenBy(role => role.RoleId),
            "TRIALFREQUENCY" => descending
                ? query.OrderByDescending(role => role.TrialFrequency).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.TrialFrequency).ThenBy(role => role.RoleId),
            "TRIALPERIOD" => descending
                ? query.OrderByDescending(role => role.TrialPeriod).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.TrialPeriod).ThenBy(role => role.RoleId),
            "ISPUBLIC" => descending
                ? query.OrderByDescending(role => role.IsPublic).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.IsPublic).ThenBy(role => role.RoleId),
            "AUTOASSIGNMENT" => descending
                ? query.OrderByDescending(role => role.AutoAssignment).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.AutoAssignment).ThenBy(role => role.RoleId),
            _ => descending
                ? query.OrderByDescending(role => role.RoleName).ThenByDescending(role => role.RoleId)
                : query.OrderBy(role => role.RoleName).ThenBy(role => role.RoleId),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // The legacy GetRoles took no argument and applied no filter, so neither does this. The
        // ordering is added because the legacy procedure's was unspecified, and an unstable sequence
        // between two calls is not a behaviour worth preserving.
        return await _context.Roles
            .Include(r => r.RoleGroup)
            .OrderBy(r => r.RoleName)
            .ThenBy(r => r.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Role?> GetByIdAsync(int roleId, int portalId, CancellationToken cancellationToken = default)
    {
        // Both keys are conditions, matching the terminal GetRole. RoleID is IDENTITY(0, 1): zero is
        // the first role an installation creates, so it can never be read as "no role". Absence is
        // reported as a null and is an ordinary answer.
        return _context.Roles
            .Include(r => r.RoleGroup)
            .FirstOrDefaultAsync(r => r.RoleId == roleId && r.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_RoleName</c> is unique over <c>(PortalID, RoleName)</c> and survives into the terminal
    /// schema, so at most one row can match and a non-null answer is also the answer to "is this name
    /// already taken within this portal". The comparison is case-insensitive on both sides, which is
    /// what the legacy collation gave the terminal procedure's plain equality and what a caller
    /// settling uniqueness needs; an empty name is a legally representable legacy value and is
    /// matched rather than read as "any name".
    /// </remarks>
    public Task<Role?> GetByNameAsync(int portalId, string roleName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleName);

        string wanted = roleName.Trim().ToLowerInvariant();

        return _context.Roles
            .Include(r => r.RoleGroup)
            .FirstOrDefaultAsync(r => r.PortalId == portalId && r.RoleName.ToLower() == wanted, cancellationToken);
    }

    /// <inheritdoc />
    public Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged only. The generated key appears on the entity after the unit of work commits, which
        // is what lets one commit span the several tables a portal creation writes - Roles among
        // them, because creating a portal creates its administrator and registered-member roles.
        _context.Roles.Add(role);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A role this repository handed out is already tracked, so its modifications are staged by the
    /// change tracker and this call is the caller's explicit statement of intent. A DETACHED instance -
    /// one rebuilt outside this context - is attached and marked modified so that the same call serves
    /// both origins. Nothing is written either way.
    /// </para>
    /// <para>
    /// MIGRATION: the detached branch ASSIGNS THE STATE and must never call <c>DbSet.Update</c>, for two
    /// independent reasons that both make <c>Update</c> unsafe on THIS entity.
    /// </para>
    /// <para>
    /// First, the key. <c>Update</c> chooses between <c>Added</c> and <c>Modified</c> by asking whether
    /// the key "is set", and it reads an <see cref="int"/> key of 0 as unset. <c>dbo.Roles.RoleID</c> is
    /// declared <c>IDENTITY(0, 1)</c> (<c>01.00.00.SqlDataProvider:L115</c>, re-declared by both rebuilds
    /// at <c>01.00.04:L1322</c> and <c>01.00.05:L2748</c>), so 0 is the ADMINISTRATORS role of every
    /// installation and the target of <c>Portals.AdministratorRoleId</c>. Calling <c>Update</c> on it
    /// staged an INSERT, duplicated the most privileged role in the installation under a new key and left
    /// the addressed row unchanged while reporting success.
    /// </para>
    /// <para>
    /// Second, the graph. <c>Update</c> traverses the navigation graph and marks everything it reaches,
    /// and every read on this repository <c>Include</c>s <see cref="Role.RoleGroup"/> - whose own key is
    /// likewise <c>IDENTITY(0, 1)</c>. So a detached role carrying group 0 duplicated the GROUP as well,
    /// and a tracked role carrying an already-tracked group failed outright. Assigning
    /// <see cref="EntityState.Modified"/> attaches this entity alone, marks its scalar properties
    /// modified and consults neither the key nor the graph.
    /// </para>
    /// <para>
    /// Leaving a TRACKED role to the change tracker rather than re-marking it is load-bearing too, and
    /// not merely tidier: it keeps the emitted statement to the columns the caller actually changed. That
    /// is what stops an unrelated edit - a new description, say - from rewriting
    /// <c>BillingFrequency</c> and <c>TrialFrequency</c>, whose <c>char(1)</c> columns a legacy
    /// installation may hold characters in that no vocabulary declares.
    /// </para>
    /// </remarks>
    public Task UpdateAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged explicitly rather than left to change detection alone, so that the call behaves
        // identically whether the caller mutated a tracked entity or rebuilt a detached one.
        //
        // MIGRATION: the state is ASSIGNED and DbSet.Update is deliberately not used. DbSet.Update
        // infers Added when a store-generated int key equals 0, and dbo.Roles.RoleID is declared
        // IDENTITY(0, 1) (01.00.00.SqlDataProvider:L115, DnnSchema.sql:260), so 0 is the real first role
        // of a tenant - by convention its Administrators role. Every read on this repository is tracked,
        // which is what kept DbSet.Update correct here in practice; assigning the state removes the
        // dependence on that, so a detached role rebuilt by a caller, a seeder or a test is UPDATED
        // rather than duplicated. Assignment also stages this entity ALONE: DbSet.Update walks the
        // navigation graph and applies the same key test to everything it reaches, which for a role
        // means the assignments and the group hanging off it. Same -1/0 sentinel collision as
        // dbo.Tabs, dbo.Portals, dbo.RoleGroups and dbo.Modules.
        SetModified(_context.Entry(role));
        return Task.CompletedTask;
    }

    /// <summary>Stages one entity as a full-row update without consulting its key.</summary>
    /// <param name="entry">The change-tracker entry for the entity being staged.</param>
    /// <remarks>
    /// Shared by the two write members whose tables are seeded at zero. A tracked entry is left to the
    /// change tracker, whose narrower set of pending modifications this must not widen; a detached entry
    /// is attached as modified in full. Assignment is what avoids <c>DbSet.Update</c>'s key inference,
    /// which treats a store-generated key of 0 as "unset" and stages an insert.
    /// </remarks>
    private static void SetModified(EntityEntry entry)
    {
        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Assignments are not removed here. <c>FK_UserRoles_Roles</c> is declared
    /// <c>ON DELETE CASCADE</c> in the schema this migration binds to, and the entity configuration
    /// declares the same behaviour, so the store discards a role's assignments with it. Deleting them
    /// row by row first would issue the same deletes twice.
    /// </remarks>
    public async Task DeleteAsync(int roleId, CancellationToken cancellationToken = default)
    {
        // Not portal-scoped, exactly as membership DataProvider.vb:L96 DeleteRole(RoleId) was not. A
        // caller confining a deletion to one tenant establishes ownership first through GetByIdAsync.
        Role? role = await _context.Roles
            .FirstOrDefaultAsync(r => r.RoleId == roleId, cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            // A key-matched delete that finds nothing affected no row in the legacy procedure and
            // reported nothing, so neither does this.
            return;
        }

        _context.Roles.Remove(role);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>UserRoles</c> carries no portal column, so the scope is applied THROUGH the role the
    /// assignment points at. Assignments to installation-wide roles therefore fall outside the
    /// answer, because such a role's <c>PortalID</c> is null and a null never equals a portal
    /// identifier. The result is role-shaped: the assignment rows themselves, with their validity
    /// bounds, are what <see cref="GetUserRolesAsync"/> returns, and the accounts holding a role
    /// belong to the account contract rather than to this one.
    /// </remarks>
    public async Task<IReadOnlyList<Role>> GetRolesByUserIdAsync(int userId, int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.UserRoles
            .Where(a => a.UserId == userId && a.Role!.PortalId == portalId)
            .Select(a => a.Role!)
            .Distinct()
            .OrderBy(r => r.RoleName)
            .ThenBy(r => r.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // Role groups - membership DataProvider.vb L101-L106
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task AddRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        cancellationToken.ThrowIfCancellationRequested();

        // The three positional arguments of AddRoleGroup(PortalId, GroupName, Description) are
        // carried by the entity, and the generated key - seeded at zero, so a portal's first group is
        // numbered zero - appears on it once the unit of work commits.
        _context.RoleGroups.Add(roleGroup);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the detached branch ASSIGNS THE STATE for the same reason
    /// <see cref="UpdateAsync(Role, CancellationToken)"/> does, and the collision is if anything sharper
    /// here. <c>DbSet.Update</c> reads an <see cref="int"/> key of 0 as unset, and
    /// <c>dbo.RoleGroups.RoleGroupID</c> is declared <c>IDENTITY(0, 1)</c>
    /// (<c>03.02.03.SqlDataProvider:L18</c>, re-declared at <c>04.00.04.SqlDataProvider:L51</c>), so a
    /// portal's FIRST group is numbered zero - and unlike a role, a group has no seeded name a caller
    /// could recognise a duplicate of. Renaming group 0 therefore created a second group and left the
    /// first one exactly as it was. Assigning <see cref="EntityState.Modified"/> attaches this entity
    /// alone and consults neither the key nor the roles that point at it.
    /// </remarks>
    public Task UpdateRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        cancellationToken.ThrowIfCancellationRequested();

        // MIGRATION: the state is ASSIGNED rather than inferred, for the reason recorded on
        // UpdateAsync above: dbo.RoleGroups.RoleGroupID is declared IDENTITY(0, 1)
        // (03.02.03.SqlDataProvider:L18, re-declared at 04.00.04.SqlDataProvider:L51, mirrored by the
        // fixture at DnnSchema.sql:135), so a tenant's first group is numbered zero and DbSet.Update
        // would read that key as "unset" and stage an insert for a detached instance.
        SetModified(_context.Entry(roleGroup));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>FK_Roles_RoleGroups</c> carries no cascade clause, unlike <c>FK_Roles_Portals</c>, so the
    /// store itself refuses to discard a group that a role still points at and the caller is expected
    /// to clear or reassign the group's roles first. That constraint belongs to the schema this
    /// migration binds to and is deliberately not worked around here; because nothing is written
    /// until the unit of work commits, the refusal surfaces at the commit rather than at this call.
    /// </remarks>
    public async Task DeleteRoleGroupAsync(int roleGroupId, CancellationToken cancellationToken = default)
    {
        // Not portal-scoped, exactly as membership DataProvider.vb:L102 was not. RoleGroupID is
        // IDENTITY(0, 1), so zero is a legitimate key rather than an unset one.
        RoleGroup? group = await _context.RoleGroups
            .FirstOrDefaultAsync(g => g.RoleGroupId == roleGroupId, cancellationToken)
            .ConfigureAwait(false);

        if (group is null)
        {
            return;
        }

        _context.RoleGroups.Remove(group);
    }

    /// <inheritdoc />
    public Task<RoleGroup?> GetRoleGroupAsync(int portalId, int roleGroupId, CancellationToken cancellationToken = default)
    {
        // RoleGroups.PortalID is NOT NULL, so every group belongs to exactly one portal and the
        // portal is a genuine condition rather than a hint: it is what stops one tenant reading
        // another tenant's group by guessing its key.
        return _context.RoleGroups
            .FirstOrDefaultAsync(g => g.RoleGroupId == roleGroupId && g.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleGroup>> GetRoleGroupsAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.RoleGroups
            .Where(g => g.PortalId == portalId)
            .OrderBy(g => g.RoleGroupName)
            .ThenBy(g => g.RoleGroupId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The portal is carried as well as the group because the legacy procedure carried it, and it
    /// remains a meaningful condition: it confines the answer to one tenant even when a group key
    /// belonging to another is supplied. Roles whose group is absent - the ordinary, ungrouped case -
    /// are returned by no group, and a caller wanting those uses <see cref="GetByPortalIdAsync"/>.
    /// </remarks>
    public async Task<IReadOnlyList<Role>> GetRolesByGroupAsync(int roleGroupId, int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .Include(r => r.RoleGroup)
            .Where(r => r.RoleGroupId == roleGroupId && r.PortalId == portalId)
            .OrderBy(r => r.RoleName)
            .ThenBy(r => r.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // UserRole assignments - membership DataProvider.vb L109-L115
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// The portal is applied through the assignment's role, since <c>dbo.UserRoles</c> has no portal
    /// column of its own. The role is materialised alongside the assignment because the caller that
    /// asks this question is almost always about to consult the role's terms, and because the removal
    /// path below reads the service fee from it.
    /// </remarks>
    public Task<UserRole?> GetUserRoleAsync(int portalId, int userId, int roleId, CancellationToken cancellationToken = default)
    {
        return _context.UserRoles
            .Include(a => a.Role)
            .FirstOrDefaultAsync(
                a => a.UserId == userId && a.RoleId == roleId && a.Role!.PortalId == portalId,
                cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>UserRoles</c> carries no portal column, so the scope is applied through the role the
    /// assignment points at, exactly as in <see cref="GetRolesByUserIdAsync"/>. This is the
    /// assignment-shaped counterpart of that member: the same memberships, but each row carrying its
    /// effective date, expiry date and trial-used flag.
    /// </remarks>
    public async Task<IReadOnlyList<UserRole>> GetUserRolesAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        return await _context.UserRoles
            .Include(a => a.Role)
            .Where(a => a.UserId == userId && a.Role!.PortalId == portalId)
            .OrderBy(a => a.RoleId)
            .ThenBy(a => a.UserRoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: A NULL NARROWS NOTHING, AN EMPTY STRING NARROWS TO ITSELF, AND THE TWO ARE NOT
    /// CONFLATED. The terminal procedure opens with <c>IF @UserName Is Null</c> and, in that branch,
    /// filters by <c>(R.Rolename = @Rolename or @RoleName is NULL)</c> alone, with the mirror-image
    /// test nested in the else-branch; the provider passed both names through <c>GetNull</c>
    /// (membership <c>DataProvider/SqlDataProvider.vb</c>:L277) and the legacy role-keyed direction
    /// was reached by supplying <c>Nothing</c> for the login name. Each test below is therefore a test
    /// for null and never for emptiness, because <c>Null.NullString</c> was the EMPTY STRING
    /// (<c>Null.vb</c> L71-L75) and an empty name is consequently a representable stored value that
    /// must keep narrowing to itself.
    /// </para>
    /// <para>
    /// Both names are matched case-insensitively, which is what the legacy collation gave the
    /// terminal procedure's plain equality. The account is materialised as well as the role because
    /// the terminal statement projects the account's display name beside the assignment columns, so a
    /// caller projecting these rows always needs it and would otherwise read once per row.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<UserRole>> GetUserRolesByUsernameAsync(
        int portalId,
        string? username,
        string? roleName,
        CancellationToken cancellationToken = default)
    {
        IQueryable<UserRole> query = _context.UserRoles
            .Include(a => a.Role)
            .Include(a => a.User)
            .Where(a => a.Role!.PortalId == portalId);

        if (username is not null)
        {
            string wantedUser = username.Trim().ToLowerInvariant();
            query = query.Where(a => a.User!.Username.ToLower() == wantedUser);
        }

        if (roleName is not null)
        {
            string wantedRole = roleName.Trim().ToLowerInvariant();
            query = query.Where(a => a.Role!.RoleName.ToLower() == wantedRole);
        }

        return await query
            .OrderBy(a => a.RoleId)
            .ThenBy(a => a.UserRoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Read-only, so the rows are not tracked. The role name is matched the same way the account-and-role
    /// read above matches it - folded case, exact equality - so the two members answer about the same role
    /// for the same argument.
    /// </para>
    /// <para>
    /// The account fragment is matched against BOTH the display name and the login name, with a relational
    /// containment test over folded case, so the caller's text stays data rather than becoming a pattern.
    /// </para>
    /// <para>
    /// MIGRATION: THREE OF THE ORDERING NAMES THIS COLLECTION ADMITS CANNOT BE ORDERED BY THE STORE, AND
    /// ORDERING BY THEM IN THIS PROCESS WAS ALREADY A NO-OP - so the two facts cancel and the observable
    /// order is unchanged. <c>CreatedDate</c>, <c>LastLoginDate</c> and <c>IsApproved</c> are
    /// <c>Ignore</c>d by <c>UserConfiguration</c>: they are not columns of <c>dbo.Users</c> in this model but
    /// values of the external <c>aspnet_Membership</c> store, and this read does not populate them. Every
    /// account it composes therefore carries the CLR default for all three, so the in-memory sort that
    /// preceded this one compared a constant across every row and the sequence it produced was decided
    /// entirely by the tie-break that followed it. Those three arms accordingly order by the tie-break alone,
    /// in the direction asked for, which reproduces that sequence exactly. They are left in the permitted set
    /// rather than withdrawn from it, because withdrawing them would turn requests the endpoint accepts today
    /// into refusals - a contract change no defect requires. Making them genuinely orderable would mean
    /// bringing the membership store into this join, which is a separate piece of work.
    /// </para>
    /// <para>
    /// The remaining arms order by mapped columns reached through the assignment's account. Ordering through
    /// a navigation renders as a join, and a null on the far side sorts first ascending - which is the same
    /// position the empty-account default the in-memory ordering substituted would have taken.
    /// </para>
    /// </remarks>
    public async Task<PagedResult<UserRole>> ListRoleMembershipsAsync(
        int portalId,
        string roleName,
        string? accountQuery,
        string? sortBy,
        bool descending,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleName);

        string wantedRole = roleName.Trim().ToLowerInvariant();

        IQueryable<UserRole> query = _context.UserRoles
            .AsNoTracking()
            .Where(a => a.Role!.PortalId == portalId && a.Role!.RoleName.ToLower() == wantedRole);

        if (!string.IsNullOrWhiteSpace(accountQuery))
        {
            string wantedAccount = accountQuery.Trim().ToLowerInvariant();
            query = query.Where(a =>
                a.User!.DisplayName.ToLower().Contains(wantedAccount)
                || a.User!.Username.ToLower().Contains(wantedAccount));
        }

        query = ApplyMembershipOrder(query, sortBy, descending);

        // The role and the account are loaded WITH THE WINDOW, so the three records that compose one row are
        // materialised for the rows being returned and for no others.
        IQueryable<UserRole> projection = query
            .Include(a => a.Role)
            .Include(a => a.User);

        if (pageSize == 0)
        {
            List<UserRole> all = await projection.ToListAsync(cancellationToken).ConfigureAwait(false);

            return PagedResult<UserRole>.Unpaged(all);
        }

        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<UserRole> rows = await projection
            .Skip(Paging.SkipCount(pageIndex, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<UserRole>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <summary>
    /// Applies the caller's chosen membership ordering, terminating on the assignment key.
    /// </summary>
    /// <param name="query">The filtered assignment query.</param>
    /// <param name="sortBy">The account property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the ordering runs downwards.</param>
    /// <returns>The ordered query.</returns>
    /// <remarks>
    /// Every arm terminates on <c>UserRoleID</c>, so the order is total. The three arms that name a value of
    /// the external membership store order by that tie-break alone; the remark on
    /// <see cref="ListRoleMembershipsAsync"/> sets out why that is exactly what the in-memory ordering they
    /// replace produced.
    /// </remarks>
    private static IQueryable<UserRole> ApplyMembershipOrder(
        IQueryable<UserRole> query,
        string? sortBy,
        bool descending)
    {
        string property = string.IsNullOrWhiteSpace(sortBy) ? DefaultMembershipSortProperty : sortBy.Trim();

        return property.ToUpperInvariant() switch
        {
            "USERID" => descending
                ? query.OrderByDescending(a => a.UserId).ThenByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.UserId).ThenBy(a => a.UserRoleId),
            "USERNAME" => descending
                ? query.OrderByDescending(a => a.User!.Username).ThenByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.User!.Username).ThenBy(a => a.UserRoleId),
            "FIRSTNAME" => descending
                ? query.OrderByDescending(a => a.User!.FirstName).ThenByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.User!.FirstName).ThenBy(a => a.UserRoleId),
            "LASTNAME" => descending
                ? query.OrderByDescending(a => a.User!.LastName).ThenByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.User!.LastName).ThenBy(a => a.UserRoleId),
            "EMAIL" => descending
                ? query.OrderByDescending(a => a.User!.Email).ThenByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.User!.Email).ThenBy(a => a.UserRoleId),
            "ISSUPERUSER" => descending
                ? query.OrderByDescending(a => a.User!.IsSuperUser).ThenByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.User!.IsSuperUser).ThenBy(a => a.UserRoleId),

            // The three membership-store values: every row carries the same CLR default for them, so the
            // tie-break alone is the whole ordering, exactly as it was in memory.
            "CREATEDDATE" or "LASTLOGINDATE" or "ISAPPROVED" => descending
                ? query.OrderByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.UserRoleId),

            _ => descending
                ? query.OrderByDescending(a => a.User!.DisplayName).ThenByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.User!.DisplayName).ThenBy(a => a.UserRoleId),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The four values the legacy <c>AddUserRole(PortalID, UserId, RoleId, EffectiveDate,
    /// ExpiryDate)</c> carried positionally are carried by the entity, and the portal argument is
    /// dropped because <c>dbo.UserRoles</c> has no portal column - it served validation only, which
    /// the Application service performs before staging.
    /// </para>
    /// <para>
    /// MIGRATION: NEITHER BOUND IS NORMALISED AND NO TERM IS DERIVED, WHICH IS BOTH THE LEGACY
    /// BEHAVIOUR AND THE LAYERING RULE. <c>RoleController.AddUserRole</c> (L294-L313) stored the two
    /// dates it was given VERBATIM and derived nothing; the derivation lived exclusively in
    /// <c>UpdateUserRole(..., Cancel:=False)</c>, a subscription operation. Deriving here would also be
    /// unsound, because a null expiry cannot distinguish "unbounded, as an enrolment means it" from
    /// "apply the role's term": every production caller passes a null expiry for the first meaning -
    /// the portal-creation sequence enrolling a new administrator in the three stock roles, and the
    /// role service's auto-assignment sweep enrolling existing members in a newly created role - and
    /// the stock roles are built with a monthly billing frequency and a ZERO billing period, so a term
    /// applied here would offset the present instant by zero months and stamp every new
    /// administrator's assignment as expiring at the instant it was created. The
    /// tenant-administration policy reads that assignment through
    /// <see cref="UserRole.GetStatus(DateTime)"/> a moment later, so the tenant's own administrator
    /// would be refused. Only the caller knows which meaning a null carries, which is precisely why
    /// the legacy engine derived in the caller and why <c>RoleService</c> owns it here.
    /// </para>
    /// <para>
    /// A CONSEQUENCE THE CALLER MUST HONOUR: both columns are SQL Server <c>datetime</c>, whose range
    /// begins at 1753-01-01, so the legacy absent-date marker <c>DateTime.MinValue</c> is not merely
    /// absent from them but unstorable. A caller that means "no bound" passes <see langword="null"/>;
    /// <c>RoleService.DeriveAssignmentDates</c> translates a submitted marker into that null before any
    /// value reaches here, and passing the marker anyway is refused loudly by the store rather than
    /// silently reinterpreted by this layer.
    /// </para>
    /// </remarks>
    public async Task AddUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userRole);

        // Staged only; UserRoleID is IDENTITY(1, 1) and appears on the entity once the unit of work
        // commits.
        await _context.UserRoles.AddAsync(userRole, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Every value on the assignment is persisted exactly as supplied</b>, both bounds and the
    /// trial-used flag, whether an expiry lies in the future or in the past. That is what the
    /// coordinated contract requires: <see cref="IRoleRepository.DeleteUserRoleAsync"/> documents that
    /// a caller reproducing the legacy expire-rather-than-delete cancellation does so by back-dating
    /// the expiry, and a repository that re-derived the value would silently undo exactly that.
    /// </para>
    /// <para>
    /// MIGRATION: NO TERM IS DERIVED HERE, THOUGH THE LEGACY MEMBER THIS STANDS IN FOR DERIVED ONE.
    /// <c>RoleController.UpdateUserRole(..., Cancel:=False)</c> (L508-L554) was the subscription
    /// operation: it primed itself from the stored assignment, chose the trial or the billing term and
    /// DERIVED an expiry, having no date parameters to honour. That engine is preserved in full - it is
    /// <c>RoleService.DeriveAssignmentDates</c>, which runs before the value reaches this layer - and it
    /// is preserved THERE rather than here because it is a business rule, and because a second copy in
    /// this layer would silently overrule the first. The legacy DATA member this file actually realises
    /// is membership <c>DataProvider/SqlDataProvider.vb</c> L280-L286
    /// <c>UpdateUserRole(UserRoleId, EffectiveDate, ExpiryDate)</c>, which applied no derivation of any
    /// kind.
    /// </para>
    /// <para>
    /// The legacy member could rewrite only the two dates; passing the entity also lets the
    /// trial-used flag be persisted, which the cancellation path depends on.
    /// </para>
    /// <para>
    /// MIGRATION: the staging ASSIGNS THE STATE and must not call <c>DbSet.Update</c>. <c>Update</c>
    /// walks the navigation graph and decides Added-versus-Modified for everything it reaches by asking
    /// whether the key "is set", reading an <see cref="int"/> key of 0 as unset. An assignment carries
    /// <see cref="UserRole.Role"/>, which every read here <c>Include</c>s and whose
    /// <c>dbo.Roles.RoleID</c> is <c>IDENTITY(0, 1)</c> (<c>01.00.00.SqlDataProvider:L115</c>), so a
    /// detached assignment to the administrators role would have DUPLICATED that role instead of merely
    /// recording the assignment's dates. <c>UserRoleID</c> is itself <c>IDENTITY(1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L239</c>), so the assignment row's own key is never at risk - the
    /// hazard is entirely in what the graph reaches. Assigning <see cref="EntityState.Modified"/>
    /// attaches this row alone.
    /// </para>
    /// </remarks>
    public Task UpdateUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userRole);

        EntityEntry<UserRole> entry = _context.Entry(userRole);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The row is identified by the account-and-role pair rather than by its own key, which is how
    /// <c>membership DataProvider.vb:L114 DeleteUserRole(UserId, RoleId)</c> identified it, and no
    /// portal argument narrows it - also as before. Every matching row is considered, so a store that
    /// somehow holds a duplicate pair is left consistent rather than half-cleared, and an assignment
    /// that is already gone is an idempotent no-op.
    /// </para>
    /// <para>
    /// MIGRATION: THE REMOVAL IS UNCONDITIONAL, AND THE EXPIRE-RATHER-THAN-DELETE RULE IS DECIDED BY
    /// THE CALLER. <c>RoleController.vb:L494-L496</c> tests
    /// <c>userRole.ServiceFee &gt; 0.0 AndAlso userRole.IsTrialUsed</c> and, when it holds, assigns
    /// <c>DateAdd(DateInterval.Day, -1, Date.Today())</c> and updates instead of deleting, so that the
    /// consumed-trial fact survives and a cancelled subscriber cannot restart a trial by
    /// re-subscribing. That test is a business rule about paid membership, so under Rule T2 it lives in
    /// <c>RoleService.RemoveUserFromRoleAsync</c>, which back-dates the expiry itself on the retaining
    /// branch and reaches this member only on the other one. An earlier revision evaluated the same test
    /// here as well, which meant one rule with two implementations free to drift apart; the fee is in
    /// any case a column on the ROLE rather than on the assignment - the legacy test could write
    /// <c>userRole.ServiceFee</c> only because <c>UserRoleInfo</c> inherited <c>RoleInfo</c>, an
    /// inheritance Rule T8 removes - so evaluating it here also required a join this member has no other
    /// reason to make.
    /// </para>
    /// <para>
    /// The rule is not weakened by living in one place. The two remaining callers - the
    /// account-deletion cascade and the failed-creation compensation - remove the owning account in the
    /// same unit of work, where <c>FK_UserRoles_Users ON DELETE CASCADE</c> discards a retained row
    /// regardless, so retaining one for them would have been a no-op even when it fired.
    /// </para>
    /// </remarks>
    public async Task DeleteUserRoleAsync(int userId, int roleId, CancellationToken cancellationToken = default)
    {
        List<UserRole> assignments = await _context.UserRoles
            .Where(a => a.UserId == userId && a.RoleId == roleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (assignments.Count == 0)
        {
            // A pair-matched delete that finds nothing affected no row in the legacy procedure and
            // reported nothing, so neither does this.
            return;
        }

        _context.UserRoles.RemoveRange(assignments);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal <c>GetServices</c> at <c>04.05.00.SqlDataProvider</c> L18-L37 selects
    /// <c>where R.PortalId = @PortalId and R.IsPublic = 1</c>, so the answer is the portal's
    /// subscribable roles under a strict portal equality that excludes installation-wide roles. The
    /// account argument did not widen or narrow that set: it fed two correlated subqueries that
    /// annotated each row with the caller's own expiry date and existing assignment key. Those
    /// annotations describe the account rather than the role, so they are composed above this layer
    /// from <see cref="GetUserRolesAsync"/> instead of being flattened into these rows, and no
    /// billing projection is invented here to give the argument something to do.
    /// </remarks>
    public async Task<IReadOnlyList<Role>> GetSubscribableRolesAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        // Deliberately unused: the contract states that the account identifies whose subscription
        // state the caller will pair with these roles, not which roles are returned.
        _ = userId;

        return await _context.Roles
            .Include(r => r.RoleGroup)
            .Where(r => r.PortalId == portalId && r.IsPublic)
            .OrderBy(r => r.RoleName)
            .ThenBy(r => r.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
