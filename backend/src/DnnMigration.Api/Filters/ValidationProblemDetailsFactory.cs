using System.Diagnostics;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DnnMigration.Api.Filters;

/// <summary>
/// Shapes every validation and model-state failure raised by this API into an RFC 7807 problem-details
/// payload. It is the only type in the solution that does so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Validation outcomes only. Unhandled exceptions belong to
/// <c>Api/ErrorHandling/GlobalExceptionHandler.cs</c>, which sits first in the request pipeline.
/// </para>
/// <para>
/// <b>Mechanism.</b> This type derives from the framework's own <see cref="ProblemDetailsFactory"/>
/// extension point instead of introducing a bespoke pipeline stage, so one override governs every producer
/// of a problem response: the automatic 400 emitted for an invalid model by a controller with API behaviour
/// enabled, and the explicit <c>ControllerBase.Problem</c> and <c>ControllerBase.ValidationProblem</c>
/// helpers.
/// </para>
/// </remarks>
public sealed class ValidationProblemDetailsFactory : ProblemDetailsFactory
{
    /// <summary>
    /// Status code applied when a caller of the general factory method supplies none. Mirrors the
    /// framework's own default factory.
    /// </summary>
    private const int DefaultProblemStatusCode = StatusCodes.Status500InternalServerError;

    /// <summary>
    /// Status code applied when a caller of either validation factory method supplies none. Mirrors the
    /// framework's own default factory.
    /// </summary>
    private const int DefaultValidationStatusCode = StatusCodes.Status400BadRequest;

    /// <summary>Name of the W3C trace-context extension member this type contributes.</summary>
    private const string TraceIdExtensionKey = "traceId";

    /// <summary>
    /// Name of the extension member carrying the identifier a caller quotes when reporting a problem.
    /// </summary>
    /// <remarks>
    /// Named separately from <see cref="TraceIdExtensionKey"/> on purpose, because the two answer different
    /// questions and are not interchangeable: this one is the correlation identifier the pipeline validated
    /// for the request, and it is the value that appears on the response header, on the request envelope in
    /// the log and on every audit event the request produced.
    /// </remarks>
    private const string CorrelationIdExtensionKey = "correlationId";

    /// <summary>Fallback problem-type identifier, title and detail for each status code this API emits.</summary>
    /// <remarks>
    /// Where a code already exists for a condition it is REUSED rather than re-spelled, so a status-only
    /// refusal and a coded refusal for the same cause are indistinguishable to a client - which is correct,
    /// because they are the same cause. <c>auth.unauthenticated</c>, <c>auth.not_permitted</c> and
    /// <c>resource.not_found</c> are the three that recur, and each is the code the authorisation result
    /// handler and the controller result helpers already publish.
    /// </remarks>
    private static readonly Dictionary<int, (string Type, string Title, string Detail)> StatusVocabulary =
        new()
        {
            [StatusCodes.Status400BadRequest] = (
                ApiResults.BuildProblemType("request.invalid"),
                "Bad Request",
                "The request could not be processed as submitted."),
            [StatusCodes.Status401Unauthorized] = (
                ApiResults.BuildProblemType("auth.unauthenticated"),
                "Unauthorized",
                "Authentication is required to reach this resource."),
            [StatusCodes.Status403Forbidden] = (
                ApiResults.BuildProblemType("auth.not_permitted"),
                "Forbidden",
                "The authenticated caller is not permitted to perform this operation."),
            [StatusCodes.Status404NotFound] = (
                ApiResults.BuildProblemType("resource.not_found"),
                "Not Found",
                "The requested resource does not exist."),
            [StatusCodes.Status405MethodNotAllowed] = (
                ApiResults.BuildProblemType("request.method_not_allowed"),
                "Method Not Allowed",
                "The requested method is not supported for this resource."),
            [StatusCodes.Status406NotAcceptable] = (
                ApiResults.BuildProblemType("request.not_acceptable"),
                "Not Acceptable",
                "No representation acceptable to the caller is available for this resource."),
            [StatusCodes.Status409Conflict] = (
                ApiResults.BuildProblemType("resource.conflict"),
                "Conflict",
                "The request conflicts with the current state of the resource."),
            [StatusCodes.Status413PayloadTooLarge] = (
                ApiResults.BuildProblemType("request.too_large"),
                "Payload Too Large",
                "The submitted request body is larger than this endpoint accepts."),
            [StatusCodes.Status415UnsupportedMediaType] = (
                ApiResults.BuildProblemType("request.unsupported_media_type"),
                "Unsupported Media Type",
                "The submitted media type is not supported by this endpoint."),
            [StatusCodes.Status422UnprocessableEntity] = (
                ApiResults.BuildProblemType("request.unprocessable"),
                "Unprocessable Content",
                "The request was understood but could not be processed."),
            [StatusCodes.Status429TooManyRequests] = (
                ApiResults.BuildProblemType("request.rate_limited"),
                "Too Many Requests",
                "Too many requests have been submitted. Retry after a short delay."),
            [StatusCodes.Status500InternalServerError] = (
                ApiResults.BuildProblemType("server.unexpected_failure"),
                "Internal Server Error",
                "An unexpected error occurred while processing the request."),
            [StatusCodes.Status501NotImplemented] = (
                ApiResults.BuildProblemType("server.not_implemented"),
                "Not Implemented",
                "The requested operation is not implemented."),
            [StatusCodes.Status503ServiceUnavailable] = (
                ApiResults.BuildProblemType("server.unavailable"),
                "Service Unavailable",
                "The service is temporarily unavailable. Retry after a short delay."),
        };

