namespace DnnMigration.Application.Dtos.Tab;

/// <summary>
/// The complete read projection for a single page, returned by <c>GET /api/v1/tabs/{tabId}</c> and by
/// <c>PUT /api/v1/tabs/{tabId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Purpose. This type is the detail representation of one page.
/// </para>
/// <para>
/// Composition: twenty-two columns plus one projection. Twenty-two of the members map one-to-one onto
/// persisted columns of the legacy <c>Tabs</c> table in its terminal state; the twenty-third, the
/// child-existence flag, is computed rather than stored.
/// </para>
/// </remarks>
public sealed class TabDetailDto
{
    // Tabs.TabID is declared IDENTITY(0, 1), so 0 is a real persisted identifier and not an "unset" marker.
    /// <summary>
    /// Gets or sets the page identifier. Maps the legacy <c>Tabs.TabID</c> column, <c>int IDENTITY(0, 1)
    /// NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// Because the column is seeded at zero, <c>0</c> is a legitimate persisted identifier belonging to a
    /// real page — the first page created in a DotNetNuke database has identifier <c>0</c>. Never treat
    /// <c>0</c>, or the equivalent <c>default(int)</c>, as meaning absent, unset or not yet saved.
    /// </remarks>
    public int TabId { get; set; }

    // TabOrder is server-owned and read-only to clients.
    /// <summary>
    /// Gets or sets the sort position of this page among its siblings under the same parent. Maps the
    /// legacy <c>Tabs.TabOrder</c> column, <c>int NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    public int TabOrder { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal that owns this page, or <see langword="null"/> when the
    /// page is a host-level page rather than a portal page. Maps the legacy <c>Tabs.PortalID</c> column,
    /// <c>int NULL</c>.
    /// </summary>
    /// <remarks>
    /// The collision that forces this change: <c>-1</c> is also a perfectly valid portal identifier,
    /// because <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c> — the seed and first generated
    /// value is <c>-1</c>, while the shipped default portal row is inserted explicitly with <c>PortalID</c>
    /// <c>0</c>, so both are real keys and <c>-1</c> is at once a real portal identifier and the legacy
    /// "absent" marker.
    /// </remarks>
    public int? PortalId { get; set; }

    /// <summary>
    /// Gets or sets the page name, captioned "Page Name" in the legacy administration screens. Maps the
    /// legacy <c>Tabs.TabName</c> column, <c>nvarchar(50) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text describes this as the name of the page, whose text "will be displayed in the
    /// menu system"; it was the only field on the legacy editor carrying a required-field validator, and
    /// the legacy lists bound it as their display text.
    /// </remarks>
    // Initialised rather than left to the global CS8618 suppression, which exists for ORM-materialised
    // entities backed by a NOT NULL column that rejects a null loudly.
    public string TabName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this page appears in the navigation menu. Captioned "Include
    /// In Menu" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// The member name is inherited from the schema, but this is not a general-purpose visibility flag and
    /// must not be read as one: it controls menu inclusion only. The legacy caption was "Include In Menu"
    /// and the legacy help text read "You have the choice on whether or not to include the page in the main
    /// navigation menu.
    /// </remarks>
    public bool IsVisible { get; set; }

    /// <summary>
    /// Gets or sets the identifier of this page's parent page, or <see langword="null"/> when the page sits
    /// at the root of the hierarchy. Maps the legacy <c>Tabs.ParentId</c> column, <c>int NULL</c>.
    /// </summary>
    /// <remarks>
    /// Test for a root-level page with <c>ParentId is null</c>.
    /// </remarks>
    public int? ParentId { get; set; }

    // Level is server-derived, not client-supplied. The legacy update path accepted no level argument: the
    // update procedure omits the column entirely, and the page-ordering routine was called with 0 for both
    // level and order so that it would recompute the whole subtree itself.
    /// <summary>
    /// Gets or sets the depth of this page in the hierarchy, where <c>0</c> denotes a root-level page. Maps
    /// the legacy <c>Tabs.Level</c> column, <c>int NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    public int Level { get; set; }

    // IconFile carries whatever the mapper supplies, which may be a raw `fileid=NNN` token rather than a
    // usable path.
    /// <summary>
    /// Gets or sets the reference to the menu icon for this page. Captioned "Icon" in the legacy
    /// administration screens.
    /// </summary>
    /// <remarks>
    /// The legacy value for "no icon" was the empty string rather than <c>null</c>, since the legacy
    /// null-string sentinel was <c>""</c>; the target representation is a nullable string and a mapper must
    /// not silently convert between the two. The maximum length of 100 characters is documentation only.
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this page is disabled. Captioned "Disabled" in the legacy
    /// administration screens.
    /// </summary>
    public bool DisableLink { get; set; }

