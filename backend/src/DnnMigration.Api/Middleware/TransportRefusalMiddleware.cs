using Microsoft.AspNetCore.Diagnostics;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Answers a refusal the HOST decided - an oversized or malformed request body - without letting it be
/// recorded as an unhandled fault.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS STAGE EXISTS AT ALL. The host raises <see cref="BadHttpRequestException"/> when it declines to
/// read a request itself: a body beyond the configured size ceiling, a malformed chunked body, a declared
/// length that disagrees with what arrived. It records the status it settled on ON THE EXCEPTION - 413 or
/// 400 - so the outcome is already decided by the time anything in this application sees it. What it is
/// NOT is an unhandled fault; the caller submitted something the host correctly refused.
/// </para>
/// <para>
/// Left to propagate, it reaches the framework's exception-handling stage, which writes
/// <c>"An unhandled exception has occurred while executing the request"</c> AT ERROR before delegating to
/// the registered handler - unconditionally, whatever status that handler goes on to choose. The response
/// was therefore correct while the log was not: a routine client mistake produced a server-fault entry, and
/// a caller repeatedly submitting oversized bodies could fill the error stream and defeat error-rate
/// alerting for the faults that matter. Silencing that stage's logger wholesale is not an option - it is
/// the entry that reports genuine 500s - so the exception is handled BEFORE it gets there instead.
/// </para>
/// <para>
/// THE PAYLOAD IS NOT WRITTEN HERE. The registered <see cref="IExceptionHandler"/> is asked to answer,
/// which is the same collaborator the framework stage would have delegated to, so the status, the
/// problem-type identifier, the authored detail, the correlation identifier and the log entry are produced
/// in exactly one place and this stage cannot come to disagree with it. Nothing about the response is
/// decided here; only where the decision is taken from.
/// </para>
/// <para>
/// POSITION. Inside the correlation and request-logging scopes, so the identifier on the payload is the one
/// on the response header and the completion entry reports the status the caller received - as a client
/// error rather than as an escaped failure, because from that stage's point of view nothing escaped. Only
/// this one exception type is caught: everything else propagates untouched to the framework stage, which is
/// where an unhandled fault belongs.
/// </para>
/// </remarks>
public sealed class TransportRefusalMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IExceptionHandler _handler;

    /// <summary>Initialises the middleware.</summary>
    /// <param name="next">The next request stage.</param>
    /// <param name="handler">
    /// The registered exception handler, which owns the problem-details payload and the log entry.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public TransportRefusalMiddleware(RequestDelegate next, IExceptionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(handler);

        _next = next;
        _handler = handler;
    }

    /// <summary>Answers a host-decided refusal, or lets anything else propagate.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes once the request has been answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException refusal)
        {
            // Once the status line and headers are flushed the status cannot be changed and a payload
            // appended to what was already sent would corrupt it. There is nothing this stage can do, so
            // the exception continues to the framework stage, which will record it - correctly, because a
            // refusal that cannot be delivered IS a fault.
            if (context.Response.HasStarted)
            {
                throw;
            }

            // The handler reports whether it answered. A refusal it declines is re-thrown rather than
            // swallowed, so a caller is never left with an empty response and no fall-back.
            if (!await _handler
                    .TryHandleAsync(context, refusal, context.RequestAborted)
                    .ConfigureAwait(false))
            {
                throw;
            }
        }
    }
}
