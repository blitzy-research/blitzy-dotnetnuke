using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    /// <summary>
    /// The total a legacy account listing reported when it had not counted anything.
    /// </summary>
    /// <remarks>
    /// <c>Library/Components/Shared/Null.vb:L41-L43</c> defines <c>NullInteger</c> as <c>-1</c>, and the
    /// account listing initialised its <c>ByRef totalRecords</c> argument to it. The value is named rather than
    /// written inline so that the assertions which forbid it read as the one fact they are all making.
    /// </remarks>
    private const int LegacySentinelTotal = -1;

    /// <summary>
    /// The shortest credential the configured policy accepts, holding no non-alphanumeric character.
    /// </summary>
    /// <remarks>
    /// Exactly seven characters, matching <c>minRequiredPasswordLength="7"</c> at
    /// <c>Website/release.config:L242</c>, and deliberately free of punctuation because
    /// <c>minRequiredNonalphanumericCharacters="0"</c> at <c>:L243</c> required none. Both bounds are asserted
    /// against this one value, which is why it sits at the boundary rather than comfortably inside it.
    /// </remarks>
    private const string PolicyFloorPassword = "Abcde12";

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/users?pageIndex=0&pageSize=100", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Meta.TotalCount.Should().BeGreaterThanOrEqualTo(2);

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/users"
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
        using HttpClient client = await _fixture.CreateHostClientAsync();
        UserDetailDto unapproved = await CreateUserAsync(client, authorize: false);

        using HttpResponseMessage approvedOnly = await client.GetAsync(new Uri(
            "/api/v1/users?pageIndex=0&pageSize=100&isApproved=true",
            UriKind.Relative));

        approvedOnly.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? approved = await approvedOnly.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        approved.Should().NotBeNull();
        approved!.Items.Select(item => item.UserId).Should().NotContain(unapproved.UserId);
        approved.Items.Select(item => item.Username).Should().Contain(IntegrationSeed.AdminUserName);

        using HttpResponseMessage pendingOnly = await client.GetAsync(new Uri(
            "/api/v1/users?pageIndex=0&pageSize=100&isApproved=false",
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/users"
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
            new Uri("/api/v1/users", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The body-bound search answers the same page as the equivalent query, and its request target carries no
    /// identifying value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THIS ENDPOINT EXISTS TO KEEP AN IDENTIFIER OUT OF THE REQUEST TARGET (CWE-598). A login name, an
    /// electronic-mail address and above all a profile-property name paired with its value are the searching
    /// operator's evidence about a particular person, and a query string is the least private part of a request:
    /// it is written to server and proxy access logs in full, kept in browser history, and forwarded in the
    /// referrer of any subsequent navigation. Transport encryption does not help with any of those, because none
    /// of them is on the wire. The remedy is to move the filter into the body, which is logged by nothing by
    /// default.
    /// <para>
    /// The two actions answer through the IDENTICAL service call, so the assertion here is equality with the
    /// query form rather than a second description of what a search returns. If they ever diverge, this fails.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SearchUsers_ByLoginName_AnswersTheSamePageAsTheEquivalentQueryWithoutNamingAnyoneInTheTarget()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        var route = new Uri("/api/v1/users/search", UriKind.Relative);

        route.OriginalString.Should()
            .NotContain(IntegrationSeed.MemberUserName, "the request target names nobody");

        using HttpResponseMessage searched = await client.PostAsJsonAsync(
            route,
            new UserSearchRequest
            {
                PageIndex = 0,
                PageSize = 100,
                UserName = IntegrationSeed.MemberUserName,
            },
            ApiTestFixture.Json);

        searched.StatusCode.Should().Be(HttpStatusCode.OK);
        searched.RequestMessage!.RequestUri!.ToString().Should()
            .NotContain(IntegrationSeed.MemberUserName, "not even after the client resolved it");

        PagedEnvelope<UserListItemDto>? page = await searched.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Should().NotBeEmpty();
        page.Items.Should().OnlyContain(item => item.Username.Contains(
            IntegrationSeed.MemberUserName,
            StringComparison.OrdinalIgnoreCase));

        PagedEnvelope<UserListItemDto> queried = await ListAsync(
            client,
            $"pageIndex=0&pageSize=100&userName={IntegrationSeed.MemberUserName}");

        page.Items.Select(item => item.UserId).Should().BeEquivalentTo(
            queried.Items.Select(item => item.UserId),
            "the two actions are one service call and must not answer differently");
        page.Meta.TotalCount.Should().Be(queried.Meta.TotalCount);
    }

    /// <summary>
    /// A profile property and its value — the sharpest disclosure of the four filters — travel in the body.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This pair is the reason the endpoint is not merely tidier. The name says which attribute of a person is
    /// being looked up and the value says what is being looked for, so a single logged line records that an
    /// operator searched for a particular person by a particular attribute. A tenant is free to declare a
    /// property holding a national identifier, a date of birth or a home address, so the content of this pair is
    /// not something the migration can bound.
    /// </remarks>
    [Fact]
    public async Task SearchUsers_ByProfileProperty_ReturnsOnlyMatchingAccountsAndDisclosesNeitherNameNorValue()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        ProfilePropertyDefinitionDto definition =
            await CreateProfileDefinitionAsync(client, required: false);
        UserDetailDto account = await CreateUserAsync(client);
        string value = $"value-{Suffix()}";

        await InsertProfileValueAsync(account.UserId, definition.PropertyDefinitionId, value);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            new UserSearchRequest
            {
                PageIndex = 0,
                PageSize = 100,
                ProfilePropertyName = definition.PropertyName,
                ProfilePropertyValue = value,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string target = response.RequestMessage!.RequestUri!.ToString();

        target.Should().NotContain(definition.PropertyName, "the attribute is not in the target");
        target.Should().NotContain(value, "and neither is what was looked for");

        PagedEnvelope<UserListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Select(item => item.UserId).Should().Contain(account.UserId);
    }

    /// <summary>
    /// The body-bound search accepts the sort direction by MEMBER NAME, which is the vocabulary the query form
    /// accepts and the only one any client of this API writes.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ONE CONTRACT MEMBER HAD TWO INCOMPATIBLE WIRE FORMS AND NEITHER SIDE COULD SEE IT. <c>SortDir</c> is
    /// bound from the query string on every collection endpoint, where the framework's type converter accepts
    /// the member name, and from THIS body, which <c>System.Text.Json</c> bound with no converter registered
    /// for the type and therefore accepted only the numeric discriminator. The single-page administration
    /// client wrote the documented name into both, so the compensating search that exists to keep an
    /// identifier out of the request target was answered <c>400</c> with
    /// <c>"$.sortDir": ["The JSON value could not be converted to …SortDirection."]</c> while the identical
    /// query-string listing succeeded. Nothing detected it: both sides are internally well typed, and no test
    /// had ever sent the member in a body.
    /// </para>
    /// <para>
    /// The body is written as a RAW JSON DOCUMENT rather than by serialising the request type, and that is the
    /// point of the test. Serialising <c>UserSearchRequest</c> here would apply this assembly's own converter
    /// policy and so could only ever produce a form the server accepts - which is precisely how the defect
    /// survived. The literal below is the byte sequence the browser client sends.
    /// </para>
    /// <para>
    /// Both directions are exercised and the two pages are asserted to be REVERSES of each other, so the test
    /// proves the value was applied rather than merely accepted. A converter that bound every name to
    /// <c>Ascending</c> would satisfy a status-code assertion and fail this one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SearchUsers_WithTheSortDirectionNamed_AppliesItRatherThanRefusingTheBody()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        PagedEnvelope<UserListItemDto> ascending = await SearchWithRawBodyAsync(
            client,
            """{"pageIndex":0,"pageSize":100,"sortBy":"username","sortDir":"Ascending"}""");

        PagedEnvelope<UserListItemDto> descending = await SearchWithRawBodyAsync(
            client,
            """{"pageIndex":0,"pageSize":100,"sortBy":"username","sortDir":"Descending"}""");

        ascending.Items.Should().NotBeEmpty();
        ascending.Items.Select(item => item.Username).Should().BeInAscendingOrder(
            StringComparer.OrdinalIgnoreCase);
        descending.Items.Select(item => item.Username).Should().BeInDescendingOrder(
            StringComparer.OrdinalIgnoreCase);

        descending.Items.Select(item => item.UserId).Should().Equal(
            ascending.Items.Select(item => item.UserId).Reverse(),
            "the named direction was applied, not merely tolerated");
    }

    /// <summary>
    /// The sort direction is also accepted as the discriminator, so a caller written against the previous
    /// behaviour is not broken by pinning the name.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The converter admits both spellings deliberately: the query-string binder accepts numeric text as well
    /// as a name, so refusing the number in a body would have replaced one divergence with another.
    /// </remarks>
    [Fact]
    public async Task SearchUsers_WithTheSortDirectionAsADiscriminator_IsStillAccepted()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        PagedEnvelope<UserListItemDto> named = await SearchWithRawBodyAsync(
            client,
            """{"pageIndex":0,"pageSize":100,"sortBy":"username","sortDir":"Descending"}""");

        PagedEnvelope<UserListItemDto> numbered = await SearchWithRawBodyAsync(
            client,
            """{"pageIndex":0,"pageSize":100,"sortBy":"username","sortDir":1}""");

        numbered.Items.Select(item => item.UserId).Should().Equal(
            named.Items.Select(item => item.UserId),
            "the two spellings name one direction");
    }

    /// <summary>
    /// A direction outside the declared pair is reported by the request validator, in the same field-level shape
    /// the query form produces, rather than as a deserialisation failure.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is why the converter carries an undeclared integer instead of refusing it. Membership is decided in
    /// exactly one place - the <c>IsInEnum</c> rule on the paging contract - so one mistake gets one
    /// explanation whichever transport carried it.
    /// </remarks>
    [Fact]
    public async Task SearchUsers_WithAnUndeclaredSortDirection_IsReportedByTheValidator()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using var body = new StringContent(
            """{"pageIndex":0,"pageSize":10,"sortBy":"username","sortDir":5}""",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Errors.Keys.Should().Contain(nameof(PagedRequest.SortDir));
        problem.Errors[nameof(PagedRequest.SortDir)].Should()
            .Contain("The sort direction must be either Ascending or Descending.");
    }

    /// <summary>
    /// Naming a profile property without a value is refused here exactly as it is on the query form, because the
    /// rule belongs to the service rather than to either action.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SearchUsers_WithProfilePropertyNameButNoValue_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            new UserSearchRequest { PageIndex = 0, PageSize = 10, ProfilePropertyName = "City" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The body-bound search applies the same paging ceiling as the query form, because its validator derives
    /// from the same base.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Asserted because a caller moving a request from the query string into the body must not discover that a
    /// larger page is suddenly legal. Two validators that happen to agree today would be free to drift; one base
    /// class cannot.
    /// </remarks>
    [Fact]
    public async Task SearchUsers_WithPageSizeAboveTheCeiling_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            new UserSearchRequest
            {
                PageIndex = 0,
                PageSize = PagedRequestValidator<UserSearchRequest>.MaximumPageSize + 1,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A blank filter in the body means what a blank filter in the query string means: no filter at all.
    /// </summary>
    /// <param name="member">The body member to send blank.</param>
    /// <param name="blank">The blank value to send.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// ⚠ THIS PINS A REGRESSION THAT REACHED A RUNNING BROWSER. The application service treats absence as
    /// "do not filter" and REFUSES a filter that is present but blank, because an empty prefix matches
    /// every row and would make a filtered search silently unfiltered. The query-bound listing never
    /// reaches that rule - the framework's query binder converts a blank query value to null before the
    /// action sees it, for the empty string and for whitespace alike - so the rule was unreachable over
    /// HTTP until a body-bound action existed. Once one did, an operator CLEARING the search box on the
    /// account listing posted <c>{"userName":""}</c> and met a red error banner where the same operation
    /// through the query string answers 200.
    /// <para>
    /// Both blank forms are exercised for each member, because the binder converts both and a fix that
    /// handled only the empty string would leave whitespace diverging - which is exactly the asymmetry
    /// this endpoint exists to avoid.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("userName", "")]
    [InlineData("userName", "   ")]
    [InlineData("email", "")]
    [InlineData("email", "   ")]
    [InlineData("profilePropertyName", "")]
    [InlineData("profilePropertyName", "   ")]
    [InlineData("profilePropertyValue", "")]
    [InlineData("profilePropertyValue", "   ")]
    public async Task SearchUsers_WithABlankFilter_AnswersAsThoughItWereOmitted(
        string member,
        string blank)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // Sent as raw JSON rather than through the request type, because the point is what an arbitrary
        // client can put on the wire: a typed fixture would let a future member rename hide the case.
        using var body = new StringContent(
            FormattableString.Invariant(
                $"{{\"pageIndex\":0,\"pageSize\":10,\"{member}\":\"{blank}\"}}"),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage blankFilter = await client.PostAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            body);

        blankFilter.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a blank {0} names nobody, exactly as a blank query value does",
            member);

        PagedEnvelope<UserListItemDto>? blankPage = await blankFilter.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        using HttpResponseMessage omitted = await client.PostAsJsonAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            new UserSearchRequest { PageIndex = 0, PageSize = 10 },
            ApiTestFixture.Json);

        omitted.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? omittedPage = await omitted.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        blankPage.Should().NotBeNull();
        omittedPage.Should().NotBeNull();
        blankPage!.Meta.TotalCount.Should().Be(
            omittedPage!.Meta.TotalCount,
            "and it must answer the same page, not merely avoid refusing");
        blankPage.Items.Select(item => item.UserId).Should()
            .BeEquivalentTo(omittedPage.Items.Select(item => item.UserId));
    }

    /// <summary>
    /// A filter carrying something to filter BY is still refused when its companion is missing, because
    /// normalising a blank value did not weaken the combination rule.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The guard on the fix above. Normalising a blank profile-property VALUE to absent means a request
    /// naming a property with a blank value is now indistinguishable from one naming a property with no
    /// value at all - and that combination must remain a refusal, exactly as it is through the query
    /// string. Without this case the previous test could be satisfied by discarding the rule instead of
    /// by relocating one conversion.
    /// </remarks>
    [Fact]
    public async Task SearchUsers_WithProfilePropertyNameAndABlankValue_IsStillRefused()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using var body = new StringContent(
            "{\"pageIndex\":0,\"pageSize\":10,\"profilePropertyName\":\"City\",\"profilePropertyValue\":\"\"}",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            body);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a property named without a value is a refusal however the absence was spelled");
    }

    /// <summary>The body-bound search requires a bearer token, exactly as the collection does.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SearchUsers_WithoutCredentials_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            new UserSearchRequest { PageIndex = 0, PageSize = 10 },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The body-bound search is gated on the same policy as the collection, so moving the filter off the target
    /// widened nothing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SearchUsers_AsRegisteredMember_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateClientForAsync(
            IntegrationSeed.MemberUserName,
            ApiTestFixture.KnownPassword);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            new UserSearchRequest { PageIndex = 0, PageSize = 10 },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A read answers <c>200 OK</c> and carries the account's role names.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetUser_ReturnsOkWithDetailAndRoles()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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

    /// <summary>
    /// Being the account owner in portal A does not authorise the same account identifier when the request
    /// arrives on portal B's host.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// SEC-004: the subject identifier matches deliberately, so the owner branch is the only tempting path.
    /// The token names portal A while the arrival host resolves to an existing portal B; the policy must
    /// refuse before the user service can turn the mismatch into an ordinary not-found response.
    /// <para>
    /// The account resource is addressed by its canonical flat route, so the tenant is selected by the
    /// arrival host rather than by a path segment. That is what makes the host the thing this fact varies:
    /// on this surface the cross-tenant attempt cannot be expressed any other way.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetUser_AsTheSameSubjectArrivingOnAnotherTenantsHost_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedPortal otherPortal = await CreateIsolatedPortalAsync(host);

        using HttpClient owner = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        owner.BaseAddress = new Uri($"http://{otherPortal.Alias}", UriKind.Absolute);

        using HttpResponseMessage response = await owner.GetAsync(
            UserRoute(otherPortal.PortalId, _fixture.Seed.MemberUserId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An unknown account answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetUser_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedPortal other = await CreateIsolatedPortalAsync(host);
        using HttpClient client = await _fixture.CreateHostClientAsync(other.Alias);

        using HttpResponseMessage response = await client.GetAsync(
            UserRoute(other.PortalId, _fixture.Seed.AdminUserId));

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
            $"/api/v1/users/{Route(created.UserId)}");

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, UnknownUserId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A credential change answers <c>204 No Content</c>, and the new credential really replaces the old one -
    /// proved by signing in with it and by the stored hash having moved.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The acting caller is THE ACCOUNT ITSELF, minted for the account the administrator just created, because
    /// the self-service change endpoint now requires the caller to be the account holder. This suite used to
    /// drive it as the host account operating on somebody else's credential, which is the shape the split
    /// closed: a caller who is not the holder must go through the administrative reset, which is separately
    /// authorised.
    /// </remarks>
    [Fact]
    public async Task ChangePassword_ReturnsNoContentAndReplacesTheCredential()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        // The CHANGE operation is self-service, so it is driven AS THE ACCOUNT ITSELF, signed in with the
        // credential the create supplied. An earlier revision of this test drove it as the host account, which
        // the service now refuses: presenting the current credential is proof of possession rather than of
        // authority, so a caller acting on somebody else's account uses the RESET operation, which is audited
        // as an administrative act. The reset path has its own test immediately below.
        using HttpClient owner = await ClientForAccountAsync(created);

        string before = (await ReadStoredHashAsync(created.Username))!;

        using HttpResponseMessage response = await owner.PostAsJsonAsync(
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

    /// <summary>
    /// An administrative reset needs no current credential and answers <c>204 No Content</c> - on its own
    /// address, reached by a caller that administers the account's portal.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ResetPassword_AsAdministrator_ReturnsNoContent()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordResetRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// A reset requires the administrators role, not merely a token, and a refused reset leaves the stored
    /// credential exactly as it was.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing test for the split between the two operations. A change proves possession by
    /// quoting the current credential; a reset quotes nothing, so the only thing standing between an
    /// authenticated caller and another account's credential is the portal-administrator policy. The stored
    /// hash is compared before and after because a refusal that still wrote would be a silent takeover.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_WithResetOperation_AsPlainMember_ReturnsForbidden()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        string before = (await ReadStoredHashAsync(created.Username))!;

        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await member.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        string after = (await ReadStoredHashAsync(created.Username))!;
        after.Should().Be(before, "a refused reset must not touch the credential store");
    }

    /// <summary>A change quoting the wrong current credential is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_WithWrongCurrentCredential_ReturnsBadRequest()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        // Driven as the account itself, because the change operation is self-service; see the note on the
        // successful-change test above.
        using HttpClient owner = await ClientForAccountAsync(created);

        using HttpResponseMessage response = await owner.PostAsJsonAsync(
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
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        using HttpClient client = await ClientForAccountAsync(created);

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
    public async Task ResetPassword_WhenItSubmitsTheStoredCredential_ReturnsConflict()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordResetRoute(_fixture.Seed.PortalId, created.UserId),
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
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        using HttpClient client = await ClientForAccountAsync(created);

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

    /// <summary>
    /// THE CENTRAL SECURITY FACT OF THE CREDENTIAL SPLIT. Naming the reset operation on the SELF-SERVICE
    /// address is refused, so the current-credential check cannot be skipped by an instruction in the caller's
    /// own request body. Before the split, this exact request succeeded and rewrote the account's credential
    /// without proving anything: any bearer token that could reach the endpoint was enough.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_NamingTheResetOperation_IsRefusedAndChangesNothing()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        using HttpClient client = await ClientForAccountAsync(created);

        string before = (await ReadStoredHashAsync(created.Username))!;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string after = (await ReadStoredHashAsync(created.Username))!;
        after.Should().Be(before, "a refused credential write must leave the stored credential untouched");
    }

    /// <summary>
    /// One account may not change another's credential, even inside the same tenant. The route names the
    /// account, so the policy compares it against the subject the token carries.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_ByAnAccountOtherThanTheHolder_ReturnsForbidden()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        string before = (await ReadStoredHashAsync(created.Username))!;

        using HttpClient other = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await other.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = ReplacementPassword,
                ConfirmPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        string after = (await ReadStoredHashAsync(created.Username))!;
        after.Should().Be(before);
    }

    /// <summary>
    /// An account may not reset its OWN credential through the administrative address, because doing so would
    /// be a credential write with no proof of the current value and no administrator involved - which is
    /// exactly the escape the split exists to remove.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ResetPassword_ByTheAccountItself_ReturnsForbidden()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        string before = (await ReadStoredHashAsync(created.Username))!;

        using HttpClient client = await ClientForAccountAsync(created);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordResetRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        string after = (await ReadStoredHashAsync(created.Username))!;
        after.Should().Be(before);
    }

    /// <summary>
    /// Naming the change operation on the ADMINISTRATIVE address is refused, which is the mirror image of the
    /// self-service refusal: neither operation can be reached through the other's endpoint.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ResetPassword_NamingTheChangeOperation_IsRefused()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PasswordResetRoute(_fixture.Seed.PortalId, created.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = ReplacementPassword,
                ConfirmPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Approval can be withdrawn and restored, and each write answers <c>204 No Content</c>.</summary>
    /// <remarks>
    /// The refused sign-in answers <c>400 Bad Request</c> rather than <c>401 Unauthorized</c>: the credential
    /// is verified before the approval gate, so a caller reaching the refusal has proved its credential and
    /// is not unauthenticated - its account is simply not authorised for this portal.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SetApproval_TogglesApprovalAndBlocksSignIn()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

        // The approval outcomes are UNAUTHORIZED, not bad-request. Each names an account state that refused
        // a sign-in the credential itself did not refuse, so the request was correct and the account was
        // not yet admissible - telling a client its request was at fault would be the wrong answer. The
        // outcome is still NAMED in the body, which is the property this fact exists to assert: the caller
        // has proved its credential, so it is entitled to know which gate refused.
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await refused.Content.ReadAsStringAsync()).Should().Contain(
            "auth.account_not_approved",
            "the credential was accepted and only the approval is missing, so the answer names the approval "
            + "outcome instead of implying the credential was wrong");

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PutAsync(
            ApprovalRoute(_fixture.Seed.PortalId, created.UserId, isApproved: true),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// Omitting the approval state is refused with a validation problem rather than being read as "withdraw
    /// approval".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE DESTRUCTIVE READING WAS THE QUIET ONE. Bound as a non-nullable boolean the query parameter was
    /// OPTIONAL, so a caller addressing this route with no query string bound <see langword="false"/>: the
    /// account's approval was withdrawn and its live sessions were revoked, with nothing about the request
    /// malformed enough for the model binder to object. The 400 this endpoint advertises was therefore
    /// unreachable. This test is the evidence path the review asked for, and it asserts the account is left
    /// APPROVED afterwards - the refusal has to be a refusal, not a refusal reported after the write.
    /// </remarks>
    [Fact]
    public async Task SetApproval_WhenTheStateIsOmitted_ReturnsBadRequestAndChangesNothing()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(client);

        using HttpResponseMessage response = await client.PutAsync(
            new Uri(
                $"/api/v1/users/{Route(created.UserId)}/approval",
                UriKind.Relative),
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The problem document names the parameter, so a caller learns which value to supply.
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("isApproved");

        // And the account is untouched: it is still approved, which is what a subsequent request to set the
        // state it already holds reports as a conflict.
        using HttpResponseMessage unchanged = await client.PutAsync(
            ApprovalRoute(_fixture.Seed.PortalId, created.UserId, isApproved: true),
            content: null);

        unchanged.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "the refused request must not have withdrawn the approval it never asked to withdraw");
    }

    /// <summary>
    /// An account may not set its own approval. Permitting it would let a pending account approve itself and
    /// bypass the gate entirely.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SetApproval_WhenActingOnSelf_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

    /// <summary>
    /// A caller owing a mandatory credential change is confined to the remediation surface until the stored
    /// flag is cleared by a successful change.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task RequiredPasswordChange_RestrictsTheSessionUntilRemediated()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        UserDetailDto account = await CreateUserAsync(host);

        using HttpResponseMessage required = await host.PostAsync(
            RequirePasswordChangeRoute(_fixture.Seed.PortalId, account.UserId),
            content: null);
        required.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpClient caller = await ClientForAccountAsync(account);

        using HttpResponseMessage blocked = await caller.GetAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("remediation");

        // MIGRATION: THE PROFILE ROUTE IS CLOSED TOO, because the outstanding requirement is a CREDENTIAL one
        // and reading a profile cannot clear it. Each remediation allowance is admitted only while the
        // requirement it remedies is the one outstanding: the profile routes open when a profile must be
        // completed, the credential route opens when a credential must be changed, and the authentication
        // lifecycle stays open throughout. Admitting every owner-scoped remediation route whenever ANY
        // requirement was outstanding would widen the restricted session well past the one route that can end
        // it, which is precisely what the sibling fact
        // AuthApiTests.RequiredPasswordChange_AllowsOnlyAuthenticationAndOwnPasswordRemediation names.
        using HttpResponseMessage profile = await caller.GetAsync(
            ProfileRoute(_fixture.Seed.PortalId, account.UserId));
        profile.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the profile routes remedy an incomplete profile, not an outstanding credential change");

        using HttpResponseMessage changed = await caller.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, account.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = ReplacementPassword,
                ConfirmPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);
        changed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage admitted = await caller.GetAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));
        admitted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Verified registration cannot approve a pending account whose stored address violates the portal's
    /// current <c>Security_EmailValidation</c> expression.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task VerificationCode_DoesNotApproveAnEmailRejectedByThePortalRule()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        await EnsureUserAccountsModuleAsync();

        int moduleId = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT TOP (1) m.[ModuleID]
            FROM [dbo].[Modules] m
            INNER JOIN [dbo].[ModuleDefinitions] d ON d.[ModuleDefID] = m.[ModuleDefID]
            WHERE m.[PortalID] = @portalId AND d.[FriendlyName] = @definitionName AND m.[IsDeleted] = 0;
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["definitionName"] = MembershipSettingsDto.UserAccountsModuleDefinitionName,
            });

        int originalRegistration = await _fixture.Database.ScalarAsync<int>(
            "SELECT [UserRegistration] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = _fixture.Seed.PortalId });
        int originalExpressionCount = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[ModuleSettings]
            WHERE [ModuleID] = @moduleId AND [SettingName] = N'Security_EmailValidation';
            """,
            new Dictionary<string, object?> { ["moduleId"] = moduleId });
        string? originalExpression = originalExpressionCount == 0
            ? null
            : await _fixture.Database.ScalarAsync<string>(
                """
                SELECT [SettingValue]
                FROM [dbo].[ModuleSettings]
                WHERE [ModuleID] = @moduleId AND [SettingName] = N'Security_EmailValidation';
                """,
                new Dictionary<string, object?> { ["moduleId"] = moduleId });

        try
        {
            await _fixture.Database.ExecuteAsync(
                """
                UPDATE [dbo].[Portals]
                SET [UserRegistration] = 3
                WHERE [PortalID] = @portalId;

                MERGE [dbo].[ModuleSettings] AS target
                USING (SELECT @moduleId AS [ModuleID], N'Security_EmailValidation' AS [SettingName]) AS source
                ON target.[ModuleID] = source.[ModuleID] AND target.[SettingName] = source.[SettingName]
                WHEN MATCHED THEN
                    UPDATE SET [SettingValue] = N'^[^@]+@example\.com$'
                WHEN NOT MATCHED THEN
                    INSERT ([ModuleID], [SettingName], [SettingValue])
                    VALUES (source.[ModuleID], source.[SettingName], N'^[^@]+@example\.com$');
                """,
                new Dictionary<string, object?>
                {
                    ["portalId"] = _fixture.Seed.PortalId,
                    ["moduleId"] = moduleId,
                });

            CreateUserRequest request = NewUserRequest();
            request.Authorize = false;
            request.Email = "pending@example.com";

            using HttpResponseMessage createdResponse = await host.PostAsJsonAsync(
                UsersRoute(_fixture.Seed.PortalId),
                request,
                ApiTestFixture.Json);
            createdResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            UserDetailDto created = await ReadDetailAsync(createdResponse);

            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Users] SET [Email] = N'pending@invalid.test' WHERE [UserID] = @userId;",
                new Dictionary<string, object?> { ["userId"] = created.UserId });

            using HttpClient anonymous = _fixture.CreateAnonymousClient();
            using HttpResponseMessage login = await anonymous.PostAsJsonAsync(
                LoginRoute(_fixture.Seed.PortalId),
                new
                {
                    username = created.Username,
                    password = ApiTestFixture.KnownPassword,
                    verificationCode = FormattableString.Invariant(
                        $"{_fixture.Seed.PortalId}-{created.UserId}"),
                },
                ApiTestFixture.Json);

            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            int approved = await _fixture.Database.ScalarAsync<int>(
                """
                SELECT CAST(am.[IsApproved] AS int)
                FROM [dbo].[aspnet_Membership] am
                INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
                WHERE au.[LoweredUserName] = LOWER(@userName);
                """,
                new Dictionary<string, object?> { ["userName"] = created.Username });
            approved.Should().Be(0);
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Portals] SET [UserRegistration] = @registration WHERE [PortalID] = @portalId;",
                new Dictionary<string, object?>
                {
                    ["registration"] = originalRegistration,
                    ["portalId"] = _fixture.Seed.PortalId,
                });

            if (originalExpression is null)
            {
                await _fixture.Database.ExecuteAsync(
                    """
                    DELETE FROM [dbo].[ModuleSettings]
                    WHERE [ModuleID] = @moduleId AND [SettingName] = N'Security_EmailValidation';
                    """,
                    new Dictionary<string, object?> { ["moduleId"] = moduleId });
            }
            else
            {
                await _fixture.Database.ExecuteAsync(
                    """
                    UPDATE [dbo].[ModuleSettings]
                    SET [SettingValue] = @expression
                    WHERE [ModuleID] = @moduleId AND [SettingName] = N'Security_EmailValidation';
                    """,
                    new Dictionary<string, object?>
                    {
                        ["moduleId"] = moduleId,
                        ["expression"] = originalExpression,
                    });
            }
        }
    }

    /// <summary>The profile projection answers <c>200 OK</c> for an account that exists.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetProfile_ReturnsOk()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            ProfileRoute(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        UserProfileDto? profile = await response.Content
            .ReadEnvelopeAsync<UserProfileDto>();

        profile.Should().NotBeNull();
        profile!.UserId.Should().Be(_fixture.Seed.MemberUserId);
        profile.Properties.Should().NotBeNull();
    }

    /// <summary>The profile projection answers <c>404 Not Found</c> for an account that does not exist.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetProfile_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
            .ReadEnvelopeAsync<UserProfileDto>();

        profile.Should().NotBeNull();

        UserProfileValueDto value = profile!.Properties
            .Should().ContainSingle(item => item.PropertyDefinitionId == definition.PropertyDefinitionId)
            .Subject;

        value.PropertyValue.Should().Be("Amsterdam");
    }

    /// <summary>
    /// Malformed profile collections are rejected at the API boundary before any definition lookup or write.
    /// </summary>
    [Fact]
    public async Task UpdateProfile_WithDuplicateOrInvalidEntries_ReturnsBadRequest()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        UserDetailDto account = await CreateUserAsync(host);
        ProfilePropertyDefinitionDto definition = await CreateProfileDefinitionAsync(host, required: false);
        Uri route = ProfileRoute(_fixture.Seed.PortalId, account.UserId);

        using HttpResponseMessage duplicate = await host.PutAsJsonAsync(
            route,
            new UserProfileDto
            {
                UserId = account.UserId,
                Properties =
                [
                    new UserProfileValueDto
                    {
                        PropertyDefinitionId = definition.PropertyDefinitionId,
                        PropertyValue = "one",
                        Visibility = 0,
                    },
                    new UserProfileValueDto
                    {
                        PropertyDefinitionId = definition.PropertyDefinitionId,
                        PropertyValue = "two",
                        Visibility = 0,
                    },
                ],
            },
            ApiTestFixture.Json);
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using HttpResponseMessage visibility = await host.PutAsJsonAsync(
            route,
            new UserProfileDto
            {
                UserId = account.UserId,
                Properties =
                [
                    new UserProfileValueDto
                    {
                        PropertyDefinitionId = definition.PropertyDefinitionId,
                        PropertyValue = "value",
                        Visibility = 3,
                    },
                ],
            },
            ApiTestFixture.Json);
        visibility.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using HttpResponseMessage excessive = await host.PutAsJsonAsync(
            route,
            new UserProfileDto
            {
                UserId = account.UserId,
                Properties = Enumerable.Range(1, 65)
                    .Select(identifier => new UserProfileValueDto
                    {
                        PropertyDefinitionId = identifier,
                        PropertyValue = "value",
                        Visibility = 0,
                    })
                    .ToList(),
            },
            ApiTestFixture.Json);
        excessive.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Updating a profile in one tenant neither returns nor clears values owned by another tenant's
    /// definitions.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateProfile_PreservesValuesOwnedByAnotherPortal()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedPortal otherPortal = await CreateIsolatedPortalAsync(host);
        int otherPortalId = otherPortal.PortalId;
        // Addressed at the OTHER portal's own alias, which is what makes the declaration foreign. A host
        // account is exempt from tenant binding, so the same persona can reach either tenant; what decides the
        // owning portal is the host name the request arrives at.
        using HttpClient foreignTenantHost = await _fixture.CreateHostClientAsync(otherPortal.Alias);
        ProfilePropertyDefinitionDto foreignDefinition =
            await CreateProfileDefinitionAsync(foreignTenantHost, required: false);

        await InsertProfileValueAsync(
            _fixture.Seed.MemberUserId,
            foreignDefinition.PropertyDefinitionId,
            "foreign-value");

        using HttpResponseMessage response = await host.PutAsJsonAsync(
            ProfileRoute(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId),
            new UserProfileDto
            {
                UserId = _fixture.Seed.MemberUserId,
                Properties = Array.Empty<UserProfileValueDto>(),
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        string retained = await _fixture.Database.ScalarAsync<string>(
            """
            SELECT COALESCE([PropertyValue], CONVERT(nvarchar(max), [PropertyText]))
            FROM [dbo].[UserProfile]
            WHERE [UserID] = @userId AND [PropertyDefinitionID] = @definitionId;
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = _fixture.Seed.MemberUserId,
                ["definitionId"] = foreignDefinition.PropertyDefinitionId,
            });

        retained.Should().Be("foreign-value");
    }

    /// <summary>
    /// Removing an account from one portal deletes that portal's profile values in the same operation while
    /// retaining values and the installation account needed by another portal membership.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteUser_RemovesOnlyTheAddressedPortalsProfileValues()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        UserDetailDto account = await CreateUserAsync(host);
        IsolatedPortal otherPortal = await CreateIsolatedPortalAsync(host);
        int otherPortalId = otherPortal.PortalId;

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[UserPortals] ([UserId], [PortalId], [CreatedDate], [Authorised])
            VALUES (@userId, @portalId, SYSUTCDATETIME(), 1);
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = account.UserId,
                ["portalId"] = otherPortalId,
            });

        ProfilePropertyDefinitionDto localDefinition =
            await CreateProfileDefinitionAsync(host, required: false);

        // Addressed at the OTHER portal's own alias; see the note on the helper. Creating both declarations
        // through the seeded-portal client would put both in the seeded portal and assert nothing.
        using HttpClient foreignTenantHost = await _fixture.CreateHostClientAsync(otherPortal.Alias);
        ProfilePropertyDefinitionDto foreignDefinition =
            await CreateProfileDefinitionAsync(foreignTenantHost, required: false);

        await InsertProfileValueAsync(account.UserId, localDefinition.PropertyDefinitionId, "local-value");
        await InsertProfileValueAsync(account.UserId, foreignDefinition.PropertyDefinitionId, "foreign-value");

        using HttpResponseMessage removed = await host.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int localValues = await CountProfileValuesAsync(account.UserId, localDefinition.PropertyDefinitionId);
        int foreignValues = await CountProfileValuesAsync(account.UserId, foreignDefinition.PropertyDefinitionId);
        int accountRows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Users] WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = account.UserId });

        localValues.Should().Be(0);
        foreignValues.Should().Be(1);
        accountRows.Should().Be(1, "another portal still owns a membership for the installation account");
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        ProfilePropertyDefinitionDto created = await CreateProfileDefinitionAsync(client, required: false);
        created.PropertyDefinitionId.Should().BeGreaterThan(0);
        created.PortalId.Should().Be(_fixture.Seed.PortalId);

        Uri itemRoute = ProfileDefinitionRoute(_fixture.Seed.PortalId, created.PropertyDefinitionId);

        using HttpResponseMessage read = await client.GetAsync(itemRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        ProfilePropertyDefinitionDto? fetched = await read.Content
            .ReadEnvelopeAsync<ProfilePropertyDefinitionDto>();

        fetched.Should().NotBeNull();
        fetched!.PropertyName.Should().Be(created.PropertyName);

        using HttpResponseMessage listed = await client.GetAsync(
            new Uri("/api/v1/profile-definitions", UriKind.Relative));

        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<ProfilePropertyDefinitionDto>? allEnvelope = await listed.Content
            .ReadFromJsonAsync<CollectionEnvelope<ProfilePropertyDefinitionDto>>(ApiTestFixture.Json);

        allEnvelope.Should().NotBeNull();
        IReadOnlyList<ProfilePropertyDefinitionDto>? all = allEnvelope!.Data;

        all.Should().NotBeNull();
        all!.Select(item => item.PropertyDefinitionId).Should().Contain(created.PropertyDefinitionId);

        UpdateProfilePropertyDefinitionRequest amendment = AmendmentFrom(created);
        amendment.PropertyCategory = "Contact";
        amendment.ViewOrder = 7;
        amendment.Visible = false;

        using HttpResponseMessage updated = await client.PutAsJsonAsync(itemRoute, amendment, ApiTestFixture.Json);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        ProfilePropertyDefinitionDto? afterUpdate = await updated.Content
            .ReadEnvelopeAsync<ProfilePropertyDefinitionDto>();

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
        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/profile-definitions", UriKind.Relative),
            NewProfileDefinition(required: false),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The definition write path is validated at the BOUNDARY on its single canonical address.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The name carries a space, which the legacy pattern
    /// <c>^[a-zA-Z0-9._%\-+']+$</c> - rendered by the reflective property editor from the attribute on
    /// <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb:L228</c> - refused. Rule-for-rule
    /// parity is proved by the validator's own unit suite; what this fact proves is different and cannot be
    /// proved there: that the validator is ATTACHED, by the globally registered filter, to this action.
    /// </para>
    /// <para>
    /// The filter resolves a validator from the bound argument's type rather than from route metadata, and
    /// the assertion proves that filter is attached to the canonical action.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateProfileDefinition_WithANameTheLegacyPatternRefused_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateProfilePropertyDefinitionRequest definition = NewProfileDefinition(required: false);
        definition.PropertyName = "Home City";

        var route = new Uri("/api/v1/profile-definitions", UriKind.Relative);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            route,
            definition,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The offending field must be named: a response that says only "bad request" gives the caller
        // nothing to correct.
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(nameof(CreateProfilePropertyDefinitionRequest.PropertyName));

        // The refusal must be a refusal: nothing may reach the store.
        int stored = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ProfilePropertyDefinition] WHERE [PropertyName] = @name;",
            new Dictionary<string, object?> { ["name"] = definition.PropertyName });

        stored.Should().Be(0);
    }

    /// <summary>
    /// Declaring the same profile property name from several callers at once declares it exactly once, refuses
    /// every other caller as a conflict, and answers no caller with a server fault.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: SEC-F6. <c>IX_ProfilePropertyDefinition</c> is unique over
    /// <c>(PortalID, ModuleDefID, PropertyName)</c>, so every racer reads "not declared" before any of them
    /// commits and the losers are refused by the index rather than by the read. The scope of what a contest
    /// fact asserts, and why it does not assert which mechanism refused a given caller, is recorded on
    /// <see cref="CreateUser_SubmittedConcurrentlyUnderOneLoginName_CreatesItOnceWithoutAnyServerFault"/>.
    /// </remarks>
    [Fact]
    public async Task CreateProfileDefinition_SubmittedConcurrentlyUnderOneName_DeclaresItOnceWithoutAnyServerFault()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string contestedName = "ITestRace" + Suffix();
        const int Callers = 10;

        IEnumerable<Task<HttpResponseMessage>> submissions = Enumerable.Range(0, Callers).Select(_ =>
        {
            CreateProfilePropertyDefinitionRequest request = NewProfileDefinition(required: false);
            request.PropertyName = contestedName;

            return client.PostAsJsonAsync(
                new Uri("/api/v1/profile-definitions", UriKind.Relative),
                request,
                ApiTestFixture.Json);
        });

        HttpResponseMessage[] responses = await Task.WhenAll(submissions);

        try
        {
            HttpStatusCode[] statuses = [.. responses.Select(response => response.StatusCode)];

            statuses.Should().NotContain(
                status => (int)status >= 500,
                "a unique index refusing a duplicate declaration is not a server fault");

            statuses.Count(status => status == HttpStatusCode.Created).Should().Be(1);
            statuses.Where(status => status != HttpStatusCode.Created).Should()
                .AllBeEquivalentTo(HttpStatusCode.Conflict);

            int declared = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[ProfilePropertyDefinition] "
                + "WHERE [PropertyName] = @name AND [PortalID] = @portalId;",
                new Dictionary<string, object?>
                {
                    ["name"] = contestedName,
                    ["portalId"] = _fixture.Seed.PortalId,
                });

            declared.Should().Be(
                1,
                "one declaration may survive the contest; a second would make the catalogue ambiguous for "
                + "every account whose values reference it by name");
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
    /// Tenant-authored validation expressions are length-bounded and must compile before they are persisted.
    /// </summary>
    [Fact]
    public async Task CreateProfileDefinition_WithUnsafeValidationExpression_ReturnsBadRequest()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        Uri route = new(
            "/api/v1/profile-definitions",
            UriKind.Relative);

        CreateProfilePropertyDefinitionRequest malformed = NewProfileDefinition(required: false);
        malformed.ValidationExpression = "([";

        using HttpResponseMessage malformedResponse = await host.PostAsJsonAsync(
            route,
            malformed,
            ApiTestFixture.Json);
        malformedResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        CreateProfilePropertyDefinitionRequest excessive = NewProfileDefinition(required: false);
        excessive.ValidationExpression = new string('x', 513);

        using HttpResponseMessage excessiveResponse = await host.PostAsJsonAsync(
            route,
            excessive,
            ApiTestFixture.Json);
        excessiveResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The canonical definition address serves the whole action set against the tenant the request resolves.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// <c>/api/v1/profile-definitions</c> is the address the contract froze, and every action is reachable
    /// through it: create, read the member, read the collection, update and remove. The returned portal
    /// identifier establishes that the operation acted on the RESOLVED tenant rather than on a default.
    /// </para>
    /// <para>
    /// The definition is removed at the end. Definitions are portal schema, so leaving one behind would
    /// change what every other profile fact in this suite sees.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CanonicalProfileDefinitionAddress_ServesTheResolvedTenantAcrossItsActionSet()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        var collection = new Uri("/api/v1/profile-definitions", UriKind.Relative);

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            collection,
            NewProfileDefinition(required: false),
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        // Read through the ENVELOPE, which is what every payload-bearing success in this API publishes.
        // Deserialising an envelope directly as its payload type yields a non-null object with every
        // member unset, so a raw read here would compare defaults and pass or fail for the wrong reason.
        ProfilePropertyDefinitionDto? definition = await created.Content
            .ReadEnvelopeAsync<ProfilePropertyDefinitionDto>();

        definition.Should().NotBeNull();
        definition!.PortalId.Should().Be(_fixture.Seed.PortalId,
            "the flat address declares the definition for the tenant the request resolved to");

        created.Headers.Location.Should().NotBeNull();
        created.Headers.Location!.OriginalString
            .Should().Be($"/api/v1/profile-definitions/{Route(definition.PropertyDefinitionId)}",
                "the location is built from the address the caller used, so a flat create must not answer a nested one");

        var itemRoute = new Uri(
            $"/api/v1/profile-definitions/{Route(definition.PropertyDefinitionId)}",
            UriKind.Relative);

        using HttpResponseMessage read = await client.GetAsync(itemRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage listed = await client.GetAsync(collection);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<ProfilePropertyDefinitionDto>? all = await listed.Content
            .ReadFromJsonAsync<CollectionEnvelope<ProfilePropertyDefinitionDto>>(ApiTestFixture.Json);

        all.Should().NotBeNull();
        all!.Data.Should().NotBeNull();
        all.Data!.Select(item => item.PropertyDefinitionId)
            .Should().Contain(definition.PropertyDefinitionId);

        UpdateProfilePropertyDefinitionRequest amendment = AmendmentFrom(definition);
        amendment.PropertyCategory = "Contact";

        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            itemRoute,
            amendment,
            ApiTestFixture.Json);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        ProfilePropertyDefinitionDto? afterUpdate = await updated.Content
            .ReadEnvelopeAsync<ProfilePropertyDefinitionDto>();

        afterUpdate.Should().NotBeNull();
        afterUpdate!.PropertyCategory.Should().Be("Contact");

        // The canonical item address resolves the same row the collection action created.
        using HttpResponseMessage throughCanonical = await client.GetAsync(
            ProfileDefinitionRoute(_fixture.Seed.PortalId, definition.PropertyDefinitionId));

        throughCanonical.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage removed = await client.DeleteAsync(itemRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage gone = await client.GetAsync(itemRoute);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The definition collection on another tenant's host is refused a portal administrator, while its own
    /// host is served. The pair makes the refusal attributable to the resolved tenant rather than to the
    /// caller's standing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The tenant a request runs under is resolved from its host header. The flat definition family carries no
    /// competing portal identity, so a token for the seeded portal sent to another portal's host is refused
    /// before the service is reached.
    /// </remarks>
    [Fact]
    public async Task ListProfileDefinitions_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IsolatedPortal other = await CreateIsolatedPortalAsync(host);

        using HttpClient ownClient = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage own = await ownClient.GetAsync(
            new Uri("/api/v1/profile-definitions", UriKind.Relative));

        own.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpClient foreignClient = await _fixture.CreateAdministratorClientAsync();
        foreignClient.BaseAddress = new Uri($"http://{other.Alias}", UriKind.Absolute);

        using HttpResponseMessage response = await foreignClient.GetAsync(
            new Uri("/api/v1/profile-definitions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A second definition bearing an existing name is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateProfileDefinition_WithDuplicateName_ReturnsConflict()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        ProfilePropertyDefinitionDto first = await CreateProfileDefinitionAsync(client, required: false);

        CreateProfilePropertyDefinitionRequest duplicate = NewProfileDefinition(required: false);
        duplicate.PropertyName = first.PropertyName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/profile-definitions", UriKind.Relative),
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
    public async Task MembershipSettings_WithoutAUserAccountsModule_ReadsDefaultsAndRefusesTheWrite()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // A tenant of its own, so the assertion cannot be disturbed by another test installing the module
        // instance into the shared seeded tenant.
        IsolatedPortal isolated = await CreateIsolatedPortalAsync(client);
        using HttpClient isolatedClient = await _fixture.CreateHostClientAsync(isolated.Alias);

        // THE READ ANSWERS 200 WITH THE LEGACY DEFAULTS, FLAGGED AS UNSTORED. It used to answer 404, which was
        // untrue - the tenant exists, so its settings resource does - and runtime testing measured the damage:
        // four screens that read this document only to decide which columns to render put a screen-level "not
        // found" alert above healthy content, and the settings screen disabled its own save control against a
        // healthy server. The legacy reader applied a measured default for every absent key, so defaults are
        // the behaviour-preserving answer; the absence of a STORE now travels inside the document.
        using HttpResponseMessage read = await isolatedClient.GetAsync(MembershipSettingsRoute(isolated.PortalId));
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        ApiResponse<MembershipSettingsDto>? document =
            await read.Content.ReadFromJsonAsync<ApiResponse<MembershipSettingsDto>>(ApiTestFixture.Json);

        document!.Data!.IsStored.Should().BeFalse("this tenant has no settings source");
        document.Data.RecordsPerPage.Should().Be(10, "the measured legacy default applies");

        using HttpResponseMessage written = await isolatedClient.PutAsJsonAsync(
            MembershipSettingsRoute(isolated.PortalId),
            new MembershipSettingsDto(),
            ApiTestFixture.Json);

        // THE WRITE ANSWERS 409, NOT 404, because the read for this very address answers 200: a caller cannot
        // act on an API that says a resource both exists and does not. What is absent is the store, which is a
        // state conflict the operator can repair, and the detail names the repair.
        written.StatusCode.Should().Be(HttpStatusCode.Conflict);

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
        await EnsureUserAccountsModuleAsync();

        Uri route = MembershipSettingsRoute(_fixture.Seed.PortalId);

        using HttpResponseMessage read = await client.GetAsync(route);
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        MembershipSettingsDto? defaults = await read.Content
            .ReadEnvelopeAsync<MembershipSettingsDto>();

        defaults.Should().NotBeNull();

        // The defaults are the legacy ones, applied when no stored setting exists.
        defaults!.RecordsPerPage.Should().Be(10);
        defaults.ColumnDisplayName.Should().BeTrue();
        defaults.ColumnFirstName.Should().BeFalse();

        // THE WRITE BODY IS ITS OWN CONTRACT. The read answers with the projection; the write states the
        // whole set explicitly, which is why the two are separate types and why the values below are set on a
        // request rather than on the object that was just read back.
        var desired = new UpdateMembershipSettingsRequest
        {
            RecordsPerPage = 25,
            ColumnFirstName = true,
            ColumnDisplayName = false,
            DisplayMode = 1,
            ProfileDefaultVisibility = 1,
        };

        // ⚠ 200 WITH A BODY, WHERE EVERY OTHER SETTINGS WRITE ANSWERS 204. This one write can rewrite every
        // account's display name in the tenant, and the caller cannot predict from the request whether it
        // did or how many it touched - so the report travels back on the response rather than being lost.
        using HttpResponseMessage written = await client.PutAsJsonAsync(route, desired, ApiTestFixture.Json);
        written.StatusCode.Should().Be(HttpStatusCode.OK);

        MembershipSettingsUpdateResultDto? report = await written.Content
            .ReadEnvelopeAsync<MembershipSettingsUpdateResultDto>();

        report.Should().NotBeNull();
        report!.DisplayNameFormatChanged.Should().BeFalse(
            "this write left the display-name format exactly as it was");
        report.DisplayNamesRewritten.Should().Be(0);

        using HttpResponseMessage reread = await client.GetAsync(route);
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        MembershipSettingsDto? persisted = await reread.Content
            .ReadEnvelopeAsync<MembershipSettingsDto>();

        persisted.Should().NotBeNull();
        persisted!.RecordsPerPage.Should().Be(25);
        persisted.ColumnFirstName.Should().BeTrue();
        persisted.ColumnDisplayName.Should().BeFalse();
        persisted.DisplayMode.Should().Be(1);
        persisted.ProfileDefaultVisibility.Should().Be(1);
    }

    /// <summary>
    /// Membership settings reject unsupported discriminators, null policy text and redirect pages owned by a
    /// different portal.
    /// </summary>
    [Fact]
    public async Task MembershipSettings_RejectMalformedValuesAndForeignRedirects()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        await EnsureUserAccountsModuleAsync();
        Uri route = MembershipSettingsRoute(_fixture.Seed.PortalId);

        using HttpResponseMessage malformed = await host.PutAsJsonAsync(
            route,
            new MembershipSettingsDto
            {
                DisplayMode = 3,
                RecordsPerPage = 0,
                ProfileDefaultVisibility = -1,
                SecurityUsersControl = 2,
                SecurityEmailValidation = null!,
                SecurityDisplayNameFormat = null!,
            },
            ApiTestFixture.Json);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IsolatedPortal otherPortal = await CreateIsolatedPortalAsync(host);
        int otherPortalId = otherPortal.PortalId;
        int foreignHomeTabId = await _fixture.Database.ScalarAsync<int>(
            "SELECT [HomeTabId] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = otherPortalId });

        using HttpResponseMessage foreignRedirect = await host.PutAsJsonAsync(
            route,
            new MembershipSettingsDto { RedirectAfterLogin = foreignHomeTabId },
            ApiTestFixture.Json);
        foreignRedirect.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await foreignRedirect.Content.ReadAsStringAsync()).Should().Contain("does not belong");
    }

    /// <summary>
    /// The refusal is a FIELD-LEVEL problem document naming every offending member, which is what proves a
    /// validator resolved for the concrete body type at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: KEPT ALONGSIDE THE FACT ABOVE RATHER THAN FOLDED INTO IT, BECAUSE THE TWO ASSERT
    /// DIFFERENT FAILURES. The fact above asserts the STATUS - that a malformed body and a foreign redirect
    /// are both refused - and would still pass if the refusal came from the service with a single opaque
    /// message. This one asserts the SHAPE: one entry per offending member, which can only happen if the
    /// pipeline resolved a validator for the type the action binds. An endpoint that advertises a
    /// field-error response and has no registered validator fails this and nothing else.
    /// <para>
    /// The bound it exercises is named on the validator of the WRITE shape. It was written against a
    /// validator declared on the read projection, which no endpoint binds and which is therefore withdrawn;
    /// the bound itself is unchanged.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MembershipSettings_WithAnInvalidShape_NamesEveryOffendingField()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        await EnsureUserAccountsModuleAsync();

        var invalid = new MembershipSettingsDto
        {
            DisplayMode = 3,
            RecordsPerPage = 0,
            ProfileDefaultVisibility = 3,
            SecurityUsersControl = 2,
            SecurityDisplayNameFormat =
                new string('x', UpdateMembershipSettingsRequestValidator.MaximumSettingValueLength + 1),
            SecurityEmailValidation = "[",
        };

        using HttpResponseMessage response = await host.PutAsJsonAsync(
            MembershipSettingsRoute(_fixture.Seed.PortalId),
            invalid,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey(nameof(UpdateMembershipSettingsRequest.DisplayMode));
        problem.Errors.Should().ContainKey(nameof(UpdateMembershipSettingsRequest.RecordsPerPage));
        problem.Errors.Should().ContainKey(nameof(UpdateMembershipSettingsRequest.ProfileDefaultVisibility));
        problem.Errors.Should().ContainKey(nameof(UpdateMembershipSettingsRequest.SecurityUsersControl));
        problem.Errors.Should().ContainKey(nameof(UpdateMembershipSettingsRequest.SecurityDisplayNameFormat));
        problem.Errors.Should().ContainKey(nameof(UpdateMembershipSettingsRequest.SecurityEmailValidation));
    }

    /// <summary>
    /// Adopting a display-name format rewrites the tenant's existing accounts, reports how many names it
    /// changed, and leaves them alone when the same format is submitted again.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: reproduces <c>Website/admin/Users/UserSettings.ascx.vb:L175-L182</c>, which compared the
    /// submitted format against the stored one and spawned <c>UserController.UpdateDisplayNames</c>
    /// (<c>Library/Components/Users/UserController.vb:L1259-L1268</c>) when they differed. The legacy sweep
    /// ran on a background thread and reported nothing; here it is part of the same write and the count comes
    /// back on the response, which is why this endpoint answers 200 with a body rather than 204. The
    /// divergence is recorded in MIGRATION_NOTES.md.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task MembershipSettings_AdoptingADisplayNameFormat_RewritesTheTenantsAccounts()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        await EnsureUserAccountsModuleAsync();

        UserDetailDto created = await CreateUserAsync(client);
        created.DisplayName.Should().StartWith("Integration Account ");

        Uri route = MembershipSettingsRoute(_fixture.Seed.PortalId);

        // A format bearing a random discriminator, so this fact cannot be satisfied by a value another fact
        // in this suite happened to leave behind.
        string marker = Suffix();
        string format = "[LASTNAME], [FIRSTNAME] <" + marker + ">";

        using HttpResponseMessage adopted = await client.PutAsJsonAsync(
            route,
            new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = format },
            ApiTestFixture.Json);

        adopted.StatusCode.Should().Be(HttpStatusCode.OK);

        MembershipSettingsUpdateResultDto? report = await adopted.Content
            .ReadEnvelopeAsync<MembershipSettingsUpdateResultDto>();

        report.Should().NotBeNull();
        report!.DisplayNameFormatChanged.Should().BeTrue();
        report.DisplayNamesRewritten.Should().BeGreaterThan(
            0,
            "the tenant holds at least the account this fact created");

        using HttpResponseMessage reread = await client.GetAsync(
            UserRoute(_fixture.Seed.PortalId, created.UserId));
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        UserDetailDto rewritten = await ReadDetailAsync(reread);
        rewritten.DisplayName.Should().Be("Account, Integration <" + marker + ">");

        // Submitting the same format again is not a change, so nothing is swept - which is the legacy
        // comparison, and what stops an operator saving the settings screen from rewriting the whole tenant.
        using HttpResponseMessage resubmitted = await client.PutAsJsonAsync(
            route,
            new UpdateMembershipSettingsRequest { SecurityDisplayNameFormat = format },
            ApiTestFixture.Json);

        resubmitted.StatusCode.Should().Be(HttpStatusCode.OK);

        MembershipSettingsUpdateResultDto? unchanged = await resubmitted.Content
            .ReadEnvelopeAsync<MembershipSettingsUpdateResultDto>();

        unchanged!.DisplayNameFormatChanged.Should().BeFalse();
        unchanged.DisplayNamesRewritten.Should().Be(0);
    }

    /// <summary>
    /// Each listed account carries whether the removal operation will accept it, and the two answers agree
    /// with what the operation actually does.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: the legacy grid decided this in markup at
    /// <c>Website/admin/Users/Users.ascx.vb:L691-L692</c>, hiding the command for the tenant's designated
    /// administrator. The capability now travels on the row, and this fact pins it to the operation rather
    /// than to itself: the protected row is offered no command AND is refused when one is issued anyway.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListUsers_PublishesADeletionCapabilityThatMatchesWhatDeletionDoes()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        UserDetailDto ordinary = await CreateUserAsync(client);

        // Both rows are reached by NAME FILTER rather than by reading the unfiltered first page. Every other
        // fact in this suite adds accounts to the same tenant, so a positional read would become
        // order-dependent and eventually stop finding what it asserts about.
        PagedEnvelope<UserListItemDto> members = await ListAsync(
            client,
            "userNameFilter=" + Uri.EscapeDataString(ordinary.Username));

        members.Items.Should().ContainSingle(row => row.UserId == ordinary.UserId);
        members.Items.Single(row => row.UserId == ordinary.UserId)
            .CanDelete.Should().BeTrue("an ordinary account of the tenant may be removed");

        PagedEnvelope<UserListItemDto> administrators = await ListAsync(
            client,
            "userNameFilter=" + Uri.EscapeDataString(IntegrationSeed.AdminUserName));

        UserListItemDto designated = administrators.Items
            .Should().ContainSingle(row => row.UserId == _fixture.Seed.AdminUserId)
            .Subject;

        designated.CanDelete.Should().BeFalse(
            "the tenant designates this account as its administrator");

        // The capability is advisory; the operation is the enforcement. Issuing the command anyway must be
        // refused, which is what makes the withheld affordance honest rather than merely cosmetic.
        using HttpResponseMessage refused = await client.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, _fixture.Seed.AdminUserId));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage accepted = await client.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, ordinary.UserId));

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
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

    /// <summary>Reads one page of the seeded tenant's accounts under an explicit filter.</summary>
    /// <param name="client">A client entitled to enumerate accounts.</param>
    /// <param name="filter">
    /// The filter portion of the query string, without a leading separator. Pass an empty string for the
    /// unfiltered collection.
    /// </param>
    /// <returns>The page the endpoint served.</returns>
    /// <remarks>
    /// Every caller supplies the paging coordinates through this one helper, and it states them explicitly
    /// rather than relying on the contract's defaults, so a change to a default cannot quietly move which page
    /// a filter assertion is reading. The page size is a stated request value and NOT the legacy
    /// <c>Records_PerPage</c> portal setting that <c>Website/admin/Users/Users.ascx.vb:L114</c> read, which is
    /// why no assertion in this suite pins a page size: that setting is a presentation preference in the
    /// membership projection, not a bound on the collection endpoint.
    /// </remarks>
    private async Task<PagedEnvelope<UserListItemDto>> ListAsync(HttpClient client, string filter)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(filter);

        string query = filter.Length == 0 ? string.Empty : "&" + filter;

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/users?pageIndex=0&pageSize=100" + query,
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!;
    }

    /// <summary>
    /// Posts a LITERAL JSON document to the body-bound search and returns the page it answered.
    /// </summary>
    /// <param name="client">An authenticated client.</param>
    /// <param name="json">The exact request body to send.</param>
    /// <returns>The page the search answered.</returns>
    /// <remarks>
    /// The body is a raw literal rather than a serialised request object on purpose. Serialising the request
    /// type would apply this assembly's own converter policy, so the bytes on the wire would be whatever the
    /// server already accepts and a wire-form mismatch would be untestable - which is exactly how the sort
    /// direction came to have one spelling in a query string and another in a body.
    /// </remarks>
    private static async Task<PagedEnvelope<UserListItemDto>> SearchWithRawBodyAsync(
        HttpClient client,
        string json)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(json);

        using var body = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/v1/users/search", UriKind.Relative),
            body);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the body the client actually sends must bind: {0}",
            await response.Content.ReadAsStringAsync());

        PagedEnvelope<UserListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!;
    }

    /// <summary>
    /// The account-owner policy admits the account holder only WITHIN THE TENANT ITS TOKEN NAMES: the same
    /// account key addressed under another tenant's route is refused on the account detail, the profile read,
    /// the profile write and the credential change alike.
    /// </summary>
    /// <param name="method">The verb under test.</param>
    /// <param name="routeSuffix">The suffix appended to the account route, empty for the account detail.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THE DEFECT THIS PINS. An account key is installation-wide - <c>dbo.Users</c> carries no portal column
    /// and membership is a row in <c>dbo.UserPortals</c> - while every route in this family names a portal.
    /// The owner arm of the policy compared only the SUBJECT claim against the route's account, so a token
    /// minted in one tenant satisfied it against any tenant's route for the same account, and the
    /// administrator arm could not refuse what the owner arm had already granted. The responses are
    /// portal-scoped projections, so that is a cross-tenant read and write rather than a theoretical one.
    /// </para>
    /// <para>
    /// THE ANSWER MUST BE 403, NOT 404, AND THE DIFFERENCE IS THE WHOLE ASSERTION. Before the tenant
    /// comparison existed these requests passed the policy and were refused further down by the service,
    /// which reads the account WITH its membership and so answered not-found for an account that is not a
    /// member of the addressed tenant. That is a refusal by accident: it depends on the account not being a
    /// member of the second tenant, and a real DotNetNuke installation is full of accounts that belong to
    /// several. A forbidden answer proves the request never reached the service at all.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/profile")]
    [InlineData("PUT", "/profile")]
    [InlineData("POST", "/password")]
    public async Task AccountRoutes_AsTheHolderButUnderAnotherTenant_ReturnForbidden(
        string method,
        string routeSuffix)
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        // A second tenant, which the account is NOT a member of and whose administrator it is not.
        IsolatedPortal otherPortal = await CreateIsolatedPortalAsync(administrator);

        // The account's own token, minted for the tenant it actually belongs to.
        using HttpClient holder = await ClientForAccountAsync(created);

        // Its own account key, addressed under the OTHER tenant. The account family is addressed by its
        // canonical flat route, so the tenant is selected by the ARRIVAL HOST rather than by a path
        // segment - which makes the alias the thing this fact varies, and is the only way the cross-tenant
        // attempt can be expressed on this surface.
        holder.BaseAddress = new Uri($"http://{otherPortal.Alias}", UriKind.Absolute);

        var route = new Uri(
            $"/api/v1/users/{Route(created.UserId)}{routeSuffix}",
            UriKind.Relative);

        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        // The bodies below are well formed and would be accepted on the account's own tenant, which is what
        // makes the refusal attributable to the policy rather than to a malformed request. Authorisation runs
        // before model binding matters, so nothing here needs to be more than deserialisable.
        if (routeSuffix.Equals("/profile", StringComparison.Ordinal))
        {
            // Deliberately carries no portal identifier: the profile contract has none, because the portal
            // scope lives on the property definition and the route is what names the tenant here.
            request.Content = JsonContent.Create(
                new UserProfileDto
                {
                    UserId = created.UserId,
                    Properties = Array.Empty<UserProfileValueDto>(),
                },
                options: ApiTestFixture.Json);
        }
        else if (routeSuffix.Equals("/password", StringComparison.Ordinal))
        {
            // The credential route is the one member of this family whose requirement forbids the
            // administrator fallback, so the owner arm is the ONLY arm that can grant it. Before the tenant
            // comparison existed, a token minted in one tenant could therefore overwrite its own credential
            // through another tenant's route - and the credential store is installation-wide, so that write
            // was not even portal-scoped.
            request.Content = JsonContent.Create(
                new ChangePasswordRequest
                {
                    Operation = ChangePasswordRequest.OperationChange,
                    CurrentPassword = ApiTestFixture.KnownPassword,
                    NewPassword = ReplacementPassword,
                    ConfirmPassword = ReplacementPassword,
                },
                options: ApiTestFixture.Json);
        }

        using HttpResponseMessage response = await holder.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the owner arm admits the holder only within the tenant its token names");
    }

    /// <summary>
    /// The same three routes ARE served for the account holder within its own tenant, which is what proves
    /// the tenant comparison closes the cross-tenant path without closing self-service.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AccountRoutes_AsTheHolderWithinItsOwnTenant_AreServed()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        using HttpClient holder = await ClientForAccountAsync(created);

        using HttpResponseMessage detail = await holder.GetAsync(new Uri(
            $"/api/v1/users/{Route(created.UserId)}",
            UriKind.Relative));

        detail.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage profile = await holder.GetAsync(new Uri(
            $"/api/v1/users/{Route(created.UserId)}/profile",
            UriKind.Relative));

        profile.StatusCode.Should().Be(HttpStatusCode.OK);

        UserProfileDto? read = await profile.Content.ReadEnvelopeAsync<UserProfileDto>();
        read.Should().NotBeNull();

        using HttpResponseMessage written = await holder.PutAsJsonAsync(
            new Uri(
                $"/api/v1/users/{Route(created.UserId)}/profile",
                UriKind.Relative),
            read!,
            ApiTestFixture.Json);

        // The profile replacement answers 204 rather than 200: it returns no body, which is what the
        // endpoint's own response declaration states.
        written.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The credential route is the strict member of this family - its requirement forbids the
        // administrator fallback - so proving it is served for the holder within its own tenant is what
        // establishes that the tenant comparison closed the cross-tenant path without closing self-service on
        // the one route that has no other way in.
        using HttpResponseMessage credential = await holder.PostAsJsonAsync(
            new Uri(
                $"/api/v1/users/{Route(created.UserId)}/password",
                UriKind.Relative),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = ReplacementPassword,
                ConfirmPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        credential.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// The account holder learns the tenant's per-property visibility decision from its OWN profile, on the
    /// same request that carries the profile, while the settings endpoint that also declares it refuses them.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THE REFUSAL IS ASSERTED IN THE SAME CASE AS THE SUCCESS, AND THAT PAIRING IS THE WHOLE POINT.
    /// <c>Website/admin/Users/Profile.ascx.vb:L58-L63</c> offered the per-value visibility control when the
    /// tenant's <c>Profile_DisplayVisibility</c> setting was on AND the viewer was the subject of the
    /// profile. The migrated screen read that setting from <c>GET api/v1/users/settings</c>, which carries
    /// <c>PolicyNames.PortalAdministrator</c> - so the second half of the legacy predicate guaranteed the
    /// first half could never be read. An ordinary holder was answered 403, the policy stayed unresolved,
    /// and the control was never offered however the tenant had configured it. The setting was stored,
    /// published and inert.
    /// </para>
    /// <para>
    /// Asserting only that the profile now carries the member would not catch a regression that moved the
    /// fact back onto the administrator-only endpoint, because such a change would leave this member
    /// present and merely stale. Asserting the 403 alongside it is what pins the reason the member exists
    /// here at all.
    /// </para>
    /// <para>
    /// The tenant is left at whatever it has stored rather than being reconfigured, so the assertion is
    /// that the fact TRAVELS and is a real boolean - not that it holds a particular value, which would make
    /// this case depend on fixture state it does not own. The stored-both-ways behaviour is pinned by
    /// <c>GetProfile_PublishesTheTenantsVisibilityAffordanceDecision</c> in the unit suite, where the
    /// settings source can be controlled directly.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Profile_AsTheHolder_CarriesTheVisibilityDecisionTheSettingsEndpointRefusesThem()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();
        UserDetailDto created = await CreateUserAsync(administrator);

        using HttpClient holder = await ClientForAccountAsync(created);

        using HttpResponseMessage profile = await holder.GetAsync(
            ProfileRoute(0, created.UserId));

        profile.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read from the RAW payload rather than only through the typed envelope, because a
        // deserialised bool cannot distinguish "the member was absent and defaulted" from "the member
        // was present and false" - and an absent member is exactly the regression this case guards.
        using JsonDocument document = JsonDocument.Parse(
            await profile.Content.ReadAsStringAsync());

        JsonElement data = document.RootElement.GetProperty("data");

        data.TryGetProperty("displayVisibilityEnabled", out JsonElement decision)
            .Should().BeTrue("the holder has no other way to learn the tenant's decision");
        decision.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);

        // The same fact, asked for where the migrated screen used to ask for it.
        using HttpResponseMessage settings = await holder.GetAsync(MembershipSettingsRoute(0));

        settings.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "which is why the decision has to travel on the profile the holder may read");
    }

    /// <summary>Mints a client authenticated as one account, for the self-service paths.</summary>
    /// <param name="account">The account to act as.</param>
    /// <returns>A client presenting the token the API issued to that account.</returns>
    /// <remarks>
    /// <para>
    /// The account is one this suite created, and the credential is the one it supplied while creating it, so
    /// the sign-in additionally proves the create path stored a credential the login path can verify - which
    /// is the half of the create contract no response body can show.
    /// </para>
    /// <para>
    /// No role is asserted, because none would help: the self-service credential and profile endpoints are
    /// gated on SUBJECT-versus-ROUTE equality rather than on a role, so no role would admit this caller and
    /// none is needed to.
    /// </para>
    /// </remarks>
    private Task<HttpClient> ClientForAccountAsync(UserDetailDto account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return _fixture.CreateClientForAsync(
            account.Username ?? string.Empty,
            ApiTestFixture.KnownPassword);
    }

    /// <summary>Creates a profile property definition through the API, in the client's OWN tenant.</summary>
    /// <param name="client">A client holding the administrators role, addressed at the owning tenant.</param>
    /// <param name="required">Whether the property must be supplied.</param>
    /// <returns>The created definition.</returns>
    /// <remarks>
    /// MIGRATION: THE OWNING TENANT IS THE CLIENT'S ADDRESS, NOT AN ARGUMENT. The declaration route is flat -
    /// <c>api/v1/profile-definitions</c> names no portal - so the endpoint takes the tenant from the host the
    /// request arrived at. This helper used to accept a portalId and DISCARD it, which quietly made every
    /// call create a declaration in the caller's own tenant: two facts about cross-tenant isolation set
    /// themselves up by asking for a declaration in another portal, received one in the seeded portal, and
    /// then asserted isolation against a declaration that was never foreign. Both now pass a client addressed
    /// at the tenant they mean, and the parameter that could lie is gone.
    /// </remarks>
    private async Task<ProfilePropertyDefinitionDto> CreateProfileDefinitionAsync(
        HttpClient client,
        bool required)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/profile-definitions", UriKind.Relative),
            NewProfileDefinition(required),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        ProfilePropertyDefinitionDto? created = await response.Content
            .ReadEnvelopeAsync<ProfilePropertyDefinitionDto>();

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>Inserts one profile value for test setup.</summary>
    private Task InsertProfileValueAsync(int userId, int propertyDefinitionId, string value) =>
        _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[UserProfile]
                ([UserID], [PropertyDefinitionID], [PropertyValue], [PropertyText], [Visibility], [LastUpdatedDate])
            VALUES (@userId, @definitionId, @value, NULL, 0, SYSUTCDATETIME());
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = userId,
                ["definitionId"] = propertyDefinitionId,
                ["value"] = value,
            });

    /// <summary>Counts one account/definition profile-value pair.</summary>
    private Task<int> CountProfileValuesAsync(int userId, int propertyDefinitionId) =>
        _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[UserProfile]
            WHERE [UserID] = @userId AND [PropertyDefinitionID] = @definitionId;
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = userId,
                ["definitionId"] = propertyDefinitionId,
            });

    /// <summary>
    /// Makes sure the seeded tenant holds a "User Accounts" module instance, which is where the membership
    /// settings projection is stored.
    /// </summary>
    /// <remarks>
    /// SEC-007: both the definition and its module instance are written directly and idempotently. The package
    /// is administrative and therefore MUST NOT be placeable through the ordinary module-creation catalogue;
    /// direct setup models host installation without weakening the API boundary this test is meant to preserve.
    /// </remarks>
    /// <returns>A task representing the setup.</returns>
    private async Task EnsureUserAccountsModuleAsync()
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

        // SEC-007: the instance is written directly rather than posted to /api/v1/modules. The package is
        // administrative, so it must not be placeable through the ordinary module-creation catalogue, and a
        // setup step that used the catalogue would weaken exactly the boundary these tests assert. No pane,
        // alignment, colour or border value is written either: those columns are appearance concerns that
        // AAP 0.2.2.1 and 0.2.2.4 place out of scope, and no request contract declares them.
        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[Modules]
                ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted],
                 [InheritViewPermissions])
            VALUES (@definitionId, @portalId, N'User Accounts', 0, 0, 0);
            """,
            new Dictionary<string, object?>
            {
                ["definitionId"] = definitionId,
                ["portalId"] = _fixture.Seed.PortalId,
            });
    }

    /// <summary>Creates a tenant of this test's own, so a tenant-wide assertion cannot be disturbed.</summary>
    /// <param name="client">A client carrying host credentials.</param>
    /// <returns>The new tenant's identifier.</returns>
    private async Task<IsolatedPortal> CreateIsolatedPortalAsync(HttpClient client)
    {
        string suffix = Suffix();
        string alias = "users-" + suffix + ".local";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "User Suite Portal " + suffix,
                portalAlias = alias,
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

        // The created representation travels inside the shared success envelope, so the identifier is one
        // level down under "data". Read as raw JSON rather than through a typed envelope because only the
        // one member is wanted, and naming it here proves the envelope member name as a side effect.
        int portalId = document.RootElement
            .GetProperty("data")
            .GetProperty("portalId")
            .GetInt32();

        return new IsolatedPortal(portalId, alias);
    }

    /// <summary>A tenant created by this suite and the host name that resolves it.</summary>
    /// <param name="PortalId">The tenant identifier.</param>
    /// <param name="Alias">The exact portal alias used as the request host.</param>
    private sealed record IsolatedPortal(int PortalId, string Alias);

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
    private static CreateProfilePropertyDefinitionRequest NewProfileDefinition(bool required) => new()
    {
        DataType = 0,
        PropertyCategory = "Address",
        PropertyName = "ITestCity" + Suffix(),
        Length = 50,
        Required = required,
        ViewOrder = 1,
        Visible = true,
        DefaultValue = string.Empty,
    };

    /// <summary>
    /// Builds the amendment a caller sends to the update verb, seeded from a definition it has just read.
    /// </summary>
    /// <param name="read">The definition the caller read back.</param>
    /// <returns>An amendment carrying the same values, ready to be modified and put.</returns>
    /// <remarks>
    /// MIGRATION: the update verb binds <c>UpdateProfilePropertyDefinitionRequest</c>, which carries the nine
    /// members <c>UpdatePropertyDefinition</c> (<c>04.05.00:L1685</c>) writes and no others. Echoing the
    /// read-back projection would still succeed, because no unmapped-member handling is configured and the
    /// deserialiser ignores what it does not recognise - but it would exercise a wider shape than the boundary
    /// advertises, which is exactly the confusion the split removed. Seeding the amendment from the read is
    /// what keeps a whole-representation PUT from blanking the members a test did not mean to change.
    /// </remarks>
    private static UpdateProfilePropertyDefinitionRequest AmendmentFrom(ProfilePropertyDefinitionDto read) => new()
    {
        DataType = read.DataType,
        PropertyCategory = read.PropertyCategory,
        PropertyName = read.PropertyName,
        Length = read.Length,
        Required = read.Required,
        ValidationExpression = read.ValidationExpression,
        ViewOrder = read.ViewOrder,
        Visible = read.Visible,
        DefaultValue = read.DefaultValue,
    };

    /// <summary>Reads an account representation out of a response, failing the test when it is absent.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The representation.</returns>
    private static async Task<UserDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        UserDetailDto? detail = await response.Content
            .ReadEnvelopeAsync<UserDetailDto>();

        detail.Should().NotBeNull();
        return detail!;
    }

    /// <summary>Builds the canonical account collection route for the resolved tenant.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <returns>A relative route.</returns>
    private static Uri UsersRoute(int _) => new("/api/v1/users", UriKind.Relative);

    /// <summary>
    /// The listing and the single read report the SAME account identically, field for field, for every
    /// member the two contracts share.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The two projections are written out by hand from different sources - the listing from a paged query,
    /// the single read from a keyed one - so nothing but a test keeps them in step. The failure this guards
    /// against was exactly that drift: the listing reported an empty given name, family name and electronic
    /// mail address for every account while the single read reported all three correctly, which no consumer
    /// can reconcile and no compiler can catch.
    /// </para>
    /// <para>
    /// The comparison is driven off the SHARED members rather than a hand-copied list of names, so a member
    /// added to both contracts is compared automatically instead of being silently omitted. The listing's
    /// address and telephone are excluded deliberately: they are profile values the listing composes and the
    /// single read does not carry at all, so they have no counterpart to agree with.
    /// </para>
    /// <para>
    /// The members the tenant WITHHOLDS from a listing are excluded too, and their exclusion is DERIVED from
    /// the tenant's own published settings rather than hard-coded, so the fact cannot drift out of step with
    /// the configuration. A listing minimises the columns the tenant has switched off - it is a screen
    /// projection subject to that tenant's configuration - while the keyed single read is the privileged read
    /// and is not minimised. Requiring the two to agree on a withheld member would be requiring the listing
    /// to publish PII the tenant has switched off, so the fact asserts agreement where the tenant publishes
    /// and asserts the withholding itself where it does not.
    /// </para>
    /// <para>
    /// Reading the flags from <c>GET /api/v1/users/settings</c> is also what demonstrates the answer to the
    /// obvious objection: a withheld value and an unrecorded one are always separable by a caller, because the
    /// flag that distinguishes them is published on the same API.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListUsers_ReportsEachAccountIdenticallyToTheSingleRead()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateUserRequest request = NewUserRequest();

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        UserDetailDto createdUser = await ReadDetailAsync(created);

        try
        {
            PagedEnvelope<UserListItemDto> page = await ListAsync(client, string.Empty);

            UserListItemDto listed = page.Items
                .Should().ContainSingle(item => item.UserId == createdUser.UserId)
                .Subject;

            using HttpResponseMessage read = await client.GetAsync(
                UserRoute(_fixture.Seed.PortalId, createdUser.UserId));

            read.StatusCode.Should().Be(HttpStatusCode.OK);
            UserDetailDto detail = await ReadDetailAsync(read);

            using HttpResponseMessage publishedSettings = await client.GetAsync(
                MembershipSettingsRoute(_fixture.Seed.PortalId));

            publishedSettings.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.OK, HttpStatusCode.NotFound },
                "the settings projection is stored against an accounts module instance, and a tenant that "
                + "holds none legitimately reports no value");

            // A tenant holding no accounts module is answered with the MEASURED LEGACY DEFAULTS, which is
            // what the listing itself resolves for such a tenant - so "no settings row" is a determinate
            // published answer rather than an unknown, and a caller reading 404 here knows exactly which
            // flags are in force.
            MembershipSettingsDto visibility = publishedSettings.StatusCode == HttpStatusCode.OK
                ? (await publishedSettings.Content.ReadEnvelopeAsync<MembershipSettingsDto>())!
                : new MembershipSettingsDto();

            // The listing composes these from the profile tables; the single read does not carry them, so
            // they have no counterpart and are not compared.
            var listingOnly = new List<string>
            {
                nameof(UserListItemDto.Address),
                nameof(UserListItemDto.Telephone),
            };

            // A column the tenant withholds is asserted BELOW as withheld, so it is excluded from the
            // agreement comparison rather than being expected to match the privileged read.
            var withheld = new List<string>();

            void Withholds(bool published, string member)
            {
                if (!published)
                {
                    withheld.Add(member);
                    listingOnly.Add(member);
                }
            }

            static void AssertProjectedText(bool published, string? projected, string? submitted, string member)
            {
                if (published)
                {
                    projected.Should().Be(
                        submitted,
                        FormattableString.Invariant($"{member} is published by this tenant"));
                }
                else
                {
                    projected.Should().BeEmpty(
                        FormattableString.Invariant(
                            $"{member} is withheld by this tenant and must not cross the API boundary"));
                }
            }

            Withholds(visibility.ColumnFirstName, nameof(UserListItemDto.FirstName));
            Withholds(visibility.ColumnLastName, nameof(UserListItemDto.LastName));
            Withholds(visibility.ColumnDisplayName, nameof(UserListItemDto.DisplayName));
            Withholds(visibility.ColumnEmail, nameof(UserListItemDto.Email));
            Withholds(visibility.ColumnCreatedDate, nameof(UserListItemDto.CreatedDate));
            Withholds(visibility.ColumnLastLogin, nameof(UserListItemDto.LastLoginDate));
            Withholds(visibility.ColumnAuthorized, nameof(UserListItemDto.IsApproved));

            IReadOnlyList<PropertyInfo> detailProperties = typeof(UserDetailDto)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance);

            var compared = new List<string>();

            foreach (PropertyInfo listProperty in typeof(UserListItemDto)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (listingOnly.Contains(listProperty.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                PropertyInfo? detailProperty = detailProperties.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, listProperty.Name, StringComparison.Ordinal)
                    && candidate.PropertyType == listProperty.PropertyType);

                if (detailProperty is null)
                {
                    continue;
                }

                object? fromList = listProperty.GetValue(listed);
                object? fromDetail = detailProperty.GetValue(detail);

                fromList.Should().Be(
                    fromDetail,
                    FormattableString.Invariant(
                        $"the listing and the single read must agree on {listProperty.Name}"));

                compared.Add(listProperty.Name);
            }

            // The comparison is only meaningful if it actually reached the members the drift emptied, so every
            // one of the five is accounted for: either compared against the privileged read, or asserted as
            // withheld by the tenant's own published setting. Neither list may simply omit it.
            foreach (string member in new[]
            {
                nameof(UserListItemDto.Username),
                nameof(UserListItemDto.FirstName),
                nameof(UserListItemDto.LastName),
                nameof(UserListItemDto.DisplayName),
                nameof(UserListItemDto.Email),
            })
            {
                (compared.Contains(member, StringComparer.Ordinal)
                    || withheld.Contains(member, StringComparer.Ordinal))
                    .Should().BeTrue(
                        FormattableString.Invariant(
                            $"{member} must be either compared with the single read or withheld by a setting"));
            }

            // Username carries no visibility flag at all, so it is always published and always agrees.
            compared.Should().Contain(nameof(UserListItemDto.Username));
            listed.Username.Should().Be(request.Username);

            // A published column must carry the submitted value rather than merely agree on emptiness; a
            // withheld one must carry the contract's absent value rather than the stored PII.
            AssertProjectedText(
                visibility.ColumnFirstName, listed.FirstName, request.FirstName, nameof(UserListItemDto.FirstName));
            AssertProjectedText(
                visibility.ColumnLastName, listed.LastName, request.LastName, nameof(UserListItemDto.LastName));
            AssertProjectedText(
                visibility.ColumnDisplayName,
                listed.DisplayName,
                request.DisplayName,
                nameof(UserListItemDto.DisplayName));
            AssertProjectedText(
                visibility.ColumnEmail, listed.Email, request.Email, nameof(UserListItemDto.Email));

            if (!visibility.ColumnCreatedDate)
            {
                listed.CreatedDate.Should().BeNull(
                    "a nullable instant the tenant withholds is projected as absent");
            }

            if (!visibility.ColumnLastLogin)
            {
                listed.LastLoginDate.Should().BeNull(
                    "a nullable instant the tenant withholds is projected as absent");
            }

            // At least one column must actually be withheld under the tenant's settings, or this fact would
            // pass without ever exercising the minimisation it exists to pin. The measured legacy defaults
            // switch the given-name, family-name, electronic-mail and last-login columns off, so a tenant that
            // has never configured the accounts module withholds four of them.
            withheld.Should().NotBeEmpty(
                "the minimisation must be exercised, not merely permitted");
        }
        finally
        {
            using HttpResponseMessage removed = await client.DeleteAsync(
                UserRoute(_fixture.Seed.PortalId, createdUser.UserId));

            removed.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

    // =============================================================================================
    // MEMBER SERVICES. The self-service subscription surface that replaces
    // Website/admin/Users/MemberServices.ascx.vb - a catalogue read, subscribe, cancel, trial and the
    // redemption of a role's invitation code, all five addressed by account and all five gated on
    // ownership alone.
    //
    // Every caller below is the ACCOUNT ITSELF, signed in through the real endpoint with the credential the
    // test created it with. That is not incidental: the legacy panel operated on the signed-in account and
    // never on the account its container was managing, so a suite that drove these routes as an
    // administrator would be exercising an affordance the legacy application did not have and would not
    // notice if the ownership policy were dropped.
    // =============================================================================================

    /// <summary>
    /// The catalogue lists the tenant's public roles with this account's own state, and a subscription
    /// round-trips through it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// One fact for the whole round trip on purpose: reading the catalogue, subscribing, re-reading it and
    /// cancelling are one workflow, and asserting them separately would leave the second read's meaning
    /// depending on a sibling test's side effect.
    /// </para>
    /// <para>
    /// The seeded <c>Subscribers</c> role is public with a zero service fee, which is exactly the shape the
    /// legacy screen offered a direct subscription for - <c>objRole.IsPublic And objRole.ServiceFee = 0.0</c>
    /// at <c>MemberServices.ascx.vb:L105</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MemberServices_ListSubscribeAndCancelRoundTripForTheAccountItself()
    {
        (int userId, HttpClient owner) = await CreateOwnedAccountAsync();
        int serviceRoleId = await InsertPublicServiceRoleAsync();

        try
        {
            using (owner)
            {
                IReadOnlyList<MemberServiceDto> before = await ReadServicesAsync(owner, userId);

                MemberServiceDto offer = before.Should()
                    .ContainSingle(row => row.RoleId == serviceRoleId).Subject;
                offer.IsSubscribed.Should().BeFalse("this service is not auto-assigned at account creation");
                offer.SubscriptionAction.Should().Be(MemberServiceActions.Subscribe);
                offer.SubscriptionOffered.Should().BeTrue();
                offer.SubscriptionRequiresPayment.Should().BeFalse("this public role charges nothing");
                offer.TrialOffered.Should().BeFalse("a free service has no trial to take");

                // The seeded Subscribers role is public AND auto-assigned, so a freshly created account
                // already holds it - and the catalogue says so rather than presenting an offer the account
                // has already taken. That is the whole point of carrying the account's own state on the row.
                before.Should()
                    .ContainSingle(row => row.RoleId == _fixture.Seed.SubscribersRoleId).Subject
                    .Should().Match<MemberServiceDto>(row =>
                        row.RoleName == IntegrationSeed.SubscribersRoleName
                        && row.IsSubscribed
                        && row.SubscriptionAction == MemberServiceActions.Unsubscribe);

                before.Should().NotContain(
                    row => row.RoleId == _fixture.Seed.AdministratorRoleId,
                    "the catalogue is the tenant's PUBLIC roles, and the administrator role is not one");

                using HttpResponseMessage subscribed = await owner.PostAsync(
                    ServiceSubscriptionRoute(userId, serviceRoleId),
                    content: null);

                subscribed.StatusCode.Should().Be(HttpStatusCode.NoContent);

                MemberServiceDto held = (await ReadServicesAsync(owner, userId))
                    .Single(row => row.RoleId == serviceRoleId);

                held.IsSubscribed.Should().BeTrue();
                held.SubscriptionAction.Should().Be(MemberServiceActions.Unsubscribe);
                held.IsExpired.Should().BeFalse(
                    "the role names the never-expires frequency, so the derivation stores no bound");

                (await CountAssignmentsAsync(userId, serviceRoleId))
                    .Should().Be(1, "the subscription is one assignment row, written by the delegated primitive");

                using HttpResponseMessage cancelled = await owner.DeleteAsync(
                    ServiceSubscriptionRoute(userId, serviceRoleId));

                cancelled.StatusCode.Should().Be(HttpStatusCode.NoContent);

                (await ReadServicesAsync(owner, userId))
                    .Single(row => row.RoleId == serviceRoleId)
                    .IsSubscribed.Should().BeFalse("the assignment was withdrawn, not merely expired");

                (await CountAssignmentsAsync(userId, serviceRoleId)).Should().Be(0);
            }
        }
        finally
        {
            await RemoveRoleAsync(serviceRoleId);
        }
    }

    /// <summary>
    /// Cancelling a service the account does not hold is reported as absent rather than silently accepted.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The legacy removal returned a bare <c>Boolean</c> and the screen turned <see langword="false"/> into
    /// one undifferentiated message, so a caller could not tell a subscription it did not hold from one it
    /// was not allowed to end. The delegated primitive reports a reason instead, which is what lets this
    /// answer <c>404</c>.
    /// </remarks>
    [Fact]
    public async Task CancelService_ForASubscriptionNotHeld_IsReportedAsAbsent()
    {
        (int userId, HttpClient owner) = await CreateOwnedAccountAsync();
        int serviceRoleId = await InsertPublicServiceRoleAsync();

        try
        {
            using (owner)
            {
                using HttpResponseMessage response = await owner.DeleteAsync(
                    ServiceSubscriptionRoute(userId, serviceRoleId));

                response.StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await response.Content.ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json))!
                    .Type.Should().Contain("role_assignment.not_found");
            }
        }
        finally
        {
            await RemoveRoleAsync(serviceRoleId);
        }
    }

    /// <summary>
    /// The five member-services routes admit NOBODY but the account they name - not an administrator, not a
    /// host account, and not an anonymous caller.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the security boundary the whole surface rests on, and it is asserted for every one of the five
    /// addresses rather than for a representative sample: a route added later that inherited mere
    /// authentication would look identical in the source.
    /// </para>
    /// <para>
    /// The administrator and the host are refused with <c>403</c> rather than admitted, which is a deliberate
    /// narrowing measured from the legacy container: <c>DisplayServices</c>
    /// (<c>ManageUsers.ascx.vb:L61-L66</c>) hid the tab whenever the screen was reached through the
    /// administrative <c>ctl=Edit</c> entry point, and the panel would have acted on the administrator's own
    /// account in any case. An administrator who must change somebody else's membership uses the role
    /// resource, which is tenant administration and a separate, reviewable act.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MemberServices_AdmitNobodyButTheAccountTheyName()
    {
        (int userId, HttpClient owner) = await CreateOwnedAccountAsync();

        using (owner)
        {
            using HttpClient anonymous = _fixture.CreateAnonymousClient();
            using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
            using HttpClient host = await _fixture.CreateHostClientAsync();

            foreach ((HttpMethod method, Uri route) in MemberServiceAddresses(userId))
            {
                using var anonymousRequest = new HttpRequestMessage(method, route);
                using HttpResponseMessage unauthenticated = await anonymous.SendAsync(anonymousRequest);

                unauthenticated.StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized,
                    "{0} {1} must refuse a caller that has not said who it is",
                    method,
                    route);

                foreach (HttpClient other in new[] { administrator, host })
                {
                    using var request = new HttpRequestMessage(method, route);
                    using HttpResponseMessage refused = await other.SendAsync(request);

                    refused.StatusCode.Should().Be(
                        HttpStatusCode.Forbidden,
                        "{0} {1} must refuse a caller that is not the account it names",
                        method,
                        route);
                }
            }
        }
    }

    /// <summary>
    /// An invitation code enrols the account in every role bearing it, published or not, and an unmatched
    /// code is a request the caller can correct.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The role is created and its code written by direct statement because no endpoint publishes a private
    /// role with an invitation code as one operation, and because the point of the fact is precisely that an
    /// UNPUBLISHED role is reachable this way: the legacy handler read
    /// <c>GetPortalRoles(PortalSettings.PortalId)</c> at <c>MemberServices.ascx.vb:L407</c> and applied
    /// neither the public test nor the fee test the grid's own commands applied.
    /// </para>
    /// <para>
    /// The empty and unmatched submissions both answer <c>400</c>, and for different reasons that the problem
    /// type distinguishes: the first is refused by declarative validation, the second by the service. The
    /// legacy screen answered the first with silence and the second with a fixed sentence.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RedeemServiceCode_EnrolsTheAccountInAnUnpublishedRoleAndRefusesAnUnmatchedCode()
    {
        (int userId, HttpClient owner) = await CreateOwnedAccountAsync();
        string code = "itest-code-" + Suffix();

        int privateRoleId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Roles]
                ([PortalID], [RoleName], [Description], [ServiceFee], [BillingPeriod], [BillingFrequency],
                 [TrialFee], [TrialPeriod], [TrialFrequency], [IsPublic], [AutoAssignment], [RSVPCode])
            VALUES (@portalId, @roleName, 'By invitation', 0, 0, 'N', 0, 0, 'N', 0, 0, @code);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["roleName"] = "ITest Founders " + Suffix(),
                ["code"] = code,
            });

        try
        {
            using (owner)
            {
                (await ReadServicesAsync(owner, userId))
                    .Should().NotContain(
                        row => row.RoleId == privateRoleId,
                        "an unpublished role is absent from the catalogue, which is what makes the code the only way in");

                using HttpResponseMessage unmatched = await owner.PostAsJsonAsync(
                    ServiceRedemptionRoute(userId),
                    new RedeemServiceCodeRequest { Code = code + "-wrong" },
                    ApiTestFixture.Json);

                unmatched.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                (await unmatched.Content.ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json))!
                    .Type.Should().Contain("code_not_matched");

                using HttpResponseMessage empty = await owner.PostAsJsonAsync(
                    ServiceRedemptionRoute(userId),
                    new RedeemServiceCodeRequest { Code = "   " },
                    ApiTestFixture.Json);

                empty.StatusCode.Should().Be(
                    HttpStatusCode.BadRequest,
                    "an empty submission is refused rather than matching every codeless role in the tenant");

                using HttpResponseMessage redeemed = await owner.PostAsJsonAsync(
                    ServiceRedemptionRoute(userId),
                    new RedeemServiceCodeRequest { Code = code },
                    ApiTestFixture.Json);

                redeemed.StatusCode.Should().Be(HttpStatusCode.OK);

                RedeemServiceCodeResultDto result =
                    (await redeemed.Content.ReadEnvelopeAsync<RedeemServiceCodeResultDto>())!;

                result.Roles.Should().ContainSingle()
                    .Which.RoleId.Should().Be(privateRoleId);

                (await CountAssignmentsAsync(userId, privateRoleId)).Should().Be(1);
            }
        }
        finally
        {
            await RemoveRoleAsync(privateRoleId);
        }
    }

    /// <summary>
    /// A service the tenant does not publish is refused for self-service subscription even when its
    /// identifier is known.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The first arm of both legacy gates is <c>objRole.IsPublic</c>. The administrator role is the clearest
    /// case available in the seeded tenant: it exists, its identifier is knowable, and self-service
    /// subscription to it would be a privilege escalation.
    /// </remarks>
    [Fact]
    public async Task SubscribeToService_RefusesARoleTheTenantDoesNotPublish()
    {
        (int userId, HttpClient owner) = await CreateOwnedAccountAsync();

        using (owner)
        {
            using HttpResponseMessage response = await owner.PostAsync(
                ServiceSubscriptionRoute(userId, _fixture.Seed.AdministratorRoleId),
                content: null);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json))!
                .Type.Should().Contain("not_offered_forbidden");

            (await CountAssignmentsAsync(userId, _fixture.Seed.AdministratorRoleId))
                .Should().Be(0, "a refused subscription must write nothing");
        }
    }

    /// <summary>
    /// A paid service is refused on both directions, and the catalogue says so before the caller tries.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: the legacy subscribe and cancel paths BOTH ended in
    /// <c>Response.Redirect("~/admin/Sales/PayPalSubscription.aspx?…")</c> for a fee-bearing role
    /// (<c>MemberServices.ascx.vb:L113</c> and <c>:L115</c>, the second appending <c>&amp;cancel=1</c>), and
    /// AAP 0.2.2.4 excludes sales administration. The row is still LISTED - dropping it would silently erase
    /// a tenant's paid offering - and the free trial on the same role remains fully performable, because the
    /// legacy trial gate is the TRIAL fee rather than the service fee.
    /// </remarks>
    [Fact]
    public async Task PaidService_IsListedWithItsTermsButNeitherSubscribedNorCancelledHere()
    {
        (int userId, HttpClient owner) = await CreateOwnedAccountAsync();

        int paidRoleId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Roles]
                ([PortalID], [RoleName], [Description], [ServiceFee], [BillingPeriod], [BillingFrequency],
                 [TrialFee], [TrialPeriod], [TrialFrequency], [IsPublic], [AutoAssignment])
            VALUES (@portalId, @roleName, 'Paid membership', 12.00, 1, 'M', 0, 14, 'D', 1, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["roleName"] = "ITest Premium " + Suffix(),
            });

        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Portals] SET [ProcessorUserID] = @processor WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?>
            {
                ["processor"] = "itest-merchant",
                ["portalId"] = _fixture.Seed.PortalId,
            });

        try
        {
            using (owner)
            {
                MemberServiceDto row = (await ReadServicesAsync(owner, userId))
                    .Single(entry => entry.RoleId == paidRoleId);

                row.ServiceFee.Should().Be(12.00m, "the stored fee is published, not suppressed");
                row.BillingFrequency.Should().Be(Domain.Enums.BillingFrequency.Month);
                row.BillingPeriod.Should().Be(1);
                row.SubscriptionRequiresPayment.Should().BeTrue();
                row.SubscriptionOffered.Should().BeTrue("the tenant has a payment processor account");
                row.TrialOffered.Should().BeTrue("the service fee is non-zero and the trial fee is zero");

                using HttpResponseMessage subscribe = await owner.PostAsync(
                    ServiceSubscriptionRoute(userId, paidRoleId),
                    content: null);

                subscribe.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await subscribe.Content.ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json))!
                    .Type.Should().Contain("payment_required_forbidden");

                using HttpResponseMessage cancel = await owner.DeleteAsync(
                    ServiceSubscriptionRoute(userId, paidRoleId));

                cancel.StatusCode.Should().Be(
                    HttpStatusCode.Forbidden,
                    "the legacy cancel path shared the subscribe gate and also went through payment");

                using HttpResponseMessage trial = await owner.PostAsync(
                    ServiceTrialRoute(userId, paidRoleId),
                    content: null);

                trial.StatusCode.Should().Be(
                    HttpStatusCode.NoContent,
                    "a free trial on a paid service is performable, because its gate is the trial fee");

                MemberServiceDto trialling = (await ReadServicesAsync(owner, userId))
                    .Single(entry => entry.RoleId == paidRoleId);

                trialling.IsSubscribed.Should().BeTrue();
                trialling.ExpiryDate.Should().NotBeNull(
                    "the fourteen-day trial term derives a bound, unlike the never-expires seeded role");

                using HttpResponseMessage secondTrial = await owner.PostAsync(
                    ServiceTrialRoute(userId, paidRoleId),
                    content: null);

                secondTrial.StatusCode.Should().Be(
                    HttpStatusCode.NoContent,
                    "the trial-used flag is never written, which is the legacy behaviour recorded in the notes");
            }
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Portals] SET [ProcessorUserID] = NULL WHERE [PortalID] = @portalId;",
                new Dictionary<string, object?> { ["portalId"] = _fixture.Seed.PortalId });

            await RemoveRoleAsync(paidRoleId);
        }
    }

    /// <summary>
    /// A free service offers no trial, which is <c>ShowTrial</c>'s own first arm.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>If objRole.IsPublic And objRole.ServiceFee = 0.0 Then _ShowTrial = False</c>
    /// (<c>MemberServices.ascx.vb:L330-L331</c>). There is nothing to trial when there is nothing to pay.
    /// </remarks>
    [Fact]
    public async Task StartServiceTrial_RefusesAFreeServiceThatHasNothingToTrial()
    {
        (int userId, HttpClient owner) = await CreateOwnedAccountAsync();

        using (owner)
        {
            using HttpResponseMessage response = await owner.PostAsync(
                ServiceTrialRoute(userId, _fixture.Seed.SubscribersRoleId),
                content: null);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json))!
                .Type.Should().Contain("trial_not_offered_forbidden");
        }
    }

    /// <summary>
    /// Creates an account through the real endpoint and signs in as it, so the caller OWNS the account the
    /// member-services routes name.
    /// </summary>
    /// <returns>The account identifier and a client presenting its own token.</returns>
    /// <remarks>
    /// A fresh account rather than the seeded member, because these facts subscribe and unsubscribe and a
    /// sibling fact reading the seeded member's memberships must not see them. Signing in through the real
    /// endpoint rather than minting a token is what makes the ownership policy's subject the same value the
    /// route carries.
    /// </remarks>
    private async Task<(int UserId, HttpClient Owner)> CreateOwnedAccountAsync()
    {
        using HttpClient administrator = await _fixture.CreateHostClientAsync();

        CreateUserRequest request = NewUserRequest();

        using HttpResponseMessage created = await administrator.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        UserDetailDto account = await ReadDetailAsync(created);

        HttpClient owner = await _fixture.CreateClientForAsync(request.Username, request.Password);

        return (account.UserId, owner);
    }

    /// <summary>
    /// Inserts a public, free, NOT auto-assigned role into the seeded tenant - a member service an account
    /// must take up for itself.
    /// </summary>
    /// <returns>The role identifier.</returns>
    /// <remarks>
    /// A role of its own rather than the seeded <c>Subscribers</c> role, because that one is auto-assigned and
    /// a freshly created account therefore already holds it: a subscription round trip needs a service whose
    /// starting state is "not held". Written by direct statement because the role write surface belongs to the
    /// role resource and reaching it here would make an account fact depend on a sibling resource's endpoint.
    /// The frequency codes are the never-expires <c>N</c>, matching what portal provisioning gives a tenant's
    /// own roles, so the derivation stores no bound and the fact does not depend on a clock.
    /// </remarks>
    private Task<int> InsertPublicServiceRoleAsync() =>
        _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Roles]
                ([PortalID], [RoleName], [Description], [ServiceFee], [BillingPeriod], [BillingFrequency],
                 [TrialFee], [TrialPeriod], [TrialFrequency], [IsPublic], [AutoAssignment])
            VALUES (@portalId, @roleName, 'A member service', 0, 0, 'N', 0, 0, 'N', 1, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["roleName"] = "ITest Service " + Suffix(),
            });

    /// <summary>
    /// Removes a role a member-services fact created, together with any assignment it accumulated.
    /// </summary>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>A task representing the removal.</returns>
    /// <remarks>
    /// The assignments go first even though the schema cascades, because these facts also assert assignment
    /// COUNTS and a row left behind by an ordering assumption would be invisible until a sibling fact
    /// disagreed with it. Every member-services fact that inserts a role removes it, so the tenant's role set
    /// is the seeded one again afterwards and a listing total elsewhere cannot drift.
    /// </remarks>
    private async Task RemoveRoleAsync(int roleId)
    {
        await _fixture.Database.ExecuteAsync(
            "DELETE FROM [dbo].[UserRoles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = roleId });

        await _fixture.Database.ExecuteAsync(
            "DELETE FROM [dbo].[Roles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>Counts the assignment rows joining one account to one role.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>The row count.</returns>
    private Task<int> CountAssignmentsAsync(int userId, int roleId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserRoles] WHERE [UserID] = @userId AND [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["userId"] = userId, ["roleId"] = roleId });

    /// <summary>Reads the member-services catalogue for one account.</summary>
    /// <param name="client">A client owning the account.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>The catalogue the endpoint served.</returns>
    private async Task<IReadOnlyList<MemberServiceDto>> ReadServicesAsync(HttpClient client, int userId)
    {
        using HttpResponseMessage response = await client.GetAsync(ServicesRoute(userId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadEnvelopeAsync<List<MemberServiceDto>>())!;
    }

    /// <summary>The five member-services addresses, each with the method that reaches it.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <returns>The method and route pairs.</returns>
    private (HttpMethod Method, Uri Route)[] MemberServiceAddresses(int userId) =>
    [
        (HttpMethod.Get, ServicesRoute(userId)),
        (HttpMethod.Post, ServiceSubscriptionRoute(userId, _fixture.Seed.SubscribersRoleId)),
        (HttpMethod.Delete, ServiceSubscriptionRoute(userId, _fixture.Seed.SubscribersRoleId)),
        (HttpMethod.Post, ServiceTrialRoute(userId, _fixture.Seed.SubscribersRoleId)),
        (HttpMethod.Post, ServiceRedemptionRoute(userId)),
    ];

    /// <summary>Builds the member-services catalogue route for one account.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ServicesRoute(int userId) =>
        new($"/api/v1/users/{Route(userId)}/services", UriKind.Relative);

    /// <summary>Builds the subscription route for one account and one service.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <param name="roleId">The role the service is expressed as.</param>
    /// <returns>A relative route.</returns>
    private static Uri ServiceSubscriptionRoute(int userId, int roleId) =>
        new($"/api/v1/users/{Route(userId)}/services/{Route(roleId)}/subscription", UriKind.Relative);

    /// <summary>Builds the trial route for one account and one service.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <param name="roleId">The role the service is expressed as.</param>
    /// <returns>A relative route.</returns>
    private static Uri ServiceTrialRoute(int userId, int roleId) =>
        new($"/api/v1/users/{Route(userId)}/services/{Route(roleId)}/trial", UriKind.Relative);

    /// <summary>Builds the invitation-code redemption route for one account.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ServiceRedemptionRoute(int userId) =>
        new($"/api/v1/users/{Route(userId)}/services/redemptions", UriKind.Relative);

    /// <summary>Builds the item route for one account.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UserRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}", UriKind.Relative);

    /// <summary>Builds the credential route for one account.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri PasswordRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}/password", UriKind.Relative);

    /// <summary>Builds the ADMINISTRATIVE credential-reset route for one account.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    /// <remarks>
    /// A separate address from <see cref="PasswordRoute"/> because it is a separate operation with a separate
    /// authorisation policy. The two used to share one address and were told apart by a discriminator in the
    /// request body, which let the caller choose whether the current credential had to be proved.
    /// </remarks>
    private static Uri PasswordResetRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}/password-reset", UriKind.Relative);

    /// <summary>Builds the unlock route for one account.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UnlockRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}/unlock", UriKind.Relative);

    /// <summary>Builds the approval route for one account.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <param name="isApproved">The approval state being requested.</param>
    /// <returns>A relative route.</returns>
    private static Uri ApprovalRoute(int _, int userId, bool isApproved) => new(
        $"/api/v1/users/{Route(userId)}/approval"
            + $"?isApproved={(isApproved ? "true" : "false")}",
        UriKind.Relative);

    /// <summary>Builds the forced-credential-change route for one account.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri RequirePasswordChangeRoute(int _, int userId) => new(
        $"/api/v1/users/{Route(userId)}/require-password-change",
        UriKind.Relative);

    /// <summary>Builds the profile route for one account.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ProfileRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}/profile", UriKind.Relative);

    /// <summary>Builds the item route for one profile property definition.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="propertyDefinitionId">The definition identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ProfileDefinitionRoute(int _, int propertyDefinitionId) => new(
        $"/api/v1/profile-definitions/{Route(propertyDefinitionId)}",
        UriKind.Relative);

    /// <summary>Builds the membership-settings route for the resolved tenant.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <returns>A relative route.</returns>
    private static Uri MembershipSettingsRoute(int _) =>
        new("/api/v1/users/settings", UriKind.Relative);

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

    /// <summary>
    /// An ordinary authenticated member holds none of the administrative account operations, however many of
    /// them it addresses at its own tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the account-takeover surface in one assertion. An earlier revision gated all thirteen user
    /// routes on the class-level authentication attribute alone, so every operation below succeeded for any
    /// authenticated caller: enumerating a tenant's accounts and their personal data, creating and deleting
    /// accounts, unlocking and approving them, forcing credential changes, and rewriting the tenant's
    /// membership settings. The member here is a real seeded account that holds no administrator role.
    /// </remarks>
    [Fact]
    public async Task AdministrativeUserRoutes_AreRefusedToAnOrdinaryMember()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        UserDetailDto victim = await CreateUserAsync(host);

        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();

        int portalId = _fixture.Seed.PortalId;

        using HttpResponseMessage listed = await member.GetAsync(UsersRoute(portalId));
        listed.StatusCode.Should().Be(HttpStatusCode.Forbidden, "enumerating a tenant's accounts is administrative");

        using HttpResponseMessage unlocked = await member.PostAsync(
            UnlockRoute(portalId, victim.UserId),
            content: null);
        unlocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage approved = await member.PutAsync(
            ApprovalRoute(portalId, victim.UserId, isApproved: false),
            content: null);
        approved.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage forced = await member.PostAsync(
            RequirePasswordChangeRoute(portalId, victim.UserId),
            content: null);
        forced.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage settings = await member.GetAsync(MembershipSettingsRoute(portalId));
        settings.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage removed = await member.DeleteAsync(UserRoute(portalId, victim.UserId));
        removed.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The victim survives every refusal, which is what distinguishes a refusal from a report of one.
        using HttpResponseMessage stillThere = await host.GetAsync(UserRoute(portalId, victim.UserId));
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// An ordinary member cannot read, update or set the credential of an account that is not its own, and
    /// cannot use the administrative reset operation on itself to sidestep presenting its current credential.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The second half is the subtler defect and the reason the service performs its own check. Account access
    /// legitimately admits the account itself, so the route policy alone would let an account choose RESET -
    /// which presents no current credential by design - and thereby set a new one without proving it knew the
    /// old. That turns a stolen access token into a permanent takeover, so the reset operation requires
    /// administrative authority regardless of who the account belongs to.
    /// </remarks>
    [Fact]
    public async Task SelfServiceUserRoutes_AreConfinedToTheCallersOwnAccountAndOperation()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        UserDetailDto victim = await CreateUserAsync(host);

        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();

        int portalId = _fixture.Seed.PortalId;

        using HttpResponseMessage read = await member.GetAsync(UserRoute(portalId, victim.UserId));
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden, "another account's record is not self-service");

        using HttpResponseMessage profile = await member.GetAsync(ProfileRoute(portalId, victim.UserId));
        profile.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage takeover = await member.PostAsJsonAsync(
            PasswordRoute(portalId, victim.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        takeover.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "setting another account's credential is an administrative reset, not self-service");

        // A self-targeted RESET is refused at the reset endpoint itself, which is where the property lives:
        // that route carries the portal-administrator policy, so the account cannot reach it even for itself.
        // This is what stops a stolen access token from becoming a permanent takeover - were the reset
        // operation available to the account, the owner could set a new credential without presenting the
        // current one, which would make the change endpoint's verification optional.
        using HttpResponseMessage selfReset = await member.PostAsJsonAsync(
            PasswordResetRoute(portalId, _fixture.Seed.MemberUserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        selfReset.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "reset presents no current credential, so it must not be reachable by the account itself");

        // And the reset operation cannot be smuggled into the CHANGE endpoint either, which is the second
        // half of the same guarantee: whether the current credential is verified is the endpoint's answer and
        // never the caller's, so a discriminator naming the other operation is refused rather than obeyed.
        // The refusal is a bad request rather than a forbidding, because the caller may legitimately use this
        // endpoint - it named the wrong operation on it.
        using HttpResponseMessage smuggledReset = await member.PostAsJsonAsync(
            PasswordRoute(portalId, _fixture.Seed.MemberUserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationReset,
                NewPassword = ReplacementPassword,
            },
            ApiTestFixture.Json);

        smuggledReset.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "the endpoint performs a change, and the discriminator that names a reset is refused rather "
            + "than silently performing the operation the caller did not ask for");
        // The published reason token spells the separator as an underscore: the document's type URN normalises
        // the code, so the wire form is not character-for-character the constant the service declares.
        (await smuggledReset.Content.ReadAsStringAsync())
            .Should().Contain("urn:dnnmigration:error:user.password.unsupported_operation");

        // The victim's credential is unchanged: it can still sign in with the value it was created with.
        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage signedIn = await anonymous.PostAsJsonAsync(
            LoginRoute(portalId),
            new { username = victim.Username, password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The electronic-mail filter narrows the collection on a PREFIX, and a fragment taken from the middle of
    /// a held address matches nothing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is one of the four filter shapes the legacy account listing offered, and the prefix half is the
    /// load-bearing assertion. <c>Website/admin/Users/Users.ascx.vb:L268</c> called
    /// <c>GetUsersByEmail(..., SearchText + "%", CurrentPage - 1, PageSize, TotalRecords)</c>: the screen
    /// appended the wildcard itself, so the match was anchored at the start of the address and a mid-string
    /// fragment found nothing. A filter that had drifted to a containment match would still answer
    /// <c>200 OK</c> with a plausible-looking page, so only the negative half of this test can tell the two
    /// apart.
    /// </para>
    /// <para>
    /// The wildcard is NOT sent by the caller here. Appending it was a property of the legacy screen composing
    /// a <c>LIKE</c> argument by hand; the target takes a plain prefix and owns the pattern behind the
    /// repository interface, which is also what keeps a caller from injecting wildcard metacharacters into the
    /// match.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_FilteredByEmailAddress_MatchesAPrefixAndNotAMidStringFragment()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateUserRequest request = NewUserRequest();
        string localPart = request.Email.Split('@')[0];

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        UserDetailDto createdUser = await ReadDetailAsync(created);

        PagedEnvelope<UserListItemDto> byPrefix = await ListAsync(
            client,
            $"email={Uri.EscapeDataString(localPart)}");

        byPrefix.TotalCount.Should().BeGreaterThan(0);
        UserListItemDto matched = byPrefix.Items
            .Should().ContainSingle(item => item.UserId == createdUser.UserId)
            .Subject;
        matched.Username.Should().Be(request.Username);

        // MIGRATION: the FILTER is server-side and the PROJECTION is minimised, and the two are independent.
        // The tenant's measured legacy default switches the electronic-mail column off
        // (UserModuleBase.vb:L109-L111 defaults Column_Email to False), so a default tenant withholds the
        // address from the listing - and the filter above still matched on it, because narrowing happens in
        // the query against the stored column and never depends on what the row is allowed to publish. This
        // is the whole point of server-side minimisation: the caller can search by an address it is not
        // handed back.
        //
        // The account is therefore identified by its login name and identifier, which carry no visibility
        // flag, rather than by the withheld address.
        matched.UserId.Should().Be(createdUser.UserId);
        matched.Email.Should().BeEmpty(
            "the tenant's default withholds the electronic-mail column, and a withheld column must not cross "
            + "the API boundary even when it was the column filtered on");

        // A fragment lifted from the middle of the very address that was just matched by prefix. The account
        // demonstrably exists and demonstrably holds the fragment, so an empty page here can only be the
        // anchoring.
        string fragment = localPart[3..];
        fragment.Should().NotBeNullOrEmpty("the seeded address must be long enough to yield a mid-string cut");

        PagedEnvelope<UserListItemDto> byFragment = await ListAsync(
            client,
            $"email={Uri.EscapeDataString(fragment)}");

        byFragment.Items.Should().BeEmpty("the legacy match was anchored at the start of the address");
        byFragment.TotalCount.Should().Be(0);
    }

    /// <summary>
    /// The login-name filter narrows the collection on a PREFIX too, and rejects a mid-string fragment for the
    /// same reason.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The legacy call site is <c>Website/admin/Users/Users.ascx.vb:L270</c>,
    /// <c>GetUsersByUserName(..., SearchText + "%", ...)</c>. The seeded login name is used rather than a
    /// created one because it is the value the rest of the suite already proves is listed, so a failure here
    /// cannot be blamed on the account being absent.
    /// </remarks>
    [Fact]
    public async Task ListUsers_FilteredByLoginName_MatchesAPrefixAndNotAMidStringFragment()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string held = IntegrationSeed.MemberUserName;
        string prefix = held[..(held.Length - 4)];
        string fragment = held[4..];

        PagedEnvelope<UserListItemDto> byPrefix = await ListAsync(
            client,
            $"userName={Uri.EscapeDataString(prefix)}");

        byPrefix.Items.Select(item => item.Username).Should().Contain(held);
        byPrefix.Items.Should().OnlyContain(item => item.Username.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase));

        PagedEnvelope<UserListItemDto> byFragment = await ListAsync(
            client,
            $"userName={Uri.EscapeDataString(fragment)}");

        byFragment.Items.Select(item => item.Username).Should().NotContain(
            held,
            "the legacy match was anchored at the start of the login name");
    }

    /// <summary>
    /// The profile-property filter is the fourth query shape, and it returns only the accounts that actually
    /// hold the named value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The legacy shape is <c>GetUsersByProfileProperty(portalId, propertyName, propertyValue, ...)</c>
    /// (<c>Library/Components/Users/UserController.vb:L864</c>), reached from
    /// <c>Website/admin/Users/Users.ascx.vb:L273</c>. It is the only filter that reaches outside the account
    /// row into the profile value table, so it is the one a mapping change is most likely to break, and the
    /// negative half - an account that holds no value must not be listed - is what proves the join rather than
    /// a coincidence.
    /// </para>
    /// <para>
    /// Both accounts are created by this test rather than seeded, so the value is held by exactly one of a
    /// known pair and the assertion does not depend on what other suites have written.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_FilteredByProfileProperty_ReturnsOnlyTheAccountsHoldingThatValue()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        ProfilePropertyDefinitionDto definition = await CreateProfileDefinitionAsync(client, required: false);
        UserDetailDto holder = await CreateUserAsync(client);
        UserDetailDto abstainer = await CreateUserAsync(client);
        string value = "Rotterdam" + Suffix();

        using HttpResponseMessage stored = await client.PutAsJsonAsync(
            ProfileRoute(_fixture.Seed.PortalId, holder.UserId),
            new UserProfileDto
            {
                UserId = holder.UserId,
                Properties =
                [
                    new UserProfileValueDto
                    {
                        PropertyDefinitionId = definition.PropertyDefinitionId,
                        PropertyValue = value,
                        Visibility = definition.Visibility,
                        Definition = definition,
                    },
                ],
            },
            ApiTestFixture.Json);

        stored.StatusCode.Should().Be(HttpStatusCode.NoContent);

        PagedEnvelope<UserListItemDto> page = await ListAsync(
            client,
            $"profilePropertyName={Uri.EscapeDataString(definition.PropertyName)}"
                + $"&profilePropertyValue={Uri.EscapeDataString(value)}");

        page.Items.Select(item => item.UserId).Should().Contain(holder.UserId);
        page.Items.Select(item => item.UserId).Should().NotContain(
            abstainer.UserId,
            "an account holding no value for the named property is not a match");
    }

    /// <summary>
    /// No query shape ever reports the legacy sentinel total, however the collection is filtered.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>Library/Components/Users/UserController.vb</c> reported the total through a
    /// <c>ByRef totalRecords</c> argument initialised to <c>-1</c>, and <c>FillUserCollection</c>
    /// (<c>L215-L255</c>) ends in a <c>Catch Exc As Exception</c> at <c>L253</c> whose body logs nothing and
    /// rethrows nothing. A failed read therefore returned an empty collection beside a total of <c>-1</c>, and
    /// the calling screen rendered it as though it were a count. The target replaces the argument with the
    /// paged envelope, whose total is a genuine count on every path, so <c>-1</c> is not merely unlikely here
    /// but unrepresentable - and that is worth pinning precisely because the legacy value was produced by
    /// swallowing the error rather than by counting.
    /// </para>
    /// <para>
    /// All five shapes are swept in one test rather than as five: the guarantee is a property of the envelope
    /// rather than of any one filter, and asserting it once per shape would multiply the host round trips
    /// without adding a distinct failure mode. The free-text shape is included even though it has no legacy
    /// counterpart, because it reaches the same envelope.
    /// </para>
    /// <para>
    /// The profile-property shape names a definition this test creates rather than a plausible literal. A
    /// property the tenant does not define is answered <c>404 Not Found</c> - a profile property is itself an
    /// addressable resource, so naming an absent one is reported the way addressing it directly would be -
    /// which would make the sweep assert the wrong thing.
    /// </para>
    /// <para>
    /// The sweep deliberately mixes shapes that match records with shapes that match none: the freshly created
    /// definition holds no values, so its page is legitimately empty. An empty page is precisely where a
    /// sentinel total would otherwise pass unnoticed, since nothing in the records would look wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_AcrossEveryQueryShape_NeverReportsTheLegacySentinelTotal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        ProfilePropertyDefinitionDto definition = await CreateProfileDefinitionAsync(client, required: false);
        string definedProperty = Uri.EscapeDataString(definition.PropertyName);

        IReadOnlyList<string> shapes =
        [
            string.Empty,
            "userName=" + Uri.EscapeDataString(IntegrationSeed.MemberUserName),
            "email=" + Uri.EscapeDataString("member@"),
            $"profilePropertyName={definedProperty}&profilePropertyValue=Amsterdam",
            "query=" + Uri.EscapeDataString("integration"),
        ];

        foreach (string shape in shapes)
        {
            PagedEnvelope<UserListItemDto> page = await ListAsync(client, shape);

            page.TotalCount.Should().NotBe(
                LegacySentinelTotal,
                "the paged envelope reports a counted total on every path, filtered or not");
            page.TotalCount.Should().BeGreaterThanOrEqualTo(0);
            page.Meta.Should().NotBeNull("the paging facts travel in the metadata companion");
            page.Items.Count.Should().BeLessThanOrEqualTo(page.TotalCount);
        }
    }

    /// <summary>
    /// The legacy unpaged sentinel is refused with the offending field named, rather than being obeyed or
    /// silently reinterpreted.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>UserController.vb:L687</c> and <c>:L706</c> called
    /// <c>GetUsers(portalId, ..., -1, -1, -1)</c>, and the provider turned that into "every row" at four
    /// identical sites -
    /// <c>Library/Providers/MembershipProviders/AspNetMembershipProvider/AspNetMembershipProvider.vb:L1160-L1163</c>
    /// and the three that follow it - by testing <c>pageIndex = -1</c> and substituting
    /// <c>pageSize = Integer.MaxValue</c>. The XML documentation on those same methods tells the caller to
    /// "set pageSize = -1", naming the wrong parameter; the code is the contract.
    /// </para>
    /// <para>
    /// The target has no unpaged mode and does not reproduce the sentinel. A page index below zero and a page
    /// size below one are each a field-level <c>400</c>, which is the deliberate divergence: the danger in
    /// carrying the sentinel across was that a nullable-or-zero mapping would read <c>-1</c> as page zero of
    /// size zero and answer <c>200 OK</c> with an empty page, reporting nothing wrong to a caller that had
    /// asked for everything. Refusing it names the parameter instead, and a caller that wants every row asks
    /// for a page size it can state.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_WithTheLegacyUnpagedSentinel_IsRefusedAndNamesTheOffendingField()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage bothSentinels = await client.GetAsync(new Uri(
            "/api/v1/users?pageIndex=-1&pageSize=-1",
            UriKind.Relative));

        bothSentinels.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? problem = await bothSentinels.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Errors.Should().ContainKey(
            nameof(PagedRequest.PageIndex),
            "the sentinel page index must be attributed to the parameter that carried it");
        problem.Errors.Should().ContainKey(
            nameof(PagedRequest.PageSize),
            "the sentinel page size must be attributed to the parameter that carried it");

        // Named separately as well, because the provider's real test was on the INDEX while its documentation
        // named the SIZE. Sending only the index proves the index alone is enough to be refused, so neither
        // parameter can pass by relying on the other to fail.
        using HttpResponseMessage indexOnly = await client.GetAsync(new Uri(
            "/api/v1/users?pageIndex=-1&pageSize=10",
            UriKind.Relative));

        indexOnly.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? indexProblem = await indexOnly.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        indexProblem.Should().NotBeNull();
        indexProblem!.Errors.Should().ContainKey(nameof(PagedRequest.PageIndex));
    }

    /// <summary>
    /// Neither zero nor minus one addresses an account, because the account table's identity seed makes both
    /// unreachable.
    /// </summary>
    /// <param name="userId">The identifier being addressed.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the CONTRAST case of the migration's sentinel handling, and it is the one place where copying
    /// the pattern that is correct everywhere else would be wrong.
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L98</c> declares
    /// <c>[UserID] int IDENTITY (1, 1)</c>, so the first account is <c>1</c> and neither <c>0</c> nor
    /// <c>-1</c> can ever name a row. The neighbouring resources are seeded differently on purpose -
    /// <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c> at <c>:L77</c> and <c>Roles.RoleID</c> is
    /// <c>IDENTITY (0, 1)</c> at <c>:L115</c> - so for those two the very values refused here are legitimate
    /// identifiers.
    /// </para>
    /// <para>
    /// The collision is what makes the assertion worth having: <c>Null.vb:L41-L43</c> defines
    /// <c>NullInteger</c> as <c>-1</c>, which is simultaneously the legacy marker for "absent" and a real
    /// portal identifier. An implementation that treated a non-positive route value as "absent" and short
    /// circuited would answer identically here while being wrong for portals, so this test is paired with the
    /// tenant fact below rather than standing alone.
    /// </para>
    /// <para>
    /// A theory over two integers rather than one over a nullable: an <c>[InlineData(null)]</c> row against a
    /// non-nullable <c>int</c> parameter is an analyser error under this solution's warnings-as-errors policy,
    /// and there is nothing to gain by widening the parameter when both values under test are real integers.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetUser_AddressedByAnIdentifierTheIdentitySeedExcludes_ReturnsNotFound(int userId)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            UserRoute(_fixture.Seed.PortalId, userId));

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the account identity column starts at one, so this identifier cannot name a row");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("an absent account is reported as a problem document, not a bare status");
        problem!.Status.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// A tenant whose identifier is not positive is served normally, which is the other half of the identity
    /// seed contrast.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), so the installation's first tenant is <c>-1</c> and its second is
    /// <c>0</c>. Both values are indistinguishable from the legacy <c>NullInteger</c> marker, and both are
    /// legitimate. The seeded tenant demonstrates the negative case through the host-resolved context; the
    /// flat route must not reject that resolved identifier merely because it is not positive.
    /// </para>
    /// <para>
    /// The assertion on the seeded identifier is an inequality rather than an equality: what matters is that a
    /// non-positive tenant identifier is exercised at all, not which particular value the seed produced.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListUsers_ForAResolvedTenantWhoseIdentifierIsNotPositive_ReturnsOk()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        _fixture.Seed.PortalId.Should().BeLessThanOrEqualTo(
            0,
            "the tenant identity column seeds at minus one, so the first tenant is not positive");

        PagedEnvelope<UserListItemDto> seededTenant = await ListAsync(client, string.Empty);
        seededTenant.TotalCount.Should().BeGreaterThan(0);
        seededTenant.TotalCount.Should().NotBe(LegacySentinelTotal);
    }

    /// <summary>
    /// Two accounts may hold the same electronic-mail address, and the second create is accepted rather than
    /// refused.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The legacy installation permitted duplicates:
    /// <c>Website/release.config:L244</c> registers the membership provider with
    /// <c>requiresUniqueEmail="false"</c>. Preserving that is a migration obligation rather than a preference,
    /// because the existing data may already contain duplicates and a uniqueness rule introduced here would
    /// reject the very accounts the installation holds. The clause is explicit that validation rules must
    /// MATCH, and a rule the legacy system did not have is as much a divergence as a rule dropped.
    /// </para>
    /// <para>
    /// This fact is the guard against that specific regression, and it is deliberately positive: it asserts
    /// <c>201 Created</c> rather than merely "not <c>409</c>", and then proves both accounts are listed under
    /// the shared address. The conflict this resource DOES report is on the login name, which the neighbouring
    /// facts cover - so the pair together fix which field is unique and which is not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateUser_WithAnEmailAddressAnotherAccountHolds_ReturnsCreated()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateUserRequest first = NewUserRequest();

        using HttpResponseMessage firstResponse = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            first,
            ApiTestFixture.Json);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        UserDetailDto firstAccount = await ReadDetailAsync(firstResponse);

        CreateUserRequest second = NewUserRequest();
        second.Email = first.Email;

        using HttpResponseMessage secondResponse = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            second,
            ApiTestFixture.Json);

        secondResponse.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the legacy installation did not require a unique address, so neither may this one");

        UserDetailDto secondAccount = await ReadDetailAsync(secondResponse);
        secondAccount.Email.Should().Be(first.Email);
        secondAccount.UserId.Should().NotBe(firstAccount.UserId);

        PagedEnvelope<UserListItemDto> shared = await ListAsync(
            client,
            $"email={Uri.EscapeDataString(first.Email)}");

        shared.Items.Select(item => item.UserId)
            .Should().Contain(firstAccount.UserId)
            .And.Contain(secondAccount.UserId);
    }

    /// <summary>
    /// Submitting the same new login name from several callers at once creates the account exactly once,
    /// refuses every other caller as a conflict, and answers no caller with a server fault.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-F6. Four sequential checks run in front of this insert and none of them can close the
    /// window in front of it: <c>IX_Users</c> is unique over the login name, so every racer reads "not taken"
    /// before any of them commits and the losers are refused by the index instead. Left untranslated, that
    /// refusal reached the transport - which references neither the mapper nor the database client by design -
    /// and was answered 500, reporting a server fault for a store that had correctly kept exactly one account.
    /// </para>
    /// <para>
    /// The row count is read from the column, because that is the only place the claim can be settled. The
    /// account create writes inside a transaction spanning the account, its memberships, its role assignments
    /// and its credential, so a partially committed racer would be invisible to any listing while still
    /// leaving a row behind.
    /// </para>
    /// <para>
    /// The fact asserts the OUTCOME - one account, every other caller refused as a conflict, no 5xx, one row -
    /// and deliberately not which mechanism refused a given caller: the checks and the index answer
    /// identically by design, and which wins depends on scheduling. The translation itself is pinned
    /// deterministically by <c>DuplicateKeyTranslationTests</c> and by the service-level facts.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateUser_SubmittedConcurrentlyUnderOneLoginName_CreatesItOnceWithoutAnyServerFault()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateUserRequest template = NewUserRequest();
        const int Callers = 10;

        IEnumerable<Task<HttpResponseMessage>> submissions = Enumerable.Range(0, Callers).Select(index =>
        {
            CreateUserRequest request = NewUserRequest();

            // Only the contested value is shared. Everything else differs per caller, so a racer cannot be
            // refused for some incidental collision and be mistaken for a login-name refusal.
            request.Username = template.Username;
            request.DisplayName = "Race Caller " + index.ToString(CultureInfo.InvariantCulture);

            return client.PostAsJsonAsync(
                UsersRoute(_fixture.Seed.PortalId),
                request,
                ApiTestFixture.Json);
        });

        HttpResponseMessage[] responses = await Task.WhenAll(submissions);

        try
        {
            HttpStatusCode[] statuses = [.. responses.Select(response => response.StatusCode)];

            statuses.Should().NotContain(
                status => (int)status >= 500,
                "the unique index refusing a duplicate login name is the index working correctly, and "
                + "reporting it as a server fault also raises a fault-level log entry for a collision");

            statuses.Count(status => status == HttpStatusCode.Created).Should().Be(
                1,
                "one caller takes the login name and every other must be refused");

            statuses.Where(status => status != HttpStatusCode.Created).Should().AllBeEquivalentTo(
                HttpStatusCode.Conflict,
                "a login name that is now held is a conflict with existing state");

            int accounts = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[Users] WHERE [Username] = @username;",
                new Dictionary<string, object?> { ["username"] = template.Username });

            accounts.Should().Be(
                1,
                "a refused caller must leave no account behind, and two accounts sharing a login name would "
                + "make sign-in ambiguous");
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
    /// A credential-store failure rolls the EF account and all dependent rows back instead of relying on a
    /// compensating delete after the account has committed.
    /// </summary>
    [Fact]
    public async Task CreateUser_WhenCredentialInsertFails_LeavesNoPartialAccount()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        CreateUserRequest request = NewUserRequest();
        string triggerName = "TRG_BlockMembership_" + Suffix();
        string loweredUserName = request.Username.ToLowerInvariant();

        await _fixture.Database.ExecuteAsync(
            $"""
            CREATE TRIGGER [dbo].[{triggerName}]
            ON [dbo].[aspnet_Membership]
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS
                (
                    SELECT 1
                    FROM inserted i
                    INNER JOIN [dbo].[aspnet_Users] u ON u.[UserId] = i.[UserId]
                    WHERE u.[LoweredUserName] = N'{loweredUserName}'
                )
                BEGIN
                    THROW 51000, 'Integration credential failure', 1;
                END
            END;
            """);

        try
        {
            using HttpResponseMessage response = await host.PostAsJsonAsync(
                UsersRoute(_fixture.Seed.PortalId),
                request,
                ApiTestFixture.Json);

            response.StatusCode.Should().NotBe(HttpStatusCode.Created);

            int users = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[Users] WHERE [Username] = @userName;",
                new Dictionary<string, object?> { ["userName"] = request.Username });

            users.Should().Be(0);
            (await CountCredentialsAsync(request.Username)).Should().Be(0);
        }
        finally
        {
            await _fixture.Database.ExecuteAsync($"DROP TRIGGER IF EXISTS [dbo].[{triggerName}];");
        }
    }

    /// <summary>
    /// A failure at the final account-row delete rolls credentials, grants, profile values, role assignments
    /// and portal membership back together.
    /// </summary>
    [Fact]
    public async Task DeleteUser_WhenFinalDeleteFails_RollsBackTheWholeCascade()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        UserDetailDto account = await CreateUserAsync(host);
        ProfilePropertyDefinitionDto definition = await CreateProfileDefinitionAsync(host, required: false);
        await InsertProfileValueAsync(account.UserId, definition.PropertyDefinitionId, "retained");

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[TabPermission] ([TabID], [PermissionID], [UserID], [AllowAccess])
            VALUES (@tabId, @permissionId, @userId, 1);
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = _fixture.Seed.RootTabId,
                ["permissionId"] = _fixture.Seed.TabViewPermissionId,
                ["userId"] = account.UserId,
            });

        string triggerName = "TRG_BlockUserDelete_" + Suffix();
        await _fixture.Database.ExecuteAsync(
            $"""
            CREATE TRIGGER [dbo].[{triggerName}]
            ON [dbo].[Users]
            AFTER DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted WHERE [UserID] = {account.UserId})
                BEGIN
                    THROW 51001, 'Integration account-delete failure', 1;
                END
            END;
            """);

        try
        {
            using HttpResponseMessage response = await host.DeleteAsync(
                UserRoute(_fixture.Seed.PortalId, account.UserId));

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

            int users = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[Users] WHERE [UserID] = @userId;",
                new Dictionary<string, object?> { ["userId"] = account.UserId });
            int memberships = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[UserPortals] WHERE [UserID] = @userId AND [PortalID] = @portalId;",
                new Dictionary<string, object?>
                {
                    ["userId"] = account.UserId,
                    ["portalId"] = _fixture.Seed.PortalId,
                });
            int permissions = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[TabPermission] WHERE [UserID] = @userId AND [TabID] = @tabId;",
                new Dictionary<string, object?>
                {
                    ["userId"] = account.UserId,
                    ["tabId"] = _fixture.Seed.RootTabId,
                });

            users.Should().Be(1);
            memberships.Should().Be(1);
            permissions.Should().Be(1);
            (await CountCredentialsAsync(account.Username)).Should().Be(1);
            (await CountProfileValuesAsync(account.UserId, definition.PropertyDefinitionId)).Should().Be(1);
        }
        finally
        {
            await _fixture.Database.ExecuteAsync($"DROP TRIGGER IF EXISTS [dbo].[{triggerName}];");
        }
    }

    /// <summary>
    /// A credential sitting exactly on the configured floor is accepted, punctuation and all absent.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The legacy policy, measured from the provider registration at
    /// <c>Website/release.config:L242-L243</c>, is <c>minRequiredPasswordLength="7"</c> and
    /// <c>minRequiredNonalphanumericCharacters="0"</c>, with <c>requiresQuestionAndAnswer="false"</c> at
    /// <c>:L241</c>. A seven-character alphanumeric credential therefore satisfied it, and the account created
    /// here proves the target still accepts one - including at sign-in, which is where a silently tightened
    /// rule would lock an existing account out rather than merely inconvenience a new one.
    /// </para>
    /// <para>
    /// Hardening the policy during a migration is the change that cannot be undone from the outside: existing
    /// stored credentials cannot be re-derived to satisfy a new rule, so every account holding a
    /// seven-character credential would be stranded. Any hardening is therefore a separate, deliberate
    /// decision, and this test is what makes an accidental one fail.
    /// </para>
    /// <para>
    /// No question-and-answer pair is sent, because the request contract carries none - which is the same
    /// parity fact expressed structurally rather than by assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateUser_WithACredentialOnThePolicyFloor_ReturnsCreatedAndCanSignIn()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        PolicyFloorPassword.Length.Should().Be(7, "the legacy floor was seven characters");
        PolicyFloorPassword.Should().MatchRegex(
            "^[A-Za-z0-9]+$",
            "the legacy policy required no non-alphanumeric character");

        CreateUserRequest request = NewUserRequest();
        request.Password = PolicyFloorPassword;
        request.ConfirmPassword = PolicyFloorPassword;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "tightening the credential policy would lock existing accounts out");

        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage signedIn = await anonymous.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new { username = request.Username, password = PolicyFloorPassword },
            ApiTestFixture.Json);

        signedIn.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a credential the create path accepted must also be accepted at sign-in");
    }

    /// <summary>
    /// A create carrying several unusable values answers a per-field validation document naming every one of
    /// them.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The status alone is not the contract. What the legacy screens gave the operator was a list of the
    /// individual things that were wrong, rendered by the validation summary beside the fields, and the
    /// equivalent of that list is the <c>errors</c> object of the RFC 7807 document. A response that collapsed
    /// three field failures into one sentence would still be a <c>400</c>, so the field names are asserted
    /// rather than the count.
    /// </para>
    /// <para>
    /// The names are read through <c>nameof</c> on the request contract, so a renamed member breaks this test
    /// at compile time instead of at run time.
    /// </para>
    /// <para>
    /// The media type is deliberately not asserted, for the reason recorded against the portal suite: the
    /// framework serves a controller-produced problem document as <c>application/json</c> rather than as
    /// <c>application/problem+json</c>, and pinning the value it currently emits would cement a deviation and
    /// make correcting it later look like a regression. The envelope MEMBERS are what this asserts, because
    /// those are what a client reads.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateUser_WithSeveralUnusableValues_NamesEveryOffendingField()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateUserRequest request = NewUserRequest();
        request.Username = string.Empty;
        request.LastName = string.Empty;
        request.Email = "not-an-address";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.Type.Should().NotBeNullOrWhiteSpace(
            "a client branches on the problem type rather than parsing prose");

        problem.Errors.Should().ContainKey(nameof(CreateUserRequest.Username));
        problem.Errors.Should().ContainKey(nameof(CreateUserRequest.LastName));
        problem.Errors.Should().ContainKey(nameof(CreateUserRequest.Email));

        problem.Errors[nameof(CreateUserRequest.Email)].Should().NotBeEmpty(
            "a named field carries the reason it was refused, not an empty list");
    }

    /// <summary>
    /// A new credential the policy cannot accept is refused with the field named, on the self-service change
    /// route.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The legacy credential screen carried no declarative validator at all - <c>Website/admin/Users/</c>
    /// <c>Password.ascx</c> declares none, and <c>User.ascx</c> only a server-side custom validator - so the
    /// code behind was the sole authority for what a usable credential was. The target moves that authority
    /// into a request validator, and this asserts the consequence a caller can observe: the refusal names the
    /// member it refused, so a client can attach the message to the field the operator typed into.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy outcome channel was <c>PasswordUpdateStatus</c>
    /// (<c>Library/Components/Users/Membership/PasswordUpdateStatus.vb</c>), whose eight members carry NO
    /// explicit values - so <c>Success</c> is <c>0</c> there, the opposite of <c>UserCreateStatus</c>, where
    /// <c>Success</c> is <c>13</c> and the zero member is the "nothing has happened yet" marker. Nothing in
    /// this suite asserts either number, and that is the point of asserting outcomes as statuses and named
    /// fields instead. Six of the eight map onto observable results here - <c>Success</c> to
    /// <c>204 No Content</c>, <c>PasswordMissing</c> and <c>PasswordInvalid</c> to a <c>400</c> naming
    /// <c>newPassword</c>, <c>PasswordMismatch</c> to a <c>400</c> naming <c>confirmPassword</c>,
    /// <c>PasswordNotDifferent</c> to the refusal the neighbouring facts cover, and
    /// <c>PasswordResetFailed</c> to the reset route's refusal. The remaining two,
    /// <c>InvalidPasswordAnswer</c> and <c>InvalidPasswordQuestion</c>, are unreachable BY CONSTRUCTION: the
    /// legacy provider was registered with <c>requiresQuestionAndAnswer="false"</c>
    /// (<c>Website/release.config:L241</c>), so no question-and-answer pair was ever demanded, and the request
    /// contract carries no member through which one could be supplied. They are recorded here rather than
    /// asserted, because a test cannot reach a state the contract has no way to express.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ChangePassword_WithAnUnusableNewCredential_NamesTheOffendingField()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        UserDetailDto account = await CreateUserAsync(host);

        using HttpClient client = await ClientForAccountAsync(account);

        using HttpResponseMessage tooShort = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, account.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = "abc12",
                ConfirmPassword = "abc12",
            },
            ApiTestFixture.Json);

        tooShort.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? shortProblem = await tooShort.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        shortProblem.Should().NotBeNull();
        shortProblem!.Errors.Should().ContainKey(nameof(ChangePasswordRequest.NewPassword));

        // A confirmation that does not match is a distinct refusal reason and is attributed to the
        // confirmation field rather than to the credential itself.
        using HttpResponseMessage mismatched = await client.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, account.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = ReplacementPassword,
                ConfirmPassword = ReplacementPassword + "x",
            },
            ApiTestFixture.Json);

        mismatched.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? mismatchProblem = await mismatched.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        mismatchProblem.Should().NotBeNull();
        mismatchProblem!.Errors.Should().ContainKey(nameof(ChangePasswordRequest.ConfirmPassword));

        // The credential is unchanged after both refusals, which is what makes them refusals rather than
        // partial writes.
        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage signedIn = await anonymous.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new { username = account.Username, password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        signedIn.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// No account representation carries credential material, and no route serves one back.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: password retrieval is deliberately NOT carried forward. The legacy installation could return
    /// a stored credential to a caller - <c>Website/release.config:L239</c> registers the provider with
    /// <c>enablePasswordRetrieval="true"</c> over <c>passwordFormat="Encrypted"</c> at <c>:L245</c>, and the
    /// key that reversed the encryption was committed to source control alongside it. The target stores a
    /// one-way BCrypt hash, so retrieval is not merely unpublished but impossible, and
    /// <c>UserController.vb:L433</c>'s <c>GetPassword</c> has no counterpart.
    /// </para>
    /// <para>
    /// Two things are therefore asserted. First, that no representation leaks the material: the payloads are
    /// scanned as RAW JSON rather than through a typed model, because a typed read can only see members the
    /// test already knows to look for, whereas an unexpected member added later is exactly the leak worth
    /// catching. Second, that reading the credential route is not a way to obtain it either.
    /// </para>
    /// <para>
    /// The scan looks for the member NAMES rather than for the credential value, since the value would be
    /// hashed and so would not appear literally even in a body that disclosed it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UserRepresentations_NeverCarryCredentialMaterial()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        UserDetailDto account = await CreateUserAsync(client);

        IReadOnlyList<Uri> representations =
        [
            UsersRoute(_fixture.Seed.PortalId),
            UserRoute(_fixture.Seed.PortalId, account.UserId),
            ProfileRoute(_fixture.Seed.PortalId, account.UserId),
            MembershipSettingsRoute(_fixture.Seed.PortalId),
        ];

        IReadOnlyList<string> forbidden =
        [
            "password",
            "passwordhash",
            "passwordsalt",
            "passwordanswer",
            "passwordquestion",
        ];

        foreach (Uri representation in representations)
        {
            using HttpResponseMessage response = await client.GetAsync(representation);

            // The membership-settings projection answers 404 where the tenant holds no accounts module, and
            // that is a documented outcome rather than a failure - a body it never wrote cannot leak.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string body = await response.Content.ReadAsStringAsync();

            foreach (string member in forbidden)
            {
                body.Should().NotContainEquivalentOf(
                    $"\"{member}\"",
                    $"{representation.OriginalString} must not disclose credential material");
            }

            // Parsed as well as scanned, so that a body which is not JSON at all cannot pass the scan by
            // accident.
            using JsonDocument document = JsonDocument.Parse(body);
            document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }

        // The credential route accepts a write and serves no read. A GET is either unrouted or unsupported,
        // and either answer is acceptable - what matters is that it is not a 200 carrying a credential.
        using HttpResponseMessage read = await client.GetAsync(
            PasswordRoute(_fixture.Seed.PortalId, account.UserId));

        read.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>
    /// The correlation identifier survives onto a refused request's problem document, whether the caller
    /// supplied one or not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The failing path is the one that matters. A correlation identifier echoed only on success is of no use
    /// to the caller who has something to report, and the header is written by a callback registered before
    /// the pipeline continues precisely so that a response composed by the error path still carries it. This
    /// asserts it on a <c>404</c> for an account, which is the outcome a caller is most likely to be asking
    /// about.
    /// </para>
    /// <para>
    /// A supplied value is echoed exactly once. The header is assigned rather than appended, so a caller
    /// cannot end up with two identifiers to choose between, and an absent one is generated so that every
    /// response carries something to quote.
    /// </para>
    /// <para>
    /// An unusable value - here one far longer than the accepted bound - is REPLACED rather than echoed, and
    /// specifically does not turn the request into a <c>400</c>. Rejecting the request would let a caller's
    /// malformed diagnostic header break an otherwise valid operation, and echoing it would put unvalidated
    /// caller text into a response header.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RefusedUserRequest_CarriesACorrelationIdentifierOnItsProblemDocument()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string supplied = "user-suite-" + Suffix();
        Uri absent = UserRoute(_fixture.Seed.PortalId, UnknownUserId);

        using HttpRequestMessage echoed = ApiTestFixture.WithCorrelationId(
            new HttpRequestMessage(HttpMethod.Get, absent),
            supplied);

        using HttpResponseMessage echoedResponse = await client.SendAsync(echoed);

        echoedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ApiTestFixture.ReadCorrelationId(echoedResponse).Should().Be(
            supplied,
            "a refused request is exactly when the caller needs the identifier it supplied");

        echoedResponse.Headers.GetValues(ApiTestFixture.CorrelationIdHeader).Should().HaveCount(
            1,
            "the header is assigned rather than appended, so there is one value to quote");

        ProblemDetails? problem = await echoedResponse.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("the refusal carries a problem document beside the header");
        problem!.Status.Should().Be(StatusCodes.Status404NotFound);

        using HttpResponseMessage generated = await client.GetAsync(absent);

        generated.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ApiTestFixture.ReadCorrelationId(generated).Should().NotBeNullOrWhiteSpace(
            "an identifier is generated when the caller supplies none");

        using HttpRequestMessage overlong = ApiTestFixture.WithCorrelationId(
            new HttpRequestMessage(HttpMethod.Get, absent),
            new string('c', 400));

        using HttpResponseMessage overlongResponse = await client.SendAsync(overlong);

        overlongResponse.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an unusable diagnostic header must not change the outcome of the operation");

        string? replacement = ApiTestFixture.ReadCorrelationId(overlongResponse);
        replacement.Should().NotBeNullOrWhiteSpace();
        replacement!.Length.Should().BeLessThan(400, "an over-long value is replaced rather than echoed");
    }

    /// <summary>
    /// The legacy account operations that were deliberately not carried forward, and the ones that belong to a
    /// different resource, are absent from this one.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// An absence is as much a part of the published contract as a presence, and it is the half that no other
    /// test can accidentally cover. Each address below was reachable in the legacy administration and is
    /// intentionally unpublished here, so a later revision that reinstates one - by porting a screen
    /// mechanically, say - fails this test rather than quietly widening the surface.
    /// </para>
    /// <para>
    /// The absences fall into two kinds. Some operations are gone outright: bulk deletion, deleting every
    /// unauthorised account at once, bulk electronic mail, and the users-online view, whose supporting
    /// subsystem is out of scope. Others exist but belong elsewhere, and the distinction is the point.
    /// Signing in is the authentication resource's operation, not an account sub-resource. Role ASSIGNMENT is
    /// the role resource's operation - <c>POST roles/{roleId}/users</c> - so posting to the
    /// account's role collection must not be a second way to do it, even though READING that collection is
    /// legitimately published and is asserted elsewhere. Reordering a profile property is a PROPERTY of the
    /// definition, written through its <c>viewOrder</c> member on the update verb, and never an action address.
    /// </para>
    /// <para>
    /// Both an unrouted address and a routed one that refuses the verb are accepted, because the two are
    /// equally conclusive about the operation being unavailable and which of them a given address produces is
    /// a routing detail rather than a contract. What is NOT accepted is a success.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WithdrawnAndForeignAccountOperations_AreNotPublishedOnThisResource()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        UserDetailDto account = await CreateUserAsync(client);

        ProfilePropertyDefinitionDto definition = await CreateProfileDefinitionAsync(client, required: false);

        string subject = Route(account.UserId);
        string property = Route(definition.PropertyDefinitionId);

        IReadOnlyList<(HttpMethod Method, string Address)> withdrawn =
        [
            (HttpMethod.Post, "/api/v1/users/bulk-delete"),
            (HttpMethod.Post, "/api/v1/users/delete-unauthorized"),
            (HttpMethod.Post, "/api/v1/users/bulk-email"),
            (HttpMethod.Get, "/api/v1/users/online"),
            (HttpMethod.Post, "/api/v1/users/login"),
            (HttpMethod.Post, $"/api/v1/users/{subject}/roles"),
            (HttpMethod.Post, $"/api/v1/profile-definitions/{property}/move-up"),
            (HttpMethod.Post, $"/api/v1/profile-definitions/{property}/move-down"),
        ];

        foreach ((HttpMethod method, string address) in withdrawn)
        {
            using var request = new HttpRequestMessage(method, new Uri(address, UriKind.Relative));
            using HttpResponseMessage response = await client.SendAsync(request);

            response.StatusCode.Should().BeOneOf(
                [HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed],
                $"{method.Method} {address} is not a published operation of the account resource");
        }

        // The reorder PROPERTY, by contrast, is honoured through the update verb - so the absence above is the
        // action address being unavailable, not the capability being lost.
        UpdateProfilePropertyDefinitionRequest amendment = AmendmentFrom(definition);
        amendment.ViewOrder = definition.ViewOrder + 5;

        using HttpResponseMessage reordered = await client.PutAsJsonAsync(
            ProfileDefinitionRoute(_fixture.Seed.PortalId, definition.PropertyDefinitionId),
            amendment,
            ApiTestFixture.Json);

        reordered.StatusCode.Should().Be(HttpStatusCode.OK);

        ProfilePropertyDefinitionDto? afterReorder = await reordered.Content
            .ReadEnvelopeAsync<ProfilePropertyDefinitionDto>();

        afterReorder.Should().NotBeNull();
        afterReorder!.ViewOrder.Should().Be(
            definition.ViewOrder + 5,
            "ordering is a member of the definition rather than an action upon it");
    }

    /// <summary>
    /// A member quota of zero means UNLIMITED, so account creation is not refused at that value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This guards an inversion that reads as a bug in either direction. The legacy gate was
    /// <c>PortalSettings.Users &lt; PortalSettings.UserQuota Or UserInfo.IsSuperUser Or
    /// PortalSettings.UserQuota = 0</c> (<c>Website/admin/Users/ManageUsers.ascx.vb:L367</c>) - the third
    /// disjunct is what makes zero mean "no limit" rather than "no accounts permitted". A port that dropped it
    /// and compared the count against the quota alone would refuse EVERY create on a default tenant, because
    /// the <c>UserQuota</c> column defaults to zero and no seeded tenant sets it.
    /// </para>
    /// <para>
    /// The quota is read from the tenant row rather than assumed, so the test states the precondition it
    /// depends on instead of inheriting it. Two accounts are then created in succession: one create could
    /// succeed under a quota of one, whereas two cannot be explained by any positive bound the tenant does not
    /// hold.
    /// </para>
    /// <para>
    /// The refusal side is deliberately NOT asserted. At a quota of zero there is no bound to exceed, so no
    /// request can reach a quota refusal, and a test that manufactured one would be asserting a rule this
    /// tenant does not have.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateUser_UnderAZeroMemberQuota_IsNotRefused()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        int quota = await _fixture.Database.ScalarAsync<int>(
            "SELECT COALESCE(MAX([UserQuota]), 0) FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = _fixture.Seed.PortalId });

        quota.Should().Be(0, "the tenant column defaults to zero, which the legacy rule read as no limit");

        int before = (await ListAsync(client, string.Empty)).TotalCount;
        before.Should().BeGreaterThan(0, "the tenant already holds accounts, so any positive bound is met");

        UserDetailDto first = await CreateUserAsync(client);
        UserDetailDto second = await CreateUserAsync(client);

        second.UserId.Should().NotBe(first.UserId);

        int after = (await ListAsync(client, string.Empty)).TotalCount;
        after.Should().Be(before + 2, "neither create was refused, so both accounts joined the tenant");
    }
}
