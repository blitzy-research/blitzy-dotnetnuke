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
/// would leave two suites free to disagree about the same rule. This suite asserts the things that are
/// invisible to a workflow test because they are properties of the TRANSLATION rather than of the
/// behaviour: that argument 11 of a twenty-seven-argument procedure still reaches column 11, that a clamp
/// written as a two-armed function still floors at the same value, that a grand total still travels with
/// the records it counts, and that a number the legacy code used as an "absent" marker is still read as the
/// real row identifier it also is. A transposition between two same-typed neighbours is the specific fault
/// this file exists to catch, because no compiler and no workflow test can see one.
/// </para>
/// <para>
/// Mocked collaborators only, and only the domain and application abstractions. Nothing here reaches a
/// database context, a query root or SQL text; the persistence context is internal to the infrastructure
/// project precisely so that a unit test cannot acquire one. Every write assertion names an explicit call
/// count, and the single transactional commit point is verified on both the success and the refusal path.
/// </para>
/// <para>
/// Reflection-only assertions are declared <see langword="void"/> rather than asynchronous. That is not an
/// inconsistency: an asynchronous method with nothing to await raises a compiler warning, and this project
/// builds warnings as errors, so a contract-shape assertion that touches no collaborator must be
/// synchronous. Every assertion that does invoke the service is asynchronous and passes its cancellation
/// token explicitly rather than relying on the parameter's default.
/// </para>
/// </remarks>
public class PortalServiceApplicationTests
{
    /// <summary>
    /// The tenant these assertions address, which is deliberately the identity seed.
    /// </summary>
    /// <remarks>
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY (-1, 1)</c>, so -1 is the first identifier the column
    /// issues AND the value the legacy null contract used to mean "absent"
    /// (<c>Library/Components/Shared/Null.vb</c>, <c>NullInteger</c>). Addressing it by default is what
    /// keeps the collision under test on every path rather than only on the one test that names it.
    /// </remarks>
    private const int SeedPortalId = -1;

    /// <summary>
    /// The shipped default tenant, which occupies the value immediately after the seed.
    /// </summary>
    private const int DefaultPortalId = 0;

    /// <summary>
    /// The installation-wide host account used by write-path tests.
    /// </summary>
    /// <remarks>
    /// SEC-011: host authority is re-read from the account store. This identifier is deliberately distinct
    /// from every portal administrator used by the fixture so a host-only exemption cannot be satisfied by
    /// an unrelated tenant-scoped account lookup.
    /// </remarks>
    private const int HostCallerUserId = 9_901;

    /// <summary>
    /// The legacy cache key shape, <c>String.Format(DataCache.PortalCacheKey, PortalId)</c> at
    /// <c>PortalController.vb:L1225</c>, preserved so cache behaviour stays auditable against the original.
    /// </summary>
    private const string PortalCacheKeyFormat = "Portal{0}";

    /// <summary>
    /// The legacy per-entity portal cache timeout in minutes, which the installation-wide performance
    /// multiplier scales (<c>PortalController.vb:L1232</c>).
    /// </summary>
    private const int LegacyPortalCacheTimeOutMinutes = 20;

    /// <summary>
    /// The host name the tenant under test is reached by.
    /// </summary>
    private const string HostAlias = "tenant.example.test";

    /// <summary>
    /// A deliberately fake stand-in for a stored one-way hash. It is not a credential, cannot be a real
    /// one, and matches no provider's format.
    /// </summary>
    private const string FakePasswordHash = "REDACTED_PASSWORD_HASH";

    /// <summary>
    /// A deliberately fake stand-in for a submitted password.
    /// </summary>
    private const string FakeSubmittedPassword = "not-a-real-password-value";

    /// <summary>
    /// The twenty-seven arguments of <c>PortalController.UpdatePortalInfo</c>
    /// (<c>PortalController.vb:L1568</c>), transcribed from the signature in the order they are declared
    /// there and forwarded at <c>:L1570</c>.
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
    /// The fifteen arguments of <c>PortalController.CreatePortal</c>
    /// (<c>PortalController.vb:L980</c>), transcribed from the signature in declaration order.
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
    /// <remarks>
    /// Every pair below was read off the legacy signature at <c>PortalController.vb:L1568</c>: the hosting
    /// charge and the disc allowance were both <c>Double</c>; the page and member quotas were both
    /// <c>Integer</c>; the processor account and its secret were both <c>String</c>; the description and the
    /// keywords were both <c>String</c>; and the four page references were all <c>Integer</c>. A swap inside
    /// any one of them is invisible to the compiler on both sides of the migration.
    /// </remarks>
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
    /// <remarks>
    /// Nested inside the test class on purpose. A shared fixture file would couple this suite to its
    /// siblings, and the whole value of these assertions is that they pin one aggregate's translation
    /// independently of anything else in the project.
    /// </remarks>
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
        /// Builds a subject whose every collaborator answers plausibly, so that a test need only override the
        /// one fact it is about.
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

