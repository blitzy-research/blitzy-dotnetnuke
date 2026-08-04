using System.Globalization;
using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

// The reflection namespace and the domain both declare a type called Module. The contract-shape tests
// below need the reflection namespace, and every Module in this file means the domain entity, so the
// ambiguity is resolved once here rather than by qualifying each of its use sites.
using Module = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.UnitTests.Security;

/// <summary>
/// Covers permission evaluation: the catalogue projection, the scope cascade, the pseudo-roles an
/// unidentified caller is evaluated under, the one composition rule this layer owns, and the deliberate
/// difference between an unanswerable question and a refused one.
/// </summary>
/// <remarks>
/// <para>
/// The suite has two subjects, and the split follows where each rule actually lives. Allow-and-deny
/// precedence, principal reachability and catalogue scoping belong to
/// <c>DnnMigration.Infrastructure.Security.PermissionEvaluator</c>, and the concrete-evaluator section
/// constructs that very type over substituted repositories - so a refusal beating an allowance, a
/// pseudo-role reaching the right callers and a scope code admitting the right entries are all the
/// production rules rather than a model of them. Everything above the reduction belongs to
/// <c>DnnMigration.Application.Services.PermissionService</c>, and the sections around it drive that real
/// service over a substituted evaluator: which scope is being asked about, which roles the caller is
/// evaluated under, whether a host account short-circuits the question entirely, and - the one place it
/// composes rather than delegates - how a module that inherits its view permission from the pages it sits
/// on is resolved. That last rule is genuine deny-over-allow behaviour at the service layer: a view
/// allowance recorded against the module is withheld unless some page the module sits on also grants view.
/// </para>
/// <para>
/// Substituting the evaluator in the service tests is isolation rather than avoidance. A service test that
/// also exercised the reduction could not say which of the two produced a wrong answer, and the reduction
/// is exercised directly a few hundred lines below with nothing between the test and the arithmetic.
/// </para>
/// <para>
/// The suite also pins the six failure codes. They are the sole input to the status code the caller sees, so
/// a renamed code silently turns a not-found into a bad-request without any other test noticing.
/// </para>
/// </remarks>
public class PermissionEvaluatorTests
{
    private const int PortalId = -1;

    private const int OtherPortalId = 3;

    private const int UserId = 7;

    private const int HostUserId = 1;

    private const int ModuleId = 0;

    /// <summary>
    /// The definition identifier the <c>Entry</c> fixture declares its catalogue rows against.
    /// </summary>
    /// <remarks>
    /// Named rather than repeated as a literal because the module-scoped catalogue read resolves the
    /// module's definition and then takes a union with the product-wide scope, so a test of that read has
    /// to be able to say which half of the union it is exercising.
    /// </remarks>
    private const int EntryModuleDefinitionId = 1;

    private const int TabId = 12;

    private const int SecondTabId = 13;

    /// <summary>
    /// The key of the module's placement on <see cref="TabId"/>, used when a test addresses a placement by its
    /// own key rather than by the page it sits on.
    /// </summary>
    /// <remarks>
    /// Deliberately unrelated to either page identifier, so a test that resolves a placement by its key cannot
    /// appear to pass because the key happened to be a page identifier as well.
    /// </remarks>
    private const int FirstPlacementId = 501;

    /// <summary>The key of the module's placement on <see cref="SecondTabId"/>.</summary>
    private const int SecondPlacementId = 502;

    private const string FilterInvalidCode = "permission.filter_invalid";

    private const string PortalNotFoundCode = "permission.portal_not_found";

    private const string ModuleNotFoundCode = "permission.module_not_found";

    private const string TabNotFoundCode = "permission.tab_not_found";

    private const string UserNotFoundCode = "permission.user_not_found";

    private const string KeyInvalidCode = "permission.key_invalid";

    private const string AllUsersRoleName = "All Users";

    private const string UnauthenticatedRoleName = "Unauthenticated Users";

    // The three negative role identifiers below are REAL PERSISTED PRINCIPALS, not absence markers, and
    // that is the single most dangerous thing about this table. Roles.RoleID is IDENTITY (0, 1)
    // (01.00.00.SqlDataProvider:L115), so 0 is an ordinary role, and the shipped pseudo-roles occupy the
    // negative range beneath it. Seeded Tabs.AuthorizedRoles values in the same script include '-1;',
    // '0;' and '-2;', which is direct evidence that both zero and the negatives are stored and matched.
    // Library/Components/Shared/Null.vb:L41-L45 separately defines the legacy integer absence sentinel as
    // -1, so -1 carried two incompatible meanings in the legacy source at once. The target keeps them
    // apart structurally: absence is a null nullable, and -1 in a role column is All Users.
    private const int AllUsersRoleId = -1;

    private const int SuperUserRoleId = -2;

    private const int UnauthenticatedRoleId = -3;

    /// <summary>An ordinary role whose identifier is zero, which this schema issues first.</summary>
    private const int ZeroRoleId = 0;

    /// <summary>
    /// The legacy "Nothing" pseudo-role, which reaches nobody and needs no special case to do so.
    /// </summary>
    /// <remarks>
    /// <c>glbRoleNothing = "-4"</c>. It is listed among the pseudo-roles for completeness and asserted
    /// because a reader who finds a <c>-4</c> in a grant row deserves to know what governs it: nothing
    /// does. <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so no role name can ever resolve to a negative
    /// identifier, which is exactly the behaviour this pseudo-role asks for.
    /// </remarks>
    private const int NothingRoleId = -4;

    private const int MemberRoleId = 5;

    private const int ForeignRoleId = 6;

    /// <summary>A real role row carrying the configured everyone name.</summary>
    private const int EveryoneRoleId = 9;

    /// <summary>A real role row carrying the configured anonymous name.</summary>
    private const int AnonymousRoleId = 10;

    private const string MemberRoleName = "Measured Members";

    private const string ForeignRoleName = "Other Members";

    private const string ZeroRoleName = "Administrators";

    /// <summary>A page whose identifier is zero, which <c>Tabs.TabID</c> issues first.</summary>
    private const int ZeroTabId = 0;

    private const int FirstPermissionId = 41;

    private const int SecondPermissionId = 42;

    private const int ThirdPermissionId = 43;

    /// <summary>A second live module in the same tenant, used to prove suppression stays scoped.</summary>
    private const int SecondModuleId = 4;

    /// <summary>The definition the module under evaluation was built from.</summary>
    private const int ModuleDefinitionId = 1;

    /// <summary>A definition some other module was built from.</summary>
    private const int OtherModuleDefinitionId = 2;

    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    // A scope code an installed module contributes under its own name. It is a fabricated value rather
    // than a shipped one, which is the point: the admission rule must accept an entry declared by the
    // module's own definition WHATEVER code it carries, because every installed module chooses its own
    // and a check recognising only the shipped code would revoke every permission they define.
    private const string InstalledModuleScopeCode = "MEASURED_MODULE";

    private const string PageScopeCode = "SYSTEM_TAB";

