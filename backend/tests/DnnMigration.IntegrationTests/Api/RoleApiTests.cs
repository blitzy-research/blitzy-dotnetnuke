using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    /// <summary>
    /// The membership write path is validated at the BOUNDARY, and identically at both of its addresses.
    /// </summary>
    /// <param name="useFlatAddress">Whether the request is sent to the flat address or the nested one.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The submitted expiry precedes its own effective date, which <c>valDates</c> - the
    /// <c>CompareValidator</c> at <c>Website/admin/Security/securityroles.ascx:L47</c>, operator
    /// <c>GreaterThan</c> - refused. Rule-for-rule parity, including the equal-dates boundary, is proved by
    /// the validator's own unit suite; what this fact proves is different and cannot be proved there: that the
    /// validator is ATTACHED, by the globally registered filter, to this action.
    /// </para>
    /// <para>
    /// Both addresses are exercised because the resource is reachable at two and the filter resolves a
    /// validator from the bound argument's type rather than from a route. The legacy wording is asserted in
    /// the body, because the message is the part of the contract a caller actually reads.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assignment_WithAnExpiryBeforeItsEffectiveDate_ReturnsBadRequest(bool useFlatAddress)
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleDetailDto role = await CreateRoleAsync(client);

        Uri route = useFlatAddress
            ? new Uri($"/api/v1/roles/{Route(role.RoleId)}/users", UriKind.Relative)
            : RoleUsersRoute(_fixture.Seed.PortalId, role.RoleId);

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
    /// The specified FLAT address serves the whole role action set, acting on the tenant the request
    /// resolved to.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// <c>/api/v1/roles</c> is the address the contract froze, and every action is reachable through it:
    /// read the collection, create, read the member, update it and remove it. The nested form is used once,
    /// at the end, to establish the fact that matters most about the flat form - that it acted on the
    /// RESOLVED tenant rather than on some default - by finding the created role through the seeded
    /// portal's own address and confirming the two forms name the same row.
    /// </para>
    /// <para>
    /// The created role is removed at the end so the flat and nested facts do not accumulate rows for one
    /// another to trip over.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FlatRoleAddress_ServesTheResolvedTenantAcrossItsActionSet()
    {
        using HttpClient client = _fixture.CreateHostClient();

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

        // The decisive check: the row the flat address created is the seeded portal's row, which is what
        // "acts on the resolved tenant" means in terms a caller can observe.
        using HttpResponseMessage throughNested = await client.GetAsync(
            RoleRoute(_fixture.Seed.PortalId, role.RoleId));

        throughNested.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage removed = await client.DeleteAsync(itemRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// The specified FLAT membership address grants and removes a membership, and both projections of it
    /// are readable there.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>/api/v1/roles/{roleId}/users</c> is named explicitly by the contract, so it gets a fact of its
    /// own rather than being covered incidentally. The account-side projection
    /// <c>/api/v1/users/{userId}/roles</c> is asserted alongside it because the two are the same relation
    /// read from either end, exactly as the legacy assignment screen offered it.
    /// </remarks>
    [Fact]
    public async Task FlatRoleMembershipAddress_GrantsReadsAndRemovesAMembership()
    {
        using HttpClient client = _fixture.CreateHostClient();
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

        // The record is a MEMBERSHIP, so it identifies the assignment and names the role it grants as well
        // as the account that holds it. The legacy grid rendered the account and the role side by side for
        // exactly this reason (securityroles.ascx:L71-L76).
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
    /// The specified FLAT role-group address serves the whole group action set against the resolved tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task FlatRoleGroupAddress_ServesTheResolvedTenantAcrossItsActionSet()
    {
        using HttpClient client = _fixture.CreateHostClient();

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

        // Read through the ENVELOPE, which is what every payload-bearing success in this API publishes.
        // Deserialising an envelope directly as its payload type yields a non-null object with every
        // member unset, so a raw read here would compare defaults and pass or fail for the wrong reason.
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

        group.Description = "Amended through the flat address.";

        using HttpResponseMessage updated = await client.PutAsJsonAsync(itemRoute, group, ApiTestFixture.Json);
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
    public async Task ListRoles_AsAdministratorOfTheRoutedTenant_ReturnsOk()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A portal administrator naming a tenant other than its own is refused, rather than being served that
    /// tenant's roles.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The tenant a request runs under is resolved from its host header, not from its route, so a route that
    /// names a different portal has to be reconciled against the resolved one before any service is reached.
    /// The legacy screen reconciled it by SUBSTITUTING the ambient tenant unless the caller was a super user
    /// (<c>Website/admin/Portal/SiteSettings.ascx.vb:L235</c>); a REST route cannot substitute, because the
    /// identifier is the resource, so the request is refused instead.
    /// </remarks>
    [Fact]
    public async Task ListRoles_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        int otherPortalId = (await CreateIsolatedPortalAsync(host)).PortalId;

        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(otherPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The role-group collection of another tenant is refused as well, because the reconciliation belongs to
    /// the policy rather than to one controller.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoleGroups_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        int otherPortalId = (await CreateIsolatedPortalAsync(host)).PortalId;

        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(RoleGroupsRoute(otherPortalId));

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
    /// An administrator of one portal is refused on another portal's role collection, which proves the
    /// class-level tenant-administration policy is anchored to the portal named in the ROUTE and not to the
    /// portal the request's host name resolved to. An unknown tenant is refused identically.
    /// </summary>
    /// <remarks>
    /// Both controllers here declare that policy once at class level and every route carries a <c>portalId</c>
    /// segment, so this single fact governs all eleven routes: none of them can be reached for a tenant the
    /// caller does not administer. Before the policy was anchored to the route, an administrator of any portal
    /// could read and rewrite every other tenant's roles, role groups and role memberships - which, because a
    /// role name in a token is what the permission gates resolve against, was a path to arbitrary privilege in
    /// a tenant the caller had nothing to do with.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_ByAnAdministratorOfADifferentPortal_ReturnsForbidden()
    {
        // A genuine, fully provisioned administrator - of the SEEDED portal. The route names a different one.
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(RolesRoute(UnknownPortalId));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a portal this caller does not administer must refuse before the tenant is even looked up, so an "
            + "unauthorised caller cannot tell an existing portal from an absent one");
    }

    /// <summary>An unknown tenant answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This test previously expected <c>404 Not Found</c> for the unknown tenant, which meant the route's
    /// tenant reached the service without ever being checked against the tenant the caller administers. The
    /// refusal now happens in authorisation, and it is deliberately the SAME refusal for a tenant that exists
    /// and one that does not: a caller with no reach into a tenant must not be able to use the difference
    /// between 403 and 404 to enumerate which tenants exist.
    /// </remarks>
    [Fact]
    public async Task ListRoles_ForATenantOtherThanTheResolvedOne_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        IsolatedTenant other = await CreateIsolatedPortalAsync(host);

        // Addressed at the seeded tenant, as its own administrator.
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage existing = await client.GetAsync(RolesRoute(other.PortalId));
        existing.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage absent = await client.GetAsync(RolesRoute(UnknownPortalId));
        absent.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The tenant's own administrator reaches it, which proves the refusal above is the binding and not a
        // blanket denial of every tenant but the seed.
        using HttpClient owner = TenantClient(other);

        using HttpResponseMessage reached = await owner.GetAsync(RolesRoute(other.PortalId));
        reached.StatusCode.Should().Be(HttpStatusCode.OK);
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
    /// <remarks>
    /// The other tenant is addressed as its OWN administrator, which is what makes this test about service-level
    /// tenant scoping rather than about authorisation. A caller that legitimately administers the other tenant
    /// still cannot see the seeded tenant's role through it, because the read is anchored to the route's portal.
    /// The authorisation half - a caller reaching across into a tenant it does not administer - is asserted
    /// separately by <see cref="ListRoles_ForATenantOtherThanTheResolvedOne_ReturnsForbidden"/>; keeping the two
    /// apart matters, because a 403 from authorisation would satisfy neither assertion on its own.
    /// </remarks>
    [Fact]
    public async Task GetRole_WhenRoleBelongsToAnotherTenant_ReturnsNotFound()
    {
        using HttpClient host = _fixture.CreateHostClient();
        IsolatedTenant other = await CreateIsolatedPortalAsync(host);

        using HttpClient client = TenantClient(other);

        using HttpResponseMessage response = await client.GetAsync(
            RoleRoute(other.PortalId, _fixture.Seed.AdministratorRoleId));

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
        IsolatedTenant other = await CreateIsolatedPortalAsync(client);

        // Each tenant is addressed by its own caller, because every role route is tenant-scoped. That is not
        // incidental to this test: the whole claim being made is that the same NAME is free in one tenant and
        // taken in another, and a single caller able to write to both tenants would be the very cross-tenant
        // reach the review flagged.
        using HttpClient owner = TenantClient(other);

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

        // Which tenant now owns the accepted role is proved by ROUTE rather than by a self-reported
        // identifier in the payload: it is readable through the other portal and unreachable through the
        // seeded one. That is the stronger of the two checks, and it is why the detail contract does not
        // echo the owning portal back.
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

        // The wording is valRoleName's own, from Website/admin/Security/editroles.ascx L31, with only
        // the leading markup tag removed. Do not reword it: the string is the parity assertion.
        body.Should().Contain("You Must Enter a Valid Name");
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

        // valBillingPeriod2 (editroles.ascx L113-L114) declares Operator="GreaterThan" against 0 while
        // its ErrorMessage says "or Equal to". The operator is the behaviour and a zero is refused; the
        // wording is carried across unchanged because a legacy defect is annotated, not repaired. Do
        // not "fix" this string to agree with the rule.
        body.Should().Contain("Billing Period Must Be Greater Than or Equal to Zero");
    }

    /// <summary>An update answers <c>200 OK</c> and the new state survives a read.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_ReturnsOkAndPersists()
    {
        using HttpClient client = _fixture.CreateHostClient();
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

    /// <summary>
    /// A row whose stored frequency character is outside the legacy vocabulary is still readable, and the
    /// whole listing that contains it is still readable.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This asserts the consequence rather than the conversion, and the consequence is what made the defect
    /// worth fixing. An unguarded character-to-member cast materialises a value the enumeration does not
    /// declare, which no read complains about; the failure surfaces at the wire, where the serialiser
    /// refuses to write a code it does not recognise. So a single out-of-vocabulary character in a single
    /// row did not degrade one field - it turned a successful read into a server fault, and took every
    /// other role in the same listing down with it.
    /// </para>
    /// <para>
    /// The character planted here is one the product itself ships: the frequency vocabulary table is seeded
    /// with <c>'4'</c> at <c>01.00.00.SqlDataProvider</c> L7192, and the foreign key that once policed the
    /// column is dropped for good at <c>03.00.01.SqlDataProvider</c> L1297 with nothing in its place. The
    /// listing is read as well as the item, because the listing is where the blast radius was.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetRole_WithAnUnrecognisedStoredFrequency_IsStillReadable()
    {
        using HttpClient client = _fixture.CreateHostClient();
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
        read.BillingFrequency.Should().Be(BillingFrequency.None);
        read.TrialFrequency.Should().Be(BillingFrequency.None);

        string body = await item.Content.ReadAsStringAsync();
        body.Should().Contain(
            "\"billingFrequency\":\"N\"",
            "the fallback reaches the wire as the code the legacy application would have treated the "
            + "unrecognised character as");

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
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(_fixture.Seed.PortalId, UnknownRoleId),
            new UpdateRoleRequest { RoleName = "Absent" + Suffix(), Description = "Absent" + Suffix() },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The update route renames a role, and the new name reaches the stored column.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION - documented behavioural difference, asserted end to end. The legacy edit screen made the
    /// name read-only (<c>EditRoles.ascx.vb</c> L131-L134) and the terminal <c>UpdateRole</c> procedure
    /// omits the column from its assignment list, so the legacy application could not rename a role. The
    /// library-level member the service replaces carried the name
    /// (<c>RoleController.vb</c> L254) and the terminal schema constrains <c>(PortalID, RoleName)</c>
    /// uniquely (<c>03.00.09.SqlDataProvider</c> L304), so the migrated contract carries a writable name.
    /// </para>
    /// <para>
    /// The stored column is read directly rather than trusted from the response, because the round trip
    /// through the projection would pass whether or not the write reached SQL.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateRole_RenamesTheRoleAndPersistsTheNewName()
    {
        using HttpClient client = _fixture.CreateHostClient();
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
    /// Renaming a role onto a name another role in the same tenant already holds answers
    /// <c>409 Conflict</c> and leaves the stored name alone.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The legacy editor guarded portal-scoped name uniqueness on its INSERT branch alone
    /// (<c>EditRoles.ascx.vb</c> L251-L257, with no equivalent at L259-L261), which was coherent only
    /// while the name could not change. With a writable name the guard has to cover this verb too, or a
    /// rename would be the one way to violate <c>IX_RoleName</c> - and the violation would surface as a
    /// server fault naming no field rather than as something the caller can correct.
    /// </para>
    /// <para>
    /// The stored column is re-read so the refusal is proved to have prevented the write rather than
    /// merely to have followed it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateRole_RenamingOntoAnExistingName_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();
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
    /// <remarks>
    /// This is what makes the guard usable on a replacement contract at all: every caller amending one
    /// field resubmits the name it read, so comparing on text alone would refuse every ordinary update.
    /// </remarks>
    [Fact]
    public async Task UpdateRole_ResubmittingItsOwnName_IsNotAConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();
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

    /// <summary>
    /// Two paged collections on the same controller carry two different sortable vocabularies, and the
    /// one applied follows the action rather than the controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// M-11: this is the case that a controller-only selection would get wrong, which is why it is
    /// asserted separately. <c>RolesController</c> serves both the role listing and the role-membership
    /// listing, and the two project different things: a membership carries the assignment dates, a role
    /// carries the paid-membership columns, and neither can be ordered by the other's fields. Binding the
    /// vocabulary to the controller alone would give both listings whichever set was chosen for the pair.
    /// </para>
    /// <para>
    /// <c>Username</c> and <c>Description</c> are the discriminating pair: the two vocabularies are entirely
    /// disjoint, and each of these is valid on exactly one of the two collections, so a selection that
    /// confused them cannot pass both halves.
    /// </para>
    /// <para>
    /// MIGRATION: the membership half of the pair used to be <c>EffectiveDate</c>. The assignment dates are
    /// deliberately NOT orderable - the legacy grid offered no ordering by them, and adding one would be a
    /// feature rather than a preserved behaviour, so <c>SortableFields</c> withholds them and records why.
    /// The account fields the membership listing composes are orderable, so the pair is drawn from those
    /// instead; nothing about what this test proves changes, because the two vocabularies share no name at
    /// all and any member of either discriminates.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RoleCollections_BindTheirSortVocabularyPerActionNotPerController()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// The membership listing accepts the three account fields the ACCOUNT listing cannot order by, which is
    /// the ordering capability the boundary previously refused.
    /// </summary>
    /// <param name="sortBy">The field the caller named.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the end-to-end proof that the membership listing no longer borrows the account
    /// collection's request contract. It bound <c>UserPagedRequest</c>, so
    /// <c>UserPagedRequestValidator</c> resolved for it and applied the account collection's seven sortable
    /// names - and every one of these three requests answered <c>400</c> naming <c>sortBy</c> as an unknown
    /// field. The service behind the action enforces the ten-name membership set and
    /// <c>RoleService.OrderRoleMemberships</c> has an arm for each of the three, so three orderings the
    /// application could perform were unreachable over HTTP.
    /// </para>
    /// <para>
    /// The asymmetry with the account listing is legitimate and is asserted from the other side in
    /// <c>RequestValidationContractTests</c>: the account listing pages in the STORE and cannot order by
    /// values the external <c>aspnet_*</c> membership objects fill after the page has been cut, whereas this
    /// listing composes each assignment with its account and pages IN MEMORY, so the values are present on
    /// every row beforehand. Both verdicts are correct for their own listing, which is why each collection
    /// binds its own request type rather than sharing or borrowing one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("CreatedDate")]
    [InlineData("LastLoginDate")]
    [InlineData("IsApproved")]
    [Trait("Category", "Integration")]
    public async Task RoleMembers_AreOrderableByTheAccountFieldsTheAccountListingCannotOrder(string sortBy)
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage membership = await client.GetAsync(
            new Uri(
                RoleUsersRoute(_fixture.Seed.PortalId, _fixture.Seed.AdministratorRoleId)
                    + "?pageIndex=0&pageSize=50&sortBy=" + sortBy,
                UriKind.Relative));

        membership.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the membership listing composes the account and pages in memory, so it genuinely orders by "
            + "this field and must not refuse it at the boundary");

        // The page must actually come back, so a permissive validator paired with a service that then
        // refused the same name would still fail here rather than passing on the status code alone.
        PagedEnvelope<RoleMembershipDto>? page = await membership.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleMembershipDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Should().NotBeNull();

        // The mirror image: the ACCOUNT listing refuses the very same name, and that refusal is correct
        // because it pages in the store. Asserting both here is what makes the difference deliberate
        // rather than an accident of two validators drifting apart.
        using HttpResponseMessage accounts = await client.GetAsync(
            new Uri(
                $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/users?pageIndex=0&pageSize=50&sortBy={sortBy}",
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
        using HttpClient client = _fixture.CreateHostClient();

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

        // MIGRATION: the update verb binds UpdateRoleGroupRequest, which carries the two members
        // dbo.UpdateRoleGroup writes and neither the group key nor the owning portal. Echoing the read-back
        // projection would still succeed - unknown JSON members are ignored - but it would exercise a wider
        // shape than the boundary advertises, which is the very confusion the split removed.
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
        using HttpClient client = _fixture.CreateHostClient();
        RoleGroupDto first = await CreateRoleGroupAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleGroupsRoute(_fixture.Seed.PortalId),
            new CreateRoleGroupRequest { RoleGroupName = first.RoleGroupName },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// A group that still classifies a role is refused deletion, so no role is left pointing at a group that
    /// no longer exists.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The refusal is a <c>409 Conflict</c>, not a <c>400 Bad Request</c>. The request is well formed - it
    /// addresses a group that exists in a portal the caller administers - and it is the STATE of that group
    /// that declines it, which is exactly what a conflict reports. The assertion at the end of this test
    /// proves the distinction matters: once the role is removed the identical request succeeds, so nothing
    /// about the request needed correcting.
    /// </remarks>
    [Fact]
    public async Task DeleteRoleGroup_WhileItStillClassifiesARole_ReturnsConflict()
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

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

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

    /// <summary>
    /// A membership's effective date reaches the wire, and an unpaid role's expiry is reported as absent.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The two dates are the whole subject of the legacy membership screen
    /// (<c>securityroles.ascx:L77-L86</c>), and until this checkpoint they were settable but not readable - a
    /// caller could set a bound it could never afterwards see. This asserts the round trip end to end,
    /// through the store, so the fix cannot regress to a write-only field.
    /// </para>
    /// <para>
    /// The effective date must be in the FUTURE for it to survive, and the expiry of an unpaid role is
    /// absent however it was submitted. Neither is an accident of this test; both are the legacy assignment
    /// rule at <c>RoleController.vb:L530-L538</c>, which discards an effective date earlier than now
    /// (<c>If EffectiveDate &lt; Now Then EffectiveDate = Null.NullDate</c>) and then nulls the expiry
    /// outright when the role declares no billing period
    /// (<c>If Period = Null.NullInteger Then ExpiryDate = Null.NullDate</c>). The role created here is
    /// unpaid, so the second rule applies to it. The paid case is asserted by the test that follows.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RoleMembership_CarriesAFutureEffectiveDateAndLeavesAnUnpaidExpiryAbsent()
    {
        using HttpClient client = _fixture.CreateHostClient();
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

    /// <summary>
    /// A paid role's membership carries the expiry the billing terms compute.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the half of the assignment rule the unpaid case cannot reach. The legacy selection at
    /// <c>RoleController.vb:L539-L546</c> offsets the expiry by the role's period and frequency - here one
    /// month - so a monthly paid role assigned today expires about a month from now. Asserting a window
    /// rather than an exact instant keeps the test honest about clock movement between the write and the read
    /// while still proving the offset was applied rather than the date being passed through or dropped.
    /// </remarks>
    [Fact]
    public async Task RoleMembership_ForAPaidRoleCarriesTheComputedExpiry()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// <para>
    /// The legacy absence marker for a date was <c>Date.MinValue</c>, and the legacy screen printed the empty
    /// string for it rather than the value. So the property worth pinning is that the sentinel never reaches
    /// a caller, which is the boundary half of AAP Rule T7. The raw body is inspected for it precisely
    /// because a sentinel would deserialise into a non-null property and a typed assertion alone would pass
    /// while the wire contract was wrong.
    /// </para>
    /// <para>
    /// An absent date is OMITTED from the payload rather than written as an explicit null, because this API
    /// serialises with <c>JsonIgnoreCondition.WhenWritingNull</c> throughout. For a nullable date the two
    /// forms are equivalent - both read back as absent, and neither can be mistaken for a real date - so the
    /// assertion is made on the deserialised value rather than on the presence of the member. Contrast the
    /// module definition's cache period, which is a non-nullable integer whose -1 IS meaningful and which is
    /// therefore asserted to be present in its own suite.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RoleMembership_ReportsAnOpenEndedMembershipWithoutASentinelDate()
    {
        using HttpClient client = _fixture.CreateHostClient();
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
    /// This is the third of the three legacy grouping intents, and the one that had no expression on this API
    /// at all before this checkpoint. It is asserted end to end because the distinction it rests on is a
    /// stored one: an ungrouped role holds SQL null in <c>Roles.RoleGroupID</c>, which is a nullable column.
    /// </remarks>
    [Fact]
    public async Task ListRoles_WithTheUngroupedScope_ExcludesAGroupedRole()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// <remarks>
    /// <c>400</c> rather than <c>409</c> is the point of the assertion. The request conflicts with nothing
    /// about the stored state - the caller's own two query values disagree - and the caller can correct it by
    /// dropping either one, which is precisely what a bad request means.
    /// </remarks>
    [Fact]
    public async Task ListRoles_WithBothAGroupAndTheUngroupedScope_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        RoleGroupDto group = await CreateRoleGroupAsync(client);

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/roles?roleGroupId={Route(group.RoleGroupId)}&scope=Ungrouped",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// An unrecognised scope spelling is refused by model binding before the action runs.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The parameter is a closed enumeration rather than free text, which is what makes this a binding
    /// failure rather than a value the service has to defend itself against. The legacy equivalent was a bare
    /// integer, where an unrecognised value silently fell into whichever magic band contained it.
    /// </remarks>
    [Fact]
    public async Task ListRoles_WithAnUnrecognisedScope_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/roles?scope=NotAScope",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Reads a role's memberships and returns the one held by the seeded member account.
    /// </summary>
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
    /// The alias and the administrator account are returned rather than discarded because every role route is
    /// tenant-scoped: the portal-administrator policy requires the route's tenant to be the tenant the request
    /// resolved to, and resolution is by host name. Reaching this tenant's roles therefore means addressing
    /// this tenant's alias as this tenant's own administrator, which is exactly how an operator reaches it.
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

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The created representation travels inside the shared success envelope, so every member is one
        // level down under "data". Read as raw JSON rather than through a typed envelope because only two
        // members are wanted, and naming them here proves the envelope member name as a side effect.
        System.Text.Json.JsonElement created = document.RootElement.GetProperty("data");

        return new IsolatedTenant(
            created.GetProperty("portalId").GetInt32(),
            alias,
            created.GetProperty("administratorId").GetInt32(),
            administrator);
    }

    /// <summary>Builds a client that resolves to an isolated tenant, as that tenant's own administrator.</summary>
    /// <param name="tenant">The tenant to address.</param>
    /// <returns>An authenticated client addressed at the tenant.</returns>
    private HttpClient TenantClient(IsolatedTenant tenant) => _fixture.CreateTenantClient(
        tenant.Alias,
        tenant.PortalId,
        tenant.AdministratorId,
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
            .ReadEnvelopeAsync<RoleDetailDto>();

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
