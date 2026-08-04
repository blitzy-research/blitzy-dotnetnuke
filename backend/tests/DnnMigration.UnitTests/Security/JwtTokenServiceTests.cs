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
/// Covers the bearer-token contract of the migrated authentication surface: the shape of the
/// contract itself, the claim vocabulary written onto every token, the options that bound a
/// token's life, and the issuance, rotation and revocation semantics the contract fixes as
/// binding terms.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: coverage placement, and it is a deliberate architectural choice rather than an
/// omission. The concrete signing implementation - the JWT token service in the infrastructure
/// security folder - is declared internal and sealed, and the layering baseline requires the
/// dependency direction to be enforced by the compiler rather than by review. Everything in this
/// file is therefore asserted through the two abstractions a unit test can legitimately see:
/// <see cref="ITokenService"/> from the application layer and <see cref="IClock"/> from the domain
/// layer. Algorithm-level verification - a real signature over a real key, a real compact
/// serialisation, a real bearer handler accepting the result - belongs to
/// DnnMigration.IntegrationTests, which hosts the API through a web-application factory and
/// resolves the public contract from that host's service provider. Nothing here loads an assembly,
/// names a concrete type, or reaches an implementation by reflection, and no visibility is widened
/// to make it possible.
/// </para>
/// <para>
/// What that boundary leaves assertable is exactly the part an end-to-end sign-in cannot show,
/// because a working sign-in looks identical whether or not these properties hold: that no member
/// can carry a credential or signing material; that the claim vocabulary is stable, since a
/// renamed claim silently unauthorises every token already in circulation; that a rotation failure
/// is reported through four distinguishable codes rather than one; that a replay ends every
/// session the account holds; that logout can only stop a session from continuing and can never
/// retract the access token already handed out; and that every instant a token asserts comes from
/// an injected clock rather than from the machine.
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
                    "a parameter called exactly that is cryptographic material, whereas an entitlement "
                    + "parameter is a list of permission names");
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
    /// Issuance takes the identity facts and the caller's entitlements, in that order and no other.
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
    /// The list is equally the proof that role groups cannot become claims. Roles arrive already
    /// resolved from the role, assignment and role-group tables; the grouping is the organising join
    /// used to perform that read, not an additional claim value, and there is no parameter through
    /// which a group name could be smuggled in as one.
    /// </para>
    /// </remarks>
    [Fact]
    public void TokenContract_IssuanceTakesTheIdentityFactsInOrderAndNothingElse()
    {
        MethodInfo issue = typeof(ITokenService).GetMethod(nameof(ITokenService.IssueTokensAsync))!;

        issue.GetParameters().Select(parameter => parameter.Name).Should().BeEquivalentTo(
            new[]
            {
                "userId",
                "portalId",
                "userName",
                "isSuperUser",
                "roles",
                "permissionKeys",
                "cancellationToken",
            },
            options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Rotation takes the presented token and nothing else, so the identity of the successor cannot
    /// be influenced by its caller.
    /// </summary>
    /// <remarks>
    /// Exchanging a token proves possession of something issued to one account in one tenant, and
    /// nothing about that proof licenses describing the successor as belonging to anybody else. The
    /// subject, the tenant and the sign-in name are read from the implementation's own record; the
    /// absence of any parameter through which they could be supplied is the mechanism, not a
    /// convention. The same absence is why role eligibility cannot be steered from the request: no
    /// date, no instant and no entitlement travels with an exchange.
    /// </remarks>
    [Fact]
    public void TokenContract_RotationTakesOnlyThePresentedToken()
    {
        MethodInfo refresh = typeof(ITokenService).GetMethod(nameof(ITokenService.RefreshAsync))!;

        refresh.GetParameters().Select(parameter => parameter.Name).Should().BeEquivalentTo(
            new[] { "refreshToken", "cancellationToken" },
            options => options.WithStrictOrdering());

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
        // M-11: this comment also used to claim "PagedRequestValidator has four sealed derivations the
        // scan finds as well". No such derivations existed, and they could not have worked - every paged
        // action binds one shared PagedRequest and is validated through IValidator<PagedRequest>, so
        // nothing would ever have resolved one. Narrowing per endpoint happens inside the single
        // validator instead.
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

        // The validator inventory is stated by name, because which requests carry a validator is the
        // substance of the registration and a count would not say it. Every request type an endpoint
        // binds appears below, and the list is therefore the inventory a reviewer checks a new endpoint
        // against.
        //
        // Twenty-five entries. FIVE are the paging requests: the generic PagedRequestValidator has one
        // sealed derivation per listed collection, each closed over a derived request type so that the
        // sort-field allowlist can be the one that collection actually honours rather than the union of
        // all of them. The base PagedRequest keeps its own registration, because the non-generic
        // PagedRequestValidator still exists for any endpoint that binds the base type. The remainder are
        // body shapes, several of which were added because their write actions advertised a field-error
        // response while no validator resolved for them at all.
        //
        // The list is a SET, and it is stated once per request type. Two separate audits of the write
        // surface reached three of these names independently - the role update, the role assignment and
        // the page update - and for a while the list named each of them twice. That is not a harmless
        // duplication: the comparison below is order-insensitive but not multiplicity-insensitive, so a
        // repeated expectation demands a repeated registration, and the assembly scan correctly produces
        // exactly one. Each name therefore appears exactly once, and the reason it was added is recorded
        // beside it rather than by repeating the entry.
        //
        // MIGRATION: the count moved from twenty-two to twenty-four when the role-group and
        // profile-definition write surfaces stopped binding their RESPONSE projections. Each of those two
        // resources previously registered one validator over one shape that both verbs bound, which is
        // precisely why the boundary advertised members neither procedure writes; each now registers a
        // create validator and an update validator over contracts carrying only what the procedure behind
        // that verb honours. The two withdrawn names are RoleGroupDto and ProfilePropertyDefinitionDto, and
        // their absence from this inventory is itself load-bearing: a response projection has nothing to
        // validate, so a validator resolving for one would mean a verb had started binding it again.
        services
            .Select(descriptor => descriptor.ServiceType)
            .Where(serviceType => serviceType.IsGenericType
                && serviceType.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Select(serviceType => serviceType.GetGenericArguments()[0])
            .Distinct()
            .Should().BeEquivalentTo(
                new[]
                {
                    typeof(LoginRequest),
                    typeof(CreateUserRequest),
                    typeof(UpdateUserRequest),
                    typeof(ChangePasswordRequest),
                    typeof(CreateRoleRequest),
                    typeof(CreateModuleRequest),
                    typeof(UpdateModuleRequest),
                    typeof(CreatePortalRequest),
                    typeof(UpdatePortalRequest),
                    typeof(CreatePortalAliasRequest),
                    typeof(UpdatePortalAliasRequest),
                    typeof(UpdateRoleRequest),

                    // The two role-group write contracts. dbo.AddRoleGroup and dbo.UpdateRoleGroup write
                    // the name and the description and nothing else, so neither contract carries the group
                    // key or the owning portal - the first is issued by the store or taken from the route,
                    // the second is the resolved tenant. Both validators read RoleGroupTermsRules, so the
                    // two verbs cannot drift apart on a shared member.
                    typeof(CreateRoleGroupRequest),
                    typeof(UpdateRoleGroupRequest),

                    typeof(RoleAssignmentRequest),

                    // The two profile-definition write contracts. The procedures behind the verbs honour
                    // DIFFERENT member sets - AddPropertyDefinition (04.06.00:L1101) declares a
                    // module-definition key that UpdatePropertyDefinition (04.05.00:L1685) does not - so a
                    // single shape could not describe both without advertising a member one verb discards.
                    // Both validators read ProfileDefinitionTermsRules for the same anti-drift reason.
                    typeof(CreateProfilePropertyDefinitionRequest),
                    typeof(UpdateProfilePropertyDefinitionRequest),

                    // The page-update validator. Added when the page-edit endpoint was found to be judging
                    // nothing at all - an overlong value travelled to SQL Server and surfaced as a 500 naming
                    // no field, and a blank page name was stored as given. This inventory is the reason the
                    // gap was visible at all, and naming the request here is what keeps it closed: the
                    // endpoint binds it, so it must appear.
                    typeof(UpdateTabRequest),
                    typeof(PagedRequest),
                    typeof(PortalPagedRequest),
                    typeof(RolePagedRequest),
                    typeof(UserPagedRequest),
                    typeof(ModulePagedRequest),

                    // The role-membership paging request. Added because that listing BORROWED
                    // UserPagedRequest, so UserPagedRequestValidator resolved for it and applied the
                    // account collection's seven sortable names while RoleService.ListRoleUsersAsync
                    // enforces the role-membership set of ten - CreatedDate, LastLoginDate and IsApproved
                    // being the difference. Its ordering has an arm for each of the three, so the boundary
                    // was refusing an ordering the service could perform. This entry is the inverse of the
                    // defect that motivated the per-collection split: sharing one type made a listing
                    // accept a name it discarded, and borrowing another's made this one refuse a name it
                    // honoured. Both are cured by one type per collection.
                    typeof(RoleUserPagedRequest),

                    // Added when the write surface was audited for missing bounds. This request was bound
                    // by an endpoint while carrying NO validator at all, so every field on it reached the
                    // provider unbounded. Its presence here is what proves the assembly scan picks a new
                    // validator up with no registration edit, which is the property the scan exists to
                    // provide. The same audit reached the role update, the role assignment and the page
                    // update, each of which is already named above.
                    typeof(ModuleSettingsDto),

                    // The two shapes below are each bound by BOTH write verbs of their resource, so one
                    // validator governs create and update alike and the shape appears once. RoleGroupDto is
                    // bound by the role-group routes and ProfilePropertyDefinitionDto by the
                    // profile-definition routes; both are reached through the same assembly scan, which is
                    // why neither needed a registration edit to appear here.
                    typeof(RoleGroupDto),
                    typeof(ProfilePropertyDefinitionDto),
                },
                "every request an endpoint binds has a registered validator");
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
    /// </remarks>
    [Fact]
    public void ClaimNames_AreTheMeasuredWireNames()
    {
        DnnClaimTypes.Subject.Should().Be("sub");
        DnnClaimTypes.JwtId.Should().Be("jti");
        DnnClaimTypes.UniqueName.Should().Be("unique_name");
        DnnClaimTypes.PortalId.Should().Be("portal_id");
        DnnClaimTypes.SuperUser.Should().Be("is_superuser");
        DnnClaimTypes.Permission.Should().Be("permission");
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
        values.Should().HaveCount(6);
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
    /// Issuance asserts the subject, the tenant and the sign-in name it was handed, verbatim.
    /// </summary>
    /// <remarks>
    /// Every fact the emitted token asserts arrives as an argument, which is what keeps issuance free
    /// of repository access and therefore safe to implement as a singleton. Trimming, casing and
    /// canonicalisation belong to the sign-in service that resolved the account; repeating them here
    /// would make the token disagree with the read that produced it.
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_AssertsTheSubjectTenantAndSignInNameItWasHanded()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId,
            TenantId,
            AccountName,
            isSuperUser: true,
            ["Administrators"],
            ["EDIT"]);

        issued.IsSuccess.Should().BeTrue();

        LoginResponse response = issued.Value;

        response.User.UserId.Should().Be(AccountId);
        response.User.PortalId.Should().Be(TenantId);
        response.User.Username.Should().Be(AccountName, "the sign-in name is asserted exactly as handed");
        response.User.IsSuperUser.Should().BeTrue(
            "the host-level flag is asserted in its own right, because the legacy authorisation checks "
            + "short-circuit on it independently of role membership");
        response.AccessToken.Should().NotBeEmpty();
        response.RefreshToken.Should().NotBeEmpty();
    }

    /// <summary>
    /// The identifiers the legacy schema actually produces are accepted, including the ones a naive
    /// validity check would reject.
    /// </summary>
    /// <remarks>
    /// The tenant table seeds its identity column at minus one and the role table seeds its own at
    /// zero, so minus one and zero both name real rows. Minus one is simultaneously the legacy
    /// absent-integer sentinel at Library/Components/Shared/Null.vb:L41, which is precisely the
    /// collision that makes a generic "greater than zero" guard a live defect rather than a
    /// hypothetical one. This test exists so that introducing such a guard breaks a test rather than
    /// a tenant.
    /// </remarks>
    [Theory]
    [InlineData(AccountId, TenantId)]
    [InlineData(ZeroAccountId, TenantId)]
    [InlineData(ZeroAccountId, SecondTenantId)]
    [InlineData(TenantId, SecondTenantId)]
    public async Task IssueTokensAsync_AcceptsTheSeededAndZeroIdentifiersTheSchemaProduces(
        int userId,
        int portalId)
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            userId,
            portalId,
            AccountName,
            isSuperUser: false,
            [],
            []);

        issued.IsSuccess.Should().BeTrue("neither identifier may be read as meaning absent");
        issued.Value.User.UserId.Should().Be(userId);
        issued.Value.User.PortalId.Should().Be(portalId);
    }

    /// <summary>
    /// Issuance copies role and permission entries exactly as handed: order, casing and duplicates
    /// all survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entries were resolved by the caller from the role, assignment and role-group tables, so
    /// normalising them here would make the token disagree with the read that produced it. Casing
    /// matters most: role names and permission keys are persisted values matched by authorisation
    /// checks, and a renamed or re-cased entry authorises nobody while looking correct.
    /// </para>
    /// <para>
    /// Blank-entry removal and ordinal de-duplication do belong to this surface, but on the rotation
    /// path, where the implementation performs the read itself - asserted separately below. The two
    /// behaviours are deliberately different and are asserted separately for that reason.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_CopiesRoleAndPermissionEntriesExactlyAsHanded()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        string[] roles = ["Administrators", "registered users", "Registered Users", "Administrators"];
        string[] permissions = ["EDIT", "edit", "VIEW"];

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId,
            TenantId,
            AccountName,
            isSuperUser: false,
            roles,
            permissions);

        issued.Value.User.Roles.Should().Equal(roles, "order, casing and duplicates are all preserved");
        issued.Value.User.Permissions.Should().Equal(permissions);
    }

    /// <summary>
    /// The identity snapshot an issued pair carries exposes no credential, no signing material and
    /// nothing about refresh state.
    /// </summary>
    /// <remarks>
    /// The snapshot is the set of facts the access token asserts, so anything reachable through it is
    /// observable by whoever holds the token. Refresh state - the family a token belongs to, the
    /// generation within it, the stored digest - is server-side bookkeeping and must not appear;
    /// neither must a network address, which the legacy flow carried into its audit trail. The
    /// snapshot type is reached through the response property rather than named, so this assertion
    /// does not depend on where the projection type lives.
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_ExposesNoCredentialOrRefreshStateOnTheSnapshot()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId,
            TenantId,
            AccountName,
            isSuperUser: false,
            ["Administrators"],
            ["EDIT"]);

        string[] forbiddenFragments =
        [
            "password",
            "credential",
            "hash",
            "secret",
            "salt",
            "answer",
            "refresh",
            "family",
            "generation",
            "address",
            "ipaddress",
            "question",
        ];

        Type snapshot = issued.Value.User.GetType();

        foreach (PropertyInfo property in snapshot.GetProperties())
        {
            forbiddenFragments.Should().NotContain(
                fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                "{0} would put material into the token that the token has no business asserting",
                property.Name);
        }

        // Nor is profile data fetched to fill the snapshot out. Every fact a token asserts arrives as
        // an argument, and no argument carries a display name, an address or a tenant title - so these
        // stay at their empty default, and issuance performs no repository read to populate them.
        issued.Value.User.Email.Should().BeEmpty(
            "issuance reads no profile, which is what keeps it free of repository access");
        issued.Value.User.DisplayName.Should().BeEmpty();
        issued.Value.User.PortalName.Should().BeEmpty();
    }

    /// <summary>
    /// Issuance raises none of the three advisories: it passes them through, it never computes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy flow reported a weak distributed credential through its login-status
    /// enumeration, at Library/Components/Users/UserController.vb:L1145 and L1150. What makes that
    /// check portable is easy to miss and worth recording: it compares the <em>submitted</em> value
    /// against the credentials the product shipped with, <em>after</em> verification has already
    /// succeeded, and never reads the stored credential at all - so it survives the move to one-way
    /// hashing completely unchanged. It is reported as an informational reason attached to a
    /// successful outcome, which is exactly why the result type permits one.
    /// </para>
    /// <para>
    /// Deciding it, however, requires the submitted value, and no member of this contract accepts
    /// one. The three advisories are therefore the sign-in service's to raise; issuance leaves all
    /// three at their no-advisory value and attaches no reason of its own.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_PassesTheAdvisoriesThroughRatherThanComputingThem()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId,
            TenantId,
            "admin",
            isSuperUser: false,
            [],
            []);

        issued.IsSuccess.Should().BeTrue();
        issued.Reason.Should().BeNull("issuance attaches no advisory of its own");

        issued.Value.MustChangePassword.Should().BeFalse();
        issued.Value.PasswordExpiring.Should().BeFalse();
        issued.Value.MustUpdateProfile.Should().BeFalse();
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
    }

    /// <summary>
    /// A pair whose refresh half could not be recorded is not handed back at all.
    /// </summary>
    /// <remarks>
    /// Returning the pair anyway would appear to work and then fail at the caller's first exchange,
    /// which converts an immediate, diagnosable failure into an intermittent one an hour later.
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_ReportsAnUnwritableStoreAndReturnsNoPair()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        service.StoreAcceptsWrites = false;

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId,
            TenantId,
            AccountName,
            isSuperUser: false,
            [],
            []);

        issued.IsFailure.Should().BeTrue();
        issued.Error!.Code.Should().Be(StoreUnavailableCode);
        service.Records.Should().BeEmpty("nothing is recorded when the record could not be written");
    }

    /// <summary>
    /// Issued token values are long, all distinct, and share no prefix - so they come from neither a
    /// counter, nor a timestamp, nor the caller's own facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value drawn from a counter, a clock reading or a digest of caller data is guessable, and a
    /// guessable refresh token is a credential anybody can mint. The prefix assertion is what makes
    /// that testable without inspecting the generator: many values minted for the same account at the
    /// same instant would agree on a long leading run if any of those three were the source, whereas
    /// values from a cryptographic source agree on nothing. Sixteen hexadecimal characters is far
    /// beyond coincidence, so the assertion is decisive rather than probabilistic.
    /// </para>
    /// <para>
    /// The sign-in name check is the complement: the name contains characters no hexadecimal value can
    /// hold, so it can only appear if a future generator started embedding caller data.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_ProducesDistinctValuesThatEncodeNothingAboutTheCaller()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        List<string> refreshValues = [];
        List<string> accessValues = [];

        for (int issue = 0; issue < 32; issue++)
        {
            Result<LoginResponse> issued = await service.IssueTokensAsync(
                AccountId, TenantId, AccountName, isSuperUser: false, [], []);

            refreshValues.Add(issued.Value.RefreshToken);
            accessValues.Add(issued.Value.AccessToken);
        }

        refreshValues.Should().OnlyHaveUniqueItems();
        accessValues.Should().OnlyHaveUniqueItems();

        refreshValues.Should().OnlyContain(
            value => value.Length >= 32,
            "a short value is a guessable value");

        refreshValues.Select(value => value[..16]).Should().OnlyHaveUniqueItems(
            "a counter, a clock reading or a digest of the caller's facts would produce a shared "
            + "leading run, and a cryptographic source does not");

        refreshValues.Should().OnlyContain(value => !value.Contains(AccountName, StringComparison.Ordinal));
        accessValues.Should().OnlyContain(value => !value.Contains(AccountName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The access token lapses exactly one configured lifetime after the instant the clock reported.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy code read the web server's local clock directly, so a time-dependent
    /// outcome could not be reproduced in a test and could differ by the host's offset. Here the
    /// instant is injected, the arithmetic is universal-time throughout, and the assertion is exact
    /// rather than a tolerance window.
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_ExpiresTheAccessTokenOneConfiguredLifetimeAfterTheClockInstant()
    {
        JwtOptions options = new() { Secret = SyntheticSigningSecret, ExpirationMinutes = 45 };
        SpecifiedTokenService service = new(StoppedAt(Instant), options);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId, TenantId, AccountName, isSuperUser: false, [], []);

        issued.Value.ExpiresAtUtc.Should().Be(Instant.AddMinutes(45));
        issued.Value.ExpiresAtUtc.Kind.Should().Be(
            DateTimeKind.Utc,
            "the expiry inherits the universal-time basis of the instant it was derived from");
    }

    /// <summary>
    /// Every bound one issued token carries is derived from a single reading of the clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The clock supplied here returns a later instant on every read, so an implementation that read
    /// it once for the issued-at bound, again for the not-before bound and again for the expiry would
    /// emit a token whose own bounds disagreed - and a token that is valid from one instant but
    /// counted from another either lapses early or lives too long. Asserting the expiry against the
    /// <em>first</em> reading is what detects that, and the read count makes the property explicit
    /// rather than incidental.
    /// </para>
    /// <para>
    /// The scope of the claim is one operation's own token bounds. The rotation path legitimately
    /// takes a further reading for the eligibility question it re-asks, which is a different question
    /// asked at a different moment; what may never happen is one token's bounds being computed from
    /// two different instants.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_DerivesEveryBoundFromASingleReadingOfTheClock()
    {
        AdvancingClock clock = new(Instant, TimeSpan.FromMinutes(5));
        JwtOptions options = new() { Secret = SyntheticSigningSecret, ExpirationMinutes = 60 };
        SpecifiedTokenService service = new(clock, options);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId, TenantId, AccountName, isSuperUser: false, [], []);

        clock.Reads.Should().Be(1, "one operation reads the instant once and derives every bound from it");

        issued.Value.ExpiresAtUtc.Should().Be(
            Instant.AddMinutes(60),
            "the expiry counts from the first reading, not from a later one");

        service.Records.Should().ContainSingle().Which.IssuedAtUtc.Should().Be(
            Instant,
            "the recorded refresh half shares the instant the access half was minted from");
    }

    /// <summary>
    /// The refresh half lapses after the configured sliding lifetime, and never later than the
    /// family's ceiling.
    /// </summary>
    /// <remarks>
    /// Two deadlines govern a session and both are asserted here. Each token carries its own sliding
    /// expiry, and the family carries an absolute ceiling fixed when it was created; the effective
    /// expiry is whichever comes first, so a ceiling nearer than the sliding lifetime truncates it
    /// rather than being ignored.
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_BoundsTheRefreshHalfBySlidingLifetimeThenByTheFamilyCeiling()
    {
        JwtOptions sliding = new() { Secret = SyntheticSigningSecret, RefreshTokenExpirationDays = 7 };
        SpecifiedTokenService withRoom = new(StoppedAt(Instant), sliding);

        await withRoom.IssueTokensAsync(AccountId, TenantId, AccountName, isSuperUser: false, [], []);

        StoredRefresh recorded = withRoom.Records.Should().ContainSingle().Which;

        recorded.ExpiresAtUtc.Should().Be(Instant.AddDays(7), "the sliding lifetime governs");
        recorded.FamilyExpiresAtUtc.Should().Be(Instant.AddDays(30), "the ceiling is fixed at creation");

        JwtOptions capped = new()
        {
            Secret = SyntheticSigningSecret,
            RefreshTokenExpirationDays = 7,
            RefreshTokenAbsoluteExpirationDays = 3,
        };
        SpecifiedTokenService truncated = new(StoppedAt(Instant), capped);

        await truncated.IssueTokensAsync(AccountId, TenantId, AccountName, isSuperUser: false, [], []);

        truncated.Records.Should().ContainSingle().Which.ExpiresAtUtc.Should().Be(
            Instant.AddDays(3),
            "the nearer of the two deadlines wins");
    }

    /// <summary>
    /// Exchanging a refresh token mints a new access token, issues a new refresh token, and
    /// invalidates the value presented.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is what the legacy persistent authentication cookie becomes. The legacy setter
    /// is declared at Library/Components/Users/UserController.vb:L919, with the live call at L1033
    /// followed by a hand-built persistent ticket honouring a timeout application setting. Surviving
    /// beyond the browser session is exactly what that cookie was for, and a rotating refresh token
    /// does the same job while being single-use and revocable, which the cookie was neither.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_IssuesANewPairAndInvalidatesThePresentedToken()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await IssuedRefreshToken(service);

        Result<LoginResponse> rotated = await service.RefreshAsync(presented);

        rotated.IsSuccess.Should().BeTrue();
        rotated.Value.RefreshToken.Should().NotBe(
            presented,
            "rotation is the point: the successor is a different value");
        rotated.Value.AccessToken.Should().NotBeEmpty();

        Result<LoginResponse> replayed = await service.RefreshAsync(presented);

        replayed.IsFailure.Should().BeTrue("a refresh token is single-use");
        replayed.Error!.Code.Should().Be(RefreshAlreadyUsedCode);
    }

    /// <summary>
    /// The four rotation failure modes are reported as four distinct, stable codes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collapsing them would make the contract unable to say which condition applied, and the API
    /// layer answers an expired token differently from a revoked one. The codes are fixed by the
    /// contract precisely so that two implementations report the same condition identically.
    /// </para>
    /// <para>
    /// The distinctness assertion is the one that matters: a well-meaning refactor that mapped every
    /// rotation failure onto one code would satisfy each individual expectation below and still
    /// destroy the property, so the four are compared against each other as well as against their
    /// expected values.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_ReportsFourDistinguishableFailureCodes()
    {
        string unknown = await UnknownTokenFailureCode();
        string revoked = await RevokedTokenFailureCode();
        string alreadyUsed = await AlreadyUsedTokenFailureCode();
        string expired = await ExpiredTokenFailureCode();

        unknown.Should().Be(RefreshNotFoundCode);
        revoked.Should().Be(RefreshRevokedCode);
        alreadyUsed.Should().Be(RefreshAlreadyUsedCode);
        expired.Should().Be(RefreshExpiredCode);

        new[] { unknown, revoked, alreadyUsed, expired }.Should().OnlyHaveUniqueItems(
            "four conditions the caller must tell apart cannot share one code");
    }

    /// <summary>
    /// A rotation failure is returned, not thrown, and reading the absent value is the only thing
    /// that raises.
    /// </summary>
    /// <remarks>
    /// An expired or replayed refresh token is ordinary control flow rather than an exceptional
    /// condition, so it travels as a failed outcome. Reading the value of a failed outcome is a
    /// different matter - it means the caller skipped its own success check - and that is a defect in
    /// the calling code, which is why it raises a plain framework exception.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_ReturnsAFailedOutcomeRatherThanThrowing()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Func<Task> exchange = () => service.RefreshAsync(UnissuedValue);

        await exchange.Should().NotThrowAsync("an unusable token is an expected outcome");

        Result<LoginResponse> refused = await service.RefreshAsync(UnissuedValue);

        refused.IsFailure.Should().BeTrue();
        refused.Error.Should().NotBeNull("every failed outcome is explicable");

        Action readingTheAbsentValue = () => _ = refused.Value;

        readingTheAbsentValue.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// No rotation failure reveals the presented value or anything about the family behind it.
    /// </summary>
    /// <remarks>
    /// A failure message travels to the client and into the log, so a message quoting the value would
    /// publish a credential. Neither may it name the family or the account, which would turn the
    /// endpoint into an oracle about state the caller is not entitled to.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_RevealsNoTokenMaterialInItsFailure()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await IssuedRefreshToken(service);

        await service.RefreshAsync(presented);

        Result<LoginResponse> replayed = await service.RefreshAsync(presented);

        replayed.Error!.Code.Should().NotContain(presented);
        replayed.Error.Message.Should().NotContain(presented);
        replayed.Error.Message.Should().NotContain(AccountName);
        replayed.Error.Message.Should().NotContain(
            AccountId.ToString(CultureInfo.InvariantCulture),
            "a refusal names the condition, never the account or the value");
    }

    /// <summary>
    /// Presenting an already-exchanged token ends every session the account holds.
    /// </summary>
    /// <remarks>
    /// Re-presentation of a single-use value is the signature of a stolen token: the legitimate holder
    /// rotated it, so a second presentation came from somewhere else. The correct response to a
    /// suspected theft is to end every session, not merely to decline this one exchange - so the
    /// successor the legitimate holder is still using stops working too, and its holder authenticates
    /// again.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_EndsEverySessionWhenAReplayIsDetected()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        string presented = await IssuedRefreshToken(service);
        string unrelated = await IssuedRefreshToken(service);

        Result<LoginResponse> rotated = await service.RefreshAsync(presented);
        string successor = rotated.Value.RefreshToken;

        Result<LoginResponse> replayed = await service.RefreshAsync(presented);

        replayed.Error!.Code.Should().Be(RefreshAlreadyUsedCode);

        Result<LoginResponse> afterTheft = await service.RefreshAsync(successor);

        afterTheft.IsFailure.Should().BeTrue("the successor belongs to a family that has been ended");
        afterTheft.Error!.Code.Should().Be(RefreshRevokedCode);

        Result<LoginResponse> otherSession = await service.RefreshAsync(unrelated);

        otherSession.IsFailure.Should().BeTrue(
            "the response reaches every family the account holds, not only the replayed one");
        otherSession.Error!.Code.Should().Be(RefreshRevokedCode);
    }

    /// <summary>
    /// The revoked condition is evaluated before the already-used one, so the theft response fires
    /// once and then falls quiet.
    /// </summary>
    /// <remarks>
    /// This ordering is not interchangeable and the reason is worth stating. Ending every session
    /// revokes the replayed record along with the rest, so a third and fourth presentation of the same
    /// value land on the revoked arm and change nothing. Were the already-used condition evaluated
    /// first, one copied value would become a reusable instrument for ending whatever sessions its
    /// owner had opened since - which hands a means of permanently signing somebody off to exactly
    /// the party who should not hold one.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_EvaluatesRevocationBeforeReplaySoTheTheftResponseFiresOnce()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await IssuedRefreshToken(service);

        await service.RefreshAsync(presented);
        await service.RefreshAsync(presented);

        service.SessionsEndedForTheft.Should().Be(1);

        Result<LoginResponse> third = await service.RefreshAsync(presented);
        Result<LoginResponse> fourth = await service.RefreshAsync(presented);

        third.Error!.Code.Should().Be(RefreshRevokedCode);
        fourth.Error!.Code.Should().Be(RefreshRevokedCode);
        service.SessionsEndedForTheft.Should().Be(
            1,
            "further presentations of the same value are not a fresh signal");
    }

    /// <summary>
    /// The rotated access token is minted from the account's current authority, not copied from the
    /// token being replaced.
    /// </summary>
    /// <remarks>
    /// A role granted or withdrawn since the last exchange therefore takes effect within one
    /// access-token lifetime instead of persisting until the caller signs in again. The re-read is
    /// also the security property: an implementation that forwarded the entries travelling with the
    /// request would hand a caller whatever authority it cared to name, and no check inside a token
    /// store could detect it.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_MintsFromCurrentAuthorityRatherThanTheReplacedToken()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId,
            TenantId,
            AccountName,
            isSuperUser: false,
            ["Subscribers"],
            ["VIEW"]);

        service.RecordAuthority(AccountId, isSuperUser: true, ["Administrators"], ["EDIT", "VIEW"]);

        Result<LoginResponse> rotated = await service.RefreshAsync(issued.Value.RefreshToken);

        rotated.Value.User.Roles.Should().Equal("Administrators");
        rotated.Value.User.Permissions.Should().Equal("EDIT", "VIEW");
        rotated.Value.User.IsSuperUser.Should().BeTrue(
            "the host-level flag is one of the three mutable authority facts and is re-read too");
    }

    /// <summary>
    /// The subject, the tenant and the sign-in name of a rotated pair come from the implementation's
    /// own record.
    /// </summary>
    /// <remarks>
    /// There is no parameter through which a caller could supply them, which is the mechanism rather
    /// than a convention - and it is why the withdrawal of every role still yields a token for the
    /// same account in the same tenant rather than for nobody.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_TakesTheSubjectAndTenantFromItsOwnRecord()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            ZeroAccountId,
            TenantId,
            AccountName,
            isSuperUser: false,
            ["Subscribers"],
            []);

        service.RecordAuthority(ZeroAccountId, isSuperUser: false, [], []);

        Result<LoginResponse> rotated = await service.RefreshAsync(issued.Value.RefreshToken);

        rotated.Value.User.UserId.Should().Be(ZeroAccountId);
        rotated.Value.User.PortalId.Should().Be(TenantId);
        rotated.Value.User.Username.Should().Be(AccountName);
        rotated.Value.User.Roles.Should().BeEmpty("the re-read found no role still in force");
    }

    /// <summary>
    /// Exchanging a token moves the access token's expiry forward but never the session's deadline.
    /// </summary>
    /// <remarks>
    /// A family receives one absolute deadline when its first token is issued, and every successor
    /// inherits it unchanged. A session therefore ends at a determined instant however continuously it
    /// is used - which is a security property rather than an implementation note, because a deadline
    /// each exchange pushed further out would never be reached by a token that kept being exchanged,
    /// including one being exchanged by a thief.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_MovesTheAccessTokenExpiryButNotTheSessionDeadline()
    {
        AdvancingClock clock = new(Instant, TimeSpan.FromMinutes(30));
        JwtOptions options = new() { Secret = SyntheticSigningSecret, ExpirationMinutes = 60 };
        SpecifiedTokenService service = new(clock, options);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId, TenantId, AccountName, isSuperUser: false, [], []);

        DateTime ceilingAtIssue = service.Records.Single().FamilyExpiresAtUtc;

        service.RecordAuthority(AccountId, isSuperUser: false, [], []);

        Result<LoginResponse> rotated = await service.RefreshAsync(issued.Value.RefreshToken);

        rotated.Value.ExpiresAtUtc.Should().Be(
            Instant.AddMinutes(30).AddMinutes(60),
            "the rotated access token counts from the instant of the exchange");

        service.Records.Select(record => record.FamilyExpiresAtUtc).Should().AllBeEquivalentTo(
            ceilingAtIssue,
            "the successor inherits the deadline rather than earning a new one");
    }

    /// <summary>
    /// The re-read that feeds a rotated token drops blank names and collapses exact duplicates with
    /// ordinal semantics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the counterpart to the verbatim copy on the issuing path, and the difference is
    /// deliberate. Here the implementation performs the read itself, joining assignments to the
    /// tenant's roles, so it owns the result: a role whose name is blank cannot be matched by any
    /// authorisation check and an assignment whose role the tenant does not have has no name to
    /// assert, so neither becomes a claim. That is not the filtering the issuing path forbids -
    /// there is no value to carry, rather than a value judged unworthy of carrying.
    /// </para>
    /// <para>
    /// De-duplication is ordinal, which is why two entries differing only in case both survive:
    /// role names and permission keys are persisted values, and treating them as equal would silently
    /// drop one of two distinct grants.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_DropsBlankNamesAndCollapsesOrdinalDuplicatesOnTheReRead()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId, TenantId, AccountName, isSuperUser: false, [], []);

        service.RecordAuthority(
            AccountId,
            isSuperUser: false,
            ["Administrators", "Administrators", "   ", string.Empty, "administrators"],
            ["EDIT", "EDIT", " ", "edit"]);

        Result<LoginResponse> rotated = await service.RefreshAsync(issued.Value.RefreshToken);

        rotated.Value.User.Roles.Should().Equal(
            "Administrators",
            "administrators");
        rotated.Value.User.Permissions.Should().Equal("EDIT", "edit");
    }

    /// <summary>
    /// An exchange abandoned with its request performs no rotation.
    /// </summary>
    /// <remarks>
    /// Cancellation is not an expected failure in the outcome sense - it is the caller withdrawing
    /// the question - so it surfaces as the framework's own cancellation exception rather than as a
    /// code. What matters is that the token presented is left exchangeable, because a cancellation
    /// that consumed it would cost the caller its session for no reason.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_HonoursCancellationWithoutConsumingThePresentedToken()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await IssuedRefreshToken(service);

        using CancellationTokenSource abandoned = new();
        await abandoned.CancelAsync();

        Func<Task> exchange = () => service.RefreshAsync(presented, abandoned.Token);

        await exchange.Should().ThrowAsync<OperationCanceledException>();

        service.RecordAuthority(AccountId, isSuperUser: false, [], []);

        Result<LoginResponse> afterwards = await service.RefreshAsync(presented);

        afterwards.IsSuccess.Should().BeTrue("an abandoned exchange leaves the token usable");
    }

    /// <summary>
    /// Revoking one refresh token ends the family it belongs to and stops any further exchange.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is the whole of what a logout can do on the server. The legacy forms-
    /// authentication sign-out at Library/Components/Security/PortalSecurity.vb:L77 ended the session
    /// instantly by destroying the cookies it lived in; here the caller's obligations are to call this
    /// operation and to discard its own copy of the access token, and the residual window is bounded
    /// by the configured access-token lifetime alone.
    /// </remarks>
    [Fact]
    public async Task RevokeRefreshTokenAsync_EndsTheFamilyAndStopsFurtherExchange()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await IssuedRefreshToken(service);

        Result revocation = await service.RevokeRefreshTokenAsync(presented);

        revocation.IsSuccess.Should().BeTrue();

        Result<LoginResponse> afterwards = await service.RefreshAsync(presented);

        afterwards.IsFailure.Should().BeTrue();
        afterwards.Error!.Code.Should().Be(RefreshRevokedCode);
    }

    /// <summary>
    /// Revocation is idempotent and says nothing about whether the value existed.
    /// </summary>
    /// <remarks>
    /// Two reasons, both load-bearing. A logout that fails is worse than useless, because a client
    /// that cannot complete one is likely to keep the token; and reporting "no such token" would make
    /// the operation an oracle telling an unauthenticated caller whether a guessed value exists.
    /// Rotation makes that distinction because its caller is asking for something and the answer is
    /// actionable; this operation does not, because its caller is giving something up.
    /// </remarks>
    [Theory]
    [InlineData(NeverIssued)]
    [InlineData(AlreadyExchanged)]
    [InlineData(AlreadyRevoked)]
    public async Task RevokeRefreshTokenAsync_SucceedsWhateverStateTheValueWasIn(string condition)
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await TokenIn(service, condition);

        Result first = await service.RevokeRefreshTokenAsync(presented);
        Result second = await service.RevokeRefreshTokenAsync(presented);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue("the desired state was already reached");
        first.Error.Should().BeNull();
        second.Error.Should().BeNull();
    }

    /// <summary>
    /// Revocation leaves an already-issued access token exactly as it was.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the executable form of the divergence recorded on this contract. A bearer access
    /// token is self-contained, so no server action retracts one: revocation guarantees only that no
    /// successor is ever minted. Asserting it here is what stops a later change from adding a
    /// rejected-token list and quietly reintroducing the server-held session - and the assertion is
    /// deliberately written as "the value the caller holds is unchanged", because there is no member
    /// on this contract through which its validity could even be questioned.
    /// </remarks>
    [Fact]
    public async Task RevokeRefreshTokenAsync_LeavesAnAlreadyIssuedAccessTokenUntouched()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId, TenantId, AccountName, isSuperUser: false, ["Administrators"], ["EDIT"]);

        string accessToken = issued.Value.AccessToken;
        DateTime expiresAtUtc = issued.Value.ExpiresAtUtc;

        await service.RevokeRefreshTokenAsync(issued.Value.RefreshToken);

        issued.Value.AccessToken.Should().Be(accessToken);
        issued.Value.ExpiresAtUtc.Should().Be(
            expiresAtUtc,
            "the access half lapses on its own schedule and revocation cannot bring that forward");

        typeof(ITokenService).GetMethods().Should().NotContain(
            member => member.GetParameters().Any(parameter => parameter.Name == "accessToken"),
            "no operation takes an access token, so none can retract one");
    }

    /// <summary>
    /// Revoking every token an account holds ends all of its families in one step.
    /// </summary>
    /// <remarks>
    /// Revoking them one family at a time would leave a window in which a concurrent exchange could
    /// mint a successor into a family the operation had not reached yet, and that successor would
    /// outlive the revocation entirely - which is the outcome the operation exists to prevent. Its
    /// callers are obligations rather than options: a credential change, an administrative reset, the
    /// withdrawal of an approval and the deletion of an account each leave an account that may no
    /// longer authenticate but would otherwise keep obtaining new access tokens.
    /// </remarks>
    [Fact]
    public async Task RevokeAllRefreshTokensAsync_EndsEveryFamilyTheAccountHolds()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        string first = await IssuedRefreshToken(service);
        string second = await IssuedRefreshToken(service);
        string otherAccount = await IssuedRefreshToken(service, ZeroAccountId);

        Result revocation = await service.RevokeAllRefreshTokensAsync(AccountId);

        revocation.IsSuccess.Should().BeTrue();

        (await service.RefreshAsync(first)).Error!.Code.Should().Be(RefreshRevokedCode);
        (await service.RefreshAsync(second)).Error!.Code.Should().Be(RefreshRevokedCode);

        service.RecordAuthority(ZeroAccountId, isSuperUser: false, [], []);

        (await service.RefreshAsync(otherAccount)).IsSuccess.Should().BeTrue(
            "the reach is one account, not the whole store");
    }

    /// <summary>
    /// Revoking every token of an account that holds none succeeds, for both seeded identifiers.
    /// </summary>
    [Theory]
    [InlineData(ZeroAccountId)]
    [InlineData(TenantId)]
    public async Task RevokeAllRefreshTokensAsync_SucceedsForAnAccountHoldingNothing(int userId)
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result revocation = await service.RevokeAllRefreshTokensAsync(userId);

        revocation.IsSuccess.Should().BeTrue(
            "an account holding no refresh token is already in the desired state, and neither zero "
            + "nor minus one may be read as meaning absent");
    }

    /// <summary>
    /// A revocation that could not be persisted is reported, because the token remains exchangeable.
    /// </summary>
    [Fact]
    public async Task Revocation_ReportsAnUnwritableStore()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await IssuedRefreshToken(service);

        service.StoreAcceptsWrites = false;

        Result single = await service.RevokeRefreshTokenAsync(presented);
        Result all = await service.RevokeAllRefreshTokensAsync(AccountId);

        single.Error!.Code.Should().Be(StoreUnavailableCode);
        all.Error!.Code.Should().Be(StoreUnavailableCode);
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
            .Setup(tokens => tokens.RefreshAsync("presented", lifetime.Token))
            .ReturnsAsync(Result<LoginResponse>.Failure(RefreshExpiredCode, "The refresh token has expired."));

        Result<LoginResponse> refused = await substitute.Object.RefreshAsync("presented", lifetime.Token);

        refused.IsFailure.Should().BeTrue();
        refused.Error!.Code.Should().Be(RefreshExpiredCode);

        substitute.Verify(
            tokens => tokens.RefreshAsync("presented", lifetime.Token),
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

    /// <summary>Builds a service whose clock is stopped at <paramref name="instant"/>.</summary>
    /// <param name="instant">The instant the clock reports on every read.</param>
    /// <returns>The service.</returns>
    private static SpecifiedTokenService ServiceStoppedAt(DateTime instant)
        => new(StoppedAt(instant), new JwtOptions { Secret = SyntheticSigningSecret });

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

    /// <summary>Issues a pair and returns its refresh half, ready to be exchanged.</summary>
    /// <param name="service">The service to issue from.</param>
    /// <param name="userId">The account to issue for.</param>
    /// <returns>The refresh-token value.</returns>
    private static async Task<string> IssuedRefreshToken(
        SpecifiedTokenService service,
        int userId = AccountId)
    {
        Result<LoginResponse> issued = await service.IssueTokensAsync(
            userId,
            TenantId,
            AccountName,
            isSuperUser: false,
            [],
            []);

        service.RecordAuthority(userId, isSuperUser: false, [], []);

        return issued.Value.RefreshToken;
    }

    /// <summary>Produces a refresh-token value in the requested condition.</summary>
    /// <param name="service">The service to act on.</param>
    /// <param name="condition">The condition to leave the value in.</param>
    /// <returns>The value.</returns>
    private static async Task<string> TokenIn(SpecifiedTokenService service, string condition)
    {
        if (condition == NeverIssued)
        {
            return UnissuedValue;
        }

        string presented = await IssuedRefreshToken(service);

        if (condition == AlreadyExchanged)
        {
            await service.RefreshAsync(presented);
        }
        else
        {
            await service.RevokeRefreshTokenAsync(presented);
        }

        return presented;
    }

    /// <summary>Reports the code produced by presenting a value that was never issued.</summary>
    /// <returns>The failure code.</returns>
    private static async Task<string> UnknownTokenFailureCode()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);

        Result<LoginResponse> refused = await service.RefreshAsync(UnissuedValue);

        return refused.Error!.Code;
    }

    /// <summary>Reports the code produced by presenting a revoked value.</summary>
    /// <returns>The failure code.</returns>
    private static async Task<string> RevokedTokenFailureCode()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await IssuedRefreshToken(service);

        await service.RevokeRefreshTokenAsync(presented);

        Result<LoginResponse> refused = await service.RefreshAsync(presented);

        return refused.Error!.Code;
    }

    /// <summary>Reports the code produced by presenting a value a second time.</summary>
    /// <returns>The failure code.</returns>
    private static async Task<string> AlreadyUsedTokenFailureCode()
    {
        SpecifiedTokenService service = ServiceStoppedAt(Instant);
        string presented = await IssuedRefreshToken(service);

        await service.RefreshAsync(presented);

        Result<LoginResponse> refused = await service.RefreshAsync(presented);

        return refused.Error!.Code;
    }

    /// <summary>Reports the code produced by presenting a value past its deadline.</summary>
    /// <returns>The failure code.</returns>
    /// <remarks>
    /// The clock steps eight days between the issue and the exchange, which is past the shipped
    /// seven-day sliding lifetime and short of the thirty-day ceiling - so the sliding bound is the
    /// one being exercised, and it is exercised without the test waiting for anything.
    /// </remarks>
    private static async Task<string> ExpiredTokenFailureCode()
    {
        AdvancingClock clock = new(Instant, TimeSpan.FromDays(8));
        SpecifiedTokenService service = new(clock, new JwtOptions { Secret = SyntheticSigningSecret });

        Result<LoginResponse> issued = await service.IssueTokensAsync(
            AccountId,
            TenantId,
            AccountName,
            isSuperUser: false,
            [],
            []);

        service.RecordAuthority(AccountId, isSuperUser: false, [], []);

        Result<LoginResponse> refused = await service.RefreshAsync(issued.Value.RefreshToken);

        return refused.Error!.Code;
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

    /// <summary>A clock stopped at one instant, reporting how many times it was read.</summary>
    /// <remarks>
    /// Time control comes from implementing the contract, never from a mutator on it: the abstraction
    /// declares one get-only member and widening it so that a test could push the clock forward would
    /// put a test affordance into production code.
    /// </remarks>
    private sealed class AdvancingClock : IClock
    {
        private readonly DateTime _first;
        private readonly TimeSpan _step;
        private int _reads;

        /// <summary>Initialises the clock.</summary>
        /// <param name="first">The instant the first read reports.</param>
        /// <param name="step">How far each subsequent read moves forward.</param>
        public AdvancingClock(DateTime first, TimeSpan step)
        {
            _first = first;
            _step = step;
        }

        /// <summary>Gets the number of reads performed so far.</summary>
        public int Reads => _reads;

        /// <summary>Gets the present instant, moving on with every read.</summary>
        /// <remarks>
        /// Deliberately never twice the same, which is what makes a consumer that derives one token's
        /// bounds from several readings observably wrong instead of merely slightly inconsistent.
        /// </remarks>
        public DateTime UtcNow => _first + (_step * _reads++);
    }

    /// <summary>One recorded refresh token, holding everything the specification needs of it.</summary>
    private sealed class StoredRefresh
    {
        /// <summary>Gets the value handed to the caller.</summary>
        public required string Value { get; init; }

        /// <summary>Gets the account the token was issued to.</summary>
        public required int UserId { get; init; }

        /// <summary>Gets the tenant the token was issued in.</summary>
        public required int PortalId { get; init; }

        /// <summary>Gets the sign-in name recorded against the token.</summary>
        public required string UserName { get; init; }

        /// <summary>Gets the family this token belongs to.</summary>
        public required int FamilyId { get; init; }

        /// <summary>Gets the instant the token was minted.</summary>
        public required DateTime IssuedAtUtc { get; init; }

        /// <summary>Gets the instant this token stops being exchangeable.</summary>
        public required DateTime ExpiresAtUtc { get; init; }

        /// <summary>Gets the absolute deadline shared by every generation of the family.</summary>
        public required DateTime FamilyExpiresAtUtc { get; init; }

        /// <summary>Gets or sets a value indicating whether the token has been exchanged.</summary>
        public bool IsConsumed { get; set; }

        /// <summary>Gets or sets a value indicating whether the token has been revoked.</summary>
        public bool IsRevoked { get; set; }
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
    /// An executable statement of the documented token contract, standing in for the concrete signing
    /// implementation that this project is not permitted to see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It encodes exactly the terms the contract fixes and nothing beyond them: the single reading of
    /// the injected clock that every bound of one token derives from; the sliding per-token expiry
    /// capped by a per-family ceiling fixed at creation and inherited unchanged; single-use exchange
    /// with the successor written in the same step that consumes its predecessor; the four failure
    /// codes and the order they are evaluated in; the theft response that ends every session an
    /// account holds; verbatim copying of the entries handed to issuance against an
    /// implementation-owned read on rotation; and idempotent revocation.
    /// </para>
    /// <para>
    /// It signs nothing, because signing needs a token library this project does not reference - which
    /// is why the values it hands back are opaque random strings and why real signature, issuer,
    /// audience and lifetime validation is asserted in DnnMigration.IntegrationTests instead. Values
    /// come from a cryptographic random source rather than a counter or a timestamp, because a
    /// guessable refresh token is a credential anybody can mint, and that property is part of the
    /// specification rather than an incidental detail of this double.
    /// </para>
    /// </remarks>
    private sealed class SpecifiedTokenService : ITokenService
    {
        private const string NotValidMessage = "The refresh token is not valid.";

        private const string ExpiredMessage = "The refresh token has expired.";

        private const string StoreUnavailableMessage = "Refresh-token state could not be persisted.";

        private readonly IClock _clock;
        private readonly JwtOptions _options;
        private readonly Dictionary<string, StoredRefresh> _byValue = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Authority> _authority = [];
        private int _nextFamily;

        /// <summary>Initialises the service.</summary>
        /// <param name="clock">The only sanctioned source of the present instant.</param>
        /// <param name="options">The bound configuration governing both lifetimes.</param>
        public SpecifiedTokenService(IClock clock, JwtOptions options)
        {
            _clock = clock;
            _options = options;
        }

        /// <summary>Gets or sets a value indicating whether refresh state can be persisted.</summary>
        public bool StoreAcceptsWrites { get; set; } = true;

        /// <summary>Gets the number of times a replay triggered the theft response.</summary>
        public int SessionsEndedForTheft { get; private set; }

        /// <summary>Gets the refresh-token records currently held.</summary>
        public IReadOnlyList<StoredRefresh> Records => [.. _byValue.Values];

        /// <summary>Records what an authority re-read would currently find for one account.</summary>
        /// <param name="userId">The account.</param>
        /// <param name="isSuperUser">Whether it is installation-wide.</param>
        /// <param name="roles">The role names in force.</param>
        /// <param name="permissionKeys">The permission keys held.</param>
        public void RecordAuthority(
            int userId,
            bool isSuperUser,
            IReadOnlyList<string> roles,
            IReadOnlyList<string> permissionKeys)
            => _authority[userId] = new Authority(isSuperUser, roles, permissionKeys);

        /// <inheritdoc/>
        public Task<Result<LoginResponse>> IssueTokensAsync(
            int userId,
            int portalId,
            string userName,
            bool isSuperUser,
            IReadOnlyList<string> roles,
            IReadOnlyList<string> permissionKeys,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(userName);
            ArgumentNullException.ThrowIfNull(roles);
            ArgumentNullException.ThrowIfNull(permissionKeys);
            cancellationToken.ThrowIfCancellationRequested();

            if (!StoreAcceptsWrites)
            {
                return Refused(StoreUnavailableCode, StoreUnavailableMessage);
            }

            DateTime issuedAtUtc = _clock.UtcNow;
            DateTime familyExpiresAtUtc = issuedAtUtc.AddDays(_options.RefreshTokenAbsoluteExpirationDays);

            StoredRefresh record = Record(
                userId,
                portalId,
                userName,
                ++_nextFamily,
                issuedAtUtc,
                familyExpiresAtUtc);

            // Verbatim: these entries were resolved by the caller, so normalising them here would make
            // the token disagree with the read that produced it.
            return Task.FromResult(Result<LoginResponse>.Success(
                Build(record, issuedAtUtc, isSuperUser, roles, permissionKeys)));
        }

        /// <inheritdoc/>
        public Task<Result<LoginResponse>> RefreshAsync(
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(refreshToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_byValue.TryGetValue(refreshToken, out StoredRefresh? presented))
            {
                return Refused(RefreshNotFoundCode, NotValidMessage);
            }

            // Revoked precedes already-used deliberately: the theft response revokes the replayed
            // record along with the rest, so a third presentation lands here and changes nothing.
            if (presented.IsRevoked)
            {
                return Refused(RefreshRevokedCode, NotValidMessage);
            }

            if (presented.IsConsumed)
            {
                EndEverySession(presented.UserId);

                return Refused(RefreshAlreadyUsedCode, NotValidMessage);
            }

            DateTime asOfUtc = _clock.UtcNow;

            if (presented.ExpiresAtUtc <= asOfUtc)
            {
                return Refused(RefreshExpiredCode, ExpiredMessage);
            }

            if (!StoreAcceptsWrites)
            {
                return Refused(StoreUnavailableCode, StoreUnavailableMessage);
            }

            presented.IsConsumed = true;

            StoredRefresh successor = Record(
                presented.UserId,
                presented.PortalId,
                presented.UserName,
                presented.FamilyId,
                asOfUtc,
                presented.FamilyExpiresAtUtc);

            Authority current = _authority.GetValueOrDefault(presented.UserId) ?? Authority.None;

            return Task.FromResult(Result<LoginResponse>.Success(Build(
                successor,
                asOfUtc,
                current.IsSuperUser,
                InForce(current.Roles),
                InForce(current.PermissionKeys))));
        }

        /// <inheritdoc/>
        public Task<Result> RevokeRefreshTokenAsync(
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(refreshToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!StoreAcceptsWrites)
            {
                return Reported(StoreUnavailableCode, StoreUnavailableMessage);
            }

            if (_byValue.TryGetValue(refreshToken, out StoredRefresh? presented))
            {
                RevokeFamily(presented.FamilyId);
            }

            // Idempotent on purpose: an unknown, used or already-revoked value succeeds, so this
            // operation never becomes an oracle telling a caller whether a guessed value exists.
            return Task.FromResult(Result.Success());
        }

        /// <inheritdoc/>
        public Task<Result> RevokeAllRefreshTokensAsync(
            int userId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!StoreAcceptsWrites)
            {
                return Reported(StoreUnavailableCode, StoreUnavailableMessage);
            }

            RevokeEvery(userId);

            return Task.FromResult(Result.Success());
        }

        /// <summary>Reports a refused exchange or issue.</summary>
        /// <param name="code">The contract's code for the condition.</param>
        /// <param name="message">A message that names the condition and nothing else.</param>
        /// <returns>The failed outcome.</returns>
        private static Task<Result<LoginResponse>> Refused(string code, string message)
            => Task.FromResult(Result<LoginResponse>.Failure(code, message));

        /// <summary>Reports a revocation that could not be persisted.</summary>
        /// <param name="code">The contract's code for the condition.</param>
        /// <param name="message">A message that names the condition and nothing else.</param>
        /// <returns>The failed outcome.</returns>
        private static Task<Result> Reported(string code, string message)
            => Task.FromResult(Result.Failure(code, message));

        /// <summary>
        /// Reduces the entries an authority re-read produced to the ones that can be asserted.
        /// </summary>
        /// <param name="resolved">The entries the re-read found.</param>
        /// <returns>The entries to assert.</returns>
        /// <remarks>
        /// Applied on the rotation path only, where the implementation owns the read. A blank name
        /// cannot be matched by any authorisation check, so it has nothing to assert; de-duplication is
        /// ordinal because role names and permission keys are persisted values and two entries
        /// differing only in case are two distinct grants.
        /// </remarks>
        private static IReadOnlyList<string> InForce(IReadOnlyList<string> resolved)
        {
            SortedSet<string> kept = new(StringComparer.Ordinal);

            foreach (string entry in resolved)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    kept.Add(entry);
                }
            }

            return [.. kept];
        }

        /// <summary>Writes one refresh-token record.</summary>
        /// <param name="userId">The account.</param>
        /// <param name="portalId">The tenant.</param>
        /// <param name="userName">The sign-in name.</param>
        /// <param name="familyId">The family, shared with every generation of it.</param>
        /// <param name="issuedAtUtc">The single instant this generation is derived from.</param>
        /// <param name="familyExpiresAtUtc">The family's inherited absolute deadline.</param>
        /// <returns>The record.</returns>
        private StoredRefresh Record(
            int userId,
            int portalId,
            string userName,
            int familyId,
            DateTime issuedAtUtc,
            DateTime familyExpiresAtUtc)
        {
            DateTime sliding = issuedAtUtc.AddDays(_options.RefreshTokenExpirationDays);

            StoredRefresh record = new()
            {
                Value = RandomNumberGenerator.GetHexString(64, lowercase: true),
                UserId = userId,
                PortalId = portalId,
                UserName = userName,
                FamilyId = familyId,
                IssuedAtUtc = issuedAtUtc,
                ExpiresAtUtc = sliding <= familyExpiresAtUtc ? sliding : familyExpiresAtUtc,
                FamilyExpiresAtUtc = familyExpiresAtUtc,
            };

            _byValue.Add(record.Value, record);

            return record;
        }

        /// <summary>Builds the pair a successful issue or exchange hands back.</summary>
        /// <param name="record">The refresh half just recorded.</param>
        /// <param name="issuedAtUtc">The single instant every bound derives from.</param>
        /// <param name="isSuperUser">Whether the account is installation-wide.</param>
        /// <param name="roles">The role names to assert.</param>
        /// <param name="permissionKeys">The permission keys to assert.</param>
        /// <returns>The pair.</returns>
        /// <remarks>
        /// The snapshot type is reached through the response's own property rather than named, so
        /// nothing here depends on where that projection lives. The three advisory booleans are left
        /// at their no-advisory value: raising one needs the submitted credential, which no member of
        /// this contract accepts.
        /// </remarks>
        private LoginResponse Build(
            StoredRefresh record,
            DateTime issuedAtUtc,
            bool isSuperUser,
            IReadOnlyList<string> roles,
            IReadOnlyList<string> permissionKeys)
            => new()
            {
                AccessToken = RandomNumberGenerator.GetHexString(96, lowercase: true),
                RefreshToken = record.Value,
                ExpiresAtUtc = issuedAtUtc.AddMinutes(_options.ExpirationMinutes),
                User = new()
                {
                    UserId = record.UserId,
                    PortalId = record.PortalId,
                    Username = record.UserName,
                    IsSuperUser = isSuperUser,
                    Roles = roles,
                    Permissions = permissionKeys,
                },
            };

        /// <summary>Ends every session an account holds, in response to a suspected theft.</summary>
        /// <param name="userId">The account.</param>
        private void EndEverySession(int userId)
        {
            SessionsEndedForTheft++;

            RevokeEvery(userId);
        }

        /// <summary>Revokes every record belonging to one account, in one step.</summary>
        /// <param name="userId">The account.</param>
        private void RevokeEvery(int userId)
        {
            foreach (StoredRefresh record in _byValue.Values)
            {
                if (record.UserId == userId)
                {
                    record.IsRevoked = true;
                }
            }
        }

        /// <summary>Revokes every generation of one family.</summary>
        /// <param name="familyId">The family.</param>
        private void RevokeFamily(int familyId)
        {
            foreach (StoredRefresh record in _byValue.Values)
            {
                if (record.FamilyId == familyId)
                {
                    record.IsRevoked = true;
                }
            }
        }
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
