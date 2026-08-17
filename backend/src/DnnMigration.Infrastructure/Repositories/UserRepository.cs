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
    /// <remarks>
    /// ⚠ NO INDEX COVERS THIS ORDERING, AND IT STAYS ANYWAY. <c>dbo.Users</c> carries exactly two indexes -
    /// <c>PK_Users</c> clustered on <c>UserID</c> and <c>UNIQUE IX_Users(Username)</c> - so ordering by the
    /// display caption cannot be satisfied from an index and a filtered page cannot stop early: every
    /// matching row is read and sorted before twenty are returned. Ordering by <c>Username</c> instead would
    /// let a page terminate early, and it is REJECTED, because the ordering is a behavioural contract rather
    /// than an implementation detail: the legacy procedure ordered its member listing by
    /// <c>FirstName + ' ' + LastName</c> - the display caption - at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/02.00.00.SqlDataProvider:L3623</c> and again at
    /// <c>03.00.08.SqlDataProvider:L925</c>, so the caption order is what an administrator opening this
    /// screen has always seen, and changing which twenty accounts arrive first is a visible change to the
    /// screen rather than a tuning decision. The schema is frozen, so no covering index may be added either.
    /// What is addressed instead is the cost of that sort being UNPREDICTABLE: with the search planned for
    /// its own values, a broad prefix over ten thousand accounts reads 227 pages every time rather than 227
    /// or 40,048 depending on plan-cache state.
    /// </remarks>
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

        // ⚠ NEITHER SIDE OF A SEARCH COMPARISON IS CASE-FOLDED HERE, AND THAT IS THE FIX RATHER THAN A
        // SHORTCUT. Every one of these three predicates used to wrap its column in LOWER(...) and lower-case
        // the search term to match. The fold changed no result - this schema's database and its Users columns
        // both carry SQL_Latin1_General_CP1_CI_AS, under which 'PERF_HOST' and 'perf_host' compare EQUAL, so
        // the legacy screen's own SearchText + '%' comparison was already case-insensitive without it - but it
        // cost two things that a case-insensitive collation was giving away for free:
        //
        //   1. SARGability. LOWER(col) LIKE @pattern is not a predicate on a column, so UNIQUE IX_Users
        //      (Username) cannot be seeked and the listing scans.
        //   2. Estimation. Wrapping the column also blocks the optimiser's LIKE-prefix range estimate on the
        //      sniffed parameter, so a selective prefix could not be recognised as selective: a performance
        //      review measured the SARGable form of the same count at 16 logical reads against 103 for the
        //      folded form on a twenty-thousand account catalogue - a 6.4-fold penalty paid by exactly the
        //      searches that should be cheapest.
        //
        // What the fold's removal does NOT fix is the plan-cache dependence the same review measured
        // alongside it - 80,089 logical reads against 442 for the byte-identical parameterised statement,
        // decided solely by which term compiled the cached plan. That is a property of the shape rather than
        // of the fold, it survives the fold's removal, and it is addressed separately below.
        //
        // Removing the fold restores the legacy comparison exactly and hands the case question back to the
        // collation, which is the only place that can answer it correctly for the deployed catalogue.
        if (!string.IsNullOrWhiteSpace(query))
        {
            string wanted = query.Trim();
            filtered = CannotDiscriminate(wanted)
                ? filtered.Where(_ => false)
                : filtered.Where(u =>
                    u.Username.Contains(wanted)
                    || u.DisplayName.Contains(wanted)
                    || (u.Email != null && u.Email.Contains(wanted)));
        }

        if (!string.IsNullOrWhiteSpace(userNamePrefix))
        {
            string prefix = userNamePrefix.Trim();
            filtered = CannotDiscriminate(prefix)
                ? filtered.Where(_ => false)
                : filtered.Where(u => u.Username.StartsWith(prefix));
        }

        if (!string.IsNullOrWhiteSpace(emailPrefix))
        {
            string prefix = emailPrefix.Trim();
            filtered = CannotDiscriminate(prefix)
                ? filtered.Where(_ => false)
                : filtered.Where(u => u.Email != null && u.Email.StartsWith(prefix));
        }

        // ⚠ U18 - THE SAME NAME IS RECORDED IN TWO PLACES AND THE SEARCH SAW ONLY ONE OF THEM.
        // `Library/Components/Users/UserInfo.vb` L144-L149 and L178-L183 are
        // `Property FirstName ... Get Return Profile.FirstName ... Set Profile.FirstName = Value`, so
        // legacy had ONE store: the account's first name WAS the profile property, and the `Users`
        // FirstName column was residue nothing read. This port models both - AAP 0.5.1.1 defines
        // `Entities/User.cs` carrying the columns AND `Entities/UserProfileValue.cs` carrying the answers -
        // and the account form writes the column while this search read only the property. An operator who
        // set a first name could not then find the account by it. Which store is authoritative is a
        // structural decision the AAP fixes, so the reconciliation is made here, on the READ side: it needs
        // no migration, mutates nothing, and makes existing data findable immediately.
        MirroredNameColumn mirrored = profilePropertyDefinitionId is int mirroredDefinitionId
            ? await ResolveMirroredNameColumnAsync(mirroredDefinitionId, cancellationToken)
                .ConfigureAwait(false)
            : MirroredNameColumn.None;

        filtered = ApplyProfileFilter(
            filtered,
            profilePropertyDefinitionId,
            profilePropertyValuePrefix,
            mirrored);

        // ⚠ AND REMOVING THE FOLD IS ONLY HALF OF IT, WHICH MEASUREMENT ESTABLISHED RATHER THAN THEORY.
        // With the columns bare, the seek became available and the optimiser's estimate became a function of
        // the sniffed search term - so the cached plan for this statement is now compiled for whichever term
        // arrived first and reused for every term after it. Re-measured on the same ten-thousand account
        // catalogue AFTER the fold came out: 40,048 logical reads for a broad prefix running on a plan
        // compiled for a selective one, against 219 for the same prefix on its own plan. The captured plan
        // named its cause - ParameterCompiledValue "perf%", ParameterRuntimeValue "u%", an index seek feeding
        // ten thousand key lookups and ten thousand per-row membership probes that a one-row estimate had
        // made look free. The swing survives the fold's removal because it never depended on it.
        //
        // So a search that carries caller-supplied text is planned for the values it actually has. That is
        // the only arrangement satisfying all three of what this endpoint owes its caller at once: an
        // identical request costs an identical amount whatever the cache holds, a selective term stays far
        // cheaper than a broad one, and UNIQUE IX_Users(Username) is still seeked when seeking is right. A
        // single stable plan for all terms - the other candidate - buys determinism by making the selective
        // search as expensive as the broad one, and the schema is frozen, so no index can absorb this
        // instead. An unfiltered listing is deliberately left untagged: its cost does not vary with its
        // parameters, so it keeps the cached plan and pays nothing for this.
        if (!string.IsNullOrWhiteSpace(query)
            || !string.IsNullOrWhiteSpace(userNamePrefix)
            || !string.IsNullOrWhiteSpace(emailPrefix)
            || !string.IsNullOrWhiteSpace(profilePropertyValuePrefix))
        {
            filtered = filtered.TagWith(QueryTags.PerValuePlan);
        }

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
            string trimmedPrefix = namePrefix.Trim();
            string pattern = LikePrefixPattern(trimmedPrefix);

            // The same fail-closed rule the listing applies, for the same reason - see CannotDiscriminate.
            // This path matters at least as much: it feeds the account picker an operator uses to ADD a
            // member to a role, so a picker that silently offered every account for a filter that could not
            // filter would invite the wrong account being added.
            filtered = CannotDiscriminate(trimmedPrefix)
                ? filtered.Where(_ => false)
                : filtered.Where(u => EF.Functions.Like(u.Username, pattern, LikeEscapeCharacter));

            // Same predicate shape, same table, same exposure as the member listing: a prefix whose
            // selectivity the caller chooses, ordered by a caption no index covers. Tagged for the same
            // reason and on the same condition - only when the caller actually typed something.
            filtered = filtered.TagWith(QueryTags.PerValuePlan);
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
    /// <remarks>
    /// ⚠ THE PROJECTION IS THE WHOLE POINT OF THIS MEMBER. <c>Select</c> is applied to the query, so the one
    /// column is the one column the provider is asked for: the generated statement names <c>IsSuperUser</c>
    /// and no others, nothing is materialised as an entity, nothing is tracked, no membership is joined and
    /// <see cref="PopulateAsync"/> - and therefore the second statement it issues against the credential
    /// store - does not run. The nullable result is what carries "no such account": the projection selects a
    /// non-nullable column into a nullable one so that a missing ROW and a stored <c>false</c> remain
    /// distinguishable, which a <c>bool</c> return could not express.
    /// </remarks>
    public async Task<bool?> GetHostAccountFlagAsync(int userId, CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => (bool?)u.IsSuperUser)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> GetByUsernameAsync(int? portalId, string username, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);

        // The name is trimmed but NOT case-folded, for the reason set out at length over the search
        // predicates in ListAsync above: the column carries SQL_Latin1_General_CP1_CI_AS, so the collation
        // already answers the case question, and folding the column instead turned a two-page seek of
        // UNIQUE IX_Users(Username) into a full scan of it - measured at 48 logical reads against a
        // twenty-thousand account catalogue, on the sign-in path, once per attempt.
        string wanted = username.Trim();

        IQueryable<User> query = _context.Users.Include(u => u.UserPortals);

        if (portalId.HasValue)
        {
            int owner = portalId.Value;
            query = query.Where(u => u.UserPortals.Any(m => m.PortalId == owner));
        }

        User? user = await query
            .FirstOrDefaultAsync(u => u.Username == wanted, cancellationToken)
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

        // Trimmed, not case-folded - the collation decides, and folding the column would defeat the very
        // index this test exists to exploit. See GetByUsernameAsync above.
        string wanted = username.Trim();

        IQueryable<User> query = _context.Users.Where(u => u.Username == wanted);

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

        // Trimmed, not case-folded - the collation decides. No index covers Email, so this test reads the
        // portal's accounts either way; removing the fold still matters, because a predicate on the bare
        // column can be estimated and pushed down where LOWER(Email) = @p can be neither.
        string wanted = email.Trim();

        IQueryable<User> query = _context.Users
            .Where(u => u.Email != null
                && u.Email == wanted
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
    /// <summary>
    /// Reports whether a text filter is composed entirely of characters the database collation cannot
    /// weigh, and therefore cannot filter on.
    /// </summary>
    /// <param name="filter">The trimmed filter text, in the case the caller typed it.</param>
    /// <returns>
    /// <see langword="true"/> when the filter cannot discriminate between rows and must therefore be
    /// treated as matching nothing.
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠ THIS EXISTS TO MAKE A FILTER FAIL CLOSED RATHER THAN OPEN, and the distinction is a safety one.
    /// The database this maps onto is collated <c>SQL_Latin1_General_CP1_CI_AS</c>, which predates
    /// supplementary-character support. In that collation a supplementary character carries NO WEIGHT, so
    /// it compares equal to the empty string: measured directly, <c>N'🎉🎉🎉' = N''</c> evaluates true, and
    /// <c>Username LIKE N'🎉🎉🎉%'</c> therefore degrades to <c>LIKE N'%'</c> and returns EVERY ROW.
    /// </para>
    /// <para>
    /// The consequence observed on the account listing was a search that reported itself as filtered while
    /// presenting the complete record set. That is the unsafe direction to fail in: an operator who
    /// believes a listing is narrowed to one account may act on a row - authorise, unauthorise, delete -
    /// in the belief that it is the account they searched for. Returning no rows for a filter that cannot
    /// filter is both honest and safe, and it is what a person typing characters no account contains
    /// expects to see.
    /// </para>
    /// <para>
    /// The column collation is NOT changed to correct this, because the schema is immutable, and the
    /// predicate is not forced onto a supplementary-aware collation either: doing so would make it
    /// non-sargable on a listing path and would silently alter matching for all other text.
    /// </para>
    /// <para>
    /// Only a filter composed ENTIRELY of weightless characters is caught. A filter mixing them with
    /// ordinary text still discriminates on the ordinary part and is left exactly as it was.
    /// </para>
    /// </remarks>
    private static bool CannotDiscriminate(string filter)
    {
        if (filter.Length == 0)
        {
            return false;
        }

        foreach (char unit in filter)
        {
            // A surrogate code unit is half of a supplementary character, which is the weightless class.
            // Anything else - letter, digit, punctuation, symbol in the basic plane - carries weight and
            // makes the filter discriminating.
            if (!char.IsSurrogate(unit))
            {
                return false;
            }
        }

        return true;
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

    /// <summary>The account column a searched profile property duplicates, if any.</summary>
    private enum MirroredNameColumn
    {
        /// <summary>The property duplicates no account column.</summary>
        None = 0,

        /// <summary>The property duplicates <c>Users.FirstName</c>.</summary>
        FirstName = 1,

        /// <summary>The property duplicates <c>Users.LastName</c>.</summary>
        LastName = 2,
    }

    /// <summary>Resolves which account column, if any, the searched profile property duplicates.</summary>
    /// <param name="propertyDefinitionId">The definition being searched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The duplicated column, or <see cref="MirroredNameColumn.None"/>.</returns>
    /// <remarks>
    /// Resolved by the definition's own name rather than by a hard-coded identifier, because
    /// <c>ProfilePropertyDefinition.PropertyDefinitionID</c> is an identity column whose values differ per
    /// installation while the two seeded names do not - <c>ProfileController.AddDefaultDefinitions</c> seeds
    /// them as <c>FirstName</c> and <c>LastName</c>. The comparison is ordinal and case-insensitive because
    /// the name is the installer's, not the operator's.
    /// </remarks>
    private async Task<MirroredNameColumn> ResolveMirroredNameColumnAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken)
    {
        string? name = await _context.ProfilePropertyDefinitions
            .AsNoTracking()
            .Where(definition => definition.PropertyDefinitionId == propertyDefinitionId)
            .Select(definition => definition.PropertyName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(name, "FirstName", StringComparison.OrdinalIgnoreCase))
        {
            return MirroredNameColumn.FirstName;
        }

        return string.Equals(name, "LastName", StringComparison.OrdinalIgnoreCase)
            ? MirroredNameColumn.LastName
            : MirroredNameColumn.None;
    }

    /// <summary>Applies the optional profile-property filter to a listing.</summary>
    /// <param name="query">The listing so far.</param>
    /// <param name="propertyDefinitionId">The property to search, or <see langword="null"/>.</param>
    /// <param name="valuePrefix">The value prefix to match, or <see langword="null"/>.</param>
    /// <param name="mirrored">
    /// The account column this property duplicates, so a name recorded on the account is matched as well as
    /// one recorded as a profile answer.
    /// </param>
    /// <returns>The filtered listing.</returns>
    /// <remarks>
    /// The property is addressed by definition identifier rather than by name, which scopes it to one
    /// portal more precisely than the legacy procedure's <c>P.PortalId = @PortalId OR (P.PortalId IS NULL
    /// AND @PortalId IS NULL)</c> did: a definition identifier belongs to exactly one definition row, and
    /// that row carries its own portal.
    /// </remarks>
    private IQueryable<User> ApplyProfileFilter(
        IQueryable<User> query,
        int? propertyDefinitionId,
        string? valuePrefix,
        MirroredNameColumn mirrored)
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

            // The union is applied ONLY for the two definitions that mirror an account column, so no other
            // property search is widened and none of them can match a row it did not match before.
            return mirrored switch
            {
                MirroredNameColumn.FirstName => query.Where(u =>
                    (u.FirstName != null && EF.Functions.Like(u.FirstName, pattern, LikeEscapeCharacter))
                    || _context.UserProfileValues.Any(v =>
                        v.UserId == u.UserId
                        && v.PropertyDefinitionId == definition
                        && ((v.PropertyValue != null && EF.Functions.Like(v.PropertyValue, pattern, LikeEscapeCharacter))
                            || (v.PropertyText != null && EF.Functions.Like(v.PropertyText, pattern, LikeEscapeCharacter))))),
                MirroredNameColumn.LastName => query.Where(u =>
                    (u.LastName != null && EF.Functions.Like(u.LastName, pattern, LikeEscapeCharacter))
                    || _context.UserProfileValues.Any(v =>
                        v.UserId == u.UserId
                        && v.PropertyDefinitionId == definition
                        && ((v.PropertyValue != null && EF.Functions.Like(v.PropertyValue, pattern, LikeEscapeCharacter))
                            || (v.PropertyText != null && EF.Functions.Like(v.PropertyText, pattern, LikeEscapeCharacter))))),
                _ => query.Where(u => _context.UserProfileValues.Any(v =>
                    v.UserId == u.UserId
                    && v.PropertyDefinitionId == definition
                    && ((v.PropertyValue != null && EF.Functions.Like(v.PropertyValue, pattern, LikeEscapeCharacter))
                        || (v.PropertyText != null && EF.Functions.Like(v.PropertyText, pattern, LikeEscapeCharacter))))),
            };
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
