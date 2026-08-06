namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The state submitted to <c>POST /api/v1/modules/import</c> to load a previously
/// exported document back into a module. A boundary contract and nothing more: no navigation property,
/// no tracked state, no behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// THE MODULE IS NAMED IN THIS BODY BECAUSE THE ROUTE CANNOT NAME IT, and the asymmetry with the export
/// request is the most important structural fact about this type. Export is reached at
/// <c>.../modules/{moduleId}/export</c>, so its target travels in the path and
/// <c>ModuleExportRequest</c> carries no module identifier at all. Import is reached at
/// <c>.../modules/import</c>, which has NO identifier segment whatsoever, so the target has to travel in
/// the body - and does, as <c>ModuleId</c>. Anyone tempted to harmonise the pair should change neither:
/// deleting the identifier here would leave the endpoint unable to say what it is importing into, and
/// inventing a <c>.../modules/{moduleId}/import</c> route would make a non-idempotent operation read as
/// though it were addressable. The endpoint is <c>POST /api/v1/modules/import</c>; the request host
/// resolves its tenant before the service refuses a body-supplied identifier belonging to another
/// tenant's module.
/// </para>
/// <para>
/// FOUR MEMBERS IS A MEASURED ANSWER. The legacy import screen was seventeen lines of markup offering
/// exactly two inputs - a folder picker and, cascading from it, a picker over the documents already
/// sitting in that folder - beside an import and a cancel button. It carried no declarative validator of
/// any kind and no upload control of any kind. Two of the members below are those two inputs; the third
/// is the module identifier the route no longer supplies; the fourth is the document itself, which the
/// caller now has to send because there is no longer a shared file system to fetch it from.
/// </para>
/// <para>
/// THE PAYLOAD IS XML, THIS REQUEST IS JSON, AND THIS TYPE IS NEVER XML-SERIALISED. The value of
/// <c>Content</c> is the document the export endpoint produced: an XML declaration followed by an element
/// named <c>content</c> carrying <c>type</c> and <c>version</c> attributes. The request carrying it is
/// JSON like every other in this API, and the one central serialisation policy at the API edge decides
/// the wire casing of all four members, so this type carries no serialisation attribute of any kind. The
/// distinction earns an explicit statement precisely because the payload is XML: a reader who assumed the
/// request must therefore be XML too would reach for serialisation attributes and would be wrong twice.
/// </para>
/// <para>
/// THE SERVICE UNWRAPS THE ENVELOPE; THE CALLER DOES NOT. The whole document travels on the wire,
/// envelope included. The service parses it, requires a <c>content</c> root, reads the <c>version</c>
/// attribute from that root - falling back to the installed package version when the attribute is absent
/// - and hands only the element's inner content to the module's own portability behaviour. That is why
/// no version member exists here: the version arrives INSIDE the payload, and a caller is not expected
/// to strip the envelope before sending it.
/// </para>
/// <para>
/// <strong>THE WRONG-MODULE REFUSAL IS NOT CURRENTLY REPRODUCED, AND THAT IS A KNOWN PARITY GAP.</strong>
/// The legacy accepted or refused a document by testing whether its NAME contained the module's own name
/// or its friendly name, reporting "The import file specified is not the correct type for this module"
/// when it did not, and the module controller then compared the payload's <c>type</c> attribute against
/// the same two values. The import path here reads only the <c>version</c> attribute; the <c>type</c>
/// attribute is written by the EXPORT path and is never read back, and <c>FileName</c> is inspected by
/// nothing. A document exported from one module is therefore accepted into another, and the module's own
/// portability behaviour is left to reject content it does not recognise. Closing the gap means reading
/// the <c>type</c> attribute in <c>ModuleService.ImportModuleContentAsync</c> and comparing it with the
/// addressed package's module name - the stronger of the two legacy checks - not adding a member here: a
/// caller that supplied the expected type would be choosing the value its own payload is checked against.
/// </para>
/// <para>
/// THE FILE SYSTEM LEFT SCOPE, WHICH IS THIS ENDPOINT'S ONE SUBSTANTIVE FUNCTIONAL REDUCTION. The legacy
/// screen enumerated candidate documents from the portal's home directory and then read the chosen one
/// from disk; the file subsystem that did both is excluded from this migration, and the destination does
/// not exist in a containerised topology in any case. So the document arrives in band through
/// <c>Content</c>, while <c>Folder</c> and <c>FileName</c> survive as optional parity metadata that
/// nothing resolves. The folder-to-document cascade - the legacy folder picker posted back to repopulate
/// the document picker - becomes a dependent select in the client with no API counterpart at all.
/// </para>
/// <para>
/// THE DOCUMENT NAME IS NO LONGER A CORRECTNESS BOUNDARY. The legacy accepted or refused a document by
/// testing whether its NAME contained the module's own name, reporting "The import file specified is not
/// the correct type for this module" when it did not. The target performs the equivalent check against
/// the <c>type</c> attribute of the payload instead - accepting the cleaned module name or the cleaned
/// friendly name, exactly as the legacy screen's second check did, and failing with
/// <c>module.content_type_mismatch</c> otherwise. The refusal is therefore preserved and re-sourced
/// rather than lost, and <c>FileName</c> is metadata that no decision depends on.
/// </para>
/// <para>
/// <c>Folder</c> AND <c>FileName</c> ARE UNBOUNDED HERE AND BOUNDED AT THE AUDIT BOUNDARY, deliberately.
/// This request declares no validator and no length limit, because the legacy screen declared none either
/// and inventing one would refuse requests the legacy accepted. The two values are nonetheless recorded on
/// the audit trail, and caller-controlled text on a retained log is both a disclosure channel and an
/// amplification vector, so the service that records them caps each value and normalises its control
/// characters before constructing the event. The bound therefore lives where the hazard is rather than on
/// the wire contract, and a caller's request is never refused for the length of a label nothing resolves.
/// </para>
/// <para>
/// THREE PRECONDITIONS GOVERN AN IMPORT AND NOT ONE OF THEM IS A MEMBER HERE. The module has to support
/// portable content, the payload has to parse as a <c>content</c>-rooted XML document, and the caller has
/// to hold the edit grant on the module - the legacy screen's help text read "Administrators can import
/// content for the specified module." The first is resolved from the module's own package record, the
/// second by parsing <c>Content</c>, and the third by the service after binding, because the target
/// arrives in the body where no route-reading policy can see it. A member for any of them would let a
/// caller assert its own capability or its own authority.
/// </para>
/// <para>
/// NOTHING HERE NAMES A TYPE TO ACTIVATE, A PATH TO READ OR A USER TO ACT AS, and those three absences
/// are the endpoint's safety properties rather than oversights. The stored business-controller class name
/// is excluded because accepting it on an import endpoint would be arbitrary remote activation over
/// caller-supplied content; every acting-user member because the legacy took the operator from ambient
/// state and accepting one would be an impersonation vector; every path, home-directory and portal member
/// because on an import endpoint a caller-supplied path is simultaneously a cross-tenant vector and an
/// arbitrary-file-read vector. No member here is computed, none carries a validation attribute, and
/// <c>Content</c> is never logged.
/// </para>
/// </remarks>
// MIGRATION: 5.6 - VALIDATION WAS IMPERATIVE, SINGULAR AND PARTLY UNLOCALISED IN THE LEGACY, AND IS
//   DECLARATIVE HERE. The legacy markup declared no validator at all. Its one rule was a single guard in
//   the click handler, `If Not cboFiles.SelectedItem Is Nothing`, and when it failed the operator was
//   shown the string literal "Please specify the file to import" - HARDCODED IN THE CODE-BEHIND AND NEVER
//   LOCALISED, uniquely among the messages on this screen. Every other message on the import path was
//   resolved from the screen's own resource file, which holds twelve entries and NEITHER a validation
//   entry NOR an empty-content entry, while the export screen's resource file holds both. The absent
//   validation entry is precisely why the literal exists: there was nothing to resolve. Recorded as a
//   legacy defect and deliberately not normalised into a localised message here, because localisation
//   itself is excluded from this migration and inventing a resource key would misrepresent what the
//   legacy did.
//
//   In the target that one guard becomes two, in Validation/ModuleImportRequestValidator.cs: the document
//   must be present, and the module it is destined for must be named, because this route names none where
//   the legacy screen took its target from ambient page context. The module service keeps both guards as
//   well, because it is reachable from callers the boundary validator does not sit in front of; the
//   wordings are identical and the pair is annotated in both places.
//
//   AN EARLIER REVISION OF THIS NOTE ASSERTED THAT NO VALIDATOR TYPE EXISTED FOR THIS REQUEST, and the
//   consequence was a published-contract defect: the import operation advertises ValidationProblemDetails
//   for 400 - the document carrying a map of the refused members - while an omitted moduleId or an empty
//   document produced a plain problem document with no map in it. The declaration described a response
//   that could not occur. The validator is what makes it occur. What has NOT changed is the shape of the
//   identifier itself, and the note on that member explains why it must stay nullable regardless.

