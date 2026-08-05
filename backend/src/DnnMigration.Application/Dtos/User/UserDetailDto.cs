// -----------------------------------------------------------------------------
// UserDetailDto - the single-user detail contract for GET /api/v1/users/{userId}.
//
// PROVENANCE. Every member is derived from a measured legacy source rather than from the shape of the
// domain entity, so that no entity crosses the wire: Library/Components/Users/UserInfo.vb (14
// properties) and the membership composite it returns at L195 (15 properties, cited below as "the
// membership composite" plus the line of the member concerned); the Website/admin/Users detail,
// membership and status screens; the terminal schema in
// Website/Providers/DataProviders/SqlDataProvider; and the sentinel table in
// Library/Components/Shared/Null.vb.
//
// THIS TYPE IS INERT: no computed getter, no lazy load, no validation rule, no data access, no
// asynchronous member. Request validation lives in Application/Validation and projection from the
// domain entity in Application/Mapping/UserMappings. That separation is load bearing rather than
// stylistic - the legacy UserInfo.Roles getter opened a database connection from inside a property
// accessor (see the Roles member), and reproducing that here would put an I/O call behind every
// serialisation of this response.
//
// NO PASSWORD MATERIAL APPEARS ON THIS TYPE. The omission is deliberate and is documented at the
// foot of the class.
// -----------------------------------------------------------------------------

namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// The full detail projection of a single portal user, returned by
/// <c>GET /api/v1/users/{userId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the richest response shape in the user DTO set: the scalars of the legacy <c>Users</c>
/// table together with the externally composed membership facts, minus every piece of password
/// material. The legacy composite is <b>flattened</b> here - <c>UserInfo.Membership</c>
/// (UserInfo.vb L195) returned an object carrying fifteen further properties, so a caller needed two
/// hops to reach an approval flag. Those facts are exposed as flat sibling properties instead: the
/// wire contract is one object, and no second DTO models a composite that had no table of its own.
/// </para>
/// <para>
/// <b>Sentinel boundary.</b> The legacy data layer did not use SQL <c>NULL</c> end to end:
/// <c>Null.SetNull</c> translated every <c>DBNull</c> read into a per-type sentinel, so "absent" was
/// <c>-1</c> for integers, <c>DateTime.MinValue</c> for dates, the empty string for text and
/// <see langword="false"/> for booleans (Null.vb L36-L84). This DTO is the boundary at which those
/// sentinels become genuine CLR nullability, and each member records the translation that applies to
/// it. Two consequences are worth stating once, because they cut in opposite directions: a sentinel
/// must never be emitted to a client as though it were real data - a user who has never signed in
/// serialises as <see langword="null"/>, not as the year 1 - and, equally, a sentinel must never be
/// assumed merely because a value is negative, since <c>-1</c> is a legitimate <c>PortalID</c> in
/// this schema (see <see cref="PortalId"/>).
/// </para>
/// <para>
/// <b>Nullability and width follow the terminal database schema</b>, not the attributes decorating
/// the legacy classes; where the two disagree the schema wins and the disagreement is recorded on the
/// affected member. Widths are quoted for the benefit of the validators in Application/Validation and
/// are deliberately not encoded as attributes, because this type carries no validation of its own.
/// </para>
/// <para>
/// <b>Serialisation must be left at its defaults.</b> A "when writing null" or "when writing default"
/// ignore condition would erase a legitimate <see langword="false"/> boolean and turn a legitimate
/// empty string into an absent field, silently changing the contract the Angular client and the
/// integration tests both bind to.
/// </para>
/// </remarks>
public sealed class UserDetailDto
{
    // Identity and tenancy

