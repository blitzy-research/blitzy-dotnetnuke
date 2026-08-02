using DnnMigration.Application.Dtos.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the bounds for <see cref="PagedRequest"/>, the paging, sorting and
/// filtering query contract that every collection endpoint accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because the request contract deliberately enforces nothing.</b>
/// <c>PagedRequest</c> states at its own L34-L38 that "bounds belong to
/// <c>Application/Validation/PagedRequestValidator</c>, which is why no property below
/// corrects, coerces or clamps what a caller supplied", and it repeats the promise on
/// the page index at L96-L99 and on the page size at L121-L131. Until this type
/// existed, that promise was unkept: every coordinate a caller sent travelled
/// unchecked.
/// </para>
/// <para>
/// <b>An unchecked coordinate was not merely unbounded, it was a fault.</b> The reply
/// envelope refuses negative coordinates by throwing rather than by reporting:
/// <c>PagedResult&lt;T&gt;.Create</c> guards its page index at
/// <c>Domain/Common/PagedResult.cs:L206</c> and its page size at L207 with
/// <c>ArgumentOutOfRangeException.ThrowIfNegative</c>, and
/// <c>IPortalService.cs:L164-L165</c> names exactly that behaviour - "the paged
/// envelope's own factory throws on negative coordinates rather than reporting them".
/// A negative page index therefore reached the reply envelope and became an unhandled
/// invariant failure, which the API edge can only render as a server fault. Rejecting
/// it here turns a 500 into the field-level 400 it always was.
/// </para>
/// <para>
/// <b>Bounds are rejections, never substitutions.</b> No rule below rewrites a value,
/// and this type has no clamping path at all. That is the request contract's
/// instruction rather than a preference of this file: "capping a page size protects
/// the server, but it is a policy decision that has to be visible as a rejection or an
/// echoed-back size, never as a quiet substitution inside a setter"
/// (<c>PagedRequest.cs:L128-L131</c>). A caller who asks for an impossible page learns
/// that the request was malformed instead of silently receiving a page it never asked
/// for.
/// </para>
/// <para>
/// <b>The sortable field set is per endpoint, which is why this type is derived from
/// rather than used directly.</b> The request contract settles the ownership question
/// at L146-L148: "the set of sortable names is decided by the repository that serves
/// the endpoint, and an unrecognised name is a validation failure rather than something
/// to be passed through to a store". One validator cannot hold four different sets, so
/// the set is a hook - <see cref="SortableFieldNames"/> - and each collection endpoint
/// contributes a derivation that declares its own. The base class permits no sort field
/// whatsoever, so an endpoint that has not declared a set refuses ordering rather than
/// forwarding an unvetted name to a store.
/// </para>
/// <para>
/// <b>The four derivations are declared in this file on purpose.</b> Each is a single
/// data declaration with no behaviour of its own, and keeping them together is what
/// makes it possible to see at a glance that no collection endpoint has been left
/// without a set - a property that four separate files would lose. The same reasoning
/// already governs <c>SortDirection</c>, which <c>PagedRequest.cs:L246-L250</c> keeps
/// beside its only consumer because "a fifth file for two members would add a file
/// without adding a boundary".
/// </para>
/// <para>
/// <b>Consumption.</b> A collection endpoint resolves the derivation that matches it
/// by its concrete type and validates before it reaches a repository. The base type is
/// deliberately not the single registration behind <c>IValidator&lt;PagedRequest&gt;</c>:
/// four sortable sets cannot share one open-generic registration, and a registration
/// that resolved an arbitrary one of them would apply the wrong endpoint's set. The
/// paging failure a service reports for coordinates it still cannot honour is a
/// separate, later condition and keeps its own stable code - <c>portal.paging_invalid</c>
/// on <c>IPortalService</c> - which this type neither replaces nor emits.
/// </para>
/// <para>
/// Rules are structural and stateless. Nothing here performs a lookup, reaches a store
/// or takes a collaborator, so every constructor is parameterless. Turning a failure
/// declared here into an RFC 7807 response belongs to the API layer.
/// </para>
/// </remarks>
// MIGRATION: an upper bound on the page size is net-new, and its absence in the legacy
// application was measured rather than assumed. The legacy page size was an
// operator-configured module setting, seeded with ten at
// Library/Components/Users/UserModuleBase.vb lines 134 and 135 and read by the account
// grid at Website/admin/Users/Users.ascx.vb lines 114 to 119, and no screen constrained
// it: Website/admin/Users/UserSettings.ascx declares no records-per-page control and no
// validator of any kind over the Records_ setting family it saves at
// UserSettings.ascx.vb line 117. Nothing legacy therefore prevented an operator from
// configuring an arbitrarily large page. The bound is introduced because the caller of
// this contract is an HTTP client rather than a trusted operator, and it is set an
// order of magnitude above the measured legacy default so that every page size the
// legacy application could plausibly have been configured with remains expressible.
//
// MIGRATION: the free-text filter is bounded by measurement rather than by preference.
// The widest column any of the four collection endpoints filters on is dbo.Users.Email,
// whose terminal width is nvarchar(256) - added at
// Website/Providers/DataProviders/SqlDataProvider/03.00.13.SqlDataProvider lines 109 and
// 110 - against dbo.Users.Username at nvarchar(100), dbo.Users.DisplayName at
// nvarchar(128), dbo.Portals.PortalName at nvarchar(128) and dbo.Roles.RoleName at
// nvarchar(50). The legacy search box carried no length attribute at all
// (Website/admin/Users/users.ascx line 7), so this bound is new, but it removes no
// reachable behaviour: the legacy match was a prefix match, and text longer than the
// widest column cannot prefix any value that column is able to hold.
public class PagedRequestValidator : AbstractValidator<PagedRequest>
{
    /// <summary>
    /// The largest page size a caller may ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten times the measured legacy default of ten, so every page size the legacy
    /// module setting could plausibly have carried is still expressible while a single
    /// request cannot ask the server to materialise an unbounded result set.
    /// </para>
    /// <para>
    /// Public so that the API layer can publish the figure in its generated OpenAPI
    /// document and a test can assert against it, both without restating the number.
    /// One definition and many readers is the same discipline the password byte ceiling
    /// follows: a bound copied into a second place is a bound that can drift.
    /// </para>
    /// </remarks>
    public const int MaximumPageSize = 100;