// MIGRATION: THE CURRENT FAILURE MESSAGES ARE THE SERVICE'S OWN, NOT THE LEGACY WORDING, and they are
//   quoted here so that a reader comparing the two knows exactly what a caller sees. In order of
//   evaluation, ModuleService.ImportModuleContentAsync answers: "The module to import into must be
//   supplied." (module.request_invalid) when ModuleId is null; "Module {id} does not exist in portal
//   {portalId}." (module.not_found); a refusal carrying the caller's missing edit grant; "Module {id} does
//   not support content import." (module.not_portable) where the legacy said "The module selected does not
//   support the importing of content"; "The submitted document is empty." (module.content_invalid), which
//   the legacy import screen had no message for at all; "The submitted document is not well-formed XML:
//   {parser message}" (module.content_invalid) where the legacy said "The file you selected does not
//   contain a valid XML structure"; and "The submitted document must have a <content> root element."
//   (module.content_invalid), which the legacy did not check. The outcomes correspond one for one apart
//   from the wrong-module refusal noted above; the wording does not, and each failure now arrives as a
//   typed reason translated into a problem document at the edge rather than as a rendered label.
//
// MIGRATION: VALIDATION WAS IMPERATIVE AND SINGULAR, AND THERE IS DELIBERATELY NO VALIDATOR TYPE FOR
//   THIS REQUEST. The legacy markup declared no validator at all. Its one rule was a single guard in the
//   click handler, `If Not cboFiles.SelectedItem Is Nothing`, and when it failed the operator was shown
//   the unlocalised string literal "Please specify the file to import" - unique among the messages on
//   that screen, because the screen's twelve-entry resource file held neither a validation entry nor an
//   empty-content entry. Recorded as a legacy defect and deliberately not normalised into a localised
//   message, because localisation is excluded from this migration and inventing a resource key would
//   misrepresent what the legacy did. In the target the rule is a failure reason raised by the module
//   service rather than a class under the declarative validation folder.
//
// MIGRATION: A STORED TYPE NAME IS NO LONGER ACTIVATED, and on an import endpoint that is the single most
//   important safety property this contract has. The legacy import passed the module's stored
//   business-controller class name to a reflection-based activator and cast the result to the portability
//   contract - one of five such activation sites across the module code. The target resolves the behaviour
//   from a closed, dependency-injected factory set instead, so a module no registration covers cannot be
//   imported into and says so. Because the class name is resolved from the addressed module and never
//   accepted from the caller, a caller cannot steer WHICH code processes content it supplied; a member
//   naming a type would turn this endpoint into arbitrary remote activation over an attacker-chosen
//   payload. One sentinel deserves noting while it is in view: the capability bit field read as -1 did
//   not mean "no capabilities" but "installation not yet complete", and the legacy deferred the work
//   through its event queue in that case - another value that is data rather than absence.
//
// MIGRATION: NO ACTING-USER IDENTIFIER CROSSES THIS BOUNDARY, AND THE LEGACY IS THE REASON RATHER THAN
//   THE OBSTACLE. The portability contract's fourth parameter IS a user identifier, so the temptation to
//   accept one here is real. The legacy never posted it: the import screen passed the current user's
//   identifier from ambient page state, and the portal-template import path passed the portal's
//   administrator identifier. The target resolves the caller from the authenticated principal, so a
//   UserId on the wire would be both redundant and spoofable - it would let any authorised caller
//   attribute an import to any user, which is a plain impersonation vector on an endpoint that writes
//   content. There is nothing to stamp either: across all eighty-eight upgrade scripts the module and
//   placement tables carry no audit columns whatsoever, so no audit member is omitted here that the
//   schema could have stored.
//
// MIGRATION: TWO LEGACY DEFECTS ARE RECORDED HERE AND NOT REPRODUCED. The portal-template import path
//   unwrapped a character-data section with hard-coded offsets, `Substring(9, strcontent.Length - 12)`,
//   without ever checking that the content was so wrapped, so any payload not produced by its own writer
//   was silently truncated at both ends. And the export and import handlers between them contain FIVE
//   BARE CATCH BLOCKS - two in the import code-behind, one in the export code-behind and one in each of
//   the two content helpers on the module controller, the latter pair commented "ignore errors" - which
//   collapsed every distinct failure into the one generic message "An error occurred during the import".
//   A missing behaviour, a non-conforming one, an unreadable document and a genuinely malformed one were
//   indistinguishable to the operator, and the malformed case is the worst of them because a more
//   specific message already existed and was masked. The target surfaces a typed failure reason for each,
//   which is a deliberate improvement in diagnosability rather than a change of outcome.
public sealed class ModuleImportRequest
{
    /// <summary>
    /// The largest document, in characters, that an import may carry: 1 048 576.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE INNERMOST NUMBER OF THE TRANSFER CONTRACT, AND THE ONE EVERY OTHER LIMIT IS DERIVED FROM. It is
    /// declared here, on the contract type both layers already see, rather than privately inside the
    /// service, because a ceiling that only the enforcing layer knows cannot be published to the caller -
    /// and a caller that cannot know the ceiling can only discover it by having a request refused.
    /// </para>
    /// <para>
    /// The three derived limits, each one bounding the layer inside it:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <see cref="FileByteMaximum"/>, the client-visible file limit. A file of N bytes decoded as UTF-8
    /// yields at most N UTF-16 code units - a one-byte sequence produces one, and every multi-byte
    /// sequence produces fewer code units than bytes - so a file bounded by this value in BYTES cannot
    /// exceed this ceiling in CHARACTERS. That is why the two numbers are equal rather than one being a
    /// fraction of the other, and it is exact rather than approximate.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The API's per-action import body limit, <c>ServiceCollectionExtensions.MaximumImportRequestBodyBytes</c>,
    /// which is computed from this value and the worst-case JSON expansion of a character. It is
    /// deliberately much larger, because a document at this ceiling does not fit in a body of the same
    /// size once it has been JSON-encoded.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The reverse proxy's <c>client_max_body_size</c> for the API location in <c>docker/nginx.conf</c>,
    /// which must be at least the API's import body limit. nginx defaults to one mebibyte, so leaving it
    /// unstated made the PROXY the binding limit of the whole path - and a body the proxy refuses never
    /// reaches the API, so the caller receives a refusal the API's own contract does not describe.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// NO VALIDATION RULE IS DECLARED AGAINST <see cref="Content"/> FOR THIS, and that is unchanged: see the
    /// note on that member. This is the published NUMBER, not an enforcement point. Enforcement stays where
    /// it belongs - the proxy and the host bound the body before anything is allocated, and the service
    /// bounds the document's characters before it is parsed - which is also why the value is a
    /// <see cref="long"/>: the host limits it feeds are expressed in that type.
    /// </para>
    /// </remarks>
    public const long ContentCharacterMaximum = 1_048_576;

