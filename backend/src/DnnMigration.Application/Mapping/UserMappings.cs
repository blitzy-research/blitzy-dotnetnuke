using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Entities;
using RoleEntity = DnnMigration.Domain.Entities.Role;
using UserEntity = DnnMigration.Domain.Entities.User;

namespace DnnMigration.Application.Mapping;

// ==============================================================================================
// MIGRATION: THE CREDENTIAL BOUNDARY THIS FILE OWNS. Read this before changing anything below.
//
// The legacy credential store was REVERSIBLE and its key was committed to source control.
// Website/release.config lines 89-93 declare a <machineKey> with decryption="3DES" and a literal
// decryption key sitting in the file, and lines 236-246 register AspNetSqlMembershipProvider with
// passwordFormat="Encrypted" alongside enablePasswordRetrieval="true". Anyone holding a checkout
// could therefore recover every stored password. The target replaces that arrangement with
// one-way BCrypt hashing and does NOT carry password retrieval forward to any endpoint or screen.
// An existing credential is made usable either by the authentication service's bounded, opt-in
// legacy verification followed by immediate BCrypt replacement, or by the administrative reset
// fallback. This mapping layer participates in neither path and never projects credential material.
// The key values themselves are deliberately not reproduced here - only the line numbers that hold
// them - so this file adds no new copy of a secret.
//
// The six rules that follow, which every future edit to this file must preserve:
//
//   1. User.PasswordHash is NEVER projected onto a response contract. Not onto UserListItemDto,
//      not onto UserDetailDto, not onto UserProfileDto, not anywhere a value flows outward.
//   2. User.PasswordAnswer is NEVER projected onto a response contract, for the same reason.
//   3. User.PasswordQuestion is a credential-recovery artefact, and the legacy install did not
//      even use it - requiresQuestionAndAnswer="false" at release.config line 241. No response
//      contract under Dtos/User declares it, so nothing here projects it.
//   4. None of the three is ever logged, formatted, interpolated or otherwise stringified.
//   5. ChangePasswordRequest is the only inbound contract that legitimately carries a password,
//      and this file deliberately declares no member for it: the plaintext travels straight from
//      the controller to IUserService.ChangePasswordAsync. THIS FILE NEVER HASHES. Hashing lives
//      in Infrastructure/Security/BcryptPasswordHasher.cs behind the Domain-declared
//      IPasswordHasher, and Rule T1 forbids this project from referencing BCrypt at all.
//   6. There is no "read the password" projection of any kind, because no such endpoint exists.
//
// The defence is the ABSENCE OF AN ASSIGNMENT, not an attribute. No [JsonIgnore] is used here to
// suppress a credential, because an attribute can be deleted by someone who does not know why it
// was added, whereas a value that is never assigned cannot leak.
// ==============================================================================================

