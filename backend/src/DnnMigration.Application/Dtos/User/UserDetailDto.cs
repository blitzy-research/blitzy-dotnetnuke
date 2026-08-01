// -----------------------------------------------------------------------------
// UserDetailDto - the single-user detail contract for GET /api/v1/users/{id}.
//
// PROVENANCE. Every member below is derived from a measured legacy source rather
// than from the shape of the domain entity, so that no entity crosses the wire:
//
//   Library/Components/Users/UserInfo.vb                    14 properties
//   Library/Components/Users/Membership/                    15 properties
//     - the membership composite type declared there, which UserInfo.vb L195
//       returns; cited throughout below as "the membership composite" plus the
//       line number of the member concerned
//   Website/admin/Users/User.ascx(.vb)                      detail screen
//   Website/admin/Users/Membership.ascx(.vb)                action panel
//   Website/admin/Users/manageusers.ascx                    status indicators
//   Website/Providers/DataProviders/SqlDataProvider/*       terminal schema
//   Library/Components/Shared/Null.vb                       sentinel table
//
// WHAT THIS TYPE DELIBERATELY IS NOT. It is inert. There is no computed getter,
// no lazy load, no validation rule, no data access and no asynchronous member.
// Request validation lives in Application/Validation; projection from the domain
// entity lives in Application/Mapping/UserMappings. That separation is load
// bearing rather than stylistic: the legacy UserInfo.Roles getter opened a
// database connection from inside a property accessor (see the Roles member
// below for the measured detail), and reproducing that here would put an I/O
// call behind every serialisation of this response.
//
// NO PASSWORD MATERIAL APPEARS ON THIS TYPE. The omission is deliberate and is
// documented on its own at the foot of the class.
// -----------------------------------------------------------------------------

namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// The full detail projection of a single portal user, returned by
/// <c>GET /api/v1/users/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the richest response shape in the user DTO set. It carries the
/// scalars of the legacy <c>Users</c> table together with the externally
/// composed membership facts that the legacy code surfaced through the separate
/// <c>UserInfo.Membership</c> composite, minus every piece of password material.
/// </para>
/// <para>
/// The legacy composite is <b>flattened</b> here. In the source,
/// <c>UserInfo.Membership</c> (UserInfo.vb L195) returned a
/// membership composite object carrying fifteen further properties, so a caller
/// needed two hops to reach an approval flag. The membership facts are exposed
/// on this type as flat sibling properties instead: the wire contract is one
/// object, and no second DTO exists to model a composite that had no table of
/// its own.
/// </para>
/// <para>
/// <b>Sentinel boundary.</b> The legacy data layer did not use SQL
/// <c>NULL</c> end to end. <c>Null.SetNull</c> translated every
/// <c>DBNull</c> read into a per-type sentinel value, so "absent" was encoded as
/// <c>-1</c> for integers, <c>DateTime.MinValue</c> for dates,
/// the empty string for text and <see langword="false"/> for booleans
/// (Null.vb L36-L84). This DTO is the boundary at which those sentinels are
/// translated into genuine CLR nullability, and each member below records the
/// translation that applies to it. Two consequences are worth stating once:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     A sentinel must never be emitted to a client as though it were real data.
///     A user who has never signed in has no last-login instant, so that member
///     serialises as <see langword="null"/> and not as the year 1.
///     </description>
///   </item>
///   <item>
///     <description>
///     The reverse error is equally real. <c>-1</c> is a legitimate
///     <c>PortalID</c> in this schema, so a sentinel cannot be assumed
///     merely because a value is negative. See <see cref="PortalId"/>.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Nullability and width follow the terminal database schema</b>, not the
/// attributes decorating the legacy classes. Where the two disagree the schema
/// wins, and the disagreement is recorded on the affected member. The widths
/// quoted throughout are stated for the benefit of the validators in
/// Application/Validation; they are deliberately not encoded as attributes here,
/// because this type carries no validation of its own.
/// </para>
/// <para>
/// Serialisation must be left at its defaults. Applying a
/// "when writing null" or "when writing default" ignore condition to this type
/// would erase a legitimate <see langword="false"/> boolean and would turn a
/// legitimate empty string into an absent field, silently changing the contract
/// that the Angular client and the integration tests both bind to.
/// </para>
/// </remarks>
public sealed class UserDetailDto
{
    // -------------------------------------------------------------------------
    // Identity and tenancy
    // -------------------------------------------------------------------------

