using System.Data;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Flushes the changes tracked by one <see cref="DnnDbContext"/> and, when a workflow needs more than one
/// flush, supplies the explicit transaction that makes them durable together.
/// </summary>
/// <remarks>
/// <para>
/// The legacy write paths had no unit of work at all. Creating a tenant, for instance, issued separate
/// stored procedure calls against the tenant table, the alias table, the roles table, the pages table and
/// the modules table with no transaction spanning them, so a failure part way through left a half-built
/// tenant behind.
/// </para>
/// <para>
/// The surface stays as narrow as the reasoning allows: a flush, a scope, a disposal, and two isolation
/// levels chosen from an enumeration declared in the Domain rather than the provider's own. No connection,
/// no savepoint and no query surface is reachable through it, so nothing above this assembly can name the
/// store even while holding a transaction.
/// </para>
/// </remarks>
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="UnitOfWork"/> class.</summary>
    /// <param name="dbContext">The context whose tracked changes this instance commits.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dbContext"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The constructor is public on a type that is not, which is deliberate: the container registration and
    /// this assembly are the only things that can name either this type or the context it takes, so a
    /// public constructor grants no reach that internal accessibility has not already withheld.
    /// </remarks>
    public UnitOfWork(DnnDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The provider's own answer is returned rather than a flag maintained here, so the property cannot
    /// drift from the transaction it describes - including on a scope that was abandoned by disposal rather
    /// than committed, which clears the provider's handle without any code here running.
    /// </remarks>
    public bool HasActiveTransaction => _dbContext.Database.CurrentTransaction is not null;

    /// <summary>Flushes every change the shared context is tracking.</summary>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="ConcurrencyConflictException">
    /// Thrown when a tracked row was changed or removed by another caller between the read and the flush,
    /// whether the mapper reported it as an affected-row count of zero or the engine reported it as a
    /// serialisation failure.
    /// </exception>
    /// <exception cref="DbUpdateException">
    /// Thrown when the store rejects the write, for example on a unique index violation that a pre-flight
    /// check could not exclude because of a concurrent insert.
    /// </exception>
    // Legacy portal, user, role, tab, module and alias creation each issued its own stored procedure with
    // no transaction spanning them - portal creation writes the portal row, the administrator, the roles,
    // the pages, the modules and the alias as separately durable steps, and its only recovery is an
    // in-process compensation call that cannot run if the process is terminated.
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw ConcurrencyConflictException.ForLostUpdate(exception);
        }
        catch (DbUpdateException exception) when (DuplicateKeyTranslator.Describes(
            exception,
            out string? constraintName))
        {
            throw DuplicateKeyException.ForConstraint(constraintName, exception);
        }
        catch (DbUpdateException exception) when (LostUpdateTranslator.Describes(exception))
        {
            throw ConcurrencyConflictException.ForLostUpdate(exception);
        }

        // ⚠ THE LAST CLAUSE IS DELIBERATELY NOT NARROWED TO DbUpdateException, AND ITS ABSENCE WAS MEASURED AS
        // A 500. Not every store refusal reaches here wrapped: the mapper wraps a failure raised while
        // EXECUTING the update, but a failure raised while OPENING the flush - the savepoint the provider
        // issues when a transaction is already in hand - is raised by the client library and arrives
        // unwrapped. Under a write race that was exactly the shape observed, so the loser of the race
        // escaped all three clauses above and was answered as a server fault. The filter is the same
        // predicate, so this clause admits nothing the narrowed ones would have refused; it only stops the
        // wrapper's presence deciding whether a lost update is reported as one.
        catch (Exception exception) when (LostUpdateTranslator.Describes(exception))
        {
            throw ConcurrencyConflictException.ForLostUpdate(exception);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A transaction is already open on this context.</exception>
    /// <remarks>
    /// The isolation enumeration is the Domain's own two-member one, mapped here onto the ADO type. The
    /// default member is passed through as <see cref="IsolationLevel.Unspecified"/> rather than as a named
    /// level, so the store's configured default continues to apply and this code does not silently become
    /// the place where an installation's isolation is decided.
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
        // the scope creates a strategy.
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

    /// <inheritdoc />
    public Task<ITransactionScope> JoinOrBeginTransactionAsync(
        TransactionIsolation isolation = TransactionIsolation.Default,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ITransactionScope>(JoinedTransactionScope.Instance);
        }

        return BeginTransactionAsync(isolation, cancellationToken);
    }

    /// <summary>Wraps one provider transaction as the Domain's technology-free scope.</summary>
    private sealed class TransactionScope : ITransactionScope
    {
        private readonly DnnDbContext _dbContext;
        private readonly IDbContextTransaction _transaction;

        /// <summary>Initialises a new scope over an open provider transaction.</summary>
        /// <param name="dbContext">The context whose retry suppression this scope owns.</param>
        /// <param name="transaction">The open transaction.</param>
        /// <remarks>
        /// No argument is guarded, and that is not an omission. The type is private to <see
        /// cref="UnitOfWork"/> and is constructed at exactly one call site, which has just proved both
        /// arguments non-null - the context by the outer constructor's guard and the transaction by having
        /// been returned from the provider.
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
        /// The transaction is disposed first, so the flag is never cleared while the provider still holds
        /// an open transaction that a retrying strategy would object to.
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

    /// <summary>Represents participation in a transaction owned by an outer application operation.</summary>
    /// <remarks>
    /// Commit and disposal intentionally do nothing: only the scope that opened the provider transaction
    /// may decide whether the complete operation commits. This lets a composed service retain its
    /// standalone atomicity without creating a nested transaction or prematurely committing its caller's
    /// work.
    /// </remarks>
    private sealed class JoinedTransactionScope : ITransactionScope
    {
        internal static readonly JoinedTransactionScope Instance = new();

        private JoinedTransactionScope()
        {
        }

        /// <inheritdoc />
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
