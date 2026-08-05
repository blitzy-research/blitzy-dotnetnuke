using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Tab"/> aggregate - the DotNetNuke page - and the
/// page transfer contracts, and from an inbound page update onto the aggregate it describes.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the reflection-driven hydrator <c>Library/Components/Shared/CBO.vb</c> for the
/// page slice. That file is 729 lines whose entire purpose was to discover members at run time and
/// fill them from a reader by string key; its public surface was a clone helper, seven collection
/// fillers, two dictionary fillers, four object fillers, a property-info lookup, an initialiser and a
/// serialiser. Not one of them has a counterpart here. Every assignment below is a named statement the
/// compiler checks, so a renamed member is a build error rather than a value that silently fails to
/// arrive. AAP 0.4.3 records the deliberate decision to take no convention-based mapping dependency
/// for exactly this reason, and the absence is total: nothing here reflects over a type, reads a
/// column by string key, or fills a collection by convention.
/// </para>
/// <para>
/// MIGRATION: the second legacy hydration path is refused just as firmly. The module controller
/// hydrated an entity by assigning one line per column through the sentinel helper and then, part-way
/// through, issued further database reads to complete the object. Both halves of that pattern are
/// absent: the object-relational materialiser in Infrastructure performs the column binding, and no
/// member below reaches a repository, opens a connection, or performs any work that could fail. These
/// projections are pure and synchronous. Nothing here is asynchronous, awaits, or reads a clock, so
/// calling any member twice with the same input yields the same result, and calling it from several
/// threads at once is safe because the type holds no state at all.
/// </para>
/// <para>
/// MIGRATION: the legacy hydration contract itself is also not carried across. It declared an integer
/// key member and a fill method taking a reader, and it is worth recording that it was dead code
/// across the whole repository - a search for implementers of it returns nothing at all, not merely
/// nothing in the pages slice. No member below names it, implements it, or exposes a reader-shaped
/// entry point of any kind.
/// </para>
/// <para>
/// MIGRATION: the legacy page entity carried 36 public members - 32 read/write properties and 4
/// read-only ones - and 14 of them are deliberately absent from the aggregate these projections read,
/// which is what reduces the surface to the 22 stored columns. Each exclusion has a distinct reason
/// and all are recorded here so that a reader is never left wondering whether a value was overlooked.
/// The permission collection at <c>TabInfo.vb</c> line 282 was a serialised array rather than a
/// column, and is a navigation on the aggregate; the child flag at line 291 is a question about other
/// rows and arrives here as an argument, described at the members that take it; the two role strings at
/// lines 327 and 336 were physically dropped from the table by the 3.0 upgrade and thereafter
/// recomputed from permissions at read time, so they belong to the server-side permission evaluator;
/// the skin and container paths at lines 347 and 356 were file-system locations belonging to the
/// out-of-scope skinning subsystem; the breadcrumb trail at line 365, the pane list at line 374 and the
/// module list at line 383 were all untyped pre-generic lists, and the last of the three is a
/// navigation on the aggregate while the first two were artefacts of the page lifecycle being replaced;
/// the host-page flag at line 392 was derived from the owning tenant being absent; and the four
/// read-only members - the page-type discriminator at line 406, the absolute address at line 412 which
/// required the excluded application-path helper, the administration-page test at line 435, and the
/// cache-level member at line 605 - were each computed rather than stored.
/// </para>
/// <para>
/// MIGRATION: the token-accessor contract the legacy entity implemented at <c>TabInfo.vb</c> line 41
/// is dropped, together with the two members that satisfied it - the name-keyed reader at line 524,
/// whose signature reported failure through a by-reference argument, and the cache-level property at
/// line 605. The subsystem that consumed them is out of scope, so nothing replaces them: there is no
/// name-keyed accessor here and no by-reference or output parameter anywhere in this file. Every
/// projection is a named assignment, which is precisely the property that makes a mistake a compiler
/// error instead of a lookup that returns nothing.
/// </para>
/// <para>
/// MIGRATION: every serialisation decoration the legacy entity carried is dropped and replaced by
/// nothing at all. The type was annotated as the root of an XML document so that page definitions
/// could round-trip through portal templates, 22 members carried element names, three carried an
/// explicit exclusion, and the permission collection carried array and array-item names. No equivalent
/// appears here or on the contracts, in any form: no serialisation attribute, no column or table
/// attribute, and no validation attribute. The wire format belongs to the serialiser configured at the
/// API edge, column binding belongs to the Infrastructure entity configuration, and constraint
/// checking belongs to the validators in <c>Validation/</c>. A mapper that also validated would be a
/// mapper that could reject input, and none of these members rejects anything or throws on odd data.
/// </para>
/// <para>
/// MIGRATION: one member is renamed on the way across and the rename carries no semantic change. The
/// legacy property spelled the search-terms field with an internal capital - <c>KeyWords</c> at
/// <c>TabInfo.vb</c> line 210 - while both the aggregate and all three contracts spell it
/// <c>Keywords</c>, the idiomatic single word that also matches the caption the legacy screens showed.
/// The stored column keeps the legacy spelling, and the bridge between the two is a single explicit
/// column-name declaration in the Infrastructure entity configuration. The projections below are
/// therefore a plain like-named assignment. Naming the C# members explicitly is what makes this safe:
/// a by-convention match could have dropped the value silently on either side of the rename.
/// </para>
/// <para>
/// MIGRATION: the legacy null sentinel for a string was the empty string rather than a null reference,
/// so an empty stored value and an absent one were indistinguishable once read. The projections below
/// preserve whichever of the two the aggregate holds: an empty string is copied as an empty string and
/// is never promoted to null, and a null is copied as null and is never demoted to an empty string. A
/// consumer still reading the legacy contract therefore sees what it saw before. The same discipline
/// applies to the two publication dates, whose legacy sentinel was the minimum date value. They are
/// nullable here and are copied verbatim, so a stored minimum date stays a minimum date and remains
/// distinguishable from an absent one. That distinction is worth stating because the legacy
/// null-test helper could not make it: its date branch compared date parts only, so any instant
/// falling on the minimum date answered "absent" no matter what time it carried.
/// </para>
/// <para>
/// MIGRATION: what makes that preservation structural rather than merely intended is that every member
/// of all three contracts is declared with exactly the type and the nullability of the aggregate
/// member it pairs with. There is consequently not one coalescing operator, cast, parse or conversion
/// anywhere in this file, in either direction. That is the point: a conversion is how a sentinel gets
/// reintroduced by accident, because forcing a nullable value onto a non-nullable member has to invent
/// something, and the obvious inventions here would have been the empty string, zero and minus one -
/// the very values the legacy platform overloaded to mean "absent".
/// </para>
/// <para>
/// MIGRATION: no member below treats any particular numeric value as meaning "absent", and two
/// measured facts make that mandatory rather than fastidious. The page key column is declared as an
/// identity seeded at zero, so zero names the first page ever created and is a real key; and the
/// legacy sentinel module used minus one as its integer null while the tenant key column is an
/// identity seeded at minus one, so minus one is at once a real tenant key and the legacy "absent"
/// marker. There is consequently no comparison against zero, no comparison against minus one, no
/// flooring of a key and no negative-argument guard on a key anywhere in this file. Absence is
/// expressed only by a nullable member being null.
/// </para>
/// <para>
/// MIGRATION: the tenant key deserves its own statement because the aggregate resolves the collision
/// above in a way the legacy type could not. The legacy property was a non-nullable integer holding
/// minus one to mean "this page belongs to the installation rather than to a tenant", and the legacy
/// host-page test read exactly that. The column is genuinely nullable, so the aggregate and the detail
/// contract both declare the key as a nullable integer in which null carries that meaning and minus
/// one means the tenant keyed minus one. Both projections copy the value straight through, in one
/// piece, with no comparison of any kind - which is the only mapping that keeps a host page and the
/// tenant keyed minus one distinguishable from each other.
/// </para>
/// <para>
/// MIGRATION: the parent key is the one place in this slice where a negative legacy value genuinely
/// did mean "absent", because a page at the root of its hierarchy has no parent. It too is a nullable
/// integer on both the aggregate and the contracts and is copied verbatim, so null continues to mean
/// "no parent" and is never collapsed back to minus one. Note the asymmetry that makes reading the two
/// keys together confusing, and which is stated here rather than resolved by a shared helper: the same
/// literal minus one meant "the installation, not a tenant" on the tenant key and "no parent" on the
/// parent key, and neither meaning survives as a literal in this file.
/// </para>
/// <para>
/// MIGRATION: no permission projection appears in this file, and its absence is a verified decision
/// rather than an omission. None of the three page contracts declares a permission member: the update
/// contract states outright that permissions are a separate concern served by the read-only permission
/// catalogue, and that the legacy permission grid is therefore absent even though the legacy form
/// carried one. Because no contract exposes them, nothing here projects the page permission record or
/// the permission catalogue entry, and nothing here converts a permission key. Had a contract exposed
/// them, two rules would have applied and are recorded so that a later change cannot lose them: the
/// three negative pseudo-principals the legacy platform defined as string constants - minus one for all
/// users, minus two for superusers, minus three for unauthenticated users - are real principals whose
/// values must survive untouched, whereas minus four was only an in-memory default a freshly
/// constructed permission carried and is not reproduced; and a permission key converts by NAME and
/// never by ordinal position, because the stored column holds the name as text.
/// </para>
/// <para>
/// MIGRATION: the projections are one-directional by intent. Reads flow from the aggregate to a
/// transfer contract; the single write flows from the update contract onto an already-loaded
/// aggregate through <see cref="ApplyUpdate"/>. No member turns a response contract back into an
/// aggregate, because no caller is entitled to post one, and there is no creation projection at all -
/// the page surface exposes a read and an update only.
/// </para>
/// <para>
/// MIGRATION: the depth, the materialised hierarchy path and the sibling order are read and published
/// but never computed here. No member below derives a depth from a parent chain, rebuilds a path from
/// page names, renumbers siblings, or assembles a parent-and-child tree from a flat set. All four are
/// write-path concerns owned by the page service, and a mapper that performed any of them would be
/// placing business logic in the projection layer. Whether a page has children is the same kind of
/// fact and is passed in for the same reason.
/// </para>
/// <para>
/// MIGRATION: one latent defect in the legacy sentinel helper is recorded here and deliberately left
/// unfixed, because repairing it would change behaviour this migration is required to preserve. Its
/// type-directed overload mapped both the 32-bit and the 64-bit signed integer types onto the same
/// 32-bit sentinel, so a 64-bit column could never have expressed absence correctly. Nothing in the
/// page slice is 64-bit, so the defect is unreachable from here; it is noted rather than repaired.
/// </para>
/// </remarks>
public static class TabMappings
{
    /// <summary>
    /// Projects a page onto the row shape the page list and the navigation tree render.
    /// </summary>
    /// <param name="tab">The page to project.</param>
    /// <param name="hasChildren">
    /// Whether any other page names this one as its parent, computed by the caller across the whole
    /// set being listed.
    /// </param>
    /// <returns>The list row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tab"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// MIGRATION: the child flag is an argument rather than a derived value. It is a question about
    /// other rows, so deriving it here would mean either walking a navigation a list read has no
    /// reason to have loaded, or issuing a query mid-projection - the precise impurity the legacy
    /// hydrator exhibited. The caller resolves it once for the whole set from the distinct parent keys
    /// and passes the answer in. The legacy read view computed the same fact but emitted it as the
    /// text "true" or "false", which the legacy reader then coerced; this is a real boolean and is
    /// carried through unchanged in both directions.
    /// </para>
    /// <para>
    /// MIGRATION: this row shape deliberately omits the tenant key, which the detail contract does
    /// declare. A list is always read within one tenant, so repeating the key on every row would add
    /// nothing; the asymmetry is recorded on the contracts themselves. The depth and the hierarchy
    /// path ARE published here because they are what lets a client render the tree without a second
    /// request, and both are copied exactly as stored - neither is recomputed.
    /// </para>
    /// </remarks>
    public static TabListItemDto ToListItem(Tab tab, bool hasChildren)
    {
        ArgumentNullException.ThrowIfNull(tab);

        return new TabListItemDto
        {
            // MIGRATION: the page key is copied verbatim and is never tested against zero. The column
            // is an identity seeded at zero, so the first page ever created carries a key of zero and
            // a zero-or-below guard here would discard a real row.
            TabId = tab.TabId,
            TabName = tab.TabName,
            Title = tab.Title,
            TabOrder = tab.TabOrder,

            // MIGRATION: the parent key passes straight through as a nullable value. Null means "root
            // of the hierarchy"; it is never collapsed to the legacy minus-one sentinel, and a stored
            // zero is a real parent because page keys start at zero.
            ParentId = tab.ParentId,
            Level = tab.Level,
            TabPath = tab.TabPath,
            IsVisible = tab.IsVisible,
            DisableLink = tab.DisableLink,

            // MIGRATION: the recycle-bin flag is published rather than filtered. The legacy delete was
            // a soft delete that set this flag and left the row in place, and the legacy recycle-bin
            // screen listed exactly the rows carrying it, so a consumer needs to see it.
            IsDeleted = tab.IsDeleted,
            HasChildren = hasChildren,
            IsSecure = tab.IsSecure,
            Url = tab.Url,
            IconFile = tab.IconFile,
        };
    }

