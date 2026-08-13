using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DnnMigration.Api.Filters;

/// <summary>
/// Labels every controller-produced problem document with the media type RFC 7807 registers for it.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS NEEDED. <c>ControllerBase.Problem(...)</c> returns an <see cref="ObjectResult"/> carrying a
/// <see cref="ProblemDetails"/> value and NO declared content types, which leaves the media type to content
/// negotiation.
/// </para>
/// <para>
/// A client consequently could not select a problem parser from the media type, which is the reason RFC
/// 7807 section 3 registers one, and the same document type arrived under two labels depending on which
/// part of the stack answered. Declaring the content type on the result settles the negotiation before it
/// happens, so the formatter that runs is the same one and only the label changes.
/// </para>
/// </remarks>
public sealed class ProblemDetailsContentTypeFilter : IAlwaysRunResultFilter
{
    /// <summary>The media type RFC 7807 section 3 registers for a problem document.</summary>
    private const string ProblemContentType = "application/problem+json";

    /// <summary>Labels a problem document before the result is executed.</summary>
    /// <param name="context">The result being executed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

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
