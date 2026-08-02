namespace DnnMigration.Application.Validation;

// MIGRATION: this type exists because the legacy code applied NO shape rule to a portal's home
// directory and concatenated the submitted value straight into a path. The signup screen resolved
// ApplicationPath + "/" + HomeDir + "/" and reported failure only when that resolution came back
// empty (Website/admin/Portal/Signup.ascx.vb:L251-L257); the controller repeated the concatenation
// when it mapped the stored value (Library/Components/Portal/PortalController.vb:L994). A rooted,
// drive-qualified or parent-traversing value therefore reached the file system unchallenged, which
// is a path-traversal defect. Preserving it is not something the behaviour-preservation rule can be
// read to require, and both portal request contracts already state that no mapped or absolute path
// is accepted from a caller, so the rules below make an existing promise true rather than invent a
// new constraint. The divergence is recorded in MIGRATION_NOTES.md.
//
// MIGRATION: the rules live in ONE place and are applied by BOTH the create and the update
// validator. An earlier revision had no such rule at all; writing it twice would have been worse
// than writing it once, because two copies of a path-traversal defence can be tightened in one place
// and left alone in the other, and the weaker copy then defines the application's actual behaviour.
// Nothing here reaches the file system, which is what allows a defence of this kind to live in the
// Application layer at all.

/// <summary>
/// The shape rules a caller-supplied portal home directory must satisfy, shared by the portal
/// create and update request validators.
/// </summary>
/// <remarks>
/// <para>
/// The rules are purely lexical. They decide whether a submitted value <b>could</b> denote
/// something outside the root it is later combined with, and they answer that question without
/// creating, reading or inspecting anything on disk. The physical checks - that the resolved
/// directory exists beneath the hosting environment's content root and is writable - remain the
/// responsibility of the service that maps the value, which is both where the legacy code performed
/// them and the only place the content root is available.
/// </para>
/// <para>
/// The type is <see langword="internal"/> deliberately. Its behaviour is reachable, and is
/// exercised, through the two public validators that apply it; exposing it separately would invite
/// a third caller to apply the rules partially.
/// </para>
/// </remarks>
internal static class PortalHomeDirectoryRules
{
    /// <summary>
    /// Maximum length of the portal-relative home directory.
    /// </summary>
    /// <remarks>
    /// The terminal column is <c>HomeDirectory varchar(100) NOT NULL DEFAULT ''</c>, introduced at
    /// <c>02.02.02.SqlDataProvider:L3823</c> and re-asserted by
    /// <c>03.01.01.SqlDataProvider:L1123</c>; it supersedes the baseline
    /// <c>UploadDirectory [nvarchar] (100) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L80</c>. Both widths are 100, and the legacy text boxes agree
    /// (<c>signup.ascx:L47</c>). No later statement in the 88-script chain widens either column.
    /// </remarks>
    internal const int MaximumLength = 100;

    /// <summary>
    /// The message reported when a submitted home directory is rejected.
    /// </summary>
    /// <remarks>
    /// The <c>InvalidHomeFolder</c> wording, taken verbatim from
    /// <c>Website/admin/Portal/App_LocalResources/Signup.ascx.resx:L288-L289</c>. The legacy trigger
    /// was a failed physical-path resolution rather than a shape rule, but the wording describes
    /// the outcome a caller needs and is reused so the rejection stays recognisable. It never
    /// echoes the submitted value, so a rejected path cannot be reflected back to the caller.
    /// </remarks>
    internal const string InvalidMessage = "The Home Folder you specified is not valid.";

    /// <summary>
    /// The single separator a submitted home directory may contain. The legacy screen, the legacy
    /// default (<c>PortalController.vb:L991-L992</c>) and the configured format all use it.
    /// </summary>
    private const char DirectorySeparator = '/';

    /// <summary>
    /// The relative-path segment denoting the current directory. Rejected because it is redundant
    /// and because accepting it would mean storing a value whose stored form and canonical form
    /// differ.
    /// </summary>
    private const string CurrentSegment = ".";

    /// <summary>
    /// The relative-path segment denoting the parent directory: the traversal vector itself.
    /// </summary>
    private const string ParentSegment = "..";

    /// <summary>
    /// Characters a submitted home directory may never contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set is the Windows invalid-path set together with the backslash and the volume
    /// separator. <c>Path.GetInvalidPathChars</c> is deliberately <b>not</b> used: it is
    /// platform-dependent and on Linux it contains only the null character, so relying on it would
    /// make this rule weakest on precisely the platform the application runs on
    /// (<c>mcr.microsoft.com/dotnet/aspnet:8.0-alpine</c>). The value is also stored in a database
    /// a Windows-hosted tool may later read, so the stricter set applies wherever the check runs.
    /// </para>
    /// <para>
    /// The backslash is forbidden rather than normalised, because normalising it would admit two
    /// different stored representations of one directory. The volume separator is forbidden because
    /// it introduces a drive-qualified form, and <c>Path.IsPathRooted</c> does not recognise that
    /// form as rooted on Linux - a value such as <c>C:\Windows</c> would otherwise pass a
    /// rootedness test on this platform and be stored as though it were relative.
    /// </para>
    /// </remarks>
    private const string ForbiddenCharacters = "\\:*?\"<>|";