    /// <summary>
    /// The surrogate key of the user, from <c>Users.UserID</c>.
    /// </summary>
    /// <remarks>
    /// Declared <c>IDENTITY(1,1) NOT NULL</c> (01.00.00.SqlDataProvider L98), so a persisted user
    /// always has a positive identifier and this member is never absent on a successful response. The
    /// legacy name is retained rather than generalised to <c>Id</c>, so a reader can line this
    /// contract up against the schema and the legacy code without a mapping table.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// The portal (tenant) whose membership this projection describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This value does not come from the user record.</b> The <c>Users</c> table has no
    /// <c>PortalID</c> column and no upgrade script in the 88-script chain ever adds one; per-portal
    /// facts live on <c>UserPortals(UserId, PortalId, Authorized)</c>
    /// (01.00.00.SqlDataProvider L153-L157), and the legacy <c>UserInfo.PortalID</c>
    /// (UserInfo.vb L219) was an in-memory field carried alongside the row. The mapper populates this
    /// member from the request route or the resolved portal context, because every user read is
    /// portal scoped.
    /// </para>
    /// <para>
    /// <b>Do not test this value for absence.</b> <c>Portals.PortalID</c> is declared
    /// <c>IDENTITY(-1, 1)</c> (01.00.00.SqlDataProvider L77), so the seed and first generated value
    /// is <c>-1</c>, while the shipped default portal row is inserted explicitly with <c>PortalID</c>
    /// <c>0</c> (L7125) - both are valid keys. The legacy sentinel for a missing integer is also
    /// <c>-1</c> and <c>Null.IsNull(-1)</c> returns <see langword="true"/>, so the same value means
    /// both "the first portal" and "no portal at all". A guard written as <c>portalId &lt;= 0</c> or
    /// <c>portalId == -1</c> rejects two real tenants. Because the value is supplied by the route or
    /// the portal context, it is always a real portal key here and the member is non-nullable.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// The user's login name, from <c>Users.Username</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>nvarchar(100) NOT NULL</c> (01.00.06.SqlDataProvider L197) and the only uniquely
    /// constrained user attribute: <c>ADD CONSTRAINT [IX_{objectQualifier}Users] UNIQUE NONCLUSTERED
    /// ([Username])</c> (03.00.09.SqlDataProvider L422). <b>Username is unique;
    /// <see cref="Email"/> is not</b> - the two must not be treated interchangeably as an account key.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy surface exposed this value twice - setting <c>UserInfo.Username</c>
    /// (L301-L312) also wrote the membership composite's own copy, which sat in a deprecated-members
    /// region - and the two are collapsed to this single member. Because the column is
    /// <c>NOT NULL</c> this member is non-nullable and defaults to the empty string, which is the
    /// legacy sentinel for absent text (Null.vb L70-L74) rather than a null the old contract never
    /// had.
    /// </para>
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    // Name and contact

