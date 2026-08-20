using System.Globalization;
using System.Reflection;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.HealthChecks;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Verifies the shared, durable refresh-token store against a real SQL Server: that it provisions nothing,
/// that it honours the settings a deployment configures, that its capacity ceiling bounds rotation as well
/// as issuance, and that its health probe reports what is actually true of the catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS STORE HAD NO BEHAVIOURAL COVERAGE AT ALL.</strong> It was reachable only through
/// configuration, so every fact about it rested on reading it.
/// </para>
/// <para>
/// <strong>The script is the deployment's script.</strong> It is embedded from
/// <c>docker/sql/refresh-token-store.sql</c> rather than copied, so the shape these tests exercise is the
/// shape an operator creates.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class SqlServerRefreshTokenStoreTests : IAsyncLifetime
{
    /// <summary>The embedded copy of the deployment's own provisioning script.</summary>
    private const string ScriptResourceName =
        "DnnMigration.IntegrationTests.Schema.RefreshTokenStoreSchema.sql";

    private const string ClientA = "client-binding-a";
    private const string ClientB = "client-binding-b";

    private static readonly DateTime Origin = new(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ApiTestFixture _fixture;

    private string _sessionCatalogue = string.Empty;
    private string _sessionConnectionString = string.Empty;
    private string _bareCatalogue = string.Empty;
    private string _bareConnectionString = string.Empty;
    private string _administrativeConnectionString = string.Empty;

    /// <summary>Initialises a new instance of the <see cref="SqlServerRefreshTokenStoreTests"/> class.</summary>
    /// <param name="fixture">The shared fixture, read for the SQL Server this run is using.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    public SqlServerRefreshTokenStoreTests(ApiTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        _fixture = fixture;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two catalogues are created: one provisioned with the deployment script, and one deliberately left
    /// EMPTY so the refusal to provision at runtime can be observed rather than argued about. Both names
    /// carry a fresh identifier, so parallel clones sharing one server cannot collide.
    /// </remarks>
    public async Task InitializeAsync()
    {
        SqlConnectionStringBuilder application = new(_fixture.Database.ConnectionString);
        string suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

        _sessionCatalogue = FormattableString.Invariant($"DnnSessions_{suffix}");
        _bareCatalogue = FormattableString.Invariant($"DnnSessionsBare_{suffix}");

        _administrativeConnectionString =
            new SqlConnectionStringBuilder(application.ConnectionString) { InitialCatalog = "master" }
                .ConnectionString;

        _sessionConnectionString =
            new SqlConnectionStringBuilder(application.ConnectionString) { InitialCatalog = _sessionCatalogue }
                .ConnectionString;

        _bareConnectionString =
            new SqlConnectionStringBuilder(application.ConnectionString) { InitialCatalog = _bareCatalogue }
                .ConnectionString;

        await CreateCatalogueAsync(_sessionCatalogue).ConfigureAwait(false);
        await CreateCatalogueAsync(_bareCatalogue).ConfigureAwait(false);

        await ApplyScriptAsync(_sessionConnectionString).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await DropCatalogueAsync(_sessionCatalogue).ConfigureAwait(false);
        await DropCatalogueAsync(_bareCatalogue).ConfigureAwait(false);
    }

    /// <summary>
    /// A catalogue with no session table makes every operation report the store unavailable, and the store
    /// creates nothing to fix it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AnUnprovisionedCatalogue_IsReportedUnavailableAndIsNotProvisioned()
    {
        SqlServerRefreshTokenStore store = Store(new MutableClock(Origin), _bareConnectionString);

        RefreshTokenIssueResult issued = await store.IssueAsync(new RefreshTokenSubject(7, -1));

        issued.Outcome.Should().Be(
            RefreshTokenOutcome.StoreUnavailable,
            "a catalogue that cannot hold a session must not report one as issued");

        (await store.RevokeAllForUserAsync(7)).Should().Be(
            RefreshTokenOutcome.StoreUnavailable,
            "a revocation against an unusable catalogue must not be reported as a revocation performed");

        (await TableExistsAsync(_bareConnectionString)).Should().BeFalse(
            "the running application must never create its own storage: the table is provisioned by "
                + "docker/sql/refresh-token-store.sql as a deployment step");
    }

    /// <summary>
    /// The table the deployment script creates supports the whole session lifecycle, and only digests reach
    /// it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The shape and the code are asserted together on purpose. A script kept separately from the store
    /// would drift from it silently - a missing column or a differently named index is a runtime failure at
    /// the first sign-in, and nothing in a build would catch it.
    /// </remarks>
    [Fact]
    public async Task TheProvisionedTableSupportsTheWholeLifecycleAndHoldsOnlyDigests()
    {
        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(clock, _sessionConnectionString);

        RefreshTokenIssueResult issued = await store.IssueAsync(new RefreshTokenSubject(11, -1));
        issued.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        string first = issued.RefreshToken!;

        RefreshTokenInspection inspected = await store.InspectAsync(first, ClientA);
        inspected.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        inspected.Subject!.UserId.Should().Be(11);
        inspected.Subject!.PortalId.Should().Be(-1);

        RefreshTokenRotationResult rotated = await store.RotateAsync(first, ClientA);
        rotated.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        rotated.RefreshToken.Should().NotBe(first);

        // The spent generation is retained and recognised, and a presentation from a DIFFERENT client is a
        // replay rather than a retry - which retires the whole family, successor included.
        (await store.RotateAsync(first, ClientB)).Outcome.Should().Be(RefreshTokenOutcome.AlreadyUsed);
        (await store.InspectAsync(rotated.RefreshToken!, ClientA)).Outcome.Should()
            .Be(RefreshTokenOutcome.Revoked, "a replay retires the family the replayed token belonged to");

        // NO RAW TOKEN IS IN THE TABLE. Asserted by searching for the token's own bytes: the column is
        // varbinary, so a store that wrote the value rather than its digest would be found by this.
        (await ContainsRawTokenAsync(first)).Should().BeFalse(
            "the table holds digests only, so a reader of it cannot mint a session");
    }

    /// <summary>
    /// Rotating a single family repeatedly stays within the configured ceiling instead of growing the table
    /// without bound.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The ceiling here is deliberately tiny, which is the only way a capacity fact is reachable in a test
    /// at all: the shipped default is a hundred thousand generations.
    /// </remarks>
    [Fact]
    public async Task RotatingRepeatedly_StaysWithinTheConfiguredCeiling()
    {
        const int Ceiling = 6;

        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(clock, _sessionConnectionString, maximumTrackedTokens: Ceiling);

        RefreshTokenIssueResult issued = await store.IssueAsync(new RefreshTokenSubject(21, -1));
        issued.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        string current = issued.RefreshToken!;

        for (int rotation = 0; rotation < 20; rotation++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));

            RefreshTokenRotationResult rotated = await store.RotateAsync(current, ClientA);

            rotated.Outcome.Should().Be(
                RefreshTokenOutcome.Succeeded,
                "bounding the table must not refuse a caller who already holds a live session");

            current = rotated.RefreshToken!;

            (await CountRowsAsync()).Should().BeLessThanOrEqualTo(
                Ceiling,
                "every rotation reclaims before it writes, so the table cannot grow past the configured "
                    + "ceiling however many times one family rotates");
        }

        (await store.InspectAsync(current, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "the generation the caller holds at the end must still be redeemable");
    }

    /// <summary>
    /// Reaching the ceiling retires the oldest families rather than refusing to issue, and it retires them
    /// whole.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// EVICTED WHOLE, which is a security property rather than a tidiness one: a half-tracked family cannot
    /// detect a replay, because the evicted generations read as unknown tokens - refused, but revoking
    /// nothing - so a thief's live generation would survive the presentation that should have killed it.
    /// Asserted by checking that BOTH generations of the evicted family are gone.
    /// </remarks>
    [Fact]
    public async Task ReachingTheCeiling_RetiresTheOldestFamilyWholeRatherThanRefusingToIssue()
    {
        const int Ceiling = 4;

        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(clock, _sessionConnectionString, maximumTrackedTokens: Ceiling);

        // The first family gets the nearest absolute ceiling, because it is issued first, so it is the family
        // eviction must choose. Rotated once so it holds two generations and "whole" is observable.
        RefreshTokenIssueResult oldest = await store.IssueAsync(new RefreshTokenSubject(31, -1));
        oldest.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromMinutes(1));

        RefreshTokenRotationResult oldestRotated = await store.RotateAsync(oldest.RefreshToken!, ClientA);
        oldestRotated.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromMinutes(1));

        RefreshTokenIssueResult second = await store.IssueAsync(new RefreshTokenSubject(32, -1));
        second.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromMinutes(1));

        RefreshTokenIssueResult third = await store.IssueAsync(new RefreshTokenSubject(33, -1));
        third.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        (await CountRowsAsync()).Should().Be(4, "two generations of the first family and one each of the others");

        clock.Advance(TimeSpan.FromMinutes(1));

        RefreshTokenIssueResult atCapacity = await store.IssueAsync(new RefreshTokenSubject(34, -1));

        atCapacity.Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "a full table must retire its oldest families rather than refuse every new sign-in");

        (await CountRowsAsync()).Should().BeLessThanOrEqualTo(Ceiling);

        (await store.InspectAsync(oldest.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "the evicted family's spent generation is gone");
        (await store.InspectAsync(oldestRotated.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "and so is its live one - a family is evicted whole or not at all, because a half-tracked family "
                + "cannot detect the replay it exists to detect");

        (await store.InspectAsync(second.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "no family beyond the excess is disturbed");
        (await store.InspectAsync(atCapacity.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "and the generation the eviction made room for is redeemable");
    }

    /// <summary>
    /// The configured concurrent-use grace governs this store, rather than a value compiled into it.
    /// </summary>
    /// <param name="graceSeconds">The grace to configure.</param>
    /// <param name="expected">The outcome an immediate same-client re-presentation must earn.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(0, RefreshTokenOutcome.AlreadyUsed)]
    [InlineData(30, RefreshTokenOutcome.ConcurrentUse)]
    public async Task TheConfiguredGrace_GovernsASameClientRePresentation(
        int graceSeconds,
        RefreshTokenOutcome expected)
    {
        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(
            clock,
            _sessionConnectionString,
            concurrentUseGraceSeconds: graceSeconds);

        RefreshTokenIssueResult issued = await store.IssueAsync(new RefreshTokenSubject(41, -1));
        issued.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        (await store.RotateAsync(issued.RefreshToken!, ClientA)).Outcome.Should()
            .Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromSeconds(10));

        (await store.RotateAsync(issued.RefreshToken!, ClientA)).Outcome.Should().Be(
            expected,
            "the configured grace decides this, and a compiled default would give the same answer to both "
                + "halves of this theory");
    }

    /// <summary>
    /// The health probe reports the shared store as this solution's, replica-safe and restart-surviving,
    /// and reports its capacity from the catalogue.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TheHealthProbe_ReportsTheSharedStoreAccurately()
    {
        SqlServerRefreshTokenStore store = Store(new MutableClock(Origin), _sessionConnectionString);

        RefreshTokenIssueResult issued = await store.IssueAsync(new RefreshTokenSubject(51, -1));
        issued.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        HealthCheckResult result = await Probe(store, _sessionConnectionString);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["provider"].Should().Be(RefreshTokenStoreOptions.SqlServerProvider);
        result.Data["storeIsThisSolutions"].Should().Be(
            true,
            "the store IS this solution's, and reporting otherwise told an operator that nothing could be "
                + "said about a store this repository wrote");
        result.Data["replicaSafe"].Should().Be(true);
        result.Data["survivesRestart"].Should().Be(true);
        result.Data["catalogueReachable"].Should().Be(true);
        result.Data["tablePresent"].Should().Be(true);
        result.Data["trackedGenerations"].Should().Be(1L);
        result.Description.Should().Contain("shared SQL Server catalogue");
    }

    /// <summary>
    /// The health probe distinguishes a reachable catalogue with no session table, and names the deployment
    /// step that fixes it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the state a first deployment of the durable store now actually reaches, because the table is
    /// provisioned out of band rather than created by the running API - so a probe that could not report it
    /// would leave an operator with a healthy instance and a sign-in that fails for no visible reason.
    /// </remarks>
    [Fact]
    public async Task TheHealthProbe_ReportsAnUnprovisionedCatalogueAndNamesTheRemedy()
    {
        SqlServerRefreshTokenStore store = Store(new MutableClock(Origin), _bareConnectionString);

        HealthCheckResult result = await Probe(store, _bareConnectionString);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["catalogueReachable"].Should().Be(true);
        result.Data["tablePresent"].Should().Be(false);
        result.Description.Should().Contain(
            "docker/sql/refresh-token-store.sql",
            "a probe that reports a missing table without naming the script that creates it leaves the "
                + "operator to guess");
    }

    // PRIV-02 — ERASURE AND OPERATION-INDEPENDENT RECLAMATION IN THE DURABLE STORE

    /// <summary>Erasing a subject deletes its rows, scoped exactly as it was asked.</summary>
    /// <remarks>
    /// The three scopes are asserted in one fact because what distinguishes them is which rows SURVIVE, and
    /// that can only be observed against a table holding rows for more than one subject at once. The tenant
    /// half of the account scope is the load-bearing part: an account removed from one tenant may still be
    /// a member of another, and erasing across every tenant would destroy sessions it legitimately holds.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ErasingASubject_DeletesExactlyItsOwnRowsAndNoOthers()
    {
        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(clock, _sessionConnectionString);

        RefreshTokenIssueResult accountHere = await store.IssueAsync(new RefreshTokenSubject(41, 7));
        RefreshTokenIssueResult accountElsewhere = await store.IssueAsync(new RefreshTokenSubject(41, 9));
        RefreshTokenIssueResult otherAccountHere = await store.IssueAsync(new RefreshTokenSubject(42, 7));

        (await CountRowsAsync()).Should().Be(3, "three subjects, three rows");

        RefreshTokenPurgeResult scoped = await store
            .PurgeSubjectAsync(RefreshTokenPurgeScope.ForAccountInPortal(41, 7));

        scoped.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        scoped.RemovedRecords.Should().Be(1);

        (await store.InspectAsync(accountHere.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "the row is deleted rather than stamped, so nothing recognises the token");
        (await store.InspectAsync(accountElsewhere.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "the account still belongs to that tenant");

        RefreshTokenPurgeResult tenant = await store.PurgeSubjectAsync(RefreshTokenPurgeScope.ForPortal(7));

        tenant.RemovedRecords.Should().Be(1, "the other account's row in the removed tenant");
        (await store.InspectAsync(otherAccountHere.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown);

        RefreshTokenPurgeResult account = await store.PurgeSubjectAsync(RefreshTokenPurgeScope.ForAccount(41));

        account.RemovedRecords.Should().Be(1, "the account's remaining row, in every tenant this time");
        (await CountRowsAsync()).Should().Be(0);
    }

    /// <summary>Erasing a subject the catalogue never held is a completed erasure.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ErasingASubjectWithNoRows_IsACompletedErasure()
    {
        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(clock, _sessionConnectionString);

        RefreshTokenPurgeResult purged = await store.PurgeSubjectAsync(RefreshTokenPurgeScope.ForAccount(4_242));

        purged.Answered.Should().BeTrue();
        purged.Outcome.Should().Be(RefreshTokenOutcome.Unknown);
        purged.RemovedRecords.Should().Be(0);
    }

    /// <summary>
    /// Reclamation removes a revoked row once its retention window elapses, leaves it inside the window,
    /// and never touches a redeemable one.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Reclamation_HonoursTheRetentionWindowAndSparesLiveSessions()
    {
        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(clock, _sessionConnectionString, revokedRetentionHours: 6);

        RefreshTokenIssueResult revoked = await store.IssueAsync(new RefreshTokenSubject(41, 7));
        RefreshTokenIssueResult live = await store.IssueAsync(new RefreshTokenSubject(42, 7));

        (await store.RevokeAsync(revoked.RefreshToken!)).Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));

        (await store.PurgeRetiredAsync()).RemovedRecords.Should().Be(
            0,
            "inside the window a replay of the revoked family is still recognisable");

        clock.Advance(TimeSpan.FromSeconds(1));

        RefreshTokenPurgeResult swept = await store.PurgeRetiredAsync();

        swept.RemovedRecords.Should().Be(1);
        (await CountRowsAsync()).Should().Be(1, "and only the revoked row went");
        (await store.InspectAsync(live.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "a redeemable row is not retired state, whatever the revoked-record window says");
    }

    /// <summary>
    /// Reclamation runs without any token being issued or rotated, which is the whole point of it existing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Reclamation_NeedsNoSignInTrafficToRunAtAll()
    {
        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(clock, _sessionConnectionString, revokedRetentionHours: 720);

        await store.IssueAsync(new RefreshTokenSubject(41, 7));

        (await CountRowsAsync()).Should().Be(1);

        clock.Advance(TimeSpan.FromDays(31));

        (await store.PurgeRetiredAsync()).RemovedRecords.Should().Be(1);
        (await CountRowsAsync()).Should().Be(
            0,
            "no issue, no rotation, and the expired row is gone regardless");
    }

    /// <summary>An unprovisioned catalogue reports the store unavailable rather than raising.</summary>
    /// <remarks>
    /// PRIV-02. Both new members are reached from paths that must not raise: erasure from a deletion whose
    /// database work is already committed, and reclamation from a background timer whose unhandled
    /// exception would stop the host.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task NeitherErasureNorReclamation_RaisesAgainstAnUnprovisionedCatalogue()
    {
        MutableClock clock = new(Origin);
        SqlServerRefreshTokenStore store = Store(clock, _bareConnectionString);

        RefreshTokenPurgeResult erased = await store.PurgeSubjectAsync(RefreshTokenPurgeScope.ForAccount(41));
        RefreshTokenPurgeResult reclaimed = await store.PurgeRetiredAsync();

        erased.Answered.Should().BeFalse("an unreachable store is reported, not raised");
        erased.Outcome.Should().Be(RefreshTokenOutcome.StoreUnavailable);
        reclaimed.Answered.Should().BeFalse();
        reclaimed.Outcome.Should().Be(RefreshTokenOutcome.StoreUnavailable);

        (await TableExistsAsync(_bareConnectionString)).Should().BeFalse(
            "and neither member provisions anything, which AAP rule T4 forbids");
    }

    /// <summary>Builds a store over the supplied catalogue with an explicit shape.</summary>
    /// <param name="clock">The clock the test advances.</param>
    /// <param name="connectionString">The session catalogue to address.</param>
    /// <param name="maximumTrackedTokens">The tracked-generation ceiling.</param>
    /// <param name="concurrentUseGraceSeconds">The same-client grace, in seconds.</param>
    /// <param name="revokedRetentionHours">How long a revoked row is retained, in hours.</param>
    /// <returns>The store.</returns>
    private static SqlServerRefreshTokenStore Store(
        IClock clock,
        string connectionString,
        int maximumTrackedTokens = 100_000,
        int concurrentUseGraceSeconds = 5,
        int revokedRetentionHours = 24) => new(
        clock,
        Options.Create(new JwtOptions
        {
            Secret = "integration-test-signing-secret-with-enough-entropy-0123456789",
            Issuer = "DnnMigration",
            Audience = "DnnMigration",
            ExpirationMinutes = 30,
            RefreshTokenExpirationDays = 7,
            RefreshTokenAbsoluteExpirationDays = 30,
        }),
        Options.Create(Settings(
            connectionString,
            maximumTrackedTokens,
            concurrentUseGraceSeconds,
            revokedRetentionHours)),
        NullLogger<SqlServerRefreshTokenStore>.Instance);

    /// <summary>Builds the store settings for a catalogue.</summary>
    /// <param name="connectionString">The session catalogue to address.</param>
    /// <param name="maximumTrackedTokens">The tracked-generation ceiling.</param>
    /// <param name="concurrentUseGraceSeconds">The same-client grace, in seconds.</param>
    /// <param name="revokedRetentionHours">How long a revoked row is retained, in hours.</param>
    /// <returns>The settings.</returns>
    private static RefreshTokenStoreOptions Settings(
        string connectionString,
        int maximumTrackedTokens = 100_000,
        int concurrentUseGraceSeconds = 5,
        int revokedRetentionHours = 24) => new()
        {
            Provider = RefreshTokenStoreOptions.SqlServerProvider,
            ConnectionString = connectionString,
            MaximumTrackedTokens = maximumTrackedTokens,
            ConcurrentUseGraceSeconds = concurrentUseGraceSeconds,
            RevokedRecordRetentionHours = revokedRetentionHours,
        };

    /// <summary>Runs the health probe over a store.</summary>
    /// <param name="store">The active store.</param>
    /// <param name="connectionString">The catalogue the settings name.</param>
    /// <returns>The probe's report.</returns>
    private static Task<HealthCheckResult> Probe(
        SqlServerRefreshTokenStore store,
        string connectionString)
    {
        RefreshTokenStoreHealth health = new(store, Options.Create(Settings(connectionString)));

        return health.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration(
                    "refresh-token-store",
                    health,
                    HealthStatus.Degraded,
                    tags: null),
            },
            CancellationToken.None);
    }

    /// <summary>Creates one catalogue on the server this run is using.</summary>
    /// <param name="name">The catalogue name.</param>
    /// <returns>A task representing the work.</returns>
    private async Task CreateCatalogueAsync(string name)
    {
        await using SqlConnection connection = new(_administrativeConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant(
            $"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}];");

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Drops one catalogue, tolerating a catalogue that was never created.</summary>
    /// <param name="name">The catalogue name.</param>
    /// <returns>A task representing the work.</returns>
    private async Task DropCatalogueAsync(string name)
    {
        if (name.Length == 0 || _administrativeConnectionString.Length == 0)
        {
            return;
        }

        SqlConnection.ClearAllPools();

        await using SqlConnection connection = new(_administrativeConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = FormattableString.Invariant($@"
IF DB_ID(N'{name}') IS NOT NULL
BEGIN
    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{name}];
END");

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Applies the deployment's provisioning script to a catalogue, batch by batch.</summary>
    /// <param name="connectionString">The catalogue to provision.</param>
    /// <returns>A task representing the work.</returns>
    private static async Task ApplyScriptAsync(string connectionString)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        foreach (string batch in ReadScript().Split(
            "\nGO",
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (batch.Length == 0)
            {
                continue;
            }

            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = batch;

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads the embedded copy of the deployment's provisioning script.</summary>
    /// <returns>The script text.</returns>
    /// <exception cref="InvalidOperationException">The resource is absent from the assembly.</exception>
    private static string ReadScript()
    {
        using Stream? stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream(ScriptResourceName);

        if (stream is null)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"The embedded resource {ScriptResourceName} is missing.")
                + " It is linked from docker/sql/refresh-token-store.sql so that these tests exercise the"
                + " exact script a deployment runs; without it they would prove nothing about that script.");
        }

        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }

    /// <summary>Reports whether the session table exists in a catalogue.</summary>
    /// <param name="connectionString">The catalogue to inspect.</param>
    /// <returns><see langword="true"/> when the table is present.</returns>
    private static async Task<bool> TableExistsAsync(string connectionString)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT OBJECT_ID(N'[dbo].[DnnMigrationRefreshTokens]', N'U');";

        object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return result is not null and not DBNull;
    }

    /// <summary>Counts the rows in the provisioned session table.</summary>
    /// <returns>The row count.</returns>
    private async Task<int> CountRowsAsync()
    {
        await using SqlConnection connection = new(_sessionConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM [dbo].[DnnMigrationRefreshTokens];";

        return Convert.ToInt32(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    /// <summary>Reports whether a raw token's own bytes appear in the session table.</summary>
    /// <param name="rawToken">The token handed to a caller.</param>
    /// <returns><see langword="true"/> when the value itself was stored.</returns>
    /// <remarks>
    /// Compares against the token's UTF-8 bytes, which is what a store writing the value rather than its
    /// digest would have persisted. Both binary columns are searched, because either would be a disclosure.
    /// </remarks>
    private async Task<bool> ContainsRawTokenAsync(string rawToken)
    {
        await using SqlConnection connection = new(_sessionConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(1)
  FROM [dbo].[DnnMigrationRefreshTokens]
 WHERE [TokenDigest] = @raw OR [ConsumedClientDigest] = @raw;";
        command.Parameters.Add(new SqlParameter("@raw", System.Data.SqlDbType.VarBinary, -1)
        {
            Value = System.Text.Encoding.UTF8.GetBytes(rawToken),
        });

        return Convert.ToInt32(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>A clock the test advances explicitly, so no fact depends on wall-clock time passing.</summary>
    private sealed class MutableClock : IClock
    {
        private DateTime _utcNow;

        /// <summary>Initialises a new instance of the <see cref="MutableClock"/> class.</summary>
        /// <param name="utcNow">The instant the clock starts at.</param>
        public MutableClock(DateTime utcNow) => _utcNow = utcNow;

        /// <inheritdoc />
        public DateTime UtcNow => _utcNow;

        /// <summary>Moves the clock forward.</summary>
        /// <param name="by">How far forward to move it.</param>
        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }
}
