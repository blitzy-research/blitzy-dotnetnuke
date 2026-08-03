using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

// MIGRATION: the legacy triad this suite stands in for was an INHERITANCE HIERARCHY, and the target
//   FLATTENED it. Library/Components/Security/Permissions/ModulePermission.vb declares
//   "Public Class ModulePermissionInfo" at line 28 followed by "Inherits PermissionInfo" at line 29,
//   and TabPermission.vb does the same at lines 29 and 30, so each grant type declared eight
//   properties of its own and inherited five more from the catalogue class - thirteen accessible
//   members apiece. The target types derive from Entity<int> instead and REFERENCE the catalogue by
//   PermissionId plus a navigation, which is what the schema always described: the grant tables carry
//   a foreign key to Permission.PermissionID with cascade delete (03.00.09.SqlDataProvider lines 488
//   and 492), never the catalogue's columns. Every assertion below was written against the flattened
//   shape actually on disk rather than against the inherited shape, and the tests that pin the
//   flattening are named for it.
//
// MIGRATION: the legacy catalogue class is named PermissionInfo, not Permission, even though its file
//   is Permission.vb (Permission.vb line 28; the class spans lines 28 to 88). Its five properties are
//   PermissionID (line 42), PermissionCode (line 51), ModuleDefID (line 60), PermissionKey (line 69)
//   and PermissionName (line 78), each a Get/Set block over a "Dim" backing field declared at lines 31
//   to 35 - "Dim", not "Private", so a search for private fields finds none of them. The target keeps
//   all five and renames two: ModuleDefID becomes ModuleDefinitionId and the free-text PermissionKey
//   becomes the closed DnnMigration.Domain.Enums.PermissionKey enumeration.
//
// MIGRATION: all XML-serialisation decoration is dropped from the domain. PermissionID,
//   PermissionCode and PermissionKey carried <XmlElement("permissionid")>,
//   <XmlElement("permissioncode")> and <XmlElement("permissionkey")>, and the two members hidden
//   behind <XmlIgnore()> were ModuleDefID and PermissionName. The wire contract now belongs to the
//   DTOs at the API boundary, so no target entity carries an attribute of any kind.
//
// MIGRATION: schema context only, asserted nowhere in this file. The catalogue table is
//   dbo.Permission - SINGULAR - which is anomalous in a schema that is otherwise plural (Portals,
//   Roles, Tabs, Modules, Users, UserRoles, RoleGroups, ModuleDefinitions), and its three string
//   columns are varchar(50) rather than nvarchar: 04.06.00.SqlDataProvider widens PermissionKey with
//   "ALTER COLUMN PermissionKey varchar(50) not null" at lines 397 and 398, then recreates
//   AddPermission at line 404 with @PermissionCode, @PermissionKey and @PermissionName all
//   varchar(50) (lines 405 to 408), inserting four columns into dbo.Permission at lines 411 to 415
//   and returning SCOPE_IDENTITY() at line 423. Pluralising the table name by convention, or letting
//   the provider default those columns to Unicode, would each break AAP Rule T4 - but the mapping
//   that prevents it belongs to the Infrastructure layer, and verifying it belongs to the integration
//   persistence suite. This suite asserts no mapping and touches no database.

/// <summary>
/// Covers the permission catalogue, the two grant tables that reference it, the closed key vocabulary
/// they name, and the outcome primitives every permission answer is carried in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariants and sentinel boundaries, and nothing else.</b> This suite asserts what the three
/// entities and the key enumeration guarantee on their own: which properties survived the port, what a
/// freshly constructed instance reports, which legacy sentinel values became honest CLR nulls and
/// which remained real data, and when two instances are the same entity. It deliberately asserts no
/// evaluation rule - whether an allow beats a refusal, and what an absent grant means, are decisions
/// the component that applies them owns - and no persistence mapping, because that is proved against a
/// real schema elsewhere.
/// </para>
/// <para>
/// <b>The single most important assertion in this file</b> is that a freshly constructed grant is a
/// <em>denial</em>: <c>AllowAccess</c> defaults to false. A grant row that was created without an
/// explicit decision therefore withholds access rather than conferring it, which is the only safe
/// default for a security row and is the opposite of what a reader who skimmed the type name would
/// assume. It is also the one legacy constructor default that the target reproduces exactly:
/// <c>ModulePermission.vb</c> line 48 and <c>TabPermission.vb</c> line 49 both assign
/// <c>_AllowAccess = False</c>.
/// </para>
/// <para>
/// <b>Why the numbers here are treated so carefully.</b> This schema reuses the same integers for
/// contradictory purposes, and the contradiction is concentrated in exactly these three tables. Zero
/// is a real <c>RoleID</c>, a real <c>TabID</c> and a real <c>ModuleID</c> - all three columns are
/// declared <c>IDENTITY(0, 1)</c> (<c>01.00.00.SqlDataProvider</c> lines 115, 140 and 221) and the
/// shipped <c>Administrators</c> role really is <c>RoleID</c> zero (line 7192) - so zero may never be
/// read as "not set". Minus one is what the legacy constructors used to mean "not set", and it is
/// simultaneously <c>glbRoleAllUsers</c>, the pseudo-principal that grants to <em>everyone</em>. Both
/// facts are pinned below, side by side, in
/// <see cref="SentinelAndIdentity_ContradictWithinASingleSchema"/>.
/// </para>
/// <para>
/// <b>Cross-references rather than duplication.</b> These entities carry a <c>RoleId</c>, whose
/// entity-side zero-and-minus-one semantics belong to <c>RoleTests</c>, and a <c>ModuleId</c> and
/// <c>TabId</c>, whose identity semantics belong to <c>ModuleTests</c>. This suite asserts what the
/// permission types themselves guarantee about those values and points at the sibling suites for the
/// rest.
/// </para>
/// <para>
/// <see cref="Result"/>, <see cref="Result{T}"/> and <see cref="ResultReason"/> are covered here rather
/// than in a suite of their own because a permission check is the canonical producer of
/// <c>Result&lt;bool&gt;</c>, and because they are <c>Domain.Common</c> primitives rather than
/// Application concerns. The behaviour that most needs pinning is the advisory channel: a
/// <em>successful</em> result may still carry a reason, and on success <c>Error</c> stays null while
/// <c>Reason</c> does not. Sign-in relies on exactly that to report a shipped credential without
/// failing the request.
/// </para>
/// </remarks>
public class PermissionTests
{
    /// <summary>
    /// The catalogue entry reports its primary key as its identity.
    /// </summary>
    [Fact]
    public void CatalogueIdentity_IsThePrimaryKey()
    {
        Permission entry = NewPermission(4, PermissionKey.VIEW);
        Permission sameRow = NewPermission(4, PermissionKey.EDIT);
        Permission otherRow = NewPermission(5, PermissionKey.VIEW);

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        entry.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        entry.Identity.Should().Be(4);
        entry.Should().Be(sameRow);
        entry.Should().NotBe(otherRow);
    }

    /// <summary>
    /// A catalogue entry is anchored to a permission code and a module definition, not to a tenant.
    /// </summary>
    [Fact]
    public void CatalogueEntry_IsAnchoredToACodeAndADefinition()
    {
        Permission entry = new()
        {
            PermissionId = 1,
            PermissionCode = "SYSTEM_MODULE_DEFINITION",
            ModuleDefinitionId = 7,
            PermissionKey = PermissionKey.VIEW,
            PermissionName = "View Module",
        };

        entry.PermissionCode.Should().Be("SYSTEM_MODULE_DEFINITION");
        entry.ModuleDefinitionId.Should().Be(7);
        entry.PermissionKey.Should().Be(
            PermissionKey.VIEW,
            "the key is the closed enumeration rather than free text, so a misspelling is now a "
            + "compile error instead of a row that silently matches nothing");
        entry.PermissionName.Should().Be("View Module");
        entry.ModulePermissions.Should().NotBeNull().And.BeEmpty();
        entry.TabPermissions.Should().NotBeNull().And.BeEmpty();

        Permission bare = new() { PermissionId = 2, ModuleDefinitionId = 7 };

        bare.PermissionCode.Should().BeEmpty();
        bare.PermissionName.Should().BeEmpty();
        bare.PermissionKey.Should().Be(
            PermissionKey.VIEW,
            "VIEW is the zero member, so an entry constructed without an explicit key reports it; the "
            + "column is NOT NULL and the enumeration declares no absent member, so a caller that "
            + "means something else must say so");
    }

