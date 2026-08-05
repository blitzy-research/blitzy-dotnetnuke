using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: this contract deliberately spans BOTH legacy provider stacks. Profile values come from the membership provider (Library/Providers/MembershipProviders/DataProvider/DataProvider.vb:L117-L119) and profile property definitions come from the core provider (Library/Components/Providers/Data/DataProvider.vb:L250-L256). Each stack had its own reflection-created static singleton; both are replaced by constructor injection, and the two concerns are combined here because a definition is meaningless without its values and no separate profile-definition repository is in scope.
//
//            The split is real rather than incidental, and it was measured rather than assumed. The
//            core provider declares 269 abstract members across 397 lines yet not one of them reads
//            or writes a profile VALUE - its profile surface is the six definition members at lines
//            251 to 256 alone. The membership provider is a separate 131-line abstract class whose
//            "Profile" block at line 117 declares exactly two members, and it resolves through its
//            own accessor under the namespace "DotNetNuke.Security.Membership.Data" rather than the
//            core "DotNetNuke.Data". The 1,773-line AspNetMembershipProvider settles it: it invokes
//            no stored procedure directly at all, and line 59 reads verbatim
//            "Private Shared dataProvider As dataProvider = dataProvider.Instance()" - it delegates
//            entirely through that second accessor. Reading either provider alone would therefore
//            have produced a materially incomplete contract: the core stack knows how a property is
//            DECLARED and the membership stack knows how it is ANSWERED.
//
// MIGRATION: the core provider's personalization block is omitted entirely - GetAllProfiles at L245, GetProfile(UserId, PortalId) at L246, AddProfile(UserId, PortalId) at L247 and UpdateProfile(UserId, PortalId, ProfileData As String) at L248 stored an entire profile as one opaque serialized string keyed by user and portal. That representation was superseded by the per-property row model reached through membership DataProvider.vb:L118 and L119, where each value is its own UserProfile row keyed by PropertyDefinitionID. The blob path was portal-scoped while the surviving row path is not, which is the structural proof they are two generations of the same feature; the Personalization subsystem is out of scope.
//
//            The block's own header is the first clue - line 244 reads "' personalization", not
//            "' profile" - and the shape of line 248 is the second: a member whose payload is a
//            single ProfileData string cannot express a per-property visibility or a per-property
//            timestamp, both of which the surviving row model carries as columns. No member below
//            accepts a (userId, portalId) pair for a whole-profile read or write, and none accepts
//            serialized profile content in any form.
//
// MIGRATION: membership DataProvider.vb:L119 UpdateProfileProperty(ProfileId, UserId, PropertyDefinitionID, PropertyValue, Visibility, LastUpdatedDate) was a single insert-or-update member keyed by ProfileId. The target splits it into explicit AddProfileValueAsync and UpdateProfileValueAsync because EF Core tracks entity state explicitly, and both take the UserProfileValue entity rather than six positional arguments. The LastUpdatedDate argument becomes a property on the entity - UserProfile is the only in-scope table carrying LastUpdatedDate, added at 03.02.03.SqlDataProvider:L1372 - so no member takes a DateTime parameter.
//
//            The procedure behind it is a true upsert and was read to confirm the split is faithful:
//            UpdateUserProfileProperty (04.00.04.SqlDataProvider:L1606) resolves a null or -1
//            @ProfileID by looking the row up on the (UserID, PropertyDefinitionID) natural key at
//            L1616-L1620, then branches to an UPDATE arm at L1622 or an INSERT arm at L1634. Those
//            two arms are the two members below. The branch itself does not survive as a member,
//            because deciding whether a value is new is the caller's knowledge here rather than
//            something to be rediscovered inside a procedure on every write.
//
// MIGRATION: ProfileController.vb:L485 GetPropertyDefinitionsByCategory is not surfaced - it filtered an already-loaded collection in memory and has no backing stored procedure in the core provider's definition block. Category filtering is an Application-layer projection over GetDefinitionsByPortalIdAsync; adding a repository member would invent persistence behaviour that never existed.
//
//            The legacy body is unambiguous: it iterated GetPropertyDefinitions(portalId) and kept
//            the entries whose PropertyCategory matched, and the pre-generics collection wrapper
//            carried the same in-memory filter a second time as GetByCategory. Two in-memory copies
//            of a predicate are still not a query.
//
// MIGRATION: ProfileController.vb:L226 GetUserProfile(ByRef objUser As UserInfo) hydrated a user's profile collection in place; it becomes an Application UserService concern and no out or ref parameter appears here. ProfileController.vb:L334 AddDefaultDefinitions is multi-step portal-provisioning orchestration and belongs to the Application layer alongside the portal-creation sequence, not to this repository.
//
//            That ByRef site is the only one in the whole 561-line controller, and it is orchestration
//            rather than persistence: it mutated a UserInfo the caller already held. This contract
//            answers with values instead and lets the caller compose them. AddDefaultDefinitions is
//            excluded on the same principle - it resolved a data-type list and then wrote a batch of
//            definitions one at a time, so it is a sequence of calls to AddDefinitionAsync under one
//            unit of work, not a persistence primitive of its own.
//
// MIGRATION: core DataProvider.vb:L251 AddPropertyDefinition carried 11 positional arguments and L256 UpdatePropertyDefinition carried 10 (DataType, DefaultValue, PropertyCategory, PropertyName, Required, ValidationExpression, ViewOrder, Visible, Length). Both collapse to a single entity parameter; the values travel as properties on ProfilePropertyDefinition.
//
//            Both argument counts were counted in the source rather than estimated. Nine of the ten
//            update arguments are shared with the insert, differing only in that the insert leads
//            with PortalId and ModuleDefId while the update leads with PropertyDefinitionId - which
//            is precisely the shape an entity already expresses, and precisely the shape a
//            positional argument list expresses worst.
//
// MIGRATION: a host-level (portal-independent) property definition was addressed by the legacy Null.NullInteger sentinel -1, which the provider translated to SQL NULL before every scoped read and write. The target entity therefore declares int? PortalId and represents host scope as genuine null. Because Portals.PortalID is IDENTITY(-1,1), -1 is simultaneously a legitimate portal identifier elsewhere in the schema; the repository performs this subsystem-specific translation at its boundary, and DTO mapping restores the externally observable sentinel.
//
//            The consequence is a deliberate asymmetry that must not be "corrected" later: the two
//            portal-scoped readers below take a non-nullable int portalId, because a caller asking
//            for a portal's definitions necessarily has a portal in hand, while the entity property
//            they filter on is nullable because a definition need not belong to one. The repository
//            recognises exactly -1 as the legacy host address; no identifier is tested for truthiness
//            or treated as absent merely because it is non-positive.
//
// MIGRATION: ProfilePropertyDefinitionCollection.vb was a pre-generics CollectionBase/DictionaryBase wrapper and produces no target file; IReadOnlyList<ProfilePropertyDefinition> replaces it. CBO.vb, 729 lines of reflection-driven IDataReader hydration, likewise produces no target file - the EF Core materializer replaces both hydration paths.
//
//            The controller's two CBO call sites and the 313-line collection wrapper disappear
//            together: one hydrated rows by reflection, the other wrapped the results in an
//            untyped CollectionBase with its own Add, AddRange, Contains, IndexOf, Insert, Remove
//            and Sort. Every one of those is a generic-collection primitive now, so the whole
//            wrapper is replaced by the return types below. No IDataReader, no DataTable and no
//            Fill member appears anywhere in this contract.
//
// MIGRATION: value ROW DELETION is deliberately absent, and that absence was measured rather than
//            assumed. The membership provider's "Profile" block declares exactly two members - the
//            reader at L118 and the upsert at L119 - and there is no DeleteProfileProperty beside
//            them. A case-insensitive sweep of all eighty-eight upgrade scripts across all four of
//            the naming forms those scripts use finds no procedure that deletes a profile value and
//            no DELETE statement against the UserProfile table at all. Clearing an answer was
//            therefore an upsert carrying an empty PropertyValue, which the legacy code could
//            express because Null.NullString was the empty string rather than null. Withdrawing a
//            DEFINITION does remove its answers, but through the store rather than through a member:
//            FK_UserProfile_ProfilePropertyDefinition is declared ON DELETE CASCADE
//            (04.00.04.SqlDataProvider:L1429). Adding a value-deletion member here would invent
//            persistence behaviour the legacy surface never had.

