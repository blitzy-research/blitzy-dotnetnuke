using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
// MIGRATION: ALL SIX PERSISTED FREQUENCY CODES ARE LOAD-BEARING AND ARE PRESERVED AS THEY ARE. The
// legacy Select Case at Library/Components/Security/Roles/RoleController.vb:L540-L547 branches on
// the literal characters N, O, D, W, M and Y, and those characters are the bytes sitting in the two
// char(1) role columns of every existing database - the installer seeds them as Lists rows under
// ListName 'Frequency' and the terminal role-listing procedures join on them to resolve display
// text. They are therefore reached here only through BillingFrequency, whose members carry the code
// points as their values. Nothing below renames a code, re-letters one, reorders the members or
// persists an ordinal, and the switch handles all six explicitly rather than folding any of them
// into a default arm.
//
// MIGRATION: THE TWO SENTINEL EXPIRY VALUES ARE TREATED DIFFERENTLY, DELIBERATELY. Code O assigned
// the literal New System.DateTime(9999, 12, 31) (RoleController.vb:L542), a real and externally
// observable instant that a caller can read and compare, so it is reproduced exactly and is not
// turned into a null. Code N and an absent period assigned Null.NullDate - Date.MinValue, per
// Library/Components/Shared/Null.vb:L66-L70 - which was the legacy way of writing "no expiry" and
// could never reach the column, because SQL Server datetime begins at 1753-01-01 and the legacy
// write path converted the marker to DBNull on every call. Under Rule T7 that one becomes a null
// expiry, and the marker itself is converted here on the write path exactly as Null.GetNull did.
//
// MIGRATION: THE AMBIENT SERVER-LOCAL CLOCK BECOMES AN INJECTED UTC CLOCK. The legacy engine read
// Now four separate times and Date.Today once within a single operation (RoleController.vb:L505,
// L530, L533, L534 and L496). Two substitutions follow and both are recorded rather than absorbed.
// First, the readings are collapsed into ONE capture per operation, because four readings of a
// moving clock can disagree with each other and a request crossing a tick could clear an effective
// date against one instant and seed an expiry from another. Second, Now and Date.Today were
// server-LOCAL whereas IClock is UTC-ONLY, so a date-only value derived here CAN NAME A DIFFERENT
// CALENDAR DAY from the one a legacy installation would have produced for the same real instant -
// a day earlier west of Greenwich, a day later east of it. That is accepted deliberately: a
// local-zone stamp is not comparable between hosts and cannot be read without knowing the machine
// that wrote it, and the injected clock is also what makes this file testable at a chosen moment.
// No line below reads DateTime.Now, DateTime.Today or DateTime.UtcNow.
//
// MIGRATION: A CANCELLED PAID TRIAL IS EXPIRED, NOT DELETED. RoleController.vb:L494-L496 refuses to
// remove an assignment when the role carries a service fee and the trial has already been consumed;
// it back-dates the expiry by one day instead, so the consumed-trial fact survives and a cancelled
// subscriber cannot restart a trial by re-subscribing. That rule is reproduced on the removal path
// below. See the note on DeleteUserRoleAsync for how it composes with the Application service that
// applies the same rule before calling in, and with the account-deletion cascade.
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
/// divergence, is documented on <see cref="IRoleRepository"/>; this type adds only the query detail
/// and the write-path normalisation.
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
/// <b>The two assignment writes normalise what they are given, and they are deliberately not
/// symmetrical.</b> Both convert the legacy absent-date marker to a null and clear an effective date
/// that has already passed. Beyond that they follow the two legacy members they stand in for, which
/// differed: <c>AddUserRole</c> (L294-L313) stored the caller's dates verbatim and derived nothing,
/// while <c>UpdateUserRole(..., Cancel:=False)</c> (L508-L554) was the subscription operation and
/// derived the term. So <see cref="AddUserRoleAsync"/> stages what it is handed, and
/// <see cref="UpdateUserRoleAsync"/> derives an expiry from the role's own billing or trial terms
/// only when the caller left the expiry unset. An expiry the caller did state is persisted verbatim
/// on both paths, which keeps the documented expire-rather-than-delete cancellation reachable
/// through <see cref="UpdateUserRoleAsync"/> and stops this layer from overruling the one that
/// computed the value - the Application service still owns the renewal engine, and the legacy
/// provider's own write members applied nothing but the <c>Null.GetNull</c> conversion (membership
/// <c>DataProvider/SqlDataProvider.vb</c> L280-L286).
/// </para>
/// <para>
/// No member commits, evaluates a permission, classifies an assignment - callers use
/// <see cref="UserRole.GetStatus(DateTime)"/> - caches a result, sends a notification or validates a
/// request.
/// </para>
/// </remarks>
internal sealed class RoleRepository : IRoleRepository
{
    /// <summary>
    /// Days in a billing week, applied as a day offset rather than any week interval.
    /// </summary>
    /// <remarks>
    /// The legacy week case multiplied by seven and added DAYS -
    /// <c>DateAdd(DateInterval.Day, (Period * 7), ...)</c> at
    /// <c>RoleController.vb:L543</c> - so the migrated arithmetic multiplies and adds days too. Using
    /// a week interval would agree on every input and would still be the wrong translation to leave
    /// behind for the next reader.
    /// </remarks>
    private const int DaysPerWeek = 7;

