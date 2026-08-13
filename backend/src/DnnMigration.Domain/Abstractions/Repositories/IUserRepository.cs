using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: PROVENANCE. This contract is reconstructed from the membership provider stack at
//            Library/Providers/MembershipProviders/DataProvider/DataProvider.vb, whose "Users"
//            block at line 72 declares the sixteen abstract members at lines 73 to 88, plus
//            UserLogin at line 69. It is deliberately NOT reconstructed from the core provider at
//            Library/Components/Providers/Data/DataProvider.vb: that class declares 269 abstract
//            members yet only four concern a user at all - AddUserAuthentication at line 372 and
//            the three permission-cleanup members keyed by user identifier at lines 296, 305 and
//            315. Reading the core provider alone would have produced a materially empty contract.
//            The split is real rather than incidental: the membership stack resolves through its
//            own separate static provider accessor under the namespace
//            "DotNetNuke.Security.Membership.Data", and the 1,773-line AspNetMembershipProvider
//            invokes no stored procedure directly - line 59 shows it delegating entirely through
//            that second accessor.
//
// MIGRATION: PAGING. UserController.vb declared eight ByRef totalRecords overloads - GetUsers at
//            L725 and L746, GetUsersByEmail at L769 and L793, GetUsersByUserName at L816 and L840,
//            and GetUsersByProfileProperty at L864 and L889. Each pair differed only by an
//            isHydrated As Boolean hydration toggle, which the EF Core materializer renders
//            meaningless, so the eight collapse to the single filtered reader below. The ByRef
//            total-record count becomes part of the PagedResult envelope; neither an out-parameter
//            nor a ref-parameter appears anywhere in this contract, in any member. The separate
//            count member the legacy surface needed
//            beside every paged read - GetUserCountByPortal at membership line 82 - is absorbed for
//            the same reason: the total travels with the page.
//
// MIGRATION: THE UNPAGED SENTINEL IS ELIMINATED. UserController.GetUsers at L685 and L704 obtained
//            unpaged results by passing the literal sentinel triple -1, -1, -1 to the paged
//            provider members - the calls are at L687 and L706, and the sentinel is the -1 defined
//            at Library/Components/Shared/Null.vb:L41. That convention is not reproduced: an
//            unpaged read asks for a page size of zero and is answered by the unpaged
//            representation of PagedResult, negative page coordinates being rejected outright.
//            Portals.PortalID is IDENTITY(-1, 1), so minus one is a legitimate portal identifier
//            and must never double as an absence marker; Roles.RoleID and Tabs.TabID seed at zero
//            for the same reason.
//
// MIGRATION: THE MEMBERSHIP SNAPSHOT IS COMPOSED BY THE READ PATH, NOT BY A SEPARATE MEMBER.
//            UserController.GetUserMembership(ByRef objUser) at L638 populated the eleven external
//            and contextual properties the User entity carries - IsApproved, CreatedDate, IsOnline,
//            LastActivityDate, LastLockoutDate, LastLoginDate, LastPasswordChangeDate, IsLockedOut,
//            PasswordHash, PasswordAnswer and PasswordQuestion - from the aspnet_* store, which the
//            88-script DDL chain only ever ALTERs and never CREATEs: 04.00.00.SqlDataProvider:L31
//            is ALTER PROCEDURE dbo.aspnet_Membership_UpdateUser and L119 is ALTER PROCEDURE
//            dbo.aspnet_Membership_UpdateUserInfo, each grafting DotNetNuke's own failed-attempt
//            and lockout bookkeeping onto a procedure it did not write. Those objects are installed
//            externally by the ASP.NET SQL registration payload, so nothing here may create, alter
//            or otherwise own them. The entity configuration ignores all eleven properties and this
//            repository composes them, so there is no membership-loading member to call: the
//            precise division of labour is stated on the interface remarks below and must be read
//            there rather than assumed.
//
// MIGRATION: legacy AddUser (10 positional arguments, membership DataProvider.vb:L73) wrote both
//            the Users row and the per-portal UserPortals row in one statement. The target splits
//            them, because the User entity carries no PortalID scalar and IsApproved is a per-portal
//            fact: one call stages the account, a second stages the membership, and a single
//            IUnitOfWork.SaveChangesAsync commits both atomically. The same reasoning applies to
//            UpdateUser (8 arguments, L88), whose PortalID and IsApproved arguments are membership
//            facts rather than account facts. No update member is declared at all: an account
//            obtained from a read on this contract is tracked, so mutating it and committing the
//            unit of work is the whole operation, and a redundant update member would invite a
//            second write path with different semantics.
//
// MIGRATION: membership DataProvider.vb:L69 UserLogin(Username, Password) is omitted - it verified
//            credentials inside SQL against a reversibly-encrypted store, registered with
//            passwordFormat="Encrypted" and a 3DES decryption key committed to source control at
//            Website/release.config:L89-L93 and L236-L246. Verification moves to the isolated
//            ILegacyCredentialVerifier and the Application AuthService. This repository exposes the
//            membership row's value, format discriminator and salt without interpreting any of them;
//            after a successful bounded legacy comparison, AuthService writes a BCrypt replacement
//            through SetPasswordHashAsync before issuing tokens. Administrative reset remains the
//            fallback when legacy verification is disabled, malformed or unsuccessful.
//
// MIGRATION: UserController.GetPassword(ByRef user, passwordAnswer) at L433 yields no member -
//            password retrieval is deliberately not carried forward to any repository, service,
//            endpoint or screen, because a one-way store cannot support it and reproducing
//            reversible storage is forbidden. For the same reason no member on this contract
//            performs hashing, verification, encryption or decryption: a stored representation,
//            format and salt are values this contract reads, and a current hash is a value it writes;
//            it never compares either.
//
// MIGRATION: membership DataProvider.vb:L80 GetUserByAuthToken(PortalID, UserToken, AuthType) is
//            omitted - the target domain declares no user-authentication entity, and the legacy
//            literal "DNN" auth-type argument disappears with the single JWT authentication path.
//
// MIGRATION: UserController.GetUserSettings(portalId) As Hashtable at L656 becomes a typed
//            settings object in the Application layer, not a member here - it projected loose
//            configuration rather than reading an entity. The untyped ArrayList returns at L685 and
//            L704 are likewise replaced by the typed, materialised collections used below.
//
// MIGRATION: the four presence-tracking members at membership DataProvider.vb lines 121 to 125 are
//            intentionally omitted. That subsystem couples to DotNetNuke.Services.Scheduling
//            through the purge job in the presence-tracking directory
//            "Library/Components/Users/Users Online/", whose line 44 inherits the scheduler client
//            and whose line 59 takes a schedule-history item. It is dropped as recorded in
//            MIGRATION_NOTES.md. (The directory name contains a space, so any shell glob over that
//            tree must be quoted.)
//            User.IsOnline therefore remains a value this contract may report but never maintains,
//            and no member here writes, purges or queries presence.
//
// MIGRATION: where the four ByRef sites in UserController.vb land. L638 GetUserMembership is
//            absorbed into the read path here, as described above. L156 CreateUser, which returned
//            a UserCreateStatus, and L200 DeleteUser(objUser, notify, deleteAdmin), which returned
//            a Boolean, both move to the Application UserService and are expressed as Result
//            values: the notify argument is notification and deleteAdmin is a business rule, so
//            neither is persistence. L433 GetPassword is dropped outright. Neither UserCreateStatus
//            nor UserLoginStatus appears in any signature on this contract, and no member takes a
//            notify flag, a deleteAdmin flag, a hydration toggle or a caching argument.
//
// MIGRATION: ownership boundaries with the sibling contracts, stated so they are not duplicated
//            here. Assignment rows - membership lines 109 to 114 - belong to IRoleRepository and
//            are exchanged there as UserRole values, never as accounts; this contract's by-role
//            reader is the opposite direction and answers with accounts. Profile values and their
//            definitions - membership lines 117 to 119 - belong to IUserProfileRepository, so no
//            UserProfileValue or ProfilePropertyDefinition member appears here; the profile-filtered
//            account search below is a search for accounts and is therefore correctly placed.

