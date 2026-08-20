using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the permission catalogue, the two grant tables that reference it, the key vocabulary this
/// solution names, and the outcome primitives every permission answer is carried in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariants and sentinel boundaries, and nothing else.</b> This suite asserts what the three entities
/// and the key enumeration guarantee on their own: which properties survived the port, what a freshly
/// constructed instance reports, which legacy sentinel values became honest CLR nulls and which remained
/// real data, and when two instances are the same entity. The catalogue's key is asserted as FREE TEXT,
/// because the column is: the enumeration bounds the keys this solution asks about, never the keys a real
/// installation can hold.
/// </para>
/// <para>
/// <b>Cross-references rather than duplication.</b> These entities carry a <c>RoleId</c>, whose entity-side
/// zero-and-minus-one semantics belong to <c>RoleTests</c>, and a <c>ModuleId</c> and <c>TabId</c>, whose
/// identity semantics belong to <c>ModuleTests</c>. This suite asserts what the permission types themselves
/// guarantee about those values and points at the sibling suites for the rest.
/// </para>
/// </remarks>
public class PermissionTests
{
    /// <summary>The catalogue entry reports its primary key as its identity.</summary>
    [Fact]
    public void CatalogueIdentity_IsThePrimaryKey()
    {
        Permission entry = NewPermission(4, PermissionKey.VIEW);
        Permission sameRow = NewPermission(4, PermissionKey.EDIT);
        Permission otherRow = NewPermission(5, PermissionKey.VIEW);

        // Identity-based comparison applies only once the persistence layer has declared the identity real.
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
            PermissionKey = nameof(PermissionKey.VIEW),
            PermissionName = "View Module",
        };

        entry.PermissionCode.Should().Be("SYSTEM_MODULE_DEFINITION");
        entry.ModuleDefinitionId.Should().Be(7);
        entry.PermissionKey.Should().Be(
            nameof(PermissionKey.VIEW),
            "the key is free text on the same footing as the code beside it, because the column is "
            + "varchar(50) with no check constraint and DotNetNuke lets a third-party module register "
            + "its own keys");
        entry.PermissionName.Should().Be("View Module");
        entry.ModulePermissions.Should().NotBeNull().And.BeEmpty();
        entry.TabPermissions.Should().NotBeNull().And.BeEmpty();

        Permission bare = new() { PermissionId = 2, ModuleDefinitionId = 7 };

