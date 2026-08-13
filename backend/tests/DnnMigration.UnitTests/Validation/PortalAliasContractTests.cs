// This suite exists for one property, and it is the property the security review found missing: the FIVE
// components that decide whether a path segment names a tenant must decide it the same way.
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Common;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves the one addressable-alias contract, at the Domain rule and at both write paths that apply it.
/// </summary>
public class PortalAliasContractTests
{
    /// <summary>
    /// The topology sentence, character for character, as an API caller and the alias screen both receive
    /// it.
    /// </summary>
    private const string UnsupportedPath =
        "An HTTP alias may carry at most 1 path segment beneath its host name; that segment may "
        + "contain only letters, digits, hyphens and underscores, and may not be one of the addresses "
        + "this application reserves for itself (api, health, login, modules, openapi, portals, "
        + "role-groups, roles, settings, swagger, users).";

    /// <summary>
    /// The general shape sentence, reported alongside the topology sentence by the alias contracts.
    /// </summary>
    private const string InvalidShape =
        "An HTTP alias must be a host name, an IP address or a server name, optionally followed by "
        + "a port and a path, and must not include a protocol prefix.";

    /// <summary>
    /// The message the portal creation contract reports for a disallowed alias character, restated so that
    /// the topology assertions below can prove they are NOT this failure.
    /// </summary>
    private const string LegacyAliasCharacters =
        "The Portal Name Must Not Contain Spaces Or Punctuation.";

    // -------------------------------------------------------------------------------------------
    // THE DOMAIN RULE
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The bound is one segment, which is what the legacy signup screen composed and what the reverse proxy
    /// can deliver.
    /// </summary>
    [Fact]
    public void TheAddressableDepth_IsExactlyOneSegment() =>
        PortalAliasTopology.MaximumPathSegments.Should().Be(
            1,
            "the legacy signup screen composed exactly one segment (Signup.ascx.vb L232-L236) and "
            + "docker/api-proxy.conf matches exactly one optional segment ahead of /api/; raising "
            + "this figure without widening the proxy and both browser mirrors would store aliases "
            + "nothing can route to");

    /// <summary>
    /// The reserved vocabulary is exactly the console's seven routes and the four roots the API answers.
    /// </summary>
    /// <remarks>
    /// ⚠ THE FOUR PLATFORM NAMES PAIR WITH <c>PortalAliasResolutionMiddleware.ExemptPathPrefixes</c>, which
    /// exempts <c>/health</c>, <c>/swagger</c> and <c>/openapi</c> from tenant resolution altogether, and
    /// with the fact that <c>/api</c> is deliberately NOT exempt because a versioned endpoint genuinely
    /// needs a tenant.
    /// </remarks>
    [Fact]
    public void TheReservedVocabulary_IsTheConsoleRoutesAndTheServerRoots() =>
        PortalAliasTopology.ReservedPathSegments.Should().BeEquivalentTo(
            new[]
            {
                "login",
                "modules",
                "portals",
                "role-groups",
                "roles",
                "settings",
                "users",
                "api",
                "health",
                "openapi",
                "swagger",
            },
            "the first seven are the closed route table the browser mirrors in "
            + "RESERVED_TOP_LEVEL_SEGMENTS and the last four are the roots the API answers, which the "
            + "browser mirrors in RESERVED_PLATFORM_SEGMENTS");

    /// <summary>
    /// The reserved vocabulary folds case, because an address may be typed in any case while a stored alias
    /// is persisted as it was authored.
    /// </summary>
    [Theory]
    [InlineData("API")]
    [InlineData("Api")]
    [InlineData("USERS")]
    [InlineData("Role-Groups")]
    public void AReservedSegment_IsRecognisedInAnyCase(string segment) =>
        PortalAliasTopology.IsReservedPathSegment(segment).Should().BeTrue();

    /// <summary>
    /// A segment the server could store is one of letters, digits, hyphens and underscores that names
    /// nothing the deployment owns.
    /// </summary>
    /// <param name="segment">The candidate segment.</param>
    [Theory]
    [InlineData("child")]
    [InlineData("acme-legal")]
    [InlineData("acme_legal")]
    [InlineData("tenant7")]
    [InlineData("Acme-Legal")]
    public void AnAddressableSegment_IsAdmitted(string segment) =>
        PortalAliasTopology.IsAddressableSegment(segment).Should().BeTrue();

