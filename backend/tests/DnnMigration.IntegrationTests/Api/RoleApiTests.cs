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

    // MIGRATION - WHY NO MEDIA TYPE IS ASSERTED ANYWHERE IN THIS SUITE, stated from a measurement rather
    // than assumed. RFC 7807 nominates application/problem+json, and it would be natural to pin it here.
    // The framework answers application/json instead for a CONTROLLER-produced problem document, because
    // the class-level [Produces("application/json")] declaration constrains content negotiation for every
    // response the action can produce, the error ones included. Measured directly: every refusal on these
    // resources carries application/json. Neither value may be asserted. Pinning RFC 7807's value would
    // fail on a correct build, and pinning the value the framework currently emits would CEMENT the
    // deviation and make correcting it later look like a regression. The deviation is recorded in the
    // repository migration notes, and the sibling problem-details and portal suites take the same position
    // for the same reason. What this suite asserts instead is the substance: the document's own members -
    // type, title, status and the per-field errors object - because those are what a client reads, and a
    // response that regressed to a bare status, a raw string or a differently shaped object would fail on
    // them whatever media type it claimed.

    /// <summary>The prefix a failure code is built into as the problem document's <c>type</c>.</summary>
    /// <remarks>
    /// The whole value is what a client branches on, so the tests that care about WHICH refusal occurred
    /// assert the type rather than the status: several distinct reasons share one status code, and a
    /// bare status cannot tell a duplicate name apart from a group that still classifies a role.
    /// </remarks>
    private const string ProblemTypePrefix = "urn:dnnmigration:error:";

    // =============================================================================================
    // THE FIVE VALIDATION MESSAGES, VERBATIM. Every string below is the wording declared by
    // Website/admin/Security/editroles.ascx, reproduced character for character. They are restated
    // here rather than referenced because the parity obligation is that the WORDING an operator reads
    // is unchanged, and a test that read the constant the production code applies would pass however
    // that constant were reworded. Do not "correct" any of them: two are defective on purpose, and
    // the defect is preserved deliberately (see the two notes below).
    //
    // MIGRATION: the leading markup tag is STRIPPED. Every legacy ErrorMessage began with a literal
    // "<br>" - for example ErrorMessage="<br>You Must Enter a Valid Name" at editroles.ascx L31 -
    // because the text was written straight into the page's markup and needed a line break ahead of
    // it. In a machine-readable problem document an HTML tag is neither markup nor data, so the tag
    // goes and the wording after it stays. Note also that this screen uses the non-self-closing
    // "<br>" spelling throughout, in contrast to Website/admin/Users/User.ascx.vb L187 which uses
    // "<br/>"; neither spelling survives, so the inconsistency is moot rather than reproduced.
    // =============================================================================================

    /// <summary>valRoleName's wording (<c>editroles.ascx</c> L31), the screen's one presence check.</summary>
    private const string RoleNameRequiredMessage = "You Must Enter a Valid Name";

    /// <summary>valServiceFee2's wording (<c>editroles.ascx</c> L95), whose operator agrees with it.</summary>
    private const string ServiceFeeNegativeMessage = "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>valBillingPeriod2's wording (<c>editroles.ascx</c> L113).</summary>
    /// <remarks>
    /// MIGRATION - DISCOVERED LEGACY DEFECT, PRESERVED RATHER THAN REPAIRED. This message says "or Equal
    /// to" while the validator beside it declares <c>Operator="GreaterThan" ValueToCompare="0"</c>
    /// (<c>editroles.ascx</c> L114). The two disagree, and the OPERATOR is the real rule: a submitted zero
    /// is refused. The migration discipline for a discovered defect is to annotate it, not to fix it -
    /// rewording the text would change what an operator reads, and relaxing the operator would accept a
    /// billing cycle of zero units, which could never advance an expiry date. The boundary theory below
    /// pins the operator; this constant pins the wording.
    /// </remarks>
    private const string BillingPeriodNotPositiveMessage =
        "Billing Period Must Be Greater Than or Equal to Zero";

    /// <summary>valTrialFee2's wording (<c>editroles.ascx</c> L127).</summary>
    /// <remarks>
    /// MIGRATION - THE SAME DEFECT IN THE OPPOSITE DIRECTION, treated identically. This message says
    /// "Greater Than Zero" while the validator declares <c>Operator="GreaterThanEqual"
    /// ValueToCompare="0"</c> (<c>editroles.ascx</c> L128), so zero IS accepted - a free trial is a real
    /// configuration. Tightening the rule to match the text would refuse every free trial the legacy
    /// screen allowed, which is why the accepted-boundary theory below asserts that a trial fee of zero
    /// is created rather than refused.
    /// </remarks>
    private const string TrialFeeNegativeMessage = "Trial Fee Must Be Greater Than Zero";

    /// <summary>valTrialPeriod2's wording (<c>editroles.ascx</c> L145), where text and operator agree.</summary>
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

    /// <summary>
    /// The membership write path is validated at the BOUNDARY on its single canonical address.
    /// </summary>
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
    /// The filter resolves a validator from the bound argument's type rather than from route metadata. The
    /// legacy wording is asserted in the body, because the message is the part of the contract a caller
    /// actually reads.
    /// </para>
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
    /// <para>
    /// <c>/api/v1/roles</c> is the address the contract froze, and every action is reachable through it:
    /// read the collection, create, read the member, update it and remove it. The returned data and database
    /// assertions establish that the action used the RESOLVED tenant rather than a default.
    /// </para>
    /// <para>
    /// The created role is removed at the end so this fact does not accumulate rows for later facts.
    /// </para>
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
    /// <remarks>
    /// <c>/api/v1/roles/{roleId}/users</c> is named explicitly by the contract, so it gets a fact of its
    /// own rather than being covered incidentally. The account-side projection
    /// <c>/api/v1/users/{userId}/roles</c> is asserted alongside it because the two are the same relation
    /// read from either end, exactly as the legacy assignment screen offered it.
    /// </remarks>
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

        // The UPDATE CONTRACT is submitted, not the response projection that was just read back. The two are
        // deliberately different shapes - the projection carries the group key and the owning portal, neither
        // of which dbo.UpdateRoleGroup writes - and the API now refuses a body carrying a member no contract
        // declares rather than discarding it silently, so echoing the projection back is a 400.
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
    /// The tenant a request runs under is resolved from its host header. The flat role family therefore has no
    /// portal segment a caller can use to override that context; a token for one portal sent to another
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
    /// The role-group collection of another tenant is refused as well, because the reconciliation belongs to
    /// the policy rather than to one controller.
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
    /// tenant-administration policy is anchored to the resolved request tenant. An unclaimed host is refused
    /// identically.
    /// </summary>
    /// <remarks>
    /// Both controllers declare the policy once at class level and every route is flat, so the host-resolved
    /// tenant is the only tenant identity available to the operation. Before the policy enforced that binding,
    /// an administrator of any portal could read and rewrite another tenant's roles, role groups and role
    /// memberships - a path to arbitrary privilege in a tenant the caller had nothing to do with.
    /// </remarks>
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
    /// another tenant and a host bound to none: a caller with no reach into a tenant must not be able to use
    /// the difference between 403 and 404 to enumerate which tenants exist.
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
    /// A role that exists in another tenant answers <c>404 Not Found</c> when addressed through this one, so a
    /// role identifier alone grants no reach across tenants.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The other tenant is addressed as its OWN administrator, which is what makes this test about service-level
    /// tenant scoping rather than about authorisation. A caller that legitimately administers the other tenant
    /// still cannot see the seeded tenant's role through it, because the read is anchored to the resolved host.
    /// The authorisation half - a caller reaching across into a tenant it does not administer - is asserted
    /// separately by <see cref="ListRoles_ForATenantOtherThanTheResolvedOne_ReturnsForbidden"/>; keeping the two
    /// apart matters, because a 403 from authorisation would satisfy neither assertion on its own.
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
    /// A role created with automatic assignment enrols the tenant's existing accounts, which is the behaviour
    /// that makes the flag meaningful rather than merely stored.
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

        // The failure code is asserted, not merely the status. Several distinct refusals answer 409 on
        // these resources - a duplicate role name, a duplicate group name and a group that still
        // classifies a role - so a client that branched on the status alone could not tell them apart.
        // The code travels as the problem document's type, which is what makes the branch possible.
        await ShouldCarryFailureCodeAsync(response, HttpStatusCode.Conflict, "role.name_duplicate");

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        RoleDetailDto held = await CreateRoleAsync(client);
        IsolatedTenant other = await CreateIsolatedPortalAsync(client);

        // Each tenant is addressed by its own caller, because every role route is tenant-scoped. That is not
        // incidental to this test: the whole claim being made is that the same NAME is free in one tenant and
        // taken in another, and a single caller able to write to both tenants would be the very cross-tenant
        // reach the review flagged.
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
        // unreachable through the seeded host. That is the stronger of the two checks, and it is why the
        // detail contract does not echo the owning portal back.
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

        // The wording is valRoleName's own, from Website/admin/Security/editroles.ascx L31, with only
        // the leading markup tag removed. Do not reword it: the string is the parity assertion. The
        // document is read as a document rather than as text, so the member the caller must correct is
        // named and the wording is compared exactly - a substring match over the raw body could not tell
        // the migrated text apart from the legacy text with its markup tag still attached.
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

    /// <summary>A billing period of zero is rejected, because a period must be a positive count.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRole_WithZeroBillingPeriod_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateRoleRequest request = NewRoleRequest();
        request.BillingPeriod = 0;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RolesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        // valBillingPeriod2 (editroles.ascx L113-L114) declares Operator="GreaterThan" against 0 while
        // its ErrorMessage says "or Equal to". The operator is the behaviour and a zero is refused; the
        // wording is carried across unchanged because a legacy defect is annotated, not repaired. Do
        // not "fix" this string to agree with the rule.
        await ShouldReportFieldAsync(
            response,
            nameof(CreateRoleRequest.BillingPeriod),
            BillingPeriodNotPositiveMessage);
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    /// <remarks>
    /// This is what makes the guard usable on a replacement contract at all: every caller amending one
    /// field resubmits the name it read, so comparing on text alone would refuse every ordinary update.
    /// </remarks>
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
    /// An assignment round-trips: it answers <c>204 No Content</c>, appears on both the role's account list and
    /// the account's role list, and is removed again with <c>204 No Content</c>.
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
    /// second one, so the operation is idempotent in the only sense that matters - the account holds the role
    /// exactly once.
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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

        // MIGRATION: the legacy screen answered a refusal with
        // Response.Redirect(NavigateURL("Access Denied"), True) - a redirect to an HTML page no
        // programmatic caller can interpret, which also conflated "you did not say who you are" with
        // "you may not do this". The target answers a plain 403 carrying a machine-readable code, and
        // the code is what separates a protected assignment from a caller who does not administer the
        // tenant: both answer 403, and only the type tells them apart.
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
    /// No account may be removed from the registered-users role, which is the membership that makes an account
    /// a member of the tenant at all.
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
    /// Two paged collections on the same controller carry two different sortable vocabularies, and the
    /// one applied follows the action rather than the controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A controller-only selection would get this case wrong, which is why it is asserted separately.
    /// <c>RolesController</c> serves both the role listing and the role-membership
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    /// <remarks>
    /// <para>
    /// The two dates are the whole subject of the legacy membership screen
    /// (<c>securityroles.ascx:L77-L86</c>), so a caller must be able to read back a bound it set. This
    /// asserts the round trip end to end, through the store, so neither date can regress to a write-only
    /// field.
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
    /// <para>
    /// The legacy absence marker for a date was <c>Date.MinValue</c>, and the legacy screen printed the empty
    /// string for it rather than the value. So the property worth pinning is that the sentinel never reaches
    /// a caller, which is the boundary half of AAP Rule T7. The raw body is inspected for it precisely
    /// because a sentinel would deserialise into a non-null property and a typed assertion alone would pass
    /// while the wire contract was wrong.
    /// </para>
    /// <para>
    /// An absent date is written as an explicit null rather than omitted, because this API serialises with
    /// <c>JsonIgnoreCondition.Never</c> throughout - measured on a live response, which returns
    /// <c>"effectiveDate":null,"expiryDate":null</c> for an open-ended membership. For a nullable date the
    /// written null and a missing member would read back the same way, and neither can be mistaken for a
    /// real date, so the assertion is deliberately made on the deserialised value rather than on the
    /// presence of the member: that keeps this test pinning the property that matters - no sentinel reaches
    /// the caller - and leaves it insensitive to which of the two absence forms the host is configured for.
    /// Contrast the
    /// module definition's cache period, which is a non-nullable integer whose -1 IS meaningful and which is
    /// therefore asserted to be present in its own suite.
    /// </para>
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
    /// <remarks>
    /// <c>400</c> rather than <c>409</c> is the point of the assertion. The request conflicts with nothing
    /// about the stored state - the caller's own two query values disagree - and the caller can correct it by
    /// dropping either one, which is precisely what a bad request means.
    /// </remarks>
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/roles?scope=NotAScope",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =============================================================================================
    // THE FEE-VERSUS-PERIOD ASYMMETRY. This is the sharpest parity obligation on these resources and
    // the one a plausible-looking validator gets wrong: the two FEES admit zero and the two PERIODS
    // do not. Measured verbatim in Website/admin/Security/editroles.ascx:
    //   valServiceFee2   L96   Operator="GreaterThanEqual" ValueToCompare="0"   -> zero ACCEPTED
    //   valBillingPeriod2 L114 Operator="GreaterThan"      ValueToCompare="0"   -> zero REFUSED
    //   valTrialFee2     L128  Operator="GreaterThanEqual" ValueToCompare="0"   -> zero ACCEPTED
    //   valTrialPeriod2  L146  Operator="GreaterThan"      ValueToCompare="0"   -> zero REFUSED
    // A validator that applied ">= 0" to all four would pass every negative-value test in this suite
    // and would still be wrong, because it would accept a billing cycle of zero units. A validator
    // that applied "> 0" to all four would be wrong in the other direction, because it would refuse
    // every free role and every free trial the legacy screen allowed. Both boundaries are therefore
    // asserted for all four members: four accepted cases and eight refused ones.
    // =============================================================================================

    /// <summary>
    /// A fee of exactly zero is accepted on both fee members, which is the <c>GreaterThanEqual</c>
    /// boundary, and the value survives as zero rather than being erased.
    /// </summary>
    /// <param name="onServiceFee">
    /// <see langword="true"/> to exercise the service fee, <see langword="false"/> for the trial fee.
    /// </param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The trial-fee half of this theory is the case the wording actively argues against: valTrialFee2's
    /// message reads "Trial Fee Must Be Greater Than Zero" while its operator admits zero. The operator
    /// wins, so a free trial is created - and that is why the defect is annotated rather than repaired.
    /// </para>
    /// <para>
    /// The second assertion is the sentinel half of the same fact. Serialisation is configured with the
    /// <c>Never</c> ignore condition, so a zero is written as a zero and an absent fee is written as an
    /// explicit null; the two are different states and a caller must be able to tell them apart. A role
    /// with NO charge is not a role charged NOTHING, which is exactly the distinction the legacy encoding
    /// could not express - Null.vb declares NullSingle as Single.MinValue (L51), so an unset fee arrived
    /// as a huge negative magnitude indistinguishable from a real one.
    /// </para>
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
    /// A negative fee is refused on both fee members, naming the offending member and carrying the
    /// legacy wording, and nothing reaches the store.
    /// </summary>
    /// <param name="onServiceFee">
    /// <see langword="true"/> to exercise the service fee, <see langword="false"/> for the trial fee.
    /// </param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION - THE SAME FIELD HAS TWO LEGACY BEHAVIOURS, and this test pins which one this resource
    /// implements. The role editor REFUSED a negative fee outright (valServiceFee2 at
    /// <c>editroles.ascx</c> L96, valTrialFee2 at L128), while the portal-creation path SILENTLY CLAMPED
    /// one up to zero - <c>Library/Components/Portal/PortalController.vb</c> L395 and L398 read
    /// <c>CType(IIf(serviceFee &lt; 0, 0, serviceFee), Single)</c>. Both survive in the target, on the
    /// paths that owned them: the write contracts here refuse, and the clamp lives in
    /// <c>Application/Mapping/RoleMappings.cs</c> where the template-driven path reaches it. The final
    /// assertion below is what makes the choice observable rather than merely stated - a clamping write
    /// path would have created a role priced at zero, so proving no row exists proves the refusal was a
    /// refusal. (The legacy <c>IIf</c> is a FUNCTION and evaluates both arms, so it is not a
    /// short-circuiting conditional; the arms here are side-effect-free, which is the only reason the
    /// two are equivalent.)
    /// </remarks>
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
    /// The zero cases are the load-bearing ones. Zero is precisely where a "greater than or equal to"
    /// rule and a "greater than" rule disagree, so a validator that had copied the fee rule onto the
    /// periods would accept these two submissions and every other assertion in this suite would still
    /// pass. The billing half additionally carries the defective wording described on
    /// <see cref="BillingPeriodNotPositiveMessage"/>: the message promises to admit zero and the
    /// operator refuses it, and the operator is the rule.
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
        }
        else
        {
            request.TrialPeriod = period;
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
    /// <remarks>
    /// The counterpart of the refusal above, and necessary rather than decorative: a rule mistakenly
    /// written as "greater than one" would refuse this submission while passing every refusal assertion
    /// in this suite. The measured legacy default for both periods was one
    /// (<c>EditRoles.ascx.vb</c> L212-L214 and L222-L224 substitute a period of one alongside the never
    /// code when a block is left blank), so this is the value the legacy screen wrote most often.
    /// </remarks>
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
    /// A fee far above the baseline column's 999.99 limit is accepted and round-trips exactly, because
    /// the terminal column is <c>money</c> and no legacy validator bounded either fee above.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION - A CEILING THAT NO LONGER APPLIES, REFUTED BY MEASUREMENT RATHER THAN ASSUMED AWAY.
    /// The baseline schema declares <c>[ServiceFee] [decimal](5, 2)</c>
    /// (<c>01.00.00.SqlDataProvider</c> L119), which caps a fee at 999.99, and it would be easy to carry
    /// that cap forward as a validation rule. Only the TERMINAL schema state is meaningful, and the
    /// destructive upgrade chain retypes the column: <c>01.00.04</c> L1326 and <c>01.00.05</c> L2752
    /// rebuild it as <c>money</c> while converting existing values, and <c>03.01.01</c> L1173 settles it
    /// with <c>ALTER COLUMN [ServiceFee] [money] NULL</c>, adding a zero default at L1177. TrialFee was
    /// <c>money</c> from birth (<c>01.00.08</c> L6830). Enforcing 999.99 would refuse a fee an
    /// installation has been able to charge for many versions.
    /// </para>
    /// <para>
    /// The <c>MaxLength="50"</c> attribute on the fee text boxes (<c>editroles.ascx</c> L89 and L122) is
    /// likewise not a ceiling: it bounded how many CHARACTERS could be typed into a text box, which is a
    /// text-entry width and is meaningless for a decimal member.
    /// </para>
    /// <para>
    /// The value is read back out of the column itself as well as off the wire, because a truncating or
    /// rounding write would leave the response correct and the stored row wrong.
    /// </para>
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
    /// database client and came back as a 500 naming no field, which tells a caller nothing it can act
    /// on. The bound is taken from the shared storage-range constants rather than restated as a literal,
    /// so the test cannot drift away from the rule it is asserting.
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
    /// <para>
    /// MIGRATION - THE SENTINEL COLLISION, ASSERTED RATHER THAN DESCRIBED. Rule T7 keeps sentinels at the
    /// boundary and out of the domain, and the reason it matters here is arithmetic: the legacy
    /// absent-integer marker is -1 (<c>Library/Components/Shared/Null.vb</c> L41-L45) while
    /// <c>Roles.RoleID</c> is seeded at zero
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> L115), so the
    /// FIRST role an installation ever creates is numbered 0 - the shipped Administrators role holds
    /// exactly that value at L7192, and the shipped portal row points at it through
    /// <c>AdministratorRoleId = 0</c>, a genuine foreign key to a genuine row.
    /// </para>
    /// <para>
    /// Zero is consequently NOT absence, and the failure this test exists to catch is the one that looks
    /// harmless: a route constraint with a lower bound, a guard written as
    /// <c>if (roleId &lt;= 0) return NotFound()</c>, or a client-side falsiness test would each make the
    /// tenant's own administrators role unreachable while every other assertion in this suite kept
    /// passing. The identifier is also read off the RAW body, because a typed round trip through the same
    /// serialiser would pass whether or not the member survived.
    /// </para>
    /// <para>
    /// The identity seed is asserted first rather than taken on trust. Each run provisions a freshly
    /// named database and seeds three roles into it, so the administrators role genuinely occupies the
    /// seed value; if that ever stopped being true this test would still be correct but would no longer
    /// be exercising zero, and the assertion says so.
    /// </para>
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
    /// <para>
    /// MIGRATION: the legacy layer had one value standing for three things. The editor's group drop-down
    /// offered "Global Roles" as -1 and that was a real, selectable choice
    /// (<c>EditRoles.ascx.vb</c> L75); the portal-creation path assigned the same number as the "no
    /// group" marker - <c>Library/Components/Portal/PortalController.vb</c> L392 reads
    /// <c>objRoleInfo.RoleGroupID = Null.NullInteger</c>; and -1 is simultaneously the generic
    /// absent-integer sentinel. Here absence is <c>null</c> and nothing else.
    /// </para>
    /// <para>
    /// The marker never reached the column even in the legacy application, and the schema is what
    /// guarantees it: <c>FK_Roles_RoleGroups</c> constrains the column to a real group row
    /// (<c>03.02.03.SqlDataProvider</c> L37, re-added at <c>04.00.04</c> L70) while
    /// <c>RoleGroups.RoleGroupID</c> is <c>IDENTITY (0, 1)</c>, so no group can bear -1 and the marker
    /// was converted to a database null on the way down. This API converts it to a refusal instead,
    /// which is strictly more informative and is asserted below - the alternative, quietly treating -1
    /// as "no group", would let a caller store one meaning and read back another.
    /// </para>
    /// <para>
    /// The absent case is read off the raw body deliberately. Serialisation is configured with the
    /// <c>Never</c> ignore condition, so an absent group is written as <c>"roleGroupId":null</c> rather
    /// than being dropped from the document; a client can therefore tell "no group" from "the server did
    /// not tell me", which an omitted member cannot express.
    /// </para>
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
    /// A role really does travel with the group it was created in, whatever that group's identifier, so
    /// a group key of zero is carried rather than treated as absence.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The companion of the assertion above. <c>RoleGroups.RoleGroupID</c> is seeded at zero as well, so
    /// the group reference has the same collision the role key has and neither the request contract nor
    /// the validator may bound it - the validator records exactly that, and this test is what would fail
    /// if a bound were ever added. The identifier is compared to the one the group resource issued rather
    /// than to a literal, because which number the store assigns is the store's business.
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
    /// <para>
    /// The envelope is the contract: <c>items</c> beside <c>meta</c>, with the total across every page in
    /// the companion rather than as a sibling of the records. The total is asserted to be a real count
    /// and specifically NOT -1, because the legacy paging idiom passed the total back through a
    /// by-reference argument and used the absent-integer marker for "not counted"; an envelope that
    /// published that marker would give a client a page count of minus one page.
    /// </para>
    /// <para>
    /// The page index is read back rather than assumed. The base is zero here - index 0 is the first
    /// page - and the assertion is written against what the caller sent rather than against a constant,
    /// so it states that the coordinates are echoed without restating the base in a second place.
    /// </para>
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
    /// <para>
    /// The total is the point of the second half. A filtered listing that narrowed its records while
    /// reporting the unfiltered total would drive a client's pager to offer pages that do not exist, and
    /// an empty result must report zero rather than the absent-integer marker.
    /// </para>
    /// <para>
    /// THE MATCH IS A SUBSTRING MATCH, NOT A PREFIX MATCH, and the mid-string case is asserted explicitly
    /// so that a later change of semantics fails here rather than silently changing the contract. The role
    /// listing matches a fragment ANYWHERE in the name - <c>Application/Services/RoleService.cs</c> applies
    /// <c>RoleName.Contains(wanted, StringComparison.OrdinalIgnoreCase)</c> - which differs from the
    /// account listing, where <c>Infrastructure/Repositories/UserRepository.cs</c> applies
    /// <c>StartsWith</c>. Neither semantics violates a legacy behaviour, because the legacy role screen
    /// declared no search control at all (<c>Website/admin/Security/roles.ascx</c> contains no filter
    /// input); the search is net-new, and this test pins the semantics it actually has.
    /// </para>
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
    /// A caller-supplied correlation identifier comes back exactly once on a success and is still
    /// present on a deliberately failed request; one is minted when the caller supplies none; and an
    /// unusable value is replaced rather than echoed, without becoming a refusal of its own.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The failure half is the half that matters. A correlation identifier exists so that the one
    /// exchange a caller wants to report - the one that went wrong - can be found in a log, so a header
    /// attached only to successful responses would be present exactly when it is not needed. The problem
    /// document's own detail text points the caller at this header, which would be a dead reference if
    /// the header were absent.
    /// </para>
    /// <para>
    /// The single-value assertion is not pedantry either: a middleware that appended rather than assigned
    /// would produce two values on a request that already carried one, and a client reading the first
    /// would silently disagree with a log written from the second.
    /// </para>
    /// <para>
    /// The oversize value exercises the sanitiser. The header is bounded at 128 characters and confined
    /// to printable ASCII, because a value copied into a log line is a header-injection vector; an
    /// unusable value is therefore REPLACED with a minted one rather than echoed, and - equally
    /// important - it does not turn the request into a 400. Rejecting the exchange over a diagnostic
    /// header would let a caller break its own request with a value that has no bearing on what it asked
    /// for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RoleRequests_RoundTripTheCorrelationIdentifierIncludingOnFailure()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string supplied = "role-suite-" + Suffix();

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

        string onFailure = "role-suite-failure-" + Suffix();

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

    /// <summary>
    /// The role resource publishes no address for the operations that belong elsewhere or nowhere.
    /// </summary>
    /// <param name="path">The address that must not resolve to a role operation.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// An absent endpoint is normally invisible to a test suite, which is exactly why it drifts back in.
    /// Each address below was a candidate this resource deliberately does not serve, and each is refused
    /// with a status that means "no such thing here" rather than answered.
    /// </para>
    /// <para>
    /// Role GROUP management is the first pair: a group's rules are rules about the roles it classifies,
    /// so the same application contract serves both, but the ADDRESSES are separate and the group ones
    /// live under the kebab-cased <c>role-groups</c> collection. The remaining addresses are the
    /// subscription and housekeeping surfaces: the legacy member-services screen subscribed and
    /// unsubscribed an account through the very same assignment operation the two membership actions
    /// already expose, so it earns no address of its own; a role's invitation code is stored and returned
    /// but redeeming it is not an operation here; there is no billing-transaction action, because no
    /// billing subsystem is in scope; and there is neither a cache-invalidation action nor a bulk action,
    /// because invalidation belongs to the service that performs a write and a bulk endpoint would be a
    /// second, weaker copy of every rule the single-item endpoints enforce.
    /// </para>
    /// <para>
    /// A credentialled administrator issues these probes on purpose. An anonymous caller would be refused
    /// by the policy before routing had anything to say, so the refusal would prove nothing about which
    /// addresses exist.
    /// </para>
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
    [InlineData("/api/v1/users/1/services")]
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
    /// <para>
    /// MIGRATION: the successor of <c>Library/Components/Security/Roles/RoleController.vb</c> L495-L496,
    /// where cancelling a used paid trial reads
    /// <c>userRole.ExpiryDate = DateAdd(DateInterval.Day, -1, Date.Today())</c> and updates the row
    /// instead of deleting it. Two facts are preserved and both are asserted. The row SURVIVES, because
    /// the trial-used flag lives on it and losing the row would let a cancelled subscriber restart a paid
    /// trial. And "expire now" is implemented as YESTERDAY rather than as the current instant, with the
    /// time component truncated away, so the membership reads as already expired for the whole of the
    /// current day rather than only after the current hour.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy stamp was <c>Date.Today()</c>, which is server-LOCAL, while the injected
    /// clock is UTC only. For the same real instant the two can name different calendar days either side
    /// of Greenwich. That is accepted deliberately - a local-zone stamp is not comparable between hosts -
    /// and it is why the expected day is captured either side of the call rather than computed once: the
    /// two bounds coincide except across a UTC midnight, where both are correct answers.
    /// </para>
    /// <para>
    /// The trial flag is set with a direct statement because no endpoint writes it: a new membership is
    /// recorded with the flag clear, and the legacy subscription flow that consumed a trial is out of
    /// scope. Only a stored row can therefore put the removal path on this branch.
    /// </para>
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

    /// <summary>
    /// A one-time term derives the far-future perpetual expiry rather than an offset from today.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The sixth frequency code, and the one a four-code reading of the legacy switch loses. The
    /// authority is <c>RoleController.vb</c> L541-L546, whose six arms are <c>N</c> for no expiry,
    /// <c>O</c> for <c>New System.DateTime(9999, 12, 31)</c>, and <c>D</c>, <c>W</c>, <c>M</c> and
    /// <c>Y</c> for a period in days, weeks, months and years. A migration that carried only the four
    /// offset codes would leave <c>O</c> falling through the switch onto the null-date marker set ahead
    /// of it at L538 - that is, onto <c>DateTime.MinValue</c> - and a perpetual membership would read as
    /// one that expired at the beginning of time.
    /// </para>
    /// <para>
    /// The date also reaches the wire unrounded, which the storage bound makes possible: SQL Server's
    /// <c>datetime</c> tops out at 9999-12-31, so the sentinel is storable exactly rather than being
    /// clamped to something near it.
    /// </para>
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
    /// A role whose stored frequency is a character the vocabulary never declared can still be updated,
    /// not merely read.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The write-path companion of the read tolerance asserted elsewhere in this suite, and it is a
    /// distinct guarantee: an update reads the stored row, applies the submitted state and writes it
    /// back, so a materialisation that faulted on the stored character would make such a role
    /// permanently uneditable - the one state from which an operator could not repair it.
    /// </para>
    /// <para>
    /// The characters planted here are the ones the product itself ships:
    /// <c>01.00.00.SqlDataProvider</c> L7192 seeds the Administrators role with <c>'4'</c> and L7194
    /// seeds Registered Users with <c>'0'</c>, and neither is among the six the switch at
    /// <c>RoleController.vb</c> L541-L546 handles. The column is plain <c>char(1) NULL</c> throughout the
    /// upgrade chain with no check constraint, and the legacy editor deliberately tolerated an unknown
    /// code by leaving its drop-down unselected (<c>EditRoles.ascx.vb</c> L149-L151 and L157-L159), so
    /// tolerance on the read and write paths is parity rather than leniency.
    /// </para>
    /// <para>
    /// The submitted frequency is one of the six, because the write CONTRACT is closed over the
    /// enumeration - an undeclared code cannot be sent, only encountered. What is under test is
    /// therefore the transition out of the undeclared state, which is exactly the repair an operator
    /// would attempt.
    /// </para>
    /// </remarks>
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

        using JsonDocument document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The created representation travels inside the shared success envelope, so every member is one
        // level down under "data". Read as raw JSON rather than through a typed envelope because only two
        // members are wanted, and naming them here proves the envelope member name as a side effect.
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
    /// <remarks>
    /// The page size is stated explicitly so that a filtered assertion is never satisfied merely because
    /// the default window happened to exclude the rows that would have contradicted it.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Three things are asserted where a weaker test asserts one. The STATUS says the request was refused.
    /// The DOCUMENT SHAPE says the refusal is an RFC 7807 document rather than a shape of the endpoint's
    /// own - a 400 carrying a bare status, a raw string or an object of its own devising would satisfy
    /// every status assertion in this suite while breaking the single error contract every client is
    /// written against. And the per-member <c>errors</c> entry says WHICH value the caller must correct,
    /// which is the whole reason the document has that member: an error naming nothing leaves a caller
    /// guessing.
    /// </para>
    /// <para>
    /// The message is compared as an exact element rather than by substring, because that is the only
    /// comparison that can tell the migrated wording apart from the legacy wording with its leading
    /// markup tag still attached - and telling those two apart is precisely the parity obligation.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// Separate from the wording-bearing form for the one rule whose message is composed at run time from
    /// the storage bounds rather than carried across from a legacy screen. Restating that text here would
    /// duplicate a computation rather than pin a legacy contract, and would fail the moment the bounds
    /// were formatted differently - so the member is asserted and the wording is left to the rule.
    /// </remarks>
    private static async Task<ValidationProblemDetails> ShouldNameFieldAsync(
        HttpResponseMessage response,
        string member)
    {
        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a value that breaks a declared rule is refused at the boundary, not further in");

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

    /// <summary>
    /// Asserts that a response is a problem document carrying one status and one named failure code.
    /// </summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="status">The status the refusal must carry.</param>
    /// <param name="code">The failure code, without the shared prefix.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The code is a stable, named string rather than a number, and it is what a client branches on. The
    /// status alone is not enough on these resources: three different reasons answer <c>409</c> - a role
    /// name already taken, a group name already taken and a group that still classifies a role - and two
    /// answer <c>403</c>, a protected assignment and a caller who does not administer the tenant. The
    /// document's <c>type</c> is where the difference is legible.
    /// </para>
    /// <para>
    /// The prefix is applied here rather than written into each call site so that the codes read as the
    /// codes the application services actually declare.
    /// </para>
    /// </remarks>
    private static async Task ShouldCarryFailureCodeAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        response.StatusCode.Should().Be(status);

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
