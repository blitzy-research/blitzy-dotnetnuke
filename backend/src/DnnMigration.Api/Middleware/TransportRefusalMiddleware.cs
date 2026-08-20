using Microsoft.AspNetCore.Diagnostics;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Answers a refusal the HOST decided - an oversized or malformed request body - without letting it be
/// recorded as an unhandled fault.
/// </summary>
/// <remarks>
/// Left to propagate, it reaches the framework's exception-handling stage, which writes <c>"An unhandled
/// exception has occurred while executing the request"</c> AT ERROR before delegating to the registered
/// handler - unconditionally, whatever status that handler goes on to choose.
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
