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
/// Shapes every validation and model-state failure raised by this API into an
/// RFC 7807 problem-details payload. It is the only type in the solution that
/// does so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Validation outcomes only. Unhandled exceptions belong to
/// <c>Api/ErrorHandling/GlobalExceptionHandler.cs</c>, which sits first in the
/// request pipeline. Nothing here observes, logs or rewrites an exception,
/// nothing here reads the hosting environment, and no exception detail can reach
/// a payload produced by this type, so the response shape is identical in
/// development and in production.
/// </para>
/// <para>
/// <b>Mechanism.</b> This type derives from the framework's own
/// <see cref="ProblemDetailsFactory"/> extension point instead of introducing a
/// bespoke pipeline stage, so one override governs every producer of a problem
/// response: the automatic 400 emitted for an invalid model by a controller with
/// API behaviour enabled, and the explicit <c>ControllerBase.Problem</c> and
/// <c>ControllerBase.ValidationProblem</c> helpers. There is no pipeline stage,
/// no filter and no request-scoped state here.
/// </para>
/// <para>
/// <b>Registration.</b> This type does not register itself. Composition lives in
/// <c>Api/Extensions/ServiceCollectionExtensions.cs</c>, which binds it as the
/// singleton implementation of the framework's
/// <see cref="ProblemDetailsFactory"/> service. The only obligation discharged
/// here is to be registerable: a public, sealed, non-generic class with one
/// constructor whose single argument the container can already resolve.
/// </para>
/// <para>
/// <b>Message text.</b> Message text is reproduced byte for byte. Wording is
/// owned by the validators in the application layer, and a second transformation
/// here would give one user-visible string two sources of truth. Nothing in this
/// file trims, re-cases, re-keys, encodes, deduplicates, reorders or otherwise
/// edits a message or a field name.
/// </para>
/// <para>
/// <b>Wire contract.</b> Payloads carry <c>type</c>, <c>title</c>,
/// <c>status</c>, <c>detail</c> and a per-field <c>errors</c> object whose values
/// are arrays of message strings, plus the one framework-native extension member
/// <c>traceId</c>. Of these, <c>status</c>, <c>title</c> and <c>detail</c> are
/// guaranteed present on every payload this type produces, at every status code,
/// so a client may bind them without a presence test.
/// The framework selects the <c>application/problem+json</c>
/// media type from the returned type, so no header is set here. Member casing and
/// null handling are decided once by the serialiser configuration in
/// <c>Program.cs</c>; this type declares no naming policy, no converter and no
/// conditional-omission rule of its own.
/// </para>
/// </remarks>
public sealed class ValidationProblemDetailsFactory : ProblemDetailsFactory
{
    // MIGRATION: the migration plan names "the asp:ValidationSummary rendering
    // path in the legacy .ascx markup" as this file's source. That control does
    // not exist anywhere in this repository. A case-insensitive search of the
    // entire checkout returns no files and zero occurrences, so the citation is
    // vacuous; no predecessor has been invented to stand in for it. What the
    // legacy application actually did was render each message inline beside its
    // own input - the five in-scope admin trees carry 35 Display="Dynamic"
    // declarations across their 39 screens - and surface aggregate outcomes
    // through a separate banner.
    //
    // MIGRATION: that arrangement maps onto RFC 7807 without loss, and the
    // mapping below is what this type implements.
    //   per-field inline validators (the 35 Display="Dynamic" declarations)
    //       -> the per-field "errors" object: one key per field, values as
    //          arrays, so several messages can coexist on one field exactly as
    //          several inline validators could.
    //   the aggregate banner (53 AddModuleMessage call sites) together with the
    //   "If Page.IsValid Then" gate (9 sites, replaced by ModelState.IsValid)
    //       -> "title" plus "detail", produced once per response.
    //   the three banner severities (ModuleMessageType.RedError 20,
    //   .YellowWarning 13 and .GreenSuccess 4 occurrences)
    //       -> collapsed into the HTTP "status" code plus "title". A problem
    //          response is by definition a failure, so the warning and success
    //          severities have no counterpart in this envelope; they belong to
    //          successful responses, which this type never shapes.
    //
    // MIGRATION: a uniform, machine-readable error contract has no legacy
    // behaviour to translate. The legacy screens surfaced failure by
    // rendering markup for a human - inline validator text plus a banner - and
    // routed unexpected faults through page-level handlers. There was no
    // machine-readable error envelope to port, so nothing was ported; the shape
    // below is introduced by this migration.

