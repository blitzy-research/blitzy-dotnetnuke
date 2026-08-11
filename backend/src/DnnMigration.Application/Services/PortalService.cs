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

/// <summary>
/// Creates, reads, modifies and removes portals, and manages the host names bound to them.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: absorbs <c>Library/Components/Portal/PortalController.vb</c> and the business rules of
/// <c>Website/admin/Portal/{Portals,SiteSettings,Signup,PortalAlias,EditPortalAlias}.ascx.vb</c>.
/// </para>
/// <para>
/// MIGRATION: the thirteen legacy portal cache sites are absorbed here. The legacy key shapes
/// <c>Portal{portalId}</c> and <c>PortalDictionary</c> and the twenty-minute base timeout
/// multiplied by the installation-wide performance setting are preserved so that cache behaviour
/// remains auditable against the original, and every mutation invalidates the portal explicitly
/// rather than relying on the legacy recursive sweep.
/// </para>
/// </remarks>
public sealed class PortalService : IPortalService
{
    private const string PagingInvalidCode = "portal.paging_invalid";

    private const string NotFoundCode = "portal.not_found";

    /// <summary>
    /// Reason code returned when a caller's update carries a concurrency token that no longer matches the
    /// stored tenant, so the record moved between the read and the write.
    /// </summary>
    /// <remarks>
    /// The suffix <c>concurrency_conflict</c> is what the API surface maps to <c>409 Conflict</c>, and it is
    /// the same suffix the role contract uses for the same outcome - deliberately, so a client has one
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

    /// <summary>
    /// Reason code reported when the requested administrator account name is already taken.
    /// </summary>
    /// <remarks>
    /// This is deliberately a separate code from <see cref="CreationFailedCode" /> rather than a second use
    /// of it.
    /// </remarks>
    private const string AdministratorDuplicateCode = "portal.administrator_duplicate";

    private const string AdministratorReferenceInvalidCode = "portal.administrator_invalid";

    private const string TabReferenceInvalidCode = "portal.tab_reference_invalid";

    private const string ProcessorReferenceInvalidCode = "portal.processor_reference_invalid";

    private const string AliasDuplicateCode = "portal.alias_duplicate";

    /// <summary>
    /// Reason code reported when tenant creation lost a race for a unique value the store could
    /// name only vaguely, so neither the alias nor the account-name code can be stated with
    /// confidence.
    /// </summary>
    /// <remarks>MIGRATION: a last resort, and deliberately not the usual answer.</remarks>
    private const string CreationConflictCode = "portal.creation_conflict";

    private const string AliasNotFoundCode = "portal.alias_not_found";

    private const string LastRemainingCode = "portal.last_remaining";

    /// <summary>
    /// Reason code reported when a rename or a removal is addressed at the alias the CURRENT
    /// REQUEST resolved through.
    /// </summary>
    /// <remarks>
    /// <para>MIGRATION: this restores a legacy affordance as a server-side RULE.</para>
    /// <para>
    /// WHAT IT PREVENTS. Renaming or unbinding the alias the current session is using re-points
    /// that host name at nothing, so the tenant stops resolving for every caller arriving through
    /// it - and the operator who did it cannot reach the screen that would undo it, because
    /// reaching that screen requires the tenant to resolve.
    /// </para>
    /// </remarks>
    private const string AliasInUseConflictCode = "portal.alias_in_use.conflict";

    private const string MemberSessionRevocationFailedCode =
        "portal.member.session.revocation_store_unavailable";

    private const string MemberCredentialRemovalFailedCode =
        "portal.member.credential.removal_store_unavailable";

    /// <summary>
    /// Reason code reported when a child portal was requested but the parent authority its address
    /// would be composed beneath could not be established.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="CreationFailedCode"/> because nothing was attempted: the request
    /// cannot be interpreted, which is the caller's to correct, whereas a failed creation is the
    /// installation's.
    /// </remarks>
    private const string ParentAliasUnresolvedCode = "portal.parent_alias_unresolved";

    private const string PortalResourceType = "Portal";

    /// <summary>
    /// The character that separates a child portal's segment from its parent's authority.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy composition was
    /// <c>GetDomainName(Request) &amp; "/" &amp; ChildPath</c> (<c>Signup.ascx.vb</c>), and the
    /// same character is what the legacy screen scanned back from in order to isolate the final
    /// segment for character validation (<c>InStrRev(..., "/")</c>).
    /// </remarks>
    private const char AliasPathSeparator = '/';

    /// <summary>
    /// Legacy cache key shape for a single portal, preserved verbatim from
    /// <c>DataCache.PortalCacheKey</c>.
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

    /// <summary>
    /// The editor type recorded on a default profile property definition, meaning "not resolved".
    /// </summary>
    /// <remarks>
    /// Zero rather than an arbitrary number. The legacy helper took this value from the
    /// <c>Lists</c> table, whose subsystem is out of scope, and that table is
    /// <c>IDENTITY (1, 1)</c> (<c>03.00.01.SqlDataProvider</c>) - so zero is guaranteed not to name
    /// a real editor type and identifies exactly the rows whose type was never resolved.
    /// </remarks>
    private const int UnresolvedProfileDataType = 0;

    /// <summary>
    /// The character bound the legacy default profile definitions carried for a free-text property.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>ProfileController.AddDefaultDefinitions</c>, which passed 50 for every
    /// property rendered by a text box and 0 for the six rendered by a chooser.
    /// </remarks>
    private const int DefaultProfilePropertyLength = 50;

    /// <summary>The name of the page every new tenant is created with.</summary>
    /// <remarks>
    /// The name the stock portal template gave its first page, and the name the integration seed
    /// uses, so a created tenant and a seeded one are recognisably the same shape.
    /// </remarks>
    private const string HomePageName = "Home";

    /// <summary>
    /// The role identifier the schema reserves for "every user, signed in or not".
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is a RESERVED identifier rather than a row in the Roles table, which is why
    /// it is a constant here and not a lookup. It is used for exactly one grant - the home page's
    /// view permission - so that a brand-new tenant is reachable before any account has been
    /// enrolled in it.
    /// </remarks>
    private const int AllUsersRoleId = -1;

