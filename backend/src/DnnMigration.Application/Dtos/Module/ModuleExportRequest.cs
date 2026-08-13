namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The state submitted to <c>POST /api/v1/modules/{moduleId}/export</c> to obtain a module's content as a
/// portable document. A boundary contract and nothing more: no navigation property, no tracked state, no
/// behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// THE MODULE IS ADDRESSED BY THE ROUTE, NOT BY THIS BODY, AND THE ASYMMETRY WITH THE IMPORT REQUEST IS
/// DELIBERATE. Export is reached at <c>.../modules/{moduleId}/export</c>, so the identity travels in the
/// path and this contract carries no module identifier at all; import is reached at
/// <c>.../modules/import</c> with no identifier in its route, so <c>ModuleImportRequest</c> must - and does
/// - carry one in its body.
/// </remarks>
public sealed class ModuleExportRequest
{
    /// <summary>
    /// The base name to label the exported document with. Mandatory and non-blank; at most 200 characters.
    /// </summary>
    /// <remarks>
    /// The legacy layer represented "not supplied" as THE EMPTY STRING rather than as an absent value,
    /// because its string sentinel was literally the empty string - which is why its own guard tested
    /// against the empty string and never against null.
    /// </remarks>
    // 5.1 - THE SERVER COMPOSES AND SANITISES THE NAME; THE CALLER SUPPLIES ONE SEGMENT OF FOUR. The legacy
    // composition was `"content." & CleanName(objModule.ModuleName) & "." & CleanName(txtFile.Text) &
    // ".xml"`, so the `content.` prefix, the module-name segment and the `.xml` suffix were all added
    // server-side and only the middle segment came from the form.
    public string? FileName { get; set; }

    /// <summary>
    /// The portal-relative folder the caller intends the document for, or <see langword="null"/> when it
    /// has none. Optional, accepted for parity, and not used to store anything.
    /// </summary>
    /// <remarks>
    /// The legacy folder list is worth recording precisely, because its shape explains the guard that
    /// governed it. Item zero was an inserted, non-selectable prompt whose VALUE WAS A HYPHEN; the real
    /// entries followed, each valued by its folder path and filtered to the folders the operator could read
    /// and write.
    /// </remarks>
    // 5.4 - THE FILE SYSTEM LEFT SCOPE, WHICH IS THIS ENDPOINT'S ONE SUBSTANTIVE FUNCTIONAL REDUCTION. The
    // legacy export wrote its document to the portal home directory beneath the web root, joining this
    // folder to the composed name, then registered the result as a portal file with extension `xml` and
    // content type `application/octet-stream`, having first checked the portal had space for it.
    public string? Folder { get; set; }
}
