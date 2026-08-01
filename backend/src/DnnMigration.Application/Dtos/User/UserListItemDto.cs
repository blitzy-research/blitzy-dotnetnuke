namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// One row of the user administration grid, returned by <c>GET /api/v1/users</c> as the element
/// type of the <c>PagedResponse&lt;UserListItemDto&gt;</c> envelope declared in
/// <c>DnnMigration.Application.Dtos.Common</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance of the member set.</b> The members below are measured, not invented. The legacy
/// DotNetNuke 4.9 grid is declared in <c>Website/admin/Users/users.ascx</c>, whose ten data columns
/// at L40-L79 are reproduced here in their declaration order immediately after the two identity
/// members, so a consumer that renders the properties in declaration order reproduces the legacy
/// column order exactly. The authoritative server-side projection is the terminal
/// <c>vw_Users</c> view (<c>04.00.04.SqlDataProvider:L771</c>), which every paged list procedure
/// selects from.
/// </para>
/// <para>
/// <b>Every field is returned unconditionally.</b> The legacy screen resolved column visibility by
/// concatenating a literal prefix with the grid header -- <c>Users.ascx.vb:L514</c> reads
/// <c>Dim settingKey As String = "Column_" + header</c> -- against the nine module settings seeded in
/// <c>UserModuleBase.vb:L98-L123</c>: <c>Column_FirstName</c>, <c>Column_LastName</c>,
/// <c>Column_DisplayName</c>, <c>Column_Address</c>, <c>Column_Telephone</c>, <c>Column_Email</c>,
/// <c>Column_CreatedDate</c>, <c>Column_LastLogin</c> and <c>Column_Authorized</c>. Those nine keys
/// correspond one-for-one to the nine toggleable headers; <c>Username</c> has no such setting and is
/// short-circuited to always-visible by <c>Users.ascx.vb:L512</c>. Visibility is therefore a
/// presentation decision, carried by <c>MembershipSettingsDto</c> and applied by the Angular data
/// table. This contract always carries the whole row: there is deliberately no visibility flag on
/// this type and no field is omitted on the strength of a setting.
/// </para>
/// <para>
/// <b>Null and sentinel policy.</b> The legacy null contract is a sentinel table, not SQL
/// <c>NULL</c>: <c>Library/Components/Shared/Null.vb:L36-L84</c> defines <c>NullInteger</c> as -1,
/// <c>NullDate</c> as <see cref="DateTime.MinValue"/>, <c>NullBoolean</c> as <see langword="false"/>
/// and -- the consequential one -- <c>NullString</c> as the empty string rather than a null
/// reference, and <c>Null.SetNull</c> applied it on every read. This type is the boundary at which
/// those sentinels are translated: absent text surfaces as <see cref="string.Empty"/> so that the
/// legacy observable value is preserved, and an absent timestamp surfaces as <see langword="null"/>
/// so that no client is handed <c>0001-01-01</c> as though it were a real instant. Serialisation
/// must not be configured to omit nulls or defaults for this type: doing so would erase an empty
/// string, and would erase a legitimate <see langword="false"/> on
/// <see cref="IsApproved"/>, <see cref="IsOnline"/>, <see cref="IsSuperUser"/> or
/// <see cref="IsLockedOut"/>.
/// </para>
/// <para>
/// <b>Deliberately absent.</b> No password material of any kind appears here -- no password, hash,
/// salt, format, question or answer. The legacy store was reversible
/// (<c>passwordFormat="Encrypted"</c> with <c>enablePasswordRetrieval="true"</c>, and a decryption
/// key committed to source control) and exposed <c>UserController.GetPassword</c> at L433; neither
/// the store nor the retrieval path is carried forward, and the legacy 20-character password column
/// width imposes nothing on this contract. No paging member appears here either: the eight legacy
/// <c>ByRef totalRecords</c> overloads (<c>UserController.vb</c> L725, L746, L769, L793, L816, L840,
/// L864 and L889) are retired in favour of the envelope, which alone owns the item list, total
/// count, page index and page size. Also absent by design: the <c>UserCreateStatus</c> and
/// <c>UserLoginStatus</c> enums, which never travel on a response body -- a failure is expressed as a
/// result at the service boundary and translated into an RFC 7807 problem document by the API
/// layer; the correlation identifier, which travels in the <c>X-Correlation-Id</c> header; the
/// legacy <c>ObjectHydrated</c> lazy-load flag (<c>UserMembership.vb:L243</c>), which the object
/// materialiser makes redundant; the obsolete computed <c>FullName</c>
/// (<c>UserInfo.vb:L375</c>, superseded by <see cref="DisplayName"/>); <c>Cacheability</c>
/// (<c>UserInfo.vb:L484</c>), which belongs to an out-of-scope token-replacement subsystem;
/// <c>AffiliateId</c>, which the terminal <c>vw_Users</c> projects but the grid never rendered, so
/// it belongs on the detail contract; and the role set, which belongs to the detail contract and to
/// the role feature.
/// </para>
/// <para>
/// <b>This type is inert.</b> It performs no work: there is no computed member, no lazy accessor, no
/// asynchronous member, no validation attribute and no clamping setter. Composition -- most notably
/// the six-part address string -- is the mapper's responsibility, and rule enforcement is the
/// validator's. It holds no reference to a persistence type, a web-framework type or a domain
/// entity, so no entity can cross the wire through it.
/// </para>
/// </remarks>
public sealed class UserListItemDto
{
    /// <summary>
    /// Gets or sets the user's identifier, the primary key of the legacy <c>Users</c> table.
    /// </summary>
    /// <remarks>
    /// Measured as <c>[UserID] [int] IDENTITY (1, 1) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider:L98</c>), so the lowest real value is 1. Do not read a
    /// non-positive value as "absent" regardless: the legacy constructor seeded this field with
    /// <c>Null.NullInteger</c>, which is -1, and the same collision that makes
    /// <see cref="PortalId"/> hazardous applies in principle here too. Presence is decided by the
    /// envelope containing the row, never by inspecting this value.
    /// </remarks>
    public int UserId { get; set; }

