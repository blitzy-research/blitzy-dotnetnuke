// MIGRATION: this service replaces Library/Components/Users/UserController.vb, all 1,372 lines of
// which were Shared (static) members reached without an instance, together with the business rules
// that lived in the nine admin code-behinds under Website/admin/Users/. Every member below is an
// instance method reached through an injected interface.
//
// MIGRATION: the by-reference status idiom is gone. UserController.vb:L156 (create), L200 (delete),
// L433 (credential retrieval) and L638 (membership read) each mutated an argument and returned a
// status, so one call produced two answers that no single type tied together. Every member here
// returns a result: the persisted value on success, a code and a message on an expected failure.
//
// MIGRATION: nothing on this service returns, echoes, logs or reconstructs a credential. The legacy
// reset at UserController.vb:L906 assigned the provider's answer onto the account and returned it, so
// a clear-text credential travelled back to the caller; the retrieval member at L433 existed solely to
// hand one out. Neither is reproduced, and the recovery question-and-answer pair that guarded
// retrieval is omitted with them.
//
// MIGRATION: the legacy credential store was reversible - the membership provider was registered with
// passwordFormat="Encrypted" and enablePasswordRetrieval="true" at Website/release.config:L236-L246,
// with the key that decrypts every stored credential committed to source control at L89-L93. The
// target hashes one way through the domain-owned hashing abstraction, so a legacy credential cannot be
// verified against a stored hash at all. THE MIGRATION PATH IS THEREFORE THE ADMINISTRATIVE RESET ON
// THIS SERVICE, AND IT IS THE WHOLE PATH RATHER THAN A FALLBACK. An earlier revision of this note
// attributed the path to a re-hash on the first successful sign-in in the authentication service and
// cast the reset as a fallback for accounts that never sign in again; that is not achievable, because
// re-hashing needs a plaintext proven against the stored value and the stored value cannot be checked.
// What the authentication service does upgrade on sign-in is the cost of a value this scheme already
// produced - a working credential made stronger, never a legacy one recovered.
//
// MIGRATION: the legacy password policy is preserved verbatim rather than tightened - minimum length
// seven, no required non-alphanumeric characters, no question-and-answer requirement and electronic
// mail uniqueness not enforced. Tightening a policy during a migration locks out existing account
// holders, so any hardening is a separate, explicit decision.
//
// MIGRATION: the mail notifications the legacy switches controlled are dropped with the excluded mail
// subsystem, which is why the notify flags on the request contracts are accepted and unused. The
// deletion audit entry UserController.vb:L240 wrote survives as the API layer's structured request
// log rather than as a row in the excluded event-log store.
//
// MIGRATION: the ambient caller accessor is gone. GetCurrentUserInfo at UserController.vb:L381 read the
// acting account out of per-request state - HttpContext.Current.Items("UserInfo"), falling back to
// Thread.CurrentPrincipal.Identity when there was no request at all - and, on every miss, returned a
// NEWLY CONSTRUCTED empty account rather than nothing. That never-null contract is deliberately not
// carried forward: it made "nobody is signed in" indistinguishable from "an account with no identifier",
// so a caller that forgot to test the identifier silently operated as an anonymous ghost. The acting
// caller arrives here through the injected ICurrentUser abstraction, which reports absence as absence,
// and every member takes the identifiers it acts on as explicit arguments.
//
// MIGRATION: the ambient TENANT accessor is gone with it, and no PortalSettings type appears anywhere on
// this service. The legacy delete reached PortalController.GetCurrentPortalSettings at
// UserController.vb:L237 purely to label its audit entry, and that accessor read
// HttpContext.Current.Items("PortalSettings") - a mutable per-request composite whose ActiveTab callers
// could reassign mid-request. Every member here takes portalId as its first argument, so the tenant a
// write lands in is fixed by the call rather than by whatever the request pipeline last stored. No type
// from the presentation stack is reachable from this project at all: the layer graph gives Application a
// reference to Domain only, so the ambient accessor could not be restated here even by accident.
//
// MIGRATION: nine further legacy members are deliberately absent, and the omissions are recorded here
// rather than left to be discovered by their absence:
// MIGRATION:  - DeleteUsers L273 and DeleteUnauthorizedUsers L293, the bulk sweeps. A reachable
// MIGRATION:    "remove every account in this tenant" operation is a defect rather than a feature, and
// MIGRATION:    neither has an endpoint in the target API. Deletion is one account per call.
// MIGRATION:  - SetAuthCookie L919, whose body is EMPTY in this checkout. It wrote a forms-authentication
// MIGRATION:    cookie, which has no counterpart under bearer authentication; sign-in and token issue
// MIGRATION:    belong to the authentication and token contracts.
// MIGRATION:  - GeneratePassword L314 and L330. A generated credential has to be transmitted to be
// MIGRATION:    useful, which reintroduces exactly the disclosure that dropping retrieval removed.
// MIGRATION:  - GetUserCreateStatus L598, the status-to-message map. Its wording came from the excluded
// MIGRATION:    localisation mechanism; the status becomes a stable failure code on the result and the
// MIGRATION:    wording is authored in the client.
// MIGRATION:  - GetUserMembership L638. It was a Sub that mutated the account it was handed, so it
// MIGRATION:    reported nothing at all; the membership facts it fetched are materialised on the account
// MIGRATION:    by the repository read path instead.
// MIGRATION:  - GetOnlineUsers L415, excluded with the users-online subsystem and its purge job, and
// MIGRATION:    GetSuperUsers L1331, excluded with host-level administration.
// MIGRATION:  - UpdateDisplayNames L1259, a bulk maintenance sweep with no endpoint.
// MIGRATION:  - GetUserCountByPortal L582, omitted as redundant rather than excluded: the paged envelope
// MIGRATION:    the listing member returns already carries the tenant-wide total, so a second member
// MIGRATION:    answering the same question from a different query would be a second source of truth for
// MIGRATION:    one number.
// MIGRATION: The hydration and provider-synchronisation switches go with them - isHydrated, hydrateRoles,
// MIGRATION: ProgressiveHydration, SynchronizeUsers and AddToMembershipProvider - because the shape of a
// MIGRATION: response is settled by its data transfer object here, never by a caller-supplied switch.
//
// MIGRATION: the caching MEMBERS are dropped while the caching itself is not. GetCachedUser L350,
// SettingsKey L924, GetCacheKey L1310 and CacheKey L1315 have no counterpart, because a cache key is an
// implementation detail rather than part of an account service's surface. The 15 measured caching call
// sites are served by the domain-owned caching abstraction, invoked inside the members below, and the two
// stateful controller properties that configured the legacy type before a call - DisplayFormat L1210 and
// PortalId L1219 - are gone with them. This service holds no mutable state of any kind.
//
// MIGRATION: THE OPTION STRICT ASYMMETRY IS RESOLVED TOWARDS THE STRICTER SIDE. The class library was
// compiled with Option Strict ON (Library/DotNetNuke.Library.vbproj:L24 - note that L23 is OptionExplicit,
// so a citation of L23 for Option Strict is off by one), but the admin code-behinds this service absorbs
// its rules from were compiled with strict="false" (Website/release.config:L125) and could therefore rely
// on late binding and implicit narrowing that C# rejects outright. Every such conversion is made explicit
// here: text arriving from a stored setting is parsed with an invariant culture and an explicit fallback
// rather than coerced, so a value the legacy screen would have silently collapsed to 0 or "" is either
// parsed or refused. The read helpers at the foot of this file are where that happens, and the coercions
// whose result could differ from the legacy one are itemised in MIGRATION_NOTES.md. This is also why the
// nine code-behinds are treated as reference inputs for endpoint and screen semantics rather than as
// candidates for line-by-line translation.
using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Mapping;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Services;

/// <summary>
/// Orchestrates accounts within a tenant: the account list and its filters, the account record itself,
/// the credential transitions an administrator performs, the tenant-wide membership settings, and the
/// profile property definitions together with the values accounts hold for them.
/// </summary>
/// <remarks>
/// <para>
/// Every member is tenant-scoped. The two whole-installation reads the legacy controller offered are
/// deliberately not reachable, because a member that crosses tenants is how one tenant's administrator
/// reaches another tenant's accounts.
/// </para>
/// <para>
/// Account columns, credential state, role membership and profile values are four separate concerns
/// with four separate write paths. An ordinary column edit therefore cannot reinstate a locked account,
/// approve an unapproved one or grant a role as a side effect.
/// </para>
/// </remarks>
public sealed class UserService : IUserService
{
    /// <summary>
    /// Reported when more than one search filter is supplied, or when only one half of the
    /// profile-property pair is supplied.
    /// </summary>
    /// <remarks>
    /// The token deliberately reads <c>filter-invalid</c> rather than <c>filter-conflict</c>, and matches the
    /// name <c>PermissionService</c> already uses for the same situation. What this reports is a malformed
    /// query string: the caller must change the request, and no amount of retrying or re-reading resource
    /// state will help. A <c>conflict</c> token would be folded onto <c>409 Conflict</c> by the status
    /// translator - which exists so that <see cref="PersistenceConflictCode"/>, a genuine optimistic-concurrency
    /// failure, is reported as one - leaving a client unable to tell a request it must correct from a write
    /// another caller won and which is worth retrying.
    /// </remarks>
    /// <summary>Resource kind published on an audit record describing an account.</summary>
    /// <remarks>
    /// Spelled as the domain entity's own type name, so a reader of the trail can go from a record
    /// straight to the type that produced it.
    /// </remarks>
    private const string UserResourceType = "User";

    private const string ListFilterInvalidCode = "user.list.filter-invalid";

    /// <summary>
    /// Reported when the named profile property is not defined for the tenant.
    /// </summary>
    private const string ListUnknownProfilePropertyCode = "user.list.unknown-profile-property";

    /// <summary>
    /// Reported when a caller names an ordering the account listing cannot apply. Every ordering falls
    /// into that category: the page is selected by the database under a fixed order, and three of the ten
    /// columns the legacy grid bound live in the external membership store rather than in this database,
    /// so no consistent caller-chosen ordering exists to offer. <c>SortableFields.Users</c> records the
    /// measurement.
    /// </summary>
    private const string ListSortUnsupportedCode = "user.list.sort-unsupported";

    /// <summary>
    /// Reported when no such account exists within the tenant.
    /// </summary>
    private const string NotFoundCode = "user.not-found";

    /// <summary>
    /// Reported when the account name is already taken somewhere in the installation.
    /// </summary>
    private const string CreateUsernameAlreadyExistsCode = "user.create.username-already-exists";

    /// <summary>
    /// Reported when the account name already holds a membership of this tenant.
    /// </summary>
    private const string CreateUserAlreadyRegisteredCode = "user.create.user-already-registered";

    /// <summary>
    /// Reported when the credential store already holds a credential for the account name.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy creation status carried both this member and
    /// <see cref="CreateUsernameAlreadyExistsCode"/> because two different checks produced them - the
    /// application's own pre-check, and the membership provider's own duplicate detection. That
    /// distinction is preserved: the pre-check reports the former, the credential store the latter.
    /// </remarks>
    private const string CreateDuplicateUsernameCode = "user.create.duplicate-username";

    /// <summary>
    /// Reported when the electronic-mail address is already in use and the deployment requires
    /// addresses to be unique.
    /// </summary>
    private const string CreateDuplicateEmailCode = "user.create.duplicate-email";

    /// <summary>
    /// Reported when the submitted account name cannot be used.
    /// </summary>
    private const string CreateInvalidUsernameCode = "user.create.invalid-username";

    /// <summary>
    /// Reported when the submitted electronic-mail address cannot be used.
    /// </summary>
    private const string CreateInvalidEmailCode = "user.create.invalid-email";

    /// <summary>
    /// Reported when the submitted credential does not satisfy the configured policy.
    /// </summary>
    private const string CreateInvalidPasswordCode = "user.create.invalid-password";

    /// <summary>
    /// Reported when the submitted credential and its confirmation differ.
    /// </summary>
    private const string CreatePasswordMismatchCode = "user.create.password-mismatch";

    /// <summary>
    /// Reported when the tenant the account is being created in does not exist.
    /// </summary>
    private const string CreatePortalAssignmentFailedCode = "user.create.portal-assignment-failed";

    /// <summary>
    /// Reported when the credential store could not be written.
    /// </summary>
    private const string CreateProviderErrorCode = "user.create.provider-error";

    /// <summary>
    /// Reported when a concurrent edit changed the row first.
    /// </summary>
    private const string PersistenceConflictCode = "persistence.conflict";

    /// <summary>
    /// Reported when the account is the tenant's designated administrator.
    /// </summary>
    private const string DeleteAdministratorProtectedCode = "user.delete.administrator-protected";

    /// <summary>
    /// Reported when the account is a host-level account and beyond tenant administration.
    /// </summary>
    private const string DeleteSuperUserProtectedCode = "user.delete.superuser-protected";

    /// <summary>
    /// Reported when a credential the operation requires was not supplied.
    /// </summary>
    private const string PasswordMissingCode = "user.password.missing";

    /// <summary>
    /// Reported when the submitted credential does not satisfy the configured policy.
    /// </summary>
    private const string PasswordInvalidCode = "user.password.invalid";

    /// <summary>
    /// Reported when the submitted credential and its confirmation differ.
    /// </summary>
    private const string PasswordMismatchCode = "user.password.mismatch";

    /// <summary>
    /// Reported when the submitted credential is the one already stored.
    /// </summary>
    private const string PasswordNotDifferentCode = "user.password.not-different";

    /// <summary>
    /// Reported when the credential store refused the write.
    /// </summary>
    private const string PasswordResetFailedCode = "user.password.reset-failed";

    /// <summary>
    /// Reported when an operation that must end an account's sessions could not have them revoked, so the
    /// operation itself was abandoned rather than completed with the sessions left alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its reason token ends in <c>store_unavailable</c>, which the Api edge classifies as a dependency
    /// failure and answers <c>503</c>. That is the honest reading: the request was valid and the refusal
    /// is temporary, so a caller may retry it. Reporting it as a bad request would invite an administrator
    /// to change the request, and reporting success would tell them an account had been locked out of its
    /// sessions when it had not.
    /// </para>
    /// <para>
    /// The three operations that can report it are the credential change, the withdrawal of an approval and
    /// the deletion of an account - the same three the token contract names.
    /// </para>
    /// </remarks>
    private const string SessionRevocationFailedCode = "user.session.revocation_store_unavailable";

    /// <summary>
    /// Reported when an account's credential could not be removed during deletion, so the deletion was
    /// abandoned rather than committed with the credential left behind.
    /// </summary>
    /// <remarks>
    /// A credential surviving the account row is unreachable by any administrative screen and is revisited
    /// by no later deletion, so reporting success over one would be a permanent, invisible remnant. Its
    /// reason token ends in <c>store_unavailable</c> so the Api edge answers <c>503</c> and the caller
    /// learns the refusal is temporary.
    /// </remarks>
    private const string CredentialRemovalFailedCode = "user.credential.removal_store_unavailable";

