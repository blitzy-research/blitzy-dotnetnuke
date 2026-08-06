using System.Reflection;
using DnnMigration.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DnnMigration.UnitTests.Infrastructure;

/// <summary>
/// Covers the recognition half of the duplicate-value translation: which store failures mean "this value is
/// already taken", and what the translator reports about the constraint that refused the write.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: SEC-F6. The defect these facts guard against was measured on a live installation, not
/// theorised. Ten simultaneous identical role creations produced one 201, seven 409 and <em>two 500s</em>,
/// with exactly one row stored; eight simultaneous identical alias creations produced one 201, four 409 and
/// three 500s, again with one row stored. In every 500 the store had behaved perfectly - it kept one record
/// and refused the rest - and the caller was told the server had failed. The sequential pre-check each of
/// those paths performs cannot close that window, because both racers read "not taken" before either
/// inserts, so the unique index is what settles it and the index's refusal has to be translated into the
/// same answer the pre-check would have given.
/// </para>
/// <para>
/// <strong>Why the recognition is tested here rather than only against a real race.</strong> This is the one
/// piece of the translation that depends on provider error numbers and on the shape of provider text, and a
/// race is a poor instrument for either: it cannot be made to produce error 547 on demand to prove the
/// translator declines it, it cannot produce a message with no quoted name to prove the name is optional, and
/// it cannot bury the fault three wrappers deep to prove the whole chain is walked. Each of those is
/// asserted below on a fabricated failure.
/// </para>
/// <para>
/// The complementary half is proven end to end against a live SQL Server by the contest facts in the API
/// suites - <c>CreateRole_SubmittedConcurrentlyUnderOneName_CreatesItOnceWithoutAnyServerFault</c> and its
/// counterparts for role groups, portal aliases, accounts and profile declarations - which fire a dozen
/// identical creations at once and assert one creation, conflicts for the rest, no 5xx and exactly one stored
/// row. Those exercise the real pipeline with nothing substituted, so between them and these facts both
/// halves are covered and neither is a substitute for the other. The integration project deliberately cannot
/// reach the persistence seam directly - the context and the unit of work are <c>internal</c> to the
/// Infrastructure assembly by design and that project declares no <c>InternalsVisibleTo</c> - which is why
/// the seam is measured here and its effect is measured there.
/// </para>
/// <para>
/// <strong>The fabrication uses private provider members, deliberately and visibly.</strong> A
/// <see cref="SqlException"/> cannot be constructed by a consumer - every constructor and factory is
/// non-public - so the only way to present the translator with a chosen error number is reflection over the
/// pinned client. That is a real cost and it is accepted knowingly: the alternative is to leave the
/// discrimination between "duplicate" and "every other store fault" untested, and mistaking a deadlock or a
/// foreign-key violation for a duplicate would answer 409 for a failure the caller cannot fix by changing a
/// value. The helpers below fail with an explicit message naming the member they could not find, so if the
/// pinned client version ever changes shape this reads as "the fabrication needs updating" rather than as a
/// mysterious null.
/// </para>
/// <para>
/// The translator is <c>internal</c> to the Infrastructure assembly and is reached here through the
/// <c>InternalsVisibleTo</c> that assembly already declares for this one. No type is made public for a
/// test's benefit.
/// </para>
/// </remarks>
public sealed class DuplicateKeyTranslationTests
{
    /// <summary>Error number SQL Server raises for a PRIMARY KEY or UNIQUE constraint violation.</summary>
    private const int UniqueConstraintViolation = 2627;

    /// <summary>Error number SQL Server raises for a UNIQUE INDEX violation.</summary>
    private const int UniqueIndexViolation = 2601;

