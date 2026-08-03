using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.Portal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that the business audit events the legacy application wrote are actually emitted, carry the facts
/// they are read for, and carry nothing sensitive.
/// </summary>
/// <remarks>
/// <para>
/// The unit suites assert what each service HANDS to the audit contract. That is the right place for the
/// property set, but it cannot establish the two things that only a composed host can: that the contract
/// resolves to an implementation at all, and that the implementation's message templates and their
/// arguments still agree. A template whose placeholders had drifted apart from its arguments would carry
/// every property and render them in the wrong places, and no unit test could see it.
/// </para>
/// <para>
/// The records are located by event identifier rather than by position, because the whole assembly shares
/// one host and one sink: other suites are logging while these facts run, so "the last record" would be a
/// race and "the only record" would be false.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class AuditTrailContractTests
{
    /// <summary>Identifier the sign-in outcome event is written under.</summary>
    /// <remarks>
    /// Written as a literal rather than read from the implementation, because the implementation is internal
    /// to the persistence assembly AND because a stable identifier is exactly the kind of value that must
    /// not be able to change without a test noticing. An operator's alert rule addresses an event by this
    /// number; renumbering it silently detaches whatever was watching it.
    /// </remarks>
    private const int SignInOutcomeEventId = 1001;

    /// <summary>Identifier the tenant installation event is written under.</summary>
    private const int PortalInstallationEventId = 1002;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="AuditTrailContractTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public AuditTrailContractTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The audit contract resolves from the composed host.</summary>
    /// <remarks>
    /// The container is validated when the host is built, so an unregistered contract would already have
    /// failed every integration fact in the assembly - which is a real guarantee but an accidental one.
    /// Asserting it here says why it matters: the contract is declared in the layer that cannot name a
    /// logger and implemented in the layer that can, so nothing but a registration joins the two.
    /// </remarks>
    [Fact]
    public void AuditTrail_ResolvesFromTheCompositionRoot()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAuditSink>().Should().NotBeNull();
    }

    /// <summary>
    /// A refused sign-in emits the outcome event, carrying the outcome name, the tenant, the submitted
    /// account name and no credential.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A refusal is used because it is what the legacy audited, and because it is the case a request log
    /// cannot describe: a refused sign-in and a refused authorisation are the same status code to a request
    /// log, and neither says which account name was tried.
    /// </remarks>
    [Fact]
    public async Task RefusedSignIn_EmitsTheOutcomeEvent()
    {
        string attempted = "audit-probe-" + Guid.NewGuid().ToString("N")[..8];

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest { Username = attempted, Password = "not-the-stored-credential" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        LogRecord record = RecordedLogs.Snapshot()
            .Where(candidate => candidate.EventId == SignInOutcomeEventId)
            .Single(candidate => candidate.Message.Contains(attempted, StringComparison.Ordinal));

        record.Level.Should().Be(
            LogEventLevel.Warning,
            "a run of refused sign-ins is the signal an operator wants raised, so a refusal is not merely "
            + "informational");
        // MIGRATION: the identifying facts are carried as the trail's own uniform members rather than as
        // per-event property names. The envelope is one fixed template for every event, so an operator
        // filters the family by this record's number and narrows to a single event by the AuditEvent
        // property - which is also why the event name asserted here is the LEGACY name, preserved verbatim,
        // rather than a name this migration coined.
        record.EventName.Should().Be("SignInOutcome");
        record.Properties["AuditEvent"].Should().Be("LOGIN_FAILURE");
        record.Properties["AuditOutcome"]!.ToString().Should().Be("Denied");
        record.Properties["AuditActorUserName"].Should().Be(attempted);
        record.Properties["AuditPortalId"].Should().Be(_fixture.Seed.PortalId);

        record.Message.Should().NotContain(
            "not-the-stored-credential",
            "no submitted credential may reach a log record; the audit contract declares no member that "
            + "could carry one");
    }

    /// <summary>An accepted sign-in emits the outcome event at the informational level.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: the legacy audited refusals only, so this is a documented superset. It is asserted because
    /// a trail that records refusals but not acceptances cannot answer the first question asked of an
    /// authentication trail - who got in - and because the resolved account identifier is carried here where
    /// the legacy always wrote its absence sentinel.
    /// </remarks>
    [Fact]
    public async Task AcceptedSignIn_EmitsTheOutcomeEventWithTheResolvedAccount()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Located by the submitted name and taken as the LAST such event, because other suites sign the same
        // seeded administrator in and every one of those attempts is audited too.
        LogRecord record = RecordedLogs.Snapshot()
            .Where(candidate => candidate.EventId == SignInOutcomeEventId)
            .Last(candidate => Equals(
                candidate.Properties.GetValueOrDefault("AuditActorUserName"),
                IntegrationSeed.AdminUserName));

        record.Level.Should().Be(LogEventLevel.Information);
        record.Properties["AuditOutcome"]!.ToString().Should().Be("Succeeded");
        record.Properties["AuditActorUserId"].Should().Be(
            _fixture.Seed.AdminUserId,
            "the resolved account is what the legacy trail could never say - it always wrote its integer "
            + "absence sentinel here");
        record.Properties["AuditSubjectUserId"].Should().Be(
            _fixture.Seed.AdminUserId,
            "and the subject of a sign-in is the account that signed in");

        record.Message.Should().NotContain(
            ApiTestFixture.KnownPassword,
            "an accepted credential is still a credential");
    }

    /// <summary>
    /// Installing a tenant emits the installation event, carrying the legacy property set and no credential.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The rendered message is asserted as well as the properties, because this is the event with twelve of
    /// them - the one where a template and its arguments are most likely to drift apart, and where that
    /// drift would produce a record that looked populated and read as nonsense.
    /// </remarks>
    [Fact]
    public async Task InstallingATenant_EmitsTheInstallationEvent()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string alias = "audit-" + suffix + ".example";
        string password = "Audited-Portal-1!";

        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new CreatePortalRequest
            {
                PortalName = "Audited Portal " + suffix,
                PortalAlias = alias,
                TemplateFile = "admin.template",
                HomeDirectory = string.Empty,
                Description = "Installed by the audit contract suite.",
                KeyWords = "audit, contract",
                AdministratorFirstName = "Grace",
                AdministratorLastName = "Hopper",
                AdministratorUsername = "audit_admin_" + suffix,
                AdministratorPassword = password,
                IsChildPortal = false,
                AdministratorEmail = "audit-" + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        LogRecord record = RecordedLogs.Snapshot()
            .Where(candidate => candidate.EventId == PortalInstallationEventId)
            .Single(candidate => candidate.Message.Contains(alias, StringComparison.Ordinal));

        record.Level.Should().Be(LogEventLevel.Information);
        record.EventName.Should().Be("PortalInstalled");
        record.Properties["AuditEvent"].Should().Be("PORTAL_CREATED");
        record.Properties["AuditPortalId"].Should().NotBeNull();

        // MIGRATION: the descriptive facts of one event are carried in the envelope's single detail member,
        // rendered as a deterministic key=value list, rather than as a property per fact. That is deliberate:
        // a property bag's destructuring behaviour differs between logging sinks - one writes a structured
        // map, another calls ToString and records the type name - and an audit trail has to read identically
        // through every sink. The facts asserted are unchanged; only where they are read from moves.
        //
        // The home directory is NOT asserted, because the installation event does not carry it and inventing
        // a producer for it here would assert this suite's own expectation rather than the trail's content.
        // The administrator's resolved identifier is asserted in its place, which is the fact that makes the
        // record answer "who can now sign in to this tenant".
        string detail = record.Properties["AuditProperties"]!.ToString()!;

        detail.Should().Contain("PortalName=Audited Portal " + suffix);
        detail.Should().Contain("PortalAlias=" + alias);
        detail.Should().Contain("Description=Installed by the audit contract suite.");
        detail.Should().Contain("Keywords=audit, contract");
        detail.Should().Contain("AdministratorUsername=audit_admin_" + suffix);
        detail.Should().Contain("AdministratorId=");
        // The two administrator name facts are adjacent in the rendered detail, so finding them rendered
        // adjacently AND IN ORDER proves the renderer's key order still matches the order they were supplied
        // in - which a containment test on each one separately cannot show.
        detail.Should().Contain(
            "AdministratorFirstName=Grace; AdministratorLastName=Hopper",
            "the rendered detail proves the facts and their names still line up, which asserting each one on "
            + "its own cannot show");
        detail.Should().Contain("IsChildPortal=False");

        // The identifier is the property the legacy entry never carried, which is what made its records
        // unjoinable to the tenants they described. It is a first-class member of the envelope rather than a
        // rendered detail, because every event carries it.
        record.Properties["AuditPortalId"].Should().NotBeNull();
        record.Properties["AuditResourceType"].Should().Be("Portal");
        record.Properties["AuditResourceId"].Should().Be(
            record.Properties["AuditPortalId"]!.ToString(),
            "the installed tenant is both the scope the event belongs to and the resource it describes");
        record.Message.Should().NotContain(
            password,
            "the legacy already declined to record the administrator's credential even while holding it in "
            + "cleartext, and the audit contract declares no member that could carry one");
    }
}