    /// <summary>Months in a billing year, applied as a month offset.</summary>
    /// <remarks>
    /// <c>DateAdd(DateInterval.Year, Period, ...)</c> at <c>RoleController.vb:L545</c> is expressed
    /// as a month offset so that both calendar cases clamp through one helper and so that the
    /// framework's own leap-day handling - 29 February onto a non-leap year - is preserved.
    /// </remarks>
    private const int MonthsPerYear = 12;

    /// <summary>
    /// The perpetual-access expiry that a one-time fee assigns, reproduced exactly.
    /// </summary>
    /// <remarks>
    /// <c>New System.DateTime(9999, 12, 31)</c> at <c>RoleController.vb:L542</c>. The value is a real
    /// instant rather than a marker for absence, so it survives the migration unchanged; it doubles
    /// as the upper clamp for every offset below, because it is also the last day SQL Server
    /// <c>datetime</c> can store.
    /// </remarks>
    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The earliest instant SQL Server <c>datetime</c> can store.</summary>
    /// <remarks>
    /// The lower clamp for a negative offset. It is declared here rather than imported so that this
    /// repository depends on nothing beyond its own contract surface; the value is the same
    /// 1753-01-01 boundary the Domain records, and the two must stay in agreement.
    /// </remarks>
    private static readonly DateTime MinimumStorableDateTime = new(1753, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly DnnDbContext _context;
    private readonly IClock _clock;

    /// <summary>Initialises a new instance of the <see cref="RoleRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <param name="clock">
    /// The solution's only sanctioned source of the present instant. The abstraction is injected
    /// rather than a concrete implementation constructed, so a test fixes the instant that the
    /// subscription and cancellation paths derive their dates from.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="clock"/> is <see langword="null"/>.
    /// </exception>
    public RoleRepository(DnnDbContext context, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);

        _context = context;
        _clock = clock;
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
    public Task UpdateAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged explicitly rather than left to change detection alone, so that the call behaves
        // identically whether the caller mutated a tracked entity or rebuilt a detached one.
        _context.Roles.Update(role);
        return Task.CompletedTask;
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
    public Task UpdateRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        cancellationToken.ThrowIfCancellationRequested();

        _context.RoleGroups.Update(roleGroup);
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
    /// The four values the legacy <c>AddUserRole(PortalID, UserId, RoleId, EffectiveDate,
    /// ExpiryDate)</c> carried positionally are carried by the entity, and the portal argument is
    /// dropped because <c>dbo.UserRoles</c> has no portal column - it served validation only, which
    /// the Application service performs before staging. Both bounds pass through
    /// <see cref="NormaliseSubscriptionBoundsAsync"/> first, so the legacy absent-date marker becomes
    /// a null and an effective date already in the past is cleared.
    /// </para>
    /// <para>
    /// MIGRATION: NO TERM IS DERIVED ON THIS PATH, AND THAT IS THE LEGACY BEHAVIOUR RATHER THAN AN
    /// OMISSION. <c>RoleController.AddUserRole</c> (L294-L313) stored the two dates it was given
    /// VERBATIM, and the portal-creation sequence called it with the absent-date marker for both, so a
    /// stock membership was unbounded. The derivation lived exclusively in
    /// <c>UpdateUserRole(..., Cancel:=False)</c>, which is a subscription operation. Reproducing it
    /// here instead would be unsound as well as unfaithful, because a null expiry cannot distinguish
    /// "unbounded, as an enrolment means it" from "apply the role's term": both of this member's
    /// production callers pass a null expiry for exactly the first meaning - the portal-creation
    /// sequence enrolling a new administrator in the three stock roles, and the role service's
    /// auto-assignment sweep enrolling existing members in a newly created role - and the stock roles
    /// are built with a monthly billing frequency and a ZERO billing period, so a term applied here
    /// would offset the current instant by zero months and stamp every new administrator's assignment
    /// as expiring at the instant it was created. The tenant-administration policy reads that
    /// assignment through <see cref="UserRole.GetStatus(DateTime)"/> a moment later, so the tenant's
    /// own administrator would be refused. Only the caller knows which meaning a null carries, which
    /// is precisely why the legacy engine derived in the caller.
    /// </para>
    /// </remarks>
    public async Task AddUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userRole);

