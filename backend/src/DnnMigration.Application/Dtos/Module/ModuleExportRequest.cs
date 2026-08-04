namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The state submitted to <c>POST /api/v1/portals/{portalId}/modules/{moduleId}/export</c> to obtain a
/// module's content as a portable document. A boundary contract and nothing more: no navigation
/// property, no tracked state, no behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// THE MODULE IS ADDRESSED BY THE ROUTE, NOT BY THIS BODY, AND THE ASYMMETRY WITH THE IMPORT REQUEST
/// IS DELIBERATE. Export is reached at <c>.../modules/{moduleId}/export</c>, so the identity travels in
/// the path and this contract carries no module identifier at all; import is reached at
/// <c>.../modules/import</c> with no identifier in its route, so <c>ModuleImportRequest</c> must - and
/// does - carry one in its body. Anyone tempted to harmonise the two shapes should change neither. The
/// planned endpoint surface names this route <c>POST /api/v1/modules/{id}/export</c>; it is realised
/// portal-nested here because every write in this API is scoped to a tenant by its path, and that
/// nesting is what lets the service refuse a module belonging to a different portal.
/// </para>
/// <para>
/// TWO MEMBERS IS A MEASURED ANSWER, NOT AN ABBREVIATION. The legacy export screen was eighteen lines
/// of markup and offered exactly two inputs - a folder picker and a file-name text box - beside an
/// export and a cancel button. It carried NO DECLARATIVE VALIDATOR OF ANY KIND: no required-field, no
/// comparison, no regular-expression and no validation summary. Its whole rule was one imperative
/// guard in the click handler, <c>If cboFolders.SelectedIndex &lt;&gt; 0 And txtFile.Text &lt;&gt; ""</c>,
/// failing with the single message "You must specify a folder and file for export". Because request
/// shapes here derive from what the legacy screen actually posted rather than from the entity behind
/// it, two inputs yield two members. A third would be an invention.
/// </para>
/// <para>
/// THE PRODUCED CONTENT IS XML; THIS REQUEST IS NOT, AND THIS TYPE IS NEVER XML-SERIALISED. The
/// endpoint answers with a document rooted at <c>&lt;content&gt;</c> carrying <c>type</c> and
/// <c>version</c> attributes, served as <c>application/xml</c> - the legacy wrapper, reproduced. The
/// request body travelling the other way is JSON, and the one central serialisation policy at the API
/// edge decides the wire casing of both members below, so this type carries no serialisation attribute
/// of any kind. That is worth stating plainly precisely because the payload is XML: the format of the
/// answer says nothing about the format of the question.
/// </para>
/// <para>
/// THE INNER PAYLOAD IS NOT REDEFINED BY THIS MIGRATION. Each module produces its own content through
/// its portability contract, whose legacy signature returned a plain string; only the transport
/// changes, from a server-side file write to an HTTP response. Whatever a given module chose to place
/// inside that string - including its own settings - it still chooses.
/// </para>
/// <para>
/// NEITHER MEMBER IS A PATH, AND NO PATH, PORTAL OR ACTING-USER MEMBER EXISTS TO BE ONE. Excluded, each
/// for a measured reason: the module identifier (route); any content, payload, upload, stream or raw
/// byte-array member, because export PRODUCES content and a request that also supplied it would be a
/// category error - and because <c>IFormFile</c> could not compile here in any case, this project
/// referencing no web framework; the content type, extension and MIME type, which the legacy layer
/// stamped server-side after the fact and which are fixed by the XML format; every path, home-directory
/// and portal member, which would open both a cross-tenant and a path-traversal vector; the version and
/// module name, which the server resolves from the addressed module and writes into the document
/// itself; the module title, which only seeded a default; the portability flag, capability bit field and
/// business-controller class name, which are server-side capability facts; any overwrite, quota or
/// size-limit flag; every module-instance property, settings bag and permission collection, which
/// belong to the sibling contracts in this folder; all paging metadata; and every audit or acting-user
/// field. No property here is computed, and none carries a validation attribute.
/// </para>
/// </remarks>
// MIGRATION: 5.6 - THE MODULE IDENTIFIER IS ABSENT BY DESIGN, AND THE LEGACY SCREENS CORROBORATE IT
//   RATHER THAN MERELY PERMITTING IT. Both the export and the import code-behind declared
//   `Private Shadows ModuleId As Integer = -1` and resolved the value from the ambient page context -
//   the export page from its own query string - so NEITHER SCREEN EVER POSTED A MODULE IDENTIFIER. The
//   difference between this contract and the sibling import contract is therefore purely a routing
//   choice in the target, not a change in what a caller supplies. Accepting an identifier here would
//   additionally allow a body that disagrees with the path. One warning for whoever validates the route
//   value: `Modules.ModuleID` is `IDENTITY (0, 1)`, so ZERO IS A REAL MODULE, while -1 is the legacy
//   integer sentinel and simultaneously a real portal identifier elsewhere in this schema. The
//   identity must be checked BY EXISTENCE - and by tenant - never by sign, which is what the service
//   does when it loads the module and compares its portal against the route.
//
// MIGRATION: 5.3 - THE ANSWER IS XML AND THE QUESTION IS JSON, AND THIS TYPE IS NEVER XML-SERIALISED.
//   The legacy handler wrapped whatever the module produced in an XML declaration followed by a
//   `<content>` element carrying `type` and `version` attributes, taking the type from the sanitised
//   module name and the version from the module's installed version - both server-resolved, which is
//   why neither is a member here. The target reproduces that wrapper and serves it as
//   `application/xml`, while THIS request body is JSON like every other in this API. The distinction
//   earns an explicit note precisely because the payload is XML: a reader who assumed the request must
//   be XML too would reach for serialisation attributes, and this type carries none. The inner payload
//   is not redefined either - only its transport changes, from a server-side file write to an HTTP
//   response - so each module's own content format survives untouched.
//
// MIGRATION: 5.5 - VALIDATION WAS IMPERATIVE AND SINGULAR, SO THERE IS DELIBERATELY NO VALIDATOR TYPE
//   FOR THIS REQUEST. The legacy markup declared no validator at all; the one rule lived in the click
//   handler and failed with "You must specify a folder and file for export". In the target that rule is
//   a failure reason raised by the module service, which refuses a blank file name, and NOT a class
//   under the declarative validation folder - the planned validator set contains no entry for this
//   request, and none has been created, referenced or implied. Note also that the folder half of the
//   legacy guard has no direct equivalent left: it tested a LIST INDEX, and the target no longer needs
//   a folder at all, per 5.4.
//
// MIGRATION: 5.7 - THREE PRECONDITIONS GOVERN AN EXPORT AND NOT ONE OF THEM IS A REQUEST FIELD. First,
//   the module had to be portable: the legacy code tested
//   `objModule.BusinessControllerClass <> "" And objModule.IsPortable` and otherwise reported "The
//   module specified does not support the exporting of content". Second, it had to have content, or the
//   answer was "The module specified does not have any content". Third, the operator had to be
//   privileged - the screen's own help text reads "Administrators can export content for the specified
//   module." The first two are resolved server-side from the module's own definition and the third by
//   policy-based authorisation at the API edge. A DTO member for any of them would let a caller assert
//   its own capability or its own authority, so all three are absent. The target does diverge on the
//   second: an empty payload still yields a document, because "asked and given nothing" is an answer a
//   caller is entitled to see, and that divergence is recorded on the service method that makes it.
//
// MIGRATION: 5.8 - A STORED TYPE NAME IS NO LONGER ACTIVATED, WHICH IS WHY NO MEMBER NAMES ONE. The
//   legacy export passed the module's stored business-controller class to a reflection-based activator
//   and cast the result to the portability contract - one of five such sites across the module code. The
//   target resolves the implementation from a closed, dependency-injected factory set instead, so a
//   module no registration covers cannot be exported and says so, rather than silently producing
//   nothing. THE CONSEQUENCE FOR THIS CONTRACT IS THE POINT: accepting a class name on a request would
//   be arbitrary remote activation, so the name is never accepted and is resolved from the addressed
//   module alone. A related sentinel deserves recording while it is in view - the capability bit field
//   read as -1 did not mean "no capabilities" but "installation not yet complete", which the legacy
//   code treated by deferring the work through its event queue.
//
// MIGRATION: 5.9 - TWO LEGACY DEFECTS ARE RECORDED HERE AND NOT REPRODUCED. The import counterpart
//   unwrapped a CDATA section with hard-coded offsets, `Substring(9, strcontent.Length - 12)`, without
//   ever checking that the content was CDATA-wrapped. And the export and import handlers between them
//   contain THREE BARE CATCH BLOCKS - one in export, two in import, alongside six typed handlers -
//   which collapsed every distinct failure into the one generic message "An error occurred during the
//   export": a missing controller, a non-conforming one, a full disk and a failed write were
//   indistinguishable to the operator. The target surfaces a typed failure reason translated into a
//   problem document at the edge, so the caller learns which precondition failed. That is a deliberate
//   improvement in diagnosability rather than a change of outcome, and the opacity is not carried over.
//
// MIGRATION: 5.10 - NO ACTING-USER IDENTIFIER CROSSES THIS BOUNDARY. The legacy content paths took the
//   operator from ambient state - the portal's administrator identifier on the export side, the current
//   user's identifier on the import side - never from the submitted form. The target resolves the caller
//   from the authenticated principal, so a user identifier on the wire would be both redundant and
//   spoofable. There is nothing to stamp either: measured across all eighty-eight upgrade scripts, the
//   module and placement tables carry NO audit columns whatsoever - zero occurrences of any created-by,
//   created-on, last-modified-by or last-modified-on column - so no audit member is omitted from this
//   contract that the schema could have stored.
public sealed class ModuleExportRequest
{
    /// <summary>
    /// The base name to label the exported document with. Mandatory and non-blank; at most 200
    /// characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE MIDDLE OF A COMPOSED NAME, NOT THE FINAL FILE NAME. The legacy screen built the real
    /// name from four parts and the operator supplied only one of them, so a caller that treats this
    /// value as the whole name will mislabel what it receives. The 200-character bound is the maximum
    /// length of the legacy text box and NOT a column width: this request writes nothing to the module
    /// or placement tables, so no column exists from which to derive one. It is preserved as a
    /// documented parity bound rather than enforced by an attribute here.
    /// </para>
    /// <para>
    /// The legacy layer represented "not supplied" as THE EMPTY STRING rather than as an absent value,
    /// because its string sentinel was literally the empty string - which is why its own guard tested
    /// against the empty string and never against null. This contract therefore admits null, and the
    /// service treats null, the empty string and whitespace alike as not supplied, refusing the request
    /// with a reason. That single translation is owned by the service and the mappers, not by this
    /// contract, whose job is to state that both spellings mean the same thing. No initialiser is
    /// declared for the same reason: an initialiser here would quietly invent a value the caller never
    /// sent, and turn a missing name into an empty one.
    /// </para>
    /// </remarks>
    // MIGRATION: 5.1 - THE SERVER COMPOSES AND SANITISES THE NAME; THE CALLER SUPPLIES ONE SEGMENT OF
    //   FOUR. The legacy composition was
    //   `"content." & CleanName(objModule.ModuleName) & "." & CleanName(txtFile.Text) & ".xml"`, so the
    //   `content.` prefix, the module-name segment and the `.xml` suffix were all added server-side and
    //   only the middle segment came from the form. `CleanName` then STRIPPED a fixed punctuation set
    //   from both the module name and the operator's text - the characters of
    //   `. ~`!@#$%^&*()-_+={[}]|\:;<,>?/` together with the double quote and the apostrophe - by
    //   removing each one outright rather than substituting for it. Both the composition and the
    //   sanitisation are the SERVER'S obligation: a caller is not expected to pre-sanitise, and an
    //   unsanitised value must not be allowed to reach a name unaltered. That sanitisation was also the
    //   only thing standing between free-typed text and a file name, which is a further reason this
    //   contract accepts no path member at all.
    //
    // MIGRATION: 5.2 - THE LEGACY FIRST LOAD PRE-FILLED THIS BOX AND THE API DELIBERATELY SUPPLIES NO
    //   DEFAULT. The screen seeded the text box with the module's own sanitised title on first render
    //   only, never on a postback. In the target that affordance is client-side convenience: a caller
    //   wanting the same starting value reads the module title from the module detail contract and
    //   offers it as the initial value of its own field. It is written down so the export screen can
    //   reproduce the affordance deliberately rather than lose it by omission, since functional parity
    //   of the workflow is the obligation, not parity of the mechanism.
    public string? FileName { get; set; }

