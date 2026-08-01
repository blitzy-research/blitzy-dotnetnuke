namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The standard success envelope for an API response that carries a payload,
/// pairing that payload with the optional <see cref="ApiMeta"/> companion.
/// </summary>
/// <typeparam name="T">
/// The transported payload type. Always a data transfer contract from
/// <c>Application/Dtos/</c>, and never a persisted record type: keeping the
/// transported shape distinct from the stored one is what allows the legacy
/// sentinel semantics noted below to be honoured at the API edge without
/// contaminating the model behind it. The parameter is deliberately
/// unconstrained, so a single item, a read-only list of items or a scalar are all
/// expressible without this type ever naming a collection of its own.
/// </typeparam>
/// <remarks>
/// <para>
/// Every one of the eleven API controllers returns this shape on success, which
/// is the whole point of the type: a client writes one deserialisation path
/// rather than eleven, and the Angular models mirror two member names rather than
/// a different envelope per resource.
/// </para>
/// <para>
/// The envelope describes success and nothing else, and a failure never travels
/// in it. An expected failure is carried inside the application by the domain
/// result types; an unexpected one is shaped into an RFC 7807 problem document by
/// <c>Api/ErrorHandling/GlobalExceptionHandler.cs</c> and by the
/// request-validation factory beside it under <c>Api/Filters/</c>. Reproducing
/// either channel here would give the system two ways to report one failure, so
/// there is no outcome flag, no failure member, no per-field validation map and
/// no transport-level code below. The response code belongs to the HTTP response
/// line and is set by the controller; a body that claimed failure alongside a
/// successful response code is the exact ambiguity RFC 7807 exists to remove.
/// </para>
/// <para>
/// <see cref="Meta"/> is optional because most responses have no page to
/// describe. A collection response populates it; a single-item response leaves it
/// absent.
/// </para>
/// <para>
/// The type is an inert data carrier. It holds two values and exposes no
/// behaviour: no validation, no clamping, no normalisation, no computed member
/// and no access to any data store. Request validation belongs to
/// <c>Application/Validation/</c>, translation between this contract and a
/// persisted record belongs to <c>Application/Mapping/</c>, and serialiser
/// configuration, including the naming policy applied to these member names,
/// belongs to the API layer. Nothing here reads or writes anything, so nothing
/// here is awaitable; that is by design and not an omission.
/// </para>
/// <para>
/// The payload is carried through untouched. No member rewrites it, no
/// serialisation attribute is applied to it and no conditional-omission policy is
/// declared for it, because the legacy null contract in
/// <c>Library/Components/Shared/Null.vb</c> represents an absent string as the
/// empty string rather than as null, and an absent integer as minus one. Either
/// value quietly turned into null would change what an existing consumer reads.
/// Minus one is doubly load-bearing: it is both that sentinel and a legitimate
/// portal identifier, because <c>Portals.PortalID</c> is declared with an
/// identity seed of minus one, while <c>Roles.RoleID</c>, <c>Tabs.TabID</c> and
/// <c>Modules.ModuleID</c> all seed at zero. Accordingly, no value on this
/// envelope is ever tested for absence by comparing it against a number.
/// </para>
/// <para>
/// Members are initialise-only, so an instance cannot be altered once built and
/// is safe to publish across threads. It stays plain enough for a serialiser to
/// materialise, which is what the integration suites depend on when they read a
/// response body back.
/// </para>
/// </remarks>
// MIGRATION: net-new type with no legacy predecessor. DotNetNuke 4.9.0 returned
// bare, untyped, pre-generics collections directly from its controllers:
// GetPortals at Library/Components/Portal/PortalController.vb line 1263 returned
// one with no element type, no total and no envelope, and GetUsers at
// Library/Components/Users/UserController.vb line 685 did the same. Response
// metadata never crossed a serialisation boundary at all; it was assigned onto a
// Web Forms pager control at Website/admin/Users/Users.ascx.vb lines 285 to 287.
// Introducing an envelope follows from replacing server-rendered pages with a
// JSON API and changes no business rule.
//
// MIGRATION: this envelope describes success only, and its omissions are
// deliberate rather than incidental. Expected failures are carried inside the
// application by the domain result types, and unexpected ones are shaped into an
// RFC 7807 problem document by Api/ErrorHandling/GlobalExceptionHandler.cs and
// the request-validation factory under Api/Filters/. Duplicating either channel
// here would leave the system with two ways to report the same failure, so this
// type carries no outcome flag, no failure member, no per-field validation map
// and no transport-level code.
//
// MIGRATION: no correlation identifier member is present, and the omission is
// deliberate. The request correlation value travels in the X-Correlation-Id HTTP
// header, written by the API-layer correlation middleware and read by the
// matching Angular HTTP interceptor. Repeating it in the response body would
// create a second source of truth that no client consults.
//
// MIGRATION: the paging idiom this envelope retires was measured directly rather
// than assumed. A reading of Library/Components/Users/UserController.vb found
// EIGHT ByRef totalRecords out-parameters, at lines 725, 746, 769, 793, 816, 840,
// 864 and 889, being GetUsers, GetUsersByEmail, GetUsersByUserName and
// GetUsersByProfileProperty twice each, where the action plan records three. The
// larger figure is a refinement of the count, reported rather than silently
// corrected; the directive it supports is unchanged. All eight handed the total
// back through the argument list instead of returning it.
public sealed class ApiResponse<T>
{
    /// <summary>
    /// Gets the payload the endpoint produced.
    /// </summary>
    /// <value>
    /// The transported contract, exactly as the application layer supplied it.
    /// </value>
    /// <remarks>
    /// <para>
    /// Initialise-only, so an envelope cannot be repointed at a different payload
    /// once built, while staying assignable by a serialiser materialising a
    /// response body.
    /// </para>
    /// <para>
    /// The initialiser is present because <typeparamref name="T"/> is
    /// unconstrained, which leaves the compiler unable to see that every
    /// construction path assigns this member. Stating the intent here is the
    /// correct response; widening the solution's warning suppressions to hide the
    /// same diagnostic is not. Each factory below assigns a real payload, and a
    /// serialiser assigns one read from the wire.
    /// </para>
    /// </remarks>
    public T Data { get; init; } = default!;