    /// <summary>
    /// The largest file, in bytes, a client may offer for import: 1 048 576.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="ContentCharacterMaximum"/> by the UTF-8 bound set out there, and published so
    /// that a client refuses an oversized document BEFORE reading it rather than decoding an arbitrary local
    /// file into memory and discovering the ceiling from a refusal. A client that checks nothing is still
    /// bounded by the proxy and the host; a client that checks this is bounded before it allocates.
    /// </remarks>
    public const long FileByteMaximum = ContentCharacterMaximum;

    /// <summary>
    /// The module whose content is being replaced. Mandatory - the only member of this contract that is -
    /// and deliberately nullable so that its absence can be told apart from a legitimate value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NULLABLE IS THE DESIGN, NOT AN OVERSIGHT, AND THE REASON IS A COLLISION IN THE SCHEMA.
    /// <c>Modules.ModuleID</c> is declared <c>IDENTITY (0, 1)</c>, so ZERO IS A REAL MODULE - the very
    /// first one a portal ever creates. A plain non-nullable integer would deserialise an omitted
    /// <c>moduleId</c> to zero, making "the caller forgot the identifier" indistinguishable from "the
    /// caller means module zero". Nullability makes the omission observable: <see langword="null"/> means
    /// nothing was supplied and is refused, while zero is a lookup like any other.
    /// </para>
    /// <para>
    /// WHY THIS DIFFERS FROM <c>CreateModuleRequest.ModuleDefId</c> AND <c>CreateModuleRequest.TabId</c>,
    /// WHICH ARE BOTH PLAIN INTEGERS. Those two are members a caller always sends, and a validator cannot
    /// tell an omitted value type from a deliberate zero either - so their contracts carry no presence rule
    /// and the store's lookup is what answers. This member is the one an import can legitimately forget,
    /// because its route names no module at all, so the omission has to be OBSERVABLE before anything can
    /// refuse it. Nullability is what makes it observable; the validator's <c>NotNull</c> rule is what
    /// refuses it with a field-keyed answer. The difference between the two spellings is therefore
    /// deliberate and load-bearing rather than inconsistent, and adding a validator has not changed it:
    /// a non-nullable member would deserialise an omitted <c>moduleId</c> to zero, which is a REAL module,
    /// and no rule of any kind could then distinguish the two.
    /// </para>
    /// <para>
    /// HOW IT MUST BE CHECKED. By EXISTENCE, through the module repository, and by tenant. NEVER by sign:
    /// not <c>&gt; 0</c>, not <c>&gt;= 0</c>, and not a comparison against <c>-1</c>. Zero is a real
    /// module, and <c>-1</c> is simultaneously the legacy integer sentinel, the legacy field's
    /// "unresolved" initial value, and a real portal identifier elsewhere in this same schema
    /// (<c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c>). A caller that sends <c>-1</c> has made a
    /// lookup that will fail, not an omission, and the two must produce different answers. There is no
    /// length or range bound to preserve here: this request writes no row to the module or placement
    /// tables, so no column exists from which one could be derived.
    /// </para>
    /// </remarks>
    // MIGRATION: NEITHER LEGACY SCREEN EVER POSTED A MODULE IDENTIFIER, so the body placement is a routing
    //   consequence rather than a legacy inheritance: both screens declared
    //   `Private Shadows ModuleId As Integer = -1` and resolved the value from ambient page context, the
    //   import screen parsing it from its own query string on load. Two consequences follow and both are
    //   honoured elsewhere. Because the target arrives in the BODY, no route-reading authorisation policy
    //   can evaluate a permission against it before the action runs, so the edit grant is confirmed by the
    //   service after binding. And because the body is the only source, the portal argument taken from the
    //   path is what stops a body-supplied identifier from reaching another tenant's module. That legacy
    //   `-1` initial value literally meant "not resolved yet" - the legacy sentinel module returns -1 for
    //   both its short and integer forms and reports -1 as null - and reproducing the convention on the
    //   wire would have been wrong twice: -1 is a real identifier in a sibling table, and the identity seed
    //   of THIS table is zero, so neither value is available as an "absent" marker.
    public int? ModuleId { get; set; }

