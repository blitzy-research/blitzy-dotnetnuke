namespace DnnMigration.Application.Options;

/// <summary>
/// Portal-wide behavioural defaults for the migrated DotNetNuke administration surface.
/// </summary>
/// <remarks>
/// <para>
/// This type is a configuration record: a plain object with no dependency
/// on the options, configuration, hosting or HTTP abstractions. It is
/// declared here in the Application layer and bound by the Api layer, which is why nothing
/// in this file reads configuration or registers itself with a service
/// container. The Application layer's composition entry point deliberately accepts no
/// configuration argument, so it could not bind this type even if that were wanted.
/// </para>
/// <para>
/// It does state its own invariants, through <see cref="Validate"/>, which the Api layer calls
/// as part of binding so that a misconfigured deployment fails while the host is starting. That
/// method reaches nothing outside the base class library, so declaring it here costs the layer
/// no dependency, and declaring each condition beside the value it governs is what keeps a
/// single rule per setting instead of one per consumer.
/// </para>
/// <para>
/// The three-way boundary. Portal-related state in this solution has three distinct homes,
/// and conflating them is the easiest way to put a value in the wrong place:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     Persisted per-portal configuration is a column on the <c>Portal</c> domain entity.
///     The home directory actually stored for a given portal, the user-registration mode,
///     the description and the keywords all belong there, because each varies per row.
///     </description>
///   </item>
///   <item>
///     <description>
///     Per-request tenant facts belong to the scoped portal-context abstraction: the
///     resolved portal id, portal name and alias, the administrator id, and the
///     administrator and registered role identifiers. Each is established once per request
///     while resolving the caller's tenant.
///     </description>
///   </item>
///   <item>
///     <description>
///     Host-level behavioural defaults belong here, and only these: the pattern applied
///     when a portal has no stored home directory, the administration template file name,
///     and the two special role display names.
///     </description>
///   </item>
/// </list>
/// <para>
/// The class is deliberately small. Every property below has measured read sites in the
/// legacy source, cited on the property itself, and a value earns a place here only when
/// migrated Application code genuinely reads it.
/// </para>
/// <para>
/// Sources examined that yielded nothing. The legacy <c>Website/release.config</c>
/// application-settings block spans L34-L55 and declares eleven active keys:
/// <c>SiteSqlServer</c> (L36), <c>InstallTemplate</c> (L40), <c>AutoUpgrade</c> (L41),
/// <c>UseInstallWizard</c> (L42), <c>InstallMemberRole</c> (L43), <c>ShowMissingKeys</c>
/// (L44), <c>EnableWebFarmSupport</c> (L45), <c>EnableCachePersistence</c> (L46),
/// <c>HostHeader</c> (L47), <c>RemoveAngleBrackets</c> (L49) and
/// <c>PersistentCookieTimeout</c> (L51), alongside two further entries that are commented
/// through. Not one is a candidate for this class: the connection string becomes a standard
/// connection-string entry owned by the Api layer, four are installer and upgrade concerns,
/// one is localisation debugging for a mechanism that is not carried forward, two are
/// web-farm and cache-persistence concerns owned by Infrastructure, one drives URL
/// rewriting, one scrubs Web Forms input, and the persistent-cookie timeout has no
/// counterpart under stateless bearer tokens. The block is recorded here as read in full
/// and deliberately empty-handed, so that a later reader can see it was examined rather
/// than overlooked, and so this class is understood to rest only on the three measured
/// legacy sites cited on its members.
/// </para>
/// </remarks>
public sealed class PortalOptions
{
    // MIGRATION: this class is where the excluded DotNetNuke.Common.Globals static module
    // (2,704 lines at Library/Components/Shared/Globals.vb, reached by 111 files) is absorbed
    // for portal defaults, as a few named replacements rather than a port of the module.

    // MIGRATION: Website/release.config application settings (L34-L55) were read in full and
    // yielded no property here; all eleven active keys are excluded or owned by another layer.