    /// <summary>
    /// The portal-relative folder the caller intends the document for, or <see langword="null"/> when it
    /// has none. Optional, accepted for parity, and not used to store anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ACCEPTED AND DELIBERATELY UNUSED. The legacy value was a real path segment; here nothing is
    /// written to a file system, so nothing consumes it. It survives so that an operator workflow keeps
    /// the vocabulary it had and a client can still reproduce the legacy naming if it wishes.
    /// </para>
    /// <para>
    /// The legacy folder list is worth recording precisely, because its shape explains the guard that
    /// governed it. Item zero was an inserted, non-selectable prompt whose VALUE WAS A HYPHEN; the real
    /// entries followed, each valued by its folder path and filtered to the folders the operator could
    /// read and write. The portal root was one of those real entries, and ITS VALUE WAS THE EMPTY
    /// STRING, displayed under the label "Root". That is exactly why the legacy guard tested the
    /// selected INDEX rather than the selected value: a test against the empty string would have
    /// rejected the portal root, which was a legitimate choice. The consequence for this contract is
    /// that the empty string is not a synonym for "absent" in this member's legacy vocabulary - it
    /// meant the root - so no initialiser is declared and null is left to mean "the caller offered
    /// none". The legacy help text reads "Select the export folder".
    /// </para>
    /// </remarks>
    // MIGRATION: 5.4 - THE FILE SYSTEM LEFT SCOPE, WHICH IS THIS ENDPOINT'S ONE SUBSTANTIVE FUNCTIONAL
    //   REDUCTION. The legacy export wrote its document to the portal home directory beneath the web
    //   root, joining this folder to the composed name, then registered the result as a portal file with
    //   extension `xml` and content type `application/octet-stream`, having first checked the portal had
    //   space for it. The file subsystem that did all of that is excluded from this migration, and the
    //   destination itself does not exist in a containerised topology. The target returns the document to
    //   the caller instead. FOUR CONSEQUENCES FOLLOW AND ARE STATED SO NONE IS DISCOVERED LATE: this
    //   member becomes parity metadata rather than a destination; there is no quota check, so the
    //   disk-space failure mode disappears entirely; there is no overwrite question, and no overwrite
    //   flag is invented to answer one - the legacy behaviour was an unconditional overwrite, since it
    //   updated the file registration when a file of that name already existed; and the portal file
    //   registry gains no row. Corroboration that the vanished disk-space message belonged to the
    //   excluded subsystem rather than to this screen: it was the ONLY message on the export path
    //   resolved from global resources, absent from this screen's own resource file, while every other
    //   message on the path was resolved from it.
    public string? Folder { get; set; }
}
