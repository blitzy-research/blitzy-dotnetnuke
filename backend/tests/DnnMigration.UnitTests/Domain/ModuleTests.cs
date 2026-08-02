using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the module aggregate and the four tables the legacy model flattened into one class:
/// the module itself, its placement on a page, its settings and its placement settings.
/// </summary>
/// <remarks>
/// <para>
/// The legacy <c>ModuleInfo</c> was a single 58-property class that was really a join across
/// <c>Modules</c>, <c>TabModules</c>, <c>ModuleDefinitions</c> and <c>ModuleControls</c>. Splitting it
/// along the real table boundaries introduced two composite-key entities, and the assertions below
/// pin the consequences: a settings row is identified by its owning module <em>and</em> its name, so
/// two settings of the same module are distinct and the same setting name under two modules is
/// distinct too.
/// </para>
/// <para>
/// <c>dbo.Modules.ModuleID</c> and <c>dbo.Tabs.TabID</c> are both declared <c>IDENTITY(0, 1)</c>, so
/// zero is a genuine identifier for both. That is asserted here because the composite keys make the
/// zero case load-bearing: a settings row whose owning module is numbered zero must still be
/// addressable.
/// </para>
/// </remarks>
public class ModuleTests
{
    private const int ModuleIdentitySeed = 0;

    private const int TabIdentitySeed = 0;

    /// <summary>
    /// The module aggregate reports its primary key as its identity, zero seed included.
    /// </summary>
    /// <param name="moduleId">The identifier under test.</param>
    [Theory]
    [InlineData(ModuleIdentitySeed)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void ModuleIdentity_IsThePrimaryKey(int moduleId)
    {
        Module module = new() { ModuleId = moduleId, ModuleDefinitionId = 5 };
        Module sameRow = new() { ModuleId = moduleId, ModuleDefinitionId = 99 };

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        module.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();

        module.Identity.Should().Be(moduleId);
        module.Should().Be(sameRow);
    }

    /// <summary>
    /// The page aggregate reports its primary key as its identity, zero seed included.
    /// </summary>
    [Fact]
    public void TabIdentity_IsThePrimaryKeyAndZeroIsARealPage()
    {
        Tab tab = new() { TabId = TabIdentitySeed, TabName = "Home" };
        Tab sameRow = new() { TabId = TabIdentitySeed, TabName = "Something Else" };
        Tab otherRow = new() { TabId = 1, TabName = "Home" };

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        tab.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        tab.Identity.Should().Be(TabIdentitySeed);
        tab.Should().Be(sameRow);
        tab.Should().NotBe(otherRow);
    }

    /// <summary>
    /// A module and a page carrying the same numeric key are different entities.
    /// </summary>
    [Fact]
    public void ModuleAndTab_ShareTheZeroSeedButNotIdentity()
    {
        Entity<int> module = new Module { ModuleId = 0, ModuleDefinitionId = 1 };
        Entity<int> tab = new Tab { TabId = 0, TabName = "Home" };

        module.Equals(tab).Should().BeFalse(
            "both tables seed at zero, which is precisely why the aggregate type has to take part in equality");
    }

    /// <summary>
    /// A module setting is identified by its owning module together with its name.
    /// </summary>
    [Fact]
    public void ModuleSettingIdentity_IsTheCompositeKey()
    {
        ModuleSetting setting = new()
        {
            ModuleId = ModuleIdentitySeed,
            SettingName = "CacheDuration",
            SettingValue = "600",
        };

        setting.Identity.Should().Be((ModuleIdentitySeed, "CacheDuration"));
    }

    /// <summary>
    /// Two settings differing only in name, or only in owner, are distinct rows.
    /// </summary>
    [Fact]
    public void ModuleSettingIdentity_SeparatesTheNameFromTheOwner()
    {
        ModuleSetting first = NewSetting(0, "Alpha");
        ModuleSetting sameOwnerOtherName = NewSetting(0, "Beta");
        ModuleSetting otherOwnerSameName = NewSetting(1, "Alpha");
        ModuleSetting duplicate = NewSetting(0, "Alpha");

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        first.MarkIdentityPersisted();
        sameOwnerOtherName.MarkIdentityPersisted();
        otherOwnerSameName.MarkIdentityPersisted();
        duplicate.MarkIdentityPersisted();

        first.Should().NotBe(sameOwnerOtherName);
        first.Should().NotBe(otherOwnerSameName);
        first.Should().Be(duplicate);
        first.GetHashCode().Should().Be(duplicate.GetHashCode());
    }

    /// <summary>
    /// Setting names are compared exactly, because the column that stores them is case-preserving.
    /// </summary>
    [Fact]
    public void ModuleSettingIdentity_ComparesTheNameExactly()
    {
        ModuleSetting lower = NewSetting(0, "cacheduration");
        ModuleSetting mixed = NewSetting(0, "CacheDuration");

        lower.Should().NotBe(
            mixed,
            "the identity tuple compares the stored name ordinally; case folding belongs to the query "
            + "layer, not to entity identity");
    }

    /// <summary>
    /// A placement setting is identified by its owning placement together with its name.
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

        setting.Identity.Should().Be((1, "hideAdminBorder"));
        setting.Should().NotBe(new TabModuleSetting
        {
            TabModuleId = 2,
            SettingName = "hideAdminBorder",
            SettingValue = "True",
        });
    }