    /// <summary>
    /// Name of the configuration section this type is bound from.
    /// </summary>
    /// <remarks>
    /// The Api layer performs the binding, reading
    /// <c>configuration.GetSection(PortalOptions.SectionName)</c>. Because the standard
    /// configuration providers treat a double underscore as a section separator, every
    /// property below is overridable in a container through an environment variable of the
    /// form <c>Portal__&lt;PropertyName&gt;</c> — for instance
    /// <c>Portal__AdminTemplateFileName</c>. The section name itself is new work: the legacy
    /// application had no equivalent grouping, because these values lived as compiled
    /// constants and hard-coded literals rather than as configuration.
    /// </remarks>
    public const string SectionName = "Portal";

    // ------------------------------------------------------------------------
    // Bounds and required shapes for the values above, held here beside the
    // settings they constrain and const rather than configurable. Two of them are
    // security controls rather than tidiness: a template file name is combined
    // with a directory, and a home-directory format that loses its placeholder
    // collapses every tenant into one directory. Enforcement lives in
    // Api/Extensions/ServiceCollectionExtensions.cs and runs at startup.
    // ------------------------------------------------------------------------

    /// <summary>
    /// The substitution placeholder <see cref="HomeDirectoryFormat"/> must
    /// contain: <c>{0}</c>.
    /// </summary>
    /// <remarks>
    /// This is the single most consequential requirement in this type. The format
    /// is expanded once per portal, and the placeholder is the only part of it
    /// that differs between portals. A format without it expands to the same
    /// string for every tenant, so every portal would read and write one shared
    /// directory - a cross-tenant data exposure produced by a configuration
    /// typo, with nothing in the running system to signal it. Startup refuses
    /// such a value instead.
    /// </remarks>
    public const string HomeDirectoryPortalPlaceholder = "{0}";

    /// <summary>
    /// Longest acceptable <see cref="UnauthenticatedRoleName"/> or
    /// <see cref="AllUsersRoleName"/> value: 50 characters.
    /// </summary>
    /// <remarks>
    /// Not a preference. The terminal legacy schema declares
    /// <c>Roles.RoleName nvarchar(50) NOT NULL</c>, so a longer configured name
    /// cannot round-trip: it is either rejected by the database or truncated,
    /// and a truncated role name silently stops matching the role it was meant
    /// to denote.
    /// </remarks>
    public const int MaximumRoleNameLength = 50;

    /// <summary>
    /// Longest acceptable <see cref="AdminTemplateFileName"/> or
    /// <see cref="HomeDirectoryFormat"/> value: 260 characters.
    /// </summary>
    /// <remarks>
    /// Both values become part of a file-system path, and 260 is the classic
    /// maximum path length - generous for a single path segment or a short
    /// relative format, and low enough that neither value can be used to build an
    /// absurd path. It bounds length only; the separate checks that neither value
    /// is rooted nor contains a parent-directory segment are what make them safe.
    /// </remarks>
    public const int MaximumPathValueLength = 260;

    /// <summary>
    /// The parent-directory segment that neither <see cref="AdminTemplateFileName"/>
    /// nor <see cref="HomeDirectoryFormat"/> may contain: <c>..</c>.
    /// </summary>
    /// <remarks>
    /// Both values are combined with a directory the application owns, so a
    /// parent-directory segment would let a configured value address a location
    /// outside it. Rejecting the segment outright is simpler and safer than
    /// attempting to normalise the result and then reason about where it landed.
    /// </remarks>
    public const string ParentDirectorySegment = "..";

