using DnnMigration.Application.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Portal"/> aggregate and the portal transfer contracts,
/// and between an inbound portal request and the aggregate it describes.
/// </summary>
/// <remarks>
/// <para>
/// Six members of the legacy portal entity are not columns of the portal table, and every one of them
/// arrives here as an explicit argument rather than being fetched.
/// </para>
/// <para>
/// The portal key is carried through verbatim, and no member below treats any particular value of it as
/// meaning "absent". Two facts make that mandatory rather than fastidious.
/// </para>
/// </remarks>
public static class PortalMappings
{
    /// <summary>Projects a portal onto the row shape the portal list screen renders.</summary>
    /// <param name="portal">The portal to project.</param>
    /// <param name="aliases">The host names bound to the portal, already ordered.</param>
    /// <param name="users">The portal's member tally.</param>
    /// <param name="pages">The portal's page tally.</param>
    /// <returns>The list row.</returns>
    /// <remarks>
    /// This row shape declares the hosting charge and the disc-space quota as required values rather than
    /// optional ones, which matches the entity and the terminal columns exactly - both are NOT NULL with a
    /// zero default constraint - so both copy straight across with no coalescing and no widening.
    /// </remarks>
    public static PortalListItemDto ToListItem(Portal portal, IReadOnlyList<string> aliases, int users, int pages)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(aliases);

