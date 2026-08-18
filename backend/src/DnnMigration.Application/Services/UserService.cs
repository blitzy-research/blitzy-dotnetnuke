using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Common;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Mapping;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>
/// Orchestrates accounts within a tenant: the account list and its filters, the account record itself, the
/// credential transitions an administrator performs, the tenant-wide membership settings, and the profile
/// property definitions together with the values accounts hold for them.
/// </summary>
/// <remarks>
/// <para>
/// Every member is tenant-scoped. The two whole-installation reads the legacy controller offered are
/// deliberately not reachable, because a member that crosses tenants is how one tenant's administrator
/// reaches another tenant's accounts.
/// </para>
/// <para>
/// Account columns, credential state, role membership and profile values are four separate concerns with
/// four separate write paths. An ordinary column edit therefore cannot reinstate a locked account, approve
/// an unapproved one or grant a role as a side effect.
/// </para>
/// </remarks>
public sealed class UserService : IUserService
{
    /// <summary>Resource kind published on an audit record describing an account.</summary>
    private const string UserResourceType = "User";

    /// <summary>
    /// Reported when more than one search filter is supplied, or when only one half of the profile-property
    /// pair is supplied.
    /// </summary>
    /// <remarks>
    /// The token deliberately reads <c>filter-invalid</c> rather than <c>filter-conflict</c>, and matches
    /// the name <c>PermissionService</c> already uses for the same situation. What this reports is a
    /// malformed query string: the caller must change the request, and no amount of retrying or re-reading
    /// resource state will help.
    /// </remarks>
    private const string ListFilterInvalidCode = "user.list.filter-invalid";

    /// <summary>Reported when the named profile property is not defined for the tenant.</summary>
    private const string ListUnknownProfilePropertyCode = "user.list.unknown-profile-property";

    /// <summary>
    /// Reported when a caller names an ordering the account listing cannot apply.
    /// </summary>
    private const string ListSortUnsupportedCode = "user.list.sort-unsupported";

    /// <summary>
    /// Reported when a caller names an ordering the account picker cannot apply, which is any name other
    /// than the two captions a choice carries.
    /// </summary>
    private const string ChoiceSortUnsupportedCode = "user.choices.sort-unsupported";

    /// <summary>Reported when no such account exists within the tenant.</summary>
    private const string NotFoundCode = "user.not-found";

    /// <summary>Reported when the account name is already taken somewhere in the installation.</summary>
    private const string CreateUsernameAlreadyExistsCode = "user.create.username-already-exists";

    /// <summary>Reported when the account name already holds a membership of this tenant.</summary>
    private const string CreateUserAlreadyRegisteredCode = "user.create.user-already-registered";

    /// <summary>Reported when the credential store already holds a credential for the account name.</summary>
    /// <remarks>
    /// The legacy creation status carried both this member and <see
    /// cref="CreateUsernameAlreadyExistsCode"/> because two different checks produced them - the
    /// application's own pre-check, and the membership provider's own duplicate detection. That distinction
    /// is preserved: the pre-check reports the former, the credential store the latter.
    /// </remarks>
    private const string CreateDuplicateUsernameCode = "user.create.duplicate-username";

    /// <summary>
    /// Reported when the electronic-mail address is already in use and the deployment requires addresses to
    /// be unique.
    /// </summary>
    private const string CreateDuplicateEmailCode = "user.create.duplicate-email";

    /// <summary>Reported when the submitted account name cannot be used.</summary>
    private const string CreateInvalidUsernameCode = "user.create.invalid-username";

    /// <summary>Reported when the submitted electronic-mail address cannot be used.</summary>
    private const string CreateInvalidEmailCode = "user.create.invalid-email";

    /// <summary>
    /// Reported when a caller supplies a concurrency token that no longer matches the account it read.
    /// </summary>
    /// <remarks>
    /// The reason token <c>concurrency_conflict</c> is what the API surface maps to <c>409 Conflict</c>, and
    /// it is spelled identically here to the role and tenant equivalents so one classification rule covers
    /// all three.
    /// </remarks>
    private const string ConcurrencyConflictCode = "user.concurrency_conflict";

    /// <summary>Reported when the submitted credential does not satisfy the configured policy.</summary>
    private const string CreateInvalidPasswordCode = "user.create.invalid-password";

    /// <summary>Reported when the submitted credential and its confirmation differ.</summary>
    private const string CreatePasswordMismatchCode = "user.create.password-mismatch";

    /// <summary>Reported when the tenant the account is being created in does not exist.</summary>
    private const string CreatePortalAssignmentFailedCode = "user.create.portal-assignment-failed";

    /// <summary>Reported when the credential store could not be written.</summary>
    private const string CreateProviderErrorCode = "user.create.provider-error";

    /// <summary>Reported when a concurrent edit changed the row first.</summary>
    private const string PersistenceConflictCode = "persistence.conflict";

    /// <summary>Reported when the account is the tenant's designated administrator.</summary>
    private const string DeleteAdministratorProtectedCode = "user.delete.administrator-protected";

    /// <summary>Reported when the account is a host-level account and beyond tenant administration.</summary>
    private const string DeleteSuperUserProtectedCode = "user.delete.superuser-protected";

    /// <summary>Reported when a credential the operation requires was not supplied.</summary>
    private const string PasswordMissingCode = "user.password.missing";

    /// <summary>Reported when the submitted credential does not satisfy the configured policy.</summary>
    private const string PasswordInvalidCode = "user.password.invalid";

    /// <summary>Reported when the submitted credential and its confirmation differ.</summary>
    private const string PasswordMismatchCode = "user.password.mismatch";

    /// <summary>Reported when the submitted credential is the one already stored.</summary>
    private const string PasswordNotDifferentCode = "user.password.not-different";

    /// <summary>Reported when the credential store refused the write.</summary>
    private const string PasswordResetFailedCode = "user.password.reset-failed";

    /// <summary>
    /// Reported when the credential changed between this request reading it and replacing it, so the
    /// replacement was refused rather than applied over the newer value.
    /// </summary>
    private const string PasswordSupersededCode = "user.password.superseded";

    /// <summary>
    /// Reported when an operation that must end an account's sessions could not have them revoked, so the
    /// operation itself was abandoned rather than completed with the sessions left alive.
    /// </summary>
    /// <remarks>
    /// Its reason token ends in <c>store_unavailable</c>, which the Api edge classifies as a dependency
    /// failure and answers <c>503</c>. That is the honest reading: the request was valid and the refusal is
    /// temporary, so a caller may retry it.
    /// </remarks>
    private const string SessionRevocationFailedCode = "user.session.revocation_store_unavailable";

    /// <summary>
    /// Reported when the credential store could not be REACHED during deletion, so the deletion was
    /// abandoned rather than committed with the credential left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A credential surviving the account row is unreachable by any administrative screen and is revisited
    /// by no later deletion, so reporting success over one would be a permanent, invisible remnant. Its
    /// reason token ends in <c>store_unavailable</c> so the Api edge answers <c>503</c> and the caller
    /// learns the refusal is temporary.
    /// </para>
    /// <para>
    /// ⚠ IT IS NOW RAISED FOR AN UNREACHABLE STORE ALONE. It used to be raised for an absent credential
    /// record too, because the store reported both as one <see langword="bool"/>, and that made the two
    /// callers racing to delete one account irreconcilable with the truth: the loser was answered <c>503</c>
    /// with a message stating that the account "was left intact" while the account had already been deleted
    /// by the winner. An absent record is the end state this deletion is reaching for, so the cascade now
    /// continues through it.
    /// </para>
    /// </remarks>
    private const string CredentialRemovalFailedCode = "user.credential.removal_store_unavailable";

    /// <summary>Reported when a self-service change presents the wrong current credential.</summary>
    private const string PasswordCurrentIncorrectCode = "user.password.current-incorrect";

    /// <summary>Reported when the deployment has administrative reset switched off.</summary>
    private const string PasswordResetNotEnabledCode = "user.password.reset-not-enabled";

    /// <summary>Reported for an unrecognised operation discriminator.</summary>
    private const string PasswordUnsupportedOperationCode = "user.password.unsupported-operation";

    // NO CODE FOR "THE ACCOUNT WAS NOT LOCKED", DELIBERATELY. Clearing a lockout is idempotent: the
    // unlocked state is the requested end state, so reaching it again succeeds. The constant that used to
    // live here - `user.unlock.not-locked` - was removed with the refusal it named, and the classification
    // table lost its entry in the same change so that nothing suggests the refusal is still reachable.

    /// <summary>Reported when an administrator invokes a membership transition on their own account.</summary>
    private const string MembershipSelfForbiddenCode = "user.membership.self-forbidden";

    /// <summary>Reported when a caller asks to change a credential that is not their own.</summary>
    /// <remarks>
    /// A change is proof of possession of the current credential and is therefore the account owner's
    /// operation. An administrator acting on another account uses the reset operation instead.
    /// </remarks>
    private const string PasswordChangeSelfOnlyForbiddenCode = "user.password.change-self-only-forbidden";

    /// <summary>
    /// Reported when a caller without administrative authority over the tenant asks to reset a credential.
    /// </summary>
    /// <remarks>
    /// This is the check that keeps the reset operation from being a way around the change operation's
    /// current-credential requirement. Without it the owner of an account - who legitimately passes the
    /// route policy - could set a new credential without presenting the old one.
    /// </remarks>
    private const string PasswordResetForbiddenCode = "user.password.reset-forbidden";

    /// <summary>Reported when the account is already in the requested approval state.</summary>
    private const string ApprovalUnchangedCode = "user.approval.unchanged";

    /// <summary>Reported when the account already carries the forced-change flag.</summary>
    private const string PasswordChangeAlreadyRequiredCode = "user.password.change-already-required";

    /// <summary>Reported when the tenant has no settings source to write membership settings to.</summary>
    private const string MembershipSettingsSourceMissingCode = "user.membership-settings.storage-conflict";

    /// <summary>Reported when a membership redirect names a page outside the addressed portal.</summary>
    private const string MembershipRedirectInvalidCode = "user.membership-settings.redirect-invalid";

    /// <summary>
    /// Reported when a submitted membership setting is outside its legal range or width, or when the
    /// electronic-mail validation expression cannot be compiled.
    /// </summary>
    /// <remarks>
    /// The Api edge maps a code ending in a bare noun to 400, which is the answer a caller that submitted
    /// an out-of-range value deserves; the declarative validator answers the same condition with a
    /// field-level problem document when the request came through the pipeline.
    /// </remarks>
    private const string MembershipSettingsInvalidCode = "user.membership-settings.invalid";

    /// <summary>Reported when a membership-settings redirect member names a page the tenant does not own.</summary>
    /// <remarks>
    /// MIGRATION: a second revision reported the same condition as <c>redirect.not-found</c> from a helper
    /// that took the READ projection. That helper could never run - no endpoint binds the read shape - so
    /// the code it reported was unreachable and both are withdrawn in favour of this one.
    /// </remarks>
    private const string MembershipSettingsRedirectNotInPortalCode =
        "user.membership-settings.redirect_not_in_portal";

    /// <summary>Reported when the configured display-name format expands beyond the stored account column.</summary>
    private const string DisplayNameTooLongCode = "user.display-name.too-long";

    /// <summary>Reported when a submitted profile set names a property the tenant does not define.</summary>
    private const string ProfileUnknownPropertyCode = "user.profile.unknown-property";

    /// <summary>Reported when a property marked as required carries no value.</summary>
    private const string ProfileRequiredPropertyMissingCode = "user.profile.required-property-missing";

    /// <summary>Reported when a value exceeds the length its definition declares.</summary>
    private const string ProfileValueTooLongCode = "user.profile.value-too-long";

    /// <summary>Reported when a value fails the validation expression its definition declares.</summary>
    private const string ProfilePropertyValidationFailedCode = "user.profile.property-validation-failed";

    /// <summary>Reported when one profile submission repeats a property definition identifier.</summary>
    private const string ProfileDuplicatePropertyCode = "user.profile.duplicate-property";

    /// <summary>Reported when a profile submission exceeds the bounded amount of per-property work.</summary>
    private const string ProfileTooManyPropertiesCode = "user.profile.too-many-properties";

    /// <summary>Reported when a profile value carries an unsupported visibility discriminator.</summary>
    private const string ProfileVisibilityInvalidCode = "user.profile.visibility-invalid";

    /// <summary>
    /// Reported when a tenant-authored validation expression is malformed or exceeds safety bounds.
    /// </summary>
    private const string ProfileDefinitionExpressionInvalidCode =
        "profile-definition.validation-expression-invalid";

    /// <summary>Reported when the tenant already declares a profile property of the submitted name.</summary>
    private const string ProfileDefinitionDuplicateNameCode = "profile-definition.duplicate-name";

    /// <summary>Reported when no such profile property definition exists within the tenant.</summary>
    private const string ProfileDefinitionNotFoundCode = "profile-definition.not-found";

    /// <summary>Reported when withdrawal is refused because the declaration is one the platform reserves.</summary>
    public const string ProfileDefinitionProtectedCode = "profile-definition.protected";

    /// <summary>
    /// Reported when withdrawal would destroy recorded answers and the caller has not stated how many.
    /// </summary>
    public const string ProfileDefinitionValueDeletionUnacknowledgedCode =
        "profile-definition.value-deletion-unacknowledged";