    /// <summary>
    /// The user's given name, from <c>Users.FirstName</c>.
    /// </summary>
    /// <remarks>
    /// <c>nvarchar(50) NOT NULL</c>, unchanged in width and nullability from its original declaration
    /// at 01.00.00.SqlDataProvider L99 through both table rebuilds, so this member is non-nullable
    /// and defaults to the empty string. <see cref="LastName"/> reaches the same terminal shape by a
    /// different route.
    /// </remarks>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// The user's family name, from <c>Users.LastName</c>.
    /// </summary>
    /// <remarks>
    /// Terminal schema <c>nvarchar(50) NOT NULL</c> - and that is NOT the baseline nullability. The
    /// column is declared <c>NULL</c> at 01.00.00.SqlDataProvider L100, but the <c>01.00.05</c>
    /// rebuild through <c>Tmp_Users</c> re-declares it <c>NOT NULL</c> (L18) and renames the copy into
    /// place, and the <c>01.00.06</c> rebuild preserves that (L186); no later <c>ALTER COLUMN</c>
    /// touches it. Reading only the baseline is what makes this look like the one genuinely nullable
    /// name column and invites typing it as nullable; it is NOT nullable. The width agrees with the
    /// legacy <c>MaxLength(50)</c> attribute (<c>UserInfo.vb:L178</c>) and with the terminal
    /// <c>UpdateUser</c> parameter (04.00.04.SqlDataProvider L1077), so absent text here is the empty
    /// string - the legacy <c>NullString</c> sentinel - exactly as for <see cref="FirstName"/>.
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// The user's presentation name, from <c>Users.DisplayName</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>nvarchar(128) NOT NULL CONSTRAINT DF_{objectQualifier}Users_DisplayName DEFAULT ''</c>
    /// (03.02.03.SqlDataProvider L630), so this member is non-nullable and defaults to the empty
    /// string, matching the column default exactly. This is the canonical name for display: the
    /// legacy combined-name property (UserInfo.vb L375) is deliberately absent because it was marked
    /// obsolete in favour of the display name and computed itself by concatenating the first and last
    /// name inside its own getter. A DTO does not compute, so a client needing a combined name
    /// composes it client-side.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy <c>UpdateDisplayName</c> helper (UserInfo.vb L358-L367) rewrote this
    /// value by substituting the <c>[USERID]</c>, <c>[FIRSTNAME]</c>, <c>[LASTNAME]</c> and
    /// <c>[USERNAME]</c> tokens of a configured format string. Formatting a display name is a service
    /// concern and stays in the service layer; this member reports the stored result and never
    /// derives it.
    /// </para>
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The user's email address, from <c>Users.Email</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terminal schema is <c>Email nvarchar(256) NULL</c>. Unlike <see cref="Username"/> it carries
    /// <b>no unique constraint</b>, which matches the legacy membership provider being registered
    /// with <c>requiresUniqueEmail="false"</c>: two accounts may legitimately share an address.
    /// </para>
    /// <para>
    /// MIGRATION: <b>the terminal width is 256, and it is not the baseline width.</b> The column is
    /// created as <c>nvarchar(100) NOT NULL</c> (01.00.00.SqlDataProvider L107), DROPPED outright by
    /// 02.02.01.SqlDataProvider L50-51 - the statement that also removes Street, City, Region,
    /// PostalCode, Country, Password, Unit and Telephone - and re-added as <c>nvarchar(256) NULL</c>
    /// by 03.00.13.SqlDataProvider L109-110, which back-fills it from
    /// <c>dbo.aspnet_Membership.Email</c>; nothing afterwards narrows it. <b>The validators must use
    /// 256.</b> There is consequently no width collision to resolve: reading the baseline as terminal
    /// is what makes the legacy <c>MaxLength(256)</c> attribute (UserInfo.vb L121-L123) look as
    /// though it exceeded the column, and enforcing 100 would refuse addresses the store already
    /// holds, since every back-filled row came from a 256-wide source column.
    /// </para>
    /// <para>
    /// MIGRATION: the terminal column is nullable while this member is not, and that asymmetry is
    /// deliberate. Absence has always been externally observable as the empty string - the legacy
    /// property was declared required, and every legacy read passed through the sentinel translation
    /// where the null string sentinel IS <see cref="string.Empty"/> - so emitting a null here would
    /// silently change an observable value. The sentinel is preserved at this boundary while the
    /// domain entity models the column honestly as nullable. As with the username, the legacy surface
    /// exposed this value twice and the duplicate on the membership composite sat in the
    /// deprecated-members region; the two are collapsed here.
    /// </para>
    /// <para>
    /// For the validators' reference, the legacy format check applied the expression held in
    /// <c>glbEmailRegEx</c> (Library/Components/Shared/Globals.vb L132). It is named rather than
    /// encoded, because this type carries no validation.
    /// </para>
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    // Elevation and attribution

