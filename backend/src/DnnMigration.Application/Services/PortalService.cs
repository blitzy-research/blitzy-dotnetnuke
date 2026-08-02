using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
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
/// Creates, reads, modifies and removes portals, and manages the host names bound to them.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: absorbs <c>Library/Components/Portal/PortalController.vb</c> (1,632 lines) and the
/// business rules of <c>Website/admin/Portal/{Portals,SiteSettings,Signup,PortalAlias,EditPortalAlias}.ascx.vb</c>.
/// Three legacy shapes disappear here: the fifteen-argument <c>CreatePortal</c> (L980) becomes one
/// request object, the twenty-seven-argument <c>UpdatePortalInfo</c> (L1568) becomes another, and the
/// untyped list returned by <c>GetPortals</c> (L1263) becomes a typed page.
/// </para>
/// <para>
/// MIGRATION: the thirteen legacy portal cache sites are absorbed here. The legacy key shapes
/// <c>Portal{portalId}</c> and <c>PortalDictionary</c> and the twenty-minute base timeout multiplied by
/// the installation-wide performance setting are preserved so that cache behaviour remains auditable
/// against the original, and every mutation invalidates the portal explicitly rather than relying on
/// the legacy recursive sweep.
/// </para>
/// <para>
/// MIGRATION: the portal template is not deserialised. The legacy creation path built a tenant's pages,
/// modules, folders, portal-level settings and profile property definitions by parsing an XML template
/// from the file system, and the file-system subsystem, the skinning subsystem and the module installer
/// are all outside this migration. <see cref="CreatePortalRequest.TemplateFile"/> and
/// <see cref="CreatePortalRequest.IsChildPortal"/> are therefore accepted and recorded on the request
/// but drive no template parsing and no directory creation; the three stock roles the legacy template
/// path created when a template did not supply them are lifted out of that path and created
/// unconditionally, because a tenant without them is not usable. The omission is a deliberate,
/// documented behavioural difference.
/// </para>
/// <para>
/// This service reaches persistence only through repository abstractions - never a database context, a
/// query root or SQL text - and commits through the unit of work.
/// </para>
/// </remarks>
public sealed class PortalService : IPortalService
{
    /// <summary>Reason code reported when the requested page coordinates are unusable.</summary>
    private const string PagingInvalidCode = "portal.paging_invalid";

    /// <summary>Reason code reported when no portal carries the supplied identifier.</summary>
    private const string NotFoundCode = "portal.not_found";

    /// <summary>Reason code reported when a tenant could not be built.</summary>
    private const string CreationFailedCode = "portal.creation_failed";

    /// <summary>Reason code reported when the requested administrator account name is already taken.</summary>
    /// <remarks>
    /// This is deliberately a separate code from <see cref="CreationFailedCode"/> rather than a second use of
    /// it. The two outcomes are not the same kind of failure: a taken account name is a collision between the
    /// submitted request and existing state, which the caller can correct by choosing another name, whereas a
    /// creation failure means the write itself did not complete and there is nothing for the caller to
    /// correct. Sharing one code would force both onto one status and one problem type, so a client could
    /// neither branch on them nor tell a retryable failure from a request it must change. Every failure code
    /// in this solution also carries a reason token that the API layer classifies, and only this spelling
    /// classifies a name collision as the conflict it is.
    /// </remarks>
    private const string AdministratorDuplicateCode = "portal.administrator_duplicate";

    /// <summary>Reason code reported when a host name is already bound to a portal.</summary>
    private const string AliasDuplicateCode = "portal.alias_duplicate";

    /// <summary>Reason code reported when no alias carries the supplied identifier.</summary>
    private const string AliasNotFoundCode = "portal.alias_not_found";

    /// <summary>Reason code reported when removal is refused to keep one portal in the installation.</summary>
    private const string LastRemainingCode = "portal.last_remaining";

    /// <summary>
    /// Legacy cache key shape for a single portal, preserved verbatim from
    /// <c>DataCache.PortalCacheKey</c> (L47).
    /// </summary>
    private const string PortalCacheKeyFormat = "Portal{0}";

