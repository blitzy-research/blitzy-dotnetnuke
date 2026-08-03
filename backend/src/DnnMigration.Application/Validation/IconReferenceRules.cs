namespace DnnMigration.Application.Validation;

// MIGRATION: the legacy screens applied NO shape rule to an icon reference. The role screen read
// whatever its picker produced and assigned it unchanged, and the page screen did the same
// (Website/admin/Tabs/ManageTabs.ascx.vb:L245,L263). A rooted, drive-qualified or parent-traversing
// value therefore reached storage unchallenged. Refusing one is a deliberate divergence rather than a
// reproduction, and it is recorded in MIGRATION_NOTES.md; the reasoning is the same as for the portal
// home directory, whose rules live beside these for the same reason.
//
// MIGRATION: the rules live in ONE place and are applied by EVERY validator that accepts an icon
// reference - role create, role update and page update. This type exists because an earlier revision
// held the check as a private method on the role create validator alone, so the role UPDATE and the
// page update accepted values the create path refused. Two copies of a traversal defence can be
// tightened in one place and left alone in the other, and the weaker copy then defines the
// application's actual behaviour; three copies compound that. Nothing here touches the file system,
// which is what allows a defence of this kind to live in the Application layer at all.
//
// MIGRATION: the check must NOT be read as validating the reference. Both contracts state that an
// icon value is opaque and is stored exactly as submitted - it may be a relative path or a raw
// "fileid=NNN" token - and nothing in the target resolves, fetches or renders one. These rules
// therefore answer only the narrow question of whether a value COULD denote something outside the
// portal's own folder if some later consumer ever did resolve it. A "fileid=NNN" token satisfies them
// unchanged: it carries no parent segment, does not begin at a root, and contains no volume or scheme
// separator.

/// <summary>
/// The shape rules a caller-supplied icon reference must satisfy, shared by every request validator
/// that accepts one.
/// </summary>
/// <remarks>
/// <para>
/// The rules are purely lexical and are expressed as character tests rather than through the
/// file-system APIs or a pattern-matching engine. That choice is deliberate twice over: this layer
/// touches neither, and the check must behave identically whichever platform the API runs on. The
/// legacy application stored Windows-style separators while the migrated API runs on Linux, so both
/// separator characters are treated as rooting characters regardless of the host - a value that is
/// relative on Linux but absolute on Windows is refused, because the stored value outlives the host
/// that accepted it.
/// </para>
/// <para>
/// An absent or empty value is acceptable and is the normal case. The legacy screens stored the empty
/// string when no icon had been picked, and the preserved sentinel convention makes the empty string
/// the representation of an absent one, so neither form may be refused here.
/// </para>
/// </remarks>
internal static class IconReferenceRules
{
    /// <summary>
    /// Reported when a submitted icon reference is rooted, drive-qualified, or traverses above the
    /// portal's own folder.
    /// </summary>
    internal const string NotContainedMessage =
        "Icon File must be a relative path within the portal's own folder.";

    /// <summary>
    /// Determines whether a submitted icon reference stays inside the portal's own folder, accepting
    /// an absent or empty value.
    /// </summary>
    /// <param name="iconFile">The submitted reference, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or a relative reference that neither
    /// begins at a root nor traverses upwards; otherwise <see langword="false"/>.
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
