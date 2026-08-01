// -----------------------------------------------------------------------------
// CurrentUserDto - the "who am I" projection returned by GET /api/v1/auth/me.
//
// PROVENANCE. Every member below is traced to a measured legacy source rather
// than to the shape of a domain entity, so that no entity crosses the wire:
//
//   Library/Components/Users/UserInfo.vb       identity, name, elevation, roles
//   Library/Components/Users/Membership/       the membership composite that
//     UserMembership.vb                        UserInfo.vb L195 returned
//   Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb
//                                              the sign-in path being replaced
//   Website/admin/Security/                    the credential-recovery screen
//                                              whose flow is abolished entirely
//   Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider
//                                              terminal schema, seeded data and
//                                              identity column declarations
//   Website/release.config                     legacy provider registration
//   Library/Components/Shared/Null.vb          the sentinel table
//
// WHY THIS TYPE IS SEPARATE FROM THE USER DTO SET. This response is fetched once
// per application-shell render, which makes it one of the most frequently
// serialised objects in the system. It answers exactly two questions - who is
// signed in, and what may the interface offer them - and carries nothing else.
// The richer per-user shapes live in the user DTO set and are fetched
// deliberately rather than on every render. Nothing is re-declared from that
// set: where a member name is shared, the name and the CLR type are identical,
// so the two contracts read as a single vocabulary and one pair of TypeScript
// models can mirror both.
//
// WHAT THIS TYPE DELIBERATELY IS NOT. It is inert. There is no computed getter,
// no lazy load, no data access, no validation rule, no asynchronous member and,
// above all, no authorisation decision. It reports; it never decides.
//
// NO CREDENTIAL MATERIAL APPEARS ON THIS TYPE, and none may ever be added. The
// prohibition is absolute and is documented in full at the foot of the class.
// -----------------------------------------------------------------------------

namespace DnnMigration.Application.Dtos.Auth;