    // MIGRATION: PortalId is NOT a column on the legacy Users table. The legacy UserInfo class
    // exposed a PortalID property (UserInfo.vb:L219), but the per-portal membership facts live on
    // the separate UserPortals table, which the terminal vw_Users view reaches through a LEFT OUTER
    // JOIN (04.00.04.SqlDataProvider:L771-L785, projecting UP.PortalId). The user aggregate
    // therefore carries no PortalId scalar, and this member must be populated from the request route
    // or from the resolved portal context rather than read off the user entity.
    // MIGRATION: the join is outer, so the legacy projection could yield SQL NULL for a host user
    // with no UserPortals row, which the legacy read collapsed to Null.NullInteger (-1) -- the exact
    // value that is also a real portal key. See the identity-seed note below.
    /// <summary>
    /// Gets or sets the identifier of the portal whose user list this row belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every user query is portal-scoped, so the row carries the scope it was produced under. The
    /// value originates in the request route or the resolved portal context -- never in the user
    /// record itself, which has no such column.
    /// </para>
    /// <para>
    /// <b>Identity-seed trap.</b> The portal table is declared
    /// <c>[PortalID] [int] IDENTITY (-1, 1) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>). The first portal ever created therefore has the
    /// identifier -1 and the second has 0, while the legacy sentinel for an absent integer is also
    /// -1 (<c>Null.vb:L41-L45</c> returns -1, and <c>Null.IsNull(-1)</c> returns true at
    /// <c>Null.vb:L207-L236</c>). Both values are simultaneously legitimate keys and legacy
    /// "absent" markers. Consequently <c>id &lt;= 0</c> and <c>id == -1</c> are both invalid
    /// absence tests against this member, and against any portal, role, tab or module identifier
    /// in this contract -- the role, tab and module tables are seeded
    /// <c>IDENTITY (0, 1)</c>, which makes 0 a real key as well. Absence is expressed by a nullable
    /// type or by an empty result, never by a magic number.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    // MIGRATION: two legacy properties collapse into this single wire field. UserInfo.Username
    // (UserInfo.vb:L301) mirrored every write onto UserMembership.Username (L311), and that mirror
    // target sits inside the "Deprecated Members" region of UserMembership.vb (region opens at L334,
    // property at L356) and is described in the source as a compatibility shim. The canonical field
    // is the one on the user itself, so exactly one Username is exposed here.
    /// <summary>
    /// Gets or sets the login name. Always rendered by the legacy grid, which has no visibility
    /// setting for this column.
    /// </summary>
    /// <remarks>
    /// Terminal schema is <c>Username nvarchar(100) NOT NULL</c>, introduced by the
    /// <c>01.00.06</c> table rebuild and carrying the unique constraint that <c>01.00.07:L76-L84</c>
    /// moved onto it from <c>Email</c> and that <c>03.00.09:L420-L422</c> re-asserted as
    /// <c>IX_{objectQualifier}Users UNIQUE ([Username])</c>. Uniqueness therefore belongs to the
    /// login name and <em>not</em> to <see cref="Email"/>, matching the legacy membership
    /// registration that did not require a unique email address. The legacy property was declared
    /// read-only after creation (<c>UserInfo.vb:L301</c>) and carried no maximum-length attribute;
    /// the 100-character ceiling is the schema's, and enforcing it is the validators' job, not this
    /// type's.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the given name. Legacy grid header <c>FirstName</c>, toggled by the
    /// <c>Column_FirstName</c> setting, which the legacy module seeded to hidden.
    /// </summary>
    /// <remarks>
    /// Terminal schema is <c>FirstName nvarchar(50) NOT NULL</c>, unchanged in width and nullability
    /// from the original declaration at <c>01.00.00.SqlDataProvider:L99</c> through both table
    /// rebuilds, and agreeing with the legacy <c>MaxLength(50)</c> attribute at
    /// <c>UserInfo.vb:L144</c>. Because the column is not nullable, absent text is the empty string
    /// -- the legacy <c>NullString</c> sentinel -- and never a null reference.
    /// </remarks>
    public string FirstName { get; set; } = string.Empty;

