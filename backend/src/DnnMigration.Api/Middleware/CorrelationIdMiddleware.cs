using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Primitives;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Gives every request a correlation identifier, publishes it through <see cref="HttpContext.Items"/> and
/// <see cref="HttpContext.TraceIdentifier"/>, carries it on the ambient logging scope, and echoes it back
/// on the response as the <c>X-Correlation-Id</c> header.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server half of a closed loop.</b> The client half is the single-page application's correlation-id
/// HTTP interceptor, which sends the same header on every outbound call. A request arriving with a usable
/// identifier keeps it, so one identifier spans the browser, this API and every log line either produces.
/// </para>
/// <para>
/// <b>Pipeline position is fixed and must be honoured:</b> immediately after the global exception handler
/// and immediately before request logging.
/// </para>
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    /// <summary>
    /// The name of the HTTP header, read on the request and written on the response, that carries the
    /// correlation identifier.
    /// </summary>
    /// <remarks>
    /// The spelling is contractual: it is shared with the single-page application's HTTP interceptor, and
    /// the cross-origin policy must expose this header for the browser to read it. Header names compare
    /// case-insensitively, so casing is immaterial, but a different spelling is not - it would silently
    /// break the loop, each side looking for a header the other never sends.
    /// </remarks>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// The <see cref="HttpContext.Items"/> key under which the resolved correlation identifier is published
    /// for the remainder of the request.
    /// </summary>
    public const string ItemKey = "DnnMigration.CorrelationId";

    /// <summary>The structured-logging property name under which the identifier joins the ambient scope.</summary>
    private const string ScopePropertyName = "CorrelationId";

    /// <summary>
    /// The length of the unhyphenated canonical form: a <see cref="Guid"/> rendered with <c>"N"</c>.
    /// </summary>
    /// <remarks>
    /// This is the form this stage generates, so an identifier that made a round trip through a caller and
    /// came back is kept rather than replaced - which is what lets a retried request be recognised as the
    /// same logical operation as its first attempt.
    /// </remarks>
    private const int CompactFormLength = 32;

    /// <summary>The length of the hyphenated canonical form: the RFC 4122 <c>8-4-4-4-12</c> rendering.</summary>
    private const int HyphenatedFormLength = 36;

    /// <summary>The character positions of the four hyphens in the hyphenated canonical form.</summary>
    private static readonly int[] HyphenPositions = [8, 13, 18, 23];

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    /// <summary>Initialises a new instance of the <see cref="CorrelationIdMiddleware"/> class.</summary>
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
    /// Resolves the identifier for the current request, publishes it, arranges for it to be echoed on the
    /// response, and runs the remainder of the pipeline inside a scope that carries it.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>A task that completes when the remainder of the pipeline has finished.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string correlationId = Resolve(context);

        // Publish before the pipeline continues, so every downstream component including the exception
        // handler that wraps this one - can read the same value without any of them having to know this
        // middleware exists. Items is the authoritative channel.
        context.Items[ItemKey] = correlationId;
        context.TraceIdentifier = correlationId;

        // Registered before awaiting the pipeline, which is what makes the header survive every outcome: a
        // success, a 4xx produced downstream, and the RFC 7807 response written by the global exception
        // handler.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        Dictionary<string, object> correlationScope = new(1, StringComparer.Ordinal)
        {
            [ScopePropertyName] = correlationId,
        };

        // The scope is opened with a using block so it is disposed even when the pipeline throws, and so
        // the exception the handler above eventually logs is still attributed to this request.
        using (_logger.BeginScope(correlationScope))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Reads the inbound <see cref="HeaderName"/> header and returns either the value it carries, when that
    /// value can be trusted, or a freshly generated identifier.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>The correlation identifier for the request: never <see langword="null"/> and never empty.</returns>
    private string Resolve(HttpContext context)
    {
        StringValues inbound = context.Request.Headers[HeaderName];

        string? candidate = inbound.Count == 1 ? inbound[0] : null;

        if (IsUsable(candidate))
        {
            return candidate;
        }

        // The rejected value is deliberately absent from this entry: writing an unvalidated header into the
        // sink is the very attack the rejection prevents, so only its shape is recorded - the number of
        // header lines that arrived and the length of the one that was refused, neither of which can carry
        // a secret.
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
    /// Decides whether an inbound header value may be trusted as this request's correlation identifier.
    /// </summary>
    /// <param name="candidate">
    /// The raw inbound header value, which may be <see langword="null"/> when the header was absent or
    /// repeated.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="candidate"/> is one of the two canonical forms - <see
    /// cref="CompactFormLength"/> hexadecimal characters, or the hyphenated <see
    /// cref="HyphenatedFormLength"/>-character <c>8-4-4-4-12</c> form - and otherwise <see
    /// langword="false"/>.
    /// </returns>
    /// <remarks>
    /// THE SHAPE IS THE SECURITY CONTROL, and it subsumes the two it replaces.
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

    /// <summary>Reports whether a 36-character value is the canonical hyphenated UUID rendering.</summary>
    /// <param name="candidate">
    /// The value to test; never <see langword="null"/> and exactly <see cref="HyphenatedFormLength"/>
    /// characters.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the four hyphens sit at the specified positions and every other position
    /// holds a hexadecimal digit.
    /// </returns>
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