/// <summary>
/// The signed-in caller's own identity and interface-gating data, returned by
/// <c>GET /api/v1/auth/me</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a claims projection, not a user record.</b> It is deliberately
/// not a <c>User</c> entity in disguise and not a second copy of the user
/// detail contract. The Angular client calls this endpoint immediately after
/// bootstrap to render the application shell - the header, the sidebar and the
/// footer - and to decide which navigation and actions to offer. Every member
/// below earns its place against one of those two needs; anything serving
/// neither belongs to the user DTO set instead.
/// </para>
/// <para>
/// <b>No credential material and no session artefact appear on this type, and
/// none may ever be added to it.</b> The legacy store was reversible by design:
/// the membership provider was registered to encrypt rather than to hash,
/// recovery was switched on, and the key that reversed every stored credential
/// was itself committed to source control (release.config L89-L93, L239, L245).
/// The recovery path measured in the credential-mailing admin control under
/// <c>Website/admin/Security/</c> at L198-L211 read the user, recovered the
/// plaintext and mailed it to the address on file. That flow has no counterpart
/// in the target, which keeps a one-way adaptive hash instead and re-hashes on
/// the first successful sign-in, with an administrative reset as the fallback.
/// Because this response is emitted on every shell render it is also the payload
/// most likely to be captured by request and response logging, so keeping every
/// credential-shaped member off it is what makes that logging safe by
/// construction.
/// </para>
/// <para>
/// <b>The portal identifier does not come from the user record.</b> The
/// <c>Users</c> table has no portal column - the original
/// <c>CREATE TABLE</c> at 01.00.00.SqlDataProvider L97-L111 declares none, and
/// no script in the 88-script upgrade chain adds one - so per-portal facts live
/// on <c>UserPortals(UserId, PortalId, Authorized)</c> at L153-L156 instead.
/// The legacy <c>UserInfo.PortalID</c> (UserInfo.vb L219) was an in-memory field
/// carried alongside the row, never persisted state. Both <see cref="PortalId"/>
/// and <see cref="PortalName"/> are therefore populated by the service from the
/// portal context that the request pipeline resolves per request, and never from
/// anything read off the user row.
/// </para>
/// <para>
/// <b>The role and permission data here is advisory, and is never the sole
/// enforcement mechanism.</b> <see cref="Roles"/> and <see cref="Permissions"/>
/// exist so the interface can hide affordances the caller cannot exercise; the
/// client-side structural directive that consumes them mirrors permission keys
/// that are evaluated on the server, and mirroring is the whole of its job.
/// Authoritative enforcement is server-side policy-based authorisation -
/// <c>PermissionRequirement</c>, <c>PermissionAuthorizationHandler</c> and
/// <c>PolicyNames</c> under <c>Api/Authorization/</c> - backed by the permission
/// evaluator under <c>Infrastructure/Security/</c>. A caller who tampers with
/// this response changes what a menu looks like and nothing whatsoever about
/// what the API will permit.
/// </para>
/// <para>
/// <b>Sentinel boundary.</b> The legacy data layer did not use SQL <c>NULL</c>
/// end to end. Every <c>DBNull</c> read was translated into a per-type sentinel,
/// so "absent" was encoded as minus one for integers, the empty string for text
/// and <see langword="false"/> for booleans (Null.vb L36-L85), and the legacy
/// null test reported <see langword="true"/> for all three
/// (Null.vb L208-L237). This contract is the boundary at which those encodings
/// are made explicit, and the technique it adopts is deliberately narrow.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Every string member is non-nullable and defaults to the empty
///     string.</b> The legacy encoding of absent text was the empty string
///     rather than <see langword="null"/> (Null.vb L71-L75), and for
///     <see cref="DisplayName"/> that encoding is baked into the schema itself,
///     which declares the column <c>NOT NULL</c> with a <c>DEFAULT ''</c>
///     (03.02.03.SqlDataProvider L630). Emitting <see langword="null"/> for any
///     of them would invent an absence the legacy contract never had.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b><see cref="IsSuperUser"/> is a non-nullable <c>bool</c>, never a
///     nullable one.</b> The legacy null test treats <see langword="false"/> as
///     absent (Null.vb L227-L228), so in the source data an unset flag and a
///     negative answer are the same value. A nullable boolean would advertise a
///     distinction the data cannot make.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>No date member is carried at all.</b> Legacy date reads arrive as the
///     minimum date rather than as <see langword="null"/>, so every such member
///     forces an explicit sentinel decision. The shell needs none of them, so
///     the decision is avoided rather than made badly - the user detail
///     contract owns the fuller shape.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Serialisation must be left at its defaults.</b> Applying a
///     "when writing null" or a "when writing default" ignore condition to this
///     type would erase a legitimate <see langword="false"/> and turn a
///     legitimate empty string into an absent field, silently changing the
///     contract that the Angular client and the integration suite both bind to.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Shape.</b> Plain settable auto-properties on a sealed class with the
/// implicit public parameterless constructor. The integration suite
/// deserialises this body directly, and that combination is the shape the
/// serialiser handles without a converter, a constructor attribute or a
/// name-mapping attribute - none of which this type declares.
/// </para>
/// </remarks>
public sealed class CurrentUserDto
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------

    /// <summary>
    /// The surrogate key of the signed-in user, from <c>Users.UserID</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared <c>[int] IDENTITY (1, 1) NOT NULL</c>
    /// (01.00.00.SqlDataProvider L98), so a persisted user always has a
    /// positive key and this member is never absent on a response that was
    /// returned at all - an unauthenticated caller receives a 401 rather than
    /// an empty projection.
    /// </para>
    /// <para>
    /// The legacy name is retained rather than generalised to <c>Id</c>. That
    /// is a solution-wide discipline: the domain base type deliberately exposes
    /// no member called <c>Id</c>, so every identifier on every contract lines
    /// up against the schema and the legacy source without a translation table.
    /// The user detail contract names this member identically and types it
    /// identically.
    /// </para>
    /// </remarks>
    public int UserId { get; set; }

    // -------------------------------------------------------------------------
    // Tenancy - supplied by the request pipeline, never by the user row
    // -------------------------------------------------------------------------

    /// <summary>
    /// The portal (tenant) the caller is signed in to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this value does not come from the user record, because there
    /// is no column to read it from. The original
    /// <c>CREATE TABLE [dbo].[Users]</c> at 01.00.00.SqlDataProvider L97-L111
    /// declares no portal column and no script in the 88-script upgrade chain
    /// adds one; per-portal membership is a table of its own,
    /// <c>UserPortals(UserId, PortalId, Authorized)</c> at L153-L156, which is
    /// exactly why the target models that association as a separate entity. The
    /// legacy <c>UserInfo.PortalID</c> (UserInfo.vb L219) was an in-memory
    /// field carried alongside the row, not persisted state. The service
    /// populates this member from the portal context that the request pipeline
    /// resolves per request from the incoming alias, and this contract simply
    /// reports what it was given.
    /// </para>
    /// <para>
    /// MIGRATION: <b>do not test this value for absence, and do not let a
    /// consumer do so either.</b> <c>Portals.PortalID</c> is declared
    /// <c>[int] IDENTITY (-1, 1) NOT NULL</c> (01.00.00.SqlDataProvider L77),
    /// so the first portal ever created is numbered minus one and the second is
    /// numbered zero - and the second is not hypothetical, because the baseline
    /// data seeds a role against portal zero at L7194. The legacy encoding for
    /// a missing integer is also minus one (Null.vb L41-L45) and the legacy
    /// null test reports <see langword="true"/> for it (Null.vb L208-L237), so
    /// a single value means both "the first portal" and "no portal at all". Any
    /// guard that rejects a non-positive identifier, or that treats minus one
    /// as a sentinel, therefore rejects two real tenants. The same trap applies
    /// to role, tab and module keys, which are all seeded from zero
    /// (01.00.00.SqlDataProvider L115, L140 and L221). Because the pipeline
    /// supplies this member it is always a real portal key, and it is
    /// non-nullable for that reason.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// The display name of the portal the caller is signed in to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Portals.PortalName</c> is declared <c>[nvarchar] (128) NOT NULL</c>
    /// (01.00.00.SqlDataProvider L79), so this member is non-nullable and
    /// defaults to the empty string.
    /// </para>
    /// <para>
    /// It is carried purely so the shell header can title itself without a
    /// second round trip; the legacy skin obtained the same string from the
    /// per-request portal object. Like <see cref="PortalId"/> it is supplied
    /// from the resolved portal context rather than read from the user row.
    /// </para>
    /// </remarks>
    public string PortalName { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Name and contact
    // -------------------------------------------------------------------------

    /// <summary>
    /// The caller's login name, from <c>Users.Username</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>nvarchar(100) NOT NULL</c> (01.00.06.SqlDataProvider L197) and the
    /// only uniquely constrained attribute on the user row, so it - and not
    /// <see cref="Email"/> - is the account key. Non-nullable, defaulting to
    /// the empty string, because the column is <c>NOT NULL</c> and the legacy
    /// encoding of absent text was itself the empty string.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy surface exposed this value twice. Setting
    /// <c>UserInfo.Username</c> (UserInfo.vb L301) also wrote the duplicate
    /// declared on the membership composite at UserMembership.vb L356, which
    /// sat inside that class's deprecated-members region. The two collapse to
    /// this single member, matching the decision already taken in the user DTO
    /// set: there is exactly one user name on this contract.
    /// </para>
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The caller's presentation name, from <c>Users.DisplayName</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared <c>nvarchar(128) NOT NULL</c> with a default of the empty
    /// string (03.02.03.SqlDataProvider L630), so this member is non-nullable
    /// and defaults to the empty string, matching the column default exactly.
    /// This is the one place where the legacy encoding of "absent" is not a
    /// convention of the data layer but a constraint of the schema, which is
    /// why it must not be surfaced as <see langword="null"/>: a caller with no
    /// display name has the empty string, and the shell renders
    /// <see cref="Username"/> in its place.
    /// </para>
    /// <para>
    /// This is the canonical name for display. The legacy combined-name
    /// property (UserInfo.vb L375) is deliberately absent - it was marked
    /// obsolete in favour of the display name, and it computed itself by
    /// concatenating the given and family names inside its own getter. A DTO
    /// does not compute, so the given and family names are not carried here at
    /// all; a client that needs them fetches the user detail contract.
    /// </para>
    /// </remarks>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The caller's email address, from <c>Users.Email</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>[nvarchar] (100) NOT NULL</c> (01.00.00.SqlDataProvider L107), so
    /// this member is non-nullable and defaults to the empty string.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy surface declared this value twice and the wire
    /// carries it once. The setter on <c>UserInfo.Email</c> (UserInfo.vb L123,
    /// setter body at L127) assigned its own backing field and then wrote
    /// straight through to the membership composite's separate declaration
    /// (UserMembership.vb L344); the comment in the source records that it did
    /// so only in case third-party code had bound to the duplicate. Two
    /// declarations, one logical value, one field on this contract. The
    /// collapse matches the decision already taken in the user DTO set and is
    /// not re-litigated here.
    /// </para>
    /// <para>
    /// <b>This is not an account key.</b> Unlike <see cref="Username"/> the
    /// column carries no unique constraint, and the legacy membership provider
    /// was registered with unique email addresses explicitly not required
    /// (release.config L244). Two accounts may legitimately share an address,
    /// so nothing downstream may treat this member as an identifier or as a
    /// sign-in credential.
    /// </para>
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Elevation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether the caller is a host-level super user, from
    /// <c>Users.IsSuperUser</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared <c>bit NOT NULL</c> with a default of zero
    /// (01.00.02.SqlDataProvider L243, carried forward at
    /// 01.00.06.SqlDataProvider L195) and mapped from
    /// <c>UserInfo.IsSuperUser</c> (UserInfo.vb L161). The column's default
    /// constraint name embeds the object-qualifier substitution variable, which
    /// is one more reason that naming belongs to the persistence configuration
    /// and never to a DTO.
    /// </para>
    /// <para>
    /// <b>Non-nullable by deliberate choice.</b> The legacy null test reports
    /// <see langword="true"/> for <see langword="false"/>
    /// (Null.vb L227-L228), so in the source data an unset flag and a negative
    /// answer are indistinguishable. A nullable boolean here would advertise a
    /// three-valued distinction the data cannot support:
    /// <see langword="false"/> means "not a super user", and there is no third
    /// state.
    /// </para>
    /// <para>
    /// Host-level administration is beyond the scope of this migration, so this
    /// member exists to let the shell suppress affordances rather than to
    /// unlock any. Like the role and permission members it is advisory, and the
    /// server decides.
    /// </para>
    /// </remarks>
    public bool IsSuperUser { get; set; }

    // -------------------------------------------------------------------------
    // Interface-gating data - advisory only; the server remains authoritative
    // -------------------------------------------------------------------------

    /// <summary>
    /// The names of the roles the caller holds in <see cref="PortalId"/>. Never
    /// <see langword="null"/>; an empty list means the caller holds none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy <c>UserInfo.Roles</c> (UserInfo.vb L261) was typed
    /// as a mutable array of role names. It is exposed here as a read-only list
    /// so that a caller cannot mutate a response object in place, and so that
    /// the contract states its intent rather than merely its storage. The
    /// legacy pre-generic collection wrappers are subsumed by the read-only
    /// generic interfaces throughout the target and produce no type of their
    /// own.
    /// </para>
    /// <para>
    /// MIGRATION: <b>the legacy getter performed database access.</b> Measured
    /// at UserInfo.vb L262-L269, it tested a private hydration flag and, when
    /// that flag was unset, constructed a role controller and queried the roles
    /// for the user and portal from inside the property accessor - so merely
    /// serialising the object issued a query. On an endpoint called once per
    /// shell render that is precisely the wrong place for I/O. Both the lazy
    /// getter and its flag are dropped: this is a plain auto-property that the
    /// service fills from data it has already loaded.
    /// </para>
    /// <para>
    /// MIGRATION: the default is an empty list rather than
    /// <see langword="null"/>, because the legacy family of encodings
    /// represented "none" as empty rather than as absent, and because a client
    /// should not have to null-check before iterating.
    /// </para>
    /// <para>
    /// Role <i>names</i> only. This contract deliberately does not describe a
    /// role - the role DTO set owns the richer shape, including the billing and
    /// trial attributes - and redeclaring any part of it here would fork that
    /// contract.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Roles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The permission keys the caller holds, used by the client to hide
    /// affordances it cannot exercise. Never <see langword="null"/>; an empty
    /// list means the caller holds none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Strings on the wire, deliberately. The values correspond to the
    /// <c>PermissionKey</c> vocabulary in the domain layer - view, edit, read
    /// and write - but that enumeration is not placed on the contract: the
    /// client-side structural directive that consumes these values takes a key
    /// as a string, the legacy permission keys were themselves strings
    /// evaluated by the three legacy permission controllers, and a list of
    /// strings needs no custom converter on either side of the wire.
    /// </para>
    /// <para>
    /// MIGRATION: <b>this list is advisory and is never the sole enforcement
    /// mechanism.</b> It mirrors keys that are evaluated on the server, and
    /// mirroring is the whole of its job. Authoritative enforcement is
    /// policy-based authorisation at the API surface -
    /// <c>PermissionRequirement</c>, <c>PermissionAuthorizationHandler</c> and
    /// <c>PolicyNames</c> under <c>Api/Authorization/</c> - backed by the
    /// permission evaluator under <c>Infrastructure/Security/</c>. Tampering
    /// with this response changes what a menu looks like and nothing at all
    /// about what the API will permit.
    /// </para>
    /// <para>
    /// No evaluation happens here. This type offers no method answering "may
    /// the caller do this": such a method would be an authorisation decision
    /// inside a data carrier, and the decision belongs to the evaluator named
    /// above.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();

    // -------------------------------------------------------------------------
    // DELIBERATE OMISSIONS - recorded so that a future reader does not
    // "restore" any of them believing it was overlooked.
    //
    // MIGRATION: NO CREDENTIAL MATERIAL, AND NONE MAY EVER BE ADDED. The
    // membership composite declared three members this contract deliberately
    // does not carry - the stored credential itself (UserMembership.vb L263),
    // the recovery answer (L283) and the recovery question (L303) - and the
    // legacy row carried a fourth, the plaintext credential column declared
    // nvarchar(20) NOT NULL at 01.00.00.SqlDataProvider L106. All four are
    // cited by line rather than by name, so that this file contains no
    // credential-shaped identifier at all; follow those lines in the source to
    // see the originals.
    //
    //   * The legacy store was reversible by design. The provider was
    //     registered to encrypt rather than to hash, recovery was switched on,
    //     and the key that reversed every stored credential was itself
    //     committed to source control (release.config L89-L93, L239, L245).
    //     The target keeps a one-way adaptive hash and re-hashes on the first
    //     successful sign-in, with an administrative reset as the fallback.
    //   * Recovery is not carried forward to any endpoint or screen. The legacy
    //     flow in the admin control under Website/admin/Security/ at L198-L211
    //     read the user, recovered the plaintext and mailed it to the address
    //     on file. It has no counterpart in the target.
    //   * Nothing that authenticates a caller appears here either - no
    //     credential digest, no session artefact, and no value a holder could
    //     replay. This body describes who the caller is; the means by which
    //     they proved it travels in a request header and is minted and
    //     validated in the infrastructure layer.
    //   * Keeping all of it off this type is also what keeps it clear of the
    //     structured request and response logs, and what keeps this file clean
    //     for credential scanning.
    //
    // MIGRATION: NO HYDRATION, CACHE-POLICY OR CHANGE-TRACKING STATE. Three
    // legacy members are dropped rather than translated, each because the
    // target platform supplies a first-class replacement for the workaround
    // that member existed to support:
    //
    //   * The progressive-hydration flag on the membership composite
    //     (UserMembership.vb L243) existed only so that every setter could mark
    //     its instance as populated. The object-relational materialiser makes
    //     the whole mechanism redundant, and the reflection-driven row hydrator
    //     it served produces no type in the target at all.
    //   * The read-only cache-level member at UserInfo.vb L484 is an HTTP
    //     output-cache policy artefact of the legacy web stack, implemented to
    //     satisfy a property-access interface that is beyond the scope of this
    //     migration. No part of that surface reaches the application layer.
    //   * A change-tracking "dirty" flag is likewise absent. Tracking is the
    //     persistence layer's concern and lives there; a wire contract has no
    //     tracking state.
    //
    // MIGRATION: NO AUDIT QUARTET. The four columns a modern DotNetNuke schema
    // carries for creation and modification attribution do not exist in this
    // one: all four names return zero occurrences across the 88 upgrade
    // scripts. Only a created-date on the user row and a last-updated-date on
    // the profile row exist, which is why the domain's auditable base type is
    // opt-in and carries exactly those two. Inventing the quartet here would
    // fabricate columns that no script ever creates - and in any case the shell
    // needs no date at all, so no date member is carried.
    //
    // NO OPERATION STATUS. The legacy sign-in, create and validate paths
    // reported their outcome through status enumerations returned by
    // out-parameter: the sign-in path at Login.ascx.vb L163-L164 declared one
    // and passed it by reference into the validation call. Three such
    // enumerations existed and no two agreed on how to spell "it worked" - one
    // treated zero as the good case, one treated one, and one used thirteen.
    // None of them reaches the wire. Expected failures are the service layer's
    // outcome type, which the API surface renders as an RFC 7807
    // problem-details payload; this contract describes a caller and reports no
    // outcome.
    //
    // NO PROFILE AND NO MEMBERSHIP BAG. The legacy profile object
    // (UserInfo.vb L236) is neither embedded nor referenced, and neither is the
    // membership composite (UserInfo.vb L195). Both have their own contracts in
    // the user DTO set, fetched deliberately. Keeping them apart is what keeps
    // this per-render response small.
    //
    // NO ENVELOPE OR TRANSPORT FIELDS. No HTTP status, no problem-details
    // shape, no per-field error map, no outcome flag and no request correlation
    // identifier: the common DTO set owns the response envelope, failures are a
    // problem-details payload produced at the API edge, and the correlation
    // identifier travels as a header rather than as a field.
    //
    // NO EDITOR OR SERIALISATION METADATA. The legacy properties cited above
    // were decorated with the sort-order, maximum-length, required, read-only
    // and browsable attributes of the legacy web-control property editor, and
    // the legacy portal types added XML-serialisation attributes on top. None
    // is reproduced: the widths quoted throughout are stated for the benefit of
    // the request validators and are deliberately not encoded here, because a
    // response contract validates nothing.
    // -------------------------------------------------------------------------
}