    /// <summary>
    /// Legacy base cache timeout in minutes for a single portal, preserved verbatim from
    /// <c>DataCache.PortalCacheTimeOut</c> (L48).
    /// </summary>
    private const int PortalCacheTimeOutMinutes = 20;

    /// <summary>Host setting naming the trial length, in days, granted to a new portal.</summary>
    private const string DemoPeriodSetting = "DemoPeriod";

    /// <summary>Host setting naming the default monthly hosting charge.</summary>
    private const string HostFeeSetting = "HostFee";

    /// <summary>Host setting naming the default disc-space quota, in whole megabytes.</summary>
    private const string HostSpaceSetting = "HostSpace";

    /// <summary>Host setting naming the default page quota.</summary>
    private const string PageQuotaSetting = "PageQuota";

    /// <summary>Host setting naming the default member quota.</summary>
    private const string UserQuotaSetting = "UserQuota";

    /// <summary>Host setting naming the default site-log retention, in days.</summary>
    private const string SiteLogHistorySetting = "SiteLogHistory";

    /// <summary>Host setting naming the default currency code.</summary>
    private const string HostCurrencySetting = "HostCurrency";

    /// <summary>Currency the legacy creation path fell back to when the host setting was blank.</summary>
    private const string FallbackCurrency = "USD";

    /// <summary>Name of the stock administrators role, preserved verbatim (L1390).</summary>
    private const string AdministratorsRoleName = "Administrators";

    /// <summary>Description of the stock administrators role, preserved verbatim (L1390).</summary>
    private const string AdministratorsRoleDescription = "Portal Administrators";

    /// <summary>Name of the stock registered-members role, preserved verbatim (L1393).</summary>
    private const string RegisteredUsersRoleName = "Registered Users";

    /// <summary>Description of the stock registered-members role, preserved verbatim (L1393).</summary>
    private const string RegisteredUsersRoleDescription = "Registered Users";

    /// <summary>Name of the stock subscribers role, preserved verbatim (L1396).</summary>
    private const string SubscribersRoleName = "Subscribers";

    /// <summary>Description of the stock subscribers role, preserved verbatim (L1396).</summary>
    private const string SubscribersRoleDescription = "A public role for portal subscriptions";

    private readonly IPortalRepository _portals;
    private readonly IPortalAliasRepository _aliases;
    private readonly ITabRepository _tabs;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IHostSettingsService _hostSettings;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;
    private readonly ICacheService _cache;
    private readonly ICurrentUser _currentUser;
    private readonly CachingOptions _caching;

    /// <summary>
    /// Initialises a new instance of the <see cref="PortalService"/> class.
    /// </summary>
    /// <param name="portals">Portal repository.</param>
    /// <param name="aliases">Portal alias repository.</param>
    /// <param name="tabs">Page repository, consulted for the page tally and the host root page.</param>
    /// <param name="users">Account repository, used for the administrator account and its credential.</param>
    /// <param name="roles">Role repository, used for the stock roles and their assignments.</param>
    /// <param name="unitOfWork">Commits each write exactly once.</param>
    /// <param name="hostSettings">Supplies the installation defaults a new portal inherits.</param>
    /// <param name="passwordHasher">Hashes the administrator's password before it is stored.</param>
    /// <param name="clock">Supplies the current instant, so time-dependent behaviour is testable.</param>
    /// <param name="cache">Absorbs the legacy portal cache.</param>
    /// <param name="currentUser">
    /// Identifies the caller, which the update path needs in order to enforce the host-only rule the
    /// legacy settings screen applied to the hosting and quota fields.
    /// </param>
    /// <param name="caching">
    /// Bound caching configuration, taken as a plain settings object because the application layer
    /// deliberately depends on no options package.
    /// </param>
    public PortalService(
        IPortalRepository portals,
        IPortalAliasRepository aliases,
        ITabRepository tabs,
        IUserRepository users,
        IRoleRepository roles,
        IUnitOfWork unitOfWork,
        IHostSettingsService hostSettings,
        IPasswordHasher passwordHasher,
        IClock clock,
        ICacheService cache,
        ICurrentUser currentUser,
        CachingOptions caching)
    {
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _hostSettings = hostSettings ?? throw new ArgumentNullException(nameof(hostSettings));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
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

        // Aliases are not loaded by the listing read, so the whole installation's aliases are fetched
        // once and grouped, rather than read per row.
        IReadOnlyList<PortalAlias> allAliases = await _aliases
            .ListAsync(null, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, List<string>> aliasesByPortal = allAliases
            .GroupBy(alias => alias.PortalId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(alias => alias.HttpAlias)
                              .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                              .ToList());

        var rows = new List<PortalListItemDto>(page.Items.Count);
        foreach (Portal portal in page.Items)
        {
            IReadOnlyList<string> aliases = aliasesByPortal.TryGetValue(portal.PortalId, out List<string>? bound)
                ? bound
                : Array.Empty<string>();

            // MIGRATION: the legacy listing read its member and page tallies from correlated
            // sub-selects inside the portal view, so they were computed per row there as well.
            int users = await _portals.CountUsersAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);
            int pages = await _portals.CountPagesAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);

            rows.Add(PortalMappings.ToListItem(portal, aliases, users, pages));
        }

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

