namespace DnnMigration.Api.Middleware;

/// <summary>
/// Marks an endpoint that may be served when the request's host name resolves to no portal.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE POLARITY IS OPT-OUT. <see cref="PortalAliasResolutionMiddleware"/> refuses an endpoint that has
/// no other way of naming its tenant when no tenant could be resolved, so an endpoint added later that
/// forgets to think about the question is refused rather than served with no tenant at all. The alternative
/// polarity - an attribute that opts IN to the requirement - fails open, and failing open on tenant
/// resolution is how a request ends up acting on a tenant nobody proved.
/// </para>
/// <para>
/// WHAT QUALIFIES FOR THE MARK. Exactly three shapes, and nothing else:
/// </para>
/// <list type="bullet">
/// <item>
/// An endpoint that reads NO tenant at all, because what it serves is installation-wide reference data
/// rather than a tenant's records.
/// </item>
/// <item>
/// An endpoint that names its tenant by some means OTHER than the host name - a portal identifier in its
/// own request, or the portal claim on the caller's token - where that name is verified against the record
/// being acted on before anything is read or written.
/// </item>
/// <item>
/// An endpoint an operator must be able to reach in order to CREATE the very alias that would let the host
/// name resolve. Without this class of mark, an installation with no portal could never be bootstrapped and
/// an installation whose aliases were entered wrongly could never be repaired, because every route capable
/// of fixing it would be refused for the reason it needed fixing.
/// </item>
/// </list>
/// <para>
/// An endpoint that reads <c>IPortalContext</c>, directly or through a service, does NOT qualify under any
/// of the three and must not carry this attribute: for such an endpoint the host name is the only source of
/// the tenant, and no tenant means no request.
/// </para>
/// <para>
/// A route that carries a <c>portalId</c> segment needs no mark. The middleware treats the presence of that
/// route value as intrinsic proof that the tenant arrives by a means other than the host name, which is what
/// keeps a host account able to administer any portal from a management host name that is deliberately not
/// any portal's alias.
/// </para>
/// <para>
/// Applied to a controller it covers every action on it; applied to an action it covers that action alone.
/// Inherited, so a derived controller cannot silently lose the mark its base declared.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
internal sealed class TenantOptionalAttribute : Attribute
{
    /// <summary>
    /// Initialises a new instance of the <see cref="TenantOptionalAttribute"/> class.
    /// </summary>
    /// <param name="justification">
    /// Why this endpoint may be served without a resolved tenant. Required, and required to be non-blank,
    /// because the mark removes a security check and a removal without a stated reason cannot be reviewed.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="justification"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    public TenantOptionalAttribute(string justification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        Justification = justification;
    }

    /// <summary>Gets the stated reason this endpoint may be served without a resolved tenant.</summary>
    /// <remarks>
    /// Read by the test that enumerates every marked endpoint, which fails when a mark carries no reason.
    /// It is never written to a response: it explains an internal decision to a reviewer and would tell a
    /// caller something about the installation's configuration.
    /// </remarks>
    public string Justification { get; }
}