    /// <summary>
    /// Reported when a self-service change presents the wrong current credential.
    /// </summary>
    private const string PasswordCurrentIncorrectCode = "user.password.current-incorrect";

    /// <summary>
    /// Reported when the deployment has administrative reset switched off.
    /// </summary>
    private const string PasswordResetNotEnabledCode = "user.password.reset-not-enabled";

    /// <summary>
    /// Reported for an unrecognised operation discriminator.
    /// </summary>
    private const string PasswordUnsupportedOperationCode = "user.password.unsupported-operation";

    /// <summary>
    /// Reported when the account is not currently locked.
    /// </summary>
    private const string UnlockNotLockedCode = "user.unlock.not-locked";

    /// <summary>
    /// Reported when an administrator invokes a membership transition on their own account.
    /// </summary>
    private const string MembershipSelfForbiddenCode = "user.membership.self-forbidden";

    /// <summary>
    /// Reported when a caller asks to change a credential that is not their own.
    /// </summary>
    /// <remarks>
    /// A change is proof of possession of the current credential and is therefore the account owner's
    /// operation. An administrator acting on another account uses the reset operation instead. The reason
    /// token classifies as 403 rather than 400, because the request is well formed and the caller simply may
    /// not make it.
    /// </remarks>
    private const string PasswordChangeSelfOnlyForbiddenCode = "user.password.change-self-only-forbidden";

    /// <summary>
    /// Reported when a caller without administrative authority over the tenant asks to reset a credential.
    /// </summary>
    /// <remarks>
    /// This is the check that keeps the reset operation from being a way around the change operation's
    /// current-credential requirement. Without it the owner of an account - who legitimately passes the route
    /// policy - could set a new credential without presenting the old one.
    /// </remarks>
    private const string PasswordResetForbiddenCode = "user.password.reset-forbidden";

    /// <summary>
    /// Reported when the account is already in the requested approval state.
    /// </summary>
    private const string ApprovalUnchangedCode = "user.approval.unchanged";

    /// <summary>
    /// Reported when the account already carries the forced-change flag.
    /// </summary>
    private const string PasswordChangeAlreadyRequiredCode = "user.password.change-already-required";

    /// <summary>
    /// Reported when the tenant has no settings source to write membership settings to.
    /// </summary>
    private const string MembershipSettingsSourceMissingCode = "user.membership-settings.source-missing";

    /// <summary>
    /// Reported when a submitted profile set names a property the tenant does not define.
    /// </summary>
    private const string ProfileUnknownPropertyCode = "user.profile.unknown-property";

    /// <summary>
    /// Reported when a property marked as required carries no value.
    /// </summary>
    private const string ProfileRequiredPropertyMissingCode = "user.profile.required-property-missing";

    /// <summary>
    /// Reported when a value exceeds the length its definition declares.
    /// </summary>
    private const string ProfileValueTooLongCode = "user.profile.value-too-long";

    /// <summary>
    /// Reported when a value fails the validation expression its definition declares.
    /// </summary>
    private const string ProfilePropertyValidationFailedCode = "user.profile.property-validation-failed";

    /// <summary>
    /// Reported when the tenant already declares a profile property of the submitted name.
    /// </summary>
    private const string ProfileDefinitionDuplicateNameCode = "profile-definition.duplicate-name";

    /// <summary>
    /// Reported when no such profile property definition exists within the tenant.
    /// </summary>
    private const string ProfileDefinitionNotFoundCode = "profile-definition.not-found";

    /// <summary>
    /// Cache key holding a tenant's projected profile property definitions, carried over verbatim from
    /// <c>DataCache.ProfileDefinitionsCacheKey</c> (DataCache.vb:L75).
    /// </summary>
    private const string ProfileDefinitionsCacheKeyFormat = "ProfileDefinitions{0}";

    /// <summary>
    /// Base expiry of the definition cache, matching <c>DataCache.ProfileDefinitionsCacheTimeOut</c>.
    /// </summary>
    private const int ProfileDefinitionsCacheTimeOutMinutes = 20;

    /// <summary>
    /// Page size that requests every match unpaged, per the repository contracts.
    /// </summary>
    private const int UnpagedPageSize = 0;

    /// <summary>
    /// Longest value <c>dbo.UserProfile.PropertyValue</c> can hold; longer values move to the
    /// unbounded text column beside it.
    /// </summary>
    private const int ProfileValueColumnLength = 3750;

    /// <summary>
    /// Number of accounts above which the legacy membership settings defaulted the account picker to a
    /// free-text control rather than a drop-down (UserModuleBase.vb:L178).
    /// </summary>
    private const int LargeTenantAccountThreshold = 1000;

    /// <summary>
    /// Legacy integer sentinel meaning "no page selected" in the three redirect settings.
    /// </summary>
    private const string UnsetRedirectSettingValue = "-1";

    /// <summary>
    /// Bound on how long a tenant-authored validation expression may run before it is abandoned.
    /// </summary>
    /// <remarks>
    /// A profile property's validation expression is tenant data rather than code, so a pathological
    /// expression must not be able to occupy a request thread indefinitely.
    /// </remarks>
    private static readonly TimeSpan ValidationExpressionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Profile property names the legacy account grid composed its address column from, in the order it
    /// composed them (Users.ascx.vb:L352, reaching the excluded <c>FormatAddress</c> helper).
    /// </summary>
    private static readonly string[] AddressProfilePropertyNames =
        ["Unit", "Street", "City", "Region", "Country", "PostalCode"];

    /// <summary>
    /// Profile property name the legacy account grid displayed as its telephone column.
    /// </summary>
    private const string TelephoneProfilePropertyName = "Telephone";

    private readonly IUserRepository _users;
    private readonly IUserProfileRepository _profiles;
    private readonly IRoleRepository _roles;
    private readonly IPermissionService _permissions;
    private readonly IPortalRepository _portals;
    private readonly IModuleRepository _modules;
    private readonly IModuleDefinitionRepository _definitions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;
    private readonly ICacheService _cache;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditSink _audit;
    private readonly ITokenService _tokens;
    private readonly PasswordPolicyOptions _passwordPolicy;
    private readonly CachingOptions _caching;