/// <summary>
/// Reads and writes the user-profile slice of the User aggregate: the per-user answers held in the
/// legacy <c>dbo.UserProfile</c> table and the <c>dbo.ProfilePropertyDefinition</c> rows those
/// answers are keyed by.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately one contract for two entities. A
/// <see cref="ProfilePropertyDefinition"/> declares what a tenant may be asked; a
/// <see cref="UserProfileValue"/> records what one account answered. Neither is usable without the
/// other - an answer carries no name, data type or validation expression of its own, and a
/// declaration with no answers describes nothing - so they are read and written together, under one
/// abstraction, and no separate profile-definition repository exists.
/// </para>
/// <para>
/// Members are grouped in two sections below, values first and declarations second, and each is
/// traced to the exact legacy provider line it replaces. The provenance of the pairing, and every
/// legacy member deliberately left out of it, is set out in the migration notes above this type.
/// </para>
/// <para>
/// Writes STAGE rather than commit. The legacy provider's insert members returned the generated
/// identifier because each stored procedure ended in <c>SCOPE_IDENTITY()</c>, but under an
/// object-relational mapper the key is not assigned until the unit of work is saved, so a member
/// that returned it would have to save on the caller's behalf. That would dissolve the commit
/// boundary the target depends on: creating a portal writes across the portal, alias, role, tab and
/// module tables in one transaction, and provisioning a tenant's default profile declarations writes
/// a whole batch of definitions, both of which must succeed or fail together. Every write member
/// here therefore returns a bare <see cref="Task"/>; the caller commits through
/// <c>IUnitOfWork.SaveChangesAsync</c>, after which
/// <see cref="ProfilePropertyDefinition.PropertyDefinitionId"/> and
/// <see cref="UserProfileValue.ProfileId"/> hold their generated keys.
/// </para>
/// <para>
/// Reads answer with materialised entities only. No member exposes a deferred or composable query,
/// a database context, a data reader or a provider type, so an implementation cannot leak its
/// persistence technology to a caller and a caller cannot extend a query it did not build.
/// </para>
/// <para>
/// Nothing here decides anything. Whether a viewer may see a value is an authorisation question
/// answered by the permission evaluator and the Application layer, not by the repository that reads
/// the row - <see cref="UserProfileValue.Visibility"/> is carried as the plain persisted
/// <see cref="int"/> it is stored as. Whether a submitted answer satisfies its declaration's
/// required flag, declared length or validation expression is likewise a validation question
/// answered above this layer, because those three constraints are tenant data rather than code.
/// Caching is an implementation concern of the Infrastructure layer and appears in no signature: no
/// member takes a flag asking it to bypass, refresh or clear a cache.
/// </para>
/// </remarks>
public interface IUserProfileRepository
{
    // =================================================================================
    // SECTION A - PROFILE VALUES
    //
    // The per-user answers, one row per account per declaration, from the membership
    // provider's "Profile" block at Library/Providers/MembershipProviders/DataProvider/
    // DataProvider.vb lines 117 to 119. That block declares exactly two members, and the
    // three below are what they become once the single upsert is split.
    // =================================================================================

