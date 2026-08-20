using System.Globalization;
using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Mapping;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Application;

/// <summary>
/// Pins the portal aggregate's ported legacy contract: the positional argument lists that became named
/// request objects, the two fee clamps, the typed listing envelope, the cache lifetime arithmetic, the
/// identity-seed sentinels, and the audit trail.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS SUITE IS FOR, AND WHAT IT DELIBERATELY LEAVES ALONE. A sibling suite already exercises the
/// tenant WORKFLOW - the provisioning transaction and its enclosure, the host-only field guard, the
/// last-remaining-tenant refusal, and the alias surface. Repeating any of that here would buy nothing and
/// would leave two suites free to disagree about the same rule.
/// </para>
/// <para>
/// Mocked collaborators only, and only the domain and application abstractions. Nothing here reaches a
/// database context, a query root or SQL text; the persistence context is internal to the infrastructure
/// project precisely so that a unit test cannot acquire one.
/// </para>
/// </remarks>
public class PortalServiceApplicationTests
{
    /// <summary>The tenant these assertions address, which is deliberately the identity seed.</summary>
    private const int SeedPortalId = -1;

    /// <summary>The shipped default tenant, which occupies the value immediately after the seed.</summary>
    private const int DefaultPortalId = 0;

    /// <summary>The installation-wide host account used by write-path tests.</summary>
    /// <remarks>
    /// host authority is re-read from the account store. This identifier is deliberately distinct from
    /// every portal administrator used by the fixture so a host-only exemption cannot be satisfied by an
    /// unrelated tenant-scoped account lookup.
    /// </remarks>
    private const int HostCallerUserId = 9_901;

    /// <summary>
    /// The legacy cache key shape, <c>String.Format(DataCache.PortalCacheKey, PortalId)</c> at
    /// <c>PortalController.vb:L1225</c>, preserved so cache behaviour stays auditable against the original.
    /// </summary>
    private const string PortalCacheKeyFormat = "Portal{0}";

    /// <summary>
    /// The legacy per-entity portal cache timeout in minutes, which the installation-wide performance
    /// multiplier scales.
    /// </summary>
    private const int LegacyPortalCacheTimeOutMinutes = 20;

    /// <summary>The host name the tenant under test is reached by.</summary>
    private const string HostAlias = "tenant.example.test";

    /// <summary>
    /// A deliberately fake stand-in for a stored one-way hash. It is not a credential, cannot be a real
    /// one, and matches no provider's format.
    /// </summary>
    private const string FakePasswordHash = "REDACTED_PASSWORD_HASH";

    /// <summary>A deliberately fake stand-in for a submitted password.</summary>
    private const string FakeSubmittedPassword = "not-a-real-password-value";

    /// <summary>
    /// The twenty-seven arguments of <c>PortalController.UpdatePortalInfo</c>, transcribed from the
    /// signature in the order they are declared there and forwarded at <c>:L1570</c>.
    /// </summary>
    /// <remarks>
    /// This sequence is the whole point of the file. It is transcribed once, in legacy order, so that a
    /// single assertion can prove the request contract still presents the same twenty-seven values in the
    /// same order - which is what makes a reviewer able to check a call site against the legacy procedure
    /// without holding both signatures in their head.
    /// </remarks>
    private static readonly string[] LegacyUpdateArguments =
    [
        "PortalId",
        "PortalName",
        "LogoFile",
        "FooterText",
        "ExpiryDate",
        "UserRegistration",
        "BannerAdvertising",
        "Currency",
        "AdministratorId",
        "HostFee",
        "HostSpace",
        "PageQuota",
        "UserQuota",
        "PaymentProcessor",
        "ProcessorUserId",
        "ProcessorCredentialReference",
        "Description",
        "KeyWords",
        "BackgroundFile",
        "SiteLogHistory",
        "SplashTabId",
        "HomeTabId",
        "LoginTabId",
        "UserTabId",
        "DefaultLanguage",
        "TimeZoneOffset",
        "HomeDirectory",
    ];

    /// <summary>
    /// The fifteen arguments of <c>PortalController.CreatePortal</c>, transcribed from the signature in
    /// declaration order.
    /// </summary>
    private static readonly string[] LegacyCreateArguments =
    [
        "PortalName",
        "FirstName",
        "LastName",
        "Username",
        "Password",
        "Email",
        "Description",
        "KeyWords",
        "TemplatePath",
        "TemplateFile",
        "HomeDirectory",
        "PortalAlias",
        "ServerPath",
        "ChildPath",
        "IsChildPortal",
    ];

    /// <summary>
    /// The pairs of adjacent, same-typed arguments in the legacy twenty-seven-argument signature - the only
    /// places where swapping two values produces code that still compiles and still runs.
    /// </summary>
    /// <returns>The property-name pairs to probe.</returns>
    public static TheoryData<string, string> SameTypedNeighbours() => new()
    {
        { "HostFee", "HostSpace" },
        { "PageQuota", "UserQuota" },
        { "ProcessorUserId", "ProcessorCredentialReference" },
        { "Description", "KeyWords" },
        { "SplashTabId", "HomeTabId" },
        { "HomeTabId", "LoginTabId" },
        { "LoginTabId", "UserTabId" },
    };

    /// <summary>
    /// Every collaborator of <see cref="PortalService"/>, stood up as a mock, together with the recordings
    /// the assertions read back.
    /// </summary>
    private sealed class Subject
    {
        private Subject()
        {
            Service = new PortalService(
                Portals.Object,
                Aliases.Object,
                Tabs.Object,
                Profiles.Object,
                Permissions.Object,
                Users.Object,
                Roles.Object,
                Modules.Object,
                UnitOfWork.Object,
                HostSettings.Object,
                PasswordHasher.Object,
                Tokens.Object,
                Clock.Object,
                Cache.Object,
                CurrentUser.Object,
                Audit.Object,
                PortalContext.Object,
                Caching);
        }

        /// <summary>Gets the service under test.</summary>
        public PortalService Service { get; }

        /// <summary>Gets the portal repository mock.</summary>
        public Mock<IPortalRepository> Portals { get; } = new();

        /// <summary>Gets the alias repository mock.</summary>
        public Mock<IPortalAliasRepository> Aliases { get; } = new();

        /// <summary>Gets the page repository mock.</summary>
        public Mock<ITabRepository> Tabs { get; } = new();

        /// <summary>Gets the profile repository mock.</summary>
        public Mock<IUserProfileRepository> Profiles { get; } = new();

        /// <summary>Gets the permission repository mock.</summary>
        public Mock<IPermissionRepository> Permissions { get; } = new();

        /// <summary>Gets the account repository mock.</summary>
        public Mock<IUserRepository> Users { get; } = new();

        /// <summary>Gets the role repository mock.</summary>
        public Mock<IRoleRepository> Roles { get; } = new();

        /// <summary>Gets the module repository mock, used only by the tenant-removal sweep.</summary>
        public Mock<IModuleRepository> Modules { get; } = new();

        /// <summary>Gets the unit-of-work mock, which owns the single transactional commit point.</summary>
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        /// <summary>Gets the transaction scope the unit of work hands out.</summary>
        public Mock<ITransactionScope> Transaction { get; } = new();

        /// <summary>Gets the installation-settings mock.</summary>
        public Mock<IHostSettingsService> HostSettings { get; } = new();

        /// <summary>Gets the password hasher mock.</summary>
        public Mock<IPasswordHasher> PasswordHasher { get; } = new();

        /// <summary>Gets the token service mock.</summary>
        public Mock<ITokenService> Tokens { get; } = new();

        /// <summary>Gets the clock mock, so no assertion ever reads the real time of day.</summary>
        public Mock<IClock> Clock { get; } = new();

