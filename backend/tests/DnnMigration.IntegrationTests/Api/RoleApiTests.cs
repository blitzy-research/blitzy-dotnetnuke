using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>Covers the role and role-group resources, and the assignment of accounts to roles, end to end.</summary>
/// <remarks>
/// <para>
/// Roles are the migration's authorisation currency: a role name in a token is what the permission gates
/// resolve against, and the seeded Administrators role is what the tenant-administration policy requires.
/// Two consequences are asserted here rather than assumed.
/// </para>
/// <para>
/// Role names are unique per tenant, and role groups likewise, so every created name carries a random
/// suffix. The suites share one database and xUnit gives no ordering guarantee inside a collection.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RoleApiTests
{
    /// <summary>An identifier no seeded or created role can hold.</summary>
    /// <summary>The media type every refusal on these resources is served as.</summary>
    private const string ProblemMediaType = "application/problem+json";

    private const int UnknownRoleId = 987654;

    /// <summary>An identifier no seeded or created account can hold.</summary>
    private const int UnknownUserId = 987654;

    /// <summary>A tenant identifier no seeded or created portal can hold.</summary>
    private const int UnknownPortalId = 987654;

    /// <summary>
    /// The all-users pseudo-principal, which the legacy source defines as <c>glbRoleAllUsers = "-1"</c> at
    /// <c>Library/Components/Shared/Globals.vb</c>:L95 and stores in the grant tables' role column.
    /// </summary>
    private const int AllUsersPseudoRoleId = -1;

    /// <summary>The prefix a failure code is built into as the problem document's <c>type</c>.</summary>
    private const string ProblemTypePrefix = "urn:dnnmigration:error:";

    /// <summary>valRoleName's wording, the screen's one presence check.</summary>
    private const string RoleNameRequiredMessage = "You Must Enter a Valid Name";

    /// <summary>valServiceFee2's wording, whose operator agrees with it.</summary>
    private const string ServiceFeeNegativeMessage = "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>valBillingPeriod2's wording.</summary>
    private const string BillingPeriodNotPositiveMessage =
        "Billing Period Must Be Greater Than Zero";

    /// <summary>valTrialFee2's wording.</summary>
    private const string TrialFeeNegativeMessage = "Trial Fee Must Be Greater Than or Equal to Zero";

    /// <summary>valTrialPeriod2's wording, where text and operator agree.</summary>
    private const string TrialPeriodNotPositiveMessage = "Trial Period Must Be Greater Than Zero";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RoleApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public RoleApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The collection answers <c>200 OK</c> and carries the three seeded roles.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_ReturnsOkContainingSeededRoles()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/roles?pageIndex=0&pageSize=100",
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
        using HttpClient client = await MemberClientAsync();

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>The membership write path is validated at the BOUNDARY on its single canonical address.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The submitted expiry precedes its own effective date, which <c>valDates</c> - the
    /// <c>CompareValidator</c> at <c>Website/admin/Security/securityroles.ascx:L47</c>, operator
    /// <c>GreaterThan</c> - refused.
    /// </remarks>
    [Fact]
    public async Task Assignment_WithAnExpiryBeforeItsEffectiveDate_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);

        Uri route = RoleUsersRoute(_fixture.Seed.PortalId, role.RoleId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            route,
            new RoleAssignmentRequest
            {
                UserId = _fixture.Seed.MemberUserId,
                EffectiveDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiryDate = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(nameof(RoleAssignmentRequest.ExpiryDate))
            .And.Contain("Expiry Date must be Greater than Effective Date");

        // The refusal must be a refusal: no membership may be written.
        (await CountAssignmentsAsync(role.RoleId)).Should().Be(0);
    }

    /// <summary>
    /// The canonical address serves the whole role action set, acting on the tenant the request resolves.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The created role is removed at the end so this fact does not accumulate rows for later facts.
    /// </remarks>
    [Fact]
    public async Task CanonicalRoleAddress_ServesTheResolvedTenantAcrossItsActionSet()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        var collection = new Uri("/api/v1/roles", UriKind.Relative);

        using HttpResponseMessage listed = await client.GetAsync(
            new Uri("/api/v1/roles?pageIndex=0&pageSize=100", UriKind.Relative));

        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleListItemDto>? page = await listed.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Select(item => item.RoleId)
            .Should().Contain(_fixture.Seed.AdministratorRoleId,
                "the flat address acts on the tenant the request resolved to, which is the seeded portal");

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            collection,
            NewRoleRequest(),
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto role = await ReadDetailAsync(created);

        created.Headers.Location.Should().NotBeNull();
        created.Headers.Location!.OriginalString
            .Should().Be($"/api/v1/roles/{Route(role.RoleId)}",
                "the location is built from the address the caller used, so a flat create must not answer a nested one");

        var itemRoute = new Uri($"/api/v1/roles/{Route(role.RoleId)}", UriKind.Relative);

        using HttpResponseMessage read = await client.GetAsync(itemRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(read)).RoleName.Should().Be(role.RoleName);

        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            itemRoute,
            new UpdateRoleRequest
            {
                RoleName = role.RoleName,
                Description = "Amended through the flat address.",
            },
            ApiTestFixture.Json);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(updated)).Description.Should().Be("Amended through the flat address.");

        using HttpResponseMessage removed = await client.DeleteAsync(itemRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// The specified FLAT membership address grants and removes a membership, and both projections of it
    /// are readable there.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task FlatRoleMembershipAddress_GrantsReadsAndRemovesAMembership()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);

        var membersRoute = new Uri($"/api/v1/roles/{Route(role.RoleId)}/users", UriKind.Relative);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            membersRoute,
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage members = await client.GetAsync(
            new Uri($"/api/v1/roles/{Route(role.RoleId)}/users?pageIndex=0&pageSize=100", UriKind.Relative));

        members.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleMembershipDto>? page = await members.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleMembershipDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();

        RoleMembershipDto membership = page!.Items
            .Should().ContainSingle(item => item.UserId == _fixture.Seed.MemberUserId).Subject;

        membership.UserRoleId.Should().BeGreaterThan(0);
        membership.RoleId.Should().Be(role.RoleId);
        membership.RoleName.Should().Be(role.RoleName);
        membership.Username.Should().Be(IntegrationSeed.MemberUserName);

        using HttpResponseMessage held = await client.GetAsync(
            new Uri($"/api/v1/users/{Route(_fixture.Seed.MemberUserId)}/roles", UriKind.Relative));

        held.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<RoleListItemDto>? heldEnvelope = await held.Content
            .ReadFromJsonAsync<CollectionEnvelope<RoleListItemDto>>(ApiTestFixture.Json);

        heldEnvelope.Should().NotBeNull();
        heldEnvelope!.Data.Should().NotBeNull();
        heldEnvelope.Data!.Select(item => item.RoleId).Should().Contain(role.RoleId);

        using HttpResponseMessage removed = await client.DeleteAsync(
            new Uri(
                $"/api/v1/roles/{Route(role.RoleId)}/users/{Route(_fixture.Seed.MemberUserId)}",
                UriKind.Relative));

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CountAssignmentsAsync(role.RoleId)).Should().Be(0);
    }

    /// <summary>
    /// The pairing address answers ONE account's membership of ONE role, and names nobody in the request
    /// target.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task MembershipPairingAddress_AnswersOneMembershipAndNamesNobodyInTheTarget()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);

        var pairingRoute = new Uri(
            $"/api/v1/roles/{Route(role.RoleId)}/users/{Route(_fixture.Seed.MemberUserId)}",
            UriKind.Relative);

        pairingRoute.OriginalString.Should()
            .NotContain(IntegrationSeed.MemberUserName, "the request target names nobody");

        // Before the membership exists, the pairing answers the refusal that MEANS "holds nothing".
        using HttpResponseMessage beforeGrant = await client.GetAsync(pairingRoute);

        beforeGrant.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            new Uri($"/api/v1/roles/{Route(role.RoleId)}/users", UriKind.Relative),
            new RoleAssignmentRequest
            {
                UserId = _fixture.Seed.MemberUserId,
                EffectiveDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiryDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage read = await client.GetAsync(pairingRoute);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        read.RequestMessage!.RequestUri!.ToString().Should()
            .NotContain(IntegrationSeed.MemberUserName, "not even after the client resolved it");
        read.RequestMessage.RequestUri.Query.Should()
            .BeEmpty("the pairing is addressed by path alone, so nothing can be logged from a query");

        ApiEnvelope<RoleMembershipDto>? envelope = await read.Content
            .ReadFromJsonAsync<ApiEnvelope<RoleMembershipDto>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();

        RoleMembershipDto membership = envelope.Data!;

        membership.UserId.Should().Be(_fixture.Seed.MemberUserId);
        membership.RoleId.Should().Be(role.RoleId);
        membership.RoleName.Should().Be(role.RoleName);
        membership.Username.Should().Be(IntegrationSeed.MemberUserName);
        membership.UserRoleId.Should().BeGreaterThan(0);
        membership.EffectiveDate.Should().Be(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        membership.ExpiryDate.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        // The same row the LISTING publishes, so a consumer holds one shape whichever address it used.
        RoleMembershipDto listed = await ReadSingleMembershipAsync(client, role.RoleId);

        membership.UserRoleId.Should().Be(listed.UserRoleId);
        membership.DisplayName.Should().Be(listed.DisplayName);

        using HttpResponseMessage removed = await client.DeleteAsync(pairingRoute);

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage afterRemoval = await client.GetAsync(pairingRoute);

        afterRemoval.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The pairing address is closed to a caller who does not administer the resolved tenant, exactly as
    /// every other operation on this controller is.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task MembershipPairingAddress_RefusesACallerWhoDoesNotAdministerTheTenant()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        RoleDetailDto role = await CreateRoleAsync(administrator);

        var pairingRoute = new Uri(
            $"/api/v1/roles/{Route(role.RoleId)}/users/{Route(_fixture.Seed.MemberUserId)}",
            UriKind.Relative);

        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage refused = await member.GetAsync(pairingRoute);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage unauthenticated = await anonymous.GetAsync(pairingRoute);

        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The specified FLAT role-group address serves the whole group action set against the resolved tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task FlatRoleGroupAddress_ServesTheResolvedTenantAcrossItsActionSet()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        var collection = new Uri("/api/v1/role-groups", UriKind.Relative);

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            collection,
            new CreateRoleGroupRequest
            {
                RoleGroupName = "ITest Flat Group " + Suffix(),
                Description = "Created through the flat address.",
            },
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleGroupDto? group = await created.Content
            .ReadEnvelopeAsync<RoleGroupDto>();

        group.Should().NotBeNull();
        group!.PortalId.Should().Be(_fixture.Seed.PortalId,
            "the flat address files the group under the tenant the request resolved to");

        created.Headers.Location.Should().NotBeNull();
        created.Headers.Location!.OriginalString
            .Should().Be($"/api/v1/role-groups/{Route(group.RoleGroupId)}");

        var itemRoute = new Uri($"/api/v1/role-groups/{Route(group.RoleGroupId)}", UriKind.Relative);

        using HttpResponseMessage listed = await client.GetAsync(collection);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<RoleGroupDto>? all = await listed.Content
            .ReadFromJsonAsync<CollectionEnvelope<RoleGroupDto>>(ApiTestFixture.Json);

        all.Should().NotBeNull();
        all!.Data.Should().NotBeNull();
        all.Data!.Select(item => item.RoleGroupId).Should().Contain(group.RoleGroupId);

        // The UPDATE CONTRACT is submitted, not the response projection that was just read back.
        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            itemRoute,
            new UpdateRoleGroupRequest
            {
                RoleGroupName = group.RoleGroupName!,
                Description = "Amended through the flat address.",
            },
            ApiTestFixture.Json);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage read = await client.GetAsync(itemRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleGroupDto? fetched = await read.Content.ReadEnvelopeAsync<RoleGroupDto>();
        fetched.Should().NotBeNull();
        fetched!.Description.Should().Be("Amended through the flat address.");

        using HttpResponseMessage removed = await client.DeleteAsync(itemRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage gone = await client.GetAsync(itemRoute);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A portal administrator is served its OWN tenant's roles. This is the positive control for the two
    /// refusals below: without it a 403 there could equally mean the caller administers nothing at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_AsAdministratorOfTheResolvedTenant_ReturnsOk()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A portal administrator addressing another tenant's host is refused, rather than being served that
    /// tenant's roles.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The tenant a request runs under is resolved from its host header. The flat role family therefore has
    /// no portal segment a caller can use to override that context; a token for one portal sent to another
    /// portal's host is refused before the service is reached.
    /// </remarks>
    [Fact]
    public async Task ListRoles_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedTenant other = await CreateIsolatedPortalAsync(host);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        client.BaseAddress = new Uri($"http://{other.Alias}", UriKind.Absolute);

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(other.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The role-group collection of another tenant is refused as well, because the reconciliation belongs
    /// to the policy rather than to one controller.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoleGroups_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedTenant other = await CreateIsolatedPortalAsync(host);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        client.BaseAddress = new Uri($"http://{other.Alias}", UriKind.Absolute);

        using HttpResponseMessage response = await client.GetAsync(RoleGroupsRoute(other.PortalId));

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

    /// <summary>
    /// An administrator of one portal is refused on another portal's host, which proves the class-level
    /// tenant-administration policy is anchored to the resolved request tenant. An unclaimed host is
    /// refused identically.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_ByAnAdministratorOfADifferentPortal_ReturnsForbidden()
    {
        // A genuine, fully provisioned administrator of the seeded portal, sent from an unclaimed host.
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        client.BaseAddress = new Uri("http://unclaimed-" + Suffix() + ".invalid", UriKind.Absolute);

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(UnknownPortalId));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a portal this caller does not administer must refuse before the tenant is even looked up, so an "
            + "unauthorised caller cannot tell an existing portal from an absent one");
    }

    /// <summary>A different resolved tenant and an unclaimed host are both refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The refusal happens in authorisation, and it is deliberately the SAME refusal for a host bound to
    /// another tenant and a host bound to none: a caller with no reach into a tenant must not be able to
    /// use the difference between 403 and 404 to enumerate which tenants exist.
    /// </remarks>
    [Fact]
    public async Task ListRoles_ForATenantOtherThanTheResolvedOne_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedTenant other = await CreateIsolatedPortalAsync(host);

        using HttpClient existingTenant = await _fixture.CreateAdministratorClientAsync();
        existingTenant.BaseAddress = new Uri($"http://{other.Alias}", UriKind.Absolute);

        using HttpResponseMessage existing = await existingTenant.GetAsync(RolesRoute(other.PortalId));
        existing.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpClient absentTenant = await _fixture.CreateAdministratorClientAsync();
        absentTenant.BaseAddress = new Uri("http://unclaimed-" + Suffix() + ".invalid", UriKind.Absolute);

        using HttpResponseMessage absent = await absentTenant.GetAsync(RolesRoute(UnknownPortalId));
        absent.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The tenant's own administrator reaches it, which proves the refusal above is the binding and not a
        // blanket denial of every tenant but the seed.
        using HttpClient owner = await TenantClientAsync(other);

        using HttpResponseMessage reached = await owner.GetAsync(RolesRoute(other.PortalId));
        reached.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>A page size beyond the permitted ceiling is refused by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_WithPageSizeAboveTheCeiling_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/roles?pageIndex=0&pageSize=5000",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Narrowing the collection to an unknown group answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_ForUnknownRoleGroup_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/roles?roleGroupId={Route(UnknownRoleId)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A read answers <c>200 OK</c> and carries the stored role row.</summary>
    /// <remarks>
    /// The response does not echo the owning portal, and this test deliberately does not look for it. Which
    /// tenant owns the role is established by the route that reached it, and that is asserted where it
    /// belongs - by <see cref="GetRole_WhenRoleBelongsToAnotherTenant_ReturnsNotFound"/>, which proves the
    /// same role identifier is unreachable through a different portal's route.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRole_ReturnsOkWithTheStoredRole()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, UnknownRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A role that exists in another tenant answers <c>404 Not Found</c> when addressed through this one,
    /// so a role identifier alone grants no reach across tenants.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The other tenant is addressed as its OWN administrator, which is what makes this test about
    /// service-level tenant scoping rather than about authorisation. A caller that legitimately administers
    /// the other tenant still cannot see the seeded tenant's role through it, because the read is anchored
    /// to the resolved host.
    /// </remarks>
    [Fact]
    public async Task GetRole_WhenRoleBelongsToAnotherTenant_ReturnsNotFound()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedTenant other = await CreateIsolatedPortalAsync(host);

        using HttpClient client = await TenantClientAsync(other);

        using HttpResponseMessage response = await client.GetAsync(
            RoleRoute(other.PortalId, _fixture.Seed.AdministratorRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A create answers <c>201 Created</c> with a location that resolves, and persists.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_ReturnsCreatedAndPersists()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
            $"/api/v1/roles/{Route(created.RoleId)}");

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
    /// A role created with automatic assignment enrols the tenant's existing accounts, which is the
    /// behaviour that makes the flag meaningful rather than merely stored.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithAutomaticAssignment_EnrolsExistingAccounts()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
            $"/api/v1/roles/{Route(created.RoleId)}/users"
                + "?pageIndex=0&pageSize=100",
            UriKind.Relative));

        members.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleMembershipDto>? page = await members.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleMembershipDto>>(ApiTestFixture.Json);

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest duplicate = NewRoleRequest();
        duplicate.RoleName = IntegrationSeed.SubscribersRoleName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            duplicate,
            ApiTestFixture.Json);

        await ShouldCarryFailureCodeAsync(response, HttpStatusCode.Conflict, "role.name_duplicate");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("A role with the same name already exists. The role was not added.");
        body.Should().NotContain(
            "identifier",
            "a refusal may not publish the row identifier of the record it refers to");
    }

    /// <summary>
    /// Submitting the same new role name from several callers at once creates it exactly once, refuses
    /// every other caller as a conflict, and answers no caller with a server fault.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Measured against a live installation with the fault untranslated: ten simultaneous creations produced
    /// one 201, seven 409 and TWO 500s, with exactly one row stored. The store had behaved perfectly -
    /// <c>IX_RoleName</c> is unique over <c>(PortalID, RoleName)</c> and it kept one row - but the two
    /// racers whose inserts it refused were told the SERVER had failed.
    /// </remarks>
    [Fact]
    public async Task CreateRole_SubmittedConcurrentlyUnderOneName_CreatesItOnceWithoutAnyServerFault()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string contestedName = "ITest Race " + Suffix();

        // Enough callers to make the window worth opening, few enough that the suite stays quick.
        const int Callers = 12;

        IEnumerable<Task<HttpResponseMessage>> submissions = Enumerable.Range(0, Callers).Select(_ =>
        {
            CreateRoleRequest request = NewRoleRequest();
            request.RoleName = contestedName;

            return client.PostAsJsonAsync(
                RolesRoute(_fixture.Seed.PortalId),
                request,
                ApiTestFixture.Json);
        });

        HttpResponseMessage[] responses = await Task.WhenAll(submissions);

        try
        {
            HttpStatusCode[] statuses = [.. responses.Select(response => response.StatusCode)];

            statuses.Should().NotContain(
                status => (int)status >= 500,
                "the store refusing a duplicate is the store working correctly; answering 5xx reports a "
                + "server fault for it and raises a fault-level log entry for an ordinary collision");

            statuses.Count(status => status == HttpStatusCode.Created).Should().Be(
                1,
                "one caller wins the name and the rest must be refused, whichever of them arrives first");

            statuses.Where(status => status != HttpStatusCode.Created).Should().AllBeEquivalentTo(
                HttpStatusCode.Conflict,
                "every caller that did not win faces a name that is now taken, which is a conflict");

            foreach (HttpResponseMessage refused in responses
                .Where(response => response.StatusCode != HttpStatusCode.Created))
            {
                await ShouldCarryFailureCodeAsync(
                    refused,
                    HttpStatusCode.Conflict,
                    "role.name_duplicate");
            }

            (await CountRolesNamedAsync(contestedName)).Should().Be(
                1,
                "exactly one row may survive the contest, and a refused caller must leave nothing behind");
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// The same role name is free in a different tenant, which is what makes the uniqueness rule
    /// tenant-scoped rather than installation-wide. Both halves of that claim are asserted here: the name
    /// genuinely collides inside the tenant that already holds it, and it is genuinely accepted in a second
    /// tenant.
    /// </summary>
    /// <remarks>
    /// The name under test is minted for this test rather than borrowed from the seed. Creating a portal
    /// always provisions the three default roles — administrators, registered users and subscribers — so
    /// any of those three names is already taken in a freshly created tenant and would prove nothing about
    /// scoping.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithANameUsedByAnotherTenant_ReturnsCreated()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        RoleDetailDto held = await CreateRoleAsync(client);
        IsolatedTenant other = await CreateIsolatedPortalAsync(client);

        // Each tenant is addressed by its own caller, because every role route is tenant-scoped.
        using HttpClient owner = await TenantClientAsync(other);

        CreateRoleRequest inTheSameTenant = NewRoleRequest();
        inTheSameTenant.RoleName = held.RoleName;

        using HttpResponseMessage collision = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            inTheSameTenant,
            ApiTestFixture.Json);

        collision.StatusCode.Should().Be(HttpStatusCode.Conflict);

        CreateRoleRequest inTheOtherTenant = NewRoleRequest();
        inTheOtherTenant.RoleName = held.RoleName;

        using HttpResponseMessage response = await owner.PostAsJsonAsync(
            RolesRoute(other.PortalId),
            inTheOtherTenant,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto accepted = await ReadDetailAsync(response);
        accepted.RoleName.Should().Be(held.RoleName);
        accepted.RoleId.Should().NotBe(held.RoleId);

        // Which tenant now owns the accepted role is proved by the request HOST rather than by a
        // self-reported identifier in the payload: it is readable through the other tenant's host and
        // unreachable through the seeded host.
        using HttpResponseMessage throughOwner = await owner.GetAsync(
            RoleRoute(other.PortalId, accepted.RoleId));
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        request.RoleName = string.Empty;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        // The wording is valRoleName's own, from Website/admin/Security/editroles.ascx L31, with only the
        // leading markup tag removed. Do not reword it: the string is the parity assertion.
        await ShouldReportFieldAsync(
            response,
            nameof(CreateRoleRequest.RoleName),
            RoleNameRequiredMessage);
    }

    /// <summary>A negative service fee is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithNegativeServiceFee_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        request.ServiceFee = -1m;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        await ShouldReportFieldAsync(
            response,
            nameof(CreateRoleRequest.ServiceFee),
            ServiceFeeNegativeMessage);
    }

    /// <summary>
    /// A billing period of zero is rejected when a recurring cycle is declared beside it, because a cycle
    /// must have a positive number of units.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithZeroBillingPeriod_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        request.BillingPeriod = 0;
        request.BillingFrequency = BillingFrequency.Month;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        await ShouldReportFieldAsync(
            response,
            nameof(CreateRoleRequest.BillingPeriod),
            BillingPeriodNotPositiveMessage);
    }

    /// <summary>
    /// The created representation reports the fee the installation actually stored, not the fee that was
    /// submitted, when the two differ by the stored column's scale.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithAFeeFinerThanTheStoredScale_ReportsTheStoredValue()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        request.ServiceFee = 1.23456789m;
        request.BillingPeriod = 2;
        request.BillingFrequency = BillingFrequency.Month;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await Diagnose(response));

        RoleDetailDto created = await ReadDetailAsync(response);
        created.ServiceFee.Should().Be(
            1.2346m,
            "the created resource must report what the money column holds, not what was submitted");

        using HttpResponseMessage reread = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);
        RoleDetailDto stored = await ReadDetailAsync(reread);
        stored.ServiceFee.Should().Be(
            created.ServiceFee,
            "a later read must not disagree with the response that reported the creation");
    }

    /// <summary>
    /// A submitted role name is stored without its surrounding whitespace, so the stored value agrees with
    /// the value that governs uniqueness.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MEASURED. A name submitted with two leading and two trailing spaces was stored verbatim at 17
    /// characters, and the 13-character name it appears to be was then refused <c>409</c> - because the
    /// uniqueness index is evaluated under a collation that gives trailing whitespace no sort weight.
    /// </remarks>
    [Fact]
    public async Task CreateRole_WithSurroundingWhitespaceInTheName_StoresItTrimmed()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string bare = "ITest Trim " + Suffix();
        CreateRoleRequest request = NewRoleRequest();
        request.RoleName = "  " + bare + "  ";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await Diagnose(response));

        RoleDetailDto created = await ReadDetailAsync(response);
        created.RoleName.Should().Be(bare, "interior spacing is preserved; only the surrounds are removed");

        using HttpResponseMessage reread = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId));

        (await ReadDetailAsync(reread)).RoleName.Should().Be(bare);

        // The name the listing shows can now be re-submitted and is recognised as the duplicate it is,
        // which is exactly what the padded row made impossible.
        CreateRoleRequest again = NewRoleRequest();
        again.RoleName = bare;

        using HttpResponseMessage duplicate = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            again,
            ApiTestFixture.Json);

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// A role submitted with both periods at zero and no recurring cycle on either is created, so a role
    /// the portal template produced can be read and written back unchanged.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithZeroPeriodsAndNoCycle_IsCreated()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        request.BillingPeriod = 0;
        request.BillingFrequency = BillingFrequency.None;
        request.TrialPeriod = 0;
        request.TrialFrequency = BillingFrequency.None;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await Diagnose(response));

        RoleDetailDto created = await ReadDetailAsync(response);
        created.BillingPeriod.Should().Be(0);
        created.TrialPeriod.Should().Be(0);

        using HttpResponseMessage echoed = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId),
            new UpdateRoleRequest
            {
                RoleName = created.RoleName,
                Description = created.Description,
                IsPublic = created.IsPublic,
                AutoAssignment = created.AutoAssignment,
                ServiceFee = created.ServiceFee,
                BillingPeriod = created.BillingPeriod,
                BillingFrequency = created.BillingFrequency,
                TrialFee = created.TrialFee,
                TrialPeriod = created.TrialPeriod,
                TrialFrequency = created.TrialFrequency,
                RsvpCode = created.RsvpCode,
                IconFile = created.IconFile,
                RoleGroupId = created.RoleGroupId,
                ConcurrencyToken = created.ConcurrencyToken,
            },
            ApiTestFixture.Json);

        echoed.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a value this API emits must be a value this API accepts");
    }

    /// <summary>An update answers <c>200 OK</c> and the new state survives a read.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_ReturnsOkAndPersists()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);

        // The role's own name is resubmitted, which is what a caller amending other fields sends on a
        // replacement contract. The uniqueness guard excludes the role being updated, so an unchanged name
        // is a no-op rather than a self-collision.
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
    /// The billing frequency is spelled on the wire with its legacy single-character code, not with the
    /// name of the enumeration member.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRole_SpellsTheBillingFrequencyWithItsLegacyCode()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

    /// <summary>
    /// A row whose stored frequency character is outside the legacy vocabulary is still readable, and the
    /// whole listing that contains it is still readable.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The character planted here is one the product itself ships: the frequency vocabulary table is seeded
    /// with <c>'4'</c> at <c>01.00.00.SqlDataProvider</c> L7192, and the foreign key that once policed the
    /// column is dropped for good at <c>03.00.01.SqlDataProvider</c> L1297 with nothing in its place. The
    /// listing is read as well as the item, because the listing is where the blast radius was.
    /// </remarks>
    [Fact]
    public async Task GetRole_WithAnUnrecognisedStoredFrequency_IsStillReadable()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);

        // Planted with a direct statement because the API cannot express it - the request contract accepts
        // only the six declared codes - so only a legacy row can put the read under this pressure.
        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Roles] SET [BillingFrequency] = '4', [TrialFrequency] = '0' "
            + "WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        using HttpResponseMessage item = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId));

        item.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a character the vocabulary does not declare is a row a legacy installation already holds, so "
            + "reading it must degrade the field rather than fail the response");

        RoleDetailDto read = await ReadDetailAsync(item);
        read.BillingFrequency.Should().Be(
            (BillingFrequency)'4',
            "the stored character is carried rather than normalised, so the response reports the truth");
        read.TrialFrequency.Should().Be((BillingFrequency)'0');

        string body = await item.Content.ReadAsStringAsync();
        body.Should().Contain(
            "\"billingFrequency\":\"4\"",
            "the character the installation stored reaches the wire unchanged");
        body.Should().Contain(
            "\"trialFrequency\":\"0\"",
            "and so does the trial character, independently of the billing one");
        body.Should().NotContain(
            "\"billingFrequency\":\"N\"",
            "substituting a declared code would tell the client something the database does not say");

        (await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] "
            + "WHERE [RoleID] = @roleId AND [BillingFrequency] = '4' AND [TrialFrequency] = '0'",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId })).Should().Be(
            1,
            "reading the role over HTTP must not have rewritten either stored character");

        using HttpResponseMessage listing = await client.GetAsync(RolesRoute(_fixture.Seed.PortalId));

        listing.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "one unreadable row must not be able to fail a page that merely contains it");
    }

    /// <summary>An update against an unknown role answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, UnknownRoleId),
            new UpdateRoleRequest { RoleName = "Absent" + Suffix(), Description = "Absent" + Suffix() },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The update route renames a role, and the new name reaches the stored column.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION - documented behavioural difference, asserted end to end. The legacy edit screen made the
    /// name read-only and the terminal <c>UpdateRole</c> procedure omits the column from its assignment
    /// list, so the legacy application could not rename a role.
    /// </remarks>
    [Fact]
    public async Task UpdateRole_RenamesTheRoleAndPersistsTheNewName()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);

        string renamed = "Renamed" + Suffix();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId),
            new UpdateRoleRequest
            {
                RoleName = renamed,
                Description = "The name was submitted and must be applied.",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleDetailDto updated = await ReadDetailAsync(response);
        updated.RoleName.Should().Be(renamed);
        updated.Description.Should().Be("The name was submitted and must be applied.");

        string storedName = await _fixture.Database.ScalarAsync<string>(
            "SELECT [RoleName] FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        storedName.Should().Be(renamed, "the rename must reach the stored column");
    }

    /// <summary>
    /// Renaming a role onto a name another role in the same tenant already holds answers <c>409
    /// Conflict</c> and leaves the stored name alone.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The stored column is re-read so the refusal is proved to have prevented the write rather than merely
    /// to have followed it.
    /// </remarks>
    [Fact]
    public async Task UpdateRole_RenamingOntoAnExistingName_ReturnsConflict()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId),
            new UpdateRoleRequest
            {
                RoleName = IntegrationSeed.SubscribersRoleName,
                Description = "A colliding name must be refused.",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        string storedName = await _fixture.Database.ScalarAsync<string>(
            "SELECT [RoleName] FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        storedName.Should().Be(created.RoleName, "a refused rename must not have been written");
    }

    /// <summary>
    /// Resubmitting a role's OWN name is not a conflict, because the uniqueness comparison excludes the
    /// role being updated.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_ResubmittingItsOwnName_IsNotAConflict()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId),
            new UpdateRoleRequest
            {
                RoleName = created.RoleName,
                Description = "Only the description changed.",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleDetailDto updated = await ReadDetailAsync(response);
        updated.RoleName.Should().Be(created.RoleName);
        updated.Description.Should().Be("Only the description changed.");
    }

    /// <summary>
    /// Six SIMULTANEOUS grants of one role to one account leave exactly one assignment row, as the sequential
    /// path does.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THE ROW COUNT IS THE ASSERTION, NOT THE STATUS. Every grant answers <c>204</c> either way - the
    /// defect this closes produced six <c>204</c>s and six rows - so a test that only read statuses would have
    /// passed against the unserialised implementation. Nothing in the schema can refuse the second row:
    /// <c>dbo.UserRoles</c> is keyed on its own identity column with non-unique indexes over <c>UserID</c> and
    /// <c>RoleID</c>, and the schema is immutable under this migration, so the invariant is enforced by a
    /// locking read inside the grant's own transaction instead.
    /// </para>
    /// <para>
    /// The sequential arm runs first and is the control: it establishes that one row is the intended outcome
    /// of repeating the grant, so the concurrent arm is measured against the implementation's own intent
    /// rather than against a number chosen by this test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AssignUser_ConcurrentIdenticalGrants_LeaveExactlyOneAssignment()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);
        Uri route = RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId);

        // THE CONTROL: repeating the grant sequentially is idempotent.
        for (int repeat = 0; repeat < 4; repeat++)
        {
            using HttpResponseMessage sequential = await client.PostAsJsonAsync(
                route,
                new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
                ApiTestFixture.Json);

            sequential.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await CountAssignmentsAsync(created.RoleId)).Should().Be(
            1,
            "the sequential path proves one row is the intended outcome of repeating the grant");

        // THE MEASUREMENT: the same grant, six times over, all in flight together.
        Task<HttpResponseMessage>[] inFlight = Enumerable.Range(0, 6)
            .Select(_ => client.PostAsJsonAsync(
                route,
                new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
                ApiTestFixture.Json))
            .ToArray();

        HttpResponseMessage[] answers = await Task.WhenAll(inFlight);

        try
        {
            answers.Should().OnlyContain(
                answer => answer.StatusCode == HttpStatusCode.NoContent,
                "a grant the account already holds is an amendment, whichever request gets there first");
        }
        finally
        {
            foreach (HttpResponseMessage answer in answers)
            {
                answer.Dispose();
            }
        }

        (await CountAssignmentsAsync(created.RoleId)).Should().Be(
            1,
            "concurrency must not change the outcome of an idempotent grant - six rows for one membership is "
            + "unbounded growth in a table the authorisation path reads on every request");

        // The single membership is still withdrawable through the ordinary path, which is what proves the
        // reconciliation left a row the removal can still address.
        using HttpResponseMessage removed = await client.DeleteAsync(
            new Uri(
                FormattableString.Invariant($"{route.OriginalString}/{_fixture.Seed.MemberUserId}"),
                UriKind.Relative));

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CountAssignmentsAsync(created.RoleId)).Should().Be(0);
    }

    /// <summary>
    /// A delete answers <c>204 No Content</c>, removes the role, and takes its assignments with it so no
    /// account is left holding a role that no longer exists.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteRole_ReturnsNoContentAndRemovesItsAssignments()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

    /// <summary>
    /// removing a role removes every module and page grant addressed to it, and leaves every grant
    /// addressed to any other principal exactly where it was.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE CONTROL PRINCIPALS ARE THE OTHER HALF OF THE ASSERTION, and each is a different way the sweep
    /// could be written too widely. A second role proves the predicate names one role rather than clearing
    /// the table.
    /// </remarks>
    [Fact]
    public async Task DeleteRole_SweepsTheGrantsAddressedToItAndSparesEveryOtherPrincipal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto doomed = await CreateRoleAsync(client);
        RoleDetailDto retained = await CreateRoleAsync(client);

        int tabId = _fixture.Seed.RootTabId;
        int modulePermissionId = _fixture.Seed.ModuleViewPermissionId;
        int tabPermissionId = _fixture.Seed.TabViewPermissionId;
        int accountId = _fixture.Seed.MemberUserId;

        int moduleId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Modules]
                ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted], [InheritViewPermissions])
            VALUES (@moduleDefinitionId, @portalId, N'Role sweep module', 0, 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["moduleDefinitionId"] = _fixture.Seed.ModuleDefinitionId,
                ["portalId"] = _fixture.Seed.PortalId,
            });

        List<int> seededTabGrants = [];

        try
        {
            // The third grant family has no table in the schema this suite provisions, because file
            // management is outside the migration's scope - so it is created here for the duration of this
            // one test, in the shape the legacy chain arrives at (02.02.00.SqlDataProvider:L659 creates it,
            // 04.05.00.SqlDataProvider:L750-L790 makes the role column nullable and adds the account
            // column).
            _ = await _fixture.Database.ExecuteAsync(
                """
                IF OBJECT_ID(N'[dbo].[FolderPermission]', N'U') IS NULL
                    CREATE TABLE [dbo].[FolderPermission] (
                        [FolderPermissionID] int NOT NULL IDENTITY,
                        [FolderID] int NOT NULL,
                        [PermissionID] int NOT NULL,
                        [RoleID] int NULL,
                        [UserID] int NULL,
                        [AllowAccess] bit NOT NULL,
                        CONSTRAINT [PK_FolderPermission] PRIMARY KEY ([FolderPermissionID])
                    );
                """);

            _ = await _fixture.Database.ExecuteAsync(
                """
                INSERT INTO [dbo].[FolderPermission]
                    ([FolderID], [PermissionID], [RoleID], [UserID], [AllowAccess])
                VALUES (1, @permissionId, @doomedRoleId, NULL, 1),
                       (1, @permissionId, @retainedRoleId, NULL, 1),
                       (1, @permissionId, @allUsersRoleId, NULL, 1);
                """,
                new Dictionary<string, object?>
                {
                    ["permissionId"] = tabPermissionId,
                    ["doomedRoleId"] = doomed.RoleId,
                    ["retainedRoleId"] = retained.RoleId,
                    ["allUsersRoleId"] = AllUsersPseudoRoleId,
                });

            await _fixture.Database.ExecuteAsync(
                """
                INSERT INTO [dbo].[ModulePermission]
                    ([ModuleID], [PermissionID], [RoleID], [UserID], [AllowAccess])
                VALUES (@moduleId, @permissionId, @doomedRoleId, NULL, 1),
                       (@moduleId, @permissionId, @retainedRoleId, NULL, 1),
                       (@moduleId, @permissionId, NULL, @accountId, 1),
                       (@moduleId, @permissionId, @allUsersRoleId, NULL, 1);
                """,
                new Dictionary<string, object?>
                {
                    ["moduleId"] = moduleId,
                    ["permissionId"] = modulePermissionId,
                    ["doomedRoleId"] = doomed.RoleId,
                    ["retainedRoleId"] = retained.RoleId,
                    ["accountId"] = accountId,
                    ["allUsersRoleId"] = AllUsersPseudoRoleId,
                });

            // Each page grant is inserted on its own so its key can be captured, which is what lets the
            // survivors be asserted and cleaned up by identity rather than by a predicate that could match
            // a row this test did not create.
            foreach ((int? roleId, int? userId) in new (int?, int?)[]
            {
                (doomed.RoleId, null),
                (retained.RoleId, null),
                (null, accountId),
                (AllUsersPseudoRoleId, null),
            })
            {
                seededTabGrants.Add(await _fixture.Database.ScalarAsync<int>(
                    """
                    INSERT INTO [dbo].[TabPermission]
                        ([TabID], [PermissionID], [RoleID], [UserID], [AllowAccess])
                    VALUES (@tabId, @permissionId, @roleId, @userId, 1);
                    SELECT CAST(SCOPE_IDENTITY() AS int);
                    """,
                    new Dictionary<string, object?>
                    {
                        ["tabId"] = tabId,
                        ["permissionId"] = tabPermissionId,
                        ["roleId"] = roleId,
                        ["userId"] = userId,
                    }));
            }

            using HttpResponseMessage response = await client.DeleteAsync(
                RoleRoute(_fixture.Seed.PortalId, doomed.RoleId));

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);

            (await CountModuleGrantsForRoleAsync(moduleId, doomed.RoleId)).Should().Be(
                0,
                "the module grants addressed to the removed role go with it");
            (await CountTabGrantsForRoleAsync(tabId, doomed.RoleId)).Should().Be(
                0,
                "the page grants addressed to the removed role go with it");

            (await CountModuleGrantsForRoleAsync(moduleId, retained.RoleId)).Should().Be(
                1,
                "a second role's grant proves the sweep names one role rather than clearing the table");
            (await CountTabGrantsForRoleAsync(tabId, retained.RoleId)).Should().Be(1);

            (await CountModuleGrantsForAccountAsync(moduleId, accountId)).Should().Be(
                1,
                "a grant addressed to an account belongs to the account, not to any role");

            (await CountModuleGrantsForRoleAsync(moduleId, AllUsersPseudoRoleId)).Should().Be(
                1,
                "the all-users pseudo-principal names no Roles row, so no role removal may discard it");
            (await CountTabGrantsForRoleAsync(tabId, AllUsersPseudoRoleId)).Should().Be(
                1,
                "discarding it would silently un-publish a public page");

            (await CountTabGrantsByKeyAsync(seededTabGrants)).Should().Be(
                3,
                "exactly one of the four page grants this test seeded was addressed to the removed role");

            (await CountFolderGrantsForRoleAsync(doomed.RoleId)).Should().Be(
                0,
                "the storage grants go too - they are the first statement of the legacy procedure, not an "
                + "optional third");
            (await CountFolderGrantsForRoleAsync(retained.RoleId)).Should().Be(1);
            (await CountFolderGrantsForRoleAsync(AllUsersPseudoRoleId)).Should().Be(1);

            (await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
                new Dictionary<string, object?> { ["roleId"] = doomed.RoleId }))
                .Should().Be(0);
        }
        finally
        {
            _ = await _fixture.Database.ExecuteAsync("DROP TABLE IF EXISTS [dbo].[FolderPermission];");

            // The module goes first: FK_ModulePermission_Modules cascades, so removing it takes every module
            // grant this test seeded, including the ones deliberately left behind.
            _ = await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId;",
                new Dictionary<string, object?> { ["moduleId"] = moduleId });

            if (seededTabGrants.Count == 4)
            {
                _ = await _fixture.Database.ExecuteAsync(
                    """
                    DELETE FROM [dbo].[TabPermission]
                    WHERE [TabPermissionID] IN (@first, @second, @third, @fourth);
                    """,
                    new Dictionary<string, object?>
                    {
                        ["first"] = seededTabGrants[0],
                        ["second"] = seededTabGrants[1],
                        ["third"] = seededTabGrants[2],
                        ["fourth"] = seededTabGrants[3],
                    });
            }

            using HttpResponseMessage discarded = await client.DeleteAsync(
                RoleRoute(_fixture.Seed.PortalId, retained.RoleId));
            _ = discarded.StatusCode;
        }
    }

    /// <summary>
    /// neither role a tenant designates for a system purpose can be removed or amended, the rows and their
    /// assignments survive untouched, and the tenant remains fully operable afterwards.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The legacy edit screen withheld both verbs outright for a designated role - <c>cmdDelete</c> and
    /// <c>cmdUpdate</c> hidden together with the whole form deactivated, at
    /// <c>Website/admin/Security/EditRoles.ascx.vb</c> L174-L178 - and this contract accepted the removal
    /// and answered <c>204</c>.
    /// </remarks>
    [Fact]
    public async Task DeleteRole_RefusesTheTenantsDesignatedRolesAndLeavesTheTenantOperable()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedTenant tenant = await CreateIsolatedPortalAsync(host);

        int administratorRoleId = await _fixture.Database.ScalarAsync<int>(
            "SELECT [AdministratorRoleId] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = tenant.PortalId });

        int registeredRoleId = await _fixture.Database.ScalarAsync<int>(
            "SELECT [RegisteredRoleId] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = tenant.PortalId });

        administratorRoleId.Should().NotBe(
            registeredRoleId,
            "the tenant designates two distinct roles, so both arms of the guard are genuinely exercised");

        using HttpClient administrator = await TenantClientAsync(tenant);

        foreach (int designated in new[] { administratorRoleId, registeredRoleId })
        {
            int assignmentsBefore = await CountAssignmentsAsync(designated);

            using HttpResponseMessage removal = await administrator.DeleteAsync(
                RoleRoute(tenant.PortalId, designated));

            await ShouldCarryFailureCodeAsync(removal, HttpStatusCode.Forbidden, "role.protected");

            using HttpResponseMessage amendment = await administrator.PutAsJsonAsync(
                RoleRoute(tenant.PortalId, designated),
                new UpdateRoleRequest
                {
                    RoleName = "Renamed" + Suffix(),
                    Description = "Renamed by a request that must not be honoured",
                },
                ApiTestFixture.Json);

            await ShouldCarryFailureCodeAsync(amendment, HttpStatusCode.Forbidden, "role.protected");

            int rows = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
                new Dictionary<string, object?> { ["roleId"] = designated });

            rows.Should().Be(1, "the designated role is still there");
            (await CountAssignmentsAsync(designated)).Should().Be(
                assignmentsBefore,
                "and nothing it grants was cascaded away");
        }

        int designation = await _fixture.Database.ScalarAsync<int>(
            "SELECT [AdministratorRoleId] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = tenant.PortalId });

        designation.Should().Be(administratorRoleId, "the tenant's designation still names a surviving role");

        // The tenant is still operable, which is the property the removal destroyed.
        using HttpClient afterwards = await _fixture.CreateTenantClientAsync(
            tenant.Alias,
            tenant.PortalId,
            tenant.AdministratorUserName);

        using HttpResponseMessage listed = await afterwards.GetAsync(
            new Uri(RolesRoute(tenant.PortalId).OriginalString + "?pageIndex=0&pageSize=50", UriKind.Relative));

        listed.StatusCode.Should().Be(HttpStatusCode.OK, "the tenant's own administrator still administers it");

        using HttpResponseMessage createdOrdinary = await afterwards.PostAsJsonAsync(
            RolesRoute(tenant.PortalId),
            NewRoleRequest(),
            ApiTestFixture.Json);

        createdOrdinary.StatusCode.Should().Be(HttpStatusCode.Created);
        RoleDetailDto ordinary = await ReadDetailAsync(createdOrdinary);

        using HttpResponseMessage removedOrdinary = await afterwards.DeleteAsync(
            RoleRoute(tenant.PortalId, ordinary.RoleId));

        removedOrdinary.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "a role the tenant designates for nothing is removed exactly as before");
    }

    /// <summary>
    /// assigning a tenant's designated administrator to that tenant's administrators role stores NO bounds
    /// however the request is filled in, and the administrator still administers the tenant afterwards.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The assertion that matters is therefore the LAST one: the same administrator, signing in again,
    /// still administers the tenant. Asserting only the two stored nulls would pass for an implementation
    /// that stored the right values by a route that happened to work for roles carrying no terms.
    /// </remarks>
    [Fact]
    public async Task Assignment_ForTheTenantsOwnAdministrator_StoresNoBoundsAndPreservesItsAuthority()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedTenant tenant = await CreateIsolatedPortalAsync(host);

        int administratorRoleId = await _fixture.Database.ScalarAsync<int>(
            "SELECT [AdministratorRoleId] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = tenant.PortalId });

        using HttpClient administrator = await TenantClientAsync(tenant);

        using HttpResponseMessage assigned = await administrator.PostAsJsonAsync(
            RoleUsersRoute(tenant.PortalId, administratorRoleId),
            new RoleAssignmentRequest
            {
                UserId = tenant.AdministratorId,
                EffectiveDate = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiryDate = new DateTime(2031, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "the bounds are discarded rather than the request refused, exactly as the screen discarded them");

        int bounded = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[UserRoles]
            WHERE [RoleID] = @roleId
              AND [UserID] = @userId
              AND ([EffectiveDate] IS NOT NULL OR [ExpiryDate] IS NOT NULL);
            """,
            new Dictionary<string, object?>
            {
                ["roleId"] = administratorRoleId,
                ["userId"] = tenant.AdministratorId,
            });

        bounded.Should().Be(
            0,
            "neither a submitted bound nor a derived one may bound the tenant's own administrative membership");

        using HttpClient afterwards = await _fixture.CreateTenantClientAsync(
            tenant.Alias,
            tenant.PortalId,
            tenant.AdministratorUserName);

        using HttpResponseMessage listed = await afterwards.GetAsync(
            new Uri(RolesRoute(tenant.PortalId).OriginalString + "?pageIndex=0&pageSize=50", UriKind.Relative));

        listed.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the tenant's administrator must still administer the tenant after being re-assigned to its role");
    }

    /// <summary>A delete against an unknown role answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteRole_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.DeleteAsync(
            RoleRoute(_fixture.Seed.PortalId, UnknownRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An assignment round-trips: it answers <c>204 No Content</c>, appears on both the role's account list
    /// and the account's role list, and is removed again with <c>204 No Content</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_RoundTripsThroughBothProjections()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

        PagedEnvelope<RoleMembershipDto>? members = await roleMembers.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleMembershipDto>>(ApiTestFixture.Json);

        members.Should().NotBeNull();
        members!.Items.Select(item => item.UserId).Should().Contain(_fixture.Seed.MemberUserId);

        using HttpResponseMessage accountRoles = await client.GetAsync(
            UserRolesRoute(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId));

        accountRoles.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<RoleListItemDto>? heldEnvelope = await accountRoles.Content
            .ReadFromJsonAsync<CollectionEnvelope<RoleListItemDto>>(ApiTestFixture.Json);

        heldEnvelope.Should().NotBeNull();
        IReadOnlyList<RoleListItemDto>? held = heldEnvelope!.Data;

        held.Should().NotBeNull();
        held!.Select(item => item.RoleId).Should().Contain(created.RoleId);

        using HttpResponseMessage removed = await client.DeleteAsync(
            RoleUserRoute(_fixture.Seed.PortalId, created.RoleId, _fixture.Seed.MemberUserId));

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CountAssignmentsAsync(created.RoleId)).Should().Be(0);
    }

    /// <summary>
    /// Assigning an account that already holds the role amends the existing assignment rather than adding a
    /// second one, so the operation is idempotent in the only sense that matters - the account holds the
    /// role exactly once.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_WhenRepeated_AmendsRatherThanDuplicates()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    /// a free role derives no expiry date of its own, and an expiry the caller submits is stored EXACTLY AS
    /// SUBMITTED rather than discarded.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_ForAFreeRole_StoresASubmittedExpiryDateVerbatim()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);

        // Whole seconds, because the column is `datetime` and its resolution is 3.33 milliseconds: a
        // round-trip comparison against an arbitrary tick-precision instant would fail on the storage
        // rounding rather than on anything this fact is about.
        DateTime submitted = new DateTime(2031, 3, 17, 9, 45, 0, DateTimeKind.Utc);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, created.RoleId),
            new RoleAssignmentRequest
            {
                UserId = _fixture.Seed.MemberUserId,
                ExpiryDate = submitted,
            },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        DateTime stored = await _fixture.Database.ScalarAsync<DateTime>(
            """
            SELECT [ExpiryDate]
            FROM [dbo].[UserRoles]
            WHERE [RoleID] = @roleId AND [UserID] = @userId;
            """,
            new Dictionary<string, object?>
            {
                ["roleId"] = created.RoleId,
                ["userId"] = _fixture.Seed.MemberUserId,
            });

        stored.Should().Be(
            submitted,
            "the caller stated when the membership ends, and a 204 says that instruction was accepted");

        // The other half: with no bound submitted, a role carrying no term still derives none.
        RoleDetailDto second = await CreateRoleAsync(client);

        using HttpResponseMessage withoutBound = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, second.RoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        withoutBound.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int derived = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[UserRoles]
            WHERE [RoleID] = @roleId AND [UserID] = @userId AND [ExpiryDate] IS NOT NULL;
            """,
            new Dictionary<string, object?>
            {
                ["roleId"] = second.RoleId,
                ["userId"] = _fixture.Seed.MemberUserId,
            });

        derived.Should().Be(0, "a role with no billing or trial period derives nothing to expire");
    }

    /// <summary>
    /// A role carrying a billing period derives an expiry date from that period, so a paid membership ends
    /// without anybody having to remember to end it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_ForAPaidRole_DerivesAnExpiryDateFromTheBillingPeriod()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.DeleteAsync(RoleUserRoute(
            _fixture.Seed.PortalId,
            _fixture.Seed.AdministratorRoleId,
            _fixture.Seed.AdminUserId));

        await ShouldCarryFailureCodeAsync(
            response,
            HttpStatusCode.Forbidden,
            "role_assignment.protected");

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
    /// No account may be removed from the registered-users role, which is the membership that makes an
    /// account a member of the tenant at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RemoveAssignment_FromTheRegisteredUsersRole_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.DeleteAsync(RoleUserRoute(
            _fixture.Seed.PortalId,
            _fixture.Seed.RegisteredRoleId,
            _fixture.Seed.MemberUserId));

        await ShouldCarryFailureCodeAsync(
            response,
            HttpStatusCode.Forbidden,
            "role_assignment.protected");
    }

    /// <summary>The account-roles projection answers <c>404 Not Found</c> for an unknown account.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListUserRoles_WhenAccountIsUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            UserRolesRoute(_fixture.Seed.PortalId, UnknownUserId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The role-accounts projection answers <c>404 Not Found</c> for an unknown role.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoleUsers_WhenRoleIsUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, UnknownRoleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Two paged collections on the same controller carry two different sortable vocabularies, and the one
    /// applied follows the action rather than the controller.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RoleCollections_BindTheirSortVocabularyPerActionNotPerController()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // The membership listing orders by an account field, which the role listing has no column for.
        using HttpResponseMessage membershipOwnField = await client.GetAsync(
            new Uri(
                RoleUsersRoute(_fixture.Seed.PortalId, _fixture.Seed.AdministratorRoleId) + "?sortBy=Username",
                UriKind.Relative));

        membershipOwnField.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the membership listing renders the account, and ordering by what it renders is what the legacy "
            + "grid allowed");

        using HttpResponseMessage membershipForeignField = await client.GetAsync(
            new Uri(
                RoleUsersRoute(_fixture.Seed.PortalId, _fixture.Seed.AdministratorRoleId) + "?sortBy=Description",
                UriKind.Relative));

        membershipForeignField.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "the role description is not carried by the membership projection, so ordering by it could "
            + "not be verified by the caller reading the page back");

        // The role listing is the mirror image: its own column is accepted, the membership's is not.
        using HttpResponseMessage roleOwnField = await client.GetAsync(
            new Uri(RolesRoute(_fixture.Seed.PortalId) + "?sortBy=Description", UriKind.Relative));

        roleOwnField.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage roleForeignField = await client.GetAsync(
            new Uri(RolesRoute(_fixture.Seed.PortalId) + "?sortBy=Username", UriKind.Relative));

        roleForeignField.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a role carries no account name; only a membership does, so the two collections must not "
            + "share one vocabulary");
    }

    /// <summary>
    /// The membership listing accepts the three account fields the ACCOUNT listing cannot order by, which
    /// is the ordering capability the boundary previously refused.
    /// </summary>
    /// <param name="sortBy">The field the caller named.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: this is the end-to-end proof that the membership listing no longer borrows the account
    /// collection's request contract. It bound <c>UserPagedRequest</c>, so <c>UserPagedRequestValidator</c>
    /// resolved for it and applied the account collection's seven sortable names - and every one of these
    /// three requests answered <c>400</c> naming <c>sortBy</c> as an unknown field.
    /// </remarks>
    [Theory]
    [InlineData("CreatedDate")]
    [InlineData("LastLoginDate")]
    [InlineData("IsApproved")]
    [Trait("Category", "Integration")]
    public async Task RoleMembers_RefuseTheMembershipStoreFieldsNeitherListingCanOrder(string sortBy)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage membership = await client.GetAsync(
            new Uri(
                RoleUsersRoute(_fixture.Seed.PortalId, _fixture.Seed.AdministratorRoleId)
                    + "?pageIndex=0&pageSize=50&sortBy=" + sortBy,
                UriKind.Relative));

        membership.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "the membership listing pages in the store, so it cannot order by a value the external "
            + "membership objects hold and its projection does not publish - and accepting the name would "
            + "return a page ordered by something else");

        // The SAME verdict from the account listing, asserted alongside so the two vocabularies cannot
        // drift apart again in either direction.
        using HttpResponseMessage accounts = await client.GetAsync(
            new Uri(
                $"/api/v1/users?pageIndex=0&pageSize=50&sortBy={sortBy}",
                UriKind.Relative));

        accounts.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "the account listing pages in the store, so it cannot order by a value the membership store "
            + "fills after the page has been taken");
    }

    /// <summary>The role-group resource supports the full round trip, ending in <c>204 No Content</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleGroups_SupportCreateReadUpdateAndDelete()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        RoleGroupDto created = await CreateRoleGroupAsync(client);
        created.PortalId.Should().Be(_fixture.Seed.PortalId);

        // RoleGroups.RoleGroupID is IDENTITY(0, 1), so zero is a legitimate identifier here too.
        created.RoleGroupId.Should().BeGreaterThanOrEqualTo(0);

        Uri itemRoute = RoleGroupRoute(_fixture.Seed.PortalId, created.RoleGroupId);

        using HttpResponseMessage read = await client.GetAsync(itemRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleGroupDto? fetched = await read.Content.ReadEnvelopeAsync<RoleGroupDto>();
        fetched.Should().NotBeNull();
        fetched!.RoleGroupName.Should().Be(created.RoleGroupName);

        using HttpResponseMessage listed = await client.GetAsync(RoleGroupsRoute(_fixture.Seed.PortalId));
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<RoleGroupDto>? allEnvelope = await listed.Content
            .ReadFromJsonAsync<CollectionEnvelope<RoleGroupDto>>(ApiTestFixture.Json);

        allEnvelope.Should().NotBeNull();
        IReadOnlyList<RoleGroupDto>? all = allEnvelope!.Data;

        all.Should().NotBeNull();
        all!.Select(item => item.RoleGroupId).Should().Contain(created.RoleGroupId);

        UpdateRoleGroupRequest amendment = new()
        {
            RoleGroupName = created.RoleGroupName,
            Description = "Amended by the integration suite.",
        };

        using HttpResponseMessage updated = await client.PutAsJsonAsync(itemRoute, amendment, ApiTestFixture.Json);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        RoleGroupDto? afterUpdate = await updated.Content
            .ReadEnvelopeAsync<RoleGroupDto>();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleGroupDto first = await CreateRoleGroupAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleGroupsRoute(_fixture.Seed.PortalId),
            new CreateRoleGroupRequest { RoleGroupName = first.RoleGroupName },
            ApiTestFixture.Json);

        // A group name collision carries its own code, distinct from a role name collision, so the two
        // are distinguishable to a client even though both answer 409.
        await ShouldCarryFailureCodeAsync(
            response,
            HttpStatusCode.Conflict,
            "role_group.name_duplicate");
    }

    /// <summary>
    /// Submitting the same new group name from several callers at once creates it exactly once, refuses
    /// every other caller as a conflict, and answers no caller with a server fault.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The group counterpart of the role contest. <c>IX_RoleGroupName</c> is unique over <c>(PortalID,
    /// RoleGroupName)</c>, and this path's check is weaker than the role path's - it reads the whole group
    /// collection and compares in memory - so the window in front of the insert is if anything wider.
    /// </remarks>
    [Fact]
    public async Task CreateRoleGroup_SubmittedConcurrentlyUnderOneName_CreatesItOnceWithoutAnyServerFault()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string contestedName = "ITest Group Race " + Suffix();
        const int Callers = 12;

        IEnumerable<Task<HttpResponseMessage>> submissions = Enumerable.Range(0, Callers).Select(_ =>
            client.PostAsJsonAsync(
                RoleGroupsRoute(_fixture.Seed.PortalId),
                new CreateRoleGroupRequest { RoleGroupName = contestedName },
                ApiTestFixture.Json));

        HttpResponseMessage[] responses = await Task.WhenAll(submissions);

        try
        {
            HttpStatusCode[] statuses = [.. responses.Select(response => response.StatusCode)];

            statuses.Should().NotContain(
                status => (int)status >= 500,
                "a unique index refusing a duplicate is not a server fault");

            statuses.Count(status => status == HttpStatusCode.Created).Should().Be(1);
            statuses.Where(status => status != HttpStatusCode.Created).Should()
                .AllBeEquivalentTo(HttpStatusCode.Conflict);

            foreach (HttpResponseMessage refused in responses
                .Where(response => response.StatusCode != HttpStatusCode.Created))
            {
                await ShouldCarryFailureCodeAsync(
                    refused,
                    HttpStatusCode.Conflict,
                    "role_group.name_duplicate");
            }

            int stored = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[RoleGroups] "
                + "WHERE [RoleGroupName] = @groupName AND [PortalID] = @portalId;",
                new Dictionary<string, object?>
                {
                    ["groupName"] = contestedName,
                    ["portalId"] = _fixture.Seed.PortalId,
                });

            stored.Should().Be(1, "exactly one row may survive the contest");
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// A group that still classifies a role is refused deletion, so no role is left pointing at a group
    /// that no longer exists.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The refusal is a <c>409 Conflict</c>, not a <c>400 Bad Request</c>. The request is well formed - it
    /// addresses a group that exists in a portal the caller administers - and it is the STATE of that group
    /// that declines it, which is exactly what a conflict reports.
    /// </remarks>
    [Fact]
    public async Task DeleteRoleGroup_WhileItStillClassifiesARole_ReturnsConflict()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

        // The third of the three 409 reasons on these resources, and the one a client is most likely to
        // want to act on differently - it can be resolved by reclassifying the roles, whereas a name
        // collision can only be resolved by choosing another name.
        await ShouldCarryFailureCodeAsync(refused, HttpStatusCode.Conflict, "role_group.in_use");

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await MemberClientAsync();

        using HttpResponseMessage response = await client.GetAsync(RoleGroupsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A membership's effective date reaches the wire, and an unpaid role's expiry is reported as absent.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleMembership_CarriesAFutureEffectiveDateAndLeavesAnUnpaidExpiryAbsent()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);

        DateTime effective = DateTime.UtcNow.AddDays(30);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, role.RoleId),
            new RoleAssignmentRequest
            {
                UserId = _fixture.Seed.MemberUserId,
                EffectiveDate = effective,
            },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        RoleMembershipDto membership = await ReadSingleMembershipAsync(client, role.RoleId);

        membership.EffectiveDate.Should().NotBeNull("a future effective date survives the legacy clamp");
        membership.EffectiveDate!.Value.Date.Should().Be(effective.Date);
        membership.ExpiryDate.Should().BeNull("an unpaid role declares no billing period, so it never expires");
    }

    /// <summary>A paid role's membership carries the expiry the billing terms compute.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleMembership_ForAPaidRoleCarriesTheComputedExpiry()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage createdRole = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            new CreateRoleRequest
            {
                RoleName = "ITest Paid Role " + Suffix(),
                Description = "A monthly paid role, so the expiry is computed from its billing terms.",
                IsPublic = false,
                AutoAssignment = false,
                ServiceFee = 9.99m,
                BillingPeriod = 1,
                BillingFrequency = BillingFrequency.Month,
            },
            ApiTestFixture.Json);

        createdRole.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto role = await ReadDetailAsync(createdRole);

        DateTime before = DateTime.UtcNow;

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, role.RoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        RoleMembershipDto membership = await ReadSingleMembershipAsync(client, role.RoleId);

        membership.ExpiryDate.Should().NotBeNull("a monthly paid role expires one period after it begins");
        membership.ExpiryDate!.Value.Should().BeAfter(before.AddDays(27));
        membership.ExpiryDate!.Value.Should().BeBefore(before.AddDays(32));
    }

    /// <summary>
    /// An open-ended membership never reports a sentinel date, and its dates deserialise as absent.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// An absent date is written as an explicit null rather than omitted, because this API serialises with
    /// <c>JsonIgnoreCondition.Never</c> throughout - measured on a live response, which returns
    /// <c>"effectiveDate":null,"expiryDate":null</c> for an open-ended membership.
    /// </remarks>
    [Fact]
    public async Task RoleMembership_ReportsAnOpenEndedMembershipWithoutASentinelDate()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, role.RoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage members = await client.GetAsync(new Uri(
            RoleUsersRoute(_fixture.Seed.PortalId, role.RoleId).OriginalString + "?pageIndex=0&pageSize=100",
            UriKind.Relative));

        members.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await members.Content.ReadAsStringAsync();

        body.Should().NotContain(
            "0001-01-01",
            "the legacy minimum-date sentinel must never reach a caller");

        PagedEnvelope<RoleMembershipDto>? page = JsonSerializer.Deserialize<PagedEnvelope<RoleMembershipDto>>(
            body,
            ApiTestFixture.Json);

        page.Should().NotBeNull();

        RoleMembershipDto membership = page!.Items
            .Should().ContainSingle(item => item.UserId == _fixture.Seed.MemberUserId).Subject;

        membership.EffectiveDate.Should().BeNull();
        membership.ExpiryDate.Should().BeNull();
    }

    /// <summary>
    /// The ungrouped scope lists the roles belonging to no group - the legacy "&lt; Global Roles &gt;"
    /// selection - and excludes a role that has been placed in a group.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the third of the three legacy grouping intents, and the one that no group identifier can
    /// express. It is asserted end to end because the distinction it rests on is a stored one: an ungrouped
    /// role holds SQL null in <c>Roles.RoleGroupID</c>, which is a nullable column.
    /// </remarks>
    [Fact]
    public async Task ListRoles_WithTheUngroupedScope_ExcludesAGroupedRole()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        RoleGroupDto group = await CreateRoleGroupAsync(client);
        RoleDetailDto ungrouped = await CreateRoleAsync(client);
        RoleDetailDto grouped = await CreateRoleAsync(client);

        using HttpResponseMessage placed = await client.PutAsJsonAsync(
            new Uri($"/api/v1/roles/{Route(grouped.RoleId)}", UriKind.Relative),
            // The contract is a replacement, so placing a role in a group means replaying the role's own
            // state - its name included - with the group identifier added.
            new UpdateRoleRequest
            {
                RoleName = grouped.RoleName,
                Description = grouped.Description,
                RoleGroupId = group.RoleGroupId,
                IsPublic = grouped.IsPublic,
                AutoAssignment = grouped.AutoAssignment,
            },
            ApiTestFixture.Json);

        placed.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/roles?scope=Ungrouped&pageIndex=0&pageSize=100",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();

        IEnumerable<int> listed = page!.Items.Select(item => item.RoleId);
        listed.Should().Contain(ungrouped.RoleId, "it belongs to no group");
        listed.Should().NotContain(grouped.RoleId, "it was placed in a group");
    }

    /// <summary>
    /// A group identifier combined with the ungrouped scope is refused as a bad request, not resolved by
    /// preferring one of the two.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_WithBothAGroupAndTheUngroupedScope_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleGroupDto group = await CreateRoleGroupAsync(client);

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/roles?roleGroupId={Route(group.RoleGroupId)}&scope=Ungrouped",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>An unrecognised scope spelling is refused by model binding before the action runs.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_WithAnUnrecognisedScope_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/roles?scope=NotAScope",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A fee of exactly zero is accepted on both fee members, which is the <c>GreaterThanEqual</c>
    /// boundary, and the value survives as zero rather than being erased.
    /// </summary>
    /// <param name="onServiceFee">
    /// <see langword="true"/> to exercise the service fee, <see langword="false"/> for the trial fee.
    /// </param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The second assertion is the sentinel half of the same fact. Serialisation is configured with the
    /// <c>Never</c> ignore condition, so a zero is written as a zero and an absent fee is written as an
    /// explicit null; the two are different states and a caller must be able to tell them apart.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateRole_WithAFeeOfZero_IsCreatedCarryingThatFee(bool onServiceFee)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        if (onServiceFee)
        {
            request.ServiceFee = 0m;
        }
        else
        {
            request.TrialFee = 0m;
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the comparison validator behind this member is GreaterThanEqual against zero, so a free "
            + "role and a free trial are configurations the legacy screen accepted");

        RoleDetailDto created = await ReadDetailAsync(response);

        decimal? submitted = onServiceFee ? created.ServiceFee : created.TrialFee;
        decimal? omitted = onServiceFee ? created.TrialFee : created.ServiceFee;

        submitted.Should().Be(0m, "a submitted zero is a value and must not be read back as absence");
        omitted.Should().BeNull("a fee that was never supplied stays absent rather than becoming zero");
    }

    /// <summary>
    /// A negative fee is refused on both fee members, naming the offending member and carrying the legacy
    /// wording, and nothing reaches the store.
    /// </summary>
    /// <param name="onServiceFee">
    /// <see langword="true"/> to exercise the service fee, <see langword="false"/> for the trial fee.
    /// </param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateRole_WithAFeeBelowZero_NamesTheMemberAndStoresNothing(bool onServiceFee)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        if (onServiceFee)
        {
            request.ServiceFee = -0.01m;
        }
        else
        {
            request.TrialFee = -0.01m;
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        await ShouldReportFieldAsync(
            response,
            onServiceFee ? nameof(CreateRoleRequest.ServiceFee) : nameof(CreateRoleRequest.TrialFee),
            onServiceFee ? ServiceFeeNegativeMessage : TrialFeeNegativeMessage);

        int stored = await CountRolesNamedAsync(request.RoleName);

        stored.Should().Be(
            0,
            "the role write paths refuse a negative fee rather than clamping it, so a refused request "
            + "must leave no row behind at all");
    }

    /// <summary>
    /// A period that is not strictly positive is refused on both period members, naming the offending
    /// member and carrying the legacy wording.
    /// </summary>
    /// <param name="onBillingPeriod">
    /// <see langword="true"/> to exercise the billing period, <see langword="false"/> for the trial period.
    /// </param>
    /// <param name="period">The value submitted: the zero boundary, and one below it.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The zero cases are the load-bearing ones. Zero is precisely where a "greater than or equal to" rule
    /// and a "greater than" rule disagree, so a validator that had copied the fee rule onto the periods
    /// would accept these two submissions and every other assertion in this suite would still pass.
    /// </remarks>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    public async Task CreateRole_WithAPeriodThatIsNotPositive_NamesTheMember(bool onBillingPeriod, int period)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        if (onBillingPeriod)
        {
            request.BillingPeriod = period;
            request.BillingFrequency = BillingFrequency.Month;
        }
        else
        {
            request.TrialPeriod = period;
            request.TrialFrequency = BillingFrequency.Month;
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        await ShouldReportFieldAsync(
            response,
            onBillingPeriod
                ? nameof(CreateRoleRequest.BillingPeriod)
                : nameof(CreateRoleRequest.TrialPeriod),
            onBillingPeriod ? BillingPeriodNotPositiveMessage : TrialPeriodNotPositiveMessage);
    }

    /// <summary>
    /// A period of one - the smallest strictly positive value, and therefore the accepted boundary - is
    /// created and carries the submitted period and frequency.
    /// </summary>
    /// <param name="onBillingPeriod">
    /// <see langword="true"/> to exercise the billing term, <see langword="false"/> for the trial term.
    /// </param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateRole_WithAPeriodOfOne_IsCreatedCarryingThatPeriod(bool onBillingPeriod)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        if (onBillingPeriod)
        {
            request.ServiceFee = 5m;
            request.BillingPeriod = 1;
            request.BillingFrequency = BillingFrequency.Month;
        }
        else
        {
            request.TrialFee = 0m;
            request.TrialPeriod = 1;
            request.TrialFrequency = BillingFrequency.Week;
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "one is the smallest period the strictly-positive comparison admits");

        RoleDetailDto created = await ReadDetailAsync(response);

        if (onBillingPeriod)
        {
            created.BillingPeriod.Should().Be(1);
            created.BillingFrequency.Should().Be(BillingFrequency.Month);
            created.TrialPeriod.Should().BeNull("an unsupplied term stays absent");
        }
        else
        {
            created.TrialPeriod.Should().Be(1);
            created.TrialFrequency.Should().Be(BillingFrequency.Week);
            created.BillingPeriod.Should().BeNull("an unsupplied term stays absent");
        }
    }

    /// <summary>
    /// A fee far above the baseline column's 999.99 limit is accepted and round-trips exactly, because the
    /// terminal column is <c>money</c> and no legacy validator bounded either fee above.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION - A CEILING THAT NO LONGER APPLIES, REFUTED BY MEASUREMENT RATHER THAN ASSUMED AWAY. The
    /// baseline schema declares <c>[ServiceFee] [decimal](5, 2)</c> (<c>01.00.00.SqlDataProvider</c> L119),
    /// which caps a fee at 999.99, and it would be easy to carry that cap forward as a validation rule.
    /// </remarks>
    [Fact]
    public async Task CreateRole_WithAFeeAboveTheBaselineColumnLimit_IsCreatedAndStoredExactly()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        const decimal baselineCeiling = 999.99m;
        const decimal wellAboveIt = 1_000_000.99m;

        CreateRoleRequest atTheBaselineCeiling = NewRoleRequest();
        atTheBaselineCeiling.ServiceFee = baselineCeiling;

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            atTheBaselineCeiling,
            ApiTestFixture.Json);

        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadDetailAsync(accepted)).ServiceFee.Should().Be(baselineCeiling);

        CreateRoleRequest aboveTheBaselineCeiling = NewRoleRequest();
        aboveTheBaselineCeiling.ServiceFee = wellAboveIt;
        aboveTheBaselineCeiling.TrialFee = wellAboveIt;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            aboveTheBaselineCeiling,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the terminal column is money, so the baseline decimal(5, 2) limit is not a rule this API "
            + "may enforce");

        RoleDetailDto created = await ReadDetailAsync(response);
        created.ServiceFee.Should().Be(wellAboveIt);
        created.TrialFee.Should().Be(wellAboveIt);

        decimal persisted = await _fixture.Database.ScalarAsync<decimal>(
            "SELECT [ServiceFee] FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        persisted.Should().Be(
            wellAboveIt,
            "the column stores the amount as submitted rather than truncating it to the baseline scale");
    }

    /// <summary>
    /// A fee the <c>money</c> column cannot represent is refused as a field-level failure naming the
    /// member, rather than reaching the provider and surfacing as a server fault.
    /// </summary>
    /// <param name="onServiceFee">
    /// <see langword="true"/> to exercise the service fee, <see langword="false"/> for the trial fee.
    /// </param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Representability, not a price ceiling - the distinction matters, because the test above proves no
    /// business maximum exists. What is refused here is an amount the column cannot hold AT ALL, and the
    /// reason to refuse it at the boundary is the shape of the answer: unbounded, the value reached the
    /// database client and came back as a 500 naming no field, which tells a caller nothing it can act on.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateRole_WithAFeeTheColumnCannotHold_NamesTheMember(bool onServiceFee)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        decimal unrepresentable = SqlServerRange.MaximumMoney + 1m;

        CreateRoleRequest request = NewRoleRequest();
        if (onServiceFee)
        {
            request.ServiceFee = unrepresentable;
        }
        else
        {
            request.TrialFee = unrepresentable;
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        // The refusal is a field-level answer naming the member, which is the whole point: unbounded, the
        // value reached the database client and came back as a server fault naming nothing at all.
        await ShouldNameFieldAsync(
            response,
            onServiceFee ? nameof(CreateRoleRequest.ServiceFee) : nameof(CreateRoleRequest.TrialFee));
    }

    /// <summary>
    /// Identifier zero addresses a real role, over both the nested and the flat address, because
    /// <c>Roles.RoleID</c> is declared <c>IDENTITY (0, 1)</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The identity seed is asserted first rather than taken on trust. Each run provisions a freshly named
    /// database and seeds three roles into it, so the administrators role genuinely occupies the seed
    /// value; if that ever stopped being true this test would still be correct but would no longer be
    /// exercising zero, and the assertion says so.
    /// </remarks>
    [Fact]
    public async Task GetRole_ForTheIdentitySeededRole_ResolvesIdentifierZero()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        int lowest = await _fixture.Database.ScalarAsync<int>(
            "SELECT MIN([RoleID]) FROM [dbo].[Roles];",
            new Dictionary<string, object?>());

        lowest.Should().Be(
            0,
            "Roles.RoleID is IDENTITY(0, 1), so the first role stored in a fresh database is numbered "
            + "zero rather than one");

        _fixture.Seed.AdministratorRoleId.Should().Be(
            0,
            "the administrators role is the first row this suite inserts, so it is the one that occupies "
            + "the identity seed and therefore the one that proves zero is addressable");

        using HttpResponseMessage nested = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, 0));

        nested.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "zero is a role key and must not be read as an absent identifier");

        RoleDetailDto read = await ReadDetailAsync(nested);
        read.RoleId.Should().Be(0);
        read.RoleName.Should().Be(IntegrationSeed.AdministratorsRoleName);

        string body = await nested.Content.ReadAsStringAsync();
        body.Should().Contain(
            "\"roleId\":0",
            "the identifier reaches the wire as an explicit zero rather than being omitted as a default");

        using HttpResponseMessage flat = await client.GetAsync(new Uri("/api/v1/roles/0", UriKind.Relative));

        flat.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the {roleId:int} route constraint carries no lower bound, so the flat address resolves zero "
            + "as well");

        (await ReadDetailAsync(flat)).RoleId.Should().Be(0);
    }

    /// <summary>
    /// A role that belongs to no group carries an explicit <c>null</c> group reference, and the legacy
    /// minus-one marker for that state is refused rather than tunnelled through the contract.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The marker never reached the column even in the legacy application, and the schema is what
    /// guarantees it: <c>FK_Roles_RoleGroups</c> constrains the column to a real group row
    /// (<c>03.02.03.SqlDataProvider</c> L37, re-added at <c>04.00.04</c> L70) while
    /// <c>RoleGroups.RoleGroupID</c> is <c>IDENTITY (0, 1)</c>, so no group can bear -1 and the marker was
    /// converted to a database null on the way down.
    /// </remarks>
    [Fact]
    public async Task RoleDetail_KeepsAnAbsentGroupExplicitAndRefusesTheLegacyMarker()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        RoleDetailDto ungrouped = await CreateRoleAsync(client);
        ungrouped.RoleGroupId.Should().BeNull("no group was nominated, so the role belongs to none");

        using HttpResponseMessage read = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, ungrouped.RoleId));

        read.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await read.Content.ReadAsStringAsync();
        body.Should().Contain(
            "\"roleGroupId\":null",
            "absence is stated explicitly rather than expressed by a missing member");

        int storedGroups = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[RoleGroups] WHERE [RoleGroupID] = -1;",
            new Dictionary<string, object?>());

        storedGroups.Should().Be(
            0,
            "RoleGroups.RoleGroupID is IDENTITY(0, 1) and FK_Roles_RoleGroups constrains the role's "
            + "column to a real group, so the legacy minus-one marker is unrepresentable at the store");

        CreateRoleRequest withTheMarker = NewRoleRequest();
        withTheMarker.RoleGroupId = -1;

        using HttpResponseMessage refused = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            withTheMarker,
            ApiTestFixture.Json);

        await ShouldCarryFailureCodeAsync(
            refused,
            HttpStatusCode.NotFound,
            "role_group.not_found");

        (await CountRolesNamedAsync(withTheMarker.RoleName)).Should().Be(
            0,
            "a refused group reference must not fall back to storing the role ungrouped");
    }

    /// <summary>
    /// A role really does travel with the group it was created in, whatever that group's identifier, so a
    /// group key of zero is carried rather than treated as absence.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The companion of the assertion above. <c>RoleGroups.RoleGroupID</c> is seeded at zero as well, so
    /// the group reference has the same collision the role key has and neither the request contract nor the
    /// validator may bound it - the validator records exactly that, and this test is what would fail if a
    /// bound were ever added.
    /// </remarks>
    [Fact]
    public async Task CreateRole_InARoleGroup_CarriesTheGroupIdentifierWhateverItsMagnitude()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleGroupDto group = await CreateRoleGroupAsync(client);

        group.RoleGroupId.Should().BeGreaterThanOrEqualTo(
            0,
            "the group key is IDENTITY(0, 1), so zero is its first legitimate value");

        CreateRoleRequest request = NewRoleRequest();
        request.RoleGroupId = group.RoleGroupId;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto created = await ReadDetailAsync(response);
        created.RoleGroupId.Should().Be(group.RoleGroupId);

        int persisted = await _fixture.Database.ScalarAsync<int>(
            "SELECT [RoleGroupID] FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        persisted.Should().Be(group.RoleGroupId, "the reference is stored as submitted");
    }

    /// <summary>
    /// The listing answers with the paging companion beside the records, carrying a real total and the
    /// coordinates the caller asked for.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The page index is read back rather than assumed. The base is zero here - index 0 is the first page -
    /// and the assertion is written against what the caller sent rather than against a constant, so it
    /// states that the coordinates are echoed without restating the base in a second place.
    /// </remarks>
    [Fact]
    public async Task ListRoles_CarriesThePagingCompanionWithARealTotal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        const int pageIndex = 0;
        const int pageSize = 2;

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/roles"
            + $"?pageIndex={Route(pageIndex)}&pageSize={Route(pageSize)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Meta.Should().NotBeNull("the paging facts travel in a companion object, not as siblings");

        page.TotalCount.Should().BeGreaterThanOrEqualTo(
            3,
            "the tenant is seeded with an administrators, a registered-users and a subscribers role");
        page.TotalCount.Should().NotBe(
            -1,
            "the legacy by-reference total used the absent-integer marker, and the envelope publishes a "
            + "count instead");

        page.PageIndex.Should().Be(pageIndex, "the coordinates a caller sends are echoed back");
        page.PageSize.Should().Be(pageSize);
        page.Items.Should().HaveCountLessThanOrEqualTo(pageSize);
        page.Items.Should().NotBeEmpty("a tenant with three roles has a non-empty first page");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"items\":");
        body.Should().Contain("\"meta\":");
        body.Should().Contain("\"totalCount\":");
    }

    /// <summary>
    /// The free-text filter narrows both the records and the reported total, and a filter that matches
    /// nothing is a successful empty page rather than a failure.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE MATCH IS A SUBSTRING MATCH, NOT A PREFIX MATCH, and the mid-string case is asserted explicitly
    /// so that a later change of semantics fails here rather than silently changing the contract.
    /// </remarks>
    [Fact]
    public async Task ListRoles_FiltersByNameAndNarrowsTheReportedTotal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);

        PagedEnvelope<RoleListItemDto> exact = await ListRolesAsync(client, created.RoleName);

        exact.Items.Should().ContainSingle(item => item.RoleId == created.RoleId);
        exact.TotalCount.Should().Be(
            exact.Items.Count,
            "a filtered listing reports the filtered total, not the unfiltered one");

        PagedEnvelope<RoleListItemDto> unmatched = await ListRolesAsync(
            client,
            "no-role-bears-this-name-" + Suffix());

        unmatched.Items.Should().BeEmpty();
        unmatched.TotalCount.Should().Be(
            0,
            "nothing matched is a successful empty page reporting zero, never the absent-integer marker");

        // The measured mid-string case. The created name is "ITest Role <suffix>", so a fragment taken
        // from inside the suffix cannot be a prefix of it.
        string midStringFragment = created.RoleName[^6..];

        PagedEnvelope<RoleListItemDto> withinTheName = await ListRolesAsync(client, midStringFragment);

        withinTheName.Items.Should().Contain(
            item => item.RoleId == created.RoleId,
            "the role filter matches anywhere within the name, which is what the service applies");
    }

    /// <summary>
    /// A caller-supplied correlation identifier comes back exactly once on a success and is still present
    /// on a deliberately failed request; one is minted when the caller supplies none; and an unusable value
    /// is replaced rather than echoed, without becoming a refusal of its own.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The single-value assertion is not pedantry either: a middleware that appended rather than assigned
    /// would produce two values on a request that already carried one, and a client reading the first would
    /// silently disagree with a log written from the second.
    /// </remarks>
    [Fact]
    public async Task RoleRequests_RoundTripTheCorrelationIdentifierIncludingOnFailure()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string supplied = ApiTestFixture.NewCorrelationId();

        using var successRequest = new HttpRequestMessage(
            HttpMethod.Get,
            RolesRoute(_fixture.Seed.PortalId));

        using HttpResponseMessage success = await client.SendAsync(
            ApiTestFixture.WithCorrelationId(successRequest, supplied));

        success.StatusCode.Should().Be(HttpStatusCode.OK);
        ApiTestFixture.ReadCorrelationId(success).Should().Be(supplied);
        success.Headers.GetValues(ApiTestFixture.CorrelationIdHeader).Should().HaveCount(
            1,
            "the header is assigned rather than appended, so a supplied value is not duplicated");

        string onFailure = ApiTestFixture.NewCorrelationId();

        using var failureRequest = new HttpRequestMessage(
            HttpMethod.Get,
            RoleRoute(_fixture.Seed.PortalId, UnknownRoleId));

        using HttpResponseMessage failure = await client.SendAsync(
            ApiTestFixture.WithCorrelationId(failureRequest, onFailure));

        failure.StatusCode.Should().Be(HttpStatusCode.NotFound);

        ProblemDetails? refusal = await failure.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        refusal.Should().NotBeNull("the failure this identifier is meant to trace is a problem document");

        ApiTestFixture.ReadCorrelationId(failure).Should().Be(
            onFailure,
            "the identifier a caller quotes when reporting a problem must survive the problem");

        using HttpResponseMessage minted = await client.GetAsync(RolesRoute(_fixture.Seed.PortalId));

        minted.StatusCode.Should().Be(HttpStatusCode.OK);
        ApiTestFixture.ReadCorrelationId(minted).Should().NotBeNullOrWhiteSpace(
            "every response carries an identifier, including one the caller did not name");

        using var oversizeRequest = new HttpRequestMessage(
            HttpMethod.Get,
            RolesRoute(_fixture.Seed.PortalId));

        string oversize = new('x', 129);

        using HttpResponseMessage sanitised = await client.SendAsync(
            ApiTestFixture.WithCorrelationId(oversizeRequest, oversize));

        sanitised.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an unusable diagnostic header is replaced, never allowed to refuse the request it rode in on");

        string? replacement = ApiTestFixture.ReadCorrelationId(sanitised);
        replacement.Should().NotBeNullOrWhiteSpace();
        replacement.Should().NotBe(oversize, "a value beyond the bound is replaced rather than echoed");
    }

    /// <summary>The role resource publishes no address for the operations that belong elsewhere or nowhere.</summary>
    /// <param name="path">The address that must not resolve to a role operation.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Role GROUP management is the first pair: a group's rules are rules about the roles it classifies, so
    /// the same application contract serves both, but the ADDRESSES are separate and the group ones live
    /// under the kebab-cased <c>role-groups</c> collection.
    /// </remarks>
    [Theory]
    [InlineData("/api/v1/roles/groups")]
    [InlineData("/api/v1/roles/0/groups")]
    [InlineData("/api/v1/roles/0/rsvp")]
    [InlineData("/api/v1/roles/0/redeem")]
    [InlineData("/api/v1/roles/0/transactions")]
    [InlineData("/api/v1/roles/0/services")]
    [InlineData("/api/v1/roles/cache")]
    [InlineData("/api/v1/roles/bulk")]
    public async Task RoleResource_PublishesNoAddressForTheOperationsItDoesNotOwn(string path)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed],
            "an address this resource does not serve must not resolve to one that it does");
    }

    /// <summary>
    /// Removing a paid membership whose trial has been consumed back-dates the expiry to yesterday and
    /// keeps the row, rather than deleting it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The legacy stamp was <c>Date.Today()</c>, which is server-LOCAL, while the injected clock is UTC
    /// only. For the same real instant the two can name different calendar days either side of Greenwich.
    /// </remarks>
    [Fact]
    public async Task RemoveAssignment_ForAPaidMembershipWithAUsedTrial_ExpiresItRatherThanDeletingIt()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest paid = NewRoleRequest();
        paid.ServiceFee = 25m;
        paid.BillingPeriod = 1;
        paid.BillingFrequency = BillingFrequency.Month;

        using HttpResponseMessage createdResponse = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            paid,
            ApiTestFixture.Json);

        createdResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        RoleDetailDto role = await ReadDetailAsync(createdResponse);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, role.RoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await _fixture.Database.ExecuteAsync(
            """
            UPDATE [dbo].[UserRoles]
               SET [IsTrialUsed] = 1
             WHERE [RoleID] = @roleId AND [UserID] = @userId;
            """,
            new Dictionary<string, object?>
            {
                ["roleId"] = role.RoleId,
                ["userId"] = _fixture.Seed.MemberUserId,
            });

        DateTime before = DateTime.UtcNow.Date;

        using HttpResponseMessage removed = await client.DeleteAsync(
            RoleUserRoute(_fixture.Seed.PortalId, role.RoleId, _fixture.Seed.MemberUserId));

        DateTime after = DateTime.UtcNow.Date;

        removed.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "the account no longer holds the role, whichever arm the service took");

        (await CountAssignmentsAsync(role.RoleId)).Should().Be(
            1,
            "the row is retained so the trial-used fact is not lost");

        DateTime expiry = await _fixture.Database.ScalarAsync<DateTime>(
            """
            SELECT [ExpiryDate]
            FROM [dbo].[UserRoles]
            WHERE [RoleID] = @roleId AND [UserID] = @userId;
            """,
            new Dictionary<string, object?>
            {
                ["roleId"] = role.RoleId,
                ["userId"] = _fixture.Seed.MemberUserId,
            });

        expiry.TimeOfDay.Should().Be(
            TimeSpan.Zero,
            "the legacy value carried no time component, and the truncation is preserved so the "
            + "membership reads as expired for the whole of the current day");

        expiry.Should().BeOneOf(
            before.AddDays(-1),
            after.AddDays(-1));
    }

    /// <summary>A one-time term derives the far-future perpetual expiry rather than an offset from today.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The date also reaches the wire unrounded, which the storage bound makes possible: SQL Server's
    /// <c>datetime</c> tops out at 9999-12-31, so the sentinel is storable exactly rather than being
    /// clamped to something near it.
    /// </remarks>
    [Fact]
    public async Task Assignment_ForAOneTimeTerm_DerivesThePerpetualExpiry()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest oneTime = NewRoleRequest();
        oneTime.ServiceFee = 10m;
        oneTime.BillingPeriod = 1;
        oneTime.BillingFrequency = BillingFrequency.OneTime;

        using HttpResponseMessage createdResponse = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            oneTime,
            ApiTestFixture.Json);

        createdResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto role = await ReadDetailAsync(createdResponse);
        role.BillingFrequency.Should().Be(BillingFrequency.OneTime);

        using HttpResponseMessage assigned = await client.PostAsJsonAsync(
            RoleUsersRoute(_fixture.Seed.PortalId, role.RoleId),
            new RoleAssignmentRequest { UserId = _fixture.Seed.MemberUserId },
            ApiTestFixture.Json);

        assigned.StatusCode.Should().Be(HttpStatusCode.NoContent);

        RoleMembershipDto membership = await ReadSingleMembershipAsync(client, role.RoleId);

        membership.ExpiryDate.Should().NotBeNull(
            "a one-time term is perpetual, which is a date rather than the absence of one");
        membership.ExpiryDate!.Value.Date.Should().Be(
            new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc).Date,
            "the legacy one-time arm assigns 9999-12-31, and the value travels rather than being rounded");
    }

    /// <summary>
    /// A role whose stored frequency is a character the vocabulary never declared can still be updated, not
    /// merely read.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_WithAnUnrecognisedStoredFrequency_IsStillWritable()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        RoleDetailDto created = await CreateRoleAsync(client);

        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Roles] SET [BillingFrequency] = '4', [TrialFrequency] = '0' "
            + "WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        var request = new UpdateRoleRequest
        {
            RoleName = created.RoleName,
            Description = "Repaired by the integration suite.",
            ServiceFee = 1m,
            BillingPeriod = 1,
            BillingFrequency = BillingFrequency.Year,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, created.RoleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a row a legacy installation already holds must be repairable rather than a server fault");

        RoleDetailDto updated = await ReadDetailAsync(response);
        updated.BillingFrequency.Should().Be(BillingFrequency.Year);
        updated.Description.Should().Be(request.Description);

        string storedCode = await _fixture.Database.ScalarAsync<string>(
            "SELECT [BillingFrequency] FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = created.RoleId });

        storedCode.Should().Be("Y", "the undeclared character is replaced by the submitted code");
    }

    /// <summary>Reads a role's memberships and returns the one held by the seeded member account.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <param name="roleId">The role whose memberships are read.</param>
    /// <returns>The single membership found for the seeded member.</returns>
    private async Task<RoleMembershipDto> ReadSingleMembershipAsync(HttpClient client, int roleId)
    {
        using HttpResponseMessage members = await client.GetAsync(new Uri(
            RoleUsersRoute(_fixture.Seed.PortalId, roleId).OriginalString + "?pageIndex=0&pageSize=100",
            UriKind.Relative));

        members.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleMembershipDto>? page = await members.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleMembershipDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();

        return page!.Items
            .Should().ContainSingle(item => item.UserId == _fixture.Seed.MemberUserId).Subject;
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
            new CreateRoleGroupRequest
            {
                RoleGroupName = "ITest Group " + Suffix(),
                Description = "Created by the integration suite.",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleGroupDto? created = await response.Content
            .ReadEnvelopeAsync<RoleGroupDto>();

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>Creates a tenant of this test's own, so a tenant-scoped assertion cannot be disturbed.</summary>
    /// <param name="client">A client carrying host credentials.</param>
    /// <returns>The new tenant, together with everything needed to address it.</returns>
    /// <remarks>
    /// The alias and the administrator account are returned rather than discarded because every role route
    /// is tenant-scoped: the portal-administrator policy requires the route's tenant to be the tenant the
    /// request resolved to, and resolution is by host name.
    /// </remarks>
    private async Task<IsolatedTenant> CreateIsolatedPortalAsync(HttpClient client)
    {
        string suffix = Suffix();
        string alias = "roles-" + suffix + ".local";
        string administrator = "roles_admin_" + suffix;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "Role Suite Portal " + suffix,
                portalAlias = alias,
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Suite",
                administratorLastName = "Administrator",
                administratorUsername = administrator,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "roles." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using JsonDocument document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement created = document.RootElement.GetProperty("data");

        return new IsolatedTenant(
            created.GetProperty("portalId").GetInt32(),
            alias,
            created.GetProperty("administratorId").GetInt32(),
            administrator);
    }

    /// <summary>Builds a client that resolves to an isolated tenant, as that tenant's own administrator.</summary>
    /// <param name="tenant">The tenant to address.</param>
    /// <returns>An authenticated client addressed at the tenant.</returns>
    private Task<HttpClient> TenantClientAsync(IsolatedTenant tenant) => _fixture.CreateTenantClientAsync(
        tenant.Alias,
        tenant.PortalId,
        tenant.AdministratorUserName);

    /// <summary>A tenant created by this suite, and everything needed to address it.</summary>
    /// <param name="PortalId">The tenant identifier.</param>
    /// <param name="Alias">The host name bound to it, which is what resolves it.</param>
    /// <param name="AdministratorId">Its designated administrator's account key.</param>
    /// <param name="AdministratorUserName">That administrator's account name.</param>
    private sealed record IsolatedTenant(
        int PortalId,
        string Alias,
        int AdministratorId,
        string AdministratorUserName);

    /// <summary>Counts the assignments held against one role.</summary>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>The number of assignment rows.</returns>
    private Task<int> CountAssignmentsAsync(int roleId) => _fixture.Database.ScalarAsync<int>(
        "SELECT COUNT(*) FROM [dbo].[UserRoles] WHERE [RoleID] = @roleId;",
        new Dictionary<string, object?> { ["roleId"] = roleId });

    /// <summary>Counts the module grants addressed to one role on one module.</summary>
    /// <param name="moduleId">The module whose grants are counted.</param>
    /// <param name="roleId">The role the grants are addressed to.</param>
    /// <returns>The number of matching grant rows.</returns>
    private Task<int> CountModuleGrantsForRoleAsync(int moduleId, int roleId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModulePermission] "
            + "WHERE [ModuleID] = @moduleId AND [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["moduleId"] = moduleId, ["roleId"] = roleId });

    /// <summary>Counts the module grants addressed to one account on one module.</summary>
    /// <param name="moduleId">The module whose grants are counted.</param>
    /// <param name="userId">The account the grants are addressed to.</param>
    /// <returns>The number of matching grant rows.</returns>
    private Task<int> CountModuleGrantsForAccountAsync(int moduleId, int userId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModulePermission] "
            + "WHERE [ModuleID] = @moduleId AND [UserID] = @userId;",
            new Dictionary<string, object?> { ["moduleId"] = moduleId, ["userId"] = userId });

    /// <summary>Counts the page grants addressed to one role on one page.</summary>
    /// <param name="tabId">The page whose grants are counted.</param>
    /// <param name="roleId">The role the grants are addressed to.</param>
    /// <returns>The number of matching grant rows.</returns>
    private Task<int> CountTabGrantsForRoleAsync(int tabId, int roleId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabPermission] "
            + "WHERE [TabID] = @tabId AND [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["tabId"] = tabId, ["roleId"] = roleId });

    /// <summary>Counts the folder grants addressed to one role.</summary>
    /// <param name="roleId">The role the grants address.</param>
    /// <returns>The number of matching grant rows.</returns>
    /// <remarks>
    /// Only meaningful while the calling test has the legacy folder grant table in place; the schema this
    /// suite provisions declares none, because file management is outside the migration's scope.
    /// </remarks>
    private Task<int> CountFolderGrantsForRoleAsync(int roleId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[FolderPermission] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = roleId });

    /// <summary>Counts how many of exactly four page grants still exist, named by key.</summary>
    /// <param name="tabPermissionIds">The four keys, in insertion order.</param>
    /// <returns>The number of those rows that survive.</returns>
    private Task<int> CountTabGrantsByKeyAsync(IReadOnlyList<int> tabPermissionIds) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabPermission] "
            + "WHERE [TabPermissionID] IN (@first, @second, @third, @fourth);",
            new Dictionary<string, object?>
            {
                ["first"] = tabPermissionIds[0],
                ["second"] = tabPermissionIds[1],
                ["third"] = tabPermissionIds[2],
                ["fourth"] = tabPermissionIds[3],
            });

    /// <summary>Signs in as the seeded account that holds no administrative role.</summary>
    /// <returns>An authenticated client without the administrators role.</returns>
    private Task<HttpClient> MemberClientAsync() => _fixture.CreateUnprivilegedClientAsync();

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
            .ReadEnvelopeAsync<RoleDetailDto>();

        detail.Should().NotBeNull();
        return detail!;
    }

    /// <summary>Reads one page of the tenant's roles through the free-text filter.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <param name="query">The filter text, sent as supplied and escaped for transport.</param>
    /// <returns>The page, as it travels on the wire.</returns>
    private async Task<PagedEnvelope<RoleListItemDto>> ListRolesAsync(HttpClient client, string query)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/roles"
            + $"?pageIndex=0&pageSize=100&query={Uri.EscapeDataString(query)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!;
    }

    /// <summary>Counts the roles the seeded tenant holds under one name.</summary>
    /// <param name="roleName">The name to count.</param>
    /// <returns>The number of matching rows, which is zero when a write was refused.</returns>
    /// <remarks>
    /// Read straight from the column rather than through the API, because the question being asked is
    /// whether a REFUSED request left anything behind, and a refusal that had quietly written a row would
    /// be invisible to a listing filtered by the same rules that produced the refusal.
    /// </remarks>
    private Task<int> CountRolesNamedAsync(string roleName) => _fixture.Database.ScalarAsync<int>(
        "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleName] = @roleName AND [PortalID] = @portalId;",
        new Dictionary<string, object?>
        {
            ["roleName"] = roleName,
            ["portalId"] = _fixture.Seed.PortalId,
        });

    /// <summary>
    /// Asserts that a response is the declared field-error document, naming one member and carrying one
    /// message exactly.
    /// </summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="member">The member the document must attribute the failure to.</param>
    /// <param name="message">The message the document must carry for that member.</param>
    /// <returns>A task representing the assertion.</returns>
    private static async Task ShouldReportFieldAsync(
        HttpResponseMessage response,
        string member,
        string message)
    {
        ValidationProblemDetails problem = await ShouldNameFieldAsync(response, member);

        problem.Errors[member].Should().Contain(
            message,
            "the wording travels to the operator, so it must be the legacy wording exactly");
    }

    /// <summary>
    /// Asserts that a response is the declared field-error document attributing a failure to one member,
    /// without constraining the wording.
    /// </summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="member">The member the document must attribute the failure to.</param>
    /// <returns>The document, so a caller can assert further on it.</returns>
    private static async Task<ValidationProblemDetails> ShouldNameFieldAsync(
        HttpResponseMessage response,
        string member)
    {
        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a value that breaks a declared rule is refused at the boundary, not further in");
        response.Content.Headers.ContentType?.MediaType.Should().Be(
            ProblemMediaType,
            "a field-error document is served as a problem document even though the controller declares "
            + "that it produces JSON, so a client can identify one by its content type alone");

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("a refusal carries a problem document rather than an empty body");
        problem!.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Type.Should().NotBeNullOrWhiteSpace("the envelope names the problem type");
        problem.Title.Should().NotBeNullOrWhiteSpace("the envelope carries a human-readable title");
        problem.Errors.Should().ContainKey(
            member,
            "the document attributes the failure to the member the caller sent");

        return problem;
    }

    /// <summary>Renders a response's status and body for an assertion message.</summary>
    /// <param name="response">The response to describe.</param>
    /// <returns>The status and the body, bounded.</returns>
    private static async Task<string> Diagnose(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();

        return FormattableString.Invariant(
            $"the response was {(int)response.StatusCode} with body {body[..Math.Min(body.Length, 600)]}");
    }

    /// <summary>
    /// Asserts that a response is a problem document carrying one status and one named failure code.
    /// </summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="status">The status the refusal must carry.</param>
    /// <param name="code">The failure code, without the shared prefix.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The code is a stable, named string rather than a number, and it is what a client branches on. The
    /// status alone is not enough on these resources: three different reasons answer <c>409</c> - a role
    /// name already taken, a group name already taken and a group that still classifies a role - and two
    /// answer <c>403</c>, a protected assignment and a caller who does not administer the tenant.
    /// </remarks>
    private static async Task ShouldCarryFailureCodeAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be(
            ProblemMediaType,
            "every refusal on these resources is served as a problem document, whether the controller "
            + "produced it or the request never reached one");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("a refusal is published as a problem document whatever its cause");
        problem!.Status.Should().Be(
            (int)status,
            "the document restates its own status, so a client reading the body agrees with the status line");
        problem.Title.Should().NotBeNullOrWhiteSpace("the envelope carries a human-readable title");
        problem.Type.Should().Be(
            ProblemTypePrefix + code,
            "the failure code is what lets a client tell one refusal from another that shares its status");
    }

    /// <summary>Builds the canonical role collection route for the resolved tenant.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <returns>A relative route.</returns>
    private static Uri RolesRoute(int _) => new("/api/v1/roles", UriKind.Relative);

    /// <summary>Builds the item route for one role.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleRoute(int _, int roleId) =>
        new($"/api/v1/roles/{Route(roleId)}", UriKind.Relative);

    /// <summary>Builds the accounts sub-resource route for one role.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleUsersRoute(int _, int roleId) =>
        new($"/api/v1/roles/{Route(roleId)}/users", UriKind.Relative);

    /// <summary>Builds the route for one account's membership of one role.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleUserRoute(int _, int roleId, int userId) => new(
        $"/api/v1/roles/{Route(roleId)}/users/{Route(userId)}",
        UriKind.Relative);

    /// <summary>Builds the roles-of-an-account projection route.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UserRolesRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}/roles", UriKind.Relative);

    /// <summary>Builds the canonical role-group collection route for the resolved tenant.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleGroupsRoute(int _) => new("/api/v1/role-groups", UriKind.Relative);

    /// <summary>Builds the item route for one role group.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="roleGroupId">The group identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RoleGroupRoute(int _, int roleGroupId) =>
        new($"/api/v1/role-groups/{Route(roleGroupId)}", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