    /// <summary>
    /// File name of the administration portal template, which is parsed for every newly
    /// created portal in addition to the portal template the caller selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy default preserved exactly: <c>admin.template</c>. Measured at
    /// <c>Library/Components/Portal/PortalController.vb:L1082</c>, the call
    /// <c>ParseTemplate(intPortalId, TemplatePath, "admin.template", AdministratorId, ...)</c>,
    /// and at <c>L1370</c>, the comparison
    /// <c>isAdminTemplate = (TemplateFile = "admin.template")</c>. Those are the only two
    /// executable occurrences in the file; the third, at <c>L971</c>, is a documentation
    /// comment. An earlier draft of the migration plan cited <c>L1360</c> and <c>L980</c>,
    /// which are in fact the declarations of <c>ParseTemplate</c> and <c>CreatePortal</c>
    /// respectively rather than the literal, so the citations here are the corrected ones.
    /// </para>
    /// <para>
    /// The value is load-bearing rather than cosmetic. Matching it selects a behavioural
    /// branch that suppresses parsing of a template's &lt;settings&gt;, &lt;roles&gt;,
    /// &lt;folders&gt; and &lt;files&gt; nodes, as the legacy comment at <c>L1379</c> states
    /// and the guard at <c>L1380</c> implements. Changing it therefore changes which nodes of
    /// a template are honoured, so it is surfaced as configuration purely to accommodate an
    /// installation that renamed the file, never as a matter of preference.
    /// </para>
    /// <para>
    /// This is the administration template alone. The per-portal template file is a property
    /// of the create-portal request, chosen per call, and must not be added here.
    /// </para>
    /// </remarks>
    public string AdminTemplateFileName { get; set; } = "admin.template";

    /// <summary>
    /// Format string yielding a portal's default home directory, applied only when the portal
    /// has no home directory stored against it. The single <c>{0}</c> placeholder receives the
    /// portal id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy default preserved exactly: <c>Portals/{0}</c>. Measured at
    /// <c>Library/Components/Portal/PortalController.vb:L991-L992</c>, where
    /// <c>If HomeDirectory = "" Then HomeDirectory = "Portals/" + intPortalId.ToString</c>.
    /// L992 holds the only occurrence of that literal anywhere in the file.
    /// </para>
    /// <para>
    /// Three characteristics of the legacy value are deliberate and must survive. The
    /// separator is a forward slash, because the composed value is a web-relative path rather
    /// than a physical one. There is no trailing slash, because the legacy call site appends
    /// its own. And the default applies only when the stored value is empty, never as an
    /// unconditional override of a portal that has its own directory.
    /// </para>
    /// <para>
    /// Emptiness is the subtle part. In the legacy codebase the null sentinel for a string is
    /// the empty string rather than a null reference: <c>Library/Components/Shared/Null.vb</c>
    /// defines its <c>NullString</c> as <c>""</c>, and the reader at
    /// <c>PortalController.vb:L105</c> funnels a database null through that sentinel, so the
    /// legacy test at L991 only ever had to compare against <c>""</c>. Under nullable
    /// reference types a stored value can now genuinely be null, so a caller must treat null
    /// and the empty string identically when deciding whether to fall back to this format.
    /// Preserving that equivalence is what keeps the migrated behaviour identical.
    /// </para>
    /// <para>
    /// The home directory actually in force for a portal is a column on the <c>Portal</c>
    /// domain entity, not a value on this class, and turning the resulting relative path into
    /// a physical one is an Api or Infrastructure concern — the legacy code performed that
    /// mapping separately at <c>L994</c>, combining the relative path with the application
    /// path. Neither of those belongs here.
    /// </para>
    /// </remarks>
    /// <example>
    /// A caller substitutes the portal id at the point of use, which keeps the emptiness test
    /// visible exactly where the legacy code had it and leaves the choice of culture with the
    /// caller rather than with a configuration object:
    /// <code>
    /// string homeDirectory = portal.HomeDirectory;
    /// if (string.IsNullOrEmpty(homeDirectory))
    /// {
    ///     homeDirectory = string.Format(
    ///         CultureInfo.InvariantCulture,
    ///         options.HomeDirectoryFormat,
    ///         portal.PortalId);
    /// }
    /// </code>
    /// </example>
    public string HomeDirectoryFormat { get; set; } = "Portals/{0}";

