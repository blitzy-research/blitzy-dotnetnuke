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
/// them advertised a <c>400</c> carrying a validation document naming the offending parameter.
/// </para>
/// <para>
/// The two facts below are deliberately expressed over the PUBLISHED DOCUMENT rather than over the
/// attributes, because the document is what a client is generated from and the attributes are only one of
/// several inputs to it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class ResponseDeclarationContractTests
{
    /// <summary>The body every failure in this API carries.</summary>
    /// <summary>The media type RFC 7807 section 3 registers for a problem document.</summary>
    private const string ProblemMediaType = "application/problem+json";

    /// <summary>The status advertised for a path matched under a method it does not accept.</summary>
    private const string MethodNotAllowed = "405";

    /// <summary>The status advertised for a body beyond the configured request-size ceiling.</summary>
    private const string PayloadTooLarge = "413";

    /// <summary>The status advertised for a body under a media type no input formatter reads.</summary>
    private const string UnsupportedMediaType = "415";

    private const string ProblemDocument = "ProblemDetails";

    /// <summary>The body a failure carries when it can name the parts of the request that were refused.</summary>
    private const string ValidationDocument = "ValidationProblemDetails";

    /// <summary>
    /// Route template of the one success that is a document rather than a payload, written as the published
    /// document renders it with the version substituted into the path.
    /// </summary>
    private const string ExportPath = "/api/v1/modules/{moduleId}/export";

    /// <summary>The media type a module's exported content is served as.</summary>
    private const string ExportMediaType = "application/xml";

    /// <summary>The media type every other response in this API is served as.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// Trailing segment of the single canonical profile-definition member resource, matched as a suffix so
    /// the versioned prefix does not have to be repeated in the assertion.
    /// </summary>
    private const string ProfileDefinitionResourceSuffix = "/profile-definitions/{propertyDefinitionId}";

    /// <summary>
    /// The operations that can refuse a fully-bound, fully-validated request on semantic grounds, and which
    /// therefore advertise the common supertype for <c>400</c> rather than the validation document.
    /// </summary>
    /// <remarks>
    /// THREE ENTRIES NAME THREE ACTIONS. Each action has one canonical route, so the literal roster and the
    /// action roster are now one-to-one. The exemption remains keyed by published operation identity
    /// because that is the contract a generated client consumes.
    /// </remarks>
    private static readonly IReadOnlyList<string> SemanticRefusalOperations =
    [
        "Post /api/v1/portals",
        "Get /api/v1/roles",
        "Get /api/v1/permissions",
    ];

    private readonly OpenApiDocument _document;

    /// <summary>Initialises a new instance of the <see cref="ResponseDeclarationContractTests"/> class.</summary>
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

    /// <summary>The published document contains only the AAP-canonical route family for each operation.</summary>
    [Fact]
    public void RouteSurface_PublishesCanonicalFamiliesAndNoWithdrawnDuplicates()
    {
        string[] canonical =
        [
            "/api/v1/modules",
            "/api/v1/modules/{moduleId}",
            "/api/v1/users",
            "/api/v1/users/settings",
            "/api/v1/roles",
            "/api/v1/role-groups",
            "/api/v1/profile-definitions",
            "/api/v1/portals/{portalId}/aliases",
            "/api/v1/permissions",
            "/api/v1/permissions/{permissionId}",
        ];

        string[] withdrawn =
        [
            "/api/v1/portals/{portalId}/modules",
            "/api/v1/portals/{portalId}/users",
            "/api/v1/portals/{portalId}/roles",
            "/api/v1/portals/{portalId}/role-groups",
            "/api/v1/portals/{portalId}/profile-definitions",
            "/api/v1/portal-aliases",
            "/api/v1/permissions/modules/{moduleId}",
            "/api/v1/permissions/tabs/{tabId}",
        ];

        _document.Paths.Keys.Should().Contain(
            canonical,
            "every AAP-authorized resource family must be discoverable at its canonical address");
        _document.Paths.Keys.Should().NotContain(
            withdrawn,
            "the frozen API permits one public identity per operation, not compatibility aliases");
    }

    /// <summary>
    /// Every advertised problem document is advertised under the media type RFC 7807 registers for it.
    /// </summary>
    /// <remarks>
    /// The explorer keys a response body by the media types an output formatter can write, and the JSON
    /// formatter reports <c>application/json</c> ahead of <c>application/problem+json</c> - so every
    /// refusal in this document claimed the plain JSON media type while the pipeline serves problem
    /// documents under the registered one.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public void EveryAdvertisedProblemDocument_IsAdvertisedUnderTheProblemMediaType()
    {
        List<string> offenders = [];

        foreach ((string path, OperationType verb, string status, OpenApiResponse response) in Failures())
        {
            foreach (KeyValuePair<string, OpenApiMediaType> body in response.Content)
            {
                string schema = body.Value.Schema?.Reference?.Id ?? string.Empty;

                if (schema is not (ProblemDocument or ValidationDocument))
                {
                    continue;
                }

                if (!string.Equals(body.Key, ProblemMediaType, StringComparison.Ordinal))
                {
                    offenders.Add($"{verb} {path} {status} -> {body.Key}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a problem document is served as " + ProblemMediaType + ", so advertising it as anything else "
            + "describes a response this API does not send");
    }

    /// <summary>
    /// The refusals decided by the TRANSPORT are advertised: the wrong method on every operation, and an
    /// oversized or unreadable body on every operation that accepts one.
    /// </summary>
    /// <remarks>
    /// The BODY-conditional half is asserted in both directions. Declaring 413 or 415 on an operation that
    /// accepts no body would describe a refusal that cannot occur, which is the same defect as omitting one
    /// that can - so an operation with no request body must NOT advertise either.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public void TheTransportRefusals_AreAdvertisedWhereTheyCanOccurAndNowhereElse()
    {
        List<string> missing = [];
        List<string> spurious = [];

        foreach (KeyValuePair<string, OpenApiPathItem> path in _document.Paths)
        {
            foreach (KeyValuePair<OperationType, OpenApiOperation> operation in path.Value.Operations)
            {
                IDictionary<string, OpenApiResponse> responses = operation.Value.Responses;
                bool acceptsBody = operation.Value.RequestBody is not null;

                if (!responses.ContainsKey(MethodNotAllowed))
                {
                    missing.Add($"{operation.Key} {path.Key} -> {MethodNotAllowed}");
                }

                foreach (string status in new[] { PayloadTooLarge, UnsupportedMediaType })
                {
                    bool advertised = responses.ContainsKey(status);

                    if (acceptsBody && !advertised)
                    {
                        missing.Add($"{operation.Key} {path.Key} -> {status}");
                    }
                    else if (!acceptsBody && advertised)
                    {
                        spurious.Add($"{operation.Key} {path.Key} -> {status}");
                    }
                }
            }
        }

        missing.Should().BeEmpty(
            "a caller cannot handle a refusal the contract does not mention, and all three of these are "
            + "reachable on the operations named");
        spurious.Should().BeEmpty(
            "an operation that accepts no body can be refused for neither reason, so advertising either "
            + "describes a response that cannot occur");
    }

    /// <summary>No DERIVED model member is published as a query parameter.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void NoDerivedMember_IsPublishedAsAQueryParameter()
    {
        HashSet<string> published = new(StringComparer.OrdinalIgnoreCase);

        foreach (OpenApiPathItem path in _document.Paths.Values)
        {
            foreach (OpenApiOperation operation in path.Operations.Values)
            {
                foreach (OpenApiParameter parameter in operation.Parameters)
                {
                    if (parameter.In == ParameterLocation.Query)
                    {
                        published.Add(parameter.Name);
                    }
                }
            }
        }

        published.Should().NotContain(
            "HasSort",
            "the member is computed from SortBy and has no setter, so a supplied value is discarded");
        published.Should().NotContain(
            "HasQuery",
            "the member is computed from Query and has no setter, so a supplied value is discarded");

        published.Should().Contain(
            ["PageIndex", "PageSize", "SortBy", "SortDir", "Query"],
            "the bindable paging members must still be published, or the removal took the contract with it");
    }

    /// <summary>Every published enumeration names its members rather than publishing bare numbers.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void EveryPublishedEnumeration_NamesItsMembers()
    {
        List<string> unnamed = [];

        foreach (KeyValuePair<string, OpenApiSchema> schema in _document.Components.Schemas)
        {
            if (schema.Value.Enum is null || schema.Value.Enum.Count == 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(schema.Value.Description)
                || !schema.Value.Description.Contains("Members:", StringComparison.Ordinal))
            {
                unnamed.Add(schema.Key);
            }
        }

        unnamed.Should().BeEmpty(
            "a bare list of numbers is not a contract a client can implement against without reading this "
            + "API's source");

        // Stated positively for the one this defect was reported against, so the rule above cannot be
        // satisfied by a document that publishes no enumeration at all.
        _document.Components.Schemas.Should().ContainKey("SortDirection");
        _document.Components.Schemas["SortDirection"].Description
            .Should().Contain("0 = Ascending").And.Contain("1 = Descending");
    }

    /// <summary>
    /// Every failure this API advertises carries the problem document, and none advertises an empty body.
    /// </summary>
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

    /// <summary>An operation that accepts nothing but its path never advertises a validation document.</summary>
    /// <remarks>
    /// THIS IS THE FACT THAT WOULD HAVE CAUGHT THE ORIGINAL DEFECT. The validation document differs from
    /// the plain problem document by carrying a map of the request members that were refused.
    /// </remarks>
    [Fact]
    public void AnOperationThatAcceptsNothingButItsPath_NeverAdvertisesTheValidationDocument()
    {
        List<string> offenders = [];

        foreach (KeyValuePair<string, OpenApiPathItem> path in _document.Paths)
        {
            foreach (KeyValuePair<OperationType, OpenApiOperation> operation in path.Value.Operations)
            {
                if (CarriesContent(operation.Value))
                {
                    continue;
                }

                if (!operation.Value.Responses.TryGetValue("400", out OpenApiResponse? refusal))
                {
                    continue;
                }

                foreach (KeyValuePair<string, OpenApiMediaType> body in refusal.Content)
                {
                    if ((body.Value.Schema?.Reference?.Id ?? string.Empty) == ValidationDocument)
                    {
                        offenders.Add($"{operation.Key} {path.Key} 400 -> {ValidationDocument}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "the validation document names the request members that were refused, so it cannot belong to an "
            + "operation with no members to refuse - a path constrained to integers has none, because a "
            + "value of the wrong shape never reaches the action");
    }

    /// <summary>
    /// An operation that accepts content advertises the validation document, unless it can also refuse a
    /// fully-bound request on semantic grounds - in which case it advertises the common supertype.
    /// </summary>
    /// <remarks>
    /// A semantic refusal travels through the shared result translator, which carries a failure code and
    /// one authored sentence and has no member name to key an error map to - so the response is a plain
    /// problem document.
    /// </remarks>
    [Fact]
    public void AnOperationCarryingContent_AdvertisesTheValidationDocumentUnlessItsRefusalCanBeSemantic()
    {
        List<string> offenders = [];
        List<string> staleExemptions = [.. SemanticRefusalOperations];

        foreach (KeyValuePair<string, OpenApiPathItem> path in _document.Paths)
        {
            foreach (KeyValuePair<OperationType, OpenApiOperation> operation in path.Value.Operations)
            {
                if (!CarriesContent(operation.Value))
                {
                    continue;
                }

                if (!operation.Value.Responses.TryGetValue("400", out OpenApiResponse? refusal))
                {
                    continue;
                }

                string identity = $"{operation.Key} {path.Key}";
                bool exempt = SemanticRefusalOperations.Contains(identity);

                foreach (KeyValuePair<string, OpenApiMediaType> body in refusal.Content)
                {
                    string schema = body.Value.Schema?.Reference?.Id ?? string.Empty;
                    bool namesTheMembers = schema == ValidationDocument;

                    if (exempt)
                    {
                        staleExemptions.Remove(identity);

                        // An exempt operation must advertise the SUPERTYPE, not nothing and not something
                        // invented. Accepting any schema here would turn the exemption into a hole through
                        // which an undeclared or bespoke 400 could pass unnoticed.
                        if (schema != ProblemDocument)
                        {
                            offenders.Add(
                                $"{identity} 400 -> {schema} (exempt, so it must advertise "
                                + $"{ProblemDocument})");
                        }

                        continue;
                    }

                    if (!namesTheMembers)
                    {
                        offenders.Add(
                            $"{identity} 400 -> {schema} (carries content and is not an exempt "
                            + "semantic-refusal operation)");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "an operation that binds a body or a query parameter is refused by the binder or by its "
            + "declarative validator, and both name the offending member - so the validation document is "
            + "the right declaration unless the operation can also refuse a fully-bound request on grounds "
            + "no member map can express");

        staleExemptions.Should().BeEmpty(
            "every exempt operation must exist and must reach this fact, or the exemption is excusing "
            + "nothing and should be deleted");
    }

    /// <summary>
    /// The one success that is a document rather than a payload is published under the media type it is
    /// actually served as.
    /// </summary>
    /// <remarks>
    /// Rather than merely widening the allowance, the rule is inverted into the converse of <see
    /// cref="EveryAdvertisedProblemDocument_IsAdvertisedUnderTheProblemMediaType"/>: that fact proves every
    /// problem document is served as a problem document, and this one proves nothing else is.
    /// </remarks>
    [Fact]
    public void TheExportedDocument_IsPublishedUnderItsOwnMediaType()
    {
        OpenApiResponse exported = _document
            .Paths[ExportPath]
            .Operations[OperationType.Post]
            .Responses["200"];

        exported.Content.Keys.Should().Equal([ExportMediaType]);

        List<(string Path, OperationType Verb, string Status, string MediaType, string Schema)> bodies =
        [
            .. _document.Paths
                .SelectMany(path => path.Value.Operations.Select(
                    operation => (Path: path.Key, Verb: operation.Key, Operation: operation.Value)))
                .SelectMany(entry => entry.Operation.Responses.SelectMany(
                    response => response.Value.Content.Select(
                        body => (
                            entry.Path,
                            entry.Verb,
                            Status: response.Key,
                            MediaType: body.Key,
                            Schema: body.Value.Schema?.Reference?.Id ?? string.Empty)))),
        ];

        List<(string Path, OperationType Verb, string Status, string MediaType, string Schema)> problems =
        [
            .. bodies.Where(entry =>
                entry.MediaType == ProblemMediaType
                && entry.Schema is ProblemDocument or ValidationDocument),
        ];

        problems.Should().NotBeEmpty(
            "the problem media type is exempted here because problem documents legitimately use it, so an "
            + "empty carve-out would mean this fact is quietly excusing a media type nothing sends");

        IEnumerable<string> departures = bodies
            .Except(problems)
            .Where(entry => entry.MediaType != JsonMediaType)
            .Where(entry => entry.Path != ExportPath)
            .Select(entry => $"{entry.Verb} {entry.Path} {entry.Status} -> {entry.MediaType}");

        departures.Should().BeEmpty(
            "a payload is JSON and a problem document is " + ProblemMediaType + ", so a third media type - "
            + "or either of those two on the wrong kind of response - is a parsing path a client has to "
            + "discover rather than a documented exception");
    }

    /// <summary>Every deletion whose service can report a persistence conflict advertises <c>409</c>.</summary>
    /// <remarks>
    /// THE PROFILE-DEFINITION DELETION DECLARED ONLY 204, 401, 403 AND 404. Its service reports a
    /// persistence conflict when a concurrent request changes or removes the definition between this
    /// request's read and its write, and the shared status table answers that code with <c>409</c> - so the
    /// status was reachable and undocumented.
    /// </remarks>
    [Fact]
    public void TheProfileDefinitionDeletion_AdvertisesTheConflictItCanReport()
    {
        List<string> deletions = [.. _document.Paths
            .Where(path => path.Key.EndsWith(ProfileDefinitionResourceSuffix, StringComparison.Ordinal))
            .Where(path => path.Value.Operations.ContainsKey(OperationType.Delete))
            .Select(path => path.Key)];

        deletions.Should().HaveCount(
            1,
            "the deletion has one canonical address and no portal-nested duplicate");

        foreach (string path in deletions)
        {
            OpenApiOperation deletion = _document.Paths[path].Operations[OperationType.Delete];

            deletion.Responses.Should().ContainKey(
                "409",
                "the service reports a persistence conflict on this path, so the status is reachable and "
                + "must be advertised rather than left for a client to meet unannounced");

            IEnumerable<string> schemas = deletion.Responses["409"].Content
                .Select(body => body.Value.Schema?.Reference?.Id ?? string.Empty);

            schemas.Should().OnlyContain(
                schema => schema == ProblemDocument,
                "a conflict names no request member, so it carries the plain problem document");
        }
    }

    /// <summary>
    /// Three further operations whose service reports a status they did not advertise now advertise it.
    /// </summary>
    /// <param name="path">The published route template of the operation.</param>
    /// <param name="verb">The verb, as the explorer names it.</param>
    /// <param name="status">The status the operation must advertise.</param>
    /// <param name="reachableBecause">The reason code that makes the status reachable, for the failure text.</param>
    /// <remarks>
    /// THE SAME CLASS OF DEFECT AS <see
    /// cref="TheProfileDefinitionDeletion_AdvertisesTheConflictItCanReport"/>, found in three more places,
    /// and stated as a table because it is one rule with three instances rather than three rules.
    /// </remarks>
    [Theory]
    [InlineData(
        "/api/v1/portals/{portalId}",
        OperationType.Delete,
        "503",
        "portal.member.session.revocation_store_unavailable")]
    [InlineData(
        "/api/v1/users/settings",
        OperationType.Put,
        "409",
        "user.membership-settings.storage-conflict")]
    [InlineData(
        "/api/v1/users/{userId}/profile",
        OperationType.Put,
        "409",
        "user.profile.duplicate-property")]
    public void AnOperationWhoseServiceReportsAStatus_AdvertisesIt(
        string path,
        OperationType verb,
        string status,
        string reachableBecause)
    {
        _document.Paths.Should().ContainKey(
            path,
            "the assertion names the published route template, so a renamed route must fail here rather than "
            + "silently stop being checked");

        _document.Paths[path].Operations.Should().ContainKey(
            verb,
            "the operation must still be published at this address");

        OpenApiOperation operation = _document.Paths[path].Operations[verb];

        operation.Responses.Should().ContainKey(
            status,
            $"the service reports {reachableBecause}, which the shared status table answers with {status}, so "
            + "the status is reachable and must be advertised rather than left for a client to meet "
            + "unannounced");

        IEnumerable<string> schemas = operation.Responses[status].Content
            .Select(body => body.Value.Schema?.Reference?.Id ?? string.Empty);

        schemas.Should().OnlyContain(
            schema => schema == ProblemDocument,
            "neither an unreachable dependency nor a state conflict names a request member, so both carry the "
            + "plain problem document");
    }

    /// <summary>Reports whether an operation accepts anything a refusal could name, beyond its path.</summary>
    /// <param name="operation">The operation to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when the operation binds a request body or a query parameter; otherwise <see
    /// langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Path parameters are deliberately not counted. Every one in this API is constrained to an integer, so
    /// a value of the wrong shape fails the route constraint and is answered by the router as a missing
    /// resource - the action is never reached and no member map can be produced.
    /// </remarks>
    private static bool CarriesContent(OpenApiOperation operation) =>
        operation.RequestBody is not null
        || operation.Parameters.Any(parameter => parameter.In == ParameterLocation.Query);

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