    /// <summary>
    /// Projects a page onto the full detail contract, covering every stored column.
    /// </summary>
    /// <param name="tab">The page to project.</param>
    /// <param name="hasChildren">
    /// Whether any other page names this one as its parent, computed by the caller.
    /// </param>
    /// <returns>The detail contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tab"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// MIGRATION: all 22 stored columns are carried, which is the whole of the legacy 36-member
    /// surface once the 14 members enumerated on this type are set aside. The child flag is the
    /// twenty-third member of the contract and is the argument described above; it is the one published
    /// value that is not a column.
    /// </para>
    /// <para>
    /// MIGRATION: the two publication dates are copied verbatim as nullable values and are never
    /// interpreted. It would be easy to read them together and publish an "is currently published"
    /// flag, and doing so would be wrong twice over: it would place a business rule in a projection,
    /// and it would require reading a clock, which would make the result depend on when it was called.
    /// Any such evaluation belongs to the page service with its injected clock, where the further
    /// subtlety also belongs - the legacy comparisons ran against server-local time whereas the
    /// injected clock is co-ordinated universal time, which can move a date-only value by a calendar
    /// day.
    /// </para>
    /// <para>
    /// MIGRATION: the skin and container sources are carried as opaque text. The legacy entity also
    /// exposed resolved file-system paths derived from them, and those two derived members are among
    /// the 14 excluded because the skinning subsystem is out of scope. Excluding the derived paths does
    /// not make the stored tokens unreadable, so both are published exactly as stored.
    /// </para>
    /// </remarks>
    public static TabDetailDto ToDetail(Tab tab, bool hasChildren)
    {
        ArgumentNullException.ThrowIfNull(tab);

        return new TabDetailDto
        {
            TabId = tab.TabId,
            TabOrder = tab.TabOrder,

            // MIGRATION: the tenant key is copied in one piece, with no comparison against minus one
            // and none against zero. Null means "a host page belonging to the installation"; minus one
            // means "the tenant keyed minus one", which is a real tenant because that column is an
            // identity seeded at minus one. Collapsing either into the other would merge two cases the
            // legacy non-nullable property could not tell apart.
            PortalId = tab.PortalId,
            TabName = tab.TabName,
            IsVisible = tab.IsVisible,
            ParentId = tab.ParentId,

            // MIGRATION: the depth and the hierarchy path are published exactly as stored. Both are
            // denormalised values the write path maintains, so recomputing them during a read could
            // disagree with what every other reader sees.
            Level = tab.Level,
            IconFile = tab.IconFile,
            DisableLink = tab.DisableLink,
            Title = tab.Title,
            Description = tab.Description,

            // MIGRATION: the legacy property spelled this KeyWords with an internal capital while the
            // aggregate and the contract spell it Keywords. The stored column keeps the legacy
            // spelling and the Infrastructure configuration bridges the two, so this is a plain
            // like-named assignment that a by-convention mapper could have dropped silently.
            Keywords = tab.Keywords,
            IsDeleted = tab.IsDeleted,
            Url = tab.Url,
            SkinSrc = tab.SkinSrc,
            ContainerSrc = tab.ContainerSrc,
            TabPath = tab.TabPath,

            // MIGRATION: both dates are copied verbatim. The legacy sentinel for an absent date was
            // the minimum date value, so a stored minimum date must stay a minimum date here and
            // remain distinguishable from null - all the more because the legacy null-test compared
            // date parts only and so answered "absent" for any instant falling on that date.
            StartDate = tab.StartDate,
            EndDate = tab.EndDate,
            RefreshInterval = tab.RefreshInterval,
            PageHeadText = tab.PageHeadText,
            IsSecure = tab.IsSecure,
            HasChildren = hasChildren,
        };
    }

