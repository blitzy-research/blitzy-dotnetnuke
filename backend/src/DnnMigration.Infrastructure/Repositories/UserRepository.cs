using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes <see cref="User"/> accounts, their portal memberships and their credentials.
/// </summary>
/// <remarks>
/// MIGRATION: reconstructed from two provider stacks, because neither is sufficient alone. The legacy
/// core data provider exposes only four user procedures - <c>AddUserAuthentication</c> and three
/// permission-cleanup procedures keyed by user identifier - while the remaining thirty-one live under
/// <c>Library/Providers/MembershipProviders/</c> alongside the data-access half of
/// <c>Library/Components/Users/UserController.vb</c>. Every member of that legacy controller is
/// <c>Shared</c>; here they are instance members reached through an injected interface so they can be
/// substituted in a test.
/// <para>
/// <strong>Two stores, one contract.</strong> The account columns live in <c>dbo.Users</c> and are
/// mapped. The credentials do not: the original <c>[Password] nvarchar(20) NOT NULL</c> column was
/// dropped by the 02.02.01 upgrade script and credentials moved into the externally installed
/// <c>aspnet_Membership</c> store. The eight credential members therefore delegate to
/// <see cref="MembershipStore"/>, which reaches that store through explicit parameterised statements.
/// Because the two stores share no key - <c>aspnet_Users.UserId</c> is a <c>uniqueidentifier</c> while
/// <c>dbo.Users.UserID</c> is an <c>int</c> - each credential member first resolves its integer
/// identifier to a user name, which is exactly how
/// <c>AspNetMembershipProvider.GetMembershipUser</c> linked the two at line 432.
/// </para>
/// <para>
/// <strong>The projection guarantee.</strong> <see cref="ListAsync"/>, <see cref="GetAsync"/> and
/// <see cref="GetByUsernameAsync"/> all populate the membership-derived properties of the accounts they
/// return - approval, lock-out, creation, last login, last activity, last lock-out and last password
/// change - so that a caller never has to make a second request to describe an account. That is part of
/// the contract rather than a convenience: the legacy administration grid presented those columns
/// alongside the account columns in one result set. A listing costs a bounded number of batched reads
/// against the external store, never one read per row. Those properties are excluded from the entity
/// configuration, so assigning them cannot mark the entity modified and cannot provoke an update.
/// </para>
/// <para>
/// <strong>What is deliberately not populated.</strong> The stored hash, the password question and the
/// password answer are never read into a returned entity - only
/// <see cref="GetCredentialStateAsync"/> obtains a hash, and only so that authentication can compare
/// one. <see cref="User.IsOnline"/> is likewise left null: the legacy users-online subsystem depended on
/// a scheduled purge job that is out of scope, so claiming to know the answer would be a fabrication.
/// </para>
/// <para>
/// No read member applies <c>AsNoTracking</c>, for the reason given on <see cref="PortalRepository"/>.
/// </para>
/// </remarks>
internal sealed class UserRepository : IUserRepository
{
    private readonly DnnDbContext _context;
    private readonly MembershipStore _membership;