    /// <summary>
    /// All five legacy catalogue properties survive the port, two of them renamed, and the legacy
    /// spellings are gone rather than kept alongside as aliases.
    /// </summary>
    [Fact]
    public void Catalogue_KeepsTheFiveLegacyPropertiesUnderTheirRenamedNames()
    {
        Permission entry = new()
        {
            PermissionId = 12,
            PermissionCode = "SYSTEM_TAB",
            ModuleDefinitionId = -1,
            PermissionKey = PermissionKey.EDIT,
            PermissionName = "Edit Page",
        };

        // Legacy PermissionInfo declared exactly five properties and the target keeps exactly five
        // pieces of catalogue state. The mapping is one-to-one: PermissionID -> PermissionId,
        // PermissionCode -> PermissionCode, ModuleDefID -> ModuleDefinitionId, PermissionKey ->
        // PermissionKey (String -> enumeration) and PermissionName -> PermissionName.
        entry.PermissionId.Should().Be(12);
        entry.PermissionCode.Should().Be("SYSTEM_TAB");
        entry.ModuleDefinitionId.Should().Be(-1);
        entry.PermissionKey.Should().Be(PermissionKey.EDIT);
        entry.PermissionName.Should().Be("Edit Page");

        // MIGRATION: the two renames are structural, not cosmetic, so the legacy spellings must be
        //   absent rather than retained as aliases. Property lookup is case-sensitive, which is what
        //   makes this a real check: the legacy "PermissionID" and "ModuleDefID" differ from the target
        //   "PermissionId" and "ModuleDefinitionId" only by casing and by expansion. A downstream agent
        //   that quietly re-adds either spelling to keep some ported call site compiling would give the
        //   entity two names for one column, and the object-relational mapping would then have to
        //   choose between them.
        (typeof(Permission).GetProperty("PermissionID") is null).Should().BeTrue(
            "the legacy all-capitals identifier suffix does not survive the port");
        (typeof(Permission).GetProperty("ModuleDefID") is null).Should().BeTrue(
            "ModuleDefID was expanded to ModuleDefinitionId, and the Infrastructure configuration maps "
            + "it back to the legacy column name rather than the entity keeping the legacy spelling");
        (typeof(Permission).GetProperty("ModuleDefinitionId") is null).Should().BeFalse();

        // MIGRATION: the legacy class doubled as its own wire format and carried five serialisation
        //   attributes to prove it - three <XmlElement> names in lower case and two <XmlIgnore>
        //   members, ModuleDefID and PermissionName. None of that survives: the target entity carries
        //   no attribute at all, so the lower-case element names disappear with the attributes that
        //   named them and the two formerly hidden members are plainly visible to any caller that
        //   needs them. Both of those members are asserted immediately above, which is the assertion
        //   that would fail if a serialisation concern were ever reintroduced onto this type.
        typeof(Permission).GetCustomAttributes(inherit: false).Should().NotContain(
            attribute => attribute.GetType().Namespace == "System.Xml.Serialization",
            "the entity is a domain type only, so every wire concern belongs to the DTOs that answer a "
            + "particular request rather than to the type that models the row");
    }

    /// <summary>
    /// The catalogue's owning definition round-trips every value it can hold, including the
    /// system-level minus one, and cannot express an absence at all.
    /// </summary>
    /// <param name="moduleDefinitionId">The definition identifier to round-trip.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(-1)]
    public void CatalogueDefinitionOwner_RoundTripsEveryValueExactly(int moduleDefinitionId)
    {
        Permission entry = new() { PermissionId = 1, ModuleDefinitionId = moduleDefinitionId };

        entry.ModuleDefinitionId.Should().Be(
            moduleDefinitionId,
            "nothing in this layer rewrites, clamps or normalises the value");
    }

    /// <summary>
    /// Minus one on the catalogue's owning definition is real system-level data rather than the legacy
    /// absent marker, and zero is a value the definition table never issues.
    /// </summary>
    [Fact]
    public void CatalogueDefinitionOwner_TreatsMinusOneAsSystemLevelRatherThanAnAbsence()
    {
        Permission systemLevel = new()
        {
            PermissionId = 1,
            PermissionCode = "SYSTEM_TAB",
            ModuleDefinitionId = -1,
            PermissionKey = PermissionKey.VIEW,
        };

        Permission definitionScoped = new()
        {
            PermissionId = 2,
            PermissionCode = "SYSTEM_MODULE_DEFINITION",
            ModuleDefinitionId = 1,
            PermissionKey = PermissionKey.VIEW,
        };

        // MIGRATION: this is the one place on the catalogue where minus one is REAL DATA and not the
        //   legacy Null.NullInteger sentinel. The column is int NOT NULL, so minus one cannot possibly
        //   mean "absent" - it means "this entry belongs to no particular module definition", which is
        //   what a SYSTEM_TAB or SYSTEM_FOLDER scope entry is. AAP Rule T7 keeps sentinels out of the
        //   domain, and honouring it here means the property is a plain int rather than an int?: a
        //   nullable property would invite exactly the conversion that must never happen, and would
        //   make a system-level entry indistinguishable from a missing value.
        systemLevel.ModuleDefinitionId.Should().Be(-1);
        definitionScoped.ModuleDefinitionId.Should().Be(1);
        systemLevel.ModuleDefinitionId.Should().NotBe(
            definitionScoped.ModuleDefinitionId,
            "a system-level entry and a definition-scoped entry are different rows, not the same row "
            + "with one value missing");

        // MIGRATION: dbo.ModuleDefinitions.ModuleDefID is declared IDENTITY(1, 1)
        //   (01.00.00.SqlDataProvider line 66), so the definition table never issues zero and minus
        //   one collides with nothing it does issue. That is the deliberate CONTRAST with RoleId,
        //   ModuleId and TabId, whose tables all seed at zero - see
        //   SentinelAndIdentity_ContradictWithinASingleSchema. Read the two facts together: minus one
        //   is a safe out-of-range marker here and a meaningful principal there, and zero is unusable
        //   as a marker there and merely unissued here.
        new Permission { PermissionId = 3, ModuleDefinitionId = 0 }.ModuleDefinitionId
            .Should().Be(0, "zero is stored and read back unchanged even though the table never issues it");
    }

    /// <summary>
    /// A bare catalogue entry reports the legacy empty constructor's defaults, hardened so that neither
    /// text column can be null.
    /// </summary>
    [Fact]
    public void Catalogue_ConstructedBare_HardensTheLegacyEmptyConstructor()
    {
        Permission bare = new();

        // MIGRATION: the base and derived legacy constructors disagreed, and the disagreement is worth
        //   recording because it is what makes this test different from the grant tests below.
        //   PermissionInfo's own constructor is EMPTY - "Public Sub New()" at Permission.vb lines 38
        //   and 39 with no body - so every field took its CLR default: zero for the two integers and
        //   Nothing for the three strings. Both DERIVED constructors, by contrast, sentinel-initialised
        //   every field they touched (ModulePermission.vb lines 43 to 53, TabPermission.vb lines 44 to
        //   54). The target reproduces the base behaviour for the integers and improves on it for the
        //   strings, which default to the empty string rather than to null so that no caller has to
        //   null-check a NOT NULL column.
        bare.PermissionId.Should().Be(0, "the legacy empty constructor left the integer at its CLR default");
        bare.ModuleDefinitionId.Should().Be(0);
        bare.PermissionCode.Should().NotBeNull().And.BeEmpty();
        bare.PermissionName.Should().NotBeNull().And.BeEmpty();
        bare.PermissionKey.Should().Be(PermissionKey.VIEW, "VIEW is the zero member of the enumeration");
        bare.IdentityIsPersisted.Should().BeFalse(
            "a constructed instance has no declared persisted identity until something says so");
    }

    /// <summary>
    /// A grant withholds access until something explicitly confers it.
    /// </summary>
    [Fact]
    public void Grants_WithholdAccessByDefault()
    {
        ModulePermission moduleGrant = new() { ModulePermissionId = 1, ModuleId = 0, PermissionId = 1 };
        TabPermission tabGrant = new() { TabPermissionId = 1, TabId = 0, PermissionId = 1 };

        moduleGrant.AllowAccess.Should().BeFalse(
            "a grant row created without an explicit decision must not confer access");
        tabGrant.AllowAccess.Should().BeFalse();
    }

    /// <summary>
    /// A grant is scoped to a role or to an account, and both scopes may be absent.
    /// </summary>
    [Fact]
    public void Grants_AreScopedToARoleOrToAnAccount()
    {
        ModulePermission roleScoped = new()
        {
            ModulePermissionId = 1,
            ModuleId = 0,
            PermissionId = 1,
            RoleId = 0,
            AllowAccess = true,
        };

        ModulePermission accountScoped = new()
        {
            ModulePermissionId = 2,
            ModuleId = 0,
            PermissionId = 1,
            UserId = 3,
            AllowAccess = false,
        };

        ModulePermission unscoped = new() { ModulePermissionId = 3, ModuleId = 0, PermissionId = 1 };

        roleScoped.RoleId.Should().Be(0, "zero is the administrator role of a fresh installation");
        roleScoped.UserId.Should().BeNull();
        accountScoped.UserId.Should().Be(3);
        accountScoped.RoleId.Should().BeNull();
        unscoped.RoleId.Should().BeNull();
        unscoped.UserId.Should().BeNull();
    }