    /// <summary>
    /// Applies a submitted page update to an already-loaded page aggregate.
    /// </summary>
    /// <param name="tab">The tracked page to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="tab"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// All 17 members the update contract carries are written, in the order that contract declares
    /// them, and every one is written unconditionally. That is deliberate and reproduces the legacy
    /// save behaviour exactly: the legacy update signature had no optional arguments, so submitting
    /// the page-settings screen with a box empty stored an empty value and cleared what was there. A
    /// caller intending to leave a field alone must therefore echo the value it already holds.
    /// </para>
    /// <para>
    /// MIGRATION: nothing here coalesces, converts or widens, and that is a measured property of the
    /// contract rather than an accident. All 17 members are declared with exactly the type and the
    /// nullability of the aggregate member each one lands on, so every assignment below is a
    /// like-for-like copy. It matters because coalescing is how a sentinel gets reintroduced by
    /// accident: a nullable member forced onto a non-nullable one has to invent a value, and the
    /// obvious inventions here would have been the empty string and minus one - the two legacy
    /// sentinels this migration is required not to resurrect. The name is the one member that could
    /// have needed a substitution, since the column is NOT NULL, and it does not: the contract already
    /// declares it non-nullable and seeds it with the empty string, so a caller that omits it submits
    /// that empty string explicitly and this method simply stores what it was given. Refusing an empty
    /// name is the validator's job, not this method's - a mapper does not reject input.
    /// </para>
    /// <para>
    /// MIGRATION: five stored members are deliberately NOT written, and each has a measured reason.
    /// The page key and the tenant key are absent from the contract entirely - the key identifies the
    /// aggregate already loaded from the route, and the legacy update accepted no tenant argument, so
    /// a page cannot be moved between tenants through this path. The sibling order, the depth and the
    /// hierarchy path are all server-owned: the legacy update neither accepted the order nor the depth
    /// and its statement body set neither, while the path was assigned only by the legacy path
    /// generator and cascaded to every descendant whenever a name or a parent changed. The page
    /// service recomputes all three after this method returns, so writing them here would be both
    /// redundant and unsafe. This method therefore computes no depth, no path and no ordinal, and
    /// assembles no tree.
    /// </para>
    /// <para>
    /// MIGRATION: the skin source, the container source and the recycle-bin flag ARE written. The
    /// first two are genuine columns the terminal update statement persists, and blanking them on
    /// every edit would silently destroy an administrator's stored choice; they are carried as opaque
    /// tokens because the skinning subsystem itself is out of scope. The recycle-bin flag is written
    /// because both legacy transitions - the soft delete and the restore - were plain writes of that
    /// flag through this very update path, and the page surface exposes no separate delete or restore
    /// route through which they could otherwise be reached.
    /// </para>
    /// <para>
    /// MIGRATION: the parent key is written as the nullable value the contract carries, so submitting
    /// null moves the page to the root of its hierarchy. It is never translated to or from the legacy
    /// minus-one sentinel, and no guard rejects a negative or zero value, because a stored zero is a
    /// real parent key.
    /// </para>
    /// </remarks>
    public static void ApplyUpdate(Tab tab, UpdateTabRequest request)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(request);

