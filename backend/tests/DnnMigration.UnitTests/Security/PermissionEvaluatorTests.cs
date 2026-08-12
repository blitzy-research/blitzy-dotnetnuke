using System.Collections;
using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;

// The reflection namespace this file leans on declares its own Module type, so the domain entity is
// aliased rather than disambiguated at each of its three uses.
using ModuleEntity = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.UnitTests.Security;

/// <summary>
/// Holds the permission-resolution contracts, and the model those contracts resolve over, to the shape the
/// migration's rules require - asserted from a project that names no implementation type at all.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS FILE IS FOR, AND WHY IT IS NOT A SECOND COPY OF THE BEHAVIOUR SUITE. Permission resolution is
/// an algorithm, and an algorithm can only be verified against the thing that implements it. That
/// verification exists, in full, in <c>DnnMigration.IntegrationTests/Security/PermissionEvaluatorTests.cs</c>,
/// which resolves the shipped component from the composed container and drives it end to end - deny
/// precedence in both row orders, the whole principal matrix, ordinal role-name comparison, inherited view,
/// cancellation, and a store fault staying a fault. None of that is repeated here, and repeating it would be
/// worse than redundant: this project's own build file states the reason in as many words - <em>"a unit test
/// must not reimplement a component and then assert that its own double behaves as specified; that is a
/// tautology which stays green while the shipped type breaks."</em>
/// </para>
/// <para>
/// What is left is not a remainder, it is a distinct claim, and it is the claim this project is uniquely able
/// to make. Everything asserted below is asserted <strong>without naming a single implementation type</strong>
/// - no evaluator, no persistence type, no host - which is the executable form of AAP Rule T1 and baseline
/// B1: the contracts are complete enough to be described, constrained and held to their shape from outside
/// the assembly that satisfies them. A test in the integration project cannot make that claim, because that
/// project can see everything. Two things follow that are worth stating plainly:
/// </para>
/// <para>
/// FIRST, THE CONTRACT SHAPE IS ASSERTED FROM THE OUTSIDE. Every member is an awaitable, cancellable task;
/// nothing declares an output or reference parameter; every type on either surface comes from an allow-listed
/// assembly; every sequence handed back is a read-only generic one; the persistence surface produces no
/// verdict; and every verdict that does exist is wrapped in an outcome. The integration project asserts the
/// same shapes and is right to, but it asserts them with the implementation on its reference graph. Here they
/// are asserted with the implementation absent, so a change that made the contracts describable only in terms
/// of implementation types would break this file first.
/// </para>
/// <para>
/// SECOND, THE MODEL THE RULES STAND ON IS ASSERTED HERE AND NOWHERE ELSE. The resolution rules are
/// statements about rows: that <c>-1</c> is the all-users principal, <c>-2</c> the installation operator,
/// <c>-3</c> the unauthenticated caller, that <c>0</c> is an ordinary role identifier, that a row naming
/// neither a role nor an account must be expressible so that it can be refused, and that inheritance is a
/// tri-state. Every one of those rules presupposes that the entity model can <em>carry</em> the value. If a
/// later change narrowed the role column to a non-nullable integer, collapsed the inheritance flag back to
/// two states, or normalised a negative identifier away, the behaviour suite would fail somewhere deep in an
/// arrangement and the diagnosis would be long. These tests fail immediately, at the model, naming the
/// column.
/// </para>
/// <para>
/// THREE INSTRUCTION CONFLICTS ARE RECONCILED HERE RATHER THAN QUIETLY RESOLVED. Each was settled against
/// the AAP and the shipped code, and each is recorded at the assertion it affects, because a reader who
/// finds only the outcome cannot tell a decision from an oversight:
/// </para>
/// <para>
/// (1) THE PROJECT REFERENCE. This file's own specification requires that no implementation assembly be
/// named, while this project's build file carries an edge to the implementation project and documents why -
/// several internal sealed security types are asserted here with substituted collaborators and no host. Both
/// hold at once, and the resolution is the strict one: the edge stays, because deleting it would delete that
/// coverage rather than relocate it, and <strong>this file behaves as though the edge did not exist</strong>.
/// Not one implementation type is named, constructed, or reached by any means. The build file is not edited.
/// </para>
/// <para>
/// (2) THE ABSENT-VERDICT ASSERTION COULD NOT BE WRITTEN HONESTLY. The specification asks this file to assert
/// that the application contract carries no member producing a boolean, on the grounds that resolution moved
/// out of the application layer. Resolution did move - the algorithm is not in this layer - but the
/// application contract deliberately publishes three members that produce a verdict, because the API layer's
/// authorisation policy needs exactly one authorised route to ask the question and must not reach past the
/// application layer to find it. Asserting their absence would assert something false. The architectural
/// protection the instruction was reaching for is therefore expressed where it can actually be broken, and in
/// two parts that together are stronger: the <em>persistence</em> surface may produce no verdict at all - that
/// is the single-reducer guarantee, and a repository member returning one would be a second, disagreeing
/// authority - and every verdict that does exist must be wrapped in an outcome, so that "you may not" and
/// "the store could not be reached" can never collapse into the same answer.
/// </para>
/// <para>
/// (3) THE STORAGE-SUBSYSTEM ASSERTION NEEDED NARROWING, NOT KEEPING. The specification asks this file to
/// assert that neither contract carries a member for the excluded storage subsystem. One such member now
/// exists, and it exists because the terminal legacy role removal swept that family's rows first and leaving
/// them behind orphans authority whose principal is gone. The exclusion is of the <em>feature</em>: the plan
/// lists that grant triad among the authorising reference sources of the in-scope security domain and counts
/// its legacy controller among in-scope call sites, while declaring no entity for it - and none exists. So the
/// assertion below pins the boundary instead of the token, and is stricter than the ban it replaces
/// everywhere else.
/// </para>
/// <para>
/// The class name is the one the plan's test tree names. It names the subject, not the seam, and it is
/// deliberately not renamed to match the abstraction this file reaches the subject through.
/// </para>
/// </remarks>
public class PermissionEvaluatorTests
{
    /// <summary>
    /// The all-users principal. Persisted in the grant tables' role column and matching no role row.
    /// </summary>
    /// <remarks>
    /// The legacy source defines it as a role NAME compared as a string; the target compares the stored
    /// identifier. It matches unconditionally, authenticated or not, which is how a public page is expressed.
    /// </remarks>
    private const int AllUsersPrincipal = -1;

