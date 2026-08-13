using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

// THE EXTERNAL STORE IS QUERIED, NEVER OWNED. The aspnet_* tables this type reads are installed by
// Microsoft's ASP.NET SQL registration payload, not by DotNetNuke: no "CREATE TABLE aspnet_*" appears
// anywhere in the eighty-eight numbered upgrade scripts, and the chain only ever ALTERs those objects -
// 04.00.00.SqlDataProvider grafts DotNetNuke's own bookkeeping onto procedures it did not write at lines
// 31, 119, 271, 305, 333, 475, 619, 648, 703 and 828.

/// <summary>Reads and writes <see cref="User"/> accounts, their portal memberships and their credentials.</summary>
/// <remarks>
/// <para>
/// <strong>Two stores, one contract.</strong> The account columns live in <c>dbo.Users</c> and are mapped.
/// The credentials do not: the original <c>[Password] nvarchar(20) NOT NULL</c> column was dropped by the
/// 02.02.01 upgrade script and credentials moved into the externally installed <c>aspnet_Membership</c>
/// store.
/// </para>
/// <para>
/// <strong>The projection guarantee.</strong> <see cref="ListAsync"/>, <see cref="GetAsync"/> and <see
/// cref="GetByUsernameAsync"/> all populate the membership-derived properties of the accounts they return -
/// approval, lock-out, creation, last login, last activity, last lock-out and last password change - so
/// that a caller never has to make a second request to describe an account.
/// </para>
/// </remarks>
internal sealed class UserRepository : IUserRepository
{
    /// <summary>
    /// Property ordered by when a caller names none, reproducing the column the legacy member grid
    /// presented first at <c>Website/admin/Users/users.ascx</c>.
    /// </summary>
    private const string DefaultSortProperty = "DisplayName";

    /// <summary>
    /// Character that removes the special meaning of a <c>LIKE</c> metacharacter in the profile-value
    /// patterns this repository builds.
    /// </summary>
    private const string LikeEscapeCharacter = "\\";

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
    /// The authorisation filter reads <c>UserPortals.Authorised</c> - the British spelling the 03.02.03
    /// script introduced - and the approval filter reaches the external membership store through a
    /// composable query root, which is what keeps it a single statement.
    /// </remarks>
    // The unpaged case is requested with a page size of ZERO, not with the legacy sentinel triple.
    // UserController.vb L687 and L706 both read "GetUsers(portalId, False, -1, -1, -1)", passing the -1
    // defined at Library/Components/Shared/Null.vb:L41 as page index, page size and total alike.
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
        string? sortBy = null,
        bool descending = false,
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

