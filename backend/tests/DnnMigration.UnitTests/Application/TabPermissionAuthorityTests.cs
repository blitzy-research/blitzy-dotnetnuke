using DnnMigration.Application.Abstractions;
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
/// Pins the authority a tenant's own administrator holds over that tenant's PAGES - the rule the single-page
/// permission question omitted while both of its page-listing siblings applied it.
/// </summary>
/// <remarks>
/// <para>
/// THE LEGACY RULE, AND WHY IT IS NOT A CONVENIENCE. <c>PortalSecurity.HasNecessaryPermission</c> -
/// <c>Library/Components/Security/PortalSecurity.vb:L519-L548</c> - read <c>isAdmin =
/// IsInRole(PortalSettings.AdministratorRoleName)</c> and admitted the View, Edit AND Admin access levels on
/// that alone, consulting no permission row at all. A freshly created page carries no grant rows, so a rule
/// that requires one refuses the tenant's administrator the read and the update of the very page they just
/// created.
/// </para>
/// <para>
/// WHAT WAS MEASURED BEFORE THE FIX. An administrator of portal <c>-1</c>, holding that portal's designated
/// administrators role, was answered <c>403</c> on both <c>GET</c> and <c>PUT /api/v1/tabs/{id}</c> for a
/// page with no row naming their role; adding a VIEW and an EDIT row flipped both to <c>200</c> and removing
/// them flipped both back. The same installation reported that same page in the page LISTING, because
/// <c>HasAnyTabPermissionInPortalAsync</c> and <c>ListTabsWithPermissionAsync</c> both ask about
/// administration first - so one installation said the administrator may act on a page and then refused the
/// page itself.
/// </para>
/// <para>
/// SENTINEL IDENTITIES ARE DELIBERATE. The tenant bears <c>-1</c> because <c>Portals.PortalID</c> is
/// <c>IDENTITY (-1, 1)</c> and that value collides with the legacy absent-integer sentinel; the
/// administrators role bears <c>0</c> because <c>Roles.RoleID</c> is <c>IDENTITY (0, 1)</c> and zero is the
/// CLR default for its type. A rule that read either as "unset" would refuse the caller who matters most.
/// </para>
/// </remarks>
public class TabPermissionAuthorityTests
{
    /// <summary>The tenant under test, bearing the identity seed that collides with the legacy sentinel.</summary>
    private const int PortalId = -1;

    /// <summary>A second tenant, used to prove the authority does not cross a tenant boundary.</summary>
    private const int OtherPortalId = 6;

    /// <summary>The page under test.</summary>
    private const int TabId = 5;

    /// <summary>The portal's designated administrator role, bearing the identity seed of its column.</summary>
    private const int AdministratorRoleId = 0;

    /// <summary>The name that role happens to carry. Operator-editable, and therefore evidence of nothing.</summary>
    private const string AdministratorRoleName = "Administrators";

    /// <summary>The administering caller.</summary>
    private const int AdministratorUserId = 2;

    /// <summary>A caller holding no administration.</summary>
    private const int MemberUserId = 3;