    /// <summary>
    /// Gets the metadata describing the response, or <c>null</c> when the
    /// response has no page to describe.
    /// </summary>
    /// <value>
    /// An <see cref="ApiMeta"/> for a collection response; <c>null</c> for a
    /// single-item response.
    /// </value>
    /// <remarks>
    /// Nullable by design, and annotated as such rather than left to a
    /// suppression: a scalar payload genuinely has no total, no page index and no
    /// page size, and emitting zeroes for them would be indistinguishable from a
    /// real, empty first page. The member composes <see cref="ApiMeta"/> and
    /// restates none of it, so the three paging facts and the page count derived
    /// from them stay defined in exactly one place.
    /// </remarks>
    public ApiMeta? Meta { get; init; }

    /// <summary>
    /// Creates a success envelope carrying <paramref name="data"/> and no
    /// metadata.
    /// </summary>
    /// <param name="data">The payload to transport.</param>
    /// <returns>
    /// An envelope whose <see cref="Data"/> is <paramref name="data"/> and whose
    /// <see cref="Meta"/> is <c>null</c>.
    /// </returns>
    /// <remarks>
    /// The form a single-item endpoint uses. The payload is stored as given and
    /// is neither inspected nor rejected here: whether a particular contract may
    /// be null is a question for the endpoint that builds it, and guarding it
    /// here would put behaviour into a type whose contract is to have none.
    /// </remarks>
    public static ApiResponse<T> Success(T data) => new() { Data = data };

    /// <summary>
    /// Creates a success envelope carrying <paramref name="data"/> together with
    /// the metadata that describes it.
    /// </summary>
    /// <param name="data">The payload to transport.</param>
    /// <param name="meta">The metadata describing the payload.</param>
    /// <returns>
    /// An envelope whose <see cref="Data"/> is <paramref name="data"/> and whose
    /// <see cref="Meta"/> is <paramref name="meta"/>.
    /// </returns>
    /// <remarks>
    /// The form a collection endpoint uses, where <typeparamref name="T"/> is
    /// typically a read-only list of item contracts and the metadata reports the
    /// page that produced it.
    /// </remarks>
    public static ApiResponse<T> Success(T data, ApiMeta meta) => new() { Data = data, Meta = meta };
}

/// <summary>
/// The standard success envelope for an API response that carries no payload,
/// such as a deletion that reports only that it happened.
/// </summary>
/// <remarks>
/// <para>
/// The payload-free companion to <c>ApiResponse&lt;T&gt;</c>, declared beside it
/// so that the two arities of one contract are read and maintained together. The
/// surfaces are kept symmetrical: the same optional metadata member and the same
/// pair of factories, minus the payload this form has nothing to put in.
/// </para>
/// <para>
/// The two types deliberately do not inherit from one another. A payload-bearing
/// envelope assigned to a payload-free declared type would serialise without its
/// payload, silently, and no compiler would object; keeping them separate makes
/// that mistake unexpressible.
/// </para>
/// <para>
/// Everything said of the generic form applies here unchanged. This is a success
/// envelope only, so it carries no outcome flag, no failure member, no per-field
/// validation map, no transport-level code and no correlation identifier: an
/// expected failure travels as a domain result inside the application, an
/// unexpected one as an RFC 7807 problem document from the API edge, the response
/// code as the HTTP response line, and the correlation value as the
/// X-Correlation-Id header.
/// </para>
/// </remarks>
// MIGRATION: net-new, with the same absence of a legacy predecessor as the
// generic form. A DotNetNuke 4.9.0 administration page reported a completed
// action by re-rendering itself, so there was no payload-free response shape to
// port; this type is what an HTTP client receives in its place.
public sealed class ApiResponse
{
    /// <summary>
    /// Gets the metadata describing the response, or <c>null</c> when the
    /// response has no page to describe.
    /// </summary>
    /// <value>
    /// An <see cref="ApiMeta"/> when an endpoint reports a count without
    /// returning the records behind it; <c>null</c> otherwise, which is the usual
    /// case for this form.
    /// </value>
    /// <remarks>
    /// Present for symmetry with the generic form and for the endpoint that has a
    /// total to report but no records to return. It composes
    /// <see cref="ApiMeta"/> and restates none of it.
    /// </remarks>
    public ApiMeta? Meta { get; init; }

    /// <summary>
    /// Creates a payload-free success envelope with no metadata.
    /// </summary>
    /// <returns>An envelope whose <see cref="Meta"/> is <c>null</c>.</returns>
    /// <remarks>
    /// The form an endpoint uses when the fact that the request succeeded is the
    /// entire response.
    /// </remarks>
    public static ApiResponse Success() => new();

    /// <summary>
    /// Creates a payload-free success envelope carrying
    /// <paramref name="meta"/>.
    /// </summary>
    /// <param name="meta">The metadata describing the response.</param>
    /// <returns>
    /// An envelope whose <see cref="Meta"/> is <paramref name="meta"/>.
    /// </returns>
    /// <remarks>
    /// The form an endpoint uses when it has a total to report but no records to
    /// return with it.
    /// </remarks>
    public static ApiResponse Success(ApiMeta meta) => new() { Meta = meta };
}