        await NormaliseSubscriptionBoundsAsync(userRole, deriveUnsetExpiry: false, cancellationToken)
            .ConfigureAwait(false);

        // Staged only; UserRoleID is IDENTITY(1, 1) and appears on the entity once the unit of work
        // commits.
        await _context.UserRoles.AddAsync(userRole, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: THE CHOSEN BRANCH, STATED EXPLICITLY, BECAUSE THIS IS THE ONE MEMBER WHERE THE TWO
    /// LEGACY BEHAVIOURS MEET. <c>RoleController.UpdateUserRole(..., Cancel:=False)</c> (L508-L554) was
    /// the subscription operation: it primed itself from the stored assignment, chose the trial or the
    /// billing term and DERIVED an expiry, having no date parameters to honour.
    /// <c>RoleController.AddUserRole</c> (L294-L313) honoured its caller's dates and derived nothing.
    /// One member cannot reproduce both, so the two are separated by whether there is anything to
    /// preserve.
    /// </para>
    /// <para>
    /// <b>An expiry the caller stated is persisted unchanged</b>, whether it lies in the future or in
    /// the past. That is what the coordinated contract requires:
    /// <see cref="IRoleRepository.DeleteUserRoleAsync"/> documents that a caller reproducing the legacy
    /// expire-rather-than-delete cancellation does so through THIS member by back-dating the expiry,
    /// and a repository that re-derived the value would silently undo exactly that.
    /// </para>
    /// <para>
    /// <b>An expiry the caller left unset is derived from the role's own terms</b>, which is the state
    /// the legacy renewal path itself began from - it seeded the expiry from the stored row and treated
    /// the absent-date marker as already past. <see cref="AddUserRoleAsync"/> deliberately does not do
    /// this, for the reason recorded there: an enrolment's null expiry means unbounded.
    /// </para>
    /// <para>
    /// The legacy member could rewrite only the two dates; passing the entity also lets the
    /// trial-used flag be persisted, which the cancellation path depends on.
    /// </para>
    /// </remarks>
    public async Task UpdateUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userRole);

        await NormaliseSubscriptionBoundsAsync(userRole, deriveUnsetExpiry: true, cancellationToken)
            .ConfigureAwait(false);