        /// <summary>Gets the cache mock.</summary>
        public Mock<ICacheService> Cache { get; } = new();

        /// <summary>Gets the caller mock.</summary>
        public Mock<ICurrentUser> CurrentUser { get; } = new();

        /// <summary>Gets the audit sink mock.</summary>
        public Mock<IAuditSink> Audit { get; } = new();

        /// <summary>Gets the tenant-context holder mock, which is read but never mutated.</summary>
        public Mock<IPortalContextHolder> PortalContext { get; } = new();

        /// <summary>Gets the bound caching settings, taken as a plain settings object by the service.</summary>
        public CachingOptions Caching { get; } = new();

        /// <summary>Gets or sets the row the portal repository answers a read with.</summary>
        public Portal? StoredPortal { get; set; }

        /// <summary>Gets the portals staged for insertion, in the order they were staged.</summary>
        public List<Portal> StagedPortals { get; } = [];

        /// <summary>Gets the aliases staged for insertion, in the order they were staged.</summary>
        public List<PortalAlias> StagedAliases { get; } = [];

        /// <summary>Gets the roles staged for insertion, in the order they were staged.</summary>
        public List<Role> StagedRoles { get; } = [];

        /// <summary>Gets the audit events the service recorded.</summary>
        public List<AuditEvent> AuditEvents { get; } = [];

        /// <summary>Gets the ordered log of the calls the staging assertions care about.</summary>
        public List<string> CallLog { get; } = [];

        /// <summary>Gets the cache key the service asked for, when it consulted the cache at all.</summary>
        public string? CacheKey { get; private set; }

        /// <summary>Gets the lifetime the service asked the cache to hold an entry for.</summary>
        public TimeSpan? CacheExpiration { get; private set; }

        /// <summary>Gets the portal identifiers whose cache entries were discarded, in order.</summary>
        public List<int> DiscardedPortals { get; } = [];

        /// <summary>Gets the number of times the installation-wide cache was discarded.</summary>
        public int DiscardedHostCount { get; private set; }

