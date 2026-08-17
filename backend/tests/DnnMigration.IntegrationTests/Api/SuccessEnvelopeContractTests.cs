using System.Net;
using System.Text.Json;
using Asp.Versioning.ApiExplorer;
using DnnMigration.Application.Dtos.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that the published contract carries exactly one success shape, and that no Domain type reaches
/// it.
/// </summary>
/// <remarks>
/// The document is generated in process from the running host's own service provider rather than fetched
/// over HTTP. The document endpoints are deliberately unmounted outside development and this host runs as
/// <c>Testing</c>, so reaching them would mean altering the very configuration under test.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class SuccessEnvelopeContractTests
{
    /// <summary>
    /// Suffix the generator appends when it names a schema for a closed generic, so the envelope closed
    /// over a portal detail payload becomes <c>PortalDetailDtoApiResponse</c>.
    /// </summary>
    private const string SingleEnvelopeSuffix = "ApiResponse";

    /// <summary>The same, for the paging projection.</summary>
    private const string PagedEnvelopeSuffix = "PagedResponse";

    /// <summary>
    /// Route template of the one action whose success is a document rather than a payload, written as the
    /// document renders it with the version substituted into the path.
    /// </summary>
    private const string ExportPath = "/api/v1/modules/{moduleId}/export";

    private readonly OpenApiDocument _document;

    /// <summary>The composed host, held so that the live-wire fact can issue a real request.</summary>
    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="SuccessEnvelopeContractTests"/> class.</summary>
    /// <param name="fixture">The shared API host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    public SuccessEnvelopeContractTests(ApiTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        _fixture = fixture;

        using IServiceScope scope = fixture.Services.CreateScope();

        IApiVersionDescriptionProvider versions = scope.ServiceProvider
            .GetRequiredService<IApiVersionDescriptionProvider>();

        string documentName = versions.ApiVersionDescriptions
            .OrderBy(description => description.GroupName, StringComparer.Ordinal)
            .First()
            .GroupName;

        _document = scope.ServiceProvider
            .GetRequiredService<ISwaggerProvider>()
            .GetSwagger(documentName);
    }

    /// <summary>
    /// The document generates at all, which is not a formality: closing one generic envelope over two dozen
    /// payload types is precisely the change that provokes a duplicate schema identifier, and the generator
    /// answers that by throwing rather than by emitting a document with a collision in it.
    /// </summary>
    [Fact]
    public void Document_IsGeneratedAndDescribesEveryResource()
    {
        _document.Should().NotBeNull();
        _document.Paths.Should().NotBeEmpty();
        _document.Components.Schemas.Should().NotBeEmpty();
    }

    /// <summary>
    /// No Domain paging type appears in the published contract, which is the layering half of the repair.
    /// </summary>
    /// <remarks>
    /// Asserted by name because that is how the breach was observable: the Domain paging type produced
    /// schemas of its own beside the payload schemas, and a client generated from this document would have
    /// taken a dependency on them. The check is deliberately broader than one exact name - anything whose
    /// schema name betrays that type fails it, including a future closed generic over it.
    /// </remarks>
    [Fact]
    public void Document_CarriesNoDomainPagingType()
    {
        IEnumerable<string> leaked = _document.Components.Schemas.Keys
            .Where(name => name.Contains("PagedResult", StringComparison.Ordinal));

        leaked.Should().BeEmpty(
            "a Domain type on the public contract makes a change to the domain model a breaking API change, "
            + "which is the reason the Application layer declares a projection of its own");
    }

    /// <summary>
    /// Every payload-bearing success in the document is one of the two envelopes, with a single documented
    /// exception.
    /// </summary>
    [Fact]
    public void EveryPayloadBearingSuccess_IsOneOfTheTwoEnvelopes()
    {
        List<string> offenders = [];

        foreach (KeyValuePair<string, OpenApiPathItem> path in _document.Paths)
        {
            foreach (KeyValuePair<OperationType, OpenApiOperation> operation in path.Value.Operations)
            {
                foreach (KeyValuePair<string, OpenApiResponse> response in operation.Value.Responses)
                {
                    if (response.Key is not ("200" or "201"))
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, OpenApiMediaType> body in response.Value.Content)
                    {
                        string schema = body.Value.Schema?.Reference?.Id ?? string.Empty;

                        bool enveloped =
                            schema.EndsWith(SingleEnvelopeSuffix, StringComparison.Ordinal)
                            || schema.EndsWith(PagedEnvelopeSuffix, StringComparison.Ordinal);

                        if (enveloped || path.Key == ExportPath)
                        {
                            continue;
                        }

                        offenders.Add(
                            $"{operation.Key} {path.Key} {response.Key} {body.Key} -> "
                            + (schema.Length == 0 ? "(inline schema)" : schema));
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "every payload-bearing success must publish one of the two envelopes, so that a client writes "
            + "one unwrapping step rather than one per resource");
    }

    /// <summary>
    /// Every collection endpoint publishes the paging projection, and its shape is the records paired with
    /// the metadata companion.
    /// </summary>
    [Fact]
    public void EveryCollectionEndpoint_PublishesThePagingProjection()
    {
        IReadOnlyList<string> pagedSchemas = _document.Components.Schemas.Keys
            .Where(name => name.EndsWith(PagedEnvelopeSuffix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        pagedSchemas.Should().BeEquivalentTo(
            new[]
            {
                "ModuleListItemDtoPagedResponse",
                "PortalListItemDtoPagedResponse",
                "RoleListItemDtoPagedResponse",

                // The role-membership listing.
                "RoleMembershipDtoPagedResponse",

                "UserChoiceDtoPagedResponse",

                "UserListItemDtoPagedResponse",

                // THE TWO COLLECTIONS THAT PAGE IN ONE FIXED ORDER, AND THE DISTINCTION IS DELIBERATE. A
                // tenant's page listing is a hierarchy - depth-first by parent then by stored order - and an
                // account's member-service catalogue is a published price list. Neither has a second
                // meaningful order, so each pages without publishing a sort vocabulary and each refuses
                // `sortBy` and `query` outright. They are paged because their responses were unbounded: the
                // listing sent 764 KiB for a tenant with three thousand pages and the catalogue 437 KiB for a
                // tenant publishing a thousand services.
                "MemberServiceDtoPagedResponse",

                "TabListItemDtoPagedResponse",
            },
            "these are the listings this API pages, and one arriving here without either a covering sort "
            + "vocabulary and an ordering implementation, or a documented single order and a refusal of every "
            + "sort field, would be a contract with no behaviour behind it");

        foreach (string name in pagedSchemas)
        {
            _document.Components.Schemas[name].Properties.Keys.Should().BeEquivalentTo(
                new[] { "items", "meta" },
                "{0} must carry the records and the companion, with nothing flat beside them",
                name);
        }

        _document.Components.Schemas[nameof(ApiMeta)].Properties.Keys.Should().BeEquivalentTo(
            new[] { "totalCount", "pageIndex", "pageSize", "totalPages" },
            "the companion publishes the derived page count as well as the three stored facts, so that a "
            + "client reads it rather than recomputing it and disagreeing at the boundaries");
    }

    /// <summary>
    /// Every single-payload success publishes its payload under one member name, with the metadata
    /// companion optional beside it.
    /// </summary>
    /// <remarks>
    /// The companion appears on the schema without being required, which is the accurate description rather
    /// than a concession: it describes a page, a single payload has none, and reporting zeroes for it would
    /// be indistinguishable from a real and empty first page.
    /// </remarks>
    [Fact]
    public void EverySinglePayloadSuccess_PublishesThePayloadUnderOneMemberName()
    {
        IReadOnlyList<string> singleSchemas = _document.Components.Schemas.Keys
            .Where(name => name.EndsWith(SingleEnvelopeSuffix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        singleSchemas.Should().NotBeEmpty(
            "every read and every creation in this API answers with the single-payload envelope");

        foreach (string name in singleSchemas)
        {
            _document.Components.Schemas[name].Properties.Keys.Should().BeEquivalentTo(
                new[] { "data", "meta" },
                "{0} must carry the payload and the optional companion",
                name);
        }
    }

    /// <summary>
    /// The payload-free envelope appears nowhere in the document, and no response that reports no content
    /// carries a body.
    /// </summary>
    [Fact]
    public void ThePayloadFreeEnvelope_AppearsNowhereAndNoContentCarriesNoBody()
    {
        _document.Components.Schemas.Keys.Should().NotContain(
            nameof(ApiResponse),
            "a payload-free success in this API is a 204 with no body at all");

        List<string> bodied = [];

        foreach (KeyValuePair<string, OpenApiPathItem> path in _document.Paths)
        {
            foreach (KeyValuePair<OperationType, OpenApiOperation> operation in path.Value.Operations)
            {
                if (operation.Value.Responses.TryGetValue("204", out OpenApiResponse? response)
                    && response.Content.Count > 0)
                {
                    bodied.Add($"{operation.Key} {path.Key}");
                }
            }
        }

        bodied.Should().BeEmpty("HTTP forbids a body on a 204");
    }

    /// <summary>A single-payload success writes its metadata member PRESENT AND NULL, never omitted.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ASinglePayloadSuccess_WritesItsMetadataMemberAsNullRatherThanOmittingIt()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage scalar = await client.GetAsync(
            new Uri(
                FormattableString.Invariant($"/api/v1/portals/{_fixture.Seed.PortalId}"),
                UriKind.Relative));

        scalar.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument scalarDocument = JsonDocument.Parse(await scalar.Content.ReadAsStringAsync());
        JsonElement scalarRoot = scalarDocument.RootElement;

        scalarRoot.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
            new[] { "data", "meta" },
            "the single-payload envelope is exactly the payload and its companion");
        scalarRoot.GetProperty("meta").ValueKind.Should().Be(
            JsonValueKind.Null,
            "a scalar payload has no page to describe, and the serializer writes every declared member, so "
            + "the companion is present and null rather than omitted - which is why the client declares it "
            + "required and nullable");

        using HttpResponseMessage paged = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=10", UriKind.Relative));

        paged.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument pagedDocument = JsonDocument.Parse(await paged.Content.ReadAsStringAsync());
        JsonElement pagedRoot = pagedDocument.RootElement;

        pagedRoot.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
            new[] { "items", "meta" },
            "the paging projection is exactly the rows and their companion");
        pagedRoot.GetProperty("meta").ValueKind.Should().Be(
            JsonValueKind.Object,
            "a page HAS coordinates to report, so the same member is populated here - which is what makes "
            + "the null above meaningful rather than merely constant");
    }
}