    /// <summary>
    /// A module grant and a page grant carrying the same numeric key are different entities.
    /// </summary>
    [Fact]
    public void Grants_OfDifferentScopesAreDistinctEntities()
    {
        Entity<int> moduleGrant = new ModulePermission { ModulePermissionId = 1, ModuleId = 0, PermissionId = 1 };
        Entity<int> tabGrant = new TabPermission { TabPermissionId = 1, TabId = 0, PermissionId = 1 };

        moduleGrant.Equals(tabGrant).Should().BeFalse(
            "both grant tables are keyed by their own identity column, so only the aggregate type "
            + "separates a module grant from a page grant that happens to share a number");
    }

    /// <summary>
    /// Each grant type points at the catalogue instead of inheriting from it, so the joined catalogue
    /// columns and the joined display columns are absent from the grant rather than duplicated onto it.
    /// </summary>
    [Fact]
    public void Grants_ReferenceTheCatalogueRatherThanInheritingFromIt()
    {
        ModulePermission moduleGrant = new() { ModulePermissionId = 1, ModuleId = 0, PermissionId = 4 };
        TabPermission tabGrant = new() { TabPermissionId = 1, TabId = 0, PermissionId = 4 };

        // MIGRATION: the legacy hierarchy is FLATTENED, and this is the assertion that pins it.
        //   ModulePermissionInfo inherited PermissionInfo (ModulePermission.vb lines 28 and 29) and so
        //   did TabPermissionInfo (TabPermission.vb lines 29 and 30), which gave each grant thirteen
        //   accessible members: its own eight plus the catalogue's five. The target derives both from
        //   Entity<int> and relates them by PermissionId, matching what the tables always said - a
        //   foreign key from each grant table to Permission.PermissionID, rebuilt with cascade delete
        //   by 03.00.09.SqlDataProvider at lines 488 and 492. The practical gain is that a grant is no
        //   longer indistinguishable from the action it grants.
        moduleGrant.Should().BeAssignableTo<Entity<int>>();
        tabGrant.Should().BeAssignableTo<Entity<int>>();
        moduleGrant.Should().NotBeAssignableTo<Permission>(
            "a grant references the catalogue entry it names; it is not a kind of catalogue entry");
        tabGrant.Should().NotBeAssignableTo<Permission>();
        moduleGrant.PermissionId.Should().Be(4, "the reference is the identifier, not an inherited column");
        tabGrant.PermissionId.Should().Be(4);

        // All three types are sealed, so the flattening cannot be quietly undone by a later type that
        // derives from one of them to get the columns back.
        typeof(Permission).IsSealed.Should().BeTrue();
        typeof(ModulePermission).IsSealed.Should().BeTrue();
        typeof(TabPermission).IsSealed.Should().BeTrue();

        // MIGRATION: the flattening also DISSOLVES a legacy defect rather than porting it. TabPermission.vb
        //   line 35 declares "Dim _permissionKey As String", which SHADOWS the identically named field
        //   PermissionInfo declares at line 34. The inherited PermissionKey property reads the BASE
        //   field while TabPermissionInfo's constructor writes the DERIVED shadow at line 47, so that
        //   write was dead and "New TabPermissionInfo().PermissionKey" returned Nothing rather than the
        //   empty string the constructor plainly intended - which is why that type declares nine
        //   backing fields for only eight properties. Because the target grant carries no PermissionKey
        //   at all, there is no field to shadow and no dead write to reproduce: the defect is not fixed,
        //   patched or annotated around, it simply has nowhere to exist. The key is read from the
        //   catalogue entry through the navigation, which is single-sourced by construction.
        (typeof(ModulePermission).GetProperty("PermissionKey") is null).Should().BeTrue(
            "the key belongs to the catalogue entry, which the grant reaches through its navigation");
        (typeof(TabPermission).GetProperty("PermissionKey") is null).Should().BeTrue(
            "and the shadowed backing field that made the legacy value null therefore has no counterpart");
        (typeof(ModulePermission).GetProperty("PermissionCode") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("PermissionCode") is null).Should().BeTrue();
        (typeof(ModulePermission).GetProperty("PermissionName") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("PermissionName") is null).Should().BeTrue();
        (typeof(ModulePermission).GetProperty("ModuleDefID") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("ModuleDefID") is null).Should().BeTrue();

        // MIGRATION: the three legacy display columns go with the inheritance. ModulePermissionInfo
        //   declared RoleName (line 93), Username (line 120) and DisplayName (line 129), and
        //   TabPermissionInfo declared the same three at lines 85, 112 and 121; each was a
        //   join-flattened copy of a column that belongs to dbo.Roles or dbo.Users. The target reaches
        //   them through the optional Role and User navigations instead, so a grant carries no stale
        //   copy of a name that the principal row can change underneath it.
        (typeof(ModulePermission).GetProperty("RoleName") is null).Should().BeTrue();
        (typeof(ModulePermission).GetProperty("Username") is null).Should().BeTrue();
        (typeof(ModulePermission).GetProperty("DisplayName") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("RoleName") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("Username") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("DisplayName") is null).Should().BeTrue();
    }

    /// <summary>
    /// A freshly constructed grant translates every legacy constructor sentinel instead of carrying it,
    /// and reproduces the one legacy default that was never a sentinel.
    /// </summary>
    [Fact]
    public void Grants_TranslateEveryLegacyConstructorSentinelRatherThanCarryingIt()
    {
        ModulePermission moduleGrant = new();
        TabPermission tabGrant = new();

        // MIGRATION: the legacy constructors are the most concrete sentinel evidence in the whole
        //   codebase, so each of their eight assignments is accounted for here one by one.
        //   ModulePermission.vb lines 43 to 53 assign, in order: _modulePermissionID = Null.NullInteger
        //   (-1), _moduleID = Null.NullInteger (-1), _roleID = Integer.Parse(glbRoleNothing) (-4),
        //   _AllowAccess = False, _RoleName = Null.NullString (the EMPTY STRING, not null), _userID =
        //   Null.NullInteger (-1), _Username = Null.NullString and _DisplayName = Null.NullString.
        //   TabPermission.vb lines 44 to 54 do the same for the page scope, adding the dead
        //   _permissionKey write and omitting the explicit MyBase.New() call that VB then inserts for
        //   it. AAP Rule T7 keeps every one of those sentinels out of the domain.

        // -1 for the surrogate key becomes the plain CLR default, and "not saved yet" is DECLARED
        // rather than deduced from the value.
        moduleGrant.ModulePermissionId.Should().Be(0);
        moduleGrant.IdentityIsPersisted.Should().BeFalse();
        tabGrant.TabPermissionId.Should().Be(0);
        tabGrant.IdentityIsPersisted.Should().BeFalse();

        // -4, glbRoleNothing, becomes null: "no role chosen" is an absence, and an absence is what a
        // nullable column says.
        moduleGrant.RoleId.Should().BeNull("glbRoleNothing was a stand-in for null, and null is available here");
        tabGrant.RoleId.Should().BeNull();

        // -1 for the account becomes null for the same reason.
        moduleGrant.UserId.Should().BeNull();
        tabGrant.UserId.Should().BeNull();

        // False was never a sentinel - it is the safe security default - so it is reproduced exactly.
        moduleGrant.AllowAccess.Should().BeFalse();
        tabGrant.AllowAccess.Should().BeFalse();

        // The three empty-string display columns are gone with the inheritance; the principal is
        // reached through an optional navigation, which reports its absence honestly.
        moduleGrant.Role.Should().BeNull();
        moduleGrant.User.Should().BeNull();
        tabGrant.Role.Should().BeNull();
        tabGrant.User.Should().BeNull();

        // MIGRATION: the owning module and page identifiers are the one place where translating -1 to a
        //   CLR default is NOT harmless, and the target answer is that they are required rather than
        //   defaulted. dbo.Modules.ModuleID and dbo.Tabs.TabID are both IDENTITY(0, 1)
        //   (01.00.00.SqlDataProvider lines 221 and 140), so the zero these properties report on a bare
        //   instance is a REAL identifier - the first module and the first page of an installation - and
        //   it is therefore indistinguishable from a deliberate reference to them. A grant must be
        //   constructed with its owner stated explicitly; nothing may read zero here as "unset", and
        //   the enforced foreign key behind each column is what rejects a grant that names no owner.
        moduleGrant.ModuleId.Should().Be(0, "which is a real ModuleID, not an absence - state the owner explicitly");
        tabGrant.TabId.Should().Be(0, "which is a real TabID, not an absence - state the owner explicitly");
    }

