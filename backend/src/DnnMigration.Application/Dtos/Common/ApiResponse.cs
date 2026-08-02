namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The standard success envelope for an API response that carries a payload, pairing that payload
/// with the optional <see cref="ApiMeta"/> companion.
/// </summary>
/// <typeparam name="T">
/// The transported payload type. Always a data transfer contract from <c>Application/Dtos/</c> and
/// never a persisted record type, which is what lets the legacy sentinel semantics noted below be
/// honoured at the API edge without contaminating the model behind it. The parameter is deliberately
/// unconstrained, so a single item, a read-only list or a scalar are all expressible without this
/// type ever naming a collection of its own.
/// </typeparam>
/// <remarks>
/// <para>
/// Every API controller is expected to return this shape on success, which is the point of the type:
/// a client writes one deserialisation path rather than one per resource, and the client models
/// mirror two member names rather than a different envelope per endpoint.
/// </para>
/// <para>
/// The envelope describes success and nothing else. An expected failure is carried inside the
/// application by the domain result types; an unexpected one is shaped into an RFC 7807 problem
/// document by <c>Api/ErrorHandling/GlobalExceptionHandler.cs</c> and by the request-validation
/// factory beside it under <c>Api/Filters/</c>. Reproducing either channel here would give the system
/// two ways to report one failure, so there is no outcome flag, no failure member, no per-field
/// validation map and no transport-level code. The response code belongs to the HTTP response line;
/// a body claiming failure alongside a successful response code is the exact ambiguity RFC 7807
/// exists to remove.
/// </para>
/// <para>
/// The type is an inert data carrier: no validation, clamping, normalisation, computed member or
/// data-store access. Request validation belongs to <c>Application/Validation/</c>, translation to
/// and from a persisted record to <c>Application/Mapping/</c>, and serialiser configuration -
/// including the naming policy applied to these member names - to the API layer.
/// </para>
/// <para>
/// The payload is carried through untouched: no member rewrites it, no serialisation attribute is
/// applied and no conditional-omission policy is declared, because the legacy null contract
/// represents an absent string as the empty string and an absent integer as minus one, and either
/// value quietly turned into null would change what an existing consumer reads. Minus one is doubly
/// load-bearing, being both that sentinel and a legitimate portal identifier, because
/// <c>Portals.PortalID</c> seeds its identity at minus one while <c>Roles.RoleID</c>,
/// <c>Tabs.TabID</c> and <c>Modules.ModuleID</c> seed at zero. Accordingly no value on this envelope
/// is ever tested for absence by comparing it against a number.
/// </para>
/// <para>
/// Members are initialise-only, so an instance cannot be altered once built and is safe to publish
/// across threads, while staying plain enough for a serialiser to materialise from a response body.
/// </para>
/// </remarks>
// MIGRATION: this envelope has no legacy predecessor. DotNetNuke 4.9.0 returned bare, untyped,
// pre-generics collections straight from its controllers - GetPortals at
// Library/Components/Portal/PortalController.vb L1263 and GetUsers at
// Library/Components/Users/UserController.vb L685, neither carrying an element type, a total or an
// envelope - and response metadata never crossed a serialisation boundary at all, being assigned
// onto a Web Forms pager control instead. Introducing an envelope follows from replacing
// server-rendered pages with a JSON API and changes no business rule.
//
// MIGRATION: no correlation identifier member is present. The correlation value travels in the
// X-Correlation-Id header, written by the API-layer correlation middleware and read by the matching
// client interceptor; repeating it in the body would create a second source of truth.
public sealed class ApiResponse<T>
{
    /// <summary>
    /// Gets the payload the endpoint produced, exactly as the application layer supplied it.
    /// </summary>
    /// <remarks>
    /// Initialise-only, so an envelope cannot be repointed at a different payload once built, while
    /// staying assignable by a serialiser materialising a response body. The initialiser is present
    /// because <typeparamref name="T"/> is unconstrained, which leaves the compiler unable to see that
    /// every construction path assigns this member; stating the intent here is the correct response,
    /// rather than widening the solution's warning suppressions to hide the same diagnostic.
    /// </remarks>
    public T Data { get; init; } = default!;

