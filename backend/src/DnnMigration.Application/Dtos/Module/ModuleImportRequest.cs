namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: the field set comes from Website/admin/Modules/import.ascx, whose only inputs are
// cboFolders and cboFiles - a folder picker and, cascading from it, a picker over the files already
// present in that folder - driven by cmdImport. Import.ascx.vb read the chosen server file and handed
// its content to the module's own business controller.
//
// MIGRATION: the content is supplied by the caller rather than read from a server file, which is the
// counterpart of the change documented on the export request. A containerised API has no portal home
// directory to enumerate and no server-side file picker to cascade from, so Content carries the document
// and the folder and file name remain as provenance the audit log records. Recorded as a deliberate
// difference.
//
// MIGRATION: the target module is carried in the body because the endpoint is POST /api/v1/modules/import
// with no identifier in its route. That asymmetry with the export endpoint is intentional and follows the
// planned endpoint surface; the service validates that the named module exists in the caller's portal
// before importing, so the body-supplied identifier cannot reach another tenant's module.
//
// MIGRATION: import is delegated to the module's own portability contract, resolved from the closed,
// dependency-injected factory set rather than by activating a stored type name. A module that has not
// been registered with that factory cannot be imported into, and the attempt fails with a reason rather
// than appearing to succeed.

/// <summary>
/// The state submitted to <c>POST /api/v1/modules/import</c> to load previously exported content into a
/// module.
/// </summary>
/// <remarks>
/// Content is replaced rather than merged, which is what the legacy import performed: the module's own
/// portability contract decides how the document is applied, and the whole import is committed as one
/// unit of work so a partially applied document is impossible.
/// </remarks>
public sealed class ModuleImportRequest
{
    /// <summary>
    /// The module to import into. Required.
    /// </summary>
    /// <remarks>
    /// <c>Modules.ModuleID</c> is <c>IDENTITY (0, 1)</c>, so 0 is a legitimate module and only a negative
    /// value is rejected outright. The service additionally confirms the module belongs to the caller's
    /// portal.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>The exported document to load. Required and non-blank; the module's portability contract interprets it.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The name of the document the content came from, recorded as provenance, or <see langword="null"/>
    /// when the caller has none. At most 200 characters and never interpreted as a path.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>The portal-relative folder the document came from, recorded as provenance, or <see langword="null"/> when the caller has none.</summary>
    public string? Folder { get; set; }
}
