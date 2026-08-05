namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// One row of the user administration grid, returned by <c>GET /api/v1/users</c> as the element
/// type of the paged response envelope.
/// </summary>
/// <remarks>
/// <para>
/// The member set and its declaration order are measured from the legacy grid
/// (<c>Website/admin/Users/users.ascx</c> L40-L79), so rendering the properties in declaration
/// order reproduces the legacy column order. The authoritative server-side projection is the
/// terminal <c>vw_Users</c> view (<c>04.00.04.SqlDataProvider:L771</c>). Where a member name
/// differs from the legacy grid header the difference is noted on the member, because the header
/// spelling forms a settings key.
/// </para>
/// <para>
/// PER-COLUMN VISIBILITY IS APPLIED BEFORE THIS CONTRACT IS RETURNED. The legacy screen resolved it by
/// concatenating a literal prefix with the grid header - <c>Users.ascx.vb:L514</c> reads
/// <c>Dim settingKey As String = "Column_" + header</c> - against nine module settings seeded in
/// <c>UserModuleBase.vb:L98-L123</c>, with <c>Username</c> short-circuited to always-visible at
/// L512. The target enforces those settings in the service rather than trusting every client to hide
/// sensitive values correctly: a hidden text field is replaced by the empty-string sentinel, a hidden
/// nullable field by <see langword="null"/>, and the username remains present. There is deliberately no
/// visibility flag on this type, because the sensitive value itself must not cross the boundary merely
/// because a cooperative client could choose not to render it.
/// </para>
/// <para>
/// SEVERAL MEMBERS ARE NOT USER-ROW DATA. <see cref="PortalId"/> comes from the request route or
/// resolved portal context; <see cref="Address"/> and <see cref="Telephone"/> are composed from
/// the profile key-value store, whose columns were dropped off the user table by
/// <c>02.02.01.SqlDataProvider:L50-L51</c>; and <see cref="CreatedDate"/>,
/// <see cref="LastLoginDate"/>, <see cref="IsOnline"/> and <see cref="IsLockedOut"/> originate in
/// the externally installed ASP.NET membership store. None of the latter group is projected by
/// <c>vw_Users</c>.
/// </para>
/// <para>
/// SENTINEL BOUNDARY: the legacy null contract is a sentinel table rather than SQL <c>NULL</c> -
/// <c>Null.vb</c> L36-L84 encodes an absent integer as -1, an absent date as
/// <see cref="DateTime.MinValue"/>, an absent boolean as <see langword="false"/> and, the
/// consequential one, absent text as the empty string rather than a null reference. This type is
/// where those encodings are translated: absent text surfaces as <see cref="string.Empty"/> so
/// the legacy observable value is preserved, and an absent timestamp surfaces as
/// <see langword="null"/> so no client is handed <c>0001-01-01</c> as though it were a real
/// instant. The host serialises with
/// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.Never"/>
/// (<c>ServiceCollectionExtensions.cs</c>, stated on both the minimal-API and controller
/// surfaces), which omits nothing at all: every member is written, so an empty string, a
/// legitimate <see langword="false"/> on any of the four flags, and a nullable timestamp that has
/// no value all reach the wire - the last as a written <c>null</c>. The consequence for the
/// nullable members is that absence is signalled by the property's VALUE being <c>null</c>, not by
/// the property being missing, and a consumer reads null as the absent state. Nothing weaker would
/// do: the sentinels above are in-band values, so a policy that dropped nulls or defaults could
/// not be trusted to leave a legitimate <c>0</c>, <c>false</c> or empty string alone.
/// </para>
/// <para>
/// SECURITY: no credential material of any kind appears here - no stored credential, digest,
/// salt, format, recovery question or recovery answer - and none may be added. The legacy
/// retrieval path is not carried forward.
/// </para>
/// <para>
/// No paging member appears here: the eight legacy <c>ByRef totalRecords</c> overloads on
/// <c>UserController.vb</c> are retired in favour of the envelope, which alone owns the item
/// list, total count, page index and page size. Also deliberately absent: the create-status and
/// login-status enumerations, because an expected failure is a service-layer result rendered as
/// an RFC 7807 problem document at the API edge; the correlation identifier, which travels in the
/// <c>X-Correlation-Id</c> header; the affiliate identifier, which <c>vw_Users</c> projects but
/// the grid never rendered; and the role set, which belongs to the detail contract.
/// </para>
/// <para>
/// This type is inert. Composition - most notably the six-part address string - belongs to a
/// hand-written user mapper under <c>Application/Mapping/</c>, and rule enforcement to a
/// validator under <c>Application/Validation/</c>.
/// </para>
/// </remarks>
public sealed class UserListItemDto
{
    /// <summary>
    /// Gets or sets the user's identifier, from <c>Users.UserID</c>
    /// (<c>[int] IDENTITY (1, 1) NOT NULL</c>, <c>01.00.00.SqlDataProvider:L98</c>).
    /// </summary>
    /// <remarks>
    /// Presence is decided by the envelope containing the row, never by inspecting this value:
    /// the legacy constructor seeded the field with the integer sentinel -1, so a non-positive
    /// value is not evidence of absence.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal whose user list this row belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is NOT a column on the legacy <c>Users</c> table. The legacy
    /// <c>UserInfo.PortalID</c> (<c>UserInfo.vb:L219</c>) was an in-memory field; the per-portal
    /// facts live on <c>UserPortals</c>, which the terminal <c>vw_Users</c> view reaches through
    /// a LEFT OUTER JOIN (<c>04.00.04.SqlDataProvider:L771-L785</c>, projecting
    /// <c>UP.PortalId</c>). Because the join is outer, the legacy projection could yield SQL
    /// <c>NULL</c> for a host user with no <c>UserPortals</c> row, which the legacy read
    /// collapsed to -1 - the exact value that is also a real portal key. Populate this member
    /// from the request route or the resolved portal context, never from the user entity.
    /// </para>
    /// <para>
    /// IDENTIFIER TRAP: <c>Portals.PortalID</c> is declared
    /// <c>[int] IDENTITY (-1, 1) NOT NULL</c> (<c>01.00.00.SqlDataProvider:L77</c>), so the seed
    /// and first generated value is -1, while the shipped default portal row is inserted
    /// explicitly with <c>PortalID</c> 0 under <c>IDENTITY_INSERT</c>
    /// (<c>01.00.00.SqlDataProvider:L7125</c>); both are valid keys. The legacy sentinel for an
    /// absent integer is also -1 and the legacy null test reports true for it
    /// (<c>Null.vb</c> L41-L45, L207-L236). Both <c>id &lt;= 0</c> and <c>id == -1</c> are
    /// therefore invalid absence tests, here and against any portal, role, tab or module
    /// identifier - the role, tab and module tables are seeded <c>IDENTITY (0, 1)</c>, which
    /// makes 0 a real key too. Absence is expressed by a nullable type or an empty result, never
    /// by a magic number.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the login name, from <c>Users.Username</c>
    /// (<c>nvarchar(100) NOT NULL</c>). Always rendered by the legacy grid, which has no
    /// visibility setting for this column.
    /// </summary>
    /// <remarks>
    /// UNIQUENESS BELONGS HERE AND NOT TO <see cref="Email"/>: <c>01.00.07:L76-L84</c> moved the
    /// unique constraint off the email column onto this one and <c>03.00.09:L420-L422</c>
    /// re-asserted it as <c>IX_{objectQualifier}Users UNIQUE ([Username])</c>, matching the
    /// legacy membership registration, which did not require a unique email address. The
    /// 100-character ceiling is the schema's, and enforcing it belongs to the validators rather
    /// than to this type.
    /// </remarks>
    // MIGRATION: two legacy properties collapse into one wire field. UserInfo.Username
    // (UserInfo.vb:L301) mirrored every write onto a duplicate inside the deprecated-members
    // region of UserMembership.vb (region opens at L334, property at L356), described in the
    // source as a compatibility shim. The canonical field is the one on the user itself.
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the given name, from <c>Users.FirstName</c>
    /// (<c>nvarchar(50) NOT NULL</c>, unchanged through both table rebuilds from
    /// <c>01.00.00.SqlDataProvider:L99</c>).
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the family name, from <c>Users.LastName</c>.
    /// </summary>
    /// <remarks>
    /// The terminal nullability is not the baseline's: <c>01.00.00.SqlDataProvider:L100</c>
    /// declared <c>[nvarchar] (50) NULL</c>, but the <c>01.00.05</c> rebuild through
    /// <c>Tmp_Users</c> re-declared it <c>NOT NULL</c> and <c>01.00.06</c> preserved that, so at
    /// the terminal state the given and family names are both <c>NOT NULL</c>. The cumulative
    /// terminal schema is the binding authority, so this member is non-nullable and absent text
    /// is the empty string.
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical display name, from <c>Users.DisplayName</c>
    /// (<c>nvarchar(128) NOT NULL</c> defaulting to the empty string, added at
    /// <c>03.02.03.SqlDataProvider:L628-L631</c>) - which is why that is the value this member
    /// carries when unset.
    /// </summary>
    /// <remarks>
    /// Supersedes the obsolete computed full name. The legacy display-name token substitution
    /// (<c>UserInfo.vb:L358-L367</c> replaced <c>[USERID]</c>, <c>[FIRSTNAME]</c>,
    /// <c>[LASTNAME]</c> and <c>[USERNAME]</c> according to a module setting) is a service
    /// concern and is deliberately not performed here: this member carries the already-resolved
    /// value.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the pre-composed postal address for display.
    /// </summary>
    /// <remarks>
    /// TWO DISTINCT ABSENCES, both preserved: <see langword="null"/> means the portal's profile
    /// definitions carry no address data for this user at all - the honest representation of a
    /// key-value store with no matching rows - while an empty string means address properties
    /// exist but compose to nothing, which is the legacy behaviour
    /// (<c>Users.ascx.vb:L353</c> seeded its local with the empty-string sentinel and returned it
    /// when composition yielded nothing). Under the configured
    /// <c>Never</c> ignore policy the two absences remain distinguishable on the wire by VALUE,
    /// which is the simpler contract: the property is always present, written as <c>null</c> for
    /// the first state and as <c>""</c> for the second. The
    /// legacy grid composed it from six separate profile properties
    /// (<c>Users.ascx.vb:L352</c>); composing it in an accessor here is forbidden, because this
    /// type does no work.
    /// </remarks>
    public string? Address { get; set; }