    /// <summary>
    /// The scope code under which the shipped page-permission catalogue is filed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The upgrade chain installs the page scope under this exact spelling - the insert is at
    /// <c>02.02.00.SqlDataProvider</c> - and <c>Permission.PermissionCode</c> is free text rather
    /// than an enumeration, so the catalogue is addressed by the code it was installed with. The
    /// constant lives here because THIS service resolves the two page keys its home page needs; the
    /// same spelling is held privately by the Infrastructure readers that filter on it, and neither
    /// copy is derived from the other because a shared constant would put installation reference
    /// data into a layer that owns none.
    /// </para>
    /// <para>
    /// MIGRATION: The catalogue is resolved BY THIS CODE and never by the page it is about to be
    /// granted on.
    /// </para>
    /// </remarks>
    private const string TabPermissionScopeCode = "SYSTEM_TAB";

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
    /// MIGRATION: this dependency exists because <c>FK_Modules_Portals</c> is the ONE foreign key
    /// to <c>dbo.Portals</c> that carries no <c>ON DELETE CASCADE</c> - every other one does
    /// (<c>PortalAlias</c>, <c>PortalDesktopModules</c>, <c>RoleGroups</c>, <c>Roles</c>,
    /// <c>Tabs</c>, <c>UserPortals</c>, <c>ProfilePropertyDefinition</c>).
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
    /// <param name="permissions">Resolves the catalogue entries the new tenant's home page is granted against.</param>
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
    /// Identifies the caller, which the update path needs in order to enforce the host-only rule the
    /// legacy settings screen applied to the hosting and quota fields.
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
    /// <remarks>
    /// Called only AFTER the change has been committed, so no record can describe a write that was
    /// later rolled back.
    /// <para>
    /// MIGRATION: the legacy entry copied the acting account's name into the logging store.
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

        // MIGRATION: the PER-COLLECTION ordering set is enforced HERE, not only at the API edge. The shared
        // request validator can apply nothing narrower than the union of every collection's set, because one
        // PagedRequest contract serves every listing and one validator is resolved for it, so on its own it
        // would admit a role-only or account-only field name for this listing and this read would then
        // quietly order by its default instead.
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
        // that read is keyed by them, and reused by the two tally reads further down, so the page's identity
        // set is established once.
        IReadOnlyCollection<int> pagePortalIds = page.Items
            .Select(portal => portal.PortalId)
            .Distinct()
            .ToList();

        // Aliases are not loaded by the listing read, so they are fetched once for THE TENANTS ON THIS PAGE
        // and grouped - not per row, and not for the whole installation.
        //
        // MIGRATION: the read is keyed by the page's own identifiers rather than being installation-wide,
        // and the difference is the whole point. This call site used to ask for every alias the installation
        // holds and then group the lot, of which it kept at most one group per row it was about to project -
        // so a request for a page of fifty tenants read, materialised and grouped the alias table of every
        // tenant in the installation, and the response gave no indication that it had.
        IReadOnlyList<PortalAlias> pageAliases = await _aliases
            .GetByPortalIdsAsync(pagePortalIds, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: dbo.PortalAlias.HTTPAlias permits null - the column is declared without a NOT NULL
        // clause at Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider - so the domain
        // property is nullable and the sentinel is restored here, at the boundary that publishes it. The
        // legacy listing hydrated the column with Convert.ToString(dr("HTTPAlias")).ToLower at
        // Library/Components/Portal/PortalAliasController.vb, and Convert.ToString of DBNull yields the
        // empty string, so a row holding no host name appeared in the list as an empty entry rather than
        // being dropped.
        Dictionary<int, List<string>> aliasesByPortal = pageAliases
            .GroupBy(alias => alias.PortalId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(alias => alias.HttpAlias ?? string.Empty)
                              .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                              .ToList());

        // MIGRATION: the legacy listing read its member and page tallies from correlated sub-selects inside
        // the portal view, so they were computed per row - but they were computed per row INSIDE ONE
        // STATEMENT, at no round-trip cost. Reproducing "per row" literally, by asking the single-portal
        // tally members once per row from here, would turn a page of fifty tenants into a hundred round
        // trips for figures the store can group in two, and would make the cost of the listing a function of
        // its page size.
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
            // contract violation rather than an expected state. It is still read defensively as zero: a
            // tenant with no members and no pages is legitimate, zero is what the legacy grid showed for it,
            // and failing an entire listing over one absent tally would be a worse answer than the figure
            // that absence implies.
            int users = usersByPortal.TryGetValue(portal.PortalId, out int userTally) ? userTally : 0;
            int pages = pagesByPortal.TryGetValue(portal.PortalId, out int pageTally) ? pageTally : 0;

            rows.Add(PortalMappings.ToListItem(portal, aliases, users, pages));
        }

        // MIGRATION: "return everything" is expressed by a NAMED FACTORY, not by a negative page index.
        // GetPortalsByName (PortalController.vb) declared that intent by passing pageIndex = -1, then
        // rewrote its own arguments to page 0 with a page size of Integer.MaxValue once it had detected the
        // sentinel.
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
        // policy of its own. The arithmetic is the legacy arithmetic: "DataCache.PortalCacheTimeOut *
        // Convert.ToInt32(Globals.PerformanceSetting)" at PortalController.vb, with the twenty-minute base
        // preserved as a named constant above and the installation-wide multiplier now bound configuration
        // rather than a static read of the excluded globals module.
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

        // MIGRATION: A CHILD PORTAL'S ALIAS IS COMPOSED, NOT STORED AS TYPED, and until now the flag was
        // accepted and never read - so a caller could ask for a child portal, be told it had one, and find
        // it unreachable. The legacy screen has two branches, both at Signup.ascx.vb:
        //
        //   - a request from a PORTAL page: child is forced, the typed value is validated against the child
        //     charset "a-z0-9-" and is therefore a bare SEGMENT, and the stored alias is
        //     GetDomainName(Request) & "/" & segment.
        //   - a request from a HOST page: child is the operator's choice, the typed value MAY already carry
        //     path separators, only its final segment is charset-validated
        //     (Mid(..., InStrRev(..., "/") + 1)), and the typed value is stored VERBATIM.
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
        // substrings. The tenant-resolution procedure was created as "where PortalAlias like '%' +
        // @PortalAlias + '%'" at Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider,
        // so a request for "example.com" also matched a stored "test.example.com.au" and the procedure then
        // took min(PortalID) among the matches.
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

        // MIGRATION: the legacy path learned why administrator creation had failed from an enum returned by
        // value - "Dim createStatus As UserCreateStatus = UserController.CreateUser(objAdminUser)" at
        // PortalController.vb - and tested it against a named member at before converting it to display
        // text. That enum does not cross this boundary.
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

        // MIGRATION: five FILE-SYSTEM stages of the legacy creation sequence are not reproduced, listed here
        // in the order they ran so the omission is auditable against the original. From PortalController.vb
        // deleted a pre-existing upload folder, reporting failure through the resource keyed
        // DeleteUploadFolder.Error, "Error deleting previous upload folder"; configured a child portal on
        // disc, reporting ChildPortal.Error, "Error configuring Child Portal"; created the home directory
        // and copied the tenant's resource file; parsed the portal and administration templates, reporting
        // PortalTemplate.Error, "Error parsing Portal Template", and AdminTemplate.Error, "Error parsing
        // Admin Template"; copied the default page template and synchronised the folder tree.

