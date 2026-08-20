namespace DnnMigration.Application.Options;

/// <summary>Portal-wide behavioural defaults for the migrated DotNetNuke administration surface.</summary>
/// <remarks>
/// Sources examined that yielded nothing.
/// </remarks>
public sealed class PortalOptions
{
    // MIGRATION: Website/release.config application settings (L34-L55) were read in full and
    // yielded no property here; all eleven active keys are excluded or owned by another layer.

    /// <summary>Name of the configuration section this type is bound from.</summary>
    public const string SectionName = "Portal";

    // Bounds and required shapes for the values above, held here beside the settings they constrain and
    // const rather than configurable.

    /// <summary>The substitution placeholder <see cref="HomeDirectoryFormat"/> must contain: <c>{0}</c>.</summary>
    /// <remarks>
    /// This is the single most consequential requirement in this type. The format is expanded once per
    /// portal, and the placeholder is the only part of it that differs between portals.
    /// </remarks>
    public const string HomeDirectoryPortalPlaceholder = "{0}";

    /// <summary>
    /// Longest acceptable <see cref="AdminTemplateFileName"/> or <see cref="HomeDirectoryFormat"/> value:
    /// 260 characters.
    /// </summary>
    public const int MaximumPathValueLength = 260;

    /// <summary>
    /// The parent-directory segment that neither <see cref="AdminTemplateFileName"/> nor <see
    /// cref="HomeDirectoryFormat"/> may contain: <c>..</c>.
    /// </summary>
    public const string ParentDirectorySegment = "..";

    /// <summary>
    /// File name of the administration portal template, which is parsed for every newly created portal in
    /// addition to the portal template the caller selected.
    /// </summary>
    /// <remarks>
    /// The value is load-bearing rather than cosmetic. Matching it selects a behavioural branch that
    /// suppresses parsing of a template's &lt;settings&gt;, &lt;roles&gt;, &lt;folders&gt; and
    /// &lt;files&gt; nodes, as the legacy comment at <c>L1379</c> states and the guard at <c>L1380</c>
    /// implements.
    /// </remarks>
    public string AdminTemplateFileName { get; set; } = "admin.template";

    /// <summary>
    /// Format string yielding a portal's default home directory, applied only when the portal has no home
    /// directory stored against it. The single <c>{0}</c> placeholder receives the portal id.
    /// </summary>
    /// <remarks>
    /// The home directory actually in force for a portal is a column on the <c>Portal</c> domain entity,
    /// not a value on this class, and turning the resulting relative path into a physical one is an Api or
    /// Infrastructure concern — the legacy code performed that mapping separately at <c>L994</c>, combining
    /// the relative path with the application path. Neither of those belongs here.
    /// </remarks>
    public string HomeDirectoryFormat { get; set; } = "Portals/{0}";

    /// <summary>
    /// Reports every way in which the values bound onto this instance are unusable, so that a misconfigured
    /// deployment fails while the host is starting rather than when the first portal is created or the
    /// first permission is evaluated.
    /// </summary>
    /// <returns>
    /// One message per failure, each naming the configuration path an operator has to change, or an empty
    /// collection when the instance is usable.
    /// </returns>
    /// <remarks>
    /// Every failure is reported rather than only the first, because an operator fixing one setting per
    /// restart is the outcome a single-failure result produces.
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

        // No role-name validation, and none is missing. The two special role names are compiled-in domain
        // constants rather than settings, so there is no configured value to check for emptiness, for width
        // against the Roles.RoleName column, or for collision with its partner.
        return failures;
    }

    /// <summary>
    /// Whether a value that must be a bare file name contains a directory separator or a parent-directory
    /// segment.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> when the value navigates rather than naming.</returns>
    private static bool ContainsSeparatorOrParentSegment(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal)
        || ContainsParentSegment(value);

    /// <summary>Whether a configured path contains a parent-directory segment.</summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> when any segment is the parent-directory marker.</returns>
    private static bool ContainsParentSegment(string value) =>
        value.Split(['/', '\\'], StringSplitOptions.None)
            .Any(segment => string.Equals(segment, ParentSegment, StringComparison.Ordinal));

    /// <summary>Whether a configured path fragment is rooted, drive-qualified or a UNC path.</summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> when the value is not purely relative.</returns>
    private static bool IsRootedOrQualified(string value) =>
        value.StartsWith('/')
        || value.StartsWith('\\')
        || value.Contains(':', StringComparison.Ordinal);

    /// <summary>Whether a configured path fragment ends with a directory separator.</summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> when a separator would be doubled by the caller.</returns>
    private static bool EndsWithSeparator(string value) =>
        value.EndsWith('/') || value.EndsWith('\\');

    /// <summary>The placeholder <see cref="HomeDirectoryFormat"/> must contain, receiving the portal id.</summary>
    private const string PortalIdPlaceholder = "{0}";

    /// <summary>The parent-directory segment neither path-shaped setting may contain.</summary>
    private const string ParentSegment = "..";
}