    /// <summary>
    /// The surrogate key of the user, from <c>Users.UserID</c>.
    /// </summary>
    /// <remarks>
    /// Declared <c>IDENTITY(1,1) NOT NULL</c>
    /// (01.00.00.SqlDataProvider L98), so a persisted user always has a
    /// positive identifier and this member is never absent on a successful
    /// response. The legacy name is retained rather than generalised to
    /// <c>Id</c>, so that a reader can line this contract up against the
    /// schema and the legacy code without a mapping table.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// The portal (tenant) whose membership this projection describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This value does not come from the user record.</b> The
    /// <c>Users</c> table has no <c>PortalID</c> column and no
    /// upgrade script in the 88-script chain ever adds one; per-portal facts
    /// live on <c>UserPortals(UserId, PortalId, Authorized)</c>
    /// (01.00.00.SqlDataProvider L153-L157). The legacy
    /// <c>UserInfo.PortalID</c> (UserInfo.vb L219) was an in-memory field
    /// carried alongside the row, not a persisted column. The mapper therefore
    /// populates this member from the request route or the resolved portal
    /// context, because every user read is portal scoped.
    /// </para>
    /// <para>
    /// <b>Do not test this value for absence.</b> <c>Portals.PortalID</c>
    /// is declared <c>IDENTITY(-1, 1)</c>
    /// (01.00.00.SqlDataProvider L77), so the first portal ever created has the
    /// identifier <c>-1</c> and the second has <c>0</c>. The
    /// legacy sentinel for a missing integer is also <c>-1</c>
    /// (Null.vb L41-L45), and <c>Null.IsNull(-1)</c> returns
    /// <see langword="true"/> (Null.vb L207-L235) - the same bit pattern means
    /// both "the first portal" and "no portal at all". A guard written as
    /// <c>portalId &lt;= 0</c> or <c>portalId == -1</c> will
    /// therefore reject two real tenants. Because the value is supplied by the
    /// route or the portal context, it is always a real portal key here and the
    /// member is non-nullable.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// The user's login name, from <c>Users.Username</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>nvarchar(100) NOT NULL</c> (01.00.06.SqlDataProvider L197) and
    /// the only uniquely constrained user attribute:
    /// <c>ADD CONSTRAINT [IX_{objectQualifier}Users] UNIQUE NONCLUSTERED
    /// ([Username])</c> (03.00.09.SqlDataProvider L422). <b>Username is
    /// unique; <see cref="Email"/> is not</b> - the two must not be treated
    /// interchangeably as an account key.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy surface exposed this value twice. Setting
    /// <c>UserInfo.Username</c> (UserInfo.vb L301-L312) also wrote
    /// <c>Me.Membership.Username</c>, and the duplicate at
    /// the membership composite L356 sat inside a
    /// <c>#Region "Deprecated Members"</c> block. The two are collapsed to
    /// this single member; there is exactly one username on this contract.
    /// </para>
    /// <para>
    /// Because the column is <c>NOT NULL</c>, this member is
    /// non-nullable and defaults to the empty string. The legacy sentinel for
    /// absent text was itself the empty string rather than
    /// <see langword="null"/> (Null.vb L70-L74), so that default preserves the
    /// legacy meaning instead of inventing a null the old contract never had.
    /// </para>
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Name and contact
    // -------------------------------------------------------------------------

    /// <summary>
    /// The user's given name, from <c>Users.FirstName</c>.
    /// </summary>
    /// <remarks>
    /// <c>nvarchar(50) NOT NULL</c> (01.00.00.SqlDataProvider L99), so
    /// this member is non-nullable and defaults to the empty string. Note the
    /// deliberate asymmetry with <see cref="LastName"/>, which the same
    /// original statement declares nullable; the difference is real schema and
    /// is preserved rather than tidied away.
    /// </remarks>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// The user's family name, from <c>Users.LastName</c>, or
    /// <see langword="null"/> when the record carries none.
    /// </summary>
    /// <remarks>
    /// <c>nvarchar(50) NULL</c> (01.00.00.SqlDataProvider L100). This is
    /// the one genuinely nullable name column: <see cref="FirstName"/> is
    /// declared <c>NOT NULL</c> in the very same
    /// <c>CREATE TABLE</c> statement. Both legacy classes decorated the
    /// pair identically, which is exactly why the schema rather than the
    /// attributes is the authority here.
    /// </remarks>
    public string? LastName { get; set; }

