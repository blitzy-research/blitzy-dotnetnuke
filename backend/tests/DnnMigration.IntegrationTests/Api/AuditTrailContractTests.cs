using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Enums;
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

    /// <summary>Identifier every audit record outside the three named families is written under.</summary>
    /// <remarks>
    /// Written as a literal for the same reason the two named identifiers above are: an operator's alert rule
    /// addresses an event by this number, so renumbering it silently detaches whatever was watching.
    /// </remarks>
    private const int GeneralAuditEventId = 1000;

    /// <summary>Symbolic name the general audit identifier carries.</summary>
    private const string GeneralAuditEventName = "Audit";

    /// <summary>Resource type the account events are recorded against.</summary>
    private const string UserResourceType = "User";

    /// <summary>Resource type the role events are recorded against.</summary>
    private const string RoleResourceType = "Role";

    /// <summary>Resource type the module events are recorded against.</summary>
    private const string ModuleResourceType = "Module";

    /// <summary>
    /// Every audit event name the application declares, against the assertion that proves it is emitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This registry is the completeness guard's subject, and it exists because emission coverage cannot be
    /// discovered at run time: a test assembly cannot see which of its own facts reference which symbol.
    /// Declaring the mapping makes the omission FAIL rather than merely go unnoticed - a newly declared event
    /// has no entry, and <see cref="EveryDeclaredAuditEventName_IsCoveredByANamedAssertion"/> refuses it.
    /// </para>
    /// <para>
    /// Entries reading "this suite" name a fact in this file, which drives the event through the composed
    /// HTTP pipeline. The others name the suite that asserts the event where its emitting branch is
    /// reachable: several events are raised from branches that need a store failure, a lockout state or a
    /// legacy credential format to reach, and a service-level fact can arrange those where a request cannot.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> AuditEventCoverage =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditEventNames.UserCreated] = "this suite - CreatingAnAccount_EmitsTheAccountCreationEvent",
            [AuditEventNames.UserDeleted] = "UnitTests/Services/UserServiceTests",
            [AuditEventNames.LoginSuperUser] = "UnitTests/Services/AuthServiceTests",
            [AuditEventNames.LoginSuccess] = "UnitTests/Services/AuthServiceTests",
            [AuditEventNames.LoginFailure] = "this suite - RefusedSignIn_EmitsTheOutcomeEvent",
            [AuditEventNames.LoginUserLockedOut] = "UnitTests/Services/AuthServiceTests",
            [AuditEventNames.LoginUserNotApproved] = "UnitTests/Services/AuthServiceTests",
            [AuditEventNames.PortalCreated] = "this suite - InstallingATenant_EmitsTheInstallationEvent",
            [AuditEventNames.PortalDeleted] = "UnitTests/Services/PortalServiceTests",
            [AuditEventNames.TabUpdated] = "UnitTests/Services/TabServiceTests",
            [AuditEventNames.TabSentToRecycleBin] = "UnitTests/Services/TabServiceTests",
            [AuditEventNames.TabRestored] = "UnitTests/Services/TabServiceTests",
            [AuditEventNames.UserRoleCreated] = "UnitTests/Services/RoleServiceTests",
            [AuditEventNames.UserRoleUpdated] = "UnitTests/Services/RoleServiceTests",
            [AuditEventNames.UserRoleDeleted] = "UnitTests/Services/RoleServiceTests",
            [AuditEventNames.RoleCreated] =
                "this suite - CreatingAndAmendingARole_EmitsTheCreationAndAmendmentEvents",
            [AuditEventNames.RoleUpdated] =
                "this suite - CreatingAndAmendingARole_EmitsTheCreationAndAmendmentEvents",
            [AuditEventNames.RoleDeleted] = "UnitTests/Services/RoleServiceTests",
            [AuditEventNames.SessionRenewed] = "UnitTests/Services/AuthServiceTests",
            [AuditEventNames.SessionRefused] = "UnitTests/Services/AuthServiceTests",
            [AuditEventNames.SessionEnded] = "UnitTests/Services/AuthServiceTests",
            [AuditEventNames.PasswordRehashFailure] = "UnitTests/Services/AuthServiceTests",
            [AuditEventNames.LegacyCredentialMigrated] = "IntegrationTests/Api/AuthApiTests",
            [AuditEventNames.HostAlert] = "this suite - InstallingATenant_EmitsTheInstallationEvent",
            [AuditEventNames.ModuleUpdated] =
                "this suite - AmendingAModule_EmitsTheAmendmentEventWithItsBlastRadius",
            [AuditEventNames.ModuleDeleted] =
                "this suite - WithdrawingAPlacementAndRemovingAModule_EmitTheTwoDistinctRemovalEvents",
            [AuditEventNames.ModulePlacementDeleted] =
                "this suite - WithdrawingAPlacementAndRemovingAModule_EmitTheTwoDistinctRemovalEvents",
            [AuditEventNames.ModuleRestored] =
                "this suite - RecyclingAndRestoringAModule_EmitsTheRemovalAndRestorationEvents",
            [AuditEventNames.ModuleExported] = "this suite - ExportingModuleContent_EmitsTheExportEvent",
            [AuditEventNames.UserDataExported] = "this suite - ExportingPersonalData_EmitsTheDataExportEvent",
            [AuditEventNames.ServiceCodeRedemptionFailure] =
                "this suite - RedeemingAnInvitationCode_EmitsTheRefusalAndTheRedemptionEvents",
            [AuditEventNames.ServiceCodeRedeemed] =
                "this suite - RedeemingAnInvitationCode_EmitsTheRefusalAndTheRedemptionEvents",
        };


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

    /// <summary>
    /// Creating an account through the API emits the account-creation event against the new account, with
    /// the acting administrator named and no submitted name or credential retained.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Driven through HTTP rather than through the service, because the record is only useful if it survives
    /// the whole composed pipeline - the sink is registered by the API's composition root, and a
    /// service-level double cannot show that the registration is present or that the admission policy lets
    /// these facts through.
    /// </remarks>
    [Fact]
    public async Task CreatingAnAccount_EmitsTheAccountCreationEvent()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        string suffix = Suffix();
        string username = "audit_user_" + suffix;
        const string password = "Audited-Account-1!";

        int firstNewRecord = RecordedLogs.Snapshot().Count;

        using HttpResponseMessage response = await administrator.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            new CreateUserRequest
            {
                Username = username,
                FirstName = "Audited",
                LastName = "Account",
                DisplayName = "Audited Account " + suffix,
                Email = "audit." + suffix + "@example.com",
                Password = password,
                ConfirmPassword = password,
                Authorize = true,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        UserDetailDto created = (await response.Content.ReadEnvelopeAsync<UserDetailDto>())!;

        LogRecord record = SingleAuditSince(
            firstNewRecord,
            AuditEventNames.UserCreated,
            UserResourceType,
            created.UserId);

        record.EventName.Should().Be(GeneralAuditEventName);
        record.Properties["AuditEvent"].Should().Be(AuditEventNames.UserCreated);
        record.Properties["AuditPortalId"].Should().Be(_fixture.Seed.PortalId);
        record.Properties["AuditActorUserId"].Should().Be(
            _fixture.Seed.HostUserId,
            "the trail must say who created the account, which is the fact the legacy entry could not carry");
        record.Properties["AuditSubjectUserId"].Should().Be(created.UserId);
        record.Properties["AuditMetadata_Approved"].Should().Be(
            "True",
            "whether the account was created already approved decides whether it can sign in, so it is the "
            + "one fact of this event worth reading");
        record.Properties.Should().NotContainKey("AuditActorUserName");
        record.Message.Should().NotContain(username);
        record.Message.Should().NotContain(password, "an account's opening credential is still a credential");
    }

    /// <summary>
    /// Creating a role and then amending it emit two distinct events against the same role, and the
    /// amendment carries no fact of its own.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Both are asserted in one fact because the amendment needs a role to amend, and because the pair is
    /// what makes each one legible: two records under the same resource identifier with different event
    /// names is the shape an operator reads a role's history from.
    /// </remarks>
    [Fact]
    public async Task CreatingAndAmendingARole_EmitsTheCreationAndAmendmentEvents()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        string roleName = "Audited Role " + Suffix();

        int firstNewRecord = RecordedLogs.Snapshot().Count;

        using HttpResponseMessage createResponse = await administrator.PostAsJsonAsync(
            new Uri("/api/v1/roles", UriKind.Relative),
            new CreateRoleRequest
            {
                RoleName = roleName,
                Description = "Created by the audit contract suite.",
                IsPublic = false,
                AutoAssignment = false,
            },
            ApiTestFixture.Json);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto created = (await createResponse.Content.ReadEnvelopeAsync<RoleDetailDto>())!;
        Uri itemRoute = new($"/api/v1/roles/{Address(created.RoleId)}", UriKind.Relative);

        try
        {
            LogRecord creation = SingleAuditSince(
                firstNewRecord,
                AuditEventNames.RoleCreated,
                RoleResourceType,
                created.RoleId);

            creation.Properties["AuditActorUserId"].Should().Be(_fixture.Seed.HostUserId);
            creation.Properties["AuditPortalId"].Should().Be(_fixture.Seed.PortalId);
            creation.Properties["AuditMetadata_AutoAssignment"].Should().Be(
                "False",
                "a role that assigns itself to every new account is the one creation an operator must be "
                + "able to spot, so the flag travels with the record");

            // The role key seeds at ZERO, so the identifier the record carries can legitimately be "0" and
            // must never be read as an absent resource.
            creation.Properties["AuditResourceId"].Should().Be(
                created.RoleId.ToString(CultureInfo.InvariantCulture));

            int beforeAmendment = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage amended = await administrator.PutAsJsonAsync(
                itemRoute,
                new UpdateRoleRequest
                {
                    RoleName = roleName,
                    Description = "Amended by the audit contract suite.",
                },
                ApiTestFixture.Json);

            amended.StatusCode.Should().Be(HttpStatusCode.OK);

            LogRecord amendment = SingleAuditSince(
                beforeAmendment,
                AuditEventNames.RoleUpdated,
                RoleResourceType,
                created.RoleId);

            amendment.Properties["AuditActorUserId"].Should().Be(_fixture.Seed.HostUserId);
            amendment.Properties["AuditPropertyCount"].Should().Be(
                0,
                "the amendment offers no fact of its own, and a record claiming facts it does not carry "
                + "would be worse than a bare one");
            amendment.Properties["AuditPropertyWithheldCount"].Should().Be(0);
        }
        finally
        {
            using HttpResponseMessage removed = await administrator.DeleteAsync(itemRoute);

            removed.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Amending a module emits the amendment event carrying the blast radius of the amendment rather than
    /// the free text the caller submitted.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The amendment asks to be named the tenant's default module, which is one of the three tenant-scoped
    /// fields. It is used here because it always registers an EFFECT - the seeded tenant publishes no site
    /// settings instance to record the designation against, so the effect is the refusal to record it - and
    /// the event is emitted only when an amendment had an effect. An ordinary title change has none, which
    /// is deliberate: a record per keystroke would bury the amendments that moved something.
    /// </remarks>
    [Fact]
    public async Task AmendingAModule_EmitsTheAmendmentEventWithItsBlastRadius()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(administrator);
        Uri itemRoute = ModuleRoute(created.ModuleId);

        try
        {
            int firstNewRecord = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage response = await administrator.PutAsJsonAsync(
                itemRoute,
                new UpdateModuleRequest
                {
                    TabId = _fixture.Seed.RootTabId,
                    ModuleTitle = created.ModuleTitle,
                    SetAsDefaultSettings = true,
                },
                ApiTestFixture.Json);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            LogRecord record = SingleAuditSince(
                firstNewRecord,
                AuditEventNames.ModuleUpdated,
                ModuleResourceType,
                created.ModuleId);

            record.Properties["AuditMetadata_Operation"].Should().Be("Update");
            record.Properties["AuditMetadata_SetAsDefaultSettings"].Should().Be("True");
            record.Properties["AuditMetadata_TabId"].Should().Be(
                _fixture.Seed.RootTabId.ToString(CultureInfo.InvariantCulture));
            record.Properties["AuditMetadata_AffectedTabCount"].Should().Be("1");
            record.Properties["AuditMetadata_EffectCount"].Should().Be(
                "1",
                "the count is what tells a reader the amendment did something, and it is the reason a "
                + "no-effect amendment writes nothing at all");
            record.Message.Should().NotContain(
                created.ModuleTitle!,
                "a module title is caller free text and no audit member carries it");
        }
        finally
        {
            using HttpResponseMessage removed = await administrator.DeleteAsync(itemRoute);

            removed.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Sending a module to the recycle bin and then bringing it back emit two DIFFERENT events, so the two
    /// halves of the round trip cannot be confused for one another in the trail.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RecyclingAndRestoringAModule_EmitsTheRemovalAndRestorationEvents()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(administrator);
        Uri itemRoute = ModuleRoute(created.ModuleId);

        try
        {
            int beforeRecycle = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage recycled = await administrator.PutAsJsonAsync(
                itemRoute,
                new UpdateModuleRequest
                {
                    TabId = _fixture.Seed.RootTabId,
                    ModuleTitle = created.ModuleTitle,
                    IsDeleted = true,
                },
                ApiTestFixture.Json);

            recycled.StatusCode.Should().Be(HttpStatusCode.OK);

            LogRecord removal = SingleAuditSince(
                beforeRecycle,
                AuditEventNames.ModuleDeleted,
                ModuleResourceType,
                created.ModuleId);

            removal.Properties["AuditMetadata_Operation"].Should().Be("Recycle");
            removal.Properties["AuditMetadata_IsDeleted"].Should().Be("True");
            removal.Properties["AuditMetadata_TabModuleId"].Should().Be(
                created.TabModuleId.ToString(CultureInfo.InvariantCulture));

            int beforeRestore = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage restored = await administrator.PutAsJsonAsync(
                itemRoute,
                new UpdateModuleRequest
                {
                    TabId = _fixture.Seed.RootTabId,
                    ModuleTitle = created.ModuleTitle,
                    IsDeleted = false,
                },
                ApiTestFixture.Json);

            restored.StatusCode.Should().Be(HttpStatusCode.OK);

            LogRecord restoration = SingleAuditSince(
                beforeRestore,
                AuditEventNames.ModuleRestored,
                ModuleResourceType,
                created.ModuleId);

            restoration.Properties["AuditMetadata_Operation"].Should().Be("Restore");
            restoration.Properties["AuditMetadata_IsDeleted"].Should().Be("False");

            AuditsSince(
                beforeRestore,
                AuditEventNames.ModuleDeleted,
                ModuleResourceType,
                created.ModuleId)
                .Should().BeEmpty(
                    "a restoration that also recorded a removal would make the recycle bin's history "
                    + "unreadable");
        }
        finally
        {
            using HttpResponseMessage removed = await administrator.DeleteAsync(itemRoute);

            removed.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Withdrawing one placement and removing a whole module are recorded as DIFFERENT events, because the
    /// first leaves the module in existence and the second does not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Two modules are used rather than one, so that the whole-module removal is observed on a module whose
    /// placement is intact - the state an operator actually deletes from - rather than on one this fact had
    /// already emptied.
    /// </para>
    /// <para>
    /// The withdrawn module is created on EVERY page, so a placement survives its withdrawal and the module
    /// genuinely does still exist afterwards. Withdrawing the LAST placement now recycles the module and
    /// records both facts, which the module suite asserts separately; this case is the one where only the
    /// placement went.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WithdrawingAPlacementAndRemovingAModule_EmitTheTwoDistinctRemovalEvents()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        ModuleDetailDto withdrawn = await CreateModuleAsync(administrator, onEveryPage: true);
        ModuleDetailDto recycled = await CreateModuleAsync(administrator);

        try
        {
            int beforeWithdrawal = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage placementRemoved = await administrator.DeleteAsync(new Uri(
                $"/api/v1/modules/{Address(withdrawn.ModuleId)}"
                    + $"?tabModuleId={Address(withdrawn.TabModuleId)}",
                UriKind.Relative));

            placementRemoved.StatusCode.Should().Be(HttpStatusCode.NoContent);

            LogRecord placement = SingleAuditSince(
                beforeWithdrawal,
                AuditEventNames.ModulePlacementDeleted,
                ModuleResourceType,
                withdrawn.ModuleId);

            placement.Properties["AuditMetadata_Operation"].Should().Be("RemovePlacement");
            placement.Properties["AuditMetadata_TabModuleId"].Should().Be(
                withdrawn.TabModuleId.ToString(CultureInfo.InvariantCulture),
                "which placement went is the only fact that distinguishes this record from the next "
                + "withdrawal of the same module");
            placement.Properties["AuditMetadata_AffectedTabCount"].Should().Be("1");
            placement.Properties.Should().NotContainKey(
                "AuditMetadata_RecycledWithLastPlacement",
                "a placement survived, so the module was not recycled with this withdrawal");

            AuditsSince(
                beforeWithdrawal,
                AuditEventNames.ModuleDeleted,
                ModuleResourceType,
                withdrawn.ModuleId)
                .Should().BeEmpty(
                    "the module still exists, and a trail saying it was removed would send an operator to "
                    + "the recycle bin to look for something that was never sent there");

            int beforeRecycle = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage moduleRemoved =
                await administrator.DeleteAsync(ModuleRoute(recycled.ModuleId));

            moduleRemoved.StatusCode.Should().Be(HttpStatusCode.NoContent);

            LogRecord removal = SingleAuditSince(
                beforeRecycle,
                AuditEventNames.ModuleDeleted,
                ModuleResourceType,
                recycled.ModuleId);

            removal.Properties["AuditMetadata_Operation"].Should().Be("Recycle");
            removal.Properties["AuditMetadata_TabModuleId"].Should().BeNull(
                "no single placement was addressed, and naming one would misdescribe what happened");
        }
        finally
        {
            using HttpResponseMessage cleared = await administrator.DeleteAsync(ModuleRoute(withdrawn.ModuleId));

            cleared.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Exporting a module's content emits the export event, carrying the size of what left and the fact
    /// that a registered controller produced it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The seeded package is temporarily made portable and pointed at the controller the fixture registers,
    /// then put back. That is the only way this path can be reached at all: the export is gated on a
    /// REGISTERED business controller, so without one the endpoint can only be observed refusing - and a
    /// refusal says nothing about the record the success path writes. The column is restored in a finally
    /// block because it belongs to a package every other fact in the assembly reads.
    /// </remarks>
    [Fact]
    public async Task ExportingModuleContent_EmitsTheExportEvent()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(administrator);

        // 1 is the portable capability bit, reproducing the legacy SupportedFeatures encoding exactly.
        await _fixture.Database.ExecuteAsync(
            """
            UPDATE [dbo].[DesktopModules]
               SET [BusinessControllerClass] = @controllerClass,
                   [SupportedFeatures] = 1
             WHERE [DesktopModuleID] = @desktopModuleId;
            """,
            new Dictionary<string, object?>
            {
                ["controllerClass"] = ApiTestFixture.PortableModuleControllerName,
                ["desktopModuleId"] = _fixture.Seed.DesktopModuleId,
            });

        try
        {
            int firstNewRecord = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage response = await administrator.PostAsJsonAsync(
                new Uri($"/api/v1/modules/{Address(created.ModuleId)}/export", UriKind.Relative),
                new ModuleExportRequest { FileName = "audited-content.xml" },
                ApiTestFixture.Json);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Contain(ApiTestFixture.PortableModuleContent);

            LogRecord record = SingleAuditSince(
                firstNewRecord,
                AuditEventNames.ModuleExported,
                ModuleResourceType,
                created.ModuleId);

            record.Properties["AuditMetadata_Operation"].Should().Be("Export");
            record.Properties["AuditMetadata_BusinessControllerRegistered"].Should().Be(
                "True",
                "content can only leave through a registered controller, and the record says so rather "
                + "than leaving a reader to infer it");
            record.Properties["AuditMetadata_PayloadLength"].Should().Be(
                ApiTestFixture.PortableModuleContent.Length.ToString(CultureInfo.InvariantCulture),
                "how much content left is the fact a data-egress question is answered with; the content "
                + "itself is never recorded");
            record.Message.Should().NotContain(
                ApiTestFixture.PortableModuleContent,
                "the exported content is the module's data and no audit member carries it");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                """
                UPDATE [dbo].[DesktopModules]
                   SET [BusinessControllerClass] = NULL,
                       [SupportedFeatures] = 0
                 WHERE [DesktopModuleID] = @desktopModuleId;
                """,
                new Dictionary<string, object?> { ["desktopModuleId"] = _fixture.Seed.DesktopModuleId });

            using HttpResponseMessage removed = await administrator.DeleteAsync(ModuleRoute(created.ModuleId));

            removed.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Taking a subject's personal data emits the data-export event naming the administrator who took it and
    /// the account it described.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: NET-NEW. The legacy had no personal-data export and therefore audited none. This record is
    /// what makes the facility answerable: a subject-access export moves an account's whole profile out of
    /// the application, and an export nobody can attribute afterwards is indistinguishable from a leak.
    /// <para>
    /// The three facts the producer offers alongside it - the profile-value count, the role-assignment count
    /// and whether the export was self-service - are NOT admitted by the sink's closed metadata vocabulary,
    /// so they are withheld and counted as withheld. That is asserted rather than glossed over: the counters
    /// exist precisely so a withheld fact is visible, and admitting these three later must be a deliberate
    /// change that this fact makes someone look at.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExportingPersonalData_EmitsTheDataExportEvent()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        int firstNewRecord = RecordedLogs.Snapshot().Count;

        using HttpResponseMessage response = await administrator.GetAsync(new Uri(
            $"/api/v1/users/{Address(_fixture.Seed.MemberUserId)}/personal-data",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        LogRecord record = SingleAuditSince(
            firstNewRecord,
            AuditEventNames.UserDataExported,
            UserResourceType,
            _fixture.Seed.MemberUserId);

        record.Properties["AuditActorUserId"].Should().Be(
            _fixture.Seed.AdminUserId,
            "who took the export is the whole point of recording it");
        record.Properties["AuditSubjectUserId"].Should().Be(_fixture.Seed.MemberUserId);
        record.Properties["AuditPortalId"].Should().Be(_fixture.Seed.PortalId);
        record.Properties["AuditPropertyCount"].Should().Be(
            0,
            "the sink's metadata vocabulary is closed, and it admits none of the three keys this producer "
            + "offers");
        record.Properties["AuditPropertyWithheldCount"].Should().Be(
            3,
            "the producer offers ProfileValues, RoleAssignments and SelfService, and the count is how a "
            + "reader learns that three facts were offered and none survived the admission policy");
        record.Message.Should().NotContain(IntegrationSeed.MemberUserName);
    }

    /// <summary>
    /// A refused invitation code and an accepted one emit DIFFERENT events, and neither record carries the
    /// submitted code.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: NET-NEW, and the refusal is the more important of the two. The legacy handler answered a
    /// miss with an on-screen sentence and wrote nothing at all, so an installation's codes could be guessed
    /// at indefinitely without leaving a trace.
    /// </remarks>
    [Fact]
    public async Task RedeemingAnInvitationCode_EmitsTheRefusalAndTheRedemptionEvents()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        string suffix = Suffix();
        string username = "audit_member_" + suffix;
        const string password = "Audited-Member-1!";
        string code = "audit-code-" + suffix;

        using HttpResponseMessage accountCreated = await administrator.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            new CreateUserRequest
            {
                Username = username,
                FirstName = "Audited",
                LastName = "Member",
                DisplayName = "Audited Member " + suffix,
                Email = "audit-member." + suffix + "@example.com",
                Password = password,
                ConfirmPassword = password,
                Authorize = true,
            },
            ApiTestFixture.Json);

        accountCreated.StatusCode.Should().Be(HttpStatusCode.Created);

        UserDetailDto account = (await accountCreated.Content.ReadEnvelopeAsync<UserDetailDto>())!;

        int roleId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Roles]
                ([PortalID], [RoleName], [Description], [ServiceFee], [BillingPeriod], [BillingFrequency],
                 [TrialFee], [TrialPeriod], [TrialFrequency], [IsPublic], [AutoAssignment], [RSVPCode])
            VALUES (@portalId, @roleName, 'By invitation', 0, 0, 'N', 0, 0, 'N', 0, 0, @code);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["roleName"] = "Audited Invitation " + suffix,
                ["code"] = code,
            });

        try
        {
            using HttpClient owner = await _fixture.CreateClientForAsync(username, password);
            Uri redemptionRoute = new(
                $"/api/v1/users/{Address(account.UserId)}/services/redemptions",
                UriKind.Relative);

            int beforeRefusal = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage refused = await owner.PostAsJsonAsync(
                redemptionRoute,
                new RedeemServiceCodeRequest { Code = code + "-wrong" },
                ApiTestFixture.Json);

            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            LogRecord refusal = SingleAuditSince(
                beforeRefusal,
                AuditEventNames.ServiceCodeRedemptionFailure,
                UserResourceType,
                account.UserId);

            refusal.Properties["AuditActorUserId"].Should().Be(account.UserId);
            refusal.Properties["AuditSubjectUserId"].Should().Be(account.UserId);
            refusal.Message.Should().NotContain(
                code,
                "a record carrying the submitted code would hand a reader of the log the guesses as well as "
                + "the fact that guessing happened");

            int beforeRedemption = RecordedLogs.Snapshot().Count;

            using HttpResponseMessage redeemed = await owner.PostAsJsonAsync(
                redemptionRoute,
                new RedeemServiceCodeRequest { Code = code },
                ApiTestFixture.Json);

            redeemed.StatusCode.Should().Be(HttpStatusCode.OK);

            LogRecord redemption = SingleAuditSince(
                beforeRedemption,
                AuditEventNames.ServiceCodeRedeemed,
                UserResourceType,
                account.UserId);

            redemption.Properties["AuditActorUserId"].Should().Be(account.UserId);
            redemption.Message.Should().NotContain(code);

            AuditsSince(
                beforeRedemption,
                AuditEventNames.ServiceCodeRedemptionFailure,
                UserResourceType,
                account.UserId)
                .Should().BeEmpty("an accepted code is not also a refusal");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[UserRoles] WHERE [RoleID] = @roleId;",
                new Dictionary<string, object?> { ["roleId"] = roleId });

            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
                new Dictionary<string, object?> { ["roleId"] = roleId });
        }
    }

    /// <summary>
    /// Every audit event name the application declares is covered by a named assertion, so a new event
    /// cannot be added and shipped without one.
    /// </summary>
    /// <remarks>
    /// This is the guard the eleven facts above would otherwise still be missing. Asserting emission for the
    /// events that exist today says nothing about the twelfth event somebody adds next; this fact fails the
    /// moment a name is declared that <see cref="AuditEventCoverage"/> does not account for, and fails
    /// equally if an entry outlives the constant it describes.
    /// <para>
    /// The registry records WHERE each event's emission is asserted rather than asserting it here, because
    /// several are asserted at the service level where the emitting branch is reachable and the HTTP surface
    /// does not reach it. The value of the guard is that the question is asked at all - a name with no entry
    /// cannot be committed.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryDeclaredAuditEventName_IsCoveredByANamedAssertion()
    {
        IReadOnlyList<string> declared = typeof(AuditEventNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false }
                && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        declared.Should().HaveCountGreaterThan(
            20,
            "the reflection must be reading the real vocabulary rather than an empty one");

        declared.Except(AuditEventCoverage.Keys, StringComparer.Ordinal).Should().BeEmpty(
            "an audit event with no named assertion can stop being emitted without anything failing, and "
            + "preserving the trail is an explicit obligation of this migration");

        AuditEventCoverage.Keys.Except(declared, StringComparer.Ordinal).Should().BeEmpty(
            "an entry that outlives its constant makes the registry a record of what used to be asserted");
    }

    /// <summary>Returns every audit record for one resource emitted since a snapshot boundary.</summary>
    /// <param name="firstNewRecord">The count of records already captured before the request was made.</param>
    /// <param name="auditEvent">The event name the record must carry.</param>
    /// <param name="resourceType">The resource type the record must carry.</param>
    /// <param name="resourceId">The resource identifier the record must carry.</param>
    /// <returns>The matching records, in the order they were emitted.</returns>
    /// <remarks>
    /// Located by the STRUCTURED members rather than by position or by a substring of the rendered message.
    /// The whole assembly shares one host and one sink, so "the last record" would be a race even in a
    /// serial collection - the host itself logs outside any request.
    /// </remarks>
    private static IReadOnlyList<LogRecord> AuditsSince(
        int firstNewRecord,
        string auditEvent,
        string resourceType,
        int resourceId)
    {
        string addressed = resourceId.ToString(CultureInfo.InvariantCulture);

        return RecordedLogs.Snapshot()
            .Skip(firstNewRecord)
            .Where(candidate => candidate.EventId == GeneralAuditEventId
                && Equals(candidate.Properties.GetValueOrDefault("AuditEvent"), auditEvent)
                && Equals(candidate.Properties.GetValueOrDefault("AuditResourceType"), resourceType)
                && Equals(candidate.Properties.GetValueOrDefault("AuditResourceId"), addressed))
            .ToList();
    }

    /// <summary>Returns the one audit record for a resource, failing the test when there is not exactly one.</summary>
    /// <param name="firstNewRecord">The count of records already captured before the request was made.</param>
    /// <param name="auditEvent">The event name the record must carry.</param>
    /// <param name="resourceType">The resource type the record must carry.</param>
    /// <param name="resourceId">The resource identifier the record must carry.</param>
    /// <returns>The single matching record.</returns>
    private static LogRecord SingleAuditSince(
        int firstNewRecord,
        string auditEvent,
        string resourceType,
        int resourceId)
    {
        return AuditsSince(firstNewRecord, auditEvent, resourceType, resourceId)
            .Should().ContainSingle(
                FormattableString.Invariant(
                    $"exactly one {auditEvent} record is expected for {resourceType} {resourceId}"))
            .Subject;
    }

    /// <summary>Creates a module on the seeded root page, failing the test when creation is refused.</summary>
    /// <param name="client">A client entitled to create a module.</param>
    /// <param name="onEveryPage">
    /// Whether the module is placed on every content page, which is how a case obtains a module with more
    /// than one placement - and therefore one that survives having a single placement withdrawn.
    /// </param>
    /// <returns>The created module.</returns>
    private async Task<ModuleDetailDto> CreateModuleAsync(HttpClient client, bool onEveryPage = false)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/modules", UriKind.Relative),
            new CreateModuleRequest
            {
                ModuleDefId = _fixture.Seed.ModuleDefinitionId,
                TabId = _fixture.Seed.RootTabId,
                ModuleTitle = "Audited Module " + Suffix(),
                ModuleOrder = 2,
                AllTabs = onEveryPage,
                InheritViewPermissions = true,
                Visibility = ModuleVisibility.Maximized,
                DisplayTitle = true,
                CacheTime = 0,
                IconFile = "module.gif",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        ModuleDetailDto? created = await response.Content.ReadEnvelopeAsync<ModuleDetailDto>();
        created.Should().NotBeNull();

        return created!;
    }

    /// <summary>Builds the item route for one module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <returns>A relative route; the request host resolves the tenant.</returns>
    private static Uri ModuleRoute(int moduleId) =>
        new($"/api/v1/modules/{Address(moduleId)}", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Address(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a suffix that keeps a created name unique across runs of the shared database.</summary>
    /// <returns>Twelve hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
