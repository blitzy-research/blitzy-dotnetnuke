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
/// Three classes of behaviour get particular attention, because they are the places where a mapper does
/// more than copy. The first is defaulting: several mappers substitute a value when the request omits one,
/// and the substitute differs between the create path and the update path in a way that is easy to get
/// wrong.
/// </para>
/// <para>
/// No entity ever crosses the API boundary, so the direction of each mapping matters. Read mappers project
/// an entity plus separately-resolved context into a response object; write mappers apply a request onto an
/// entity that the caller already owns.
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
            superTabId: 99,
            currentPortalAliasId: null);

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

    /// <summary>The detail projection orders the aliases without regard to case.</summary>
    [Fact]
    public void PortalToDetail_OrdersAliasesWithoutRegardToCase()
    {
        Portal portal = FullPortal();
        portal.PortalAliases.Clear();
        portal.PortalAliases.Add(new PortalAlias { PortalAliasId = 3, PortalId = -1, HttpAlias = "zulu.example.com" });
        portal.PortalAliases.Add(new PortalAlias { PortalAliasId = 1, PortalId = -1, HttpAlias = "Alpha.example.com" });
        portal.PortalAliases.Add(new PortalAlias { PortalAliasId = 2, PortalId = -1, HttpAlias = "mike.example.com" });

        PortalDetailDto dto = PortalMappings.ToDetail(portal, 0, 0, null, null, null, null, null);

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

    /// <summary>The alias projection carries all three of its columns.</summary>
    [Fact]
    public void PortalAliasToDto_CarriesAllThreeColumns()
    {
        PortalAlias alias = new() { PortalAliasId = 17, PortalId = -1, HttpAlias = "alpha.example.com" };

        PortalAliasDto dto = PortalMappings.ToDto(alias, currentPortalAliasId: null);

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
        portal.PortalGuid.Should().BeEmpty(
            "the handle is left at its type default so the store's newid() default supplies it, which is "
            + "what keeps this mapper a pure function of its arguments");
    }

    /// <summary>
    /// The mapper is deterministic: two identical calls produce identical aggregates, handle included.
    /// </summary>
    [Fact]
    public void PortalToNewPortal_IsDeterministicAndLeavesTheHandleToTheStore()
    {
        CreatePortalRequest request = new() { PortalName = "Tenant" };

        Portal first = PortalMappings.ToNewPortal(request, "USD", null, 0m, 0, 0, 0, null, "Portals/1");
        Portal second = PortalMappings.ToNewPortal(request, "USD", null, 0m, 0, 0, 0, null, "Portals/1");

        first.PortalGuid.Should().BeEmpty();
        second.PortalGuid.Should().Be(first.PortalGuid);
        second.PortalName.Should().Be(first.PortalName);
        second.HomeDirectory.Should().Be(first.HomeDirectory);
    }

    /// <summary>
    /// A new portal records its hosting terms exactly as supplied, including negative ones, and tolerates
    /// an unnamed request.
    /// </summary>
    [Fact]
    public void PortalToNewPortal_RecordsTheHostingTermsVerbatimAndToleratesAnUnnamedRequest()
    {
        CreatePortalRequest request = new() { PortalName = null };

        Portal portal = PortalMappings.ToNewPortal(
            request, "USD", null, hostFee: -50m, hostSpace: -1, pageQuota: -2, userQuota: -3, null, "Portals/1");

        portal.PortalName.Should().BeEmpty(
            "the name column does not accept null, and the create validator does not require a name");
        portal.HostFee.Should().Be(-50m);
        portal.HostSpace.Should().Be(-1);
        portal.PageQuota.Should().Be(-2);
        portal.UserQuota.Should().Be(-3);
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
            ProcessorCredentialReference = "secret://processor/replacement",
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
        portal.ProcessorCredentialReference.Should().Be("secret://processor/replacement");
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
    /// The processor reference uses an explicit keep/clear/replace contract without ever carrying the
    /// referenced credential.
    /// </summary>
    [Fact]
    public void PortalApplyUpdate_UsesExplicitProcessorReferenceOperations()
    {
        Portal portal = FullPortal();
        portal.ProcessorCredentialReference = "secret://processor/existing";

        PortalMappings.ApplyUpdate(portal, new UpdatePortalRequest());
        portal.ProcessorCredentialReference.Should().Be("secret://processor/existing");

        PortalMappings.ApplyUpdate(portal, new UpdatePortalRequest { ProcessorCredentialReference = string.Empty });
        portal.ProcessorCredentialReference.Should().BeNull();

        PortalMappings.ApplyUpdate(
            portal,
            new UpdatePortalRequest { ProcessorCredentialReference = "secret://processor/new" });
        portal.ProcessorCredentialReference.Should().Be("secret://processor/new");
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

    /// <summary>Applying an update stores a negative charge and negative quotas exactly as submitted.</summary>
    [Fact]
    public void PortalApplyUpdate_StoresNegativeTermsVerbatim()
    {
        Portal portal = FullPortal();

        UpdatePortalRequest request = new()
        {
            PortalId = portal.PortalId,
            PortalName = "Renamed",
            HostFee = -0.01m,
            HostSpace = -5,
            PageQuota = -5,
            UserQuota = -5,
        };

        PortalMappings.ApplyUpdate(portal, request);

        portal.HostFee.Should().Be(-0.01m);
        portal.HostSpace.Should().Be(-5);
        portal.PageQuota.Should().Be(-5);
        portal.UserQuota.Should().Be(-5);
    }

    /// <summary>The fee floor admits zero and every positive amount and refuses to store a credit.</summary>
    /// <param name="supplied">The submitted amount, as an invariant decimal literal.</param>
    /// <param name="expected">The amount that should be stored, as an invariant decimal literal.</param>
    /// <remarks>
    /// The amounts travel as strings because a decimal cannot appear in an attribute argument at all, and
    /// routing them through a double first would introduce a representation question that has nothing to do
    /// with what is being tested.
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

    /// <summary>Every portal mapper refuses a null argument rather than producing a half-populated result.</summary>
    [Fact]
    public void PortalMappers_RefuseNullArguments()
    {
        Portal portal = FullPortal();
        CreatePortalRequest createRequest = new();
        UpdatePortalRequest updateRequest = new();

        Assert.Throws<ArgumentNullException>(() => { _ = PortalMappings.ToListItem(null!, [], 0, 0); });
        Assert.Throws<ArgumentNullException>(() => { _ = PortalMappings.ToListItem(portal, null!, 0, 0); });
        Assert.Throws<ArgumentNullException>(
            () => { _ = PortalMappings.ToDetail(null!, 0, 0, null, null, null, null, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = PortalMappings.ToSettings(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = PortalMappings.ToDto(null!, null); });
        Assert.Throws<ArgumentNullException>(
            () => { _ = PortalMappings.ToNewPortal(null!, "USD", null, 0m, 0, 0, 0, null, "Portals/1"); });
        Assert.Throws<ArgumentNullException>(() => PortalMappings.ApplyUpdate(null!, updateRequest));
        Assert.Throws<ArgumentNullException>(() => PortalMappings.ApplyUpdate(portal, null!));

        createRequest.PortalName.Should().BeNull("the request used above carries no values of its own");
    }

    // ---------------------------------------------------------------------------------------------
    // Tab
    // ---------------------------------------------------------------------------------------------

    /// <summary>The page list projection carries the tree columns and the separately-computed child flag.</summary>
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

    /// <summary>The child flag is carried through unchanged when the page has no children.</summary>
    [Fact]
    public void TabToListItem_CarriesAnAbsentChildFlag()
    {
        TabMappings.ToListItem(FullTab(), hasChildren: false).HasChildren.Should().BeFalse();
    }

    /// <summary>The page detail projection carries every editable column, keywords included.</summary>
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
            "the entity and the response both spell the field Keywords while the legacy column behind "
            + "it is spelled KeyWords - bridging that casing is the persistence layer's job, so this "
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
            "[G]Skins/Default/Home.ascx",
            "the stored skin survives an unrelated edit rather than being blanked by an absent member");
        tab.ContainerSrc.Should().Be(
            "[G]Containers/Default/Blue.ascx",
            "the stored container survives for the same reason");
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

    /// <summary>Every page mapper refuses a null argument.</summary>
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

    /// <summary>The role list projection carries the membership terms the list screen shows.</summary>
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
    /// The role detail projection carries every one of the fourteen members it declares, each drawn from a
    /// column on the single role row it is handed.
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
    /// The detail projection carries no member that is not a column on the role row, so neither the owning
    /// portal nor any join denormalisation appears on it.
    /// </summary>
    [Fact]
    public void RoleDetail_DeclaresNoPortalIdentifierAndNoJoinDenormalisation()
    {
        string[] declared = typeof(RoleDetailDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        // FIFTEEN: the fourteen role columns plus the derived optimistic-concurrency token.
        declared.Should().HaveCount(15);
        declared.Should().Contain("ConcurrencyToken");
        declared.Should().NotContain("PortalId");
        declared.Should().NotContain("RoleGroupName");
        declared.Should().NotContain("UserCount");
        declared.Should().NotContain("MemberCount");
        declared.Should().NotContain("RsvpLink");
        declared.Should().NotContain("RoleStatus");
    }

    /// <summary>
    /// An ungrouped role projects a null group identifier, and a role in the group whose identifier is zero
    /// projects that zero faithfully rather than treating it as absent.
    /// </summary>
    /// <remarks>
    /// The second case is the one worth guarding: <c>RoleGroups.RoleGroupID</c> is seeded
    /// <c>IDENTITY(0,1)</c>, so zero identifies the first group ever created and is not a stand-in for "no
    /// group". The legacy interface encoded the absent case as -1, which never reached the column because
    /// the foreign key would have rejected it.
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
    /// The membership projection carries the five values the legacy grid rendered plus the three keys a
    /// caller needs in order to act on the row.
    /// </summary>
    /// <remarks>
    /// Measured against <c>Website/admin/Security/securityroles.ascx:L71-L86</c>. The delete affordance on
    /// each legacy row was governed by <c>DeleteButtonVisible(UserID, RoleID)</c> at <c>:L68</c>, which is
    /// why the role key travels beside the role name.
    /// </remarks>
    [Fact]
    public void RoleMembershipToDto_CarriesTheRenderedColumnsAndTheKeys()
    {
        DateTime effective = new(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        DateTime expiry = new(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        UserRole assignment = new()
        {
            UserRoleId = 88,
            UserId = 12,
            RoleId = 0,
            EffectiveDate = effective,
            ExpiryDate = expiry,
            User = new User { UserId = 12, Username = "grace", DisplayName = "Grace Hopper" },
            Role = new Role { RoleId = 0, PortalId = -1, RoleName = "Subscribers" },
        };

        RoleMembershipDto dto = RoleMappings.ToMembership(assignment);

        dto.UserRoleId.Should().Be(88);
        dto.UserId.Should().Be(12);
        dto.Username.Should().Be("grace");
        dto.DisplayName.Should().Be("Grace Hopper");
        dto.RoleId.Should().Be(0, "Roles.RoleID is IDENTITY(0, 1), so zero is a real key");
        dto.RoleName.Should().Be("Subscribers");
        dto.EffectiveDate.Should().Be(effective);
        dto.ExpiryDate.Should().Be(expiry);
    }

    /// <summary>An open-ended membership projects both dates as absent, never as a sentinel date.</summary>
    [Fact]
    public void RoleMembershipToDto_LeavesAnOpenEndedMembershipsDatesAbsent()
    {
        UserRole assignment = new()
        {
            UserRoleId = 1,
            UserId = 2,
            RoleId = 3,
            EffectiveDate = null,
            ExpiryDate = null,
            User = new User { UserId = 2, Username = "ada", DisplayName = "Ada Lovelace" },
            Role = new Role { RoleId = 3, PortalId = -1, RoleName = "Registered Users" },
        };

        RoleMembershipDto dto = RoleMappings.ToMembership(assignment);

        dto.EffectiveDate.Should().BeNull();
        dto.ExpiryDate.Should().BeNull();
    }

    /// <summary>
    /// A membership whose account or role was not composed is refused rather than projected with an empty
    /// name.
    /// </summary>
    [Fact]
    public void RoleMembershipToDto_RefusesAnAssignmentThatWasNotComposed()
    {
        UserRole withoutAccount = new()
        {
            UserRoleId = 1,
            UserId = 2,
            RoleId = 3,
            Role = new Role { RoleId = 3, PortalId = -1, RoleName = "Registered Users" },
        };

        Assert.Throws<ArgumentNullException>(() => RoleMappings.ToMembership(withoutAccount));

        UserRole withoutRole = new()
        {
            UserRoleId = 1,
            UserId = 2,
            RoleId = 3,
            User = new User { UserId = 2, Username = "ada", DisplayName = "Ada Lovelace" },
        };

        Assert.Throws<ArgumentNullException>(() => RoleMappings.ToMembership(withoutRole));
    }

    /// <summary>The group projection carries all four of its columns.</summary>
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

    /// <summary>A new role is anchored to the tenant the route named and takes every term from the request.</summary>
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

    /// <summary>A free role keeps every term absent, and an absent fee is not converted into a zero fee.</summary>
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
            new UpdateRoleRequest { RoleName = "Odd", ServiceFee = -5m, TrialFee = -1m });

        updated.ServiceFee.Should().Be(0m);
        updated.TrialFee.Should().Be(0m);
    }

    /// <summary>
    /// Applying a role update overwrites every term, including the name, and leaves identity and ownership
    /// alone.
    /// </summary>
    [Fact]
    public void RoleApplyUpdate_OverwritesEveryTermAndLeavesIdentityAlone()
    {
        Role role = FullRole();

        UpdateRoleRequest request = new()
        {
            RoleName = "Platinum Members",
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

        // The name IS overwritten.
        role.RoleName.Should().Be("Platinum Members", "an update replaces the name like any other term");
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

    /// <summary>A new group is anchored to the tenant the route named, which the body cannot contradict.</summary>
    [Fact]
    public void RoleGroupToNewGroup_AnchorsTheTenantFromTheRoute()
    {
        CreateRoleGroupRequest request = new()
        {
            RoleGroupName = "Paid Tiers",
            Description = "Tiers that carry a charge",
        };

        RoleGroup group = RoleMappings.ToNewGroup(portalId: -1, request);

        group.PortalId.Should().Be(
            -1,
            "the tenant comes from the route, and the contract carries no member that could name another");
        group.RoleGroupName.Should().Be("Paid Tiers");
        group.Description.Should().Be("Tiers that carry a charge");
        group.RoleGroupId.Should().Be(0, "the store issues the key, not the caller");
    }

    /// <summary>Applying a group update overwrites the name and the description only.</summary>
    /// <remarks>
    /// The update contract carries only those two members, so "only" is now a property of the contract as
    /// well as of the mapper. The identifier and tenant assertions below are retained because they prove
    /// the mapper writes neither from anywhere else.
    /// </remarks>
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
            new UpdateRoleGroupRequest { RoleGroupName = "Renamed", Description = null });

        group.RoleGroupName.Should().Be("Renamed");
        group.Description.Should().BeNull();
        group.RoleGroupId.Should().Be(6, "an update never moves a row");
        group.PortalId.Should().Be(-1, "a group cannot be moved between tenants by editing it");
    }

    /// <summary>Every role mapper refuses a null argument.</summary>
    [Fact]
    public void RoleMappers_RefuseNullArguments()
    {
        Role role = FullRole();
        RoleGroup group = new() { RoleGroupName = "Group" };

        Assert.Throws<ArgumentNullException>(() => { _ = RoleMappings.ToListItem(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = RoleMappings.ToDetail(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = RoleMappings.ToDto(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = RoleMappings.ToNewRole(-1, null!); });
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ApplyUpdate(
            null!,
            new UpdateRoleRequest { RoleName = "Gold Members" }));
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ApplyUpdate(role, null!));
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ToNewGroup(-1, null!));
        Assert.Throws<ArgumentNullException>(
            () => RoleMappings.ApplyGroupUpdate(null!, new UpdateRoleGroupRequest()));
        Assert.Throws<ArgumentNullException>(() => RoleMappings.ApplyGroupUpdate(group, null!));
    }

    // ---------------------------------------------------------------------------------------------
    // Module
    // ---------------------------------------------------------------------------------------------

    /// <summary>The module list projection draws each field from the correct one of its two sources.</summary>
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

        ModuleListItemDto dto = ModuleMappings.ToListItem(module, placement, AnnouncementsCatalogue);

        dto.ModuleId.Should().Be(0, "the module identifier comes from the module row");
        dto.TabModuleId.Should().Be(31, "the placement identifier comes from the placement row");
        dto.TabId.Should().Be(
            7,
            "which page the module appears on is a fact about the placement, not about the module");
        dto.ModuleDefId.Should().Be(4);
        dto.ModuleTitle.Should().Be("Measured Module");
        dto.FriendlyName.Should().Be("Announcements");
        dto.DesktopModuleId.Should().Be(
            3,
            "the package key comes from the resolved catalogue facts, not from the module row");
        dto.ModuleName.Should().Be("Announcements");
        dto.Description.Should().Be("Displays a list of announcements.");
        dto.Version.Should().Be("04.09.00");
        dto.IsAdmin.Should().BeFalse(
            "an ordinary module reports the package's own administrative flag, not an absence");
        dto.ModuleOrder.Should().Be(6, "the ordinal within the pane belongs to the placement");
        dto.AllTabs.Should().BeTrue();
        dto.Visibility.Should().Be(ModuleVisibility.Minimized);
        dto.IsDeleted.Should().BeTrue();
        dto.DisplayTitle.Should().BeFalse();
        dto.StartDate.Should().Be(new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc));
        dto.EndDate.Should().Be(new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>The list projection falls back to the loaded definition when no name is supplied.</summary>
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

        ModuleMappings.ToListItem(module, FullPlacement(), catalogue: null)
            .FriendlyName.Should().Be("Loaded Definition");
    }

    /// <summary>The list projection leaves the name absent when neither source can supply one.</summary>
    [Fact]
    public void ModuleToListItem_LeavesTheNameAbsentWhenNeitherSourceHasOne()
    {
        Module module = FullModule();

        module.ModuleDefinition.Should().BeNull("the fixture deliberately loads no definition");

        ModuleListItemDto unresolved = ModuleMappings.ToListItem(module, FullPlacement(), catalogue: null);

        unresolved.FriendlyName.Should().BeNull(
            "the list projection reports an unresolved name as absent rather than inventing one");
        unresolved.DesktopModuleId.Should().BeNull(
            "an unresolved definition has no package key to report, and zero is not one");
        unresolved.ModuleName.Should().BeNull();
        unresolved.Description.Should().BeNull();
        unresolved.Version.Should().BeNull();

        // ⚠ NULL, NOT FALSE, AND THE DIFFERENCE IS WHAT A CLIENT ACTS ON. The column behind this member is
        // `bit NOT NULL`, so false is a POSITIVE claim that the module is ordinary - and a client reads that
        // claim as licence to offer the generic settings screen. Reporting it for a package nobody could read
        // would be inventing the very fact the caller asked about.
        unresolved.IsAdmin.Should().BeNull(
            "an unresolved package makes no claim either way about being administrative");
    }

    /// <summary>
    /// An administrative package is reported as such on both module contracts, which is the only way a client
    /// can tell that the generic settings surface will refuse it.
    /// </summary>
    /// <remarks>
    /// Reported against the running application: the module listing offered a Settings command on every row,
    /// and on the row created from the administrative User Accounts package it led to a screen that answered
    /// 403 `module.settings_protected` every time. The administrative definitions are also withheld from the
    /// portal-placeable definition catalogue, so a second read could not have told the client either.
    /// </remarks>
    [Fact]
    public void ModuleProjections_ReportAnAdministrativePackageAsAdministrative()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleCatalogueFacts administrative = ModuleCatalogueFacts.From(
            new ModuleDefinition
            {
                ModuleDefinitionId = 4,
                FriendlyName = "User Accounts",
                DesktopModuleId = 9,
            },
            new DesktopModule
            {
                DesktopModuleId = 9,
                ModuleName = "DNN_UserAccounts",
                FriendlyName = "User Accounts",
                IsAdmin = true,
            });

        administrative.IsAdmin.Should().BeTrue("the facts are resolved from the package row itself");

        ModuleMappings.ToListItem(module, placement, administrative).IsAdmin.Should().BeTrue();
        ModuleMappings.ToDetail(module, placement, administrative).IsAdmin.Should().BeTrue(
            "the detail contract is what the settings screen reads before it offers a form");
    }

    /// <summary>The module detail projection draws each field from the correct one of its two sources.</summary>
    [Fact]
    public void ModuleToDetail_DrawsEachFieldFromTheCorrectRow()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleDetailDto dto = ModuleMappings.ToDetail(module, placement, AnnouncementsCatalogue);

        dto.ModuleId.Should().Be(0);
        dto.TabModuleId.Should().Be(31);
        dto.TabId.Should().Be(7);
        dto.PortalId.Should().Be(-1);
        dto.ModuleDefId.Should().Be(4);
        dto.FriendlyName.Should().Be("Announcements");
        dto.IsAdmin.Should().BeFalse();
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

        // MIGRATION: these four are asserted against the RESOLVED facts, not against the module's own
        // navigations.
        dto.DesktopModuleId.Should().Be(3, "the package key comes from the resolved catalogue facts");
        dto.ModuleName.Should().Be("Announcements");
        dto.Description.Should().Be("Displays a list of announcements.");
        dto.Version.Should().Be("04.09.00");
    }

    /// <summary>
    /// The detail projection reports an unresolved definition name as absent rather than inventing one.
    /// </summary>
    [Fact]
    public void ModuleToDetail_ReportsAnUnresolvedNameAsAbsent()
    {
        ModuleMappings.ToDetail(FullModule(), FullPlacement(), catalogue: null)
            .FriendlyName.Should().BeNull(
                "the fallback chain ends at null, so an unresolved join stays distinguishable from a "
                + "definition whose name is genuinely blank");
    }

    /// <summary>
    /// The three appearance columns the legacy settings screen administered are carried on both the detail
    /// and the update contract; the four that only a renderer ever chose are on neither. No contract invents
    /// a container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ THE THREE ADMINISTERED COLUMNS USED TO BE ON NEITHER CONTRACT, AND THIS TEST ASSERTED THAT. Reported
    /// against the running application: the settings screen told the operator the module "has no stored
    /// settings of its own" while `dbo.TabModules` held an Alignment, a Color and a Border for it, and the
    /// screen offered no way to read or change any of them. `modulesettings.ascx:L120-L139` declares all three
    /// as fields - a radio list, a free-text colour and a one-character border - and
    /// `ModuleSettings.ascx.vb:L144-L147` reads them while `:L345-L347` saves them, so they are part of the
    /// legacy screen's workflow and MCC item 4 requires the workflow to be supported.
    /// </para>
    /// <para>
    /// They also still have a live consumer: this migration is side-by-side, the legacy site runs against the
    /// same database, and its container rendering is what paints these three columns. Declining to administer
    /// them did not make them unused - it made them unreachable.
    /// </para>
    /// <para>
    /// The four below are a different case and stay excluded. A pane is chosen by the skin that declares it, a
    /// container IS a skin object, and print and syndicate are rendering affordances of the legacy page
    /// framework - none of them is a field this migration's screens can honestly offer.
    /// </para>
    /// </remarks>
    [Fact]
    public void ModuleReadProjections_CarryTheAdministeredAppearanceAndOfferNoContainer()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleDetailDto detail = ModuleMappings.ToDetail(module, placement, AnnouncementsCatalogue);
        ModuleSettingsDto settings = ModuleMappings.ToSettings(module, placement, [], []);

        foreach (string administered in new[] { "Alignment", "Color", "Border" })
        {
            typeof(UpdateModuleRequest).GetProperty(administered).Should().NotBeNull(
                $"the legacy settings screen let an administrator set {administered}, so the update contract "
                + "must carry it or the workflow is gone");

            typeof(ModuleDetailDto).GetProperty(administered).Should().NotBeNull(
                $"a screen cannot show the stored {administered} it is about to replace unless the detail "
                + "contract reports it");

            typeof(ModuleSettingsDto).GetProperty(administered).Should().BeNull(
                $"{administered} is a column on the placement, not a key-value setting, so it belongs to the "
                + "detail contract and must not be duplicated onto the settings map");
        }

        detail.Alignment.Should().Be(
            placement.Alignment,
            "the projection reports what is stored rather than a default");
        detail.Color.Should().Be(placement.Color);
        detail.Border.Should().Be(placement.Border);

        foreach (string appearance in new[]
        {
            "PaneName", "DisplayPrint", "DisplaySyndicate",
        })
        {
            typeof(UpdateModuleRequest).GetProperty(appearance).Should().BeNull(
                $"the placement's {appearance} is chosen by the page framework this migration excludes, "
                + "so the update contract must not offer it either");

            typeof(ModuleDetailDto).GetProperty(appearance).Should().BeNull(
                $"the placement's {appearance} is chosen by the page framework this migration excludes, "
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
    /// The four pane-layout, rendering and skinning columns the update contract excludes are left exactly as
    /// they were stored, rather than being cleared by an update that cannot name them - while the three the
    /// contract DOES carry are written from the request, including to nothing.
    /// </summary>
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
        placement.ContainerSrc.Should().Be(storedContainer);
        placement.DisplayPrint.Should().Be(storedPrint);
        placement.DisplaySyndicate.Should().Be(storedSyndicate);

        // ⚠ AND THE THREE ADMINISTERED COLUMNS ARE CLEARED, WHICH IS THE CONTRACT AND NOT AN OVERSIGHT. This
        // is a full-replacement update: every member it carries is written from the request, so a request
        // that names none of the three withdraws all three. That is what makes clearing the colour field on
        // the settings screen mean something, and it is why every screen that submits this contract must
        // round-trip the values it is not editing - the module form does exactly that.
        storedColor.Should().NotBeNull("the fixture must start with values for this to prove anything");
        storedBorder.Should().NotBeNull();

        placement.Alignment.Should().BeNull("the request named no alignment, and the update replaces");
        placement.Color.Should().BeNull();
        placement.Border.Should().BeNull();

        // The members the contract DOES carry still applied, proving preservation is targeted rather than a
        // wholesale refusal to write.
        placement.ModuleOrder.Should().Be(7);
        placement.IconFile.Should().Be("changed.gif");
    }

    /// <summary>
    /// The three administered appearance columns are written from the request, exactly as submitted.
    /// </summary>
    /// <remarks>
    /// The empty string is a REAL stored alignment - it is the value behind the legacy radio list's "Not
    /// Specified" entry at `modulesettings.ascx:L120-L139` - so it must reach the column as an empty string
    /// rather than being folded into a null on the way.
    /// </remarks>
    [Fact]
    public void ModuleApplyUpdate_WritesTheAdministeredAppearanceAsSubmitted()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        UpdateModuleRequest request = new()
        {
            TabId = placement.TabId,
            ModuleOrder = placement.ModuleOrder,
            Alignment = "center",
            Color = "#003366",
            Border = "3",
        };

        ModuleMappings.ApplyUpdate(module, placement, request);

        placement.Alignment.Should().Be("center");
        placement.Color.Should().Be("#003366");
        placement.Border.Should().Be("3");

        UpdateModuleRequest unspecified = new()
        {
            TabId = placement.TabId,
            ModuleOrder = placement.ModuleOrder,
            Alignment = string.Empty,
            Color = null,
            Border = null,
        };

        ModuleMappings.ApplyUpdate(module, placement, unspecified);

        placement.Alignment.Should().Be(
            string.Empty,
            "the empty string is the legacy list's own \"Not Specified\" value, which is a stored alignment "
            + "and not an absent one");
        placement.Color.Should().BeNull("a cleared colour is a withdrawal, and null is how the column holds it");
        placement.Border.Should().BeNull();
    }

    /// <summary>The settings projection keeps the two setting scopes apart.</summary>
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

    /// <summary>Setting names are matched without regard to case, as the legacy store did.</summary>
    [Fact]
    public void ModuleToSettings_MatchesSettingNamesWithoutRegardToCase()
    {
        ModuleSetting[] moduleSettings = [new() { ModuleId = 0, SettingName = "Skin", SettingValue = "Blue" }];

        ModuleSettingsDto dto = ModuleMappings.ToSettings(FullModule(), FullPlacement(), moduleSettings, []);

        dto.ModuleSettings.ContainsKey("SKIN").Should().BeTrue();
        dto.ModuleSettings.ContainsKey("skin").Should().BeTrue();
        dto.ModuleSettings["sKiN"].Should().Be("Blue");
    }

    /// <summary>A setting with no name is discarded rather than keyed on an empty string.</summary>
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

    /// <summary>A repeated setting name resolves to the value read last rather than throwing.</summary>
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

    /// <summary>The definition projection prefers a package handed in over one loaded on the navigation.</summary>
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

    /// <summary>The definition projection falls back to the loaded package when none is handed in.</summary>
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

    /// <summary>The definition projection tolerates a package that was never loaded.</summary>
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

    /// <summary>A new module is anchored to the tenant the route named and starts undeleted.</summary>
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
    /// A new placement takes the page from the request and supplies the pane itself, because the creation
    /// contract deliberately carries no pane.
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
    /// A new placement preserves a zero caching period as "do not cache" and carries a negative one through
    /// unchanged rather than flooring it.
    /// </summary>
    [Fact]
    public void ModuleToNewPlacement_PreservesZeroAndCarriesANegativePeriodThrough()
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

        ModuleMappings.ToNewPlacement(negative).CacheTime.Should().Be(-30);
    }

    /// <summary>
    /// Applying a module update overwrites both rows and leaves identity, ownership and deletion alone.
    /// </summary>
    [Fact]
    public void ModuleApplyUpdate_OverwritesBothRowsAndLeavesIdentityAlone()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        // Every one of the nineteen members the contract carries, so this exercises the whole surface.
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
            Alignment = "right",
            Color = "#003366",
            Border = "2",
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

        // The three pane-layout and rendering columns keep their FIXTURE values, because the update contract
        // carries no field for any of them. The same reasoning as the container below: excluding a column
        // means declining to manage it, not wiping it.
        placement.PaneName.Should().Be("RightPane");
        placement.DisplayPrint.Should().BeFalse();
        placement.DisplaySyndicate.Should().BeFalse();

        // The three the contract DOES carry were named by this request and are written from it, which is the
        // whole surface being exercised here.
        placement.Alignment.Should().Be("right");
        placement.Color.Should().Be("#003366");
        placement.Border.Should().Be("2");

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
        // 5.6 - the fixture starts deleted and the request clears the flag, so this asserts that IsDeleted
        // IS applied.
        module.IsDeleted.Should().BeFalse("the request cleared the flag, exactly as the legacy save did");
        placement.TabModuleId.Should().Be(31);
        placement.TabId.Should().Be(
            7,
            "the projection leaves the page alone, and the request named the page the placement is on");
        placement.ModuleId.Should().Be(0);
    }

    /// <summary>Applying an update that names a DIFFERENT page leaves the placement where it is.</summary>
    [Fact]
    public void ModuleApplyUpdate_NamingADifferentPageDoesNotReParentThePlacement()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        placement.TabId.Should().Be(7, "the fixture places the module on a page that is not the submitted one");

        ModuleMappings.ApplyUpdate(
            module,
            placement,
            new UpdateModuleRequest { TabId = 9, ModuleTitle = "Renamed Module" });

        placement.TabId.Should().Be(
            7,
            "the submitted page selects the placement and is never written onto it");
        module.ModuleTitle.Should().Be(
            "Renamed Module",
            "the rest of the projection still applies, so this is not a wholesale refusal to write");

        // The identity of the placement row itself is untouched either way: the key and the module it belongs
        // to are write-once at create.
        placement.TabModuleId.Should().Be(31);
        placement.ModuleId.Should().Be(0);
    }

    /// <summary>
    /// Zero selects a page like any other identifier and is still never written onto the placement.
    /// </summary>
    /// <remarks>
    /// Page identity seeds at zero - <c>Tabs.TabID</c> is an identity column whose first value is the first
    /// page of the installation - so a submitted zero is a legitimate page identifier and must never be
    /// read as "no page supplied".
    /// </remarks>
    [Fact]
    public void ModuleApplyUpdate_TreatsPageZeroLikeAnyOtherSelectorAndStillDoesNotWriteIt()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleMappings.ApplyUpdate(module, placement, new UpdateModuleRequest { TabId = 0 });

        placement.TabId.Should().Be(
            7,
            "zero is a real page identifier, and no page identifier is written onto an existing placement");
    }

    /// <summary>
    /// The update path falls back to the period already stored, which is not the create path's fallback.
    /// </summary>
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
    /// The update path writes a negative period through unchanged and leaves the stored pane alone.
    /// </summary>
    /// <remarks>
    /// The pane is not substituted here, because the update contract carries no pane member to substitute
    /// FOR - the column is excluded and its stored value is preserved.
    /// </remarks>
    [Fact]
    public void ModuleApplyUpdate_WritesANegativePeriodThroughAndLeavesTheStoredPaneAlone()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();
        string storedPane = placement.PaneName;

        ModuleMappings.ApplyUpdate(module, placement, new UpdateModuleRequest { CacheTime = -1 });

        placement.PaneName.Should().Be(storedPane);
        placement.CacheTime.Should().Be(-1);
    }

    /// <summary>Every module mapper refuses a null argument.</summary>
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

    /// <summary>The account list projection carries the account row plus the separately-resolved context.</summary>
    /// <remarks>
    /// The tenant, the postal address and the telephone number are all arguments rather than columns on the
    /// account row: an account is installation-wide, its membership is per tenant, and the address and
    /// telephone live in the profile value table under whichever definitions the tenant happens to have.
    /// </remarks>
    [Fact]
    public void UserToListItem_CarriesTheAccountPlusTheResolvedContext()
    {
        User user = FullUser();

        UserListItemDto dto = UserMappings.ToListItem(
            user,
            portalId: -1,
            address: "1 Measured Way",
            telephone: "555-0100",
            portalAdministratorId: null);

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
    /// The deletion capability reports what the service will actually do with a removal request: a host
    /// account is refused, and so is the account the tenant designates as its administrator.
    /// </summary>
    /// <remarks>
    /// The legacy grid decided the same thing in markup. <c>Website/admin/Users/Users.ascx.vb:L691-L692</c>
    /// read <c>delImage.Visible = Not (user.UserID = PortalSettings.AdministratorId) AndAlso Not
    /// (user.UserID = Me.UserId And user.IsSuperUser)</c>. The first arm is reproduced exactly.
    /// </remarks>
    [Fact]
    public void UserToListItem_WithholdsDeletionFromTheHostAndFromTheDesignatedAdministrator()
    {
        User host = FullUser();
        host.IsSuperUser.Should().BeTrue("the shared fixture is a host account");

        UserMappings.ToListItem(host, -1, null, null, portalAdministratorId: null)
            .CanDelete.Should().BeFalse("the service refuses to remove a host account");

        User member = FullUser();
        member.IsSuperUser = false;

        UserMappings.ToListItem(member, -1, null, null, portalAdministratorId: member.UserId)
            .CanDelete.Should().BeFalse("the tenant designates this account as its administrator");

        UserMappings.ToListItem(member, -1, null, null, portalAdministratorId: 9)
            .CanDelete.Should().BeTrue("an ordinary account of the tenant may be removed");
    }

    /// <summary>
    /// A tenant that designates no administrator, or records the legacy absent marker in that column,
    /// protects no account at all.
    /// </summary>
    /// <param name="designated">The designated administrator the tenant reports.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void UserToListItem_ProtectsNobodyWhenTheTenantDesignatesNoAdministrator(int? designated)
    {
        User member = FullUser();
        member.IsSuperUser = false;

        UserMappings.ToListItem(member, -1, null, null, designated)
            .CanDelete.Should().BeTrue();
    }

    /// <summary>An unread credential fact is reported as false and an absent address as empty.</summary>
    [Fact]
    public void UserToListItem_ReportsUnreadCredentialFactsAsFalse()
    {
        User user = FullUser();
        user.IsApproved = null;
        user.IsOnline = null;
        user.IsLockedOut = null;
        user.Email = null;

        UserListItemDto dto = UserMappings.ToListItem(user, -1, null, null, null);

        dto.IsApproved.Should().BeFalse();
        dto.IsOnline.Should().BeFalse();
        dto.IsLockedOut.Should().BeFalse();
        dto.Email.Should().BeEmpty();
        dto.Address.Should().BeNull();
        dto.Telephone.Should().BeNull();
    }

    /// <summary>The account detail projection carries the account row plus the resolved role names.</summary>
    /// <summary>
    /// The detail projection publishes the SAME removal capability as the list projection, for every
    /// account the rule distinguishes.
    /// </summary>
    [Fact]
    public void UserToDetail_PublishesTheSameRemovalCapabilityAsTheList()
    {
        // ⚠ FullUser() DELIBERATELY CARRIES IsSuperUser = true so that every mapped column holds a
        // distinguishable value, so an "ordinary" account has to clear it explicitly. Leaving it set is how
        // the first draft of this test failed: it asserted a host account was removable.
        User ordinary = FullUser();
        ordinary.IsSuperUser = false;

        User superUser = FullUser();
        superUser.IsSuperUser = true;

        // An ordinary account in a tenant that designates somebody else may be removed.
        UserMappings.ToDetail(ordinary, -1, [], portalAdministratorId: 99).CanDelete.Should().BeTrue();

        // A super user may not, whoever the tenant designates.
        UserMappings.ToDetail(superUser, -1, [], portalAdministratorId: 99).CanDelete.Should().BeFalse();

        // ⚠ THE CLAUSE THE CLIENT COULD NOT SEE: the designated administrator may not be removed even
        // though they are NOT a super user. This is the exact case the two screens disagreed about.
        UserMappings
            .ToDetail(ordinary, -1, [], portalAdministratorId: ordinary.UserId)
            .CanDelete.Should()
            .BeFalse();

        // ⚠ A TENANT THAT DESIGNATES NOBODY PROTECTS NOBODY, and the legacy spelling of a missing integer
        // is MINUS ONE rather than null - so neither may be read as "matches this row". Users.UserID seeds
        // IDENTITY(1, 1), so -1 can never be a real account key.
        UserMappings.ToDetail(ordinary, -1, [], portalAdministratorId: null).CanDelete.Should().BeTrue();
        UserMappings.ToDetail(ordinary, -1, [], portalAdministratorId: -1).CanDelete.Should().BeTrue();

        // AGREEMENT with the list projection, asserted directly for every distinguished case.
        foreach (int? designation in new int?[] { null, -1, 99, ordinary.UserId })
        {
            UserMappings
                .ToDetail(ordinary, -1, [], designation)
                .CanDelete.Should()
                .Be(UserMappings.ToListItem(ordinary, -1, null, null, designation).CanDelete);
        }
    }

    [Fact]
    public void UserToDetail_CarriesTheAccountPlusTheResolvedRoles()
    {
        User user = FullUser();
        string[] roles = ["Administrators", "Registered Users"];

        UserDetailDto dto = UserMappings.ToDetail(user, portalId: -1, roles, portalAdministratorId: null);

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

        UserDetailDto dto = UserMappings.ToDetail(user, -1, [], null);

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
            defaultVisibility: 2,
            displayVisibilityEnabled: true);

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
    /// single column aliased <c>PropertyValue</c> computed as "case when (PropertyValue Is Null) then
    /// PropertyText else PropertyValue end" (<c>04.00.04.SqlDataProvider</c> line 1592, identical at
    /// <c>03.02.03</c> line 1533), so the bounded <c>nvarchar(3750)</c> column wins and the <c>ntext</c>
    /// column is the fallback.
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

    /// <summary>A repeated stored value for one definition resolves to the row read last.</summary>
    [Fact]
    public void UserToProfile_ResolvesARepeatedValueToTheLastRow()
    {
        UserProfileValue first = NewProfileValue("first", null);
        UserProfileValue second = NewProfileValue("second", null);

        UserProfileDto dto = UserMappings.ToProfile(1, [FullDefinition()], [first, second], 2, true);

        dto.Properties.Should().HaveCount(1);
        dto.Properties[0].PropertyValue.Should().Be("second");
    }

    /// <summary>A profile with no definitions at all projects no properties rather than failing.</summary>
    [Fact]
    public void UserToProfile_ProjectsNothingWhenTheTenantDefinesNoProperties()
    {
        UserProfileDto dto = UserMappings.ToProfile(1, [], [], 2, true);

        dto.UserId.Should().Be(1);
        dto.Properties.Should().BeEmpty();
    }

    /// <summary>
    /// The tenant's visibility-affordance decision is carried through the projection in both states, and is
    /// not derived from the default visibility that sits beside it.
    /// </summary>
    [Fact]
    public void UserToProfile_CarriesTheTenantsVisibilityAffordanceDecision()
    {
        UserProfileDto enabled = UserMappings.ToProfile(
            1,
            [FullDefinition()],
            [NewProfileValue("555-0100", null)],
            2,
            true);

        UserProfileDto disabled = UserMappings.ToProfile(
            1,
            [FullDefinition()],
            [NewProfileValue("555-0100", null)],
            2,
            false);

        enabled.DisplayVisibilityEnabled.Should().BeTrue();
        disabled.DisplayVisibilityEnabled.Should().BeFalse();

        // ⚠ THE AFFORDANCE DECISION MUST NOT DISTURB THE VALUES. A recorded value keeps the visibility it
        // was stored with - `NewProfileValue` stores 1 - whether or not the tenant permits the holder to
        // change it.
        disabled.Properties.Should().OnlyContain(
            property => property.Visibility == 1,
            "the recorded value keeps the visibility it was stored with, whatever the affordance decision is");
    }

    /// <summary>A new account computes its display name from the two name parts when none is supplied.</summary>
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

    /// <summary>A supplied display name is kept exactly as submitted.</summary>
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

    /// <summary>A new account is created unprivileged and is not asked to change its password.</summary>
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

    /// <summary>Applying an account update leaves identity, sign-in name and privilege alone.</summary>
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
    /// A new profile definition stores the scope it is given - including the first tenant key an
    /// installation issues - starts undeleted, and floors a negative length.
    /// </summary>
    [Fact]
    public void ProfileDefinitionToNewDefinition_StoresTheScopeGivenAndFloorsTheLength()
    {
        CreateProfilePropertyDefinitionRequest request = new()
        {
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
        };

        ProfilePropertyDefinition definition = UserMappings.ToNewDefinition(portalId: -1, request);

        definition.PortalId.Should().Be(
            -1,
            "-1 is the first key IDENTITY(-1, 1) issues, so the declaration belongs to that tenant and "
            + "must not be filed into the SQL-null host scope");
        definition.ModuleDefinitionId.Should().Be(4);
        definition.IsDeleted.Should().BeFalse();
        definition.DataType.Should().Be(349);
        definition.DefaultValue.Should().Be("a default");
        definition.PropertyCategory.Should().Be("Contact Information");
        definition.PropertyName.Should().Be("Telephone");
        definition.Length.Should().Be(0, "a negative width is floored rather than stored");
        definition.IsRequired.Should().BeTrue();

        UserMappings.ToNewDefinition(portalId: 0, request).PortalId.Should().Be(
            0,
            "0 is the first key an ordinary installation issues and is carried through like any other");
        UserMappings.ToNewDefinition(portalId: null, request).PortalId.Should().BeNull(
            "the SQL-null host scope is expressed by passing a null and by nothing else");
        definition.ValidationExpression.Should().Be(@"^\d+$");
        definition.ViewOrder.Should().Be(3);
        definition.IsVisible.Should().BeTrue();
        definition.PropertyDefinitionId.Should().Be(0, "the store issues the key, not the caller");
    }

    /// <summary>Applying a definition update leaves ownership, the module association and deletion alone.</summary>
    [Fact]
    public void ProfileDefinitionApplyUpdate_LeavesOwnershipAndAssociationAlone()
    {
        ProfilePropertyDefinition definition = FullDefinition();

        UserMappings.ApplyDefinitionUpdate(definition, new UpdateProfilePropertyDefinitionRequest
        {
            DataType = 350,
            DefaultValue = null,
            PropertyCategory = "Renamed Category",
            PropertyName = "Mobile",
            Length = 20,
            Required = false,
            ValidationExpression = null,
            ViewOrder = 7,
            Visible = false,
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
            + "untouched and its contract carries no member for it");
        definition.IsDeleted.Should().BeFalse("deletion is its own operation");
    }

    /// <summary>Every account mapper refuses a null argument.</summary>
    [Fact]
    public void UserMappers_RefuseNullArguments()
    {
        User user = FullUser();
        ProfilePropertyDefinition definition = FullDefinition();

        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToListItem(null!, -1, null, null, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToDetail(null!, -1, [], null); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToDetail(user, -1, null!, null); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToDto(null!, 0); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToProfile(1, null!, [], 0, true); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToProfile(1, [], null!, 0, true); });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToNewUser(null!); });
        Assert.Throws<ArgumentNullException>(() => UserMappings.ApplyUpdate(null!, new UpdateUserRequest()));
        Assert.Throws<ArgumentNullException>(() => UserMappings.ApplyUpdate(user, null!));
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToNewDefinition(-1, null!); });
        Assert.Throws<ArgumentNullException>(
            () => UserMappings.ApplyDefinitionUpdate(null!, new UpdateProfilePropertyDefinitionRequest()));
        Assert.Throws<ArgumentNullException>(() => UserMappings.ApplyDefinitionUpdate(definition, null!));
    }

    // Sentinel, enum and determinism boundary

    /// <summary>
    /// A portal whose handle has never been issued projects <see cref="Guid.Empty"/> as a value on both
    /// read projections rather than reporting the handle as absent.
    /// </summary>
    /// <remarks>
    /// <c>Guid.Empty</c> was the legacy stand-in for an absent globally unique identifier, and both
    /// projections declare the member as a non-nullable <c>Guid</c>. There is therefore nowhere for an
    /// "absent" answer to go, and that is the point: the empty handle travels as itself.
    /// </remarks>
    [Fact]
    public void PortalReadProjections_CarryAnUnissuedHandleAsAValueRatherThanAsAbsent()
    {
        Portal portal = FullPortal();
        portal.PortalGuid = Guid.Empty;

        PortalDetailDto detail = PortalMappings.ToDetail(portal, 0, 0, null, null, null, null, null);
        PortalSettingsDto settings = PortalMappings.ToSettings(portal);

        detail.Guid.Should().Be(Guid.Empty, "the legacy stand-in for an absent handle is a real value here");
        settings.Guid.Should().Be(Guid.Empty);

        // And an issued handle still travels unchanged, so the empty case is not being special-cased.
        Guid issued = new("57ad7180-c5e7-49f5-b282-c6475cdb7ee7");
        portal.PortalGuid = issued;

        PortalMappings.ToDetail(portal, 0, 0, null, null, null, null, null).Guid.Should().Be(issued);
        PortalMappings.ToSettings(portal).Guid.Should().Be(issued);
    }

    /// <summary>
    /// The twenty-seven values this suite pushes through the settings request are pairwise distinct, so a
    /// mapper that assigned two of them to each other's field would fail rather than pass.
    /// </summary>
    /// <remarks>
    /// The legacy entry point was <c>PortalController.UpdatePortalInfo</c>, twenty-seven positional
    /// parameters wide, and seven runs of it are adjacent parameters of the SAME type: three strings at
    /// positions 2-4, two integers at 6-7, two doubles at 10-11, two integers at 12-13, three strings at
    /// 14-16 - one of which is the payment secret - three strings at 17-19, and a four-wide run of tab
    /// identifiers at 21-24.
    /// </remarks>
    [Fact]
    public void PortalUpdateRequest_IsSeededWithTwentySevenDistinctValues()
    {
        UpdatePortalRequest request = new()
        {
            PortalId = -1,
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
            ProcessorCredentialReference = "secret://processor/distinct",
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

        string[] seeded =
        [
            request.PortalId.ToString(CultureInfo.InvariantCulture),
            request.PortalName!,
            request.LogoFile!,
            request.FooterText!,
            request.ExpiryDate!.Value.ToString("O", CultureInfo.InvariantCulture),
            request.UserRegistration.ToString(),
            request.BannerAdvertising.ToString(),
            request.Currency!,
            request.AdministratorId!.Value.ToString(CultureInfo.InvariantCulture),
            request.HostFee!.Value.ToString(CultureInfo.InvariantCulture),
            request.HostSpace!.Value.ToString(CultureInfo.InvariantCulture),
            request.PageQuota!.Value.ToString(CultureInfo.InvariantCulture),
            request.UserQuota!.Value.ToString(CultureInfo.InvariantCulture),
            request.PaymentProcessor!,
            request.ProcessorUserId!,
            request.ProcessorCredentialReference!,
            request.Description!,
            request.KeyWords!,
            request.BackgroundFile!,
            request.SiteLogHistory!.Value.ToString(CultureInfo.InvariantCulture),
            request.SplashTabId!.Value.ToString(CultureInfo.InvariantCulture),
            request.HomeTabId!.Value.ToString(CultureInfo.InvariantCulture),
            request.LoginTabId!.Value.ToString(CultureInfo.InvariantCulture),
            request.UserTabId!.Value.ToString(CultureInfo.InvariantCulture),
            request.DefaultLanguage!,
            request.TimeZoneOffset!.Value.ToString(CultureInfo.InvariantCulture),
            request.HomeDirectory!,
        ];

        seeded.Should().HaveCount(
            27,
            "the legacy signature took twenty-seven parameters and the request must carry all of them");
        seeded.Should().OnlyHaveUniqueItems(
            "a transposition inside one of the seven same-typed runs is invisible to the compiler, so "
            + "the seeds are what make it visible to the suite");

        // TWENTY-EIGHT: the twenty-seven legacy parameters seeded above, plus the optimistic-concurrency
        // token.
        typeof(UpdatePortalRequest).GetProperties().Should().HaveCount(
            28,
            "the settings contract mirrors the legacy parameter list exactly - none dropped, and the only "
            + "addition is the concurrency token, which describes a revision rather than a portal attribute");
    }

    /// <summary>
    /// A portal column holding the empty string projects the empty string, and a column holding null
    /// projects null; the two are never conflated.
    /// </summary>
    /// <remarks>
    /// The target keeps them apart. A null means the column was never populated and an empty string means
    /// it was populated with nothing, and the read projections pass both through unchanged rather than
    /// normalising one into the other.
    /// </remarks>
    [Fact]
    public void PortalReadProjections_KeepAnEmptyColumnApartFromAnAbsentOne()
    {
        Portal blank = FullPortal();
        blank.Description = string.Empty;
        blank.KeyWords = string.Empty;
        blank.FooterText = string.Empty;
        blank.LogoFile = string.Empty;
        blank.PaymentProcessor = string.Empty;
        blank.ProcessorUserId = string.Empty;

        PortalDetailDto emptied = PortalMappings.ToDetail(blank, 0, 0, null, null, null, null, null);

        emptied.Description.Should().Be(string.Empty).And.NotBeNull();
        emptied.KeyWords.Should().Be(string.Empty);
        emptied.FooterText.Should().Be(string.Empty);
        emptied.LogoFile.Should().Be(string.Empty);
        emptied.PaymentProcessor.Should().Be(string.Empty);
        emptied.ProcessorUserId.Should().Be(string.Empty);

        Portal absent = FullPortal();
        absent.Description = null;
        absent.KeyWords = null;
        absent.FooterText = null;
        absent.LogoFile = null;
        absent.PaymentProcessor = null;
        absent.ProcessorUserId = null;

        PortalDetailDto nulled = PortalMappings.ToDetail(absent, 0, 0, null, null, null, null, null);

        nulled.Description.Should().BeNull("an unpopulated column is not an empty one");
        nulled.KeyWords.Should().BeNull();
        nulled.FooterText.Should().BeNull();
        nulled.LogoFile.Should().BeNull();
        nulled.PaymentProcessor.Should().BeNull();
        nulled.ProcessorUserId.Should().BeNull();
    }

    /// <summary>
    /// An expiry date of <see cref="DateTime.MinValue"/> projects that instant as a value and stays
    /// distinguishable from an absent expiry.
    /// </summary>
    [Fact]
    public void PortalReadProjections_CarryTheDateStandInAsAValueDistinctFromAbsent()
    {
        Portal atStandIn = FullPortal();
        atStandIn.ExpiryDate = DateTime.MinValue;

        PortalMappings.ToDetail(atStandIn, 0, 0, null, null, null, null, null)
            .ExpiryDate.Should().Be(DateTime.MinValue);
        PortalMappings.ToSettings(atStandIn).ExpiryDate.Should().Be(DateTime.MinValue);
        PortalMappings.ToListItem(atStandIn, [], 0, 0).ExpiryDate.Should().Be(DateTime.MinValue);

        Portal neverExpires = FullPortal();
        neverExpires.ExpiryDate = null;

        PortalMappings.ToDetail(neverExpires, 0, 0, null, null, null, null, null).ExpiryDate.Should().BeNull();
        PortalMappings.ToSettings(neverExpires).ExpiryDate.Should().BeNull();
        PortalMappings.ToListItem(neverExpires, [], 0, 0).ExpiryDate.Should().BeNull();

        // The time component survives too, which is the half of the legacy defect that lost data.
        Portal withTime = FullPortal();
        withTime.ExpiryDate = DateTime.MinValue.AddHours(9).AddMinutes(30);

        PortalMappings.ToDetail(withTime, 0, 0, null, null, null, null, null)
            .ExpiryDate.Should().Be(
                DateTime.MinValue.AddHours(9).AddMinutes(30),
                "the legacy write path compared only the date part and discarded the time");
    }

    /// <summary>
    /// A retention count of 255 travels as the number 255, because the legacy stand-in for an absent byte
    /// is the only one that is a plausible real value.
    /// </summary>
    /// <remarks>
    /// 255 is a number. The projections carry it unchanged in both directions, and the absent case is
    /// expressed as null, which the store can hold because the column is nullable.
    /// </remarks>
    [Fact]
    public void PortalMappers_TreatTheByteStandInAsAnOrdinaryCount()
    {
        Portal portal = FullPortal();
        portal.SiteLogHistory = 255;

        PortalMappings.ToDetail(portal, 0, 0, null, null, null, null, null).SiteLogHistory.Should().Be(255);
        PortalMappings.ToSettings(portal).SiteLogHistory.Should().Be(255);

        UpdatePortalRequest request = new() { SiteLogHistory = 255 };
        PortalMappings.ApplyUpdate(portal, request);
        portal.SiteLogHistory.Should().Be(255, "the byte stand-in is retained on the way in as well");

        UpdatePortalRequest cleared = new() { SiteLogHistory = null };
        PortalMappings.ApplyUpdate(portal, cleared);
        portal.SiteLogHistory.Should().BeNull("absent is null here, never 255 and never zero");
    }

    /// <summary>
    /// A root page projects an absent parent, and the page whose parent is the tab identified by zero
    /// projects that zero; the two are never conflated.
    /// </summary>
    /// <remarks>
    /// This is one of only three places in the whole migration where the legacy -1 legitimately becomes
    /// null, because a root page genuinely has no parent. The trap is the other half: <c>Tabs.TabID</c> is
    /// seeded <c>IDENTITY(0,1)</c>, so zero identifies the first page ever created and is a perfectly good
    /// parent.
    /// </remarks>
    [Fact]
    public void TabMappers_DistinguishARootPageFromThePageParentedAtZero()
    {
        Tab root = FullTab();
        root.ParentId = null;

        TabMappings.ToDetail(root, false).ParentId.Should().BeNull();
        TabMappings.ToListItem(root, false).ParentId.Should().BeNull();

        Tab underFirstPage = FullTab();
        underFirstPage.ParentId = 0;

        TabMappings.ToDetail(underFirstPage, false).ParentId.Should().Be(
            0,
            "Tabs.TabID is seeded IDENTITY(0,1), so zero is the first page rather than no page");
        TabMappings.ToListItem(underFirstPage, false).ParentId.Should().Be(0);

        // And the same distinction survives the write path in both directions.
        Tab moved = FullTab();
        TabMappings.ApplyUpdate(moved, new UpdateTabRequest { ParentId = 0 });
        moved.ParentId.Should().Be(0);

        TabMappings.ApplyUpdate(moved, new UpdateTabRequest { ParentId = null });
        moved.ParentId.Should().BeNull("promotion to the root is expressible and is not a no-op");
    }

    /// <summary>
    /// The page projections keep an empty column apart from an absent one, carry the byte stand-in as an
    /// ordinary interval and carry the date stand-in as an ordinary instant.
    /// </summary>
    [Fact]
    public void TabMappers_CarryEveryStandInAsAValueRatherThanAsAbsent()
    {
        Tab tab = FullTab();
        tab.Title = string.Empty;
        tab.Description = string.Empty;
        tab.Keywords = string.Empty;
        tab.Url = string.Empty;
        tab.RefreshInterval = 255;
        tab.StartDate = DateTime.MinValue;
        tab.EndDate = null;

        TabDetailDto detail = TabMappings.ToDetail(tab, false);

        detail.Title.Should().Be(string.Empty);
        detail.Description.Should().Be(string.Empty);
        detail.Keywords.Should().Be(string.Empty, "an empty keyword list is not an absent one");
        detail.Url.Should().Be(string.Empty);
        detail.RefreshInterval.Should().Be(255);
        detail.StartDate.Should().Be(DateTime.MinValue);
        detail.EndDate.Should().BeNull("absence is null, and only null");

        Tab absent = FullTab();
        absent.Title = null;
        absent.Description = null;
        absent.Keywords = null;
        absent.Url = null;
        absent.RefreshInterval = null;
        absent.StartDate = null;

        TabDetailDto nulled = TabMappings.ToDetail(absent, false);

        nulled.Title.Should().BeNull();
        nulled.Description.Should().BeNull();
        nulled.Keywords.Should().BeNull();
        nulled.Url.Should().BeNull();
        nulled.RefreshInterval.Should().BeNull();
        nulled.StartDate.Should().BeNull();
    }

    /// <summary>
    /// Every one of the six billing codes round-trips through the create path, the update path and both
    /// read projections, on the recurrence field and on the trial field alike.
    /// </summary>
    /// <remarks>
    /// The codes are the single characters the legacy <c>RoleController</c> switched on, and there are SIX
    /// of them rather than the four that a reading of the recurrence arithmetic alone suggests: <c>N</c>
    /// and <c>O</c> carry no arithmetic, which is exactly why they are easy to miss.
    /// </remarks>
    /// <param name="code">The billing code under test.</param>
    [Theory]
    [InlineData(BillingFrequency.None)]
    [InlineData(BillingFrequency.OneTime)]
    [InlineData(BillingFrequency.Day)]
    [InlineData(BillingFrequency.Week)]
    [InlineData(BillingFrequency.Month)]
    [InlineData(BillingFrequency.Year)]
    public void RoleMappers_RoundTripEveryBillingCodeOnBothRecurrenceFields(BillingFrequency code)
    {
        CreateRoleRequest create = new()
        {
            RoleName = "Coded Members",
            BillingFrequency = code,
            TrialFrequency = code,
        };

        Role created = RoleMappings.ToNewRole(-1, create);

        created.BillingFrequency.Should().Be(code);
        created.TrialFrequency.Should().Be(code, "the trial field is the same closed set as the recurrence field");

        RoleMappings.ToDetail(created).BillingFrequency.Should().Be(code);
        RoleMappings.ToDetail(created).TrialFrequency.Should().Be(code);
        RoleMappings.ToListItem(created).BillingFrequency.Should().Be(code);
        RoleMappings.ToListItem(created).TrialFrequency.Should().Be(code);

        Role stored = FullRole();
        RoleMappings.ApplyUpdate(
            stored,
            new UpdateRoleRequest
            {
                RoleName = "Coded Members",
                BillingFrequency = code,
                TrialFrequency = code,
            });

        stored.BillingFrequency.Should().Be(code);
        stored.TrialFrequency.Should().Be(code);
    }

    /// <summary>
    /// The billing code is carried as the legacy character, not as the member name and not as the ordinal
    /// position of the member.
    /// </summary>
    /// <remarks>
    /// The column is <c>char(1)</c> and the stored letters are <c>N</c>, <c>O</c>, <c>D</c>, <c>W</c>,
    /// <c>M</c> and <c>Y</c>, so the enum is declared over <c>ushort</c> with each member VALUED at its
    /// character. That makes the underlying value the wire value and the persistence value at once, and it
    /// is why the members may be renamed but never renumbered.
    /// </remarks>
    [Fact]
    public void RoleBillingCode_IsValuedAtTheLegacyCharacterRatherThanAnOrdinal()
    {
        ((char)BillingFrequency.None).Should().Be('N');
        ((char)BillingFrequency.OneTime).Should().Be('O');
        ((char)BillingFrequency.Day).Should().Be('D');
        ((char)BillingFrequency.Week).Should().Be('W');
        ((char)BillingFrequency.Month).Should().Be('M');
        ((char)BillingFrequency.Year).Should().Be('Y');

        Enum.GetUnderlyingType(typeof(BillingFrequency)).Should().Be(
            typeof(ushort),
            "the members are valued at their characters, which will not fit an ordinal-shaped enum");

        Enum.GetValues<BillingFrequency>().Should().HaveCount(6, "the lookup table seeds exactly six codes");

        // The ordinal position of a member is emphatically NOT its value: Day is declared third and
        // valued 68, so anything that persisted a position would corrupt every row it touched.
        ((ushort)BillingFrequency.Day).Should().Be(68);
        ((ushort)BillingFrequency.Month).Should().Be(77);
        ((ushort)BillingFrequency.None).Should().Be(78);
    }

    /// <summary>
    /// A role that never expires carries the <c>N</c> code, and a role whose recurrence was never chosen
    /// carries null; the two are never conflated.
    /// </summary>
    [Fact]
    public void RoleMappers_DistinguishTheNonRecurringCodeFromAnAbsentOne()
    {
        Role neverExpires = RoleMappings.ToNewRole(
            -1,
            new CreateRoleRequest { RoleName = "Lifetime", BillingFrequency = BillingFrequency.None });

        neverExpires.BillingFrequency.Should().Be(BillingFrequency.None);
        RoleMappings.ToDetail(neverExpires).BillingFrequency.Should().Be(BillingFrequency.None);

        Role unchosen = RoleMappings.ToNewRole(
            -1,
            new CreateRoleRequest { RoleName = "Unpriced", BillingFrequency = null });

        unchosen.BillingFrequency.Should().BeNull("no recurrence was chosen, which is not the same as none");
        RoleMappings.ToDetail(unchosen).BillingFrequency.Should().BeNull();

        // Clearing a stored code is expressible, so the absent state is reachable and not merely initial.
        Role stored = FullRole();
        stored.BillingFrequency.Should().Be(BillingFrequency.Month);

        RoleMappings.ApplyUpdate(stored, new UpdateRoleRequest { RoleName = stored.RoleName });
        stored.BillingFrequency.Should().BeNull();
        stored.TrialFrequency.Should().BeNull();
    }

    /// <summary>
    /// An unpriced role keeps both charges absent and a free role keeps both charges at zero; the two are
    /// never conflated in either direction.
    /// </summary>
    [Fact]
    public void RoleMappers_DistinguishAnUndecidedChargeFromAFreeOne()
    {
        Role undecided = RoleMappings.ToNewRole(
            -1,
            new CreateRoleRequest { RoleName = "Undecided", ServiceFee = null, TrialFee = null });

        undecided.ServiceFee.Should().BeNull();
        undecided.TrialFee.Should().BeNull();
        RoleMappings.ToDetail(undecided).ServiceFee.Should().BeNull();
        RoleMappings.ToDetail(undecided).TrialFee.Should().BeNull();
        RoleMappings.ToListItem(undecided).ServiceFee.Should().BeNull();
        RoleMappings.ToListItem(undecided).TrialFee.Should().BeNull();

        Role free = RoleMappings.ToNewRole(
            -1,
            new CreateRoleRequest { RoleName = "Free", ServiceFee = 0m, TrialFee = 0m });

        free.ServiceFee.Should().Be(0m, "a charge of nothing is a decided charge");
        free.TrialFee.Should().Be(0m);
        RoleMappings.ToDetail(free).ServiceFee.Should().Be(0m);
        RoleMappings.ToDetail(free).TrialFee.Should().Be(0m);

        // The periods behave the same way, and zero is a legitimate period rather than an absent one.
        Role zeroPeriods = RoleMappings.ToNewRole(
            -1,
            new CreateRoleRequest { RoleName = "Zeroed", BillingPeriod = 0, TrialPeriod = 0 });

        zeroPeriods.BillingPeriod.Should().Be(0);
        zeroPeriods.TrialPeriod.Should().Be(0);

        Role absentPeriods = RoleMappings.ToNewRole(-1, new CreateRoleRequest { RoleName = "Unbounded" });

        absentPeriods.BillingPeriod.Should().BeNull();
        absentPeriods.TrialPeriod.Should().BeNull();
    }

    /// <summary>
    /// The legacy stand-in for an undecided charge cannot survive the money-typed column, which is why
    /// absence is expressed as null instead.
    /// </summary>
    /// <remarks>
    /// First, <c>Single.MinValue</c> and <c>Double.MinValue</c> - the stand-ins for an absent charge - are
    /// around twenty orders of magnitude outside the decimal range, so they cannot even be REPRESENTED on
    /// the way to the column, let alone stored in it. Second, <c>Decimal.MinValue</c> is representable but
    /// is floored to zero by the charge floor, which would silently reinterpret "undecided" as "free".
    /// </remarks>
    [Fact]
    public void RoleCharges_CannotCarryTheLegacyUndecidedStandIn()
    {
        // Held in locals so the conversion happens at run time; as constants these would not compile,
        // which is itself the point being recorded.
        float singleStandIn = float.MinValue;
        double doubleStandIn = double.MinValue;

        FluentActions.Invoking(() => (decimal)singleStandIn).Should().Throw<OverflowException>(
            "Single.MinValue is about -3.4E+38 and the money-shaped decimal tops out near -7.9E+28");
        FluentActions.Invoking(() => (decimal)doubleStandIn).Should().Throw<OverflowException>();

        // Representable, but destroyed by the floor - so it cannot mean "undecided" either.
        PortalMappings.ClampFee(decimal.MinValue).Should().Be(
            0m,
            "the floor cannot tell an enormous negative from an ordinary one, which is precisely why "
            + "absence is carried as null rather than as a magic number");

        Role role = RoleMappings.ToNewRole(
            -1,
            new CreateRoleRequest { RoleName = "Floored", ServiceFee = decimal.MinValue });

        role.ServiceFee.Should().Be(0m);
        role.ServiceFee.Should().NotBeNull("a floored charge is decided, and that is the divergence");

        // The precision half of the mismatch, at the top of the money range.
        const decimal moneyCeiling = 922337203685477.5807m;
        decimal narrowed = (decimal)(float)moneyCeiling;

        narrowed.Should().NotBe(
            moneyCeiling,
            "a money value carries far more significant digits than a Single can hold, so nothing in "
            + "the target narrows one through the legacy shape");

        // A charge of ordinary size still survives the target's own decimal path untouched.
        RoleMappings.ToNewRole(-1, new CreateRoleRequest { RoleName = "Priced", ServiceFee = 19.9999m })
            .ServiceFee.Should().Be(19.9999m, "money holds exactly four decimal places");
    }

    /// <summary>
    /// Composing a membership anchors the role from the route, takes the member from the body, takes both
    /// bounds from the caller and starts the trial unused.
    /// </summary>
    /// <remarks>
    /// <c>UserRoles.UserRoleID</c> is seeded <c>IDENTITY(1,1)</c>, so a freshly composed membership
    /// legitimately carries zero until the store issues a key - unlike the portal, role, page and module
    /// keys, where zero is a real identifier.
    /// </remarks>
    [Fact]
    public void RoleToNewAssignment_AnchorsTheRoleAndRecordsBothBounds()
    {
        DateTime effective = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        DateTime expiry = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        UserRole assignment = RoleMappings.ToNewAssignment(
            42,
            new RoleAssignmentRequest { UserId = 7, NotifyUser = true },
            effective,
            expiry);

        assignment.RoleId.Should().Be(42, "the role is addressed by the route, never by the body");
        assignment.UserId.Should().Be(7);
        assignment.EffectiveDate.Should().Be(effective);
        assignment.ExpiryDate.Should().Be(expiry);
        assignment.IsTrialUsed.Should().BeFalse("a membership that has just been granted has used no trial");
        assignment.UserRoleId.Should().Be(0, "the key is the store's to issue");

        // The notification flag is a service concern and has no column, so it must not reach the row.
        typeof(UserRole).GetProperty("NotifyUser").Should().BeNull();
    }

    /// <summary>
    /// A membership granted without bounds carries both bounds absent, and the caller's bounds are taken
    /// verbatim even when they are the legacy date stand-in.
    /// </summary>
    /// <remarks>
    /// The legacy auto-assignment path passed <c>Null.NullDate</c> for BOTH bounds, so an unbounded
    /// membership was stored as two instants of 0001-01-01 rather than as two nulls. The target records
    /// absence as null, and this test pins the consequence: the stand-in is carried as an ordinary instant,
    /// so a caller that hands one over gets one back rather than getting null.
    /// </remarks>
    [Fact]
    public void RoleToNewAssignment_KeepsAnUnboundedMembershipUnboundedAndTakesTheStandInVerbatim()
    {
        UserRole unbounded = RoleMappings.ToNewAssignment(
            42,
            new RoleAssignmentRequest { UserId = 7 },
            effectiveDate: null,
            expiryDate: null);

        unbounded.EffectiveDate.Should().BeNull();
        unbounded.ExpiryDate.Should().BeNull();

        UserRole atStandIn = RoleMappings.ToNewAssignment(
            42,
            new RoleAssignmentRequest { UserId = 7 },
            effectiveDate: DateTime.MinValue,
            expiryDate: DateTime.MinValue);

        atStandIn.EffectiveDate.Should().Be(DateTime.MinValue, "the mapper records what it is given");
        atStandIn.ExpiryDate.Should().Be(DateTime.MinValue);
        atStandIn.EffectiveDate.Should().NotBeNull("the stand-in is a value, not an absence");
    }

    /// <summary>
    /// Amending a membership moves both bounds and leaves the member, the role, the key and the trial flag
    /// exactly where they were.
    /// </summary>
    [Fact]
    public void RoleApplyAssignmentUpdate_MovesBothBoundsAndLeavesTheMembershipAlone()
    {
        UserRole assignment = new()
        {
            UserRoleId = 900,
            UserId = 7,
            RoleId = 42,
            IsTrialUsed = true,
            EffectiveDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpiryDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        DateTime movedEffective = new(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        DateTime movedExpiry = new(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);

        RoleMappings.ApplyAssignmentUpdate(assignment, movedEffective, movedExpiry);

        assignment.EffectiveDate.Should().Be(movedEffective);
        assignment.ExpiryDate.Should().Be(movedExpiry);

        assignment.UserRoleId.Should().Be(900, "an amendment never moves the row");
        assignment.UserId.Should().Be(7, "an amendment never reassigns the membership to another member");
        assignment.RoleId.Should().Be(42, "nor to another role");
        assignment.IsTrialUsed.Should().BeTrue(
            "whether the trial has been consumed is a fact about the past and is not part of an amendment");

        // Clearing both bounds is expressible, so an amendment can open a membership up again.
        RoleMappings.ApplyAssignmentUpdate(assignment, null, null);

        assignment.EffectiveDate.Should().BeNull();
        assignment.ExpiryDate.Should().BeNull();
        assignment.IsTrialUsed.Should().BeTrue();
    }

    /// <summary>
    /// The bounds a membership mapper writes are the bounds its status is read from, and the reading needs
    /// no clock beyond the instant the caller supplies.
    /// </summary>
    /// <remarks>
    /// Status is computed rather than stored - it appears in no column and on no response contract - so
    /// what this test guards is the pairing: the mapper records two bounds, and the domain answers from
    /// those two bounds and an instant handed in. Fixing the instant is what makes every case below
    /// deterministic; a status rule that read a clock could not be asserted at a boundary at all.
    /// </remarks>
    [Fact]
    public void RoleAssignmentBounds_DriveTheStatusRuleFromTheInstantTheCallerSupplies()
    {
        DateTime asOfUtc = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        RoleAssignmentRequest member = new() { UserId = 7 };

        RoleMappings.ToNewAssignment(42, member, asOfUtc.AddDays(-30), asOfUtc.AddDays(30))
            .GetStatus(asOfUtc).Should().Be(RoleStatus.Active, "the instant falls inside both bounds");

        RoleMappings.ToNewAssignment(42, member, asOfUtc.AddDays(1), null)
            .GetStatus(asOfUtc).Should().Be(RoleStatus.Pending, "granted, but not yet in force");

        RoleMappings.ToNewAssignment(42, member, null, asOfUtc.AddDays(-1))
            .GetStatus(asOfUtc).Should().Be(RoleStatus.Expired, "the bound has lapsed");

        RoleMappings.ToNewAssignment(42, member, null, null)
            .GetStatus(asOfUtc).Should().Be(RoleStatus.Active, "bounded on neither side");

        // Equality at either bound is inclusive, so a membership is in force on the day it starts and on
        // the day it ends rather than falling into a one-day gap at each edge.
        RoleMappings.ToNewAssignment(42, member, asOfUtc, null)
            .GetStatus(asOfUtc).Should().Be(RoleStatus.Active);
        RoleMappings.ToNewAssignment(42, member, null, asOfUtc)
            .GetStatus(asOfUtc).Should().Be(RoleStatus.Active);

        // The far-future bound an OneTime role was given reads as active unaided.
        RoleMappings.ToNewAssignment(42, member, null, new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc))
            .GetStatus(asOfUtc).Should().Be(RoleStatus.Active);

        // The stand-in bound a None role was given does NOT, which is the divergence recorded above.
        RoleMappings.ToNewAssignment(42, member, null, DateTime.MinValue)
            .GetStatus(asOfUtc).Should().Be(
                RoleStatus.Expired,
                "absence is null and only null, so the legacy stand-in reads as a lapsed bound and has "
                + "to be normalised away before a legacy row becomes a membership");

        // An effective bound at the stand-in is harmless, because a past instant cannot be pending.
        RoleMappings.ToNewAssignment(42, member, DateTime.MinValue, null)
            .GetStatus(asOfUtc).Should().Be(RoleStatus.Active);
    }

    /// <summary>
    /// A role column holding the empty string projects the empty string, and the byte stand-in travels as
    /// an ordinary period.
    /// </summary>
    [Fact]
    public void RoleMappers_KeepAnEmptyColumnApartFromAnAbsentOneAndCarryTheByteStandIn()
    {
        Role emptied = RoleMappings.ToNewRole(
            -1,
            new CreateRoleRequest
            {
                RoleName = "Coded",
                Description = string.Empty,
                RsvpCode = string.Empty,
                IconFile = string.Empty,
                TrialPeriod = 255,
            });

        emptied.Description.Should().Be(string.Empty);
        emptied.RsvpCode.Should().Be(string.Empty, "an empty invitation code is not an absent one");
        emptied.IconFile.Should().Be(string.Empty);
        emptied.TrialPeriod.Should().Be(255);

        RoleDetailDto detail = RoleMappings.ToDetail(emptied);

        detail.Description.Should().Be(string.Empty);
        detail.RsvpCode.Should().Be(string.Empty);
        detail.IconFile.Should().Be(string.Empty);
        detail.TrialPeriod.Should().Be(255);

        Role absent = RoleMappings.ToNewRole(-1, new CreateRoleRequest { RoleName = "Uncoded" });

        absent.Description.Should().BeNull();
        absent.RsvpCode.Should().BeNull();
        absent.IconFile.Should().BeNull();
        absent.TrialPeriod.Should().BeNull();
    }

    /// <summary>
    /// Visibility is a property of a module's placement on a page and of nothing else, so the module row
    /// does not declare it and the projections must not look for it there.
    /// </summary>
    [Fact]
    public void ModuleEntity_DeclaresNoVisibilityBecauseVisibilityBelongsToThePlacement()
    {
        typeof(Module).GetProperty("Visibility").Should().BeNull(
            "the same module placed on two pages can be maximised on one and minimised on the other");
        typeof(TabModule).GetProperty("Visibility").Should().NotBeNull();

        foreach (string placementScoped in new[]
        {
            "PaneName", "ModuleOrder", "CacheTime", "Alignment", "Color", "Border",
            "DisplayTitle", "DisplayPrint", "DisplaySyndicate", "ContainerSrc",
        })
        {
            typeof(Module).GetProperty(placementScoped).Should().BeNull(
                $"{placementScoped} describes one placement of the module, not the module");
        }

        // And the projection genuinely reads it from the placement: moving the placement's value moves the
        // projected value, with the module left untouched between the two calls.
        Module module = FullModule();
        TabModule maximised = FullPlacement();
        maximised.Visibility = ModuleVisibility.Maximized;
        TabModule minimised = FullPlacement();
        minimised.Visibility = ModuleVisibility.Minimized;

        ModuleMappings.ToDetail(module, maximised, null).Visibility.Should().Be(ModuleVisibility.Maximized);
        ModuleMappings.ToDetail(module, minimised, null).Visibility.Should().Be(ModuleVisibility.Minimized);
        ModuleMappings.ToListItem(module, maximised, null).Visibility.Should().Be(ModuleVisibility.Maximized);
        ModuleMappings.ToListItem(module, minimised, null).Visibility.Should().Be(ModuleVisibility.Minimized);
    }

    /// <summary>
    /// All three visibility states round-trip through the create path, the update path and both read
    /// projections.
    /// </summary>
    /// <remarks>
    /// The legacy enum left its members unnumbered and the target numbers them explicitly, so the stored
    /// ordinals are pinned rather than left to declaration order. Maximised is zero, which matches the
    /// legacy row reader: it collapsed an unreadable value to the first member, so a module whose
    /// visibility had never been set displayed maximised.
    /// </remarks>
    /// <param name="state">The visibility state under test.</param>
    /// <param name="stored">The ordinal the state is stored as.</param>
    [Theory]
    [InlineData(ModuleVisibility.Maximized, 0)]
    [InlineData(ModuleVisibility.Minimized, 1)]
    [InlineData(ModuleVisibility.None, 2)]
    public void ModuleMappers_RoundTripEveryVisibilityState(ModuleVisibility state, int stored)
    {
        ((int)state).Should().Be(stored, "the ordinals are part of the contract, not an accident of order");

        TabModule placement = ModuleMappings.ToNewPlacement(
            new CreateModuleRequest { TabId = 7, Visibility = state });

        placement.Visibility.Should().Be(state);

        Module module = FullModule();

        ModuleMappings.ToDetail(module, placement, null).Visibility.Should().Be(state);
        ModuleMappings.ToListItem(module, placement, null).Visibility.Should().Be(state);

        TabModule stored2 = FullPlacement();
        ModuleMappings.ApplyUpdate(module, stored2, new UpdateModuleRequest { Visibility = state });
        stored2.Visibility.Should().Be(state);
    }

    /// <summary>
    /// The three capability flags are read from the package's bitmask rather than recomputed, and the
    /// mapper never writes one back.
    /// </summary>
    /// <param name="supportedFeatures">The stored capability bitmask.</param>
    /// <param name="portable">Whether bit one is set.</param>
    /// <param name="searchable">Whether bit two is set.</param>
    /// <param name="upgradeable">Whether bit four is set.</param>
    [Theory]
    [InlineData(-1, false, false, false)]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(2, false, true, false)]
    [InlineData(3, true, true, false)]
    [InlineData(4, false, false, true)]
    [InlineData(5, true, false, true)]
    [InlineData(6, false, true, true)]
    [InlineData(7, true, true, true)]
    public void ModuleDefinitionToDto_ReadsEveryCapabilityFlagFromTheBitmask(
        int supportedFeatures,
        bool portable,
        bool searchable,
        bool upgradeable)
    {
        DesktopModule package = NewDesktopModule("Announcements", supportedFeatures);

        package.IsPortable.Should().Be(portable);
        package.IsSearchable.Should().Be(searchable);
        package.IsUpgradeable.Should().Be(upgradeable);
        package.SupportedFeatures.Should().Be(
            supportedFeatures,
            "the bitmask itself stays a plain integer - no flags enum, no re-encoding");

        ModuleDefinition definition = new()
        {
            ModuleDefinitionId = 4,
            FriendlyName = "Announcements",
            DesktopModuleId = package.DesktopModuleId,
            DefaultCacheTime = 60,
        };

        ModuleMappings.ToDto(definition, package).IsPortable.Should().Be(
            portable,
            "the projection reads the derived flag rather than deriving it a second time");

        typeof(DesktopModule).GetProperty("IsPortable")!.CanWrite.Should().BeFalse(
            "a derived flag has no setter, so it cannot drift from the bitmask it comes from");
        typeof(DesktopModule).GetProperty("IsSearchable")!.CanWrite.Should().BeFalse();
        typeof(DesktopModule).GetProperty("IsUpgradeable")!.CanWrite.Should().BeFalse();
    }

    /// <summary>
    /// A setting stored with an empty value projects an empty value, because an empty setting is a setting
    /// that was chosen rather than one that was never made.
    /// </summary>
    [Fact]
    public void ModuleToSettings_KeepsAnEmptySettingValueAsAPresentSetting()
    {
        Module module = FullModule();
        TabModule placement = FullPlacement();

        ModuleSettingsDto settings = ModuleMappings.ToSettings(
            module,
            placement,
            [new ModuleSetting { ModuleId = 0, SettingName = "cacheKey", SettingValue = string.Empty }],
            [new TabModuleSetting { TabModuleId = 31, SettingName = "skin", SettingValue = string.Empty }]);

        settings.ModuleSettings.Should().ContainKey("cacheKey");
        settings.ModuleSettings["cacheKey"].Should().Be(
            string.Empty,
            "a setting stored as nothing is a setting, and the caller has to be able to see it");
        settings.TabModuleSettings.Should().ContainKey("skin");
        settings.TabModuleSettings["skin"].Should().Be(string.Empty);

        // Absence is a missing key, which is the state an empty value is being kept apart from.
        settings.ModuleSettings.Should().NotContainKey("neverSet");
        settings.TabModuleSettings.Should().NotContainKey("neverSet");
    }

    /// <summary>
    /// A module whose inheritance flag was never read is indistinguishable from one whose flag is false,
    /// and a module date at the legacy stand-in travels as an ordinary instant.
    /// </summary>
    /// <remarks>
    /// The inheritance flag is the one boolean on the module row that is nullable, and the detail
    /// projection flattens it with a default of false.
    /// </remarks>
    [Fact]
    public void ModuleMappers_FlattenAnUnreadInheritanceFlagToFalseAndCarryTheDateStandIn()
    {
        Module unread = FullModule();
        unread.InheritViewPermissions = null;
        unread.StartDate = DateTime.MinValue;
        unread.EndDate = null;

        ModuleDetailDto fromUnread = ModuleMappings.ToDetail(unread, FullPlacement(), null);

        fromUnread.InheritViewPermissions.Should().BeFalse();
        fromUnread.StartDate.Should().Be(DateTime.MinValue, "the stand-in instant is a value here");
        fromUnread.EndDate.Should().BeNull("absence is null, and only null");

        Module explicitlyFalse = FullModule();
        explicitlyFalse.InheritViewPermissions = false;

        ModuleMappings.ToDetail(explicitlyFalse, FullPlacement(), null)
            .InheritViewPermissions.Should().BeFalse(
                "false and never-read are the same answer on the response, which is what the legacy "
                + "store already did to them");

        Module written = FullModule();
        written.InheritViewPermissions = null;

        ModuleMappings.ApplyUpdate(
            written,
            FullPlacement(),
            new UpdateModuleRequest { InheritViewPermissions = false });
        written.InheritViewPermissions.Should().BeFalse("an update decides the flag one way or the other");

        ModuleMappings.ApplyUpdate(
            written,
            FullPlacement(),
            new UpdateModuleRequest { InheritViewPermissions = true });
        written.InheritViewPermissions.Should().BeTrue();

        typeof(UpdateModuleRequest).GetProperty("InheritViewPermissions")!
            .PropertyType.Should().Be(
                typeof(bool),
                "the undecided state belongs to rows the legacy application wrote and is not offered "
                + "to callers of this API");
        typeof(Module).GetProperty("InheritViewPermissions")!
            .PropertyType.Should().Be(
                typeof(bool?),
                "the entity still has to be able to READ the undecided state out of the column");
    }

    /// <summary>
    /// A freshly composed placement shows its title and its print affordance and withholds its syndication
    /// affordance, because the mapper supplies none of the three and the row's own defaults decide.
    /// </summary>
    /// <remarks>
    /// The two visible affordances default on and syndication defaults OFF, which follows the legacy
    /// constructor. The column default introduced later in the upgrade chain is 1, so a row inserted by the
    /// legacy application and a row inserted by the legacy DDL disagreed with each other.
    /// </remarks>
    [Fact]
    public void ModuleToNewPlacement_LeavesTheAffordanceDefaultsToTheRow()
    {
        TabModule fresh = ModuleMappings.ToNewPlacement(new CreateModuleRequest { TabId = 7 });

        fresh.DisplayTitle.Should().BeTrue("the legacy constructor showed the title");
        fresh.DisplayPrint.Should().BeTrue("and offered the print affordance");
        fresh.DisplaySyndicate.Should().BeFalse(
            "the legacy constructor withheld syndication even though the column default is 1");

        // The create contract carries the title affordance, so a caller can still switch it off, and the
        // other two remain the row's business because the contract does not offer them.
        ModuleMappings.ToNewPlacement(new CreateModuleRequest { TabId = 7, DisplayTitle = false })
            .DisplayTitle.Should().BeFalse();

        typeof(CreateModuleRequest).GetProperty("DisplayPrint").Should().BeNull();
        typeof(CreateModuleRequest).GetProperty("DisplaySyndicate").Should().BeNull();
    }

    /// <summary>
    /// No response projection declares a credential member, and no response projection carries a credential
    /// value even when the account it was built from holds all three.
    /// </summary>
    /// <remarks>
    /// MIGRATION: password RETRIEVAL is not carried forward in any form. There is no mapper, no contract
    /// member and no endpoint for it, and the recovery path is a reset rather than a disclosure.
    /// </remarks>
    [Fact]
    public void UserReadProjections_NeitherDeclareNorCarryAnyCredential()
    {
        const string storedHash = "$2a$12$measuredhashvalue";
        const string storedAnswer = "not-a-real-answer";
        const string storedQuestion = "not-a-real-question";

        User user = FullUser();
        user.PasswordHash = storedHash;
        user.PasswordAnswer = storedAnswer;
        user.PasswordQuestion = storedQuestion;

        object[] projections =
        [
            UserMappings.ToListItem(user, -1, "12 Measured Street", "555-0100", null),
            UserMappings.ToDetail(user, -1, ["Administrators"], null),
            UserMappings.ToProfile(user.UserId, [FullDefinition()], [NewProfileValue("555-0100", null)], 2, true),
            UserMappings.ToDto(FullDefinition(), 2),
        ];

        foreach (object projection in projections)
        {
            Type contract = projection.GetType();

            foreach (string credential in new[]
            {
                "Password", "PasswordHash", "PasswordAnswer", "PasswordQuestion", "PasswordSalt",
                "PasswordFormat", "EncryptedPassword", "PasswordConfirmation",
            })
            {
                contract.GetProperty(credential).Should().BeNull(
                    $"{contract.Name} is a response contract and {credential} is a credential");
            }

            string?[] carried = contract.GetProperties()
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => (string?)property.GetValue(projection))
                .ToArray();

            carried.Should().NotContain(storedHash, $"{contract.Name} must not carry the stored hash");
            carried.Should().NotContain(storedAnswer);
            carried.Should().NotContain(storedQuestion);
        }
    }

    /// <summary>
    /// Creating an account never hashes and never copies the submitted password onto the row, and updating
    /// an account never touches the stored credential.
    /// </summary>
    /// <remarks>
    /// Hashing belongs behind the password-hasher abstraction, not in a mapper. A mapper that hashed would
    /// be neither pure nor testable without a cost parameter, and it would put a credential decision in the
    /// one layer that is meant only to move fields.
    /// </remarks>
    [Fact]
    public void UserWriteMappers_NeverHashAndNeverDisturbTheStoredCredential()
    {
        User created = UserMappings.ToNewUser(new CreateUserRequest
        {
            Username = "measured_member",
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
            Password = "not-a-real-password",
            ConfirmPassword = "not-a-real-password",
            Authorize = true,
        });

        created.PasswordHash.Should().BeNull("the mapper neither hashes nor copies the submitted password");
        created.PasswordAnswer.Should().BeNull();
        created.PasswordQuestion.Should().BeNull();

        User stored = FullUser();
        stored.PasswordHash = "$2a$12$measuredhashvalue";
        stored.PasswordAnswer = "not-a-real-answer";
        stored.PasswordQuestion = "not-a-real-question";

        UserMappings.ApplyUpdate(stored, new UpdateUserRequest
        {
            FirstName = "Augusta",
            LastName = "King",
            DisplayName = "Augusta King",
            Email = "augusta@example.com",
        });

        stored.PasswordHash.Should().Be(
            "$2a$12$measuredhashvalue",
            "the profile contract carries no credential, so an update cannot clobber one");
        stored.PasswordAnswer.Should().Be("not-a-real-answer");
        stored.PasswordQuestion.Should().Be("not-a-real-question");
        stored.UpdatePassword.Should().BeTrue("nor can it clear a standing instruction to change it");

        typeof(UpdateUserRequest).GetProperty("Password").Should().BeNull();
        typeof(UpdateUserRequest).GetProperty("PasswordHash").Should().BeNull();
    }

    /// <summary>
    /// Each account projection declares exactly one address member, because the legacy account and its
    /// membership record each carried one and the two collapse onto a single field.
    /// </summary>
    [Fact]
    public void UserReadProjections_DeclareExactlyOneAddressMember()
    {
        foreach (Type contract in new[] { typeof(UserListItemDto), typeof(UserDetailDto) })
        {
            string[] emailish = contract.GetProperties()
                .Select(property => property.Name)
                .Where(name => name.Contains("Email", StringComparison.Ordinal))
                .ToArray();

            emailish.Should().Equal(
                ["Email"],
                $"{contract.Name} must carry one e-mail and must not qualify it with a source");
        }

        User user = FullUser();
        user.Email = "ada@example.com";

        UserMappings.ToListItem(user, -1, null, null, null).Email.Should().Be("ada@example.com");
        UserMappings.ToDetail(user, -1, [], null).Email.Should().Be("ada@example.com");
    }

    /// <summary>
    /// An account fact that was never read and one that was read as false project the same answer, and a
    /// membership instant at the legacy stand-in projects as that instant rather than as absent.
    /// </summary>
    /// <remarks>
    /// The three membership flags are nullable on the entity because the credential store is an externally
    /// installed set of objects that the migration reads rather than owns, so "not read" is a real state.
    /// </remarks>
    [Fact]
    public void UserReadProjections_ReportAnUnreadFlagAsFalseAndCarryTheDateStandIn()
    {
        User unread = FullUser();
        unread.IsApproved = null;
        unread.IsOnline = null;
        unread.IsLockedOut = null;
        unread.CreatedDate = DateTime.MinValue;
        unread.LastLoginDate = null;

        UserDetailDto fromUnread = UserMappings.ToDetail(unread, -1, [], null);

        fromUnread.IsApproved.Should().BeFalse();
        fromUnread.IsOnline.Should().BeFalse();
        fromUnread.IsLockedOut.Should().BeFalse();
        fromUnread.CreatedDate.Should().Be(DateTime.MinValue, "the stand-in instant is a value here");
        fromUnread.LastLoginDate.Should().BeNull("an account that has never signed in has no instant");

        User explicitlyFalse = FullUser();
        explicitlyFalse.IsApproved = false;
        explicitlyFalse.IsOnline = false;
        explicitlyFalse.IsLockedOut = false;

        UserDetailDto fromFalse = UserMappings.ToDetail(explicitlyFalse, -1, [], null);

        fromFalse.IsApproved.Should().BeFalse(
            "false and never-read are the same answer on the response, exactly as they were in the store");
        fromFalse.IsOnline.Should().BeFalse();
        fromFalse.IsLockedOut.Should().BeFalse();

        UserListItemDto listed = UserMappings.ToListItem(unread, -1, null, null, null);

        listed.IsApproved.Should().BeFalse();
        listed.IsOnline.Should().BeFalse();
        listed.IsLockedOut.Should().BeFalse();
        listed.CreatedDate.Should().Be(DateTime.MinValue);
        listed.LastLoginDate.Should().BeNull();
    }

    /// <summary>
    /// A host-level profile property projects the legacy host identifier rather than an absent tenant.
    /// </summary>
    [Fact]
    public void ProfileDefinitionToDto_ProjectsAHostLevelDefinitionAsTheLegacyHostIdentifier()
    {
        ProfilePropertyDefinition hostLevel = FullDefinition();
        hostLevel.PortalId = null;

        UserMappings.ToDto(hostLevel, 2).PortalId.Should().Be(
            -1,
            "the installation is addressed as a portal, and -1 is the identifier it is addressed by");

        ProfilePropertyDefinition tenantLevel = FullDefinition();
        tenantLevel.PortalId = 0;

        UserMappings.ToDto(tenantLevel, 2).PortalId.Should().Be(
            0,
            "Portals.PortalID is seeded IDENTITY(-1,1), so zero is the first tenant rather than no tenant");

        ProfilePropertyDefinition firstTenant = FullDefinition();
        firstTenant.PortalId = -1;

        UserMappings.ToDto(firstTenant, 2).PortalId.Should().Be(
            -1,
            "a stored -1 is a tenant key and is published unchanged, which is indistinguishable from the "
            + "host encoding in this non-nullable response member and is documented as such");
    }

    /// <summary>
    /// A profile answer stored as the empty string projects the empty string rather than falling back to
    /// the long-text column or reporting the answer as absent.
    /// </summary>
    /// <remarks>
    /// A profile value is stored in one of two columns - a bounded one for ordinary answers and an
    /// unbounded one for long text - and the projection has to choose. The choice must be made on nullness,
    /// not on emptiness: an answer of "" is an answer the member gave, and treating it as missing would
    /// silently substitute a different column's content.
    /// </remarks>
    [Fact]
    public void UserToProfile_KeepsAnEmptyAnswerApartFromAnAbsentOne()
    {
        Answer(NewProfileValue(string.Empty, "long text that must not be reached"))
            .PropertyValue.Should().Be(
                string.Empty,
                "an empty bounded answer is present, so the long-text column is not consulted");

        Answer(NewProfileValue(null, "long text")).PropertyValue.Should().Be(
            "long text",
            "an absent bounded answer is what sends the projection to the long-text column");

        Answer(NewProfileValue(null, string.Empty)).PropertyValue.Should().Be(
            string.Empty,
            "and an empty long text is itself an answer rather than a reason to report nothing");

        Answer(NewProfileValue(null, null)).PropertyValue.Should().Be(
            string.Empty,
            "with neither column populated the response carries the empty string, because the member is "
            + "declared non-nullable and a caller rendering a form needs something to bind to");

        // The plain answer path is unaffected, so the empty cases are not being special-cased.
        Answer(NewProfileValue("555-0100", null)).PropertyValue.Should().Be("555-0100");
        Answer(NewProfileValue("555-0100", "long text")).PropertyValue.Should().Be("555-0100");
    }

    /// <summary>
    /// Every enum a mapper carries keeps the member that the legacy row reader would have produced for an
    /// unreadable value as its lowest-valued member, because that member is what an unset column becomes.
    /// </summary>
    /// <remarks>
    /// The legacy reader had a general rule for enums: sort the declared values and take the first. Any
    /// column it could not read therefore became the lowest-VALUED member rather than a documented default,
    /// which makes the numbering of these enums load-bearing.
    /// </remarks>
    [Fact]
    public void EveryCarriedEnum_KeepsTheMemberAnUnsetColumnWouldBecome()
    {
        Enum.GetValues<UserCreateStatus>()[0].Should().Be(
            UserCreateStatus.AddUser,
            "the lowest-valued outcome is the first step of the attempt, not its success");
        ((int)UserCreateStatus.Success).Should().Be(13, "success is emphatically not the zero value");

        Enum.GetValues<BillingFrequency>()[0].Should().Be(
            BillingFrequency.Day,
            "the members are valued at their characters, so 'D' at 68 sorts below 'N' at 78");

        Enum.GetValues<UserLoginStatus>()[0].Should().Be(
            UserLoginStatus.Failure,
            "an unreadable sign-in outcome must fail closed");
        Enum.GetValues<PasswordFormat>()[0].Should().Be(
            PasswordFormat.Clear,
            "the least secure format is the zero value, which is why no code path may default to it");
        Enum.GetValues<ModuleVisibility>()[0].Should().Be(ModuleVisibility.Maximized);
        Enum.GetValues<UserRegistrationMode>()[0].Should().Be(
            UserRegistrationMode.NoRegistration,
            "an unreadable registration mode must not open registration");
        Enum.GetValues<BannerAdvertisingMode>()[0].Should().Be(BannerAdvertisingMode.None);
        Enum.GetValues<PermissionKey>()[0].Should().Be(PermissionKey.VIEW);
        Enum.GetValues<RoleStatus>()[0].Should().Be(
            RoleStatus.Pending,
            "the computed status has no legacy ancestor, and pending is the conservative zero");

        ModuleMappings.ToNewPlacement(new CreateModuleRequest { TabId = 7 })
            .Visibility.Should().Be(ModuleVisibility.Maximized);
    }

    /// <summary>
    /// The permission keyword is carried by name, because the column stores the keyword itself rather than
    /// a number, and no mapper in this suite projects a permission at all.
    /// </summary>
    /// <remarks>
    /// The permission column is <c>varchar(20)</c> holding the keyword, so the enum's members carry no
    /// explicit values and their ordinals are an implementation detail with no meaning outside the process.
    /// Anything that persisted an ordinal would write a number into a text column that a foreign key
    /// expects to hold one of four words.
    /// </remarks>
    [Fact]
    public void PermissionKeyword_IsCarriedByNameAndNoMapperProjectsAPermission()
    {
        Enum.GetNames<PermissionKey>().Should().Equal(
            ["VIEW", "EDIT", "READ", "WRITE"],
            "the stored keywords are these four words, upper-case, and there is no fifth");

        nameof(PermissionKey.VIEW).Should().Be("VIEW");
        Enum.Parse<PermissionKey>("EDIT").Should().Be(PermissionKey.EDIT);
        Enum.Parse<PermissionKey>(nameof(PermissionKey.WRITE)).ToString().Should().Be("WRITE");

        ((int)PermissionKey.EDIT).Should().Be(1);
        Enum.GetUnderlyingType(typeof(PermissionKey)).Should().Be(typeof(int));

        foreach (Type contract in new[]
        {
            typeof(TabDetailDto), typeof(TabListItemDto), typeof(UpdateTabRequest),
            typeof(ModuleDetailDto), typeof(ModuleListItemDto), typeof(ModuleSettingsDto),
            typeof(RoleDetailDto), typeof(RoleListItemDto),
        })
        {
            string[] permissionish = contract.GetProperties()
                .Select(property => property.Name)
                .Where(name => name.Contains("Permission", StringComparison.Ordinal))
                .ToArray();

            permissionish.Should().BeSubsetOf(
                ["InheritViewPermissions"],
                $"{contract.Name} carries no permission collection, so the permission catalogue is "
                + "projected by the permissions endpoint rather than by any of these five mappers");
        }
    }

    /// <summary>
    /// Every mapper is a pure function of its arguments: called twice with the same input it produces the
    /// same output, and it reads no clock, no random source and no ambient state.
    /// </summary>
    [Fact]
    public void EveryMapper_IsAPureFunctionOfItsArguments()
    {
        Portal portal = FullPortal();
        Tab tab = FullTab();
        Role role = FullRole();
        Module module = FullModule();
        TabModule placement = FullPlacement();
        User user = FullUser();
        ProfilePropertyDefinition definition = FullDefinition();
        RoleGroup group = new()
        {
            RoleGroupId = 6,
            PortalId = -1,
            RoleGroupName = "Paid Memberships",
            Description = "A measured group",
        };
        ModuleDefinition moduleDefinition = new()
        {
            ModuleDefinitionId = 4,
            FriendlyName = "Announcements",
            DesktopModuleId = 3,
            DefaultCacheTime = 60,
        };

        PortalMappings.ToListItem(portal, ["alpha.example.com"], 3, 4).Should().BeEquivalentTo(
            PortalMappings.ToListItem(portal, ["alpha.example.com"], 3, 4));
        PortalMappings.ToDetail(portal, 3, 4, "Administrators", "Registered Users", "admin@example.com", 5, null)
            .Should().BeEquivalentTo(
                PortalMappings.ToDetail(
                    portal, 3, 4, "Administrators", "Registered Users", "admin@example.com", 5, null));
        PortalMappings.ToSettings(portal).Should().BeEquivalentTo(PortalMappings.ToSettings(portal));
        PortalMappings.ToDto(portal.PortalAliases.First(), null).Should()
            .BeEquivalentTo(PortalMappings.ToDto(portal.PortalAliases.First(), null));

        TabMappings.ToListItem(tab, true).Should().BeEquivalentTo(TabMappings.ToListItem(tab, true));
        TabMappings.ToDetail(tab, true).Should().BeEquivalentTo(TabMappings.ToDetail(tab, true));

        RoleMappings.ToListItem(role).Should().BeEquivalentTo(RoleMappings.ToListItem(role));
        RoleMappings.ToDetail(role).Should().BeEquivalentTo(RoleMappings.ToDetail(role));
        RoleMappings.ToDto(group).Should().BeEquivalentTo(RoleMappings.ToDto(group));

        ModuleMappings.ToListItem(module, placement, AnnouncementsCatalogue).Should()
            .BeEquivalentTo(ModuleMappings.ToListItem(module, placement, AnnouncementsCatalogue));
        ModuleMappings.ToDetail(module, placement, AnnouncementsCatalogue).Should()
            .BeEquivalentTo(ModuleMappings.ToDetail(module, placement, AnnouncementsCatalogue));
        ModuleMappings.ToSettings(module, placement, [], []).Should()
            .BeEquivalentTo(ModuleMappings.ToSettings(module, placement, [], []));
        ModuleMappings.ToDto(moduleDefinition, NewDesktopModule("Announcements", 7)).Should()
            .BeEquivalentTo(ModuleMappings.ToDto(moduleDefinition, NewDesktopModule("Announcements", 7)));

        UserMappings.ToListItem(user, -1, "12 Measured Street", "555-0100", null).Should()
            .BeEquivalentTo(UserMappings.ToListItem(user, -1, "12 Measured Street", "555-0100", null));
        UserMappings.ToDetail(user, -1, ["Administrators"], null).Should()
            .BeEquivalentTo(UserMappings.ToDetail(user, -1, ["Administrators"], null));
        UserMappings.ToDto(definition, 2).Should().BeEquivalentTo(UserMappings.ToDto(definition, 2));
        UserMappings.ToProfile(1, [definition], [NewProfileValue("555-0100", null)], 2, true).Should()
            .BeEquivalentTo(
                UserMappings.ToProfile(1, [definition], [NewProfileValue("555-0100", null)], 2, true));

        // The write mappers are pure in the same sense: applied twice they leave the same row, so neither
        // accumulates nor stamps an instant of its own.
        Portal appliedOnce = FullPortal();
        Portal appliedTwice = FullPortal();
        UpdatePortalRequest request = new() { PortalName = "Renamed Portal", HostFee = 3.25m };

        PortalMappings.ApplyUpdate(appliedOnce, request);
        PortalMappings.ApplyUpdate(appliedTwice, request);
        PortalMappings.ApplyUpdate(appliedTwice, request);

        // Compared field by field rather than as an object graph, because the fixture issues a random
        // handle and an entity graph carries navigations that say nothing about the mapper.
        PortalMappings.ToSettings(appliedTwice).Should().BeEquivalentTo(
            PortalMappings.ToSettings(appliedOnce),
            options => options.Excluding(settings => settings.Guid));
        appliedTwice.ProcessorCredentialReference.Should().Be(appliedOnce.ProcessorCredentialReference);
        appliedTwice.AdminTabId.Should().Be(appliedOnce.AdminTabId);
        appliedTwice.AdministratorRoleId.Should().Be(appliedOnce.AdministratorRoleId);
        appliedTwice.RegisteredRoleId.Should().Be(appliedOnce.RegisteredRoleId);

        // Freshly composed entities carry no generated handle and no generated instant, so nothing here
        // depends on when the suite runs.
        Portal composed = PortalMappings.ToNewPortal(
            new CreatePortalRequest { PortalName = "Composed" },
            "GBP",
            null,
            0m,
            0,
            0,
            0,
            null,
            "Portals/composed");

        composed.PortalGuid.Should().Be(
            Guid.Empty,
            "the handle is issued by the store, so the mapper reads no random source");

        UserMappings.ToNewUser(new CreateUserRequest { Username = "u", FirstName = "F", LastName = "L" })
            .CreatedDate.Should().BeNull("the creation instant belongs to the credential store, not here");
        RoleMappings.ToNewAssignment(42, new RoleAssignmentRequest { UserId = 7 }, null, null)
            .EffectiveDate.Should().BeNull("an unbounded membership is unbounded, not bounded at 'now'");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    /// <summary>Builds a portal whose every mapped column carries a distinguishable value.</summary>
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
            ProcessorCredentialReference = "secret://processor/existing",
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

    /// <summary>Builds a page whose every mapped column carries a distinguishable value.</summary>
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
    /// The member-services projection carries the role's own stored terms, the account's own subscription
    /// state, and the three predicates the legacy grid bound.
    /// </summary>
    [Fact]
    public void MemberServiceProjection_CarriesTheStoredTermsAndTheAccountsOwnState()
    {
        Role role = FullRole();
        var assignment = new UserRole
        {
            UserRoleId = 7,
            UserId = 1,
            RoleId = role.RoleId,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpiryDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            IsTrialUsed = true,
        };

        MemberServiceDto row = UserMappings.ToMemberService(
            role,
            assignment,
            new DateTime(2026, 6, 1, 9, 30, 0, DateTimeKind.Utc),
            tenantTakesPayment: true);

        row.RoleId.Should().Be(role.RoleId);
        row.RoleName.Should().Be(role.RoleName);
        row.Description.Should().Be(role.Description);
        row.ServiceFee.Should().Be(role.ServiceFee);
        row.BillingPeriod.Should().Be(role.BillingPeriod);
        row.BillingFrequency.Should().Be(role.BillingFrequency);
        row.TrialFee.Should().Be(role.TrialFee);
        row.TrialPeriod.Should().Be(role.TrialPeriod);
        row.TrialFrequency.Should().Be(role.TrialFrequency);
        row.EffectiveDate.Should().Be(assignment.EffectiveDate);
        row.ExpiryDate.Should().Be(assignment.ExpiryDate);
        row.IsSubscribed.Should().BeTrue();
        row.IsTrialUsed.Should().BeTrue();
        row.IsExpired.Should().BeFalse("the expiry is later than the supplied date");
        row.SubscriptionAction.Should().Be(MemberServiceActions.Unsubscribe);
        row.SubscriptionOffered.Should().BeTrue("the tenant can take payment");
        row.SubscriptionRequiresPayment.Should().BeTrue();
        row.TrialOffered.Should().BeFalse("this account has already consumed the trial");
    }

    /// <summary>
    /// The lapsed test compares DATES and is strict, so a subscription expiring today is not yet lapsed.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void MemberServiceProjection_TreatsTheExpiryDayItselfAsStillCurrent(int expiryOffsetInDays, bool expected)
    {
        var today = new DateTime(2026, 6, 1, 23, 45, 0, DateTimeKind.Utc);
        Role role = FullRole();
        role.ServiceFee = 0m;

        MemberServiceDto row = UserMappings.ToMemberService(
            role,
            new UserRole
            {
                UserId = 1,
                RoleId = role.RoleId,
                ExpiryDate = today.Date.AddDays(expiryOffsetInDays),
            },
            today,
            tenantTakesPayment: false);

        row.IsExpired.Should().Be(expected);
        row.SubscriptionAction.Should().Be(
            expected ? MemberServiceActions.Renew : MemberServiceActions.Unsubscribe);
    }

    /// <summary>A perpetual subscription - one with no expiry - never reads as lapsed, however old it is.</summary>
    /// <remarks>
    /// The legacy arm is guarded by <c>Not Null.IsNull(expiryDate)</c> (<c>:L295</c>), so an unbounded
    /// membership fell through to the unsubscribe label. Under Rule T7 the absent bound is a null here
    /// rather than the minimum-date sentinel, and a mapper that compared the sentinel would call every
    /// unbounded subscription expired.
    /// </remarks>
    [Fact]
    public void MemberServiceProjection_NeverCallsAnUnboundedSubscriptionLapsed()
    {
        Role role = FullRole();
        role.ServiceFee = null;

        MemberServiceDto row = UserMappings.ToMemberService(
            role,
            new UserRole { UserId = 1, RoleId = role.RoleId, EffectiveDate = DateTime.MinValue },
            new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            tenantTakesPayment: false);

        row.IsExpired.Should().BeFalse();
        row.SubscriptionAction.Should().Be(MemberServiceActions.Unsubscribe);
    }

    /// <summary>An ABSENT fee means no fee, on both the service and the trial term.</summary>
    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData(0, 0, false, false)]
    [InlineData(9, null, true, true)]
    [InlineData(9, 0, true, true)]
    [InlineData(9, 4, true, false)]
    public void MemberServiceProjection_ReadsAnAbsentFeeAsNoFee(
        int? serviceFee,
        int? trialFee,
        bool requiresPayment,
        bool trialOffered)
    {
        Role role = FullRole();
        role.ServiceFee = serviceFee;
        role.TrialFee = trialFee;

        MemberServiceDto row = UserMappings.ToMemberService(
            role,
            assignment: null,
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            tenantTakesPayment: true);

        row.SubscriptionRequiresPayment.Should().Be(requiresPayment);
        row.TrialOffered.Should().Be(trialOffered);
        row.IsSubscribed.Should().BeFalse();
        row.IsTrialUsed.Should().BeFalse("an absent assignment has consumed no trial");
        row.SubscriptionAction.Should().Be(MemberServiceActions.Subscribe);
    }

    /// <summary>
    /// A role the tenant does not publish is offered neither a subscription nor a trial, whatever its
    /// terms.
    /// </summary>
    /// <remarks>
    /// The first arm of both legacy predicates is <c>objRole.IsPublic</c>. The mapper tests the role's own
    /// flag rather than assuming it, so it is correct for a caller that pairs it with a wider read than the
    /// subscribable-role one.
    /// </remarks>
    [Fact]
    public void MemberServiceProjection_OffersNothingForAnUnpublishedRole()
    {
        Role role = FullRole();
        role.IsPublic = false;

        MemberServiceDto row = UserMappings.ToMemberService(
            role,
            assignment: null,
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            tenantTakesPayment: true);

        row.SubscriptionOffered.Should().BeFalse();
        row.TrialOffered.Should().BeFalse();
        row.SubscriptionRequiresPayment.Should().BeTrue("the terms are a fact about the role, not about the offer");
    }

    /// <summary>The redemption row carries the key and the name, and nothing else.</summary>
    [Fact]
    public void RedeemedServiceProjection_CarriesTheKeyAndTheName()
    {
        Role role = FullRole();

        RedeemedServiceDto redeemed = UserMappings.ToRedeemedService(role);

        redeemed.RoleId.Should().Be(role.RoleId);
        redeemed.RoleName.Should().Be(role.RoleName);
    }

    /// <summary>Both member-services mappers refuse a null role.</summary>
    [Fact]
    public void MemberServiceMappers_RefuseANullRole()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = UserMappings.ToMemberService(null!, null, DateTime.UtcNow, tenantTakesPayment: false);
        });
        Assert.Throws<ArgumentNullException>(() => { _ = UserMappings.ToRedeemedService(null!); });
    }

    /// <summary>Builds a role whose every mapped column carries a distinguishable value.</summary>
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

    /// <summary>The catalogue facts a caller resolves for the fixture module's definition and package.</summary>
    /// <remarks>
    /// Every value differs from every value on the module and placement fixtures, so a projection that
    /// reads a catalogue member from the wrong row fails rather than coincidentally agreeing. The package
    /// key is deliberately 3 rather than 0, because 0 is not a key the store can issue.
    /// </remarks>
    private static ModuleCatalogueFacts AnnouncementsCatalogue => new(
        DesktopModuleId: 3,
        FriendlyName: "Announcements",
        ModuleName: "Announcements",
        Description: "Displays a list of announcements.",
        Version: "04.09.00",

        // FALSE RATHER THAN NULL, AND STATED EXPLICITLY: the fixture stands for a resolved package, and a
        // resolved package always answers this one way or the other. Null is reserved for a package that
        // could not be read at all, which is a different fixture and a different assertion.
        IsAdmin: false);

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

    /// <summary>Builds a package carrying a given name and capability bitmask.</summary>
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

    /// <summary>Builds an account whose every mapped column carries a distinguishable value.</summary>
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

    /// <summary>Builds a stored profile value for the definition the fixtures use.</summary>
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
        => UserMappings.ToProfile(1, [FullDefinition()], [stored], defaultVisibility: 2, displayVisibilityEnabled: true)
            .Properties[0];
}
