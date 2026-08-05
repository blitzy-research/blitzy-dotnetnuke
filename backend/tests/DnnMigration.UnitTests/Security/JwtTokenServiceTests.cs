using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using DnnMigration.Application;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Security;

/// <summary>
/// Covers the DECLARED SHAPE of the bearer-token contract: the member set
/// <see cref="ITokenService"/> offers, the claim vocabulary written onto every token, and the
/// options that bound a token's life. It asserts nothing about what an implementation DOES.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS FILE IS DELIBERATELY CONTRACT-ONLY, AND THE BOUNDARY IS ENFORCED RATHER THAN
/// DESCRIBED.</strong> The concrete signing implementation is internal and sealed to the
/// infrastructure assembly, and this project references Application alone - AAP 0.5.2.2 gives it
/// exactly that one edge - so no test here can reach it, by name, by reflection or by any widened
/// visibility. What is asserted is therefore Application and Domain surface and nothing else:
/// <see cref="ITokenService"/>, <see cref="DnnClaimTypes"/>, <see cref="JwtOptions"/> and
/// <see cref="IClock"/>.
/// </para>
/// <para>
/// <strong>Semantic coverage lives in DnnMigration.IntegrationTests/Security/TokenServiceTests.cs,
/// which drives the REAL registered service.</strong> An earlier revision of this file declared a
/// private token service of its own and ran more than thirty issuance, rotation and revocation
/// tests against it. Every one of them passed by construction: they specified a double and then
/// asserted that the double behaved as specified, which is a tautology that would stay green while
/// the shipped implementation broke. That reimplementation and its tests were deleted rather than
/// annotated, because a suite that reports confidence it does not have is worse than no suite -
/// and the semantics it claimed to cover are now asserted against the service the container
/// actually resolves, over a real signature, a real compact serialisation and the real refresh
/// store.
/// </para>
/// <para>
/// What genuinely belongs here is the part no end-to-end sign-in can show, because a working
/// sign-in looks identical whether or not it holds: that no member of the contract can carry a
/// credential or signing material; that the contract offers no access-token retraction,
/// inspection or sign-out operation, so a caller cannot come to depend on one; that the claim
/// vocabulary is stable, since a renamed claim silently unauthorises every token already in
/// circulation; that the option defaults and their validation rules are what the documentation
/// says; and that the clock abstraction exposes one get-only instant, so no test affordance for
/// moving time can exist in production code.
/// </para>
/// <para>
/// MIGRATION: <see cref="ITokenService"/> is net-new. The legacy application shipped no token
/// service and no abstraction resembling one, so there is no predecessor to transliterate. What it
/// displaces is the .NET Framework 2.0 membership-and-cookie chain, replaced wholesale rather than
/// ported.
/// </para>
/// </remarks>
public class JwtTokenServiceTests
{
    /// <summary>
    /// An obviously synthetic signing secret, used wherever a value of the right shape is needed.
    /// </summary>
    /// <remarks>
    /// Forty-eight hexadecimal characters, so it clears the documented minimum length and distinct
    /// character count while being self-evidently fake. MIGRATION: the legacy installation
    /// registered its membership provider for reversible credential storage and committed the key
    /// that recovered every stored credential to source control. No value from that configuration
    /// is reproduced here, in code, in a comment or in a fixture.
    /// </remarks>
    private const string SyntheticSigningSecret = "0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>
    /// The tenant identifier used throughout, chosen to be the seed value of the legacy identity
    /// column rather than a comfortable positive number.
    /// </summary>
    /// <remarks>
    /// The tenant table seeds at minus one, so minus one names a real tenant. It is also the legacy
    /// absent-integer sentinel published by Library/Components/Shared/Null.vb:L41, which is exactly
    /// why a generic "greater than zero" validity check anywhere on this path would reject live
    /// data.
    /// </remarks>
    private const int TenantId = -1;

    /// <summary>A second tenant identifier, zero, which is equally real.</summary>
    private const int SecondTenantId = 0;

    /// <summary>The account identifier used throughout.</summary>
    private const int AccountId = 7;

    /// <summary>An account identifier of zero, which the schema also permits.</summary>
    private const int ZeroAccountId = 0;

    /// <summary>The sign-in name used throughout, deliberately mixed-case.</summary>
    private const string AccountName = "Measured.Member";

    /// <summary>The code reported when no record matches the presented refresh token.</summary>
    private const string RefreshNotFoundCode = "REFRESH_TOKEN_NOTFOUND";

    /// <summary>The code reported when the presented refresh token was explicitly revoked.</summary>
    private const string RefreshRevokedCode = "REFRESH_TOKEN_REVOKED";

    /// <summary>The code reported when the presented refresh token had already been exchanged.</summary>
    private const string RefreshAlreadyUsedCode = "REFRESH_TOKEN_ALREADYUSED";

    /// <summary>The code reported when the presented refresh token's chain has passed its deadline.</summary>
    private const string RefreshExpiredCode = "REFRESH_TOKEN_EXPIRED";

    /// <summary>The code reported when refresh-token state could not be persisted.</summary>
    private const string StoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";

    /// <summary>A refresh-token value that was never issued by the service under test.</summary>
    private const string NeverIssued = "never issued";

    /// <summary>A refresh-token value that has already been exchanged once.</summary>
    private const string AlreadyExchanged = "already exchanged";

    /// <summary>A refresh-token value whose family has already been revoked.</summary>
    private const string AlreadyRevoked = "already revoked";

    /// <summary>
    /// A value of the right shape that no service in this file ever issued, used to exercise the
    /// unknown-token path. Opaque and meaningless by construction.
    /// </summary>
    private const string UnissuedValue = "9f21c7d0e4b84a1cbe6f37a05d8e2b41";

