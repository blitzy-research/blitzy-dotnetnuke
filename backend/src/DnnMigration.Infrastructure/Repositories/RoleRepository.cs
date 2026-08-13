using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes <see cref="Role"/> permission groupings, their <see cref="RoleGroup"/> containers and
/// the <see cref="UserRole"/> assignments that connect them to accounts.
/// </summary>
/// <remarks>
/// <para>
/// Every member realises one declaration from
/// <c>Library/Providers/MembershipProviders/DataProvider/DataProvider.vb</c> L91-L115, and the members
/// appear in that provider's own order - roles, then groups, then assignments - so the two can be read side
/// by side.
/// </para>
/// <para>
/// A role belongs to a portal through a nullable <c>Roles.PortalID</c>, and an installation-wide role
/// carries null there. That matters because <c>PortalID</c> is <c>IDENTITY(-1, 1)</c>, so minus one is a
/// real portal and could never have served as an "unscoped" marker.
/// </para>
/// </remarks>
internal sealed class RoleRepository : IRoleRepository
{
    private readonly DnnDbContext _context;

    /// <summary>The role property a role listing orders by when the caller names none.</summary>
    /// <remarks>
    /// The role name, which is the order the legacy administration grid presented and the order the unpaged
    /// reads on this repository already use.
    /// </remarks>
    private const string DefaultRoleSortProperty = "RoleName";

    /// <summary>The account property a role-membership listing orders by when the caller names none.</summary>
    private const string DefaultMembershipSortProperty = "DisplayName";

    /// <summary>Initialises a new instance of the <see cref="RoleRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public RoleRepository(DnnDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <inheritdoc />
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
    /// The tenant predicate is STRICT equality and admits no installation-wide role, which is what makes
    /// this member a different question from <see cref="GetByPortalIdAsync"/> rather than a paged version
    /// of it.
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

    /// <summary>Applies the caller's chosen role ordering, terminating on the primary key.</summary>
    /// <param name="query">The filtered role query.</param>
    /// <param name="sortBy">The role property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the ordering runs downwards.</param>
    /// <returns>The ordered query.</returns>
    /// <remarks>
    /// Every arm terminates on <c>RoleID</c>, so the order is TOTAL and two roles sharing a sort value have
    /// a defined relative position - without which the same page coordinates can return different rows on
    /// two calls.
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
        // The legacy GetRoles took no argument and applied no filter, so neither does this. The ordering is
        // added because the legacy procedure's was unspecified, and an unstable sequence between two calls
        // is not a behaviour worth preserving.
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
        // Both keys are conditions, matching the terminal GetRole. RoleID is IDENTITY(0, 1): zero is the
        // first role an installation creates, so it can never be read as "no role".
        return _context.Roles
            .Include(r => r.RoleGroup)
            .FirstOrDefaultAsync(r => r.RoleId == roleId && r.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_RoleName</c> is unique over <c>(PortalID, RoleName)</c> and survives into the terminal schema,
    /// so at most one row can match and a non-null answer is also the answer to "is this name already taken
    /// within this portal".
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

        // Staged only. The generated key appears on the entity after the unit of work commits, which is
        // what lets one commit span the several tables a portal creation writes - Roles among them, because
        // creating a portal creates its administrator and registered-member roles.
        _context.Roles.Add(role);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// First, the key. <c>Update</c> chooses between <c>Added</c> and <c>Modified</c> by asking whether the
    /// key "is set", and it reads an <see cref="int"/> key of 0 as unset. <c>dbo.Roles.RoleID</c> is
    /// declared <c>IDENTITY(0, 1)</c> (<c>01.00.00.SqlDataProvider:L115</c>, re-declared by both rebuilds
    /// at <c>01.00.04:L1322</c> and <c>01.00.05:L2748</c>), so 0 is the ADMINISTRATORS role of every
    /// installation and the target of <c>Portals.AdministratorRoleId</c>.
    /// </remarks>
    public Task UpdateAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        cancellationToken.ThrowIfCancellationRequested();

        SetModified(_context.Entry(role));
        return Task.CompletedTask;
    }