    /// <summary>
    /// Both numbers a duplicated unique value is reported under are recognised, and the constraint the store
    /// named is reported with them.
    /// </summary>
    /// <param name="number">The error number the store raised.</param>
    /// <param name="message">The wording the store raised it with.</param>
    /// <param name="expectedName">The constraint name the caller of the translator must receive.</param>
    /// <remarks>
    /// Both numbers must be matched rather than whichever one a particular table happens to produce. The
    /// terminal legacy schema carries both kinds: <c>PK_Roles</c> is a constraint and raises 2627, while
    /// <c>IX_RoleName</c> over <c>(PortalID, RoleName)</c> is a <c>CREATE UNIQUE INDEX</c> and raises 2601.
    /// A translator that matched only one would answer 409 for a collision on one table and 500 for the
    /// same collision on the next.
    /// </remarks>
    [Theory]
    [InlineData(
        UniqueConstraintViolation,
        "Violation of UNIQUE KEY constraint 'IX_RoleName'. Cannot insert duplicate key in object 'dbo.Roles'."
        + " The duplicate key value is (0, Race).",
        "dbo.Roles")]
    [InlineData(
        UniqueIndexViolation,
        "Cannot insert duplicate key row in object 'dbo.PortalAlias' with unique index 'IX_PortalAlias'."
        + " The duplicate key value is (race.example).",
        "IX_PortalAlias")]
    public void Describes_RecognisesBothNumbersADuplicateIsReportedUnder(
        int number,
        string message,
        string expectedName)
    {
        SqlException refusal = FabricateSqlException((number, message));

        bool recognised = DuplicateKeyTranslator.Describes(refusal, out string? constraintName);

        recognised.Should().BeTrue(
            "error {0} means the store refused a duplicated unique value, so the caller must be told its "
            + "submission conflicts rather than that the server failed",
            number);

        constraintName.Should().Be(
            expectedName,
            "the LAST quoted token is the object that refused the write in both of the store's two wordings");
    }

    /// <summary>
    /// A store failure that is not a duplicated value is left alone, so it continues to be reported as the
    /// server-side fault it is.
    /// </summary>
    /// <param name="number">The error number the store raised.</param>
    /// <param name="description">What that number means, for the failure message.</param>
    /// <remarks>
    /// This is the half of the translation that protects the caller from a wrong answer rather than from a
    /// wrong status. A deadlock victim, a foreign-key violation, a timeout and a check-constraint refusal are
    /// all failures the caller cannot resolve by submitting a different value, so answering 409 - "your
    /// values conflict with an existing record" - would send it into a retry loop or a pointless edit. Only
    /// the two duplicate numbers may be claimed.
    /// </remarks>
    [Theory]
    [InlineData(547, "a foreign key violation")]
    [InlineData(1205, "a deadlock victim")]
    [InlineData(-2, "a command timeout")]
    [InlineData(2628, "a value too long for its column")]
    [InlineData(515, "a null in a column that forbids one")]
    public void Describes_DeclinesAStoreFailureThatIsNotADuplicatedValue(int number, string description)
    {
        SqlException refusal = FabricateSqlException((number, "Store failure 'dbo.Roles' occurred."));

        bool recognised = DuplicateKeyTranslator.Describes(refusal, out string? constraintName);

        recognised.Should().BeFalse(
            "{0} is not a duplicated value: the caller cannot fix it by submitting different values, so "
            + "claiming it as a conflict would be a wrong answer rather than merely a wrong status",
            description);

        constraintName.Should().BeNull("nothing was recognised, so nothing about a constraint is reported");
    }

    /// <summary>
    /// The whole inner chain is walked, so the same refusal is recognised however deeply the mapper happens
    /// to have wrapped it.
    /// </summary>
    /// <remarks>
    /// The depth is not constant in practice. The mapper wraps a provider fault in a
    /// <see cref="DbUpdateException"/>, and an execution strategy, a retrying strategy or an ambient
    /// transaction scope adds further wrappers on some code paths and not others. A translator that inspected
    /// only the immediate inner exception would therefore classify one and the same race correctly on one
    /// path and report it as a server fault on the next - a defect that reproduces intermittently and looks
    /// like flakiness rather than like a missing case.
    /// </remarks>
    [Fact]
    public void Describes_FindsTheRefusalHoweverDeeplyItIsWrapped()
    {
        SqlException refusal = FabricateSqlException((
            UniqueIndexViolation,
            "Cannot insert duplicate key row in object 'dbo.RoleGroups' with unique index 'IX_RoleGroupName'."));

        Exception wrapped = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException(
                "An exception has been raised that is likely due to a transient failure.",
                new AggregateException("one or more errors occurred", refusal)));

        // Four levels down, and behind an aggregate whose own InnerException is the interesting one.
        bool recognised = DuplicateKeyTranslator.Describes(wrapped, out string? constraintName);