    /// <summary>
    /// Every role subject a grant can name round-trips exactly, including the four negative
    /// pseudo-principals, and none of them collapses to null.
    /// </summary>
    /// <param name="roleId">The role subject to round-trip.</param>
    [Theory]
    [InlineData(-4)]
    [InlineData(-3)]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    public void Grants_RoundTripEveryRoleSubjectWithoutCollapsingItToNull(int roleId)
    {
        ModulePermission moduleGrant = new()
        {
            ModulePermissionId = 1,
            ModuleId = 0,
            PermissionId = 4,
            RoleId = roleId,
        };

        TabPermission tabGrant = new()
        {
            TabPermissionId = 1,
            TabId = 0,
            PermissionId = 4,
            RoleId = roleId,
        };

        // MIGRATION: nothing in this layer rewrites, clamps or normalises a role subject, and the
        //   negative values are the reason that matters. dbo.ModulePermission.RoleID never acquires a
        //   foreign key to dbo.Roles across any of the eighty-eight upgrade scripts - only an index, at
        //   04.06.00.SqlDataProvider line 1226 - which is precisely why vw_ModulePermissions reaches
        //   roles through a LEFT OUTER JOIN (04.05.00.SqlDataProvider line 685) and synthesises names
        //   for the values that resolve to nothing: -1 reads "All Users", -2 "Superuser" and -3
        //   "Unauthenticated Users" (lines 670 to 672). Those pseudo-principals are persisted,
        //   externally observable data, so a mapping that treated any negative value as absent would
        //   destroy real rows.
        moduleGrant.RoleId.Should().NotBeNull("a stored subject is never converted to an absence");
        moduleGrant.RoleId.Should().Be(roleId);
        tabGrant.RoleId.Should().NotBeNull();
        tabGrant.RoleId.Should().Be(roleId);
    }

    /// <summary>
    /// The one place where the whole sentinel argument is stated side by side: minus one was the legacy
    /// absence and is also a real principal, while zero is a real identifier that may never be read as
    /// an absence.
    /// </summary>
    [Fact]
    public void SentinelAndIdentity_ContradictWithinASingleSchema()
    {
        // A grant naming the first module of an installation, granted to the shipped Administrators
        // role. Every number in it is zero, and every one of them is real.
        ModulePermission everyValueIsZero = new()
        {
            ModulePermissionId = 1,
            ModuleId = 0,
            PermissionId = 4,
            RoleId = 0,
            AllowAccess = true,
        };

        // MIGRATION: zero may NEVER be read as "not set" anywhere in this schema. dbo.Roles.RoleID,
        //   dbo.Tabs.TabID and dbo.Modules.ModuleID are all declared IDENTITY(0, 1)
        //   (01.00.00.SqlDataProvider lines 115, 140 and 221), and the shipped Administrators role
        //   really is RoleID zero - inserted verbatim as (0, 0, 'Administrators', ...) at line 7192
        //   under SET IDENTITY_INSERT. A grant addressed to role zero is the single most common grant a
        //   real installation holds, so a "zero means unsaved" convenience anywhere in the model would
        //   silently discard portal administration itself. This is why Entity<TId> declares persisted
        //   identity instead of deducing it from the value, and why no member on it tests for a default.
        everyValueIsZero.RoleId.Should().Be(0, "zero is the shipped Administrators role");
        everyValueIsZero.RoleId.Should().NotBeNull();
        everyValueIsZero.ModuleId.Should().Be(0, "zero is the first module the installation issued");
        everyValueIsZero.AllowAccess.Should().BeTrue();

        TabPermission pageGrantOnPageZero = new()
        {
            TabPermissionId = 1,
            TabId = 0,
            PermissionId = 4,
            RoleId = 0,
            AllowAccess = true,
        };

        pageGrantOnPageZero.TabId.Should().Be(0, "zero is the first page the installation issued");

        // MIGRATION: minus one is the SECOND, security-relevant collision, and it points the other way.
        //   Null.NullInteger is -1 (Library/Components/Shared/Null.vb), and the legacy constructors used
        //   it to mean "absent" for the surrogate key, the owning module or page, and the account. But
        //   for a ROLE subject, -1 is glbRoleAllUsers (Library/Components/Shared/Globals.vb line 95),
        //   which means GRANT TO EVERYONE - the widest possible subject, not the absence of one. A
        //   nullable mapping that collapsed -1 to null would therefore convert an all-users grant into
        //   no grant at all, quietly narrowing or widening access depending on how the evaluator then
        //   treated the null. The target keeps the two apart by representing absence as null and leaving
        //   every negative subject as the integer it is. RoleTests owns the complementary entity-side
        //   assertion for dbo.Roles itself.
        ModulePermission grantedToEveryone = new()
        {
            ModulePermissionId = 2,
            ModuleId = 0,
            PermissionId = 4,
            RoleId = -1,
            AllowAccess = true,
        };

        ModulePermission grantedToNoRole = new()
        {
            ModulePermissionId = 3,
            ModuleId = 0,
            PermissionId = 4,
            AllowAccess = true,
        };

        grantedToEveryone.RoleId.Should().Be(-1, "minus one is the all-users pseudo-principal");
        grantedToEveryone.RoleId.Should().NotBeNull(
            "so it must survive as data; converting it to an absence would change who the grant reaches");
        grantedToNoRole.RoleId.Should().BeNull("and an absence is a different thing entirely");
        grantedToEveryone.RoleId.Should().NotBe(grantedToNoRole.RoleId);

        // MIGRATION: the contrast that completes the picture. dbo.ModuleDefinitions.ModuleDefID and
        //   dbo.Users.UserID are IDENTITY(1, 1) (01.00.00.SqlDataProvider lines 66 and 98), so neither
        //   table ever issues zero and minus one collides with nothing either issues. Minus one is
        //   consequently a SAFE out-of-range marker on those two columns - the legacy code used exactly
        //   that, testing Null.IsNull(objModulePermission.UserID) to tell a role grant from an account
        //   grant - while it is a MEANINGFUL PRINCIPAL on a role subject. Same integer, three different
        //   meanings across four columns of the same three tables; that is the entire justification for
        //   this suite existing.
        Permission systemLevelEntry = new() { PermissionId = 4, ModuleDefinitionId = -1 };
        ModulePermission accountGrant = new()
        {
            ModulePermissionId = 4,
            ModuleId = 0,
            PermissionId = 4,
            UserId = 1,
            AllowAccess = true,
        };

        systemLevelEntry.ModuleDefinitionId.Should().Be(
            -1,
            "on a definition owner minus one is real system-level data on a NOT NULL column");
        accountGrant.UserId.Should().Be(1, "and the account table starts at one, so zero identifies nobody");
        accountGrant.RoleId.Should().BeNull("an account grant names no role");
    }

    /// <summary>
    /// A grant may address a role or an individual account, on both the module scope and the page scope,
    /// and both shapes round-trip through the identifier and through the navigation.
    /// </summary>
    [Fact]
    public void Grants_AddressARoleOrAnAccountOnBothScopes()
    {
        // MIGRATION: a permission can be granted DIRECTLY TO A USER, not only to a role, and that is a
        //   genuine domain fact rather than an artefact. dbo.ModulePermission.UserID did not exist in
        //   the original table: 04.05.00.SqlDataProvider lines 640 to 655 add it as int NULL behind a
        //   COLUMNPROPERTY guard, together with a foreign key to dbo.Users, and 04.06.00.SqlDataProvider
        //   line 1220 indexes it. Its arrival is what makes an account-addressed grant possible, and
        //   both legacy grant classes surfaced it as UserID plus the two flattened display columns.
        Role administrators = new() { RoleId = 0, RoleName = "Administrators" };
        User account = new() { UserId = 1, Username = "host", DisplayName = "Host Account" };

        ModulePermission roleAddressed = new()
        {
            ModulePermissionId = 1,
            ModuleId = 0,
            PermissionId = 4,
            RoleId = administrators.RoleId,
            Role = administrators,
            AllowAccess = true,
        };

        ModulePermission accountAddressed = new()
        {
            ModulePermissionId = 2,
            ModuleId = 0,
            PermissionId = 4,
            UserId = account.UserId,
            User = account,
            AllowAccess = true,
        };

        TabPermission pageRoleAddressed = new()
        {
            TabPermissionId = 1,
            TabId = 0,
            PermissionId = 4,
            RoleId = administrators.RoleId,
            Role = administrators,
            AllowAccess = true,
        };

        TabPermission pageAccountAddressed = new()
        {
            TabPermissionId = 2,
            TabId = 0,
            PermissionId = 4,
            UserId = account.UserId,
            User = account,
            AllowAccess = false,
        };

        roleAddressed.RoleId.Should().Be(0);
        roleAddressed.UserId.Should().BeNull();
        roleAddressed.Role.Should().BeSameAs(administrators);
        roleAddressed.Role!.RoleName.Should().Be(
            "Administrators",
            "the name is read from the principal rather than copied onto the grant, so it cannot go stale");
        roleAddressed.User.Should().BeNull();

        accountAddressed.UserId.Should().Be(1);
        accountAddressed.RoleId.Should().BeNull();
        accountAddressed.User.Should().BeSameAs(account);
        accountAddressed.User!.Username.Should().Be("host");
        accountAddressed.User!.DisplayName.Should().Be("Host Account");
        accountAddressed.Role.Should().BeNull();

        pageRoleAddressed.RoleId.Should().Be(0);
        pageRoleAddressed.Role!.RoleName.Should().Be("Administrators");
        pageRoleAddressed.UserId.Should().BeNull();

        pageAccountAddressed.UserId.Should().Be(1);
        pageAccountAddressed.User!.Username.Should().Be("host");
        pageAccountAddressed.RoleId.Should().BeNull();
        pageAccountAddressed.AllowAccess.Should().BeFalse(
            "an account-addressed row can refuse as well as confer; the row states one decision either way");
    }