    // MIGRATION: the terminal nullability of this column is NOT the nullability of the baseline
    // script. 01.00.00.SqlDataProvider:L100 declared [LastName] [nvarchar] (50) NULL, but the
    // 01.00.05 rebuild through Tmp_Users re-declared it LastName nvarchar(50) NOT NULL and the
    // 01.00.06 rebuild preserved that, so at the terminal state FirstName and LastName are both
    // NOT NULL and the baseline asymmetry no longer exists. The cumulative terminal schema is the
    // binding authority, so this member is non-nullable and absent text is the empty string.
    /// <summary>
    /// Gets or sets the family name. Legacy grid header <c>LastName</c>, toggled by the
    /// <c>Column_LastName</c> setting, which the legacy module seeded to hidden.
    /// </summary>
    /// <remarks>
    /// Terminal schema is <c>LastName nvarchar(50) NOT NULL</c> (promoted from the baseline
    /// <c>NULL</c> by the <c>01.00.05</c> rebuild), agreeing in width with the legacy
    /// <c>MaxLength(50)</c> attribute at <c>UserInfo.vb:L178</c>. Both name members were surfaced by
    /// the legacy class through its profile accessor rather than directly off the user record, which
    /// is why the mapper may source them from either the user row or the profile projection; the
    /// wire shape is unaffected either way.
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical display name. Legacy grid header <c>DisplayName</c>, toggled by
    /// the <c>Column_DisplayName</c> setting, which the legacy module seeded to visible.
    /// </summary>
    /// <remarks>
    /// Terminal schema is
    /// <c>DisplayName nvarchar(128) NOT NULL CONSTRAINT DF_{objectQualifier}Users_DisplayName
    /// DEFAULT ''</c>, added at <c>03.02.03.SqlDataProvider:L628-L631</c> and re-applied
    /// conditionally at <c>04.00.04.SqlDataProvider:L668-L671</c>; the width agrees with the legacy
    /// <c>MaxLength(128)</c> attribute at <c>UserInfo.vb:L104</c>, and the column default is itself
    /// the empty string, which is why that is the value this member carries when unset. This is the
    /// canonical display field, superseding the obsolete computed full name. The legacy
    /// display-name format substitution -- <c>UserInfo.vb:L358-L367</c> replaced the tokens
    /// <c>[USERID]</c>, <c>[FIRSTNAME]</c>, <c>[LASTNAME]</c> and <c>[USERNAME]</c> according to a
    /// module setting -- is a service concern and is deliberately not performed here: this member
    /// carries the already-resolved value.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    // MIGRATION: Address is a denormalised profile projection, not a column. The legacy grid
    // rendered it as a template column (users.ascx:L44-L49) by calling
    // DisplayAddress(Profile.Unit, Profile.Street, Profile.City, Profile.Region, Profile.Country,
    // Profile.PostalCode) -- see Users.ascx.vb:L352 -- over six separate profile properties. The
    // corresponding user columns (Unit, Street, City, Region, PostalCode, Country) were dropped
    // outright by 02.02.01.SqlDataProvider:L50-L51 and the data moved into the profile key-value
    // store, which is why the terminal vw_Users view does not project an address at all. The mapper
    // composes this string from the profile value set; composing it in an accessor here is
    // forbidden, because this type does no work.
    /// <summary>
    /// Gets or sets the pre-composed postal address for display. Legacy grid header
    /// <c>Address</c>, toggled by the <c>Column_Address</c> setting, which the legacy module seeded
    /// to visible.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means the portal's profile definitions carry no address data for this
    /// user at all -- the honest representation of a key-value store with no matching rows. An empty
    /// string means address properties exist but compose to nothing, which is the legacy behaviour:
    /// <c>Users.ascx.vb:L353</c> seeded its local with <c>Null.NullString</c>, the empty string, and
    /// returned it when composition yielded nothing. Both states are preserved rather than
    /// conflated, so serialisation must not be configured to omit nulls for this member.
    /// </remarks>
    public string? Address { get; set; }