        // MIGRATION: the legacy screen rendered the literal placeholder "Portals/[PortalID]" in the home
        // directory box and deliberately did not submit it (Signup.ascx.vb), so the column was stored empty
        // and the effective directory was derived at request time by the excluded file-system layer. That is
        // reproduced exactly: whatever the request carries is stored verbatim, and an omitted directory is
        // stored as the empty string the column defaults.

        // ONE TRANSACTION SPANS THE WHOLE OF THE REST OF THIS MEMBER. Creating a tenant is not a single
        // write and cannot be made into one: three columns on the tenant row need keys the store assigns
        // during the first commit, and the administrator's CREDENTIAL lives in the ASP.NET membership
        // objects, which are mapped alongside rather than owned (Rule T4) and are written by statement
        // rather than by the change tracker.
        //
        // The transaction subsumes all of it. Both SaveChanges calls and the credential statement enlist in
        // it - the credential statement because the membership store borrows this unit of work's own
        // connection and binds the ambient transaction onto its command
        // (Infrastructure/Persistence/MembershipStore.cs, the shared command helper) - so there is no
        // cross-store boundary to compensate across.
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

        // Staged only. The alias commits in the same transaction as the portal, the roles, the pages and the
        // modules, which is why this member yields no key and why the alias is bound by navigation rather
        // than by an identifier the portal does not have yet.
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

        // MIGRATION: the instant comes from the injected clock, which is UTC-ONLY, where every legacy
        // reading came from VB's Now and was therefore in the SERVER'S LOCAL zone. The two differ by the
        // host's offset, so a value stored here can fall on a different calendar day from the one the legacy
        // code would have stored for the same real instant - west of UTC it can appear a day later, east of
        // it a day earlier.
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
        //
        // THIS COMMIT IS NOT DURABLE ON ITS OWN: it is enclosed by the transaction opened above, which is
        // committed only once the credential and the three stamped columns have been written too. That
        // enclosure is the whole of the fix.
        Result aliasFlush = await FlushCreationAsync(alias, administratorUsername, cancellationToken)
            .ConfigureAwait(false);
        if (aliasFlush.IsFailure)
        {
            return Result<PortalDetailDto>.Failure(aliasFlush.Reason!);
        }