    /// <summary>Stages one entity as a full-row update without consulting its key.</summary>
    /// <param name="entry">The change-tracker entry for the entity being staged.</param>
    /// <remarks>
    /// Shared by the two write members whose tables are seeded at zero. A tracked entry is left to the
    /// change tracker, whose narrower set of pending modifications this must not widen; a detached entry is
    /// attached as modified in full.
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
    /// Assignments are not removed here. <c>FK_UserRoles_Roles</c> is declared <c>ON DELETE CASCADE</c> in
    /// the schema this migration binds to, and the entity configuration declares the same behaviour, so the
    /// store discards a role's assignments with it. Deleting them row by row first would issue the same
    /// deletes twice.
    /// </remarks>
    public async Task DeleteAsync(int roleId, CancellationToken cancellationToken = default)
    {
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
    /// <c>UserRoles</c> carries no portal column, so the scope is applied THROUGH the role the assignment
    /// points at. Assignments to installation-wide roles therefore fall outside the answer, because such a
    /// role's <c>PortalID</c> is null and a null never equals a portal identifier.
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

    /// <inheritdoc />
    public Task AddRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        cancellationToken.ThrowIfCancellationRequested();

        // The three positional arguments of AddRoleGroup(PortalId, GroupName, Description) are carried by
        // the entity, and the generated key - seeded at zero, so a portal's first group is numbered zero -
        // appears on it once the unit of work commits.
        _context.RoleGroups.Add(roleGroup);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The detached branch ASSIGNS THE STATE for the same reason <see cref="UpdateAsync(Role,
    /// CancellationToken)"/> does, and the collision is if anything sharper here. <c>DbSet.Update</c> reads
    /// an <see cref="int"/> key of 0 as unset, and <c>dbo.RoleGroups.RoleGroupID</c> is declared
    /// <c>IDENTITY(0, 1)</c> (<c>03.02.03.SqlDataProvider:L18</c>, re-declared at
    /// <c>04.00.04.SqlDataProvider:L51</c>), so a portal's FIRST group is numbered zero - and unlike a
    /// role, a group has no seeded name a caller could recognise a duplicate of.
    /// </remarks>
    public Task UpdateRoleGroupAsync(RoleGroup roleGroup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        cancellationToken.ThrowIfCancellationRequested();

        // The state is ASSIGNED rather than inferred, for the reason recorded on UpdateAsync above:
        // dbo.RoleGroups.RoleGroupID is declared IDENTITY(0, 1) (03.02.03.SqlDataProvider:L18, re-declared
        // at 04.00.04.SqlDataProvider:L51, recorded in Schema/TerminalSchema.manifest), so a tenant's first
        // group is numbered zero and DbSet.Update would read that key as "unset" and stage an insert for a
        // detached instance.
        SetModified(_context.Entry(roleGroup));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>FK_Roles_RoleGroups</c> carries no cascade clause, unlike <c>FK_Roles_Portals</c>, so the store
    /// itself refuses to discard a group that a role still points at and the caller is expected to clear or
    /// reassign the group's roles first.
    /// </remarks>
    public async Task DeleteRoleGroupAsync(int roleGroupId, CancellationToken cancellationToken = default)
    {
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
        // RoleGroups.PortalID is NOT NULL, so every group belongs to exactly one portal and the portal is a
        // genuine condition rather than a hint: it is what stops one tenant reading another tenant's group
        // by guessing its key.
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
    /// The portal is carried as well as the group because the legacy procedure carried it, and it remains a
    /// meaningful condition: it confines the answer to one tenant even when a group key belonging to
    /// another is supplied. Roles whose group is absent - the ordinary, ungrouped case - are returned by no
    /// group, and a caller wanting those uses <see cref="GetByPortalIdAsync"/>.
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

    /// <inheritdoc />
    /// <remarks>
    /// The portal is applied through the assignment's role, since <c>dbo.UserRoles</c> has no portal column
    /// of its own. The role is materialised alongside the assignment because the caller that asks this
    /// question is almost always about to consult the role's terms, and because the removal path below
    /// reads the service fee from it.
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
    /// <c>UserRoles</c> carries no portal column, so the scope is applied through the role the assignment
    /// points at, exactly as in <see cref="GetRolesByUserIdAsync"/>. This is the assignment-shaped
    /// counterpart of that member: the same memberships, but each row carrying its effective date, expiry
    /// date and trial-used flag.
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
    /// THREE OF THE ORDERING NAMES THIS COLLECTION ADMITS CANNOT BE ORDERED BY THE STORE, AND ORDERING BY
    /// THEM IN THIS PROCESS WAS ALREADY A NO-OP - so the two facts cancel and the observable order is
    /// unchanged. <c>CreatedDate</c>, <c>LastLoginDate</c> and <c>IsApproved</c> are <c>Ignore</c>d by
    /// <c>UserConfiguration</c>: they are not columns of <c>dbo.Users</c> in this model but values of the
    /// external <c>aspnet_Membership</c> store, and this read does not populate them.
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

    /// <summary>Applies the caller's chosen membership ordering, terminating on the assignment key.</summary>
    /// <param name="query">The filtered assignment query.</param>
    /// <param name="sortBy">The account property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the ordering runs downwards.</param>
    /// <returns>The ordered query.</returns>
    /// <remarks>
    /// Every arm terminates on <c>UserRoleID</c>, so the order is total.
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

            _ => descending
                ? query.OrderByDescending(a => a.User!.DisplayName).ThenByDescending(a => a.UserRoleId)
                : query.OrderBy(a => a.User!.DisplayName).ThenBy(a => a.UserRoleId),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// The four values the legacy <c>AddUserRole(PortalID, UserId, RoleId, EffectiveDate, ExpiryDate)</c>
    /// carried positionally are carried by the entity, and the portal argument is dropped because
    /// <c>dbo.UserRoles</c> has no portal column - it served validation only, which the Application service
    /// performs before staging.
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
    /// <b>Every value on the assignment is persisted exactly as supplied</b>, both bounds and the
    /// trial-used flag, whether an expiry lies in the future or in the past.
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