        return new PortalListItemDto
        {
            PortalId = portal.PortalId,
            PortalName = portal.PortalName,
            Aliases = aliases,
            Users = users,
            Pages = pages,
            HostSpace = portal.HostSpace,
            HostFee = portal.HostFee,
            ExpiryDate = portal.ExpiryDate,
        };
    }

    /// <summary>Projects a portal onto the full detail contract.</summary>
    /// <param name="portal">The portal to project, with its aliases loaded.</param>
    /// <param name="users">The portal's member tally.</param>
    /// <param name="pages">The portal's page tally.</param>
    /// <param name="administratorRoleName">
    /// Name of the role named by the administrator role identifier, or <see langword="null"/> when it names
    /// none.
    /// </param>
    /// <param name="registeredRoleName">
    /// Name of the role named by the registered-members role identifier, or <see langword="null"/> when it
    /// names none.
    /// </param>
    /// <param name="administratorEmail">
    /// The designated administrator's electronic-mail address, or <see langword="null"/> when no
    /// administrator is designated.
    /// </param>
    /// <param name="superTabId">
    /// Identifier of the host-level root page, or <see langword="null"/> when the installation has none.
    /// </param>
    /// <param name="currentPortalAliasId">
    /// Surrogate key of the alias the CURRENT REQUEST resolved through, or <see langword="null"/> when the
    /// request resolved no tenant.
    /// </param>
    /// <returns>The detail contract.</returns>
    /// <remarks>
    /// The host-level root page is the same value for every portal in an installation. The legacy read view
    /// resolved it with a sub-query that filtered the page table for rows belonging to no portal and having
    /// no parent, so it never varied by portal even though it was published on each portal's record.
    /// </remarks>
    public static PortalDetailDto ToDetail(
        Portal portal,
        int users,
        int pages,
        string? administratorRoleName,
        string? registeredRoleName,
        string? administratorEmail,
        int? superTabId,
        int? currentPortalAliasId)
    {
        ArgumentNullException.ThrowIfNull(portal);

        return new PortalDetailDto
        {
            PortalId = portal.PortalId,
            PortalName = portal.PortalName,
            Description = portal.Description,
            KeyWords = portal.KeyWords,
            FooterText = portal.FooterText,

            // Both file members carry the raw stored value, and a caller must not assume it is a path.
            LogoFile = portal.LogoFile,
            BackgroundFile = portal.BackgroundFile,
            ExpiryDate = portal.ExpiryDate,
            UserRegistration = portal.UserRegistration,
            BannerAdvertising = portal.BannerAdvertising,
            Currency = portal.Currency,
            AdministratorId = portal.AdministratorId,
            Email = administratorEmail,

            // The hosting terms are widened, never narrowed.
            HostFee = portal.HostFee,
            HostSpace = portal.HostSpace,
            PageQuota = portal.PageQuota,
            UserQuota = portal.UserQuota,
            Users = users,
            Pages = pages,
            AdministratorRoleId = portal.AdministratorRoleId,
            AdministratorRoleName = administratorRoleName,
            RegisteredRoleId = portal.RegisteredRoleId,
            RegisteredRoleName = registeredRoleName,
            Guid = portal.PortalGuid,
            PaymentProcessor = portal.PaymentProcessor,
            ProcessorUserId = portal.ProcessorUserId,
            SiteLogHistory = portal.SiteLogHistory,
            AdminTabId = portal.AdminTabId,
            SuperTabId = superTabId,
            SplashTabId = portal.SplashTabId,
            HomeTabId = portal.HomeTabId,
            LoginTabId = portal.LoginTabId,
            UserTabId = portal.UserTabId,
            DefaultLanguage = portal.DefaultLanguage,
            TimeZoneOffset = portal.TimeZoneOffset,
            HomeDirectory = portal.HomeDirectory,

            // The contract declares this member optional, which admits three states absent, present but
            // empty, and populated. This projection never yields the first of them.
            Aliases = portal.PortalAliases.OrderBy(alias => alias.HttpAlias, StringComparer.OrdinalIgnoreCase)
                                          .Select(alias => ToDto(alias, currentPortalAliasId))
                                          .ToList(),
            ConcurrencyToken = ConcurrencyTokenFor(portal),
        };
    }

    /// <summary>
    /// Derives the optimistic-concurrency token a caller round-trips to prove it is replacing the tenant it
    /// read.
    /// </summary>
    /// <param name="portal">The portal as it currently stands.</param>
    /// <returns>The token.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="portal"/> is null.</exception>
    /// <remarks>
    /// ⚠ THE MEMBER ORDER IS PART OF THE CONTRACT. The token published by a read and the token verified by
    /// a write are both produced here, so a reordering changes both together and stays self-consistent -
    /// but a token already in a browser's hands would stop matching, and every open editor would be refused
    /// once. Adding a member has the same effect.
    /// </remarks>
    internal static string ConcurrencyTokenFor(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        return ConcurrencyToken.From(
            portal.PortalName,
            portal.LogoFile,
            portal.FooterText,
            portal.ExpiryDate,
            portal.UserRegistration,
            portal.BannerAdvertising,
            portal.Currency,
            portal.AdministratorId,
            portal.HostFee,
            portal.HostSpace,
            portal.PageQuota,
            portal.UserQuota,
            portal.PaymentProcessor,
            portal.ProcessorUserId,
            portal.Description,
            portal.KeyWords,
            portal.BackgroundFile,
            portal.SiteLogHistory,
            portal.SplashTabId,
            portal.HomeTabId,
            portal.LoginTabId,
            portal.UserTabId,
            portal.DefaultLanguage,
            portal.TimeZoneOffset,
            portal.HomeDirectory);
    }

    /// <summary>Projects a portal onto the configuration contract the settings screen renders.</summary>
    /// <param name="portal">The portal to project.</param>
    /// <returns>The configuration contract.</returns>
    /// <remarks>
    /// This is a projection of stored columns, not a bag of keyed values: the shipped schema defines no
    /// portal settings table, so there is nothing keyed to read. The evidence for that is recorded on the
    /// type.
    /// </remarks>
    public static PortalSettingsDto ToSettings(Portal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);

        return new PortalSettingsDto
        {
            PortalId = portal.PortalId,
            PortalName = portal.PortalName,
            Description = portal.Description,
            KeyWords = portal.KeyWords,
            FooterText = portal.FooterText,
            LogoFile = portal.LogoFile,
            BackgroundFile = portal.BackgroundFile,
            ExpiryDate = portal.ExpiryDate,
            UserRegistration = portal.UserRegistration,
            BannerAdvertising = portal.BannerAdvertising,
            Currency = portal.Currency,
            AdministratorId = portal.AdministratorId,
            HostFee = portal.HostFee,
            HostSpace = portal.HostSpace,
            PageQuota = portal.PageQuota,
            UserQuota = portal.UserQuota,
            PaymentProcessor = portal.PaymentProcessor,
            ProcessorUserId = portal.ProcessorUserId,
            SiteLogHistory = portal.SiteLogHistory,
            SplashTabId = portal.SplashTabId,
            HomeTabId = portal.HomeTabId,
            LoginTabId = portal.LoginTabId,
            UserTabId = portal.UserTabId,
            DefaultLanguage = portal.DefaultLanguage,
            TimeZoneOffset = portal.TimeZoneOffset,
            HomeDirectory = portal.HomeDirectory,
            Guid = portal.PortalGuid,
            ConcurrencyToken = ConcurrencyTokenFor(portal),
        };
    }

    /// <summary>Projects one bound host name onto its transfer contract.</summary>
    /// <param name="alias">The alias to project.</param>
    /// <param name="currentPortalAliasId">
    /// Surrogate key of the alias the CURRENT REQUEST resolved through, or <see langword="null"/> when the
    /// request resolved no tenant.
    /// </param>
    /// <returns>The alias contract.</returns>
    /// <remarks>
    /// The parameter has no default. A default of <see langword="null"/> would make "no tenant resolved"
    /// the value a caller gets by FORGETTING to supply the fact, which is the one mistake that must not be
    /// silent: it reports every row as safe to edit, including the row that is not.
    /// </remarks>
    public static PortalAliasDto ToDto(PortalAlias alias, int? currentPortalAliasId)
    {
        ArgumentNullException.ThrowIfNull(alias);

        return new PortalAliasDto
        {
            PortalAliasId = alias.PortalAliasId,
            PortalId = alias.PortalId,
            HttpAlias = alias.HttpAlias,

            // Equality against the resolved key, never a magnitude test and never a truthiness test.
            IsCurrent = currentPortalAliasId is int resolved && resolved == alias.PortalAliasId,
        };
    }

    /// <summary>
    /// Projects one administrator-role assignment onto the entry the settings screen's administrator
    /// selector offers.
    /// </summary>
    /// <param name="membership">
    /// One assignment of the portal's administrator role, with its account materialised.
    /// </param>
    /// <returns>The selector entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="membership"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The assignment's account was not materialised by the read that produced it.
    /// </exception>
    public static PortalAdministratorDto ToAdministratorCandidate(UserRole membership)
    {
        ArgumentNullException.ThrowIfNull(membership);

        User account = membership.User
            ?? throw new InvalidOperationException(
                $"The account behind role assignment {membership.UserRoleId} was not materialised, so it cannot be offered as an administrator.");

        return new PortalAdministratorDto
        {
            UserId = account.UserId,
            Username = account.Username,
            DisplayName = account.DisplayName,
        };
    }

    /// <summary>
    /// Builds a new portal aggregate from a creation request and the installation-wide defaults that the
    /// legacy creation path read from host configuration.
    /// </summary>
    /// <param name="request">The submitted creation request.</param>
    /// <param name="currency">Default currency code.</param>
    /// <param name="expiryDate">Default expiry instant, or <see langword="null"/> for no expiry.</param>
    /// <param name="hostFee">Default monthly hosting charge.</param>
    /// <param name="hostSpace">Default disc-space quota in whole megabytes.</param>
    /// <param name="pageQuota">Default page quota.</param>
    /// <param name="userQuota">Default member quota.</param>
    /// <param name="siteLogHistory">Default site-log retention in days, or <see langword="null"/> for none.</param>
    /// <param name="homeDirectory">
    /// The home directory to record, which may be empty when it is derived after the identifier is
    /// assigned.
    /// </param>
    /// <returns>An unsaved portal aggregate.</returns>
    /// <remarks>
    /// Every host setting the legacy seeding path read was a string, and it treated an empty one as zero by
    /// leaving its freshly declared local at zero. The shipped installation script shows why that mattered:
    /// it identity-inserts the default portal with an EMPTY STRING supplied for the hosting charge, into a
    /// column that a later upgrade converted to money.
    /// </remarks>
    public static Portal ToNewPortal(
        CreatePortalRequest request,
        string currency,
        DateTime? expiryDate,
        decimal hostFee,
        int hostSpace,
        int pageQuota,
        int userQuota,
        int? siteLogHistory,
        string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new Portal
        {
            PortalName = request.PortalName ?? string.Empty,
            Description = request.Description,
            KeyWords = request.KeyWords,
            Currency = currency,
            ExpiryDate = expiryDate,

            HostFee = hostFee,
            HostSpace = hostSpace,
            PageQuota = pageQuota,
            UserQuota = userQuota,
            SiteLogHistory = siteLogHistory,
            HomeDirectory = homeDirectory,
            UserRegistration = UserRegistrationMode.NoRegistration,
            BannerAdvertising = BannerAdvertisingMode.None,
            DefaultLanguage = DefaultLanguageCode,
            TimeZoneOffset = DefaultTimeZoneOffsetMinutes,
        };
    }

    /// <summary>Applies a submitted update to a tracked portal aggregate.</summary>
    /// <param name="portal">The tracked portal to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// MIGRATION: plaintext processor credentials are no longer accepted or stored. The legacy
    /// ProcessorPassword column carries an opaque managed-secret reference and the request uses an explicit
    /// three-state update: null keeps, empty clears, and a non-empty reference replaces.
    /// </remarks>
    public static void ApplyUpdate(Portal portal, IPortalSettingsUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(request);

        portal.PortalName = request.PortalName ?? string.Empty;
        portal.LogoFile = request.LogoFile;
        portal.FooterText = request.FooterText;
        portal.ExpiryDate = request.ExpiryDate;
        portal.UserRegistration = request.UserRegistration;
        portal.BannerAdvertising = request.BannerAdvertising;
        portal.Currency = request.Currency;
        portal.AdministratorId = request.AdministratorId;
        portal.HostFee = request.HostFee ?? 0m;
        portal.HostSpace = request.HostSpace ?? 0;
        portal.PageQuota = request.PageQuota ?? 0;
        portal.UserQuota = request.UserQuota ?? 0;
        portal.PaymentProcessor = request.PaymentProcessor;
        portal.ProcessorUserId = request.ProcessorUserId;

        // Null is the explicit keep operation. Empty clears the reference to SQL NULL, while a non-empty,
        // validator-approved secret:// value replaces it.
        if (request.ProcessorCredentialReference is not null)
        {
            portal.ProcessorCredentialReference = request.ProcessorCredentialReference.Length == 0
                ? null
                : request.ProcessorCredentialReference;
        }

        portal.Description = request.Description;
        portal.KeyWords = request.KeyWords;
        portal.BackgroundFile = request.BackgroundFile;
        portal.SiteLogHistory = request.SiteLogHistory;
        portal.SplashTabId = request.SplashTabId;
        portal.HomeTabId = request.HomeTabId;
        portal.LoginTabId = request.LoginTabId;
        portal.UserTabId = request.UserTabId;
        portal.DefaultLanguage = string.IsNullOrWhiteSpace(request.DefaultLanguage)
            ? DefaultLanguageCode
            : request.DefaultLanguage;
        portal.TimeZoneOffset = request.TimeZoneOffset ?? DefaultTimeZoneOffsetMinutes;
        portal.HomeDirectory = request.HomeDirectory ?? string.Empty;
    }

    /// <summary>Clamps a ROLE's monetary fee so that a negative submission is stored as zero.</summary>
    /// <param name="fee">The submitted fee.</param>
    /// <returns>The fee, never below zero.</returns>
    public static decimal ClampFee(decimal fee) => fee < 0m ? 0m : fee;

    /// <summary>
    /// The default culture code a new portal carries, matching the column default the 02.02.00 upgrade
    /// script installed.
    /// </summary>
    private const string DefaultLanguageCode = "en-US";

    /// <summary>
    /// The default offset from co-ordinated universal time, in minutes, matching the column default the
    /// 02.02.00 upgrade script installed.
    /// </summary>
    private const int DefaultTimeZoneOffsetMinutes = -8;
}
