// MIGRATION: this service replaces the three legacy permission controllers -
// Library/Components/Security/Permissions/PermissionController.vb (9 public members),
// ModulePermissionController.vb (18) and TabPermissionController.vb (15) - which between them
// duplicated the same permission-string evaluation three times over.
//
// MIGRATION: allow-and-deny precedence is NOT re-derived here. The permission repository owns the
// single evaluator, and its contract states the rule it applies: a denying entry suppresses the key
// even when another entry allows it. Re-implementing that ordering in this layer would create a second
// evaluator that could disagree with the first, which is precisely the duplication the three legacy
// controllers suffered from.
//
// MIGRATION: what this service does own is the caller-to-role-names rule, because that rule is business
// logic rather than a query. It reproduces PortalSecurity.IsInRoles (PortalSecurity.vb:L114-L134)
// exactly: a host account holds every permission unconditionally; the "All Users" pseudo-role applies to
// every caller, authenticated or not; and the "Unauthenticated Users" pseudo-role applies only while the
// caller is anonymous. The legacy member read the caller from ambient request state and from a cookie it
// wrote at PortalSecurity.vb:L98; here the caller is always named by argument.
//
// MIGRATION: the legacy grant-management members - adding, updating and deleting individual
// access-control entries - are deliberately not exposed on this contract. Grant editing belonged to the
// permission-grid control that the excluded control library supplied, and no admin screen in scope
// reaches it. The repository still declares the staging members, so the capability is present in the
// layer that owns it whenever a grant-editing surface is added.
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>
/// Answers permission questions within a portal: what the catalogue defines, what a caller effectively
/// holds, whether a caller holds one named key on one module or page, and the cleanup a deleted account
/// requires.
/// </summary>
/// <remarks>
/// <para>
/// Every member is tenant-scoped and every caller is named by argument, so no answer here depends on
/// ambient request state.
/// </para>
/// <para>
/// This service resolves the caller into a set of role names and then delegates the evaluation itself to
/// the permission repository, which owns the single allow-and-deny evaluator. It therefore contains the
/// rules about <em>who the caller is</em> and none of the rules about <em>how grants combine</em>.
/// </para>
/// </remarks>
public sealed class PermissionService : IPermissionService
{
    /// <summary>
    /// Reported when a catalogue filter is supplied in a form that cannot match anything.
    /// </summary>
    private const string FilterInvalidCode = "permission.filter_invalid";

    /// <summary>
    /// Reported when the named portal does not exist.
    /// </summary>
    private const string PortalNotFoundCode = "permission.portal_not_found";

    /// <summary>
    /// Reported when the named module does not exist within the portal.
    /// </summary>
    private const string ModuleNotFoundCode = "permission.module_not_found";

    /// <summary>
    /// Reported when the named page does not exist within the portal.
    /// </summary>
    private const string TabNotFoundCode = "permission.tab_not_found";

    /// <summary>
    /// Reported when the named caller does not exist.
    /// </summary>
    private const string UserNotFoundCode = "permission.user_not_found";

    /// <summary>
    /// Reported when the submitted key is not a defined member of the closed key set.
    /// </summary>
    private const string KeyInvalidCode = "permission.key_invalid";

    /// <summary>
    /// Lowest value <c>dbo.ModuleDefinitions.ModuleDefID</c> can take, the column being
    /// <c>IDENTITY (1, 1)</c>, so a smaller value cannot name a row.
    /// </summary>
    private const int LowestModuleDefinitionId = 1;

    /// <summary>
    /// The view key, named once because the inherit-view rule is the only place a key is special-cased.
    /// </summary>
    private const string ViewPermissionKey = "VIEW";

    /// <summary>
    /// Page size that requests every match unpaged, per the repository contracts.
    /// </summary>
    private const int UnpagedPageSize = 0;

    /// <summary>
    /// Every permission key the schema can hold, which is the enumeration itself.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>Permission.PermissionKey</c> is a closed enumeration whose member names are the
    /// stored <c>varchar(50)</c> values, so this sequence is the complete key vocabulary by
    /// construction rather than by observation - no catalogue row can carry a key outside it. That is
    /// what lets the unfiltered catalogue question and the host-account answer be settled without a
    /// round trip.
    /// </remarks>
    private static readonly IReadOnlyList<PermissionKey> AllPermissionKeys = Enum.GetValues<PermissionKey>();

