using System.Globalization;
using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using Moq;
using Xunit;
using Frequency = DnnMigration.Domain.Enums.BillingFrequency;

namespace DnnMigration.UnitTests.Services;

/// <summary>
/// Covers the role and role-group workflow: tenant scoping, name uniqueness, automatic enrolment, the
/// paid-membership term arithmetic that derives an assignment's dates, and the two assignments the service
/// refuses to remove.
/// </summary>
/// <remarks>
/// Two asymmetries in this service are deliberate and are pinned here because they look like oversights
/// until the reason is stated. First, the creation member performs no shape checking of its own while the
/// update member performs a full set: the creation route has a registered request validator and the update
/// route has none, so the service compensates exactly where the pipeline does not.
/// </remarks>
public class RoleServiceTests
{
    private const int PortalId = -1;

    private const int OtherPortalId = 3;

    private const int RoleId = 0;

    private const int OtherRoleId = 41;

    private const int RoleGroupId = 0;

    private const int UserId = 7;

    private const string RoleName = "Subscribers";

    /// <summary>
    /// The identifier the harness gives the role that already holds a name, so a refusal message that names
    /// the existing role can be asserted exactly.
    /// </summary>
    private const int ClashingRoleId = 4242;

    private const string RoleGroupName = "Paid Membership";

    private const string MemberName = "measured_member";

    /// <summary>The operator every harnessed call is made on behalf of, for audit attribution.</summary>
    private const int OperatorUserId = 2;

    /// <summary>The operator's account name, as an audit record would carry it.</summary>
    private const string OperatorUserName = "measured_operator";

    private const string PortalNotFoundCode = "portal.not_found";

    private const string RoleNotFoundCode = "role.not_found";

    private const string RoleNameDuplicateCode = "role.name_duplicate";

    private const string RoleCreateFailedCode = "role.create_failed";

    private const string RoleGroupNotFoundCode = "role_group.not_found";

    private const string RoleGroupNameDuplicateCode = "role_group.name_duplicate";

    private const string RoleGroupInUseCode = "role_group.in_use";

    private const string UserNotFoundCode = "user.not_found";

    private const string AssignmentNotFoundCode = "role_assignment.not_found";

    private const string AssignmentProtectedCode = "role_assignment.protected";

    private const string AssignmentExpiredNotRemovedCode = "role_assignment.expired_not_removed";

    private const string PagingInvalidCode = "role.paging_invalid";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The role contract exposes fifteen asynchronous operations and nothing else, each of which accepts a
    /// cancellation token as its final argument.
    /// </summary>
    [Fact]
    public void RoleContract_OffersExactlyFifteenOperations()
    {
        MethodInfo[] members = typeof(IRoleService).GetMethods();

        members.Should().HaveCount(15, "the role contract covers roles, their members and their groups");

        foreach (MethodInfo member in members)
        {
            member.Name.Should().EndWith("Async");
            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue();

            ParameterInfo[] parameters = member.GetParameters();
            parameters[^1].ParameterType.Should().Be(typeof(CancellationToken));
            parameters[0].Name.Should().Be("portalId", "every operation is scoped to one tenant");
        }
    }

    /// <summary>The service refuses to be constructed without every collaborator it depends on.</summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        var roles = new Mock<IRoleRepository>().Object;
        var portals = new Mock<IPortalRepository>().Object;
        var users = new Mock<IUserRepository>().Object;
        var permissions = new Mock<IPermissionService>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var clock = new Mock<IClock>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var audit = new Mock<IAuditSink>().Object;