    /// <summary>Detail used for a status code that appears in neither mapping.</summary>
    private const string FallbackDetail = "The request could not be completed.";

    /// <summary>
    /// Title used when neither mapping covers the status code and the platform knows no reason phrase for
    /// it.
    /// </summary>
    private const string FallbackTitle = "Error";

    private readonly ApiBehaviorOptions _apiBehaviorOptions;

    /// <summary>
    /// Initialises the factory with the MVC API behaviour options that supply the problem-type link and
    /// title vocabulary shared with every other framework-produced problem response.
    /// </summary>
    /// <param name="apiBehaviorOptions">Options accessor for <see cref="ApiBehaviorOptions"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="apiBehaviorOptions"/> is <see langword="null"/>.
    /// </exception>
    public ValidationProblemDetailsFactory(IOptions<ApiBehaviorOptions> apiBehaviorOptions)
    {
        ArgumentNullException.ThrowIfNull(apiBehaviorOptions);

        _apiBehaviorOptions = apiBehaviorOptions.Value;
    }

    /// <summary>
    /// Builds a problem-details payload for a failure that is not expressed as a set of per-field
    /// validation messages.
    /// </summary>
    /// <param name="httpContext">Context of the request being answered.</param>
    /// <param name="statusCode">HTTP status code to report.</param>
    /// <param name="title">Short human-readable summary.</param>
    /// <param name="type">
    /// Problem-type URI. When omitted, the link registered for the resolved status code is used.
    /// </param>
    /// <param name="detail">Human-readable explanation of this specific occurrence.</param>
    /// <param name="instance">Identifier of this specific occurrence.</param>
    /// <returns>
    /// A populated <see cref="ProblemDetails"/> instance carrying the <c>traceId</c> extension when a trace
    /// identifier is available.
    /// </returns>
    public override ProblemDetails CreateProblemDetails(
        HttpContext httpContext,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        int resolvedStatusCode = statusCode ?? DefaultProblemStatusCode;

        var problemDetails = new ProblemDetails
        {
            Status = resolvedStatusCode,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = instance,
        };

        ApplyProblemDetailsDefaults(httpContext, problemDetails, resolvedStatusCode);

        return problemDetails;
    }

    /// <summary>
    /// Builds a problem-details payload from model-binding and model-validation state. This is the path
    /// taken by the automatic 400 response and by <c>ControllerBase.ValidationProblem</c>.
    /// </summary>
    /// <param name="httpContext">
    /// Context of the request being answered, supplied by the framework as an argument.
    /// </param>
    /// <param name="modelStateDictionary">Model state to project.</param>
    /// <param name="statusCode">HTTP status code to report.</param>
    /// <param name="title">Short human-readable summary.</param>
    /// <param name="type">
    /// Problem-type URI. When omitted, the link registered for the resolved status code is used.
    /// </param>
    /// <param name="detail">Human-readable explanation of this specific occurrence.</param>
    /// <param name="instance">Identifier of this specific occurrence.</param>
    /// <returns>
    /// A populated <see cref="ValidationProblemDetails"/> instance whose <c>errors</c> keys are the
    /// model-state keys and whose values are the unmodified message arrays.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="modelStateDictionary"/> is <see langword="null"/>.
    /// </exception>
    public override ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext httpContext,
        ModelStateDictionary modelStateDictionary,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(modelStateDictionary);

