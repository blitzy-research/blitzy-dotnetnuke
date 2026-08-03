using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes <see cref="Role"/> permission groupings, their <see cref="RoleGroup"/> containers
/// and the <see cref="UserRole"/> assignments that connect them to accounts.
/// </summary>
/// <remarks>
/// MIGRATION: the legacy core data provider contains no role procedure at all - the role procedures
/// live under <c>Library/Providers/MembershipProviders/</c>, alongside the data-access half of
/// <c>Library/Components/Security/Roles/RoleController.vb</c>. Both were read to reconstruct this
/// contract; reading only the core provider would have produced an almost empty repository.
/// <para>
/// Every member here realises one declaration from
/// <c>Library/Providers/MembershipProviders/DataProvider/DataProvider.vb</c> L90-L115, and the
/// members appear in that provider's own order so the two can be read side by side. The contract
/// itself, together with the reasoning behind each divergence, is documented on
/// <see cref="IRoleRepository"/>; this type adds only the query detail.
/// </para>
/// <para>
/// A role belongs to a portal through a nullable <c>Roles.PortalID</c>, and a host-level role carries
/// null there. That matters because <c>PortalID</c> is <c>IDENTITY(-1, 1)</c>, so minus one is a real
/// portal and could not have been used as an "unscoped" marker. The two portal-scoped role reads
/// treat host roles differently, deliberately, and the difference is drawn from the terminal
/// procedures - see the migration note on <see cref="GetByPortalIdAsync"/>.
/// </para>
/// <para>
/// No read member applies <c>AsNoTracking</c>, for the reason given on <see cref="PortalRepository"/>.
/// No member calls <c>SaveChanges</c>: every write stages an intention and the transaction is closed
/// once by the unit of work.
/// </para>
/// </remarks>
internal sealed class RoleRepository : IRoleRepository
{
    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="RoleRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public RoleRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // ---------------------------------------------------------------------------------------------
    // Roles - membership DataProvider.vb L91-L98
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the host-role predicate is not an embellishment, it is what the terminal procedure
    /// wrote. <c>GetPortalRoles</c> at <c>04.08.00.SqlDataProvider</c> L18-L42 filters on
    /// <c>( R.PortalId = @PortalId OR R.PortalId is null )</c> and orders by <c>R.RoleName</c>, so an
    /// installation-wide role is visible to every portal. <c>GetRole</c> at
    /// <c>04.00.04.SqlDataProvider</c> L311-L336 filters on <c>PortalId = @PortalId</c> alone, a
    /// strict equality that excludes those roles because a null never equals an identifier. The two
    /// are therefore reproduced as they were rather than made consistent with each other.
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
        // ordering is added because the legacy procedure's was unspecified and an unstable sequence
        // between calls is not a behaviour worth preserving.
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
        // reported as null.
        return _context.Roles
            .Include(r => r.RoleGroup)
            .FirstOrDefaultAsync(r => r.RoleId == roleId && r.PortalId == portalId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_RoleName</c> is unique over <c>(PortalID, RoleName)</c> and survives into the terminal
    /// schema, so at most one row can match and a non-null answer is also the answer to "is this name
    /// already taken within this portal".
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
        // is what lets one commit span the several tables a portal creation writes.
        _context.Roles.Add(role);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        cancellationToken.ThrowIfCancellationRequested();

        // Update rather than relying on change detection alone, so that the call behaves identically
        // whether the caller mutated a tracked entity or rebuilt a detached one.
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
    /// <c>UserRoles</c> carries no portal column, so the scope is applied through the role the
    /// assignment points at. Assignments to host-level roles therefore fall outside the answer,
    /// because such a role's <c>PortalID</c> is null and never equals a portal identifier.
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
    /// <c>FK_Roles_RoleGroups</c> carries no cascade clause, so the store refuses to discard a group
    /// that a role still points at. That refusal surfaces when the unit of work commits, and it is
    /// deliberately not worked around here.
    /// </remarks>
    public async Task DeleteRoleGroupAsync(int roleGroupId, CancellationToken cancellationToken = default)
    {
        // RoleGroupID is IDENTITY(0, 1), so zero is a legitimate key.
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
        // portal is a genuine condition rather than a hint.
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
    /// assignment points at, exactly as in <see cref="GetRolesByUserIdAsync"/>.
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
    /// A null role name narrows nothing, which is what the legacy procedure's optional role argument
    /// meant. An empty role name is a legally representable legacy value and therefore narrows to it,
    /// so the two are not conflated. The login name is matched case-insensitively, as the legacy
    /// collation did.
    /// </remarks>
    public async Task<IReadOnlyList<UserRole>> GetUserRolesByUsernameAsync(
        int portalId,
        string username,
        string? roleName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);

        string wantedUser = username.Trim().ToLowerInvariant();

        IQueryable<UserRole> query = _context.UserRoles
            .Include(a => a.Role)
            .Where(a =>
                a.User!.Username.ToLower() == wantedUser
                && a.Role!.PortalId == portalId);

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
    public Task AddUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userRole);
        cancellationToken.ThrowIfCancellationRequested();

        _context.UserRoles.Add(userRole);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateUserRoleAsync(UserRole userRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userRole);
        cancellationToken.ThrowIfCancellationRequested();

        _context.UserRoles.Update(userRole);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The row is identified by the account-and-role pair rather than by its own key, which is how the
    /// legacy procedure identified it, and no portal argument narrows it - also as before. Every
    /// matching row is staged for removal, so a store that somehow holds a duplicate pair is left
    /// consistent rather than half-cleared.
    /// </remarks>
    public async Task DeleteUserRoleAsync(int userId, int roleId, CancellationToken cancellationToken = default)
    {
        List<UserRole> assignments = await _context.UserRoles
            .Where(a => a.UserId == userId && a.RoleId == roleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (assignments.Count == 0)
        {
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
    /// annotations belong to the account rather than to the role, so they are composed above this
    /// layer from <see cref="GetUserRolesAsync"/> instead of being flattened into these rows.
    /// </remarks>
    public async Task<IReadOnlyList<Role>> GetSubscribableRolesAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
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
