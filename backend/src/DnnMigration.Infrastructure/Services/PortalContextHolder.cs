using System.Globalization;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Services;

/// <summary>Resolves the tenant for one inbound call and holds the resulting snapshot.</summary>
/// <remarks>
/// <para>
/// Registered with a lifetime scoped to one call, which is what makes "resolve once" mean "once per call"
/// rather than "once per process". A longer lifetime would be a cross-tenant defect outright: the first
/// caller's portal would be served to every caller after it.
/// </para>
/// <para>
/// THIS TYPE NEVER TOUCHES THE WEB PIPELINE, and that is a structural constraint rather than a style
/// preference. This assembly's project file forbids a reference to the ASP.NET Core shared framework, so
/// there is no request object, no host object and no ambient accessor available here even in principle.
/// </para>
/// </remarks>
internal sealed class PortalContextHolder : IPortalContextHolder
{
    /// <summary>Longest address that could ever match, being the width of <c>dbo.PortalAlias.HTTPAlias</c>.</summary>
    /// <remarks>
    /// Pinned to the column, not chosen: <c>nvarchar(200)</c> at
    /// <c>PortalAliasConfiguration.HasMaxLength(200)</c>, so a longer candidate could never have been
    /// stored and generating it would put an unmatchable value into the query.
    /// </remarks>
    private const int MaximumAliasLength = 200;

    /// <summary>
    /// Most path segments beneath the authority that are considered when building the candidate chain.
    /// </summary>
    /// <remarks>
    /// This was an independently declared 4 and is now the single authority's 1. Four was never reachable -
    /// the legacy signup screen composed one segment - so the surplus widened this matcher without widening
    /// the feature, and it widened it PAST what the validator would store and the proxy would route.
    /// </remarks>
    private const int MaximumAliasPathSegments = PortalAliasTopology.MaximumPathSegments;

    /// <summary>
    /// Base lifetime, in minutes, of a cached alias resolution before the configured performance multiplier
    /// is applied.
    /// </summary>
    /// <remarks>
    /// Twenty minutes is the conventional base lifetime this installation's other portal-scoped entries
    /// carry, so alias resolution ages on the same schedule as the portal read whose facts it projects.
    /// Every write path that can change one of those facts evicts this family explicitly, so the lifetime
    /// bounds how long an entry survives WITHOUT a write rather than how stale an answer may be.
    /// </remarks>
    private const int AliasResolutionCacheTimeOutMinutes = 20;

    /// <summary>Key family every cached alias resolution is filed under.</summary>
    /// <remarks>
    /// The legacy alias-resolution key name, reused rather than invented, so that the existing host and
    /// portal invalidations - which already name this family - evict these entries without being taught
    /// about them.
    /// </remarks>
    private const string AliasResolutionCacheKeyPrefix = MemoryCacheService.PortalAliasCacheKey;

    private readonly IPortalAliasRepository _aliases;

    /// <summary>The shared cache the resolution is read through.</summary>
    private readonly ICacheService _cache;

    /// <summary>Supplies the configured cache-lifetime multiplier.</summary>
    private readonly CachingOptions _caching;

    /// <summary>Guards creation of the resolution task. Never held across an await.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// The single resolution attempt for this call, or <see langword="null"/> before the first attempt.
    /// Memoising the task rather than the value is what makes concurrent first callers share one attempt.
    /// </summary>
    private Task<Result>? _resolution;

    /// <summary>The resolved snapshot, assigned exactly once and only on success.</summary>
    private volatile IPortalContext? _current;

    /// <summary>Initialises a new holder for one inbound call.</summary>
    /// <param name="aliases">The repository that performs the exact-match alias lookup.</param>
    /// <param name="cache">The shared cache the resolution is read through.</param>
    /// <param name="caching">The configured cache-lifetime multiplier.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="aliases"/>, <paramref name="cache"/> or <paramref name="caching"/> is
    /// <see langword="null"/>.
    /// </exception>
    public PortalContextHolder(
        IPortalAliasRepository aliases,
        ICacheService cache,
        IOptions<CachingOptions> caching)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(caching);