    /// <summary>
    /// Display name of the built-in role standing for callers who are not authenticated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy default preserved exactly: <c>Unauthenticated Users</c>. Declared as
    /// <c>glbRoleUnauthUserName</c> at
    /// <c>Library/Components/Shared/Globals.vb:L102</c>. The path matters, because the
    /// migration plan elsewhere refers to <c>Library/Components/Common/Globals.vb</c>, which
    /// does not exist in this repository; the module lives under <c>Shared</c>.
    /// </para>
    /// <para>
    /// Read by in-scope legacy code at <c>PortalController.vb:L557</c>,
    /// <c>ModuleController.vb:L356</c>, <c>TabController.vb:L903</c> and
    /// <c>PortalSecurity.vb:L108</c> and <c>L124</c>. This property is the replacement for
    /// that excluded static constant.
    /// </para>
    /// <para>
    /// It is configurable rather than a fixed constant because the legacy value is a display
    /// name that is also persisted as a row in the <c>Roles</c> table, and the legacy lookup
    /// matches roles by comparing that name as a string. An installation that renamed the
    /// role would silently stop matching if the name were compiled in, so preserving the
    /// legacy comparison faithfully requires the name to be overridable.
    /// </para>
    /// </remarks>
    public string UnauthenticatedRoleName { get; set; } = "Unauthenticated Users";

    /// <summary>
    /// Display name of the built-in role standing for every caller, authenticated or not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy default preserved exactly: <c>All Users</c>. Declared as
    /// <c>glbRoleAllUsersName</c> at <c>Library/Components/Shared/Globals.vb:L100</c>. Read by
    /// in-scope legacy code at <c>PortalController.vb:L555</c>,
    /// <c>ModuleController.vb:L354</c>, <c>TabController.vb:L901</c>,
    /// <c>UserInfo.vb:L330</c> and <c>PortalSecurity.vb:L125</c>.
    /// </para>
    /// <para>
    /// This property's presence needs justifying, because the migration plan names only the
    /// unauthenticated role when it describes what replaces the excluded static module.
    /// Measurement settles the question. Across the five in-scope legacy trees the two special
    /// role names have exactly ten read sites between them, and six of those ten are paired
    /// arms of one and the same construct: the role-name-to-role-id switch appears three
    /// times, identical in shape, at <c>PortalController.vb:L555</c> and <c>L557</c>, at
    /// <c>ModuleController.vb:L354</c> and <c>L356</c>, and at <c>TabController.vb:L901</c>
    /// and <c>L903</c>, and every one of the three reads both names. The role test at
    /// <c>PortalSecurity.vb:L124</c> and <c>L125</c> likewise reads both within a single
    /// expression. Migrated code porting that switch cannot function with only one of the
    /// pair, so this property meets the same "only when a service genuinely reads it" test as
    /// the other three.
    /// </para>
    /// <para>
    /// Configurable for the same reason as the unauthenticated role name: it is a display name
    /// persisted in the <c>Roles</c> table and matched by string comparison.
    /// </para>
    /// </remarks>
    public string AllUsersRoleName { get; set; } = "All Users";

    /// <summary>
    /// Width of the <c>Roles.RoleName</c> column, which bounds both role-name settings.
    /// </summary>
    /// <remarks>
    /// Measured as <c>[RoleName] [nvarchar] (50) NOT NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L117</c> and
    /// carried forward unchanged by both table rebuilds (<c>01.00.04:L1324</c> and
    /// <c>01.00.05:L2750</c>), with no later altering statement in any of the 88 scripts. Because
    /// the legacy lookup matches a role by comparing this display name as a string, a configured
    /// name longer than the column could never match a stored row, so the width is a genuine
    /// constraint on the setting rather than a formatting preference.
    /// </remarks>
    private const int RoleNameMaximumLength = 50;