        /// <summary>
        /// Builds a subject whose every collaborator answers plausibly, so that a test need only override
        /// the one fact it is about.
        /// </summary>
        /// <returns>A ready subject.</returns>
        public static Subject Ready()
        {
            var subject = new Subject();
            subject.StoredPortal = StoredRow();

            subject.Clock.SetupGet(clock => clock.UtcNow)
                .Returns(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            subject.CurrentUser.SetupGet(caller => caller.IsSuperUser).Returns(true);
            subject.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
            subject.CurrentUser.SetupGet(caller => caller.UserId).Returns(HostCallerUserId);

            subject.PasswordHasher.Setup(hasher => hasher.Hash(It.IsAny<string>())).Returns(FakePasswordHash);

            subject.PortalContext.SetupGet(holder => holder.IsResolved).Returns(false);

            subject.UnitOfWork
                .Setup(work => work.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(subject.Transaction.Object);
            subject.UnitOfWork
                .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1)
                .Callback(() => subject.CallLog.Add("commit"));

            subject.HostSettings
                .Setup(settings => settings.GetSettingsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            subject.Portals
                .Setup(portals => portals.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => subject.StoredPortal);
            subject.Portals
                .Setup(portals => portals.GetRoleNamesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<int, string>());
            subject.Portals
                .Setup(portals => portals.TabBelongsToPortalAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            subject.Portals
                .Setup(portals => portals.CountUsersForPortalsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<int, int>());
            subject.Portals
                .Setup(portals => portals.CountPagesForPortalsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<int, int>());
            subject.Portals
                .Setup(portals => portals.AddAsync(It.IsAny<Portal>(), It.IsAny<CancellationToken>()))
                .Callback<Portal, CancellationToken>((portal, _) =>
                {
                    subject.StagedPortals.Add(portal);
                    subject.CallLog.Add("stage:portal");
                })
                .Returns(Task.CompletedTask);

            subject.Aliases
                .Setup(aliases => aliases.AliasExistsAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            subject.Aliases
                .Setup(aliases => aliases.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<PortalAlias>());
            subject.Aliases
                .Setup(aliases => aliases.GetByPortalIdsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<PortalAlias>());
            subject.Aliases
                .Setup(aliases => aliases.AddAsync(It.IsAny<PortalAlias>(), It.IsAny<CancellationToken>()))
                .Callback<PortalAlias, CancellationToken>((alias, _) =>
                {
                    subject.StagedAliases.Add(alias);
                    subject.CallLog.Add("stage:alias");
                })
                .Returns(Task.CompletedTask);

            subject.Roles
                .Setup(roles => roles.AddAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Callback<Role, CancellationToken>((role, _) =>
                {
                    subject.StagedRoles.Add(role);
                    subject.CallLog.Add("stage:role");
                })
                .Returns(Task.CompletedTask);

            subject.Users
                .Setup(users => users.UsernameExistsAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            subject.Users
                .Setup(users => users.GetAsync(
                    It.Is<int?>(portalId => portalId == null),
                    HostCallerUserId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new User
                {
                    UserId = HostCallerUserId,
                    Username = "host-caller",
                    IsSuperUser = true,
                });
            subject.Users
                .Setup(users => users.GetMembershipAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, int userId, CancellationToken _) => new UserPortal
                {
                    PortalId = portalId,
                    UserId = userId,
                });
            subject.Users
                .Setup(users => users.CreateCredentialAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            subject.Users
                .Setup(users => users.ListPortalMembersForRemovalAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<User>());
            subject.Users
                .Setup(users => users.Add(It.IsAny<User>()))
                .Callback<User>(_ => subject.CallLog.Add("stage:user"));

            subject.Tokens
                .Setup(tokens => tokens.RevokeAllRefreshTokensAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            // PRIV-02. Tenant-scoped erasure, which portal removal performs after its commit.
            subject.Tokens
                .Setup(tokens => tokens.PurgePortalSessionRecordsAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            subject.Permissions
                .Setup(permissions => permissions.GetByTabIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Permission>());
            subject.Permissions
                .Setup(permissions => permissions.GetByCodeAndKeyAsync(
                    It.IsAny<string>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string code, PermissionKey key, CancellationToken _) => new[]
                {
                    new Permission
                    {
                        PermissionId = key == PermissionKey.VIEW ? 3 : 4,
                        PermissionCode = code,
                        PermissionKey = key.ToString(),
                        PermissionName = key == PermissionKey.VIEW ? "View Tab" : "Edit Tab",
                    },
                });

            subject.Tabs
                .Setup(tabs => tabs.AddAsync(It.IsAny<Tab>(), It.IsAny<CancellationToken>()))
                .Callback<Tab, CancellationToken>((_, _) => subject.CallLog.Add("stage:page"))
                .Returns(Task.CompletedTask);

            subject.Cache
                .Setup(cache => cache.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<PortalDetailDto?>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, Func<CancellationToken, Task<PortalDetailDto?>>, TimeSpan, CancellationToken>(
                    (key, factory, expiration, token) =>
                    {
                        subject.CacheKey = key;
                        subject.CacheExpiration = expiration;
                        return factory(token);
                    });
            subject.Cache
                .Setup(cache => cache.InvalidatePortal(It.IsAny<int>()))
                .Callback<int>(portalId => subject.DiscardedPortals.Add(portalId));
            subject.Cache
                .Setup(cache => cache.InvalidateHost())
                .Callback(() => subject.DiscardedHostCount++);

            subject.Audit
                .Setup(audit => audit.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(recorded => subject.AuditEvents.Add(recorded));

            return subject;
        }
    }

    /// <summary>
    /// A stored tenant row shaped so that the update guards pass and every column carries a value distinct
    /// from the ones a request will write over it.
    /// </summary>
    /// <returns>The stored row.</returns>
    private static Portal StoredRow() => new()
    {
        PortalId = SeedPortalId,
        PortalName = "stored-name",
        DefaultLanguage = "stored-language",
        HomeDirectory = "stored-home-directory",
        AdministratorId = 1_001,
        HostFee = 1m,
        HostSpace = 2,
        PageQuota = 3,
        UserQuota = 4,
        SiteLogHistory = 5,
        TimeZoneOffset = 6,
    };

    /// <summary>
    /// A modification request in which every one of the twenty-seven values is distinct, and in which each
    /// numeric value equals its own legacy argument position.
    /// </summary>
    /// <returns>The request.</returns>
    private static UpdatePortalRequest SentinelUpdateRequest() => new()
    {
        PortalId = SeedPortalId,
        PortalName = "argument-02-portal-name",
        LogoFile = "argument-03-logo-file",
        FooterText = "argument-04-footer-text",
        ExpiryDate = new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        UserRegistration = UserRegistrationMode.VerifiedRegistration,
        BannerAdvertising = BannerAdvertisingMode.Host,
        Currency = "argument-08-currency",
        AdministratorId = 9,
        HostFee = 10m,
        HostSpace = 11,
        PageQuota = 12,
        UserQuota = 13,
        PaymentProcessor = "argument-14-payment-processor",
        ProcessorUserId = "argument-15-processor-user-id",
        ProcessorCredentialReference = "secret://processor/argument-16",
        Description = "argument-17-description",
        KeyWords = "argument-18-keywords",
        BackgroundFile = "argument-19-background-file",
        SiteLogHistory = 20,
        SplashTabId = 21,
        HomeTabId = 22,
        LoginTabId = 23,
        UserTabId = 24,
        DefaultLanguage = "argument-25-default-language",
        TimeZoneOffset = 26,
        HomeDirectory = "argument-27-home-directory",
    };

    /// <summary>
    /// Reads one property off an object by name, failing the test rather than returning nothing when the
    /// property does not exist.
    /// </summary>
    /// <param name="instance">The object to read.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <returns>The value the property holds.</returns>
    private static object? ReadProperty(object instance, string propertyName)
    {
        PropertyInfo? property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull(
            "{0} must declare a property named {1}",
            instance.GetType().Name,
            propertyName);

        return property!.GetValue(instance);
    }

    /// <summary>
    /// Reduces a value to a form two differently-typed properties can be compared in, so that a nullable
    /// term on a request and a non-nullable column on an entity compare equal when they hold the same
    /// value.
    /// </summary>
    /// <param name="value">The value to reduce.</param>
    /// <returns>A decimal for any numeric value, the value itself otherwise.</returns>
    private static object? Normalise(object? value) => value switch
    {
        null => null,
        decimal amount => amount,
        int count => (decimal)count,
        long count => (decimal)count,
        double amount => (decimal)amount,
        float amount => (decimal)amount,
        _ => value,
    };

    /// <summary>
    /// The request contract opens with the twenty-seven legacy arguments in the legacy order, and the only
    /// member that follows them is the optimistic-concurrency token.
    /// </summary>
    /// <remarks>
    /// ONE MEMBER HAS BEEN ADDED SINCE, DELIBERATELY, AND THIS ASSERTION PINS BOTH ITS IDENTITY AND ITS
    /// POSITION. <c>ConcurrencyToken</c> describes no portal attribute - it states WHICH revision of the
    /// record the caller read, so a stale whole-record replace is refused with
    /// <c>portal.concurrency_conflict</c> instead of silently destroying another operator's committed edit
    /// to a field neither operator had opened.
    /// </remarks>
    [Fact]
    public void UpdateRequest_PresentsTheTwentySevenLegacyArgumentsInTheirLegacyOrderThenTheConcurrencyToken()
    {
        IReadOnlyList<string> declared = typeof(UpdatePortalRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.Name)
            .ToList();

        declared.Take(LegacyUpdateArguments.Length).Should().Equal(
            LegacyUpdateArguments,
            "the twenty-seven positional arguments of UpdatePortalInfo (PortalController.vb:L1568) must "
            + "still be presented first and in their legacy order so a call site stays checkable against "
            + "the original");

        declared.Skip(LegacyUpdateArguments.Length).Should().Equal(
            ["ConcurrencyToken"],
            "the only member added to the legacy argument list states which revision the caller read, and "
            + "it sits after the legacy run so the legacy run stays contiguous");
    }

    /// <summary>
    /// Every one of the twenty-six writable arguments lands on its own column, and the subject identifier
    /// lands on none.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_LandsEachLegacyArgumentOnItsOwnColumn()
    {
        Subject subject = Subject.Ready();

        Result<PortalDetailDto?> outcome = await subject.Service.UpdatePortalAsync(
            SeedPortalId,
            SentinelUpdateRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        Portal stored = subject.StoredPortal!;

        // Argument 1 is the SUBJECT of the write, not a column of it. The route names the tenant and the
        // service addresses that tenant and no other, so the value must remain what the store issued.
        stored.PortalId.Should().Be(SeedPortalId);

        stored.PortalName.Should().Be("argument-02-portal-name");
        stored.LogoFile.Should().Be("argument-03-logo-file");
        stored.FooterText.Should().Be("argument-04-footer-text");
        stored.ExpiryDate.Should().Be(new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        stored.UserRegistration.Should().Be(UserRegistrationMode.VerifiedRegistration);
        stored.BannerAdvertising.Should().Be(BannerAdvertisingMode.Host);
        stored.Currency.Should().Be("argument-08-currency");
        stored.AdministratorId.Should().Be(9);
        stored.HostFee.Should().Be(10m);
        stored.HostSpace.Should().Be(11);
        stored.PageQuota.Should().Be(12);
        stored.UserQuota.Should().Be(13);
        stored.PaymentProcessor.Should().Be("argument-14-payment-processor");
        stored.ProcessorUserId.Should().Be("argument-15-processor-user-id");
        stored.ProcessorCredentialReference.Should().Be("secret://processor/argument-16");
        stored.Description.Should().Be("argument-17-description");
        stored.KeyWords.Should().Be("argument-18-keywords");
        stored.BackgroundFile.Should().Be("argument-19-background-file");
        stored.SiteLogHistory.Should().Be(20);
        stored.SplashTabId.Should().Be(21);
        stored.HomeTabId.Should().Be(22);
        stored.LoginTabId.Should().Be(23);
        stored.UserTabId.Should().Be(24);
        stored.DefaultLanguage.Should().Be("argument-25-default-language");
        stored.TimeZoneOffset.Should().Be(26);
        stored.HomeDirectory.Should().Be("argument-27-home-directory");

        // The write reaches the store exactly once, through the one transactional commit point.
        subject.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Neither member of any same-typed neighbouring pair takes the other's value.</summary>
    /// <param name="first">The earlier argument of the pair.</param>
    /// <param name="second">The later argument of the pair.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A per-pair restatement of the assertion above, and it exists for the diagnosis rather than for the
    /// coverage: when a swap happens, this theory fails naming the two arguments involved, which is the
    /// information a reader needs first.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SameTypedNeighbours))]
    public async Task UpdatePortal_KeepsSameTypedNeighboursApart(string first, string second)
    {
        Subject subject = Subject.Ready();
        UpdatePortalRequest request = SentinelUpdateRequest();

        await subject.Service.UpdatePortalAsync(SeedPortalId, request, CancellationToken.None);

        Portal stored = subject.StoredPortal!;

        object? submittedFirst = Normalise(ReadProperty(request, first));
        object? submittedSecond = Normalise(ReadProperty(request, second));
        object? storedFirst = Normalise(ReadProperty(stored, first));
        object? storedSecond = Normalise(ReadProperty(stored, second));

        submittedFirst.Should().NotBe(
            submittedSecond,
            "the sentinels for {0} and {1} must differ, or this probe cannot detect a swap between them",
            first,
            second);

        storedFirst.Should().Be(
            submittedFirst,
            "{0} must be stored in {0} and not in {1}",
            first,
            second);
        storedSecond.Should().Be(
            submittedSecond,
            "{0} must be stored in {0} and not in {1}",
            second,
            first);
    }

    /// <summary>
    /// A modification that would leave the tenant with no administrator is refused, and refusing it reaches
    /// the store not at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The counterpart to the commit assertion above, and the reason both belong in one file: a guard that
    /// throws after the row has already been altered in memory is only safe while nothing commits behind
    /// it. The refusal is an expected request failure with a stable code, so the API can return a bounded
    /// 400.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_CommitsNothingWhenItRefusesToClearTheAdministrator()
    {
        Subject subject = Subject.Ready();
        UpdatePortalRequest request = SentinelUpdateRequest();
        request.AdministratorId = null;

        Result<PortalDetailDto?> outcome = await subject.Service.UpdatePortalAsync(
            SeedPortalId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("portal.administrator_invalid");

        subject.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        subject.DiscardedPortals.Should().BeEmpty(
            "a refused write must not discard a cache entry that still describes the stored row");
    }

    /// <summary>A designated administrator must be a member of the addressed portal.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_RefusesAnAdministratorFromAnotherPortal()
    {
        Subject subject = Subject.Ready();
        UpdatePortalRequest request = SentinelUpdateRequest();
        request.AdministratorId = 9_999;
        subject.Users
            .Setup(users => users.GetMembershipAsync(
                SeedPortalId,
                9_999,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserPortal?)null);

        Result<PortalDetailDto?> outcome = await subject.Service.UpdatePortalAsync(
            SeedPortalId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("portal.administrator_invalid");
        subject.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A designation the caller merely ECHOES BACK is admitted even when the account holds no membership row
    /// for the addressed portal, and the ownership rule is not asked at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Measured on this installation before the split: PortalIDs 0 through 5 each designate one account that
    /// carries no matching UserPortals row, so opening Site Settings as host, changing nothing and pressing
    /// Update was refused 400 portal.administrator_invalid - and so was PUTting back the exact body the GET had
    /// just returned. No submission could satisfy the rule short of altering data the caller never asked to
    /// touch, which left those six tenants unmaintainable through this API on a field they did not change. The
    /// Times.Never assertion is the load-bearing half: admitting the value by re-reading membership and
    /// tolerating its absence would pass this test while still charging every unchanged save a round trip.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_UnchangedAdministratorWithoutMembership_IsAdmittedWithoutAskingTheRule()
    {
        Subject subject = Subject.Ready();
        UpdatePortalRequest request = SentinelUpdateRequest();
        request.AdministratorId = subject.StoredPortal!.AdministratorId;
        subject.Users
            .Setup(users => users.GetMembershipAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserPortal?)null);

        Result<PortalDetailDto?> outcome = await subject.Service.UpdatePortalAsync(
            SeedPortalId,
            request,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "a submission equal to the stored designation authors nothing and is admitted on the strength of already being there");
        subject.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        subject.Users.Verify(
            users => users.GetMembershipAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A designation the caller CHANGES is still tested, so grandfathering history admits no new breakage.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_ChangedAdministratorWithoutMembership_IsStillRefused()
    {
        Subject subject = Subject.Ready();
        UpdatePortalRequest request = SentinelUpdateRequest();
        request.AdministratorId = subject.StoredPortal!.AdministratorId + 1;
        subject.Users
            .Setup(users => users.GetMembershipAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserPortal?)null);

        Result<PortalDetailDto?> outcome = await subject.Service.UpdatePortalAsync(
            SeedPortalId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("portal.administrator_invalid");
        subject.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Each submitted page reference must belong to the addressed portal.</summary>
    /// <param name="field">The reference to make foreign.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(nameof(UpdatePortalRequest.SplashTabId))]
    [InlineData(nameof(UpdatePortalRequest.HomeTabId))]
    [InlineData(nameof(UpdatePortalRequest.LoginTabId))]
    [InlineData(nameof(UpdatePortalRequest.UserTabId))]
    public async Task UpdatePortal_RefusesAPageFromAnotherPortal(string field)
    {
        Subject subject = Subject.Ready();
        UpdatePortalRequest request = SentinelUpdateRequest();
        typeof(UpdatePortalRequest).GetProperty(field)!.SetValue(request, 9_999);
        subject.Portals
            .Setup(portals => portals.TabBelongsToPortalAsync(
                SeedPortalId,
                9_999,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result<PortalDetailDto?> outcome = await subject.Service.UpdatePortalAsync(
            SeedPortalId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("portal.tab_reference_invalid");
        subject.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A legacy plaintext processor credential cannot be retained silently; the caller must clear it or
    /// replace it with a managed-secret reference.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_RequiresLegacyProcessorPlaintextToBeRemediated()
    {
        Subject refusing = Subject.Ready();
        refusing.StoredPortal!.ProcessorCredentialReference = "legacy-plaintext-password";
        UpdatePortalRequest keep = SentinelUpdateRequest();
        keep.ProcessorCredentialReference = null;

        Result<PortalDetailDto?> refused = await refusing.Service.UpdatePortalAsync(
            SeedPortalId,
            keep,
            CancellationToken.None);

        refused.IsFailure.Should().BeTrue();
        refused.Error!.Code.Should().Be("portal.processor_reference_invalid");
        refusing.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        Subject clearing = Subject.Ready();
        clearing.StoredPortal!.ProcessorCredentialReference = "legacy-plaintext-password";
        UpdatePortalRequest clear = SentinelUpdateRequest();
        clear.ProcessorCredentialReference = string.Empty;

        Result<PortalDetailDto?> cleared = await clearing.Service.UpdatePortalAsync(
            SeedPortalId,
            clear,
            CancellationToken.None);

        cleared.IsSuccess.Should().BeTrue();
        clearing.StoredPortal.ProcessorCredentialReference.Should().BeNull();
    }

    /// <summary>Service callers cannot bypass the managed-secret reference syntax enforced at the API edge.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_RefusesAPlaintextProcessorCredentialFromDirectCallers()
    {
        Subject subject = Subject.Ready();
        UpdatePortalRequest request = SentinelUpdateRequest();
        request.ProcessorCredentialReference = "plaintext-password";

        Result<PortalDetailDto?> outcome = await subject.Service.UpdatePortalAsync(
            SeedPortalId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("portal.processor_reference_invalid");
        subject.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The creation contract carries every legacy argument that survives the migration, under its new name,
    /// and carries none of the three that do not.
    /// </summary>
    [Fact]
    public void CreateRequest_RenamesTheFiveAdministratorArgumentsAndDropsTheThreePathArguments()
    {
        var mapping = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PortalName"] = "PortalName",
            ["FirstName"] = "AdministratorFirstName",
            ["LastName"] = "AdministratorLastName",
            ["Username"] = "AdministratorUsername",
            ["Password"] = "AdministratorPassword",
            ["Email"] = "AdministratorEmail",
            ["Description"] = "Description",
            ["KeyWords"] = "KeyWords",
            ["TemplatePath"] = null,
            ["TemplateFile"] = "TemplateFile",
            ["HomeDirectory"] = "HomeDirectory",
            ["PortalAlias"] = "PortalAlias",
            ["ServerPath"] = null,
            ["ChildPath"] = null,
            ["IsChildPortal"] = "IsChildPortal",
        };

        mapping.Keys.Should().Equal(
            LegacyCreateArguments,
            "the mapping must account for all fifteen legacy arguments, in their legacy order");

        Type contract = typeof(CreatePortalRequest);

        foreach ((string legacyArgument, string? targetProperty) in mapping)
        {
            if (targetProperty is null)
            {
                contract.GetProperty(legacyArgument, BindingFlags.Public | BindingFlags.Instance)
                    .Should().BeNull(
                        "legacy argument {0} names a file-system location, which is beyond the migrated "
                        + "scope and must not reappear on the request",
                        legacyArgument);
                continue;
            }

            contract.GetProperty(targetProperty, BindingFlags.Public | BindingFlags.Instance)
                .Should().NotBeNull(
                    "legacy argument {0} survives as {1}",
                    legacyArgument,
                    targetProperty);
        }

        contract.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().HaveCount(
                12,
                "twelve of the fifteen legacy arguments survive, and the contract must carry nothing else");
    }

    /// <summary>
    /// Every table the legacy creation sequence wrote is staged before anything is flushed, and the whole
    /// of it is enclosed by a single transaction.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy sequence wrote the tenant, its host name, its roles, its pages and its modules through
    /// separate statements that shared no transaction, so a failure part-way through left a half-built
    /// tenant behind - which is why the legacy body accumulated a message string as it went rather than
    /// reporting success or failure outright.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_StagesEveryTableBeforeItFlushesAndEnclosesItInOneTransaction()
    {
        Subject subject = Subject.Ready();

        await subject.Service.CreatePortalAsync(SentinelCreateRequest(), CancellationToken.None);

        int firstFlush = subject.CallLog.IndexOf("commit");
        firstFlush.Should().BePositive("staging must precede the first flush");

        IReadOnlyList<string> staged = subject.CallLog.Take(firstFlush).ToList();
        staged.Should().Contain("stage:portal");
        staged.Should().Contain("stage:alias");
        staged.Should().Contain("stage:user");
        staged.Count(entry => entry == "stage:role").Should().Be(
            3,
            "the three stock roles are staged with the tenant, not written after it");

        subject.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        subject.UnitOfWork.Verify(
            work => work.BeginTransactionAsync(
                TransactionIsolation.Default,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>A creation refused before any work is staged opens no transaction and flushes nothing.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The duplicate-host-name check runs before the transaction opens, which is deliberate: refusing a
    /// request should not cost a transaction. Asserting the absence of both the flush and the transaction
    /// is what pins that ordering, since a later refactor could satisfy the refusal while moving the check
    /// inside a transaction nobody noticed was being opened.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_OpensNoTransactionWhenTheHostNameIsAlreadyBound()
    {
        Subject subject = Subject.Ready();
        subject.Aliases
            .Setup(aliases => aliases.AliasExistsAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Result<PortalDetailDto> outcome = await subject.Service.CreatePortalAsync(
            SentinelCreateRequest(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("portal.alias_duplicate");

        subject.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        subject.UnitOfWork.Verify(
            work => work.BeginTransactionAsync(
                It.IsAny<TransactionIsolation>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        subject.StagedPortals.Should().BeEmpty();
    }

    /// <summary>
    /// A creation request whose every value is distinct, so that a value landing in the wrong place is
    /// visible.
    /// </summary>
    /// <returns>The request.</returns>
    private static CreatePortalRequest SentinelCreateRequest() => new()
    {
        PortalName = "create-argument-01-portal-name",
        AdministratorFirstName = "create-argument-02-first-name",
        AdministratorLastName = "create-argument-03-last-name",
        AdministratorUsername = "create-argument-04-username",
        AdministratorPassword = FakeSubmittedPassword,
        AdministratorEmail = "create-argument-06@example.test",
        Description = "create-argument-07-description",
        KeyWords = "create-argument-08-keywords",
        TemplateFile = "create-argument-10-template-file",
        HomeDirectory = "create-argument-11-home-directory",
        PortalAlias = HostAlias,
        IsChildPortal = false,
    };

    /// <summary>
    /// The clamp that replaced the legacy two-armed conditional floors a fee at nothing and is exactly
    /// equivalent to the idiomatic replacement.
    /// </summary>
    /// <param name="supplied">
    /// The submitted fee, as invariant text because a decimal cannot be an attribute constant.
    /// </param>
    /// <param name="expected">The fee that must be stored.</param>
    [Theory]
    [InlineData("-79228162514264337593543950335", "0")]
    [InlineData("-1000000", "0")]
    [InlineData("-19.99", "0")]
    [InlineData("-0.01", "0")]
    [InlineData("0", "0")]
    [InlineData("0.01", "0.01")]
    [InlineData("19.99", "19.99")]
    [InlineData("1000000", "1000000")]
    public void FeeClamp_FloorsAtNothingAndAgreesWithTheIdiomaticReplacement(string supplied, string expected)
    {
        decimal fee = decimal.Parse(supplied, CultureInfo.InvariantCulture);
        decimal floored = decimal.Parse(expected, CultureInfo.InvariantCulture);

        PortalMappings.ClampFee(fee).Should().Be(floored);
        PortalMappings.ClampFee(fee).Should().Be(
            Math.Max(fee, 0m),
            "the two-armed legacy conditional and the clamp that replaced it must agree at every value");
    }

    /// <summary>
    /// The subscription fee and the trial fee are clamped as two independent columns, so neither can be
    /// floored from the other's value.
    /// </summary>
    /// <param name="suppliedServiceFee">The submitted subscription fee, as invariant text.</param>
    /// <param name="suppliedTrialFee">The submitted trial fee, as invariant text.</param>
    /// <param name="expectedServiceFee">The subscription fee that must be stored.</param>
    /// <param name="expectedTrialFee">The trial fee that must be stored.</param>
    /// <remarks>
    /// The ASYMMETRIC rows below are the ones that carry the weight. When only one of the two fees is
    /// negative, a clamp fed from the wrong argument floors the wrong column and stores the other one
    /// unchanged - and those two rows fail while the symmetric rows would still pass.
    /// </remarks>
    [Theory]
    [InlineData("-19.99", "-1.99", "0", "0")]
    [InlineData("-19.99", "4.99", "0", "4.99")]
    [InlineData("19.99", "-1.99", "19.99", "0")]
    [InlineData("0", "0", "0", "0")]
    [InlineData("19.99", "4.99", "19.99", "4.99")]
    public void FeeClamp_TreatsTheSubscriptionAndTrialFeesAsSeparateColumns(
        string suppliedServiceFee,
        string suppliedTrialFee,
        string expectedServiceFee,
        string expectedTrialFee)
    {
        decimal serviceFee = decimal.Parse(suppliedServiceFee, CultureInfo.InvariantCulture);
        decimal trialFee = decimal.Parse(suppliedTrialFee, CultureInfo.InvariantCulture);

        var role = new Role
        {
            RoleName = "fee-clamp-probe",
            ServiceFee = PortalMappings.ClampFee(serviceFee),
            TrialFee = PortalMappings.ClampFee(trialFee),
        };

        role.ServiceFee.Should().Be(
            decimal.Parse(expectedServiceFee, CultureInfo.InvariantCulture),
            "the subscription fee is floored from the subscription fee, not from the trial fee");
        role.TrialFee.Should().Be(
            decimal.Parse(expectedTrialFee, CultureInfo.InvariantCulture),
            "the trial fee is floored from the trial fee, not from the subscription fee");
    }

    /// <summary>
    /// Both fee columns are floored on every stock role a new tenant receives, and the legacy
    /// absent-integer marker on the role group becomes genuine absence.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: the role group is where a sentinel becomes absence. The legacy line assigned it the
    /// absent-integer marker, which in this schema is -1 - and -1 is also a legitimate role-group key, so
    /// the legacy value could not distinguish "no group" from "group -1".
    /// </remarks>
    [Fact]
    public async Task CreatePortal_FloorsBothFeesOnEveryStockRoleAndLeavesTheRoleGroupAbsent()
    {
        Subject subject = Subject.Ready();

        await subject.Service.CreatePortalAsync(SentinelCreateRequest(), CancellationToken.None);

        subject.StagedRoles.Should().HaveCount(3);

        foreach (Role role in subject.StagedRoles)
        {
            role.ServiceFee.Should().Be(
                0m,
                "the subscription fee of the stock role {0} is floored at nothing",
                role.RoleName);
            role.TrialFee.Should().Be(
                0m,
                "the trial fee of the stock role {0} is floored at nothing, and is a different column from "
                + "the subscription fee",
                role.RoleName);
            role.RoleGroupId.Should().BeNull(
                "the legacy absent-integer marker on {0}'s role group becomes genuine absence",
                role.RoleName);
        }
    }

    /// <summary>
    /// Each stock role is created once, and the legacy create-if-absent lookup is not performed at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The reuse arm is UNREACHABLE for a tenant being created. A role is owned by a portal, and this
    /// portal does not exist until this transaction commits, so no role can already be bound to it and the
    /// lookup can only ever answer "nothing".
    /// </remarks>
    [Fact]
    public async Task CreatePortal_CreatesEachStockRoleOnceWithoutTheLegacyExistenceLookup()
    {
        Subject subject = Subject.Ready();

        await subject.Service.CreatePortalAsync(SentinelCreateRequest(), CancellationToken.None);

        subject.StagedRoles.Select(role => role.RoleName).Should().OnlyHaveUniqueItems(
            "each stock role is inserted once, which is the create arm of the legacy guard");

        subject.Roles.Verify(
            roles => roles.GetByNameAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The listing carries its records and its grand total on ONE value, and omitting the name filter
    /// reproduces the legacy reader that returned everything.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_CarriesTheRecordsAndTheGrandTotalOnOneTypedValue()
    {
        Subject subject = Subject.Ready();
        subject.Portals
            .Setup(portals => portals.ListAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedResult<Portal>.Create(
                [StoredRow()],
                totalCount: 57,
                pageIndex: 2,
                pageSize: 1));

        Result<PagedResult<PortalListItemDto>> outcome = await subject.Service.ListPortalsAsync(
            new PagedRequest { PageIndex = 2, PageSize = 1 },
            nameFilter: null,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        PagedResult<PortalListItemDto> page = outcome.Value;
        page.Items.Should().ContainSingle().Which.PortalId.Should().Be(SeedPortalId);
        page.TotalCount.Should().Be(57, "the grand total travels with the records rather than beside them");
        page.PageIndex.Should().Be(2);
        page.PageSize.Should().Be(1);

        // Omitting the filter is what reproduces the unfiltered legacy reader, so nothing narrowing may be
        // invented on the caller's behalf.
        subject.Portals.Verify(
            portals => portals.ListAsync(
                2,
                1,
                null,
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>A listing that matches nothing is an empty page and a success, not a failure.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_ReportsAnEmptyPageAsASuccess()
    {
        Subject subject = Subject.Ready();
        subject.Portals
            .Setup(portals => portals.ListAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedResult<Portal>.Create([], totalCount: 0, pageIndex: 0, pageSize: 10));

        Result<PagedResult<PortalListItemDto>> outcome = await subject.Service.ListPortalsAsync(
            new PagedRequest { PageIndex = 0, PageSize = 10 },
            nameFilter: "matches-nothing",
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Items.Should().BeEmpty();
        outcome.Value.TotalCount.Should().Be(0);
    }

    /// <summary>
    /// Nothing on the listing payload is an untyped collection, and the element type of every sequence is
    /// declared.
    /// </summary>
    [Fact]
    public void ListingPayload_DeclaresTheElementTypeOfEverySequence()
    {
        IEnumerable<PropertyInfo> sequences = typeof(PagedResult<PortalListItemDto>)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(PortalListItemDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property.PropertyType != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType));

        sequences.Should().NotBeEmpty("the payload does carry sequences, so the check is meaningful");

        foreach (PropertyInfo sequence in sequences)
        {
            sequence.PropertyType.IsGenericType.Should().BeTrue(
                "{0}.{1} must declare its element type rather than leaving a caller to cast",
                sequence.DeclaringType!.Name,
                sequence.Name);
            sequence.PropertyType.GetGenericArguments().Should().ContainSingle();
        }
    }

    /// <summary>
    /// The contract measures no disc consumption, while the three stored allowances such a measurement
    /// would be compared against survive.
    /// </summary>
    /// <remarks>
    /// What is asserted instead is the boundary of the omission. The measurement is dropped; the stored
    /// ALLOWANCES are not.
    /// </remarks>
    [Fact]
    public void PortalContract_MeasuresNoDiscConsumptionButKeepsTheStoredAllowances()
    {
        string[] retired = ["GetPortalSpaceUsed", "GetPortalSpaceUsedBytes", "HasSpaceAvailable"];

        IReadOnlyList<string> members = typeof(IPortalService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToList();

        foreach (string legacyMember in retired)
        {
            members.Should().NotContain(
                legacyMember,
                "{0} measured files on disc, which is beyond the migrated scope",
                legacyMember);
            members.Should().NotContain(
                legacyMember + "Async",
                "{0} must not reappear under an asynchronous name either",
                legacyMember);
        }

        foreach (string allowance in new[] { "HostSpace", "PageQuota", "UserQuota" })
        {
            typeof(UpdatePortalRequest).GetProperty(allowance, BindingFlags.Public | BindingFlags.Instance)
                .Should().NotBeNull("the stored allowance {0} remains writable", allowance);
            typeof(PortalDetailDto).GetProperty(allowance, BindingFlags.Public | BindingFlags.Instance)
                .Should().NotBeNull("the stored allowance {0} remains readable", allowance);
        }
    }

    /// <summary>
    /// Every member of the contract is asynchronous and takes neither an output nor a by-reference
    /// argument.
    /// </summary>
    /// <remarks>
    /// The legacy surface reported a secondary answer by mutating an argument the caller passed in - the
    /// tenant listing returned its grand total that way, and thirty such signatures were counted across the
    /// migrated trees.
    /// </remarks>
    [Fact]
    public void PortalContract_IsAsynchronousThroughoutAndMutatesNoArgument()
    {
        MethodInfo[] members = typeof(IPortalService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        members.Should().NotBeEmpty();

        foreach (MethodInfo member in members)
        {
            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue(
                "{0} must be asynchronous so that cancellation can be honoured on every path",
                member.Name);

            foreach (ParameterInfo argument in member.GetParameters())
            {
                argument.IsOut.Should().BeFalse(
                    "{0} must report its answer as a return value, not by filling in {1}",
                    member.Name,
                    argument.Name);
                argument.ParameterType.IsByRef.Should().BeFalse(
                    "{0} must not mutate the argument {1} to report a secondary answer",
                    member.Name,
                    argument.Name);
            }

            member.GetParameters().Should().Contain(
                argument => argument.ParameterType == typeof(CancellationToken),
                "{0} performs input or output and must accept a cancellation token",
                member.Name);
        }
    }

    /// <summary>
    /// The cache lifetime is the legacy arithmetic - a per-entity base scaled by the installation-wide
    /// multiplier - and the key keeps the legacy shape.
    /// </summary>
    /// <param name="multiplier">The configured installation-wide multiplier.</param>
    /// <param name="expectedMinutes">The lifetime that arithmetic must yield.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 40)]
    [InlineData(3, 60)]
    [InlineData(20, 400)]
    [InlineData(1440, 28_800)]
    public async Task GetPortal_DerivesTheCacheLifetimeFromTheLegacyArithmetic(
        int multiplier,
        int expectedMinutes)
    {
        Subject subject = Subject.Ready();
        subject.Caching.PerformanceMultiplier = multiplier;

        Result<PortalDetailDto?> outcome = await subject.Service.GetPortalAsync(
            SeedPortalId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        subject.CacheKey.Should().Be(string.Format(
            CultureInfo.InvariantCulture,
            PortalCacheKeyFormat,
            SeedPortalId));
        subject.CacheExpiration.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
        subject.CacheExpiration.Should().Be(
            TimeSpan.FromMinutes(LegacyPortalCacheTimeOutMinutes * multiplier),
            "the lifetime must remain the legacy base scaled by the legacy multiplier");
    }

    /// <summary>
    /// A multiplier that cannot yield a usable lifetime bypasses the cache instead of storing an entry that
    /// has already expired.
    /// </summary>
    /// <param name="multiplier">The configured multiplier.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetPortal_BypassesTheCacheWhenTheArithmeticYieldsNoUsableLifetime(int multiplier)
    {
        Subject subject = Subject.Ready();
        subject.Caching.PerformanceMultiplier = multiplier;

        Result<PortalDetailDto?> outcome = await subject.Service.GetPortalAsync(
            SeedPortalId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().NotBeNull("a bypassed cache still reads through to the store");
        subject.CacheKey.Should().BeNull("the cache is not consulted at all");
        subject.CacheExpiration.Should().BeNull();
    }

    /// <summary>
    /// Every identifier reaches the store exactly as it was given, including the two values the legacy
    /// contract could not tell apart from absence.
    /// </summary>
    /// <param name="portalId">The identifier to address.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The rule this pins is therefore narrow and absolute: no value is special. Every integer is
    /// forwarded, none is coerced to absence, and none short-circuits the read.
    /// </remarks>
    [Theory]
    [InlineData(SeedPortalId)]
    [InlineData(DefaultPortalId)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task GetPortal_ForwardsEveryIdentifierVerbatim(int portalId)
    {
        Subject subject = Subject.Ready();
        subject.StoredPortal = null;

        Result<PortalDetailDto?> outcome = await subject.Service.GetPortalAsync(
            portalId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("a tenant that is not there is an ordinary answer, not a failure");
        outcome.Value.Should().BeNull();

        subject.Portals.Verify(
            portals => portals.GetByIdAsync(portalId, It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
        subject.CacheKey.Should().Be(string.Format(
            CultureInfo.InvariantCulture,
            PortalCacheKeyFormat,
            portalId));
    }

    /// <summary>
    /// A submitted empty string is stored as an empty string rather than being turned into absence.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy null contract defined its absent-string marker as the EMPTY STRING rather than as
    /// nothing, so a database null and an empty string were indistinguishable once read through the legacy
    /// path.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_KeepsAnEmptyStringRatherThanTurningItIntoAbsence()
    {
        Subject subject = Subject.Ready();
        UpdatePortalRequest request = SentinelUpdateRequest();
        request.Description = string.Empty;
        request.KeyWords = string.Empty;
        request.FooterText = string.Empty;
        request.PortalName = null;

        await subject.Service.UpdatePortalAsync(SeedPortalId, request, CancellationToken.None);

        Portal stored = subject.StoredPortal!;
        stored.Description.Should().BeEmpty().And.NotBeNull();
        stored.KeyWords.Should().BeEmpty().And.NotBeNull();
        stored.FooterText.Should().BeEmpty().And.NotBeNull();
        stored.PortalName.Should().BeEmpty(
            "the name column rejects a database null, so an omitted name becomes the empty string the legacy "
            + "contract used for the same purpose");
    }

    /// <summary>
    /// The tenant-installed record is written after the change is committed, under the legacy event name
    /// and naming the tenant it describes.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The ordering matters as much as the content: the record is written after the commit, so no record
    /// can describe a write that was rolled back.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_RecordsTheInstallationAfterItCommits()
    {
        Subject subject = Subject.Ready();

        await subject.Service.CreatePortalAsync(SentinelCreateRequest(), CancellationToken.None);

        // TWO records for one installation, deliberately. The enumeration's accurate member and the coarser
        // type the legacy installation actually raised are both emitted, so neither an operator's existing
        // HOST_ALERT alert nor a reader looking for the tenant itself is silently dropped.
        subject.AuditEvents.Select(candidate => candidate.EventName)
            .Should()
            .BeEquivalentTo([AuditEventNames.PortalCreated, AuditEventNames.HostAlert]);

        AuditEvent recorded = subject.AuditEvents
            .Should()
            .ContainSingle(candidate => candidate.EventName == AuditEventNames.PortalCreated)
            .Which;
        recorded.Outcome.Should().Be(AuditOutcome.Succeeded);
        recorded.PortalId.Should().Be(subject.StagedPortals.Single().PortalId);

        subject.AuditEvents
            .Should()
            .ContainSingle(candidate => candidate.EventName == AuditEventNames.HostAlert)
            .Which.PortalId.Should().Be(recorded.PortalId);

        subject.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A failure to write the audit record is NOT discarded, which is a deliberate divergence from the
    /// legacy behaviour.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: the divergence is bounded, and the boundary is what makes it safe. The record is written
    /// AFTER the transaction has committed, so a fault here cannot undo the tenant - the tenant exists, and
    /// the caller learns that the trail did not.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_DoesNotDiscardAFailureToRecordTheInstallation()
    {
        Subject subject = Subject.Ready();
        subject.Audit
            .Setup(audit => audit.Record(It.IsAny<AuditEvent>()))
            .Throws(new InvalidOperationException("the trail is unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => subject.Service.CreatePortalAsync(
            SentinelCreateRequest(),
            CancellationToken.None));

        subject.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Modifying a tenant records no audit event, because the legacy modification wrote none.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_InventsNoAuditEventBecauseTheLegacyModificationWroteNone()
    {
        Subject subject = Subject.Ready();

        Result<PortalDetailDto?> outcome = await subject.Service.UpdatePortalAsync(
            SeedPortalId,
            SentinelUpdateRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        subject.AuditEvents.Should().BeEmpty();
        subject.Audit.Verify(audit => audit.Record(It.IsAny<AuditEvent>()), Times.Never);
    }

    /// <summary>
    /// The clock is the only source of the current instant, so no assertion in this suite can depend on the
    /// time of day it runs at.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy source read the machine clock directly - in the server's LOCAL zone - which made every
    /// time-dependent branch untestable and every stored timestamp uninterpretable without knowing which
    /// machine wrote it.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_TakesEveryInstantFromTheInjectedClock()
    {
        Subject subject = Subject.Ready();
        var fixedInstant = new DateTime(2033, 7, 8, 9, 10, 11, DateTimeKind.Utc);
        subject.Clock.SetupGet(clock => clock.UtcNow).Returns(fixedInstant);

        await subject.Service.CreatePortalAsync(SentinelCreateRequest(), CancellationToken.None);

        subject.Clock.VerifyGet(clock => clock.UtcNow, Times.AtLeastOnce);
        subject.Users.Verify(
            users => users.CreateCredentialAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                fixedInstant,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The stored administrator role's key, distinct from the seed so a zero cannot pass by accident.
    /// </summary>
    private const int AdministratorRoleId = 0;

    /// <summary>Arranges a portal whose administrator role holds the supplied assignments.</summary>
    /// <param name="subject">The prepared subject.</param>
    /// <param name="assignments">The assignments the membership read should answer with.</param>
    private static void ArrangeAdministratorRole(Subject subject, params UserRole[] assignments)
    {
        subject.StoredPortal!.AdministratorRoleId = AdministratorRoleId;

        subject.Roles
            .Setup(roles => roles.GetByIdAsync(
                AdministratorRoleId,
                SeedPortalId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role
            {
                RoleId = AdministratorRoleId,
                PortalId = SeedPortalId,
                RoleName = "Renamed Administrators",
            });

        subject.Roles
            .Setup(roles => roles.GetUserRolesByUsernameAsync(
                SeedPortalId,
                null,
                "Renamed Administrators",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(assignments);
    }

    /// <summary>One administrator-role assignment with its account materialised.</summary>
    /// <param name="userRoleId">The assignment's own surrogate key.</param>
    /// <param name="userId">The account key.</param>
    /// <param name="username">The account's login name.</param>
    /// <param name="displayName">The account's display name.</param>
    /// <returns>The assignment.</returns>
    private static UserRole AdministratorAssignment(
        int userRoleId,
        int userId,
        string username,
        string displayName) => new()
        {
            UserRoleId = userRoleId,
            UserId = userId,
            RoleId = AdministratorRoleId,
            User = new User
            {
                UserId = userId,
                Username = username,
                DisplayName = displayName,
            },
        };

    [Fact]
    public async Task ListAdministratorCandidates_ReadsTheMembersOfThePortalsOwnAdministratorRole()
    {
        Subject subject = Subject.Ready();
        ArrangeAdministratorRole(
            subject,
            AdministratorAssignment(11, 1_001, "ada", "Ada Lovelace"));

        Result<IReadOnlyList<PortalAdministratorDto>?> outcome = await subject.Service
            .ListAdministratorCandidatesAsync(SeedPortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().NotBeNull();

        IReadOnlyList<PortalAdministratorDto> candidates = outcome.Value!;
        candidates.Should().HaveCount(1);
        candidates[0].UserId.Should().Be(1_001);
        candidates[0].Username.Should().Be("ada");
        candidates[0].DisplayName.Should().Be("Ada Lovelace");

        // Exactly the legacy GetUserRolesByRoleName(portalId, roleName): a null login name, which the
        // contract documents as "every account in the portal", leaving the role name as the only narrowing.
        subject.Roles.Verify(
            roles => roles.GetUserRolesByUsernameAsync(
                SeedPortalId,
                null,
                "Renamed Administrators",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListAdministratorCandidates_ReportsAnAbsentPortalAsAbsentRatherThanAsEmpty()
    {
        Subject subject = Subject.Ready();
        subject.StoredPortal = null;

        Result<IReadOnlyList<PortalAdministratorDto>?> outcome = await subject.Service
            .ListAdministratorCandidatesAsync(SeedPortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    [Fact]
    public async Task ListAdministratorCandidates_TreatsRoleZeroAsARealRoleRatherThanAsAbsence()
    {
        // ⚠ THE SENTINEL BOUNDARY. Roles.RoleID is IDENTITY (0, 1) (01.00.00.SqlDataProvider:L114), so the
        // FIRST role a tenant ever gets is numbered zero - and on the shipped installation that role is the
        // Administrators role.
        Subject subject = Subject.Ready();
        ArrangeAdministratorRole(
            subject,
            AdministratorAssignment(11, 1_001, "ada", "Ada Lovelace"));

        subject.StoredPortal.Should().NotBeNull();
        subject.StoredPortal!.AdministratorRoleId.Should().Be(0, "the arrangement pins the seed value");

        Result<IReadOnlyList<PortalAdministratorDto>?> outcome = await subject.Service
            .ListAdministratorCandidatesAsync(SeedPortalId, CancellationToken.None);

        outcome.Value.Should().NotBeNull();
        outcome.Value!.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListAdministratorCandidates_AnswersEmptyWhenThePortalDesignatesNoAdministratorRole()
    {
        Subject subject = Subject.Ready();
        subject.StoredPortal!.AdministratorRoleId = null;

        Result<IReadOnlyList<PortalAdministratorDto>?> outcome = await subject.Service
            .ListAdministratorCandidatesAsync(SeedPortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().NotBeNull();
        outcome.Value!.Should().BeEmpty();

        subject.Roles.Verify(
            roles => roles.GetUserRolesByUsernameAsync(
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ListAdministratorCandidates_OffersEachAccountOnceAndOrdersThemAsTheyAreDisplayed()
    {
        // Two facts in one case because they concern the same list.
        Subject subject = Subject.Ready();
        ArrangeAdministratorRole(
            subject,
            AdministratorAssignment(11, 1_001, "zoe", "Zoe Zebra"),
            AdministratorAssignment(12, 1_002, "ada", "Ada Lovelace"),
            AdministratorAssignment(13, 1_001, "zoe", "Zoe Zebra"));

        Result<IReadOnlyList<PortalAdministratorDto>?> outcome = await subject.Service
            .ListAdministratorCandidatesAsync(SeedPortalId, CancellationToken.None);

        outcome.Value.Should().NotBeNull();
        outcome.Value!.Select(candidate => candidate.DisplayName)
            .Should().Equal("Ada Lovelace", "Zoe Zebra");
    }

    [Fact]
    public async Task ListAdministratorCandidates_BreaksADisplayNameTieByTheLoginName()
    {
        Subject subject = Subject.Ready();
        ArrangeAdministratorRole(
            subject,
            AdministratorAssignment(11, 1_001, "smith.b", "B Smith"),
            AdministratorAssignment(12, 1_002, "smith.a", "B Smith"));

        Result<IReadOnlyList<PortalAdministratorDto>?> outcome = await subject.Service
            .ListAdministratorCandidatesAsync(SeedPortalId, CancellationToken.None);

        outcome.Value.Should().NotBeNull();
        outcome.Value!.Select(candidate => candidate.Username)
            .Should().Equal("smith.a", "smith.b");
    }

    [Fact]
    public async Task ListAdministratorCandidates_IsNotServedFromTheCache()
    {
        Subject subject = Subject.Ready();
        ArrangeAdministratorRole(
            subject,
            AdministratorAssignment(11, 1_001, "ada", "Ada Lovelace"));

        await subject.Service.ListAdministratorCandidatesAsync(SeedPortalId, CancellationToken.None);

        subject.Cache.Verify(
            cache => cache.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<PortalAdministratorDto>>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void AdministratorCandidateMapping_RefusesAnAssignmentWhoseAccountDidNotMaterialise()
    {
        var orphan = new UserRole { UserRoleId = 77, UserId = 1_001, RoleId = AdministratorRoleId };

        Action projecting = () => PortalMappings.ToAdministratorCandidate(orphan);

        projecting.Should().Throw<InvalidOperationException>()
            .WithMessage("*77*");
    }

    [Fact]
    public void AdministratorCandidateMapping_TakesEveryMemberFromTheAccountRatherThanTheAssignment()
    {
        // The assignment carries its own copy of the account key, and taking the key from one object and
        // the names from another is what would let a selector entry describe two different people.
        UserRole membership = AdministratorAssignment(11, 1_001, "ada", "Ada Lovelace");
        membership.UserId = 9_999;

        PortalAdministratorDto candidate = PortalMappings.ToAdministratorCandidate(membership);

        candidate.UserId.Should().Be(1_001);
    }
}
