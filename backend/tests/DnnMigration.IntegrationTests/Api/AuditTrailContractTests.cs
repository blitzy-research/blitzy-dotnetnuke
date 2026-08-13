using System.Globalization;
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
/// The records are located by event identifier rather than by position, because the whole assembly shares
/// one host and one sink: other suites are logging while these facts run, so "the last record" would be a
/// race and "the only record" would be false.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class AuditTrailContractTests
{
    /// <summary>Identifier the sign-in outcome event is written under.</summary>
    /// <remarks>
    /// Written as a literal rather than read from the implementation, because the implementation is
    /// internal to the persistence assembly AND because a stable identifier is exactly the kind of value
    /// that must not be able to change without a test noticing. An operator's alert rule addresses an event
    /// by this number; renumbering it silently detaches whatever was watching it.
    /// </remarks>
    private const int SignInOutcomeEventId = 1001;

    /// <summary>Identifier the tenant installation event is written under.</summary>
    private const int PortalInstallationEventId = 1002;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="AuditTrailContractTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public AuditTrailContractTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The audit contract resolves from the composed host.</summary>
    [Fact]
    public void AuditTrail_ResolvesFromTheCompositionRoot()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAuditSink>().Should().NotBeNull();
    }

    /// <summary>
    /// A refused sign-in emits the outcome event, carrying the outcome name and tenant without retaining
    /// the submitted account name or credential.
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
        int firstNewRecord = RecordedLogs.Snapshot().Count;
        string attempted = "audit-probe-" + Guid.NewGuid().ToString("N")[..8];

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest { Username = attempted, Password = "not-the-stored-credential" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        LogRecord record = RecordedLogs.Snapshot()
            .Skip(firstNewRecord)
            .Where(candidate => candidate.EventId == SignInOutcomeEventId)
            .Single(candidate => Equals(
                candidate.Properties.GetValueOrDefault("AuditEvent"),
                AuditEventNames.LoginFailure));

        record.Level.Should().Be(
            LogEventLevel.Warning,
            "a run of refused sign-ins is the signal an operator wants raised, so a refusal is not merely "
            + "informational");
        record.EventName.Should().Be("SignInOutcome");
        record.Properties["AuditEvent"].Should().Be("LOGIN_FAILURE");
        record.Properties["AuditOutcome"]!.ToString().Should().Be("Denied");
        record.Properties["AuditActorUserId"].Should().BeNull();
        record.Properties["AuditPortalId"].Should().Be(_fixture.Seed.PortalId);
        record.Properties.Should().NotContainKey("AuditActorUserName");
        record.Message.Should().NotContain(attempted);

        record.Message.Should().NotContain(
            "not-the-stored-credential",
            "no submitted credential may reach a log record; the audit contract declares no member that "
            + "could carry one");
    }

    /// <summary>An accepted sign-in emits the outcome event at the informational level.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The legacy audited refusals only, so this is a documented superset. It is asserted because a trail
    /// that records refusals but not acceptances cannot answer the first question asked of an
    /// authentication trail - who got in - and because the resolved account identifier is carried here
    /// where the legacy always wrote its absence sentinel.
    /// </remarks>
    [Fact]
    public async Task AcceptedSignIn_EmitsTheOutcomeEventWithTheResolvedAccount()
    {
        int firstNewRecord = RecordedLogs.Snapshot().Count;
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

        // Located among records emitted by this request and by the stable account identifier. Submitted
        // names are not retained by the audit transport.
        LogRecord record = RecordedLogs.Snapshot()
            .Skip(firstNewRecord)
            .Where(candidate => candidate.EventId == SignInOutcomeEventId)
            .Single(candidate => Equals(
                candidate.Properties.GetValueOrDefault("AuditActorUserId"),
                _fixture.Seed.AdminUserId));

        record.Level.Should().Be(LogEventLevel.Information);
        record.Properties["AuditOutcome"]!.ToString().Should().Be("Succeeded");
        record.Properties["AuditActorUserId"].Should().Be(
            _fixture.Seed.AdminUserId,
            "the resolved account is what the legacy trail could never say - it always wrote its integer "
            + "absence sentinel here");
        record.Properties["AuditSubjectUserId"].Should().Be(
            _fixture.Seed.AdminUserId,
            "and the subject of a sign-in is the account that signed in");
        record.Properties.Should().NotContainKey("AuditActorUserName");
        record.Message.Should().NotContain(IntegrationSeed.AdminUserName);

        record.Message.Should().NotContain(
            ApiTestFixture.KnownPassword,
            "an accepted credential is still a credential");
    }

    /// <summary>
    /// Installing a tenant emits the installation event with stable identifiers and bounded operational
    /// metadata, carrying no credential or directly identifying prose.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task InstallingATenant_EmitsTheInstallationEvent()
    {
        int firstNewRecord = RecordedLogs.Snapshot().Count;
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string alias = "audit-" + suffix + ".example";
        string password = "Audited-Portal-1!";

        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        PortalDetailDto? created = await response.Content.ReadEnvelopeAsync<PortalDetailDto>();
        created.Should().NotBeNull();

        // Located by the STRUCTURED resource identifier rather than by a substring of the rendered message.
        IReadOnlyList<LogRecord> installation = RecordedLogs.Snapshot()
            .Skip(firstNewRecord)
            .Where(candidate => candidate.EventId == PortalInstallationEventId
                && Equals(
                    candidate.Properties.GetValueOrDefault("AuditResourceId"),
                    created!.PortalId.ToString()))
            .ToList();

        // TWO records, deliberately, and this is the assertion that proves it.
        installation.Select(candidate => candidate.Properties["AuditEvent"]?.ToString())
            .Should()
            .BeEquivalentTo(
                ["PORTAL_CREATED", "HOST_ALERT"],
                "both legacy intents of a tenant installation are preserved");

        LogRecord record = installation.Single(candidate =>
            string.Equals(candidate.Properties["AuditEvent"]?.ToString(), "PORTAL_CREATED", StringComparison.Ordinal));

        LogRecord alert = installation.Single(candidate =>
            string.Equals(candidate.Properties["AuditEvent"]?.ToString(), "HOST_ALERT", StringComparison.Ordinal));

        alert.Properties["AuditPortalId"].Should().Be(
            record.Properties["AuditPortalId"],
            "the two records describe the same installation, not two of them");
        alert.Properties["AuditPropertyCount"].Should().Be(
            record.Properties["AuditPropertyCount"],
            "the alert carries the same facts; only the name differs");

        record.Level.Should().Be(LogEventLevel.Information);
        record.EventName.Should().Be("PortalInstalled");
        record.Properties["AuditEvent"].Should().Be("PORTAL_CREATED");
        record.Properties["AuditPortalId"].Should().Be(created!.PortalId);
        record.Properties["AuditSubjectUserId"].Should().Be(created.AdministratorId);

        // MIGRATION: DIVERGENCE from the legacy record's shape, and a correction of this suite's own
        // earlier expectation.
        record.Properties["AuditMetadata_IsChildPortal"].Should().Be("False");
        record.Properties.Should().NotContainKeys(
            "AuditActorUserName",
            "AuditProperties",
            "AuditMetadata_PortalName",
            "AuditMetadata_PortalAlias",
            "AuditMetadata_AdministratorUsername",
            "AuditMetadata_AdministratorEmail");

        // The two counts are the envelope's own statement about the facts it carries: how many were
        // attached, and how many were withheld by the admission policy.
        record.Properties["AuditPropertyCount"].Should().Be(
            4,
            "the installation event offers exactly four bounded operational facts, and all are admissible");
        record.Properties["AuditMetadata_IsChildPortal"].Should().Be("False");
        record.Properties["AuditMetadata_AdministratorId"].Should().Be(
            created.AdministratorId!.Value.ToString(CultureInfo.InvariantCulture),
            "the administrator is identified by key, which resolves to the personal data the log omits");
        record.Properties["AuditMetadata_DescriptionSupplied"].Should().Be("True");
        record.Properties["AuditMetadata_KeywordsSupplied"].Should().Be("True");
        record.Properties["AuditPropertyWithheldCount"].Should().Be(
            0,
            "no fact of this event may be withheld; a non-zero count means a key stopped being admissible");

        // The identifier is the property the legacy entry never carried, which is what made its records
        // unjoinable to the tenants they described.
        record.Properties["AuditResourceType"].Should().Be("Portal");
        record.Properties["AuditResourceId"].Should().Be(
            record.Properties["AuditPortalId"]!.ToString(),
            "the installed tenant is both the scope the event belongs to and the resource it describes");
        record.Message.Should().NotContain(alias);
        record.Message.Should().NotContain("Audited Portal " + suffix);
        record.Message.Should().NotContain("audit_admin_" + suffix);
        record.Message.Should().NotContain("Grace");
        record.Message.Should().NotContain("Hopper");
        record.Message.Should().NotContain(
            password,
            "the legacy already declined to record the administrator's credential even while holding it in "
            + "cleartext, and the audit contract declares no member that could carry one");

        foreach (KeyValuePair<string, object?> property in record.Properties)
        {
            property.Value?.ToString().Should().NotContain(
                password,
                "no property may carry the credential either, now that the facts are properties rather than "
                + "one rendered member");
        }
    }
}