    /// <summary>
    /// The user's presentation name, from <c>Users.DisplayName</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>nvarchar(128) NOT NULL CONSTRAINT
    /// DF_{objectQualifier}Users_DisplayName DEFAULT ''</c>
    /// (03.02.03.SqlDataProvider L630), so this member is non-nullable and
    /// defaults to the empty string, matching the column default exactly.
    /// </para>
    /// <para>
    /// This is the canonical name for display. The legacy
    /// the legacy combined-name property at UserInfo.vb L375 is deliberately
    /// absent from this contract: it was marked
    /// <c>Obsolete("This property has been deprecated in favour of Display
    /// Name")</c>, it sat in the deprecated-members region, and it computed
    /// itself by concatenating the first and last name inside its own getter.
    /// A DTO does not compute, so no equivalent is offered and callers use this
    /// member. A client needing a combined name composes it client-side.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy <c>UpdateDisplayName</c> helper
    /// (UserInfo.vb L358-L367) rewrote this value by substituting the
    /// <c>[USERID]</c>, <c>[FIRSTNAME]</c>,
    /// <c>[LASTNAME]</c> and <c>[USERNAME]</c> tokens of a
    /// configured format string. Formatting a display name is a service
    /// concern and stays in the service layer; this member reports the stored
    /// result and never derives it.
    /// </para>
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The user's email address, from <c>Users.Email</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>nvarchar(100) NOT NULL</c> (01.00.00.SqlDataProvider L107), so
    /// this member is non-nullable and defaults to the empty string. Unlike
    /// <see cref="Username"/> it carries <b>no unique constraint</b>, which
    /// matches the legacy membership provider being registered with
    /// <c>requiresUniqueEmail="false"</c>: two accounts may legitimately
    /// share an address.
    /// </para>
    /// <para>
    /// MIGRATION: <b>a width collision resolved in favour of the schema.</b>
    /// The legacy class decorated this property with
    /// <c>MaxLength(256)</c> (UserInfo.vb L121-L123) while the column has
    /// only ever been <c>nvarchar(100)</c>. The attribute drove a
    /// presentation-layer property editor, not the database, so the effective
    /// legacy limit was always 100 and anything longer would have failed on
    /// write. <b>The validators must use 100, not 256.</b>
    /// </para>
    /// <para>
    /// MIGRATION: as with the username, the legacy surface exposed this value
    /// twice - <c>UserInfo.Email</c> (UserInfo.vb L123-L134) also wrote
    /// <c>Me.Membership.Email</c>, whose duplicate at
    /// the membership composite L344 sat in the deprecated-members region. The two are
    /// collapsed to this single member.
    /// </para>
    /// <para>
    /// For the validators' reference, the legacy format check applied the
    /// expression held in <c>glbEmailRegEx</c>
    /// (Library/Components/Shared/Globals.vb L132). It is named here rather
    /// than encoded, because this type carries no validation.
    /// </para>
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Elevation and attribution
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether the user is a host-level super user, from
    /// <c>Users.IsSuperUser</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>bit NOT NULL</c> with <c>DEFAULT (0)</c>
    /// (added 01.00.02.SqlDataProvider L243; terminal form at
    /// 03.01.01.SqlDataProvider L1351 and L1353), so this member is a
    /// non-nullable <see langword="bool"/> defaulting to
    /// <see langword="false"/>.
    /// </para>
    /// <para>
    /// The member is on this contract because the client cannot reproduce the
    /// delete affordance without it. User.ascx.vb L267 reads
    /// <c>cmdDelete.Visible = Not (User.UserID =
    /// PortalSettings.AdministratorId) AndAlso Not (IsUser And
    /// User.IsSuperUser)</c>, and Users.ascx.vb L693 enforces the same
    /// administrator rule on the list screen.
    /// </para>
    /// <para>
    /// MIGRATION: the administrator half of that rule is intentionally
    /// <b>not</b> mirrored by a convenience flag on this type. It compares the
    /// user against <c>PortalSettings.AdministratorId</c>, which is a
    /// property of the portal and not of the user, and the portal DTO set
    /// already carries the administrator identifier. A client holding both
    /// evaluates the rule directly from <see cref="UserId"/> and the portal's
    /// administrator identifier, so duplicating a portal fact onto a user
    /// response would add coupling without adding information. Had such a flag
    /// been surfaced it would have been populated by the mapper from the
    /// resolved portal context - never computed in a getter here.
    /// </para>
    /// </remarks>
    public bool IsSuperUser { get; set; }

