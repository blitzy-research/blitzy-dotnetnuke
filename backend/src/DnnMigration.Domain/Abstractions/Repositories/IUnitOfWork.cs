namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// The single atomic commit boundary for the domain model: the one point at which work
/// staged through the sibling repository abstractions is made durable.
/// </summary>
/// <remarks>
/// <para>
/// Repositories stage inserts, updates and deletes; an implementer is obliged to make this
/// the only flush point, because a repository that persisted on its own behalf would split
/// a multi-table write into independently durable statements.
/// </para>
/// <para>
/// That split is the legacy defect this abstraction removes. Portal creation
/// (<c>Library/Components/Portal/PortalController.vb</c>, line 980) writes the
/// <c>Portals</c>, <c>PortalAlias</c>, <c>Roles</c>, <c>Tabs</c> and <c>Modules</c> tables
/// as five independent statement sequences and can raise after the portal row and its
/// administrator are already durable, leaving a portal with no alias, roles, pages or
/// modules and no means of recovery. The legacy data provider did declare transaction
/// members (<c>Library/Components/Providers/Data/DataProvider.vb</c>, lines 70 to 74),
/// but <c>PortalController</c> never invoked them.
/// </para>
/// <para>
/// No persistence session, transaction handle, query surface or change-tracking mechanic is
/// exposed, so a consumer cannot name - and therefore cannot depend upon - the technology
/// that stores the batch. An implementation is expected to be registered with a per-request
/// lifetime and disposed by the container: no release member is declared, and a consumer
/// must not attempt to manage its lifetime.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Atomically persists every change staged since the previous commit.
    /// </summary>
    /// <remarks>
    /// An implementer must apply the whole batch or none of it, so the multi-table sequence
    /// described on <see cref="IUnitOfWork"/> commits indivisibly. Server-generated keys
    /// become readable only after this call returns: an add operation stages a row and yields
    /// nothing because the store has not yet assigned an identifier, and the assigned key is
    /// then observed on the entity rather than through a return value, which is what lets
    /// several inserts across several tables share one commit.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Abandons the operation. When cancellation is observed before the batch is committed, no
    /// change is applied.
    /// </param>
    /// <returns>
    /// A task whose result is the number of state entries written to the underlying store. Zero
    /// indicates that nothing was staged.
    /// </returns>
    // MIGRATION: the legacy provider returned a generated key directly from each of its 28 Add
    // members; key visibility is now deferred until this commit completes and is observed on
    // the entity.
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