    /// <summary>
    /// The catalogue values a grant needs are reached through its navigation, because the legacy
    /// copy constructor that flattened them onto the grant has no target counterpart.
    /// </summary>
    [Fact]
    public void Grants_ReachTheCatalogueThroughTheNavigationRatherThanACopyConstructor()
    {
        Permission entry = NewPermission(4, PermissionKey.VIEW);

        ModulePermission grant = new()
        {
            ModulePermissionId = 1,
            ModuleId = 0,
            PermissionId = entry.PermissionId,
            Permission = entry,
            RoleId = -1,
            AllowAccess = true,
        };

        // MIGRATION: ModulePermissionInfo carried a second constructor at ModulePermission.vb lines 55
        //   to 63 - "Public Sub New(ByVal permission As PermissionInfo)" - which chained to the
        //   sentinel-initialising constructor and then copied ModuleDefID, PermissionCode, PermissionID,
        //   PermissionKey and PermissionName off the catalogue entry onto itself. It existed only
        //   because the inheritance gave the grant somewhere to put them. TabPermissionInfo never had
        //   the overload at all - it declares one constructor, at TabPermission.vb line 44 - so the
        //   legacy pair was already inconsistent about it. With the hierarchy flattened, the target
        //   drops the overload from both types: the catalogue values are read through the navigation, so
        //   they are single-sourced and cannot drift from the row that owns them.
        (typeof(ModulePermission).GetConstructor([typeof(Permission)]) is null).Should().BeTrue(
            "there is no copy constructor to keep the flattened columns in step with");
        (typeof(TabPermission).GetConstructor([typeof(Permission)]) is null).Should().BeTrue(
            "and the page grant never had one to begin with");

        grant.PermissionId.Should().Be(entry.PermissionId);
        grant.Permission.Should().BeSameAs(entry);
        grant.Permission.PermissionKey.Should().Be(
            PermissionKey.VIEW,
            "the five catalogue values the copy constructor used to duplicate are read from the entry");
        grant.Permission.PermissionCode.Should().Be("SYSTEM_MODULE_DEFINITION");
        grant.Permission.ModuleDefinitionId.Should().Be(1);
        grant.Permission.PermissionName.Should().Be("VIEW Module");
        entry.ModulePermissions.Should().BeEmpty(
            "the inverse collection is populated by a read that includes it, not by assigning the navigation");
    }

    /// <summary>
    /// The catalogue's text columns keep whatever was stored, the empty string included, and never
    /// report null in its place.
    /// </summary>
    /// <param name="stored">The text to round-trip.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("SYSTEM_TAB")]
    [InlineData("system_tab")]
    public void CatalogueText_KeepsTheStoredStringExactlyIncludingTheEmptyOne(string stored)
    {
        Permission entry = new()
        {
            PermissionId = 1,
            PermissionCode = stored,
            PermissionName = stored,
        };

        // MIGRATION: the legacy null contract made this ambiguous and the target does not.
        //   Null.NullString is the EMPTY STRING rather than Nothing, and Null.IsNull("") answers True,
        //   so once a value had been read through the legacy path a SQL NULL and an empty string were
        //   indistinguishable. Here the empty string is a value: it round-trips as itself, it is never
        //   converted to null, and null is never substituted for it. Case is preserved exactly too,
        //   because the scope codes are compared against stored text.
        entry.PermissionCode.Should().NotBeNull();
        entry.PermissionCode.Should().Be(stored);
        entry.PermissionName.Should().NotBeNull();
        entry.PermissionName.Should().Be(stored);
    }

    /// <summary>
    /// The permission-key enumeration holds exactly the four legacy keys, in their legacy order.
    /// </summary>
    [Fact]
    public void PermissionKeys_AreTheFourLegacyKeys()
    {
        // MIGRATION: the vocabulary is FOUR members, not the two that a census of the five in-scope
        //   domain trees alone would suggest. Counting only the code literals finds EDIT and VIEW and
        //   stops there, because those are the two keys the module and page permission checks name. The
        //   catalogue table is wider than the checks that read it: READ and WRITE are seeded against the
        //   SYSTEM_FOLDER scope by four and three upgrade scripts respectively, and READ is not even
        //   confined to that scope - PortalController.vb line 1416 compares a catalogue entry's
        //   PermissionKey against the literal "READ" while seeding a new portal's root folder, so the key
        //   is reachable from portal creation itself. Omitting either would leave part of the catalogue
        //   unrepresentable by a read-only permissions endpoint, and would make a stored row fail to
        //   materialise rather than round-trip. A two-member enumeration was considered and is wrong for
        //   this schema; the count below is the one the terminal schema requires.
        Enum.GetValues<PermissionKey>().Should().HaveCount(4);
        ((int)PermissionKey.VIEW).Should().Be(0);
        ((int)PermissionKey.EDIT).Should().Be(1);
        ((int)PermissionKey.READ).Should().Be(2);
        ((int)PermissionKey.WRITE).Should().Be(3);

        PermissionKey.VIEW.ToString().Should().Be(
            "VIEW",
            "the member names are the stored keys verbatim, in upper case, so a permission check can be "
            + "written against the enumeration and compared against the column without a translation table");
    }

    /// <summary>
    /// A value outside the enumeration is recognised as undefined rather than silently accepted.
    /// </summary>
    [Fact]
    public void PermissionKeys_RejectAnUndefinedValue()
    {
        Enum.IsDefined(default(PermissionKey)).Should().BeTrue("VIEW is the zero member");
        Enum.IsDefined((PermissionKey)4).Should().BeFalse();
        Enum.IsDefined((PermissionKey)(-1)).Should().BeFalse(
            "the permission service tests for a defined member before it reaches the repository, so an "
            + "out-of-range cast is refused rather than resolved to nothing");
    }

    /// <summary>
    /// Each key's name is the stored literal verbatim, so it round-trips through text without a
    /// translation step and without a casing step.
    /// </summary>
    /// <param name="key">The key to round-trip.</param>
    /// <param name="storedLiteral">The literal the legacy code compares against.</param>
    [Theory]
    [InlineData(PermissionKey.VIEW, "VIEW")]
    [InlineData(PermissionKey.EDIT, "EDIT")]
    [InlineData(PermissionKey.READ, "READ")]
    [InlineData(PermissionKey.WRITE, "WRITE")]
    public void PermissionKeys_RoundTripTheStoredLiteralVerbatim(PermissionKey key, string storedLiteral)
    {
        key.ToString().Should().Be(storedLiteral, "the member name IS the stored value");
        Enum.Parse<PermissionKey>(storedLiteral).Should().Be(key);
        Enum.TryParse(storedLiteral, out PermissionKey parsed).Should().BeTrue();
        parsed.Should().Be(key);

        // MIGRATION: the ordinals are incidental and must never be persisted. The members carry no
        //   explicit numeric values, so a default enumeration mapping would write 0, 1, 2 or 3 into a
        //   varchar column and every stored row and every ported predicate would stop matching. The
        //   Infrastructure configuration therefore converts by NAME, which is what this assertion pins:
        //   the name and the stored literal are the same string, so the conversion is an identity.
        storedLiteral.Should().Be(
            storedLiteral.ToUpperInvariant(),
            "a PascalCase member would force every caller to remember an upper-casing transform, and "
            + "forgetting it would produce a value that silently matches no stored row");
    }