    /// <summary>
    /// The affiliate the user was referred by, from
    /// <c>Users.AffiliateId</c>, or <see langword="null"/> when the user
    /// was not referred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AffiliateId int NULL</c> (02.00.00.SqlDataProvider L6958).
    /// The spelling follows the column, <c>AffiliateId</c>, rather than
    /// the legacy property's <c>AffiliateID</c> (UserInfo.vb L87).
    /// </para>
    /// <para>
    /// MIGRATION: the legacy representation of "not referred" was the integer
    /// sentinel <c>-1</c>, assigned in the <c>UserInfo</c>
    /// constructor as <c>_AffiliateID = Null.NullInteger</c>
    /// (UserInfo.vb L64-L73). The mapper translates that sentinel to
    /// <see langword="null"/> so the absence is expressed in the type system
    /// rather than by a magic number.
    /// </para>
    /// <para>
    /// That translation is safe <b>here specifically</b>, and the reason is
    /// worth stating because it does not generalise: unlike
    /// <see cref="PortalId"/>, this column is not an identity column with a
    /// negative seed, so <c>-1</c> is never a real affiliate key and
    /// collapsing it to <see langword="null"/> cannot destroy a legitimate
    /// value. The same reasoning applied to <see cref="PortalId"/> would be a
    /// defect.
    /// </para>
    /// </remarks>
    public int? AffiliateId { get; set; }

    // -------------------------------------------------------------------------
    // Membership state - flattened from the legacy UserInfo.Membership composite
    //
    // MIGRATION: UserInfo.Membership (UserInfo.vb L195) returned a
    // membership composite object. These flags and the dates that follow are its
    // reportable content, lifted onto this type as flat siblings. No nested
    // membership DTO exists, and this file references no legacy membership type.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether the user's membership has been approved for the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: renamed from the legacy <c>Approved</c>
    /// (membership composite L83) to the <c>Is</c>-prefixed form used
    /// consistently by the boolean members of this contract. The meaning is
    /// unchanged.
    /// </para>
    /// <para>
    /// Note the asymmetry in the legacy defaults, which is easy to invert by
    /// accident: the backing field was initialised
    /// <c>Private _Approved As Boolean = True</c>, whereas
    /// <see cref="IsLockedOut"/> was initialised
    /// <see langword="false"/>. A user record therefore started out approved
    /// and unlocked. This member stays a non-nullable
    /// <see langword="bool"/>; introducing a tri-state would be a divergence
    /// from a contract that was always two-valued, because the legacy sentinel
    /// for an absent boolean was simply <see langword="false"/>
    /// (Null.vb L80-L84).
    /// </para>
    /// <para>
    /// Directly load bearing for the client's action bar. Membership.ascx.vb
    /// gates two mutually exclusive commands on it:
    /// <c>cmdUnAuthorize.Visible = Membership.Approved</c> (L142) and
    /// <c>cmdAuthorize.Visible = Not Membership.Approved</c> (L143).
    /// </para>
    /// </remarks>
    public bool IsApproved { get; set; }

    /// <summary>
    /// Whether the account is currently locked out after failed sign-in
    /// attempts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: renamed from the legacy <c>LockedOut</c>
    /// (membership composite L223), whose backing field defaulted to
    /// <see langword="false"/>. Non-nullable <see langword="bool"/>, for the
    /// reason given on <see cref="IsApproved"/>.
    /// </para>
    /// <para>
    /// Required by the client for two distinct affordances: the unlock command,
    /// gated by <c>cmdUnLock.Visible = Membership.LockedOut</c>
    /// (Membership.ascx.vb L141) and cleared by
    /// <c>LockedOut = False</c> (L265); and the lock status indicator
    /// <c>imgLockedOut</c> rendered by the detail screen host
    /// (manageusers.ascx L53).
    /// </para>
    /// </remarks>
    public bool IsLockedOut { get; set; }

