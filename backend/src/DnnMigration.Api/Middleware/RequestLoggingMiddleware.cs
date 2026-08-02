using System.Diagnostics;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Records one structured log entry for every request that reaches the application,
/// carrying what it was, how it ended and how long it took - and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy application had no request log. It wrote audit entries for
/// business events through its own event-log provider - a portal name, a user name, a
/// user id and an event type - and nothing that described a request or tied two
/// entries to the same one. This stage is therefore net-new, and the entry it writes
/// is not a substitute for those audit records: business events are recorded by the
/// services that perform them, at the point where the outcome is known.
/// </para>
/// <para>
/// <b>What is deliberately absent from the entry, and why.</b> No query string, no
/// header, no request body, no response body, no account name, no address. A request
/// log is the highest-volume, longest-retained and most widely-readable log an
/// application produces, so anything personal placed in it is copied everywhere and
/// kept the longest. The query string is the sharpest case: the search endpoints of
/// this API take a free-text filter, and an operator searching for a user by email
/// address would write that address into the log of every request that carried it.
/// The entry therefore records only <em>whether</em> a query string was present, which
/// is what an operator needs in order to tell a filtered request from an unfiltered
/// one.
/// </para>
/// <para>
/// The correlation identifier is not a property of this entry, and that is not an
/// omission: the stage that issues it opens a logging scope around the remainder of
/// the pipeline, so the identifier is attached to this entry - and to every other
/// entry written while handling the request - without this type naming it. That is
/// also why this stage must be composed inside that one.
/// </para>
/// <para>
/// Nothing here catches an exception. A failure is recorded, in full, by the
/// application's exception handler; catching it here would either duplicate that
/// record or, worse, swallow the failure and report the request as ordinary. The
/// timing entry is still written for a failed request, because it is written from a
/// block that runs whatever the outcome.
/// </para>
/// </remarks>
internal sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    /// <summary>
    /// Creates the stage.
    /// </summary>
    /// <param name="next">The remainder of the pipeline.</param>
    /// <param name="logger">Receives the completion entry.</param>
    /// <exception cref="ArgumentNullException">
    /// Either argument is <see langword="null"/>.
    /// </exception>
    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Times the request and records the outcome.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Timing is taken from the high-resolution timestamp rather than from a stopwatch
    /// instance, so measuring a request allocates nothing. This runs on every request,
    /// so its own cost is the one cost paid unconditionally.
    /// </para>
    /// <para>
    /// The entry is written at informational level for an ordinary outcome and at
    /// warning level for a server-error status. A 4xx stays informational: a rejected
    /// request is the application working correctly, and promoting it would fill an
    /// operator's warnings with ordinary validation failures until the genuine ones
    /// were invisible.
    /// </para>
    /// </remarks>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

            int statusCode = context.Response.StatusCode;

            // Three severities rather than two. A 5xx is this application's own fault and an
            // operator has to see it, so it is an error; a 4xx is the caller's and is worth
            // noticing without being alarming, so it is a warning; everything else is routine.
            // Collapsing the first two would either bury real faults among rejected requests or
            // fill the error stream with ordinary validation failures.
            LogLevel level = statusCode >= StatusCodes.Status500InternalServerError
                ? LogLevel.Error
                : statusCode >= StatusCodes.Status400BadRequest
                    ? LogLevel.Warning
                    : LogLevel.Information;

            // The path is logged without its query string; only the presence of one is
            // recorded. See this type's remarks for why.
            _logger.Log(
                level,
                "{RequestMethod} {RequestPath} completed with {StatusCode} in {ElapsedMilliseconds:F1} ms. Query string present: {HasQueryString}.",
                context.Request.Method,
                context.Request.Path.Value,
                statusCode,
                elapsed.TotalMilliseconds,
                context.Request.QueryString.HasValue);
        }
    }
}