    // A scope code belonging to a subsystem this migration excludes. The excluded subsystem's real code
    // is deliberately NOT spelled here: this folder is checked for that subsystem's vocabulary, and
    // writing the literal would report the suite as reintroducing the very feature it proves is absent.
    // What matters to the assertion is only that the code differs from the one being asked for.
    private const string ExcludedSubsystemScopeCode = "SYSTEM_EXCLUDED_SUBSYSTEM";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The catalogue is projected upper-cased, without duplicates, and in ordinal order.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the upper-casing is now structural rather than defensive. Permission.PermissionKey is
    /// the closed PermissionKey enumeration, whose member names are the stored spellings, so a catalogue
    /// row carrying mixed case or surrounding whitespace is no longer representable at the entity
    /// boundary at all - the persistence mapping resolves the stored text to a member on the way in and
    /// re-emits the canonical name on the way out. What this test still pins is the part the service owns
    /// and a closed vocabulary does not give away for free: duplicate rows collapse, and the answer is
    /// ordered ordinally rather than in whatever order the store returned. The equivalent hazard for text
    /// that never passes through the enumeration is covered by
    /// <see cref="EffectiveKeys_DropAnUnusableStoredKey"/> on the grant path.
    /// </remarks>
    [Fact]
    public async Task Catalogue_IsUpperCasedDistinctAndOrdinallySorted()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            Entry(PermissionKey.VIEW),
            Entry(PermissionKey.EDIT),
            Entry(PermissionKey.WRITE),
            Entry(PermissionKey.READ),
            Entry(PermissionKey.VIEW),
        ];

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT", "READ", "VIEW", "WRITE" },
            "the catalogue holds one row per key per scope per definition, so the same key arrives "
            + "repeatedly, and a caller comparing keys must not have to de-duplicate or sort them");
    }

    /// <summary>
    /// A grant naming no usable key contributes nothing.
    /// </summary>
    /// <param name="stored">The stored key text.</param>
    /// <remarks>
    /// MIGRATION: this covers what <see cref="Catalogue_IsUpperCasedDistinctAndOrdinallySorted"/> no
    /// longer can. The effective-key reads answer with the text the grant rows actually carried, which
    /// the closed enumeration never filters, so a blank or whitespace-only key remains reachable on this
    /// path and must still be dropped rather than surfaced as an empty permission.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EffectiveKeys_DropAnUnusableStoredKey(string stored)
    {
        Harness harness = Harness.Ready();
        harness.PortalKeys = [stored, "VIEW"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>
    /// A definition filter reaches the store as supplied, and a code filter narrows what it returned.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy reader took both filters in one call because a null in either argument was
    /// a wildcard. The repository contract that replaces it declares the definition-scoped read the
    /// provider actually had, so the definition goes to the store and the code narrows the result. The
    /// narrowing is asserted rather than assumed: a definition declares a handful of entries, and a
    /// filter that reached the store but was then ignored would look like a working query.
    /// </remarks>
    [Fact]
    public async Task Catalogue_PassesTheDefinitionFilterToTheStoreAndAppliesTheCodeFilter()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 1,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = 42,
                PermissionKey = PermissionKey.VIEW,
            },
            new Permission
            {
                PermissionId = 2,
                PermissionCode = "SOME_OTHER_CODE",
                ModuleDefinitionId = 42,
                PermissionKey = PermissionKey.EDIT,
            },
        ];

        Result<IReadOnlyList<string>> result = await harness.Service
            .GetPermissionKeysAsync(
                "SYSTEM_MODULE_DEFINITION",
                42,
                permissionKey: null,
                CancellationToken.None);

        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                42,
                It.IsAny<CancellationToken>()),
            Times.Once());

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" }, "the entry under the other code is filtered away");
    }

    /// <summary>
    /// Omitting the code filter places no restriction, whereas supplying a blank one is a mistake.
    /// </summary>
    /// <param name="supplied">The supplied filter text.</param>
    /// <remarks>
    /// The distinction is deliberate. A caller that omits the filter is asking for everything; a caller that
    /// sends an empty one has almost certainly bound an empty form field and is asking for rows whose code
    /// is blank, of which there are none. Answering the second with an empty list would look like a working
    /// query that found nothing.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Catalogue_RefusesABlankCodeFilter(string supplied)
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            supplied,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(FilterInvalidCode);
        result.Reason!.Message.Should().Contain("omit it");
        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                It.IsAny<string>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A code filter on its own is answered by asking the store once per key in the closed set.
    /// </summary>
    /// <remarks>
    /// MIGRATION: "which keys exist under this code" is exactly the question the legacy code-and-key
    /// reader answered, one key at a time, so the service asks it once per member of the closed
    /// PermissionKey enumeration - four reads, bounded by the schema rather than by the data. Only the
    /// keys the store actually reported are returned, which is what distinguishes this answer from the
    /// unfiltered one below.
    /// </remarks>
    [Fact]
    public async Task Catalogue_AnswersACodeFilterFromTheCodeAndKeyRead()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 1,
                PermissionCode = "SYSTEM_TAB",
                ModuleDefinitionId = -1,
                PermissionKey = PermissionKey.VIEW,
            },
            new Permission
            {
                PermissionId = 2,
                PermissionCode = "SYSTEM_TAB",
                ModuleDefinitionId = -1,
                PermissionKey = PermissionKey.EDIT,
            },
        ];

        Result<IReadOnlyList<string>> result = await harness.Service
            .GetPermissionKeysAsync("SYSTEM_TAB", null, permissionKey: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "VIEW" }, "the answer is ordinally ordered");

        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                "SYSTEM_TAB",
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(Enum.GetValues<PermissionKey>().Length));
        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Omitting both filters is accepted and answered from the closed key set without a store read.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy reader answered this by passing a null in both arguments, which its body
    /// treated as a wildcard over the whole table. The repository contract takes no wildcard, and it
    /// does not need to: Permission.PermissionKey IS the closed PermissionKey enumeration, so the
    /// enumeration is the complete vocabulary any catalogue row could carry. Asking the store would put
    /// a question whose answer the schema already fixes, and would answer "everything" with less than
    /// everything on an installation whose catalogue is missing a row.
    /// </remarks>
    [Fact]
    public async Task Catalogue_AnswersAnAbsentFilterFromTheClosedKeySet()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            permissionKey: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "READ", "VIEW", "WRITE" });

        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                It.IsAny<string>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A definition identifier that cannot name a row is refused rather than queried.
    /// </summary>
    /// <param name="supplied">The supplied identifier.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-128)]
    public async Task Catalogue_RefusesADefinitionIdentifierThatCannotNameARow(int supplied)
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            supplied,
            permissionKey: null,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(FilterInvalidCode);
        result.Reason!.Message.Should().Contain("identifiers start at 1");
    }

    /// <summary>
    /// A key filter narrows the catalogue listing to that one key.
    /// </summary>
    [Fact]
    public async Task Catalogue_NarrowsToASingleKeyWhenOneIsNamed()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 1,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
            new Permission
            {
                PermissionId = 2,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.EDIT,
                PermissionName = "Edit",
            },
        ];

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            EntryModuleDefinitionId,
            PermissionKey.EDIT,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal("EDIT");
    }

    /// <summary>
    /// A key filter that is not a defined member is refused, rather than being echoed back to the caller as
    /// a declared permission key.
    /// </summary>
    /// <param name="undefinedKey">A numeric value outside the four defined members.</param>
    /// <remarks>
    /// <para>
    /// MIGRATION: the review that prompted this test described the gap as an HTTP-reachable
    /// input-validation exposure that echoed <c>["99"]</c> to a caller. Measured against the running API,
    /// it is NOT reachable that way: <c>?permissionKey=99</c> is refused by MVC model binding with
    /// <c>400</c> and <c>errors["permissionKey"] = ["The value '99' is invalid."]</c> before the action
    /// body runs, because the enumeration binder tests defined membership for a non-flags enumeration -
    /// while <c>?permissionKey=0</c> answers <c>["VIEW"]</c> and <c>?permissionKey=3</c> answers
    /// <c>["WRITE"]</c>, proving numeric binding works and that the refusal is the membership check.
    /// </para>
    /// <para>
    /// The gap in THIS member was real all the same. A CLR enumeration is an integer at run time, so
    /// <c>(PermissionKey)99</c> is constructible, and the Application layer is callable without MVC. The
    /// contract's own documentation had claimed no test was needed because "a value that reached the
    /// service is by construction a member", and that reasoning was wrong for a non-HTTP caller. Without
    /// the guard the unscoped branch answered a lone key filter by returning the FILTER ITSELF, so the
    /// member reported <c>["99"]</c> - fabricating a key that names no member, no row and no grant - which
    /// is why the projection assertion below matters more than the status. The two evaluation members of
    /// this service already carried the identical guard, and those ARE reached from the authorization path;
    /// this filter is nullable, so the test is on the value rather than on the presence.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(4)]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Catalogue_RefusesAKeyFilterThatIsNotADefinedMember(int undefinedKey)
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            (PermissionKey)undefinedKey,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue("an undefined key must not be answered with a catalogue");
        result.Reason!.Code.Should().Be(KeyInvalidCode);
        result.Reason!.Message.Should().Contain(
            undefinedKey.ToString(CultureInfo.InvariantCulture),
            "the refusal names the value the caller sent");

        // Refused before any store is touched, and - decisively - the undefined value is never projected
        // into an answer. This is the assertion that pins the fabrication down: the unscoped branch used to
        // return the filter itself, so a caller received the undefined number back as a declared key.
        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Permissions.Verify(
            permissions => permissions.GetByCodeAndKeyAsync(
                It.IsAny<string>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An undefined key filter is refused on the SCOPED branches too, not only on the unscoped one.
    /// </summary>
    /// <remarks>
    /// The unscoped branch fabricated an answer, which is the loudest symptom, but the scoped branches used
    /// the undefined value as a comparison operand and answered with an empty set - a quieter wrong answer
    /// that reads as "that key is declared nowhere" rather than as "that key does not exist". The guard is
    /// placed at the entry to the member so that all three branches are covered by one test, and both scoped
    /// forms are exercised here to prove it.
    /// </remarks>
    [Fact]
    public async Task Catalogue_RefusesAnUndefinedKeyFilterOnEveryScopedBranch()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> withinDefinition = await harness.Service.GetPermissionKeysAsync(
            null,
            EntryModuleDefinitionId,
            (PermissionKey)99,
            CancellationToken.None);

        withinDefinition.IsFailure.Should().BeTrue();
        withinDefinition.Reason!.Code.Should().Be(KeyInvalidCode);

        Result<IReadOnlyList<string>> withinCode = await harness.Service.GetPermissionKeysAsync(
            "SYSTEM_MODULE_DEFINITION",
            null,
            (PermissionKey)99,
            CancellationToken.None);

        withinCode.IsFailure.Should().BeTrue();
        withinCode.Reason!.Code.Should().Be(KeyInvalidCode);
    }

    /// <summary>
    /// Every defined member is accepted as a filter, so the membership guard bounds the enumeration without
    /// narrowing it.
    /// </summary>
    /// <param name="definedKey">A defined member of the enumeration.</param>
    /// <remarks>
    /// The counterweight to the refusals above. A guard that refused a legitimate member would make the key
    /// filter unusable, so all four members are asserted explicitly.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public async Task Catalogue_AcceptsEveryDefinedKeyFilter(PermissionKey definedKey)
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            definedKey,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(definedKey.ToString());
    }

    /// <summary>
    /// A key filter supplied with no other filter is answered from the closed enumeration without touching
    /// the store at all.
    /// </summary>
    /// <remarks>
    /// A DEFINED key needs no lookup: it is a member of the catalogue's key vocabulary by construction, so
    /// querying would only risk reporting a key as absent because no row happened to declare it. That
    /// reasoning holds only because the member now tests membership on entry - it was previously offered as
    /// the reason no test was needed, and an undefined value was echoed straight back.
    /// </remarks>
    [Fact]
    public async Task Catalogue_AnswersALoneKeyFilterFromTheClosedKeySet()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            null,
            null,
            PermissionKey.VIEW,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal("VIEW");

        harness.Permissions.Verify(
            permissions => permissions.GetByModuleDefinitionIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A catalogue definition is returned in full by its own identifier.
    /// </summary>
    /// <remarks>
    /// Restores <c>PermissionController.GetPermission(permissionID)</c>
    /// (<c>PermissionController.vb:L30</c>). The record rather than the bare key is what a caller needs: a
    /// key alone cannot say which scope code or which module definition declared it, and the same key is
    /// declared repeatedly across scopes.
    /// </remarks>
    [Fact]
    public async Task Definition_IsReturnedInFullByItsOwnIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue = [Entry(PermissionKey.EDIT)];

        Result<PermissionDto?> result = await harness.Service
            .GetPermissionAsync(1, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().NotBeNull();
        result.Value!.PermissionId.Should().Be(1);
        result.Value.PermissionCode.Should().Be("SYSTEM_MODULE_DEFINITION");
        result.Value.ModuleDefId.Should().Be(EntryModuleDefinitionId);
        result.Value.PermissionKey.Should().Be("EDIT", "the key travels as the enumeration member's name");
        result.Value.PermissionName.Should().Be("EDIT");
    }

    /// <summary>
    /// An identifier naming no catalogue row is reported as absent, not as a failure.
    /// </summary>
    [Fact]
    public async Task Definition_ReportsAnUnknownIdentifierAsAbsent()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue = [Entry(PermissionKey.VIEW)];

        Result<PermissionDto?> result = await harness.Service
            .GetPermissionAsync(4242, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("absence is an answer, not a failure");
        result.Value.Should().BeNull();
    }

    /// <summary>
    /// The module-scoped read answers with the union the terminal statement takes, not with the module's own
    /// definition alone.
    /// </summary>
    /// <remarks>
    /// Measured from <c>GetPermissionsByModuleID</c> (04.05.03), whose body is
    /// <c>WHERE ModuleDefID = (SELECT ModuleDefID FROM Modules WHERE ModuleID = @ModuleID)
    /// OR PermissionCode = 'SYSTEM_MODULE_DEFINITION'</c>. Dropping the second arm would silently narrow
    /// the answer for every module in the installation.
    /// </remarks>
    [Fact]
    public async Task ModuleDefinitions_AnswerWithTheUnionOfTheDefinitionAndTheProductWideScope()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 1,
                PermissionCode = "MY_MODULE",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.EDIT,
                PermissionName = "Edit",
            },
            new Permission
            {
                PermissionId = 2,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = 99,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
            new Permission
            {
                PermissionId = 3,
                PermissionCode = "SYSTEM_TAB",
                ModuleDefinitionId = 99,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
        ];

        Result<IReadOnlyList<PermissionDto>> result = await harness.Service
            .GetModulePermissionDefinitionsAsync(ModuleId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Select(row => row.PermissionId).Should().Equal(
            new[] { 1, 2 },
            "the module's own definition contributes the first, the product-wide scope the second, "
                + "and the page scope belongs to neither arm");
    }

    /// <summary>
    /// The page-scoped read answers with the page scope, and ignores its page argument entirely.
    /// </summary>
    /// <remarks>
    /// This pins a legacy QUIRK rather than a design: the terminal <c>GetPermissionsByTabID</c> (04.05.03)
    /// never references <c>@TabID</c>, so every page receives the identical catalogue. Two different pages
    /// are asked here precisely so that the equality of the two answers is recorded as intended behaviour;
    /// if a later change made the argument meaningful, this test would fail and demand a decision rather
    /// than passing silently.
    /// </remarks>
    [Fact]
    public async Task TabDefinitions_AnswerWithThePageScopeAndIgnoreThePageArgument()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 5,
                PermissionCode = "SYSTEM_TAB",
                ModuleDefinitionId = -1,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
            new Permission
            {
                PermissionId = 6,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.EDIT,
                PermissionName = "Edit",
            },
        ];

        Result<IReadOnlyList<PermissionDto>> first = await harness.Service
            .GetTabPermissionDefinitionsAsync(TabId, CancellationToken.None);
        Result<IReadOnlyList<PermissionDto>> second = await harness.Service
            .GetTabPermissionDefinitionsAsync(SecondTabId, CancellationToken.None);

        first.IsSuccess.Should().BeTrue(first.Reason?.ToString());
        first.Value.Select(row => row.PermissionId).Should().Equal(5);
        second.Value.Select(row => row.PermissionId).Should().Equal(
            first.Value.Select(row => row.PermissionId),
            "the terminal statement never references the page argument");
    }

    /// <summary>
    /// The identifying reads answer each definition once, in identifier order.
    /// </summary>
    /// <remarks>
    /// Ordering and distinctness are promises the application contract makes, so they are asserted against
    /// the contract rather than left to whatever shape the store query happens to have.
    /// </remarks>
    [Fact]
    public async Task ModuleDefinitions_AnswerEachDefinitionOnceInIdentifierOrder()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            new Permission
            {
                PermissionId = 9,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View",
            },
            new Permission
            {
                PermissionId = 4,
                PermissionCode = "SYSTEM_MODULE_DEFINITION",
                ModuleDefinitionId = EntryModuleDefinitionId,
                PermissionKey = PermissionKey.EDIT,
                PermissionName = "Edit",
            },
        ];

        Result<IReadOnlyList<PermissionDto>> result = await harness.Service
            .GetModulePermissionDefinitionsAsync(ModuleId, CancellationToken.None);

        result.Value.Select(row => row.PermissionId).Should().Equal(new[] { 4, 9 });
    }

    /// <summary>
    /// A tenant-wide question is answered by the tenant-wide query alone.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_ForTheWholeTenantUseTheTenantWideQuery()
    {
        Harness harness = Harness.Ready();
        harness.PortalKeys = ["view", "EDIT"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "VIEW" });
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectivePortalPermissionKeysAsync(
                PortalId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// An unknown tenant is reported as missing.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_RefuseAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.Portals
            .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(PortalNotFoundCode);
    }

    /// <summary>
    /// An account that is not a member of the tenant cannot be evaluated, and both identifiers are named.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_RefuseAnAccountThatIsNotAMemberOfTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(UserNotFoundCode);
        result.Reason!.Message.Should().Contain("7");
        result.Reason!.Message.Should().Contain("-1");
    }

    /// <summary>
    /// An unidentified caller is evaluated under the two pseudo-roles and no account is read.
    /// </summary>
    /// <remarks>
    /// The legacy schema records these grants against reserved role identifiers rather than against real
    /// rows, so an anonymous visitor is a first-class subject of evaluation rather than an absence of one.
    /// Both names are supplied, in that order, because a grant may be recorded against either.
    /// </remarks>
    [Fact]
    public async Task EffectiveKeys_ForAnUnidentifiedCallerUseThePseudoRoles()
    {
        Harness harness = Harness.Ready();

        await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            userId: null,
            cancellationToken: CancellationToken.None);

        harness.CapturedRoleNames.Should().Equal(new[] { AllUsersRoleName, UnauthenticatedRoleName });
        harness.Users.Verify(
            users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Users.Verify(
            users => users.ListRoleNamesAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A member is evaluated under its assigned roles plus the everyone role, as of the current instant.
    /// </summary>
    /// <remarks>
    /// The instant matters because a role assignment carries effective and expiry dates. Reading the
    /// assignments as of the clock rather than as of no particular time is what makes a lapsed assignment
    /// stop granting anything.
    /// </remarks>
    [Fact]
    public async Task EffectiveKeys_ForAMemberAppendTheEveryoneRole()
    {
        Harness harness = Harness.Ready();
        harness.AssignedRoles = ["Subscribers", "Registered Users"];

        await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            cancellationToken: CancellationToken.None);

        harness.CapturedRoleNames.Should().Equal(
            new[] { "Subscribers", "Registered Users", AllUsersRoleName });
        harness.Users.Verify(
            users => users.ListRoleNamesAsync(PortalId, UserId, Now, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The pseudo-role names come from configuration rather than from a literal.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_UseTheConfiguredPseudoRoleNames()
    {
        Harness harness = Harness.Ready();
        harness.PortalOptions.AllUsersRoleName = "Tout le monde";
        harness.PortalOptions.UnauthenticatedRoleName = "Visiteurs";

        await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            userId: null,
            cancellationToken: CancellationToken.None);

        harness.CapturedRoleNames.Should().Equal(new[] { "Tout le monde", "Visiteurs" });
    }

    /// <summary>
    /// A host account holds the whole catalogue and no scope is consulted.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_ForAHostAccountAreTheWholeCatalogue()
    {
        Harness harness = Harness.Ready();
        harness.Catalogue =
        [
            Entry(PermissionKey.VIEW),
            Entry(PermissionKey.EDIT),
            Entry(PermissionKey.READ),
            Entry(PermissionKey.WRITE),
        ];
        harness.Account.IsSuperUser = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT", "READ", "VIEW", "WRITE" });
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectivePortalPermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A host account is answered before any scope is resolved, so an unresolvable scope is not reported.
    /// </summary>
    /// <remarks>
    /// This is a consequence of the order in which the checks run, and it is the desired one: the answer for
    /// a host account is the same for every scope, so resolving the scope could only turn a correct answer
    /// into a not-found. It also means the endpoint cannot be used by a host account to discover which
    /// module identifiers exist, which is consistent with the refusal-not-absence rule the protected routes
    /// follow.
    /// <para>
    /// MIGRATION: a host account is answered with the whole closed key set rather than with whichever
    /// keys the catalogue table happens to contain. That reproduces the legacy rule rather than departing
    /// from it: PortalSecurity.IsInRoles returned true for a host account at PortalSecurity.vb:L123
    /// before examining a single grant, so the legacy answer was "everything" and was never derived from
    /// the catalogue at all. Since Permission.PermissionKey IS the closed enumeration, the enumeration is
    /// what "everything" means, and it is also the only answer the permission repository's contract can
    /// support - that contract mirrors the legacy provider blocks, which offered no unfiltered catalogue
    /// read. A catalogue-derived answer would additionally under-report on any installation whose
    /// catalogue is missing a row, which is the one case where the two answers differ and the one case
    /// where "everything" must not shrink.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EffectiveKeys_ForAHostAccountIgnoreAnUnresolvableScope()
    {
        Harness harness = Harness.Ready();
        harness.Account.IsSuperUser = true;
        harness.Catalogue = [Entry(PermissionKey.VIEW)];
        harness.Modules
            .Setup(modules => modules.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Module?)null);

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            moduleId: 987654,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT", "READ", "VIEW", "WRITE" },
            "a host account holds every key, and the single-entry catalogue fixture must not narrow that");

        // The scope was never resolved, which is the property this test exists to pin.
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A module-scoped question is answered by the module query.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_ForAModuleUseTheModuleQuery()
    {
        Harness harness = Harness.Ready();
        harness.ModuleKeys = ["EDIT"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT" });
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                ModuleId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A module belonging to another tenant is reported as missing.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_RefuseAModuleFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.Module.PortalId = OtherPortalId;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(ModuleNotFoundCode);
    }

    /// <summary>
    /// A module owned by the installation rather than a tenant is evaluable in any tenant.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_AcceptAnInstallationOwnedModuleInAnyTenant()
    {
        Harness harness = Harness.Ready();
        harness.Module.PortalId = null;
        harness.ModuleKeys = ["VIEW"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            OtherPortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>
    /// A page-scoped question is answered by the page query.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_ForAPageUseThePageQuery()
    {
        Harness harness = Harness.Ready();
        harness.TabKeys = ["VIEW"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            tabId: TabId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "VIEW" });
        harness.Evaluator.Verify(
            evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                TabId,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A page belonging to another tenant is reported as missing.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_RefuseAPageFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.Tab.PortalId = OtherPortalId;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            tabId: TabId,
            cancellationToken: CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(TabNotFoundCode);
    }

    /// <summary>
    /// A page owned by the installation rather than a tenant is evaluable in any tenant.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_AcceptAnInstallationOwnedPageInAnyTenant()
    {
        Harness harness = Harness.Ready();
        harness.Tab.PortalId = null;
        harness.TabKeys = ["EDIT"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            OtherPortalId,
            UserId,
            tabId: TabId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(new[] { "EDIT" });
    }

    /// <summary>
    /// Asking about a module on a page unions both scopes.
    /// </summary>
    [Fact]
    public async Task EffectiveKeys_ForAModuleOnAPageUnionBothScopes()
    {
        Harness harness = Harness.Ready();
        harness.ModuleKeys = ["EDIT"];
        harness.TabKeys = ["VIEW", "EDIT"];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            ModuleId,
            TabId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT", "VIEW" },
            "a key granted in both scopes appears once");
    }

    /// <summary>
    /// A module that inherits its view permission is not viewable when no page it sits on grants view.
    /// </summary>
    /// <remarks>
    /// This is the deny-over-allow rule this layer owns. The module's own grants say view is allowed; the
    /// module is nevertheless not viewable, because a module that inherits defers the decision to the pages
    /// it sits on and none of them allows it. Every other key the module grants is unaffected.
    /// </remarks>
    [Fact]
    public async Task InheritedView_IsWithheldWhenNoPlacementPageGrantsIt()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleKeys = ["VIEW", "EDIT"];
        harness.PageViewGrants[TabId] = false;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Equal(
            new[] { "EDIT" },
            "the module allows view and the page it sits on does not, and the page decides");
    }

    /// <summary>
    /// A module that inherits its view permission is viewable when the page it sits on grants view.
    /// </summary>
    [Fact]
    public async Task InheritedView_IsGrantedWhenAPlacementPageGrantsIt()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleKeys = ["EDIT"];
        harness.PageViewGrants[TabId] = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(
            new[] { "EDIT", "VIEW" },
            "view is contributed by the page even though the module's own grants never mention it");
    }

    /// <summary>
    /// A module that does not inherit keeps its own view grant and no page is consulted.
    /// </summary>
    /// <param name="inherits">The stored inheritance flag.</param>
    /// <remarks>
    /// The flag is nullable in the schema, and an unrecorded value is not an inheriting module: the legacy
    /// default was to hold the module's own grants, so an absent flag must behave as "does not inherit"
    /// rather than as "inherit".
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task InheritedView_IsNotConsultedWhenTheModuleDoesNotInherit(bool? inherits)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = inherits;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.ModuleKeys = ["VIEW"];
        harness.PageViewGrants[TabId] = false;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(new[] { "VIEW" });
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Inheritance stops asking as soon as one page withholds view, because the unaddressed question is
    /// answered by every placement at once.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This test measured the opposite short circuit before the placement-insensitivity defect was corrected:
    /// it stopped at the first page that GRANTED, which is the disjunction that let a permissive placement
    /// admit callers to a restrictive one. The conjunction settles on the first page that WITHHOLDS instead,
    /// so the remaining pages are still not worth a round trip — but the outcome it settles on is the safe one.
    /// </remarks>
    [Fact]
    public async Task InheritedView_StopsAtTheFirstWithholdingPage()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.ModuleKeys = [];
        harness.PageViewGrants[TabId] = false;
        harness.PageViewGrants[SecondTabId] = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().BeEmpty();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                SecondTabId,
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "one withholding page settles it, so the rest are not worth a round trip");
    }

    /// <summary>
    /// A question that names a module but no page is answered by EVERY placement at once, so a module placed
    /// on one permissive and one restrictive page is not viewable.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// This is the half of the placement defect that no route could otherwise reach. Every module route in
    /// this application addresses a module WITHOUT naming a page, so had the unaddressed case kept granting
    /// when any placement granted, the escalation would have remained fully exploitable however carefully the
    /// addressed case was decided: place a module on a public page and again on a restricted one, and the
    /// public placement would answer for both.
    /// </para>
    /// <para>
    /// The conjunction is the only collective reading that cannot exceed the answer for an individual
    /// placement. A caller genuinely entitled to the permissive placement names it, and is then decided by
    /// that page alone.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task InheritedView_WithoutAnAddressedPlacementRequiresEveryPlacementToGrant()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<bool> collective = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        collective.IsSuccess.Should().BeTrue(collective.Reason?.ToString());
        collective.Value.Should().BeFalse(
            "a question that names no page is about the module wherever it sits, so the restrictive "
            + "placement decides");

        // Naming the permissive placement is how a caller entitled to it asks, and that answer is affirmative.
        Result<bool> addressed = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: TabId,
            placementTabModuleId: null,
            CancellationToken.None);

        addressed.Value.Should().BeTrue();
    }

    /// <summary>
    /// A placement addressed by its own key is decided by the page that placement sits on, so the restrictive
    /// placement of a doubly-placed module is refused while the permissive one is allowed.
    /// </summary>
    /// <param name="addressedTabModuleId">The placement key the caller names.</param>
    /// <param name="expected">Whether the page that placement sits on grants view.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the form of address the module resource itself uses — <c>ModulesController.GetAsync</c> accepts
    /// <c>tabModuleId</c> as a query field — so it is the form a real request arrives in, and reading it is
    /// what lets a caller entitled to one particular placement be decided by that placement rather than by the
    /// stricter collective answer. It is the more precise of the two forms because a module may be placed on
    /// the same page more than once, which a page identifier alone could not distinguish.
    /// </remarks>
    [Theory]
    [InlineData(FirstPlacementId, true)]
    [InlineData(SecondPlacementId, false)]
    public async Task HasModulePermission_ForAPlacementAddressedByItsOwnKeyIsDecidedByThatPlacementsPage(
        int addressedTabModuleId,
        bool expected)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId, FirstPlacementId));
        harness.Module.TabModules.Add(Placement(SecondTabId, SecondPlacementId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: addressedTabModuleId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().Be(expected);
    }

    /// <summary>
    /// A placement key the module does not occupy is refused rather than answered from any other placement.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Falling back would reinstate the collective answer for a request that asked a specific question, and
    /// would let a caller enumerate a module's placements by observing which keys answer affirmatively.
    /// </remarks>
    [Fact]
    public async Task HasModulePermission_ForAPlacementKeyTheModuleDoesNotOccupyIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId, FirstPlacementId));
        harness.PageViewGrants[TabId] = true;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: SecondPlacementId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeFalse("the module does not occupy the placement the request addressed");
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "no page is consulted, because the addressed placement does not exist to consult one for");
    }

    /// <summary>
    /// Addressing a placement by its own key and simultaneously naming a page that placement does not sit on
    /// is a contradiction, and is refused rather than resolved in favour of either.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Whichever of the two were preferred would be preferred for the permissions it carries and not for what
    /// the request meant, which is exactly the shape of a confused-deputy decision. Refusing is also the only
    /// answer that cannot be widened by adding a second, contradictory field to a request.
    /// </remarks>
    [Fact]
    public async Task HasModulePermission_ForContradictoryFormsOfAddressIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId, FirstPlacementId));
        harness.Module.TabModules.Add(Placement(SecondTabId, SecondPlacementId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = true;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: SecondTabId,
            placementTabModuleId: FirstPlacementId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeFalse(
            "the placement key names one page and the page identifier names another, and both pages grant "
            + "view, so a permissive answer here would be an answer to neither question that was asked");
    }

    /// <summary>
    /// Naming a placement by its own key together with the page it really does sit on is consistent, and is
    /// decided by that page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task HasModulePermission_ForAgreeingFormsOfAddressIsDecidedByTheNamedPage()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId, FirstPlacementId));
        harness.Module.TabModules.Add(Placement(SecondTabId, SecondPlacementId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: TabId,
            placementTabModuleId: FirstPlacementId,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeTrue();
    }

    /// <summary>
    /// A module that inherits its view permission and sits on no page at all is not viewable.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The conjunction over an empty set is vacuously true, so this case has to be stated rather than left to
    /// the loop: a module that takes its view permission from its pages and has no pages inherits nothing, and
    /// must not thereby become visible to everyone — which is the exact shape of an accidental world-readable
    /// grant.
    /// </remarks>
    [Fact]
    public async Task InheritedView_IsWithheldFromAModuleThatSitsOnNoPage()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Clear();
        harness.ModuleKeys = ["VIEW", "EDIT"];

        Result<bool> outcome = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Value.Should().BeFalse("there is no page for the module to inherit a view grant from");
    }

    /// <summary>
    /// An inheriting module placed on two pages is decided by the page the caller ADDRESSED, so a placement
    /// that denies view is denied even while the module's other placement allows it.
    /// </summary>
    /// <param name="addressedTabId">The placement the caller names.</param>
    /// <param name="expected">Whether that placement grants view.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the case an earlier revision answered wrongly. It unioned every placement, so the permissive
    /// placement admitted callers to the restrictive one - a module on a public page and again on a private
    /// page became viewable on the private page by everybody. Both directions are asserted, because a fix
    /// that merely denied more would be just as wrong as the union.
    /// </remarks>
    [Theory]
    [InlineData(TabId, true)]
    [InlineData(SecondTabId, false)]
    public async Task HasModulePermission_ForAnAddressedPlacementIsDecidedByThatPageAlone(
        int addressedTabId,
        bool expected)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: addressedTabId,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Be(expected);

        // The other placement is never consulted: consulting it is what produced the union.
        int otherTabId = addressedTabId == TabId ? SecondTabId : TabId;
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                otherTabId,
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "the decision is about the addressed placement, so no other placement may influence it");
    }

    /// <summary>
    /// Addressing a page the module is not placed on is a denial, and no page's grants are consulted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Falling back to the module's real placements here would reinstate the union through the back door, and
    /// would additionally let a caller map a module's placements by observing which page identifiers answer
    /// affirmatively.
    /// </remarks>
    [Fact]
    public async Task HasModulePermission_ForAPlacementTheModuleDoesNotOccupyIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.PageViewGrants[TabId] = true;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: SecondTabId,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeFalse();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "a module that is not on the addressed page cannot inherit that page's grants");
    }

    /// <summary>
    /// Naming a module and a page together names a placement, so the effective-key listing resolves the
    /// inherited view key from that page alone rather than from every placement.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetEffectivePermissionKeys_ForAModuleAndPageResolvesInheritanceAtThatPlacement()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.ModuleKeys = ["EDIT"];
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<IReadOnlyList<string>> denied = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            tabId: SecondTabId,
            cancellationToken: CancellationToken.None);

        denied.IsSuccess.Should().BeTrue(denied.Reason?.ToString());
        denied.Value.Should().NotContain(
            "VIEW",
            "the addressed placement denies view, and the module's other placement must not supply it");
        denied.Value.Should().Contain("EDIT", "keys other than view are not inherited from the page");
    }

    /// <summary>
    /// Inheritance considers every page until one withholds, so a granting page does not end the traversal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The mirror of the short-circuit test above, and the reason both are kept: that one proves the traversal
    /// STOPS at a withholding page, this one proves it does not stop at a granting one. Together they pin the
    /// conjunction rather than merely one of its outcomes. Before the placement-insensitivity defect was
    /// corrected this test asserted the opposite - that a later granting page rescued an earlier withholding
    /// one - which is precisely the disjunction that made a permissive placement answer for a restrictive one.
    /// </remarks>
    [Fact]
    public async Task InheritedView_ConsidersEveryPageUntilOneWithholds()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.Module.TabModules.Add(Placement(SecondTabId));
        harness.ModuleKeys = [];
        harness.PageViewGrants[TabId] = true;
        harness.PageViewGrants[SecondTabId] = false;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().BeEmpty();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                SecondTabId,
                PermissionKey.VIEW,
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once(),
            "the first page granting view is not the answer, so the second still has to be asked");
    }

    /// <summary>
    /// Inheritance reads the placements when the module was loaded without them.
    /// </summary>
    [Fact]
    public async Task InheritedView_ReadsThePlacementsWhenTheyWereNotLoaded()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.ModuleKeys = [];
        harness.StoredPlacements = [Placement(SecondTabId)];
        harness.PageViewGrants[SecondTabId] = true;

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(new[] { "VIEW" });
        harness.Modules.Verify(
            modules => modules.GetTabModulesByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A module with no placement at all inherits nothing.
    /// </summary>
    [Fact]
    public async Task InheritedView_GrantsNothingWhenTheModuleSitsNowhere()
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.ModuleKeys = ["VIEW", "EDIT"];
        harness.StoredPlacements = [];

        Result<IReadOnlyList<string>> result = await harness.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            UserId,
            moduleId: ModuleId,
            cancellationToken: CancellationToken.None);

        result.Value.Should().Equal(
            new[] { "EDIT" },
            "an unplaced module has no page to inherit from, so its own view grant is still withheld");
    }

    /// <summary>
    /// Asking whether an inheriting module is viewable is answered by the page.
    /// </summary>
    /// <param name="pageGrantsView">Whether the placement page allows view.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HasModulePermission_ForViewOnAnInheritingModuleIsAnsweredByThePage(bool pageGrantsView)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.PageViewGrants[TabId] = pageGrantsView;
        harness.ModuleGrant = !pageGrantsView;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().Be(pageGrantsView);
        harness.Evaluator.Verify(
            evaluator => evaluator.HasModulePermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "an inheriting module's own view grant is not the answer, so it is not even read");
    }

    /// <summary>
    /// Inheritance applies to the view permission only.
    /// </summary>
    /// <param name="key">The permission asked about.</param>
    [Theory]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public async Task HasModulePermission_ForOtherKeysAsksTheModuleEvenWhenItInherits(PermissionKey key)
    {
        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.Module.TabModules.Add(Placement(TabId));
        harness.PageViewGrants[TabId] = false;
        harness.ModuleGrant = true;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            key,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.Value.Should().BeTrue(
            "only the view permission is inherited from the page; editing is decided by the module");
        harness.Evaluator.Verify(
            evaluator => evaluator.HasModulePermissionAsync(
                ModuleId,
                key,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A permission key that names no member of the vocabulary is refused.
    /// </summary>
    /// <remarks>
    /// The keys arrive as an enumeration, and an enumeration in this language accepts any integer of its
    /// underlying type. Without this check an unmapped value would be passed to the store, match nothing,
    /// and be reported as a legitimate refusal rather than as a malformed request.
    /// </remarks>
    [Fact]
    public async Task HasModulePermission_RefusesAKeyThatNamesNoMember()
    {
        Harness harness = Harness.Ready();

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            (PermissionKey)99,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(KeyInvalidCode);
        result.Reason!.Message.Should().Contain("99");
        harness.Modules.Verify(
            modules => modules.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "a malformed request is refused before anything is read");
    }

    /// <summary>
    /// A page question refuses an unrecognised key too.
    /// </summary>
    [Fact]
    public async Task HasTabPermission_RefusesAKeyThatNamesNoMember()
    {
        Harness harness = Harness.Ready();

        Result<bool> result = await harness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            (PermissionKey)(-5),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(KeyInvalidCode);
    }

    /// <summary>
    /// Every defined key is accepted.
    /// </summary>
    /// <param name="key">The permission asked about.</param>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public async Task HasTabPermission_AcceptsEveryDefinedKey(PermissionKey key)
    {
        Harness harness = Harness.Ready();
        harness.TabGrant = true;

        Result<bool> result = await harness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            key,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().BeTrue();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                TabId,
                key,
                UserId,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// An unknown module is reported as missing.
    /// </summary>
    [Fact]
    public async Task HasModulePermission_RefusesAnUnknownModule()
    {
        Harness harness = Harness.Ready();
        harness.Modules
            .Setup(modules => modules.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Module?)null);

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(ModuleNotFoundCode);
    }

    /// <summary>
    /// An unknown page is reported as missing.
    /// </summary>
    [Fact]
    public async Task HasTabPermission_RefusesAnUnknownPage()
    {
        Harness harness = Harness.Ready();
        harness.Tabs
            .Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tab?)null);

        Result<bool> result = await harness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(TabNotFoundCode);
    }

    /// <summary>
    /// An account that is not a member of the tenant is answered no rather than reported as missing.
    /// </summary>
    /// <remarks>
    /// The asymmetry with the effective-keys question is deliberate. "May this account do this?" always has
    /// an answer, and for a non-member the answer is no; turning it into a failure would make the gate
    /// harder to use and would leak the fact that the identifier names nobody. "What may this account do?"
    /// has no answer at all for a non-member, so it fails.
    /// </remarks>
    [Fact]
    public async Task HasPermission_AnswersNoForAnAccountThatIsNotAMember()
    {
        Harness moduleHarness = Harness.Ready();
        moduleHarness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<bool> moduleAnswer = await moduleHarness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        moduleAnswer.IsSuccess.Should().BeTrue();
        moduleAnswer.Value.Should().BeFalse();

        Harness tabHarness = Harness.Ready();
        tabHarness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<bool> tabAnswer = await tabHarness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        tabAnswer.IsSuccess.Should().BeTrue();
        tabAnswer.Value.Should().BeFalse();
    }

    /// <summary>
    /// A host account is allowed without the grants being read at all.
    /// </summary>
    [Fact]
    public async Task HasPermission_AllowsAHostAccountWithoutReadingAnyGrant()
    {
        Harness harness = Harness.Ready();
        harness.Account.IsSuperUser = true;
        harness.ModuleGrant = false;
        harness.TabGrant = false;

        Result<bool> moduleAnswer = await harness.Service.HasModulePermissionAsync(
            PortalId,
            HostUserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        Result<bool> tabAnswer = await harness.Service.HasTabPermissionAsync(
            PortalId,
            HostUserId,
            TabId,
            PermissionKey.EDIT,
            CancellationToken.None);

        moduleAnswer.Value.Should().BeTrue();
        tabAnswer.Value.Should().BeTrue();
        harness.Evaluator.Verify(
            evaluator => evaluator.HasModulePermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Evaluator.Verify(
            evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The decision itself is delegated, and the caller's roles travel with the question.
    /// </summary>
    /// <param name="storedAnswer">The decision the store reports.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HasModulePermission_DelegatesTheDecisionWithTheCallersRoles(bool storedAnswer)
    {
        Harness harness = Harness.Ready();
        harness.AssignedRoles = ["Subscribers"];
        harness.ModuleGrant = storedAnswer;

        Result<bool> result = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        result.Value.Should().Be(
            storedAnswer,
            "precedence between an allowance and a denial is reduced where the grants are, and is not "
            + "second-guessed here");
        harness.CapturedRoleNames.Should().Equal(new[] { "Subscribers", AllUsersRoleName });
    }

    /// <summary>
    /// A permission question does not verify the tenant separately.
    /// </summary>
    /// <remarks>
    /// Confirming that the module or page belongs to the tenant is a stronger check than confirming the
    /// tenant exists, and it is one read rather than two. A separate existence check would be dead work on
    /// the hottest path in the application.
    /// </remarks>
    [Fact]
    public async Task HasPermission_DoesNotVerifyTheTenantSeparately()
    {
        Harness harness = Harness.Ready();

        await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            placementTabId: null,
            placementTabModuleId: null,
            CancellationToken.None);

        await harness.Service.HasTabPermissionAsync(
            PortalId,
            UserId,
            TabId,
            PermissionKey.VIEW,
            CancellationToken.None);

        harness.Portals.Verify(
            portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Removing an account's grants refuses an unknown tenant and touches nothing.
    /// </summary>
    [Fact]
    public async Task RemoveGrants_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.Portals
            .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(PortalNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Removing an account's grants refuses an account that is not a member, and deletes nothing.
    /// </summary>
    /// <remarks>
    /// Here a non-member is a failure rather than a no-op, which is the opposite of the permission question
    /// above. A caller asking to remove grants believes the account exists; silently succeeding would report
    /// that grants had been cleaned up when nothing had been examined, which is exactly the kind of quiet
    /// success that leaves stale grants behind.
    /// </remarks>
    [Fact]
    public async Task RemoveGrants_RefusesAnAccountThatIsNotAMember()
    {
        Harness harness = Harness.Ready();
        harness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(UserNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Removing an account's grants delegates the removal.
    /// </summary>
    [Fact]
    public async Task RemoveGrants_DelegatesTheRemoval()
    {
        Harness harness = Harness.Ready();

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        // MIGRATION: both grant tables are cleaned, because the legacy provider declared the removal
        //            as two members over two tables - DeleteModulePermissionsByUserID at core
        //            DataProvider.vb:L296 and DeleteTabPermissionsByUserID at L305. A grant left
        //            behind on either table would outlive the account that held it.
        harness.Permissions.Verify(
            permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Permissions.Verify(
            permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The removal lands as one unit of work rather than as two independently durable statements.
    /// </summary>
    [Fact]
    public async Task RemoveGrants_CommitsBothTablesInOneTransaction()
    {
        Harness harness = Harness.Ready();

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        // MIGRATION: the legacy pair was unprotected. The provider declared transaction members at
        //            Library/Components/Providers/Data/DataProvider.vb:L70-L74 and neither cleanup -
        //            ModulePermissionController.vb:L218 nor TabPermissionController.vb:L209 - invoked
        //            them, so each statement committed alone and a failure between them left the
        //            account's module grants gone and its page grants intact. Both repository members
        //            issue a set-based delete that reaches the store when called rather than when
        //            changes are flushed, so an enclosing transaction is the only thing that makes the
        //            pair atomic.
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.BeginTransactionAsync(
                TransactionIsolation.Default,
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Transaction.Verify(
            transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()),
            Times.Once());

        // Disposal is what rolls an uncommitted transaction back, so it has to happen on every path.
        harness.Transaction.Verify(transaction => transaction.DisposeAsync(), Times.Once());
    }

    /// <summary>
    /// The removal evicts both grant families afterwards, the module family page by page.
    /// </summary>
    [Fact]
    public async Task RemoveGrants_EvictsBothGrantFamilies()
    {
        Harness harness = Harness.Ready();
        harness.PortalTabs =
        [
            new Tab { TabId = TabId, PortalId = PortalId, TabName = "First" },
            new Tab { TabId = TabId + 1, PortalId = PortalId, TabName = "Second" },
        ];

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        // MIGRATION: the legacy cleanups both evicted immediately after these same two deletes -
        //            ModulePermissionController.vb:L220 cleared the module-permission entries of every
        //            tab in the portal and TabPermissionController.vb:L211 cleared the portal's
        //            page-permission entry. An earlier revision dropped both, which left a deleted
        //            account's grants being served from a warm entry: stale authorisation rather than a
        //            stale listing.
        harness.Cache.Verify(cache => cache.InvalidateTabPermissions(PortalId), Times.Once());

        // MIGRATION: the page family is portal-keyed and the module family is TAB-keyed, so the
        //            portal-wide clear is expressed by naming each page in turn. That is the breadth
        //            ICacheService documents its narrow members as replacing, and it is what the legacy
        //            private ClearPermissionCache(moduleId) did internally at
        //            ModulePermissionController.vb:L62-L66 - resolve the module, then clear by its
        //            owning TabID. No new cache member is invented for it.
        harness.Cache.Verify(cache => cache.InvalidateModulePermissions(TabId), Times.Once());
        harness.Cache.Verify(cache => cache.InvalidateModulePermissions(TabId + 1), Times.Once());
    }

    /// <summary>
    /// A removal refused before it starts neither opens a transaction nor evicts anything.
    /// </summary>
    [Fact]
    public async Task RemoveGrants_TouchesNothingWhenThePortalIsUnknown()
    {
        Harness harness = Harness.Ready();
        harness.Portals
            .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result result = await harness.Service.DeleteUserPermissionsAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(PortalNotFoundCode);
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.BeginTransactionAsync(
                It.IsAny<TransactionIsolation>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Cache.Verify(cache => cache.InvalidateTabPermissions(It.IsAny<int>()), Times.Never());
        harness.Cache.Verify(cache => cache.InvalidateModulePermissions(It.IsAny<int>()), Times.Never());
    }

    /// <summary>
    /// Two contradictory forms of addressing a placement are refused for EVERY key, not only for view.
    /// </summary>
    [Fact]
    public async Task ContradictoryPlacementAddresses_AreRefusedForANonViewKey()
    {
        // THE REGRESSION THIS PINS. The agreement rule used to be enforced only inside the
        // inherited-view branch, so a contradictory pair asking about any other key never reached it and
        // was answered from the module's own grants as though no placement had been named at all. The
        // contract states the rule without qualification, and refusing is what the request meant:
        // choosing either address would be choosing whichever granted more.
        const int placementTabModuleId = 900;
        const int otherTabId = TabId + 5;

        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = false;
        harness.StoredPlacements =
        [
            new TabModule { TabModuleId = placementTabModuleId, TabId = TabId, ModuleId = ModuleId },
        ];
        harness.Evaluator
            .Setup(evaluator => evaluator.HasModulePermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Success(true));

        Result<bool> agreeing = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            TabId,
            placementTabModuleId,
            CancellationToken.None);

        Result<bool> contradicting = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.EDIT,
            otherTabId,
            placementTabModuleId,
            CancellationToken.None);

        agreeing.IsSuccess.Should().BeTrue(agreeing.Reason?.ToString());
        agreeing.Value.Should().BeTrue("the two addresses agree, so the module's own grant decides");

        contradicting.IsSuccess.Should().BeTrue(
            "a refusal is a denial - the contract publishes no failure code for a contradiction");
        contradicting.Value.Should().BeFalse(
            "the named placement does not sit on the named page, so the request contradicts itself");
    }

    /// <summary>
    /// The same contradiction is still refused on the inherited-view path it was originally enforced on.
    /// </summary>
    [Fact]
    public async Task ContradictoryPlacementAddresses_AreStillRefusedForInheritedView()
    {
        const int placementTabModuleId = 901;
        const int otherTabId = TabId + 6;

        Harness harness = Harness.Ready();
        harness.Module.InheritViewPermissions = true;
        harness.StoredPlacements =
        [
            new TabModule { TabModuleId = placementTabModuleId, TabId = TabId, ModuleId = ModuleId },
        ];
        harness.Evaluator
            .Setup(evaluator => evaluator.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Success(true));

        Result<bool> contradicting = await harness.Service.HasModulePermissionAsync(
            PortalId,
            UserId,
            ModuleId,
            PermissionKey.VIEW,
            otherTabId,
            placementTabModuleId,
            CancellationToken.None);

        contradicting.IsSuccess.Should().BeTrue(contradicting.Reason?.ToString());
        contradicting.Value.Should().BeFalse("hoisting the check must not weaken the path it came from");
    }

    /// <summary>
    /// A cached catalogue read asks for the legacy lifetime: twenty minutes times the multiplier.
    /// </summary>
    [Fact]
    public async Task CatalogueRead_RequestsTheLegacyLifetime()
    {
        Harness harness = Harness.Ready();
        harness.CachingOptions.PerformanceMultiplier = 3;

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            cancellationToken: CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());

        // MIGRATION: both legacy permission timeouts were 20 and each was multiplied by the
        //            installation-wide performance setting at its point of use
        //            (ModulePermissionController.vb:L177 and L315, TabPermissionController.vb:L285).
        //            The shipped multiplier is 3, so the lifetime is an hour.
        harness.CacheLifetimesRequested.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromMinutes(60));

        // The catalogue gets its own key family; reusing a legacy grant key would collide with the grant
        // eviction that ICacheService targets at that exact name.
        harness.CacheKeysRequested.Should().ContainSingle()
            .Which.Should().StartWith("PermissionCatalogueKeys|");
    }

    /// <summary>
    /// A multiplier of zero bypasses the cache rather than writing an entry that expires at once.
    /// </summary>
    [Fact]
    public async Task CatalogueRead_BypassesTheCacheWhenCachingIsDisabled()
    {
        Harness harness = Harness.Ready();
        harness.CachingOptions.PerformanceMultiplier = 0;

        Result<IReadOnlyList<string>> result = await harness.Service.GetPermissionKeysAsync(
            cancellationToken: CancellationToken.None);

        // MIGRATION: the legacy writes were conditioned on the product being positive
        //            (ModulePermissionController.vb:L183, TabPermissionController.vb:L290), so a zero
        //            multiplier meant "do not cache". The store is still reached - only the cache is
        //            skipped - because an entry with a zero lifetime is a write, an eviction and a miss
        //            where the configuration asked for none of them.
        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.CacheKeysRequested.Should().BeEmpty();
        harness.Cache.Verify(
            cache => cache.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<string>>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The two definition reads are cached under their own key families, keyed by their own subject.
    /// </summary>
    [Fact]
    public async Task DefinitionReads_AreCachedUnderTheirOwnKeyFamilies()
    {
        Harness harness = Harness.Ready();

        _ = await harness.Service.GetModulePermissionDefinitionsAsync(ModuleId, CancellationToken.None);
        _ = await harness.Service.GetTabPermissionDefinitionsAsync(TabId, CancellationToken.None);

        harness.CacheKeysRequested.Should().HaveCount(2);
        harness.CacheKeysRequested[0].Should().Be(
            FormattableString.Invariant($"PermissionDefinitionsByModule|{ModuleId}"));

        // The page identifier is part of the key even though the read behind it ignores the argument,
        // so a later product revision that makes the page distinction real cannot serve one page's
        // answer for another.
        harness.CacheKeysRequested[1].Should().Be(
            FormattableString.Invariant($"PermissionDefinitionsByTab|{TabId}"));
    }

    /// <summary>
    /// A host account is resolvable outside the tenant, and an ordinary account is not.
    /// </summary>
    [Fact]
    public async Task CallerResolution_AcceptsAHostAccountOutsideTheTenantAndNothingElse()
    {
        Harness host = Harness.Ready();
        host.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId != null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        host.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId == null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = HostUserId,
                Username = "host",
                FirstName = "Host",
                LastName = "Account",
                DisplayName = "Host Account",
                IsSuperUser = true,
            });
        host.Catalogue = [Entry(PermissionKey.VIEW)];

        Result<IReadOnlyList<string>> hostAnswer = await host.Service.GetEffectivePermissionKeysAsync(
            PortalId,
            HostUserId,
            cancellationToken: CancellationToken.None);

        hostAnswer.IsSuccess.Should().BeTrue(hostAnswer.Reason?.ToString());

        // MIGRATION: the host answer is the closed key set, for the reason set out on
        //            EffectiveKeys_ForAHostAccountIgnoreAnUnresolvableScope. What this test pins is that
        //            the host account was RESOLVED from outside the tenant at all; the ordinary account
        //            below is not.
        hostAnswer.Value.Should().Equal(new[] { "EDIT", "READ", "VIEW", "WRITE" });

        Harness outsider = Harness.Ready();
        outsider.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId != null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        outsider.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId == null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = 4242,
                Username = "someone_elses_member",
                FirstName = "Other",
                LastName = "Tenant",
                DisplayName = "Other Tenant",
                IsSuperUser = false,
            });

        Result<IReadOnlyList<string>> outsiderAnswer = await outsider.Service
            .GetEffectivePermissionKeysAsync(PortalId, 4242, cancellationToken: CancellationToken.None);

        outsiderAnswer.IsFailure.Should().BeTrue(
            "the installation-wide fallback exists for host accounts alone");
        outsiderAnswer.Reason!.Code.Should().Be(UserNotFoundCode);
    }

    /// <summary>
    /// Every collaborator is required.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        Mock<IPermissionRepository> permissions = new();
        Mock<IPermissionEvaluator> evaluator = new();
        Mock<IPortalRepository> portals = new();
        Mock<IModuleRepository> modules = new();
        Mock<ITabRepository> tabs = new();
        Mock<IUserRepository> users = new();
        Mock<IUnitOfWork> unitOfWork = new();
        Mock<ICacheService> cache = new();
        Mock<IClock> clock = new();
        PortalOptions options = new();
        CachingOptions caching = new();

        // One case per constructor parameter, each passing null in exactly one position. Written out rather
        // than driven from a loop because the compiler then checks the arity of every case: adding a
        // collaborator without adding its case leaves this test failing to compile rather than silently
        // covering one parameter less than the constructor declares.
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                null!, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                unitOfWork.Object, cache.Object, clock.Object, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, null!, portals.Object, modules.Object, tabs.Object, users.Object,
                unitOfWork.Object, cache.Object, clock.Object, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, null!, modules.Object, tabs.Object, users.Object,
                unitOfWork.Object, cache.Object, clock.Object, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, null!, tabs.Object, users.Object,
                unitOfWork.Object, cache.Object, clock.Object, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, null!, users.Object,
                unitOfWork.Object, cache.Object, clock.Object, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, null!,
                unitOfWork.Object, cache.Object, clock.Object, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                null!, cache.Object, clock.Object, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                unitOfWork.Object, null!, clock.Object, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                unitOfWork.Object, cache.Object, null!, options, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                unitOfWork.Object, cache.Object, clock.Object, null!, caching);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new PermissionService(
                permissions.Object, evaluator.Object, portals.Object, modules.Object, tabs.Object, users.Object,
                unitOfWork.Object, cache.Object, clock.Object, options, null!);
        });
    }

    // =================================================================================================
    // Contract shape.
    //
    // These tests read the three permission contracts through reflection and never construct anything.
    // Reflection over an abstraction is inspection, not reach: it asks what a contract declares without
    // naming a single implementation. The concrete precedence reducer is deliberately NOT reachable from
    // this project and is not reached here by any means - see the coverage-placement note below.
    // =================================================================================================

    /// <summary>
    /// The three permission contracts this project can legitimately see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: coverage placement is a deliberate architectural choice, not an oversight. The concrete
    /// precedence reducer lives in the infrastructure assembly, which this test project does not
    /// reference and must never reference - AAP Rule T1 makes the dependency direction a compile error
    /// rather than a review finding, and AAP section 0.8.3 baseline B1 states the layering is enforced by
    /// the compiler rather than by convention. That class is additionally declared internal to its own
    /// assembly, so it is unreachable twice over: the assembly is off the reference graph, and the type is
    /// not public within it. Algorithm-level verification of the concrete reducer therefore belongs to the
    /// integration project, which reaches it legitimately through its api reference and the container, and
    /// proves it over real rows and real HTTP.
    /// </para>
    /// <para>
    /// What lives here instead is the executable <em>specification</em> that reducer must satisfy, written
    /// against the domain repository contract and the real grant entities. A specification a future
    /// implementer can run is worth more than a description of one, and it costs no layering violation.
    /// </para>
    /// <para>
    /// Note which contract is which. The precedence <em>abstraction</em> is an application-layer contract
    /// and so is fully reachable and legitimately substitutable; only its concrete implementation is
    /// infrastructure. Conflating the two would either forbid a legal test or excuse an illegal one.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Type> PermissionContracts =>
    [
        typeof(IPermissionService),
        typeof(IPermissionRepository),
        typeof(IPermissionEvaluator),
    ];

    /// <summary>
    /// The assemblies a permission contract is allowed to mention on its surface.
    /// </summary>
    /// <remarks>
    /// Described by example rather than by name so the set cannot drift from reality: the two assemblies
    /// this project references, plus the framework assemblies that supply the primitives, tasks and
    /// read-only sequence types the contracts are built from. Anything else - a persistence library, a
    /// mapping library, a web framework, or an infrastructure type - is absent by construction rather
    /// than by a blacklist that a new dependency could slip past.
    /// </remarks>
    private static IReadOnlySet<Assembly> PermittedSurfaceAssemblies =>
        new HashSet<Assembly>
        {
            typeof(Permission).Assembly,
            typeof(IPermissionService).Assembly,
            typeof(object).Assembly,
            typeof(Task).Assembly,
            typeof(IReadOnlyList<>).Assembly,
            typeof(CancellationToken).Assembly,
        };

    /// <summary>
    /// Every method the three permission contracts declare.
    /// </summary>
    /// <returns>The declared methods, paired with the contract that declares them.</returns>
    private static IEnumerable<(Type Contract, MethodInfo Member)> ContractMembers()
        => PermissionContracts.SelectMany(contract => contract
            .GetMethods()
            .Select(member => (Contract: contract, Member: member)));

    /// <summary>
    /// Unwraps a declared return type down to the value it ultimately produces.
    /// </summary>
    /// <param name="declared">The declared return type.</param>
    /// <returns>
    /// The produced value type: <see cref="void"/> for a bare task, and the innermost argument once any
    /// task and outcome wrappers have been peeled away.
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
    /// Expands a type into itself and every generic argument it is built from.
    /// </summary>
    /// <param name="type">The type to expand.</param>
    /// <returns>The type and, recursively, its generic arguments.</returns>
    private static IEnumerable<Type> Flatten(Type type)
    {
        Type subject = type.IsByRef || type.IsArray ? type.GetElementType() ?? type : type;

        yield return subject;

        foreach (Type argument in subject.GetGenericArguments())
        {
            foreach (Type nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Every type that appears on a member's surface, in its parameters or its return value.
    /// </summary>
    /// <param name="member">The member to inspect.</param>
    /// <returns>The surface types.</returns>
    private static IEnumerable<Type> SurfaceTypes(MethodInfo member)
        => member
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Append(member.ReturnType)
            .SelectMany(Flatten);

    /// <summary>
    /// Every permission contract member performs input or output, so every one is a cancellable task.
    /// </summary>
    /// <remarks>
    /// AAP Rule T6 and section 0.8.3 baseline B4: asynchronous throughout, with no synchronous-over-
    /// asynchronous bridging anywhere in the request path. This pins all three halves of that - the task
    /// return, the naming that tells a caller to await it, and the token that lets a caller abandon it.
    /// The token is required to be last because a trailing optional token is the convention the whole
    /// solution is written to; a token buried mid-list is the shape that gets silently dropped at a call
    /// site.
    /// </remarks>
    [Fact]
    public void Contract_EveryMemberIsAnAwaitableCancellableTask()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            string described = $"{contract.Name}.{member.Name}";

            bool producesTask = member.ReturnType == typeof(Task)
                || (member.ReturnType.IsGenericType
                    && member.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));

            producesTask.Should().BeTrue(
                $"{described} reaches a store and must hand back a task rather than a completed value");

            member.Name.Should().EndWith(
                "Async",
                $"{described} returns a task, and the name is what tells a caller at the call site to await it");

            ParameterInfo[] parameters = member.GetParameters();

            parameters.Should().NotBeEmpty($"{described} must at least accept a cancellation token");

            parameters[^1].ParameterType.Should().Be<CancellationToken>(
                $"{described} must take its cancellation token last, which is the convention every caller in this solution relies on");
        }
    }

    /// <summary>
    /// No permission contract member reports anything through an output or reference parameter.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy status-by-reference idiom is gone. AAP section 0.7.4 measured 30 in-scope
    /// mutate-and-report-by-reference signatures and states flatly that no output or reference parameter
    /// appears in any target public api. The replacement is visible in the return types the previous test
    /// pins: an outcome wrapper carries both the produced value and the reason it could not be produced,
    /// so a caller can no longer read a status it forgot to pass a variable for.
    /// </remarks>
    [Fact]
    public void Contract_DeclaresNoOutputOrReferenceParameter()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (ParameterInfo parameter in member.GetParameters())
            {
                string described = $"{contract.Name}.{member.Name}({parameter.Name})";

                parameter.IsOut.Should().BeFalse(
                    $"{described} must report through its return value, not through an output parameter");

                parameter.ParameterType.IsByRef.Should().BeFalse(
                    $"{described} must report through its return value, not by mutating an argument");
            }
        }
    }

    /// <summary>
    /// The persistence surface answers no access question at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single-reducer guard, and it is the assertion that actually protects the architecture.
    /// AAP section 0.5.1.2 relocates permission evaluation out of the three legacy controllers and into a
    /// dedicated component; the repository contract's own documentation states that nothing on it returns
    /// an access decision. Two reducers that can disagree about whether a caller may act is the worst
    /// available outcome in this area, so the way to prevent a second one appearing is to keep the shape
    /// of a decision off the layer that holds the rows. A repository member that started returning a
    /// verdict would be exactly that second reducer, and this test fails the moment one does.
    /// </para>
    /// <para>
    /// Stated as a property of the produced value rather than of the member name, so it cannot be evaded
    /// by choosing a name the guard does not recognise.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_ThePersistenceSurfaceReturnsNoAccessDecision()
    {
        foreach (MethodInfo member in typeof(IPermissionRepository).GetMethods())
        {
            ProducedValue(member.ReturnType).Should().NotBe(
                typeof(bool),
                $"IPermissionRepository.{member.Name} holds rows; deciding what they add up to belongs to exactly one other component");
        }
    }

    /// <summary>
    /// A decision is an outcome carrying a boolean, never a bare boolean.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction this pins is the one that matters most to a caller and is the easiest to lose. A
    /// refusal and a failure are not the same event: "you may not do this" is a successful answer whose
    /// value happens to be false, whereas "the store could not be reached" is a failure with no answer at
    /// all. A bare boolean cannot express the difference and would force the second case to masquerade as
    /// the first, which reads to an operator as a permissions problem when it is an availability problem.
    /// </para>
    /// <para>
    /// This test also replaces a weaker check that could not be written honestly. The decision members do
    /// live on the application contract - deliberately, because the api authorisation handler must have a
    /// single authorised route to ask the question - so asserting that no decision exists there would be
    /// asserting something false. The prohibition that carries the architectural weight is "no second
    /// reducer anywhere", which the previous test enforces where it can actually be broken.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_ADecisionIsAnOutcomeCarryingABooleanNeverABareBoolean()
    {
        MethodInfo[] decisions = typeof(IPermissionService)
            .GetMethods()
            .Where(member => ProducedValue(member.ReturnType) == typeof(bool))
            .ToArray();

        decisions.Should().HaveCount(
            2,
            "a caller asks about a module or about a page, and those are the only two scopes a grant is recorded against");

        foreach (MethodInfo decision in decisions)
        {
            Type produced = decision.ReturnType.GetGenericArguments()[0];

            produced.IsGenericType.Should().BeTrue(
                $"IPermissionService.{decision.Name} must wrap its answer so a refusal and a failure stay distinguishable");

            produced.GetGenericTypeDefinition().Should().Be(
                typeof(Result<>),
                $"IPermissionService.{decision.Name} must return an outcome of boolean, so that a denial is a successful false rather than an error");
        }
    }

    /// <summary>
    /// Every member of the evaluation contract reports through an outcome, not bare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// M-10: this contract previously handed back its verdicts bare - three sequences and two booleans -
    /// which left it able to say the verdict and nothing else. One consequence was concrete rather than
    /// theoretical: a denial and a question about a module or page that does not exist produced byte-for-byte
    /// the same answer, so a caller needing to tell them apart had to read the subject again to discover
    /// which of the two it had been given.
    /// </para>
    /// <para>
    /// What this pins is only the SHAPE. The verdict semantics are asserted elsewhere in this file and are
    /// deliberately unchanged by the conversion: a denial remains a successful outcome carrying false, and
    /// absence still denies. Wrapping is what gives the contract somewhere to put the advisory, not a licence
    /// to start reporting refusals as errors.
    /// </para>
    /// <para>
    /// Asserted over every member by reflection rather than over a written list of five, so a sixth member
    /// added later cannot be introduced bare without failing here.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_TheEvaluatorReportsEveryVerdictThroughAnOutcome()
    {
        MethodInfo[] members = typeof(IPermissionEvaluator).GetMethods();

        members.Should().HaveCount(
            5,
            "the evaluator decides a portal-wide, a module-scoped and a page-scoped listing, plus the two "
            + "single-key verdicts, and nothing else belongs on a decision contract");

        foreach (MethodInfo member in members)
        {
            member.ReturnType.IsGenericType.Should().BeTrue(
                $"IPermissionEvaluator.{member.Name} must be awaitable and carry a value");

            member.ReturnType.GetGenericTypeDefinition().Should().Be(
                typeof(Task<>),
                $"IPermissionEvaluator.{member.Name} performs I/O, so it must return a task");

            Type produced = member.ReturnType.GetGenericArguments()[0];

            produced.IsGenericType.Should().BeTrue(
                $"IPermissionEvaluator.{member.Name} must wrap its verdict so an advisory can travel with it");

            produced.GetGenericTypeDefinition().Should().Be(
                typeof(Result<>),
                $"IPermissionEvaluator.{member.Name} must report through an outcome rather than bare, so that "
                + "\"no such subject\" is distinguishable from \"you hold nothing\" without a second read");
        }
    }

    /// <summary>
    /// Every sequence a permission contract hands back is a read-only generic sequence.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the untyped and pre-generics collection types are gone, and so is any bespoke wrapper.
    /// Six of the nine legacy catalogue members returned an untyped list, and the two grant controllers
    /// were fronted by hand-written pre-generics wrapper classes; AAP section 0.5.1.10 records that those
    /// wrappers produce no target type at all. Asserting the positive shape rather than listing the
    /// banished type names is both stronger and self-maintaining: a newly invented wrapper, a mutable
    /// list, or an untyped sequence all fail this, including ones nobody thought to blacklist.
    /// </remarks>
    [Fact]
    public void Contract_HandsBackOnlyReadOnlyGenericSequences()
    {
        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            Type produced = ProducedValue(member.ReturnType);

            if (produced == typeof(void) || !produced.IsGenericType)
            {
                continue;
            }

            produced.GetGenericTypeDefinition().Should().Be(
                typeof(IReadOnlyList<>),
                $"{contract.Name}.{member.Name} returns a sequence, which must be read-only and generic so a caller can neither mutate the answer nor cast its way out of the element type");
        }
    }

    /// <summary>
    /// A permission contract mentions only domain, application and framework types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the reflection-driven row hydrator and the reflection-created static provider accessor
    /// are both gone, replaced by the object-relational materializer and constructor injection, and
    /// neither leaves a trace on these contracts. This test is what makes that verifiable rather than
    /// merely asserted: it proves no persistence type, no query type, no mapping library type and no
    /// infrastructure type can appear on the surface, because every surface type must come from an
    /// assembly this project already legitimately depends on.
    /// </para>
    /// <para>
    /// Expressed as an allow-list of assemblies rather than a deny-list of names on purpose. A deny-list
    /// only rejects what its author remembered; this rejects everything that was not explicitly permitted,
    /// which includes dependencies that do not exist yet.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contract_MentionsOnlyDomainApplicationAndFrameworkTypes()
    {
        IReadOnlySet<Assembly> permitted = PermittedSurfaceAssemblies;

        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (Type surface in SurfaceTypes(member))
            {
                if (surface.IsGenericParameter)
                {
                    continue;
                }

                permitted.Should().Contain(
                    surface.Assembly,
                    $"{contract.Name}.{member.Name} mentions {surface.Name}, which comes from an assembly these contracts are not permitted to expose");
            }
        }
    }

    /// <summary>
    /// No permission contract carries a member for the excluded file-management subsystem.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the path-scoped catalogue lookup and the eight file-system grant members are not
    /// ported. The subsystem they serve is out of scope, so there is no target feature for a grant keyed
    /// by a storage path to serve, and the target declares no file-system grant entity. The excluded
    /// vocabulary is checked as separate short tokens rather than as the subsystem's full type names,
    /// because this folder is itself checked for those names and quoting them would report the suite as
    /// reintroducing what it proves is absent.
    /// </remarks>
    [Fact]
    public void Contract_CarriesNoMemberForTheExcludedStorageSubsystem()
    {
        string[] excludedVocabulary = ["Folder", "Directory", "Path"];

        foreach ((Type contract, MethodInfo member) in ContractMembers())
        {
            foreach (string token in excludedVocabulary)
            {
                member.Name.Should().NotContain(
                    token,
                    $"{contract.Name}.{member.Name} names a subsystem this migration excludes, so no member should mention it");
            }
        }
    }

    // =================================================================================================
    // The concrete evaluator.
    //
    // Every test in this section constructs DnnMigration.Infrastructure.Security.PermissionEvaluator
    // itself over substituted repositories, so the reachability test, the pseudo-role rules, the
    // catalogue scope admissions and the allow-and-deny reduction are all the production ones.
    //
    // An earlier revision of this file asserted those rules against a private specification class
    // instead, on the ground that the concrete type lived in an assembly this project must not
    // reference, and it additionally specified a grant-replacement rule the application contract does
    // not publish at all. BOTH DECISIONS WERE WRONG and are replaced rather than softened. The
    // replacement tests exercised no production member, so they could not fail however production
    // behaved; and the precedence specification had drifted from the implementation it claimed to
    // describe - it modelled a superuser short circuit and an installation-wide account rule that the
    // real evaluator does not have, and it lacked the scope correlation that the real reduction does
    // have. A specification that can disagree with the code it specifies is worse than no
    // specification, because it reads as evidence.
    //
    // MIGRATION: DENY PRECEDENCE UNIFIES TWO INCONSISTENT LEGACY PATHS. THIS IS A DELIBERATE
    //            BEHAVIOURAL DIVERGENCE, AND IT IS THE HEADLINE FINDING OF THIS FILE.
    //
    //            The legacy source decided the same question two different ways and did not reconcile
    //            them. TabPermissionController.vb:L38-L54 and its module twin walked the grant rows and
    //            returned True on the FIRST row whose principal the caller matched. Neither one tested
    //            the allow-or-deny flag at all, so a row recorded specifically to REFUSE a role the
    //            caller held would GRANT that caller access - the refusal was not merely ignored, it was
    //            read as permission. Meanwhile ModulePermissionController.vb:L239-L252 and its tab twin,
    //            which flattened grants into a delimited string, DID filter on that flag being set.
    //
    //            The two paths met inside one method. PortalSecurity.vb:L521 computed view access from
    //            the flag-FILTERED delimited string, while L522 computed edit access from the UNFILTERED
    //            first-match-wins walk. The measured consequence is that in the legacy application VIEW
    //            honoured the allow-or-deny flag and EDIT silently did not.
    //
    //            The target unifies both paths under one rule: deny beats allow, for every key, WITHIN
    //            THE SCOPE THAT CARRIES THE DENIAL. Every test below pins the TARGET rule, never the
    //            legacy first-match-wins behaviour - so a reader who expects legacy parity here should
    //            read this note as the explanation rather than these tests as a defect.
    //
    // MIGRATION: the suppression is SCOPED rather than global, and that is a second deliberate decision
    //            with a measurable consequence. The portal-wide read spans every module and page of a
    //            tenant, so applying one denial across that whole union would let a single forgotten page
    //            strip a key the caller genuinely holds everywhere else. Two tests below exist only to
    //            pin the correlation, one across two modules and one across a module and a page sharing
    //            an identifier - which they can, because Modules.ModuleID and Tabs.TabID both seed at 0.
    // =================================================================================================

    /// <summary>
    /// The evaluator refuses to be constructed without any one of its five collaborators.
    /// </summary>
    /// <param name="omitted">Which collaborator is withheld.</param>
    /// <remarks>
    /// Five arguments and every one of them earns its place: the grants and the catalogue, the roles a
    /// name resolves through, and the module and page reads that establish which portal owns the scope
    /// under evaluation - without which a role name could only be resolved installation-wide, which is
    /// the cross-tenant escalation the contract forbids. A missing collaborator must fail at
    /// construction rather than produce a decision that quietly consulted less than it should.
    /// </remarks>
    [Theory]
    [InlineData("permissions")]
    [InlineData("roles")]
    [InlineData("modules")]
    [InlineData("tabs")]
    [InlineData("portalOptions")]
    public void Evaluator_RequiresEveryCollaborator(string omitted)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();

        Action construct = () => _ = new PermissionEvaluator(
            omitted == "permissions" ? null! : world.Permissions.Object,
            omitted == "roles" ? null! : world.RoleStore.Object,
            omitted == "modules" ? null! : world.ModuleStore.Object,
            omitted == "tabs" ? null! : world.TabStore.Object,
            omitted == "portalOptions" ? null! : Options.Create(world.Portal));

        construct.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be(omitted);
    }

    /// <summary>
    /// A blank built-in role name is refused at construction rather than left to match nothing.
    /// </summary>
    /// <param name="allUsersRoleName">The configured name standing for every caller.</param>
    /// <param name="unauthenticatedRoleName">The configured name standing for an anonymous caller.</param>
    /// <remarks>
    /// Both values are load-bearing: they are the names by which a caller is taken to stand for every
    /// user or for an unidentified one, and the comparison against a stored role name is exact. A blank
    /// value would therefore stop matching silently, revoking every public grant in the installation
    /// without any error to explain it, which is precisely the failure mode a start-up refusal exists to
    /// convert into a visible one.
    /// </remarks>
    [Theory]
    [InlineData("", UnauthenticatedRoleName)]
    [InlineData("   ", UnauthenticatedRoleName)]
    [InlineData(AllUsersRoleName, "")]
    [InlineData(AllUsersRoleName, "   ")]
    public void Evaluator_RefusesABlankBuiltInRoleName(
        string allUsersRoleName,
        string unauthenticatedRoleName)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.Portal.AllUsersRoleName = allUsersRoleName;
        world.Portal.UnauthenticatedRoleName = unauthenticatedRoleName;

        Action construct = () => _ = world.Build();

        construct.Should().Throw<ArgumentException>().And.ParamName.Should().Be("portalOptions");
    }

    /// <summary>
    /// Every member refuses an absent role-name collection.
    /// </summary>
    /// <remarks>
    /// An empty collection is a legitimate caller - it describes someone holding no named role, who is
    /// still reachable through the everyone, anonymous and account-scoped grants - so absence cannot be
    /// modelled as emptiness and has to be a fault.
    /// </remarks>
    [Fact]
    public async Task EveryMember_RefusesAnAbsentRoleNameCollection()
    {
        IPermissionEvaluator evaluator = EvaluatorWorld.Create().Build();

        await FluentActions
            .Awaiting(() => evaluator.ListEffectivePortalPermissionKeysAsync(PortalId, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveTabPermissionKeysAsync(TabId, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => evaluator.HasTabPermissionAsync(TabId, PermissionKey.VIEW, UserId, null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// An allowing grant the caller reaches confers its key.
    /// </summary>
    [Fact]
    public async Task ModuleKeys_AnAllowingGrantConfersItsKey()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().Equal("VIEW");
    }

    /// <summary>
    /// A denying grant on its own confers nothing, which is the same answer absence produces.
    /// </summary>
    /// <remarks>
    /// Under the legacy first-match-wins walk this row would have GRANTED the key, because that walk
    /// never read the allow-or-deny flag. This test is the point at which the divergence recorded above
    /// becomes executable.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_ADenyingGrantAloneConfersNothing()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: MemberRoleId));

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty();
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
    }

    /// <summary>
    /// A caller reaching neither a catalogue entry nor a grant holds nothing, and is refused rather than
    /// failed.
    /// </summary>
    /// <param name="withCatalogue">Whether the catalogue defines the key at all.</param>
    /// <param name="withGrant">Whether any grant row exists for it.</param>
    /// <remarks>
    /// Two distinct absences, one answer. An empty catalogue means the permission is not defined for
    /// this scope; an empty grant set means it is defined but conferred on nobody. Neither is an error:
    /// a caller asking "may I?" is entitled to be told "no" rather than handed an exception to
    /// interpret, and absence must produce exactly the answer an explicit denial does.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ModuleKeys_WithNoCatalogueEntryOrNoGrantRefuseWithoutFailing(
        bool withCatalogue,
        bool withGrant)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);

        if (withCatalogue)
        {
            world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        }

        if (withGrant)
        {
            world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        }

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty();
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
    }

    /// <summary>
    /// A denial suppresses an allowance of the same key on the same module, in either row order.
    /// </summary>
    /// <param name="denyFirst">Whether the refusing row is returned before the allowing one.</param>
    /// <remarks>
    /// The order-independence proof, and the single most valuable assertion in this section. A grant and
    /// a denial of one key on one scope is a legitimate configuration, so "whichever row came first
    /// wins" would make an access decision depend on a query plan. The implementation collects every
    /// denial in a separate pass before judging any allowance, which is what makes the outcome a
    /// property of the data rather than of its ordering.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModuleKeys_ADenialSuppressesAnAllowanceInEitherRowOrder(bool denyFirst)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));

        ModulePermission allowance = ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId);
        ModulePermission refusal = ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: AllUsersRoleId);

        if (denyFirst)
        {
            world.ModuleGrants.Add(refusal);
            world.ModuleGrants.Add(allowance);
        }
        else
        {
            world.ModuleGrants.Add(allowance);
            world.ModuleGrants.Add(refusal);
        }

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty("a refusal beats an allowance, whichever order the store returned them in");
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
    }

    /// <summary>
    /// A denial recorded on one module leaves the same key intact on another.
    /// </summary>
    /// <remarks>
    /// The suppression is correlated to the scope that carries the denial. Applying it across the
    /// tenant-wide union instead would let one forgotten module strip a key the caller genuinely holds
    /// on every other one, which is a silent revocation rather than a visible configuration.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_ADenialOnOneModuleLeavesTheKeyIntactOnAnother()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithModule(SecondModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: false, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(
            FirstPermissionId,
            allowAccess: true,
            roleId: MemberRoleId,
            moduleId: SecondModuleId));

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]))
            .Should().Equal(
                new[] { "VIEW" },
                "the allowance on the second module survives the refusal on the first");
        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty("while the module carrying the refusal confers nothing");
    }

    /// <summary>
    /// A denial recorded on a page does not suppress the same key on a module that shares its
    /// identifier.
    /// </summary>
    /// <remarks>
    /// Both identity columns seed at zero, so an identifier on its own does not say what it identifies.
    /// This test uses module zero and page zero deliberately: a reduction keyed on the identifier alone
    /// rather than on the pair of kind and identifier would collapse the two scopes and fail here.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_ADenialOnAPageDoesNotSuppressTheSameKeyOnAModuleSharingItsIdentifier()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(ZeroTabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.TabGrants.Add(PageGrantRow(ZeroTabId, FirstPermissionId, allowAccess: false, roleId: MemberRoleId));

        ModuleId.Should().Be(ZeroTabId, "the premise of this test is that the two identifiers collide");

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]));

        keys.Should().Equal(
            new[] { "VIEW" },
            "a refusal on page zero must not reach module zero, because a bare identifier does not say "
            + "what it identifies");
    }

    /// <summary>
    /// The principal matrix: which stored role identifier reaches which caller.
    /// </summary>
    /// <param name="grantedRoleId">The role identifier recorded on the grant row.</param>
    /// <param name="identified">Whether the caller carries an account identifier.</param>
    /// <param name="expected">Whether the row should reach the caller.</param>
    /// <remarks>
    /// <para>
    /// Ported from the legacy membership loop at <c>PortalSecurity.vb:L115-L136</c>: the all-users
    /// pseudo-role was admitted unconditionally at L125 and the unauthenticated one only while the
    /// request was in fact unauthenticated at L124, so the two are genuinely different widths and not
    /// interchangeable. The contract carries no authentication flag of its own - an absent account
    /// identifier <em>is</em> the anonymous caller.
    /// </para>
    /// <para>
    /// The superuser identifier reaches NOBODY here, which is deliberate rather than an omission. No
    /// member of this contract accepts a host-account flag, so admitting <c>-2</c> would have to admit
    /// every caller; a host account is answered by the application service before a grant is read, just
    /// as <c>PortalSecurity.vb:L123</c> answered it before examining a role. The legacy "Nothing" role
    /// <c>-4</c> needs no special case and gets none: <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so
    /// no role name can ever resolve to it and it therefore reaches nobody, which is exactly what it
    /// asks for.
    /// </para>
    /// <para>
    /// The row identified by zero is the case worth stating out loud. Zero is the first role the schema
    /// ever issues and grants exactly like any other, so a future reader who "tidies" this rule into a
    /// positive-identifier test would break the shipped administrators role of a real installation.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(AllUsersRoleId, true, true)]
    [InlineData(AllUsersRoleId, false, true)]
    [InlineData(UnauthenticatedRoleId, false, true)]
    [InlineData(UnauthenticatedRoleId, true, false)]
    [InlineData(SuperUserRoleId, true, false)]
    [InlineData(SuperUserRoleId, false, false)]
    [InlineData(NothingRoleId, true, false)]
    [InlineData(NothingRoleId, false, false)]
    [InlineData(ZeroRoleId, true, true)]
    [InlineData(MemberRoleId, true, true)]
    [InlineData(ForeignRoleId, true, false)]
    public async Task ModuleKeys_MatchAStoredRoleIdentifierAgainstTheCaller(
        int grantedRoleId,
        bool identified,
        bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(ZeroRoleId, ZeroRoleName);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.WithRole(ForeignRoleId, ForeignRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: grantedRoleId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            identified ? UserId : null,
            identified ? [ZeroRoleName, MemberRoleName] : []));

        held.Should().Be(
            expected,
            $"a grant to role {grantedRoleId} against an identified={identified} caller must resolve "
            + "this way");
    }

    /// <summary>
    /// A grant naming an account reaches that account and no other, and its role column is not
    /// consulted at all.
    /// </summary>
    /// <param name="callerUserId">The account asking, or <see langword="null"/> when anonymous.</param>
    /// <param name="expected">Whether the row should reach that caller.</param>
    /// <remarks>
    /// MIGRATION: the bracketed pseudo-role encoding is gone. A legacy account-scoped grant was tested by
    /// synthesising the literal <c>"["</c>, the account identifier and <c>"]"</c> and passing that through
    /// the very same role-name membership helper a real role name went through, so an account and a role
    /// were indistinguishable to the matcher. The target records an account-scoped grant in its own
    /// nullable account column, so the two principals are different columns rather than different string
    /// shapes and no encoding has to be parsed to tell them apart. The account column also takes
    /// precedence, which is the order the legacy code tested in
    /// (<c>ModulePermissionController.vb:L37-L45</c>) - the row below names the everyone pseudo-role as
    /// well, and it still reaches only the one account.
    /// </remarks>
    [Theory]
    [InlineData(UserId, true)]
    [InlineData(UserId + 1, false)]
    [InlineData(null, false)]
    public async Task ModuleKeys_AGrantNamingAnAccountReachesOnlyThatAccount(
        int? callerUserId,
        bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(
            FirstPermissionId,
            allowAccess: true,
            roleId: AllUsersRoleId,
            userId: UserId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            callerUserId,
            [MemberRoleName]));

        held.Should().Be(
            expected,
            "the account column decides on its own, so the everyone role beside it confers nothing");
    }

    /// <summary>
    /// A grant naming neither a role nor an account reaches nobody.
    /// </summary>
    /// <remarks>
    /// Both columns became nullable in the same upgrade, so the combination is representable in the
    /// terminal schema. The closed reading is the only safe one: a row that names no principal describes
    /// no principal, and guessing that it means "everybody" would turn a broken row into an open door.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_AGrantNamingNeitherRoleNorAccountReachesNobody()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true));

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, null, []))
            .Should().BeFalse();
    }

    /// <summary>
    /// A role name resolves only within the portal that owns the module under evaluation.
    /// </summary>
    /// <remarks>
    /// Role names are unique per portal rather than per installation, so resolving one installation-wide
    /// would let a grant to one tenant's "Administrators" be honoured for another tenant's. That is a
    /// silent cross-tenant escalation rather than a visible failure, which is why the identifier the name
    /// resolves to is read from the owning portal's roles and from nowhere else.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_ResolveARoleNameOnlyWithinThePortalThatOwnsTheModule()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(ForeignRoleId, MemberRoleName, OtherPortalId);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: ForeignRoleId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            UserId,
            [MemberRoleName]));

        held.Should().BeFalse(
            "the role carrying that name belongs to another tenant, so the name must not resolve to its "
            + "identifier here");
    }

    /// <summary>
    /// A declared role name is compared to the stored one exactly.
    /// </summary>
    /// <param name="declaredName">The name the caller declares.</param>
    /// <param name="expected">Whether it should resolve to the stored role.</param>
    /// <remarks>
    /// Nothing is trimmed, case-folded or localised, because the value in <c>Roles.RoleName</c> is the
    /// value a grant was made against. Normalising the comparison here would make the evaluator disagree
    /// with the store on any installation whose collation does not, and disagreeing about who holds a
    /// permission is the one thing this component cannot do.
    /// </remarks>
    [Theory]
    [InlineData(MemberRoleName, true)]
    [InlineData("measured members", false)]
    [InlineData("MEASURED MEMBERS", false)]
    [InlineData(" Measured Members", false)]
    [InlineData("Measured Members ", false)]
    public async Task ModuleKeys_CompareRoleNamesOrdinally(string declaredName, bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            UserId,
            [declaredName]));

        held.Should().Be(expected);
    }

    /// <summary>
    /// A blank declared role name is discarded rather than matched.
    /// </summary>
    /// <remarks>
    /// This is the <c>role &lt;&gt; ""</c> guard at <c>PortalSecurity.vb:L123</c>, which existed because
    /// the semicolon-delimited string the legacy code split carried a leading delimiter and so always
    /// produced an empty first element. The target takes a collection rather than a delimited string, so
    /// the empty element no longer arises by construction - but a caller can still send one, and a role
    /// row whose name is blank must not become a principal everybody reaches.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_DiscardABlankDeclaredRoleName()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, "   ");
        world.WithRole(ForeignRoleId, string.Empty);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: ForeignRoleId));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, ["", "   ", "\t"]));

        keys.Should().BeEmpty("a blank name is not a name, so it resolves to no identifier at all");
    }

    /// <summary>
    /// The configured everyone role participates for every caller, and the configured anonymous role
    /// only for a caller with no account.
    /// </summary>
    /// <param name="identified">Whether the caller carries an account identifier.</param>
    /// <param name="expectedKeys">The keys the caller should hold.</param>
    /// <remarks>
    /// Both names are matched against real role rows, which is why they are configurable rather than
    /// compiled in: the legacy comparison matched a persisted display name as a string, so an
    /// installation that renamed either role would silently stop matching a literal. Neither name has to
    /// be declared by the caller - that is the whole point of them - and the two are asserted together
    /// because the difference between "unconditionally" and "only when anonymous" is the difference
    /// between a public grant and a narrower one.
    /// </remarks>
    [Theory]
    [InlineData(true, new[] { "VIEW" })]
    [InlineData(false, new[] { "EDIT", "VIEW" })]
    public async Task ModuleKeys_ApplyTheBuiltInRoleNamesWithTheirLegacyWidths(
        bool identified,
        string[] expectedKeys)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(EveryoneRoleId, AllUsersRoleName);
        world.WithRole(AnonymousRoleId, UnauthenticatedRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: EveryoneRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: AnonymousRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build().ListEffectiveModulePermissionKeysAsync(
            ModuleId,
            identified ? UserId : null,
            []));

        keys.Should().Equal(expectedKeys);
    }

    /// <summary>
    /// The built-in role names come from configuration, so renaming one moves which stored role it
    /// matches.
    /// </summary>
    [Fact]
    public async Task ModuleKeys_UseTheConfiguredBuiltInRoleNames()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.Portal.AllUsersRoleName = "Everybody";
        world.WithModule(ModuleId);
        world.WithRole(EveryoneRoleId, "Everybody");
        world.WithRole(ForeignRoleId, AllUsersRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: EveryoneRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: ForeignRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, []));

        keys.Should().Equal(
            new[] { "VIEW" },
            "the renamed role is the one that stands for every caller, and the role still carrying the "
            + "default name is now an ordinary role nobody declared");
    }

    /// <summary>
    /// A module catalogue entry is admitted under the shared module scope code or by the module's own
    /// definition, and under nothing else.
    /// </summary>
    /// <param name="permissionCode">The scope code on the catalogue entry.</param>
    /// <param name="entryModuleDefinitionId">The definition the entry declares.</param>
    /// <param name="expected">Whether the entry should be admitted.</param>
    /// <remarks>
    /// Two admissions, and both are needed. An entry carrying the product-wide code applies to every
    /// module; an entry declared by the module's own definition applies to that module. The second
    /// admission is why this is not a fixed list of known codes - every installed module contributes
    /// catalogue entries under a code of its own choosing, and a check recognising only the shipped code
    /// would revoke every permission those modules define. What must never be admitted is an entry
    /// belonging to some other definition under some other code, which is how a scope this solution
    /// models no entity for could otherwise reach a module verdict.
    /// </remarks>
    [Theory]
    [InlineData(ModuleDefinitionScopeCode, ModuleDefinitionId, true)]
    [InlineData(ModuleDefinitionScopeCode, OtherModuleDefinitionId, true)]
    [InlineData(InstalledModuleScopeCode, ModuleDefinitionId, true)]
    [InlineData(InstalledModuleScopeCode, OtherModuleDefinitionId, false)]
    [InlineData(PageScopeCode, OtherModuleDefinitionId, false)]
    [InlineData(ExcludedSubsystemScopeCode, OtherModuleDefinitionId, false)]
    public async Task ModuleKeys_AdmitOnlyCatalogueEntriesThatBelongToTheModule(
        string permissionCode,
        int entryModuleDefinitionId,
        bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(
            FirstPermissionId,
            PermissionKey.VIEW,
            permissionCode,
            entryModuleDefinitionId));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        bool held = Succeeded(await world.Build().HasModulePermissionAsync(
            ModuleId,
            PermissionKey.VIEW,
            UserId,
            [MemberRoleName]));

        held.Should().Be(expected);
    }

    /// <summary>
    /// A page catalogue entry is admitted under the page scope code and under nothing else.
    /// </summary>
    /// <param name="permissionCode">The scope code on the catalogue entry.</param>
    /// <param name="expected">Whether the entry should be admitted.</param>
    /// <remarks>
    /// Unlike the module vocabulary this one is closed: the terminal page catalogue read filters on the
    /// product-wide page code and on nothing else, so every page shares one vocabulary and no definition
    /// widens it. Keeping the two scopes apart is what stops a grant recorded in one from being read as
    /// the other, which matters because both scope identifiers seed at zero.
    /// </remarks>
    [Theory]
    [InlineData(PageScopeCode, true)]
    [InlineData(ModuleDefinitionScopeCode, false)]
    [InlineData(InstalledModuleScopeCode, false)]
    [InlineData(ExcludedSubsystemScopeCode, false)]
    public async Task TabKeys_AdmitOnlyThePageScopeCode(string permissionCode, bool expected)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, permissionCode));
        world.TabGrants.Add(PageGrantRow(TabId, FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        bool held = Succeeded(await world.Build().HasTabPermissionAsync(
            TabId,
            PermissionKey.VIEW,
            UserId,
            [MemberRoleName]));

        held.Should().Be(expected);
    }

    /// <summary>
    /// A catalogue entry carrying the reader's own wildcard identifier is skipped rather than queried.
    /// </summary>
    /// <remarks>
    /// The grant readers accept minus one in the permission position as "every permission". No catalogue
    /// row can legitimately carry it, because <c>Permission.PermissionID</c> is <c>IDENTITY(1, 1)</c>, so
    /// the guard can never reject a real entry - but the consequence of losing it is silent and severe:
    /// passing the wildcard would return the grants of every permission and they would then all be
    /// judged as though they carried the key being asked about.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_SkipACatalogueEntryCarryingTheReadersWildcardIdentifier()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(
            WildcardPermissionId,
            PermissionKey.VIEW,
            ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(
            WildcardPermissionId,
            allowAccess: true,
            roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty();
        world.Permissions.Verify(
            permissions => permissions.GetModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                WildcardPermissionId,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "the wildcard must never be passed through as though it named a permission");
    }

    /// <summary>
    /// A catalogue identifier appearing twice is judged once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A catalogue read is per module rather than per grant, and the same identifier can appear in it
    /// more than once - two rows may name one permission under different codes. Judging its grants twice
    /// would double every row they contribute, and a duplicated refusal is harmless while a duplicated
    /// allowance beside a single refusal is not, so collapsing the duplicate is load-bearing rather than
    /// an optimisation.
    /// </para>
    /// <para>
    /// MIGRATION: the collapsing moved. An earlier revision spent one grant read per surviving catalogue
    /// entry and de-duplicated with a visited set to avoid reading the same identifier twice; the grants
    /// are now fetched for the whole module in ONE read and joined in memory, so the duplicate collapses
    /// in the applicable-entry dictionary instead. The property asserted is unchanged - a repeated
    /// identifier neither doubles the rows judged nor duplicates the key returned - so both the read
    /// shape and the surviving key set are asserted below, the first to pin the single read and the
    /// second to pin the outcome that read exists to produce.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_JudgeADuplicatedCatalogueIdentifierOnlyOnce()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(
            FirstPermissionId,
            PermissionKey.VIEW,
            InstalledModuleScopeCode,
            ModuleDefinitionId));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().Equal(
            new[] { "VIEW" },
            "a key survives once, however many catalogue rows name it");
        world.Permissions.Verify(
            permissions => permissions.GetModulePermissionsByModuleIdAsync(
                ModuleId,
                WildcardPermissionId,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the module's grants are read once for the whole module, not once per catalogue entry");
        world.Permissions.Verify(
            permissions => permissions.GetModulePermissionsByModuleIdAsync(
                ModuleId,
                FirstPermissionId,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "so the duplicated identifier cannot cost a second read either");
    }

    /// <summary>
    /// A grant row answering a wider question than the one asked is discarded.
    /// </summary>
    /// <remarks>
    /// Both arguments of the module grant reader carry a documented wildcard, so a substituted or future
    /// store may legitimately return rows belonging to another module or another permission. Judging
    /// such a row as though it carried the requested key on the requested module is how a grant made
    /// somewhere else silently becomes a grant here, which is why the identifiers are re-asserted on the
    /// way out rather than assumed from the way in.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_DiscardAGrantRowNamingADifferentModuleOrPermission()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));

        world.Permissions
            .Setup(permissions => permissions.GetModulePermissionsByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ModulePermission>)
            [
                ModuleGrantRow(
                    FirstPermissionId,
                    allowAccess: true,
                    roleId: MemberRoleId,
                    moduleId: SecondModuleId),
                ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId),
            ]);

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty(
            "one row belongs to another module and the other to another permission, so neither confers "
            + "the key that was asked about");
    }

    /// <summary>
    /// A page grant row answering a wider question than the one asked is discarded too.
    /// </summary>
    /// <remarks>
    /// The page reader treats only its permission argument as a wildcard, never its page argument, so the
    /// page half of the check is defensive symmetry rather than a requirement. It is asserted anyway so
    /// that the two collectors read identically and neither can be tightened without the other.
    /// </remarks>
    [Fact]
    public async Task TabKeys_DiscardAGrantRowNamingADifferentPageOrPermission()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, PageScopeCode));

        world.Permissions
            .Setup(permissions => permissions.GetTabPermissionsByTabIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TabPermission>)
            [
                PageGrantRow(SecondTabId, FirstPermissionId, allowAccess: true, roleId: MemberRoleId),
                PageGrantRow(TabId, SecondPermissionId, allowAccess: true, roleId: MemberRoleId),
            ]);

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveTabPermissionKeysAsync(TabId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty();
    }

    /// <summary>
    /// A module or page that does not exist confers nothing, and says so without failing.
    /// </summary>
    /// <remarks>
    /// The closed default rather than an error: this contract is asked what a caller holds, and the
    /// answer for something that does not exist is "nothing". Reporting existence is the application
    /// service's job, and it does it before asking - which is why an unknown scope must not become an
    /// exception here.
    /// </remarks>
    [Fact]
    public async Task Keys_ForAnUnknownModuleOrPageAreEmptyAndTheVerdictIsFalse()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: AllUsersRoleId));

        IPermissionEvaluator evaluator = world.Build();

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]))
            .Should().BeEmpty();
        Succeeded(await evaluator.HasModulePermissionAsync(ModuleId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
        Succeeded(await evaluator.ListEffectiveTabPermissionKeysAsync(TabId, UserId, [MemberRoleName]))
            .Should().BeEmpty();
        Succeeded(await evaluator.HasTabPermissionAsync(TabId, PermissionKey.VIEW, UserId, [MemberRoleName]))
            .Should().BeFalse();
    }

    /// <summary>
    /// Module zero, page zero and portal minus one are real identifiers, not absences.
    /// </summary>
    /// <remarks>
    /// <c>Modules.ModuleID</c> and <c>Tabs.TabID</c> are both <c>IDENTITY(0, 1)</c> and
    /// <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, while the legacy absent-integer sentinel is
    /// also minus one. All three values therefore address real rows, and every one of them is the value
    /// a plausible-looking guard would reject.
    /// </remarks>
    [Fact]
    public async Task Keys_TreatZeroAndMinusOneIdentifiersAsRealRows()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(ZeroTabId);
        world.WithRole(ZeroRoleId, ZeroRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, PageScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: ZeroRoleId));
        world.TabGrants.Add(PageGrantRow(ZeroTabId, SecondPermissionId, allowAccess: true, roleId: ZeroRoleId));

        IPermissionEvaluator evaluator = world.Build();

        PortalId.Should().Be(-1, "the portal identity column seeds at minus one");

        Succeeded(await evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [ZeroRoleName]))
            .Should().Equal(new[] { "VIEW" }, "module zero is a real module");
        Succeeded(await evaluator.ListEffectiveTabPermissionKeysAsync(ZeroTabId, UserId, [ZeroRoleName]))
            .Should().Equal(new[] { "EDIT" }, "page zero is a real page");
        Succeeded(await evaluator.ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [ZeroRoleName]))
            .Should().Equal(new[] { "EDIT", "VIEW" }, "and portal minus one is a real portal");
    }

    /// <summary>
    /// The tenant-wide union excludes grants on soft-deleted modules and pages.
    /// </summary>
    /// <remarks>
    /// A grant on something the caller can no longer reach confers nothing, so the recycled content is
    /// filtered before its grants are considered rather than after - which also keeps the catalogue read
    /// down to the permissions that can still affect the answer.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_ExcludeGrantsOnSoftDeletedModulesAndPages()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId, isDeleted: true);
        world.WithTab(TabId, isDeleted: true);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, PageScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.TabGrants.Add(PageGrantRow(TabId, SecondPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty();
    }

    /// <summary>
    /// A grant naming a catalogue entry that does not exist confers nothing.
    /// </summary>
    /// <remarks>
    /// A broken row rather than a denial, and failing closed is the only safe reading of it: there is no
    /// key to confer, so nothing is conferred. The key is resolved from the grant's own permission
    /// identifier rather than from a navigation property, so whether the store loaded that reference
    /// cannot change the answer - a decision that quietly returned "holds nothing" because a reference
    /// happened to be unloaded would be an authorisation defect no test of this type could see.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_ConferNothingForAGrantWhoseCatalogueEntryIsMissing()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]));

        keys.Should().BeEmpty();
        world.Permissions.Verify(
            permissions => permissions.GetByIdsAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(FirstPermissionId)),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the key is resolved by the grant's own permission identifier, and the whole set of "
            + "identifiers the grants name is resolved in one read");
    }

    /// <summary>
    /// The tenant-wide union is distinct and ordered ordinally.
    /// </summary>
    /// <remarks>
    /// The ordering is not cosmetic. An access token minted twice from the same grants must carry an
    /// identical claim set both times, and the enumeration order of a set is not a contract - so the
    /// answer is sorted before it leaves.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_AreDistinctAndOrderedOrdinally()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithModule(SecondModuleId);
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(ThirdPermissionId, PermissionKey.WRITE, PageScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(
            FirstPermissionId,
            allowAccess: true,
            roleId: MemberRoleId,
            moduleId: SecondModuleId));
        world.TabGrants.Add(PageGrantRow(TabId, ThirdPermissionId, allowAccess: true, roleId: MemberRoleId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName]));

        keys.Should().Equal(
            "EDIT",
            "VIEW",
            "WRITE");
    }

    /// <summary>
    /// A module the installation owns rather than a tenant resolves no named role, and is still reachable
    /// through the pseudo-roles and through an account.
    /// </summary>
    /// <remarks>
    /// A host-level scope carries no portal, and that is deliberate and closed: with no portal there is
    /// no set of role names that can be resolved without reaching installation-wide, and reaching
    /// installation-wide is the cross-tenant escalation the contract forbids. Such a scope stays
    /// reachable through the everyone, anonymous and account-scoped grants, none of which needs a role
    /// identifier at all.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_ForAnInstallationOwnedModuleResolveNoNamedRole()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId, portalId: null);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.EDIT, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(ThirdPermissionId, PermissionKey.WRITE, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: AllUsersRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(ThirdPermissionId, allowAccess: true, userId: UserId));

        IReadOnlyList<string> keys = Succeeded(await world.Build()
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));

        keys.Should().Equal(
            "VIEW",
            "WRITE");
        world.RoleStore.Verify(
            roles => roles.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "there is no portal whose roles could be read without reaching across tenants");
    }

    /// <summary>
    /// A verdict is membership in the very set the listing returns, for both scopes.
    /// </summary>
    /// <param name="permissionKey">The key asked about.</param>
    /// <remarks>
    /// Expressing the verdict a second time is how a verdict and a listing come to disagree, and a user
    /// offered an action that is then refused is a defect they experience and a test rarely catches. The
    /// implementation defines the verdict as a membership test over the same reduction, and this test
    /// pins that identity across every member of the key vocabulary rather than asserting the two
    /// separately and hoping they agree.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW)]
    [InlineData(PermissionKey.EDIT)]
    [InlineData(PermissionKey.READ)]
    [InlineData(PermissionKey.WRITE)]
    public async Task Verdicts_AreMembershipInTheSetTheListingReturns(PermissionKey permissionKey)
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(SecondPermissionId, PermissionKey.READ, ModuleDefinitionScopeCode));
        world.Catalogue.Add(CatalogueEntry(ThirdPermissionId, PermissionKey.EDIT, PageScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));
        world.ModuleGrants.Add(ModuleGrantRow(SecondPermissionId, allowAccess: false, roleId: MemberRoleId));
        world.TabGrants.Add(PageGrantRow(TabId, ThirdPermissionId, allowAccess: true, roleId: MemberRoleId));

        IPermissionEvaluator evaluator = world.Build();

        IReadOnlyList<string> moduleKeys = Succeeded(await evaluator
            .ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [MemberRoleName]));
        bool moduleVerdict = Succeeded(await evaluator
            .HasModulePermissionAsync(ModuleId, permissionKey, UserId, [MemberRoleName]));

        moduleVerdict.Should().Be(moduleKeys.Contains(permissionKey.ToString()));

        IReadOnlyList<string> tabKeys = Succeeded(await evaluator
            .ListEffectiveTabPermissionKeysAsync(TabId, UserId, [MemberRoleName]));
        bool tabVerdict = Succeeded(await evaluator
            .HasTabPermissionAsync(TabId, permissionKey, UserId, [MemberRoleName]));

        tabVerdict.Should().Be(tabKeys.Contains(permissionKey.ToString()));
    }

    /// <summary>
    /// The supplied cancellation token reaches every read a decision performs.
    /// </summary>
    /// <remarks>
    /// A token that is accepted and then dropped is worse than no token at all: the caller believes the
    /// work can be abandoned and it cannot. The token is matched by identity rather than by shape, so
    /// substituting a different one - or the default - fails here.
    /// </remarks>
    [Fact]
    public async Task PortalKeys_PassTheSuppliedCancellationTokenToEveryRead()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(TabId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Catalogue.Add(CatalogueEntry(FirstPermissionId, PermissionKey.VIEW, ModuleDefinitionScopeCode));
        world.ModuleGrants.Add(ModuleGrantRow(FirstPermissionId, allowAccess: true, roleId: MemberRoleId));

        using CancellationTokenSource source = new();
        CancellationToken token = source.Token;

        await world.Build().ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [MemberRoleName], token);

        world.RoleStore.Verify(roles => roles.GetByPortalIdAsync(PortalId, token), Times.Once);
        world.ModuleStore.Verify(modules => modules.GetByPortalIdAsync(PortalId, token), Times.Once);
        world.TabStore.Verify(tabs => tabs.GetByPortalIdAsync(PortalId, token), Times.Once);
        world.Permissions.Verify(
            permissions => permissions.GetModulePermissionsByPortalIdAsync(PortalId, token),
            Times.Once);
        world.Permissions.Verify(
            permissions => permissions.GetTabPermissionsByPortalIdAsync(PortalId, token),
            Times.Once);
        world.Permissions.Verify(
            permissions => permissions.GetByIdsAsync(
                It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(FirstPermissionId)),
                token),
            Times.Once);
    }

    /// <summary>
    /// A token already cancelled stops every member before it reads anything.
    /// </summary>
    /// <remarks>
    /// Observing cancellation at entry rather than only between reads is what makes a cancelled request
    /// cost nothing. It also keeps a cancellation distinguishable from a refusal: an abandoned request
    /// must not come back as "you are not allowed".
    /// </remarks>
    [Fact]
    public async Task EveryMember_ObservesATokenThatIsAlreadyCancelled()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithTab(TabId);
        IPermissionEvaluator evaluator = world.Build();

        using CancellationTokenSource source = new();
        await source.CancelAsync();
        CancellationToken cancelled = source.Token;

        await FluentActions
            .Awaiting(() => evaluator.ListEffectivePortalPermissionKeysAsync(PortalId, UserId, [], cancelled))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveModulePermissionKeysAsync(ModuleId, UserId, [], cancelled))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveTabPermissionKeysAsync(TabId, UserId, [], cancelled))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Awaiting(() => evaluator.HasModulePermissionAsync(
                ModuleId,
                PermissionKey.VIEW,
                UserId,
                [],
                cancelled))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions
            .Awaiting(() => evaluator.HasTabPermissionAsync(TabId, PermissionKey.VIEW, UserId, [], cancelled))
            .Should().ThrowAsync<OperationCanceledException>();

        world.ModuleStore.Verify(
            modules => modules.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a cancelled request must cost nothing at all");
    }

    /// <summary>
    /// A store that cannot be reached produces a fault rather than a refusal.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point. "The database is unavailable" and "you are not allowed" are
    /// different answers, and collapsing the first into the second would tell an operator their
    /// permissions were wrong while the real problem was elsewhere - and would, on a write path, let a
    /// transient outage read as a deliberate denial.
    /// </remarks>
    [Fact]
    public async Task ModuleKeys_LetAStoreFaultSurfaceRatherThanBecomingARefusal()
    {
        EvaluatorWorld world = EvaluatorWorld.Create();
        world.WithModule(ModuleId);
        world.WithRole(MemberRoleId, MemberRoleName);
        world.Permissions
            .Setup(permissions => permissions.GetByModuleIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the grant store is unreachable"));

        IPermissionEvaluator evaluator = world.Build();

        await FluentActions
            .Awaiting(() => evaluator.ListEffectiveModulePermissionKeysAsync(
                ModuleId,
                UserId,
                [MemberRoleName]))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions
            .Awaiting(() => evaluator.HasModulePermissionAsync(
                ModuleId,
                PermissionKey.VIEW,
                UserId,
                [MemberRoleName]))
            .Should().ThrowAsync<InvalidOperationException>();
    }



    // =================================================================================================
    // The grant entities.
    //
    // MIGRATION: three static controllers collapse into one application contract, and the decision moves
    //            out of all three. The legacy source spread this aggregate across a 71-line catalogue
    //            controller with 9 public members, a 389-line module-grant controller with 18 and a
    //            349-line page-grant controller with 15 - 42 measured members, of which 13 were already
    //            marked obsolete by their own author and are not ported, and many of the rest were
    //            near-duplicate overloads differing only in whether the caller had already materialised
    //            the rows. The persistence subset becomes one repository contract, the orchestration
    //            becomes one application service, precedence becomes one reducer, and enforcement becomes
    //            an authorisation handler at the api boundary. The five obsolete access-check and
    //            edit-permission members are not ported, and neither is the path-scoped catalogue lookup.
    //
    // MIGRATION: the legacy tables are singular and their text columns are narrow. The terminal schema
    //            names them in the singular, and the four catalogue text columns are each fifty
    //            characters, with the catalogue key obtained from an identity column. Rule T4 makes that
    //            schema immutable, so the mapping binds to those names and widths rather than redefining
    //            them - which is exactly why the key enumeration's member names had to match the stored
    //            spellings rather than the other way round. The mapping itself is asserted by the
    //            persistence suite in the integration project, which is the only place a mapping can be
    //            proved against a real database; what is provable here is the entity shape those mappings
    //            bind to, which is what the test below pins.
    // =================================================================================================

    /// <summary>
    /// The grant entities carry no serialisation attributes.
    /// </summary>
    /// <remarks>
    /// MIGRATION: every xml serialisation attribute is dropped from the domain. The legacy catalogue type
    /// decorated three of its five properties with element names and marked the other two as ignored,
    /// because the type doubled as the wire format for portal templates. In the target the domain entity is
    /// a plain persisted type with no opinion about transport: serialisation happens once, at the api
    /// boundary, over the request and response contracts. An attribute reappearing here would mean an entity
    /// had started crossing the wire directly, which baseline B6 forbids - so this is a boundary test rather
    /// than a tidiness test.
    /// </remarks>
    [Fact]
    public void GrantEntities_CarryNoSerialisationAttributes()
    {
        Type[] entities = [typeof(Permission), typeof(ModulePermission), typeof(TabPermission)];

        foreach (Type entity in entities)
        {
            foreach (MemberInfo member in entity.GetMembers())
            {
                foreach (CustomAttributeData attribute in member.GetCustomAttributesData())
                {
                    (attribute.AttributeType.Namespace ?? string.Empty).Should().NotStartWith(
                        "System.Xml",
                        $"{entity.Name}.{member.Name} must not describe a wire format; serialisation belongs to the request and response contracts at the api boundary");
                }
            }
        }
    }

    /// <summary>
    /// A grant references its catalogue entry rather than deriving from it.
    /// </summary>
    /// <remarks>
    /// The two grant entities are independent types carrying a foreign key, not specialisations of the
    /// catalogue entry - a grant is a reference to a permission, not a kind of one. Worth pinning because
    /// the legacy types were denormalised joins that repeated the catalogue's key and name inline, so a
    /// reader coming from that source reasonably expects an inheritance relationship, and code written on
    /// that expectation would compile against a base type that does not exist.
    /// </remarks>
    [Fact]
    public void GrantEntities_ReferenceTheCatalogueRatherThanDerivingFromIt()
    {
        typeof(Permission).IsAssignableFrom(typeof(ModulePermission)).Should().BeFalse(
            "a module grant references a catalogue entry through its permission identifier and is not a kind of catalogue entry");

        typeof(Permission).IsAssignableFrom(typeof(TabPermission)).Should().BeFalse(
            "a page grant references a catalogue entry through its permission identifier and is not a kind of catalogue entry");
    }

    // =================================================================================================
    // The permission key vocabulary.
    // =================================================================================================

    /// <summary>
    /// The key vocabulary is a closed set of four persisted spellings.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the key column was free text against which the legacy source compared bare literals. The
    /// target types it as a closed enumeration whose member names are the stored spellings exactly, so the
    /// magic strings become named members without changing one stored value - which is what lets the schema
    /// stay immutable while the code stops guessing. The order is the declaration order, and it is pinned
    /// because these members are persisted data: renaming one, re-casing one, or inserting one is a schema
    /// change disguised as a refactor.
    /// </remarks>
    [Fact]
    public void PermissionKeys_AreAClosedSetOfFourPersistedSpellings()
    {
        Enum.GetNames<PermissionKey>().Should().Equal(
            ["VIEW", "EDIT", "READ", "WRITE"],
            "these four spellings are what the key column stores, so the enumeration must mirror them exactly");
    }

    /// <summary>
    /// A key is a discrete member, never a bit field.
    /// </summary>
    /// <remarks>
    /// A grant row names exactly one key, and the tables record one row per key per principal. Marking the
    /// enumeration as a bit field would invite combining keys into a single value, which no column can
    /// store and which would make the stored numbers meaningless.
    /// </remarks>
    [Fact]
    public void PermissionKeys_AreNotABitField()
    {
        typeof(PermissionKey).GetCustomAttribute<FlagsAttribute>().Should().BeNull(
            "keys are discrete members recorded one row at a time, not flags to be combined");
    }

    /// <summary>
    /// Every declared key round-trips as its persisted spelling, and no other casing is a member.
    /// </summary>
    /// <param name="permissionKey">The key under test.</param>
    /// <param name="persistedSpelling">The spelling stored in the key column.</param>
    /// <remarks>
    /// All four members are covered here. Only two of them have a literal call site in the legacy code that
    /// is still live - and the census is worth recording precisely, because the raw counts mislead. Of the
    /// six edit literals, three sit inside a region the legacy author explicitly marked as obsolete and
    /// retained only for binary compatibility, and one of those three calls an overload that was itself
    /// already marked obsolete. Only three edit sites and four view sites are live. Separately, one view
    /// literal elsewhere in the legacy source is not a permission key at all: it is a control-panel display
    /// mode compared against a site setting, and treating it as a key would invent a permission that never
    /// existed. The read and write members carry no in-scope legacy literal, and they are still pinned here
    /// because they are stored values that the schema and installed modules use.
    /// </remarks>
    [Theory]
    [InlineData(PermissionKey.VIEW, "VIEW")]
    [InlineData(PermissionKey.EDIT, "EDIT")]
    [InlineData(PermissionKey.READ, "READ")]
    [InlineData(PermissionKey.WRITE, "WRITE")]
    public void PermissionKeys_RoundTripAsTheirPersistedSpellingAndAreNeverCaseFolded(
        PermissionKey permissionKey,
        string persistedSpelling)
    {
        permissionKey.ToString().Should().Be(
            persistedSpelling,
            "the member name is the stored value, so the two must not drift apart");

        Enum.Parse<PermissionKey>(persistedSpelling).Should().Be(
            permissionKey,
            "a value read back from the key column must resolve to the member that wrote it");

        Enum.TryParse<PermissionKey>(persistedSpelling.ToLowerInvariant(), out PermissionKey folded)
            .Should()
            .BeFalse(
                $"a lower-cased spelling is not a member, so nothing silently accepts \"{persistedSpelling.ToLowerInvariant()}\" as {permissionKey}; the parsed-out value was {folded}");
    }

    /// <summary>
    /// Builds a catalogue row naming one permission key.
    /// </summary>
    /// <param name="permissionKey">The key the row names.</param>
    /// <returns>The catalogue row.</returns>
    private static Permission Entry(PermissionKey permissionKey) => new()
    {
        PermissionId = 1,
        PermissionCode = "SYSTEM_MODULE_DEFINITION",
        ModuleDefinitionId = 1,
        PermissionKey = permissionKey,
        PermissionName = permissionKey.ToString(),
    };

    /// <summary>
    /// Builds a placement of the module under test on a page.
    /// </summary>
    /// <param name="tabId">The page the module sits on.</param>
    /// <param name="tabModuleId">
    /// The placement's own key, when a test addresses the placement by it rather than by its page. Defaults to
    /// a value derived from the page so that the many tests which never name a placement key keep working
    /// unchanged, and so that no two placements built for one module ever collide.
    /// </param>
    /// <returns>The placement.</returns>
    private static TabModule Placement(int tabId, int? tabModuleId = null) => new()
    {
        TabModuleId = tabModuleId ?? (1000 + tabId),
        TabId = tabId,
        ModuleId = ModuleId,
        PaneName = "ContentPane",
    };

    /// <summary>
    /// The identifier the grant reads treat as "every permission" rather than as "no permission".
    /// </summary>
    /// <remarks>
    /// Measured legacy behaviour that the terminal procedures still carry: in this argument position the
    /// value is a wildcard, which is a third distinct meaning for the same number alongside the All Users
    /// role and the legacy absence sentinel. Named here so the substituted store reproduces it instead of
    /// quietly matching nothing.
    /// </remarks>
    private const int WildcardPermissionId = -1;

    /// <summary>
    /// Builds a catalogue row.
    /// </summary>
    /// <param name="permissionId">The row's own identifier, which grants reference.</param>
    /// <param name="permissionKey">The key the row defines.</param>
    /// <param name="permissionCode">The scope code the row belongs to.</param>
    /// <param name="moduleDefinitionId">
    /// The definition the row declares. Defaults to the definition the module under evaluation was built
    /// from, so a row is in scope unless a test deliberately places it elsewhere.
    /// </param>
    /// <returns>The catalogue row.</returns>
    private static Permission CatalogueEntry(
        int permissionId,
        PermissionKey permissionKey,
        string permissionCode,
        int moduleDefinitionId = ModuleDefinitionId) => new()
        {
            PermissionId = permissionId,
            PermissionCode = permissionCode,
            ModuleDefinitionId = moduleDefinitionId,
            PermissionKey = permissionKey,
            PermissionName = permissionKey.ToString(),
        };

    /// <summary>
    /// Builds one grant recorded against the module under test.
    /// </summary>
    /// <param name="permissionId">The catalogue row the grant references.</param>
    /// <param name="allowAccess">Whether the row confers the key or refuses it.</param>
    /// <param name="roleId">The role the grant names, or <see langword="null"/> when it names none.</param>
    /// <param name="userId">The account the grant names, or <see langword="null"/> when it names none.</param>
    /// <param name="moduleId">The module the grant is recorded against.</param>
    /// <returns>The grant row.</returns>
    private static ModulePermission ModuleGrantRow(
        int permissionId,
        bool allowAccess,
        int? roleId = null,
        int? userId = null,
        int moduleId = ModuleId)
    {
        return new ModulePermission
        {
            // The surrogate key has to differ per row, and rows for one permission may be recorded
            // against several modules, so both coordinates contribute to it.
            ModulePermissionId = (1000 * (moduleId + 2)) + permissionId,
            ModuleId = moduleId,
            PermissionId = permissionId,
            RoleId = roleId,
            UserId = userId,
            AllowAccess = allowAccess,
        };
    }

    /// <summary>
    /// Builds one grant recorded against a page.
    /// </summary>
    /// <param name="tabId">The page the grant is recorded against.</param>
    /// <param name="permissionId">The catalogue row the grant references.</param>
    /// <param name="allowAccess">Whether the row confers the key or refuses it.</param>
    /// <param name="roleId">The role the grant names, or <see langword="null"/> when it names none.</param>
    /// <param name="userId">The account the grant names, or <see langword="null"/> when it names none.</param>
    /// <returns>The grant row.</returns>
    private static TabPermission PageGrantRow(
        int tabId,
        int permissionId,
        bool allowAccess,
        int? roleId = null,
        int? userId = null)
    {
        return new TabPermission
        {
            // Distinct per page as well as per permission, for the reason given on the module row above.
            TabPermissionId = (1000 * (tabId + 2)) + permissionId,
            TabId = tabId,
            PermissionId = permissionId,
            RoleId = roleId,
            UserId = userId,
            AllowAccess = allowAccess,
        };
    }


    /// <summary>
    /// The rows the concrete evaluator reads, together with the substituted repositories that serve them
    /// and the configuration it is constructed over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every repository is substituted <em>strictly</em>, and every read the evaluator can perform is
    /// wired here from these lists. That combination buys two things at once: a test arranges rows rather
    /// than call expectations, so it reads like the data it describes; and the evaluator cannot touch a
    /// repository member that was not wired without failing outright, so this type also pins how narrow
    /// the read surface of an access decision is.
    /// </para>
    /// <para>
    /// The lists are mutable and public on purpose. A test adds the rows it needs after
    /// <see cref="Create"/> and before <see cref="Build"/>, and the wiring closes over the lists rather
    /// than over snapshots of them, so ordering the two the other way round would still work. Any wired
    /// member may also be re-substituted by a test that needs a store to return something the lists
    /// cannot express - a row answering a wider question than the one asked, or a fault.
    /// </para>
    /// <para>
    /// Nothing here is production code and nothing here re-implements a rule. The filtering below
    /// reproduces only what the repository CONTRACT documents - which rows a read returns, including the
    /// minus-one wildcard the two grant readers accept in their permission position - so the reachability
    /// test, the pseudo-role rules, the scope admissions and the allow-and-deny reduction all remain
    /// entirely the evaluator's own.
    /// </para>
    /// </remarks>
    private sealed class EvaluatorWorld
    {
        private EvaluatorWorld()
        {
            ModuleStore
                .Setup(modules => modules.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, CancellationToken _) =>
                    Modules.FirstOrDefault(module => module.ModuleId == moduleId));

            ModuleStore
                .Setup(modules => modules.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) => (IReadOnlyList<Module>)
                    Modules.Where(module => module.PortalId == portalId).ToList());

            TabStore
                .Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, CancellationToken _) =>
                    Tabs.FirstOrDefault(tab => tab.TabId == tabId));

            TabStore
                .Setup(tabs => tabs.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) => (IReadOnlyList<Tab>)
                    Tabs.Where(tab => tab.PortalId == portalId).ToList());

            RoleStore
                .Setup(roles => roles.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) => (IReadOnlyList<Role>)
                    Roles.Where(role => role.PortalId == portalId).ToList());

            // The module and page catalogue reads are narrowed by the scope in the real store. They are
            // returned UNFILTERED here so that the scope-code and definition admissions stay the
            // evaluator's own: a substitute that also filtered would hide whether those checks are
            // applied at all, and they are exactly what the admission tests assert.
            Permissions
                .Setup(permissions => permissions.GetByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, CancellationToken __) => (IReadOnlyList<Permission>)
                    Catalogue.ToList());

            Permissions
                .Setup(permissions => permissions.GetByTabIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, CancellationToken __) => (IReadOnlyList<Permission>)
                    Catalogue.ToList());

            Permissions
                .Setup(permissions => permissions.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int permissionId, CancellationToken _) =>
                    Catalogue.FirstOrDefault(entry => entry.PermissionId == permissionId));

            // The SET-WISE catalogue read, which is how the portal-wide walk resolves the entries its
            // reached grants name: the distinct identifiers are collected and resolved in one read rather
            // than one read per identifier. Stubbed from the same fixture as the single-identifier read, and
            // reproducing the one contract difference that matters to the caller - an identifier naming no
            // entry is OMITTED from the result rather than yielding a null element, so the caller may index
            // the result without a per-element null test. Returning a null-padded list here would let a
            // regression in that handling pass unnoticed.
            Permissions
                .Setup(permissions => permissions.GetByIdsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<int> permissionIds, CancellationToken _) =>
                    (IReadOnlyList<Permission>)Catalogue
                        .Where(entry => permissionIds.Contains(entry.PermissionId))
                        .ToList());

            Permissions
                .Setup(permissions => permissions.GetModulePermissionsByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, int permissionId, CancellationToken _) =>
                    (IReadOnlyList<ModulePermission>)ModuleGrants
                        .Where(grant => grant.ModuleId == moduleId
                            && (permissionId == WildcardPermissionId || grant.PermissionId == permissionId))
                        .ToList());

            Permissions
                .Setup(permissions => permissions.GetTabPermissionsByTabIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, int permissionId, CancellationToken _) =>
                    (IReadOnlyList<TabPermission>)TabGrants
                        .Where(grant => grant.TabId == tabId
                            && (permissionId == WildcardPermissionId || grant.PermissionId == permissionId))
                        .ToList());

            Permissions
                .Setup(permissions => permissions.GetModulePermissionsByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) =>
                    (IReadOnlyList<ModulePermission>)ModuleGrants
                        .Where(grant => Modules.Any(module =>
                            module.ModuleId == grant.ModuleId && module.PortalId == portalId))
                        .ToList());

            Permissions
                .Setup(permissions => permissions.GetTabPermissionsByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) =>
                    (IReadOnlyList<TabPermission>)TabGrants
                        .Where(grant => Tabs.Any(tab => tab.TabId == grant.TabId && tab.PortalId == portalId))
                        .ToList());
        }

        /// <summary>Gets the grant and catalogue store.</summary>
        public Mock<IPermissionRepository> Permissions { get; } = new(MockBehavior.Strict);

        /// <summary>Gets the store role names resolve through.</summary>
        public Mock<IRoleRepository> RoleStore { get; } = new(MockBehavior.Strict);

        /// <summary>Gets the store that establishes which portal owns a module.</summary>
        public Mock<IModuleRepository> ModuleStore { get; } = new(MockBehavior.Strict);

        /// <summary>Gets the store that establishes which portal owns a page.</summary>
        public Mock<ITabRepository> TabStore { get; } = new(MockBehavior.Strict);

        /// <summary>Gets the configuration supplying the two built-in role names.</summary>
        public PortalOptions Portal { get; } = new()
        {
            AllUsersRoleName = AllUsersRoleName,
            UnauthenticatedRoleName = UnauthenticatedRoleName,
        };

        /// <summary>Gets the modules the store holds.</summary>
        public List<Module> Modules { get; } = [];

        /// <summary>Gets the pages the store holds.</summary>
        public List<Tab> Tabs { get; } = [];

        /// <summary>Gets the roles the store holds.</summary>
        public List<Role> Roles { get; } = [];

        /// <summary>Gets the catalogue rows the store holds.</summary>
        public List<Permission> Catalogue { get; } = [];

        /// <summary>Gets the module grants the store holds.</summary>
        public List<ModulePermission> ModuleGrants { get; } = [];

        /// <summary>Gets the page grants the store holds.</summary>
        public List<TabPermission> TabGrants { get; } = [];

        /// <summary>Builds an empty world whose every read is wired and returns nothing.</summary>
        /// <returns>The world.</returns>
        public static EvaluatorWorld Create() => new();

        /// <summary>Adds a module owned by a portal.</summary>
        /// <param name="moduleId">The module key, which the schema seeds at zero.</param>
        /// <param name="portalId">
        /// The owning portal, or <see langword="null"/> when the installation owns the module rather than
        /// a tenant.
        /// </param>
        /// <param name="isDeleted">Whether the module sits in the recycle bin.</param>
        /// <param name="moduleDefinitionId">The definition the module was built from.</param>
        public void WithModule(
            int moduleId,
            int? portalId = PortalId,
            bool isDeleted = false,
            int moduleDefinitionId = ModuleDefinitionId)
            => Modules.Add(new Module
            {
                ModuleId = moduleId,
                ModuleDefinitionId = moduleDefinitionId,
                PortalId = portalId,
                IsDeleted = isDeleted,
            });

        /// <summary>Adds a page owned by a portal.</summary>
        /// <param name="tabId">The page key, which the schema seeds at zero.</param>
        /// <param name="portalId">The owning portal, or <see langword="null"/> for a host page.</param>
        /// <param name="isDeleted">Whether the page sits in the recycle bin.</param>
        public void WithTab(int tabId, int? portalId = PortalId, bool isDeleted = false)
            => Tabs.Add(new Tab
            {
                TabId = tabId,
                TabName = "Page " + tabId.ToString(CultureInfo.InvariantCulture),
                PortalId = portalId,
                IsDeleted = isDeleted,
            });

        /// <summary>Adds a role belonging to a portal.</summary>
        /// <param name="roleId">The role key, which the schema seeds at zero.</param>
        /// <param name="roleName">The stored name, matched exactly.</param>
        /// <param name="portalId">The owning portal.</param>
        public void WithRole(int roleId, string roleName, int? portalId = PortalId)
            => Roles.Add(new Role
            {
                RoleId = roleId,
                RoleName = roleName,
                PortalId = portalId,
            });

        /// <summary>Constructs the concrete evaluator over this world.</summary>
        /// <returns>The evaluator, typed as the contract its callers depend on.</returns>
        public IPermissionEvaluator Build() => new PermissionEvaluator(
            Permissions.Object,
            RoleStore.Object,
            ModuleStore.Object,
            TabStore.Object,
            Options.Create(Portal));
    }

    /// <summary>
    /// Builds a permission service over substituted collaborators.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            Account = new User
            {
                UserId = UserId,
                Username = "measured_member",
                FirstName = "Ada",
                LastName = "Lovelace",
                DisplayName = "Ada Lovelace",
            };

            Module = new Module
            {
                ModuleId = ModuleId,
                ModuleDefinitionId = 1,
                PortalId = PortalId,
                ModuleTitle = "Measured Module",
            };

            Tab = new Tab
            {
                TabId = TabId,
                PortalId = PortalId,
                TabName = "Measured Page",
            };

            Catalogue = [];
            AssignedRoles = [];
            PortalKeys = [];
            ModuleKeys = [];
            TabKeys = [];
            StoredPlacements = [];
            PageViewGrants = [];
            CapturedRoleNames = [];
            PortalTabs = [Tab];
            CacheKeysRequested = [];
            CacheLifetimesRequested = [];
            PortalOptions = new PortalOptions();

            Permissions = new Mock<IPermissionRepository>(MockBehavior.Loose);
            Evaluator = new Mock<IPermissionEvaluator>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Transaction = new Mock<ITransactionScope>(MockBehavior.Loose);

            // The transaction scope is handed back by the unit of work so that the cleanup member under test
            // can open, commit and dispose one. Loose behaviour would return null for the scope and the
            // await-using would then dereference it, so this stub is required rather than decorative.
            UnitOfWork
                .Setup(unitOfWork => unitOfWork.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Transaction.Object);

            // A multiplier of zero would take every read down the caching-disabled branch, which is a real
            // configuration but not the default one. The suite's baseline is the shipped default, so the
            // cached path is what the read tests exercise unless a test says otherwise.
            CachingOptions = new CachingOptions();

            Service = new PermissionService(
                Permissions.Object,
                Evaluator.Object,
                Portals.Object,
                Modules.Object,
                Tabs.Object,
                Users.Object,
                UnitOfWork.Object,
                Cache.Object,
                Clock.Object,
                PortalOptions,
                CachingOptions);
        }

        public User Account { get; }

        public Module Module { get; }

        public Tab Tab { get; }

        public PortalOptions PortalOptions { get; }

        public IReadOnlyList<Permission> Catalogue { get; set; }

        public IReadOnlyList<string> AssignedRoles { get; set; }

        public IReadOnlyList<string> PortalKeys { get; set; }

        public IReadOnlyList<string> ModuleKeys { get; set; }

        public IReadOnlyList<string> TabKeys { get; set; }

        public IReadOnlyList<TabModule> StoredPlacements { get; set; }

        public Dictionary<int, bool> PageViewGrants { get; }

        public List<string> CapturedRoleNames { get; }

        public bool ModuleGrant { get; set; }

        public bool TabGrant { get; set; }

        public Mock<IPermissionRepository> Permissions { get; }

        public Mock<IPermissionEvaluator> Evaluator { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<ITransactionScope> Transaction { get; }

        public Mock<IClock> Clock { get; }

        public CachingOptions CachingOptions { get; }

        /// <summary>The pages the portal holds, which the account cleanup enumerates to evict by page.</summary>
        public IReadOnlyList<Tab> PortalTabs { get; set; }

        /// <summary>Every cache key the service asked for, in the order it asked.</summary>
        public IList<string> CacheKeysRequested { get; }

        /// <summary>Every cache lifetime the service computed, in the order it computed them.</summary>
        public IList<TimeSpan> CacheLifetimesRequested { get; }

        public PermissionService Service { get; }

        /// <summary>
        /// Builds a harness whose collaborators can answer every question asked of them.
        /// </summary>
        /// <returns>The harness.</returns>
        public static Harness Ready()
        {
            Harness harness = new();

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            harness.Portals
                .Setup(portals => portals.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            harness.Users
                .Setup(users => users.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Account);

            harness.Users
                .Setup(users => users.ListRoleNamesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AssignedRoles);

            harness.Modules
                .Setup(modules => modules.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Module);

            harness.Modules
                .Setup(modules => modules.GetTabModulesByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.StoredPlacements);

            harness.Tabs
                .Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Tab);

            // The pages of the portal, which the account cleanup enumerates in order to evict the
            // module-permission entry of each one. Stubbed to the harness's own page rather than to an empty
            // sequence, so a test asserting the eviction has something to observe it against.
            harness.Tabs
                .Setup(tabs => tabs.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalTabs);

            // A PASS-THROUGH CACHE, not a stub that answers from nothing. The read-through helper hands the
            // cache a factory and expects the value back; a loose mock would return null without ever
            // invoking the factory, which would make every cached read look like an empty answer and would
            // test the mock rather than the service. Invoking the factory is what the real service does on a
            // miss, so this models a cold cache - the state every one of these tests is written against.
            // One setup per closed generic the service instantiates, because a generic method cannot be
            // stubbed open.
            harness.Cache
                .Setup(cache => cache.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<string>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string key,
                    Func<CancellationToken, Task<IReadOnlyList<string>>> factory,
                    TimeSpan expiration,
                    CancellationToken token) =>
                {
                    harness.CacheKeysRequested.Add(key);
                    harness.CacheLifetimesRequested.Add(expiration);
                    return factory(token);
                });

            harness.Cache
                .Setup(cache => cache.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<PermissionDto>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string key,
                    Func<CancellationToken, Task<IReadOnlyList<PermissionDto>>> factory,
                    TimeSpan expiration,
                    CancellationToken token) =>
                {
                    harness.CacheKeysRequested.Add(key);
                    harness.CacheLifetimesRequested.Add(expiration);
                    return factory(token);
                });

            // MIGRATION: the catalogue is no longer one wildcard-tolerant read. The repository
            //            contract mirrors the legacy provider, which offered a definition-scoped read
            //            and a code-and-key read, so the service composes the three question shapes
            //            from those two. Both are stubbed from the same Catalogue fixture.
            harness.Permissions
                .Setup(permissions => permissions.GetByModuleDefinitionIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Catalogue);

            harness.Permissions
                .Setup(permissions => permissions.GetByCodeAndKeyAsync(
                    It.IsAny<string>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string code, PermissionKey key, CancellationToken _) => harness.Catalogue
                    .Where(entry => entry.PermissionKey == key
                        && string.Equals(entry.PermissionCode, code, StringComparison.OrdinalIgnoreCase))
                    .ToList());

            // The three IDENTIFYING catalogue reads. Each is stubbed from the same Catalogue fixture, and
            // each reproduces the shape of the terminal procedure it realises, measured from the DDL:
            //   - by identifier: at most one row, so an unknown key yields null.
            //   - by module: the UNION of the module's own definition's entries with every entry carrying
            //     the product-wide SYSTEM_MODULE_DEFINITION code (04.05.03).
            //   - by page: filters on the SYSTEM_TAB code and never references its page argument at all
            //     (04.05.03), so every page receives the identical catalogue.
            harness.Permissions
                .Setup(permissions => permissions.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int permissionId, CancellationToken _) => harness.Catalogue
                    .FirstOrDefault(entry => entry.PermissionId == permissionId));

            harness.Permissions
                .Setup(permissions => permissions.GetByModuleIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Catalogue
                    .Where(entry => entry.ModuleDefinitionId == EntryModuleDefinitionId
                        || string.Equals(
                            entry.PermissionCode,
                            "SYSTEM_MODULE_DEFINITION",
                            StringComparison.OrdinalIgnoreCase))
                    .ToList());

            harness.Permissions
                .Setup(permissions => permissions.GetByTabIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Catalogue
                    .Where(entry => string.Equals(
                        entry.PermissionCode,
                        "SYSTEM_TAB",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList());

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectivePortalPermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => Result<IReadOnlyList<string>>.Success(harness.PortalKeys));

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectiveModulePermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => Result<IReadOnlyList<string>>.Success(harness.ModuleKeys));

            harness.Evaluator
                .Setup(evaluator => evaluator.ListEffectiveTabPermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => Result<IReadOnlyList<string>>.Success(harness.TabKeys));

            harness.Evaluator
                .Setup(evaluator => evaluator.HasModulePermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, PermissionKey, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync(() => Result<bool>.Success(harness.ModuleGrant));

            harness.Evaluator
                .Setup(evaluator => evaluator.HasTabPermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, PermissionKey, int?, IReadOnlyCollection<string>, CancellationToken>(
                    (_, _, _, roleNames, _) => harness.Capture(roleNames))
                .ReturnsAsync((
                    int tabId,
                    PermissionKey key,
                    int? userId,
                    IReadOnlyCollection<string> roleNames,
                    CancellationToken token) => Result<bool>.Success(harness.AnswerPage(tabId, key)));

            harness.Permissions
                .Setup(permissions => permissions.DeleteModulePermissionsByUserIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            harness.Permissions
                .Setup(permissions => permissions.DeleteTabPermissionsByUserIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            return harness;
        }

        /// <summary>
        /// Records the roles the caller was evaluated under.
        /// </summary>
        /// <param name="roleNames">The roles supplied to the store.</param>
        public void Capture(IReadOnlyCollection<string> roleNames)
        {
            CapturedRoleNames.Clear();
            CapturedRoleNames.AddRange(roleNames);
        }

        /// <summary>
        /// Answers a page-level question from the configured page grants.
        /// </summary>
        /// <param name="tabId">The page asked about.</param>
        /// <param name="permissionKey">The permission asked about.</param>
        /// <returns>Whether the page allows it.</returns>
        private bool AnswerPage(int tabId, PermissionKey permissionKey)
            => permissionKey == PermissionKey.VIEW && PageViewGrants.TryGetValue(tabId, out bool granted)
                ? granted
                : TabGrant;
    }

    /// <summary>
    /// Unwraps a successful evaluator answer, failing the test when the evaluator reported a failure.
    /// </summary>
    /// <typeparam name="TValue">The answer type.</typeparam>
    /// <param name="result">The outcome the evaluator produced.</param>
    /// <returns>The answer carried by a successful outcome.</returns>
    /// <remarks>
    /// The evaluator reports its answers as outcomes rather than as bare values, so that a caller can tell
    /// "this caller holds nothing" apart from "the question could not be answered" - two conditions an empty
    /// list conflates, and conflating them silently denies access for an infrastructure reason. Every fact
    /// in this suite is about the ANSWER, so each one asserts that the question was answerable and then reads
    /// the answer; a failure surfaces here as a failing test rather than as an empty list that looks like a
    /// legitimate refusal.
    /// </remarks>
    private static TValue Succeeded<TValue>(Result<TValue> result)
    {
        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        return result.Value;
    }
}
