using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

// MIGRATION: this repository spans the TWO legacy provider stacks its contract was assembled from, and
// nothing else. The per-user answers come from the membership provider's "Profile" block -
// Library/Providers/MembershipProviders/DataProvider/DataProvider.vb:L117-L119, implemented at
// Library/Providers/MembershipProviders/DataProvider/SqlDataProvider.vb:L302 and L306 - while the
// declarations those answers are keyed by come from the core provider's "profile property definitions"
// block at Library/Components/Providers/Data/DataProvider.vb:L250-L256, implemented at
// Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb:L1017-L1048. Both stacks resolved
// through a reflection-created static singleton, and ProfileController.vb held one of each in a shared
// field (L48 and L49); both are replaced by the single injected context below.
//
// MIGRATION: the older SERIALIZED profile is not ported. The core provider's personalization block -
// GetAllProfiles (L245), GetProfile(UserId, PortalId) (L246), AddProfile(UserId, PortalId) (L247) and
// UpdateProfile(UserId, PortalId, ProfileData As String) (L248) - stored a whole profile as one opaque
// string keyed by user AND portal. The surviving row model is keyed by user and declaration and takes
// no portal at all, which is what proves the two are successive generations rather than peers, and the
// Personalization subsystem is out of scope. No member here reads or writes serialized profile content.
//
// MIGRATION: the single membership upsert splits into two explicit operations. UpdateProfileProperty
// (DataProvider.vb:L119) carried six positional arguments and its procedure decided at run time which
// arm to take: UpdateUserProfileProperty resolves a null or -1 @ProfileID against the
// (UserID, PropertyDefinitionID) natural key (04.00.04.SqlDataProvider:L1616-L1620) and then branches
// to an UPDATE arm (L1622) or an INSERT arm (L1633). Those two arms are AddProfileValueAsync and
// UpdateProfileValueAsync. The branch itself does not survive, and no member below inspects
// ProfileId - least of all against -1 - to decide what to stage: a caller that read a row knows it
// exists and a caller building one knows it does not, so the decision is made where the knowledge is.
//
// MIGRATION: the sixth legacy argument, LastUpdatedDate, is a COLUMN and not a parameter. dbo.UserProfile
// is the only in-scope table that carries it - it arrived with the table at 03.02.03.SqlDataProvider:L1372
// - so the value travels on UserProfileValue.LastUpdatedDate, stamped by the Application layer from the
// injected clock. No member here takes a DateTime and none reads ambient time, which is what keeps
// time-dependent behaviour testable.
//
// MIGRATION: both storage columns are preserved exactly as stored. The write procedure splits every
// submitted value across PropertyValue and PropertyText on DATALENGTH(@PropertyValue) > 7500, in its
// UPDATE arm (04.00.04:L1626-L1627) and its INSERT arm (L1647-L1648), so a long answer lives ONLY in
// PropertyText; the read procedure then hid the split behind one coalesced column,
// "case when (PropertyValue Is Null) then PropertyText else PropertyValue end" aliased PropertyValue
// (04.00.04:L1592). Reproducing that coalesce here would conceal which column a round trip actually
// wrote, so this repository returns both columns untouched and the Application mapper performs the
// PropertyValue ?? PropertyText projection where it can be seen and tested.
//
// MIGRATION: hydration is gone rather than translated. ProfileController.vb reached rows two ways -
// reflection through CBO.FillCollection into an ArrayList wrapped in ProfilePropertyDefinitionCollection
// (L102-L103), and a hand-rolled reader loop assigning each column through Null.SetNull (L136-L160) -
// and handed the result back as a pre-generics CollectionBase subclass. The Entity Framework materialiser
// replaces both paths and IReadOnlyList<T> replaces the wrapper, so this file contains no reader, no
// Fill member, no reflection and no sentinel translation on the read path.
//
// MIGRATION: WRITES STAGE, THEY DO NOT COMMIT, and no write member answers with a key. The legacy insert
// procedures could return one because they had already written the row - AddPropertyDefinition ends in
// SELECT @PropertyDefinitionId over SCOPE_IDENTITY() (04.03.03.SqlDataProvider:L150 and L168) and the
// value insert ends in SELECT SCOPE_IDENTITY() (04.00.04:L1653). Under an object-relational mapper the
// key is assigned when the unit of work is saved, so returning it would force a flush here and split
// batches that must be atomic: provisioning a tenant's default declarations writes a whole set, and
// portal creation spans seven tables across several flushes inside one explicit transaction.
// IUnitOfWork.SaveChangesAsync is the flush; the transaction scope's commit is what makes it durable.
//
// MIGRATION: rules that shape a declaration stay OUT of this layer, deliberately. ProfileController.vb
// forced Visible true whenever Required was set, in the add path (L373-L375) and again in the update path
// (L535-L537); it seeded a tenant's default declarations one at a time (L334); it filtered an
// already-loaded collection by category with no backing procedure (L485); it cloned every entry on the
// way out (L512-L518); and it mutated a caller's UserInfo in place through a ByRef argument (L226). None
// of that is persistence. This repository stages whatever declaration it is handed, returns entities
// rather than clones, and mutates no caller's object.
//
// MIGRATION: caching is not performed here. ProfileController.vb read definitions through
// DataCache.ProfileDefinitionsCacheKey with a timeout multiplied by Common.Globals.PerformanceSetting
// (L188-L204), cleared them portal-wide on every write (L397-L398, L412, L544) and cleared the user
// cache on a profile write (L250). The target coordinates that above this layer through ICacheService,
// which is why no member below takes a bypass, refresh or clear flag and no cache is touched: a
// repository that cached its own reads would answer from a cache the committing caller cannot evict.