    /// <summary>
    /// The four declarations the legacy administration screen refused to let an operator withdraw, compared
    /// case-insensitively exactly as it compared them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>grdProfileProperties_ItemDataBound</c> reached into the delete column and set the command invisible
    /// for these four names, tested against <c>PropertyName.ToLower</c>. In the legacy application that WAS
    /// the enforcement: the administration grid was the only path to the operation, so hiding the command
    /// made it unreachable.
    /// </para>
    /// <para>
    /// ⚠ REPRODUCING ONLY THE HIDDEN BUTTON WOULD HAVE BEEN A LOSS OF PROTECTION, NOT PARITY. This API is a
    /// second path that the legacy design never had, and it is reachable by any authenticated administrator
    /// with a single request. Restating the rule here is what keeps the OBSERVABLE behaviour of the system
    /// the same - these four cannot be withdrawn - rather than keeping the implementation the same and
    /// quietly widening what the system permits.
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> ProtectedProfilePropertyNames =
        new[] { "firstname", "lastname", "timezone", "preferredlocale" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reported when no such role exists within the tenant.</summary>
    /// <remarks>
    /// Spelled BYTE FOR BYTE as the sibling role service spells it - with an underscore, which is that
    /// service's convention rather than this one's hyphen - because the member-services operations
    /// pre-resolve the role and then delegate the write to that service, and the two must not hand a client
    /// two different codes for one condition.
    /// </remarks>
    private const string ServiceRoleNotFoundCode = "role.not_found";

    /// <summary>Reported when the member-services operation names a tenant that does not exist.</summary>
    /// <remarks>
    /// Spelled byte for byte as the sibling role service spells it, for the reason recorded on <see
    /// cref="ServiceRoleNotFoundCode"/>. It is the only portal-existence code this service raises: the
    /// account-creation path reports a missing tenant as a portal-assignment failure instead, because there
    /// the tenant is a target of a write rather than the resource being addressed.
    /// </remarks>
    private const string ServicePortalNotFoundCode = "portal.not_found";

    /// <summary>Reported when the tenant has switched self-service subscription off.</summary>
    private const string ServiceDisabledCode = "user.service.disabled-forbidden";

    /// <summary>
    /// Reported when the addressed role is not one the tenant publishes for self-service subscription.
    /// </summary>
    private const string ServiceNotOfferedCode = "user.service.not-offered-forbidden";

    /// <summary>Reported when completing a subscription or a cancellation would require taking payment.</summary>
    private const string ServicePaymentRequiredCode = "user.service.payment-required-forbidden";

    /// <summary>Reported when the addressed role offers this account no free trial.</summary>
    private const string ServiceTrialNotOfferedCode = "user.service.trial-not-offered-forbidden";

    /// <summary>Reported when an invitation-code redemption carries no code.</summary>
    /// <remarks>
    /// Duplicates <see cref="RedeemServiceCodeRequestValidator"/> rather than trusting it, so the guard
    /// holds for a caller that reached this service without the validation pipeline in front of it. Both
    /// sites are annotated so a change to either is visibly a change to a pair.
    /// </remarks>
    private const string ServiceCodeRequiredCode = "user.service.code-required";

    /// <summary>Reported when no role in the tenant bears the submitted invitation code.</summary>
    private const string ServiceCodeNotMatchedCode = "user.service.code-not-matched";

    /// <summary>
    /// Reported when a catalogue page request names an ordering or a filter the catalogue does not offer.
    /// </summary>
    /// <remarks>
    /// Hyphenated like its siblings above; the API layer normalises a code before looking up its status, so
    /// the hyphen and the underscore spellings are the same code and only one status can be reached.
    /// </remarks>
    private const string MemberServicePagingInvalidCode = "user.service.paging-invalid";

    /// <summary>
    /// Cache key holding a tenant's projected profile property definitions, carried over verbatim from
    /// <c>DataCache.ProfileDefinitionsCacheKey</c>.
    /// </summary>
    private const string ProfileDefinitionsCacheKeyFormat = "ProfileDefinitions{0}";

    /// <summary>
    /// Base expiry of the definition cache, matching <c>DataCache.ProfileDefinitionsCacheTimeOut</c>.
    /// </summary>
    private const int ProfileDefinitionsCacheTimeOutMinutes = 20;

    /// <summary>Page size that requests every match unpaged, per the repository contracts.</summary>
    private const int UnpagedPageSize = 0;

    /// <summary>
    /// Longest value <c>dbo.UserProfile.PropertyValue</c> can hold; longer values move to the unbounded
    /// text column beside it.
    /// </summary>
    private const int ProfileValueColumnLength = 3750;

    /// <summary>Maximum number of profile values one request may validate and reconcile.</summary>
    private const int ProfilePropertySubmissionMaximum = 64;

    /// <summary>Maximum length accepted for tenant-authored regular expressions.</summary>
    private const int ValidationExpressionMaximumLength = 512;

    /// <summary>Maximum number of compiled tenant expressions retained process-wide.</summary>
    /// <remarks>
    /// INFO-01: the cache holds at most this many entries and, once full, evicts exactly ONE entry per
    /// newly admitted expression rather than clearing itself.
    /// </remarks>
    private const int ValidationExpressionCacheMaximum = 1024;

    /// <summary>
    /// Number of accounts above which the legacy membership settings defaulted the account picker to a
    /// free-text control rather than a drop-down.
    /// </summary>
    private const int LargeTenantAccountThreshold = 1000;

    /// <summary>Terminal width of <c>dbo.Users.DisplayName</c>.</summary>
    /// <remarks>
    /// Measured as <c>nvarchar(128)</c> in both the terminal DDL and <c>UserConfiguration</c>. <see
    /// cref="string.Length"/> counts UTF-16 code units, which is the same unit SQL Server uses for this
    /// column's declared length.
    /// </remarks>
    private const int DisplayNameMaximumLength = 128;

    /// <summary>Legacy integer sentinel meaning "no page selected" in the three redirect settings.</summary>
    private const string UnsetRedirectSettingValue = "-1";

    /// <summary>Bound on how long a tenant-authored validation expression may run before it is abandoned.</summary>
    /// <remarks>
    /// A profile property's validation expression is tenant data rather than code, so a pathological
    /// expression must not be able to occupy a request thread indefinitely.
    /// </remarks>
    private static readonly TimeSpan ValidationExpressionTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Bounded cache of validated regular-expression objects, keyed by the exact stored expression.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> ValidationExpressionCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Profile property names the legacy account grid composed its address column from, in the order it
    /// composed them.
    /// </summary>
    private static readonly string[] AddressProfilePropertyNames =
        ["Unit", "Street", "City", "Region", "Country", "PostalCode"];

    /// <summary>Profile property name the legacy account grid displayed as its telephone column.</summary>
    private const string TelephoneProfilePropertyName = "Telephone";

    private readonly IUserRepository _users;
    private readonly IUserProfileRepository _profiles;
    private readonly IRoleRepository _roles;
    private readonly IPermissionService _permissions;

    /// <summary>
    /// The role contract, reached for the two membership primitives the member-services surface is built
    /// out of.
    /// </summary>
    private readonly IRoleService _roleService;
    private readonly IPortalRepository _portals;
    private readonly IModuleRepository _modules;
    private readonly IModuleDefinitionRepository _definitions;
    private readonly ITabRepository _tabs;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;
    private readonly ICacheService _cache;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditSink _audit;
    private readonly ITokenService _tokens;
    private readonly IStoreFailureClassifier _storeFailures;
    private readonly ISecurityDiagnostics _diagnostics;
    private readonly PasswordPolicyOptions _passwordPolicy;
    private readonly CachingOptions _caching;
    private readonly IPortalContextHolder _portalContext;

    /// <summary>
    /// Initialises the service with the collaborators it reaches the store, the credential policy and the
    /// cache through.
    /// </summary>
    /// <param name="users">Account, membership and credential persistence.</param>
    /// <param name="profiles">Profile property definitions and the values held against them.</param>
    /// <param name="roles">Role lookups, used for automatic enrolment and for role projection.</param>
    /// <param name="permissions">
    /// Permission grant cleanup on deletion, reached through the application contract that owns it rather
    /// than through the grant repository directly.
    /// </param>
    /// <param name="roleService">
    /// The role contract, reached only by the member-services operations and only for the two membership
    /// primitives they are built out of, so that the expiry derivation and the protected-assignment rules
    /// keep exactly one implementation.
    /// </param>
    /// <param name="portals">Tenant existence, the designated administrator and the account count.</param>
    /// <param name="modules">Module settings persistence, where membership settings are stored.</param>
    /// <param name="definitions">Definition lookups, used to locate the settings source module.</param>
    /// <param name="tabs">Page lookups.</param>
    /// <param name="unitOfWork">The single commit point for every write below.</param>
    /// <param name="passwordHasher">One-way credential hashing and verification.</param>
    /// <param name="clock">The clock every timestamp is taken from.</param>
    /// <param name="cache">Cache reads and invalidation.</param>
    /// <param name="currentUser">The acting caller, needed by the self-service prohibitions.</param>
    /// <param name="audit">Receives the business audit record for a created or removed account.</param>
    /// <param name="tokens">Session revocation.</param>
    /// <param name="storeFailures">
    /// Classifies a caught exception as a failure of the store rather than of this service.
    /// </param>
    /// <param name="passwordPolicy">Bound credential policy, preserved from the legacy configuration.</param>
    /// <param name="diagnostics">
    /// The route by which this service reports a security-relevant anomaly it has decided not to publish to
    /// the caller.
    /// </param>
    /// <param name="caching">Bound caching configuration supplying the performance multiplier.</param>
    /// <param name="portalContext">
    /// The tenant facts already settled for the call being served, read for exactly one purpose: the
    /// designated administrator, which the detail projection needs in order to publish the removal
    /// capability.
    /// </param>
    public UserService(
        IUserRepository users,
        IUserProfileRepository profiles,
        IRoleRepository roles,
        IPermissionService permissions,
        IRoleService roleService,
        IPortalRepository portals,
        IModuleRepository modules,
        IModuleDefinitionRepository definitions,
        ITabRepository tabs,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IClock clock,
        ICacheService cache,
        ICurrentUser currentUser,
        IAuditSink audit,
        ITokenService tokens,
        IStoreFailureClassifier storeFailures,
        ISecurityDiagnostics diagnostics,
        PasswordPolicyOptions passwordPolicy,
        CachingOptions caching,
        IPortalContextHolder portalContext)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _roleService = roleService ?? throw new ArgumentNullException(nameof(roleService));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _storeFailures = storeFailures ?? throw new ArgumentNullException(nameof(storeFailures));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _passwordPolicy = passwordPolicy ?? throw new ArgumentNullException(nameof(passwordPolicy));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Returns the account the tenant designates as its administrator, or <see langword="null"/>.</summary>
    /// <param name="portalId">The tenant whose designated administrator is wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// The designated administrator's account identifier, or <see langword="null"/> when the tenant
    /// designates nobody or does not exist.
    /// </returns>
    /// <remarks>
    /// ⚠ THE TENANT FACTS OF THE CALL ARE ALREADY IN HAND, AND READING THE PORTAL AGAIN TO GET ONE COLUMN
    /// OF THEM IS REDUNDANT. Every request that reaches this service has had its tenant resolved by
    /// <c>Api/Middleware/PortalAliasResolutionMiddleware</c> or by the portal-administrator authorisation
    /// handler, whichever ran first, and the snapshot they settled ALREADY CARRIES <c>AdministratorId</c> -
    /// it is a declared member of <c>IPortalContext</c>.
    /// </remarks>
    private async Task<int?> ReadDesignatedAdministratorAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        if (_portalContext.IsResolved && _portalContext.Current.PortalId == portalId)
        {
            return _portalContext.Current.AdministratorId;
        }

        Portal? owner = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        return owner?.AdministratorId;
    }

    /// <summary>Records one committed account change on the audit trail.</summary>
    /// <param name="eventName">The stable event name, from <see cref="AuditEventNames"/>.</param>
    /// <param name="portalId">The tenant the change was made within.</param>
    /// <param name="subjectUserId">The account the change was made against.</param>
    /// <param name="properties">Short, non-sensitive machine-readable facts about the change.</param>
    /// <remarks>
    /// Called only after the change has been committed. The acting account comes from the credential, and
    /// it is not always the account acted upon: an administrator creating a member records the
    /// administrator as the actor and the member as the subject, a distinction the legacy record - which
    /// had a single user field - could not express.
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
            SubjectUserId = subjectUserId,
            ResourceType = UserResourceType,
            ResourceId = subjectUserId.ToString(CultureInfo.InvariantCulture),
            Properties = properties,
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// The address and telephone columns the grid displayed are profile values rather than account columns,
    /// so they are composed here from the six address properties the legacy helper concatenated and from
    /// the telephone property.
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

        // The PER-COLLECTION ordering set is enforced HERE, against this collection's own set rather than
        // against the union.
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

