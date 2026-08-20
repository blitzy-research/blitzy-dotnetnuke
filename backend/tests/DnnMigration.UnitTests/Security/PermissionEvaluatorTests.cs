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
/// an algorithm, and an algorithm can only be verified against the thing that implements it.
/// </para>
/// <para>
/// FIRST, THE CONTRACT SHAPE IS ASSERTED FROM THE OUTSIDE. Every member is an awaitable, cancellable task;
/// nothing declares an output or reference parameter; every type on either surface comes from an
/// allow-listed assembly; every sequence handed back is a read-only generic one; the persistence surface
/// produces no verdict; and every verdict that does exist is wrapped in an outcome.
/// </para>
/// </remarks>
public class PermissionEvaluatorTests
{
    /// <summary>
    /// The all-users principal. Persisted in the grant tables' role column and matching no role row.
    /// </summary>
    /// <remarks>
    /// The legacy source defines it as a role NAME compared as a string; the target compares the stored
    /// identifier. It matches unconditionally, authenticated or not, which is how a public page is
    /// expressed.
    /// </remarks>
    private const int AllUsersPrincipal = -1;

    /// <summary>The installation-operator principal, likewise persisted and matching no role row.</summary>
    private const int OperatorPrincipal = -2;

    /// <summary>The unauthenticated-caller principal, which matches only a caller who has not signed in.</summary>
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
    private static readonly Type[] Contracts =
    [
        typeof(IPermissionService),
        typeof(IPermissionRepository),
    ];

    /// <summary>
    /// The assemblies a permission contract may mention on its surface: the domain, the application, and
    /// the framework's own core.
    /// </summary>
    /// <remarks>
    /// Stated as an allow-list rather than a list of forbidden names, and that is the stronger of the two
    /// forms: a denylist rejects only what its author remembered, whereas this rejects everything that was
    /// not permitted - including dependencies that do not exist yet.
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
    [Fact]
    public void Contract_EveryMemberIsAnAwaitableCancellableTask()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            // The one deliberate exception. The cache eviction runs AFTER its caller's commit, so it must
            // not be able to fail or be cancelled half-done; it performs no input or output at all,
            // evicting two in-memory key families by name and by prefix.
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

    /// <summary>Neither contract declares an output or reference parameter anywhere.</summary>
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

    /// <summary>The persistence surface answers no access question at all.</summary>
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
    /// The distinction this pins is the one a caller most needs and most easily loses. A refusal and a
    /// failure are not the same event: "you may not do this" is a successful answer whose value happens to
    /// be false, while "the store could not be reached" is a failure carrying no answer at all.
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

    /// <summary>Every type either contract mentions comes from an allow-listed assembly.</summary>
    /// <remarks>
    /// The reflection-driven row hydrator and the reflection-created provider singleton are both gone,
    /// replaced by the object-relational materialiser and constructor injection, and neither may leave a
    /// trace on these contracts.
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
    /// Every sequence either contract hands back is a read-only generic one, and no legacy collection
    /// wrapper appears anywhere on either surface.
    /// </summary>
    /// <remarks>
    /// Detected by SHAPE rather than by name, which is both necessary and better. Necessary, because naming
    /// the untyped collection types in order to forbid them would put those names on this file.
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
    /// MIGRATION: the path-scoped catalogue lookup and the storage-grant feature members are not ported.
    /// File management is out of scope, so no target feature exists for a grant keyed by a storage location
    /// to serve, and the domain declares no entity for one.
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

    // Every rule about who a grant reaches is a statement about a row, and presupposes that the row can
    // carry the value. These assertions fail at the model, naming the column, which is where such a
    // regression is cheapest to read.

    /// <summary>
    /// A grant addresses either a role or an account, and both columns are optional, so a row addressing
    /// neither is expressible - which is what allows it to be refused rather than crashed on.
    /// </summary>
    /// <param name="grantType">The grant entity under inspection.</param>
    /// <remarks>
    /// The legacy row expressed "no account" as the integer sentinel <c>-1</c> and asked
    /// <c>Null.IsNull(UserID)</c> about it. That conversion to a nullable column is done, and Rule T7
    /// governs what may follow it: the ACCOUNT sentinel became null, and the ROLE column must not be given
    /// the same treatment, because <c>-1</c> there is a real principal rather than an absence.
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
    /// The legacy predicate compared role NAMES, matching the all-users and unauthenticated names as
    /// strings and short-circuiting on the operator flag. The target compares stored identifiers, so those
    /// principals are values this model must carry - and two of them are the kind of value a well-meaning
    /// guard destroys.
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
    /// The legacy predicate expressed an account-addressed grant by composing the pseudo-role text <c>"["
    /// &amp; UserID &amp; "]"</c> and testing membership of it against a delimited string of role names.
    /// Both mechanisms are gone: the account travels as an optional integer, and there is no delimited
    /// string and no bracketed pseudo-role anywhere in the target.
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