    // MIGRATION: Telephone is likewise a denormalised profile projection rather than a column. The
    // legacy grid rendered it as a template column (users.ascx:L50-L55) from Profile.Telephone; the
    // Telephone column that briefly existed on the user table (added by the 01.00.05 rebuild) was
    // dropped by 02.02.01.SqlDataProvider:L50-L51, so the terminal vw_Users view does not project
    // it. The mapper populates this from the profile value set.
    /// <summary>
    /// Gets or sets the telephone number for display. Legacy grid header <c>Telephone</c>, toggled
    /// by the <c>Column_Telephone</c> setting, which the legacy module seeded to visible.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means the profile carries no telephone property for this user; an
    /// empty string means the property exists and is blank. As with <see cref="Address"/>, the two
    /// states are distinct and are both preserved on the wire.
    /// </remarks>
    public string? Telephone { get; set; }

    // MIGRATION: two legacy properties collapse into this single wire field, exactly as for
    // Username. UserInfo.Email (UserInfo.vb:L123) mirrored every write onto UserMembership.Email
    // (L133), and that mirror target sits inside the "Deprecated Members" region of
    // UserMembership.vb (region opens at L334, property at L344). The legacy grid actually bound the
    // deprecated copy -- users.ascx:L58 reads Membership.Email -- but the canonical field is the one
    // on the user itself, so exactly one Email is exposed here and the shim is not reproduced.
    // MIGRATION: this column has a drop-and-recreate history that changes its width and its
    // nullability, so the baseline declaration alone is not the schema. 01.00.00:L105 declared
    // Email nvarchar(100) NOT NULL with a unique constraint; 01.00.07 moved that constraint to
    // Username; 02.02.01:L50-L51 DROPPED the column entirely when credentials moved to the external
    // membership store; and 03.00.13:L109-L110 re-added it as Email nvarchar(256) NULL, back-filled
    // from aspnet_Membership.Email. The terminal width is therefore 256 and the terminal column is
    // nullable -- which means the legacy MaxLength(256) attribute at UserInfo.vb:L121 agrees with the
    // terminal schema rather than colliding with it. The 100-character figure is the superseded
    // baseline value and must not be used as a validation ceiling.
    /// <summary>
    /// Gets or sets the email address. Legacy grid header <c>Email</c>, toggled by the
    /// <c>Column_Email</c> setting, which the legacy module seeded to hidden.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terminal schema is <c>Email nvarchar(256) NULL</c> (<c>03.00.13.SqlDataProvider:L109</c>),
    /// after the baseline <c>nvarchar(100) NOT NULL</c> was dropped by <c>02.02.01</c> and the column
    /// re-created at the wider size. Validators must use 256; the earlier 100 is history. Unlike
    /// <see cref="Username"/> this field carries <em>no</em> uniqueness guarantee -- the unique index
    /// was moved off it by <c>01.00.07.SqlDataProvider:L76-L84</c> -- so two users in the same portal
    /// may legitimately share an address, and no consumer may treat it as a key.
    /// </para>
    /// <para>
    /// Although the terminal column is nullable, this member is not: the legacy property was
    /// declared required (<c>UserInfo.vb:L121</c>) and every legacy read passed through the sentinel
    /// translation that renders an absent string as the empty string, so the externally observable
    /// value for a missing address has always been <see cref="string.Empty"/>. Preserving that is
    /// the sentinel-boundary rule for this contract; emitting a null here would be a silent change
    /// to an observable value.
    /// </para>
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    // MIGRATION: DateTime.MinValue is translated to null. The legacy sentinel for an absent date is
    // Date.MinValue (Null.vb:L66-L70) and Null.IsNull compares only the date component
    // (Null.vb:L223-L225), so legacy code observed 0001-01-01 where a modern client expects an
    // absent value. The mapper must translate the sentinel to null rather than let it serialise, so
    // that no client is ever handed 0001-01-01 as though it were a real timestamp.
    // MIGRATION: this value is not read from the user row. CreatedDate was declared on the baseline
    // Users table (01.00.00:L107) but dropped by 01.00.02:L282-L283, briefly relocated to
    // UserPortals (01.00.02:L254) and dropped from there too by 02.02.01:L54-L55, which is why the
    // terminal vw_Users view does not project it. It now originates in the externally installed
    // ASP.NET membership store: AspNetMembershipProvider.vb:L405 assigns it from the membership
    // user's CreationDate -- note the legacy rename from CreationDate to CreatedDate.
    /// <summary>
    /// Gets or sets the instant the account was created, or <see langword="null"/> when unknown.
    /// Legacy grid header <c>CreatedDate</c>, toggled by the <c>Column_CreatedDate</c> setting,
    /// which the legacy module seeded to visible.
    /// </summary>
    /// <remarks>
    /// Nullable because the underlying store permits absence and because the legacy
    /// <see cref="DateTime.MinValue"/> sentinel is translated to <see langword="null"/> at this
    /// boundary. Callers must not compare against <see cref="DateTime.MinValue"/> to detect absence;
    /// they must test for null.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    // MIGRATION: the legacy grid header for this column is "LastLogin", not "LastLoginDate" -- see
    // users.ascx:L68. That header spelling is load-bearing because it is what forms the
    // Column_LastLogin visibility key through the "Column_" + header concatenation at
    // Users.ascx.vb:L514, so the settings contract keeps the short name while this member takes the
    // longer name of the actual column and of the legacy property (UserMembership.vb:L183).
    // MIGRATION: like CreatedDate this value is not read from the user row. LastLoginDate was on the
    // baseline Users table (01.00.00:L108), dropped by 01.00.02:L282-L283, relocated to UserPortals
    // and dropped again by 02.02.01:L54-L55; it now originates in the external ASP.NET membership
    // store, assigned at AspNetMembershipProvider.vb:L408.
    // MIGRATION: DateTime.MinValue is translated to null here for the same reason as CreatedDate.
    /// <summary>
    /// Gets or sets the instant of the most recent successful sign-in, or <see langword="null"/> when
    /// the account has never signed in. Legacy grid header <c>LastLogin</c>, toggled by the
    /// <c>Column_LastLogin</c> setting, which the legacy module seeded to hidden.
    /// </summary>
    /// <remarks>
    /// A never-signed-in account is the ordinary reason for <see langword="null"/> here, and it is a
    /// materially different state from "signed in at the dawn of the calendar", which is what the
    /// untranslated legacy sentinel would have implied. Test for null, never for
    /// <see cref="DateTime.MinValue"/>.
    /// </remarks>
    public DateTime? LastLoginDate { get; set; }

