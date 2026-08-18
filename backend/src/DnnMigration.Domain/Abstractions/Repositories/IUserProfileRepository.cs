using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the user-profile slice of the User aggregate: the per-user answers held in the legacy
/// <c>dbo.UserProfile</c> table and the <c>dbo.ProfilePropertyDefinition</c> rows those answers are keyed
/// by.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately one contract for two entities. A <see cref="ProfilePropertyDefinition"/>
/// declares what a tenant may be asked; a <see cref="UserProfileValue"/> records what one account answered.
/// </para>
/// <para>
/// Writes STAGE rather than commit. The legacy provider's insert members returned the generated identifier
/// because each stored procedure ended in <c>SCOPE_IDENTITY()</c>, but under an object-relational mapper
/// the key is not assigned until the unit of work is saved, so a member that returned it would have to save
/// on the caller's behalf.
/// </para>
/// </remarks>
public interface IUserProfileRepository
{
    // SECTION A - PROFILE VALUES

    /// <summary>Returns every profile answer recorded for one account.</summary>
    /// <param name="userId">Identifier of the account whose answers are wanted.</param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>The account's answers, empty when it has recorded none.</returns>
    /// <remarks>
    /// An answer is only interpretable beside the declaration it answers, so an implementation is expected
    /// to load <see cref="UserProfileValue.PropertyDefinition"/> with each row.
    /// </remarks>
    Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one account's profile values whose property definitions belong to the addressed scope.
    /// </summary>
    /// <param name="portalId">
    /// The tenant that must own each returned definition, matched EXACTLY - including the real keys -1 and
    /// 0 - or <see langword="null"/> for the host scope, meaning the definitions whose <c>PortalID</c>
    /// column is SQL <c>NULL</c>.
    /// </param>
    /// <param name="userId">The account whose values are returned.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <returns>The scoped values, ordered by definition and row identity.</returns>
    /// <remarks>
    /// The value table has no portal column; scope is established through the required definition foreign
    /// key. Callers handling a portal route must use this member rather than the installation-wide
    /// overload, otherwise updating one tenant's profile can observe and clear another tenant's values.
    /// </remarks>
    Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int? portalId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the scoped profile values of MANY accounts in one read.</summary>
    /// <param name="portalId">
    /// The tenant that must own each returned definition, matched EXACTLY - including the real keys -1 and
    /// 0 - or <see langword="null"/> for the host scope, meaning the definitions whose <c>PortalID</c>
    /// column is SQL <c>NULL</c>.
    /// </param>
    /// <param name="userIds">The accounts whose values are wanted.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <returns>
    /// The scoped values of every named account, FLAT rather than grouped, so a caller groups by <see
    /// cref="UserProfileValue.UserId"/> itself.
    /// </returns>
    /// <remarks>
    /// The set-based form of the scoped single-account overload, and it exists for one reason: an account
    /// LISTING that projects a profile value needs the values of the accounts on its page, and asking for
    /// them one account at a time makes the read cost proportional to the page size - which is a per-row
    /// round trip in everything but name.
    /// </remarks>
    Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int? portalId,
        IReadOnlyCollection<int> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a new profile answer for insertion.</summary>
    /// <param name="profileValue">The answer to record.</param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    /// <remarks>
    /// The sixth legacy argument, <c>LastUpdatedDate</c>, is deliberately NOT a parameter of this member.
    /// <c>dbo.UserProfile</c> is the only in-scope table that carries the column at all - it arrived with
    /// the table itself at <c>03.02.03.SqlDataProvider:L1372</c> - so the timestamp is a property of this
    /// row rather than an argument of this operation.
    /// </remarks>
    Task AddProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default);

    /// <summary>Stages an existing profile answer for update.</summary>
    /// <param name="profileValue">
    /// The answer to rewrite, carrying its already-assigned <see cref="UserProfileValue.ProfileId"/>.
    /// </param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    Task UpdateProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default);

    /// <summary>Stages removal of one account's profile values whose definitions belong to one portal.</summary>
    /// <param name="portalId">
    /// The tenant whose definition-owned values are removed, matched EXACTLY, or <see langword="null"/> for
    /// the host scope.
    /// </param>
    /// <param name="userId">The account being removed from the portal.</param>
    /// <param name="cancellationToken">Abandons the read used to stage the removals.</param>
    /// <returns>A task that completes once matching rows have been staged for deletion.</returns>
    Task DeleteProfileValuesAsync(
        int? portalId,
        int userId,
        CancellationToken cancellationToken = default);

    // SECTION B - PROFILE PROPERTY DEFINITIONS

    /// <summary>Stages a new profile property declaration for insertion.</summary>
    /// <param name="definition">The declaration to create.</param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    /// <remarks>
    /// The seeding of a tenant's DEFAULT declarations is not a member of this contract.
    /// <c>AddDefaultDefinitions</c> resolved a data-type list and then wrote each declaration in turn,
    /// which makes it a sequence of calls to this member under one unit of work - Application
    /// orchestration, alongside the portal-creation path, rather than a persistence primitive.
    /// </remarks>
    Task AddDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>Stages an existing profile property declaration for update.</summary>
    /// <param name="definition">
    /// The declaration to rewrite, carrying its <see
    /// cref="ProfilePropertyDefinition.PropertyDefinitionId"/>.
    /// </param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    /// <remarks>
    /// Reordering a tenant's profile form needs no member of its own. The legacy administration grid moved
    /// a property by exchanging the display-order values of two declarations and persisting each through
    /// this same update, so display order is simply <see cref="ProfilePropertyDefinition.ViewOrder"/> on
    /// the declarations being updated.
    /// </remarks>
    Task UpdateDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>Counts the recorded answers that withdrawing one declaration would destroy.</summary>
    /// <param name="propertyDefinitionId">Identifier of the declaration whose answers are counted.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <returns>The number of <c>UserProfile</c> rows referencing the declaration; zero when it has none.</returns>
    /// <remarks>
    /// <para>
    /// Exists so a caller can state the SIZE of a cascade before performing it. Removing a declaration takes
    /// its answers with it - see <see cref="DeleteDefinitionAsync"/> - and that consequence is invisible from
    /// the declaration alone, so the count is the one fact an operator needs in order to consent to it.
    /// </para>
    /// <para>
    /// Counted in the store rather than by loading the rows: the answer is a number, the rows can span every
    /// account in the tenant, and nothing here needs their content.
    /// </para>
    /// </remarks>
    Task<int> CountProfileValuesForDefinitionAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a profile property declaration for removal.</summary>
    /// <param name="propertyDefinitionId">Identifier of the declaration to withdraw.</param>
    /// <param name="cancellationToken">Token observed while the write is staged.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    /// <remarks>
    /// Whichever reading applies, the answers must not be left orphaned.
    /// </remarks>
    Task DeleteDefinitionAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one profile property declaration by key within the tenant scope that can address it.
    /// </summary>
    /// <param name="portalId">
    /// The tenant whose declaration is wanted, matched EXACTLY - including the real keys -1 and 0 - or <see
    /// langword="null"/> for the host scope, in exactly the same way as the collection and name reads.
    /// </param>
    /// <param name="propertyDefinitionId">Identifier of the declaration wanted.</param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// The declaration, or <see langword="null"/> when no declaration in the requested scope carries that
    /// identifier.
    /// </returns>
    /// <remarks>
    /// Absence is <see langword="null"/> and nothing else. It is NOT signalled by a sentinel identifier:
    /// <c>Portals.PortalID</c> seeds at -1 and <c>Roles.RoleID</c>, <c>Tabs.TabID</c> and
    /// <c>Modules.ModuleID</c> seed at 0, so both of the values the legacy <c>Null</c> helper treated as
    /// empty are genuine keys somewhere in this schema.
    /// </remarks>
    Task<ProfilePropertyDefinition?> GetDefinitionByIdAsync(
        int? portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one profile property declaration by name within a tenant.</summary>
    /// <param name="portalId">
    /// The tenant whose declarations are searched, matched EXACTLY, or <see langword="null"/> for the host
    /// scope - see the scope note below.
    /// </param>
    /// <param name="propertyName">The declared property name to match.</param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// The matching declaration, or <see langword="null"/> when the tenant declares no property of that
    /// name.
    /// </returns>
    Task<ProfilePropertyDefinition?> GetDefinitionByNameAsync(
        int? portalId,
        string propertyName,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every profile property declaration belonging to one tenant.</summary>
    /// <param name="portalId">
    /// The tenant whose declarations are wanted, matched EXACTLY, or <see langword="null"/> for the host
    /// scope - the same two-valued scope as <see cref="GetDefinitionByNameAsync(int?, string,
    /// CancellationToken)"/>.
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>The tenant's declarations, empty when it declares none.</returns>
    /// <remarks>
    /// Callers render profile forms from this sequence, so an implementation is expected to return it in
    /// the tenant's declared display order - <see cref="ProfilePropertyDefinition.ViewOrder"/> first - and
    /// to exclude declarations withdrawn through <see cref="ProfilePropertyDefinition.IsDeleted"/>, so a
    /// retired property is not presented for answering.
    /// </remarks>
    Task<IReadOnlyList<ProfilePropertyDefinition>> GetDefinitionsByPortalIdAsync(
        int? portalId,
        CancellationToken cancellationToken = default);
}
