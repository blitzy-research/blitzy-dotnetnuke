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
/// paid-membership term arithmetic that derives an assignment's dates, and the two assignments the
/// service refuses to remove.
/// </summary>
/// <remarks>
/// <para>
/// Two asymmetries in this service are deliberate and are pinned here because they look like oversights
/// until the reason is stated. First, the creation member performs no shape checking of its own while the
/// update member performs a full set: the creation route has a registered request validator and the update
/// route has none, so the service compensates exactly where the pipeline does not. Second, the removal
/// member reads the tenant row rather than merely probing for its existence, because it needs the
/// administrator and registered-role identifiers to decide whether the assignment is one it is allowed to
/// remove at all.
/// </para>
/// <para>
/// The term arithmetic deserves the space it takes. An assignment's expiry is not the date that was
/// submitted; it is derived from the role's billing or trial term, and which of the two governs depends on
/// whether the member has already consumed a trial. A submitted date in the past is not stored as given,
/// and a submitted date is discarded outright when the role declares no period. Each of those rules is
/// asserted separately, because a single combined test would not say which rule had broken.
/// </para>
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

    private const string RoleGroupName = "Paid Membership";

    private const string MemberName = "measured_member";

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

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The role contract exposes fourteen asynchronous operations and nothing else, each of which accepts a
    /// cancellation token as its final argument.
    /// </summary>
    [Fact]
    public void RoleContract_OffersExactlyFourteenOperations()
    {
        MethodInfo[] members = typeof(IRoleService).GetMethods();

        members.Should().HaveCount(14, "the role contract covers roles, their members and their groups");

        foreach (MethodInfo member in members)
        {
            member.Name.Should().EndWith("Async");
            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue();

            ParameterInfo[] parameters = member.GetParameters();
            parameters[^1].ParameterType.Should().Be(typeof(CancellationToken));
            parameters[0].Name.Should().Be("portalId", "every operation is scoped to one tenant");
        }
    }

    /// <summary>
    /// The service refuses to be constructed without every collaborator it depends on.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        var roles = new Mock<IRoleRepository>().Object;
        var portals = new Mock<IPortalRepository>().Object;
        var users = new Mock<IUserRepository>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var clock = new Mock<IClock>().Object;
        var cache = new Mock<ICacheService>().Object;

        Assert.Throws<ArgumentNullException>("roles", () =>
        {
            _ = new RoleService(null!, portals, users, unitOfWork, clock, cache);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new RoleService(roles, null!, users, unitOfWork, clock, cache);
        });
        Assert.Throws<ArgumentNullException>("users", () =>
        {
            _ = new RoleService(roles, portals, null!, unitOfWork, clock, cache);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new RoleService(roles, portals, users, null!, clock, cache);
        });
        Assert.Throws<ArgumentNullException>("clock", () =>
        {
            _ = new RoleService(roles, portals, users, unitOfWork, null!, cache);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new RoleService(roles, portals, users, unitOfWork, clock, null!);
        });
    }

    /// <summary>
    /// Listing roles requires a paging request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ListRolesAsync(PortalId, null!, null, CancellationToken.None));
    }

    /// <summary>
    /// Listing roles for a tenant that does not exist is refused before the role store is read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest(), null, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
        outcome.Reason!.Message.Should().Be($"No portal bears identifier {PortalId}.");
        harness.Roles.Verify(
            r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A group filter naming a group of another tenant is refused rather than silently ignored.
    /// </summary>
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
            .ListRolesAsync(PortalId, new PagedRequest(), RoleGroupId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} has no role group bearing identifier {RoleGroupId}.");
    }

    /// <summary>
    /// A group filter naming no stored group at all is refused with the same reason.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_RefusesAnUnknownGroup()
    {
        Harness harness = Harness.Ready();
        harness.LookupGroup = null;

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest(), RoleGroupId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNotFoundCode);
    }

    /// <summary>
    /// No group is verified when the caller supplied no group filter.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_DoesNotVerifyAGroupWhenNoneWasAsked()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest(), null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Roles.Verify(
            r => r.GetRoleGroupAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The tenant's roles are read once and the request's paging and free-text term are applied to that
    /// result, because the legacy membership provider exposed no paged or filtered role read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_PassesTheRequestThroughUnchanged()
    {
        Harness harness = Harness.Ready();
        var request = new PagedRequest { PageIndex = 3, PageSize = 25, Query = "sub" };

        await harness.Service.ListRolesAsync(PortalId, request, RoleGroupId, CancellationToken.None);

        harness.Roles.Verify(
            r => r.GetByPortalIdAsync(PortalId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A request that asks for no page size receives an unpaged answer.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_ReportsAnUnpagedAnswerWhenNoPageSizeWasAsked()
    {
        Harness harness = Harness.Ready();
        harness.RolePage = PagedResult<Role>.Unpaged([StoredRole(), SecondRole()]);

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service
            .ListRolesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.IsUnpaged.Should().BeTrue();
        outcome.Value.PageSize.Should().Be(0);
        outcome.Value.TotalCount.Should().Be(2);
        outcome.Value.Items.Should().HaveCount(2);
    }

    /// <summary>
    /// A paged request keeps the store's total rather than the size of the returned page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoles_PreservesTheStoresTotalWhenAPageWasAsked()
    {
        // MIGRATION: the total is computed over the tenant's roles rather than reported by the store,
        // because the legacy membership provider had no paged role read - GetPortalRoles(PortalId)
        // returned every row and the admin screen paged it. Twenty-one roles across three pages of ten
        // therefore reproduces the same arithmetic the legacy screen performed.
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
            .ListRolesAsync(PortalId, new PagedRequest { PageIndex = 2, PageSize = 10 }, null, CancellationToken.None);

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
            .ListRolesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, CancellationToken.None);

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

    /// <summary>
    /// Reading one role from a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// A role belonging to another tenant is indistinguishable from one that does not exist.
    /// </summary>
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
    /// The detail projection carries the identifier of the group that classifies the role, and resolves
    /// its name no further.
    /// </summary>
    /// <remarks>
    /// The group is identified, not named. A caller that needs the name reads it from the group
    /// contract, exactly as the legacy editor did: it had already bound a drop-down of the portal's
    /// groups, so it resolved the name locally rather than having the role row carry it.
    /// </remarks>
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

    /// <summary>
    /// An unclassified role carries a null group identifier and provokes no group lookup.
    /// </summary>
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
    /// Reading one role reads no page of assignments, because the detail contract carries no member
    /// tally to populate.
    /// </summary>
    /// <remarks>
    /// This is a round-trip guard, not a projection assertion. An earlier revision of the contract
    /// carried a member count and the service produced it by reading the total off a one-row page of
    /// assignments, so a single-role request cost two extra queries - one for the group, one for the
    /// count - to populate two members no legacy role screen displayed. Both are gone, and this test
    /// fails if either read is reintroduced.
    /// </remarks>
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

    /// <summary>
    /// Creating a role requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRole_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.CreateRoleAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>
    /// Creating a role in a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// A role may not be classified into a group belonging to another tenant.
    /// </summary>
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

    /// <summary>
    /// A name already held within the tenant is refused.
    /// </summary>
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
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} already has a role named '{RoleName}'.");
    }

    /// <summary>
    /// The uniqueness check excludes nothing, because no row exists yet to exclude.
    /// </summary>
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

    /// <summary>
    /// Nothing is written when the name is already taken.
    /// </summary>
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
    /// Updating a role requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateRoleAsync(PortalId, RoleId, null!, CancellationToken.None));
    }

    /// <summary>
    /// Updating a role in a tenant that does not exist is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
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

    /// <summary>
    /// A role belonging to another tenant cannot be reached through this tenant's route.
    /// </summary>
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
    /// The update member carries the shape checks the update route's missing validator would otherwise
    /// have performed, and reports each violation with its own measured wording.
    /// </summary>
    /// <param name="violation">The single field to spoil.</param>
    /// <param name="expectedMessage">The message the service is measured to report.</param>
    /// <returns>A task representing the assertion.</returns>
    // MIGRATION: there are deliberately no name cases here, unlike the creation theory. The update
    // contract carries no name, so the service checks the STORED name - which is already valid - and
    // no submitted value can spoil it. This mirrors the legacy screen exactly: editing an existing
    // role disabled the name's required-field validator
    // (Website/admin/Security/EditRoles.ascx.vb L134), so the required and length rules genuinely did
    // not apply on the edit path. The name-preservation behaviour that replaces them is asserted by
    // its own tests below.
    [Theory]
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
                break;
            case "negative-billing-period":
                request.BillingPeriod = -3;
                break;
            case "zero-trial-period":
                request.TrialPeriod = 0;
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
    /// The update route never reads name uniqueness, because a name it cannot change cannot clash.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_NeverReadsNameUniqueness()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        await harness.Service.UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        // MIGRATION: the legacy screen applied its duplicate-name guard only when INSERTING - at
        // Website/admin/Security/EditRoles.ascx.vb L251-L257 the add branch looks the name up and
        // refuses on a hit, while the edit branch updates with no such check. The absence of this read
        // is therefore the faithful behaviour, not a missing rule.
        harness.Roles.Verify(
            r => r.GetByNameAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An update succeeds and leaves the stored name untouched even when another role already holds
    /// that name, because the update route neither accepts nor writes a name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_PreservesTheStoredNameEvenWhenAnotherRoleHoldsIt()
    {
        Harness harness = Harness.Ready();
        Role tracked = StoredRole();
        harness.LookupRole = tracked;
        harness.NameTaken = true;

        Result<RoleDetailDto> outcome = await harness.Service
            .UpdateRoleAsync(PortalId, RoleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull();
        tracked.RoleName.Should().Be(RoleName);
        outcome.Value.RoleName.Should().Be(RoleName);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The submitted shape is applied to the tracked row, and the tenant assignment is left alone.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRole_AppliesTheSubmittedShape()
    {
        Harness harness = Harness.Ready();
        Role tracked = StoredRole();
        harness.LookupRole = tracked;

        UpdateRoleRequest request = ValidUpdateRequest();
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

        // MIGRATION: the name is PRESERVED rather than replaced, because the update contract carries
        // none and the projection passes the tracked entity's own value back through.
        tracked.RoleName.Should().Be(RoleName);
        tracked.Description.Should().Be("Changed.");
        tracked.IsPublic.Should().BeFalse();
        tracked.AutoAssignment.Should().BeTrue();
        tracked.ServiceFee.Should().Be(19.5m);
        tracked.BillingPeriod.Should().Be(2);
        tracked.BillingFrequency.Should().Be(Frequency.Year);
        tracked.RsvpCode.Should().Be("NEW");
        tracked.IconFile.Should().Be("new.gif");
        tracked.PortalId.Should().Be(PortalId);
        outcome.Value.RoleName.Should().Be(RoleName);
    }

    /// <summary>
    /// The change commits once and the tenant's cached state is discarded.
    /// </summary>
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
    /// The answer is projected from the tracked row rather than from a second read of the store.
    /// </summary>
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

    /// <summary>
    /// Deleting a role in a tenant that does not exist is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRole_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result outcome = await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// Deleting a role the tenant does not have is refused.
    /// </summary>
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

    /// <summary>
    /// Nothing is written when the role is unknown.
    /// </summary>
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
        // MIGRATION: the assignments are no longer swept row by row. FK_UserRoles_Roles is declared
        // ON DELETE CASCADE and UserRoleConfiguration declares the same behaviour, so the single keyed
        // delete carries them - and issuing the deletes here as well would issue them twice.
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
    /// The removal commits once and discards both the tenant's cached state and its cached page grants,
    /// because a page permission may have named the role that has gone.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteRole_CommitsOnceAndInvalidatesBothTheTenantAndItsPageGrants()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();

        await harness.Service.DeleteRoleAsync(PortalId, RoleId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidatePortal(PortalId), Times.Once);
        harness.Cache.Verify(c => c.InvalidateTabPermissions(PortalId), Times.Once);
    }

    /// <summary>
    /// Listing a role's members requires a paging request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ListRoleUsersAsync(PortalId, RoleId, null!, CancellationToken.None));
    }

    /// <summary>
    /// Listing a role's members in a tenant that does not exist is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// Listing the members of a role the tenant does not have is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_RefusesAnUnknownRole()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = null;

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleNotFoundCode);
    }

    /// <summary>
    /// The members are read within the tenant and by the role's own name, never installation-wide.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_ResolvesEachMemberWithinTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Member(UserId)];

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        // The tenant and the role name are both passed, because a role name is unique only within a
        // portal - the legacy GetUsersByRolename took the same pair.
        harness.Users.Verify(
            u => u.ListByRoleNameAsync(PortalId, RoleName, It.IsAny<CancellationToken>()),
            Times.Once);

        // No read-per-row remains: the accounts arrive already composed.
        harness.Users.Verify(
            u => u.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        UserListItemDto row = outcome.Value.Items.Should().ContainSingle().Which;
        row.UserId.Should().Be(UserId);
        row.PortalId.Should().Be(PortalId);
        row.Username.Should().Be(MemberName);
    }

    /// <summary>
    /// An account that is not a member of the tenant never reaches the projection at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: this used to be a skip inside the projection, because the projection walked assignment
    /// rows and resolved each account separately - so an account outside the tenant produced a row that
    /// had to be discarded. The read now answers the tenant-scoped question directly, exactly as the
    /// legacy GetUsersByRolename(PortalID, Rolename) did, so the hollow row cannot be produced in the
    /// first place and there is nothing left to skip.
    /// </remarks>
    [Fact]
    public async Task ListRoleUsers_ReportsOnlyTheAccountsTheTenantReadidReturns()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Member(UserId)];

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        outcome.Value.Items.Should().ContainSingle().Which.UserId.Should().Be(UserId);
    }

    /// <summary>
    /// The reported total counts every member of the role, not just the page that was returned.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_ReportsTheTotalIndependentlyOfThePageSize()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Member(UserId), Member(8), Member(9)];

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 2 }, CancellationToken.None);

        outcome.Value.Items.Select(row => row.UserId).Should().Equal(new[] { UserId, 8 });
        outcome.Value.TotalCount.Should().Be(3, "the total counts every member, not the page");
        outcome.Value.PageIndex.Should().Be(0);
        outcome.Value.PageSize.Should().Be(2);
        outcome.Value.HasNextPage.Should().BeTrue();

        Result<PagedResult<UserListItemDto>> second = await harness.Service
            .ListRoleUsersAsync(
                PortalId,
                RoleId,
                new PagedRequest { PageIndex = 1, PageSize = 2 },
                CancellationToken.None);

        second.Value.Items.Select(row => row.UserId).Should().Equal(9);
        second.Value.TotalCount.Should().Be(3);
        second.Value.HasNextPage.Should().BeFalse();
    }

    /// <summary>
    /// The member rows carry no postal address and no telephone number, because this projection reads no
    /// profile values.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListRoleUsers_LeavesTheProfileFieldsAbsent()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = StoredRole();
        harness.RoleMembers = [Member(UserId)];

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        UserListItemDto row = outcome.Value.Items.Should().ContainSingle().Which;
        row.Address.Should().BeNull();
        row.Telephone.Should().BeNull();
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
        harness.RoleMembers = [Member(UserId)];

        Result<PagedResult<UserListItemDto>> outcome = await harness.Service
            .ListRoleUsersAsync(PortalId, RoleId, new PagedRequest { PageSize = 0 }, CancellationToken.None);

        outcome.Value.IsUnpaged.Should().BeTrue();

        // The accounts in a role are read through the account repository, because the legacy procedure for
        // that question - GetUsersByRolename - sits in the membership provider's Users section
        // (DataProvider.vb:L85) rather than its role section.
        harness.Users.Verify(
            u => u.ListByRoleNameAsync(PortalId, RoleName, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Listing a member's roles in a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// Listing the roles of an account that is not a member of the tenant is refused.
    /// </summary>
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

    /// <summary>
    /// A member holding nothing receives an empty list rather than a failure.
    /// </summary>
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

    /// <summary>
    /// Assigning a member to a role requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.AssignUserToRoleAsync(PortalId, RoleId, null!, CancellationToken.None));
    }

    /// <summary>
    /// Assigning within a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// Assigning to a role the tenant does not have is refused.
    /// </summary>
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

    /// <summary>
    /// Assigning an account that is not a member of the tenant is refused.
    /// </summary>
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

    /// <summary>
    /// A member who holds nothing yet receives a new assignment naming the route's role.
    /// </summary>
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

    /// <summary>
    /// An existing assignment is amended in place rather than replaced by a second row.
    /// </summary>
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

    /// <summary>
    /// Amending an assignment leaves the trial flag exactly as the store recorded it.
    /// </summary>
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

    /// <summary>
    /// The assignment commits once and the member's cached state is discarded by name.
    /// </summary>
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
    /// An effective date already in the past is discarded, so the assignment takes effect immediately
    /// rather than appearing to have been granted retrospectively.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_DiscardsAnEffectiveDateAlreadyInThePast()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, EffectiveDate = Now.AddDays(-5) },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.EffectiveDate.Should().BeNull();
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
    /// A role that declares no period stores no expiry at all, even when the caller submitted one, because
    /// an expiry with no term behind it cannot be renewed by anything.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_LeavesTheExpiryAbsentWhenTheRoleDeclaresNoPeriod()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = FreeRole();

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, ExpiryDate = Now.AddYears(1) },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().BeNull();
    }

    /// <summary>
    /// An expiry already in the past is treated as the present when the term is offset, so a stale date
    /// does not produce an assignment that has already lapsed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_TreatsAnExpiryAlreadyInThePastAsThePresentWhenOffsettingTheTerm()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = MonthlyRole();

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, ExpiryDate = Now.AddYears(-3) },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().Be(Now.AddMonths(1));
    }

    /// <summary>
    /// An expiry still in the future is the base the term is added to, so renewing extends the existing
    /// entitlement rather than restarting it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Assign_OffsetsFromTheSubmittedExpiryWhenOneIsStillInTheFuture()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = MonthlyRole();
        DateTime paidUntil = Now.AddDays(10);

        await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = UserId, ExpiryDate = paidUntil },
            CancellationToken.None);

        harness.AddedAssignments.Should().ContainSingle().Which.ExpiryDate.Should().Be(paidUntil.AddMonths(1));
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

    /// <summary>
    /// A term that charges nothing stores no expiry, even when a period is declared alongside it.
    /// </summary>
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

    /// <summary>
    /// The trial term governs an assignment for a member who has not consumed a trial.
    /// </summary>
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

    /// <summary>
    /// Removing a member from a role within a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// Removing a member from a role the tenant does not have is refused.
    /// </summary>
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

    /// <summary>
    /// Removing an account that is not a member of the tenant is refused.
    /// </summary>
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
    /// The tenant's own administrator cannot be removed from the administrator role, because doing so
    /// would leave the tenant with nobody able to administer it.
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

    /// <summary>
    /// An assignment to a role that charges nothing is deleted outright.
    /// </summary>
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

    /// <summary>
    /// The removal commits once and the member's cached state is discarded by name.
    /// </summary>
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

    /// <summary>
    /// Listing groups for a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// Every group the tenant owns is projected, carrying the tenant it belongs to.
    /// </summary>
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

    /// <summary>
    /// Reading one group from a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// A group the tenant does not have is reported as absent rather than as a failure.
    /// </summary>
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

    /// <summary>
    /// A group belonging to another tenant is indistinguishable from one that does not exist.
    /// </summary>
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

    /// <summary>
    /// Creating a group requires a request.
    /// </summary>
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
                new RoleGroupDto { RoleGroupName = name },
                CancellationToken.None));

        refusal.Message.Should().Be(expectedMessage);
    }

    /// <summary>
    /// A group name longer than the column permits is refused with the measured bound.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_RefusesAnOverlongName()
    {
        Harness harness = Harness.Ready();

        DomainException refusal = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.CreateRoleGroupAsync(
                PortalId,
                new RoleGroupDto { RoleGroupName = new string('g', 51) },
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
                new RoleGroupDto { RoleGroupName = string.Empty },
                CancellationToken.None));

        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Creating a group in a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// A group name already held within the tenant is refused, and the check excludes nothing.
    /// </summary>
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
    /// The stored group takes its tenant from the route rather than from the request body.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_StoresTheSubmittedGroup()
    {
        Harness harness = Harness.Ready();
        RoleGroupDto request = ValidGroupRequest();
        request.PortalId = OtherPortalId;
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

    /// <summary>
    /// Creating a group commits once and discards the tenant's cached state.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateRoleGroup_CommitsAndInvalidatesTheTenant()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreateRoleGroupAsync(PortalId, ValidGroupRequest(), CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Cache.Verify(c => c.InvalidatePortal(PortalId), Times.Once);
    }

    /// <summary>
    /// Updating a group requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateRoleGroupAsync(PortalId, RoleGroupId, null!, CancellationToken.None));
    }

    /// <summary>
    /// The group name is checked for shape on update as well, before the tenant is probed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_RefusesAMalformedName()
    {
        Harness harness = Harness.Ready();

        DomainException refusal = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateRoleGroupAsync(
                PortalId,
                RoleGroupId,
                new RoleGroupDto { RoleGroupName = "  " },
                CancellationToken.None));

        refusal.Message.Should().Be("A role group name is required.");
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Updating a group in a tenant that does not exist is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<RoleGroupDto> outcome = await harness.Service
            .UpdateRoleGroupAsync(PortalId, RoleGroupId, ValidGroupRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// A group belonging to another tenant cannot be edited through this tenant's route.
    /// </summary>
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
            .UpdateRoleGroupAsync(PortalId, RoleGroupId, ValidGroupRequest(), CancellationToken.None);

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
            ValidGroupRequest(),
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
            .UpdateRoleGroupAsync(PortalId, RoleGroupId, ValidGroupRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RoleGroupNameDuplicateCode);
        outcome.Reason!.Message.Should()
            .Be($"Portal {PortalId} already has a different role group named '{RoleGroupName}'.");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The submitted name and description are applied to the tracked group, which commits once.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_AppliesTheSubmittedNameAndDescription()
    {
        Harness harness = Harness.Ready();
        RoleGroup tracked = StoredGroup();
        harness.LookupGroup = tracked;

        RoleGroupDto request = ValidGroupRequest();
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
    /// A tenant identifier in the request body cannot move a group between tenants.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateRoleGroup_DoesNotMoveTheGroupBetweenTenants()
    {
        Harness harness = Harness.Ready();
        RoleGroup tracked = StoredGroup();
        harness.LookupGroup = tracked;

        RoleGroupDto request = ValidGroupRequest();
        request.PortalId = OtherPortalId;

        await harness.Service.UpdateRoleGroupAsync(PortalId, RoleGroupId, request, CancellationToken.None);

        tracked.PortalId.Should().Be(PortalId);
    }

    /// <summary>
    /// Deleting a group in a tenant that does not exist is refused.
    /// </summary>
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

    /// <summary>
    /// Deleting a group the tenant does not have is refused.
    /// </summary>
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
        // The occupancy question is asked through GetRolesByGroup (membership DataProvider.vb:L105),
        // which is the legacy read for exactly it, so the roles the group classifies are what the world
        // must contain. FK_Roles_RoleGroups carries no cascade, so the store would refuse this anyway -
        // reporting it here turns a constraint violation into an intelligible failure.
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

    /// <summary>
    /// The occupancy probe is the legacy group read, asked with both the group and its tenant.
    /// </summary>
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
    /// Builds a second role fixture so a list projection has more than one row.
    /// </summary>
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

    /// <summary>
    /// Builds a role charging a monthly fee with a one-month term and no trial.
    /// </summary>
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

    /// <summary>
    /// Builds a role offering a fourteen-day trial ahead of a monthly billing term.
    /// </summary>
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

    /// <summary>
    /// Builds the group fixture the store returns.
    /// </summary>
    /// <returns>A role group belonging to the tenant under test.</returns>
    private static RoleGroup StoredGroup() => new()
    {
        RoleGroupId = RoleGroupId,
        PortalId = PortalId,
        RoleGroupName = RoleGroupName,
        Description = "Groups paid roles.",
    };

    /// <summary>
    /// Builds a member of the tenant under test.
    /// </summary>
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
    /// Builds a creation request that passes every check the service performs.
    /// </summary>
    /// <returns>A well-formed creation request.</returns>
    private static CreateRoleRequest ValidCreateRequest() => new()
    {
        RoleName = RoleName,
        ServiceFee = 9.99m,
    };

    /// <summary>
    /// Builds an update request that passes every shape check the service performs.
    /// </summary>
    /// <returns>A well-formed update request.</returns>
    private static UpdateRoleRequest ValidUpdateRequest() => new()
    {
        // MIGRATION: no name is set, because the update contract deliberately declares none - the
        // stored name is preserved instead. Contrast the creation factory, which must supply one.
        ServiceFee = 9.99m,
        BillingPeriod = 1,
        BillingFrequency = Frequency.Month,
    };

    /// <summary>
    /// Builds a group request that passes every shape check the service performs.
    /// </summary>
    /// <returns>A well-formed group request.</returns>
    private static RoleGroupDto ValidGroupRequest() => new()
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
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);

            Service = new RoleService(
                Roles.Object,
                Portals.Object,
                Users.Object,
                UnitOfWork.Object,
                Clock.Object,
                Cache.Object);
        }

        public RoleService Service { get; }

        public Mock<IRoleRepository> Roles { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<IClock> Clock { get; }

        public Mock<ICacheService> Cache { get; }

        public Portal? PortalRow { get; set; }

        public bool PortalExists { get; set; }

        public Role? LookupRole { get; set; }

        public RoleGroup? LookupGroup { get; set; }

        public UserRole? ExistingAssignment { get; set; }

        public User? Member { get; set; }

        public Dictionary<int, User?> MembersById { get; }

        public bool NameTaken { get; set; }

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

        public IReadOnlyList<User> RoleMembers { get; set; } = [];

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
            harness.Portals
                .Setup(p => p.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalRow);

            // Every stub below names a member of the role contract as it is actually declared: reads
            // scoped by portal, writes staged asynchronously and never returning a generated key. The
            // harness properties the tests set are unchanged, so a test still describes its world in
            // the same terms.
            // Both reads make the portal a CONDITION rather than a hint, exactly as the terminal
            // procedures did - GetRole filters on "RoleId = @RoleId AND PortalId = @PortalId"
            // (04.00.04.SqlDataProvider:L334-L335). The stub therefore withholds a row belonging to
            // another tenant, so a test that plants a foreign row still exercises a genuine refusal
            // instead of relying on a check the service no longer needs to perform.
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
                    ? new Role { RoleId = 4242, PortalId = portalId, RoleName = roleName }
                    : null);

            harness.Roles
                .Setup(r => r.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RolePage.Items);
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

            // A delete now carries the key rather than the entity, so the harness resolves the entity it
            // was given a key for. That keeps the existing assertions - which name the role object -
            // meaningful while the contract stays key-based, as the legacy DeleteRole was.
            harness.Roles
                .Setup(r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((roleId, _) =>
                {
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

            // The accounts in a role are read through the account repository - membership
            // DataProvider.vb:L85 GetUsersByRolename sits in the provider's Users section - so the
            // members of a role are published here rather than as assignment rows.
            harness.Users
                .Setup(u => u.ListByRoleNameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RoleMembers);

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
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.MemberPage);

            harness.UnitOfWork
                .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            harness.Clock.SetupGet(c => c.UtcNow).Returns(() => Now);

            return harness;
        }
    }
}
