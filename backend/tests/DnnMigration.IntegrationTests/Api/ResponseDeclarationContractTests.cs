using Asp.Versioning.ApiExplorer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that every response this API advertises is one the pipeline can actually produce, and that the
/// body shape advertised for it is the body shape it carries.
/// </summary>
/// <remarks>
/// <para>
/// The defect this suite exists to prevent was a published contract that described a different API from the
/// one running. Thirteen actions took nothing but route values constrained to integers, and every one of
/// them advertised a <c>400</c> carrying a validation document naming the offending parameter. No such
/// response was reachable: a route value that is not an integer fails the route constraint, so the request
/// matches no endpoint and the router answers <c>404</c> before any action, filter or model binder runs.
/// A client written against that description would have carried a branch that could never be taken, and -
/// worse - would have had no branch for the <c>404</c> it actually receives. Removal of a definition also
/// advertised a <c>409</c> for a state conflict the service has no way to report.
/// </para>
/// <para>
/// The two facts below are deliberately expressed over the PUBLISHED DOCUMENT rather than over the
/// attributes, because the document is what a client is generated from and the attributes are only one of
/// several inputs to it. The explorer drops a declared media type it believes no formatter can write, and
/// the framework supplies a body type for a declaration that names none; both happen after the attribute is
/// written and before the document is served, so a test that read the attributes would pass while the
/// published description was still wrong.
/// </para>
/// <para>
/// Generating the document is not free, and it is paid for per fact rather than cached in a static, for the
/// same reason the sibling envelope suite gives: a cache shared across facts is a worse thing to own than a
/// few repeated reflections.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class ResponseDeclarationContractTests
{
    /// <summary>The body every failure in this API carries.</summary>
    private const string ProblemDocument = "ProblemDetails";

    /// <summary>
    /// The body a failure carries when it can name the parts of the request that were refused.
    /// </summary>
    private const string ValidationDocument = "ValidationProblemDetails";

    /// <summary>
    /// Route template of the one success that is a document rather than a payload, written as the published
    /// document renders it with the version substituted into the path.
    /// </summary>
    private const string ExportPath = "/api/v1/portals/{portalId}/modules/{moduleId}/export";

    /// <summary>The media type a module's exported content is served as.</summary>
    private const string ExportMediaType = "application/xml";

    /// <summary>The media type every other response in this API is served as.</summary>
    private const string JsonMediaType = "application/json";

    private readonly OpenApiDocument _document;

    /// <summary>
    /// Initialises a new instance of the <see cref="ResponseDeclarationContractTests"/> class.
    /// </summary>
    /// <param name="fixture">The shared API host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    public ResponseDeclarationContractTests(ApiTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        using IServiceScope scope = fixture.Services.CreateScope();

        string documentName = scope.ServiceProvider
            .GetRequiredService<IApiVersionDescriptionProvider>()
            .ApiVersionDescriptions
            .OrderBy(description => description.GroupName, StringComparer.Ordinal)
            .First()
            .GroupName;

        _document = scope.ServiceProvider
            .GetRequiredService<ISwaggerProvider>()
            .GetSwagger(documentName);
    }

    /// <summary>
    /// Every failure this API advertises carries the problem document, and none advertises an empty body.
    /// </summary>
    /// <remarks>
    /// A declaration that names no type publishes a status with no body at all in some framework versions
    /// and an unnamed inline shape in others, and both read to a client as "this status carries nothing".
    /// Every failure here does carry something: a single exception handler and a single authorisation
    /// result handler write the same problem document for refusals raised anywhere in the pipeline,
    /// including the token and policy refusals that used to answer with an empty body. Asserting the
    /// schema by name is what keeps that uniformity visible in the description rather than only in the
    /// implementation.
    /// </remarks>
    [Fact]
    public void EveryAdvertisedFailure_CarriesTheProblemDocument()
    {
        List<string> offenders = [];

        foreach ((string path, OperationType verb, string status, OpenApiResponse response) in Failures())
        {
            if (response.Content.Count == 0)
            {
                offenders.Add($"{verb} {path} {status} -> no body advertised");

                continue;
            }

            foreach (KeyValuePair<string, OpenApiMediaType> body in response.Content)
            {
                string schema = body.Value.Schema?.Reference?.Id ?? string.Empty;

                if (schema is not (ProblemDocument or ValidationDocument))
                {
                    offenders.Add(
                        $"{verb} {path} {status} {body.Key} -> "
                        + (schema.Length == 0 ? "(inline schema)" : schema));
                }
            }
        }

        offenders.Should().BeEmpty(
            "a client parses one failure shape for this whole API, so a status advertised without one - or "
            + "with a shape of its own - forces a special case for an endpoint that does not have one");
    }

    /// <summary>
    /// An operation that accepts nothing but its path never advertises a validation document, and one that
    /// does accept content always does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE FACT THAT WOULD HAVE CAUGHT THE ORIGINAL DEFECT. The validation document differs from the
    /// plain problem document by carrying a map of the request members that were refused. An operation whose
    /// only input is a path constrained to integers has no member that can be refused: a value of the wrong
    /// shape fails the route constraint and is answered by the router as a missing resource, so the action
    /// is never reached and no map can be produced. Advertising the validation document there described a
    /// response that could not occur.
    /// </para>
    /// <para>
    /// The converse half is asserted in the same fact deliberately, because the two halves are one rule
    /// stated from either side and splitting them would let a correction to one silently break the other.
    /// A route-only operation MAY still refuse a well-formed request on state grounds - a lock that is not
    /// held, a tenant that must retain one portal - and such a refusal is a plain problem document, which is
    /// why the rule constrains the SHAPE rather than forbidding the status.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheValidationDocument_IsAdvertisedExactlyWhereTheRequestCarriesContent()
    {
        List<string> offenders = [];

        foreach (KeyValuePair<string, OpenApiPathItem> path in _document.Paths)
        {
            foreach (KeyValuePair<OperationType, OpenApiOperation> operation in path.Value.Operations)
            {
                bool carriesContent =
                    operation.Value.RequestBody is not null
                    || operation.Value.Parameters.Any(
                        parameter => parameter.In == ParameterLocation.Query);

                if (!operation.Value.Responses.TryGetValue("400", out OpenApiResponse? refusal))
                {
                    continue;
                }

                foreach (KeyValuePair<string, OpenApiMediaType> body in refusal.Content)
                {
                    string schema = body.Value.Schema?.Reference?.Id ?? string.Empty;
                    bool namesTheMembers = schema == ValidationDocument;

                    if (namesTheMembers != carriesContent)
                    {
                        offenders.Add(
                            $"{operation.Key} {path.Key} 400 -> {schema} "
                            + $"(request carries content: {carriesContent})");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "the validation document names the request members that were refused, so it belongs exactly "
            + "where the request has members to refuse - a path constrained to integers has none, because "
            + "a value of the wrong shape never reaches the action");
    }

    /// <summary>
    /// The one success that is a document rather than a payload is published under the media type it is
    /// actually served as.
    /// </summary>
    /// <remarks>
    /// A module's exported content is that module's own XML, written straight to the response, and a caller
    /// saves the body as a file exactly as the legacy page produced one. The explorer describes a response
    /// with the media types declared for the whole action and then keeps only those a registered output
    /// formatter can write, so the XML declaration was discarded and the document advertised JSON - a
    /// contract that would have had a client parsing a document as a string field. This is asserted as an
    /// exact media type, and asserted alongside the fact that nothing ELSE in the document departs from
    /// JSON, so the exception stays a single decision a reader can see rather than a licence.
    /// </remarks>
    [Fact]
    public void TheExportedDocument_IsPublishedUnderItsOwnMediaType()
    {
        OpenApiResponse exported = _document
            .Paths[ExportPath]
            .Operations[OperationType.Post]
            .Responses["200"];

        exported.Content.Keys.Should().Equal([ExportMediaType]);

        IEnumerable<string> departures = _document.Paths
            .SelectMany(path => path.Value.Operations.Select(
                operation => (Path: path.Key, Verb: operation.Key, Operation: operation.Value)))
            .SelectMany(entry => entry.Operation.Responses.SelectMany(
                response => response.Value.Content.Keys.Select(
                    mediaType => (entry.Path, entry.Verb, response.Key, mediaType))))
            .Where(entry => entry.mediaType != JsonMediaType)
            .Where(entry => entry.Path != ExportPath)
            .Select(entry => $"{entry.Verb} {entry.Path} {entry.Key} -> {entry.mediaType}");

        departures.Should().BeEmpty(
            "every other response in this API is JSON, and a second media type would be a second parsing "
            + "path for a client to discover rather than a documented exception");
    }

    /// <summary>Yields every failure response the document advertises.</summary>
    /// <returns>The path, verb, status and response of each advertised failure.</returns>
    private IEnumerable<(string Path, OperationType Verb, string Status, OpenApiResponse Response)> Failures()
    {
        foreach (KeyValuePair<string, OpenApiPathItem> path in _document.Paths)
        {
            foreach (KeyValuePair<OperationType, OpenApiOperation> operation in path.Value.Operations)
            {
                foreach (KeyValuePair<string, OpenApiResponse> response in operation.Value.Responses)
                {
                    if (int.TryParse(response.Key, out int status) && status >= 400)
                    {
                        yield return (path.Key, operation.Key, response.Key, response.Value);
                    }
                }
            }
        }
    }
}