    /// <summary>
    /// Whether the user is a host-level super user, from <c>Users.IsSuperUser</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>bit NOT NULL</c> with <c>DEFAULT (0)</c> (added 01.00.02.SqlDataProvider L243; terminal
    /// form at 03.01.01.SqlDataProvider L1351 and L1353), so this member is a non-nullable
    /// <see langword="bool"/> defaulting to <see langword="false"/>. It is on this contract because
    /// the client cannot reproduce the delete affordance without it: User.ascx.vb L267 hides the
    /// delete command for the portal administrator and for a super user, and Users.ascx.vb L693
    /// enforces the same administrator rule on the list screen.
    /// </para>
    /// <para>
    /// MIGRATION: the administrator half of that rule is intentionally <b>not</b> mirrored by a
    /// convenience flag here. It compares the user against <c>PortalSettings.AdministratorId</c>,
    /// which is a property of the portal rather than of the user, and the portal DTO set already
    /// carries that identifier - so a client holding both evaluates the rule directly from
    /// <see cref="UserId"/>, and duplicating a portal fact onto a user response would add coupling
    /// without adding information. Had such a flag been surfaced it would have been populated by the
    /// mapper from the resolved portal context, never computed in a getter here.
    /// </para>
    /// </remarks>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// The affiliate the user was referred by, from <c>Users.AffiliateId</c>, or
    /// <see langword="null"/> when the user was not referred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AffiliateId int NULL</c> (02.00.00.SqlDataProvider L6958). The spelling follows the column
    /// rather than the legacy property's <c>AffiliateID</c> (UserInfo.vb L87).
    /// </para>
    /// <para>
    /// MIGRATION: the legacy representation of "not referred" was the integer sentinel <c>-1</c>,
    /// assigned in the <c>UserInfo</c> constructor (L64-L73); the mapper translates it to
    /// <see langword="null"/> so absence is expressed in the type system rather than by a magic
    /// number. That translation is safe <b>here specifically</b>, and the reason does not generalise:
    /// unlike <see cref="PortalId"/> this column is not an identity column with a negative seed, so
    /// <c>-1</c> is never a real affiliate key. The same reasoning applied to
    /// <see cref="PortalId"/> would be a defect.
    /// </para>
    /// </remarks>
    public int? AffiliateId { get; set; }

    // Membership state - flattened from the legacy UserInfo.Membership composite (UserInfo.vb L195).
    // These flags and the dates that follow are its reportable content, lifted onto this type as flat
    // siblings. No nested membership DTO exists, and this file references no legacy membership type.

    /// <summary>
    /// Whether the user's membership has been approved for the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: renamed from the legacy <c>Approved</c> (membership composite L83) to the
    /// <c>Is</c>-prefixed form used consistently by the boolean members here; the meaning is
    /// unchanged. Note the asymmetry in the legacy defaults, which is easy to invert by accident: the
    /// backing field was initialised <see langword="true"/>, whereas <see cref="IsLockedOut"/> was
    /// initialised <see langword="false"/>, so a user record started out approved and unlocked. This
    /// member stays a non-nullable <see langword="bool"/> - introducing a tri-state would diverge
    /// from a contract that was always two-valued, because the legacy sentinel for an absent boolean
    /// was simply <see langword="false"/> (Null.vb L80-L84).
    /// </para>
    /// <para>
    /// Directly load bearing for the client's action bar: Membership.ascx.vb gates two mutually
    /// exclusive commands on it (L142, L143).
    /// </para>
    /// </remarks>
    public bool IsApproved { get; set; }

    /// <summary>
    /// Whether the account is currently locked out after failed sign-in attempts.
    /// </summary>
    /// <remarks>
    /// MIGRATION: renamed from the legacy <c>LockedOut</c> (membership composite L223), whose backing
    /// field defaulted to <see langword="false"/>; non-nullable for the reason given on
    /// <see cref="IsApproved"/>. Required by the client for two distinct affordances: the unlock
    /// command, gated on it at Membership.ascx.vb L141 and cleared at L265, and the lock status
    /// indicator on the detail screen host.
    /// </remarks>
    public bool IsLockedOut { get; set; }

    /// <summary>
    /// Whether the user is currently considered online.
    /// </summary>
    /// <remarks>
    /// MIGRATION: renamed from the legacy <c>IsOnLine</c> (membership composite L123) - note the
    /// internal capital L in the original - to the conventional <c>IsOnline</c>; the meaning is
    /// unchanged. Required by the client to render the online status indicator. This is a derived,
    /// time-sensitive observation rather than a stored column, so it is composed by the repository and
    /// simply reported here, and it is non-nullable because the legacy contract had no third state.
    /// </remarks>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Whether the user must change their password before continuing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <b>renamed</b> from the legacy <c>UpdatePassword</c> (membership composite L323),
    /// backed by <c>UpdatePassword bit NOT NULL CONSTRAINT
    /// DF_{objectQualifier}Users_UpdatePassword DEFAULT 0</c> (03.02.03.SqlDataProvider L631). The
    /// legacy name reads as an instruction to perform an update while the column records a standing
    /// obligation on the account, so the new name states the state; the legacy name is recorded here
    /// to keep the column greppable from this contract. Non-nullable, defaulting to
    /// <see langword="false"/>, matching <c>NOT NULL DEFAULT 0</c>.
    /// </para>
    /// <para>
    /// Set by the administrator's force-password-change action (Membership.ascx.vb L221) and read to
    /// gate the password command (L144). The sign-in response carries the same signal, and the two
    /// must continue to mean the same thing: the account is usable, but a password change is
    /// outstanding.
    /// </para>
    /// </remarks>
    public bool MustChangePassword { get; set; }