    /// <summary>
    /// A tenant administrator is admitted to a page of their own tenant that no permission row names, for
    /// every key the legacy admitted on administration alone.
    /// </summary>
    /// <param name="permissionKey">The key being asked about.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The evaluator is asserted NEVER to be consulted, which is what distinguishes this from a grant that
    /// merely happens to exist: the answer comes from the tenant's own designation, and the arrangement holds
    /// ZERO grant rows.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    public async Task TenantAdministrator_IsAdmittedToItsOwnPageWithNoGrantRowPresent(PermissionKey permissionKey)
    {
        Harness harness = Harness.Ready();

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            AdministratorUserId,
            TabId,
            permissionKey,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeTrue(
            "the tenant's administrator administers its pages whether or not a grant row names their role");

        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "administration is the cheap and decisive arm, so it is answered before any grant read is issued");
    }

    /// <summary>
    /// An ordinary member of the same tenant, holding no grant, is still refused - so the new arm widens
    /// authority for the administrator alone.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task OrdinaryMember_HoldingNoGrant_IsStillRefused()
    {
        Harness harness = Harness.Ready();
        harness.CallerRoleNames = ["Registered Users"];

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            MemberUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeFalse();

        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                TabId,
                PermissionKey.EDIT,
                MemberUserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a caller who does not administer the tenant is decided by the grant rows, as before");
    }

    /// <summary>
    /// A grant row still admits a caller who does not administer the tenant, so nothing the row-based arm
    /// decided has been taken away.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task OrdinaryMember_HoldingAGrant_IsStillAdmitted()
    {
        Harness harness = Harness.Ready();
        harness.CallerRoleNames = ["Registered Users"];
        harness.EvaluatorVerdict = Result<bool>.Success(true);

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            MemberUserId,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeTrue();
    }

    /// <summary>
    /// Administration of ONE tenant is not authority over ANOTHER tenant's page: the foreign-tenant refusal
    /// is reported before the authority arm is reached.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The page belongs to a different portal from the one the question is asked within, which is the shape a
    /// cross-tenant attempt takes. The failure code matters as well as the verdict: the endpoint answers
    /// <c>400</c> for it rather than describing the page as ungranted.
    /// </remarks>
    [Fact]
    public async Task TenantAdministrator_IsRefusedAnotherTenantsPage()
    {
        Harness harness = Harness.Ready();
        harness.Tab.PortalId = OtherPortalId;

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            AdministratorUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("permission.tab_foreign_tenant");
    }

    /// <summary>
    /// A page belonging to NO tenant is not a page this tenant's administrator administers, so it falls
    /// through to the grant rows.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// ⚠ THE OWNERSHIP TEST APPLIED BEFORE ANY AUTHORITY QUESTION IS DELIBERATELY WIDER THAN THE AUTHORITY.
    /// It admits a page with no tenant, because a host-level page is in scope for every portal - which is
    /// what makes the host administration pages addressable from inside one. The AUTHORITY requires exact
    /// ownership, and the API records the narrower rule as a deliberate divergence from the legacy ownership
    /// check, which accepted <c>Tabs.PortalId is null</c> unconditionally. Widening the authority to match
    /// the ownership test would make every host-level page readable and writable from inside every tenant.
    /// </remarks>
    [Fact]
    public async Task APageBelongingToNoTenant_IsNotAdministeredByATenantAdministrator()
    {
        Harness harness = Harness.Ready();
        harness.Tab.PortalId = null;

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            AdministratorUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("a host-level page is in scope, so this is not a refusal to answer");
        outcome.Value.Should().BeFalse();

        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                TabId,
                PermissionKey.EDIT,
                AdministratorUserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the page falls through to the grant rows exactly as it did before the authority arm existed");
    }

    /// <summary>
    /// A page that does not exist is reported as absent rather than as ungranted, even for the tenant's
    /// administrator.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The order this pins is what lets the API answer <c>404</c> for an identifier that names nothing: the
    /// page's existence is proved before any authority question is asked, so the authority arm cannot turn a
    /// missing page into a permitted one.
    /// </remarks>
    [Fact]
    public async Task AnUnknownPage_IsReportedAbsentEvenForTheTenantAdministrator()
    {
        Harness harness = Harness.Ready();
        harness.TabRow = null;

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            AdministratorUserId,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("permission.tab_not_found");
    }

    /// <summary>
    /// An assignment of the designated role whose validity window has closed confers nothing, so a lapsed
    /// administrator is refused.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The authority arm reads the role names the caller resolution produced, and that resolution asks the
    /// account store for the names valid AS OF NOW - so an expired assignment is absent from the list rather
    /// than filtered afterwards. Modelled here by the store answering with no names.
    /// </remarks>
    [Fact]
    public async Task ALapsedAdministratorAssignment_ConfersNothing()
    {
        Harness harness = Harness.Ready();
        harness.CallerRoleNames = [];

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            AdministratorUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeFalse();
    }

    /// <summary>
    /// A tenant that designates no administrator role grants nothing on that account: a configuration gap
    /// must not admit.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task ATenantDesignatingNoAdministratorRole_AdmitsNobodyOnThatBasis()
    {
        Harness harness = Harness.Ready();
        harness.Portal.AdministratorRoleId = null;

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            AdministratorUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeFalse();
    }

    /// <summary>
    /// The authority follows the tenant's DESIGNATION rather than the role's name: a caller holding a role
    /// called "Administrators" that the tenant does not designate is refused.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// <c>Roles.RoleName</c> is an ordinary updatable column and a role of the same name may belong to
    /// another tenant, so a name match is right about the word and wrong about the portal. The comparison is
    /// by name only because the name is read FROM the designated role's own row.
    /// </remarks>
    [Fact]
    public async Task ARoleMerelyNamedAdministrators_ConfersNoAuthority()
    {
        Harness harness = Harness.Ready();
        harness.DesignatedRole = new Role
        {
            RoleId = AdministratorRoleId,
            PortalId = PortalId,
            RoleName = "Site Managers",
        };

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            AdministratorUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeFalse(
            "the caller holds a role named Administrators, but the tenant designates a differently named role");
    }

    /// <summary>
    /// An anonymous caller is never admitted by the authority arm, whatever the designated role is called.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// An anonymous caller's resolved names are the two built-in pseudo-roles rather than assignments, so an
    /// installation that had renamed its administrators role to one of those wordings would otherwise admit
    /// an unauthenticated caller on a naming coincidence. The designated role is named for that coincidence
    /// here deliberately, and the caller is still refused.
    /// </remarks>
    [Fact]
    public async Task AnAnonymousCaller_IsNeverAdmittedByTheAuthorityArm()
    {
        Harness harness = Harness.Ready();
        harness.DesignatedRole = new Role
        {
            RoleId = AdministratorRoleId,
            PortalId = PortalId,
            RoleName = SpecialRoleNames.AllUsers,
        };

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            userId: null,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeFalse();
    }

    /// <summary>
    /// A host account is admitted before the tenant-authority arm is reached, which is the answer the
    /// enforcing policy gives too.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task AHostAccount_IsAdmittedWithoutTenantMembership()
    {
        Harness harness = Harness.Ready();
        harness.Account.IsSuperUser = true;
        harness.Portal.AdministratorRoleId = null;
        harness.CallerRoleNames = [];

        Result<bool> outcome = await harness.Service.HasTabPermissionAsync(
            PortalId,
            AdministratorUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeTrue();
    }

    /// <summary>
    /// The collaborators of <see cref="PermissionService"/>, wired so that the single-page question has an
    /// answer for everything it asks and every fact a test needs to vary is a settable member.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            Tab = new Tab
            {
                TabId = TabId,
                PortalId = PortalId,
                TabName = "QA Secondary Page",
            };

            TabRow = Tab;

            Portal = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                AdministratorRoleId = AdministratorRoleId,
            };

            Account = new User
            {
                UserId = AdministratorUserId,
                Username = "setup_admin",
                DisplayName = "Setup Administrator",
            };

            DesignatedRole = new Role
            {
                RoleId = AdministratorRoleId,
                PortalId = PortalId,
                RoleName = AdministratorRoleName,
            };

            CallerRoleNames = [AdministratorRoleName];
            EvaluatorVerdict = Result<bool>.Success(false);

            Permissions = new Mock<IPermissionRepository>(MockBehavior.Loose);
            Evaluator = new Mock<IPermissionEvaluator>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Roles = new Mock<IRoleRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);

            Service = new PermissionService(
                Permissions.Object,
                Evaluator.Object,
                Portals.Object,
                Modules.Object,
                Tabs.Object,
                Users.Object,
                Roles.Object,
                UnitOfWork.Object,
                Cache.Object,
                Clock.Object,
                new CachingOptions());
        }

        /// <summary>The page under test, mutable so a foreign tenant is testable.</summary>
        public Tab Tab { get; }

        /// <summary>
        /// What the page store answers with, settable to <see langword="null"/> so that "does not exist" and
        /// "belongs to another tenant" can be told apart.
        /// </summary>
        public Tab? TabRow { get; set; }

        /// <summary>The tenant row, mutable so an undesignated administrator role is testable.</summary>
        public Portal Portal { get; }

        /// <summary>The calling account.</summary>
        public User Account { get; }

        /// <summary>The role the tenant designates, settable so a differently named one is testable.</summary>
        public Role? DesignatedRole { get; set; }

        /// <summary>The role names the account store reports as valid at the instant being judged.</summary>
        public IReadOnlyList<string> CallerRoleNames { get; set; }

        /// <summary>The row-based verdict, so a genuine grant can be told from the authority arm.</summary>
        public Result<bool> EvaluatorVerdict { get; set; }

        public Mock<IPermissionRepository> Permissions { get; }

        public Mock<IPermissionEvaluator> Evaluator { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IRoleRepository> Roles { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<IClock> Clock { get; }

        public PermissionService Service { get; }

        /// <summary>
        /// Builds a harness describing the measured fixture: one tenant designating its administrators role,
        /// one page belonging to it, one caller holding that role, and NO permission rows anywhere.
        /// </summary>
        /// <returns>The harness.</returns>
        public static Harness Ready()
        {
            Harness harness = new();

            harness.Clock
                .SetupGet(clock => clock.UtcNow)
                .Returns(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));

            harness.Tabs
                .Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.TabRow);

            harness.Portals
                .Setup(portals => portals.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Portal);

            harness.Users
                .Setup(users => users.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Account);

            harness.Users
                .Setup(users => users.ListRoleNamesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.CallerRoleNames);

            harness.Roles
                .Setup(roles => roles.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.DesignatedRole);

            harness.Evaluator
                .Setup(evaluator => evaluator.HasTabPermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.EvaluatorVerdict);

            return harness;
        }
    }
}
