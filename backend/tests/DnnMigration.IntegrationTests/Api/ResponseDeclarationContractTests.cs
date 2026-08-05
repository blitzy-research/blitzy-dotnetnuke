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
    /// <para>
    /// A LITERAL LIST, deliberately, and it is the whole reason the converse rule can be asserted at all. A
    /// predicate would exempt whatever it happened to match and prove nothing; a fixed roster means a new
    /// operation cannot join it silently. Each entry is written exactly as the published document renders the
    /// operation - the verb as the explorer names it, then the templated path.
    /// </para>
    /// <para>
    /// Each is here because a specific reason code is reachable with every request member individually
    /// valid: <c>portal.parent_alias_unresolved</c> on the portal creation, <c>role_group.scope_invalid</c>
    /// on the role listing when a group identifier is combined with the ungrouped scope, and
    /// <c>permission.filter_invalid</c> on the permission catalogue for a module-definition identifier below
    /// the lowest one that can name a row. None of them has a member to key an error map to, so none can
    /// produce the validation document, so none may advertise it.
    /// </para>
    /// <para>
    /// EACH EXEMPTION WAS MEASURED AGAINST A RUNNING INSTANCE rather than read off the service code, and one
    /// candidate justification did not survive that check. The permission catalogue also refuses a supplied
    /// but blank <c>permissionCode</c> with the same failure code, and an earlier revision of this block cited
    /// it - but the simple-type binder converts a whitespace-only query value to null, so that branch answers
    /// 200 with the whole catalogue and is unreachable over HTTP. The identifier branch is what earns this
    /// operation its exemption: <c>?moduleDefinitionId=0</c> answers 400 with a plain problem document, while
    /// <c>?permissionKey=99</c> answers 400 with a validation document naming the parameter. Both shapes, one
    /// operation, so only the supertype describes it honestly. The role listing was confirmed the same way at
    /// its canonical address, and the portal creation refuses an unresolvable parent alias.
    /// </para>
    /// <para>
    /// THREE ENTRIES NAME THREE ACTIONS. Each action has one canonical route, so the literal roster and the
    /// action roster are now one-to-one. The exemption remains keyed by published operation identity because
    /// that is the contract a generated client consumes.
    /// </para>
    /// <para>
    /// Note what is NOT here. Every listing enforces its collection's sortable vocabulary in the application
    /// layer as well as at the boundary, and those service-side paging refusals would be semantic 400s too -
    /// but they are unreachable over HTTP, because the per-collection request validator answers first and
    /// names <c>sortBy</c>. An unreachable refusal must not earn an exemption, so those listings are held to
    /// the validation document like everything else.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyList<string> SemanticRefusalOperations =
    [
        "Post /api/v1/portals",
        "Get /api/v1/roles",
        "Get /api/v1/permissions",
    ];

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

    /// <summary>The published document contains only the AAP-canonical route family for each operation.</summary>
    /// <remarks>
    /// A duplicate route is not harmless compatibility: generated clients expose both paths as separate
    /// operations and force callers to guess which identity is authoritative. This assertion pins the flat
    /// module, account, role, role-group and profile-definition families, the portal-owned alias family and
    /// the two permission-catalogue reads while explicitly rejecting every duplicate removed by AAP-1 and
    /// AAP-3.
    /// </remarks>
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
    /// An operation that accepts nothing but its path never advertises a validation document.
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
    /// A route-only operation MAY still refuse a well-formed request on state grounds - a lock that is not
    /// held, a tenant that must retain one portal - and such a refusal is a plain problem document, which is
    /// why the rule constrains the SHAPE rather than forbidding the status.
    /// </para>
    /// <para>
    /// MIGRATION: THIS RULE USED TO BE STATED BI-DIRECTIONALLY, and the converse half was false. It asserted
    /// that an operation carrying content ALWAYS advertises the validation document, on the reasoning that
    /// the two halves are one rule read from either side. They are not. Carrying content makes a member-named
    /// refusal POSSIBLE; it does not make it the only refusal reachable. Three actions refuse a well-formed,
    /// fully-bound request on semantic grounds, and such a refusal travels through the shared result
    /// translator, which produces a PLAIN problem document - it has a failure code and a sentence, and no
    /// member to key an error map to. Those three therefore emit both shapes depending on why they refused,
    /// so the only schema they can honestly advertise is the common supertype, and the withdrawn half of this
    /// rule forbade exactly that honest declaration.
    /// </para>
    /// <para>
    /// The converse is still asserted, in <see cref="AnOperationCarryingContent_AdvertisesTheValidationDocumentUnlessItsRefusalCanBeSemantic"/>,
    /// against an explicit exemption set naming those three actions at their canonical addresses. Keeping it
    /// as a separate fact with a named list is what stops the exemption from becoming a silent hole: admitting
    /// another mixed-refusal operation is a deliberate edit to that list rather than a test that quietly keeps
    /// passing.
    /// </para>
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
    /// <para>
    /// This is the converse of the fact above, and it is stated separately precisely because it needs an
    /// exemption while the other half does not. Every operation that binds a body or a query parameter can
    /// produce a member-named refusal, so the validation document is the right declaration for almost all of
    /// them - the model binder or the declarative validator answers first and names the offending member.
    /// </para>
    /// <para>
    /// MIGRATION: three operations are different, and declaring the validation document for them described a
    /// response they do not always produce. Each can refuse a request that bound and validated perfectly, on
    /// grounds only the application layer can judge:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <c>POST /api/v1/portals</c> - the submitted parent portal alias resolves to no portal
    ///   (<c>portal.parent_alias_unresolved</c>). The value is well-formed; whether it names a row is a
    ///   question about stored state.
    ///   </description></item>
    ///   <item><description>
    ///   <c>GET /api/v1/roles</c> - a group identifier combined with the ungrouped scope
    ///   (<c>role_group.scope_invalid</c>). Both parameters are individually valid and contradict each other.
    ///   </description></item>
    ///   <item><description>
    ///   <c>GET /api/v1/permissions</c> - a blank code filter, or a module-definition identifier that cannot
    ///   name a row (<c>permission.filter_invalid</c>).
    ///   </description></item>
    /// </list>
    /// <para>
    /// A semantic refusal travels through the shared result translator, which carries a failure code and one
    /// authored sentence and has no member name to key an error map to - so the response is a plain problem
    /// document. Both shapes are reachable on these three, and the common supertype is the only schema that
    /// describes both: every validation document IS a problem document, so a client parsing the declared
    /// shape reads either successfully, and one that additionally branches on the member map still finds it
    /// when it is there. Advertising the narrower shape promised a member map that half of these refusals
    /// cannot contain.
    /// </para>
    /// <para>
    /// The exemption set is a LITERAL LIST rather than a predicate, and that is the point of the design. A
    /// predicate - "exempt any operation whose controller can return a failed result" - would exempt almost
    /// everything and assert nothing. Naming three operations means a fourth cannot join them by accident:
    /// it fails here until somebody adds it deliberately, and the reason has to be written down beside it.
    /// </para>
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

        // The exemption list is held to be exactly as long as it needs to be. An entry that matches no
        // operation is a stale exemption, and a stale exemption is a hole: it would keep excusing an
        // operation that had been renamed or removed, and nothing else in this suite would notice.
        staleExemptions.Should().BeEmpty(
            "every exempt operation must exist and must reach this fact, or the exemption is excusing "
            + "nothing and should be deleted");
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

    /// <summary>
    /// Every deletion whose service can report a persistence conflict advertises <c>409</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one shape of declaration defect the schema rules above cannot see. They inspect the
    /// statuses an operation DOES advertise and check each carries a problem document; a status the operation
    /// never mentions passes every one of them, because there is nothing to inspect. An undeclared but
    /// reachable status is worse than a wrongly-typed one for a generated client: the wrongly-typed status
    /// deserialises into the wrong shape, while the undeclared status has no branch at all and surfaces as an
    /// unhandled response.
    /// </para>
    /// <para>
    /// MIGRATION: THE PROFILE-DEFINITION DELETION DECLARED ONLY 204, 401, 403 AND 404. Its service reports a
    /// persistence conflict when a concurrent request changes or removes the definition between this
    /// request's read and its write, and the shared status table answers that code with <c>409</c> - so the
    /// status was reachable and undocumented. A comment in the service asserted the endpoint already declared
    /// it, which made the gap read as intentional; it did not, and the comment was corrected alongside the
    /// declaration.
    /// </para>
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
    /// Reports whether an operation accepts anything a refusal could name, beyond its path.
    /// </summary>
    /// <param name="operation">The operation to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when the operation binds a request body or a query parameter; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Path parameters are deliberately not counted. Every one in this API is constrained to an integer, so a
    /// value of the wrong shape fails the route constraint and is answered by the router as a missing
    /// resource - the action is never reached and no member map can be produced. Stated once here so the two
    /// halves of the rule cannot disagree about what "carries content" means.
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