    /// <summary>
    /// A key names exactly one action, so the enumeration is not a bit-mask and must never become one.
    /// </summary>
    [Fact]
    public void PermissionKeys_AreNotABitMask()
    {
        // MIGRATION: the shape of the schema is what forbids a flag enumeration here. A row in
        //   dbo.Permission names exactly one action, and holding several actions is several grant rows;
        //   combining members would model a grant the tables cannot represent. Adding [Flags] would also
        //   silently break the by-name conversion, because a combined value has no single member name.
        typeof(PermissionKey).GetCustomAttributes(typeof(FlagsAttribute), inherit: false).Should().BeEmpty(
            "a permission key is a single named action, never a combination of them");

        // MIGRATION: the measured literal sites, recorded because they are the evidence for the
        //   vocabulary and because the count was previously reported wrongly. "EDIT" appears at SIX
        //   sites - ModuleController.vb line 130, PortalSecurity.vb lines 522, 618, 623 and 628, and
        //   TabController.vb line 109 - and "VIEW" at FOUR - ModuleController.vb lines 134, 136 and
        //   1108, and TabController.vb line 110 - for TEN in total. An earlier count of five VIEW sites
        //   double-counted TabController.vb line 109, which reads "EDIT": that line resolves a page's
        //   administrator roles while line 110 immediately below it resolves the viewer roles, so the
        //   two are adjacent and easy to conflate.
        //
        // MIGRATION: one apparent eleventh site is a CONFIRMED FALSE POSITIVE and must not be read as a
        //   key. PortalSettings.vb line 504 compares a site setting named "ControlPanelMode" against the
        //   text "VIEW" and returns a member of that class's own Mode enumeration; it is a user-interface
        //   mode string with no relationship to dbo.Permission, so it warrants no enumeration member.
        //
        // MIGRATION: SecurityAccessLevel is a SEPARATE concept and is deliberately not asserted here.
        //   PortalSecurity.vb lines 45 to 53 declare it "As Integer" with ControlPanel = -3,
        //   SkinObject = -2, Anonymous = -1, View = 0, Edit = 1, Admin = 2 and Host = 3. It types a
        //   module control's required access level rather than naming a catalogue row, so it belongs to
        //   the module suite. Worth recording alongside the two collisions above: its Anonymous = -1 is a
        //   THIRD independent meaning for minus one, distinct from both the legacy absent marker and the
        //   all-users role subject.
        //
        // Context only, asserted nowhere: the legacy application also flattened whole permission sets
        //   into semicolon-delimited role-id strings on two nvarchar(256) columns of dbo.Modules and one
        //   of dbo.Tabs, which is why the shipped seeds read '-1;', '0;' and '-2;'. Those strings are a
        //   denormalised cache of the grant rows, and the target reads the rows instead.
        Enum.GetNames<PermissionKey>().Should().BeEquivalentTo(["VIEW", "EDIT", "READ", "WRITE"]);
    }

    /// <summary>
    /// Comparing an entity with null, or with an object that is not an entity at all, answers false
    /// rather than throwing.
    /// </summary>
    /// <param name="other">The value to compare against.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("SYSTEM_MODULE_DEFINITION")]
    [InlineData(1)]
    public void Equality_TreatsNullAndForeignObjectsAsUnequalWithoutThrowing(object? other)
    {
        ModulePermission grant = new() { ModulePermissionId = 1, ModuleId = 0, PermissionId = 4 };
        grant.MarkIdentityPersisted();

        // MIGRATION: this corrects a legacy DEFECT that cannot be reproduced, which is the one carve-out
        //   the minimal-change discipline allows - a defect is annotated rather than fixed UNLESS
        //   reproducing it would block delivery, and reproducing this one would. ModulePermission.vb line
        //   159 reads "If obj Is Nothing Or Not Me.GetType() Is obj.GetType() Then". The operator is
        //   "Or", not "OrElse", and VB's "Or" does NOT short-circuit, so when obj really is Nothing the
        //   right-hand side is evaluated anyway and obj.GetType() dereferences it: the legacy
        //   Equals(Nothing) THREW a NullReferenceException instead of returning False, violating the
        //   Object.Equals contract that every framework collection depends on. The target cannot behave
        //   that way - the base override forwards through "obj as Entity<TId>", which yields null for
        //   both a null argument and a foreign object - so the corrected contract is asserted here and
        //   the divergence is recorded rather than silently absorbed.
        grant.Equals(other).Should().BeFalse();
    }

    /// <summary>
    /// Both null-typed overloads and both operators answer for null without throwing.
    /// </summary>
    [Fact]
    public void Equality_AnswersForNullThroughEveryOverload()
    {
        ModulePermission grant = new() { ModulePermissionId = 1, ModuleId = 0, PermissionId = 4 };
        grant.MarkIdentityPersisted();

        (grant == null).Should().BeFalse();
        (null == grant).Should().BeFalse("the operator is null-safe from either side");
        (grant != null).Should().BeTrue();

        ModulePermission? absent = null;
        (absent == null).Should().BeTrue("two absences are the same absence");

        // Each overload is exercised on its own instance deliberately. Comparing a reference against a
        // null-valued argument teaches the compiler's null-state analysis that the receiver MIGHT be
        // null - the analysis reads the comparison as evidence about the receiver rather than as a
        // question about it - so any further member access on the same local afterwards is reported as
        // a possible null dereference. Two instances state the intent plainly and keep the nullable
        // contract intact rather than silencing it at the call site.
        object? nullObject = null;
        Entity<int>? nullEntity = null;
        ModulePermission comparedWithNullObject = new() { ModulePermissionId = 2, ModuleId = 0, PermissionId = 4 };
        ModulePermission comparedWithNullEntity = new() { ModulePermissionId = 3, ModuleId = 0, PermissionId = 4 };

        comparedWithNullObject.MarkIdentityPersisted();
        comparedWithNullEntity.MarkIdentityPersisted();

        comparedWithNullObject.Equals(nullObject).Should().BeFalse(
            "the object override forwards a failed cast as null");
        comparedWithNullEntity.Equals(nullEntity).Should().BeFalse(
            "and the typed overload rejects null directly");
    }

    /// <summary>
    /// Equality and the hash code agree: two instances reported equal always produce the same hash code.
    /// </summary>
    [Fact]
    public void Equality_AgreesWithTheHashCode()
    {
        ModulePermission grant = new() { ModulePermissionId = 9, ModuleId = 0, PermissionId = 4 };
        ModulePermission sameRow = new()
        {
            ModulePermissionId = 9,
            ModuleId = 3,
            PermissionId = 7,
            RoleId = -1,
            AllowAccess = true,
        };

        grant.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();

        // MIGRATION: this is the second legacy defect that could not be reproduced. ModulePermissionInfo
        //   overrode Equals (lines 158 to 165) and overrode GetHashCode NOWHERE - a search of the file
        //   finds no such member - so two instances the type called equal could land in different
        //   buckets and become unfindable in a collection that still contained them. In C# that
        //   omission is the compiler's "overrides Object.Equals but does not override
        //   Object.GetHashCode" diagnostic, which this solution promotes to a build failure and whose
        //   suppression list is closed, so the target MUST override it. The base type does, and the
        //   agreement is asserted rather than assumed.
        grant.Equals(sameRow).Should().BeTrue("the identity is the same row");
        grant.GetHashCode().Should().Be(
            sameRow.GetHashCode(),
            "equal instances must share a hash code, or a hash-based collection loses them");
        grant.GetHashCode().Should().Be(grant.GetHashCode(), "and the value is stable across calls");

        ModulePermission unmarked = new() { ModulePermissionId = 9, ModuleId = 0, PermissionId = 4 };

        unmarked.GetHashCode().Should().Be(
            unmarked.GetHashCode(),
            "an instance with no declared persisted identity still hashes stably, by reference");
        unmarked.Equals(grant).Should().BeFalse(
            "and it is not the same entity as a row that has one, which keeps the pair consistent");
    }

    /// <summary>
    /// Equality is decided by the identity, replacing the legacy four-part comparison that ignored the
    /// primary key it was defined on.
    /// </summary>
    [Fact]
    public void Equality_IsByIdentityRatherThanTheLegacyFourPartComparison()
    {
        // MIGRATION: the third equality divergence, and the largest behavioural one. The legacy
        //   ModulePermissionInfo.Equals (ModulePermission.vb lines 158 to 165) returned
        //   "(AllowAccess = perm.AllowAccess) And (ModuleID = perm.ModuleID) And (RoleID = perm.RoleID)
        //   And (PermissionID = perm.PermissionID)" - four columns, DELIBERATELY EXCLUDING its own
        //   primary key ModulePermissionID. Its documentation at lines 149 to 153 says why: it existed
        //   solely to stop duplicates being added to the pre-generics grant collection, whose Contains
        //   used it. That collection produces no target file, so the reason for the rule is gone while
        //   the rule's consequences are not, and the target replaces it with identity equality. Both
        //   directions of the change are asserted below, because the two rules genuinely disagree.

        // Direction one: the legacy rule called these EQUAL - same allow flag, module, role and
        // catalogue reference - while identity equality calls them different rows, which they are.
        ModulePermission firstRow = new()
        {
            ModulePermissionId = 1,
            ModuleId = 3,
            PermissionId = 4,
            RoleId = 0,
            AllowAccess = true,
        };

        ModulePermission secondRow = new()
        {
            ModulePermissionId = 2,
            ModuleId = 3,
            PermissionId = 4,
            RoleId = 0,
            AllowAccess = true,
        };

        firstRow.MarkIdentityPersisted();
        secondRow.MarkIdentityPersisted();

        firstRow.Should().NotBe(
            secondRow,
            "the legacy four-part rule ignored the primary key and would have called these the same "
            + "permission; two distinct rows are two distinct entities");

        // Direction two: the legacy rule called these DIFFERENT - every one of its four columns differs
        // - while identity equality recognises one row read twice and edited in memory, which it is.
        ModulePermission asRead = new()
        {
            ModulePermissionId = 5,
            ModuleId = 3,
            PermissionId = 4,
            RoleId = 0,
            AllowAccess = false,
        };

        ModulePermission asEdited = new()
        {
            ModulePermissionId = 5,
            ModuleId = 8,
            PermissionId = 9,
            RoleId = -1,
            AllowAccess = true,
        };

        asRead.MarkIdentityPersisted();
        asEdited.MarkIdentityPersisted();

        asRead.Should().Be(
            asEdited,
            "identity survives an edit, so a row that has been changed is still the same row - which is "
            + "exactly what change tracking needs and what the legacy rule could not express");
        asRead.GetHashCode().Should().Be(asEdited.GetHashCode());

        // MIGRATION: the FOURTH equality divergence, and the quietest. TabPermissionInfo overrode
        //   neither Equals nor GetHashCode - its last member is DisplayName at TabPermission.vb line 121
        //   and the class ends at line 132 - so a page grant compared by REFERENCE, and two instances
        //   read from the same row were never equal. Porting it onto the shared base gives it identity
        //   equality, so the legacy pair stops being inconsistent: both grant types now answer the same
        //   way, whereas one used a four-column tuple and the other used object identity.
        TabPermission pageAsRead = new() { TabPermissionId = 5, TabId = 3, PermissionId = 4 };
        TabPermission pageAsEdited = new() { TabPermissionId = 5, TabId = 8, PermissionId = 9, AllowAccess = true };

        pageAsRead.MarkIdentityPersisted();
        pageAsEdited.MarkIdentityPersisted();

        pageAsRead.Should().Be(pageAsEdited, "the page grant is compared the same way the module grant is");
        pageAsRead.GetHashCode().Should().Be(pageAsEdited.GetHashCode());
    }