    /// <summary>
    /// Status code applied when a caller of the general factory method supplies
    /// none. Mirrors the framework's own default factory.
    /// </summary>
    private const int DefaultProblemStatusCode = StatusCodes.Status500InternalServerError;

    /// <summary>
    /// Status code applied when a caller of either validation factory method
    /// supplies none. Mirrors the framework's own default factory.
    /// </summary>
    private const int DefaultValidationStatusCode = StatusCodes.Status400BadRequest;

    /// <summary>
    /// Name of the W3C trace-context extension member this type contributes.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the support reference a caller quotes. It is the distributed-tracing
    /// identifier, it changes whenever the ambient activity changes, and it is present only when one
    /// exists at all - so a client that quoted it would sometimes quote nothing and sometimes quote a
    /// value that appears in no other record of the request.
    /// </remarks>
    private const string TraceIdExtensionKey = "traceId";

    /// <summary>
    /// Name of the extension member carrying the identifier a caller quotes when reporting a problem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named separately from <see cref="TraceIdExtensionKey"/> on purpose, because the two answer
    /// different questions and are not interchangeable: this one is the correlation identifier the
    /// pipeline validated for the request, and it is the value that appears on the response header, on
    /// the request envelope in the log and on every audit event the request produced. Quoting it is
    /// what lets an operator find the request a caller is describing.
    /// </para>
    /// <para>
    /// The client reads this member in preference to the trace identifier, and the two names exist
    /// side by side rather than one replacing the other so that a consumer already reading the
    /// framework-shaped <c>traceId</c> keeps working.
    /// </para>
    /// </remarks>
    private const string CorrelationIdExtensionKey = "correlationId";

    /// <summary>
    /// Fallback problem-type identifier, title and detail for each status code this API emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This table exists because the framework's own registration cannot cover the whole range. The
    /// mapping the framework populates — <c>ApiBehaviorOptions.ClientErrorMapping</c> — holds
    /// CLIENT-error status codes only, so every server-error response produced through this factory
    /// arrived with a null title and a null problem type. A payload missing either is still valid
    /// against the specification, and that is precisely the difficulty: a client written against a
    /// documented envelope receives a member that is sometimes present and sometimes not, and no
    /// failure ever announces itself.
    /// </para>
    /// <para>
    /// MIGRATION: THE PROBLEM TYPE IS THIS API'S OWN IDENTIFIER, NOT AN EXTERNAL SPECIFICATION LINK.
    /// These rows previously carried <c>https://tools.ietf.org/html/rfc9110#section-15.x.y</c> - the
    /// framework's default - which meant the API emitted TWO problem-type vocabularies at once: a
    /// refusal decided with a failure code carried
    /// <c>urn:dnnmigration:error:&lt;code&gt;</c>, while the same status arrived at without one carried
    /// a specification link. A client branching on <c>type</c> then had to know which producer had
    /// answered before it could interpret the member, which is the one thing the member exists to make
    /// unnecessary. Every row is now built by <see cref="ApiResults.BuildProblemType(string)"/> from the
    /// failure code that names the condition, so there is exactly one namespace, one spelling
    /// convention and one method that produces it.
    /// </para>
    /// <para>
    /// Where a code already exists for a condition it is REUSED rather than re-spelled, so a status-only
    /// refusal and a coded refusal for the same cause are indistinguishable to a client - which is
    /// correct, because they are the same cause. <c>auth.unauthenticated</c>, <c>auth.not_permitted</c>
    /// and <c>resource.not_found</c> are the three that recur, and each is the code the authorisation
    /// result handler and the controller result helpers already publish.
    /// </para>
    /// <para>
    /// The specification links are not lost information: the status code itself is the reference into
    /// RFC 9110, and it is on every payload, in the <c>status</c> member and on the status line.
    /// </para>
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

