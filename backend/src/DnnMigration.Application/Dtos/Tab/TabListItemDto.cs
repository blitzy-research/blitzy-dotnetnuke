namespace DnnMigration.Application.Dtos.Tab;

/// <summary>A single row in the page list returned by <c>GET /api/v1/portals/{id}/tabs</c>.</summary>
/// <remarks>
/// <para>
/// The schema retains the legacy term <em>Tab</em>, while administrator-facing labels say <em>Page</em>.
/// <c>PortalId</c> is deliberately absent because the portal-scoped route already identifies the tenant for
/// every row; the detail shape carries it because <c>GET /api/v1/tabs/{id}</c> is not portal-scoped.
/// </para>
/// <para>
/// This DTO is the sentinel boundary: legacy <c>-1</c> becomes nullable integer state, while legacy empty
/// strings and target <see langword="null"/> remain distinct unless the mapper explicitly documents a
/// conversion. Members are inert auto-properties; central JSON policy supplies camel-case names, and the
/// legacy XML attributes, token accessor, and validation metadata are not reproduced.
/// </para>
/// </remarks>
public sealed class TabListItemDto
{
    // MIGRATION: Tabs.TabID is IDENTITY(0, 1), so 0 is persisted data, not an unset marker.
    /// <summary>Gets or sets the page identifier. Maps the legacy <c>Tabs.TabID</c> column.</summary>
    public int TabId { get; set; }

    // MIGRATION: the list binds this non-null display value; null would create a blank row.
    /// <summary>
    /// Gets or sets the menu text, labelled "Page Name" in the legacy UI. Maps <c>Tabs.TabName</c>,
    /// <c>nvarchar(50) NOT NULL</c>.
    /// </summary>
    public string TabName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the browser title, labelled "Page Title" in the legacy UI. Maps <c>Tabs.Title</c>,
    /// <c>nvarchar(200) NULL</c>.
    /// </summary>
    public string? Title { get; set; }

    // MIGRATION: TabOrder is server-derived and deliberately absent from the update request.
    /// <summary>
    /// Gets or sets the server-computed sibling order. Maps <c>Tabs.TabOrder</c>, <c>int NOT NULL DEFAULT
    /// (0)</c>.
    /// </summary>
    /// <remarks>
    /// The terminal schema is non-nullable, so the legacy <c>-1</c> read sentinel is not part of this
    /// contract.
    /// </remarks>
    public int TabOrder { get; set; }

    // MIGRATION: legacy ParentId was a non-nullable Integer with -1 meaning no parent. The target uses
    // int? and emits an explicit `"parentId": null` for a root page, never -1 or an omitted member.
    /// <summary>
    /// Gets or sets the parent identifier, or <see langword="null"/> for a root page. Maps
    /// <c>Tabs.ParentId</c>, <c>int NULL</c>.
    /// </summary>
    /// <remarks>
    /// Central serialization writes the null member. Test roots with <c>ParentId is null</c>, never <c>==
    /// -1</c> or <c>&lt;= 0</c>, because those values collide with live schema semantics.
    /// </remarks>
    public int? ParentId { get; set; }

    // MIGRATION: Level is server-derived and exposed only so a client can render the hierarchy.
    /// <summary>
    /// Gets or sets the hierarchy depth, where <c>0</c> is a root page. Maps <c>Tabs.Level</c>, <c>int NOT
    /// NULL DEFAULT (0)</c>.
    /// </summary>
    /// <remarks>
    /// The terminal schema is non-nullable, so the legacy <c>-1</c> read sentinel is not part of this
    /// contract.
    /// </remarks>
    public int Level { get; set; }

    // MIGRATION: TabPath is generated from ParentId and TabName and is never client-authored.
    /// <summary>
    /// Gets or sets the server-generated hierarchy path. Maps <c>Tabs.TabPath</c>, <c>nvarchar(255)
    /// NULL</c>.
    /// </summary>
    public string? TabPath { get; set; }

    /// <summary>
    /// Gets or sets whether the page appears in navigation. Labelled "Include In Menu" in the legacy UI;
    /// maps <c>Tabs.IsVisible</c>, <c>bit NOT NULL DEFAULT (1)</c>.
    /// </summary>
    public bool IsVisible { get; set; }

    /// <summary>
    /// Gets or sets whether the page link is disabled. Labelled "Disabled" in the legacy UI; maps
    /// <c>Tabs.DisableLink</c>, <c>bit NOT NULL DEFAULT (0)</c>.
    /// </summary>
    public bool DisableLink { get; set; }

    // MIGRATION: soft deletion remains row state. The DTO exposes it and leaves filtering to the caller.
    /// <summary>
    /// Gets or sets whether the page is in the recycle bin. Maps <c>Tabs.IsDeleted</c>, <c>bit NOT NULL
    /// DEFAULT (0)</c>.
    /// </summary>
    public bool IsDeleted { get; set; }

    // MIGRATION: HasChildren is a computed projection. The legacy view emitted strings
    // 'true'/'false'; this contract emits a JSON boolean.
    /// <summary>Gets or sets whether another page names this page as its parent.</summary>
    /// <remarks>
    /// This is not a table column or domain-entity member; the repository or service populates it so the
    /// client can render an expander without a per-row query.
    /// </remarks>
    public bool HasChildren { get; set; }

    /// <summary>
    /// Gets or sets whether the page requires a secure connection. Labelled "Secure?" in the legacy UI;
    /// maps <c>Tabs.IsSecure</c>, <c>bit NOT NULL DEFAULT (0)</c>.
    /// </summary>
    public bool IsSecure { get; set; }

    /// <summary>
    /// Gets or sets the external navigation target, labelled "Link Url" in the legacy UI. Maps
    /// <c>Tabs.Url</c>, <c>nvarchar(255) NULL</c>.
    /// </summary>
    public string? Url { get; set; }

    // MIGRATION: IconFile may be a raw fileid=NNN token. Resolution belongs to the repository or mapper,
    // never to this inert DTO.
    /// <summary>Gets or sets the menu-icon reference. Maps <c>Tabs.IconFile</c>, <c>nvarchar(100) NULL</c>.</summary>
    /// <remarks>
    /// Consumers must tolerate either a path or an unresolved token. Legacy absence was <c>""</c>; the
    /// target permits <see langword="null"/>, and the mapper must preserve its supplied representation.
    /// </remarks>
    public string? IconFile { get; set; }
}
