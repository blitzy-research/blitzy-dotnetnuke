// Every annotation line below carries the "// MIGRATION:" marker, including continuation
// lines, so an automated scan can lift the whole block with a single filter. Rule T5 requires
// each deliberate behavioural difference to be annotated in place rather than absorbed
// silently; the longer narrative for each one lives in the XML documentation further down.
//
// MIGRATION: (01) The legacy surface was static. Library/Components/Users/UserController.vb is
// MIGRATION:      1,372 lines and declares 64 public members, measured directly. Apart from two
// MIGRATION:      stateful instance properties and a tail of thin instance wrappers, every
// MIGRATION:      service member is Shared, so nothing that consumed it could be substituted or
// MIGRATION:      tested without a live database. Every member below is an instance member on an
// MIGRATION:      injected abstraction, per AAP 0.5.1.2 and 0.7.4.
//
// MIGRATION: (02) Seven members reported an outcome by mutating an argument passed by reference.
// MIGRATION:      They are CreateUser L156, DeleteUser L200, GetPassword L433,
// MIGRATION:      GetUserMembership L638 - a Sub, so pure argument mutation with no return value
// MIGRATION:      at all - UserLogin L991 and the two ValidateUser overloads at L1110 and L1132.
// MIGRATION:      Each becomes a Task<Result<...>>: the mutated object becomes the result value
// MIGRATION:      and the status enumeration becomes the failure code. No target member on this
// MIGRATION:      contract declares a by-reference or by-output parameter.
//
// MIGRATION: (03) Eight paged reads handed their grand total back through the argument list
// MIGRATION:      instead of returning it - GetUsers at L725 and L746, GetUsersByEmail at L769
// MIGRATION:      and L793, GetUsersByUserName at L816 and L840, and GetUsersByProfileProperty
// MIGRATION:      at L864 and L889. All eight collapse into the single list member below, which
// MIGRATION:      returns PagedResult<UserListItemDto> and so carries the page and the grand
// MIGRATION:      total on one value.
//
// MIGRATION: (04) Credential retrieval is NOT carried forward. GetPassword L433 is dropped
// MIGRATION:      outright and no member returns, echoes or reconstructs a credential. The
// MIGRATION:      legacy store made that possible: the original table held the credential in
// MIGRATION:      clear text as [Password] nvarchar(20) NOT NULL, inside the CREATE TABLE at
// MIGRATION:      Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider
// MIGRATION:      L97-L110, and the later membership store was configured reversible with
// MIGRATION:      passwordFormat="Encrypted" plus enablePasswordRetrieval="true" at
// MIGRATION:      Website/release.config L236-L246, over a Triple-DES machine key committed to
// MIGRATION:      source control at L89-L93. That key's value is deliberately not reproduced
// MIGRATION:      anywhere in this tree. Recovery is one-way hashing plus administrative reset.
//
// MIGRATION: (05) ResetPassword L906 returned the new credential as its value. The reset path
// MIGRATION:      here returns Task<Result> and never a credential.
//
// MIGRATION: (06) The recovery question-and-answer pair is omitted.
// MIGRATION:      ChangePasswordQuestionAndAnswer L139 has no counterpart, and no member
// MIGRATION:      declares a passwordQuestion or passwordAnswer parameter. The legacy policy did
// MIGRATION:      not require the pair - requiresQuestionAndAnswer="false" at
// MIGRATION:      Website/release.config L241 - and its only real use was credential retrieval,
// MIGRATION:      which item (04) removes.
//
// MIGRATION: (07) The credential policy itself is preserved verbatim, as declarative request
// MIGRATION:      validation rather than as a service predicate: minimum length 7, zero required
// MIGRATION:      non-alphanumeric characters and email uniqueness not enforced, read from
// MIGRATION:      Website/release.config L242-L244. ValidatePassword L1067 is therefore NOT
// MIGRATION:      exposed; a second, service-side predicate would be a second policy free to
// MIGRATION:      diverge from the first. GeneratePassword L314 and L330 are likewise absent,
// MIGRATION:      because a generated credential has to be transmitted to be useful and that
// MIGRATION:      reintroduces exactly the disclosure item (04) removes.
//
// MIGRATION: (08) This contract owns the administrative-reset half of the credential migration
// MIGRATION:      path. AAP 0.7.5.5 prescribes re-hashing on first successful sign-in with
// MIGRATION:      administrative reset as the fallback; the sign-in half belongs to the sibling
// MIGRATION:      authentication contract and is not duplicated here.
//
// MIGRATION: (09) GetCurrentUserInfo L381 resolved the caller from ambient per-request state and
// MIGRATION:      is NOT here. The sibling ICurrentUser abstraction in this folder replaces it.
// MIGRATION:      For an anonymous caller the legacy accessor returned a hollow object rather
// MIGRATION:      than nothing at all, and that empty-object sentinel becomes an explicit
// MIGRATION:      unauthenticated flag on that sibling. Every member below takes the
// MIGRATION:      identifiers it operates on explicitly.
//
// MIGRATION: (10) Sign-in and cookie members are NOT here. UserLogin L991 and L1024,
// MIGRATION:      ValidateUser L1110, L1132 and L1171, and SetAuthCookie L919 - whose body is
// MIGRATION:      empty in this checkout - belong to the sibling authentication and token
// MIGRATION:      contracts. Two sign-in paths would be the worst available outcome, so none of
// MIGRATION:      them is restated here even in a weaker form.
//
// MIGRATION: (11) GetUserSettings L656 could legitimately return nothing. It assigns its result
// MIGRATION:      only inside an "If Not objModule Is Nothing" guard, after locating the User
// MIGRATION:      Accounts module by definition name, so an installation without that module
// MIGRATION:      yielded nothing rather than an error. The settings read below therefore
// MIGRATION:      reports a SUCCESSFUL result whose value is null to mean "absent", never a
// MIGRATION:      failure and never an exception.
//
// MIGRATION: (12) Hydration and provider-synchronisation switches are dropped. isHydrated at
// MIGRATION:      L477, L497, L518, L564, L704, L746, L793, L840 and L889, hydrateRoles at L518,
// MIGRATION:      ProgressiveHydration at L1336 and L1341, SynchronizeUsers at L1326, L1336 and
// MIGRATION:      L1341, and AddToMembershipProvider at L1281 all disappear, and FillUserInfo
// MIGRATION:      L1305 is not ported at all. The persistence materialiser replaces both legacy
// MIGRATION:      hydration paths per AAP 0.7.3, and the shape of each response is settled by
// MIGRATION:      its data transfer object rather than by a caller-supplied switch.
//
// MIGRATION: (13) Caching members are dropped. GetCachedUser L350, SettingsKey L924, CacheKey
// MIGRATION:      L1315 and GetCacheKey L1310 have no counterpart; the 15 measured caching call
// MIGRATION:      sites in this controller are absorbed by the domain-owned caching abstraction,
// MIGRATION:      invoked inside the implementation. No member here names or exposes a cache.
//
// MIGRATION: (14) Four reads are not ported. GetSuperUsers L1331 is excluded with host-level
// MIGRATION:      administration; GetOnlineUsers L415 is excluded with the users-online purge
// MIGRATION:      job and its scheduler; GetUserCreateStatus L598 mapped a status to a localised
// MIGRATION:      message, and the localisation mechanism is not ported - status becomes a stable
// MIGRATION:      failure code here and the wording is authored in the client, with the legacy
// MIGRATION:      resource files as reference only. GetUserCountByPortal L582 is omitted for a
// MIGRATION:      different reason: it is not excluded, it is redundant. It returned a bare
// MIGRATION:      tenant-wide account count, and the paged envelope this contract returns already
// MIGRATION:      carries that count as its total across all pages, so the listing member answers
// MIGRATION:      the question with no extra round trip. Every screen that needed a count needed
// MIGRATION:      a page of accounts too - the legacy grid read both - and no AAP-named screen
// MIGRATION:      asks for the count alone. A second member returning the same number from a
// MIGRATION:      different query would be a second source of truth for it.
//
// MIGRATION: (15) Bulk deletion is not exposed. DeleteUsers L273 and L1300, DeleteAllUsers L1287
// MIGRATION:      and DeleteUnauthorizedUsers L293 have no endpoint in the target API, and a
// MIGRATION:      reachable "erase every account in this tenant" member would be a defect rather
// MIGRATION:      than a feature. The notify switch on L200 and L273 is dropped because the mail
// MIGRATION:      subsystem it drove is excluded. The deleteAdmin switch is a genuine business
// MIGRATION:      rule - L200 sets its verdict from the tenant's AdministratorId - so it becomes
// MIGRATION:      a rule enforced inside the delete member and surfaced as its own failure code,
// MIGRATION:      never a boolean the caller has to reason about.
//
// MIGRATION: (16) The stateful controller properties are not ported. DisplayFormat L1210 and
// MIGRATION:      PortalId L1219 made the legacy type a mutable object that callers configured
// MIGRATION:      before invoking a method. This contract is stateless and the tenant identifier
// MIGRATION:      is an explicit parameter on every member. UpdateDisplayNames L1259, a bulk
// MIGRATION:      maintenance sweep with no endpoint in the target API, is omitted with it.
//
// MIGRATION: (17) Untyped, pre-generics collection returns become typed ones. Every legacy
// MIGRATION:      collection read returned the non-generic framework list type and
// MIGRATION:      GetUserSettings L656 returned the non-generic key-value table type; this
// MIGRATION:      contract exposes IReadOnlyList<T> and typed data transfer objects only.
//
// MIGRATION: (18) Numeric sentinels are never used to mean "absent". Null.vb L41-L45 defines the
// MIGRATION:      integer sentinel as minus one, the date sentinel as the minimum date and - the
// MIGRATION:      subtle one - the string sentinel as the EMPTY STRING rather than nothing at
// MIGRATION:      all. The schema makes the numeric sentinel unusable: Portals.PortalID is
// MIGRATION:      IDENTITY(-1, 1) at 01.00.00.SqlDataProvider L77, so minus one is both the
// MIGRATION:      legacy "absent" marker and the identifier of the first real tenant, while
// MIGRATION:      Users.UserID is IDENTITY(1, 1) at L98, so zero is never a valid account
// MIGRATION:      identifier even though Roles.RoleID at L114 seeds at zero. Optional
// MIGRATION:      identifiers on this surface are therefore int? carrying null, and optional
// MIGRATION:      text is null for "not supplied" while the empty string keeps its legacy
// MIGRATION:      meaning of a stored blank - the distinction is externally observable on a
// MIGRATION:      profile value, so it is preserved rather than normalised away.
//
// MIGRATION: (19) Deleting an account cascades to its permission grants. DeleteUser L200 removes
// MIGRATION:      folder, module and tab permissions keyed by the account identifier before the
// MIGRATION:      account itself, which is the trio of permission-cleanup procedures recorded in
// MIGRATION:      AAP 0.7.1.1. The cascade is orchestrated inside the implementation by calling
// MIGRATION:      the sibling permission contract; it is not a parameter or a member here.
//
// MIGRATION: (20) The per-account membership transitions keep their legacy granularity.
// MIGRATION:      Website/admin/Users/Membership.ascx.vb drives four discrete commands, each
// MIGRATION:      gated on current state at L135-L144 and each mutating exactly one flag:
// MIGRATION:      approve at L199, force a credential change at L221, revoke approval at L243
// MIGRATION:      and clear a lockout at L262-L265. They are separate members below rather than
// MIGRATION:      fields on the update request, so no ordinary edit can silently reinstate a
// MIGRATION:      locked account, and L135 shows the legacy screen refusing all four when the
// MIGRATION:      administrator is acting on their own account.
//
// MIGRATION: (21) Status enumerations do not appear on this surface at all. The result primitive
// MIGRATION:      carries its reason generically, as a code and a message, because its folder
// MIGRATION:      sits upstream of the enumerations folder. UserCreateStatus is the trap that
// MIGRATION:      makes this worth stating: it declares 18 members valued 0 through 17 and its
// MIGRATION:      Success member is 13, NOT 0, while 0 is AddUser, the pre-call state seeded at
// MIGRATION:      L158 before the provider is even invoked. PasswordUpdateStatus (8 members),
// MIGRATION:      UserValidStatus (5 members) and UserRegistrationStatus (negative values) are
// MIGRATION:      likewise mapped to failure codes rather than exposed.