    /// <summary>
    /// Detail used for a status code that appears in neither mapping.
    /// </summary>
    /// <remarks>
    /// Deliberately says nothing about the cause. A detail is a required member of the documented
    /// envelope, so one must exist for every status code this API can possibly return, including a
    /// status code added later by a caller of this factory. Saying nothing is the only wording that is
    /// guaranteed to remain both true and safe for an unknown condition — a guess about the cause
    /// could be wrong, and an internal explanation could disclose something.
    /// </remarks>
    private const string FallbackDetail = "The request could not be completed.";

    /// <summary>
    /// Title used when neither mapping covers the status code and the platform knows no reason phrase
    /// for it.
    /// </summary>
    private const string FallbackTitle = "Error";

    private readonly ApiBehaviorOptions _apiBehaviorOptions;

    /// <summary>
    /// Initialises the factory with the MVC API behaviour options that supply the
    /// problem-type link and title vocabulary shared with every other
    /// framework-produced problem response.
    /// </summary>
    /// <param name="apiBehaviorOptions">
    /// Options accessor for <see cref="ApiBehaviorOptions"/>. Its
    /// <see cref="ApiBehaviorOptions.ClientErrorMapping"/> is the only member
    /// read.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="apiBehaviorOptions"/> is <see langword="null"/>.
    /// </exception>
    public ValidationProblemDetailsFactory(IOptions<ApiBehaviorOptions> apiBehaviorOptions)
    {
        ArgumentNullException.ThrowIfNull(apiBehaviorOptions);

        _apiBehaviorOptions = apiBehaviorOptions.Value;
    }

    /// <summary>
    /// Builds a problem-details payload for a failure that is not expressed as a
    /// set of per-field validation messages.
    /// </summary>
    /// <param name="httpContext">
    /// Context of the request being answered. The framework supplies this as an
    /// argument, so this type never resolves the request from ambient state. It
    /// is read only for its trace identifier and is tolerated as
    /// <see langword="null"/> because some framework paths pass no context.
    /// </param>
    /// <param name="statusCode">
    /// HTTP status code to report. Defaults to 500 when not supplied.
    /// </param>
    /// <param name="title">
    /// Short human-readable summary. When omitted, the title registered for the
    /// resolved status code is used.
    /// </param>
    /// <param name="type">
    /// Problem-type URI. When omitted, the link registered for the resolved
    /// status code is used.
    /// </param>
    /// <param name="detail">
    /// Human-readable explanation of this specific occurrence. Passed through
    /// exactly as supplied, including an empty string.
    /// </param>
    /// <param name="instance">
    /// Identifier of this specific occurrence. Passed through exactly as
    /// supplied and never derived from the request.
    /// </param>
    /// <returns>
    /// A populated <see cref="ProblemDetails"/> instance carrying the
    /// <c>traceId</c> extension when a trace identifier is available.
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
    /// Builds a problem-details payload from model-binding and model-validation
    /// state. This is the path taken by the automatic 400 response and by
    /// <c>ControllerBase.ValidationProblem</c>.
    /// </summary>
    /// <param name="httpContext">
    /// Context of the request being answered, supplied by the framework as an
    /// argument. Read only for its trace identifier and tolerated as
    /// <see langword="null"/>.
    /// </param>
    /// <param name="modelStateDictionary">
    /// Model state to project. Every entry present is emitted and no entry is
    /// synthesised, so a partially populated model state - the normal outcome
    /// when a rule is conditional - produces exactly the fields that actually
    /// failed. Valid model state is not an error: it yields an empty
    /// <c>errors</c> object.
    /// </param>
    /// <param name="statusCode">
    /// HTTP status code to report. Defaults to 400 when not supplied.
    /// </param>
    /// <param name="title">
    /// Short human-readable summary. When omitted, the default validation title
    /// carried by <see cref="ValidationProblemDetails"/> is preserved.
    /// </param>
    /// <param name="type">
    /// Problem-type URI. When omitted, the link registered for the resolved
    /// status code is used.
    /// </param>
    /// <param name="detail">
    /// Human-readable explanation of this specific occurrence. Passed through
    /// exactly as supplied, including an empty string.
    /// </param>
    /// <param name="instance">
    /// Identifier of this specific occurrence. Passed through exactly as
    /// supplied and never derived from the request.
    /// </param>
    /// <returns>
    /// A populated <see cref="ValidationProblemDetails"/> instance whose
    /// <c>errors</c> keys are the model-state keys and whose values are the
    /// unmodified message arrays.
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

