using DnnMigration.Api.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Marks every response produced by a credential endpoint as never cacheable, so that an access token, a
/// refresh token or a credential-bearing refusal cannot be retained by a browser, a shared proxy or any
/// other intermediary.
/// </summary>
/// <remarks>
/// <para>
/// WHY A PIPELINE STAGE AND NOT AN ACTION FILTER. An action filter runs only when an action runs, and the
/// responses that matter most here are the ones where it does not: the rate limiter answers 429 before the
/// action is reached, authentication answers 401, authorisation answers 403, and model-state validation
/// answers 400. Every one of those is a response to a request addressed to a credential endpoint, and an
/// intermediary that caches any of them serves a stale security decision to the next caller. A stage placed
/// immediately after routing sees all of them.
/// </para>
/// <para>
/// WHY THE HEADERS ARE ATTACHED THROUGH A CALLBACK. Writing them here would be writing them before the
/// response exists: a later stage may replace the whole header collection (the problem-details writer does),
/// and a header written now can be overwritten then. <see cref="HttpResponse.OnStarting(Func{Task})"/> runs
/// once, immediately before the first byte reaches the wire and after every producer has finished composing,
/// which is the only point at which "the response definitely carries these headers" can be guaranteed.
/// </para>
/// <para>
/// WHY THE ENDPOINT DECIDES, NOT THE PATH. The classification comes from
/// <see cref="CredentialEndpointAttribute"/> - the same metadata the credential rate limiter reads - so an
/// endpoint is treated as credential-bearing because its author said so, and one statement at the action
/// governs both the budget it consumes and the cacheability of its responses. Deriving it from the path here
/// would be a second, weaker copy of a rule that already exists, free to disagree with it.
/// </para>
/// <para>
/// WHAT IS EMITTED, AND WHY THREE HEADERS RATHER THAN ONE.
/// <c>Cache-Control: no-store, no-cache, must-revalidate</c> is the directive that binds every conforming
/// HTTP/1.1 cache; <c>no-store</c> alone is sufficient in principle, and the other two are present because
/// intermediaries that mishandle <c>no-store</c> are common enough that the belt-and-braces spelling is the
/// recommended one. <c>Pragma: no-cache</c> is the HTTP/1.0 equivalent, which some corporate proxies still
/// honour in preference. <c>Expires: 0</c> closes the same gap for a cache that heuristically assigns a
/// freshness lifetime.
/// </para>
/// <para>
/// SCOPE. Nothing outside a credential endpoint is touched. Ordinary resource responses keep whatever
/// caching semantics their own contract implies, and the anonymous health endpoint is already answered with
/// caching disabled by its own options.
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
    /// <remarks>
    /// Zero rather than a date in the past. Both are treated as already stale, and zero cannot be
    /// misparsed by a cache that expects a date it does not recognise.
    /// </remarks>
    internal const string ExpiresValue = "0";

    private readonly RequestDelegate _next;

    /// <summary>Initialises the middleware.</summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="next"/> is <see langword="null"/>.</exception>
    public CredentialCacheControlMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    /// <summary>
    /// Registers the cache directives for a credential endpoint and hands the request on.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<CredentialEndpointAttribute>() is not null)
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
        }

        return _next(context);
    }
}