using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Application-layer contract for the user aggregate - an identity with credentials and a
/// profile - together with profile values, profile property definitions and the tenant-level
/// membership settings that govern how accounts are presented and administered.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this contract owns.</b> It is the single entry point for the seven client routes
/// that administer accounts: the account list, account creation, the account editor, the
/// profile editor, the credential screen, the tenant membership settings and the profile
/// definition catalogue. It replaces the whole administrative half of the legacy
/// <c>UserController</c>, whose 64 public members are enumerated with their disposition in the
/// annotation block at the head of this file. There is deliberately no separate profile
/// service and no separate profile-definition service: profile values and profile property
/// definitions are part of the user aggregate and are administered through the members below.
/// </para>
/// <para>
/// <b>Who the caller is, is not asked here.</b> No member resolves the acting principal from
/// ambient state, and none accepts or returns the sibling current-user abstraction. Every
/// member takes the tenant and account identifiers it operates on explicitly, which is what
/// makes the contract substitutable in a test without a request in flight. An implementation
/// may inject the current-user abstraction to attribute an audit record - the legacy trail
/// wrote one per sign-in attempt at <c>UserController.vb</c> L81 and one per deletion at L240,
/// and those become structured log events - but that is an implementation concern and never
/// appears on this surface.
/// </para>
/// <para>
/// <b>Signing in is not asked here either.</b> Credential verification, token issue, refresh
/// rotation, session termination and the re-hash-on-first-sign-in half of the credential
/// migration belong to the sibling authentication and token contracts in this folder. This
/// covers administrative credential changes and the administrative reset only. Nothing from
/// the HTTP stack appears on this surface: this project declares one project reference, to the
/// domain layer, and no web framework reference at all, so reaching for a transport type here
/// does not compile.
/// </para>
/// <para>
/// <b>Data access is not asked here.</b> No member exposes a persistence type, a query or a
/// text command. The domain-owned repository and unit-of-work abstractions carry that, and the
/// domain-owned hashing, clock and caching abstractions carry credential hashing, all time
/// arithmetic - lockout windows and credential expiry included - and every caching decision.
/// None of them is redeclared here and none appears on a signature.
/// </para>
/// <para>
/// <b>Outcome model.</b> Every member returns a result. An <em>expected</em> failure is a
/// failed result carrying a stable code, never an exception; the API edge turns codes into
/// problem documents and the client turns them into wording. An <em>unexpected</em> failure
/// stays an exception and is shaped by the global handler at the edge. Reading the value of a
/// failed result throws, so a caller tests the success flag first.
/// </para>
/// <para>
/// <b>Absent is not failed.</b> A successful result whose value is <see langword="null"/>
/// means the thing asked for does not exist, which is a legitimate answer rather than an
/// error. That applies to every single-item read below and, most consequentially, to the
/// membership settings read, whose legacy predecessor genuinely returned nothing when the User
/// Accounts module was not installed. A caller distinguishes the two by testing the value for
/// <see langword="null"/> after confirming success, and typically maps it to a 404.
/// </para>
/// <para>
/// <b>Failure code vocabulary.</b> Codes are lower-case, dot-separated and stable, because a
/// client branches on them. The set an implementation may raise is fixed and documented on
/// each member; the shared members of it are <c>user.not-found</c>,
/// <c>profile-definition.not-found</c> and <c>persistence.conflict</c> for a concurrent-edit
/// collision. Codes derived from a legacy status enumeration are named after the status member
/// so the mapping stays auditable, but the enumeration itself never crosses this boundary.
/// </para>
/// <para>
/// <b>Asynchrony.</b> Every member is I/O bound, so every member returns a task, carries the
/// <c>Async</c> suffix and accepts a cancellation token as its final, defaulted parameter.
/// There is no synchronous member and no blocking bridge anywhere in the request path.
/// </para>
/// <para>
/// <b>Registration and lifetime.</b> The application layer's <c>AddApplication()</c> extension
/// registers this contract with a scoped lifetime, as one of the seven services it registers
/// alongside the portal, module, role, permission, tab and authentication contracts. Scoped is
/// the correct lifetime because the implementation composes with the scoped repository and
/// unit-of-work abstractions; a singleton would capture a unit of work across requests.
/// </para>
/// <para>
/// <b>Implementer's checklist.</b> Enforce every rule described on a member inside that
/// member, never by asking the caller for a switch. Raise only the documented codes. Return a
/// successful result with a <see langword="null"/> value for an absent item rather than a
/// failure. Never place a credential, a hash or a salt value in a result, a log entry or a
/// message. Never treat minus one or zero as an absent identifier. Never normalise an empty
/// stored string to <see langword="null"/> or the reverse. Honour the cancellation token on
/// every await.
/// </para>
/// </remarks>
public interface IUserService
{
    /// <summary>
    /// Returns one page of the accounts belonging to a tenant, optionally narrowed by account
    /// name, electronic-mail address, a single profile property value, or approval state.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant whose accounts are listed, backed by the legacy
    /// <c>Portals.PortalID</c> column. Always a real identifier: the column is declared
    /// <c>IDENTITY(-1, 1)</c>, so minus one and zero both address genuine tenants and neither
    /// may be read as "unspecified".
    /// </param>
    /// <param name="page">
    /// The page coordinates to read. The page-index base and the way an unpaged, all-records
    /// request is expressed are fixed and documented by the paging primitives in the domain
    /// layer, and are deliberately not restated here so that they can only be read one way.
    /// </param>
    /// <param name="userNameFilter">
    /// Optional account-name filter, matched as a prefix. <see langword="null"/> means "do not
    /// filter by account name"; the empty string is not a synonym for that and is rejected by
    /// request validation.
    /// </param>
    /// <param name="emailFilter">
    /// Optional electronic-mail filter, matched as a prefix, with the same
    /// <see langword="null"/> semantics as <paramref name="userNameFilter"/>.
    /// </param>
    /// <param name="profilePropertyName">
    /// Optional name of the profile property to filter on. Must be supplied together with
    /// <paramref name="profilePropertyValue"/>; supplying one without the other is a failure
    /// rather than a silently ignored argument.
    /// </param>
    /// <param name="profilePropertyValue">
    /// Optional profile property value to match as a prefix, paired with
    /// <paramref name="profilePropertyName"/>.
    /// </param>
    /// <param name="isApproved">
    /// Optional approval-state filter. <see langword="false"/> selects the accounts awaiting
    /// approval, <see langword="true"/> selects the approved ones, and <see langword="null"/>
    /// selects both.
    /// </param>
    /// <param name="cancellationToken">Token observed while the page is read.</param>
    /// <returns>
    /// A successful result carrying the requested page, which is an empty page - never a failure
    /// and never a <see langword="null"/> value - when no account matches or when the tenant
    /// holds no accounts at all. Documented failure codes are
    /// <c>user.list.filter-conflict</c>, when more than one of the account-name,
    /// electronic-mail and profile-property filters is supplied, or when only one half of the
    /// profile-property pair is supplied; and <c>user.list.unknown-profile-property</c>, when
    /// the named profile property is not defined for the tenant.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This one member replaces twelve legacy reads. Eight of them reported the grand total by
    /// mutating an argument - <c>GetUsers</c> at <c>UserController.vb</c> L725 and L746,
    /// <c>GetUsersByEmail</c> at L769 and L793, <c>GetUsersByUserName</c> at L816 and L840, and
    /// <c>GetUsersByProfileProperty</c> at L864 and L889 - and the paging envelope now carries
    /// the page and the total together. Two more, <c>GetUsers</c> at L685 and L704, expressed
    /// "return everything" by passing the integer sentinel three times over, which the unpaged
    /// factory on the paging envelope replaces. The last two, <c>GetUnAuthorizedUsers</c> at
    /// L458 and L477, are the <paramref name="isApproved"/> filter set to
    /// <see langword="false"/>. The two whole-installation overloads at L1336 and L1341 are not
    /// reachable here at all, because every member on this contract is tenant-scoped.
    /// </para>
    /// <para>
    /// The filters are mutually exclusive by design, not by accident. The legacy account grid
    /// dispatched to exactly one search shape per postback through an if-else chain at
    /// <c>Website/admin/Users/Users.ascx.vb</c> L265, L269, L271 and L274, selected by a
    /// drop-down, and each branch appended a percent sign to the search text - so prefix
    /// matching, not containment, is the preserved semantic. Combining filters would be new
    /// behaviour, so it is refused with a code rather than invented.
    /// </para>
    /// <para>
    /// No hydration, progressive-materialisation or provider-synchronisation switch appears
    /// here, and none is needed: the list projection is settled by
    /// <see cref="UserListItemDto"/>. Note that the address and telephone columns that grid
    /// displayed are profile values rather than account columns, which is why they arrive
    /// through the list projection rather than through a filter.
    /// </para>
    /// </remarks>
    Task<Result<PagedResult<UserListItemDto>>> ListUsersAsync(
        int portalId,
        PagedRequest page,
        string? userNameFilter = null,
        string? emailFilter = null,
        string? profilePropertyName = null,
        string? profilePropertyValue = null,
        bool? isApproved = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single account within a tenant.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant that owns the account. An account belonging to a different
    /// tenant is reported as absent rather than returned, so this parameter enforces tenant
    /// isolation and is not merely a hint.
    /// </param>
    /// <param name="userId">
    /// Identifier of the account, backed by <c>Users.UserID</c>, which is declared
    /// <c>IDENTITY(1, 1)</c>, so zero is never a valid value.
    /// </param>
    /// <param name="cancellationToken">Token observed while the account is read.</param>
    /// <returns>
    /// A successful result whose value is the account, or a successful result whose value is
    /// <see langword="null"/> when no such account exists within the tenant. Absent is not a
    /// failure, so this member documents no failure code; an unexpected fault propagates as an
    /// exception and is shaped by the global handler at the API edge.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This single member replaces eight legacy reads: <c>GetUser</c> at
    /// <c>UserController.vb</c> L497, L518 and L1245, <c>GetUserByName</c> at L544 and L564,
    /// <c>GetUserByUsername</c> at L1321 and L1326, and the caching wrapper at L350. The
    /// by-name variants are not carried forward as their own member: administering an account
    /// always begins from its identifier, and searching by account name is the
    /// <c>userNameFilter</c> argument of <see cref="ListUsersAsync"/>. Resolving an account name
    /// while verifying a credential belongs to the sibling authentication contract and reaches
    /// the repository directly, so it does not need a member here.
    /// </para>
    /// <para>
    /// The four legacy switches on those overloads all disappear. Two of them selected how much
    /// of the object graph to materialise and the persistence materialiser makes that decision
    /// obsolete; a third asked the excluded provider model to synchronise first; and the caching
    /// wrapper at L350 is subsumed by the domain-owned caching abstraction, which the
    /// implementation consults without exposing a switch. The membership facts that
    /// <c>GetUserMembership</c> L638 populated by mutating its argument - approval, lockout,
    /// forced credential change and the associated timestamps - are plain members of
    /// <see cref="UserDetailDto"/>, so that member needs no counterpart either.
    /// </para>
    /// </remarks>
    Task<Result<UserDetailDto>> GetUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an account within a tenant and returns it as persisted.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that will own the new account.</param>
    /// <param name="request">
    /// The account to create. Shape and field-level rules - including the credential policy
    /// preserved verbatim from the legacy configuration - are enforced declaratively by the
    /// matching validator before this member is reached.
    /// </param>
    /// <param name="cancellationToken">Token observed while the account is created.</param>
    /// <returns>
    /// A successful result carrying the persisted account, including its server-assigned
    /// identifier, which is what lets the API layer answer 201 Created with a location. The
    /// documented failure codes are the reachable members of the legacy creation status,
    /// renamed but not renumbered: <c>user.create.username-already-exists</c>,
    /// <c>user.create.user-already-registered</c>, <c>user.create.duplicate-username</c>,
    /// <c>user.create.duplicate-email</c>, <c>user.create.invalid-username</c>,
    /// <c>user.create.invalid-email</c>, <c>user.create.invalid-password</c>,
    /// <c>user.create.password-mismatch</c>, <c>user.create.user-rejected</c>,
    /// <c>user.create.portal-assignment-failed</c>, <c>user.create.provider-error</c> and
    /// <c>user.create.unexpected-error</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the archetype of the by-reference conversion. <c>CreateUser</c> at
    /// <c>UserController.vb</c> L156 took its argument by reference, mutated it with the
    /// server-assigned identifier, <em>and</em> returned a status enumeration, so one call
    /// produced two answers that no single type tied together. Here the persisted account is
    /// the result value and the status becomes the failure code. The two instance wrappers at
    /// L1275 and L1281 collapse into this member as well, and the provider-synchronisation
    /// switch the second one carried is dropped with the excluded provider model.
    /// </para>
    /// <para>
    /// Read the status mapping carefully, because the legacy numbering is a trap. The creation
    /// enumeration declares 18 members valued 0 through 17 and its <c>Success</c> member is
    /// <b>13, not 0</b>. Zero is <c>AddUser</c>, the pre-call state that L158 assigns before the
    /// provider is invoked at all, so testing a status against zero, or relying on the default
    /// value of the enumeration type, reports success for a call that never happened. That is
    /// precisely why no status value crosses this boundary: success is the success flag on the
    /// result, and failure is a code.
    /// </para>
    /// <para>
    /// Six enumeration members are deliberately unreachable here. <c>AddUser</c> is the pre-call
    /// state described above. The two provider-key members belong to the excluded membership
    /// provider model. The invalid-question and invalid-answer members belong to the recovery
    /// pair this contract omits. The wording each code carries is authored in the client, using
    /// the legacy resource files as the reference for phrasing, because the legacy member that
    /// turned a status into a message - L598 - mapped through the localisation mechanism, which
    /// is not ported.
    /// </para>
    /// <para>
    /// Two measured behaviours are preserved inside this member rather than exposed on it.
    /// After a successful creation the legacy code assigned the new account to every tenant role
    /// flagged for automatic assignment, at L167 through L180, skipping that step for a
    /// host-level account; the implementation reproduces it by calling the sibling role
    /// contract, which is also why no role-assignment member appears on this contract. It also
    /// invalidated the tenant cache at L164, which the domain-owned caching abstraction now
    /// handles. Creation is a single unit of work: the account row, the tenant membership row
    /// and the automatic role assignments commit together or not at all, which the legacy
    /// sequence of independent statements could not guarantee.
    /// </para>
    /// </remarks>
    Task<Result<UserDetailDto>> CreateUserAsync(
        int portalId,
        CreateUserRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the editable columns of an existing account and returns it as persisted.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account to update.</param>
    /// <param name="request">
    /// The new values for the editable columns. The account name is not among them, matching the
    /// legacy data surface, whose terminal creation procedure accepts an account name and whose
    /// terminal update procedure does not.
    /// </param>
    /// <param name="cancellationToken">Token observed while the account is updated.</param>
    /// <returns>
    /// A successful result carrying the account as persisted, which is what lets the API layer
    /// answer 200 OK with the updated representation. Documented failure codes are
    /// <c>user.not-found</c>, when no such account exists within the tenant, and
    /// <c>persistence.conflict</c>, when a concurrent edit changed the row first.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>UpdateUser</c> at <c>UserController.vb</c> L963 and its instance wrapper at
    /// L1361, both of which accepted a mutable entity, returned nothing and therefore reported
    /// neither success nor a reason.
    /// </para>
    /// <para>
    /// Electronic-mail uniqueness is deliberately <b>not</b> enforced, and there is consequently
    /// no duplicate-address failure code on this member. The legacy membership provider was
    /// configured with unique addresses switched off, at <c>Website/release.config</c> L244, so
    /// enforcing it here would reject edits the legacy application accepted and could strand
    /// existing accounts that already share an address. Tightening the rule is a separate,
    /// explicit decision and not something a migration performs quietly.
    /// </para>
    /// <para>
    /// Two behaviours stay inside the member. The display name is reformatted to the tenant's
    /// configured format before the row is written, reproducing the call the legacy editor made
    /// immediately beforehand at <c>Website/admin/Users/User.ascx.vb</c> L374; and editing the
    /// tenant administrator's own account invalidates the tenant-scoped cache, which L368 through
    /// L371 did explicitly. Neither is a parameter.
    /// </para>
    /// <para>
    /// This member changes columns only. Approval, lockout and forced-credential-change are
    /// state transitions with their own preconditions and have their own members below, so an
    /// ordinary edit cannot reinstate a locked account as a side effect. Role membership is
    /// administered through the sibling role contract, profile values through
    /// <see cref="UpdateProfileAsync"/>, and credentials through
    /// <see cref="ChangePasswordAsync"/>.
    /// </para>
    /// </remarks>
    Task<Result<UserDetailDto>> UpdateUserAsync(
        int portalId,
        int userId,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an account from a tenant, together with the permission grants that reference it.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account to delete.</param>
    /// <param name="cancellationToken">Token observed while the account is deleted.</param>
    /// <returns>
    /// A successful result with no value, which is what lets the API layer answer 204 No
    /// Content. Documented failure codes are <c>user.not-found</c>;
    /// <c>user.delete.administrator-protected</c>, when the account is the tenant's designated
    /// administrator; and <c>user.delete.superuser-protected</c>, when the account is a
    /// host-level account and therefore beyond the reach of tenant administration.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>DeleteUser</c> at <c>UserController.vb</c> L200 and its instance wrapper at
    /// L1292. The legacy pair reported a bare boolean and swallowed every exception into it, so
    /// a refusal on policy grounds and a genuine fault were indistinguishable at the call site.
    /// Splitting them is the whole point of returning a result with a code.
    /// </para>
    /// <para>
    /// The administrator-protection rule is enforced <b>inside</b> this member and is not a
    /// switch. L200 read the tenant row, compared the account against its designated
    /// administrator and then set its own verdict from the caller-supplied switch, which meant
    /// the caller decided whether the tenant's administrator survived. Carrying that switch
    /// forward would place a business decision in the API layer, so the rule is enforced here
    /// and refused with its own code. The host-level guard follows the legacy editor, which
    /// computed the visibility of its delete affordance from the same two conditions at
    /// <c>Website/admin/Users/User.ascx.vb</c> L267.
    /// </para>
    /// <para>
    /// Deletion cascades. Before removing the account, L200 removed the folder, module and tab
    /// permission grants keyed by its identifier - the three cleanup procedures the data-surface
    /// census records - and the implementation reproduces that by calling the sibling permission
    /// contract inside one unit of work, so a partial deletion cannot leave orphaned grants
    /// behind. The mail notification the legacy switch controlled is dropped with the excluded
    /// mail subsystem, and the deletion audit entry L240 wrote becomes a structured log event,
    /// so the audit intent survives the change of mechanism.
    /// </para>
    /// <para>
    /// There is deliberately no bulk counterpart. The legacy surface offered three of them plus
    /// an unapproved-account sweep, none of which has an endpoint in the target API, and a
    /// reachable member that erases every account in a tenant would be a defect rather than a
    /// feature.
    /// </para>
    /// </remarks>
    Task<Result> DeleteUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes an account's credential, either as a self-service change that presents the
    /// current credential or as an administrative reset that does not.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose credential is changing.</param>
    /// <param name="request">
    /// The credential change. Its operation discriminator selects the self-service change from
    /// the administrative reset, and the preserved policy - minimum length seven, no required
    /// non-alphanumeric character - is applied declaratively by the matching validator before
    /// this member is reached.
    /// </param>
    /// <param name="cancellationToken">Token observed while the credential is changed.</param>
    /// <returns>
    /// A successful result with no value. <b>It never carries a credential.</b> Documented
    /// failure codes correspond to the reachable members of the legacy credential-update status:
    /// <c>user.password.missing</c>, <c>user.password.invalid</c>,
    /// <c>user.password.mismatch</c>, <c>user.password.not-different</c> and
    /// <c>user.password.reset-failed</c>; together with <c>user.not-found</c>,
    /// <c>user.password.current-incorrect</c>, when a self-service change presents the wrong
    /// current credential, <c>user.password.reset-not-enabled</c>, when the deployment has
    /// administrative reset switched off, and <c>user.password.unsupported-operation</c>, for an
    /// unrecognised operation discriminator.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>ChangePassword</c> at <c>UserController.vb</c> L103, the two
    /// <c>SetPassword</c> wrappers at L1346 and L1351, and <c>ResetPassword</c> at L906. The
    /// first three returned a bare boolean, which lost the reason; L103 additionally threw a
    /// bare exception for an invalid credential, which is an <em>expected</em> failure and
    /// therefore becomes a code rather than a thrown object.
    /// </para>
    /// <para>
    /// <b>The reset path never returns the new credential.</b> L906 assigned the provider's
    /// answer onto the account and then returned it, so the credential travelled back to the
    /// caller in clear text. That is not reproduced in any form. Nothing on this contract
    /// returns, echoes, logs or reconstructs a credential, and there is no retrieval member at
    /// all: the legacy retrieval member at L433 is dropped outright, together with the recovery
    /// question-and-answer pair at L139 whose only real purpose was to guard it. Hashing,
    /// verification and the re-hash decision belong to the domain-owned hashing abstraction and
    /// are invoked inside the implementation, so no hash and no salt value appears on this
    /// surface.
    /// </para>
    /// <para>
    /// This member owns the administrative-reset half of the credential migration path: legacy
    /// credentials were held reversibly and cannot be verified against a one-way hash, so an
    /// account is re-hashed on its first successful sign-in - the sibling authentication
    /// contract's half - and an administrator resets it here when that never happens. Two
    /// members of the legacy status enumeration, both concerning the recovery answer and
    /// question, are unreachable here because that pair is omitted.
    /// </para>
    /// <para>
    /// No validation predicate is exposed. The legacy surface offered one at L1067 and called it
    /// from inside L103 as well, so the policy existed in two places; here it exists once, as
    /// declarative request validation. A generation member is likewise absent, because a
    /// generated credential must be transmitted to be useful, which would reintroduce exactly
    /// the disclosure this design removes.
    /// </para>
    /// </remarks>
    Task<Result> ChangePasswordAsync(
        int portalId,
        int userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the lockout on an account so that its holder can attempt to sign in again.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the locked account.</param>
    /// <param name="cancellationToken">Token observed while the lockout is cleared.</param>
    /// <returns>
    /// A successful result with no value. Documented failure codes are <c>user.not-found</c>;
    /// <c>user.unlock.not-locked</c>, when the account is not currently locked, because the
    /// legacy screen offered the command only in that state; and
    /// <c>user.membership.self-forbidden</c>, when an administrator invokes the transition on
    /// their own account.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>UnLockUser</c> at <c>UserController.vb</c> L938 and the instance wrapper at
    /// L1356, and is reached from the membership panel's unlock command at
    /// <c>Website/admin/Users/Membership.ascx.vb</c> L262, which then cleared the in-memory flag
    /// at L265 only when the call reported success. The cache invalidation L938 performed
    /// afterwards is the domain-owned caching abstraction's concern here.
    /// </para>
    /// <para>
    /// The two preconditions are measured, not invented. L142 made the command visible only
    /// while the account was locked, and L135 hid all four membership commands when the acting
    /// administrator was looking at their own account. Both become codes rather than switches,
    /// so the API layer cannot decide either question for itself.
    /// </para>
    /// <para>
    /// A caveat worth recording next to this member: the legacy sign-in path treated a locked
    /// account as authenticated, because it derived its verdict by testing the status
    /// against outright failure alone. That is a discovered defect in the sign-in path, annotated
    /// where it lives - on the sibling authentication contract - and deliberately not repaired
    /// here. This member is simply the affordance an administrator uses once an account is
    /// locked.
    /// </para>
    /// </remarks>
    Task<Result> UnlockUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves an account, or revokes an approval already granted.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose approval state changes.</param>
    /// <param name="isApproved">
    /// <see langword="true"/> to approve the account, <see langword="false"/> to revoke an
    /// approval already granted.
    /// </param>
    /// <param name="cancellationToken">Token observed while the transition is applied.</param>
    /// <returns>
    /// A successful result with no value. Documented failure codes are <c>user.not-found</c>;
    /// <c>user.approval.unchanged</c>, when the account is already in the requested state,
    /// because the legacy screen offered each direction only from its opposite; and
    /// <c>user.membership.self-forbidden</c>, when an administrator invokes the transition on
    /// their own account.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Consolidates the two approval commands on the membership panel, which set the flag and
    /// then persisted the account through <c>UserController.vb</c> L963: approve at
    /// <c>Website/admin/Users/Membership.ascx.vb</c> L199 and revoke at L243. One member with a
    /// direction argument rather than two members keeps the pair symmetrical and keeps the
    /// precondition in one place; the direction is data, not a policy decision delegated to the
    /// caller.
    /// </para>
    /// <para>
    /// This transition is deliberately absent from <see cref="UpdateUserAsync"/>. The legacy
    /// screen gated each direction on current state at L143 and L144 and refused all of them for
    /// the acting administrator's own account at L135, and folding an approval flag into an
    /// ordinary column edit would discard those preconditions. Bulk approval has no counterpart:
    /// the legacy sweep that deleted every unapproved account has no endpoint in the target API.
    /// </para>
    /// </remarks>
    Task<Result> SetUserApprovalAsync(
        int portalId,
        int userId,
        bool isApproved,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an account as required to change its credential at its next successful sign-in.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account to flag.</param>
    /// <param name="cancellationToken">Token observed while the flag is set.</param>
    /// <returns>
    /// A successful result with no value. Documented failure codes are <c>user.not-found</c>;
    /// <c>user.password.change-already-required</c>, when the account already carries the flag,
    /// because the legacy screen offered the command only while it did not; and
    /// <c>user.membership.self-forbidden</c>, when an administrator invokes the transition on
    /// their own account.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces the membership panel's third command, which set the forced-change flag at
    /// <c>Website/admin/Users/Membership.ascx.vb</c> L221 and persisted the account through
    /// <c>UserController.vb</c> L963. It is the non-destructive alternative to an administrative
    /// reset: it demands a new credential at the next sign-in without replacing the current one,
    /// and it therefore transmits nothing. The precondition mirrors L144, and the self-service
    /// prohibition mirrors L135.
    /// </para>
    /// </remarks>
    Task<Result> RequirePasswordChangeAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the tenant-level membership settings that govern how accounts are listed, which
    /// profile fields are shown, and where a visitor is sent after signing in, registering or
    /// ending a session.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose settings are read.</param>
    /// <param name="cancellationToken">Token observed while the settings are read.</param>
    /// <returns>
    /// A successful result carrying the settings, <b>or a successful result whose value is
    /// <see langword="null"/> when the tenant has no settings source at all</b>. The null value
    /// means "absent", not "failed": the legacy reader assigned its result only inside a
    /// not-nothing guard, after locating the User Accounts module by definition name, so an
    /// installation without that module legitimately yielded nothing and the screens that
    /// consumed it fell back to their defaults. Reporting a failure or throwing instead would
    /// change behaviour those screens depended upon, so this member documents no failure code.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>GetUserSettings</c> at <c>UserController.vb</c> L656, whose return type was
    /// the untyped, pre-generics key-value table and whose keys were therefore unchecked
    /// strings. The typed projection is <see cref="MembershipSettingsDto"/>. The cache lookup and
    /// the cache-key helper that surrounded the legacy reader, and the performance multiplier its
    /// expiry was computed from, all belong to the domain-owned caching abstraction and to bound
    /// configuration; none of them appears here.
    /// </para>
    /// <para>
    /// This member serves the tenant membership settings screen. It is unrelated to the
    /// per-account membership transitions above, which act on one account: these settings are
    /// tenant-wide presentation and policy, consolidated from the three legacy screens the
    /// migration plan folds together.
    /// </para>
    /// </remarks>
    Task<Result<MembershipSettingsDto>> GetMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the tenant-level membership settings.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose settings are written.</param>
    /// <param name="settings">
    /// The complete settings to store. Every member is written, so this is a replace-the-set
    /// operation rather than a partial patch, which matches the legacy screen's behaviour of
    /// re-writing each setting it manages on every update.
    /// </param>
    /// <param name="cancellationToken">Token observed while the settings are written.</param>
    /// <returns>
    /// A successful result with no value. The documented failure code is
    /// <c>user.membership-settings.source-missing</c>, raised when the tenant has no settings
    /// source to write to - the same condition that makes the read above answer with a
    /// <see langword="null"/> value. Absence is a legitimate answer to a read and an
    /// impossibility for a write, which is why the two members treat it differently.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Included because the screen genuinely writes: <c>Website/admin/Users/UserSettings.ascx.vb</c>
    /// handles its update command at L163 and persists each value through a module-setting write
    /// at L183. Exposing only a read would have left that screen unimplementable, so this member
    /// exists on measured evidence rather than for symmetry.
    /// </para>
    /// </remarks>
    Task<Result> UpdateMembershipSettingsAsync(
        int portalId,
        MembershipSettingsDto settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads an account's profile: the values it holds for the profile properties its tenant
    /// defines.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose profile is read.</param>
    /// <param name="cancellationToken">Token observed while the profile is read.</param>
    /// <returns>
    /// A successful result carrying the profile, or a successful result whose value is
    /// <see langword="null"/> when no such account exists within the tenant. A defined property
    /// that the account has never filled in is present in the projection with an absent value
    /// rather than missing from it, so a client can render the full form from one read. This
    /// member documents no failure code.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Serves both the editable profile screen and the read-only profile view. Profile values
    /// are stored as a row set keyed by profile property definition rather than as columns, which
    /// is why they are not members of <see cref="UserDetailDto"/> and why the address and
    /// telephone fields the legacy account grid displayed are profile values rather than account
    /// columns.
    /// </para>
    /// <para>
    /// The empty string is preserved and never normalised. The legacy string sentinel <em>is</em>
    /// the empty string, so a stored blank and a value that was never supplied were
    /// indistinguishable once read through the legacy path. This contract keeps them distinct -
    /// absent means "no value has been stored", the empty string means "a blank value has been
    /// stored" - and an implementation must not convert either into the other, because the
    /// difference is observable by any client that renders the field.
    /// </para>
    /// </remarks>
    Task<Result<UserProfileDto>> GetProfileAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an account's profile values with the supplied set.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose profile is written.</param>
    /// <param name="profile">
    /// The complete set of profile values to store. This is a replace-the-set operation: the
    /// supplied values become the account's profile, so a value omitted from the set is cleared
    /// rather than left as it was.
    /// </param>
    /// <param name="cancellationToken">Token observed while the profile is written.</param>
    /// <returns>
    /// A successful result with no value. Documented failure codes are <c>user.not-found</c>;
    /// <c>user.profile.unknown-property</c>, when the set names a property the tenant does not
    /// define; <c>user.profile.required-property-missing</c>, when a property marked as required
    /// carries no value; <c>user.profile.value-too-long</c>, when a value exceeds the length its
    /// definition declares; and <c>user.profile.property-validation-failed</c>, when a value
    /// fails the validation expression its definition declares.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces the legacy profile update at <c>Website/admin/Users/Profile.ascx.vb</c> L226,
    /// which likewise handed the whole property collection across in one call. Replace-the-set is
    /// the deliberate shape: a per-property mutation member would force the API layer to compute
    /// the difference between the stored profile and the submitted one, which is a business
    /// decision and belongs in this layer.
    /// </para>
    /// <para>
    /// The three validation codes are the three rules a profile property definition actually
    /// carries - required, declared length and a validation expression - so they are preserved
    /// rather than invented, and they are enforced here because they depend on tenant data that
    /// a static request validator cannot see.
    /// </para>
    /// </remarks>
    Task<Result> UpdateProfileAsync(
        int portalId,
        int userId,
        UserProfileDto profile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the profile property definitions a tenant declares, in their configured display
    /// order.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose definitions are listed.</param>
    /// <param name="cancellationToken">Token observed while the definitions are read.</param>
    /// <returns>
    /// A successful result carrying the definitions, which is an empty sequence - never
    /// <see langword="null"/> and never a failure - when the tenant declares none. This member
    /// documents no failure code.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces the tenant-scoped definition read at
    /// <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L133, which returned the untyped,
    /// pre-generics collection type. This is the catalogue endpoint: the profile screen consumes
    /// it to render its form, and the definition management screen consumes it as its grid.
    /// The sequence is ordered by the display-order column so the client never has to sort it.
    /// </para>
    /// <para>
    /// The result is deliberately not paged. The legacy screen listed every definition on one
    /// page with no pager at all, and a tenant declares definitions in the tens, so introducing
    /// paging would add a contract the legacy application never had.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<ProfilePropertyDefinitionDto>>> ListProfilePropertyDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single profile property definition within a tenant.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that declares the definition.</param>
    /// <param name="propertyDefinitionId">Identifier of the definition to read.</param>
    /// <param name="cancellationToken">Token observed while the definition is read.</param>
    /// <returns>
    /// A successful result carrying the definition, or a successful result whose value is
    /// <see langword="null"/> when no such definition exists within the tenant. This member
    /// documents no failure code.
    /// </returns>
    /// <remarks>
    /// Replaces the single-definition read the legacy editor performed at
    /// <c>Website/admin/Users/EditProfileDefinition.ascx.vb</c> L110, which passed both the
    /// definition identifier and the tenant identifier, so tenant scoping is preserved exactly
    /// rather than added.
    /// </remarks>
    Task<Result<ProfilePropertyDefinitionDto>> GetProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Declares a new profile property definition for a tenant.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that will declare the definition.</param>
    /// <param name="definition">
    /// The definition to declare. Its identifier member is ignored on this path, because the
    /// identifier is assigned by the store.
    /// </param>
    /// <param name="cancellationToken">Token observed while the definition is declared.</param>
    /// <returns>
    /// A successful result carrying the definition as persisted, including its assigned
    /// identifier, which is what lets the API layer answer 201 Created. The documented failure
    /// code is <c>profile-definition.duplicate-name</c>, when the tenant already declares a
    /// property of that name.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces the add path of the legacy definition editor at
    /// <c>Website/admin/Users/EditProfileDefinition.ascx.vb</c> L451, which chose between adding
    /// and updating by testing the identifier against the integer sentinel at L449 - the very
    /// coupling of "absent" to a magic number that this contract removes by taking the identifier
    /// as a separate parameter on the update member instead.
    /// </para>
    /// <para>
    /// The duplicate-name code preserves a measured outcome rather than inventing one: the legacy
    /// add returned a value below the sentinel to signal a name collision, and the screen turned
    /// that into its duplicate-name message at L453 through L455. Encoding an error in the
    /// returned identifier is exactly the idiom a result with a code replaces.
    /// </para>
    /// </remarks>
    Task<Result<ProfilePropertyDefinitionDto>> CreateProfilePropertyDefinitionAsync(
        int portalId,
        ProfilePropertyDefinitionDto definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing profile property definition, including its position in the display
    /// order.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that declares the definition.</param>
    /// <param name="propertyDefinitionId">Identifier of the definition to update.</param>
    /// <param name="definition">The new state of the definition.</param>
    /// <param name="cancellationToken">Token observed while the definition is updated.</param>
    /// <returns>
    /// A successful result carrying the definition as persisted, which is what lets the API layer
    /// answer 200 OK. Documented failure codes are <c>profile-definition.not-found</c>,
    /// <c>profile-definition.duplicate-name</c> and <c>persistence.conflict</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces the update path of the legacy definition editor at
    /// <c>Website/admin/Users/EditProfileDefinition.ascx.vb</c> L459. Reordering needs no member
    /// of its own: the legacy grid moved a property by swapping the display-order values of two
    /// definitions and persisting each through the same update call, at
    /// <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L295, so the display order is simply
    /// a member of the definition being updated here.
    /// </para>
    /// </remarks>
    Task<Result<ProfilePropertyDefinitionDto>> UpdateProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        ProfilePropertyDefinitionDto definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a profile property definition from a tenant, together with the values accounts
    /// hold for it.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that declares the definition.</param>
    /// <param name="propertyDefinitionId">Identifier of the definition to remove.</param>
    /// <param name="cancellationToken">Token observed while the definition is removed.</param>
    /// <returns>
    /// A successful result with no value, which is what lets the API layer answer 204 No Content.
    /// The documented failure code is <c>profile-definition.not-found</c>.
    /// </returns>
    /// <remarks>
    /// Replaces the two legacy delete paths, both of which called the same underlying operation:
    /// the grid's row command at <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L161 and
    /// the editor's delete command at
    /// <c>Website/admin/Users/EditProfileDefinition.ascx.vb</c> L302. Removal discards the
    /// per-account values recorded against the definition in the same unit of work, so no value
    /// is left referencing a definition that no longer exists.
    /// </remarks>
    Task<Result> DeleteProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);
}
