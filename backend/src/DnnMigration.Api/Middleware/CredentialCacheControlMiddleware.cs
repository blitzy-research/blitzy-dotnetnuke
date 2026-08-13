using DnnMigration.Api.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Marks every response produced by a credential endpoint as never cacheable, and every response produced by
/// an endpoint that requires authorisation as non-storable and private, so that neither a bearer credential
/// nor the personal data an authorised read returns can be retained by a browser, a shared proxy or any other
/// intermediary.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ TWO RULES, AND THE NAME RECORDS ONLY THE FIRST. The class was authored for credential endpoints and keeps
/// that name so the security review that named the file, and the pipeline registration that names the type,
/// still point at the same place. PRIV-03 added the second rule: every authorised endpoint's response is
/// marked non-storable as well. The two directives differ deliberately and both are stated in full below.
/// </para>
/// <para>
/// MIGRATION: PRIV-03. WHY THE SECOND RULE EXISTS. Only credential endpoints were covered, and a previous regression
/// case asserted that an authenticated portal read was NOT marked - reasoning that marking everything would
/// make the credential assertion vacuous. That reasoning inverted the priorities. This is an administration
/// API whose authorised reads return account names, e-mail addresses, profile values, role memberships and
/// tenant configuration; none of it is a public cacheable resource, and every one of those responses could be
/// retained in a private browser cache and read from disk afterwards by anyone with access to the machine, or
/// re-displayed by pressing Back after a sign-out. The distinction between the two rules is preserved instead:
/// the credential rule adds the HTTP/1.0 and heuristic-freshness spellings, and only the credential rule does.
/// </para>
/// <para>
/// WHY "REQUIRES AUTHORISATION" AND NOT A LIST OF SENSITIVE ROUTES. A list is a second copy of a decision the
/// endpoints already carry, and it fails silently: an action added later is absent from it, and nothing about
/// omitting a route from a cache-control list looks like a mistake. Authorisation metadata is the property
/// that actually distinguishes the responses at issue - an endpoint requiring a caller to prove who they are
/// is, by construction, returning something that belongs to that caller. Anonymous endpoints are untouched,
/// which keeps the health probe and the API documentation exactly as they were.
/// </para>
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
/// SCOPE. Nothing anonymous is touched. The anonymous health endpoint is already answered with caching
/// disabled by its own options, the API documentation is served only outside production, and an anonymous
/// endpoint returns nothing that belongs to a particular caller.
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

    /// <summary>The directive applied to every response from an endpoint that requires authorisation.</summary>
    /// <remarks>
    /// <para>
    /// PRIV-03. Three directives, each doing separate work. <c>no-store</c> forbids writing the response to any
    /// storage at all, which is what keeps a personal-data response out of the browser's on-disk cache.
    /// <c>private</c> forbids a SHARED cache from holding it even if the response were storable - the proxy in
    /// front of this API serves every tenant, so a response cached there would be served to the wrong caller.
    /// <c>max-age=0</c> is the belt-and-braces value for an intermediary that mishandles the first two and
    /// falls back to freshness arithmetic.
    /// </para>
    /// <para>
    /// Published as a constant because the regression suite asserts it verbatim: a value drifting here without
    /// the assertion following would remove a privacy control silently.
    /// </para>
    /// <para>
    /// It is deliberately NOT the credential spelling. A credential response additionally carries
    /// <c>Pragma</c> and <c>Expires</c> for HTTP/1.0 proxies and heuristic caches, because leaking a bearer
    /// token is worse than leaking a display name and is worth the redundant headers on every response from
    /// four endpoints. Emitting all five on every authorised response would put two more headers on every
    /// request the application makes for no additional protection that <c>no-store</c> does not already give.
    /// </para>
    /// </remarks>
    internal const string AuthorizedCacheControlValue = "no-store, private, max-age=0";

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
            // exclusively. Registering both would leave whichever callback ran last in charge of the header,
            // which is an ordering dependency for no gain.
            return _next(context);
        }

        // PRIV-03. Every endpoint that requires the caller to prove who they are is returning something that
        // belongs to that caller, so its response must not be storable by a private cache and must never be
        // held by the shared proxy in front of this API.
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

    /// <summary>
    /// Reports whether the matched endpoint requires an authorised caller.
    /// </summary>
    /// <param name="endpoint">The endpoint routing selected, or <see langword="null"/> when none matched.</param>
    /// <returns><see langword="true"/> when the endpoint's response belongs to a particular caller.</returns>
    /// <remarks>
    /// <para>
    /// PRIV-03. Read from METADATA rather than from the caller's identity, and the distinction is what makes
    /// this correct on a refusal: a 401 and a 403 are answered before any identity is established, and both are
    /// responses to a request for personal data. Keying on <c>context.User</c> would leave exactly those two
    /// unmarked.
    /// </para>
    /// <para>
    /// <see cref="IAllowAnonymous"/> wins over <see cref="IAuthorizeData"/>, which is the same precedence the
    /// authorisation middleware itself applies - an action that opts out of authorisation is anonymous however
    /// its controller is decorated, and marking its response would be a claim about a caller there is none of.
    /// </para>
    /// <para>
    /// An unmatched request - no endpoint at all - is left alone. Routing has already failed, so the response is
    /// a 404 that carries nothing belonging to anybody.
    /// </para>
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
