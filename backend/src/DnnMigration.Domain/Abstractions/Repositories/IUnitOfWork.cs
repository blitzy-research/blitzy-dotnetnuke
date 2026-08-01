namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Defines the single atomic commit boundary for the domain model: the one point
/// at which work staged through the sibling repository abstractions in this
/// namespace is made durable.
/// </summary>
/// <remarks>
/// <para>
/// Every insert, update and delete staged by a repository is held pending until
/// it is flushed through <see cref="SaveChangesAsync"/>. Nothing else in the
/// solution flushes. A repository that persisted on its own behalf would split a
/// multi-table write into independently durable statements, reintroducing the
/// defect described next.
/// </para>
/// <para>
/// Why this abstraction exists. The legacy entry point
/// <c>PortalController.CreatePortal</c>
/// (<c>Library/Components/Portal/PortalController.vb</c>, line 980, fifteen
/// positional arguments) writes the <c>Portals</c>, <c>PortalAlias</c>,
/// <c>Roles</c>, <c>Tabs</c> and <c>Modules</c> tables as five independent
/// statement sequences, and raises on three separate paths after the portal row
/// and its administrator are already durable. A failure part way through
/// therefore leaves a portal with no alias, no roles, no pages and no modules,
/// and no means of recovery. The legacy data provider did declare explicit
/// transaction-handle members
/// (<c>Library/Components/Providers/Data/DataProvider.vb</c>, lines 70 to 74),
/// yet not one of the 1,632 lines of <c>PortalController</c> ever invoked them.
/// Routing that whole sequence through a single <see cref="SaveChangesAsync"/>
/// call is what converts a write that can half-succeed into one that either
/// wholly succeeds or wholly does not.
/// </para>
/// <para>
/// Provider neutrality. This contract exposes no persistence session, no
/// transaction handle, no query surface and no change-tracking mechanics, so an
/// application service can commit a batch while remaining unable to name, and
/// therefore unable to depend upon, whatever technology stores it. The legacy
/// equivalent was reached through a reflection-created static singleton
/// accessor; the member below is supplied by constructor injection instead,
/// which is precisely what makes it substitutable under test.
/// </para>
/// <para>
/// Lifetime. No release member is declared. An implementation is registered with
/// a per-request scope and the dependency-injection container owns its teardown,
/// so a consumer must never attempt to manage it.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Atomically persists every change staged since the previous commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only mechanism by which a staged change becomes durable, and
    /// the single point at which the multi-table portal creation sequence
    /// described on <see cref="IUnitOfWork"/> is committed as one indivisible
    /// operation. Should persistence fail, no part of the batch is applied.
    /// </para>
    /// <para>
    /// Server-generated keys become readable only once this call has returned.
    /// The legacy provider surfaced each new identity as the return value of an
    /// <c>Add</c> member: twenty-eight of them, every one declared
    /// <c>As Integer</c> because the procedure behind it ended in
    /// <c>SCOPE_IDENTITY()</c>. The sibling abstractions here deliberately do
    /// not, because the store has not yet assigned an identifier at the moment a
    /// row is staged; an add operation stages the row and yields nothing. Once
    /// this method completes, the identity property of each staged entity
    /// carries its assigned key. The legacy guarantee that a new key is
    /// observable is thus preserved, but it is observed on the entity instead of
    /// through a return value, and that is exactly what lets several inserts
    /// across several tables share one commit.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// Propagates notification that the operation should be abandoned. When
    /// cancellation is observed before the batch is committed, no change is
    /// applied.
    /// </param>
    /// <returns>
    /// A task whose result is the affected-row count: the number of state
    /// entries written to the underlying store. Zero indicates that nothing was
    /// staged.
    /// </returns>
    // MIGRATION: the legacy provider returned a generated key directly from each
    // Add member; key visibility is now deferred until this commit completes and
    // is observed on the entity. Recorded in MIGRATION_NOTES.md.
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
