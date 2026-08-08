namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The state submitted to <c>POST /api/v1/modules/import</c> to load a previously exported document
/// back into a module. A boundary contract and nothing more: no navigation property, no tracked
/// state, no behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// The module is named in this body because the route cannot name it: import is reached at
/// <c>.../modules/import</c>, which has no identifier segment, so the target travels as
/// <see cref="ModuleId"/> while export carries none in its body at all.
/// </para>
/// <para>
/// The payload is XML, the request is JSON, and this type is never XML-serialised.
/// <see cref="Content"/> holds the whole document the export endpoint produced - a <c>content</c>
/// element carrying <c>type</c> and <c>version</c> attributes - and the wire casing of all four
/// members is decided by the API edge, so this type carries no serialisation attribute. The service
/// unwraps that envelope: it requires a <c>content</c> root, reads <c>version</c> (falling back to the
/// installed package version), compares <c>type</c> against the addressed package's cleaned module or
/// friendly name - the legacy wrong-module refusal, re-sourced from the file name to the payload
/// attribute and reported as <c>module.content_type_mismatch</c> - and hands only the inner content to
/// the module's own portability behaviour. No version or content-type member exists here because both
/// arrive inside the payload, and letting a caller assert the type would defeat the check.
/// </para>
/// <para>
/// MIGRATION: the file system left scope, which is this endpoint's one substantive functional
/// reduction. The legacy screen enumerated candidate documents in the portal's home directory and read
/// the chosen one from disk; the document now arrives in band through <see cref="Content"/>, while
/// <see cref="Folder"/> and <see cref="FileName"/> survive as parity metadata nothing resolves. Both
/// are unbounded here because the legacy screen declared no length rule, and are capped instead by the
/// service that records them, since caller-controlled text on a retained log amplifies.
/// </para>
/// <para>
/// Three preconditions govern an import and none is a member here - the module must support portable
/// content, the payload must parse as a <c>content</c>-rooted document, and the caller must hold the
/// edit grant. A member for any of them would let a caller assert its own capability or authority. For
/// the same reason nothing here names a type to activate, an acting user, or a path.
/// </para>
/// </remarks>
// MIGRATION: a stored type name is no longer activated, and on an import endpoint that is this contract's most
// important safety property. The legacy passed the module's stored business-controller class name to a
// reflection-based activator; the behaviour is now resolved from a closed, dependency-injected factory set, so
// a module no registration covers cannot be imported into. Because the class name is resolved from the
// addressed module and never accepted from the caller, a caller cannot steer which code processes the content
// it supplied.
public sealed class ModuleImportRequest
{
    /// <summary>The largest document, in characters, that an import may carry: 1 048 576.</summary>
    /// <remarks>
    /// Declared on the contract type both layers already see, rather than privately inside the
    /// service, because a ceiling only the enforcing layer knows can be discovered only by having a
    /// request refused. Three limits are sized from it: <see cref="FileByteMaximum"/>; the API's
    /// per-action body limit <c>ServiceCollectionExtensions.MaximumImportRequestBodyBytes</c>,
    /// deliberately larger because a document at this ceiling does not fit in a body of the same size
    /// once JSON-encoded; and the reverse proxy's <c>client_max_body_size</c> in
    /// <c>docker/nginx.conf</c>, which must be at least the API's limit - nginx defaults to one
    /// mebibyte, so leaving it unstated would make the proxy the binding limit and produce a refusal
    /// the API's contract does not describe. This is the published NUMBER and not an enforcement
    /// point, and it is typed <see cref="long"/> because the host limits it feeds are.
    /// </remarks>
    public const long ContentCharacterMaximum = 1_048_576;

    /// <summary>The largest file, in bytes, a client may offer for import: 1 048 576.</summary>
    /// <remarks>
    /// Equal to <see cref="ContentCharacterMaximum"/> rather than a fraction of it, and exactly so:
    /// N bytes decoded as UTF-8 yield at most N UTF-16 code units. Published so a client can refuse
    /// an oversized document before reading it rather than learning the ceiling from a refusal.
    /// </remarks>
    public const long FileByteMaximum = ContentCharacterMaximum;

