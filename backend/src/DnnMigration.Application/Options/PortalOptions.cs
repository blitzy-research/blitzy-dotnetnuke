namespace DnnMigration.Application.Options;

/// <summary>
/// Portal-wide behavioural defaults for the migrated DotNetNuke administration surface.
/// </summary>
/// <remarks>
/// <para>
/// This type is a configuration record and nothing more: a plain object with no dependency
/// on the options, configuration, hosting or HTTP abstractions, and no behaviour. It is
/// declared here in the Application layer and bound by the Api layer, which is why nothing
/// in this file reads configuration, validates itself, or registers itself with a service
/// container. The Application layer's composition entry point deliberately accepts no
/// configuration argument, so it could not bind this type even if that were wanted.
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

    // MIGRATION: glbRoleSuperUserName ("Superuser", Globals.vb:L101) is deliberately not
    // carried into this class, because host-level super-user administration is excluded from
    // the migration scope. The numeric special role ids "-1", "-2", "-3" and "-4"
    // (Globals.vb:L95-L98) are likewise excluded: identifier sentinels are a Domain concern,
    // not host configuration, and exposing them here would invite them to be overridden.
}
