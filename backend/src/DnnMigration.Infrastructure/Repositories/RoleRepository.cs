using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes <see cref="Role"/> permission groupings, their <see cref="RoleGroup"/> containers
/// and the <see cref="UserRole"/> assignments that connect them to accounts.
/// </summary>
/// <remarks>
/// MIGRATION: the legacy core data provider contains no role procedure at all - the twenty-two role
/// procedures live under <c>Library/Providers/MembershipProviders/</c>, alongside the data-access half
/// of <c>Library/Components/Security/Roles/RoleController.vb</c>. Both were read to reconstruct this
/// contract; reading only the core provider would have produced an almost empty repository.
/// <para>
/// A role belongs to a portal through a nullable <c>Roles.PortalID</c>, and a host-level role carries
/// null there. Every portal-scoped member compares that column to the requested identifier, which
/// excludes host roles by construction rather than by an extra predicate - relational equality never
/// matches null. That matters because <c>PortalID</c> is <c>IDENTITY(-1, 1)</c>, so minus one is a
/// real portal and could not have been used as an "unscoped" marker.
/// </para>
/// <para>
/// No read member applies <c>AsNoTracking</c>, for the reason given on <see cref="PortalRepository"/>.
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

    /// <inheritdoc />
    public async Task<PagedResult<Role>> ListAsync(
        int portalId,
        int? roleGroupId,
        int pageIndex,
        int pageSize,
        string? nameFilter,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Role> query = _context.Roles
            .Include(r => r.RoleGroup)
            .Where(r => r.PortalId == portalId);

        if (roleGroupId.HasValue)
        {
            // RoleGroupID is IDENTITY(0, 1), so zero is a legitimate group key; the presence of a
            // value selects the filter, never its magnitude.
            int group = roleGroupId.Value;
            query = query.Where(r => r.RoleGroupId == group);
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            string wanted = nameFilter.Trim().ToLowerInvariant();
            query = query.Where(r => r.RoleName.ToLower().Contains(wanted));
        }

        query = query
            .OrderBy(r => r.RoleName)
            .ThenBy(r => r.RoleId);

        if (pageSize == 0)
        {
            List<Role> all = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
            return PagedResult<Role>.Unpaged(all);
        }

        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<Role> rows = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<Role>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <inheritdoc />
    public Task<Role?> GetAsync(int roleId, CancellationToken cancellationToken = default)
    {
        // RoleID is IDENTITY(0, 1): zero is the first role an installation creates, so it can never
        // be read as "no role". Absence is reported as null.
        return _context.Roles
            .Include(r => r.RoleGroup)
            .FirstOrDefaultAsync(r => r.RoleId == roleId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Role?> GetByNameAsync(int portalId, string roleName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleName);

        string wanted = roleName.Trim().ToLowerInvariant();

        return _context.Roles
            .Include(r => r.RoleGroup)
            .FirstOrDefaultAsync(r => r.PortalId == portalId && r.RoleName.ToLower() == wanted, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_RoleName</c> is unique over <c>(PortalID, RoleName)</c> and, unlike the page-name index,
    /// it survives into the terminal schema, so a collision reported here would also be rejected by
    /// the store. Reporting it lets the caller answer with a validation failure instead of surfacing a
    /// constraint violation.
    /// </remarks>
    public Task<bool> RoleNameExistsAsync(int portalId, string roleName, int? excludingRoleId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleName);

        string wanted = roleName.Trim().ToLowerInvariant();

        IQueryable<Role> query = _context.Roles
            .Where(r => r.PortalId == portalId && r.RoleName.ToLower() == wanted);

        if (excludingRoleId.HasValue)
        {
            int excluded = excludingRoleId.Value;
            query = query.Where(r => r.RoleId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Role>> ListAutoAssignedAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .Where(r => r.PortalId == portalId && r.AutoAssignment)
            .OrderBy(r => r.RoleName)
            .ThenBy(r => r.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Add(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        _context.Roles.Add(role);
    }

    /// <inheritdoc />
    public void Remove(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        _context.Roles.Remove(role);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The account is loaded with each assignment so that a membership listing can name its members
    /// without one further read per row. The total count is taken even when the caller asks for a
    /// single row, which is what lets a caller obtain a membership count by requesting page zero of
    /// size one.
    /// </remarks>
    public async Task<PagedResult<UserRole>> ListAssignmentsAsync(
        int roleId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        IQueryable<UserRole> query = _context.UserRoles
            .Include(a => a.User)
            .Where(a => a.RoleId == roleId)
            .OrderBy(a => a.UserId)
            .ThenBy(a => a.UserRoleId);

        if (pageSize == 0)
        {
            List<UserRole> all = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
            return PagedResult<UserRole>.Unpaged(all);
        }

        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<UserRole> rows = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<UserRole>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>UserRoles</c> carries no portal column, so the scope is applied through the role the
    /// assignment points at. Assignments to host-level roles are therefore excluded, because a host
    /// role's <c>PortalID</c> is null and never equals a portal identifier.
    /// </remarks>
    public async Task<IReadOnlyList<UserRole>> ListUserAssignmentsAsync(int portalId, int userId, CancellationToken cancellationToken = default)
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
    public Task<UserRole?> GetAssignmentAsync(int roleId, int userId, CancellationToken cancellationToken = default)
    {
        return _context.UserRoles
            .FirstOrDefaultAsync(a => a.RoleId == roleId && a.UserId == userId, cancellationToken);
    }

    /// <inheritdoc />
    public void AddAssignment(UserRole assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        _context.UserRoles.Add(assignment);
    }

    /// <inheritdoc />
    public void RemoveAssignment(UserRole assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        _context.UserRoles.Remove(assignment);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleGroup>> ListGroupsAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.RoleGroups
            .Where(g => g.PortalId == portalId)
            .OrderBy(g => g.RoleGroupName)
            .ThenBy(g => g.RoleGroupId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<RoleGroup?> GetGroupAsync(int roleGroupId, CancellationToken cancellationToken = default)
    {
        // RoleGroupID is IDENTITY(0, 1), so zero is a legitimate key.
        return _context.RoleGroups
            .FirstOrDefaultAsync(g => g.RoleGroupId == roleGroupId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> GroupNameExistsAsync(
        int portalId,
        string roleGroupName,
        int? excludingRoleGroupId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleGroupName);

        string wanted = roleGroupName.Trim().ToLowerInvariant();

        IQueryable<RoleGroup> query = _context.RoleGroups
            .Where(g => g.PortalId == portalId && g.RoleGroupName.ToLower() == wanted);

        if (excludingRoleGroupId.HasValue)
        {
            int excluded = excludingRoleGroupId.Value;
            query = query.Where(g => g.RoleGroupId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void AddGroup(RoleGroup roleGroup)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        _context.RoleGroups.Add(roleGroup);
    }

    /// <inheritdoc />
    public void RemoveGroup(RoleGroup roleGroup)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        _context.RoleGroups.Remove(roleGroup);
    }


    /// <inheritdoc />
    public Task<bool> IsUserInPortalRoleAsync(
        int userId,
        int roleId,
        int portalId,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        // Every condition below is a direct counterpart of one in the terminal procedure quoted above,
        // and there are no others. AnyAsync is used rather than a count or a materialised row because the
        // question is existential: the caller asks whether a valid assignment exists, not how many.
        return _context.UserRoles
            .AsNoTracking()
            .Where(assignment =>
                assignment.UserId == userId
                && assignment.RoleId == roleId

                // The role must be owned by the portal the question is about. Expressed through the
                // navigation so that the portal key is read from the role row rather than trusted from
                // the caller, which is what makes this the tenant anchor rather than a restatement of
                // the caller's own claim. Note the equality: a role with no owning portal fails it, as
                // it did in the legacy membership procedure.
                && assignment.Role!.PortalId == portalId

                // Validity window, both bounds inclusive and both treating null as unbounded, exactly as
                // the legacy predicate did.
                && (assignment.EffectiveDate == null || assignment.EffectiveDate <= asOfUtc)
                && (assignment.ExpiryDate == null || assignment.ExpiryDate >= asOfUtc))
            .AnyAsync(cancellationToken);
    }
}