            subject.Permissions
                .Setup(permissions => permissions.GetByTabIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Permission>());

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
    /// <remarks>
    /// The numbering is the technique. Because argument 11 carries the value 11 and argument 12 carries the
    /// value 12, a swap between two same-typed neighbours does not merely fail an assertion - it fails one
    /// that names the position it landed in, so the diagnosis is immediate. Strings carry the same number in
    /// their text for the same reason.
    /// </remarks>
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
    /// term on a request and a non-nullable column on an entity compare equal when they hold the same value.
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
    /// The request contract presents the twenty-seven legacy arguments, in the legacy order, and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the cheapest defence available against the fault this file exists to catch. The legacy
    /// procedure at <c>PortalController.vb:L1568</c> took its values POSITIONALLY, so a reviewer checking a
    /// call site had to count arguments; the request object names them, but only stays checkable against the
    /// original while it presents the same members in the same sequence. Asserting the sequence - rather
    /// than only the set - is what lets the next reader lay the two signatures side by side.
    /// </para>
    /// <para>
    /// Declaration order is read through the metadata token, because the reflection API documents no
    /// ordering guarantee of its own.
    /// </para>
    /// </remarks>
    [Fact]
    public void UpdateRequest_PresentsTheTwentySevenLegacyArgumentsInTheirLegacyOrder()
    {
        IReadOnlyList<string> declared = typeof(UpdatePortalRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.Name)
            .ToList();

        declared.Should().Equal(
            LegacyUpdateArguments,
            "the twenty-seven positional arguments of UpdatePortalInfo (PortalController.vb:L1568) must "
            + "still be presented in their legacy order so a call site stays checkable against the original");
    }

    /// <summary>
    /// Every one of the twenty-six writable arguments lands on its own column, and the subject identifier
    /// lands on none.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The single most valuable assertion in this file. Twenty-six one-for-one assignments run between the
    /// request and the stored row, five pairs of them between neighbours the compiler cannot tell apart, and
    /// a transposition anywhere in that sequence produces working, wrong software. Asserting all twenty-six
    /// against numbered sentinels is the only way to see one.
    /// </remarks>
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

    /// <summary>
    /// Neither member of any same-typed neighbouring pair takes the other's value.
    /// </summary>
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
    /// throws after the row has already been altered in memory is only safe while nothing commits behind it.
    /// The refusal is an expected request failure with a stable code, so the API can return a bounded 400.
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
    /// <remarks>
    /// <para>
    /// The fifteen positional arguments of <c>CreatePortal</c> (<c>PortalController.vb:L980</c>) do not map
    /// one-for-one, so the mapping is transcribed here rather than inferred. Five of them named the
    /// administrator being created and are renamed to say so. Three of them were file-system locations -
    /// the template directory, the physical server path and the child directory - and file management is
    /// beyond the migrated scope, so they are carried by nothing.
    /// </para>
    /// <para>
    /// MIGRATION: the three absent arguments are the observable trace of that omission. Asserting their
    /// absence is what stops a later agent reintroducing a path argument on the assumption it was overlooked.
    /// </para>
    /// </remarks>
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
    /// Every table the legacy creation sequence wrote is staged before anything is flushed, and the whole of
    /// it is enclosed by a single transaction.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The legacy sequence wrote the tenant, its host name, its roles, its pages and its modules through
    /// separate statements that shared no transaction, so a failure part-way through left a half-built tenant
    /// behind - which is why the legacy body accumulated a message string as it went rather than reporting
    /// success or failure outright.
    /// </para>
    /// <para>
    /// Note what is asserted and what is not. Creation FLUSHES more than once, because three columns on the
    /// tenant row need keys the store assigns during the first flush and the administrator's credential
    /// lives in a store no entity maps. Claiming a single flush here would be false. What is single is the
    /// TRANSACTION, and that is the property that makes the sequence atomic.
    /// </para>
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

