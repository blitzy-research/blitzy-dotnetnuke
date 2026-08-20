using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Common;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
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

/// <summary>Creates, reads, modifies and removes portals, and manages the host names bound to them.</summary>
public sealed class PortalService : IPortalService
{
    private const string PagingInvalidCode = "portal.paging_invalid";

    private const string NotFoundCode = "portal.not_found";

    /// <summary>
    /// Reason code returned when a caller's update carries a concurrency token that no longer matches the
    /// stored tenant, so the record moved between the read and the write.
    /// </summary>
    /// <remarks>
    /// The suffix <c>concurrency_conflict</c> is what the API surface maps to <c>409 Conflict</c>, and it
    /// is the same suffix the role contract uses for the same outcome - deliberately, so a client has one
    /// conflict to recognise rather than two spellings of it.
    /// </remarks>
    private const string ConcurrencyConflictCode = "portal.concurrency_conflict";

    private const string CreationFailedCode = "portal.creation_failed";

    /// <summary>
    /// Reason code recorded - never returned - when the installation's page-permission catalogue does not
    /// declare a key the new tenant's home page would otherwise have been granted.
    /// </summary>
    /// <remarks>
    /// This code never reaches a caller, so it is deliberately NOT one of the codes the API surface maps to
    /// a status. It exists so that the audit record for a tenant created with an incomplete grant set is
    /// searchable, because the repair is an installation-level one and the condition is invisible in the
    /// response.
    /// </remarks>
    private const string PermissionCatalogueIncompleteCode = "portal.permission_catalogue_incomplete";

    /// <summary>Reason code reported when the requested administrator account name is already taken.</summary>
    private const string AdministratorDuplicateCode = "portal.administrator_duplicate";

    private const string AdministratorReferenceInvalidCode = "portal.administrator_invalid";

    private const string TabReferenceInvalidCode = "portal.tab_reference_invalid";

    private const string ProcessorReferenceInvalidCode = "portal.processor_reference_invalid";

    private const string AliasDuplicateCode = "portal.alias_duplicate";

    /// <summary>
    /// Reason code reported when tenant creation lost a race for a unique value the store could name only
    /// vaguely, so neither the alias nor the account-name code can be stated with confidence.
    /// </summary>
    /// <remarks>
    /// A last resort, and deliberately not the usual answer.
    /// </remarks>
    private const string CreationConflictCode = "portal.creation_conflict";

    private const string AliasNotFoundCode = "portal.alias_not_found";

    private const string LastRemainingCode = "portal.last_remaining";

    /// <summary>
    /// Reason code reported when a rename or a removal is addressed at the alias the CURRENT REQUEST
    /// resolved through.
    /// </summary>
    /// <remarks>
    /// WHAT IT PREVENTS. Renaming or unbinding the alias the current session is using re-points that host
    /// name at nothing, so the tenant stops resolving for every caller arriving through it - and the
    /// operator who did it cannot reach the screen that would undo it, because reaching that screen
    /// requires the tenant to resolve.
    /// </remarks>
    private const string AliasInUseConflictCode = "portal.alias_in_use.conflict";

    private const string MemberSessionRevocationFailedCode =
        "portal.member.session.revocation_store_unavailable";

    /// <summary>
    /// Reported when member sessions were ended but the portal removal itself did not become durable.
    /// </summary>
    /// <remarks>
    /// The session store is not a relational participant, so the two stores cannot roll back together. This
    /// code exists so that outcome is stated instead of being presented as an ordinary server fault: the
    /// caller learns that the tenant still exists AND that some members must sign in again, which is
    /// exactly the state the retry has to be performed against.
    /// </remarks>
    private const string PortalRemovalIncompleteCode = "portal.delete.partially_applied";

    /// <summary>
    /// Reported when a member's credential store could not be REACHED while the tenant was being removed, so
    /// the removal was abandoned rather than committed with a credential left behind.
    /// </summary>
    /// <remarks>
    /// Raised for an unreachable store alone. A store that answers and holds no credential for the member is
    /// already in the state this step is reaching for, so the removal continues through it - the same rule
    /// <c>UserService.DeleteUserAsync</c> applies, stated once in both places because it is one rule about
    /// how a store-unconfirmed outcome is classified rather than two local judgements.
    /// </remarks>
    private const string MemberCredentialRemovalFailedCode =
        "portal.member.credential.removal_store_unavailable";

    /// <summary>
    /// Reason code reported when a child portal was requested but the parent authority its address would be
    /// composed beneath could not be established.
    /// </summary>
    private const string ParentAliasUnresolvedCode = "portal.parent_alias_unresolved";

    /// <summary>
    /// Reason code reported when a child portal was requested beneath a parent that is ITSELF addressed
    /// beneath a path segment, so the composed address would be deeper than this deployment can deliver.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this refusal is net-new, and it replaces a legacy behaviour rather than preserving it.
    /// </remarks>
    private const string ParentAliasTooDeepCode = "portal.parent_alias_too_deep";

    private const string PortalResourceType = "Portal";

    /// <summary>The character that separates a child portal's segment from its parent's authority.</summary>
    private const char AliasPathSeparator = '/';

    /// <summary>
    /// Legacy cache key shape for a single portal, preserved verbatim from <c>DataCache.PortalCacheKey</c>.
    /// </summary>
    private const string PortalCacheKeyFormat = "Portal{0}";

    /// <summary>
    /// Legacy base cache timeout in minutes for a single portal, preserved verbatim from
    /// <c>DataCache.PortalCacheTimeOut</c>.
    /// </summary>
    private const int PortalCacheTimeOutMinutes = 20;

    private const string DemoPeriodSetting = "DemoPeriod";

    private const string HostFeeSetting = "HostFee";

    private const string HostSpaceSetting = "HostSpace";

    private const string PageQuotaSetting = "PageQuota";

    private const string UserQuotaSetting = "UserQuota";

    private const string SiteLogHistorySetting = "SiteLogHistory";

    private const string HostCurrencySetting = "HostCurrency";

    private const string FallbackCurrency = "USD";

    private const string AdministratorsRoleName = "Administrators";

    private const string AdministratorsRoleDescription = "Portal Administrators";

    private const string RegisteredUsersRoleName = "Registered Users";

    private const string RegisteredUsersRoleDescription = "Registered Users";

    private const string SubscribersRoleName = "Subscribers";

    private const string SubscribersRoleDescription = "A public role for portal subscriptions";

    /// <summary>The editor type recorded on a default profile property definition, meaning "not resolved".</summary>
    /// <remarks>
    /// Zero rather than an arbitrary number. The legacy helper took this value from the <c>Lists</c> table,
    /// whose subsystem is out of scope, and that table is <c>IDENTITY (1, 1)</c>
    /// (<c>03.00.01.SqlDataProvider</c>) - so zero is guaranteed not to name a real editor type and
    /// identifies exactly the rows whose type was never resolved.
    /// </remarks>
    private const int UnresolvedProfileDataType = 0;

    /// <summary>
    /// The character bound the legacy default profile definitions carried for a free-text property.
    /// </summary>
    private const int DefaultProfilePropertyLength = 50;

    /// <summary>The name of the page every new tenant is created with.</summary>
    /// <remarks>
    /// The name the stock portal template gave its first page, and the name the integration seed uses, so a
    /// created tenant and a seeded one are recognisably the same shape.
    /// </remarks>
    private const string HomePageName = "Home";

    /// <summary>The role identifier the schema reserves for "every user, signed in or not".</summary>
    /// <remarks>
    /// This is a RESERVED identifier rather than a row in the Roles table, which is why it is a constant
    /// here and not a lookup. It is used for exactly one grant - the home page's view permission - so that
    /// a brand-new tenant is reachable before any account has been enrolled in it.
    /// </remarks>
    private const int AllUsersRoleId = -1;

    /// <summary>The scope code under which the shipped page-permission catalogue is filed.</summary>
    private const string TabPermissionScopeCode = "SYSTEM_TAB";

    private readonly IPortalRepository _portals;
    private readonly IPortalAliasRepository _aliases;
    private readonly ITabRepository _tabs;
    private readonly IUserProfileRepository _profiles;
    private readonly IPermissionRepository _permissions;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;

    /// <summary>Module repository, used only to remove a tenant's modules before the tenant row itself.</summary>
    /// <remarks>
    /// This dependency exists because <c>FK_Modules_Portals</c> is the ONE foreign key to
    /// <c>dbo.Portals</c> that carries no <c>ON DELETE CASCADE</c> - every other one does
    /// (<c>PortalAlias</c>, <c>PortalDesktopModules</c>, <c>RoleGroups</c>, <c>Roles</c>, <c>Tabs</c>,
    /// <c>UserPortals</c>, <c>ProfilePropertyDefinition</c>).
    /// </remarks>
    private readonly IModuleRepository _modules;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IHostSettingsService _hostSettings;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokens;
    private readonly IClock _clock;
    private readonly ICacheService _cache;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditSink _audit;
    private readonly IPortalContextHolder _portalContext;
    private readonly CachingOptions _caching;

    /// <summary>Initialises a new instance of the <see cref="PortalService"/> class.</summary>
    /// <param name="portals">Portal repository.</param>
    /// <param name="aliases">Portal alias repository.</param>
    /// <param name="profiles">Stages the default profile property definitions a new tenant begins with.</param>
    /// <param name="permissions">
    /// Resolves the catalogue entries the new tenant's home page is granted against.
    /// </param>
    /// <param name="tabs">Page repository, consulted for the page tally and the host root page.</param>
    /// <param name="users">Account repository, used for the administrator account and its credential.</param>
    /// <param name="roles">Role repository, used for the stock roles and their assignments.</param>
    /// <param name="modules">
    /// Module repository, used only to remove a tenant's modules before the tenant row, because
    /// <c>FK_Modules_Portals</c> declares no cascade.
    /// </param>
    /// <param name="unitOfWork">Commits each write exactly once.</param>
    /// <param name="hostSettings">Supplies the installation defaults a new portal inherits.</param>
    /// <param name="passwordHasher">Hashes the administrator's password before it is stored.</param>
    /// <param name="tokens">Ends every refresh-token family belonging to an account portal removal deletes.</param>
    /// <param name="clock">Supplies the current instant, so time-dependent behaviour is testable.</param>
    /// <param name="cache">Absorbs the legacy portal cache.</param>
    /// <param name="currentUser">
    /// Identifies the caller, which the update path needs in order to enforce the host-only rule the legacy
    /// settings screen applied to the hosting and quota fields.
    /// </param>
    /// <param name="audit">Records the tenant lifecycle under the legacy event names.</param>
    /// <param name="portalContext">The tenant the current request resolved to, where there is one.</param>
    /// <param name="caching">
    /// Bound caching configuration, taken as a plain settings object because the application layer
    /// deliberately depends on no options package.
    /// </param>
    public PortalService(
        IPortalRepository portals,
        IPortalAliasRepository aliases,
        ITabRepository tabs,
        IUserProfileRepository profiles,
        IPermissionRepository permissions,
        IUserRepository users,
        IRoleRepository roles,
        IModuleRepository modules,
        IUnitOfWork unitOfWork,
        IHostSettingsService hostSettings,
        IPasswordHasher passwordHasher,
        ITokenService tokens,
        IClock clock,
        ICacheService cache,
        ICurrentUser currentUser,
        IAuditSink audit,
        IPortalContextHolder portalContext,
        CachingOptions caching)
    {
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _hostSettings = hostSettings ?? throw new ArgumentNullException(nameof(hostSettings));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
    }

