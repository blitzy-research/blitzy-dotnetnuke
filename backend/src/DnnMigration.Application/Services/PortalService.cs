using System.Globalization;
using DnnMigration.Application.Abstractions;
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
/// MIGRATION: the legacy creation path ran <c>ParseTemplate</c> (<c>PortalController.vb:L1360</c>)
/// TWICE - once over the caller's chosen template at L1075 and once over a second, fixed template at
/// L1082 whose file name was the bare literal <c>"admin.template"</c>. That literal is load-bearing
/// rather than incidental: it names the template that provisions a tenant's administration pages, so
/// a misspelling would have produced a portal with no administration surface and no error. It is
/// preserved as the named constant <see cref="PortalOptions.AdminTemplateFileName"/>, whose default is
/// that exact string, so the value survives this migration in configuration even though, template
/// parsing being out of scope, nothing here reads it. Recording it rather than discarding it is what
/// lets the second pass be restored later without rediscovering the name from the legacy source.
/// </para>
/// <para>
/// MIGRATION: DEFECT 5, annotated and deliberately NOT fixed, per the migration discipline that a
/// defect discovered in the legacy source is recorded in place rather than corrected. Two faults sit
/// in <c>ParseTemplate</c>. First, <c>PortalController.vb:L1372-L1376</c> wraps the template load in
/// <c>Try xmlDoc.Load(TemplatePath &amp; TemplateFile)</c> followed by a <c>Catch</c> whose body is
/// EMPTY, so a missing or malformed template was swallowed without trace and parsing simply continued
/// against an empty document - a tenant was then created with none of the pages, modules or settings
/// the template described, and the caller was told the creation had succeeded. Second, L1364-L1366
/// seed <c>AdministratorRoleId</c>, <c>RegisteredRoleId</c> and <c>SubscriberRoleId</c> each to
/// <c>-1</c>, the legacy absent-Integer sentinel, which in this schema is also a real role identifier
/// - the sentinel and a legitimate key are indistinguishable. Neither fault is reproduced, because
/// this service does not parse templates at all; both are recorded so that a later agent restoring the
/// template pass does not reintroduce them, and so that the divergence is traceable rather than silent.
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
        // MIGRATION: the installation-wide read is its own member. The legacy code asked this question by
        // passing -1 to the portal-scoped read, where the procedure's own predicate turned it into a
        // wildcard; -1 is also a real portal identifier in this schema, so the two questions are now
        // separate members and the intent of this call site is visible without knowing that.
        IReadOnlyList<PortalAlias> allAliases = await _aliases
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: dbo.PortalAlias.HTTPAlias permits null - the column is declared without a NOT NULL
        // clause at Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider line 3807
        // - so the domain property is nullable and the sentinel is restored here, at the boundary that
        // publishes it. The legacy listing hydrated the column with
        // Convert.ToString(dr("HTTPAlias")).ToLower at
        // Library/Components/Portal/PortalAliasController.vb line 52, and Convert.ToString of DBNull
        // yields the empty string, so a row holding no host name appeared in the list as an empty entry
        // rather than being dropped. Preserving that keeps both the entry and the alias tally identical
        // to what the legacy screen showed, which is what Rule T7 asks of a DTO boundary.
        Dictionary<int, List<string>> aliasesByPortal = allAliases
            .GroupBy(alias => alias.PortalId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(alias => alias.HttpAlias ?? string.Empty)
                              .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                              .ToList());

        var rows = new List<PortalListItemDto>(page.Items.Count);
        foreach (Portal portal in page.Items)
        {
            IReadOnlyList<string> aliases = aliasesByPortal.GetValueOrDefault(portal.PortalId)
                ?? (IReadOnlyList<string>)Array.Empty<string>();

            // MIGRATION: the legacy listing read its member and page tallies from correlated
            // sub-selects inside the portal view, so they were computed per row there as well.
            int users = await _portals.CountUsersAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);
            int pages = await _portals.CountPagesAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);

            rows.Add(PortalMappings.ToListItem(portal, aliases, users, pages));
        }

        // MIGRATION: "return everything" is expressed by a NAMED FACTORY, not by a negative page index.
        // GetPortalsByName (PortalController.vb:L262) declared that intent by passing pageIndex = -1, then
        // rewrote its own arguments to page 0 with a page size of Integer.MaxValue once it had detected the
        // sentinel. Two things were wrong with that and both are closed here. The sentinel was
        // indistinguishable from a caller's arithmetic error, so a page index that had underflowed to -1
        // silently returned the whole table instead of failing; and it collided with this schema's use of
        // -1 as a real identifier. A page size of zero selects the unpaged factory explicitly, and the
        // guard at the top of this member REJECTS a negative page index outright with
        // portal.paging_invalid rather than reinterpreting it. The paged factory is given the store's own
        // coordinates so that the envelope reports what was actually read.
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
        // MIGRATION: the CALLER computes the lifetime, because ICacheService takes a TimeSpan and holds no
        // policy of its own. The arithmetic is the legacy arithmetic:
        // "DataCache.PortalCacheTimeOut * Convert.ToInt32(Globals.PerformanceSetting)" at
        // PortalController.vb:L218, with the twenty-minute base preserved as a named constant above and
        // the installation-wide multiplier now bound configuration rather than a static read of the
        // excluded globals module. Its default is 3, which is the legacy default, so an installation that
        // configures nothing caches for the same sixty minutes it always did.
        // MIGRATION: a multiplier of ZERO DISABLES CACHING, and that is load-bearing rather than an edge
        // case - it is how the legacy installation turned caching off, and an operator diagnosing a stale
        // read still relies on it. Multiplying yields a zero lifetime, and handing a zero lifetime to a
        // cache would ask it to store an entry that has already expired, whose behaviour is the cache's
        // business and not something this service should depend on. The guard below therefore bypasses the
        // cache entirely and reads through, which is unambiguous. Any negative multiplier that reached
        // here would be bypassed by the same test rather than producing a negative lifetime.
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

        // MIGRATION: host names are matched EXACTLY here, where the legacy installation matched them as
        // substrings. The tenant-resolution procedure was created as
        // "where PortalAlias like '%' + @PortalAlias + '%'" at
        // Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569-L4600, so a
        // request for "example.com" also matched a stored "test.example.com.au" and the procedure then
        // took min(PortalID) among the matches. Two consequences followed: an alias could be accepted as
        // free while colliding with an existing tenant under the legacy predicate, and a live request
        // could be resolved to the WRONG TENANT purely because one host name was a substring of another -
        // a cross-tenant data-exposure hazard in a multi-tenant product. The exact match closes both.
        // This is a deliberate behavioural difference and not an optimisation: an installation that
        // relied on substring resolution will resolve differently. Request-time resolution itself lives
        // in Api/Middleware/PortalAliasResolutionMiddleware; this call is the write-side counterpart,
        // and both use exact matching so that the check and the later lookup cannot disagree.
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

        // MIGRATION: the legacy path learned why administrator creation had failed from an enum returned
        // by value - "Dim createStatus As UserCreateStatus = UserController.CreateUser(objAdminUser)" at
        // PortalController.vb:L1013 - and tested it against a named member at L1015 before converting it
        // to display text at L1018. That enum does not cross this boundary. Its eighteen members become
        // distinct, stable failure codes on the Result, which is the canonical replacement for a status
        // channel and lets the API edge classify a collision separately from a failed write.
        // MIGRATION: the enum's shape is the reason this matters. UserCreateStatus declares eighteen
        // members numbered 0 to 17 and its Success member is 13, NOT 0, so the usual "zero means
        // success" reflex is exactly wrong: member 0 is a FAILURE. Testing a numeric zero for success
        // would have inverted the outcome and reported every failed creation as a success. No numeric
        // comparison is carried forward - success is the absence of a failure code - which removes the
        // trap rather than documenting a way to live with it.
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
        // here in the order they ran so the omission is auditable against the original. From
        // PortalController.vb: L1030-L1034 deleted a pre-existing upload folder, reporting failure through
        // the resource keyed DeleteUploadFolder.Error, "Error deleting previous upload folder";
        // L1038-L1053 configured a child portal on disc, reporting ChildPortal.Error, "Error configuring
        // Child Portal"; L1058-L1067 created the home directory and copied the tenant's resource file;
        // L1075 and L1082 parsed the portal and administration templates, reporting PortalTemplate.Error,
        // "Error parsing Portal Template", and AdminTemplate.Error, "Error parsing Admin Template"; and
        // L1087-L1102 copied the default page template and synchronised the folder tree. The file-system
        // subsystem, the skinning subsystem and the module installer are all outside this migration, so
        // none of these has a counterpart and none of those five failure codes is reachable. The
        // consequence is stated plainly rather than implied: a portal created here has its DATABASE rows
        // complete but NO DIRECTORIES on disc, and the pages, modules and folder permissions the template
        // would have supplied are absent - except the three stock roles, which are lifted out of the
        // template path below precisely because a tenant without them cannot be administered.
        // MIGRATION: two further legacy members that touched this lifecycle are not ported at all, because
        // the scheduling subsystem is excluded: DeleteExpiredPortals (L156), which swept expired tenants,
        // and UpdatePortalExpiry (L1495), which advanced the expiry date. The ExpiryDate COLUMN survives
        // and is still read and written, so no data is lost and an operator can still see and set it -
        // only the unattended sweep is gone. Restoring it would mean a hosted background service rather
        // than a ported scheduler client, and it is recorded so the absence is a decision, not a gap.

        // MIGRATION: the legacy screen rendered the literal placeholder "Portals/[PortalID]" in the home
        // directory box and deliberately did not submit it (Signup.ascx.vb L245-L246), so the column was
        // stored empty and the effective directory was derived at request time by the excluded
        // file-system layer. That is reproduced exactly: whatever the request carries is stored
        // verbatim, and an omitted directory is stored as the empty string the column defaults to.
        // MIGRATION: the legacy path did contain a defaulting step - "If HomeDirectory = "" Then
        // HomeDirectory = "Portals/" + intPortalId.ToString" at PortalController.vb:L991-L993 - and it is
        // deliberately NOT reproduced, because reading the surrounding code shows the defaulted value
        // never reached the stored column. It assigned a LOCAL parameter, and that local was consumed in
        // exactly two places: the mapped directory computed at L994, which belongs to the excluded
        // file-system layer, and one property of the audit entry at L1151. The write-back that followed
        // took its value from elsewhere - the twenty-seven-argument update at L1114-L1118 passes
        // objportal.HomeDirectory, a field of the record READ BACK from the store at L1109, not the local
        // - so the column kept whatever the insert had put there. Defaulting it here would therefore
        // introduce a persisted value the legacy installation never held, which is why the apparent
        // omission is in fact the faithful behaviour.
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

        // Staged, not committed: IRoleRepository.AddAsync records the insertion and the single
        // SaveChanges below closes the transaction over all five tables at once, which is what keeps
        // the legacy multi-table creation sequence atomic.
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

        // MIGRATION: the instant comes from the injected clock, which is UTC-ONLY, where every legacy
        // reading came from VB's Now() and was therefore in the SERVER'S LOCAL zone. The two differ by
        // the host's offset, so a value stored here can fall on a different calendar day from the one
        // the legacy code would have stored for the same real instant - west of UTC it can appear a day
        // later, east of it a day earlier. That is accepted deliberately: a local-zone timestamp is not
        // comparable across hosts and cannot be interpreted without knowing the machine that wrote it,
        // whereas UTC is unambiguous. The offset is also what makes time-dependent behaviour testable at
        // all, since the clock can be substituted. Recorded because it is observable in stored data, not
        // merely internal.
        DateTime createdUtc = _clock.UtcNow;
        _users.AddMembership(new UserPortal
        {
            User = administrator,
            Portal = portal,
            CreatedDate = createdUtc,
            IsAuthorised = true,
        });

        // MIGRATION: the legacy path assigned the new administrator to all three stock roles with an
        // absent effective date and an absent expiry date (L1399-L1401).
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
            // MIGRATION: the administrator's password is HASHED here. The legacy path assigned it in
            // CLEARTEXT - "objAdminUser.Membership.Password = Password" at PortalController.vb:L1005 -
            // and the membership provider that received it was registered with
            // passwordFormat="Encrypted" and enablePasswordRetrieval="true"
            // (Website/release.config:L236-L246), a REVERSIBLE scheme whose 3DES decryption key was
            // itself committed to source control at Website/release.config:L89-L93. Anyone holding the
            // repository and the database could therefore recover every password in plaintext.
            // The replacement is a one-way hash through IPasswordHasher, so the stored value cannot be
            // reversed even by this application. Two behavioural consequences are accepted and recorded:
            // password RETRIEVAL is not carried forward to any endpoint or screen, because a one-way
            // hash cannot support it, and a forgotten password is answered by reset rather than by
            // recovery. The cleartext value is never logged, never returned and never stored - only the
            // hash reaches the store - and this is the single point in this service that touches it.
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

        // Reproduces DataCache.ClearHostCache(True) at PortalController.vb:L1128, which discarded the
        // installation-wide entries so the new tenant became reachable, plus the portal's own entry.
        _cache.InvalidateHost();
        _cache.InvalidatePortal(portal.PortalId);

        // MIGRATION: the legacy path had a THIRD invalidation here that is not reproduced, and it is
        // recorded rather than dropped in silence. PortalController.vb:L1131 is
        // DataCache.RemoveCache("GetRoles"), evicting a single entry keyed by that bare literal, because
        // creating a portal had just inserted the three stock roles below and the role cache would
        // otherwise have served a list that predated them. ICacheService exposes twelve members and none
        // of them evicts roles: there is no InvalidateRoles, and the generic Remove(key) member cannot be
        // used correctly from here because the key is composed inside the infrastructure layer and this
        // layer has no constant for it. Passing the bare legacy string would be a guess that fails
        // silently if the two spellings ever diverge - worse than the omission, because it would look
        // like the concern was handled. Extending ICacheService is not available either: that contract
        // belongs to another file and is outside this file's scope.
        // MIGRATION: the omission is bounded rather than open-ended. The roles created here are the three
        // stock roles of a BRAND-NEW portal, so no reader can hold a cached role list for a tenant that
        // did not exist a moment ago; the stale-read window the legacy eviction guarded is empty at this
        // point in the lifecycle. The two invalidations above additionally discard the host-wide and
        // portal-scoped entries. Recorded so that a later change to ICacheService can close it explicitly.
        PortalDetailDto? created = await ReadDetailAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);
        if (created is null)
        {
            return Result<PortalDetailDto>.Failure(
                CreationFailedCode,
                "The portal was created but could not be read back.");
        }

        // MIGRATION: the legacy path closed with an AUDIT ENTRY that this service does not emit, and the
        // reason is a layering constraint rather than an oversight, so it is recorded in full here.
        // PortalController.vb:L1137-L1160 built a Services.Log.EventLog.LogInfo with BypassBuffering set
        // to True - the entry was written through immediately rather than batched, because a failed
        // installation had to leave a trace - typed it as EventLogType.HOST_ALERT, attached FOURTEEN
        // properties and submitted it through EventLogController.AddLog. The fourteen, verbatim and in
        // order, at L1142-L1155: "Install Portal:" carrying the portal name, then FirstName, LastName,
        // Username, Email, Description, "Keywords:" (spelt with a lower-case w in the label although the
        // argument it read was KeyWords), TemplatePath, TemplateFile, HomeDirectory, PortalAlias,
        // ServerPath, ChildPath and IsChildPortal. Note what is absent: the password argument was NOT
        // among them, so the legacy code already declined to record the credential and this migration
        // preserves that rather than newly imposing it.
        // MIGRATION: this layer cannot emit it. The audit sites become structured log events, but
        // DnnMigration.Application declares exactly two packages - FluentValidation and its dependency
        // injection extensions - because AAP 0.6.1 states "Application declares only FluentValidation",
        // and Microsoft.Extensions.Logging.Abstractions is neither among them nor present in the
        // reference pack a class library targets. Naming ILogger<T> in this project therefore fails to
        // compile with CS0234 and CS0246; that was VERIFIED by compiling it, not assumed, exactly as the
        // project file records for an earlier attempt to import the options package. AAP 0.6.1 assigns
        // Serilog to the Api layer and AAP 0.9.6 places structured logging in Program.cs and
        // Api/Middleware/RequestLoggingMiddleware.cs, so the owning layer is above this one. Adding the
        // package here would breach the frozen inventory and edit a file outside this file's scope; the
        // correct action is to record the gap, which is what this comment does. The successful outcome
        // returned below carries the created portal's identifier, which is the value the request-scoped
        // logging middleware needs in order to record the installation with these property names.
        // MIGRATION: DEFECT 4, annotated and deliberately NOT fixed. L1140 and L1141 are BYTE-IDENTICAL
        // consecutive statements, both assigning
        // "objEventLogInfo.LogTypeKey = ...EventLogType.HOST_ALERT.ToString". The second assignment is
        // pure redundancy - it overwrites the first with the same value, so the behaviour is unaffected -
        // but it is almost certainly a copy-and-paste survivor of a line that was meant to set a
        // different property, which is why it is recorded rather than quietly tidied away.
        // MIGRATION: DEFECT 6, annotated and deliberately NOT fixed. The whole audit block is wrapped in
        // "Catch ex As Exception" / "' error" / "End Try" at L1158-L1160 - an EMPTY handler. A logging
        // failure was therefore swallowed, so the one record proving a portal had been installed could go
        // missing while the caller was told the installation had succeeded. Reproducing an empty handler
        // would violate the enterprise baseline this migration is held to, so it is not reproduced: this
        // service surfaces failures as a Result or lets them propagate, and never discards one. That is a
        // documented divergence from the legacy behaviour rather than a silent correction of it.
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
            .GetByIdAsync(portalId, includeAliases: true, cancellationToken)
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
            .GetByIdAsync(portalId, includeAliases: true, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result.Failure(NotFoundCode, $"No portal bears identifier {portalId}.");
        }

        // One page of size one is read purely for its total, because the repository exposes no bare
        // count of portals and an installation must retain at least one tenant.
        // MIGRATION: the tally is read and judged INSIDE this service, exactly as the legacy body did -
        // "Dim portalCount As Integer = DataProvider.Instance.GetPortalCount()" followed by
        // "If portalCount > 1 Then" at PortalController.vb:L162. No member of this service reports a
        // portal count to a caller, deliberately: exposing one would let the decision be taken in a
        // controller, which is the layering violation Rule T2 exists to prevent, and would let two
        // callers disagree about the threshold.
        PagedResult<Portal> firstPage = await _portals
            .ListAsync(0, 1, null, null, false, cancellationToken)
            .ConfigureAwait(false);

        if (firstPage.TotalCount <= 1)
        {
            // MIGRATION: the wording is preserved verbatim from the legacy resource the screen displayed,
            // Website/App_GlobalResources/SharedResources.resx keyed LastPortal.Text - "You Can Not
            // Delete The Last Portal In Your Database" - because the migration discipline requires error
            // messages to remain equivalent to the ones existing operators already recognise. The legacy
            // member reported this by RETURNING that string, with an empty string meaning success, so a
            // caller had to compare text to learn whether the delete had happened; here the outcome is a
            // failure Result whose stable code is what a caller branches on and whose message is what a
            // human reads.
            return Result.Failure(
                LastRemainingCode,
                "You Can Not Delete The Last Portal In Your Database. The installation must retain at least one portal.");
        }

        // MIGRATION: four FILE-SYSTEM removal stages of the legacy delete are not reproduced, because the
        // file-system subsystem is outside this migration. In order, from PortalController.vb:L162:
        // DeleteFilesRecursive over the server path matching ".Portal-<id>.resx" (the tenant's localised
        // resource overrides); a sweep that read the tenant's aliases, reduced each to a domain name and
        // deleted the matching child-portal directory; DeleteFolderRecursive over "Portals\<id>"; and,
        // when it existed, DeleteFolderRecursive over the tenant's mapped home directory. The database
        // rows are removed in full, so no tenant remains addressable, but ORPHANED DIRECTORIES AND
        // RESOURCE FILES ARE LEFT ON DISC for an operator to reclaim. That is a deliberate, documented
        // behavioural difference and the omission is recorded rather than absorbed.
        // MIGRATION: those legacy stages reached their paths through the VB runtime's string intrinsics -
        // InStr, Mid and InStrRev - which arrived without an Imports statement via the project-level
        // imports at Library/DotNetNuke.Library.vbproj:L107-L134. None is transliterated: the BCL
        // equivalents are IndexOf, Substring and LastIndexOf, and they differ in a way that would matter
        // if this path were ever restored, because the VB intrinsics are ONE-BASED and return 0 for "not
        // found" whereas the BCL members are ZERO-BASED and return -1.
        // MIGRATION: DeletePortalInfo (L1191) also began with four SkinController.SetSkin calls, resetting
        // the tenant's page and container skins. Skinning is out of scope - this generation of the product
        // uses skins rather than master pages and none of it is ported - so those calls have no counterpart.
        // Aliases are loaded, so they are removed explicitly. Pages, modules, roles and memberships are
        // removed by the cascade configured on the portal's relationships: the repository contracts
        // expose no removal member for a page or a module, and expressing the sweep here would require
        // widening abstractions that are deliberately narrow.
        foreach (PortalAlias alias in portal.PortalAliases.ToList())
        {
            // Identified by key, as the legacy procedure was. These rows are already loaded, so the
            // repository resolves them from the change tracker rather than re-reading them.
            await _aliases.DeleteAsync(alias.PortalAliasId, cancellationToken).ConfigureAwait(false);
        }

        await _portals.DeleteAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);

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
        // MIGRATION: this read is deliberately NOT CACHED, and the omission is recorded rather than left
        // to be noticed. Every value it returns is projected from columns of the portal row, so the legacy
        // Portal{id} entry would have served it - but this member exists to back the settings screen, whose
        // whole purpose is to show an administrator what is currently stored immediately after they have
        // changed it. Serving that from a cache with a sixty-minute lifetime would show a stale form and
        // invite the administrator to save the old values back over their own edit. The listing and detail
        // reads above are cached because a slightly stale list is harmless; an editing form is not. The
        // same reasoning applies to the alias reads, which back an editing screen too.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: there is NO portal-setting entity, and this projection is where that shows. A reader
        // meeting a "portal settings" contract would reasonably expect a key-value table behind it, and
        // there is none: no such member appears among the abstract data provider's methods, no such
        // procedure appears among the ones the provider invoked, and no such table appears in any of the
        // schema scripts - which define ModuleSettings, HostSettings, TabModuleSettings and
        // ScheduleItemSettings, but nothing portal-scoped. The legacy PortalSettings CLASS that lent the
        // name was not persisted at all; PortalController.vb:L1209-L1210 returns it from the ambient
        // per-request store, so it was a request-lifetime composite assembled from the portal row and
        // discarded at the end of the request. Portal configuration therefore lives as COLUMNS on the
        // portal, this DTO projects those columns, and the request-scoped half of the legacy class is
        // served by the scoped portal context instead. Nothing here reads or writes a settings table.
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

        // The optional scope is resolved here rather than pushed into the repository as a nullable
        // filter: asking for one portal's aliases and asking for every alias in the installation are
        // different questions, and each has its own member.
        IReadOnlyList<PortalAlias> aliases = portalId is int wantedPortalId
            ? await _aliases.GetByPortalIdAsync(wantedPortalId, cancellationToken).ConfigureAwait(false)
            : await _aliases.GetAllAsync(cancellationToken).ConfigureAwait(false);

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
        PortalAlias? alias = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);

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

        // Staged, then committed by the unit of work. Only after the commit does created.PortalAliasId
        // hold the generated key, which is what the mapping below reads.
        await _aliases.AddAsync(created, cancellationToken).ConfigureAwait(false);
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

        PortalAlias? stored = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);
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

        // MIGRATION: the explicit counterpart of legacy UpdatePortalAliasInfo, which was the real per-row
        // update. Stated rather than left to the change tracker so that the intention to write this row is
        // visible at the call site, and so the same code is correct for an alias that was not read here.
        await _aliases.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);

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
        PortalAlias? stored = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Result.Failure(AliasNotFoundCode, $"No portal alias bears identifier {portalAliasId}.");
        }

        int owningPortalId = stored.PortalId;

        await _aliases.DeleteAsync(portalAliasId, cancellationToken).ConfigureAwait(false);
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
            .GetByIdAsync(portalId, includeAliases: true, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return null;
        }

        // MIGRATION: both tallies are AWAITED EXPLICITLY here because they were LAZY PROPERTY GETTERS on
        // the legacy record - Users at PortalInfo.vb:L309 and Pages at L320 - each of which issued a
        // synchronous database read the first time it was touched, memoised the answer in a backing field,
        // and used -1 in that field to mean "not loaded yet". Three problems came with that shape and all
        // three are removed rather than carried. Reading a property performed hidden I/O, so a caller
        // could not tell a field access from a query and a template that touched it in a loop issued one
        // query per iteration. The I/O was synchronous, which Rule T6 forbids anywhere in the request
        // path. And the not-loaded marker was -1, which in this schema is also a legitimate identifier, so
        // the sentinel was ambiguous. The domain entity therefore carries neither property: both are
        // computed once, here, where the awaiting is visible and cancellable, and travel onward as plain
        // values on the projection.
        int users = await _portals.CountUsersAsync(portalId, cancellationToken).ConfigureAwait(false);
        int pages = await _portals.CountPagesAsync(portalId, cancellationToken).ConfigureAwait(false);

        // MIGRATION: the two role names were correlated sub-selects in the legacy portal view
        // (04.05.00 line 1584), so they are read-only projections here rather than stored columns.
        IReadOnlyDictionary<int, string> roleNames = await _portals
            .GetRoleNamesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // Each designated role is resolved only when the portal actually designates one. The test is for
        // a value being present, never for a particular number: Roles.RoleID is declared IDENTITY (0, 1),
        // so zero is the first role an installation creates and -1 is a legitimate key as well, which
        // means no numeric comparison can stand in for absence here.
        string? administratorRoleName = portal.AdministratorRoleId is int administratorRoleId
            ? roleNames.GetValueOrDefault(administratorRoleId)
            : null;

        string? registeredRoleName = portal.RegisteredRoleId is int registeredRoleId
            ? roleNames.GetValueOrDefault(registeredRoleId)
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
    /// <para>
    /// MIGRATION: reproduces the private two-argument <c>CreatePortal</c> (L326-L377) exactly. A blank
    /// setting yields zero for the monetary and quota values, no expiry for a blank trial length, and
    /// an absent retention period where the legacy code used its -1 sentinel. A blank currency falls
    /// back to the same literal the legacy code used. Seven host settings are read, and the legacy
    /// default for each is preserved: <c>DemoPeriod</c> yields no expiry, <c>HostFee</c>,
    /// <c>HostSpace</c>, <c>PageQuota</c> and <c>UserQuota</c> each yield zero, <c>SiteLogHistory</c>
    /// yields absent, and <c>HostCurrency</c> yields <c>"USD"</c>.
    /// </para>
    /// <para>
    /// MIGRATION: the GUARD IS INVERTED on all seven reads, and the inversion is what makes them
    /// behave identically rather than differently. Every legacy guard tested
    /// <c>If Convert.ToString(HostSettings(key)) &lt;&gt; ""</c>, comparing against the EMPTY STRING,
    /// because the legacy null contract at <c>Library/Components/Shared/Null.vb</c> defines its
    /// absent-String marker as <c>""</c> and not as null, and <c>Convert.ToString</c> of a database null
    /// yields <c>""</c> as well - so one comparison covered a missing key, a null value and a blank
    /// value alike. <see cref="IHostSettingsService.GetSettingsAsync"/> deliberately diverges from that
    /// contract: a key that is not present is simply absent from the dictionary, and a present key can
    /// still hold blank text. Testing for <c>""</c> alone would therefore let a MISSING key through as
    /// though it were configured. Each read here tests presence AND non-blankness AND parsability, which
    /// restores the legacy outcome across all three cases; a value that is present but unparsable is
    /// treated as absent, matching the legacy conversion, which yielded the type's default.
    /// </para>
    /// <para>
    /// MIGRATION: the trial length is applied through the injected clock, whose reading is UTC, where
    /// the legacy expression added a day interval to the current instant using the VB runtime's
    /// date-arithmetic intrinsic - reached without an <c>Imports</c> statement, because the project
    /// imported the runtime namespace globally - over a SERVER-LOCAL current time. Neither the intrinsic
    /// nor the local reading is carried forward; adding days to the clock's reading replaces both.
    /// Because the offset between the two can cross midnight, a
    /// computed expiry can land on a different CALENDAR DAY from the one the legacy code would have
    /// produced for the same real instant, and a trial can therefore appear to end a day early or a day
    /// late relative to the legacy installation. The legacy expression compounded that by round-tripping
    /// the result through a formatted medium-date string and re-parsing it - a lossy artefact of a
    /// formatting helper in the excluded globals module, which truncated the time component. The value
    /// is kept strongly typed here instead of being formatted and re-parsed, so no precision is lost.
    /// </para>
    /// <para>
    /// MIGRATION: DEFECT 7, annotated and deliberately NOT fixed. The legacy body ended at
    /// <c>PortalController.vb:L371-L373</c> with <c>Catch</c> / <c>' error creating portal</c> /
    /// <c>End Try</c> - an EMPTY handler. Every failure in reading the host settings or inserting the
    /// portal row was therefore discarded and the method returned <c>-1</c>, which the caller tested at
    /// L990 as its sole failure signal. That signal is unusable in this schema, because
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY (-1, 1)</c>, so <c>-1</c> is the identifier of the
    /// first portal an installation creates: a successful creation and a swallowed failure returned the
    /// SAME value. Neither half is reproduced. Failures here propagate, and the outcome is reported by a
    /// Result whose failure code cannot be confused with an identifier. Reproducing the empty handler
    /// would violate the enterprise baseline, so this is recorded as a documented divergence rather than
    /// presented as a silent fix.
    /// </para>
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

        // The retention period is the one setting whose absence is NOT zero. The legacy code carried its
        // -1 sentinel here, and -1 meant "keep for ever" rather than "keep for minus one day", so
        // collapsing it to zero would silently turn unlimited retention into none. It stays absent.
        int? siteLogHistory = ReadInt(settings, SiteLogHistorySetting);

        string? configuredCurrency = settings.GetValueOrDefault(HostCurrencySetting);
        string currency = string.IsNullOrWhiteSpace(configuredCurrency)
            ? FallbackCurrency
            : configuredCurrency;

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
    /// <para>
    /// MIGRATION: the legacy path created each role through a private helper that clamped a negative
    /// fee to zero and passed a monthly billing frequency with a zero period and no trial (L1390,
    /// L1393, L1396). Those values are reproduced literally, and the shared clamp is reused so that
    /// the rule lives in exactly one place.
    /// </para>
    /// <para>
    /// MIGRATION: the clamp replaces the VB runtime's inline-conditional intrinsic, and the substitution
    /// needs justifying because it is NOT valid in general. The legacy statements each wrapped that
    /// intrinsic in a narrowing conversion, testing the fee for being below zero and yielding zero when it
    /// was - at <c>PortalController.vb:L395</c> for the service fee and L398 for the trial fee. That
    /// intrinsic is an ordinary FUNCTION rather than an operator, so both of its value arguments are
    /// EVALUATED BEFORE IT IS CALLED, whereas the C# conditional operator SHORT-CIRCUITS and evaluates
    /// only the branch it selects. Substituting one for the other is therefore only faithful when neither
    /// branch has a side effect, can throw, or is expensive - and here each branch is a literal zero or a
    /// parameter already in hand, so nothing is observable in the difference and the substitution is
    /// exact. A maximum against zero is used rather than a conditional because it states the intent -
    /// never below zero - in one term. Where a future port meets that same intrinsic with arguments that
    /// call a method, index a collection or read a property with a side effect, this rewrite would
    /// silently change behaviour by no longer evaluating the discarded branch, so the two must not be
    /// swapped without re-checking this reasoning.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy helper looked the role up BY NAME first -
    /// <c>GetRoleByName(PortalId, roleName)</c> - and created a row only when none was found, otherwise
    /// returning the existing identifier UNCHANGED. That create-or-reuse behaviour is a genuine rule
    /// rather than an optimisation, because the same helper was reachable for a tenant that already
    /// carried the role. It is preserved by construction on this path rather than by a lookup: these
    /// three roles are built for a portal that is being created in the same transaction, so the tenant
    /// provably carries no role of any name yet and a lookup could only ever miss. A caller adding a
    /// role to an EXISTING tenant goes through the role service, which performs the name check.
    /// </para>
    /// <para>
    /// MIGRATION: the billing and trial codes are LOAD-BEARING DATA, not presentation. The legacy calls
    /// passed the string literals <c>"M"</c> and <c>"N"</c>, which are stored in
    /// <c>Roles.BillingFrequency</c>, a <c>char(1)</c> column, so the letters themselves are what the
    /// schema holds. They are carried across as the enum members whose stored values are those exact
    /// characters and are never renamed or normalised. The enum admits SIX codes rather than the four a
    /// reader might assume from the calendar-interval letters alone, so no exhaustive handling may be
    /// written on the assumption that there are four.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy helper also set <c>RoleGroupID</c> to its absent-Integer sentinel, <c>-1</c>,
    /// as its "belongs to no group" convention. No sentinel is stored here: the grouping navigation is
    /// simply left unset, so absence is represented by a null column rather than by a number that is
    /// also a legitimate group identifier.
    /// </para>
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
            // The assignment is read before it is withdrawn, so a compensation withdraws only what the
            // store actually accepted. DeleteUserRoleAsync would itself be a no-op on an absent row, but
            // issuing it regardless would make a compensation that reverses nothing indistinguishable
            // from one that reverses three enrolments - and this path exists precisely to be auditable.
            UserRole? assignment = await _roles
                .GetUserRoleAsync(portal.PortalId, administrator.UserId, role.RoleId, cancellationToken)
                .ConfigureAwait(false);

            if (assignment is not null)
            {
                await _roles
                    .DeleteUserRoleAsync(assignment.UserId, assignment.RoleId, cancellationToken)
                    .ConfigureAwait(false);
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
            await _roles.DeleteAsync(role.RoleId, cancellationToken).ConfigureAwait(false);
        }

        _users.Remove(administrator);
        await _aliases.DeleteAsync(alias.PortalAliasId, cancellationToken).ConfigureAwait(false);
        await _portals.DeleteAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);

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
    /// against the submitted value alone, and they are compared by calling the very members that write
    /// it - <see cref="PortalMappings.ClampFee"/> and <see cref="PortalMappings.ClampQuota"/> - rather
    /// than by restating their arithmetic here. That is deliberate: this rule is only sound while the
    /// comparison and the write agree, and routing both through one member makes them agree by
    /// construction instead of by coincidence. A restated copy could drift, and the drift would not
    /// break a build or a test - it would quietly admit the change this rule exists to refuse.
    /// This request is a whole-row replacement, so an omitted numeric
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
            || PortalMappings.ClampQuota(request.HostSpace ?? 0) != portal.HostSpace
            || PortalMappings.ClampQuota(request.PageQuota ?? 0) != portal.PageQuota
            || PortalMappings.ClampQuota(request.UserQuota ?? 0) != portal.UserQuota
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
    /// Reads a host setting as a whole number, treating a missing, blank or unparsable value as absent.
    /// </summary>
    /// <param name="settings">The host settings.</param>
    /// <param name="name">The setting name.</param>
    /// <returns>The parsed value, or <see langword="null"/> when the setting was not usable.</returns>
    /// <remarks>
    /// Absence is returned as a nullable value rather than reported through an output argument, so this
    /// member states its own outcome in its return type and every caller must decide what an absent
    /// setting means for the value it is computing. That matters here because the answer is not uniform:
    /// four of the settings this reads default to zero and one defaults to absent. Parsing is culture
    /// invariant, because a host setting is stored configuration rather than user-entered text and must
    /// read identically on every machine.
    /// </remarks>
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
    /// Parsing is culture invariant so that a stored amount is not reinterpreted by the host's locale -
    /// a decimal separator read under the wrong culture would change the amount by orders of magnitude.
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