        recognised.Should().BeTrue(
            "the depth a provider fault arrives at varies with the execution strategy in play, so a "
            + "translation that only looked one level down would answer the same race two different ways");

        constraintName.Should().Be("IX_RoleGroupName");
    }

    /// <summary>
    /// Every error the batch reported is examined, not only the one the exception presents as its own.
    /// </summary>
    /// <remarks>
    /// A flush stages every pending change and can report more than one error, and the duplicate need not be
    /// the first. The exception's own <see cref="SqlException.Number"/> surfaces only the leading error, so a
    /// translator reading that alone would miss a duplicate reported behind an earlier, unrelated one.
    /// </remarks>
    [Fact]
    public void Describes_ExaminesEveryErrorTheBatchReportedAndNotOnlyTheLeadingOne()
    {
        SqlException refusal = FabricateSqlException(
            (8134, "Divide by zero error encountered."),
            (UniqueConstraintViolation, "Violation of UNIQUE KEY constraint 'IX_Users'."));

        refusal.Number.Should().Be(
            8134,
            "this fabrication is only meaningful while the leading error is the one the exception surfaces");

        bool recognised = DuplicateKeyTranslator.Describes(refusal, out string? constraintName);

        recognised.Should().BeTrue(
            "the duplicate was reported by the batch even though it was not the batch's first complaint");

        constraintName.Should().Be("IX_Users");
    }

    /// <summary>
    /// A failure with no store error anywhere in it is not claimed.
    /// </summary>
    /// <remarks>
    /// The flush can fail for reasons that never reached the store at all - a broken value conversion, a
    /// disposed context, a cancelled operation. None is a duplicate and none may be answered as one.
    /// </remarks>
    [Fact]
    public void Describes_DeclinesAFailureThatNeverReachedTheStore()
    {
        Exception failure = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("The instance of entity type 'Role' cannot be tracked."));

        bool recognised = DuplicateKeyTranslator.Describes(failure, out string? constraintName);

        recognised.Should().BeFalse("no store error is present, so there is nothing to recognise");
        constraintName.Should().BeNull();
    }

    /// <summary>
    /// The error number decides the answer and the constraint name is only ever extra, so an unreadable
    /// message costs the caller nothing.
    /// </summary>
    /// <param name="message">A message the name cannot be read out of.</param>
    /// <remarks>
    /// The name is best effort by construction: it is read out of provider text, which is localised, version
    /// dependent and authored by nobody in this solution. A translation that required a legible name would
    /// turn a message change - or a server running in another language - into a 500 for a collision the
    /// number had already identified beyond doubt.
    /// </remarks>
    [Theory]
    [InlineData("Cannot insert duplicate key row.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Describes_RecognisesTheRefusalEvenWhenNoConstraintNameIsLegible(string message)
    {
        SqlException refusal = FabricateSqlException((UniqueIndexViolation, message));

        bool recognised = DuplicateKeyTranslator.Describes(refusal, out string? constraintName);

        recognised.Should().BeTrue(
            "the number is the decision; a message this translator cannot read is not a reason to report a "
            + "server fault for a collision the store already identified");

        constraintName.Should().BeNull("nothing legible was disclosed, and the answer does not depend on it");
    }

    /// <summary>
    /// A null failure is a programming error in the caller rather than something to classify.
    /// </summary>
    [Fact]
    public void Describes_RefusesANullFailure()
    {
        Action reading = () => DuplicateKeyTranslator.Describes(null!, out _);

        reading.Should().Throw<ArgumentNullException>(
            "a translator that silently answered false for a missing failure would hide the caller's defect");
    }

    /// <summary>
    /// Builds a <see cref="SqlException"/> carrying the given errors, in order.
    /// </summary>
    /// <param name="errors">The error numbers and messages the store reported, leading error first.</param>
    /// <returns>An exception shaped as the pinned client shapes one.</returns>
    /// <remarks>
    /// Every construction path on the provider's error types is non-public, so this reaches them by
    /// reflection over the pinned <c>Microsoft.Data.SqlClient</c>. Each lookup asserts what it was looking
    /// for, so a client whose shape has changed reports that plainly instead of failing somewhere further
    /// down as a null reference.
    /// </remarks>
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