        Assert.Throws<ArgumentNullException>("roles", () =>
        {
            _ = new RoleService(null!, portals, users, permissions, unitOfWork, clock, cache, currentUser, audit);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new RoleService(roles, null!, users, permissions, unitOfWork, clock, cache, currentUser, audit);
        });
        Assert.Throws<ArgumentNullException>("users", () =>
        {
            _ = new RoleService(roles, portals, null!, permissions, unitOfWork, clock, cache, currentUser, audit);
        });
        Assert.Throws<ArgumentNullException>("permissions", () =>
        {
            _ = new RoleService(roles, portals, users, null!, unitOfWork, clock, cache, currentUser, audit);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new RoleService(roles, portals, users, permissions, null!, clock, cache, currentUser, audit);
        });
        Assert.Throws<ArgumentNullException>("clock", () =>
        {
            _ = new RoleService(roles, portals, users, permissions, unitOfWork, null!, cache, currentUser, audit);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new RoleService(roles, portals, users, permissions, unitOfWork, clock, null!, currentUser, audit);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new RoleService(roles, portals, users, permissions, unitOfWork, clock, cache, null!, audit);
        });
        Assert.Throws<ArgumentNullException>("audit", () =>
        {
            _ = new RoleService(roles, portals, users, permissions, unitOfWork, clock, cache, currentUser, null!);
        });
    }

    /// <summary>
    /// Every collaborator the constructor accepts is guarded, so the guard list cannot fall behind the
    /// parameter list.
    /// </summary>
    [Fact]
    public void Service_GuardsAsManyCollaboratorsAsItAccepts()
    {
        ConstructorInfo constructor = typeof(RoleService).GetConstructors().Should().ContainSingle().Subject;

        string[] guarded =
        [
            "roles",
            "portals",
            "users",
            "permissions",
            "unitOfWork",
            "clock",
            "cache",
            "currentUser",
            "audit",
        ];

        constructor.GetParameters().Select(parameter => parameter.Name).Should().Equal(
            guarded,
            "the null-argument cases above are written out one per collaborator, in this order");
    }

    /// <summary>Listing roles requires a paging request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ListRolesAsync(PortalId, null!, null, cancellationToken: CancellationToken.None));
    }

    /// <summary>Listing roles for a tenant that does not exist is refused before the role store is read.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest(), null, cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
        outcome.Reason!.Message.Should().Be($"No portal bears identifier {PortalId}.");
        harness.Roles.Verify(
            r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A group filter naming a group of another tenant is refused rather than silently ignored.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_RefusesAGroupFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = new RoleGroup
        {
            RoleGroupId = RoleGroupId,
            PortalId = OtherPortalId,
            RoleGroupName = RoleGroupName,
        };

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest(), RoleGroupId, cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} has no role group bearing identifier {RoleGroupId}.");
    }

    /// <summary>A group filter naming no stored group at all is refused with the same reason.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_RefusesAnUnknownGroup()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = null;

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest(), RoleGroupId, cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNotFoundCode);
    }

    /// <summary>The ungrouped scope lists the roles that belong to no group, and nothing else.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_ForTheUngroupedScopeListsOnlyTheRolesWithNoGroup()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged(
        [
            new Role { RoleId = 0, PortalId = PortalId, RoleName = "Ungrouped", RoleGroupId = null },
            new Role { RoleId = 1, PortalId = PortalId, RoleName = "Grouped", RoleGroupId = RoleGroupId },
            new Role { RoleId = 2, PortalId = PortalId, RoleName = "GroupedAtZero", RoleGroupId = 0 },
        ]);

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            RoleGroupScope.Ungrouped,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Items.Select(row => row.RoleId).Should().Equal(
            new[] { 0 },
            "a role grouped at zero is grouped - RoleGroups.RoleGroupID is IDENTITY(0, 1) - so only the "
                + "role whose group is absent qualifies");
    }

    /// <summary>
    /// The default scope lists every role whatever its grouping, which is what an omitted scope means.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_ForTheDefaultScopeListsEveryRoleWhateverItsGrouping()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged(
        [
            new Role { RoleId = 0, PortalId = PortalId, RoleName = "Ungrouped", RoleGroupId = null },
            new Role { RoleId = 1, PortalId = PortalId, RoleName = "Grouped", RoleGroupId = RoleGroupId },
        ]);

        Result<PagedResult<RoleListItemDto>> explicitAll = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            RoleGroupScope.All,
            CancellationToken.None);

        Result<PagedResult<RoleListItemDto>> omitted = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            cancellationToken: CancellationToken.None);

        explicitAll.Value.Items.Select(row => row.RoleId).Should().BeEquivalentTo(new[] { 0, 1 });
        omitted.Value.Items.Select(row => row.RoleId).Should().BeEquivalentTo(
            explicitAll.Value.Items.Select(row => row.RoleId),
            "omitting the scope must mean exactly what naming All means");
    }

    /// <summary>
    /// A scope that is not a defined member of the enumeration is refused, rather than falling through the
    /// narrowing and answering with the unfiltered list.
    /// </summary>
    /// <param name="undefinedScope">A numeric value outside the two defined members.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The review that prompted this test described the gap as an HTTP-reachable input-validation exposure.
    /// </remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(999)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task ListRoles_RefusesAScopeThatIsNotADefinedMember(int undefinedScope)
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged(
        [
            new Role { RoleId = 0, PortalId = PortalId, RoleName = "Ungrouped", RoleGroupId = null },
            new Role { RoleId = 1, PortalId = PortalId, RoleName = "Grouped", RoleGroupId = RoleGroupId },
        ]);

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            (RoleGroupScope)undefinedScope,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue("an undefined scope must not be answered with a page");
        outcome.Reason!.Code.Should().Be("role_group.scope_invalid");
        outcome.Reason!.Message.Should().Contain(
            undefinedScope.ToString(CultureInfo.InvariantCulture),
            "the refusal names the value the caller sent, so the caller can see what was rejected");

        // Refused before anything is read: an undefined scope costs no query at all.
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Roles.Verify(
            r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An undefined scope paired with a group identifier is reported as the undefined scope, not as a
    /// contradiction between two meaningful arguments.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_ReportsAnUndefinedScopeRatherThanAContradiction()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest(),
            RoleGroupId,
            (RoleGroupScope)999,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("role_group.scope_invalid");
        outcome.Reason!.Message.Should().Contain("999");
        outcome.Reason!.Message.Should().NotContain(
            "cannot be combined",
            "an undefined scope is not a contradiction between two meaningful arguments");
    }

    /// <summary>
    /// Both defined members are accepted, so the membership guard bounds the enumeration without narrowing
    /// it.
    /// </summary>
    /// <param name="definedScope">A defined member of the enumeration.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(RoleGroupScope.All)]
    [InlineData(RoleGroupScope.Ungrouped)]
    public async Task ListRoles_AcceptsEveryDefinedScope(RoleGroupScope definedScope)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            definedScope,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A group identifier combined with the ungrouped scope is refused rather than resolved by precedence.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_RefusesAGroupIdentifierCombinedWithTheUngroupedScope()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest(),
            RoleGroupId,
            RoleGroupScope.Ungrouped,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("role_group.scope_invalid");

        // Refused before anything is read, so a contradictory request costs no query at all.
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Roles.Verify(
            r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A group identifier combined with the DEFAULT scope is honoured, because that pairing is what a
    /// caller unaware of the scope sends.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_AcceptsAGroupIdentifierCombinedWithTheDefaultScope()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged(
        [
            new Role { RoleId = 1, PortalId = PortalId, RoleName = "Grouped", RoleGroupId = RoleGroupId },
            new Role { RoleId = 2, PortalId = PortalId, RoleName = "Ungrouped", RoleGroupId = null },
        ]);

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            RoleGroupId,
            RoleGroupScope.All,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Items.Select(row => row.RoleId).Should().Equal(
            new[] { 1 },
            "the identifier still selects its group; the default scope contradicts nothing");
    }

    /// <summary>No group is verified when the caller supplied no group filter.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_DoesNotVerifyAGroupWhenNoneWasAsked()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest(), null, cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Roles.Verify(
            r => r.GetRoleGroupAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Every narrowing the request carried travels to the store in one read, and nothing tenant-wide is
    /// read at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the fact that pins the fix for the role listing's cost. Each argument is checked because one
    /// that failed to travel would be applied by nobody: reading the tenant's whole role set and narrowing
    /// it here produced the same rows for a small tenant, which is exactly why the old shape survived
    /// unnoticed.
    /// </remarks>
    [Fact]
    public async Task ListRoles_PassesEveryNarrowingToTheStoreAndReadsNothingTenantWide()
    {
        Harness harness = Harness.Ready();
        var request = new PagedRequest
        {
            PageIndex = 3,
            PageSize = 25,
            Query = "sub",
            SortBy = "RoleId",
            SortDir = SortDirection.Descending,
        };

        await harness.Service.ListRolesAsync(PortalId, request, RoleGroupId, cancellationToken: CancellationToken.None);

        harness.Roles.Verify(
            r => r.ListAsync(
                PortalId,
                RoleGroupId,
                false,
                "sub",
                "RoleId",
                true,
                3,
                25,
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.Roles.Verify(
            r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An ordering that belongs to another collection is refused before the tenant is even confirmed,
    /// rather than accepted and answered by this listing's default order.
    /// </summary>
    /// <param name="foreignField">A field name declared for a different collection.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Each name passes the shared request validator, which applies the union of every collection's
    /// sortable set because one validator serves the one shared request type. The per-collection set is
    /// therefore what distinguishes a name that means something here from one that does not, and nothing is
    /// read before that question is settled.
    /// </remarks>
    [Theory]
    [InlineData("PortalName")]
    [InlineData("HostFee")]
    [InlineData("HostSpace")]
    [InlineData("DisplayName")]
    [InlineData("LastLoginDate")]
    public async Task ListRoles_RefusesAnOrderingThatBelongsToAnotherCollection(string foreignField)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { SortBy = foreignField },
            null,
            cancellationToken: CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PagingInvalidCode);
        outcome.Reason!.Message.Should().Be($"Roles cannot be ordered by '{foreignField}'.");
        harness.Roles.Verify(
            r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Every field this listing's own ordering honours is accepted, so the allowlist and the ordering
    /// expression behind it agree in both directions.
    /// </summary>
    /// <param name="field">A field name declared for the role listing.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("RoleId")]
    [InlineData("RoleName")]
    [InlineData("Description")]
    [InlineData("ServiceFee")]
    [InlineData("BillingFrequency")]
    [InlineData("BillingPeriod")]
    [InlineData("TrialFee")]
    [InlineData("TrialFrequency")]
    [InlineData("TrialPeriod")]
    [InlineData("IsPublic")]
    [InlineData("AutoAssignment")]
    public async Task ListRoles_AcceptsEveryFieldItsOwnOrderingHonours(string field)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { SortBy = field },
            null,
            cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A permitted ordering actually reorders the answer, in both directions, and is not merely accepted
    /// and then discarded.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the half an allowlist cannot prove on its own. The three roles are given service fees whose
    /// ascending order differs from their name order, so an implementation that accepted the field and then
    /// applied its default order by name would produce the name sequence and fail here.
    /// </remarks>
    [Fact]
    public async Task ListRoles_OrdersByThePermittedFieldInBothDirections()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged(
        [
            NamedRole(1, "Alpha", 30m),
            NamedRole(2, "Bravo", 10m),
            NamedRole(3, "Charlie", 20m),
        ]);

        Result<PagedResult<RoleListItemDto>> ascending = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0, SortBy = "ServiceFee" },
            null,
            cancellationToken: CancellationToken.None);

        ascending.IsSuccess.Should().BeTrue();
        ascending.Value.Items.Select(row => row.RoleId).Should().Equal(2, 3, 1);

        Result<PagedResult<RoleListItemDto>> descending = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0, SortBy = "ServiceFee", SortDir = SortDirection.Descending },
            null,
            cancellationToken: CancellationToken.None);

        descending.Value.Items.Select(row => row.RoleId).Should().Equal(1, 3, 2);
    }

    /// <summary>
    /// A caller that names no ordering still receives the order this listing has always had - role name,
    /// then key - so the enforcement above changes nothing for a caller who expressed no preference.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_OrdersByNameWhenNoOrderingWasNamed()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged(
        [
            NamedRole(1, "Charlie", 30m),
            NamedRole(2, "Alpha", 10m),
            NamedRole(3, "Bravo", 20m),
        ]);

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            cancellationToken: CancellationToken.None);

        outcome.Value.Items.Select(row => row.RoleName).Should().Equal("Alpha", "Bravo", "Charlie");
    }

    /// <summary>A request that asks for no page size receives an unpaged answer.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_ReportsAnUnpagedAnswerWhenNoPageSizeWasAsked()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged([StoredRole(), SecondRole()]);

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, cancellationToken: CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.IsUnpaged.Should().BeTrue();
        outcome.Value.PageSize.Should().Be(0);
        outcome.Value.TotalCount.Should().Be(2);
        outcome.Value.Items.Should().HaveCount(2);
    }

    /// <summary>A paged request keeps the store's total rather than the size of the returned page.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_PreservesTheStoresTotalWhenAPageWasAsked()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged(
            Enumerable.Range(0, 21)
                .Select(index =>
                {
                    Role role = StoredRole();
                    role.RoleId = index;
                    role.RoleName = FormattableString.Invariant($"Role {index:D2}");
                    return role;
                })
                .ToList());

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest { PageIndex = 2, PageSize = 10 }, null, cancellationToken: CancellationToken.None);

        outcome.Value.TotalCount.Should().Be(21, "the total counts every role, not the page returned");
        outcome.Value.PageIndex.Should().Be(2);
        outcome.Value.PageSize.Should().Be(10);
        outcome.Value.TotalPages.Should().Be(3);
        outcome.Value.Items.Should().HaveCount(1, "the third page of ten holds the single remaining role");
        outcome.Value.Items[0].RoleId.Should().Be(20, "the ordering is by name, which here tracks the index");
    }

    /// <summary>
    /// The list projection carries the membership terms but withholds the classification, the subscription
    /// code, the icon reference and the member count, all of which belong to the detail projection.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_ProjectsTheListShapeOnly()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged([StoredRole()]);

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, cancellationToken: CancellationToken.None);

        RoleListItemDto row = outcome.Value.Items.Should().ContainSingle().Which;
        row.RoleId.Should().Be(RoleId);
        row.RoleName.Should().Be(RoleName);
        row.ServiceFee.Should().Be(9.99m);
        row.BillingFrequency.Should().Be(Frequency.Month);
        row.IsPublic.Should().BeTrue();

        string[] declared = typeof(RoleListItemDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        declared.Should().NotContain("RoleGroupId");
        declared.Should().NotContain("RsvpCode");
        declared.Should().NotContain("IconFile");
        declared.Should().NotContain("UserCount");
    }

    /// <summary>Reading one role from a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRole_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<RoleDetailDto?> outcome = await harness.Service
            .GetRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// A role the tenant does not have is reported as absent rather than as a failure, so the caller can
    /// answer a not-found status without inspecting a reason code.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRole_ReportsAbsenceRatherThanFailureForAnUnknownRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = null;

        Result<RoleDetailDto?> outcome = await harness.Service
            .GetRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
        outcome.Reason.Should().BeNull();
    }

    /// <summary>A role belonging to another tenant is indistinguishable from one that does not exist.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRole_ReportsAbsenceForARoleOfAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.LookupRole.PortalId = OtherPortalId;

        Result<RoleDetailDto?> outcome = await harness.Service
            .GetRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// The detail projection carries the identifier of the group that classifies the role, and resolves its
    /// name no further.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRole_CarriesTheClassifyingGroupIdentifierWithoutResolvingItsName()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.LookupRole.RoleGroupId = RoleGroupId;
        harness.LookupGroup = new RoleGroup
        {
            RoleGroupId = RoleGroupId,
            PortalId = PortalId,
            RoleGroupName = RoleGroupName,
        };

        Result<RoleDetailDto?> outcome = await harness.Service
            .GetRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.Value!.RoleGroupId.Should().Be(RoleGroupId);
        harness.Roles.Verify(
            r => r.GetRoleGroupAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>An unclassified role carries a null group identifier and provokes no group lookup.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRole_LeavesTheGroupIdentifierAbsentWhenTheRoleIsUnclassified()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.LookupRole.RoleGroupId = null;

        Result<RoleDetailDto?> outcome = await harness.Service
            .GetRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.Value!.RoleGroupId.Should().BeNull();
        harness.Roles.Verify(
            r => r.GetRoleGroupAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Reading one role reads no page of assignments, because the detail contract carries no member tally
    /// to populate.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRole_ReadsNoAssignmentPageBecauseTheContractCarriesNoMemberTally()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.AssignmentPage = PagedResult<UserRole>.Create(
            [new UserRole { UserRoleId = 1, UserId = UserId, RoleId = RoleId }],
            totalCount: 412,
            pageIndex: 0,
            pageSize: 1);

        Result<RoleDetailDto?> outcome = await harness.Service
            .GetRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.Value!.RoleId.Should().Be(RoleId);
        harness.Roles.Verify(
            r => r.GetUserRolesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Creating a role requires a request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.CreateRoleAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>Creating a role in a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
        harness.AddedRoles.Should().BeEmpty();
    }

    /// <summary>A role may not be classified into a group belonging to another tenant.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_RefusesAGroupFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = new RoleGroup
        {
            RoleGroupId = RoleGroupId,
            PortalId = OtherPortalId,
            RoleGroupName = RoleGroupName,
        };

        CreateRoleRequest request = ValidCreateRequest();
        request.RoleGroupId = RoleGroupId;

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNotFoundCode);
        harness.AddedRoles.Should().BeEmpty();
    }

    /// <summary>A name already held within the tenant is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_RefusesADuplicateName()
    {
        Harness harness = Harness.Ready();
        harness.NameTaken = true;

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNameDuplicateCode);
        outcome.Reason!.Message.Should().Be("A role with the same name already exists. The role was not added.");
        outcome.Reason!.Message.Should().NotContain(
            ClashingRoleId.ToString(CultureInfo.InvariantCulture),
            "a refusal may not publish the row identifier of the record it refers to");
    }

    /// <summary>
    /// A name the store treats as equal to a visibly different stored name is refused with a sentence that
    /// explains the collision and names the stored name, and with no row identifier anywhere in it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// BOTH HALVES MATTER AND THEY PULL AGAINST EACH OTHER, WHICH IS WHY THEY ARE ASSERTED TOGETHER. The
    /// uniqueness index is evaluated under the database's collation, which gives no sort weight to
    /// supplementary-plane characters, zero-width characters or trailing whitespace - so a submitted name
    /// can collide with a stored name that is visibly different from it, and runtime testing recorded an
    /// operator being told a portal already had a role whose name appeared in none of its rows.
    /// </remarks>
    [Fact]
    public async Task CreateRole_RefusesANameTheStoreTreatsAsEqualWithoutNamingItsIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.NameTaken = true;
        harness.ClashingStoredName = "Subscriber\u200bs";

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNameDuplicateCode);
        outcome.Reason!.Message.Should().Contain(
            "Subscriber\u200bs",
            "the operator has to be able to find the row that already holds the name");
        outcome.Reason!.Message.Should().Contain(
            RoleName,
            "the sentence has to say which submitted name the store treated as identical");
        outcome.Reason!.Message.Should().NotContain(
            ClashingRoleId.ToString(CultureInfo.InvariantCulture),
            "a refusal may not publish the row identifier of the record it refers to");
        outcome.Reason!.Message.Should().NotContain(
            PortalId.ToString(CultureInfo.InvariantCulture),
            "nor the tenant identifier, which the legacy wording never carried either");
    }

    /// <summary>
    /// A name taken between the check and the commit is refused with exactly the answer the check gives, so
    /// losing a race is indistinguishable from arriving second.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The check above cannot close this window. <c>IX_RoleName</c> is unique over <c>(PortalID,
    /// RoleName)</c>, so two requests carrying the same name arriving together BOTH read "not taken" and
    /// the loser's insert is refused by the index rather than by the check.
    /// </remarks>
    [Fact]
    public async Task CreateRole_RefusesANameTakenBetweenTheCheckAndTheCommit()
    {
        Harness harness = Harness.Ready();
        harness.UnitOfWork
            .Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(DuplicateKeyException.ForConstraint("IX_RoleName", null));

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue(
            "the store refused the insert, so the creation did not happen and must not be reported as if it "
            + "had");
        outcome.Reason!.Code.Should().Be(
            RoleNameDuplicateCode,
            "the racer faces exactly the state the sequential check describes, so it is told the same thing");
        outcome.Reason!.Message.Should().Be(
            "A role with the same name already exists. The role was not added.",
            "identical wording is what keeps the two paths from drifting into two vocabularies");

        harness.Cache.Verify(
            cache => cache.InvalidatePortal(It.IsAny<int>()),
            Times.Never,
            "nothing was committed, so no cached state became stale");
    }

    /// <summary>The uniqueness check excludes nothing, because no row exists yet to exclude.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_ChecksTheNameAcrossTheWholeTenant()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        harness.Roles.Verify(
            r => r.GetByNameAsync(PortalId, RoleName, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Nothing is written when the name is already taken.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_WritesNothingWhenTheNameIsTaken()
    {
        Harness harness = Harness.Ready();
        harness.NameTaken = true;

        await harness.Service.CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        harness.AddedRoles.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Cache.Verify(c => c.InvalidatePortal(It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// The submitted shape reaches the stored row, and the tenant is taken from the route rather than from
    /// the request body.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_StoresTheSubmittedShape()
    {
        Harness harness = Harness.Ready();
        CreateRoleRequest request = ValidCreateRequest();
        request.Description = "Members who pay monthly.";
        request.RsvpCode = "RSVP-2026";
        request.IconFile = "icon_role.gif";
        request.IsPublic = true;
        request.BillingPeriod = 1;
        request.BillingFrequency = Frequency.Month;
        request.TrialFee = 0m;
        request.TrialPeriod = 14;
        request.TrialFrequency = Frequency.Day;

        await harness.Service.CreateRoleAsync(PortalId, request, CancellationToken.None);

        Role stored = harness.AddedRoles.Should().ContainSingle().Which;
        stored.PortalId.Should().Be(PortalId);
        stored.RoleName.Should().Be(RoleName);
        stored.Description.Should().Be("Members who pay monthly.");
        stored.RsvpCode.Should().Be("RSVP-2026");
        stored.IconFile.Should().Be("icon_role.gif");
        stored.IsPublic.Should().BeTrue();
        stored.ServiceFee.Should().Be(9.99m);
        stored.BillingPeriod.Should().Be(1);
        stored.BillingFrequency.Should().Be(Frequency.Month);
        stored.TrialFee.Should().Be(0m);
        stored.TrialPeriod.Should().Be(14);
        stored.TrialFrequency.Should().Be(Frequency.Day);
    }

    /// <summary>
    /// A negative fee is clamped to nothing on creation rather than refused, because the creation route has
    /// a registered validator that has already rejected a negative value before the service is reached.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_ClampsANegativeFeeRatherThanRefusingIt()
    {
        Harness harness = Harness.Ready();
        CreateRoleRequest request = ValidCreateRequest();
        request.ServiceFee = -5m;
        request.TrialFee = -1m;

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        Role stored = harness.AddedRoles.Should().ContainSingle().Which;
        stored.ServiceFee.Should().Be(0m);
        stored.TrialFee.Should().Be(0m);
    }

    /// <summary>
    /// The creation member performs none of the shape checks the update member performs, because the
    /// creation route carries a registered request validator and the update route does not.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_DoesNotApplyTheUpdatePathsShapeChecks()
    {
        Harness harness = Harness.Ready();
        CreateRoleRequest request = ValidCreateRequest();
        request.RoleName = new string('r', 200);

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedRoles.Should().ContainSingle().Which.RoleName.Should().HaveLength(200);
    }

    /// <summary>
    /// Automatic assignment enrols every existing member of the tenant, unauthorised members included and
    /// installation-wide accounts excluded, attaching each enrolment through the role's navigation so that
    /// no identifier the store has yet to assign is needed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_EnrolsEveryExistingMemberWhenAssignmentIsAutomatic()
    {
        Harness harness = Harness.Ready();
        harness.MemberPage = PagedResult<User>.Unpaged(
        [
            Member(UserId),
            Member(8),
            Member(9),
        ]);

        CreateRoleRequest request = ValidCreateRequest();
        request.AutoAssignment = true;

        await harness.Service.CreateRoleAsync(PortalId, request, CancellationToken.None);

        harness.Users.Verify(
            u => u.ListAsync(
                PortalId,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                true,
                false,
                null,
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.AddedAssignments.Should().HaveCount(3);
        harness.AddedAssignments.Select(a => a.UserId).Should().Equal(new[] { UserId, 8, 9 });
        foreach (UserRole enrolment in harness.AddedAssignments)
        {
            enrolment.Role.Should().BeSameAs(harness.AddedRoles.Single());
            enrolment.RoleId.Should().Be(0, "the identifier arrives through the navigation, not by hand");
            enrolment.EffectiveDate.Should().BeNull();
            enrolment.ExpiryDate.Should().BeNull();
            enrolment.IsTrialUsed.Should().BeFalse();
        }
    }

    /// <summary>
    /// No member is enrolled, and the member store is not even read, when assignment is not automatic.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_EnrolsNobodyWhenAssignmentIsNotAutomatic()
    {
        Harness harness = Harness.Ready();
        CreateRoleRequest request = ValidCreateRequest();
        request.AutoAssignment = false;

        await harness.Service.CreateRoleAsync(PortalId, request, CancellationToken.None);

        harness.AddedAssignments.Should().BeEmpty();
        harness.Users.Verify(
            u => u.ListAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The role and its enrolments commit together, and the tenant's cached state is discarded afterwards.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_CommitsOnceAndInvalidatesTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.MemberPage = PagedResult<User>.Unpaged([Member(UserId)]);
        CreateRoleRequest request = ValidCreateRequest();
        request.AutoAssignment = true;

        await harness.Service.CreateRoleAsync(PortalId, request, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidatePortal(PortalId), Times.Once);
    }

    /// <summary>
    /// A committed creation is recorded on the audit trail under the legacy event name, attributed to the
    /// operator rather than to the role.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_RecordsTheLegacyRoleCreatedAuditEvent()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("ROLE_CREATED");
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.PortalId.Should().Be(PortalId);
        record.ActorUserId.Should().Be(OperatorUserId, "the acting operator comes from the credential");
        record.ResourceType.Should().Be("Role");
        record.Properties.Should().NotContainKey("RoleName", "the resource identifier is the durable attribution");
    }

    /// <summary>
    /// A refused creation writes nothing to the audit trail, so a record never describes a change that did
    /// not happen.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_RecordsNothingWhenTheNameIsTaken()
    {
        Harness harness = Harness.Ready();
        harness.NameTaken = true;

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// The stored row is read back before the answer is built, so the caller receives the identifier the
    /// store assigned rather than the unsaved placeholder.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_ReadsTheStoredRowBackBeforeAnswering()
    {
        Harness harness = Harness.Ready();
        harness.EchoCreatedRole = false;
        harness.LookupRole = StoredRole();
        harness.LookupRole.RoleId = 88;

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.RoleId.Should().Be(88);
    }

    /// <summary>
    /// A row that cannot be read back after the commit is reported rather than answered with a hollow
    /// projection.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_ReportsAFailureWhenTheStoredRowCannotBeReadBack()
    {
        Harness harness = Harness.Ready();
        harness.EchoCreatedRole = false;
        harness.LookupRole = null;

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleCreateFailedCode);
        outcome.Reason!.Message.Should().Be("The role was created but could not be read back.");
    }

    /// <summary>
    /// A role that cannot be read back is still recorded as created, because the row exists whether or not
    /// the response could be composed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// WAITING FOR THE READ-BACK BOUGHT NOTHING. The stated reason was that its identifier is the one the
    /// database assigned - but the change tracker writes the store-generated identity back onto the tracked
    /// entity during the flush, so the identifier is already known. The previous code proved it by passing
    /// that same identifier as the read-back's own argument.
    /// </remarks>
    [Fact]
    public async Task CreateRole_RecordsTheCreationEvenWhenTheStoredRowCannotBeReadBack()
    {
        Harness harness = Harness.Ready();
        harness.EchoCreatedRole = false;
        harness.LookupRole = null;

        Result<RoleDetailDto> outcome = await harness.Service
            .CreateRoleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue("the caller asked for a representation and did not receive one");

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("ROLE_CREATED");
        record.PortalId.Should().Be(PortalId);
        record.ResourceType.Should().Be("Role");
    }

    /// <summary>Updating a role requires a request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateRoleAsync(PortalId, RoleId, null!, CancellationToken.None));
    }

    /// <summary>Updating a role in a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The absence is expressed as a MISSING ROW rather than as a false existence probe, because this
    /// member now READS the tenant: it needs the two designations the protected-role guard compares
    /// against, and one read answers both questions. The existence flag is cleared as well so that the
    /// harness describes one world rather than two contradictory ones.
    /// </remarks>
    [Fact]
    public async Task UpdateRole_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;
        harness.PortalRow = null;

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// neither of the two roles the tenant designates for a system purpose can be amended, and nothing is
    /// staged or committed when one is named.
    /// </summary>
    /// <param name="designateAdministrators">
    /// Whether the tenant designates the named role as its administrators role rather than as its
    /// registered-members role.
    /// </param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateRole_RefusesTheTenantsDesignatedRole(bool designateAdministrators)
    {
        Harness harness = Harness.Ready();

        if (designateAdministrators)
        {
            harness.PortalRow!.AdministratorRoleId = RoleId;
        }
        else
        {
            harness.PortalRow!.RegisteredRoleId = RoleId;
        }

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("role.protected");
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// a role the tenant designates for nothing is amended normally, which is what proves the guard is a
    /// comparison against the tenant's own columns rather than a blanket refusal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_AmendsARoleTheTenantDesignatesForNothing()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.AdministratorRoleId = null;
        harness.PortalRow!.RegisteredRoleId = null;

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Updating a role the tenant does not have is a failure rather than an absence, because the caller
    /// asked to change a named row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_RefusesAnUnknownRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = null;

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} has no role bearing identifier {RoleId}.");
    }

    /// <summary>A role belonging to another tenant cannot be reached through this tenant's route.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_RefusesARoleFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.LookupRole.PortalId = OtherPortalId;

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
    }

    /// <summary>
    /// The update member carries the shape checks the update route's missing validator would otherwise have
    /// performed, and reports each violation with its own measured wording.
    /// </summary>
    /// <param name="violation">The single field to spoil.</param>
    /// <param name="expectedMessage">The message the service is measured to report.</param>
    /// <returns>A task representing the assertion.</returns>
    // MIGRATION: the two name cases are present here as well as on the creation theory, because the update
    // contract carries a writable name.
    [Theory]
    [InlineData("absent-name", "Role Name Is Required.")]
    [InlineData("long-name", "A role name may not exceed 50 characters.")]
    [InlineData("long-description", "A role description may not exceed 1000 characters.")]
    [InlineData("long-rsvp", "A subscription code may not exceed 50 characters.")]
    [InlineData("long-icon", "An icon reference may not exceed 100 characters.")]
    [InlineData("negative-fee", "Service Fee Must Be Greater Than or Equal to Zero")]
    [InlineData("negative-trial-fee", "Trial Fee Must Be Greater Than or Equal to Zero")]
    [InlineData("zero-billing-period", "Billing Period Must Be Greater Than Zero")]
    [InlineData("negative-billing-period", "Billing Period Must Be Greater Than Zero")]
    [InlineData("zero-trial-period", "Trial Period Must Be Greater Than Zero")]
    [InlineData("negative-trial-period", "Trial Period Must Be Greater Than Zero")]
    [InlineData("undefined-billing-frequency", "The billing frequency is not one of the recognised codes.")]
    [InlineData("undefined-trial-frequency", "The trial frequency is not one of the recognised codes.")]
    public async Task UpdateRole_RefusesAMalformedShape(string violation, string expectedMessage)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        UpdateRoleRequest request = ValidUpdateRequest();
        switch (violation)
        {
            case "absent-name":
                request.RoleName = "   ";
                break;
            case "long-name":
                request.RoleName = new string('n', 51);
                break;
            case "long-description":
                request.Description = new string('d', 1001);
                break;
            case "long-rsvp":
                request.RsvpCode = new string('c', 51);
                break;
            case "long-icon":
                request.IconFile = new string('i', 101);
                break;
            case "negative-fee":
                request.ServiceFee = -0.01m;
                break;
            case "negative-trial-fee":
                request.TrialFee = -0.01m;
                break;
            case "zero-billing-period":
                request.BillingPeriod = 0;
                request.BillingFrequency = Frequency.Month;
                break;
            case "negative-billing-period":
                request.BillingPeriod = -3;
                break;
            case "zero-trial-period":
                request.TrialPeriod = 0;
                request.TrialFrequency = Frequency.Month;
                break;
            case "negative-trial-period":
                request.TrialPeriod = -3;
                break;
            case "undefined-billing-frequency":
                request.BillingFrequency = (Frequency)0;
                break;
            default:
                request.TrialFrequency = (Frequency)0;
                break;
        }

        DomainException refusal = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateRoleAsync(PortalId, RoleId, request, CancellationToken.None));

        refusal.Message.Should().Be(expectedMessage);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The shape is checked before the classification, so a request that is malformed in several ways
    /// reports the shape first and reads nothing further.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_ChecksTheShapeBeforeTheGroup()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.LookupGroup = null;

        UpdateRoleRequest request = ValidUpdateRequest();
        request.Description = new string('d', 1001);
        request.RoleGroupId = RoleGroupId;

        await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateRoleAsync(PortalId, RoleId, request, CancellationToken.None));

        harness.Roles.Verify(
            r => r.GetRoleGroupAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The update route reads name uniqueness, because the name it can now change is covered by a unique
    /// constraint over the portal and the name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_ReadsNameUniqueness()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        await harness.Service.UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        harness.Roles.Verify(
            r => r.GetByNameAsync(
                PortalId,
                RoleName,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// An update that names a role ANOTHER role in the portal already holds is refused as a duplicate, with
    /// the same code the creation route reports.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_RefusesANameAnotherRoleAlreadyHolds()
    {
        Harness harness = Harness.Ready();
        Role tracked = StoredRole();
        harness.LookupRole = tracked;

        // The harness answers a taken name with a row bearing a DIFFERENT identifier, which is precisely
        // the case the exclusion must not absorb.
        harness.NameTaken = true;

        UpdateRoleRequest request = ValidUpdateRequest();
        request.RoleName = "Contributors";

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(RoleNameDuplicateCode);

        // Nothing was written: the refusal precedes the projection and the commit.
        tracked.RoleName.Should().Be(RoleName);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// An update that resubmits the role's OWN name succeeds, because the uniqueness comparison excludes
    /// the role being edited.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_AcceptsTheRolesOwnNameResubmitted()
    {
        Harness harness = Harness.Ready();
        Role tracked = StoredRole();
        harness.LookupRole = tracked;

        // The stored row itself is what the name lookup finds, which is what an unchanged name means.
        harness.Roles
            .Setup(r => r.GetByNameAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tracked);

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull();
        tracked.RoleName.Should().Be(RoleName);
        outcome.Value.RoleName.Should().Be(RoleName);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The submitted shape is applied to the tracked row, and the tenant assignment is left alone.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_AppliesTheSubmittedShape()
    {
        Harness harness = Harness.Ready();
        Role tracked = StoredRole();
        harness.LookupRole = tracked;

        UpdateRoleRequest request = ValidUpdateRequest();
        request.RoleName = "Renamed subscribers";
        request.Description = "Changed.";
        request.IsPublic = false;
        request.AutoAssignment = true;
        request.ServiceFee = 19.5m;
        request.BillingPeriod = 2;
        request.BillingFrequency = Frequency.Year;
        request.RsvpCode = "NEW";
        request.IconFile = "new.gif";

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        // MIGRATION: the name IS replaced, because the update contract carries one and the projection
        // applies it - a documented behavioural difference from the legacy edit screen, which displayed the
        // name read-only.
        tracked.RoleName.Should().Be("Renamed subscribers");
        tracked.Description.Should().Be("Changed.");
        tracked.IsPublic.Should().BeFalse();
        tracked.AutoAssignment.Should().BeTrue();
        tracked.ServiceFee.Should().Be(19.5m);
        tracked.BillingPeriod.Should().Be(2);
        tracked.BillingFrequency.Should().Be(Frequency.Year);
        tracked.RsvpCode.Should().Be("NEW");
        tracked.IconFile.Should().Be("new.gif");
        tracked.PortalId.Should().Be(PortalId);
        outcome.Value.RoleName.Should().Be("Renamed subscribers");
    }

    /// <summary>The change commits once and the tenant's cached state is discarded.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_CommitsAndInvalidatesTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        await harness.Service.UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidatePortal(PortalId), Times.Once);
    }

    /// <summary>
    /// The designation read, the token comparison, the rename guard and the write are one serialisable
    /// transaction, and it commits.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The isolation is asserted rather than merely the presence of a scope, because the level is the whole
    /// point: this is a read-modify-write of the row it judged, and its rename guard asks a question about
    /// the tenant's other roles whose answer must still hold when the write lands.
    /// </remarks>
    [Fact]
    public async Task UpdateRole_JudgesAndWritesInOneSerialisableTransaction()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        Result<RoleDetailDto> outcome = await harness.Service.UpdateRoleAsync(
            PortalId,
            RoleId,
            ValidUpdateRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.Isolation.Should().Be(TransactionIsolation.Serializable);
        scope.Committed.Should().BeTrue();
    }

    /// <summary>
    /// A refusal raised before the write leaves the scope abandoned rather than committed, so nothing the
    /// judged sequence read or staged becomes durable.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The duplicate-name refusal is used because it is the LAST guard before the mapping, so it proves the
    /// rollback covers the whole judged sequence rather than only its opening reads.
    /// </remarks>
    [Fact]
    public async Task UpdateRole_WhenRefused_AbandonsTheTransaction()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        // The harness answers a taken name with a row bearing a DIFFERENT identifier, which is the shape the
        // exclusion must not absorb - the same setup the dedicated duplicate-name fact above uses.
        harness.NameTaken = true;
        UpdateRoleRequest request = ValidUpdateRequest();
        request.RoleName = "Contributors";

        Result<RoleDetailDto> outcome = await harness.Service.UpdateRoleAsync(
            PortalId,
            RoleId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(RoleNameDuplicateCode);
        harness.OpenedTransactions.Should().ContainSingle().Which.RolledBack.Should().BeTrue();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Cache.Verify(c => c.InvalidatePortal(It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// A write the store itself refuses as a lost update is reported as the same conflict a stale
    /// concurrency token reports, and nothing is evicted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The token comparison closes the window a caller can OBSERVE; it cannot close the window between that
    /// comparison and the write, and under serialisable isolation this participant can also be aborted as a
    /// deadlock victim. Both are the same event from the caller's position - the record moved and nothing
    /// was written - so both must produce the refusal the stale token produces rather than a server fault.
    /// </remarks>
    [Fact]
    public async Task UpdateRole_ReportsAStoreRefusedLostUpdateAsAConcurrencyConflict()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.UnitOfWork
            .Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(ConcurrencyConflictException.ForLostUpdate(null));

        Result<RoleDetailDto> outcome = await harness.Service.UpdateRoleAsync(
            PortalId,
            RoleId,
            ValidUpdateRequest(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("role.concurrency_conflict");
        outcome.Error.Message.Should().Contain(
            "changed by someone else",
            "the store-refused loser reads the same sentence the stale-token loser reads");
        harness.OpenedTransactions.Should().ContainSingle().Which.RolledBack.Should().BeTrue();
        harness.Cache.Verify(c => c.InvalidatePortal(It.IsAny<int>()), Times.Never);
    }

    /// <summary>The answer is projected from the tracked row rather than from a second read of the store.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_DoesNotReReadTheRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        await harness.Service.UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        harness.Roles.Verify(
            r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>Deleting a role in a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Expressed as a missing ROW for the reason recorded on the update path - the member reads the tenant
    /// now, because the protected-role designations live on it.
    /// </remarks>
    [Fact]
    public async Task DeleteRole_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;
        harness.PortalRow = null;

        Result outcome = await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// neither of the two roles the tenant designates for a system purpose can be removed, the store is
    /// left untouched, and the refusal names the purpose that protects the role.
    /// </summary>
    /// <param name="designateAdministrators">
    /// Whether the tenant designates the named role as its administrators role rather than as its
    /// registered-members role.
    /// </param>
    /// <param name="expectedPurpose">The purpose the refusal must name, so the operator can act on it.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Nothing reaching the store is the assertion that matters most here. The removal cascades to every
    /// assignment of the role, so a guard that refused the caller AFTER staging would still dispossess
    /// every administrator of the tenant the moment anything else committed in the same scope.
    /// </remarks>
    [Theory]
    [InlineData(true, "administrators")]
    [InlineData(false, "registered members")]
    public async Task DeleteRole_RefusesTheTenantsDesignatedRole(
        bool designateAdministrators,
        string expectedPurpose)
    {
        Harness harness = Harness.Ready();

        if (designateAdministrators)
        {
            harness.PortalRow!.AdministratorRoleId = RoleId;
        }
        else
        {
            harness.PortalRow!.AdministratorRoleId = OtherRoleId;
            harness.PortalRow!.RegisteredRoleId = RoleId;
        }

        Result outcome = await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("role.protected");
        outcome.Reason!.Message.Should().Contain(expectedPurpose);
        harness.RemovedRoles.Should().BeEmpty("a protected role never reaches the store");
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Deleting a role the tenant does not have is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRole_RefusesAnUnknownRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = null;

        Result outcome = await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
    }

    /// <summary>Nothing is written when the role is unknown.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRole_WritesNothingWhenTheRoleIsUnknown()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = null;

        await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        harness.RemovedRoles.Should().BeEmpty();
        harness.RemovedAssignments.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Every assignment is detached before the role itself is removed, and the assignment list is read
    /// unpaged so that no member is left holding a role that has gone.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRole_DetachesEveryAssignmentBeforeRemovingTheRole()
    {
        Harness harness = Harness.Ready();
        Role tracked = StoredRole();
        harness.LookupRole = tracked;
        harness.AssignmentPage = PagedResult<UserRole>.Unpaged(
        [
            new UserRole { UserRoleId = 1, UserId = UserId, RoleId = RoleId },
            new UserRole { UserRoleId = 2, UserId = 8, RoleId = RoleId },
        ]);

        Result outcome = await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        // MIGRATION: the assignments are no longer swept row by row. FK_UserRoles_Roles is declared ON
        // DELETE CASCADE and UserRoleConfiguration declares the same behaviour, so the single keyed delete
        // carries them - and issuing the deletes here as well would issue them twice.
        harness.Roles.Verify(
            r => r.DeleteAsync(RoleId, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Roles.Verify(
            r => r.DeleteUserRoleAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.RemovedRoleIds.Should().Equal(RoleId);
        harness.RemovedRoles.Should().ContainSingle().Which.Should().BeSameAs(tracked);
    }

    /// <summary>
    /// The removal commits once, discards the tenant's cached state, and hands the grant-cache eviction to
    /// the contract that owns it rather than evicting one of the two affected entries itself.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRole_CommitsOnceAndDelegatesTheGrantCacheEviction()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidatePortal(PortalId), Times.Once);
        harness.Permissions.Verify(
            permissions => permissions.InvalidateUserPermissionCaches(),
            Times.Once);

        // Not evicted directly, because the delegated member evicts it - a direct call as well would be a
        // second definition of the same set.
        harness.Cache.Verify(c => c.InvalidateTabPermissions(It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// every grant the role held is swept, and the sweep happens before the role row goes, inside the same
    /// committed transaction.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The whole of the finding is here. The grant tables carry no cascading foreign key to the role table,
    /// so the rows survive their principal; <c>Roles.RoleID</c> is an identity column, so the vacated
    /// identifier is reissued; and the next role to take it inherits authority nobody granted it.
    /// </remarks>
    [Fact]
    public async Task DeleteRole_SweepsTheRolesGrantsBeforeItsRowInsideOneCommittedTransaction()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        Result outcome = await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.Permissions.Verify(
            permissions => permissions.StageRolePermissionRemovalAsync(
                PortalId,
                RoleId,
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.Steps.Should().Equal(
            [
                "transaction.begin",
                "permissions.sweep",
                "roles.delete",
                "unitOfWork.save",
                "transaction.commit",
                "audit.ROLE_DELETED",
                "permissions.evict",
            ],
            "the grants go before the role, both inside one scope; the record is the FIRST thing after the "
            + "commit, so it cannot be lost to the cancellable eviction that follows it");

        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.Committed.Should().BeTrue();
        scope.Isolation.Should().Be(
            TransactionIsolation.Default,
            "nothing this removal read has to stay unchanged - the designation it checks is written only "
            + "during portal provisioning - so the stronger isolation the tenant removal needs is not "
            + "needed here");
    }

    /// <summary>
    /// the sweep is the stage-only member, so a role removal that cannot be committed leaves the grants in
    /// place.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the reason the removal opens a transaction at all. The sweep reaches the store as set-based
    /// statements the moment it is issued, so without one it would be durable on its own, and a failing
    /// commit would leave the role present with every grant gone - strictly worse than the fault being
    /// repaired, because grants cannot be reconstructed.
    /// </remarks>
    [Fact]
    public async Task DeleteRole_WhenTheCommitFails_AbandonsTheSweepWithTheRemoval()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.UnitOfWork
            .Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the store refused the batch"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None));

        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.RolledBack.Should().BeTrue("the grants and the role stand or fall together");

        harness.Permissions.Verify(
            permissions => permissions.InvalidateUserPermissionCaches(),
            Times.Never);

        harness.AuditRecords.Should().BeEmpty(
            "no record may describe a removal that was rolled back");
    }

    /// <summary>a sweep that refuses is reported and the role survives.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRole_WhenTheSweepRefuses_ReportsItAndRemovesNothing()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.GrantSweep = Result.Failure("permission.role_not_found", "the sweep declined");

        Result outcome = await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("permission.role_not_found");

        harness.RemovedRoleIds.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.OpenedTransactions.Should().ContainSingle().Which.RolledBack.Should().BeTrue();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>a refusal that precedes the sweep neither opens a transaction nor touches a grant.</summary>
    /// <param name="designatedRoleId">The identifier the tenant designates.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(OtherRoleId)]
    [InlineData(OtherRoleId + 1)]
    public async Task DeleteRole_WhenRefusedBeforeTheSweep_OpensNoTransactionAndSweepsNothing(int designatedRoleId)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        Result outcome = await harness.Service.DeleteRoleAsync(
            PortalId,
            designatedRoleId,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        harness.OpenedTransactions.Should().BeEmpty();
        harness.Permissions.Verify(
            permissions => permissions.StageRolePermissionRemovalAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A committed removal is recorded under the legacy event name and stable role identifier without
    /// copying the deleted role's caller-authored name into the logging store.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRole_RecordsTheLegacyRoleDeletedAuditEventByIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("ROLE_DELETED");
        record.PortalId.Should().Be(PortalId);
        record.ResourceType.Should().Be("Role");
        record.ResourceId.Should().Be(RoleId.ToString(CultureInfo.InvariantCulture));
        record.Properties.Should().NotContainKey("RoleName");
    }

    /// <summary>
    /// A committed removal is recorded even when the post-commit grant-cache eviction fails, because the
    /// role and every assignment to it are gone either way.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The exception still escapes: an eviction that did not happen means stale grants may be served from
    /// memory, which is a real condition and must not be swallowed. Only the ORDER changed.
    /// </remarks>
    [Fact]
    public async Task DeleteRole_RecordsTheRemovalEvenWhenThePostCommitEvictionFails()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.GrantCacheEvictionFault = new OperationCanceledException("the caller disconnected");

        Func<Task> removal = () => harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        await removal.Should().ThrowAsync<OperationCanceledException>(
            "maintenance that did not happen is a real condition and must not be swallowed");

        harness.OpenedTransactions.Should().ContainSingle().Which.Committed.Should().BeTrue(
            "the removal was committed before the eviction ran");

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("ROLE_DELETED");
        record.ResourceId.Should().Be(RoleId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Granting a role records the legacy membership event, naming both the operator and the member.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task AssignUser_RecordsTheLegacyUserRoleCreatedAuditEvent()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.MembersById[UserId] = Member(UserId);

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be("USER_ROLE_CREATED");
        record.ActorUserId.Should().Be(OperatorUserId);
        record.SubjectUserId.Should().Be(UserId, "the member acted upon is not the operator acting");
        record.ResourceType.Should().Be("UserRole");
        record.Properties["Renewed"].Should().Be("False");
    }

    /// <summary>Listing a role's members requires a paging request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ListRoleUsersAsync(PortalId, RoleId, null!, CancellationToken.None));
    }

    /// <summary>Listing a role's members in a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>The exact-identifier membership read answers the pairing itself, with the terms it runs on.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRoleMembership_AnswersThePairingWithoutReadingTheListing()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.Member = Member(UserId);
        harness.ExistingAssignment = Membership(
            UserId,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));

        Result<RoleMembershipDto?> outcome = await harness.Service
            .GetRoleMembershipAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        RoleMembershipDto row = outcome.Value.Should().NotBeNull().And.Subject.As<RoleMembershipDto>();

        row.UserId.Should().Be(UserId);
        row.RoleId.Should().Be(RoleId);
        row.Username.Should().Be(MemberName);
        row.DisplayName.Should().Be("Ada Lovelace");
        row.RoleName.Should().Be(RoleName);
        row.EffectiveDate.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        row.ExpiryDate.Should().Be(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));

        harness.Roles.Verify(
            r => r.ListRoleMembershipsAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An account that holds no membership of the role is a SUCCESSFUL outcome carrying no value, not a
    /// failure.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRoleMembership_ReportsNoMembershipAsASuccessCarryingNothing()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.Member = Member(UserId);
        harness.ExistingAssignment = null;

        Result<RoleMembershipDto?> outcome = await harness.Service
            .GetRoleMembershipAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// Each unknown identifier is reported under its own code, so a caller can tell the three apart from
    /// each other and all three from "holds nothing".
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRoleMembership_ReportsEachUnknownIdentifierUnderItsOwnCode()
    {
        Harness absentPortal = Harness.Ready();
        absentPortal.PortalExists = false;

        Result<RoleMembershipDto?> withoutPortal = await absentPortal.Service
            .GetRoleMembershipAsync(PortalId, RoleId, UserId, CancellationToken.None);

        withoutPortal.IsFailure.Should().BeTrue();
        withoutPortal.Reason!.Code.Should().Be(PortalNotFoundCode);

        Harness absentRole = Harness.Ready();
        absentRole.LookupRole = null;

        Result<RoleMembershipDto?> withoutRole = await absentRole.Service
            .GetRoleMembershipAsync(PortalId, RoleId, UserId, CancellationToken.None);

        withoutRole.IsFailure.Should().BeTrue();
        withoutRole.Reason!.Code.Should().Be(RoleNotFoundCode);

        Harness absentMember = Harness.Ready();
        absentMember.LookupRole = StoredRole();
        absentMember.Member = null;

        Result<RoleMembershipDto?> withoutMember = await absentMember.Service
            .GetRoleMembershipAsync(PortalId, RoleId, UserId, CancellationToken.None);

        withoutMember.IsFailure.Should().BeTrue();
        withoutMember.Reason!.Code.Should().Be(UserNotFoundCode);
    }

    /// <summary>
    /// A role belonging to another tenant is not readable through this member, so the pairing address
    /// cannot be used to read across a tenant boundary.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRoleMembership_RefusesARoleBelongingToAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = new Role { RoleId = RoleId, PortalId = PortalId + 7, RoleName = RoleName };
        harness.Member = Member(UserId);
        harness.ExistingAssignment = Membership(UserId);

        Result<RoleMembershipDto?> outcome = await harness.Service
            .GetRoleMembershipAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
    }

    /// <summary>
    /// An ordering outside this listing's own set is refused, including one the account listing would have
    /// no arm for either.
    /// </summary>
    /// <param name="foreignField">A field name declared for a different collection, or for none.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The membership set is wider than the account listing's - which is empty - because this listing
    /// materialises the role's members and pages them in memory, so it can order what it holds.
    /// </remarks>
    [Theory]
    [InlineData("RoleId")]
    [InlineData("PortalId")]
    [InlineData("EffectiveDate")]
    [InlineData("ExpiryDate")]
    [InlineData("IsOnline")]
    public async Task ListRoleUsers_RefusesAnOrderingOutsideItsOwnSet(string foreignField)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service.ListRoleUsersAsync(
            PortalId,
            RoleId,
            new PagedRequest { SortBy = foreignField },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PagingInvalidCode);
        outcome.Reason!.Message.Should().Be($"Role members cannot be ordered by '{foreignField}'.");
        harness.Users.Verify(
            u => u.ListByRoleNameAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Every field the membership set names is accepted, including the three read from the external
    /// membership store, which are orderable only because this listing pages in memory.
    /// </summary>
    /// <param name="field">A field name declared for the membership listing.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("UserId")]
    [InlineData("Username")]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("DisplayName")]
    [InlineData("Email")]
    [InlineData("IsSuperUser")]
    public async Task ListRoleUsers_AcceptsEveryFieldItsOwnOrderingHonours(string field)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Membership(UserId)];

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service.ListRoleUsersAsync(
            PortalId,
            RoleId,
            new PagedRequest { PageSize = 0, SortBy = field },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A permitted ordering reorders the members before they are paged, so the ordering decides which
    /// member lands on which page rather than merely rearranging one page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_OrdersTheMembersBeforePaging()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers =
        [
            Membership(1, displayName: "Charlie"),
            Membership(2, displayName: "Alpha"),
            Membership(3, displayName: "Bravo"),
        ];

        Result<PagedResult<RoleMembershipDto>> first = await harness.Service.ListRoleUsersAsync(
            PortalId,
            RoleId,
            new PagedRequest { PageSize = 2, SortBy = "DisplayName" },
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.Items.Select(row => row.UserId).Should().Equal(2, 3);
        first.Value.TotalCount.Should().Be(3);

        Result<PagedResult<RoleMembershipDto>> second = await harness.Service.ListRoleUsersAsync(
            PortalId,
            RoleId,
            new PagedRequest
            {
                PageIndex = 1,
                PageSize = 2,
                SortBy = "DisplayName",
                SortDir = SortDirection.Descending,
            },
            CancellationToken.None);

        second.Value.Items.Select(row => row.UserId).Should().Equal(2);
    }

    /// <summary>Listing the members of a role the tenant does not have is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_RefusesAnUnknownRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = null;

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
    }

    /// <summary>The members are read within the tenant and by the role's own name, never installation-wide.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_ResolvesEachMemberWithinTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Membership(UserId)];

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.Roles.Verify(
            r => r.ListRoleMembershipsAsync(
                PortalId,
                RoleName,
                null,
                null,
                false,
                0,
                0,
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Roles.Verify(
            r => r.GetUserRolesByUsernameAsync(
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // No read-per-row remains: the account and the role arrive composed with the assignment.
        harness.Users.Verify(
            u => u.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        RoleMembershipDto row = outcome.Value.Items.Should().ContainSingle().Which;
        row.UserId.Should().Be(UserId);
        row.Username.Should().Be(MemberName);
        row.RoleId.Should().Be(RoleId);
        row.RoleName.Should().Be(RoleName);
    }

    /// <summary>An account that is not a member of the tenant never reaches the projection at all.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_ReportsOnlyTheAccountsTheTenantReadidReturns()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Membership(UserId)];

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        outcome.Value.Items.Should().ContainSingle().Which.UserId.Should().Be(UserId);
    }

    /// <summary>The reported total counts every member of the role, not just the page that was returned.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_ReportsTheTotalIndependentlyOfThePageSize()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Membership(UserId), Membership(8), Membership(9)];

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 2 }, CancellationToken.None);

        outcome.Value.Items.Select(row => row.UserId).Should().Equal(new[] { UserId, 8 });
        outcome.Value.TotalCount.Should().Be(3, "the total counts every member, not the page");
        outcome.Value.PageIndex.Should().Be(0);
        outcome.Value.PageSize.Should().Be(2);
        outcome.Value.HasNextPage.Should().BeTrue();

        Result<PagedResult<RoleMembershipDto>> second = await harness.Service
            .ListRoleUsersAsync(
                PortalId,
                RoleId,
                new PagedRequest { PageIndex = 1, PageSize = 2 },
                CancellationToken.None);

        second.Value.Items.Select(row => row.UserId).Should().Equal(9);
        second.Value.TotalCount.Should().Be(3);
        second.Value.HasNextPage.Should().BeFalse();
    }

    /// <summary>The membership rows carry the effective and expiry dates the legacy grid rendered.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_CarriesTheMembershipDates()
    {
        DateTime effective = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime expiry = new(2024, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Membership(UserId, effective, expiry)];

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        RoleMembershipDto row = outcome.Value.Items.Should().ContainSingle().Which;
        row.EffectiveDate.Should().Be(effective);
        row.ExpiryDate.Should().Be(expiry);
        row.DisplayName.Should().Be("Ada Lovelace", "the display name is the value the legacy grid showed");
    }

    /// <summary>An open-ended membership reports both dates as absent rather than as a sentinel date.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_ReportsAnOpenEndedMembershipAsAbsentDates()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Membership(UserId)];

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        RoleMembershipDto row = outcome.Value.Items.Should().ContainSingle().Which;
        row.EffectiveDate.Should().BeNull();
        row.ExpiryDate.Should().BeNull();
        row.UserRoleId.Should().Be(UserId + 1000, "the assignment carries its own identity");
    }

    /// <summary>
    /// A request that asks for no page size receives an unpaged answer, and the assignment store is asked
    /// for an unpaged page too.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_ReportsAnUnpagedAnswerWhenNoPageSizeWasAsked()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Membership(UserId)];

        Result<PagedResult<RoleMembershipDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        outcome.Value.IsUnpaged.Should().BeTrue();

        harness.Roles.Verify(
            r => r.ListRoleMembershipsAsync(
                PortalId,
                RoleName,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<int>(),
                0,
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.Users.Verify(
            u => u.ListByRoleNameAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Listing a member's roles in a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUserRoles_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<IReadOnlyList<RoleListItemDto>> outcome = await harness.Service
            .ListUserRolesAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>Listing the roles of an account that is not a member of the tenant is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUserRoles_RefusesAnAccountThatIsNotAMember()
    {
        Harness harness = Harness.Ready();
        harness.Member = null;

        Result<IReadOnlyList<RoleListItemDto>> outcome = await harness.Service
            .ListUserRolesAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(UserNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} has no member bearing identifier {UserId}.");
    }

    /// <summary>
    /// Only roles the tenant owns are projected, so an assignment to a role of another tenant is dropped
    /// rather than leaking that tenant's role name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUserRoles_ProjectsOnlyRolesTheTenantOwns()
    {
        Harness harness = Harness.Ready();
        harness.UserAssignments =
        [
            new UserRole { UserRoleId = 1, UserId = UserId, RoleId = RoleId },
            new UserRole { UserRoleId = 2, UserId = UserId, RoleId = 999 },
        ];
        harness.RolePage = PagedResult<Role>.Unpaged([StoredRole()]);

        Result<IReadOnlyList<RoleListItemDto>> outcome = await harness.Service
            .ListUserRolesAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().ContainSingle().Which.RoleId.Should().Be(RoleId);
    }

    /// <summary>
    /// The tenant's roles are read unpaged and unclassified, so the projection can resolve an assignment to
    /// any role the tenant owns.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUserRoles_ReadsTheTenantsRolesUnpaged()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListUserRolesAsync(PortalId, UserId, CancellationToken.None);

        harness.Roles.Verify(
            r => r.GetByPortalIdAsync(PortalId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>A member holding nothing receives an empty list rather than a failure.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListUserRoles_ReportsAnEmptyAnswerForAMemberHoldingNothing()
    {
        Harness harness = Harness.Ready();
        harness.UserAssignments = [];

        Result<IReadOnlyList<RoleListItemDto>> outcome = await harness.Service
            .ListUserRolesAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeEmpty();
    }

    /// <summary>Assigning a member to a role requires a request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.AssignUserToRoleAsync(PortalId, RoleId, null!, CancellationToken.None));
    }

    /// <summary>Assigning within a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, new RoleAssignmentRequest { UserId = UserId }, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>Assigning to a role the tenant does not have is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_RefusesAnUnknownRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = null;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, new RoleAssignmentRequest { UserId = UserId }, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
    }

    /// <summary>Assigning an account that is not a member of the tenant is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_RefusesAnAccountThatIsNotAMember()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.Member = null;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, new RoleAssignmentRequest { UserId = UserId }, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(UserNotFoundCode);
        harness.AddedAssignments.Should().BeEmpty();
    }

    /// <summary>A member who holds nothing yet receives a new assignment naming the route's role.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_CreatesAnAssignmentWhenNoneExists()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();
        harness.ExistingAssignment = null;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, new RoleAssignmentRequest { UserId = UserId }, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole created = harness.AddedAssignments.Should().ContainSingle().Which;
        created.UserId.Should().Be(UserId);
        created.RoleId.Should().Be(RoleId);
        created.IsTrialUsed.Should().BeFalse();
    }

    /// <summary>An existing assignment is amended in place rather than replaced by a second row.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_UpdatesTheDatesOfAnExistingAssignmentWithoutReplacingIt()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = MonthlyRole();
        var existing = new UserRole
        {
            UserRoleId = 5,
            UserId = UserId,
            RoleId = RoleId,
            EffectiveDate = Now.AddYears(-2),
            ExpiryDate = Now.AddYears(-1),
        };
        harness.ExistingAssignment = existing;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, new RoleAssignmentRequest { UserId = UserId }, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedAssignments.Should().BeEmpty();
        existing.ExpiryDate.Should().Be(Now.AddMonths(1));
    }

    /// <summary>Amending an assignment leaves the trial flag exactly as the store recorded it.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_LeavesTheTrialFlagAloneWhenUpdating()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = MonthlyRole();
        var existing = new UserRole
        {
            UserRoleId = 5,
            UserId = UserId,
            RoleId = RoleId,
            IsTrialUsed = true,
        };
        harness.ExistingAssignment = existing;

        await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, new RoleAssignmentRequest { UserId = UserId }, CancellationToken.None);

        existing.IsTrialUsed.Should().BeTrue();
    }

    /// <summary>The assignment commits once and the member's cached state is discarded by name.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_CommitsAndInvalidatesTheMember()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();

        await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, new RoleAssignmentRequest { UserId = UserId }, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidateUser(PortalId, MemberName), Times.Once);
    }

    /// <summary>
    /// A stored term whose offset runs past the calendar the column can hold yields the perpetual expiry
    /// rather than an arithmetic fault.
    /// </summary>
    /// <param name="frequency">The billing frequency under test.</param>
    /// <param name="period">The stored period, chosen to overrun the calendar at that frequency.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Each row below reached the framework's date arithmetic directly before this work, and each failed
    /// there rather than here. The daily, monthly and yearly rows raised an out-of-range fault, which
    /// surfaced as a server error naming no field.
    /// </remarks>
    [Theory]
    [InlineData(Frequency.Day, int.MaxValue)]
    [InlineData(Frequency.Week, 400_000_000)]
    [InlineData(Frequency.Month, 200_000)]
    [InlineData(Frequency.Year, 20_000)]
    public async Task Assign_ClampsATermThatOutrunsTheStoredCalendar(Frequency frequency, int period)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = new Role
        {
            RoleId = RoleId,
            PortalId = PortalId,
            RoleName = RoleName,
            ServiceFee = 1m,
            BillingPeriod = period,
            BillingFrequency = frequency,
        };

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("an overrunning term is clamped, not rejected");

        DateTime? storedExpiry = harness.AddedAssignments.Should().ContainSingle().Subject.ExpiryDate;

        storedExpiry.Should().Be(
            new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            "a term that outruns the calendar is recorded as the perpetual expiry");
        storedExpiry.Should().BeAfter(Now, "the expiry must never be moved into the past");
    }

    /// <summary>
    /// An ordinary term is still offset exactly as it was, so the clamp changed nothing reachable in normal
    /// use.
    /// </summary>
    /// <param name="frequency">The billing frequency under test.</param>
    /// <param name="period">The stored period.</param>
    /// <param name="expectedDaysAhead">The offset the derived expiry must land at, in whole days.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(Frequency.Day, 30, 30)]
    [InlineData(Frequency.Week, 2, 14)]
    [InlineData(Frequency.Month, 1, 31)]
    [InlineData(Frequency.Year, 1, 365)]
    public async Task Assign_OffsetsAnOrdinaryTermExactlyAsBefore(
        Frequency frequency,
        int period,
        int expectedDaysAhead)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = new Role
        {
            RoleId = RoleId,
            PortalId = PortalId,
            RoleName = RoleName,
            ServiceFee = 1m,
            BillingPeriod = period,
            BillingFrequency = frequency,
        };

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Subject.ExpiryDate.Should().Be(
            Now.AddDays(expectedDaysAhead),
            "the offset for an ordinary term is unchanged");
    }

    /// <summary>
    /// an effective date already in the past is STORED AS SUBMITTED, because a start date is a caller's
    /// instruction and a backdated grant is a legitimate one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_StoresAnEffectiveDateAlreadyInThePastAsSubmitted()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();
        DateTime backdated = Now.AddDays(-5);

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, EffectiveDate = backdated },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.EffectiveDate.Should().Be(backdated);
    }

    /// <summary>
    /// An effective date in the future is kept, because a deferred start is a legitimate instruction.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_KeepsAnEffectiveDateInTheFuture()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();
        DateTime start = Now.AddDays(30);

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, EffectiveDate = start },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.EffectiveDate.Should().Be(start);
    }

    /// <summary>
    /// A submitted bound carrying the legacy absent-date marker is read as "no bound" rather than as a real
    /// instant at the dawn of the calendar.
    /// </summary>
    /// <param name="hours">
    /// Hours to add to the marker, because the legacy emptiness test compared date parts only and a value
    /// copied out of a legacy object may carry a time component.
    /// </param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(23)]
    public async Task Assign_ReadsTheLegacyAbsentDateMarkerAsNoBound(int hours)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();

        DateTime marker = DateTime.MinValue.AddHours(hours);

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, EffectiveDate = marker, ExpiryDate = marker },
            CancellationToken.None);

        UserRole staged = harness.AddedAssignments.Should().ContainSingle().Subject;

        staged.EffectiveDate.Should().BeNull(
            "the marker means absence, and absence is a null once it is past this boundary");
        staged.ExpiryDate.Should().BeNull("and the same holds for the closing bound");
    }

    /// <summary>
    /// The marker is read as absence on the paid path too, where the expiry is derived from the role's term
    /// rather than taken from the caller.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_DerivesTheSameExpiryFromTheMarkerAsFromAnAbsentBound()
    {
        Harness withMarker = Harness.Ready();
        withMarker.LookupRole = MonthlyRole();

        await withMarker.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest
            {
                UserId = UserId,
                EffectiveDate = DateTime.MinValue,
                ExpiryDate = DateTime.MinValue,
            },
            CancellationToken.None);

        Harness withNulls = Harness.Ready();
        withNulls.LookupRole = MonthlyRole();

        await withNulls.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        UserRole fromMarker = withMarker.AddedAssignments.Should().ContainSingle().Subject;
        UserRole fromNulls = withNulls.AddedAssignments.Should().ContainSingle().Subject;

        fromMarker.EffectiveDate.Should().BeNull();
        fromMarker.ExpiryDate.Should().Be(
            Now.AddMonths(1),
            "the term is offset from the current instant, which is what an absent expiry does as well");
        fromMarker.ExpiryDate.Should().Be(fromNulls.ExpiryDate, "so the two spellings of absence agree");
        fromMarker.EffectiveDate.Should().Be(fromNulls.EffectiveDate);
    }

    /// <summary>
    /// a role that declares no period stores a SUBMITTED expiry verbatim, and stores none when the caller
    /// submitted none.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_ForARoleDeclaringNoPeriod_StoresASubmittedExpiryAndDerivesNothingWithout()
    {
        Harness submitted = Harness.Ready();
        submitted.LookupRole = FreeRole();
        DateTime ends = Now.AddYears(1);

        await submitted.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, ExpiryDate = ends },
            CancellationToken.None);

        submitted.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().Be(ends);

        Harness absent = Harness.Ready();
        absent.LookupRole = FreeRole();

        await absent.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        absent.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().BeNull(
            "a role with no term derives no bound of its own");
    }

    /// <summary>
    /// an expiry already in the past is stored as submitted rather than advanced to the present and then
    /// extended by the role's term.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_StoresAnExpiryAlreadyInThePastAsSubmitted()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = MonthlyRole();
        DateTime lapsed = Now.AddYears(-3);

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, ExpiryDate = lapsed },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().Be(lapsed);
    }

    /// <summary>
    /// an expiry still in the future is stored as submitted rather than used as the base the role's term is
    /// added to.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_StoresASubmittedFutureExpiryWithoutAddingTheTerm()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = MonthlyRole();
        DateTime paidUntil = Now.AddDays(10);

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, ExpiryDate = paidUntil },
            CancellationToken.None);

        UserRole stored = harness.AddedAssignments.Should().ContainSingle().Subject;
        stored.ExpiryDate.Should().Be(paidUntil);
        stored.ExpiryDate.Should().NotBe(paidUntil.AddMonths(1), "the term is not added to a stated bound");
    }

    /// <summary>
    /// The expiry is derived from the role's billing term, with the weekly code measured in whole weeks.
    /// </summary>
    /// <param name="frequency">The billing code the role declares.</param>
    /// <param name="units">The number of periods the role declares.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(Frequency.Day, 10)]
    [InlineData(Frequency.Week, 3)]
    [InlineData(Frequency.Month, 6)]
    [InlineData(Frequency.Year, 2)]
    public async Task Assign_DerivesTheExpiryFromTheBillingTerm(Frequency frequency, int units)
    {
        Harness harness = Harness.Ready();
        Role role = FreeRole();
        role.ServiceFee = 5m;
        role.BillingFrequency = frequency;
        role.BillingPeriod = units;
        harness.LookupRole = role;

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        DateTime expected = frequency switch
        {
            Frequency.Day => Now.AddDays(units),
            Frequency.Week => Now.AddDays(units * 7),
            Frequency.Month => Now.AddMonths(units),
            _ => Now.AddYears(units),
        };

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().Be(expected);
    }

    /// <summary>
    /// A one-time charge grants an entitlement that does not lapse, expressed as the far-future date the
    /// legacy screen used rather than as an absent expiry.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_GrantsAPerpetualExpiryForAOneTimeCharge()
    {
        Harness harness = Harness.Ready();
        Role role = FreeRole();
        role.ServiceFee = 49m;
        role.BillingFrequency = Frequency.OneTime;
        role.BillingPeriod = 1;
        harness.LookupRole = role;

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().Be(PerpetualExpiry);
    }

    /// <summary>A term that charges nothing stores no expiry, even when a period is declared alongside it.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_LeavesTheExpiryAbsentForAnUnchargedTerm()
    {
        Harness harness = Harness.Ready();
        Role role = FreeRole();
        role.BillingFrequency = Frequency.None;
        role.BillingPeriod = 4;
        harness.LookupRole = role;

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().BeNull();
    }

    /// <summary>The trial term governs an assignment for a member who has not consumed a trial.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_PrefersTheTrialTermUntilItHasBeenUsed()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TrialThenMonthlyRole();
        harness.ExistingAssignment = null;

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().Be(Now.AddDays(14));
    }

    /// <summary>
    /// Once the trial has been consumed the billing term governs, so a renewal is charged at the full term
    /// rather than granting a second trial.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_FallsBackToTheBillingTermOnceTheTrialHasBeenUsed()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TrialThenMonthlyRole();
        var existing = new UserRole
        {
            UserRoleId = 5,
            UserId = UserId,
            RoleId = RoleId,
            IsTrialUsed = true,
        };
        harness.ExistingAssignment = existing;

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        existing.ExpiryDate.Should().Be(Now.AddMonths(1));
    }

    /// <summary>
    /// A trial term whose code charges nothing does not govern, so the billing term applies even to a
    /// member who has consumed no trial.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_IgnoresATrialTermThatChargesNothing()
    {
        Harness harness = Harness.Ready();
        Role role = MonthlyRole();
        role.TrialFrequency = Frequency.None;
        role.TrialPeriod = 90;
        harness.LookupRole = role;

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().Be(Now.AddMonths(1));
    }

    /// <summary>Removing a member from a role within a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow = null;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// This member reads the tenant row rather than merely probing for it, because it needs the
    /// administrator and registered-role identifiers to decide whether the assignment may be removed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_ReadsTheTenantRatherThanMerelyProbingIt()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();
        harness.ExistingAssignment = new UserRole { UserRoleId = 5, UserId = UserId, RoleId = RoleId };

        await harness.Service.RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        harness.Portals.Verify(
            p => p.GetByIdAsync(PortalId, false, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Removing a member from a role the tenant does not have is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_RefusesAnUnknownRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = null;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
    }

    /// <summary>Removing an account that is not a member of the tenant is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_RefusesAnAccountThatIsNotAMember()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();
        harness.Member = null;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(UserNotFoundCode);
    }

    /// <summary>
    /// Removing an assignment the member does not hold is reported distinctly from a missing member or a
    /// missing role.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_RefusesWhenTheMemberDoesNotHoldTheRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();
        harness.ExistingAssignment = null;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AssignmentNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Member {UserId} does not hold role {RoleId} in portal {PortalId}.");
    }

    /// <summary>
    /// The tenant's own administrator cannot be removed from the administrator role, because doing so would
    /// leave the tenant with nobody able to administer it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_RefusesToUnseatTheTenantAdministrator()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.AdministratorId = UserId;
        harness.PortalRow!.AdministratorRoleId = RoleId;
        harness.LookupRole = FreeRole();
        harness.ExistingAssignment = new UserRole { UserRoleId = 5, UserId = UserId, RoleId = RoleId };

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AssignmentProtectedCode);
        outcome.Reason!.Message.Should().Be("This assignment is protected and cannot be removed.");
        harness.RemovedAssignments.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Another member may leave the administrator role: the protection is specific to the account the
    /// tenant names as its administrator.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_AllowsAnotherMemberToLeaveTheAdministratorRole()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.AdministratorId = 2;
        harness.PortalRow!.AdministratorRoleId = RoleId;
        harness.PortalRow!.RegisteredRoleId = OtherRoleId;
        harness.LookupRole = FreeRole();
        harness.ExistingAssignment = new UserRole { UserRoleId = 5, UserId = UserId, RoleId = RoleId };

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedAssignments.Should().ContainSingle().Which.UserRoleId.Should().Be(5);
    }

    /// <summary>
    /// Nobody may be removed from the registered-members role, whichever account is named, because tenant
    /// membership itself is expressed through it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_RefusesToRemoveAnyoneFromTheRegisteredRole()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.AdministratorId = 2;
        harness.PortalRow!.AdministratorRoleId = OtherRoleId;
        harness.PortalRow!.RegisteredRoleId = RoleId;
        harness.LookupRole = FreeRole();
        harness.ExistingAssignment = new UserRole { UserRoleId = 5, UserId = UserId, RoleId = RoleId };

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AssignmentProtectedCode);
    }

    /// <summary>An assignment to a role that charges nothing is deleted outright.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_DeletesAnUnpaidAssignment()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();
        var assignment = new UserRole { UserRoleId = 5, UserId = UserId, RoleId = RoleId };
        harness.ExistingAssignment = assignment;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull();
        harness.RemovedAssignments.Should().ContainSingle().Which.Should().BeSameAs(assignment);
        assignment.ExpiryDate.Should().BeNull();
    }

    /// <summary>
    /// A paid assignment whose trial has already been consumed is expired rather than deleted, so the
    /// consumed trial cannot be claimed a second time, and the caller is told that this happened.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_ExpiresAPaidAssignmentWhoseTrialWasUsed()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = MonthlyRole();
        var assignment = new UserRole
        {
            UserRoleId = 5,
            UserId = UserId,
            RoleId = RoleId,
            IsTrialUsed = true,
        };
        harness.ExistingAssignment = assignment;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AssignmentExpiredNotRemovedCode);
        outcome.Reason!.Message.Should().Be(
            "The assignment was expired rather than deleted, because its paid trial had already been used.");
        harness.RemovedAssignments.Should().BeEmpty();
        assignment.ExpiryDate.Should().Be(Now.Date.AddDays(-1));
        assignment.IsTrialUsed.Should().BeTrue();
    }

    /// <summary>
    /// A paid assignment whose trial has not been consumed is deleted, because there is no consumed trial
    /// to protect.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_DeletesAPaidAssignmentWhoseTrialWasNotUsed()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = MonthlyRole();
        var assignment = new UserRole
        {
            UserRoleId = 5,
            UserId = UserId,
            RoleId = RoleId,
            IsTrialUsed = false,
        };
        harness.ExistingAssignment = assignment;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull();
        harness.RemovedAssignments.Should().ContainSingle();
    }

    /// <summary>
    /// A role that charges nothing is deleted even when the assignment records a consumed trial, because
    /// there is no fee for a second trial to avoid.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_DeletesAFreeAssignmentEvenWhenItsTrialWasUsed()
    {
        Harness harness = Harness.Ready();
        Role role = FreeRole();
        role.ServiceFee = 0m;
        harness.LookupRole = role;
        harness.ExistingAssignment = new UserRole
        {
            UserRoleId = 5,
            UserId = UserId,
            RoleId = RoleId,
            IsTrialUsed = true,
        };

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull();
        harness.RemovedAssignments.Should().ContainSingle();
    }

    /// <summary>The removal commits once and the member's cached state is discarded by name.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Remove_CommitsAndInvalidatesTheMember()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();
        harness.ExistingAssignment = new UserRole { UserRoleId = 5, UserId = UserId, RoleId = RoleId };

        await harness.Service.RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidateUser(PortalId, MemberName), Times.Once);
    }

    /// <summary>Listing groups for a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleGroups_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<IReadOnlyList<RoleGroupDto>> outcome = await harness.Service
            .ListRoleGroupsAsync(PortalId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>Every group the tenant owns is projected, carrying the tenant it belongs to.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleGroups_ProjectsEveryGroup()
    {
        Harness harness = Harness.Ready();
        harness.Groups =
        [
            new RoleGroup
            {
                RoleGroupId = RoleGroupId,
                PortalId = PortalId,
                RoleGroupName = RoleGroupName,
                Description = "Groups paid roles.",
            },
            new RoleGroup { RoleGroupId = 1, PortalId = PortalId, RoleGroupName = "Staff" },
        ];

        Result<IReadOnlyList<RoleGroupDto>> outcome = await harness.Service
            .ListRoleGroupsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().HaveCount(2);
        outcome.Value[0].RoleGroupId.Should().Be(RoleGroupId);
        outcome.Value[0].PortalId.Should().Be(PortalId);
        outcome.Value[0].RoleGroupName.Should().Be(RoleGroupName);
        outcome.Value[0].Description.Should().Be("Groups paid roles.");
        outcome.Value[1].RoleGroupName.Should().Be("Staff");
    }

    /// <summary>Reading one group from a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRoleGroup_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<RoleGroupDto?> outcome = await harness.Service
            .GetRoleGroupAsync(PortalId, RoleGroupId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>A group the tenant does not have is reported as absent rather than as a failure.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRoleGroup_ReportsAbsenceRatherThanFailure()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = null;

        Result<RoleGroupDto?> outcome = await harness.Service
            .GetRoleGroupAsync(PortalId, RoleGroupId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>A group belonging to another tenant is indistinguishable from one that does not exist.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetRoleGroup_ReportsAbsenceForAGroupOfAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = new RoleGroup
        {
            RoleGroupId = RoleGroupId,
            PortalId = OtherPortalId,
            RoleGroupName = RoleGroupName,
        };

        Result<RoleGroupDto?> outcome = await harness.Service
            .GetRoleGroupAsync(PortalId, RoleGroupId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>Creating a group requires a request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.CreateRoleGroupAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>
    /// The group name is checked for shape, because no request validator is registered for this route.
    /// </summary>
    /// <param name="name">The malformed name to submit.</param>
    /// <param name="expectedMessage">The message the service is measured to report.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("", "A role group name is required.")]
    [InlineData("   ", "A role group name is required.")]
    public async Task CreateRoleGroup_RefusesAMalformedName(string name, string expectedMessage)
    {
        Harness harness = Harness.Ready();

        DomainException refusal = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.CreateRoleGroupAsync(
                PortalId,
                new CreateRoleGroupRequest { RoleGroupName = name },
                CancellationToken.None));

        refusal.Message.Should().Be(expectedMessage);
    }

    /// <summary>A group name longer than the column permits is refused with the measured bound.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_RefusesAnOverlongName()
    {
        Harness harness = Harness.Ready();

        DomainException refusal = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.CreateRoleGroupAsync(
                PortalId,
                new CreateRoleGroupRequest { RoleGroupName = new string('g', 51) },
                CancellationToken.None));

        refusal.Message.Should().Be("A role group name may not exceed 50 characters.");
    }

    /// <summary>
    /// The group's shape is checked before the tenant is probed, which is the reverse of the order the role
    /// update member uses and is asserted here so the difference is not mistaken for an accident.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_ChecksTheNameShapeBeforeTheTenant()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.CreateRoleGroupAsync(
                PortalId,
                new CreateRoleGroupRequest { RoleGroupName = string.Empty },
                CancellationToken.None));

        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Creating a group in a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<RoleGroupDto> outcome = await harness.Service
            .CreateRoleGroupAsync(PortalId, ValidGroupRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
        harness.AddedGroups.Should().BeEmpty();
    }

    /// <summary>A group name already held within the tenant is refused, and the check excludes nothing.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_RefusesADuplicateName()
    {
        Harness harness = Harness.Ready();
        harness.GroupNameTaken = true;

        Result<RoleGroupDto> outcome = await harness.Service
            .CreateRoleGroupAsync(PortalId, ValidGroupRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNameDuplicateCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} already has a role group named '{RoleGroupName}'.");
        harness.Roles.Verify(
            r => r.GetRoleGroupsAsync(PortalId, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.AddedGroups.Should().BeEmpty();
    }

    /// <summary>
    /// A group name taken between the check and the commit is refused with exactly the answer the check
    /// gives.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The concurrent counterpart of the check above. <c>IX_RoleGroupName</c> is unique over <c>(PortalID,
    /// RoleGroupName)</c>, and the check reads the whole group collection and compares in memory - which
    /// cannot see a group another request is inserting concurrently.
    /// </remarks>
    [Fact]
    public async Task CreateRoleGroup_RefusesANameTakenBetweenTheCheckAndTheCommit()
    {
        Harness harness = Harness.Ready();
        harness.UnitOfWork
            .Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(DuplicateKeyException.ForConstraint("IX_RoleGroupName", null));

        Result<RoleGroupDto> outcome = await harness.Service
            .CreateRoleGroupAsync(PortalId, ValidGroupRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(
            RoleGroupNameDuplicateCode,
            "one outcome carries one code whether the collision was found by the check or by the index");
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} already has a role group named '{RoleGroupName}'.");

        harness.Cache.Verify(cache => cache.InvalidatePortal(It.IsAny<int>()), Times.Never);
    }

    /// <summary>The stored group takes its tenant from the route, which the request body cannot contradict.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_StoresTheSubmittedGroup()
    {
        Harness harness = Harness.Ready();
        CreateRoleGroupRequest request = ValidGroupRequest();
        request.Description = "Groups paid roles.";

        Result<RoleGroupDto> outcome = await harness.Service
            .CreateRoleGroupAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        RoleGroup stored = harness.AddedGroups.Should().ContainSingle().Which;
        stored.PortalId.Should().Be(PortalId, "the route names the tenant, not the request body");
        stored.RoleGroupName.Should().Be(RoleGroupName);
        stored.Description.Should().Be("Groups paid roles.");
        outcome.Value.PortalId.Should().Be(PortalId);
    }

    /// <summary>Creating a group commits once and discards the tenant's cached state.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_CommitsAndInvalidatesTheTenant()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreateRoleGroupAsync(PortalId, ValidGroupRequest(), CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidatePortal(PortalId), Times.Once);
    }

    /// <summary>Updating a group requires a request.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateRoleGroupAsync(PortalId, RoleGroupId, null!, CancellationToken.None));
    }

    /// <summary>The group name is checked for shape on update as well, before the tenant is probed.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_RefusesAMalformedName()
    {
        Harness harness = Harness.Ready();

        DomainException refusal = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateRoleGroupAsync(
                PortalId,
                RoleGroupId,
                new UpdateRoleGroupRequest { RoleGroupName = "  " },
                CancellationToken.None));

        refusal.Message.Should().Be("A role group name is required.");
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Updating a group in a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<RoleGroupDto> outcome = await harness.Service
            .UpdateRoleGroupAsync(PortalId, RoleGroupId, ValidGroupUpdate(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>A group belonging to another tenant cannot be edited through this tenant's route.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_RefusesAGroupFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = new RoleGroup
        {
            RoleGroupId = RoleGroupId,
            PortalId = OtherPortalId,
            RoleGroupName = RoleGroupName,
        };

        Result<RoleGroupDto> outcome = await harness.Service
            .UpdateRoleGroupAsync(PortalId, RoleGroupId, ValidGroupUpdate(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} has no role group bearing identifier {RoleGroupId}.");
    }

    /// <summary>
    /// The uniqueness check excludes the group being edited, so keeping a group's own name is not a clash.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_ExcludesTheGroupItselfFromTheNameCheck()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = StoredGroup();

        await harness.Service.UpdateRoleGroupAsync(
            PortalId,
            RoleGroupId,
            ValidGroupUpdate(),
            CancellationToken.None);

        harness.Roles.Verify(
            r => r.GetRoleGroupsAsync(PortalId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A group name held by a different group is refused, with wording that distinguishes the clash from
    /// the one the creation route reports.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_RefusesADuplicateNameHeldByAnotherGroup()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = StoredGroup();
        harness.GroupNameTaken = true;

        Result<RoleGroupDto> outcome = await harness.Service
            .UpdateRoleGroupAsync(PortalId, RoleGroupId, ValidGroupUpdate(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNameDuplicateCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} already has a different role group named '{RoleGroupName}'.");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The submitted name and description are applied to the tracked group, which commits once.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_AppliesTheSubmittedNameAndDescription()
    {
        Harness harness = Harness.Ready();
        RoleGroup tracked = StoredGroup();
        harness.LookupGroup = tracked;

        UpdateRoleGroupRequest request = ValidGroupUpdate();
        request.RoleGroupName = "Renamed Group";
        request.Description = "Changed.";

        Result<RoleGroupDto> outcome = await harness.Service
            .UpdateRoleGroupAsync(PortalId, RoleGroupId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        tracked.RoleGroupName.Should().Be("Renamed Group");
        tracked.Description.Should().Be("Changed.");
        outcome.Value.RoleGroupName.Should().Be("Renamed Group");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidatePortal(PortalId), Times.Once);
    }

    /// <summary>
    /// An update cannot move a group between tenants, because the write contract carries no tenant member
    /// and the mapper writes none.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_DoesNotMoveTheGroupBetweenTenants()
    {
        Harness harness = Harness.Ready();
        RoleGroup tracked = StoredGroup();
        tracked.PortalId = PortalId;
        harness.LookupGroup = tracked;

        UpdateRoleGroupRequest request = ValidGroupUpdate();
        request.RoleGroupName = "Renamed Group";

        await harness.Service.UpdateRoleGroupAsync(PortalId, RoleGroupId, request, CancellationToken.None);

        tracked.PortalId.Should().Be(PortalId);
        tracked.RoleGroupName.Should().Be("Renamed Group", "the members the contract does carry are written");
    }

    /// <summary>Deleting a group in a tenant that does not exist is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRoleGroup_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result outcome = await harness.Service
            .DeleteRoleGroupAsync(PortalId, RoleGroupId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>Deleting a group the tenant does not have is refused.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRoleGroup_RefusesAnUnknownGroup()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = null;

        Result outcome = await harness.Service
            .DeleteRoleGroupAsync(PortalId, RoleGroupId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNotFoundCode);
        harness.RemovedGroups.Should().BeEmpty();
    }

    /// <summary>
    /// A group that still classifies a role is kept, because removing it would leave those roles pointing
    /// at a classification that no longer exists.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRoleGroup_RefusesAGroupThatStillClassifiesARole()
    {
        // The occupancy question is asked through GetRolesByGroup, which is the legacy read for exactly it,
        // so the roles the group classifies are what the world must contain.
        Harness harness = Harness.Ready();
        harness.LookupGroup = StoredGroup();
        harness.RolesInGroup = [StoredRole(), StoredRole(), StoredRole(), StoredRole()];

        Result outcome = await harness.Service
            .DeleteRoleGroupAsync(PortalId, RoleGroupId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupInUseCode);
        outcome.Reason!.Message.Should()
            .Be($"Role group {RoleGroupId} still classifies 4 role(s) and cannot be removed.");
        harness.RemovedGroups.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The occupancy probe is the legacy group read, asked with both the group and its tenant.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRoleGroup_ProbesTheGroupsClassifiedRoles()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = StoredGroup();

        await harness.Service.DeleteRoleGroupAsync(PortalId, RoleGroupId, CancellationToken.None);

        harness.Roles.Verify(
            r => r.GetRolesByGroupAsync(RoleGroupId, PortalId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// An empty group is removed, the change commits once and the tenant's cached state is discarded.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRoleGroup_RemovesAnEmptyGroup()
    {
        Harness harness = Harness.Ready();
        RoleGroup tracked = StoredGroup();
        harness.LookupGroup = tracked;
        harness.RolePage = PagedResult<Role>.Create([], totalCount: 0, pageIndex: 0, pageSize: 1);

        Result outcome = await harness.Service
            .DeleteRoleGroupAsync(PortalId, RoleGroupId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedGroups.Should().ContainSingle().Which.Should().BeSameAs(tracked);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidatePortal(PortalId), Times.Once);
    }

    /// <summary>
    /// Builds the role fixture the store returns: a public monthly subscription bearing the measured
    /// identifier seed of zero.
    /// </summary>
    /// <returns>A role belonging to the tenant under test.</returns>
    private static Role StoredRole() => new()
    {
        RoleId = RoleId,
        PortalId = PortalId,
        RoleName = RoleName,
        Description = "Members who pay monthly.",
        ServiceFee = 9.99m,
        BillingPeriod = 1,
        BillingFrequency = Frequency.Month,
        IsPublic = true,
        AutoAssignment = false,
    };

    /// <summary>
    /// Builds a role whose name and service fee are both stated, so an ordering assertion can be made over
    /// two fields whose sequences deliberately disagree.
    /// </summary>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="roleName">The role name.</param>
    /// <param name="serviceFee">The service fee.</param>
    /// <returns>A role belonging to the tenant under test.</returns>
    private static Role NamedRole(int roleId, string roleName, decimal serviceFee) => new()
    {
        RoleId = roleId,
        PortalId = PortalId,
        RoleName = roleName,
        ServiceFee = serviceFee,
        IsPublic = true,
        AutoAssignment = false,
    };

    /// <summary>Builds a second role fixture so a list projection has more than one row.</summary>
    /// <returns>A second role belonging to the tenant under test.</returns>
    private static Role SecondRole() => new()
    {
        RoleId = OtherRoleId,
        PortalId = PortalId,
        RoleName = "Administrators",
        IsPublic = false,
    };

    /// <summary>
    /// Builds a role that charges nothing and declares no term, so an assignment to it carries no expiry.
    /// </summary>
    /// <returns>A role with no membership term.</returns>
    private static Role FreeRole() => new()
    {
        RoleId = RoleId,
        PortalId = PortalId,
        RoleName = RoleName,
        ServiceFee = null,
        BillingPeriod = null,
        BillingFrequency = null,
        TrialPeriod = null,
        TrialFrequency = null,
    };

    /// <summary>Builds a role charging a monthly fee with a one-month term and no trial.</summary>
    /// <returns>A role with a monthly billing term.</returns>
    private static Role MonthlyRole() => new()
    {
        RoleId = RoleId,
        PortalId = PortalId,
        RoleName = RoleName,
        ServiceFee = 9.99m,
        BillingPeriod = 1,
        BillingFrequency = Frequency.Month,
    };

    /// <summary>Builds a role offering a fourteen-day trial ahead of a monthly billing term.</summary>
    /// <returns>A role with both a trial term and a billing term.</returns>
    private static Role TrialThenMonthlyRole() => new()
    {
        RoleId = RoleId,
        PortalId = PortalId,
        RoleName = RoleName,
        ServiceFee = 9.99m,
        BillingPeriod = 1,
        BillingFrequency = Frequency.Month,
        TrialFee = 0m,
        TrialPeriod = 14,
        TrialFrequency = Frequency.Day,
    };

    /// <summary>Builds the group fixture the store returns.</summary>
    /// <returns>A role group belonging to the tenant under test.</returns>
    private static RoleGroup StoredGroup() => new()
    {
        RoleGroupId = RoleGroupId,
        PortalId = PortalId,
        RoleGroupName = RoleGroupName,
        Description = "Groups paid roles.",
    };

    /// <summary>Builds a member of the tenant under test.</summary>
    /// <param name="userId">The account identifier to carry.</param>
    /// <returns>An account satisfying the columns the store declares as required.</returns>
    private static User Member(int userId) => new()
    {
        UserId = userId,
        Username = MemberName,
        FirstName = "Ada",
        LastName = "Lovelace",
        DisplayName = "Ada Lovelace",
        Email = "ada@example.com",
        IsApproved = true,
    };

    /// <summary>
    /// Builds one membership of the role under test, composed with its account and its role exactly as the
    /// repository read composes them.
    /// </summary>
    /// <param name="userId">The account identifier the membership belongs to.</param>
    /// <param name="effectiveDate">When the membership takes effect, or null for no start bound.</param>
    /// <param name="expiryDate">When the membership ceases, or null for an open-ended membership.</param>
    /// <param name="displayName">
    /// The account's display name, which is both the value the legacy grid rendered and the value this
    /// listing orders by.
    /// </param>
    /// <param name="username">The account's login name.</param>
    /// <returns>An assignment row carrying both navigations.</returns>
    private static UserRole Membership(
        int userId,
        DateTime? effectiveDate = null,
        DateTime? expiryDate = null,
        string displayName = "Ada Lovelace",
        string username = MemberName) => new()
        {
            UserRoleId = userId + 1000,
            UserId = userId,
            RoleId = RoleId,
            EffectiveDate = effectiveDate,
            ExpiryDate = expiryDate,
            User = new User
            {
                UserId = userId,
                Username = username,
                FirstName = "Ada",
                LastName = "Lovelace",
                DisplayName = displayName,
                Email = "ada@example.com",
                IsApproved = true,
            },
            Role = StoredRole(),
        };

    /// <summary>Builds a creation request that passes every check the service performs.</summary>
    /// <returns>A well-formed creation request.</returns>
    private static CreateRoleRequest ValidCreateRequest() => new()
    {
        RoleName = RoleName,
        ServiceFee = 9.99m,
    };

    /// <summary>Builds an update request that passes every shape check the service performs.</summary>
    /// <returns>A well-formed update request.</returns>
    private static UpdateRoleRequest ValidUpdateRequest() => new()
    {
        // The name is required on the update contract as well as on the creation one, so this factory
        // supplies the stored role's own name - which is what a caller amending one other field sends, and
        // which the uniqueness guard must treat as a no-op rather than as a self-collision.
        RoleName = RoleName,
        ServiceFee = 9.99m,
        BillingPeriod = 1,
        BillingFrequency = Frequency.Month,
    };

    /// <summary>Builds a group creation that passes every shape check the service performs.</summary>
    /// <returns>A well-formed create request.</returns>
    /// <remarks>
    /// The two write verbs bind two request types rather than the <c>RoleGroupDto</c> response projection
    /// they return. Both procedures write only the name and the description, so neither contract carries a
    /// group identifier or an owning portal - the first is assigned by the store or taken from the route,
    /// and the second is the resolved tenant.
    /// </remarks>
    private static CreateRoleGroupRequest ValidGroupRequest() => new()
    {
        RoleGroupName = RoleGroupName,
    };

    /// <summary>Builds the update-verb counterpart of <see cref="ValidGroupRequest"/>, member for member.</summary>
    /// <returns>A well-formed update request.</returns>
    private static UpdateRoleGroupRequest ValidGroupUpdate() => new()
    {
        RoleGroupName = RoleGroupName,
    };

    /// <summary>
    /// Assembles the service over six recording doubles, exposing every answer as mutable state so a test
    /// can change the world after the doubles have been wired.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            PortalRow = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                DefaultLanguage = "en-US",
                HomeDirectory = "Portals/0",
                AdministratorId = 2,
                AdministratorRoleId = OtherRoleId,
                RegisteredRoleId = OtherRoleId + 1,
            };

            PortalExists = true;
            Member = RoleServiceTests.Member(UserId);
            LookupRole = StoredRole();
            LookupGroup = StoredGroup();
            RolePage = PagedResult<Role>.Empty;
            AssignmentPage = PagedResult<UserRole>.Empty;
            MemberPage = PagedResult<User>.Empty;
            UserAssignments = [];
            Groups = [];
            MembersById = [];
            AddedRoles = [];
            RemovedRoles = [];
            AddedAssignments = [];
            RemovedAssignments = [];
            AddedGroups = [];
            RemovedGroups = [];

            Roles = new Mock<IRoleRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);

            OpenedTransactions = [];
            Steps = [];
            GrantSweep = Result.Success();

            // The removal now spans a set-based grant sweep and a staged role delete, so it opens a
            // transaction.
            UnitOfWork
                .Setup(unit => unit.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .Returns<TransactionIsolation, CancellationToken>((isolation, _) =>
                {
                    var scope = new RecordingTransactionScope(isolation, Steps);
                    OpenedTransactions.Add(scope);
                    Steps.Add("transaction.begin");
                    return Task.FromResult<ITransactionScope>(scope);
                });

            // Ordering is recorded rather than inferred. The sweep must precede the role delete, both must
            // precede the commit, and the eviction must follow it; verifying each call in isolation would
            // pass for any permutation of the four.
            Permissions
                .Setup(permissions => permissions.StageRolePermissionRemovalAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Steps.Add("permissions.sweep");
                    return Task.FromResult(GrantSweep);
                });

            Permissions
                .Setup(permissions => permissions.InvalidateUserPermissionCaches())
                .Callback(() =>
                {
                    Steps.Add("permissions.evict");

                    if (GrantCacheEvictionFault is not null)
                    {
                        throw GrantCacheEvictionFault;
                    }
                });

            // The acting operator is fixed so that every audit assertion below can name the actor it
            // expects rather than accepting whatever a loose mock returns.
            CurrentUser.SetupGet(caller => caller.UserId).Returns(OperatorUserId);
            CurrentUser.SetupGet(caller => caller.UserName).Returns(OperatorUserName);

            AuditRecords = [];
            Audit
                .Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(record =>
                {
                    Steps.Add("audit." + record.EventName);
                    AuditRecords.Add(record);
                });

            Service = new RoleService(
                Roles.Object,
                Portals.Object,
                Users.Object,
                Permissions.Object,
                UnitOfWork.Object,
                Clock.Object,
                Cache.Object,
                CurrentUser.Object,
                Audit.Object);
        }

        public RoleService Service { get; }

        public Mock<IRoleRepository> Roles { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IPermissionService> Permissions { get; }

        /// <summary>Every transaction the service opened, in the order it opened them.</summary>
        public List<RecordingTransactionScope> OpenedTransactions { get; }

        /// <summary>
        /// The ordered sequence of transaction, sweep, delete, commit, audit and eviction steps the service
        /// took.
        /// </summary>
        public List<string> Steps { get; }

        /// <summary>
        /// A failure raised by the POST-COMMIT grant-cache eviction, or <see langword="null"/> for none.
        /// </summary>
        /// <remarks>
        /// The eviction is the last step after the commit, so it is the one piece of post-commit
        /// maintenance a disconnecting caller can make fail. This knob lets an assertion prove that the
        /// record of a completed removal no longer depends on it.
        /// </remarks>
        public Exception? GrantCacheEvictionFault { get; set; }

        /// <summary>The answer the permission contract gives when asked to sweep a role's grants.</summary>
        public Result GrantSweep { get; set; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<IClock> Clock { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        public Mock<IAuditSink> Audit { get; }

        /// <summary>Every audit record the service emitted, in the order it emitted them.</summary>
        public List<AuditEvent> AuditRecords { get; }

        public Portal? PortalRow { get; set; }

        public bool PortalExists { get; set; }

        public Role? LookupRole { get; set; }

        public RoleGroup? LookupGroup { get; set; }

        public UserRole? ExistingAssignment { get; set; }

        public User? Member { get; set; }

        public Dictionary<int, User?> MembersById { get; }

        public bool NameTaken { get; set; }

        /// <summary>
        /// Gets or sets the name the CLASHING row is to carry, when it must differ from the submitted one.
        /// </summary>
        /// <remarks>
        /// Null means "the same string the caller submitted", which is the ordinary duplicate. A value
        /// makes the clash a COLLATION collision - the store treating two visibly different strings as one
        /// name - which is the case the refusal has to explain rather than merely report.
        /// </remarks>
        public string? ClashingStoredName { get; set; }

        public bool GroupNameTaken { get; set; }

        public bool EchoCreatedRole { get; set; } = true;

        public PagedResult<Role> RolePage { get; set; }

        public PagedResult<UserRole> AssignmentPage { get; set; }

        public PagedResult<User> MemberPage { get; set; }

        public IReadOnlyList<UserRole> UserAssignments { get; set; }

        public IReadOnlyList<RoleGroup> Groups { get; set; }

        public List<Role> AddedRoles { get; }

        public List<Role> RemovedRoles { get; }

        public List<UserRole> AddedAssignments { get; }

        public List<UserRole> RemovedAssignments { get; }

        public List<RoleGroup> AddedGroups { get; }

        public List<RoleGroup> RemovedGroups { get; }

        public List<Role> UpdatedRoles { get; } = [];

        public List<UserRole> UpdatedAssignments { get; } = [];

        public List<RoleGroup> UpdatedGroups { get; } = [];

        public List<int> RemovedRoleIds { get; } = [];

        public List<int> RemovedGroupIds { get; } = [];

        public List<(int UserId, int RoleId)> RemovedAssignmentKeys { get; } = [];

        public IReadOnlyList<Role> RolesInGroup { get; set; } = [];

        /// <summary>Gets or sets the MEMBERSHIP rows the role-membership listing reads.</summary>
        public IReadOnlyList<UserRole> RoleMembers { get; set; } = [];

        /// <summary>
        /// Composes one page of roles from the harness's role world, reproducing the semantics <see
        /// cref="IRoleRepository.ListAsync"/> documents.
        /// </summary>
        /// <param name="portalId">The owning tenant, matched strictly.</param>
        /// <param name="roleGroupId">Restrict to one group, or <see langword="null"/> for none.</param>
        /// <param name="ungroupedOnly">Restrict to the roles belonging to no group.</param>
        /// <param name="nameQuery">A name fragment, or <see langword="null"/> for no name restriction.</param>
        /// <param name="sortBy">The role property to order by, or <see langword="null"/> for the default.</param>
        /// <param name="descending">Whether the ordering runs downwards.</param>
        /// <param name="pageIndex">The page to return, counted from zero.</param>
        /// <param name="pageSize">The page width, or zero for every matching row.</param>
        /// <returns>The window and the total, exactly as the store would report them.</returns>
        /// <remarks>
        /// The STRICT tenant predicate is reproduced deliberately: it is the substantive difference between
        /// this member and the unpaged read the harness also serves, and a fake that ignored it would let a
        /// listing that had lost the narrowing still pass.
        /// </remarks>
        public PagedResult<Role> ComposeRolePage(
            int portalId,
            int? roleGroupId,
            bool ungroupedOnly,
            string? nameQuery,
            string? sortBy,
            bool descending,
            int pageIndex,
            int pageSize)
        {
            IEnumerable<Role> matching = RolePage.Items.Where(role => role.PortalId == portalId);

            if (roleGroupId is int wantedGroup)
            {
                matching = matching.Where(role => role.RoleGroupId == wantedGroup);
            }
            else if (ungroupedOnly)
            {
                matching = matching.Where(role => role.RoleGroupId is null);
            }

            if (!string.IsNullOrWhiteSpace(nameQuery))
            {
                string wanted = nameQuery.Trim();
                matching = matching.Where(role =>
                    role.RoleName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            }

            string field = string.IsNullOrWhiteSpace(sortBy) ? "ROLENAME" : sortBy.Trim().ToUpperInvariant();

            IOrderedEnumerable<Role> ordered = field switch
            {
                "ROLEID" => OrderedBy(matching, role => role.RoleId, descending),
                "DESCRIPTION" => OrderedBy(matching, role => role.Description ?? string.Empty, descending, StringComparer.OrdinalIgnoreCase),
                "SERVICEFEE" => OrderedBy(matching, role => role.ServiceFee, descending),
                "BILLINGFREQUENCY" => OrderedBy(matching, role => role.BillingFrequency, descending),
                "BILLINGPERIOD" => OrderedBy(matching, role => role.BillingPeriod, descending),
                "TRIALFEE" => OrderedBy(matching, role => role.TrialFee, descending),
                "TRIALFREQUENCY" => OrderedBy(matching, role => role.TrialFrequency, descending),
                "TRIALPERIOD" => OrderedBy(matching, role => role.TrialPeriod, descending),
                "ISPUBLIC" => OrderedBy(matching, role => role.IsPublic, descending),
                "AUTOASSIGNMENT" => OrderedBy(matching, role => role.AutoAssignment, descending),
                _ => OrderedBy(matching, role => role.RoleName, descending, StringComparer.OrdinalIgnoreCase),
            };

            List<Role> rows = (descending
                    ? ordered.ThenByDescending(role => role.RoleId)
                    : ordered.ThenBy(role => role.RoleId))
                .ToList();

            return Window(rows, pageIndex, pageSize);
        }

        /// <summary>
        /// Composes one page of role memberships from the harness's assignment world, reproducing the
        /// semantics <see cref="IRoleRepository.ListRoleMembershipsAsync"/> documents.
        /// </summary>
        /// <param name="accountQuery">An account fragment, or <see langword="null"/> for no restriction.</param>
        /// <param name="sortBy">The account property to order by, or <see langword="null"/> for the default.</param>
        /// <param name="descending">Whether the ordering runs downwards.</param>
        /// <param name="pageIndex">The page to return, counted from zero.</param>
        /// <param name="pageSize">The page width, or zero for every matching row.</param>
        /// <returns>The window and the total, exactly as the store would report them.</returns>
        /// <remarks>
        /// The three arms that name a value of the external membership store order by the tie-break alone,
        /// which is what the store does and what the in-memory ordering they replace already produced,
        /// since this read populates none of those three values.
        /// </remarks>
        public PagedResult<UserRole> ComposeMembershipPage(
            string? accountQuery,
            string? sortBy,
            bool descending,
            int pageIndex,
            int pageSize)
        {
            IEnumerable<UserRole> matching = RoleMembers;

            if (!string.IsNullOrWhiteSpace(accountQuery))
            {
                string wanted = accountQuery.Trim();
                matching = matching.Where(assignment =>
                    assignment.User is not null
                    && (assignment.User.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                        || assignment.User.Username.Contains(wanted, StringComparison.OrdinalIgnoreCase)));
            }

            string field = string.IsNullOrWhiteSpace(sortBy) ? "DISPLAYNAME" : sortBy.Trim().ToUpperInvariant();

            IOrderedEnumerable<UserRole> ordered = field switch
            {
                "USERID" => OrderedBy(matching, assignment => assignment.UserId, descending),
                "USERNAME" => OrderedBy(matching, assignment => AccountOf(assignment).Username, descending, StringComparer.OrdinalIgnoreCase),
                "FIRSTNAME" => OrderedBy(matching, assignment => AccountOf(assignment).FirstName, descending, StringComparer.OrdinalIgnoreCase),
                "LASTNAME" => OrderedBy(matching, assignment => AccountOf(assignment).LastName, descending, StringComparer.OrdinalIgnoreCase),
                "EMAIL" => OrderedBy(matching, assignment => AccountOf(assignment).Email ?? string.Empty, descending, StringComparer.OrdinalIgnoreCase),
                "ISSUPERUSER" => OrderedBy(matching, assignment => AccountOf(assignment).IsSuperUser, descending),
                "CREATEDDATE" or "LASTLOGINDATE" or "ISAPPROVED" => OrderedBy(matching, assignment => assignment.UserRoleId, descending),
                _ => OrderedBy(matching, assignment => AccountOf(assignment).DisplayName, descending, StringComparer.OrdinalIgnoreCase),
            };

            List<UserRole> rows = (descending
                    ? ordered.ThenByDescending(assignment => assignment.UserRoleId)
                    : ordered.ThenBy(assignment => assignment.UserRoleId))
                .ToList();

            return Window(rows, pageIndex, pageSize);
        }

        /// <summary>The account an assignment composes, or an empty account when it composed none.</summary>
        /// <param name="assignment">The assignment row.</param>
        /// <returns>The composed account, never null.</returns>
        private static User AccountOf(UserRole assignment) => assignment.User ?? new User();

        /// <summary>Applies one ordering in the requested direction.</summary>
        /// <typeparam name="TItem">The item type being ordered.</typeparam>
        /// <typeparam name="TKey">The sort key type.</typeparam>
        /// <param name="items">The items to order.</param>
        /// <param name="key">Selects the sort key.</param>
        /// <param name="descending">Whether the ordering is descending.</param>
        /// <param name="comparer">An optional comparer for the key.</param>
        /// <returns>The ordered sequence, still open for a tie-breaking key.</returns>
        private static IOrderedEnumerable<TItem> OrderedBy<TItem, TKey>(
            IEnumerable<TItem> items,
            Func<TItem, TKey> key,
            bool descending,
            IComparer<TKey>? comparer = null)
            => descending
                ? items.OrderByDescending(key, comparer)
                : items.OrderBy(key, comparer);

        /// <summary>Cuts the store's window over an ordered row set.</summary>
        /// <typeparam name="TRow">The row type.</typeparam>
        /// <param name="rows">The whole ordered set.</param>
        /// <param name="pageIndex">The page to return, counted from zero.</param>
        /// <param name="pageSize">The page width, or zero for every row.</param>
        /// <returns>The window and the total.</returns>
        private static PagedResult<TRow> Window<TRow>(List<TRow> rows, int pageIndex, int pageSize)
            => pageSize == 0
                ? PagedResult<TRow>.Unpaged(rows)
                : PagedResult<TRow>.Create(
                    rows.Skip(Paging.SkipCount(pageIndex, pageSize)).Take(pageSize).ToList(),
                    rows.Count,
                    pageIndex,
                    pageSize);

        /// <summary>
        /// Builds a harness whose world is consistent: the tenant exists, the role exists and belongs to
        /// it, the member exists, and no name is taken.
        /// </summary>
        /// <returns>A wired harness.</returns>
        public static Harness Ready()
        {
            var harness = new Harness();

            harness.Portals
                .Setup(p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalExists);
            // MIGRATION: the read is gated by the same existence flag as the probe, so the
            // harness describes one world; see the fuller note on the sibling suite's harness.
            harness.Portals
                .Setup(p => p.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalExists ? harness.PortalRow : null);

            // Every stub below names a member of the role contract as it is actually declared: reads scoped
            // by portal, writes staged asynchronously and never returning a generated key.
            harness.Roles
                .Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int roleId, int portalId, CancellationToken _) =>
                    harness.LookupRole is { } candidate && candidate.PortalId == portalId
                        ? candidate
                        : null);
            harness.Roles
                .Setup(r => r.GetRoleGroupAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, int roleGroupId, CancellationToken _) =>
                    harness.LookupGroup is { } candidate && candidate.PortalId == portalId
                        ? candidate
                        : null);
            harness.Roles
                .Setup(r => r.GetUserRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ExistingAssignment);

            // Role-name uniqueness is now answered by the name lookup itself, because IX_RoleName makes
            // it a single-row question. A taken name is therefore modelled as a matching row.
            harness.Roles
                .Setup(r => r.GetByNameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, string roleName, CancellationToken _) => harness.NameTaken
                    ? new Role
                    {
                        RoleId = 4242,
                        PortalId = portalId,
                        RoleName = harness.ClashingStoredName ?? roleName,
                    }
                    : null);

            harness.Roles
                .Setup(r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RolePage.Items);

            // THE ROLE LISTING'S PAGE NOW COMES FROM THE STORE, filtered, ordered, counted and windowed
            // there, so this is the seam the listing facts exercise.
            harness.Roles
                .Setup(r => r.ListAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    int portalId,
                    int? roleGroupId,
                    bool ungroupedOnly,
                    string? nameQuery,
                    string? sortBy,
                    bool descending,
                    int pageIndex,
                    int pageSize,
                    CancellationToken _) => harness.ComposeRolePage(
                        portalId,
                        roleGroupId,
                        ungroupedOnly,
                        nameQuery,
                        sortBy,
                        descending,
                        pageIndex,
                        pageSize));
            harness.Roles
                .Setup(r => r.GetUserRolesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UserAssignments);
            harness.Roles
                .Setup(r => r.GetRolesByGroupAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RolesInGroup);

            // Group-name uniqueness is answered over the portal's group list, so a taken name is
            // modelled by a group of that name being present in it.
            harness.Roles
                .Setup(r => r.GetRoleGroupsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) => harness.GroupNameTaken
                    ? harness.Groups
                        .Concat([new RoleGroup { RoleGroupId = 4242, PortalId = portalId, RoleGroupName = RoleGroupName }])
                        .ToList()
                    : harness.Groups);

            harness.Roles
                .Setup(r => r.AddAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Callback<Role, CancellationToken>((role, _) =>
                {
                    harness.AddedRoles.Add(role);
                    if (harness.EchoCreatedRole)
                    {
                        harness.LookupRole = role;
                    }
                })
                .Returns(Task.CompletedTask);
            harness.Roles
                .Setup(r => r.UpdateAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Callback<Role, CancellationToken>((role, _) => harness.UpdatedRoles.Add(role))
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((roleId, _) =>
                {
                    harness.Steps.Add("roles.delete");
                    harness.RemovedRoleIds.Add(roleId);
                    if (harness.LookupRole is { } candidate && candidate.RoleId == roleId)
                    {
                        harness.RemovedRoles.Add(candidate);
                    }
                })
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(r => r.AddUserRoleAsync(It.IsAny<UserRole>(), It.IsAny<CancellationToken>()))
                .Callback<UserRole, CancellationToken>((assignment, _) => harness.AddedAssignments.Add(assignment))
                .Returns(Task.CompletedTask);
            harness.Roles
                .Setup(r => r.UpdateUserRoleAsync(It.IsAny<UserRole>(), It.IsAny<CancellationToken>()))
                .Callback<UserRole, CancellationToken>((assignment, _) => harness.UpdatedAssignments.Add(assignment))
                .Returns(Task.CompletedTask);
            harness.Roles
                .Setup(r => r.DeleteUserRoleAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, int, CancellationToken>((userId, roleId, _) =>
                {
                    harness.RemovedAssignmentKeys.Add((userId, roleId));

                    UserRole? matched = harness.UserAssignments
                        .Concat(harness.ExistingAssignment is null ? [] : new[] { harness.ExistingAssignment })
                        .FirstOrDefault(a => a.UserId == userId && a.RoleId == roleId);
                    if (matched is not null)
                    {
                        harness.RemovedAssignments.Add(matched);
                    }
                })
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(r => r.AddRoleGroupAsync(It.IsAny<RoleGroup>(), It.IsAny<CancellationToken>()))
                .Callback<RoleGroup, CancellationToken>((group, _) => harness.AddedGroups.Add(group))
                .Returns(Task.CompletedTask);
            harness.Roles
                .Setup(r => r.UpdateRoleGroupAsync(It.IsAny<RoleGroup>(), It.IsAny<CancellationToken>()))
                .Callback<RoleGroup, CancellationToken>((group, _) => harness.UpdatedGroups.Add(group))
                .Returns(Task.CompletedTask);
            harness.Roles
                .Setup(r => r.DeleteRoleGroupAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((roleGroupId, _) =>
                {
                    harness.RemovedGroupIds.Add(roleGroupId);
                    if (harness.LookupGroup is { } candidate && candidate.RoleGroupId == roleGroupId)
                    {
                        harness.RemovedGroups.Add(candidate);
                    }
                })
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(r => r.GetUserRolesByUsernameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RoleMembers);

            // The membership listing's page, likewise served by the store and likewise faked over the same
            // assignment world the unpaged stub above serves.
            harness.Roles
                .Setup(r => r.ListRoleMembershipsAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    int _,
                    string _,
                    string? accountQuery,
                    string? sortBy,
                    bool descending,
                    int pageIndex,
                    int pageSize,
                    CancellationToken _) => harness.ComposeMembershipPage(
                        accountQuery,
                        sortBy,
                        descending,
                        pageIndex,
                        pageSize));

            harness.Users
                .Setup(u => u.ListByRoleNameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => []);

            harness.Users
                .Setup(u => u.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int? _, int userId, CancellationToken _) => harness.MembersById.Count > 0
                    ? harness.MembersById.TryGetValue(userId, out User? found) ? found : null
                    : harness.Member);
            harness.Users
                .Setup(u => u.ListAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.MemberPage);

            harness.UnitOfWork
                .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Callback(() => harness.Steps.Add("unitOfWork.save"))
                .ReturnsAsync(1);

            harness.Clock.SetupGet(c => c.UtcNow).Returns(() => Now);

            return harness;
        }
    }

    /// <summary>
    /// A transaction scope that records whether it was committed and whether it was disposed, and appends
    /// its commit to the harness's ordered step list.
    /// </summary>
    private sealed class RecordingTransactionScope : ITransactionScope
    {
        private readonly List<string> _steps;

        /// <summary>Initialises a new instance of the <see cref="RecordingTransactionScope"/> class.</summary>
        /// <param name="isolation">The isolation the service asked for.</param>
        /// <param name="steps">The harness's ordered step list, which the commit is appended to.</param>
        public RecordingTransactionScope(TransactionIsolation isolation, List<string> steps)
        {
            Isolation = isolation;
            _steps = steps;
        }

        /// <summary>The isolation the service asked for.</summary>
        public TransactionIsolation Isolation { get; }

        /// <summary>Whether the scope was committed.</summary>
        public bool Committed { get; private set; }

        /// <summary>Whether the scope was disposed.</summary>
        public bool Disposed { get; private set; }

        /// <summary>Whether the scope was abandoned - disposed without ever being committed.</summary>
        public bool RolledBack => Disposed && !Committed;

        /// <inheritdoc />
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Committed = true;
            _steps.Add("transaction.commit");

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }
}