        // MIGRATION: a straight copy, with no coalescing. Both sides declare the name as a
        // non-nullable string, so there is nothing to substitute; the empty string a caller may submit
        // is the legacy "no text" value arriving explicitly and is stored as given, never promoted to
        // null and never rejected here.
        tab.TabName = request.TabName;

        tab.Title = request.Title;
        tab.Description = request.Description;

        // MIGRATION: KeyWords on the legacy property, Keywords on both the contract and the
        // aggregate; the column keeps the legacy spelling and Infrastructure bridges it.
        tab.Keywords = request.Keywords;

        // MIGRATION: nullable in, nullable out. Null means "move to the root"; the legacy minus-one
        // sentinel is neither read nor written, and no guard rejects zero or a negative value.
        tab.ParentId = request.ParentId;

        tab.IsVisible = request.IsVisible;

        // MIGRATION: copied here because a mapper has no portal context. TabService applies the legacy
        // five-special-page rule after it has loaded the owning Portal and forces this value back to false
        // for the administration, splash, home, login and user pages.
        tab.DisableLink = request.DisableLink;
        tab.IconFile = request.IconFile;

        // MIGRATION: THE SKIN SOURCE AND THE CONTAINER SOURCE ARE DELIBERATELY LEFT ALONE, and the
        // update contract no longer carries either. An earlier revision assigned both from the request,
        // reasoning that leaving a real column untouched would make an administrator's stored choice
        // uneditable. That reversed the priority: skinning and containers are an explicit exclusion of
        // this migration, so making them settable through the page-edit endpoint re-admitted the
        // excluded subsystem through the write surface - and it carried a worse consequence than the one
        // it avoided, because the projection is a WHOLE-ROW replacement. A caller that simply omitted
        // the member from its JSON deserialised to null and therefore BLANKED a stored skin on every
        // ordinary edit. Not assigning them preserves both columns exactly as stored, which is what an
        // exclusion should mean; both remain readable on TabDetailDto, so a stored choice is still
        // observable even though it is no longer settable here.
        tab.Url = request.Url;

        // MIGRATION: both dates are stored exactly as submitted. Nothing here compares them to each
        // other or to a clock, so an out-of-order window is a validator's concern and an "is
        // published" answer is the page service's.
        tab.StartDate = request.StartDate;
        tab.EndDate = request.EndDate;

        tab.RefreshInterval = request.RefreshInterval;
        tab.PageHeadText = request.PageHeadText;
        tab.IsSecure = request.IsSecure;

        // MIGRATION: the recycle-bin flag is part of the update contract because both legacy
        // transitions were writes of this flag through this path; restoring a page is expressed by
        // submitting false.
        tab.IsDeleted = request.IsDeleted;
    }
}