    /// <summary>
    /// The exported document to load, as text. Effectively mandatory: a blank value is refused, and the
    /// module's own portability behaviour - not this contract - interprets what it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE WHOLE DOCUMENT TRAVELS HERE, ENVELOPE AND ALL. What the export endpoint produces is an XML
    /// declaration followed by an element named <c>content</c> carrying a <c>type</c> attribute and a
    /// <c>version</c> attribute, wrapped around whatever the module itself emitted. That entire document
    /// is what belongs in this member. The service parses it, requires the <c>content</c> root, reads the
    /// <c>version</c> attribute from it, and passes only the element's inner content onward. A caller must
    /// not be expected to strip the envelope first, and one that does will have removed the version the
    /// server would otherwise have read.
    /// </para>
    /// <para>
    /// A STRING, AND NOTHING ELSE. The legacy portability contract's import member takes a <c>String</c>
    /// parameter, itself named <c>Content</c>, exactly as its export member returns one, so a string is
    /// what the two contracts exchange. The legacy screen also had no upload control of any kind, because
    /// it read a document that was already on the server's own disk. A <c>Stream</c> or <c>byte[]</c> would
    /// compile here - both are base-library types - but neither is what the portability contract speaks,
    /// and a byte payload would additionally push the document's encoding onto the caller. An
    /// <c>IFormFile</c> is the one shape that could NOT appear on this type: it belongs to the web
    /// framework, which this project does not reference.
    /// </para>
    /// <para>
    /// XML BY VALUE, JSON BY TRANSPORT. The text is an XML document; the request carrying it is JSON. This
    /// contract treats the value as opaque text and this type is never itself XML-serialised, which is
    /// worth stating plainly on the one member where the two formats meet.
    /// </para>
    /// <para>
    /// NULLABLE BECAUSE THE LEGACY ALTERNATIVE WAS TO NAME A SERVER-SIDE DOCUMENT INSTEAD OF SENDING ONE.
    /// At most one of this member and the <c>Folder</c>-plus-<c>FileName</c> pair is meaningful in any
    /// single request, and the service decides how they relate - a rule held in a contract would be
    /// business logic in the wrong layer. No initial value is declared, because one would quietly invent a
    /// document the caller never sent and turn a missing payload into an empty one. The legacy string
    /// sentinel was the empty string rather than null, so its own tests compared against the empty string;
    /// this contract admits null, and the service treats null, empty and whitespace alike as not supplied,
    /// answering <c>module.content_invalid</c>.
    /// </para>
    /// <para>
    /// NO SIZE BOUND IS ASSERTED ON THIS PROPERTY. The legacy contract took an unbounded string and the
    /// legacy screen had no text input at all to bound - its document picker was a list, not a field - so
    /// there is no parity limit to preserve, and a length rule here would refuse the document only after the
    /// whole body had already been read and bound, which is after the allocation a bound exists to prevent.
    /// Enforcement therefore stays with the API host and the reverse proxy in front of it, which bound the
    /// BODY while it is still being read off the socket, and with the service, which bounds the document's
    /// CHARACTERS before it parses. What this type does carry is the published number those layers are sized
    /// from - <see cref="ContentCharacterMaximum"/> - so that a client can satisfy the contract instead of
    /// discovering it from a refusal.
    /// </para>
    /// </remarks>
    // MIGRATION: THE ENVELOPE IS UNWRAPPED SERVER-SIDE, WHICH IS WHY NEITHER A VERSION NOR A CONTENT-TYPE
    //   MEMBER EXISTS. The legacy sequence was: read the document's text from disk, load it into an XML
    //   parser, read `GetAttribute("type")`, read `GetAttribute("version")`, then call the portability
    //   contract with `DocumentElement.InnerXml` - the INNER content of the root element, not the whole
    //   document. The target reproduces the division of labour and most of the sequence: the wire carries
    //   the complete `<content type="..." version="...">...</content>` document and the service, not the
    //   caller, unwraps it and reads the version. It does NOT read the type attribute, which is the parity
    //   gap recorded at the head of this file. Accepting a Version member beside the payload would create a
    //   contradiction the legacy could not have had, because the legacy read the version FROM the payload;
    //   accepting a ContentType, Type, MimeType or Extension member would be worse, because the type
    //   attribute is the evidence that decides whether a document belongs to a module, and letting a caller
    //   assert it would defeat the check that closing the gap depends on.
    //
    // MIGRATION: THIS VALUE MUST NEVER BE LOGGED, and that is a forward-looking constraint rather than a
    //   translation. The non-functional requirements call for structured logging that carries no sensitive
    //   data, and this payload is opaque module content of unknown sensitivity and unbounded size: a module
    //   is free to export anything it stores, so the boundary cannot know whether a given document holds
    //   personal data. Request logging and the logging pipeline behind it must therefore never emit it, and
    //   no string conversion declared on this type may include it - which is one more reason there is no
    //   such conversion at all. The legacy screen never logged the content either, so nothing is lost by
    //   the constraint and nothing would be gained by relaxing it.
    public string? Content { get; set; }

