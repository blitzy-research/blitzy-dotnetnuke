namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: the field set comes from Website/admin/Modules/export.ascx, whose only inputs are
// cboFolders and txtFile - a folder picker and a file name - driven by cmdExport. Export.ascx.vb then
// asked the module's own business controller for its content and wrote it into that file beneath the
// portal's home directory.
//
// MIGRATION: the content itself is returned in the response rather than written to a server file, and
// this is the one substantive behavioural change in the export path. Writing to a server path from an
// API is a file-system side effect on a containerised, read-only-by-default runtime, and the legacy
// destination - the portal home directory under the web root - does not exist in the target topology.
// The folder and file name are therefore accepted and echoed so an existing operator workflow keeps its
// naming, while the payload travels to the caller. Recorded as a deliberate difference.
//
// MIGRATION: the module's content is produced by the module's own portability contract, which the legacy
// code reached by passing a stored type name to a reflection-based activator at five call sites. The
// target resolves it from a closed, dependency-injected set through a factory, so a module that has not
// been registered with that factory cannot be exported and the attempt fails with a reason rather than
// silently producing an empty document.
//
// MIGRATION: the module identifier is absent. It is taken from the route of
// POST /api/v1/portals/{portalId}/modules/{moduleId}/export, so a body-supplied identifier could export a different module
// than the one addressed.

/// <summary>
/// The state submitted to <c>POST /api/v1/portals/{portalId}/modules/{moduleId}/export</c> to export a module's content.
/// </summary>
/// <remarks>
/// Both members are naming hints for the produced document; neither causes a write to the server's file
/// system. The exported content is returned to the caller.
/// </remarks>
public sealed class ModuleExportRequest
{
    /// <summary>
    /// The name to give the exported document. Required, at most 200 characters.
    /// </summary>
    /// <remarks>
    /// Must not contain a path separator or a parent-directory segment: the value names a file and is
    /// never interpreted as a path, which closes the traversal the legacy screen's free-text field
    /// allowed.
    /// </remarks>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The portal-relative folder the operator intended the document for, or <see langword="null"/> for
    /// the portal root. Echoed back for naming only.
    /// </summary>
    public string? Folder { get; set; }
}