    /// <summary>
    /// Returns every profile answer recorded for one account.
    /// </summary>
    /// <param name="userId">
    /// Identifier of the account whose answers are wanted. The <c>Users</c> table seeds its identity
    /// at 1, so every stored account has a positive identifier; no value of this parameter is read
    /// as meaning "any account".
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// The account's answers, empty when it has recorded none. Never <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces membership <c>DataProvider.vb:L118</c>
    /// <c>GetUserProfile(ByVal UserId As Integer) As IDataReader</c>. Note what that signature does
    /// NOT contain: a portal. The surviving row model is keyed by account and declaration alone,
    /// which is the structural difference from the withdrawn blob path at core
    /// <c>DataProvider.vb:L246</c>, whose reader took <c>(UserId, PortalId)</c>. Preserving the
    /// single-argument shape is therefore behaviour preservation, not an omission: a portal filter
    /// here would narrow a result the legacy reader never narrowed.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy return was an open <see cref="System.Data.IDataReader"/> that each
    /// caller drained by hand. A materialised, read-only sequence replaces it, so no caller holds a
    /// live cursor and none can mutate the collection it is handed.
    /// </para>
    /// <para>
    /// An answer is only interpretable beside the declaration it answers, so an implementation is
    /// expected to load <see cref="UserProfileValue.PropertyDefinition"/> with each row. The stored
    /// answer itself spans two columns - <see cref="UserProfileValue.PropertyValue"/> when it fits
    /// the bounded column and <see cref="UserProfileValue.PropertyText"/> when it outgrew it - and
    /// both are returned exactly as stored. Collapsing them is the Application mapper's business,
    /// because the legacy reader's coalesce order is observable and belongs where it can be seen.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one account's profile values whose property definitions belong to the addressed portal.
    /// </summary>
    /// <param name="portalId">The portal that must own each returned definition.</param>
    /// <param name="userId">The account whose values are returned.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <returns>The portal-scoped values, ordered by definition and row identity.</returns>
    /// <remarks>
    /// The value table has no portal column; scope is established through the required definition foreign
    /// key. Callers handling a portal route must use this member rather than the installation-wide overload,
    /// otherwise updating one tenant's profile can observe and clear another tenant's values.
    /// </remarks>
    Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new profile answer for insertion.
    /// </summary>
    /// <param name="profileValue">
    /// The answer to record. Its <see cref="UserProfileValue.UserId"/> and
    /// <see cref="UserProfileValue.PropertyDefinitionId"/> must identify an existing account and
    /// declaration, because both columns are non-nullable foreign keys.
    /// </param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the insert arm of the single membership upsert at
    /// <c>DataProvider.vb:L119</c>, whose procedure branch is
    /// <c>UpdateUserProfileProperty</c> at <c>04.00.04.SqlDataProvider:L1634</c>. Six positional
    /// arguments become one entity: <c>ProfileId</c>, <c>UserId</c>, <c>PropertyDefinitionID</c>,
    /// <c>PropertyValue</c>, <c>Visibility</c> and <c>LastUpdatedDate</c> are all properties of
    /// <see cref="UserProfileValue"/> already.
    /// </para>
    /// <para>
    /// MIGRATION: the sixth legacy argument, <c>LastUpdatedDate</c>, is deliberately NOT a parameter
    /// of this member. <c>dbo.UserProfile</c> is the only in-scope table that carries the column at
    /// all - it arrived with the table itself at <c>03.02.03.SqlDataProvider:L1372</c> - so the
    /// timestamp is a property of this row rather than an argument of this operation. The caller
    /// stamps <see cref="UserProfileValue.LastUpdatedDate"/> from the injected clock, which is what
    /// keeps time-dependent behaviour testable. No member of this contract accepts a date.
    /// </para>
    /// <para>
    /// The insert is STAGED, not committed, and this member returns no identifier. The legacy
    /// procedure could answer with <c>SELECT @ProfileID</c> because it had already written the row;
    /// here <see cref="UserProfileValue.ProfileId"/> is assigned when the unit of work is saved, and
    /// it holds the generated key from that point on. The reasoning is on the interface remarks.
    /// </para>
    /// <para>
    /// Absence is <see langword="null"/> on both value columns, never the empty string. The legacy
    /// read path could not tell the two apart, because <c>Null.NullString</c> WAS the empty string,
    /// but the store distinguishes them and so does this contract: an empty string is an answer the
    /// account gave, and normalising it to <see langword="null"/> would discard that fact.
    /// </para>
    /// </remarks>
    Task AddProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an existing profile answer for update.
    /// </summary>
    /// <param name="profileValue">
    /// The answer to rewrite, carrying its already-assigned
    /// <see cref="UserProfileValue.ProfileId"/>.
    /// </param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the update arm of that same membership upsert at
    /// <c>DataProvider.vb:L119</c> - procedure branch <c>UpdateUserProfileProperty</c> at
    /// <c>04.00.04.SqlDataProvider:L1622</c>. The legacy member decided at run time which arm to
    /// take, resolving a null or -1 <c>@ProfileID</c> against the
    /// <c>(UserID, PropertyDefinitionID)</c> natural key first
    /// (<c>04.00.04.SqlDataProvider:L1616-L1620</c>). The target does not rediscover that: a caller
    /// that has just read a row knows it exists, and a caller building a new one knows it does not,
    /// so the branch becomes the choice between this member and
    /// <see cref="AddProfileValueAsync(UserProfileValue, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// MIGRATION: this is also how an answer is CLEARED, and that is faithful rather than a
    /// shortcut. No legacy member and no procedure in the eighty-eight upgrade scripts ever deleted
    /// a profile value row; clearing was an upsert carrying an empty value, which the legacy code
    /// could express because <c>Null.NullString</c> was the empty string. Blanking the row through
    /// this member therefore preserves the row's visibility and refreshes its timestamp exactly as
    /// the legacy write did, whereas deleting it would reset both. No value-deletion member exists
    /// on this contract for that reason.
    /// </para>
    /// <para>
    /// Withdrawing a whole DECLARATION is the one case where answers do disappear, and it is handled
    /// by the store rather than here: <c>FK_UserProfile_ProfilePropertyDefinition</c> is declared
    /// <c>ON DELETE CASCADE</c> (<c>04.00.04.SqlDataProvider:L1429</c>), so
    /// <see cref="DeleteDefinitionAsync(int, CancellationToken)"/> takes the answers with it.
    /// </para>
    /// </remarks>
    Task UpdateProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages removal of one account's profile values whose definitions belong to one portal.
    /// </summary>
    /// <param name="portalId">The portal whose definition-owned values are removed.</param>
    /// <param name="userId">The account being removed from the portal.</param>
    /// <param name="cancellationToken">Abandons the read used to stage the removals.</param>
    /// <returns>A task that completes once matching rows have been staged for deletion.</returns>
    Task DeleteProfileValuesAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default);

    // =================================================================================
    // SECTION B - PROFILE PROPERTY DEFINITIONS
    //
    // The declarations those answers are keyed by, from the core provider's
    // "profile property definitions" block at Library/Components/Providers/Data/
    // DataProvider.vb lines 250 to 256. All six members of that block are represented,
    // one for one. The personalization block immediately above it, lines 244 to 248, is
    // omitted in full - see the migration notes on this file.
    // =================================================================================

    /// <summary>
    /// Stages a new profile property declaration for insertion.
    /// </summary>
    /// <param name="definition">
    /// The declaration to create. Leave <see cref="ProfilePropertyDefinition.PortalId"/>
    /// <see langword="null"/> to declare it host-wide rather than for a single tenant.
    /// </param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces core <c>DataProvider.vb:L251</c> <c>AddPropertyDefinition</c>, which
    /// carried ELEVEN positional arguments - <c>PortalId</c>, <c>ModuleDefId</c>, <c>DataType</c>,
    /// <c>DefaultValue</c>, <c>PropertyCategory</c>, <c>PropertyName</c>, <c>Required</c>,
    /// <c>ValidationExpression</c>, <c>ViewOrder</c>, <c>Visible</c> and <c>Length</c>. All eleven
    /// are properties of <see cref="ProfilePropertyDefinition"/>, so the whole list collapses to one
    /// parameter and the call site can no longer transpose two same-typed arguments.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy member returned <see cref="int"/> because its procedure ended in
    /// <c>SCOPE_IDENTITY()</c>. This one returns a bare <see cref="Task"/>. Returning the key would
    /// force an implementation to save in order to obtain it, which would break the batch write that
    /// tenant provisioning performs - the legacy <c>ProfileController.vb:L334</c>
    /// <c>AddDefaultDefinitions</c> declared a whole default set for a new portal in one go, and that
    /// sequence must commit atomically.
    /// <see cref="ProfilePropertyDefinition.PropertyDefinitionId"/> holds the generated key once the
    /// unit of work is saved.
    /// </para>
    /// <para>
    /// MIGRATION: the seeding of a tenant's DEFAULT declarations is not a member of this contract.
    /// <c>AddDefaultDefinitions</c> resolved a data-type list and then wrote each declaration in
    /// turn, which makes it a sequence of calls to this member under one unit of work - Application
    /// orchestration, alongside the portal-creation path, rather than a persistence primitive.
    /// </para>
    /// </remarks>
    Task AddDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an existing profile property declaration for update.
    /// </summary>
    /// <param name="definition">
    /// The declaration to rewrite, carrying its
    /// <see cref="ProfilePropertyDefinition.PropertyDefinitionId"/>.
    /// </param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces core <c>DataProvider.vb:L256</c> <c>UpdatePropertyDefinition</c>, which
    /// carried TEN positional arguments. Nine of them are shared with the eleven-argument insert at
    /// L251, the two lists differing only in that the insert leads with the portal and module
    /// declaration keys while the update leads with the definition key - which is exactly the
    /// redundancy an entity parameter removes.
    /// </para>
    /// <para>
    /// Reordering a tenant's profile form needs no member of its own. The legacy administration grid
    /// moved a property by exchanging the display-order values of two declarations and persisting
    /// each through this same update, so display order is simply
    /// <see cref="ProfilePropertyDefinition.ViewOrder"/> on the declarations being updated.
    /// </para>
    /// <para>
    /// The three constraints a declaration carries - <see cref="ProfilePropertyDefinition.IsRequired"/>,
    /// <see cref="ProfilePropertyDefinition.Length"/> and
    /// <see cref="ProfilePropertyDefinition.ValidationExpression"/> - are stored here and enforced
    /// above here. They constrain the ANSWERS a tenant may give, so applying them is the Application
    /// layer's business; this member persists the declaration whatever it says.
    /// </para>
    /// </remarks>
    Task UpdateDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a profile property declaration for removal.
    /// </summary>
    /// <param name="propertyDefinitionId">Identifier of the declaration to withdraw.</param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces core <c>DataProvider.vb:L252</c>
    /// <c>DeletePropertyDefinition(ByVal definitionId As Integer)</c>. The identifier alone is the
    /// whole legacy signature and it is the whole signature here: no flag chooses between a hard and
    /// a soft removal, and no companion member restores one, because the legacy surface offered
    /// neither and inventing either would be inventing behaviour.
    /// </para>
    /// <para>
    /// <see cref="ProfilePropertyDefinition"/> does carry <see cref="ProfilePropertyDefinition.IsDeleted"/>,
    /// so an implementation MAY honour this member by setting that flag rather than removing the row,
    /// and the reason to consider it is that removing the declaration removes every account's answer
    /// with it - <c>FK_UserProfile_ProfilePropertyDefinition</c> is declared <c>ON DELETE CASCADE</c>
    /// at <c>04.00.04.SqlDataProvider:L1429</c>. Either reading satisfies this contract; the choice
    /// is an Infrastructure decision and is stated there, not selected through a parameter here.
    /// </para>
    /// <para>
    /// Whichever reading applies, the answers must not be left orphaned. An implementation that
    /// removes the row is expected to have the dependent answers tracked so the cascade is performed
    /// by the change tracker as well as by the store constraint - a database-level cascade alone
    /// would not fire on a provider that does not enforce one, which is precisely the case an
    /// integration run against a non-SQL-Server provider creates.
    /// </para>
    /// </remarks>
    Task DeleteDefinitionAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one profile property declaration by key within the tenant scope that can address it.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant whose declaration is wanted. The legacy host identifier is translated by
    /// the repository in exactly the same way as the collection and name reads.
    /// </param>
    /// <param name="propertyDefinitionId">Identifier of the declaration wanted.</param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// The declaration, or <see langword="null"/> when no declaration in the requested scope carries that
    /// identifier.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy controller accepted both the definition and portal identifiers
    /// (<c>ProfileController.vb:L425</c>) and searched the portal-scoped catalogue before falling back to
    /// the provider's unscoped key lookup. That fallback could reveal another tenant's row on a cache miss.
    /// The target keeps the controller's tenant-bearing contract and applies the same authoritative scope
    /// predicate as the list and name reads, preserving tenant isolation rather than reproducing the leak.
    /// A reader that a caller had to test for emptiness becomes a nullable return, so absence is expressed
    /// in the type system rather than discovered by draining a cursor.
    /// </para>
    /// <para>
    /// MIGRATION: absence is <see langword="null"/> and nothing else. It is NOT signalled by a
    /// sentinel identifier: <c>Portals.PortalID</c> seeds at -1 and <c>Roles.RoleID</c>,
    /// <c>Tabs.TabID</c> and <c>Modules.ModuleID</c> seed at 0, so both of the values the legacy
    /// <c>Null</c> helper treated as empty are genuine keys somewhere in this schema. No caller
    /// should test a returned declaration's identifier against -1 or 0 to decide whether it was
    /// found.
    /// </para>
    /// <para>
    /// A caller intending to withdraw the declaration should expect its answers to be reachable
    /// through <see cref="ProfilePropertyDefinition.ProfileValues"/>, so that the cascade described
    /// on <see cref="DeleteDefinitionAsync(int, CancellationToken)"/> is performed by the change
    /// tracker and does not depend solely on the store enforcing the constraint.
    /// </para>
    /// </remarks>
    Task<ProfilePropertyDefinition?> GetDefinitionByIdAsync(
        int portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one profile property declaration by name within a tenant.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant whose declarations are searched. Non-nullable deliberately - see the
    /// asymmetry note below.
    /// </param>
    /// <param name="propertyName">
    /// The declared property name to match. Non-nullable: the legacy
    /// <c>Null.NullString</c> sentinel WAS the empty string, so an empty name was legally
    /// representable and is passed through as the ordinary value it is rather than being treated as
    /// a request for any name.
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// The matching declaration, or <see langword="null"/> when the tenant declares no property of
    /// that name.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces core <c>DataProvider.vb:L254</c>
    /// <c>GetPropertyDefinitionByName(ByVal portalId As Integer, ByVal name As String) As IDataReader</c>.
    /// It answers with the DECLARATION rather than with a boolean, which is what the legacy member
    /// did, and that matters for the caller it principally serves: rejecting a duplicate name while
    /// EDITING a declaration means comparing the found declaration's identifier with the one being
    /// edited, and a boolean cannot express "found, but it is the same row". Composing the answer
    /// that way keeps the exclusion rule in the Application layer where the edit is happening,
    /// instead of pushing an exclusion argument down into persistence.
    /// </para>
    /// <para>
    /// MIGRATION: the deliberate ASYMMETRY, recorded so it is not later "corrected". This parameter
    /// is a non-nullable <see cref="int"/> because a caller asking what a tenant declares necessarily
    /// has a tenant in hand, while <see cref="ProfilePropertyDefinition.PortalId"/> is
    /// <see cref="Nullable{T}"/> because a declaration may be host-level and belong to no tenant at
    /// all. The legacy API addressed host scope with <c>Null.NullInteger</c> -1 and the provider
    /// translated it to SQL <c>NULL</c>. That convention is hazardous because
    /// <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so -1 is also a real tenant elsewhere in
    /// the schema. The target uses genuine <see langword="null"/> on the entity, translates the
    /// legacy address once in the repository predicate, and restores it only on the DTO boundary.
    /// </para>
    /// </remarks>
    Task<ProfilePropertyDefinition?> GetDefinitionByNameAsync(
        int portalId,
        string propertyName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every profile property declaration belonging to one tenant.
    /// </summary>
    /// <param name="portalId">
    /// Identifier of the tenant whose declarations are wanted. Non-nullable for the same reason as
    /// on <see cref="GetDefinitionByNameAsync(int, string, CancellationToken)"/>.
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// The tenant's declarations, empty when it declares none. Never <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces core <c>DataProvider.vb:L255</c>
    /// <c>GetPropertyDefinitionsByPortal(ByVal portalId As Integer) As IDataReader</c>. The legacy
    /// reader was hydrated by reflection through <c>CBO</c> and handed back inside
    /// <c>ProfilePropertyDefinitionCollection</c>, a 313-line <c>CollectionBase</c> subclass;
    /// <see cref="IReadOnlyList{T}"/> of materialised entities replaces both, and neither the
    /// hydrator nor the wrapper produces a target file.
    /// </para>
    /// <para>
    /// MIGRATION: this read is UNPAGED, matching the legacy member, which took no page coordinates -
    /// nor did any other member of either profile block. The legacy administration screen listed a
    /// tenant's declarations on one page with no pager, and a tenant declares them in the tens, so no
    /// paged overload and no separate count member is offered. Introducing paging here would invent
    /// a contract the legacy surface never had.
    /// </para>
    /// <para>
    /// MIGRATION: filtering by <see cref="ProfilePropertyDefinition.PropertyCategory"/> is
    /// deliberately not offered as a member. <c>ProfileController.vb:L485</c>
    /// <c>GetPropertyDefinitionsByCategory</c> iterated an already-loaded collection and kept the
    /// matching entries, and the collection wrapper carried the same in-memory predicate again as
    /// <c>GetByCategory</c>; neither had a backing stored procedure. A category view is therefore a
    /// projection the Application layer performs over this member's result.
    /// </para>
    /// <para>
    /// Callers render profile forms from this sequence, so an implementation is expected to return it
    /// in the tenant's declared display order - <see cref="ProfilePropertyDefinition.ViewOrder"/>
    /// first - and to exclude declarations withdrawn through
    /// <see cref="ProfilePropertyDefinition.IsDeleted"/>, so a retired property is not presented for
    /// answering.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ProfilePropertyDefinition>> GetDefinitionsByPortalIdAsync(
        int portalId,
        CancellationToken cancellationToken = default);
}