    /// <summary>
    /// The portal-relative folder the document came from, or <see langword="null"/> when the caller has
    /// none. Optional, accepted for parity, and resolved against nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ACCEPTED AND DELIBERATELY UNUSED. In the legacy this was a real location: it selected which
    /// directory to enumerate candidate documents from, and it was then joined to the chosen name to read
    /// the document off disk. Here nothing is read from a file system, so nothing consumes it. It survives
    /// so an operator workflow keeps the vocabulary it had and a client can still echo the legacy naming
    /// if it wishes. Its help text read "Select the import folder".
    /// </para>
    /// <para>
    /// THE EMPTY STRING IS NOT A SYNONYM FOR ABSENT IN THIS MEMBER'S LEGACY VOCABULARY - IT MEANT THE
    /// PORTAL ROOT. The legacy list inserted a non-selectable prompt at position zero whose value was a
    /// hyphen, then added one entry per folder the operator could read and write, each valued by its own
    /// path. The portal root was one of those real entries and ITS VALUE WAS THE EMPTY STRING, displayed
    /// under the label "Root" - which is exactly why the legacy guard tested the selected INDEX rather than
    /// the selected value. So no initial value is declared and null is left to mean "the caller offered
    /// none", while the empty string keeps its legacy meaning. Neither is interpreted as a location.
    /// </para>
    /// </remarks>
    // MIGRATION: THE FILE SYSTEM LEFT SCOPE, AND FOUR CONSEQUENCES FOLLOW. The legacy enumerated
    //   candidates with a global helper that listed the portal's files of a given extension within a given
    //   folder, and then opened the chosen document from the portal's home-directory map path. The file
    //   subsystem behind both is an excluded component of this migration, and a containerised deployment
    //   has no such directory to point at in any case. So: the document arrives in band through Content,
    //   which is why that member exists at all; this member and FileName become parity metadata rather than
    //   a location, and no HomeDirectory, MapPath, FilePath, FullPath or PortalId member is accepted to
    //   rebuild one, because on an import endpoint a caller-supplied path is both a cross-tenant vector and
    //   an arbitrary-file-read vector; the folder-to-document cascade, which the legacy implemented by
    //   posting the whole page back whenever the folder selection changed, becomes a dependent select in
    //   the client with no API counterpart; and the portal itself is resolved from the route and the
    //   request's tenant context rather than from anything in this body.
    public string? Folder { get; set; }

