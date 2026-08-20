using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Persistence;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using FluentAssertions.Specialized;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Services;

/// <summary>Drives the installation-settings reader against real rows in SQL Server.</summary>
/// <remarks>
/// <para>
/// <strong>Why the existing coverage was not coverage.</strong> Every consumer of <see
/// cref="IHostSettingsService"/> is tested against a mock of it, and the one path the integration host
/// exercised for real was the EMPTY table - portal creation reads seven settings, finds no rows, and
/// applies its defaults.
/// </para>
/// <para>
/// <strong>This suite writes to <c>dbo.HostSettings</c>.</strong> The table is not one of the twenty-one
/// mapped entities and no other suite reads or asserts on it, so the rows here cannot perturb another test.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class HostSettingsServiceTests
{
    /// <summary>The longest name the <c>SettingName</c> column accepts.</summary>
    /// <remarks>
    /// Stated here as the schema fact it is - <c>01.00.05.SqlDataProvider</c> creates the column as
    /// <c>nvarchar(50)</c> - so the boundary cases below are anchored to the column rather than to the
    /// service's own constant.
    /// </remarks>
    private const int SettingNameMaxLength = 50;

    /// <summary>The name of the row the whole-table read creates when it is absent.</summary>
    private const string InstallationIdentifierSettingName = "GUID";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="HostSettingsServiceTests"/> class.</summary>
    /// <param name="fixture">The shared host and provisioned database.</param>
    public HostSettingsServiceTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The composed application resolves this implementation, per scope.</summary>
    [Fact]
    public void TheComposedApplication_ResolvesAScopedHostSettingsService()
    {
        using IServiceScope first = _fixture.Services.CreateScope();
        using IServiceScope second = _fixture.Services.CreateScope();

        IHostSettingsService fromFirst = first.ServiceProvider.GetRequiredService<IHostSettingsService>();
        IHostSettingsService againFromFirst = first.ServiceProvider.GetRequiredService<IHostSettingsService>();
        IHostSettingsService fromSecond = second.ServiceProvider.GetRequiredService<IHostSettingsService>();

        fromFirst.Should().BeOfType<HostSettingsService>();
        againFromFirst.Should().BeSameAs(fromFirst, "one instance serves one scope");
        fromSecond.Should().NotBeSameAs(
            fromFirst,
            "the service borrows the scoped context's connection, so sharing one instance across scopes would "
            + "make it use a context that belongs to another request");
    }

    /// <summary>A keyed read returns the value stored under exactly that name.</summary>
    /// <remarks>
    /// Two rows are seeded, one of which is a prefix of nothing and neither of which is the first row in
    /// the table, so a read that ignored its parameter or matched loosely would return the wrong value
    /// rather than no value - which is the failure a single-row fixture cannot detect.
    /// </remarks>
    [Fact]
    public async Task GetSettingAsync_ReturnsTheValueStoredUnderThatName()
    {
        string wanted = UniqueName("Wanted");
        string other = UniqueName("Other");

        await SeedAsync((wanted, "42"), (other, "99"));

        try
        {
            (await ReadSettingAsync(wanted)).Should().Be("42");
            (await ReadSettingAsync(other)).Should().Be(
                "99",
                "each name must resolve to its own row, so a read that matched loosely is caught");
        }
        finally
        {
            await RemoveAsync(wanted, other);
        }
    }

    /// <summary>A name no row carries is reported as absent.</summary>
    /// <remarks>
    /// Null, not an empty string. This is one half of the distinction the legacy reader could not express,
    /// and the half that matters to a caller deciding whether to apply a default: an empty string is a
    /// configured value and must not trigger one.
    /// </remarks>
    [Fact]
    public async Task GetSettingAsync_ReportsAnAbsentRowAsNull()
    {
        (await ReadSettingAsync(UniqueName("NeverStored"))).Should().BeNull(
            "the legacy reader answered a missing key with the empty-string sentinel, which made an absent "
            + "row indistinguishable from a configured empty value");
    }

    /// <summary>A row holding an empty value is reported as an empty string.</summary>
    [Fact]
    public async Task GetSettingAsync_ReportsAStoredEmptyValueAsEmptyRatherThanAbsent()
    {
        string name = UniqueName("Blanked");

        await SeedAsync((name, string.Empty));

        try
        {
            string? value = await ReadSettingAsync(name);

            value.Should().NotBeNull("a row exists, so this is not absence");
            value.Should().BeEmpty(
                "an operator who blanked a setting must not have their default silently reinstated");
        }
        finally
        {
            await RemoveAsync(name);
        }
    }

    /// <summary>A name exactly as long as the column allows is read rather than refused.</summary>
    [Fact]
    public async Task GetSettingAsync_AcceptsANameOfExactlyTheColumnWidth()
    {
        string name = new('n', SettingNameMaxLength);

        await SeedAsync((name, "boundary"));

        try
        {
            (await ReadSettingAsync(name)).Should().Be("boundary");
        }
        finally
        {
            await RemoveAsync(name);
        }
    }

    /// <summary>A null name is refused as an argument fault.</summary>
    [Fact]
    public async Task GetSettingAsync_RefusesANullName()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IHostSettingsService settings = scope.ServiceProvider.GetRequiredService<IHostSettingsService>();

        Func<Task> read = () => settings.GetSettingAsync(null!);

        ExceptionAssertions<ArgumentNullException> refusal =
            await read.Should().ThrowAsync<ArgumentNullException>();

        refusal.WithParameterName("settingName");
    }

    /// <summary>A blank or over-long name is refused rather than sent to the provider.</summary>
    /// <remarks>
    /// A blank name is refused because the column is the primary key and a blank key identifies nothing the
    /// caller could have meant. An over-long one is refused because no row can carry it, so accepting it
    /// would either truncate the caller's intent or surface as a provider error whose message names a
    /// parameter rather than the mistake.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   ")]
    public async Task GetSettingAsync_RefusesABlankName(string settingName)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IHostSettingsService settings = scope.ServiceProvider.GetRequiredService<IHostSettingsService>();

        Func<Task> read = () => settings.GetSettingAsync(settingName);

        ExceptionAssertions<ArgumentException> refusal =
            await read.Should().ThrowAsync<ArgumentException>();

        refusal.WithParameterName("settingName");
    }

    /// <summary>An over-long name is refused, and the refusal says how long it was.</summary>
    [Fact]
    public async Task GetSettingAsync_RefusesANameLongerThanTheColumn()
    {
        string tooLong = new('n', SettingNameMaxLength + 1);

        using IServiceScope scope = _fixture.Services.CreateScope();
        IHostSettingsService settings = scope.ServiceProvider.GetRequiredService<IHostSettingsService>();

        Func<Task> read = () => settings.GetSettingAsync(tooLong);

        ExceptionAssertions<ArgumentException> refusal =
            await read.Should().ThrowAsync<ArgumentException>();

        refusal.WithParameterName("settingName");
        refusal.Which.Message.Should().Contain(
            (SettingNameMaxLength + 1).ToString(CultureInfo.InvariantCulture),
            "the refusal reports the supplied length, so the caller can see by how much it overran");
    }

    /// <summary>The whole-table read projects every stored row.</summary>
    /// <remarks>
    /// Asserted by containment rather than by an exact set, because the table is installation-wide and
    /// holds the installation identifier as well as anything a concurrently running suite legitimately
    /// placed there. What matters is that every seeded row appears with its own value.
    /// </remarks>
    [Fact]
    public async Task GetSettingsAsync_ProjectsEveryStoredRow()
    {
        string first = UniqueName("First");
        string second = UniqueName("Second");
        string blank = UniqueName("Blank");

        await SeedAsync((first, "one"), (second, "two"), (blank, string.Empty));

        try
        {
            Dictionary<string, string> projected = Materialise(await ReadAllSettingsAsync());

            projected.Should().Contain(first, "one");
            projected.Should().Contain(second, "two");
            projected.Should().Contain(
                blank,
                string.Empty,
                "a row holding an empty value is still a row, and dropping it would hide a configured setting");
        }
        finally
        {
            await RemoveAsync(first, second, blank);
        }
    }

    /// <summary>Keys compare ordinally, as the legacy untyped collection did.</summary>
    /// <remarks>
    /// The legacy accumulation used the default case-sensitive comparison, so a caller asking by the exact
    /// stored name behaves as it always did.
    /// </remarks>
    [Fact]
    public async Task GetSettingsAsync_ComparesKeysOrdinally()
    {
        string name = UniqueName("CaseSensitive");

        await SeedAsync((name, "stored"));

        try
        {
            IReadOnlyDictionary<string, string> settings = await ReadAllSettingsAsync();

            settings.ContainsKey(name).Should().BeTrue();
            settings.ContainsKey(name.ToUpperInvariant()).Should().BeFalse(
                "the legacy collection compared names case-sensitively, and this read is the same read");
        }
        finally
        {
            await RemoveAsync(name);
        }
    }

    /// <summary>The projection is genuinely read-only rather than a mutable map behind an interface.</summary>
    [Fact]
    public async Task GetSettingsAsync_ReturnsAGenuinelyReadOnlyProjection()
    {
        IReadOnlyDictionary<string, string> settings = await ReadAllSettingsAsync();

        settings.Should().BeOfType<ReadOnlyDictionary<string, string>>(
            "a mutable map widened to a read-only interface can be cast straight back to "
            + "Dictionary<string, string> and written to, which would let one caller alter the answer "
            + "another is holding");

        IDictionary<string, string> mutable = (IDictionary<string, string>)settings;

        mutable.IsReadOnly.Should().BeTrue();

        Action write = () => mutable["ZZTest.Injected"] = "value";

        write.Should().Throw<NotSupportedException>(
            "the guard has to be the projection itself, because the mutating interface is reachable by a "
            + "cast whatever the declared return type says");
    }

    /// <summary>The whole-table read creates the installation identifier when it is absent.</summary>
    /// <remarks>
    /// The row is deleted first so that the creation is genuinely observed rather than inferred from a row
    /// that was already there, and the read is then performed twice to prove the write is idempotent - a
    /// second insert would violate the name's unique constraint and fail the read outright.
    /// </remarks>
    [Fact]
    public async Task GetSettingsAsync_CreatesTheInstallationIdentifierWhenItIsAbsent()
    {
        await RemoveAsync(InstallationIdentifierSettingName);

        (await CountOfAsync(InstallationIdentifierSettingName)).Should().Be(
            0,
            "the row has to be genuinely absent, or the creation below would be inferred rather than observed");

        IReadOnlyDictionary<string, string> settings = await ReadAllSettingsAsync();

        settings.Should().ContainKey(InstallationIdentifierSettingName);
        Guid.TryParse(settings[InstallationIdentifierSettingName], out Guid identifier).Should().BeTrue(
            "the value is a fresh globally unique identifier, which is what makes it usable as the "
            + "installation's identity");
        identifier.Should().NotBe(Guid.Empty);

        (await CountOfAsync(InstallationIdentifierSettingName)).Should().Be(1);

        IReadOnlyDictionary<string, string> again = await ReadAllSettingsAsync();

        again[InstallationIdentifierSettingName].Should().Be(
            settings[InstallationIdentifierSettingName],
            "the write is conditional, so a second read must not replace an identifier the installation is "
            + "already known by");
        (await CountOfAsync(InstallationIdentifierSettingName)).Should().Be(
            1,
            "an unconditional insert would violate the unique constraint on the name and fail the read");
    }

    /// <summary>Concurrent first reads create exactly one identifier between them.</summary>
    /// <remarks>
    /// The legacy procedure tested for the row and inserted it as two separate statements, which races: two
    /// callers arriving together both find it absent and both insert, and the second violates the primary
    /// key. The target folds the test into the insert and takes an update lock over the key range, so the
    /// race cannot occur - and that is a claim only concurrency can check.
    /// </remarks>
    [Fact]
    public async Task GetSettingsAsync_CreatesOneIdentifierUnderConcurrentFirstReads()
    {
        const int callersPerRound = 32;
        const int rounds = 5;

        for (int round = 0; round < rounds; round++)
        {
            await RemoveAsync(InstallationIdentifierSettingName);

            List<IServiceScope> scopes = [];
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                List<Task<IReadOnlyDictionary<string, string>>> reads = [];

                for (int caller = 0; caller < callersPerRound; caller++)
                {
                    IServiceScope scope = _fixture.Services.CreateScope();
                    scopes.Add(scope);

                    IHostSettingsService settings =
                        scope.ServiceProvider.GetRequiredService<IHostSettingsService>();

                    // The connection is opened BEFORE the barrier, so that the only work left after it is
                    // the command itself.
                    DbConnection connection = scope.ServiceProvider
                        .GetRequiredService<DnnDbContext>()
                        .Database
                        .GetDbConnection();

                    await connection.OpenAsync();

                    reads.Add(Task.Run(async () =>
                    {
                        await start.Task;

                        return await settings.GetSettingsAsync();
                    }));
                }

                start.SetResult();

                IReadOnlyDictionary<string, string>[] answers = await Task.WhenAll(reads);

                (await CountOfAsync(InstallationIdentifierSettingName)).Should().Be(
                    1,
                    "the conditional insert takes an update lock over the key range, so a concurrent caller "
                    + "cannot slip an identical row in between the test and the insert");

                answers.Select(answer => answer[InstallationIdentifierSettingName])
                    .Distinct(StringComparer.Ordinal)
                    .Should()
                    .HaveCount(1, "every caller must see the one identifier the installation now has");
            }
            finally
            {
                foreach (IServiceScope scope in scopes)
                {
                    scope.Dispose();
                }
            }
        }
    }

    /// <summary>A cancelled caller is refused, and the borrowed connection is left as it was found.</summary>
    /// <remarks>
    /// Cancellation is never caught, wrapped or converted, so it must leave as it arrived. The second half
    /// is the part a cancellation test usually forgets: the service opens the context's connection when it
    /// finds it closed and must close it again on every exit path, cancellation included.
    /// </remarks>
    [Fact]
    public async Task BothReads_HonourCancellationAndLeaveTheConnectionClosed()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IHostSettingsService settings = scope.ServiceProvider.GetRequiredService<IHostSettingsService>();
        DbConnection connection = scope.ServiceProvider
            .GetRequiredService<DnnDbContext>()
            .Database
            .GetDbConnection();

        connection.State.Should().Be(ConnectionState.Closed, "nothing in this scope has opened it yet");

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Func<Task> keyedRead = () => settings.GetSettingAsync("GUID", cancelled.Token);
        Func<Task> wholeTableRead = () => settings.GetSettingsAsync(cancelled.Token);

        await keyedRead.Should().ThrowAsync<OperationCanceledException>();
        connection.State.Should().Be(
            ConnectionState.Closed,
            "a cancelled read must not leave the context's connection open for the rest of the request");

        await wholeTableRead.Should().ThrowAsync<OperationCanceledException>();
        connection.State.Should().Be(ConnectionState.Closed);
    }

    /// <summary>A connection the caller already opened is left open.</summary>
    [Fact]
    public async Task AReadDoesNotCloseAConnectionItDidNotOpen()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IHostSettingsService settings = scope.ServiceProvider.GetRequiredService<IHostSettingsService>();
        DbConnection connection = scope.ServiceProvider
            .GetRequiredService<DnnDbContext>()
            .Database
            .GetDbConnection();

        await connection.OpenAsync();

        try
        {
            _ = await settings.GetSettingAsync("GUID");

            connection.State.Should().Be(
                ConnectionState.Open,
                "the caller opened it, so the caller closes it: closing it here would break the rest of the "
                + "request");

            _ = await settings.GetSettingsAsync();

            connection.State.Should().Be(ConnectionState.Open);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>Both reads enlist in a transaction the unit of work has open.</summary>
    /// <remarks>
    /// The service issues its own commands on the borrowed connection, and SQL Server refuses a command
    /// that carries no transaction on a connection with a pending local one. So this is not a nicety:
    /// without enlistment, every host-settings read inside a transactional write path would fail outright -
    /// and portal creation, which reads seven of these settings, is exactly such a path.
    /// </remarks>
    [Fact]
    public async Task BothReads_EnlistInAnOpenTransaction()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IHostSettingsService settings = scope.ServiceProvider.GetRequiredService<IHostSettingsService>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await using ITransactionScope transaction = await unitOfWork.BeginTransactionAsync();

        Func<Task> keyedRead = () => settings.GetSettingAsync("GUID");
        Func<Task> wholeTableRead = () => settings.GetSettingsAsync();

        await keyedRead.Should().NotThrowAsync(
            "a command with no transaction on a connection holding a pending local one is refused by the "
            + "provider, so a read that did not enlist would fail every transactional write path that reads "
            + "a host setting");
        await wholeTableRead.Should().NotThrowAsync();
    }

    /// <summary>Reads the value of one setting through a fresh scope.</summary>
    /// <param name="settingName">The name to read.</param>
    /// <returns>The stored value, or <see langword="null"/> when no row carries the name.</returns>
    private async Task<string?> ReadSettingAsync(string settingName)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<IHostSettingsService>()
            .GetSettingAsync(settingName);
    }

    /// <summary>Reads every setting through a fresh scope.</summary>
    /// <returns>The projection the service produced.</returns>
    private async Task<IReadOnlyDictionary<string, string>> ReadAllSettingsAsync()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<IHostSettingsService>()
            .GetSettingsAsync();
    }

    /// <summary>Inserts rows directly, bypassing the service under test.</summary>
    /// <param name="rows">The name and value of each row to insert.</param>
    /// <returns>A task that completes when every row exists.</returns>
    private async Task SeedAsync(params (string Name, string Value)[] rows)
    {
        foreach ((string name, string value) in rows)
        {
            await _fixture.Database.ExecuteAsync(
                "INSERT INTO [dbo].[HostSettings] ([SettingName], [SettingValue], [SettingIsSecure]) "
                + "VALUES (@name, @value, 0);",
                new Dictionary<string, object?> { ["name"] = name, ["value"] = value });
        }
    }

    /// <summary>Removes rows directly, bypassing the service under test.</summary>
    /// <param name="names">The names to remove.</param>
    /// <returns>A task that completes when none of the names has a row.</returns>
    private async Task RemoveAsync(params string[] names)
    {
        foreach (string name in names)
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[HostSettings] WHERE [SettingName] = @name;",
                new Dictionary<string, object?> { ["name"] = name });
        }
    }

    /// <summary>Counts the rows carrying one name.</summary>
    /// <param name="settingName">The name to count.</param>
    /// <returns>How many rows carry it.</returns>
    private async Task<int> CountOfAsync(string settingName) =>
        await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM [dbo].[HostSettings] WHERE [SettingName] = @name;",
            new Dictionary<string, object?> { ["name"] = settingName });

    /// <summary>
    /// A secure-marked value cannot escape through the API layer, because nothing in that layer depends on
    /// this abstraction at all.
    /// </summary>
    /// <remarks>
    /// Constructor parameters AND fields are both examined, because injection is not the only route: a type
    /// could resolve the service from a provider and hold it. Reflection over the assembly rather than a
    /// text search, so a rename cannot silently defeat it.
    /// </remarks>
    [Fact]
    public void SecureValuesCannotEscapeThroughTheApiLayer()
    {
        Assembly api = typeof(DnnMigration.Api.Controllers.PortalsController).Assembly;

        List<string> offenders = [];

        foreach (Type type in api.GetTypes())
        {
            bool injected = type
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(IHostSettingsService));

            bool held = type
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static)
                .Any(field => field.FieldType == typeof(IHostSettingsService));

            bool exposed = type
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static)
                .Any(property => property.PropertyType == typeof(IHostSettingsService));

            if (injected || held || exposed)
            {
                offenders.Add(type.FullName ?? type.Name);
            }
        }

        offenders.Should().BeEmpty(
            "INFO-03: host settings are returned WITHOUT filtering the SettingIsSecure flag, which is safe "
            + "only while nothing publishes them. A type in the API assembly now depends on "
            + "IHostSettingsService, so a secure value has a route to a response: either withhold secure rows "
            + "at the point of publication or gate that endpoint on host administration, and update the note "
            + "beside the flag in HostSettingsService");
    }

    /// <summary>
    /// Composes a setting name this suite owns, short enough for the column and unlikely to collide.
    /// </summary>
    /// <param name="label">A readable label naming what the row is for.</param>
    /// <returns>The composed name.</returns>
    /// <remarks>
    /// Distinctive because the table is installation-wide: a name that collided with a real setting would
    /// let a test overwrite configuration the application reads, and the identifier keeps each run's rows
    /// apart from a previous run's leftovers.
    /// </remarks>
    private static string UniqueName(string label)
    {
        string composed = string.Create(CultureInfo.InvariantCulture, $"ZZTest.{label}.{Guid.NewGuid():N}");

        // Truncated rather than refused: the label is the readable part and the identifier's leading
        // characters keep the name unique, so trimming the tail costs nothing a test depends on. Refusing
        // would make adding a longer label fail here instead of where the label was written.
        return composed.Length <= SettingNameMaxLength ? composed : composed[..SettingNameMaxLength];
    }

    /// <summary>Copies a projection into an ordinary dictionary so that dictionary assertions apply to it.</summary>
    /// <param name="settings">The projection the service produced.</param>
    /// <returns>An equivalent mutable copy, used only for assertions.</returns>
    private static Dictionary<string, string> Materialise(IReadOnlyDictionary<string, string> settings) =>
        settings.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