    /// <summary>Records one committed tenant-lifecycle change on the audit trail.</summary>
    /// <param name="record">The event to record, already carrying its tenant facts.</param>
    private void RecordAudit(AuditEvent record)
    {
        if (_currentUser.IsAuthenticated)
        {
            record = record with
            {
                ActorUserId = _currentUser.UserId,
            };
        }

        _audit.Record(record);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<PortalListItemDto>>> ListPortalsAsync(
        PagedRequest request,
        string? nameFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PageIndex < 0
            || request.PageSize < 0
            || request.PageSize > PagedRequestValidator.MaximumPageSize)
        {
            // The request validator rejects the same coordinates at the API edge; this guard makes the
            // service safe to call from anywhere, including a test that bypasses validation.
            return Result<PagedResult<PortalListItemDto>>.Failure(
                PagingInvalidCode,
                "The requested page coordinates are outside the permitted range.");
        }

        // The PER-COLLECTION ordering set is enforced HERE, not only at the API edge.
        if (!SortableFields.IsPermittedFor(request.SortBy, SortableFields.Portals))
        {
            return Result<PagedResult<PortalListItemDto>>.Failure(
                PagingInvalidCode,
                $"Portals cannot be ordered by '{request.SortBy}'.");
        }

        // An explicit name filter takes precedence over the request's generic search term, because the
        // caller stated it deliberately; the generic term is used only when no explicit filter is given.
        string? effectiveFilter = string.IsNullOrWhiteSpace(nameFilter) ? request.Query : nameFilter;

        PagedResult<Portal> page = await _portals.ListAsync(
            request.PageIndex,
            request.PageSize,
            effectiveFilter,
            request.SortBy,
            request.SortDir == SortDirection.Descending,
            cancellationToken).ConfigureAwait(false);

        // The distinct tenants the WINDOW actually contains. Resolved before the alias read below because
        // that read is keyed by them, and reused by the two tally reads further down, so the page's
        // identity set is established once.
        IReadOnlyCollection<int> pagePortalIds = page.Items
            .Select(portal => portal.PortalId)
            .Distinct()
            .ToList();

        IReadOnlyList<PortalAlias> pageAliases = await _aliases
            .GetByPortalIdsAsync(pagePortalIds, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, List<string>> aliasesByPortal = pageAliases
            .GroupBy(alias => alias.PortalId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(alias => alias.HttpAlias ?? string.Empty)
                              .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                              .ToList());

        IReadOnlyDictionary<int, int> usersByPortal = await _portals
            .CountUsersForPortalsAsync(pagePortalIds, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyDictionary<int, int> pagesByPortal = await _portals
            .CountPagesForPortalsAsync(pagePortalIds, cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<PortalListItemDto>(page.Items.Count);
        foreach (Portal portal in page.Items)
        {
            IReadOnlyList<string> aliases = aliasesByPortal.GetValueOrDefault(portal.PortalId)
                ?? (IReadOnlyList<string>)Array.Empty<string>();

            // The batched members promise a TOTAL map over the identifiers supplied, so a missing key is a
            // contract violation rather than an expected state.
            int users = usersByPortal.TryGetValue(portal.PortalId, out int userTally) ? userTally : 0;
            int pages = pagesByPortal.TryGetValue(portal.PortalId, out int pageTally) ? pageTally : 0;

            rows.Add(PortalMappings.ToListItem(portal, aliases, users, pages));
        }

        // "return everything" is expressed by a NAMED FACTORY, not by a negative page index.
        // GetPortalsByName declared that intent by passing pageIndex = -1, then rewrote its own arguments
        // to page 0 with a page size of Integer.MaxValue once it had detected the sentinel.
        PagedResult<PortalListItemDto> projected = request.PageSize == 0
            ? PagedResult<PortalListItemDto>.Unpaged(rows)
            : PagedResult<PortalListItemDto>.Create(rows, page.TotalCount, page.PageIndex, page.PageSize);

        return Result<PagedResult<PortalListItemDto>>.Success(projected);
    }

    /// <inheritdoc />
    public async Task<Result<PortalDetailDto?>> GetPortalAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = string.Format(CultureInfo.InvariantCulture, PortalCacheKeyFormat, portalId);
        TimeSpan expiration = TimeSpan.FromMinutes(PortalCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        PortalDetailDto? detail = expiration > TimeSpan.Zero
            ? await _cache.GetOrCreateAsync(
                cacheKey,
                token => ReadDetailAsync(portalId, token),
                expiration,
                cancellationToken).ConfigureAwait(false)
            : await ReadDetailAsync(portalId, cancellationToken).ConfigureAwait(false);

        // Absence is an ordinary outcome of a lookup and is reported as a success carrying no value, so that
        // a caller can tell "there is no such tenant" from "the lookup could not be performed".
        return Result<PortalDetailDto?>.Success(detail);
    }

    /// <inheritdoc />
    public async Task<Result<PortalDetailDto>> CreatePortalAsync(
        CreatePortalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string submittedAlias = (request.PortalAlias ?? string.Empty).Trim();
        if (submittedAlias.Length == 0)
        {
            throw new DomainException("A portal alias is required in order to reach the new portal.");
        }

        Result<string> composed = ComposeAlias(request, submittedAlias);

        if (composed.IsFailure)
        {
            return Result<PortalDetailDto>.Failure(composed.Reason!);
        }

        string alias = composed.Value;

        // Host names are matched EXACTLY here, where the legacy installation matched them as substrings.
        bool aliasTaken = await _aliases
            .AliasExistsAsync(alias, null, cancellationToken)
            .ConfigureAwait(false);
        if (aliasTaken)
        {
            return Result<PortalDetailDto>.Failure(
                AliasDuplicateCode,
                $"The host name '{alias}' is already bound to a portal.");
        }

        string administratorUsername = (request.AdministratorUsername ?? string.Empty).Trim();
        if (administratorUsername.Length == 0)
        {
            throw new DomainException("An administrator account name is required in order to create a portal.");
        }

        bool usernameTaken = await _users
            .UsernameExistsAsync(administratorUsername, null, cancellationToken)
            .ConfigureAwait(false);
        if (usernameTaken)
        {
            return Result<PortalDetailDto>.Failure(
                AdministratorDuplicateCode,
                $"The account name '{administratorUsername}' is already in use, so the portal administrator could not be created.");
        }

        string password = request.AdministratorPassword ?? string.Empty;
        if (password.Length == 0)
        {
            throw new DomainException("An administrator password is required in order to create a portal.");
        }

        PortalDefaults defaults = await ReadPortalDefaultsAsync(cancellationToken).ConfigureAwait(false);

        // MIGRATION: five FILE-SYSTEM stages of the legacy creation sequence are not reproduced, listed
        // here in the order they ran so the omission is auditable against the original.

        // ONE TRANSACTION SPANS THE WHOLE OF THE REST OF THIS MEMBER. Creating a tenant is not a single
        // write and cannot be made into one: three columns on the tenant row need keys the store assigns
        // during the first commit, and the administrator's CREDENTIAL lives in the ASP.NET membership
        // objects, which are mapped alongside rather than owned (Rule T4) and are written by statement
        // rather than by the change tracker.
        await using ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
            .ConfigureAwait(false);

        Portal portal = PortalMappings.ToNewPortal(
            request,
            defaults.Currency,
            defaults.ExpiryDate,
            defaults.HostFee,
            defaults.HostSpace,
            defaults.PageQuota,
            defaults.UserQuota,
            defaults.SiteLogHistory,
            (request.HomeDirectory ?? string.Empty).Trim());

        await _portals.AddAsync(portal, cancellationToken).ConfigureAwait(false);

        var portalAlias = new PortalAlias
        {
            Portal = portal,
            HttpAlias = alias,
        };

        // Staged only. The alias commits in the same transaction as the portal, the roles, the pages and
        // the modules, which is why this member yields no key and why the alias is bound by navigation
        // rather than by an identifier the portal does not have yet.
        await _aliases.AddAsync(portalAlias, cancellationToken).ConfigureAwait(false);

        Role administratorsRole = BuildStockRole(
            portal,
            AdministratorsRoleName,
            AdministratorsRoleDescription,
            isPublic: false,
            autoAssignment: false);
        Role registeredUsersRole = BuildStockRole(
            portal,
            RegisteredUsersRoleName,
            RegisteredUsersRoleDescription,
            isPublic: false,
            autoAssignment: true);
        Role subscribersRole = BuildStockRole(
            portal,
            SubscribersRoleName,
            SubscribersRoleDescription,
            isPublic: true,
            autoAssignment: true);

        // Staged, not flushed: IRoleRepository.AddAsync records the insertion and the SaveChanges below
        // flushes it. What keeps the whole multi-table creation sequence atomic is the explicit transaction
        // this method opened - its commit, not any single flush, is the durability boundary.
        await _roles.AddAsync(administratorsRole, cancellationToken).ConfigureAwait(false);
        await _roles.AddAsync(registeredUsersRole, cancellationToken).ConfigureAwait(false);
        await _roles.AddAsync(subscribersRole, cancellationToken).ConfigureAwait(false);

        User administrator = UserMappings.ToNewUser(new CreateUserRequest
        {
            Username = administratorUsername,
            FirstName = request.AdministratorFirstName ?? string.Empty,
            LastName = request.AdministratorLastName ?? string.Empty,
            DisplayName = string.Empty,
            Email = request.AdministratorEmail ?? string.Empty,
        });
        _users.Add(administrator);

        // The instant comes from the injected clock, which is UTC-ONLY, where every legacy reading came
        // from VB's Now and was therefore in the SERVER'S LOCAL zone.
        DateTime createdUtc = _clock.UtcNow;
        _users.AddMembership(new UserPortal
        {
            User = administrator,
            Portal = portal,
            CreatedDate = createdUtc,
            IsAuthorised = true,
        });

        // MIGRATION: the legacy path assigned the new administrator to all three stock roles with an absent
        // effective date and an absent expiry date.
        foreach (Role role in new[] { administratorsRole, registeredUsersRole, subscribersRole })
        {
            await _roles.AddUserRoleAsync(
                new UserRole
                {
                    User = administrator,
                    Role = role,
                    EffectiveDate = null,
                    ExpiryDate = null,
                    IsTrialUsed = false,
                },
                cancellationToken).ConfigureAwait(false);
        }

        // The whole tenant graph - portal, alias, three roles, administrator, membership and three
        // assignments - is committed here. Every foreign key inside the graph is resolved by the object
        // graph itself, so no store-assigned identifier is needed beforehand.
        Result aliasFlush = await FlushCreationAsync(alias, administratorUsername, cancellationToken)
            .ConfigureAwait(false);
        if (aliasFlush.IsFailure)
        {
            return Result<PortalDetailDto>.Failure(aliasFlush.Reason!);
        }

        // Declared OUTSIDE the staging block below so that the permission-catalogue condition the page
        // stage discovers survives to the post-commit audit.
        HomePageStage? page = null;

        // The legacy path had this same shape - insert the portal, create the administrator, create the
        // roles, then call UpdatePortalSetup to stamp the identifiers - and had no transaction over any of
        // it.
        {
            string passwordHash = _passwordHasher.Hash(password);
            bool credentialCreated = await _users
                .CreateCredentialAsync(administrator.UserId, passwordHash, isApproved: true, createdUtc, cancellationToken)
                .ConfigureAwait(false);

            if (!credentialCreated)
            {
                // Returning without committing rolls the transaction back, so the portal, its alias, its
                // three roles, its administrator, the membership and the three enrolments all disappear.
                return Result<PortalDetailDto>.Failure(
                    CreationFailedCode,
                    "The portal administrator's credential could not be created, so the portal was rolled back.");
            }

            await CreateDefaultProfileDefinitionsAsync(portal.PortalId, cancellationToken)
                .ConfigureAwait(false);

            // The page stage can now REFUSE, and the refusal is propagated rather than absorbed: returning
            // here disposes the transaction scope without committing, so the portal, its alias, its three
            // roles, its administrator, the credential and the profile definitions all disappear.
            Result<HomePageStage> pageStage = await CreateHomePageAsync(portal, administratorsRole, cancellationToken)
                .ConfigureAwait(false);

            if (pageStage.IsFailure)
            {
                return Result<PortalDetailDto>.Failure(pageStage.Reason!);
            }

            page = pageStage.Value;
            Tab homePage = page.Page;

            // Conflict-aware, like every flush in this sequence. Nothing staged here is unique-indexed
            // today, and the uniformity is the point: a flush added to this sequence later inherits the
            // correct answer instead of reintroducing a server fault.
            Result pageFlush = await FlushCreationAsync(alias, administratorUsername, cancellationToken)
                .ConfigureAwait(false);
            if (pageFlush.IsFailure)
            {
                return Result<PortalDetailDto>.Failure(pageFlush.Reason!);
            }

            portal.AdministratorId = administrator.UserId;
            portal.AdministratorRoleId = administratorsRole.RoleId;
            portal.RegisteredRoleId = registeredUsersRole.RoleId;
            portal.HomeTabId = homePage.TabId;

            // Conflict-aware, for the reason given on the two flushes above.
            Result stampFlush = await FlushCreationAsync(alias, administratorUsername, cancellationToken)
                .ConfigureAwait(false);
            if (stampFlush.IsFailure)
            {
                return Result<PortalDetailDto>.Failure(stampFlush.Reason!);
            }
        }

        // Everything staged since the transaction was opened becomes durable here, and nothing before it.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string?> installation = new(StringComparer.Ordinal)
        {
            // MIGRATION: THE TENANT NAME AND ALIAS ARE DELIBERATELY ABSENT, AND ONE REVISION RECORDED BOTH.
            // The argument for recording them is readability: a record naming "Contoso Intranet" is easier
            // to reconstruct an incident from than one naming tenant 42.
            ["IsChildPortal"] = request.IsChildPortal.ToString(),
            ["AdministratorId"] = administrator.UserId.ToString(CultureInfo.InvariantCulture),
            ["DescriptionSupplied"] = (!string.IsNullOrWhiteSpace(request.Description))
                .ToString(CultureInfo.InvariantCulture),
            ["KeywordsSupplied"] = (!string.IsNullOrWhiteSpace(request.KeyWords))
                .ToString(CultureInfo.InvariantCulture),
        };

        // Recorded after the commit, so no record can describe an installation that was rolled back. The
        // SUBJECT is the administrator the installation created, which is what makes the record answer "who
        // can now sign in to this tenant" and not merely "a tenant appeared".
        AuditEvent installed = new(AuditEventNames.PortalCreated)
        {
            PortalId = portal.PortalId,
            SubjectUserId = administrator.UserId,
            ResourceType = PortalResourceType,
            ResourceId = portal.PortalId.ToString(CultureInfo.InvariantCulture),
            Properties = installation,
        };

        RecordAudit(installed);

        RecordAudit(installed with { EventName = AuditEventNames.HostAlert });

        // THE PERMISSION-CATALOGUE ALERT, RAISED HERE RATHER THAN FROM THE PAGE STAGE, AND UNDER A
        // DIFFERENT EVENT NAME THAN BEFORE. Two things were wrong with the record it replaces.
        if (page is { } stage && (stage.ViewDefinitionMissing || stage.EditDefinitionMissing))
        {
            RecordAudit(new AuditEvent(AuditEventNames.HostAlert)
            {
                Outcome = AuditOutcome.Failed,
                PortalId = portal.PortalId,
                SubjectUserId = administrator.UserId,
                ResourceType = PortalResourceType,
                ResourceId = portal.PortalId.ToString(CultureInfo.InvariantCulture),
                FailureCode = PermissionCatalogueIncompleteCode,
                Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["PermissionCode"] = TabPermissionScopeCode,
                    ["MissingViewDefinition"] = stage.ViewDefinitionMissing
                        .ToString(CultureInfo.InvariantCulture),
                    ["MissingEditDefinition"] = stage.EditDefinitionMissing
                        .ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        _cache.InvalidateHost();
        _cache.InvalidatePortal(portal.PortalId);

        // MIGRATION: the legacy path had a THIRD invalidation here that is not reproduced, and it is
        // recorded rather than dropped in silence.
        PortalDetailDto? created = await ReadDetailAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);
        if (created is null)
        {
            // The tenant exists and the records above already say so, which is the whole reason they are
            // emitted before this point: this refusal is about the RESPONSE, not about the work.
            return Result<PortalDetailDto>.Failure(
                CreationFailedCode,
                "The portal was created but could not be read back.");
        }

        return Result<PortalDetailDto>.Success(created);
    }

    /// <inheritdoc />
    public async Task<Result<PortalDetailDto?>> UpdatePortalAsync(
        int portalId,
        UpdatePortalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The ownership checks and the write are one serialisable operation. Without the transaction, a
        // membership or page could move after it was validated and before the portal row was saved,
        // recreating the foreign reference through a time-of-check/time-of-use race.
        await using (ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Serializable, cancellationToken)
            .ConfigureAwait(false))
        {
            Portal? portal = await _portals
                .GetByIdAsync(portalId, includeAliases: true, cancellationToken)
                .ConfigureAwait(false);
            if (portal is null)
            {
                return Result<PortalDetailDto?>.Success(null);
            }

            // OPTIMISTIC CONCURRENCY, CHECKED BEFORE ANY OTHER RULE. The order is deliberate and matches
            // the role contract: a caller holding a stale snapshot must be told that the record moved under
            // it, not that some field of the snapshot it is trying to restore is now invalid or now names a
            // page that has since been removed - those are symptoms of the staleness rather than separate
            // faults.
            if (IsWritingOverSomeoneElsesEdit(portal, request))
            {
                return Result<PortalDetailDto?>.Failure(
                    ConcurrencyConflictCode,
                    DescribeConcurrencyConflict(portalId));
            }

            await EnsureHostOnlyFieldsUnchangedAsync(portal, request, cancellationToken).ConfigureAwait(false);

            Result references = await ValidateUpdateReferencesAsync(portal, request, cancellationToken)
                .ConfigureAwait(false);
            if (references.IsFailure)
            {
                return Result<PortalDetailDto?>.Failure(references.Error!);
            }

            PortalMappings.ApplyUpdate(portal, request);

            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException)
            {
                // The token comparison above closes the window a caller can observe, but it cannot close
                // the window BETWEEN that comparison and the write: the store can still refuse the update
                // as a lost update, and under serialisable isolation it can be chosen as a deadlock victim.
                return Result<PortalDetailDto?>.Failure(
                    ConcurrencyConflictCode,
                    DescribeConcurrencyConflict(portalId));
            }
        }

        _cache.InvalidatePortal(portalId);

        PortalDetailDto? detail = await ReadDetailAsync(portalId, cancellationToken).ConfigureAwait(false);
        return Result<PortalDetailDto?>.Success(detail);
    }

    /// <summary>
    /// Reports whether a failed revocation means the session store could not answer, as opposed to
    /// answering that it holds no such session.
    /// </summary>
    /// <param name="reason">The failure the token service reported.</param>
    /// <returns><see langword="true"/> when the store itself was the obstacle.</returns>
    /// <remarks>
    /// Matched on the <c>store_unavailable</c> reason token, which is the same token the API's own
    /// translator classifies as <c>503</c>, so the two readings of a session-store failure cannot drift
    /// apart. Every other failure means the store answered and held nothing, which for a member who never
    /// signed in on this instance is the ordinary case and is not an obstacle to removing the tenant.
    /// </remarks>
    private static bool IsSessionStoreUnavailable(ResultReason? reason) =>
        reason is ResultReason failure
        && failure.Code.Contains("store_unavailable", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<Result> DeletePortalAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // THE WHOLE OF THIS OPERATION IS ONE SERIALISABLE TRANSACTION, and the reason is the guard below
        // rather than the removal itself. The guard counts the installation's tenants, judges the count,
        // and deletes in a later statement.
        await using ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Serializable, cancellationToken)
            .ConfigureAwait(false);

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: true, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result.Failure(NotFoundCode, $"No portal bears identifier {portalId}.");
        }

        PagedResult<Portal> firstPage = await _portals
            .ListAsync(0, 1, null, null, false, cancellationToken)
            .ConfigureAwait(false);

        if (firstPage.TotalCount <= 1)
        {
            return Result.Failure(
                LastRemainingCode,
                "You Can Not Delete The Last Portal In Your Database. The installation must retain at least one portal.");
        }

        int releasedAliasCount = portal.PortalAliases.Count;

        // MIGRATION: THE TENANT'S MODULES ARE REMOVED FIRST, AND EXPLICITLY, because the store will not do
        // it.
        IReadOnlyList<Module> portalModules = await _modules
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        await _modules.DeleteRangeAsync(portalModules, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<User> portalMembers = await _users
            .ListPortalMembersForRemovalAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> accountIdsToRemove = portalMembers
            .Where(member =>
                !member.IsSuperUser
                && !member.UserPortals.Any(membership => membership.PortalId != portalId))
            .Select(member => member.UserId)
            .ToHashSet();

        // THE TWO STORES CANNOT ROLL BACK TOGETHER, SO THIS DOES NOT PRETEND THEY CAN. The session store is
        // not a relational participant and cannot enlist in the transaction below, so the ordering is a
        // deliberate choice between two asymmetric risks.
        int confirmedRevocations = 0;
        int unconfirmedRevocations = 0;

        foreach (User account in portalMembers.Where(account => accountIdsToRemove.Contains(account.UserId)))
        {
            Result revoked = await _tokens
                .RevokeAllRefreshTokensAsync(account.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (revoked.IsFailure)
            {
                // AN UNCONFIRMED RETIREMENT AND AN UNREACHABLE STORE ARE NOT THE SAME FAILURE HERE. The
                // token service now reports success only for a PROVEN retirement, so an account that never
                // signed in on this instance - the common case for most members of a tenant - answers "no
                // such family".
                if (IsSessionStoreUnavailable(revoked.Reason))
                {
                    return Result.Failure(
                        MemberSessionRevocationFailedCode,
                        FormattableString.Invariant(
                            $"The sessions held by account {account.UserId} could not be ended.")
                        + " The portal removal was abandoned before any database row was removed. Try again.");
                }

                unconfirmedRevocations++;
            }
            else
            {
                confirmedRevocations++;
            }
        }

        foreach (User account in portalMembers)
        {
            UserPortal? membership = account.UserPortals
                .SingleOrDefault(candidate => candidate.PortalId == portalId);

            bool removeAccount = accountIdsToRemove.Contains(account.UserId);

            if (removeAccount)
            {
                // Both direct-grant foreign keys to dbo.Users are deliberately restrictive in the legacy
                // schema. Staging these rows first gives EF an explicit dependency graph and prevents an
                // account delete from being issued while either table still points at it.
                await _permissions
                    .DeleteModulePermissionsByUserIdAsync(portalId, account.UserId, cancellationToken)
                    .ConfigureAwait(false);
                await _permissions
                    .DeleteTabPermissionsByUserIdAsync(portalId, account.UserId, cancellationToken)
                    .ConfigureAwait(false);

                // This write reaches the external aspnet membership store immediately but uses the unit of
                // work's connection and ambient transaction.
                MembershipWriteOutcome credential = await _users
                    .DeleteCredentialAsync(account.UserId, cancellationToken)
                    .ConfigureAwait(false);

                // An UNREACHABLE store abandons the removal; a store that answers and holds no credential
                // does not, because that is the end state this step exists to produce. The two were one
                // boolean and the message below - which asserts that nothing was separated - was therefore
                // published over the case where the credential was already gone.
                if (credential == MembershipWriteOutcome.StoreUnavailable)
                {
                    return Result.Failure(
                        MemberCredentialRemovalFailedCode,
                        FormattableString.Invariant(
                            $"The credential held by account {account.UserId} could not be removed.")
                        + " The portal and account were left intact rather than separated. Try again.");
                }

                _users.Remove(account);
                continue;
            }

            if (membership is not null)
            {
                // A host account is installation-wide and must never be globally removed by deleting one
                // tenant, even when this is its only UserPortals row. The legacy bulk path admitted super
                // users and could therefore delete the installation's final operator.
                _users.RemoveMembership(membership);
            }
        }

        foreach (PortalAlias alias in portal.PortalAliases.ToList())
        {
            // Identified by key, as the legacy procedure was. These rows are already loaded, so the
            // repository resolves them from the change tracker rather than re-reading them.
            await _aliases.DeleteAsync(alias.PortalAliasId, cancellationToken).ConfigureAwait(false);
        }

        await _portals.DeleteAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);

        // THE COMMIT IS GUARDED BECAUSE THE COMPENSATION IS IMPOSSIBLE. Sessions ended above cannot be
        // reinstated - a revoked family is retired for good, by design - so a failure from here onwards
        // leaves a state no rollback can restore: the tenant survives and some of its members have been
        // signed out.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (confirmedRevocations > 0)
        {
            _audit.Record(new AuditEvent(AuditEventNames.PortalDeleted)
            {
                Outcome = AuditOutcome.Failed,
                PortalId = portalId,
                ActorUserId = _currentUser.IsAuthenticated ? _currentUser.UserId : null,
                FailureCode = PortalRemovalIncompleteCode,
            });

            return Result.Failure(
                PortalRemovalIncompleteCode,
                FormattableString.Invariant(
                    $"Portal {portalId} was NOT removed - every database change was rolled back - but the sessions of ")
                + FormattableString.Invariant(
                    $"{confirmedRevocations} member account(s) had already been ended and cannot be reinstated. ")
                + "Those members must sign in again. Retry the removal; it is safe to repeat. "
                + FormattableString.Invariant($"Underlying fault: {error.GetType().Name}."));
        }

        _cache.InvalidateTabs(portalId);
        _cache.InvalidatePortal(portalId);
        _cache.InvalidateHost();

        // Per-account entries are evicted too. They are keyed by tenant and login name, so an entry left
        // warm would keep answering for a tenant that no longer exists - and would still answer for an
        // account whose membership of it has just been removed.
        foreach (User account in portalMembers)
        {
            _cache.InvalidateUser(portalId, account.Username);
        }
        // ONE TENANT-SCOPED ERASURE RATHER THAN ONE PER ACCOUNT, and the difference is not efficiency.
        Result purgedSessions = await _tokens
            .PurgePortalSessionRecordsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // Recorded after the commit, so no event can describe a removal that was rolled back. The stable
        // identifier remains in the envelope; the deleted tenant's name is deliberately not copied into the
        // independently retained logging store.
        RecordAudit(new AuditEvent(AuditEventNames.PortalDeleted)
        {
            PortalId = portalId,
            ResourceType = PortalResourceType,
            ResourceId = portalId.ToString(CultureInfo.InvariantCulture),
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AliasesReleased"] = releasedAliasCount.ToString(CultureInfo.InvariantCulture),
                ["SessionRecordsErased"] = purgedSessions.IsSuccess.ToString(CultureInfo.InvariantCulture),
            },
        });

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<PortalSettingsDto?>> GetPortalSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        // This read is deliberately NOT CACHED, and the omission is recorded rather than left to be
        // noticed.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        // There is NO portal-setting entity, and this projection is where that shows.
        return portal is null
            ? Result<PortalSettingsDto?>.Success(null)
            : Result<PortalSettingsDto?>.Success(PortalMappings.ToSettings(portal));
    }

    /// <inheritdoc />
    public async Task<Result<PortalSettingsDto?>> UpdatePortalSettingsAsync(
        int portalId,
        UpdatePortalSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PortalSettingsDto? stored;

        // MIGRATION: THE OWNERSHIP CHECKS AND THE WRITE ARE ONE SERIALISABLE OPERATION HERE, EXACTLY AS
        // THEY ARE ON UpdatePortalAsync, and the omission this replaces was a real defect rather than a
        // stylistic difference.
        await using (ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Serializable, cancellationToken)
            .ConfigureAwait(false))
        {
            // The settings projection carries no aliases, so this path deliberately avoids loading them. The
            // general portal update still requests them because its response is the full detail contract.
            Portal? portal = await _portals
                .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
                .ConfigureAwait(false);
            if (portal is null)
            {
                return Result<PortalSettingsDto?>.Success(null);
            }

            // OPTIMISTIC CONCURRENCY, CHECKED BEFORE ANY OTHER RULE, for the reason given on
            // UpdatePortalAsync: a caller holding a stale snapshot must be told the record moved under it,
            // not that a field of the snapshot it is restoring is now refused or now names a page that has
            // since been removed.
            if (IsWritingOverSomeoneElsesEdit(portal, request))
            {
                return Result<PortalSettingsDto?>.Failure(
                    ConcurrencyConflictCode,
                    DescribeConcurrencyConflict(portalId));
            }

            // Both public update resources replace the same stored row and therefore share the same
            // content-sensitive authorisation and aggregate invariant.
            await EnsureHostOnlyFieldsUnchangedAsync(portal, request, cancellationToken).ConfigureAwait(false);

            // ORDERED BEFORE THE SHARED REFERENCE VALIDATION DELIBERATELY. Both members judge the same
            // condition - a stored administrator that the request would clear - but they report it
            // differently: this one raises DomainException and the shared validation returns
            // portal.administrator_invalid.
            EnsureAdministratorRetained(portal, request);

            // TENANT-SCOPED REFERENCE VALIDATION NOW RUNS ON THIS PATH TOO. It was absent, and the absence
            // was exploitable rather than theoretical: every identifier this request carries the
            // administrator account and the splash, home, login and user pages - was stored without any
            // ownership test, so an authenticated administrator of one tenant could name another tenant's
            // account or another tenant's page and have it written.
            Result references = await ValidateUpdateReferencesAsync(portal, request, cancellationToken)
                .ConfigureAwait(false);
            if (references.IsFailure)
            {
                return Result<PortalSettingsDto?>.Failure(references.Error!);
            }

            PortalMappings.ApplyUpdate(portal, request);

            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException)
            {
                // The token comparison above closes the window a caller can observe, but it cannot close
                // the window BETWEEN that comparison and the write: the store can still refuse the update
                // as a lost update, and under serialisable isolation it can be chosen as a deadlock victim.
                return Result<PortalSettingsDto?>.Failure(
                    ConcurrencyConflictCode,
                    DescribeConcurrencyConflict(portalId));
            }

            // Projected from the tracked entity inside the scope, so the response describes precisely what
            // the commit stored rather than requiring a second read.
            stored = PortalMappings.ToSettings(portal);
        }

