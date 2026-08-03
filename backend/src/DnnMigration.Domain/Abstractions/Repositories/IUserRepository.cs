using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the <see cref="User"/> aggregate, its per-portal memberships and the credential
/// record that backs authentication.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the user contract is reconstructed from two legacy provider stacks, not one. The core
/// data provider exposes only four user procedures; the remaining thirty-one live in the membership
/// provider stack under <c>Library/Providers/MembershipProviders/</c>. Both were read to establish
/// this surface.
/// </para>
/// <para>
/// MIGRATION: credentials are not columns on <c>dbo.Users</c>. The original
/// <c>[Password] nvarchar(20) NOT NULL</c> column was dropped in the 02.02.01 upgrade script and
/// credentials moved into the externally installed <c>aspnet_Membership</c> store, which the DotNetNuke
/// scripts only ever <c>ALTER</c>. The credential members below therefore address that external store
/// through explicit statements rather than through a mapped entity, which is why they exchange
/// primitives instead of domain objects.
/// </para>
/// <para>
/// MIGRATION: because the credential store is external, <see cref="User"/> carries a set of
/// membership-derived properties that no <c>dbo.Users</c> column backs and that the entity
/// configuration therefore ignores - approval, lock-out, creation, last-login, last-activity,
/// last-lock-out and last-password-change. Every read member on this contract
/// (<see cref="ListAsync"/>, <see cref="GetAsync"/> and <see cref="GetByUsernameAsync"/>) populates
/// those properties from the external store as part of the same read, so a caller never has to make
/// a second request to describe an account. That population is part of the contract, not an
/// implementation convenience: the legacy administration grid displayed those columns alongside the
/// account columns in one result set.
/// </para>
/// </remarks>
public interface IUserRepository
{
    /// <summary>Returns one page of the users who are members of a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pageSize">Page size; 0 requests every match unpaged.</param>
    /// <param name="query">Case-insensitive substring matched against username, display name and email, or <see langword="null"/> for all.</param>
    /// <param name="userNamePrefix">Case-insensitive prefix of the username, or <see langword="null"/> for all.</param>
    /// <param name="emailPrefix">Case-insensitive prefix of the email address, or <see langword="null"/> for all.</param>
    /// <param name="profilePropertyDefinitionId">
    /// Restrict to the members who hold a value for one profile property definition, or
    /// <see langword="null"/> to ignore profile values. Supplied together with
    /// <paramref name="profilePropertyValuePrefix"/>.
    /// </param>
    /// <param name="profilePropertyValuePrefix">Case-insensitive prefix of the profile value, or <see langword="null"/> for any value.</param>
    /// <param name="isApproved">Restrict to approved or unapproved accounts, or <see langword="null"/> for both.</param>
    /// <param name="includeUnauthorised">Whether to include members whose portal membership is not authorised.</param>
    /// <param name="includeSuperUsers">Whether to include host super-users.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces the <c>ByRef totalRecords</c> paging idiom used throughout
    /// <c>UserController.vb</c>; the count travels with the page inside <see cref="PagedResult{T}"/>.
    /// The authorisation filter reads <see cref="UserPortal.IsAuthorised"/>, which maps to the
    /// British-spelled <c>Authorised</c> column added to <c>UserPortals</c> in the 03.02.03 upgrade
    /// script; that column spelling is preserved deliberately.
    /// </para>
    /// <para>
    /// MIGRATION: the username, email and profile-property filters are matched as prefixes, not as
    /// substrings, because each legacy search branch appended a single trailing percent sign to the
    /// search text before handing it to the provider
    /// (<c>Website/admin/Users/Users.ascx.vb</c> lines 265, 269, 271 and 274). They are separate
    /// arguments rather than one combined term so that the filter actually applied is decided by the
    /// caller and evaluated by the database, which is what keeps the page count correct; a page can
    /// never be filtered after it has been read. The approval filter reaches the external membership
    /// store and replaces the two <c>GetUnAuthorizedUsers</c> overloads.
    /// </para>
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
        CancellationToken cancellationToken = default);

    /// <summary>Returns one user by key, or <see langword="null"/> when absent or not a member of the portal.</summary>
    /// <param name="portalId">Portal identifier the user must belong to, or <see langword="null"/> to ignore membership.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<User?> GetAsync(int? portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Returns one user by username, or <see langword="null"/>.</summary>
    /// <param name="portalId">Portal identifier the user must belong to, or <see langword="null"/> to ignore membership.</param>
    /// <param name="username">Username, matched case-insensitively and exactly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<User?> GetByUsernameAsync(int? portalId, string username, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a username is already taken.</summary>
    /// <param name="username">The username to test.</param>
    /// <param name="excludingUserId">A user to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>Usernames are unique across the whole installation, not per portal.</remarks>
    Task<bool> UsernameExistsAsync(string username, int? excludingUserId = null, CancellationToken cancellationToken = default);

    /// <summary>Determines whether an email address is already used within a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="email">The email address to test.</param>
    /// <param name="excludingUserId">A user to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: the legacy membership provider was configured with
    /// <c>requiresUniqueEmail="false"</c>, so uniqueness is reported here and enforced only where the
    /// caller's policy asks for it. Tightening it during migration would reject existing accounts.
    /// </remarks>
    Task<bool> EmailExistsAsync(int portalId, string email, int? excludingUserId = null, CancellationToken cancellationToken = default);

    /// <summary>Returns a user's membership record for one portal, or <see langword="null"/>.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserPortal?> GetMembershipAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Returns the names of the roles a user holds in a portal at a point in time.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="asOfUtc">The instant at which effective and expiry dates are evaluated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>These names become the role claims of the issued access token.</remarks>
    Task<IReadOnlyList<string>> ListRoleNamesAsync(
        int portalId,
        int userId,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default);

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
    /// MIGRATION: the terminal <c>dbo.Users</c> table carries no deletion flag, so the legacy
    /// administration screens removed a user from one portal by deleting the
    /// <see cref="UserPortal"/> membership row and only deleted the account itself once no membership
    /// remained. Callers reproduce that order: <see cref="RemoveMembership"/> first, then this method
    /// when the user belongs to no further portal.
    /// </remarks>
    void Remove(User user);

    /// <summary>Reads the credential state of a user from the external membership store.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>Exists</c> is <see langword="false"/> when the user has no credential record at all;
    /// <c>PasswordHash</c> carries the stored hash; <c>IsApproved</c> and <c>IsLockedOut</c> carry the
    /// account gates that authentication must honour before comparing a password.
    /// </returns>
    Task<(bool Exists, string? PasswordHash, bool IsApproved, bool IsLockedOut)> GetCredentialStateAsync(
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

    /// <summary>Replaces a user's stored password hash.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="passwordHash">The new one-way password hash.</param>
    /// <param name="utcNow">The change instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a record was updated.</returns>
    /// <remarks>
    /// MIGRATION: the legacy store used reversible encryption with
    /// <c>passwordFormat="Encrypted"</c> and <c>enablePasswordRetrieval="true"</c>. Hashes are one-way,
    /// so password retrieval is deliberately not carried forward and a legacy password is re-hashed on
    /// the owner's first successful login or by administrative reset.
    /// </remarks>
    Task<bool> SetPasswordHashAsync(
        int userId,
        string passwordHash,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the failure counters and stamps the last-login instant.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="utcNow">The login instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a record was updated.</returns>
    Task<bool> RecordSuccessfulLoginAsync(int userId, DateTime utcNow, CancellationToken cancellationToken = default);

    /// <summary>Increments the failed-attempt counter and locks the account once the threshold is reached.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="lockoutThreshold">The number of consecutive failures that locks the account.</param>
    /// <param name="attemptWindow">The window within which consecutive failures accumulate.</param>
    /// <param name="utcNow">The failure instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the account is locked out after recording this failure.</returns>
    /// <remarks>
    /// MIGRATION: reproduces the failed-attempt and lockout bookkeeping that DotNetNuke added to the
    /// membership procedures in the 04.00.00 upgrade script.
    /// </remarks>
    Task<bool> RecordFailedLoginAsync(
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
    Task<bool> DeleteCredentialAsync(int userId, CancellationToken cancellationToken = default);
}
