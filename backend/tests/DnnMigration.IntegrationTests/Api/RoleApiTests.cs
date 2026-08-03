using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the role and role-group resources, and the assignment of accounts to roles, end to end.
/// </summary>
/// <remarks>
/// <para>
/// Roles are the migration's authorisation currency: a role name in a token is what the permission gates
/// resolve against, and the seeded Administrators role is what the tenant-administration policy requires. Two
/// consequences are asserted here rather than assumed. Every route on both controllers demands that policy, so
/// an authenticated plain member is refused throughout; and two assignments are protected outright, because
/// removing them would strand a tenant - the designated administrator's membership of the administrators role,
/// and any membership of the registered-users role.
/// </para>
/// <para>
/// Paid membership is preserved from the legacy model and is exercised deliberately. A role carrying a billing
/// or trial frequency derives an expiry date on assignment; a free role does not, and requesting one for a free
/// role is discarded rather than honoured. Both halves are pinned, because the derivation is easy to
/// misread as "the caller's dates are stored".
/// </para>
/// <para>
/// Role names are unique per tenant, and role groups likewise, so every created name carries a random suffix.
/// The suites share one database and xUnit gives no ordering guarantee inside a collection.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RoleApiTests
{
    /// <summary>An identifier no seeded or created role can hold.</summary>
    private const int UnknownRoleId = 987654;

    /// <summary>An identifier no seeded or created account can hold.</summary>
    private const int UnknownUserId = 987654;

    /// <summary>A tenant identifier no seeded or created portal can hold.</summary>
    private const int UnknownPortalId = 987654;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RoleApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public RoleApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The collection answers <c>200 OK</c> and carries the three seeded roles.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_ReturnsOkContainingSeededRoles()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/roles?pageIndex=0&pageSize=100",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();

        IReadOnlyList<string> names = page!.Items.Select(item => item.RoleName).ToList();
        names.Should().Contain(IntegrationSeed.AdministratorsRoleName)
            .And.Contain(IntegrationSeed.RegisteredUsersRoleName)
            .And.Contain(IntegrationSeed.SubscribersRoleName);

        RoleListItemDto registered = page.Items
            .Should().ContainSingle(item => item.RoleName == IntegrationSeed.RegisteredUsersRoleName)
            .Subject;

        registered.AutoAssignment.Should().BeTrue();
        registered.IsPublic.Should().BeFalse();
    }

    /// <summary>
    /// The collection is refused to an authenticated account that does not hold the administrators role.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_AsPlainMember_ReturnsForbidden()
    {
        using HttpClient client = MemberClient();

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>The collection requires a bearer token.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_WithoutCredentials_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>An unknown tenant answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_ForUnknownPortal_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(UnknownPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A page size beyond the permitted ceiling is refused by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_WithPageSizeAboveTheCeiling_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/roles?pageIndex=0&pageSize=5000",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Narrowing the collection to an unknown group answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_ForUnknownRoleGroup_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/roles?roleGroupId={Route(UnknownRoleId)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A read answers <c>200 OK</c> and carries the stored role row.</summary>
    /// <remarks>
    /// The response does not echo the owning portal, and this test deliberately does not look for it.
    /// Which tenant owns the role is established by the route that reached it, and that is asserted
    /// where it belongs - by
    /// <see cref="GetRole_WhenRoleBelongsToAnotherTenant_ReturnsNotFound"/>, which proves the same role
    /// identifier is unreachable through a different portal's route. Trusting a self-reported
    /// identifier in the payload would be the weaker of the two checks.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRole_ReturnsOkWithTheStoredRole()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, _fixture.Seed.AdministratorRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleDetailDto detail = await ReadDetailAsync(response);
        detail.RoleId.Should().Be(_fixture.Seed.AdministratorRoleId);
        detail.RoleName.Should().Be(IntegrationSeed.AdministratorsRoleName);
    }

    /// <summary>An unknown role answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRole_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, UnknownRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A role that exists in another tenant answers <c>404 Not Found</c> when addressed through this one, so a
    /// role identifier alone grants no reach across tenants.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRole_WhenRoleBelongsToAnotherTenant_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();
        int otherPortalId = await CreateIsolatedPortalAsync(client);

        using HttpResponseMessage response = await client.GetAsync(
            RoleRoute(otherPortalId, _fixture.Seed.AdministratorRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A create answers <c>201 Created</c> with a location that resolves, and persists.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_ReturnsCreatedAndPersists()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateRoleRequest request = NewRoleRequest();
        request.Description = "Created by the integration suite.";
        request.IsPublic = true;
        request.RsvpCode = "RSVP" + Suffix();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto created = await ReadDetailAsync(response);
        created.RoleName.Should().Be(request.RoleName);
        created.Description.Should().Be(request.Description);
        created.IsPublic.Should().BeTrue();
        created.AutoAssignment.Should().BeFalse();
        created.RsvpCode.Should().Be(request.RsvpCode);

        // Roles.RoleID is IDENTITY(0, 1), so zero is a legitimate identifier and must not be read as absent.
        created.RoleId.Should().BeGreaterThanOrEqualTo(0);

        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/roles/{Route(created.RoleId)}");

        using HttpResponseMessage followed = await client.GetAsync(
            new Uri(response.Headers.Location.OriginalString, UriKind.Relative));

        followed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(followed)).RoleName.Should().Be(request.RoleName);

        int rows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleID] = @roleId AND [PortalID] = @portalId;",
            new Dictionary<string, object?>
            {
                ["roleId"] = created.RoleId,
                ["portalId"] = _fixture.Seed.PortalId,
            });

        rows.Should().Be(1);
    }

    /// <summary>
    /// A role created with automatic assignment enrols the tenant's existing accounts, which is the behaviour
    /// that makes the flag meaningful rather than merely stored.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithAutomaticAssignment_EnrolsExistingAccounts()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateRoleRequest request = NewRoleRequest();
        request.AutoAssignment = true;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto created = await ReadDetailAsync(response);
        created.AutoAssignment.Should().BeTrue();

        // The tally is read from the assignment table directly rather than from the response, because the
        // detail contract carries no member count - it holds only columns of the role row itself.
        (await CountAssignmentsAsync(created.RoleId)).Should().BeGreaterThanOrEqualTo(2);

        using HttpResponseMessage members = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/roles/{Route(created.RoleId)}/users"
                + "?pageIndex=0&pageSize=100",
            UriKind.Relative));

        members.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? page = await members.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Select(item => item.Username).Should()
            .Contain(IntegrationSeed.AdminUserName)
            .And.Contain(IntegrationSeed.MemberUserName);
    }

    /// <summary>A second role bearing an existing name in the same tenant answers <c>409 Conflict</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithDuplicateName_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateRoleRequest duplicate = NewRoleRequest();
        duplicate.RoleName = IntegrationSeed.SubscribersRoleName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            duplicate,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("already has a role named");
    }

    /// <summary>
    /// The same role name is free in a different tenant, which is what makes the uniqueness rule tenant-scoped
    /// rather than installation-wide. Both halves of that claim are asserted here: the name genuinely collides
    /// inside the tenant that already holds it, and it is genuinely accepted in a second tenant.
    /// </summary>
    /// <remarks>
    /// The name under test is minted for this test rather than borrowed from the seed. Creating a portal always
    /// provisions the three default roles — administrators, registered users and subscribers — so any of those
    /// three names is already taken in a freshly created tenant and would prove nothing about scoping.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithANameUsedByAnotherTenant_ReturnsCreated()
    {
        using HttpClient client = _fixture.CreateHostClient();

        RoleDetailDto held = await CreateRoleAsync(client);
        int otherPortalId = await CreateIsolatedPortalAsync(client);

        CreateRoleRequest inTheSameTenant = NewRoleRequest();
        inTheSameTenant.RoleName = held.RoleName;

        using HttpResponseMessage collision = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            inTheSameTenant,
            ApiTestFixture.Json);

        collision.StatusCode.Should().Be(HttpStatusCode.Conflict);

        CreateRoleRequest inTheOtherTenant = NewRoleRequest();
        inTheOtherTenant.RoleName = held.RoleName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(otherPortalId),
            inTheOtherTenant,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto accepted = await ReadDetailAsync(response);
        accepted.RoleName.Should().Be(held.RoleName);
        accepted.RoleId.Should().NotBe(held.RoleId);

        // Which tenant now owns the accepted role is proved by ROUTE rather than by a self-reported
        // identifier in the payload: it is readable through the other portal and unreachable through the
        // seeded one. That is the stronger of the two checks, and it is why the detail contract does not
        // echo the owning portal back.
        using HttpResponseMessage throughOwner = await client.GetAsync(
            RoleRoute(otherPortalId, accepted.RoleId));
        throughOwner.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage throughSeed = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, accepted.RoleId));
        throughSeed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A create naming a group the tenant does not hold answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithUnknownRoleGroup_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateRoleRequest request = NewRoleRequest();
        request.RoleGroupId = UnknownRoleId;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A missing role name is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithoutName_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateRoleRequest request = NewRoleRequest();
        request.RoleName = string.Empty;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Role Name Is Required.");
    }

    /// <summary>A negative service fee is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithNegativeServiceFee_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateRoleRequest request = NewRoleRequest();
        request.ServiceFee = -1m;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Service Fee Must Be Greater Than or Equal to Zero");
    }

    /// <summary>A billing period of zero is rejected, because a period must be a positive count.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithZeroBillingPeriod_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateRoleRequest request = NewRoleRequest();
        request.BillingPeriod = 0;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Billing Period Must Be Greater Than Zero");
    }

    /// <summary>An update answers <c>200 OK</c> and the new state survives a read.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_ReturnsOkAndPersists()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        var request = new UpdateRoleRequest
        {
            RoleName = created.RoleName,
            Description = "Amended by the integration suite.",
            IsPublic = true,
            AutoAssignment = false,
            ServiceFee = 12.50m,
            BillingPeriod = 1,
            BillingFrequency = BillingFrequency.Month,
            IconFile = "role.gif",
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleDetailDto updated = await ReadDetailAsync(response);
        updated.Description.Should().Be(request.Description);
        updated.IsPublic.Should().BeTrue();
        updated.ServiceFee.Should().Be(12.50m);
        updated.BillingPeriod.Should().Be(1);
        updated.IconFile.Should().Be("role.gif");

        // The billing frequency is stored as a single character and read back through a value converter, so a
        // round trip is the only thing that proves the conversion is symmetrical.
        updated.BillingFrequency.Should().Be(BillingFrequency.Month);

        using HttpResponseMessage reread = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleDetailDto persisted = await ReadDetailAsync(reread);
        persisted.BillingFrequency.Should().Be(BillingFrequency.Month);
        persisted.ServiceFee.Should().Be(12.50m);

        string storedCode = await _fixture.Database.ScalarAsync<string>(
            "SELECT [BillingFrequency] FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        storedCode.Should().Be("M", "the legacy schema stores the frequency as a single character");
    }

    /// <summary>
    /// The billing frequency is spelled on the wire with its legacy single-character code, not with
    /// the name of the enumeration member.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Every other assertion about this value reads the response back into a typed object, which
    /// serialises and deserialises through the same options and therefore passes whatever spelling
    /// those options happen to produce. This test reads the RAW body instead, because the spelling
    /// itself is the contract: <c>dbo.Roles.BillingFrequency</c> and <c>dbo.Roles.TrialFrequency</c>
    /// are <c>char(1)</c> columns holding N, O, D, W, M or Y, and a consumer of this API reads and
    /// writes those codes.
    /// </para>
    /// <para>
    /// It also pins a start-up ordering that nothing else would catch. The converter that produces
    /// the code is selected only because it is registered ahead of the general enumeration
    /// converter; reverse the two registrations and the wire form silently becomes "Month" while
    /// every typed round trip keeps passing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetRole_SpellsTheBillingFrequencyWithItsLegacyCode()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        var request = new UpdateRoleRequest
        {
            RoleName = created.RoleName,
            Description = created.Description,
            IsPublic = created.IsPublic,
            AutoAssignment = created.AutoAssignment,
            ServiceFee = 5.00m,
            BillingPeriod = 1,
            BillingFrequency = BillingFrequency.Month,
            TrialPeriod = 2,
            TrialFrequency = BillingFrequency.Week,
        };

        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId),
            request,
            ApiTestFixture.Json);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain(
            "\"billingFrequency\":\"M\"",
            "the wire form of this value is the legacy char(1) code");
        body.Should().Contain(
            "\"trialFrequency\":\"W\"",
            "the trial frequency shares the same reading as the billing frequency");
        body.Should().NotContain(
            "\"Month\"",
            "a member name on the wire would be a spelling no legacy consumer recognises");

        // The request half is proven by the same body: the update above SENT "M" and "W", so a
        // response that reports them is a response to a request the API accepted in that form.
        RoleDetailDto typed = await ReadDetailAsync(response);
        typed.BillingFrequency.Should().Be(BillingFrequency.Month);
        typed.TrialFrequency.Should().Be(BillingFrequency.Week);
    }

    /// <summary>An update against an unknown role answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, UnknownRoleId),
            new UpdateRoleRequest { RoleName = "Absent" + Suffix() },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Renaming a role onto another role's name in the same tenant answers <c>409 Conflict</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_RenamingOntoAnExistingName_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId),
            new UpdateRoleRequest { RoleName = IntegrationSeed.SubscribersRoleName },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// A delete answers <c>204 No Content</c>, removes the role, and takes its assignments with it so no
    /// account is left holding a role that no longer exists.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteRole_ReturnsNoContentAndRemovesItsAssignments()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CountAssignmentsAsync(created.RoleId)).Should().Be(1);

        using HttpResponseMessage response = await client.DeleteAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage reread = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId));

        reread.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await CountAssignmentsAsync(created.RoleId)).Should().Be(0);

        int rows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        rows.Should().Be(0);
    }

    /// <summary>A delete against an unknown role answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteRole_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.DeleteAsync(
            RoleRoute(_fixture.Seed.PortalId, UnknownRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An assignment round-trips: it answers <c>204 No Content</c>, appears on both the role's account list and
    /// the account's role list, and is removed again with <c>204 No Content</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_RoundTripsThroughBothProjections()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage roleMembers = await client.GetAsync(
            new Uri(RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId).OriginalString
                + "?pageIndex=0&pageSize=100", UriKind.Relative));

        roleMembers.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? members = await roleMembers.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        members.Should().NotBeNull();
        members!.Items.Select(item => item.UserId).Should().Contain(_fixture.Seed.MemberUserId);

        using HttpResponseMessage accountRoles = await client.GetAsync(
            UserRolesRoute(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId));

        accountRoles.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<RoleListItemDto>? held = await accountRoles.Content
            .ReadFromJsonAsync<IReadOnlyList<RoleListItemDto>>(ApiTestFixture.Json);

        held.Should().NotBeNull();
        held!.Select(item => item.RoleId).Should().Contain(created.RoleId);

        using HttpResponseMessage removed = await client.DeleteAsync(
            RoleUserRoute(_fixture.Seed.PortalId, created.RoleId, _fixture.Seed.MemberUserId));

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CountAssignmentsAsync(created.RoleId)).Should().Be(0);
    }

    /// <summary>
    /// Assigning an account that already holds the role amends the existing assignment rather than adding a
    /// second one, so the operation is idempotent in the only sense that matters - the account holds the role
    /// exactly once.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_WhenRepeated_AmendsRatherThanDuplicates()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpResponseMessage assigned = await client.PostAsJsonAsync(
                RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId),
                new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
                ApiTestFixture.Json);

            assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await CountAssignmentsAsync(created.RoleId)).Should().Be(1);
    }

    /// <summary>
    /// A free role derives no expiry date, and a date the caller submits for one is discarded rather than
    /// stored.
    /// </summary>
    /// <remarks>
    /// This is the half of the derivation that is easiest to misread. The legacy rule is that the role's own
    /// billing or trial period governs the expiry; a role with no period has nothing to expire, so a submitted
    /// date has no meaning and is dropped. Its paid counterpart is the companion test below.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_ForAFreeRole_DiscardsASubmittedExpiryDate()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId),
            new RoleAssignmentRequest
            {
                UserId = _fixture.Seed.MemberUserId,
                ExpiryDate = DateTime.UtcNow.AddYears(5),
            },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int dated = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[UserRoles]
            WHERE [RoleID] = @roleId AND [UserID] = @userId AND [ExpiryDate] IS NOT NULL;
            """,
            new Dictionary<string, object?>
            {
                ["roleId"] = created.RoleId,
                ["userId"] = _fixture.Seed.MemberUserId,
            });

        dated.Should().Be(0, "a role with no billing or trial period has nothing to expire");
    }

    /// <summary>
    /// A role carrying a billing period derives an expiry date from that period, so a paid membership ends
    /// without anybody having to remember to end it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_ForAPaidRole_DerivesAnExpiryDateFromTheBillingPeriod()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateRoleRequest request = NewRoleRequest();
        request.ServiceFee = 5m;
        request.BillingPeriod = 2;
        request.BillingFrequency = BillingFrequency.Month;

        using HttpResponseMessage createdResponse = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        createdResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        RoleDetailDto created = await ReadDetailAsync(createdResponse);

        DateTime before = DateTime.UtcNow;

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        DateTime expiry = await _fixture.Database.ScalarAsync<DateTime>(
            """
            SELECT MAX([ExpiryDate])
            FROM [dbo].[UserRoles]
            WHERE [RoleID] = @roleId AND [UserID] = @userId;
            """,
            new Dictionary<string, object?>
            {
                ["roleId"] = created.RoleId,
                ["userId"] = _fixture.Seed.MemberUserId,
            });

        // Two months on from the moment of assignment, allowing for the time the request itself took.
        expiry.Should().BeAfter(before.AddMonths(2).AddMinutes(-5))
            .And.BeBefore(before.AddMonths(2).AddMinutes(5));
    }

    /// <summary>An assignment naming an account outside the tenant answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_ForUnknownAccount_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId),
            new RoleAssignmentRequest { UserId = UnknownUserId },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>An assignment against an unknown role answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_ForUnknownRole_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, UnknownRoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Removing an assignment that is not held answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RemoveAssignment_WhenNotHeld_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto created = await CreateRoleAsync(client);

        using HttpResponseMessage response = await client.DeleteAsync(
            RoleUserRoute(_fixture.Seed.PortalId, created.RoleId, _fixture.Seed.MemberUserId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The designated administrator may not be removed from the administrators role, because a tenant whose
    /// administrator holds no administrative role cannot be administered.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RemoveAssignment_ForTheDesignatedAdministrator_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.DeleteAsync(RoleUserRoute(
            _fixture.Seed.PortalId,
            _fixture.Seed.AdministratorRoleId,
            _fixture.Seed.AdminUserId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("protected");

        int remaining = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[UserRoles]
            WHERE [RoleID] = @roleId AND [UserID] = @userId;
            """,
            new Dictionary<string, object?>
            {
                ["roleId"] = _fixture.Seed.AdministratorRoleId,
                ["userId"] = _fixture.Seed.AdminUserId,
            });

        remaining.Should().Be(1, "a refused removal must leave the assignment exactly as it was");
    }

    /// <summary>
    /// No account may be removed from the registered-users role, which is the membership that makes an account
    /// a member of the tenant at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RemoveAssignment_FromTheRegisteredUsersRole_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.DeleteAsync(RoleUserRoute(
            _fixture.Seed.PortalId,
            _fixture.Seed.RegisteredRoleId,
            _fixture.Seed.MemberUserId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>The account-roles projection answers <c>404 Not Found</c> for an unknown account.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListUserRoles_WhenAccountIsUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            UserRolesRoute(_fixture.Seed.PortalId, UnknownUserId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The role-accounts projection answers <c>404 Not Found</c> for an unknown role.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoleUsers_WhenRoleIsUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, UnknownRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The role-group resource supports the full round trip, ending in <c>204 No Content</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleGroups_SupportCreateReadUpdateAndDelete()
    {
        using HttpClient client = _fixture.CreateHostClient();

        RoleGroupDto created = await CreateRoleGroupAsync(client);
        created.PortalId.Should().Be(_fixture.Seed.PortalId);

        // RoleGroups.RoleGroupID is IDENTITY(0, 1), so zero is a legitimate identifier here too.
        created.RoleGroupId.Should().BeGreaterThanOrEqualTo(0);

        Uri itemRoute = RoleGroupRoute(_fixture.Seed.PortalId, created.RoleGroupId);

        using HttpResponseMessage read = await client.GetAsync(itemRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleGroupDto? fetched = await read.Content.ReadFromJsonAsync<RoleGroupDto>(ApiTestFixture.Json);
        fetched.Should().NotBeNull();
        fetched!.RoleGroupName.Should().Be(created.RoleGroupName);

        using HttpResponseMessage listed = await client.GetAsync(RoleGroupsRoute(_fixture.Seed.PortalId));
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<RoleGroupDto>? all = await listed.Content
            .ReadFromJsonAsync<IReadOnlyList<RoleGroupDto>>(ApiTestFixture.Json);

        all.Should().NotBeNull();
        all!.Select(item => item.RoleGroupId).Should().Contain(created.RoleGroupId);

        created.Description = "Amended by the integration suite.";

        using HttpResponseMessage updated = await client.PutAsJsonAsync(itemRoute, created, ApiTestFixture.Json);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleGroupDto? afterUpdate = await updated.Content
            .ReadFromJsonAsync<RoleGroupDto>(ApiTestFixture.Json);

        afterUpdate.Should().NotBeNull();
        afterUpdate!.Description.Should().Be("Amended by the integration suite.");

        using HttpResponseMessage removed = await client.DeleteAsync(itemRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage gone = await client.GetAsync(itemRoute);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A second group bearing an existing name answers <c>409 Conflict</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRoleGroup_WithDuplicateName_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleGroupDto first = await CreateRoleGroupAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleGroupsRoute(_fixture.Seed.PortalId),
            new RoleGroupDto { RoleGroupName = first.RoleGroupName },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// A group that still classifies a role is refused deletion, so no role is left pointing at a group that
    /// no longer exists.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteRoleGroup_WhileItStillClassifiesARole_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleGroupDto group = await CreateRoleGroupAsync(client);

        CreateRoleRequest request = NewRoleRequest();
        request.RoleGroupId = group.RoleGroupId;

        using HttpResponseMessage classified = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        classified.StatusCode.Should().Be(HttpStatusCode.Created);
        RoleDetailDto role = await ReadDetailAsync(classified);

        // The role identifies its group; it does not name it. The name is the group contract's to carry.
        role.RoleGroupId.Should().Be(group.RoleGroupId);

        using HttpResponseMessage refused = await client.DeleteAsync(
            RoleGroupRoute(_fixture.Seed.PortalId, group.RoleGroupId));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await refused.Content.ReadAsStringAsync();
        body.Should().Contain("still classifies");

        // Removing the role releases the group, and the deletion then succeeds - which is what makes the
        // refusal above a guard rather than a permanent obstacle.
        using HttpResponseMessage roleRemoved = await client.DeleteAsync(
            RoleRoute(_fixture.Seed.PortalId, role.RoleId));

        roleRemoved.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage groupRemoved = await client.DeleteAsync(
            RoleGroupRoute(_fixture.Seed.PortalId, group.RoleGroupId));

        groupRemoved.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>An unknown group answers <c>404 Not Found</c> on both read and delete.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleGroup_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage read = await client.GetAsync(
            RoleGroupRoute(_fixture.Seed.PortalId, UnknownRoleId));

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage removed = await client.DeleteAsync(
            RoleGroupRoute(_fixture.Seed.PortalId, UnknownRoleId));

        removed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Role-group administration is refused to an account without the administrators role.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleGroups_AsPlainMember_ReturnsForbidden()
    {
        using HttpClient client = MemberClient();

        using HttpResponseMessage response = await client.GetAsync(RoleGroupsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Creates a role through the API and returns its representation.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <returns>The created role.</returns>
    private async Task<RoleDetailDto> CreateRoleAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            NewRoleRequest(),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return await ReadDetailAsync(response);
    }

    /// <summary>Creates a role group through the API and returns its representation.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <returns>The created group.</returns>
    private async Task<RoleGroupDto> CreateRoleGroupAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleGroupsRoute(_fixture.Seed.PortalId),
            new RoleGroupDto
            {
                RoleGroupName = "ITest Group " + Suffix(),
                Description = "Created by the integration suite.",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleGroupDto? created = await response.Content
            .ReadFromJsonAsync<RoleGroupDto>(ApiTestFixture.Json);

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>Creates a tenant of this test's own, so a tenant-scoped assertion cannot be disturbed.</summary>
    /// <param name="client">A client carrying host credentials.</param>
    /// <returns>The new tenant's identifier.</returns>
    private async Task<int> CreateIsolatedPortalAsync(HttpClient client)
    {
        string suffix = Suffix();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "Role Suite Portal " + suffix,
                portalAlias = "roles-" + suffix + ".local",
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Suite",
                administratorLastName = "Administrator",
                administratorUsername = "roles_admin_" + suffix,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "roles." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("portalId").GetInt32();
    }

    /// <summary>Counts the assignments held against one role.</summary>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>The number of assignment rows.</returns>
    private Task<int> CountAssignmentsAsync(int roleId) => _fixture.Database.ScalarAsync<int>(
        "SELECT COUNT(*) FROM [dbo].[UserRoles] WHERE [RoleID] = @roleId;",
        new Dictionary<string, object?> { ["roleId"] = roleId });

    /// <summary>Builds a client for the seeded account that holds no administrative role.</summary>
    /// <returns>An authenticated client without the administrators role.</returns>
    private HttpClient MemberClient() => _fixture.CreateClientFor(
        _fixture.Seed.MemberUserId,
        IntegrationSeed.MemberUserName,
        _fixture.Seed.PortalId,
        isSuperUser: false,
        roles: [IntegrationSeed.RegisteredUsersRoleName]);

    /// <summary>Builds a free-role create request whose name carries a random suffix.</summary>
    /// <returns>A well formed create request.</returns>
    private static CreateRoleRequest NewRoleRequest() => new()
    {
        RoleName = "ITest Role " + Suffix(),
        Description = "Created by the integration suite.",
        IsPublic = false,
        AutoAssignment = false,
    };

    /// <summary>Reads a role representation out of a response, failing the test when it is absent.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The representation.</returns>
    private static async Task<RoleDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        RoleDetailDto? detail = await response.Content
            .ReadFromJsonAsync<RoleDetailDto>(ApiTestFixture.Json);

        detail.Should().NotBeNull();
        return detail!;
    }

    /// <summary>Builds the collection route for a tenant's roles.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RolesRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}/roles", UriKind.Relative);

    /// <summary>Builds the item route for one role.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleRoute(int portalId, int roleId) =>
        new($"/api/v1/portals/{Route(portalId)}/roles/{Route(roleId)}", UriKind.Relative);

    /// <summary>Builds the accounts sub-resource route for one role.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleUsersRoute(int portalId, int roleId) =>
        new($"/api/v1/portals/{Route(portalId)}/roles/{Route(roleId)}/users", UriKind.Relative);

    /// <summary>Builds the route for one account's membership of one role.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleUserRoute(int portalId, int roleId, int userId) => new(
        $"/api/v1/portals/{Route(portalId)}/roles/{Route(roleId)}/users/{Route(userId)}",
        UriKind.Relative);

    /// <summary>Builds the roles-of-an-account projection route.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UserRolesRoute(int portalId, int userId) =>
        new($"/api/v1/portals/{Route(portalId)}/users/{Route(userId)}/roles", UriKind.Relative);

    /// <summary>Builds the collection route for a tenant's role groups.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleGroupsRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}/role-groups", UriKind.Relative);

    /// <summary>Builds the item route for one role group.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="roleGroupId">The group identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleGroupRoute(int portalId, int roleGroupId) =>
        new($"/api/v1/portals/{Route(portalId)}/role-groups/{Route(roleGroupId)}", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
