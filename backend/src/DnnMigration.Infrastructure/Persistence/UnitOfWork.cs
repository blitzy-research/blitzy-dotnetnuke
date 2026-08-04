using System.Data;
using DnnMigration.Domain.Abstractions.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Commits every change tracked by one <see cref="DnnDbContext"/> as a single unit.
/// </summary>
/// <remarks>
/// <para>
/// The legacy write paths had no unit of work at all. Creating a tenant, for instance, issued
/// separate stored procedure calls against the tenant table, the alias table, the roles table, the
/// pages table and the modules table with no transaction spanning them, so a failure part way
/// through left a half-built tenant behind. Because every repository in this assembly shares the
/// scoped context that this type commits, a single call now sends all of those writes inside one
/// transaction that the provider opens implicitly.
/// </para>
/// <para>
/// EXPLICIT TRANSACTIONS ARE NOW OFFERED, AND THE REASON THEY WERE NOT IS WORTH KEEPING. This type
/// previously exposed the commit alone, on the reasoning that the provider already wraps one save in a
/// transaction and that exposing transaction control would invite the application layer to hold a
/// transaction open across awaits it does not own. The first half is true and unchanged; the second
/// half was answered in the wrong direction. Two sequences in this application cannot be expressed as
/// one save - tenant creation, because three tenant columns need keys the store assigns during the
/// first commit and the credential lives in an external membership store, and the last-tenant guard on
/// deletion, because it counts and then deletes - and for those two the alternative to a transaction
/// was an in-process compensation routine, which cannot run if the process is terminated, plus a
/// count-then-delete race that can empty an installation. A scope that is rolled back by disposal is
/// strictly safer than either.
/// </para>
/// <para>
/// The surface stays as narrow as the reasoning allows: a commit, a disposal, and two isolation levels
/// chosen from an enumeration declared in the Domain rather than the provider's own. No connection, no
/// savepoint and no query surface is reachable through it, so nothing above this assembly can name the
/// store even while holding a transaction.
/// </para>
/// </remarks>
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly DnnDbContext _dbContext;

    /// <summary>
    /// Initialises a new instance of the <see cref="UnitOfWork"/> class.
    /// </summary>
    /// <param name="dbContext">The context whose tracked changes this instance commits.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dbContext"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The constructor is public on a type that is not, which is deliberate: the container registration
    /// and this assembly are the only things that can name either this type or the context it takes, so a
    /// public constructor grants no reach that internal accessibility has not already withheld. Nothing
    /// beyond the guard happens here - no scope is opened, no query is issued and no state is read - so
    /// resolving the unit of work costs nothing on a request that never commits.
    /// </remarks>
    public UnitOfWork(DnnDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    /// <summary>
    /// Commits every tracked change.
    /// </summary>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="DbUpdateConcurrencyException">
    /// Thrown when a tracked row was changed or removed by another caller. Application services
    /// translate this into a conflict result rather than letting it escape as a server fault.
    /// </exception>
    /// <exception cref="DbUpdateException">
    /// Thrown when the store rejects the write, for example on a unique index violation that a
    /// pre-flight check could not exclude because of a concurrent insert.
    /// </exception>
    // MIGRATION: this one call is now the atomic commit boundary. Legacy portal, user, role, tab, module
    // and alias creation each issued its own stored procedure with no transaction spanning them - portal
    // creation (Library/Components/Portal/PortalController.vb, line 980) writes the portal row, the
    // administrator, the roles, the pages, the modules and the alias as separately durable steps, and its
    // only recovery is an in-process compensation call that cannot run if the process is terminated - so a
    // failure part way through left a half-built portal behind. The provider even declared transaction
    // members (Library/Components/Providers/Data/DataProvider.vb, lines 70 to 74) that no caller invoked.
    // Every repository in this assembly stages against the one scoped context committed here, so the whole
    // batch now applies or none of it does. Gaining atomicity is a deliberate behavioural improvement over
    // the legacy write paths, not an incidental effect, and is recorded as such in MIGRATION_NOTES.md.
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A transaction is already open on this context. Refused rather than ignored: a caller that
    /// believes it has opened a transaction and has not is worse off than one that fails, because it
    /// will treat a partially durable sequence as atomic.
    /// </exception>
    /// <remarks>
    /// The isolation enumeration is the Domain's own two-member one, mapped here onto the ADO type. The
    /// default member is passed through as <see cref="IsolationLevel.Unspecified"/> rather than as a
    /// named level, so the store's configured default continues to apply and this code does not silently
    /// become the place where an installation's isolation is decided.
    /// </remarks>
    public async Task<ITransactionScope> BeginTransactionAsync(
        TransactionIsolation isolation = TransactionIsolation.Default,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A transaction is already open on this unit of work. Nested transactions are not "
                + "supported: the inner scope could not be rolled back independently of the outer one, "
                + "so a caller relying on it would be relying on a guarantee that does not exist.");
        }

        IsolationLevel level = isolation switch
        {
            TransactionIsolation.Serializable => IsolationLevel.Serializable,
            _ => IsolationLevel.Unspecified,
        };

        // Announced BEFORE the transaction is opened, because the registered execution strategy factory
        // reads this flag and the substitution has to be in place by the time the first SaveChanges inside
        // the scope creates a strategy. See DnnDbContext.ExplicitTransactionOpen for why the factory cannot
        // simply ask the database facade whether a transaction is open.
        _dbContext.ExplicitTransactionOpen = true;

        IDbContextTransaction transaction;
        try
        {
            transaction = await _dbContext.Database
                .BeginTransactionAsync(level, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Nothing was opened, so nothing may be left announced. Leaving the flag set would suppress
            // retrying for the remainder of the request on the strength of a transaction that does not
            // exist - a silent loss of resilience that no later call could diagnose.
            _dbContext.ExplicitTransactionOpen = false;
            throw;
        }

        return new TransactionScope(_dbContext, transaction);
    }

    /// <summary>
    /// Wraps one provider transaction as the Domain's technology-free scope.
    /// </summary>
    /// <remarks>
    /// Disposal without a commit rolls back. That is the provider's own behaviour rather than something
    /// implemented here, and it is the reason the abstraction declares no rollback member: the safe
    /// outcome is the default one, including on a path whose author did not think about failure.
    /// </remarks>
    private sealed class TransactionScope : ITransactionScope
    {
        private readonly DnnDbContext _dbContext;
        private readonly IDbContextTransaction _transaction;

        /// <summary>
        /// Initialises a new scope over an open provider transaction.
        /// </summary>
        /// <param name="dbContext">The context whose retry suppression this scope owns.</param>
        /// <param name="transaction">The open transaction.</param>
        /// <remarks>
        /// No argument is guarded, and that is not an omission. The type is private to
        /// <see cref="UnitOfWork"/> and is constructed at exactly one call site, which has just proved both
        /// arguments non-null - the context by the outer constructor's guard and the transaction by having
        /// been returned from the provider. A guard here would be unreachable code asserting something the
        /// compiler and the single caller already establish.
        /// </remarks>
        public TransactionScope(DnnDbContext dbContext, IDbContextTransaction transaction)
        {
            _dbContext = dbContext;
            _transaction = transaction;
        }

        /// <inheritdoc />
        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            _transaction.CommitAsync(cancellationToken);

        /// <inheritdoc />
        /// <remarks>
        /// The retry suppression is lifted here rather than on commit, because it must outlast a scope that
        /// was ABANDONED as well as one that was committed - and disposal is the only step both paths take.
        /// The transaction is disposed first, so the flag is never cleared while the provider still holds an
        /// open transaction that a retrying strategy would object to.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _dbContext.ExplicitTransactionOpen = false;
            }
        }
    }
}