    /// <summary>Everything the deployment cannot address is refused, and the dot is refused with the rest.</summary>
    /// <param name="segment">The candidate segment.</param>
    /// <remarks>
    /// The dot cases are the load-bearing ones. Refusing the dot is what makes "names a served file" and
    /// "names a tenant" disjoint by construction, and it is therefore what allowed the browser's closed
    /// list of nineteen document extensions - which could never anticipate <c>.webmanifest</c> or
    /// <c>.aspx</c> - to be deleted rather than extended.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("acme.co")]
    [InlineData("robots.txt")]
    [InlineData(".well-known")]
    [InlineData("child portal")]
    [InlineData("child%20portal")]
    [InlineData("~child")]
    [InlineData("child!")]
    [InlineData("api")]
    [InlineData("users")]
    [InlineData("swagger")]
    public void ANonAddressableSegment_IsRefused(string segment) =>
        PortalAliasTopology.IsAddressableSegment(segment).Should().BeFalse();

    /// <summary>
    /// An alias with no path at all is within the topology, and so is one carrying a single addressable
    /// segment.
    /// </summary>
    /// <param name="alias">The candidate alias.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("localhost:4200")]
    [InlineData("www.example.com")]
    [InlineData("example.com/child")]
    [InlineData("example.com:8443/child")]
    [InlineData("MYSERVER/acme_legal-7")]
    public void ASupportedAddress_IsAdmitted(string? alias) =>
        PortalAliasTopology.IsSupportedAddress(alias).Should().BeTrue();

    /// <summary>
    /// An alias deeper than the topology, or naming a reserved or dotted segment, is refused - and so is
    /// one that merely ends in a separator.
    /// </summary>
    /// <param name="alias">The candidate alias.</param>
    [Theory]
    [InlineData("example.com/first/second")]
    [InlineData("example.com/a/b/c/d/e")]
    [InlineData("example.com/api")]
    [InlineData("example.com/users")]
    [InlineData("example.com/acme.co")]
    [InlineData("example.com/")]
    [InlineData("example.com//child")]
    public void AnUnsupportedAddress_IsRefused(string alias) =>
        PortalAliasTopology.IsSupportedAddress(alias).Should().BeFalse();

