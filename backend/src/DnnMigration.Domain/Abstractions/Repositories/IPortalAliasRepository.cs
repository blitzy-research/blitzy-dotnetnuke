using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes <see cref="PortalAlias"/> rows and resolves an incoming host name to a portal.
/// </summary>
/// <remarks>
/// MIGRATION: <see cref="GetByAliasAsync"/> replaces the legacy tenant-resolution predicate
/// <c>where PortalAlias like '%' + @PortalAlias + '%'</c> from the <c>GetPortalSettings</c> procedure
/// with an exact match. The substring form could resolve one tenant's alias to another portal whose
/// alias contained it; the unique constraint added to <c>HTTPAlias</c> in 03.00.07 makes the exact
/// match total. The change is deliberate and is recorded as a behavioural difference.
/// </remarks>
public interface IPortalAliasRepository
{
    /// <summary>Returns every alias, or only those of one portal.</summary>
    /// <param name="portalId">Portal identifier, or <see langword="null"/> for every portal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PortalAlias>> ListAsync(int? portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns one alias by key, or <see langword="null"/>.</summary>
    /// <param name="portalAliasId">Alias identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PortalAlias?> GetAsync(int portalAliasId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a host name, with optional virtual path, to its alias row.</summary>
    /// <param name="httpAlias">The host name to resolve. Matched case-insensitively and exactly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PortalAlias?> GetByAliasAsync(string httpAlias, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a host name is already claimed.</summary>
    /// <param name="httpAlias">The host name to test.</param>
    /// <param name="excludingPortalAliasId">An alias to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> AliasExistsAsync(string httpAlias, int? excludingPortalAliasId = null, CancellationToken cancellationToken = default);

    /// <summary>Stages a new alias for insertion.</summary>
    /// <param name="alias">The alias to insert.</param>
    void Add(PortalAlias alias);

    /// <summary>Stages an alias for deletion.</summary>
    /// <param name="alias">The alias to delete.</param>
    void Remove(PortalAlias alias);

    /// <summary>
    /// The failure code reported when no alias matches the supplied host name exactly.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="AmbiguousReasonCode"/> so that a caller can tell "this host is not
    /// configured" from "this host is configured more than once". Both are refusals; only their diagnostics
    /// differ, and neither is disclosed to the caller verbatim - the host name is attacker-supplied text
    /// and belongs in structured internal diagnostics rather than in a response body.
    /// </remarks>
    public const string NotFoundReasonCode = "PORTAL_ALIAS_NOT_FOUND";

    /// <summary>
    /// The failure code reported when more than one alias matches the supplied host name exactly.
    /// </summary>
    /// <remarks>
    /// Reported rather than resolved. The legacy statement collapsed this case with <c>min(PortalID)</c>
    /// and so served one tenant's content under another tenant's host name; refusing is the only outcome
    /// that cannot silently cross a tenant boundary. An installation that provokes this result has a data
    /// defect an operator must correct, and the ambiguity is recorded in the diagnostics so they can.
    /// </remarks>
    public const string AmbiguousReasonCode = "PORTAL_ALIAS_AMBIGUOUS";

    /// <summary>
    /// Resolves the single portal alias whose stored host name is exactly the supplied value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching is by equality on the whole stored value, never by prefix, suffix, substring or pattern.
    /// The supplied value is data and is never treated as a pattern, so no character within it - including
    /// a wildcard - can broaden what matches.
    /// </para>
    /// <para>
    /// An implementation returns success only when exactly one alias matches. Zero matches yield a failure
    /// carrying <see cref="NotFoundReasonCode"/>; two or more yield a failure carrying
    /// <see cref="AmbiguousReasonCode"/>. There is no third path in which a candidate is chosen.
    /// </para>
    /// <para>
    /// On success the returned alias carries its owning <see cref="PortalAlias.Portal"/>, and that portal
    /// carries its <see cref="Portal.Roles"/>, because the request-scoped tenant context that consumes this
    /// result needs the portal's administrator and registered role names as well as their identifiers, and
    /// those names live on <see cref="Role"/> rather than on <see cref="Portal"/>. Loading them here keeps
    /// tenant resolution to a single round trip and keeps the consumer free of query concerns.
    /// </para>
    /// </remarks>
    /// <param name="httpAlias">
    /// The host name to resolve, as supplied by the caller. Treated as untrusted data throughout.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagates notification that the operation should be abandoned.
    /// </param>
    /// <returns>
    /// A successful result carrying the single matching alias with its portal and that portal's roles
    /// loaded, or a failure carrying <see cref="NotFoundReasonCode"/> or
    /// <see cref="AmbiguousReasonCode"/>.
    /// </returns>
    Task<Result<PortalAlias>> ResolveByHttpAliasAsync(string httpAlias, CancellationToken cancellationToken);
}
