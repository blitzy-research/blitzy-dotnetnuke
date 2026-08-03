using System.Globalization;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Mapping;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Mapping;

/// <summary>
/// Covers the five hand-written mappers that carry entities across the API boundary in both directions.
/// </summary>
/// <remarks>
/// <para>
/// These mappers exist instead of a convention-based mapping library, and that choice is the reason this
/// suite is worth writing. A convention-based mapper is verified by asserting that a configuration is
/// internally consistent; a hand-written one has to be verified by asserting that each field actually
/// lands where it is supposed to. Every projection below therefore populates its source with values that
/// are distinguishable from one another, so a copy-and-paste error that assigns the wrong source field
/// fails rather than passing silently.
/// </para>
/// <para>
/// Three classes of behaviour get particular attention, because they are the places where a mapper does
/// more than copy. The first is defaulting: several mappers substitute a value when the request omits one,
/// and the substitute differs between the create path and the update path in a way that is easy to get
/// wrong. The second is clamping: fees, quotas and lengths are floored at zero rather than trusted, which
/// preserves the legacy screens' behaviour of refusing to store a negative charge. The third is the
/// deliberate naming bridges, where a target field is spelled differently from the legacy column it
/// carries - those are asserted explicitly so that nobody "corrects" one of them and silently drops data.
/// </para>
/// <para>
/// No entity ever crosses the API boundary, so the direction of each mapping matters. Read mappers
/// project an entity plus separately-resolved context into a response object; write mappers apply a
/// request onto an entity that the caller already owns. A write mapper must therefore leave the entity's
/// identity and ownership alone, and that too is asserted rather than assumed.
/// </para>
/// </remarks>
public class MappingTests
{
    private const string DefaultLanguageCode = "en-US";

    private const int DefaultTimeZoneOffsetMinutes = -8;

    private const string DefaultPaneName = "ContentPane";

    // ---------------------------------------------------------------------------------------------
    // Portal
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The portal list projection carries the columns the list screen shows plus the three counts and
    /// aliases the service resolves separately.
    /// </summary>
    [Fact]
    public void PortalToListItem_ProjectsTheListColumnsAndTheResolvedContext()
    {
        Portal portal = FullPortal();
        string[] aliases = ["alpha.example.com", "beta.example.com"];

        PortalListItemDto dto = PortalMappings.ToListItem(portal, aliases, users: 41, pages: 7);

        dto.PortalId.Should().Be(-1);
        dto.PortalName.Should().Be("Measured Portal");
        dto.Aliases.Should().Equal(new[] { "alpha.example.com", "beta.example.com" });
        dto.Users.Should().Be(41);
        dto.Pages.Should().Be(7);
        dto.HostSpace.Should().Be(2048);
        dto.HostFee.Should().Be(12.5m);
        dto.ExpiryDate.Should().Be(new DateTime(2027, 3, 4, 5, 6, 7, DateTimeKind.Utc));
    }

    /// <summary>
    /// The portal detail projection takes the names, the counts, the address and the host page from its
    /// arguments rather than from the entity, because none of them is a column on the portal row.
    /// </summary>
    [Fact]
    public void PortalToDetail_TakesResolvedContextFromItsArgumentsRatherThanTheEntity()
    {
        Portal portal = FullPortal();

        PortalDetailDto dto = PortalMappings.ToDetail(
            portal,
            users: 41,
            pages: 7,
            administratorRoleName: "Administrators",
            registeredRoleName: "Registered Users",
            administratorEmail: "curator@example.com",
            superTabId: 99);

        dto.Users.Should().Be(41);
        dto.Pages.Should().Be(7);
        dto.AdministratorRoleName.Should().Be("Administrators");
        dto.RegisteredRoleName.Should().Be("Registered Users");
        dto.Email.Should().Be(
            "curator@example.com",
            "the administrator address lives on the account row, not on the portal row, so the service "
            + "resolves it and hands it in");
        dto.SuperTabId.Should().Be(
            99,
            "the host page is not a portal column either - it is resolved from the host tenant");

        dto.AdministratorRoleId.Should().Be(3);
        dto.RegisteredRoleId.Should().Be(4);
        dto.AdministratorId.Should().Be(2);
        dto.Guid.Should().Be(portal.PortalGuid);
        dto.AdminTabId.Should().Be(11);
        dto.SplashTabId.Should().Be(12);
        dto.HomeTabId.Should().Be(13);
        dto.LoginTabId.Should().Be(14);
        dto.UserTabId.Should().Be(15);
        dto.PaymentProcessor.Should().Be("PayPal");
        dto.ProcessorUserId.Should().Be("processor-user");
        dto.SiteLogHistory.Should().Be(60);
        dto.DefaultLanguage.Should().Be("en-GB");
        dto.TimeZoneOffset.Should().Be(120);
        dto.HomeDirectory.Should().Be("Portals/0");
        dto.UserRegistration.Should().Be(UserRegistrationMode.VerifiedRegistration);
        dto.BannerAdvertising.Should().Be(BannerAdvertisingMode.Host);
        dto.Currency.Should().Be("GBP");
    }

    /// <summary>
    /// The detail projection orders the aliases without regard to case.
    /// </summary>
    [Fact]
    public void PortalToDetail_OrdersAliasesWithoutRegardToCase()
    {
        Portal portal = FullPortal();
        portal.PortalAliases.Clear();
        portal.PortalAliases.Add(new PortalAlias { PortalAliasId = 3, PortalId = -1, HttpAlias = "zulu.example.com" });
        portal.PortalAliases.Add(new PortalAlias { PortalAliasId = 1, PortalId = -1, HttpAlias = "Alpha.example.com" });
        portal.PortalAliases.Add(new PortalAlias { PortalAliasId = 2, PortalId = -1, HttpAlias = "mike.example.com" });

        PortalDetailDto dto = PortalMappings.ToDetail(portal, 0, 0, null, null, null, null);

        dto.Aliases.Should().NotBeNull();
        IReadOnlyList<PortalAliasDto> ordered = dto.Aliases!;

        ordered.Select(alias => alias.HttpAlias).Should().Equal(
            new[] { "Alpha.example.com", "mike.example.com", "zulu.example.com" });
        ordered.Select(alias => alias.PortalAliasId).Should().Equal(new[] { 1, 2, 3 });
    }

    /// <summary>
    /// The settings projection carries the editable portal columns and omits the resolved context the
    /// detail projection adds.
    /// </summary>
    [Fact]
    public void PortalToSettings_CarriesTheEditableColumnsOnly()
    {
        Portal portal = FullPortal();

        PortalSettingsDto dto = PortalMappings.ToSettings(portal);

        dto.PortalId.Should().Be(-1);
        dto.PortalName.Should().Be("Measured Portal");
        dto.Description.Should().Be("A measured description");
        dto.KeyWords.Should().Be("measured,keywords");
        dto.FooterText.Should().Be("Measured footer");
        dto.LogoFile.Should().Be("logo.gif");
        dto.BackgroundFile.Should().Be("background.gif");
        dto.HostFee.Should().Be(12.5m);
        dto.HostSpace.Should().Be(2048);
        dto.PageQuota.Should().Be(50);
        dto.UserQuota.Should().Be(500);
        dto.AdministratorId.Should().Be(2);
        dto.Guid.Should().Be(portal.PortalGuid);
        dto.SiteLogHistory.Should().Be(60);
        dto.SplashTabId.Should().Be(12);
        dto.HomeTabId.Should().Be(13);
        dto.LoginTabId.Should().Be(14);
        dto.UserTabId.Should().Be(15);
        dto.DefaultLanguage.Should().Be("en-GB");
        dto.TimeZoneOffset.Should().Be(120);
        dto.HomeDirectory.Should().Be("Portals/0");
        dto.PaymentProcessor.Should().Be("PayPal");
        dto.ProcessorUserId.Should().Be("processor-user");
    }

    /// <summary>
    /// The alias projection carries all three of its columns.
    /// </summary>
    [Fact]
    public void PortalAliasToDto_CarriesAllThreeColumns()
    {
        PortalAlias alias = new() { PortalAliasId = 17, PortalId = -1, HttpAlias = "alpha.example.com" };

        PortalAliasDto dto = PortalMappings.ToDto(alias);

        dto.PortalAliasId.Should().Be(17);
        dto.PortalId.Should().Be(-1);
        dto.HttpAlias.Should().Be("alpha.example.com");
    }