    // -------------------------------------------------------------------------------------------
    // THE ALIAS WRITE PATHS
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Binding an alias to a portal refuses a path this deployment cannot deliver, and says which part of
    /// the value was refused.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData("example.com/first/second")]
    [InlineData("example.com/api")]
    [InlineData("example.com/users")]
    [InlineData("example.com/acme.co")]
    public void CreatingAnAlias_RefusesAnUnaddressablePath(string alias)
    {
        CreatePortalAliasRequestValidator validator = new();

        ValidationResult result = validator.Validate(new CreatePortalAliasRequest
        {
            HttpAlias = alias,
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Select(failure => failure.ErrorMessage).Should().Contain(UnsupportedPath);
        result.Errors.Select(failure => failure.ErrorMessage).Should().Contain(InvalidShape);
        result.Errors.Should().OnlyContain(
            failure => failure.PropertyName == nameof(CreatePortalAliasRequest.HttpAlias));
    }

    /// <summary>
    /// The same rule applies on update, because an alias submitted on either verb binds the same uniquely
    /// indexed column.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData("example.com/first/second")]
    [InlineData("example.com/api")]
    [InlineData("example.com/acme.co")]
    public void UpdatingAnAlias_RefusesTheSamePaths(string alias)
    {
        UpdatePortalAliasRequestValidator validator = new();

        ValidationResult result = validator.Validate(new UpdatePortalAliasRequest
        {
            HttpAlias = alias,
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Select(failure => failure.ErrorMessage).Should().Contain(UnsupportedPath);
    }

    /// <summary>
    /// An addressable alias is still accepted by both alias contracts, so the tightening refused nothing
    /// that was deliverable.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:4200")]
    [InlineData("example.com/child")]
    [InlineData("example.com:8443/child")]
    [InlineData("example.com/acme_legal-7")]
    public void AnAddressableAlias_IsAcceptedByBothAliasContracts(string alias)
    {
        new CreatePortalAliasRequestValidator()
            .Validate(new CreatePortalAliasRequest { HttpAlias = alias })
            .IsValid.Should().BeTrue();

        new UpdatePortalAliasRequestValidator()
            .Validate(new UpdatePortalAliasRequest { HttpAlias = alias })
            .IsValid.Should().BeTrue();
    }

    // -------------------------------------------------------------------------------------------
    // THE PORTAL CREATION CONTRACT
    // -------------------------------------------------------------------------------------------

    /// <summary>Creating a CHILD portal refuses a submitted value carrying more than one path segment.</summary>
    [Fact]
    public void CreatingAChildPortal_RefusesAValueCarryingSeveralSegments()
    {
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = true;
        request.PortalAlias = "foo/bar/baz";

        ValidationResult result = Validator().Validate(request);

        result.IsValid.Should().BeFalse();
        AliasMessages(result).Should().Contain(UnsupportedPath);
        AliasMessages(result).Should().NotContain(
            LegacyAliasCharacters,
            "the legacy character rule judges only the final segment and cannot see this defect, "
            + "which is why the depth rule had to be added rather than the character rule widened");
    }

    /// <summary>
    /// Creating a PARENT portal refuses a submitted value carrying more than one path segment, which the
    /// widened parent character set admitted.
    /// </summary>
    [Fact]
    public void CreatingAParentPortal_RefusesAValueCarryingSeveralSegments()
    {
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = false;
        request.PortalAlias = "example.com/first/second";

        ValidationResult result = Validator().Validate(request);

        result.IsValid.Should().BeFalse();
        AliasMessages(result).Should().Contain(UnsupportedPath);
    }

    /// <summary>
    /// Creating a portal refuses a value whose path segment names one of the deployment's own addresses.
    /// </summary>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData("example.com/api")]
    [InlineData("example.com/users")]
    [InlineData("example.com/swagger")]
    public void CreatingAPortal_RefusesAReservedSegment(string alias)
    {
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = false;
        request.PortalAlias = alias;

        ValidationResult result = Validator().Validate(request);

        result.IsValid.Should().BeFalse();
        AliasMessages(result).Should().Contain(UnsupportedPath);
    }

    /// <summary>
    /// A bare child segment and a singly-qualified value both remain acceptable, so the tightening refused
    /// nothing the legacy screen could produce.
    /// </summary>
    /// <param name="isChildPortal">Whether the request asks for a child portal.</param>
    /// <param name="alias">The submitted alias.</param>
    [Theory]
    [InlineData(true, "sales")]
    [InlineData(true, "acme-legal")]
    [InlineData(true, "other.example/child")]
    [InlineData(false, "localhost")]
    [InlineData(false, "example.com:8443")]
    [InlineData(false, "example.com/child")]
    public void CreatingAPortal_StillAcceptsAnAddressableAlias(bool isChildPortal, string alias)
    {
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = isChildPortal;
        request.PortalAlias = alias;

        ValidationResult result = Validator().Validate(request);

        AliasMessages(result).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------
    // FIXTURES
    // -------------------------------------------------------------------------------------------

    /// <summary>Builds the portal creation validator bound to the legacy password policy.</summary>
    /// <returns>The validator.</returns>
    private static CreatePortalRequestValidator Validator() =>
        new(new PasswordPolicyOptions
        {
            MinRequiredPasswordLength = 7,
            MinRequiredNonAlphanumericCharacters = 0,
        });

    /// <summary>
    /// Builds a creation request that satisfies every other rule, so a failure names the alias rule.
    /// </summary>
    /// <returns>The request.</returns>
    private static CreatePortalRequest ValidCreateRequest() => new()
    {
        PortalName = "Migration Portal",
        PortalAlias = "localhost",
        Description = "A portal created by the alias contract suite.",
        KeyWords = "migration, alias",
        HomeDirectory = "Portals/migration",
        TemplateFile = "admin.template",
        IsChildPortal = false,
        AdministratorFirstName = "Migration",
        AdministratorLastName = "Administrator",
        AdministratorUsername = "migration_admin",
        AdministratorPassword = "Migr8tion!Pass",
        AdministratorEmail = "admin@example.com",
    };

    /// <summary>The messages reported against the alias field.</summary>
    /// <param name="result">The validation result.</param>
    /// <returns>The alias messages.</returns>
    private static IReadOnlyList<string> AliasMessages(ValidationResult result) =>
        result.Errors
            .Where(failure => failure.PropertyName == nameof(CreatePortalRequest.PortalAlias))
            .Select(failure => failure.ErrorMessage)
            .ToList();
}