    // MIGRATION: this single member reconciles four legacy spellings of one concept. The grid header
    // is "Authorized" (users.ascx:L74), which is what forms the Column_Authorized visibility key;
    // the legacy property is Approved, without the Is prefix (UserMembership.vb:L83); the baseline
    // column was UserPortals.[Authorized] (01.00.00:L156), US spelling, dropped by 02.02.01:L54-L55;
    // and the terminal column is UserPortals.Authorised, UK spelling, re-added by
    // 03.02.03:L639 as "Authorised bit NOT NULL ... DEFAULT 1" and projected by the terminal
    // vw_Users view as UP.Authorised. The target name is IsApproved, which also matches the ASP.NET
    // membership property that AspNetMembershipProvider.vb:L415 assigns from (IsApproved).
    // MIGRATION: the legacy default is true at both levels -- UserMembership.vb:L45 declares
    // "_Approved As Boolean = True" and the terminal column defaults to 1 -- whereas the default of
    // this member is false. That is deliberate: a response row is always populated by the mapper, so
    // the member default is never observed on the wire, and false is both the legacy NullBoolean
    // sentinel (Null.vb:L76-L80) and the fail-safe value. Defaulting to true here could report an
    // unapproved account as approved. The true-by-default behaviour belongs to account creation, in
    // the domain and infrastructure layers.
    /// <summary>
    /// Gets or sets a value indicating whether the account is approved for the portal -- that is,
    /// authorised to sign in. Legacy grid header <c>Authorized</c>, toggled by the
    /// <c>Column_Authorized</c> setting, which the legacy module seeded to visible.
    /// </summary>
    /// <remarks>
    /// The legacy grid rendered this as a pair of mutually exclusive images bound to
    /// <c>Membership.Approved = true</c> and <c>Membership.Approved = false</c>
    /// (<c>users.ascx:L76-L77</c>), so both states are meaningful and neither may be dropped from
    /// the payload; serialisation must not be configured to omit default values.
    /// </remarks>
    public bool IsApproved { get; set; }

