using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Declares that every response from this API may vary by the request's <c>Origin</c>, so that no shared
/// cache can serve one origin's cross-origin permission headers to another origin.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS NEEDED. The cross-origin stage writes <c>Access-Control-Allow-Origin</c> for a permitted
/// origin and writes nothing for any other, which makes the response a function of a REQUEST HEADER. The
/// framework's own stage announces that with <c>Vary: Origin</c> only when the policy names MORE THAN ONE
/// origin - reasoning that with a single permitted origin the header can only ever hold one value.
/// </para>
/// <para>
/// WHAT GOES WRONG WITHOUT IT. A shared cache holding the variant produced for a request with no
/// <c>Origin</c> may replay it to the single-page application, which then sees a response with no
/// permission header and reports an opaque cross-origin failure; and a cache holding the permitted variant
/// may replay its permission header to a request from a different origin.
/// </para>
/// </remarks>
public sealed class OriginVaryMiddleware
{
    /// <summary>The request header the cross-origin decision is a function of.</summary>
    private const string OriginHeaderName = "Origin";

    /// <summary>The wildcard <c>Vary</c> value, which already subsumes any specific header.</summary>
    private const string VaryAny = "*";

    private readonly RequestDelegate _next;

    /// <summary>Initialises the middleware.</summary>
    /// <param name="next">The next request stage.</param>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    public OriginVaryMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Declares the origin dependency on one response.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the rest of the pipeline has.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(
            static state =>
            {
                Declare((HttpResponse)state);
                return Task.CompletedTask;
            },
            context.Response);

        return _next(context);
    }

    /// <summary>Adds <c>Origin</c> to a response's <c>Vary</c> header unless it is already covered.</summary>
    /// <param name="response">The response about to be sent.</param>
    private static void Declare(HttpResponse response)
    {
        StringValues existing = response.Headers[HeaderNames.Vary];

        if (existing.Count == 0)
        {
            response.Headers[HeaderNames.Vary] = OriginHeaderName;
            return;
        }

        foreach (string? value in existing)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // A single header value may list several field names, so each entry is split before the
            // comparison. Field names are case-insensitive, and the wildcard is tested first because it
            // makes any specific addition redundant.
            foreach (string candidate in value.Split(','))
            {
                string trimmed = candidate.Trim();

                if (string.Equals(trimmed, VaryAny, StringComparison.Ordinal)
                    || string.Equals(trimmed, OriginHeaderName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        response.Headers.Append(HeaderNames.Vary, OriginHeaderName);
    }
}