    /// <summary>
    /// The instant every clock in this file is pinned to, so no assertion depends on when it ran.
    /// </summary>
    private static readonly DateTime Instant = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The namespaces a type on this contract's surface is permitted to come from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated as a closed allow-list rather than as a list of banned namespaces, which is both
    /// stronger and self-maintaining: anything outside these six fails, so the assertion covers
    /// every presentation-framework, token-library, principal, ambient-web and non-generic
    /// collection type at once, including ones nobody thought to ban. It equally forbids a domain
    /// entity - the entity namespace is absent - and the options type, whose namespace is absent
    /// too, so a caller of this contract can never choose a token's lifetime.
    /// </para>
    /// <para>
    /// The generic collection namespace is present because the entitlement collections are
    /// declared as read-only generic lists; its non-generic sibling is deliberately absent, which
    /// is what keeps the legacy untyped collection contracts off this surface.
    /// </para>
    /// </remarks>
    private static readonly string[] PermittedSurfaceNamespaces =
    [
        "System",
        "System.Collections.Generic",
        "System.Threading",
        "System.Threading.Tasks",
        "DnnMigration.Domain.Common",
        "DnnMigration.Application.Dtos.Auth",
    ];

    /// <summary>
    /// The contract offers exactly four operations, and they are these four.
    /// </summary>
    [Fact]
    public void TokenContract_OffersExactlyFourOperations()
    {
        typeof(ITokenService).GetMethods().Select(member => member.Name).Should().BeEquivalentTo(
            new[]
            {
                nameof(ITokenService.IssueTokensAsync),
                nameof(ITokenService.RefreshAsync),
                nameof(ITokenService.RevokeRefreshTokenAsync),
                nameof(ITokenService.RevokeAllRefreshTokensAsync),
            });
    }

    /// <summary>
    /// Every operation is awaitable, named for it, and cancellable.
    /// </summary>
    /// <remarks>
    /// Every member of this contract performs genuine input and output - refresh-token state is
    /// read and written - so the solution-wide rule that such members be awaitable applies in
    /// full. The three properties are asserted together because they are one convention: a member
    /// that returned a value directly, or that was awaitable while named as though it were not, or
    /// that could not be abandoned when its request was, would each break the same expectation at
    /// every call site.
    /// </remarks>
    [Fact]
    public void TokenContract_IsAwaitableAsyncSuffixedAndCancellableThroughout()
    {
        foreach (MethodInfo member in typeof(ITokenService).GetMethods())
        {
            member.ReturnType.Should().Match(
                type => type == typeof(Task) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)),
                "{0} performs store input and output, so it must be awaitable",
                member.Name);

            member.Name.Should().EndWith(
                "Async",
                "an awaitable member is named for it, so a caller cannot forget to await it");

            ParameterInfo[] parameters = member.GetParameters();

            parameters.Should().NotBeEmpty("{0} must at least accept a cancellation token", member.Name);

            ParameterInfo last = parameters[^1];

            last.ParameterType.Should().Be(
                typeof(CancellationToken),
                "{0} must take its cancellation token last, as the convention requires",
                member.Name);

            last.HasDefaultValue.Should().BeTrue(
                "{0} must let a caller omit the token rather than forcing None at every call site",
                member.Name);