    /// <summary>
    /// Gets or sets the page title, captioned "Page Title" in the legacy administration screens. Maps the
    /// legacy <c>Tabs.Title</c> column, <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text explains that this text "will be displayed in the browser window title". The
    /// legacy representation of "no title" was the empty string rather than <c>null</c>, because the legacy
    /// null-string sentinel was <c>""</c>; the target representation is a nullable string.
    /// </remarks>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the free-text description of this page, captioned "Description" in the legacy
    /// administration screens. Maps the legacy <c>Tabs.Description</c> column, <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text reads "Enter a description about this page here." The legacy representation of
    /// "no description" was the empty string rather than <c>null</c>, because the legacy null-string
    /// sentinel was <c>""</c>; the target representation is a nullable string, and a mapper must not
    /// silently convert between the two in either direction.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the comma-separated search keywords for this page, captioned "Keywords" in the legacy
    /// administration screens. Maps the legacy <c>Tabs.KeyWords</c> column, <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy help text reads "Enter some keywords for this page (separated by commas). These keywords
    /// are used by search engines to help index your site's pages."
    /// </remarks>
    public string? Keywords { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this page has been soft-deleted into the recycle bin. Maps
    /// the legacy <c>Tabs.IsDeleted</c> column, <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// A soft-deleted page remains a row in the table: deletion is a flag, not a removal, and the legacy
    /// recycle-bin screen enumerated pages by testing exactly this flag. The value is therefore surfaced
    /// rather than suppressed, and this projection neither filters nor hides it.
    /// </remarks>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the navigation target used when this page acts as a link to another resource rather
    /// than hosting content of its own. Captioned "Link Url" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// The legacy help text reads "If you would like this page to behave as a navigation link to another
    /// resource, you can specify the Link URL value here. Please note that this field is optional."
    /// </remarks>
    public string? Url { get; set; }

    // SkinSrc is a real, persisted column and is written by the terminal update procedure, so it belongs on
    // both the read and the write surface.
    /// <summary>
    /// Gets or sets the opaque skin source token applied to this page, captioned "Page Skin" in the legacy
    /// administration screens. Maps the legacy <c>Tabs.SkinSrc</c> column, <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy value for "no skin" was the empty string rather than <c>null</c>, since the legacy
    /// null-string sentinel was <c>""</c>; the target representation is a nullable string and a mapper must
    /// not silently convert between the two. The maximum length of 200 characters is documentation only.
    /// </remarks>
    public string? SkinSrc { get; set; }

    // MIGRATION: ContainerSrc is a real, persisted column and is written by the terminal update procedure,
    // so it belongs on both the read and the write surface.
    /// <summary>
    /// Gets or sets the opaque container source token applied to the modules on this page, captioned "Page
    /// Container" in the legacy administration screens. Maps the legacy <c>Tabs.ContainerSrc</c> column,
    /// <c>nvarchar(200) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy value for "no container" was the empty string rather than <c>null</c>, since the legacy
    /// null-string sentinel was <c>""</c>; the target representation is a nullable string and a mapper must
    /// not silently convert between the two. The maximum length of 200 characters is documentation only.
    /// </remarks>
    public string? ContainerSrc { get; set; }

    /// <summary>
    /// Gets or sets the materialised hierarchical path of this page. Maps the legacy <c>Tabs.TabPath</c>
    /// column, <c>nvarchar(255) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy representation of "no path" was the empty string rather than <c>null</c>, because the
    /// legacy null-string sentinel was <c>""</c>; the target representation is a nullable string, and a
    /// mapper must not silently convert between the two in either direction. The maximum length of 255
    /// characters is documentation only.
    /// </remarks>
    public string? TabPath { get; set; }

    /// <summary>
    /// Gets or sets the date from which this page becomes available, or <see langword="null"/> when no
    /// start date is set. Captioned "Start Date:" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// The legacy help text reads "Enter the start date for displaying this page. You may use the Calendar
    /// to pick a date."
    /// </remarks>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// Gets or sets the date after which this page ceases to be available, or <see langword="null"/> when
    /// no end date is set. Captioned "End Date:" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// The legacy application did not verify that this date follows the start date. Its only check on the
    /// field was a data-type check reporting "Invalid End Date", which validates the format of one value in
    /// isolation.
    /// </remarks>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Gets or sets the automatic page refresh interval <b>in seconds</b>, or <see langword="null"/> when
    /// the page does not refresh automatically. Captioned "Refresh Interval (seconds)" in the legacy
    /// administration screens.
    /// </summary>
    /// <remarks>
    /// The unit is seconds, on the authority of the legacy caption "Refresh Interval (seconds)". It is
    /// stated explicitly because the column name alone does not carry the unit and a consumer could
    /// otherwise reasonably guess milliseconds or minutes.
    /// </remarks>
    public int? RefreshInterval { get; set; }

    /// <summary>
    /// Gets or sets the raw markup injected into the document head when this page is rendered, captioned
    /// "Page Header Tags" in the legacy administration screens. Maps the legacy <c>Tabs.PageHeadText</c>
    /// column, <c>nvarchar(500) NULL</c>.
    /// </summary>
    /// <remarks>
    /// The legacy editor presented this as a free-form multi-line field and stored whatever was typed, so
    /// the value is arbitrary author-supplied markup rather than a structured value. It is carried
    /// verbatim: this projection neither parses, validates, sanitises nor escapes it.
    /// </remarks>
    public string? PageHeadText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this page must be served over a secure connection. Captioned
    /// "Secure?" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// This is the newest column on the page table, and it exists only in the terminal schema: it was
    /// introduced by the last upgrade script in the legacy chain, which added it as <c>bit NOT NULL</c>
    /// with a default of <c>0</c> and, in the same script, rebuilt the page read view to project it.
    /// </remarks>
    public bool IsSecure { get; set; }

    // HasChildren is a computed projection, not a persisted column — it is absent from the Tabs table and
    // from the Tab domain entity, and is populated by the repository or the application service.
    /// <summary>Gets or sets a value indicating whether any other page names this page as its parent.</summary>
    /// <remarks>
    /// This is not a persisted column and not a member of the page domain entity. It is a computed
    /// existence projection, populated by the repository or the application service, and it exists so that
    /// a client can decide whether to render an expander on a hierarchy node without issuing a second
    /// request.
    /// </remarks>
    public bool HasChildren { get; set; }
}
