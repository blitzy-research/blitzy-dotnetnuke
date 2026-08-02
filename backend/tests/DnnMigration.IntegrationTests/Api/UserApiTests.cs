using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Dtos.User;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the user resource end to end, across the real HTTP pipeline, the real database and the external
/// credential store.
/// </summary>
/// <remarks>
/// <para>
/// The class name is fixed by validation gate 5, which names this suite and requires the four documented status
/// codes for the user resource - <c>201</c> on a create, <c>200</c> on a read and an update, and <c>204</c> on a
/// delete.
/// </para>
/// <para>
/// This is the only resource in the migration whose writes reach outside the mapped entity model. Credentials
/// live in the ASP.NET membership tables, which the eighty-eight legacy upgrade scripts only ever alter and
/// never create, so they are provisioned by the fixture from an explicit script and reached through
/// parameterised statements rather than through an entity type. Every assertion below that inspects a
/// credential therefore queries those tables directly, and a create that succeeded but wrote no credential row
/// would be caught rather than passing as a success.
/// </para>
/// <para>
/// Three service behaviours shape the tests and are asserted rather than avoided. An account may not act on its
/// own membership, so the unlock, approval and forced-password-change routes refuse when the caller addresses
/// itself; the suite therefore drives those routes as the host account against a different account. A host
/// account cannot be deleted through portal administration, and neither can the portal's designated
/// administrator - both are refused so that a tenant cannot be left without anyone able to administer it.
/// And a new credential must differ from the stored one, which is why the change-password test supplies a
/// genuinely new value and a companion test proves that resubmitting the old one is refused.
/// </para>
/// <para>
/// Every account name carries a random suffix. The login name is unique installation-wide, the suites share one
/// database, and xUnit gives no ordering guarantee inside a collection.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class UserApiTests
{
    /// <summary>An identifier no seeded or created account can hold.</summary>
    private const int UnknownUserId = 987654;

    /// <summary>A tenant identifier no seeded or created portal can hold.</summary>
    private const int UnknownPortalId = 987654;

    /// <summary>A second credential used by the change-password tests.</summary>
    private const string ReplacementPassword = "Repl4cement!Pass";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="UserApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public UserApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The collection answers <c>200 OK</c>, carries the tenant's own accounts, and withholds the host
    /// account.
    /// </summary>
    /// <remarks>
    /// The absence of the host account is the load-bearing half of this test. A tenant's account list is a
    /// tenant-scoped view, and the installation's operator is not one of the tenant's accounts even though it
    /// holds a membership row so that it can administer the tenant. Listing it would disclose the operator's
    /// login name to every tenant administrator, and would offer it as a target to routes that legitimately
    /// refuse to act on it.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListUsers_ReturnsOkWithTenantAccountsAndWithoutTheHostAccount()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/users?pageIndex=0&pageSize=100", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.TotalCount.Should().BeGreaterThanOrEqualTo(2);

        IReadOnlyList<string> names = page.Items.Select(item => item.Username).ToList();
        names.Should().Contain(IntegrationSeed.AdminUserName)
            .And.Contain(IntegrationSeed.MemberUserName)
            .And.NotContain(IntegrationSeed.HostUserName);

        page.Items.Should().OnlyContain(item => !item.IsSuperUser);
    }

    /// <summary>The login-name filter narrows the collection.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListUsers_FilteredByLoginName_ReturnsOnlyMatchingAccounts()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/users"
                + $"?pageIndex=0&pageSize=100&userName={IntegrationSeed.MemberUserName}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Should().NotBeEmpty();
        page.Items.Should().OnlyContain(item => item.Username.Contains(
            IntegrationSeed.MemberUserName,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The approval filter is answered by the credential store rather than by the mapped model, because approval
    /// is recorded outside the entity graph. Asserting it proves the query root that reaches those tables is
    /// wired and composable with paging.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListUsers_FilteredByApproval_ReturnsOnlyApprovedAccounts()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto unapproved = await CreateUserAsync(client, authorize: false);

        using HttpResponseMessage approvedOnly = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/users?pageIndex=0&pageSize=100&isApproved=true",
            UriKind.Relative));

        approvedOnly.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? approved = await approvedOnly.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        approved.Should().NotBeNull();
        approved!.Items.Select(item => item.UserId).Should().NotContain(unapproved.UserId);
        approved.Items.Select(item => item.Username).Should().Contain(IntegrationSeed.AdminUserName);

        using HttpResponseMessage pendingOnly = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/users?pageIndex=0&pageSize=100&isApproved=false",
            UriKind.Relative));

        pendingOnly.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? pending = await pendingOnly.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        pending.Should().NotBeNull();
        pending!.Items.Select(item => item.UserId).Should().Contain(unapproved.UserId);
    }

    /// <summary>Naming a profile property without a value is a contradictory filter and is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListUsers_WithProfilePropertyNameButNoValue_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/users"
                + "?pageIndex=0&pageSize=10&profilePropertyName=City",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>The collection requires a bearer token.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListUsers_WithoutCredentials_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/users", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A read answers <c>200 OK</c> and carries the account's role names.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetUser_ReturnsOkWithDetailAndRoles()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            UserRoute(_fixture.Seed.PortalId, _fixture.Seed.AdminUserId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        UserDetailDto detail = await ReadDetailAsync(response);
        detail.UserId.Should().Be(_fixture.Seed.AdminUserId);
        detail.PortalId.Should().Be(_fixture.Seed.PortalId);
        detail.Username.Should().Be(IntegrationSeed.AdminUserName);
        detail.IsSuperUser.Should().BeFalse();
        detail.IsApproved.Should().BeTrue();
        detail.IsLockedOut.Should().BeFalse();
        detail.Roles.Should().Contain(IntegrationSeed.AdministratorsRoleName);
    }

    /// <summary>An unknown account answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetUser_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            UserRoute(_fixture.Seed.PortalId, UnknownUserId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An account that exists but holds no membership of the addressed tenant answers <c>404 Not Found</c>,
    /// which is the resource's tenant-isolation guarantee: reads are scoped by membership, not by identifier.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetUser_WhenAccountIsNotAMemberOfThePortal_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            UserRoute(UnknownPortalId, _fixture.Seed.AdminUserId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A create answers <c>201 Created</c>, carries a location that resolves, records the tenant membership,
    /// auto-assigns the tenant's automatic roles, and writes a credential to the external store.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateUser_ReturnsCreatedWithCredentialAndMembership()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateUserRequest request = NewUserRequest();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        UserDetailDto created = await ReadDetailAsync(response);
        created.UserId.Should().BeGreaterThan(0);
        created.Username.Should().Be(request.Username);
        created.FirstName.Should().Be(request.FirstName);
        created.LastName.Should().Be(request.LastName);
        created.Email.Should().Be(request.Email);
        created.IsApproved.Should().BeTrue();
        created.IsLockedOut.Should().BeFalse();
        created.MustChangePassword.Should().BeFalse();
        created.CreatedDate.Should().NotBeNull();

        // Registered Users is seeded with automatic assignment, so a new account joins it without being asked.
        created.Roles.Should().Contain(IntegrationSeed.RegisteredUsersRoleName);

        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/users/{Route(created.UserId)}");

        using HttpResponseMessage followed = await client.GetAsync(
            new Uri(response.Headers.Location.OriginalString, UriKind.Relative));

        followed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(followed)).UserId.Should().Be(created.UserId);

        int membership = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserPortals] WHERE [UserId] = @userId AND [PortalId] = @portalId;",
            new Dictionary<string, object?>
            {
                ["userId"] = created.UserId,
                ["portalId"] = _fixture.Seed.PortalId,
            });

        membership.Should().Be(1);

        int assignments = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserRoles] WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = created.UserId });

        assignments.Should().BeGreaterThan(0);

        // The credential is the part of a created account that does not live in the mapped model, so it is
        // asserted against the external tables the fixture provisions rather than inferred.
        (await CountCredentialsAsync(request.Username)).Should().Be(1);

        string? storedHash = await ReadStoredHashAsync(request.Username);
        storedHash.Should().NotBeNullOrWhiteSpace();
        storedHash.Should().NotBe(request.Password, "a credential must never be stored in a recoverable form");
        storedHash!.Should().StartWith("$2", "the credential must be stored as a BCrypt hash");
    }

    /// <summary>An account name already in use answers <c>409 Conflict</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateUser_WhenAlreadyRegisteredInThePortal_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateUserRequest request = NewUserRequest();
        request.Username = IntegrationSeed.MemberUserName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("already registered");
    }

    /// <summary>A credential shorter than the configured floor is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateUser_WithShortCredential_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateUserRequest request = NewUserRequest();
        request.Password = "abc12";
        request.ConfirmPassword = "abc12";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A credential that does not match its confirmation is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateUser_WithMismatchedConfirmation_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateUserRequest request = NewUserRequest();
        request.ConfirmPassword = ReplacementPassword;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A malformed electronic-mail address is rejected by the legacy pattern.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateUser_WithMalformedEmail_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateUserRequest request = NewUserRequest();
        request.Email = "not-an-address";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("valid email address");
    }

    /// <summary>A missing family name is rejected, because the stored column does not accept one.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateUser_WithoutFamilyName_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateUserRequest request = NewUserRequest();
        request.LastName = string.Empty;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>An update answers <c>200 OK</c> and the new state survives a read.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateUser_ReturnsOkAndPersists()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        string suffix = Suffix();
        var request = new UpdateUserRequest
        {
            FirstName = "Renamed",
            LastName = "Account",
            DisplayName = "Renamed Account " + suffix,
            Email = "renamed." + suffix + "@example.com",
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            UserRoute(_fixture.Seed.PortalId, created.UserId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        UserDetailDto updated = await ReadDetailAsync(response);
        updated.FirstName.Should().Be("Renamed");
        updated.LastName.Should().Be("Account");
        updated.DisplayName.Should().Be(request.DisplayName);
        updated.Email.Should().Be(request.Email);

        // The login name is not part of the update contract and must be untouched by it.
        updated.Username.Should().Be(created.Username);

        using HttpResponseMessage reread = await client.GetAsync(
            UserRoute(_fixture.Seed.PortalId, created.UserId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(reread)).Email.Should().Be(request.Email);
    }

    /// <summary>An update against an unknown account answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateUser_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            UserRoute(_fixture.Seed.PortalId, UnknownUserId),
            new UpdateUserRequest
            {
                FirstName = "No",
                LastName = "Account",
                DisplayName = "No Account",
                Email = "no.account@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A delete answers <c>204 No Content</c>, the account becomes unreadable in the tenant, and its credential
    /// is removed from the external store because it held no other membership.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteUser_ReturnsNoContentAndRemovesTheCredential()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        (await CountCredentialsAsync(created.Username)).Should().Be(1);

        using HttpResponseMessage response = await client.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, created.UserId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage reread = await client.GetAsync(
            UserRoute(_fixture.Seed.PortalId, created.UserId));

        reread.StatusCode.Should().Be(HttpStatusCode.NotFound);

        int rows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Users] WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = created.UserId });

        rows.Should().Be(0);

        (await CountCredentialsAsync(created.Username)).Should().Be(0);
    }

    /// <summary>
    /// A host account cannot be deleted through portal administration. Permitting it would let a tenant
    /// administrator remove the installation's own operator.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteUser_WhenAccountIsAHostAccount_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, _fixture.Seed.HostUserId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        int rows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Users] WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = _fixture.Seed.HostUserId });

        rows.Should().Be(1);
    }

    /// <summary>
    /// The tenant's designated administrator cannot be deleted, because a tenant with no administrator cannot be
    /// administered.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteUser_WhenAccountIsTheDesignatedAdministrator_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, _fixture.Seed.AdminUserId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        int rows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Users] WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = _fixture.Seed.AdminUserId });

        rows.Should().Be(1);
    }

    /// <summary>A delete against an unknown account answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteUser_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, UnknownUserId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A credential change answers <c>204 No Content</c>, and the new credential really replaces the old one -
    /// proved by signing in with it and by the stored hash having moved.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_ReturnsNoContentAndReplacesTheCredential()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        string before = (await ReadStoredHashAsync(created.Username))!;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = ReplacementPassword,
                ConfirmPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        string after = (await ReadStoredHashAsync(created.Username))!;
        after.Should().NotBe(before);
        after.Should().StartWith("$2");

        // The only assertion that proves the change took effect end to end is a sign-in with the new value.
        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage signedIn = await anonymous.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new { username = created.Username, password = ReplacementPassword },
            ApiTestFixture.Json);

        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage refused = await anonymous.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new { username = created.Username, password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>An administrative reset needs no current credential and answers <c>204 No Content</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_WithResetOperation_ReturnsNoContent()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>A change quoting the wrong current credential is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_WithWrongCurrentCredential_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = "Not-the-current-one!1",
                NewPassword = ReplacementPassword,
                ConfirmPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("current credential is not correct");
    }

    /// <summary>
    /// A self-service change that resubmits the credential already stored is refused by the request
    /// validator, which compares the two values it was given.
    /// </summary>
    /// <remarks>
    /// The answer is <c>400 Bad Request</c> rather than <c>409 Conflict</c> because the request carries both
    /// values, so it can be judged malformed without consulting the store at all. This reproduces the legacy
    /// self-service check, which likewise compared the two form fields. The equivalent judgement made against
    /// the stored hash - the only route available when no current credential is supplied - is a distinct
    /// outcome and is pinned by the companion test below.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_WhenSelfServiceResubmitsTheSameCredential_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = ApiTestFixture.KnownPassword,
                ConfirmPassword = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// An administrative reset to the credential already stored is refused against the stored hash, and
    /// answers <c>409 Conflict</c>.
    /// </summary>
    /// <remarks>
    /// A reset supplies no current credential, so nothing in the request itself reveals that the new value
    /// is the old one - the comparison can only be made against the store, which is why this outcome
    /// describes the state of the resource rather than the shape of the request. Together with the companion
    /// test above, this pins both halves of the guarantee that a credential change must actually change
    /// something, and pins them to the two different reasons they are reached.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_WhenResetSubmitsTheStoredCredential_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("must differ from the one currently stored");
    }

    /// <summary>An unrecognised operation is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_WithUnrecognisedOperation_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = "obliterate",
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Approval can be withdrawn and restored, and each write answers <c>204 No Content</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SetApproval_TogglesApprovalAndBlocksSignIn()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage withdrawn = await client.PutAsync(
            ApprovalRoute(_fixture.Seed.PortalId, created.UserId, isApproved: false),
            content: null);

        withdrawn.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage afterWithdrawal = await client.GetAsync(
            UserRoute(_fixture.Seed.PortalId, created.UserId));

        (await ReadDetailAsync(afterWithdrawal)).IsApproved.Should().BeFalse();

        // Withdrawal has a consequence, and asserting it is what makes the flag more than a stored boolean.
        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage refused = await anonymous.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new { username = created.Username, password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage restored = await client.PutAsync(
            ApprovalRoute(_fixture.Seed.PortalId, created.UserId, isApproved: true),
            content: null);

        restored.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage admitted = await anonymous.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new { username = created.Username, password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        admitted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Setting approval to the value already stored is refused as a request that changes nothing.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SetApproval_WhenAlreadyInThatState_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PutAsync(
            ApprovalRoute(_fixture.Seed.PortalId, created.UserId, isApproved: true),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// An account may not set its own approval. Permitting it would let a pending account approve itself and
    /// bypass the gate entirely.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SetApproval_WhenActingOnSelf_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsync(
            ApprovalRoute(_fixture.Seed.PortalId, _fixture.Seed.HostUserId, isApproved: false),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An unlock of an account that is not locked is refused rather than silently succeeding.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Unlock_WhenAccountIsNotLocked_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsync(
            UnlockRoute(_fixture.Seed.PortalId, created.UserId),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("is not locked");
    }

    /// <summary>
    /// A locked account is released by the unlock route, answers <c>204 No Content</c>, and can sign in again.
    /// </summary>
    /// <remarks>
    /// The lock is applied by writing the credential store directly rather than by signing in wrongly enough
    /// times to trip it. Driving the failure counter through the sign-in route would couple this test to the
    /// configured attempt threshold and to the sign-in rate limiter, neither of which is what it is testing.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Unlock_WhenAccountIsLocked_ReturnsNoContentAndRestoresSignIn()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        await _fixture.Database.ExecuteAsync(
            """
            UPDATE am
            SET am.[IsLockedOut] = 1,
                am.[LastLockoutDate] = SYSUTCDATETIME(),
                am.[FailedPasswordAttemptCount] = 5
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = created.Username });

        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage lockedOut = await anonymous.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new { username = created.Username, password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        lockedOut.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage response = await client.PostAsync(
            UnlockRoute(_fixture.Seed.PortalId, created.UserId),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int stillLocked = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName) AND am.[IsLockedOut] = 1;
            """,
            new Dictionary<string, object?> { ["userName"] = created.Username });

        stillLocked.Should().Be(0);

        using HttpResponseMessage admitted = await anonymous.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new { username = created.Username, password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        admitted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>An unlock of an unknown account answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Unlock_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PostAsync(
            UnlockRoute(_fixture.Seed.PortalId, UnknownUserId),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Requiring a credential change answers <c>204 No Content</c>, is visible on the account, and is refused a
    /// second time because the account already owes one.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RequirePasswordChange_ReturnsNoContentAndIsRefusedWhenAlreadyRequired()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto created = await CreateUserAsync(client);

        created.MustChangePassword.Should().BeFalse();

        using HttpResponseMessage response = await client.PostAsync(
            RequirePasswordChangeRoute(_fixture.Seed.PortalId, created.UserId),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage reread = await client.GetAsync(
            UserRoute(_fixture.Seed.PortalId, created.UserId));

        (await ReadDetailAsync(reread)).MustChangePassword.Should().BeTrue();

        using HttpResponseMessage again = await client.PostAsync(
            RequirePasswordChangeRoute(_fixture.Seed.PortalId, created.UserId),
            content: null);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>The profile projection answers <c>200 OK</c> for an account that exists.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetProfile_ReturnsOk()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            ProfileRoute(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        UserProfileDto? profile = await response.Content
            .ReadFromJsonAsync<UserProfileDto>(ApiTestFixture.Json);

        profile.Should().NotBeNull();
        profile!.UserId.Should().Be(_fixture.Seed.MemberUserId);
        profile.Properties.Should().NotBeNull();
    }

    /// <summary>The profile projection answers <c>404 Not Found</c> for an account that does not exist.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetProfile_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            ProfileRoute(_fixture.Seed.PortalId, UnknownUserId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A profile value round-trips through a definition created for the purpose, which exercises the definition
    /// resource and the profile resource against one another.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Profile_RoundTripsAValueAgainstACreatedDefinition()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto account = await CreateUserAsync(client);

        ProfilePropertyDefinitionDto definition = await CreateProfileDefinitionAsync(client, required: false);

        using HttpResponseMessage stored = await client.PutAsJsonAsync(
            ProfileRoute(_fixture.Seed.PortalId, account.UserId),
            new UserProfileDto
            {
                UserId = account.UserId,
                Properties =
                [
                    new UserProfileValueDto
                    {
                        PropertyDefinitionId = definition.PropertyDefinitionId,
                        PropertyValue = "Amsterdam",
                        Visibility = definition.Visibility,
                        Definition = definition,
                    },
                ],
            },
            ApiTestFixture.Json);

        stored.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage reread = await client.GetAsync(
            ProfileRoute(_fixture.Seed.PortalId, account.UserId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        UserProfileDto? profile = await reread.Content
            .ReadFromJsonAsync<UserProfileDto>(ApiTestFixture.Json);

        profile.Should().NotBeNull();

        UserProfileValueDto value = profile!.Properties
            .Should().ContainSingle(item => item.PropertyDefinitionId == definition.PropertyDefinitionId)
            .Subject;

        value.PropertyValue.Should().Be("Amsterdam");
    }

    /// <summary>A profile value naming a definition the tenant does not hold is refused.</summary>
    /// <remarks>
    /// The answer is <c>404 Not Found</c> rather than <c>400 Bad Request</c>, and that is the deliberate
    /// contract rather than an accident: a profile property is itself an addressable resource under
    /// <c>profile-definitions/{propertyDefinitionId}</c>, so naming one the tenant does not hold is reported the
    /// same way as addressing it directly would be. The status translator registers both the long and the short
    /// form of the unknown-property token, so the two routes cannot drift apart.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateProfile_WithUnknownDefinition_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ProfileRoute(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId),
            new UserProfileDto
            {
                UserId = _fixture.Seed.MemberUserId,
                Properties =
                [
                    new UserProfileValueDto
                    {
                        PropertyDefinitionId = UnknownUserId,
                        PropertyValue = "anything",
                    },
                ],
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("does not define profile property");
    }

    /// <summary>A profile value longer than its definition permits is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateProfile_WithOverlongValue_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        UserDetailDto account = await CreateUserAsync(client);

        ProfilePropertyDefinitionDto definition = await CreateProfileDefinitionAsync(client, required: false);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ProfileRoute(_fixture.Seed.PortalId, account.UserId),
            new UserProfileDto
            {
                UserId = account.UserId,
                Properties =
                [
                    new UserProfileValueDto
                    {
                        PropertyDefinitionId = definition.PropertyDefinitionId,
                        PropertyValue = new string('x', definition.Length + 25),
                    },
                ],
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>The definition resource supports the full round trip, ending in <c>204 No Content</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ProfileDefinitions_SupportCreateReadUpdateAndDelete()
    {
        using HttpClient client = _fixture.CreateHostClient();

        ProfilePropertyDefinitionDto created = await CreateProfileDefinitionAsync(client, required: false);
        created.PropertyDefinitionId.Should().BeGreaterThan(0);
        created.PortalId.Should().Be(_fixture.Seed.PortalId);

        Uri itemRoute = ProfileDefinitionRoute(_fixture.Seed.PortalId, created.PropertyDefinitionId);

        using HttpResponseMessage read = await client.GetAsync(itemRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        ProfilePropertyDefinitionDto? fetched = await read.Content
            .ReadFromJsonAsync<ProfilePropertyDefinitionDto>(ApiTestFixture.Json);

        fetched.Should().NotBeNull();
        fetched!.PropertyName.Should().Be(created.PropertyName);

        using HttpResponseMessage listed = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/profile-definitions", UriKind.Relative));

        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<ProfilePropertyDefinitionDto>? all = await listed.Content
            .ReadFromJsonAsync<IReadOnlyList<ProfilePropertyDefinitionDto>>(ApiTestFixture.Json);

        all.Should().NotBeNull();
        all!.Select(item => item.PropertyDefinitionId).Should().Contain(created.PropertyDefinitionId);

        created.PropertyCategory = "Contact";
        created.ViewOrder = 7;
        created.Visible = false;

        using HttpResponseMessage updated = await client.PutAsJsonAsync(itemRoute, created, ApiTestFixture.Json);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        ProfilePropertyDefinitionDto? afterUpdate = await updated.Content
            .ReadFromJsonAsync<ProfilePropertyDefinitionDto>(ApiTestFixture.Json);

        afterUpdate.Should().NotBeNull();
        afterUpdate!.PropertyCategory.Should().Be("Contact");
        afterUpdate.ViewOrder.Should().Be(7);
        afterUpdate.Visible.Should().BeFalse();

        using HttpResponseMessage removed = await client.DeleteAsync(itemRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage gone = await client.GetAsync(itemRoute);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Creating a definition requires the administrators role, not merely a token.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateProfileDefinition_AsPlainMember_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/profile-definitions", UriKind.Relative),
            NewProfileDefinition(required: false),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A second definition bearing an existing name is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateProfileDefinition_WithDuplicateName_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();

        ProfilePropertyDefinitionDto first = await CreateProfileDefinitionAsync(client, required: false);

        ProfilePropertyDefinitionDto duplicate = NewProfileDefinition(required: false);
        duplicate.PropertyName = first.PropertyName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/profile-definitions", UriKind.Relative),
            duplicate,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// The membership-settings projection is stored against a "User Accounts" module instance, so a tenant
    /// without one is told there is nowhere to read or write it rather than being given silent defaults.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task MembershipSettings_WithoutAUserAccountsModule_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        // A tenant of its own, so the assertion cannot be disturbed by another test installing the module
        // instance into the shared seeded tenant.
        int isolatedPortalId = await CreateIsolatedPortalAsync(client);

        using HttpResponseMessage read = await client.GetAsync(MembershipSettingsRoute(isolatedPortalId));
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage written = await client.PutAsJsonAsync(
            MembershipSettingsRoute(isolatedPortalId),
            new MembershipSettingsDto(),
            ApiTestFixture.Json);

        written.StatusCode.Should().Be(HttpStatusCode.NotFound);

        string body = await written.Content.ReadAsStringAsync();
        body.Should().Contain(MembershipSettingsDto.UserAccountsModuleDefinitionName);
    }

    /// <summary>
    /// With a "User Accounts" module instance in place the projection reads and writes, and the written values
    /// survive a re-read - which proves they were reduced onto stored module settings and read back out again.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task MembershipSettings_RoundTripAgainstAUserAccountsModule()
    {
        using HttpClient client = _fixture.CreateHostClient();
        await EnsureUserAccountsModuleAsync(client);

        Uri route = MembershipSettingsRoute(_fixture.Seed.PortalId);

        using HttpResponseMessage read = await client.GetAsync(route);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        MembershipSettingsDto? defaults = await read.Content
            .ReadFromJsonAsync<MembershipSettingsDto>(ApiTestFixture.Json);

        defaults.Should().NotBeNull();

        // The defaults are the legacy ones, applied when no stored setting exists.
        defaults!.RecordsPerPage.Should().Be(10);
        defaults.ColumnDisplayName.Should().BeTrue();
        defaults.ColumnFirstName.Should().BeFalse();

        MembershipSettingsDto desired = defaults;
        desired.RecordsPerPage = 25;
        desired.ColumnFirstName = true;
        desired.ColumnDisplayName = false;
        desired.DisplayMode = 1;
        desired.ProfileDefaultVisibility = 1;

        using HttpResponseMessage written = await client.PutAsJsonAsync(route, desired, ApiTestFixture.Json);
        written.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage reread = await client.GetAsync(route);
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        MembershipSettingsDto? persisted = await reread.Content
            .ReadFromJsonAsync<MembershipSettingsDto>(ApiTestFixture.Json);

        persisted.Should().NotBeNull();
        persisted!.RecordsPerPage.Should().Be(25);
        persisted.ColumnFirstName.Should().BeTrue();
        persisted.ColumnDisplayName.Should().BeFalse();
        persisted.DisplayMode.Should().Be(1);
        persisted.ProfileDefaultVisibility.Should().Be(1);
    }

    /// <summary>Creates an account through the API and returns its representation.</summary>
    /// <param name="client">A client entitled to create accounts.</param>
    /// <param name="authorize">Whether the account is approved on creation.</param>
    /// <returns>The created account.</returns>
    private async Task<UserDetailDto> CreateUserAsync(HttpClient client, bool authorize = true)
    {
        CreateUserRequest request = NewUserRequest();
        request.Authorize = authorize;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return await ReadDetailAsync(response);
    }

    /// <summary>Creates a profile property definition through the API.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <param name="required">Whether the property must be supplied.</param>
    /// <returns>The created definition.</returns>
    private async Task<ProfilePropertyDefinitionDto> CreateProfileDefinitionAsync(HttpClient client, bool required)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/profile-definitions", UriKind.Relative),
            NewProfileDefinition(required),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        ProfilePropertyDefinitionDto? created = await response.Content
            .ReadFromJsonAsync<ProfilePropertyDefinitionDto>(ApiTestFixture.Json);

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>
    /// Makes sure the seeded tenant holds a "User Accounts" module instance, which is where the membership
    /// settings projection is stored.
    /// </summary>
    /// <remarks>
    /// The definition is written directly and idempotently. Its friendly name carries a unique index, so it can
    /// exist only once installation-wide, and the API exposes no route for installing a definition - packages
    /// are installed, not posted. Only the module INSTANCE is created through the API, because that is the part
    /// under test.
    /// </remarks>
    /// <param name="client">A client entitled to create modules.</param>
    /// <returns>A task representing the setup.</returns>
    private async Task EnsureUserAccountsModuleAsync(HttpClient client)
    {
        int definitionId = await _fixture.Database.ScalarAsync<int>(
            """
            DECLARE @desktopModuleId int;
            DECLARE @definitionId int;

            SELECT @desktopModuleId = [DesktopModuleID]
            FROM [dbo].[DesktopModules]
            WHERE [ModuleName] = @moduleName;

            IF @desktopModuleId IS NULL
            BEGIN
                INSERT INTO [dbo].[DesktopModules]
                    ([FriendlyName], [Description], [Version], [IsPremium], [IsAdmin],
                     [BusinessControllerClass], [FolderName], [ModuleName], [SupportedFeatures])
                VALUES
                    (@definitionName, N'Seeded user-accounts package', N'01.00.00', 0, 1,
                     NULL, N'Admin/Users', @moduleName, 0);

                SET @desktopModuleId = CAST(SCOPE_IDENTITY() AS int);
            END

            SELECT @definitionId = [ModuleDefID]
            FROM [dbo].[ModuleDefinitions]
            WHERE [FriendlyName] = @definitionName;

            IF @definitionId IS NULL
            BEGIN
                INSERT INTO [dbo].[ModuleDefinitions] ([FriendlyName], [DesktopModuleID], [DefaultCacheTime])
                VALUES (@definitionName, @desktopModuleId, 0);

                SET @definitionId = CAST(SCOPE_IDENTITY() AS int);
            END

            SELECT @definitionId;
            """,
            new Dictionary<string, object?>
            {
                ["moduleName"] = "IntegrationUserAccounts",
                ["definitionName"] = MembershipSettingsDto.UserAccountsModuleDefinitionName,
            });

        definitionId.Should().BeGreaterThan(0);

        int instances = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[Modules]
            WHERE [PortalID] = @portalId AND [ModuleDefID] = @definitionId AND [IsDeleted] = 0;
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["definitionId"] = definitionId,
            });

        if (instances > 0)
        {
            return;
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/modules", UriKind.Relative),
            new
            {
                moduleDefId = definitionId,
                tabId = _fixture.Seed.RootTabId,
                moduleTitle = "User Accounts",
                paneName = "ContentPane",
                moduleOrder = 4,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>Creates a tenant of this test's own, so a tenant-wide assertion cannot be disturbed.</summary>
    /// <param name="client">A client carrying host credentials.</param>
    /// <returns>The new tenant's identifier.</returns>
    private async Task<int> CreateIsolatedPortalAsync(HttpClient client)
    {
        string suffix = Suffix();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "User Suite Portal " + suffix,
                portalAlias = "users-" + suffix + ".local",
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Suite",
                administratorLastName = "Administrator",
                administratorUsername = "users_admin_" + suffix,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "users." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("portalId").GetInt32();
    }

    /// <summary>Counts the credential rows held for one login name in the external store.</summary>
    /// <param name="userName">The login name.</param>
    /// <returns>The number of complete credential rows.</returns>
    private Task<int> CountCredentialsAsync(string userName) => _fixture.Database.ScalarAsync<int>(
        """
        SELECT COUNT(*)
        FROM [dbo].[aspnet_Users] au
        INNER JOIN [dbo].[aspnet_Membership] am ON am.[UserId] = au.[UserId]
        WHERE au.[LoweredUserName] = LOWER(@userName);
        """,
        new Dictionary<string, object?> { ["userName"] = userName });

    /// <summary>Reads the stored credential hash for one login name.</summary>
    /// <param name="userName">The login name.</param>
    /// <returns>The stored hash, or <see langword="null"/> when none is held.</returns>
    private async Task<string?> ReadStoredHashAsync(string userName)
    {
        string stored = await _fixture.Database.ScalarAsync<string>(
            """
            SELECT COALESCE(MAX(am.[Password]), N'')
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = userName });

        return stored.Length == 0 ? null : stored;
    }

    /// <summary>Builds a create request whose every unique value carries a random suffix.</summary>
    /// <returns>A well formed create request.</returns>
    private static CreateUserRequest NewUserRequest()
    {
        string suffix = Suffix();

        return new CreateUserRequest
        {
            Username = "itest_user_" + suffix,
            FirstName = "Integration",
            LastName = "Account",
            DisplayName = "Integration Account " + suffix,
            Email = "itest." + suffix + "@example.com",
            Password = ApiTestFixture.KnownPassword,
            ConfirmPassword = ApiTestFixture.KnownPassword,
            Authorize = true,
        };
    }

    /// <summary>Builds a profile property definition whose name carries a random suffix.</summary>
    /// <param name="required">Whether the property must be supplied.</param>
    /// <returns>A definition ready to be posted.</returns>
    private static ProfilePropertyDefinitionDto NewProfileDefinition(bool required) => new()
    {
        DataType = 0,
        PropertyCategory = "Address",
        PropertyName = "ITestCity" + Suffix(),
        Length = 50,
        Required = required,
        ViewOrder = 1,
        Visible = true,
        Visibility = 2,
        DefaultValue = string.Empty,
    };

    /// <summary>Reads an account representation out of a response, failing the test when it is absent.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The representation.</returns>
    private static async Task<UserDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        UserDetailDto? detail = await response.Content
            .ReadFromJsonAsync<UserDetailDto>(ApiTestFixture.Json);

        detail.Should().NotBeNull();
        return detail!;
    }

    /// <summary>Builds the collection route for a tenant's accounts.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UsersRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}/users", UriKind.Relative);

    /// <summary>Builds the item route for one account.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UserRoute(int portalId, int userId) =>
        new($"/api/v1/portals/{Route(portalId)}/users/{Route(userId)}", UriKind.Relative);

    /// <summary>Builds the credential route for one account.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri PasswordRoute(int portalId, int userId) =>
        new($"/api/v1/portals/{Route(portalId)}/users/{Route(userId)}/password", UriKind.Relative);

    /// <summary>Builds the unlock route for one account.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UnlockRoute(int portalId, int userId) =>
        new($"/api/v1/portals/{Route(portalId)}/users/{Route(userId)}/unlock", UriKind.Relative);

    /// <summary>Builds the approval route for one account.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <param name="isApproved">The approval state being requested.</param>
    /// <returns>A relative route.</returns>
    private static Uri ApprovalRoute(int portalId, int userId, bool isApproved) => new(
        $"/api/v1/portals/{Route(portalId)}/users/{Route(userId)}/approval"
            + $"?isApproved={(isApproved ? "true" : "false")}",
        UriKind.Relative);

    /// <summary>Builds the forced-credential-change route for one account.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RequirePasswordChangeRoute(int portalId, int userId) => new(
        $"/api/v1/portals/{Route(portalId)}/users/{Route(userId)}/require-password-change",
        UriKind.Relative);

    /// <summary>Builds the profile route for one account.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ProfileRoute(int portalId, int userId) =>
        new($"/api/v1/portals/{Route(portalId)}/users/{Route(userId)}/profile", UriKind.Relative);

    /// <summary>Builds the item route for one profile property definition.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="propertyDefinitionId">The definition identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ProfileDefinitionRoute(int portalId, int propertyDefinitionId) => new(
        $"/api/v1/portals/{Route(portalId)}/profile-definitions/{Route(propertyDefinitionId)}",
        UriKind.Relative);

    /// <summary>Builds the membership-settings route for a tenant.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri MembershipSettingsRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}/membership-settings", UriKind.Relative);

    /// <summary>Builds the sign-in route for a tenant.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri LoginRoute(int portalId) =>
        new($"/api/v1/auth/login?portalId={Route(portalId)}", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