        _context.UserRoles.Update(userRole);
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
    /// MIGRATION: A CANCELLED PAID TRIAL IS EXPIRED RATHER THAN DELETED, AND THE RULE LIVES IN BOTH
    /// PLACES ON PURPOSE. <c>RoleController.vb:L494-L496</c> tests
    /// <c>userRole.ServiceFee &gt; 0.0 AndAlso userRole.IsTrialUsed</c> and, when it holds, assigns
    /// <c>DateAdd(DateInterval.Day, -1, Date.Today())</c> and updates instead of deleting, so that the
    /// consumed-trial fact survives and a cancelled subscriber cannot restart a trial by
    /// re-subscribing. Reproducing it here makes the guarantee a property of the row rather than of
    /// one caller's diligence: the Application service applies the same rule before it ever reaches
    /// this member - which is why the two never both fire, the service's remaining branch arriving
    /// here only when the test is false - and the two other callers, the account-deletion cascade and
    /// the failed-creation compensation, remove the owning account in the same unit of work, where
    /// <c>FK_UserRoles_Users ON DELETE CASCADE</c> discards the retained row regardless. The fee is
    /// read from the assignment's role because <c>dbo.UserRoles</c> has no fee column; the legacy test
    /// could write <c>userRole.ServiceFee</c> only because <c>UserRoleInfo</c> inherited
    /// <c>RoleInfo</c>, an inheritance Rule T8 removes.
    /// </para>
    /// <para>
    /// The date is <c>UtcNow.Date.AddDays(-1)</c>: date-only because the legacy value carried no time
    /// component either, and because the row must read as already expired for the whole of the current
    /// day rather than only after the current hour. The clock is the injected UTC one, so the
    /// calendar-day caveat recorded in this file's header applies.
    /// </para>
    /// </remarks>
    public async Task DeleteUserRoleAsync(int userId, int roleId, CancellationToken cancellationToken = default)
    {
        List<UserRole> assignments = await _context.UserRoles
            .Include(a => a.Role)
            .Where(a => a.UserId == userId && a.RoleId == roleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (assignments.Count == 0)
        {
            // A pair-matched delete that finds nothing affected no row in the legacy procedure and
            // reported nothing, so neither does this.
            return;
        }

        // One clock reading for the whole operation, so several rows of one pair cannot be expired
        // against instants that disagree.
        DateTime expiredOnTheDayBefore = _clock.UtcNow.Date.AddDays(-1);

        foreach (UserRole assignment in assignments)
        {
            Role? role = assignment.Role;

            bool retainForConsumedTrial = role?.ServiceFee is decimal serviceFee
                && serviceFee > 0m
                && assignment.IsTrialUsed == true;

            if (retainForConsumedTrial)
            {
                assignment.ExpiryDate = expiredOnTheDayBefore;
                _context.UserRoles.Update(assignment);
            }
            else
            {
                _context.UserRoles.Remove(assignment);
            }
        }
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

    // ---------------------------------------------------------------------------------------------
    // Write-path normalisation - RoleController.vb L508-L554 and membership
    // DataProvider/SqlDataProvider.vb L280-L286
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Brings an assignment's two validity bounds into the shape the store expects before the row is
    /// staged.
    /// </summary>
    /// <param name="userRole">The assignment being staged.</param>
    /// <param name="deriveUnsetExpiry">
    /// Whether an expiry the caller left unset should be derived from the role's billing or trial
    /// terms. Set by the calling member rather than by its caller, because the two legacy write
    /// members differed on exactly this point: an enrolment stored what it was given and a
    /// subscription renewal computed the term. See the remarks on <see cref="AddUserRoleAsync"/> and
    /// <see cref="UpdateUserRoleAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>A task that completes once both bounds have been settled.</returns>
    /// <remarks>
    /// <para>
    /// Three things happen here, in this order, and each one reproduces a specific legacy line.
    /// </para>
    /// <para>
    /// <b>One.</b> Both bounds pass through <see cref="ReadLegacyDateMarkerAsAbsent"/>, which is
    /// <c>Null.GetNull</c> at the same boundary and for the same reason: the legacy write path wrapped
    /// both values on every call (membership <c>DataProvider/SqlDataProvider.vb</c> L280-L286), and
    /// the marker is unstorable in a SQL Server <c>datetime</c> column anyway.
    /// </para>
    /// <para>
    /// <b>Two.</b> An effective date that has already passed is cleared, reproducing
    /// <c>If EffectiveDate &lt; Now Then EffectiveDate = Null.NullDate</c>
    /// (<c>RoleController.vb</c>:L530-L532). The comparison is strict, so an effective date falling
    /// exactly on the captured instant is left in place - it is in force, not past - which agrees with
    /// the inclusive lower bound the terminal <c>GetRolesByUser</c> predicate tests and with
    /// <see cref="UserRole.GetStatus(DateTime)"/>.
    /// </para>
    /// <para>
    /// <b>Three.</b> When the calling member asks for it AND the caller stated no expiry, one is
    /// derived from the role's own terms by <see cref="DeriveExpiryFromRoleTerms"/>. A stated expiry is
    /// left exactly as it was, for the reason recorded on <see cref="UpdateUserRoleAsync"/>.
    /// </para>
    /// <para>
    /// MIGRATION: the present instant is captured ONCE and every comparison and offset below runs
    /// against that single value, replacing the four separate ambient <c>Now</c> readings the legacy
    /// engine took within one operation. It is read from the injected UTC clock, never from the
    /// machine.
    /// </para>
    /// </remarks>
    private async Task NormaliseSubscriptionBoundsAsync(
        UserRole userRole,
        bool deriveUnsetExpiry,
        CancellationToken cancellationToken)
    {
        DateTime now = _clock.UtcNow;

        userRole.EffectiveDate = ReadLegacyDateMarkerAsAbsent(userRole.EffectiveDate);
        userRole.ExpiryDate = ReadLegacyDateMarkerAsAbsent(userRole.ExpiryDate);

        if (userRole.EffectiveDate is DateTime effectiveDate && effectiveDate < now)
        {
            userRole.EffectiveDate = null;
        }

        if (!deriveUnsetExpiry || userRole.ExpiryDate is not null)
        {
            return;
        }

        Role? role = await ResolveRoleTermsAsync(userRole, cancellationToken).ConfigureAwait(false);

        if (role is null)
        {
            // No terms are reachable, so the membership is unbounded - which is the same answer the
            // legacy absent-period branch stored, and the same answer a null expiry already carries.
            return;
        }

        userRole.ExpiryDate = DeriveExpiryFromRoleTerms(role, userRole.IsTrialUsed, userRole.ExpiryDate, now);
    }

    /// <summary>
    /// Obtains the role whose billing and trial terms govern an assignment's expiry.
    /// </summary>
    /// <param name="userRole">The assignment being staged.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should stop.</param>
    /// <returns>
    /// The role, or <see langword="null"/> when no role bearing the assignment's key is reachable -
    /// which is the ordinary state while a role and its first membership are being staged together in
    /// one unit of work.
    /// </returns>
    /// <remarks>
    /// The navigation is preferred when the caller has already set it, so attaching an assignment to a
    /// role object costs no query at all. Otherwise the key is resolved through the context's own
    /// key-lookup, which consults the change tracker before the store: that is what lets a role staged
    /// earlier in the same unit of work, and therefore not yet present in any table, still supply its
    /// terms. The navigation on the assignment is deliberately not populated as a side effect, because
    /// assigning it would drag the role into the graph that
    /// <see cref="UpdateUserRoleAsync"/> subsequently attaches.
    /// </remarks>
    private async Task<Role?> ResolveRoleTermsAsync(UserRole userRole, CancellationToken cancellationToken)
    {
        if (userRole.Role is Role attached)
        {
            return attached;
        }

        return await _context.Roles
            .FindAsync([userRole.RoleId], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Derives a membership expiry from a role's billing or trial terms.
    /// </summary>
    /// <param name="role">The role whose terms govern the membership.</param>
    /// <param name="isTrialUsed">
    /// Whether the membership has already consumed the role's trial period. The column is nullable, so
    /// "nothing recorded" is a third state; it is read as "not yet used" here, which is the state the
    /// legacy non-nullable flag collapsed to.
    /// </param>
    /// <param name="storedExpiry">
    /// The expiry already standing on the membership, which seeds the offset. A value in the past is
    /// advanced to the present instant so that the term runs forward from now.
    /// </param>
    /// <param name="now">The single instant captured for this operation.</param>
    /// <returns>
    /// The derived expiry, or <see langword="null"/> when the terms grant a membership that never
    /// lapses.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A faithful translation of <c>RoleController.vb</c> L515-L547, in its own order.
    /// </para>
    /// <para>
    /// <b>Term selection.</b> <c>If IsTrialUsed = False And role.TrialFrequency.ToString &lt;&gt; "N"</c>
    /// (L521) chose the trial period and frequency, and anything else chose the billing pair. So the
    /// trial governs only while it has not been consumed AND the role states a trial frequency that is
    /// not the no-billing code - which is the second, independent responsibility
    /// <see cref="BillingFrequency.None"/> carries.
    /// </para>
    /// <para>
    /// <b>The absence guard runs before the code is examined at all.</b>
    /// <c>If Period = Null.NullInteger Then ExpiryDate = Null.NullDate</c> (L537) short-circuited to
    /// "no expiry", and both period columns are <c>int NULL</c>, so absence is a null rather than a
    /// small number. An absent frequency is treated the same way: with no code there is no term to
    /// apply, and a membership with no term does not lapse.
    /// </para>
    /// <para>
    /// <b>All six codes are handled explicitly and there is no default label.</b> The legacy
    /// <c>Select Case</c> had no <c>Case Else</c>, so a stored character outside the six left the date
    /// exactly as the seeding above had set it; initialising the result from that seed reproduces the
    /// same outcome without inventing a fall-through branch that swallows the six real codes.
    /// </para>
    /// <para>
    /// <b>Every offset is clamped into the storable calendar.</b> The period is an unbounded stored
    /// integer and the four direct legacy calls each failed on a large one: the week case multiplied by
    /// seven in 32-bit arithmetic and could wrap to a negative day count, silently moving an expiry
    /// into the past, while the day, month and year cases raised an out-of-range fault. Widening the
    /// multiplication to 64 bits removes the wrap outright - no product of an <see cref="int"/> period
    /// and seven, or twelve, can overflow a <see cref="long"/> - and the two helpers below then clamp
    /// the result rather than letting it throw.
    /// </para>
    /// </remarks>
    private static DateTime? DeriveExpiryFromRoleTerms(
        Role role,
        bool? isTrialUsed,
        DateTime? storedExpiry,
        DateTime now)
    {
        bool trialGoverns = isTrialUsed != true
            && role.TrialFrequency is BillingFrequency trialFrequency
            && trialFrequency != BillingFrequency.None;

        int? period = trialGoverns ? role.TrialPeriod : role.BillingPeriod;
        BillingFrequency? frequency = trialGoverns ? role.TrialFrequency : role.BillingFrequency;

        if (period is not int units || frequency is not BillingFrequency code)
        {
            return null;
        }

        // If ExpiryDate < Now Then ExpiryDate = Now (RoleController.vb:L533-L535), with an absent
        // expiry taking the same branch because the legacy absent-date marker was the minimum date
        // value and therefore always in the past.
        DateTime offsetBase = storedExpiry is DateTime stored && stored > now ? stored : now;

        DateTime? expiry = offsetBase;

        switch (code)
        {
            case BillingFrequency.None:
                // 'N' - the role is not billed and the membership never lapses. The legacy line
                // assigned Null.NullDate; under Rule T7 that is a null expiry.
                expiry = null;
                break;

            case BillingFrequency.OneTime:
                // 'O' - a single fee buys perpetual access, so the far-future sentinel is assigned
                // outright and the period is not consulted.
                expiry = PerpetualExpiry;
                break;

            case BillingFrequency.Day:
                // 'D' - DateAdd(DateInterval.Day, Period, ...).
                expiry = AddDaysWithinStorableRange(offsetBase, units);
                break;

            case BillingFrequency.Week:
                // 'W' - DateAdd(DateInterval.Day, (Period * 7), ...). A day offset, never a week
                // interval, and multiplied in 64-bit arithmetic so it cannot wrap.
                expiry = AddDaysWithinStorableRange(offsetBase, (long)units * DaysPerWeek);
                break;

            case BillingFrequency.Month:
                // 'M' - DateAdd(DateInterval.Month, Period, ...), which clamps onto a shorter month.
                expiry = AddMonthsWithinStorableRange(offsetBase, units);
                break;

            case BillingFrequency.Year:
                // 'Y' - DateAdd(DateInterval.Year, Period, ...), expressed in months so that both
                // calendar cases clamp through one helper and 29 February keeps its legacy handling.
                expiry = AddMonthsWithinStorableRange(offsetBase, (long)units * MonthsPerYear);
                break;
        }

        return expiry;
    }

    /// <summary>
    /// Returns <see langword="null"/> when the supplied bound is the legacy absent-date marker, and
    /// the bound itself otherwise.
    /// </summary>
    /// <param name="bound">A membership bound as the caller supplied it.</param>
    /// <returns>The value to store.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: THIS IS <c>Null.GetNull</c>, AT THE SAME BOUNDARY AND FOR THE SAME REASON. The
    /// legacy absent-date marker is <c>Null.NullDate</c>, which is <c>Date.MinValue</c>
    /// (<c>Null.vb</c> L66-L70), and the legacy data-access layer converted it to <c>DBNull</c> on the
    /// way to the database - <c>Null.GetNull</c> (<c>Null.vb</c> L183-L186) substitutes <c>DBNull</c>
    /// when the DATE PART equals <c>NullDate.Date</c>, carrying the source comment "this avoids subtle
    /// time differences". Both members that wrote these two columns wrapped both values on every call:
    /// <c>AddUserRole(PortalId, UserId, RoleId, GetNull(EffectiveDate), GetNull(ExpiryDate))</c> and
    /// <c>UpdateUserRole(UserRoleId, GetNull(EffectiveDate), GetNull(ExpiryDate))</c>, membership
    /// <c>DataProvider/SqlDataProvider.vb</c> L280-L286. The two repository members that stand in for
    /// those two legacy members therefore do the same thing, which is what keeps the stored shape
    /// identical under Rule T5 and keeps the sentinel out of the Domain under Rule T7.
    /// </para>
    /// <para>
    /// The comparison is on the date part alone, matching <c>Null.GetNull</c> rather than improving on
    /// it. A caller that carried the marker forward from a legacy object may well have a non-zero time
    /// component attached to it, and an exact-equality test would let such a value through.
    /// </para>
    /// <para>
    /// This is not merely a compatibility nicety. <c>dbo.UserRoles.EffectiveDate</c> and
    /// <c>ExpiryDate</c> are both SQL Server <c>datetime</c>, whose range begins at 1753-01-01, so
    /// 0001-01-01 is unstorable and an attempt to write it is refused by the database outright. Absent
    /// this normalisation a caller passing the marker would receive a range failure from the store
    /// rather than the "no bound" the legacy application recorded.
    /// </para>
    /// <para>
    /// It is done here, in an explicit method on the write path, rather than as a mapping value
    /// converter. Entity Framework Core does not invoke a value converter for a null model value and a
    /// converter cannot introduce one, so expressing "this value becomes SQL NULL" as a converter would
    /// require a null-converting converter - a construct documented as unsupported for most uses. The
    /// entity configuration records that no conversion is attached and points here instead, so the two
    /// files cannot drift into disagreeing about where the normalisation lives.
    /// </para>
    /// </remarks>
    private static DateTime? ReadLegacyDateMarkerAsAbsent(DateTime? bound) =>
        bound is DateTime value && value.Date == DateTime.MinValue.Date ? null : bound;

    /// <summary>
    /// Offsets an instant by a number of days, clamped into the calendar the column can store.
    /// </summary>
    /// <param name="offsetBase">The instant the term runs from.</param>
    /// <param name="days">
    /// The offset in days, already widened to 64 bits so that a large weekly period cannot wrap.
    /// </param>
    /// <returns>The offset instant, or the nearer storable boundary when the offset overshoots it.</returns>
    /// <remarks>
    /// A period large enough to leave the storable calendar is a data defect rather than a request for
    /// an exception: the legacy code would have thrown, which surfaced as a server fault naming no
    /// field, so the boundary is returned and the membership reads as perpetual or as long expired
    /// accordingly. A negative period is clamped at the lower boundary for the same reason.
    /// </remarks>
    private static DateTime AddDaysWithinStorableRange(DateTime offsetBase, long days)
    {
        if (days >= 0)
        {
            long daysAvailable = (PerpetualExpiry - offsetBase).Days;
            return days > daysAvailable ? PerpetualExpiry : offsetBase.AddDays(days);
        }

        long daysBehind = (offsetBase - MinimumStorableDateTime).Days;
        return -days > daysBehind ? MinimumStorableDateTime : offsetBase.AddDays(days);
    }

    /// <summary>
    /// Offsets an instant by a number of months, clamped into the calendar the column can store.
    /// </summary>
    /// <param name="offsetBase">The instant the term runs from.</param>
    /// <param name="months">
    /// The offset in months, already widened to 64 bits so that a large yearly period cannot wrap.
    /// </param>
    /// <returns>The offset instant, or the nearer storable boundary when the offset overshoots it.</returns>
    /// <remarks>
    /// The comparison counts month ordinals from year one, which turns the range test into a single
    /// subtraction and avoids reasoning about calendar carries twice. Once the ordinal is proved to sit
    /// inside the storable calendar - which is narrower than the type's own - the framework call cannot
    /// fail and the narrowing cast is provably safe.
    /// </remarks>
    private static DateTime AddMonthsWithinStorableRange(DateTime offsetBase, long months)
    {
        long ordinalBase = ((long)offsetBase.Year * MonthsPerYear) + offsetBase.Month;
        long ordinalTarget = ordinalBase + months;

        if (ordinalTarget > ((long)PerpetualExpiry.Year * MonthsPerYear) + PerpetualExpiry.Month)
        {
            return PerpetualExpiry;
        }

        if (ordinalTarget
            < ((long)MinimumStorableDateTime.Year * MonthsPerYear) + MinimumStorableDateTime.Month)
        {
            return MinimumStorableDateTime;
        }

        return offsetBase.AddMonths((int)months);
    }
}