    /// <summary>
    /// Gets or sets the telephone number for display.
    /// </summary>
    /// <remarks>
    /// As with <see cref="Address"/>, <see langword="null"/> means the profile carries no
    /// telephone property for this user and an empty string means the property exists and is
    /// blank; both states stay distinct on the wire, the first as a written <c>null</c> and the
    /// second as <c>""</c>, under the host's <c>Never</c> ignore policy.
    /// </remarks>
    public string? Telephone { get; set; }

    /// <summary>
    /// Gets or sets the email address, from <c>Users.Email</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terminal column is <c>nvarchar(256) NULL</c>
    /// (<c>03.00.13.SqlDataProvider:L109</c>), reached by a drop and re-create: the baseline
    /// declared it <c>nvarchar(100) NOT NULL</c> with a unique constraint
    /// (<c>01.00.00:L105</c>), <c>01.00.07</c> moved that constraint to the login name,
    /// <c>02.02.01:L50-L51</c> dropped the column entirely when credentials moved to the
    /// external membership store, and <c>03.00.13:L109-L110</c> re-added it at the wider,
    /// nullable width. VALIDATORS MUST USE 256 - the 100-character figure is the superseded
    /// baseline. This field carries no uniqueness guarantee, so two users in the same portal may
    /// legitimately share an address and no consumer may treat it as a key.
    /// </para>
    /// <para>
    /// Although the terminal column is nullable this member is not: the legacy property was
    /// declared required (<c>UserInfo.vb:L121</c>) and every legacy read passed through the
    /// sentinel translation that renders absent text as the empty string, so the externally
    /// observable value for a missing address has always been <see cref="string.Empty"/>.
    /// Emitting a null here would be a silent change to an observable value.
    /// </para>
    /// </remarks>
    // MIGRATION: two legacy properties collapse into one wire field, exactly as for the login
    // name. UserInfo.Email (UserInfo.vb:L123) mirrored every write onto a duplicate inside the
    // deprecated-members region of UserMembership.vb (region opens at L334, property at L344),
    // and the legacy grid actually bound the deprecated copy (users.ascx:L58). The canonical
    // field is the one on the user itself, so the shim is not reproduced.
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the instant the account was created, or <see langword="null"/> when unknown.
    /// </summary>
    /// <remarks>
    /// Callers must test for null and must never compare against
    /// <see cref="DateTime.MinValue"/>: that value is the legacy absent-date sentinel
    /// (<c>Null.vb</c> L66-L70, whose null test compares only the date component at L223-L225),
    /// and it is translated to <see langword="null"/> at this boundary rather than serialised, so
    /// that no client is handed <c>0001-01-01</c> as though it were a real timestamp. The
    /// membership store names the same value CreationDate; the legacy rename to CreatedDate is
    /// preserved here.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Gets or sets the instant of the most recent successful sign-in, or
    /// <see langword="null"/> when the account has never signed in.
    /// </summary>
    /// <remarks>
    /// A never-signed-in account is the ordinary reason for <see langword="null"/>, and it is a
    /// materially different state from "signed in at the dawn of the calendar", which is what the
    /// untranslated legacy sentinel would have implied. Test for null, never for
    /// <see cref="DateTime.MinValue"/>.
    /// </remarks>
    // MIGRATION: the legacy grid header is "LastLogin", not "LastLoginDate" (users.ascx:L68).
    // That spelling is load-bearing, because it is what forms the Column_LastLogin visibility key
    // through the "Column_" + header concatenation at Users.ascx.vb:L514 - so the settings
    // contract keeps the short name while this member takes the longer name of the actual column
    // and of the legacy property.
    public DateTime? LastLoginDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is approved for the portal - that is,
    /// authorised to sign in.
    /// </summary>
    /// <remarks>
    /// The legacy grid rendered this as a pair of mutually exclusive images bound to the true and
    /// the false case (<c>users.ascx:L76-L77</c>), so both states are meaningful and neither may
    /// be dropped from the payload: serialisation must not be configured to omit default values.
    /// </remarks>
    // MIGRATION: one member reconciles four legacy spellings of one concept - the grid header
    // "Authorized" (users.ascx:L74, which forms the Column_Authorized settings key), the legacy
    // property Approved without the Is prefix (UserMembership.vb:L83), the baseline US-spelled
    // column UserPortals.[Authorized] (01.00.00:L156, dropped by 02.02.01:L54-L55) and the
    // terminal UK-spelled column UserPortals.Authorised, re-added by 03.02.03:L639 as
    // "Authorised bit NOT NULL ... DEFAULT 1" and projected by vw_Users. A maintainer searching
    // the schema for the US spelling will not find it.
    // MIGRATION: the legacy default is true at both levels (UserMembership.vb:L45 declares
    // "_Approved As Boolean = True" and the terminal column defaults to 1), whereas this member
    // defaults to false. That is deliberate and fail-safe: a response row is always populated by
    // the mapper so the member default is never observed on the wire, and defaulting to true
    // could report an unapproved account as approved.
    public bool IsApproved { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is currently considered online. Advisory
    /// only: the legacy scheduled job that purged the presence table is outside this scope, so a
    /// stale presence row is possible.
    /// </summary>
    /// <remarks>
    /// This drove the legacy grid's online indicator, made visible exactly when the flag was set
    /// (<c>Users.ascx.vb:L702</c>) in the dedicated template column at
    /// <c>users.ascx:L35-L39</c>. It has no <c>Column_</c> visibility setting because it is an
    /// indicator rather than a toggleable data column. The legacy property spelling is
    /// <c>IsOnLine</c>, with a capital L in the middle (<c>UserMembership.vb:L123</c>).
    /// </remarks>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is a host-level super user, whose reach
    /// spans every portal rather than one. From <c>Users.IsSuperUser</c>
    /// (<c>bit NOT NULL</c> defaulting to 0, <c>01.00.02.SqlDataProvider:L243</c>).
    /// </summary>
    /// <remarks>
    /// The list row genuinely needs it: the legacy delete affordance was suppressed
    /// (<c>Users.ascx.vb:L693</c>) not only for the portal administrator but also when the row
    /// was the acting user and that user was a super user, so a client cannot reproduce the
    /// legacy action set without this flag. Advisory for rendering only - every authorisation
    /// decision is taken on the server.
    /// </remarks>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is under a lockout following repeated
    /// failed sign-in attempts.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="IsApproved"/>: an approved account can be under a lockout, and an
    /// account that is not approved need not be. Carried because a list that cannot distinguish a
    /// locked account from an unapproved one loses information the legacy screens conveyed. The
    /// lockout bookkeeping is maintained by membership procedures that the legacy upgrade scripts
    /// patch rather than create (<c>04.00.00.SqlDataProvider</c> L31 and L119 alter
    /// <c>aspnet_Membership_UpdateUser</c> and <c>aspnet_Membership_UpdateUserInfo</c>). The
    /// legacy property spelling is <c>LockedOut</c>, without the Is prefix
    /// (<c>UserMembership.vb:L223</c>).
    /// </remarks>
    public bool IsLockedOut { get; set; }
}
