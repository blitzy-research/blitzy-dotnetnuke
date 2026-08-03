using Asp.Versioning.ApiExplorer;
using DnnMigration.Application.Dtos.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that the published contract carries exactly one success shape, and that no Domain type reaches it.
/// </summary>
/// <remarks>
/// <para>
/// The defect this suite exists to prevent had two halves. The Application layer declared a success
/// envelope, a paging projection and a metadata companion, all documented as the contract every controller
/// returns - and not one of them had a single consumer anywhere in the solution. Meanwhile every collection
/// endpoint serialised the Domain paging type directly, so a Domain type WAS the public contract: its
/// members were the client's contract, and a change to the domain model would have been a breaking API
/// change made without touching an API file.
/// </para>
/// <para>
/// The generated OpenAPI document is the right subject for this, and the only one that can settle it. The
/// response assertions elsewhere in this suite prove that a particular endpoint returns the envelope; only
/// the document can prove that EVERY endpoint does, and that nothing else is even describable. That
/// distinction is the whole point of adding a permanent fact rather than inspecting the document once by
/// hand: the shape held at the moment of the repair either way, and only the fact keeps holding.
/// </para>
/// <para>
/// The document is generated in process from the running host's own service provider rather than fetched
/// over HTTP. The document endpoints are deliberately unmounted outside development and this host runs as
/// <c>Testing</c>, so reaching them would mean altering the very configuration under test. The generator
/// itself is registered unconditionally, so the document is available to a caller that asks for it directly
/// in every environment - which is exactly the seam this suite needs and the pipeline does not open.
/// </para>
/// <para>
/// Each fact generates the document once, because xUnit constructs the test class per fact. The document is
/// derived entirely from metadata and reaches no database, so repeating it cannot change an outcome; it is
/// paid for rather than cached because a static cache shared across facts is a worse thing to own than a
/// few repeated reflections.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class SuccessEnvelopeContractTests
{
    /// <summary>
    /// Suffix the generator appends when it names a schema for a closed generic, so the envelope closed over
    /// a portal detail payload becomes <c>PortalDetailDtoApiResponse</c>.
    /// </summary>
    private const string SingleEnvelopeSuffix = "ApiResponse";

    /// <summary>The same, for the paging projection.</summary>
    private const string PagedEnvelopeSuffix = "PagedResponse";

    /// <summary>
    /// Route template of the one action whose success is a document rather than a payload, written as the
    /// document renders it with the version substituted into the path.
    /// </summary>
    /// <remarks>
    /// A module export answers with the module's own content document under an XML media type. There is no
    /// payload to place in an envelope and no client that would benefit from one, so it is the single
    /// documented exception to the uniform success shape - named here so that the exception is a decision a
    /// reader can see and challenge, rather than a gap the assertion happens not to notice.
    /// </remarks>
    private const string ExportPath = "/api/v1/portals/{portalId}/modules/{moduleId}/export";

    private readonly OpenApiDocument _document;

    /// <summary>Initialises a new instance of the <see cref="SuccessEnvelopeContractTests"/> class.</summary>
    /// <param name="fixture">The shared API host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    public SuccessEnvelopeContractTests(ApiTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        using IServiceScope scope = fixture.Services.CreateScope();

        // The document name is read from the versioning explorer rather than written out here. A hard-coded
        // name would keep passing while describing nothing the day a second version is introduced, because
        // the generator would answer for the version this suite named and leave the new one unexamined.
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
    /// <remarks>
    /// This is the fact that makes the contract uniform rather than merely usually-uniform. One action
    /// publishing a bare payload would be invisible to every other assertion in this suite and would force
    /// every client to special-case it, which is the cost the envelope exists to remove. Inline schemas are
    /// examined as well as references, so an action that describes a bare shape without naming it is caught
    /// on the same terms as one that names it.
    /// </remarks>
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
    /// <remarks>
    /// The member names are asserted, not merely the schema's presence, because the member names are the
    /// difference a client would get wrong. The paging facts used to be siblings of the records and are now
    /// one level down under the companion; binding the old flat shape against the new body still yields the
    /// records while reading every paging number as zero, so a pager shows one page of everything instead of
    /// failing where a reader would notice.
    /// </remarks>
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

                // The role-membership listing. It is paged for the reason the others are - a popular role
                // holds more members than a screen can render - and it satisfies the condition this list
                // exists to enforce: SortableFields declares a vocabulary of its own for it, and the listing
                // composes the assignment rows with their accounts and orders by a name from that vocabulary
                // before it takes the page window. So it is a contract WITH behaviour behind it, which is why
                // it belongs here rather than being treated as an unbacked arrival.
                "RoleMembershipDtoPagedResponse",
                "UserListItemDtoPagedResponse",
            },
            "these are the listings this API pages, and one arriving here without a covering sort vocabulary "
            + "and an ordering implementation would be a contract with no behaviour behind it");

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
    /// Every single-payload success publishes its payload under one member name, with the metadata companion
    /// optional beside it.
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
    /// <remarks>
    /// Asserted rather than left implicit, because the absence is the decision. The Application layer
    /// declares a payload-free arity beside the generic form and it genuinely has no producer here: a command
    /// that returns nothing answers <c>204</c>, HTTP forbids a body on a <c>204</c>, and the acceptance
    /// criteria pin deletion to <c>204</c> for portals, modules and users. Attaching the envelope would mean
    /// demoting those responses to <c>200</c> to satisfy a type's symmetry, which trades a stated criterion
    /// for the tidiness of an unused declaration. This fact fails if a later revision makes that trade.
    /// </remarks>
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
}
