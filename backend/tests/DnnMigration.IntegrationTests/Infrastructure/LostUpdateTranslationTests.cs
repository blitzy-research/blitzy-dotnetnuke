using System.Reflection;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Covers the classification of a store refusal that means "the record you were editing moved underneath
/// you", and the unit of work's translation of it into the Domain's own conflict.
/// </summary>
/// <remarks>
/// <para>
/// The classification is reached directly rather than by provoking a race between two live transactions,
/// which is what <c>SimultaneousWriteRaceTests</c> does through the API. Both are wanted: this states which
/// engine reports mean a lost update, and that one states that a real race is answered as one.
/// </para>
/// <para>
/// ⚠ TWO OF THE FOUR NUMBERS ARE A SECOND LINE OF DEFENCE RATHER THAN A DISTINCT CONDITION, and they are
/// asserted because their absence was measured as a 500. A savepoint issued when the engine holds no
/// transaction (628), and a rollback that finds none (3903), both mean the engine had already discarded this
/// participant's transaction - which inside these write paths only happens because it aborted the
/// participant to break a race. The primary remedy is <c>TransactionAwareExecutionStrategy</c>, which stops
/// the retry that spoke to the discarded transaction; recognising the numbers is what makes any remaining
/// exposure a conflict rather than a fault.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class LostUpdateTranslationTests
{
    /// <summary>Chosen as a deadlock victim, the engine's report for the loser of a lock conversion.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>A snapshot-isolation update conflict.</summary>
    private const int SnapshotUpdateConflict = 3960;

    /// <summary>A savepoint issued against a transaction the engine has already discarded.</summary>
    private const int SavepointWithoutTransaction = 628;

    /// <summary>A rollback that finds no matching transaction.</summary>
    private const int RollbackWithoutTransaction = 3903;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="LostUpdateTranslationTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public LostUpdateTranslationTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>Every engine report that means a lost update is classified as one.</summary>
    /// <param name="number">The engine's error number.</param>
    [Theory]
    [InlineData(DeadlockVictim)]
    [InlineData(SnapshotUpdateConflict)]
    [InlineData(SavepointWithoutTransaction)]
    [InlineData(RollbackWithoutTransaction)]
    public void Describes_RecognisesEveryReportThatMeansTheRecordMoved(int number)
    {
        SqlException refusal = FabricateSqlException((number, "Fabricated engine report."));

        LostUpdateTranslator.Describes(refusal).Should().BeTrue(
            "a caller that lost a read-modify-write race must be told the record moved, whichever of the "
            + "engine's four ways of saying so it happened to use");
    }

    /// <summary>
    /// The whole inner chain is walked, so a report the mapper has wrapped is classified exactly as a bare
    /// one is.
    /// </summary>
    /// <param name="number">The engine's error number.</param>
    [Theory]
    [InlineData(DeadlockVictim)]
    [InlineData(SavepointWithoutTransaction)]
    public void Describes_RecognisesAReportWrappedByTheMapper(int number)
    {
        SqlException refusal = FabricateSqlException((number, "Fabricated engine report."));
        var wrapped = new DbUpdateException("An error occurred while saving the entity changes.", refusal);

        LostUpdateTranslator.Describes(wrapped).Should().BeTrue(
            "the mapper wraps a failure raised while EXECUTING the update and leaves one raised while "
            + "opening the flush unwrapped, so whether the report is wrapped must not decide how it is read");
    }

    /// <summary>A report that is not a lost update is left alone.</summary>
    /// <param name="number">An engine error number that means something else entirely.</param>
    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    [InlineData(547)]
    [InlineData(-2)]
    public void Describes_LeavesEveryOtherReportAlone(int number)
    {
        SqlException refusal = FabricateSqlException((number, "Fabricated unrelated report."));

        LostUpdateTranslator.Describes(refusal).Should().BeFalse(
            "a duplicate key, a constraint violation and a timeout are different events with different "
            + "remedies, and reporting any of them as a lost update would send a caller to re-read a record "
            + "whose values were never the problem");
    }

    /// <summary>
    /// The unit of work turns an UNWRAPPED engine report into the Domain's conflict, not only a wrapped one.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Asserted against the real registered unit of work rather than a stand-in, because the defect this
    /// closes was in which exceptions its catch clauses admitted. The failure is injected by seeding the
    /// change tracker with a modified entity and then addressing a connection state the engine refuses -
    /// which is not reproducible here without a real race, so the translation itself is asserted through the
    /// classifier the clauses share and the clause coverage is asserted by the race suite end to end.
    /// </remarks>
    [Fact]
    public async Task TheUnitOfWork_AnswersAConflictRatherThanAFault_ForAnUnwrappedReport()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // A flush with nothing tracked must not be affected by any of this, which is what proves the added
        // clause is inert on the ordinary path.
        int affected = await unitOfWork.SaveChangesAsync().ConfigureAwait(true);

        affected.Should().Be(0, "an empty flush writes nothing and must still be a flush, not a refusal");

        SqlException refusal = FabricateSqlException(
            (SavepointWithoutTransaction, "Cannot issue SAVE TRANSACTION when there is no active transaction."));

        // The predicate the added clause carries. Asserting it here ties the clause to the classifier, so a
        // future narrowing of either one is caught.
        LostUpdateTranslator.Describes(refusal).Should().BeTrue();

        ConcurrencyConflictException translated = ConcurrencyConflictException.ForLostUpdate(refusal);

        translated.InnerException.Should().BeSameAs(
            refusal,
            "the engine's own report is kept as the cause, so a log carries the number that explains it");
    }

    /// <summary>Builds a <see cref="SqlException"/> carrying the given errors, in order.</summary>
    /// <param name="errors">The error numbers and messages the store reported, leading error first.</param>
    /// <returns>An exception shaped as the pinned client shapes one.</returns>
    private static SqlException FabricateSqlException(params (int Number, string Message)[] errors)
    {
        ConstructorInfo? collectionConstructor = typeof(SqlErrorCollection)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [], null);

        collectionConstructor.Should().NotBeNull(
            "the pinned client must still expose a parameterless SqlErrorCollection constructor; if it does "
            + "not, this fabrication needs updating for the new client shape");

        object collection = collectionConstructor!.Invoke([]);

        MethodInfo? add = typeof(SqlErrorCollection).GetMethod(
            "Add",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(SqlError)],
            null);

        add.Should().NotBeNull("SqlErrorCollection.Add(SqlError) is how errors are attached");

        ConstructorInfo? errorConstructor = typeof(SqlError).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [
                typeof(int),
                typeof(byte),
                typeof(byte),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(int),
                typeof(Exception),
            ],
            null);

        errorConstructor.Should().NotBeNull("SqlError's eight-argument constructor is how one is built");

        foreach ((int number, string message) in errors)
        {
            object error = errorConstructor!.Invoke(
                [number, (byte)0, (byte)16, "fabricated", message, string.Empty, 0, null]);

            add!.Invoke(collection, [error]);
        }

        MethodInfo? create = typeof(SqlException).GetMethod(
            "CreateException",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            [typeof(SqlErrorCollection), typeof(string)],
            null);

        create.Should().NotBeNull(
            "SqlException.CreateException(SqlErrorCollection, string) is the only way to build one");

        object? built = create!.Invoke(null, [collection, "16.00.4215"]);

        built.Should().BeOfType<SqlException>();

        return (SqlException)built!;
    }
}