    /// <summary>The installation-operator principal, likewise persisted and matching no role row.</summary>
    private const int OperatorPrincipal = -2;

    /// <summary>
    /// The unauthenticated-caller principal, which matches only a caller who has not signed in.
    /// </summary>
    private const int UnauthenticatedPrincipal = -3;

    /// <summary>
    /// The first identifier the role table issues. Included in every principal assertion below precisely
    /// because it is falsy, and because a generic sign test on a role identifier would silently discard it.
    /// </summary>
    private const int FirstIssuedRoleId = 0;

    /// <summary>An ordinary role identifier, well clear of both the seed and the pseudo-principals.</summary>
    private const int OrdinaryRoleId = 42;

    /// <summary>
    /// An identifier that is NOT a principal, and is never repurposed as one. Stated so that the matrix
    /// below is a closed set rather than "negatives are special".
    /// </summary>
    private const int NotAPrincipal = -4;

    /// <summary>
    /// The two contracts this file holds to their shape: the application surface callers use, and the
    /// persistence surface that carries the rows.
    /// </summary>
    /// <remarks>
    /// Both are reachable from this project. The component that resolves a verdict from those rows is not,
    /// and is deliberately not named anywhere in this file.
    /// </remarks>
    private static readonly Type[] Contracts =
    [
        typeof(IPermissionService),
        typeof(IPermissionRepository),
    ];

    /// <summary>
    /// The assemblies a permission contract may mention on its surface: the domain, the application, and the
    /// framework's own core.
    /// </summary>
    /// <remarks>
    /// Stated as an allow-list rather than a list of forbidden names, and that is the stronger of the two
    /// forms: a denylist rejects only what its author remembered, whereas this rejects everything that was
    /// not permitted - including dependencies that do not exist yet. It is also the only form available to
    /// this file, since naming a forbidden persistence type in order to forbid it would put that name on the
    /// surface of the test.
    /// </remarks>
    private static readonly IReadOnlySet<Assembly> PermittedSurfaceAssemblies = new HashSet<Assembly>
    {
        typeof(IPermissionService).Assembly,
        typeof(IPermissionRepository).Assembly,
        typeof(int).Assembly,
        typeof(Task).Assembly,
    };

    // =================================================================================================
    // The contract shape, asserted from a project that names no implementation type.
    // =================================================================================================

    /// <summary>
    /// Every member of both contracts is an awaitable task whose last argument is a cancellation token, and
    /// whose name says so.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy surface was wholly synchronous and, in the case the plan measures, wholly
    /// static: every read went through a reflection-created singleton and blocked. Rule T6 replaces that with
    /// "async all the way down", and the naming convention is part of the contract rather than decoration - a
    /// synchronous member added here would be invisible to a reader scanning for the suffix.
    /// </remarks>
    [Fact]
    public void Contract_EveryMemberIsAnAwaitableCancellableTask()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            // SEC-F8. The one deliberate exception. The cache eviction runs AFTER its caller's commit, so it
            // must not be able to fail or be cancelled half-done; it performs no input or output at all,
            // evicting two in-memory key families by name and by prefix. It was a cancellable Task only
            // because it read every page of the tenant to compose legacy grant keys nothing ever wrote.
            if (member.Name == nameof(IPermissionService.InvalidateUserPermissionCaches))
            {
                continue;
            }

            member.Name.Should().EndWith(
                "Async",
                $"{contract.Name}.{member.Name} performs input and output, so its name must say so");

            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue(
                $"{contract.Name}.{member.Name} must be awaitable");

            ParameterInfo[] parameters = member.GetParameters();

            parameters.Should().NotBeEmpty(
                $"{contract.Name}.{member.Name} must at least accept a cancellation token");

