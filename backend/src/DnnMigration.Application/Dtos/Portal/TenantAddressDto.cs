namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Wire contract describing how THIS deployment addresses the tenant that the calling request resolved to:
/// the path prefix the request was rebased beneath, or the empty string when the request reached no tenant
/// beneath a path segment at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHY A CONTRACT EXISTS FOR ONE STRING.</strong> One built browser bundle is served to every
/// tenant, so the single-page application cannot know at build time whether the first segment of the
/// address it was loaded from names a child portal (<c>host/acme</c>, a stored <c>PortalAlias</c>) or is a
/// mistyped console route (<c>host/portls</c> for <c>host/portals</c>). Both look identical to a client:
/// one segment of alias shape that is not one of the console's own routes. Only the deployment holds the
/// alias rows that tell them apart, so the client asks, and this is the answer.
/// </para>
/// <para>
/// <strong>IT DISCLOSES NOTHING BUT THE CALLER'S OWN ADDRESS.</strong> The member below is the path portion
/// of the alias the request itself resolved by - the caller supplied it - and no portal identifier, portal
/// name, alias key or other alias is carried. There is deliberately no listing operation and no lookup by
/// value: a caller learns whether the address IT used reaches a tenant, which the sign-in endpoint already
/// distinguishes for an unconfigured address, and learns nothing about any address it did not ask about.
/// </para>
/// <para>
/// MIGRATION: no legacy counterpart exists. The legacy application resolved the tenant server-side on every
/// request and rendered the page for it, so no client ever had to ask - the question only arises because the
/// presentation moved into the browser while tenant identity stayed with the request. The legacy resolution
/// procedure it replaces matched an alias with a substring predicate
/// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> lines 4569 to 4600),
/// which is exactly the behaviour this API refuses: the value below reports a whole-segment match or
/// nothing.
/// </para>
/// </remarks>
public sealed class TenantAddressDto
{
    /// <summary>
    /// Gets or sets the path prefix beneath which this deployment addresses the tenant the calling request
    /// resolved to - for example <c>/acme</c> - or the empty string when it resolved to a bare-host alias or
    /// to no portal at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value INCLUDES its leading separator when present, so a client can concatenate it with a
    /// root-relative path without deciding where the separator goes, and it is reported exactly as the alias
    /// row stores it rather than case-folded: aliases are matched case-insensitively, so a caller that
    /// reached this endpoint under a different case is answered with the stored spelling and can compare
    /// case-insensitively.
    /// </para>
    /// <para>
    /// The empty string is a real answer and never a missing one. It is what a request to the bare host
    /// receives, which is the ordinary single-tenant deployment, and it is also what a request beneath an
    /// unrecognised segment would receive if it reached this endpoint at all - which it cannot, because an
    /// unrecognised segment is never moved out of the routable path and therefore matches no route.
    /// </para>
    /// </remarks>
    public string PathPrefix { get; set; } = string.Empty;
}