    /// <summary>
    /// Whether the user is currently considered online.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: renamed from the legacy <c>IsOnLine</c>
    /// (membership composite L123) - note the internal capital L in the original
    /// - to the conventional <c>IsOnline</c>. The meaning is unchanged.
    /// </para>
    /// <para>
    /// Required by the client to render the <c>imgOnline</c> status
    /// indicator (manageusers.ascx L54). This is a derived, time-sensitive
    /// observation rather than a stored column, so it is composed by the
    /// repository and simply reported here; it is a non-nullable
    /// <see langword="bool"/> because the legacy contract had no third state.
    /// </para>
    /// </remarks>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Whether the user must change their password before continuing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <b>renamed</b> from the legacy <c>UpdatePassword</c>
    /// (membership composite L323), backed by
    /// <c>UpdatePassword bit NOT NULL CONSTRAINT
    /// DF_{objectQualifier}Users_UpdatePassword DEFAULT 0</c>
    /// (03.02.03.SqlDataProvider L631). The legacy name reads as an
    /// instruction to perform an update, while the column records a standing
    /// obligation on the account; the new name states the state. The legacy
    /// name is recorded here so the column remains greppable from this
    /// contract.
    /// </para>
    /// <para>
    /// Non-nullable <see langword="bool"/> defaulting to
    /// <see langword="false"/>, matching <c>NOT NULL DEFAULT 0</c>.
    /// </para>
    /// <para>
    /// Set by the administrator's force-password-change action
    /// (<c>UpdatePassword = True</c>, Membership.ascx.vb L221) and read
    /// to gate the password command
    /// (<c>cmdPassword.Visible = Not Membership.UpdatePassword</c>, L144).
    /// The sign-in response carries the same signal, and the two must continue
    /// to mean the same thing: the account is usable, but a password change is
    /// outstanding.
    /// </para>
    /// </remarks>
    public bool MustChangePassword { get; set; }

    // -------------------------------------------------------------------------
    // Membership dates
    //
    // MIGRATION: all five were declared "As Date" on the legacy composite with
    // no initialiser, so an unset value was DateTime.MinValue rather than SQL
    // NULL - the sentinel defined at Null.vb L66-L69. Null.IsNull compares only
    // the date component (Null.vb L207-L235), so any instant on 0001-01-01 read
    // as absent. Every one is therefore nullable here and the mapper translates
    // DateTime.MinValue to null. Emitting 0001-01-01 to a client as though it
    // were a real timestamp is the specific defect these members exist to
    // prevent.
    // -------------------------------------------------------------------------

    /// <summary>
    /// When the user record was created, or <see langword="null"/> if unknown.
    /// </summary>
    /// <remarks>
    /// <c>Users.CreatedDate datetime NULL</c>
    /// (01.00.00.SqlDataProvider L108), surfaced by the legacy composite as a
    /// read-only property (membership composite L103). Nullable both because the
    /// column is nullable and because the legacy sentinel
    /// <see cref="DateTime.MinValue"/> must not reach a client. This is the
    /// only creation timestamp available: the modern DotNetNuke audit quartet
    /// of created-by and modified-by columns does not exist anywhere in the
    /// 88-script chain, so no such member appears on this contract.
    /// </remarks>
    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// When the user last signed in successfully, or <see langword="null"/> if
    /// they never have.
    /// </summary>
    /// <remarks>
    /// <c>Users.LastLoginDate datetime NULL</c>
    /// (01.00.00.SqlDataProvider L109), read-only on the legacy composite
    /// (membership composite L183). The <see langword="null"/> case is the
    /// ordinary one for a newly created account and is exactly where the
    /// legacy sentinel would otherwise surface as the year 1.
    /// </remarks>
    public DateTime? LastLoginDate { get; set; }

    /// <summary>
    /// When the user was last active, or <see langword="null"/> if never
    /// recorded.
    /// </summary>
    /// <remarks>
    /// Read-only on the legacy composite (membership composite L143). Composed
    /// externally rather than read from a <c>Users</c> column, and
    /// nullable for the same sentinel reason as the other dates.
    /// </remarks>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>
    /// When the account was last locked out, or <see langword="null"/> if it
    /// never has been.
    /// </summary>
    /// <remarks>
    /// Read-only on the legacy composite (membership composite L163). Independent
    /// of <see cref="IsLockedOut"/>: a value here records that a lockout
    /// occurred at some point, whereas <see cref="IsLockedOut"/> reports
    /// whether one is in force now. A populated date with
    /// <see cref="IsLockedOut"/> <see langword="false"/> is an ordinary
    /// history, not a contradiction.
    /// </remarks>
    public DateTime? LastLockoutDate { get; set; }