    /// <summary>
    /// The longest free-text filter a caller may supply, taken from the terminal width
    /// of the widest column any collection endpoint filters on, which is
    /// <c>dbo.Users.Email nvarchar(256) NULL</c>.
    /// </summary>
    /// <remarks>
    /// A filter longer than the widest filterable column cannot match any stored value,
    /// so the bound rejects only requests that were already incapable of returning a
    /// row. It is a length bound and nothing more: the text itself is passed through
    /// exactly as the caller typed it, because <c>PagedRequest.cs:L178-L182</c> requires
    /// that pattern composition happen behind the repository interfaces and not here.
    /// </remarks>
    public const int QueryMaximumLength = 256;

    /// <summary>
    /// Message reported when a caller supplies a negative page index.
    /// </summary>
    /// <remarks>
    /// The message states the base explicitly, because the commonest cause of a
    /// negative index is a client that counted from one and subtracted twice.
    /// </remarks>
    private const string PageIndexNegativeMessage =
        "The page index may not be negative. Page indexes are zero-based, so the first page is 0.";

    /// <summary>
    /// Message reported when a caller asks for a page of zero or fewer records.
    /// </summary>
    private const string PageSizeTooSmallMessage =
        "The page size must be at least 1.";

    /// <summary>
    /// Message reported when a caller supplies a sort direction that is not one of the
    /// two declared members.
    /// </summary>
    private const string SortDirectionUnknownMessage =
        "The sort direction must be either Ascending or Descending.";

    /// <summary>
    /// Message reported when a caller names a sort field on an endpoint that offers no
    /// caller-chosen ordering at all.
    /// </summary>
    private const string SortingUnsupportedMessage =
        "This endpoint does not accept a sort field.";

    /// <summary>
    /// Opening text of the message reported when a caller names a field the endpoint
    /// cannot order by. The permitted names follow.
    /// </summary>
    private const string SortFieldUnknownMessagePrefix =
        "The sort field is not one this endpoint can order by. Accepted fields: ";

    /// <summary>
    /// Separator placed between the permitted sort field names.
    /// </summary>
    private const string SortFieldSeparator = ", ";

    /// <summary>
    /// Closing text of the unrecognised-sort-field message.
    /// </summary>
    private const string SortFieldUnknownMessageSuffix = ".";

    /// <summary>
    /// Message reported when a caller asks for more records than
    /// <see cref="MaximumPageSize"/> permits.
    /// </summary>
    /// <remarks>
    /// Composed from the bound rather than restating it, and composed
    /// culture-invariantly so the figure reads identically for every caller.
    /// </remarks>
    private static readonly string PageSizeTooLargeMessage =
        FormattableString.Invariant($"The page size may not exceed {MaximumPageSize}.");