    private readonly IPermissionRepository _permissions;
    private readonly IPermissionEvaluator _evaluator;
    private readonly IPortalRepository _portals;
    private readonly IModuleRepository _modules;
    private readonly ITabRepository _tabs;
    private readonly IUserRepository _users;
    private readonly IClock _clock;
    private readonly PortalOptions _portalOptions;

    /// <summary>
    /// Initialises the service with the collaborators it resolves callers and scopes through.
    /// </summary>
    /// <param name="permissions">
    /// The permission aggregate's persistence contract: the catalogue and the grant rows. It answers
    /// no access question, which is why the evaluator is a separate collaborator.
    /// </param>
    /// <param name="evaluator">
    /// The single authority on allow-and-deny precedence. This service resolves the caller into a set
    /// of role names and then asks the evaluator what the caller consequently holds, so it owns the
    /// rules about <em>who the caller is</em> and none of the rules about <em>how grants combine</em>.
    /// </param>
    /// <param name="portals">Portal existence.</param>
    /// <param name="modules">Module existence and, for the inherit-view rule, module placement.</param>
    /// <param name="tabs">Page existence.</param>
    /// <param name="users">Caller resolution and the caller's role names.</param>
    /// <param name="clock">The instant role assignments are evaluated as of.</param>
    /// <param name="portalOptions">Bound configuration supplying the two pseudo-role names.</param>
    public PermissionService(
        IPermissionRepository permissions,
        IPermissionEvaluator evaluator,
        IPortalRepository portals,
        IModuleRepository modules,
        ITabRepository tabs,
        IUserRepository users,
        IClock clock,
        PortalOptions portalOptions)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _portalOptions = portalOptions ?? throw new ArgumentNullException(nameof(portalOptions));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The two filters compose conjunctively, so supplying neither returns the whole catalogue. The scope
    /// code is deliberately matched as free text rather than against an enumeration, because the column it
    /// lives in is free text and an installation carrying a code this codebase has never seen must still
    /// round-trip intact.
    /// </remarks>
    public async Task<Result<IReadOnlyList<string>>> GetPermissionKeysAsync(
        string? permissionCode = null,
        int? moduleDefinitionId = null,
        PermissionKey? permissionKey = null,
        CancellationToken cancellationToken = default)
    {
        if (permissionCode is not null && string.IsNullOrWhiteSpace(permissionCode))
        {
            return Result<IReadOnlyList<string>>.Failure(
                FilterInvalidCode,
                "The permission code filter must not be blank; omit it to place no restriction.");
        }

        if (moduleDefinitionId is int definitionId && definitionId < LowestModuleDefinitionId)
        {
            return Result<IReadOnlyList<string>>.Failure(
                FilterInvalidCode,
                FormattableString.Invariant(
                    $"Module definition {definitionId} cannot name a row; identifiers start at {LowestModuleDefinitionId}."));
        }

        IReadOnlyList<string> catalogueKeys =
            await ReadCatalogueKeysAsync(permissionCode, moduleDefinitionId, permissionKey, cancellationToken)
                .ConfigureAwait(false);

        // MIGRATION: Permission.PermissionKey is the closed PermissionKey enumeration, whose member
        // NAME is the stored and wire value, so the projection is ToString() rather than a lookup and
        // it can only ever yield VIEW, EDIT, READ or WRITE. Normalise still runs: it de-duplicates the
        // catalogue and imposes the ordinal ordering the contract promises, and it is the same
        // treatment the grant-derived answers below receive, which do arrive as arbitrary column text.
        return Result<IReadOnlyList<string>>.Success(Normalise(catalogueKeys));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Supplying neither scope answers at portal level - the union of everything the caller holds anywhere
    /// in the portal - which is the set an administration shell needs to decide which sections to offer and
    /// the set that populates an access token's permission claims. Supplying either scope, or both,
    /// narrows the answer to it.
    /// </para>
    /// <para>
    /// A host account holds every permission, so it is answered with the whole catalogue rather than being
    /// evaluated against grants. That reproduces the legacy role test, whose very first condition returned
    /// true for a host account before any role was examined.
    /// </para>
    /// <para>
    /// A module configured to inherit its view permission is answered for that one key from the pages it
    /// is placed on, exactly as the legacy read did; every other key still comes from the module's own
    /// grants.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<string>>> GetEffectivePermissionKeysAsync(
        int portalId,
        int? userId,
        int? moduleId = null,
        int? tabId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<string>>.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<IReadOnlyList<string>>.Failure(
                UserNotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        if (caller.IsSuperUser)
        {
            // MIGRATION: a host account is answered from the closed PermissionKey enumeration rather
            // than by reading the catalogue table. The legacy role test returned true for a host
            // account before examining a single grant, so the answer is "everything" by definition;
            // and since Permission.PermissionKey IS that enumeration, the enumeration is the complete
            // set of keys any catalogue row could ever carry. Reading the table would ask the store a
            // question whose answer is already known, and would answer "everything" with less than
            // everything on an installation whose catalogue happens to be missing a row.
            return Result<IReadOnlyList<string>>.Success(Normalise(
                AllPermissionKeys.Select(key => key.ToString())));
        }

        Module? module = null;
        if (moduleId is int scopedModuleId)
        {
            module = await _modules
                .GetByIdAsync(scopedModuleId, cancellationToken)
                .ConfigureAwait(false);

            if (module is null || !BelongsToPortal(module.PortalId, portalId))
            {
                return Result<IReadOnlyList<string>>.Failure(
                    ModuleNotFoundCode,
                    FormattableString.Invariant(
                        $"Module {scopedModuleId} does not exist in portal {portalId}."));
            }
        }

        if (tabId is int scopedTabId)
        {
            Tab? tab = await _tabs.GetByIdAsync(scopedTabId, cancellationToken).ConfigureAwait(false);
            if (tab is null || !BelongsToPortal(tab.PortalId, portalId))
            {
                return Result<IReadOnlyList<string>>.Failure(
                    TabNotFoundCode,
                    FormattableString.Invariant($"Page {scopedTabId} does not exist in portal {portalId}."));
            }
        }

        if (module is null && tabId is null)
        {
            Result<IReadOnlyList<string>> portalWide = await _evaluator
                .ListEffectivePortalPermissionKeysAsync(portalId, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<string>>.Success(Normalise(portalWide.Value));
        }

        var keys = new List<string>();

        if (module is not null)
        {
            // The module's existence was established above, so the evaluator cannot report it absent here;
            // its verdict is read directly.
            IReadOnlyList<string> moduleKeys = (await _evaluator
                    .ListEffectiveModulePermissionKeysAsync(
                        module.ModuleId,
                        userId,
                        caller.RoleNames,
                        cancellationToken)
                    .ConfigureAwait(false))
                .Value;

            if (module.InheritViewPermissions == true)
            {
                // The module takes its view permission from the pages it sits on, so its own view grant is
                // disregarded and the page grants decide, which is what ModuleController.vb:L131-L136 did.
                keys.AddRange(moduleKeys.Where(key =>
                    !string.Equals(key, ViewPermissionKey, StringComparison.OrdinalIgnoreCase)));

                // When the caller named a page as well as a module it has named a PLACEMENT, and the
                // inherited view key is then decided from that page alone. Naming both is the precise
                // question; naming only the module is the collective one. See the contract remarks.
                if (await InheritedViewGrantedAsync(
                        module,
                        tabId,
                        placementTabModuleId: null,
                        userId,
                        caller.RoleNames,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    keys.Add(ViewPermissionKey);
                }
            }
            else
            {
                keys.AddRange(moduleKeys);
            }
        }

        if (tabId is int pageId)
        {
            IReadOnlyList<string> tabKeys = (await _evaluator
                    .ListEffectiveTabPermissionKeysAsync(pageId, userId, caller.RoleNames, cancellationToken)
                    .ConfigureAwait(false))
                .Value;

            keys.AddRange(tabKeys);
        }

        return Result<IReadOnlyList<string>>.Success(Normalise(keys));
    }

    /// <inheritdoc />
    /// <remarks>
    /// A denial is a successful result carrying <see langword="false"/>, never a failure: the caller asked
    /// a question and got an answer. Only an unanswerable question - a module that does not exist, or a key
    /// outside the closed set - is reported as a failure.
    /// </remarks>
    public async Task<Result<bool>> HasModulePermissionAsync(
        int portalId,
        int? userId,
        int moduleId,
        PermissionKey permissionKey,
        int? placementTabId = null,
        int? placementTabModuleId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(permissionKey))
        {
            return Result<bool>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant($"Permission key {(int)permissionKey} is not defined."));
        }

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || !BelongsToPortal(module.PortalId, portalId))
        {
            return Result<bool>.Failure(
                ModuleNotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<bool>.Success(false);
        }

        if (caller.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        if (permissionKey == PermissionKey.VIEW && module.InheritViewPermissions == true)
        {
            bool inherited = await InheritedViewGrantedAsync(
                    module,
                    placementTabId,
                    placementTabModuleId,
                    userId,
                    caller.RoleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            return Result<bool>.Success(inherited);
        }

        Result<bool> granted = await _evaluator
            .HasModulePermissionAsync(moduleId, permissionKey, userId, caller.RoleNames, cancellationToken)
            .ConfigureAwait(false);

        return Result<bool>.Success(granted.Value);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The page is always named by argument. The legacy pair this replaces included one member that read
    /// the page from ambient request state, so the same question produced different answers depending on
    /// which member the caller happened to reach.
    /// </remarks>
    public async Task<Result<bool>> HasTabPermissionAsync(
        int portalId,
        int? userId,
        int tabId,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(permissionKey))
        {
            return Result<bool>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant($"Permission key {(int)permissionKey} is not defined."));
        }

        Tab? tab = await _tabs.GetByIdAsync(tabId, cancellationToken).ConfigureAwait(false);
        if (tab is null || !BelongsToPortal(tab.PortalId, portalId))
        {
            return Result<bool>.Failure(
                TabNotFoundCode,
                FormattableString.Invariant($"Page {tabId} does not exist in portal {portalId}."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<bool>.Success(false);
        }

        if (caller.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        Result<bool> granted = await _evaluator
            .HasTabPermissionAsync(tabId, permissionKey, userId, caller.RoleNames, cancellationToken)
            .ConfigureAwait(false);

        return Result<bool>.Success(granted.Value);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Only grants made directly to the account are removed. Grants the account receives through a role
    /// belong to the role rather than to the account, so removing them would strip every other holder of
    /// that role as well.
    /// </para>
    /// <para>
    /// MIGRATION: the removal spans both grant tables and is therefore two repository calls, one per
    /// table, exactly as the legacy provider declared it - <c>DeleteModulePermissionsByUserID</c> at
    /// core <c>DataProvider.vb</c>:L296 and <c>DeleteTabPermissionsByUserID</c> at L305 were two
    /// separate members over two separate tables, and each terminal procedure joins its own grant
    /// table to its own owning table to bound the delete to one tenant. Neither reports a count, and
    /// neither did in the legacy source; removing nothing is a legitimate outcome, so an account that
    /// held no direct grants succeeds.
    /// </para>
    /// <para>
    /// The portal is taken as an argument deliberately. The two legacy cleanups read it off the account
    /// object they were handed, so an account-only contract would silently widen the removal to every
    /// portal the account belongs to.
    /// </para>
    /// </remarks>
    public async Task<Result> DeleteUserPermissionsAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result.Failure(
                UserNotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        await _permissions.DeleteModulePermissionsByUserIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        await _permissions.DeleteTabPermissionsByUserIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Reads the catalogue keys matching an optional scope code, an optional module definition and an
    /// optional key.
    /// </summary>
    /// <param name="permissionCode">The scope code filter, or <see langword="null"/> for no restriction.</param>
    /// <param name="moduleDefinitionId">The module definition filter, or <see langword="null"/> for no restriction.</param>
    /// <param name="permissionKey">The key filter, or <see langword="null"/> for no restriction.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The keys the catalogue reports for that combination, unnormalised.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy catalogue reader, core <c>DataProvider.vb</c>:L284
    /// <c>GetPermissionByCodeAndKey</c>, treated a null in either argument as a wildcard, so one
    /// procedure served "everything", "by code", "by key" and "by both". The repository contract that
    /// replaces it takes a non-nullable code and a non-nullable key, because a wildcard argument on a
    /// typed contract is precisely the sentinel-in-the-signature habit this migration removes. The
    /// three shapes are therefore composed here, in the layer that owns the question, from the two
    /// filtered reads the provider actually declared.
    /// </para>
    /// <para>
    /// A definition filter goes straight to the definition-scoped read (core
    /// <c>DataProvider.vb</c>:L281), and any code filter is then applied to its result: a definition
    /// declares a handful of entries, so filtering them in memory costs nothing and avoids a second
    /// round trip. A code filter on its own iterates the closed key enumeration - at most four reads,
    /// bounded by the schema rather than by the data - because "which keys exist under this code" is
    /// exactly the question the code-and-key read answers, one key at a time. Neither filter present
    /// is answered from the enumeration, for the reason given on that field.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> ReadCatalogueKeysAsync(
        string? permissionCode,
        int? moduleDefinitionId,
        PermissionKey? permissionKey,
        CancellationToken cancellationToken)
    {
        string? wantedCode = permissionCode?.Trim();

        if (moduleDefinitionId is int definitionId)
        {
            IReadOnlyList<Permission> declared = await _permissions
                .GetByModuleDefinitionIdAsync(definitionId, cancellationToken)
                .ConfigureAwait(false);

            IEnumerable<Permission> matching = wantedCode is null
                ? declared
                : declared.Where(entry => string.Equals(
                    entry.PermissionCode,
                    wantedCode,
                    StringComparison.OrdinalIgnoreCase));

            if (permissionKey is PermissionKey wantedWithinDefinition)
            {
                matching = matching.Where(entry => entry.PermissionKey == wantedWithinDefinition);
            }

            return matching.Select(entry => entry.PermissionKey.ToString()).ToList();
        }

        if (wantedCode is not null)
        {
            // MIGRATION: naming the code AND the key is GetPermissionByCodeAndKey
            // (PermissionController.vb:L47) exactly - one store read asking whether that key is declared
            // within that scope. Without the key the same read is repeated once per candidate key, which is
            // how the four legacy single-column reads collapse into one member; narrowing the candidate set
            // to the one key asked about is therefore the same code path with a shorter loop rather than a
            // second implementation of it.
            IReadOnlyList<PermissionKey> candidates = permissionKey is PermissionKey wantedKey
                ? [wantedKey]
                : AllPermissionKeys;

            var present = new List<string>(candidates.Count);

            foreach (PermissionKey candidate in candidates)
            {
                IReadOnlyList<Permission> entries = await _permissions
                    .GetByCodeAndKeyAsync(wantedCode, candidate, cancellationToken)
                    .ConfigureAwait(false);

                if (entries.Count > 0)
                {
                    present.Add(candidate.ToString());
                }
            }

            return present;
        }

        // With no scope and no definition named there is nothing to read: the unfiltered catalogue of keys
        // IS the closed enumeration, which is why this branch touches no store. A key filter therefore
        // narrows the enumeration rather than querying, and answers with that key alone - it is by
        // definition declared somewhere, or it would not be a member.
        if (permissionKey is PermissionKey only)
        {
            return [only.ToString()];
        }

        return AllPermissionKeys.Select(key => key.ToString()).ToList();
    }

    /// <summary>
    /// Decides whether a module configured to inherit its view permission is viewable by the caller, at the
    /// placement the caller addressed or - when none was addressed - at every placement it occupies.
    /// </summary>
    /// <param name="module">The module, with its placements loaded.</param>
    /// <param name="placementTabId">
    /// The page the module is being addressed on, or <see langword="null"/> when the caller named none.
    /// </param>
    /// <param name="placementTabModuleId">
    /// The placement being addressed, named by its own key, or <see langword="null"/> when the caller named
    /// none. Takes precedence over <paramref name="placementTabId"/> because it is the more precise of the
    /// two; when both are given they must agree.
    /// </param>
    /// <param name="userId">The caller, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// When a placement was addressed: whether its page grants the view key, and <see langword="false"/>
    /// when the module does not occupy that placement or the two forms of address contradict each other. When
    /// none was addressed: whether EVERY page the module sits on grants the view key, and
    /// <see langword="false"/> when it sits on none.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy read answered this for one module <em>instance on one page</em>, because the
    /// object it hydrated was a flattened module-and-placement join that always carried a page identifier.
    /// Naming the placement is therefore the faithful behaviour, not an addition to it.
    /// </para>
    /// <para>
    /// THE UNION IS NOT AN ACCEPTABLE ANSWER IN EITHER CASE, and an earlier revision used it in both. It
    /// granted the view key when ANY page the module sat on granted it, whatever the caller had asked about.
    /// For a module with a single placement - the common case - that is identical to the legacy answer, which
    /// is what made the defect easy to miss. For a module placed twice it is strictly wider: place a module on
    /// a public page and again on a restricted one, and every caller who may see the public placement is
    /// admitted to the restricted one, because the test never asked which placement was being requested.
    /// </para>
    /// <para>
    /// AN ADDRESSED PLACEMENT IS DECIDED BY THAT PAGE ALONE. A placement the module does not occupy is a
    /// denial, not a reason to consult the others: falling back would restore the union through the back door,
    /// and would additionally let a caller discover a module's other placements by observing which page
    /// identifiers produce an affirmative answer.
    /// </para>
    /// <para>
    /// AN UNADDRESSED QUESTION IS DECIDED BY EVERY PLACEMENT AT ONCE, so it grants only what holds at all of
    /// them. A caller that names no page is asking about the module irrespective of where it sits, and the
    /// only answer to that which cannot exceed the per-page answer is the conjunction. It coincides with the
    /// legacy answer for a singly-placed module, and for a multiply-placed one it is deliberately the
    /// narrower reading: a caller who is entitled to a particular placement says so, and is then decided by
    /// that page under the branch above. Choosing the disjunction here instead would leave the escalation
    /// fully reachable, because every route in this application addresses a module without naming a page.
    /// </para>
    /// <para>
    /// A MODULE THAT SITS ON NO PAGE IS NOT VIEWABLE. The conjunction over an empty set is vacuously true, so
    /// the empty case is stated rather than left to the loop - a module that inherits its view permission from
    /// its pages and has no pages inherits nothing, and must not thereby become visible to everyone.
    /// </para>
    /// <para>
    /// The placements are read only when the entity did not arrive with them loaded, and the addressed case
    /// tests membership against the same collection, so naming a placement costs no extra round trip. Either
    /// branch stops at the first page that settles the outcome.
    /// </para>
    /// </remarks>
    private async Task<bool> InheritedViewGrantedAsync(
        Module module,
        int? placementTabId,
        int? placementTabModuleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TabModule> placements = module.TabModules.Count > 0
            ? module.TabModules.ToList()
            : await _modules.GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken).ConfigureAwait(false);

        if (placementTabModuleId is int addressedTabModuleId)
        {
            // Addressed by the placement's own key, which is the precise form: a module may be placed on one
            // page more than once, so the page identifier alone cannot always name a single placement.
            TabModule? addressed = placements
                .FirstOrDefault(placement => placement.TabModuleId == addressedTabModuleId);

            if (addressed is null)
            {
                return false;
            }

            // Both forms of address were given and they disagree. Refused rather than resolved in favour of
            // either, because whichever were chosen would be chosen for its permissions and not for what the
            // request meant.
            if (placementTabId is int alsoNamedTabId && alsoNamedTabId != addressed.TabId)
            {
                return false;
            }

            // The evaluator reports an unknown page as a successful negative rather than a failure, so the
            // value is read directly here as it is in the loop below.
            Result<bool> addressedPlacementGrant = await _evaluator
                .HasTabPermissionAsync(
                    addressed.TabId,
                    PermissionKey.VIEW,
                    userId,
                    roleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            return addressedPlacementGrant.Value;
        }

        if (placementTabId is int addressedTabId)
        {
            // Both -1 and 0 are genuine identifiers in this schema, so this is a real comparison rather
            // than a sentinel test.
            if (!placements.Any(placement => placement.TabId == addressedTabId))
            {
                return false;
            }

            Result<bool> addressedPageGrant = await _evaluator
                .HasTabPermissionAsync(
                    addressedTabId,
                    PermissionKey.VIEW,
                    userId,
                    roleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            return addressedPageGrant.Value;
        }

        // Stated rather than left to the loop below, whose conjunction over an empty set would be true.
        if (placements.Count == 0)
        {
            return false;
        }

        foreach (TabModule placement in placements)
        {
            Result<bool> granted = await _evaluator
                .HasTabPermissionAsync(
                    placement.TabId,
                    PermissionKey.VIEW,
                    userId,
                    roleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            // ONE WITHHOLDING PAGE SETTLES IT, so the rest are not worth a round trip - this loop is the
            // conjunction the summary above describes, not the union an earlier revision computed. A
            // placement naming a page that no longer exists withholds, exactly as an ungranted page does, so
            // the advisory the evaluator attaches to that verdict is not consulted here: both answers are
            // "not this one". The evaluator reports an unknown page as a successful negative rather than a
            // failure, so reading the value directly cannot throw.
            if (!granted.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves a caller into the role names the evaluator must consider.
    /// </summary>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <param name="userId">The caller, or <see langword="null"/> when anonymous.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>Whether the caller was found, whether it is a host account, and its role names.</returns>
    /// <remarks>
    /// <para>
    /// This reproduces PortalSecurity.IsInRoles (PortalSecurity.vb:L114-L134) member for member. The
    /// "All Users" pseudo-role is added for every caller, and the "Unauthenticated Users" pseudo-role only
    /// while the caller is anonymous, because the legacy test admitted it only when the request was not
    /// authenticated.
    /// </para>
    /// <para>
    /// A host account is accepted even when it holds no membership of the portal, because a host account is
    /// installation-wide and the legacy test short-circuited on it before examining any role.
    /// </para>
    /// </remarks>
    private async Task<CallerIdentity> ResolveCallerAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken)
    {
        if (userId is not int callerId)
        {
            return new CallerIdentity(
                Found: true,
                IsSuperUser: false,
                RoleNames: [_portalOptions.AllUsersRoleName, _portalOptions.UnauthenticatedRoleName]);
        }

        User? account = await _users.GetAsync(portalId, callerId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            account = await _users.GetAsync(portalId: null, callerId, cancellationToken).ConfigureAwait(false);
            if (account is null || !account.IsSuperUser)
            {
                return new CallerIdentity(Found: false, IsSuperUser: false, RoleNames: []);
            }
        }

        if (account.IsSuperUser)
        {
            return new CallerIdentity(Found: true, IsSuperUser: true, RoleNames: []);
        }

        IReadOnlyList<string> assigned = await _users
            .ListRoleNamesAsync(portalId, callerId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var roleNames = new List<string>(assigned.Count + 1);
        roleNames.AddRange(assigned);
        roleNames.Add(_portalOptions.AllUsersRoleName);

        return new CallerIdentity(Found: true, IsSuperUser: false, RoleNames: roleNames);
    }

    /// <summary>
    /// Tests whether an entity whose portal is optional belongs to the portal in question.
    /// </summary>
    /// <param name="owningPortalId">The portal recorded on the entity, absent for a host-level one.</param>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <returns><see langword="true"/> when the entity is in scope.</returns>
    /// <remarks>
    /// A host-level module or page carries no portal, and is in scope for every portal, which is what makes
    /// the host administration pages reachable from within a portal. Both zero and minus one are genuine
    /// identifiers in this schema, so the comparison is a real comparison rather than a sentinel test.
    /// </remarks>
    private static bool BelongsToPortal(int? owningPortalId, int portalId)
        => owningPortalId is null || owningPortalId.Value == portalId;

    /// <summary>
    /// Reduces a key sequence to the distinct, upper-cased keys in a stable order.
    /// </summary>
    /// <param name="keys">The keys to reduce.</param>
    /// <returns>The normalised keys.</returns>
    /// <remarks>
    /// The repository already upper-cases what it returns; normalising again here costs nothing and makes
    /// the guarantee hold for the catalogue projection too, whose column is stored as authored.
    /// </remarks>
    private static IReadOnlyList<string> Normalise(IEnumerable<string> keys)
        => keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

    /// <inheritdoc />
    public async Task<Result<PermissionDto?>> GetPermissionAsync(
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        Permission? definition = await _permissions
            .GetByIdAsync(permissionId, cancellationToken)
            .ConfigureAwait(false);

        // No bound test on the identifier, here or anywhere in this solution: an unknown identifier is
        // reported as absent by the read itself, and a bound would be a second, weaker copy of that answer.
        return Result<PermissionDto?>.Success(definition is null ? null : ToDto(definition));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PermissionDto>>> GetModulePermissionDefinitionsAsync(
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Permission> definitions = await _permissions
            .GetByModuleIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<PermissionDto>>.Success(Project(definitions));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PermissionDto>>> GetTabPermissionDefinitionsAsync(
        int tabId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Permission> definitions = await _permissions
            .GetByTabIdAsync(tabId, cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<PermissionDto>>.Success(Project(definitions));
    }

    /// <summary>
    /// Projects catalogue rows into their wire shape, de-duplicated and in a stable order.
    /// </summary>
    /// <param name="definitions">The rows read from the store.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    /// <para>
    /// Both the ordering and the distinctness are promises this contract makes, so both are asserted here
    /// rather than inherited from however the store happens to be queried today. The ordering is by
    /// identifier rather than by name so that it cannot shift when a display name is edited.
    /// </para>
    /// <para>
    /// Distinctness is asserted rather than assumed because the widest of the three reads behind this
    /// projection is a UNION: the module-scoped read takes the entries of the module's own definition
    /// together with every entry carrying the product-wide module-definition scope code, and a definition
    /// that satisfies both arms is one definition, not two. The current store query expresses that union as
    /// a single predicate over a single table and therefore cannot repeat a row - measured, not assumed -
    /// so this assertion removes nothing today. It is kept because the promise belongs to the layer that
    /// publishes it: a caller reading these entries is entitled to one entry per definition whatever shape
    /// the read behind it later takes.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<PermissionDto> Project(IEnumerable<Permission> definitions)
        => definitions
            .GroupBy(definition => definition.PermissionId)
            .Select(group => group.First())
            .OrderBy(definition => definition.PermissionId)
            .Select(ToDto)
            .ToList();

    /// <summary>Projects one catalogue row into its wire shape.</summary>
    /// <param name="definition">The row to project.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    /// The key travels as the enumeration member's NAME, which is both what the column stores and what every
    /// other permission-shaped value in this API carries - the token's claims and the catalogue listing use
    /// the same spellings - so one concept never travels two ways.
    /// </remarks>
    private static PermissionDto ToDto(Permission definition) => new()
    {
        PermissionId = definition.PermissionId,
        PermissionCode = definition.PermissionCode,
        ModuleDefId = definition.ModuleDefinitionId,
        PermissionKey = definition.PermissionKey.ToString(),
        PermissionName = definition.PermissionName,
    };

    /// <summary>
    /// The outcome of resolving a caller: whether it exists, whether it is a host account, and the role
    /// names the evaluator must consider for it.
    /// </summary>
    /// <param name="Found">Whether the caller exists and is in scope.</param>
    /// <param name="IsSuperUser">Whether the caller is a host account and therefore holds everything.</param>
    /// <param name="RoleNames">The role names, including the applicable pseudo-roles.</param>
    private readonly record struct CallerIdentity(
        bool Found,
        bool IsSuperUser,
        IReadOnlyList<string> RoleNames);
}
