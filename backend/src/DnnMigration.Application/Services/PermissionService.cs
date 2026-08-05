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
//
// MIGRATION: the folder-scoped catalogue read is not ported. PermissionController.vb:L43-L45
// GetPermissionsByFolder(PortalID, Folder) served the file-management subsystem, which AAP section 0.2.2.2
// places out of scope; the target declares no file-system permission entity, so there is no member here
// keyed by a folder path. The catalogue ROWS that subsystem declared still exist and an unfiltered
// catalogue read may legitimately return them - only the lookup keyed by a path disappears.
//
// MIGRATION: the two delimited-string encoders are not ported, and they DISAGREED WITH EACH OTHER. Both
// flattened a set of grants into one string value for a server-rendered control property, and both were
// read verbatim before being dropped. ModulePermissionController.vb:L239-L252 seeds its role and user
// accumulators EMPTY and returns ";" & roles & users. Its sibling at L327-L341 applies the identical
// filter but seeds BOTH accumulators with ";" and returns roles + users, so it emits a DOUBLE SEMICOLON
// in the middle of the value - ";roles;;users;" against the sibling's ";roles;users;". The tab-scoped
// pair repeats the same split (TabPermissionController.vb:L214-L227 seeds empty, the deprecated
// L303-L318 seeds both). That inconsistency is a measured legacy defect, and per AAP section 0.9.1 it is
// ANNOTATED HERE AND NOT HARMONISED: harmonising it would mean choosing one encoding as correct, and
// neither is, because the format was never a data contract. What the encoders genuinely expressed is
// preserved: the allow-only filter, the key filter, and the discrimination between a role grant and a
// user grant. Every read on this service returns a typed sequence, so nothing parses a string to learn
// who holds what.
//
// MIGRATION: the bracketed pseudo-role is not reproduced. A user-scoped grant was tested by synthesising
// the literal "[" & UserID & "]" and passing it to the same role-membership helper a real role name went
// to (ModulePermissionController.vb:L42, TabPermissionController.vb:L47, and the encoders at L247 and
// L222 emitted the same shape). It existed because the legacy row could not distinguish the two cases
// any other way. In the terminal schema both identifier columns are nullable, so a grant names a role or
// a single account and the caller's identity travels as a nullable account identifier - never as a
// synthesised role name. The two columns are asymmetric BY DESIGN and the asymmetry is honoured: a role
// identifier of -1 is the "All Users" pseudo-principal and must NEVER be read as absence, whereas a
// legacy account identifier of -1 genuinely did mean absence, which is exactly what the legacy
// discriminator If Null.IsNull(objModulePermission.UserID) tested.
//
// MIGRATION: the "any tab" sentinel is gone from every signature. GetModulePermissionsCollectionByModuleID
// at ModulePermissionController.vb:L254-L270 looked in a tab-keyed dictionary first and then fell back to
// the provider with a literal -1 in the tab position (L266, and again at the deprecated L346 and L379;
// TabPermissionController.vb:L241, L299 and L335 do the same). A wildcard smuggled into an identifier
// argument is the habit this migration removes: an optional scope is a nullable argument here, and no
// literal -1 is passed anywhere in this file. It cannot be otherwise in this schema - the portal identity
// column seeds at -1 and the role, page and module columns seed at 0, so both values are real keys.
//
// MIGRATION: the nine-versus-seven repository asymmetry is preserved deliberately. The module grant table
// is served by nine repository members and the page grant table by seven, the page side having no
// single-row getter, because the legacy source had none either - ModulePermissionController.vb:L223
// declares GetModulePermission(modulePermissionID) and TabPermissionController has no counterpart. The
// Minimal Change Clause forbids inventing one, so nothing here reaches for a by-identifier page-grant
// read, and the shape of this service follows what the contract actually exposes.
//
// MIGRATION: THE 21 LEGACY CACHE SITES, AND WHERE EACH ONE LANDS. The two grant controllers carried 11
// and 10 static DataCache references between them, and they resolve into exactly three groups rather
// than into 21 target call sites, because most of the 21 were repeated reads of the same two entries.
//
//   The two READ entries were GRANT ROW SETS. ModulePermissionController.vb:L167-L190 cached a
//   tab-keyed dictionary of module grants under String.Format(ModulePermissionCacheKey, TabId), and
//   TabPermissionController.vb:L278-L295 cached a portal-keyed list of page grants under
//   String.Format(TabPermissionCacheKey, PortalID). Both computed their lifetime as a timeout of 20
//   multiplied by the installation-wide performance setting and both skipped the write when the product
//   was not positive. NEITHER ENTRY HAS A COUNTERPART READ ON THIS CONTRACT: the grant-management
//   surface is deliberately absent, so no member here returns grant rows. The consumer of grant rows in
//   the target is Infrastructure/Security/PermissionEvaluator.cs, which is where those two entries
//   belong; this service caches only what it actually reads. That is the omission AAP section 0.7.5.2
//   requires to be stated rather than passed over, and it is stated here.
//
//   The INVALIDATION sites do land here, and they are restored on the one mutating member below. The
//   legacy account cleanup ended by evicting both families - ModulePermissionController.vb:L220 called
//   the by-portal module-permission clear and TabPermissionController.vb:L211 called the page-permission
//   clear - and an earlier revision of this service dropped both silently. Dropping them is not a
//   simplification: a deleted account's grants would keep being served from a warm entry, which is a
//   stale-authorisation defect rather than a stale-listing one.
//
//   The two SCOPED CATALOGUE reads are cached here, and that is a net addition rather than a translation:
//   the catalogue controller carried no cache site at all (PermissionController.vb, 0 DataCache
//   references, measured). They are nevertheless the one thing in this file that is safe to cache and
//   worth caching - installation-wide reference data seeded by the upgrade scripts, with no write member
//   anywhere in this solution, and consumed by no access decision. The legacy timeout-times-multiplier
//   idiom and its not-positive bypass are reproduced exactly, so a multiplier of zero genuinely reaches
//   the store.
//
//   WHAT IS CACHED IS BOUNDED IN THE NUMBER OF ENTRIES IT CAN EVER CREATE, which is a condition rather
//   than a detail: an entry is keyed by a module DEFINITION - so every module of an installation collapses
//   onto the definitions it has installed - or by a single installation-wide key for the page-scoped
//   answer, whose read ignores the page it is given. The FILTERED key listing is not cached at all,
//   because its dimensions are a free-text scope code and an unvalidated identifier, which no key scheme
//   can bound and which no token can render unambiguously - any token is itself a legal scope code. Both
//   decisions are recorded at their members.
//
// MIGRATION: only PROJECTIONS are cached, never entities, and that is forced rather than stylistic. The
// permission repository issues no no-tracking query, so every entity it returns is attached to the
// request's change tracker; holding one in a process-wide cache would hand a later request an entity
// bound to a disposed session. Every entry written below is a read-only sequence of strings or of the
// catalogue projection type.
//
// MIGRATION: no log and no audit event is emitted here, and the omission is measured rather than
// overlooked. All three legacy controllers contain ZERO AddLog call sites; their only six logging
// references are LogException inside the row-hydration helpers that the object-relational materialiser
// deletes outright, so there is no audit trail to preserve. The account deletion that reaches the
// cleanup member below is already audited by the account service, which records the legacy USER_DELETED
// entry for it, so recording a second event here would double-count one action. Separately, ILogger<T>
// cannot be named in this project at all: it references FluentValidation and nothing else per AAP
// section 0.6.1, which is why this layer publishes IAuditSink for the services that genuinely have a
// trail to write and why this one takes neither.
using System.Globalization;
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
    /// Minutes a cached catalogue projection is held for before the multiplier is applied.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the measured legacy value. Both permission timeouts were declared as 20 -
    /// <c>DataCache.ModulePermissionCacheTimeOut</c> and <c>DataCache.TabPermissionCacheTimeOut</c> - and
    /// each was multiplied by the installation-wide performance setting at its point of use
    /// (<c>ModulePermissionController.vb</c>:L177 and L315, <c>TabPermissionController.vb</c>:L285). The
    /// number is restated here rather than imported because the legacy constant lived on a static cache
    /// class that no longer exists, and the cache abstraction that replaced it deliberately publishes no
    /// timeout: <em>the caller</em> computes the lifetime, which is the whole reason the multiplier is
    /// injected.
    /// </remarks>
    private const int CatalogueCacheTimeOutMinutes = 20;

    /// <summary>
    /// Cache key format for the module-scoped catalogue definitions, keyed by the MODULE DEFINITION the
    /// answer actually depends on rather than by the module that was asked about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: a NEW key family, and deliberately not one of the two legacy permission families. The
    /// legacy keys <c>ModulePermissions{0}</c> (tab-keyed) and <c>TabPermissions{0}</c> (portal-keyed)
    /// held GRANT ROW SETS, and this contract exposes no grant-set read at all - the grant-management
    /// surface is deliberately absent - so neither key has a counterpart read here to attach to. Writing
    /// a catalogue projection under a grant key would both misdescribe the entry and collide with the
    /// grant invalidation that <c>ICacheService</c> targets at those exact names.
    /// </para>
    /// <para>
    /// THE DIMENSION IS THE DEFINITION, NOT THE MODULE, and that substitution is what bounds this key
    /// space. The read behind it selects the entries of the module's own definition together with every
    /// entry carrying the product-wide definition scope code, so two modules sharing a definition have
    /// the same answer by construction and every module of an installation collapses onto the handful of
    /// definitions it has installed. Keying by module instead admitted one entry per identifier a caller
    /// chose to name, which is unbounded growth bought with well-formed requests. The tenant guard on
    /// the member itself bounds the space a second time and from the other direction: an identifier that
    /// names no module of the portal is REFUSED before any key is formed, so it earns no entry at all.
    /// </para>
    /// </remarks>
    private const string ModuleDefinitionsCacheKeyFormat = "PermissionDefinitionsByModuleDefinition|{0}";

    /// <summary>
    /// Cache key for the page-scoped catalogue definitions, which are installation-wide.
    /// </summary>
    /// <remarks>
    /// ONE ENTRY, WITH NO PAGE DIMENSION, and that is a measurement rather than a simplification: the
    /// terminal page-scoped catalogue statement filters on the product-wide page scope code and never
    /// references its page argument at all, so every page receives the same rows and the repository
    /// documents that it accepts the argument unused. Keying by page therefore stored one identical copy
    /// per page identifier a caller happened to name - unbounded in the number of keys and constant in
    /// the number of distinct answers. The tenant check on the member is unaffected by the collapse: it
    /// happens before this key is used and refuses a page the portal does not own. If a later revision ever makes the page distinction real, this key
    /// must regain the dimension in the same change that makes the read use it.
    /// </remarks>
    private const string TabDefinitionsCacheKey = "PermissionDefinitionsByTab|all";

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

    /// <summary>
    /// Role assignments, read only to answer whether a caller administers a portal.
    /// </summary>
    /// <remarks>
    /// The assignment-shaped read is the one that carries the validity window, which the name-shaped read
    /// on <see cref="IUserRepository"/> does not, and the window is what distinguishes an active
    /// administrator from a lapsed one.
    /// </remarks>
    private readonly IRoleRepository _roles;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly IClock _clock;
    private readonly CachingOptions _caching;

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
    /// <param name="roles">
    /// The caller's role ASSIGNMENTS, read only by the portal-administration test. The assignment carries
    /// the effective and expiry window that decides whether a membership is currently active, which the
    /// name-shaped read on <paramref name="users"/> does not.
    /// </param>
    /// <param name="unitOfWork">
    /// The commit boundary. Required rather than optional: the only mutating member here removes rows
    /// from two grant tables, and the contract promises that removal lands as one unit.
    /// </param>
    /// <param name="cache">
    /// Cache reads for the catalogue and the eviction the account cleanup owes its two grant tables.
    /// </param>
    /// <param name="clock">The instant role assignments are evaluated as of.</param>
    /// <param name="caching">
    /// Bound configuration supplying the performance multiplier that scales every cache lifetime. Taken as
    /// the options class itself rather than through an options accessor, because this project references
    /// FluentValidation and nothing else per AAP section 0.6.1 - the hosting layer projects the bound
    /// instance precisely so this layer can name it.
    /// </param>
    public PermissionService(
        IPermissionRepository permissions,
        IPermissionEvaluator evaluator,
        IPortalRepository portals,
        IModuleRepository modules,
        ITabRepository tabs,
        IUserRepository users,
        IRoleRepository roles,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        IClock clock,
        CachingOptions caching)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: this reproduces the authority test the legacy administration screens performed
    /// imperatively in their own code-behinds - <c>ModuleSettings.ascx.vb:L214-L219</c> and
    /// <c>:L332-L338</c> disabled four controls outright "for tab administrators", who "can only manage
    /// their own tab" - and it reproduces it from the same stored facts: the portal's own
    /// <c>AdministratorRoleId</c> column and the caller's <c>UserRoles</c> assignments. The legacy test
    /// itself read a role NAME off ambient portal state; naming is deliberately not used here, because
    /// "Administrators" identifies a different row in every portal and a name-based test is satisfied by
    /// an unrelated role in another tenant.
    /// </para>
    /// <para>
    /// The ordering is the legacy ordering. <c>PortalSecurity.vb:L123</c> short-circuits on the host
    /// account before it examines any role, so a host account is admitted here without a membership of
    /// the portal at all - which is what makes host administration of any tenant work.
    /// </para>
    /// <para>
    /// The role designated by the portal is read from the portal NAMED IN THE ARGUMENT rather than from
    /// the resolved tenant snapshot, because "does this caller administer portal X" and "does this caller
    /// administer the tenant they arrived through" are different questions and only the first is being
    /// asked. Every assignment is examined rather than the first, so a duplicated pair cannot hide a valid
    /// grant behind a lapsed one, and validity is <c>UserRole.GetStatus</c> - the role's effective and
    /// expiry window, with the legacy absent-date marker read as unbounded - evaluated against the
    /// injected clock in coordinated universal time.
    /// </para>
    /// <para>
    /// Nothing here fails. An anonymous caller, an unknown account, an unknown portal and a portal with no
    /// designated administrator role are all legitimate questions whose answer is "no", and reporting them
    /// as failures would force every caller to distinguish "not permitted" from "could not tell" when the
    /// two have the same consequence. It is also why an unset designation cannot grant: a configuration
    /// gap answers false rather than being widened to another role.
    /// </para>
    /// </remarks>
    public async Task<Result<bool>> IsPortalAdministratorAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken = default)
    {
        if (userId is not int callerId)
        {
            return Result<bool>.Success(false);
        }

        // The host account is resolved without a portal scope, exactly as ResolveCallerAsync does, because
        // a host account belongs to no tenant and a portal-scoped read would not find it.
        User? account = await _users.GetAsync(portalId, callerId, cancellationToken).ConfigureAwait(false)
            ?? await _users.GetAsync(portalId: null, callerId, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return Result<bool>.Success(false);
        }

        if (account.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        // Aliases are not requested: the administrator role identifier is a column on the portal row, and
        // loading the alias collection to read it would fetch rows this question never looks at.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        if (portal?.AdministratorRoleId is not int administratorRoleId)
        {
            return Result<bool>.Success(false);
        }

        DateTime asOfUtc = _clock.UtcNow;

        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesAsync(portalId, callerId, cancellationToken)
            .ConfigureAwait(false);

        bool administers = assignments.Any(assignment =>
            assignment.RoleId == administratorRoleId
            && assignment.GetStatus(asOfUtc) == RoleStatus.Active);

        return Result<bool>.Success(administers);
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
        // MIGRATION: a SUPPLIED-BUT-BLANK code is refused rather than treated as "no filter", because the
        // two mean different things to a caller: omitting the parameter asks for the whole catalogue, while
        // sending an empty one asks for the codes that are blank, and there are none. Answering the first
        // question when asked the second would report success for a request that matched nothing.
        //
        // MIGRATION - THIS BRANCH IS UNREACHABLE OVER MVC QUERY BINDING, AND THAT WAS MEASURED, NOT ASSUMED.
        // "?permissionCode=", "?permissionCode=%20%20" and "?permissionCode=%09" all answer 200 with the full
        // catalogue, because the simple-type binder converts a whitespace-only query value to null before the
        // action body runs, so the service sees an omitted filter rather than a blank one. The guard is kept
        // for the same reason as the key guard below: the Application layer is a public API in its own right,
        // and a direct caller can pass " " where a query string cannot. The identifier guard further down IS
        // reachable over HTTP - "?moduleDefinitionId=0" answers 400 with this same failure code - so this
        // member genuinely emits both a member-named validation document and a plain problem document
        // depending on which filter was wrong, which is why its endpoint advertises the common supertype.
        if (permissionCode is not null && string.IsNullOrWhiteSpace(permissionCode))
        {
            return Result<IReadOnlyList<string>>.Failure(
                FilterInvalidCode,
                "The permission code filter must not be blank; omit it to place no restriction.");
        }

        // MIGRATION: an UNDEFINED key filter is refused here, because this member's own contract cannot
        // honour one. PermissionKey is a closed set of four members, but a CLR enumeration is an integer
        // at run time, so (PermissionKey)99 is a constructible value. Every branch of the read below then
        // treats the filter as a value to narrow by, and the unscoped branch answers with the FILTER
        // ITSELF - so without this guard the member returned ["99"], fabricating a key that names no
        // member, no row and no grant. The scoped branches used it as a comparison operand and answered
        // with an empty set, which reads as "declared nowhere" rather than as "does not exist".
        //
        // MIGRATION - WHAT THIS GUARD IS AND IS NOT. It is NOT the HTTP boundary's defence. MEASURED at
        // run time against this API: "?permissionKey=99" is refused by MVC model binding with 400 and
        // errors["permissionKey"] = ["The value '99' is invalid."] before the action body runs, because
        // EnumTypeModelBinder tests DEFINED membership for a non-flags enumeration; "?permissionKey=0"
        // answers ["VIEW"] and "?permissionKey=3" answers ["WRITE"], which proves numeric binding works
        // and that the refusal is specifically the membership check. The guard exists because the
        // Application layer is a public API in its own right, reachable from callers that never touch MVC.
        // The two permission EVALUATION members in this class already guard their non-nullable key
        // parameters for the same reason, and those ARE reached from the authorization path; this member's
        // filter is nullable, which is why the test is on the value and not on the presence.
        //
        // MIGRATION: the equivalent question for a JSON BODY has the opposite answer, which is why a
        // validator rather than the binder carries it there. System.Text.Json performs no defined-member
        // check, so this solution's body-bound enumerations are guarded by converters and FluentValidation
        // rules - measured: posting billingFrequency as the number 99 is refused by the converter, not by
        // MVC. Query-bound enumerations are checked by the binder; body-bound ones are not.
        if (permissionKey is PermissionKey wantedKeyFilter && !Enum.IsDefined(wantedKeyFilter))
        {
            return Result<IReadOnlyList<string>>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant(
                    $"Permission key {(int)wantedKeyFilter} is not defined; omit the filter to place no restriction."));
        }

        if (moduleDefinitionId is int definitionId && definitionId < LowestModuleDefinitionId)
        {
            return Result<IReadOnlyList<string>>.Failure(
                FilterInvalidCode,
                FormattableString.Invariant(
                    $"Module definition {definitionId} cannot name a row; identifiers start at {LowestModuleDefinitionId}."));
        }

        // THIS ANSWER IS DELIBERATELY NOT CACHED, and the reason is the shape of its key space rather than
        // the cost of the read. An entry for this member would have to be dimensioned by all three filters,
        // and two of the three are chosen freely by the caller: the scope code is FREE TEXT - the column it
        // matches is free text, and an installation may carry a code this codebase has never seen - and the
        // definition identifier is an arbitrary integer that nothing here proves names a row. A cache keyed
        // on those admits as many entries as a caller cares to invent, each held for the whole lifetime, so
        // one caller could grow the shared store and its key registry without limit while breaking no rule.
        //
        // The two escapes were both rejected. Validating the dimensions first would buy a bounded key space
        // with a round trip per call on the very path the cache existed to spare. Rendering an absent filter
        // as a token cannot make the key unambiguous either: any token is itself a legal scope code, so
        // "?permissionCode=<token>" and "no code supplied" produced the SAME key, and whichever request
        // warmed the entry first then answered both - a filtered request served the unfiltered catalogue, or
        // the reverse. That was a real defect and not a hypothetical one.
        //
        // Nothing is lost by reading through. The unfiltered shape touches no store at all - the catalogue
        // of keys IS the closed enumeration - the definition-scoped shape is one indexed read of a handful
        // of rows, and the code-scoped shape is at most one indexed read per candidate key over the same
        // small table. MIGRATION: the legacy catalogue controller carried NO cache site whatsoever
        // (PermissionController.vb, zero DataCache references, measured), so caching here was a net addition
        // rather than a ported behaviour, and withdrawing it restores the legacy read pattern rather than
        // regressing from it. The two definition reads below ARE cached, because their key spaces are
        // canonical and bounded.
        //
        // MIGRATION: Permission.PermissionKey is the closed PermissionKey enumeration, whose member NAME is
        // the stored and wire value, so the projection is ToString() rather than a lookup and it can only
        // ever yield VIEW, EDIT, READ or WRITE. Normalise still runs: it de-duplicates the catalogue and
        // imposes the ordinal ordering the contract promises, and it is the same treatment the grant-derived
        // answers below receive, which do arrive as arbitrary column text.
        IReadOnlyList<string> catalogueKeys = Normalise(
            await ReadCatalogueKeysAsync(permissionCode, moduleDefinitionId, permissionKey, cancellationToken)
                .ConfigureAwait(false));

        return Result<IReadOnlyList<string>>.Success(catalogueKeys);
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

            return Result<IReadOnlyList<string>>.Success(Normalise(KeysOf(portalWide)));
        }

        var keys = new List<string>();

        if (module is not null)
        {
            // The module's existence was established above, so the evaluator cannot report it absent here.
            // Its listing is nonetheless read through the shared reader, so that the failure its contract
            // permits but its implementation never produces cannot become an unhandled exception.
            IReadOnlyList<string> moduleKeys = KeysOf(await _evaluator
                .ListEffectiveModulePermissionKeysAsync(
                    module.ModuleId,
                    userId,
                    caller.RoleNames,
                    cancellationToken)
                .ConfigureAwait(false));

            if (module.InheritViewPermissions == true)
            {
                // The module takes its view permission from the pages it sits on, so its own view grant is
                // disregarded and the page grants decide, which is what ModuleController.vb:L131-L136 did.
                keys.AddRange(moduleKeys.Where(key =>
                    !string.Equals(key, ViewPermissionKey, StringComparison.OrdinalIgnoreCase)));

                // When the caller named a page as well as a module it has named a PLACEMENT, and the
                // inherited view key is then decided from that page alone. Naming both is the precise
                // question; naming only the module is the collective one. See the contract remarks.
                //
                // The placements are read here, in the one branch that needs them, and handed to the
                // decision - the same single-read discipline the decision member's own callers follow.
                IReadOnlyList<TabModule> placements =
                    await ReadPlacementsAsync(module, cancellationToken).ConfigureAwait(false);

                if (await InheritedViewGrantedAsync(
                        placements,
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
            IReadOnlyList<string> tabKeys = KeysOf(await _evaluator
                .ListEffectiveTabPermissionKeysAsync(pageId, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false));

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

        // THE TWO FORMS OF ADDRESS ARE RECONCILED HERE, FOR EVERY KEY, and the placement is where that has
        // to happen. The contract states the agreement rule without qualification - naming a placement by
        // its own key and also naming a page that is not the one that placement sits on is a contradiction,
        // and a contradiction is refused rather than resolved in favour of either. An earlier revision
        // enforced it only inside the inherited-view branch below, which left every other key answered as
        // though no placement had been named at all: a contradictory pair asking about EDIT, or asking
        // about VIEW on a module that does not inherit, was silently accepted and evaluated against the
        // module's own grants. Refusing is what the request meant; picking one of the two would be picking
        // whichever happened to grant more.
        //
        // The refusal is a DENIAL rather than a failure, deliberately. The contract publishes exactly two
        // failure codes for this member - the module being absent and the key being undefined - so a
        // contradiction has no code to report under, and a denial is the closed default this whole area
        // falls back to. It also matches what the inherited branch already did for the same condition.
        //
        // THE PLACEMENTS ARE READ AT MOST ONCE PER CALL, and that is why they are held here rather than
        // fetched by each of the two members that need them. Reconciling the addresses and deciding an
        // inherited view are two questions over the SAME set, so an earlier revision asked the store for it
        // twice on the one request shape that reaches both - a caller naming a placement and asking about
        // VIEW on a module that inherits it. The read stays LAZY, so a shape that needs no placement still
        // makes no placement read: only a fully addressed pair, or the inherited-view branch below, brings
        // the set into being.
        IReadOnlyList<TabModule>? placements = null;

        if (placementTabId is int namedTabId && placementTabModuleId is int namedTabModuleId)
        {
            placements = await ReadPlacementsAsync(module, cancellationToken).ConfigureAwait(false);

            if (AddressesDisagree(placements, namedTabId, namedTabModuleId))
            {
                return Result<bool>.Success(false);
            }
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
            // Reuses the set the reconciliation above already read when both addresses were named, and
            // reads it here only when they were not.
            placements ??= await ReadPlacementsAsync(module, cancellationToken).ConfigureAwait(false);

            bool inherited = await InheritedViewGrantedAsync(
                    placements,
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

        return Result<bool>.Success(VerdictOf(granted));
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

        return Result<bool>.Success(VerdictOf(granted));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// THE SELF-CONTAINED FORM, for the caller whose whole operation is "revoke this account's own
    /// grants". It performs no removal reasoning of its own: it delegates to
    /// <see cref="StageUserPermissionRemovalAsync"/>, commits once, and then evicts through
    /// <see cref="InvalidateUserPermissionCachesAsync"/>. Every rule about WHICH rows go therefore has
    /// exactly one home, and the two entry points cannot drift apart.
    /// </para>
    /// <para>
    /// It is deliberately NOT the member the account-deletion cascade calls. That cascade owns a
    /// transaction spanning several writes, and a suboperation committing inside it would make the whole
    /// sequence partially durable - which is why the staging member exists and why this one is documented
    /// as top-level-only.
    /// </para>
    /// </remarks>
    public async Task<Result> DeleteUserPermissionsAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        // STAGING AND ORCHESTRATION ARE SEPARATE, AND THIS MEMBER IS THE ORCHESTRATION HALF. It decides
        // nothing about WHICH rows go - that rule lives once, below, in the staging member - and owns only
        // the commit and the eviction that follow. The split exists because this same removal is one step
        // of deleting the account that holds the grants, and a suboperation that commits on its own turns
        // the enclosing operation into a sequence of independently durable parts: a later step failing
        // would leave the grants gone and the account intact, with no way back.
        Result staged = await StageUserPermissionRemovalAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (staged.IsFailure)
        {
            return staged;
        }

        // BOTH TABLES OR NEITHER, IN ONE COMMIT AND WITHOUT AN EXPLICIT TRANSACTION. Both removals are
        // STAGED against the one scoped change tracker - IPermissionRepository documents them as staging
        // members, and neither issues a statement of its own - so a single SaveChangesAsync applies them
        // indivisibly. That is exactly the guarantee IUnitOfWork.SaveChangesAsync makes ("an implementer
        // must apply the whole batch or none of it"), which is why no scope is opened here: a transaction
        // spanning one commit adds nothing, and opening one would make this member unusable inside a larger
        // operation, since the unit of work refuses a nested scope rather than silently ignoring it.
        //
        // MIGRATION: the legacy pair WAS a pair of independently durable statements. The provider declared
        // transaction members at Library/Components/Providers/Data/DataProvider.vb:L70-L74 and the two
        // cleanups - ModulePermissionController.vb:L218 and TabPermissionController.vb:L209 - never invoked
        // them, so a failure between the two left the account's module grants removed and its page grants
        // intact, the half-cleaned state in which a later account reusing the identifier inherits what was
        // left behind. Making them atomic is a documented divergence rather than an opportunistic tidy-up:
        // it is the unit-of-work boundary AAP section 0.4.3 requires of this layer. The legacy behaviour is
        // annotated rather than reproduced, because reproducing it would mean writing a known half-failure
        // into new code.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Ordered after the commit, deliberately, and delegated to the member the enclosing-operation
        // caller also uses, so the two paths cannot evict different things.
        await InvalidateUserPermissionCachesAsync(portalId, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Only grants made directly to the account are removed. Grants the account receives through a role
    /// belong to the role rather than to the account, so removing them would strip every other holder of
    /// that role as well. This is the single definition of "this account's own grants"; the top-level
    /// member above delegates to it rather than restating it.
    /// </para>
    /// <para>
    /// NOTHING IS COMMITTED, FLUSHED OR EVICTED HERE. Both repository members stage against the scoped
    /// change tracker, so the rows disappear only when the caller's own commit runs - which is what lets
    /// this removal join a larger unit of work and be abandoned with it. No transaction is opened either:
    /// the unit of work refuses a nested scope, so a scope taken here would fault the enclosing operation
    /// outright.
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
    public async Task<Result> StageUserPermissionRemovalAsync(
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

        // Both guards are ahead of both stagings, so a refusal leaves the change tracker exactly as it was
        // and the caller's own unit of work is unaffected by having asked.
        await _permissions.DeleteModulePermissionsByUserIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        await _permissions.DeleteTabPermissionsByUserIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the eviction the legacy cleanup performed and an earlier revision of this service
    /// dropped. <c>ModulePermissionController.vb:L220</c> cleared the module-permission entries of every
    /// tab in the portal and <c>TabPermissionController.vb:L211</c> cleared the portal's page-permission
    /// entry, both immediately after the same two deletes. Dropping them was not a simplification: a warm
    /// entry would keep answering with grants that no longer exist, and grants are what authorisation is
    /// decided from, so the staleness is a security matter rather than a display one.
    /// </para>
    /// <para>
    /// MIGRATION: this is also where the cache-key mismatch between the two families is resolved. The
    /// page-permission entry is portal-keyed, so it is evicted directly. The module-permission entry is
    /// TAB-keyed, so the portal-wide clear the legacy code performed is expressed by naming each of the
    /// portal's pages in turn - which is precisely the breadth <c>ICacheService</c> documents these narrow
    /// members as replacing, and precisely what the legacy code did internally: its private
    /// <c>ClearPermissionCache(moduleId)</c> at <c>ModulePermissionController.vb:L62-L66</c> resolved the
    /// module in order to clear by its owning TabID. No new cache member is invented for this.
    /// </para>
    /// <para>
    /// It is the CALLER'S responsibility to reach here only after its commit has succeeded. Evicting before
    /// the commit would open a window in which a concurrent reader repopulates the entry from rows that are
    /// about to be removed - and would have discarded a valid entry for nothing if the operation were then
    /// abandoned.
    /// </para>
    /// </remarks>
    public async Task InvalidateUserPermissionCachesAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        _cache.InvalidateTabPermissions(portalId);

        IReadOnlyList<Tab> portalTabs = await _tabs
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        foreach (Tab tab in portalTabs)
        {
            _cache.InvalidateModulePermissions(tab.TabId);
        }
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
    /// Decides whether the two forms of addressing a module placement contradict each other.
    /// </summary>
    /// <param name="placements">
    /// The module's placements, already read by the caller. Passing the materialised set rather than the
    /// module is what keeps one request from reading it twice.
    /// </param>
    /// <param name="placementTabId">The page the caller named.</param>
    /// <param name="placementTabModuleId">The placement the caller named by its own key.</param>
    /// <returns>
    /// <see langword="true"/> when the named placement does not sit on the named page, which is the
    /// contradiction the contract refuses.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Called only when BOTH forms were supplied - one form alone cannot contradict anything - so the
    /// presence test lives at the call site, where it also decides whether the placements need reading at
    /// all.
    /// </para>
    /// <para>
    /// A placement key that names nothing this module occupies is NOT reported as a contradiction here, and
    /// the distinction is deliberate. That condition is an unoccupied placement rather than two
    /// disagreeing addresses, and the branches that consume it already answer it as a denial for their own
    /// reasons - the inherited-view path because an unoccupied placement grants nothing, and the
    /// module-grant path because the module's own grants are what it evaluates. Reporting it from here as
    /// well would give one condition two owners.
    /// </para>
    /// <para>
    /// Both zero and minus one are genuine page identifiers in this schema, so the comparison is a real
    /// comparison and not a sentinel test. No store is reached from here at all, which is why the member is
    /// synchronous: it decides over a set it is handed.
    /// </para>
    /// </remarks>
    private static bool AddressesDisagree(
        IReadOnlyList<TabModule> placements,
        int placementTabId,
        int placementTabModuleId)
    {
        TabModule? addressed = placements
            .FirstOrDefault(placement => placement.TabModuleId == placementTabModuleId);

        return addressed is not null && addressed.TabId != placementTabId;
    }

    /// <summary>
    /// Reads a module's placements, preferring the collection the entity already carries.
    /// </summary>
    /// <param name="module">The module whose placements are wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The placements, which may legitimately be empty for a module that sits on no page.</returns>
    /// <remarks>
    /// Named once and shared by the two members that need placements, so the "loaded collection first,
    /// store second" rule cannot drift between them. An empty collection on the entity is treated as "not
    /// loaded" rather than as "no placements", which is the same reading the read it replaced took: the
    /// store is then asked, and it is the store's empty answer that means the module sits on no page.
    /// </remarks>
    private async Task<IReadOnlyList<TabModule>> ReadPlacementsAsync(
        Module module,
        CancellationToken cancellationToken)
        => module.TabModules.Count > 0
            ? module.TabModules.ToList()
            : await _modules
                .GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken)
                .ConfigureAwait(false);

    /// <summary>
    /// Decides whether a module configured to inherit its view permission is viewable by the caller, at the
    /// placement the caller addressed or - when none was addressed - at every placement it occupies.
    /// </summary>
    /// <param name="placements">
    /// The module's placements, already read by the caller. The set is passed in rather than read here so
    /// that a request needing both this decision and the address reconciliation above reads it once.
    /// </param>
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
    /// The placements arrive already read, and the addressed case tests membership against that same
    /// collection, so naming a placement costs no round trip of its own. Either branch stops at the first
    /// page that settles the outcome.
    /// </para>
    /// </remarks>
    private async Task<bool> InheritedViewGrantedAsync(
        IReadOnlyList<TabModule> placements,
        int? placementTabId,
        int? placementTabModuleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
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

            // The two forms of address have already been reconciled by the caller, for every key rather than
            // for this branch alone, so a contradiction cannot reach here. The check that used to stand at
            // this point was correct but too narrowly placed: it left the same contradiction unexamined for
            // every key that does not inherit.
            Result<bool> addressedPlacementGrant = await _evaluator
                .HasTabPermissionAsync(
                    addressed.TabId,
                    PermissionKey.VIEW,
                    userId,
                    roleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            // The evaluator reports an unknown page as a successful negative rather than a failure, and a
            // failure would mean it could not answer at all - which withholds, exactly as an ungranted page
            // does. Reading the verdict through the shared reader keeps that from being a throw.
            return VerdictOf(addressedPlacementGrant);
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

            return VerdictOf(addressedPageGrant);
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
            // "not this one".
            if (!VerdictOf(granted))
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
                RoleNames: [SpecialRoleNames.AllUsers, SpecialRoleNames.Unauthenticated]);
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
        roleNames.Add(SpecialRoleNames.AllUsers);

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
    /// Reads an evaluator verdict without letting an unanswerable question become a thrown exception.
    /// </summary>
    /// <param name="verdict">The outcome the evaluator reported.</param>
    /// <returns>The verdict when the evaluator answered, and <see langword="false"/> when it could not.</returns>
    /// <remarks>
    /// <para>
    /// Reading <c>Value</c> on a failed outcome throws, so every verdict this service consumes is read
    /// through here instead. The evaluator does not currently report a failure from any of these members -
    /// measured against its implementation, not assumed - and its contract reserves failure for "a question
    /// this contract genuinely cannot answer", which is precisely the case that must not surface as an
    /// unhandled exception. Absent this reader, that case would leave the api edge with an internal error
    /// where the closed default was the correct answer.
    /// </para>
    /// <para>
    /// Falling back to <see langword="false"/> rather than to a failure is the same closed default the whole
    /// area applies: absence denies, and a question the evaluator cannot answer has certainly not
    /// established a grant. It also keeps this conversion behaviour-neutral today, since the branch cannot
    /// currently be reached.
    /// </para>
    /// </remarks>
    private static bool VerdictOf(Result<bool> verdict) => verdict.IsSuccess && verdict.Value;

    /// <summary>
    /// Reads an evaluator key listing without letting an unanswerable question become a thrown exception.
    /// </summary>
    /// <param name="listing">The outcome the evaluator reported.</param>
    /// <returns>
    /// The keys when the evaluator answered, and an empty sequence when it could not - the same closed
    /// default a caller holding no reachable grant receives.
    /// </returns>
    /// <remarks>
    /// The listing counterpart of <see cref="VerdictOf(Result{bool})"/>, and it exists for the same reason:
    /// the evaluator's contract permits a failure that its current implementation never produces, and
    /// reading <c>Value</c> unguarded would turn that permitted case into an internal error rather than into
    /// the closed default.
    /// </remarks>
    private static IReadOnlyList<string> KeysOf(Result<IReadOnlyList<string>> listing)
        => listing.IsSuccess ? listing.Value : [];

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
    /// <remarks>
    /// The module is resolved first, and it is resolved twice over: for the tenant check and then for the
    /// cache. SEC-033 - a module that does not exist, or exists in another portal, is refused with
    /// <c>module.not_found</c> before the cache is consulted, so no entry can be reached through a
    /// neighbouring tenant's identifier. The read behind this member then depends only on the module's
    /// DEFINITION, so the definition is what the entry is keyed by: modules sharing a definition share one
    /// entry, and the key space is consequently bounded by the installation's definition catalogue instead
    /// of by the identifiers callers choose to name. The read itself is unchanged and is still issued
    /// against the module identifier, so the repository's own predicate - the module's definition unioned
    /// with the product-wide definition scope - continues to decide the answer.
    /// </remarks>
    public async Task<Result<IReadOnlyList<PermissionDto>>> GetModulePermissionDefinitionsAsync(
        int portalId,
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || !BelongsToPortal(module.PortalId, portalId))
        {
            return Result<IReadOnlyList<PermissionDto>>.Failure(
                ModuleNotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        // SEC-033 and the key space in one step. The identifier is checked against the tenant BEFORE the
        // cache is consulted, so an identifier belonging to another portal is refused rather than answered
        // from an entry, and the entry itself is then keyed by the module's DEFINITION rather than by the
        // module - the read below depends on nothing else, so modules sharing a definition share one entry
        // and the key space is bounded by the installation's definition catalogue instead of by the
        // identifiers callers name. The tenant is not a key component and does not need to be: the refusal
        // above is what isolates the tenants, and the rows themselves are product-wide definition metadata
        // rather than tenant data.
        string cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            ModuleDefinitionsCacheKeyFormat,
            module.ModuleDefinitionId);

        IReadOnlyList<PermissionDto> definitions = await ReadThroughCacheAsync(
                cacheKey,
                async token => Project(
                    await _permissions.GetByModuleIdAsync(moduleId, token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<PermissionDto>>.Success(definitions);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The page is resolved first and for the tenant check alone - SEC-033 - so a page that does not exist,
    /// or exists in another portal, is refused with <c>tab.not_found</c> rather than answered. The answer
    /// itself is then cached under ONE key for the whole installation, because the read behind it is
    /// page-independent by measurement: the terminal statement selects the entries carrying the product-wide
    /// page scope code and never references the page it was given. The page argument is still passed to the
    /// repository, which owns that measurement and documents it; what changes here is only that one answer
    /// is stored once rather than copied under every page identifier a caller names.
    /// </remarks>
    public async Task<Result<IReadOnlyList<PermissionDto>>> GetTabPermissionDefinitionsAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default)
    {
        Tab? tab = await _tabs
            .GetByIdAsync(tabId, cancellationToken)
            .ConfigureAwait(false);

        if (tab is null || !BelongsToPortal(tab.PortalId, portalId))
        {
            return Result<IReadOnlyList<PermissionDto>>.Failure(
                TabNotFoundCode,
                FormattableString.Invariant($"Page {tabId} does not exist in portal {portalId}."));
        }
        IReadOnlyList<PermissionDto> definitions = await ReadThroughCacheAsync(
                TabDefinitionsCacheKey,
                async token => Project(
                    await _permissions.GetByTabIdAsync(tabId, token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<PermissionDto>>.Success(definitions);
    }

    /// <summary>
    /// Reads a catalogue projection through the cache, or straight from the store when caching is off.
    /// </summary>
    /// <typeparam name="T">The projection type, which is always a read-only sequence.</typeparam>
    /// <param name="cacheKey">The entry's key.</param>
    /// <param name="read">Reads the projection from the store; invoked at most once per miss.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The projection, from the cache when one was warm and from the store otherwise.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: reproduces the legacy lifetime arithmetic exactly - a timeout of
    /// <see cref="CatalogueCacheTimeOutMinutes"/> multiplied by the installation-wide performance
    /// multiplier - and reproduces the guard that went with it. The legacy writes were conditioned on the
    /// product being positive (<c>ModulePermissionController.vb</c>:L183,
    /// <c>TabPermissionController.vb</c>:L290), so a multiplier of zero meant "do not cache". That is
    /// honoured here by bypassing the cache entirely rather than by writing an entry that expires
    /// immediately: an entry with a zero lifetime is a write, an eviction and a miss where the
    /// configuration asked for none of them.
    /// </para>
    /// <para>
    /// Only the two SCOPED catalogue reads come through here - the module-scoped definitions and the
    /// page-scoped definitions - and each is keyed by a dimension that is canonical and bounded: the
    /// definition a module belongs to, and a single installation-wide key for the page-scoped answer whose
    /// read is page-independent. That bound is the condition on caching anything here at all. The filtered
    /// key listing is deliberately NOT cached, because its dimensions include free text and an unvalidated
    /// identifier and therefore cannot be bounded without a round trip that would defeat the cache; the
    /// reasoning is recorded at that member.
    /// </para>
    /// <para>
    /// The effective-key and decision members deliberately do not come through here either, and that is a
    /// security judgement rather than an oversight: their answers are caller-dimensioned, and this contract
    /// exposes no grant mutation from which such an entry could be invalidated, so a warm entry would
    /// outlive a grant change with no hook able to clear it. The catalogue carries neither property - it is
    /// installation-wide and this solution declares no write member for it, the three legacy catalogue
    /// writers at <c>PermissionController.vb</c> L55, L59 and L63 being deliberately unported - so an entry
    /// can only ever go stale against a module installation, which is out of scope, and expiry alone is
    /// sufficient.
    /// </para>
    /// </remarks>
    private async Task<T> ReadThroughCacheAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
        where T : notnull
    {
        TimeSpan expiration = TimeSpan.FromMinutes(
            CatalogueCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        if (expiration <= TimeSpan.Zero)
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }

        return await _cache
            .GetOrCreateAsync(cacheKey, read, expiration, cancellationToken)
            .ConfigureAwait(false);
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