// MIGRATION: FLATTENING PATTERN F3 - two legacy classes compose onto one aggregate.
// Library/Components/Users/UserInfo.vb (class at line 41) published fourteen properties and OWNED
// a UserMembership instance (line 195) and a UserProfile instance (line 236), hydrating each from
// inside a property getter. Library/Components/Users/Membership/UserMembership.vb (class at line
// 41) published a further fifteen. The target merges both onto the single User aggregate, so the
// composition seam disappears and reading a property performs no database work.
//
// MIGRATION: the two Email properties collapse into exactly ONE. UserInfo declared Email at line
// 123 - lines 121-122 being its attribute continuation - and its setter body at line 132 reads
// "Me.Membership.Email = Value", writing straight through to UserMembership.Email at line 344.
// The two were always the same fact stored twice. User.Email is the single survivor, and no
// projection in this file emits a second email member under any name.
//
// MIGRATION: four members are renamed on the way across, and the casing of one of them is a real
// trap: UserMembership.Approved (line 83) becomes IsApproved; LockedOut (line 223) becomes
// IsLockedOut; IsOnLine (line 123, with a CAPITAL L in the legacy name) becomes IsOnline; and
// Password (line 263) becomes PasswordHash, which - per the credential boundary above - travels
// inbound only.
//
// MIGRATION: the legacy hydration-tracking flags are dropped and replaced by nothing.
// UserMembership.ObjectHydrated (line 243), UserProfile.ObjectHydrated (line 271),
// UserProfile.IsDirty (line 237) and ProfilePropertyDefinition.IsDirty (line 127) existed so the
// legacy code could tell a materialised object from an empty one and a modified object from a
// clean one. Rule T8 hands both jobs to Entity Framework Core's change tracker, so no target
// member corresponds to any of the four and this file neither reads nor sets such a flag.
//
// MIGRATION: User carries NO scalar PortalId, even though UserInfo declared PortalID at line 219.
// A user is one row shared by every portal it belongs to, and the per-portal facts live on the
// UserPortal join entity whose real database key is the composite (UserId, PortalId). Every
// projection below therefore takes the tenant as an EXPLICIT portalId argument supplied by the
// caller that resolved it, rather than reading it off the account.
//
// MIGRATION: UserPortal.IsAuthorised maps the British-spelled column "Authorised". The spelling is
// preserved in the schema because Rule T4 makes the schema authoritative; only the CLR member is
// renamed to the Is-prefixed form. No projection here writes that row: it is created alongside the
// account by the service, which owns the clock that stamps its CreatedDate.
//
// MIGRATION: the eleven membership-snapshot members of User - IsApproved, CreatedDate, IsOnline,
// LastActivityDate, LastLockoutDate, LastLoginDate, LastPasswordChangeDate, IsLockedOut,
// PasswordHash, PasswordAnswer and PasswordQuestion - are unmapped by the entity configuration
// because the store that holds them is not DotNetNuke's. The 88-script upgrade chain only ever
// ALTERs the ASP.NET membership objects: 04.00.00.SqlDataProvider line 31 is
// "ALTER PROCEDURE dbo.aspnet_Membership_UpdateUser" and line 119 is
// "ALTER PROCEDURE dbo.aspnet_Membership_UpdateUserInfo". They are installed externally, so the
// repository composes these eleven values during its read and this file simply reads them off the
// entity. Each is nullable because "not known" is a genuine third state, and where a response
// contract declares a non-nullable counterpart the projection reports the CLOSED answer - not
// approved, not online, not locked out - rather than inventing a permissive one.
//
// MIGRATION: every attribute on the legacy classes is dropped and replaced by nothing at all.
// UserInfo.vb line 42 declared "Implements IPropertyAccess", implemented at GetProperty (line 426)
// with a ByRef status argument and at Cacheability (line 484); both go, along with the
// token-replacement subsystem they served. The Browsable, SortOrder, Required, MaxLength,
// IsReadOnly and RegularExpressionValidator attributes all come from DotNetNuke.UI.WebControls,
// imported at UserInfo.vb line 22 and out of scope. They are NOT swapped for DataAnnotations, EF
// Core or System.Text.Json equivalents: the wire contract belongs to the DTOs and every length,
// required and format rule belongs to a FluentValidation validator in Application/Validation.
//
// MIGRATION: MaxLength(256) on UserInfo.Email (line 121) contradicts the schema, and the schema
// wins. The authoritative width is "[Email] [nvarchar] (100) NOT NULL" at
// 01.00.00.SqlDataProvider line 107. Per Rule T4 the discrepancy is recorded rather than
// reconciled here, and this file enforces NEITHER bound - a mapper that truncated or rejected a
// value would be validating.
//
// MIGRATION: no projection in this file validates anything, and the schema proves why it must not.
// The built-in Host superuser is seeded at 01.00.00.SqlDataProvider line 7205 with Email = 'host',
// which fails the legacy address pattern glbEmailRegEx declared at
// Library/Components/Shared/Globals.vb line 132. Existing production data therefore does not
// satisfy the validator, so a mapper that rejected it would make the host account unreadable.
// Validation is a write-time concern owned by Application/Validation; reading is unconditional.
// (That pattern also caps the top-level domain at four letters, so addresses ending .museum or
// .travel are refused. The limit is preserved as legacy behaviour and is the validator's business,
// not this file's.)
//
// MIGRATION: the legacy null-sentinel table is honoured as mapping knowledge and never restored as
// a value. Library/Components/Shared/Null.vb encodes absence as -1 for the integral types
// (NullInteger, line 41), Date.MinValue for a date (NullDate, line 66) and - the trap - the EMPTY
// STRING for text (NullString, line 71). Two consequences bind this file. First, an empty string
// is a STORED VALUE and is never converted to null or treated as an absence. Second, a date whose
// date part is 0001-01-01 was treated as absent by Null.IsNull regardless of its time component
// (line 224 compares "objDate.Date.Equals(NullDate.Date)"), so DateTime.MinValue must remain
// distinguishable from null end to end here rather than being folded into it.
//
// MIGRATION: a latent defect in the legacy sentinel helper is recorded and deliberately NOT fixed,
// per the minimal-change discipline. Null.vb line 123 matches "System.Int32" and "System.Int64" in
// the same Select Case arm and returns the Int32-sized NullInteger for both, so a 64-bit field was
// given a 32-bit sentinel. Nothing in this file depends on that behaviour; it is noted so that a
// later reader does not mistake the asymmetry for an accident of this migration.
//
// MIGRATION: the two legacy row-hydration paths are replaced, not ported. The reflection hydrator
// Library/Components/Shared/CBO.vb (729 lines) and the hand-rolled per-column sentinel reads in
// ModuleController.FillModuleInfo are both superseded by Entity Framework Core's materialiser in
// the Infrastructure layer. Consequently this file declares no Fill, FillObject, FillCollection,
// FillDictionary, CloneObject, GetPropertyInfo, InitializeObject or Serialize member, touches no
// IDataReader, DataTable or DataRow, and uses no ArrayList or Hashtable. IHydratable is not
// implemented either - and it never was: not one file in the entire repository implements it.
// ==============================================================================================

/// <summary>
/// Hand-written projections between the <see cref="UserEntity"/> aggregate, its profile records and
/// the user transfer contracts.
/// </summary>
/// <remarks>
/// <para>
/// Every method here is a pure, synchronous function of its arguments. Nothing awaits, nothing
/// reads a clock, nothing touches a repository or a cache, nothing validates and nothing hashes.
/// A projection that needed the current instant would take it as an explicit argument; none does,
/// so calling any method twice with the same input yields an equal result.
/// </para>
/// <para>
/// The mapping is written out by hand, member by member. No convention-based or reflection-driven
/// mapper is used, so a contract that gains a member produces a visible gap here rather than a
/// silently unpopulated field discovered at run time.
/// </para>
/// <para>
/// The terminal <c>dbo.Users</c> table carries nine columns. Everything else an account screen
/// displays comes from somewhere else - the external membership store, the profile tables, or the
/// per-portal join row - and these projections make each source explicit rather than hiding it
/// behind one flattened class as the legacy entity did.
/// </para>
/// <para>
/// The postal address and telephone number that the legacy account grid displayed are profile
/// values, dropped from <c>dbo.Users</c> by the 02.02.01 upgrade script. They arrive as explicit
/// arguments so a caller can resolve a whole page of them in one read.
/// </para>
/// <para>
/// No projection here reads, writes, returns or logs a credential. See the credential boundary
/// recorded immediately above this type.
/// </para>
/// </remarks>
public static class UserMappings
{
    /// <summary>
    /// The legacy integral encoding of a host-level profile-property declaration, published OUTBOUND
    /// only.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this constant is a response-contract encoding and nothing else. It is applied by
    /// <see cref="ToDto(ProfilePropertyDefinition, int)"/> when the stored scope is SQL <c>NULL</c>,
    /// because <c>ProfilePropertyDefinitionDto.PortalId</c> deliberately keeps the non-nullable
    /// <see cref="int"/> and the -1 encoding the legacy class published, and Rule T7 preserves an
    /// externally observable sentinel at the boundary. It is deliberately NOT applied inbound: the
    /// create mapper writes the scope it is given, for the reason recorded on
    /// <see cref="ToNewDefinition(int?, CreateProfilePropertyDefinitionRequest)"/>. Nothing in the
    /// domain, the repository or the service layer may compare a portal identifier against it.
    /// </remarks>
    private const int LegacyHostPortalId = -1;

