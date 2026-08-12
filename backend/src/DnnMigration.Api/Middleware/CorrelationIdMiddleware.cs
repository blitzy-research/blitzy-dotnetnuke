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
/// <b>ONLY A CANONICAL, NON-SEMANTIC SHAPE IS TRUSTED, AND THAT IS THE WHOLE OF THE VALIDATION
/// RULE.</b> An earlier revision accepted any non-blank printable US-ASCII value up to 128
/// characters. That blocked carriage return and line feed - so it closed log forging and response
/// splitting - but it did nothing about SEMANTIC content, and semantic content is the more
/// consequential exposure: a caller could send a password, a bearer token, an e-mail address or an
/// API key as its correlation header and this stage would then publish that value to
/// <see cref="HttpContext.Items"/>, to <see cref="HttpContext.TraceIdentifier"/>, onto the ambient
/// logging scope that every request, exception and audit entry is written within, back onto the
/// response header, into the RFC 7807 <c>correlationId</c> member and finally onto the operator's
/// screen as a support reference. A secret reaching retained logs by way of a diagnostic aid is a
/// disclosure whichever way it arrived, and the caller chose it.
/// </para>
/// <para>
/// The shape accepted is therefore closed to two forms, both of which are pure hexadecimal and
/// therefore carry no words, no punctuation and no structure a secret could be smuggled inside:
/// 32 hexadecimal characters, which is what this stage itself generates, and the canonical
/// hyphenated 36-character UUID form <c>8-4-4-4-12</c>, which is what the single-page application
/// generates. Case is immaterial and is neither required nor rewritten. Anything else - including
/// a caller's own scheme, however well intentioned - is discarded and replaced, so nothing this
/// stage publishes can be a value with meaning to anybody.
/// </para>
/// <para>
/// THE CLIENT APPLIES THE IDENTICAL RULE, in <c>core/interceptors/correlation-id.interceptor.ts</c>,
/// and the two must not drift: a value one side keeps and the other replaces leaves the browser
/// holding an identifier that appears in no server log line, which is indistinguishable from having
/// none. The reverse proxy applies the same rule a third time, in <c>docker/nginx.conf</c>, where a
/// non-canonical value is replaced with nginx's own 32-hexadecimal request identifier before it is
/// forwarded here.
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
    /// The length of the unhyphenated canonical form: a <see cref="Guid"/> rendered with <c>"N"</c>.
    /// </summary>
    /// <remarks>
    /// This is the form this stage generates, so an identifier that made a round trip through a
    /// caller and came back is kept rather than replaced - which is what lets a retried request be
    /// recognised as the same logical operation as its first attempt.
    /// </remarks>
    private const int CompactFormLength = 32;

    /// <summary>
    /// The length of the hyphenated canonical form: the RFC 4122 <c>8-4-4-4-12</c> rendering.
    /// </summary>
    /// <remarks>
    /// This is the form the single-page application generates, through
    /// <c>crypto.randomUUID()</c> or an equivalent, so a browser-supplied identifier takes this
    /// branch.
    /// </remarks>
    private const int HyphenatedFormLength = 36;

    /// <summary>
    /// The character positions of the four hyphens in the hyphenated canonical form.
    /// </summary>
    /// <remarks>
    /// Declared rather than derived so the shape test reads as the specification it enforces. Every
    /// other position in that form must be a hexadecimal digit.
    /// </remarks>
    private static readonly int[] HyphenPositions = [8, 13, 18, 23];

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
        // prevents, so only its shape is recorded - the number of header lines that
        // arrived and the length of the one that was refused, neither of which can
        // carry a secret. Trace level, because the request and response envelope
        // belongs to the request-logging middleware that runs next and this must not
        // duplicate it. The level is tested first because an absent header is an
        // ordinary case on this path - a health probe sends none - and the arguments
        // would otherwise be boxed into an array on every such request only for a
        // disabled logger to discard them.
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "No canonical inbound {HeaderName} header was supplied, so a correlation identifier was generated. Inbound header values: {InboundValueCount}. Rejected length: {RejectedLength}.",
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
    /// <see langword="true"/> when <paramref name="candidate"/> is one of the two canonical forms -
    /// <see cref="CompactFormLength"/> hexadecimal characters, or the hyphenated
    /// <see cref="HyphenatedFormLength"/>-character <c>8-4-4-4-12</c> form - and otherwise
    /// <see langword="false"/>. The nullable-state annotation is what lets both this method and its
    /// caller treat an accepted value as non-null without a suppression.
    /// </returns>
    /// <remarks>
    /// <para>
    /// THE SHAPE IS THE SECURITY CONTROL, and it subsumes the two it replaces. Every character
    /// admitted is a hexadecimal digit or, at four fixed positions, a hyphen, so a control character
    /// cannot appear - which closes the log forging and response splitting the previous
    /// printable-ASCII test existed for - and neither can a word, a delimiter, an at-sign, a dot or
    /// a slash, which is what closes the semantic exposure that test did not address. There is no
    /// separate length bound because each accepted form has one exact length.
    /// </para>
    /// <para>
    /// The value is NEITHER TRIMMED NOR RE-CASED. A caller's identifier is either usable exactly as
    /// it arrived or is replaced outright, because a repaired value is one the caller has never
    /// seen: it would appear in this server's logs while the caller quoted the value it sent, which
    /// is the precise failure a shared identifier exists to prevent. Case is accepted in either
    /// register and preserved as received for the same reason.
    /// </para>
    /// <para>
    /// No regular expression and no allocation. This runs on every request, before anything else in
    /// the pipeline, so the test is a single pass over the characters already in memory.
    /// </para>
    /// </remarks>
    private static bool IsUsable([NotNullWhen(true)] string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        return candidate.Length switch
        {
            CompactFormLength => IsHexadecimalThroughout(candidate),
            HyphenatedFormLength => IsHyphenatedUuid(candidate),
            _ => false,
        };
    }

    /// <summary>Reports whether every character of a value is a hexadecimal digit.</summary>
    /// <param name="candidate">The value to test; never <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the value is hexadecimal throughout.</returns>
    private static bool IsHexadecimalThroughout(string candidate)
    {
        foreach (char character in candidate)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports whether a 36-character value is the canonical hyphenated UUID rendering.
    /// </summary>
    /// <param name="candidate">
    /// The value to test; never <see langword="null"/> and exactly
    /// <see cref="HyphenatedFormLength"/> characters.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the four hyphens sit at the specified positions and every other
    /// position holds a hexadecimal digit.
    /// </returns>
    /// <remarks>
    /// Tested by position rather than through <see cref="Guid.TryParseExact(string, string, out
    /// Guid)"/> because a successful parse would answer a different question. That method accepts a
    /// value this stage must refuse - the braced and parenthesised renderings among them, depending
    /// on the format specifier - and, more to the point, it discards the original text: a parse
    /// tells us a value CAN be read as an identifier, whereas what has to be true here is that the
    /// text about to be logged and echoed is already in the one shape agreed with the client and the
    /// proxy.
    /// </remarks>
    private static bool IsHyphenatedUuid(string candidate)
    {
        for (int position = 0; position < candidate.Length; position++)
        {
            bool hyphenExpected = Array.IndexOf(HyphenPositions, position) >= 0;
            char character = candidate[position];

            if (hyphenExpected)
            {
                if (character != '-')
                {
                    return false;
                }

                continue;
            }

            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
