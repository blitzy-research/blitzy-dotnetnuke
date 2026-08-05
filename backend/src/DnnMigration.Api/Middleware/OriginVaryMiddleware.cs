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
/// origin - reasoning that with a single permitted origin the header can only ever hold one value. That
/// reasoning is incomplete, because the response still differs between a request that carried a permitted
/// origin, one that carried some other origin, and one that carried none at all: the first gets the
/// permission header, the other two do not. A single-origin policy is exactly this deployment's
/// configuration, so in practice no response carried the declaration.
/// </para>
/// <para>
/// WHAT GOES WRONG WITHOUT IT. A shared cache holding the variant produced for a request with no
/// <c>Origin</c> may replay it to the single-page application, which then sees a response with no
/// permission header and reports an opaque cross-origin failure; and a cache holding the permitted
/// variant may replay its permission header to a request from a different origin. Neither is a
/// vulnerability in this process - a browser still enforces the header it receives, and the permitted
/// origin list is unchanged - but both are wrong answers a caller cannot diagnose, and both are removed by
/// one response header.
/// </para>
/// <para>
/// WHY IT IS UNCONDITIONAL. Emitting the declaration only when the request carried an <c>Origin</c> would
/// leave precisely the hole that matters: the response to a request WITHOUT one would then carry no
/// <c>Vary</c>, so a cache is entitled to reuse it for a request WITH one. The declaration has to be on
/// the variant that lacks the permission header just as much as on the variant that has it. Every route
/// this API exposes is reachable from the browser application, so there is no subset to which this could
/// sensibly be narrowed.
/// </para>
/// <para>
/// The value is APPENDED rather than assigned, and only when it is not already declared, so a stage that
/// varies a response by some other header keeps its own declaration. A response already declaring
/// <c>Vary: *</c> is left untouched: that wildcard says the response varies by everything, which already
/// includes the origin and is stricter than anything this stage would add.
/// </para>
/// </remarks>
public sealed class OriginVaryMiddleware
{
    /// <summary>The request header the cross-origin decision is a function of.</summary>
    private const string OriginHeaderName = "Origin";

    /// <summary>
    /// The wildcard <c>Vary</c> value, which already subsumes any specific header.
    /// </summary>
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
    /// <remarks>
    /// Attached through a response-starting callback rather than written here, so the declaration lands on
    /// whatever eventually writes the response - a controller result, a refusal from a stage that
    /// short-circuits, the problem document from the exception handler, or a preflight answered by the
    /// cross-origin stage itself. Writing it on the way in would leave it liable to be replaced by a later
    /// stage that assigns the header outright.
    /// </remarks>
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