    /// <summary>
    /// Reports every way in which the values bound onto this instance are unusable, so that a
    /// misconfigured deployment fails while the host is starting rather than when the first
    /// portal is created or the first permission is evaluated.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or
    /// an empty collection when the instance is usable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every failure is reported rather than only the first, because an operator fixing one setting
    /// per restart is the outcome a single-failure result produces.
    /// </para>
    /// <para>
    /// The path checks are deliberately narrow and are about CONFIGURATION, not about caller input.
    /// Both path-shaped settings are composed into a web-relative location, so a rooted path, a
    /// drive or UNC prefix, or a parent-directory segment in either of them would escape the
    /// intended root for every portal at once. Validating the request-supplied home directory of an
    /// individual portal is a separate concern and belongs to the portal request validators.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(AdminTemplateFileName))
        {
            failures.Add(
                $"{SectionName}:{nameof(AdminTemplateFileName)} is not set. The value selects a "
                + "behavioural branch during portal creation by exact name comparison, so an empty "
                + "value matches no template and silently changes which template nodes are "
                + "honoured.");
        }
        else if (ContainsSeparatorOrParentSegment(AdminTemplateFileName))
        {
            failures.Add(
                $"{SectionName}:{nameof(AdminTemplateFileName)} is '{AdminTemplateFileName}', which "
                + "is a path rather than a bare file name. The value is compared by name against a "
                + "template file name, so a path both fails that comparison and points outside the "
                + "template directory.");
        }

        if (string.IsNullOrWhiteSpace(HomeDirectoryFormat))
        {
            failures.Add(
                $"{SectionName}:{nameof(HomeDirectoryFormat)} is not set. Portals with no stored "
                + "home directory would then be given an empty one, placing their content at the "
                + "application root.");
        }
        else
        {
            if (!HomeDirectoryFormat.Contains(PortalIdPlaceholder, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{SectionName}:{nameof(HomeDirectoryFormat)} is '{HomeDirectoryFormat}', which "
                    + $"contains no {PortalIdPlaceholder} placeholder. Without it every portal "
                    + "lacking a stored home directory would be given the same one, so tenants "
                    + "would share a content directory.");
            }

            if (ContainsParentSegment(HomeDirectoryFormat) || IsRootedOrQualified(HomeDirectoryFormat))
            {
                failures.Add(
                    $"{SectionName}:{nameof(HomeDirectoryFormat)} is '{HomeDirectoryFormat}', which "
                    + "is not a plain relative path. The composed value is web-relative and is "
                    + "combined with the application path, so a rooted path, a drive or UNC prefix, "
                    + "or a '..' segment would place portal content outside the application.");
            }

            if (EndsWithSeparator(HomeDirectoryFormat))
            {
                failures.Add(
                    $"{SectionName}:{nameof(HomeDirectoryFormat)} is '{HomeDirectoryFormat}', which "
                    + "ends with a separator. The legacy call site appends its own, so a trailing "
                    + "separator here yields a doubled one.");
            }
        }

        ValidateRoleName(nameof(UnauthenticatedRoleName), UnauthenticatedRoleName, failures);
        ValidateRoleName(nameof(AllUsersRoleName), AllUsersRoleName, failures);

        if (!string.IsNullOrWhiteSpace(UnauthenticatedRoleName)
            && string.Equals(UnauthenticatedRoleName, AllUsersRoleName, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"{SectionName}:{nameof(UnauthenticatedRoleName)} and "
                + $"{SectionName}:{nameof(AllUsersRoleName)} are both '{UnauthenticatedRoleName}'. "
                + "They name two different rows in the Roles table, and the legacy "
                + "role-name-to-role-id switch reads both arms of the pair, so collapsing them "
                + "would resolve one role's identifier for the other.");
        }