        filtered = ApplyOrder(filtered, sortBy, descending);

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
            .Skip(Paging.SkipCount(pageIndex, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await PopulateAsync(rows, cancellationToken).ConfigureAwait(false);

        return PagedResult<User>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ THE PROJECTION IS THE WHOLE POINT OF THIS MEMBER. <c>Select</c> is applied to the query, so the
    /// three columns are the three columns the provider is asked for: the generated statement names
    /// <c>UserID</c>, <c>Username</c> and <c>DisplayName</c> and no others, nothing is materialised as an
    /// entity, nothing is tracked, and none of the composition <see cref="ListAsync"/> performs after
    /// taking a page runs here.
    /// </remarks>
    public async Task<PagedResult<AccountChoice>> ListAccountChoicesAsync(
        int portalId,
        int pageIndex,
        int pageSize,
        string? namePrefix,
        string? sortBy = null,
        bool descending = false,
        CancellationToken cancellationToken = default)
    {
        // Host accounts are excluded and unauthorised memberships are included, which is the pair the
        // legacy picker applied: every account holding a membership row for the tenant was offered,
        // authorised or not, and a host account never was.
        IQueryable<User> filtered = _context.Users
            .AsNoTracking()
            .Where(u => !u.IsSuperUser
                && _context.UserPortals.Any(m => m.UserId == u.UserId && m.PortalId == portalId));

        if (!string.IsNullOrWhiteSpace(namePrefix))
        {
            string pattern = LikePrefixPattern(namePrefix.Trim());
            filtered = filtered.Where(u => EF.Functions.Like(u.Username, pattern, LikeEscapeCharacter));
        }

        // Ordered by what the option shows, and always ending on the key so the sequence is total and a
        // page boundary cannot repeat or drop a row. Ordering happens BEFORE the projection and before Skip
        // and Take, so it orders the collection rather than one arbitrary page.
        IQueryable<AccountChoice> choices = ApplyChoiceOrder(filtered, sortBy, descending)
            .Select(u => new AccountChoice(u.UserId, u.Username, u.DisplayName));

        if (pageSize == 0)
        {
            List<AccountChoice> all = await choices.ToListAsync(cancellationToken).ConfigureAwait(false);
            return PagedResult<AccountChoice>.Unpaged(all);
        }

        int totalCount = await choices.CountAsync(cancellationToken).ConfigureAwait(false);

        List<AccountChoice> rows = await choices
            .Skip(Paging.SkipCount(pageIndex, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<AccountChoice>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A null portal identifier ignores membership entirely, which is how a host account is resolved:
    /// <c>PortalID</c> is <c>IDENTITY(-1, 1)</c>, so minus one is a real portal and could never have served
    /// as an "any portal" marker.
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
    /// The test is installation-wide, not per portal. <c>IX_Users</c> is unique over <c>Username</c> alone,
    /// so the same name cannot describe two accounts even when they belong to different tenants - which is
    /// also why the external membership store can be keyed on the name at all.
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
    // An address is a PLAIN STRING here, not a validated value object, and it is deliberately not unique.
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
    /// Only roles belonging to the requested portal are considered. <c>UserRoles</c> carries no portal
    /// column, so the scope is applied through the role, and a row whose <c>PortalID</c> were null would
    /// never equal a portal identifier and so would be excluded by construction - a case the terminal
    /// <c>Roles.PortalID int NOT NULL</c> forbids outright, which makes the exclusion belt and braces
    /// rather than a live path.
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
    /// Assignment dates are deliberately not evaluated, reproducing the terminal legacy procedure at
    /// <c>04.03.06.SqlDataProvider</c> lines 15 to 41, which filtered on the role's name and portal alone.
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
    /// <c>03.00.08.SqlDataProvider</c> lines 797 to 805, which selected on the super-user flag alone. That
    /// is the whole point of the member: a host account need hold no membership row, so it is not reliably
    /// reachable from <see cref="ListAsync"/>, whose root filter requires one.
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
    /// <remarks>
    /// Unlike the ordinary listing paths, this query intentionally performs no membership-store population.
    /// Portal removal needs the relational account and membership graph only, and the returned entities
    /// must remain tracked so that membership or account removal can be staged in the same unit of work
    /// that removes the portal.
    /// </remarks>
    public async Task<IReadOnlyList<User>> ListPortalMembersForRemovalAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .Include(u => u.UserPortals)
            .Where(u => u.UserPortals.Any(membership => membership.PortalId == portalId))
            .OrderBy(u => u.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    // MIGRATION: THE SWALLOWED DUPLICATE PATH IS NOT REPRODUCED, AND THAT IS A CONTRACT BOUNDARY RATHER
    // THAN AN OMISSION. The legacy membership provider wrapped its whole AddUser body in a catch-all and
    // answered a failure of ANY kind by returning the -1 null-integer sentinel, so a duplicate username, a
    // constraint violation and a dead connection were indistinguishable to the caller - and every one of
    // them was reported as though it were an ordinary, expected outcome.
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
    public async Task<(
        bool Exists,
        string? PasswordValue,
        PasswordFormat? Format,
        string? PasswordSalt,
        bool IsApproved,
        bool IsLockedOut)> GetCredentialStateAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        if (userName is null)
        {
            return (false, null, null, null, false, false);
        }

        MembershipCredentialSnapshot? snapshot = await _membership
            .GetCredentialStateAsync(userName, cancellationToken)
            .ConfigureAwait(false);

        return snapshot is null
            ? (false, null, null, null, false, false)
            : (
                true,
                snapshot.PasswordValue,
                snapshot.Format,
                snapshot.PasswordSalt,
                snapshot.IsApproved,
                snapshot.IsLockedOut);
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
    public async Task<CredentialWriteOutcome> SetPasswordHashAsync(
        int userId,
        string passwordHash,
        string? expectedPasswordValue,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);

        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is null
            ? CredentialWriteOutcome.NoRecord
            : await _membership
                .SetPasswordHashAsync(
                    userName,
                    passwordHash,
                    expectedPasswordValue,
                    utcNow,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MembershipWriteOutcome> RecordSuccessfulLoginAsync(
        int userId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        return userName is null
            ? MembershipWriteOutcome.NoRecord
            : await _membership
                .RecordSuccessfulLoginAsync(userName, utcNow, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MembershipWriteOutcome> RecordFailedLoginAsync(
        int userId,
        int lockoutThreshold,
        TimeSpan attemptWindow,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        string? userName = await ResolveUserNameAsync(userId, cancellationToken).ConfigureAwait(false);

        // As above: an unresolvable name is an absent record, never an unreachable store. Collapsing the
        // two would make a deleted account look like a failed security control, and the caller escalates
        // the latter.
        return userName is null
            ? MembershipWriteOutcome.NoRecord
            : await _membership
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

    /// <summary>Applies a deterministic ordering to a member listing.</summary>
    /// <param name="query">The listing so far.</param>
    /// <param name="sortBy">The sortable property the caller named, or <see langword="null"/>.</param>
    /// <param name="descending">Whether the named property is applied in descending order.</param>
    /// <returns>The ordered listing.</returns>
    /// <remarks>
    /// An ordering is applied unconditionally, including when the caller names nothing and when the caller
    /// names something this repository does not recognise, because skip-and-take over an unordered
    /// relational query has no defined row assignment. Every ordering ends on the primary key so that rows
    /// sharing a sort value still have a stable relative order.
    /// </remarks>
    private static IQueryable<User> ApplyOrder(IQueryable<User> query, string? sortBy, bool descending)
    {
        string property = string.IsNullOrWhiteSpace(sortBy) ? DefaultSortProperty : sortBy.Trim();

        return property.ToUpperInvariant() switch
        {
            "USERID" => descending
                ? query.OrderByDescending(u => u.UserId)
                : query.OrderBy(u => u.UserId),
            "USERNAME" => descending
                ? query.OrderByDescending(u => u.Username).ThenByDescending(u => u.UserId)
                : query.OrderBy(u => u.Username).ThenBy(u => u.UserId),
            "FIRSTNAME" => descending
                ? query.OrderByDescending(u => u.FirstName).ThenByDescending(u => u.UserId)
                : query.OrderBy(u => u.FirstName).ThenBy(u => u.UserId),
            "LASTNAME" => descending
                ? query.OrderByDescending(u => u.LastName).ThenByDescending(u => u.UserId)
                : query.OrderBy(u => u.LastName).ThenBy(u => u.UserId),
            "EMAIL" => descending
                ? query.OrderByDescending(u => u.Email).ThenByDescending(u => u.UserId)
                : query.OrderBy(u => u.Email).ThenBy(u => u.UserId),
            "ISSUPERUSER" => descending
                ? query.OrderByDescending(u => u.IsSuperUser).ThenByDescending(u => u.UserId)
                : query.OrderBy(u => u.IsSuperUser).ThenBy(u => u.UserId),
            _ => descending
                ? query.OrderByDescending(u => u.DisplayName)
                    .ThenByDescending(u => u.Username)
                    .ThenByDescending(u => u.UserId)
                : query.OrderBy(u => u.DisplayName)
                    .ThenBy(u => u.Username)
                    .ThenBy(u => u.UserId),
        };
    }

    /// <summary>Orders an account query by one of the two captions an account picker shows.</summary>
    /// <param name="query">The filtered accounts.</param>
    /// <param name="sortBy">The caption to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether to order descending.</param>
    /// <returns>The ordered query.</returns>
    /// <remarks>
    /// Separate from <see cref="ApplyOrder"/> because the admissible set is different, and it is different
    /// because the PROJECTION is different: a picker returns a key and two captions, so those captions are
    /// the only values it can meaningfully be ordered by.
    /// </remarks>
    private static IQueryable<User> ApplyChoiceOrder(IQueryable<User> query, string? sortBy, bool descending)
    {
        return (sortBy ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "USERNAME" => descending
                ? query.OrderByDescending(u => u.Username).ThenByDescending(u => u.UserId)
                : query.OrderBy(u => u.Username).ThenBy(u => u.UserId),
            _ => descending
                ? query.OrderByDescending(u => u.DisplayName)
                    .ThenByDescending(u => u.Username)
                    .ThenByDescending(u => u.UserId)
                : query.OrderBy(u => u.DisplayName)
                    .ThenBy(u => u.Username)
                    .ThenBy(u => u.UserId),
        };
    }

    /// <summary>Turns literal search text into a <c>LIKE</c> pattern that matches it as a prefix.</summary>
    /// <param name="text">The literal text to match at the start of a value.</param>
    /// <returns>
    /// A pattern for use with <see cref="LikeEscapeCharacter"/> as the escape character, matching any value
    /// that begins with <paramref name="text"/>.
    /// </returns>
    /// <remarks>
    /// The escape character is replaced FIRST, and the order matters rather than being incidental. Were it
    /// replaced after the others, the backslash this method had just introduced in front of a metacharacter
    /// would itself be escaped, leaving a literal backslash followed by a still-live metacharacter - so
    /// escaping would produce precisely the pattern it was meant to prevent.
    /// </remarks>
    private static string LikePrefixPattern(string text)
    {
        string literal = text
            .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter, StringComparison.Ordinal)
            .Replace("%", LikeEscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscapeCharacter + "_", StringComparison.Ordinal)
            .Replace("[", LikeEscapeCharacter + "[", StringComparison.Ordinal);

        return literal + "%";
    }

    /// <summary>Applies the optional profile-property filter to a listing.</summary>
    /// <param name="query">The listing so far.</param>
    /// <param name="propertyDefinitionId">The property to search, or <see langword="null"/>.</param>
    /// <param name="valuePrefix">The value prefix to match, or <see langword="null"/>.</param>
    /// <returns>The filtered listing.</returns>
    /// <remarks>
    /// The property is addressed by definition identifier rather than by name, which scopes it to one
    /// portal more precisely than the legacy procedure's <c>P.PortalId = @PortalId OR (P.PortalId IS NULL
    /// AND @PortalId IS NULL)</c> did: a definition identifier belongs to exactly one definition row, and
    /// that row carries its own portal.
    /// </remarks>
    private IQueryable<User> ApplyProfileFilter(IQueryable<User> query, int? propertyDefinitionId, string? valuePrefix)
    {
        bool hasProperty = propertyDefinitionId.HasValue;
        bool hasPrefix = !string.IsNullOrWhiteSpace(valuePrefix);

        if (!hasProperty && !hasPrefix)
        {
            return query;
        }

        // One pattern is built once and compared against both columns, reproducing the legacy procedure's
        // own shape: it bound a single @PropertyValue parameter and applied it to each column in turn.
        string pattern = hasPrefix ? LikePrefixPattern(valuePrefix!.Trim()) : string.Empty;

        if (hasProperty && hasPrefix)
        {
            int definition = propertyDefinitionId!.Value;
            return query.Where(u => _context.UserProfileValues.Any(v =>
                v.UserId == u.UserId
                && v.PropertyDefinitionId == definition
                && ((v.PropertyValue != null && EF.Functions.Like(v.PropertyValue, pattern, LikeEscapeCharacter))
                    || (v.PropertyText != null && EF.Functions.Like(v.PropertyText, pattern, LikeEscapeCharacter)))));
        }

        if (hasProperty)
        {
            int definition = propertyDefinitionId!.Value;
            return query.Where(u => _context.UserProfileValues.Any(v =>
                v.UserId == u.UserId && v.PropertyDefinitionId == definition));
        }

        return query.Where(u => _context.UserProfileValues.Any(v =>
            v.UserId == u.UserId
            && ((v.PropertyValue != null && EF.Functions.Like(v.PropertyValue, pattern, LikeEscapeCharacter))
                || (v.PropertyText != null && EF.Functions.Like(v.PropertyText, pattern, LikeEscapeCharacter)))));
    }

    /// <summary>Populates the membership-derived properties of the accounts just read.</summary>
    /// <param name="users">The accounts to describe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the accounts have been described.</returns>
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
    /// The two stores share no key, so this resolution is unavoidable rather than incidental - see the type
    /// remarks. An account that does not exist has no credential record either, which is why every
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
