namespace DnnMigration.Application.Dtos.Portal;

/// <summary>Inbound request contract for <c>POST /api/v1/portals</c>.</summary>
/// <remarks>
/// <para>
/// This type replaces the fifteen positional arguments of the legacy
/// <c>PortalController.CreatePortal</c> overload at
/// <c>Library/Components/Portal/PortalController.vb</c>. Twelve of those arguments were supplied by
/// a control on the legacy signup screen and appear below as named properties; three were computed
/// by the code-behind and are deliberately absent, each recorded against the property group it
/// would have joined.
/// </para>
/// <para>
/// A create request describes only what is to be created: it carries no status, identifier, success
/// flag or error member, because the portal service reports its outcome through its return value
/// and the controller translates that into an HTTP status and body.
/// </para>
/// </remarks>
public sealed class CreatePortalRequest
{
    // MIGRATION: the legacy CreatePortal returned an Integer and signalled failure with a NEGATIVE sentinel
    // identifier. That same value is both the legacy absent-integer sentinel AND the IDENTITY seed of the
    // Portals primary key - a real, addressable PortalID, with the shipped default portal at the very next
    // value, zero - so a legacy caller could not tell a failed creation from a successfully located portal.

    /// <summary>Gets or sets the display name of the new portal.</summary>
    /// <remarks>
    /// Legacy argument 1, <c>PortalName</c>, supplied by the <c>txtSiteName</c> text box
    /// (<c>maxlength</c> 128) and persisted to <c>Portals.PortalName</c>. Measured validation
    /// rules: required, message "Site Name Is Required."; maximum length 128; no format pattern.
    /// </remarks>
    public string? PortalName { get; set; }

    // MIGRATION: two documented behavioural differences attach to the alias, and neither is implemented on
    // this request.
    //
    // CASING IS NOT PRESERVED, which is legacy behaviour retained rather than introduced.
    // PortalAliasController.vb lower-cases the alias on every write and keys its lookups by the lower-cased
    // value; the signup screen lower-cased the field even earlier (Signup.ascx.vb) and stripped a leading
    // scheme.
    //
    // ALIAS RESOLUTION CHANGES FROM SUBSTRING TO EXACT MATCH, and this one IS a deliberate divergence. The
    // legacy tenant-resolution procedure selected min(PortalID) with "where PortalAlias like '%' +
    // @PortalAlias + '%'" (01.00.00.SqlDataProvider), so an alias that was a substring of another portal's
    // resolved to the wrong tenant - a multi-tenant isolation defect.

    /// <summary>Gets or sets the HTTP alias through which the new portal is addressed.</summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 12, <c>PortalAlias</c>, consumed at <c>PortalController.vb</c> to create the
    /// portal's first alias row. The backing column is <c>PortalAlias nvarchar(200) NOT NULL</c>.
    /// </para>
    /// <para>
    /// Callers must expect the stored alias to differ in case from the value they submit, and must
    /// not read partial, substring or pattern matching into it.
    /// </para>
    /// </remarks>
    public string? PortalAlias { get; set; }

    /// <summary>Gets or sets the portal description used for site metadata.</summary>
    /// <remarks>
    /// Legacy argument 7, a genuine <c>Portals</c> column assigned at <c>PortalController.vb</c>.
    /// Measured validation rules: maximum length 500, no validator declared on the legacy screen.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>Gets or sets the portal keywords used for site metadata.</summary>
    /// <remarks>
    /// Legacy argument 8, persisted alongside <see cref="Description"/> at
    /// <c>PortalController.vb</c>. Measured validation rules: maximum length 500, no validator
    /// declared.
    /// </remarks>
    public string? KeyWords { get; set; }

    /// <summary>
    /// Gets or sets the portal-relative home directory for uploaded content, or leaves it unset so
    /// that the service derives the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 11, whose backing column is <c>UploadDirectory nvarchar(100) NOT NULL</c> in
    /// the baseline schema, renamed later in the upgrade chain.
    /// </para>
    /// <para>
    /// An omitted value is MEANINGFUL, not merely missing. The legacy screen pre-filled the box
    /// with the literal placeholder <c>Portals/[PortalID]</c> and sent the empty string when the
    /// user left it untouched (<c>Signup.ascx.vb</c>); the service then substituted
    /// <c>"Portals/" + PortalID</c> (<c>PortalController.vb</c>), which cannot be evaluated before
    /// the portal has an identifier.
    /// </para>
    /// </remarks>
    public string? HomeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the name of the portal template that seeds the new portal's pages, modules and
    /// roles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 10. The code-behind appended the <c>.template</c> extension server-side
    /// before calling the controller (<c>Signup.ascx.vb</c>), so a caller submits the template's
    /// name and the service owns the extension.
    /// </para>
    /// <para>
    /// A file name only, never a path: the directory was legacy argument 9 and is deliberately
    /// absent, as the note above <see cref="IsChildPortal"/> records.
    /// </para>
    /// </remarks>
    public string? TemplateFile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the new portal is a child portal addressed beneath
    /// an existing portal's alias rather than a parent portal with an alias of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 15 and the only non-string argument in the signature, derived by the
    /// code-behind from a Parent/Child radio-button list (<c>Signup.ascx.vb</c>).
    /// </para>
    /// <para>
    /// Non-nullable, matching the legacy <c>Boolean</c>. The legacy null contract cannot express an
    /// unset boolean - its boolean sentinel is <c>False</c> and its absence test consequently
    /// reports <c>False</c> itself as absent - so a deliberate "parent portal" and an omitted
    /// choice were already indistinguishable.
    /// </para>
    /// </remarks>
    public bool IsChildPortal { get; set; }

