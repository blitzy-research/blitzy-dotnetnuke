using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Races two callers that hold the SAME concurrency token into the same record at the same moment, on every
/// whole-record write path that publishes a token.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ THESE TESTS ARE SIMULTANEOUS, AND THE DISTINCTION FROM THE SEQUENTIAL COVERAGE IS THE WHOLE POINT. Each
/// resource already had a stale-snapshot test: read a token, save once, then save AGAIN with the spent token.
/// That test only ever exercises the branch where the first commit has already landed, so the second caller's
/// own read observes the new values and the in-process token comparison refuses it. It cannot reach the
/// branch where the two callers' reads and writes genuinely interleave, and that branch was where the
/// defects lived:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     On the two paths that DID hold a serialisable transaction, the engine broke the interleaving by
///     aborting one participant, the registered retrying execution strategy re-ran the flush against the
///     transaction the engine had just discarded, and the resulting savepoint failure surfaced as
///     <c>500 server.unexpected_failure</c>. The loser of an ordinary write race was told the server was
///     broken.
///     </description>
///   </item>
///   <item>
///     <description>
///     On the account path, which held NO transaction, both callers passed the comparison and both wrote:
///     two <c>200</c> responses, and the first operator's committed edit silently replaced.
///     </description>
///   </item>
/// </list>
/// <para>
/// WHAT IS ASSERTED, AND WHY IT IS NOT "EXACTLY ONE 409". Which participant the engine picks is genuinely
/// its choice, and how many callers reach the flush together depends on scheduling, so the assertions state
/// the properties that must hold on EVERY outcome rather than one transcript: at most one caller succeeds,
/// no caller is answered a server fault, every refusal is a conflict naming the resource's own conflict
/// code, and the value the store ends up holding is the value the successful caller sent. A lost update
/// fails the last of those, and a mistranslated race fails the second.
/// </para>
/// <para>
/// Each test creates its own record. The suites share one database with no ordering guarantee, and a race
/// against a seeded record would leave its result to whichever test read it next.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class SimultaneousWriteRaceTests
{
    /// <summary>The prefix a failure code is built into as the problem document's <c>type</c>.</summary>
    private const string ProblemTypePrefix = "urn:dnnmigration:error:";

    /// <summary>
    /// How many callers are released together. Two is the minimum that can race and is what the report
    /// measured; four is included on the portal path because a wider release is what turned the outcome
    /// from intermittent into deterministic.
    /// </summary>
    private const int PairedWriters = 2;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="SimultaneousWriteRaceTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public SimultaneousWriteRaceTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Two simultaneous settings saves holding one token yield one success and one conflict, never a server
    /// fault and never a lost update.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortalSettings_FromTwoSimultaneousCallersHoldingOneToken_RefusesTheLoserWithAConflict()
        => await RacePortalSettingsAsync(PairedWriters).ConfigureAwait(true);

    /// <summary>
    /// Four simultaneous settings saves holding one token yield one success and three conflicts. The wider
    /// release is what made the savepoint failure deterministic, so it is asserted as well as the pair.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortalSettings_FromFourSimultaneousCallersHoldingOneToken_RefusesEveryLoserWithAConflict()
        => await RacePortalSettingsAsync(4).ConfigureAwait(true);

    /// <summary>
    /// Two simultaneous role amendments holding one token yield one success and one conflict, never a server
    /// fault.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_FromTwoSimultaneousCallersHoldingOneToken_RefusesTheLoserWithAConflict()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync().ConfigureAwait(true);

        var create = new CreateRoleRequest
        {
            RoleName = "Race Role " + Suffix(),
            Description = "Created to be raced.",
            IsPublic = false,
            AutoAssignment = false,
        };

        using HttpResponseMessage created = await client
            .PostAsJsonAsync(new Uri("/api/v1/roles", UriKind.Relative), create, ApiTestFixture.Json)
            .ConfigureAwait(true);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        RoleDetailDto role = (await created.Content.ReadEnvelopeAsync<RoleDetailDto>().ConfigureAwait(true))!;
        role.ConcurrencyToken.Should().NotBeNullOrWhiteSpace();

        var route = new Uri($"/api/v1/roles/{Route(role.RoleId)}", UriKind.Relative);

        IReadOnlyList<RaceOutcome> outcomes = await ReleaseTogetherAsync(
            PairedWriters,
            index => client.PutAsJsonAsync(
                route,
                new UpdateRoleRequest
                {
                    RoleName = role.RoleName,
                    Description = DescriptionFor(index),
                    IsPublic = role.IsPublic,
                    AutoAssignment = role.AutoAssignment,
                    ConcurrencyToken = role.ConcurrencyToken,
                },
                ApiTestFixture.Json)).ConfigureAwait(true);

        await AssertOneWinnerAndNoFaultAsync(outcomes, "role.concurrency_conflict").ConfigureAwait(true);

        using HttpResponseMessage reread = await client.GetAsync(route).ConfigureAwait(true);
        reread.StatusCode.Should().Be(HttpStatusCode.OK);
        RoleDetailDto stored = (await reread.Content.ReadEnvelopeAsync<RoleDetailDto>().ConfigureAwait(true))!;

        stored.Description.Should().Be(
            DescriptionFor(WinningIndex(outcomes)),
            "the store must hold the value the caller that was answered 200 sent, or an amendment was lost");
    }

    /// <summary>
    /// Two simultaneous account amendments holding one token yield one success and one conflict, and the
    /// loser's values are NOT written.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The measured failure here was not a mistranslated refusal but no refusal at all: both callers were
    /// answered <c>200</c> and the second overwrote the first. The re-read is therefore the assertion that
    /// matters most on this path.
    /// </remarks>
    [Fact]
    public async Task UpdateUser_FromTwoSimultaneousCallersHoldingOneToken_RefusesTheLoserAndLosesNoEdit()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync().ConfigureAwait(true);

        string suffix = Suffix();
        var create = new CreateUserRequest
        {
            Username = "race_" + suffix,
            FirstName = "Race",
            LastName = "Subject",
            DisplayName = "Race Subject " + suffix,
            Email = $"race_{suffix}@integration.test",
            Password = "Race!Subject2026",
            ConfirmPassword = "Race!Subject2026",
            Authorize = true,
        };

        using HttpResponseMessage created = await client
            .PostAsJsonAsync(new Uri("/api/v1/users", UriKind.Relative), create, ApiTestFixture.Json)
            .ConfigureAwait(true);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        UserDetailDto account = (await created.Content.ReadEnvelopeAsync<UserDetailDto>().ConfigureAwait(true))!;

        var route = new Uri($"/api/v1/users/{Route(account.UserId)}", UriKind.Relative);

        using HttpResponseMessage read = await client.GetAsync(route).ConfigureAwait(true);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        UserDetailDto snapshot = (await read.Content.ReadEnvelopeAsync<UserDetailDto>().ConfigureAwait(true))!;
        snapshot.ConcurrencyToken.Should().NotBeNullOrWhiteSpace();

        IReadOnlyList<RaceOutcome> outcomes = await ReleaseTogetherAsync(
            PairedWriters,
            index => client.PutAsJsonAsync(
                route,
                new UpdateUserRequest
                {
                    FirstName = DescriptionFor(index),
                    LastName = snapshot.LastName,
                    DisplayName = snapshot.DisplayName,
                    Email = snapshot.Email,
                    ConcurrencyToken = snapshot.ConcurrencyToken,
                },
                ApiTestFixture.Json)).ConfigureAwait(true);

        await AssertOneWinnerAndNoFaultAsync(outcomes, "user.concurrency_conflict").ConfigureAwait(true);

        using HttpResponseMessage reread = await client.GetAsync(route).ConfigureAwait(true);
        reread.StatusCode.Should().Be(HttpStatusCode.OK);
        UserDetailDto stored = (await reread.Content.ReadEnvelopeAsync<UserDetailDto>().ConfigureAwait(true))!;

        stored.FirstName.Should().Be(
            DescriptionFor(WinningIndex(outcomes)),
            "the store must hold the value the caller that was answered 200 sent; holding the other "
            + "caller's value is the silent lost update this test exists to exclude");
    }

    /// <summary>Races a given number of callers into one portal's settings with a single shared token.</summary>
    /// <param name="writers">How many callers are released together.</param>
    /// <returns>A task representing the race and its assertions.</returns>
    private async Task RacePortalSettingsAsync(int writers)
    {
        using HttpClient host = await _fixture.CreateHostClientAsync().ConfigureAwait(true);

        var route = new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings",
            UriKind.Relative);

        using HttpResponseMessage read = await host.GetAsync(route).ConfigureAwait(true);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto snapshot =
            (await read.Content.ReadEnvelopeAsync<PortalSettingsDto>().ConfigureAwait(true))!;
        snapshot.ConcurrencyToken.Should().NotBeNullOrWhiteSpace();

        IReadOnlyList<RaceOutcome> outcomes = await ReleaseTogetherAsync(
            writers,
            index => host.PutAsJsonAsync(
                route,
                SettingsUpdateFrom(snapshot, DescriptionFor(index)),
                ApiTestFixture.Json)).ConfigureAwait(true);

        await AssertOneWinnerAndNoFaultAsync(outcomes, "portal.concurrency_conflict").ConfigureAwait(true);

        using HttpResponseMessage reread = await host.GetAsync(route).ConfigureAwait(true);
        reread.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto stored =
            (await reread.Content.ReadEnvelopeAsync<PortalSettingsDto>().ConfigureAwait(true))!;

        stored.FooterText.Should().Be(
            DescriptionFor(WinningIndex(outcomes)),
            "the store must hold the value the caller that was answered 200 sent, or a save was lost");

        // Left as the read found it, because the whole suite shares this portal and the next test to read
        // it must not inherit a race marker in its footer.
        UpdatePortalSettingsRequest restore = SettingsUpdateFrom(stored, snapshot.FooterText);
        restore.ConcurrencyToken = stored.ConcurrencyToken;
        using HttpResponseMessage restored = await host
            .PutAsJsonAsync(route, restore, ApiTestFixture.Json)
            .ConfigureAwait(true);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Issues a given number of requests that are all held at a barrier and released together, so they reach
    /// the server inside one another's windows rather than merely close together.
    /// </summary>
    /// <param name="writers">How many callers to release.</param>
    /// <param name="send">Produces the request for one caller, given its index.</param>
    /// <returns>Each caller's index, status and response body.</returns>
    /// <remarks>
    /// A barrier rather than a bare <c>Task.WhenAll</c> over eagerly started tasks. Starting the sends in a
    /// loop lets the first one finish before the last one begins on a busy machine, which is exactly the
    /// sequential case the existing tests already cover; the barrier makes the overlap a property of the
    /// test rather than of the scheduler.
    /// </remarks>
    private static async Task<IReadOnlyList<RaceOutcome>> ReleaseTogetherAsync(
        int writers,
        Func<int, Task<HttpResponseMessage>> send)
    {
        using var gate = new Barrier(writers);

        Task<RaceOutcome>[] callers = Enumerable.Range(0, writers)
            .Select(index => Task.Run(async () =>
            {
                gate.SignalAndWait();

                using HttpResponseMessage response = await send(index).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                return new RaceOutcome(index, response.StatusCode, body);
            }))
            .ToArray();

        return await Task.WhenAll(callers).ConfigureAwait(false);
    }

    /// <summary>
    /// Asserts the properties every outcome of a same-token race must satisfy, whichever participant the
    /// engine happened to choose.
    /// </summary>
    /// <param name="outcomes">What each caller was answered.</param>
    /// <param name="conflictCode">The failure code this resource reports a lost update under.</param>
    /// <returns>A task representing the assertions.</returns>
    private static Task AssertOneWinnerAndNoFaultAsync(
        IReadOnlyList<RaceOutcome> outcomes,
        string conflictCode)
    {
        outcomes.Should().NotContain(
            outcome => (int)outcome.Status >= 500,
            "a caller that lost a write race must never be told the server failed; a 500 here is the "
            + "savepoint-on-a-discarded-transaction defect");

        outcomes.Count(outcome => outcome.Status == HttpStatusCode.OK).Should().Be(
            1,
            "exactly one of two callers holding one token may write; more than one is a lost update and "
            + "none at all means a legitimate save was refused");

        foreach (RaceOutcome loser in outcomes.Where(outcome => outcome.Status != HttpStatusCode.OK))
        {
            loser.Status.Should().Be(
                HttpStatusCode.Conflict,
                "the only admissible refusal for a same-token race is a conflict");

            ProblemDetails? problem = Deserialise(loser.Body);
            problem.Should().NotBeNull("every refusal is served as a problem document");
            problem!.Type.Should().Be(
                ProblemTypePrefix + conflictCode,
                "the refusal must name this resource's own concurrency code, so a client can tell a lost "
                + "update from any other conflict");
        }

        return Task.CompletedTask;
    }

    /// <summary>The index of the caller that was answered <c>200</c>.</summary>
    /// <param name="outcomes">What each caller was answered.</param>
    /// <returns>The winning caller's index.</returns>
    private static int WinningIndex(IReadOnlyList<RaceOutcome> outcomes) =>
        outcomes.Single(outcome => outcome.Status == HttpStatusCode.OK).Index;

    /// <summary>Reads a problem document out of a response body, or null when the body is not one.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The problem document, or null.</returns>
    private static ProblemDetails? Deserialise(string body) =>
        string.IsNullOrWhiteSpace(body)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<ProblemDetails>(body, ApiTestFixture.Json);

    /// <summary>Projects a settings snapshot onto an update carrying one changed value.</summary>
    /// <param name="snapshot">The values read from the store.</param>
    /// <param name="footerText">The value this caller writes, which identifies it in the stored record.</param>
    /// <returns>An update request that changes only the footer.</returns>
    private static UpdatePortalSettingsRequest SettingsUpdateFrom(
        PortalSettingsDto snapshot,
        string? footerText) => new()
        {
            PortalName = snapshot.PortalName,
            LogoFile = snapshot.LogoFile,
            FooterText = footerText,
            ExpiryDate = snapshot.ExpiryDate,
            UserRegistration = snapshot.UserRegistration,
            BannerAdvertising = snapshot.BannerAdvertising,
            Currency = snapshot.Currency,
            AdministratorId = snapshot.AdministratorId,
            HostFee = snapshot.HostFee,
            HostSpace = snapshot.HostSpace,
            PageQuota = snapshot.PageQuota,
            UserQuota = snapshot.UserQuota,
            PaymentProcessor = snapshot.PaymentProcessor,
            ProcessorUserId = snapshot.ProcessorUserId,
            Description = snapshot.Description,
            KeyWords = snapshot.KeyWords,
            BackgroundFile = snapshot.BackgroundFile,
            SiteLogHistory = snapshot.SiteLogHistory,
            SplashTabId = snapshot.SplashTabId,
            HomeTabId = snapshot.HomeTabId,
            LoginTabId = snapshot.LoginTabId,
            UserTabId = snapshot.UserTabId,
            DefaultLanguage = snapshot.DefaultLanguage,
            TimeZoneOffset = snapshot.TimeZoneOffset,
            HomeDirectory = snapshot.HomeDirectory,
            ConcurrencyToken = snapshot.ConcurrencyToken,
        };

    /// <summary>The value one caller writes, distinct per caller so the store names its winner.</summary>
    /// <param name="index">The caller's index.</param>
    /// <returns>The value to write.</returns>
    private static string DescriptionFor(int index) =>
        "Simultaneous writer " + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Renders an identifier for a route segment.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant rendering.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];

    /// <summary>What one caller in a race was answered.</summary>
    /// <param name="Index">The caller's index, which identifies the value it wrote.</param>
    /// <param name="Status">The status it was answered.</param>
    /// <param name="Body">The response body, verbatim.</param>
    private sealed record RaceOutcome(int Index, HttpStatusCode Status, string Body);
}