    // Membership dates
    //
    // MIGRATION: all five were declared "As Date" on the legacy composite with no initialiser, so an
    // unset value was DateTime.MinValue rather than SQL NULL - the sentinel at Null.vb L66-L69 - and
    // Null.IsNull compares only the date component, so any instant on 0001-01-01 read as absent.
    // Every one is therefore nullable here and the mapper translates DateTime.MinValue to null.
    // Emitting 0001-01-01 to a client as though it were a real timestamp is the specific defect these
    // members exist to prevent.

    /// <summary>
    /// When the user record was created, or <see langword="null"/> if unknown.
    /// </summary>
    /// <remarks>
    /// <c>Users.CreatedDate datetime NULL</c> (01.00.00.SqlDataProvider L108), surfaced by the legacy
    /// composite as a read-only property (L103). Nullable both because the column is and because the
    /// legacy sentinel must not reach a client. This is the only creation timestamp available: the
    /// modern DotNetNuke audit quartet of created-by and modified-by columns does not exist anywhere
    /// in the 88-script chain, so no such member appears on this contract.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// When the user last signed in successfully, or <see langword="null"/> if they never have.
    /// </summary>
    /// <remarks>
    /// <c>Users.LastLoginDate datetime NULL</c> (01.00.00.SqlDataProvider L109), read-only on the
    /// legacy composite (L183). The <see langword="null"/> case is the ordinary one for a newly
    /// created account and is exactly where the legacy sentinel would otherwise surface as the year 1.
    /// </remarks>
    public DateTime? LastLoginDate { get; set; }

    /// <summary>
    /// When the user was last active, or <see langword="null"/> if never recorded.
    /// </summary>
    /// <remarks>
    /// Read-only on the legacy composite (L143). Composed externally rather than read from a
    /// <c>Users</c> column, and nullable for the same sentinel reason as the other dates.
    /// </remarks>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>
    /// When the account was last locked out, or <see langword="null"/> if it never has been.
    /// </summary>
    /// <remarks>
    /// Read-only on the legacy composite (L163). Independent of <see cref="IsLockedOut"/>: a value
    /// here records that a lockout occurred at some point, whereas <see cref="IsLockedOut"/> reports
    /// whether one is in force now, so a populated date with <see cref="IsLockedOut"/>
    /// <see langword="false"/> is ordinary history rather than a contradiction.
    /// </remarks>
    public DateTime? LastLockoutDate { get; set; }

    /// <summary>
    /// When the user's password was last changed, or <see langword="null"/> if it never has been.
    /// </summary>
    /// <remarks>
    /// Read-only on the legacy composite (L203). This member reports <b>when</b> a change occurred and
    /// carries no password material of any kind; see the note at the foot of this class. It pairs
    /// naturally with <see cref="MustChangePassword"/> for age-based prompting.
    /// </remarks>
    public DateTime? LastPasswordChangeDate { get; set; }

    // Role membership