        // Absence is an ordinary outcome of a lookup and is reported as a success carrying no value, so
        // that a caller can tell "there is no such tenant" from "the lookup could not be performed".
        return Result<PortalDetailDto?>.Success(detail);
    }

    /// <inheritdoc />
    public async Task<Result<PortalDetailDto>> CreatePortalAsync(
        CreatePortalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string alias = (request.PortalAlias ?? string.Empty).Trim();
        if (alias.Length == 0)
        {
            throw new DomainException("A portal alias is required in order to reach the new portal.");
        }

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

        // MIGRATION: the legacy screen rendered the literal placeholder "Portals/[PortalID]" in the home
        // directory box and deliberately did not submit it (Signup.ascx.vb L245-L246), so the column was
        // stored empty and the effective directory was derived at request time by the excluded
        // file-system layer. That is reproduced exactly: whatever the request carries is stored
        // verbatim, and an omitted directory is stored as the empty string the column defaults to.
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

        _portals.Add(portal);

        var portalAlias = new PortalAlias
        {
            Portal = portal,
            HttpAlias = alias,
        };
        _aliases.Add(portalAlias);

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

        _roles.Add(administratorsRole);
        _roles.Add(registeredUsersRole);
        _roles.Add(subscribersRole);

        User administrator = UserMappings.ToNewUser(new CreateUserRequest
        {
            Username = administratorUsername,
            FirstName = request.AdministratorFirstName ?? string.Empty,
            LastName = request.AdministratorLastName ?? string.Empty,
            DisplayName = string.Empty,
            Email = request.AdministratorEmail ?? string.Empty,
        });
        _users.Add(administrator);

        DateTime createdUtc = _clock.UtcNow;
        _users.AddMembership(new UserPortal
        {
            User = administrator,
            Portal = portal,
            CreatedDate = createdUtc,
            Authorised = true,
        });

        // MIGRATION: the legacy path assigned the new administrator to all three stock roles with an
        // absent effective date and an absent expiry date (L1399-L1401).
        foreach (Role role in new[] { administratorsRole, registeredUsersRole, subscribersRole })
        {
            _roles.AddAssignment(new UserRole
            {
                User = administrator,
                Role = role,
                EffectiveDate = null,
                ExpiryDate = null,
                IsTrialUsed = false,
            });
        }

        // The whole tenant graph - portal, alias, three roles, administrator, membership and three
        // assignments - is committed here in one transaction. Every foreign key inside the graph is
        // resolved by the object graph itself, so no store-assigned identifier is needed beforehand.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Three portal columns and the credential record cannot be written in the commit above, because
        // each needs an identifier the database only assigns during it and the portal aggregate carries
        // no reference navigation for an administrator or a role. The legacy path had the same shape: it
        // inserted the portal, created the administrator, created the roles, and only then called
        // UpdatePortalSetup to stamp the identifiers. Any failure here is compensated by removing
        // everything the first commit created, so a half-built tenant is never left behind.
        try
        {
            string passwordHash = _passwordHasher.Hash(password);
            bool credentialCreated = await _users
                .CreateCredentialAsync(administrator.UserId, passwordHash, isApproved: true, createdUtc, cancellationToken)
                .ConfigureAwait(false);

            if (!credentialCreated)
            {
                await CompensateFailedCreationAsync(
                    portal,
                    portalAlias,
                    administrator,
                    new[] { administratorsRole, registeredUsersRole, subscribersRole },
                    deleteCredential: false,
                    cancellationToken).ConfigureAwait(false);

                return Result<PortalDetailDto>.Failure(
                    CreationFailedCode,
                    "The portal administrator's credential could not be created, so the portal was rolled back.");
            }

            portal.AdministratorId = administrator.UserId;
            portal.AdministratorRoleId = administratorsRole.RoleId;
            portal.RegisteredRoleId = registeredUsersRole.RoleId;

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await CompensateFailedCreationAsync(
                portal,
                portalAlias,
                administrator,
                new[] { administratorsRole, registeredUsersRole, subscribersRole },
                deleteCredential: true,
                CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        _cache.InvalidateHost();
        _cache.InvalidatePortal(portal.PortalId);

        PortalDetailDto? created = await ReadDetailAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);
        if (created is null)
        {
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

        Portal? portal = await _portals
            .GetAsync(portalId, includeAliases: true, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result<PortalDetailDto?>.Success(null);
        }

        EnsureHostOnlyFieldsUnchanged(portal, request);

        EnsureAdministratorRetained(portal, request);

        PortalMappings.ApplyUpdate(portal, request);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);

        PortalDetailDto? detail = await ReadDetailAsync(portalId, cancellationToken).ConfigureAwait(false);
        return Result<PortalDetailDto?>.Success(detail);
    }

    /// <inheritdoc />
    public async Task<Result> DeletePortalAsync(int portalId, CancellationToken cancellationToken = default)
    {
        Portal? portal = await _portals
            .GetAsync(portalId, includeAliases: true, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result.Failure(NotFoundCode, $"No portal bears identifier {portalId}.");
        }

        // One page of size one is read purely for its total, because the repository exposes no bare
        // count of portals and an installation must retain at least one tenant.
        PagedResult<Portal> firstPage = await _portals
            .ListAsync(0, 1, null, null, false, cancellationToken)
            .ConfigureAwait(false);

        if (firstPage.TotalCount <= 1)
        {
            return Result.Failure(
                LastRemainingCode,
                "The installation must retain at least one portal, so the last remaining portal cannot be removed.");
        }

        // Aliases are loaded, so they are removed explicitly. Pages, modules, roles and memberships are
        // removed by the cascade configured on the portal's relationships: the repository contracts
        // expose no removal member for a page or a module, and expressing the sweep here would require
        // widening abstractions that are deliberately narrow.
        foreach (PortalAlias alias in portal.PortalAliases.ToList())
        {
            _aliases.Remove(alias);
        }

        _portals.Remove(portal);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateTabs(portalId);
        _cache.InvalidatePortal(portalId);
        _cache.InvalidateHost();

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<PortalSettingsDto?>> GetPortalSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        Portal? portal = await _portals
            .GetAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        return portal is null
            ? Result<PortalSettingsDto?>.Success(null)
            : Result<PortalSettingsDto?>.Success(PortalMappings.ToSettings(portal));
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

        IReadOnlyList<PortalAlias> aliases = await _aliases
            .ListAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PortalAliasDto> rows = aliases
            .Select(PortalMappings.ToDto)
            .ToList();

        return Result<IReadOnlyList<PortalAliasDto>>.Success(rows);
    }

    /// <inheritdoc />
    public async Task<Result<PortalAliasDto?>> GetPortalAliasAsync(
        int portalAliasId,
        CancellationToken cancellationToken = default)
    {
        PortalAlias? alias = await _aliases.GetAsync(portalAliasId, cancellationToken).ConfigureAwait(false);

        return alias is null
            ? Result<PortalAliasDto?>.Success(null)
            : Result<PortalAliasDto?>.Success(PortalMappings.ToDto(alias));
    }

    /// <inheritdoc />
    public async Task<Result<PortalAliasDto>> AddPortalAliasAsync(
        int portalId,
        CreatePortalAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // MIGRATION: the write takes a dedicated request rather than the read representation. The read
        // DTO carries the alias key and the portal key, both of which the caller would then be able to
        // set - the alias key is ignored on a create and the portal key is taken from the route, so a
        // caller supplying either would be silently overruled while believing it had been honoured. The
        // request carries the host name and nothing else, which is the only member a create writes.
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

        _aliases.Add(created);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Alias resolution is installation-wide rather than portal-scoped, so binding a host name
        // invalidates the host entries as well as the portal's own.
        _cache.InvalidateHost();
        _cache.InvalidatePortal(portalId);

        return Result<PortalAliasDto>.Success(PortalMappings.ToDto(created));
    }

    /// <inheritdoc />
    public async Task<Result> UpdatePortalAliasAsync(
        int portalAliasId,
        UpdatePortalAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // As with the create above, the request carries the host name only. That is what makes the
        // comment further down - that this member does not re-bind the tenant - true by construction
        // rather than by convention: there is no portal key on the wire to re-bind it to.
        string httpAlias = NormaliseAlias(request.HttpAlias);

        PortalAlias? stored = await _aliases.GetAsync(portalAliasId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Result.Failure(AliasNotFoundCode, $"No portal alias bears identifier {portalAliasId}.");
        }

        bool aliasTaken = await _aliases
            .AliasExistsAsync(httpAlias, portalAliasId, cancellationToken)
            .ConfigureAwait(false);
        if (aliasTaken)
        {
            return Result.Failure(
                AliasDuplicateCode,
                $"The host name '{httpAlias}' is already bound to another portal alias.");
        }

        // Only the host name is written. The portal an alias is bound to is deliberately not moved
        // here: this member declares no portal-not-found reason code, which is itself the statement
        // that it does not re-validate, and therefore does not re-bind, the tenant.
        stored.HttpAlias = httpAlias;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateHost();
        _cache.InvalidatePortal(stored.PortalId);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> DeletePortalAliasAsync(
        int portalAliasId,
        CancellationToken cancellationToken = default)
    {
        PortalAlias? stored = await _aliases.GetAsync(portalAliasId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Result.Failure(AliasNotFoundCode, $"No portal alias bears identifier {portalAliasId}.");
        }

        int owningPortalId = stored.PortalId;

        _aliases.Remove(stored);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateHost();
        _cache.InvalidatePortal(owningPortalId);

        return Result.Success();
    }

    /// <summary>
    /// Reads one portal and every derived value the detail contract carries.
    /// </summary>
    /// <param name="portalId">The portal to read.</param>
    /// <param name="cancellationToken">Token observed while the reads are in flight.</param>
    /// <returns>The detail projection, or <see langword="null"/> when no such portal exists.</returns>
    private async Task<PortalDetailDto?> ReadDetailAsync(int portalId, CancellationToken cancellationToken)
    {
        Portal? portal = await _portals
            .GetAsync(portalId, includeAliases: true, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return null;
        }

        int users = await _portals.CountUsersAsync(portalId, cancellationToken).ConfigureAwait(false);
        int pages = await _portals.CountPagesAsync(portalId, cancellationToken).ConfigureAwait(false);

        // MIGRATION: the two role names were correlated sub-selects in the legacy portal view
        // (04.05.00 line 1584), so they are read-only projections here rather than stored columns.
        IReadOnlyDictionary<int, string> roleNames = await _portals
            .GetRoleNamesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        string? administratorRoleName = portal.AdministratorRoleId is int administratorRoleId
            && roleNames.TryGetValue(administratorRoleId, out string? administratorName)
            ? administratorName
            : null;

        string? registeredRoleName = portal.RegisteredRoleId is int registeredRoleId
            && roleNames.TryGetValue(registeredRoleId, out string? registeredName)
            ? registeredName
            : null;

        // MIGRATION: the legacy view exposed an Email column on the portal result set that was in fact
        // the designated administrator's address; the Portals table has never carried one.
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
            superTabId);
    }

    /// <summary>
    /// Reads the installation defaults a new portal inherits.
    /// </summary>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>The resolved defaults.</returns>
    /// <remarks>
    /// MIGRATION: reproduces the private two-argument <c>CreatePortal</c> (L326-L377) exactly. A blank
    /// setting yields zero for the monetary and quota values, no expiry for a blank trial length, and
    /// an absent retention period where the legacy code used its -1 sentinel. A blank currency falls
    /// back to the same literal the legacy code used.
    /// </remarks>
    private async Task<PortalDefaults> ReadPortalDefaultsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> settings = await _hostSettings
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTime? expiryDate = null;
        if (TryReadInt(settings, DemoPeriodSetting, out int demoPeriodDays))
        {
            expiryDate = _clock.UtcNow.AddDays(demoPeriodDays);
        }

        decimal hostFee = TryReadDecimal(settings, HostFeeSetting, out decimal fee) ? fee : 0m;
        int hostSpace = TryReadInt(settings, HostSpaceSetting, out int space) ? space : 0;
        int pageQuota = TryReadInt(settings, PageQuotaSetting, out int pages) ? pages : 0;
        int userQuota = TryReadInt(settings, UserQuotaSetting, out int members) ? members : 0;
        int? siteLogHistory = TryReadInt(settings, SiteLogHistorySetting, out int retention) ? retention : null;

        string currency = settings.TryGetValue(HostCurrencySetting, out string? configured)
            && !string.IsNullOrWhiteSpace(configured)
                ? configured
                : FallbackCurrency;

        return new PortalDefaults(currency, expiryDate, hostFee, hostSpace, pageQuota, userQuota, siteLogHistory);
    }

    /// <summary>
    /// Builds one of the three stock roles a new portal requires.
    /// </summary>
    /// <param name="portal">The owning portal, linked by navigation so that its identifier resolves on commit.</param>
    /// <param name="roleName">The role name, preserved verbatim from the legacy creation path.</param>
    /// <param name="description">The role description, preserved verbatim.</param>
    /// <param name="isPublic">Whether members may subscribe to the role themselves.</param>
    /// <param name="autoAssignment">Whether new members receive the role automatically.</param>
    /// <returns>An unsaved role aggregate.</returns>
    /// <remarks>
    /// MIGRATION: the legacy path created each role through a private helper that clamped a negative
    /// fee to zero and passed a monthly billing frequency with a zero period and no trial (L1390,
    /// L1393, L1396). Those values are reproduced literally, and the shared clamp is reused so that
    /// the rule lives in exactly one place.
    /// </remarks>
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
    /// Removes everything a failed creation had already committed.
    /// </summary>
    /// <param name="portal">The portal to remove.</param>
    /// <param name="alias">The alias bound to it.</param>
    /// <param name="administrator">The administrator account created for it.</param>
    /// <param name="roles">The stock roles created for it.</param>
    /// <param name="deleteCredential">Whether a credential record may already exist and must be removed.</param>
    /// <param name="cancellationToken">Token observed while the compensating writes are in flight.</param>
    /// <remarks>
    /// Compensation is best-effort in the sense that it cannot itself be retried, but it is committed
    /// in one transaction so it either fully reverses the creation or leaves it untouched for an
    /// operator to inspect. It deliberately does not swallow its own failure: a compensation that
    /// cannot run is a genuinely unexpected condition and must surface.
    /// </remarks>
    private async Task CompensateFailedCreationAsync(
        Portal portal,
        PortalAlias alias,
        User administrator,
        IReadOnlyList<Role> roles,
        bool deleteCredential,
        CancellationToken cancellationToken)
    {
        if (deleteCredential)
        {
            await _users.DeleteCredentialAsync(administrator.UserId, cancellationToken).ConfigureAwait(false);
        }

        foreach (Role role in roles)
        {
            UserRole? assignment = await _roles
                .GetAssignmentAsync(role.RoleId, administrator.UserId, cancellationToken)
                .ConfigureAwait(false);
            if (assignment is not null)
            {
                _roles.RemoveAssignment(assignment);
            }
        }

        UserPortal? membership = await _users
            .GetMembershipAsync(portal.PortalId, administrator.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (membership is not null)
        {
            _users.RemoveMembership(membership);
        }

        foreach (Role role in roles)
        {
            _roles.Remove(role);
        }

        _users.Remove(administrator);
        _aliases.Remove(alias);
        _portals.Remove(portal);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateHost();
        _cache.InvalidatePortal(portal.PortalId);
    }

    /// <summary>
    /// Refuses an update in which a caller who is not a host account has altered a host-only field.
    /// </summary>
    /// <param name="portal">The stored portal.</param>
    /// <param name="request">The submitted values.</param>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when a non-host caller has altered the hosting charge, the disc-space quota, the page
    /// quota, the member quota, the site-log retention period or the expiry date.
    /// </exception>
    /// <remarks>
    /// MIGRATION: the legacy settings screen compared exactly these six submitted values against the
    /// stored portal and refused the whole save when a non-super-user had changed any of them
    /// (<c>Website/admin/Portal/SiteSettings.ascx.vb</c> L760-L770). It is an authorisation rule over
    /// the request's contents rather than over the route, so it cannot be expressed as a policy on the
    /// endpoint and lives here. It is reported by exception rather than by reason code because the
    /// member documents no failure code for it, and the API edge translates the exception into a single
    /// forbidden response.
    /// </remarks>
    /// <remarks>
    /// The four numeric terms are compared against the value the update is actually going to write, not
    /// against the submitted value alone. This request is a whole-row replacement, so an omitted numeric
    /// term is not "leave it alone": <see cref="PortalMappings.ApplyUpdate"/> substitutes zero for it,
    /// because the underlying columns cannot hold null. Testing <c>request.HostFee is decimal</c> and
    /// letting an omission through would therefore admit the exact change this rule exists to refuse — a
    /// tenant administrator could waive the hosting charge and lift every quota simply by leaving those
    /// fields out of the request. Comparing the effective value closes that, and costs a caller who is
    /// genuinely not changing them nothing, because echoing a value back compares equal.
    /// </remarks>
    private void EnsureHostOnlyFieldsUnchanged(Portal portal, UpdatePortalRequest request)
    {
        if (_currentUser.IsSuperUser)
        {
            return;
        }

        bool altered = PortalMappings.ClampFee(request.HostFee ?? 0m) != portal.HostFee
            || Math.Max(request.HostSpace ?? 0, 0) != portal.HostSpace
            || Math.Max(request.PageQuota ?? 0, 0) != portal.PageQuota
            || Math.Max(request.UserQuota ?? 0, 0) != portal.UserQuota
            || request.SiteLogHistory != portal.SiteLogHistory
            || request.ExpiryDate != portal.ExpiryDate;

        if (altered)
        {
            throw new UnauthorizedAccessException(
                "Only a host account may change the hosting charge, the quotas, the site-log retention period or the expiry date of a portal.");
        }
    }

    /// <summary>
    /// Refuses an update that would leave a portal designating no administrator account.
    /// </summary>
    /// <param name="portal">The stored portal, read before anything is applied.</param>
    /// <param name="request">The submitted update.</param>
    /// <exception cref="DomainException">
    /// Thrown when the portal currently designates an administrator and the update would clear it.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This guard exists because of what the surrounding contract makes possible rather than because the
    /// legacy application needed it. The request is a whole-row replacement - the sibling guard above
    /// explains that at length - so a caller that simply leaves <c>AdministratorId</c> out of the body is
    /// not saying "leave it alone", it is saying "set it to nothing", and
    /// <c>Portals.AdministratorId</c> is <c>int NULL</c>, so the database accepts that write without
    /// complaint.
    /// </para>
    /// <para>
    /// The consequence is out of all proportion to the omission. A portal that designates no
    /// administrator cannot be used: request-time tenant resolution reports the portal as incomplete and
    /// the portal-administrator authorisation policy then denies every request addressed to that tenant,
    /// including the requests that would put the value back. One well-formed update would therefore take
    /// a live tenant permanently out of service, and nothing in the write path would report a problem.
    /// </para>
    /// <para>
    /// Refusing the clear costs no legacy behaviour, which is why it is safe to add.
    /// <c>Website/admin/Portal/sitesettings.ascx</c> renders the administrator as a drop-down list of the
    /// portal's administrator accounts, so every save the legacy screen could produce carried a value and
    /// no legacy input reaches this refusal. It is recorded as an intentional strengthening.
    /// </para>
    /// <para>
    /// The test is deliberately the narrow one - a designated administrator being cleared - rather than
    /// "the effective value is null". A portal whose stored value is already absent stays editable, so an
    /// operator can repair such a row instead of finding it frozen, and an update that designates an
    /// administrator for the first time is exactly the repair. Existence and tenant ownership of the
    /// referenced account are not checked here: that is a repository question, and the column's foreign
    /// key settles existence authoritatively.
    /// </para>
    /// </remarks>
    private static void EnsureAdministratorRetained(Portal portal, UpdatePortalRequest request)
    {
        if (portal.AdministratorId is null || request.AdministratorId is not null)
        {
            return;
        }

        throw new DomainException(
            "A portal must designate an administrator account, so an update may not clear it.");
    }

    /// <summary>
    /// Trims a submitted host name and refuses a blank one.
    /// </summary>
    /// <param name="httpAlias">The submitted host name.</param>
    /// <returns>The trimmed host name.</returns>
    /// <exception cref="DomainException">Thrown when the host name is absent or blank.</exception>
    /// <remarks>
    /// Case is preserved, because the legacy screen stored the alias exactly as typed and the unique
    /// index on the column is what enforces distinctness. A blank host name is a shape violation rather
    /// than one of this member's documented outcomes, so it is reported as a domain exception, which
    /// the API edge renders as a bad request.
    /// </remarks>
    private static string NormaliseAlias(string? httpAlias)
    {
        string trimmed = (httpAlias ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainException("A portal alias must carry a host name.");
        }

        return trimmed;
    }

    /// <summary>
    /// Reads a host setting as a whole number, treating a blank or unparsable value as absent.
    /// </summary>
    /// <param name="settings">The host settings.</param>
    /// <param name="name">The setting name.</param>
    /// <param name="value">The parsed value when the setting was usable.</param>
    /// <returns><see langword="true"/> when the setting was present and parsable.</returns>
    private static bool TryReadInt(IReadOnlyDictionary<string, string> settings, string name, out int value)
    {
        value = 0;
        return settings.TryGetValue(name, out string? raw)
            && !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Reads a host setting as a monetary amount, treating a blank or unparsable value as absent.
    /// </summary>
    /// <param name="settings">The host settings.</param>
    /// <param name="name">The setting name.</param>
    /// <param name="value">The parsed value when the setting was usable.</param>
    /// <returns><see langword="true"/> when the setting was present and parsable.</returns>
    private static bool TryReadDecimal(IReadOnlyDictionary<string, string> settings, string name, out decimal value)
    {
        value = 0m;
        return settings.TryGetValue(name, out string? raw)
            && !string.IsNullOrWhiteSpace(raw)
            && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The installation defaults a new portal inherits from host configuration.
    /// </summary>
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
}
