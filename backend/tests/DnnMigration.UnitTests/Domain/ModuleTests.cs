using System.Reflection;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

// The module aggregate and System.Reflection.Module share a simple name, and this file needs both: the
// reflection type brings in the metadata surface the structural assertions rely on. The alias resolves
// the collision explicitly rather than leaving it to import order, and follows the convention the
// sibling suites already use.
using Module = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the module aggregate: the installed package, the definition and control it publishes, the
/// module instance itself, its placement on a page, the two settings stores, the per-portal grant, and
/// the page type only as far as placement requires.
/// </summary>
/// <remarks>
/// <para>
/// The subject of this suite is the sentinel boundary, not the property bag. The legacy
/// <c>ModuleInfo</c> was one 58-property class that was really a join across <c>Modules</c>,
/// <c>TabModules</c>, <c>ModuleDefinitions</c> and <c>ModuleControls</c>, and its constructor seeded
/// every SQL-nullable member with an encoded sentinel. Splitting that class along the real table
/// boundaries and replacing those sentinels with nullable CLR types is the single largest behavioural
/// decision in the module domain, so every assertion below exists to pin one consequence of it.
/// </para>
/// <para>
/// MIGRATION: the four-way split, as the generated entities actually implement it. Eleven of the 58
/// legacy properties are terminal <c>dbo.Modules</c> columns and stay on <see cref="Module"/>
/// (<c>ModuleID</c>, <c>ModuleDefID</c>, <c>ModuleTitle</c>, <c>AllTabs</c>, <c>IsDeleted</c>,
/// <c>InheritViewPermissions</c>, <c>Header</c>, <c>Footer</c>, <c>StartDate</c>, <c>EndDate</c>,
/// <c>PortalID</c>). Fifteen describe one placement of a module on one page and move to
/// <see cref="TabModule"/> (<c>TabModuleID</c>, <c>TabID</c>, <c>ModuleID</c>, <c>PaneName</c>,
/// <c>ModuleOrder</c>, <c>CacheTime</c>, <c>Alignment</c>, <c>Color</c>, <c>Border</c>,
/// <c>IconFile</c>, <c>Visibility</c>, <c>ContainerSrc</c>, <c>DisplayTitle</c>, <c>DisplayPrint</c>,
/// <c>DisplaySyndicate</c>). Four describe the kind of module and move to
/// <see cref="ModuleDefinition"/>; ten describe its user interface and move to
/// <see cref="ModuleControl"/>; thirteen describe the installed package and move to
/// <see cref="DesktopModule"/>. The remainder produce no member anywhere and are asserted absent
/// below, because reinstating one is the natural instinct when porting a class property by property.
/// </para>
/// <para>
/// MIGRATION: <c>Implements IPropertyAccess</c> is dropped from both <c>ModuleInfo</c> (line 37) and
/// <c>TabInfo</c> (line 41), together with the name-keyed <c>GetProperty</c> accessor and the
/// <c>Cacheability</c> member each declared for it. That interface existed only for the
/// token-replacement subsystem, which this migration excludes.
/// </para>
/// <para>
/// MIGRATION: every XML serialisation attribute is dropped rather than translated, and no JSON
/// replacement is substituted. <c>ModuleInfo</c> carried <c>&lt;XmlRoot("module")&gt;</c> with an
/// <c>&lt;XmlElement&gt;</c>, <c>&lt;XmlArray&gt;</c> or <c>&lt;XmlIgnore&gt;</c> on all 58 members and
/// <c>TabInfo</c> did the same on all 36, because portal templates were serialised straight off the
/// entity. The wire contract now belongs to the Application DTOs, so the domain expresses no
/// serialisation opinion at all. The Domain project declares no package reference, which is what makes
/// that a compile-time fact rather than a convention.
/// </para>
/// <para>
/// The sentinel matrix this file pins, value by value:
/// <list type="bullet">
/// <item><description>
/// <c>-1</c> — the legacy <c>Null.NullInteger</c>, and simultaneously a real <c>Portals.PortalID</c>.
/// Accepted as an identifier everywhere; never absence. Its one surviving guard is
/// <see cref="DesktopModule.SupportedFeatures"/>, where the legacy code read it as "no capabilities"
/// and this suite proves the guard is reproduced.
/// </description></item>
/// <item><description>
/// <c>0</c> — a real <c>Modules.ModuleID</c> and a real <c>Tabs.TabID</c>, because both columns are
/// declared <c>IDENTITY(0, 1)</c>, and also <c>default(int)</c>. Never absence. It is <em>not</em> a
/// real <c>ModuleDefinitions.ModuleDefID</c>, <c>DesktopModules.DesktopModuleID</c> or
/// <c>PortalDesktopModules.PortalDesktopModuleID</c>, because those seed at 1 — which is exactly why
/// the decision is asserted per column rather than once for the aggregate.
/// </description></item>
/// <item><description>
/// <c>""</c> — the legacy <c>Null.NullString</c>. A stored empty string stays an empty string and is
/// never collapsed to null; equally, no domain property is <em>initialised</em> to it, because doing
/// so would plant the sentinel back in the model.
/// </description></item>
/// <item><description>
/// <c>DateTime.MinValue</c> — the legacy <c>Null.NullDate</c>. Now an ordinary date, distinct from the
/// null that expresses absence.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Absence is expressed by a nullable type in every case and never by a reserved value. That is
/// Rule T7: the sentinels survive at the DTO and API boundary, where a legacy consumer can observe
/// them, and nowhere else.
/// </para>
/// <para>
/// Scope. <see cref="Tab"/> is covered here only where module placement requires it — the zero seed,
/// the root marker and the sentinel replacement — because the page aggregate has no test class of its
/// own by design. Permission-key vocabulary belongs to the permission suite, mapping verification
/// belongs to the integration suite, and business-controller resolution belongs to the application
/// suite; none of the three is duplicated here.
/// </para>
/// </remarks>
public class ModuleTests
{
    /// <summary>
    /// The first identifier <c>dbo.Modules</c> can issue, because <c>ModuleID</c> is declared
    /// <c>IDENTITY(0, 1)</c> at <c>01.00.00.SqlDataProvider</c> line 221.
    /// </summary>
    private const int ModuleIdentitySeed = 0;

    /// <summary>
    /// The first identifier <c>dbo.Tabs</c> can issue, because <c>TabID</c> is declared
    /// <c>IDENTITY(0, 1)</c> at <c>01.00.00.SqlDataProvider</c> line 140.
    /// </summary>
    private const int TabIdentitySeed = 0;

    /// <summary>
    /// The first identifier <c>dbo.ModuleDefinitions</c> can issue, because <c>ModuleDefID</c> is
    /// declared <c>IDENTITY(1, 1)</c> at <c>01.00.00.SqlDataProvider</c> line 66. Zero is therefore not
    /// a definition key, which is the contrast that makes the seed decision per column.
    /// </summary>
    private const int ModuleDefinitionIdentitySeed = 1;

    /// <summary>
    /// The first identifier <c>dbo.DesktopModules</c> can issue: <c>DesktopModuleID</c> is declared
    /// <c>IDENTITY(1, 1)</c>, so no package key collides with the legacy integer sentinel.
    /// </summary>
    private const int DesktopModuleIdentitySeed = 1;

    /// <summary>
    /// The first identifier <c>dbo.PortalDesktopModules</c> can issue: <c>PortalDesktopModuleID</c> is
    /// declared <c>IDENTITY(1, 1)</c> at <c>02.02.02.SqlDataProvider</c> line 13 of its create body.
    /// </summary>
    private const int PortalDesktopModuleIdentitySeed = 1;

    /// <summary>
    /// The legacy encoded null for every integral field, from <c>Null.NullInteger</c> in
    /// <c>Library/Components/Shared/Null.vb</c>. Named rather than written as a bare literal so that
    /// each assertion states which of its several meanings is under test.
    /// </summary>
    private const int LegacyNullInteger = -1;

    /// <summary>
    /// The <c>DesktopModuleSupportedFeature.IsPortable</c> member value from
    /// <c>Library/Components/Modules/DesktopModuleInfo.vb</c> line 31.
    /// </summary>
    private const int PortableFeatureBit = 1;

    /// <summary>
    /// The <c>DesktopModuleSupportedFeature.IsSearchable</c> member value from
    /// <c>Library/Components/Modules/DesktopModuleInfo.vb</c> line 32.
    /// </summary>
    private const int SearchableFeatureBit = 2;

    /// <summary>
    /// The <c>DesktopModuleSupportedFeature.IsUpgradeable</c> member value from
    /// <c>Library/Components/Modules/DesktopModuleInfo.vb</c> line 33.
    /// </summary>
    private const int UpgradeableFeatureBit = 4;

    // =============================================================================================
    // DesktopModule.SupportedFeatures — the capability bit field and its negative-value guard
    // =============================================================================================