    /// <summary>Initialises a new instance of the <see cref="UserRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <param name="membership">The external membership store, sharing this context's connection.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public UserRepository(DnnDbContext context, MembershipStore membership)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _membership = membership ?? throw new ArgumentNullException(nameof(membership));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every filter is applied by the database before the page is taken. The three prefix arguments are
    /// prefix matches rather than substring matches because each legacy search branch appended a single
    /// trailing wildcard - <c>SearchText + "%"</c> at <c>Website/admin/Users/Users.ascx.vb</c> lines 269,
    /// 271 and 274 - whereas <paramref name="query"/> is the free-text branch and matches anywhere within
    /// the user name, the display name or the address. They are separate arguments so that which filter
    /// applies is the caller's decision and not a guess made from the shape of one string.
    /// <para>
    /// The authorisation filter reads <c>UserPortals.Authorised</c> - the British spelling the 03.02.03
    /// script introduced - and the approval filter reaches the external membership store through a
    /// composable query root, which is what keeps it a single statement. When approval is requested but
    /// that store cannot be reached, an empty page is returned rather than an unfiltered one: presenting
    /// unfiltered rows as though they had been filtered would be the one failure mode a caller could not
    /// detect.
    /// </para>
    /// <para>
    /// The ordering is fixed here rather than left to the caller, because skip-and-take over an unordered
    /// query has no defined row assignment. It leads on the display name, which is the column the legacy
    /// grid presented, and ends on the primary key so the order is total.
    /// </para>
    /// </remarks>
    public async Task<PagedResult<User>> ListAsync(
        int portalId,
        int pageIndex,
        int pageSize,
        string? query,
        string? userNamePrefix,
        string? emailPrefix,
        int? profilePropertyDefinitionId,
        string? profilePropertyValuePrefix,
        bool? isApproved,
        bool includeUnauthorised,
        bool includeSuperUsers,
        CancellationToken cancellationToken = default)
    {
        IQueryable<User> root;

        if (isApproved.HasValue)
        {
            if (!await _membership.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return EmptyPage(pageIndex, pageSize);
            }

            root = _membership.ApprovedUsers(isApproved.Value);
        }
        else
        {
            root = _context.Users;
        }

        IQueryable<User> filtered = includeUnauthorised
            ? root.Where(u => _context.UserPortals.Any(m => m.UserId == u.UserId && m.PortalId == portalId))
            : root.Where(u => _context.UserPortals.Any(m => m.UserId == u.UserId && m.PortalId == portalId && m.IsAuthorised));

        if (!includeSuperUsers)
        {
            filtered = filtered.Where(u => !u.IsSuperUser);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            string wanted = query.Trim().ToLowerInvariant();
            filtered = filtered.Where(u =>
                u.Username.ToLower().Contains(wanted)
                || u.DisplayName.ToLower().Contains(wanted)
                || (u.Email != null && u.Email.ToLower().Contains(wanted)));
        }

        if (!string.IsNullOrWhiteSpace(userNamePrefix))
        {
            string prefix = userNamePrefix.Trim().ToLowerInvariant();
            filtered = filtered.Where(u => u.Username.ToLower().StartsWith(prefix));
        }

        if (!string.IsNullOrWhiteSpace(emailPrefix))
        {
            string prefix = emailPrefix.Trim().ToLowerInvariant();
            filtered = filtered.Where(u => u.Email != null && u.Email.ToLower().StartsWith(prefix));
        }

        filtered = ApplyProfileFilter(filtered, profilePropertyDefinitionId, profilePropertyValuePrefix);

        filtered = filtered
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Username)
            .ThenBy(u => u.UserId);

        List<User> rows;
        int totalCount;

        if (pageSize == 0)
        {
            rows = await filtered.ToListAsync(cancellationToken).ConfigureAwait(false);
            await PopulateAsync(rows, cancellationToken).ConfigureAwait(false);
            return PagedResult<User>.Unpaged(rows);
        }

        totalCount = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        rows = await filtered
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await PopulateAsync(rows, cancellationToken).ConfigureAwait(false);

