using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the <see cref="User"/> aggregate, its per-portal memberships and the credential record
/// that backs authentication.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a read gives you.</b> Every member that answers with a <see cref="User"/> - the filtered reader,
/// the by-identifier and by-name readers, and the two collection readers - returns an account whose
/// membership-derived properties have already been composed from the external store as part of the same
/// read.
/// </para>
/// <para>
/// <b>Paging is zero-based.</b> A page index of zero identifies the first page, matching <see
/// cref="PagedResult{T}"/> and the legacy offset arithmetic it was derived from. A page size of zero
/// requests every match as an unpaged set.
/// </para>
/// </remarks>
public interface IUserRepository
{
    // ---------------------------------------------------------------------------------------------
    // ACCOUNT READS
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns one page of the users who are members of a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="pageIndex">Zero-based page index; the first page is index zero.</param>
    /// <param name="pageSize">Page size; zero requests every match unpaged.</param>
    /// <param name="query">
    /// Case-insensitive substring matched against username, display name and email, or <see
    /// langword="null"/> for all.
    /// </param>
    /// <param name="userNamePrefix">
    /// Case-insensitive prefix of the username, or <see langword="null"/> for all.
    /// </param>
    /// <param name="emailPrefix">
    /// Case-insensitive prefix of the email address, or <see langword="null"/> for all.
    /// </param>
    /// <param name="profilePropertyDefinitionId">
    /// Restrict to the members who hold a value for one profile property definition, or <see
    /// langword="null"/> to ignore profile values.
    /// </param>
    /// <param name="profilePropertyValuePrefix">
    /// Case-insensitive prefix of the profile value, or <see langword="null"/> for any value.
    /// </param>
    /// <param name="isApproved">
    /// Restrict to approved or unapproved accounts, or <see langword="null"/> for both.
    /// </param>
    /// <param name="includeUnauthorised">
    /// Whether to include members whose portal membership is not authorised.
    /// </param>
    /// <param name="includeSuperUsers">Whether to include host super-users.</param>
    /// <param name="sortBy">
    /// Name of the property to order by, or <see langword="null"/> for the display-name order the legacy
    /// grid presented.
    /// </param>
    /// <param name="descending">Whether <paramref name="sortBy"/> is applied in descending order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One page of fully composed accounts, carrying the total across all pages.</returns>
    /// <remarks>
    /// The ordering is a request of this member, not a decision left to the caller after the fact. It has
    /// to be, because paging is applied by the database: rows are ordered, then skipped, then taken, so a
    /// caller that re-ordered the returned page would only be re-ordering the rows that one arbitrary page
    /// happened to contain.
    /// </remarks>
    Task<PagedResult<User>> ListAsync(
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one page of a portal's accounts as an account picker needs them: the key and the two
    /// captions, and nothing else.
    /// </summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="pageIndex">Zero-based page index; the first page is index zero.</param>
    /// <param name="pageSize">Page size; zero requests every match unpaged.</param>
    /// <param name="namePrefix">Case-insensitive prefix of the LOGIN NAME, or <see langword="null"/> for all.</param>
    /// <param name="sortBy">
    /// Name of the caption to order by - the display name or the login name - or <see langword="null"/> for
    /// the display-name order the legacy drop-down presented.
    /// </param>
    /// <param name="descending">Whether <paramref name="sortBy"/> is applied in descending order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One page of choices carrying the total across all pages.</returns>
    /// <remarks>
    /// ⚠ A SEPARATE MEMBER FROM <see cref="ListAsync"/> ON PURPOSE, AND IT MUST NOT BE FOLDED BACK INTO IT.
    /// A performance and privacy review measured the role-assignment picker filling itself from the account
    /// listing: for every candidate account it moved a postal address, a telephone number, an
    /// electronic-mail address, two audit instants and four status flags out of the database and into
    /// browser memory so that a name and a login could be rendered.
    /// </remarks>
    Task<PagedResult<AccountChoice>> ListAccountChoicesAsync(
        int portalId,
        int pageIndex,
        int pageSize,
        string? namePrefix,
        string? sortBy = null,
        bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one user by key, or <see langword="null"/> when absent or not a member of the portal.
    /// </summary>
    /// <param name="portalId">
    /// Portal identifier the user must belong to, or <see langword="null"/> to ignore membership.
    /// </param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fully composed account, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Replaces GetUser(PortalId, UserId) at membership line 79. A null portal identifier ignores
    /// membership, which is how a host account is resolved; minus one could never have served as an "any
    /// portal" marker because <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c> and minus one is a real
    /// portal.
    /// </remarks>
    Task<User?> GetAsync(int? portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Returns one user by username, or <see langword="null"/>.</summary>
    /// <param name="portalId">
    /// Portal identifier the user must belong to, or <see langword="null"/> to ignore membership.
    /// </param>
    /// <param name="username">Username, matched case-insensitively and exactly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fully composed account, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Replaces GetUserByUsername(PortalID, Username) at membership line 81. This is also the member the
    /// Application sign-in path uses in place of the omitted SQL-side credential check, loading the account
    /// so that <c>IPasswordHasher</c> can verify the supplied password outside the database.
    /// </remarks>
    Task<User?> GetByUsernameAsync(int? portalId, string username, CancellationToken cancellationToken = default);

    /// <summary>Returns the users who hold a named role in a portal.</summary>
    /// <param name="portalId">Portal identifier that scopes the role.</param>
    /// <param name="roleName">Role name, matched case-insensitively and exactly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The fully composed accounts holding the role, or an empty list when the role does not exist in the
    /// portal or nobody currently holds it.
    /// </returns>
    /// <remarks>
    /// Results are ordered by given name then family name, matching the legacy <c>ORDER BY U.FirstName + '
    /// ' + U.LastName</c>, with the account identifier as a final tie-break so that the order is stable -
    /// the legacy ordering was not unique and could return equal-named accounts in any sequence between
    /// calls.
    /// </remarks>
    Task<IReadOnlyList<User>> ListByRoleNameAsync(
        int portalId,
        string roleName,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every host super-user in the installation.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fully composed super-user accounts, or an empty list when there are none.</returns>
    /// <remarks>
    /// Replaces GetSuperUsers() at membership line 87, which took no portal argument.
    /// </remarks>
    Task<IReadOnlyList<User>> ListSuperUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the tracked accounts that currently hold a membership in one portal, including every
    /// membership each account holds.
    /// </summary>
    /// <param name="portalId">Portal whose members are being removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The portal's member accounts in deterministic identifier order, with <see cref="User.UserPortals"/>
    /// populated.
    /// </returns>
    /// <remarks>
    /// The implementation deliberately does not populate approval, lockout or credential timestamps from
    /// the external membership store. None is needed to remove a tenant membership, and making this
    /// enumeration depend on that separate store would prevent the relational cleanup from even being
    /// planned when the store is unavailable.
    /// </remarks>
    Task<IReadOnlyList<User>> ListPortalMembersForRemovalAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Determines whether a username is already taken.</summary>
    /// <param name="username">The username to test.</param>
    /// <param name="excludingUserId">A user to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the name is already in use.</returns>
    /// <remarks>
    /// Usernames are unique across the whole installation, not per portal, which is also why the external
    /// membership store can be keyed on the name at all.
    /// </remarks>
    Task<bool> UsernameExistsAsync(string username, int? excludingUserId = null, CancellationToken cancellationToken = default);

    /// <summary>Reports whether an email address is already used within a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="email">The email address to test.</param>
    /// <param name="excludingUserId">A user to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the address is already in use within the portal.</returns>
    /// <remarks>
    /// This member <b>reports</b> a collision and never enforces one. The legacy membership provider was
    /// registered with <c>requiresUniqueEmail="false"</c> - measured at <c>Website/release.config</c> line
    /// 244 - so duplicate addresses are legitimate existing data, and the shipped Host account's value
    /// would not even satisfy the legacy address expression.
    /// </remarks>
    Task<bool> EmailExistsAsync(int portalId, string email, int? excludingUserId = null, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------------
    // PER-PORTAL MEMBERSHIP
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns a user's membership record for one portal, or <see langword="null"/>.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The membership row, or <see langword="null"/> when the user does not belong to the portal.</returns>
    /// <remarks>
    /// The membership row is the target of the per-portal arguments the legacy AddUser and UpdateUser
    /// members carried, and its authorisation flag is what the legacy unauthorised-user listing selected
    /// on. Its database key is the composite pair of user and portal identifiers.
    /// </remarks>
    Task<UserPortal?> GetMembershipAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Returns the names of the roles a user holds in a portal at a point in time.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="asOfUtc">The instant at which effective and expiry dates are evaluated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The distinct role names, ordered so that an issued claim set is stable between issues.</returns>
    /// <remarks>
    /// These names become the role claims of the issued access token. The instant is supplied by the caller
    /// rather than read from the server clock so that the answer is reproducible and testable.
    /// </remarks>
    Task<IReadOnlyList<string>> ListRoleNamesAsync(
        int portalId,
        int userId,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default);

    // STAGED WRITES

    /// <summary>Stages a new user for insertion.</summary>
    /// <param name="user">The user to insert.</param>
    void Add(User user);

    /// <summary>Stages a new per-portal membership for insertion.</summary>
    /// <param name="membership">The membership to insert.</param>
    void AddMembership(UserPortal membership);

    /// <summary>Stages a per-portal membership for deletion.</summary>
    /// <param name="membership">The membership to delete.</param>
    void RemoveMembership(UserPortal membership);

    /// <summary>Stages a user for deletion.</summary>
    /// <param name="user">The user to delete.</param>
    /// <remarks>
    /// Replaces DeleteUser(UserId) at membership line 74. The terminal <c>dbo.Users</c> table carries no
    /// deletion flag, so the legacy administration screens removed a user from one portal by deleting the
    /// <see cref="UserPortal"/> membership row and only deleted the account itself once no membership
    /// remained.
    /// </remarks>
    void Remove(User user);

    // EXTERNAL CREDENTIAL RECORD
    // These members address the externally installed membership store described in the provenance notes
    // above. None of them hashes, verifies, encrypts or decrypts anything: a hash is a value they carry,
    // and comparison belongs to IPasswordHasher.

    /// <summary>Reads the credential state of a user from the external membership store.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>Exists</c> is <see langword="false"/> when the user has no credential record at all;
    /// <c>PasswordValue</c>, <c>Format</c> and <c>PasswordSalt</c> carry the bounded stored representation
    /// needed to distinguish current BCrypt data from a legacy migration value; <c>IsApproved</c> and
    /// <c>IsLockedOut</c> carry the account gates authentication must honour.
    /// </returns>
    Task<(
        bool Exists,
        string? PasswordValue,
        PasswordFormat? Format,
        string? PasswordSalt,
        bool IsApproved,
        bool IsLockedOut)> GetCredentialStateAsync(
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the credential record of a newly registered user.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="passwordHash">The one-way password hash to store.</param>
    /// <param name="isApproved">Whether the account is usable immediately.</param>
    /// <param name="utcNow">The creation instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a record was created.</returns>
    Task<bool> CreateCredentialAsync(
        int userId,
        string passwordHash,
        bool isApproved,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a user's stored password hash, and only while the stored representation is still the one
    /// the caller read.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="passwordHash">The new one-way password hash.</param>
    /// <param name="expectedPasswordValue">
    /// The stored representation the caller read from <see cref="GetCredentialStateAsync"/> and made its
    /// decision against, or <see langword="null"/> when that read returned no stored value.
    /// </param>
    /// <param name="utcNow">The change instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Which of the four <see cref="CredentialWriteOutcome"/> states occurred.</returns>
    /// <remarks>
    /// MIGRATION: the legacy store used reversible encryption, so retrieval was possible and is
    /// deliberately not carried forward. This member only ever writes a hash it is given; a legacy
    /// credential is replaced on the owner's first successful login within the bounded compatibility
    /// window, with administrative reset retained as the fallback.
    /// </remarks>
    Task<CredentialWriteOutcome> SetPasswordHashAsync(
        int userId,
        string passwordHash,
        string? expectedPasswordValue,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the failure counters and stamps the last-login instant.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="utcNow">The login instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// What happened. <see cref="MembershipWriteOutcome.Recorded"/> when the counters were cleared, <see
    /// cref="MembershipWriteOutcome.NoRecord"/> when the account holds no credential record, and <see
    /// cref="MembershipWriteOutcome.StoreUnavailable"/> when the store could not be reached. <see
    /// cref="MembershipWriteOutcome.RecordedAndLocked"/> is never returned: clearing the counters is what a
    /// successful sign-in does, so this member never leaves an account locked.
    /// </returns>
    /// <remarks>
    /// The outcome is not decoration. Clearing the counters is half of the lock-out control - it is what
    /// stops failures accumulated over days from eventually locking an account whose owner keeps signing in
    /// successfully in between - so a caller that discards the answer cannot tell that the control has
    /// stopped working.
    /// </remarks>
    Task<MembershipWriteOutcome> RecordSuccessfulLoginAsync(
        int userId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>Increments the failed-attempt counter and locks the account once the threshold is reached.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="lockoutThreshold">The number of consecutive failures that locks the account.</param>
    /// <param name="attemptWindow">The window within which consecutive failures accumulate.</param>
    /// <param name="utcNow">The failure instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// What happened. <see cref="MembershipWriteOutcome.Recorded"/> when the failure was counted and the
    /// account is not locked, <see cref="MembershipWriteOutcome.RecordedAndLocked"/> when it is now locked
    /// or already was, <see cref="MembershipWriteOutcome.NoRecord"/> when the account holds no credential
    /// record, and <see cref="MembershipWriteOutcome.StoreUnavailable"/> when the store could not be
    /// reached and the failure was therefore NOT counted.
    /// </returns>
    /// <remarks>
    /// Preserves the failed-attempt and lockout bookkeeping that DotNetNuke itself grafted onto the
    /// external membership procedures in the 04.00.00 upgrade script, at lines 135 to 138. Dropping it
    /// would weaken a security behaviour the legacy installation actually had, so it is carried forward
    /// rather than discarded.
    /// </remarks>
    Task<MembershipWriteOutcome> RecordFailedLoginAsync(
        int userId,
        int lockoutThreshold,
        TimeSpan attemptWindow,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>Sets whether an account is approved for use.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="isApproved">The approval state to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a record was updated.</returns>
    Task<bool> SetApprovalAsync(int userId, bool isApproved, CancellationToken cancellationToken = default);

    /// <summary>Clears a lockout and resets the failure counters.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a record was updated.</returns>
    Task<bool> UnlockAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a user's credential record from the external membership store.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a record was deleted.</returns>
    /// <remarks>
    /// The account row and the credential record live in different stores, so deleting an account is two
    /// operations. This one is not staged, because the external store is addressed directly rather than
    /// through the change tracker.
    /// </remarks>
    Task<bool> DeleteCredentialAsync(int userId, CancellationToken cancellationToken = default);
}
