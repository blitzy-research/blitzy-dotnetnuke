namespace DnnMigration.Application.Dtos.Tab;

/// <summary>
/// The client-settable state of one page, submitted as the request body of <c>PUT /api/v1/tabs/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The procedure's <c>UPDATE</c> body touches neither the portal, the sibling order nor the depth. That was
/// measured in the terminal script rather than inferred, and it is the direct reason none of the three
/// appears here.
/// </para>
/// <para>
/// Members excluded because the legacy column no longer exists. Two role-string columns were physically
/// dropped in the 3.0 upgrade and thereafter recomputed from permissions at read time, and four further
/// columns - two mobile-presentation, two pane-width - were dropped outright in the 2.0 upgrade despite
/// appearing in the original create-table statement.
/// </para>
/// </remarks>
public sealed class UpdateTabRequest
{
    // Initialised rather than left to the global uninitialised-property suppression.
    /// <summary>
    /// Gets or sets the page's name as it appears in the navigation menu. Captioned "Page Name" in the
    /// legacy administration screens.
    /// </summary>
    public string TabName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the page title rendered in the browser window title. Captioned "Page Title" in the
    /// legacy administration screens.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the page description used in page metadata. Captioned "Description" in the legacy
    /// administration screens.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "Enter a description about this page here." Maximum length 500.
    /// </remarks>
    public string? Description { get; set; }

    // The property is spelled Keywords; the database column and the stored-procedure parameter are both
    // spelled KeyWords, with a capital W. The bridge is deliberate: the wire contract and the domain-facing
    // name use conventional English casing while the persistence layer keeps the legacy spelling, and the
    // mapper crosses between the two explicitly.
    /// <summary>
    /// Gets or sets the comma-separated keywords used in page metadata. Captioned "Keywords" in the legacy
    /// administration screens.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "Enter some keywords for this page (separated by commas). These keywords are used
    /// by search engines to help index your site's pages."
    /// </remarks>
    public string? Keywords { get; set; }

    // A non-nullable legacy Integer whose sentinel was -1 becomes a genuine int?.
    // Library/Components/Tabs/TabInfo.vb line 156 declares ParentId As Integer, and the legacy constructor
    // sentinel-initialised it to Null.NullInteger, which is literally -1.
    /// <summary>
    /// Gets or sets the identifier of the page this page hangs beneath, or <see langword="null"/> to make
    /// it a root-level page. Captioned "Parent Page" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// Changing this moves the page and every descendant with it, and the server recomputes the depth, the
    /// sibling order and the materialised path of the affected subtree afterwards. None of those three is
    /// accepted from a client.
    /// </remarks>
    public int? ParentId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the page is included in the navigation menu. Captioned
    /// "Include In Menu" in the legacy administration screens - it is a menu-inclusion flag, not a
    /// general-purpose visibility or publication switch.
    /// </summary>
    /// <remarks>
    /// No initialiser is declared, so an omitted field binds to <see langword="false"/>. That is
    /// intentional for a complete-replacement write: the column default of <c>1</c> governs the creation of
    /// a row, which this endpoint cannot do, and inventing a default here would let an omitted field
    /// silently contradict the submitted document.
    /// </remarks>
    public bool IsVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the page's navigation entry is inert - present in the menu
    /// but not navigable. Captioned "Disabled" in the legacy administration screens.
    /// </summary>
    public bool DisableLink { get; set; }

    /// <summary>
    /// Gets or sets the reference to the icon shown beside this page in the menu. Captioned "Icon" in the
    /// legacy administration screens.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "You can choose an icon that can be used in the menu." Maximum length 100.
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// Gets or sets the target that turns this page into a navigation link to another resource, or <see
    /// langword="null"/> for an ordinary page. Captioned "Link Url" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// Legacy help text: "If you would like this page to behave as a navigation link to another resource,
    /// you can specify the Link URL value here. Please note that this field is optional."
    /// </remarks>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the moment from which the page becomes available, or <see langword="null"/> for no
    /// start restriction. Captioned "Start Date:" in the legacy administration screens.
    /// </summary>
    public DateTime? StartDate { get; set; }

    // The legacy application enforced no end-date-after-start-date rule, and none is enforced here - not as
    // an attribute, not as code, and not as documentation implying one exists. The absence is deliberate
    // behavioural parity: a page may carry an end date earlier than its start date, and the effect is a page
    // that is never available.
    /// <summary>
    /// Gets or sets the moment at which the page stops being available, or <see langword="null"/> for no
    /// end restriction. Captioned "End Date:" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// <strong>No relationship between this member and the start date is validated anywhere in the target,
    /// because none was validated anywhere in the legacy application.</strong> An end date earlier than the
    /// start date is accepted and stored, exactly as it was before. The legacy validator on this field
    /// checked only that the entered text was a parseable date, reporting "Invalid End Date" on failure.
    /// </remarks>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Gets or sets the automatic page-refresh interval <strong>in seconds</strong>, or <see
    /// langword="null"/> for no automatic refresh. Captioned "Refresh Interval (seconds)" in the legacy
    /// administration screens.
    /// </summary>
    public int? RefreshInterval { get; set; }

    /// <summary>
    /// Gets or sets raw markup to be emitted inside the page's head element, such as meta tags. Captioned
    /// "Page Header Tags" in the legacy administration screens.
    /// </summary>
    public string? PageHeadText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the page must be served over a secure connection. Captioned
    /// "Secure?" in the legacy administration screens.
    /// </summary>
    /// <remarks>
    /// This column exists <em>only</em> in the terminal schema. It is absent from the baseline create-table
    /// statement and was added late in the upgrade chain, by the very same script that defines the terminal
    /// update procedure, as <c>ALTER TABLE ... Tabs ADD IsSecure bit NOT NULL ... DEFAULT (0)</c>.
    /// </remarks>
    public bool IsSecure { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the page is soft-deleted into the recycle bin. Maps the
    /// legacy <c>Tabs.IsDeleted</c> column, <c>bit NOT NULL</c> defaulting to <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Two related constraints are enforced by the page service rather than here. Restoration was blocked
    /// while the page's own parent remained deleted, refused with "Page Cannot Be Restored Until Its Parent
    /// Is Restored First."
    /// </remarks>
    public bool IsDeleted { get; set; }
}