        int resolvedStatusCode = statusCode ?? DefaultValidationStatusCode;

        // ⚠ BINDER MESSAGES ARE REWRITTEN; APPLICATION MESSAGES ARE PASSED THROUGH BYTE FOR BYTE. See
        // SanitiseBinderFailures for what a binder message is and why its own text may not be published.
        var problemDetails = new ValidationProblemDetails(SanitiseBinderFailures(modelStateDictionary))
        {
            Status = resolvedStatusCode,
            Type = type,
            Detail = detail,
            Instance = instance,
        };

        ApplyValidationTitle(problemDetails, title);
        ApplyProblemDetailsDefaults(httpContext, problemDetails, resolvedStatusCode);

        return problemDetails;
    }

    /// <summary>
    /// Builds the same payload as the model-state overload from failures that have already been projected
    /// to field names and message arrays.
    /// </summary>
    /// <remarks>
    /// The application layer's declarative validators are invoked explicitly by the controller rather than
    /// through a model-binding bridge, so their failures never reach model state. This overload exists so
    /// that such a controller produces a byte-identical envelope.
    /// </remarks>
    /// <param name="httpContext">Context of the request being answered.</param>
    /// <param name="errors">Field names mapped to their message arrays.</param>
    /// <param name="statusCode">HTTP status code to report.</param>
    /// <param name="title">Short human-readable summary.</param>
    /// <param name="type">
    /// Problem-type URI. When omitted, the link registered for the resolved status code is used.
    /// </param>
    /// <param name="detail">Human-readable explanation of this specific occurrence.</param>
    /// <param name="instance">Identifier of this specific occurrence.</param>
    /// <returns>
    /// A populated <see cref="ValidationProblemDetails"/> instance identical in shape to the one produced
    /// from model state.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> is <see langword="null"/>.</exception>
    public ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext httpContext,
        IDictionary<string, string[]> errors,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(errors);

        int resolvedStatusCode = statusCode ?? DefaultValidationStatusCode;

        var problemDetails = new ValidationProblemDetails(errors)
        {
            Status = resolvedStatusCode,
            Type = type,
            Detail = detail,
            Instance = instance,
        };

        ApplyValidationTitle(problemDetails, title);
        ApplyProblemDetailsDefaults(httpContext, problemDetails, resolvedStatusCode);

        return problemDetails;
    }

    /// <summary>
    /// The key the deserialiser uses for a fault in the document as a whole.
    /// </summary>
    /// <remarks>
    /// Also the key an EMPTY body is republished under. The binder reports that one under the EMPTY STRING,
    /// which serialises as an `errors` member with no name at all - a shape a client cannot address, cannot
    /// map to a control, and cannot even read aloud. It is a fault in the document, so it is reported where
    /// every other document-level fault is reported.
    /// </remarks>
    private const string DocumentKey = "$";

    /// <summary>What is published in place of the deserialiser's own account of a malformed document.</summary>
    private const string MalformedDocumentMessage = "The request body is not valid JSON.";

    /// <summary>What is published when a member carried a value of the wrong type.</summary>
    private const string WrongTypeMessage =
        "The value supplied for this member is not of the type this member accepts.";

    /// <summary>What is published when the document carried a member the contract does not declare.</summary>
    private const string UnknownMemberMessage = "This member is not part of the request contract.";

    /// <summary>What is published when no body was sent at all.</summary>
    private const string BodyRequiredMessage = "A request body is required.";

    /// <summary>Marker identifying the deserialiser's wrong-type account, which names a CLR type.</summary>
    private const string WrongTypeMarker = "could not be converted to";

    /// <summary>Marker identifying the deserialiser's unknown-member account, which names a CLR type.</summary>
    private const string UnknownMemberMarker = "could not be mapped to any";

    /// <summary>Marker identifying the binder's parameter-level "field is required" entry.</summary>
    private const string ParameterRequiredMarker = "field is required";

    /// <summary>
    /// Rewrites the deserialiser's own failure text and drops the entries that name no client field, leaving
    /// every application-authored message untouched.
    /// </summary>
    /// <param name="modelState">The model state as the binder and the filters left it.</param>
    /// <returns>The `errors` map to publish.</returns>
    /// <remarks>
    /// <para>
    /// <b>What was leaking.</b> The deserialiser's messages are written for a developer holding the payload
    /// in a debugger, and this API was publishing them verbatim to any caller. A truncated body disclosed
    /// the parser's JSON path, line number and BYTE POSITION; a value of the wrong type disclosed the
    /// framework type name <c>System.Nullable`1[System.Decimal]</c>; and an unrecognised member disclosed
    /// the FULLY QUALIFIED name of the internal contract class it failed to bind to, namespace included.
    /// None of that helps a caller correct the request, and all of it describes the inside of this
    /// application to somebody outside it.
    /// </para>
    /// <para>
    /// <b>How a binder message is told apart from a real one - by KEY SHAPE, not by text.</b> The
    /// deserialiser keys its failures by JSON path, so they are <c>$</c> or begin <c>$.</c> or <c>$[</c>.
    /// Application validators key theirs by MEMBER name, which never begins with a dollar sign. The
    /// discriminator is therefore structural and survives a framework wording change or a localised build;
    /// the message text is consulted afterwards only to choose which of three sentences is the most useful,
    /// and an unrecognised binder message still falls back to the malformed-document sentence rather than
    /// being published.
    /// </para>
    /// <para>
    /// <b>The two entries that name no client field are dropped.</b> When a body fails to bind at all the
    /// binder ALSO records a parameter-level entry - keyed by the action's parameter name, in practice
    /// <c>request</c> - saying that field is required. There is no such field on any client form; it is the
    /// name of a C# argument. It appeared on every malformed-body response beside the real complaint and
    /// invited a client to look for a control it does not have. It is dropped only when a document-level
    /// fault is present, so an application message that legitimately concerned a member called
    /// <c>request</c> would survive.
    /// </para>
    /// </remarks>
    private static IDictionary<string, string[]> SanitiseBinderFailures(ModelStateDictionary modelState)
    {
        var sanitised = new Dictionary<string, string[]>(StringComparer.Ordinal);
        bool sawDocumentFault = false;

        foreach (KeyValuePair<string, ModelStateEntry> entry in modelState)
        {
            string[] messages = entry.Value.Errors
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToArray();

            if (messages.Length == 0)
            {
                continue;
            }

            // An empty key is the binder's account of an empty body. Republished under the document key.
            if (entry.Key.Length == 0)
            {
                sawDocumentFault = true;
                sanitised[DocumentKey] = [BodyRequiredMessage];
                continue;
            }

            if (!IsBinderKey(entry.Key))
            {
                // An application message. Untouched, byte for byte.
                sanitised[entry.Key] = messages;
                continue;
            }

            sawDocumentFault = true;
            sanitised[entry.Key] = [.. messages.Select(DescribeBinderFailure).Distinct(StringComparer.Ordinal)];
        }

        if (sawDocumentFault)
        {
            RemoveParameterLevelRequiredEntries(modelState, sanitised);
        }

        return sanitised;
    }

    /// <summary>Whether a model-state key was written by the JSON deserialiser.</summary>
    /// <param name="key">The model-state key.</param>
    /// <returns><see langword="true"/> when the key is a JSON path.</returns>
    private static bool IsBinderKey(string key) =>
        key.Length > 0 && key[0] == '$';

    /// <summary>Chooses the sentence to publish in place of one deserialiser message.</summary>
    /// <param name="message">The deserialiser's own message, which is never published.</param>
    /// <returns>The sentence to publish.</returns>
    private static string DescribeBinderFailure(string message)
    {
        if (message.Contains(WrongTypeMarker, StringComparison.OrdinalIgnoreCase))
        {
            return WrongTypeMessage;
        }

        if (message.Contains(UnknownMemberMarker, StringComparison.OrdinalIgnoreCase))
        {
            return UnknownMemberMessage;
        }

        return MalformedDocumentMessage;
    }

    /// <summary>
    /// Removes the binder's parameter-level "field is required" entries, which name a C# argument rather
    /// than anything a caller sent.
    /// </summary>
    /// <param name="modelState">The model state, consulted for the original message text.</param>
    /// <param name="sanitised">The map being built, from which the entries are removed.</param>
    private static void RemoveParameterLevelRequiredEntries(
        ModelStateDictionary modelState,
        Dictionary<string, string[]> sanitised)
    {
        foreach (KeyValuePair<string, ModelStateEntry> entry in modelState)
        {
            if (IsBinderKey(entry.Key) || entry.Key.Length == 0)
            {
                continue;
            }

            bool onlyTheRequiredNotice = entry.Value.Errors.Count > 0
                && entry.Value.Errors.All(error =>
                    error.ErrorMessage.Contains(ParameterRequiredMarker, StringComparison.OrdinalIgnoreCase));

            if (onlyTheRequiredNotice)
            {
                sanitised.Remove(entry.Key);
            }
        }
    }

    /// <summary>
    /// Applies a caller-supplied title to a validation payload, leaving the framework's default validation
    /// title in place when none was supplied.
    /// </summary>
    /// <param name="problemDetails">Payload being populated.</param>
    /// <param name="title">Caller-supplied title, or <see langword="null"/> to keep the default.</param>
    private static void ApplyValidationTitle(ValidationProblemDetails problemDetails, string? title)
    {
        // A null title must NOT be assigned: ValidationProblemDetails arrives already carrying "One or more
        // validation errors occurred.", the string the client is written against, and overwriting it with a
        // status-code title would change a contract the client mirrors.
        if (title is not null)
        {
            problemDetails.Title = title;
        }
    }

    /// <summary>
    /// Fills in the members the caller left unspecified, so that both factory paths cannot drift apart, and
    /// attaches the trace identifier.
    /// </summary>
    /// <param name="httpContext">
    /// Context of the request being answered, or <see langword="null"/> when the framework supplied none.
    /// </param>
    /// <param name="problemDetails">Payload being populated.</param>
    /// <param name="statusCode">Status code already resolved by the caller.</param>
    private void ApplyProblemDetailsDefaults(
        HttpContext httpContext,
        ProblemDetails problemDetails,
        int statusCode)
    {
        problemDetails.Status ??= statusCode;

        // THIS API'S OWN VOCABULARY IS CONSULTED FIRST, and the order is load-bearing. Assignment is by
        // null-coalescence throughout, so whichever source is consulted first and has a value decides which
        // means the framework registration read below can only ever fill a gap this table left.
        if (StatusVocabulary.TryGetValue(statusCode, out var vocabulary))
        {
            problemDetails.Title ??= vocabulary.Title;
            problemDetails.Type ??= vocabulary.Type;
            problemDetails.Detail ??= vocabulary.Detail;
        }

        // The framework's registration for this status code, as the remaining fallback.
        ClientErrorData? clientErrorData = _apiBehaviorOptions.ClientErrorMapping
            .FirstOrDefault(registration => registration.Key == statusCode)
            .Value;

        if (clientErrorData is not null)
        {
            problemDetails.Title ??= clientErrorData.Title;
        }

        // Last resort for a status code in neither mapping.
        if (problemDetails.Title is null)
        {
            string reasonPhrase = ReasonPhrases.GetReasonPhrase(statusCode);
            problemDetails.Title = string.IsNullOrEmpty(reasonPhrase) ? FallbackTitle : reasonPhrase;
        }

        // Detail is a required member of the documented envelope, so it is filled unconditionally last. No
        // detail assigned anywhere in this file mentions an exception, a message, a path, a type name or
        // any other internal fact: every one is a fixed string chosen for its status code.
        problemDetails.Detail ??= FallbackDetail;

        string? traceId = Activity.Current?.Id ?? httpContext?.TraceIdentifier;

        if (traceId is not null)
        {
            problemDetails.Extensions[TraceIdExtensionKey] = traceId;
        }

        string? correlationId = ResolveCorrelationId(httpContext);

        if (correlationId is not null)
        {
            problemDetails.Extensions[CorrelationIdExtensionKey] = correlationId;
        }
    }

    /// <summary>Reads the correlation identifier published for the request being answered.</summary>
    /// <param name="httpContext">The request being answered, which may be absent.</param>
    /// <returns>
    /// The identifier the pipeline validated for this request, or <see langword="null"/> when there is no
    /// request to read one from.
    /// </returns>
    /// <remarks>
    /// The item published by <see cref="CorrelationIdMiddleware"/> is preferred, because that is the value
    /// that stage VALIDATED, whereas an inbound header holds whatever the caller sent.
    /// </remarks>
    private static string? ResolveCorrelationId(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        // ContainsKey before the indexer: the item dictionary is keyed by object, and probing first is
        // correct against any implementation of it rather than only the framework's own.
        if (httpContext.Items.ContainsKey(CorrelationIdMiddleware.ItemKey)
            && httpContext.Items[CorrelationIdMiddleware.ItemKey] is string published
            && published.Length > 0)
        {
            return published;
        }

        if (httpContext.Response.Headers.TryGetValue(CorrelationIdMiddleware.HeaderName, out StringValues assigned)
            && assigned.Count == 1
            && assigned[0] is { Length: > 0 } header)
        {
            return header;
        }

        return httpContext.TraceIdentifier;
    }
}