    /// <summary>
    /// Each capability is reported from its own bit, and a negative bit field reports none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The nine rows are the whole reachable truth table for three bits plus the legacy sentinel. Eight
    /// of them are ordinary mask tests; the ninth is the one that matters, and it is described in
    /// detail on the dedicated test below.
    /// </para>
    /// <para>
    /// Legacy behaviour is <c>GetFeature</c> at
    /// <c>Library/Components/Modules/DesktopModuleInfo.vb</c> lines 220-229, whose single condition at
    /// line 224 reads verbatim:
    /// <c>If SupportedFeatures &gt; Null.NullInteger AndAlso (SupportedFeatures And Feature) = Feature
    /// Then</c>.
    /// </para>
    /// </remarks>
    /// <param name="supportedFeatures">The stored bit field under test.</param>
    /// <param name="expectedPortable">Whether the package must report itself portable.</param>
    /// <param name="expectedSearchable">Whether the package must report itself searchable.</param>
    /// <param name="expectedUpgradeable">Whether the package must report itself upgradeable.</param>
    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(2, false, true, false)]
    [InlineData(3, true, true, false)]
    [InlineData(4, false, false, true)]
    [InlineData(5, true, false, true)]
    [InlineData(6, false, true, true)]
    [InlineData(7, true, true, true)]
    [InlineData(LegacyNullInteger, false, false, false)]
    public void SupportedFeatures_ReportsEachCapabilityFromItsOwnBit(
        int supportedFeatures,
        bool expectedPortable,
        bool expectedSearchable,
        bool expectedUpgradeable)
    {
        DesktopModule package = NewPackage(supportedFeatures);

        package.SupportedFeatures.Should().Be(
            supportedFeatures,
            "the stored bit field is reported back exactly as written, including a negative value");
        package.IsPortable.Should().Be(expectedPortable);
        package.IsSearchable.Should().Be(expectedSearchable);
        package.IsUpgradeable.Should().Be(expectedUpgradeable);
    }

    /// <summary>
    /// A bit field holding the legacy integer sentinel reports no capabilities at all, even though
    /// every capability bit is set in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the sharpest silent-inversion hazard in the module domain, and it is the
    /// reason the guard exists. The legacy test at
    /// <c>Library/Components/Modules/DesktopModuleInfo.vb</c> line 224 is
    /// <c>If SupportedFeatures &gt; Null.NullInteger AndAlso (SupportedFeatures And Feature) = Feature
    /// Then</c> — that is, <c>SupportedFeatures &gt; -1</c> evaluated first, with short-circuiting
    /// <c>AndAlso</c>, so a sentinel bit field never reaches the mask test at all and every capability
    /// reads as <see langword="false"/>.
    /// </para>
    /// <para>
    /// MIGRATION: a naive port that drops the guard and keeps only
    /// <c>(SupportedFeatures &amp; bit) == bit</c> inverts all three answers at once, because -1 is
    /// all-ones in two's complement: <c>-1 &amp; 1 == 1</c>, <c>-1 &amp; 2 == 2</c> and
    /// <c>-1 &amp; 4 == 4</c> are each true. A package whose capability mask was never written would
    /// then be reported portable, searchable <em>and</em> upgradeable, so export, indexing and content
    /// upgrade would all be offered for a module that implements none of them. The arithmetic is
    /// demonstrated inline below rather than described, so the hazard cannot be argued away.
    /// </para>
    /// <para>
    /// The guard is reproduced verbatim as <c>SupportedFeatures &gt; -1</c> in
    /// <c>Domain/Entities/DesktopModule.cs</c>. Preserving it is Minimal Change Clause item 3: identical
    /// inputs must produce identical outcomes.
    /// </para>
    /// </remarks>
    [Fact]
    public void SupportedFeatures_LegacySentinelReportsNoCapabilitiesDespiteEveryBitBeingSet()
    {
        // The premise: the sentinel really does have all three capability bits set.
        (LegacyNullInteger & PortableFeatureBit).Should().Be(PortableFeatureBit);
        (LegacyNullInteger & SearchableFeatureBit).Should().Be(SearchableFeatureBit);
        (LegacyNullInteger & UpgradeableFeatureBit).Should().Be(UpgradeableFeatureBit);

        DesktopModule sentinelMask = NewPackage(LegacyNullInteger);

        // The conclusion: the guard runs first, so none of those bits is ever consulted.
        sentinelMask.IsPortable.Should().BeFalse(
            "the legacy guard rejects a negative bit field before the mask test, and a mask-only port "
            + "would report the opposite");
        sentinelMask.IsSearchable.Should().BeFalse(
            "the legacy guard rejects a negative bit field before the mask test, and a mask-only port "
            + "would report the opposite");
        sentinelMask.IsUpgradeable.Should().BeFalse(
            "the legacy guard rejects a negative bit field before the mask test, and a mask-only port "
            + "would report the opposite");

        // And the guard is a lower bound rather than an equality test, so every negative value is
        // rejected, not only the sentinel itself.
        DesktopModule furtherNegative = NewPackage(int.MinValue);
        furtherNegative.IsPortable.Should().BeFalse();
        furtherNegative.IsSearchable.Should().BeFalse();
        furtherNegative.IsUpgradeable.Should().BeFalse();
    }

    /// <summary>
    /// Each capability bit carries the exact legacy member value, proved through the projection that
    /// reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values are persisted data rather than internal identifiers: they are the individual bits of
    /// <c>DesktopModules.SupportedFeatures</c>, so renumbering one would reinterpret every existing row.
    /// </para>
    /// <para>
    /// Every assertion therefore goes through the real projections rather than through the constants this
    /// file declares. Storing the literal 1, 2 or 4 and asking which capability the entity then reports is
    /// the only form of this test that can detect a production mask being renumbered — an earlier revision
    /// compared the test's own constants to one another and to plain bit arithmetic, so it would have
    /// stayed green through exactly the regression it existed to catch. The masks are private to the
    /// entity, which is why they are reached through their consequences and not read directly.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy <c>DesktopModuleSupportedFeature</c> enumeration (DesktopModuleInfo.vb lines
    /// 30-34) is deliberately not recreated as a Domain type. It was a flags enumeration in everything but
    /// declaration — it carried no <c>&lt;Flags&gt;</c> attribute — and its members were only ever used as
    /// masks over one integer column, so promoting it would publish a persistence detail as domain
    /// vocabulary. The bit values themselves are load-bearing, which is what this test pins.
    /// </para>
    /// </remarks>
    /// <param name="storedValue">The single bit written to the persisted field.</param>
    /// <param name="expectedPortable">Whether that bit must be read as the export capability.</param>
    /// <param name="expectedSearchable">Whether it must be read as the indexing capability.</param>
    /// <param name="expectedUpgradeable">Whether it must be read as the content-upgrade capability.</param>
    [Theory]
    [InlineData(1, true, false, false)]
    [InlineData(2, false, true, false)]
    [InlineData(4, false, false, true)]
    public void SupportedFeatures_EachBitValueIsReadAsExactlyOneCapability(
        int storedValue,
        bool expectedPortable,
        bool expectedSearchable,
        bool expectedUpgradeable)
    {
        DesktopModule package = NewPackage(storedValue);

        package.IsPortable.Should().Be(
            expectedPortable,
            $"the persisted value {storedValue} is read by the entity's own export mask");
        package.IsSearchable.Should().Be(
            expectedSearchable,
            $"the persisted value {storedValue} is read by the entity's own indexing mask");
        package.IsUpgradeable.Should().Be(
            expectedUpgradeable,
            $"the persisted value {storedValue} is read by the entity's own content-upgrade mask");
    }

    /// <summary>
    /// The bits compose additively, so a combined field confers exactly the capabilities it sums.
    /// </summary>
    /// <remarks>
    /// The values are powers of two, which is what makes a combined field readable as a sum as well as a
    /// union — and it is why the truth table above can be written as the plain integers 0 through 7. The
    /// combinations are asserted through the entity as well, so a production mask that stopped being a
    /// distinct power of two would surface here as a capability bleeding into a value that never claimed
    /// it, rather than as arithmetic the test performed on itself.
    /// </remarks>
    /// <param name="storedValue">The combined bit field written to the persisted field.</param>
    /// <param name="expectedPortable">Whether the export capability must be reported.</param>
    /// <param name="expectedSearchable">Whether the indexing capability must be reported.</param>
    /// <param name="expectedUpgradeable">Whether the content-upgrade capability must be reported.</param>
    [Theory]
    [InlineData(PortableFeatureBit + SearchableFeatureBit, true, true, false)]
    [InlineData(PortableFeatureBit + UpgradeableFeatureBit, true, false, true)]
    [InlineData(SearchableFeatureBit + UpgradeableFeatureBit, false, true, true)]
    [InlineData(PortableFeatureBit + SearchableFeatureBit + UpgradeableFeatureBit, true, true, true)]
    public void SupportedFeatures_ComposeAdditivelyThroughTheEntity(
        int storedValue,
        bool expectedPortable,
        bool expectedSearchable,
        bool expectedUpgradeable)
    {
        DesktopModule package = NewPackage(storedValue);

        package.IsPortable.Should().Be(expectedPortable);
        package.IsSearchable.Should().Be(expectedSearchable);
        package.IsUpgradeable.Should().Be(expectedUpgradeable);

        // The sum and the union agree, which is the property that lets a combined field be written either
        // way. Asserted on the value the entity was actually handed rather than on the constants alone.
        (PortableFeatureBit | SearchableFeatureBit | UpgradeableFeatureBit).Should().Be(
            PortableFeatureBit + SearchableFeatureBit + UpgradeableFeatureBit,
            "each bit is a distinct power of two, so no two of them overlap");
        package.SupportedFeatures.Should().Be(storedValue, "the field is reported back exactly as written");
    }

    /// <summary>
    /// A freshly constructed package reports no capabilities, and does so by a different route from the
    /// sentinel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy constructor at <c>Library/Components/Modules/DesktopModuleInfo.vb</c> lines 58-59 is
    /// empty, so a new package left <c>SupportedFeatures</c> at the CLR default of zero rather than at
    /// the sentinel — unlike <c>ModuleInfo</c>, whose constructor seeded five identifiers with -1. Zero
    /// then passes the negative-value guard and fails all three mask tests.
    /// </para>
    /// <para>
    /// Both routes to "no capabilities" are asserted together because they are not the same route, and
    /// a port that collapsed them — by seeding the field at -1 to mean "unset", say — would be
    /// indistinguishable here while diverging the moment a real bit was written.
    /// </para>
    /// </remarks>
    [Fact]
    public void SupportedFeatures_DefaultConstructedPackageReportsNoCapabilitiesByTheZeroRoute()
    {
        DesktopModule fresh = new();

        fresh.SupportedFeatures.Should().Be(
            0,
            "the legacy constructor is empty, so the field starts at the CLR default and not at the "
            + "sentinel the ModuleInfo constructor used");
        fresh.IsPortable.Should().BeFalse();
        fresh.IsSearchable.Should().BeFalse();
        fresh.IsUpgradeable.Should().BeFalse();

        // The two routes reach the same answers through different comparisons: zero satisfies the
        // guard and fails the masks, while the sentinel fails the guard and never reaches the masks.
        DesktopModule sentinelMask = NewPackage(LegacyNullInteger);
        sentinelMask.SupportedFeatures.Should().NotBe(fresh.SupportedFeatures);
        sentinelMask.IsPortable.Should().Be(fresh.IsPortable);
        sentinelMask.IsSearchable.Should().Be(fresh.IsSearchable);
        sentinelMask.IsUpgradeable.Should().Be(fresh.IsUpgradeable);
    }

    /// <summary>
    /// Capabilities are composed by assigning the whole bit field, and cleared the same way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy <c>SetFeature</c> (lines 231-236, <c>SupportedFeatures Or Feature</c>) and
    /// <c>ClearFeature</c> (lines 213-218, <c>SupportedFeatures And (Not Feature)</c>) were reached
    /// through three read/write facade properties whose setters called <c>UpdateFeature</c> (lines
    /// 238-246). Those setters are not carried forward: three read-modify-write facades over one shared
    /// field lose an update whenever two capabilities are set from separately read copies. The bit
    /// arithmetic they performed is preserved and is asserted here against the one writable member.
    /// </para>
    /// <para>
    /// The reduction from read/write flags to read-only projections is recorded in
    /// <c>MIGRATION_NOTES.md</c>. This test asserts the read direction, which is all the target
    /// exposes, plus the composition and clearing the removed setters used to perform.
    /// </para>
    /// </remarks>
    [Fact]
    public void SupportedFeatures_ComposesAndClearsThroughTheWholeBitField()
    {
        DesktopModule package = NewPackage(0);

        // Set, one capability at a time, exactly as the legacy SetFeature did.
        package.SupportedFeatures |= PortableFeatureBit;
        package.IsPortable.Should().BeTrue();
        package.IsSearchable.Should().BeFalse();

        package.SupportedFeatures |= SearchableFeatureBit;
        package.SupportedFeatures.Should().Be(3);
        package.IsPortable.Should().BeTrue();
        package.IsSearchable.Should().BeTrue();
        package.IsUpgradeable.Should().BeFalse();

        // Clear, exactly as the legacy ClearFeature did, and only the named capability moves.
        package.SupportedFeatures &= ~PortableFeatureBit;
        package.SupportedFeatures.Should().Be(SearchableFeatureBit);
        package.IsPortable.Should().BeFalse();
        package.IsSearchable.Should().BeTrue();

        // Setting the same capability twice is idempotent, which is why the legacy code could call
        // SetFeature unconditionally.
        package.SupportedFeatures |= SearchableFeatureBit;
        package.SupportedFeatures.Should().Be(SearchableFeatureBit);
    }

    /// <summary>
    /// Bits beyond the three documented capabilities survive a read and a write untouched.
    /// </summary>
    /// <remarks>
    /// Normalising a value the column already accepts would lose information on a read-modify-write
    /// cycle, and the legacy code normalised nothing: <c>GetFeature</c> tested one bit and ignored the
    /// rest. A future capability written by a newer installation therefore round-trips through this
    /// model rather than being silently stripped by it.
    /// </remarks>
    [Fact]
    public void SupportedFeatures_PreservesBitsBeyondTheThreeDocumentedCapabilities()
    {
        const int unknownCapability = 8;

        DesktopModule package = NewPackage(unknownCapability | PortableFeatureBit);

        package.SupportedFeatures.Should().Be(
            9,
            "an unrecognised bit is stored data, so it is neither validated nor stripped");
        package.IsPortable.Should().BeTrue();
        package.IsSearchable.Should().BeFalse();
        package.IsUpgradeable.Should().BeFalse();
    }

    /// <summary>
    /// The three capability properties are read-only projections over the stored bit field.
    /// </summary>
    /// <remarks>
    /// Asserted by reflection so that reinstating a setter — the natural instinct when porting the
    /// legacy class member for member — fails the build rather than silently reintroducing the
    /// lost-update defect described above.
    /// </remarks>
    [Fact]
    public void DesktopModule_CapabilityFlagsAreReadOnlyProjectionsOverOneWritableField()
    {
        PropertyOf<DesktopModule>(nameof(DesktopModule.SupportedFeatures)).CanWrite.Should().BeTrue(
            "the bit field is the single writable member of the capability group");

        foreach (string flagName in new[]
        {
            nameof(DesktopModule.IsPortable),
            nameof(DesktopModule.IsSearchable),
            nameof(DesktopModule.IsUpgradeable),
        })
        {
            PropertyInfo flag = PropertyOf<DesktopModule>(flagName);

            flag.CanRead.Should().BeTrue();
            flag.CanWrite.Should().BeFalse(
                $"{flagName} is a projection: a setter over a shared bit field loses updates");
            flag.PropertyType.Should().Be(typeof(bool));
        }
    }

    /// <summary>
    /// The package carries exactly the sixteen members the legacy class declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DesktopModuleInfo</c> declared thirteen stored properties over thirteen backing fields plus
    /// the three computed capability flags. Sixteen is therefore the whole legacy contract rather than a
    /// chosen subset of it, so any further data member here would be an invention and any missing one
    /// would be a silent loss.
    /// </para>
    /// <para>
    /// MIGRATION: the <c>...Info</c> suffix is dropped, because it existed only to distinguish a data
    /// carrier from its static <c>...Controller</c> companion and the layer boundary draws that
    /// distinction now. The private backing fields, the empty constructor and the four bit-manipulation
    /// helpers have no counterpart: auto-properties express the same contract.
    /// </para>
    /// </remarks>
    [Fact]
    public void DesktopModule_CarriesTheSixteenLegacyMembersAndNoOthers()
    {
        DataPropertyNames(typeof(DesktopModule)).Should().BeEquivalentTo(
            "DesktopModuleId",
            "FriendlyName",
            "Description",
            "Version",
            "IsPremium",
            "IsAdmin",
            "BusinessControllerClass",
            "FolderName",
            "ModuleName",
            "SupportedFeatures",
            "CompatibleVersions",
            "Dependencies",
            "Permissions",
            "IsPortable",
            "IsSearchable",
            "IsUpgradeable");
    }

    /// <summary>
    /// A freshly constructed package reproduces the empty legacy constructor: nothing is seeded, so no
    /// legacy sentinel is planted anywhere on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy <c>NullString</c> is the empty string rather than <see langword="null"/>
    /// (<c>Library/Components/Shared/Null.vb</c>), but <c>DesktopModuleInfo</c> never seeded it: its
    /// constructor is empty, so every string member started as <see langword="null"/>. The target
    /// reproduces that exactly. No string property on this entity carries an initialiser, and none may
    /// acquire one, because initialising to <c>string.Empty</c> would reinstate the sentinel in the
    /// domain and make "stored blank" indistinguishable from "never written".
    /// </para>
    /// <para>
    /// The three non-nullable string members are non-nullable because their columns are
    /// <c>NOT NULL</c>; the compiler's <c>CS8618</c> warning for them is suppressed solution-wide
    /// because these entities are materialised by the persistence layer, which is the assignment the
    /// compiler cannot see. A default-constructed instance is therefore a legitimately incomplete
    /// object, and that is what this test records.
    /// </para>
    /// </remarks>
    [Fact]
    public void DesktopModule_DefaultConstructedInstanceReproducesTheEmptyLegacyConstructor()
    {
        DesktopModule fresh = new();

        fresh.DesktopModuleId.Should().Be(0, "the empty legacy constructor seeded no identifier either");
        fresh.SupportedFeatures.Should().Be(0);
        fresh.IsPremium.Should().BeFalse();
        fresh.IsAdmin.Should().BeFalse();

        fresh.Description.Should().BeNull();
        fresh.Version.Should().BeNull();
        fresh.BusinessControllerClass.Should().BeNull();
        fresh.CompatibleVersions.Should().BeNull();
        fresh.Dependencies.Should().BeNull();
        fresh.Permissions.Should().BeNull();

        fresh.ModuleDefinitions.Should().NotBeNull().And.BeEmpty();
        fresh.PortalDesktopModules.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// The stored controller-class name is inert data on this entity and is never an activation
    /// instruction.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy code handed this column to
    /// <c>Framework.Reflection.CreateObject(objModule.BusinessControllerClass)</c> at
    /// <c>Library/Components/Modules/ModuleController.vb</c> lines 231 and 431, so a value in the
    /// database instructed the server to load an assembly and construct a type. Those are .NET
    /// reflection call sites and not COM interop — the COM-interop exclusion is vacuous against this
    /// codebase. The capability is not carried forward: a business controller is resolved from a
    /// closed, dependency-injected set by an Application-layer factory. This suite therefore asserts
    /// only that the string round-trips as data, and deliberately performs no activation of any kind.
    /// </remarks>
    [Fact]
    public void DesktopModule_TreatsTheBusinessControllerClassAsOpaqueText()
    {
        const string storedTypeName = "DotNetNuke.Modules.Html.HtmlTextController, DotNetNuke.HtmlText";

        DesktopModule package = NewPackage(PortableFeatureBit);
        package.BusinessControllerClass = storedTypeName;

        package.BusinessControllerClass.Should().Be(
            storedTypeName,
            "the value is stored and returned verbatim, with no parsing and no resolution");
        package.IsPortable.Should().BeTrue(
            "the legacy export guard read the controller name together with the portable bit, which is "
            + "why the column is retained at all");
    }

    // =============================================================================================
    // Identity seeds — the per-column decision, and the equality rule built on it
    // =============================================================================================

    /// <summary>
    /// A module reports its primary key as its identity, and every value an identity column can issue is
    /// accepted as one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>dbo.Modules.ModuleID</c> is declared <c>IDENTITY(0, 1)</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 221 — a fact
    /// the action plan's body does not state — so <b>zero is a real, persisted module key</b> owned by
    /// the first row ever inserted. It must never be read as absent, transient, unsaved or "default",
    /// and no <c>ModuleId == 0</c>, <c>ModuleId &lt;= 0</c> or <c>default(int)</c> heuristic may be
    /// written against it anywhere.
    /// </para>
    /// <para>
    /// MIGRATION: -1 is accepted too, and for a different reason. The legacy constructor seeded
    /// <c>_ModuleID</c> with <c>Null.NullInteger</c> (<c>ModuleInfo.vb</c> line 108), so -1 was the
    /// legacy in-memory marker for "no module yet". The target expresses that state through
    /// <see cref="Entity{TId}.IdentityIsPersisted"/> instead, which leaves -1 as an ordinary integer the
    /// property must carry without reinterpretation. Two different absence conventions therefore
    /// collapse into one, and neither -1 nor 0 survives as a reserved value.
    /// </para>
    /// </remarks>
    /// <param name="moduleId">The identifier under test.</param>
    [Theory]
    [InlineData(LegacyNullInteger)]
    [InlineData(ModuleIdentitySeed)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void ModuleIdentity_IsThePrimaryKeyForEveryValueTheColumnCanHold(int moduleId)
    {
        Module module = NewModule(moduleId);
        Module sameRow = new() { ModuleId = moduleId, ModuleDefinitionId = 99 };

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared the
        // identity real. That declaration is what these two calls stand in for: every candidate "not
        // saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and the role,
        // page and module tables at 0 - so an undeclared instance is compared by object reference
        // instead, and two separately constructed instances are two different entities.
        module.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();

        module.Identity.Should().Be(moduleId);
        module.Should().Be(sameRow, "the key alone decides which row an entity is");
        module.Should().NotBe(NewPersistedModule(moduleId == 1 ? 2 : 1));
    }

    /// <summary>
    /// A page reports its primary key as its identity, and zero is a real page.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>dbo.Tabs.TabID</c> is declared <c>IDENTITY(0, 1)</c> at
    /// <c>01.00.00.SqlDataProvider</c> line 140 — the second seed the action plan's body does not state —
    /// and the shipped Home page is seeded with <c>PortalID = 0</c> in the same script. Zero is
    /// therefore a real page key as well as a real module key.
    /// </remarks>
    [Fact]
    public void TabIdentity_IsThePrimaryKeyAndZeroIsARealPage()
    {
        Tab tab = NewTab(TabIdentitySeed);
        Tab sameRow = new() { TabId = TabIdentitySeed, TabName = "Something Else" };
        Tab otherRow = NewTab(1);

        tab.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        tab.Identity.Should().Be(TabIdentitySeed);
        tab.Should().Be(sameRow);
        tab.Should().NotBe(otherRow);
    }

    /// <summary>
    /// The identity seed is a per-column fact, so zero is a key for some of these tables and not for
    /// others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion that stops the zero rule from being over-generalised. Measured seeds, all
    /// from <c>01.00.00.SqlDataProvider</c> unless noted:
    /// <c>ModuleDefID IDENTITY(1, 1)</c> at line 66, <c>PortalID IDENTITY(-1, 1)</c> at line 77,
    /// <c>RoleID IDENTITY(0, 1)</c> at line 115, <c>TabID IDENTITY(0, 1)</c> at line 140,
    /// <c>ModuleID IDENTITY(0, 1)</c> at line 221, <c>UserRoleID IDENTITY(1, 1)</c> at line 239, and
    /// <c>PortalDesktopModuleID IDENTITY(1, 1)</c> in the create body at
    /// <c>02.02.02.SqlDataProvider</c>.
    /// </para>
    /// <para>
    /// The consequence is that no single rule covers the aggregate: a zero module key and a zero page
    /// key identify real rows, while a zero definition key, package key or grant key identifies no row
    /// at all. Rather than encode that difference as a value check — which is precisely the mistake the
    /// legacy sentinel system institutionalised — the target answers "does this row exist" through the
    /// persistence layer's declaration, uniformly, for every entity.
    /// </para>
    /// </remarks>
    [Fact]
    public void IdentitySeeds_DifferPerTableSoZeroIsAKeyForSomeTablesAndNotOthers()
    {
        ModuleIdentitySeed.Should().Be(0);
        TabIdentitySeed.Should().Be(0);
        ModuleDefinitionIdentitySeed.Should().Be(1);
        DesktopModuleIdentitySeed.Should().Be(1);
        PortalDesktopModuleIdentitySeed.Should().Be(1);

        // A definition, a package and a grant all seed at 1, so zero is not one of their keys - yet each
        // still carries zero without complaint, because rejecting it would be a validation rule the
        // column does not have and the entity is not the place to invent one.
        NewDefinition(0).Identity.Should().Be(0);
        NewPackage(0).DesktopModuleId.Should().Be(0);
        new PortalDesktopModule { PortalDesktopModuleId = 0, PortalId = 0, DesktopModuleId = 1 }
            .Identity.Should().Be(0);

        // The real seeds are ordinary identities on the same types.
        NewDefinition(ModuleDefinitionIdentitySeed).Identity.Should().Be(1);
        NewPackage(0, DesktopModuleIdentitySeed).DesktopModuleId.Should().Be(1);
    }

    /// <summary>
    /// A module, a page, a definition and a package that share a numeric key are four different
    /// entities.
    /// </summary>
    /// <remarks>
    /// Equality compares the exact runtime type with <see cref="object.GetType"/> before it compares
    /// identities, so a shared key value never crosses an aggregate boundary. Zero is used deliberately:
    /// it is the value two of these tables actually issue, which is what makes the type component of
    /// equality load-bearing rather than decorative.
    /// </remarks>
    [Fact]
    public void Equality_DoesNotHoldAcrossAggregatesThatShareAKeyValue()
    {
        Entity<int> module = NewPersistedModule(0);
        Entity<int> tab = NewPersistedTab(0);
        Entity<int> definition = NewPersistedDefinition(0);
        Entity<int> package = NewPersistedPackage(0);

        module.Equals(tab).Should().BeFalse(
            "both tables seed at zero, which is precisely why the aggregate type has to take part in "
            + "equality");
        module.Equals(definition).Should().BeFalse();
        module.Equals(package).Should().BeFalse();
        tab.Equals(definition).Should().BeFalse();
        definition.Equals(package).Should().BeFalse();

        module.GetHashCode().Should().NotBe(
            tab.GetHashCode(),
            "the hash combines the runtime type with the identity, so the two do not collide either");
    }

    /// <summary>
    /// Two modules carrying the same key are different entities until the persistence layer says the key
    /// is the database's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the invariant that makes the zero seed survivable. <c>dbo.Modules</c>, <c>dbo.Tabs</c> and
    /// <c>dbo.Roles</c> all seed their identity column at zero, so if identity-based equality applied
    /// before the database had spoken, every newly constructed module would equal every other newly
    /// constructed module — and a set of unsaved modules would silently collapse to one element.
    /// </para>
    /// <para>
    /// The declaration is one-way and idempotent: an entity does not stop having been written. Calling
    /// it twice is harmless and is asserted here so that repository code may call it unconditionally.
    /// </para>
    /// </remarks>
    [Fact]
    public void Equality_IsReferenceBasedUntilTheIdentityIsDeclaredPersisted()
    {
        Module first = NewModule(ModuleIdentitySeed);
        Module second = NewModule(ModuleIdentitySeed);

        first.IdentityIsPersisted.Should().BeFalse("nothing has declared the key real yet");
        first.Should().NotBe(second, "two unsaved modules are two entities even at the zero seed");
        first.Should().Be(first, "an instance is always itself, by reference");

        first.MarkIdentityPersisted();
        first.Should().NotBe(second, "the declaration has to hold on both sides before keys are compared");

        second.MarkIdentityPersisted();
        first.Should().Be(second);

        // Idempotent, so a repository may declare unconditionally after materialising a row.
        second.MarkIdentityPersisted();
        first.Should().Be(second);
        second.IdentityIsPersisted.Should().BeTrue();
    }

    /// <summary>
    /// The hash code is consistent with equality, and stays consistent even when an entity is hashed
    /// before its key is declared real.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hash is latched on first use. Without that, a module constructed in memory, filed in a hash
    /// set, and only then given its generated key would answer with a different bucket than the one it
    /// was filed under, and would become unfindable in a collection that still contains it.
    /// </para>
    /// <para>
    /// Latching forces the equality rule to follow it rather than lead it: an instance that published a
    /// reference-based hash keeps comparing by reference afterwards, so the pair can never disagree. The
    /// consequence is asserted rather than assumed, because it is surprising and a well-meant
    /// "simplification" would break the collection invariant it exists to protect.
    /// </para>
    /// </remarks>
    [Fact]
    public void Equality_HashCodeIsLatchedOnFirstUseAndTheComparisonFollowsIt()
    {
        Module hashedEarly = NewModule(ModuleIdentitySeed);
        int publishedHash = hashedEarly.GetHashCode();

        hashedEarly.MarkIdentityPersisted();
        hashedEarly.GetHashCode().Should().Be(publishedHash, "a published hash may never move");

        Module hashedAfterDeclaration = NewPersistedModule(ModuleIdentitySeed);
        hashedEarly.Should().NotBe(
            hashedAfterDeclaration,
            "the early-hashed instance stays on reference comparison so that its published hash remains "
            + "valid for it");

        // The ordinary ordering - materialise, declare, then hash - takes the identity path throughout.
        Module left = NewPersistedModule(7);
        Module right = NewPersistedModule(7);
        left.Should().Be(right);
        left.GetHashCode().Should().Be(right.GetHashCode());
    }

    /// <summary>
    /// Equality copes with a null operand and with a foreign object on both entry points.
    /// </summary>
    [Fact]
    public void Equality_HandlesNullOperandsAndForeignObjects()
    {
        Module module = NewPersistedModule(ModuleIdentitySeed);
        Module? absent = null;

        module.Equals(absent).Should().BeFalse("a null operand is never the same entity");
        module.Equals("not an entity").Should().BeFalse(
            "the untyped override refuses an object that is not an entity of this exact type");

        (module == absent).Should().BeFalse();
        (absent == module).Should().BeFalse("the operator inspects its left operand for null first");
        (module != absent).Should().BeTrue();

        Module? alsoAbsent = null;
        (absent == alsoAbsent).Should().BeTrue("two absent operands are equal");
        (absent != alsoAbsent).Should().BeFalse();

        // The untyped override is exercised against a null literal on an instance of its own, and that
        // instance is deliberately never touched again. Passing null through object.Equals teaches the
        // compiler's null-state analysis that the receiver may itself be null, so every later use of the
        // same local would be reported as a possible null dereference. Isolating the call keeps the
        // assertion while leaving the suppression list closed - a pragma here is forbidden.
        Module untypedNullOperand = NewPersistedModule(ModuleIdentitySeed);
        untypedNullOperand.Equals((object?)null).Should().BeFalse();
    }

    /// <summary>
    /// No entity in this aggregate offers a way to deduce whether it has been saved from the value of its
    /// key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this absence is deliberate and is the direct consequence of the seed table above. A
    /// conventional <c>IsTransient()</c>, <c>IsNew</c> or <c>Identity == default</c> predicate would
    /// report the first module and the first page of every installation as unsaved, because both are
    /// keyed zero, and would report the first portal as unsaved because it is keyed -1. Every candidate
    /// marker for "no row yet" is a real key belonging to a real row, so the question is answered by the
    /// persistence layer's declaration and by nothing else.
    /// </para>
    /// <para>
    /// Asserted by reflection across every type in the aggregate so that adding such a member to any one
    /// of them fails the build.
    /// </para>
    /// </remarks>
    [Fact]
    public void Entities_DeclareNoWayToDeduceWhetherARowExistsFromItsKey()
    {
        foreach (Type entityType in ModuleAggregateTypes)
        {
            foreach (string forbidden in new[] { "IsTransient", "IsNew", "IsPersisted", "HasIdentity" })
            {
                entityType.GetProperty(forbidden).Should().BeNull(
                    $"{entityType.Name}.{forbidden} would read a real key as an absent one");
                entityType.GetMethod(forbidden).Should().BeNull(
                    $"{entityType.Name}.{forbidden}() would read a real key as an absent one");
            }
        }

        // Zero and -1 both satisfy `== default` reasoning on at least one table in this aggregate, which
        // is why the reasoning itself is refused rather than merely discouraged.
        NewPersistedModule(ModuleIdentitySeed).IdentityIsPersisted.Should().BeTrue(
            "a module keyed zero is a written row, and only the declaration can say so");
    }

    /// <summary>
    /// None of the module tables carries audit timestamps, so none of these entities is audited.
    /// </summary>
    /// <remarks>
    /// <c>AuditableEntity&lt;TId&gt;</c> supplies a created and a last-updated timestamp for the tables
    /// that genuinely carry them. None of the module tables does, so deriving from it would invent two
    /// columns per table — which is why the base type is asserted rather than assumed. The two settings
    /// entities are excluded from the base-type check because they derive from nothing at all, for the
    /// separate reason recorded on their own tests.
    /// </remarks>
    [Fact]
    public void ModuleAggregate_IsNotAudited()
    {
        foreach (Type entityType in new[]
        {
            typeof(Module),
            typeof(TabModule),
            typeof(ModuleDefinition),
            typeof(ModuleControl),
            typeof(DesktopModule),
            typeof(PortalDesktopModule),
            typeof(Tab),
        })
        {
            entityType.BaseType.Should().Be(
                typeof(Entity<int>),
                $"{entityType.Name} derives from the plain entity base");
            typeof(AuditableEntity<int>).IsAssignableFrom(entityType).Should().BeFalse();
            entityType.GetProperty("CreatedDate").Should().BeNull();
            entityType.GetProperty("LastUpdatedDate").Should().BeNull();
        }
    }

    /// <summary>
    /// Every value the legacy sentinel system encoded is accepted as ordinary data rather than rejected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entities in this aggregate enforce no invariants of their own: they are plain data carriers,
    /// and validation belongs to the Application layer's request validators. This test states that
    /// positively, because a guard added here would reject data the existing database already holds.
    /// </para>
    /// <para>
    /// <see cref="DomainException"/> is the domain's own failure channel and is named explicitly so the
    /// assertion is about that channel rather than about exceptions in general.
    /// </para>
    /// </remarks>
    [Fact]
    public void SentinelBoundary_EveryLegacyEncodedValueIsAcceptedAsData()
    {
        Action assignEveryLegacySentinel = () =>
        {
            Module module = new()
            {
                ModuleId = LegacyNullInteger,
                ModuleDefinitionId = LegacyNullInteger,
                PortalId = LegacyNullInteger,
                ModuleTitle = string.Empty,
                Header = string.Empty,
                Footer = string.Empty,
                StartDate = DateTime.MinValue,
                EndDate = DateTime.MinValue,
            };

            TabModule placement = new()
            {
                TabModuleId = LegacyNullInteger,
                TabId = LegacyNullInteger,
                ModuleId = LegacyNullInteger,
                PaneName = string.Empty,
                Alignment = string.Empty,
                Color = string.Empty,
                Border = string.Empty,
                IconFile = string.Empty,
                ContainerSrc = string.Empty,
            };

            Tab page = new()
            {
                TabId = LegacyNullInteger,
                TabName = string.Empty,
                PortalId = LegacyNullInteger,
                ParentId = LegacyNullInteger,
                RefreshInterval = LegacyNullInteger,
                StartDate = DateTime.MinValue,
                EndDate = DateTime.MinValue,
            };

            // Read the assigned values back so the assignments cannot be optimised away and so the
            // round trip - not merely the construction - is what this test exercises.
            module.ModuleId.Should().Be(LegacyNullInteger);
            module.ModuleTitle.Should().BeEmpty();
            module.StartDate.Should().Be(DateTime.MinValue);
            placement.PaneName.Should().BeEmpty();
            placement.Alignment.Should().BeEmpty();
            page.ParentId.Should().Be(LegacyNullInteger);
            page.RefreshInterval.Should().Be(LegacyNullInteger);
        };

        assignEveryLegacySentinel.Should().NotThrow<DomainException>(
            "these entities enforce no invariants: rejecting a value the existing columns already hold "
            + "would make the migration unable to read its own database");
        assignEveryLegacySentinel.Should().NotThrow();
    }

    // =============================================================================================
    // Module — the eleven terminal columns of dbo.Modules
    // =============================================================================================

    /// <summary>
    /// The module instance carries the eleven terminal columns of its own table and nothing else.
    /// </summary>
    /// <remarks>
    /// Eleven of the legacy class's 58 properties correspond to a column <c>dbo.Modules</c> still has;
    /// the other 47 describe a different table, a dropped column or per-request presentation state. This
    /// assertion is the positive half of that split and the theory below is the negative half.
    /// </remarks>
    [Fact]
    public void Module_CarriesTheElevenTerminalColumnsAndNoOthers()
    {
        DataPropertyNames(typeof(Module)).Should().BeEquivalentTo(
            "ModuleId",
            "ModuleDefinitionId",
            "ModuleTitle",
            "AllTabs",
            "IsDeleted",
            "InheritViewPermissions",
            "Header",
            "Footer",
            "StartDate",
            "EndDate",
            "PortalId");
    }

    /// <summary>
    /// The members the legacy class flattened in from other tables, or kept after their column was
    /// dropped, are absent from the module entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: fourteen of these names are <c>dbo.TabModules</c> facts that <c>03.00.01</c> moved off
    /// <c>dbo.Modules</c>, and they now live on <see cref="TabModule"/>. <c>Visibility</c> in particular
    /// was never a <c>Modules</c> column at all. Twenty more describe the definition, the package or the
    /// control and live on <see cref="ModuleDefinition"/>, <see cref="DesktopModule"/> and
    /// <see cref="ModuleControl"/>; <c>DesktopModuleId</c> is one of them, which is what makes the
    /// legacy constructor's inconsistency unobservable here — see the dedicated test below.
    /// </para>
    /// <para>
    /// MIGRATION: <c>AuthorizedEditRoles</c>, <c>AuthorizedViewRoles</c> and <c>AuthorizedRoles</c> are
    /// the interesting cases, because they survive on <b>no</b> target entity. <c>03.00.01</c> dropped
    /// both <c>Modules</c> columns when grants became rows in <c>dbo.ModulePermission</c>, yet the legacy
    /// class kept exposing all three — and the legacy author's own comment above <c>AuthorizedRoles</c>
    /// (<c>ModuleInfo.vb</c> line 626) reads "should be deprecated due to roles being abstracted". A
    /// semicolon-delimited role-identifier string is not carried forward in any form; grants are reached
    /// through <see cref="Module.ModulePermissions"/>.
    /// </para>
    /// <para>
    /// MIGRATION: <c>ContainerPath</c>, <c>PaneModuleIndex</c>, <c>PaneModuleCount</c>,
    /// <c>IsDefaultModule</c> and <c>AllModules</c> — the five <c>&lt;XmlIgnore()&gt;</c> properties at
    /// <c>ModuleInfo.vb</c> lines 563 to 599 — were Web Forms rendering state, populated while a skin
    /// laid out its panes. There is no server-side rendering in the target, so they have no counterpart
    /// in any layer.
    /// </para>
    /// <para>
    /// Asserted by reflection so that reinstating any of them fails the build.
    /// </para>
    /// </remarks>
    /// <param name="memberName">The legacy member that must not exist on the module entity.</param>
    [Theory]
    [InlineData("TabId")]
    [InlineData("TabModuleId")]
    [InlineData("ModuleOrder")]
    [InlineData("PaneName")]
    [InlineData("CacheTime")]
    [InlineData("Alignment")]
    [InlineData("Color")]
    [InlineData("Border")]
    [InlineData("IconFile")]
    [InlineData("Visibility")]
    [InlineData("ContainerSrc")]
    [InlineData("DisplayTitle")]
    [InlineData("DisplayPrint")]
    [InlineData("DisplaySyndicate")]
    [InlineData("DesktopModuleId")]
    [InlineData("FolderName")]
    [InlineData("FriendlyName")]
    [InlineData("Description")]
    [InlineData("Version")]
    [InlineData("IsPremium")]
    [InlineData("IsAdmin")]
    [InlineData("BusinessControllerClass")]
    [InlineData("ModuleName")]
    [InlineData("SupportedFeatures")]
    [InlineData("CompatibleVersions")]
    [InlineData("Dependencies")]
    [InlineData("Permissions")]
    [InlineData("DefaultCacheTime")]
    [InlineData("ModuleControlId")]
    [InlineData("ControlSrc")]
    [InlineData("ControlType")]
    [InlineData("ControlTitle")]
    [InlineData("HelpUrl")]
    [InlineData("SupportsPartialRendering")]
    [InlineData("AuthorizedEditRoles")]
    [InlineData("AuthorizedViewRoles")]
    [InlineData("AuthorizedRoles")]
    [InlineData("ContainerPath")]
    [InlineData("PaneModuleIndex")]
    [InlineData("PaneModuleCount")]
    [InlineData("IsDefaultModule")]
    [InlineData("AllModules")]
    [InlineData("IsPortable")]
    [InlineData("IsSearchable")]
    [InlineData("IsUpgradeable")]
    [InlineData("Cacheability")]
    public void Module_OmitsTheMembersTheLegacyClassFlattenedIntoOneRow(string memberName)
    {
        typeof(Module).GetProperty(memberName).Should().BeNull(
            $"{memberName} is not a column of the terminal dbo.Modules base table");
    }

    /// <summary>
    /// No entity in this aggregate carries a serialisation, mapping or validation attribute.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy classes were decorated <c>&lt;XmlRoot&gt;</c> at the type and
    /// <c>&lt;XmlElement&gt;</c>, <c>&lt;XmlArray&gt;</c> or <c>&lt;XmlIgnore&gt;</c> on every member,
    /// because portal templates were serialised straight off the entity — <c>ModuleInfo.vb</c> line 36
    /// and <c>TabInfo.vb</c> line 40. The wire contract now belongs to the Application DTOs, so the
    /// domain expresses no serialisation opinion at all, not even a JSON one, and the persistence
    /// mapping is declared by the Infrastructure entity configurations instead of by an attribute.
    /// </remarks>
    [Fact]
    public void ModuleAggregate_CarriesNoSerialisationOrValidationAttributes()
    {
        // Compiler-emitted attributes are excluded rather than asserted away. Enabling nullable reference
        // types makes the compiler stamp NullableAttribute and NullableContextAttribute onto the type and
        // onto every member with a reference-typed signature, so an unfiltered check would fail for a
        // reason that has nothing to do with the legacy attributes this test is about.
        foreach (Type entityType in ModuleAggregateTypes)
        {
            AuthoredAttributeNames(entityType.GetCustomAttributesData()).Should().BeEmpty(
                $"the legacy XmlRoot attribute on {entityType.Name} is dropped, not replaced");

            foreach (PropertyInfo property in entityType.GetProperties())
            {
                AuthoredAttributeNames(property.GetCustomAttributesData()).Should().BeEmpty(
                    $"{entityType.Name}.{property.Name} must carry no serialisation, mapping or "
                    + "validation attribute");
            }
        }
    }

    /// <summary>
    /// The legacy empty-string sentinel is not reinstated as a property initialiser anywhere, and a
    /// stored empty string is preserved exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the literal discharge of Rule T7 for the ten strings the legacy constructor
    /// seeded. <c>Null.NullString</c> is the <b>empty string</b>, not <see langword="null"/>, and
    /// <c>ModuleInfo.vb</c> lines 110 to 121 assigned it to <c>ModuleTitle</c>,
    /// <c>AuthorizedEditRoles</c>, <c>AuthorizedViewRoles</c>, <c>Alignment</c>, <c>Color</c>,
    /// <c>Border</c>, <c>IconFile</c>, <c>Header</c>, <c>Footer</c> and <c>ContainerSrc</c>. Once read
    /// back, a SQL <c>NULL</c> and a zero-length string were therefore indistinguishable.
    /// </para>
    /// <para>
    /// The ten split three ways in the target. <c>ModuleTitle</c>, <c>Header</c> and <c>Footer</c> stay
    /// on <see cref="Module"/>. <c>Alignment</c>, <c>Color</c>, <c>Border</c>, <c>IconFile</c> and
    /// <c>ContainerSrc</c> move to <see cref="TabModule"/>, because <c>03.00.01</c> moved their columns
    /// to <c>dbo.TabModules</c>. <c>AuthorizedEditRoles</c> and <c>AuthorizedViewRoles</c> survive
    /// nowhere, because the same script dropped their columns outright.
    /// </para>
    /// <para>
    /// Two rules follow, and both are asserted: absence is <see langword="null"/>, so no property is
    /// initialised to <c>string.Empty</c>; and a value of <c>string.Empty</c> that a caller or a row
    /// genuinely supplies stays exactly that and is never collapsed to <see langword="null"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Module_DoesNotReinstateTheLegacyEmptyStringSentinel()
    {
        Module fresh = NewModule(ModuleIdentitySeed);
        TabModule freshPlacement = NewPlacement(1);

        // Unset is null on every one of the eight strings that survived the split.
        fresh.ModuleTitle.Should().BeNull("seeding string.Empty would plant the sentinel in the domain");
        fresh.Header.Should().BeNull();
        fresh.Footer.Should().BeNull();
        freshPlacement.Alignment.Should().BeNull();
        freshPlacement.Color.Should().BeNull();
        freshPlacement.Border.Should().BeNull();
        freshPlacement.IconFile.Should().BeNull();
        freshPlacement.ContainerSrc.Should().BeNull();

        // A genuinely stored empty string is preserved, and is a different state from unset.
        Module stored = NewModule(ModuleIdentitySeed);
        stored.ModuleTitle = string.Empty;
        stored.Header = string.Empty;
        stored.Footer = string.Empty;

        stored.ModuleTitle.Should().NotBeNull().And.BeEmpty();
        stored.Header.Should().NotBeNull().And.BeEmpty();
        stored.Footer.Should().NotBeNull().And.BeEmpty();
        stored.ModuleTitle.Should().NotBeSameAs(null);

        // MIGRATION: the two legacy role strings the same constructor seeded have no target member at
        // all, so the sentinel question does not arise for them - it is answered by their absence.
        typeof(Module).GetProperty("AuthorizedEditRoles").Should().BeNull();
        typeof(Module).GetProperty("AuthorizedViewRoles").Should().BeNull();
        typeof(TabModule).GetProperty("AuthorizedEditRoles").Should().BeNull();
        typeof(TabModule).GetProperty("AuthorizedViewRoles").Should().BeNull();
    }

    /// <summary>
    /// The module's date boundaries express absence as a null rather than as the legacy minimum date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>ModuleInfo.vb</c> lines 119 and 120 seeded <c>_StartDate</c> and <c>_EndDate</c>
    /// with <c>Null.NullDate</c>, which is <c>Date.MinValue</c>, so the earliest representable date was
    /// indistinguishable from "no boundary". Both properties are <c>DateTime?</c> here: null is the
    /// absence and <see cref="DateTime.MinValue"/> is an ordinary date that round-trips as data.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy absence test was <b>date-part only</b> — <c>Null.IsNull</c> compares
    /// <c>objDate.Date</c> against <c>NullDate.Date</c> — so any time of day on the minimum date also
    /// read as absent. That comparison is not reproduced: a value one hour after the minimum date is an
    /// ordinary instant here, which the assertions below state explicitly because it is the one case
    /// where preserving the sentinel would have required preserving a truncation too.
    /// </para>
    /// </remarks>
    [Fact]
    public void Module_ExpressesDateAbsenceAsNullRatherThanAsTheLegacyMinimumDate()
    {
        Module unbounded = NewModule(ModuleIdentitySeed);

        unbounded.StartDate.Should().BeNull("no start boundary is a null, not a minimum date");
        unbounded.EndDate.Should().BeNull();

        Module minimumDate = NewModule(ModuleIdentitySeed);
        minimumDate.StartDate = DateTime.MinValue;
        minimumDate.EndDate = DateTime.MinValue.AddHours(5);

        minimumDate.StartDate.Should().Be(DateTime.MinValue).And.NotBeNull();
        minimumDate.EndDate.Should().Be(
            DateTime.MinValue.AddHours(5),
            "the legacy date-part-only comparison would have read this as absent; here it is an instant");
        minimumDate.EndDate.Should().NotBe(DateTime.MinValue);
    }

    /// <summary>
    /// View-permission inheritance is a tri-state, because the column admits no value at all.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>02.02.00</c> adds <c>InheritViewPermissions bit NULL</c> and no later script makes
    /// it <c>NOT NULL</c>, so nullable is the terminal shape. <c>ModuleInfo.vb</c> declared it a plain VB
    /// <c>Boolean</c> over a <c>Boolean</c> field, which cannot represent the SQL null at all: every
    /// unwritten row arrived as <c>False</c>, silently merging "never set" with "set to false". Widening
    /// it to <c>bool?</c> is a deliberate, documented type correction, and the legacy consumer at
    /// <c>ModuleController.vb</c> line 1108 tested it for truth, so only an affirmative value may defer
    /// to the page's grants.
    /// </remarks>
    [Fact]
    public void InheritViewPermissions_IsATriState()
    {
        Module unset = NewModule(1);
        Module inheriting = new() { ModuleId = 2, ModuleDefinitionId = 1, InheritViewPermissions = true };
        Module standalone = new() { ModuleId = 3, ModuleDefinitionId = 1, InheritViewPermissions = false };

        unset.InheritViewPermissions.Should().BeNull(
            "an unset column must not collapse to false: the permission resolver tests for true "
            + "explicitly so that only an affirmative value reaches the page grants");
        inheriting.InheritViewPermissions.Should().BeTrue();
        standalone.InheritViewPermissions.Should().BeFalse();

        (unset.InheritViewPermissions == true).Should().BeFalse();
        (standalone.InheritViewPermissions == true).Should().BeFalse();
        (inheriting.InheritViewPermissions == true).Should().BeTrue();
    }

    /// <summary>
    /// A module may be owned by the installation rather than by a tenant, and both real portal keys are
    /// ordinary owners.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>03.00.01</c> adds <c>PortalID int NULL</c>, so null is the way a host-level module
    /// is expressed. It must not be modelled as a non-nullable integer carrying <c>Null.NullInteger</c>
    /// for "no portal": -1 is a genuine portal key, because <c>dbo.Portals.PortalID</c> is
    /// <c>IDENTITY(-1, 1)</c>, so the sentinel and a real owner would collide.
    /// </remarks>
    [Fact]
    public void Module_PortalOwnershipIsOptionalAndBothRealPortalKeysAreOwners()
    {
        Module hostOwned = NewModule(1);
        Module firstTenant = new() { ModuleId = 2, ModuleDefinitionId = 1, PortalId = LegacyNullInteger };
        Module secondTenant = new() { ModuleId = 3, ModuleDefinitionId = 1, PortalId = 0 };

        hostOwned.PortalId.Should().BeNull(
            "dbo.Modules.PortalID is nullable, which is how a host-level module instance is expressed");
        firstTenant.PortalId.Should().Be(
            LegacyNullInteger,
            "-1 is the seed of Portals.PortalID and therefore a real owner, not an absence");
        secondTenant.PortalId.Should().Be(0, "zero is the second tenant of an installation, not an absence");

        firstTenant.PortalId.Should().NotBeNull();
        secondTenant.PortalId.Should().NotBeNull();
    }

    /// <summary>
    /// A module is undeleted, single-page and untitled until something says otherwise.
    /// </summary>
    [Fact]
    public void Module_IsUndeletedAndSinglePageByDefault()
    {
        Module module = NewModule(1);

        module.IsDeleted.Should().BeFalse();
        module.AllTabs.Should().BeFalse();
        module.ModuleTitle.Should().BeNull();
        module.StartDate.Should().BeNull();
        module.EndDate.Should().BeNull();
    }

    /// <summary>
    /// The legacy constructor's own inconsistency between its five identifier fields is preserved as
    /// knowledge rather than as behaviour, because the field it concerned is not a column of this table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>ModuleInfo.vb</c> lines 105 to 109 seeded <c>_PortalID</c>, <c>_TabID</c>,
    /// <c>_TabModuleID</c>, <c>_ModuleID</c> and <c>_ModuleDefID</c> with <c>Null.NullInteger</c>, under
    /// the author's own comment "initialize the properties that can be null in the database". A sixth
    /// identifier field, <c>_DesktopModuleID</c>, was <b>not</b> seeded and therefore started at
    /// <c>0</c> rather than at -1 — a genuine legacy inconsistency, since the same class treated the
    /// other five as absent at -1 while treating this one as absent at a value that is a legitimate key
    /// elsewhere in the schema.
    /// </para>
    /// <para>
    /// Minimal Change Clause item 1 requires a discovered defect to be annotated in place and not fixed.
    /// It is annotated here and there is nothing to fix, because <c>DesktopModuleID</c> is not a
    /// <c>dbo.Modules</c> column: it belongs to <c>dbo.ModuleDefinitions</c> and appears on
    /// <see cref="ModuleDefinition"/>. The inconsistency is therefore unobservable in the target — the
    /// four-way split removed the surface it lived on — and that outcome is asserted rather than
    /// described.
    /// </para>
    /// </remarks>
    [Fact]
    public void Module_PreservesTheLegacyDesktopModuleIdInconsistencyAsKnowledgeOnly()
    {
        typeof(Module).GetProperty("DesktopModuleId").Should().BeNull(
            "the desktop-module key is a dbo.ModuleDefinitions column, so the legacy field that started "
            + "at 0 rather than at -1 has no counterpart on this entity");

        ModuleDefinition definition = NewDefinition(ModuleDefinitionIdentitySeed);
        definition.DesktopModuleId.Should().Be(
            0,
            "the definition entity does carry the key, and a fresh instance leaves it at the CLR default "
            + "exactly as the legacy field did - the difference is that no sentinel is claimed for it");

        // The five identifiers the legacy constructor did seed are all reachable, and none of them
        // carries a sentinel initialiser in the target either.
        NewModule(0).ModuleId.Should().Be(0);
        NewModule(0).ModuleDefinitionId.Should().Be(1);
        NewPlacement(0).TabModuleId.Should().Be(0);
        NewPlacement(0).TabId.Should().Be(TabIdentitySeed);
        NewModule(0).PortalId.Should().BeNull();
    }

    /// <summary>
    /// A newly constructed module, placement, definition, package and page expose empty collections rather
    /// than null ones.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy model exposed an untyped <c>ModulePermissionCollection</c> and a
    /// <c>Hashtable</c> of settings, both of which started as <c>Nothing</c> and had to be
    /// null-checked at every use. The pre-generics <c>CollectionBase</c> and <c>DictionaryBase</c>
    /// wrappers produce no target file at all: they are subsumed by
    /// <see cref="ICollection{T}"/>. Emptiness is a legitimate answer and load state is the repository's
    /// decision, so a count here never means "not loaded".
    /// </remarks>
    [Fact]
    public void NavigationCollections_AreInitialisedRatherThanNull()
    {
        Module module = NewModule(1);
        TabModule placement = NewPlacement(1);
        ModuleDefinition definition = NewDefinition(ModuleDefinitionIdentitySeed);
        DesktopModule package = NewPackage(0);
        Tab page = NewTab(1);

        module.TabModules.Should().NotBeNull().And.BeEmpty();
        module.Settings.Should().NotBeNull().And.BeEmpty();
        module.ModulePermissions.Should().NotBeNull().And.BeEmpty();
        placement.Settings.Should().NotBeNull().And.BeEmpty();
        definition.ModuleControls.Should().NotBeNull().And.BeEmpty();
        definition.Modules.Should().NotBeNull().And.BeEmpty();
        package.ModuleDefinitions.Should().NotBeNull().And.BeEmpty();
        package.PortalDesktopModules.Should().NotBeNull().And.BeEmpty();
        page.Children.Should().NotBeNull().And.BeEmpty();
        page.TabModules.Should().NotBeNull().And.BeEmpty();
        page.TabPermissions.Should().NotBeNull().And.BeEmpty();

        // A collection reports what it holds, so emptiness is an answer rather than an unknown.
        module.Settings.Add(NewSetting(module.ModuleId, "Editor"));
        module.Settings.Should().ContainSingle().Which.SettingName.Should().Be("Editor");
    }

    // =============================================================================================
    // TabModule — the fifteen per-placement facts the module table gave up
    // =============================================================================================

    /// <summary>
    /// A placement carries the fifteen per-placement facts and joins exactly one page to exactly one
    /// module.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>03.00.01</c> created <c>dbo.TabModules</c> and dropped ten presentation columns plus
    /// <c>TabID</c> from <c>dbo.Modules</c>, because a module can appear on several pages and each
    /// appearance has its own order, pane, chrome and visibility. The legacy class flattened the join, so
    /// its single set of presentation properties silently described whichever placement had been loaded.
    /// Splitting the entity is what makes several placements of one module representable at all.
    /// </remarks>
    [Fact]
    public void Placement_CarriesThePerPlacementFactsAndJoinsOnePageToOneModule()
    {
        DataPropertyNames(typeof(TabModule)).Should().BeEquivalentTo(
            "TabModuleId",
            "TabId",
            "ModuleId",
            "PaneName",
            "ModuleOrder",
            "CacheTime",
            "Alignment",
            "Color",
            "Border",
            "IconFile",
            "Visibility",
            "ContainerSrc",
            "DisplayTitle",
            "DisplayPrint",
            "DisplaySyndicate");

        TabModule placement = NewPlacement(1);
        placement.TabId.Should().Be(TabIdentitySeed, "zero is a real page key");
        placement.ModuleId.Should().Be(ModuleIdentitySeed, "zero is a real module key");

        // One module, two pages: the state the flattened legacy class could not represent.
        Module module = NewModule(ModuleIdentitySeed);
        module.TabModules.Add(placement);
        module.TabModules.Add(new TabModule { TabModuleId = 2, TabId = 1, ModuleId = ModuleIdentitySeed });

        module.TabModules.Should().HaveCount(2);
        module.TabModules.Select(each => each.TabId).Should().BeEquivalentTo(new[] { 0, 1 });
        module.TabModules.Select(each => each.ModuleId).Should().AllBeEquivalentTo(ModuleIdentitySeed);
    }

    /// <summary>
    /// A placement carries the display defaults the legacy constructor set, including the two that are
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the trap a mechanical port walks straight into. <c>ModuleInfo.vb</c> lines 122
    /// and 123 set <c>_DisplayTitle = True</c> and <c>_DisplayPrint = True</c> — <b>non-sentinel</b>
    /// defaults, since <c>Null.NullBoolean</c> is <c>False</c> — while line 124 sets
    /// <c>_DisplaySyndicate = False</c>. A bare C# auto-property <see cref="bool"/> defaults to
    /// <see langword="false"/>, so transcribing the three as plain properties would silently invert two
    /// display defaults: every newly created placement would hide its title and drop its print
    /// affordance. Both are therefore initialised explicitly, and all three are asserted here so the
    /// contrast between them is impossible to lose.
    /// </para>
    /// <para>
    /// This is Minimal Change Clause item 3 in its most literal form: identical inputs must produce
    /// identical outcomes, and a default is an input. The <c>DisplaySyndicate</c> case additionally
    /// diverges from the store default of <c>1</c> that <c>03.00.08</c> declared and <c>03.01.01</c>
    /// re-established; the legacy object default is kept deliberately, and the disagreement between the
    /// two is recorded in <c>MIGRATION_NOTES.md</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Placement_CarriesTheLegacyDisplayDefaultsIncludingTheTwoThatAreTrue()
    {
        TabModule placement = NewPlacement(1);

        placement.DisplayTitle.Should().BeTrue(
            "ModuleInfo.vb line 122 sets _DisplayTitle = True, so a bare auto-property would invert it");
        placement.DisplayPrint.Should().BeTrue(
            "ModuleInfo.vb line 123 sets _DisplayPrint = True, so a bare auto-property would invert it");
        placement.DisplaySyndicate.Should().BeFalse(
            "ModuleInfo.vb line 124 sets _DisplaySyndicate to False in both its constructor and its "
            + "Initialize routine, and that object default is deliberately kept even though the "
            + "TabModules column defaults to 1 - the divergence is recorded in MIGRATION_NOTES.md");

        // The remaining unseeded members keep the CLR defaults the legacy fields had, and no sentinel is
        // substituted for any of them.
        placement.PaneName.Should().BeNull(
            "the legacy constructor leaves the pane unset, and seeding string.Empty would plant the "
            + "Null.NullString sentinel in the domain");
        placement.ModuleOrder.Should().Be(0);
        placement.CacheTime.Should().Be(0);
        placement.Visibility.Should().Be(ModuleVisibility.Maximized, "zero is the maximised state");
    }

    /// <summary>
    /// The placement's chrome strings are null when unset and preserve a stored empty string.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>Alignment</c>, <c>Color</c>, <c>Border</c>, <c>IconFile</c> and <c>ContainerSrc</c>
    /// are five of the ten strings <c>ModuleInfo.vb</c> seeded with <c>Null.NullString</c>, and their
    /// columns moved to <c>dbo.TabModules</c>. They are opaque persisted text in the target: the legacy
    /// values were HTML attribute fragments, and neither validating nor normalising them here would be
    /// faithful to a column that accepts whatever was written to it.
    /// </remarks>
    [Fact]
    public void Placement_ChromeStringsAreNullWhenUnsetAndPreserveAStoredEmptyString()
    {
        TabModule unset = NewPlacement(1);

        unset.Alignment.Should().BeNull();
        unset.Color.Should().BeNull();
        unset.Border.Should().BeNull();
        unset.IconFile.Should().BeNull();
        unset.ContainerSrc.Should().BeNull();

        TabModule stored = NewPlacement(2);
        stored.Alignment = string.Empty;
        stored.Color = "#003366";
        stored.Border = "0";
        stored.IconFile = "fileid=42";
        stored.ContainerSrc = "[G]Containers/_default/Title.ascx";

        stored.Alignment.Should().NotBeNull().And.BeEmpty("a stored blank stays blank");
        stored.Color.Should().Be("#003366", "the value is opaque text rather than a parsed colour");
        stored.Border.Should().Be("0", "the border width is text in the schema, not a number");
        stored.IconFile.Should().Be(
            "fileid=42",
            "the raw base-table token is preserved; resolving it would require reading dbo.Files");
        stored.ContainerSrc.Should().Be("[G]Containers/_default/Title.ascx");
    }

    // =============================================================================================
    // ModuleDefinition and ModuleControl — the kind of module, and its user interface
    // =============================================================================================

    /// <summary>
    /// The definition carries the four facts that describe a kind of module, and reaches its package.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>dbo.ModuleDefinitions.ModuleDefID</c> is <c>IDENTITY(1, 1)</c> at
    /// <c>01.00.00.SqlDataProvider</c> line 66, so unlike a module or a page key, <b>zero is not a real
    /// definition key</b>. The entity still stores zero without complaint, because the column has no
    /// such constraint and inventing one here would reject data; the difference is asserted so that the
    /// zero rule is never generalised from the two tables it applies to.
    /// </remarks>
    [Fact]
    public void ModuleDefinition_CarriesTheDefinitionFactsAndSeedsAtOne()
    {
        DataPropertyNames(typeof(ModuleDefinition)).Should().BeEquivalentTo(
            "ModuleDefinitionId",
            "FriendlyName",
            "DesktopModuleId",
            "DefaultCacheTime");

        ModuleDefinition definition = NewDefinition(ModuleDefinitionIdentitySeed);
        DesktopModule package = NewPackage(PortableFeatureBit, DesktopModuleIdentitySeed);

        definition.DesktopModuleId = package.DesktopModuleId;
        definition.DesktopModule = package;
        package.ModuleDefinitions.Add(definition);

        definition.Identity.Should().Be(ModuleDefinitionIdentitySeed);
        definition.DesktopModule.Should().BeSameAs(package);
        definition.DesktopModule!.IsPortable.Should().BeTrue(
            "the capability is a package fact reached through the definition, never duplicated onto it");
        package.ModuleDefinitions.Should().ContainSingle().Which.Should().BeSameAs(definition);

        // The definition's own capability duplication is refused: the bit field lives on the package.
        typeof(ModuleDefinition).GetProperty("SupportedFeatures").Should().BeNull();
        typeof(ModuleDefinition).GetProperty("IsPortable").Should().BeNull();
    }

    /// <summary>
    /// The definition's package reference is optional, and a definition with no package loaded is not a
    /// definition with no package.
    /// </summary>
    [Fact]
    public void ModuleDefinition_PackageReferenceIsOptionalOnRead()
    {
        ModuleDefinition definition = NewDefinition(ModuleDefinitionIdentitySeed);

        definition.DesktopModule.Should().BeNull(
            "a navigation is null until a query loads it, which is why reading it defensively is correct");
        definition.DesktopModuleId.Should().Be(
            0,
            "the foreign key is a scalar and is present whether or not the principal has been loaded");
    }

    /// <summary>
    /// The control's access level stays a stored integer, because the legacy enumeration it came from has
    /// no target type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>ModuleInfo.ControlType</c> was typed <c>SecurityAccessLevel</c>, declared at
    /// <c>Library/Components/Security/PortalSecurity.vb</c> as
    /// <c>Public Enum SecurityAccessLevel As Integer</c> with <b>explicit negative members</b>:
    /// <c>ControlPanel = -3</c>, <c>SkinObject = -2</c>, <c>Anonymous = -1</c>, <c>View = 0</c>,
    /// <c>Edit = 1</c>, <c>Admin = 2</c>, <c>Host = 3</c>. That enumeration is not in the target's enum
    /// set and <c>Domain/Enums/SecurityAccessLevel.cs</c> does not exist, so the values are recorded
    /// here and the column is carried as the plain <see cref="int"/> the schema declares. Nothing is
    /// fabricated: naming an enumeration the plan never specified would be an invention, and asserting
    /// against a type that is absent would not compile.
    /// </para>
    /// <para>
    /// MIGRATION: <c>Anonymous = -1</c> is a further, distinct meaning of -1 — the fifth in this domain,
    /// alongside the generic integer sentinel, a real portal key, the "all users" role identifier and the
    /// capability-mask guard. It is also why the legacy "numerically lowest member is the absent value"
    /// convention would have made <c>ControlPanel(-3)</c> the absent access level rather than
    /// <c>View(0)</c>, which is the sort of accident an untyped integer cannot cause.
    /// </para>
    /// </remarks>
    [Fact]
    public void ModuleControl_AccessLevelStaysAStoredInteger()
    {
        PropertyOf<ModuleControl>(nameof(ModuleControl.ControlType)).PropertyType.Should().Be(
            typeof(int),
            "the legacy SecurityAccessLevel enumeration has no target type, so the column is carried as "
            + "the integer it is declared as");

        ModuleControl control = NewControl(1);

        // Every measured legacy member value round-trips, negatives included.
        foreach (int legacyAccessLevel in new[] { -3, -2, -1, 0, 1, 2, 3 })
        {
            control.ControlType = legacyAccessLevel;
            control.ControlType.Should().Be(legacyAccessLevel);
        }

        control.ControlType = 0;
        control.ControlType.Should().Be(0, "View is zero, so the CLR default happens to be the view level");
    }

    /// <summary>
    /// The control carries its own ten columns and reaches its definition optionally.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>ModuleDefinitionId</c> is nullable on the control, which the flattened legacy class
    /// could not express: it held one control's worth of fields alongside the definition's, so a control
    /// that belonged to no definition had no representation at all.
    /// </remarks>
    [Fact]
    public void ModuleControl_CarriesItsOwnColumnsAndReachesItsDefinitionOptionally()
    {
        DataPropertyNames(typeof(ModuleControl)).Should().BeEquivalentTo(
            "ModuleControlId",
            "ModuleDefinitionId",
            "ControlKey",
            "ControlTitle",
            "ControlSrc",
            "IconFile",
            "ControlType",
            "ViewOrder",
            "HelpUrl",
            "SupportsPartialRendering");

        ModuleControl orphan = NewControl(1);
        orphan.ModuleDefinitionId.Should().BeNull("a control need not belong to a definition");
        orphan.ModuleDefinition.Should().BeNull();
        orphan.SupportsPartialRendering.Should().BeFalse();
        orphan.ViewOrder.Should().BeNull("no view order is a null rather than a reserved number");

        ModuleDefinition definition = NewDefinition(ModuleDefinitionIdentitySeed);
        ModuleControl attached = NewControl(2);
        attached.ModuleDefinitionId = definition.ModuleDefinitionId;
        attached.ModuleDefinition = definition;
        definition.ModuleControls.Add(attached);

        attached.ModuleDefinitionId.Should().Be(ModuleDefinitionIdentitySeed);
        definition.ModuleControls.Should().ContainSingle().Which.Should().BeSameAs(attached);
    }

    // =============================================================================================
    // ModuleSetting and TabModuleSetting — two genuine key/value entities with composite keys
    // =============================================================================================

    /// <summary>
    /// A module setting carries its composite key as two ordinary columns and declares no surrogate
    /// identity of its own.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy <c>Hashtable</c> settings idiom becomes two real key/value entities, and only
    /// two. They survive because they are backed by real tables that the upgrade chain alters thirteen and
    /// eight times respectively, whereas <c>PortalSetting</c> deliberately does <b>not</b> exist as an
    /// entity: no such table appears anywhere in the upgrade chain, and the legacy type of that name was a
    /// per-request ambient composite retrieved from the Web Forms request items, not a persisted
    /// aggregate. Portal configuration is columns on the portal row.
    /// </remarks>
    [Fact]
    public void ModuleSetting_CarriesTheCompositeKeyAsPlainColumns()
    {
        ModuleSetting setting = new()
        {
            ModuleId = ModuleIdentitySeed,
            SettingName = "CacheDuration",
            SettingValue = "600",
        };

        setting.ModuleId.Should().Be(ModuleIdentitySeed, "zero is a real ModuleID under IDENTITY(0, 1)");
        setting.SettingName.Should().Be("CacheDuration");
        setting.SettingValue.Should().Be("600");

        // MIGRATION: dbo.ModuleSettings has no surrogate key and never had one. It was created with no
        // primary key at all (01.00.00.SqlDataProvider lines 350-354, backed only by the non-unique index
        // at line 571) and acquired one solely at 02.00.01.SqlDataProvider lines 47-52, as
        // PRIMARY KEY CLUSTERED (ModuleID, SettingName). So this entity deliberately derives from nothing
        // and exposes no Identity, no Id and no ModuleSettingId: inventing one would assert a column the
        // table does not have, and it would compete with the explicit composite key that the
        // Infrastructure mapping declares. TabModuleSetting below is shaped identically and for the same
        // reason - both settings tables are keyed by (owner, name) and neither carries a surrogate column
        // - so the two are asserted the same way rather than differently.
        typeof(ModuleSetting).BaseType.Should().Be(typeof(object));
        typeof(ModuleSetting).GetProperty("Identity").Should().BeNull();
        typeof(ModuleSetting).GetProperty("Id").Should().BeNull();
        typeof(ModuleSetting).GetProperty("ModuleSettingId").Should().BeNull();
        typeof(ModuleSetting).GetProperties().Select(property => property.Name).Should()
            .BeEquivalentTo("ModuleId", "SettingName", "SettingValue", "Module");
    }

    /// <summary>
    /// Two settings differing only in name, or only in owner, are distinct rows.
    /// </summary>
    [Fact]
    public void ModuleSetting_SeparatesTheNameFromTheOwner()
    {
        ModuleSetting first = NewSetting(0, "Alpha");
        ModuleSetting sameOwnerOtherName = NewSetting(0, "Beta");
        ModuleSetting otherOwnerSameName = NewSetting(1, "Alpha");
        ModuleSetting duplicate = NewSetting(0, "Alpha");

        // MIGRATION: the row's identity is the pair (ModuleID, SettingName) that the clustered primary key
        // at 02.00.01.SqlDataProvider lines 47-52 declares, and it is asserted here directly against the
        // two columns the key is built from rather than through an identity member the entity does not
        // have. Zero appears on both sides deliberately: Modules.ModuleID is IDENTITY(0, 1)
        // (01.00.00.SqlDataProvider line 221), so a setting owned by module zero must still be addressable
        // and zero may never be read as "no module".
        CompositeKeyOf(first).Should().NotBe(CompositeKeyOf(sameOwnerOtherName));
        CompositeKeyOf(first).Should().NotBe(CompositeKeyOf(otherOwnerSameName));
        CompositeKeyOf(first).Should().Be(CompositeKeyOf(duplicate));

        // Two instances carrying the same key are still two distinct objects. With no identity-based
        // equality on this type, reconciling them is the change tracker's work, not the entity's.
        first.Should().NotBeSameAs(duplicate);
        first.Equals(duplicate).Should().BeFalse(
            "the type inherits reference equality, which is correct for a row the tracker reconciles");
    }

    /// <summary>
    /// Setting names are stored exactly as written, because the column that holds them preserves case.
    /// </summary>
    [Fact]
    public void ModuleSetting_PreservesTheSettingNameCaseExactly()
    {
        ModuleSetting lower = NewSetting(0, "cacheduration");
        ModuleSetting mixed = NewSetting(0, "CacheDuration");

        CompositeKeyOf(lower).Should().NotBe(
            CompositeKeyOf(mixed),
            "the key columns compare the stored name ordinally; case folding belongs to the query layer, "
            + "not to the row");
        lower.SettingName.Should().Be("cacheduration");
        mixed.SettingName.Should().Be("CacheDuration");
    }

    /// <summary>
    /// A blank setting value survives as the empty string in both settings stores and is never turned into
    /// a null.
    /// </summary>
    [Fact]
    public void SettingValues_KeepTheEmptyStringRatherThanBecomingNull()
    {
        ModuleSetting moduleScope = new()
        {
            ModuleId = 0,
            SettingName = "Editor",
            SettingValue = string.Empty,
        };

        TabModuleSetting placementScope = new()
        {
            TabModuleId = 1,
            SettingName = "Editor",
            SettingValue = string.Empty,
        };

        // MIGRATION: Library/Components/Shared/Null.vb defines NullString as "" rather than as Nothing, so
        // in the legacy store a setting recorded with no value and a setting recorded with a blank value
        // were indistinguishable once read. Both value columns are NOT NULL, so the empty string remains
        // the only way to record "stored, but blank", and nothing in this model may collapse it to null.
        // That is the Rule T7 boundary discipline applied to these two rows.
        moduleScope.SettingValue.Should().NotBeNull().And.BeEmpty();
        placementScope.SettingValue.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// A placement setting is identified by its owning placement together with its name, and carries that
    /// pair as two plain columns rather than as an identity member.
    /// </summary>
    [Fact]
    public void PlacementSettingIdentity_IsTheCompositeKey()
    {
        TabModuleSetting setting = new()
        {
            TabModuleId = 1,
            SettingName = "hideAdminBorder",
            SettingValue = "True",
        };

        // MIGRATION: dbo.TabModuleSettings has no surrogate key either. It is created at
        // 03.00.01.SqlDataProvider lines 722-727 with exactly three NOT NULL columns and keyed at lines
        // 730-735 as PRIMARY KEY CLUSTERED (TabModuleID, SettingName); 03.00.09.SqlDataProvider lines
        // 331-333 drop and re-add that key under an object-qualifier-aware name without changing its
        // shape. So, exactly as for ModuleSetting above, the entity derives from nothing and exposes no
        // Identity, no Id and no TabModuleSettingId - the row's identity is the pair of key columns, and
        // the key itself is declared by the Infrastructure mapping.
        typeof(TabModuleSetting).BaseType.Should().Be(typeof(object));
        typeof(TabModuleSetting).GetProperty("Identity").Should().BeNull();
        typeof(TabModuleSetting).GetProperty("Id").Should().BeNull();
        typeof(TabModuleSetting).GetProperty("TabModuleSettingId").Should().BeNull();
        typeof(TabModuleSetting).GetProperties().Select(property => property.Name).Should()
            .BeEquivalentTo("TabModuleId", "SettingName", "SettingValue", "TabModule");

        // The key is asserted directly against the two columns it is built from. One is a placement and
        // the other a name, so a setting keeps its meaning only while both travel together.
        CompositeKeyOf(setting).Should().Be((1, "hideAdminBorder"));
        CompositeKeyOf(setting).Should().NotBe(CompositeKeyOf(NewPlacementSetting(2, "hideAdminBorder")));
        CompositeKeyOf(setting).Should().NotBe(CompositeKeyOf(NewPlacementSetting(1, "hideadminborder")));

        // Two instances carrying the same key are still two distinct objects. With no identity-based
        // equality on this type, reconciling them is the change tracker's work, not the entity's.
        setting.Should().NotBeSameAs(NewPlacementSetting(1, "hideAdminBorder"));
    }

    /// <summary>
    /// The two settings stores are separate: a setting on a module instance is not a setting on one of its
    /// placements.
    /// </summary>
    /// <remarks>
    /// The legacy model exposed one <c>Hashtable</c> per loaded object, so the distinction between a
    /// module-scoped and a placement-scoped setting existed only in whichever procedure had filled it.
    /// Two entities backed by two tables make the scope explicit, which is what allows the same setting
    /// name to carry different values at the two scopes.
    /// </remarks>
    [Fact]
    public void SettingsStores_AreScopedSeparatelyToTheModuleAndToItsPlacement()
    {
        Module module = NewModule(ModuleIdentitySeed);
        TabModule placement = NewPlacement(1);

        module.Settings.Add(NewSetting(module.ModuleId, "Editor"));
        placement.Settings.Add(NewPlacementSetting(placement.TabModuleId, "Editor"));

        module.Settings.Should().ContainSingle();
        placement.Settings.Should().ContainSingle();

        typeof(ModuleSetting).GetProperty("TabModuleId").Should().BeNull(
            "a module-scoped setting is keyed by the module, never by a placement");
        typeof(TabModuleSetting).GetProperty("ModuleId").Should().BeNull(
            "a placement-scoped setting is keyed by the placement, never by the module");
    }

    // =============================================================================================
    // PortalDesktopModule — the per-portal grant, and the two names that were never columns
    // =============================================================================================

    /// <summary>
    /// The grant carries exactly the three columns its table has, and neither of the two names the legacy
    /// class read from a join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy <c>PortalDesktopModuleInfo</c> declared five properties, but
    /// <c>dbo.PortalDesktopModules</c> has only three columns. <c>02.02.02.SqlDataProvider</c> creates it
    /// with <c>PortalDesktopModuleID</c>, <c>PortalID</c> and <c>DesktopModuleID</c> and nothing else, and
    /// its <c>GetPortalDesktopModules</c> procedure in the same script selects
    /// <c>PortalDesktopModules.*, PortalName, FriendlyName</c> across joins to <c>Portals</c> and
    /// <c>DesktopModules</c>. <c>FriendlyName</c> and <c>PortalName</c> are therefore <b>denormalised join
    /// projections</b>, not columns on the join table; the legacy reflection hydrator filled all five
    /// properties indiscriminately, which is precisely how a result-set shape came to be mistaken for an
    /// entity shape.
    /// </para>
    /// <para>
    /// Both are absent here by design and neither may be added back as a scalar. Their only legacy purpose
    /// was display text for an administrator's picker, so they belong on an Application-layer DTO composed
    /// from this entity's two references.
    /// </para>
    /// </remarks>
    [Fact]
    public void PortalDesktopModule_CarriesExactlyTheThreeTableColumns()
    {
        DataPropertyNames(typeof(PortalDesktopModule)).Should().BeEquivalentTo(
            "PortalDesktopModuleId",
            "PortalId",
            "DesktopModuleId");

        typeof(PortalDesktopModule).GetProperty("FriendlyName").Should().BeNull(
            "FriendlyName arrives from DesktopModules in a join projection and is not a column here");
        typeof(PortalDesktopModule).GetProperty("PortalName").Should().BeNull(
            "PortalName arrives from Portals in a join projection and is not a column here");

        // The two names remain reachable through the references, which is where the join actually lives.
        DesktopModule package = NewPackage(0, DesktopModuleIdentitySeed);
        package.FriendlyName = "Text/HTML";

        PortalDesktopModule grant = new()
        {
            PortalDesktopModuleId = PortalDesktopModuleIdentitySeed,
            PortalId = 0,
            DesktopModuleId = package.DesktopModuleId,
            DesktopModule = package,
        };

        grant.Identity.Should().Be(PortalDesktopModuleIdentitySeed);
        grant.DesktopModule.FriendlyName.Should().Be(
            "Text/HTML",
            "the display name is composed from the principal rather than duplicated onto the grant");
    }

    /// <summary>
    /// The grant's portal key accepts both values the portal table actually issues.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>PortalID</c> is <c>NOT NULL</c> on this table, so the grant always names a portal.
    /// Because <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, the two values it names first are -1
    /// and 0 — the legacy integer sentinel and <c>default(int)</c> respectively. Both are ordinary portal
    /// keys, which the guarding value object confirms; that wrapper is exercised in full by the portal
    /// suite and is used here only to tie the module-side key to the same primitive.
    /// </remarks>
    /// <param name="portalId">The portal key under test.</param>
    [Theory]
    [InlineData(LegacyNullInteger)]
    [InlineData(0)]
    [InlineData(1)]
    public void PortalDesktopModule_AcceptsEveryRealPortalKey(int portalId)
    {
        PortalDesktopModule grant = new()
        {
            PortalDesktopModuleId = PortalDesktopModuleIdentitySeed,
            PortalId = portalId,
            DesktopModuleId = DesktopModuleIdentitySeed,
        };

        grant.PortalId.Should().Be(portalId, "every portal key is a real one, including -1 and 0");
        new PortalId(grant.PortalId).Value.Should().Be(
            portalId,
            "the module-side portal key is the same primitive the portal aggregate uses, and that "
            + "primitive reserves no value");
    }

    // =============================================================================================
    // Tab — module placement scope only; the page aggregate has no suite of its own by design
    // =============================================================================================

    /// <summary>
    /// A page's key is zero-seeded, and the legacy constructor could not say whether a page had one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is a measured legacy inconsistency, preserved as knowledge rather than corrected as
    /// behaviour, per Minimal Change Clause item 1. <c>TabInfo</c>'s constructor
    /// (<c>Library/Components/Tabs/TabInfo.vb</c> lines 86 to 105) seeds sixteen fields but <b>not</b>
    /// <c>_TabID</c>, so a freshly constructed page carried <c>TabID = 0</c> — and because
    /// <c>dbo.Tabs.TabID</c> is <c>IDENTITY(0, 1)</c>, that value is indistinguishable from the first real
    /// page of the installation. <c>ModuleInfo</c>'s constructor, by contrast, <em>did</em> seed
    /// <c>_ModuleID = Null.NullInteger</c> (line 108) even though <c>dbo.Modules.ModuleID</c> is equally
    /// zero-seeded. Two sibling classes over two identically seeded tables therefore disagreed about how
    /// to represent "no key yet", and neither convention worked.
    /// </para>
    /// <para>
    /// The target resolves the asymmetry without adopting either convention: no entity carries a sentinel
    /// initialiser, and existence is declared through
    /// <see cref="Entity{TId}.MarkIdentityPersisted"/> by code that already knows the row exists -
    /// which these tests do explicitly, because nothing declares it automatically. That is why <c>default(int) == 0</c> is not
    /// interpretable as "no id" for either table, which is what this test asserts.
    /// </para>
    /// </remarks>
    [Fact]
    public void Tab_ZeroIsARealPageAndTheLegacyConstructorCouldNotSayOtherwise()
    {
        Tab fresh = NewTab(TabIdentitySeed);
        Module freshModule = NewModule(ModuleIdentitySeed);

        fresh.TabId.Should().Be(0, "the legacy constructor left _TabID at the CLR default, and so does this");
        fresh.TabId.Should().Be(default);
        freshModule.ModuleId.Should().Be(default);

        // Neither default is interpretable as an absence: only the declaration answers that question, and
        // it answers it identically for both tables, which the legacy code did not.
        fresh.IdentityIsPersisted.Should().BeFalse();
        freshModule.IdentityIsPersisted.Should().BeFalse();

        fresh.MarkIdentityPersisted();
        freshModule.MarkIdentityPersisted();

        fresh.IdentityIsPersisted.Should().BeTrue("a page keyed zero is a written row once declared so");
        freshModule.IdentityIsPersisted.Should().BeTrue();
        fresh.Identity.Should().Be(0);
        freshModule.Identity.Should().Be(0);
    }

    /// <summary>
    /// A root page expresses "no parent" as a null rather than as the legacy sentinel.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>TabInfo</c>'s constructor seeded <c>_ParentId = Null.NullInteger</c>, so -1 meant
    /// "this page is at the root of its portal's hierarchy" — yet another distinct meaning of -1 in this
    /// domain, alongside the generic integer sentinel, a real portal key, the all-users role identifier,
    /// the anonymous access level and the capability-mask guard. The column is <c>int NULL</c> with a
    /// self-referencing foreign key, so the target expresses the root as null and -1 must never be
    /// substituted back for it: doing so would make a root page point at a portal-shaped key.
    /// </remarks>
    [Fact]
    public void Tab_ExpressesTheRootAsNullRatherThanAsTheLegacySentinel()
    {
        Tab root = NewTab(TabIdentitySeed);
        root.ParentId.Should().BeNull("a page at the root of the hierarchy has no parent key at all");
        root.Parent.Should().BeNull();

        Tab child = NewTab(1);
        child.ParentId = root.TabId;
        child.Parent = root;
        root.Children.Add(child);

        child.ParentId.Should().Be(
            0,
            "zero is a real page key, so a child of the first page names it rather than reading it as "
            + "absent");
        root.Children.Should().ContainSingle().Which.Should().BeSameAs(child);

        // -1 remains storable, because the column accepts it; what it may not do is mean "root".
        Tab legacyPayload = NewTab(2);
        legacyPayload.ParentId = LegacyNullInteger;
        legacyPayload.ParentId.Should().Be(LegacyNullInteger).And.NotBeNull(
            "a -1 arriving from a legacy payload is a key, and a root is expressed by null instead");
    }

    /// <summary>
    /// The page replaces the legacy constructor's sixteen sentinel initialisations with nullability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>TabInfo.vb</c> lines 86 to 105 seeded exactly sixteen fields under the author's
    /// comment "initialize the properties that can be null in the database" — three integers with
    /// <c>Null.NullInteger</c> (<c>_PortalID</c>, <c>_ParentId</c>, <c>_RefreshInterval</c>), eleven
    /// strings with <c>Null.NullString</c> (<c>_AuthorizedRoles</c>, <c>_IconFile</c>,
    /// <c>_AdministratorRoles</c>, <c>_Title</c>, <c>_Description</c>, <c>_KeyWords</c>, <c>_Url</c>,
    /// <c>_SkinSrc</c>, <c>_ContainerSrc</c>, <c>_TabPath</c>, <c>_PageHeadText</c>) and two dates with
    /// <c>Null.NullDate</c>.
    /// </para>
    /// <para>
    /// Fourteen of the sixteen map onto a nullable property here. The two that do not are
    /// <c>AuthorizedRoles</c> and <c>AdministratorRoles</c>: <c>03.00.01</c> dropped both columns when
    /// page grants became rows in <c>dbo.TabPermission</c>, so the semicolon-delimited role-identifier
    /// strings the legacy constructor seeded have no target member at all, and grants are reached through
    /// <see cref="Tab.TabPermissions"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Tab_ReplacesTheSixteenSentinelInitialisationsWithNullability()
    {
        Tab fresh = NewTab(TabIdentitySeed);

        // The three legacy -1 integers.
        fresh.PortalId.Should().BeNull("the page's portal key is nullable, and -1 is a real portal key");
        fresh.ParentId.Should().BeNull();
        fresh.RefreshInterval.Should().BeNull("no refresh interval is a null, not a negative number");

        // Nine of the eleven legacy "" strings.
        fresh.IconFile.Should().BeNull();
        fresh.Title.Should().BeNull();
        fresh.Description.Should().BeNull();
        fresh.Keywords.Should().BeNull();
        fresh.Url.Should().BeNull();
        fresh.SkinSrc.Should().BeNull();
        fresh.ContainerSrc.Should().BeNull();
        fresh.TabPath.Should().BeNull("the path is computed by the tree renumbering rather than supplied");
        fresh.PageHeadText.Should().BeNull();

        // The two legacy DateTime.MinValue dates.
        fresh.StartDate.Should().BeNull();
        fresh.EndDate.Should().BeNull();

        // The two of the sixteen whose columns were dropped outright.
        typeof(Tab).GetProperty("AuthorizedRoles").Should().BeNull(
            "03.00.01 dropped the column when page grants became rows in dbo.TabPermission");
        typeof(Tab).GetProperty("AdministratorRoles").Should().BeNull(
            "03.00.01 dropped the column when page grants became rows in dbo.TabPermission");
    }

    /// <summary>
    /// The Web Forms render state the legacy page class carried does not survive as a domain member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>BreadCrumbs</c>, <c>Panes</c> and <c>Modules</c> (<c>TabInfo.vb</c> lines 365, 374
    /// and 383) were untyped <c>ArrayList</c> properties, each <c>&lt;XmlIgnore()&gt;</c>, populated while
    /// a skin laid out a request. There is no server-side rendering in the target: navigation is the
    /// Angular router's concern, and a page's placements are reached through the typed
    /// <see cref="Tab.TabModules"/> collection instead of an untyped list. <c>SkinPath</c>,
    /// <c>ContainerPath</c>, <c>Level</c> as a computed value and <c>IsSuperTab</c> belong to the same
    /// category.
    /// </para>
    /// <para>
    /// MIGRATION: <c>TabType</c> (line 406) and <c>FullUrl</c> (line 412) were read-only computed members
    /// that resolved navigation URLs, and <c>IsAdminTab</c> (line 435) was the sharpest case of all — its
    /// getter reached the portal settings, then the cache, then the portal controller, so <em>reading a
    /// property issued a database query</em>. A domain entity performs no I/O, so none of the three has a
    /// counterpart. The legacy <c>TabType</c> enumeration itself measured <c>File = 0</c>,
    /// <c>Normal = 1</c>, <c>Tab = 2</c>, <c>Url = 3</c>, <c>Member = 4</c> with implicit ordinals, so
    /// <c>default(TabType)</c> was <c>File</c> rather than <c>Normal</c>; the values are recorded here
    /// because the type is not in the target's enum set and <c>Domain/Enums/TabType.cs</c> does not
    /// exist. Nothing is fabricated to stand in for it.
    /// </para>
    /// </remarks>
    /// <param name="memberName">The legacy member that must not exist on the page entity.</param>
    [Theory]
    [InlineData("BreadCrumbs")]
    [InlineData("Panes")]
    [InlineData("Modules")]
    [InlineData("SkinPath")]
    [InlineData("ContainerPath")]
    [InlineData("IsSuperTab")]
    [InlineData("TabType")]
    [InlineData("FullUrl")]
    [InlineData("IsAdminTab")]
    [InlineData("Cacheability")]
    [InlineData("SuperTabIdSet")]
    [InlineData("HasChildren")]
    public void Tab_OmitsTheWebFormsRenderStateAndTheComputedNavigationMembers(string memberName)
    {
        typeof(Tab).GetProperty(memberName).Should().BeNull(
            $"{memberName} was Web Forms render state or a computed navigation value, not a stored column");
    }

    /// <summary>
    /// A page is visible and undeleted until something says otherwise.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>IsVisible</c> is initialised to <see langword="true"/>, which reproduces the store
    /// default <c>DF_Tabs_IsVisible DEFAULT (1)</c> rather than the legacy object default. <c>TabInfo</c>
    /// left <c>_IsVisible</c> unseeded, so a legacy in-memory page started hidden while any row inserted
    /// without naming the column started visible — the same object-versus-store disagreement that
    /// <c>DisplaySyndicate</c> exhibits, resolved here in favour of the store because a
    /// <see cref="bool"/> cannot distinguish "not supplied" from "explicitly false" and a request for a
    /// hidden page would otherwise be stored as visible. The divergence is recorded in
    /// <c>MIGRATION_NOTES.md</c>.
    /// </remarks>
    [Fact]
    public void Tab_IsVisibleAndUndeletedByDefault()
    {
        Tab page = NewTab(1);

        page.IsVisible.Should().BeTrue("the initialiser reproduces the store default of 1");
        page.IsDeleted.Should().BeFalse();
        page.DisableLink.Should().BeFalse();
        page.IsSecure.Should().BeFalse();
        page.Level.Should().Be(0, "the root of the hierarchy is depth zero");
        page.TabOrder.Should().Be(0);
    }

    // =============================================================================================
    // Enumerations — one rename to record, and two legacy types with no target
    // =============================================================================================

    /// <summary>
    /// The visibility discriminator keeps the stored integer meanings, the member names and the default it
    /// had in the legacy schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy type is <c>DotNetNuke.Entities.Modules.VisibilityState</c>
    /// (<c>ModuleInfo.vb</c> lines 30 to 34) and the target type is
    /// <see cref="ModuleVisibility"/> — a <b>rename of the type only</b>, recorded in
    /// <c>MIGRATION_NOTES.md</c>. The three member names are byte-identical, because the legacy portal
    /// template importer matched them <em>as text</em>, so renaming one would break template import.
    /// </para>
    /// <para>
    /// The ordinals are persisted data held in <c>TabModules.Visibility</c>, declared <c>int NOT NULL</c>,
    /// and the legacy reader decoded them by number. VB.NET assigned them implicitly from declaration
    /// order; the target writes them out explicitly, which is a faithfulness improvement rather than a
    /// change, and renumbering any of them would reinterpret every existing row.
    /// </para>
    /// <para>
    /// <c>Maximized</c> is zero and is therefore <c>default(ModuleVisibility)</c>, which matches the
    /// legacy constructor: it left the backing field at the same member. The legacy reader also folded the
    /// integer sentinel -1 into that member rather than treating it as a further state, so the type
    /// deliberately carries no "not yet chosen" member and none may be added.
    /// </para>
    /// </remarks>
    [Fact]
    public void ModuleVisibility_KeepsItsStoredIntegerMeaningsAndItsDefault()
    {
        ((int)ModuleVisibility.Maximized).Should().Be(0);
        ((int)ModuleVisibility.Minimized).Should().Be(1);
        ((int)ModuleVisibility.None).Should().Be(2);

        default(ModuleVisibility).Should().Be(
            ModuleVisibility.Maximized,
            "zero is the maximised state, which is what the legacy constructor left the field at");

        Enum.GetNames<ModuleVisibility>().Should().BeEquivalentTo("Maximized", "Minimized", "None");
        Enum.GetValues<ModuleVisibility>().Should().BeEquivalentTo(new[]
        {
            ModuleVisibility.Maximized,
            ModuleVisibility.Minimized,
            ModuleVisibility.None,
        });

        // No absence member: the type has exactly the three legacy states and nothing else.
        Enum.IsDefined(ModuleVisibility.None).Should().BeTrue();
        Enum.IsDefined((ModuleVisibility)LegacyNullInteger).Should().BeFalse(
            "the legacy reader folded the sentinel into Maximized rather than adding a state for it");
        Enum.IsDefined((ModuleVisibility)3).Should().BeFalse();
    }

    /// <summary>
    /// The two legacy enumerations that stayed behind have no target type, and none is fabricated for
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>SecurityAccessLevel</c>
    /// (<c>Library/Components/Security/PortalSecurity.vb</c>, declared <c>As Integer</c> with
    /// <c>ControlPanel = -3</c>, <c>SkinObject = -2</c>, <c>Anonymous = -1</c>, <c>View = 0</c>,
    /// <c>Edit = 1</c>, <c>Admin = 2</c>, <c>Host = 3</c>) and <c>TabType</c>
    /// (<c>Library/Components/Tabs/TabInfo.vb</c> lines 32 to 38, implicit ordinals
    /// <c>File = 0</c>, <c>Normal = 1</c>, <c>Tab = 2</c>, <c>Url = 3</c>, <c>Member = 4</c>) are both
    /// absent from the target's enumeration set. The measured values are recorded above and in
    /// <c>MIGRATION_NOTES.md</c>; the column that held the first is carried as a plain
    /// <see cref="int"/> on <see cref="ModuleControl"/>, and the second was never stored at all — it was
    /// computed from the page's URL.
    /// </para>
    /// <para>
    /// Their absence is asserted rather than merely observed, so that adding either one later is a
    /// deliberate decision that must revisit this test rather than an accident of porting. The two
    /// opposite failures this avoids are fabricating a type the plan never specified, and writing
    /// assertions against a type that does not exist.
    /// </para>
    /// </remarks>
    [Fact]
    public void DomainEnums_DeclareNoTypeForTheTwoLegacyEnumerationsThatStayedBehind()
    {
        Assembly domainAssembly = typeof(ModuleVisibility).Assembly;

        domainAssembly.GetType("DnnMigration.Domain.Enums.SecurityAccessLevel").Should().BeNull(
            "the access level is carried as the integer the column declares, on ModuleControl.ControlType");
        domainAssembly.GetType("DnnMigration.Domain.Enums.TabType").Should().BeNull(
            "the page type was computed from the URL and was never a stored column");

        // The one module enumeration that does exist is reachable from the same assembly, which is what
        // makes the two negatives above meaningful rather than a lookup mistake.
        domainAssembly.GetType("DnnMigration.Domain.Enums.ModuleVisibility").Should().Be(
            typeof(ModuleVisibility));
    }

    // =============================================================================================
    // Fixtures and reflection helpers
    // =============================================================================================

    /// <summary>
    /// Gets the seven entity types the module aggregate is split across, in dependency order.
    /// </summary>
    /// <remarks>
    /// The two settings entities are held separately, because they derive from nothing and so cannot take
    /// part in the base-type and identity assertions the rest of the aggregate shares.
    /// </remarks>
    private static IEnumerable<Type> ModuleAggregateTypes =>
    [
        typeof(DesktopModule),
        typeof(PortalDesktopModule),
        typeof(ModuleDefinition),
        typeof(ModuleControl),
        typeof(Module),
        typeof(TabModule),
        typeof(Tab),
    ];

    /// <summary>
    /// Builds an installed package carrying the supplied capability bit field.
    /// </summary>
    /// <param name="supportedFeatures">The bit field to store, sentinel values included.</param>
    /// <param name="desktopModuleId">The package key, defaulting to zero so that a caller asserting the
    /// capability projections need not name one.</param>
    /// <returns>The package.</returns>
    private static DesktopModule NewPackage(int supportedFeatures, int desktopModuleId = 0) => new()
    {
        DesktopModuleId = desktopModuleId,
        ModuleName = "DNN_HTML",
        FriendlyName = "Text/HTML",
        FolderName = "HTML",
        SupportedFeatures = supportedFeatures,
    };

    /// <summary>
    /// Builds a package whose identity has been declared persisted.
    /// </summary>
    /// <param name="desktopModuleId">The package key.</param>
    /// <returns>The package.</returns>
    private static DesktopModule NewPersistedPackage(int desktopModuleId)
    {
        DesktopModule package = NewPackage(0, desktopModuleId);
        package.MarkIdentityPersisted();
        return package;
    }

    /// <summary>
    /// Builds a module definition.
    /// </summary>
    /// <param name="moduleDefinitionId">The definition key.</param>
    /// <returns>The definition.</returns>
    private static ModuleDefinition NewDefinition(int moduleDefinitionId) => new()
    {
        ModuleDefinitionId = moduleDefinitionId,
        FriendlyName = "Text/HTML",
    };

    /// <summary>
    /// Builds a module definition whose identity has been declared persisted.
    /// </summary>
    /// <param name="moduleDefinitionId">The definition key.</param>
    /// <returns>The definition.</returns>
    private static ModuleDefinition NewPersistedDefinition(int moduleDefinitionId)
    {
        ModuleDefinition definition = NewDefinition(moduleDefinitionId);
        definition.MarkIdentityPersisted();
        return definition;
    }

    /// <summary>
    /// Builds a module control.
    /// </summary>
    /// <param name="moduleControlId">The control key.</param>
    /// <returns>The control.</returns>
    private static ModuleControl NewControl(int moduleControlId) => new()
    {
        ModuleControlId = moduleControlId,
    };

    /// <summary>
    /// Builds a module instance whose definition key is the first the definition table can issue.
    /// </summary>
    /// <param name="moduleId">The module key, sentinel and zero-seed values included.</param>
    /// <returns>The module.</returns>
    private static Module NewModule(int moduleId) => new()
    {
        ModuleId = moduleId,
        ModuleDefinitionId = ModuleDefinitionIdentitySeed,
    };

    /// <summary>
    /// Builds a module instance whose identity has been declared persisted.
    /// </summary>
    /// <param name="moduleId">The module key.</param>
    /// <returns>The module.</returns>
    private static Module NewPersistedModule(int moduleId)
    {
        Module module = NewModule(moduleId);
        module.MarkIdentityPersisted();
        return module;
    }

    /// <summary>
    /// Builds a placement of the zero-keyed module on the zero-keyed page, so that both identity seeds are
    /// exercised by default.
    /// </summary>
    /// <param name="tabModuleId">The placement key.</param>
    /// <returns>The placement.</returns>
    private static TabModule NewPlacement(int tabModuleId) => new()
    {
        TabModuleId = tabModuleId,
        TabId = TabIdentitySeed,
        ModuleId = ModuleIdentitySeed,
    };

    /// <summary>
    /// Builds a page.
    /// </summary>
    /// <param name="tabId">The page key, zero seed included.</param>
    /// <returns>The page.</returns>
    private static Tab NewTab(int tabId) => new()
    {
        TabId = tabId,
        TabName = "Home",
    };

    /// <summary>
    /// Builds a page whose identity has been declared persisted.
    /// </summary>
    /// <param name="tabId">The page key.</param>
    /// <returns>The page.</returns>
    private static Tab NewPersistedTab(int tabId)
    {
        Tab tab = NewTab(tabId);
        tab.MarkIdentityPersisted();
        return tab;
    }

    /// <summary>
    /// Builds a module setting carrying the supplied composite key.
    /// </summary>
    /// <param name="moduleId">The owning module.</param>
    /// <param name="settingName">The setting name.</param>
    /// <returns>The setting.</returns>
    private static ModuleSetting NewSetting(int moduleId, string settingName) => new()
    {
        ModuleId = moduleId,
        SettingName = settingName,
        SettingValue = "irrelevant",
    };

    /// <summary>
    /// Projects the two columns that make up a module setting's composite primary key.
    /// </summary>
    /// <param name="setting">The setting to read.</param>
    /// <returns>The <c>(ModuleID, SettingName)</c> pair the clustered key is built from.</returns>
    private static (int ModuleId, string SettingName) CompositeKeyOf(ModuleSetting setting) =>
        (setting.ModuleId, setting.SettingName);

    /// <summary>
    /// Builds a placement setting carrying the supplied composite key.
    /// </summary>
    /// <param name="tabModuleId">The owning placement.</param>
    /// <param name="settingName">The setting name.</param>
    /// <returns>The setting.</returns>
    private static TabModuleSetting NewPlacementSetting(int tabModuleId, string settingName) => new()
    {
        TabModuleId = tabModuleId,
        SettingName = settingName,
        SettingValue = "irrelevant",
    };

    /// <summary>
    /// Projects the two columns that make up a placement setting's composite primary key.
    /// </summary>
    /// <param name="setting">The setting to read.</param>
    /// <returns>The <c>(TabModuleID, SettingName)</c> pair the clustered key is built from.</returns>
    private static (int TabModuleId, string SettingName) CompositeKeyOf(TabModuleSetting setting) =>
        (setting.TabModuleId, setting.SettingName);

    /// <summary>
    /// Resolves a declared public instance property, failing the test rather than returning null when the
    /// member has been renamed or removed.
    /// </summary>
    /// <typeparam name="TEntity">The type to inspect.</typeparam>
    /// <param name="propertyName">The property to resolve.</param>
    /// <returns>The property.</returns>
    private static PropertyInfo PropertyOf<TEntity>(string propertyName)
    {
        PropertyInfo? property = typeof(TEntity).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull($"{typeof(TEntity).Name}.{propertyName} must exist");
        return property!;
    }

    /// <summary>
    /// Names the attributes an author wrote, excluding the ones the compiler emits.
    /// </summary>
    /// <remarks>
    /// Enabling nullable reference types makes the compiler stamp <c>NullableAttribute</c> and
    /// <c>NullableContextAttribute</c> onto a type and onto every member with a reference-typed signature,
    /// so an unfiltered check would fail for a reason that has nothing to do with the legacy attributes
    /// under test.
    /// </remarks>
    /// <param name="attributes">The attribute data to filter.</param>
    /// <returns>The authored attribute names.</returns>
    private static IEnumerable<string> AuthoredAttributeNames(IList<CustomAttributeData> attributes)
    {
        return attributes
            .Select(attribute => attribute.AttributeType.FullName ?? attribute.AttributeType.Name)
            .Where(name => !name.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Names the scalar, column-backed properties a type declares, excluding its identity projection and
    /// its navigations.
    /// </summary>
    /// <param name="entityType">The entity to inspect.</param>
    /// <returns>The data property names.</returns>
    private static IEnumerable<string> DataPropertyNames(Type entityType)
    {
        return entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.Name != nameof(Entity<int>.Identity))
            .Where(property => !IsNavigation(property.PropertyType))
            .Select(property => property.Name);
    }

    /// <summary>
    /// Determines whether a property type is a relationship rather than a stored scalar.
    /// </summary>
    /// <param name="propertyType">The property type to classify.</param>
    /// <returns><see langword="true"/> for a reference to, or a collection of, another entity.</returns>
    private static bool IsNavigation(Type propertyType)
    {
        if (propertyType.IsGenericType
            && propertyType.GetGenericTypeDefinition() == typeof(ICollection<>))
        {
            return true;
        }

        return propertyType.Namespace == typeof(Module).Namespace;
    }
}