    /// <summary>
    /// Projects an account onto the row shape the account list renders.
    /// </summary>
    /// <param name="user">The account to project.</param>
    /// <param name="portalId">The tenant the account is being listed within.</param>
    /// <param name="address">The account's postal address profile value, or <see langword="null"/> when unset.</param>
    /// <param name="telephone">The account's telephone profile value, or <see langword="null"/> when unset.</param>
    /// <param name="portalAdministratorId">
    /// The account named by the tenant's <c>Portals.AdministratorId</c>, or <see langword="null"/> when the
    /// tenant designates nobody. Used only to compute <see cref="UserListItemDto.CanDelete"/>.
    /// </param>
    /// <returns>The list row.</returns>
    /// <remarks>
    /// The tenant is an argument rather than a property read because the aggregate has no portal
    /// scalar; it is assigned through unchanged, since a portal identifier of -1 is the genuine host
    /// portal created by the <c>IDENTITY(-1, 1)</c> seed on <c>Portals.PortalID</c> and 0 is the
    /// shipped <c>_default</c> portal. Neither may be mistaken for an absent value.
    /// </remarks>
    public static UserListItemDto ToListItem(
        UserEntity user,
        int portalId,
        string? address,
        string? telephone,
        int? portalAdministratorId)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserListItemDto
        {
            UserId = user.UserId,
            PortalId = portalId,
            Username = user.Username,
            FirstName = user.FirstName,
            LastName = user.LastName,
            DisplayName = user.DisplayName,
            Address = address,
            Telephone = telephone,

            // MIGRATION: widening only. The aggregate models the column's history with a nullable
            // string, while the contract declares a non-nullable one, so an unknown address becomes
            // the empty string. This is the null-to-empty direction and never the reverse: an email
            // that IS the empty string stays the empty string, because Null.vb line 71 makes ""
            // a stored value rather than an absence.
            Email = user.Email ?? string.Empty,

            CreatedDate = user.CreatedDate,
            LastLoginDate = user.LastLoginDate,

            // MIGRATION: the three membership flags are nullable on the aggregate because the
            // external store may not have been read, while the contract declares them non-nullable.
            // An unknown answer therefore reports the CLOSED state. Reporting "approved" or "not
            // locked out" for a fact nobody has established would be the permissive reading, and a
            // list projection is not the place to take that risk.
            IsApproved = user.IsApproved ?? false,
            IsOnline = user.IsOnline ?? false,
            IsSuperUser = user.IsSuperUser,
            IsLockedOut = user.IsLockedOut ?? false,

            // THE DELETE CAPABILITY, computed from the same two protections the delete operation
            // enforces rather than from a rule of the mapper's own. An account that is a host account,
            // or that is the one named by this portal's AdministratorId, cannot be deleted through
            // portal administration - so a client that offers the command would be offering a refusal.
            //
            // ⚠ THE ADMINISTRATOR COMPARISON IS AN EQUALITY AND MUST STAY ONE. Users.UserID seeds
            // IDENTITY(1, 1), but Portals.AdministratorId is an ordinary nullable integer column and the
            // legacy null contract spells a missing integer as MINUS ONE - so a portal that designates
            // nobody may hold either null or -1, and neither may be read as "matches this row". A null
            // administrator therefore protects nothing, which is the correct reading: there is no
            // designated administrator to protect.
            CanDelete = CanBeDeleted(user, portalAdministratorId),
        };
    }

    /// <summary>
    /// Whether an account may be removed from a tenant.
    /// </summary>
    /// <param name="user">The account being judged.</param>
    /// <param name="portalAdministratorId">
    /// The account named by the tenant's <c>Portals.AdministratorId</c>, or <see langword="null"/> when the
    /// tenant designates nobody.
    /// </param>
    /// <returns><see langword="true"/> when the removal operation would be permitted.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ SHARED BY THE LIST AND THE DETAIL PROJECTIONS, AND THAT IS THE WHOLE REASON IT EXISTS. The rule
    /// was previously written out in the list projection only, and the detail contract published no
    /// capability at all - so the client screen that edits one account had to approximate it, and
    /// approximated it as "not a super user", omitting the administrator clause. The two surfaces then
    /// disagreed about the same permission for the same account: the listing correctly withheld the
    /// removal affordance for the tenant's designated administrator while the detail screen offered it,
    /// where its only possible outcome was a refusal.
    /// </para>
    /// <para>
    /// ⚠ THE ADMINISTRATOR COMPARISON IS AN EQUALITY AND MUST STAY ONE. <c>Users.UserID</c> seeds
    /// <c>IDENTITY(1, 1)</c>, but <c>Portals.AdministratorId</c> is an ordinary nullable integer column and
    /// the legacy null contract spells a missing integer as MINUS ONE - so a portal that designates nobody
    /// may hold either null or -1, and neither may be read as "matches this row". A null administrator
    /// therefore protects nothing, which is the correct reading: there is no designated administrator to
    /// protect.
    /// </para>
    /// </remarks>
    private static bool CanBeDeleted(UserEntity user, int? portalAdministratorId) =>
        !user.IsSuperUser
        && (portalAdministratorId is not { } designated || designated != user.UserId);

    /// <summary>
    /// Projects an account onto the full detail contract.
    /// </summary>
    /// <param name="user">The account to project.</param>
    /// <param name="portalId">The tenant the account is being read within.</param>
    /// <param name="roles">The names of the roles the account currently holds in that tenant.</param>
    /// <param name="portalAdministratorId">
    /// The account named by the tenant's <c>Portals.AdministratorId</c>, or <see langword="null"/> when the
    /// tenant designates nobody. Used only to compute <see cref="UserDetailDto.CanDelete"/>, by the same
    /// member that computes it for the list projection.
    /// </param>
    /// <returns>The detail contract.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the role names arrive as an argument, resolved by the caller from navigations it
    /// has already loaded. The legacy aggregate exposed them as a raw <c>String()</c> array
    /// (<c>UserInfo.vb</c> line 261) whose getter could trigger a fetch; the target reaches them
    /// through the <c>UserRoles</c> navigation instead, and this file never queries for them. Role
    /// entities themselves are projected by <c>RoleMappings</c>, not here.
    /// </para>
    /// <para>
    /// MIGRATION: <c>MustChangePassword</c> is the only credential-adjacent member on this contract.
    /// It maps <c>User.UpdatePassword</c>, a genuine <c>dbo.Users</c> column and a boolean flag - it
    /// is not, and must never become, a channel for the credential itself.
    /// </para>
    /// </remarks>
    public static UserDetailDto ToDetail(
        UserEntity user,
        int portalId,
        IReadOnlyList<string> roles,
        int? portalAdministratorId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roles);

        return new UserDetailDto
        {
            UserId = user.UserId,
            PortalId = portalId,
            Username = user.Username,
            FirstName = user.FirstName,
            LastName = user.LastName,
            DisplayName = user.DisplayName,

            // MIGRATION: one email member, never two - see the F3 note above this type.
            Email = user.Email ?? string.Empty,

            IsSuperUser = user.IsSuperUser,
            AffiliateId = user.AffiliateId,
            IsApproved = user.IsApproved ?? false,
            IsLockedOut = user.IsLockedOut ?? false,
            IsOnline = user.IsOnline ?? false,
            MustChangePassword = user.UpdatePassword,

            // MIGRATION: the five membership instants are passed through exactly as read, including
            // DateTime.MinValue. Null.vb line 224 compared only the DATE part against NullDate, so
            // the legacy code treated any 0001-01-01 timestamp as absent whatever its time was.
            // This projection keeps MinValue and null distinct instead, so a caller can tell "the
            // stored value is the legacy sentinel" from "the store was never read".
            CreatedDate = user.CreatedDate,
            LastLoginDate = user.LastLoginDate,
            LastActivityDate = user.LastActivityDate,
            LastLockoutDate = user.LastLockoutDate,
            LastPasswordChangeDate = user.LastPasswordChangeDate,

            Roles = roles,

            // The SAME rule the list projection publishes, from the one member that owns it. Before this
            // the detail contract carried no capability at all and the client screen approximated the
            // rule, omitting the administrator clause - so the two surfaces disagreed for one account.
            CanDelete = CanBeDeleted(user, portalAdministratorId),
        };
    }

    // ==========================================================================================
    // MIGRATION: FLATTENING PATTERN F5 - a definition/value split, and the single most confusing
    // thing in this file.
    //
    // Library/Components/Users/Profile/UserProfile.vb (class at line 41) published NINETEEN named
    // properties - Cell, City, Country, Fax, FirstName, IM, LastName, PostalCode, PreferredLocale,
    // Region, Street, Telephone, TimeZone, Unit and Website among them - as though a profile were a
    // fixed record. It is not: the schema stores one ROW PER PROPERTY in UserProfile, keyed by a
    // ProfilePropertyDefinition, so the set of properties is tenant data rather than source code.
    // This file therefore creates no nineteen-property class. It projects a COLLECTION, and adding
    // a profile field to an installation requires no code change at all.
    //
    // Four of those nineteen produce no target member whatsoever: FullName (line 203) was computed
    // rather than stored, IsDirty (line 237) and ObjectHydrated (line 271) were hydration
    // bookkeeping that the change tracker replaces, and ProfileProperties (line 329) returned a
    // pre-generics ProfilePropertyDefinitionCollection - one of the four legacy CollectionBase
    // wrappers that IReadOnlyList<T> subsumes and that produce no target file.
    //
    // MIGRATION: F5's essence is that TWO MEMBERS MOVE OFF THE DEFINITION ONTO THE VALUE. The
    // legacy ProfilePropertyDefinition (class at line 43 of
    // Library/Components/Users/Profile/ProfilePropertyDefinition.vb) carried fifteen properties,
    // two of which were not definition metadata at all: PropertyValue (line 246) and Visibility
    // (line 336) are facts about ONE ACCOUNT'S answer, not about the question. The target moves
    // both onto UserProfileValue and leaves the definition holding metadata only. That is why
    // ToDto below cannot populate a real visibility, and why UserProfileValueDto carries the value.
    //
    // MIGRATION: Visibility is deliberately demoted from the legacy UserVisibilityMode enumeration
    // to a plain int, on both the entity and the contract. UserVisibilityMode is not among the nine
    // enumerations this migration creates, so no such type exists to convert to and none is
    // invented here. The loss of type safety is real and is accepted rather than concealed: the
    // integer is carried through unchanged in both directions and the client interprets it.
    //
    // MIGRATION: three members are renamed against their columns on the definition -
    // Deleted becomes IsDeleted, Required becomes IsRequired and Visible becomes IsVisible. Note
    // that the legacy class had no Deleted property at all; the entity takes IsDeleted from the
    // schema, which is the authority per Rule T4.
    // ==========================================================================================

    /// <summary>
    /// Projects a profile property definition onto its transfer contract.
    /// </summary>
    /// <param name="definition">The definition to project.</param>
    /// <param name="defaultVisibility">The tenant's default visibility hint for a value that has not been set.</param>
    /// <returns>The definition contract.</returns>
    /// <remarks>
    /// MIGRATION: there is no visibility column on the definition table - verified across all
    /// eighty-eight upgrade scripts - because per Pattern F5 visibility is a fact about an account's
    /// answer and lives on <c>UserProfileValue</c>. The contract's visibility member is therefore a
    /// default HINT rather than stored state, and the caller supplies it from the tenant's
    /// membership settings. The stored per-account column defaults to zero while this hint defaults
    /// to two, an asymmetry the legacy collection loader introduced and which is preserved rather
    /// than tidied away.
    /// </remarks>
    public static ProfilePropertyDefinitionDto ToDto(ProfilePropertyDefinition definition, int defaultVisibility)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new ProfilePropertyDefinitionDto
        {
            PropertyDefinitionId = definition.PropertyDefinitionId,

            // MIGRATION: the two encodings of "host-level" meet only in this mapping layer, and only
            // in this direction. 03.03.03 lines 77-83 made ProfilePropertyDefinition.PortalID nullable
            // and migrated the rows holding the legacy -1 with "SET PortalId = NULL WHERE
            // PortalId = -1", so the entity carries int? and the domain never restores the sentinel.
            // The contract, by its own deliberate decision recorded on
            // ProfilePropertyDefinitionDto.PortalId, keeps the non-nullable int and the legacy -1
            // encoding because -1 is what the legacy class published to its consumers, and Rule T7
            // preserves an externally observable sentinel at the boundary rather than letting
            // serialisation turn it into an absent value.
            //
            // MIGRATION: this is the OUTBOUND half and it has no inbound counterpart. A stored -1 is a
            // tenant key here, not a host marker, and is published unchanged - dbo.Portals.PortalID is
            // IDENTITY(-1, 1) (01.00.00.SqlDataProvider:L77), so -1 is the first real tenant of an
            // installation. Only a SQL NULL scope is encoded as -1 on the way out. The two therefore
            // read alike in this response member, which is the price of the non-nullable contract and
            // is accepted rather than concealed: host-level administration is out of scope
            // (AAP 0.2.2.4), so every read that reaches this projection is scoped to one resolved
            // tenant and cannot mix the two. ToNewDefinition deliberately performs NO inverse
            // translation; an earlier revision that did filed a real tenant's declaration into the
            // host scope, where the tenant's own read could not find it.
            //
            // Stated plainly, because it is the price of not treating -1 as absent: a host-level
            // declaration and a declaration belonging to tenant -1 are published identically in this
            // member. Nothing in this API can confuse them in practice - every declaration route is
            // tenant-scoped and a tenant's scope never includes the host rows - so a response carrying
            // -1 describes a row of the tenant the caller is signed in to, and only such rows are
            // reachable.
            PortalId = definition.PortalId ?? LegacyHostPortalId,

            ModuleDefId = definition.ModuleDefinitionId,
            DataType = definition.DataType,
            DefaultValue = definition.DefaultValue,
            PropertyCategory = definition.PropertyCategory,
            PropertyName = definition.PropertyName,
            Length = definition.Length,

            // MIGRATION: the three Is-prefixed entity members map back to their unprefixed column
            // names here - IsRequired to Required and IsVisible to Visible. IsDeleted is absent from
            // the contract by design: a deleted definition is filtered out before it reaches a
            // client, so publishing the flag would invite a client to filter instead.
            Required = definition.IsRequired,
            ValidationExpression = definition.ValidationExpression,
            ViewOrder = definition.ViewOrder,
            Visible = definition.IsVisible,

            Visibility = defaultVisibility,
        };
    }

    /// <summary>
    /// Projects an account's profile as the full set of its tenant's definitions, each paired with the
    /// value the account has supplied for it.
    /// </summary>
    /// <param name="userId">The account whose profile is projected.</param>
    /// <param name="definitions">The tenant's definitions, already in view order.</param>
    /// <param name="values">The values the account has supplied.</param>
    /// <param name="defaultVisibility">The tenant's default visibility hint.</param>
    /// <param name="displayVisibilityEnabled">
    /// Whether the tenant lets an account holder choose who may see each of their own values. Carried
    /// through to the projection rather than derived from <paramref name="defaultVisibility"/>: the two come
    /// from different tenant settings and answer different questions - one is which audience a value
    /// defaults to, the other is whether the audience may be chosen at all.
    /// </param>
    /// <returns>The profile contract.</returns>
    /// <remarks>
    /// Every definition appears in the result, including one the account has never filled in, which is
    /// reported with an empty value and no last-updated instant. That is what lets a client render the
    /// whole form from a single read instead of having to reconcile two collections itself. A stored
    /// value whose definition is not in the supplied set is not reported, because there is no field to
    /// render it in.
    /// </remarks>
    public static UserProfileDto ToProfile(
        int userId,
        IReadOnlyList<ProfilePropertyDefinition> definitions,
        IReadOnlyList<UserProfileValue> values,
        int defaultVisibility,
        bool displayVisibilityEnabled)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(values);

        // Indexed by definition so the pairing below is a lookup rather than a nested scan. A
        // duplicate row for one definition is not expected - the table is keyed by
        // (UserID, PropertyDefinitionID) - and the later row wins if one ever appears.
        var byDefinition = new Dictionary<int, UserProfileValue>(values.Count);
        foreach (UserProfileValue value in values)
        {
            byDefinition[value.PropertyDefinitionId] = value;
        }

        var properties = new List<UserProfileValueDto>(definitions.Count);
        foreach (ProfilePropertyDefinition definition in definitions)
        {
            UserProfileValue? stored = byDefinition.GetValueOrDefault(definition.PropertyDefinitionId);

            properties.Add(new UserProfileValueDto
            {
                PropertyDefinitionId = definition.PropertyDefinitionId,

                // MIGRATION: two physical columns, one logical value, and the coalescing is this
                // file's job because the entity exposes both columns raw and no derived member.
                // A short answer is stored in the bounded nvarchar(3750) PropertyValue and a long
                // one overflows into the ntext PropertyText.
                //
                // The precedence is not a choice - it reproduces the legacy read procedure
                // GetUserProfile, which returned a single column aliased PropertyValue computed as
                // "case when (PropertyValue Is Null) then PropertyText else PropertyValue end"
                // (04.00.04.SqlDataProvider line 1592, identical at 03.02.03 line 1533). The
                // bounded column wins whenever it is not SQL NULL; the overflow column is the
                // fallback.
                //
                // Null-coalescing is the EXACT equivalent because "??" tests for null alone. Writing
                // this as string.IsNullOrEmpty(PropertyValue) ? PropertyText : PropertyValue would
                // be a DEFECT: Null.vb line 71 makes the empty string a stored value, so a
                // deliberately blanked field would silently fall through to the overflow column.
                // PropertyValue = "" therefore yields "", never PropertyText and never null.
                PropertyValue = stored is null
                    ? string.Empty
                    : stored.PropertyValue ?? stored.PropertyText ?? string.Empty,

                // MIGRATION: a stored visibility of zero is a real answer and is reported as zero;
                // only the ABSENCE of a row falls back to the tenant's hint. Testing the integer
                // for zero instead would collapse the two.
                Visibility = stored is null ? defaultVisibility : stored.Visibility,

                LastUpdatedDate = stored?.LastUpdatedDate,
                Definition = ToDto(definition, defaultVisibility),
            });
        }

        return new UserProfileDto
        {
            UserId = userId,
            Properties = properties,
            DisplayVisibilityEnabled = displayVisibilityEnabled,
        };
    }

    /// <summary>
    /// Builds a new account aggregate from a creation request.
    /// </summary>
    /// <param name="request">The submitted creation request.</param>
    /// <returns>An unsaved account aggregate, without its credential record.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the credential is deliberately absent from the result. <c>CreateUserRequest</c>
    /// does carry a password, and this projection does not read it: the plaintext goes from the
    /// service to <c>IPasswordHasher</c> and from there to the external membership store, so
    /// <c>PasswordHash</c> is left null on the returned aggregate and this file never hashes.
    /// </para>
    /// <para>
    /// MIGRATION: the per-portal join row is not built here either. It carries a creation instant,
    /// and a pure projection has no clock; the service adds it, supplying the instant from the
    /// Domain-declared <c>IClock</c>.
    /// </para>
    /// </remarks>
    public static UserEntity ToNewUser(CreateUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // MIGRATION: the coalescing is not redundant even though the request member is declared
        // non-nullable. System.Text.Json will assign null to a non-nullable string property from a
        // JSON null without complaint, and dbo.Users.LastName is NOT NULL, so an explicit floor to
        // the empty string is what keeps a malformed body from reaching the column. This is the
        // null-to-empty direction only; a submitted "" is preserved as "".
        string firstName = request.FirstName;
        string lastName = request.LastName ?? string.Empty;

        // MIGRATION: FullName was COMPUTED, never persisted - UserInfo.vb line 375 and
        // UserProfile.vb line 203 both derived it. DisplayName, by contrast, is a real column
        // (nvarchar(128) NOT NULL, default '') and is what the target stores, so the composition is
        // used only as a fallback when the request supplies no display name of its own. That
        // reproduces what the legacy portal-creation path composed for a new administrator. The
        // whitespace test is on a DISPLAY name, not on a sentinel: a name of spaces is not a name,
        // and Trim keeps a missing surname from leaving a trailing space in a value shown on every
        // screen. It is a deterministic string operation with no I/O.
        string displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"{firstName} {lastName}".Trim()
            : request.DisplayName;

        return new UserEntity
        {
            Username = request.Username,
            FirstName = firstName,
            LastName = lastName,
            DisplayName = displayName,
            Email = request.Email,

            // MIGRATION: privilege is never granted from a request body. The legacy host account is
            // seeded by the installer (01.00.00.SqlDataProvider line 7205), not created through the
            // ordinary account path, so this is hard-coded false whatever the caller sent.
            IsSuperUser = false,
            UpdatePassword = false,
        };
    }

    /// <summary>
    /// Applies a submitted update to a tracked account aggregate.
    /// </summary>
    /// <param name="user">The tracked account to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// <para>
    /// The account name is not updatable: the legacy edit screen rendered it as read-only text once an
    /// account existed - <c>UserInfo.vb</c> line 301 marked the property read-only - and the request
    /// contract carries no member for it. It is also the join key into the external membership store,
    /// so changing it would rehome the account's credentials.
    /// </para>
    /// <para>
    /// MIGRATION: exactly four members are written. Identity, sign-in name, the host-account flag,
    /// the affiliate, every membership instant and every credential member are all left untouched, so
    /// an update that carries no password cannot blank one. Assigning only what the request declares
    /// is what makes that guarantee structural rather than incidental.
    /// </para>
    /// </remarks>
    public static void ApplyUpdate(UserEntity user, UpdateUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        user.FirstName = request.FirstName;

        // MIGRATION: floored to the empty string for the NOT NULL column, for the reason given in
        // ToNewUser above.
        user.LastName = request.LastName ?? string.Empty;

        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"{request.FirstName} {request.LastName ?? string.Empty}".Trim()
            : request.DisplayName;

        user.Email = request.Email;
    }

    /// <summary>
    /// Builds a new profile property definition from a submitted contract.
    /// </summary>
    /// <param name="portalId">
    /// The scope the definition belongs to: an identifier for a tenant-owned declaration, or
    /// <see langword="null"/> for a host-level declaration stored with a SQL <c>NULL</c> portal.
    /// </param>
    /// <param name="request">The submitted definition.</param>
    /// <returns>An unsaved definition.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this path binds the CREATE request rather than the response projection, and the module
    /// definition key below is the reason the two verbs cannot share one contract. The terminal insert
    /// procedure <c>AddPropertyDefinition</c> (<c>04.06.00:L1101</c>) declares <c>@ModuleDefId</c>; its
    /// update counterpart (<c>04.05.00:L1685</c>) does not declare it and does not write the column, so a
    /// member bound here would have been silently discarded on the other path.
    /// </para>
    /// <para>
    /// MIGRATION: the scope is nullable because the terminal column is, and NOT because a sentinel is
    /// being decoded. An earlier revision rewrote an incoming -1 into <see langword="null"/> here, which
    /// reproduced <c>AddPropertyDefinition</c>'s <c>GetNull</c> wrapper (<c>SqlDataProvider.vb:L1021</c>)
    /// and was wrong in this schema: <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), so -1 is the FIRST REAL TENANT an installation has, and the
    /// portal identifier reaching this mapper arrives from the resolved tenant route rather than from a
    /// caller asking for the host scope - host-level administration is out of scope
    /// (<c>AAP 0.2.2.4</c>). The collapse therefore filed a real tenant's declaration into the host
    /// scope, from which the same tenant's scoped read could never retrieve it. Every scope is now
    /// written exactly as it is given, so a create and the read that follows it agree for -1 as they
    /// already did for every other key.
    /// </para>
    /// </remarks>
    public static ProfilePropertyDefinition ToNewDefinition(
        int? portalId,
        CreateProfilePropertyDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = new ProfilePropertyDefinition
        {
            // MIGRATION: written through unchanged - see the scope note on this method. null is the
            // SQL-null host scope that 03.03.03.SqlDataProvider:L74-L83 established, and every non-null
            // value, -1 and 0 included, is an exact tenant key.
            //
            // MIGRATION: TRANSLATING THE TENANT IDENTIFIER HERE IS FORBIDDEN, and the reason is measured.
            // The legacy provider did translate: AddPropertyDefinition passed PortalId through GetNull
            // (SqlDataProvider.vb:L1021), so a caller naming -1 wrote a SQL NULL and created a HOST-LEVEL
            // declaration instead of one belonging to that tenant. In dbo.ProfilePropertyDefinition a NULL
            // PortalID means "host-level, shared by every portal", so reproducing that filed the tenant's
            // declaration as global and leaked it to every other tenant - while the API's own 201 response
            // reported portalId -1, describing a row it had not written. AAP 0.5.1.1 states the rule
            // directly: the portal-identifier wrapper "forbids treating -1 as absent", and preserving
            // tenant isolation is a stated preservation requirement. Absence therefore has exactly one
            // representation in this column, a SQL NULL, reachable only from a host-scoped write and never
            // by spelling a tenant identifier that happens to be negative.
            PortalId = portalId,
            ModuleDefinitionId = request.ModuleDefId,

            // MIGRATION: a definition is born live. IsDeleted is moved by a deletion, never by a
            // create or an edit, so neither path below reads it from the request.
            IsDeleted = false,
        };

        ApplyDefinitionCore(definition, request);
        return definition;
    }

    /// <summary>
    /// Applies a submitted update to a tracked profile property definition.
    /// </summary>
    /// <param name="definition">The tracked definition to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// <para>
    /// The owning portal and the deletion flag are never written from a request: the portal arrives from
    /// the route, and the flag is moved by a deletion rather than by an edit.
    /// </para>
    /// <para>
    /// MIGRATION: this path binds the UPDATE request, which carries NO module definition key, so the
    /// column cannot be rehomed by an edit. That is not a restriction this mapper invents - the terminal
    /// procedure <c>UpdatePropertyDefinition</c> (<c>04.05.00:L1685</c>) declares no such parameter and
    /// its <c>UPDATE ... SET</c> list does not name the column. Previously both verbs bound the response
    /// projection, so a caller could submit a module key on an edit and watch it be discarded without
    /// complaint; the member is now absent from the contract instead of ignored inside it. Visibility is
    /// likewise absent from both write contracts rather than ignored, because it is not a column on this
    /// table at all - per Pattern F5 it belongs to an account's value.
    /// </para>
    /// </remarks>
    public static void ApplyDefinitionUpdate(
        ProfilePropertyDefinition definition,
        UpdateProfilePropertyDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);

        ApplyDefinitionCore(definition, request);
    }

    /// <summary>
    /// Writes the definition member set that the creation and update paths share.
    /// </summary>
    /// <param name="definition">The definition to write to.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// MIGRATION: the parameter is typed as the shared member set rather than as either concrete request,
    /// which is what keeps these nine assignments at ONE site now that the two verbs bind two types. The
    /// interface deliberately excludes the create-only module definition key, so this method cannot reach
    /// a member only one verb honours even by accident.
    /// </remarks>
    private static void ApplyDefinitionCore(
        ProfilePropertyDefinition definition,
        IProfilePropertyDefinitionWriteMembers request)
    {
        definition.DataType = request.DataType;
        definition.DefaultValue = request.DefaultValue;
        definition.PropertyCategory = request.PropertyCategory;

        // MIGRATION: the name is stored exactly as submitted. The legacy property carried a
        // RegularExpressionValidator attribute (ProfilePropertyDefinition.vb line 228) restricting
        // it to "^[a-zA-Z0-9._%\-+']+$"; that rule is preserved as a FluentValidation rule in
        // Application/Validation, not enforced here, because a mapper that rejected stored data
        // could not read an installation that predates the rule.
        definition.PropertyName = request.PropertyName;

        // MIGRATION: this floor is a COLUMN WIDTH, not an identifier, and the distinction is the
        // whole point. A negative display width is meaningless, so it is floored to zero - the
        // behaviour the profile editor relied on. The prohibition on clamping applies to IDENTIFIERS,
        // where -1 is the host portal and 0 is the shipped _default portal, and no identifier
        // anywhere in this file is clamped, defaulted or tested against zero.
        definition.Length = Math.Max(request.Length, 0);

        definition.IsRequired = request.Required;
        definition.ValidationExpression = request.ValidationExpression;
        definition.ViewOrder = request.ViewOrder;
        definition.IsVisible = request.Visible;
    }

    /// <summary>
    /// Projects one published role, and the account's own assignment to it, onto a member-services
    /// catalogue row.
    /// </summary>
    /// <param name="role">The published role the service is expressed as.</param>
    /// <param name="assignment">
    /// The account's assignment to that role, or <see langword="null"/> when it holds none.
    /// </param>
    /// <param name="today">
    /// The current date, supplied by the caller from the injected clock, against which a lapsed
    /// subscription is recognised.
    /// </param>
    /// <param name="tenantTakesPayment">
    /// Whether the tenant has a payment processor account configured, which is the second arm of the
    /// legacy subscribe predicate.
    /// </param>
    /// <returns>The catalogue row.</returns>
    /// <remarks>
    /// <para>
    /// Reproduces the three predicates the legacy grid bound, all three of which read the FULL role
    /// rather than the suppressed <c>GetServices</c> projection:
    /// <c>ServiceText</c> (<c>Website/admin/Users/MemberServices.ascx.vb:L288-L305</c>),
    /// <c>ShowSubscribe</c> (<c>:L307-L323</c>) and <c>ShowTrial</c> (<c>:L325-L342</c>).
    /// </para>
    /// <para>
    /// The current date is a PARAMETER rather than a reading taken here, for two reasons. A mapper that
    /// read a clock would be untestable, and - the substantive one - one row's classification must not
    /// be able to disagree with the next row's because the two were mapped either side of midnight.
    /// The caller reads the clock once for the whole catalogue.
    /// </para>
    /// <para>
    /// MIGRATION: a fee is "charged" only when it is present AND greater than zero, and the legacy
    /// sentinel is why that has to be said. <c>RoleInfo.ServiceFee</c> was a non-nullable
    /// <c>Single</c>, so a stored <c>NULL</c> reached the legacy predicates through
    /// <c>Null.SetNull</c> as <c>Single.MinValue</c> - which is NOT zero, so
    /// <c>objRole.ServiceFee = 0.0</c> was FALSE for a role with no fee at all and such a role was
    /// handed to the payment page. Reading absence as "no fee" is the Rule T7 translation of the
    /// evident intent and matches the sibling cancellation rule in <c>RoleService</c>, which already
    /// tests <c>role.ServiceFee is decimal fee &amp;&amp; fee &gt; 0m</c>. The divergence is recorded
    /// in <c>MIGRATION_NOTES.md</c>.
    /// </para>
    /// <para>
    /// MIGRATION: the trial-used flag is a nullable bit and an absent value is read as "not used",
    /// which is exactly the collapse the legacy predicate performed with
    /// <c>(objUserRole Is Nothing) OrElse (Not objUserRole.IsTrialUsed)</c> (<c>:L336</c>).
    /// </para>
    /// </remarks>
    public static MemberServiceDto ToMemberService(
        RoleEntity role,
        UserRole? assignment,
        DateTime today,
        bool tenantTakesPayment)
    {
        ArgumentNullException.ThrowIfNull(role);

        bool subscribed = assignment is not null;
        bool trialUsed = assignment?.IsTrialUsed ?? false;

        // ServiceText's own two tests, in its own order: not subscribed at all, otherwise subscribed
        // with an expiry strictly in the past. The legacy comparison is `expiryDate < Date.Today`
        // guarded by `Not Null.IsNull(expiryDate)`, so an assignment with no expiry never reads as
        // lapsed however old it is - a perpetual membership is not an expired one.
        bool expired = subscribed
            && assignment!.ExpiryDate is DateTime expiry
            && expiry.Date < today.Date;

        bool chargesFee = role.ServiceFee is decimal serviceFee && serviceFee > 0m;
        bool chargesTrialFee = role.TrialFee is decimal trialFee && trialFee > 0m;

        return new MemberServiceDto
        {
            RoleId = role.RoleId,
            RoleName = role.RoleName,
            Description = role.Description,
            ServiceFee = role.ServiceFee,
            BillingPeriod = role.BillingPeriod,
            BillingFrequency = role.BillingFrequency,
            TrialFee = role.TrialFee,
            TrialPeriod = role.TrialPeriod,
            TrialFrequency = role.TrialFrequency,
            EffectiveDate = assignment?.EffectiveDate,
            ExpiryDate = assignment?.ExpiryDate,
            IsSubscribed = subscribed,
            IsTrialUsed = trialUsed,
            IsExpired = expired,
            SubscriptionAction = !subscribed
                ? MemberServiceActions.Subscribe
                : expired
                    ? MemberServiceActions.Renew
                    : MemberServiceActions.Unsubscribe,

            // ShowSubscribe: public - which every row of this catalogue is, since the read selects on
            // IsPublic - and either no fee to take or a processor account to take it with. The role's
            // own flag is still tested rather than assumed, so this mapper is correct for any caller.
            SubscriptionOffered = role.IsPublic && (!chargesFee || tenantTakesPayment),
            SubscriptionRequiresPayment = chargesFee,

            // ShowTrial: its first arm returns FALSE for a public role with no fee - a free service has
            // nothing to trial - and its second offers the trial only when the trial itself is free and
            // this account has not already consumed it.
            TrialOffered = role.IsPublic && chargesFee && !chargesTrialFee && !trialUsed,
        };
    }

    /// <summary>
    /// Projects a role an invitation code enrolled an account in onto the redemption result row.
    /// </summary>
    /// <param name="role">The role the account was enrolled in.</param>
    /// <returns>The result row.</returns>
    /// <remarks>
    /// Deliberately two members. The legacy container's confirmation interpolated the role NAME
    /// (<c>Website/admin/Users/ManageUsers.ascx.vb:L847</c>, reached through the event argument the
    /// panel raised at <c>MemberServices.ascx.vb:L418</c>), and the key is carried so a client can
    /// locate the affected row in the catalogue it already holds. Everything else about the service is
    /// in that catalogue, and a thinner second copy here would be a second thing to keep consistent.
    /// </remarks>
    public static RedeemedServiceDto ToRedeemedService(RoleEntity role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RedeemedServiceDto
        {
            RoleId = role.RoleId,
            RoleName = role.RoleName,
        };
    }
}
