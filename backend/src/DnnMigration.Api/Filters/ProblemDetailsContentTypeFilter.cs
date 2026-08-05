using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DnnMigration.Api.Filters;

/// <summary>
/// Labels every controller-produced problem document with the media type RFC 7807 registers for it.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS NEEDED. <c>ControllerBase.Problem(...)</c> returns an <see cref="ObjectResult"/> carrying a
/// <see cref="ProblemDetails"/> value and NO declared content types, which leaves the media type to
/// content negotiation. The JSON output formatter is registered for both <c>application/json</c> and
/// <c>application/problem+json</c>, and a caller sending no <c>Accept</c> header - or the <c>*/*</c> that
/// most clients send - matches the first of those. Every 400, 404 and 409 a controller produced was
/// therefore labelled <c>application/json</c>, while the refusals decided outside MVC - the
/// authentication challenge, the authorisation refusal, the rate-limit refusal - were labelled
/// <c>application/problem+json</c> by the middleware that wrote them.
/// </para>
/// <para>
/// A client consequently could not select a problem parser from the media type, which is the reason RFC
/// 7807 section 3 registers one, and the same document type arrived under two labels depending on which
/// part of the stack answered. Declaring the content type on the result settles the negotiation before it
/// happens, so the formatter that runs is the same one and only the label changes.
/// </para>
/// <para>
/// WHY A FILTER RATHER THAN A FORMATTER OR A CONVENTION. The formatter is shared with successful results
/// and must keep offering <c>application/json</c> for those. A convention could annotate declared response
/// types for the documentation, but the documented type and the type actually written are separate things,
/// and it is the written one that was wrong. Deciding at result-execution time is the only point where the
/// value carried is known to be a problem document.
/// </para>
/// <para>
/// It runs as an ALWAYS-RUN result filter so that a result produced by a short-circuiting authorisation or
/// action filter is labelled too, not only one returned from an action body. It is deliberately narrow: it
/// touches nothing but an <see cref="ObjectResult"/> whose value is a <see cref="ProblemDetails"/>, so
/// every successful result keeps the media type its controller declares.
/// </para>
/// <para>
/// ORDERING IS LOAD-BEARING, AND WAS THE WHOLE DIFFICULTY. Every controller in this API carries
/// <c>[Produces("application/json")]</c>, which is itself a result filter: it CLEARS
/// <see cref="ObjectResult.ContentTypes"/> and assigns its own value. Filters run in order and, at equal
/// order, global before controller - so this filter registered at the default order had its addition wiped
/// by that attribute on every single response, which is precisely why every controller-produced problem
/// document was served as <c>application/json</c> while the middleware-produced ones were correct. It is
/// therefore registered with an order ABOVE the default so it runs last and has the final word, and the
/// collection is ASSIGNED rather than added to for the same reason: the value found there is the
/// attribute's, not a caller's.
/// </para>
/// <para>
/// The controller attribute is deliberately left in place. It states what the SUCCESSFUL responses are,
/// which is true and is what the generated document should say; a problem document is the exception to it
/// and is the one thing this filter changes.
/// </para>
/// </remarks>
public sealed class ProblemDetailsContentTypeFilter : IAlwaysRunResultFilter
{
    /// <summary>
    /// The media type RFC 7807 section 3 registers for a problem document.
    /// </summary>
    /// <remarks>
    /// Spelled without a charset parameter. The formatter appends <c>charset=utf-8</c> itself, and
    /// supplying one here would produce it twice.
    /// </remarks>
    private const string ProblemContentType = "application/problem+json";

    /// <summary>Labels a problem document before the result is executed.</summary>
    /// <param name="context">The result being executed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The value test covers ValidationProblemDetails as well, which derives from ProblemDetails - so
        // the automatic 400 carrying the per-field errors object is labelled by the same rule rather than
        // by a second one that could come to disagree with it.
        if (context.Result is not ObjectResult { Value: ProblemDetails } result)
        {
            return;
        }

        // Already correct and nothing else offered: leave it untouched, so a result that states this
        // media type for itself is not rewritten to an identical value.
        if (result.ContentTypes.Count == 1
            && string.Equals(result.ContentTypes[0], ProblemContentType, StringComparison.Ordinal))
        {
            return;
        }

        // Assigned, not appended. Whatever is here was put there by the controller's produces attribute,
        // which speaks for the successful responses rather than for this one; leaving it alongside would
        // let content negotiation pick it, which is exactly what happened before.
        result.ContentTypes.Clear();
        result.ContentTypes.Add(ProblemContentType);
    }

    /// <summary>Takes no action after the result has executed.</summary>
    /// <param name="context">The result that executed.</param>
    /// <remarks>
    /// The media type has to be decided BEFORE the formatter is selected, so there is nothing left to do
    /// once the result has run. The member is required by the interface and is empty deliberately rather
    /// than by omission.
    /// </remarks>
    public void OnResultExecuted(ResultExecutedContext context)
    {
        // Intentionally empty; see the remarks on this member.
    }
}