        return PagedResult<User>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="User.UserPortals"/> is populated with every membership the account holds, not only the
    /// one that was asked about. A caller deleting an account has to know whether the account still
    /// belongs to another portal before it may remove the account itself, and a caller resolving a
    /// superuser has to see that the account exists without a membership at all - neither question can be
    /// answered from a collection that was filtered down to one portal.
    /// <para>
    /// A null portal identifier ignores membership entirely, which is how a host account is resolved:
    /// <c>PortalID</c> is <c>IDENTITY(-1, 1)</c>, so minus one is a real portal and could never have
    /// served as an "any portal" marker.
    /// </para>
    /// </remarks>
    public async Task<User?> GetAsync(int? portalId, int userId, CancellationToken cancellationToken = default)
    {
        IQueryable<User> query = _context.Users.Include(u => u.UserPortals);

        if (portalId.HasValue)
        {
            int owner = portalId.Value;
            query = query.Where(u => u.UserPortals.Any(m => m.PortalId == owner));
        }

        User? user = await query
            .FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (user is not null)
        {
            await PopulateAsync(new[] { user }, cancellationToken).ConfigureAwait(false);
        }

        return user;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The name is matched case-insensitively and exactly. Memberships are loaded for the same reasons
    /// given on <see cref="GetAsync"/>, and additionally because a sign-in has to establish that the
    /// account belongs to the portal it is signing in to.
    /// </remarks>
    public async Task<User?> GetByUsernameAsync(int? portalId, string username, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);

        string wanted = username.Trim().ToLowerInvariant();

        IQueryable<User> query = _context.Users.Include(u => u.UserPortals);

        if (portalId.HasValue)
        {
            int owner = portalId.Value;
            query = query.Where(u => u.UserPortals.Any(m => m.PortalId == owner));
        }

        User? user = await query
            .FirstOrDefaultAsync(u => u.Username.ToLower() == wanted, cancellationToken)
            .ConfigureAwait(false);

        if (user is not null)
        {
            await PopulateAsync(new[] { user }, cancellationToken).ConfigureAwait(false);
        }

        return user;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The test is installation-wide, not per portal. <c>IX_Users</c> is unique over <c>Username</c>
    /// alone, so the same name cannot describe two accounts even when they belong to different tenants -
    /// which is also why the external membership store can be keyed on the name at all.
    /// </remarks>
    public Task<bool> UsernameExistsAsync(string username, int? excludingUserId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);

        string wanted = username.Trim().ToLowerInvariant();

        IQueryable<User> query = _context.Users.Where(u => u.Username.ToLower() == wanted);

        if (excludingUserId.HasValue)
        {
            int excluded = excludingUserId.Value;
            query = query.Where(u => u.UserId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Scoped to the accounts that belong to the portal, because the legacy provider was registered with
    /// <c>requiresUniqueEmail="false"</c> - measured at <c>Website/release.config</c> line 244 - so an
    /// address genuinely may repeat. This member therefore reports a collision and leaves enforcement to
    /// whichever caller's policy asks for it; enforcing it here would reject accounts that already exist.
    /// </remarks>
    public Task<bool> EmailExistsAsync(int portalId, string email, int? excludingUserId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        string wanted = email.Trim().ToLowerInvariant();

        IQueryable<User> query = _context.Users
            .Where(u => u.Email != null
                && u.Email.ToLower() == wanted
                && _context.UserPortals.Any(m => m.UserId == u.UserId && m.PortalId == portalId));

        if (excludingUserId.HasValue)
        {
            int excluded = excludingUserId.Value;
            query = query.Where(u => u.UserId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<UserPortal?> GetMembershipAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        return _context.UserPortals
            .FirstOrDefaultAsync(m => m.PortalId == portalId && m.UserId == userId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// An assignment applies at the requested instant when it has started and has not expired; a null
    /// effective date means it has always applied and a null expiry date means it never lapses. Both
    /// bounds are evaluated by the database against the instant the caller supplies rather than against
    /// the server clock, so the answer is reproducible and testable.
    /// <para>
    /// Only roles belonging to the requested portal are considered. <c>UserRoles</c> carries no portal
    /// column, so the scope is applied through the role, and a host-level role - whose <c>PortalID</c> is
    /// null - never equals a portal identifier and is therefore excluded by construction. The names are
    /// ordered so that the claim set an access token carries is stable between issues.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListRoleNamesAsync(
        int portalId,
        int userId,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        return await _context.UserRoles
            .Where(a => a.UserId == userId
                && a.Role!.PortalId == portalId
                && (a.EffectiveDate == null || a.EffectiveDate <= asOfUtc)
                && (a.ExpiryDate == null || a.ExpiryDate >= asOfUtc))
            .Select(a => a.Role!.RoleName)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The scope is applied through the role, because <c>UserRoles</c> carries no portal column: a
    /// host-level role, whose portal identifier is null, never equals a portal identifier and is
    /// therefore excluded by construction.
    /// <para>
    /// Assignment dates are deliberately not evaluated, reproducing the terminal legacy procedure at
    /// <c>04.03.06.SqlDataProvider</c> lines 15 to 41, which filtered on the role's name and portal
    /// alone. The ordering follows its <c>ORDER BY U.FirstName + ' ' + U.LastName</c>; ordering by the
    /// two names in sequence is equivalent to ordering by that concatenation, because the separating
    /// space sorts before every character a name can begin with, and the identifier is appended as a
    /// tie-break so the sequence is stable.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<User>> ListByRoleNameAsync(
        int portalId,
        string roleName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleName);

        string wanted = roleName.Trim().ToLowerInvariant();

        List<User> rows = await _context.Users
            .Include(u => u.UserPortals)
            .Where(u => _context.UserRoles.Any(a =>
                a.UserId == u.UserId
                && a.Role!.PortalId == portalId
                && a.Role!.RoleName.ToLower() == wanted))
            .OrderBy(u => u.FirstName)
            .ThenBy(u => u.LastName)
            .ThenBy(u => u.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await PopulateAsync(rows, cancellationToken).ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    /// <remarks>
    /// No portal join is applied, reproducing the terminal legacy procedure at
    /// <c>03.00.08.SqlDataProvider</c> lines 797 to 805, which selected on the super-user flag alone.
    /// That is the whole point of the member: a host account need hold no membership row, so it is
    /// not reliably reachable from <see cref="ListAsync"/>, whose root filter requires one. The
    /// synthetic portal identifier of minus one that the legacy procedure projected is not
    /// reproduced, and a deterministic order is applied because the legacy procedure declared none.
    /// </remarks>
    public async Task<IReadOnlyList<User>> ListSuperUsersAsync(CancellationToken cancellationToken = default)
    {
        List<User> rows = await _context.Users
            .Include(u => u.UserPortals)
            .Where(u => u.IsSuperUser)
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Username)
            .ThenBy(u => u.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await PopulateAsync(rows, cancellationToken).ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    public void Add(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _context.Users.Add(user);
    }

    /// <inheritdoc />
    public void AddMembership(UserPortal membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _context.UserPortals.Add(membership);
    }

    /// <inheritdoc />
    public void RemoveMembership(UserPortal membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _context.UserPortals.Remove(membership);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal <c>dbo.Users</c> table carries no deletion flag, so removing an account is a hard
    /// delete. That is why a caller removes the membership first and removes the account only once it
    /// belongs to no further portal.
    /// </remarks>
    public void Remove(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _context.Users.Remove(user);
    }

    /// <inheritdoc />
    public async Task<(bool Exists, string? PasswordHash, bool IsApproved, bool IsLockedOut)> GetCredentialStateAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        if (userName is null)
        {
            return (false, null, false, false);
        }

        MembershipCredentialSnapshot? snapshot = await _membership
            .GetCredentialStateAsync(userName, cancellationToken)
            .ConfigureAwait(false);

        return snapshot is null
            ? (false, null, false, false)
            : (true, snapshot.PasswordHash, snapshot.IsApproved, snapshot.IsLockedOut);
    }

    /// <inheritdoc />
    public async Task<bool> CreateCredentialAsync(
        int userId,
        string passwordHash,
        bool isApproved,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);

        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is not null
            && await _membership
                .CreateAsync(userName, passwordHash, isApproved, utcNow, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SetPasswordHashAsync(
        int userId,
        string passwordHash,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);

        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is not null
            && await _membership
                .SetPasswordHashAsync(userName, passwordHash, utcNow, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RecordSuccessfulLoginAsync(int userId, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is not null
            && await _membership
                .RecordSuccessfulLoginAsync(userName, utcNow, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RecordFailedLoginAsync(
        int userId,
        int lockoutThreshold,
        TimeSpan attemptWindow,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is not null
            && await _membership
                .RecordFailedLoginAsync(userName, lockoutThreshold, attemptWindow, utcNow, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SetApprovalAsync(int userId, bool isApproved, CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is not null
            && await _membership.SetApprovalAsync(userName, isApproved, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UnlockAsync(int userId, CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is not null
            && await _membership.UnlockAsync(userName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteCredentialAsync(int userId, CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is not null
            && await _membership.DeleteAsync(userName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the optional profile-property filter to a listing.</summary>
    /// <param name="query">The listing so far.</param>
    /// <param name="propertyDefinitionId">The property to search, or <see langword="null"/>.</param>
    /// <param name="valuePrefix">The value prefix to match, or <see langword="null"/>.</param>
    /// <returns>The filtered listing.</returns>
    /// <remarks>
    /// The two arguments are independent, so three shapes are meaningful: both together select accounts
    /// whose answer to one property starts with the prefix, which is the legacy
    /// <c>GetUsersByProfileProperty</c> search; a property alone selects accounts that answered it at
    /// all; and a prefix alone searches every property. Only <c>PropertyValue</c> is searched, never the
    /// <c>PropertyText</c> overflow column, because that is the column the legacy search compared and
    /// because a prefix match over <c>ntext</c> would force a scan of the whole profile table.
    /// </remarks>
    private IQueryable<User> ApplyProfileFilter(IQueryable<User> query, int? propertyDefinitionId, string? valuePrefix)
    {
        bool hasProperty = propertyDefinitionId.HasValue;
        bool hasPrefix = !string.IsNullOrWhiteSpace(valuePrefix);

        if (!hasProperty && !hasPrefix)
        {
            return query;
        }

        string prefix = hasPrefix ? valuePrefix!.Trim().ToLowerInvariant() : string.Empty;

        if (hasProperty && hasPrefix)
        {
            int definition = propertyDefinitionId!.Value;
            return query.Where(u => _context.UserProfileValues.Any(v =>
                v.UserId == u.UserId
                && v.PropertyDefinitionId == definition
                && v.PropertyValue != null
                && v.PropertyValue.ToLower().StartsWith(prefix)));
        }

        if (hasProperty)
        {
            int definition = propertyDefinitionId!.Value;
            return query.Where(u => _context.UserProfileValues.Any(v =>
                v.UserId == u.UserId && v.PropertyDefinitionId == definition));
        }

        return query.Where(u => _context.UserProfileValues.Any(v =>
            v.UserId == u.UserId
            && v.PropertyValue != null
            && v.PropertyValue.ToLower().StartsWith(prefix)));
    }

    /// <summary>Populates the membership-derived properties of the accounts just read.</summary>
    /// <param name="users">The accounts to describe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the accounts have been described.</returns>
    /// <remarks>
    /// One batched read serves the whole set, so a page costs a bounded number of round trips rather than
    /// one per row. When the external store cannot be reached the properties are left null, which reads
    /// as "not known" - every one of them is a nullable type precisely so that this case has an honest
    /// representation and no value has to be invented. None of these properties is mapped, so assigning
    /// them cannot mark an entity modified.
    /// </remarks>
    private async Task PopulateAsync(IReadOnlyList<User> users, CancellationToken cancellationToken)
    {
        if (users.Count == 0)
        {
            return;
        }

        List<string> names = new(users.Count);

        foreach (User user in users)
        {
            names.Add(user.Username);
        }

        IReadOnlyDictionary<string, MembershipAccountSnapshot> snapshots = await _membership
            .GetAccountSnapshotsAsync(names, cancellationToken)
            .ConfigureAwait(false);

        if (snapshots.Count == 0)
        {
            return;
        }

        foreach (User user in users)
        {
            if (snapshots.TryGetValue(user.Username, out MembershipAccountSnapshot? snapshot))
            {
                user.IsApproved = snapshot.IsApproved;
                user.IsLockedOut = snapshot.IsLockedOut;
                user.CreatedDate = snapshot.CreatedDate;
                user.LastLoginDate = snapshot.LastLoginDate;
                user.LastActivityDate = snapshot.LastActivityDate;
                user.LastLockoutDate = snapshot.LastLockoutDate;
                user.LastPasswordChangeDate = snapshot.LastPasswordChangeDate;
            }
        }
    }

    /// <summary>Resolves an account identifier to the user name the external store is keyed on.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user name, or <see langword="null"/> when no such account exists.</returns>
    /// <remarks>
    /// The two stores share no key, so this resolution is unavoidable rather than incidental - see the
    /// type remarks. An account that does not exist has no credential record either, which is why every
    /// credential member answers with its "no record" result instead of throwing.
    /// </remarks>
    private Task<string?> ResolveUserNameAsync(int userId, CancellationToken cancellationToken)
    {
        return _context.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Builds the empty result a listing returns when a requested filter cannot be applied.</summary>
    /// <param name="pageIndex">The page index the caller asked for.</param>
    /// <param name="pageSize">The page size the caller asked for; 0 means unpaged.</param>
    /// <returns>An empty page of the shape the caller requested.</returns>
    private static PagedResult<User> EmptyPage(int pageIndex, int pageSize)
    {
        List<User> none = new(0);

        return pageSize == 0
            ? PagedResult<User>.Unpaged(none)
            : PagedResult<User>.Create(none, 0, pageIndex, pageSize);
    }
}
