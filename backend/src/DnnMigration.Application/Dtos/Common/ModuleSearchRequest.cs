namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging, sorting and filtering contract bound from the BODY of <c>POST /api/v1/modules/search</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ THIS TYPE EXISTS FOR A PRIVACY REASON, NOT AN ERGONOMIC ONE, AND MUST NOT BE COLLAPSED BACK INTO A
/// QUERY STRING. <see cref="ModulePagedRequest"/> carries a free-text term, and the module listing bound it
/// from the query string, so every search a person typed was written into the request line that the reverse
/// proxy's access log and this application's own request log both record. A term is content the caller
/// chose; a page index is not. Moving the term into the body keeps it out of both logs while changing
/// nothing about what the server is asked.
/// </para>
/// <para>
/// The account listing had already settled this question the same way - see <c>UserSearchRequest</c> and
/// <c>POST /api/v1/users/search</c> - and the two listings disagreeing was the whole of the defect. This
/// type derives from the query-bound contract rather than restating it, so the two transports can never
/// drift apart in what they accept.
/// </para>
/// </remarks>
public sealed class ModuleSearchRequest : ModulePagedRequest
{
    /// <summary>Restricts the result to the placements on one page, or <see langword="null"/> for all.</summary>
    public int? TabId { get; set; }

    /// <summary>Whether placements that are in the recycle bin are included.</summary>
    public bool IncludeDeleted { get; set; }
}
