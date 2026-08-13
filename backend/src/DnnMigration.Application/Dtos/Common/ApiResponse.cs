namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The success envelope for an API response that carries a payload, pairing that payload with the optional
/// <see cref="ApiMeta"/> companion.
/// </summary>
/// <typeparam name="T">The transported payload type.</typeparam>
/// <remarks>
/// The type is an inert data carrier: no validation, clamping, normalisation, computed member or data-store
/// access. Request validation belongs to <c>Application/Validation/</c>, translation to and from a
/// persisted record to <c>Application/Mapping/</c>, and serialiser configuration - including the naming
/// policy applied to these member names - to the API layer.
/// </remarks>
// No correlation identifier member is present. The correlation value travels in the X-Correlation-Id
// header, written by the API-layer correlation middleware and read by the matching client interceptor;
// repeating it in the body would create a second source of truth.
public sealed class ApiResponse<T>
{
    /// <summary>Gets the payload the endpoint produced, exactly as the application layer supplied it.</summary>
    public T Data { get; init; } = default!;

    /// <summary>
    /// Gets the metadata describing the response, or <see langword="null"/> when the response has no page
    /// to describe: populated for a collection response, absent for a single item.
    /// </summary>
    /// <remarks>
    /// Nullable by design, and annotated as such rather than left to a suppression: a scalar payload
    /// genuinely has no total, page index or page size, and emitting zeroes for them would be
    /// indistinguishable from a real, empty first page.
    /// </remarks>
    public ApiMeta? Meta { get; init; }

    /// <summary>
    /// Creates a success envelope carrying <paramref name="data"/> and no metadata: the form every
    /// single-resource endpoint uses.
    /// </summary>
    /// <param name="data">The payload to transport.</param>
    /// <returns>An envelope whose <see cref="Meta"/> is <see langword="null"/>.</returns>
    public static ApiResponse<T> Success(T data) => new() { Data = data };

    /// <summary>
    /// Creates a success envelope carrying <paramref name="data"/> together with the metadata that
    /// describes it: the form for a payload that has a window to report without being paged.
    /// </summary>
    /// <param name="data">The payload to transport.</param>
    /// <param name="meta">The metadata describing the payload.</param>
    /// <returns>An envelope carrying both values.</returns>
    public static ApiResponse<T> Success(T data, ApiMeta meta) => new() { Data = data, Meta = meta };
}

/// <summary>
/// The declared success envelope for an API response that carries no payload. No endpoint returns it; the
/// reason is below.
/// </summary>
public sealed class ApiResponse
{
    /// <summary>
    /// Gets the metadata describing the response, or <see langword="null"/> when there is no page to
    /// describe - which is every case for this form, since no endpoint here returns it. Present for
    /// symmetry with the generic form and for the endpoint that would have a total to report but no records
    /// to return with it.
    /// </summary>
    public ApiMeta? Meta { get; init; }

    /// <summary>
    /// Creates a payload-free success envelope with no metadata: the form an endpoint would use when the
    /// fact that the request succeeded is the entire response, were such a response given a body at all in
    /// this API rather than answered <c>204</c>.
    /// </summary>
    /// <returns>An envelope whose <see cref="Meta"/> is <see langword="null"/>.</returns>
    public static ApiResponse Success() => new();

    /// <summary>
    /// Creates a payload-free success envelope carrying <paramref name="meta"/>: the form an endpoint would
    /// use when it has a total to report but no records to return with it.
    /// </summary>
    /// <param name="meta">The metadata describing the response.</param>
    /// <returns>An envelope carrying <paramref name="meta"/>.</returns>
    public static ApiResponse Success(ApiMeta meta) => new() { Meta = meta };
}