    /// <summary>The catalogue entry carries BOTH its key and its scope as text.</summary>
    /// <remarks>
    /// The legacy catalogue class declared all four of its string members as text, and the terminal schema
    /// stores them as narrow character columns with no check constraint on any of them. Typing the key as
    /// the closed enumeration was tried and is a defect rather than a hardening: DotNetNuke's own
    /// <c>AddPermission</c> procedure accepts <c>@PermissionKey varchar(50)</c> so a third-party module can
    /// register its own keys at install time, and an enum-typed property makes every such row unreadable -
    /// it threw from inside the materialiser, which no result type can intercept, so the catalogue read and
    /// the module authorisation path both answered an unhandled fault. This assertion is what stops that
    /// mapping from coming back.
    /// </remarks>
    [Fact]
    public void CatalogueModel_CarriesBothTheKeyAndTheScopeAsText()
    {
        typeof(Permission).GetProperty(nameof(Permission.PermissionKey)).Should().NotBeNull()
            .And.Subject.As<PropertyInfo>().PropertyType.Should().Be(
                typeof(string),
                "the key column is free text, so every value a real installation can hold must materialise");

        typeof(Permission).GetProperty(nameof(Permission.PermissionCode)).Should().NotBeNull()
            .And.Subject.As<PropertyInfo>().PropertyType.Should().Be(
                typeof(string),
                "the scope code is open text, which is how an excluded subsystem's code stays merely unmatched");
    }

    /// <summary>
    /// The four keys the upgrade chain seeds remain a closed enumeration, because that set is what this
    /// solution's own policies are written against.
    /// </summary>
    /// <remarks>
    /// The two facts are complementary rather than contradictory: the enumeration bounds the keys the target
    /// can ASK ABOUT, and the free-text column bounds nothing at all. A member's identifier is the stored
    /// spelling, which is what lets one be compared against the other without a lookup table.
    /// </remarks>
    [Fact]
    public void CatalogueKeys_ThisSolutionNamesAreStillAClosedEnumerationSpelledAsStored()
    {
        Enum.GetNames<PermissionKey>().Should().Equal("VIEW", "EDIT", "READ", "WRITE");

        nameof(PermissionKey.VIEW).Should().Be("VIEW");
        nameof(PermissionKey.EDIT).Should().Be("EDIT");
        nameof(PermissionKey.READ).Should().Be("READ");
        nameof(PermissionKey.WRITE).Should().Be("WRITE");
    }

    /// <summary>Inherited view is a TRI-STATE, and the third state is a row the flag was never written on.</summary>
    /// <param name="stored">The stored value.</param>
    /// <param name="defersToThePage">Whether that value defers view access to the hosting page.</param>
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

    /// <summary>The key vocabulary is a closed set of exactly four persisted spellings.</summary>
    /// <remarks>
    /// The legacy keys were bare upper-case literals scattered across the three grant controllers and the
    /// security helper. They are centralised as named members whose NAMES are the persisted and wire
    /// values, matching the catalogue's narrow character column, and the ordinals are incidental - never
    /// persisted, never serialised - so a mapping must convert by name and never by number.
    /// </remarks>
    [Fact]
    public void PermissionKeys_AreAClosedSetOfFourPersistedSpellings()
    {
        Enum.GetNames<PermissionKey>().Should().BeEquivalentTo(
            ["VIEW", "EDIT", "READ", "WRITE"],
            "these spellings are stored data; renaming or re-casing one silently invalidates every stored row");
    }

    /// <summary>A key is one member, never a combination.</summary>
    [Fact]
    public void PermissionKeys_AreNotABitField()
    {
        typeof(PermissionKey).GetCustomAttribute<FlagsAttribute>().Should().BeNull(
            "a grant names exactly one key, and a combination names no stored row");
    }

    /// <summary>Every key round-trips as its own persisted spelling, and is never case-folded on the way.</summary>
    /// <param name="permissionKey">The member under test - all four are covered.</param>
    /// <remarks>
    /// The name is the stored value, so this asserts the property that makes the enumeration usable as one:
    /// the spelling a member produces is upper case, is its own name, and parses back to the same member
    /// under an ordinal, case-sensitive parse.
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
    /// A subject identifier is required where the subject always exists, and optional only where absence is
    /// a distinct question from the identifier zero.
    /// </summary>
    /// <remarks>
    /// The page and module identifier tables are identity columns seeded at ZERO, so zero names a real row
    /// and "no page" is not expressible as an integer. The grant reads therefore take a plain integer: a
    /// nullable one there would invite exactly the conflation this migration removes, in which zero and
    /// absent become the same argument.
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
    /// MIGRATION: THE HEADLINE DIVERGENCE, and this is its contract-level foundation.
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

    /// <summary>A refusal and a failure are different outcomes, and the outcome type keeps them apart.</summary>
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

    /// <summary>Unwraps a declared return type down to the value it ultimately produces.</summary>
    /// <param name="declared">The declared return type.</param>
    /// <returns>
    /// <see cref="void"/> for a bare task or a valueless outcome, and the innermost argument once the task
    /// and outcome wrappers have been peeled away.
    /// </returns>
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