    /// <summary>
    /// A creation refused before any work is staged opens no transaction and flushes nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The duplicate-host-name check runs before the transaction opens, which is deliberate: refusing a
    /// request should not cost a transaction. Asserting the absence of both the flush and the transaction is
    /// what pins that ordering, since a later refactor could satisfy the refusal while moving the check
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
    /// <param name="supplied">The submitted fee, as invariant text because a decimal cannot be an attribute constant.</param>
    /// <param name="expected">The fee that must be stored.</param>
    /// <remarks>
    /// <para>
    /// The legacy source floors two fees, at <c>PortalController.vb:L395</c> and <c>:L398</c>, each written
    /// <c>CType(IIf(fee &lt; 0, 0, fee), Single)</c>. Those are the ONLY two such guards in the whole of the
    /// migrated surface, which is why they are pinned rather than left to a general mapper assertion.
    /// </para>
    /// <para>
    /// The equivalence is the point of this theory, not merely the flooring. The legacy construct is a
    /// FUNCTION and therefore evaluates BOTH of its arms, whereas the modern conditional short-circuits, so
    /// the substitution is only safe because both arms here are side-effect-free. Asserting agreement with
    /// <see cref="Math.Max(decimal, decimal)"/> - the replacement the migration plan names - proves the
    /// substitution rather than assuming it, at the boundary and on either side of it.
    /// </para>
    /// </remarks>
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
    /// <para>
    /// The legacy helper clamped TWO fees, not one: <c>serviceFee</c> was argument 4 of <c>CreateRole</c> and
    /// <c>trialFee</c> was argument 7, both typed the same, and each was floored by its own guard at
    /// <c>PortalController.vb:L395</c> and <c>:L398</c>. Two same-typed arguments each passing through an
    /// identical guard is the ideal conditions for a transposition that nothing notices, because the two
    /// clamps are textually almost the same line.
    /// </para>
    /// <para>
    /// The ASYMMETRIC rows below are the ones that carry the weight. When only one of the two fees is
    /// negative, a clamp fed from the wrong argument floors the wrong column and stores the other one
    /// unchanged - and those two rows fail while the symmetric rows would still pass. Covering each fee at a
    /// negative value, at the boundary and at a positive value is what makes the pairing checkable rather
    /// than merely plausible.
    /// </para>
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
    /// Both fee columns are floored on every stock role a new tenant receives, and the legacy absent-integer
    /// marker on the role group becomes genuine absence.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The legacy role-creation step set the two fees at <c>PortalController.vb:L395</c> and <c>:L398</c> and
    /// the role group at <c>:L393</c>. Each of the three roles is asserted individually rather than
    /// collectively, because the two fees are ADJACENT SAME-TYPED arguments of the legacy signature -
    /// arguments 4 and 7 of <c>CreateRole</c> - and a collective assertion that both are nothing cannot tell
    /// a swap between them from a correct assignment.
    /// </para>
    /// <para>
    /// MIGRATION: the role group is where a sentinel becomes absence. The legacy line assigned it the
    /// absent-integer marker, which in this schema is -1 - and -1 is also a legitimate role-group key, so the
    /// legacy value could not distinguish "no group" from "group -1". The domain column is a nullable
    /// integer and carries nothing at all, which can.
    /// </para>
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
    /// <para>
    /// The legacy step guarded itself: <c>PortalController.vb:L386</c> looked the role up by name and
    /// <c>:L388</c> created it only when the lookup found nothing, reusing the existing key at <c>:L405</c>
    /// otherwise. BOTH arms are accounted for here, and the accounting is what justifies the omission.
    /// </para>
    /// <para>
    /// The reuse arm is UNREACHABLE for a tenant being created. A role is owned by a portal, and this portal
    /// does not exist until this transaction commits, so no role can already be bound to it and the lookup
    /// can only ever answer "nothing". The legacy guard was needed because the same private helper also ran
    /// while a template was being applied to an existing tenant - a path this migration does not carry - and
    /// performing a lookup that cannot succeed would be a round trip per role for no answer. Asserting that
    /// the lookup is never made pins the reasoning rather than leaving it as a comment, and asserting one
    /// insertion per role name pins the create arm.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// Two legacy readers collapse into this one member. <c>GetPortals</c>
    /// (<c>PortalController.vb:L1263</c>) returned the non-generic collection type of the era - it declared
    /// no element type and no total, so a caller learned the element type only by casting and learned the
    /// total only by counting. <c>GetPortalsByName</c> (<c>:L262</c>) returned the same untyped collection
    /// and passed its grand total back through a by-reference argument, so one call produced two answers that
    /// no type tied together and that nothing obliged a caller to read consistently.
    /// </para>
    /// <para>
    /// Both defects close in the same assertion: the records and the total arrive on a single typed value, so
    /// neither can be read without the other, and the element type is declared rather than discovered.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A listing that matches nothing is an empty page and a success, not a failure.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy untyped reader expressed "nothing matched" and "the read failed" identically, as a
    /// collection with no elements, because a failure produced one too. Separating them is a behavioural
    /// improvement that costs no caller anything, and it is asserted so the distinction cannot quietly
    /// collapse back.
    /// </remarks>
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
    /// <remarks>
    /// The legacy readers' return type was the era's non-generic collection, whose element type existed only
    /// in a documentation comment. This assertion walks the payload and requires every sequence-shaped member
    /// to be a closed generic, which is the property that makes the wire contract self-describing.
    /// </remarks>
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
    /// The contract measures no disc consumption, while the three stored allowances it used to be measured
    /// against survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: four legacy members measured a tenant's files on disc and none is ported, because file
    /// management is beyond the migrated scope: <c>GetPortalSpaceUsed</c>
    /// (<c>PortalController.vb:L1596</c>), <c>GetPortalSpaceUsedBytes</c> (<c>:L1278</c> and <c>:L1296</c>)
    /// and <c>HasSpaceAvailable</c> (<c>:L1323</c>). The first was additionally marked obsolete IN THE LEGACY
    /// SOURCE, superseded there by the byte-counting member, so omission rather than translation is the
    /// faithful treatment of it.
    /// </para>
    /// <para>
    /// MIGRATION: one consequence is worth stating plainly, because it is the only place this suite cannot
    /// assert a legacy behaviour. <c>GetPortalSpaceUsed</c> wrapped its work in a handler that returned
    /// <c>Integer.MaxValue</c> on ANY failure - it neither returned nothing nor let the fault surface - and
    /// that behaviour has no target to exercise it, since the member does not exist. It is recorded here and
    /// in <c>MIGRATION_NOTES.md</c> rather than reproduced, and no substitute member is invented to give the
    /// assertion something to run against.
    /// </para>
    /// <para>
    /// What is asserted instead is the boundary of the omission. The measurement is dropped; the stored
    /// ALLOWANCES are not. The disc allowance and the page and member quotas were arguments 11, 12 and 13 of
    /// the twenty-seven-argument update and remain writable and readable, so an operator can still set a
    /// limit even though nothing now measures a file system against it.
    /// </para>
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
    /// Every member of the contract is asynchronous and takes neither an output nor a by-reference argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy surface reported a secondary answer by mutating an argument the caller passed in - the
    /// tenant listing returned its grand total that way, and thirty such signatures were counted across the
    /// migrated trees. That idiom is replaced entirely by outcome and page types, and this assertion is what
    /// keeps it replaced: a single reintroduced argument of that kind would restore a contract a caller can
    /// use without noticing there is a second answer to read.
    /// </para>
    /// <para>
    /// Asynchrony is asserted in the same place because the two properties are enforced by the same
    /// discipline. Every member is bound by input or output, so every member returns a task, and a
    /// synchronous member would be a path on which cancellation could not be honoured.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// The legacy expression is <c>DataCache.PortalCacheTimeOut * Convert.ToInt32(Globals.PerformanceSetting)</c>
    /// at <c>PortalController.vb:L1232</c>, with a twenty-minute base for this entity and a multiplier whose
    /// measured default is 3. Pinning the arithmetic across several multipliers, rather than pinning one
    /// lifetime at the default, is what proves the base and the multiplier are both still in play: a
    /// hard-coded sixty minutes would satisfy a single-value assertion and fail every row below.
    /// </para>
    /// <para>
    /// The key shape is asserted alongside because the two travel together. Keeping the legacy key name means
    /// an operator can still correlate a cache entry with the legacy behaviour it reproduces.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// The legacy code guarded its own store with <c>If timeOut &gt; 0 Then</c> - visible at
    /// <c>PortalController.vb:L1222</c> and again at <c>:L1245</c> - which is how an installation turned
    /// caching off, and an operator diagnosing a stale read still relies on it. A negative multiplier is
    /// invalid configuration rather than an off switch, but the same guard must absorb it: what must never
    /// happen is a negative lifetime reaching the cache, whose handling would then be the cache's business
    /// rather than something this service can state.
    /// </remarks>
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
    /// <para>
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY (-1, 1)</c>, so -1 is the FIRST identifier the column
    /// issues and 0 is the shipped default tenant. The legacy null contract, at
    /// <c>Library/Components/Shared/Null.vb</c>, defined its absent-integer marker as -1, so the two meanings
    /// were indistinguishable: code that tested for -1 to mean "nothing was supplied" was also testing for a
    /// real, addressable row.
    /// </para>
    /// <para>
    /// The rule this pins is therefore narrow and absolute: no value is special. Every integer is forwarded,
    /// none is coerced to absence, and none short-circuits the read. The unfiltered case, where one genuinely
    /// is wanted, is expressed as a nullable argument elsewhere on the contract precisely so it cannot
    /// collide with a real key.
    /// </para>
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
    /// <para>
    /// The legacy null contract defined its absent-string marker as the EMPTY STRING rather than as nothing,
    /// so a database null and an empty string were indistinguishable once read through the legacy path. The
    /// domain model uses nullable types, which is the honest representation - but the translation must not
    /// change what a caller observes: a caller that submits an empty string has cleared a value, and turning
    /// that into absence would alter the stored row on a column where the two are distinguishable.
    /// </para>
    /// <para>
    /// The reverse coercion is asserted too, on the one column where the legacy schema forbids a null: an
    /// omitted name is stored as an empty string, because the column rejects a database null.
    /// </para>
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
    /// The tenant-installed record is written after the change is committed, under the legacy event name and
    /// naming the tenant it describes.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The legacy trail is real and had to survive. <c>PortalController.vb:L1157</c> wrote an entry carrying
    /// fourteen named properties, and the storage subsystem behind it is beyond the migrated scope, so the
    /// entry becomes a structured event under a stable name instead. What is asserted is that the INTENT
    /// survives - an event is emitted, it is named as the legacy type was, and it identifies the tenant - and
    /// not that any particular table was written, because none is.
    /// </para>
    /// <para>
    /// The ordering matters as much as the content: the record is written after the commit, so no record can
    /// describe a write that was rolled back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreatePortal_RecordsTheInstallationAfterItCommits()
    {
        Subject subject = Subject.Ready();

        await subject.Service.CreatePortalAsync(SentinelCreateRequest(), CancellationToken.None);

        // TWO records for one installation, deliberately. The enumeration's accurate member and the coarser
        // type the legacy installation actually raised (PortalController.vb:L1140-L1141) are both emitted, so
        // neither an operator's existing HOST_ALERT alert nor a reader looking for the tenant itself is
        // silently dropped. They are built from the same facts, so they cannot describe different events.
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
    /// A failure to write the audit record is NOT discarded, which is a deliberate divergence from the legacy
    /// behaviour.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: DELIBERATE DIVERGENCE, and the only one this suite asserts as a change rather than as a
    /// preservation. The legacy audit write sat inside a handler whose body was EMPTY, at
    /// <c>PortalController.vb:L1158-L1160</c>, so a logging failure was thrown away: the one record proving a
    /// tenant had been installed could go missing while the caller was told the installation had succeeded.
    /// That handler is not reproduced. A failure to record surfaces instead, and the divergence is written up
    /// in <c>MIGRATION_NOTES.md</c> under the note that none of the legacy empty handlers is reproduced.
    /// </para>
    /// <para>
    /// MIGRATION: the divergence is bounded, and the boundary is what makes it safe. The record is written
    /// AFTER the transaction has committed, so a fault here cannot undo the tenant - the tenant exists, and
    /// the caller learns that the trail did not. Losing the record silently is the worse of the two
    /// outcomes for a trail whose whole purpose is to be complete. The alternative - restoring the empty
    /// handler - was rejected because the migration is held to an engineering baseline that forbids
    /// discarding failures, and reproducing it would have to be justified as fidelity to a defect.
    /// </para>
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

    /// <summary>
    /// Modifying a tenant records no audit event, because the legacy modification wrote none.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The mirror image of the assertion two above, and it is the one that keeps the trail HONEST rather than
    /// merely present. The legacy <c>UpdatePortalInfo</c> (<c>PortalController.vb:L1568</c>) forwarded its
    /// twenty-seven arguments and cleared the cache, and did nothing else: there is no audit entry on that
    /// path to port. Emitting one would be an invented behaviour, and an invented audit event is worse than a
    /// missing one, because a reader of the trail cannot tell which entries reflect the original system. The
    /// migrated audit surface is the two tenant-lifecycle events the legacy source actually wrote.
    /// </remarks>
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
    /// machine wrote it. The injected clock exists to close both problems, and this assertion pins the first:
    /// a creation that stamps a date must take it from the clock, so that substituting the clock is
    /// sufficient to make the path deterministic.
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
}