    /// <summary>
    /// Gets the metadata describing the response, or <see langword="null"/> when the response has no
    /// page to describe: populated for a collection response, absent for a single item.
    /// </summary>
    /// <remarks>
    /// Nullable by design, and annotated as such rather than left to a suppression: a scalar payload
    /// genuinely has no total, page index or page size, and emitting zeroes for them would be
    /// indistinguishable from a real, empty first page. The member composes <see cref="ApiMeta"/> and
    /// restates none of it, so the three paging facts and the count derived from them stay defined in
    /// exactly one place.
    /// </remarks>
    public ApiMeta? Meta { get; init; }

    /// <summary>
    /// Creates a success envelope carrying <paramref name="data"/> and no metadata: the form a
    /// single-item endpoint uses.
    /// </summary>
    /// <param name="data">The payload to transport.</param>
    /// <returns>An envelope whose <see cref="Meta"/> is <see langword="null"/>.</returns>
    /// <remarks>
    /// The payload is stored as given and is neither inspected nor rejected here: whether a particular
    /// contract may be null is a question for the endpoint that builds it, and guarding it here would
    /// put behaviour into a type whose contract is to have none.
    /// </remarks>
    public static ApiResponse<T> Success(T data) => new() { Data = data };

    /// <summary>
    /// Creates a success envelope carrying <paramref name="data"/> together with the metadata that
    /// describes it: the form a collection endpoint uses.
    /// </summary>
    /// <param name="data">The payload to transport.</param>
    /// <param name="meta">The metadata describing the payload.</param>
    /// <returns>An envelope carrying both values.</returns>
    public static ApiResponse<T> Success(T data, ApiMeta meta) => new() { Data = data, Meta = meta };
}

/// <summary>
/// The standard success envelope for an API response that carries no payload, such as a deletion
/// that reports only that it happened.
/// </summary>
/// <remarks>
/// <para>
/// The payload-free companion to <c>ApiResponse&lt;T&gt;</c>, declared beside it so the two arities
/// of one contract are read and maintained together, with a deliberately symmetrical surface: the
/// same optional metadata member and the same pair of factories, minus the payload.
/// </para>
/// <para>
/// The two types deliberately do not inherit from one another. A payload-bearing envelope assigned to
/// a payload-free declared type would serialise without its payload, silently, and no compiler would
/// object; keeping them separate makes that mistake unexpressible.
/// </para>
/// <para>
/// Everything said of the generic form applies here unchanged: success only, so no outcome flag,
/// failure member, per-field validation map, transport-level code or correlation identifier.
/// </para>
/// </remarks>
public sealed class ApiResponse
{
    /// <summary>
    /// Gets the metadata describing the response, or <see langword="null"/> when there is no page to
    /// describe - the usual case for this form. Present for symmetry with the generic form and for the
    /// endpoint that has a total to report but no records to return.
    /// </summary>
    public ApiMeta? Meta { get; init; }

    /// <summary>
    /// Creates a payload-free success envelope with no metadata: the form an endpoint uses when the
    /// fact that the request succeeded is the entire response.
    /// </summary>
    /// <returns>An envelope whose <see cref="Meta"/> is <see langword="null"/>.</returns>
    public static ApiResponse Success() => new();

    /// <summary>
    /// Creates a payload-free success envelope carrying <paramref name="meta"/>: the form an endpoint
    /// uses when it has a total to report but no records to return with it.
    /// </summary>
    /// <param name="meta">The metadata describing the response.</param>
    /// <returns>An envelope carrying <paramref name="meta"/>.</returns>
    public static ApiResponse Success(ApiMeta meta) => new() { Meta = meta };
}
