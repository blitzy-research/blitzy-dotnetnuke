using System.Reflection;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Pins the one decision the transaction-aware execution strategy exists to make: whether a failed
/// operation may be retried.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why one property needs its own suite.</strong> Retrying is configured because SQL Server
/// produces genuinely transient faults, and the retry is applied by the provider around a single operation.
/// </para>
/// <para>
/// <strong>And it is a property whose value is not fixed.</strong> The whole point is that the same
/// strategy instance answers differently at different moments in one request - retrying before a
/// transaction opens, declining while one is open, and retrying again once it has closed.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class TransactionAwareExecutionStrategyTests
{
    private readonly ApiTestFixture _fixture;

    /// <summary>
    /// Initialises a new instance of the <see cref="TransactionAwareExecutionStrategyTests"/> class.
    /// </summary>
    /// <param name="fixture">The shared host, whose container supplies the context and unit of work.</param>
    public TransactionAwareExecutionStrategyTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The composed application really does use this strategy.</summary>
    /// <remarks>
    /// Asserted first because every other case in this suite would pass vacuously against the provider's
    /// own strategy in one direction: a non-retrying strategy would satisfy the "declines inside a
    /// transaction" assertions for entirely the wrong reason.
    /// </remarks>
    [Fact]
    public void TheComposedApplication_ResolvesTheTransactionAwareStrategy()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();

        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();

        strategy.Should().BeOfType<TransactionAwareExecutionStrategy>(
            "the factory registered with the SQL Server options is what substitutes this strategy for the "
            + "provider's own, and without it every transactional write would meet the framework's refusal "
            + "to run a retrying strategy inside a user transaction");

        typeof(TransactionAwareExecutionStrategy).BaseType!.Name.Should().Be(
            "SqlServerRetryingExecutionStrategy",
            "the transient-fault list, the retry count and the back-off are the provider's, and rewriting "
            + "them here would mean maintaining Microsoft's list of retryable error numbers by hand");
    }

    /// <summary>Outside a transaction the strategy retries.</summary>
    /// <remarks>
    /// This is the resilience the configuration was added for, and it is the case that would be silently
    /// lost by an implementation that declined always - which is what returning a constant <see
    /// langword="false"/> would do, and it would pass every transaction case below.
    /// </remarks>
    [Fact]
    public void RetriesOnFailure_IsTrueOutsideATransaction()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();

        context.ExplicitTransactionOpen.Should().BeFalse(
            "a freshly resolved context has no transaction announced on it");

        context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue(
            "an ordinary single-statement write must survive a transient SQL Server fault, which is the "
            + "entire reason a retrying strategy is configured");
    }

    /// <summary>Inside a transaction opened through the unit of work the strategy declines to retry.</summary>
    /// <remarks>
    /// The transaction is opened the way production opens one - through <see
    /// cref="IUnitOfWork.BeginTransactionAsync"/> - rather than by setting the announcement flag directly,
    /// so this covers the announcement being made at all and being made before the transaction opens.
    /// </remarks>
    [Fact]
    public async Task RetriesOnFailure_IsFalseInsideATransactionOpenedThroughTheUnitOfWork()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await using ITransactionScope transaction = await unitOfWork.BeginTransactionAsync();

        context.ExplicitTransactionOpen.Should().BeTrue(
            "the unit of work announces the transaction on the context before opening it, because the "
            + "strategy is created before the first write inside the scope");

        context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeFalse(
            "retrying one statement of a multi-statement transaction would either double-apply work or "
            + "apply it outside the atomic unit the caller believed it was inside, and Entity Framework "
            + "Core throws rather than allow it");
    }

    /// <summary>Committing restores retrying.</summary>
    /// <remarks>
    /// The announcement must be withdrawn on the way out, not merely made on the way in. A flag left set
    /// would suppress retrying for the remainder of the request - a silent, permanent loss of resilience on
    /// every operation after the first transaction, which no later call could diagnose because nothing
    /// fails.
    /// </remarks>
    [Fact]
    public async Task RetriesOnFailure_IsTrueAgainAfterACommit()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await using (ITransactionScope transaction = await unitOfWork.BeginTransactionAsync())
        {
            context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeFalse();

            await transaction.CommitAsync();
        }

        context.ExplicitTransactionOpen.Should().BeFalse(
            "the announcement is withdrawn when the scope closes, or resilience would be lost for the rest "
            + "of the request");
        context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue(
            "the next single-statement write is no longer inside a transaction and must be retryable again");
    }

    /// <summary>Rolling back restores retrying too.</summary>
    [Fact]
    public async Task RetriesOnFailure_IsTrueAgainAfterARollback()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await using (ITransactionScope transaction = await unitOfWork.BeginTransactionAsync())
        {
            context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeFalse();
        }

        context.ExplicitTransactionOpen.Should().BeFalse(
            "an abandoned transaction must withdraw the announcement exactly as a committed one does, and "
            + "this is the path taken when something has already failed");
        context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();
    }

    /// <summary>One strategy instance answers differently before, during and after a transaction.</summary>
    /// <remarks>
    /// This is the assertion that separates the working implementation from the rejected one. The strategy
    /// is created ONCE, before any transaction exists, and is then read at three moments.
    /// </remarks>
    [Fact]
    public async Task TheSameStrategyInstance_ReEvaluatesRatherThanCapturingItsAnswer()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();

        bool beforeTransaction = strategy.RetriesOnFailure;

        bool duringTransaction;
        await using (ITransactionScope transaction = await unitOfWork.BeginTransactionAsync())
        {
            duringTransaction = strategy.RetriesOnFailure;

            await transaction.CommitAsync();
        }

        bool afterTransaction = strategy.RetriesOnFailure;

        beforeTransaction.Should().BeTrue("the strategy is created before any transaction exists");
        duringTransaction.Should().BeFalse(
            "the one instance must notice the transaction that opened after it was created, which an "
            + "implementation that decided at construction could not do");
        afterTransaction.Should().BeTrue(
            "and must notice that the transaction closed, or the scope would lose resilience permanently");
    }

    /// <summary>The announcement flag alone decides the answer.</summary>
    /// <remarks>
    /// Isolates the predicate from the transaction machinery: with no transaction anywhere near the
    /// context, the announcement is set and cleared directly and the strategy follows it.
    /// </remarks>
    [Fact]
    public void RetriesOnFailure_FollowsTheAnnouncementOnTheContext()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();

        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();

        try
        {
            context.ExplicitTransactionOpen = true;

            strategy.RetriesOnFailure.Should().BeFalse(
                "the announcement is the whole mechanism, and it is read from the live context rather than "
                + "from the database facade - which cannot be asked, because resolving it resolves the "
                + "factory that is asking");

            context.ExplicitTransactionOpen = false;

            strategy.RetriesOnFailure.Should().BeTrue("and clearing it restores retrying immediately");
        }
        finally
        {
            context.ExplicitTransactionOpen = false;
        }
    }

    /// <summary>
    /// The RETRY LOOP is suspended inside a unit-of-work transaction as well, not only the first-execution
    /// check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ THIS IS THE ASSERTION WHOSE ABSENCE LET A REAL DEFECT SHIP, and every test above it passed while
    /// that defect was live. The two members are read by different parts of the base class:
    /// <c>RetriesOnFailure</c> is consulted once, to decide whether to REFUSE an operation that already holds
    /// a user transaction, while <c>ShouldRetryOn</c> is what the loop asks after each failure. Overriding
    /// only the first therefore bought the unit of work the right to hold its own transaction WITHOUT buying
    /// it protection from being retried inside one.
    /// </para>
    /// <para>
    /// What that cost: the engine aborted one participant of a write race, the loop treated the abort as
    /// transient and re-ran the flush, and the flush opened by issuing a savepoint against a transaction the
    /// engine had already discarded - error 628, which is not transient, describes nothing the caller did,
    /// and reached the caller as a 500.
    /// </para>
    /// <para>
    /// Reached by reflection because the member is protected, which is the framework's shape rather than a
    /// choice made here. The alternative - provoking a real deadlock and observing the answer - is what
    /// <c>SimultaneousWriteRaceTests</c> does through the API; this asserts the mechanism directly so that a
    /// regression is named at its cause rather than diagnosed from a status code.
    /// </para>
    /// </remarks>
    [Fact]
    public void ShouldRetryOn_FollowsTheAnnouncementOnTheContext()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();

        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();

        MethodInfo shouldRetryOn = strategy.GetType()
            .GetMethod("ShouldRetryOn", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The execution strategy no longer declares ShouldRetryOn, so the retry loop is driven by "
                + "something this test does not describe.");

        // A timeout is the provider's canonical transient fault, so the base classification answers true for
        // it. Any exception the base class retries would serve; this one makes the contrast unambiguous.
        var transient = new TimeoutException("Provoked, to be classified rather than thrown.");

        try
        {
            context.ExplicitTransactionOpen = false;

            shouldRetryOn.Invoke(strategy, [transient]).Should().Be(
                true,
                "outside a unit-of-work transaction the provider's own transient classification must be "
                + "left exactly as it ships, or the resilience this strategy exists to keep is lost");

            context.ExplicitTransactionOpen = true;

            shouldRetryOn.Invoke(strategy, [transient]).Should().Be(
                false,
                "inside a unit-of-work transaction the same fault must NOT be retried: the transaction the "
                + "retry would run in may already have been discarded by the engine, and re-running the "
                + "flush against it fails with a savepoint error that reaches the caller as a server fault");

            context.ExplicitTransactionOpen = false;

            shouldRetryOn.Invoke(strategy, [transient]).Should().Be(
                true,
                "and the suspension lifts with the announcement, so a scope does not cost the rest of the "
                + "request its resilience");
        }
        finally
        {
            context.ExplicitTransactionOpen = false;
        }
    }
}