    /// <summary>
    /// A synthetic, never-touched root used only to prove containment lexically.
    /// </summary>
    /// <remarks>
    /// It exists so containment is an asserted property rather than a claim. It is not the
    /// application's content root, nothing is read from or written to it, and the trailing
    /// separator is significant: without it, a sibling directory whose name merely began with this
    /// value would satisfy the prefix test.
    /// </remarks>
    private const string ContainmentProbeRoot = "/dnn-portal-home-containment-probe/";

    /// <summary>
    /// Reports whether a submitted home directory is a safe, portal-relative directory that cannot
    /// denote anything outside the root it is later combined with.
    /// </summary>
    /// <param name="submittedDirectory">
    /// The submitted value, which may be absent. An absent or empty value is accepted because it is
    /// the caller's request for the server-derived default rather than a malformed path: the legacy
    /// screen sent the empty string when the user left the pre-filled placeholder untouched
    /// (<c>Signup.ascx.vb:L245-L249</c>) and the controller then derived
    /// <c>Portals/&lt;portalId&gt;</c> (<c>PortalController.vb:L991-L992</c>).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or a relative directory built only
    /// from ordinary segments separated by <c>/</c>; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Rejected forms, each for a specific reason: a value that is only white space, because it is
    /// neither a directory nor an omission; any character in <see cref="ForbiddenCharacters"/> or
    /// any control character, so no backslash, drive-qualified or wildcard form survives; a leading
    /// separator, which covers both the rooted form <c>/x</c> and the UNC form
    /// <c>//server/share</c>; an empty or blank segment, so <c>a//b</c> cannot be stored in a form
    /// that differs from its canonical form; and any segment equal to <see cref="CurrentSegment"/>
    /// or <see cref="ParentSegment"/>.
    /// </para>
    /// <para>
    /// One trailing separator is tolerated and trimmed before segments are examined, because the
    /// legacy code appended a separator itself when it mapped the value
    /// (<c>Signup.ascx.vb:L254</c>), so a caller who typed one submitted a value the legacy screen
    /// accepted. Rejecting it would be a tightening with no security benefit.
    /// </para>
    /// <para>
    /// The closing containment assertion should be unreachable, because every escape vector is
    /// rejected above. It is performed anyway so that containment is a checked property rather than
    /// a claim resting on the completeness of the preceding list - which is the assumption that
    /// produced the defect this type exists to close.
    /// </para>
    /// </remarks>
    internal static bool IsSafeRelativeDirectory(string? submittedDirectory)
    {
        if (string.IsNullOrEmpty(submittedDirectory))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(submittedDirectory))
        {
            return false;
        }

        foreach (char character in submittedDirectory)
        {
            if (char.IsControl(character)
                || ForbiddenCharacters.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (submittedDirectory[0] == DirectorySeparator)
        {
            return false;
        }

        string candidate = submittedDirectory[^1] == DirectorySeparator
            ? submittedDirectory[..^1]
            : submittedDirectory;

        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (string segment in candidate.Split(DirectorySeparator))
        {
            if (string.IsNullOrWhiteSpace(segment)
                || string.Equals(segment, CurrentSegment, StringComparison.Ordinal)
                || string.Equals(segment, ParentSegment, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return RemainsContained(candidate);
    }

    /// <summary>
    /// Canonicalises a candidate relative directory against a synthetic root and reports whether the
    /// result is still strictly beneath that root.
    /// </summary>
    /// <param name="relativeDirectory">
    /// The candidate directory, already stripped of any trailing separator.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the canonical form remains beneath
    /// <see cref="ContainmentProbeRoot"/>; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <c>Path.GetFullPath</c> performs no input or output: it neither creates nor inspects anything
    /// on disk, and because the combined path is already rooted by the probe root the result cannot
    /// depend on the process's current directory either. That is what keeps this check inside a
    /// layer which must not reach the file system.
    /// </para>
    /// <para>
    /// The combination is deliberately the vulnerable one. <c>Path.Combine</c> discards its first
    /// argument when the second is rooted, so a rooted candidate produces a canonical form outside
    /// the probe root and is reported as uncontained - which is precisely what makes this a
    /// containment check rather than a formatting exercise.
    /// </para>
    /// <para>
    /// The two caught exceptions are the documented failures of <c>Path.GetFullPath</c>. Both are
    /// answered with a rejection rather than allowed to propagate: an exception escaping a
    /// validation predicate would surface as an unhandled fault, converting a field-level problem
    /// into a server error - the same defect class the credential rules were changed to avoid.
    /// Neither is reachable through a request boundary, because invalid characters are rejected
    /// before this point and the length is capped at <see cref="MaximumLength"/>.
    /// </para>
    /// </remarks>
    private static bool RemainsContained(string relativeDirectory)
    {
        try
        {
            string canonical = Path.GetFullPath(
                Path.Combine(ContainmentProbeRoot, relativeDirectory));

            return canonical.Length > ContainmentProbeRoot.Length
                && canonical.StartsWith(ContainmentProbeRoot, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }
}