    // MIGRATION: three of the fifteen legacy arguments are absent because the measured call site at
    // Website/admin/Portal/Signup.ascx.vb shows the code-behind COMPUTING them rather than reading them from
    // a control. Each is a server physical path, and each must be derived inside the portal service from the
    // hosting environment.

    // MIGRATION: legacy arguments were named FirstName, LastName, Username, Password and Email, and each is
    // renamed below with an Administrator prefix, because NONE of them is a Portals column. The legacy
    // parameter documentation describes all five as the "Portal Administrator's" (PortalController.vb) and
    // the body assigns them to a UserInfo instance and its Membership and Profile members before passing it
    // to UserController.CreateUser.

    /// <summary>
    /// Gets or sets the given name of the initial administrator user created together with the
    /// portal.
    /// </summary>
    /// <remarks>
    /// Legacy argument 2, assigned to the new user at <c>PortalController.vb</c> and to that user's
    /// profile. Measured validation rules: required, message "First Name Is Required."; maximum
    /// length 100.
    /// </remarks>
    public string? AdministratorFirstName { get; set; }

    /// <summary>
    /// Gets or sets the family name of the initial administrator user created together with the
    /// portal.
    /// </summary>
    /// <remarks>
    /// Legacy argument 3, assigned to the new user at <c>PortalController.vb</c> and to that user's
    /// profile. Measured validation rules: required, message "Last Name Is Required."; maximum
    /// length 100.
    /// </remarks>
    public string? AdministratorLastName { get; set; }

    /// <summary>
    /// Gets or sets the sign-in name of the initial administrator user created together with the
    /// portal.
    /// </summary>
    /// <remarks>
    /// Legacy argument 4, assigned to the new user at <c>PortalController.vb</c>. Measured
    /// validation rules: required, message "Username Is Required."; maximum length 100.
    /// </remarks>
    public string? AdministratorUsername { get; set; }

    // MIGRATION: the password store changed, and the change is deliberate rather than incidental. The legacy
    // membership provider stored passwords in a reversible format with retrieval enabled, and the symmetric
    // key that reversed them was itself committed to source control, so every historical password was
    // recoverable by anyone holding the repository; the baseline schema was worse still, holding the
    // password as a plain narrow string column directly on Users.
    //
    // The password POLICY, by contrast, is preserved verbatim rather than tightened: minimum length seven,
    // no non-alphanumeric character required, no question and answer, and email uniqueness not enforced.
    // Tightening it during a migration would deny existing users access to their own accounts, so any
    // hardening is a separate, explicit decision.

    /// <summary>
    /// Gets or sets the initial administrator's password. Write-only: accepted on create, never
    /// returned, never logged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 5, assigned to the new user's membership record at
    /// <c>PortalController.vb</c> and supplied by a password field of <c>maxlength</c> 20.
    /// </para>
    /// <para>
    /// Handling requirements. The value is hashed by
    /// <c>Infrastructure/Security/BcryptPasswordHasher.cs</c> before it is persisted and is
    /// retained in no other form.
    /// </para>
    /// </remarks>
    public string? AdministratorPassword { get; set; }

    // MIGRATION: the legacy screen's password CONFIRMATION box is absent by design - it was never one of the
    // fifteen arguments and was never persisted, existing only to compare two browser inputs ("The Password
    // Values Entered Do Not Match."). The equivalent check belongs to a cross-field validator on the client
    // form, where both values are already present; transmitting a password twice would widen its exposure
    // without adding any safety.

    /// <summary>
    /// Gets or sets the electronic mail address of the initial administrator user created together
    /// with the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 6, assigned to the new user at <c>PortalController.vb</c> and supplied by a
    /// text box of <c>maxlength</c> 100. The backing column is <c>Users.Email</c>, whose TERMINAL
    /// shape is <c>nvarchar(256) NULL</c>: the baseline declared it <c>nvarchar(100) NOT NULL</c>,
    /// a later script dropped the column outright, and <c>03.00.13.SqlDataProvider</c> re-added it
    /// as <c>nvarchar(256) NULL</c>, which every terminal procedure parameter matches.
    /// </para>
    /// <para>
    /// Measured validation rules: required, message "Email Is Required."; control maximum length
    /// 100; and, importantly, NO format pattern - the legacy screen declared no regular-expression
    /// validator on this field, and every validator on that screen is a required-field validator.
    /// The validator author must reproduce that rule set exactly and must not invent a format rule
    /// the legacy screen did not impose.
    /// </para>
    /// </remarks>
    public string? AdministratorEmail { get; set; }
}