    /// <summary>
    /// Message reported when a caller supplies a filter longer than
    /// <see cref="QueryMaximumLength"/>.
    /// </summary>
    private static readonly string QueryTooLongMessage =
        FormattableString.Invariant($"The filter text may not exceed {QueryMaximumLength} characters.");

    /// <summary>
    /// Initialises a new instance of the <see cref="PagedRequestValidator"/> class and
    /// declares the bounds that apply to every collection endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parameterless by design. Every rule here is a question about the shape of the
    /// request and none is a question about state, so there is nothing to inject.
    /// </para>
    /// <para>
    /// The sort-field message is supplied through a callback rather than composed here.
    /// <see cref="SortableFieldNames"/> is overridable, and reading an overridable
    /// member from a constructor would read it before the derived instance was fully
    /// constructed; deferring the read to validation time avoids that entirely.
    /// </para>
    /// </remarks>
    public PagedRequestValidator()
    {
        // A negative index is rejected rather than reinterpreted, so the legacy
        // integer absence sentinel of minus one - Library/Components/Shared/Null.vb
        // line 43 - cannot arrive disguised as a page address, exactly as
        // PagedRequest.cs lines 96 to 99 require.
        RuleFor(request => request.PageIndex)
            .GreaterThanOrEqualTo(0)
            .WithMessage(PageIndexNegativeMessage);

        // Zero is refused on the REQUEST even though zero is meaningful on the REPLY,
        // where PagedResult<T>.IsUnpaged reports it at Domain/Common/PagedResult.cs
        // line 131. The two are not in conflict and neither should be changed to
        // resemble the other: an unpaged reply is produced by the server through the
        // envelope's own Unpaged factory, and is not something a caller can ask for by
        // sending a page size of nothing.
        RuleFor(request => request.PageSize)
            .GreaterThan(0)
            .WithMessage(PageSizeTooSmallMessage)
            .LessThanOrEqualTo(MaximumPageSize)
            .WithMessage(PageSizeTooLargeMessage);

        // Model binding will happily place an undeclared integer into an enum-typed
        // property, so the direction is checked for membership rather than assumed
        // valid. Only the two members declared beside the request contract can order
        // anything, and a third value would reach a repository with no defined meaning.
        RuleFor(request => request.SortDir)
            .IsInEnum()
            .WithMessage(SortDirectionUnknownMessage);

        // A length bound only. The value is not trimmed, unescaped, decorated or
        // otherwise rewritten: PagedRequest.cs lines 178 to 182 keep the caller's text
        // intact so that a repository can tell a literal per-cent typed by a user from
        // a wildcard added by a caller. A null or blank filter satisfies this rule
        // without a guard, which matches the request contract's own reading of absent
        // and blank as the same request.
        RuleFor(request => request.Query)
            .MaximumLength(QueryMaximumLength)
            .WithMessage(QueryTooLongMessage);

        // Guarded by the request contract's own absent-versus-blank test rather than by
        // a second interpretation of it, so a caller who expressed no preference is not
        // asked to justify a field they never named.
        RuleFor(request => request.SortBy)
            .Must(IsSortableFieldName)
            .WithMessage(_ => BuildUnsortableFieldMessage())
            .When(request => request.HasSort);
    }

    /// <summary>
    /// Gets the field names this endpoint is able to order by, in the order they are
    /// reported to a caller whose sort field was not recognised.
    /// </summary>
    /// <value>
    /// An empty list on this base type, meaning the endpoint accepts no caller-chosen
    /// ordering at all. A derivation overrides it with the names its own projection and
    /// repository can order by.
    /// </value>
    /// <remarks>
    /// <para>
    /// <b>Empty means refuse, not permit.</b> The default is deliberately the closed
    /// one: a set that had to be populated before a name could pass cannot forward an
    /// unvetted field name to a store, whereas a default of "anything goes" would do
    /// exactly that for every endpoint whose author forgot to declare a set.
    /// </para>
    /// <para>
    /// <b>The names are the names a caller can see.</b> Each entry is a property name
    /// of the projected item the endpoint returns, so the vocabulary a caller sorts by
    /// and the vocabulary a caller reads are one vocabulary. A name that mapped to
    /// something absent from the projection would ask a caller to order by a field they
    /// could not observe.
    /// </para>
    /// </remarks>
    protected virtual IReadOnlyList<string> SortableFieldNames => Array.Empty<string>();

