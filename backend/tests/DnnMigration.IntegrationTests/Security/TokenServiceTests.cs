using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Exercises the REGISTERED token service: what it issues, what it rotates, what it revokes, and which
/// failure it reports when an exchange cannot proceed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this suite exists at all.</strong> These semantics used to be asserted in
/// backend/tests/DnnMigration.UnitTests/Security/JwtTokenServiceTests.cs against a token service that file
/// declared for itself. Thirty-odd tests specified a double and then asserted the double met the
/// specification, which is a tautology: every one of them would have stayed green through any regression in
/// the shipped implementation. They were deleted and re-expressed here, against the instance the container
/// resolves, over a real signature and the real refresh store. What remains in the unit project is the
/// contract's declared SHAPE, which is public application surface and genuinely belongs there.
/// </para>
/// <para>
/// <strong>How the implementation is reached.</strong> It is internal sealed to the infrastructure assembly
/// and no project is granted visibility into it. <see cref="ITokenService"/> is public application surface
/// and <c>AddInfrastructure</c> registers the implementation behind it, so the service is resolved exactly
/// as the sign-in endpoint resolves it. Nothing is substituted, so an assertion here is evidence about the
/// composed application rather than about a stand-in.
/// </para>
/// <para>
/// <strong>What is deliberately elsewhere.</strong> The endpoint behaviour of sign-in, rotation and sign-out
/// - status codes, problem documents, cookie-free transport, rate limiting - belongs to Api/AuthApiTests.cs.
/// The store's own retention and trimming bookkeeping belongs to RefreshTokenStoreTests.cs. The lapse of a
/// refresh chain past its absolute deadline is not asserted here because it needs the clock moved, which the
/// contract deliberately offers no way to do; the store's bound is asserted directly in its own suite. Every
/// test below uses account identifiers of its own, because the refresh store is a singleton shared with the
/// rest of the collection.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class TokenServiceTests : IDisposable
{
    /// <summary>Failure code reported when no record matches the presented refresh token.</summary>
    private const string NotFoundCode = "REFRESH_TOKEN_NOTFOUND";

    /// <summary>Failure code reported when the presented refresh token was already exchanged.</summary>
    private const string AlreadyUsedCode = "REFRESH_TOKEN_ALREADYUSED";

    /// <summary>Failure code reported when the presented refresh token was explicitly revoked.</summary>
    private const string RevokedCode = "REFRESH_TOKEN_REVOKED";

    /// <summary>The client fingerprint the legitimate holder presents on every exchange below.</summary>
    /// <remarks>
    /// <para>
    /// <strong>MIGRATION:</strong> rotation takes a bounded server-observed client fingerprint alongside the
    /// presented value. It exists to separate two things that look identical in the store - a client that
    /// retried a request whose response it never saw, and a second party spending a value it should not hold
    /// - so every exchange here states which of the two it is rather than leaving it to chance.
    /// </para>
    /// <para>
    /// The value is opaque to the contract: it is hashed before it is stored and only ever compared for
    /// equality, so a readable literal is safe and makes the intent of each exchange legible at the call site.
    /// </para>
    /// </remarks>
    private const string HolderBinding = "holder-fingerprint-a";

    /// <summary>A fingerprint belonging to somebody other than the holder of the presented value.</summary>
    /// <remarks>
    /// Used only where a REPLAY is the behaviour under test. Presenting a spent value from a different
    /// fingerprint is unambiguously the theft signal, whereas presenting it from the holder's own fingerprint
    /// within the near-simultaneous window is a retry and is answered without ending the session - a
    /// distinction asserted directly in
    /// <see cref="RefreshAsync_WhenTheHolderRetriesImmediately_RefusesWithoutEndingTheSession"/>. A replay
    /// test that reused <see cref="HolderBinding"/> could be satisfied by the retry path and would stop being
    /// evidence about theft detection.
    /// </remarks>
    private const string OtherHolderBinding = "holder-fingerprint-b";

    /// <summary>The role claim type a writer might reach for instead of the fully qualified name.</summary>
    /// <remarks>
    /// Named here so the absence assertion below reads as a deliberate check of both spellings rather than
    /// as a stray literal. Neither is written; see
    /// <see cref="IssueTokensAsync_WritesNoRoleOrPermissionClaim"/>.
    /// </remarks>
    private const string ShortRoleClaimType = "role";

    /// <summary>A permission claim type the reconciled contract deliberately does not write.</summary>
    private const string WithdrawnPermissionClaimType = "permission";

    /// <summary>A sign-in name claim type the reconciled contract deliberately does not write.</summary>
    private const string WithdrawnUniqueNameClaimType = "unique_name";

    /// <summary>A super-user claim type the reconciled contract deliberately does not write.</summary>
    private const string WithdrawnSuperUserClaimType = "is_superuser";

    /// <summary>The registered JWT envelope members, which are not part of the application claim set.</summary>
    /// <remarks>
    /// Subtracted before the minted set is compared, so the comparison stays an assertion about what THIS
    /// service chose to write rather than about what the token library happens to place in the envelope.
    /// </remarks>
    private static readonly HashSet<string> RegisteredEnvelopeClaimTypes = new(StringComparer.Ordinal)
    {
        "iss",
        "aud",
        "exp",
        "nbf",
        "iat",
    };
    /// <summary>The tenant every issuance below is scoped to: the seeded portal's own identifier.</summary>
    /// <remarks>
    /// Read from the seed rather than written as a literal, because <c>Portals.PortalID</c> is
    /// <c>IDENTITY(-1, 1)</c> and the first portal therefore carries the very value the legacy sentinel
    /// table used for "absent". A test that hard-coded a tenant would either miss that or assert it by
    /// accident.
    /// </remarks>
    private readonly ApiTestFixture _fixture;

    /// <summary>The request scope every resolution below is performed in.</summary>
    /// <remarks>
    /// <para>
    /// <strong>MIGRATION:</strong> the registered token service is SCOPED, because the refresh store it
    /// writes through is scoped to a request's own database connection. Resolving it from the root provider
    /// is therefore not merely untidy - the container refuses it outright - and a suite that reached for the
    /// root would be reporting a composition error as a test failure on every single fact.
    /// </para>
    /// <para>
    /// One scope per test instance rather than one per resolution, because xUnit constructs a fresh instance
    /// for every fact: each fact gets its own scope, and every resolution inside a fact returns the same
    /// instance the way a request would. The refresh store's own persistence outlives the scope, which is why
    /// each fact below uses account identifiers of its own.
    /// </para>
    /// </remarks>
    private readonly IServiceScope _scope;

    /// <summary>Initialises a new instance of the <see cref="TokenServiceTests"/> class.</summary>
    /// <param name="fixture">The shared composed host, which is the composition root used here.</param>
    public TokenServiceTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
        _scope = fixture.CreateScope();
    }

    /// <summary>Closes the request scope the resolutions were performed in.</summary>
    public void Dispose() => _scope.Dispose();

    /// <summary>
    /// The service is registered, resolvable, and implemented inside the infrastructure layer.
    /// </summary>
    /// <remarks>
    /// The assertion a direct construction cannot make, and the one that makes every other test in this
    /// file evidence about production: a service dropped from <c>AddInfrastructure</c> or registered against
    /// a different contract would leave a sign-in endpoint unable to mint anything, while a suite that
    /// constructed its own implementation carried on passing.
    /// </remarks>
    [Fact]
    public void Contract_IsRegisteredAndImplementedByTheInfrastructureLayer()
    {
        ITokenService service = Tokens();

        Type implementation = service.GetType();

        implementation.Assembly.Should().BeSameAs(
            typeof(DnnMigration.Infrastructure.DependencyInjection).Assembly,
            "signing belongs to the infrastructure layer, which is the only layer holding the key");
        implementation.IsSealed.Should().BeTrue(
            "a token service that can be subclassed can have its validation overridden");
        implementation.IsPublic.Should().BeFalse(
            "the implementation is internal so that the contract is the only way to mint a token");
    }

    /// <summary>
    /// Issuance writes the subject, the tenant and a token identifier - and nothing mutable.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Every fact the emitted token asserts arrives as an argument, which is what keeps issuance free of
    /// repository access. Both arguments - the account the sign-in resolved and the tenant it was resolved
    /// against - are written verbatim: trimming, casing and canonicalisation belong to the sign-in service
    /// that read the row, and repeating them here would make the token disagree with the read that produced
    /// it. The values are therefore compared for exact equality rather than case-insensitively.
    /// </para>
    /// <para>
    /// <strong>MIGRATION:</strong> the reconciled contract mints a MINIMAL claim set - <c>sub</c>,
    /// <c>portal_id</c> and <c>jti</c> over the registered <c>iss</c>/<c>aud</c>/<c>nbf</c>/<c>exp</c>/
    /// <c>iat</c> envelope - and the absence of everything else is an assertion here rather than an omission
    /// in the test. A sign-in name, a host flag, a role or a permission key would each be a snapshot of
    /// mutable state that stayed authoritative for the whole token lifetime after the row behind it changed:
    /// a demoted account would keep its authority and a renamed one would keep its old name in every audit
    /// line. Every server-side guard re-reads those facts from authoritative storage per request, so the
    /// minted set is pinned exactly and a well-meaning re-addition fails here first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_WritesTheSubjectAndTenantAndNothingMutable()
    {
        const int AccountId = 90_001;

        LoginResponse issued = Succeeded(await Tokens().IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        JwtSecurityToken token = Read(issued.AccessToken);

        Claim(token, DnnClaimTypes.Subject).Should().Be(
            AccountId.ToString(CultureInfo.InvariantCulture),
            "the subject is the account identifier the sign-in resolved, unaltered");
        Claim(token, DnnClaimTypes.PortalId).Should().Be(
            _fixture.Seed.PortalId.ToString(CultureInfo.InvariantCulture),
            "every request is evaluated against the tenant the token was issued for");
        Claim(token, DnnClaimTypes.JwtId).Should().NotBeNullOrWhiteSpace(
            "the identifier is what makes an issued token auditable after the fact");

        token.Claims
            .Select(claim => claim.Type)
            .Where(type => !RegisteredEnvelopeClaimTypes.Contains(type))
            .Distinct()
            .Should()
            .BeEquivalentTo(
                new[] { DnnClaimTypes.Subject, DnnClaimTypes.PortalId, DnnClaimTypes.JwtId },
                "three application claims, and no snapshot of a name, a host flag, a role or a permission");
    }

    /// <summary>
    /// The identifiers this schema really issues are accepted, including the two that look like absences.
    /// </summary>
    /// <param name="portalId">The tenant to issue for.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so -1 is the first tenant and simultaneously the
    /// legacy integer sentinel for "absent"; <c>Roles.RoleID</c> and <c>Tabs.TabID</c> are
    /// <c>IDENTITY(0, 1)</c>, so zero is an ordinary key. A guard that treated either as missing would
    /// refuse to issue a token for the first tenant of an installation.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task IssueTokensAsync_AcceptsTheIdentifiersTheSchemaSeeds(int portalId)
    {
        const int AccountId = 90_002;

        LoginResponse issued = Succeeded(await Tokens().IssueTokensAsync(
            AccountId,
            portalId));

        Claim(Read(issued.AccessToken), DnnClaimTypes.PortalId).Should().Be(
            portalId.ToString(CultureInfo.InvariantCulture),
            "neither -1 nor 0 means absent in this schema, and a token must carry the value it was given");
    }

    /// <summary>
    /// No role and no permission claim is written: entitlement is read from storage, never from a token.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// <strong>MIGRATION:</strong> the reconciled contract writes neither, and the absence is the assertion.
    /// An entitlement claim is a snapshot: a role revoked, a permission withdrawn or an account locked out
    /// one second after issuance would keep taking effect until the access token lapsed, and lengthening the
    /// token lifetime would lengthen that window. Every policy therefore re-reads roles and permission
    /// grants from authoritative storage on the request that needs them, so a claim-shaped shortcut could
    /// only ever be a second, staler answer to a question already answered correctly.
    /// </para>
    /// <para>
    /// The hazards the withdrawn shortcut had to navigate are recorded here because they are the reason the
    /// absence is worth pinning rather than merely tolerating. Had entitlement travelled on the token, it
    /// would have had to be one claim per entry rather than a delimited list: role-based authorisation
    /// matches a single claim value, so a comma-joined list matches no role at all while looking populated
    /// in a debugger, and a role name containing the separator silently becomes two entitlements. The claim
    /// TYPE would have mattered equally, because the bearer handler runs with inbound claim mapping switched
    /// off and a value written under any other type is invisible to every role policy. Carrying no
    /// entitlement removes both hazards outright, which is why both spellings are checked below.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_WritesNoRoleOrPermissionClaim()
    {
        const int AccountId = 90_003;

        LoginResponse issued = Succeeded(await Tokens().IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        JwtSecurityToken token = Read(issued.AccessToken);

        Claims(token, ClaimTypes.Role).Should().BeEmpty(
            "policy evaluation reads roles from storage, so a role snapshot on the token could only be a "
            + "staler second answer");
        Claims(token, ShortRoleClaimType).Should().BeEmpty(
            "the short spelling is checked too, because inbound claim mapping is switched off and a writer "
            + "could reach for either name");
        Claims(token, WithdrawnPermissionClaimType).Should().BeEmpty(
            "a permission grant is re-read per request for the same reason");
        Claims(token, WithdrawnUniqueNameClaimType).Should().BeEmpty(
            "the sign-in name is mutable, and an audit line reading it off a token would report the name the "
            + "account held when it signed in rather than the one it holds now");
        Claims(token, WithdrawnSuperUserClaimType).Should().BeEmpty(
            "installation-wide authority is re-read from the account row, never trusted from a token");
    }

    /// <summary>
    /// The token is signed with the configured key, for the configured issuer and audience, and validates.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the assertion no reimplementation could make, and the reason this suite exists: the token is
    /// validated with the same handler the request pipeline uses, against the key, issuer and audience the
    /// host is configured with. A token signed with the wrong key, or minted for an issuer the pipeline does
    /// not accept, would be refused on every request while looking perfectly well formed.
    /// </para>
    /// <para>
    /// The algorithm is asserted explicitly. A token presented with <c>alg: none</c>, or signed with an
    /// algorithm the validator does not expect, is the classic bearer-token forgery, so the emitted header
    /// is pinned rather than trusted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_SignsATokenTheConfiguredValidationAccepts()
    {
        const int AccountId = 90_004;

        LoginResponse issued = Succeeded(await Tokens().IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        JwtOptions options = Options();

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(options.Secret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };

        Action validate = () => handler.ValidateToken(issued.AccessToken, parameters, out _);

        validate.Should().NotThrow(
            "the token the service issues has to be the token the request pipeline accepts, and the "
            + "pipeline validates the signature, the issuer, the audience and the lifetime");

        Read(issued.AccessToken).Header.Alg.Should().Be(
            SecurityAlgorithms.HmacSha256,
            "the signing algorithm is pinned, because a token presented under an unexpected algorithm is "
            + "how a forged bearer token gets accepted");
    }

    /// <summary>
    /// The access token lapses exactly one configured lifetime after it was minted.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The relationship is asserted rather than the wall-clock instant: the difference between the token's
    /// own issued-at and expiry claims is the configured lifetime, whatever the clock said. That also pins
    /// the two halves together - a reported expiry that disagreed with the token's own claim would leave a
    /// client refreshing at the wrong moment, and the reported value is what the client acts on.
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_ExpiresOneConfiguredLifetimeAfterIssuance()
    {
        const int AccountId = 90_005;

        LoginResponse issued = Succeeded(await Tokens().IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        JwtSecurityToken token = Read(issued.AccessToken);
        TimeSpan configured = TimeSpan.FromMinutes(Options().ExpirationMinutes);

        (token.ValidTo - token.ValidFrom).Should().Be(
            configured,
            "the lifetime is a configured span applied to one reading of the clock, not a value computed "
            + "twice from a moving one");
        issued.ExpiresAtUtc.Should().BeCloseTo(
            token.ValidTo,
            TimeSpan.FromSeconds(1),
            "the reported expiry is what a client refreshes against, so it has to be the token's own");
        issued.ExpiresAtUtc.Kind.Should().Be(
            DateTimeKind.Utc,
            "an instant a client compares against its own clock must state the zone it is in");
    }

    /// <summary>
    /// A freshly issued pair raises no advisory and carries no credential.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The three advisories are decided by the sign-in service from account state, not by issuance, so the
    /// pair this operation returns must leave them alone rather than guess at them. The absence of a
    /// credential on the projection is asserted with them because the same object is serialised straight to
    /// the caller: anything on it reaches the wire.
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_RaisesNoAdvisoryAndCarriesNoCredential()
    {
        const int AccountId = 90_006;

        LoginResponse issued = Succeeded(await Tokens().IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        issued.MustChangePassword.Should().BeFalse();
        issued.PasswordExpiring.Should().BeFalse();
        issued.MustUpdateProfile.Should().BeFalse();

        issued.User.Should().NotBeNull("the caller reads its own identity off the pair");
        issued.User.GetType().GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain(
                name => name.Contains("Password", StringComparison.OrdinalIgnoreCase),
                "the projection is serialised to the caller, so a credential-shaped member on it would "
                + "reach the wire");
    }

    /// <summary>
    /// Refresh values are unguessable, distinct per issuance, and encode nothing about the caller.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A refresh token is a bearer credential with a longer life than the access token it mints, so a value
    /// derived from the account, the tenant or a counter would be forgeable by anybody who could guess the
    /// inputs. Two proofs are taken, both cheap and both observable: the account identifier does not appear
    /// in the value, and two issuances for the SAME account and tenant share no prefix. The second matters
    /// because the reconciled contract hands issuance only those two identifiers - a value derived from them
    /// would necessarily repeat structure, and a counter appended to a derived stem would repeat a prefix.
    /// The sign-in name is no longer an input to issuance at all, so it can no longer be searched for.
    /// </remarks>
    [Fact]
    public async Task IssueTokensAsync_ProducesDistinctOpaqueRefreshValues()
    {
        const int AccountId = 90_007;

        ITokenService tokens = Tokens();

        LoginResponse first = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));
        LoginResponse second = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        first.RefreshToken.Should().NotBeNullOrWhiteSpace();
        second.RefreshToken.Should().NotBe(
            first.RefreshToken,
            "two sign-ins are two sessions, and sharing a refresh value would make revoking one revoke "
            + "the other");
        first.RefreshToken.Should().NotContain(
            AccountId.ToString(CultureInfo.InvariantCulture),
            "a value that encodes the account is a value an attacker can construct");
        first.RefreshToken[..8].Should().NotBe(
            second.RefreshToken[..8],
            "two values derived from the same two identifiers would share structure; these share no prefix");
    }

    /// <summary>
    /// Rotation issues a new pair and the presented refresh token cannot be exchanged again.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Single use is the property the whole design rests on: it is what makes a stolen refresh token
    /// detectable, because the legitimate holder and the thief cannot both spend the same generation. Both
    /// halves are asserted - that the replacement pair really is new, and that the spent value is refused.
    /// The replay is presented from a DIFFERENT client fingerprint, because that is what makes the refusal
    /// attributable to single use: the holder's own fingerprint inside the near-simultaneous window is read
    /// as a retry, which is refused with the same code for a different reason.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_IssuesANewPairAndSpendsThePresentedToken()
    {
        const int AccountId = 90_008;

        ITokenService tokens = Tokens();

        LoginResponse issued = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        LoginResponse rotated = Succeeded(await tokens.RefreshAsync(issued.RefreshToken, HolderBinding));

        rotated.RefreshToken.Should().NotBe(
            issued.RefreshToken,
            "an exchange that returned the same value would leave the presented token live");
        rotated.AccessToken.Should().NotBeNullOrWhiteSpace();

        JwtSecurityToken minted = Read(rotated.AccessToken);

        Claim(minted, DnnClaimTypes.Subject).Should().Be(
            AccountId.ToString(CultureInfo.InvariantCulture),
            "the replacement is minted from the record's own subject, not from anything the caller sent");
        Claim(minted, DnnClaimTypes.PortalId).Should().Be(
            _fixture.Seed.PortalId.ToString(CultureInfo.InvariantCulture),
            "and from the record's own tenant, so a rotation cannot cross a tenant boundary");

        Result<LoginResponse> replay = await tokens.RefreshAsync(issued.RefreshToken, OtherHolderBinding);

        replay.IsSuccess.Should().BeFalse("a refresh token is single use");
        replay.Error!.Code.Should().Be(
            AlreadyUsedCode,
            "the caller is told the value was already spent rather than merely that it failed");
    }

    /// <summary>
    /// A replay ends every session the account holds, not merely the generation presented.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A presented-and-already-spent value is evidence that two parties hold the same refresh token, which
    /// means one of them stole it. Because the store cannot tell which, the only safe response is to end
    /// every session the account holds and require a fresh sign-in - so a second, entirely separate session
    /// is opened here and asserted to be collateral damage, deliberately.
    /// The replay arrives from a different client fingerprint, which is the signal the store reads as theft;
    /// the holder retrying its own exchange is a separate case and does not end anything.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_OnReplay_EndsEverySessionTheAccountHolds()
    {
        const int AccountId = 90_009;

        ITokenService tokens = Tokens();

        LoginResponse spent = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));
        LoginResponse bystander = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        _ = Succeeded(await tokens.RefreshAsync(spent.RefreshToken, HolderBinding));

        Result<LoginResponse> replay = await tokens.RefreshAsync(spent.RefreshToken, OtherHolderBinding);

        replay.IsSuccess.Should().BeFalse();
        replay.Error!.Code.Should().Be(AlreadyUsedCode);

        Result<LoginResponse> other = await tokens.RefreshAsync(bystander.RefreshToken, HolderBinding);

        other.IsSuccess.Should().BeFalse(
            "a replay is evidence of theft, and the store cannot tell which holder is the thief, so every "
            + "session the account holds ends");
        other.Error!.Code.Should().Be(
            RevokedCode,
            "the bystander session was revoked in response to the replay rather than spent by its holder");
    }

    /// <summary>
    /// The holder retrying its own exchange is refused without ending the session it already established.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// <strong>MIGRATION:</strong> the counterpart to the replay case above, and the reason rotation is
    /// handed a client fingerprint at all. A client whose response was lost - a dropped connection, a proxy
    /// timeout, a page reloaded mid-exchange - retries with the value it still holds, which the store has
    /// already consumed. Read purely as a spent value that would be theft, and the account would be signed
    /// out of everything by an ordinary network fault.
    /// </para>
    /// <para>
    /// Both halves matter and both are asserted. The retry is still REFUSED, because the presented generation
    /// really has been spent and handing back a second replacement would make the generation multi-use; and
    /// the session is left INTACT, which is what distinguishes the retry from the replay. The rotated pair the
    /// first exchange produced is exchanged afterwards to prove the family survived.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_WhenTheHolderRetriesImmediately_RefusesWithoutEndingTheSession()
    {
        const int AccountId = 90_016;

        ITokenService tokens = Tokens();

        LoginResponse issued = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        LoginResponse rotated = Succeeded(await tokens.RefreshAsync(issued.RefreshToken, HolderBinding));

        Result<LoginResponse> retry = await tokens.RefreshAsync(issued.RefreshToken, HolderBinding);

        retry.IsSuccess.Should().BeFalse(
            "the presented generation was spent, and a second replacement would make it multi-use");
        retry.Error!.Code.Should().Be(
            AlreadyUsedCode,
            "the caller is told the value was already exchanged, which is what its retry logic needs to hear");

        Result<LoginResponse> continued = await tokens.RefreshAsync(rotated.RefreshToken, HolderBinding);

        continued.IsSuccess.Should().BeTrue(
            "a retry from the holder's own fingerprint is a network fault, not theft, so the family it "
            + "belongs to must survive it - otherwise a dropped response signs the account out of everything");
    }
    /// <summary>
    /// An unknown refresh value is refused as unknown, and the refusal names no token material.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The failure code has to distinguish an unknown value from a spent, revoked or lapsed one, because the
    /// caller's remedy differs. The message is asserted to contain neither the presented value nor any part
    /// of it, since a refusal is exactly what gets logged and quoted in a support ticket.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_ForAnUnknownValue_ReportsNotFoundWithoutEchoingIt()
    {
        const string Presented = "unknown-refresh-value-6f2a1c";

        Result<LoginResponse> refused = await Tokens().RefreshAsync(Presented, HolderBinding);

        refused.IsSuccess.Should().BeFalse();
        refused.Error!.Code.Should().Be(NotFoundCode);
        refused.Error.Message.Should().NotContain(
            Presented,
            "a refusal is logged and quoted, so it must not carry the credential that was presented");
    }

    /// <summary>
    /// Revoking one refresh token stops that session and reports the revocation on the next exchange.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is what sign-out performs, and its reach is the point: it ends the session it was given and
    /// reports a revoked value distinguishably, so a client can tell "you signed out" from "your token
    /// expired". A revoked value must not be reported as unknown, or a signed-out client would look like a
    /// forger.
    /// </remarks>
    [Fact]
    public async Task RevokeRefreshTokenAsync_StopsFurtherExchangeOfThatValue()
    {
        const int AccountId = 90_010;

        ITokenService tokens = Tokens();

        LoginResponse issued = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        Result revoked = await tokens.RevokeRefreshTokenAsync(issued.RefreshToken);

        revoked.IsSuccess.Should().BeTrue("revocation is the sign-out operation and must not fail");

        Result<LoginResponse> exchange = await tokens.RefreshAsync(issued.RefreshToken, HolderBinding);

        exchange.IsSuccess.Should().BeFalse();
        exchange.Error!.Code.Should().Be(
            RevokedCode,
            "a revoked value is distinguishable from an unknown one, so a signed-out client is not "
            + "reported as presenting something forged");
    }

    /// <summary>
    /// Revocation is idempotent for a family the store holds, and UNCONFIRMED for a value it does not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// SEC-F2. The first two assertions are the idempotence sign-out depends on: repeating it asks for a
    /// state that already holds, so it succeeds. The third one changed, and the change is the point. A value
    /// this store never issued used to be reported as a completed sign-out on the reading that nothing was
    /// left exchangeable - but a PROCESS-LOCAL store cannot know that. Presented with a session established
    /// on another replica it answers exactly as it does here, and reporting success left that session live
    /// while the client dropped the only credential that could have retried. The permissive reading is now
    /// earned by a store that is authoritative across replicas, and withheld from one that is not.
    /// </remarks>
    [Fact]
    public async Task RevokeRefreshTokenAsync_IsIdempotentForAHeldFamilyAndUnconfirmedOtherwise()
    {
        const int AccountId = 90_011;

        ITokenService tokens = Tokens();

        LoginResponse issued = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        (await tokens.RevokeRefreshTokenAsync(issued.RefreshToken)).IsSuccess.Should().BeTrue();
        (await tokens.RevokeRefreshTokenAsync(issued.RefreshToken)).IsSuccess.Should().BeTrue(
            "a repeated sign-out asks for a state that already holds");
        (await tokens.RevokeRefreshTokenAsync("never-issued-value-4c1b")).IsFailure.Should().BeTrue(
            "a store that sees only its own process has not proved that a value it does not hold is "
            + "unexchangeable anywhere, so the retirement is reported as unconfirmed and the caller keeps "
            + "its credential");
    }

    /// <summary>
    /// Revoking every token for an account ends all of its sessions, and succeeds when it holds none.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The reach is deliberately wider than sign-out: this is what an administrative lock-out and the
    /// theft response both use, so it has to end sessions the caller never named. It is asserted across two
    /// independently issued families, because a single-family implementation would pass a one-session test.
    /// </remarks>
    [Fact]
    public async Task RevokeAllRefreshTokensAsync_EndsEveryFamilyTheAccountHolds()
    {
        const int AccountId = 90_012;
        const int Untouched = 90_013;

        ITokenService tokens = Tokens();

        LoginResponse first = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));
        LoginResponse second = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));
        LoginResponse other = Succeeded(await tokens.IssueTokensAsync(
            Untouched,
            _fixture.Seed.PortalId));

        Result revoked = await tokens.RevokeAllRefreshTokensAsync(AccountId);

        revoked.IsSuccess.Should().BeTrue();

        (await tokens.RefreshAsync(first.RefreshToken, HolderBinding)).Error!.Code.Should().Be(RevokedCode);
        (await tokens.RefreshAsync(second.RefreshToken, HolderBinding)).Error!.Code.Should().Be(
            RevokedCode,
            "every family the account holds ends, not merely the most recent one");

        (await tokens.RefreshAsync(other.RefreshToken, HolderBinding)).IsSuccess.Should().BeTrue(
            "another account's session is not collateral damage");

        // SEC-F2. An account this store holds nothing for is reported as UNCONFIRMED rather than as a
        // completed retirement, because a process-local store has established only its own ignorance. The
        // cascading callers - account deletion and tenant removal - tell that apart from an unanswerable
        // store themselves and proceed, which is asserted by their own suites.
        (await tokens.RevokeAllRefreshTokensAsync(90_014)).IsFailure.Should().BeTrue(
            "a store that sees only its own process cannot prove an account holds no session elsewhere");
    }

    /// <summary>
    /// An access token already handed out keeps working after the session it came from is revoked.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the documented reduction of a stateless bearer scheme, asserted so that nobody later reads
    /// sign-out as retraction. An access token is validated by its signature and its lifetime and is
    /// consulted against no store, so revocation stops the session CONTINUING - no further access token can
    /// be minted - and cannot recall the one already issued. The mitigation is the short lifetime asserted
    /// above, and the legacy cookie scheme it replaces had exactly the same property.
    /// </remarks>
    [Fact]
    public async Task Revocation_LeavesAnAlreadyIssuedAccessTokenValid()
    {
        const int AccountId = 90_015;

        ITokenService tokens = Tokens();

        LoginResponse issued = Succeeded(await tokens.IssueTokensAsync(
            AccountId,
            _fixture.Seed.PortalId));

        (await tokens.RevokeAllRefreshTokensAsync(AccountId)).IsSuccess.Should().BeTrue();

        JwtOptions options = Options();

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(options.Secret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };

        JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };

        Action validate = () => handler.ValidateToken(issued.AccessToken, parameters, out _);

        validate.Should().NotThrow(
            "an access token is validated by signature and lifetime alone, so revocation stops the session "
            + "continuing and cannot retract what was already issued - which is why the lifetime is short");
    }

    /// <summary>The registered token service.</summary>
    /// <returns>The service the composed host resolves for the contract.</returns>
    private ITokenService Tokens() => _scope.ServiceProvider.GetRequiredService<ITokenService>();

    /// <summary>The bound token options the host is running with.</summary>
    /// <returns>The options.</returns>
    /// <remarks>
    /// Read from the container rather than restated, so that an assertion about a lifetime, an issuer or an
    /// audience is an assertion about what this host is configured with rather than about a literal that
    /// happens to agree with it.
    /// </remarks>
    private JwtOptions Options() =>
        _scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;

    /// <summary>Reads a compact serialised token without validating it.</summary>
    /// <param name="accessToken">The compact serialisation.</param>
    /// <returns>The parsed token.</returns>
    private static JwtSecurityToken Read(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

    /// <summary>Reads the single value a token carries for one claim type.</summary>
    /// <param name="token">The parsed token.</param>
    /// <param name="type">The claim type to read.</param>
    /// <returns>The value, or <see langword="null"/> when the claim is absent.</returns>
    private static string? Claim(JwtSecurityToken token, string type) =>
        token.Claims.SingleOrDefault(claim => claim.Type == type)?.Value;

    /// <summary>Reads every value a token carries for one claim type.</summary>
    /// <param name="token">The parsed token.</param>
    /// <param name="type">The claim type to read.</param>
    /// <returns>The values, in the order the token carries them.</returns>
    private static IReadOnlyList<string> Claims(JwtSecurityToken token, string type) =>
        [.. token.Claims.Where(claim => claim.Type == type).Select(claim => claim.Value)];

    /// <summary>Unwraps a successful outcome, failing the test when the operation reported a failure.</summary>
    /// <typeparam name="TValue">The payload type.</typeparam>
    /// <param name="result">The outcome.</param>
    /// <returns>The payload.</returns>
    /// <remarks>
    /// The contract reports expected failures as outcomes rather than exceptions, so a test that read the
    /// value without checking would assert against a default and report a misleading difference instead of
    /// the failure the service actually returned.
    /// </remarks>
    private static TValue Succeeded<TValue>(Result<TValue> result)
    {
        result.IsSuccess.Should().BeTrue(result.Error?.ToString());
        return result.Value!;
    }
}