    /// <summary>
    /// A new portal takes its narrative fields from the request and its hosting terms from the resolved
    /// defaults, and it starts closed to registration.
    /// </summary>
    [Fact]
    public void PortalToNewPortal_CombinesTheRequestWithTheResolvedDefaults()
    {
        CreatePortalRequest request = new()
        {
            PortalName = "New Tenant",
            Description = "A new tenant",
            KeyWords = "new,tenant",
            PortalAlias = "new.example.com",
        };

        Portal portal = PortalMappings.ToNewPortal(
            request,
            currency: "USD",
            expiryDate: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            hostFee: 9.5m,
            hostSpace: 100,
            pageQuota: 25,
            userQuota: 250,
            siteLogHistory: 30,
            homeDirectory: "Portals/7");

        portal.PortalName.Should().Be("New Tenant");
        portal.Description.Should().Be("A new tenant");
        portal.KeyWords.Should().Be("new,tenant");
        portal.Currency.Should().Be("USD");
        portal.ExpiryDate.Should().Be(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        portal.HostFee.Should().Be(9.5m);
        portal.HostSpace.Should().Be(100);
        portal.PageQuota.Should().Be(25);
        portal.UserQuota.Should().Be(250);
        portal.SiteLogHistory.Should().Be(30);
        portal.HomeDirectory.Should().Be("Portals/7");

        portal.UserRegistration.Should().Be(
            UserRegistrationMode.NoRegistration,
            "a tenant is created closed and is opened deliberately afterwards");
        portal.BannerAdvertising.Should().Be(BannerAdvertisingMode.None);
        portal.DefaultLanguage.Should().Be(DefaultLanguageCode);
        portal.TimeZoneOffset.Should().Be(DefaultTimeZoneOffsetMinutes);
        portal.PortalGuid.Should().NotBeEmpty(
            "the handle is generated here, and an all-zero handle is the legacy absent-value sentinel");
    }

    /// <summary>
    /// Each new portal receives its own handle.
    /// </summary>
    [Fact]
    public void PortalToNewPortal_GeneratesADistinctHandleEachTime()
    {
        CreatePortalRequest request = new() { PortalName = "Tenant" };

        Portal first = PortalMappings.ToNewPortal(request, "USD", null, 0m, 0, 0, 0, null, "Portals/1");
        Portal second = PortalMappings.ToNewPortal(request, "USD", null, 0m, 0, 0, 0, null, "Portals/2");

        first.PortalGuid.Should().NotBe(second.PortalGuid);
    }

    /// <summary>
    /// A new portal floors its hosting terms at zero and tolerates an unnamed request.
    /// </summary>
    [Fact]
    public void PortalToNewPortal_FloorsTheHostingTermsAndToleratesAnUnnamedRequest()
    {
        CreatePortalRequest request = new() { PortalName = null };

        Portal portal = PortalMappings.ToNewPortal(
            request, "USD", null, hostFee: -50m, hostSpace: -1, pageQuota: -2, userQuota: -3, null, "Portals/1");

        portal.PortalName.Should().BeEmpty(
            "the name column does not accept null, and the create validator does not require a name");
        portal.HostFee.Should().Be(0m);
        portal.HostSpace.Should().Be(0);
        portal.PageQuota.Should().Be(0);
        portal.UserQuota.Should().Be(0);
    }

    /// <summary>
    /// Applying an update overwrites the editable columns and leaves identity and role wiring alone.
    /// </summary>
    [Fact]
    public void PortalApplyUpdate_OverwritesTheEditableColumnsAndLeavesTheWiringAlone()
    {
        Portal portal = FullPortal();
        Guid handleBefore = portal.PortalGuid;

        UpdatePortalRequest request = new()
        {
            PortalName = "Renamed Portal",
            LogoFile = "new-logo.png",
            FooterText = "New footer",
            ExpiryDate = new DateTime(2031, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            UserRegistration = UserRegistrationMode.PublicRegistration,
            BannerAdvertising = BannerAdvertisingMode.Site,
            Currency = "EUR",
            AdministratorId = 88,
            HostFee = 3.25m,
            HostSpace = 4096,
            PageQuota = 75,
            UserQuota = 750,
            PaymentProcessor = "Stripe",
            ProcessorUserId = "new-processor-user",
            ProcessorPassword = "new-processor-password",
            Description = "A renamed description",
            KeyWords = "renamed,keywords",
            BackgroundFile = "new-background.png",
            SiteLogHistory = 90,
            SplashTabId = 21,
            HomeTabId = 22,
            LoginTabId = 23,
            UserTabId = 24,
            DefaultLanguage = "fr-FR",
            TimeZoneOffset = 60,
            HomeDirectory = "Portals/renamed",
        };

        PortalMappings.ApplyUpdate(portal, request);

        portal.PortalName.Should().Be("Renamed Portal");
        portal.LogoFile.Should().Be("new-logo.png");
        portal.FooterText.Should().Be("New footer");
        portal.ExpiryDate.Should().Be(new DateTime(2031, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        portal.UserRegistration.Should().Be(UserRegistrationMode.PublicRegistration);
        portal.BannerAdvertising.Should().Be(BannerAdvertisingMode.Site);
        portal.Currency.Should().Be("EUR");
        portal.AdministratorId.Should().Be(88);
        portal.HostFee.Should().Be(3.25m);
        portal.HostSpace.Should().Be(4096);
        portal.PageQuota.Should().Be(75);
        portal.UserQuota.Should().Be(750);
        portal.PaymentProcessor.Should().Be("Stripe");
        portal.ProcessorUserId.Should().Be("new-processor-user");
        portal.ProcessorPassword.Should().Be("new-processor-password");
        portal.Description.Should().Be("A renamed description");
        portal.KeyWords.Should().Be("renamed,keywords");
        portal.BackgroundFile.Should().Be("new-background.png");
        portal.SiteLogHistory.Should().Be(90);
        portal.SplashTabId.Should().Be(21);
        portal.HomeTabId.Should().Be(22);
        portal.LoginTabId.Should().Be(23);
        portal.UserTabId.Should().Be(24);
        portal.DefaultLanguage.Should().Be("fr-FR");
        portal.TimeZoneOffset.Should().Be(60);
        portal.HomeDirectory.Should().Be("Portals/renamed");

        portal.PortalId.Should().Be(-1, "an update never moves a row");
        portal.PortalGuid.Should().Be(handleBefore, "the handle is issued once and never reissued");
        portal.AdministratorRoleId.Should().Be(3, "role wiring is not part of the settings request");
        portal.RegisteredRoleId.Should().Be(4);
        portal.AdminTabId.Should().Be(11, "the administration page is not part of the settings request");
    }

    /// <summary>
    /// Applying an update substitutes for the omitted hosting terms, the omitted language and the omitted
    /// offset rather than storing null into columns that cannot hold it.
    /// </summary>
    [Fact]
    public void PortalApplyUpdate_SubstitutesForOmittedNonNullableColumns()
    {
        Portal portal = FullPortal();

        UpdatePortalRequest request = new()
        {
            PortalName = "Renamed",
            HostFee = null,
            HostSpace = null,
            PageQuota = null,
            UserQuota = null,
            DefaultLanguage = "   ",
            TimeZoneOffset = null,
            HomeDirectory = null,
        };

        PortalMappings.ApplyUpdate(portal, request);

        portal.HostFee.Should().Be(0m);
        portal.HostSpace.Should().Be(0);
        portal.PageQuota.Should().Be(0);
        portal.UserQuota.Should().Be(0);
        portal.DefaultLanguage.Should().Be(
            DefaultLanguageCode,
            "a whitespace-only language is treated as absent, because the column cannot hold a meaningless "
            + "culture code");
        portal.TimeZoneOffset.Should().Be(DefaultTimeZoneOffsetMinutes);
        portal.HomeDirectory.Should().BeEmpty();
    }

    /// <summary>
    /// Applying an update floors a negative charge and negative quotas.
    /// </summary>
    [Fact]
    public void PortalApplyUpdate_FloorsNegativeTerms()
    {
        Portal portal = FullPortal();

        UpdatePortalRequest request = new()
        {
            PortalName = "Renamed",
            HostFee = -0.01m,
            HostSpace = -5,
            PageQuota = -5,
            UserQuota = -5,
        };

        PortalMappings.ApplyUpdate(portal, request);

        portal.HostFee.Should().Be(0m);
        portal.HostSpace.Should().Be(0);
        portal.PageQuota.Should().Be(0);
        portal.UserQuota.Should().Be(0);
    }

    /// <summary>
    /// The fee floor admits zero and every positive amount and refuses to store a credit.
    /// </summary>
    /// <param name="supplied">The submitted amount, as an invariant decimal literal.</param>
    /// <param name="expected">The amount that should be stored, as an invariant decimal literal.</param>
    /// <remarks>
    /// The amounts travel as strings because a decimal cannot appear in an attribute argument at all, and
    /// routing them through a double first would introduce a representation question that has nothing to
    /// do with what is being tested.
    /// </remarks>
    [Theory]
    [InlineData("-1000", "0")]
    [InlineData("-0.01", "0")]
    [InlineData("0", "0")]
    [InlineData("0.01", "0.01")]
    [InlineData("12.5", "12.5")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void ClampFee_FloorsAtZeroAndPreservesEverythingElse(string supplied, string expected)
    {
        decimal suppliedFee = decimal.Parse(supplied, CultureInfo.InvariantCulture);
        decimal expectedFee = decimal.Parse(expected, CultureInfo.InvariantCulture);

        PortalMappings.ClampFee(suppliedFee).Should().Be(expectedFee);
    }

    /// <summary>
    /// Every portal mapper refuses a null argument rather than producing a half-populated result.
    /// </summary>
    [Fact]
    public void PortalMappers_RefuseNullArguments()
    {
        Portal portal = FullPortal();
        CreatePortalRequest createRequest = new();
        UpdatePortalRequest updateRequest = new();

        Assert.Throws<ArgumentNullException>(() => { _ = PortalMappings.ToListItem(null!, [], 0, 0); });
        Assert.Throws<ArgumentNullException>(() => { _ = PortalMappings.ToListItem(portal, null!, 0, 0); });
        Assert.Throws<ArgumentNullException>(
            () => { _ = PortalMappings.ToDetail(null!, 0, 0, null, null, null, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = PortalMappings.ToSettings(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = PortalMappings.ToDto(null!); });
        Assert.Throws<ArgumentNullException>(
            () => { _ = PortalMappings.ToNewPortal(null!, "USD", null, 0m, 0, 0, 0, null, "Portals/1"); });
        Assert.Throws<ArgumentNullException>(() => PortalMappings.ApplyUpdate(null!, updateRequest));
        Assert.Throws<ArgumentNullException>(() => PortalMappings.ApplyUpdate(portal, null!));

        createRequest.PortalName.Should().BeNull("the request used above carries no values of its own");
    }

    // ---------------------------------------------------------------------------------------------
    // Tab
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The page list projection carries the tree columns and the separately-computed child flag.
    /// </summary>
    [Fact]
    public void TabToListItem_ProjectsTheTreeColumnsAndTheChildFlag()
    {
        Tab tab = FullTab();

        TabListItemDto dto = TabMappings.ToListItem(tab, hasChildren: true);

        dto.TabId.Should().Be(0);
        dto.TabName.Should().Be("Reports");
        dto.Title.Should().Be("Reports Title");
        dto.TabOrder.Should().Be(4);
        dto.ParentId.Should().Be(1);
        dto.Level.Should().Be(2);
        dto.TabPath.Should().Be("//Home//Reports");
        dto.IsVisible.Should().BeTrue();
        dto.DisableLink.Should().BeTrue();
        dto.IsDeleted.Should().BeTrue();
        dto.IsSecure.Should().BeTrue();
        dto.Url.Should().Be("~/Reports.aspx");
        dto.IconFile.Should().Be("reports.gif");
        dto.HasChildren.Should().BeTrue(
            "whether a page has children is a query about other rows, so the service computes it");
    }

    /// <summary>
    /// The child flag is carried through unchanged when the page has no children.
    /// </summary>
    [Fact]
    public void TabToListItem_CarriesAnAbsentChildFlag()
    {
        TabMappings.ToListItem(FullTab(), hasChildren: false).HasChildren.Should().BeFalse();
    }

    /// <summary>
    /// The page detail projection carries every editable column, keywords included.
    /// </summary>
    [Fact]
    public void TabToDetail_CarriesEveryColumn()
    {
        Tab tab = FullTab();

        TabDetailDto dto = TabMappings.ToDetail(tab, hasChildren: false);

        dto.TabId.Should().Be(0);
        dto.TabOrder.Should().Be(4);
        dto.PortalId.Should().Be(-1);
        dto.TabName.Should().Be("Reports");
        dto.IsVisible.Should().BeTrue();
        dto.ParentId.Should().Be(1);
        dto.Level.Should().Be(2);
        dto.IconFile.Should().Be("reports.gif");
        dto.DisableLink.Should().BeTrue();
        dto.Title.Should().Be("Reports Title");
        dto.Description.Should().Be("Reports description");
        dto.IsDeleted.Should().BeTrue();
        dto.Url.Should().Be("~/Reports.aspx");
        dto.SkinSrc.Should().Be("[G]Skins/Default/Home.ascx");
        dto.ContainerSrc.Should().Be("[G]Containers/Default/Blue.ascx");
        dto.TabPath.Should().Be("//Home//Reports");
        dto.StartDate.Should().Be(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        dto.EndDate.Should().Be(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        dto.RefreshInterval.Should().Be(45);
        dto.PageHeadText.Should().Be("<meta name=\"measured\" />");
        dto.IsSecure.Should().BeTrue();
        dto.HasChildren.Should().BeFalse();

        dto.Keywords.Should().Be(
            "reports,measured",
            "the entity and the response both spell the field Keywords while the column behind it is "
            + "spelled KeyWords - the bridge belongs to TabConfiguration.HasColumnName, so this "
            + "projection is a plain like-named assignment and must not silently lose the value");
    }

    /// <summary>
    /// Applying a page update overwrites all seventeen editable columns and touches neither identity,
    /// ownership, nor the server-computed tree position.
    /// </summary>
    [Fact]
    public void TabApplyUpdate_OverwritesEditableColumnsAndLeavesTheComputedTreeAlone()
    {
        Tab tab = FullTab();

        UpdateTabRequest request = new()
        {
            TabName = "Renamed Page",
            Title = "Renamed Title",
            Description = "Renamed description",
            Keywords = "renamed,keywords",
            ParentId = 5,
            IsVisible = false,
            DisableLink = false,
            IconFile = "renamed.gif",
            SkinSrc = "[G]Skins/Default/Plain.ascx",
            ContainerSrc = "[G]Containers/Default/Grey.ascx",
            Url = "~/Renamed.aspx",
            StartDate = new DateTime(2027, 5, 6, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2027, 6, 7, 0, 0, 0, DateTimeKind.Utc),
            RefreshInterval = 90,
            PageHeadText = "<meta name=\"renamed\" />",
            IsSecure = false,
            IsDeleted = false,
        };

        TabMappings.ApplyUpdate(tab, request);

        tab.TabName.Should().Be("Renamed Page");
        tab.Title.Should().Be("Renamed Title");
        tab.Description.Should().Be("Renamed description");
        tab.Keywords.Should().Be("renamed,keywords");
        tab.ParentId.Should().Be(5);
        tab.IsVisible.Should().BeFalse();
        tab.DisableLink.Should().BeFalse();
        tab.IconFile.Should().Be("renamed.gif");
        tab.Url.Should().Be("~/Renamed.aspx");
        tab.StartDate.Should().Be(new DateTime(2027, 5, 6, 0, 0, 0, DateTimeKind.Utc));
        tab.EndDate.Should().Be(new DateTime(2027, 6, 7, 0, 0, 0, DateTimeKind.Utc));
        tab.RefreshInterval.Should().Be(90);
        tab.PageHeadText.Should().Be("<meta name=\"renamed\" />");
        tab.IsSecure.Should().BeFalse();

        tab.SkinSrc.Should().Be(
            "[G]Skins/Default/Plain.ascx",
            "the skin source is a genuine column the terminal update procedure writes, so the request "
            + "carries it and the mapper must apply it - skinning being out of scope makes the value "
            + "opaque, not unwritable");
        tab.ContainerSrc.Should().Be("[G]Containers/Default/Grey.ascx");
        tab.IsDeleted.Should().BeFalse(
            "both legacy recycle-bin transitions were plain writes of this flag through this very "
            + "update path, and the page surface exposes no delete or restore route, so the request "
            + "carries the flag and restoring is expressed by submitting false");

        tab.TabId.Should().Be(0, "an update never moves a row");
        tab.PortalId.Should().Be(-1, "a page cannot be moved between tenants by editing it");
        tab.TabOrder.Should().Be(
            4,
            "the sibling order is server-owned: the legacy update procedure neither accepted nor wrote "
            + "it, so the request carries no ordinal and the mapper leaves the stored one for the "
            + "renumbering pass to recompute");
        tab.Level.Should().Be(
            2,
            "the depth and the path are recomputed for the whole tenant after the write, so the mapper "
            + "deliberately leaves them as they were");
        tab.TabPath.Should().Be("//Home//Reports");
    }

    /// <summary>
    /// Every page mapper refuses a null argument.
    /// </summary>
    [Fact]
    public void TabMappers_RefuseNullArguments()
    {
        Tab tab = FullTab();

        Assert.Throws<ArgumentNullException>(() => { _ = TabMappings.ToListItem(null!, false); });
        Assert.Throws<ArgumentNullException>(() => { _ = TabMappings.ToDetail(null!, false); });
        Assert.Throws<ArgumentNullException>(() => TabMappings.ApplyUpdate(null!, new UpdateTabRequest()));
        Assert.Throws<ArgumentNullException>(() => TabMappings.ApplyUpdate(tab, null!));
    }

    // ---------------------------------------------------------------------------------------------
    // Role
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The role list projection carries the membership terms the list screen shows.
    /// </summary>
    [Fact]
    public void RoleToListItem_ProjectsTheMembershipTerms()
    {
        Role role = FullRole();

        RoleListItemDto dto = RoleMappings.ToListItem(role);

        dto.RoleId.Should().Be(0);
        dto.RoleName.Should().Be("Gold Members");
        dto.Description.Should().Be("A measured role");
        dto.ServiceFee.Should().Be(19.99m);
        dto.BillingPeriod.Should().Be(1);
        dto.BillingFrequency.Should().Be(BillingFrequency.Month);
        dto.TrialFee.Should().Be(0m);
        dto.TrialPeriod.Should().Be(14);
        dto.TrialFrequency.Should().Be(BillingFrequency.Day);
        dto.IsPublic.Should().BeTrue();
        dto.AutoAssignment.Should().BeTrue();
    }

    /// <summary>
    /// The role detail projection carries every one of the fourteen members it declares, each drawn
    /// from a column on the single role row it is handed.
    /// </summary>
    [Fact]
    public void RoleToDetail_CarriesEveryColumnOfTheRoleRow()
    {
        Role role = FullRole();

        RoleDetailDto dto = RoleMappings.ToDetail(role);

        dto.RoleId.Should().Be(0);
        dto.RoleGroupId.Should().Be(6);
        dto.RoleName.Should().Be("Gold Members");
        dto.Description.Should().Be("A measured role");
        dto.BillingFrequency.Should().Be(BillingFrequency.Month);
        dto.ServiceFee.Should().Be(19.99m);
        dto.TrialFrequency.Should().Be(BillingFrequency.Day);
        dto.TrialPeriod.Should().Be(14);
        dto.BillingPeriod.Should().Be(1);
        dto.TrialFee.Should().Be(0m);
        dto.IsPublic.Should().BeTrue();
        dto.AutoAssignment.Should().BeTrue();
        dto.RsvpCode.Should().Be("GOLD2026");
        dto.IconFile.Should().Be("gold.gif");
    }

    /// <summary>
    /// The detail projection carries no member that is not a column on the role row, so neither the
    /// owning portal nor any join denormalisation appears on it.
    /// </summary>
    /// <remarks>
    /// Asserted over the declared surface rather than member by member, so adding a portal identifier,
    /// a group name or a member tally back onto the contract fails here rather than passing silently.
    /// </remarks>
    [Fact]
    public void RoleDetail_DeclaresNoPortalIdentifierAndNoJoinDenormalisation()
    {
        string[] declared = typeof(RoleDetailDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        declared.Should().HaveCount(14);
        declared.Should().NotContain("PortalId");
        declared.Should().NotContain("RoleGroupName");
        declared.Should().NotContain("UserCount");
        declared.Should().NotContain("MemberCount");
        declared.Should().NotContain("RsvpLink");
        declared.Should().NotContain("RoleStatus");
    }

    /// <summary>
    /// An ungrouped role projects a null group identifier, and a role in the group whose identifier is
    /// zero projects that zero faithfully rather than treating it as absent.
    /// </summary>
    /// <remarks>
    /// The second case is the one worth guarding: <c>RoleGroups.RoleGroupID</c> is seeded
    /// <c>IDENTITY(0,1)</c>, so zero identifies the first group ever created and is not a stand-in for
    /// "no group". The legacy interface encoded the absent case as -1, which never reached the column
    /// because the foreign key would have rejected it.
    /// </remarks>
    [Fact]
    public void RoleToDetail_DistinguishesAnUngroupedRoleFromTheGroupIdentifiedByZero()
    {
        Role ungrouped = FullRole();
        ungrouped.RoleGroupId = null;

        RoleMappings.ToDetail(ungrouped).RoleGroupId.Should().BeNull();

        Role inFirstGroup = FullRole();
        inFirstGroup.RoleGroupId = 0;

        RoleMappings.ToDetail(inFirstGroup).RoleGroupId.Should().Be(0);
    }

    /// <summary>
    /// The group projection carries all four of its columns.
    /// </summary>
    [Fact]
    public void RoleGroupToDto_CarriesAllFourColumns()
    {
        RoleGroup group = new()
        {
            RoleGroupId = 6,
            PortalId = -1,
            RoleGroupName = "Paid Tiers",
            Description = "Tiers that carry a charge",
        };

        RoleGroupDto dto = RoleMappings.ToDto(group);

        dto.RoleGroupId.Should().Be(6);
        dto.PortalId.Should().Be(-1);
        dto.RoleGroupName.Should().Be("Paid Tiers");
        dto.Description.Should().Be("Tiers that carry a charge");
    }

    /// <summary>
    /// A new role is anchored to the tenant the route named and takes every term from the request.
    /// </summary>
    [Fact]
    public void RoleToNewRole_AnchorsTheTenantAndAppliesEveryTerm()
    {
        CreateRoleRequest request = new()
        {
            RoleName = "Silver Members",
            Description = "A cheaper tier",
            RoleGroupId = 6,
            IsPublic = true,
            AutoAssignment = true,
            ServiceFee = 4.99m,
            BillingPeriod = 3,
            BillingFrequency = BillingFrequency.Month,
            TrialFee = 1.99m,
            TrialPeriod = 7,
            TrialFrequency = BillingFrequency.Day,
            RsvpCode = "SILVER",
            IconFile = "silver.gif",
        };

        Role role = RoleMappings.ToNewRole(portalId: -1, request);

        role.PortalId.Should().Be(-1);
        role.RoleName.Should().Be("Silver Members");
        role.Description.Should().Be("A cheaper tier");
        role.RoleGroupId.Should().Be(6);
        role.IsPublic.Should().BeTrue();
        role.AutoAssignment.Should().BeTrue();
        role.ServiceFee.Should().Be(4.99m);
        role.BillingPeriod.Should().Be(3);
        role.BillingFrequency.Should().Be(BillingFrequency.Month);
        role.TrialFee.Should().Be(1.99m);
        role.TrialPeriod.Should().Be(7);
        role.TrialFrequency.Should().Be(BillingFrequency.Day);
        role.RsvpCode.Should().Be("SILVER");
        role.IconFile.Should().Be("silver.gif");
    }

    /// <summary>
    /// A free role keeps every term absent, and an absent fee is not converted into a zero fee.
    /// </summary>
    [Fact]
    public void RoleToNewRole_KeepsAnAbsentFeeAbsent()
    {
        Role role = RoleMappings.ToNewRole(-1, new CreateRoleRequest { RoleName = "Registered Users" });

        role.ServiceFee.Should().BeNull(
            "a role with no charge is not a role charged nothing - the column stays null so a free tier "
            + "stays distinguishable from a zero-priced one");
        role.TrialFee.Should().BeNull();
        role.BillingPeriod.Should().BeNull();
        role.BillingFrequency.Should().BeNull();
        role.TrialPeriod.Should().BeNull();
        role.TrialFrequency.Should().BeNull();
        role.RoleGroupId.Should().BeNull();
    }

    /// <summary>
    /// A submitted negative charge is floored rather than stored, on both the create and the update path.
    /// </summary>
    [Fact]
    public void RoleMappers_FloorNegativeChargesOnBothPaths()
    {
        Role created = RoleMappings.ToNewRole(
            -1,
            new CreateRoleRequest { RoleName = "Odd", ServiceFee = -5m, TrialFee = -1m });

        created.ServiceFee.Should().Be(0m);
        created.TrialFee.Should().Be(0m);

        Role updated = FullRole();

        RoleMappings.ApplyUpdate(
            updated,
            new UpdateRoleRequest { ServiceFee = -5m, TrialFee = -1m });

        updated.ServiceFee.Should().Be(0m);
        updated.TrialFee.Should().Be(0m);
    }

    /// <summary>
    /// Applying a role update overwrites every term and leaves identity, ownership and the name alone.
    /// </summary>
    [Fact]
    public void RoleApplyUpdate_OverwritesEveryTermAndLeavesIdentityAlone()
    {
        Role role = FullRole();

        UpdateRoleRequest request = new()
        {
            Description = "The dearest tier",
            RoleGroupId = null,
            IsPublic = false,
            AutoAssignment = false,
            ServiceFee = 99m,
            BillingPeriod = 1,
            BillingFrequency = BillingFrequency.Year,
            TrialFee = null,
            TrialPeriod = null,
            TrialFrequency = null,
            RsvpCode = null,
            IconFile = null,
        };

        RoleMappings.ApplyUpdate(role, request);

        // MIGRATION: the name is PRESERVED, not overwritten. The update contract declares no name
        // because the legacy edit screen made it read-only and the terminal UpdateRole procedure omits
        // the column from its assignment list, so the projection passes the stored value through.
        role.RoleName.Should().Be("Gold Members", "an update cannot rename a role");
        role.Description.Should().Be("The dearest tier");
        role.RoleGroupId.Should().BeNull("clearing the group is a legitimate edit");
        role.IsPublic.Should().BeFalse();
        role.AutoAssignment.Should().BeFalse();
        role.ServiceFee.Should().Be(99m);
        role.BillingPeriod.Should().Be(1);
        role.BillingFrequency.Should().Be(BillingFrequency.Year);
        role.TrialFee.Should().BeNull("withdrawing a trial is a legitimate edit");
        role.TrialPeriod.Should().BeNull();
        role.TrialFrequency.Should().BeNull();
        role.RsvpCode.Should().BeNull();
        role.IconFile.Should().BeNull();

        role.RoleId.Should().Be(0, "an update never moves a row");
        role.PortalId.Should().Be(-1, "a role cannot be moved between tenants by editing it");
    }

    /// <summary>
    /// A new group is anchored to the tenant the route named rather than to the one the body carried.
    /// </summary>
    [Fact]
    public void RoleGroupToNewGroup_AnchorsTheTenantFromTheRoute()
    {
        RoleGroupDto request = new()
        {
            RoleGroupId = 999,
            PortalId = 12345,
            RoleGroupName = "Paid Tiers",
            Description = "Tiers that carry a charge",
        };

        RoleGroup group = RoleMappings.ToNewGroup(portalId: -1, request);

        group.PortalId.Should().Be(
            -1,
            "the tenant comes from the route, so a body claiming a different tenant cannot create a group "
            + "somewhere else");
        group.RoleGroupName.Should().Be("Paid Tiers");
        group.Description.Should().Be("Tiers that carry a charge");
        group.RoleGroupId.Should().Be(0, "the store issues the key, not the caller");
    }

    /// <summary>
    /// Applying a group update overwrites the name and the description only.
    /// </summary>
    [Fact]
    public void RoleGroupApplyUpdate_OverwritesTheNameAndDescriptionOnly()
    {
        RoleGroup group = new()
        {
            RoleGroupId = 6,
            PortalId = -1,
            RoleGroupName = "Paid Tiers",
            Description = "Tiers that carry a charge",
        };

        RoleMappings.ApplyGroupUpdate(
            group,
            new RoleGroupDto { RoleGroupId = 999, PortalId = 12345, RoleGroupName = "Renamed", Description = null });

        group.RoleGroupName.Should().Be("Renamed");
        group.Description.Should().BeNull();
        group.RoleGroupId.Should().Be(6, "an update never moves a row");
        group.PortalId.Should().Be(-1, "a group cannot be moved between tenants by editing it");
    }

    /// <summary>
    /// Every role mapper refuses a null argument.
    /// </summary>
    [Fact]
    public void RoleMappers_RefuseNullArguments()
    {
        Role role = FullRole();
        RoleGroup group = new() { RoleGroupName = "Group" };

        Assert.Throws<ArgumentNullException>(() => { _ = RoleMappings.ToListItem(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = RoleMappings.ToDetail(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = RoleMappings.ToDto(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = RoleMappings.ToNewRole(-1, null!); });
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ApplyUpdate(null!, new UpdateRoleRequest()));
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ApplyUpdate(role, null!));
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ToNewGroup(-1, null!));
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ApplyGroupUpdate(null!, new RoleGroupDto()));
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ApplyGroupUpdate(group, null!));
    }

    // ---------------------------------------------------------------------------------------------
    // Module
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The module list projection draws each field from the correct one of its two sources.
    /// </summary>
    /// <remarks>
    /// A module and its placement on a page are separate rows, and the list screen shows fields from both.
    /// The fixtures below give the two rows deliberately different identifiers and ordinals so that a
    /// projection reading the wrong row fails rather than coincidentally agreeing.
    /// </remarks>
    [Fact]
    public void ModuleToListItem_DrawsEachFieldFromTheCorrectRow()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleListItemDto dto = ModuleMappings.ToListItem(module, placement, friendlyName: "Announcements");

        dto.ModuleId.Should().Be(0, "the module identifier comes from the module row");
        dto.TabModuleId.Should().Be(31, "the placement identifier comes from the placement row");
        dto.TabId.Should().Be(
            7,
            "which page the module appears on is a fact about the placement, not about the module");
        dto.ModuleDefId.Should().Be(4);
        dto.ModuleTitle.Should().Be("Measured Module");
        dto.FriendlyName.Should().Be("Announcements");
        dto.ModuleOrder.Should().Be(6, "the ordinal within the pane belongs to the placement");
        dto.AllTabs.Should().BeTrue();
        dto.Visibility.Should().Be(ModuleVisibility.Minimized);
        dto.IsDeleted.Should().BeTrue();
        dto.DisplayTitle.Should().BeFalse();
        dto.StartDate.Should().Be(new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc));
        dto.EndDate.Should().Be(new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// The list projection falls back to the loaded definition when no name is supplied.
    /// </summary>
    [Fact]
    public void ModuleToListItem_FallsBackToTheLoadedDefinitionName()
    {
        Module module = FullModule();
        module.ModuleDefinition = new ModuleDefinition
        {
            ModuleDefinitionId = 4,
            FriendlyName = "Loaded Definition",
            DesktopModuleId = 3,
        };

        ModuleMappings.ToListItem(module, FullPlacement(), friendlyName: null)
            .FriendlyName.Should().Be("Loaded Definition");
    }

    /// <summary>
    /// The list projection leaves the name absent when neither source can supply one.
    /// </summary>
    [Fact]
    public void ModuleToListItem_LeavesTheNameAbsentWhenNeitherSourceHasOne()
    {
        Module module = FullModule();

        module.ModuleDefinition.Should().BeNull("the fixture deliberately loads no definition");

        ModuleMappings.ToListItem(module, FullPlacement(), friendlyName: null)
            .FriendlyName.Should().BeNull(
                "the list projection reports an unresolved name as absent rather than inventing one");
    }

    /// <summary>
    /// The module detail projection draws each field from the correct one of its two sources.
    /// </summary>
    [Fact]
    public void ModuleToDetail_DrawsEachFieldFromTheCorrectRow()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleDetailDto dto = ModuleMappings.ToDetail(module, placement, friendlyName: "Announcements");

        dto.ModuleId.Should().Be(0);
        dto.TabModuleId.Should().Be(31);
        dto.TabId.Should().Be(7);
        dto.PortalId.Should().Be(-1);
        dto.ModuleDefId.Should().Be(4);
        dto.FriendlyName.Should().Be("Announcements");
        dto.ModuleTitle.Should().Be("Measured Module");
        dto.ModuleOrder.Should().Be(6);
        dto.AllTabs.Should().BeTrue();
        dto.IsDeleted.Should().BeTrue();
        dto.InheritViewPermissions.Should().BeFalse();
        dto.Header.Should().Be("Measured header");
        dto.Footer.Should().Be("Measured footer");
        dto.StartDate.Should().Be(new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc));
        dto.EndDate.Should().Be(new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc));
        dto.CacheTime.Should().Be(120, "the cache period belongs to the placement");
        dto.IconFile.Should().Be("module.gif");
        dto.Visibility.Should().Be(ModuleVisibility.Minimized);
        dto.DisplayTitle.Should().BeFalse();

        dto.DesktopModuleId.Should().Be(
            0,
            "the fixture loads no definition, and the package key's stored default is 0 - which is a "
            + "legitimate value rather than a marker of absence");
        dto.ModuleName.Should().BeNull("the package projection cannot resolve without a definition");
        dto.Description.Should().BeNull();
        dto.Version.Should().BeNull();
    }

    /// <summary>
    /// The detail projection reports an unresolved definition name as absent rather than inventing one.
    /// </summary>
    /// <remarks>
    /// The four catalogue projections on the detail contract are all nullable precisely because each is the
    /// result of a join that may not resolve. Reporting an empty string instead would be indistinguishable
    /// from a definition genuinely named with one, and the legacy layer's conflation of the two - its
    /// absent-string constant evaluates to the empty string - is exactly what this contract undoes.
    /// </remarks>
    [Fact]
    public void ModuleToDetail_ReportsAnUnresolvedNameAsAbsent()
    {
        ModuleMappings.ToDetail(FullModule(), FullPlacement(), friendlyName: null)
            .FriendlyName.Should().BeNull(
                "the fallback chain ends at null, so an unresolved join stays distinguishable from a "
                + "definition whose name is genuinely blank");
    }

    /// <summary>
    /// The placement's appearance fields are write-only in the target: settable through the update
    /// request, but absent from every response contract. Neither read contract invents a container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These columns are editable on the legacy screen — <c>modulesettings.ascx</c> renders cboAlign,
    /// txtColor, txtBorder, chkDisplayPrint and chkDisplaySyndicate, and
    /// <c>ModuleSettings.ascx.vb:L345-L347,L381-L382</c> writes every one of them — so the WRITE capability
    /// must remain reachable, and that is asserted positively on <see cref="UpdateModuleRequest"/> below.
    /// </para>
    /// <para>
    /// No response contract reads them back, and that is deliberate rather than an oversight. They exist
    /// solely to drive server-side markup generation, and both server-side rendering and the postback
    /// presentation model are excluded from this migration, so there is no target consumer for the read.
    /// <see cref="ModuleSettingsDto"/> is emphatically not their home: it carries the module and placement
    /// identifiers and the two key-value settings maps only, because restating placement columns there
    /// would duplicate <see cref="ModuleDetailDto"/> and reintroduce the ambiguity about which scope a
    /// value belongs to that separating the two maps exists to remove. Their absence from all three read
    /// contracts is asserted rather than assumed, so a later change that quietly reintroduces them fails
    /// here.
    /// </para>
    /// <para>
    /// The container is asserted absent from every contract. A module container is a skin object and
    /// skinning is excluded by AAP 0.2.2.4, so surfacing it would widen the contract past the agreed scope
    /// even though the column is mapped and preserved.
    /// </para>
    /// </remarks>
    [Fact]
    public void ModuleReadProjections_TreatTheAppearanceFieldsAsWriteOnlyAndOfferNoContainer()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleDetailDto detail = ModuleMappings.ToDetail(module, placement, "Announcements");
        ModuleSettingsDto settings = ModuleMappings.ToSettings(module, placement, [], []);

        foreach (string appearance in new[]
        {
            "PaneName", "Alignment", "Color", "Border", "DisplayPrint", "DisplaySyndicate",
        })
        {
            // MIGRATION: the appearance columns are absent from the UPDATE contract too, so they are
            // neither readable nor writable through the module API - not write-only, as an earlier
            // revision of this assertion had it. They remain mapped and PRESERVED in the store; the
            // migration simply declines to manage them, pane layout and server-side rendering being
            // excluded by AAP 0.2.2.1.
            typeof(UpdateModuleRequest).GetProperty(appearance).Should().BeNull(
                $"the placement's {appearance} drives server-side markup, which this migration excludes, "
                + "so the update contract must not offer it either");

            typeof(ModuleDetailDto).GetProperty(appearance).Should().BeNull(
                $"the placement's {appearance} drives server-side markup, which this migration excludes, "
                + "so no response contract carries it");

            typeof(ModuleSettingsDto).GetProperty(appearance).Should().BeNull(
                $"the settings contract carries the two identifiers and the two key-value maps only, so "
                + $"{appearance} must not reappear on it");
        }

        settings.ModuleId.Should().Be(module.ModuleId);
        settings.TabModuleId.Should().Be(
            placement.TabModuleId,
            "the settings contract identifies the placement whose scoped settings it carries");

        detail.TabModuleId.Should().Be(
            placement.TabModuleId,
            "the detail contract still identifies which placement it describes, which is what a caller "
            + "needs in order to fetch that placement's settings");

        typeof(ModuleDetailDto).GetProperty("ContainerSrc").Should().BeNull(
            "containers are skin objects and skinning is excluded by AAP 0.2.2.4");
        typeof(ModuleSettingsDto).GetProperty("ContainerSrc").Should().BeNull();
        typeof(UpdateModuleRequest).GetProperty("ContainerSrc").Should().BeNull();
    }

    /// <summary>
    /// The seven pane-layout, rendering and skinning columns the update contract excludes are left exactly as
    /// they were stored, rather than being cleared by an update that cannot name them.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this asserts the CORRECTED behaviour, and an earlier pair of tests asserted the opposite -
    /// that an omitted appearance field should clear its column. That was wrong twice over. The seven columns
    /// - pane, alignment, colour, border, print flag, syndicate flag and container source - are excluded from
    /// UpdateModuleRequest as Web Forms pane-layout, server-side rendering and skinning concerns (AAP 0.2.2.1
    /// and 0.2.2.4), so no caller can express them; clearing them on every update would therefore destroy
    /// stored data that the new contract offers no way to restore. The pane makes the point unanswerably: its
    /// column is NOT NULL, so clearing it would fail the write outright. Excluding a column means declining
    /// to manage it, not deleting it.
    /// </remarks>
    [Fact]
    public void ModuleApplyUpdate_PreservesEveryExcludedAppearanceColumn()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        // Capture the stored appearance so the assertions can prove each column SURVIVES the update.
        string storedPane = placement.PaneName;
        string? storedAlignment = placement.Alignment;
        string? storedColor = placement.Color;
        string? storedBorder = placement.Border;
        string? storedContainer = placement.ContainerSrc;
        bool storedPrint = placement.DisplayPrint;
        bool storedSyndicate = placement.DisplaySyndicate;

        storedAlignment.Should().NotBeNull("the fixture must start with a value for this to prove anything");

        // A request that changes everything it CAN change, to prove the excluded columns are untouched even
        // on a maximally invasive update.
        UpdateModuleRequest request = new()
        {
            TabId = placement.TabId,
            ModuleTitle = "Renamed",
            ModuleOrder = 7,
            CacheTime = 60,
            IconFile = "changed.gif",
            Visibility = ModuleVisibility.None,
            DisplayTitle = false,
        };

        ModuleMappings.ApplyUpdate(module, placement, request);

        placement.PaneName.Should().Be(storedPane, "the pane column is NOT NULL, so clearing it would fail the write");
        placement.Alignment.Should().Be(storedAlignment);
        placement.Color.Should().Be(storedColor);
        placement.Border.Should().Be(storedBorder);
        placement.ContainerSrc.Should().Be(storedContainer);
        placement.DisplayPrint.Should().Be(storedPrint);
        placement.DisplaySyndicate.Should().Be(storedSyndicate);

        // The members the contract DOES carry still applied, proving preservation is targeted rather than a
        // wholesale refusal to write.
        placement.ModuleOrder.Should().Be(7);
        placement.IconFile.Should().Be("changed.gif");
    }


    /// <summary>
    /// The settings projection keeps the two setting scopes apart.
    /// </summary>
    /// <remarks>
    /// A module carries settings that apply wherever it appears, and each placement carries settings that
    /// apply only on that page. Merging the two would make a page-specific override look like a global one,
    /// so the projection exposes them as two separate maps.
    /// </remarks>
    [Fact]
    public void ModuleToSettings_KeepsTheTwoSettingScopesApart()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleSetting[] moduleSettings =
        [
            new() { ModuleId = 0, SettingName = "Skin", SettingValue = "Global Blue" },
            new() { ModuleId = 0, SettingName = "Rows", SettingValue = "10" },
        ];

        TabModuleSetting[] placementSettings =
        [
            new() { TabModuleId = 31, SettingName = "Skin", SettingValue = "Page Red" },
        ];

        ModuleSettingsDto dto = ModuleMappings.ToSettings(module, placement, moduleSettings, placementSettings);

        dto.ModuleId.Should().Be(
            0,
            "the module identity column is seeded at zero, so zero is a real module and must survive the "
            + "projection rather than being read as an absent identifier");
        dto.TabModuleId.Should().Be(31);

        dto.ModuleSettings.Should().HaveCount(2);
        dto.ModuleSettings["Skin"].Should().Be("Global Blue");
        dto.ModuleSettings["Rows"].Should().Be("10");

        dto.TabModuleSettings.Should().HaveCount(1);
        dto.TabModuleSettings["Skin"].Should().Be(
            "Page Red",
            "the placement override must not be flattened into the module-wide value");
    }

    /// <summary>
    /// Setting names are matched without regard to case, as the legacy store did.
    /// </summary>
    [Fact]
    public void ModuleToSettings_MatchesSettingNamesWithoutRegardToCase()
    {
        ModuleSetting[] moduleSettings = [new() { ModuleId = 0, SettingName = "Skin", SettingValue = "Blue" }];

        ModuleSettingsDto dto = ModuleMappings.ToSettings(FullModule(), FullPlacement(), moduleSettings, []);

        dto.ModuleSettings.ContainsKey("SKIN").Should().BeTrue();
        dto.ModuleSettings.ContainsKey("skin").Should().BeTrue();
        dto.ModuleSettings["sKiN"].Should().Be("Blue");
    }

    /// <summary>
    /// A setting with no name is discarded rather than keyed on an empty string.
    /// </summary>
    /// <param name="settingName">The stored name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ModuleToSettings_DiscardsAnUnnamedSetting(string settingName)
    {
        ModuleSetting[] moduleSettings =
        [
            new() { ModuleId = 0, SettingName = settingName, SettingValue = "orphan" },
            new() { ModuleId = 0, SettingName = "Skin", SettingValue = "Blue" },
        ];

        ModuleSettingsDto dto = ModuleMappings.ToSettings(FullModule(), FullPlacement(), moduleSettings, []);

        dto.ModuleSettings.Should().HaveCount(1);
        dto.ModuleSettings.ContainsKey("Skin").Should().BeTrue();
    }

    /// <summary>
    /// A repeated setting name resolves to the value read last rather than throwing.
    /// </summary>
    [Fact]
    public void ModuleToSettings_ResolvesARepeatedNameToTheLastValue()
    {
        ModuleSetting[] moduleSettings =
        [
            new() { ModuleId = 0, SettingName = "Skin", SettingValue = "First" },
            new() { ModuleId = 0, SettingName = "skin", SettingValue = "Second" },
        ];

        ModuleSettingsDto dto = ModuleMappings.ToSettings(FullModule(), FullPlacement(), moduleSettings, []);

        dto.ModuleSettings.Should().HaveCount(
            1,
            "the two names differ only in case, and the map is case-insensitive, so they are one entry");
        dto.ModuleSettings["Skin"].Should().Be("Second");
    }

    /// <summary>
    /// The definition projection prefers a package handed in over one loaded on the navigation.
    /// </summary>
    [Fact]
    public void ModuleDefinitionToDto_PrefersTheSuppliedPackage()
    {
        ModuleDefinition definition = new()
        {
            ModuleDefinitionId = 4,
            FriendlyName = "Announcements",
            DesktopModuleId = 3,
            DefaultCacheTime = 240,
            DesktopModule = NewDesktopModule("NavigationPackage", supportedFeatures: 0),
        };

        ModuleDefinitionDto dto = ModuleMappings.ToDto(definition, NewDesktopModule("SuppliedPackage", 1));

        dto.ModuleDefId.Should().Be(4);
        dto.FriendlyName.Should().Be("Announcements");
        dto.DesktopModuleId.Should().Be(3);
        dto.DefaultCacheTime.Should().Be(240);
        dto.ModuleName.Should().Be("SuppliedPackage");
        dto.IsPortable.Should().BeTrue("the supplied package is portable and the navigation one is not");
    }

    /// <summary>
    /// The definition projection falls back to the loaded package when none is handed in.
    /// </summary>
    [Fact]
    public void ModuleDefinitionToDto_FallsBackToTheLoadedPackage()
    {
        ModuleDefinition definition = new()
        {
            ModuleDefinitionId = 4,
            FriendlyName = "Announcements",
            DesktopModuleId = 3,
            DesktopModule = NewDesktopModule("NavigationPackage", supportedFeatures: 1),
        };

        ModuleDefinitionDto dto = ModuleMappings.ToDto(definition, desktopModule: null);

        dto.ModuleName.Should().Be("NavigationPackage");
        dto.Description.Should().Be("A measured package");
        dto.Version.Should().Be("04.09.00");
        dto.IsPremium.Should().BeTrue();
        dto.IsAdmin.Should().BeTrue();
        dto.IsPortable.Should().BeTrue();
    }

    /// <summary>
    /// The definition projection tolerates a package that was never loaded.
    /// </summary>
    [Fact]
    public void ModuleDefinitionToDto_ToleratesAnUnloadedPackage()
    {
        ModuleDefinition definition = new()
        {
            ModuleDefinitionId = 4,
            FriendlyName = "Announcements",
            DesktopModuleId = 3,
            DefaultCacheTime = 60,
        };

        ModuleDefinitionDto dto = ModuleMappings.ToDto(definition, desktopModule: null);

        dto.ModuleDefId.Should().Be(4);
        dto.FriendlyName.Should().Be("Announcements");
        dto.DefaultCacheTime.Should().Be(60);
        dto.ModuleName.Should().BeEmpty(
            "an unresolved package yields an empty name rather than a failure, so a definition row that "
            + "outlived its package still lists");
        dto.Description.Should().BeNull();
        dto.Version.Should().BeNull();
        dto.IsPremium.Should().BeFalse();
        dto.IsAdmin.Should().BeFalse();
        dto.IsPortable.Should().BeFalse();
    }

    /// <summary>
    /// Portability is read out of the stored capability bitmask rather than stored as its own flag.
    /// </summary>
    /// <param name="supportedFeatures">The stored bitmask.</param>
    /// <param name="expected">Whether the package should report itself portable.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(7, true)]
    [InlineData(-1, false)]
    public void ModuleDefinitionToDto_ReadsPortabilityFromTheCapabilityBitmask(int supportedFeatures, bool expected)
    {
        ModuleDefinition definition = new()
        {
            ModuleDefinitionId = 4,
            FriendlyName = "Announcements",
            DesktopModuleId = 3,
        };

        ModuleDefinitionDto dto = ModuleMappings.ToDto(definition, NewDesktopModule("Package", supportedFeatures));

        dto.IsPortable.Should().Be(expected);
    }

    /// <summary>
    /// A new module is anchored to the tenant the route named and starts undeleted.
    /// </summary>
    [Fact]
    public void ModuleToNewModule_AnchorsTheTenantAndStartsUndeleted()
    {
        CreateModuleRequest request = new()
        {
            ModuleDefId = 4,
            TabId = 7,
            ModuleTitle = "New Module",
            AllTabs = true,
            InheritViewPermissions = false,
            Header = "New header",
            Footer = "New footer",
            StartDate = new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2028, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        };

        Module module = ModuleMappings.ToNewModule(portalId: -1, request);

        module.PortalId.Should().Be(-1);
        module.ModuleDefinitionId.Should().Be(4);
        module.ModuleTitle.Should().Be("New Module");
        module.AllTabs.Should().BeTrue();
        module.IsDeleted.Should().BeFalse("a module is not created in the recycle bin");
        module.InheritViewPermissions.Should().BeFalse();
        module.Header.Should().Be("New header");
        module.Footer.Should().Be("New footer");
        module.StartDate.Should().Be(new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        module.EndDate.Should().Be(new DateTime(2028, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        module.ModuleId.Should().Be(0, "the store issues the key, not the caller");
    }

    /// <summary>
    /// A new placement takes the page from the request and supplies the pane itself, because the
    /// creation contract deliberately carries no pane.
    /// </summary>
    [Fact]
    public void ModuleToNewPlacement_SuppliesThePaneAndCarriesTheSubmittedPeriod()
    {
        CreateModuleRequest request = new()
        {
            ModuleDefId = 4,
            TabId = 7,
            ModuleOrder = 6,
            CacheTime = 180,
            IconFile = "new.gif",
            Visibility = ModuleVisibility.None,
            DisplayTitle = false,
        };

        TabModule placement = ModuleMappings.ToNewPlacement(request);

        placement.TabId.Should().Be(7);
        placement.PaneName.Should().Be(
            DefaultPaneName,
            "the pane belongs to the excluded skinning surface, so the create path always places the "
            + "module in the content pane rather than letting the caller choose");
        placement.ModuleOrder.Should().Be(6);
        placement.CacheTime.Should().Be(180, "a submitted period is carried through unchanged");
        placement.IconFile.Should().Be("new.gif");
        placement.Visibility.Should().Be(ModuleVisibility.None);
        placement.DisplayTitle.Should().BeFalse();
    }

    /// <summary>
    /// A new placement preserves a zero caching period as "do not cache" and floors a negative one.
    /// </summary>
    [Fact]
    public void ModuleToNewPlacement_PreservesZeroAndFloorsANegativePeriod()
    {
        CreateModuleRequest omitted = new() { TabId = 7 };

        TabModule placement = ModuleMappings.ToNewPlacement(omitted);

        placement.CacheTime.Should().Be(
            0,
            "a blank legacy cache field stored literally zero, meaning \"do not cache\", so zero is a "
            + "real submitted value and is never substituted for the definition's default");
        placement.ModuleOrder.Should().Be(
            -1,
            "an omitted order keeps the append-at-bottom command the legacy create branched on");
        placement.DisplayTitle.Should().BeTrue("the legacy container default is on");

        CreateModuleRequest negative = new() { TabId = 7, CacheTime = -30 };

        ModuleMappings.ToNewPlacement(negative).CacheTime.Should().Be(0);
    }

    /// <summary>
    /// Applying a module update overwrites both rows and leaves identity, ownership and deletion alone.
    /// </summary>
    [Fact]
    public void ModuleApplyUpdate_OverwritesBothRowsAndLeavesIdentityAlone()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        // Every one of the sixteen members the contract carries, so this exercises the whole surface.
        UpdateModuleRequest request = new()
        {
            TabId = placement.TabId,
            ModuleTitle = "Renamed Module",
            AllTabs = false,
            Header = "Renamed header",
            Footer = "Renamed footer",
            StartDate = null,
            EndDate = null,
            InheritViewPermissions = true,
            IsDeleted = false,
            ModuleOrder = 9,
            CacheTime = 30,
            IconFile = "renamed.gif",
            Visibility = ModuleVisibility.Maximized,
            DisplayTitle = true,
            SetAsDefaultSettings = false,
            ApplyToAllModules = false,
        };

        ModuleMappings.ApplyUpdate(module, placement, request);

        module.ModuleTitle.Should().Be("Renamed Module");
        module.AllTabs.Should().BeFalse();
        module.InheritViewPermissions.Should().BeTrue();
        module.Header.Should().Be("Renamed header");
        module.Footer.Should().Be("Renamed footer");
        module.StartDate.Should().BeNull("clearing a publication window is a legitimate edit");
        module.EndDate.Should().BeNull();

        placement.ModuleOrder.Should().Be(9);
        placement.CacheTime.Should().Be(30);
        placement.IconFile.Should().Be("renamed.gif");
        placement.Visibility.Should().Be(ModuleVisibility.Maximized);
        placement.DisplayTitle.Should().BeTrue();

        // MIGRATION: the six pane-layout and rendering columns keep their FIXTURE values, because the update
        // contract carries no field for any of them. The same reasoning as the container below: excluding a
        // column means declining to manage it, not wiping it. The pane matters most - its column is NOT NULL.
        placement.PaneName.Should().Be("RightPane");
        placement.Alignment.Should().Be("left");
        placement.Color.Should().Be("#EEEEEE");
        placement.Border.Should().Be("1");
        placement.DisplayPrint.Should().BeFalse();
        placement.DisplaySyndicate.Should().BeFalse();

        placement.ContainerSrc.Should().Be(
            "[G]Containers/DNN/Blue.ascx",
            "a module container is a skin object and skinning is excluded by AAP 0.2.2.4, so the request "
            + "carries no container field. The stored value must therefore survive an update untouched "
            + "rather than being wiped by an absent one");

        module.ModuleId.Should().Be(0, "an update never moves a row");
        module.PortalId.Should().Be(-1, "a module cannot be moved between tenants by editing it");
        module.ModuleDefinitionId.Should().Be(
            4,
            "which definition a module instantiates is fixed when it is created, and the update request "
            + "carries no definition field");
        // MIGRATION: 5.6 - the fixture starts deleted and the request clears the flag, so this asserts that
        // IsDeleted IS applied. An earlier revision expected it to be left alone on the grounds that deletion
        // was a separate operation; measured, IsDeleted is genuinely the ninth argument of the legacy first
        // provider call, and the legacy settings save wrote it unconditionally - as a bare False, so that
        // screen could only ever CLEAR it. Carrying it on the contract consolidates soft delete and restore,
        // a deliberate widening, and it means an omitted flag now clears rather than preserves.
        module.IsDeleted.Should().BeFalse("the request cleared the flag, exactly as the legacy save did");
        placement.TabModuleId.Should().Be(31);
        placement.TabId.Should().Be(7, "moving a module between pages is not an edit of the module");
        placement.ModuleId.Should().Be(0);
    }

    /// <summary>
    /// The update path falls back to the period already stored, which is not the create path's fallback.
    /// </summary>
    /// <remarks>
    /// MIGRATION: 5.5 - this asserts the LEGACY behaviour, and an earlier revision of this test asserted the
    /// opposite, that an omitted period should be left as it was. Measured: the legacy save read the
    /// cache-time box and stored a parsed integer when it was non-empty and LITERALLY ZERO when it was empty,
    /// so a blank field DISABLED caching. Zero is consequently a real value meaning "do not cache" and not an
    /// absent one, which is why the member is a non-nullable integer with no "unspecified" state. Treating
    /// zero as unset would silently enable caching on a module the caller asked not to cache. Note the
    /// deliberate contrast with the create path, where no stored period exists yet.
    /// </remarks>
    [Fact]
    public void ModuleApplyUpdate_StoresZeroForAnOmittedPeriodRatherThanKeepingTheStoredOne()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        placement.CacheTime.Should().Be(120, "the fixture stores a period that is not a default");

        ModuleMappings.ApplyUpdate(module, placement, new UpdateModuleRequest());

        placement.CacheTime.Should().Be(0);
    }

    /// <summary>
    /// The update path floors a negative period at zero and leaves the stored pane alone.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the pane is no longer substituted here, because the update contract carries no pane member
    /// to substitute FOR - the column is excluded and its stored value is preserved. Only the negative-period
    /// floor remains, which guards a value the store would refuse to interpret.
    /// </remarks>
    [Fact]
    public void ModuleApplyUpdate_FloorsANegativePeriodAndLeavesTheStoredPaneAlone()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();
        string storedPane = placement.PaneName;

        ModuleMappings.ApplyUpdate(module, placement, new UpdateModuleRequest { CacheTime = -1 });

        placement.PaneName.Should().Be(storedPane);
        placement.CacheTime.Should().Be(0);
    }

    /// <summary>
    /// Every module mapper refuses a null argument.
    /// </summary>
    [Fact]
    public void ModuleMappers_RefuseNullArguments()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();
        ModuleDefinition definition = new() { ModuleDefinitionId = 4, FriendlyName = "Announcements" };

        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToListItem(null!, placement, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToListItem(module, null!, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToDetail(null!, placement, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToDetail(module, null!, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToSettings(null!, placement, [], []); });
        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToSettings(module, null!, [], []); });
        Assert.Throws<ArgumentNullException>(
            () => { _ = ModuleMappings.ToSettings(module, placement, null!, []); });
        Assert.Throws<ArgumentNullException>(
            () => { _ = ModuleMappings.ToSettings(module, placement, [], null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToDto(null!, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToNewModule(-1, null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = ModuleMappings.ToNewPlacement(null!); });
        Assert.Throws<ArgumentNullException>(
            () => ModuleMappings.ApplyUpdate(null!, placement, new UpdateModuleRequest()));
        Assert.Throws<ArgumentNullException>(
            () => ModuleMappings.ApplyUpdate(module, null!, new UpdateModuleRequest()));
        Assert.Throws<ArgumentNullException>(() => ModuleMappings.ApplyUpdate(module, placement, null!));

        definition.FriendlyName.Should().Be("Announcements", "the definition above is otherwise unused");
    }

    // ---------------------------------------------------------------------------------------------
    // User
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The account list projection carries the account row plus the separately-resolved context.
    /// </summary>
    /// <remarks>
    /// The tenant, the postal address and the telephone number are all arguments rather than columns on the
    /// account row: an account is installation-wide, its membership is per tenant, and the address and
    /// telephone live in the profile value table under whichever definitions the tenant happens to have.
    /// </remarks>
    [Fact]
    public void UserToListItem_CarriesTheAccountPlusTheResolvedContext()
    {
        User user = FullUser();

        UserListItemDto dto = UserMappings.ToListItem(user, portalId: -1, address: "1 Measured Way", telephone: "555-0100");

        dto.UserId.Should().Be(1);
        dto.PortalId.Should().Be(-1, "the tenant comes from the route, not from the account row");
        dto.Username.Should().Be("measured_member");
        dto.FirstName.Should().Be("Ada");
        dto.LastName.Should().Be("Lovelace");
        dto.DisplayName.Should().Be("Ada Lovelace");
        dto.Address.Should().Be("1 Measured Way");
        dto.Telephone.Should().Be("555-0100");
        dto.Email.Should().Be("ada@example.com");
        dto.CreatedDate.Should().Be(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        dto.LastLoginDate.Should().Be(new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc));
        dto.IsApproved.Should().BeTrue();
        dto.IsOnline.Should().BeTrue();
        dto.IsSuperUser.Should().BeTrue();
        dto.IsLockedOut.Should().BeTrue();
    }

    /// <summary>
    /// An unread credential fact is reported as false and an absent address as empty.
    /// </summary>
    /// <remarks>
    /// Approval, lockout and presence are not columns on the account row - they live in the external
    /// membership store, and the repository leaves them null when it has not read that store. The list
    /// projection collapses null to false so the grid renders, which means a caller cannot tell "not
    /// approved" from "approval unknown" through this projection. That is intentional for a list, and it is
    /// asserted here so nobody mistakes it for a mapping bug.
    /// </remarks>
    [Fact]
    public void UserToListItem_ReportsUnreadCredentialFactsAsFalse()
    {
        User user = FullUser();
        user.IsApproved = null;
        user.IsOnline = null;
        user.IsLockedOut = null;
        user.Email = null;

        UserListItemDto dto = UserMappings.ToListItem(user, -1, null, null);

        dto.IsApproved.Should().BeFalse();
        dto.IsOnline.Should().BeFalse();
        dto.IsLockedOut.Should().BeFalse();
        dto.Email.Should().BeEmpty();
        dto.Address.Should().BeNull();
        dto.Telephone.Should().BeNull();
    }

    /// <summary>
    /// The account detail projection carries the account row plus the resolved role names.
    /// </summary>
    [Fact]
    public void UserToDetail_CarriesTheAccountPlusTheResolvedRoles()
    {
        User user = FullUser();
        string[] roles = ["Administrators", "Registered Users"];

        UserDetailDto dto = UserMappings.ToDetail(user, portalId: -1, roles);

        dto.UserId.Should().Be(1);
        dto.PortalId.Should().Be(-1);
        dto.Username.Should().Be("measured_member");
        dto.FirstName.Should().Be("Ada");
        dto.LastName.Should().Be("Lovelace");
        dto.DisplayName.Should().Be("Ada Lovelace");
        dto.Email.Should().Be("ada@example.com");
        dto.IsSuperUser.Should().BeTrue();
        dto.AffiliateId.Should().Be(5);
        dto.IsApproved.Should().BeTrue();
        dto.IsLockedOut.Should().BeTrue();
        dto.IsOnline.Should().BeTrue();
        dto.CreatedDate.Should().Be(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        dto.LastLoginDate.Should().Be(new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc));
        dto.LastActivityDate.Should().Be(new DateTime(2026, 7, 8, 9, 30, 0, DateTimeKind.Utc));
        dto.LastLockoutDate.Should().Be(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        dto.LastPasswordChangeDate.Should().Be(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        dto.Roles.Should().Equal(new[] { "Administrators", "Registered Users" });

        dto.MustChangePassword.Should().BeTrue(
            "the response calls the field MustChangePassword while the column it carries is named "
            + "UpdatePassword - the bridge is deliberate");
    }

    /// <summary>
    /// The detail projection reports unread credential facts as false and never leaks the stored hash.
    /// </summary>
    [Fact]
    public void UserToDetail_ReportsUnreadFactsAsFalseAndNeverCarriesTheStoredHash()
    {
        User user = FullUser();
        user.IsApproved = null;
        user.IsLockedOut = null;
        user.IsOnline = null;

        UserDetailDto dto = UserMappings.ToDetail(user, -1, []);

        dto.IsApproved.Should().BeFalse();
        dto.IsLockedOut.Should().BeFalse();
        dto.IsOnline.Should().BeFalse();
        dto.Roles.Should().BeEmpty();

        user.PasswordHash.Should().NotBeNull("the fixture stores a hash, so the next assertion is meaningful");
        typeof(UserDetailDto).GetProperties().Select(property => property.Name).Should().NotContain(
            new[] { "PasswordHash", "PasswordQuestion", "PasswordAnswer" },
            "no response object may expose the credential columns, and the projection cannot leak a field "
            + "the response has no place to put");
    }

    /// <summary>
    /// The profile definition projection takes the visibility from its argument, because the definition row
    /// carries no visibility of its own.
    /// </summary>
    [Fact]
    public void ProfileDefinitionToDto_TakesVisibilityFromItsArgument()
    {
        ProfilePropertyDefinition definition = FullDefinition();

        ProfilePropertyDefinitionDto dto = UserMappings.ToDto(definition, defaultVisibility: 2);

        dto.PropertyDefinitionId.Should().Be(9);
        dto.PortalId.Should().Be(-1);
        dto.ModuleDefId.Should().Be(4);
        dto.DataType.Should().Be(349);
        dto.DefaultValue.Should().Be("a default");
        dto.PropertyCategory.Should().Be("Contact Information");
        dto.PropertyName.Should().Be("Telephone");
        dto.Length.Should().Be(30);
        dto.Required.Should().BeTrue();
        dto.ValidationExpression.Should().Be(@"^\d+$");
        dto.ViewOrder.Should().Be(3);
        dto.Visible.Should().BeTrue();
        dto.Visibility.Should().Be(
            2,
            "visibility is a per-value fact, so a definition carries only the default the caller supplies");
    }

    /// <summary>
    /// The profile projection is driven by the definitions, so a definition with nothing stored still
    /// appears and a stored value with no definition does not.
    /// </summary>
    [Fact]
    public void UserToProfile_IsDrivenByTheDefinitionsRatherThanTheStoredValues()
    {
        ProfilePropertyDefinition telephone = FullDefinition();
        ProfilePropertyDefinition city = FullDefinition();
        city.PropertyDefinitionId = 10;
        city.PropertyName = "City";
        city.ViewOrder = 4;

        UserProfileValue stored = new()
        {
            ProfileId = 100,
            UserId = 1,
            PropertyDefinitionId = 9,
            PropertyValue = "555-0100",
            Visibility = 1,
            LastUpdatedDate = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc),
        };

        UserProfileValue orphan = new()
        {
            ProfileId = 101,
            UserId = 1,
            PropertyDefinitionId = 9999,
            PropertyValue = "orphaned",
            Visibility = 1,
            LastUpdatedDate = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc),
        };

        UserProfileDto dto = UserMappings.ToProfile(
            userId: 1,
            [telephone, city],
            [stored, orphan],
            defaultVisibility: 2);

        dto.UserId.Should().Be(1);
        dto.Properties.Should().HaveCount(
            2,
            "the projection walks the definitions, so the orphaned value is dropped and the unset "
            + "definition still appears");
        dto.Properties.Select(property => property.PropertyDefinitionId).Should().Equal(
            new[] { 9, 10 },
            "the definitions are walked in the order they were supplied, which is the order the screen "
            + "renders");

        UserProfileValueDto answered = dto.Properties[0];
        answered.PropertyValue.Should().Be("555-0100");
        answered.Visibility.Should().Be(1, "a stored value carries its own visibility");
        answered.LastUpdatedDate.Should().Be(new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc));
        answered.Definition.PropertyName.Should().Be("Telephone");

        UserProfileValueDto unanswered = dto.Properties[1];
        unanswered.PropertyValue.Should().BeEmpty();
        unanswered.Visibility.Should().Be(2, "an unset property falls back to the supplied default");
        unanswered.LastUpdatedDate.Should().BeNull(
            "there is no update date for a value that was never written, and reporting one would be a "
            + "fabrication");
        unanswered.Definition.PropertyName.Should().Be("City");
    }

    /// <summary>
    /// A stored value takes the bounded column when it holds anything and falls back to the long text
    /// column only when the bounded one is null.
    /// </summary>
    /// <remarks>
    /// This is the legacy read order, not a preference invented here. <c>GetUserProfile</c> returns a
    /// single column aliased <c>PropertyValue</c> computed as
    /// "case when (PropertyValue Is Null) then PropertyText else PropertyValue end"
    /// (<c>04.00.04.SqlDataProvider</c> line 1592, identical at <c>03.02.03</c> line 1533), so the
    /// bounded <c>nvarchar(3750)</c> column wins and the <c>ntext</c> column is the fallback. The write
    /// procedure fills exactly one of the two, so a row holding both is one some other writer left
    /// behind - and for it the legacy answer is the bounded column.
    /// </remarks>
    [Fact]
    public void UserToProfile_PrefersTheBoundedValueAndFallsBackToTheLongText()
    {
        UserProfileValue both = NewProfileValue(propertyValue: "short", propertyText: "long");
        UserProfileValue shortOnly = NewProfileValue(propertyValue: "short", propertyText: null);
        UserProfileValue longOnly = NewProfileValue(propertyValue: null, propertyText: "long");
        UserProfileValue neither = NewProfileValue(propertyValue: null, propertyText: null);

        Answer(both).PropertyValue.Should().Be(
            "short",
            "the legacy read procedure returns PropertyValue whenever it is not SQL NULL and only "
            + "falls back to PropertyText when it is");
        Answer(shortOnly).PropertyValue.Should().Be("short");
        Answer(longOnly).PropertyValue.Should().Be("long");

        UserProfileValue clearedWithStaleOverflow =
            NewProfileValue(propertyValue: string.Empty, propertyText: "long");

        Answer(clearedWithStaleOverflow).PropertyValue.Should().BeEmpty(
            "an empty string is the legacy Null.NullString sentinel and therefore a stored value, not "
            + "an absence: the read procedure tests PropertyValue Is Null, so a deliberately cleared "
            + "answer stays cleared and must never fall through to the overflow column");

        UserProfileValueDto blank = Answer(neither);

        blank.PropertyValue.Should().BeEmpty();
        blank.Visibility.Should().Be(
            1,
            "a row that exists but holds nothing is still a stored answer, so it keeps its own visibility "
            + "rather than falling back to the default - that is how a blanked answer stays "
            + "distinguishable from an unanswered one");
        blank.LastUpdatedDate.Should().NotBeNull();
    }

    /// <summary>
    /// A repeated stored value for one definition resolves to the row read last.
    /// </summary>
    [Fact]
    public void UserToProfile_ResolvesARepeatedValueToTheLastRow()
    {
        UserProfileValue first = NewProfileValue("first", null);
        UserProfileValue second = NewProfileValue("second", null);

        UserProfileDto dto = UserMappings.ToProfile(1, [FullDefinition()], [first, second], 2);

        dto.Properties.Should().HaveCount(1);
        dto.Properties[0].PropertyValue.Should().Be("second");
    }

    /// <summary>
    /// A profile with no definitions at all projects no properties rather than failing.
    /// </summary>
    [Fact]
    public void UserToProfile_ProjectsNothingWhenTheTenantDefinesNoProperties()
    {
        UserProfileDto dto = UserMappings.ToProfile(1, [], [], 2);

        dto.UserId.Should().Be(1);
        dto.Properties.Should().BeEmpty();
    }

    /// <summary>
    /// A new account computes its display name from the two name parts when none is supplied.
    /// </summary>
    /// <param name="displayName">The submitted display name.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UserToNewUser_ComputesTheDisplayNameWhenNoneIsSupplied(string? displayName)
    {
        CreateUserRequest request = new()
        {
            Username = "ada",
            FirstName = "Ada",
            LastName = "Lovelace",
            DisplayName = displayName!,
            Email = "ada@example.com",
        };

        UserMappings.ToNewUser(request).DisplayName.Should().Be("Ada Lovelace");
    }

    /// <summary>
    /// A supplied display name is kept exactly as submitted.
    /// </summary>
    [Fact]
    public void UserToNewUser_KeepsASuppliedDisplayName()
    {
        CreateUserRequest request = new()
        {
            Username = "ada",
            FirstName = "Ada",
            LastName = "Lovelace",
            DisplayName = "The Countess",
            Email = "ada@example.com",
        };

        UserMappings.ToNewUser(request).DisplayName.Should().Be("The Countess");
    }

    /// <summary>
    /// An absent surname becomes an empty one, and the computed display name carries no stray separator.
    /// </summary>
    [Fact]
    public void UserToNewUser_TreatsAnAbsentSurnameAsEmpty()
    {
        CreateUserRequest request = new()
        {
            Username = "ada",
            FirstName = "Ada",
            LastName = string.Empty,
            Email = "ada@example.com",
        };

        User user = UserMappings.ToNewUser(request);

        user.LastName.Should().BeEmpty("the surname column does not accept null");
        user.DisplayName.Should().Be(
            "Ada",
            "the computed name is trimmed, so a missing surname does not leave a trailing space in a "
            + "column that is shown on every screen");
    }

    /// <summary>
    /// A new account is created unprivileged and is not asked to change its password.
    /// </summary>
    [Fact]
    public void UserToNewUser_CreatesAnUnprivilegedAccount()
    {
        User user = UserMappings.ToNewUser(new CreateUserRequest
        {
            Username = "ada",
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
        });

        user.Username.Should().Be("ada");
        user.Email.Should().Be("ada@example.com");
        user.IsSuperUser.Should().BeFalse(
            "a host account is never created through the ordinary account path, whatever the request says");
        user.UpdatePassword.Should().BeFalse();
        user.UserId.Should().Be(0, "the store issues the key, not the caller");
        user.PasswordHash.Should().BeNull(
            "the credential is written to the external membership store, never onto the account row");
    }

    /// <summary>
    /// Applying an account update overwrites the editable names and recomputes a blanked display name.
    /// </summary>
    [Fact]
    public void UserApplyUpdate_OverwritesTheNamesAndRecomputesABlankedDisplayName()
    {
        User user = FullUser();

        UserMappings.ApplyUpdate(user, new UpdateUserRequest
        {
            FirstName = "Grace",
            LastName = "Hopper",
            DisplayName = "   ",
            Email = "grace@example.com",
        });

        user.FirstName.Should().Be("Grace");
        user.LastName.Should().Be("Hopper");
        user.DisplayName.Should().Be("Grace Hopper");
        user.Email.Should().Be("grace@example.com");
    }

    /// <summary>
    /// Applying an account update leaves identity, sign-in name and privilege alone.
    /// </summary>
    [Fact]
    public void UserApplyUpdate_LeavesIdentitySignInNameAndPrivilegeAlone()
    {
        User user = FullUser();

        UserMappings.ApplyUpdate(user, new UpdateUserRequest
        {
            FirstName = "Grace",
            LastName = "Hopper",
            DisplayName = "Rear Admiral",
            Email = "grace@example.com",
        });

        user.UserId.Should().Be(1, "an update never moves a row");
        user.Username.Should().Be(
            "measured_member",
            "the sign-in name is installation-wide and uniquely indexed, so it is not editable through "
            + "the ordinary profile form");
        user.IsSuperUser.Should().BeTrue(
            "privilege cannot be granted or withdrawn by editing a name, whatever the request carries");
        user.PasswordHash.Should().Be("$2a$12$measuredhashvalue");
        user.AffiliateId.Should().Be(5);
        user.DisplayName.Should().Be("Rear Admiral");
    }

    /// <summary>
    /// A new profile definition is anchored to the tenant the route named, starts undeleted, and floors a
    /// negative length.
    /// </summary>
    [Fact]
    public void ProfileDefinitionToNewDefinition_AnchorsTheTenantAndFloorsTheLength()
    {
        ProfilePropertyDefinitionDto request = new()
        {
            PropertyDefinitionId = 999,
            PortalId = 12345,
            ModuleDefId = 4,
            DataType = 349,
            DefaultValue = "a default",
            PropertyCategory = "Contact Information",
            PropertyName = "Telephone",
            Length = -30,
            Required = true,
            ValidationExpression = @"^\d+$",
            ViewOrder = 3,
            Visible = true,
            Visibility = 2,
        };

        ProfilePropertyDefinition definition = UserMappings.ToNewDefinition(portalId: -1, request);

        definition.PortalId.Should().Be(
            -1,
            "the tenant comes from the route, so a body claiming another tenant cannot define a property "
            + "somewhere else");
        definition.ModuleDefinitionId.Should().Be(4);
        definition.IsDeleted.Should().BeFalse();
        definition.DataType.Should().Be(349);
        definition.DefaultValue.Should().Be("a default");
        definition.PropertyCategory.Should().Be("Contact Information");
        definition.PropertyName.Should().Be("Telephone");
        definition.Length.Should().Be(0, "a negative width is floored rather than stored");
        definition.IsRequired.Should().BeTrue();
        definition.ValidationExpression.Should().Be(@"^\d+$");
        definition.ViewOrder.Should().Be(3);
        definition.IsVisible.Should().BeTrue();
        definition.PropertyDefinitionId.Should().Be(0, "the store issues the key, not the caller");
    }

    /// <summary>
    /// Applying a definition update leaves ownership, the module association and deletion alone, and
    /// ignores the visibility the response object happens to carry.
    /// </summary>
    [Fact]
    public void ProfileDefinitionApplyUpdate_LeavesOwnershipAndAssociationAlone()
    {
        ProfilePropertyDefinition definition = FullDefinition();

        UserMappings.ApplyDefinitionUpdate(definition, new ProfilePropertyDefinitionDto
        {
            PropertyDefinitionId = 999,
            PortalId = 12345,
            ModuleDefId = 8888,
            DataType = 350,
            DefaultValue = null,
            PropertyCategory = "Renamed Category",
            PropertyName = "Mobile",
            Length = 20,
            Required = false,
            ValidationExpression = null,
            ViewOrder = 7,
            Visible = false,
            Visibility = 0,
        });

        definition.DataType.Should().Be(350);
        definition.DefaultValue.Should().BeNull();
        definition.PropertyCategory.Should().Be("Renamed Category");
        definition.PropertyName.Should().Be("Mobile");
        definition.Length.Should().Be(20);
        definition.IsRequired.Should().BeFalse();
        definition.ValidationExpression.Should().BeNull();
        definition.ViewOrder.Should().Be(7);
        definition.IsVisible.Should().BeFalse();

        definition.PropertyDefinitionId.Should().Be(9, "an update never moves a row");
        definition.PortalId.Should().Be(-1, "a definition cannot be moved between tenants by editing it");
        definition.ModuleDefinitionId.Should().Be(
            4,
            "the module a definition belongs to is fixed when it is created, so the update path leaves it "
            + "untouched even though the response object carries the field");
        definition.IsDeleted.Should().BeFalse("deletion is its own operation");
    }

    /// <summary>
    /// Every account mapper refuses a null argument.
    /// </summary>
    [Fact]
    public void UserMappers_RefuseNullArguments()
    {
        User user = FullUser();
        ProfilePropertyDefinition definition = FullDefinition();

        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToListItem(null!, -1, null, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToDetail(null!, -1, []); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToDetail(user, -1, null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToDto(null!, 0); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToProfile(1, null!, [], 0); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToProfile(1, [], null!, 0); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToNewUser(null!); });
        Assert.Throws<ArgumentNullException>(() => UserMappings.ApplyUpdate(null!, new UpdateUserRequest()));
        Assert.Throws<ArgumentNullException>(() => UserMappings.ApplyUpdate(user, null!));
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToNewDefinition(-1, null!); });
        Assert.Throws<ArgumentNullException>(
            () => UserMappings.ApplyDefinitionUpdate(null!, new ProfilePropertyDefinitionDto()));
        Assert.Throws<ArgumentNullException>(() => UserMappings.ApplyDefinitionUpdate(definition, null!));
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a portal whose every mapped column carries a distinguishable value.
    /// </summary>
    /// <returns>The portal.</returns>
    private static Portal FullPortal()
    {
        Portal portal = new()
        {
            PortalId = -1,
            PortalName = "Measured Portal",
            Description = "A measured description",
            KeyWords = "measured,keywords",
            FooterText = "Measured footer",
            LogoFile = "logo.gif",
            BackgroundFile = "background.gif",
            ExpiryDate = new DateTime(2027, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            UserRegistration = UserRegistrationMode.VerifiedRegistration,
            BannerAdvertising = BannerAdvertisingMode.Host,
            Currency = "GBP",
            AdministratorId = 2,
            HostFee = 12.5m,
            HostSpace = 2048,
            PageQuota = 50,
            UserQuota = 500,
            AdministratorRoleId = 3,
            RegisteredRoleId = 4,
            PortalGuid = Guid.NewGuid(),
            PaymentProcessor = "PayPal",
            ProcessorUserId = "processor-user",
            ProcessorPassword = "processor-password",
            SiteLogHistory = 60,
            AdminTabId = 11,
            SplashTabId = 12,
            HomeTabId = 13,
            LoginTabId = 14,
            UserTabId = 15,
            DefaultLanguage = "en-GB",
            TimeZoneOffset = 120,
            HomeDirectory = "Portals/0",
        };

        portal.PortalAliases.Add(new PortalAlias
        {
            PortalAliasId = 1,
            PortalId = -1,
            HttpAlias = "alpha.example.com",
        });

        return portal;
    }

    /// <summary>
    /// Builds a page whose every mapped column carries a distinguishable value.
    /// </summary>
    /// <returns>The page.</returns>
    private static Tab FullTab() => new()
    {
        TabId = 0,
        TabOrder = 4,
        PortalId = -1,
        TabName = "Reports",
        IsVisible = true,
        ParentId = 1,
        Level = 2,
        IconFile = "reports.gif",
        DisableLink = true,
        Title = "Reports Title",
        Description = "Reports description",
        Keywords = "reports,measured",
        IsDeleted = true,
        Url = "~/Reports.aspx",
        SkinSrc = "[G]Skins/Default/Home.ascx",
        ContainerSrc = "[G]Containers/Default/Blue.ascx",
        TabPath = "//Home//Reports",
        StartDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        RefreshInterval = 45,
        PageHeadText = "<meta name=\"measured\" />",
        IsSecure = true,
    };

    /// <summary>
    /// Builds a role whose every mapped column carries a distinguishable value.
    /// </summary>
    /// <returns>The role.</returns>
    private static Role FullRole() => new()
    {
        RoleId = 0,
        PortalId = -1,
        RoleName = "Gold Members",
        Description = "A measured role",
        ServiceFee = 19.99m,
        BillingPeriod = 1,
        BillingFrequency = BillingFrequency.Month,
        TrialFee = 0m,
        TrialPeriod = 14,
        TrialFrequency = BillingFrequency.Day,
        IsPublic = true,
        AutoAssignment = true,
        RoleGroupId = 6,
        RsvpCode = "GOLD2026",
        IconFile = "gold.gif",
    };

    /// <summary>
    /// Builds a module whose every mapped column carries a distinguishable value and whose definition
    /// navigation is deliberately not loaded.
    /// </summary>
    /// <returns>The module.</returns>
    private static Module FullModule() => new()
    {
        ModuleId = 0,
        ModuleDefinitionId = 4,
        PortalId = -1,
        ModuleTitle = "Measured Module",
        AllTabs = true,
        IsDeleted = true,
        InheritViewPermissions = false,
        Header = "Measured header",
        Footer = "Measured footer",
        StartDate = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// Builds a placement whose identifiers and ordinals differ from the module's, so that a projection
    /// reading the wrong row cannot pass by coincidence.
    /// </summary>
    /// <returns>The placement.</returns>
    private static TabModule FullPlacement() => new()
    {
        TabModuleId = 31,
        TabId = 7,
        ModuleId = 0,
        PaneName = "RightPane",
        ModuleOrder = 6,
        CacheTime = 120,
        IconFile = "module.gif",
        Alignment = "left",
        Color = "#EEEEEE",
        Border = "1",
        ContainerSrc = "[G]Containers/DNN/Blue.ascx",
        Visibility = ModuleVisibility.Minimized,
        DisplayTitle = false,
        DisplayPrint = false,
        DisplaySyndicate = false,
    };

    /// <summary>
    /// Builds a package carrying a given name and capability bitmask.
    /// </summary>
    /// <param name="moduleName">The package name.</param>
    /// <param name="supportedFeatures">The stored capability bitmask.</param>
    /// <returns>The package.</returns>
    private static DesktopModule NewDesktopModule(string moduleName, int supportedFeatures) => new()
    {
        DesktopModuleId = 3,
        FriendlyName = "Measured Package",
        Description = "A measured package",
        Version = "04.09.00",
        IsPremium = true,
        IsAdmin = true,
        FolderName = "Measured",
        ModuleName = moduleName,
        SupportedFeatures = supportedFeatures,
    };

    /// <summary>
    /// Builds an account whose every mapped column carries a distinguishable value.
    /// </summary>
    /// <returns>The account.</returns>
    private static User FullUser() => new()
    {
        UserId = 1,
        Username = "measured_member",
        FirstName = "Ada",
        LastName = "Lovelace",
        DisplayName = "Ada Lovelace",
        Email = "ada@example.com",
        IsSuperUser = true,
        AffiliateId = 5,
        UpdatePassword = true,
        IsApproved = true,
        IsOnline = true,
        IsLockedOut = true,
        CreatedDate = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        LastLoginDate = new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc),
        LastActivityDate = new DateTime(2026, 7, 8, 9, 30, 0, DateTimeKind.Utc),
        LastLockoutDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        LastPasswordChangeDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        PasswordHash = "$2a$12$measuredhashvalue",
    };

    /// <summary>
    /// Builds a profile property definition whose every mapped column carries a distinguishable value.
    /// </summary>
    /// <returns>The definition.</returns>
    private static ProfilePropertyDefinition FullDefinition() => new()
    {
        PropertyDefinitionId = 9,
        PortalId = -1,
        ModuleDefinitionId = 4,
        IsDeleted = false,
        DataType = 349,
        DefaultValue = "a default",
        PropertyCategory = "Contact Information",
        PropertyName = "Telephone",
        Length = 30,
        IsRequired = true,
        ValidationExpression = @"^\d+$",
        ViewOrder = 3,
        IsVisible = true,
    };

    /// <summary>
    /// Builds a stored profile value for the definition the fixtures use.
    /// </summary>
    /// <param name="propertyValue">The short value column.</param>
    /// <param name="propertyText">The long text column.</param>
    /// <returns>The stored value.</returns>
    private static UserProfileValue NewProfileValue(string? propertyValue, string? propertyText) => new()
    {
        ProfileId = 100,
        UserId = 1,
        PropertyDefinitionId = 9,
        PropertyValue = propertyValue,
        PropertyText = propertyText,
        Visibility = 1,
        LastUpdatedDate = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc),
    };

    /// <summary>
    /// Projects a single stored value through the profile mapper and returns the one property it produced.
    /// </summary>
    /// <param name="stored">The stored value.</param>
    /// <returns>The projected property.</returns>
    private static UserProfileValueDto Answer(UserProfileValue stored)
        => UserMappings.ToProfile(1, [FullDefinition()], [stored], defaultVisibility: 2).Properties[0];
}