        _aliases = aliases;
        _cache = cache;
        _caching = caching.Value;
    }

    /// <inheritdoc />
    public bool IsResolved => _current is not null;

    /// <inheritdoc />
    public IPortalContext Current =>
        _current ?? throw new InvalidOperationException(
            "The tenant for this request has not been resolved. Resolve it before reading the portal " +
            "context, or test whether it is resolved when running before resolution is possible.");

    /// <inheritdoc />
    public Task<Result> EnsureResolvedAsync(string httpAlias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpAlias);

        lock (_gate)
        {
            // Starting the task inside the gate is what makes the attempt singular; awaiting it inside
            // would serialise the whole request behind the lock and is not done. A second caller finds the
            // field populated and receives the first caller's task, whether or not it has completed.
            return _resolution ??= ResolveAsync(httpAlias, cancellationToken);
        }
    }

    /// <summary>Performs the single resolution attempt.</summary>
    /// <param name="httpAlias">The host name to resolve.</param>
    /// <param name="cancellationToken">Propagates abandonment of the operation.</param>
    /// <returns>The outcome of the attempt.</returns>
    private async Task<Result> ResolveAsync(string httpAlias, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(httpAlias))
        {
            return Result.Failure(
                IPortalContextHolder.NotFoundReasonCode,
                "The request did not carry a host name that identifies a portal.");
        }

        IReadOnlyList<string> chain = BuildAddressChain(httpAlias);

        // One round trip for the whole chain. Asking per candidate would be a query per path segment on
        // every request, which is why the repository member takes the collection.
        //
        // CACHED UNDER THE LEGACY ALIAS-RESOLUTION KEY, which is a restoration rather than an addition:
        // DotNetNuke cached this same question, the key vocabulary already declares its name, and
        // InvalidateHost has always evicted it. The entry is per address because that is what the question
        // is asked by, and InvalidatePortal evicts the whole family - so a change to any portal, role,
        // alias or membership fact the snapshot is built from is visible to the very next request. The
        // snapshot carries the administrator and registered-role keys that authorisation decisions read, so
        // there is no acceptable window in which it may be stale.
        IReadOnlyList<TenantResolution> matches = await ReadResolutionsAsync(chain, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return Result.Failure(
                IPortalContextHolder.NotFoundReasonCode,
                "The host name in the request does not identify a configured portal.");
        }

        string? resolvedAddress = null;
        foreach (string candidate in chain)
        {
            if (matches.Any(match => string.Equals(match.HttpAlias, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                resolvedAddress = candidate;
                break;
            }
        }

        if (resolvedAddress is null)
        {
            // The store answered with rows whose value is in the chain by the repository's comparison but
            // not by this one.
            return Result.Failure(
                IPortalContextHolder.NotFoundReasonCode,
                "The host name in the request does not identify a configured portal.");
        }

        List<TenantResolution> candidates = matches
            .Where(match => string.Equals(match.HttpAlias, resolvedAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Two or more rows for THE SAME address. Refused, not resolved: an installation in this state has a
        // data defect an operator must correct, and serving either candidate would cross a tenant boundary.
        if (candidates.Count > 1)
        {
            return Result.Failure(
                IPortalContextHolder.AmbiguousReasonCode,
                "The host name in the request identifies more than one configured portal.");
        }

        TenantResolution resolution = candidates[0];

        // The four facts the snapshot requires that the Portals table may legitimately leave unset. Each
        // is refused individually so that an operator reading the diagnostics learns which one to set.
        if (resolution.AdministratorId is not { } administratorId)
        {
            return Incomplete("the portal designates no administrator account");
        }

        if (resolution.AdministratorRoleId is not { } administratorRoleId)
        {
            return Incomplete("the portal designates no administrator role");
        }

        if (resolution.RegisteredRoleId is not { } registeredRoleId)
        {
            return Incomplete("the portal designates no registered-user role");
        }

        // The two role NAMES are not columns on Portals - the legacy views produced them with correlated
        // sub-queries over Roles - so the lookup resolves them by key, exactly as those sub-queries did. A
        // role whose name is stored blank is refused rather than carried as present-and-empty, because a
        // blank name is a value capable of matching a role-name comparison.
        if (resolution.AdministratorRoleName is not { Length: > 0 } administratorRoleName)
        {
            return Incomplete("the role designated administrator does not exist on the portal");
        }

        if (resolution.RegisteredRoleName is not { Length: > 0 } registeredRoleName)
        {
            return Incomplete("the role designated registered-user does not exist on the portal");
        }

        if (string.IsNullOrEmpty(resolution.PortalName))
        {
            return Incomplete("the portal has no name");
        }

        // The STORED alias, not the value the caller supplied. They are equal by the lookup's own
        // predicate, so this is a statement of which one is authoritative rather than a correction.
        if (string.IsNullOrEmpty(resolution.HttpAlias))
        {
            return Incomplete("the matched alias has no stored host name");
        }

        _current = new PortalContextAccessor(
            resolution.PortalId,
            resolution.PortalName,
            resolution.HttpAlias,
            resolution.PortalAliasId,
            administratorId,
            administratorRoleId,
            administratorRoleName,
            registeredRoleId,
            registeredRoleName);

        return Result.Success();
    }

    /// <summary>
    /// Reads the resolutions for one address chain, through the shared cache when caching is enabled.
    /// </summary>
    /// <param name="chain">The candidate addresses, most specific first.</param>
    /// <param name="cancellationToken">Propagates abandonment of the operation.</param>
    /// <returns>One resolution per matching alias; empty when none matches.</returns>
    /// <remarks>
    /// A MISS AND A NEGATIVE ANSWER ARE BOTH CACHED, deliberately. An address that names no configured
    /// portal is the shape an unconfigured host and a probe both take, and re-asking the store for it on
    /// every such request would let either one drive load that a configured caller cannot.
    /// </remarks>
    private Task<IReadOnlyList<TenantResolution>> ReadResolutionsAsync(
        IReadOnlyList<string> chain,
        CancellationToken cancellationToken)
    {
        TimeSpan expiration = TimeSpan.FromMinutes(
            AliasResolutionCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        if (expiration <= TimeSpan.Zero)
        {
            // Caching disabled by configuration. The creation path is not reached through the cache at all,
            // which is what the multiplier's zero setting means rather than a zero-length lifetime.
            return _aliases.ResolveTenantsByHttpAliasAsync(chain, cancellationToken);
        }

        // Keyed by the chain rather than by the raw host name, because the chain is what the store is asked
        // and two host names producing the same chain are one question. Ordinal-lower-cased so that a
        // caller's choice of case cannot multiply entries for one address, matching the case insensitivity
        // the store's own comparison applies.
        string cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{AliasResolutionCacheKeyPrefix}|{string.Join('|', chain).ToLowerInvariant()}");

        return _cache.GetOrCreateAsync(
            cacheKey,
            token => _aliases.ResolveTenantsByHttpAliasAsync(chain, token),
            expiration,
            cancellationToken);
    }

    /// <summary>Builds an incomplete-configuration refusal.</summary>
    /// <remarks>
    /// The message names the missing FACT and never its value, nor the host name that led here. Field names
    /// are schema facts and are safe to record; the host name is attacker-supplied text and the stored
    /// values are tenant data, so neither belongs in a message that a boundary might surface.
    /// </remarks>
    /// <param name="detail">Which required tenant fact is missing.</param>
    /// <returns>A failed outcome carrying the incomplete-configuration code.</returns>
    private static Result Incomplete(string detail) =>
        Result.Failure(
            IPortalContextHolder.IncompleteReasonCode,
            $"The portal identified by this request cannot be used because {detail}.");

    /// <summary>Builds the chain of addresses a request could be matched by, most specific first.</summary>
    /// <param name="address">
    /// The address as the caller supplied it: a host name, optionally followed by the request's path.
    /// </param>
    /// <returns>
    /// The single candidate address this request can be matched by, or an empty list when the input carries
    /// no segment at all and when the candidate it would produce exceeds the width the alias column can
    /// hold, since such a value could never have been stored and so can match nothing.
    /// </returns>
    /// <remarks>
    /// A REQUEST THAT MATCHED NO ROUTE IS STILL A 404, not a tenant refusal.
    /// <c>PortalAliasResolutionMiddleware</c> refuses only when an endpoint was matched and that endpoint
    /// requires a tenant, so an address like <c>/childish/api/v1/roles</c> - which no longer rebases onto
    /// any path base and therefore matches no route - is answered by routing exactly as before.
    /// </remarks>
    private static IReadOnlyList<string> BuildAddressChain(string address)
    {
        string trimmed = address.Trim();

        string[] parts = trimmed.Split(
            PortalAliasTopology.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return Array.Empty<string>();
        }

        string authority = parts[0];

        // How many leading path segments could name a tenant, bounded by the topology.
        int considered = Math.Min(parts.Length - 1, MaximumAliasPathSegments);
        int addressable = 0;

        while (addressable < considered
            && PortalAliasTopology.IsAddressableSegment(parts[addressable + 1]))
        {
            addressable++;
        }

        if (addressable == 0)
        {
            // No segment beneath the authority could name a tenant, so the address means the bare host and
            // the bare host is the only thing looked up.
            return authority.Length <= MaximumAliasLength
                ? new[] { authority }
                : Array.Empty<string>();
        }

        string candidate = string.Join(
            PortalAliasTopology.PathSeparator,
            parts.Take(addressable + 1));

        // ⚠ NO FALLBACK. When the address names a tenant segment, that address must match a stored alias
        // exactly or resolve to nothing at all.
        return candidate.Length <= MaximumAliasLength
            ? new[] { candidate }
            : Array.Empty<string>();
    }
}