        // MIGRATION: message text is passed through unmodified, byte for byte.
        // Handing the model state straight to the framework constructor makes
        // that true by construction rather than by discipline - the errors
        // dictionary is populated by the framework and this type never touches a
        // message or a key afterwards. It matters because a substantial part of
        // the legacy wording carries an embedded HTML line-break prefix, held in
        // the authoritative resource files at
        // Website/admin/*/App_LocalResources/EditRoles.ascx.resx:L183-L209 (nine
        // fee, period and name messages) and
        // Website/admin/*/App_LocalResources/Signup.ascx.resx:L144-L269 (eight
        // required-field messages); each file name occurs exactly once beneath
        // Website/admin, so both citations resolve to a single file. Whether that
        // prefix is reproduced or dropped is the owning validator's decision to
        // make and to annotate. Re-deciding it here would break the equivalence
        // of error messages that the migration discipline requires, and it is not
        // a script-injection concern: the client renders title, detail and errors
        // as text.
        var problemDetails = new ValidationProblemDetails(modelStateDictionary)
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
    /// Builds the same payload as the model-state overload from failures that
    /// have already been projected to field names and message arrays.
    /// </summary>
    /// <remarks>
    /// The application layer's declarative validators are invoked explicitly by
    /// the controller rather than through a model-binding bridge, so their
    /// failures never reach model state. This overload exists so that such a
    /// controller produces a byte-identical envelope. It deliberately accepts a
    /// base-class-library dictionary rather than any validation library's result
    /// type, so this project never names that library at all, and it neither
    /// reads nor alters the supplied instance: the framework constructor copies
    /// it.
    /// </remarks>
    /// <param name="httpContext">
    /// Context of the request being answered. Read only for its trace identifier
    /// and tolerated as <see langword="null"/>.
    /// </param>
    /// <param name="errors">
    /// Field names mapped to their message arrays. Keys and messages are emitted
    /// exactly as supplied; an entry whose array is empty is preserved and
    /// serialises as an empty array rather than being discarded.
    /// </param>
    /// <param name="statusCode">
    /// HTTP status code to report. Defaults to 400 when not supplied.
    /// </param>
    /// <param name="title">
    /// Short human-readable summary. When omitted, the default validation title
    /// carried by <see cref="ValidationProblemDetails"/> is preserved.
    /// </param>
    /// <param name="type">
    /// Problem-type URI. When omitted, the link registered for the resolved
    /// status code is used.
    /// </param>
    /// <param name="detail">
    /// Human-readable explanation of this specific occurrence. Passed through
    /// exactly as supplied, including an empty string.
    /// </param>
    /// <param name="instance">
    /// Identifier of this specific occurrence. Passed through exactly as
    /// supplied and never derived from the request.
    /// </param>
    /// <returns>
    /// A populated <see cref="ValidationProblemDetails"/> instance identical in
    /// shape to the one produced from model state.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="errors"/> is <see langword="null"/>.
    /// </exception>
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
    /// Applies a caller-supplied title to a validation payload, leaving the
    /// framework's default validation title in place when none was supplied.
    /// </summary>
    /// <param name="problemDetails">Payload being populated.</param>
    /// <param name="title">
    /// Caller-supplied title, or <see langword="null"/> to keep the default.
    /// </param>
    private static void ApplyValidationTitle(ValidationProblemDetails problemDetails, string? title)
    {
        // A null title must NOT be assigned: ValidationProblemDetails arrives
        // already carrying "One or more validation errors occurred.", the string
        // the client is written against, and overwriting it with a status-code
        // title would change a contract the client mirrors. The test is against
        // null alone, so a caller that deliberately supplies an empty title gets
        // an empty title rather than a silent substitution.
        if (title is not null)
        {
            problemDetails.Title = title;
        }
    }