    /// <summary>
    /// Initialises the service with the collaborators it reaches the store, the credential policy and
    /// the cache through.
    /// </summary>
    /// <param name="users">Account, membership and credential persistence.</param>
    /// <param name="profiles">Profile property definitions and the values held against them.</param>
    /// <param name="roles">Role lookups, used for automatic enrolment and for role projection.</param>
    /// <param name="permissions">
    /// Permission grant cleanup on deletion, reached through the application contract that owns it rather
    /// than through the grant repository directly. The rule that only grants made DIRECTLY to the account
    /// are removed - grants reaching it through a role belong to the role and would strip every other
    /// holder - lives on that contract, and this service does not restate it.
    /// </param>
    /// <param name="portals">Tenant existence, the designated administrator and the account count.</param>
    /// <param name="modules">Module settings persistence, where membership settings are stored.</param>
    /// <param name="definitions">Definition lookups, used to locate the settings source module.</param>
    /// <param name="unitOfWork">The single commit point for every write below.</param>
    /// <param name="passwordHasher">One-way credential hashing and verification.</param>
    /// <param name="clock">The clock every timestamp is taken from.</param>
    /// <param name="cache">Cache reads and invalidation.</param>
    /// <param name="currentUser">The acting caller, needed by the self-service prohibitions.</param>
    /// <param name="audit">Receives the business audit record for a created or removed account.</param>
    /// <param name="tokens">
    /// Session revocation. Present because three operations below end an account's right to sign in, or
    /// change the credential by which it does so, and the token contract states in terms that each of them
    /// must revoke the account's refresh tokens within the same request. Without this collaborator that
    /// obligation was documented on every one of them and honoured by none.
    /// </param>
    /// <param name="passwordPolicy">Bound credential policy, preserved from the legacy configuration.</param>
    /// <param name="caching">Bound caching configuration supplying the performance multiplier.</param>
    /// <remarks>
    /// MIGRATION: the audit sink preserves the two account events the legacy site recorded,
    /// <c>USER_CREATED</c> and <c>USER_DELETED</c> (<c>EventLogController.vb:L39-L40</c>). The deletion
    /// entry is the one measured call site in the legacy account controller, at
    /// <c>UserController.vb:L240</c>, which passed the account name as the log key together with the
    /// account identifier and the ambient tenant.
    /// </remarks>
    public UserService(
        IUserRepository users,
        IUserProfileRepository profiles,
        IRoleRepository roles,
        IPermissionService permissions,
        IPortalRepository portals,
        IModuleRepository modules,
        IModuleDefinitionRepository definitions,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IClock clock,
        ICacheService cache,
        ICurrentUser currentUser,
        IAuditSink audit,
        ITokenService tokens,
        PasswordPolicyOptions passwordPolicy,
        CachingOptions caching)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _passwordPolicy = passwordPolicy ?? throw new ArgumentNullException(nameof(passwordPolicy));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
    }

    /// <summary>
    /// Records one committed account change on the audit trail.
    /// </summary>
    /// <param name="eventName">The stable event name, from <see cref="AuditEventNames"/>.</param>
    /// <param name="portalId">The tenant the change was made within.</param>
    /// <param name="subjectUserId">The account the change was made against.</param>
    /// <param name="properties">Short, non-sensitive descriptive facts about the change.</param>
    /// <remarks>
    /// <para>
    /// Called only after the change has been committed. The acting account comes from the credential, and
    /// it is not always the account acted upon: an administrator creating a member records the
    /// administrator as the actor and the member as the subject, a distinction the legacy record - which
    /// had a single user field - could not express.
    /// </para>
    /// <para>
    /// No credential material of any kind reaches an audit record from this service: not a submitted
    /// password, not a stored hash, not a reset token, and no answer to a security question.
    /// </para>
    /// </remarks>
    private void RecordAudit(
        string eventName,
        int portalId,
        int subjectUserId,
        IReadOnlyDictionary<string, string?> properties)
    {
        _audit.Record(new AuditEvent(eventName)
        {
            PortalId = portalId,
            ActorUserId = _currentUser.UserId,
            ActorUserName = _currentUser.UserName,
            SubjectUserId = subjectUserId,
            ResourceType = UserResourceType,
            ResourceId = subjectUserId.ToString(CultureInfo.InvariantCulture),
            Properties = properties,
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The three search filters are mutually exclusive because the legacy grid dispatched to exactly one
    /// search shape per postback, selected by a drop-down, and each branch appended a percent sign to
    /// the search text - so prefix matching is the preserved semantic and combining filters would be
    /// new behaviour. Supplying one half of the profile-property pair is refused for the same reason:
    /// silently ignoring the other half would answer a question the caller did not ask.
    /// </para>
    /// <para>
    /// The address and telephone columns the grid displayed are profile values rather than account
    /// columns, so they are composed here from the six address properties the legacy helper concatenated
    /// and from the telephone property. That costs one profile read per row of the page, which is the
    /// price of a column the legacy grid also assembled per row; a tenant that does not define those
    /// properties simply leaves both columns absent.
    /// </para>
    /// <para>
    /// Host-level accounts are excluded because this list serves tenant administration and a host
    /// account is beyond its reach, which is the same rule the deletion guard enforces. Unauthorised
    /// accounts are included, because the grid renders an authorised column and hiding them would make
    /// that column meaningless.
    /// </para>
    /// </remarks>
    public async Task<Result<PagedResult<UserListItemDto>>> ListUsersAsync(
        int portalId,
        PagedRequest page,
        string? userNameFilter = null,
        string? emailFilter = null,
        string? profilePropertyName = null,
        string? profilePropertyValue = null,
        bool? isApproved = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        EnsurePagingIsUsable(page);

        // MIGRATION: the PER-COLLECTION ordering set is enforced HERE, against this collection's own set
        // rather than against the union. The shared request validator applies nothing narrower than the
        // union of every collection's set, because one PagedRequest contract serves every listing, so it
        // would otherwise admit a portal-only or role-only field name here and the read would order by its
        // own sequence regardless - returning a page the caller cannot account for and cannot detect.
        // Refusing says so instead.
        //
        // SortableFields.Users holds exactly the names the underlying query can honour, which are the
        // mapped columns of the entity it pages over. It is deliberately NARROWER than the projection: the
        // creation instant, the last sign-in instant, the approval flag and the lock flag are read from the
        // external membership store AFTER the page has been taken, so ordering by one of them would order
        // the page rather than the collection - not an ordering at all. Those are refused here rather than
        // accepted and silently discarded. The set and the ordering the repository actually applies must
        // agree, and a test asserts that agreement in both directions.
        if (!SortableFields.IsPermittedFor(page.SortBy, SortableFields.Users))
        {
            return Result<PagedResult<UserListItemDto>>.Failure(
                ListSortUnsupportedCode,
                $"Accounts cannot be ordered by '{page.SortBy}'.");
        }

        EnsureFilterIsNotBlank(userNameFilter, nameof(userNameFilter));
        EnsureFilterIsNotBlank(emailFilter, nameof(emailFilter));
        EnsureFilterIsNotBlank(profilePropertyName, nameof(profilePropertyName));

        bool propertyNameSupplied = profilePropertyName is not null;
        bool propertyValueSupplied = profilePropertyValue is not null;

        if (propertyNameSupplied != propertyValueSupplied)
        {
            return Result<PagedResult<UserListItemDto>>.Failure(
                ListFilterInvalidCode,
                "The profile property name and value must be supplied together.");
        }

        int suppliedFilters = 0;
        if (userNameFilter is not null)
        {
            suppliedFilters++;
        }

        if (emailFilter is not null)
        {
            suppliedFilters++;
        }

        if (propertyNameSupplied)
        {
            suppliedFilters++;
        }

        if (suppliedFilters > 1)
        {
            return Result<PagedResult<UserListItemDto>>.Failure(
                ListFilterInvalidCode,
                "Only one of the account name, electronic-mail and profile property filters may be supplied.");
        }

        IReadOnlyList<ProfilePropertyDefinition> definitions =
            await _profiles.GetDefinitionsByPortalIdAsync(portalId, cancellationToken)
                .ConfigureAwait(false);

        int? profilePropertyDefinitionId = null;
        if (propertyNameSupplied)
        {
            ProfilePropertyDefinition? filtered = definitions.FirstOrDefault(candidate =>
                string.Equals(candidate.PropertyName, profilePropertyName, StringComparison.OrdinalIgnoreCase));

            if (filtered is null)
            {
                return Result<PagedResult<UserListItemDto>>.Failure(
                    ListUnknownProfilePropertyCode,
                    FormattableString.Invariant(
                        $"Portal {portalId} does not define a profile property named \"{profilePropertyName}\"."));
            }

            profilePropertyDefinitionId = filtered.PropertyDefinitionId;
        }

        // The sort field travels to the repository so the database orders BEFORE it skips and takes.
        // Re-ordering the returned page here would only re-order the rows this one page happened to
        // contain, which is how an accepted sort field comes to be silently ignored. The field itself has
        // already been checked against the names this collection honours - SortableFields.Users, applied
        // by the sealed UserPagedRequestValidator at the API boundary - so nothing is re-validated here.
        PagedResult<User> matches = await _users.ListAsync(
            portalId,
            page.PageIndex,
            page.PageSize,
            page.HasQuery ? page.Query : null,
            userNameFilter,
            emailFilter,
            profilePropertyDefinitionId,
            profilePropertyValue,
            isApproved,
            includeUnauthorised: true,
            includeSuperUsers: false,
            // "No ordering named" is passed as null rather than as whatever blank text arrived, so the
            // store receives one canonical representation of the absence instead of three - the same
            // normalisation the query filter above performs for the same reason.
            sortBy: string.IsNullOrWhiteSpace(page.SortBy) ? null : page.SortBy,
            descending: page.SortDir == SortDirection.Descending,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var addressPropertyIds = new List<int>(AddressProfilePropertyNames.Length);
        foreach (string propertyName in AddressProfilePropertyNames)
        {
            ProfilePropertyDefinition? part = definitions.FirstOrDefault(candidate =>
                string.Equals(candidate.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase));

            if (part is not null)
            {
                addressPropertyIds.Add(part.PropertyDefinitionId);
            }
        }

        int? telephonePropertyId = definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.PropertyName, TelephoneProfilePropertyName, StringComparison.OrdinalIgnoreCase))
            ?.PropertyDefinitionId;

        var rows = new List<UserListItemDto>(matches.Items.Count);
        foreach (User account in matches.Items)
        {
            string? address = null;
            string? telephone = null;

            if (addressPropertyIds.Count > 0 || telephonePropertyId is not null)
            {
                IReadOnlyList<UserProfileValue> values =
                    await _profiles.GetProfileValuesAsync(account.UserId, cancellationToken).ConfigureAwait(false);

                address = ComposeAddress(values, addressPropertyIds);

                if (telephonePropertyId is int telephoneId)
                {
                    telephone = StoredValue(
                        values.FirstOrDefault(value => value.PropertyDefinitionId == telephoneId));
                }
            }

            rows.Add(UserMappings.ToListItem(account, portalId, address, telephone));
        }

        return Result<PagedResult<UserListItemDto>>.Success(
            page.PageSize == UnpagedPageSize
                ? PagedResult<UserListItemDto>.Unpaged(rows)
                : PagedResult<UserListItemDto>.Create(rows, matches.TotalCount, matches.PageIndex, matches.PageSize));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Absence is not a failure, so an account the tenant does not hold reads as a successful result with
    /// no value. The legacy caching wrapper is not reproduced on this read: its key was composed from the
    /// account name (<c>DataCache.UserCacheKey</c>, "UserInfo|{0}|{1}"), which this member does not have
    /// before it reads, so caching here would need a second key shape for the same payload. Every write
    /// below still evicts that key, so the sign-in path's cached read stays correct.
    /// </remarks>
    public async Task<Result<UserDetailDto?>> GetUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            // The contract declares absence as a null value on a non-nullable type parameter, so the
            // null-forgiving operator is required here; the interface is a frozen contract.
            return Result<UserDetailDto?>.Success(null);
        }

        IReadOnlyList<string> roles = await _users
            .ListRoleNamesAsync(portalId, userId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Result<UserDetailDto?>.Success(UserMappings.ToDetail(account, portalId, roles));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The account row, the tenant membership row and the automatic role enrolments commit together, so a
    /// half-built account cannot be left behind. Automatic enrolment reproduces UserController.vb
    /// L167-L180, which assigned a newly created account to every tenant role flagged for it; it is done
    /// through the role repository rather than through the role service precisely so that it lands in the
    /// same commit.
    /// </para>
    /// <para>
    /// The credential is written afterwards, because the credential store is the external
    /// <c>aspnet_*</c> membership store whose members are immediate by contract rather than staged. A
    /// failure there is compensated by removing everything the first commit wrote, so the two steps are
    /// atomic in effect.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy screen could create an account with a generated credential and mail it to
    /// its holder. The mail subsystem is out of scope, so a credential that is generated here could never
    /// reach anybody; rather than accept a request the service cannot honour, the operation requires an
    /// explicit credential and <see cref="CreateUserRequest"/> carries no generated-credential flag at
    /// all. An administrator who wants the holder to choose their own credential creates the account with
    /// a credential of the administrator's choosing and then issues a reset. Electronic-mail uniqueness is
    /// enforced only when the deployment asks for it, because the legacy provider was configured with it
    /// switched off.
    /// </para>
    /// </remarks>
    public async Task<Result<UserDetailDto>> CreateUserAsync(
        int portalId,
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Result<UserDetailDto>.Failure(CreateInvalidUsernameCode, "An account name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Result<UserDetailDto>.Failure(
                CreateInvalidEmailCode,
                "An electronic-mail address is required.");
        }

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<UserDetailDto>.Failure(
                CreatePortalAssignmentFailedCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return Result<UserDetailDto>.Failure(CreateInvalidPasswordCode, "A credential is required.");
        }

        // An omitted confirmation is not a mismatch. The member is a non-nullable string that
        // defaults to empty, so "not submitted" reads as empty rather than as null; testing for
        // null instead would compare an omitted confirmation against the credential and refuse a
        // request the endpoint may legitimately be called without. The declarative validator is
        // the stricter authority and demands the pair agree; this is the service's own guard.
        if (!string.IsNullOrEmpty(request.ConfirmPassword)
            && !string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return Result<UserDetailDto>.Failure(
                CreatePasswordMismatchCode,
                "The credential and its confirmation do not match.");
        }

        if (ValidateCredential(request.Password, CreateInvalidPasswordCode) is ResultReason weakCredential)
        {
            return Result<UserDetailDto>.Failure(weakCredential);
        }

        string credential = request.Password;

        User? existing = await _users
            .GetByUsernameAsync(portalId: null, request.Username, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            UserPortal? membership = await _users
                .GetMembershipAsync(portalId, existing.UserId, cancellationToken)
                .ConfigureAwait(false);

            return membership is not null
                ? Result<UserDetailDto>.Failure(
                    CreateUserAlreadyRegisteredCode,
                    FormattableString.Invariant(
                        $"Account \"{request.Username}\" is already registered in portal {portalId}."))
                : Result<UserDetailDto>.Failure(
                    CreateUsernameAlreadyExistsCode,
                    FormattableString.Invariant($"Account name \"{request.Username}\" is already in use."));
        }

        if (await _users.UsernameExistsAsync(request.Username, excludingUserId: null, cancellationToken)
            .ConfigureAwait(false))
        {
            return Result<UserDetailDto>.Failure(
                CreateUsernameAlreadyExistsCode,
                FormattableString.Invariant($"Account name \"{request.Username}\" is already in use."));
        }

        if (_passwordPolicy.RequiresUniqueEmail
            && await _users.EmailExistsAsync(portalId, request.Email, excludingUserId: null, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<UserDetailDto>.Failure(
                CreateDuplicateEmailCode,
                "The electronic-mail address is already in use.");
        }

        DateTime now = _clock.UtcNow;
        User account = UserMappings.ToNewUser(request);

        account.UserPortals.Add(new UserPortal
        {
            PortalId = portalId,
            CreatedDate = now,
            IsAuthorised = request.Authorize,
        });

        // The auto-assignment filter is applied here rather than in the repository because the legacy
        // membership provider had no such procedure: it exposed GetPortalRoles alone
        // (DataProvider.vb:L91) and the AutoAssignment column was tested by the caller. Roles are
        // counted in tens per portal, so selecting over the portal's own set costs nothing.
        IReadOnlyList<Role> portalRoles =
            await _roles.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        List<Role> automatic = portalRoles.Where(role => role.AutoAssignment).ToList();

        foreach (Role role in automatic)
        {
            account.UserRoles.Add(new UserRole { RoleId = role.RoleId });
        }

        _users.Add(account);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            bool created = await _users.CreateCredentialAsync(
                account.UserId,
                _passwordHasher.Hash(credential),
                request.Authorize,
                now,
                cancellationToken).ConfigureAwait(false);

            if (!created)
            {
                await CompensateFailedCreationAsync(account, cancellationToken).ConfigureAwait(false);
                return Result<UserDetailDto>.Failure(
                    CreateDuplicateUsernameCode,
                    FormattableString.Invariant(
                        $"The credential store already holds a credential for account name \"{request.Username}\"."));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await CompensateFailedCreationAsync(account, CancellationToken.None).ConfigureAwait(false);
            return Result<UserDetailDto>.Failure(
                CreateProviderErrorCode,
                FormattableString.Invariant(
                    $"The credential store could not be written: {exception.GetType().Name}."));
        }

        // The membership facts live in the external credential store and are projections rather than
        // columns, so they are set here for the response the caller receives.
        account.IsApproved = request.Authorize;
        account.IsLockedOut = false;
        account.CreatedDate = now;
        account.LastPasswordChangeDate = now;

        _cache.InvalidatePortal(portalId);
        _cache.InvalidateUser(portalId, account.Username);

        // MIGRATION: reproduces the legacy USER_CREATED audit entry (EventLogController.vb:L39). Recorded
        // only once the credential store has accepted the credential, so an account whose creation was
        // compensated away above never produces a record.
        RecordAudit(
            AuditEventNames.UserCreated,
            portalId,
            account.UserId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Username"] = account.Username,
                ["Approved"] = request.Authorize.ToString(CultureInfo.InvariantCulture),
            });

        IReadOnlyList<string> roleNames = automatic.Select(role => role.RoleName).ToList();
        return Result<UserDetailDto>.Success(UserMappings.ToDetail(account, portalId, roleNames));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The display name is reformatted to the tenant's configured format before the row is written,
    /// reproducing the call the legacy editor made immediately beforehand at User.ascx.vb:L374, and
    /// editing the tenant administrator's own account drops the tenant cache, which L368-L371 did
    /// explicitly. Electronic-mail uniqueness is deliberately not enforced here at all, for the reason
    /// the contract records.
    /// </remarks>
    public async Task<Result<UserDetailDto>> UpdateUserAsync(
        int portalId,
        int userId,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Result<UserDetailDto>.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        UserMappings.ApplyUpdate(account, request);

        MembershipSettingsDto? settings =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false);

        if (settings is not null && !string.IsNullOrWhiteSpace(settings.SecurityDisplayNameFormat))
        {
            account.DisplayName = FormatDisplayName(settings.SecurityDisplayNameFormat, account);
        }

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            return Result<UserDetailDto>.Failure(
                PersistenceConflictCode,
                "The account was changed by another request; reload it and try again.");
        }

        if (portal?.AdministratorId == userId)
        {
            _cache.InvalidatePortal(portalId);
        }

        _cache.InvalidateUser(portalId, account.Username);

        IReadOnlyList<string> roles = await _users
            .ListRoleNamesAsync(portalId, userId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Result<UserDetailDto>.Success(UserMappings.ToDetail(account, portalId, roles));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Deletion removes the tenant membership and the tenant's role assignments, and removes the account
    /// row and its credential only once no membership of any tenant remains. That is what the schema
    /// permits: an account is installation-wide and its tenant memberships are rows in
    /// <c>dbo.UserPortals</c>, so erasing the account while another tenant still holds a membership would
    /// destroy that tenant's data.
    /// </para>
    /// <para>
    /// The permission grants keyed by the account are removed first, reproducing the three cleanup
    /// procedures UserController.vb:L200 invoked before deleting the account. That removal is immediate by
    /// repository contract rather than staged, which the contract documents, so it is ordered ahead of the
    /// commit exactly as the legacy sequence was.
    /// </para>
    /// <para>
    /// Both protections are enforced here rather than exposed as switches. The legacy delete let its
    /// caller decide whether the tenant's designated administrator survived, which placed a business
    /// decision in the presentation layer.
    /// </para>
    /// </remarks>
    public async Task<Result> DeleteUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        if (account.IsSuperUser)
        {
            return Result.Failure(
                DeleteSuperUserProtectedCode,
                FormattableString.Invariant(
                    $"Account {userId} is a host account and cannot be deleted through portal administration."));
        }

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        if (portal?.AdministratorId == userId)
        {
            return Result.Failure(
                DeleteAdministratorProtectedCode,
                FormattableString.Invariant(
                    $"Account {userId} is the designated administrator of portal {portalId} and cannot be deleted."));
        }

        // THE FIRST DESTRUCTIVE STEP, and deliberately so. Every guard above has passed, so the deletion is
        // going to be attempted; and nothing below has yet removed anything, so a revocation that cannot be
        // written abandons the deletion with the account wholly intact rather than half dismantled. Placing
        // it after the cascade would mean reporting failure over an account whose grants, role assignments
        // and credential had already gone.
        //
        // IUserService calls this the worst of the three cases it covers, and the reasoning is worth keeping
        // in view: a refresh token that outlives the account it names is exchanged for access tokens
        // asserting an identity that no longer exists, and there is no longer any account for an
        // administrator to inspect or disable. Revoking is part of deleting rather than a follow-up to it.
        if (await EndSessionsAsync(userId, cancellationToken).ConfigureAwait(false) is ResultReason sessions)
        {
            return Result.Failure(sessions);
        }

        // MIGRATION: the legacy cascade removed the account's direct grants from both grant tables
        // through two separate provider members - DeleteModulePermissionsByUserID, reached from
        // ModulePermissionController.vb:L218, and DeleteTabPermissionsByUserID, reached from
        // TabPermissionController.vb:L209 - and each terminal procedure joins its own grant table to its
        // own owning table so the delete is bounded to this portal. Both are still issued, and for the
        // same reason the legacy code issued both: a grant left behind on either table would outlive the
        // account that held it and a later account reusing the identifier would inherit it.
        //
        // THE CASCADE IS ORCHESTRATED THROUGH THE PERMISSION CONTRACT, NOT THROUGH THE GRANT REPOSITORY.
        // The two tables are one concern and the rule bounding the removal to DIRECT grants - grants
        // reaching the account through a role belong to the role, so removing them would strip every
        // other holder of that role - is permission knowledge rather than account knowledge. Consolidating
        // it behind the one member that owns it keeps a single definition of "this account's own grants";
        // issuing the two table deletes from here would put a second copy of that rule in a service whose
        // subject is accounts, free to drift from the first. The account identifier crosses the boundary
        // as an int: the legacy members took the account OBJECT and read the portal off it, which is
        // precisely why a caller could widen the removal to every portal the account belonged to.
        //
        // The removal is staged rather than committed, so it joins the single commit below and an account
        // whose deletion is abandoned further down keeps its grants.
        Result cascade = await _permissions
            .DeleteUserPermissionsAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        // A refusal is propagated rather than discarded. Nothing has been committed at this point, so
        // reporting the reason leaves the account whole; swallowing it would commit an account removal
        // whose grants were still in place, which is the one outcome the cascade exists to prevent.
        if (cascade.IsFailure)
        {
            return cascade;
        }

        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        foreach (UserRole assignment in assignments)
        {
            await _roles
                .DeleteUserRoleAsync(assignment.UserId, assignment.RoleId, cancellationToken)
                .ConfigureAwait(false);
        }

        UserPortal? membership = await _users
            .GetMembershipAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (membership is not null)
        {
            _users.RemoveMembership(membership);
        }

        bool holdsAnotherMembership = account.UserPortals
            .Any(candidate => candidate.PortalId != portalId);

        if (!holdsAnotherMembership)
        {
            // THE ANSWER IS READ, WHICH IT PREVIOUSLY WAS NOT. This is the one write in the cascade that
            // leaves the external membership store, and it is the one whose failure matters most: the account
            // row is about to be removed, so a credential left behind becomes an orphan that no
            // administrative screen can reach and no later deletion will revisit, while the deletion itself
            // reported success. Discarding the answer made an unreachable membership store look exactly like
            // a completed removal.
            //
            // Refusing here is safe precisely because nothing has been committed. Every removal above is
            // tracked or issued inside this unit of work and the commit is still ahead, so abandoning now
            // leaves the account whole rather than partly dismantled, and the caller is told the truth. The
            // reason token ends in store_unavailable, so the Api edge answers 503 and a caller may retry.
            if (!await _users.DeleteCredentialAsync(userId, cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure(
                    CredentialRemovalFailedCode,
                    FormattableString.Invariant($"The credential held by account {userId} could not be removed.")
                    + " The account was left intact rather than deleted without it. Try again.");
            }

            _users.Remove(account);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);
        _cache.InvalidateUser(portalId, account.Username);

        // MIGRATION: reproduces the legacy USER_DELETED audit entry, whose one measured call site is
        // UserController.vb:L240 - AddLog("Username", objUser.Username, _portalSettings, objUser.UserID,
        // EventLogType.USER_DELETED). The account name it recorded is carried on the Username property and
        // the account identifier it recorded is the subject, so the two facts the legacy record held both
        // survive. The account-fully-removed distinction is net-new detail: the legacy delete had no
        // multi-tenant retention arm to report.
        RecordAudit(
            AuditEventNames.UserDeleted,
            portalId,
            account.UserId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Username"] = account.Username,
                ["AccountRemoved"] = (!holdsAnotherMembership).ToString(CultureInfo.InvariantCulture),
            });

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// SELF-SERVICE ONLY, AND THE CURRENT CREDENTIAL IS ALWAYS VERIFIED. This member no longer carries the
    /// administrative reset, and that separation is a security fix rather than a tidying. One member serving
    /// both meant the caller's own request body chose which of the two it got: sending the reset
    /// discriminator skipped the current-credential check entirely, so any bearer token that could reach the
    /// endpoint could overwrite any account's credential without proving anything. The two operations now
    /// have two members, two endpoints and two authorisation policies - this one is reachable only by the
    /// account holder, and <see cref="ResetPasswordAsync"/> only by an administrator of the account's portal.
    /// </para>
    /// <para>
    /// A reset discriminator submitted here is REFUSED rather than honoured or ignored. Honouring it would
    /// restore the defect; ignoring it would silently perform a different operation from the one the caller
    /// asked for, on a credential.
    /// </para>
    /// <para>
    /// Neither path returns, echoes or logs a credential, and no hash value appears on any result.
    /// </para>
    /// <para>
    /// A successful change clears the forced-change flag, because the flag's purpose has been served.
    /// </para>
    /// <para>
    /// MIGRATION: the recovery question-and-answer operation the legacy screen offered is reported as
    /// unsupported rather than implemented. Its only real purpose was to guard credential retrieval,
    /// which this migration removes outright, so carrying the pair forward would preserve a mechanism
    /// with nothing left to protect.
    /// </para>
    /// </remarks>
    public Task<Result> ChangePasswordAsync(
        int portalId,
        int userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (EnsureOperation(request, ChangePasswordRequest.OperationChange) is ResultReason wrongOperation)
        {
            return Task.FromResult(Result.Failure(wrongOperation));
        }

        return WriteCredentialAsync(portalId, userId, request, verifyCurrent: true, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// ADMINISTRATIVE, AND THEREFORE GATED OUTSIDE THIS LAYER. This member deliberately does not verify the
    /// current credential, which is the whole point of a reset: its holder has lost it. What makes that safe
    /// is that the endpoint carrying it requires administration of the account's own portal, so the absence
    /// of a credential check is compensated by the presence of an authorisation check. It was previously
    /// selectable from inside a request body on an endpoint that required nothing but authentication, which
    /// is the account-takeover path this split closes.
    /// </para>
    /// <para>
    /// Refused outright when the deployment has reset switched off, before the account is read, so a
    /// deployment that has disabled reset cannot be probed for which accounts exist.
    /// </para>
    /// <para>
    /// MIGRATION: this member owns the whole of the credential migration path. Legacy credentials were held
    /// reversibly and cannot be verified against a one-way hash, so an administrative reset is the only way a
    /// pre-existing account regains access. The shipped default keeps reset ENABLED, which is faithful:
    /// <c>Website/release.config</c> L240 registers the legacy provider with
    /// <c>enablePasswordReset="true"</c>. Disabling it by default would be an unrequested behavioural change
    /// that locked every pre-migration account out permanently.
    /// </para>
    /// <para>
    /// The new credential is never returned. The legacy reset assigned the provider's answer onto the account
    /// and returned it, so the credential travelled back in clear text; nothing here reproduces that.
    /// </para>
    /// </remarks>
    public Task<Result> ResetPasswordAsync(
        int portalId,
        int userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (EnsureOperation(request, ChangePasswordRequest.OperationReset) is ResultReason wrongOperation)
        {
            return Task.FromResult(Result.Failure(wrongOperation));
        }

        if (!_passwordPolicy.PasswordResetEnabled)
        {
            return Task.FromResult(Result.Failure(
                PasswordResetNotEnabledCode,
                "Administrative credential reset is switched off for this deployment."));
        }

        return WriteCredentialAsync(portalId, userId, request, verifyCurrent: false, cancellationToken);
    }

    /// <summary>
    /// Confirms that the submitted operation discriminator names the operation actually being performed.
    /// </summary>
    /// <param name="request">The submitted credential change.</param>
    /// <param name="expected">The discriminator this entry point implements.</param>
    /// <returns>
    /// The failure reason when the discriminator names a different operation, or <see langword="null"/> when
    /// it names this one or is absent.
    /// </returns>
    /// <remarks>
    /// An ABSENT discriminator is accepted, because the endpoint the caller chose has already stated which
    /// operation they meant and requiring them to say it twice would refuse well-formed requests. A
    /// discriminator naming the OTHER operation is refused rather than ignored: the two differ in whether a
    /// credential is verified, so silently performing the one the caller did not ask for is not an option.
    /// An unrecognised value is refused for the same reason.
    /// </remarks>
    private static ResultReason? EnsureOperation(ChangePasswordRequest request, string expected)
    {
        if (string.IsNullOrWhiteSpace(request.Operation)
            || string.Equals(request.Operation, expected, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ResultReason(
            PasswordUnsupportedOperationCode,
            FormattableString.Invariant(
                $"Operation \"{request.Operation}\" is not supported by this endpoint, which performs \"{expected}\". Use the endpoint that performs the operation you intend."));
    }

    /// <summary>
    /// Writes a new credential, shared by the self-service change and the administrative reset.
    /// </summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose credential is changing.</param>
    /// <param name="request">The submitted credential change.</param>
    /// <param name="verifyCurrent">
    /// Whether the current credential must be presented and verified. This is the ONLY difference between
    /// the two operations, and it is a parameter rather than a re-reading of the request so that the
    /// decision belongs to the entry point - and therefore to the endpoint's authorisation policy - and can
    /// never again be chosen by the caller.
    /// </param>
    /// <param name="cancellationToken">Token observed while the credential is written.</param>
    /// <returns>A successful result with no value, or the reason the credential was not written.</returns>
    private async Task<Result> WriteCredentialAsync(
        int portalId,
        int userId,
        ChangePasswordRequest request,
        bool verifyCurrent,
        CancellationToken cancellationToken)
    {
        // WHO IS ALLOWED TO PERFORM WHICH OPERATION IS SETTLED HERE, BEFORE ANY ACCOUNT IS READ, and it is
        // settled a SECOND time rather than once. The endpoints that reach this member already carry the
        // policies that answer it - a change is routed through the account-owner policy and a reset through
        // the portal-administrator policy - so on the HTTP path this check agrees with the policy that has
        // already run. It is stated here anyway, for two reasons. Any other caller of this service reaches
        // the member directly, with no policy in front of it. And the defect this closes is severe enough
        // that it should not depend on a route attribute remaining correct: an earlier revision performed
        // NEITHER check, so the reset branch skipped current-credential verification by design - correctly,
        // since an administrator does not know the credential they are resetting - and performed no
        // privilege check either, which meant any caller who could reach this member could set anybody's
        // credential.
        //
        // A CHANGE is self-service and is refused to anyone but the owner. Verifying the current credential,
        // which the branch below does, is proof of possession rather than of authority: it establishes that
        // the caller knows the credential, and an administrator who wishes to act on another account uses
        // the reset operation, which is audited as an administrative act.
        //
        // A RESET is administrative and is refused to anyone who does not administer the tenant the account
        // belongs to. The asymmetry matters: were the reset operation admitted to the account itself, the
        // owner could invoke it on themselves and thereby set a new credential WITHOUT presenting the
        // current one - which would make the change branch's verification optional, and turn a stolen
        // access token into a permanent account takeover.
        //
        // An administrator's OWN account is not additionally singled out, which is a considered position
        // rather than an oversight: an account that already administers the tenant can reset every credential
        // in it, so resetting its own escalates nothing that is not already conferred. This differs from the
        // three membership transitions, which DO refuse a self-targeted change, because those exist to keep an
        // administrator subject to a control - lockout and approval - that resetting a credential is not.
        //
        // The operation is read from verifyCurrent rather than from the request, so it is the entry point's
        // answer and never the caller's: a reset is exactly the call that does not verify the current value.
        Result authorised = await AuthoriseCredentialOperationAsync(
                portalId,
                userId,
                isReset: !verifyCurrent,
                cancellationToken)
            .ConfigureAwait(false);

        if (authorised.IsFailure)
        {
            return authorised;
        }

        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        if (string.IsNullOrEmpty(request.NewPassword))
        {
            return Result.Failure(PasswordMissingCode, "A new credential is required.");
        }

        if (request.ConfirmPassword is not null
            && !string.Equals(request.NewPassword, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return Result.Failure(PasswordMismatchCode, "The new credential and its confirmation do not match.");
        }

        if (ValidateCredential(request.NewPassword, PasswordInvalidCode) is ResultReason weakCredential)
        {
            return Result.Failure(weakCredential);
        }

        (bool exists, string? storedHash, _, _) = await _users
            .GetCredentialStateAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} holds no credential."));
        }

        if (verifyCurrent)
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
            {
                return Result.Failure(PasswordMissingCode, "The current credential is required.");
            }

            if (storedHash is null || !_passwordHasher.Verify(request.CurrentPassword, storedHash))
            {
                return Result.Failure(PasswordCurrentIncorrectCode, "The current credential is not correct.");
            }
        }

        if (storedHash is not null && _passwordHasher.Verify(request.NewPassword, storedHash))
        {
            return Result.Failure(
                PasswordNotDifferentCode,
                "The new credential must differ from the one currently stored.");
        }

        // Every reason to refuse this request has now been exhausted, so this is the last moment at which
        // the sessions can be ended without ending them for a request that was going to be rejected anyway.
        // It is also the last moment BEFORE the credential is replaced, which is the ordering the helper
        // exists to enforce: a credential changed with sessions left exchangeable would not end the session
        // the change was performed to end, and the obligation is stated on IUserService in exactly those
        // terms. Both a self-service change and an administrative reset are covered - the reset ends the
        // sessions of the account being reset, not the administrator's own.
        if (await EndSessionsAsync(userId, cancellationToken).ConfigureAwait(false) is ResultReason sessions)
        {
            return Result.Failure(sessions);
        }

        DateTime now = _clock.UtcNow;
        bool written = await _users
            .SetPasswordHashAsync(userId, _passwordHasher.Hash(request.NewPassword), now, cancellationToken)
            .ConfigureAwait(false);

        if (!written)
        {
            return Result.Failure(PasswordResetFailedCode, "The credential store refused the change.");
        }

        if (account.UpdatePassword)
        {
            account.UpdatePassword = false;
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        account.LastPasswordChangeDate = now;
        _cache.InvalidateUser(portalId, account.Username);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Both preconditions are measured rather than invented: the legacy membership panel offered the
    /// command only while the account was locked (Membership.ascx.vb:L142) and hid all four membership
    /// commands when the acting administrator was looking at their own account (L135). Both become codes
    /// so the API layer cannot decide either question for itself.
    /// </remarks>
    public async Task<Result> UnlockUserAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (EnsureNotActingOnSelf(userId) is ResultReason self)
        {
            return Result.Failure(self);
        }

        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        (bool exists, _, _, bool isLockedOut) = await _users
            .GetCredentialStateAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} holds no credential."));
        }

        if (!isLockedOut)
        {
            return Result.Failure(
                UnlockNotLockedCode,
                FormattableString.Invariant($"Account {userId} is not locked."));
        }

        if (!await _users.UnlockAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} holds no credential."));
        }

        account.IsLockedOut = false;
        _cache.InvalidateUser(portalId, account.Username);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// One member with a direction argument replaces the panel's two commands, which kept their
    /// precondition in two places. The direction is data; the precondition - that the account is not
    /// already in the requested state - and the prohibition on acting upon one's own account are both
    /// enforced here.
    /// </remarks>
    public async Task<Result> SetUserApprovalAsync(
        int portalId,
        int userId,
        bool isApproved,
        CancellationToken cancellationToken = default)
    {
        if (EnsureNotActingOnSelf(userId) is ResultReason self)
        {
            return Result.Failure(self);
        }

        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        (bool exists, _, bool storedApproval, _) = await _users
            .GetCredentialStateAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} holds no credential."));
        }

        if (storedApproval == isApproved)
        {
            return Result.Failure(
                ApprovalUnchangedCode,
                FormattableString.Invariant(
                    $"Account {userId} is already {(isApproved ? "approved" : "unapproved")}."));
        }

        // Withdrawal only. Granting an approval takes nothing away, so it revokes nothing - IUserService
        // says so in terms, and ending a session because an account gained a right would be gratuitous.
        // Withdrawal is the opposite: it ends the account's right to sign in, so leaving it holding
        // exchangeable refresh tokens would let it keep obtaining access tokens after the withdrawal.
        if (!isApproved
            && await EndSessionsAsync(userId, cancellationToken).ConfigureAwait(false) is ResultReason sessions)
        {
            return Result.Failure(sessions);
        }

        if (!await _users.SetApprovalAsync(userId, isApproved, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} holds no credential."));
        }

        account.IsApproved = isApproved;
        _cache.InvalidateUser(portalId, account.Username);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is the non-destructive alternative to an administrative reset: it demands a new credential at
    /// the next sign-in without replacing the current one, so nothing is transmitted. The precondition
    /// mirrors Membership.ascx.vb:L144 and the self-service prohibition mirrors L135.
    /// </remarks>
    public async Task<Result> RequirePasswordChangeAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (EnsureNotActingOnSelf(userId) is ResultReason self)
        {
            return Result.Failure(self);
        }

        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        if (account.UpdatePassword)
        {
            return Result.Failure(
                PasswordChangeAlreadyRequiredCode,
                FormattableString.Invariant($"Account {userId} is already required to change its credential."));
        }

        account.UpdatePassword = true;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _cache.InvalidateUser(portalId, account.Username);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A tenant with no settings source legitimately answers with no value, which is what the legacy
    /// reader did: it assigned its result only inside a not-nothing guard after locating the account
    /// module by definition name, and the screens that consumed it fell back to their own defaults.
    /// Reporting a failure instead would change behaviour those screens depended upon.
    /// </remarks>
    public async Task<Result<MembershipSettingsDto?>> GetMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        MembershipSettingsDto? settings =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false);

        // The contract declares absence as a null value on a non-nullable type parameter.
        return Result<MembershipSettingsDto?>.Success(settings);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every setting is written as a module setting against the tenant's account module instance, which
    /// is precisely where the legacy screen wrote them (UserSettings.ascx.vb:L183). Absence of that
    /// module is a legitimate answer to a read and an impossibility for a write, which is why this member
    /// reports it and the read above does not.
    /// </remarks>
    public async Task<Result> UpdateMembershipSettingsAsync(
        int portalId,
        MembershipSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Module? source = await FindMembershipSettingsSourceAsync(portalId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return Result.Failure(
                MembershipSettingsSourceMissingCode,
                FormattableString.Invariant(
                    $"Portal {portalId} has no \"{MembershipSettingsDto.UserAccountsModuleDefinitionName}\" module instance to store membership settings against."));
        }

        IReadOnlyList<ModuleSetting> stored =
            await _modules.GetModuleSettingsAsync(source.ModuleId, cancellationToken).ConfigureAwait(false);

        foreach (KeyValuePair<string, string> setting in ProjectMembershipSettings(settings))
        {
            await UpsertModuleSettingAsync(stored, source.ModuleId, setting.Key, setting.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _cache.InvalidatePortal(portalId);
        _cache.InvalidateProfileDefinitions(portalId);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// C-03: the two halves of <c>UserController.vb</c> L1189-L1193, in the legacy order and with the
    /// legacy short-circuit. The setting is consulted FIRST, so a tenant that does not require a valid
    /// profile at sign-in never pays for the definition read at all - which is the same ordering the
    /// legacy <c>And</c> expression produced, since VB's <c>And</c> on the two operands was evaluated left
    /// to right and the second operand was a method call the first could not skip. The reproduction here is
    /// the stronger one: this genuinely short-circuits, so it issues fewer reads than the legacy did while
    /// producing an identical answer.
    /// </para>
    /// <para>
    /// A tenant with no User Accounts module instance has no settings source, so the setting cannot be
    /// read. The legacy behaviour in that case is measured rather than guessed: <c>GetUserSettings</c>
    /// returned nothing, <c>UserModuleBase.GetSetting</c> therefore yielded the key's default, and the
    /// default for this key is <see langword="true"/> (<c>UserModuleBase.vb</c> L94-L194). The default is
    /// applied here rather than the gate being skipped, which is why the settings read is treated as
    /// present-with-defaults instead of as an absence.
    /// </para>
    /// <para>
    /// The completeness test walks the tenant's definitions and stops at the first required property whose
    /// answer is missing or empty, exactly as <c>ProfileController.ValidateProfile</c> did with its
    /// <c>Exit For</c>. An account with no stored answers at all and at least one required definition is
    /// therefore incomplete, which is the case the legacy hit for a newly created account.
    /// </para>
    /// </remarks>
    public async Task<Result<bool>> RequiresProfileCompletionAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        MembershipSettingsDto? settings =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false);

        // An absent settings source yields the key's own default, which is true; a present source yields
        // whatever it stores.
        bool required = settings?.SecurityRequireValidProfileAtLogin
            ?? MembershipSettingsDto.DefaultRequireValidProfileAtLogin;

        if (!required)
        {
            return Result<bool>.Success(false);
        }

        IReadOnlyList<ProfilePropertyDefinition> definitions = await _profiles
            .GetDefinitionsByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        List<ProfilePropertyDefinition> mandatory = definitions.Where(d => d.IsRequired).ToList();
        if (mandatory.Count == 0)
        {
            // Nothing is required, so nothing can be missing. The stored answers are not read at all,
            // which keeps the sign-in path free of a query it cannot learn anything from.
            return Result<bool>.Success(false);
        }

        IReadOnlyList<UserProfileValue> stored =
            await _profiles.GetProfileValuesAsync(userId, cancellationToken).ConfigureAwait(false);

        var answers = new Dictionary<int, string?>(stored.Count);
        foreach (UserProfileValue value in stored)
        {
            answers[value.PropertyDefinitionId] = value.PropertyValue;
        }

        foreach (ProfilePropertyDefinition definition in mandatory)
        {
            if (!answers.TryGetValue(definition.PropertyDefinitionId, out string? answer)
                || string.IsNullOrWhiteSpace(answer))
            {
                return Result<bool>.Success(true);
            }
        }

        return Result<bool>.Success(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every property the tenant defines is present in the projection, so a client can render the whole
    /// form from one read; a property the account has never filled in carries an absent value rather than
    /// being missing. The empty string is preserved and never normalised, because a stored blank and a
    /// value that was never supplied are observably different to any client that renders the field.
    /// </remarks>
    public async Task<Result<UserProfileDto?>> GetProfileAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            // The contract declares absence as a null value on a non-nullable type parameter.
            return Result<UserProfileDto?>.Success(null);
        }

        IReadOnlyList<ProfilePropertyDefinition> definitions = await _profiles
            .GetDefinitionsByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<UserProfileValue> values =
            await _profiles.GetProfileValuesAsync(userId, cancellationToken).ConfigureAwait(false);

        int defaultVisibility =
            await ReadProfileDefaultVisibilityAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<UserProfileDto?>.Success(
            UserMappings.ToProfile(userId, definitions, values, defaultVisibility));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Replace-the-set is the deliberate shape: the caller submits the whole desired profile and the
    /// difference is computed here, because computing it in the API layer would put a business decision
    /// there. A property the submitted set omits therefore has its stored value removed.
    /// </para>
    /// <para>
    /// The three validation rules are the three a profile property definition actually carries - required,
    /// declared length and a validation expression - and they are enforced here because they depend on
    /// tenant data a static request validator cannot see. A validation expression is tenant data rather
    /// than code, so it runs under a time bound and a pathological expression is reported as a validation
    /// failure rather than being allowed to occupy the request.
    /// </para>
    /// <para>
    /// A value longer than the bounded column can hold is stored in the unbounded text column beside it,
    /// which is what the two columns exist for; the projection reads whichever of the pair holds a value.
    /// </para>
    /// </remarks>
    public async Task<Result> UpdateProfileAsync(
        int portalId,
        int userId,
        UserProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        IReadOnlyList<ProfilePropertyDefinition> definitions = await _profiles
            .GetDefinitionsByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        var byDefinitionId = new Dictionary<int, ProfilePropertyDefinition>(definitions.Count);
        foreach (ProfilePropertyDefinition definition in definitions)
        {
            byDefinitionId[definition.PropertyDefinitionId] = definition;
        }

        var submitted = new Dictionary<int, UserProfileValueDto>(profile.Properties.Count);
        foreach (UserProfileValueDto property in profile.Properties)
        {
            if (!byDefinitionId.TryGetValue(property.PropertyDefinitionId, out ProfilePropertyDefinition? definition))
            {
                return Result.Failure(
                    ProfileUnknownPropertyCode,
                    FormattableString.Invariant(
                        $"Portal {portalId} does not define profile property {property.PropertyDefinitionId}."));
            }

            if (ValidateProfileValue(definition, property.PropertyValue) is ResultReason invalid)
            {
                return Result.Failure(invalid);
            }

            submitted[property.PropertyDefinitionId] = property;
        }

        foreach (ProfilePropertyDefinition definition in definitions)
        {
            if (!definition.IsRequired)
            {
                continue;
            }

            if (!submitted.TryGetValue(definition.PropertyDefinitionId, out UserProfileValueDto? property)
                || string.IsNullOrWhiteSpace(property.PropertyValue))
            {
                return Result.Failure(
                    ProfileRequiredPropertyMissingCode,
                    FormattableString.Invariant(
                        $"Profile property \"{definition.PropertyName}\" is required."));
            }
        }

        IReadOnlyList<UserProfileValue> stored =
            await _profiles.GetProfileValuesAsync(userId, cancellationToken).ConfigureAwait(false);

        DateTime now = _clock.UtcNow;
        var retained = new HashSet<int>();

        foreach (UserProfileValue value in stored)
        {
            // MIGRATION: a stored answer the submitted set omits is BLANKED, not deleted. No legacy
            // member and no procedure in the eighty-eight upgrade scripts ever removed a UserProfile
            // row - the membership provider's profile block declares only a reader and an upsert
            // (DataProvider.vb:L118 and L119), and clearing an answer was an upsert carrying an empty
            // value, which the legacy code could express because Null.NullString was the empty
            // string. Blanking therefore reproduces the legacy write exactly, keeping the row's
            // visibility and refreshing its timestamp, where deleting would reset both.
            UserProfileValueDto replacement =
                submitted.TryGetValue(value.PropertyDefinitionId, out UserProfileValueDto? property)
                    ? property
                    : Cleared(value);

            if (property is not null)
            {
                retained.Add(value.PropertyDefinitionId);
            }

            WriteProfileValue(value, replacement, now);
            await _profiles.UpdateProfileValueAsync(value, cancellationToken).ConfigureAwait(false);
        }

        foreach (KeyValuePair<int, UserProfileValueDto> property in submitted)
        {
            if (retained.Contains(property.Key))
            {
                continue;
            }

            var value = new UserProfileValue
            {
                UserId = userId,
                PropertyDefinitionId = property.Key,
            };

            WriteProfileValue(value, property.Value, now);
            await _profiles.AddProfileValueAsync(value, cancellationToken).ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _cache.InvalidateUser(portalId, account.Username);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The sequence is ordered by the display-order column so no client has to sort it, and it is
    /// deliberately unpaged because the legacy screen listed every definition on one page with no pager
    /// and a tenant declares definitions in the tens. The read goes through the cache under the legacy key
    /// so the eviction member the cache abstraction already declares for it stays meaningful.
    /// </para>
    /// <para>
    /// MIGRATION: THE PROFILE-DEFINITION SURFACE IS FULL DEFINITION MANAGEMENT, NOT A READ-ONLY LOOKUP, and
    /// the reading is stated here because the plan describes it both ways - AAP 0.5.1.4 calls the API
    /// surface a "Lookup surface" while AAP 0.5.1.8 calls the client feature "Definition management". The
    /// contract settles it: <see cref="IUserService"/> declares a create, an update and a delete for
    /// definitions alongside the two reads, so read-only is not an available interpretation of it. The
    /// legacy source agrees - <c>Website/admin/Users/ProfileDefinitions.ascx.vb</c> is a management grid
    /// with add, edit, reorder and delete commands, and <c>EditProfileDefinition.ascx.vb</c> is its editor -
    /// so a read-only surface would have LOST a workflow the legacy application offered, which UI
    /// functional parity forbids. The narrower phrase is best read as describing how the definitions are
    /// consumed by the profile screens, which do only read them. The reconciliation is recorded in
    /// MIGRATION_NOTES.md so the two descriptions are not re-litigated downstream.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<ProfilePropertyDefinitionDto>>> ListProfilePropertyDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = string.Format(CultureInfo.InvariantCulture, ProfileDefinitionsCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(
            ProfileDefinitionsCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        // MIGRATION: the legacy caller skipped the database read entirely when the configured expiry
        // resolved to zero. That is not reproduced: disabling caching disables caching only, and the
        // read still runs, because a configuration value must not silently change what is reported.
        IReadOnlyList<ProfilePropertyDefinitionDto> catalogue = expiration > TimeSpan.Zero
            ? await _cache.GetOrCreateAsync(
                cacheKey,
                token => ReadProfileDefinitionsAsync(portalId, token),
                expiration,
                cancellationToken).ConfigureAwait(false)
            : await ReadProfileDefinitionsAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<ProfilePropertyDefinitionDto>>.Success(catalogue);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tenant scoping is preserved rather than added: the legacy editor passed both the definition
    /// identifier and the tenant identifier, so a definition belonging to another tenant reads as an
    /// absence here too.
    /// </remarks>
    public async Task<Result<ProfilePropertyDefinitionDto?>> GetProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default)
    {
        ProfilePropertyDefinition? definition = await _profiles
            .GetDefinitionByIdAsync(propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: PortalId is int? because 03.03.03 lines 77-83 made the column nullable and
        // migrated the legacy host-level -1 to NULL. The lifted comparison therefore also reads a
        // host-level definition as an absence for a portal-scoped request, which is the honest
        // answer: such a definition belongs to no single portal.
        if (definition is null || definition.PortalId != portalId || definition.IsDeleted)
        {
            // The contract declares absence as a null value on a non-nullable type parameter.
            return Result<ProfilePropertyDefinitionDto?>.Success(null);
        }

        int defaultVisibility =
            await ReadProfileDefaultVisibilityAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<ProfilePropertyDefinitionDto?>.Success(
            UserMappings.ToDto(definition, defaultVisibility));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The duplicate-name code preserves a measured outcome: the legacy add returned an identifier below
    /// the integer sentinel to signal a name collision, and the screen turned that into its duplicate-name
    /// message. Encoding an error inside a returned identifier is exactly the idiom a result with a code
    /// replaces.
    /// </remarks>
    public async Task<Result<ProfilePropertyDefinitionDto>> CreateProfilePropertyDefinitionAsync(
        int portalId,
        CreateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.PropertyName))
        {
            throw new DomainException("A profile property name is required.");
        }

        // MIGRATION: the duplicate test reads the declaration rather than asking for a boolean,
        // matching core DataProvider.vb:L254 GetPropertyDefinitionByName, which returned the row. On
        // a create there is nothing to exclude, so any match at all is a collision.
        if (await _profiles
            .GetDefinitionByNameAsync(portalId, request.PropertyName, cancellationToken)
            .ConfigureAwait(false) is not null)
        {
            return Result<ProfilePropertyDefinitionDto>.Failure(
                ProfileDefinitionDuplicateNameCode,
                FormattableString.Invariant(
                    $"Portal {portalId} already declares a profile property named \"{request.PropertyName}\"."));
        }

        ProfilePropertyDefinition created = UserMappings.ToNewDefinition(portalId, request);
        await _profiles.AddDefinitionAsync(created, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _cache.InvalidateProfileDefinitions(portalId);

        int defaultVisibility =
            await ReadProfileDefaultVisibilityAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<ProfilePropertyDefinitionDto>.Success(UserMappings.ToDto(created, defaultVisibility));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reordering needs no member of its own: the legacy grid moved a property by swapping the
    /// display-order values of two definitions and persisting each through the same update call, so the
    /// display order is simply a member of the definition being updated here.
    /// </remarks>
    public async Task<Result<ProfilePropertyDefinitionDto>> UpdateProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        UpdateProfilePropertyDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProfilePropertyDefinition? stored = await _profiles
            .GetDefinitionByIdAsync(propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: A WITHDRAWN DECLARATION IS ABSENT TO THIS MEMBER TOO, and it previously was not.
        // Withdrawal is logical rather than physical, because stored answers reference the declaration, so
        // the row survives with Deleted set. Every READ in this service already treats such a row as gone -
        // the single read tests IsDeleted, and the portal listing filters on it in the store - but this guard
        // tested only existence and tenancy, so a declaration the API refuses to SHOW remained editable
        // through it. That is not a recycle bin: a recycle bin lets a caller see and restore what it holds,
        // and this contract exposes no member that reads, restores or even acknowledges a withdrawn
        // declaration. The asymmetry therefore had no contract behind it, and the honest answer for a
        // resource this API will not show is that it does not exist.
        //
        // Reachable against the schema this migration binds to. The removal path here is physical (the
        // repository stages a Remove and lets the answers cascade), so this application does not itself
        // create withdrawn rows - but the existing DotNetNuke database does, which is the whole premise of
        // mapping to an unaltered schema. Rows carrying Deleted are exactly the legacy data this member will
        // meet first.
        if (stored is null || stored.PortalId != portalId || stored.IsDeleted)
        {
            return Result<ProfilePropertyDefinitionDto>.Failure(
                ProfileDefinitionNotFoundCode,
                FormattableString.Invariant(
                    $"Profile property definition {propertyDefinitionId} does not exist in portal {portalId}."));
        }

        if (string.IsNullOrWhiteSpace(request.PropertyName))
        {
            throw new DomainException("A profile property name is required.");
        }

        // MIGRATION: the same read-the-row test as on create, except that here the declaration being
        // edited must not collide with itself. A boolean answer could not express "found, but it is
        // the row I am editing", which is exactly why the legacy exclusion argument is not pushed down
        // into the repository: the caller knows which row it is editing and compares identifiers here.
        // The comparison is reachable because the terminal update procedure genuinely writes the name
        // (04.05.00:L1685 assigns PropertyName = @PropertyName), so a rename is a supported edit.
        //
        // MIGRATION: THIS READ DELIBERATELY STILL SEES WITHDRAWN DECLARATIONS, and it must not be "made
        // consistent" with the absence rule applied to the guard above. The terminal index is
        // IX_ProfilePropertyDefinition ON (PortalID, ModuleDefID, PropertyName), declared UNIQUE at
        // 03.02.03:L1082 and again at 04.00.04:L1127, and it does NOT include Deleted - so a withdrawn row
        // still occupies its name in the store. A duplicate check that skipped withdrawn rows would accept a
        // rename the database then rejects, turning a clear duplicate-name result into a constraint
        // violation surfacing as a server fault. Absence is the right answer for addressing a withdrawn
        // declaration; presence is the right answer for asking whether its name is free.
        ProfilePropertyDefinition? sameName = await _profiles
            .GetDefinitionByNameAsync(portalId, request.PropertyName, cancellationToken)
            .ConfigureAwait(false);

        if (sameName is not null && sameName.PropertyDefinitionId != propertyDefinitionId)
        {
            return Result<ProfilePropertyDefinitionDto>.Failure(
                ProfileDefinitionDuplicateNameCode,
                FormattableString.Invariant(
                    $"Portal {portalId} already declares a profile property named \"{request.PropertyName}\"."));
        }

        UserMappings.ApplyDefinitionUpdate(stored, request);
        await _profiles.UpdateDefinitionAsync(stored, cancellationToken).ConfigureAwait(false);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            return Result<ProfilePropertyDefinitionDto>.Failure(
                PersistenceConflictCode,
                "The definition was changed by another request; reload it and try again.");
        }

        _cache.InvalidateProfileDefinitions(portalId);

        int defaultVisibility =
            await ReadProfileDefaultVisibilityAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<ProfilePropertyDefinitionDto>.Success(UserMappings.ToDto(stored, defaultVisibility));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The per-account values recorded against the definition are discarded in the same unit of work, so
    /// no value is left referencing a definition that no longer exists. The values already loaded with the
    /// definition are removed explicitly; the schema additionally declares a cascade from the definition
    /// to its values, which the persistence configuration honours, so the outcome does not depend on which
    /// of the two paths ran.
    /// </remarks>
    public async Task<Result> DeleteProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default)
    {
        ProfilePropertyDefinition? definition = await _profiles
            .GetDefinitionByIdAsync(propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: A WITHDRAWN DECLARATION IS ABSENT TO THIS MEMBER TOO, matching the single read, the
        // portal listing and the update beside it. This guard tested only existence and tenancy, so a
        // declaration the API refuses to show could still be deleted through it - and because removal here
        // is physical and cascades the stored answers, that made the one reachable operation on a withdrawn
        // declaration the most destructive one available. Reporting absence instead is both consistent with
        // every read and the safer of the two answers: the caller asked to remove something this API says
        // does not exist, and it now says so before any answer is destroyed.
        if (definition is null || definition.PortalId != portalId || definition.IsDeleted)
        {
            return Result.Failure(
                ProfileDefinitionNotFoundCode,
                FormattableString.Invariant(
                    $"Profile property definition {propertyDefinitionId} does not exist in portal {portalId}."));
        }

        // MIGRATION: the answers recorded against the declaration are NOT removed one at a time here.
        // FK_UserProfile_ProfilePropertyDefinition is declared ON DELETE CASCADE
        // (04.00.04.SqlDataProvider:L1429), and the repository loads the answers before staging the
        // removal, so the change tracker cascades them as explicit statements on any provider that
        // does not enforce the constraint itself. An explicit loop here would duplicate that work,
        // and no value-deletion member exists on the contract because the legacy surface had none.
        await _profiles
            .DeleteDefinitionAsync(definition.PropertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: the commit is guarded exactly as the update path beside it is, and for the same
        // race. A concurrent administrator that removed this declaration between the read above and
        // this commit makes the tracked deletion affect no rows, which the persistence layer reports
        // as a concurrency conflict. Left unguarded that escaped as an unhandled exception and the
        // caller was told the server had failed, when in truth the caller had simply lost a race and
        // the outcome it asked for has already happened. The conflict code is the one this contract
        // documents, so the shared status table answers it as 409.
        //
        // MIGRATION: this comment used to end "and the endpoint's declared conflict response becomes
        // reachable rather than notional", which was half wrong in the direction that matters - the
        // reachability was real but the DELETE endpoint declared no 409 at all, so the reachable response
        // was the undeclared one. The declaration has been added to the endpoint; the claim is stated here
        // without asserting anything about a declaration this layer cannot see.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            return Result.Failure(
                PersistenceConflictCode,
                "The definition was changed by another request; reload it and try again.");
        }

        _cache.InvalidateProfileDefinitions(portalId);

        return Result.Success();
    }

    /// <summary>
    /// Rejects a paging request whose bounds no validator can have accepted.
    /// </summary>
    /// <param name="page">The submitted paging request.</param>
    /// <exception cref="DomainException">Thrown when the bounds are unusable.</exception>
    private static void EnsurePagingIsUsable(PagedRequest page)
    {
        if (page.PageIndex < 0)
        {
            throw new DomainException("The page index must not be negative.");
        }

        if (page.PageSize < 0)
        {
            throw new DomainException("The page size must not be negative.");
        }

        if (page.PageSize > PagedRequestValidator.MaximumPageSize)
        {
            throw new DomainException(FormattableString.Invariant(
                $"The page size must not exceed {PagedRequestValidator.MaximumPageSize}."));
        }

        if (page.Query is not null && page.Query.Length > PagedRequestValidator.QueryMaximumLength)
        {
            throw new DomainException(FormattableString.Invariant(
                $"The search text must not exceed {PagedRequestValidator.QueryMaximumLength} characters."));
        }
    }

    /// <summary>
    /// Rejects a filter that was supplied but is blank.
    /// </summary>
    /// <param name="filter">The submitted filter.</param>
    /// <param name="name">The argument name, used in the reported message.</param>
    /// <exception cref="DomainException">Thrown when the filter is present but blank.</exception>
    /// <remarks>
    /// Absence means "do not filter"; the empty string is not a synonym for it, because an empty prefix
    /// matches every row and would make a filtered search silently unfiltered.
    /// </remarks>
    private static void EnsureFilterIsNotBlank(string? filter, string name)
    {
        if (filter is not null && string.IsNullOrWhiteSpace(filter))
        {
            throw new DomainException(FormattableString.Invariant(
                $"The {name} filter must not be blank; omit it to search without it."));
        }
    }

    /// <summary>
    /// Decides whether the current caller may perform the requested credential operation on the addressed
    /// account.
    /// </summary>
    /// <param name="portalId">The tenant the account belongs to.</param>
    /// <param name="userId">The account whose credential the operation addresses.</param>
    /// <param name="isReset">
    /// <see langword="true"/> for an administrative reset, which presents no current credential;
    /// <see langword="false"/> for a self-service change, which does.
    /// </param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns>A successful outcome when the operation is permitted, and a failure naming why when it is not.</returns>
    /// <remarks>
    /// <para>
    /// THIS IS A SECOND CHECK, NOT THE ONLY ONE, AND BOTH ARE NECESSARY. The route policy establishes that the
    /// caller may address this account at all - it admits the account itself or an administrator of its tenant
    /// - and it does so without reading a request body, which is what a policy can see. It therefore cannot
    /// distinguish the two operations this member accepts, and the distinction is exactly where the danger
    /// lies: reset legitimately skips current-credential verification, so a caller who may address an account
    /// but may not administer it must be prevented from choosing reset.
    /// </para>
    /// <para>
    /// THE AUTHORITY IS READ FROM STORED STATE, NOT FROM THE CALLER'S CLAIMS. <c>ICurrentUser</c> reports what
    /// the token said, and its super-user flag and role list are documented as informational for exactly this
    /// reason: they were minted at sign-in and cannot observe an account demoted, or a role assignment lapsed,
    /// since. Only the account key and tenant are taken from the token - the caller's identity is the one thing
    /// the token is authoritative about - and everything that confers authority is read from the database.
    /// </para>
    /// <para>
    /// AN UNAUTHENTICATED OR UNIDENTIFIABLE CALLER IS REFUSED BOTH OPERATIONS. Reaching this member without an
    /// identity should be impossible, because every route that leads here requires one; it is refused rather
    /// than assumed impossible, so that a future caller reaching the service directly cannot bypass the rule.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy screens expressed this by which screen a caller could open rather than by a check
    /// inside the operation. <c>Website/admin/Users/Password.ascx</c> was the account's own change screen and
    /// required the current credential; the administrative reset was reached from the membership panel, which
    /// the account-management page only rendered for an administrator, and which additionally hid its commands
    /// when an administrator was looking at their own account
    /// (<c>Website/admin/Users/Membership.ascx.vb</c> L135). A stateless API has no screen to gate, so the rule
    /// moves into the operation - which also makes it hold for every caller rather than only for one that came
    /// through a page.
    /// </para>
    /// </remarks>
    private async Task<Result> AuthoriseCredentialOperationAsync(
        int portalId,
        int userId,
        bool isReset,
        CancellationToken cancellationToken)
    {
        // The caller's own identity, and nothing else, is taken from the token.
        bool isSelf = _currentUser.IsAuthenticated
            && _currentUser.UserId is int callerId
            && callerId == userId
            && _currentUser.PortalId is int callerPortalId
            && callerPortalId == portalId;

        if (!isReset)
        {
            return isSelf
                ? Result.Success()
                : Result.Failure(
                    PasswordChangeSelfOnlyForbiddenCode,
                    "A credential change may only be performed by the account that owns it; an administrator "
                    + "uses the reset operation instead.");
        }

        bool administers = _currentUser.IsAuthenticated
            && _currentUser.UserId is int resetterId
            && await CallerAdministersPortalAsync(portalId, resetterId, cancellationToken).ConfigureAwait(false);

        return administers
            ? Result.Success()
            : Result.Failure(
                PasswordResetForbiddenCode,
                "An administrative credential reset requires administrative authority over the portal the "
                + "account belongs to.");
    }

    /// <summary>
    /// Reports whether one account holds administrative authority over one tenant, judged from stored state.
    /// </summary>
    /// <param name="portalId">The tenant in question.</param>
    /// <param name="callerUserId">The account whose authority is being established.</param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns><see langword="true"/> when the account is a host account or administers that tenant.</returns>
    /// <remarks>
    /// <para>
    /// A host account is installation-wide and is accepted without any tenant membership, which is measured
    /// rather than assumed: a host account is created by the installer and need hold membership of no portal,
    /// so a portal-scoped read would not find it. The account is therefore read without a tenant scope and its
    /// own super-user column decides.
    /// </para>
    /// <para>
    /// Otherwise the question is asked by KEYS and by TIME: the administrator role's key comes from the
    /// tenant's own <c>Portals.AdministratorRoleId</c> column, and membership is judged in force at one instant
    /// read once from the injected clock. It is never asked by role name, because no unique constraint on
    /// <c>Roles.RoleName</c> exists anywhere in the eighty-eight upgrade scripts, so the stock name
    /// "Administrators" names a different row in every portal and a name-based test would be satisfied by an
    /// administrator of any of them. The validity window itself is
    /// <c>UserRole.AnyActiveInRole</c>, which is the single implementation shared with the API layer's
    /// authorisation evaluator, so the two layers cannot come to different conclusions about the same
    /// assignment.
    /// </para>
    /// <para>
    /// A tenant that designates no administrator role confers authority on nobody. An unset designation is a
    /// configuration gap, and a gap must not grant.
    /// </para>
    /// </remarks>
    private async Task<bool> CallerAdministersPortalAsync(
        int portalId,
        int callerUserId,
        CancellationToken cancellationToken)
    {
        User? caller = await _users
            .GetAsync(portalId: null, callerUserId, cancellationToken)
            .ConfigureAwait(false);

        if (caller is null)
        {
            return false;
        }

        if (caller.IsSuperUser)
        {
            return true;
        }

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        if (portal?.AdministratorRoleId is not int administratorRoleId)
        {
            return false;
        }

        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesAsync(portalId, callerUserId, cancellationToken)
            .ConfigureAwait(false);

        return UserRole.AnyActiveInRole(assignments, administratorRoleId, _clock.UtcNow);
    }

    /// <summary>
    /// Ends every session the account holds, and reports why the calling operation must be abandoned when
    /// they could not be ended.
    /// </summary>
    /// <param name="userId">The account whose sessions are to end.</param>
    /// <param name="cancellationToken">Token observed while the revocation is written.</param>
    /// <returns>
    /// <see langword="null"/> once no refresh token belonging to the account is exchangeable - including
    /// when it held none - or the reason the caller must report instead of success.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Call this BEFORE the state change, not after it.</b> The ordering is the whole reason this is a
    /// helper rather than two lines repeated three times. Revoking first and then failing leaves the account
    /// signed out of sessions it may simply re-establish, which is an inconvenience; changing the state
    /// first and then failing to revoke leaves the operation reported as failed while the credential has in
    /// fact been replaced, or the approval withdrawn, with every session still live - which is both a
    /// falsehood to the caller and the exact exposure the revocation exists to close.
    /// </para>
    /// <para>
    /// The residual race is bounded and is worth naming. Between the revocation and the state change a
    /// concurrent exchange could mint one fresh family, which the revocation has already passed. That family
    /// is short-lived by construction and, once the state change lands, cannot be renewed: the sign-in
    /// service re-reads approval and lock-out on every exchange, so the next rotation refuses it and revokes
    /// it. What remains is one access token's own lifetime, which is the irreducible floor for stateless
    /// bearer tokens and is documented as such rather than papered over.
    /// </para>
    /// <para>
    /// The revocation is idempotent, so an account holding no session succeeds here rather than reporting
    /// that nothing was found, and a caller that retries after a store failure cannot do harm by retrying.
    /// </para>
    /// </remarks>
    private async Task<ResultReason?> EndSessionsAsync(int userId, CancellationToken cancellationToken)
    {
        Result revoked = await _tokens
            .RevokeAllRefreshTokensAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        if (revoked.IsSuccess)
        {
            return null;
        }

        // The token service's own reason is deliberately not forwarded. Its code belongs to that contract's
        // vocabulary and its message describes a store this caller does not own, so it is replaced by this
        // service's own code and a sentence written for the administrator who will read it.
        return new ResultReason(
            SessionRevocationFailedCode,
            FormattableString.Invariant($"The sessions held by account {userId} could not be ended.")
            + " The operation was abandoned rather than completed while they remained active. Try again.");
    }

    /// <summary>
    /// Refuses a membership transition an administrator aimed at their own account.
    /// </summary>
    /// <param name="userId">The account the transition addresses.</param>
    /// <returns>The reason the transition is refused, or <see langword="null"/> when it is permitted.</returns>
    private ResultReason? EnsureNotActingOnSelf(int userId)
        => _currentUser.UserId is int actor && actor == userId
            ? new ResultReason(
                MembershipSelfForbiddenCode,
                "A membership transition cannot be applied to the acting administrator's own account.")
            : null;

    /// <summary>
    /// Tests whether an exception, or any exception it wraps, reports a concurrency conflict.
    /// </summary>
    /// <param name="exception">The exception raised by the commit.</param>
    /// <returns><see langword="true"/> when the commit lost a race.</returns>
    /// <remarks>
    /// The conflict is recognised by type name rather than by catching the persistence provider's own
    /// exception type, because this project deliberately declares no persistence package: adding one so
    /// that a single <c>catch</c> clause could name a type would put an infrastructure dependency in the
    /// application layer, which the layering forbids.
    /// </remarks>
    private static bool IsConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (string.Equals(current.GetType().Name, "DbUpdateConcurrencyException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reverses everything the first commit of a failed creation wrote.
    /// </summary>
    /// <param name="account">The account whose creation failed.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A task that completes once the reversal is committed.</returns>
    private async Task CompensateFailedCreationAsync(User account, CancellationToken cancellationToken)
    {
        foreach (UserRole assignment in account.UserRoles.ToList())
        {
            // The account key is read from the account rather than from the assignment, so the
            // reversal does not depend on the dependent's foreign key having been populated by the
            // commit that is being reversed.
            await _roles
                .DeleteUserRoleAsync(account.UserId, assignment.RoleId, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (UserPortal membership in account.UserPortals.ToList())
        {
            _users.RemoveMembership(membership);
        }

        _users.Remove(account);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the bound credential policy to a submitted credential, returning the reason it was refused
    /// or <see langword="null"/> when it satisfies the policy.
    /// </summary>
    /// <param name="credential">The submitted credential. Never logged, echoed or recorded.</param>
    /// <param name="code">The failure code to report the refusal under, so the caller's context is kept.</param>
    /// <returns>The refusal reason, or <see langword="null"/> when the credential is acceptable.</returns>
    /// <remarks>
    /// <para>
    /// No message produced here contains any part of the credential.
    /// </para>
    /// <para>
    /// PRIVATE, AND NOT A SECOND POLICY. The legacy predicate this derives from, <c>ValidatePassword</c> at
    /// <c>UserController.vb</c>:L1067, was public and therefore a policy in its own right, free to diverge
    /// from the one the screens declared. This one is unreachable from outside the service and reads the
    /// SAME bound <see cref="PasswordPolicyOptions"/> instance that the request validators in
    /// <c>Validation/</c> read, so there is one policy with one source. It neither duplicates nor tightens
    /// the declarative rules: it hardcodes no threshold, adds no rule the options do not declare, and skips
    /// the non-alphanumeric and strength rules entirely unless the configuration asks for them. Its purpose
    /// is that a credential write is never performed on the strength of a validator having been wired up at
    /// the boundary - the service is reachable from a test, a seeder or a future host that has no such
    /// pipeline.
    /// </para>
    /// <para>
    /// The legacy policy is preserved verbatim rather than hardened: minimum length seven, zero required
    /// non-alphanumeric characters and no question-and-answer requirement, read from
    /// <c>Website/release.config</c>:L242-L245. Tightening a policy during a migration locks out existing
    /// account holders, so any hardening is a separate and explicit decision.
    /// </para>
    /// <para>
    /// MIGRATION: DEFECT 1 IS ANNOTATED HERE AND DELIBERATELY NOT FIXED IN THE LEGACY SOURCE. The legacy
    /// predicate evaluated three rules but its third rule ASSIGNED its verdict rather than accumulating
    /// it, so a credential that failed the length rule and then matched the strength expression was
    /// reported VALID - the earlier failure was overwritten. The defect is DORMANT in this installation
    /// because no strength expression is configured, so the third rule never runs and the legacy and
    /// target verdicts agree on every input this configuration can produce. The rules are evaluated in
    /// order here and the first refusal returns immediately, which is the behaviour the legacy code was
    /// evidently reaching for. Per AAP 0.9.1 a discovered defect is annotated in place and not fixed, so
    /// nothing in <c>Library/</c> is touched; were a strength expression ever configured, this service
    /// would refuse a short credential that the legacy predicate accepted, and that difference is recorded
    /// in MIGRATION_NOTES.md rather than left to be found in production.
    /// </para>
    /// <para>
    /// A malformed or pathological strength expression is reported as a refusal rather than thrown: the
    /// match is bounded by a timeout, and both the malformed-pattern and timeout cases answer that the rule
    /// could not be applied. A configuration error must not present as a valid credential.
    /// </para>
    /// </remarks>
    private ResultReason? ValidateCredential(string credential, string code)
    {
        if (credential.Length < _passwordPolicy.MinRequiredPasswordLength)
        {
            return new ResultReason(code, FormattableString.Invariant(
                $"The credential must be at least {_passwordPolicy.MinRequiredPasswordLength} characters long."));
        }

        if (_passwordPolicy.MinRequiredNonAlphanumericCharacters > 0)
        {
            int nonAlphanumeric = credential.Count(character => !char.IsLetterOrDigit(character));
            if (nonAlphanumeric < _passwordPolicy.MinRequiredNonAlphanumericCharacters)
            {
                return new ResultReason(code, FormattableString.Invariant(
                    $"The credential must contain at least {_passwordPolicy.MinRequiredNonAlphanumericCharacters} non-alphanumeric character(s)."));
            }
        }

        if (!string.IsNullOrWhiteSpace(_passwordPolicy.PasswordStrengthRegularExpression))
        {
            try
            {
                if (!Regex.IsMatch(
                        credential,
                        _passwordPolicy.PasswordStrengthRegularExpression,
                        RegexOptions.CultureInvariant,
                        ValidationExpressionTimeout))
                {
                    return new ResultReason(code, "The credential does not satisfy the configured strength rule.");
                }
            }
            catch (ArgumentException)
            {
                return new ResultReason(code, "The configured credential strength rule could not be applied.");
            }
            catch (RegexMatchTimeoutException)
            {
                return new ResultReason(code, "The configured credential strength rule could not be applied.");
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the tenant's display-name format to an account.
    /// </summary>
    /// <param name="format">The configured format, carrying the legacy tokens.</param>
    /// <param name="account">The account whose values fill the tokens.</param>
    /// <returns>The formatted display name.</returns>
    /// <remarks>
    /// The four tokens and their substitution order are taken verbatim from
    /// <c>UserInfo.UpdateDisplayName</c> (UserInfo.vb:L358-L368).
    /// </remarks>
    private static string FormatDisplayName(string format, User account)
        => format
            .Replace("[USERID]", account.UserId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("[FIRSTNAME]", account.FirstName, StringComparison.Ordinal)
            .Replace("[LASTNAME]", account.LastName, StringComparison.Ordinal)
            .Replace("[USERNAME]", account.Username, StringComparison.Ordinal);

    /// <summary>
    /// Concatenates the address parts an account holds, in the legacy order.
    /// </summary>
    /// <param name="values">Every profile value the account holds.</param>
    /// <param name="addressPropertyIds">The address property definitions, in composition order.</param>
    /// <returns>The composed address, or <see langword="null"/> when the account holds no part of one.</returns>
    /// <remarks>
    /// The legacy grid reached the excluded <c>FormatAddress</c> helper, which appended each non-blank
    /// part after a comma and a space and then trimmed the leading separator. Joining the non-blank parts
    /// with the same separator is output-identical.
    /// </remarks>
    private static string? ComposeAddress(IReadOnlyList<UserProfileValue> values, IReadOnlyList<int> addressPropertyIds)
    {
        if (addressPropertyIds.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(addressPropertyIds.Count);
        foreach (int propertyDefinitionId in addressPropertyIds)
        {
            string? part = StoredValue(
                values.FirstOrDefault(value => value.PropertyDefinitionId == propertyDefinitionId));

            if (!string.IsNullOrWhiteSpace(part))
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// Reads the value a profile row holds, from whichever of its two storage columns holds it.
    /// </summary>
    /// <param name="row">The stored row, or <see langword="null"/> when the account has no answer.</param>
    /// <returns>
    /// The stored value, or <see langword="null"/> when <paramref name="row"/> is
    /// <see langword="null"/> or holds nothing in either column.
    /// </returns>
    /// <remarks>
    /// MIGRATION: <c>UserProfileValue</c> exposes <c>PropertyValue</c> and <c>PropertyText</c> raw
    /// and derives nothing, so the coalesce lives here. The order reproduces the legacy read
    /// procedure <c>GetUserProfile</c>, which returns one column aliased <c>PropertyValue</c>
    /// computed as "case when (PropertyValue Is Null) then PropertyText else PropertyValue end"
    /// (<c>04.00.04.SqlDataProvider</c> line 1592): the bounded <c>nvarchar(3750)</c> column wins
    /// whenever it is not SQL <c>NULL</c>, and the <c>ntext</c> overflow column is the fallback.
    /// The two are mutually exclusive per row because <see cref="WriteProfileValue"/> and the legacy
    /// write procedure both null one while filling the other, so the order only becomes observable
    /// for a row some other writer left holding both - and there the legacy answer is the bounded
    /// column. Null-coalescing is exact here precisely because it tests for null alone: an empty
    /// string is a stored value, not an absence, and must not fall through.
    /// </remarks>
    private static string? StoredValue(UserProfileValue? row) => row?.PropertyValue ?? row?.PropertyText;

    /// <summary>
    /// Locates the module instance a tenant's membership settings are stored against.
    /// </summary>
    /// <param name="portalId">The tenant whose settings source is wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The module instance, or <see langword="null"/> when the tenant has none.</returns>
    /// <remarks>
    /// The legacy reader located this module by definition name and then read ordinary module settings
    /// against it, so the settings a tenant-wide screen edits have always been module settings on a
    /// well-known module instance rather than rows in a settings table of their own.
    /// </remarks>
    private async Task<Module?> FindMembershipSettingsSourceAsync(int portalId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModuleDefinition> definitions =
            await _definitions.GetModuleDefinitionsByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        ModuleDefinition? accounts = definitions.FirstOrDefault(candidate => string.Equals(
            candidate.FriendlyName,
            MembershipSettingsDto.UserAccountsModuleDefinitionName,
            StringComparison.OrdinalIgnoreCase));

        if (accounts is null)
        {
            return null;
        }

        // MIGRATION: the legacy module block has no paging member, so the tenant's modules are read whole
        // and the recycle bin is excluded here. This call site never wanted a page - it asked for every
        // live instance so it could locate one by its definition.
        IReadOnlyList<Module> instances =
            await _modules.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        return instances.FirstOrDefault(candidate =>
            !candidate.IsDeleted
            && candidate.ModuleDefinitionId == accounts.ModuleDefinitionId);
    }

    /// <summary>
    /// Reads a tenant's membership settings, applying the legacy defaults for every absent key.
    /// </summary>
    /// <param name="portalId">The tenant whose settings are read.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The settings, or <see langword="null"/> when the tenant has no settings source.</returns>
    private async Task<MembershipSettingsDto?> ReadMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        // MIGRATION: THE LEGACY READ WAS CACHED AND THIS ONE IS NOT, WHICH IS A DELIBERATE OMISSION RATHER
        // than an oversight. GetUserSettings at UserController.vb:L656-L670 kept its answer under the key
        // SettingsKey(portalId) - "UserSettings|" + portalId, composed at L924 - and the measured expiry is
        // the reason this note exists: it is TimeSpan.FromMinutes(Globals.PerformanceSetting), so the
        // performance setting is THE WHOLE TIMEOUT here, not a multiplier over a 20-minute base as it is at
        // every other caching site in the legacy tree (compare the profile-definition read above, which is
        // 20 * the multiplier). The legacy default for that setting is 3 - Globals.vb resolves an absent
        // host setting to 3 - so the real legacy lifetime of this entry was THREE MINUTES against the
        // sixty this convention would otherwise imply. That inconsistency is recorded rather than
        // normalised: anyone reintroducing this cache must reproduce three minutes and not "correct" it to
        // 20 * multiplier, because that would multiply the staleness window twentyfold.
        //
        // It is not reintroduced now for a correctness reason, not a performance one. The caching
        // abstraction classifies a key by declared prefix family in order to scope its invalidation, and
        // "UserSettings|" is not a declared family, so InvalidatePortal could not sweep this entry. The
        // rows behind it are ORDINARY MODULE SETTINGS on the account module instance, reachable and
        // writable through the module service as well as through the write member beside this one, so an
        // entry this service alone knew how to evict would go stale on a write this service never saw. The
        // sibling module service records the same decision for the same reason, for the analogous
        // "GetModuleSettings<id>" key. What is cached instead is the profile-definition catalogue, which is
        // installation-time reference data with a declared family and a declared eviction member.
        //
        // A second difference follows from the legacy code and is preserved: the legacy cached only inside
        // its not-nothing guard, so a tenant with no account module re-probed on every call and never
        // cached the absence. Nothing here caches an absence either.
        Module? source = await FindMembershipSettingsSourceAsync(portalId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        IReadOnlyList<ModuleSetting> stored =
            await _modules.GetModuleSettingsAsync(source.ModuleId, cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, string>(stored.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ModuleSetting setting in stored)
        {
            map[setting.SettingName] = setting.SettingValue;
        }

        // Every default below is the one UserModuleBase.GetSettings applied when the key was absent
        // (UserModuleBase.vb:L94-L194); the property initialisers on the contract already carry them, so
        // only a stored value overrides one.
        var settings = new MembershipSettingsDto
        {
            ColumnFirstName = ReadBoolean(map, "Column_FirstName", false),
            ColumnLastName = ReadBoolean(map, "Column_LastName", false),
            ColumnDisplayName = ReadBoolean(map, "Column_DisplayName", true),
            ColumnAddress = ReadBoolean(map, "Column_Address", true),
            ColumnTelephone = ReadBoolean(map, "Column_Telephone", true),
            ColumnEmail = ReadBoolean(map, "Column_Email", false),
            ColumnCreatedDate = ReadBoolean(map, "Column_CreatedDate", true),
            ColumnLastLogin = ReadBoolean(map, "Column_LastLogin", false),
            ColumnAuthorized = ReadBoolean(map, "Column_Authorized", true),
            DisplayMode = ReadInteger(map, "Display_Mode", 2),
            DisplaySuppressPager = ReadBoolean(map, "Display_SuppressPager", false),
            RecordsPerPage = ReadInteger(map, "Records_PerPage", 10),
            ProfileDefaultVisibility = ReadInteger(map, "Profile_DefaultVisibility", 2),
            ProfileDisplayVisibility = ReadBoolean(map, "Profile_DisplayVisibility", true),
            ProfileManageServices = ReadBoolean(map, "Profile_ManageServices", true),
            RedirectAfterLogin = ReadOptionalInteger(map, "Redirect_AfterLogin"),
            RedirectAfterRegistration = ReadOptionalInteger(map, "Redirect_AfterRegistration"),
            RedirectAfterLogout = ReadOptionalInteger(map, "Redirect_AfterLogout"),
            SecurityEmailValidation = ReadString(
                map,
                "Security_EmailValidation",
                MembershipSettingsDto.DefaultEmailValidationExpression),
            SecurityRequireValidProfile = ReadBoolean(map, "Security_RequireValidProfile", false),
            SecurityRequireValidProfileAtLogin = ReadBoolean(
                map,
                "Security_RequireValidProfileAtLogin",
                MembershipSettingsDto.DefaultRequireValidProfileAtLogin),
            SecurityDisplayNameFormat = ReadString(map, "Security_DisplayNameFormat", string.Empty),
        };

        if (map.TryGetValue("Security_UsersControl", out string? usersControl)
            && int.TryParse(usersControl, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            settings.SecurityUsersControl = parsed;
        }
        else
        {
            // MIGRATION: the legacy default depended on the tenant's size - a free-text picker above a
            // thousand accounts, a drop-down below it (UserModuleBase.vb:L178). The rule is reproduced;
            // the two numeric values are the contract's own, and the client interprets them.
            int accounts = await _portals.CountUsersAsync(portalId, cancellationToken).ConfigureAwait(false);
            settings.SecurityUsersControl = accounts > LargeTenantAccountThreshold ? 1 : 0;
        }

        return settings;
    }

    /// <summary>
    /// Projects membership settings back into the setting names the legacy screen wrote.
    /// </summary>
    /// <param name="settings">The submitted settings.</param>
    /// <returns>The name and value of every setting to store.</returns>
    private static IReadOnlyDictionary<string, string> ProjectMembershipSettings(MembershipSettingsDto settings)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Column_FirstName"] = WriteBoolean(settings.ColumnFirstName),
            ["Column_LastName"] = WriteBoolean(settings.ColumnLastName),
            ["Column_DisplayName"] = WriteBoolean(settings.ColumnDisplayName),
            ["Column_Address"] = WriteBoolean(settings.ColumnAddress),
            ["Column_Telephone"] = WriteBoolean(settings.ColumnTelephone),
            ["Column_Email"] = WriteBoolean(settings.ColumnEmail),
            ["Column_CreatedDate"] = WriteBoolean(settings.ColumnCreatedDate),
            ["Column_LastLogin"] = WriteBoolean(settings.ColumnLastLogin),
            ["Column_Authorized"] = WriteBoolean(settings.ColumnAuthorized),
            ["Display_Mode"] = settings.DisplayMode.ToString(CultureInfo.InvariantCulture),
            ["Display_SuppressPager"] = WriteBoolean(settings.DisplaySuppressPager),
            ["Records_PerPage"] = settings.RecordsPerPage.ToString(CultureInfo.InvariantCulture),
            ["Profile_DefaultVisibility"] = settings.ProfileDefaultVisibility.ToString(CultureInfo.InvariantCulture),
            ["Profile_DisplayVisibility"] = WriteBoolean(settings.ProfileDisplayVisibility),
            ["Profile_ManageServices"] = WriteBoolean(settings.ProfileManageServices),
            ["Redirect_AfterLogin"] = WriteOptionalInteger(settings.RedirectAfterLogin),
            ["Redirect_AfterRegistration"] = WriteOptionalInteger(settings.RedirectAfterRegistration),
            ["Redirect_AfterLogout"] = WriteOptionalInteger(settings.RedirectAfterLogout),
            ["Security_EmailValidation"] = settings.SecurityEmailValidation,
            ["Security_RequireValidProfile"] = WriteBoolean(settings.SecurityRequireValidProfile),
            ["Security_RequireValidProfileAtLogin"] = WriteBoolean(settings.SecurityRequireValidProfileAtLogin),
            ["Security_UsersControl"] = settings.SecurityUsersControl.ToString(CultureInfo.InvariantCulture),
            ["Security_DisplayNameFormat"] = settings.SecurityDisplayNameFormat,
        };

    /// <summary>
    /// Reads the tenant's default profile-value visibility.
    /// </summary>
    /// <param name="portalId">The tenant whose default is wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The configured default, or the legacy fallback when the tenant has no settings source.</returns>
    /// <remarks>
    /// The visibility carried on a definition is a default hint rather than stored state: the schema
    /// declares no visibility column on a profile property definition, only on the per-account value, and
    /// the two defaults differ - the hint defaults to administrators-only while the stored column defaults
    /// to everyone.
    /// </remarks>
    private async Task<int> ReadProfileDefaultVisibilityAsync(int portalId, CancellationToken cancellationToken)
    {
        MembershipSettingsDto? settings =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false);

        return settings?.ProfileDefaultVisibility ?? new MembershipSettingsDto().ProfileDefaultVisibility;
    }

    /// <summary>
    /// Projects a tenant's profile property definitions in display order.
    /// </summary>
    /// <param name="portalId">The tenant whose definitions are read.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The definitions, ordered as configured.</returns>
    private async Task<IReadOnlyList<ProfilePropertyDefinitionDto>> ReadProfileDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProfilePropertyDefinition> definitions = await _profiles
            .GetDefinitionsByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        int defaultVisibility =
            await ReadProfileDefaultVisibilityAsync(portalId, cancellationToken).ConfigureAwait(false);

        return definitions
            .OrderBy(definition => definition.ViewOrder)
            .ThenBy(definition => definition.PropertyDefinitionId)
            .Select(definition => UserMappings.ToDto(definition, defaultVisibility))
            .ToList();
    }

    /// <summary>
    /// Tests a submitted profile value against the rules its definition declares.
    /// </summary>
    /// <param name="definition">The definition the value belongs to.</param>
    /// <param name="value">The submitted value.</param>
    /// <returns>The reason the value is unusable, or <see langword="null"/> when it is usable.</returns>
    private static ResultReason? ValidateProfileValue(ProfilePropertyDefinition definition, string value)
    {
        if (definition.Length > 0 && value.Length > definition.Length)
        {
            return new ResultReason(ProfileValueTooLongCode, FormattableString.Invariant(
                $"Profile property \"{definition.PropertyName}\" accepts at most {definition.Length} characters."));
        }

        if (string.IsNullOrWhiteSpace(definition.ValidationExpression) || value.Length == 0)
        {
            return null;
        }

        try
        {
            if (!Regex.IsMatch(
                    value,
                    definition.ValidationExpression,
                    RegexOptions.CultureInvariant,
                    ValidationExpressionTimeout))
            {
                return new ResultReason(ProfilePropertyValidationFailedCode, FormattableString.Invariant(
                    $"Profile property \"{definition.PropertyName}\" does not match the format it requires."));
            }
        }
        catch (ArgumentException)
        {
            return new ResultReason(ProfilePropertyValidationFailedCode, FormattableString.Invariant(
                $"The validation rule declared for profile property \"{definition.PropertyName}\" could not be applied."));
        }
        catch (RegexMatchTimeoutException)
        {
            return new ResultReason(ProfilePropertyValidationFailedCode, FormattableString.Invariant(
                $"The validation rule declared for profile property \"{definition.PropertyName}\" could not be applied."));
        }

        return null;
    }

    /// <summary>
    /// Writes a submitted profile value onto a row, choosing the column that can hold it.
    /// </summary>
    /// <param name="row">The row to write.</param>
    /// <param name="property">The submitted value.</param>
    /// <param name="utcNow">The instant the write is stamped with.</param>
    private static void WriteProfileValue(UserProfileValue row, UserProfileValueDto property, DateTime utcNow)
    {
        if (property.PropertyValue.Length > ProfileValueColumnLength)
        {
            row.PropertyValue = null;
            row.PropertyText = property.PropertyValue;
        }
        else
        {
            row.PropertyValue = property.PropertyValue;
            row.PropertyText = null;
        }

        row.Visibility = property.Visibility;
        row.LastUpdatedDate = utcNow;
    }

    /// <summary>
    /// Builds the submission that clears a stored profile answer without discarding its row.
    /// </summary>
    /// <param name="row">The stored answer being cleared.</param>
    /// <returns>A submission carrying an empty value and the row's existing visibility.</returns>
    /// <remarks>
    /// MIGRATION: this is how the legacy application cleared a profile answer, and it is why no
    /// value-deletion member exists on the repository contract. The membership provider declared only
    /// a reader and an upsert for profile values (<c>DataProvider.vb:L118</c> and <c>L119</c>), and a
    /// sweep of all eighty-eight upgrade scripts finds no procedure that deletes a
    /// <c>UserProfile</c> row and no <c>DELETE</c> statement against that table. An answer was
    /// cleared by upserting an empty value, which was representable because the legacy
    /// <c>Null.NullString</c> sentinel was the empty string rather than null.
    /// <para>
    /// The stored visibility is carried forward rather than reset, because the legacy upsert wrote
    /// the property's own visibility and clearing an answer never re-decided who could see the
    /// property. The timestamp is refreshed by <see cref="WriteProfileValue"/>, as the legacy write
    /// refreshed it on every call.
    /// </para>
    /// </remarks>
    private static UserProfileValueDto Cleared(UserProfileValue row) => new()
    {
        PropertyDefinitionId = row.PropertyDefinitionId,
        PropertyValue = string.Empty,
        Visibility = row.Visibility,
    };

    /// <summary>
    /// Writes one module setting, updating the stored row when it already exists.
    /// </summary>
    /// <param name="stored">The rows already held against the module.</param>
    /// <param name="moduleId">The module the setting belongs to.</param>
    /// <param name="settingName">The setting name.</param>
    /// <param name="settingValue">The value to store.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>A task that completes once the write is staged.</returns>
    private async Task UpsertModuleSettingAsync(
        IReadOnlyList<ModuleSetting> stored,
        int moduleId,
        string settingName,
        string settingValue,
        CancellationToken cancellationToken)
    {
        ModuleSetting? existing = stored.FirstOrDefault(candidate =>
            string.Equals(candidate.SettingName, settingName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.SettingValue = settingValue;
            return;
        }

        await _modules
            .AddModuleSettingAsync(
                new ModuleSetting
                {
                    ModuleId = moduleId,
                    SettingName = settingName,
                    SettingValue = settingValue,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a stored switch, falling back to the legacy default when it is absent or unreadable.
    /// </summary>
    /// <param name="map">The stored settings.</param>
    /// <param name="settingName">The setting name.</param>
    /// <param name="fallback">The legacy default.</param>
    /// <returns>The stored value, or the fallback.</returns>
    private static bool ReadBoolean(IReadOnlyDictionary<string, string> map, string settingName, bool fallback)
        => map.TryGetValue(settingName, out string? stored) && bool.TryParse(stored, out bool parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// Reads a stored whole number, falling back to the legacy default when it is absent or unreadable.
    /// </summary>
    /// <param name="map">The stored settings.</param>
    /// <param name="settingName">The setting name.</param>
    /// <param name="fallback">The legacy default.</param>
    /// <returns>The stored value, or the fallback.</returns>
    private static int ReadInteger(IReadOnlyDictionary<string, string> map, string settingName, int fallback)
        => map.TryGetValue(settingName, out string? stored)
            && int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;

    /// <summary>
    /// Reads a stored page reference, translating the legacy sentinel into an absence.
    /// </summary>
    /// <param name="map">The stored settings.</param>
    /// <param name="settingName">The setting name.</param>
    /// <returns>The referenced page, or <see langword="null"/> when none is set.</returns>
    /// <remarks>
    /// The legacy default for all three redirect settings was the integer sentinel, so a stored sentinel
    /// and an absent key both mean "no page selected".
    /// </remarks>
    private static int? ReadOptionalInteger(IReadOnlyDictionary<string, string> map, string settingName)
    {
        if (!map.TryGetValue(settingName, out string? stored)
            || !int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return null;
        }

        return parsed < 0 ? null : parsed;
    }

    /// <summary>
    /// Reads a stored string, falling back to the legacy default when it is absent.
    /// </summary>
    /// <param name="map">The stored settings.</param>
    /// <param name="settingName">The setting name.</param>
    /// <param name="fallback">The legacy default.</param>
    /// <returns>The stored value, or the fallback.</returns>
    private static string ReadString(IReadOnlyDictionary<string, string> map, string settingName, string fallback)
        => map.TryGetValue(settingName, out string? stored) ? stored : fallback;

    /// <summary>
    /// Renders a switch in the form the legacy store held.
    /// </summary>
    /// <param name="value">The value to render.</param>
    /// <returns>The stored representation.</returns>
    private static string WriteBoolean(bool value) => value ? bool.TrueString : bool.FalseString;

    /// <summary>
    /// Renders a page reference in the form the legacy store held, using the sentinel for an absence.
    /// </summary>
    /// <param name="value">The referenced page, when one is set.</param>
    /// <returns>The stored representation.</returns>
    private static string WriteOptionalInteger(int? value)
        => value is int page
            ? page.ToString(CultureInfo.InvariantCulture)
            : UnsetRedirectSettingValue;
}
