namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// A boundary contract and nothing more: no navigation property, no tracked state, no behaviour and no
/// domain entity, in either direction.
/// </summary>
// MIGRATION: a stored type name is no longer activated, and on an import endpoint that is this contract's
// most important safety property.
public sealed class ModuleImportRequest
{
    /// <summary>The largest document, in characters, that an import may carry: 1 048 576.</summary>
    public const long ContentCharacterMaximum = 1_048_576;

    /// <summary>The largest file, in bytes, a client may offer for import: 1 048 576.</summary>
    /// <remarks>
    /// Equal to <see cref="ContentCharacterMaximum"/> rather than a fraction of it, and exactly so: N bytes
    /// decoded as UTF-8 yield at most N UTF-16 code units. Published so a client can refuse an oversized
    /// document before reading it rather than learning the ceiling from a refusal.
    /// </remarks>
    public const long FileByteMaximum = ContentCharacterMaximum;

    /// <summary>
    /// The module whose content is being replaced. Mandatory - the only member of this contract that is -
    /// and deliberately nullable so that its absence can be told apart from a legitimate value.
    /// </summary>
    /// <remarks>
    /// <c>Modules.ModuleID</c> is <c>IDENTITY (0, 1)</c>, so zero is a real module and a non-nullable
    /// member would deserialise an omitted <c>moduleId</c> to zero, making "the caller forgot"
    /// indistinguishable from "the caller means module zero".
    /// </remarks>
    public int? ModuleId { get; set; }

    /// <summary>
    /// The exported document to load, as text. Effectively mandatory: a blank value is refused, and the
    /// module's own portability behaviour - not this contract - interprets what it holds.
    /// </summary>
    /// <remarks>
    /// The whole document belongs here, envelope and all. A caller that strips the <c>content</c> root
    /// first has removed the version and type the server would otherwise have read.
    /// </remarks>
    public string? Content { get; set; }

    /// <summary>
    /// The portal-relative folder the document came from, or <see langword="null"/> when the caller has
    /// none. Optional, accepted for parity, and resolved against nothing.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>
    /// The name of the document the content came from, or <see langword="null"/> when the caller has none.
    /// Optional parity metadata that no decision depends on, and never interpreted as a path.
    /// </summary>
    public string? FileName { get; set; }
}
