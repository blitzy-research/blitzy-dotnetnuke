namespace DnnMigration.Application.Validation;

/// <summary>
/// The shape rules a caller-supplied icon reference must satisfy, shared by every request validator that
/// accepts one.
/// </summary>
/// <remarks>
/// An absent or empty value is acceptable and is the normal case. The legacy screens stored the empty
/// string when no icon had been picked, and the preserved sentinel convention makes the empty string the
/// representation of an absent one, so neither form may be refused here.
/// </remarks>
internal static class IconReferenceRules
{
    /// <summary>
    /// Reported when a submitted icon reference is rooted, drive-qualified, or traverses above the portal's
    /// own folder.
    /// </summary>
    internal const string NotContainedMessage =
        "Icon File must be a relative path within the portal's own folder.";

    /// <summary>
    /// Determines whether a submitted icon reference stays inside the portal's own folder, accepting an
    /// absent or empty value.
    /// </summary>
    /// <param name="iconFile">The submitted reference, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or a relative reference that neither begins
    /// at a root nor traverses upwards; otherwise <see langword="false"/>.
    /// </returns>
    internal static bool IsContained(string? iconFile)
    {
        // An absent or empty icon is the normal case: the legacy screens stored the empty string when
        // no icon had been picked, so neither form may be refused here.
        if (string.IsNullOrEmpty(iconFile))
        {
            return true;
        }

        // A parent-directory segment in any position escapes the portal's folder.
        if (iconFile.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        // A leading separator makes the reference absolute on either platform, and a volume or scheme
        // separator turns it into a drive-qualified path or a URI.
        return iconFile[0] is not ('/' or '\\')
            && !iconFile.Contains(':', StringComparison.Ordinal);
    }
}
