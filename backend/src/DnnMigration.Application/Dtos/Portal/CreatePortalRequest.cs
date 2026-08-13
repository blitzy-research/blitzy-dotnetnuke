namespace DnnMigration.Application.Dtos.Portal;

/// <summary>Inbound request contract for <c>POST /api/v1/portals</c>.</summary>
/// <remarks>
/// This type replaces the fifteen positional arguments of the legacy <c>PortalController.CreatePortal</c>
/// overload at <c>Library/Components/Portal/PortalController.vb</c>.
/// </remarks>
public sealed class CreatePortalRequest
{
    // The legacy CreatePortal returned an Integer and signalled failure with a NEGATIVE sentinel
    // identifier.

    /// <summary>Gets or sets the display name of the new portal.</summary>
    public string? PortalName { get; set; }

    // MIGRATION: two documented behavioural differences attach to the alias, and neither is implemented on
    // this request.

    /// <summary>Gets or sets the HTTP alias through which the new portal is addressed.</summary>
    /// <remarks>
    /// Callers must expect the stored alias to differ in case from the value they submit, and must not read
    /// partial, substring or pattern matching into it.
    /// </remarks>
    public string? PortalAlias { get; set; }

    /// <summary>Gets or sets the portal description used for site metadata.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the portal keywords used for site metadata.</summary>
    public string? KeyWords { get; set; }

    /// <summary>
    /// Gets or sets the portal-relative home directory for uploaded content, or leaves it unset so that the
    /// service derives the default.
    /// </summary>
    /// <remarks>
    /// Legacy argument 11, whose backing column is <c>UploadDirectory nvarchar(100) NOT NULL</c> in the
    /// baseline schema, renamed later in the upgrade chain.
    /// </remarks>
    public string? HomeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the name of the portal template that seeds the new portal's pages, modules and roles.
    /// </summary>
    /// <remarks>
    /// A file name only, never a path: the directory was legacy argument 9 and is deliberately absent, as
    /// the note above <see cref="IsChildPortal"/> records.
    /// </remarks>
    public string? TemplateFile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the new portal is a child portal addressed beneath an
    /// existing portal's alias rather than a parent portal with an alias of its own.
    /// </summary>
    /// <remarks>
    /// Non-nullable, matching the legacy <c>Boolean</c>. The legacy null contract cannot express an unset
    /// boolean - its boolean sentinel is <c>False</c> and its absence test consequently reports
    /// <c>False</c> itself as absent - so a deliberate "parent portal" and an omitted choice were already
    /// indistinguishable.
    /// </remarks>
    public bool IsChildPortal { get; set; }

    // Legacy arguments were named FirstName, LastName, Username, Password and Email, and each is renamed
    // below with an Administrator prefix, because NONE of them is a Portals column.

    /// <summary>
    /// Gets or sets the given name of the initial administrator user created together with the portal.
    /// </summary>
    public string? AdministratorFirstName { get; set; }

    /// <summary>
    /// Gets or sets the family name of the initial administrator user created together with the portal.
    /// </summary>
    public string? AdministratorLastName { get; set; }

    /// <summary>
    /// Gets or sets the sign-in name of the initial administrator user created together with the portal.
    /// </summary>
    public string? AdministratorUsername { get; set; }

    // The password store changed, and the change is deliberate rather than incidental.

    /// <summary>
    /// Gets or sets the initial administrator's password. Write-only: accepted on create, never returned,
    /// never logged.
    /// </summary>
    /// <remarks>
    /// Handling requirements. The value is hashed by <c>Infrastructure/Security/BcryptPasswordHasher.cs</c>
    /// before it is persisted and is retained in no other form.
    /// </remarks>
    public string? AdministratorPassword { get; set; }

    /// <summary>
    /// Gets or sets the electronic mail address of the initial administrator user created together with the
    /// portal.
    /// </summary>
    /// <remarks>
    /// Measured validation rules: required, message "Email Is Required."; control maximum length 100; and,
    /// importantly, NO format pattern - the legacy screen declared no regular-expression validator on this
    /// field, and every validator on that screen is a required-field validator. The validator author must
    /// reproduce that rule set exactly and must not invent a format rule the legacy screen did not impose.
    /// </remarks>
    public string? AdministratorEmail { get; set; }
}