        bare.PermissionCode.Should().BeEmpty();
        bare.PermissionName.Should().BeEmpty();
        bare.PermissionKey.Should().BeEmpty(
            "an entry constructed without an explicit key carries the empty string exactly as the code "
            + "and name beside it do, rather than silently reporting whichever key happens to be first "
            + "in the enumeration; the column is NOT NULL, so a caller must say what it means");
    }

    /// <summary>
    /// All five legacy catalogue properties survive the port, two of them renamed, and the legacy spellings
    /// are gone rather than kept alongside as aliases.
    /// </summary>
    [Fact]
    public void Catalogue_KeepsTheFiveLegacyPropertiesUnderTheirRenamedNames()
    {
        Permission entry = new()
        {
            PermissionId = 12,
            PermissionCode = "SYSTEM_TAB",
            ModuleDefinitionId = -1,
            PermissionKey = nameof(PermissionKey.EDIT),
            PermissionName = "Edit Page",
        };

        entry.PermissionId.Should().Be(12);
        entry.PermissionCode.Should().Be("SYSTEM_TAB");
        entry.ModuleDefinitionId.Should().Be(-1);
        entry.PermissionKey.Should().Be(nameof(PermissionKey.EDIT));
        entry.PermissionName.Should().Be("Edit Page");

        // The two renames are structural, not cosmetic, so the legacy spellings must be absent rather than
        // retained as aliases.
        (typeof(Permission).GetProperty("PermissionID") is null).Should().BeTrue(
            "the legacy all-capitals identifier suffix does not survive the port");
        (typeof(Permission).GetProperty("ModuleDefID") is null).Should().BeTrue(
            "ModuleDefID was expanded to ModuleDefinitionId, and the Infrastructure configuration maps "
            + "it back to the legacy column name rather than the entity keeping the legacy spelling");
        (typeof(Permission).GetProperty("ModuleDefinitionId") is null).Should().BeFalse();

        typeof(Permission).GetCustomAttributes(inherit: false).Should().NotContain(
            attribute => attribute.GetType().Namespace == "System.Xml.Serialization",
            "the entity is a domain type only, so every wire concern belongs to the DTOs that answer a "
            + "particular request rather than to the type that models the row");
    }

    /// <summary>
    /// The catalogue's owning definition round-trips every value it can hold, including the system-level
    /// minus one, and cannot express an absence at all.
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
            PermissionKey = nameof(PermissionKey.VIEW),
        };

        Permission definitionScoped = new()
        {
            PermissionId = 2,
            PermissionCode = "SYSTEM_MODULE_DEFINITION",
            ModuleDefinitionId = 1,
            PermissionKey = nameof(PermissionKey.VIEW),
        };

        systemLevel.ModuleDefinitionId.Should().Be(-1);
        definitionScoped.ModuleDefinitionId.Should().Be(1);
        systemLevel.ModuleDefinitionId.Should().NotBe(
            definitionScoped.ModuleDefinitionId,
            "a system-level entry and a definition-scoped entry are different rows, not the same row "
            + "with one value missing");

        // Dbo.ModuleDefinitions.ModuleDefID is declared IDENTITY(1, 1) (01.00.00.SqlDataProvider line 66),
        // so the definition table never issues zero and minus one collides with nothing it does issue.
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

        bare.PermissionId.Should().Be(0, "the legacy empty constructor left the integer at its CLR default");
        bare.ModuleDefinitionId.Should().Be(0);
        bare.PermissionCode.Should().NotBeNull().And.BeEmpty();
        bare.PermissionName.Should().NotBeNull().And.BeEmpty();
        bare.PermissionKey.Should().NotBeNull().And.BeEmpty(
            "the key is hardened exactly as the two text columns beside it are: all three are free text in "
            + "the terminal schema, so all three default to the empty string rather than to null");
        bare.IdentityIsPersisted.Should().BeFalse(
            "a constructed instance has no declared persisted identity until something says so");
    }

    /// <summary>A grant withholds access until something explicitly confers it.</summary>
    [Fact]
    public void Grants_WithholdAccessByDefault()
    {
        ModulePermission moduleGrant = new() { ModulePermissionId = 1, ModuleId = 0, PermissionId = 1 };
        TabPermission tabGrant = new() { TabPermissionId = 1, TabId = 0, PermissionId = 1 };

        moduleGrant.AllowAccess.Should().BeFalse(
            "a grant row created without an explicit decision must not confer access");
        tabGrant.AllowAccess.Should().BeFalse();
    }

    /// <summary>A grant is scoped to a role or to an account, and both scopes may be absent.</summary>
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

    /// <summary>A module grant and a page grant carrying the same numeric key are different entities.</summary>
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

        // The legacy hierarchy is FLATTENED, and this is the assertion that pins it.
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

        // The three legacy display columns go with the inheritance.
        (typeof(ModulePermission).GetProperty("RoleName") is null).Should().BeTrue();
        (typeof(ModulePermission).GetProperty("Username") is null).Should().BeTrue();
        (typeof(ModulePermission).GetProperty("DisplayName") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("RoleName") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("Username") is null).Should().BeTrue();
        (typeof(TabPermission).GetProperty("DisplayName") is null).Should().BeTrue();
    }

    /// <summary>
    /// A freshly constructed grant translates every legacy constructor sentinel instead of carrying it, and
    /// reproduces the one legacy default that was never a sentinel.
    /// </summary>
    [Fact]
    public void Grants_TranslateEveryLegacyConstructorSentinelRatherThanCarryingIt()
    {
        ModulePermission moduleGrant = new();
        TabPermission tabGrant = new();

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

        // The owning module and page identifiers are the one place where translating -1 to a CLR default is
        // NOT harmless, and the target answer is that they are required rather than defaulted.
        // dbo.Modules.ModuleID and dbo.Tabs.TabID are both IDENTITY(0, 1) (01.00.00.SqlDataProvider lines
        // 221 and 140), so the zero these properties report on a bare instance is a REAL identifier - the
        // first module and the first page of an installation - and it is therefore indistinguishable from a
        // deliberate reference to them.
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

        // Nothing in this layer rewrites, clamps or normalises a role subject, and the negative values are
        // the reason that matters. dbo.ModulePermission.RoleID never acquires a foreign key to dbo.Roles
        // across any of the eighty-eight upgrade scripts - only an index, at 04.06.00.SqlDataProvider line
        // 1226 - which is precisely why vw_ModulePermissions reaches roles through a LEFT OUTER JOIN
        // (04.05.00.SqlDataProvider line 685) and synthesises names for the values that resolve to nothing:
        // -1 reads "All Users", -2 "Superuser" and -3 "Unauthenticated Users" (lines 670 to 672).
        moduleGrant.RoleId.Should().NotBeNull("a stored subject is never converted to an absence");
        moduleGrant.RoleId.Should().Be(roleId);
        tabGrant.RoleId.Should().NotBeNull();
        tabGrant.RoleId.Should().Be(roleId);
    }

    /// <summary>
    /// The one place where the whole sentinel argument is stated side by side: minus one was the legacy
    /// absence and is also a real principal, while zero is a real identifier that may never be read as an
    /// absence.
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

        // Minus one is the SECOND, security-relevant collision, and it points the other way.
        // Null.NullInteger is -1, and the legacy constructors used it to mean "absent" for the surrogate
        // key, the owning module or page, and the account.
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

        // The contrast that completes the picture. dbo.ModuleDefinitions.ModuleDefID and dbo.Users.UserID
        // are IDENTITY(1, 1) (01.00.00.SqlDataProvider lines 66 and 98), so neither table ever issues zero
        // and minus one collides with nothing either issues.
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
        // A permission can be granted DIRECTLY TO A USER, not only to a role, and that is a genuine domain
        // fact rather than an artefact. dbo.ModulePermission.UserID did not exist in the original table:
        // 04.05.00.SqlDataProvider lines 640 to 655 add it as int NULL behind a COLUMNPROPERTY guard,
        // together with a foreign key to dbo.Users, and 04.06.00.SqlDataProvider line 1220 indexes it.
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
    /// The catalogue values a grant needs are reached through its navigation, because the legacy copy
    /// constructor that flattened them onto the grant has no target counterpart.
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

        (typeof(ModulePermission).GetConstructor([typeof(Permission)]) is null).Should().BeTrue(
            "there is no copy constructor to keep the flattened columns in step with");
        (typeof(TabPermission).GetConstructor([typeof(Permission)]) is null).Should().BeTrue(
            "and the page grant never had one to begin with");

        grant.PermissionId.Should().Be(entry.PermissionId);
        grant.Permission.Should().BeSameAs(entry);
        grant.Permission.PermissionKey.Should().Be(
            nameof(PermissionKey.VIEW),
            "the five catalogue values the copy constructor used to duplicate are read from the entry");
        grant.Permission.PermissionCode.Should().Be("SYSTEM_MODULE_DEFINITION");
        grant.Permission.ModuleDefinitionId.Should().Be(1);
        grant.Permission.PermissionName.Should().Be("VIEW Module");
        entry.ModulePermissions.Should().BeEmpty(
            "the inverse collection is populated by a read that includes it, not by assigning the navigation");
    }

    /// <summary>
    /// The catalogue's text columns keep whatever was stored, the empty string included, and never report
    /// null in its place.
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

        entry.PermissionCode.Should().NotBeNull();
        entry.PermissionCode.Should().Be(stored);
        entry.PermissionName.Should().NotBeNull();
        entry.PermissionName.Should().Be(stored);
    }

    /// <summary>The permission-key enumeration holds exactly the four legacy keys, in their legacy order.</summary>
    [Fact]
    public void PermissionKeys_AreTheFourLegacyKeys()
    {
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

    /// <summary>A value outside the enumeration is recognised as undefined rather than silently accepted.</summary>
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
    /// Each key's name is the stored literal verbatim, so it round-trips through text without a translation
    /// step and without a casing step.
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

        // The ordinals are incidental and must never be persisted. The members carry no explicit numeric
        // values, so a default enumeration mapping would write 0, 1, 2 or 3 into a varchar column and every
        // stored row and every ported predicate would stop matching.
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
        // The shape of the schema is what forbids a flag enumeration here. A row in dbo.Permission names
        // exactly one action, and holding several actions is several grant rows; combining members would
        // model a grant the tables cannot represent.
        typeof(PermissionKey).GetCustomAttributes(typeof(FlagsAttribute), inherit: false).Should().BeEmpty(
            "a permission key is a single named action, never a combination of them");

        // Context only, asserted nowhere: the legacy application also flattened whole permission sets into
        // semicolon-delimited role-id strings on two nvarchar(256) columns of dbo.Modules and one of
        // dbo.Tabs, which is why the shipped seeds read '-1;', '0;' and '-2;'.
        Enum.GetNames<PermissionKey>().Should().BeEquivalentTo(["VIEW", "EDIT", "READ", "WRITE"]);
    }

    /// <summary>
    /// Comparing an entity with null, or with an object that is not an entity at all, answers false rather
    /// than throwing.
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

        // This corrects a legacy DEFECT that cannot be reproduced, which is the one carve-out the
        // minimal-change discipline allows - a defect is annotated rather than fixed UNLESS reproducing it
        // would block delivery, and reproducing this one would.
        grant.Equals(other).Should().BeFalse();
    }

    /// <summary>Both null-typed overloads and both operators answer for null without throwing.</summary>
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

        // Each overload is exercised on its own instance deliberately.
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
        // MIGRATION: the third equality divergence, and the largest behavioural one.

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

        // MIGRATION: the FOURTH equality divergence, and the quietest.
        TabPermission pageAsRead = new() { TabPermissionId = 5, TabId = 3, PermissionId = 4 };
        TabPermission pageAsEdited = new() { TabPermissionId = 5, TabId = 8, PermissionId = 9, AllowAccess = true };

        pageAsRead.MarkIdentityPersisted();
        pageAsEdited.MarkIdentityPersisted();

        pageAsRead.Should().Be(pageAsEdited, "the page grant is compared the same way the module grant is");
        pageAsRead.GetHashCode().Should().Be(pageAsEdited.GetHashCode());
    }

    /// <summary>
    /// The exact runtime type separates the three permission types even when they carry the same identity
    /// value.
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

    /// <summary>"No role chosen" is an absence rather than the legacy magic number that stood in for one.</summary>
    [Fact]
    public void RoleSubject_ExpressesNoRoleAsAnAbsenceRatherThanTheLegacyMagicNumber()
    {
        ModulePermission moduleGrant = new() { ModulePermissionId = 1, ModuleId = 0, PermissionId = 4 };
        TabPermission pageGrant = new() { TabPermissionId = 1, TabId = 0, PermissionId = 4 };

        moduleGrant.RoleId.Should().BeNull("minus four was a placeholder for an absence, and this IS an absence");
        pageGrant.RoleId.Should().BeNull();

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

        moduleGrant.UserId.Should().BeNull("the account subject is absent on the same terms");
        pageGrant.UserId.Should().BeNull();
    }

    /// <summary>The three pseudo-principals are three different subjects, and none of them is an absence.</summary>
    [Fact]
    public void RoleSubject_KeepsThePseudoPrincipalsDistinctFromEachOtherAndFromAnAbsence()
    {
        ModulePermission everyVisitor = NewModuleGrant(1, roleId: -1);
        ModulePermission superUsers = NewModuleGrant(2, roleId: -2);
        ModulePermission signedOutVisitors = NewModuleGrant(3, roleId: -3);
        ModulePermission noRole = NewModuleGrant(4, roleId: null);

        // The security-relevant collision, stated once more where it is asserted. The legacy absent marker
        // Null.NullInteger is -1, and -1 is ALSO glbRoleAllUsers, the widest subject the model has.
        everyVisitor.RoleId.Should().Be(-1);
        superUsers.RoleId.Should().Be(-2);
        signedOutVisitors.RoleId.Should().Be(-3);
        noRole.RoleId.Should().BeNull();

        everyVisitor.RoleId.Should().NotBe(superUsers.RoleId);
        everyVisitor.RoleId.Should().NotBe(signedOutVisitors.RoleId);
        everyVisitor.RoleId.Should().NotBe(noRole.RoleId, "the widest possible subject is not an absence");
        superUsers.RoleId.Should().NotBe(signedOutVisitors.RoleId);

        NewModuleGrant(5, roleId: 0).RoleId.Should().Be(0, "the shipped Administrators role");
    }

    /// <summary>A success carries no failure detail.</summary>
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

    /// <summary>A success may carry an advisory reason without becoming a failure.</summary>
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

    /// <summary>A failure reports the same reason through both channels.</summary>
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

    /// <summary>A successful permission answer exposes the decision it reached.</summary>
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

    /// <summary>A refusal and a failure are different answers: false is a decision, a failure is not.</summary>
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

    /// <summary>A typed success may also carry an advisory reason.</summary>
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

    /// <summary>A typed failure may be built from a reason as well as from its parts.</summary>
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

    /// <summary>A reason must actually say something: neither part may be blank.</summary>
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

    /// <summary>Two reasons carrying the same parts are the same reason.</summary>
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

    /// <summary>The domain exception carries its message and any inner cause.</summary>
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

    /// <summary>The auditable base adds the two schema audit columns to the entity identity contract.</summary>
    [Fact]
    public void AuditableEntity_AddsTheAuditColumnsToTheIdentityContract()
    {
        AuditedThing thing = new(5);
        AuditedThing sameRow = new(5);

        // Identity-based comparison applies only once the persistence layer has declared the identity real.
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

    /// <summary>Builds a catalogue entry carrying the supplied identifier and key.</summary>
    /// <param name="permissionId">The identifier to carry.</param>
    /// <param name="permissionKey">The permission key.</param>
    /// <returns>The catalogue entry.</returns>
    private static Permission NewPermission(int permissionId, PermissionKey permissionKey) => new()
    {
        PermissionId = permissionId,
        PermissionCode = "SYSTEM_MODULE_DEFINITION",
        ModuleDefinitionId = 1,
        PermissionKey = permissionKey.ToString(),
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

    /// <summary>A minimal concrete auditable entity, present only so the shared base can be exercised.</summary>
    private sealed class AuditedThing : AuditableEntity<int>
    {
        private readonly int _identity;

        /// <summary>Initialises a new instance of the <see cref="AuditedThing"/> class.</summary>
        /// <param name="identity">The identity the instance reports.</param>
        public AuditedThing(int identity) => _identity = identity;

        /// <inheritdoc />
        public override int Identity => _identity;
    }
}
