using System.Data;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Flushes the changes tracked by one <see cref="DnnDbContext"/> and, when a workflow needs more than
/// one flush, supplies the explicit transaction that makes them durable together.
/// </summary>
/// <remarks>
/// <para>
/// The legacy write paths had no unit of work at all. Creating a tenant, for instance, issued
/// separate stored procedure calls against the tenant table, the alias table, the roles table, the
/// pages table and the modules table with no transaction spanning them, so a failure part way
/// through left a half-built tenant behind.
/// </para>
/// <para>
/// Two boundaries, and the distinction is load-bearing. <see cref="SaveChangesAsync"/> flushes what
/// the shared scoped context is tracking; the provider wraps that one flush in a transaction of its
/// own, so a single-flush operation is atomic without any further ceremony. A workflow that needs
/// several flushes - tenant creation, because three tenant columns need keys the store assigns during
/// the first flush and the credential lives in an external membership store, and the last-tenant guard
/// on deletion, because it counts and then deletes - opens a scope through
/// <see cref="BeginTransactionAsync"/> and becomes durable only at <c>ITransactionScope.CommitAsync</c>.
/// For those workflows the alternative was an in-process compensation routine, which cannot run if the
/// process is terminated, plus a count-then-delete race that can empty an installation; a scope that
/// is rolled back by disposal is strictly safer than either.
/// </para>
/// <para>
/// The surface stays as narrow as the reasoning allows: a flush, a scope, a disposal, and two isolation
/// levels chosen from an enumeration declared in the Domain rather than the provider's own. No
/// connection, no savepoint and no query surface is reachable through it, so nothing above this
/// assembly can name the store even while holding a transaction.
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

    /// <inheritdoc />
    /// <remarks>
    /// The provider's own answer is returned rather than a flag maintained here, so the property cannot
    /// drift from the transaction it describes - including on a scope that was abandoned by disposal
    /// rather than committed, which clears the provider's handle without any code here running.
    /// </remarks>
    public bool HasActiveTransaction => _dbContext.Database.CurrentTransaction is not null;

    /// <summary>
    /// Flushes every change the shared context is tracking.
    /// </summary>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The number of rows affected.</returns>
    /// <exception cref="DbUpdateConcurrencyException">
    /// Thrown when a tracked row was changed or removed by another caller between the read and the
    /// flush.
    /// </exception>
    /// <exception cref="DbUpdateException">
    /// Thrown when the store rejects the write, for example on a unique index violation that a
    /// pre-flight check could not exclude because of a concurrent insert.
    /// </exception>
    // MIGRATION: legacy portal, user, role, tab, module and alias creation each issued its own stored
    // procedure with no transaction spanning them - portal creation
    // (Library/Components/Portal/PortalController.vb, line 980) writes the portal row, the administrator,
    // the roles, the pages, the modules and the alias as separately durable steps, and its only recovery
    // is an in-process compensation call that cannot run if the process is terminated. The provider even
    // declared transaction members (Library/Components/Providers/Data/DataProvider.vb, lines 70 to 74)
    // that no caller invoked.
    //
    // Every repository in this assembly stages against the one scoped context flushed here, so a single
    // flush applies whole or not at all: the provider wraps it in its own transaction. A workflow that
    // flushes more than once is atomic only inside a scope from BeginTransactionAsync, whose CommitAsync
    // is the durability boundary. Gaining atomicity is a deliberate behavioural improvement over the
    // legacy write paths and is recorded as such in MIGRATION_NOTES.md.
    //
    // MIGRATION: SEC-F6. A UNIQUE-INDEX VIOLATION IS TRANSLATED HERE, AND THIS IS THE ONLY PLACE IT COULD
    // BE. Every create path in the application layer asks the store whether a value is taken before
    // inserting, which answers the ordinary case precisely and cannot answer the concurrent one: two
    // requests carrying the same name both read "not taken", and the loser's insert is refused by the
    // index. Measured on a live installation, ten simultaneous identical role creations produced one 201,
    // seven 409 and two 500 - the two that lost the race after passing the pre-check - and eight
    // simultaneous identical alias creations produced one 201, four 409 and three 500. Exactly one row was
    // stored each time, so the store had behaved correctly and only the report was wrong.
    //
    // The api project references neither this mapper nor the database client, by design, so the provider
    // exception reached its handler as the general case and was published as a server fault. It cannot be
    // classified up there, and it must not be: naming the client from the transport would put a provider
    // dependency in the layer whose whole purpose is to be free of one. This assembly is the only one that
    // may name it, so the provider fault becomes a Domain-level signal here and travels upward as a type
    // every layer is entitled to catch.
    //
    // The two numbers are the store's own: 2627 is a unique CONSTRAINT violation and 2601 a unique INDEX
    // violation, and this schema raises both - PRIMARY KEY and UNIQUE constraints give 2627 while the
    // CREATE UNIQUE INDEX statements the 88-script chain leaves behind give 2601. Both are matched, and
    // nothing else is: a foreign-key violation, a check violation or a deadlock is a different failure with
    // a different correct answer and continues to travel as it did.
    //
    // DbUpdateConcurrencyException is excluded EXPLICITLY even though the number test would exclude it
    // anyway. It derives from DbUpdateException, so a future edit that loosened the inner test would
    // silently start reporting a lost update as a duplicate - and the two demand opposite things of a
    // caller, one to re-read and retry, the other to choose a different value.
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException
            && DuplicateKeyTranslator.Describes(exception, out string? constraintName))
        {
            throw DuplicateKeyException.ForConstraint(constraintName, exception);
        }
    }

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

    /// <summary>
    /// Represents participation in a transaction owned by an outer application operation.
    /// </summary>
    /// <remarks>
    /// Commit and disposal intentionally do nothing: only the scope that opened the provider transaction may
    /// decide whether the complete operation commits. This lets a composed service retain its standalone
    /// atomicity without creating a nested transaction or prematurely committing its caller's work.
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
