namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging, sorting and filtering contract bound from the BODY of <c>POST /api/v1/portals/search</c>.
/// </summary>
/// <remarks>
/// ⚠ THIS TYPE EXISTS FOR A PRIVACY REASON, NOT AN ERGONOMIC ONE, AND MUST NOT BE COLLAPSED BACK INTO A
/// QUERY STRING. It carries TWO caller-chosen terms - the inherited free-text term and the site-name
/// filter below - and binding them from the query string wrote both into the request line that the reverse
/// proxy's access log and this application's own request log record. The reasoning is set out in full on
/// <see cref="ModuleSearchRequest"/>, and the account listing had already settled it the same way.
/// </remarks>
public sealed class PortalSearchRequest : PortalPagedRequest
{
    /// <summary>Restricts the result to portals whose title begins with this text.</summary>
    public string? Name { get; set; }
}