    /// <summary>
    /// The exact runtime type separates the three permission types even when they carry the same
    /// identity value.
    /// </summary>
    [Fact]
    public void Equality_SeparatesTheThreeTypesAtTheSameIdentity()
    {
        Entity<int> catalogueEntry = NewPermission(7, PermissionKey.VIEW);
        Entity<int> moduleGrant = new ModulePermission { ModulePermissionId = 7, ModuleId = 0, PermissionId = 4 };
        Entity<int> pageGrant = new TabPermission { TabPermissionId = 7, TabId = 0, PermissionId = 4 };

        catalogueEntry.MarkIdentityPersisted();
        moduleGrant.MarkIdentityPersisted();
        pageGrant.MarkIdentityPersisted();

        // MIGRATION: this is the ONE part of the legacy equality that survives unchanged, and it is
        //   worth saying so plainly. ModulePermission.vb line 159 compared "Me.GetType() Is
        //   obj.GetType()" - an EXACT runtime type test, not an "is a kind of" test - and the shared
        //   base does exactly the same with "GetType() != other.GetType()". The technique matters here
        //   more than anywhere else in the model: the legacy triad was one of only two inheritance
        //   hierarchies in scope, so a subtype-tolerant comparison would have let a grant equal the
        //   catalogue entry it inherited from whenever their identifiers happened to coincide. All three
        //   tables are keyed by their own IDENTITY column, so colliding identifiers are the normal case
        //   rather than a corner one, and the type is the only thing that separates them.
        catalogueEntry.Identity.Should().Be(7);
        moduleGrant.Identity.Should().Be(7);
        pageGrant.Identity.Should().Be(7);

        catalogueEntry.Equals(moduleGrant).Should().BeFalse();
        moduleGrant.Equals(catalogueEntry).Should().BeFalse("and the answer is symmetric");
        catalogueEntry.Equals(pageGrant).Should().BeFalse();
        moduleGrant.Equals(pageGrant).Should().BeFalse();
        (moduleGrant == pageGrant).Should().BeFalse("the operators follow the same rule");
        (moduleGrant != catalogueEntry).Should().BeTrue();
    }

    /// <summary>
    /// "No role chosen" is an absence rather than the legacy magic number that stood in for one.
    /// </summary>
    [Fact]
    public void RoleSubject_ExpressesNoRoleAsAnAbsenceRatherThanTheLegacyMagicNumber()
    {
        ModulePermission moduleGrant = new() { ModulePermissionId = 1, ModuleId = 0, PermissionId = 4 };
        TabPermission pageGrant = new() { TabPermissionId = 1, TabId = 0, PermissionId = 4 };

        // MIGRATION: the legacy role-subject vocabulary, measured at Library/Components/Shared/Globals.vb
        //   lines 95 to 102 and recorded here because no target constant holds it. All seven members are
        //   declared "As String" - the identifiers are text even though four of them spell integers - and
        //   they are consumed as text, compared at Globals.vb lines 2303 and 2305 through
        //   Convert.ToString(RoleID) rather than numerically:
        //
        //       glbRoleAllUsers       "-1"   grant reaches every visitor
        //       glbRoleSuperUser      "-2"   grant reaches the installation's super users
        //       glbRoleUnauthUser     "-3"   grant reaches signed-out visitors only
        //       glbRoleNothing        "-4"   no role chosen - an in-memory placeholder, never a row
        //       glbRoleAllUsersName   "All Users"
        //       glbRoleSuperUserName  "Superuser"
        //       glbRoleUnauthUserName "Unauthenticated Users"
        //
        //   The first three are persisted, externally observable data and the view that reads grants
        //   synthesises exactly those three display names for them. The fourth is different in kind: it
        //   was only ever a constructor default.
        //
        // MIGRATION: no domain constant for this vocabulary exists on disk, and this suite deliberately
        //   does not introduce one. A test may not invent the production surface it is meant to be
        //   checking, so the vocabulary is recorded above as a measured fact and what is ASSERTED below
        //   is the observable consequence: the value the legacy constructor used to mean "no role
        //   chosen" is not present at all. ModulePermission.vb line 47 assigned
        //   "_roleID = Integer.Parse(glbRoleNothing)", making -4 the default of every freshly
        //   constructed module grant, and TabPermission.vb line 48 did the same for a page grant. Under
        //   AAP Rule T7 the target says null instead, which is what a nullable column means and what
        //   removes the need for any caller to know that -4 was special.
        moduleGrant.RoleId.Should().BeNull("minus four was a placeholder for an absence, and this IS an absence");
        pageGrant.RoleId.Should().BeNull();

        // MIGRATION: the placeholder is not expected in a stored row, but the entity still round-trips it
        //   rather than rewriting it, because a domain entity that silently corrected its own data would
        //   hide a real defect in an installation instead of surfacing it. Distinguishing "the row said
        //   minus four" from "the row said nothing" is exactly the distinction a nullable property buys.
        ModulePermission carryingThePlaceholder = new()
        {
            ModulePermissionId = 2,
            ModuleId = 0,
            PermissionId = 4,
            RoleId = -4,
        };

        carryingThePlaceholder.RoleId.Should().Be(-4, "stored data is reported, not corrected");
        carryingThePlaceholder.RoleId.Should().NotBe(
            moduleGrant.RoleId,
            "so a row that names the placeholder is still distinguishable from a row that names nothing");

        // MIGRATION: worth recording against the AAP itself. Section 0.2.2.1 lists six distinct members
        //   of the excluded Globals module that in-scope code reaches - the host settings reader, the
        //   performance multiplier, the application path, the application map path, the map-path helper
        //   and glbRoleUnauthUserName - and glbRoleNothing is NOT among them. It should be: the two
        //   permission constructors reach it directly, which makes it a seventh member and the only one
        //   reached from a domain constructor rather than from a service or a path helper. The list is
        //   therefore incomplete rather than wrong, and the right home for this vocabulary in the target
        //   is a domain constant, exactly as the AAP treats glbRoleUnauthUserName.
        moduleGrant.UserId.Should().BeNull("the account subject is absent on the same terms");
        pageGrant.UserId.Should().BeNull();
    }

    /// <summary>
    /// The three pseudo-principals are three different subjects, and none of them is an absence.
    /// </summary>
    [Fact]
    public void RoleSubject_KeepsThePseudoPrincipalsDistinctFromEachOtherAndFromAnAbsence()
    {
        ModulePermission everyVisitor = NewModuleGrant(1, roleId: -1);
        ModulePermission superUsers = NewModuleGrant(2, roleId: -2);
        ModulePermission signedOutVisitors = NewModuleGrant(3, roleId: -3);
        ModulePermission noRole = NewModuleGrant(4, roleId: null);

        // MIGRATION: the security-relevant collision, stated once more where it is asserted. The legacy
        //   absent marker Null.NullInteger is -1, and -1 is ALSO glbRoleAllUsers, the widest subject the
        //   model has. A nullable mapping that collapsed -1 to null would convert "grant to everyone"
        //   into "grant to no role", and the consequence would depend entirely on how the evaluator then
        //   read the null - so the failure could as easily widen access as narrow it, and either way it
        //   would happen silently and to the rows a real installation holds most of. The three subjects
        //   below therefore stay three distinct integers, and the absence stays a fourth thing.
        everyVisitor.RoleId.Should().Be(-1);
        superUsers.RoleId.Should().Be(-2);
        signedOutVisitors.RoleId.Should().Be(-3);
        noRole.RoleId.Should().BeNull();

        everyVisitor.RoleId.Should().NotBe(superUsers.RoleId);
        everyVisitor.RoleId.Should().NotBe(signedOutVisitors.RoleId);
        everyVisitor.RoleId.Should().NotBe(noRole.RoleId, "the widest possible subject is not an absence");
        superUsers.RoleId.Should().NotBe(signedOutVisitors.RoleId);

        // Zero completes the set: it is neither negative nor absent, and it is the most common subject a
        // real installation holds. RoleTests owns the dbo.Roles side of this; asserted here only to keep
        // the five cases visible together.
        NewModuleGrant(5, roleId: 0).RoleId.Should().Be(0, "the shipped Administrators role");
    }