    /// <summary>
    /// The names of the roles the user holds in the portal. Never <see langword="null"/>; an empty
    /// list means the user holds none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy <c>UserInfo.Roles</c> (L261) was typed <c>String()</c> - a mutable array
    /// of role <i>names</i>. It is exposed here as a read-only list so a caller cannot mutate a
    /// response object in place, defaulting to an empty array because the legacy representation of
    /// "no roles" was an empty set and a client should not have to null-check before iterating.
    /// </para>
    /// <para>
    /// MIGRATION: <b>the legacy getter performed database access.</b> Measured at UserInfo.vb
    /// L261-L274, it tested a private <c>_RolesHydrated</c> flag and, when unset, constructed a
    /// <c>RoleController</c> and called <c>GetRolesByUser(_UserID, PortalID)</c> from inside the
    /// property accessor - so reading the property could issue a query and serialising the object
    /// would trigger one per instance. Both the lazy getter and the hydration flag are dropped: this
    /// is a plain auto-property the mapper fills from data already loaded, which is what makes this
    /// response cheap and predictable.
    /// </para>
    /// <para>
    /// Role <i>names</i> only. This contract deliberately does not describe a role: the role DTO set
    /// owns the richer shape, including the billing and trial attributes, and redeclaring any part of
    /// it here would fork that contract.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    // -------------------------------------------------------------------------
    // DELIBERATE OMISSIONS - recorded so that a future reader does not "restore" them believing they
    // were overlooked.
    //
    // NO CREDENTIAL MATERIAL. The membership composite declared members this contract deliberately
    // does not carry: the stored credential itself (L263), the recovery answer (L283) and the recovery
    // question (L303); the stored credential digest held by the user entity is likewise absent. They
    // are cited by line rather than by name so that this file contains no credential identifier at
    // all - follow those lines in the source to see the originals.
    //   * The legacy store was reversible BY DESIGN: the membership provider was registered to
    //     encrypt rather than hash, retrieval was switched on, and the key that reversed every stored
    //     credential was itself committed to source control. The retrieval path at
    //     UserController.vb L433 has no equivalent in the target and is not carried forward to any
    //     endpoint or screen.
    //   * The recovery question is omitted even though the legacy reset screen displayed it: it is a
    //     credential-recovery secret, so it does not belong on a general-purpose detail response that
    //     any authorised reader of a user record can fetch. A reset flow that needs it is a narrow,
    //     deliberate concern for the authentication surface.
    //   * Keeping all of them off this type also keeps them out of the structured request and response
    //     logs, which must never be able to serialise credential material, and keeps this file clean
    //     for secret scanning.
    //   * The legacy plaintext credential column was nvarchar(20). That ceiling described a plaintext
    //     column, is superseded by hashed storage, and must never be carried onto a DTO as a length
    //     rule.
    //
    // NO HYDRATION OR CHANGE-TRACKING STATE. The hydration flag (membership composite L243) existed
    // only so that every setter on that class could mark the instance as populated; the
    // object-relational materialiser makes the mechanism redundant, and a change-tracking "dirty"
    // flag is likewise absent. Neither is part of a wire contract.
    //
    // NO NESTED PROFILE. The legacy UserInfo.Profile (L236) is neither embedded nor referenced. The
    // profile is its own response, served by GET .../users/{userId}/profile, and keeping the two
    // independent is what keeps this detail read cheap.
    //
    // NO OPERATION STATUS. The legacy create, delete and sign-in paths reported outcomes through
    // status enumerations and ByRef arguments. Outcome reporting is the service layer's result type,
    // which the API surface translates into a problem-details payload; neither appears on the wire.
    //
    // NO NESTED PROFILE. The legacy UserInfo.Profile (UserInfo.vb L236) is not
    // embedded and not referenced. The profile is its own response, served by
    // GET /api/v1/users/{userId}/profile, and keeping the two independent is what
    // keeps this detail read cheap.
    //
    // NO OPERATION STATUS. The legacy create, delete and sign-in paths reported
    // outcomes through status enumerations and ByRef arguments. Outcome
    // reporting is the service layer's result type, which the API surface
    // translates into a problem-details payload; neither the status
    // enumerations nor the result type appear on the wire.
    //
    // NO PAGING OR CORRELATION FIELDS. A single-item response has no page
    // metadata - the paged envelope in the common DTO set owns that - and the
    // correlation identifier travels as a response header, not as a field.
    //
    // NO PAGING OR CORRELATION FIELDS. A single-item response has no page metadata - the paged
    // envelope in the common DTO set owns that - and the correlation identifier travels as a response
    // header, not as a field.
    // -------------------------------------------------------------------------
}
