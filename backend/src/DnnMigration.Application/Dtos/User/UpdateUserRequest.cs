namespace DnnMigration.Application.Dtos.User;

/// <summary>
/// Inbound contract for <c>PUT /api/v1/users/{id}</c>, carrying the complete set of fields an
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
/// Where the schema disagrees with the legacy class, the schema wins. Three such disagreements
/// were measured and all three are resolved toward the column definition. First,
/// <c>UserInfo.vb:L121</c> declares <c>MaxLength(256)</c> on <c>Email</c> while the column is
/// <c>nvarchar(100)</c>, so 100 governs. Second, <c>UserInfo.vb:L178</c> declares
/// <c>Required(True)</c> on <c>LastName</c> while the column permits NULL, so the member is
/// optional. Third, the terminal procedure's own parameters are wider than the columns they feed
/// (<c>Email</c> declared at 256 and <c>DisplayName</c> at 100 against columns of 100 and 128),
/// which independently confirms that a declaration upstream of the table is not evidence about
/// the table. <c>FirstName</c> and <c>DisplayName</c> show no disagreement.
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
// MIGRATION: The user identifier is absent because PUT /api/v1/users/{id} already carries it in
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
// bound to POST /api/v1/users/{id}/password. Folding credential mutation into a general profile
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
// DELETE on /api/v1/roles/{id}/users, so that an assignment carries its own effective and expiry
// dates. No role shape is declared here either; the role DTO folder owns that contract.
//
// MIGRATION: Profile values are absent. They are managed through PUT /api/v1/users/{id}/profile
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
    /// Gets or sets the user's family name, which may legitimately be absent.
    /// </summary>
    /// <value>
    /// The family name, or <c>null</c> when the caller supplies none.
    /// </value>
    /// <remarks>
    /// Backed by <c>Users.LastName</c>, measured as <c>nvarchar(50) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L100</c>. This member is nullable while <c>FirstName</c> is
    /// not, and that asymmetry is real rather than untidy: the two columns were declared one line
    /// apart with different nullability and the difference has survived the whole upgrade chain.
    /// It is preserved deliberately. Note the disagreement it resolves: <c>UserInfo.vb:L178</c>
    /// declares <c>Required(True)</c>, but the column permits NULL, and the schema is the
    /// authority. For the validator author: optional, maximum length 50 when present, and no
    /// required check.
    /// </remarks>
    public string? LastName { get; set; }

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
    /// Backed by <c>Users.Email</c>, measured as <c>nvarchar(100) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L107</c>, and assigned by the terminal <c>UpdateUser</c>
    /// procedure. For the validator author: required, maximum length 100, and not unique. The
    /// length resolves a disagreement in favour of the schema, since <c>UserInfo.vb:L121</c>
    /// declares <c>MaxLength(256)</c> against a column of 100; the terminal procedure's own
    /// parameter is likewise declared at 256, and neither overrides the table. Uniqueness is not
    /// enforced anywhere, by measurement rather than by preference. The pattern the legacy screen
    /// applied is the constant <c>glbEmailRegEx</c> at
    /// <c>Library/Components/Shared/Globals.vb:L132</c>, namely
    /// <c>\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b</c>, and it is recorded here for
    /// reference only, unencoded and unenforced, because a portal-level
    /// <c>Security_EmailValidation</c> setting supersedes it at runtime.
    /// </remarks>
    public string Email { get; set; } = string.Empty;
}