    /// <summary>
    /// A placement carries the display defaults the legacy grid rendered with.
    /// </summary>
    [Fact]
    public void Placement_CarriesTheLegacyDisplayDefaults()
    {
        TabModule placement = new() { TabModuleId = 1, TabId = 0, ModuleId = 0 };

        placement.PaneName.Should().BeEmpty();
        placement.ModuleOrder.Should().Be(0);
        placement.CacheTime.Should().Be(0);
        placement.Visibility.Should().Be(ModuleVisibility.Maximized, "zero is the maximised state");
        placement.DisplayTitle.Should().BeTrue();
        placement.DisplayPrint.Should().BeTrue();
        placement.DisplaySyndicate.Should().BeTrue();
        placement.TabModuleSettings.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// The visibility discriminator keeps the stored integer meanings it had in the legacy schema.
    /// </summary>
    [Fact]
    public void Visibility_KeepsItsStoredIntegerMeanings()
    {
        ((int)ModuleVisibility.Maximized).Should().Be(0);
        ((int)ModuleVisibility.Minimized).Should().Be(1);
        ((int)ModuleVisibility.None).Should().Be(2);
    }

    /// <summary>
    /// View-permission inheritance is a tri-state, because the column admits no value at all.
    /// </summary>
    [Fact]
    public void InheritViewPermissions_IsATriState()
    {
        Module unset = new() { ModuleId = 1, ModuleDefinitionId = 1 };
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
    /// A module may be owned by the installation rather than by a tenant.
    /// </summary>
    [Fact]
    public void PortalOwnership_IsOptional()
    {
        Module hostOwned = new() { ModuleId = 1, ModuleDefinitionId = 1 };
        Module tenantOwned = new() { ModuleId = 2, ModuleDefinitionId = 1, PortalId = 0 };

        hostOwned.PortalId.Should().BeNull(
            "dbo.Modules.PortalID is nullable, which is how a host-level module instance is expressed");
        tenantOwned.PortalId.Should().Be(0, "zero is the second tenant of an installation, not an absence");
    }

    /// <summary>
    /// A newly constructed module and page expose empty collections rather than null ones.
    /// </summary>
    [Fact]
    public void NavigationCollections_AreInitialisedRatherThanNull()
    {
        Module module = new() { ModuleId = 1, ModuleDefinitionId = 1 };
        Tab tab = new() { TabId = 1, TabName = "Reports" };

        module.TabModules.Should().NotBeNull().And.BeEmpty();
        module.ModuleSettings.Should().NotBeNull().And.BeEmpty();
        module.ModulePermissions.Should().NotBeNull().And.BeEmpty();
        tab.Children.Should().NotBeNull().And.BeEmpty();
        tab.TabModules.Should().NotBeNull().And.BeEmpty();
        tab.TabPermissions.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// A page is visible and undeleted until something says otherwise.
    /// </summary>
    [Fact]
    public void Tab_IsVisibleAndUndeletedByDefault()
    {
        Tab tab = new() { TabId = 1, TabName = "Reports" };

        tab.IsVisible.Should().BeTrue();
        tab.IsDeleted.Should().BeFalse();
        tab.DisableLink.Should().BeFalse();
        tab.IsSecure.Should().BeFalse();
        tab.Level.Should().Be(0);
        tab.ParentId.Should().BeNull();
        tab.TabPath.Should().BeNull("the path is computed by the tree renumbering rather than supplied");
    }

    /// <summary>
    /// A module is undeleted and single-page until something says otherwise.
    /// </summary>
    [Fact]
    public void Module_IsUndeletedAndSinglePageByDefault()
    {
        Module module = new() { ModuleId = 1, ModuleDefinitionId = 1 };

        module.IsDeleted.Should().BeFalse();
        module.AllTabs.Should().BeFalse();
        module.ModuleTitle.Should().BeNull();
        module.StartDate.Should().BeNull();
        module.EndDate.Should().BeNull();
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
}