        MembershipSettingsDto visibility =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false)
            ?? new MembershipSettingsDto();

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
        // contain, which is how an accepted sort field comes to be silently ignored.
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
        if (visibility.ColumnAddress)
        {
            foreach (string propertyName in AddressProfilePropertyNames)
            {
                ProfilePropertyDefinition? part = definitions.FirstOrDefault(candidate =>
                    string.Equals(candidate.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase));

                if (part is not null)
                {
                    addressPropertyIds.Add(part.PropertyDefinitionId);
                }
            }
        }

        int? telephonePropertyId = visibility.ColumnTelephone
            ? definitions.FirstOrDefault(candidate =>
                string.Equals(candidate.PropertyName, TelephoneProfilePropertyName, StringComparison.OrdinalIgnoreCase))
                ?.PropertyDefinitionId
            : null;

        bool profileValuesWanted = addressPropertyIds.Count > 0 || telephonePropertyId is not null;

        Dictionary<int, List<UserProfileValue>> profileValuesByUser = new();

        if (profileValuesWanted && matches.Items.Count > 0)
        {
            IReadOnlyList<UserProfileValue> pageValues = await _profiles
                .GetProfileValuesAsync(
                    portalId,
                    matches.Items.Select(account => account.UserId).Distinct().ToList(),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (UserProfileValue value in pageValues)
            {
                if (!profileValuesByUser.TryGetValue(value.UserId, out List<UserProfileValue>? group))
                {
                    group = new List<UserProfileValue>();
                    profileValuesByUser[value.UserId] = group;
                }

                group.Add(value);
            }
        }

        int? designatedAdministrator = null;
        if (matches.Items.Count > 0)
        {
            Portal? owner = await _portals
                .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
                .ConfigureAwait(false);

            designatedAdministrator = owner?.AdministratorId;
        }

        var rows = new List<UserListItemDto>(matches.Items.Count);
        foreach (User account in matches.Items)
        {
            string? address = null;
            string? telephone = null;

            if (profileValuesWanted)
            {
                // An account that has recorded no answers contributes no row to the batched read, which is
                // the same state the per-account read reported as an empty list.
                IReadOnlyList<UserProfileValue> values =
                    profileValuesByUser.TryGetValue(account.UserId, out List<UserProfileValue>? stored)
                        ? stored
                        : Array.Empty<UserProfileValue>();

                address = ComposeAddress(values, addressPropertyIds);

                if (telephonePropertyId is int telephoneId)
                {
                    telephone = StoredValue(
                        values.FirstOrDefault(value => value.PropertyDefinitionId == telephoneId));
                }
            }

            UserListItemDto row = UserMappings.ToListItem(
                account,
                portalId,
                address,
                telephone,
                designatedAdministrator);

            WithholdProfileValuesTheTenantHides(row, visibility);

            rows.Add(row);
        }

        return Result<PagedResult<UserListItemDto>>.Success(
            page.PageSize == UnpagedPageSize
                ? PagedResult<UserListItemDto>.Unpaged(rows)
                : PagedResult<UserListItemDto>.Create(rows, matches.TotalCount, matches.PageIndex, matches.PageSize));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ THE READS THIS MEMBER DOES NOT PERFORM ARE THE POINT OF IT. <see cref="ListUsersAsync"/> reads the
    /// tenant's account-policy settings to learn which columns it may publish, reads the tenant's profile
    /// property definitions, issues a batched profile-value read to fill the address and telephone columns,
    /// and reads the portal to learn which account it must not offer for deletion.
    /// </remarks>
    public async Task<Result<PagedResult<UserChoiceDto>>> ListAccountChoicesAsync(
        int portalId,
        PagedRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        EnsurePagingIsUsable(page);

        if (!SortableFields.IsPermittedFor(page.SortBy, SortableFields.UserChoices))
        {
            return Result<PagedResult<UserChoiceDto>>.Failure(
                ChoiceSortUnsupportedCode,
                $"Account choices cannot be ordered by '{page.SortBy}'.");
        }

        EnsureFilterIsNotBlank(page.HasQuery ? page.Query : null, nameof(page.Query));

        PagedResult<AccountChoice> matches = await _users.ListAccountChoicesAsync(
            portalId,
            page.PageIndex,
            page.PageSize,
            // Normalised to one canonical representation of absence, exactly as the account listing
            // normalises its own, so the store is never handed blank text to decide about.
            page.HasQuery ? page.Query : null,
            string.IsNullOrWhiteSpace(page.SortBy) ? null : page.SortBy,
            page.SortDir == SortDirection.Descending,
            cancellationToken).ConfigureAwait(false);

        var rows = new List<UserChoiceDto>(matches.Items.Count);
        foreach (AccountChoice choice in matches.Items)
        {
            rows.Add(UserMappings.ToChoice(choice));
        }

        return Result<PagedResult<UserChoiceDto>>.Success(
            page.PageSize == UnpagedPageSize
                ? PagedResult<UserChoiceDto>.Unpaged(rows)
                : PagedResult<UserChoiceDto>.Create(rows, matches.TotalCount, matches.PageIndex, matches.PageSize));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Absence is not a failure, so an account the tenant does not hold reads as a successful result with
    /// no value. The legacy caching wrapper is not reproduced on this read: its key was composed from the
    /// account name (<c>DataCache.UserCacheKey</c>, "UserInfo|{0}|{1}"), which this member does not have
    /// before it reads, so caching here would need a second key shape for the same payload.
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

        int? designatedAdministrator = await ReadDesignatedAdministratorAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return Result<UserDetailDto?>.Success(
            UserMappings.ToDetail(account, portalId, roles, designatedAdministrator));
    }

    /// <inheritdoc />
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

        // THE CANONICAL ACCOUNT NAME IS COMPUTED ONCE AND USED EVERYWHERE BELOW - for the two uniqueness
        // reads, for the display-name format, for the credential store's own lookup and for every message.
        string username = request.Username.Trim();

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Result<UserDetailDto>.Failure(
                CreateInvalidEmailCode,
                "An electronic-mail address is required.");
        }

        Result<bool> validEmail = await IsEmailValidAsync(portalId, request.Email, cancellationToken)
            .ConfigureAwait(false);
        if (validEmail.IsFailure || !validEmail.Value)
        {
            return Result<UserDetailDto>.Failure(
                CreateInvalidEmailCode,
                "The electronic-mail address does not satisfy this portal's validation rule.");
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

        // An omitted confirmation is not a mismatch.
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
            .GetByUsernameAsync(portalId: null, username, cancellationToken)
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
                        $"Account \"{username}\" is already registered in portal {portalId}."))
                : Result<UserDetailDto>.Failure(
                    CreateUsernameAlreadyExistsCode,
                    FormattableString.Invariant($"Account name \"{username}\" is already in use."));
        }

        if (await _users.UsernameExistsAsync(username, excludingUserId: null, cancellationToken)
            .ConfigureAwait(false))
        {
            return Result<UserDetailDto>.Failure(
                CreateUsernameAlreadyExistsCode,
                FormattableString.Invariant($"Account name \"{username}\" is already in use."));
        }

        if (_passwordPolicy.RequiresUniqueEmail
            && await _users.EmailExistsAsync(portalId, request.Email, excludingUserId: null, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<UserDetailDto>.Failure(
                CreateDuplicateEmailCode,
                "The electronic-mail address is already in use.");
        }

        MembershipSettingsDto? tenantPolicy =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false);

        string? displayNameFormat =
            tenantPolicy is not null && !string.IsNullOrWhiteSpace(tenantPolicy.SecurityDisplayNameFormat)
                ? tenantPolicy.SecurityDisplayNameFormat
                : null;

        DateTime now = _clock.UtcNow;
        User account = UserMappings.ToNewUser(request);

        account.UserPortals.Add(new UserPortal
        {
            PortalId = portalId,
            CreatedDate = now,
            IsAuthorised = request.Authorize,
        });

        // The auto-assignment filter is applied here rather than in the repository because the legacy
        // membership provider had no such procedure: it exposed GetPortalRoles alone and the AutoAssignment
        // column was tested by the caller.
        IReadOnlyList<Role> portalRoles =
            await _roles.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        List<Role> automatic = portalRoles.Where(role => role.AutoAssignment).ToList();

        foreach (Role role in automatic)
        {
            account.UserRoles.Add(new UserRole { RoleId = role.RoleId });
        }

        await using (ITransactionScope transaction = await _unitOfWork
            .JoinOrBeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            _users.Add(account);
            // THE FOUR CHECKS ABOVE CANNOT CLOSE THE RACE. IX_Users is unique over the account name, so two
            // requests carrying the same name arriving together both read "not taken" and the loser's
            // insert is refused by the index.
            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DuplicateKeyException)
            {
                return Result<UserDetailDto>.Failure(
                    CreateUserAlreadyRegisteredCode,
                    FormattableString.Invariant(
                        $"Account \"{username}\" is already registered in portal {portalId}."));
            }

            // THE FORMAT IS APPLIED HERE, AFTER THE INSERT, AND THE POSITION IS THE ONE DIVERGENCE.
            if (displayNameFormat is not null)
            {
                string formatted = FormatDisplayName(
                    displayNameFormat,
                    account.UserId,
                    request.FirstName,
                    request.LastName,
                    username);

                if (formatted.Length > DisplayNameMaximumLength)
                {
                    // The same guard the update path applies, with the same code, because it is the same
                    // stored width being protected. Refused rather than truncated: a silently shortened
                    // display name is a value the tenant did not ask for and cannot see it did not get.
                    return Result<UserDetailDto>.Failure(
                        DisplayNameTooLongCode,
                        FormattableString.Invariant(
                            $"The tenant's display-name format produces {formatted.Length} characters for account {account.UserId}; the stored limit is {DisplayNameMaximumLength}."));
                }

                if (!string.Equals(account.DisplayName, formatted, StringComparison.Ordinal))
                {
                    account.DisplayName = formatted;

                    await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            string credentialHash = _passwordHasher.Hash(credential);

            try
            {
                bool created = await _users.CreateCredentialAsync(
                    account.UserId,
                    credentialHash,
                    request.Authorize,
                    now,
                    cancellationToken).ConfigureAwait(false);

                if (!created)
                {
                    return Result<UserDetailDto>.Failure(
                        CreateDuplicateUsernameCode,
                        FormattableString.Invariant(
                            $"The credential store already holds a credential for account name \"{username}\"."));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && _storeFailures.IsStoreUnavailable(exception))
            {
                // The published text no longer carries exception.GetType().Name.
                _diagnostics.Record(
                    SecurityDiagnosticEvent.CredentialStoreWriteFailed,
                    portalId,
                    account.UserId,
                    exception.GetType().Name);

                return Result<UserDetailDto>.Failure(
                    CreateProviderErrorCode,
                    "The credential store could not be written, so the account was not created. Try again, "
                    + "and quote the correlation identifier from the response if the problem persists.");
            }

            // The one point at which the account becomes visible to anything else. Both the account row
            // and its credential are published here, together.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The membership facts live in the external credential store and are projections rather than
        // columns, so they are set here for the response the caller receives.
        account.IsApproved = request.Authorize;
        account.IsLockedOut = false;
        account.CreatedDate = now;
        account.LastPasswordChangeDate = now;

        _cache.InvalidatePortal(portalId);
        _cache.InvalidateUser(portalId, account.Username);

        // Reproduces the legacy USER_CREATED audit entry. Recorded only after the commit, so an account
        // whose creation was rolled back - because the credential store refused it or was unreachable -
        // never produces a record.
        RecordAudit(
            AuditEventNames.UserCreated,
            portalId,
            account.UserId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Approved"] = request.Authorize.ToString(CultureInfo.InvariantCulture),
            });

        IReadOnlyList<string> roleNames = automatic.Select(role => role.RoleName).ToList();

        // ⚠ NO ADMINISTRATOR READ ON THE CREATE PATH, AND THE OMISSION IS PROVABLY EQUIVALENT RATHER THAN
        // AN APPROXIMATION. The capability withholds removal from the account named by
        // Portals.AdministratorId, compared by equality against Users.UserID. This account was inserted a
        // moment ago and carries a fresh identity, so it cannot be the account an existing designation
        // names - the comparison could only ever be false.
        return Result<UserDetailDto>.Success(
            UserMappings.ToDetail(account, portalId, roleNames, portalAdministratorId: null));
    }

    /// <inheritdoc />
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

        // OPTIMISTIC CONCURRENCY, CHECKED BEFORE ANY FIELD RULE RUNS. The order matches the role path's for
        // the same reason: a caller holding a stale snapshot must be told that the record moved under it,
        // not that some field of the snapshot it is trying to restore is unacceptable. It is also checked
        // before the grandfathering test below, because "the address is unchanged" is a claim about a
        // snapshot, and a stale snapshot is exactly the case where that claim cannot be trusted.
        if (!ConcurrencyToken.Matches(request.ConcurrencyToken, UserMappings.ConcurrencyTokenFor(account)))
        {
            return Result<UserDetailDto>.Failure(
                ConcurrencyConflictCode,
                FormattableString.Invariant(
                    $"Account {userId} was changed by someone else after you read it, so nothing was written. Reload the account to see the current values, then apply your change again."));
        }

        // ⚠ AN UNCHANGED STORED ADDRESS IS GRANDFATHERED, AND WITHOUT THIS AN EXISTING ACCOUNT CAN BECOME
        // UNMAINTAINABLE. Two independent rules govern an e-mail address here. The SHAPE rule lives in
        // Domain/ValueObjects/EmailAddress and is applied to every submission by
        // UpdateUserRequestValidator; the ADMISSION rule is the tenant's own Security_EmailValidation
        // expression, which an operator may set to anything. They are different questions and both are
        // legitimate - but only one of them was ever asked of the value already in the column.
        //
        // The measured consequence: DotNetNuke's own default expression ends in [a-zA-Z]{2,4}, and this
        // installation's seeded accounts hold admin@setup.local and member@setup.local, whose final label
        // is five letters. Editing an unrelated field on either account - a surname, a display name - re-ran
        // the admission rule over the address the caller had merely echoed back, and refused the whole
        // update. The address was already stored, no caller was proposing to change it, and there was no
        // submission that could have satisfied the rule short of altering data the caller never asked to
        // touch. That is a maintenance dead end rather than a validation.
        //
        // So the admission rule is asked only of an address that is actually being CHANGED. A submission
        // equal to the stored value is a no-op and is admitted on the strength of already being there;
        // anything else must satisfy the tenant's rule as before. The shape rule still applies to both,
        // because a caller must not be able to write a malformed address by claiming it was already stored.
        //
        // The comparison is case-insensitive because the store treats an address that way: the account
        // read by address is matched case-insensitively, so two spellings that differ only in case name one
        // account and echoing either of them back is echoing the stored value.
        // account.Email is nullable because the column is; request.Email is not, so only the stored side
        // needs a substitute. Coalescing the request side as well would tell the compiler it might be null
        // and cost the admission call below its non-null argument.
        bool emailUnchanged = string.Equals(
            account.Email ?? string.Empty,
            request.Email,
            StringComparison.OrdinalIgnoreCase);

        if (!emailUnchanged)
        {
            Result<bool> validEmail = await IsEmailValidAsync(portalId, request.Email, cancellationToken)
                .ConfigureAwait(false);
            if (validEmail.IsFailure || !validEmail.Value)
            {
                return Result<UserDetailDto>.Failure(
                    CreateInvalidEmailCode,
                    "The electronic-mail address does not satisfy this portal's validation rule.");
            }
        }

        MembershipSettingsDto? settings =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false);

        string? formattedDisplayName = null;
        if (settings is not null && !string.IsNullOrWhiteSpace(settings.SecurityDisplayNameFormat))
        {
            formattedDisplayName = FormatDisplayName(
                settings.SecurityDisplayNameFormat,
                account.UserId,
                request.FirstName,
                request.LastName,
                account.Username);

            if (formattedDisplayName.Length > DisplayNameMaximumLength)
            {
                return Result<UserDetailDto>.Failure(
                    DisplayNameTooLongCode,
                    FormattableString.Invariant(
                        $"The tenant's display-name format produces {formattedDisplayName.Length} characters for account {userId}; the stored limit is {DisplayNameMaximumLength}."));
            }
        }

        // Apply the request only after every state-dependent guard has passed. Besides avoiding a database
        // write, this keeps an expected refusal from leaving a tracked aggregate mutated in a caller that
        // continues using the same unit-of-work scope.
        UserMappings.ApplyUpdate(account, request);
        if (formattedDisplayName is not null)
        {
            account.DisplayName = formattedDisplayName;
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

        // The designation is already in hand here - it was consulted above to decide whether this write
        // invalidated the tenant's cached view - so the capability costs no additional read.
        return Result<UserDetailDto>.Success(
            UserMappings.ToDetail(account, portalId, roles, portal?.AdministratorId));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deletion removes the tenant membership and the tenant's role assignments, and removes the account
    /// row and its credential only once no membership of any tenant remains.
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

        // IUserService calls this the worst of the three cases it covers, and the reasoning is worth
        // keeping in view: a refresh token that outlives the account it names is exchanged for access
        // tokens asserting an identity that no longer exists, and there is no longer any account for an
        // administrator to inspect or disable.
        if (await EndSessionsAsync(userId, cancellationToken).ConfigureAwait(false) is ResultReason sessions)
        {
            return Result.Failure(sessions);
        }

        bool holdsAnotherMembership = account.UserPortals
            .Any(candidate => candidate.PortalId != portalId);

        // ONE SCOPE AROUND THE WHOLE CASCADE. Five writes follow - the account's direct grants, its role
        // assignments, its tenant membership, its credential in the external membership store, and the
        // account row - and they cannot be expressed as a single SaveChanges because the credential lives
        // outside the mapped model and is written through its own statement.
        //
        // THE SCOPE IS WRAPPED SO THAT A LOST RACE ANSWERS AS A REPEAT DELETION DOES. Every write in the
        // cascade is a removal, and the store reports a removal that affected no row as a concurrency
        // conflict - so a conflict here means precisely one thing: another caller removed what this cascade
        // was removing. That is not a conflict a caller can resolve by retrying with fresh state, which is
        // what a 409 would invite; it is the same condition the pre-flight guard above reports when the
        // account is already gone. Answering it with the SAME reason keeps the answer independent of timing:
        // whether a second deletion arrives a moment or a day after the first, it is told the account does
        // not exist.
        try
        {
            await using ITransactionScope transaction = await _unitOfWork
                .BeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
                .ConfigureAwait(false);

            // THE CASCADE IS ORCHESTRATED THROUGH THE PERMISSION CONTRACT, NOT THROUGH THE GRANT
            // REPOSITORY. The two tables are one concern and the rule bounding the removal to DIRECT grants
            // - grants reaching the account through a role belong to the role, so removing them would strip
            // every other holder of that role - is permission knowledge rather than account knowledge.
            Result cascade = await _permissions
                .StageUserPermissionRemovalAsync(portalId, userId, cancellationToken)
                .ConfigureAwait(false);

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

            // profile rows have no PortalID of their own. Their tenant ownership is carried by the required
            // definition foreign key, so portal removal must stage precisely the values whose definitions
            // belong to this portal before the membership is removed.
            await _profiles.DeleteProfileValuesAsync(portalId, userId, cancellationToken).ConfigureAwait(false);

            UserPortal? membership = await _users
                .GetMembershipAsync(portalId, userId, cancellationToken)
                .ConfigureAwait(false);

            if (membership is not null)
            {
                _users.RemoveMembership(membership);
            }

            if (!holdsAnotherMembership)
            {
                MembershipWriteOutcome credential = await _users
                    .DeleteCredentialAsync(userId, cancellationToken)
                    .ConfigureAwait(false);

                // ONLY AN UNREACHABLE STORE ABANDONS THE CASCADE. NoRecord means the store answered and
                // holds no credential for this account, which is the state this step exists to produce - so
                // it is passed through rather than reported as a failure. Reporting it refused two callers
                // racing to delete one account and, worse, told the loser the account had been left intact
                // when the winner had already removed it.
                if (credential == MembershipWriteOutcome.StoreUnavailable)
                {
                    return Result.Failure(
                        CredentialRemovalFailedCode,
                        FormattableString.Invariant($"The credential held by account {userId} could not be removed.")
                        + " The account was left intact rather than deleted without it. Try again.");
                }

                _users.Remove(account);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            // Nothing was committed - the scope was disposed without a commit, so every staged removal is
            // rolled back - and the account this request addressed is gone, removed by whoever won the race.
            // The desired end state holds; only this request's own work did not produce it.
            return Result.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        // Everything below runs only once the batch is durable, so no eviction and no audit record can
        // describe a deletion that did not happen.

        // Reproduces the legacy USER_DELETED audit entry, whose one measured call site is
        // UserController.vb:L240 - AddLog("Username", objUser.Username, _portalSettings, objUser.UserID,
        // EventLogType.USER_DELETED).
        Result purged = holdsAnotherMembership
            ? await _tokens
                .PurgeAccountSessionRecordsAsync(account.UserId, portalId, cancellationToken)
                .ConfigureAwait(false)
            : await _tokens
                .PurgeAccountSessionRecordsAsync(account.UserId, null, cancellationToken)
                .ConfigureAwait(false);

        RecordAudit(
            AuditEventNames.UserDeleted,
            portalId,
            account.UserId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AccountRemoved"] = (!holdsAnotherMembership).ToString(CultureInfo.InvariantCulture),
                ["SessionRecordsErased"] = purged.IsSuccess.ToString(CultureInfo.InvariantCulture),
            });

        _cache.InvalidatePortal(portalId);
        _cache.InvalidateUser(portalId, account.Username);

        // The grant-cache eviction the permission contract owns, deferred to here because this method owned
        // the commit.
        _permissions.InvalidateUserPermissionCaches();

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// SELF-SERVICE ONLY, AND THE CURRENT CREDENTIAL IS ALWAYS VERIFIED. This member no longer carries the
    /// administrative reset, and that separation is a security fix rather than a tidying.
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
    /// MIGRATION: this member owns the fallback half of credential migration. AuthService owns the primary
    /// first-login path through the bounded legacy verifier; an administrative reset remains necessary
    /// after that deadline, for an unsupported representation, or when the owner no longer knows the
    /// credential.
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
    /// The failure reason when the discriminator names a different operation, or <see langword="null"/>
    /// when it names this one or is absent.
    /// </returns>
    /// <remarks>
    /// An ABSENT discriminator is accepted, because the endpoint the caller chose has already stated which
    /// operation they meant and requiring them to say it twice would refuse well-formed requests. A
    /// discriminator naming the OTHER operation is refused rather than ignored: the two differ in whether a
    /// credential is verified, so silently performing the one the caller did not ask for is not an option.
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

    /// <summary>Writes a new credential, shared by the self-service change and the administrative reset.</summary>
    /// <param name="portalId">Identifier of the tenant that owns the account.</param>
    /// <param name="userId">Identifier of the account whose credential is changing.</param>
    /// <param name="request">The submitted credential change.</param>
    /// <param name="verifyCurrent">Whether the current credential must be presented and verified.</param>
    /// <param name="cancellationToken">Token observed while the credential is written.</param>
    /// <returns>A successful result with no value, or the reason the credential was not written.</returns>
    private async Task<Result> WriteCredentialAsync(
        int portalId,
        int userId,
        ChangePasswordRequest request,
        bool verifyCurrent,
        CancellationToken cancellationToken)
    {
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

        (bool exists, string? storedHash, _, _, _, _) = await _users
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
        if (await EndSessionsAsync(userId, cancellationToken).ConfigureAwait(false) is ResultReason sessions)
        {
            return Result.Failure(sessions);
        }

        DateTime now = _clock.UtcNow;

        CredentialWriteOutcome written = await _users
            .SetPasswordHashAsync(
                userId,
                _passwordHasher.Hash(request.NewPassword),
                storedHash,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        if (written == CredentialWriteOutcome.Superseded)
        {
            // NOT retried here, and not reported as a store failure.
            return Result.Failure(
                PasswordSupersededCode,
                "The credential changed while this request was being processed, so it was not replaced. "
                + "Read the current state and submit the change again.");
        }

        if (written != CredentialWriteOutcome.Replaced)
        {
            return Result.Failure(PasswordResetFailedCode, "The credential store refused the change.");
        }

        if (await EndSessionsAsync(userId, cancellationToken).ConfigureAwait(false) is ResultReason lingering)
        {
            return Result.Failure(lingering);
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

        (bool exists, _, _, _, _, bool isLockedOut) = await _users
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
            // ALREADY IN THE REQUESTED END STATE, WHICH IS NOT A FAILURE. This refused with a
            // user-visible 400, which reported work that had in fact been done as an action that had not -
            // and left the operation unsafe to retry when an answer was lost in transit or two
            // administrators cleared the same lockout at once. Nothing is written and nothing is
            // invalidated, because nothing changed.
            return Result.Success();
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

        (bool exists, _, _, _, bool storedApproval, _) = await _users
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
    public async Task<Result<MembershipSettingsDto?>> GetMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        MembershipSettingsDto? settings =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false);

        // Never null: a tenant with no settings source gets the property initialisers, which ARE the
        // measured legacy defaults, and IsStored stays false so the caller knows nothing is persisted.
        return Result<MembershipSettingsDto?>.Success(settings ?? new MembershipSettingsDto());
    }

    /// <inheritdoc />
    public async Task<Result<MembershipSettingsUpdateResultDto>> UpdateMembershipSettingsAsync(
        int portalId,
        UpdateMembershipSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // THE SERVICE'S OWN COPY OF THE FIELD RULES, and the duplication is deliberate for the same reason
        // it is on the module-settings write: the declarative validator answers a caller that came through
        // the API pipeline with a field-level 400, and this member is reachable from callers that did not.
        if (ValidateMembershipSettings(request) is ResultReason invalid)
        {
            return Result<MembershipSettingsUpdateResultDto>.Failure(invalid);
        }

        var checkedPages = new HashSet<int>();
        foreach ((string member, int? tabId) in new[]
        {
            (nameof(UpdateMembershipSettingsRequest.RedirectAfterLogin), request.RedirectAfterLogin),
            (nameof(UpdateMembershipSettingsRequest.RedirectAfterRegistration), request.RedirectAfterRegistration),
            (nameof(UpdateMembershipSettingsRequest.RedirectAfterLogout), request.RedirectAfterLogout),
        })
        {
            if (tabId is not { } wanted || !checkedPages.Add(wanted))
            {
                continue;
            }

            Tab? page = await _tabs.GetByIdAsync(wanted, cancellationToken).ConfigureAwait(false);

            if (page is null || page.PortalId != portalId)
            {
                return Result<MembershipSettingsUpdateResultDto>.Failure(
                    MembershipSettingsRedirectNotInPortalCode,
                    FormattableString.Invariant(
                        $"{member} names page {wanted}, which does not belong to portal {portalId}."));
            }
        }

        Module? source = await FindMembershipSettingsSourceAsync(portalId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return Result<MembershipSettingsUpdateResultDto>.Failure(
                MembershipSettingsSourceMissingCode,
                FormattableString.Invariant(
                    $"Portal {portalId} has no \"{MembershipSettingsDto.UserAccountsModuleDefinitionName}\" module instance, so there is nowhere to store membership settings. Add the \"{MembershipSettingsDto.UserAccountsModuleDefinitionName}\" module to one of this portal's pages and try again."));
        }

        if (string.IsNullOrWhiteSpace(request.SecurityEmailValidation))
        {
            return Result<MembershipSettingsUpdateResultDto>.Failure(
                ProfileDefinitionExpressionInvalidCode,
                "The electronic-mail validation expression must not be empty.");
        }

        if (ValidateStoredExpression(request.SecurityEmailValidation) is ResultReason invalidExpression)
        {
            return Result<MembershipSettingsUpdateResultDto>.Failure(invalidExpression);
        }

        IReadOnlyList<ModuleSetting> stored =
            await _modules.GetModuleSettingsAsync(source.ModuleId, cancellationToken).ConfigureAwait(false);

        // THE STORED FORMAT, READ BEFORE IT IS OVERWRITTEN. The sweep below is driven by a COMPARISON, and
        // the value being compared against exists only until the upsert loop replaces it - which is why the
        // read is taken here rather than after the write.
        string previousFormat = ReadStoredDisplayNameFormat(stored);
        string submittedFormat = request.SecurityDisplayNameFormat ?? string.Empty;
        bool formatChanged = !string.Equals(previousFormat, submittedFormat, StringComparison.Ordinal);

        foreach (KeyValuePair<string, string> setting in ProjectMembershipSettings(request))
        {
            await UpsertModuleSettingAsync(stored, source.ModuleId, setting.Key, setting.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        var outcome = new MembershipSettingsUpdateResultDto
        {
            DisplayNameFormatChanged = formatChanged,
        };

        // The accounts the sweep actually rewrote, named so their per-account cache entries can be dropped
        // once the write has committed. Empty whenever no sweep ran.
        IReadOnlyList<string> rewrittenAccounts = [];

        // MIGRATION: the legacy save committed the policy immediately and started a BACKGROUND THREAD to
        // rewrite every account's display name.
        await using (ITransactionScope transaction = await _unitOfWork
            .JoinOrBeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            if (formatChanged && submittedFormat.Length > 0)
            {
                Result<IReadOnlyList<string>> swept =
                    await RewriteDisplayNamesAsync(portalId, submittedFormat, cancellationToken)
                        .ConfigureAwait(false);

                if (swept.IsFailure)
                {
                    // The policy is abandoned WITH the sweep.
                    return Result<MembershipSettingsUpdateResultDto>.Failure(swept.Error!);
                }

                rewrittenAccounts = swept.Value!;
                outcome.DisplayNamesRewritten = rewrittenAccounts.Count;
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _cache.InvalidatePortal(portalId);
        _cache.InvalidateProfileDefinitions(portalId);

        // Every rewritten account is stale in the PER-ACCOUNT cache too, which is keyed by username rather
        // than by tenant, so the tenant-wide eviction above does not reach it. Each rewritten account is
        // therefore evicted by name.
        foreach (string rewrittenAccount in rewrittenAccounts)
        {
            _cache.InvalidateUser(portalId, rewrittenAccount);
        }

        return Result<MembershipSettingsUpdateResultDto>.Success(outcome);
    }

    /// <summary>Reads the display-name format currently stored against the tenant's settings source.</summary>
    /// <param name="stored">The settings rows already read for the upsert.</param>
    /// <returns>The stored format, or the empty string when the tenant has never stored one.</returns>
    /// <remarks>
    /// Reads the rows already in hand rather than issuing a second query, and answers the empty string for
    /// an absent key - which is the value <c>ReadString</c> defaults this key to, so a tenant that has
    /// never configured a format compares equal to a submission that leaves it blank and is not swept.
    /// </remarks>
    private static string ReadStoredDisplayNameFormat(IReadOnlyList<ModuleSetting> stored)
    {
        ModuleSetting? setting = stored.FirstOrDefault(candidate =>
            string.Equals(candidate.SettingName, "Security_DisplayNameFormat", StringComparison.OrdinalIgnoreCase));

        return setting?.SettingValue ?? string.Empty;
    }

    /// <summary>Recomposes every account's display name in one tenant from a newly adopted format.</summary>
    /// <param name="portalId">The tenant to sweep.</param>
    /// <param name="format">The newly adopted format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The usernames of the accounts whose stored display name changed, or a failure when the format would
    /// overflow the stored column for any one of them.
    /// </returns>
    /// <remarks>
    /// ⚠ ONE WRITE, NOT ONE PER ACCOUNT. The legacy issued a separate update per account, so a sweep over a
    /// large tenant was N round trips and a failure at row K left K-1 accounts formatted. Here the tracked
    /// aggregates are mutated and the caller's single <c>SaveChanges</c> writes them together inside the
    /// caller's transaction.
    /// </remarks>
    private async Task<Result<IReadOnlyList<string>>> RewriteDisplayNamesAsync(
        int portalId,
        string format,
        CancellationToken cancellationToken)
    {
        // The unpaged read, which the repository answers when the page size is zero. The sweep is
        // tenant-wide by definition, so paging it would only decide how many round trips it took.
        PagedResult<User> everyAccount = await _users.ListAsync(
            portalId,
            pageIndex: 0,
            pageSize: UnpagedPageSize,
            query: null,
            userNamePrefix: null,
            emailPrefix: null,
            profilePropertyDefinitionId: null,
            profilePropertyValuePrefix: null,
            isApproved: null,
            includeUnauthorised: true,
            includeSuperUsers: false,
            sortBy: null,
            descending: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var rewritten = new List<string>();

        foreach (User account in everyAccount.Items)
        {
            string formatted = FormatDisplayName(
                format,
                account.UserId,
                account.FirstName,
                account.LastName,
                account.Username);

            if (formatted.Length > DisplayNameMaximumLength)
            {
                return Result<IReadOnlyList<string>>.Failure(
                    DisplayNameTooLongCode,
                    FormattableString.Invariant(
                        $"The submitted display-name format produces {formatted.Length} characters for account {account.UserId}; the stored limit is {DisplayNameMaximumLength}."));
            }

            // Recorded only when the value actually changes. A format whose tokens resolve to what is
            // already stored is not a rewrite, and counting it would report a tenant-wide change to an
            // operator who caused none.
            if (!string.Equals(account.DisplayName, formatted, StringComparison.Ordinal))
            {
                account.DisplayName = formatted;
                rewritten.Add(account.Username);
            }
        }

        return Result<IReadOnlyList<string>>.Success(rewritten);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsEmailValidAsync(
        int portalId,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Result<bool>.Success(false);
        }

        MembershipSettingsDto settings =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false)
            ?? new MembershipSettingsDto();

        if (string.IsNullOrWhiteSpace(settings.SecurityEmailValidation))
        {
            return Result<bool>.Success(false);
        }

        Result<Regex> expression = GetValidationExpression(settings.SecurityEmailValidation);
        if (expression.IsFailure)
        {
            return Result<bool>.Failure(expression.Error!);
        }

        try
        {
            return Result<bool>.Success(expression.Value.IsMatch(email));
        }
        catch (RegexMatchTimeoutException)
        {
            return Result<bool>.Failure(
                ProfileDefinitionExpressionInvalidCode,
                "The portal's electronic-mail validation rule could not be applied safely.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The completeness test walks the tenant's definitions and stops at the first required property whose
    /// answer is missing or empty, exactly as <c>ProfileController.ValidateProfile</c> did with its <c>Exit
    /// For</c>. An account with no stored answers at all and at least one required definition is therefore
    /// incomplete, which is the case the legacy hit for a newly created account.
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
        bool required = settings is null
            ? MembershipSettingsDto.DefaultRequireValidProfileAtLogin
            : settings.SecurityRequireValidProfile || settings.SecurityRequireValidProfileAtLogin;

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
            await _profiles.GetProfileValuesAsync(portalId, userId, cancellationToken).ConfigureAwait(false);

        var answers = new Dictionary<int, string?>(stored.Count);
        foreach (UserProfileValue value in stored)
        {
            answers[value.PropertyDefinitionId] = value.PropertyValue;
        }

        foreach (ProfilePropertyDefinition definition in mandatory)
        {
            if (!answers.TryGetValue(definition.PropertyDefinitionId, out string? answer)
                || string.IsNullOrEmpty(answer))
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
            await _profiles.GetProfileValuesAsync(portalId, userId, cancellationToken).ConfigureAwait(false);

        // ONE READ FOR BOTH TENANT FACTS, deliberately.
        MembershipSettingsDto? settings =
            await ReadMembershipSettingsAsync(portalId, cancellationToken).ConfigureAwait(false);

        // The same fallback the settings reader itself applies, so a tenant that has stored nothing
        // behaves identically whichever way the values are reached.
        var fallback = new MembershipSettingsDto();
        int defaultVisibility = settings?.ProfileDefaultVisibility ?? fallback.ProfileDefaultVisibility;
        bool displayVisibilityEnabled =
            settings?.ProfileDisplayVisibility ?? fallback.ProfileDisplayVisibility;

        return Result<UserProfileDto?>.Success(
            UserMappings.ToProfile(
                userId,
                definitions,
                values,
                defaultVisibility,
                displayVisibilityEnabled));
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE ABSENCE TEST IS THE ACCOUNT READ, once. Every member below is scoped to the same tenant and the
    /// same account, so if the account is not a member of the tenant there is nothing to describe and the
    /// document is null - the same convention <see cref="GetUserAsync"/> uses.
    /// </remarks>
    public async Task<Result<UserPersonalDataExportDto?>> ExportPersonalDataAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        Result<UserDetailDto?> account = await GetUserAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (account.IsFailure)
        {
            return Result<UserPersonalDataExportDto?>.Failure(account.Reason!);
        }

        if (account.Value is null)
        {
            // The contract declares absence as a null value on a non-nullable type parameter, matching the
            // account read this decision is taken from.
            return Result<UserPersonalDataExportDto?>.Success(null);
        }

        Result<UserProfileDto?> profile = await GetProfileAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (profile.IsFailure)
        {
            return Result<UserPersonalDataExportDto?>.Failure(profile.Reason!);
        }

        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        List<RoleMembershipDto> memberships = new(assignments.Count);

        foreach (UserRole assignment in assignments)
        {
            // Tenant-scoped, so a role identifier that belongs to another tenant resolves to nothing rather
            // than naming a role from a portal this document is not about.
            Role? role = await _roles
                .GetByIdAsync(assignment.RoleId, portalId, cancellationToken)
                .ConfigureAwait(false);

            memberships.Add(new RoleMembershipDto
            {
                UserRoleId = assignment.UserRoleId,
                UserId = assignment.UserId,

                // The subject's own identifying fields, taken from the account projection above rather than
                // re-read: this document describes one account, so these are the same two values on every
                // row and they must not be able to disagree with the account record beside them.
                Username = account.Value.Username,
                DisplayName = account.Value.DisplayName,
                RoleId = assignment.RoleId,

                RoleName = role?.RoleName ?? string.Empty,
                EffectiveDate = assignment.EffectiveDate,
                ExpiryDate = assignment.ExpiryDate,
            });
        }

        UserPersonalDataExportDto export = new()
        {
            GeneratedAtUtc = _clock.UtcNow,
            PortalId = portalId,
            UserId = userId,
            Account = account.Value,
            Profile = profile.Value,
            RoleAssignments = memberships,
        };

        RecordAudit(
            AuditEventNames.UserDataExported,
            portalId,
            userId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProfileValues"] = (export.Profile?.Properties.Count ?? 0)
                    .ToString(CultureInfo.InvariantCulture),
                ["RoleAssignments"] = memberships.Count.ToString(CultureInfo.InvariantCulture),
                ["SelfService"] = (_currentUser.UserId == userId).ToString(CultureInfo.InvariantCulture),
            });

        return Result<UserPersonalDataExportDto?>.Success(export);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The three validation rules are the three a profile property definition actually carries - required,
    /// declared length and a validation expression - and they are enforced here because they depend on
    /// tenant data a static request validator cannot see.
    /// </remarks>
    public async Task<Result> UpdateProfileAsync(
        int portalId,
        int userId,
        UserProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Properties is null)
        {
            return Result.Failure(ProfileUnknownPropertyCode, "The profile properties collection is required.");
        }

        if (profile.Properties.Count > ProfilePropertySubmissionMaximum)
        {
            return Result.Failure(
                ProfileTooManyPropertiesCode,
                FormattableString.Invariant(
                    $"A profile may contain no more than {ProfilePropertySubmissionMaximum} submitted properties."));
        }

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

        // Kept as two accumulators rather than one, because the summary code published for the whole refusal
        // has to be the code of the FIRST submitted value that failed — a required property the caller never
        // sent has no position among the values it did send, so it cannot be allowed to claim first place.
        ProfileFieldFailures valueFailures = default;
        ProfileFieldFailures requiredFailures = default;

        foreach (UserProfileValueDto property in profile.Properties)
        {
            if (property is null)
            {
                return Result.Failure(ProfileUnknownPropertyCode, "A profile property entry must not be null.");
            }

            if (property.PropertyValue is null)
            {
                return Result.Failure(
                    ProfilePropertyValidationFailedCode,
                    FormattableString.Invariant(
                        $"Profile property {property.PropertyDefinitionId} must carry a value; use an empty string to clear it."));
            }

            if (property.Visibility is < 0 or > 2)
            {
                return Result.Failure(
                    ProfileVisibilityInvalidCode,
                    FormattableString.Invariant(
                        $"Profile property {property.PropertyDefinitionId} has an unsupported visibility."));
            }

            if (submitted.ContainsKey(property.PropertyDefinitionId))
            {
                return Result.Failure(
                    ProfileDuplicatePropertyCode,
                    FormattableString.Invariant(
                        $"Profile property {property.PropertyDefinitionId} was submitted more than once."));
            }

            if (!byDefinitionId.TryGetValue(property.PropertyDefinitionId, out ProfilePropertyDefinition? definition))
            {
                return Result.Failure(
                    ProfileUnknownPropertyCode,
                    FormattableString.Invariant(
                        $"Portal {portalId} does not define profile property {property.PropertyDefinitionId}."));
            }

            // ⚠ A VALUE FAILURE IS COLLECTED, NOT RETURNED, and the distinction from the refusals above is
            // deliberate. Everything before this point is a malformed REQUEST — a null entry, a property this
            // tenant does not define, the same property twice, a visibility outside the enumeration — which no
            // operator can correct by retyping a box, so failing on the first is right. A value that is too
            // long or does not match its declared format IS correctable per field, and an operator handed one
            // refusal at a time has to submit once per mistake to discover them all.
            if (ValidateProfileValue(definition, property.PropertyValue) is ResultReason invalid)
            {
                AddFieldFailure(ref valueFailures, definition.PropertyName, invalid);
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
                || string.IsNullOrEmpty(property.PropertyValue))
            {
                AddFieldFailure(
                    ref requiredFailures,
                    definition.PropertyName,
                    new ResultReason(
                        ProfileRequiredPropertyMissingCode,
                        FormattableString.Invariant(
                            $"Profile property \"{definition.PropertyName}\" is required.")));
            }
        }

        // ORDER IS THE SUBMITTED ORDER FIRST, THEN THE OMITTED-BUT-REQUIRED ONES, so that the summary code and
        // sentence a client reads on its own are the ones it would have received before this became a
        // multi-field refusal. That keeps the flat half of the document byte-compatible with what every
        // existing caller already handles, while the `errors` member is purely additive.
        if (CombineFieldFailures(valueFailures, requiredFailures) is { } refusal)
        {
            return Result.Failure(refusal);
        }

        IReadOnlyList<UserProfileValue> stored =
            await _profiles.GetProfileValuesAsync(portalId, userId, cancellationToken).ConfigureAwait(false);

        DateTime now = _clock.UtcNow;
        var retained = new HashSet<int>();

        foreach (UserProfileValue value in stored)
        {
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
    public async Task<Result<IReadOnlyList<ProfilePropertyDefinitionDto>>> ListProfilePropertyDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = string.Format(CultureInfo.InvariantCulture, ProfileDefinitionsCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(
            ProfileDefinitionsCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        // MIGRATION: the legacy caller skipped the database read entirely when the configured expiry
        // resolved to zero. That is not reproduced: disabling caching disables caching only, and the read
        // still runs, because a configuration value must not silently change what is reported.
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
            .GetDefinitionByIdAsync(portalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // The repository has already applied the same portal predicate used by the collection read: the
        // scope reaching it is matched exactly, so -1 addresses the tenant IDENTITY(-1, 1) numbered -1
        // rather than the host-level rows, which the terminal schema stores with a SQL NULL portal.
        if (definition is null || definition.IsDeleted)
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

        if (ValidateStoredExpression(request.ValidationExpression) is ResultReason invalidExpression)
        {
            return Result<ProfilePropertyDefinitionDto>.Failure(invalidExpression);
        }

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

        // THE NAME TEST ABOVE CANNOT CLOSE THE RACE, SO THE FLUSH ANSWERS FOR IT.
        // IX_ProfilePropertyDefinition is unique over (PortalID, ModuleDefID, PropertyName), so two
        // requests declaring the same property name in the same tenant arriving together both read "not
        // declared" before either inserts, and the loser's insert is refused by the index rather than by
        // the read.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            return Result<ProfilePropertyDefinitionDto>.Failure(
                ProfileDefinitionDuplicateNameCode,
                FormattableString.Invariant(
                    $"Portal {portalId} already declares a profile property named \"{request.PropertyName}\"."));
        }

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
            .GetDefinitionByIdAsync(portalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null || stored.IsDeleted)
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

        if (ValidateStoredExpression(request.ValidationExpression) is ResultReason invalidExpression)
        {
            return Result<ProfilePropertyDefinitionDto>.Failure(invalidExpression);
        }

        // MIGRATION: THIS READ DELIBERATELY STILL SEES WITHDRAWN DECLARATIONS, and it must not be "made
        // consistent" with the absence rule applied to the guard above.
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

        // MIGRATION: THE NAME COMPARISON ABOVE CANNOT CLOSE THE RACE EITHER. A rename onto a name that is
        // free when read and taken by the time the update is written is refused by
        // IX_ProfilePropertyDefinition, and the reasoning already recorded above for withdrawn rows applies
        // unchanged to a concurrent one: the honest answer is the duplicate-name result the comparison
        // would have produced, not a server fault.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            return Result<ProfilePropertyDefinitionDto>.Failure(
                ProfileDefinitionDuplicateNameCode,
                FormattableString.Invariant(
                    $"Portal {portalId} already declares a profile property named \"{request.PropertyName}\"."));
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
    /// <para>
    /// ⚠ THE COMMIT IS DELIBERATELY OUTSIDE THE LOOP, and moving it inside would reintroduce exactly the
    /// defect this member exists to close. Every named declaration is resolved from ONE read, every
    /// position is staged, and the whole set is written by a single
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> — so an exchange of two positions either lands complete
    /// or does not land at all.
    /// </para>
    /// <para>
    /// The read excludes withdrawn declarations, which is what makes a single absence test sufficient: an
    /// identifier the request names and the read did not return is unknown, belongs to another tenant, or
    /// has been withdrawn, and all three are the same answer to the caller. The absence is reported BEFORE
    /// anything is staged.
    /// </para>
    /// <para>
    /// Positions are stored exactly as submitted. Nothing is renumbered or compacted, because the legacy
    /// grid exchanged two stored values and persisted them unchanged, leaving the sequence sparse — and a
    /// tidy-up here would silently move declarations the caller never named.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<ProfilePropertyDefinitionDto>>>
        ReorderProfilePropertyDefinitionsAsync(
            int portalId,
            ReorderProfilePropertyDefinitionsRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<ProfilePropertyDefinitionPosition> positions = request.Positions;

        if (positions.Count == 0)
        {
            throw new DomainException("At least one profile property position is required.");
        }

        // ONE read for the whole request. Resolving each identifier separately would make the cost
        // proportional to the number of rows moved, and a Move Up on a long catalogue moves two rows out of
        // however many the tenant declares.
        IReadOnlyList<ProfilePropertyDefinition> declared = await _profiles
            .GetDefinitionsByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, ProfilePropertyDefinition> byId = declared
            .ToDictionary(definition => definition.PropertyDefinitionId);

        // ⚠ TWO PASSES, AND THE FIRST ONE MUTATES NOTHING. Every identifier is resolved before any position
        // is assigned, because the resolved declarations are TRACKED entities: assigning as the loop went
        // would leave an earlier declaration carrying a new position in memory after a later identifier
        // failed to resolve, and the next commit anywhere in the request would then persist a change this
        // member had just declined to make. Measured against a request whose second position was unknown.
        List<(ProfilePropertyDefinition Definition, int ViewOrder)> resolved = new(positions.Count);

        foreach (ProfilePropertyDefinitionPosition position in positions)
        {
            if (!byId.TryGetValue(position.PropertyDefinitionId, out ProfilePropertyDefinition? definition))
            {
                return Result<IReadOnlyList<ProfilePropertyDefinitionDto>>.Failure(
                    ProfileDefinitionNotFoundCode,
                    FormattableString.Invariant(
                        $"Profile property definition {position.PropertyDefinitionId} does not exist in portal {portalId}."));
            }

            resolved.Add((definition, position.ViewOrder));
        }

        // The second pass, reached only when the whole set resolved.
        foreach ((ProfilePropertyDefinition definition, int viewOrder) in resolved)
        {
            definition.ViewOrder = viewOrder;
            await _profiles.UpdateDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
        }

        // The single commit. A concurrency conflict is reported as one, exactly as the per-declaration
        // update beside this member reports it, because the honest answer to "someone else moved this
        // while you were moving it" is to reload rather than to guess whose order wins.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            return Result<IReadOnlyList<ProfilePropertyDefinitionDto>>.Failure(
                PersistenceConflictCode,
                "The definitions were changed by another request; reload them and try again.");
        }

        _cache.InvalidateProfileDefinitions(portalId);

        // The WHOLE catalogue in its new order, read back through the same projection the listing uses, so
        // a caller rebinds from this response instead of following it with a read that could observe
        // another writer's work and appear to have lost the move.
        IReadOnlyList<ProfilePropertyDefinitionDto> reordered =
            await ReadProfileDefinitionsAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<ProfilePropertyDefinitionDto>>.Success(reordered);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The per-account values recorded against the definition are discarded in the same unit of work, so no
    /// value is left referencing a definition that no longer exists.
    /// </remarks>
    public async Task<Result> DeleteProfilePropertyDefinitionAsync(
        int portalId,
        int propertyDefinitionId,
        bool confirmValueDeletion = false,
        CancellationToken cancellationToken = default)
    {
        ProfilePropertyDefinition? definition = await _profiles
            .GetDefinitionByIdAsync(portalId, propertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: A WITHDRAWN DECLARATION IS ABSENT TO THIS MEMBER TOO, matching the single read, the
        // portal listing and the update beside it.
        if (definition is null || definition.IsDeleted)
        {
            return Result.Failure(
                ProfileDefinitionNotFoundCode,
                FormattableString.Invariant(
                    $"Profile property definition {propertyDefinitionId} does not exist in portal {portalId}."));
        }

        // THE FOUR RESERVED DECLARATIONS ARE REFUSED OUTRIGHT, restoring the protection the legacy screen
        // provided by hiding the command. See ProtectedProfilePropertyNames for why enforcing it here is
        // parity of behaviour rather than a departure from it.
        if (definition.PropertyName is { Length: > 0 } propertyName
            && ProtectedProfilePropertyNames.Contains(propertyName))
        {
            return Result.Failure(
                ProfileDefinitionProtectedCode,
                FormattableString.Invariant(
                    $"\"{definition.PropertyName}\" is one of the profile properties this platform reserves, so it cannot be withdrawn. Clear it from the accounts that should not carry it, or mark it optional, instead."));
        }

        // ⚠ THE CASCADE IS THE HAZARD, AND CONSENT TO IT IS EXPLICIT. Withdrawing a declaration takes every
        // answer recorded against it, because the store's foreign key cascades and DeleteDefinitionAsync
        // loads those rows to be removed with it. That is irreversible and invisible from the request, so a
        // caller who has not said how many answers they expect to destroy is told the number and asked again
        // rather than being taken at their word.
        //
        // A declaration nobody has answered needs no acknowledgement: there is nothing to lose, so requiring
        // a second request would be ceremony rather than safety.
        int recordedAnswers = await _profiles
            .CountProfileValuesForDefinitionAsync(definition.PropertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (recordedAnswers > 0 && !confirmValueDeletion)
        {
            // THE REFUSAL IS THE IMPACT REPORT. It states what will be destroyed, that the loss is permanent,
            // where to take a backup, and the one parameter that performs it - so a caller holding nothing but
            // this response knows both the cost and the next step.
            //
            // ⚠ THE OVERRIDE IS A FLAG AND NOT THE COUNT, WHICH IS A DELIBERATE CHOICE. Requiring the caller to
            // echo the exact number back would guard against the total changing between the two requests, but it
            // would also force every client to recover that number from prose - the count is a fact about the
            // store, not a field of the request, so there is no structured channel it belongs in. The consent
            // being sought is "remove this declaration and whatever answers it holds", which is well defined
            // however many there turn out to be, and a flag expresses exactly that without inviting a fragile
            // contract.
            return Result.Failure(
                ProfileDefinitionValueDeletionUnacknowledgedCode,
                FormattableString.Invariant(
                    $"Withdrawing \"{definition.PropertyName}\" will permanently delete {recordedAnswers} recorded profile answer(s) held by this portal's accounts, and that cannot be undone. Back up the UserProfile table first, then repeat this request with confirmValueDeletion=true to proceed."));
        }

        // The answers recorded against the declaration are NOT removed one at a time here.
        await _profiles
            .DeleteDefinitionAsync(definition.PropertyDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // The commit is guarded exactly as the update path beside it is, and for the same race.
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

    /// <summary>Rejects a paging request whose bounds no validator can have accepted.</summary>
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

    /// <summary>Rejects a filter that was supplied but is blank.</summary>
    /// <param name="filter">The submitted filter.</param>
    /// <param name="name">The argument name, used in the reported message.</param>
    /// <exception cref="DomainException">Thrown when the filter is present but blank.</exception>
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
    /// <see langword="true"/> for an administrative reset, which presents no current credential; <see
    /// langword="false"/> for a self-service change, which does.
    /// </param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns>
    /// A successful outcome when the operation is permitted, and a failure naming why when it is not.
    /// </returns>
    /// <remarks>
    /// THIS IS A SECOND CHECK, NOT THE ONLY ONE, AND BOTH ARE NECESSARY. The route policy establishes that
    /// the caller may address this account at all - it admits the account itself or an administrator of its
    /// tenant - and it does so without reading a request body, which is what a policy can see.
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
    /// Reports whether one account holds administrative authority over one tenant, judged from stored
    /// state.
    /// </summary>
    /// <param name="portalId">The tenant in question.</param>
    /// <param name="callerUserId">The account whose authority is being established.</param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns><see langword="true"/> when the account is a host account or administers that tenant.</returns>
    /// <remarks>
    /// A host account is installation-wide and is accepted without any tenant membership, which is measured
    /// rather than assumed: a host account is created by the installer and need hold membership of no
    /// portal, so a portal-scoped read would not find it. The account is therefore read without a tenant
    /// scope and its own super-user column decides.
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
    /// <b>Call this BEFORE the state change, not after it.</b> The ordering is the whole reason this is a
    /// helper rather than two lines repeated three times.
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

        // AN UNCONFIRMED RETIREMENT IS NOT AN OBSTACLE; AN UNREACHABLE STORE IS. The token service now
        // reports success only for a PROVEN retirement, so an account that never signed in on this instance
        // - the common case - answers "no such family".
        if (!revoked.Reason!.Code.Contains("store_unavailable", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Refuses a membership transition an administrator aimed at their own account.</summary>
    /// <param name="userId">The account the transition addresses.</param>
    /// <returns>The reason the transition is refused, or <see langword="null"/> when it is permitted.</returns>
    private ResultReason? EnsureNotActingOnSelf(int userId)
        => _currentUser.UserId is int actor && actor == userId
            ? new ResultReason(
                MembershipSelfForbiddenCode,
                "A membership transition cannot be applied to the acting administrator's own account.")
            : null;

    /// <summary>Tests whether an exception, or any exception it wraps, reports a concurrency conflict.</summary>
    /// <param name="exception">The exception raised by the commit.</param>
    /// <returns><see langword="true"/> when the commit lost a race.</returns>
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
    /// Applies the bound credential policy to a submitted credential, returning the reason it was refused
    /// or <see langword="null"/> when it satisfies the policy.
    /// </summary>
    /// <param name="credential">The submitted credential.</param>
    /// <param name="code">The failure code to report the refusal under, so the caller's context is kept.</param>
    /// <returns>The refusal reason, or <see langword="null"/> when the credential is acceptable.</returns>
    /// <remarks>
    /// No message produced here contains any part of the credential.
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

    /// <summary>Applies the tenant's display-name format to an account.</summary>
    /// <param name="format">The configured format, carrying the legacy tokens.</param>
    /// <param name="userId">The account identifier substituted for <c>[USERID]</c>.</param>
    /// <param name="firstName">The submitted given name substituted for <c>[FIRSTNAME]</c>.</param>
    /// <param name="lastName">The submitted family name substituted for <c>[LASTNAME]</c>.</param>
    /// <param name="username">The immutable account name substituted for <c>[USERNAME]</c>.</param>
    /// <returns>The formatted display name.</returns>
    private static string FormatDisplayName(
        string format,
        int userId,
        string? firstName,
        string? lastName,
        string? username)
        => format
            .Replace("[USERID]", userId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("[FIRSTNAME]", firstName ?? string.Empty, StringComparison.Ordinal)
            .Replace("[LASTNAME]", lastName ?? string.Empty, StringComparison.Ordinal)
            .Replace("[USERNAME]", username ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Withholds the two PROFILE VALUES the tenant's settings hide, and leaves every account column as
    /// stored.
    /// </summary>
    /// <param name="row">The row about to be published.</param>
    /// <param name="settings">The tenant's membership settings, holding the nine column flags.</param>
    /// <remarks>
    /// <para>
    /// ⚠ A HIDDEN COLUMN IS NOT RENDERED; IT IS NOT EMPTIED. This member used to overwrite the given name,
    /// family name, display name, address, creation instant, last sign-in instant and approval flag with
    /// each contract's absent value whenever the matching <c>Column_*</c> setting was off. Six of the nine
    /// settings default to off, so a tenant with no accounts-module settings configured received an empty
    /// name and an empty address for every account - which is what a QA pass measured and reported.
    /// </para>
    /// <para>
    /// <b>The legacy application did not do this.</b> <c>UserModuleBase.vb:L98-L115</c> reads the same
    /// settings and the grid honoured them by DECLINING TO RENDER A COLUMN. It never altered the value
    /// behind the column, because which columns to render is a presentation decision.
    /// </para>
    /// <para>
    /// <b>Why overwriting was strictly worse than either alternative.</b> An empty name is
    /// indistinguishable from an account that holds no name, and an absent instant from an account that has
    /// never signed in, so a caller could not tell a minimised row from an incomplete one. It concealed
    /// nothing either: the same settings are published verbatim by <c>GET /api/v1/users/settings</c> and
    /// every value is published unminimised by the single-account read, so any caller could learn which
    /// columns were hidden and could read the values regardless. Reporting an APPROVED account as
    /// unapproved was the sharpest case - a presentation setting was changing a membership fact, and a
    /// caller acting on it would have been acting on a falsehood.
    /// </para>
    /// <para>
    /// <b>What deliberately keeps its gate.</b> The postal address and the telephone number, because they
    /// are different in kind: they are profile VALUES costing one additional read per row, and that read is
    /// already skipped when the tenant hides them. A <see langword="null"/> there therefore reports "not
    /// requested" rather than "overwritten", and the address composer already returns <see
    /// langword="null"/> for an empty property set. The two assignments below are belt and braces over that
    /// fetch gate: a future change to the gate must not be able to leak a value the tenant hides.
    /// </para>
    /// </remarks>
    private static void WithholdProfileValuesTheTenantHides(
        UserListItemDto row,
        MembershipSettingsDto settings)
    {
        if (!settings.ColumnAddress)
        {
            row.Address = null;
        }

        if (!settings.ColumnTelephone)
        {
            row.Telephone = null;
        }
    }

    /// <summary>Concatenates the address parts an account holds, in the legacy order.</summary>
    /// <param name="values">Every profile value the account holds.</param>
    /// <param name="addressPropertyIds">The address property definitions, in composition order.</param>
    /// <returns>The composed address, or <see langword="null"/> when the account holds no part of one.</returns>
    /// <remarks>
    /// The legacy grid reached the excluded <c>FormatAddress</c> helper, which appended each non-blank part
    /// after a comma and a space and then trimmed the leading separator. Joining the non-blank parts with
    /// the same separator is output-identical.
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

    /// <summary>Reads the value a profile row holds, from whichever of its two storage columns holds it.</summary>
    /// <param name="row">The stored row, or <see langword="null"/> when the account has no answer.</param>
    /// <returns>
    /// The stored value, or <see langword="null"/> when <paramref name="row"/> is <see langword="null"/> or
    /// holds nothing in either column.
    /// </returns>
    /// <remarks>
    /// <c>UserProfileValue</c> exposes <c>PropertyValue</c> and <c>PropertyText</c> raw and derives
    /// nothing, so the coalesce lives here.
    /// </remarks>
    private static string? StoredValue(UserProfileValue? row) => row?.PropertyValue ?? row?.PropertyText;

    /// <summary>Locates the module instance a tenant's membership settings are stored against.</summary>
    /// <param name="portalId">The tenant whose settings source is wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The module instance, or <see langword="null"/> when the tenant has none.</returns>
    /// <remarks>
    /// <c>User Accounts</c> is an administrative package and is deliberately absent from the
    /// portal-placeable definition catalogue. The repository exposes a separately named privileged lookup
    /// so this security-settings path cannot accidentally widen the catalogue used by module creation.
    /// </remarks>
    private async Task<Module?> FindMembershipSettingsSourceAsync(int portalId, CancellationToken cancellationToken)
    {
        ModuleDefinition? accounts = await _definitions
            .GetAdministrativeDefinitionByFriendlyNameAsync(
                portalId,
                MembershipSettingsDto.UserAccountsModuleDefinitionName,
                cancellationToken)
            .ConfigureAwait(false);

        if (accounts is null)
        {
            return null;
        }

        // The legacy module block has no paging member, so the tenant's modules are read whole and the
        // recycle bin is excluded here. This call site never wanted a page - it asked for every live
        // instance so it could locate one by its definition.
        IReadOnlyList<Module> instances =
            await _modules.GetByPortalIdAsync(portalId, cancellationToken).ConfigureAwait(false);

        return instances.FirstOrDefault(candidate =>
            !candidate.IsDeleted
            && candidate.ModuleDefinitionId == accounts.ModuleDefinitionId);
    }

    /// <summary>Reads a tenant's membership settings, applying the legacy defaults for every absent key.</summary>
    /// <param name="portalId">The tenant whose settings are read.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The settings, or <see langword="null"/> when the tenant has no settings source.</returns>
    private async Task<MembershipSettingsDto?> ReadMembershipSettingsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
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

        var settings = new MembershipSettingsDto
        {
            // A source was found and read, so every value below either came from a stored row or from the
            // legacy default for an absent key. Either way this tenant HAS a settings store, which is the
            // distinction the marker records - see MembershipSettingsDto.IsStored.
            IsStored = true,
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
            // The legacy default depended on the tenant's size - a free-text picker above a thousand
            // accounts, a drop-down below it. The rule is reproduced; the two numeric values are the
            // contract's own, and the client interprets them.
            int accounts = await _portals.CountUsersAsync(portalId, cancellationToken).ConfigureAwait(false);
            settings.SecurityUsersControl = accounts > LargeTenantAccountThreshold ? 1 : 0;
        }

        return settings;
    }

    /// <summary>Applies this service's own copy of the membership-settings field rules.</summary>
    /// <param name="request">The submitted settings.</param>
    /// <returns>The reason the settings are refused, or <see langword="null"/> when they are acceptable.</returns>
    /// <remarks>
    /// Deliberately duplicates <c>UpdateMembershipSettingsRequestValidator</c> rather than trusting it. The
    /// validator is the authority for the message a caller sees, because it can name the offending member
    /// as a field; this guard is what makes the rules hold for a caller that reached the service without
    /// the pipeline in front of it.
    /// </remarks>
    private static ResultReason? ValidateMembershipSettings(UpdateMembershipSettingsRequest request)
    {
        if (request.DisplayMode is < 0 or > UpdateMembershipSettingsRequestValidator.MaximumDisplayMode)
        {
            return new ResultReason(
                MembershipSettingsInvalidCode,
                UpdateMembershipSettingsRequestValidator.DisplayModeOutOfRangeMessage);
        }

        if (request.ProfileDefaultVisibility
            is < 0 or > UpdateMembershipSettingsRequestValidator.MaximumProfileVisibility)
        {
            return new ResultReason(
                MembershipSettingsInvalidCode,
                UpdateMembershipSettingsRequestValidator.ProfileVisibilityOutOfRangeMessage);
        }

        if (request.SecurityUsersControl
            is < 0 or > UpdateMembershipSettingsRequestValidator.MaximumUsersControl)
        {
            return new ResultReason(
                MembershipSettingsInvalidCode,
                UpdateMembershipSettingsRequestValidator.UsersControlOutOfRangeMessage);
        }

        if (request.RecordsPerPage < UpdateMembershipSettingsRequestValidator.MinimumRecordsPerPage
            || request.RecordsPerPage > UpdateMembershipSettingsRequestValidator.MaximumRecordsPerPage)
        {
            return new ResultReason(
                MembershipSettingsInvalidCode,
                UpdateMembershipSettingsRequestValidator.RecordsPerPageOutOfRangeMessage);
        }

        if ((request.SecurityDisplayNameFormat?.Length ?? 0)
                > UpdateMembershipSettingsRequestValidator.MaximumSettingValueLength
            || (request.SecurityEmailValidation?.Length ?? 0)
                > UpdateMembershipSettingsRequestValidator.MaximumSettingValueLength)
        {
            return new ResultReason(
                MembershipSettingsInvalidCode,
                UpdateMembershipSettingsRequestValidator.SettingValueTooLongMessage);
        }

        if (!IsUsableExpression(request.SecurityEmailValidation))
        {
            return new ResultReason(
                MembershipSettingsInvalidCode,
                UpdateMembershipSettingsRequestValidator.EmailExpressionUnusableMessage);
        }

        return null;
    }

    /// <summary>Determines whether a submitted pattern can be compiled and applied as a regular expression.</summary>
    /// <param name="pattern">The submitted pattern.</param>
    /// <returns><see langword="true"/> when the pattern is usable.</returns>
    private static bool IsUsableExpression(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            var expression = new Regex(
                pattern,
                RegexOptions.None,
                UpdateMembershipSettingsRequestValidator.ExpressionCompilationTimeout);

            _ = expression.IsMatch("probe.address@example.com");

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Projects membership settings back into the setting names the legacy screen wrote.</summary>
    /// <param name="settings">The submitted settings.</param>
    /// <returns>The name and value of every setting to store.</returns>
    private static IReadOnlyDictionary<string, string> ProjectMembershipSettings(
        UpdateMembershipSettingsRequest settings)
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

    /// <summary>Reads the tenant's default profile-value visibility.</summary>
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

    /// <summary>Projects a tenant's profile property definitions in display order.</summary>
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
    /// The per-field refusals gathered while checking one profile submission, together with the code and
    /// sentence of the first of them.
    /// </summary>
    /// <remarks>
    /// A struct with a lazily created dictionary, so the overwhelmingly common outcome — a submission with
    /// nothing wrong with it — allocates nothing at all.
    /// </remarks>
    private struct ProfileFieldFailures
    {
        /// <summary>Gets or sets the messages gathered per property name, or <see langword="null"/> when none.</summary>
        public Dictionary<string, List<string>>? Messages { get; set; }

        /// <summary>Gets or sets the reason of the first failure gathered, which supplies the summary.</summary>
        public ResultReason? First { get; set; }
    }

    /// <summary>Records one property's refusal, keyed by the property name the caller submitted it under.</summary>
    /// <param name="failures">The accumulator to add to.</param>
    /// <param name="propertyName">The declared name of the property at fault.</param>
    /// <param name="reason">The refusal for that property.</param>
    /// <remarks>
    /// The key is the DECLARED PROPERTY NAME rather than the definition identifier, because the client has to
    /// attach the message to a control and it identifies its controls by name. A numeric identifier would put
    /// the burden of the mapping on every consumer.
    /// </remarks>
    private static void AddFieldFailure(
        ref ProfileFieldFailures failures,
        string propertyName,
        ResultReason reason)
    {
        failures.Messages ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        failures.First ??= reason;

        if (!failures.Messages.TryGetValue(propertyName, out List<string>? messages))
        {
            messages = new List<string>(1);
            failures.Messages[propertyName] = messages;
        }

        messages.Add(reason.Message);
    }

    /// <summary>
    /// Folds the gathered refusals into the single failure reason the caller receives, or reports that there
    /// were none.
    /// </summary>
    /// <param name="values">Refusals about values that were submitted.</param>
    /// <param name="required">Refusals about required properties that were not submitted.</param>
    /// <returns>
    /// The combined reason, or <see langword="null"/> when neither accumulator gathered anything.
    /// </returns>
    private static ResultReason? CombineFieldFailures(
        ProfileFieldFailures values,
        ProfileFieldFailures required)
    {
        ResultReason? summary = values.First ?? required.First;

        if (summary is null)
        {
            return null;
        }

        var merged = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (Dictionary<string, List<string>>? source in new[] { values.Messages, required.Messages })
        {
            if (source is null)
            {
                continue;
            }

            foreach (KeyValuePair<string, List<string>> entry in source)
            {
                if (merged.TryGetValue(entry.Key, out IReadOnlyList<string>? existing))
                {
                    // One property can legitimately fail twice — a value both too long and malformed — and both
                    // sentences are kept, because each names a different correction.
                    merged[entry.Key] = existing.Concat(entry.Value).ToArray();

                    continue;
                }

                merged[entry.Key] = entry.Value.ToArray();
            }
        }

        return new ResultReason(summary.Code, summary.Message, merged);
    }

    /// <summary>Tests a submitted profile value against the rules its definition declares.</summary>
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

        Result<Regex> expression = GetValidationExpression(definition.ValidationExpression);
        if (expression.IsFailure)
        {
            return new ResultReason(
                ProfilePropertyValidationFailedCode,
                FormattableString.Invariant(
                    $"The validation rule declared for profile property \"{definition.PropertyName}\" could not be applied."));
        }

        try
        {
            if (!expression.Value.IsMatch(value))
            {
                return new ResultReason(ProfilePropertyValidationFailedCode, FormattableString.Invariant(
                    $"Profile property \"{definition.PropertyName}\" does not match the format it requires."));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new ResultReason(ProfilePropertyValidationFailedCode, FormattableString.Invariant(
                $"The validation rule declared for profile property \"{definition.PropertyName}\" could not be applied."));
        }

        return null;
    }

    /// <summary>Validates a regular expression before it is persisted as tenant configuration.</summary>
    /// <param name="expression">The submitted expression, or <see langword="null"/> for no rule.</param>
    /// <returns>The refusal reason, or <see langword="null"/> when the expression is safe to store.</returns>
    private static ResultReason? ValidateStoredExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        Result<Regex> validated = GetValidationExpression(expression);
        return validated.IsFailure ? validated.Error : null;
    }

    /// <summary>
    /// Resolves a bounded regular-expression object, preferring the non-backtracking engine and retaining a
    /// bounded cache of validated tenant expressions.
    /// </summary>
    /// <param name="expression">The exact tenant-authored expression.</param>
    /// <returns>The compiled expression or a caller-safe validation failure.</returns>
    private static Result<Regex> GetValidationExpression(string expression)
    {
        if (expression.Length > ValidationExpressionMaximumLength)
        {
            return Result<Regex>.Failure(
                ProfileDefinitionExpressionInvalidCode,
                FormattableString.Invariant(
                    $"A validation expression must be {ValidationExpressionMaximumLength} characters or fewer."));
        }

        if (ValidationExpressionCache.TryGetValue(expression, out Regex? cached))
        {
            return Result<Regex>.Success(cached);
        }

        Regex created;
        try
        {
            try
            {
                created = new Regex(
                    expression,
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    ValidationExpressionTimeout);
            }
            catch (NotSupportedException)
            {
                created = new Regex(
                    expression,
                    RegexOptions.CultureInvariant,
                    ValidationExpressionTimeout);
            }
        }
        catch (ArgumentException)
        {
            return Result<Regex>.Failure(
                ProfileDefinitionExpressionInvalidCode,
                "The validation expression is not a valid regular expression.");
        }

        // Removing a single entry per admitted expression makes the cost PROPORTIONATE: introducing one new
        // expression can displace at most one other, so a writer can no longer do more damage than the work
        // it brought. The cache stays exactly at its ceiling rather than emptying and refilling.
        if (ValidationExpressionCache.Count >= ValidationExpressionCacheMaximum)
        {
            foreach (string victim in ValidationExpressionCache.Keys)
            {
                ValidationExpressionCache.TryRemove(victim, out _);
                break;
            }
        }

        ValidationExpressionCache.TryAdd(expression, created);
        return Result<Regex>.Success(created);
    }

    /// <summary>Writes a submitted profile value onto a row, choosing the column that can hold it.</summary>
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

    /// <summary>Builds the submission that clears a stored profile answer without discarding its row.</summary>
    /// <param name="row">The stored answer being cleared.</param>
    /// <returns>A submission carrying an empty value and the row's existing visibility.</returns>
    private static UserProfileValueDto Cleared(UserProfileValue row) => new()
    {
        PropertyDefinitionId = row.PropertyDefinitionId,
        PropertyValue = string.Empty,
        Visibility = row.Visibility,
    };

    /// <summary>Writes one module setting, updating the stored row when it already exists.</summary>
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

    /// <summary>Reads a stored switch, falling back to the legacy default when it is absent or unreadable.</summary>
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

    /// <summary>Reads a stored page reference, translating the legacy sentinel into an absence.</summary>
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

    /// <summary>Reads a stored string, falling back to the legacy default when it is absent.</summary>
    /// <param name="map">The stored settings.</param>
    /// <param name="settingName">The setting name.</param>
    /// <param name="fallback">The legacy default.</param>
    /// <returns>The stored value, or the fallback.</returns>
    private static string ReadString(IReadOnlyDictionary<string, string> map, string settingName, string fallback)
        => map.TryGetValue(settingName, out string? stored) ? stored : fallback;

    /// <summary>Renders a switch in the form the legacy store held.</summary>
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

    /// <summary>The tenant and the account a member-services operation acts within, resolved once.</summary>
    /// <param name="Portal">
    /// The tenant row, read rather than probed because the subscribe predicate needs its payment-processor
    /// account.
    /// </param>
    /// <param name="Account">The account the operation acts on.</param>
    private readonly record struct MemberServiceScope(Portal Portal, User Account);

    /// <inheritdoc />
    public async Task<Result<PagedResult<MemberServiceDto>>> ListMemberServicesAsync(
        int portalId,
        int userId,
        MemberServicePagedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refused here as well as at the boundary, because this is a public application-layer contract and
        // the validator that rejects an ordering or a filter runs on the HTTP path only. The catalogue is
        // published in the order the tenant's subscribable roles are read; accepting a sort field and then
        // ignoring it would tell the caller their ordering applied.
        if (request.HasSort)
        {
            return Result<PagedResult<MemberServiceDto>>.Failure(
                MemberServicePagingInvalidCode,
                $"The member-service catalogue cannot be ordered by '{request.SortBy}'.");
        }

        if (request.HasQuery)
        {
            return Result<PagedResult<MemberServiceDto>>.Failure(
                MemberServicePagingInvalidCode,
                "The member-service catalogue is not filtered by text.");
        }

        Result<MemberServiceScope> scope = await OpenMemberServiceScopeAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result<PagedResult<MemberServiceDto>>.Failure(scope.Error!);
        }

        IReadOnlyList<Role> published = await _roles
            .GetSubscribableRolesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<UserRole> held = await _roles
            .GetUserRolesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        // Keyed by role because that is how the two sides are paired.
        var assignments = new Dictionary<int, UserRole>(held.Count);
        foreach (UserRole assignment in held)
        {
            _ = assignments.TryAdd(assignment.RoleId, assignment);
        }

        // ONE clock reading for the whole catalogue. Reading it per row would let two rows classify against
        // different days if the loop straddled midnight, so every row is judged against the same instant.
        DateTime today = _clock.UtcNow;

        bool tenantTakesPayment = !string.IsNullOrWhiteSpace(scope.Value.Portal.ProcessorUserId);

        var catalogue = new List<MemberServiceDto>(published.Count);
        foreach (Role role in published)
        {
            catalogue.Add(UserMappings.ToMemberService(
                role,
                assignments.GetValueOrDefault(role.RoleId),
                today,
                tenantTakesPayment));
        }

        // ⚠ CLASSIFIED IN FULL, THEN PAGED, AND THE ORDER OF THOSE TWO IS DELIBERATE. Every row's
        // subscription state is decided against the single clock reading above, so cutting the page earlier
        // would save classifying the rows that are not returned while making the reported total a count of
        // rows nobody classified. Paging last keeps the total honest and keeps every row's answer identical
        // to what the unpaged catalogue reported for it.
        if (request.PageSize == 0)
        {
            return Result<PagedResult<MemberServiceDto>>.Success(
                PagedResult<MemberServiceDto>.Unpaged(catalogue));
        }

        List<MemberServiceDto> window = catalogue
            .Skip(Paging.SkipCount(request.PageIndex, request.PageSize))
            .Take(request.PageSize)
            .ToList();

        return Result<PagedResult<MemberServiceDto>>.Success(
            PagedResult<MemberServiceDto>.Create(window, catalogue.Count, request.PageIndex, request.PageSize));
    }

    /// <inheritdoc />
    public async Task<Result> SubscribeToServiceAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default)
    {
        Result<Role> offer = await ResolveServiceOfferAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);
        if (offer.IsFailure)
        {
            return Result.Failure(offer.Error!);
        }

        // The legacy gate is `If objRole.IsPublic And objRole.ServiceFee = 0.0`. The public test is already
        // satisfied by the resolver above; the fee test is the excluded payment path, so it is refused here
        // rather than redirected.
        if (ChargesServiceFee(offer.Value))
        {
            return Result.Failure(
                ServicePaymentRequiredCode,
                "This service charges a fee, and payment cannot be taken here.");
        }

        // NEITHER DATE IS CALLER-SUPPLIED, and that is what makes this a subscription rather than an
        // administrative assignment.
        return await _roleService
            .AssignUserToRoleAsync(
                portalId,
                roleId,
                new RoleAssignmentRequest { UserId = userId },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> CancelServiceAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default)
    {
        Result<Role> offer = await ResolveServiceOfferAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);
        if (offer.IsFailure)
        {
            return Result.Failure(offer.Error!);
        }

        if (ChargesServiceFee(offer.Value))
        {
            return Result.Failure(
                ServicePaymentRequiredCode,
                "This service charges a fee, and a paid subscription cannot be settled here.");
        }

        // The delegate answers role_assignment.not_found when the account does not hold the service and
        // role_assignment.protected when the membership may not be withdrawn at all, and it decides between
        // deleting the row and back-dating its expiry to retain a consumed paid trial.
        return await _roleService
            .RemoveUserFromRoleAsync(portalId, roleId, userId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> StartServiceTrialAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken = default)
    {
        Result<Role> offer = await ResolveServiceOfferAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);
        if (offer.IsFailure)
        {
            // The resolver's public-role refusal is reported under the TRIAL code on this path, because
            // ShowTrial's first arm and ShowSubscribe's are the same test and a caller of this operation
            // asked about a trial.
            return Result.Failure(
                offer.Error!.Code == ServiceNotOfferedCode
                    ? new ResultReason(ServiceTrialNotOfferedCode, TrialNotOfferedMessage)
                    : offer.Error!);
        }

        Role role = offer.Value;

        // ShowTrial and UseTrial (:L120-L133) together.
        if (!ChargesServiceFee(role) || ChargesTrialFee(role))
        {
            return Result.Failure(ServiceTrialNotOfferedCode, TrialNotOfferedMessage);
        }

        UserRole? existing = await _roles
            .GetUserRoleAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);

        // `If (objUserRole Is Nothing) OrElse (Not objUserRole.IsTrialUsed)` (:L336): no membership, or a
        // membership whose nullable trial flag is absent or false.
        if (existing is not null && (existing.IsTrialUsed ?? false))
        {
            return Result.Failure(ServiceTrialNotOfferedCode, TrialNotOfferedMessage);
        }

        return await _roleService
            .AssignUserToRoleAsync(
                portalId,
                roleId,
                new RoleAssignmentRequest { UserId = userId },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<RedeemServiceCodeResultDto>> RedeemServiceCodeAsync(
        int portalId,
        int userId,
        RedeemServiceCodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<MemberServiceScope> scope = await OpenMemberServiceScopeAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result<RedeemServiceCodeResultDto>.Failure(scope.Error!);
        }

        // Duplicates RedeemServiceCodeRequestValidator's emptiness rule rather than trusting it, so the
        // guard holds for a caller that reached this service without the validation pipeline in front of
        // it.
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Result<RedeemServiceCodeResultDto>.Failure(
                ServiceCodeRequiredCode,
                RedeemServiceCodeRequestValidator.CodeRequiredMessage);
        }

        string code = request.Code;

        // EVERY role of the tenant is searched, public or not, free or not.
        IReadOnlyList<Role> tenantRoles = await _roles
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // The loop has NO EARLY EXIT, matching L410-L420, so one code legitimately enrols an account in
        // several services. Ordered by name and then key, which is the order the catalogue read returns, so
        // a client can present the result against the grid it already shows.
        List<Role> matches = tenantRoles
            .Where(role => !string.IsNullOrEmpty(role.RsvpCode)
                && string.Equals(role.RsvpCode, code, StringComparison.Ordinal))
            .OrderBy(role => role.RoleName, StringComparer.Ordinal)
            .ThenBy(role => role.RoleId)
            .ToList();

        if (matches.Count == 0)
        {
            // SEC: A FAILED ATTEMPT IS RECORDED, AND THE SUBMITTED CODE IS NOT. The legacy handler answered
            // a miss with an on-screen sentence and wrote nothing, so an installation could be guessed at
            // indefinitely and leave no trace; the endpoint's window now bounds the RATE, and this record
            // is what makes a bounded-but-persistent attempt visible to an operator.
            RecordAudit(
                AuditEventNames.ServiceCodeRedemptionFailure,
                portalId,
                userId,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["CodedServiceCount"] = tenantRoles
                        .Count(role => !string.IsNullOrEmpty(role.RsvpCode))
                        .ToString(CultureInfo.InvariantCulture),
                });

            return Result<RedeemServiceCodeResultDto>.Failure(
                ServiceCodeNotMatchedCode,
                "The invitation code entered is not valid or does not exist.");
        }

        var enrolled = new List<RedeemedServiceDto>(matches.Count);
        foreach (Role role in matches)
        {
            // The three-argument UpdateUserRole overload the legacy handler called forwards to the
            // four-argument one with Cancel = False, which is the ordinary assignment this delegate
            // performs.
            Result assigned = await _roleService
                .AssignUserToRoleAsync(
                    portalId,
                    role.RoleId,
                    new RoleAssignmentRequest { UserId = userId },
                    cancellationToken)
                .ConfigureAwait(false);

            if (assigned.IsFailure)
            {
                // A refusal on one match ABANDONS the redemption rather than reporting a partial success,
                // because a caller told "you were enrolled in these two" when a third was refused has no
                // way to learn about the third.
                return Result<RedeemServiceCodeResultDto>.Failure(assigned.Error!);
            }

            enrolled.Add(UserMappings.ToRedeemedService(role));
        }

        RecordAudit(
            AuditEventNames.ServiceCodeRedeemed,
            portalId,
            userId,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GrantedServiceCount"] = enrolled.Count.ToString(CultureInfo.InvariantCulture),
            });

        return Result<RedeemServiceCodeResultDto>.Success(new RedeemServiceCodeResultDto
        {
            Roles = enrolled,
        });
    }

    /// <summary>Wording reported for every reason a trial is not on offer.</summary>
    /// <remarks>
    /// Deliberately one sentence for four distinct conditions - the role is not published, it charges no
    /// service fee and so has nothing to trial, its trial itself carries a fee, or this account has already
    /// consumed it. Distinguishing them would tell a caller which of a tenant's commercial terms it had
    /// guessed wrong about, and the catalogue already reports whether the trial is offered.
    /// </remarks>
    private const string TrialNotOfferedMessage = "This service offers no trial to this account.";

    /// <summary>
    /// Resolves the tenant and the account a member-services operation acts within, and enforces the
    /// tenant's own switch.
    /// </summary>
    /// <param name="portalId">The tenant.</param>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Token observed while the two rows are read.</param>
    /// <returns>The resolved scope, or the reason the operation is refused.</returns>
    /// <remarks>
    /// The tenant row is READ rather than probed, because the subscribe predicate needs its
    /// payment-processor account. The account is read so that an unknown one is answered 404 rather than
    /// silently producing an empty catalogue or an assignment against a row that does not exist.
    /// </remarks>
    private async Task<Result<MemberServiceScope>> OpenMemberServiceScopeAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken)
    {
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result<MemberServiceScope>.Failure(
                ServicePortalNotFoundCode,
                FormattableString.Invariant($"No portal bears identifier {portalId}."));
        }

        User? account = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Result<MemberServiceScope>.Failure(
                NotFoundCode,
                FormattableString.Invariant($"Portal {portalId} has no account bearing identifier {userId}."));
        }

        MembershipSettingsDto? settings = await ReadMembershipSettingsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        if (settings is not null && !settings.ProfileManageServices)
        {
            return Result<MemberServiceScope>.Failure(
                ServiceDisabledCode,
                "This site does not offer self-service subscription management.");
        }

        return Result<MemberServiceScope>.Success(new MemberServiceScope(portal, account));
    }

    /// <summary>Resolves the published role a member-services write operation addresses.</summary>
    /// <param name="portalId">The tenant.</param>
    /// <param name="userId">The account.</param>
    /// <param name="roleId">The role the service is expressed as.</param>
    /// <param name="cancellationToken">Token observed while the rows are read.</param>
    /// <returns>The role, or the reason the operation is refused.</returns>
    private async Task<Result<Role>> ResolveServiceOfferAsync(
        int portalId,
        int userId,
        int roleId,
        CancellationToken cancellationToken)
    {
        Result<MemberServiceScope> scope = await OpenMemberServiceScopeAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);
        if (scope.IsFailure)
        {
            return Result<Role>.Failure(scope.Error!);
        }

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Result<Role>.Failure(
                ServiceRoleNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} has no role bearing identifier {roleId}."));
        }

        if (!role.IsPublic)
        {
            return Result<Role>.Failure(
                ServiceNotOfferedCode,
                "This role is not offered for self-service subscription.");
        }

        return Result<Role>.Success(role);
    }

    /// <summary>Reports whether a role charges a recurring service fee.</summary>
    /// <param name="role">The role to test.</param>
    /// <returns><see langword="true"/> when a fee greater than zero is stored.</returns>
    /// <remarks>
    /// MIGRATION: absence is read as "no fee", which the legacy comparison could not do.
    /// <c>RoleInfo.ServiceFee</c> was a non-nullable <c>Single</c>, so a stored <c>NULL</c> arrived through
    /// <c>Null.SetNull</c> as <c>Single.MinValue</c> - not zero - and <c>objRole.ServiceFee = 0.0</c> was
    /// consequently FALSE for a role with no fee at all, handing such a role to the payment page.
    /// </remarks>
    private static bool ChargesServiceFee(Role role) =>
        role.ServiceFee is decimal serviceFee && serviceFee > 0m;

    /// <summary>Reports whether a role charges a fee for its trial period.</summary>
    /// <param name="role">The role to test.</param>
    /// <returns><see langword="true"/> when a trial fee greater than zero is stored.</returns>
    private static bool ChargesTrialFee(Role role) =>
        role.TrialFee is decimal trialFee && trialFee > 0m;
}
