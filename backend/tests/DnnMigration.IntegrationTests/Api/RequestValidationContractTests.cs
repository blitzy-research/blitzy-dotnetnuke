using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves end to end that every write action's declared field-error response is actually reachable, and
/// that every sort field a listing advertises is actually applied.
/// </summary>
/// <remarks>
/// <para>
/// Two separate review findings meet in this suite, and both were about a promise the API made and did not
/// keep. Six write actions advertised a per-field error document while no validator resolved for their body
/// at all, so semantically invalid input travelled past the boundary and surfaced as a persistence fault or
/// a single-message problem document - either way carrying nothing a caller could act on. Four listings
/// advertised a sort vocabulary that was the union of all of them, so each accepted the others' field names,
/// answered <c>200</c>, and quietly ordered by something else.
/// </para>
/// <para>
/// A unit test cannot close either finding. Whether a validator exists is a unit question; whether the
/// framework RESOLVES it for the parameter an action actually binds, and whether its failure is rendered as
/// the declared document, are properties of the composed pipeline. Likewise, whether a repository has an
/// ordering arm is a unit question; whether the field survives the boundary, the service, and the
/// skip-and-take is not. So every fact below goes over HTTP.
/// </para>
/// <para>
/// The ordering facts assert the returned sequence rather than only the status code, because a status code
/// is exactly what the defect produced: before the repair every one of these requests answered <c>200</c>.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RequestValidationContractTests
{
    /// <summary>Wording of <c>valRoleGroupName</c> on the legacy group editor, its markup tag removed.</summary>
    private const string NameRequiredMessage = "You Must Enter a Valid Name";

    /// <summary>Wording of <c>valServiceFee2</c> on the legacy role editor.</summary>
    private const string ServiceFeeNegativeMessage =
        "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>Wording of <c>valDates</c> on the legacy role-assignment screen, its markup tag removed.</summary>
    private const string ExpiryNotAfterEffectiveMessage =
        "Expiry Date must be Greater than Effective Date";

    /// <summary>Message reported for an icon path that leaves the portal's own folder.</summary>
    private const string IconFileNotRelativeMessage =
        "Icon File must be a relative path within the portal's own folder.";

    /// <summary>Width of <c>RoleGroups.RoleGroupName</c>.</summary>
    private const int RoleGroupNameWidth = 50;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RequestValidationContractTests"/> class.</summary>
    /// <param name="fixture">The shared API host and seeded database.</param>
    public RequestValidationContractTests(ApiTestFixture fixture)
        => _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));

    // =============================================================================================
    // F4-01 - THE DECLARED FIELD-ERROR DOCUMENT IS REACHABLE ON EVERY REPAIRED WRITE ACTION
    // =============================================================================================

    /// <summary>
    /// Creating a role group without a name answers the declared field-error document naming the member,
    /// rather than the persistence fault the <c>NOT NULL</c> column used to produce.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateRoleGroup_WithoutAName_NamesTheOffendingMember()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleGroupsRoute(),
            new CreateRoleGroupRequest { RoleGroupName = string.Empty, Description = "No name." },
            ApiTestFixture.Json);

        await ShouldReportAsync(
            response,
            nameof(CreateRoleGroupRequest.RoleGroupName),
            NameRequiredMessage);
    }

    /// <summary>
    /// Updating a role group with an over-long name answers the declared document, so the width rule
    /// applies on the update verb as well as the create verb.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRoleGroup_WithAnOverlongName_NamesTheOffendingMember()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        RoleGroupDto created = await CreateRoleGroupAsync(client);

        // MIGRATION: the body no longer echoes the group key or the owning portal. Both verbs used to bind
        // RoleGroupDto, so a caller submitted three members of which the store read one; the write contract
        // now carries only what AddRoleGroup and UpdateRoleGroup actually write.
        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleGroupRoute(created.RoleGroupId),
            new UpdateRoleGroupRequest
            {
                RoleGroupName = new string('n', RoleGroupNameWidth + 1),
            },
            ApiTestFixture.Json);

        await ShouldNameAsync(response, nameof(UpdateRoleGroupRequest.RoleGroupName));
    }

    /// <summary>
    /// Updating a role with an icon path that leaves the portal's folder is refused, which is the rule the
    /// creation path enforced and the update path did not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the single most important fact in the suite. The value below was already refused by
    /// <c>POST</c>, so the column could only ever receive it through <c>PUT</c> - a rule a caller could
    /// bypass by choosing the other verb is not a rule. The stored value is read back afterwards to prove
    /// the refusal was a refusal and not merely a differently worded success.
    /// </remarks>
    [Fact]
    public async Task UpdateRole_WithAnEscapingIconPath_IsRefusedAndStoresNothing()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(role.RoleId),
            new UpdateRoleRequest { IconFile = "../../../etc/passwd" },
            ApiTestFixture.Json);

        await ShouldReportAsync(
            response,
            nameof(UpdateRoleRequest.IconFile),
            IconFileNotRelativeMessage);

        // Counted rather than read back as a value, because the column is null on a role that has never
        // carried an icon and the fixture's scalar reader treats a null result as "no value" and raises.
        // Counting the rows that DO hold a path asks the same question without that ambiguity.
        int storedPaths = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] "
            + "WHERE [RoleID] = @roleId AND [IconFile] IS NOT NULL AND [IconFile] <> '';",
            new Dictionary<string, object?> { ["roleId"] = role.RoleId });

        storedPaths.Should().Be(
            0,
            "the refusal must have stopped before the column, not after it");
    }

    /// <summary>
    /// Updating a role with a negative service fee answers the declared document carrying the legacy
    /// wording, so the fee rules apply on the update verb as well.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRole_WithANegativeServiceFee_NamesTheOffendingMember()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            RoleRoute(role.RoleId),
            new UpdateRoleRequest { ServiceFee = -5m },
            ApiTestFixture.Json);

        await ShouldReportAsync(
            response,
            nameof(UpdateRoleRequest.ServiceFee),
            ServiceFeeNegativeMessage);
    }

    /// <summary>
    /// Assigning a membership without naming a user answers the declared document, rather than the foreign
    /// key failure the assignment table used to produce.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AssignRole_WithoutAUser_NamesTheOffendingMember()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleUsersRoute(role.RoleId),
            new RoleAssignmentRequest { UserId = 0, NotifyUser = false },
            ApiTestFixture.Json);

        await ShouldNameAsync(response, nameof(RoleAssignmentRequest.UserId));
    }

    /// <summary>
    /// Assigning a membership whose expiry is not later than its effective date answers the declared
    /// document carrying the legacy wording, and writes nothing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Equality is used rather than an inverted window, because the legacy comparison was strictly greater
    /// than: a window that opens and closes at the same instant is a membership that is never in force, and
    /// a rule that admitted it would silently store a lapsed assignment.
    /// </remarks>
    [Fact]
    public async Task AssignRole_WithAnExpiryEqualToItsEffectiveDate_IsRefusedAndWritesNothing()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        RoleDetailDto role = await CreateRoleAsync(client);
        DateTime instant = new(2008, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleUsersRoute(role.RoleId),
            new RoleAssignmentRequest
            {
                UserId = _fixture.Seed.MemberUserId,
                EffectiveDate = instant,
                ExpiryDate = instant,
                NotifyUser = false,
            },
            ApiTestFixture.Json);

        await ShouldReportAsync(
            response,
            nameof(RoleAssignmentRequest.ExpiryDate),
            ExpiryNotAfterEffectiveMessage);

        int assignments = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserRoles] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = role.RoleId });

        assignments.Should().Be(0, "a refused assignment must leave the table untouched");
    }

    /// <summary>
    /// Creating a profile definition whose name breaks the legacy pattern answers the declared document
    /// naming the member.
    /// </summary>
    /// <param name="propertyName">The name the caller submitted.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The space case is deliberate and is the one a future reader is most likely to think is a bug: the
    /// property name is an identifier used as a resource key, and the legacy screen carried a separate
    /// localisation step for the human-readable label, so its pattern admits no space. The empty case is
    /// what used to reach the <c>NOT NULL</c> column.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("Preferred Name")]
    [InlineData("Na/me")]
    public async Task CreateProfileDefinition_WithAnInvalidName_NamesTheOffendingMember(string propertyName)
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        CreateProfilePropertyDefinitionRequest definition = NewDefinition();
        definition.PropertyName = propertyName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ProfileDefinitionsRoute(),
            definition,
            ApiTestFixture.Json);

        await ShouldNameAsync(response, nameof(CreateProfilePropertyDefinitionRequest.PropertyName));
    }

    /// <summary>
    /// Updating a profile definition with a property name the legacy pattern refuses answers the declared
    /// document, so the update verb is bounded as well as the create verb.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: the offending member used to be the visibility hint, submitted outside the three documented
    /// modes. No such bound exists any more and none should: the legacy member was assigned by converting a
    /// module setting without any check, so a value outside those three was representable and a caller that
    /// read a definition and sent it back unchanged would be refused by a rule this migration had invented.
    /// The property name carries a bound that IS measured - the regular expression declared on
    /// <c>ProfilePropertyDefinition.vb:L228</c> - so the fact still asserts what it exists to assert: this
    /// verb resolves a validator, rather than advertising a field-error document that nothing can produce.
    /// </remarks>
    [Fact]
    public async Task UpdateProfileDefinition_WithARefusedPropertyName_NamesTheOffendingMember()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        ProfilePropertyDefinitionDto created = await CreateDefinitionAsync(client);

        UpdateProfilePropertyDefinitionRequest amendment = NewDefinitionUpdate();
        amendment.PropertyCategory = created.PropertyCategory;
        amendment.PropertyName = "not a legal name";

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ProfileDefinitionRoute(created.PropertyDefinitionId),
            amendment,
            ApiTestFixture.Json);

        await ShouldNameAsync(response, nameof(UpdateProfilePropertyDefinitionRequest.PropertyName));
    }

    /// <summary>
    /// BOTH profile-definition write verbs really do resolve their own request validator, proven by a
    /// refusal only that validator can produce and by the exact wording it produces it with.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The two facts above name the offending member, which a refusal from model binding or from a column
    /// constraint could also do. This one cannot be satisfied by anything except the resolved validator: an
    /// over-long property CATEGORY breaks no binding rule, no route constraint and no column constraint that
    /// answers a field-error document - the column is <c>nvarchar(50)</c>, so a body that reached the store
    /// would be refused there as a server fault rather than as a named field - and the message asserted is
    /// the legacy wording declared on the shared rules type. Both together are the proof: the filter looked
    /// up an <c>IValidator&lt;T&gt;</c> for the DECLARED parameter type of each action and ran it.
    /// </para>
    /// <para>
    /// It is asserted on the create verb AND the update verb because they bind DIFFERENT contracts with
    /// DIFFERENT validators, and the filter resolves per parameter type. A validator registered for one and
    /// missing for the other would leave one verb silently unvalidated, which is exactly the gap a suite
    /// aimed at the wrong type would not see.
    /// </para>
    /// <para>
    /// MIGRATION: this fact replaces a unit suite that exercised the validator declared for the RESPONSE
    /// projection, <c>ProfilePropertyDefinitionDto</c>. No action binds that type, so no request path ever
    /// invoked it - assembly scanning registered it, which made it resolvable but not reachable - and its
    /// twenty-five tests reported confidence in rules the write path did not apply. Its genuinely
    /// legacy-derived assertions live on the two real request validators in
    /// backend/tests/DnnMigration.UnitTests/Validation/ProfileDefinitionWriteContractValidatorTests.cs, in
    /// the stronger both-verbs form, and the three it carried that had no legacy counterpart concerned
    /// members the write contracts do not publish at all. This test is the standing proof that the rules
    /// really are applied where the requests really arrive.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ProfileDefinitionWrites_AreValidatedByTheValidatorResolvedForEachVerb()
    {
        const string CategoryTooLongMessage = "Property Category must be 50 characters or fewer";

        string overlongCategory = new('C', 51);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        CreateProfilePropertyDefinitionRequest definition = NewDefinition();
        definition.PropertyCategory = overlongCategory;

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            ProfileDefinitionsRoute(),
            definition,
            ApiTestFixture.Json);

        await ShouldReportAsync(
            created,
            nameof(CreateProfilePropertyDefinitionRequest.PropertyCategory),
            CategoryTooLongMessage);

        ProfilePropertyDefinitionDto existing = await CreateDefinitionAsync(client);

        UpdateProfilePropertyDefinitionRequest amendment = NewDefinitionUpdate();
        amendment.PropertyName = existing.PropertyName;
        amendment.PropertyCategory = overlongCategory;

        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            ProfileDefinitionRoute(existing.PropertyDefinitionId),
            amendment,
            ApiTestFixture.Json);

        await ShouldReportAsync(
            updated,
            nameof(UpdateProfilePropertyDefinitionRequest.PropertyCategory),
            CategoryTooLongMessage);
    }

    /// <summary>
    /// The append instruction on the view order is still accepted, so narrowing the definition contract did
    /// not remove the affordance the terminal insert procedure implements.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Asserted alongside the refusals because it is the counterweight to them. Minus one is not an absence
    /// marker on this member: the terminal procedure branches on it and substitutes the current maximum
    /// order plus one, so a validator that refused it would have removed a real workflow while appearing to
    /// tighten a rule.
    /// </remarks>
    [Fact]
    public async Task CreateProfileDefinition_NamingTheAppendOrder_IsAccepted()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        CreateProfilePropertyDefinitionRequest definition = NewDefinition();
        definition.ViewOrder = -1;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ProfileDefinitionsRoute(),
            definition,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "-1 instructs the terminal insert procedure to append, and must not be read as an absence");
    }

    /// <summary>
    /// A write action still answers <c>400</c> when the body is absent altogether, now that the hand-written
    /// null checks have been removed from the controllers.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This fact exists because of what was removed, not what was added. Making the registered validation
    /// filter the sole invocation path meant deleting ten hand-written call sites, and several of those had
    /// also been checking the parameter for null. The check is not lost: the controllers carry
    /// <c>[ApiController]</c>, nullable reference types are enabled, and the invalid-model-state filter is
    /// not suppressed, so an absent body for a non-nullable parameter is refused by model binding before an
    /// action body runs. Asserting it keeps that guarantee from being a matter of belief.
    /// </remarks>
    [Fact]
    public async Task CreateRoleGroup_WithNoBodyAtAll_StillAnswersBadRequest()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using StringContent empty = new("null", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(RoleGroupsRoute(), empty);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an absent body must be refused by model binding, not reach the action");
    }

    // =============================================================================================
    // F4-03 - EVERY ADVERTISED SORT FIELD IS APPLIED, AND NO FOREIGN FIELD IS ADVERTISED
    // =============================================================================================

    /// <summary>
    /// The role listing applies the requested ordering, which it previously ignored entirely in favour of a
    /// fixed order by name.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Both directions are read and compared to each other rather than to a fixed expectation, so the fact
    /// holds however many roles the tenant happens to hold and whatever their identifiers are. Before the
    /// repair both requests answered <c>200</c> with the same name-ordered sequence, so the reversal is
    /// precisely what was missing.
    /// </remarks>
    [Fact]
    public async Task ListRoles_AppliesTheRequestedOrdering()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        IReadOnlyList<int> ascending = await ReadRoleIdsAsync(client, "RoleId", "Ascending");
        IReadOnlyList<int> descending = await ReadRoleIdsAsync(client, "RoleId", "Descending");

        ascending.Should().HaveCountGreaterThan(
            1,
            "the seeded tenant holds several roles, so an ordering is observable");
        ascending.Should().BeInAscendingOrder();
        descending.Should().Equal(
            ascending.Reverse(),
            "the descending page must be the ascending page reversed, which is only true if the field "
            + "reached the ordering at all");
    }

    /// <summary>
    /// The role listing orders by a field other than its default when asked, proving the field is read
    /// rather than the default silently reapplied.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListRoles_OrdersByNameDescendingWhenAsked()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        IReadOnlyList<string> names = await ReadRoleNamesAsync(client, "RoleName", "Descending");

        names.Should().HaveCountGreaterThan(1);
        names.Should().BeInDescendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The member listing applies the requested ordering, which it previously ignored entirely in favour of
    /// a fixed order by display name.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListUsers_AppliesTheRequestedOrdering()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        IReadOnlyList<int> ascending = await ReadUserIdsAsync(client, "UserId", "Ascending");
        IReadOnlyList<int> descending = await ReadUserIdsAsync(client, "UserId", "Descending");

        ascending.Should().HaveCountGreaterThan(
            1,
            "the seeded tenant holds several accounts, so an ordering is observable");
        ascending.Should().BeInAscendingOrder();
        descending.Should().Equal(ascending.Reverse());
    }

    /// <summary>
    /// The member listing orders by a field the repository has to reach the database for, and does so before
    /// the page is cut.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The single-row page is the point. Ordering applied after a page has been taken would return the first
    /// row of the DEFAULT order here, so a one-row descending page that carries the highest identifier
    /// proves the ordering preceded the skip and take rather than following it - which is the whole
    /// substance of the finding.
    /// </remarks>
    [Fact]
    public async Task ListUsers_OrdersBeforeItPages()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        IReadOnlyList<int> everyone = await ReadUserIdsAsync(client, "UserId", "Descending");
        IReadOnlyList<int> firstOfOne = await ReadUserIdsAsync(client, "UserId", "Descending", pageSize: 1);

        firstOfOne.Should().ContainSingle();
        firstOfOne[0].Should().Be(
            everyone[0],
            "a page of one taken from a descending ordering must carry the highest identifier, which is "
            + "only true if the database ordered before it skipped and took");
    }

    /// <summary>
    /// The portal listing accepts the two hosting fields it advertises, which used to fall through its
    /// ordering switch to the portal name.
    /// </summary>
    /// <param name="sortBy">The field the caller named.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("HostFee")]
    [InlineData("HostSpace")]
    public async Task ListPortals_AcceptsTheHostingFieldsItAdvertises(string sortBy)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/portals?pageIndex=0&pageSize=50&sortBy=" + sortBy,
            UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "{0} is advertised by this listing and is now honoured by its ordering switch",
            sortBy);
    }

    /// <summary>
    /// Each listing refuses the sort field names that belong to a different collection, naming the offending
    /// member, instead of accepting them and quietly ordering by something else.
    /// </summary>
    /// <param name="route">The listing addressed.</param>
    /// <param name="sortBy">A field name that belongs only to another collection.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Every one of these combinations answered <c>200</c> before the repair, because one shared request
    /// type resolved one validator that applied the union of all four vocabularies. The refusal is asserted
    /// per listing because the defect was per listing.
    /// </remarks>
    [Theory]
    [InlineData("portals", "TrialFrequency")]
    [InlineData("portals", "DisplayName")]
    [InlineData("roles", "HostSpace")]
    [InlineData("roles", "Username")]
    [InlineData("users", "ServiceFee")]
    [InlineData("users", "HostFee")]
    [InlineData("modules", "RoleName")]
    [InlineData("modules", "PortalName")]
    public async Task EachListing_RefusesAnotherCollectionsSortField(string route, string sortBy)
    {
        using HttpClient client = route == "portals"
            ? await _fixture.CreateHostClientAsync()
            : await _fixture.CreateAdministratorClientAsync();

        Uri address = route == "portals"
            ? new Uri("/api/v1/portals?pageIndex=0&pageSize=10&sortBy=" + sortBy, UriKind.Relative)
            : new Uri(
                FormattableString.Invariant(
                    $"/api/v1/{route}?pageIndex=0&pageSize=10&sortBy={sortBy}"),
                UriKind.Relative);

        using HttpResponseMessage response = await client.GetAsync(address);

        await ShouldNameAsync(response, nameof(PagedRequest.SortBy));
    }

    /// <summary>
    /// The member listing refuses the three fields it projects but cannot order in the database, so no
    /// caller is told a request was honoured when it could not have been.
    /// </summary>
    /// <param name="sortBy">The field the caller named.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Each is a real member of the listing's representation, which is why refusing them looks wrong and is
    /// not: two have no column on the entity in this model and the third lives in the external membership
    /// store, so all three are composed after the page has been skipped and taken. Ordering by any of them
    /// could only re-order one arbitrary page.
    /// </remarks>
    [Theory]
    [InlineData("CreatedDate")]
    [InlineData("LastLoginDate")]
    [InlineData("IsApproved")]
    public async Task ListUsers_RefusesTheFieldsItCannotOrderInTheDatabase(string sortBy)
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            FormattableString.Invariant(
                $"/api/v1/users?pageIndex=0&pageSize=10&sortBy={sortBy}"),
            UriKind.Relative));

        await ShouldNameAsync(response, nameof(PagedRequest.SortBy));
    }

    // =============================================================================================
    // HELPERS
    // =============================================================================================

    /// <summary>Reads the role identifiers one ordered listing returns.</summary>
    /// <param name="client">An authenticated client.</param>
    /// <param name="sortBy">The field to order by.</param>
    /// <param name="sortDir">The direction to order in.</param>
    /// <returns>The identifiers, in the order the API returned them.</returns>
    private async Task<IReadOnlyList<int>> ReadRoleIdsAsync(
        HttpClient client,
        string sortBy,
        string sortDir)
    {
        PagedEnvelope<RoleListItemDto> page = await ReadRolePageAsync(client, sortBy, sortDir);

        return page.Items.Select(role => role.RoleId).ToList();
    }

    /// <summary>Reads the role names one ordered listing returns.</summary>
    /// <param name="client">An authenticated client.</param>
    /// <param name="sortBy">The field to order by.</param>
    /// <param name="sortDir">The direction to order in.</param>
    /// <returns>The names, in the order the API returned them.</returns>
    private async Task<IReadOnlyList<string>> ReadRoleNamesAsync(
        HttpClient client,
        string sortBy,
        string sortDir)
    {
        PagedEnvelope<RoleListItemDto> page = await ReadRolePageAsync(client, sortBy, sortDir);

        return page.Items.Select(role => role.RoleName).ToList();
    }

    /// <summary>Reads one page of the role listing.</summary>
    /// <param name="client">An authenticated client.</param>
    /// <param name="sortBy">The field to order by.</param>
    /// <param name="sortDir">The direction to order in.</param>
    /// <returns>The page the API returned.</returns>
    private async Task<PagedEnvelope<RoleListItemDto>> ReadRolePageAsync(
        HttpClient client,
        string sortBy,
        string sortDir)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(
            FormattableString.Invariant(
                $"/api/v1/roles?pageIndex=0&pageSize=100&sortBy={sortBy}&sortDir={sortDir}"),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<RoleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<RoleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!;
    }

    /// <summary>Reads the account identifiers one ordered listing returns.</summary>
    /// <param name="client">An authenticated client.</param>
    /// <param name="sortBy">The field to order by.</param>
    /// <param name="sortDir">The direction to order in.</param>
    /// <param name="pageSize">The page size to request.</param>
    /// <returns>The identifiers, in the order the API returned them.</returns>
    private async Task<IReadOnlyList<int>> ReadUserIdsAsync(
        HttpClient client,
        string sortBy,
        string sortDir,
        int pageSize = 100)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(
            FormattableString.Invariant(
                $"/api/v1/users?pageIndex=0&pageSize={Route(pageSize)}&sortBy={sortBy}&sortDir={sortDir}"),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<UserListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<UserListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!.Items.Select(account => account.UserId).ToList();
    }

    /// <summary>Creates a role through the API.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <returns>The created role.</returns>
    private async Task<RoleDetailDto> CreateRoleAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/roles", UriKind.Relative),
            new CreateRoleRequest
            {
                RoleName = "VTest Role " + Suffix(),
                Description = "Created by the validation-contract suite.",
                IsPublic = false,
                AutoAssignment = false,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleDetailDto? created = await response.Content
            .ReadEnvelopeAsync<RoleDetailDto>();

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>Creates a role group through the API.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <returns>The created group.</returns>
    private async Task<RoleGroupDto> CreateRoleGroupAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RoleGroupsRoute(),
            new CreateRoleGroupRequest
            {
                RoleGroupName = "VTest Group " + Suffix(),
                Description = "Created by the validation-contract suite.",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        RoleGroupDto? created = await response.Content
            .ReadEnvelopeAsync<RoleGroupDto>();

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>Creates a profile property definition through the API.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <returns>The created definition.</returns>
    private async Task<ProfilePropertyDefinitionDto> CreateDefinitionAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ProfileDefinitionsRoute(),
            NewDefinition(),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        ProfilePropertyDefinitionDto? created = await response.Content
            .ReadEnvelopeAsync<ProfilePropertyDefinitionDto>();

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>Builds a well formed profile property definition whose name carries a random suffix.</summary>
    /// <returns>A definition the validator accepts.</returns>
    /// <remarks>
    /// The data-type key is zero deliberately. The list subsystem that would supply a real key is outside
    /// this migration's scope, so no endpoint can offer one, which is why the rule admits zero rather than
    /// demanding a positive key that a caller has no way to obtain.
    /// </remarks>
    private static CreateProfilePropertyDefinitionRequest NewDefinition() => new()
    {
        DataType = 0,
        PropertyCategory = "Address",
        PropertyName = "VTestCity" + Suffix(),
        Length = 50,
        Required = false,
        ViewOrder = 1,
        Visible = true,
        DefaultValue = string.Empty,
    };

    /// <summary>Builds a well formed profile-definition update whose name carries a random suffix.</summary>
    /// <returns>An update the validator accepts.</returns>
    /// <remarks>
    /// MIGRATION: the update verb binds its own contract because the terminal procedures honour different
    /// member sets - <c>AddPropertyDefinition</c> declares a module-definition key that
    /// <c>UpdatePropertyDefinition</c> does not - so the builder above cannot serve both verbs. Sending the
    /// narrower shape here is what makes these tests exercise the schema the boundary now advertises rather
    /// than a wider one the deserialiser happens to tolerate.
    /// </remarks>
    private static UpdateProfilePropertyDefinitionRequest NewDefinitionUpdate() => new()
    {
        DataType = 0,
        PropertyCategory = "Address",
        PropertyName = "VTestCity" + Suffix(),
        Length = 50,
        Required = false,
        ViewOrder = 1,
        Visible = true,
        DefaultValue = string.Empty,
    };

    /// <summary>Asserts that a response is the declared field-error document naming one member.</summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="member">The member the document must name.</param>
    /// <returns>A task representing the assertion.</returns>
    private static async Task ShouldNameAsync(HttpResponseMessage response, string member)
    {
        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a semantically invalid body must be refused at the boundary, not further in");

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Errors.Should().ContainKey(
            member,
            "the declared document attributes a failure to the member the caller sent, and an error "
            + "response that named no member would give the caller nothing to correct");
    }

    /// <summary>
    /// Asserts that a response is the declared field-error document naming one member and carrying one
    /// exact message.
    /// </summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="member">The member the document must name.</param>
    /// <param name="message">The message the document must carry for that member.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The message is compared exactly rather than by substring, because the legacy parity obligation is
    /// about the wording an operator reads and a substring match cannot tell the migrated text apart from
    /// the legacy text with its leading markup tag still attached.
    /// </remarks>
    private static async Task ShouldReportAsync(
        HttpResponseMessage response,
        string member,
        string message)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey(member);
        problem.Errors[member].Should().Contain(
            message,
            "the wording travels to the operator, so it must be the legacy wording exactly");
    }

    /// <summary>Builds the role-group collection route for the seeded tenant.</summary>
    /// <returns>A relative route.</returns>
    private Uri RoleGroupsRoute() =>
        new("/api/v1/role-groups", UriKind.Relative);

    /// <summary>Builds the item route for one role group of the seeded tenant.</summary>
    /// <param name="roleGroupId">The group identifier.</param>
    /// <returns>A relative route.</returns>
    private Uri RoleGroupRoute(int roleGroupId) => new(
        $"/api/v1/role-groups/{Route(roleGroupId)}",
        UriKind.Relative);

    /// <summary>Builds the item route for one role of the seeded tenant.</summary>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>A relative route.</returns>
    private Uri RoleRoute(int roleId) => new(
        $"/api/v1/roles/{Route(roleId)}",
        UriKind.Relative);

    /// <summary>Builds the accounts sub-resource route for one role of the seeded tenant.</summary>
    /// <param name="roleId">The role identifier.</param>
    /// <returns>A relative route.</returns>
    private Uri RoleUsersRoute(int roleId) => new(
        $"/api/v1/roles/{Route(roleId)}/users",
        UriKind.Relative);

    /// <summary>Builds the profile-definition collection route for the seeded tenant.</summary>
    /// <returns>A relative route.</returns>
    private Uri ProfileDefinitionsRoute() => new(
        "/api/v1/profile-definitions",
        UriKind.Relative);

    /// <summary>Builds the item route for one profile definition of the seeded tenant.</summary>
    /// <param name="propertyDefinitionId">The definition identifier.</param>
    /// <returns>A relative route.</returns>
    private Uri ProfileDefinitionRoute(int propertyDefinitionId) => new(
        FormattableString.Invariant(
            $"/api/v1/profile-definitions/{Route(propertyDefinitionId)}"),
        UriKind.Relative);

    /// <summary>Renders an identifier for a route segment without culture sensitivity.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant rendering.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix so concurrently created rows cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