    // MIGRATION: the legacy property is IsOnLine, with a capital L in the middle
    // (UserMembership.vb:L123); the target spelling is IsOnline.
    // MIGRATION: this is a computed presence flag, not a stored column and not part of the terminal
    // vw_Users projection. AspNetMembershipProvider.vb:L1139 assigns it from a presence check
    // against the UsersOnline table (created at 02.00.01.SqlDataProvider:L309). The legacy
    // scheduled job that purged that table is outside this scope, so a stale presence row is
    // possible; consumers must treat this as advisory rather than authoritative.
    /// <summary>
    /// Gets or sets a value indicating whether the user is currently considered online.
    /// </summary>
    /// <remarks>
    /// This drove the legacy grid's online indicator, which <c>Users.ascx.vb:L702</c> made visible
    /// exactly when the flag was set, in the dedicated template column declared at
    /// <c>users.ascx:L35-L39</c>. It has no <c>Column_</c> visibility setting because it is an
    /// indicator rather than a toggleable data column.
    /// </remarks>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is a host-level super user, whose reach
    /// spans every portal rather than one.
    /// </summary>
    /// <remarks>
    /// Terminal schema is <c>IsSuperUser bit NOT NULL</c> with a default of 0, introduced at
    /// <c>01.00.02.SqlDataProvider:L243</c> and finally fixed as non-nullable with a
    /// qualifier-embedding default constraint at <c>03.01.01.SqlDataProvider:L1351-L1353</c>; it is
    /// projected by the terminal <c>vw_Users</c> view. The list row genuinely needs it: the legacy
    /// delete affordance was suppressed by <c>Users.ascx.vb:L693</c> not only for the portal
    /// administrator but also when the row was the acting user and that user was a super user, so a
    /// client cannot reproduce the legacy action set without this flag. It is an advisory hint for
    /// rendering only -- every authorisation decision is taken on the server.
    /// </remarks>
    public bool IsSuperUser { get; set; }

    // MIGRATION: the legacy property is LockedOut, without the Is prefix
    // (UserMembership.vb:L223); the target spelling is IsLockedOut, which also matches the ASP.NET
    // membership property that AspNetMembershipProvider.vb:L410 assigns from (IsLockedOut).
    // MIGRATION: like the two timestamps this value originates in the externally installed ASP.NET
    // membership store rather than in the user row, and it is not part of the terminal vw_Users
    // projection. The lockout bookkeeping itself is maintained by the membership procedures that the
    // legacy upgrade scripts patch rather than create (04.00.00.SqlDataProvider:L31 and L119 alter
    // aspnet_Membership_UpdateUser and aspnet_Membership_UpdateUserInfo).
    /// <summary>
    /// Gets or sets a value indicating whether the account is under a lockout following repeated
    /// failed sign-in attempts.
    /// </summary>
    /// <remarks>
    /// Carried because the tab-hosted user management screen renders a lockout indicator alongside
    /// the online indicator, and because a list that cannot distinguish a locked account from an
    /// unapproved one loses information the legacy screens conveyed. Distinct from
    /// <see cref="IsApproved"/>: an approved account can be under a lockout, and an account that is
    /// not approved need not be.
    /// </remarks>
    public bool IsLockedOut { get; set; }
}