/// <summary>
/// Reads and writes the <see cref="User"/> aggregate, its per-portal memberships and the credential
/// record that backs authentication.
/// </summary>
/// <remarks>
/// <para>
/// This aggregate spans two physically separate stores, and that is the single most important thing
/// to know before calling anything here. The first is the DotNetNuke <c>dbo.Users</c> table together
/// with <c>dbo.UserPortals</c>. The second is the ASP.NET 2.0 membership schema, keyed by user name
/// rather than by identifier and installed by Microsoft's registration payload rather than by
/// DotNetNuke. This contract reads across both and never pretends they are one table.
/// </para>
/// <para>
/// <b>What a read gives you.</b> Every member that answers with a <see cref="User"/> - the filtered
/// reader, the by-identifier and by-name readers, and the two collection readers - returns an
/// account whose membership-derived properties have already been composed from the external store as
/// part of the same read. Concretely, the seven state and timestamp facts are populated: approval,
/// lockout state, creation, last login, last activity, last lockout and last password change. This
/// is part of the contract rather than an implementation convenience, because the legacy
/// administration grid displayed those columns beside the account columns in one result set, and a
/// caller must never have to make a second request merely to describe an account.
/// </para>
/// <para>
/// <b>What a read deliberately does not give you.</b> The stored password hash is not broadcast on
/// read members; it is served only by the credential-state member, so a listing cannot leak
/// credential material into a projection, a log or a cache. The presence flag and the
/// question-and-answer pair are never populated at all: presence tracking is a dropped subsystem,
/// and the legacy provider was registered with <c>requiresQuestionAndAnswer="false"</c>, so the pair
/// guarded nothing. Each of those properties is nullable precisely so that "not known" has an honest
/// representation and no value has to be invented.
/// </para>
/// <para>
/// <b>Writes are staged, never committed.</b> The staging members register an insert or a delete
/// with the change tracker and return nothing to await, because they perform no input or output.
/// Nothing reaches the database until the caller commits the unit of work, which is what allows an
/// account and its membership - two rows the legacy provider wrote in a single statement - to be
/// committed atomically. A generated key is therefore not available on return from a staging call;
/// it is populated by the commit.
/// </para>
/// <para>
/// <b>Paging is zero-based.</b> A page index of zero identifies the first page, matching
/// <see cref="PagedResult{T}"/> and the legacy offset arithmetic it was derived from. A page size of
/// zero requests every match as an unpaged set. Negative page coordinates are rejected; the legacy
/// practice of signalling "unpaged" with a negative sentinel is not carried forward.
/// </para>
/// <para>
/// Identifiers cross this boundary as plain CLR scalars rather than as value objects, and search
/// text as plain non-nullable strings. A search fragment is not a valid address, and the shipped
/// Host account's electronic-mail value would not satisfy the legacy address expression at all, so
/// requiring a validated value object here would make legitimate rows unreachable. Note also that
/// the legacy null-string sentinel was the empty string rather than null, so an empty value was
/// legally representable and must not be silently coerced into an absent one.
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
    /// <param name="sortBy">
    /// Name of the property to order by, or <see langword="null"/> for the display-name order the
    /// legacy grid presented. An unrecognised name falls back to that same default order.
    /// </param>
    /// <param name="descending">Whether <paramref name="sortBy"/> is applied in descending order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One page of fully composed accounts, carrying the total across all pages. Page indexing is
    /// zero-based.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this single reader replaces the four paged legacy searches and their eight
    /// ByRef totalRecords overloads, enumerated in the provenance notes above, together with
    /// GetAllUsers at membership line 76, GetUsers at line 78, GetUsersByEmail at line 83,
    /// GetUsersByProfileProperty at line 84, GetUsersByUsername at line 86 and both
    /// GetUnAuthorizedUsers paths at line 77. The count travels with the page inside
    /// <see cref="PagedResult{T}"/>. The authorisation filter reads
    /// <see cref="UserPortal.IsAuthorised"/>, which maps to the British-spelled <c>Authorised</c>
    /// column added to <c>UserPortals</c> in the 03.02.03 upgrade script; that column spelling is
    /// preserved deliberately.
    /// </para>
    /// <para>
    /// The ordering is a request of this member, not a decision left to the caller after the fact. It
    /// has to be, because paging is applied by the database: rows are ordered, then skipped, then
    /// taken, so a caller that re-ordered the returned page would only be re-ordering the rows that
    /// one arbitrary page happened to contain. That is why <paramref name="sortBy"/> is accepted here
    /// rather than above - a listing whose sort field is honoured only within a page is a listing that
    /// silently ignores the field, which is the defect this argument exists to close. The recognised
    /// names are the ones the boundary admits for this collection, declared in
    /// <c>Application/Validation/SortableFields.cs</c>; an ordering is applied unconditionally,
    /// including for an absent or unrecognised name, and always ends on the primary key so the order
    /// is total.
    /// </para>
    /// <para>
    /// MIGRATION: the username, email and profile-property filters are matched as prefixes, not as
    /// substrings, because each legacy search branch appended a single trailing percent sign to the
    /// search text before handing it to the provider
    /// (<c>Website/admin/Users/Users.ascx.vb</c> lines 265, 269, 271 and 274). They are separate
    /// arguments rather than one combined term so that the filter actually applied is decided by the
    /// caller and evaluated by the database, which is what keeps the page count correct; a page can
    /// never be filtered after it has been read. That single consideration is why this member
    /// carries a wide argument list instead of being split into one member per legacy search: four
    /// narrower members would either duplicate the query or return counts that disagree with the
    /// rows they accompany. The approval filter reaches the external membership store.
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
    /// <param name="namePrefix">
    /// Case-insensitive prefix of the LOGIN NAME, or <see langword="null"/> for all. A prefix rather
    /// than a substring, because the legacy account searches appended a single trailing wildcard to
    /// the search text (<c>Website/admin/Users/Users.ascx.vb</c> lines 269, 271 and 274) and this
    /// member answers the same question the same way. Every wildcard in the caller's text matches
    /// itself, so an operator's own per-cent sign is data rather than a pattern.
    /// <para>
    /// The display name is deliberately NOT matched, even though it is returned. The legacy name box
    /// resolved an account by its login name (<c>SecurityRoles.ascx.vb:L476-L488</c>), and a caller
    /// walking this member for a typed name relies on ordering by that login name to put an exact
    /// match on the first page - which a wider filter would break.
    /// </para>
    /// </param>
    /// <param name="sortBy">
    /// Name of the caption to order by - the display name or the login name - or
    /// <see langword="null"/> for the display-name order the legacy drop-down presented. An
    /// unrecognised name falls back to that same default order.
    /// </param>
    /// <param name="descending">Whether <paramref name="sortBy"/> is applied in descending order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One page of choices carrying the total across all pages. Page indexing is zero-based, matching
    /// <see cref="ListAsync"/> and the envelope a caller reads back.
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠ A SEPARATE MEMBER FROM <see cref="ListAsync"/> ON PURPOSE, AND IT MUST NOT BE FOLDED BACK INTO
    /// IT. A performance and privacy review measured the role-assignment picker filling itself from the
    /// account listing: for every candidate account it moved a postal address, a telephone number, an
    /// electronic-mail address, two audit instants and four status flags out of the database and into
    /// browser memory so that a name and a login could be rendered. On a tenant the picker is allowed to
    /// enumerate that is up to a thousand accounts' worth of personal detail transferred to draw a
    /// drop-down. Authorisation to read the grid is not a licence to send fields the caller cannot use.
    /// </para>
    /// <para>
    /// The listing ALSO composes each row after the page is taken - the profile values behind the
    /// address and telephone columns, and the tenant's designated administrator so the grid can withhold
    /// a delete affordance - so a picker built on it pays for a projection it renders none of. This
    /// member reads the three columns and stops: no profile read, no portal read, no membership-settings
    /// read, no per-row composition.
    /// </para>
    /// <para>
    /// THE ORDERING IS A REQUEST OF THIS MEMBER for the same reason it is on <see cref="ListAsync"/>:
    /// paging is applied by the database, so rows are ordered, then skipped, then taken, and a caller
    /// that re-ordered the returned page would only be re-ordering the rows that one page happened to
    /// contain. The two names it admits are the two captions the options SHOW - a picker cannot
    /// meaningfully be ordered by a value the operator cannot see, which is why this member's admissible
    /// set is narrower than the listing's and is exactly its own projection. An ordering is applied
    /// unconditionally, including for an absent or unrecognised name, and always ends on the primary key
    /// so the sequence is total and a page boundary cannot repeat or drop a row.
    /// </para>
    /// <para>
    /// Host super-users are excluded and unauthorised memberships are included, which is the pair
    /// <c>UserModuleBase.vb:L178-L186</c> enumerated: the legacy picker offered every account holding a
    /// membership row for the tenant, whether or not that membership was authorised, and never offered a
    /// host account.
    /// </para>
    /// </remarks>
    Task<PagedResult<AccountChoice>> ListAccountChoicesAsync(
        int portalId,
        int pageIndex,
        int pageSize,
        string? namePrefix,
        string? sortBy = null,
        bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one user by key, or <see langword="null"/> when absent or not a member of the portal.</summary>
    /// <param name="portalId">Portal identifier the user must belong to, or <see langword="null"/> to ignore membership.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fully composed account, or <see langword="null"/>.</returns>
    /// <remarks>
    /// MIGRATION: replaces GetUser(PortalId, UserId) at membership line 79. A null portal identifier
    /// ignores membership, which is how a host account is resolved; minus one could never have
    /// served as an "any portal" marker because <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// and minus one is a real portal.
    /// </remarks>
    Task<User?> GetAsync(int? portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Returns one user by username, or <see langword="null"/>.</summary>
    /// <param name="portalId">Portal identifier the user must belong to, or <see langword="null"/> to ignore membership.</param>
    /// <param name="username">Username, matched case-insensitively and exactly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fully composed account, or <see langword="null"/>.</returns>
    /// <remarks>
    /// MIGRATION: replaces GetUserByUsername(PortalID, Username) at membership line 81. This is also
    /// the member the Application sign-in path uses in place of the omitted SQL-side credential
    /// check, loading the account so that <c>IPasswordHasher</c> can verify the supplied password
    /// outside the database.
    /// </remarks>
    Task<User?> GetByUsernameAsync(int? portalId, string username, CancellationToken cancellationToken = default);

    /// <summary>Returns the users who hold a named role in a portal.</summary>
    /// <param name="portalId">Portal identifier that scopes the role.</param>
    /// <param name="roleName">Role name, matched case-insensitively and exactly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The fully composed accounts holding the role, or an empty list when the role does not exist
    /// in the portal or nobody currently holds it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces GetUsersByRolename(PortalID, Rolename) at membership line 85. It answers
    /// with accounts, which is what makes it this contract's business and not the role contract's.
    /// </para>
    /// <para>
    /// Do not confuse this with the two neighbouring readers that look superficially similar. This
    /// one answers "who holds this role?" with accounts. <see cref="ListRoleNamesAsync"/> answers the
    /// opposite question, "which roles does this account hold?", with names. The assignment rows
    /// themselves belong to the role contract and are exchanged there, never here.
    /// </para>
    /// <para>
    /// Only roles belonging to the requested portal are considered. Assignment dates are
    /// deliberately <b>not</b> evaluated, because the terminal legacy procedure did not evaluate
    /// them either: the definition at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/04.03.06.SqlDataProvider</c> lines 15 to 41
    /// takes only a portal identifier and a role name, joins the assignment to the role, and filters
    /// on the role's name and portal alone. A holder whose assignment has not yet started or has
    /// already lapsed was therefore returned by the legacy reader and is returned here too. A caller
    /// that needs the time-aware answer asks <see cref="ListRoleNamesAsync"/>, which does evaluate
    /// both bounds against a supplied instant.
    /// </para>
    /// <para>
    /// Results are ordered by given name then family name, matching the legacy
    /// <c>ORDER BY U.FirstName + ' ' + U.LastName</c>, with the account identifier as a final
    /// tie-break so that the order is stable - the legacy ordering was not unique and could return
    /// equal-named accounts in any sequence between calls.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<User>> ListByRoleNameAsync(
        int portalId,
        string roleName,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every host super-user in the installation.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fully composed super-user accounts, or an empty list when there are none.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces GetSuperUsers() at membership line 87, which took no portal argument. The
    /// absence of a portal parameter here is deliberate and is the reason this member exists
    /// separately from the filtered reader: a host account is installation-wide and need hold no
    /// membership row in any particular portal, so it is not reliably reachable from a reader that
    /// scopes its results to one portal's members. The terminal legacy procedure agrees - the
    /// definition at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/03.00.08.SqlDataProvider</c> lines 797 to
    /// 805 selects from the accounts table on the super-user flag alone, with no portal join at all.
    /// Callers administering host accounts use this member; callers listing a portal's members use
    /// the filtered reader with the super-user flag.
    /// </para>
    /// <para>
    /// MIGRATION: that legacy procedure also projected a synthetic portal identifier of minus one
    /// beside each row, as a placeholder meaning "not scoped to a portal". That literal is <b>not</b>
    /// reproduced. <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so minus one is a real portal
    /// identifier, and carrying it as a placeholder is exactly the sentinel collision this migration
    /// removes. A host account simply has no portal identifier of its own, and the accounts returned
    /// here carry none.
    /// </para>
    /// <para>
    /// The legacy procedure declared no ordering, which makes its row sequence unpredictable between
    /// calls. A deterministic order is applied here instead, so a listing is reproducible.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<User>> ListSuperUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the tracked accounts that currently hold a membership in one portal, including every
    /// membership each account holds.
    /// </summary>
    /// <param name="portalId">Portal whose members are being removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The portal's member accounts in deterministic identifier order, with
    /// <see cref="User.UserPortals"/> populated.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is a removal-specific read rather than another listing overload. A portal deletion has to
    /// decide separately for every member whether to remove only the membership row or, when this is the
    /// final membership, the installation-wide account together with its credential and active sessions.
    /// That decision cannot be made from the ordinary paged listing because it does not load the complete
    /// membership collection.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy bulk deletion made the same decision by reading one row per membership from
    /// <c>vw_Users</c>, then deleting the global account only when no second row existed. The entities
    /// returned here preserve that meaning without reproducing the reader-position test.
    /// </para>
    /// <para>
    /// The implementation deliberately does not populate approval, lockout or credential timestamps from
    /// the external membership store. None is needed to remove a tenant membership, and making this
    /// enumeration depend on that separate store would prevent the relational cleanup from even being
    /// planned when the store is unavailable. Credential deletion remains an explicit operation and may
    /// still refuse the overall deletion atomically.
    /// </para>
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
    /// Usernames are unique across the whole installation, not per portal, which is also why the
    /// external membership store can be keyed on the name at all.
    /// </remarks>
    Task<bool> UsernameExistsAsync(string username, int? excludingUserId = null, CancellationToken cancellationToken = default);

    /// <summary>Reports whether an email address is already used within a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="email">The email address to test.</param>
    /// <param name="excludingUserId">A user to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the address is already in use within the portal.</returns>
    /// <remarks>
    /// MIGRATION: this member <b>reports</b> a collision and never enforces one. The legacy
    /// membership provider was registered with <c>requiresUniqueEmail="false"</c> - measured at
    /// <c>Website/release.config</c> line 244 - so duplicate addresses are legitimate existing data,
    /// and the shipped Host account's value would not even satisfy the legacy address expression.
    /// Uniqueness is therefore never a precondition of persistence here: the corresponding policy
    /// option defaults to <see langword="false"/>, and the Application layer consults this member
    /// only when an operator has deliberately switched that policy on. Enforcing it unconditionally
    /// would reject accounts that already exist, which is why it is reported rather than imposed.
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
    /// MIGRATION: the membership row is the target of the per-portal arguments the legacy AddUser and
    /// UpdateUser members carried, and its authorisation flag is what the legacy unauthorised-user
    /// listing selected on. Its database key is the composite pair of user and portal identifiers.
    /// </remarks>
    Task<UserPortal?> GetMembershipAsync(int portalId, int userId, CancellationToken cancellationToken = default);

    /// <summary>Returns the names of the roles a user holds in a portal at a point in time.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="asOfUtc">The instant at which effective and expiry dates are evaluated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The distinct role names, ordered so that an issued claim set is stable between issues.</returns>
    /// <remarks>
    /// These names become the role claims of the issued access token. The instant is supplied by the
    /// caller rather than read from the server clock so that the answer is reproducible and testable.
    /// This is the inverse of <see cref="ListByRoleNameAsync"/> and answers with names, not accounts.
    /// </remarks>
    Task<IReadOnlyList<string>> ListRoleNamesAsync(
        int portalId,
        int userId,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------------
    // STAGED WRITES
    //
    // These members register an insert or a delete with the change tracker and perform no input or
    // output, which is why they return nothing to await. Nothing reaches the database until the unit
    // of work is committed. A generated key is populated by that commit, not by the call below, so
    // none of these members answers with an identifier - the legacy provider members returned one
    // only because each stored procedure ended with SCOPE_IDENTITY().
    // ---------------------------------------------------------------------------------------------

    /// <summary>Stages a new user for insertion.</summary>
    /// <param name="user">The user to insert.</param>
    /// <remarks>
    /// MIGRATION: one half of the decomposed legacy AddUser at membership line 73. Stage the
    /// membership with <see cref="AddMembership"/> as the other half and commit both together.
    /// </remarks>
    void Add(User user);

    /// <summary>Stages a new per-portal membership for insertion.</summary>
    /// <param name="membership">The membership to insert.</param>
    /// <remarks>
    /// MIGRATION: the second half of the decomposed legacy AddUser. Committing it in the same unit
    /// of work as <see cref="Add"/> is what preserves the atomicity the single legacy statement had.
    /// </remarks>
    void AddMembership(UserPortal membership);

    /// <summary>Stages a per-portal membership for deletion.</summary>
    /// <param name="membership">The membership to delete.</param>
    /// <remarks>MIGRATION: replaces DeleteUserPortal(UserId, PortalId) at membership line 75.</remarks>
    void RemoveMembership(UserPortal membership);

    /// <summary>Stages a user for deletion.</summary>
    /// <param name="user">The user to delete.</param>
    /// <remarks>
    /// MIGRATION: replaces DeleteUser(UserId) at membership line 74. The terminal <c>dbo.Users</c>
    /// table carries no deletion flag, so the legacy administration screens removed a user from one
    /// portal by deleting the <see cref="UserPortal"/> membership row and only deleted the account
    /// itself once no membership remained. Callers reproduce that order:
    /// <see cref="RemoveMembership"/> first, then this member when the user belongs to no further
    /// portal.
    /// </remarks>
    void Remove(User user);

    // ---------------------------------------------------------------------------------------------
    // EXTERNAL CREDENTIAL RECORD
    //
    // These members address the externally installed membership store described in the provenance
    // notes above. None of them hashes, verifies, encrypts or decrypts anything: a hash is a value
    // they carry, and comparison belongs to IPasswordHasher. They exchange primitives rather than
    // entities precisely because the store is not modelled as a mapped entity - the DDL chain only
    // ever ALTERs it.
    //
    // THEIR WRITES ARE IMMEDIATE, AND THEY ENLIST IN AN AMBIENT TRANSACTION. Because the store is not a
    // mapped entity, nothing here is staged for a later flush: each member issues its statement when it is
    // called. An implementer MUST issue that statement on the unit of work's own connection and attach the
    // transaction opened through IUnitOfWork.BeginTransactionAsync when one is open, so that a caller which
    // needs an account row and its credential to appear together - or not at all - can obtain that by
    // wrapping both in one transaction. Account creation and account deletion both depend on this property;
    // an implementation that opened its own connection would silently break their atomicity.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Reads the credential state of a user from the external membership store.</summary>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>Exists</c> is <see langword="false"/> when the user has no credential record at all;
    /// <c>PasswordValue</c>, <c>Format</c> and <c>PasswordSalt</c> carry the bounded stored
    /// representation needed to distinguish current BCrypt data from a legacy migration value;
    /// <c>IsApproved</c> and <c>IsLockedOut</c> carry the account gates authentication must honour.
    /// </returns>
    /// <remarks>
    /// MIGRATION: the stored representation, format and salt are served only through this narrow member
    /// and are deliberately not broadcast by listing or projection reads. Format and salt are required
    /// during the bounded cut-over window so a successful legacy verification can immediately replace
    /// the row with a BCrypt value; no retrieval path is introduced.
    /// </remarks>
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
    /// Replaces a user's stored password hash, and only while the stored representation is still the one the
    /// caller read.
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
    /// <para>
    /// MIGRATION: the legacy store used reversible encryption, so retrieval was possible and is
    /// deliberately not carried forward. This member only ever writes a hash it is given; a legacy
    /// credential is replaced on the owner's first successful login within the bounded compatibility
    /// window, with administrative reset retained as the fallback.
    /// </para>
    /// <para>
    /// ⚠ THIS IS A COMPARE-AND-SWAP, AND THE EXPECTATION IS NOT OPTIONAL. Every caller of this member first
    /// reads the credential and decides from that read - a self-service change verifies the current value, an
    /// administrative reset authorises against the account, a sign-in proves a legacy representation - and an
    /// unconditional write would discard whatever changed in between. An implementation MUST evaluate the
    /// expectation inside the same statement that performs the update, because that statement's row is the
    /// only serialisation point two API replicas share. <see cref="CredentialWriteOutcome"/> enumerates the
    /// races this closes and why a boolean could not express them.
    /// </para>
    /// <para>
    /// A caller that receives <see cref="CredentialWriteOutcome.Superseded"/> must NOT retry with the same
    /// hash: doing so would reintroduce the overwrite. It must re-read, re-decide, and report a conflict. On
    /// a sign-in path it must additionally refuse the sign-in, because the credential the request verified is
    /// no longer the account's credential.
    /// </para>
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
    /// What happened. <see cref="MembershipWriteOutcome.Recorded"/> when the counters were cleared,
    /// <see cref="MembershipWriteOutcome.NoRecord"/> when the account holds no credential record, and
    /// <see cref="MembershipWriteOutcome.StoreUnavailable"/> when the store could not be reached.
    /// <see cref="MembershipWriteOutcome.RecordedAndLocked"/> is never returned: clearing the counters is
    /// what a successful sign-in does, so this member never leaves an account locked.
    /// </returns>
    /// <remarks>
    /// The outcome is not decoration. Clearing the counters is half of the lock-out control - it is what stops
    /// failures accumulated over days from eventually locking an account whose owner keeps signing in
    /// successfully in between - so a caller that discards the answer cannot tell that the control has stopped
    /// working.
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
    /// account is not locked, <see cref="MembershipWriteOutcome.RecordedAndLocked"/> when it is now locked or
    /// already was, <see cref="MembershipWriteOutcome.NoRecord"/> when the account holds no credential record,
    /// and <see cref="MembershipWriteOutcome.StoreUnavailable"/> when the store could not be reached and the
    /// failure was therefore NOT counted.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: preserves the failed-attempt and lockout bookkeeping that DotNetNuke itself grafted
    /// onto the external membership procedures in the 04.00.00 upgrade script, at lines 135 to 138.
    /// Dropping it would weaken a security behaviour the legacy installation actually had, so it is
    /// carried forward rather than discarded.
    /// </para>
    /// <para>
    /// WHY THIS DOES NOT RETURN A BOOLEAN. An earlier revision returned <see langword="true"/> only when the
    /// account had become locked, which made <see langword="false"/> mean three unrelated things at once:
    /// counted and not yet at the threshold, no record to count against, and THE STORE COULD NOT BE REACHED SO
    /// NOTHING WAS COUNTED. The third is the failure of the only control standing between an attacker and
    /// unlimited credential guessing, and it was indistinguishable from the first - which is the ordinary
    /// result of one mistyped password. A caller must be able to tell them apart, and
    /// <see cref="MembershipWriteOutcome"/> is how; see that type for why its zero member is the worst case.
    /// </para>
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
    /// <remarks>
    /// MIGRATION: approval is the one membership-store fact the legacy AddUser and UpdateUser members
    /// carried as an argument, which is why it is settable here rather than through the account.
    /// </remarks>
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
    /// MIGRATION: the account row and the credential record live in different stores, so deleting an
    /// account is two operations. This one is not staged, because the external store is addressed
    /// directly rather than through the change tracker.
    /// </remarks>
    Task<bool> DeleteCredentialAsync(int userId, CancellationToken cancellationToken = default);
}