    /// <summary>
    /// Reports whether a caller-supplied sort field names one of this endpoint's
    /// sortable fields.
    /// </summary>
    /// <param name="candidate">The sort field exactly as the caller supplied it.</param>
    /// <returns>
    /// <see langword="true"/> when the candidate matches a permitted name ignoring
    /// case; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Case is ignored, whitespace is not.</b> Casing is ignored because the wire
    /// form of a property name is decided by the API layer's serialisation policy
    /// rather than by the caller, so a client that reads a camel-cased name in a
    /// response and echoes it back as a sort field must be understood. Surrounding
    /// whitespace is not trimmed, because trimming here would leave the repository
    /// receiving text this validator had already accepted in a form it never saw -
    /// a second place for the two to disagree. An exact published name is required.
    /// </para>
    /// <para>
    /// Iterative rather than set-based on purpose: the largest declared set holds
    /// eleven names, the comparison runs once per rejected request, and a loop keeps
    /// the ordinal, case-insensitive comparison visible at the point it is made.
    /// </para>
    /// </remarks>
    private bool IsSortableFieldName(string? candidate)
    {
        IReadOnlyList<string> permitted = SortableFieldNames;

        for (int index = 0; index < permitted.Count; index++)
        {
            if (string.Equals(permitted[index], candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Composes the message reported when a sort field is not one this endpoint can
    /// order by.
    /// </summary>
    /// <returns>
    /// A message naming the permitted fields, or a message stating that the endpoint
    /// offers no ordering when it declares none.
    /// </returns>
    /// <remarks>
    /// <b>The rejected value is deliberately not echoed.</b> Naming what is accepted
    /// tells a caller how to correct the request, whereas repeating what they sent tells
    /// them nothing they did not already know and reflects caller-supplied text into a
    /// response body. Messages on this surface describe the contract, never the
    /// submission.
    /// </remarks>
    private string BuildUnsortableFieldMessage()
    {
        IReadOnlyList<string> permitted = SortableFieldNames;

        if (permitted.Count == 0)
        {
            return SortingUnsupportedMessage;
        }

        return string.Concat(
            SortFieldUnknownMessagePrefix,
            string.Join(SortFieldSeparator, permitted),
            SortFieldUnknownMessageSuffix);
    }
}

/// <summary>
/// Bounds the paging request accepted by the portal listing endpoint.
/// </summary>
/// <remarks>
/// The sortable set is the set of columns the legacy portal grid bound, measured at
/// <c>Website/admin/Portal/portals.ascx:L23-L51</c>, expressed as the property names of
/// <c>PortalListItemDto</c>.
/// </remarks>
// MIGRATION: the alias column the legacy grid rendered at portals.ascx lines 37 to 41 is
// deliberately absent from the sortable set. It projects to a collection of host names
// rather than to a value, and a collection has no ordering a caller could name; the
// legacy grid rendered it as a repeated list and offered no ordering over it either.
public sealed class PortalListPagedRequestValidator : PagedRequestValidator
{
    /// <summary>
    /// The portal columns that can be ordered by, each a property of the projected
    /// portal list item.
    /// </summary>
    private static readonly IReadOnlyList<string> PortalSortableFields = Array.AsReadOnly(new[]
    {
        nameof(Dtos.Portal.PortalListItemDto.PortalId),
        nameof(Dtos.Portal.PortalListItemDto.PortalName),
        nameof(Dtos.Portal.PortalListItemDto.Users),
        nameof(Dtos.Portal.PortalListItemDto.Pages),
        nameof(Dtos.Portal.PortalListItemDto.HostSpace),
        nameof(Dtos.Portal.PortalListItemDto.HostFee),
        nameof(Dtos.Portal.PortalListItemDto.ExpiryDate),
    });

    /// <inheritdoc />
    protected override IReadOnlyList<string> SortableFieldNames => PortalSortableFields;
}

/// <summary>
/// Bounds the paging request accepted by the portal role listing endpoint.
/// </summary>
/// <remarks>
/// The sortable set is the set of columns the legacy roles grid bound, measured at
/// <c>Website/admin/Security/roles.ascx:L36-L75</c>, expressed as the property names of
/// <c>RoleListItemDto</c>. The paid-membership columns are included because preserving
/// them is a functional-parity requirement, and a caller who can see a service fee can
/// order by it.
/// </remarks>
public sealed class RoleListPagedRequestValidator : PagedRequestValidator
{
    /// <summary>
    /// The role columns that can be ordered by, each a property of the projected role
    /// list item.
    /// </summary>
    private static readonly IReadOnlyList<string> RoleSortableFields = Array.AsReadOnly(new[]
    {
        nameof(Dtos.Role.RoleListItemDto.RoleId),
        nameof(Dtos.Role.RoleListItemDto.RoleName),
        nameof(Dtos.Role.RoleListItemDto.Description),
        nameof(Dtos.Role.RoleListItemDto.ServiceFee),
        nameof(Dtos.Role.RoleListItemDto.BillingPeriod),
        nameof(Dtos.Role.RoleListItemDto.BillingFrequency),
        nameof(Dtos.Role.RoleListItemDto.TrialFee),
        nameof(Dtos.Role.RoleListItemDto.TrialPeriod),
        nameof(Dtos.Role.RoleListItemDto.TrialFrequency),
        nameof(Dtos.Role.RoleListItemDto.IsPublic),
        nameof(Dtos.Role.RoleListItemDto.AutoAssignment),
    });

    /// <inheritdoc />
    protected override IReadOnlyList<string> SortableFieldNames => RoleSortableFields;
}

/// <summary>
/// Bounds the paging request accepted by the endpoint that lists the members of one
/// role.
/// </summary>
/// <remarks>
/// The set is narrower than the account listing's because the endpoint's projection is
/// narrower in what it can meaningfully order: the role is fixed by the route, so a
/// role column would order nothing.
/// </remarks>
// MIGRATION: the legacy security-roles grid bound five columns at
// Website/admin/Security/securityroles.ascx lines 68 to 84 - the user identifier, the
// role identifier, the display name, the effective date and the expiry date - and only
// three are sortable here. The two assignment dates are excluded because the projected
// item does not carry them, a reduction IRoleService.cs lines 444 to 453 already records
// and annotates; offering them as sort fields would let a caller order by a value the
// response cannot show. The role identifier is excluded because it is supplied by the
// route and is therefore the same for every record on the page.
public sealed class RoleUserListPagedRequestValidator : PagedRequestValidator
{
    /// <summary>
    /// The member columns that can be ordered by within a single role.
    /// </summary>
    private static readonly IReadOnlyList<string> RoleUserSortableFields = Array.AsReadOnly(new[]
    {
        nameof(Dtos.User.UserListItemDto.UserId),
        nameof(Dtos.User.UserListItemDto.Username),
        nameof(Dtos.User.UserListItemDto.DisplayName),
    });

    /// <inheritdoc />
    protected override IReadOnlyList<string> SortableFieldNames => RoleUserSortableFields;
}

/// <summary>
/// Bounds the paging request accepted by the account listing endpoint.
/// </summary>
/// <remarks>
/// The sortable set is the set of columns the legacy account grid bound, measured at
/// <c>Website/admin/Users/users.ascx:L40-L74</c>, expressed as the property names of
/// <c>UserListItemDto</c>. Three legacy header names differ from the property names and
/// the property names are authoritative, because they are what a caller reads in the
/// response: the grid's <c>UserName</c> column is <c>Username</c>, its <c>LastLogin</c>
/// column is <c>LastLoginDate</c>, and its <c>Authorized</c> column is
/// <c>IsApproved</c>.
/// </remarks>
// MIGRATION: three columns the projected item carries are deliberately not sortable, and
// each omission has its own reason. The portal identifier is supplied by the route, so
// it is identical on every record of the page. The online flag is presence state rather
// than a stored column, so there is nothing for a store to order by. The super-user and
// locked-out flags were offered by no column of the legacy grid, whose bound set ends at
// users.ascx line 74; adding them would be a new capability, and a capability is added
// deliberately and visibly rather than smuggled into a validator's allowlist.
public sealed class UserListPagedRequestValidator : PagedRequestValidator
{
    /// <summary>
    /// The account columns that can be ordered by, each a property of the projected
    /// account list item.
    /// </summary>
    private static readonly IReadOnlyList<string> UserSortableFields = Array.AsReadOnly(new[]
    {
        nameof(Dtos.User.UserListItemDto.UserId),
        nameof(Dtos.User.UserListItemDto.Username),
        nameof(Dtos.User.UserListItemDto.FirstName),
        nameof(Dtos.User.UserListItemDto.LastName),
        nameof(Dtos.User.UserListItemDto.DisplayName),
        nameof(Dtos.User.UserListItemDto.Address),
        nameof(Dtos.User.UserListItemDto.Telephone),
        nameof(Dtos.User.UserListItemDto.Email),
        nameof(Dtos.User.UserListItemDto.CreatedDate),
        nameof(Dtos.User.UserListItemDto.LastLoginDate),
        nameof(Dtos.User.UserListItemDto.IsApproved),
    });

    /// <inheritdoc />
    protected override IReadOnlyList<string> SortableFieldNames => UserSortableFields;
}