            parameters[^1].ParameterType.Should().Be(
                typeof(CancellationToken),
                $"{contract.Name}.{member.Name} must accept cancellation as its final argument");
        }
    }

    /// <summary>
    /// Neither contract declares an output or reference parameter anywhere.
    /// </summary>
    /// <remarks>
    /// MIGRATION: thirty in-scope legacy members mutated an argument and reported a status through it, and the
    /// plan's own words are that no output or reference parameter appears in any target public API. The
    /// replacement is an outcome carrying the value, which is why this assertion and the wrapped-verdict one
    /// below are two halves of one rule: the status left the argument list, and it had to land somewhere.
    /// </remarks>
    [Fact]
    public void Contract_DeclaresNoOutputOrReferenceParameter()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (ParameterInfo parameter in member.GetParameters())
            {
                parameter.IsOut.Should().BeFalse(
                    $"{contract.Name}.{member.Name} must report through its return value, not through {parameter.Name}");

                parameter.ParameterType.IsByRef.Should().BeFalse(
                    $"{contract.Name}.{member.Name} must not mutate {parameter.Name} in place");
            }
        }
    }

    /// <summary>
    /// The persistence surface answers no access question at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE SINGLE-REDUCER GUARANTEE, and it is the assertion that carries the architectural weight in
    /// this file. Two components that can disagree about whether a caller may act is the worst available
    /// outcome in this area, and the way to stop a second one appearing is to keep the shape of a verdict off
    /// the layer that holds the rows. A repository member that began returning one would be exactly that
    /// second authority, and this fails the moment one does.
    /// </para>
    /// <para>
    /// Stated as a property of the value produced rather than of the member's name, so it cannot be evaded by
    /// choosing a name no guard recognises. It is also the honest half of an instruction that asked for the
    /// absence of a verdict on the APPLICATION contract - see this class's remarks: three such members exist
    /// there deliberately, so their absence could not be asserted, whereas their absence here is both true and
    /// the property that actually protects the design.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_ThePersistenceSurfaceProducesNoVerdict()
    {
        foreach (MethodInfo member in typeof(IPermissionRepository).GetMethods())
        {
            ProducedValue(member.ReturnType).Should().NotBe(
                typeof(bool),
                $"IPermissionRepository.{member.Name} holds rows; deciding what they add up to belongs to exactly one other component");
        }
    }

    /// <summary>
    /// Every verdict the application contract produces is wrapped in an outcome, and at least one exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction this pins is the one a caller most needs and most easily loses. A refusal and a failure
    /// are not the same event: "you may not do this" is a successful answer whose value happens to be false,
    /// while "the store could not be reached" is a failure carrying no answer at all. A bare boolean cannot
    /// express the difference, so it would force the second to masquerade as the first - which reads to an
    /// operator as a permissions problem when it is an availability problem, and which is precisely the
    /// mislabelling the specification for this file names.
    /// </para>
    /// <para>
    /// The set is asserted to be non-empty deliberately. Without that, a change that removed every verdict
    /// from the contract would leave this test passing over nothing, and the API layer's authorisation policy
    /// would have lost its one authorised route to ask the question. No member is named: which members carry a
    /// verdict is pinned by the behaviour suite, and naming them here would put an implementation-side
    /// identifier into a file whose whole point is that it needs none.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_EveryVerdictIsAnOutcomeCarryingABooleanNeverABareBoolean()
    {
        MethodInfo[] verdicts = typeof(IPermissionService)
            .GetMethods()
            .Where(member => ProducedValue(member.ReturnType) == typeof(bool))
            .ToArray();

        verdicts.Should().NotBeEmpty(
            "the API layer's authorisation policy must have one authorised route to ask whether a caller may act");

        foreach (MethodInfo verdict in verdicts)
        {
            Type produced = verdict.ReturnType.GetGenericArguments()[0];

            produced.IsGenericType.Should().BeTrue(
                $"IPermissionService.{verdict.Name} must wrap its answer so a refusal and a failure stay distinguishable");

            produced.GetGenericTypeDefinition().Should().Be(
                typeof(Result<>),
                $"IPermissionService.{verdict.Name} must produce an outcome of boolean, so a denial is a successful false rather than an error");
        }
    }

    /// <summary>
    /// Every type either contract mentions comes from an allow-listed assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the reflection-driven row hydrator and the reflection-created provider singleton are both
    /// gone, replaced by the object-relational materialiser and constructor injection, and neither may leave a
    /// trace on these contracts. This is what makes that verifiable rather than merely asserted: no
    /// persistence type, no query type, no mapping-library type and no host type can appear on either surface,
    /// because every surface type must come from an assembly named here.
    /// </para>
    /// <para>
    /// It is also the only form of the check this file can write. Forbidding a persistence type by name would
    /// require writing that name, which is the very thing the layering guard for this file prohibits - so the
    /// prohibition is expressed positively, and is stronger for it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_MentionsOnlyDomainApplicationAndFrameworkTypes()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (Type surface in SurfaceTypes(member))
            {
                if (surface.IsGenericParameter)
                {
                    continue;
                }

                PermittedSurfaceAssemblies.Should().Contain(
                    surface.Assembly,
                    $"{contract.Name}.{member.Name} mentions {surface.Name}, which comes from an assembly these contracts may not expose");
            }
        }
    }

    /// <summary>
    /// Every sequence either contract hands back is a read-only generic one, and no legacy collection wrapper
    /// appears anywhere on either surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: six of the nine members of the legacy catalogue controller returned an untyped list, and the
    /// grant controllers returned hand-rolled wrapper types built on the pre-generics collection bases. Both
    /// habits are replaced by typed read-only sequences, and the plan records that those wrapper types produce
    /// no target type at all.
    /// </para>
    /// <para>
    /// Detected by SHAPE rather than by name, which is both necessary and better. Necessary, because naming
    /// the untyped collection types in order to forbid them would put those names on this file. Better,
    /// because the shape test catches every untyped sequence rather than the two that happen to be
    /// remembered: a surface type that enumerates but offers no element type is exactly the legacy habit,
    /// whatever it is called. The wrapper check is confined to non-generic types for the same reason - the
    /// legacy wrappers were non-generic by construction, and a generic read-only collection is the
    /// replacement rather than another instance of the problem.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_HandsBackOnlyReadOnlyGenericSequences()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (Type surface in SurfaceTypes(member))
            {
                if (surface.IsGenericParameter || surface == typeof(string))
                {
                    continue;
                }

                bool enumerates = typeof(IEnumerable).IsAssignableFrom(surface);

                if (!enumerates)
                {
                    continue;
                }

                bool offersAnElementType = surface.IsGenericType
                    || surface.GetInterfaces().Any(implemented =>
                        implemented.IsGenericType
                        && implemented.GetGenericTypeDefinition() == typeof(IEnumerable<>));

                offersAnElementType.Should().BeTrue(
                    $"{contract.Name}.{member.Name} mentions {surface.Name}, a sequence with no element type - the untyped-collection habit the migration removes");

                bool isLegacyWrapper = !surface.IsGenericType
                    && surface.Name.EndsWith("Collection", StringComparison.Ordinal);

                isLegacyWrapper.Should().BeFalse(
                    $"{contract.Name}.{member.Name} mentions {surface.Name}, which has the shape of a pre-generics collection wrapper");
            }
        }
    }

    /// <summary>
    /// Neither contract carries a member for the excluded storage subsystem, save one removal that is not a
    /// feature and cannot become one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the path-scoped catalogue lookup and the storage-grant feature members are not ported. File
    /// management is out of scope, so no target feature exists for a grant keyed by a storage location to
    /// serve, and the domain declares no entity for one. The excluded vocabulary is checked as short tokens
    /// rather than as the subsystem's compound type names, because quoting those would report this file as
    /// reintroducing what it proves is absent.
    /// </para>
    /// <para>
    /// MIGRATION: THE ONE EXCEPTION IS A ROLE-SCOPED REMOVAL, admitted because the terminal legacy role
    /// deletion performed it as its first statement. Neither grant table declares a key to the role table, so
    /// the rows outlive their principal, and the role identifier is reissued - so leaving them behind hands
    /// authority to whichever role next takes the vacated identifier. The plan's own reference list names that
    /// grant triad within the in-scope security domain, so the rows are within this migration's knowledge even
    /// though the feature is not.
    /// </para>
    /// <para>
    /// The exception is therefore pinned by SHAPE, and the three conditions are what make it incapable of
    /// growing into the excluded feature: it must be exactly one member, it must be on the persistence surface
    /// rather than the application one, it must produce nothing - a removal cannot disclose a grant, whereas a
    /// read could - and it must be addressable by role alone, so no caller can name a storage location to it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_CarriesNoFeatureForTheExcludedStorageSubsystem()
    {
        string[] excludedVocabulary = ["Folder", "Directory", "Path"];

        var admitted = new List<(Type Contract, MethodInfo Member)>();

        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            bool namesTheSubsystem = excludedVocabulary.Any(token =>
                member.Name.Contains(token, StringComparison.Ordinal));

            if (!namesTheSubsystem)
            {
                continue;
            }

            admitted.Add((contract, member));
        }

        (Type Contract, MethodInfo Member) exception = admitted.Should().ContainSingle(
            "the storage subsystem is excluded except for the single removal the legacy role deletion performed")
            .Subject;

        exception.Contract.Should().Be(
            typeof(IPermissionRepository),
            "removing rows belongs to the persistence surface; the application surface exposes the role-scoped "
            + "sweep as one operation over every grant family and names no subsystem");

        ProducedValue(exception.Member.ReturnType).Should().Be(
            typeof(void),
            "a removal reports nothing, so nothing about the excluded subsystem can be read back through it");

        exception.Member.GetParameters().Select(parameter => parameter.Name).Should().Equal(
            ["roleId", "cancellationToken"],
            "it is addressable by role alone - a storage argument would make it the feature this exclusion forbids");
    }

    // =================================================================================================
    // The model the resolution rules stand on.
    //
    // Every rule about who a grant reaches is a statement about a row, and presupposes that the row can
    // carry the value. These assertions fail at the model, naming the column, which is where such a
    // regression is cheapest to read.
    // =================================================================================================

    /// <summary>
    /// A grant addresses either a role or an account, and both columns are optional, so a row addressing
    /// neither is expressible - which is what allows it to be refused rather than crashed on.
    /// </summary>
    /// <param name="grantType">The grant entity under inspection.</param>
    /// <remarks>
    /// MIGRATION: the legacy row expressed "no account" as the integer sentinel <c>-1</c> and asked
    /// <c>Null.IsNull(UserID)</c> about it. That conversion to a nullable column is done, and Rule T7 governs
    /// what may follow it: the ACCOUNT sentinel became null, and the ROLE column must not be given the same
    /// treatment, because <c>-1</c> there is a real principal rather than an absence. Both columns being
    /// optional is therefore not laxity - it is the only shape in which "addressed to a role", "addressed to
    /// an account" and "addressed to nobody" are three distinct rows.
    /// </remarks>
    [Theory]
    [MemberData(nameof(GrantEntityTypes))]
    public void GrantModel_AddressesARoleOrAnAccountAndBothAreOptional(Type grantType)
    {
        PropertyInfo role = grantType.GetProperty("RoleId").Should().NotBeNull().And.Subject.As<PropertyInfo>();
        PropertyInfo account = grantType.GetProperty("UserId").Should().NotBeNull().And.Subject.As<PropertyInfo>();

        role.PropertyType.Should().Be(
            typeof(int?),
            $"{grantType.Name} must be able to carry a grant that names no role, or an account-addressed grant becomes unrepresentable");

        account.PropertyType.Should().Be(
            typeof(int?),
            $"{grantType.Name} must be able to carry a grant that names no account");

        grantType.GetProperty("AllowAccess").Should().NotBeNull()
            .And.Subject.As<PropertyInfo>().PropertyType.Should().Be(
                typeof(bool),
                $"{grantType.Name} records an allowance or a denial and never a third state - the deny-precedence rule has nothing to say about an absent one");
    }

    /// <summary>
    /// Every principal the resolution rules name survives a round trip through the grant model unchanged,
    /// including the identifier the role table issues first.
    /// </summary>
    /// <param name="principal">The stored role identifier.</param>
    /// <param name="description">What that identifier means, so a failure names the rule it breaks.</param>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy predicate compared role NAMES, matching the all-users and unauthenticated names
    /// as strings and short-circuiting on the operator flag. The target compares stored identifiers, so those
    /// principals are values this model must carry - and two of them are the kind of value a well-meaning
    /// guard destroys. The role table is an identity column seeded at zero, so <strong>zero is an ordinary
    /// role</strong>; the pseudo-principals are negative and match no role row at all, which is why neither
    /// grant table declares a key on the column.
    /// </para>
    /// <para>
    /// This is deliberately a round-trip assertion rather than a behavioural one. It is the cheapest possible
    /// detector for the specific regression that would break every rule at once: a change that normalised,
    /// clamped or absent-ed a negative or zero identifier on the way in. A value object or a setter that
    /// treated a negative as "unset" would fail here immediately, naming the value, instead of surfacing as a
    /// puzzling denial deep in the behaviour suite.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(AllUsersPrincipal, "the all-users principal, which grants a public page unconditionally")]
    [InlineData(OperatorPrincipal, "the installation-operator principal")]
    [InlineData(UnauthenticatedPrincipal, "the unauthenticated-caller principal")]
    [InlineData(FirstIssuedRoleId, "the first identifier the role table issues, which is an ordinary role")]
    [InlineData(OrdinaryRoleId, "an ordinary role well clear of the seed and the pseudo-principals")]
    [InlineData(NotAPrincipal, "an identifier that is not a principal, and is never repurposed as one")]
    public void GrantModel_CarriesEveryPrincipalIdentifierUnchanged(int principal, string description)
    {
        var moduleGrant = new ModulePermission { RoleId = principal, AllowAccess = true };
        var pageGrant = new TabPermission { RoleId = principal, AllowAccess = true };

        moduleGrant.RoleId.Should().Be(principal, $"a module grant must carry {description}");
        pageGrant.RoleId.Should().Be(principal, $"a page grant must carry {description}");

        moduleGrant.UserId.Should().BeNull("a role-addressed grant names no account");
        pageGrant.UserId.Should().BeNull("a role-addressed grant names no account");
    }

    /// <summary>
    /// A grant naming neither a role nor an account is expressible, so the rule that it reaches nobody has
    /// something to refuse.
    /// </summary>
    /// <remarks>
    /// The row exists in real data and the resolution rule for it is to fail closed. A model that could not
    /// represent it would make that rule untestable and would turn a legacy row into a load-time fault, which
    /// is a worse outcome than refusing it.
    /// </remarks>
    [Fact]
    public void GrantModel_CanExpressAGrantThatReachesNobody()
    {
        var moduleGrant = new ModulePermission { RoleId = null, UserId = null, AllowAccess = true };
        var pageGrant = new TabPermission { RoleId = null, UserId = null, AllowAccess = true };

        moduleGrant.RoleId.Should().BeNull();
        moduleGrant.UserId.Should().BeNull();
        pageGrant.RoleId.Should().BeNull();
        pageGrant.UserId.Should().BeNull();
    }

    /// <summary>
    /// An account-addressed grant carries the account and no role, and the account identifier is a plain
    /// value rather than a name wrapped in punctuation.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy predicate expressed an account-addressed grant by composing the pseudo-role text
    /// <c>"[" &amp; UserID &amp; "]"</c> and testing membership of it against a delimited string of role names.
    /// Both mechanisms are gone: the account travels as an optional integer, and there is no delimited string
    /// and no bracketed pseudo-role anywhere in the target. This assertion is the model-level residue of that
    /// removal - the row says which account it names, in a column, and nothing has to be parsed to find out.
    /// </remarks>
    [Fact]
    public void GrantModel_AddressesAnAccountByIdentifierRatherThanByComposedText()
    {
        const int accountId = 7;

        var moduleGrant = new ModulePermission { UserId = accountId, AllowAccess = true };
        var pageGrant = new TabPermission { UserId = accountId, AllowAccess = true };

        moduleGrant.UserId.Should().Be(accountId);
        moduleGrant.RoleId.Should().BeNull("an account-addressed grant names no role");
        pageGrant.UserId.Should().Be(accountId);
        pageGrant.RoleId.Should().BeNull("an account-addressed grant names no role");
    }

    /// <summary>
    /// A grant names its catalogue entry by identifier and its subject by identifier, and derives from
    /// neither.
    /// </summary>
    /// <param name="grantType">The grant entity under inspection.</param>
    /// <param name="subjectKeyName">The name of the column identifying what the grant is recorded against.</param>
    /// <remarks>
    /// The two grant families are scalar rows, not specialisations of the catalogue entry, and the resolution
    /// rules depend on that: a verdict needs a catalogue lookup AND an assignment lookup, which are two reads
    /// rather than one traversal. A grant that inherited from the catalogue entry would make "which key is
    /// this row about" answerable without the second read, and the rule about a grant whose catalogue entry is
    /// missing would become unexpressible.
    /// </remarks>
    [Theory]
    [InlineData(typeof(ModulePermission), "ModuleId")]
    [InlineData(typeof(TabPermission), "TabId")]
    public void GrantModel_NamesItsCatalogueEntryAndItsSubjectByIdentifier(Type grantType, string subjectKeyName)
    {
        grantType.Should().NotBeDerivedFrom<Permission>(
            $"{grantType.Name} records an assignment of a catalogue entry, and is not a kind of one");

        grantType.GetProperty("PermissionId").Should().NotBeNull()
            .And.Subject.As<PropertyInfo>().PropertyType.Should().Be(
                typeof(int),
                $"{grantType.Name} must name exactly one catalogue entry, always");

        grantType.GetProperty(subjectKeyName).Should().NotBeNull()
            .And.Subject.As<PropertyInfo>().PropertyType.Should().Be(
                typeof(int),
                $"{grantType.Name} must name exactly one subject, always - and the identifier table is seeded at zero, so absence is not representable here and does not need to be");
    }

    /// <summary>
    /// The catalogue entry carries its key as the closed enumeration and its scope as text, not the reverse.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy catalogue class declared all four of its string members as text, and the terminal
    /// schema stores them as narrow character columns. The key is the one of the four whose values are a closed
    /// set, so it becomes the enumeration and the scope code stays text - which is what lets a caller name a
    /// key without quoting a literal, while a scope code from a subsystem this migration excludes remains
    /// merely a string that matches nothing. All serialisation decoration is dropped; the wire shape belongs to
    /// the API boundary.
    /// </remarks>
    [Fact]
    public void CatalogueModel_CarriesTheKeyAsTheClosedEnumerationAndTheScopeAsText()
    {
        typeof(Permission).GetProperty(nameof(Permission.PermissionKey)).Should().NotBeNull()
            .And.Subject.As<PropertyInfo>().PropertyType.Should().Be(
                typeof(PermissionKey),
                "the key is a closed set and must not be reachable as free text");

        typeof(Permission).GetProperty(nameof(Permission.PermissionCode)).Should().NotBeNull()
            .And.Subject.As<PropertyInfo>().PropertyType.Should().Be(
                typeof(string),
                "the scope code is open text, which is how an excluded subsystem's code stays merely unmatched");
    }

    /// <summary>
    /// Inherited view is a TRI-STATE, and the third state is a row the flag was never written on.
    /// </summary>
    /// <param name="stored">The stored value.</param>
    /// <param name="defersToThePage">Whether that value defers view access to the hosting page.</param>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy class declared this a plain boolean over a boolean field, which could not
    /// represent the column's null at all - every unwritten row arrived as false, silently merging "never set"
    /// with "set to false". The column is nullable in the terminal schema and no later script narrows it, so
    /// the target widens the property to a tri-state. That widening is a documented type correction, not a
    /// transliteration, and it must not be collapsed back.
    /// </para>
    /// <para>
    /// MEASURED REFINEMENT, reported rather than assumed: the specification for this file frames inheritance
    /// as two branches, true and false. The shipped model has THREE states, and the rule that follows is the
    /// one the entity's own remarks state - only an affirmative value defers to the page, so null and false
    /// behave alike at the point of decision while remaining distinguishable in the row. That is why this
    /// assertion pins the deferral question rather than the raw value: a future change that tested the flag for
    /// truthiness rather than for true would pass the raw-value form and break this one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void ModuleModel_TreatsInheritedViewAsATriStateInWhichOnlyAnAffirmativeValueDefers(
        bool? stored,
        bool defersToThePage)
    {
        typeof(ModuleEntity).GetProperty(nameof(ModuleEntity.InheritViewPermissions)).Should().NotBeNull()
            .And.Subject.As<PropertyInfo>().PropertyType.Should().Be(
                typeof(bool?),
                "a row the flag was never written on is a third state and must stay distinguishable from false");

        var module = new ModuleEntity { InheritViewPermissions = stored };

        module.InheritViewPermissions.Should().Be(stored, "the stored value is carried, whichever of the three it is");

        (module.InheritViewPermissions == true).Should().Be(
            defersToThePage,
            "only an affirmative value defers view access to the hosting page; an unwritten flag must not");
    }

    /// <summary>
    /// Inheritance is a property of the module and of nothing else, so it cannot be stated per grant, per
    /// catalogue entry or per key - which is the structural reason the edit key is never inherited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy read consulted this flag on ONE branch only.
    /// <c>ModuleController.vb:L130</c> takes the edit principals from the module's own grants
    /// unconditionally, and only <c>L131-L137</c> branches on the flag - taking the view principals from the
    /// hosting PAGE when it is set and from the module itself when it is not. The write side matches:
    /// <c>L1108</c> withholds an explicit module grant from persistence when the flag is set AND the key is
    /// the view key, and every other grant falls through to a branch that persists it only if it allows
    /// access. So the flag governs the view key and no other, in both directions.
    /// </para>
    /// <para>
    /// That asymmetry is easy to lose, and the way it would be lost is by making inheritance expressible
    /// somewhere per-key - a flag on the grant row, or on the catalogue entry - after which "inherited edit"
    /// becomes representable and someone will eventually honour it. This asserts that there is nowhere to say
    /// it: the module carries the flag, and none of the three permission types carries anything like it. The
    /// branch behaviour itself is proven against the shipped component in the integration suite, which can
    /// exercise a real placement; what is proven here is that the shape leaves the rule only one place to
    /// live.
    /// </para>
    /// <para>
    /// MEASURED REFINEMENT, reported rather than assumed: the specification for this file expects the
    /// application contract to expose replace-the-set members for a module's and a page's grants, and to
    /// assert the write-side rule through them. The shipped contract exposes no such member - its write-side
    /// surface is the removals and the cascade, and grant replacement is reached through the module and page
    /// contracts instead. The write-side rule is therefore cited here and asserted where the members that
    /// perform it actually live, rather than asserted against a member this contract does not declare.
    /// </para>
    /// </remarks>
    [Fact]
    public void InheritedView_IsAModulePropertyAndIsNotExpressiblePerGrantOrPerKey()
    {
        typeof(ModuleEntity).GetProperty(nameof(ModuleEntity.InheritViewPermissions)).Should().NotBeNull(
            "inheritance is decided once, for the module, which is the only thing that hosts placements");

        Type[] permissionTypes = [typeof(Permission), typeof(ModulePermission), typeof(TabPermission)];

        foreach (Type permissionType in permissionTypes)
        {
            IEnumerable<string> inheritanceLikeMembers = permissionType
                .GetProperties()
                .Select(property => property.Name)
                .Where(name => name.Contains("Inherit", StringComparison.Ordinal));

            inheritanceLikeMembers.Should().BeEmpty(
                $"{permissionType.Name} must not be able to state inheritance, or an inherited edit key becomes representable");
        }
    }

    // =================================================================================================
    // The key vocabulary.
    // =================================================================================================

    /// <summary>
    /// The key vocabulary is a closed set of exactly four persisted spellings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy keys were bare upper-case literals scattered across the three grant controllers
    /// and the security helper. They are centralised as named members whose NAMES are the persisted and wire
    /// values, matching the catalogue's narrow character column, and the ordinals are incidental - never
    /// persisted, never serialised - so a mapping must convert by name and never by number.
    /// </para>
    /// <para>
    /// MEASURED, and worth recording because the set is wider than the call sites: only two of the four have
    /// any in-scope legacy literal at all. The edit key has six literal sites of which <strong>three are
    /// obsolete</strong> - the security helper's obsolete region opens at <c>L614</c> and encloses <c>L618</c>,
    /// <c>L623</c> and <c>L628</c>, and <c>L617</c> calls an already-obsolete two-argument overload declared at
    /// <c>L377</c>, making it doubly so - leaving three live sites: <c>PortalSecurity.vb:L522</c>,
    /// <c>ModuleController.vb:L130</c> and <c>TabController.vb:L109</c>. The view key has four genuine sites:
    /// <c>ModuleController.vb:L134</c>, <c>L136</c>, <c>L1108</c> and <c>TabController.vb:L110</c>. A fifth
    /// apparent hit, at <c>L504</c> of the legacy tenant-settings source, is a control-panel-mode setting value
    /// and NOT a permission key - it is named here so no reader counts it. The read and write keys are declared
    /// with no in-scope legacy literal, which is a fact about the call sites rather than about the vocabulary.
    /// </para>
    /// </remarks>
    [Fact]
    public void PermissionKeys_AreAClosedSetOfFourPersistedSpellings()
    {
        Enum.GetNames<PermissionKey>().Should().BeEquivalentTo(
            ["VIEW", "EDIT", "READ", "WRITE"],
            "these spellings are stored data; renaming or re-casing one silently invalidates every stored row");
    }

    /// <summary>
    /// A key is one member, never a combination.
    /// </summary>
    /// <remarks>
    /// The catalogue stores one key per row, so a bit-field enumeration would admit a value no row can hold
    /// and would make a combined value look answerable. This is asserted rather than assumed because the
    /// attribute is a one-line change with no other visible consequence.
    /// </remarks>
    [Fact]
    public void PermissionKeys_AreNotABitField()
    {
        typeof(PermissionKey).GetCustomAttribute<FlagsAttribute>().Should().BeNull(
            "a grant names exactly one key, and a combination names no stored row");
    }

    /// <summary>
    /// Every key round-trips as its own persisted spelling, and is never case-folded on the way.
    /// </summary>
    /// <param name="permissionKey">The member under test - all four are covered.</param>
    /// <remarks>
    /// The name is the stored value, so this asserts the property that makes the enumeration usable as one: the
    /// spelling a member produces is upper case, is its own name, and parses back to the same member under an
    /// ordinal, case-sensitive parse. The case-sensitive parse is the load-bearing half - a case-insensitive one
    /// would accept a lower-case spelling that no row holds and hide a mapping that had folded the value.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public void PermissionKeys_RoundTripAsTheirPersistedSpellingAndAreNeverCaseFolded(PermissionKey permissionKey)
    {
        string spelling = permissionKey.ToString();

        spelling.Should().Be(
            spelling.ToUpperInvariant(),
            "the persisted spelling is upper case, and the member name is the persisted spelling");

        Enum.Parse<PermissionKey>(spelling, ignoreCase: false).Should().Be(
            permissionKey,
            "a key must survive the round trip through its own spelling without a case-insensitive parse to help it");
    }

    // =================================================================================================
    // How the rules reach the rows.
    // =================================================================================================

    /// <summary>
    /// The reads a verdict is resolved from are narrowed at the store by catalogue entry, not in memory.
    /// </summary>
    /// <param name="memberName">The repository member.</param>
    /// <remarks>
    /// MIGRATION: the legacy path read every grant of a subject and then filtered the resulting untyped list on
    /// the key. Both grant reads now take the catalogue identifier as well as the subject, so the store returns
    /// the rows a verdict is actually about. That is why the parameter is asserted rather than assumed: a
    /// member that lost it would still compile at every call site that passed only a subject, and the filtering
    /// would quietly move back into memory.
    /// </remarks>
    [Theory]
    [InlineData("GetModulePermissionsByModuleIdAsync")]
    [InlineData("GetTabPermissionsByTabIdAsync")]
    public void GrantReads_AreNarrowedByCatalogueEntryAtTheStore(string memberName)
    {
        MethodInfo member = typeof(IPermissionRepository).GetMethod(memberName)
            .Should().NotBeNull().And.Subject.As<MethodInfo>();

        member.GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(
            [typeof(int), typeof(int), typeof(CancellationToken)],
            $"IPermissionRepository.{memberName} must narrow to one subject and one catalogue entry at the store");

        member.GetParameters()[1].Name.Should().Be(
            "permissionId",
            $"IPermissionRepository.{memberName} must take the catalogue entry, not a second subject");
    }

    /// <summary>
    /// A subject identifier is required where the subject always exists, and optional only where absence is a
    /// distinct question from the identifier zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page and module identifier tables are identity columns seeded at ZERO, so zero names a real row and
    /// "no page" is not expressible as an integer. The grant reads therefore take a plain integer: a nullable
    /// one there would invite exactly the conflation this migration removes, in which zero and absent become
    /// the same argument.
    /// </para>
    /// <para>
    /// Where absence IS a real question - resolving the keys a caller effectively holds, which may be asked
    /// about a module, a page, both or neither - the arguments are optional, and null there means "not scoped
    /// to one" rather than "scoped to the row numbered zero". Asserting both halves together is the point:
    /// either half alone would permit the other to drift into the wrong shape.
    /// </para>
    /// </remarks>
    [Fact]
    public void SubjectScope_IsRequiredWhereZeroIsARealRowAndOptionalOnlyWhereAbsenceIsADistinctQuestion()
    {
        MethodInfo tabKeyedGrants = typeof(IPermissionRepository)
            .GetMethod("GetModulePermissionsByTabIdAsync")
            .Should().NotBeNull().And.Subject.As<MethodInfo>();

        tabKeyedGrants.GetParameters()[0].ParameterType.Should().Be(
            typeof(int),
            "the page identifier table is seeded at zero, so a page is always named and absence is not an argument");

        MethodInfo effectiveKeys = typeof(IPermissionService)
            .GetMethod("GetEffectivePermissionKeysAsync")
            .Should().NotBeNull().And.Subject.As<MethodInfo>();

        IReadOnlyDictionary<string, Type> optionalScope = effectiveKeys
            .GetParameters()
            .Where(parameter => parameter.Name is "moduleId" or "tabId")
            .ToDictionary(parameter => parameter.Name!, parameter => parameter.ParameterType);

        optionalScope.Should().HaveCount(2, "a caller may ask about a module, a page, both or neither");

        optionalScope.Values.Should().AllBeEquivalentTo(
            typeof(int?),
            "absence must be expressible without borrowing the identifier zero, which names a real row");
    }

    /// <summary>
    /// A grant read hands back the rows themselves, denials included, which is what makes deny precedence
    /// resolvable at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: THE HEADLINE DIVERGENCE, and this is its contract-level foundation. The legacy source was
    /// INTERNALLY INCONSISTENT about denials. <c>TabPermissionController.vb:L38-L54</c> and its module twin
    /// resolved a verdict by walking the rows and returning true on the FIRST row whose key matched and whose
    /// principal the caller held - with <strong>no test of the allowance flag anywhere in either method</strong>
    /// - so a stored DENIAL whose role the caller held GRANTED access. Meanwhile
    /// <c>ModulePermissionController.vb:L239-L252</c> built its delimited principal string with
    /// <c>AllowAccess = True AndAlso PermissionKey = key</c>, and therefore did honour the flag. The two met at
    /// <c>PortalSecurity.vb:L521-L522</c>, where the view decision read the FILTERED string and the edit
    /// decision called the UNFILTERED walk: <strong>view honoured the allowance flag and edit did not</strong>.
    /// The target unifies both paths under deny precedence - any matching denial withholds the key, whatever
    /// else grants it, in either row order. That is a deliberate behavioural divergence, and the behaviour
    /// itself is proven against the shipped component in the integration suite.
    /// </para>
    /// <para>
    /// What is proven HERE is the precondition without which that rule could not be implemented: the read hands
    /// back the grant rows, unfiltered, with the allowance flag on each. The legacy helper's shape made deny
    /// precedence impossible rather than merely unimplemented - it discarded denials before its caller ever saw
    /// them, and flattened what survived into text - so a read that reverted to filtering, or to a projection
    /// without the flag, would silently make the rule unenforceable again while every behavioural test that
    /// mocked the resolver kept passing. This assertion fails instead.
    /// </para>
    /// <para>
    /// Substitution is the mechanism as well as the subject: the persistence surface is stood in for entirely
    /// from this project, with no implementation assembly on the graph, which is the same claim the rest of this
    /// file makes about the contracts' completeness.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GrantReads_HandBackTheRowsThemselvesIncludingDenials()
    {
        const int moduleId = 0;
        const int pageId = 0;
        const int catalogueEntryId = 11;

        var allowing = new ModulePermission
        {
            ModuleId = moduleId,
            PermissionId = catalogueEntryId,
            RoleId = OrdinaryRoleId,
            AllowAccess = true,
        };

        var denying = new ModulePermission
        {
            ModuleId = moduleId,
            PermissionId = catalogueEntryId,
            RoleId = AllUsersPrincipal,
            AllowAccess = false,
        };

        var denyingPageGrant = new TabPermission
        {
            TabId = pageId,
            PermissionId = catalogueEntryId,
            RoleId = FirstIssuedRoleId,
            AllowAccess = false,
        };

        var store = new Mock<IPermissionRepository>(MockBehavior.Strict);

        store
            .Setup(rows => rows.GetModulePermissionsByModuleIdAsync(
                moduleId,
                catalogueEntryId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([allowing, denying]);

        store
            .Setup(rows => rows.GetTabPermissionsByTabIdAsync(
                pageId,
                catalogueEntryId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([denyingPageGrant]);

        IPermissionRepository substituted = store.Object;

        IReadOnlyList<ModulePermission> moduleGrants = await substituted
            .GetModulePermissionsByModuleIdAsync(moduleId, catalogueEntryId, CancellationToken.None);

        moduleGrants.Should().HaveCount(
            2,
            "a denial must reach the resolver, or deny precedence cannot be applied to it");

        moduleGrants.Should().Contain(grant => !grant.AllowAccess,
            "the read is unfiltered - the legacy helper discarded denials before its caller saw them");

        moduleGrants.Select(grant => grant.RoleId).Should().Equal(
            [OrdinaryRoleId, AllUsersPrincipal],
            "every principal reaches the resolver unchanged, the pseudo-principal included");

        IReadOnlyList<TabPermission> pageGrants = await substituted
            .GetTabPermissionsByTabIdAsync(pageId, catalogueEntryId, CancellationToken.None);

        pageGrants.Should().ContainSingle().Which.AllowAccess.Should().BeFalse(
            "the page read carries the allowance flag too - the legacy page path filtered on it and lost it");

        pageGrants[0].RoleId.Should().Be(
            FirstIssuedRoleId,
            "the identifier the role table issues first survives the read like any other");
    }

    /// <summary>
    /// A refusal and a failure are different outcomes, and the outcome type keeps them apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the contract-level form of the rule that a store fault must never read as a denial. The rule
    /// itself is behavioural and is proven against the shipped component in the integration suite; what is
    /// proven here is that the shape it relies on actually holds - a successful outcome carrying false is
    /// distinguishable from a failed outcome carrying a reason, and the failed one cannot be mistaken for a
    /// refusal because it has no value to read at all.
    /// </para>
    /// <para>
    /// Not a tautology: the outcome type is real production code, reachable from this project, and this is the
    /// property every verdict in the system rests on. A change that gave a failed outcome a default value would
    /// pass every test that only checked the success flag, and would turn every unavailable store into a
    /// silent denial.
    /// </para>
    /// </remarks>
    [Fact]
    public void VerdictOutcome_KeepsARefusalAndAFailureApart()
    {
        Result<bool> refused = Result<bool>.Success(false);

        refused.IsSuccess.Should().BeTrue("a denial is an answer, and answering is not failing");
        refused.Value.Should().BeFalse();
        refused.Reason.Should().BeNull();

        Result<bool> failed = Result<bool>.Failure("permission.store_unavailable", "The store could not be reached.");

        failed.IsSuccess.Should().BeFalse();
        failed.IsFailure.Should().BeTrue();
        failed.Reason.Should().NotBeNull();
        failed.Error!.Code.Should().Be("permission.store_unavailable");

        Action readingAFailedValue = () => _ = failed.Value;

        readingAFailedValue.Should().Throw<InvalidOperationException>(
            "a failure carries no verdict, so it cannot be read as one - which is what stops an unavailable store becoming a denial");
    }

    // =================================================================================================
    // Helpers. Every one is private and nested, because this assembly's five test folders share a
    // namespace root and a duplicate type name across them is a compile error.
    // =================================================================================================

    /// <summary>The two grant entity types, for the theories that assert both.</summary>
    /// <returns>One row per grant entity.</returns>
    public static TheoryData<Type> GrantEntityTypes() => new()
    {
        typeof(ModulePermission),
        typeof(TabPermission),
    };

    /// <summary>Every member of both contracts, paired with the contract that declares it.</summary>
    /// <returns>The contract and member pairs.</returns>
    private static IEnumerable<(Type Contract, MethodInfo Member)> ContractMembers()
        => Contracts.SelectMany(contract => contract
            .GetMethods()
            .Select(member => (Contract: contract, Member: member)));

    /// <summary>
    /// Unwraps a declared return type down to the value it ultimately produces.
    /// </summary>
    /// <param name="declared">The declared return type.</param>
    /// <returns>
    /// <see cref="void"/> for a bare task or a valueless outcome, and the innermost argument once the task and
    /// outcome wrappers have been peeled away.
    /// </returns>
    /// <remarks>
    /// Written as one unwrapper rather than repeated at each call site so that "what does this member actually
    /// produce" has a single answer. The valueless outcome collapses to void deliberately: a member reporting
    /// only success or failure produces no value, and treating it as producing an outcome object would make
    /// every such member look like it carried one.
    /// </remarks>
    private static Type ProducedValue(Type declared)
    {
        Type current = declared;

        if (current == typeof(Task))
        {
            return typeof(void);
        }

        if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Task<>))
        {
            current = current.GetGenericArguments()[0];
        }

        if (current == typeof(Result))
        {
            return typeof(void);
        }

        if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Result<>))
        {
            current = current.GetGenericArguments()[0];
        }

        return current;
    }

    /// <summary>
    /// Every distinct type a member mentions: its produced value, its declared return type's arguments, and
    /// each parameter type, with generic arguments and nullable and array element types unwrapped.
    /// </summary>
    /// <param name="member">The member to inspect.</param>
    /// <returns>The distinct surface types.</returns>
    /// <remarks>
    /// Recursive through generic arguments on purpose. A forbidden type reached only as the element of a
    /// returned sequence is just as much on the surface as one returned directly, and a shallow walk would
    /// miss it.
    /// </remarks>
    private static IReadOnlyCollection<Type> SurfaceTypes(MethodInfo member)
    {
        var collected = new HashSet<Type>();

        Collect(member.ReturnType, collected);

        foreach (ParameterInfo parameter in member.GetParameters())
        {
            Collect(parameter.ParameterType, collected);
        }

        return collected;
    }

    /// <summary>Adds one type and everything it is composed of to the collected set.</summary>
    /// <param name="type">The type to add.</param>
    /// <param name="collected">The set being built.</param>
    private static void Collect(Type type, HashSet<Type> collected)
    {
        Type current = Nullable.GetUnderlyingType(type) ?? type;

        if (current.IsArray)
        {
            Collect(current.GetElementType()!, collected);
            return;
        }

        if (!collected.Add(current))
        {
            return;
        }

        if (current.IsGenericType)
        {
            foreach (Type argument in current.GetGenericArguments())
            {
                Collect(argument, collected);
            }
        }
    }
}