        // The settings read bypasses the cache, but other readers do not. Invalidating after the commit
        // prevents the list/detail surfaces from continuing to publish the values this operation replaced.
        _cache.InvalidatePortal(portalId);

        return Result<PortalSettingsDto?>.Success(stored);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PortalAdministratorDto>?>> ListAdministratorCandidatesAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result<IReadOnlyList<PortalAdministratorDto>?>.Success(null);
        }

        // A portal with no administrator role designated has no candidates to offer, and that is an empty
        // answer rather than a failure - the write path's own guard is what refuses a designation, and it
        // permits any account belonging to the portal.
        if (portal.AdministratorRoleId is not int administratorRoleId)
        {
            return Result<IReadOnlyList<PortalAdministratorDto>?>.Success(
                Array.Empty<PortalAdministratorDto>());
        }

        Role? administratorsRole = await _roles
            .GetByIdAsync(administratorRoleId, portalId, cancellationToken)
            .ConfigureAwait(false);
        if (administratorsRole is null)
        {
            return Result<IReadOnlyList<PortalAdministratorDto>?>.Success(
                Array.Empty<PortalAdministratorDto>());
        }

        IReadOnlyList<UserRole> memberships = await _roles
            .GetUserRolesByUsernameAsync(portalId, username: null, administratorsRole.RoleName, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PortalAdministratorDto> candidates = memberships
            .Select(PortalMappings.ToAdministratorCandidate)

            // DISTINCT BY ACCOUNT, because the relation permits one account to hold one role more than once
            // - UserRoles.UserRoleID is the surrogate and no unique constraint spans the account and role
            // pair - and a selector offering the same person twice invites the operator to wonder which of
            // the two they picked.
            .GroupBy(candidate => candidate.UserId)
            .Select(group => group.First())

            // Ordered by the text the selector shows, so the list reads the way it is displayed. The
            // membership read orders by role and then by assignment key, which is the right order for a
            // membership grid and the wrong one for a name picker.
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Result<IReadOnlyList<PortalAdministratorDto>?>.Success(candidates);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PortalAliasDto>>> ListPortalAliasesAsync(
        int? portalId,
        CancellationToken cancellationToken = default)
    {
        if (portalId is int scopedPortalId)
        {
            bool portalExists = await _portals.ExistsAsync(scopedPortalId, cancellationToken).ConfigureAwait(false);
            if (!portalExists)
            {
                return Result<IReadOnlyList<PortalAliasDto>>.Failure(
                    NotFoundCode,
                    $"No portal bears identifier {scopedPortalId}.");
            }
        }

        IReadOnlyList<PortalAlias> aliases = portalId is int wantedPortalId
            ? await _aliases.GetByPortalIdAsync(wantedPortalId, cancellationToken).ConfigureAwait(false)
            : await _aliases.GetAllAsync(cancellationToken).ConfigureAwait(false);

        // Each row is told which alias the CURRENT REQUEST resolved through, so the one that must not be
        // renamed or removed identifies itself. See CurrentPortalAliasId for why the answer is the server's
        // to give.
        int? currentAliasId = CurrentPortalAliasId();

        IReadOnlyList<PortalAliasDto> rows = aliases
            .Select(alias => PortalMappings.ToDto(alias, currentAliasId))
            .ToList();

        return Result<IReadOnlyList<PortalAliasDto>>.Success(rows);
    }

    /// <inheritdoc />
    public async Task<Result<TenantPathPrefixDto>> ResolveTenantPathPrefixAsync(
        string hostAuthority,
        string segment,
        CancellationToken cancellationToken = default)
    {
        // ⚠ A MALFORMED OR RESERVED SEGMENT IS ANSWERED "NO", NOT REFUSED, AND THE DIFFERENCE MATTERS TO THE
        // CALLER. The client asking this is deciding whether to hold the segment aside or match it as a
        // route; a refusal gives it neither answer and leaves it exactly where the defect left it. "Not a
        // tenant" is a complete, actionable answer for a segment nobody could have stored.
        if (string.IsNullOrWhiteSpace(hostAuthority)
            || !PortalAliasTopology.IsAddressableSegment(segment))
        {
            return Result<TenantPathPrefixDto>.Success(
                new TenantPathPrefixDto { Segment = segment ?? string.Empty, IsTenantPath = false });
        }

        string candidate = $"{hostAuthority}{PortalAliasTopology.PathSeparator}{segment}";

        // The SAME reader the request pipeline resolves tenants through, deliberately. A second predicate
        // written here could accept a segment the pipeline rejects, and a client told "tenant" about an
        // address the pipeline then refuses to scope is worse off than one told nothing.
        IReadOnlyList<TenantResolution> matches = await _aliases
            .ResolveTenantsByHttpAliasAsync(new[] { candidate }, cancellationToken)
            .ConfigureAwait(false);

        return Result<TenantPathPrefixDto>.Success(
            new TenantPathPrefixDto { Segment = segment, IsTenantPath = matches.Count > 0 });
    }

    /// <inheritdoc />
    public async Task<Result<PortalAliasDto?>> GetPortalAliasAsync(
        int? portalId,
        int portalAliasId,
        CancellationToken cancellationToken = default)
    {
        PortalAlias? alias = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);

        // The owning portal is verified rather than assumed.
        return alias is null || (portalId is int scopedPortalId && alias.PortalId != scopedPortalId)
            ? Result<PortalAliasDto?>.Success(null)
            : Result<PortalAliasDto?>.Success(PortalMappings.ToDto(alias, CurrentPortalAliasId()));
    }

    /// <inheritdoc />
    public async Task<Result<PortalAliasDto>> AddPortalAliasAsync(
        int portalId,
        CreatePortalAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string httpAlias = NormaliseAlias(request.HttpAlias);

        bool portalExists = await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false);
        if (!portalExists)
        {
            return Result<PortalAliasDto>.Failure(NotFoundCode, $"No portal bears identifier {portalId}.");
        }

        bool aliasTaken = await _aliases
            .AliasExistsAsync(httpAlias, null, cancellationToken)
            .ConfigureAwait(false);
        if (aliasTaken)
        {
            return Result<PortalAliasDto>.Failure(
                AliasDuplicateCode,
                $"The host name '{httpAlias}' is already bound to a portal.");
        }

        var created = new PortalAlias
        {
            PortalId = portalId,
            HttpAlias = httpAlias,
        };

        // Staged, then committed by the unit of work. Only after the commit does created.PortalAliasId hold
        // the generated key, which is what the mapping below reads.
        await _aliases.AddAsync(created, cancellationToken).ConfigureAwait(false);

        // MIGRATION: the existence check above cannot close the race.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            return Result<PortalAliasDto>.Failure(
                AliasDuplicateCode,
                $"The host name '{httpAlias}' is already bound to a portal.");
        }

        // Alias resolution is installation-wide rather than portal-scoped, so binding a host name
        // invalidates the host entries as well as the portal's own.
        _cache.InvalidateHost();
        _cache.InvalidatePortal(portalId);

        // A freshly bound alias cannot be the one this request resolved through - resolution happened
        // before it existed - so the flag comes back false.
        return Result<PortalAliasDto>.Success(PortalMappings.ToDto(created, CurrentPortalAliasId()));
    }

    /// <inheritdoc />
    public async Task<Result<PortalAliasDto>> UpdatePortalAliasAsync(
        int? portalId,
        int portalAliasId,
        UpdatePortalAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // As with the create above, the request carries the host name only. That is what makes the comment
        // further down - that this member does not re-bind the tenant - true by construction rather than by
        // convention: there is no portal key on the wire to re-bind it to.
        string httpAlias = NormaliseAlias(request.HttpAlias);

        PortalAlias? stored = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);

        // The addressed row must belong to the addressed tenant. This is the most consequential of the
        // three ownership checks on this contract: an alias is what tenant resolution matches on, so
        // renaming somebody else's alias re-points their portal's traffic.
        if (stored is null || (portalId is int scopedPortalId && stored.PortalId != scopedPortalId))
        {
            return Result<PortalAliasDto>.Failure(
                AliasNotFoundCode,
                $"No portal alias bears identifier {portalAliasId}.");
        }

        if (IsCurrentPortalAlias(portalAliasId))
        {
            return Result<PortalAliasDto>.Failure(
                AliasInUseConflictCode,
                "This host name is the one the current request reached the portal through, so it cannot " +
                "be renamed. Reach the portal through one of its other host names and try again.");
        }

        bool aliasTaken = await _aliases
            .AliasExistsAsync(httpAlias, portalAliasId, cancellationToken)
            .ConfigureAwait(false);
        if (aliasTaken)
        {
            return Result<PortalAliasDto>.Failure(
                AliasDuplicateCode,
                $"The host name '{httpAlias}' is already bound to another portal alias.");
        }

        // Only the host name is written. The portal an alias is bound to is deliberately not moved here:
        // this member declares no portal-not-found reason code, which is itself the statement that it does
        // not re-validate, and therefore does not re-bind, the tenant.
        stored.HttpAlias = httpAlias;

        await _aliases.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);

        // A rename races exactly as a binding does - the check reads, another request binds, the rename is
        // refused by the index. Same code and wording as the check above, for the reason recorded in full
        // on the create path.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            return Result<PortalAliasDto>.Failure(
                AliasDuplicateCode,
                $"The host name '{httpAlias}' is already bound to another portal alias.");
        }

        _cache.InvalidateHost();
        _cache.InvalidatePortal(stored.PortalId);

        // The current-alias flag is resolved the same way the create path resolves it rather than being
        // written as false, even though the refusal above means this row provably is not the current one.
        return Result<PortalAliasDto>.Success(PortalMappings.ToDto(stored, CurrentPortalAliasId()));
    }

    /// <inheritdoc />
    public async Task<Result> DeletePortalAliasAsync(
        int? portalId,
        int portalAliasId,
        CancellationToken cancellationToken = default)
    {
        PortalAlias? stored = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);

        // Ownership is verified before the removal, for the reason given on the update member above.
        // Unbinding another tenant's alias would make that tenant unreachable at the host name its users
        // hold, which is a denial of service reached from a grant over an unrelated portal.
        if (stored is null || (portalId is int scopedPortalId && stored.PortalId != scopedPortalId))
        {
            return Result.Failure(AliasNotFoundCode, $"No portal alias bears identifier {portalAliasId}.");
        }

        if (IsCurrentPortalAlias(portalAliasId))
        {
            return Result.Failure(
                AliasInUseConflictCode,
                "This host name is the one the current request reached the portal through, so it cannot " +
                "be removed. Reach the portal through one of its other host names and try again.");
        }

        int owningPortalId = stored.PortalId;

        await _aliases.DeleteAsync(portalAliasId, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateHost();
        _cache.InvalidatePortal(owningPortalId);

        return Result.Success();
    }

    /// <summary>Reads one portal and every derived value the detail contract carries.</summary>
    /// <param name="portalId">The portal to read.</param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns>The detail projection, or <see langword="null" /> when no such portal exists.</returns>
    private async Task<PortalDetailDto?> ReadDetailAsync(int portalId, CancellationToken cancellationToken)
    {
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: true, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return null;
        }

        // MIGRATION: both tallies are AWAITED EXPLICITLY here because they were LAZY PROPERTY GETTERS on
        // the legacy record - Users at PortalInfo.vb and Pages at - each of which issued a synchronous
        // database read the first time it was touched, memoised the answer in a backing field, and used -1
        // in that field to mean "not loaded yet".
        int users = await _portals.CountUsersAsync(portalId, cancellationToken).ConfigureAwait(false);
        int pages = await _portals.CountPagesAsync(portalId, cancellationToken).ConfigureAwait(false);

        // MIGRATION: the two role names were correlated sub-selects in the legacy portal view (04.05.00), so
        // they are read-only projections here rather than stored columns.
        IReadOnlyDictionary<int, string> roleNames = await _portals
            .GetRoleNamesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // Each designated role is resolved only when the portal actually designates one.
        string? administratorRoleName = portal.AdministratorRoleId is int administratorRoleId
            ? roleNames.GetValueOrDefault(administratorRoleId)
            : null;

        string? registeredRoleName = portal.RegisteredRoleId is int registeredRoleId
            ? roleNames.GetValueOrDefault(registeredRoleId)
            : null;

        // MIGRATION: the legacy view exposed an Email column on the portal result set that was in fact the
        // designated administrator's address; the Portals table has never carried one.
        string? administratorEmail = null;
        if (portal.AdministratorId is int administratorId)
        {
            User? administrator = await _users
                .GetAsync(portalId, administratorId, cancellationToken)
                .ConfigureAwait(false);
            administratorEmail = administrator?.Email;
        }

        int? superTabId = await _tabs.GetHostRootTabIdAsync(cancellationToken).ConfigureAwait(false);

        return PortalMappings.ToDetail(
            portal,
            users,
            pages,
            administratorRoleName,
            registeredRoleName,
            administratorEmail,
            superTabId,
            CurrentPortalAliasId());
    }

    /// <summary>
    /// Flushes one stage of tenant creation, reporting a lost race for a unique value as the conflict it
    /// is.
    /// </summary>
    /// <param name="alias">The host name being bound, so the refusal can name it.</param>
    /// <param name="administratorUsername">The account name being taken, so the refusal can name it.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>Success, or the conflict the store refused.</returns>
    /// <remarks>
    /// Tenant creation checks the alias and the account name before writing either, and neither check can
    /// close the window between itself and the flush: two requests naming the same alias both read "free",
    /// and the loser is refused by the unique index.
    /// </remarks>
    private async Task<Result> FlushCreationAsync(
        string alias,
        string administratorUsername,
        CancellationToken cancellationToken)
    {
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (DuplicateKeyException exception)
        {
            string? constraint = exception.ConstraintName;

            if (Names(constraint, "PortalAlias"))
            {
                return Result.Failure(
                    AliasDuplicateCode,
                    $"The host name '{alias}' is already bound to a portal.");
            }

            if (Names(constraint, "Users"))
            {
                return Result.Failure(
                    AdministratorDuplicateCode,
                    $"The account name '{administratorUsername}' is already in use, so the portal administrator could not be created.");
            }

            return Result.Failure(
                CreationConflictCode,
                "The portal could not be created because another request has just taken one of the values it "
                + "requires to be unique.");
        }

        static bool Names(string? constraintName, string table) =>
            constraintName is not null
            && constraintName.Contains(table, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the installation defaults a new portal inherits.</summary>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>The resolved defaults.</returns>
    /// <remarks>
    /// Reproduces the private two-argument <c>CreatePortal</c> exactly. A blank setting yields zero for the
    /// monetary and quota values, no expiry for a blank trial length, and an absent retention period where
    /// the legacy code used its -1 sentinel.
    /// </remarks>
    private async Task<PortalDefaults> ReadPortalDefaultsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> settings = await _hostSettings
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        int? demoPeriodDays = ReadInt(settings, DemoPeriodSetting);
        DateTime? expiryDate = demoPeriodDays is int trialDays
            ? _clock.UtcNow.AddDays(trialDays)
            : null;

        decimal hostFee = ReadDecimal(settings, HostFeeSetting) ?? 0m;
        int hostSpace = ReadInt(settings, HostSpaceSetting) ?? 0;
        int pageQuota = ReadInt(settings, PageQuotaSetting) ?? 0;
        int userQuota = ReadInt(settings, UserQuotaSetting) ?? 0;

        // The retention period is the one setting whose absence is NOT zero. The legacy code carried its -1
        // sentinel here, and -1 meant "keep for ever" rather than "keep for minus one day", so collapsing
        // it to zero would silently turn unlimited retention into none.
        int? siteLogHistory = ReadInt(settings, SiteLogHistorySetting);

        string? configuredCurrency = settings.GetValueOrDefault(HostCurrencySetting);
        string currency = string.IsNullOrWhiteSpace(configuredCurrency)
            ? FallbackCurrency
            : configuredCurrency;

        return new PortalDefaults(currency, expiryDate, hostFee, hostSpace, pageQuota, userQuota, siteLogHistory);
    }

    /// <summary>Builds one of the three stock roles a new portal requires.</summary>
    /// <param name="portal">
    /// The owning portal, linked by navigation so that its identifier resolves on commit.
    /// </param>
    /// <param name="roleName">The role name, preserved verbatim from the legacy creation path.</param>
    /// <param name="description">The role description, preserved verbatim.</param>
    /// <param name="isPublic">Whether members may subscribe to the role themselves.</param>
    /// <param name="autoAssignment">Whether new members receive the role automatically.</param>
    /// <returns>An unsaved role aggregate.</returns>
    private static Role BuildStockRole(
        Portal portal,
        string roleName,
        string description,
        bool isPublic,
        bool autoAssignment) => new()
        {
            Portal = portal,
            RoleName = roleName,
            Description = description,
            ServiceFee = PortalMappings.ClampFee(0m),
            BillingPeriod = 0,
            BillingFrequency = BillingFrequency.Month,
            TrialFee = PortalMappings.ClampFee(0m),
            TrialPeriod = 0,
            TrialFrequency = BillingFrequency.None,
            IsPublic = isPublic,
            AutoAssignment = autoAssignment,
        };

    /// <summary>
    /// Surrogate key of the alias the CURRENT REQUEST resolved through, or <see langword="null"/> when the
    /// request resolved no tenant.
    /// </summary>
    /// <returns>The resolved alias key, or <see langword="null"/> when no tenant was resolved.</returns>
    private int? CurrentPortalAliasId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalAliasId : null;

    /// <summary>Whether one alias is the alias the CURRENT REQUEST resolved through.</summary>
    /// <param name="portalAliasId">The alias key to test.</param>
    /// <returns><see langword="true"/> when it is the resolved alias.</returns>
    /// <remarks>
    /// Compared for EQUALITY against the resolved key, never by magnitude and never by truthiness.
    /// <c>PortalAlias.PortalAliasID</c> is <c>IDENTITY (1, 1)</c> so no legal key collides with the legacy
    /// absent-integer sentinel, but the discipline is applied anyway because every sibling key this service
    /// handles — portal, role, page and module — is seeded at zero or minus one and is compared by the same
    /// code paths.
    /// </remarks>
    private bool IsCurrentPortalAlias(int portalAliasId) =>
        CurrentPortalAliasId() is int resolved && resolved == portalAliasId;

    /// <summary>Resolves the host name a new portal will actually be reachable at.</summary>
    /// <param name="request">The submitted creation request, read for <c>IsChildPortal</c>.</param>
    /// <param name="submittedAlias">The trimmed value the caller submitted.</param>
    /// <returns>
    /// A success carrying the alias to store; a failure carrying <see cref="ParentAliasUnresolvedCode"/>
    /// when a child portal was asked for from a request that resolved to no tenant; or a failure carrying
    /// <see cref="ParentAliasTooDeepCode"/> when the resolved parent is itself addressed beneath a path
    /// segment.
    /// </returns>
    private Result<string> ComposeAlias(CreatePortalRequest request, string submittedAlias)
    {
        if (!request.IsChildPortal)
        {
            return Result<string>.Success(submittedAlias);
        }

        if (submittedAlias.Contains(AliasPathSeparator, StringComparison.Ordinal))
        {
            return Result<string>.Success(submittedAlias);
        }

        // The legacy PORTAL branch: a bare segment is composed beneath the addressed authority. Resolution
        // is memoised per request, so this does not re-read the store on a request whose tenant has already
        // been established.
        if (!_portalContext.IsResolved)
        {
            return Result<string>.Failure(
                ParentAliasUnresolvedCode,
                "A child portal is reached beneath its parent's host name, and this request did not "
                + "resolve to a parent portal. Submit the child's full host name, or address the "
                + "request to the parent portal it is to be created beneath.");
        }

        string parentAuthority = _portalContext.Current.PortalAlias.Trim().Trim(AliasPathSeparator);

        if (parentAuthority.Length == 0)
        {
            return Result<string>.Failure(
                ParentAliasUnresolvedCode,
                "The parent portal this request resolved to carries no host name, so a child portal's "
                + "address cannot be composed beneath it.");
        }

        // ⚠ THE PARENT MUST NOT ITSELF BE NESTED. The resolved authority already carries any path portion
        // the request arrived under, because resolution matches the longest addressable prefix, so
        // composing unconditionally would yield host/first/second - an address of a depth nothing in this
        // deployment can deliver.
        if (parentAuthority.Contains(AliasPathSeparator, StringComparison.Ordinal))
        {
            return Result<string>.Failure(
                ParentAliasTooDeepCode,
                "This request resolved to a portal that is itself addressed beneath a path segment, and "
                + "a child portal cannot be nested a second level deep. Submit the new portal's full "
                + "host name, or create it from a request addressed to a portal that is not itself a "
                + "child.");
        }

        return Result<string>.Success(
            string.Concat(parentAuthority, AliasPathSeparator.ToString(), submittedAlias));
    }

    /// <summary>
    /// Reports whether a whole-record portal write would overwrite an edit made after the caller read the
    /// record.
    /// </summary>
    /// <param name="portal">The portal as it currently stands.</param>
    /// <param name="request">The submitted state, carrying the token the caller read.</param>
    /// <returns>
    /// <see langword="true"/> when the caller supplied a token that no longer matches the stored record, so
    /// the write must be refused; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// A CALLER THAT SUPPLIES NO TOKEN IS NOT REFUSED. <see cref="ConcurrencyToken.Matches"/> treats a null
    /// or blank submitted token as a match, which is what keeps every caller written before the token
    /// existed working - see the member documentation on
    /// <c>IPortalSettingsUpdateRequest.ConcurrencyToken</c> for why that trade is made explicitly rather
    /// than silently.
    /// </remarks>
    private static bool IsWritingOverSomeoneElsesEdit(Portal portal, IPortalSettingsUpdateRequest request)
        => !ConcurrencyToken.Matches(
            request.ConcurrencyToken,
            PortalMappings.ConcurrencyTokenFor(portal));

    /// <summary>Composes the explanation a refused stale portal write reports.</summary>
    /// <param name="portalId">The portal the caller addressed.</param>
    /// <returns>The explanation.</returns>
    /// <remarks>
    /// Shared by both write paths so the two cannot drift into reporting the same condition differently,
    /// which is also why it says "reload the portal" rather than naming one of the two screens: runtime
    /// testing showed the same sentence reaching an operator on the portal EDIT form as well as on the Site
    /// Settings screen, and an instruction naming the wrong screen is worse than a general one.
    /// </remarks>
    private static string DescribeConcurrencyConflict(int portalId)
        => FormattableString.Invariant(
            $"Portal {portalId} was changed by someone else after you read it, so nothing was written. Reload the portal to see the current values, then apply your change again.");

    /// <summary>
    /// Refuses an update in which a caller who is not a host account has altered a host-only field.
    /// </summary>
    /// <param name="portal">The stored portal.</param>
    /// <param name="request">The submitted values.</param>
    /// <param name="cancellationToken">Abandons the authority read when the caller disconnects.</param>
    /// <returns>A task that completes when the request has been admitted.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when a non-host caller has altered the hosting charge, the disc-space quota, the page quota,
    /// the member quota, the site-log retention period or the expiry date.
    /// </exception>
    private async Task EnsureHostOnlyFieldsUnchangedAsync(
        Portal portal,
        IPortalSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerIsHostAccountAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        bool altered = (request.HostFee ?? 0m) != portal.HostFee
            || (request.HostSpace ?? 0) != portal.HostSpace
            || (request.PageQuota ?? 0) != portal.PageQuota
            || (request.UserQuota ?? 0) != portal.UserQuota
            || request.SiteLogHistory != portal.SiteLogHistory
            || request.ExpiryDate != portal.ExpiryDate;

        if (altered)
        {
            throw new UnauthorizedAccessException(
                "Only a host account may change the hosting charge, the quotas, the site-log retention period or the expiry date of a portal.");
        }
    }

    /// <summary>Refuses an update that would leave a portal with no designated administrator account.</summary>
    /// <param name="portal">The stored tenant being updated.</param>
    /// <param name="request">The submitted settings.</param>
    /// <exception cref="DomainException">
    /// The stored portal designates an administrator and the request would clear it.
    /// </exception>
    /// <remarks>
    /// An omitted administrator means "leave it as it is", which is why the guard tests the STORED value as
    /// well as the submitted one: a portal that already has none is not made worse by an update that
    /// supplies none, whereas clearing a designated administrator would leave the tenant with no account
    /// able to administer it and no route back other than a host-level repair.
    /// </remarks>
    private static void EnsureAdministratorRetained(Portal portal, IPortalSettingsUpdateRequest request)
    {
        if (portal.AdministratorId is null || request.AdministratorId is not null)
        {
            return;
        }

        throw new DomainException(
            "A portal must designate an administrator account, so an update may not clear it.");
    }

    /// <summary>
    /// Reports whether the caller is a host account according to the store, rather than according to the
    /// claim its token carries.
    /// </summary>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns><see langword="true"/> when the caller's stored account is a host account.</returns>
    /// <remarks>
    /// EVERY ARM OF THIS FAILS CLOSED. A caller whose account is absent from the store - deleted since
    /// sign-in, or a token minted for an identifier that never existed - is not a host account either,
    /// because a missing record cannot evidence authority.
    /// </remarks>
    private async Task<bool> CallerIsHostAccountAsync(CancellationToken cancellationToken)
    {
        if (!_currentUser.IsAuthenticated || _currentUser.UserId is not { } userId)
        {
            return false;
        }

        User? account = await _users
            .GetAsync(portalId: null, userId, cancellationToken)
            .ConfigureAwait(false);

        return account?.IsSuperUser == true;
    }

    /// <summary>
    /// Validates every account and page reference before an update can be mapped onto the tracked portal.
    /// </summary>
    /// <param name="portal">The stored portal, read before anything is applied.</param>
    /// <param name="request">The submitted update.</param>
    /// <param name="cancellationToken">Abandons the ownership reads when the caller disconnects.</param>
    /// <returns>A successful result when every reference belongs to the addressed portal.</returns>
    /// <remarks>
    /// Each page is tested through <see cref="IPortalRepository.TabBelongsToPortalAsync"/>, whose false
    /// result deliberately covers both absence and another tenant's row.
    /// </remarks>
    private async Task<Result> ValidateUpdateReferencesAsync(
        Portal portal,
        IPortalSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProcessorCredentialReference is null)
        {
            if (!string.IsNullOrEmpty(portal.ProcessorCredentialReference)
                && !IsManagedSecretReference(portal.ProcessorCredentialReference))
            {
                return Result.Failure(
                    ProcessorReferenceInvalidCode,
                    "The legacy processor credential must be cleared or replaced with a managed-secret reference before the portal can be updated.");
            }
        }
        else if (request.ProcessorCredentialReference.Length > 0
            && !IsManagedSecretReference(request.ProcessorCredentialReference))
        {
            return Result.Failure(
                ProcessorReferenceInvalidCode,
                "The processor credential reference must use secret:// followed by a managed-secret identifier.");
        }

        // ⚠ AN UNCHANGED STORED DESIGNATION IS GRANDFATHERED, AND WITHOUT THIS SIX READABLE PORTALS COULD NOT
        // BE SAVED AT ALL. The rule below asks whether the designated account holds a UserPortals row for the
        // addressed portal, and asking it of a value the caller is merely ECHOING BACK made a maintenance
        // dead end rather than a validation:
        //
        //   • Measured on this installation: PortalIDs 0 through 5 all designate AdministratorId 2, and no
        //     matching UserPortals row exists for any of them - membership the migrated data never carried.
        //     Opening Site Settings as host, changing nothing, and pressing Update was refused 400
        //     portal.administrator_invalid. So was PUTting the exact body the GET had just returned.
        //   • There was no submission that could have satisfied the rule short of altering data the caller
        //     never asked to touch, and the screen offered no repair affordance, so every one of those six
        //     tenants was unmaintainable through this API: no footer, no keyword, no page reference could be
        //     amended, because the whole request was refused on a field it did not change.
        //
        // So the rule is asked only of a designation that is actually being CHANGED. A submission equal to the
        // stored value authors nothing and is admitted on the strength of already being there; anything else -
        // including clearing it, which the branch below still refuses - must satisfy the rule exactly as
        // before. A NEW invalid reference therefore remains impossible to write, which is the property that
        // matters: this admits history, not new breakage.
        //
        // The same reasoning, and the same shape, as the unchanged-address grandfathering on
        // UserService.UpdateUserAsync. Recorded in MIGRATION_NOTES.md as a deliberate divergence, together
        // with the remediation route for an orphaned designation: designate an account that does hold
        // membership, or add the membership row.
        if (request.AdministratorId is int administratorId)
        {
            bool designationUnchanged = portal.AdministratorId == administratorId;

            if (!designationUnchanged)
            {
                UserPortal? membership = await _users
                    .GetMembershipAsync(portal.PortalId, administratorId, cancellationToken)
                    .ConfigureAwait(false);

                if (membership is null)
                {
                    return Result.Failure(
                        AdministratorReferenceInvalidCode,
                        "The designated administrator must be an account that belongs to the addressed portal.");
                }
            }
        }
        else if (portal.AdministratorId is not null)
        {
            return Result.Failure(
                AdministratorReferenceInvalidCode,
                "A portal must designate an administrator account that belongs to the addressed portal.");
        }

        (string Field, int? TabId)[] pageReferences =
        [
            (nameof(IPortalSettingsUpdateRequest.SplashTabId), request.SplashTabId),
            (nameof(IPortalSettingsUpdateRequest.HomeTabId), request.HomeTabId),
            (nameof(IPortalSettingsUpdateRequest.LoginTabId), request.LoginTabId),
            (nameof(IPortalSettingsUpdateRequest.UserTabId), request.UserTabId),
        ];

        foreach ((string field, int? tabId) in pageReferences)
        {
            if (tabId is int referencedTabId
                && !await _portals
                    .TabBelongsToPortalAsync(portal.PortalId, referencedTabId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Result.Failure(
                    TabReferenceInvalidCode,
                    FormattableString.Invariant(
                        $"{field} must identify a page that belongs to the addressed portal."));
            }
        }

        return Result.Success();
    }

    /// <summary>Reports whether a value satisfies the bounded managed-secret reference contract.</summary>
    /// <param name="reference">The non-empty reference to inspect.</param>
    /// <returns><see langword="true"/> only for a 50-character-or-shorter <c>secret://</c> reference.</returns>
    private static bool IsManagedSecretReference(string reference)
    {
        const string prefix = "secret://";
        if (reference.Length is < 10 or > 50
            || !reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> identifier = reference.AsSpan(prefix.Length);
        if (!char.IsAsciiLetterOrDigit(identifier[0]))
        {
            return false;
        }

        foreach (char character in identifier[1..])
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or '/' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Trims a submitted host name and refuses a blank one.</summary>
    /// <param name="httpAlias">The submitted host name.</param>
    /// <returns>The trimmed host name.</returns>
    /// <exception cref="DomainException">Thrown when the host name is absent or blank.</exception>
    private static string NormaliseAlias(string? httpAlias)
    {
        string trimmed = (httpAlias ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainException("A portal alias must carry a host name.");
        }

        // MIGRATION: LOWER CASE IS THE LEGACY RULE, AND IT WAS BEING LOST. Every path in
        // Library/Components/Portal/PortalAliasController.vb that touched an alias applied .ToLower -
        // AddPortalAlias at L31, UpdatePortalAliasInfo at L97, and both read paths at L52 and L76 - so a
        // DotNetNuke installation never held a mixed-case alias. Trimming alone let "WWW.Example.Test" and
        // "www.example.test" be stored as written; because an alias is the sole means by which an incoming
        // request resolves to a tenant, that is a tenant-resolution difference rather than a cosmetic one.
        // ToLowerInvariant, not ToLower: the current culture's casing rules would map a dotted capital I to
        // a dotless one under tr-TR, so the same submitted alias would canonicalise two different ways
        // depending on the server's locale.
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Reads a host setting as a whole number, treating a missing, blank or unparsable value as absent.
    /// </summary>
    /// <param name="settings">The host settings.</param>
    /// <param name="name">The setting name.</param>
    /// <returns>The parsed value, or <see langword="null"/> when the setting was not usable.</returns>
    private static int? ReadInt(IReadOnlyDictionary<string, string> settings, string name)
    {
        string? raw = settings.GetValueOrDefault(name);

        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;
    }

    /// <summary>
    /// Reads a host setting as a monetary amount, treating a missing, blank or unparsable value as absent.
    /// </summary>
    /// <param name="settings">The host settings.</param>
    /// <param name="name">The setting name.</param>
    /// <returns>The parsed value, or <see langword="null"/> when the setting was not usable.</returns>
    /// <remarks>
    /// Absence is returned as a nullable value for the reason given on its whole-number counterpart.
    /// Parsing is culture invariant so that a stored amount is not reinterpreted by the host's locale - a
    /// decimal separator read under the wrong culture would change the amount by orders of magnitude.
    /// </remarks>
    private static decimal? ReadDecimal(IReadOnlyDictionary<string, string> settings, string name)
    {
        string? raw = settings.GetValueOrDefault(name);

        return !string.IsNullOrWhiteSpace(raw)
            && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
                ? parsed
                : null;
    }

    /// <summary>
    /// The staged home page, together with whichever page-scope permission definitions the installation's
    /// catalogue failed to declare.
    /// </summary>
    /// <param name="Page">The staged page.</param>
    /// <param name="ViewDefinitionMissing">
    /// <see langword="true"/> when the catalogue declares no page-scope VIEW definition, so the page was
    /// staged without its two view grants.
    /// </param>
    /// <param name="EditDefinitionMissing">
    /// <see langword="true"/> when the catalogue declares no page-scope EDIT definition, so the page was
    /// staged without its administrator edit grant.
    /// </param>
    /// <remarks>
    /// The two flags exist so that an installation defect discovered mid-transaction can be REPORTED after
    /// the commit rather than from inside it. Returning the condition as data is what makes the eventual
    /// audit record a statement about durable state; see the remarks on <see cref="CreateHomePageAsync"/>
    /// for the failure this replaced.
    /// </remarks>
    private sealed record HomePageStage(Tab Page, bool ViewDefinitionMissing, bool EditDefinitionMissing);

    /// <summary>The installation defaults a new portal inherits from host configuration.</summary>
    /// <param name="Currency">Default currency code.</param>
    /// <param name="ExpiryDate">Default expiry instant, or <see langword="null"/> for no expiry.</param>
    /// <param name="HostFee">Default monthly hosting charge.</param>
    /// <param name="HostSpace">Default disc-space quota in whole megabytes.</param>
    /// <param name="PageQuota">Default page quota.</param>
    /// <param name="UserQuota">Default member quota.</param>
    /// <param name="SiteLogHistory">Default site-log retention in days, or <see langword="null"/> for none.</param>
    private sealed record PortalDefaults(
        string Currency,
        DateTime? ExpiryDate,
        decimal HostFee,
        int HostSpace,
        int PageQuota,
        int UserQuota,
        int? SiteLogHistory);

    /// <summary>Installs the nineteen profile property definitions every new tenant begins with.</summary>
    /// <param name="portalId">The tenant the definitions belong to.</param>
    /// <param name="token">Token observed while the definitions are staged.</param>
    /// <remarks>
    /// The view order is 3, 5, 7 and so on, and that is not an off-by-one. Renumbering them from 1 would
    /// change the order every profile screen renders them, so the sequence is preserved exactly.
    /// </remarks>
    private async Task CreateDefaultProfileDefinitionsAsync(int portalId, CancellationToken token)
    {
        // (category, name, whether the property is rendered by a chooser rather than a text box)
        (string Category, string Name, bool Chooser)[] defaults =
        [
            ("Name", "Prefix", false),
            ("Name", "FirstName", false),
            ("Name", "MiddleName", false),
            ("Name", "LastName", false),
            ("Name", "Suffix", false),
            ("Address", "Unit", false),
            ("Address", "Street", false),
            ("Address", "City", false),
            ("Address", "Region", true),
            ("Address", "Country", true),
            ("Address", "PostalCode", false),
            ("Contact Info", "Telephone", false),
            ("Contact Info", "Cell", false),
            ("Contact Info", "Fax", false),
            ("Contact Info", "Website", false),
            ("Contact Info", "IM", false),
            ("Preferences", "Biography", true),
            ("Preferences", "TimeZone", true),
            ("Preferences", "PreferredLocale", true),
        ];

        int viewOrder = 1;

        foreach ((string category, string name, bool chooser) in defaults)
        {
            // Incremented BEFORE it is assigned, exactly as the legacy helper did, which is what makes the
            // first view order 3 rather than 1.
            viewOrder += 2;

            await _profiles.AddDefinitionAsync(
                new ProfilePropertyDefinition
                {
                    PortalId = portalId,
                    PropertyCategory = category,
                    PropertyName = name,
                    DataType = UnresolvedProfileDataType,
                    DefaultValue = string.Empty,
                    ModuleDefinitionId = null,
                    IsRequired = false,
                    IsVisible = true,
                    Length = chooser ? 0 : DefaultProfilePropertyLength,
                    ViewOrder = viewOrder,
                },
                token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the new tenant's home page and grants it the permissions the legacy portal template granted.
    /// </summary>
    /// <param name="portal">The tenant the page belongs to.</param>
    /// <param name="administratorsRole">The tenant's administrators role, which receives the edit grant.</param>
    /// <param name="token">Token observed while the page and its grants are staged.</param>
    /// <returns>
    /// The staged page, whose identifier becomes the tenant's home page once it is committed, together with
    /// whichever permission definitions the catalogue failed to declare.
    /// </returns>
    /// <remarks>
    /// MIGRATION: THIS MEMBER NO LONGER EMITS AN AUDIT RECORD, AND THE CHANGE IS ABOUT DURABILITY RATHER
    /// THAN ABOUT WORDING. Everything it stages is inside the caller's open transaction, and a record
    /// written from here therefore described work that had not yet been committed and might never be: any
    /// later stage of the sequence can return, which disposes the scope without committing and makes the
    /// portal, its alias, its roles, its administrator and this page disappear - while the record asserting
    /// that a permission catalogue was incomplete for portal N stayed in the log, naming a tenant that does
    /// not exist.
    /// </remarks>
    private async Task<Result<HomePageStage>> CreateHomePageAsync(
        Portal portal,
        Role administratorsRole,
        CancellationToken token)
    {
        var homePage = new Tab
        {
            PortalId = portal.PortalId,
            TabName = HomePageName,
            Title = HomePageName,
            IsVisible = true,
            DisableLink = false,
            TabOrder = 1,
            Level = 0,
            ParentId = null,
            IsDeleted = false,
        };

        await _tabs.AddAsync(homePage, token).ConfigureAwait(false);

        // Each key is resolved against the page SCOPE, so the answer does not depend on any page row. Both
        // reads order by identifier inside the repository, so the definition selected here is the same one
        // the page-scoped reader would have selected on an installation where that reader worked at all.
        Permission? viewDefinition = await ResolvePageScopeDefinitionAsync(PermissionKey.VIEW, token)
            .ConfigureAwait(false);
        Permission? editDefinition = await ResolvePageScopeDefinitionAsync(PermissionKey.EDIT, token)
            .ConfigureAwait(false);

        if (viewDefinition is null || editDefinition is null)
        {
            // MIGRATION: AN INCOMPLETE PERMISSION CATALOGUE NO LONGER REFUSES THE CREATE, and the reason is
            // legacy parity rather than leniency.
        }

        if (viewDefinition is not null)
        {
            await GrantHomePagePermissionAsync(homePage, viewDefinition, AllUsersRoleId, token)
                .ConfigureAwait(false);
            await GrantHomePagePermissionAsync(homePage, viewDefinition, administratorsRole.RoleId, token)
                .ConfigureAwait(false);
        }

        if (editDefinition is not null)
        {
            await GrantHomePagePermissionAsync(homePage, editDefinition, administratorsRole.RoleId, token)
                .ConfigureAwait(false);
        }

        return Result<HomePageStage>.Success(
            new HomePageStage(homePage, viewDefinition is null, editDefinition is null));
    }

    /// <summary>Resolves one page-scope permission definition by the key it grants.</summary>
    /// <param name="permissionKey">The key whose definition is wanted.</param>
    /// <param name="token">Token observed while the catalogue is read.</param>
    /// <returns>The definition, or <see langword="null"/> when the catalogue does not declare the key.</returns>
    /// <remarks>
    /// The scope code and the key together are how the shipped catalogue is addressed, and the uniqueness
    /// rule on that table spans the scope code, the owning definition and the key - so one code-and-key
    /// pair may legitimately exist once per module definition.
    /// </remarks>
    private async Task<Permission?> ResolvePageScopeDefinitionAsync(
        PermissionKey permissionKey,
        CancellationToken token)
    {
        IReadOnlyList<Permission> definitions = await _permissions
            .GetByCodeAndKeyAsync(TabPermissionScopeCode, permissionKey, token)
            .ConfigureAwait(false);

        return definitions.Count == 0 ? null : definitions[0];
    }

    /// <summary>Stages one page permission grant against a definition the caller has already resolved.</summary>
    /// <param name="homePage">The page receiving the grant.</param>
    /// <param name="definition">The catalogue definition the grant references.</param>
    /// <param name="roleId">The role receiving it.</param>
    /// <param name="token">Token observed while the grant is staged.</param>
    /// <remarks>
    /// The page is bound by NAVIGATION rather than by identifier, because the page has no identifier until
    /// the commit that follows; the object graph resolves the foreign key for both rows in one write.
    /// </remarks>
    private async Task GrantHomePagePermissionAsync(
        Tab homePage,
        Permission definition,
        int roleId,
        CancellationToken token)
    {
        await _permissions.AddTabPermissionAsync(
            new TabPermission
            {
                Tab = homePage,
                PermissionId = definition.PermissionId,
                RoleId = roleId,
                AllowAccess = true,
            },
            token).ConfigureAwait(false);
    }
}
