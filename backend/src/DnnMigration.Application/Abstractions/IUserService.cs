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
// MIGRATION:      clear text as [Password] nvarchar(20) NOT NULL in the baseline schema, and the
// MIGRATION:      later membership store was configured for reversible storage with retrieval
// MIGRATION:      enabled, over signing material held in source control. Neither that material
// MIGRATION:      nor its location is reproduced anywhere in this tree. The authentication service owns
// MIGRATION:      bounded first-login migration; recovery here is one-way hashing plus administrative
// MIGRATION:      reset as the fallback.
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
// MIGRATION: (08) THIS CONTRACT OWNS THE ADMINISTRATIVE FALLBACK OF THE CREDENTIAL MIGRATION PATH.
// MIGRATION:      The authentication service owns the primary cut-over path: an opt-in,
// MIGRATION:      bounded ILegacyCredentialVerifier checks the external membership row and a
// MIGRATION:      successful presentation is immediately replaced with BCrypt. ResetPasswordAsync
// MIGRATION:      remains mandatory for rows that cannot be verified, for owners who do not sign in
// MIGRATION:      during the migration window, and after the legacy verifier is disabled. Neither
// MIGRATION:      path exposes plaintext or reproduces password retrieval.
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
//
// MIGRATION: (22) The member-services panel is SELF-SERVICE and belongs to this contract, not to
// MIGRATION:      the role contract. Website/admin/Users/MemberServices.ascx.vb is one of the nine
// MIGRATION:      account screens AAP 0.5.1.4 maps into the account resource, and it operated on
// MIGRATION:      the SIGNED-IN account rather than on the account its container was managing:
// MIGRATION:      DataBind at L150, Subscribe at L106, UseTrial at L125 and cmdRSVP_Click at L413
// MIGRATION:      every one of them pass UserInfo.UserID, which PortalModuleBase.vb L319-L323
// MIGRATION:      resolves as UserController.GetCurrentUserInfo, while the subject account the
// MIGRATION:      container assigned at ManageUsers.ascx.vb L517 is never read. The container
// MIGRATION:      hid the tab whenever an administrator reached the screen at all - DisplayServices
// MIGRATION:      at L61-L66 is the tenant's Profile_ManageServices setting AND NOT (IsEdit Or
// MIGRATION:      User.IsSuperUser), and IsEdit at UserModuleBase.vb L329-L340 is the
// MIGRATION:      administrative "ctl=Edit" entry point. So the five members below are addressed by
// MIGRATION:      account and the API gates every one of them on account ownership alone.
// MIGRATION:      An earlier revision of this contract, and of the role contract beside it, recorded
// MIGRATION:      that self-service subscription "maps onto the two membership actions" of the role
// MIGRATION:      resource and needed no surface of its own. That is withdrawn. Those two actions are
// MIGRATION:      the right PRIMITIVES and are reused verbatim - the implementation delegates to them
// MIGRATION:      so the expiry derivation, the protected-assignment refusals and the
// MIGRATION:      expire-rather-than-delete rule keep exactly one implementation - but they are gated
// MIGRATION:      on tenant administration, so an account holder could never reach them; and three of
// MIGRATION:      the five operations here are not assignments at all. The catalogue read, the trial
// MIGRATION:      predicate and the invitation-code redemption have no counterpart on that resource.
//
// MIGRATION: (23) The paid-subscription payment redirect is NOT ported, and the refusal is explicit.
// MIGRATION:      Subscribe at L113 and L115, and UseTrial at L131, each ended in
// MIGRATION:      Response.Redirect("~/admin/Sales/PayPalSubscription.aspx?…") whenever the role's
// MIGRATION:      terms required payment. AAP 0.2.2.4 excludes sales administration, so no member
// MIGRATION:      below completes such an operation: the catalogue still reports the offer the legacy
// MIGRATION:      screen displayed and says that payment is required, and the write members refuse it
// MIGRATION:      with a stable code. A free trial on a paid role remains fully performable, because
// MIGRATION:      the legacy trial gate is the TRIAL fee rather than the service fee.

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
/// <b>What this contract owns.</b> It is the single entry point for the eight client routes
/// that administer accounts: the account list, account creation, the account editor, the
/// profile editor, the credential screen, the account's own member-services catalogue, the tenant
/// membership settings and the profile
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
/// rotation and session termination belong to the sibling authentication and token contracts in
/// this folder, as do both replacements a successful sign-in may perform: a current-scheme work-factor
/// upgrade and the bounded legacy-to-BCrypt migration. This contract covers self-service changes and the
/// administrative-reset fallback only. Nothing from
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
/// <see langword="null"/> after confirming success, and typically maps it to a 404. Every read
/// that can answer "absent" is therefore typed with a nullable payload,
/// <c>Result&lt;TDto?&gt;</c>, so the compiler holds the contract this paragraph states rather
/// than leaving it to prose. A create or an update keeps a non-nullable payload, because it
/// either produced a record or failed, and the collection reads keep one too, because they
/// promise an empty sequence rather than an absent one.
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
    /// <c>user.list.filter-invalid</c>, when more than one of the account-name,
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
    /// <para>
    /// The page coordinates and the paging contract's own free-text filter are bounded by the
    /// shared <c>PagedRequestValidator</c>. The sort field is bounded HERE, and for this one
    /// collection the permitted set is EMPTY: a named ordering is refused with
    /// <c>user.list.sort-unsupported</c>. The page is selected by the database under a fixed order,
    /// and three of the ten columns the legacy account grid bound - the creation date, the
    /// last-login date and the approval flag - are not columns of this database at all but values
    /// read from the external membership store after the page has been chosen, so no consistent
    /// caller-chosen ordering exists to offer. <c>SortableFields.Users</c> records the measurement,
    /// and a caller that must order accounts asks for an unpaged answer and orders it client-side.
    /// Three of the grid's header names also differ from the projection's property names and the
    /// projection's are authoritative, because they are what a caller reads back. The four filter
    /// arguments declared on this member are distinct from that free-text filter and are bounded by
    /// this member's own implementation.
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
    /// Reads one page of a tenant's accounts as an account PICKER needs them: the key to submit and
    /// the two captions an option shows, and nothing else.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant whose accounts are offered. Enforces tenant isolation: an account
    /// belonging to another tenant is not a choice here, whatever the caller's own standing.
    /// </param>
    /// <param name="page">
    /// Page coordinates, the optional ordering, and the optional name filter carried by the paging
    /// contract's own free-text member. Bounded by <c>UserChoicePagedRequestValidator</c> at the
    /// boundary and re-checked here, so a caller reaching this member without passing through
    /// validation cannot ask for an unbounded page.
    /// </param>
    /// <param name="cancellationToken">Token observed while the page is read.</param>
    /// <returns>
    /// A successful result carrying the requested page, which is an empty page - never a failure and
    /// never a <see langword="null"/> value - when no account matches or when the tenant holds none.
    /// The one documented failure code is <c>user.choices.sort-unsupported</c>, when the request names
    /// an ordering that is not one of the two captions the choice carries.
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠ WHY THIS IS NOT <see cref="ListUsersAsync"/> WITH A THINNER PROJECTION. A performance and
    /// privacy review measured the role-assignment screen filling its account drop-down, and its
    /// account count probe, from the account LISTING. Every candidate row carried a postal address, a
    /// telephone number, an electronic-mail address, a creation instant, a last-login instant and the
    /// approval, lockout, online and super-user flags out of the database and into browser memory so
    /// that three values could be rendered - and the drop-down is permitted to enumerate a tenant of up
    /// to a thousand accounts. Being authorised to read the account grid is not a licence to receive
    /// fields the asking screen has no use for.
    /// </para>
    /// <para>
    /// The saving is not only bytes on the wire. The listing reads the tenant's account-policy settings
    /// to decide which columns it may publish, reads the tenant's profile-property definitions, issues a
    /// batched profile-value read for the address and telephone columns, and reads the portal itself to
    /// learn which account it may not offer for deletion. A picker needs none of those four reads, and
    /// this member performs none of them.
    /// </para>
    /// <para>
    /// MIGRATION: this is <c>cboUsers</c> on <c>Website/admin/Security/securityroles.ascx</c>, filled by
    /// <c>UserModuleBase.vb:L178-L186</c>. That code read the tenant's account count first and offered
    /// the drop-down only at or below one thousand accounts, offering a name box above it - which is why
    /// the count is worth a request of its own, and why the cheapest possible count request is the one a
    /// caller should be able to make. Asking this member for a single row and reading the envelope's
    /// total is that request.
    /// </para>
    /// </remarks>
    Task<Result<PagedResult<UserChoiceDto>>> ListAccountChoicesAsync(
        int portalId,
        PagedRequest page,
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
    // MIGRATION: the payload is nullable because this read can legitimately answer "absent",
    // which the returns clause above has always stated. Declaring it non-nullable made the
    // compiler contradict the contract: a caller acting on the documented null had to defeat
    // the annotation to do so, and an implementation returning one did the same.
    Task<Result<UserDetailDto?>> GetUserAsync(
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
    /// <b>Deletion must also revoke every refresh token the account held</b>, through
    /// <see cref="ITokenService.RevokeAllRefreshTokensAsync"/>, as part of the same request. A refresh
    /// token outliving the account it names is the worst of the three cases this obligation covers:
    /// the account is gone, so nothing remains that an administrator could inspect or disable, and yet
    /// the token would still be exchanged for access tokens asserting an identity that no longer
    /// exists. Revoking is therefore part of deleting rather than a follow-up to it.
    /// </para>
    /// <para>
    /// <b>It is the FIRST destructive step, and it cannot be enrolled in the unit of work.</b> The
    /// session store is not a relational participant, so no transaction spans it and the cascade
    /// together; an implementation that treated it as one would be describing a guarantee it does not
    /// have. What is achievable is ordering, and it is required: the revocation is attempted once every
    /// guard has passed and before anything has been removed, so a revocation that cannot be written
    /// abandons the deletion with the account wholly intact rather than half dismantled. Reversing the
    /// order would mean reporting failure over an account whose grants, assignments and credential had
    /// already gone.
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
    /// Changes an account's credential as a self-service change, presenting and verifying the
    /// current credential.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose credential is changing.</param>
    /// <param name="request">
    /// The credential change. The preserved policy - minimum length seven, no required
    /// non-alphanumeric character - is applied declaratively by the matching validator before this
    /// member is reached. Its operation discriminator no longer SELECTS anything: the two operations
    /// are two members with two endpoints and two authorisation policies, so a discriminator naming
    /// the other operation is refused rather than honoured.
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
    /// <b>THIS MEMBER NO LONGER PERFORMS AN ADMINISTRATIVE RESET.</b> One member serving both operations
    /// meant the caller's own request body chose whether the current credential was verified, on an
    /// endpoint that required nothing beyond authentication - so any bearer token could overwrite any
    /// account's credential in any tenant. The reset now lives on
    /// <see cref="ResetPasswordAsync"/>, whose endpoint requires administration of the account's own
    /// portal, while this member is reachable only by the account holder and always verifies the
    /// credential presented. <c>user.password.reset-not-enabled</c> is consequently unreachable here.
    /// </para>
    /// <para>
    /// <see cref="ResetPasswordAsync"/> owns the administrative fallback of the credential migration
    /// path. The authentication service may verify a bounded legacy representation during the
    /// explicitly enabled migration window and immediately replace it with BCrypt; this member
    /// handles every row that cannot take that path. Two members of the legacy status enumeration,
    /// both concerning the recovery answer and question, are unreachable here because that pair is
    /// omitted.
    /// </para>
    /// <para>
    /// <b>A successful change must revoke every refresh token the account holds</b>, through
    /// <see cref="ITokenService.RevokeAllRefreshTokensAsync"/>, in the same request. This is an
    /// obligation on the implementation rather than an option: a credential that has been changed
    /// because it may have been compromised, or reset because its holder lost it, is of no use to
    /// whoever had it - but a refresh token issued under the old credential keeps yielding new access
    /// tokens indefinitely, so a change that left one exchangeable would not end the very session it
    /// was performed to end. The revocation applies to the whole account, not to the caller's own
    /// session, so an administrative reset ends the sessions of the account being reset. Access
    /// tokens already issued cannot be recalled and are not attempted; the window they leave is
    /// bounded by their own short lifetime, and no successor follows them.
    /// </para>
    /// <para>
    /// <b>It precedes the credential write, and its failure abandons the operation.</b> Ending the
    /// sessions first and then failing costs the holder an inconvenience it can undo by signing in
    /// again; writing the credential first and then failing to end them would report the operation as
    /// failed while the credential had in fact been replaced and every session was still exchangeable -
    /// a falsehood to the caller as well as the exposure the revocation exists to close. The reported
    /// failure is a dependency failure rather than a bad request, because nothing about the submission
    /// was wrong and a caller may retry it.
    /// </para>
    /// <para>
    /// This has no legacy counterpart, because the legacy credential change had nothing to revoke.
    /// <c>UserController.vb</c> L103 changed the stored credential and returned, leaving the forms
    /// ticket the browser already held entirely untouched and valid for the remainder of its
    /// configured window. Ending the sessions is net-new strengthening, recorded as such.
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
    /// Resets an account's credential administratively, without presenting the current one.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose credential is being reset.</param>
    /// <param name="request">
    /// The new credential. The current credential is neither required nor consulted, which is the whole
    /// point of a reset; the same declarative policy applies to the new value as for a change.
    /// </param>
    /// <param name="cancellationToken">Token observed while the credential is written.</param>
    /// <returns>
    /// A successful result with no value. <b>It never carries a credential.</b> Documented failure codes are
    /// those of <see cref="ChangePasswordAsync"/> less <c>user.password.current-incorrect</c>, which cannot
    /// arise, plus <c>user.password.reset-not-enabled</c> when the deployment has reset switched off.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>WHY THIS IS A SEPARATE MEMBER.</b> Skipping the current-credential check is exactly what a reset
    /// is for, and it is safe only while something else establishes that the caller is entitled to perform
    /// one. When the two operations shared a member, that something was a discriminator in the caller's own
    /// request body on an endpoint requiring only authentication - so the check was skipped on the caller's
    /// instruction. Separating them lets the endpoint carrying this member require administration of the
    /// account's portal, which is the compensating control the absence of a credential check depends on.
    /// </para>
    /// <para>
    /// <b>The new credential is never returned.</b> The legacy reset at <c>UserController.vb</c> L906
    /// assigned the provider's answer onto the account and returned it, so the credential travelled back to
    /// the caller in clear text. That is not reproduced in any form, and there is no retrieval member at all.
    /// </para>
    /// <para>
    /// This member owns the administrative fallback of credential migration. The first-login path belongs to
    /// <see cref="IAuthService"/> and is available only inside its bounded compatibility window; reset remains
    /// the route after that deadline, for an unsupported representation, or when the owner no longer knows
    /// the credential. That is why the shipped default keeps reset enabled, faithfully to
    /// <c>Website/release.config</c> L240.
    /// </para>
    /// </remarks>
    Task<Result> ResetPasswordAsync(
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
    /// <b>Withdrawing an approval must revoke every refresh token the account holds</b>, through
    /// <see cref="ITokenService.RevokeAllRefreshTokensAsync"/>, in the same request. Withdrawal ends
    /// the account's right to sign in, and an account that may not sign in must not retain the means
    /// to keep obtaining access tokens: a refresh token issued while the approval stood would go on
    /// yielding them, so the withdrawal would take effect for new sign-ins and not for the session
    /// already running. Granting an approval revokes nothing, because it takes nothing away.
    /// </para>
    /// <para>
    /// <b>The revocation precedes the withdrawal, and its failure leaves the approval standing.</b> The
    /// same ordering rule the credential change follows, for the same reason: an account recorded as
    /// unapproved while every session it holds remains exchangeable is the outcome this obligation
    /// exists to prevent, so the withdrawal is not attempted until the sessions have ended.
    /// </para>
    /// <para>
    /// This too is net-new. <c>Website/admin/Users/Membership.ascx.vb</c> L243 cleared the flag and
    /// persisted the account, and the forms ticket the withdrawn account's browser held stayed valid
    /// for the rest of its window - the legacy screen had no mechanism with which to end it.
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
    // MIGRATION: nullable for the reason the returns clause gives, which is the most
    // consequential instance of it on this contract: the legacy reader assigned its result only
    // inside a not-nothing guard, so an installation without the User Accounts module yielded
    // nothing and the screens fell back to their defaults. The annotation is what stops a
    // caller treating that documented absence as impossible.
    Task<Result<MembershipSettingsDto?>> GetMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the tenant-level membership settings.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose settings are written.</param>
    /// <param name="request">
    /// The complete settings to store. Every member is written, so this is a replace-the-set
    /// operation rather than a partial patch, which matches the legacy screen's behaviour of
    /// re-writing each setting it manages on every update.
    /// </param>
    /// <param name="cancellationToken">Token observed while the settings are written.</param>
    /// <returns>
    /// A successful result carrying what the write did BEYOND storing the policy - whether the
    /// display-name format changed, and how many accounts were consequently rewritten. Three failure
    /// codes are documented.
    /// <c>user.membership-settings.storage-conflict</c> is raised when the tenant has no settings
    /// source to write to - the same condition that makes the read above answer with a
    /// <see langword="null"/> value; absence is a legitimate answer to a read and an
    /// impossibility for a write, which is why the two members treat it differently.
    /// <c>user.membership-settings.redirect_not_in_portal</c> is raised when one of the three
    /// redirect members names a page the tenant does not own.
    /// <c>user.display-name.too-long</c> is raised when the submitted display-name format would
    /// compose a name longer than the stored column for at least one account in the tenant; the
    /// policy is then NOT stored, because a format that breaks the accounts it governs is worse
    /// than a refusal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Included because the screen genuinely writes: <c>Website/admin/Users/UserSettings.ascx.vb</c>
    /// handles its update command at L163 and persists each value through a module-setting write
    /// at L183. Exposing only a read would have left that screen unimplementable, so this member
    /// exists on measured evidence rather than for symmetry.
    /// </para>
    /// <para>
    /// THE WRITE CONTRACT IS ITS OWN TYPE, and not the projection the read returns. The two are not the
    /// same act: the read DERIVES the accounts-display choice from the tenant's account count when the
    /// tenant stores none, and it is documented as never omitting a member, whereas a write states each
    /// value explicitly and is subject to field rules. Binding the response projection as a request body -
    /// which this member previously accepted - meant twenty-three administrative values reached the store
    /// with no rule applied to any of them, because no validator existed for that type and none could be
    /// registered for it without also firing on every read.
    /// </para>
    /// <para>
    /// PAGE OWNERSHIP IS CHECKED HERE RATHER THAN BY A FIELD RULE. Whether a page identifier belongs to
    /// this tenant is a data question, so the declarative rules bound the ranges and the widths and this
    /// member refuses a redirect target the tenant does not own - reproducing the legacy page picker, which
    /// could only offer the portal's own pages (<c>UserSettings.ascx.vb:L80-L82</c>).
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// MIGRATION (24): CHANGING THE DISPLAY-NAME FORMAT REWRITES EVERY ACCOUNT IN THE TENANT, and this
    /// member owns that sweep. The legacy screen compared the submitted format against the stored one and,
    /// when they differed, started a BACKGROUND THREAD running
    /// <c>UserController.UpdateDisplayNames</c> (<c>UserSettings.ascx.vb</c> L175-L182;
    /// <c>UserController.vb</c> L1259-L1268), which walked the tenant's accounts issuing one update each.
    /// Three properties of that arrangement are deliberately not reproduced: the operator was told
    /// nothing, a failure part way through left the tenant half formatted, and abandoning the request
    /// abandoned the sweep. Here the sweep is inside the same transaction as the policy write, so the two
    /// commit together or neither does, and the count is returned. An operator therefore waits where the
    /// legacy operator did not - which is the cost of an atomic outcome, and is recorded as a divergence
    /// rather than absorbed.
    /// </para>
    /// </remarks>
    Task<Result<MembershipSettingsUpdateResultDto>> UpdateMembershipSettingsAsync(
        int portalId,
        UpdateMembershipSettingsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates an electronic-mail address against the validation expression configured for one portal.
    /// </summary>
    /// <param name="portalId">The portal whose membership policy is applied.</param>
    /// <param name="email">The address to validate.</param>
    /// <param name="cancellationToken">Token observed while membership settings are read.</param>
    /// <returns>
    /// A successful result carrying the validation outcome. A malformed stored expression fails closed and
    /// reports <c>profile-definition.validation-expression-invalid</c>.
    /// </returns>
    /// <remarks>
    /// Shared by registration/account maintenance and the verified-registration approval path so the
    /// <c>Security_EmailValidation</c> setting has one implementation at every boundary that admits an
    /// account.
    /// </remarks>
    Task<Result<bool>> IsEmailValidAsync(
        int portalId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether the named account must complete or correct its profile before it may continue.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant the account is signing in to.</param>
    /// <param name="userId">Identifier of the account being admitted.</param>
    /// <param name="cancellationToken">Token observed while the two reads are made.</param>
    /// <returns>
    /// A successful result carrying <see langword="true"/> when the tenant requires a valid profile at
    /// sign-in AND the account leaves at least one required property empty; <see langword="false"/> in
    /// every other case, including a tenant with no settings source and an account that does not exist.
    /// This member reports a FACT rather than adjudicating a request, so it declares no failure code -
    /// an unanswerable question is answered <see langword="false"/>, which is the legacy outcome for it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// C-03: this member exists so that the sign-in path can evaluate the legacy profile-completeness
    /// gate WITHOUT the completeness rule acquiring a second implementation. The gate is
    /// <c>UserController.vb</c> L1189-L1193: while the accumulated outcome was still the valid member,
    /// the legacy read the per-tenant setting <c>Security_RequireValidProfileAtLogin</c> through
    /// <c>UserModuleBase.GetSetting</c> and, if it was set, called
    /// <c>ProfileController.ValidateProfile</c> (L305-L319), which walked the tenant's property
    /// definitions and reported invalid at the first definition that was required and whose value was
    /// empty. Both halves are reproduced here.
    /// </para>
    /// <para>
    /// The member lives on THIS contract rather than on the sign-in contract because both halves belong
    /// to the account-administration vertical: the setting is a module setting on the tenant's User
    /// Accounts module instance, which this service already reads, and the definitions are this
    /// service's own reference data. An earlier revision recorded that as the reason for leaving the gate
    /// unevaluated; the reason was sound but the conclusion was not, because the owning vertical can
    /// expose the QUESTION without the sign-in path acquiring the RULE. That is what this member does,
    /// and it is why the rule still has exactly one implementation.
    /// </para>
    /// <para>
    /// A required property whose stored value is present but whitespace counts as empty, because
    /// <c>Null.NullString</c> is the empty string (Rule T7) and the legacy comparison against it could
    /// not distinguish the two; treating whitespace as an answer would let a space satisfy a required
    /// property.
    /// </para>
    /// </remarks>
    Task<Result<bool>> RequiresProfileCompletionAsync(
        int portalId,
        int userId,
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
    // MIGRATION: nullable to match the returns clause, which distinguishes two absences that
    // must not be conflated - an account that does not exist, reported as a null payload, and a
    // defined property the account has never filled in, which is present in the projection with
    // an absent value so a client can render the whole form from one read.
    Task<Result<UserProfileDto?>> GetProfileAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assembles everything this application holds about one account within one tenant into a single
    /// portability document.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant the export is scoped to.</param>
    /// <param name="userId">Identifier of the account being described.</param>
    /// <param name="cancellationToken">Token observed while the document is assembled.</param>
    /// <returns>
    /// A successful result carrying the document, or carrying <see langword="null"/> when the account is
    /// not a member of that tenant - the same absence convention as
    /// <see cref="GetUserAsync"/>, and for the same reason: an account that is not there is not an error.
    /// This member documents no failure code.
    /// </returns>
    /// <remarks>
    /// <para>
    /// PRIV-01. <b>It exists because nothing did.</b> A module's content could be exported and a subject's
    /// own personal data could not: the account record and the profile were readable through two separate
    /// endpoints, the role assignments through a third that is addressed by role rather than by account, and
    /// there was no single answer to "what do you hold about me". A security review recorded that as a
    /// portability gap.
    /// </para>
    /// <para>
    /// <b>It reveals nothing a caller could not already read, and that is a deliberate constraint on the
    /// design.</b> The endpoint is reachable by the account holder and by an administrator of the addressed
    /// tenant, and every member of the document is drawn from a projection one of those two is already
    /// entitled to. Portability is a matter of assembling what is held into one document a subject can take
    /// away - not of creating a route to something that was previously withheld, which would be a privacy
    /// regression wearing a privacy feature's name.
    /// </para>
    /// <para>
    /// <b>No secret is carried, including the one-way ones.</b> No password hash, format, salt, question or
    /// answer, and no session or token material. The document is handed to a caller who may save it to a
    /// disk, mail it or paste it into a support ticket, so a credential inside it is a credential in all of
    /// those places; a hash is still the material an offline guessing attack works against and a subject can
    /// do nothing with it. The credential FACTS a subject can act on - when it last changed, whether a change
    /// is required, whether the account is locked out - are carried, because those are account state.
    /// </para>
    /// <para>
    /// <b>One tenant only.</b> An account may belong to several tenants, administered by different people, so
    /// a document naming them all would disclose one tenant's membership to another tenant's administrator.
    /// An account holder belonging to several tenants exports from each.
    /// </para>
    /// <para>
    /// <b>It is audited.</b> Reading a subject's whole record out of the system is worth a record of its own,
    /// and the actor is the fact worth recording: an export taken by an administrator and one taken by the
    /// subject are different events, and only the audit trail distinguishes them afterwards. An
    /// implementation records the event whether the caller is the subject or an administrator.
    /// </para>
    /// <para>
    /// <b>Read-only.</b> It changes nothing, writes nothing and evicts no cache entry.
    /// </para>
    /// </remarks>
    // MIGRATION: NET-NEW, with no legacy antecedent. The legacy administration had no data-portability
    // affordance of any kind: Website/admin/Users/ViewProfile.ascx rendered a profile to a screen and no
    // legacy page, procedure or provider member assembled a subject's record for export.
    Task<Result<UserPersonalDataExportDto?>> ExportPersonalDataAsync(
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
    // MIGRATION: nullable to match the returns clause. Note the deliberate asymmetry with the
    // listing member above and the create and update members below, all of which keep a
    // non-nullable payload: a list promises an empty sequence and a write promises a record.
    Task<Result<ProfilePropertyDefinitionDto?>> GetProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Declares a new profile property definition for a tenant.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that will declare the definition.</param>
    /// <param name="request">
    /// The definition to declare. It carries no identifier member at all - the store assigns one -
    /// and no owning-portal member, because the tenant arrives as the parameter beside it.
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
    /// <para>
    /// MIGRATION: this member takes the CREATE request rather than the response projection. The
    /// terminal insert procedure <c>AddPropertyDefinition</c> (<c>04.06.00:L1101</c>) declares
    /// eleven parameters - the tenant, which arrives separately, and the ten members the request
    /// carries - and the module definition key among them is the member the update path does not
    /// have. Binding the response projection here made this member advertise an identifier and an
    /// owning portal it never read.
    /// </para>
    /// </remarks>
    Task<Result<ProfilePropertyDefinitionDto>> CreateProfilePropertyDefinitionAsync(
        int portalId,
        CreateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing profile property definition, including its position in the display
    /// order.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that declares the definition.</param>
    /// <param name="propertyDefinitionId">Identifier of the definition to update.</param>
    /// <param name="request">
    /// The new state of the definition. It carries no identifier member - the definition is named by
    /// the parameter beside it - and no module definition key, because the terminal update procedure
    /// does not write that column.
    /// </param>
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
    /// <para>
    /// MIGRATION: this member takes the UPDATE request rather than the response projection. The
    /// terminal procedure <c>UpdatePropertyDefinition</c> (<c>04.05.00:L1685</c>) declares ten
    /// parameters - the definition key, which arrives separately, and the nine members the request
    /// carries - and it notably DOES assign <c>PropertyName</c>, so a rename is a supported edit
    /// rather than a discarded one. The module definition key is absent because the procedure does
    /// not write it, and a caller that previously submitted one on this path had it silently
    /// ignored.
    /// </para>
    /// <para>
    /// MIGRATION: A WITHDRAWN DECLARATION READS AS ABSENT HERE, reported with the not-found code, exactly as
    /// it does from the single read and the tenant listing. Withdrawal is logical because stored answers
    /// reference the declaration, and this contract exposes no member that reads, restores or acknowledges a
    /// withdrawn one - so a declaration this contract will not show is a declaration it will not edit. The
    /// duplicate-name check is deliberately NOT subject to that rule, because the terminal unique index
    /// spans the tenant, the module definition and the name without including the withdrawal flag, so a
    /// withdrawn row still occupies its name.
    /// </para>
    /// </remarks>
    Task<Result<ProfilePropertyDefinitionDto>> UpdateProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        UpdateProfilePropertyDefinitionRequest request,
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
    /// Documented failure codes are <c>profile-definition.not-found</c>, when the tenant declares no
    /// such definition, and <c>persistence.conflict</c>, when a concurrent request removed or changed
    /// the declaration between the read and the commit.
    /// </returns>
    /// <remarks>
    /// Replaces the two legacy delete paths, both of which called the same underlying operation:
    /// the grid's row command at <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> L161 and
    /// the editor's delete command at
    /// <c>Website/admin/Users/EditProfileDefinition.ascx.vb</c> L302. Removal discards the
    /// per-account values recorded against the definition in the same unit of work, so no value
    /// is left referencing a definition that no longer exists.
    /// <para>
    /// MIGRATION: A WITHDRAWN DECLARATION READS AS ABSENT HERE TOO, reported with the not-found code, so
    /// every member of this contract agrees on which declarations exist. This is the operation where the
    /// former disagreement mattered most: removal is physical and discards the stored answers with it, so a
    /// declaration the contract refused to show was nonetheless removable through it, along with data no
    /// caller could have inspected first.
    /// </para>
    /// </remarks>
    Task<Result> DeleteProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the member-services catalogue an account sees: every role the tenant publishes for
    /// self-service subscription, on the terms it is offered, annotated with that account's own
    /// subscription state and with the commands the legacy panel would have offered for each.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant whose published services are listed. Always a real identifier -
    /// <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c> - so neither minus one nor zero means
    /// "unspecified".
    /// </param>
    /// <param name="userId">
    /// Identifier of the account whose subscription state annotates each row. The legacy panel always
    /// used the signed-in account, so the API admits nobody else; this parameter is explicit so the
    /// member is testable without a request in flight.
    /// </param>
    /// <param name="cancellationToken">Token observed while the catalogue is read.</param>
    /// <returns>
    /// A successful result carrying the catalogue, which is EMPTY - not absent - when the tenant
    /// publishes no services. Documented failure codes are <c>portal.not-found</c>,
    /// <c>user.not-found</c> and <c>user.service.disabled-forbidden</c>, the last when the tenant has
    /// switched self-service subscription off.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>MemberServices.ascx.vb</c> <c>DataBind</c> at L147-L157, whose data source is
    /// <c>GetRoles(PortalId, UserInfo.UserID)</c> at L94-L99 - that is,
    /// <c>RoleController.GetUserRoles(portalId, userId, includePrivate:False)</c>, which reaches
    /// <c>dataProvider.GetServices</c> (<c>DNNRoleProvider.vb:L481-L488</c>). The terminal statement
    /// selects <c>from Roles R where R.PortalId = @PortalId and R.IsPublic = 1</c>
    /// (<c>04.06.00.SqlDataProvider:L993-L1013</c>), so the catalogue is the tenant's PUBLIC roles and
    /// an unsubscribed offer appears exactly as a subscribed one does.
    /// </para>
    /// <para>
    /// The three per-row predicates the legacy markup bound - the command label, whether the
    /// subscription command was rendered and whether the trial command was - are computed by the
    /// implementation and travel as data, because they are business rules and Rule T2 keeps them
    /// beneath the transport. Each of them read the FULL role rather than the suppressed projection,
    /// and so does this member.
    /// </para>
    /// <para>
    /// The read is deliberately UNPAGED, because the read it replaces was: the legacy grid bound the
    /// whole result and hid itself when the count was zero (<c>:L154</c>). A tenant's set of public
    /// roles is small by construction - it is a published price list, not a data set.
    /// </para>
    /// <para>
    /// MIGRATION: the tenant's own switch is enforced HERE and is not merely advisory. The legacy
    /// container hid the whole tab when <c>Profile_ManageServices</c> was off
    /// (<c>ManageUsers.ascx.vb:L61-L66</c>), and in Web Forms an unrendered control was an unreachable
    /// handler. An HTTP resource has no tab to hide, so the setting becomes a refusal on this member
    /// and on the four write members below. Its default when the tenant stores no value is
    /// <see langword="true"/>, exactly as <c>UserModuleBase.vb:L146-L147</c> applied it.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<MemberServiceDto>>> ListMemberServicesAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes an account to one of the tenant's published services, or renews a subscription that
    /// has lapsed.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that publishes the service.</param>
    /// <param name="userId">Identifier of the account being subscribed.</param>
    /// <param name="roleId">
    /// Identifier of the role the service is expressed as. <b>Zero is a real key</b>:
    /// <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>.
    /// </param>
    /// <param name="cancellationToken">Token observed while the subscription is written.</param>
    /// <returns>
    /// A successful result with no value, which is what lets the API layer answer 204 No Content.
    /// Documented failure codes are <c>portal.not-found</c>, <c>user.not-found</c>,
    /// <c>role.not-found</c>, <c>user.service.disabled-forbidden</c> when the tenant has switched
    /// self-service subscription off, <c>user.service.not-offered-forbidden</c> when the role is not
    /// published for self-service, and <c>user.service.payment-required-forbidden</c> when completing
    /// the subscription would require taking payment.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>Subscribe(roleID, cancel:False)</c> (<c>MemberServices.ascx.vb:L101-L118</c>),
    /// which the grid reached under BOTH the subscribe and the renew command
    /// (<c>:L440-L442</c>) - one operation with two labels, which is why there is one member here and
    /// not two. Its gate is <c>objRole.IsPublic And objRole.ServiceFee = 0.0</c> (<c>:L105</c>), and
    /// the else-branch is the excluded payment redirect, so a fee-bearing role is refused rather than
    /// half served.
    /// </para>
    /// <para>
    /// The write itself is the ordinary role assignment, delegated so that the expiry derivation - the
    /// trial-versus-billing term selection, the six frequency codes and the perpetual one-time
    /// sentinel at <c>RoleController.vb:L503-L558</c> - keeps exactly one implementation. Neither date
    /// is caller-supplied: the legacy path took no date arguments at all and derived both from the
    /// role's stored terms.
    /// </para>
    /// <para>
    /// Idempotent in the way the legacy member was: subscribing an account that already holds the
    /// service revises its expiry rather than failing, which is precisely how a renewal was performed.
    /// </para>
    /// </remarks>
    Task<Result> SubscribeToServiceAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an account's subscription to one of the tenant's published services.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that publishes the service.</param>
    /// <param name="userId">Identifier of the account whose subscription is cancelled.</param>
    /// <param name="roleId">Identifier of the role the service is expressed as.</param>
    /// <param name="cancellationToken">Token observed while the cancellation is written.</param>
    /// <returns>
    /// A successful result with no value. When the subscription was expired rather than withdrawn -
    /// the paid-trial retention rule - the success carries the reason
    /// <c>role_assignment.expired_not_removed</c>, so a caller can say which happened. Documented
    /// failure codes are <c>portal.not-found</c>, <c>user.not-found</c>, <c>role.not-found</c>,
    /// <c>role_assignment.not-found</c> when the account does not hold the service,
    /// <c>role_assignment.protected</c> when the assignment may not be withdrawn at all,
    /// <c>user.service.disabled-forbidden</c>, <c>user.service.not-offered-forbidden</c> and
    /// <c>user.service.payment-required-forbidden</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>Subscribe(roleID, cancel:True)</c> (<c>MemberServices.ascx.vb:L101-L118</c>),
    /// reached under the unsubscribe command (<c>:L443-L445</c>). Note that the legacy gate is the
    /// SAME as the subscribe gate - the cancel path redirected a fee-bearing role to the payment page
    /// too, with <c>&amp;cancel=1</c> appended (<c>:L115</c>) - so this member refuses a fee-bearing
    /// role for the same excluded reason and not by oversight.
    /// </para>
    /// <para>
    /// The removal is the ordinary membership removal, delegated so that the expire-rather-than-delete
    /// rule (<c>RoleController.vb:L494-L496</c>, which back-dates the expiry when a paid trial has
    /// been consumed so the trial-used fact survives) and the two protected refusals - the portal
    /// administrator's own administrator membership, and any membership of the registered-members role
    /// - keep exactly one implementation.
    /// </para>
    /// </remarks>
    Task<Result> CancelServiceAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an account's free trial of one of the tenant's published paid services.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that publishes the service.</param>
    /// <param name="userId">Identifier of the account starting the trial.</param>
    /// <param name="roleId">Identifier of the role the service is expressed as.</param>
    /// <param name="cancellationToken">Token observed while the trial subscription is written.</param>
    /// <returns>
    /// A successful result with no value. Documented failure codes are <c>portal.not-found</c>,
    /// <c>user.not-found</c>, <c>role.not-found</c>, <c>user.service.disabled-forbidden</c> and
    /// <c>user.service.trial-not-offered-forbidden</c>, the last covering every reason the legacy
    /// panel would not have rendered the command: the service is free and so has no trial to take,
    /// its trial itself carries a fee, or this account has already consumed it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>UseTrial(roleID)</c> (<c>MemberServices.ascx.vb:L120-L133</c>). It is a separate
    /// member from the subscription above because the two gates genuinely differ: subscribing requires
    /// a zero SERVICE fee while trialling requires a zero TRIAL fee, so a paid service with a free
    /// trial can be trialled here even though it cannot be subscribed to. Folding them into one
    /// operation that chose between them by reading a body field would make the effect depend on the
    /// payload rather than on the address.
    /// </para>
    /// <para>
    /// The write is the same role assignment the subscription performs, because the legacy member
    /// called the same operation (<c>:L125</c>); which of the two terms governs the derived expiry is
    /// decided inside that operation from the role's trial frequency and the stored trial-used flag,
    /// exactly as <c>RoleController.vb:L521-L527</c> decided it. Nothing here computes a date.
    /// </para>
    /// <para>
    /// MIGRATION: the trial-used flag is never written, because the legacy application never wrote it
    /// either. The terminal <c>AddUserRole</c> and <c>UpdateUserRole</c> procedures
    /// (<c>04.00.04.SqlDataProvider</c>) set only the two dates, and no in-scope legacy code assigns
    /// <c>UserRoleInfo.IsTrialUsed</c> - it is read at <c>RoleController.vb:L494</c> and
    /// <c>:L515</c> and at <c>MemberServices.ascx.vb:L336</c> and assigned nowhere. Setting it here
    /// would be a new behaviour, and the consequence of not setting it - a trial that can be taken
    /// again - is the legacy behaviour and is recorded in <c>MIGRATION_NOTES.md</c> rather than
    /// silently corrected.
    /// </para>
    /// </remarks>
    Task<Result> StartServiceTrialAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a service invitation code, subscribing an account to every role of the tenant that bears
    /// it.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant whose roles are searched for the code.</param>
    /// <param name="userId">Identifier of the account being subscribed.</param>
    /// <param name="request">The submitted code.</param>
    /// <param name="cancellationToken">Token observed while the subscriptions are written.</param>
    /// <returns>
    /// A successful result naming the services the code enrolled the account in, never empty.
    /// Documented failure codes are <c>portal.not-found</c>, <c>user.not-found</c>,
    /// <c>user.service.disabled-forbidden</c>, <c>user.service.code-required</c> when the submission
    /// carries no code, and <c>user.service.code-not-matched</c> when no role bears it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>cmdRSVP_Click</c> (<c>MemberServices.ascx.vb:L397-L433</c>). Three properties of
    /// that handler are load-bearing and are preserved exactly.
    /// </para>
    /// <para>
    /// First, it searched EVERY role of the tenant - <c>GetPortalRoles(PortalSettings.PortalId)</c> at
    /// <c>:L407</c> - and applied neither the public-role test nor the fee test that the grid's own
    /// commands applied. An invitation code is precisely the bypass for a service that is not
    /// published, so narrowing the search to public or free roles here would break the feature's whole
    /// purpose. It follows that this member does not refuse a fee-bearing role: no payment was taken
    /// on the legacy path either.
    /// </para>
    /// <para>
    /// Second, the loop had NO early exit (<c>:L410-L420</c>), so one code legitimately enrolls an
    /// account in several services and the result carries a collection.
    /// </para>
    /// <para>
    /// Third, the comparison was an in-memory Visual Basic string equality with no
    /// <c>Option Compare Text</c> in the file, so it was ordinal and untrimmed. Both properties are
    /// preserved: widening the comparison would let a code match a role its issuer did not intend.
    /// Roles that carry no code are skipped, which the legacy guard <c>If code &lt;&gt; ""</c>
    /// (<c>:L403</c>) achieved indirectly - an absent <c>RSVPCode</c> reached the comparison as the
    /// EMPTY STRING rather than null.
    /// </para>
    /// <para>
    /// MIGRATION: an unmatched code is a FAILED outcome rather than a successful empty result, and an
    /// empty submission is refused rather than ignored. The legacy handler posted one of two fixed
    /// sentences and, for an empty box, posted nothing at all.
    /// </para>
    /// </remarks>
    Task<Result<RedeemServiceCodeResultDto>> RedeemServiceCodeAsync(
        int portalId,
        int userId,
        RedeemServiceCodeRequest request,
        CancellationToken cancellationToken = default);
}