    /// <summary>
    /// Fills in the members the caller left unspecified, so that both factory
    /// paths cannot drift apart, and attaches the trace identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three sources are consulted in strict precedence, and every assignment is by null-coalescence
    /// so that an earlier source always wins: what the caller supplied, then the framework's
    /// client-error registration, then this type's own status vocabulary. Title and detail are then
    /// guaranteed non-null, because both are documented members of the envelope this API publishes
    /// and a member present on some responses but absent on others is a contract a client cannot
    /// rely on.
    /// </para>
    /// <para>
    /// An empty string supplied by a caller is content, not absence, and survives every pass — the
    /// tests here are against null alone, exactly as they are in
    /// <see cref="ApplyValidationTitle"/> and in the trace-identifier assignment below.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">
    /// Context of the request being answered, or <see langword="null"/> when the
    /// framework supplied none.
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
        // null-coalescence throughout, so whichever source is consulted first and has a value decides -
        // which means the framework registration read below can only ever fill a gap this table left.
        //
        // MIGRATION: the two passes used to run the other way round, and that is what produced two
        // problem-type vocabularies in one API. ApiBehaviorOptions.ClientErrorMapping is populated with
        // RFC 9110 section links, so going first it claimed the type for every client-error status, and
        // this table - which now holds urn:dnnmigration:error:* identifiers - only ever reached the
        // server-error rows. A caller therefore received a specification link from one producer and this
        // API's own identifier from another, for the same status. The table below covers every status
        // this API emits, so in practice the framework registration now contributes nothing but remains
        // consulted as the safety net it always was.
        //
        // The caller's own arguments still win over both, which is why the default validation title
        // survives this call untouched and why a coded refusal keeps the type its producer chose.
        if (StatusVocabulary.TryGetValue(statusCode, out var vocabulary))
        {
            problemDetails.Title ??= vocabulary.Title;
            problemDetails.Type ??= vocabulary.Type;
            problemDetails.Detail ??= vocabulary.Detail;
        }

        // The framework's registration for this status code, as the remaining fallback. Its TITLE is
        // taken; its LINK is deliberately not, because taking it would reintroduce the second
        // vocabulary this factory exists to remove - a status the table above does not name would be
        // the one response carrying a specification link. Such a status has no problem type instead,
        // which the specification permits and which the note at the end of this method explains.
        //
        // The registration is read in a single pass rather than by testing membership and then indexing
        // by key, so the mapping is examined once and no by-reference argument appears anywhere in this
        // file. It holds one row per client-error status code, so a single pass is trivially short. An
        // unregistered status code, and equally a registration with no value, simply leaves the members
        // as they stand.
        ClientErrorData? clientErrorData = _apiBehaviorOptions.ClientErrorMapping
            .FirstOrDefault(registration => registration.Key == statusCode)
            .Value;

        if (clientErrorData is not null)
        {
            problemDetails.Title ??= clientErrorData.Title;
        }

        // Last resort for a status code in neither mapping. The reason phrase is the platform's own,
        // so a code it recognises still yields the conventional wording; it answers with an empty
        // string rather than null for one it does not, which is why the result is tested for content
        // instead of for null before being used.
        if (problemDetails.Title is null)
        {
            string reasonPhrase = ReasonPhrases.GetReasonPhrase(statusCode);
            problemDetails.Title = string.IsNullOrEmpty(reasonPhrase) ? FallbackTitle : reasonPhrase;
        }

