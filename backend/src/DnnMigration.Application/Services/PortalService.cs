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

    /// <summary>Reason code reported when the designated administrator is not a member of the portal.</summary>
    private const string AdministratorReferenceInvalidCode = "portal.administrator_invalid";

    /// <summary>Reason code reported when a submitted page reference belongs to another portal.</summary>
    private const string TabReferenceInvalidCode = "portal.tab_reference_invalid";

    /// <summary>Reason code reported when a processor credential is not represented by a managed reference.</summary>
    private const string ProcessorReferenceInvalidCode = "portal.processor_reference_invalid";

    /// <summary>Reason code reported when a host name is already bound to a portal.</summary>
    private const string AliasDuplicateCode = "portal.alias_duplicate";

    /// <summary>Reason code reported when no alias carries the supplied identifier.</summary>
    private const string AliasNotFoundCode = "portal.alias_not_found";

    /// <summary>Reason code reported when removal is refused to keep one portal in the installation.</summary>
    private const string LastRemainingCode = "portal.last_remaining";

    /// <summary>Reason code reported when a member's live sessions could not be ended before removal.</summary>
    private const string MemberSessionRevocationFailedCode =
        "portal.member.session.revocation_store_unavailable";

    /// <summary>Reason code reported when a final member's external credential could not be removed.</summary>
    private const string MemberCredentialRemovalFailedCode =
        "portal.member.credential.removal_store_unavailable";

    /// <summary>
    /// Reason code reported when a child portal was requested but the parent authority its address
    /// would be composed beneath could not be established.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="CreationFailedCode"/> because nothing was attempted: the request cannot
    /// be interpreted, which is the caller's to correct, whereas a failed creation is the installation's.
    /// </remarks>
    private const string ParentAliasUnresolvedCode = "portal.parent_alias_unresolved";

    /// <summary>Resource type recorded on every tenant-lifecycle audit event.</summary>
    private const string PortalResourceType = "Portal";

    /// <summary>
    /// The character that separates a child portal's segment from its parent's authority.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy composition was <c>GetDomainName(Request) &amp; "/" &amp; ChildPath</c>
    /// (<c>Signup.ascx.vb:L232-L233</c>), and the same character is what the legacy screen scanned back
    /// from in order to isolate the final segment for character validation (<c>InStrRev(..., "/")</c> at
    /// L211). Named once so the composition and the validator's own scan cannot disagree.
    /// </remarks>
    private const char AliasPathSeparator = '/';

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

    /// <summary>
    /// The editor type recorded on a default profile property definition, meaning "not resolved".
    /// </summary>
    /// <remarks>
    /// Zero rather than an arbitrary number. The legacy helper took this value from the <c>Lists</c> table,
    /// whose subsystem is out of scope, and that table is <c>IDENTITY (1, 1)</c>
    /// (<c>03.00.01.SqlDataProvider:L842</c>) - so zero is guaranteed not to name a real editor type and
    /// identifies exactly the rows whose type was never resolved.
    /// </remarks>
    private const int UnresolvedProfileDataType = 0;

    /// <summary>
    /// The character bound the legacy default profile definitions carried for a free-text property.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>ProfileController.AddDefaultDefinitions</c>, which passed 50 for every property
    /// rendered by a text box and 0 for the six rendered by a chooser.
    /// </remarks>
    private const int DefaultProfilePropertyLength = 50;

    /// <summary>The name of the page every new tenant is created with.</summary>
    /// <remarks>
    /// The name the stock portal template gave its first page, and the name the integration seed uses, so a
    /// created tenant and a seeded one are recognisably the same shape.
    /// </remarks>
    private const string HomePageName = "Home";

    /// <summary>
    /// The role identifier the schema reserves for "every user, signed in or not".
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is a RESERVED identifier rather than a row in the Roles table, which is why it is a
    /// constant here and not a lookup. The legacy permission grids expressed an unrestricted grant with it,
    /// and <c>Roles.RoleID</c> is <c>IDENTITY (0, 1)</c>, so a negative value cannot collide with a real
    /// role. It is used for exactly one grant - the home page's view permission - so that a brand-new tenant
    /// is reachable before any account has been enrolled in it.
    /// </remarks>
    private const int AllUsersRoleId = -1;

    private readonly IPortalRepository _portals;
    private readonly IPortalAliasRepository _aliases;
    private readonly ITabRepository _tabs;
    private readonly IUserProfileRepository _profiles;
    private readonly IPermissionRepository _permissions;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;

    /// <summary>
    /// Module repository, used only to remove a tenant's modules before the tenant row itself.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this dependency exists because <c>FK_Modules_Portals</c> is the ONE foreign key to
    /// <c>dbo.Portals</c> that carries no <c>ON DELETE CASCADE</c> - every other one does
    /// (<c>PortalAlias</c>, <c>PortalDesktopModules</c>, <c>RoleGroups</c>, <c>Roles</c>, <c>Tabs</c>,
    /// <c>UserPortals</c>, <c>ProfilePropertyDefinition</c>). That asymmetry is not an oversight in this
    /// solution's mapping: the terminal legacy schema declares it exactly so, the 03.00.09 upgrade script
    /// dropping and re-adding the constraint WITHOUT a cascade clause, and Rule T4 forbids altering it. The
    /// legacy application compensated in the procedure rather than in the schema, and so does this service -
    /// see the removal sequence in <see cref="DeletePortalAsync"/>.
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

    /// <summary>
    /// Initialises a new instance of the <see cref="PortalService"/> class.
    /// </summary>
    /// <param name="portals">Portal repository.</param>
    /// <param name="aliases">Portal alias repository.</param>
    /// <param name="profiles">
    /// Stages the profile property definitions a new tenant begins with, reproducing the legacy
    /// creation sequence's default definition set.
    /// </param>
    /// <param name="permissions">
    /// Resolves the permission catalogue entries the new tenant's home page is granted against.
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
    /// <param name="tokens">
    /// Ends every refresh-token family belonging to an account that portal removal deletes globally.
    /// </param>
    /// <param name="clock">Supplies the current instant, so time-dependent behaviour is testable.</param>
    /// <param name="cache">Absorbs the legacy portal cache.</param>
    /// <param name="currentUser">
    /// Identifies the caller, which the update path needs in order to enforce the host-only rule the
    /// legacy settings screen applied to the hosting and quota fields.
    /// </param>
    /// <param name="audit">
    /// Records the tenant lifecycle under the legacy event names. Package-neutral by construction, which
    /// is what allows a trail to be kept from a project that can name no logging package.
    /// </param>
    /// <param name="portalContext">
    /// The tenant the current request resolved to, where there is one. Read for ONE purpose: composing a
    /// child tenant's alias from the authority the operator is addressing, which is what the legacy
    /// signup screen took from the request. Taken as the HOLDER rather than as the resolved context
    /// because a child tenant can be created from a request that resolved to no tenant at all, and the
    /// resolved abstraction's container factory throws in that case.
    /// </param>
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

    /// <summary>
    /// Records one committed tenant-lifecycle change on the audit trail.
    /// </summary>
    /// <param name="record">
    /// The event to record, already carrying its tenant facts. The acting account is stamped here rather
    /// than by the caller, so that no call site can name an actor of its own choosing.
    /// </param>
    /// <remarks>
    /// Called only AFTER the change has been committed, so no record can describe a write that was later
    /// rolled back. The actor identifier is read from the credential through <see cref="ICurrentUser"/> and
    /// only when the caller is authenticated: an unauthenticated path leaves the actor absent rather than
    /// fabricating one, which is honest about a signup that carries no credential.
    /// <para>
    /// MIGRATION: the legacy entry copied the acting account's name into the logging store. The target keeps
    /// the stable account identifier instead: it remains joinable to the authoritative account while the
    /// account exists, and deletion does not leave a second copy of the person's identifier behind.
    /// </para>
    /// </remarks>
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

        // MIGRATION: the PER-COLLECTION ordering set is enforced HERE, not only at the API edge. The
        // shared request validator can apply nothing narrower than the union of every collection's set,
        // because one PagedRequest contract serves every listing and one validator is resolved for it, so
        // on its own it would admit a role-only or account-only field name for this listing and this read
        // would then quietly order by its default instead. Enforcing the narrow set at the point of
        // dispatch closes that, and it closes it for every caller rather than only for an HTTP one -
        // including a test or another service that bypasses validation entirely. The set is exactly the
        // arms PortalRepository's ordering expression honours.
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

        // MIGRATION: the legacy listing read its member and page tallies from correlated sub-selects
        // inside the portal view, so they were computed per row - but they were computed per row INSIDE
        // ONE STATEMENT, at no round-trip cost. Reproducing "per row" literally, by asking the
        // single-portal tally members once per row from here, would turn a page of fifty tenants into a
        // hundred round trips for figures the store can group in two, and would make the cost of the
        // listing a function of its page size. The batched members exist for exactly this call site: the
        // distinct identifiers of the page are resolved in one read each, then joined in memory below.
        IReadOnlyCollection<int> pagePortalIds = page.Items
            .Select(portal => portal.PortalId)
            .Distinct()
            .ToList();

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

            // The batched members promise a TOTAL map over the identifiers supplied, so a missing key is
            // a contract violation rather than an expected state. It is still read defensively as zero:
            // a tenant with no members and no pages is legitimate, zero is what the legacy grid showed
            // for it, and failing an entire listing over one absent tally would be a worse answer than
            // the figure that absence implies.
            int users = usersByPortal.TryGetValue(portal.PortalId, out int userTally) ? userTally : 0;
            int pages = pagesByPortal.TryGetValue(portal.PortalId, out int pageTally) ? pageTally : 0;

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

        string submittedAlias = (request.PortalAlias ?? string.Empty).Trim();
        if (submittedAlias.Length == 0)
        {
            throw new DomainException("A portal alias is required in order to reach the new portal.");
        }

        // MIGRATION: A CHILD PORTAL'S ALIAS IS COMPOSED, NOT STORED AS TYPED, and until now the flag was
        // accepted and never read - so a caller could ask for a child portal, be told it had one, and find
        // it unreachable. The legacy screen has two branches, both at Signup.ascx.vb:
        //
        //   L187-L197  request came from a PORTAL page: child is forced, the typed value is validated
        //              against the child charset "a-z0-9-" and is therefore a bare SEGMENT, and
        //              L232-L233 stores GetDomainName(Request) & "/" & segment.
        //   L199-L216  request came from a HOST page: child is the operator's choice, the typed value MAY
        //              already carry path separators, only its final segment is charset-validated
        //              (Mid(..., InStrRev(..., "/") + 1)), and L235 stores the typed value VERBATIM.
        //
        // Both are reproduced. A submitted value that already carries a separator is the host branch and is
        // stored as typed; one that does not is the portal branch and is composed beneath the authority the
        // operator is addressing. The authority comes from the tenant this request resolved to, which is the
        // target's equivalent of GetDomainName(Request) - and a closer one, because it is by construction an
        // alias that exists rather than a value re-derived from the URL.
        Result<string> composed = ComposeAlias(request, submittedAlias);

        if (composed.IsFailure)
        {
            return Result<PortalDetailDto>.Failure(composed.Reason!);
        }

        string alias = composed.Value;

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

        // ONE TRANSACTION SPANS THE WHOLE OF THE REST OF THIS MEMBER. Creating a tenant is not a single
        // write and cannot be made into one: three columns on the tenant row need keys the store assigns
        // during the first commit, and the administrator's CREDENTIAL lives in the ASP.NET membership
        // objects, which are mapped alongside rather than owned (Rule T4) and are written by statement
        // rather than by the change tracker. The sequence is therefore commit, write credential, commit -
        // and until now the FIRST of those commits was durable on its own, so a failure after it left a
        // half-built tenant: a portal reachable at its alias whose administrator held no credential and
        // whose three stamped columns were empty. The only thing standing between that and a consistent
        // installation was an in-process compensation routine, which could not run if the process was
        // terminated and was written not to run on cancellation either.
        //
        // The transaction subsumes all of it. Both SaveChanges calls and the credential statement enlist in
        // it - the credential statement because the membership store borrows this unit of work's own
        // connection and binds the ambient transaction onto its command
        // (Infrastructure/Persistence/MembershipStore.cs, the shared command helper) - so there is no
        // cross-store boundary to compensate across. Returning or throwing anywhere below disposes the
        // scope without committing, and the STORE reverses every one of those writes, including on paths
        // nobody anticipated. That is why the compensation routine is deleted rather than retained as a
        // belt-and-braces second mechanism: a second mechanism that can disagree with the first is a
        // liability, and this one is strictly weaker than what it would sit beside.
        //
        // Default isolation, not serialisable. The two collision checks above are guarded by unique
        // indexes on the alias and account-name columns, so a concurrent creation of the same alias is
        // refused by the store rather than by the check, and a stricter level would buy nothing while
        // widening the lock footprint of the installation's busiest write.
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
        // flushes it. What keeps the whole multi-table creation sequence atomic is the explicit
        // transaction this method opened - its commit, not any single flush, is the durability boundary.
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
        // assignments - is committed here. Every foreign key inside the graph is resolved by the object
        // graph itself, so no store-assigned identifier is needed beforehand.
        //
        // THIS COMMIT IS NOT DURABLE ON ITS OWN: it is enclosed by the transaction opened above, which is
        // committed only once the credential and the three stamped columns have been written too. That
        // enclosure is the whole of the fix. Two writes are unavoidable here - three columns on the tenant
        // row need keys the store assigns DURING this commit, and the credential lives in an external
        // membership store that no entity maps - so the sequence must be commit, write, commit. What was
        // wrong was that the first commit was durable by itself, leaving an in-process compensation routine
        // as the only thing between a failure and a half-built tenant: a routine that could not run if the
        // process was terminated, and that was written not to run on cancellation either. A rolled-back
        // transaction reverses all of it, including on paths nobody anticipated.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // MIGRATION: the legacy path had this same shape - insert the portal, create the administrator,
        // create the roles, then call UpdatePortalSetup to stamp the identifiers (PortalController.vb
        // L1114-L1118) - and had no transaction over any of it.
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
                // Returning without committing rolls the transaction back, so the portal, its alias, its
                // three roles, its administrator, the membership and the three enrolments all disappear.
                // No compensation routine is called and none exists any more: the store reverses the work,
                // which is the one mechanism that also covers a terminated process.
                //
                // MIGRATION: the credential store is EXTERNAL to this transaction - the aspnet_Membership
                // objects are installed by the ASP.NET registration tool and are mapped alongside rather
                // than owned (Rule T4) - so a credential that WAS created and then rolled back around
                // would leave an orphan. That cannot arise on this path, because this branch is reached
                // only when the credential was NOT created. The failure path below covers the other case.
                return Result<PortalDetailDto>.Failure(
                    CreationFailedCode,
                    "The portal administrator's credential could not be created, so the portal was rolled back.");
            }

            // The two DATABASE-BACKED stages of the legacy creation sequence that this service used to
            // omit. Both are required for the tenant to be usable rather than merely present: without the
            // definitions no account in it can hold a profile at all, and without a home page it has
            // nowhere to serve. Staged inside the same transaction as everything else, so a failure in
            // either rolls the whole tenant back.
            await CreateDefaultProfileDefinitionsAsync(portal.PortalId, cancellationToken)
                .ConfigureAwait(false);

            Tab homePage = await CreateHomePageAsync(portal, administratorsRole, cancellationToken)
                .ConfigureAwait(false);

            // Commits the definitions and the page, so the identifier the store assigns to the page is
            // readable for the stamp below.
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            portal.AdministratorId = administrator.UserId;
            portal.AdministratorRoleId = administratorsRole.RoleId;
            portal.RegisteredRoleId = registeredUsersRole.RoleId;
            portal.HomeTabId = homePage.TabId;

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Everything staged since the transaction was opened becomes durable here, and nothing before it.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

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

        // MIGRATION: the legacy path closed with an AUDIT ENTRY that this service now emits through the
        // package-neutral sink; the original shape and the layering reason for the indirection are recorded
        // in full here.
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
        // MIGRATION: THIS LAYER NOW EMITS IT, through a package-neutral sink. The obstacle was real and is
        // recorded so the shape of the solution is understandable rather than arbitrary: the audit sites
        // become structured log events, but DnnMigration.Application declares exactly two packages -
        // FluentValidation and its dependency-injection extensions - because AAP 0.6.1 states "Application
        // declares only FluentValidation", and Microsoft.Extensions.Logging.Abstractions is neither among
        // them nor present in the reference pack a class library targets. Naming ILogger<T> in this project
        // therefore fails to compile with CS0234 and CS0246, which was VERIFIED by compiling it rather than
        // assumed. The resolution is not to import the package but to invert the dependency: IAuditSink is
        // declared HERE, in terms this project can express, and Infrastructure implements it over ILogger -
        // which is where AAP 0.6.1 already assigns Serilog. The trail is kept, the package inventory is
        // untouched, and the layering rule is satisfied rather than worked around.
        // MIGRATION: TWO REVISIONS NARROWED THIS SET INDEPENDENTLY AND THE NARROWER ONE SURVIVES, WIDENED BY
        // THE THREE PROPERTIES THE OTHER ADDED THAT CARRY NO CALLER-SHAPED TEXT. What has to go either way is
        // the administrator's name, user name and email, which are personal, and the CONTENT of the two
        // free-text members. What the two revisions disagreed about was the tenant name and alias; the
        // reasoning is set out at the dictionary below. An earlier rationale here asserted that six
        // properties were "carried verbatim" while one was recorded, and this paragraph replaces that drift.
        //
        // MIGRATION: of the fourteen legacy properties, the ones that survive are recorded below, and the
        // set is NARROWER than the original's. Four are NOT carried because they describe
        // file-system work this migration does not perform - TemplatePath, TemplateFile, ServerPath and
        // ChildPath - so recording them would assert something untrue about what happened. The password
        // remains absent, exactly as it was in the legacy entry: the legacy code already declined to
        // record the credential and that restraint is preserved rather than newly imposed.
        //
        // SEC: FIVE FURTHER PROPERTIES ARE NOW WITHHELD, AND THIS IS A DELIBERATE NARROWING OF THE
        // LEGACY RECORD. An earlier revision carried the administrator's FIRST NAME, LAST NAME, USER NAME
        // and EMAIL ADDRESS, plus the caller's free-text DESCRIPTION and KEYWORDS. Every one of those is
        // personal data or unvalidated caller text, and this sink writes to the GENERAL application log -
        // the highest-volume, longest-retained and most widely-readable store the application produces,
        // and one this codebase does not control the retention of. Two distinct problems followed:
        //
        //   - Personal data was copied into a store chosen for diagnostics rather than for records
        //     management, where it cannot be located for a subject-access or erasure request and is
        //     retained for as long as the log is. An audit trail that legally requires personal data
        //     belongs in a dedicated, access-controlled store with its own retention policy; it does not
        //     belong here by default, and adding one is a deployment decision rather than a side effect
        //     of porting a legacy entry.
        //   - The two free-text members were caller-supplied and therefore attacker-shaped. They are
        //     bounded in length by the request validators but not in CONTENT, so a description containing
        //     a carriage return and a line feed could forge additional log lines - a log-injection defect
        //     whose root cause is now also closed at the rendering layer, in
        //     Infrastructure/Services/LoggingAuditSink.cs, so no property from any event can break out
        //     of its own line.
        //
        // WHAT SURVIVES IS EVERY FACT AN AUDITOR ACTUALLY NEEDS, and it is deliberately all
        // non-personal: the portal identifier and name, the alias the tenant answers on, whether it is a
        // child portal, and the administrator's USER IDENTIFIER. The identifier is the stable key that
        // resolves to the account's name and address in the store whenever an operator legitimately needs
        // them, which is the difference between a record that points at a person and a record that copies
        // them. The event's own SubjectUserId already carries that identifier, so "who can now sign in to
        // this tenant" remains answerable.
        // MIGRATION: the legacy entry was typed HOST_ALERT and this one is named PORTAL_CREATED, from the
        // same EventLogType enum (EventLogController.vb:L38-L77). The legacy typing was the coarser of the
        // two available choices; the enum's own PORTAL_CREATED member is the accurate one, so it is used.
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
        // documented divergence from the legacy behaviour rather than a silent correction of it. The sink
        // itself is written not to throw, so an audit failure cannot fail an installation either - the
        // legacy outcome is preserved without the legacy mechanism.
        Dictionary<string, string?> installation = new(StringComparer.Ordinal)
        {
            // MIGRATION: THE TENANT NAME AND ALIAS ARE DELIBERATELY ABSENT, AND ONE REVISION RECORDED BOTH.
            // The argument for recording them is readability: a record naming "Contoso Intranet" is easier
            // to reconstruct an incident from than one naming tenant 42. The argument against is decisive
            // for a general log: both are CALLER-SUPPLIED text bounded in length and not in content, and the
            // envelope of this very record already carries the tenant key, so the name adds no identifying
            // power the record did not already have - it only adds an unbounded-shape string to a line-
            // oriented sink. The sink's allowlist enforces the same rule from the other side, admitting only
            // identifiers, closed vocabularies and booleans, so recording them here would have had them
            // withheld there and the two layers would have disagreed in silence.
            //
            // What that revision contributed and is KEPT: the administrator's numeric key, which answers
            // "who can now sign in to this tenant" without naming anybody, and presence-only flags for the
            // two free-text members - so an auditor can still tell that an installation was asked for a
            // description without the description itself reaching the log.
            ["IsChildPortal"] = request.IsChildPortal.ToString(),
            ["AdministratorId"] = administrator.UserId.ToString(CultureInfo.InvariantCulture),
            ["DescriptionSupplied"] = (!string.IsNullOrWhiteSpace(request.Description))
                .ToString(CultureInfo.InvariantCulture),
            ["KeywordsSupplied"] = (!string.IsNullOrWhiteSpace(request.KeyWords))
                .ToString(CultureInfo.InvariantCulture),
        };

        // Recorded after the commit, so no record can describe an installation that was rolled back. The
        // SUBJECT is the administrator the installation created, which is what makes the record answer
        // "who can now sign in to this tenant" and not merely "a tenant appeared".
        AuditEvent installed = new(AuditEventNames.PortalCreated)
        {
            PortalId = portal.PortalId,
            SubjectUserId = administrator.UserId,
            ResourceType = PortalResourceType,
            ResourceId = portal.PortalId.ToString(CultureInfo.InvariantCulture),
            Properties = installation,
        };

        RecordAudit(installed);

        // The legacy type for the same operation, emitted from the same facts by changing only the name, so
        // the two records cannot drift apart or describe different installations.
        RecordAudit(installed with { EventName = AuditEventNames.HostAlert });

        return Result<PortalDetailDto>.Success(created);
    }

    /// <inheritdoc />
    public async Task<Result<PortalDetailDto?>> UpdatePortalAsync(
        int portalId,
        UpdatePortalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // SEC-010: the ownership checks and the write are one serialisable operation. Without the
        // transaction, a membership or page could move after it was validated and before the portal row
        // was saved, recreating the foreign reference through a time-of-check/time-of-use race.
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

            await EnsureHostOnlyFieldsUnchangedAsync(portal, request, cancellationToken).ConfigureAwait(false);

            Result references = await ValidateUpdateReferencesAsync(portal, request, cancellationToken)
                .ConfigureAwait(false);
            if (references.IsFailure)
            {
                return Result<PortalDetailDto?>.Failure(references.Error!);
            }

            PortalMappings.ApplyUpdate(portal, request);

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _cache.InvalidatePortal(portalId);

        PortalDetailDto? detail = await ReadDetailAsync(portalId, cancellationToken).ConfigureAwait(false);
        return Result<PortalDetailDto?>.Success(detail);
    }

    /// <inheritdoc />
    public async Task<Result> DeletePortalAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // THE WHOLE OF THIS OPERATION IS ONE SERIALISABLE TRANSACTION, and the reason is the guard below
        // rather than the removal itself. The guard counts the installation's tenants, judges the count,
        // and deletes in a later statement. Under any weaker isolation two callers removing the two
        // remaining tenants concurrently can BOTH read a count of two, both conclude that one will
        // remain, and both proceed - leaving an installation with no tenant at all, which is unreachable
        // and cannot be repaired through this API because every route needs a tenant to resolve against.
        // Serialisable is what makes the second caller's count wait for the first caller's delete and
        // then observe one.
        //
        // The read of the tenant is inside the transaction as well, deliberately: a tenant removed by a
        // concurrent caller between that read and the count would otherwise be deleted twice, and the
        // second attempt would fail at the store rather than reporting the absence this contract names.
        //
        // Disposal rolls back, so every failure path below - a refusal, a store rejection, a
        // cancellation, an exception from anything the removal touches - leaves the installation exactly
        // as it was without a rollback statement being written for it.
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
        // Aliases are loaded, so they are removed explicitly. Pages, roles and memberships are removed by
        // the cascade configured on the portal's relationships; the tenant's MODULES are not, and are
        // removed explicitly below.
        // Captured BEFORE the removal because the alias rows do not survive it.
        int releasedAliasCount = portal.PortalAliases.Count;

        // MIGRATION: THE TENANT'S MODULES ARE REMOVED FIRST, AND EXPLICITLY, because the store will not do
        // it. Every other foreign key into dbo.Portals is declared ON DELETE CASCADE, but
        // FK_Modules_Portals is not - the 03.00.09 upgrade script drops the constraint and re-adds it with
        // no cascade clause, which is the terminal state Rule T4 binds this model to. A portal row deleted
        // while any dbo.Modules row still points at it is therefore refused by the store, which surfaced as
        // an undeclared 500 for every tenant that owned so much as one module.
        //
        // The legacy application solved this in exactly the same place and in exactly this order. The
        // terminal DeletePortalInfo procedure (04.04.00.SqlDataProvider lines 155-175) opens with
        // "DELETE FROM Modules WHERE PortalId = @PortalId" and only then deletes the Portals row, and the
        // controller's own history records the move - "[cnurse] 24/11/2006 Removal of Modules moved to
        // sproc" at PortalController.vb:L1191. Reproducing the order here rather than relying on a cascade
        // keeps that behaviour where a reader can see it.
        //
        // Removing the module rows is sufficient for everything that hangs off them: FK_ModuleSettings_Modules,
        // FK_TabModules_Modules and FK_ModulePermission_Modules all cascade, so a module's settings, its
        // placements and its grants go with it. The procedure's second statement, which joined dbo.SearchItem,
        // has no counterpart because the search subsystem is out of scope.
        //
        // Staged inside the transaction already open above, so a later refusal rolls the module removals back
        // with everything else and the tenant is left exactly as it was.
        IReadOnlyList<Module> portalModules = await _modules
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        foreach (Module module in portalModules)
        {
            await _modules.DeleteAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<User> portalMembers = await _users
            .ListPortalMembersForRemovalAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> accountIdsToRemove = portalMembers
            .Where(member =>
                !member.IsSuperUser
                && !member.UserPortals.Any(membership => membership.PortalId != portalId))
            .Select(member => member.UserId)
            .ToHashSet();

        // The token store is in process and cannot enlist in the database transaction. The serialisable
        // transaction is already open because the final-membership decision above has to be protected from
        // concurrent membership changes, but no relational removal has been staged yet. Ending sessions
        // first therefore has the same safety property as UserService.DeleteUserAsync: a refusal leaves all
        // database rows intact, while a later database refusal can cost an account a re-authentication and
        // nothing more.
        foreach (User account in portalMembers.Where(account => accountIdsToRemove.Contains(account.UserId)))
        {
            Result revoked = await _tokens
                .RevokeAllRefreshTokensAsync(account.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (revoked.IsFailure)
            {
                return Result.Failure(
                    MemberSessionRevocationFailedCode,
                    FormattableString.Invariant(
                        $"The sessions held by account {account.UserId} could not be ended.")
                    + " The portal removal was abandoned before any database row was removed. Try again.");
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
                // work's connection and ambient transaction. A refusal consequently rolls back every
                // relational removal staged above and leaves the account reachable rather than orphaning
                // its credential.
                if (!await _users
                    .DeleteCredentialAsync(account.UserId, cancellationToken)
                    .ConfigureAwait(false))
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
                // MIGRATION: a host account is installation-wide and must never be globally removed by
                // deleting one tenant, even when this is its only UserPortals row. The legacy bulk path
                // admitted super users and could therefore delete the installation's final operator. The
                // divergence is intentional: only the expiring tenant membership is removed. The same arm
                // retains ordinary accounts that still belong to another portal, and their sessions remain
                // active because the account itself survives.
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

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

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
        // Recorded after the commit, so no event can describe a removal that was rolled back. The stable
        // identifier remains in the envelope; the deleted tenant's name is deliberately not copied into
        // the independently retained logging store.
        RecordAudit(new AuditEvent(AuditEventNames.PortalDeleted)
        {
            PortalId = portalId,
            ResourceType = PortalResourceType,
            ResourceId = portalId.ToString(CultureInfo.InvariantCulture),
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AliasesReleased"] = releasedAliasCount.ToString(CultureInfo.InvariantCulture),
            },
        });

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
    public async Task<Result<PortalSettingsDto?>> UpdatePortalSettingsAsync(
        int portalId,
        UpdatePortalSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The settings projection carries no aliases, so this path deliberately avoids loading them.
        // The general portal update still requests them because its response is the full detail contract.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result<PortalSettingsDto?>.Success(null);
        }

        // Both public update resources replace the same stored row and therefore share the same
        // content-sensitive authorisation and aggregate invariant. Keeping the guards on the shared
        // request interface prevents the settings route from becoming a less protected way to write the
        // fields already defended by UpdatePortalAsync.
        await EnsureHostOnlyFieldsUnchangedAsync(portal, request, cancellationToken).ConfigureAwait(false);
        EnsureAdministratorRetained(portal, request);

        PortalMappings.ApplyUpdate(portal, request);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The settings read bypasses the cache, but other readers do not. Invalidating after the commit
        // prevents the list/detail surfaces from continuing to publish the values this operation replaced.
        _cache.InvalidatePortal(portalId);

        return Result<PortalSettingsDto?>.Success(PortalMappings.ToSettings(portal));
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
        int? portalId,
        int portalAliasId,
        CancellationToken cancellationToken = default)
    {
        PortalAlias? alias = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);

        // The owning portal is verified rather than assumed. PortalAlias.PortalAliasID is a surrogate
        // declared IDENTITY (1, 1) and is therefore unique across the installation and guessable across
        // tenants, so a read keyed by it alone discloses every tenant's host bindings to anybody
        // authorised over any one tenant. An alias belonging to another portal is reported as absent
        // rather than refused, so this member cannot be used to discover which keys exist elsewhere.
        // A null scope is installation-wide authority, established by the route's own policy, so the
        // comparison is applied only when a tenant was named. See the contract for why absence is a
        // deliberate mode rather than a missing argument.
        return alias is null || (portalId is int scopedPortalId && alias.PortalId != scopedPortalId)
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
        int? portalId,
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

        // The addressed row must belong to the addressed tenant. This is the most consequential of the
        // three ownership checks on this contract: an alias is what tenant resolution matches on, so
        // renaming somebody else's alias re-points their portal's traffic. An alias owned by another
        // portal is reported as not found, with the same code and wording as one that does not exist,
        // so the refusal carries no information about the other tenant.
        if (stored is null || (portalId is int scopedPortalId && stored.PortalId != scopedPortalId))
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
        int? portalId,
        int portalAliasId,
        CancellationToken cancellationToken = default)
    {
        PortalAlias? stored = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);

        // Ownership is verified before the removal, for the reason given on the update member above.
        // Unbinding another tenant's alias would make that tenant unreachable at the host name its
        // users hold, which is a denial of service reached from a grant over an unrelated portal.
        if (stored is null || (portalId is int scopedPortalId && stored.PortalId != scopedPortalId))
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
    /// Resolves the host name a new portal will actually be reachable at.
    /// </summary>
    /// <param name="request">The submitted creation request, read for <c>IsChildPortal</c>.</param>
    /// <param name="submittedAlias">The trimmed value the caller submitted.</param>
    /// <returns>
    /// A success carrying the alias to store, or a failure carrying
    /// <see cref="ParentAliasUnresolvedCode"/> when a child portal was asked for from a request that
    /// resolved to no tenant.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this member is where the <c>IsChildPortal</c> flag becomes observable. The legacy
    /// signup screen had two branches, both in <c>Website/admin/Portal/Signup.ascx.vb</c>, and both are
    /// reproduced:
    /// </para>
    /// <para>
    /// L187-L197 — the request came from a PORTAL page. Child was FORCED rather than chosen, the typed
    /// value was validated against the child character set (lower-case letters, digits and the hyphen) and
    /// was therefore a bare SEGMENT, and L232-L233 stored <c>GetDomainName(Request) &amp; "/" &amp;
    /// segment</c>.
    /// </para>
    /// <para>
    /// L199-L216 — the request came from a HOST page. Child was the operator's CHOICE, the typed value was
    /// permitted to carry path separators of its own, only its final segment was character-validated
    /// (<c>Mid(..., InStrRev(..., "/") + 1)</c>), and L235 stored the typed value VERBATIM.
    /// </para>
    /// <para>
    /// The branch is selected the same way the legacy screen's own output distinguished them: a submitted
    /// value that already carries a separator is a fully-qualified child address and is stored as typed,
    /// and one that does not is a bare segment and is composed beneath the authority the operator is
    /// addressing. Selecting on the value rather than on the caller's authority level is deliberate — the
    /// legacy discriminator was ambient per-request page state, which does not exist here, and the
    /// authority question is settled by the endpoint's authorisation policy rather than by re-deriving it.
    /// </para>
    /// <para>
    /// The parent authority is the resolved tenant's OWN alias, which is this target's equivalent of
    /// <c>Globals.GetDomainName(Request)</c> (<c>Library/Components/Shared/Globals.vb:L551</c>, implemented
    /// at L563). It is a closer equivalent than a value re-derived from the URL, for two reasons. It is by
    /// construction an alias that EXISTS, so a composed child address is guaranteed to sit beneath a real
    /// tenant rather than beneath a host name nobody has bound. And it already carries any path portion the
    /// request was addressed under, because resolution prefers the longest matching prefix — which
    /// reproduces the legacy member's own behaviour of returning <c>www.domain.com/directory</c> rather
    /// than the bare host when the request arrived beneath a sub-directory, and so nests exactly as the
    /// legacy screen nested.
    /// </para>
    /// <para>
    /// A child portal asked for from a request that resolved to NO tenant is refused rather than guessed
    /// at. The legacy member could always answer, because it read the incoming URL directly; here the
    /// authority must be a bound alias, and inventing one would create a tenant reachable at an address
    /// the installation does not serve. That is reported as a failure code so the API edge can render it
    /// as a bad request rather than as a server fault.
    /// </para>
    /// <para>
    /// Synchronous by construction, and that is a property worth stating rather than an oversight. Tenant
    /// resolution is performed once per request by the API's own alias-resolution stage and is memoised, so
    /// by the time a creation reaches this service the answer is already held and reading it costs nothing.
    /// This member deliberately does NOT resolve on demand: doing so would need the incoming address, which
    /// only the API layer holds, and reaching for it here would put request state in the application layer
    /// (Rule T1). No I/O means no <see cref="Task"/>, per the reading of Rule T6 that async is for I/O.
    /// </para>
    /// </remarks>
    private Result<string> ComposeAlias(CreatePortalRequest request, string submittedAlias)
    {
        if (!request.IsChildPortal)
        {
            // The parent branch. The value is a host authority in its own right and is stored as typed,
            // which is what the legacy screen did for a non-child portal.
            return Result<string>.Success(submittedAlias);
        }

        if (submittedAlias.Contains(AliasPathSeparator, StringComparison.Ordinal))
        {
            // The legacy HOST branch (L235): the operator qualified the address themselves, so it is
            // stored verbatim. The validator has already character-checked its final segment.
            return Result<string>.Success(submittedAlias);
        }

        // The legacy PORTAL branch (L232-L233): a bare segment is composed beneath the addressed
        // authority. Resolution is memoised per request, so this does not re-read the store on a request
        // whose tenant has already been established.
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

        return Result<string>.Success(
            string.Concat(parentAuthority, AliasPathSeparator.ToString(), submittedAlias));
    }

    /// <summary>
    /// Refuses an update in which a caller who is not a host account has altered a host-only field.
    /// </summary>
    /// <param name="portal">The stored portal.</param>
    /// <param name="request">The submitted values.</param>
    /// <param name="cancellationToken">Abandons the authority read when the caller disconnects.</param>
    /// <returns>A task that completes when the request has been admitted.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when a non-host caller has altered the hosting charge, the disc-space quota, the page
    /// quota, the member quota, the site-log retention period or the expiry date.
    /// </exception>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy settings screen compared exactly these six submitted values against the
    /// stored portal and refused the whole save when a non-super-user had changed any of them
    /// (<c>Website/admin/Portal/SiteSettings.ascx.vb</c> L760-L770). It is an authorisation rule over
    /// the request's contents rather than over the route, so it cannot be expressed as a policy on the
    /// endpoint and lives here. It is reported by exception rather than by reason code because the
    /// member documents no failure code for it, and the API edge translates the exception into a single
    /// forbidden response.
    /// </para>
    /// <para>
    /// Every term is compared against the value the update is actually going to WRITE, term for term with
    /// <see cref="PortalMappings.ApplyUpdate"/>, and no arithmetic is applied to either side. That
    /// agreement is the whole soundness argument: the write applies no floor to the hosting charge or to
    /// any allowance - the legacy save path applied none either, and the mapper records why - so flooring
    /// them HERE would compare a coerced submission against a stored value and admit exactly the change
    /// this rule exists to refuse. A caller submitting a negative charge where the stored charge is zero
    /// would pass a floored comparison and then have the negative value stored.
    /// </para>
    /// <para>
    /// This request is a whole-row replacement, so an omitted numeric term is not "leave it alone":
    /// <see cref="PortalMappings.ApplyUpdate"/> substitutes zero for it, because the underlying columns
    /// cannot hold null. Testing <c>request.HostFee is decimal</c> and letting an omission through would
    /// therefore admit the same change by another route — a tenant administrator could waive the hosting
    /// charge and lift every quota simply by leaving those fields out of the request. Comparing the
    /// effective value closes that, and costs a caller who is genuinely not changing them nothing,
    /// because echoing a value back compares equal.
    /// </para>
    /// <para>
    /// SEC-011: THE EXEMPTION IS READ FROM THE STORE, NOT FROM THE TOKEN. It used to test the super-user claim
    /// carried on the caller's bearer token, which is a snapshot of what was true when the token was minted.
    /// Access tokens outlive the facts they assert: an account demoted out of the host role keeps a valid token
    /// until it expires, and would have kept the exemption with it - able to waive the hosting charge and lift
    /// every quota on any portal it could otherwise administer, for the remainder of that token's life. The
    /// status is therefore re-read from <c>dbo.Users</c> per request, which is the same rule the
    /// portal-administration authorisation handler already applies to the same claim, so the two cannot
    /// disagree about who is a host account.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Refuses an update that would leave a portal with no designated administrator account.
    /// </summary>
    /// <param name="portal">The stored tenant being updated.</param>
    /// <param name="request">The submitted settings.</param>
    /// <exception cref="DomainException">
    /// The stored portal designates an administrator and the request would clear it.
    /// </exception>
    /// <remarks>
    /// An omitted administrator means "leave it as it is", which is why the guard tests the STORED value as
    /// well as the submitted one: a portal that already has none is not made worse by an update that supplies
    /// none, whereas clearing a designated administrator would leave the tenant with no account able to
    /// administer it and no route back other than a host-level repair.
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
    /// <para>
    /// SEC-011: EVERY ARM OF THIS FAILS CLOSED. An unauthenticated caller is not a host account. A caller
    /// whose account is absent from the store - deleted since sign-in, or a token minted for an identifier
    /// that never existed - is not a host account either, because a missing record cannot evidence authority.
    /// Only a stored row saying so grants the exemption.
    /// </para>
    /// <para>
    /// The account is read installation-wide, with no portal anchor. A host account belongs to no tenant, so
    /// anchoring the read to a portal would fail to find precisely the account being asked about.
    /// </para>
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
    /// <para>
    /// SEC-010: the legacy screen populated the administrator and four page selectors from this portal's
    /// own records, but the API accepts identifiers directly. A foreign identifier is therefore rejected
    /// here rather than trusted until a database constraint fails; the page columns do not even carry
    /// foreign keys in every supported schema.
    /// </para>
    /// <para>
    /// The request is a whole-row replacement, so a null administrator clears the column. A portal that
    /// already designates an administrator may not be cleared accidentally; a pre-existing broken row with
    /// no administrator remains repairable by assigning a valid member.
    /// </para>
    /// <para>
    /// Each page is tested through <see cref="IPortalRepository.TabBelongsToPortalAsync"/>, whose false
    /// result deliberately covers both absence and another tenant's row. No numeric range check is used:
    /// zero is a legitimate page identifier.
    /// </para>
    /// <para>
    /// Expected failures carry bounded messages and stable reason codes, so the API returns a correctable
    /// 400 response without disclosing another tenant's account or page metadata.
    /// </para>
    /// </remarks>
    private async Task<Result> ValidateUpdateReferencesAsync(
        Portal portal,
        UpdatePortalRequest request,
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

        if (request.AdministratorId is int administratorId)
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
        else if (portal.AdministratorId is not null)
        {
            return Result.Failure(
                AdministratorReferenceInvalidCode,
                "A portal must designate an administrator account that belongs to the addressed portal.");
        }

        (string Field, int? TabId)[] pageReferences =
        [
            (nameof(UpdatePortalRequest.SplashTabId), request.SplashTabId),
            (nameof(UpdatePortalRequest.HomeTabId), request.HomeTabId),
            (nameof(UpdatePortalRequest.LoginTabId), request.LoginTabId),
            (nameof(UpdatePortalRequest.UserTabId), request.UserTabId),
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

    /// <summary>
    /// Installs the nineteen profile property definitions every new tenant begins with.
    /// </summary>
    /// <param name="portalId">The tenant the definitions belong to.</param>
    /// <param name="token">Token observed while the definitions are staged.</param>
    /// <remarks>
    /// <para>
    /// Reproduces <c>ProfileController.AddDefaultDefinitions</c>
    /// (<c>Library/Components/Users/Profile/ProfileController.vb:L334-L361</c>), which the legacy creation
    /// sequence reached through <c>CreateProfileDefinitions</c> at <c>PortalController.vb:L300</c>. The four
    /// categories, the nineteen names and their order are transcribed from that method rather than chosen:
    /// five under Name, six under Address, five under Contact Info and three under Preferences.
    /// </para>
    /// <para>
    /// The view order is 3, 5, 7 and so on to 39, and that is not an off-by-one. The legacy helper set
    /// <c>_orderCounter = 1</c> and then incremented it by two BEFORE assigning
    /// (<c>ProfileController.vb</c> <c>AddDefaultDefinition</c>), so the first definition is 3 rather than 1
    /// and no definition is even. Renumbering them from 1 would change the order every profile screen renders
    /// them in, so the sequence is preserved exactly.
    /// </para>
    /// <para>
    /// The length is 50 for the free-text properties and 0 for the six that are rendered by a chooser rather
    /// than a text box - Region, Country, Biography, TimeZone and PreferredLocale - because a chooser imposes
    /// no character bound. Again transcribed, not inferred.
    /// </para>
    /// <para>
    /// MIGRATION: the editor type cannot be resolved and is stored as zero. The legacy helper looked each
    /// type up in the <c>Lists</c> table - <c>types.Item("DataType." + strType)</c> - and used the row's
    /// <c>EntryID</c>. That table belongs to the list subsystem, which AAP 0.2.2.2 places out of scope, so
    /// there is no lookup to perform. Zero is the honest value rather than an arbitrary one: <c>Lists</c> is
    /// declared <c>IDENTITY (1, 1)</c> at <c>03.00.01.SqlDataProvider:L842</c>, so zero cannot collide with
    /// any real editor type, and the legacy helper itself already had a fallback for an unresolvable type -
    /// it substituted <c>DataType.Unknown</c>. An installation that adopts the list subsystem later can map
    /// these nineteen rows without ambiguity, because zero identifies exactly the ones that were never
    /// resolved.
    /// </para>
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
    /// <returns>The staged page, whose identifier becomes the tenant's home page once it is committed.</returns>
    /// <remarks>
    /// <para>
    /// A tenant with no page has nowhere to serve, so this is the minimum that makes one usable. The legacy
    /// sequence obtained its pages by parsing an XML portal template
    /// (<c>PortalController.vb:L1075</c>), and that whole path is unavailable here: the template lives on
    /// disc, and the modules it places require the module installer, the skinning subsystem and the
    /// <c>IPortable</c> contract, all of which AAP 0.2.2 excludes. What survives is the part that is purely
    /// relational - one page row and its permission grants - which is why the page is created directly rather
    /// than by reproducing a template parser that has nothing to parse.
    /// </para>
    /// <para>
    /// MIGRATION: the consequence is stated plainly. The tenant receives ONE empty page, not the several
    /// pages populated with modules that a template would have supplied. That is a reduction from the legacy
    /// behaviour, and it is the largest one in this sequence; it is bounded by what the excluded subsystems
    /// own, and an operator can add pages through the page endpoints afterwards.
    /// </para>
    /// <para>
    /// The three grants are those the stock template declared for its home page: view for all users, view for
    /// administrators and edit for administrators. The all-users grant is expressed by the role identifier
    /// the schema reserves for it rather than by a role row, which is why it carries no role of this tenant.
    /// A grant is staged only when its catalogue definition can be found - the catalogue is installed by the
    /// upgrade scripts, so on a real installation all three resolve, and on a database that lacks them the
    /// page is still created rather than the whole tenant failing over reference data.
    /// </para>
    /// </remarks>
    private async Task<Tab> CreateHomePageAsync(Portal portal, Role administratorsRole, CancellationToken token)
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

        // The page scope's catalogue definitions. Read once and matched by key, because the two keys this
        // page needs are declared under the same scope code and one read answers for both.
        IReadOnlyList<Permission> pageScope = await _permissions
            .GetByTabIdAsync(homePage.TabId, token)
            .ConfigureAwait(false);

        await GrantHomePagePermissionAsync(homePage, pageScope, PermissionKey.VIEW, AllUsersRoleId, token)
            .ConfigureAwait(false);
        await GrantHomePagePermissionAsync(homePage, pageScope, PermissionKey.VIEW, administratorsRole.RoleId, token)
            .ConfigureAwait(false);
        await GrantHomePagePermissionAsync(homePage, pageScope, PermissionKey.EDIT, administratorsRole.RoleId, token)
            .ConfigureAwait(false);

        return homePage;
    }

    /// <summary>
    /// Stages one page permission grant, when the catalogue defines the key it names.
    /// </summary>
    /// <param name="homePage">The page receiving the grant.</param>
    /// <param name="pageScope">The page scope's catalogue definitions.</param>
    /// <param name="permissionKey">The key to grant.</param>
    /// <param name="roleId">The role receiving it.</param>
    /// <param name="token">Token observed while the grant is staged.</param>
    /// <remarks>
    /// The page is bound by NAVIGATION rather than by identifier, because the page has no identifier until the
    /// commit that follows; the object graph resolves the foreign key for both rows in one write. A key the
    /// catalogue does not define is skipped rather than invented, because a grant referencing a definition
    /// that does not exist would violate the foreign key and fail the whole tenant creation over reference
    /// data that the upgrade scripts own.
    /// </remarks>
    private async Task GrantHomePagePermissionAsync(
        Tab homePage,
        IReadOnlyList<Permission> pageScope,
        PermissionKey permissionKey,
        int roleId,
        CancellationToken token)
    {
        Permission? definition = pageScope.FirstOrDefault(entry => entry.PermissionKey == permissionKey);
        if (definition is null)
        {
            return;
        }

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