/// <summary>
/// Reads and writes the user-profile slice of the User aggregate over the legacy
/// <c>dbo.UserProfile</c> and <c>dbo.ProfilePropertyDefinition</c> tables.
/// </summary>
/// <remarks>
/// <para>
/// The type is <see langword="internal"/> and sealed. Callers reach it only through
/// <see cref="IUserProfileRepository"/>, resolved from the container, so neither the Application layer
/// nor the API layer can name it, name the context it holds, or extend a query it did not build. Its
/// provenance in the two legacy provider stacks, and every legacy member deliberately left out, are set
/// out in the migration notes above.
/// </para>
/// <para>
/// Every read applies <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/> and
/// materialises before returning, so a caller is handed values rather than a live query and the change
/// tracker carries nothing a read merely looked at. That is safe here - and it is worth stating why,
/// because the sibling repositories in this folder deliberately track their reads - because BOTH write
/// members below stage a detached instance explicitly. A caller that reads a row, mutates it and calls
/// the matching update member gets exactly the same committed outcome it would from a tracked read; what
/// it does not get is a silent commit of a mutation it never announced.
/// </para>
/// <para>
/// The one read that must track is the resolution inside
/// <see cref="DeleteDefinitionAsync(int, CancellationToken)"/>, which is a write in progress rather than
/// a read: staging a removal requires a tracked entity, and the answers recorded against the declaration
/// are loaded with it so the cascade is staged by the change tracker as explicit statements instead of
/// depending on the store enforcing <c>FK_UserProfile_ProfilePropertyDefinition</c>
/// (<c>04.00.04.SqlDataProvider:L1429</c>).
/// </para>
/// <para>
/// Nothing here validates and nothing here decides. A declaration's required flag, declared length and
/// validation expression constrain the ANSWERS a tenant may give and are tenant data rather than code,
/// so they are stored by this layer and enforced above it; whether a viewer may see a value is an
/// authorisation question, so <see cref="UserProfileValue.Visibility"/> travels as the plain persisted
/// integer the column holds.
/// </para>
/// </remarks>
internal sealed class UserProfileRepository : IUserProfileRepository
{
    // MIGRATION: THERE IS NO HOST SENTINEL CONSTANT HERE ANY LONGER, and its removal is the whole of a
    // measured defect fix. The legacy definition readers did not pass a portal identifier straight
    // through: GetPropertyDefinitionByName and GetPropertyDefinitionsByPortal both wrapped it as
    // GetNull(portalId) (SqlDataProvider.vb:L1039 and L1042), GetNull is
    // Null.GetNull(Field, DBNull.Value) (L325-L326), and that helper turns an int equal to -1 into
    // DBNull.Value (Null.vb:L167-L170). The terminal procedures then filter
    // "(PortalId = @PortalId OR (PortalId IS NULL AND @PortalId IS NULL))" (04.03.03:L206 and L226), so a
    // request carrying -1 reached the rows stored with a SQL NULL portal and nothing else.
    //
    // An earlier revision reproduced that by declaring "private const int HostPortalId = -1" and
    // rewriting portalId == -1 into "PortalId IS NULL" over a NON-NULLABLE parameter. That was wrong in
    // this schema: dbo.Portals.PortalID is IDENTITY(-1, 1) (01.00.00.SqlDataProvider:L77), so -1 is
    // simultaneously the FIRST REAL TENANT of an installation. The consequence was measured, not
    // theoretical - a real tenant numbered -1 could not read its own declarations or answers at all, and
    // every such request was served the host scope's rows instead: inaccessibility in one direction and
    // cross-scope exposure in the other.
    //
    // The scope is therefore expressed the way the COLUMN expresses it, as int? - null is the SQL-null
    // host scope and every non-null value, -1 and 0 included, is an exact tenant key. That is also what
    // AAP Rule T7 requires: a sentinel belongs at the boundary, and the Application layer keeps the
    // externally observable -1 in the DTO contract. 03.03.03.SqlDataProvider:L74-L83 confirms SQL NULL is
    // the only host encoding the terminal schema has, since it made the column nullable and migrated the
    // stored rows with "SET PortalId = NULL WHERE PortalId = -1".