        // MIGRATION: the legacy path had this same shape - insert the portal, create the administrator,
        // create the roles, then call UpdatePortalSetup to stamp the identifiers - and had no transaction
        // over any of it.
        {
            // MIGRATION: the administrator's password is HASHED here. The legacy path assigned it in
            // CLEARTEXT - "objAdminUser.Membership.Password = Password" at PortalController.vb - and the
            // membership provider that received it was registered with passwordFormat="Encrypted" and
            // enablePasswordRetrieval="true" (Website/release.config), a REVERSIBLE scheme whose 3DES
            // decryption key was itself committed to source control at Website/release.config.
            string passwordHash = _passwordHasher.Hash(password);
            bool credentialCreated = await _users
                .CreateCredentialAsync(administrator.UserId, passwordHash, isApproved: true, createdUtc, cancellationToken)
                .ConfigureAwait(false);

            if (!credentialCreated)
            {
                // Returning without committing rolls the transaction back, so the portal, its alias, its
                // three roles, its administrator, the membership and the three enrolments all disappear. No
                // compensation routine is called and none exists any more: the store reverses the work,
                // which is the one mechanism that also covers a terminated process.
                //
                // MIGRATION: the credential store is EXTERNAL to this transaction - the aspnet_Membership
                // objects are installed by the ASP.NET registration tool and are mapped alongside rather
                // than owned (Rule T4) - so a credential that WAS created and then rolled back around would
                // leave an orphan. That cannot arise on this path, because this branch is reached only when
                // the credential was NOT created. The failure path below covers the other case.
                return Result<PortalDetailDto>.Failure(
                    CreationFailedCode,
                    "The portal administrator's credential could not be created, so the portal was rolled back.");
            }

            // The two DATABASE-BACKED stages of the legacy creation sequence that this service used to omit.
            // Both are required for the tenant to be usable rather than merely present: without the
            // definitions no account in it can hold a profile at all, and without a home page it has nowhere
            // to serve.
            await CreateDefaultProfileDefinitionsAsync(portal.PortalId, cancellationToken)
                .ConfigureAwait(false);

            // MIGRATION: The page stage can now REFUSE, and the refusal is propagated rather than absorbed:
            // returning here disposes the transaction scope without committing, so the portal, its alias,
            // its three roles, its administrator, the credential and the profile definitions all disappear.
            Result<Tab> pageStage = await CreateHomePageAsync(portal, administratorsRole, cancellationToken)
                .ConfigureAwait(false);

            if (pageStage.IsFailure)
            {
                return Result<PortalDetailDto>.Failure(pageStage.Reason!);
            }

            Tab homePage = pageStage.Value;

            // Commits the definitions and the page, so the identifier the store assigns to the page is
            // readable for the stamp below.
            //
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

        // Reproduces DataCache.ClearHostCache(True) at PortalController.vb, which discarded the
        // installation-wide entries so the new tenant became reachable, plus the portal's own entry.
        _cache.InvalidateHost();
        _cache.InvalidatePortal(portal.PortalId);

        // MIGRATION: the legacy path had a THIRD invalidation here that is not reproduced, and it is
        // recorded rather than dropped in silence. PortalController.vb is DataCache.RemoveCache("GetRoles"),
        // evicting a single entry keyed by that bare literal, because creating a portal had just inserted
        // the three stock roles below and the role cache would otherwise have served a list that predated
        // them.
        PortalDetailDto? created = await ReadDetailAsync(portal.PortalId, cancellationToken).ConfigureAwait(false);
        if (created is null)
        {
            return Result<PortalDetailDto>.Failure(
                CreationFailedCode,
                "The portal was created but could not be read back.");
        }

        // MIGRATION: the legacy path closed with an AUDIT ENTRY that this service now emits through the
        // package-neutral sink; the original shape and the layering reason for the indirection are recorded
        // in full here. PortalController.vb built a Services.Log.EventLog.LogInfo with BypassBuffering set
        // to True - the entry was written through immediately rather than batched, because a failed
        // installation had to leave a trace - typed it as EventLogType.HOST_ALERT, attached FOURTEEN
        // properties and submitted it through EventLogController.AddLog.
        //
        // MIGRATION: of the fourteen legacy properties, the ones that survive are recorded below, and the
        // set is NARROWER than the original's. Four are NOT carried because they describe file-system work
        // this migration does not perform - TemplatePath, TemplateFile, ServerPath and ChildPath - so
        // recording them would assert something untrue about what happened.
        Dictionary<string, string?> installation = new(StringComparer.Ordinal)
        {
            // MIGRATION: THE TENANT NAME AND ALIAS ARE DELIBERATELY ABSENT, AND ONE REVISION RECORDED BOTH.
            // The argument for recording them is readability: a record naming "Contoso Intranet" is easier
            // to reconstruct an incident from than one naming tenant 42.
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

            // OPTIMISTIC CONCURRENCY, CHECKED BEFORE ANY OTHER RULE. The order is deliberate and matches the
            // role contract: a caller holding a stale snapshot must be told that the record moved under it,
            // not that some field of the snapshot it is trying to restore is now invalid or now names a page
            // that has since been removed - those are symptoms of the staleness rather than separate faults.
            //
            // MIGRATION: the legacy screen posted the whole record back with no version check of any kind
            // (SiteSettings.ascx.vb cmdUpdate_Click reads every control and calls the 27-argument save), so
            // one operator could silently destroy another's committed edit. Because this request replaces
            // EVERY column and roughly twenty of them are never displayed by the editing screen, the
            // destruction reached fields neither operator had opened: runtime testing saved from two sessions
            // and measured the earlier operator's changes gone with both saves answering 200. Rule T5
            // requires the departure from legacy behaviour to be recorded rather than absorbed, and it is, in
            // MIGRATION_NOTES.md. A request that omits the token is still applied, so no caller that predates
            // the token is refused.
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
        // rather than the removal itself. The guard counts the installation's tenants, judges the count, and
        // deletes in a later statement.
        //
        // The read of the tenant is inside the transaction as well, deliberately: a tenant removed by a
        // concurrent caller between that read and the count would otherwise be deleted twice, and the second
        // attempt would fail at the store rather than reporting the absence this contract names.
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

        // One page of size one is read purely for its total, because the repository exposes no bare count of
        // portals and an installation must retain at least one tenant. MIGRATION: the tally is read and
        // judged INSIDE this service, exactly as the legacy body did - "Dim portalCount As Integer =
        // DataProvider.Instance.GetPortalCount" followed by "If portalCount > 1 Then" at
        // PortalController.vb.
        PagedResult<Portal> firstPage = await _portals
            .ListAsync(0, 1, null, null, false, cancellationToken)
            .ConfigureAwait(false);

        if (firstPage.TotalCount <= 1)
        {
            // MIGRATION: the wording is preserved verbatim from the legacy resource the screen displayed,
            // Website/App_GlobalResources/SharedResources.resx keyed LastPortal.Text - "You Can Not Delete
            // The Last Portal In Your Database" - because the migration discipline requires error messages
            // to remain equivalent to the ones existing operators already recognise. The legacy member
            // reported this by RETURNING that string, with an empty string meaning success, so a caller had
            // to compare text to learn whether the delete had happened; here the outcome is a failure Result
            // whose stable code is what a caller branches on and whose message is what a human reads.
            return Result.Failure(
                LastRemainingCode,
                "You Can Not Delete The Last Portal In Your Database. The installation must retain at least one portal.");
        }

        // MIGRATION: four FILE-SYSTEM removal stages of the legacy delete are not reproduced, because the
        // file-system subsystem is outside this migration. In order, from PortalController.vb:
        // DeleteFilesRecursive over the server path matching ".Portal-<id>.resx" (the tenant's localised
        // resource overrides); a sweep that read the tenant's aliases, reduced each to a domain name and
        // deleted the matching child-portal directory; DeleteFolderRecursive over "Portals\<id>"; and, when
        // it existed, DeleteFolderRecursive over the tenant's mapped home directory.
        int releasedAliasCount = portal.PortalAliases.Count;

        // MIGRATION: THE TENANT'S MODULES ARE REMOVED FIRST, AND EXPLICITLY, because the store will not do
        // it. Every other foreign key into dbo.Portals is declared ON DELETE CASCADE, but FK_Modules_Portals
        // is not - the 03.00.09 upgrade script drops the constraint and re-adds it with no cascade clause,
        // which is the terminal state Rule T4 binds this model to.
        //
        // The legacy application solved this in exactly the same place and in exactly this order. The
        // terminal DeletePortalInfo procedure (04.04.00.SqlDataProvider) opens with "DELETE FROM Modules
        // WHERE PortalId = @PortalId" and only then deletes the Portals row, and the controller's own
        // history records the move - "[cnurse] 24/11/2006 Removal of Modules moved to sproc" at
        // PortalController.vb.
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

        // The token store is in process and cannot enlist in the database transaction. The serialisable
        // transaction is already open because the final-membership decision above has to be protected from
        // concurrent membership changes, but no relational removal has been staged yet.
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
                // relational removal staged above and leaves the account reachable rather than orphaning its
                // credential.
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
                // admitted super users and could therefore delete the installation's final operator.
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
            },
        });

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<PortalSettingsDto?>> GetPortalSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        // MIGRATION: this read is deliberately NOT CACHED, and the omission is recorded rather than left to
        // be noticed. Every value it returns is projected from columns of the portal row, so the legacy
        // Portal{id} entry would have served it - but this member exists to back the settings screen, whose
        // whole purpose is to show an administrator what is currently stored immediately after they have
        // changed it.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: there is NO portal-setting entity, and this projection is where that shows. A reader
        // meeting a "portal settings" contract would reasonably expect a key-value table behind it, and
        // there is none: no such member appears among the abstract data provider's methods, no such
        // procedure appears among the ones the provider invoked, and no such table appears in any of the
        // schema scripts - which define ModuleSettings, HostSettings, TabModuleSettings and
        // ScheduleItemSettings, but nothing portal-scoped.
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

        // The settings projection carries no aliases, so this path deliberately avoids loading them. The
        // general portal update still requests them because its response is the full detail contract.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result<PortalSettingsDto?>.Success(null);
        }

        // OPTIMISTIC CONCURRENCY, CHECKED BEFORE ANY OTHER RULE, for the reason given on UpdatePortalAsync: a
        // caller holding a stale snapshot must be told the record moved under it, not that a field of the
        // snapshot it is restoring is now refused or now names a page that has since been removed.
        //
        // The check is on THIS path as well as on UpdatePortalAsync because both paths hand the same request
        // interface to the same mapper and replace the same twenty-five columns. Protecting one and leaving
        // the other would move the lost-update surface to the sibling route rather than remove it, and this
        // is the route the settings screen uses. The token is derived by one function for both reads, so a
        // token obtained from either screen verifies here.
        if (IsWritingOverSomeoneElsesEdit(portal, request))
        {
            return Result<PortalSettingsDto?>.Failure(
                ConcurrencyConflictCode,
                DescribeConcurrencyConflict(portalId));
        }

        // Both public update resources replace the same stored row and therefore share the same
        // content-sensitive authorisation and aggregate invariant. Keeping the guards on the shared request
        // interface prevents the settings route from becoming a less protected way to write the fields
        // already defended by UpdatePortalAsync.
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
    public async Task<Result<IReadOnlyList<PortalAdministratorDto>?>> ListAdministratorCandidatesAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        // Not cached, on the same reasoning as the settings read directly above: this backs an editing
        // form, and a stale candidate list would either hide an administrator promoted a moment ago or
        // offer one who has just been removed from the role.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result<IReadOnlyList<PortalAdministratorDto>?>.Success(null);
        }

        // A portal with no administrator role designated has no candidates to offer, and that is an empty
        // answer rather than a failure - the write path's own guard is what refuses a designation, and it
        // permits any account belonging to the portal. Tested against null rather than for a positive
        // number: RoleID seeds IDENTITY (0, 1), so zero is a real role.
        if (portal.AdministratorRoleId is not int administratorRoleId)
        {
            return Result<IReadOnlyList<PortalAdministratorDto>?>.Success(
                Array.Empty<PortalAdministratorDto>());
        }

        // The ROLE IS RESOLVED FROM ITS KEY AND THE MEMBERSHIP READ BY ITS NAME, which looks indirect and
        // is not. The legacy screen passed objPortal.AdministratorRoleName - a value the terminal read view
        // supplies through a join rather than as a constant, precisely because a tenant may rename the
        // role - and the membership read the legacy reached for is name-keyed. Starting from the stored key
        // reproduces that without a literal role name anywhere, so a renamed role still yields its members.
        Role? administratorsRole = await _roles
            .GetByIdAsync(administratorRoleId, portalId, cancellationToken)
            .ConfigureAwait(false);
        if (administratorsRole is null)
        {
            return Result<IReadOnlyList<PortalAdministratorDto>?>.Success(
                Array.Empty<PortalAdministratorDto>());
        }

        // Exactly the legacy GetUserRolesByRoleName(portalId, roleName): the login-name argument is null,
        // which the contract documents as "every account in the portal", leaving the role name as the only
        // narrowing. The account behind each assignment is materialised by that read, so the display and
        // login names below cost no further query.
        IReadOnlyList<UserRole> memberships = await _roles
            .GetUserRolesByUsernameAsync(portalId, username: null, administratorsRole.RoleName, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PortalAdministratorDto> candidates = memberships
            .Select(PortalMappings.ToAdministratorCandidate)

            // DISTINCT BY ACCOUNT, because the relation permits one account to hold one role more than once
            // - UserRoles.UserRoleID is the surrogate and no unique constraint spans the account and role
            // pair - and a selector offering the same person twice invites the operator to wonder which of
            // the two they picked. The legacy loop added one entry per assignment and could show duplicates.
            .GroupBy(candidate => candidate.UserId)
            .Select(group => group.First())

            // Ordered by the text the selector shows, so the list reads the way it is displayed. The
            // membership read orders by role and then by assignment key, which is the right order for a
            // membership grid and the wrong one for a name picker. The login name breaks a tie between two
            // accounts sharing a display name, which is the collision the login name is published for.
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

        // The optional scope is resolved here rather than pushed into the repository as a nullable filter:
        // asking for one portal's aliases and asking for every alias in the installation are different
        // questions, and each has its own member.
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
    public async Task<Result<PortalAliasDto?>> GetPortalAliasAsync(
        int? portalId,
        int portalAliasId,
        CancellationToken cancellationToken = default)
    {
        PortalAlias? alias = await _aliases.GetByIdAsync(portalAliasId, cancellationToken).ConfigureAwait(false);

        // The owning portal is verified rather than assumed. PortalAlias.PortalAliasID is a surrogate
        // declared IDENTITY (1, 1) and is therefore unique across the installation and guessable across
        // tenants, so a read keyed by it alone discloses every tenant's host bindings to anybody authorised
        // over any one tenant.
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

        // MIGRATION: the write takes a dedicated request rather than the read representation. The read DTO
        // carries the alias key and the portal key, both of which the caller would then be able to set - the
        // alias key is ignored on a create and the portal key is taken from the route, so a caller supplying
        // either would be silently overruled while believing it had been honoured.
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

        // A freshly bound alias cannot be the one this request resolved through - resolution happened before
        // it existed - so the flag comes back false. It is still resolved rather than hard-coded, because a
        // literal here would be a second statement of the rule, free to disagree with the one above the day
        // the read path changes.
        return Result<PortalAliasDto>.Success(PortalMappings.ToDto(created, CurrentPortalAliasId()));
    }

    /// <inheritdoc />
    public async Task<Result> UpdatePortalAliasAsync(
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

        // The addressed row must belong to the addressed tenant. This is the most consequential of the three
        // ownership checks on this contract: an alias is what tenant resolution matches on, so renaming
        // somebody else's alias re-points their portal's traffic.
        if (stored is null || (portalId is int scopedPortalId && stored.PortalId != scopedPortalId))
        {
            return Result.Failure(AliasNotFoundCode, $"No portal alias bears identifier {portalAliasId}.");
        }

        // MIGRATION: the alias the CURRENT REQUEST resolved through cannot be renamed, which restores the
        // legacy screen's IsNotCurrent affordance (PortalAlias.ascx.vb) as an enforced rule rather than a
        // hidden control. Refused AFTER ownership is settled and BEFORE the duplicate check, because the
        // answer does not depend on the submitted host name at all: a rename to the value the row already
        // holds is still a write against the row resolution is using, and reporting a duplicate first would
        // explain the wrong thing.
        if (IsCurrentPortalAlias(portalAliasId))
        {
            return Result.Failure(
                AliasInUseConflictCode,
                "This host name is the one the current request reached the portal through, so it cannot " +
                "be renamed. Reach the portal through one of its other host names and try again.");
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

        // Only the host name is written. The portal an alias is bound to is deliberately not moved here:
        // this member declares no portal-not-found reason code, which is itself the statement that it does
        // not re-validate, and therefore does not re-bind, the tenant.
        stored.HttpAlias = httpAlias;

        // MIGRATION: the explicit counterpart of legacy UpdatePortalAliasInfo, which was the real per-row
        // update. Stated rather than left to the change tracker so that the intention to write this row is
        // visible at the call site, and so the same code is correct for an alias that was not read here.
        await _aliases.UpdateAsync(stored, cancellationToken).ConfigureAwait(false);

        // A rename races exactly as a binding does - the check reads, another request binds, the rename is
        // refused by the index. Same code and wording as the check above, for the reason recorded in full on
        // the create path.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            return Result.Failure(
                AliasDuplicateCode,
                $"The host name '{httpAlias}' is already bound to another portal alias.");
        }

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
        // Unbinding another tenant's alias would make that tenant unreachable at the host name its users
        // hold, which is a denial of service reached from a grant over an unrelated portal.
        if (stored is null || (portalId is int scopedPortalId && stored.PortalId != scopedPortalId))
        {
            return Result.Failure(AliasNotFoundCode, $"No portal alias bears identifier {portalAliasId}.");
        }

        // MIGRATION: removal of the alias the CURRENT REQUEST resolved through is refused as well, and this
        // half goes BEYOND the legacy screen rather than reproducing it. Legacy hid the EDIT affordance for
        // the current row and left removal governed only by a count - SetDeleteVisibility at
        // EditPortalAlias.ascx.vb hid the button when the portal held one alias or fewer - so an operator on
        // a portal with several aliases could unbind the very one they had arrived through.
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

        // MIGRATION: both tallies are AWAITED EXPLICITLY here because they were LAZY PROPERTY GETTERS on the
        // legacy record - Users at PortalInfo.vb and Pages at - each of which issued a synchronous database
        // read the first time it was touched, memoised the answer in a backing field, and used -1 in that
        // field to mean "not loaded yet". Three problems came with that shape and all three are removed
        // rather than carried.
        int users = await _portals.CountUsersAsync(portalId, cancellationToken).ConfigureAwait(false);
        int pages = await _portals.CountPagesAsync(portalId, cancellationToken).ConfigureAwait(false);

        // MIGRATION: the two role names were correlated sub-selects in the legacy portal view (04.05.00), so
        // they are read-only projections here rather than stored columns.
        IReadOnlyDictionary<int, string> roleNames = await _portals
            .GetRoleNamesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // Each designated role is resolved only when the portal actually designates one. The test is for a
        // value being present, never for a particular number: Roles.RoleID is declared IDENTITY (0, 1), so
        // zero is the first role an installation creates and -1 is a legitimate key as well, which means no
        // numeric comparison can stand in for absence here.
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
            // The detail contract carries the portal's aliases in full, so each of them is told which one
            // the current request resolved through for the same reason the alias collection endpoint is: a
            // screen rendering this projection must be able to withhold the affordance on that row.
            CurrentPortalAliasId());
    }

    /// <summary>
    /// Flushes one stage of tenant creation, reporting a lost race for a unique value as the
    /// conflict it is.
    /// </summary>
    /// <param name="alias">The host name being bound, so the refusal can name it.</param>
    /// <param name="administratorUsername">
    /// The account name being taken, so the refusal can name it.
    /// </param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>Success, or the conflict the store refused.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: Tenant creation checks the alias and the account name before writing either, and
    /// neither check can close the window between itself and the flush: two requests naming the
    /// same alias both read "free", and the loser is refused by the unique index.
    /// </para>
    /// <para>
    /// EVERY flush in the sequence goes through here, including the ones that stage nothing
    /// unique-indexed today.
    /// </para>
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
    /// <para>
    /// MIGRATION: reproduces the private two-argument <c>CreatePortal</c> exactly. A blank setting
    /// yields zero for the monetary and quota values, no expiry for a blank trial length, and an
    /// absent retention period where the legacy code used its -1 sentinel.
    /// </para>
    /// <para>
    /// MIGRATION: the GUARD IS INVERTED on all seven reads, and the inversion is what makes them
    /// behave identically rather than differently. Every legacy guard tested
    /// <c>If Convert.ToString(HostSettings(key)) &lt;&gt; ""</c>, comparing against the EMPTY
    /// STRING, because the legacy null contract at <c>Library/Components/Shared/Null.vb</c> defines
    /// its absent-String marker as <c>""</c> and not as null, and <c>Convert.ToString</c> of a
    /// database null yields <c>""</c> as well - so one comparison covered a missing key, a null
    /// value and a blank value alike.
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

        // The retention period is the one setting whose absence is NOT zero. The legacy code carried its -1
        // sentinel here, and -1 meant "keep for ever" rather than "keep for minus one day", so collapsing it
        // to zero would silently turn unlimited retention into none. It stays absent.
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
    /// <param name="roleName">
    /// The role name, preserved verbatim from the legacy creation path.
    /// </param>
    /// <param name="description">The role description, preserved verbatim.</param>
    /// <param name="isPublic">Whether members may subscribe to the role themselves.</param>
    /// <param name="autoAssignment">Whether new members receive the role automatically.</param>
    /// <returns>An unsaved role aggregate.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy path created each role through a private helper that clamped a
    /// negative fee to zero and passed a monthly billing frequency with a zero period and no trial.
    /// Those values are reproduced literally, and the shared clamp is reused so that the rule lives
    /// in exactly one place.
    /// </para>
    /// <para>
    /// MIGRATION: the clamp replaces the VB runtime's inline-conditional intrinsic, and the
    /// substitution needs justifying because it is NOT valid in general. A maximum against zero is
    /// used rather than a conditional because it states the intent - never below zero - in one
    /// term.
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
    /// Surrogate key of the alias the CURRENT REQUEST resolved through, or <see langword="null"/>
    /// when the request resolved no tenant.
    /// </summary>
    /// <returns>
    /// The resolved alias key, or <see langword="null"/> when no tenant was resolved.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the target's equivalent of
    /// <c>PortalSettings.PortalAlias.PortalAliasID</c>, the single fact the legacy alias
    /// administration screen read from the resolved alias entity — <c>IsNotCurrent</c> at
    /// <c>Website/admin/Portal/PortalAlias.ascx.vb</c>.
    /// </para>
    /// <para>
    /// WHY THE ANSWER IS THE SERVER'S TO GIVE AND NOT THE BROWSER'S TO INFER. Resolution matches
    /// the request's host, port included, against stored aliases exactly and refuses an ambiguous
    /// match; a reverse proxy may present the API with a host the browser never saw; and stored
    /// casing need not match what a caller submitted, because the legacy write path lower-cased
    /// while its reader did not.
    /// </para>
    /// </remarks>
    private int? CurrentPortalAliasId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalAliasId : null;

    /// <summary>Whether one alias is the alias the CURRENT REQUEST resolved through.</summary>
    /// <param name="portalAliasId">The alias key to test.</param>
    /// <returns><see langword="true"/> when it is the resolved alias.</returns>
    /// <remarks>
    /// Compared for EQUALITY against the resolved key, never by magnitude and never by truthiness.
    /// <c>PortalAlias.PortalAliasID</c> is <c>IDENTITY (1, 1)</c> so no legal key collides with the
    /// legacy absent-integer sentinel, but the discipline is applied anyway because every sibling
    /// key this service handles — portal, role, page and module — is seeded at zero or minus one
    /// and is compared by the same code paths.
    /// </remarks>
    private bool IsCurrentPortalAlias(int portalAliasId) =>
        CurrentPortalAliasId() is int resolved && resolved == portalAliasId;

    /// <summary>Resolves the host name a new portal will actually be reachable at.</summary>
    /// <param name="request">The submitted creation request, read for <c>IsChildPortal</c>.</param>
    /// <param name="submittedAlias">The trimmed value the caller submitted.</param>
    /// <returns>
    /// A success carrying the alias to store, or a failure carrying
    /// <see cref="ParentAliasUnresolvedCode"/> when a child portal was asked for from a request
    /// that resolved to no tenant.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this member is where the <c>IsChildPortal</c> flag becomes observable.
    /// </para>
    /// <para>
    /// The parent authority is the resolved tenant's OWN alias, which is this target's equivalent
    /// of <c>Globals.GetDomainName(Request)</c> (<c>Library/Components/Shared/Globals.vb</c>,
    /// implemented). And it already carries any path portion the request was addressed under,
    /// because resolution prefers the longest matching prefix — which reproduces the legacy
    /// member's own behaviour of returning <c>www.domain.com/directory</c> rather than the bare
    /// host when the request arrived beneath a sub-directory, and so nests exactly as the legacy
    /// screen nested.
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
            // The legacy HOST branch: the operator qualified the address themselves, so it is stored
            // verbatim. The validator has already character-checked its final segment.
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
    /// <para>
    /// ONE GUARD FOR BOTH PORTAL WRITE PATHS, on purpose. <c>UpdatePortalAsync</c> and
    /// <c>UpdatePortalSettingsAsync</c> hand the same request interface to the same mapper and replace the
    /// same twenty-five columns, so they share one lost-update surface and must share one remedy; two copies
    /// of this comparison would eventually disagree.
    /// </para>
    /// <para>
    /// A CALLER THAT SUPPLIES NO TOKEN IS NOT REFUSED. <see cref="ConcurrencyToken.Matches"/> treats a null
    /// or blank submitted token as a match, which is what keeps every caller written before the token existed
    /// working - see the member documentation on <c>IPortalSettingsUpdateRequest.ConcurrencyToken</c> for why
    /// that trade is made explicitly rather than silently.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy screen posted the whole record back with no version check of any kind
    /// (<c>SiteSettings.ascx.vb</c> <c>cmdUpdate_Click</c> reads every control and calls the
    /// twenty-seven-argument save), so one operator silently destroyed another's committed edit - and because
    /// the payload replaces every column while the editing screen displays only some of them, the destruction
    /// reached fields neither operator had opened. Rule T5 requires the departure from legacy behaviour to be
    /// recorded rather than absorbed, and it is, in MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    private static bool IsWritingOverSomeoneElsesEdit(Portal portal, IPortalSettingsUpdateRequest request)
        => !ConcurrencyToken.Matches(
            request.ConcurrencyToken,
            PortalMappings.ConcurrencyTokenFor(portal));

    /// <summary>
    /// Composes the explanation a refused stale portal write reports.
    /// </summary>
    /// <param name="portalId">The portal the caller addressed.</param>
    /// <returns>The explanation.</returns>
    /// <remarks>
    /// Shared by both write paths so the two cannot drift into reporting the same condition differently, which
    /// is also why it says "reload the portal" rather than naming one of the two screens: runtime testing showed
    /// the same sentence reaching an operator on the portal EDIT form as well as on the Site Settings screen, and
    /// an instruction naming the wrong screen is worse than a general one. It
    /// names the state that changed and the action that recovers from it, and it discloses nothing about WHICH
    /// field moved - the token is opaque, and a caller that could infer the changed column from a refusal
    /// would be reading another operator's edit through an error message.
    /// </remarks>
    private static string DescribeConcurrencyConflict(int portalId)
        => FormattableString.Invariant(
            $"Portal {portalId} was changed by someone else after you read it, so nothing was written. Reload the portal to see the current values, then apply your change again.");

    /// <summary>
    /// Refuses an update in which a caller who is not a host account has altered a host-only field.
    /// </summary>
    /// <param name="portal">The stored portal.</param>
    /// <param name="request">The submitted values.</param>
    /// <param name="cancellationToken">
    /// Abandons the authority read when the caller disconnects.
    /// </param>
    /// <returns>A task that completes when the request has been admitted.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when a non-host caller has altered the hosting charge, the disc-space quota, the page
    /// quota, the member quota, the site-log retention period or the expiry date.
    /// </exception>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy settings screen compared exactly these six submitted values against
    /// the stored portal and refused the whole save when a non-super-user had changed any of them
    /// (<c>Website/admin/Portal/SiteSettings.ascx.vb</c>). It is reported by exception rather than
    /// by reason code because the member documents no failure code for it, and the API edge
    /// translates the exception into a single forbidden response.
    /// </para>
    /// <para>
    /// Every term is compared against the value the update is actually going to WRITE, term for
    /// term with <see cref="PortalMappings.ApplyUpdate"/>, and no arithmetic is applied to either
    /// side. That agreement is the whole soundness argument: the write applies no floor to the
    /// hosting charge or to any allowance - the legacy save path applied none either, and the
    /// mapper records why - so flooring them HERE would compare a coerced submission against a
    /// stored value and admit exactly the change this rule exists to refuse.
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
    /// An omitted administrator means "leave it as it is", which is why the guard tests the STORED
    /// value as well as the submitted one: a portal that already has none is not made worse by an
    /// update that supplies none, whereas clearing a designated administrator would leave the
    /// tenant with no account able to administer it and no route back other than a host-level
    /// repair.
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
    /// Reports whether the caller is a host account according to the store, rather than according
    /// to the claim its token carries.
    /// </summary>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>
    /// <see langword="true"/> when the caller's stored account is a host account.
    /// </returns>
    /// <remarks>
    /// <para>
    /// EVERY ARM OF THIS FAILS CLOSED. A caller whose account is absent from the store - deleted
    /// since sign-in, or a token minted for an identifier that never existed - is not a host
    /// account either, because a missing record cannot evidence authority.
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
    /// Validates every account and page reference before an update can be mapped onto the tracked
    /// portal.
    /// </summary>
    /// <param name="portal">The stored portal, read before anything is applied.</param>
    /// <param name="request">The submitted update.</param>
    /// <param name="cancellationToken">
    /// Abandons the ownership reads when the caller disconnects.
    /// </param>
    /// <returns>A successful result when every reference belongs to the addressed portal.</returns>
    /// <remarks>
    /// <para>
    /// The legacy screen populated the administrator and four page selectors from this portal's own
    /// records, but the API accepts identifiers directly. A foreign identifier is therefore
    /// rejected here rather than trusted until a database constraint fails; the page columns do not
    /// even carry foreign keys in every supported schema.
    /// </para>
    /// <para>
    /// Each page is tested through <see cref="IPortalRepository.TabBelongsToPortalAsync"/>, whose
    /// false result deliberately covers both absence and another tenant's row.
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

    /// <summary>Trims a submitted host name and refuses a blank one.</summary>
    /// <param name="httpAlias">The submitted host name.</param>
    /// <returns>The trimmed host name.</returns>
    /// <exception cref="DomainException">Thrown when the host name is absent or blank.</exception>
    /// <remarks>
    /// Case is preserved, because the legacy screen stored the alias exactly as typed and the
    /// unique index on the column is what enforces distinctness.
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
    /// Reads a host setting as a whole number, treating a missing, blank or unparsable value as
    /// absent.
    /// </summary>
    /// <param name="settings">The host settings.</param>
    /// <param name="name">The setting name.</param>
    /// <returns>
    /// The parsed value, or <see langword="null"/> when the setting was not usable.
    /// </returns>
    /// <remarks>
    /// Absence is returned as a nullable value rather than reported through an output argument, so
    /// this member states its own outcome in its return type and every caller must decide what an
    /// absent setting means for the value it is computing. That matters here because the answer is
    /// not uniform: four of the settings this reads default to zero and one defaults to absent.
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
    /// Reads a host setting as a monetary amount, treating a missing, blank or unparsable value as
    /// absent.
    /// </summary>
    /// <param name="settings">The host settings.</param>
    /// <param name="name">The setting name.</param>
    /// <returns>
    /// The parsed value, or <see langword="null"/> when the setting was not usable.
    /// </returns>
    /// <remarks>
    /// Absence is returned as a nullable value for the reason given on its whole-number
    /// counterpart. Parsing is culture invariant so that a stored amount is not reinterpreted by
    /// the host's locale - a decimal separator read under the wrong culture would change the amount
    /// by orders of magnitude.
    /// </remarks>
    private static decimal? ReadDecimal(IReadOnlyDictionary<string, string> settings, string name)
    {
        string? raw = settings.GetValueOrDefault(name);

        return !string.IsNullOrWhiteSpace(raw)
            && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
                ? parsed
                : null;
    }

    /// <summary>The installation defaults a new portal inherits from host configuration.</summary>
    /// <param name="Currency">Default currency code.</param>
    /// <param name="ExpiryDate">
    /// Default expiry instant, or <see langword="null"/> for no expiry.
    /// </param>
    /// <param name="HostFee">Default monthly hosting charge.</param>
    /// <param name="HostSpace">Default disc-space quota in whole megabytes.</param>
    /// <param name="PageQuota">Default page quota.</param>
    /// <param name="UserQuota">Default member quota.</param>
    /// <param name="SiteLogHistory">
    /// Default site-log retention in days, or <see langword="null"/> for none.
    /// </param>
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
    /// (<c>Library/Components/Users/Profile/ProfileController.vb</c>), which the legacy creation
    /// sequence reached through <c>CreateProfileDefinitions</c> at <c>PortalController.vb</c>. The
    /// four categories, the nineteen names and their order are transcribed from that method rather
    /// than chosen: five under Name, six under Address, five under Contact Info and three under
    /// Preferences.
    /// </para>
    /// <para>
    /// The view order is 3, 5, 7 and so on, and that is not an off-by-one. Renumbering them from 1
    /// would change the order every profile screen renders them, so the sequence is preserved
    /// exactly.
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
    /// <param name="administratorsRole">
    /// The tenant's administrators role, which receives the edit grant.
    /// </param>
    /// <param name="token">Token observed while the page and its grants are staged.</param>
    /// <returns>
    /// The staged page, whose identifier becomes the tenant's home page once it is committed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A tenant with no page has nowhere to serve, so this is the minimum that makes one usable.
    /// </para>
    /// <para>
    /// MIGRATION: the consequence is stated plainly.
    /// </para>
    /// </remarks>
    private async Task<Result<Tab>> CreateHomePageAsync(
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
            // legacy parity rather than leniency. The legacy template parser resolved each key through
            // PermissionController.GetPermissionByCodeAndKey and then iterated the answer
            // (ParseTabPermissions, reached from PortalController.CreatePortal at
            // Library/Components/Portal/PortalController.vb:L980) - an empty answer produced an empty loop,
            // so the page was created with no grant and the portal came into being regardless. Refusing
            // instead turned a foreseeable installation condition into the API's only 5xx and made tenant
            // provisioning impossible on any database whose catalogue rows are absent, which is a
            // behavioural regression against the system being migrated.
            //
            // The condition is still recorded loudly, because it IS an installation defect that an operator
            // must repair - the upgrade scripts own these rows - and a silently permission-less home page
            // would otherwise be discovered much later. AuditOutcome.Failed is the declared member for
            // precisely this shape - "the operation was permitted but could not be completed ... where
            // proceeding is correct and the failure must still leave a trace" (AuditEvent.cs) - and it is
            // the grant, not the create, that could not be completed. Whatever the catalogue DOES declare
            // is still granted below, so a catalogue missing only EDIT still yields the two VIEW grants.
            RecordAudit(new AuditEvent(AuditEventNames.PortalCreated)
            {
                Outcome = AuditOutcome.Failed,
                PortalId = portal.PortalId,
                ResourceType = PortalResourceType,
                ResourceId = portal.PortalId.ToString(CultureInfo.InvariantCulture),
                FailureCode = PermissionCatalogueIncompleteCode,
                Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["PermissionCode"] = TabPermissionScopeCode,
                    ["MissingViewDefinition"] = (viewDefinition is null).ToString(CultureInfo.InvariantCulture),
                    ["MissingEditDefinition"] = (editDefinition is null).ToString(CultureInfo.InvariantCulture),
                },
            });
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

        return Result<Tab>.Success(homePage);
    }

    /// <summary>Resolves one page-scope permission definition by the key it grants.</summary>
    /// <param name="permissionKey">The key whose definition is wanted.</param>
    /// <param name="token">Token observed while the catalogue is read.</param>
    /// <returns>
    /// The definition, or <see langword="null"/> when the catalogue does not declare the key.
    /// </returns>
    /// <remarks>
    /// The scope code and the key together are how the shipped catalogue is addressed, and the
    /// uniqueness rule on that table spans the scope code, the owning definition and the key - so
    /// one code-and-key pair may legitimately exist once per module definition.
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

    /// <summary>
    /// Stages one page permission grant against a definition the caller has already resolved.
    /// </summary>
    /// <param name="homePage">The page receiving the grant.</param>
    /// <param name="definition">The catalogue definition the grant references.</param>
    /// <param name="roleId">The role receiving it.</param>
    /// <param name="token">Token observed while the grant is staged.</param>
    /// <remarks>
    /// The page is bound by NAVIGATION rather than by identifier, because the page has no
    /// identifier until the commit that follows; the object graph resolves the foreign key for both
    /// rows in one write.
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