    /// <summary>
    /// The module whose content is being replaced. Mandatory - the only member of this contract that
    /// is - and deliberately nullable so that its absence can be told apart from a legitimate value.
    /// </summary>
    /// <remarks>
    /// <c>Modules.ModuleID</c> is <c>IDENTITY (0, 1)</c>, so zero is a real module and a non-nullable
    /// member would deserialise an omitted <c>moduleId</c> to zero, making "the caller forgot"
    /// indistinguishable from "the caller means module zero". Check it by EXISTENCE, through the
    /// module repository, and by tenant - never by sign: -1 is simultaneously the legacy integer
    /// sentinel and a real portal identifier (<c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c>), so
    /// a caller sending -1 has made a lookup that will fail rather than an omission.
    /// </remarks>
    // MIGRATION: neither legacy screen ever posted a module identifier - both resolved it from ambient page
    // context - so the body placement is a routing consequence. No route-reading authorisation policy can
    // evaluate a permission against a body value, so the edit grant is confirmed by the service after
    // binding, and the portal taken from the request's tenant context is what stops a body-supplied
    // identifier from reaching another tenant's module.
    public int? ModuleId { get; set; }

    /// <summary>
    /// The exported document to load, as text. Effectively mandatory: a blank value is refused, and
    /// the module's own portability behaviour - not this contract - interprets what it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole document belongs here, envelope and all. A caller that strips the <c>content</c>
    /// root first has removed the version and type the server would otherwise have read.
    /// </para>
    /// <para>
    /// A string, because the legacy portability contract's import and export members exchange
    /// strings; <c>IFormFile</c> could not appear here at all, belonging to the web framework this
    /// project does not reference. Nullable with no initial value, so a missing payload cannot be
    /// quietly turned into an empty document - the service treats null, empty and whitespace alike as
    /// not supplied, answering <c>module.content_invalid</c>.
    /// </para>
    /// <para>
    /// No size bound is asserted on this property: a length rule would refuse the document only after
    /// the whole body had been read and bound, which is after the allocation a bound exists to
    /// prevent. Enforcement stays with the host and proxy, which bound the body on the socket, and
    /// with the service, which bounds characters before parsing, both sized from
    /// <see cref="ContentCharacterMaximum"/>.
    /// </para>
    /// </remarks>
    // MIGRATION: this value must never be logged. It is opaque module content of unknown sensitivity and
    // unbounded size, so the boundary cannot know whether a given document holds personal data. Request
    // logging must never emit it, and this type declares no string conversion that could.
    public string? Content { get; set; }

    /// <summary>
    /// The portal-relative folder the document came from, or <see langword="null"/> when the caller
    /// has none. Optional, accepted for parity, and resolved against nothing.
    /// </summary>
    /// <remarks>
    /// The empty string is not a synonym for absent in this member's legacy vocabulary - it meant the
    /// portal ROOT, which the legacy folder list offered as a real entry valued by the empty string.
    /// So <see langword="null"/> means the caller offered none, the empty string keeps its legacy
    /// meaning, and neither is interpreted as a location.
    /// </remarks>
    public string? Folder { get; set; }

    /// <summary>
    /// The name of the document the content came from, or <see langword="null"/> when the caller has
    /// none. Optional parity metadata that no decision depends on, and never interpreted as a path.
    /// </summary>
    /// <remarks>
    /// MIGRATION: its value used to be load-bearing - the legacy decided whether a document belonged
    /// to a module by testing whether its NAME contained the module's own or friendly name. The target
    /// performs the equivalent, stronger check against the payload's <c>type</c> attribute instead, so
    /// nothing branches on this member. The module name and friendly name are deliberately not
    /// accepted here even though the legacy check consumed them: they are resolved server-side from
    /// the module's package record, and accepting them would let a caller choose the values its own
    /// payload is checked against.
    /// </remarks>
    public string? FileName { get; set; }
}