    // The full evidence for the predicate itself, including the AAP citation that forbids treating -1
    // as an absence, is carried on DefinitionsInPortalScope below; the recorded divergence is in
    // MIGRATION_NOTES.md.

    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="UserProfileRepository"/> class.</summary>
    /// <param name="dbContext">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// MIGRATION: the context is the whole dependency. The legacy controller reached two reflection-created
    /// static providers (<c>ProfileController.vb:L48</c> and <c>L49</c>), a static cache, a static host
    /// setting and a list service; none of those is a persistence concern, so none is injected here.
    /// </remarks>
    public UserProfileRepository(DnnDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    // Profile values (membership DataProvider.vb:L117-L119).

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>GetUserProfile(ByVal UserId As Integer) As IDataReader</c>
    /// (membership <c>DataProvider.vb:L118</c>, executed at
    /// <c>MembershipProviders/DataProvider/SqlDataProvider.vb:L302</c>). The filter is the account and
    /// only the account, exactly as the procedure's <c>WHERE UserId = @UserId</c>
    /// (<c>04.00.04.SqlDataProvider:L1596</c>) - narrowing by tenant here would narrow a result the
    /// legacy reader never narrowed, because this generation of the profile store is not portal-scoped.
    /// </para>
    /// <para>
    /// MIGRATION: both storage columns are returned as stored. The legacy procedure projected one
    /// coalesced column (<c>04.00.04.SqlDataProvider:L1592</c>) and so could not report which of the two
    /// a round trip had written; the Application mapper performs
    /// <see cref="UserProfileValue.PropertyValue"/> then <see cref="UserProfileValue.PropertyText"/>
    /// instead, and neither column is discarded, normalised or combined on the way out.
    /// </para>
    /// <para>
    /// MIGRATION: the ordering is a deliberate ADDITION. The legacy procedure declared no
    /// <c>ORDER BY</c> at all (<c>04.00.04.SqlDataProvider:L1583-L1594</c>), so its row order was
    /// whatever the query plan produced and no caller could depend on it. Ordering by the declaration
    /// key and then by the row key makes the sequence stable across providers and plans without
    /// asserting any order the legacy contract promised, and it needs no join to compute.
    /// </para>
    /// <para>
    /// The declaration is loaded with each answer, because an answer carries no name, data type or
    /// validation expression of its own and is only interpretable beside the property it answers.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.UserProfileValues
            .AsNoTracking()
            .Include(value => value.PropertyDefinition)
            .Where(value => value.UserId == userId)
            .OrderBy(value => value.PropertyDefinitionId)
            .ThenBy(value => value.ProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: THE PORTAL SCOPE IS THE ONE <see cref="DefinitionsInPortalScope(int?)"/> DEFINES, and
    /// delegating to it rather than restating it is the point. There is exactly one statement in this
    /// repository of what a scope's declarations are, and every read and write asks it rather than
    /// re-deriving the predicate, so the answer read and the declaration read cannot drift apart. The
    /// scope itself is nullable because the column is: <see langword="null"/> addresses the host-level
    /// declarations that <c>03.03.03.SqlDataProvider:L74-L83</c> migrated onto a SQL <c>NULL</c> portal,
    /// and every non-null value - <c>-1</c> and <c>0</c> included - is an exact tenant key, because
    /// <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>).
    /// </para>
    /// <para>
    /// Expressed as a subquery over the scoped declaration keys, so the two reads cannot drift apart again:
    /// there is one definition of what a scope's declarations are, and this read asks it rather than
    /// re-deriving it. Deleted declarations are deliberately not filtered out here - an answer to a
    /// withdrawn property is still an answer, and every caller that cares about required properties consults
    /// the declaration list, which does filter them.
    /// </para>
    /// <para>
    /// The ordering and the loaded declaration are exactly as in the account-wide overload above, for the
    /// reasons recorded there.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<UserProfileValue>> GetProfileValuesAsync(
        int? portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<int> scopedDefinitions = DefinitionsInPortalScope(portalId)
            .Select(definition => definition.PropertyDefinitionId);

        return await _dbContext.UserProfileValues
            .AsNoTracking()
            .Include(value => value.PropertyDefinition)
            .Where(value =>
                value.UserId == userId
                && scopedDefinitions.Contains(value.PropertyDefinitionId))
            .OrderBy(value => value.PropertyDefinitionId)
            .ThenBy(value => value.ProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the INSERT arm of the single membership upsert
    /// (<c>DataProvider.vb:L119</c>, procedure branch <c>04.00.04.SqlDataProvider:L1633</c>). Six
    /// positional arguments become one entity, and the arm is chosen by the caller calling this member
    /// rather than rediscovered by a natural-key probe on every write: this method stages the row it is
    /// given and never inspects <see cref="UserProfileValue.ProfileId"/> to decide anything.
    /// </para>
    /// <para>
    /// MIGRATION: staged, not written, and no identifier is returned.
    /// <see cref="UserProfileValue.ProfileId"/> holds its generated key once
    /// <c>IUnitOfWork.SaveChangesAsync</c> has run, which is where the legacy
    /// <c>SELECT SCOPE_IDENTITY()</c> (<c>04.00.04.SqlDataProvider:L1653</c>) now happens.
    /// </para>
    /// </remarks>
    public Task AddProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileValue);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.UserProfileValues.Add(profileValue);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the UPDATE arm of that same upsert (procedure branch
    /// <c>04.00.04.SqlDataProvider:L1622</c>). The legacy procedure reached this arm by resolving a null
    /// or -1 <c>@ProfileID</c> against the <c>(UserID, PropertyDefinitionID)</c> natural key
    /// (<c>L1616-L1620</c>); no such probe happens here, and <c>-1</c> carries no meaning in this member.
    /// </para>
    /// <para>
    /// MIGRATION: this is also how an answer is CLEARED. No legacy member and no procedure in the
    /// eighty-eight upgrade scripts ever deleted a <c>UserProfile</c> row - the membership provider's
    /// profile block declares only the reader at <c>L118</c> and this upsert at <c>L119</c> - so
    /// clearing was an upsert carrying an empty value, which preserved the row's visibility and
    /// refreshed its timestamp where a deletion would have reset both. Staging a blanked value through
    /// this member reproduces that exactly, which is why the contract offers no value-deletion member.
    /// </para>
    /// <para>
    /// MIGRATION: <see cref="UserProfileValue.LastUpdatedDate"/> arrives on the entity. The legacy
    /// provider took it as the sixth argument and the profile provider stamped it from
    /// <c>Now()</c> at the call site; here the Application layer stamps it from the injected clock, so
    /// this member neither accepts a date nor reads one.
    /// </para>
    /// </remarks>
    public Task UpdateProfileValueAsync(
        UserProfileValue profileValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileValue);
        cancellationToken.ThrowIfCancellationRequested();

        StageModified(profileValue);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteProfileValuesAsync(
        int? portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        // MIGRATION: scoped through DefinitionsInPortalScope for the same reason the portal-scoped read is.
        // The scope is nullable because the column is - null is the host-level declarations that
        // 03.03.03.SqlDataProvider:L74-L83 migrated onto a SQL NULL portal, and every non-null value is an
        // exact tenant key, -1 included, because dbo.Portals.PortalID is IDENTITY(-1, 1)
        // (01.00.00.SqlDataProvider:L77). Asking the one definition of a scope's declarations keeps this
        // write and the reads beside it describing the same set, so a membership removal cannot leave the
        // addressed scope's own answers behind or reach another scope's.
        IQueryable<int> scopedDefinitions = DefinitionsInPortalScope(portalId)
            .Select(definition => definition.PropertyDefinitionId);

        List<UserProfileValue> values = await _dbContext.UserProfileValues
            .Include(value => value.PropertyDefinition)
            .Where(value =>
                value.UserId == userId
                && scopedDefinitions.Contains(value.PropertyDefinitionId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _dbContext.UserProfileValues.RemoveRange(values);
    }

    // =================================================================================================
    // SECTION B - PROFILE PROPERTY DEFINITIONS  (core DataProvider.vb:L250-L256)
    // =================================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>AddPropertyDefinition</c> (core <c>DataProvider.vb:L251</c>, executed at
    /// <c>SqlDataProvider.vb:L1017-L1031</c>), whose ELEVEN positional arguments are all properties of
    /// <see cref="ProfilePropertyDefinition"/>. The whole list collapses to one parameter, so a call site
    /// can no longer transpose two same-typed arguments.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy member was an insert-OR-update in disguise and this one is not. Its
    /// procedure probed
    /// <c>(PortalId = @PortalId OR (PortalId IS NULL AND @PortalId IS NULL)) AND PropertyName = @PropertyName</c>
    /// first (<c>04.03.03.SqlDataProvider:L114-L118</c>) and silently UPDATED the row it found, clearing
    /// <c>Deleted</c> as it went (<c>L152-L166</c>). That hid two different outcomes behind one call and,
    /// worse, hid an undelete. Here a create stages a create; the duplicate test the Application layer
    /// performs before calling this member is
    /// <see cref="GetDefinitionByNameAsync(int?, string, CancellationToken)"/>, whose answer it can act on.
    /// </para>
    /// <para>
    /// MIGRATION: no rule is applied to the declaration on the way in. The legacy add forced
    /// <c>Visible</c> true whenever <c>Required</c> was set (<c>ProfileController.vb:L373-L375</c>) and a
    /// separate member seeded a tenant's whole default set (<c>L334</c>); both are Application
    /// orchestration, and the second is simply a sequence of calls to this member under one unit of work.
    /// </para>
    /// <para>
    /// MIGRATION: staged, not written, and no identifier is returned - the legacy member answered with
    /// <c>SCOPE_IDENTITY()</c> (<c>04.03.03.SqlDataProvider:L150</c> and <c>L168</c>), whereas
    /// <see cref="ProfilePropertyDefinition.PropertyDefinitionId"/> holds its generated key once the unit
    /// of work is saved. Returning it here would force a flush and break the batch that tenant
    /// provisioning commits atomically.
    /// </para>
    /// </remarks>
    public Task AddDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.ProfilePropertyDefinitions.Add(definition);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>UpdatePropertyDefinition</c> (core <c>DataProvider.vb:L256</c>, executed at
    /// <c>SqlDataProvider.vb:L1044-L1048</c>) and its TEN positional arguments, nine of which the
    /// eleven-argument insert also carried. The two lists differed only in which key led them, which is
    /// exactly the redundancy an entity parameter removes.
    /// </para>
    /// <para>
    /// MIGRATION: the required-implies-visible coupling the legacy update applied
    /// (<c>ProfileController.vb:L535-L537</c>) is NOT reproduced. Silently rewriting a submitted value is
    /// a rule, not persistence, and hiding it here would make the stored declaration differ from the one
    /// the caller believes it saved; the Application layer applies it where it can be seen and tested.
    /// </para>
    /// <para>
    /// Reordering a tenant's profile form needs no member of its own: the legacy grid swapped the display
    /// order of two declarations and persisted each through this same call, so the order is simply
    /// <see cref="ProfilePropertyDefinition.ViewOrder"/> on the declarations being updated.
    /// </para>
    /// </remarks>
    public Task UpdateDefinitionAsync(
        ProfilePropertyDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        StageModified(definition);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces <c>DeletePropertyDefinition(ByVal definitionId As Integer)</c> (core
    /// <c>DataProvider.vb:L252</c>, executed at <c>SqlDataProvider.vb:L1032-L1034</c>). The identifier
    /// alone was the whole legacy signature and it is the whole signature here: no flag chooses between a
    /// physical and a logical removal, and no companion member restores one, because the legacy surface
    /// offered neither.
    /// </para>
    /// <para>
    /// MIGRATION: the removal is PHYSICAL, matching that legacy member.
    /// <see cref="ProfilePropertyDefinition.IsDeleted"/> is a real column and the contract permits an
    /// implementation to honour this member by setting it, but the legacy call deleted the row and the
    /// Application layer above depends on that reading - it guards this call precisely because "removal
    /// here is physical and cascades the stored answers". Turning this member into a flag write would
    /// change an outcome a caller already reasons about, so the flag stays what it is: state the caller
    /// sets through <see cref="UpdateDefinitionAsync(ProfilePropertyDefinition, CancellationToken)"/>
    /// and this repository filters on in
    /// <see cref="GetDefinitionsByPortalIdAsync(int?, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// MIGRATION: the resolution is TRACKED and pulls the recorded answers with it, which is the one
    /// place in this file where tracking is required rather than avoided.
    /// <c>FK_UserProfile_ProfilePropertyDefinition</c> is declared <c>ON DELETE CASCADE</c>
    /// (<c>04.00.04.SqlDataProvider:L1429</c>), so a SQL Server store would cascade on its own - but a
    /// cascade the store performs is invisible to the change tracker, and a provider that does not
    /// enforce the constraint would leave every account's answer behind referencing a declaration that
    /// no longer exists. Loading them makes the removal explicit on every provider.
    /// </para>
    /// <para>
    /// Absence is not an error. A caller that has already established absence - as the Application layer
    /// does before calling this member - need not distinguish the two cases, so nothing is staged and no
    /// exception is raised.
    /// </para>
    /// </remarks>
    public async Task DeleteDefinitionAsync(
        int propertyDefinitionId,
        CancellationToken cancellationToken = default)
    {
        ProfilePropertyDefinition? definition = await _dbContext.ProfilePropertyDefinitions
            .Include(candidate => candidate.ProfileValues)
            .FirstOrDefaultAsync(
                candidate => candidate.PropertyDefinitionId == propertyDefinitionId,
                cancellationToken)
            .ConfigureAwait(false);

        if (definition is null)
        {
            return;
        }

        _dbContext.ProfilePropertyDefinitions.Remove(definition);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: the raw provider member accepted only the definition identifier, but its only controller
    /// wrapper accepted the portal too and first searched the portal-scoped catalogue
    /// (<c>ProfileController.vb:L425-L439</c>). Falling through to the raw key lookup on a catalogue miss
    /// discarded that scope and could answer another tenant's declaration. This member retains the
    /// tenant-bearing controller contract and routes the key through
    /// <see cref="DefinitionsInPortalScope(int?)"/>, the same predicate used by the name and collection
    /// reads. The unsafe fallback is not reproduced.
    /// </para>
    /// <para>
    /// MIGRATION: the scope predicate is applied before the identifier one, and it says exactly what it
    /// means. A caller passing <see langword="null"/> reaches the host-level rows whose <c>PortalID</c> is
    /// SQL <c>NULL</c>; a caller passing an identifier reaches that tenant's rows and no others. -1 is an
    /// identifier like any other here, not a request for the host scope - it is the first key
    /// <c>IDENTITY(-1, 1)</c> issues (<c>01.00.00.SqlDataProvider:L77</c>), and an earlier revision that
    /// rewrote it into <c>PortalID IS NULL</c> made that tenant's own declarations unreachable while
    /// serving it another scope's.
    /// The legacy translation through <c>GetNull</c> did the opposite, and that difference is a
    /// deliberate, documented divergence; the evidence is on
    /// <see cref="DefinitionsInPortalScope(int?)"/>.
    /// </para>
    /// <para>
    /// MIGRATION: absence is <see langword="null"/> and never a sentinel identifier. Both values the
    /// legacy <c>Null</c> helper treated as empty are genuine keys in this schema - <c>Portals.PortalID</c>
    /// seeds at -1 and <c>Roles.RoleID</c>, <c>Tabs.TabID</c> and <c>Modules.ModuleID</c> seed at 0 - so no
    /// caller should test a returned declaration's identifier to decide whether it was found.
    /// </para>
    /// </remarks>
    public async Task<ProfilePropertyDefinition?> GetDefinitionByIdAsync(
        int? portalId,
        int propertyDefinitionId,
        CancellationToken cancellationToken = default)
    {
        return await DefinitionsInPortalScope(portalId)
            .FirstOrDefaultAsync(
                definition => definition.PropertyDefinitionId == propertyDefinitionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces
    /// <c>GetPropertyDefinitionByName(ByVal portalId As Integer, ByVal name As String) As IDataReader</c>
    /// (core <c>DataProvider.vb:L254</c>, executed at <c>SqlDataProvider.vb:L1038-L1040</c>). It answers
    /// with the declaration rather than with a boolean, as the legacy member did, which is what lets the
    /// caller that principally uses it - a duplicate-name test during an edit - tell "found" from "found,
    /// but it is the row I am editing".
    /// </para>
    /// <para>
    /// MIGRATION: the name comparison is exact, and stays the store's own comparison. The terminal
    /// procedure matched <c>PropertyName = @Name</c> (<c>04.03.03.SqlDataProvider:L207</c>), so
    /// case-sensitivity was and remains the column collation's decision; forcing a case fold or a trim
    /// here would both diverge from that and defeat the unique index over
    /// <c>(PortalID, ModuleDefID, PropertyName)</c>. The legacy empty-string sentinel needs no handling
    /// either: <c>Null.NullString</c> WAS the empty string, so an empty name was legally representable and
    /// is matched as the ordinary value it is rather than read as a request for any name.
    /// </para>
    /// <para>
    /// MIGRATION: the portal scope is resolved by <see cref="DefinitionsInPortalScope(int?)"/>, whose
    /// remarks carry the evidence. Ordering follows the legacy <c>ORDER BY ViewOrder</c> (<c>L208</c>) with
    /// the definition key as a tie-break, so that a store holding a host-level and a tenant declaration of
    /// the same name - which the unique index permits - answers the same way on every read rather than the
    /// way the plan happened to produce.
    /// </para>
    /// <para>
    /// MIGRATION: withdrawn declarations are deliberately still visible to this member. The legacy
    /// procedure applied no <c>Deleted</c> filter here, unlike its portal listing (<c>L227</c>), so a
    /// retired declaration still reserves its name - which is what keeps a duplicate from being created
    /// against a row that could later be restored. The Application layer decides what a withdrawn
    /// declaration means to the operation in hand.
    /// </para>
    /// </remarks>
    public async Task<ProfilePropertyDefinition?> GetDefinitionByNameAsync(
        int? portalId,
        string propertyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        return await DefinitionsInPortalScope(portalId)
            .Where(definition => definition.PropertyName == propertyName)
            .OrderBy(definition => definition.ViewOrder)
            .ThenBy(definition => definition.PropertyDefinitionId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces
    /// <c>GetPropertyDefinitionsByPortal(ByVal portalId As Integer) As IDataReader</c> (core
    /// <c>DataProvider.vb:L255</c>, executed at <c>SqlDataProvider.vb:L1041-L1043</c>), whose reader was
    /// hydrated by reflection through <c>CBO</c> and handed back inside a 313-line
    /// <c>CollectionBase</c> subclass. A materialised <see cref="IReadOnlyList{T}"/> replaces both.
    /// </para>
    /// <para>
    /// MIGRATION: withdrawn declarations are excluded, reproducing the procedure's <c>AND Deleted = 0</c>
    /// (<c>04.03.03.SqlDataProvider:L227</c>). No global query filter is configured for this entity, so
    /// the predicate belongs to the read that needs it; a caller wanting a retired declaration addresses
    /// it by key or by name, neither of which filters the flag.
    /// </para>
    /// <para>
    /// MIGRATION: the ordering reproduces <c>ORDER BY ViewOrder</c> (<c>L228</c>) - callers render profile
    /// forms straight from this sequence - and adds the property name and then the key as tie-breaks,
    /// because the legacy order was ambiguous whenever two declarations shared a display position and a
    /// form that reshuffles between two identical reads is a defect the legacy plan merely hid.
    /// </para>
    /// <para>
    /// MIGRATION: the read is UNPAGED and uncategorised, matching the legacy member, which took neither
    /// page coordinates nor a category. Category filtering existed only as an in-memory predicate over an
    /// already-loaded collection (<c>ProfileController.vb:L485-L496</c>) with a procedure that the core
    /// provider never called, so it is an Application projection over this result rather than a member
    /// here. Entries are returned as tracked-free entities rather than the clones the legacy accessor
    /// produced (<c>L512-L518</c>), which is what <c>AsNoTracking</c> already guarantees: nothing a
    /// caller does to them can reach the change tracker.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ProfilePropertyDefinition>> GetDefinitionsByPortalIdAsync(
        int? portalId,
        CancellationToken cancellationToken = default)
    {
        return await DefinitionsInPortalScope(portalId)
            .Where(definition => !definition.IsDeleted)
            .OrderBy(definition => definition.ViewOrder)
            .ThenBy(definition => definition.PropertyName)
            .ThenBy(definition => definition.PropertyDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the untracked declaration query for one scope.
    /// </summary>
    /// <param name="portalId">
    /// The tenant the caller named, matched exactly, or <see langword="null"/> for the host scope.
    /// </param>
    /// <returns>A no-tracking query narrowed to the declarations that scope reaches.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the single place the portal scope of a declaration read is decided, and it
    /// reproduces the terminal procedures' own predicate,
    /// <c>(PortalId = @PortalId OR (PortalId IS NULL AND @PortalId IS NULL))</c>
    /// (<c>04.03.03.SqlDataProvider:L206</c> and <c>L226</c>), faithfully: a supplied identifier matches
    /// the column exactly, and a supplied <c>NULL</c> matches the rows whose column is <c>NULL</c>. The
    /// only thing that changes is WHO turns a caller's intent into that <c>NULL</c>. The legacy stack did
    /// it inside the provider by testing for -1 (<c>SqlDataProvider.vb:L1039</c> and <c>L1042</c> through
    /// <c>Null.GetNull</c> at <c>L325-L326</c> and <c>Null.vb:L167-L170</c>); here the caller says
    /// <see langword="null"/> and means it.
    /// </para>
    /// <para>
    /// MIGRATION: -1 IS NOT THE HOST SCOPE. <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), so -1 is the first real tenant of an installation as well as
    /// the legacy absence marker. Testing for it here made that tenant's own declarations unreachable and
    /// served it the host scope's rows instead, which is a data-isolation failure in both directions. Rule
    /// T7 keeps a sentinel at the boundary rather than in a repository, so the translation - where a
    /// legacy contract still needs one - belongs to the Application mapper.
    /// </para>
    /// <para>
    /// MIGRATION: it is NOT an all-portals wildcard and must never be widened into one. Exactly one scope
    /// is ever in play: <see langword="null"/> selects the host-level rows and every identifier selects
    /// one tenant's rows. Note that a comparison against a <see cref="Nullable{T}"/> would translate to
    /// SQL as a comparison against <c>NULL</c> for the host case, which matches nothing, so the two cases
    /// are branched in C# and each produces a predicate that says what it means.
    /// </para>
    /// <para>
    /// MIGRATION: DELIBERATE, DOCUMENTED DIVERGENCE, and the action plan names it. AAP section 0.5.1.1
    /// states that the portal-identifier wrapper "forbids treating -1 as absent", and tenant isolation is
    /// a stated preservation requirement. Reproducing the legacy <c>GetNull</c> reading made the FIRST
    /// TENANT OF EVERY INSTALLATION unable to see its own declarations: they were invisible through every
    /// read, while declarations created for it were filed as host-level and shared with every other
    /// tenant. The divergence is recorded in <c>MIGRATION_NOTES.md</c>.
    /// </para>
    /// <para>
    /// MIGRATION: the host-level rows are not orphaned by this, they are simply out of every tenant's
    /// scope - which is precisely how the legacy predicate already behaved for every tenant other than -1.
    /// A host-level declaration is still expressible in the store, still readable by a host-scoped read
    /// (<paramref name="portalId"/> <see langword="null"/>), and still distinguishable in the model, where
    /// absence is a genuine <see langword="null"/> on
    /// <see cref="ProfilePropertyDefinition.PortalId"/> and never a sentinel.
    /// </para>
    /// </remarks>
    private IQueryable<ProfilePropertyDefinition> DefinitionsInPortalScope(int? portalId)
    {
        IQueryable<ProfilePropertyDefinition> definitions =
            _dbContext.ProfilePropertyDefinitions.AsNoTracking();

        return portalId is int tenantId
            ? definitions.Where(definition => definition.PortalId == tenantId)
            : definitions.Where(definition => definition.PortalId == null);
    }

    /// <summary>
    /// Stages a caller-supplied entity for update without traversing the graph hanging off it.
    /// </summary>
    /// <typeparam name="TEntity">The entity type being staged.</typeparam>
    /// <param name="entity">The entity whose stored row is to be rewritten.</param>
    /// <remarks>
    /// <para>
    /// An entity this repository is handed is normally DETACHED, because every read member here is
    /// untracked, so the update members cannot rely on the change tracker having noticed a mutation.
    /// Setting the entry's state attaches the instance and marks its scalar properties modified. An
    /// instance that is already tracked is left exactly as it is: it is either being mutated under the
    /// tracker's eye, or already staged as added or removed, and none of those states should be
    /// overwritten by a statement of intent.
    /// </para>
    /// <para>
    /// The state assignment is deliberate in preference to <c>DbSet.Update</c>. That method walks the
    /// graph reachable from the entity and marks everything it finds, which for a value carrying the
    /// declaration it was read with - see
    /// <see cref="GetProfileValuesAsync(int, CancellationToken)"/> - would stage a rewrite of that
    /// declaration as well, and would fail outright if another instance of it were already tracked.
    /// Setting the state touches this entity and nothing else, which is exactly the promise the write
    /// members make.
    /// </para>
    /// </remarks>
    private void StageModified<TEntity>(TEntity entity)
        where TEntity : class
    {
        var entry = _dbContext.Entry(entity);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }
    }
}