            parameters.Take(parameters.Length - 1).Should().NotContain(
                parameter => parameter.ParameterType == typeof(CancellationToken),
                "{0} must take exactly one cancellation token",
                member.Name);
        }
    }

    /// <summary>
    /// No operation declares an output or a by-reference parameter.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is the executable form of the single largest contract change on the
    /// authentication path. The legacy entry point was
    /// Library/Components/Users/UserController.vb:L1132, which returned the account and reported
    /// the outcome through a by-reference status argument that its caller declared at
    /// Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L163 and then read back. No
    /// target public interface carries an output or by-reference parameter: an outcome travels in
    /// the return value, as a result, where a caller cannot ignore it by forgetting to look at a
    /// variable it passed in.
    /// </remarks>
    [Fact]
    public void TokenContract_DeclaresNoOutputOrByReferenceParameter()
    {
        foreach (MethodInfo member in typeof(ITokenService).GetMethods())
        {
            foreach (ParameterInfo parameter in member.GetParameters())
            {
                parameter.IsOut.Should().BeFalse(
                    "{0} must report through its return value, not through {1}",
                    member.Name,
                    parameter.Name);

                parameter.ParameterType.IsByRef.Should().BeFalse(
                    "{0} must not mutate {1} in place",
                    member.Name,
                    parameter.Name);
            }
        }
    }

    /// <summary>
    /// Every type on the surface comes from the closed set of permitted namespaces.
    /// </summary>
    /// <remarks>
    /// Generic arguments are expanded, so a forbidden type cannot hide inside a task or a result.
    /// Passing this assertion is what proves, by exhaustion rather than by enumeration, that no
    /// presentation-framework type, token-library type, principal type, ambient-web type,
    /// non-generic collection, queryable, options type or domain entity appears anywhere on the
    /// contract - and it proves it without this project referencing any of those packages, which is
    /// the whole point of asserting it here.
    /// </remarks>
    [Fact]
    public void TokenContract_ExposesOnlyPermittedNamespacesOnItsSurface()
    {
        IReadOnlyList<Type> surface = SurfaceTypes();

        surface.Should().NotBeEmpty("the contract has parameters and return values to inspect");

        foreach (Type type in surface)
        {
            (type.Namespace ?? string.Empty).Should().BeOneOf(
                PermittedSurfaceNamespaces,
                "{0} is not a namespace this contract is allowed to expose",
                type.Namespace);
        }
    }

    /// <summary>
    /// The surface is a closed set of concrete types, and every one of them is named here.
    /// </summary>
    /// <remarks>
    /// The narrower companion to the namespace assertion above. Identity facts arrive as integers,
    /// a boolean and strings; entitlements as read-only string lists; the outcome as a result. A
    /// signing key, a stored credential digest, a configuration object or an entity would each have
    /// to appear in this list to compile, which is what makes adding one a visible decision rather
    /// than an accident.
    /// </remarks>
    [Fact]
    public void TokenContract_ExposesOnlyAClosedSetOfTypes()
    {
        Type[] permitted =
        [
            typeof(int),
            typeof(bool),
            typeof(string),
            typeof(IReadOnlyList<string>),
            typeof(CancellationToken),
            typeof(Task<Result<LoginResponse>>),
            typeof(Task<Result>),
            typeof(Result<LoginResponse>),
            typeof(Result),
            typeof(LoginResponse),
        ];

        SurfaceTypes().Should().OnlyContain(type => permitted.Contains(type));
    }

    /// <summary>
    /// No operation accepts or returns a credential, a credential digest or any signing material.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction being drawn is between a credential or a cryptographic key, neither of which
    /// may cross this boundary, and a permission key, which is merely the name of an entitlement and
    /// is one of the things issuance exists to carry. Treating the bare fragment "key" as
    /// disqualifying would fail on the entitlement parameter and the contract - not the assertion -
    /// would be wrongly blamed, so the rule is stated precisely rather than loosened.
    /// </para>
    /// <para>
    /// The property being protected is that authentication and signing are separate
    /// responsibilities. This service is handed an identity that has already been accepted; if it
    /// could also be handed a credential, a later change could quietly move credential comparison
    /// into it and the single verification path the sign-in flow depends on would stop being single.
    /// </para>
    /// </remarks>
    [Fact]
    public void TokenContract_NeverAcceptsACredentialOrSigningMaterial()
    {
        string[] credentialFragments = ["password", "passphrase", "credential", "hash", "answer", "salt", "pin"];
        string[] signingFragments = ["secret", "signingkey", "securitykey", "privatekey", "apikey", "certificate"];
        string[] authorityFragments = ["role", "permission", "super", "username", "email", "profile"];

        foreach (MethodInfo member in typeof(ITokenService).GetMethods())
        {
            foreach (ParameterInfo parameter in member.GetParameters())
            {
                string name = parameter.Name ?? string.Empty;

                credentialFragments.Should().NotContain(
                    fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    "{0} must not take a credential - it signs, it does not authenticate",
                    member.Name);

                signingFragments.Should().NotContain(
                    fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    "{0} must not be handed signing material - the key is bound configuration held by "
                    + "the implementation, never an argument callers pass around",
                    member.Name);

                name.Should().NotBe(
                    "key",
                    "a parameter called exactly that is cryptographic material");

                authorityFragments.Should().NotContain(
                    fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    "{0} must not accept authority or personal data - each would be frozen into the token "
                    + "for its whole lifetime, and every guard re-reads them from storage instead",
                    member.Name);
            }
        }
    }

    /// <summary>
    /// The contract offers no way to retract, deny-list, validate or inspect an access token, and
    /// no server-side sign-out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the single most consequential behavioural difference on the authentication
    /// path, and it is asserted rather than merely noted. The legacy forms-authentication sign-out
    /// at Library/Components/Security/PortalSecurity.vb:L77 ended a session instantly, because the
    /// session lived in a cookie: it cleared five cookies by name, back-dating two of them by thirty
    /// years so the browser dropped them at once, and the role half of that same operation is
    /// repeated at L97. A bearer access token is self-contained and asserts its own validity, so
    /// once one has been handed to a caller no server action retracts it.
    /// </para>
    /// <para>
    /// Logout therefore has exactly three parts: revoking refresh state so no successor access token
    /// can be minted, waiting out the short access-token lifetime, and the client discarding its own
    /// copy. Introducing a rejected-access-token list would put a store read on every single request
    /// and turn stateless bearer authentication back into the server-held session this migration
    /// exists to leave behind. A validate-or-read member is absent for a related reason: the API
    /// layer's bearer handler already validates every inbound token, and a second entry point would
    /// create two code paths that could disagree about whether a token is acceptable.
    /// </para>
    /// </remarks>
    [Fact]
    public void TokenContract_OffersNoAccessTokenRetractionInspectionOrSignOut()
    {
        string[] forbiddenFragments =
        [
            "access",
            "denylist",
            "blocklist",
            "blacklist",
            "validate",
            "verify",
            "inspect",
            "introspect",
            "parse",
            "decode",
            "read",
            "signout",
            "signoff",
            "cookie",
            "scheme",
            "ticket",
        ];

        foreach (MethodInfo member in typeof(ITokenService).GetMethods())
        {
            forbiddenFragments.Should().NotContain(
                fragment => member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                "{0} names an operation this contract must not offer",
                member.Name);

            string[] transportFragments = ["cookie", "scheme", "ticket"];

            foreach (ParameterInfo parameter in member.GetParameters())
            {
                string name = parameter.Name ?? string.Empty;

                transportFragments.Should().NotContain(
                    fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    "how a token travels is the caller's concern, so no transport detail appears on {0}",
                    member.Name);
            }
        }

        typeof(ITokenService).GetProperties().Should().BeEmpty(
            "the contract is four operations and holds no observable state");
    }

    /// <summary>
    /// Issuance takes the two identifiers, in that order, and nothing else at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy entry point at
    /// Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L164 passed an
    /// authentication-type argument naming the provider that had accepted the credential, and
    /// repeated the same literal at L191 when raising its event. With a single bearer path there is
    /// no second provider to name, so no such argument exists here - which this assertion pins by
    /// naming the parameter list exhaustively.
    /// </para>
    /// <para>
    /// MIGRATION: a sign-in name, a host flag, a role list and a permission list were each considered
    /// as arguments and withdrawn, and the reconciled contract accepts none of them. Anything handed in
    /// here would be written into the token and frozen for its whole lifetime, so a role revoked or an
    /// account locked out a second after issuance would keep taking effect; every server-side guard
    /// therefore re-reads those facts from authoritative storage on the request that needs them. The
    /// exhaustive parameter list is the mechanism that keeps them out, which is why it is asserted with
    /// types and in order rather than merely by name.
    /// </para>
    /// <para>
    /// The list is equally the proof that role groups cannot become claims. Role groups organise roles
    /// for the read that resolves them and are never themselves an entitlement, and there is no
    /// parameter through which a group name - or a role name, or a permission key - could be smuggled
    /// in as one.
    /// </para>
    /// </remarks>
    [Fact]
    public void TokenContract_IssuanceTakesTheTwoIdentifiersInOrderAndNothingElse()
    {
        MethodInfo issue = typeof(ITokenService).GetMethod(nameof(ITokenService.IssueTokensAsync))!;

        issue.GetParameters().Select(parameter => (parameter.Name, parameter.ParameterType))
            .Should().Equal(
                ("userId", typeof(int)),
                ("portalId", typeof(int)),
                ("cancellationToken", typeof(CancellationToken)));
    }

    /// <summary>
    /// Rotation takes the presented token and a server-observed client fingerprint, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exchanging a token proves possession of something issued to one account in one tenant, and
    /// nothing about that proof licenses describing the successor as belonging to anybody else. The
    /// subject and the tenant are read from the implementation's own record; the absence of any
    /// parameter through which they could be supplied is the mechanism, not a convention. The same
    /// absence is why role eligibility cannot be steered from the request: no date, no instant and no
    /// entitlement travels with an exchange.
    /// </para>
    /// <para>
    /// MIGRATION: the second parameter is a bounded fingerprint the SERVER observes, not authority the
    /// caller asserts. It carries no identity and grants nothing; it exists only so that the store can
    /// separate a client retrying an exchange whose response it never saw from a second party spending
    /// a value it should not hold - the first is refused without ending the session, the second is read
    /// as theft and ends every session the account holds. Both halves are asserted end to end in
    /// DnnMigration.IntegrationTests against the registered service and the real store; what is
    /// asserted here is that the parameter exists, is a string, and sits between the presented value
    /// and the cancellation token, because a caller that transposed the two would silently bind every
    /// session to the same fingerprint.
    /// </para>
    /// </remarks>
    [Fact]
    public void TokenContract_RotationTakesThePresentedTokenAndAClientFingerprintOnly()
    {
        MethodInfo refresh = typeof(ITokenService).GetMethod(nameof(ITokenService.RefreshAsync))!;

        refresh.GetParameters().Select(parameter => (parameter.Name, parameter.ParameterType))
            .Should().Equal(
                ("refreshToken", typeof(string)),
                ("clientBinding", typeof(string)),
                ("cancellationToken", typeof(CancellationToken)));

        refresh.GetParameters().Should().NotContain(
            parameter => parameter.ParameterType == typeof(DateTime)
                || parameter.ParameterType == typeof(IReadOnlyList<string>)
                || parameter.ParameterType == typeof(bool),
            "authority and time are re-read by the implementation, never accepted from the request");
    }

    /// <summary>
    /// The application layer registers its own services and deliberately does not register the token
    /// contract.
    /// </summary>
    /// <remarks>
    /// Signing is infrastructure. Registering the contract here would put a signing key inside the layer
    /// that is not allowed to know about one, and would make the application assembly unusable without a
    /// configured key even in a test that never signs anything.
    /// </remarks>
    [Fact]
    public void TokenContract_IsNotRegisteredByTheApplicationLayer()
    {
        RecordingServiceCollection services = [];

        services.AddApplication();

        services.Select(descriptor => descriptor.ServiceType).Should().NotContain(typeof(ITokenService));

        services.Should().Contain(
            descriptor => descriptor.ServiceType == typeof(IAuthService)
                && descriptor.ImplementationType == typeof(AuthService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IPortalService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IModuleService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IUserService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IRoleService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IPermissionService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ITabService));

        services.Should().OnlyContain(
            descriptor => descriptor.Lifetime == ServiceLifetime.Scoped,
            "every application service and validator is per request");

        // MIGRATION: this pins the SHAPE of the registered set rather than its size. An earlier
        // revision asserted a descriptor count, which measured the validator scan's registration
        // arithmetic instead of the property under test: AddValidatorsFromAssemblyContaining registers
        // every validator twice, once under IValidator<T> and once under its own concrete type, so the
        // number moves whenever a validator is added or the scan's strategy changes while the property
        // this test exists to state - that this layer registers its own service contracts and its
        // request validators, and nothing else - does not.
        //
        // MIGRATION: an earlier revision of this comment claimed no derived paging request existed and
        // that narrowing happened inside one shared validator. The merged tree does the opposite and does it
        // deliberately: each listed collection binds its OWN paged request contract with its own sortable
        // allowlist, because a shared shape made one listing accept an ordering it silently discarded and
        // made another refuse an ordering its service could perform. The base contract keeps a registration
        // of its own for any endpoint that binds it.
        Type[] applicationServices =
        [
            typeof(IPortalService),
            typeof(IModuleService),
            typeof(IUserService),
            typeof(IRoleService),
            typeof(IPermissionService),
            typeof(ITabService),
            typeof(IAuthService),
        ];

        services.Should().OnlyContain(
            descriptor => applicationServices.Contains(descriptor.ServiceType)
                || typeof(IValidator).IsAssignableFrom(descriptor.ServiceType),
            "the application layer registers its own service contracts and its request validators, "
            + "and nothing else");

        // The validator INVENTORY - which request shapes carry a validator - is asserted in
        // Validation/ValidatorRegistrationTests.cs, where it belongs and where the per-entry reasoning for
        // every name lives. It was stated here as well for a while, and the duplicate went stale: the
        // registered set moved on - two response projections stopped being bound and five write and paging
        // shapes gained validators - while this copy kept asserting the older set. One inventory, in the
        // file named after it, is the arrangement that cannot drift against itself.
    }

    /// <summary>
    /// No type in the application assembly implements the contract.
    /// </summary>
    /// <remarks>
    /// The structural reason the abstraction lives here while its implementation does not: signing
    /// requires a token library and a signing key, and neither belongs in the layer that declares
    /// this contract. Registering an implementation here would also make the assembly unusable
    /// without a configured key even in a test that never signs anything. Asserted over the
    /// assembly's own metadata, so it holds however the layer's service registration is later
    /// arranged.
    /// </remarks>
    [Fact]
    public void TokenContract_HasNoImplementationInsideTheApplicationLayer()
    {
        Assembly applicationLayer = typeof(ITokenService).Assembly;

        applicationLayer.GetTypes()
            .Where(candidate => candidate is { IsClass: true, IsAbstract: false })
            .Where(candidate => typeof(ITokenService).IsAssignableFrom(candidate))
            .Should().BeEmpty(
                "signing is infrastructure, so the layer that declares the contract implements none of it");
    }

    /// <summary>
    /// The clock abstraction exposes exactly one get-only instant, and nothing else.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy code read the web server's local clock directly; the measured site is
    /// Library/Components/Security/Roles/RoleController.vb:L496, which derives a role expiry from
    /// the server's local calendar date. Every instant in the target is read through this
    /// abstraction and is in universal time, which is what makes an expiry assertion deterministic.
    /// The contract carries no adjustment member on purpose: a test supplies its own implementation,
    /// as this file does below, rather than widening the production surface with a setter or an
    /// advance-the-clock method. The framework's own time-provider type is deliberately not
    /// substituted for it.
    /// </remarks>
    [Fact]
    public void Clock_ExposesExactlyOneGetOnlyInstant()
    {
        PropertyInfo instant = typeof(IClock).GetProperties().Should().ContainSingle().Which;

        instant.Name.Should().Be(nameof(IClock.UtcNow));
        instant.PropertyType.Should().Be(typeof(DateTime));
        instant.CanRead.Should().BeTrue();
        instant.CanWrite.Should().BeFalse("an instant is observed, never assigned");

        typeof(IClock).GetMethods()
            .Where(member => !member.IsSpecialName)
            .Should().BeEmpty("reading the clock is the whole contract");

        string[] widenings =
        [
            "Today",
            "Now",
            "LocalNow",
            "TimeZone",
            "Offset",
            "SetUtcNow",
            "Advance",
            "Freeze",
            "Reset",
        ];

        typeof(IClock).GetMembers().Select(member => member.Name).Should().NotIntersectWith(
            widenings,
            "a calendar date is derived from the instant and local time is converted at the "
            + "presentation boundary, so no second time member is offered here");
    }

    /// <summary>
    /// The claim vocabulary is the measured set of wire names.
    /// </summary>
    /// <remarks>
    /// These strings are written into every issued token and read out of every presented one.
    /// Renaming one unauthorises every token already in circulation without a single test failing
    /// anywhere else, and the symptom - a caller who authenticates successfully and then appears to
    /// belong to no tenant and hold no entitlement - is indistinguishable from a legitimate
    /// authorisation denial. They are pinned here by value for that reason. The vocabulary is
    /// declared in the application layer beside the contract, rather than taken from a token
    /// library, so the wire format does not shift when a library renames its own constants.
    /// <para>
    /// MIGRATION: the set is asserted EXHAUSTIVELY, by reflection over the declared constants, rather
    /// than name by name. A sign-in name, a host flag and a permission key were all declared here at one
    /// point and were withdrawn: each froze mutable state for the token's whole lifetime, and every guard
    /// now re-reads it from authoritative storage instead. Pinning the count is what makes a well-meaning
    /// re-addition fail in this file rather than pass unnoticed and reach the wire.
    /// </para>
    /// </remarks>
    [Fact]
    public void ClaimNames_AreTheMeasuredWireNames()
    {
        FieldInfo[] declared = typeof(DnnClaimTypes).GetFields(BindingFlags.Public | BindingFlags.Static);

        declared.Select(field => field.Name).Should().BeEquivalentTo(
            new[]
            {
                nameof(DnnClaimTypes.Subject),
                nameof(DnnClaimTypes.PortalId),
                nameof(DnnClaimTypes.JwtId),
            },
            "the vocabulary is exactly three names; a fourth would be a snapshot of mutable state");

        DnnClaimTypes.Subject.Should().Be("sub");
        DnnClaimTypes.JwtId.Should().Be("jti");
        DnnClaimTypes.PortalId.Should().Be("portal_id");
    }

    /// <summary>
    /// The vocabulary declares no role name of its own, and none for a role group.
    /// </summary>
    /// <remarks>
    /// Roles are emitted under the framework's own role claim type so that the standard role
    /// requirements and the role-based policies work with no mapping step; a private role name here
    /// would look correct and silently authorise nobody. A group name is absent for a different
    /// reason: role groups organise roles for the read that resolves them and are never themselves
    /// an entitlement, so inventing a claim for one would assert authority the schema does not
    /// grant.
    /// </remarks>
    [Fact]
    public void ClaimNames_DeclareNeitherARoleNorARoleGroupNameOfTheirOwn()
    {
        IReadOnlyList<string> declared = DeclaredClaimMembers();

        declared.Should().NotContain(
            name => name.Contains("role", StringComparison.OrdinalIgnoreCase));

        DeclaredClaimValues().Should().NotContain(
            value => value.Contains("role", StringComparison.OrdinalIgnoreCase)
                || value.Contains("group", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// No two claim names collide, and none is blank.
    /// </summary>
    [Fact]
    public void ClaimNames_AreDistinctAndPopulated()
    {
        IReadOnlyList<string> values = DeclaredClaimValues();

        values.Should().OnlyHaveUniqueItems();
        values.Should().HaveCount(3);
        values.Should().NotContain(value => string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    /// The token options carry the documented defaults and name their configuration section.
    /// </summary>
    /// <remarks>
    /// MIGRATION: these five settings replace the fourteen provider declarations and the membership
    /// registration of the legacy web configuration. The access-token lifetime is the one that
    /// matters most, because an access token cannot be recalled once issued and its expiry is the
    /// only thing that ends it.
    /// </remarks>
    [Fact]
    public void TokenOptions_CarryTheDocumentedDefaults()
    {
        JwtOptions.SectionName.Should().Be("Jwt");

        JwtOptions options = new();

        options.Issuer.Should().Be("DnnMigration");
        options.Audience.Should().Be("DnnMigration");
        options.ExpirationMinutes.Should().Be(60);
        options.RefreshTokenExpirationDays.Should().Be(7);
        options.RefreshTokenAbsoluteExpirationDays.Should().Be(30);
    }

    /// <summary>
    /// There is no default signing key.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy installation committed the key that recovered every stored credential
    /// to source control. Shipping a default here would repeat that mistake in a form that works,
    /// which is worse, because a deployment that forgot to supply a key would run happily on a key
    /// an attacker also holds. An empty default is what lets start-up refuse instead, and the
    /// documented minimums below are what stop a placeholder standing in for one.
    /// </remarks>
    [Fact]
    public void TokenOptions_ShipNoSigningKeyAndDemandARealOne()
    {
        JwtOptions options = new();

        options.Secret.Should().BeEmpty();
        options.Validate().Should().NotBeEmpty("a missing key must be refused at start-up");

        JwtOptions.MinimumSecretByteLength.Should().Be(32);
        JwtOptions.MinimumSecretDistinctCharacters.Should().Be(8);
    }

    /// <summary>
    /// Every token option is settable, so a deployment configures them without a code change.
    /// </summary>
    [Fact]
    public void TokenOptions_AreConfigurableEntirelyFromSettings()
    {
        JwtOptions options = new()
        {
            Secret = SyntheticSigningSecret,
            Issuer = "contoso-portal",
            Audience = "contoso-spa",
            ExpirationMinutes = 15,
            RefreshTokenExpirationDays = 1,
            RefreshTokenAbsoluteExpirationDays = 2,
        };

        options.Secret.Should().Be(SyntheticSigningSecret);
        options.Issuer.Should().Be("contoso-portal");
        options.Audience.Should().Be("contoso-spa");
        options.ExpirationMinutes.Should().Be(15);
        options.RefreshTokenExpirationDays.Should().Be(1);
        options.RefreshTokenAbsoluteExpirationDays.Should().Be(2);
        options.Validate().Should().BeEmpty("a coherent configuration is accepted as given");
    }

    /// <summary>
    /// Both the sliding per-token lifetime and the absolute session ceiling are bounded.
    /// </summary>
    /// <remarks>
    /// The ceiling is the setting that governs how long a session may be renewed without the caller
    /// ever presenting a credential again. Since authenticating again is the only moment a
    /// credential, an approval state and a lock state are examined from scratch, an unbounded
    /// ceiling would extend by its own length the window in which an account disabled after sign-in
    /// keeps renewing. The two bounds are asserted together because checking only the sliding one is
    /// what would let an unbounded ceiling through.
    /// </remarks>
    [Fact]
    public void TokenOptions_BoundBothTheSlidingLifetimeAndTheSessionCeiling()
    {
        JwtOptions.MaximumRefreshTokenExpirationDays.Should().Be(30);
        JwtOptions.MaximumRefreshTokenAbsoluteExpirationDays.Should().Be(30);

        JwtOptions shipped = new() { Secret = SyntheticSigningSecret };

        shipped.RefreshTokenAbsoluteExpirationDays.Should().Be(
            JwtOptions.MaximumRefreshTokenAbsoluteExpirationDays,
            "the shipped default sits exactly at the bound, so the bound refuses nothing legitimate");
        shipped.Validate().Should().BeEmpty();

        JwtOptions oneDayOver = new()
        {
            Secret = SyntheticSigningSecret,
            RefreshTokenAbsoluteExpirationDays = JwtOptions.MaximumRefreshTokenAbsoluteExpirationDays + 1,
        };

        oneDayOver.Validate().Should().ContainSingle(
            "the bound is inclusive, so thirty-one days is the first refused value")
            .Which.Should().Contain(nameof(JwtOptions.RefreshTokenAbsoluteExpirationDays));
    }

    /// <summary>
    /// A ceiling below the sliding lifetime is still refused, for its own reason.
    /// </summary>
    /// <remarks>
    /// The two rules share a branch, so the maximum must not shadow the coherence rule that was
    /// already there. A ceiling shorter than a single token's life would truncate every token to the
    /// ceiling and make the per-token setting unreachable.
    /// </remarks>
    [Fact]
    public void TokenOptions_StillRefuseACeilingBelowTheSlidingLifetime()
    {
        JwtOptions options = new()
        {
            Secret = SyntheticSigningSecret,
            RefreshTokenExpirationDays = 14,
            RefreshTokenAbsoluteExpirationDays = 7,
        };

        options.Validate().Should().ContainSingle().Which.Should().Contain("less than");
    }

    /// <summary>
    /// A response raises no advisory and carries no credential before anything is put into it.
    /// </summary>
    /// <remarks>
    /// The three advisory booleans are the boundary form of the legacy post-verification status
    /// enumeration, and each must default to the no-advisory value. They are plain booleans rather
    /// than nullable ones on purpose: the legacy absent test reported "absent" for a false boolean
    /// itself, so a third state would advertise a distinction the source data cannot make.
    /// </remarks>
    [Fact]
    public void TokenResponse_RaisesNoAdvisoryAndCarriesNoCredentialByDefault()
    {
        LoginResponse response = new();

        response.AccessToken.Should().BeEmpty();
        response.RefreshToken.Should().BeEmpty();
        response.ExpiresAtUtc.Should().Be(
            default,
            "the expiry is assigned by the issuing service and is never computed by the contract");

        response.MustChangePassword.Should().BeFalse();
        response.PasswordExpiring.Should().BeFalse();
        response.MustUpdateProfile.Should().BeFalse();

        response.User.Should().NotBeNull(
            "the snapshot is always present, so a caller never null-checks the identity it just "
            + "authenticated");
        response.User.Roles.Should().BeEmpty();
        response.User.Permissions.Should().BeEmpty();
        response.User.IsSuperUser.Should().BeFalse(
            "a default projection must not read as installation-wide authority");
        response.User.Username.Should().BeEmpty();
        response.User.Email.Should().BeEmpty(
            "an address is personal data and is populated only by the read that resolved the account");
    }

    /// <summary>
    /// The contract is substitutable, and a caller observes a rotation failure through the outcome
    /// rather than through an exception.
    /// </summary>
    /// <remarks>
    /// The seam this asserts is the one the sign-in service depends on: it holds the abstraction, not
    /// an implementation, so a test can stand a double in its place and a deployment can substitute a
    /// durable refresh-token store without a single change on this surface. The cancellation token is
    /// verified as forwarded, because a store round trip that ignored it would keep running after its
    /// request had gone.
    /// </remarks>
    [Fact]
    public async Task Contract_IsSubstitutableAndReportsFailureThroughTheOutcome()
    {
        using CancellationTokenSource lifetime = new();
        Mock<ITokenService> substitute = new(MockBehavior.Strict);

        substitute
            .Setup(tokens => tokens.RefreshAsync("presented", "fingerprint", lifetime.Token))
            .ReturnsAsync(Result<LoginResponse>.Failure(RefreshExpiredCode, "The refresh token has expired."));

        Result<LoginResponse> refused = await substitute.Object.RefreshAsync(
            "presented",
            "fingerprint",
            lifetime.Token);

        refused.IsFailure.Should().BeTrue();
        refused.Error!.Code.Should().Be(RefreshExpiredCode);

        substitute.Verify(
            tokens => tokens.RefreshAsync("presented", "fingerprint", lifetime.Token),
            Times.Once);
    }

    /// <summary>
    /// Role eligibility is not reachable through this contract, and cannot be steered from a request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated explicitly because it is a coverage boundary rather than an omission. Which of an
    /// account's role assignments are in force at a given instant is decided by the read the rotation
    /// path performs against the assignment and role tables; no member here accepts an instant, a
    /// date, an assignment or an entitlement, so the question cannot be asked or answered through
    /// this surface. The exhaustive per-assignment table is asserted in the domain suite beside the
    /// assignment entity, and the end-to-end property - that a role withdrawn between two exchanges
    /// stops appearing in the next access token - is asserted in DnnMigration.IntegrationTests, which
    /// exchanges a real token against a real schema.
    /// </para>
    /// <para>
    /// What this file can and does assert is the token-side half: that eligibility is decided
    /// server-side from storage, because there is no parameter through which a caller could influence
    /// it.
    /// </para>
    /// </remarks>
    [Fact]
    public void RoleEligibility_IsNotReachableThroughThisContract()
    {
        IEnumerable<ParameterInfo> parameters = typeof(ITokenService)
            .GetMethods()
            .SelectMany(member => member.GetParameters());

        parameters.Should().NotContain(
            parameter => parameter.ParameterType == typeof(DateTime)
                || parameter.ParameterType == typeof(DateTime?),
            "the instant eligibility is judged at is read from the injected clock, never accepted");

        string[] eligibilityFragments = ["asof", "effective", "expiry", "expires", "eligib", "assignment"];

        foreach (ParameterInfo parameter in parameters)
        {
            string name = parameter.Name ?? string.Empty;

            eligibilityFragments.Should().NotContain(
                fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                "eligibility is decided from storage, so no aspect of it travels with a request");
        }
    }

    /// <summary>
    /// The eligibility rule the rotation path re-reads against treats both bounds as inclusive at the
    /// single captured instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted here because it is the rule that decides which roles a rotated access token asserts,
    /// and the boundary behaviour is the part that is easy to get wrong by one comparison operator: an
    /// assignment whose bound falls exactly on the instant is in force, matching the terminal
    /// predicate of the legacy role read. A future start excludes it; a past end excludes it,
    /// including the legacy expire-rather-than-delete value of yesterday written by
    /// Library/Components/Security/Roles/RoleController.vb:L496.
    /// </para>
    /// <para>
    /// MIGRATION, and a correction worth recording: a bound counts as set only when it is null or not,
    /// and the legacy absent-date sentinel of the minimum date value published by
    /// Library/Components/Shared/Null.vb:L66 is deliberately NOT recognised as "unset" by the target
    /// rule. Sentinel semantics survive at the boundary where a wire contract is externally
    /// observable, not inside the domain model - so a stored minimum date is treated as the very old
    /// date it literally is, which for a start bound means "in force" and never means "ignore me".
    /// </para>
    /// <para>
    /// Billing frequency is deliberately absent from all of this: the legacy billing codes are resolved
    /// into the two bounds when an assignment is written, so nothing is recomputed while a token is
    /// being minted.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false, 0, false, 0, true)]
    [InlineData(true, 0, false, 0, true)]
    [InlineData(false, 0, true, 0, true)]
    [InlineData(true, -30, true, 30, true)]
    [InlineData(true, 1, false, 0, false)]
    [InlineData(false, 0, true, -1, false)]
    public void RoleEligibility_TreatsBothBoundsAsInclusiveAtTheCapturedInstant(
        bool startIsSet,
        int startOffsetDays,
        bool endIsSet,
        int endOffsetDays,
        bool expectedInForce)
    {
        IClock clock = StoppedAt(Instant);
        DateTime asOfUtc = clock.UtcNow;

        UserRole assignment = new()
        {
            UserRoleId = 1,
            UserId = AccountId,
            RoleId = 0,
            EffectiveDate = startIsSet ? asOfUtc.AddDays(startOffsetDays) : null,
            ExpiryDate = endIsSet ? asOfUtc.AddDays(endOffsetDays) : null,
        };

        RoleStatus status = assignment.GetStatus(asOfUtc);

        (status == RoleStatus.Active).Should().Be(
            expectedInForce,
            "the bounds are inclusive and the instant is captured once");
    }

    /// <summary>
    /// The seeded and special role identifiers are real principals, not invalid input.
    /// </summary>
    /// <remarks>
    /// The role table seeds its identity column at zero and the installation's built-in principals
    /// carry negative identifiers, so an eligibility decision must turn on the two date bounds alone.
    /// A generic "greater than zero" guard anywhere on this path would strip exactly the roles every
    /// account holds.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    public void RoleEligibility_TreatsSeededAndSpecialRoleIdentifiersAsRealPrincipals(int roleId)
    {
        UserRole assignment = new()
        {
            UserRoleId = 1,
            UserId = ZeroAccountId,
            RoleId = roleId,
        };

        assignment.GetStatus(Instant).Should().Be(
            RoleStatus.Active,
            "an unbounded assignment is in force whatever the role identifier happens to be");
    }

    /// <summary>Builds a clock stopped at one instant.</summary>
    /// <param name="instant">The instant to report.</param>
    /// <returns>The clock.</returns>
    /// <remarks>
    /// A strict substitute, so any member other than the single documented one would fail the test
    /// rather than silently returning a default.
    /// </remarks>
    private static IClock StoppedAt(DateTime instant)
    {
        Mock<IClock> clock = new(MockBehavior.Strict);

        clock.SetupGet(reading => reading.UtcNow).Returns(instant);

        return clock.Object;
    }

    /// <summary>Lists the member names of the claim vocabulary.</summary>
    /// <returns>The member names.</returns>
    /// <remarks>
    /// Enumerated from the type's own metadata rather than written out, so a claim added later is
    /// covered by the vocabulary assertions without anybody having to remember to extend a list.
    /// </remarks>
    private static IReadOnlyList<string> DeclaredClaimMembers()
        => [.. DeclaredClaimFields().Select(field => field.Name)];

    /// <summary>Lists the wire values of the claim vocabulary.</summary>
    /// <returns>The wire values.</returns>
    private static IReadOnlyList<string> DeclaredClaimValues()
        => [.. DeclaredClaimFields().Select(field => (string)field.GetRawConstantValue()!)];

    /// <summary>Lists the constant fields that make up the claim vocabulary.</summary>
    /// <returns>The fields.</returns>
    private static IEnumerable<FieldInfo> DeclaredClaimFields()
        => typeof(DnnClaimTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string));

    /// <summary>Lists every type reachable on the contract's surface, generic arguments included.</summary>
    /// <returns>The distinct surface types.</returns>
    private static IReadOnlyList<Type> SurfaceTypes()
    {
        List<Type> surface = [];

        foreach (MethodInfo member in typeof(ITokenService).GetMethods())
        {
            Expand(member.ReturnType, surface);

            foreach (ParameterInfo parameter in member.GetParameters())
            {
                Expand(parameter.ParameterType, surface);
            }
        }

        return surface;
    }

    /// <summary>Adds a type and everything reachable inside it to <paramref name="into"/>.</summary>
    /// <param name="type">The type to expand.</param>
    /// <param name="into">The accumulating list.</param>
    /// <remarks>
    /// Generic arguments and element types are expanded so that a forbidden type cannot hide inside a
    /// task, a result or a collection.
    /// </remarks>
    private static void Expand(Type type, List<Type> into)
    {
        if (!into.Contains(type))
        {
            into.Add(type);
        }

        if (type.HasElementType && type.GetElementType() is Type element)
        {
            Expand(element, into);
        }

        if (!type.IsGenericType)
        {
            return;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            Expand(argument, into);
        }
    }

    /// <summary>The three mutable authority facts a rotation re-reads.</summary>
    /// <param name="IsSuperUser">Whether the account is installation-wide.</param>
    /// <param name="Roles">The role names currently in force.</param>
    /// <param name="PermissionKeys">The permission keys currently held.</param>
    private sealed record Authority(
        bool IsSuperUser,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> PermissionKeys)
    {
        /// <summary>Gets the authority of an account that currently holds none.</summary>
        public static Authority None { get; } = new(false, [], []);
    }

    /// <summary>
    /// A registration list that records what an extension method asked for.
    /// </summary>
    /// <remarks>
    /// The concrete collection type lives in a package this project does not reference, and does not need
    /// to: the registration contract is a list of descriptors, so recording them is enough to assert what
    /// the application layer registers and what it deliberately leaves to the composition root.
    /// </remarks>
    private sealed class RecordingServiceCollection : IServiceCollection
    {
        private readonly List<ServiceDescriptor> _descriptors = [];

        public int Count => _descriptors.Count;

        public bool IsReadOnly => false;

        public ServiceDescriptor this[int index]
        {
            get => _descriptors[index];
            set => _descriptors[index] = value;
        }

        public void Add(ServiceDescriptor item) => _descriptors.Add(item);

        public void Clear() => _descriptors.Clear();

        public bool Contains(ServiceDescriptor item) => _descriptors.Contains(item);

        public void CopyTo(ServiceDescriptor[] array, int arrayIndex)
            => _descriptors.CopyTo(array, arrayIndex);

        public IEnumerator<ServiceDescriptor> GetEnumerator() => _descriptors.GetEnumerator();

        public int IndexOf(ServiceDescriptor item) => _descriptors.IndexOf(item);

        public void Insert(int index, ServiceDescriptor item) => _descriptors.Insert(index, item);

        public bool Remove(ServiceDescriptor item) => _descriptors.Remove(item);

        public void RemoveAt(int index) => _descriptors.RemoveAt(index);

        IEnumerator IEnumerable.GetEnumerator() => _descriptors.GetEnumerator();
    }
}
