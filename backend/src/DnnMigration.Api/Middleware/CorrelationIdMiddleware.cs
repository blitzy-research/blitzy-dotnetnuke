using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Primitives;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Gives every request a correlation identifier, publishes it through
/// <see cref="HttpContext.Items"/> and <see cref="HttpContext.TraceIdentifier"/>, carries it on the
/// ambient logging scope, and echoes it back on the response as the <c>X-Correlation-Id</c> header.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server half of a closed loop.</b> The client half is the single-page application's
/// correlation-id HTTP interceptor, which sends the same header on every outbound call. A request
/// arriving with a usable identifier keeps it, so one identifier spans the browser, this API and
/// every log line either produces.
/// </para>
/// <para>
/// <b>Pipeline position is fixed and must be honoured:</b> immediately after the global exception
/// handler and immediately before request logging. Running before request logging is what makes
/// every log line carry the identifier; running inside the exception handler is what makes an
/// RFC 7807 <c>ProblemDetails</c> error response carry the header, because it is registered as an
/// <see cref="HttpResponse.OnStarting(Func{Task})"/> callback before the pipeline continues rather
/// than assigned after it returns. By the time control returns the response has usually already
/// started, and a direct assignment at that point is silently discarded.
/// </para>
/// <para>
/// <b>An inbound value is untrusted input.</b> It flows straight into a log sink and back onto a
/// response, so it is validated before it is trusted. A value that fails validation is replaced
/// with a freshly generated identifier: never repaired, never echoed, and never answered with a
/// rejection status, because a correlation identifier is a diagnostic aid and refusing the request
/// over one would be a denial of service the caller controls.
/// </para>
/// <para>
/// <b>One instance serves the whole application.</b> Registration through
/// <c>UseMiddleware&lt;CorrelationIdMiddleware&gt;()</c> activates a single instance for the
/// application's lifetime, so this type must hold nothing request-scoped. Its only dependencies are
/// the pipeline continuation and a logger, both safe to hold for that lifetime; per-request state
/// lives on the <see cref="HttpContext"/> passed to <see cref="InvokeAsync(HttpContext)"/> and
/// nowhere else.
/// </para>
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    /// <summary>
    /// The name of the HTTP header, read on the request and written on the response, that carries
    /// the correlation identifier.
    /// </summary>
    /// <remarks>
    /// The spelling is contractual: it is shared with the single-page application's HTTP
    /// interceptor, and the cross-origin policy must expose this header for the browser to read it.
    /// Header names compare case-insensitively, so casing is immaterial, but a different spelling is
    /// not - it would silently break the loop, each side looking for a header the other never
    /// sends. Consumers reference this constant rather than repeating the literal.
    /// </remarks>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// The <see cref="HttpContext.Items"/> key under which the resolved correlation identifier is
    /// published for the remainder of the request.
    /// </summary>
    /// <remarks>
    /// The stored value is always a non-empty <see cref="string"/>. Downstream components read it
    /// from here rather than re-reading and re-validating the request header, so they observe the
    /// validated identifier rather than whatever the caller sent. The key is namespaced to stay
    /// distinct from keys owned by the framework or by anything else sharing the dictionary.
    /// </remarks>
    public const string ItemKey = "DnnMigration.CorrelationId";

    /// <summary>
    /// The structured-logging property name under which the identifier joins the ambient scope.
    /// </summary>
    private const string ScopePropertyName = "CorrelationId";

    /// <summary>
    /// The greatest length, in characters, at which an inbound header value is still trusted.
    /// </summary>
    /// <remarks>
    /// A generated identifier is 32 characters, so this leaves generous room for a caller's own
    /// scheme while keeping an oversized header from being copied onto every log line the request
    /// produces.
    /// </remarks>
    private const int MaxLength = 128;

    /// <summary>
    /// The lowest character accepted in an inbound identifier: the space, the first printable
    /// character in US-ASCII.
    /// </summary>
    private const char LowestAcceptedCharacter = ' ';

    /// <summary>
    /// The highest character accepted in an inbound identifier: the tilde, the last printable
    /// character in US-ASCII.
    /// </summary>
    private const char HighestAcceptedCharacter = '~';

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next component in the request pipeline.</param>
    /// <param name="logger">The logger whose scope downstream components log within.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="next"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the identifier for the current request, publishes it, arranges for it to be echoed
    /// on the response, and runs the remainder of the pipeline inside a scope that carries it.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>A task that completes when the remainder of the pipeline has finished.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string correlationId = Resolve(context);

        // Publish before the pipeline continues, so every downstream component -
        // including the exception handler that wraps this one - can read the same
        // value without any of them having to know this middleware exists. Items is
        // the authoritative channel. TraceIdentifier is aligned to the same value so
        // that the framework's own request identifier and the correlation identifier
        // cannot disagree; note that a ProblemDetails payload prefers the ambient
        // Activity id over TraceIdentifier and falls back to it only when no Activity
        // is running, so the response header - not traceId - is the dependable
        // carrier, and that is why the header is registered unconditionally below.
        context.Items[ItemKey] = correlationId;
        context.TraceIdentifier = correlationId;

        // Registered before awaiting the pipeline, which is what makes the header
        // survive every outcome: a success, a 4xx produced downstream, and the
        // RFC 7807 response written by the global exception handler. Assignment
        // through the indexer replaces any existing value, so the header cannot end
        // up present twice even if something downstream set it too.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        Dictionary<string, object> correlationScope = new(1, StringComparer.Ordinal)
        {
            [ScopePropertyName] = correlationId,
        };

        // The scope is opened with a using block so it is disposed even when the
        // pipeline throws, and so the exception the handler above eventually logs is
        // still attributed to this request. Scopes are ambient across the logging
        // provider, so every downstream logger carries the property without any of
        // them taking a dependency on this type.
        using (_logger.BeginScope(correlationScope))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Reads the inbound <see cref="HeaderName"/> header and returns either the value it carries,
    /// when that value can be trusted, or a freshly generated identifier.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>
    /// The correlation identifier for the request: never <see langword="null"/> and never empty. A
    /// generated identifier is a <see cref="Guid"/> formatted with <c>"N"</c>, giving 32 hexadecimal
    /// characters with no hyphens or braces, which is safe in a header, a URL and a log line
    /// without escaping.
    /// </returns>
    private string Resolve(HttpContext context)
    {
        StringValues inbound = context.Request.Headers[HeaderName];

        // Exactly one header line is the only shape trusted. A repeated header is
        // joined with commas on read, and echoing a smuggled composite back to the
        // caller would be worse than starting fresh.
        string? candidate = inbound.Count == 1 ? inbound[0] : null;

        if (IsUsable(candidate))
        {
            return candidate;
        }

        // The rejected value is deliberately absent from this entry: writing an
        // unvalidated header into the sink is the very attack the rejection
        // prevents, so only its shape is recorded. Trace level, because the request
        // and response envelope belongs to the request-logging middleware that runs
        // next and this must not duplicate it. The level is tested first because an
        // absent header is an ordinary case on this path - a health probe sends none
        // - and the arguments would otherwise be boxed into an array on every such
        // request only for a disabled logger to discard them.
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "No usable inbound {HeaderName} header was supplied, so a correlation identifier was generated. Inbound header values: {InboundValueCount}. Rejected length: {RejectedLength}.",
                HeaderName,
                inbound.Count,
                candidate?.Length ?? 0);
        }

        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Decides whether an inbound header value may be trusted as this request's correlation
    /// identifier.
    /// </summary>
    /// <param name="candidate">
    /// The raw inbound header value, which may be <see langword="null"/> when the header was absent
    /// or repeated.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="candidate"/> holds something other than
    /// whitespace, is no longer than <see cref="MaxLength"/> characters, and consists entirely of
    /// printable US-ASCII; otherwise <see langword="false"/>. The nullable-state annotation is what
    /// lets both this method and its caller treat an accepted value as non-null without a
    /// suppression.
    /// </returns>
    private static bool IsUsable([NotNullWhen(true)] string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Length > MaxLength)
        {
            return false;
        }

        // Accepting only printable US-ASCII rejects every control character in a
        // single test, carriage return and line feed among them. That is the point
        // of this method: a value carrying CR or LF could otherwise terminate a log
        // line, or a response header, and let the caller append a line of its own.
        return !candidate.AsSpan().ContainsAnyExceptInRange(LowestAcceptedCharacter, HighestAcceptedCharacter);
    }
}
