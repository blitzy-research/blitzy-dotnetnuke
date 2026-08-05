using System.Globalization;
using System.Security.Claims;
using DnnMigration.Application.Abstractions;

namespace DnnMigration.Api.Authorization;

/// <summary>
/// Projects the verified claims of the authenticated principal into the caller facts the layers beneath
/// depend upon.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>UserController.GetCurrentUserInfo()</c> (<c>UserController.vb:L381</c>), which
/// reached into ambient request state from anywhere in the call stack and returned a hollow object when
/// nobody was signed in - so every caller had to know that an object with an identifier of -1 meant
/// "absent". That sentinel is deliberately not carried forward: absence is carried by
/// <see langword="null"/>, and neither -1 nor 0 is ever coalesced to null, because both are legitimate
/// identifiers in this schema.
/// </para>
/// <para>
/// <strong>Every member is synchronous and does no work worth mentioning.</strong> The legacy roles
/// accessor (<c>UserInfo.vb:L261-269</c>) issued a database query on first read from inside a property
/// getter, which made an innocuous-looking expression a hidden round trip. Nothing here performs I/O,
/// blocks, fills a cache or logs. The claims are projected once, on first access, into an immutable
/// snapshot, and every getter after that is a field read.
/// </para>
/// <para>
/// <strong>Why the projection is lazy rather than done in the constructor.</strong> This service is
/// scoped, and a scoped instance is created when it is first requested. Projecting in the constructor
/// would bind the snapshot to whatever principal existed at that moment, so anything that resolved this
/// service before the authentication middleware had run would be served an anonymous snapshot for the
/// rest of the request - and would do so silently. Reading the principal on first access instead means the
/// snapshot always reflects the authenticated principal, whatever the resolution order turns out to be.
/// </para>
/// <para>
/// The claim names come from the shared vocabulary beside <see cref="ITokenService"/>, never from literals
/// spelled here. A mis-spelled claim name would not fail to compile and would not throw: it would present
/// as a caller who signs in successfully and then holds no permissions, which is indistinguishable from a
/// genuine authorisation denial.
/// </para>
/// </remarks>
internal sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    private Snapshot? _snapshot;

    /// <summary>Initialises a new instance of the <see cref="CurrentUser"/> class.</summary>
    /// <param name="httpContextAccessor">Supplies the request's authenticated principal.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpContextAccessor"/> is <see langword="null"/>.
    /// </exception>
    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor
            ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    /// <inheritdoc />
    public bool IsAuthenticated => Current.IsAuthenticated;

    /// <inheritdoc />
    public int? UserId => Current.UserId;

    /// <inheritdoc />
    public string? UserName => Current.UserName;

    /// <inheritdoc />
    public int? PortalId => Current.PortalId;

    /// <inheritdoc />
    public bool IsSuperUser => Current.IsSuperUser;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => Current.Roles;

    /// <inheritdoc />
    public IReadOnlyList<string> PermissionKeys => Current.PermissionKeys;

    /// <summary>Gets the projected snapshot, building it on first access.</summary>
    private Snapshot Current => _snapshot ??= Project(_httpContextAccessor.HttpContext?.User);

    /// <summary>Projects a principal into an immutable snapshot.</summary>
    /// <param name="principal">The request's principal, or <see langword="null"/> outside a request.</param>
    /// <returns>The snapshot.</returns>
    /// <remarks>
    /// An unauthenticated principal yields the anonymous snapshot rather than a partially populated one.
    /// The framework presents an unauthenticated request as a principal with an identity that reports
    /// itself unauthenticated, so the check is on the identity rather than on the presence of the object.
    /// </remarks>
    private static Snapshot Project(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is null || !principal.Identity.IsAuthenticated)
        {
            return Snapshot.Anonymous;
        }

        return new Snapshot(
            IsAuthenticated: true,
            UserId: ReadInt(principal, DnnClaimTypes.Subject),
            UserName: null,
            PortalId: ReadInt(principal, DnnClaimTypes.PortalId),
            IsSuperUser: false,
            Roles: Array.Empty<string>(),
            PermissionKeys: Array.Empty<string>());
    }

    /// <summary>Reads one claim as text.</summary>
    /// <param name="principal">The principal to read.</param>
    /// <param name="claimType">The claim name.</param>
    /// <returns>The value, or <see langword="null"/> when the claim is absent or blank.</returns>
    private static string? ReadString(ClaimsPrincipal principal, string claimType)
    {
        string? value = principal.FindFirst(claimType)?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Reads one claim as an integer.</summary>
    /// <param name="principal">The principal to read.</param>
    /// <param name="claimType">The claim name.</param>
    /// <returns>The value, or <see langword="null"/> when the claim is absent or unparseable.</returns>
    /// <remarks>
    /// Parsed with the invariant culture, because the claim was written by this system in that culture and
    /// a token must mean the same thing wherever it is read. An unparseable value yields null rather than a
    /// substituted default: a token carrying a malformed identifier is not a token carrying identifier
    /// zero, and treating it as one would grant access to whatever row happens to occupy that key.
    /// </remarks>
    private static int? ReadInt(ClaimsPrincipal principal, string claimType)
    {
        string? value = ReadString(principal, claimType);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    /// <summary>The immutable projection of one request's principal.</summary>
    /// <param name="IsAuthenticated">Whether a principal was authenticated.</param>
    /// <param name="UserId">The account identifier, or null when anonymous.</param>
    /// <param name="UserName">The compatibility name projection; always null.</param>
    /// <param name="PortalId">The tenant the token was issued for, or null when anonymous.</param>
    /// <param name="IsSuperUser">The compatibility host projection; always false.</param>
    /// <param name="Roles">The compatibility role projection; always empty.</param>
    /// <param name="PermissionKeys">The compatibility permission projection; always empty.</param>
    private sealed record Snapshot(
        bool IsAuthenticated,
        int? UserId,
        string? UserName,
        int? PortalId,
        bool IsSuperUser,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> PermissionKeys)
    {
        /// <summary>The snapshot served for a request with no authenticated principal.</summary>
        public static Snapshot Anonymous { get; } = new(
            IsAuthenticated: false,
            UserId: null,
            UserName: null,
            PortalId: null,
            IsSuperUser: false,
            Roles: Array.Empty<string>(),
            PermissionKeys: Array.Empty<string>());
    }
}
