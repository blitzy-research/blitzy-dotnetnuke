using DnnMigration.Api.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Marks every response produced by a credential endpoint as never cacheable, and every response produced
/// by an endpoint that requires authorisation as non-storable and private, so that neither a bearer
/// credential nor the personal data an authorised read returns can be retained by a browser, a shared proxy
/// or any other intermediary.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ TWO RULES, AND THE NAME RECORDS ONLY THE FIRST. The class was authored for credential endpoints and
/// keeps that name so the security review that named the file, and the pipeline registration that names the
/// type, still point at the same place. PRIV-03 added the second rule: every authorised endpoint's response
/// is marked non-storable as well.
/// </para>
/// <para>
/// PRIV-03. WHY THE SECOND RULE EXISTS. Only credential endpoints were covered, and a previous regression
/// case asserted that an authenticated portal read was NOT marked - reasoning that marking everything would
/// make the credential assertion vacuous.
/// </para>
/// </remarks>
internal sealed class CredentialCacheControlMiddleware
{
    /// <summary>The directive applied to every credential-endpoint response.</summary>
    /// <remarks>
    /// Published as a constant because it is asserted verbatim by the regression suite: a value drifting
    /// here without the assertion following would remove a security control silently.
    /// </remarks>
    internal const string CacheControlValue = "no-store, no-cache, must-revalidate";

    /// <summary>The HTTP/1.0 directive applied alongside <see cref="CacheControlValue"/>.</summary>
    internal const string PragmaValue = "no-cache";

    /// <summary>The freshness bound applied alongside <see cref="CacheControlValue"/>.</summary>
    internal const string ExpiresValue = "0";

    /// <summary>The directive applied to every response from an endpoint that requires authorisation.</summary>
    /// <remarks>
    /// PRIV-03.
    /// </remarks>
    internal const string AuthorizedCacheControlValue = "no-store, private, max-age=0";

    private readonly RequestDelegate _next;

    /// <summary>Initialises the middleware.</summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    public CredentialCacheControlMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    /// <summary>Registers the cache directives for a credential endpoint and hands the request on.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Endpoint? endpoint = context.GetEndpoint();

        if (endpoint?.Metadata.GetMetadata<CredentialEndpointAttribute>() is not null)
        {
            // Assigned rather than appended, so a directive composed by another producer cannot leave a
            // contradictory pair such as "no-store, max-age=60" on one response.
            context.Response.OnStarting(
                static state =>
                {
                    HttpResponse response = (HttpResponse)state;

                    response.Headers[HeaderNames.CacheControl] = CacheControlValue;
                    response.Headers[HeaderNames.Pragma] = PragmaValue;
                    response.Headers[HeaderNames.Expires] = ExpiresValue;

                    return Task.CompletedTask;
                },
                context.Response);

            // The credential rule is the stronger of the two and subsumes the other, so it is applied
            // exclusively. Registering both would leave whichever callback ran last in charge of the
            // header, which is an ordering dependency for no gain.
            return _next(context);
        }

        // PRIV-03. Every endpoint that requires the caller to prove who they are is returning something
        // that belongs to that caller, so its response must not be storable by a private cache and must
        // never be held by the shared proxy in front of this API.
        if (RequiresAuthorization(endpoint))
        {
            context.Response.OnStarting(
                static state =>
                {
                    HttpResponse response = (HttpResponse)state;

                    // Assigned, for the same reason as above: appending could leave "private, max-age=0" beside
                    // a "public" a framework component had already written.
                    response.Headers[HeaderNames.CacheControl] = AuthorizedCacheControlValue;

                    return Task.CompletedTask;
                },
                context.Response);
        }

        return _next(context);
    }

    /// <summary>Reports whether the matched endpoint requires an authorised caller.</summary>
    /// <param name="endpoint">The endpoint routing selected, or <see langword="null"/> when none matched.</param>
    /// <returns><see langword="true"/> when the endpoint's response belongs to a particular caller.</returns>
    /// <remarks>
    /// PRIV-03. Read from METADATA rather than from the caller's identity, and the distinction is what
    /// makes this correct on a refusal: a 401 and a 403 are answered before any identity is established,
    /// and both are responses to a request for personal data.
    /// </remarks>
    private static bool RequiresAuthorization(Endpoint? endpoint)
    {
        if (endpoint is null)
        {
            return false;
        }

        return endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
            && endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
    }
}