    /// <summary>
    /// A success carries no failure detail.
    /// </summary>
    [Fact]
    public void Result_Success_CarriesNoFailureDetail()
    {
        Result outcome = Result.Success();

        outcome.IsSuccess.Should().BeTrue();
        outcome.IsFailure.Should().BeFalse();
        outcome.Reason.Should().BeNull();
        outcome.Error.Should().BeNull();
        outcome.ToString().Should().Be("Success");
    }

    /// <summary>
    /// A success may carry an advisory reason without becoming a failure.
    /// </summary>
    [Fact]
    public void Result_Success_MayCarryAnAdvisoryReason()
    {
        ResultReason advisory = new("auth.insecure_admin_password", "Change the shipped credential.");
        Result outcome = Result.Success(advisory);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().Be(advisory);
        outcome.Error.Should().BeNull(
            "Error is the failure channel, so it stays empty on a success even when a reason is attached - "
            + "this is what lets sign-in report a shipped credential while still signing the caller in");
        outcome.ToString().Should().Be("Success (auth.insecure_admin_password: Change the shipped credential.)");
    }

    /// <summary>
    /// A failure reports the same reason through both channels.
    /// </summary>
    [Fact]
    public void Result_Failure_ReportsTheReasonThroughBothChannels()
    {
        Result outcome = Result.Failure("permission.key_invalid", "The permission key is not recognised.");

        outcome.IsSuccess.Should().BeFalse();
        outcome.IsFailure.Should().BeTrue();
        outcome.Reason.Should().NotBeNull();
        outcome.Reason!.Code.Should().Be("permission.key_invalid");
        outcome.Reason!.Message.Should().Be("The permission key is not recognised.");
        outcome.Error.Should().Be(outcome.Reason);
        outcome.ToString().Should().StartWith("Failure (permission.key_invalid:");
    }

    /// <summary>
    /// A successful permission answer exposes the decision it reached.
    /// </summary>
    /// <param name="granted">The decision under test.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResultOfT_Success_ExposesItsValue(bool granted)
    {
        Result<bool> outcome = Result<bool>.Success(granted);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().Be(granted);
        outcome.Reason.Should().BeNull();
    }

    /// <summary>
    /// A refusal and a failure are different answers: false is a decision, a failure is not.
    /// </summary>
    [Fact]
    public void ResultOfT_DistinguishesARefusalFromAFailure()
    {
        Result<bool> refused = Result<bool>.Success(false);
        Result<bool> failed = Result<bool>.Failure("permission.module_not_found", "No such module.");

        refused.IsSuccess.Should().BeTrue("the check ran and answered no");
        refused.Value.Should().BeFalse();
        failed.IsFailure.Should().BeTrue("the check could not run at all");

        Func<bool> readValue = () => failed.Value;

        readValue.Should().Throw<InvalidOperationException>()
            .WithMessage("*failed result*");
    }

    /// <summary>
    /// A typed success may also carry an advisory reason.
    /// </summary>
    [Fact]
    public void ResultOfT_Success_MayCarryAnAdvisoryReason()
    {
        ResultReason advisory = new("role_assignment.expired_not_removed", "The assignment had already lapsed.");
        Result<int> outcome = Result<int>.Success(7, advisory);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().Be(7);
        outcome.Reason.Should().Be(advisory);
        outcome.Error.Should().BeNull();
    }

    /// <summary>
    /// A typed failure may be built from a reason as well as from its parts.
    /// </summary>
    [Fact]
    public void ResultOfT_Failure_AcceptsAReasonOrItsParts()
    {
        ResultReason reason = new("permission.portal_not_found", "No such portal.");

        Result<IReadOnlyList<string>> fromReason = Result<IReadOnlyList<string>>.Failure(reason);
        Result<IReadOnlyList<string>> fromParts =
            Result<IReadOnlyList<string>>.Failure("permission.portal_not_found", "No such portal.");

        fromReason.IsFailure.Should().BeTrue();
        fromParts.IsFailure.Should().BeTrue();
        fromReason.Reason.Should().Be(fromParts.Reason);
    }

    /// <summary>
    /// A reason must actually say something: neither part may be blank.
    /// </summary>
    /// <param name="code">The code under test.</param>
    /// <param name="message">The message under test.</param>
    [Theory]
    [InlineData(null, "a message")]
    [InlineData("", "a message")]
    [InlineData("   ", "a message")]
    [InlineData("a.code", null)]
    [InlineData("a.code", "")]
    [InlineData("a.code", "   ")]
    public void ResultReason_RejectsABlankPart(string? code, string? message)
    {
        Action construct = () => _ = new ResultReason(code!, message!);

        construct.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Two reasons carrying the same parts are the same reason.
    /// </summary>
    [Fact]
    public void ResultReason_IsComparedByItsParts()
    {
        ResultReason left = new("permission.key_invalid", "The permission key is not recognised.");
        ResultReason right = new("permission.key_invalid", "The permission key is not recognised.");
        ResultReason other = new("permission.key_invalid", "Something else entirely.");

        left.Should().Be(right);
        left.GetHashCode().Should().Be(right.GetHashCode());
        left.Should().NotBe(other);
        left.ToString().Should().Be("permission.key_invalid: The permission key is not recognised.");
    }

    /// <summary>
    /// The domain exception carries its message and any inner cause.
    /// </summary>
    [Fact]
    public void DomainException_CarriesItsMessageAndCause()
    {
        DomainException bare = new();
        DomainException withMessage = new("A PortalGuid cannot be the all-zero GUID.");
        InvalidOperationException cause = new("the underlying problem");
        DomainException withCause = new("wrapped", cause);

        bare.Message.Should().NotBeNullOrWhiteSpace();
        withMessage.Message.Should().Be("A PortalGuid cannot be the all-zero GUID.");
        withCause.Message.Should().Be("wrapped");
        withCause.InnerException.Should().BeSameAs(cause);
        withMessage.Should().BeAssignableTo<Exception>();
    }

    /// <summary>
    /// The auditable base adds the two schema audit columns to the entity identity contract.
    /// </summary>
    [Fact]
    public void AuditableEntity_AddsTheAuditColumnsToTheIdentityContract()
    {
        AuditedThing thing = new(5);
        AuditedThing sameRow = new(5);

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        thing.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();

        thing.Should().BeAssignableTo<Entity<int>>();
        thing.Identity.Should().Be(5);
        thing.CreatedDate.Should().BeNull("an unstamped row reports no creation instant, rather than the epoch");
        thing.LastUpdatedDate.Should().BeNull();

        DateTime stamp = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        thing.CreatedDate = stamp;
        thing.LastUpdatedDate = stamp;

        thing.CreatedDate.Should().Be(stamp);
        thing.LastUpdatedDate.Should().Be(stamp);
        thing.Should().Be(sameRow, "the audit columns are not part of the identity contract");
    }

    /// <summary>
    /// Builds a catalogue entry carrying the supplied identifier and key.
    /// </summary>
    /// <param name="permissionId">The identifier to carry.</param>
    /// <param name="permissionKey">The permission key.</param>
    /// <returns>The catalogue entry.</returns>
    private static Permission NewPermission(int permissionId, PermissionKey permissionKey) => new()
    {
        PermissionId = permissionId,
        PermissionCode = "SYSTEM_MODULE_DEFINITION",
        ModuleDefinitionId = 1,
        PermissionKey = permissionKey,
        PermissionName = permissionKey + " Module",
    };

    /// <summary>
    /// Builds a module grant on the first module of an installation, addressed to the supplied role
    /// subject.
    /// </summary>
    /// <param name="modulePermissionId">The identifier to carry.</param>
    /// <param name="roleId">The role subject, or <see langword="null"/> for a grant naming no role.</param>
    /// <returns>The module grant.</returns>
    private static ModulePermission NewModuleGrant(int modulePermissionId, int? roleId) => new()
    {
        ModulePermissionId = modulePermissionId,
        ModuleId = 0,
        PermissionId = 4,
        RoleId = roleId,
        AllowAccess = true,
    };

    /// <summary>
    /// A minimal concrete auditable entity, present only so the shared base can be exercised.
    /// </summary>
    private sealed class AuditedThing : AuditableEntity<int>
    {
        private readonly int _identity;

        /// <summary>
        /// Initialises a new instance of the <see cref="AuditedThing"/> class.
        /// </summary>
        /// <param name="identity">The identity the instance reports.</param>
        public AuditedThing(int identity) => _identity = identity;

        /// <inheritdoc />
        public override int Identity => _identity;
    }
}