    /// <summary>
    /// When the user's password was last changed, or <see langword="null"/> if
    /// it never has been.
    /// </summary>
    /// <remarks>
    /// Read-only on the legacy composite (membership composite L203). This member
    /// reports <b>when</b> a change occurred and carries no password material
    /// of any kind; see the note at the foot of this class. It pairs naturally
    /// with <see cref="MustChangePassword"/> for age-based prompting.
    /// </remarks>
    public DateTime? LastPasswordChangeDate { get; set; }

    // -------------------------------------------------------------------------
    // Role membership
    // -------------------------------------------------------------------------

    /// <summary>
    /// The names of the roles the user holds in the portal. Never
    /// <see langword="null"/>; an empty list means the user holds none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy <c>UserInfo.Roles</c> (UserInfo.vb L261) was
    /// typed <c>String()</c> - a mutable array of role <i>names</i>. It is
    /// exposed here as a read-only list so a caller cannot mutate a response
    /// object in place, and so the contract states its intent rather than
    /// merely its storage.
    /// </para>
    /// <para>
    /// MIGRATION: <b>the legacy getter performed database access.</b> Measured
    /// at UserInfo.vb L261-L274, it tested a private
    /// <c>_RolesHydrated</c> flag and, when unset, constructed a
    /// <c>RoleController</c> and called
    /// <c>GetRolesByUser(_UserID, PortalID)</c> from inside the property
    /// accessor. Reading the property could therefore issue a query, and
    /// serialising the object would trigger one per instance. Both the lazy
    /// getter and the hydration flag are dropped: this is a plain
    /// auto-property that the mapper fills from data already loaded, which is
    /// what makes this response cheap and predictable.
    /// </para>
    /// <para>
    /// The default is an empty array rather than <see langword="null"/>,
    /// because the legacy representation of "no roles" was an empty set and a
    /// client should not have to null-check before iterating.
    /// </para>
    /// <para>
    /// Role <i>names</i> only. This contract deliberately does not describe a
    /// role: the role DTO set owns the richer shape, including the billing and
    /// trial attributes, and redeclaring any part of it here would fork that
    /// contract.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    // -------------------------------------------------------------------------
    // DELIBERATE OMISSIONS - recorded so that a future reader does not "restore"
    // them believing they were overlooked.
    //
    // NO CREDENTIAL MATERIAL. The membership composite declared four members
    // this contract deliberately does not carry: the stored credential itself
    // (L263), the recovery answer (L283) and the recovery question (L303); the
    // stored credential digest held by the user entity is likewise absent. The
    // members are cited by line rather than by name so that this file contains
    // no credential identifier at all - follow those lines in the source to see
    // the originals.
    //
    //   * The legacy store was reversible by design - the membership provider
    //     was registered to encrypt rather than hash, retrieval was switched
    //     on, and the key that reversed every stored credential was itself
    //     committed to source control. The retrieval path at
    //     UserController.vb L433 has no equivalent in the target and is not
    //     carried forward to any endpoint or screen.
    //   * The recovery question is omitted even though the legacy reset screen
    //     displayed it. It is a credential-recovery secret, so it does not
    //     belong on a general-purpose detail response that any authorised
    //     reader of a user record can fetch. A reset flow that needs it is a
    //     narrow, deliberate concern for the authentication surface to solve.
    //   * Keeping all four off this type also keeps them out of the structured
    //     request and response logs, which must never be able to serialise
    //     credential material, and keeps this file clean for secret scanning.
    //
    // The legacy plaintext credential column was nvarchar(20). That
    // 20-character ceiling described a plaintext column and is superseded by
    // hashed storage; it must never be carried onto a DTO as a length rule.
    //
    // NO HYDRATION OR CHANGE-TRACKING STATE. The hydration flag at the
    // membership composite L243 existed only so that every setter on that class
    // could mark the instance as populated - each one ended by setting it. The
    // object-relational materialiser makes the whole mechanism redundant, and a
    // change-tracking "dirty" flag is likewise absent: neither is part of a wire
    // contract, and both are workarounds the target platform replaces outright.
    //
    // NO NESTED PROFILE. The legacy UserInfo.Profile (UserInfo.vb L236) is not
    // embedded and not referenced. The profile is its own response, served by
    // GET /api/v1/users/{id}/profile, and keeping the two independent is what
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
    // -------------------------------------------------------------------------
}