        return failures;
    }

    /// <summary>
    /// Adds a failure when a role-name setting is absent or wider than the column that stores it.
    /// </summary>
    /// <param name="settingName">Name of the setting being checked, for the message.</param>
    /// <param name="value">The configured value.</param>
    /// <param name="failures">The collection failures are appended to.</param>
    private static void ValidateRoleName(
        string settingName,
        string value,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(
                $"{SectionName}:{settingName} is not set. The value is matched against a role's "
                + "stored display name, so an empty value matches no role and every permission "
                + "check that depends on it silently fails to resolve.");
            return;
        }

        if (value.Length > RoleNameMaximumLength)
        {
            // Every number reaches the message through an invariant conversion first, so the
            // concatenation below interpolates strings only and cannot pick up a culture.
            string actual = FormattableString.Invariant($"{value.Length}");
            string allowed = FormattableString.Invariant($"{RoleNameMaximumLength}");

            failures.Add(
                $"{SectionName}:{settingName} is {actual} characters long, and the Roles.RoleName "
                + $"column stores {allowed}. A longer name could never match a stored row.");
        }
    }

    /// <summary>
    /// Whether a value that must be a bare file name contains a directory separator or a
    /// parent-directory segment.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> when the value navigates rather than naming.</returns>
    /// <remarks>
    /// Both separator forms are checked regardless of the host platform, because the value is
    /// authored once in configuration and may be composed on Linux or Windows; accepting a
    /// backslash on Linux merely defers the problem to a Windows deployment. This is the strict
    /// check, applied only to <see cref="AdminTemplateFileName"/>: any separator at all disqualifies
    /// a bare file name. <see cref="HomeDirectoryFormat"/> is a path and legitimately contains
    /// separators, so it is checked by <see cref="ContainsParentSegment"/> instead.
    /// </remarks>
    private static bool ContainsSeparatorOrParentSegment(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal)
        || ContainsParentSegment(value);

    /// <summary>
    /// Whether a configured path contains a parent-directory segment.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> when any segment is the parent-directory marker.</returns>
    /// <remarks>
    /// The comparison is per SEGMENT rather than a substring search, so a legitimate name that
    /// merely contains two consecutive dots is not rejected while an actual traversal segment is.
    /// Both separator forms split the value, for the platform reason recorded above.
    /// </remarks>
    private static bool ContainsParentSegment(string value) =>
        value.Split(['/', '\\'], StringSplitOptions.None)
            .Any(segment => string.Equals(segment, ParentSegment, StringComparison.Ordinal));

    /// <summary>
    /// Whether a configured path fragment is rooted, drive-qualified or a UNC path.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> when the value is not purely relative.</returns>
    private static bool IsRootedOrQualified(string value) =>
        value.StartsWith('/')
        || value.StartsWith('\\')
        || value.Contains(':', StringComparison.Ordinal);

    /// <summary>
    /// Whether a configured path fragment ends with a directory separator.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> when a separator would be doubled by the caller.</returns>
    private static bool EndsWithSeparator(string value) =>
        value.EndsWith('/') || value.EndsWith('\\');

    /// <summary>
    /// The placeholder <see cref="HomeDirectoryFormat"/> must contain, receiving the portal id.
    /// </summary>
    private const string PortalIdPlaceholder = "{0}";

    /// <summary>
    /// The parent-directory segment neither path-shaped setting may contain.
    /// </summary>
    private const string ParentSegment = "..";

    // MIGRATION: glbRoleSuperUserName ("Superuser", Globals.vb:L101) is deliberately not
    // carried into this class, because host-level super-user administration is excluded from
    // the migration scope. The numeric special role ids "-1", "-2", "-3" and "-4"
    // (Globals.vb:L95-L98) are likewise excluded: identifier sentinels are a Domain concern,
    // not host configuration, and exposing them here would invite them to be overridden.
}
