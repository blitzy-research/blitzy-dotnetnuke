namespace DnnMigration.Api.Middleware;

/// <summary>Marks an endpoint that may be served when the request's host name resolves to no portal.</summary>
/// <remarks>
/// <para>
/// WHY THE POLARITY IS OPT-OUT. <see cref="PortalAliasResolutionMiddleware"/> refuses an endpoint that has
/// no other way of naming its tenant when no tenant could be resolved, so an endpoint added later that
/// forgets to think about the question is refused rather than served with no tenant at all.
/// </para>
/// <para>
/// An endpoint that reads <c>IPortalContext</c>, directly or through a service, does NOT qualify under any
/// of the three and must not carry this attribute: for such an endpoint the host name is the only source of
/// the tenant, and no tenant means no request.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
internal sealed class TenantOptionalAttribute : Attribute
{
    /// <summary>Initialises a new instance of the <see cref="TenantOptionalAttribute"/> class.</summary>
    /// <param name="justification">Why this endpoint may be served without a resolved tenant.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="justification"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    public TenantOptionalAttribute(string justification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        Justification = justification;
    }

    /// <summary>Gets the stated reason this endpoint may be served without a resolved tenant.</summary>
    public string Justification { get; }
}