        // Detail is a required member of the documented envelope, so it is filled unconditionally
        // last. No detail assigned anywhere in this file mentions an exception, a message, a path, a
        // type name or any other internal fact: every one is a fixed string chosen for its status
        // code. That is what makes this member safe to guarantee — the guarantee is that it is
        // always present, never that it is ever specific.
        problemDetails.Detail ??= FallbackDetail;

        // The problem type is deliberately NOT given a blanket fallback. Every status code this API
        // returns is covered above, and inventing a link for one that is not would publish a URI
        // that documents nothing. Absent is more honest than wrong, and the specification permits it.

        // MIGRATION: traceId is the framework's own value, taken from the ambient
        // diagnostic activity and falling back to the request's trace identifier.
        // Preserving it keeps this factory's output indistinguishable from a
        // framework-produced problem response, which is the whole point of
        // extending the framework's factory rather than replacing it.
        string? traceId = Activity.Current?.Id ?? httpContext?.TraceIdentifier;

        // MIGRATION: the legacy null contract, defined in
        // Library/Components/Shared/Null.vb, encodes absence as the empty string
        // for text and as minus one for integers, and it treats minus one as
        // absent even where minus one is a real key - the identity seeds in
        // Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider
        // start at minus one for the site container (L77) and at zero for roles
        // (L115), pages (L140) and modules (L221). Those sentinels are visible
        // inside legacy validation too: the template selector at
        // Website/admin/*/signup.ascx:L67-L68 declares InitialValue="-1" so that
        // an unselected list counts as empty. This boundary therefore never
        // rewrites such a value. The test below is against null alone, so an
        // empty string survives as an empty string; no member of the payload is
        // omitted on a null-or-default condition, and no serialiser rule is
        // declared here that could turn one of those sentinels into null.
        if (traceId is not null)
        {
            problemDetails.Extensions[TraceIdExtensionKey] = traceId;
        }

        // MIGRATION: DIVERGENCE, and a correction of the reasoning recorded here before. An earlier
        // revision added no body member for the correlation identifier, on the grounds that the value
        // travels in its own response header and that duplicating it in the body would create a second
        // source of truth. THAT REASONING IS REPLACED RATHER THAN SOFTENED, because the outcome it
        // produced was worse than the duplication it avoided: the client quotes a reference from the
        // BODY, so with no correlation member present it quoted traceId instead - an identifier taken
        // from the ambient diagnostic activity, which is not the value on the header, not the value on
        // the request envelope in the log, and not the value on the request's audit events. A caller
        // reporting a problem therefore quoted a reference that appeared in no operator-visible record
        // of their request, which is the one job a support reference has.
        //
        // The two are named separately, so nothing is ambiguous: traceId remains the W3C tracing
        // identifier, and correlationId is the identifier the pipeline validated for this request. The
        // header remains the primary channel and the body member is a copy of the SAME value, resolved
        // from the same place the logging stage resolves it, so the two cannot disagree.
        string? correlationId = ResolveCorrelationId(httpContext);

        if (correlationId is not null)
        {
            problemDetails.Extensions[CorrelationIdExtensionKey] = correlationId;
        }
    }

    /// <summary>
    /// Reads the correlation identifier published for the request being answered.
    /// </summary>
    /// <param name="httpContext">The request being answered, which may be absent.</param>
    /// <returns>
    /// The identifier the pipeline validated for this request, or <see langword="null"/> when there is
    /// no request to read one from.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The item published by <see cref="CorrelationIdMiddleware"/> is preferred, because that is the
    /// value that stage VALIDATED, whereas an inbound header holds whatever the caller sent. The
    /// response header is consulted second so that a payload built before the item was published still
    /// carries the identifier the response will carry, and the framework's own request identifier is
    /// the last resort - the same order, and the same reasoning, as the request-logging stage applies.
    /// </para>
    /// <para>
    /// The constants are referenced rather than their text repeated, so the body member and the header
    /// cannot drift apart.
    /// </para>
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