    /// <summary>
    /// The name of the document the content came from, or <see langword="null"/> when the caller has none.
    /// Optional parity metadata that no decision depends on, and never interpreted as a path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ITS VALUE USED TO BE LOAD-BEARING AND NO LONGER IS, which is the one thing a reader most needs to
    /// know about this member. The legacy decided whether a document belonged to a module by inspecting
    /// its NAME: it accepted the document only if the name contained the module's own name or its legacy
    /// friendly name, and otherwise refused with "The import file specified is not the correct type for
    /// this module". Nothing branches on this member now, and the head of this file records that the
    /// refusal itself is not currently reproduced by any other means either - the payload's <c>type</c>
    /// attribute, which is the stronger evidence and the intended replacement, is not read on import.
    /// Its help text read "Select the import file", and it was the subject of the screen's only guard.
    /// </para>
    /// <para>
    /// NO LENGTH BOUND EXISTS TO PRESERVE. The legacy control here was a list, not a text field, so it
    /// carried no maximum length - unlike the export screen's name box, which was bounded at 200
    /// characters and is documented as such on its own contract. Nor does the terminal schema supply one:
    /// this request writes no row to the module or placement tables. As with the other two text members, no
    /// initial value is declared, and the null-versus-empty translation belongs to the service.
    /// </para>
    /// </remarks>
    // MIGRATION: THE NAME IS NOT A CORRECTNESS BOUNDARY, AND THE OUTCOME IT GUARDED IS NOT PRESENTLY
    //   GUARDED AT ALL. The legacy performed the type check twice over, and both halves were weak: the
    //   screen's own helper accepted the document if its name merely CONTAINED the sanitised module name or
    //   the sanitised legacy friendly name, so a mislabelled document passed, and the module controller
    //   then compared the payload's type attribute against the same two values. The target keeps neither
    //   today; the second, stronger check is the one to restore, in the service and against the payload
    //   attribute. Whichever way it is restored, the module name and its legacy friendly name must NOT be
    //   accepted here even though the legacy check consumed them: they are resolved server-side from the
    //   module's package record, where the module name additionally carries a unique constraint that is
    //   what made it usable as a discriminator at all, and accepting them from a caller would let the
    //   caller choose the values its own payload is checked against. They remain readable on the
    //   module-definition contract, which is the correct place for them.
    //
    // MIGRATION: A LEGACY DEFECT IN HOW THIS FIELD WAS POPULATED, RECORDED AND NOT REPRODUCED. When the
    //   folder selection changed, the legacy filled the document list by testing each candidate name
    //   against TWO prefixes independently - the current one, built from `content.` plus the sanitised
    //   module name, and a legacy one built the same way from the module's friendly name - and each test
    //   called Add on its own, so a document whose name satisfied BOTH prefixes appeared as two identical
    //   choices with different displayed text. The target does not enumerate documents at all, so there is
    //   no list to duplicate an entry in. Noted rather than silently absorbed, because the duplication is
    //   visible in the legacy behaviour and its disappearance should read as a consequence of the file
    //   system leaving scope.
    public string? FileName { get; set; }
}
