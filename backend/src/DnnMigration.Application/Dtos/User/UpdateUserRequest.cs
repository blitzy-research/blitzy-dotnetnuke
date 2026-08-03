namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for <c>PUT /api/v1/portals/{portalId}/users/{userId}</c>, carrying the complete set of fields an
/// administrator may change on an existing DotNetNuke user account.
/// </summary>
/// <remarks>
/// <para>
/// How this member set was derived. It is not a design preference; it is the intersection of two
/// independent legacy authorities that were each measured and found to agree exactly.
/// </para>
/// <list type="number">
///   <item><description>
///   The edit screen. <c>Website/admin/Users/User.ascx</c> renders one property editor declared
///   with <c>sortmode="SortOrderAttribute"</c> and <c>editmode="Edit"</c>, bound at
///   <c>User.ascx.vb:L293</c> to a <c>UserInfo</c> instance. Its rendered field set is therefore
///   precisely the five <c>SortOrder</c>-attributed properties of that class: <c>Username</c> at
///   ordinal 0, <c>FirstName</c> at 1, <c>LastName</c> at 2, <c>DisplayName</c> at 3 and
///   <c>Email</c> at 4. Every other property of <c>UserInfo</c> carries <c>Browsable(False)</c> and
///   so renders nowhere on that editor. The remaining inputs on the page sit inside the sibling
///   panel <c>pnlAddUser</c>, which <c>User.ascx.vb:L276</c> makes visible only when adding a user,
///   so they are creation-time concerns and are absent here.
///   </description></item>
///   <item><description>
///   The terminal stored procedure. Nine <c>UpdateUser</c> definitions exist across the 88 upgrade
///   scripts because that chain is destructive, so only the last one describes the live database.
///   At <c>Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider:L1072</c> it
///   assigns exactly <c>FirstName</c>, <c>LastName</c>, <c>Email</c> and <c>DisplayName</c> on the
///   <c>Users</c> row, and takes the identifiers only as predicates for its two WHERE clauses.
///   </description></item>
/// </list>
/// <para>
/// Full-replacement semantics. This type expresses HTTP PUT, which replaces the addressed
/// resource rather than patching it. No member carries an "unspecified" marker, so a field the
/// caller omits deserialises to its default and overwrites the stored value; an omitted
/// <c>FirstName</c> therefore lands as the empty string, not as "leave unchanged". Callers must
/// send the whole field set. The Angular editor does exactly that: its typed reactive form
/// declares every control with an explicit non-nullable initial value and patches them from the
/// current <c>UserDetailDto</c> before the user edits anything. A partial-update shape was
/// deliberately not introduced, and neither was any optional-marker wrapper type or
/// patch-document type, because a second inbound contract for one endpoint is a larger change
/// than the problem warrants and none of the three exists in the legacy application.
/// </para>
/// <para>
/// Sentinel boundary. The legacy code represents absence with sentinel values rather than with
/// null: <c>Library/Components/Shared/Null.vb</c> returns <c>-1</c> for its integer sentinel at
/// L43 and, decisively for this type, returns the empty string rather than null for its string
/// sentinel at L73. Those helpers fire on every legacy read. The consequence honoured here is
/// that the empty string is the legacy notion of an absent string, so no member on this request
/// converts between empty and null in either direction, and serialisation is never configured to
/// skip null or default values on the way past. Doing so would erase legitimately empty values
/// and break round-trip fidelity for the integration suite that posts this shape.
/// </para>
/// <para>
/// Where the schema disagrees with the legacy class, the schema wins -- but the schema means the
/// CUMULATIVE TERMINAL schema, obtained by replaying all 88 upgrade scripts, and not the baseline
/// script read on its own. Read against the baseline alone there appear to be three disagreements;
/// replaying the chain dissolves two of them and leaves one.
/// <list type="bullet">
/// <item>
/// <description>
/// <c>Email</c>: not a disagreement. <c>UserInfo.vb:L121</c> declares <c>MaxLength(256)</c>, and
/// the terminal column is <c>nvarchar(256) NULL</c> -- the baseline's 100 was dropped outright at
/// <c>02.02.01:L50-51</c> and re-added at 256 by <c>03.00.13:L109-110</c>. Both agree at 256.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>LastName</c>: not a disagreement. <c>UserInfo.vb:L178</c> declares <c>Required(True)</c>,
/// and the terminal column is <c>NOT NULL</c> -- the baseline's <c>NULL</c> was promoted by the
/// <c>01.00.05</c> rebuild (<c>L18</c>) and held by the <c>01.00.06</c> rebuild (<c>L186</c>).
/// Both agree that a value is required.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>DisplayName</c>: a real disagreement, and the schema governs. The terminal
/// <c>UpdateUser</c> procedure declares <c>@DisplayName nvarchar(100)</c> against a column of
/// <c>nvarchar(128)</c>, which independently confirms that a declaration upstream of the table is
/// not evidence about the table. The bound is the column's 128.
/// </description>
/// </item>
/// </list>
/// <c>FirstName</c> shows no disagreement at any point in the chain.
/// </para>
/// <para>
/// Validation lives elsewhere. This type is an inert transport shape and deliberately carries no
/// attribute, no rule, no self-check and no normalising or trimming accessor, so nothing here can
/// silently rewrite what the caller sent. Every rule belongs in
/// <c>Application/Validation/UpdateUserRequestValidator.cs</c>. The measured legacy rules that
/// author needs are recorded on each member below, plus two whole-request facts. Email
/// uniqueness must not be enforced: the only uniqueness constraint on the table is
/// <c>IX_Users UNIQUE NONCLUSTERED (Username)</c>, added at
/// <c>03.00.09.SqlDataProvider:L422</c>, and the membership provider is registered with
/// <c>requiresUniqueEmail="false"</c> at <c>Website/release.config:L244</c>; tightening that
/// during a migration would reject accounts that are valid today. And the email pattern is not a
/// fixed constant at runtime, because <c>User.ascx.vb:L408</c> replaces the editor's expression
/// with the portal's <c>Security_EmailValidation</c> setting whenever that setting is present.
/// </para>
/// <para>
/// Logging. Unlike <c>CreateUserRequest</c> and <c>ChangePasswordRequest</c>, this request
/// carries no credential material of any kind, so an instance of it is safe to write to a
/// structured request log in full. That property is a reason to keep the type this narrow rather
/// than a happy accident.
/// </para>
/// <para>
/// Identifiers never appear in this body, and the reason generalises. The addressed user comes
/// from the route and the tenant comes from the resolved portal context; the legacy signature
/// <c>UserController.vb:L963</c> already separates them from the payload, taking
/// <c>portalId</c> as its own argument alongside the user object. Anything downstream that tests
/// an identifier for absence must not treat a non-positive value as absent: the identity seeds in
/// this schema are deliberately low or negative, with <c>PortalID</c> seeded at <c>-1</c>
/// (<c>01.00.00.SqlDataProvider:L77</c>) and <c>RoleID</c>, <c>TabID</c> and <c>ModuleID</c> all
/// seeded at 0, while the legacy integer sentinel is itself <c>-1</c>. A real portal can therefore
/// hold the same integer the legacy code used to mean "nothing".
/// </para>
/// </remarks>
// MIGRATION: Username is absent by measurement, not by omission. UserInfo.vb:L301 declares it
// IsReadOnly(True), which is the legacy signal that the property editor renders it read-only once
// the record exists, and the schema agrees: it is the unique natural key
// (IX_Users UNIQUE NONCLUSTERED (Username), 03.00.09.SqlDataProvider:L422) and the ASP.NET
// membership store keys on it as well. The terminal UpdateUser procedure never assigns it.
// Renaming a user is simply not an operation the legacy application offers, so exposing one here
// would add behaviour rather than preserve it. This makes UpdateUserRequest intentionally
// asymmetric with CreateUserRequest, which does carry Username because the terminal AddUser
// procedure inserts it. That asymmetry is correct and measured; it is not an oversight to repair.
//
// MIGRATION: The user identifier is absent because PUT /api/v1/portals/{portalId}/users/{userId} already carries it in
// the path. Accepting it in the body too would create two sources of truth for one value and
// invite the mass-assignment defect where they disagree. UsersController reads it from the route
// and passes it to IUserService as a separate argument.
//
// MIGRATION: The portal identifier is absent for a stronger reason than convention. The Users
// table has no portal column at all; per-portal facts live on UserPortals, which is why the
// target model splits UserPortal into its own entity, and the terminal UpdateUser procedure uses
// its @PortalId parameter solely to scope the UserPortals update. Accepting a portal identifier
// in an inbound body would let a caller move a user between tenants, defeating the tenant
// isolation the migration is required to preserve. The value comes from the route or from
// IPortalContext.
//
// MIGRATION: AffiliateId is absent, resolving the open question of whether to surface it against
// four measured facts. It carries Browsable(False) at UserInfo.vb:L87 so it never rendered on the
// edit screen; the terminal UpdateUser procedure does not accept it; no UpdateUser definition in
// any of the 88 scripts mentions it; and the terminal AddUser procedure
// (04.00.04.SqlDataProvider:L696) both accepts and inserts it. It is therefore a create-time
// column. The delta between the terminal AddUser and UpdateUser parameter lists is exactly
// Username, AffiliateId and IsSuperUser, and all three are excluded here on the same reasoning.
// The translation of its -1 sentinel to null accordingly belongs to CreateUserRequest, which is
// the contract that actually writes it. Note that the translation is safe there precisely because
// AffiliateId is not an identity column with a negative seed, unlike the portal identifier.
//
// MIGRATION: No credential material appears here, inbound or outbound - no secret, no verifier,
// no hash, no salt, no storage-format discriminator and no recovery question or answer. This
// mirrors the legacy design rather than trimming it: credential changes had their own screen,
// Website/admin/Users/Password.ascx, and they get their own request in ChangePasswordRequest,
// bound to POST /api/v1/portals/{portalId}/users/{userId}/password. Folding credential mutation into a general profile
// update would widen the attack surface for no functional gain. The legacy store was reversible,
// registered with an encrypted format and retrieval enabled and decrypted by a key committed to
// source control, and retrieval is deliberately not carried forward to any endpoint or screen.
// The legacy 20-character column ceiling is likewise not carried onto any target contract.
//
// MIGRATION: The membership flags - approved, lockout and force-change-on-next-signin - are
// absent because the legacy application models them as state transitions rather than as editable
// fields, and each transition has its own precondition and its own affordance.
// Website/admin/Users/Membership.ascx exposes four discrete command buttons, and
// Membership.ascx.vb gates each one on current state at L141 through L144 before mutating exactly
// one flag per postback: approve at L199, force a credential change at L221, revoke approval at
// L243, and clear a lockout at L265, that last one only after the unlock itself succeeds. Folding
// them into a general PUT would discard those preconditions and let one request silently
// reinstate a locked account while editing a display name. They are therefore expressed as
// route-only sub-resource actions on UsersController, which needs no request body and so adds no
// file to this folder.
//
// MIGRATION: IsSuperUser is absent as a privilege-escalation control. The column defaults to 0
// and host-level administration is excluded from this migration's scope, so a PUT that honoured
// an inbound value of true would hand a caller host rights through an ordinary profile edit. Like
// Username and AffiliateId it is a create-time column: the terminal AddUser procedure accepts it
// and the terminal UpdateUser procedure does not. The legacy edit screen only ever reads it, at
// User.ascx.vb:L267, to decide whether to show the delete button.
//
// MIGRATION: Roles are absent. Role membership is managed as its own resource, through POST and
// DELETE on /api/v1/portals/{portalId}/roles/{roleId}/users/{userId}, so that an assignment carries its own effective and expiry
// dates. No role shape is declared here either; the role DTO folder owns that contract.
//
// MIGRATION: Profile values are absent. They are managed through PUT /api/v1/portals/{portalId}/users/{userId}/profile
// with UserProfileDto, and no dependency on that type is taken from here. Note that address and
// telephone, which the legacy user grid displays alongside these fields, are profile values and
// not columns on the Users table, which is a second reason they are not editable through this
// request.
//
// MIGRATION: Server-computed facts are absent - presence, the last-signin and last-activity
// stamps, lockout and credential-change stamps, and the creation stamp. None is ever
// client-supplied; the creation stamp is immutable and written once from the injected clock.
// Audit-stamp members are absent for a sharper reason: the modern DotNetNuke audit quartet does
// not occur anywhere in the 88 scripts, so there is nothing to preserve. The correlation
// identifier is absent because it travels as a request header, not as payload.
//
// MIGRATION: Three legacy mechanisms are dropped rather than translated, per the rule that a
// workaround with a first-class replacement produces no target member. UserMembership.vb
// maintains an ObjectHydrated latch, set at L89 and L109, to drive progressive hydration; the ORM
// materialiser replaces it, and the parallel roles-hydrated latch on UserInfo goes with it. The
// change-tracking IsDirty flag is replaced by the ORM's own tracking. FullName, at
// UserInfo.vb:L375, is both computed from first and last name and explicitly marked obsolete in
// favour of DisplayName. The token-replacement cacheability member goes with its excluded
// subsystem. Equally, none of the legacy accessor side effects is reproduced: the legacy Email
// setter also writes through to the membership object at UserInfo.vb:L127, the first and last name
// accessors delegate to the profile object, and the display-name format is applied by
// UpdateDisplayName before the update at User.ascx.vb:L374. All of those are service concerns; the
// members below are plain data.
public class UpdateUserRequest
{
    /// <summary>
    /// Gets or sets the user's given name.
    /// </summary>
    /// <value>
    /// The given name. Defaults to the empty string, which is the legacy representation of an
    /// absent string rather than a placeholder.
    /// </value>
    /// <remarks>
    /// Backed by <c>Users.FirstName</c>, measured as <c>nvarchar(50) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L99</c>, and assigned by the terminal <c>UpdateUser</c>
    /// procedure. Non-nullable here because the column is: the legacy class agrees, declaring
    /// <c>MaxLength(50)</c> and <c>Required(True)</c> at <c>UserInfo.vb:L144</c>. For the
    /// validator author: required, maximum length 50. An omitted field arrives as the empty
    /// string, which is exactly the value the legacy required-field check rejected, so the two
    /// treatments coincide.
    /// </remarks>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user's family name.
    /// </summary>
    /// <value>
    /// The family name. Defaults to the empty string, not <c>null</c>.
    /// </value>
    /// <remarks>
    /// Backed by <c>Users.LastName</c>, whose terminal declaration is
    /// <c>nvarchar(50) NOT NULL</c>. For the validator author: required, maximum length 50.
    /// <para>
    /// The nullability has to be read as a chain rather than from the baseline. The column is
    /// declared <c>[LastName] [nvarchar] (50) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L100</c>, one line below a <c>NOT NULL</c>
    /// <c>FirstName</c>, but the <c>01.00.05</c> rebuild through <c>Tmp_Users</c> re-declares it
    /// <c>LastName nvarchar(50) NOT NULL</c> (<c>01.00.05:L18</c>) and drops and renames the real
    /// table over it (<c>01.00.05:L54</c>, <c>L57</c>); the <c>01.00.06</c> rebuild preserves
    /// <c>NOT NULL</c> (<c>01.00.06:L186</c>, with the same drop and rename at <c>L227</c> and
    /// <c>L230</c>). No <c>ALTER COLUMN</c> touches it afterwards, so the baseline asymmetry with
    /// <c>FirstName</c> does not survive the chain. Citing the
    /// baseline alone is what produces the opposite reading - that the asymmetry survives the whole
    /// upgrade chain and the member is optional - and that reading is wrong.
    /// </para>
    /// <para>
    /// There is consequently no disagreement to resolve: <c>UserInfo.vb:L178</c>'s
    /// <c>Required(True)</c>, the terminal column, the persistence configuration's
    /// <c>IsRequired()</c> and the terminal <c>UpdateUser</c> parameter
    /// <c>@LastName nvarchar(50)</c> (<c>04.00.04.SqlDataProvider:L1077</c>) all agree.
    /// </para>
    /// </remarks>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name shown to other users in place of the sign-in name.
    /// </summary>
    /// <value>
    /// The display name. Defaults to the empty string, which the column's own default makes a
    /// legitimate stored value.
    /// </value>
    /// <remarks>
    /// Backed by <c>Users.DisplayName</c>, added to the table as
    /// <c>nvarchar(128) NOT NULL CONSTRAINT DF_Users_DisplayName DEFAULT ''</c> at
    /// <c>04.00.04.SqlDataProvider:L670</c>, and assigned by the terminal <c>UpdateUser</c>
    /// procedure. Because that default is the empty string, an empty display name is valid stored
    /// state and the application service may derive one from the portal's display-name format;
    /// this member performs no such derivation. For the validator author: maximum length 128, and
    /// note that the legacy class declares <c>Required(True)</c> at <c>UserInfo.vb:L104</c> while
    /// the column defaults to empty, so a required check here is a policy decision to make
    /// consciously rather than a schema requirement. Also note that
    /// <c>User.ascx.vb:L399</c> switches this editor to read-only whenever the portal defines a
    /// display-name format.
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user's email address.
    /// </summary>
    /// <value>
    /// The email address. Defaults to the empty string, not <c>null</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// Backed by <c>Users.Email</c>, measured as <c>nvarchar(256) NULL</c> at
    /// <c>03.00.13.SqlDataProvider:L109-110</c>, and assigned by the terminal <c>UpdateUser</c>
    /// procedure. For the validator author: required, maximum length 256, and not unique.
    /// </para>
    /// <para>
    /// THE WIDTH IS 256, NOT 100. The
    /// <c>nvarchar(100) NOT NULL</c> column at <c>01.00.00.SqlDataProvider:L107</c> was REMOVED by
    /// the nine-column drop at <c>02.02.01:L50-51</c> and replaced by the nullable
    /// <c>nvarchar(256)</c> one cited above, which nothing later alters; the terminal
    /// <c>@Email nvarchar(256)</c> parameter at <c>04.00.04:L704</c> and
    /// <c>UserInfo.vb:L121</c>'s <c>MaxLength(256)</c> both AGREE with it. There was no
    /// disagreement to resolve - only a superseded column being cited as though it were current.
    /// </para>
    /// <para>
    /// Requiredness is an API-LEVEL rule, not the column's nullability: the column permits an
    /// absent address, and the legacy screen's <c>Required(True)</c> is what an update request
    /// must satisfy. Uniqueness is not enforced anywhere, by measurement rather than by
    /// preference.
    /// </para>
    /// <para>
    /// The address SHAPE rule is not reproduced here and is no longer quoted here either. It lives
    /// once, in <see cref="Domain.ValueObjects.EmailAddress"/>, which transcribes the legacy
    /// constant <c>glbEmailRegEx</c> from <c>Library/Components/Shared/Globals.vb:L132</c> clause
    /// by clause and records its two deliberate departures - notably that the legacy
    /// four-character cap on the final domain label is replaced by bounded, standards-derived
    /// label checks. The validator calls that type. What remains unenforced is the PER-PORTAL
    /// override: the legacy screen replaced the shipped pattern from a
    /// <c>Security_EmailValidation</c> setting at runtime, and reading a portal setting is data
    /// access that a validator does not perform.
    /// </para>
    /// </remarks>
    public string Email { get; set; } = string.Empty;
}
